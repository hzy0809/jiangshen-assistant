using System;
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Threading;
using UnityEngine;
using D = JiangShen.AssistantRuntime.SharedDevelopmentContext;

namespace JiangShen.AssistantRuntime
{
    internal delegate D DailyBattleContextProvider();
    internal delegate bool DailyBattleAdmissionCheck();
    internal delegate bool DailyBattleAdmission(DailyBattleAdmissionCheck prepare, DailyBattleAdmissionCheck check);

    internal sealed class DailyTrialModel : DailyOrdinaryBattleModel
    {
        internal DailyTrialModel() : base(14, false) { }
    }

    // Fixed stock routes for the two ordinary NPC battle activities.
    // Training may finish the loaded report without waiting for its result button;
    // settlement still uses the game's normal battle-end request and callback.
    internal class DailyOrdinaryBattleModel
    {
        private sealed class Frame
        {
            internal D Context;
            internal object Role, Client, Loader, Sub, Trial, Campaign, Target;
            internal Component Root, Panel, TrialUi, Formation, Battle, NormalResult, Reward, Message, Consume;
            internal ulong RoleId;
            internal uint Now, Cycle, TargetCooldown, SelectedTargetCooldown, NextTrainingCooldown;
            internal int Current, Required, Stamina, TargetId, Progress, Gold, BoundGold;
            internal int TargetLevel, TargetDifficulty, RemainingMonsters, SummonTimes;
            internal int TrialLevel, TrialChests;
            internal uint BossCooldown;
            internal bool TrialObserved, BossKilled, BossRankAllowed;
            internal bool Loading, NetworkBusy, Scene, Home, TargetFinished, CanSummon, CanKillAll, TrainingUnlocked, DailyObserved;
            internal bool SelectedTargetObserved;
            internal bool HomeReady, PanelEnabled, MainScene;
            internal string Error = "", ReturnLevel = "", RecoveryBlocker = "";
        }

        private sealed class Intent
        {
            internal string Action, Id;
            internal int Tick, Sequence;
            internal Frame Frame;
        }

        private sealed class TrialStartAttempt
        {
            internal int Sequence, Frame;
            internal string OperationId, Mode, Outcome = "prepared", Failure = "", Boundary = "";
            internal string Before = "null", After = "null";
            internal bool CallEntered, CallReturned;
            internal bool? TicketCreated, FormationUpdateAttempted, BattleLaunchInvoked, BattleRequestObserved;

            private static string Flag(bool? value) { return value.HasValue ? D.B(value.Value) : "null"; }

            internal string Json()
            {
                return "{\"command_seq\":" + Sequence + ",\"frame\":" + Frame +
                    ",\"operation_id\":" + D.Q(OperationId) + ",\"mode\":" + D.Q(Mode) +
                    ",\"outcome\":" + D.Q(Outcome) + ",\"failure\":" + D.Q(Failure) +
                    ",\"failure_boundary\":" + D.Q(Boundary) +
                    ",\"formation_call_entered\":" + D.B(CallEntered) +
                    ",\"formation_call_returned\":" + D.B(CallReturned) +
                    ",\"ticket_created_at_capture\":" + Flag(TicketCreated) +
                    ",\"formation_update_attempted_at_capture\":" + Flag(FormationUpdateAttempted) +
                    ",\"battle_launch_invoked_at_capture\":" + Flag(BattleLaunchInvoked) +
                    ",\"battle_request_observed_at_capture\":" + Flag(BattleRequestObserved) +
                    ",\"before\":" + Before + ",\"after\":" + After + "}";
            }
        }

        private sealed class TrialBossSelection
        {
            internal readonly object Role, Client, Loader, Target;
            internal readonly Component Root, GlobalUi, UiRoot, Source, SourceRoot, Formation;
            internal readonly DailyTransport Transport;
            internal readonly ulong RoleId, BossId;
            internal readonly uint Cycle;
            internal readonly int Thread;
            internal TrialBossSelection(Frame f)
            {
                Role = f.Role; Client = f.Client; Loader = f.Loader; Target = f.Trial;
                RoleId = f.RoleId; Cycle = f.Cycle; Root = f.Root;
                GlobalUi = f.Context.GlobalUi; UiRoot = f.Context.UiRoot; Source = f.TrialUi;
                SourceRoot = Source == null ? null : D.Read(Source, "rmg") as Component;
                GameObject panel = GameObject.Find("/Trial/TrialUI");
                Formation = panel == null ? null : panel.GetComponent(Model.GameType("FormationUI"));
                Transport = DailyTransport.Read(Client); Thread = System.Threading.Thread.CurrentThread.ManagedThreadId;
                BossId = Target == null ? 0 : Convert.ToUInt64(D.Read(Target, "bossid"), CultureInfo.InvariantCulture);
            }
        }

        private readonly int taskId;
        private readonly bool training;
        private readonly bool allowTrainingBatchKill;
        private Frame frame;
        private Intent queued;
        private object owner, selected;
        private Component battleSource, battlePrevious;
        private object battleTarget;
        private string battleTargetId = "";
        private BattleMode battleMode;
        private ulong roleId;
        private uint cycle;
        private string operationId = "", nextAction = "", terminalReason = "";
        private int lastSequence, selectedId, stageTick;
        private bool stopped, holding, battleStarted, battleLost, returning, trainingRunComplete, trainingRunBlocked, killAllPending;
        private bool resumedExistingRun, entryRequestAttempted, battleRequestAttempted;
        private bool skipInvoked, battleEndRequested;
        private BattleLaunchAttempt batchLaunch;
        private BattleLaunchAttempt trialLaunch;
        private TrialStartAttempt lastTrialStart;
        private TrialBossSelection bossSelection;
        private bool recoveryOnly, recoveryComplete, returnInvoked;
        private bool trialRunComplete, trialRunBlocked, bossSelected;
        private int enteredTrialLevel;
        private uint trialCompletedCycle;
        private uint trainingCompletedCycle, trainingBattleCooldownBefore;
        private string signature;
        private int capturedFrame = -1;
        internal int Revision { get; private set; }
        internal string Status { get; private set; }
        internal string Reason { get; private set; }
        internal bool MutationAttempted { get; private set; }
        internal bool GuiInvocationAttempted { get; private set; }
        internal bool TaskConfirmed { get; private set; }
        internal bool CleanupPending { get { return holding; } }
        internal bool Busy { get { return queued != null || holding || !training && trialLaunch != null && trialLaunch.Unresolved; } }
        internal bool Unresolved { get { return holding || Status == "UNKNOWN"; } }
        private bool ReleaseVerified
        { get { return !training && !stopped && capturedFrame == Time.frameCount && frame != null && frame.Error == "" && frame.HomeReady &&
            !Busy && !CleanupPending && Status != "UNKNOWN" &&
            (operationId == "" && owner == null || trialRunComplete || trialRunBlocked); } }

        internal BattlePresentationOwnership OwnedBattlePresentation(Component battle)
        {
            if (stopped) return null;
            object role = owner, target = battleTarget;
            Component source = battleSource, previous = battlePrevious;
            string operation = operationId, targetId = battleTargetId;
            BattleMode mode = battleMode;
            TrialBossSelection boss = mode == BattleMode.TrialBoss ? bossSelection : null;
            ulong expectedRole = roleId;
            uint expectedCycle = cycle;
            BattleSettlementAdmission verify = delegate
            {
                try
                {
                    uint now, day, currentCycle;
                    if (!holding || Status == "UNKNOWN" || !battleStarted || operation == "" ||
                        operationId != operation || !ReferenceEquals(owner, role) || roleId != expectedRole || expectedRole == 0 ||
                        cycle != expectedCycle || !ReferenceEquals(battleTarget, target) || battleTargetId != targetId ||
                        battleMode != mode || !ReferenceEquals(battleSource, source) || !ReferenceEquals(battlePrevious, previous) ||
                        battle == null || source == null || previous == null || target == null || targetId == "" ||
                        !ReferenceEquals(D.Static(Model.FirstType("Global"), "character_"), role) ||
                        Convert.ToUInt64(D.Read(role, "id"), CultureInfo.InvariantCulture) != expectedRole ||
                        DailyRefreshGate.ReadClock(role, out now, out day, out currentCycle) != "" || currentCycle != expectedCycle ||
                        !ReferenceEquals(D.Read(battle, "m_parentUI"), source) ||
                        !ReferenceEquals(D.Read(battle, "m_prevUI"), previous)) return false;
                    if (mode == BattleMode.TrialBoss)
                        return !training && bossSelected && boss != null && ReferenceEquals(bossSelection, boss) &&
                            ReferenceEquals(boss.Role, role) && ReferenceEquals(boss.Target, target) &&
                            ReferenceEquals(boss.Source, source) && ReferenceEquals(boss.Root, previous) &&
                            ReferenceEquals(D.Static(Model.FirstType("BaseAsset"), "lds"), boss.Loader) &&
                            ReferenceEquals(D.Read(boss.Loader, "currmg"), previous) &&
                            boss.Transport != null && boss.Transport.StillCurrent() &&
                            BattleSettlement.TrialBossSourceIdentity(source, boss.SourceRoot) &&
                            ReferenceEquals(boss.SourceRoot, boss.UiRoot) && boss.GlobalUi != null &&
                            ReferenceEquals(source.gameObject.GetComponent(Model.GameType("RootUI")), boss.GlobalUi) &&
                            ReferenceEquals(D.Read(boss.GlobalUi, "level_rmg"), previous) &&
                            ReferenceEquals(D.Read(role, "trialinfo"), target) &&
                            Convert.ToUInt64(D.Read(target, "bossid"), CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) == targetId;
                    return (mode == BattleMode.Trial || mode == BattleMode.TrainingMonster) &&
                        ReferenceEquals(D.Read(source, "rmg"), previous) &&
                        ReferenceEquals(selected, target) && selectedId.ToString(CultureInfo.InvariantCulture) == targetId &&
                        D.Int(D.Read(target, "idtroop")) == selectedId && ReferenceEquals(D.Read(source, "m_troop"), target);
                }
                catch (Exception) { return false; }
            };
            return verify() ? new BattlePresentationOwnership(mode, operation, source, target, targetId, verify) : null;
        }

        internal DailyOrdinaryBattleModel(int activityId, bool trainingMonsters, bool allowFreeBatchKill = false)
        {
            taskId = activityId; training = trainingMonsters; allowTrainingBatchKill = allowFreeBatchKill;
            Status = "IDLE"; Reason = "not_observed"; Revision = 1;
        }

        internal void Capture(D context)
        {
            frame = Read(context);
            capturedFrame = Time.frameCount;
            nextAction = "";
            if (stopped) { Publish("BLOCKED", "stopped"); return; }
            if (frame.Error != "")
            {
                Publish(holding ? "PENDING" : "BLOCKED", frame.Error);
                return;
            }
            if (holding && (!ReferenceEquals(owner, frame.Role) || roleId != frame.RoleId || cycle != frame.Cycle))
            { Publish("UNKNOWN", "role_or_daily_cycle_changed"); return; }
            if (!training && Status == "UNKNOWN") return;
            TaskConfirmed = frame.DailyObserved && frame.Current >= frame.Required;
            if (trainingRunComplete && trainingCompletedCycle != frame.Cycle) trainingRunComplete = false;
            if (!training && trialCompletedCycle != frame.Cycle) trialRunComplete = trialRunBlocked = false;
            if (training && !holding && frame.Scene)
            {
                if (frame.RecoveryBlocker == "") Offer("READY", "resume_close");
                else Publish("BLOCKED", frame.RecoveryBlocker);
                return;
            }
            if (training && recoveryOnly)
            {
                if (Status == "UNKNOWN") return;
                if (!recoveryComplete && returnInvoked && frame.HomeReady)
                { holding = false; recoveryComplete = true; }
                if (recoveryComplete)
                {
                    if (frame.HomeReady) nextAction = "open";
                    Publish("CLOSED", "training_recovered_to_main");
                }
                else Publish("PENDING", "training_return_pending");
                return;
            }
            if (holding && frame.Home && returning && frame.HomeReady &&
                (training || trialLaunch == null || !trialLaunch.Unresolved))
            {
                holding = false;
                if (training)
                {
                    trainingRunComplete = terminalReason == "training_free_summons_exhausted";
                    trainingRunBlocked = !trainingRunComplete;
                    trainingCompletedCycle = frame.Cycle;
                    if (trainingRunBlocked && frame.TrainingUnlocked) nextAction = "open";
                    Publish(trainingRunComplete ? "COMPLETED" : "BLOCKED", terminalReason);
                }
                else FinishTrial();
                return;
            }
            if (!holding)
            {
                if (training && trainingRunComplete) Publish("COMPLETED", "training_free_summons_exhausted");
                else if (training && trainingRunBlocked)
                {
                    // Retain this operation's terminal evidence across automatic
                    // captures. Only a fresh operation may use the offered open.
                    if (frame.Home && frame.TrainingUnlocked) nextAction = "open";
                    Publish("BLOCKED", terminalReason);
                }
                else if (training && !frame.TrainingUnlocked) Publish("BLOCKED", "training_feature_locked");
                else if (!training && trialRunComplete) Publish("COMPLETED", "trial_cleared");
                else if (!training && trialRunBlocked)
                {
                    if (frame.Home) nextAction = "open";
                    Publish("BLOCKED", terminalReason);
                }
                else if (!training && CanResume(frame)) Offer("READY", "resume");
                else if (frame.Home) Offer("IDLE", "open");
                else Publish("BLOCKED", "main_screen_required");
                return;
            }
            if (frame.Consume != null && D.Visible(frame.Consume))
            { Publish("BLOCKED", "premium_confirmation_visible"); return; }
            if (frame.Loading || frame.NetworkBusy)
            { Publish("PENDING", frame.Loading ? "stock_loading" : "stock_reply_pending"); return; }
            if (frame.Message != null && D.Visible(frame.Message))
            { Offer("MESSAGE", "dismiss"); return; }
            if (frame.Reward != null && D.Visible(frame.Reward))
            { Offer("REWARD", "reward_close"); return; }
            if (training && killAllPending)
            {
                if (batchLaunch == null || batchLaunch.Error != "")
                { Publish("UNKNOWN", batchLaunch == null ? "training_batch_launch_proof_missing" : batchLaunch.Error); return; }
                if (!batchLaunch.NonReportReturned)
                { Publish("PENDING", "training_batch_native_reply_pending"); return; }
                killAllPending = false;
                if (frame.RemainingMonsters != 0)
                { Publish("BLOCKED", "training_batch_no_result"); return; }
            }
            if (frame.NormalResult != null && D.Visible(frame.NormalResult))
            {
                if (!training && battleStarted && !(bossSelected ? frame.BossKilled : frame.TargetFinished)) battleLost = true;
                Offer("RESULT", "battle_exit");
                return;
            }
            if (frame.Battle != null && D.Visible(frame.Battle))
            {
                if (training && BattleSettlement.WasPresented(frame.Battle))
                { skipInvoked = true; battleEndRequested = BattleSettlement.RequestCount(frame.Battle) > 0; }
                if (training && CanSkipTrainingBattle(frame)) Offer("BATTLE", "skip");
                else Publish("PENDING", training && (bool)D.Read(frame.Battle, "bBattle_started") &&
                    !(bool)D.Read(frame.Battle, "bBattle_fighting") && (bool)D.Read(frame.Battle, "bUseBattleEnd") ?
                    "stock_battle_settlement_pending" : "stock_battle_playing");
                return;
            }
            if (frame.Formation != null && ReferenceEquals(frame.Sub, frame.Formation) && D.Enabled(frame.Formation))
            {
                if (D.Loading(frame.Formation) || D.Read(frame.Formation, "CharFormation") == null ||
                    D.Int(D.Read(frame.Formation, "waitstate")) != 0)
                { Publish("PENDING", "formation_loading"); return; }
                if (!training && (TrialCleared(frame) || battleLost)) Offer("FORMATION", "close");
                else if (!training && battleStarted)
                { terminalReason = "trial_battle_no_result"; Offer("FORMATION", "close"); }
                else if (!training && bossSelected && !frame.BossRankAllowed)
                { terminalReason = "trial_boss_rank_limited"; Offer("FORMATION", "close"); }
                else if (!training && bossSelected && frame.BossCooldown > frame.Now)
                { terminalReason = "trial_boss_cooldown"; Offer("FORMATION", "close"); }
                else if (frame.Stamina < (training || bossSelected ? 0 : 1))
                { terminalReason = "insufficient_stamina"; Offer("FORMATION", "close"); }
                else Offer("FORMATION", "start");
                return;
            }
            if (!training && battleStarted && frame.Sub != null && frame.Sub.GetType() == Model.GameType("BattleValueUI"))
                battleLost = true;
            if (!training && (TrialCleared(frame) || battleLost))
            {
                terminalReason = TrialCleared(frame) ? "trial_cleared" : bossSelected ? "trial_boss_lost" : "battle_lost";
                if (frame.Scene || frame.Sub != null) Offer("CLOSING", "close");
                else if (frame.HomeReady && (trialLaunch == null || !trialLaunch.Unresolved))
                { holding = false; FinishTrial(); }
                return;
            }
            if (!training && frame.Sub != null && ReferenceEquals(frame.Sub, frame.TrialUi) && D.Enabled(frame.TrialUi))
            {
                if (!frame.TrialObserved || frame.TrialLevel < 1 || frame.TrialLevel > 6)
                { terminalReason = "trial_state_unavailable"; Offer("CLOSING", "close"); }
                else if (frame.TrialChests < frame.TrialLevel - 1)
                {
                    // load_all and each chapter's BonusClose request this chest
                    // themselves. Do not duplicate that mutation if no receipt arrives.
                    if (unchecked((uint)(Environment.TickCount - stageTick)) > 45000)
                    { terminalReason = "trial_chest_not_confirmed"; Offer("CLOSING", "close"); }
                    else Publish("PENDING", "trial_chest_pending");
                }
                else if (frame.TrialLevel <= 5)
                {
                    if (frame.Stamina < 1)
                    { terminalReason = "insufficient_stamina"; Offer("CLOSING", "close"); }
                    else if (entryRequestAttempted && enteredTrialLevel == frame.TrialLevel)
                    { terminalReason = "trial_entry_no_result"; Offer("CLOSING", "close"); }
                    else Offer("OPEN", "enter");
                }
                else if (frame.BossCooldown > frame.Now)
                { terminalReason = "trial_boss_cooldown"; Offer("CLOSING", "close"); }
                else if (!frame.BossRankAllowed)
                { terminalReason = "trial_boss_rank_limited"; Offer("CLOSING", "close"); }
                else if (bossSelected)
                { terminalReason = "trial_boss_selection_no_result"; Offer("CLOSING", "close"); }
                else Offer("OPEN", "boss_select");
                return;
            }
            if (frame.Scene && frame.Panel != null && D.Enabled(frame.Panel))
            {
                // A failed target can enter cooldown while other targets are
                // still available. Continue those before closing the scene.
                if (training && battleStarted && frame.SelectedTargetObserved && !frame.TargetFinished &&
                    frame.SelectedTargetCooldown > trainingBattleCooldownBefore && frame.Target == null)
                { terminalReason = "training_battle_no_progress_cooldown"; Offer("CLOSING", "close"); }
                else if (training && frame.RemainingMonsters > 0 && allowTrainingBatchKill && frame.CanKillAll)
                    Offer("READY", "kill_all");
                else if (training && frame.Target == null && frame.RemainingMonsters > 0 && frame.TargetCooldown > frame.Now)
                { terminalReason = "training_target_cooldown"; Offer("CLOSING", "close"); }
                else if (frame.Target != null)
                {
                    if (!training && battleStarted && !frame.TargetFinished)
                    { terminalReason = "trial_battle_no_progress"; Offer("CLOSING", "close"); }
                    else if (!training && ReferenceEquals(selected, frame.Target) && !battleStarted)
                    { terminalReason = "trial_selection_no_result"; Offer("CLOSING", "close"); }
                    else if (!training && frame.Stamina < 1)
                    { terminalReason = "insufficient_stamina"; Offer("CLOSING", "close"); }
                    else Offer("READY", "select");
                }
                else if (training && frame.CanSummon) Offer("READY", "summon");
                else
                {
                    terminalReason = training ? frame.RemainingMonsters == 0 ?
                        "training_free_summons_exhausted" : "training_target_unavailable" : "trial_targets_unavailable";
                    Offer("CLOSING", "close");
                }
                return;
            }
            if (unchecked((uint)(Environment.TickCount - stageTick)) > 45000)
                Publish("UNKNOWN", "stock_transition_timeout");
            else Publish("PENDING", "stock_transition");
        }

        private static bool TrialCleared(Frame f)
        { return f.TrialObserved && f.TrialLevel == 6 && f.TrialChests >= 5 && f.BossKilled; }

        private void FinishTrial()
        {
            trialRunComplete = TrialCleared(frame);
            trialRunBlocked = !trialRunComplete;
            trialCompletedCycle = frame.Cycle;
            if (trialRunComplete) terminalReason = "trial_cleared";
            else if (terminalReason == "") terminalReason = "trial_not_cleared";
            Publish(trialRunComplete ? "COMPLETED" : "BLOCKED", terminalReason);
        }

        private void Offer(string status, string action)
        { nextAction = action; Publish(status, ""); }

        private void Publish(string status, string reason)
        {
            Status = status; Reason = reason;
            string current = JsonBody();
            if (current != signature) { signature = current; Revision++; }
        }

        internal bool Queue(int activityId, ulong characterId, string id, int sequence, int expectedRevision, string action, out string why)
        {
            why = "daily_battle_command_unavailable";
            if (stopped || queued != null || frame == null || frame.Error != "" || activityId != taskId ||
                characterId == 0 || characterId != frame.RoleId || sequence <= lastSequence || expectedRevision != Revision ||
                action != nextAction || action == "" || !Command.ValidNonce(id)) return false;
            bool entry = action == "open" || action == "resume" || action == "resume_close";
            if (!entry && (!holding || operationId != id)) return false;
            if (entry && Busy) return false;
            if (training && (trainingRunBlocked || recoveryComplete) && entry && operationId == id)
            { why = "training_operation_already_finished"; return false; }
            if (!training && (trialRunComplete || trialRunBlocked) && entry && operationId == id)
            { why = "trial_operation_already_finished"; return false; }
            queued = new Intent { Action = action, Id = id, Tick = Environment.TickCount, Sequence = sequence, Frame = frame };
            lastSequence = sequence; GuiInvocationAttempted = false; Revision++;
            why = "";
            return true;
        }

        internal bool ExecuteGui(DailyBattleContextProvider fresh, DevelopmentPublication publish, DailyBattleAdmission admit, out string why)
        {
            why = "daily_battle_not_queued";
            Intent intent = queued;
            if (intent == null) return false;
            bool committed = false;
            Frame current = null;
            DevelopmentPublication stock = null;
            object client = intent.Frame.Client;
            if (client == null || !Monitor.TryEnter(client, 0))
            { NotInvoked("client_busy"); why = "client_busy"; return false; }
            try
            {
                publish();
                if (!admit(delegate
                {
                    if (stopped || queued != intent || !D.Gui || unchecked((uint)(Environment.TickCount - intent.Tick)) > 1500) return false;
                    Capture(fresh()); current = frame;
                    if (current.Error != "" || current.RoleId != intent.Frame.RoleId || nextAction != intent.Action) return false;
                    stock = Bind(current, intent.Action);
                    return stock != null;
                }, delegate
                {
                    committed = stock != null && !stopped && queued == intent && D.Gui &&
                        ReferenceEquals(D.Static(Model.FirstType("Global"), "character_"), current.Role) &&
                        ReferenceEquals(D.Static(Model.GameType("RootUI"), "m_client"), current.Client);
                    return committed;
                }) || !committed)
                { why = "daily_battle_gui_admission_changed"; NotInvoked(why); return false; }

                queued = null; stageTick = Environment.TickCount;
                if (intent.Action == "open" || intent.Action == "resume" || intent.Action == "resume_close")
                {
                    operationId = intent.Id; owner = current.Role; roleId = current.RoleId; cycle = current.Cycle;
                    holding = true; battleLost = battleStarted = returning = MutationAttempted = trainingRunComplete = trainingRunBlocked = killAllPending = false; terminalReason = "";
                    trialRunComplete = trialRunBlocked = bossSelected = false;
                    bossSelection = null;
                    if (!training) trialLaunch = null;
                    selected = null; selectedId = 0;
                    battleSource = battlePrevious = null; battleTarget = null; battleTargetId = "";
                    trainingBattleCooldownBefore = 0;
                    resumedExistingRun = intent.Action == "resume";
                    entryRequestAttempted = battleRequestAttempted = false;
                    enteredTrialLevel = 0;
                    skipInvoked = battleEndRequested = false;
                    recoveryOnly = intent.Action == "resume_close";
                    recoveryComplete = returnInvoked = false;
                }
                if (intent.Action == "select")
                {
                    selected = current.Target; selectedId = current.TargetId; battleStarted = false;
                    battleSource = current.Panel; battlePrevious = D.Read(battleSource, "rmg") as Component;
                    battleTarget = selected; battleTargetId = selectedId.ToString(CultureInfo.InvariantCulture);
                    battleMode = training ? BattleMode.TrainingMonster : BattleMode.Trial;
                }
                if (intent.Action == "boss_select")
                {
                    selected = null; selectedId = 0; bossSelected = true; battleStarted = false;
                    // TrialUI is global; stock TrialBoss pins GetCurUI() as the
                    // previous scene, independently of TrialUI's /Root messenger.
                    battleSource = current.TrialUi; battlePrevious = current.Root;
                    battleTarget = current.Trial;
                    battleTargetId = Convert.ToUInt64(D.Read(battleTarget, "bossid"), CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
                    battleMode = BattleMode.TrialBoss;
                }
                if (intent.Action == "start")
                {
                    if (training) trainingBattleCooldownBefore = current.SelectedTargetCooldown;
                    MutationAttempted = battleStarted = battleRequestAttempted = true;
                }
                if (intent.Action == "enter")
                {
                    entryRequestAttempted = true; enteredTrialLevel = current.TrialLevel;
                    selected = null; selectedId = 0; battleStarted = bossSelected = false;
                    bossSelection = null;
                    battleSource = battlePrevious = null; battleTarget = null; battleTargetId = "";
                }
                if (intent.Action == "summon") MutationAttempted = true;
                if (intent.Action == "kill_all") MutationAttempted = killAllPending = true;
                GuiInvocationAttempted = intent.Action != "resume";
                stock();
                if (intent.Action == "resume_close") returnInvoked = true;
                if (intent.Action == "close" || intent.Action == "resume_close") { returning = true; Status = "CLOSING"; }
                else Status = "PENDING";
                Reason = intent.Action == "resume" ? "existing_trial_resumed" : "stock_action_invoked"; nextAction = ""; Revision++;
                why = "";
                return true;
            }
            catch (Exception error)
            {
                queued = null;
                Publish(GuiInvocationAttempted ? "UNKNOWN" : "BLOCKED", error is TargetInvocationException && error.InnerException != null ?
                    error.InnerException.Message : error.Message);
                why = Reason;
                return false;
            }
            finally { Monitor.Exit(client); }
        }

        private DevelopmentPublication Bind(Frame f, string action)
        {
            // Adopt the loaded campaign without repeating the 817 entry request
            // or RootUI.startTrial, which destroys the current scene's meshes.
            if (action == "resume") return !training && CanResume(f) ? (DevelopmentPublication)delegate { } : null;
            if (action == "resume_close") return training && f.Scene && f.RecoveryBlocker == "" ?
                (DevelopmentPublication)delegate { D.Call(f.Panel, "_return"); } : null;
            if (action == "open") return delegate { D.MainBranch(training ? "_seal_monster" : "_training"); };
            if (action == "enter") return delegate { D.Call(f.TrialUi, "cs_trial_load_campaign"); };
            if (action == "boss_select")
            {
                if (training || f.TrialUi == null || f.TrialLevel != 6 || f.BossKilled ||
                    f.BossCooldown > f.Now || !f.BossRankAllowed) return null;
                return delegate
                {
                    TrialBossSelection selection = new TrialBossSelection(f);
                    bossSelection = selection;
                    TrialBossIdentity(selection, false, null, null);
                    D.Call(selection.Source, "callBoss");
                };
            }
            if (action == "summon")
            {
                if (!f.CanSummon) return null;
                return delegate { D.Call(f.Panel, "cs_sealmonster_refresh"); };
            }
            if (action == "kill_all")
            {
                if (!training || !allowTrainingBatchKill || !f.CanKillAll || f.RemainingMonsters == 0) return null;
                return delegate
                {
                    batchLaunch = BattleLaunch.NonReport(BattleMode.TrainingMonster, f.Panel, f.Campaign, delegate
                    { return !stopped && holding && ReferenceEquals(owner, f.Role); },
                        delegate { D.Call(f.Panel, "cs_sealmonster_killall"); }, 0x370);
                };
            }
            if (action == "skip")
            {
                if (!training || !CanSkipTrainingBattle(f)) return null;
                return delegate
                {
                    if (!BattleSettlement.PresentOwned(f.Battle, OwnedBattlePresentation(f.Battle))) throw new ProbeFailure("training_battle_settlement_owner_unavailable");
                    skipInvoked = true;
                    battleEndRequested = BattleSettlement.RequestCount(f.Battle) > 0;
                };
            }
            if (action == "select")
            {
                MethodInfo select = D.Method(f.Panel.GetType(), "wantCombat", new[] { f.Target.GetType() }, false, typeof(void));
                return delegate { select.Invoke(f.Panel, new[] { f.Target }); };
            }
            if (action == "start")
            {
                if (f.Formation == null) return null;
                if (bossSelected)
                {
                    if (training || f.TrialLevel != 6 || f.BossKilled || f.BossCooldown > f.Now ||
                        !f.BossRankAllowed || !ReferenceEquals(f.Sub, f.Formation)) return null;
                }
                else if (selected == null || !ReferenceEquals(D.Read(f.Panel, "m_troop"), selected)) return null;
                TrialBossSelection boss = bossSelected ? bossSelection : null;
                if (bossSelected && boss == null) throw new ProbeFailure("trial_boss_identity_selection_missing");
                // callBoss hides its TrialUI. Never rediscover that source by
                // choosing an active UI candidate after the scene transition.
                Component launchSource = bossSelected ? boss.Source : f.Panel;
                object launchTarget = bossSelected ? boss.Target : selected;
                Component launchFormation = bossSelected ? boss.Formation : f.Formation;
                object bossParameters = bossSelected ? D.Read(launchFormation, "m_lastparam") : null;
                Delegate bossAttack = bossSelected ? D.Read(launchFormation, "OnAttack") as Delegate : null;
                if (bossSelected && !ReferenceEquals(f.Formation, launchFormation))
                    throw new ProbeFailure("trial_boss_identity_formation_changed");
                BattleMode launchMode = training ? BattleMode.TrainingMonster : bossSelected ? BattleMode.TrialBoss : BattleMode.Trial;
                int startSequence = queued == null ? 0 : queued.Sequence;
                return delegate
                {
                    // Keep this call distinct from an earlier successful launch.
                    // These observations never authorize cleanup or another start.
                    TrialStartAttempt diagnostic = training ? null : new TrialStartAttempt {
                        Sequence = startSequence, Frame = Time.frameCount, OperationId = operationId,
                        Mode = launchMode.ToString(), Before = TrialStartState(f, launchFormation, launchSource, launchTarget)
                    };
                    if (diagnostic != null) { lastTrialStart = diagnostic; diagnostic.CallEntered = true; }
                    try
                    {
                        BattleLaunchAttempt launch = BattleLaunch.Formation(launchMode, launchFormation, launchSource, launchTarget, delegate
                        {
                            if (launchMode == BattleMode.TrialBoss)
                                return TrialBossIdentity(boss, true, bossParameters, bossAttack);
                            return !stopped && holding && ReferenceEquals(owner, f.Role) && ReferenceEquals(battleTarget, launchTarget) &&
                                ReferenceEquals(battleSource, launchSource) &&
                                ReferenceEquals(D.Read(launchSource, "m_troop"), launchTarget);
                        }, null);
                        if (!training) trialLaunch = launch;
                        if (diagnostic != null)
                        {
                            diagnostic.CallReturned = true; diagnostic.Outcome = "returned";
                            diagnostic.TicketCreated = launch.DurableTicket != null;
                            diagnostic.FormationUpdateAttempted = launch.FormationUpdateAttempted;
                            diagnostic.BattleLaunchInvoked = launch.BattleLaunchInvoked;
                            diagnostic.BattleRequestObserved = launch.BattleRequestObserved;
                        }
                    }
                    catch (Exception error)
                    {
                        if (diagnostic != null)
                        {
                            Exception cause = error is TargetInvocationException && error.InnerException != null ? error.InnerException : error;
                            diagnostic.Outcome = "threw"; diagnostic.Failure = cause.Message;
                            // This unique constructor throw precedes Reserve and
                            // StartFormation. Other failures retain null witnesses.
                            if (cause.Message == "battle_launch_durable_identity_unverified" ||
                                cause.Message.StartsWith("battle_launch_durable_identity_unverified:", StringComparison.Ordinal))
                            {
                                diagnostic.Boundary = "constructor_identity_before_reserve";
                                diagnostic.TicketCreated = diagnostic.FormationUpdateAttempted =
                                    diagnostic.BattleLaunchInvoked = diagnostic.BattleRequestObserved = false;
                            }
                        }
                        throw;
                    }
                    finally
                    {
                        if (diagnostic != null) diagnostic.After = TrialStartState(f, launchFormation, launchSource, launchTarget);
                    }
                };
            }
            if (action == "battle_exit")
                return delegate { D.Call(f.NormalResult, "Hide"); D.Call(f.Battle, "battle_exit"); };
            if (action == "reward_close")
            {
                Delegate close = D.Read(f.Reward, "OnClose") as Delegate;
                return delegate { D.Call(f.Reward, "Hide"); if (close != null) close.DynamicInvoke(); };
            }
            if (action == "dismiss")
            {
                MethodInfo cancel = D.Method(f.Message.GetType(), "ButtonOK", new[] { typeof(bool) }, false, typeof(void));
                return delegate { cancel.Invoke(f.Message, new object[] { false }); };
            }
            if (action == "close")
            {
                if (training && (!f.Scene || f.RecoveryBlocker != "")) return null;
                MethodInfo clear = D.Method(Model.GameType("MainUI"), "SetSubUI", new[] { Model.FirstType("__BaseUI") }, true, typeof(void));
                return delegate
                {
                    if (f.Sub != null) clear.Invoke(null, new object[] { null });
                    if (f.Scene) D.Call(f.Panel, "_return");
                };
            }
            return null;
        }

        private delegate string DiagnosticRead();

        private static string DiagnosticPart(DiagnosticRead read)
        {
            try { return read(); }
            catch (Exception error) { return "{\"capture_error\":" + D.Q(error.GetType().Name) + "}"; }
        }

        private static string CallbackDiagnostic(Delegate callback)
        {
            if (callback == null) return "null";
            string value = "[";
            foreach (Delegate part in callback.GetInvocationList())
            {
                if (value != "[") value += ",";
                value += "{\"method\":" + D.Q((part.Method.DeclaringType == null ? "" : part.Method.DeclaringType.FullName + ".") + part.Method.Name) +
                    ",\"target_type\":" + D.Q(part.Target == null ? "" : part.Target.GetType().FullName) + "}";
            }
            return value + "]";
        }

        private string TrialStartState(Frame f, Component formation, Component source, object target)
        {
            return DiagnosticPart(delegate
            {
                return "{\"frame\":" + Time.frameCount +
                    ",\"source_instance_id\":" + (source == null ? 0 : source.GetInstanceID()) +
                    ",\"formation_instance_id\":" + (formation == null ? 0 : formation.GetInstanceID()) +
                    ",\"root_instance_id\":" + (f.Root == null ? 0 : f.Root.GetInstanceID()) +
                    ",\"operation_binding_retained\":" + D.B(holding && operationId != "" && ReferenceEquals(owner, f.Role) &&
                        ReferenceEquals(battleSource, source) && ReferenceEquals(battleTarget, target) &&
                        ReferenceEquals(D.Static(Model.FirstType("Global"), "character_"), f.Role)) +
                    ",\"published_launch_at_capture\":" + BattleLaunch.Json() +
                    ",\"formation\":" + DiagnosticPart(delegate
                    {
                        if (formation == null) return "null";
                        Delegate attack = D.Read(formation, "OnAttack") as Delegate;
                        Delegate close = D.Read(formation, "OnClose") as Delegate;
                        IDictionary parameters = D.Read(formation, "m_lastparam") as IDictionary;
                        return "{\"onfight\":" + D.B((bool)D.Read(formation, "onfight")) +
                            ",\"waitstate\":" + D.Int(D.Read(formation, "waitstate")) +
                            ",\"right_panel\":" + D.Q(D.Read(formation, "RightPanel") as string) +
                            ",\"default_state\":" + D.Q(D.Read(formation, "DefaultState") as string) +
                            ",\"substate\":" + D.Q(D.Read(formation, "substate") as string) +
                            ",\"enabled\":" + D.B(D.Enabled(formation)) + ",\"loading\":" + D.B(D.Loading(formation)) +
                            ",\"is_current_subui\":" + D.B(ReferenceEquals(D.Static(Model.GameType("MainUI"), "m_subui"), formation)) +
                            ",\"source_is_boss_selection\":" + D.B(bossSelection != null && ReferenceEquals(bossSelection.Source, source) &&
                                ReferenceEquals(bossSelection.Formation, formation) && ReferenceEquals(bossSelection.Target, target)) +
                            ",\"on_attack\":" + CallbackDiagnostic(attack) + ",\"on_close\":" + CallbackDiagnostic(close) +
                            ",\"attack_targets_source\":" + D.B(attack != null && attack.GetInvocationList().Length == 1 && ReferenceEquals(attack.Target, source)) +
                            ",\"parameter_attack_matches\":" + D.B(parameters != null && ReferenceEquals(parameters["OnAttack"], attack)) + "}";
                    }) +
                    ",\"transport\":" + DiagnosticPart(delegate
                    {
                        IDictionary handlers = D.Read(f.Client, "m_handlers") as IDictionary;
                        IList messages = D.Read(f.Client, "messages") as IList, sends = D.Read(f.Client, "m_sendDatas") as IList;
                        string active = "[";
                        if (handlers != null)
                            foreach (DictionaryEntry entry in handlers)
                                if ((float)D.Read(entry.Value, "life") > 0f)
                                { if (active != "[") active += ","; active += Convert.ToUInt16(entry.Key, CultureInfo.InvariantCulture); }
                        return "{\"active_handler_opcodes\":" + (handlers == null ? "null" : active + "]") +
                            ",\"queued_messages\":" + (messages == null ? "null" : messages.Count.ToString(CultureInfo.InvariantCulture)) +
                            ",\"queued_sends\":" + (sends == null ? "null" : sends.Count.ToString(CultureInfo.InvariantCulture)) +
                            ",\"sending\":" + D.B(D.Bool(D.Read(f.Client, "m_bSending"))) +
                            ",\"callback_executing\":" + D.B(DevelopmentCallbackCapture.AnyCallbackExecuting) +
                            ",\"same_client\":" + D.B(ReferenceEquals(f.Client, DailyTransport.CurrentClient(f.Role))) + "}";
                    }) +
                    ",\"presentation\":" + DiagnosticPart(delegate
                    {
                        object interpreter = f.Battle == null ? null : D.Read(f.Battle, "interpreter");
                        object report = interpreter == null ? null : D.Read(interpreter, "m_report");
                        return "{\"battle_visible\":" + D.B(Visible(f.Battle)) +
                            ",\"result_visible\":" + D.B(Visible(f.NormalResult)) + ",\"reward_visible\":" + D.B(Visible(f.Reward)) +
                            ",\"message_visible\":" + D.B(Visible(f.Message)) + ",\"consume_visible\":" + D.B(Visible(f.Consume)) +
                            ",\"report_present\":" + D.B(report != null) +
                            ",\"owned_settlement_pending\":" + D.B(report != null && BattleSettlement.OwnsPending(f.Client, f.Battle, report)) +
                            ",\"loader_battling\":" + D.B((bool)D.Read(f.Loader, "isBattling")) +
                            ",\"root_loading\":" + D.B((bool)D.Read(f.Root, "bLoading")) +
                            ",\"root_expecting\":" + D.Int(D.Read(f.Root, "ExpectingCount")) + "}";
                    }) + "}";
            });
        }

        private bool TrialBossIdentity(TrialBossSelection selection, bool formationReady, object parameters, Delegate attack)
        {
            if (selection == null || !ReferenceEquals(bossSelection, selection))
                throw new ProbeFailure("trial_boss_identity_selection_changed");
            if (training || stopped || !holding || !bossSelected || battleMode != BattleMode.TrialBoss ||
                !ReferenceEquals(owner, selection.Role) || roleId != selection.RoleId || cycle != selection.Cycle)
                throw new ProbeFailure("trial_boss_identity_owner_changed");
            if (Thread.CurrentThread.ManagedThreadId != selection.Thread)
                throw new ProbeFailure("trial_boss_identity_thread_changed");
            if (selection.Role == null || selection.RoleId == 0 ||
                !ReferenceEquals(D.Static(Model.FirstType("Global"), "character_"), selection.Role) ||
                Convert.ToUInt64(D.Read(selection.Role, "id"), CultureInfo.InvariantCulture) != selection.RoleId)
                throw new ProbeFailure("trial_boss_identity_role_changed");
            uint now, day, currentCycle;
            if (DailyRefreshGate.ReadClock(selection.Role, out now, out day, out currentCycle) != "" || currentCycle != selection.Cycle)
                throw new ProbeFailure("trial_boss_identity_cycle_changed");
            if (selection.Client == null || !ReferenceEquals(DailyTransport.CurrentClient(selection.Role), selection.Client) ||
                selection.Transport == null || !selection.Transport.StillCurrent())
                throw new ProbeFailure("trial_boss_identity_transport_changed");
            if (selection.Loader == null || !ReferenceEquals(D.Static(Model.FirstType("BaseAsset"), "lds"), selection.Loader))
                throw new ProbeFailure("trial_boss_identity_loader_changed");
            if (selection.Root == null || selection.GlobalUi == null ||
                !ReferenceEquals(D.Read(selection.Loader, "currmg"), selection.Root) ||
                !ReferenceEquals(D.Read(selection.GlobalUi, "level_rmg"), selection.Root))
                throw new ProbeFailure("trial_boss_identity_root_changed");
            if (selection.Source == null || selection.Source.GetType() != Model.GameType("TrialUI") ||
                !ReferenceEquals(battleSource, selection.Source) || !ReferenceEquals(battlePrevious, selection.Root) ||
                !BattleSettlement.TrialBossSourceIdentity(selection.Source, selection.SourceRoot) ||
                selection.SourceRoot == null || !ReferenceEquals(selection.SourceRoot, selection.UiRoot) ||
                !ReferenceEquals(D.Read(selection.Source, "rmg"), selection.SourceRoot) ||
                !ReferenceEquals(D.Read(selection.GlobalUi, "rmg"), selection.SourceRoot) ||
                selection.Source.transform.root != selection.SourceRoot.transform ||
                selection.Source.gameObject != selection.GlobalUi.gameObject ||
                !ReferenceEquals(selection.Source.gameObject.GetComponent(Model.GameType("RootUI")), selection.GlobalUi))
                throw new ProbeFailure("trial_boss_identity_source_changed");
            if (selection.Target == null || selection.BossId == 0 || !ReferenceEquals(battleTarget, selection.Target) ||
                !ReferenceEquals(D.Read(selection.Role, "trialinfo"), selection.Target) ||
                Convert.ToUInt64(D.Read(selection.Target, "bossid"), CultureInfo.InvariantCulture) != selection.BossId ||
                battleTargetId != selection.BossId.ToString(CultureInfo.InvariantCulture))
                throw new ProbeFailure("trial_boss_identity_target_changed");
            if (D.Int(D.Read(selection.Target, "cur_level")) != 6 || D.Int(D.Read(selection.Target, "bosskilled")) != 0 ||
                (uint)D.Read(selection.Target, "bosscd") > now ||
                D.Int(D.Read(selection.Target, "trial_rank")) > D.Int(D.Read(selection.Role, "level")) + 5)
                throw new ProbeFailure("trial_boss_identity_eligibility_changed");
            GameObject panel = GameObject.Find("/Trial/TrialUI");
            Component formation = selection.Formation;
            if (formation == null || panel == null || formation.gameObject != panel ||
                !ReferenceEquals(panel.GetComponent(Model.GameType("FormationUI")), formation))
                throw new ProbeFailure("trial_boss_identity_formation_changed");
            if (!formationReady)
            {
                if (!ReferenceEquals(D.Static(Model.GameType("MainUI"), "m_subui"), selection.Source))
                    throw new ProbeFailure("trial_boss_identity_selection_ui_changed");
                return true;
            }
            if (!ReferenceEquals(D.Static(Model.GameType("MainUI"), "m_subui"), formation) ||
                !ReferenceEquals(D.Read(formation, "m_lastparam"), parameters))
                throw new ProbeFailure("trial_boss_identity_formation_parameters_changed");
            IDictionary args = parameters as IDictionary;
            if (args == null || args["subui"] as string != "NPCBattle" || args["substate"] as string != "trial_boss" ||
                D.Read(formation, "DefaultState") as string != "NPCBattle" || D.Read(formation, "substate") as string != "trial_boss" ||
                args["campaignid"] == null || Convert.ToUInt64(args["campaignid"], CultureInfo.InvariantCulture) != selection.BossId ||
                Convert.ToUInt64(D.Read(formation, "Campaignid"), CultureInfo.InvariantCulture) != selection.BossId)
                throw new ProbeFailure("trial_boss_identity_formation_target_changed");
            MethodInfo original = D.Method(Model.GameType("TrialUI"), "OnAttack", Type.EmptyTypes, false, typeof(bool));
            if (attack == null || attack.GetInvocationList().Length != 1 || !ReferenceEquals(attack.Target, selection.Source) ||
                attack.Method != original ||
                !(ReferenceEquals(D.Read(formation, "OnAttack"), attack) && ReferenceEquals(args["OnAttack"], attack) ||
                    BattleLaunch.OwnsAttack(formation, attack)))
                throw new ProbeFailure("trial_boss_identity_formation_attack_changed");
            return true;
        }

        private Frame Read(D context)
        {
            Frame f = new Frame { Context = context };
            if (context == null) { f.Error = "context_unavailable"; return f; }
            try
            {
                if (context.Paused) { f.Error = "paused"; return f; }
                f.Role = D.Static(Model.FirstType("Global"), "character_");
                f.Client = D.Static(Model.GameType("RootUI"), "m_client");
                if (f.Role == null || f.Client == null) { f.Error = "character_or_connection_unavailable"; return f; }
                f.RoleId = Convert.ToUInt64(D.Read(f.Role, "id"), CultureInfo.InvariantCulture);
                f.Stamina = D.Int(D.Read(f.Role, "stamina")); f.Gold = D.Int(D.Read(f.Role, "gold")); f.BoundGold = D.Int(D.Read(f.Role, "bind_gold"));
                f.Loader = D.Static(Model.FirstType("BaseAsset"), "lds");
                f.Root = D.Read(f.Loader, "currmg") as Component;
                f.Sub = D.Static(Model.GameType("MainUI"), "m_subui");
                uint day = (uint)D.Static(Model.FirstType("TimeUtil"), "_thisDay");
                f.Now = (uint)D.Static(Model.FirstType("TimeUtil"), "_now");
                f.Cycle = f.Now >= day + 18000L ? day + 18000u : day - 68400u;
                if (!training)
                {
                    string clock = DailyRefreshGate.Read(f.Role, out f.Now, out day, out f.Cycle);
                    if (clock != "") { f.Error = clock; return f; }
                }
                f.Loading = ((Behaviour)f.Loader).enabled;
                string level = f.Root == null ? "" : D.Read(f.Root, "LevelID") as string;
                f.Scene = training && level == "SealMonster";
                f.MainScene = level == "Campaign" || level == "Capital";
                f.Home = !f.Loading && f.MainScene && f.Sub == null;
                f.Panel = Ui(context, training ? "SealMonsterUI" : "TrialLevelUI");
                f.TrialUi = training ? null : holding && bossSelected && bossSelection != null ? bossSelection.Source : Ui(context, "TrialUI");
                f.Battle = Ui(context, "BattleUI");
                if (training && f.Battle != null && D.Visible(f.Battle))
                {
                    // BattleUI.Show deactivates the previous scene. AllUi uses
                    // FindObjectsOfType, so recover its inactive stock parent
                    // through the battle's authoritative binding instead.
                    Component parent = D.Read(f.Battle, "m_parentUI") as Component;
                    if (parent != null && parent.GetType() == Model.GameType("SealMonsterUI")) f.Panel = parent;
                }
                f.Formation = f.Panel == null ? null : f.Panel.GetComponent(Model.GameType("FormationUI"));
                f.PanelEnabled = f.Panel != null && D.Enabled(f.Panel);
                f.NormalResult = Ui(context, "BattleNormalResultUI");
                f.Reward = Ui(context, "BattleResultUI", true);
                f.Message = Ui(context, "MessageBox", true);
                f.Consume = Ui(context, "ConsumeUI", true);
                IDictionary acts = D.Read(f.Role, "acts") as IDictionary;
                object row = acts == null ? null : acts[(uint)taskId];
                if (row != null)
                {
                    object template = D.Read(row, "temp");
                    f.Current = D.Int(D.Read(row, "condition_param")); f.Required = D.Int(D.Read(template, "cond_param"));
                    f.DailyObserved = f.Required > 0;
                }
                ReadTarget(f);
                if (!training) f.Formation = holding && bossSelected && bossSelection != null ? bossSelection.Formation :
                    f.Panel == null ? null : f.Panel.GetComponent(Model.GameType("FormationUI"));
                IDictionary handlers = D.Read(f.Client, "m_handlers") as IDictionary;
                if (handlers != null)
                    foreach (DictionaryEntry entry in handlers)
                        if ((float)D.Read(entry.Value, "life") > 0f) { f.NetworkBusy = true; break; }
                if (f.Panel != null && (D.Loading(f.Panel) || !training && f.TrialUi != null && D.Loading(f.TrialUi))) f.Loading = true;
                if (training && f.Scene && f.Panel != null &&
                    ((bool)D.Read(f.Panel, "needload") || (bool)D.Read(f.Panel, "needloadeffect"))) f.Loading = true;
                if (f.Reward != null && D.Loading(f.Reward)) f.Loading = true;
                if (training)
                {
                    object oldMain = D.Read(f.Loader, "oldmainrmg");
                    string oldLevel = oldMain == null ? "Login" : D.Read(oldMain, "LevelID") as string;
                    f.ReturnLevel = oldLevel == "Login" ? "Campaign" : oldLevel ?? "";
                    f.RecoveryBlocker = TrainingRecoveryBlocker(f);
                    Component main = f.Root == null || !f.Home ? null : f.Root.GetComponentInChildren(Model.GameType("MainUI"));
                    f.HomeReady = f.Home && f.Root.gameObject.activeInHierarchy && !f.NetworkBusy &&
                        !(bool)D.Read(f.Loader, "isBattling") && !(bool)D.Read(f.Root, "bLoading") &&
                        D.Int(D.Read(f.Root, "ExpectingCount")) == 0 && main != null && D.Enabled(main) &&
                        (bool)D.Static(Model.FirstType("Global"), "extern_enabled") &&
                        !Visible(f.Panel) && !Visible(f.Formation) && !Visible(f.Battle) && !Visible(f.NormalResult) &&
                        !Visible(f.Reward) && !Visible(f.Message) && !Visible(f.Consume);
                }
                else
                {
                    Component main = f.Root == null || !f.Home ? null : f.Root.GetComponentInChildren(Model.GameType("MainUI"));
                    f.HomeReady = f.Home && ReferenceEquals(context.Root, f.Root) && context.GlobalUi != null &&
                        ReferenceEquals(D.Read(context.GlobalUi, "level_rmg"), f.Root) &&
                        f.Root.GetType() == Model.FirstType("RootMessenger") && f.Root.transform.root == f.Root.transform &&
                        f.Root.gameObject.activeInHierarchy && !(bool)D.Read(f.Loader, "isBattling") &&
                        !(bool)D.Read(f.Root, "bLoading") && D.Int(D.Read(f.Root, "ExpectingCount")) == 0 &&
                        main != null && D.Enabled(main) && !D.Loading(main) &&
                        !f.NetworkBusy && TrialTransportIdle(f) && context.Panels() == "PASS" &&
                        !Visible(f.Panel) && !Visible(f.TrialUi) && !Visible(f.Formation) &&
                        !Visible(f.Battle) && !Visible(f.NormalResult) && !Visible(f.Reward) &&
                        !Visible(f.Message) && !Visible(f.Consume);
                }
            }
            catch (Exception error) { f.Error = error.Message; }
            return f;
        }

        private void ReadTarget(Frame f)
        {
            if (training)
            {
                f.Campaign = D.Read(f.Role, "sealmonsterinfo");
                f.TrainingUnlocked = (bool)D.Method(f.Role.GetType(), "checkUI", new[] { typeof(string) }, false, typeof(bool))
                    .Invoke(f.Role, new object[] { "seal_monster" });
                Array troops = D.Read(f.Campaign, "m_troops") as Array;
                bool allFinished = true;
                for (int index = 0; troops != null && index < troops.Length && index < 6; index++)
                {
                    object troop = troops.GetValue(index);
                    int slot = index + 1;
                    bool finished = (bool)D.Read(f.Campaign, "finish" + slot);
                    allFinished &= finished;
                    if (!finished) f.RemainingMonsters++;
                    if (selected != null && ReferenceEquals(troop, selected))
                    {
                        f.SelectedTargetObserved = true; f.TargetFinished = finished;
                        f.SelectedTargetCooldown = (uint)D.Read(f.Campaign, "cd" + slot);
                    }
                    if (troop == null || finished || D.Int(D.Read(f.Campaign, "troop" + slot)) == 0) continue;
                    uint cooldown = (uint)D.Read(f.Campaign, "cd" + slot);
                    if (cooldown > f.Now)
                    {
                        if (f.NextTrainingCooldown == 0 || cooldown < f.NextTrainingCooldown)
                            f.NextTrainingCooldown = cooldown;
                        continue;
                    }
                    MethodInfo getTemplate = troop.GetType().GetMethod("getTemplate", D.Instance);
                    object monster = getTemplate.Invoke(troop, null);
                    int monsterLevel = D.Int(D.Read(monster, "level")), difficulty = D.Int(D.Read(monster, "difficulty"));
                    if (f.Target == null || monsterLevel < f.TargetLevel || monsterLevel == f.TargetLevel && difficulty < f.TargetDifficulty)
                    {
                        f.Target = troop; f.TargetId = slot; f.TargetLevel = monsterLevel; f.TargetDifficulty = difficulty;
                        f.TargetCooldown = cooldown;
                    }
                }
                if (f.Target == null && f.RemainingMonsters > 0) f.TargetCooldown = f.NextTrainingCooldown;
                int times = D.Int(D.Read(f.Campaign, "times"));
                f.SummonTimes = times;
                object price = D.Template("sealmonster_refresh_gold", (uint)(times + 1));
                uint vip = (byte)D.Method(f.Role.GetType(), "get_VIPLEVEL", Type.EmptyTypes, false, typeof(byte)).Invoke(f.Role, null);
                object vipTemplate = D.Template("vip_template", vip);
                f.CanKillAll = vipTemplate != null && (bool)D.Read(vipTemplate, "sealmonster_killall");
                f.CanSummon = allFinished && f.Target == null && price != null && D.Int(D.Read(price, "gold")) == 0 &&
                    vipTemplate != null && times < D.Int(D.Read(vipTemplate, "sealmonster_refresh_times"));
                return;
            }
            f.Trial = D.Read(f.Role, "trialinfo");
            f.Campaign = D.Read(f.Role, "trial_campaign");
            if (f.Trial != null)
            {
                f.TrialObserved = true;
                f.TrialLevel = D.Int(D.Read(f.Trial, "cur_level"));
                f.TrialChests = D.Int(D.Read(f.Trial, "chest_got"));
                f.BossKilled = D.Int(D.Read(f.Trial, "bosskilled")) == 1;
                f.BossCooldown = (uint)D.Read(f.Trial, "bosscd");
                f.BossRankAllowed = D.Int(D.Read(f.Trial, "trial_rank")) <= D.Int(D.Read(f.Role, "level")) + 5;
            }
            // The installed /Trial RootMessenger reports LevelID "Divinity".
            // Match the exact objects bound by RootUI.startTrial instead.
            GameObject scene = GameObject.Find("/Trial"), panel = GameObject.Find("/Trial/TrialUI");
            if (f.Panel == null && panel != null) f.Panel = panel.GetComponent(Model.GameType("TrialLevelUI"));
            f.Scene = f.Root != null && scene != null && panel != null && f.Panel != null &&
                f.Root.gameObject == scene && f.Root.GetType() == Model.FirstType("RootMessenger") &&
                f.Panel.gameObject == panel && f.Panel.transform.root == f.Root.transform &&
                ReferenceEquals(D.Read(f.Panel, "rmg"), f.Root) && ReferenceEquals(f.Context.Root, f.Root) &&
                f.Context.GlobalUi != null && ReferenceEquals(D.Read(f.Context.GlobalUi, "level_rmg"), f.Root);
            if (f.Campaign == null) return;
            f.Progress = D.Int(D.Read(f.Campaign, "finish"));
            Array candidates = D.Read(f.Campaign, "troops") as Array;
            foreach (object candidate in candidates ?? new object[0])
            {
                if (candidate == null) continue;
                bool killed = (bool)D.Read(candidate, "killed");
                if (selected != null && ReferenceEquals(candidate, selected)) f.TargetFinished = killed;
                int id = D.Int(D.Read(candidate, "idtroop"));
                if (f.Target == null && !killed && id == f.Progress + 1) { f.Target = candidate; f.TargetId = id; }
            }
        }

        private static bool CanResume(Frame f)
        {
            return (f.Scene && f.Sub == null && f.Target != null && D.Enabled(f.Panel) ||
                (f.Scene || f.MainScene) && ReferenceEquals(f.Context.Root, f.Root) &&
                f.Sub != null && ReferenceEquals(f.Sub, f.TrialUi) && D.Enabled(f.TrialUi)) && !f.Loading && !f.NetworkBusy &&
                (f.Consume == null || !D.Visible(f.Consume)) &&
                (f.Message == null || !D.Visible(f.Message)) && (f.Reward == null || !D.Visible(f.Reward)) &&
                (f.NormalResult == null || !D.Visible(f.NormalResult)) && (f.Battle == null || !D.Visible(f.Battle));
        }

        private static string TrainingRecoveryBlocker(Frame f)
        {
            if (!f.Scene) return "not_training_scene";
            if (f.Root == null || !f.Root.gameObject.activeInHierarchy || f.Context == null ||
                !ReferenceEquals(f.Context.Root, f.Root) || f.Panel == null ||
                f.Panel.GetType() != Model.GameType("SealMonsterUI") ||
                !ReferenceEquals(D.Read(f.Panel, "rmg"), f.Root) || f.Panel.transform.root != f.Root.transform)
                return "training_recovery_owner_changed";
            if (f.Loading || f.NetworkBusy || (bool)D.Read(f.Loader, "isBattling") ||
                (bool)D.Read(f.Root, "bLoading") || D.Int(D.Read(f.Root, "ExpectingCount")) != 0)
                return "training_recovery_busy";
            if (!f.PanelEnabled || !(bool)D.Static(Model.FirstType("Global"), "extern_enabled"))
                return "training_recovery_input_disabled";
            if (f.Sub != null || Visible(f.Formation) || Visible(f.Battle) || Visible(f.NormalResult) ||
                Visible(f.Reward) || Visible(f.Message) || Visible(f.Consume))
                return "training_recovery_overlay_visible";
            if (f.ReturnLevel != "Capital" && f.ReturnLevel != "Campaign")
                return "training_return_destination_unsupported:" + f.ReturnLevel;
            return "";
        }

        private static bool Visible(Component component) { return component != null && D.Visible(component); }

        private static bool TrialTransportIdle(Frame f)
        {
            IDictionary handlers = D.Read(f.Client, "m_handlers") as IDictionary;
            IList messages = D.Read(f.Client, "messages") as IList, sends = D.Read(f.Client, "m_sendDatas") as IList;
            return ReferenceEquals(f.Client, DailyTransport.CurrentClient(f.Role)) && DailyTransport.Read(f.Client) != null &&
                handlers != null && messages != null && messages.Count == 0 && sends != null && sends.Count == 0 &&
                !D.Bool(D.Read(f.Client, "m_bSending")) && !DevelopmentCallbackCapture.AnyCallbackExecuting;
        }

        private bool CanSkipTrainingBattle(Frame f)
        {
            if (f.Battle == null || f.Panel == null || selected == null ||
                !ReferenceEquals(D.Read(f.Battle, "m_parentUI"), f.Panel) ||
                !ReferenceEquals(D.Read(f.Panel, "m_troop"), selected) || D.Loading(f.Battle) ||
                !(bool)D.Read(f.Battle, "bBattle_started") || !(bool)D.Read(f.Battle, "bBattle_fighting")) return false;
            object interpreter = D.Read(f.Battle, "interpreter");
            // SealMonsterUI supplies the complete report before starting battle
            // playback, but selects viewFull. Its result-button/VIP gates do not
            // describe report readiness. Wait for InitTroop, then run the stock
            // final-state/0x408 branch even while the UI only offers surrender.
            return interpreter != null && D.Read(interpreter, "m_report") != null &&
                (bool)D.Method(f.Battle.GetType(), "get_ArmyBehaviourCreated", Type.EmptyTypes, false, typeof(bool))
                    .Invoke(f.Battle, null);
        }

        private static Component Ui(D context, string name, bool first = false)
        {
            Type type = first ? Model.FirstType(name) : Model.GameType(name);
            Component found = null;
            foreach (UnityEngine.Object item in context.All)
            {
                Component candidate = item as Component;
                if (candidate == null || candidate.GetType() != type) continue;
                if (found == null || candidate.gameObject.activeInHierarchy && ((Behaviour)candidate).enabled) found = candidate;
            }
            return found;
        }

        internal void NotInvoked(string reason)
        { queued = null; GuiInvocationAttempted = false; Reason = reason; Revision++; }
        internal void Stop() { Stop("stopped"); }
        internal void Stop(string reason)
        { stopped = true; queued = null; nextAction = ""; Publish("BLOCKED", reason); }

        private string JsonBody()
        {
            return "\"task_id\":" + taskId + ",\"role_id\":" + D.Q(frame == null ? "0" : frame.RoleId.ToString(CultureInfo.InvariantCulture)) +
                ",\"operation_id\":" + D.Q(operationId) + ",\"status\":" + D.Q(Status) + ",\"reason\":" + D.Q(Reason) +
                ",\"next_action\":" + D.Q(nextAction) + ",\"activity_count\":" + (frame == null || frame.Error != "" || !frame.DailyObserved ? "null" : frame.Current.ToString(CultureInfo.InvariantCulture)) +
                ",\"required\":" + (frame == null || frame.Error != "" || !frame.DailyObserved ? "null" : frame.Required.ToString(CultureInfo.InvariantCulture)) +
                ",\"refresh_cycle\":" + (frame == null ? 0u : frame.Cycle) +
                ",\"target_id\":" + (training ? frame == null ? 0 : frame.TargetId : selectedId != 0 ? selectedId : frame == null ? 0 : frame.TargetId) +
                (training ? ",\"remaining_monsters\":" + (frame == null ? 0 : frame.RemainingMonsters) +
                    ",\"training_scene\":" + D.B(frame != null && frame.Scene) +
                    ",\"home_ready\":" + D.B(frame != null && frame.Error == "" && frame.HomeReady) +
                    ",\"training_panel_enabled\":" + D.B(frame != null && frame.PanelEnabled) +
                    ",\"return_level\":" + D.Q(frame == null ? "" : frame.ReturnLevel) +
                    ",\"recovery_reason\":" + D.Q(frame == null ? "" : frame.RecoveryBlocker) +
                    ",\"return_invoked\":" + D.B(returnInvoked) +
                    ",\"recovery_complete\":" + D.B(recoveryComplete) +
                    ",\"target_level\":" + (frame == null ? 0 : frame.TargetLevel) +
                    ",\"target_difficulty\":" + (frame == null ? 0 : frame.TargetDifficulty) +
                    ",\"target_cooldown\":" + (frame == null ? 0u : frame.TargetCooldown) +
                    ",\"server_time\":" + (frame == null ? 0u : frame.Now) +
                    ",\"summon_times\":" + (frame == null ? 0 : frame.SummonTimes) +
                    ",\"free_summon_available\":" + D.B(frame != null && frame.CanSummon) +
                    ",\"free_batch_available\":" + D.B(frame != null && frame.CanKillAll) +
                    ",\"batch_kill_enabled\":" + D.B(allowTrainingBatchKill) +
                    ",\"skip_invoked\":" + D.B(skipInvoked) +
                    ",\"battle_end_requested\":" + D.B(battleEndRequested) +
                    ",\"clearing_complete\":" + D.B(trainingRunComplete) :
                    ",\"trial_level\":" + (frame == null ? 0 : frame.TrialLevel) +
                    ",\"trial_chests\":" + (frame == null ? 0 : frame.TrialChests) +
                    ",\"chapter_progress\":" + (frame == null ? 0 : frame.Progress) +
                    ",\"boss_killed\":" + D.B(frame != null && frame.BossKilled) +
                    ",\"boss_cooldown\":" + (frame == null ? 0u : frame.BossCooldown) +
                    ",\"server_time\":" + (frame == null ? 0u : frame.Now) +
                    ",\"boss_rank_allowed\":" + D.B(frame != null && frame.BossRankAllowed) +
                    ",\"boss_selected\":" + D.B(bossSelected) +
                    ",\"home_ready\":" + D.B(frame != null && frame.Error == "" && frame.HomeReady) +
                    ",\"release_verified\":" + D.B(ReleaseVerified) +
                    ",\"last_start_attempt\":" + (lastTrialStart == null ? "null" : lastTrialStart.Json()) +
                    ",\"clearing_complete\":" + D.B(trialRunComplete)) +
                ",\"stamina\":" + (frame == null ? 0 : frame.Stamina) + ",\"mutation_attempted\":" + D.B(MutationAttempted) +
                ",\"resumed_existing_run\":" + D.B(resumedExistingRun) +
                ",\"entry_request_attempted\":" + D.B(entryRequestAttempted) +
                ",\"battle_request_attempted\":" + D.B(battleRequestAttempted) +
                ",\"task_confirmed\":" + D.B(TaskConfirmed) + ",\"cleanup_pending\":" + D.B(CleanupPending) + ",\"busy\":" + D.B(Busy);
        }
        internal string Json() { return "{\"schema\":1,\"revision\":" + Revision + "," + JsonBody() + "}"; }
    }
}
