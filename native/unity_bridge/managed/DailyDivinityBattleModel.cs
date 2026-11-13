using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using UnityEngine;
using D = JiangShen.AssistantRuntime.SharedDevelopmentContext;

namespace JiangShen.AssistantRuntime
{
    internal delegate D DailyDivinityContextProvider();
    internal delegate bool DailyDivinityAdmissionCheck();
    // prepare runs before the controller reads the exact nonce-bound command.
    // check runs after that read, immediately before admission to the stock call.
    internal delegate bool DailyDivinityAdmission(DailyDivinityAdmissionCheck prepare, DailyDivinityAdmissionCheck check);
    internal delegate object DailyDivinityLiveHandler();
#if JSB_DAILY_DIVINITY_TEST
    internal delegate DailyDivinityReplyTap DailyDivinityTestAttach(ushort opcode, DailyDivinityAdmissionCheck before, Action<byte[]> after, Action<byte[]> preflight);
    internal delegate DailyDivinityBattleAnchor DailyDivinityTestBattle();
    internal delegate object DailyDivinityTestHandler(ushort opcode);
#endif

    // Legacy callers may add a request observer. The default script route
    // follows the game's own callbacks and the completed BattleUI projection.
    internal delegate IDailyDivinityNaturalWitness DailyDivinityNaturalArm(DailyDivinityNaturalScope scope);

    internal interface IDailyDivinityNaturalWitness
    {
        bool Armed { get; }
        DailyDivinityNaturalEvidence Read();
        // Stop observing and continue forwarding stock callbacks. Return true
        // only after verifying restoration or independent removal/replacement
        // of the exact registration, with no owned callback still in flight.
        // Contention returns false without blocking. Stock forwarding must
        // survive exceptions. The model retains ownership and retries.
        bool TryDisarmAndRestore();
    }

    internal sealed class DailyDivinityNaturalScope
    {
        internal readonly string Operation;
        internal readonly object Character, Client, Scene, Panel, TargetData;
        internal readonly DailyTransport Transport;
        internal readonly ulong Role;
        internal readonly uint Cycle;
        internal readonly int Target;
        internal DailyDivinityBattleAnchor Battle { get; private set; }

        internal DailyDivinityNaturalScope(string operation, DailyDivinityBattleModel.Frame frame, int target)
        {
            Operation = operation;
            Character = frame.Character; Client = frame.Client; Transport = frame.Transport;
            Scene = frame.Scene; Panel = frame.Panel; TargetData = frame.SelectedData;
            Role = frame.Role; Cycle = frame.Cycle; Target = target;
        }

        internal void Bind(DailyDivinityBattleAnchor battle) { Battle = battle; }
    }

    internal sealed class DailyDivinityBattleAnchor
    {
        internal readonly object Battle, Root, Report;
        internal DailyDivinityBattleAnchor(object battle, object root, object report)
        { Battle = battle; Root = root; Report = report; }
    }

    internal sealed class DailyDivinityNaturalEvidence
    {
        // Record these at the stock registration, before dispatch or UI
        // progression. Registration/dispatch must still belong to Scope.
        internal DailyDivinityNaturalScope Scope { get; set; }
        internal DailyDivinityBattleAnchor Battle { get; set; }
        internal object RegisteredHandler { get; set; }
        internal object DispatchedHandler { get; set; }
        internal int Calls { get; set; }
        internal ulong ReportAtRegistration { get; set; }
        internal bool ViewAllAtRegistration { get; set; }
        internal bool NaturalRegistration { get; set; }
        internal bool CallbackReturned { get; set; }
        internal byte[] Body { get; set; }
    }

    internal sealed class DailyDivinityBattleModel
    {
        internal const int TaskId = 8, Category = 2, TaskTarget = 4;
        private const int FullRosterMask = (1 << (TaskTarget + 1)) - 1;
        internal const ushort FormationOpcode = 298, BattleOpcode = 1201, EndOpcode = 1032, ActivityOpcode = 355;
        private const int GuiMs = 1500, LoadingMs = 25000, BattleMs = 300000, ProgressMs = 20000;
        private const uint TargetTemplate = 116441;
        private const string TargetName = "\u5de1\u6e38\u5929\u795e";
        private const string TaskNote = "\u51fb\u8d25" + TargetName;

        internal sealed class Frame
        {
            internal D Context;
            internal DailyTransport Transport;
            internal object Character, Client, Loader, GlobalRoot, Root, HomeMain, Scene, Panel, Formation, Form, OnAttack,
                Rows, Reward, Listed, Row, TaskTemplate, RewardTemplate, ListedRow, Troops, Selected, SelectedData, Battle, BattleRoot, ReportToken;
            internal ulong Role, ReportId;
#if JSB_DAILY_DIVINITY_TEST
            internal object SocketWitness;
#endif
            internal uint Cycle, Now, Next, Cooldown, SelectedCooldown;
            internal int Task, TaskCategory, Required, Count, Refresh, ListedCount, ListedRefresh, Target = -1, KilledMask, FormId, ChallengeLevel;
            internal int Tick;
            internal bool Home, PanelReady, FormReady, NetworkIdle, Battling, BattleOwned, BattleVisible, Fighting, WaitEnd,
                UseEnd, Replay, Surrendered, SelectedOwned, SelectedKilled, SelectedIdentity;
            internal string Error = "", TaskError = "";
            internal readonly Dictionary<ushort, object> Handlers = new Dictionary<ushort, object>();

            internal object Handler(ushort op)
            {
                object value;
                return Handlers.TryGetValue(op, out value) ? value : null;
            }

            internal bool SameOwner(Frame other)
            {
                return other != null && Role != 0 && Role == other.Role && Cycle != 0 && Cycle == other.Cycle &&
                    Next == other.Next && Now >= other.Now && Character != null && Client != null && Loader != null &&
                    GlobalRoot != null && ReferenceEquals(Character, other.Character) && ReferenceEquals(Client, other.Client) &&
                    ReferenceEquals(Loader, other.Loader) && ReferenceEquals(GlobalRoot, other.GlobalRoot) &&
                    SameTransport(other);
            }

            private bool SameTransport(Frame other)
            {
#if JSB_DAILY_DIVINITY_TEST
                if (Transport == null) return SocketWitness != null && ReferenceEquals(SocketWitness, other.SocketWitness);
#endif
                return Transport != null && Transport.Same(other.Transport);
            }

            internal bool SameTask(Frame other)
            {
                return Task == TaskId && other.Task == TaskId && TaskCategory == Category && other.TaskCategory == Category &&
                    Required == 1 && other.Required == 1 && Row != null && ListedRow != null &&
                    Count == ListedCount && Refresh == ListedRefresh && other.Row != null && other.ListedRow != null &&
                    other.Count == other.ListedCount && other.Refresh == other.ListedRefresh && ReferenceEquals(Rows, other.Rows) &&
                    ReferenceEquals(Reward, other.Reward) && ReferenceEquals(Listed, other.Listed) &&
                    ReferenceEquals(TaskTemplate, other.TaskTemplate) && ReferenceEquals(RewardTemplate, other.RewardTemplate);
            }

            internal bool SameCounter(Frame other)
            { return SameTask(other) && Count == other.Count && Refresh == other.Refresh && ReferenceEquals(Row, other.Row); }

            internal bool SameTarget(Frame other)
            {
                if (other != null && Target < 0 && other.Target < 0 &&
                    KilledMask == FullRosterMask && other.KilledMask == FullRosterMask)
                    return ChallengeLevel == other.ChallengeLevel && ReferenceEquals(Troops, other.Troops);
                return SelectedIdentity && other.SelectedIdentity && Target == other.Target && KilledMask == other.KilledMask &&
                    ChallengeLevel == other.ChallengeLevel && Cooldown == other.Cooldown && ReferenceEquals(Troops, other.Troops) &&
                    ReferenceEquals(Selected, other.Selected) && ReferenceEquals(SelectedData, other.SelectedData);
            }

            internal bool SameAdmission(Frame other)
            {
                return Error == "" && other.Error == "" && TaskError == "" && other.TaskError == "" &&
                    SameOwner(other) && SameCounter(other) && SameTarget(other) &&
                    ReferenceEquals(Root, other.Root) && ReferenceEquals(HomeMain, other.HomeMain) &&
                    ReferenceEquals(Scene, other.Scene) && ReferenceEquals(Panel, other.Panel) && ReferenceEquals(Formation, other.Formation) &&
                    ReferenceEquals(Form, other.Form) && ReferenceEquals(OnAttack, other.OnAttack) && FormId == other.FormId &&
                    ReferenceEquals(Handler(ActivityOpcode), other.Handler(ActivityOpcode)) &&
                    Home == other.Home && PanelReady == other.PanelReady && FormReady == other.FormReady && NetworkIdle == other.NetworkIdle;
            }
        }

        private sealed class Intent
        {
            internal string Operation, Action;
            internal int Deadline, Target;
            internal Frame Baseline;
        }

        private readonly Hashtable operations = new Hashtable();
        private sealed class Defeated
        {
            internal uint Cycle;
            internal int Mask;
        }
        private readonly Dictionary<ulong, Defeated> defeated = new Dictionary<ulong, Defeated>();
        private readonly DailyDivinityNaturalArm armNatural;
        // origin owns the whole page visit; targetOrigin owns one formation
        // admission. Completing a target must not release the page visit.
        private Frame frame, origin, targetOrigin, selected, committed;
        private Intent queued, operation;
        private DailyDivinityReplyTap formTap, battleTap, activity;
        private BattleLaunchAttempt launch;
        private DailyDivinityReplyTap endTap;
        private DailyDivinityWire endResponse;
        private Component resultBattle;
        private DailyTimedWire.Progress progress;
        private DailyDivinityNaturalScope scope;
        private IDailyDivinityNaturalWitness natural;
        private int lastSequence, started, progressStarted;
        private int capturedFrame = -1;
        private int attemptedMask, settledMask;
        private bool stopped, uiHold, resultKnown, targetClosed, stoppedInitialReply;
        private string initialBinding = "", initialOutcomeBinding = "";
        private string progressError;
#if JSB_DAILY_DIVINITY_TEST
        internal DailyDivinityTestAttach TestAttach { get; set; }
        internal DailyDivinityTestBattle TestBattle { get; set; }
        internal DailyDivinityTestHandler TestHandler { get; set; }
        internal DailyDivinityAdmissionCheck TestCallbackOwner { get; set; }
#endif
        internal int Revision { get; private set; }
        internal string Status { get; private set; }
        internal string Reason { get; private set; }
        internal bool MutationAttempted { get { return committed != null; } }
        internal bool? BattleWon { get; private set; }
        internal bool TaskConfirmed { get; private set; }
        internal bool CleanupPending { get; private set; }
        internal bool Busy { get { return queued != null || uiHold || Unresolved; } }
        internal bool Unresolved { get { return CleanupPending || Status == "UNKNOWN" || Status == "PENDING"; } }
        internal int Target { get { return operation == null ? -1 : operation.Target; } }
        internal int CandidateTarget { get { return frame != null && frame.Error == "" && frame.SelectedIdentity ? frame.Target : -1; } }
        internal int? ActivityCount { get { return frame != null && frame.Error == "" && frame.TaskError == "" && frame.SameTask(frame) ? (int?)frame.Count : null; } }
        internal bool OutcomeUnknown { get { return Status == "UNKNOWN"; } }

        internal BattlePresentationOwnership OwnedBattlePresentation(Component battle)
        {
            if (stopped) return null;
            Intent ownedOperation = operation;
            Frame ownedFrame = committed;
            DailyDivinityNaturalScope ownedScope = scope;
            DailyDivinityBattleAnchor anchor = ownedScope == null ? null : ownedScope.Battle;
            if (ownedOperation == null || ownedFrame == null || anchor == null ||
                !ReferenceEquals(anchor.Battle, battle)) return null;
            Component source = ownedFrame.Panel as Component;
            object target = ownedFrame.Selected;
            string operationId = ownedOperation.Operation;
            int targetId = ownedOperation.Target;
            BattleSettlementAdmission verify = delegate
            {
                try
                {
                    uint now, day, currentCycle;
                    if (!uiHold || Status == "UNKNOWN" || operation != ownedOperation ||
                        committed != ownedFrame || scope != ownedScope || ownedScope.Battle != anchor ||
                        operationId == "" || ownedOperation.Operation != operationId || ownedOperation.Target != targetId ||
                        targetId < 0 || targetId > TaskTarget || source == null || target == null || battle == null ||
                        !ReferenceEquals(ownedScope.Character, ownedFrame.Character) || ownedScope.Role != ownedFrame.Role ||
                        ownedScope.Cycle != ownedFrame.Cycle || !ReferenceEquals(ownedScope.Panel, source) ||
                        !ReferenceEquals(ownedScope.TargetData, ownedFrame.SelectedData) ||
                        !ReferenceEquals(D.Static(Model.FirstType("Global"), "character_"), ownedFrame.Character) ||
                        Field<ulong>(ownedFrame.Character, "id") != ownedFrame.Role ||
                        DailyRefreshGate.ReadClock(ownedFrame.Character, out now, out day, out currentCycle) != "" ||
                        currentCycle != ownedFrame.Cycle || ownedFrame.Transport == null ||
                        !ownedFrame.Transport.Same(DailyTransport.Read(DailyTransport.CurrentClient(ownedFrame.Character))) ||
                        !ReferenceEquals(D.Read(ownedFrame.Character, "god_troops"), ownedFrame.Troops) ||
                        !ReferenceEquals(((Array)ownedFrame.Troops).GetValue(targetId), target) ||
                        Field<byte>(target, "idtroop") != targetId ||
                        !ReferenceEquals(D.Read(target, "data_"), ownedFrame.SelectedData) ||
                        !ReferenceEquals(D.Read(source, "m_troop"), target) ||
                        !ReferenceEquals(D.Read(source, "rmg"), ownedFrame.Scene) ||
                        !ReferenceEquals(D.Read(battle, "m_parentUI"), source) ||
                        !ReferenceEquals(D.Read(battle, "m_prevUI"), ownedFrame.Scene) ||
                        !ReferenceEquals(anchor.Battle, battle)) return false;
                    object interpreter = D.Read(battle, "interpreter");
                    return interpreter != null && ReferenceEquals(D.Read(interpreter, "m_report"), anchor.Report);
                }
                catch (Exception) { return false; }
            };
            return verify() ? new BattlePresentationOwnership(BattleMode.Divinity, operationId, source, target,
                targetId.ToString(CultureInfo.InvariantCulture), verify) : null;
        }

        internal DailyDivinityBattleModel() : this(null) { }
        internal DailyDivinityBattleModel(DailyDivinityNaturalArm naturalProvider)
        { armNatural = naturalProvider; Set("IDLE", "divinity_unobserved"); }

        internal void Capture(D context)
        {
            // A pending tap retains the original client even if the current
            // context is unavailable or has switched to a different client.
            RetryCleanup();
            object client = context == null ? null : context.Client;
            if (client == null || !Monitor.TryEnter(client, 0))
            {
                if (Status == "PENDING" && (client == null || Expired(Environment.TickCount, started, BattleMs)))
                    Unknown("divinity_client_unavailable");
                return;
            }
            try { Observe(Read(context)); }
            finally { Monitor.Exit(client); }
        }

        internal void Observe(Frame next)
        {
            Frame previous = frame;
            frame = next;
            capturedFrame = Time.frameCount;
            if (previous == null || !next.SameAdmission(previous)) Advance();
            if (origin == null) return;
            if (Status == "UNKNOWN") return;
            if (!next.SameOwner(origin))
            {
                if (Status == "PENDING") Unknown("divinity_owner_changed");
                else if (!resultKnown && !stopped) Set("BLOCKED", "divinity_owner_changed");
                return;
            }
            if (Status == "PENDING")
            {
                try { ObserveCommitted(next); }
                catch (Exception) { Unknown("divinity_result_readback_failed"); }
                if (!resultKnown || Status == "UNKNOWN") return;
            }
            if (Status == "OPENING" || Status == "SELECTING")
            {
                if (Expired(next.Tick, started, LoadingMs)) { Set("BLOCKED", "divinity_stock_loading_timeout"); return; }
                if (next.Error != "" || next.TaskError != "" || !next.SameCounter(targetOrigin) || !next.SameTarget(targetOrigin) ||
                    next.Handler(ActivityOpcode) != targetOrigin.Handler(ActivityOpcode))
                { Set("BLOCKED", "divinity_open_context_changed"); return; }
                if (Status == "OPENING" && OwnPanel(next) && next.PanelReady) Set("OPEN", "");
                else if (Status == "SELECTING" && OwnPanel(next) && next.FormReady)
                { selected = next; Set("READY", ""); }
                return;
            }
            if (!targetClosed && TargetClosed(next)) { targetClosed = true; Advance(); }
            // Release a known result or pre-commit interruption only after a
            // stock return to the exact original, clean home.
            if (uiHold && queued == null && ReturnedHome(next))
            {
                uiHold = false;
                scope = null;
                if (!resultKnown && Status != "BLOCKED") Set("BLOCKED", "divinity_returned_without_commit");
                else Advance();
            }
        }

        private bool ReturnedHome(Frame next)
        {
            return next.Home && next.NetworkIdle && ReferenceEquals(next.Root, origin.Root) &&
                ReferenceEquals(next.HomeMain, origin.HomeMain);
        }

        internal bool Queue(int task, ulong role, string id, int sequence, int revision, string action, out string why)
        {
            int target = action == "open" || action == "select" && TargetClosed() ? CandidateTarget : Target;
            if (IsCleanup(action))
            {
                why = "divinity_cleanup_unavailable";
                if (frame == null || task != TaskId || frame.Role != role || queued != null || revision != Revision ||
                    sequence <= lastSequence || operation == null || operation.Operation != id || !CleanupAvailable(action)) return false;
                lastSequence = sequence;
                queued = new Intent { Operation = id, Action = action, Target = target, Baseline = frame, Deadline = Environment.TickCount };
                Advance(); why = ""; return true;
            }
            return Queue(task, role, id, sequence, revision, action, target, out why);
        }

        internal bool Queue(int task, ulong role, string operationId, int sequence, int revision, string action, int target, out string why)
        {
            why = "divinity_command_identity";
            if (task != TaskId || frame == null || frame.Role != role || role == 0 || sequence <= lastSequence ||
                revision != Revision || queued != null || stopped || CleanupPending ||
                !new DevelopmentCommand { OperationId = operationId }.ValidId())
            {
                why = String.Format(CultureInfo.InvariantCulture,
                    "divinity_command_identity action={0} task={1} role={2}/{3} seq={4}/{5} revision={6}/{7} queued={8} stopped={9} cleanup={10}",
                    action, task, role, frame == null ? 0 : frame.Role, sequence, lastSequence, revision, Revision,
                    queued != null, stopped, CleanupPending);
                return false;
            }
            bool open = action == "open";
            bool nextTarget = action == "select" && TargetClosed() && CanContinue(frame);
            if (open ? Busy || operations.Count >= 1024 || operations.Contains(operationId) :
                operation == null || operation.Operation != operationId || (!nextTarget && operation.Target != target))
            {
                why = String.Format(CultureInfo.InvariantCulture,
                    "divinity_command_operation action={0} busy={1} operation_match={2} target={3}/{4}",
                    action, Busy, operation != null && operation.Operation == operationId,
                    target, operation == null ? -1 : operation.Target);
                return false;
            }
            why = Gate(frame, action, target);
            if (why != "") return false;
            lastSequence = sequence;
            queued = new Intent { Operation = operationId, Action = action, Target = target,
                Baseline = frame, Deadline = frame.Tick };
            if (open) operations.Add(operationId, true);
            Advance();
            return true;
        }

        internal bool ExecuteGui(DailyDivinityContextProvider fresh, DevelopmentPublication publish, DailyDivinityAdmission admit, out string why)
        {
            why = "divinity_not_queued";
            Intent intent = queued;
            if (intent == null) return false;
            if (IsCleanup(intent.Action)) return ExecuteCleanup(intent, fresh, publish, admit, out why);
            object client = intent.Baseline.Client;
            if (!D.Gui || fresh == null || publish == null || admit == null || !Monitor.TryEnter(client, 0))
                return Reject("divinity_gui_or_client_unavailable", out why);
            bool publishing = false;
            try
            {
                Frame current = null;
                MethodInfo method = null;
                object receiver = null;
                object[] arguments = null;
                FieldInfo fight = null;
                string rejected = "divinity_final_command_changed";
                bool start = intent.Action == "start";
                publishing = true; publish(); publishing = false;
                bool passed = admit(delegate
                {
                    if (queued != intent || stopped || !D.Gui || Expired(Environment.TickCount, intent.Deadline, GuiMs)) return false;
                    current = Read(fresh());
                    rejected = Gate(current, intent.Action, intent.Target);
                    if (rejected != "" || !current.SameAdmission(intent.Baseline)) return false;
                    BindCall(current, intent.Action, out method, out receiver, out arguments, out fight);
                    if (start && !ArmAttempt(current, intent))
                    { rejected = "divinity_synchronous_witness_unavailable"; return false; }
                    return true;
                }, delegate
                {
                    return current != null && method != null && queued == intent && !stopped && D.Gui &&
                        !Expired(Environment.TickCount, intent.Deadline, GuiMs) && StillCurrent(current, intent.Action) &&
                        (!start || WitnessesArmed());
                });
                if (!passed || current == null || method == null || queued != intent || stopped ||
                    !D.Gui || Expired(Environment.TickCount, intent.Deadline, GuiMs) || !StillCurrent(current, intent.Action) ||
                    (start && !WitnessesArmed()))
                    return Reject(rejected == "" ? "divinity_gui_baseline_or_admission_changed" : rejected, out why);

                Begin(current, intent);
                if (start)
                {
                    launch = BattleLaunch.Formation(BattleMode.Divinity, (Component)current.Formation, (Component)current.Panel,
                        current.Selected, delegate { return CommittedCallbackOwner(false); }, null);
                    BindFormationReply();
                }
                else
                {
                    if (fight != null) fight.SetValue(receiver, true);
                    method.Invoke(receiver, arguments);
                }
                why = Status == "UNKNOWN" ? Reason : "";
                return why == "";
            }
            catch (Exception)
            {
                Reject("divinity_stock_or_admission_failed", out why);
                if (publishing) throw;
                return false;
            }
            finally { Monitor.Exit(client); }
        }

        private void Begin(Frame current, Intent intent)
        {
            queued = null;
            started = current.Tick;
            if (intent.Action == "open")
            {
                operation = intent;
                origin = current;
                targetOrigin = current;
                attemptedMask = settledMask = 0;
                selected = committed = null;
                endResponse = null; resultBattle = null;
                progress = null; progressError = null;
                resultKnown = targetClosed = TaskConfirmed = false;
                stoppedInitialReply = false; initialBinding = initialOutcomeBinding = "";
                BattleWon = null;
                uiHold = true;
                Set("OPENING", "divinity_stock_scene_loading");
            }
            else if (intent.Action == "select")
            {
                if (resultKnown)
                {
                    // All callbacks and rewards of the previous target are
                    // closed before retargeting this same owned page.
                    operation.Target = intent.Target;
                    targetOrigin = current;
                    selected = committed = null;
                    scope = null;
                    endResponse = null; resultBattle = null;
                    progress = null; progressError = null;
                    resultKnown = targetClosed = TaskConfirmed = false;
                    stoppedInitialReply = false; initialBinding = initialOutcomeBinding = "";
                    BattleWon = null;
                }
                Set("SELECTING", "divinity_stock_formation_loading");
            }
            else
            {
                committed = current;
                attemptedMask |= 1 << intent.Target;
                Set("PENDING", "divinity_waiting_stock_298");
            }
        }

#if JSB_DAILY_DIVINITY_TEST
        internal bool BeginQueuedForTest(Frame fresh, DevelopmentPublication stockCall)
        {
            Intent intent = queued;
            if (intent == null || Expired(fresh.Tick, intent.Deadline, GuiMs) ||
                Gate(fresh, intent.Action, intent.Target) != "" || !fresh.SameAdmission(intent.Baseline)) return false;
            bool start = intent.Action == "start";
            if (start && !ArmAttempt(fresh, intent))
            {
                string why;
                return Reject("divinity_synchronous_witness_unavailable", out why);
            }
            Begin(fresh, intent);
            if (stockCall != null) stockCall();
            if (start) BindFormationReply();
            return true;
        }
#endif

        internal void NotInvoked(string why)
        {
            if (queued == null) return;
            queued = null;
            Detach();
            Set("BLOCKED", why);
        }

        internal void Stop()
        {
            // Stop closes launch admission. A request already registered by
            // the stock 298 callback still owns its one validated native reply.
            // Bind that handoff if Stop was called reentrantly before the
            // observer's after-hook could attach it.
            if (Status == "PENDING" && battleTap == null && committed != null)
            {
                try
                {
#if JSB_DAILY_DIVINITY_TEST
                    if (TestHandler != null ? TestHandler(BattleOpcode) != null : StockHandler(committed.Client, BattleOpcode, committed.Panel) != null)
#else
                    if (StockHandler(committed.Client, BattleOpcode, committed.Panel) != null)
#endif
                        BindBattleReply();
                }
                catch (Exception) { }
            }
            stoppedInitialReply = stoppedInitialReply || !stopped && Status == "PENDING" && battleTap != null && battleTap.Error == null;
            stopped = true;
            queued = null;
            if (Status == "PENDING")
            { Set("UNKNOWN", "divinity_stopped_after_298"); Detach(stoppedInitialReply); }
            else
            {
                Detach(stoppedInitialReply);
                if (!resultKnown && Status != "UNKNOWN") Set("BLOCKED", "divinity_stopped_before_298");
            }
        }

        private bool Reject(string why, out string output)
        {
            output = why;
            queued = null;
            if (Status == "PENDING") Unknown(why);
            else
            {
                Detach();
                if (Status != "UNKNOWN") Set("BLOCKED", why);
            }
            return false;
        }

        private string Gate(Frame f, string action, int target)
        {
            if (f == null || f.Error != "" || f.TaskError != "" || !f.NetworkIdle || f.Handler(ActivityOpcode) == null ||
                !f.SameTask(f) || f.Refresh < 0 || !f.SelectedIdentity ||
                target < 0 || target > TaskTarget || target != f.Target || f.Cooldown > f.Now)
                return "divinity_task_target_cooldown_or_network";
            if (resultKnown && BattleWon == false && operation != null && origin != null &&
                f.Role == origin.Role && f.Cycle == origin.Cycle)
                return "divinity_target_already_lost";
            Defeated prior;
            if (defeated.TryGetValue(f.Role, out prior) && (prior.Cycle > f.Cycle ||
                (prior.Cycle == f.Cycle && ((prior.Mask & (1 << target)) != 0 || (f.KilledMask & prior.Mask) != prior.Mask))))
                return "divinity_fresh_stock_target_required";
            if (action == "open")
            {
                if (!f.Home) return "divinity_stock_home_required";
                return "";
            }
            if (action == "select" && TargetClosed() && CanContinue(f)) return "";
            if (!OwnPanel(f) || !f.SameCounter(targetOrigin) || !f.SameTarget(targetOrigin)) return "divinity_owned_target_changed";
            if (action == "select" && Status == "OPEN" && f.PanelReady) return "";
            if (action == "start" && (attemptedMask & (1 << target)) != 0) return "divinity_target_already_attempted";
            if (action == "start" && Status == "READY" && f.FormReady && f.SameAdmission(selected)) return "";
            return "divinity_stock_formation_unavailable";
        }

        private bool OwnPanel(Frame f)
        {
            return f.SameOwner(origin) && f.Scene != null && f.Panel != null && ReferenceEquals(f.Root, f.Scene) &&
                ReferenceEquals(f.Scene, origin.Scene) && ReferenceEquals(f.Panel, origin.Panel);
        }

        private bool ArmAttempt(Frame current, Intent intent)
        {
            scope = new DailyDivinityNaturalScope(intent.Operation, current, intent.Target);
            natural = armNatural == null ? null : armNatural(scope);
            activity = AttachTap(current, ActivityOpcode, null, null, ReadProgress);
            return WitnessesArmed();
        }

        private bool WitnessesArmed()
        { return (natural == null || natural.Armed) && activity != null && activity.Attached; }

        private void BindFormationReply()
        {
            formTap = AttachTap(committed, FormationOpcode, committed.Formation, FormationCanDispatch, FormationReturned);
            if (formTap == null || !formTap.Attached || !CommittedCallbackOwner(false))
                Unknown("divinity_owned_298_unobserved");
        }

        private DailyDivinityReplyTap AttachTap(Frame owner, ushort opcode, object receiver,
            DailyDivinityAdmissionCheck before, Action<byte[]> after, Action<byte[]> preflight = null)
        {
#if JSB_DAILY_DIVINITY_TEST
            if (TestAttach != null) return TestAttach(opcode, before, after, preflight);
#endif
            object client = owner.Client;
            object handler = StockHandler(client, opcode, receiver);
            if (handler == null || (opcode == FormationOpcode && Field<float>(handler, "life") != 10f)) return null;
            if (opcode == ActivityOpcode && !ReferenceEquals(owner.Handler(opcode), handler)) return null;
            return DailyDivinityReplyTap.Attach(client, opcode == ActivityOpcode, handler,
                handler.GetType().GetField("handler", D.Instance), D.Read(handler, "arg"),
                delegate { return Map(D.Read(client, "m_handlers"), 512)[opcode]; }, before, after, preflight);
        }

        private bool FormationCanDispatch()
        {
            if (!CommittedCallbackOwner(false)) return false;
#if JSB_DAILY_DIVINITY_TEST
            if (TestHandler != null) return TestHandler(BattleOpcode) == null;
#endif
            object formation = committed.Formation;
            return Map(D.Read(committed.Client, "m_handlers"), 512)[BattleOpcode] == null &&
                (ReferenceEquals(D.Read(formation, "OnAttack"), committed.OnAttack) || BattleLaunch.OwnsAttack(formation, committed.OnAttack)) &&
                ReferenceEquals(D.Read(formation, "CharFormation"), committed.Form) &&
                Field<bool>(formation, "onfight") && Field<int>(formation, "waitstate") == 2;
        }

        private void FormationReturned(byte[] bytes)
        {
            DevelopmentPacket receipt = DevelopmentPacket.Decode(FormationOpcode, bytes);
            if (receipt.Error != null || receipt.Result != 1u || !CommittedCallbackOwner(false))
                throw new ProbeFailure("divinity_298_result_or_owner");
            // The original callback already ran OnAttack synchronously and
            // registered the stock 1201. Bind under the same client monitor
            // before Run can dispatch another message.
            BindBattleReply();
            if (battleTap == null || !battleTap.Attached)
                throw new ProbeFailure("divinity_stock_1201_unbound");
            if (launch != null) launch.FormationReturned();
        }

        private void BindBattleReply()
        {
            Frame requestFrame = committed; Intent requestOperation = operation;
            DailyDivinityInitialReply validated = null;
            battleTap = AttachTap(requestFrame, BattleOpcode, requestFrame.Panel,
                delegate { return InitialReplyOwner(requestFrame, requestOperation, true); },
                delegate(byte[] body) { BattleReturned(validated, requestFrame, requestOperation); },
                delegate(byte[] body)
                {
                    validated = DailyDivinityInitialReply.Decode(body, requestOperation.Target);
                    initialBinding = validated.Response.HasTarget ? "target-echo" : "opcode-only";
                    initialOutcomeBinding = validated.Response.HasWin ? "outcome-echo" : "report-only";
                });
        }

        private bool InitialReplyOwner(Frame requested, Intent requestedOperation, bool beforeStock)
        {
            if (requested == null || requestedOperation == null || !uiHold || committed != requested || operation != requestedOperation ||
                (stopped ? !stoppedInitialReply : Status != "PENDING") || requestedOperation.Target < 0 || requestedOperation.Target > TaskTarget)
                return false;
#if JSB_DAILY_DIVINITY_TEST
            if (TestCallbackOwner != null) return TestCallbackOwner();
#endif
            uint now, day, cycle;
            object troop = requested.Selected;
            Array troops = D.Read(requested.Character, "god_troops") as Array;
            return ReferenceEquals(D.Static(Model.FirstType("Global"), "character_"), requested.Character) &&
                Field<ulong>(requested.Character, "id") == requested.Role && requested.Transport != null &&
                requested.Transport.Same(DailyTransport.Read(DailyTransport.CurrentClient(requested.Character))) &&
                DailyRefreshGate.ReadClock(requested.Character, out now, out day, out cycle) == "" &&
                now >= requested.Now && cycle == requested.Cycle && Field<uint>(requested.Character, "next_resource_time") == requested.Next &&
                ReferenceEquals(D.Static(Model.FirstType("BaseAsset"), "lds"), requested.Loader) &&
                ReferenceEquals(D.Read(requested.Loader, "currmg"), requested.Scene) &&
                ReferenceEquals(D.Read(requested.Panel, "rmg"), requested.Scene) &&
                ReferenceEquals(troops, requested.Troops) && troops != null && requestedOperation.Target < troops.Length &&
                ReferenceEquals(troops.GetValue(requestedOperation.Target), troop) && Field<byte>(troop, "idtroop") == requestedOperation.Target &&
                ReferenceEquals(D.Read(troop, "data_"), requested.SelectedData) && ReferenceEquals(D.Read(requested.Panel, "m_troop"), troop) &&
                (!beforeStock || !Field<bool>(requested.SelectedData, "killed") && Field<uint>(requested.SelectedData, "cd_time") == requested.SelectedCooldown);
        }

        private void BattleReturned(DailyDivinityInitialReply validated, Frame requestFrame, Intent requestOperation)
        {
            if (validated == null || !InitialReplyOwner(requestFrame, requestOperation, false))
                throw new ProbeFailure("divinity_1201_callback_owner_changed");
            DailyDivinityWire response = validated.Response;
            DailyDivinityBattleAnchor anchor;
#if JSB_DAILY_DIVINITY_TEST
            if (TestBattle != null) anchor = TestBattle();
            else
#endif
            {
                Frame startedBattle = new Frame();
                ReadBattle(startedBattle, (Component)committed.Scene, (Component)committed.Panel,
                    Model.FirstType("RootMessenger"), committed.ChallengeLevel);
                if (!startedBattle.BattleOwned || !Field<bool>(committed.Loader, "isBattling") ||
                    startedBattle.ReportToken == null || startedBattle.ReportId != 0 ||
                    ReferenceEquals(startedBattle.ReportToken, committed.ReportToken) || !startedBattle.WaitEnd ||
                    !startedBattle.UseEnd || startedBattle.Replay ||
                    Field<bool>(committed.SelectedData, "killed") ||
                    Field<uint>(committed.SelectedData, "cd_time") != response.Cooldown)
                    throw new ProbeFailure("divinity_1201_stock_battle_not_bound");
                anchor = new DailyDivinityBattleAnchor(startedBattle.Battle, startedBattle.BattleRoot, startedBattle.ReportToken);
            }
            if (anchor == null || anchor.Battle == null || anchor.Root == null || anchor.Report == null ||
                !BattleReplyPreflight.Matches(validated.Report, anchor.Report))
                throw new ProbeFailure("divinity_1201_stock_battle_not_bound");
            // A stopped helper leaves this already-sent native battle to the
            // game. It never promotes the outcome or authorizes another launch.
            if (stopped) return;
            if (scope == null) throw new ProbeFailure("divinity_1201_scope_missing");
            scope.Bind(anchor);
            resultBattle = anchor.Battle as Component;
            if (launch != null) launch.InitialResponseApplied(resultBattle, anchor.Report);
        }

        private bool CommittedCallbackOwner(bool allowHome)
        {
            if (stopped || Status != "PENDING" || committed == null) return false;
#if JSB_DAILY_DIVINITY_TEST
            if (TestCallbackOwner != null) return TestCallbackOwner();
#endif
            object root = D.Read(committed.Loader, "currmg");
            bool atDivinity = ReferenceEquals(root, committed.Scene);
            uint now, day, cycle;
            return committed.Transport.Same(DailyTransport.Read(DailyTransport.CurrentClient(committed.Character))) &&
                Field<ulong>(committed.Character, "id") == committed.Role &&
                DailyRefreshGate.Read(committed.Character, out now, out day, out cycle) == "" &&
                now >= committed.Now && cycle == committed.Cycle &&
                Field<uint>(committed.Character, "next_resource_time") == committed.Next &&
                ReferenceEquals(D.Static(Model.FirstType("BaseAsset"), "lds"), committed.Loader) &&
                (atDivinity || allowHome && resultKnown && ReferenceEquals(root, origin.Root)) &&
                (resultKnown || ReferenceEquals(D.Read(committed.Panel, "m_troop"), committed.Selected));
        }

        private void ReadProgress(byte[] body)
        {
            DailyTimedWire.Progress value;
            if (!DailyTimedWire.ReadProgress(body, TaskId, out value))
            { progressError = "divinity_355_wire_invalid"; return; }
            if (value == null || committed != null && value.Count == committed.Count) return;
            if (battleTap == null || !CommittedCallbackOwner(true))
            { progressError = "divinity_355_context_or_conflict"; return; }
            progress = value;
        }

        private void ObserveCommitted(Frame f)
        {
            if (f.Error != "") { Unknown("divinity_committed_context_changed"); return; }
            if (resultKnown)
            {
                if (!OwnPanel(f) && !ReferenceEquals(f.Root, origin.Root))
                { Unknown("divinity_return_context_changed"); return; }
                ConfirmProgress(f);
                return;
            }
            if (!OwnPanel(f) || !f.SelectedOwned || !ReferenceEquals(f.Selected, committed.Selected) ||
                !ReferenceEquals(f.SelectedData, committed.SelectedData) || !ReferenceEquals(f.Troops, committed.Troops))
            { Unknown("divinity_committed_target_changed"); return; }
            if (Expired(f.Tick, started, BattleMs) || scope.Battle == null && Expired(f.Tick, started, LoadingMs))
            { Unknown("divinity_stock_response_timeout"); return; }
            if (!TapReturned(f, FormationOpcode, formTap)) return;
            Set("PENDING", "divinity_waiting_stock_1201");
            if (!TapReturned(f, BattleOpcode, battleTap)) return;
            Set("PENDING", "divinity_waiting_natural_1032");

            DailyDivinityBattleAnchor anchor = scope.Battle;
            if (anchor == null || !f.Battling || !f.BattleOwned || !ReferenceEquals(f.Battle, anchor.Battle) ||
                !ReferenceEquals(f.BattleRoot, anchor.Root) || !ReferenceEquals(f.ReportToken, anchor.Report) ||
                (f.BattleVisible && (f.Replay || f.Surrendered)))
            { Unknown("divinity_battle_context_changed"); return; }

            DailyDivinityWire response;
            bool appliedReportlessLoss = false;
            if (natural != null)
            {
                DailyDivinityNaturalEvidence evidence = natural.Read();
                if (evidence == null || !evidence.CallbackReturned || evidence.Body == null) return;
                response = DailyDivinityWire.Decode(EndOpcode, evidence.Body);
            }
            else
            {
                byte[] early = BattleSettlement.AppliedResponse(f.Battle as Component, f.ReportToken);
                bool earlyOwned = BattleSettlement.Owns(f.Battle as Component, f.ReportToken);
                if (earlyOwned)
                {
                    if (early == null) return; // Explicit acceleration rejections remain pending.
                    response = DailyDivinityWire.Decode(EndOpcode, early);
                    byte[] appliedLoss = BattleSettlement.AppliedDivinityLossResponse(f.Battle as Component,
                        f.ReportToken, OwnedBattlePresentation(f.Battle as Component));
                    appliedReportlessLoss = appliedLoss != null && ReferenceEquals(appliedLoss, early) &&
                        response.Result == 0 && response.Type == 5 && !response.Win && response.ReportId == 0;
                }
                else
                {
                    if (endTap == null && f.Handler(EndOpcode) != null)
                        endTap = AttachTap(committed, EndOpcode, anchor.Battle, null,
                            delegate(byte[] body) { endResponse = DailyDivinityWire.Decode(EndOpcode, body); });
                    if (endTap != null && endTap.Error != null) { Unknown(endTap.Error); return; }
                    response = endResponse;
                    if (response == null && f.ReportId != 0 && !f.WaitEnd && !f.UseEnd && !f.Fighting)
                        response = new DailyDivinityWire { Result = 0, Type = 5, Target = (uint)Target,
                            ReportId = f.ReportId, Win = f.SelectedKilled };
                    if (response == null) return;
                }
            }
            int expectedKilled = committed.KilledMask | (response.Win ? 1 << Target : 0);
            // The stock type-5 handler applies the response to its bound
            // m_troop; idtroop is optional and is not used by that handler.
            // The bound battle, selected object and killed-mask transition
            // identify the target even when the server omits that field.
            if (response.Result != 0 || response.Type != 5 ||
                (!appliedReportlessLoss && (response.ReportId == 0 || response.ReportId == committed.ReportId)) ||
                f.ReportId != response.ReportId ||
                f.Fighting || f.WaitEnd || f.UseEnd || f.SelectedKilled != response.Win || f.KilledMask != expectedKilled ||
                (response.Win && f.SelectedCooldown != 0))
            {
                Unknown(String.Format(CultureInfo.InvariantCulture,
                    "divinity_1032_stock_projection_mismatch result={0} type={1} target={2}/{3} report={4}/{5}/{6} fight={7} wait={8} use={9} killed={10}/{11} mask={12}/{13} cooldown={14}",
                    response.Result, response.Type, response.Target, Target, response.ReportId, committed.ReportId,
                    f.ReportId, f.Fighting, f.WaitEnd, f.UseEnd, f.SelectedKilled, response.Win,
                    f.KilledMask, expectedKilled, f.SelectedCooldown));
                return;
            }

            // The server has no operation nonce in either reply. Same-role,
            // same-cycle stock context and a task push corroborate the result.
            resultKnown = true; BattleWon = response.Win; progressStarted = f.Tick;
            settledMask |= 1 << Target;
            if (response.Win)
            {
                Defeated value;
                if (!defeated.TryGetValue(f.Role, out value) || value.Cycle != f.Cycle)
                {
                    value = new Defeated { Cycle = f.Cycle };
                    defeated[f.Role] = value;
                }
                value.Mask |= 1 << Target;
            }
            Advance();
            // Once the daily 1/1 was already recorded there may be no further
            // activity push. The exact battle result and stock killed mask
            // still establish clearance of the current five-target roster.
            if (!response.Win || Target != TaskTarget || committed.Count >= committed.Required)
            {
                // Further wins can refresh an already-complete activity row.
                // The daily counter is independent of the five-target roster;
                // accept a coherent non-regressing row without requiring the
                // same row object or absence of an additional activity push.
                if (f.TaskError != "" || !f.SameTask(committed) || f.Count < committed.Count)
                { Unknown("divinity_unexpected_task_progress"); return; }
                Set(response.Win ? (Target == TaskTarget ? "SUCCEEDED" : "PREREQUISITE_WON") : "LOST",
                    "divinity_stock_result_ready");
                Detach();
                return;
            }
            Set("PENDING", "divinity_won_waiting_same_cycle_355");
            ConfirmProgress(f);
        }

        private bool TapReturned(Frame f, ushort opcode, DailyDivinityReplyTap tap)
        {
            if (tap == null || tap.Error != null ||
                (tap.Returned ? f.Handler(opcode) != null : !tap.Attached || !tap.Owns(f.Handler(opcode))))
            {
                Unknown(tap == null ? "divinity_stock_callback_unbound" : tap.Error ?? "divinity_stock_callback_replaced");
                return false;
            }
            return tap.Returned;
        }

        private void ConfirmProgress(Frame f)
        {
            if (!ActivityHealthy(f))
            { Unknown(progressError ?? "divinity_355_category2_cache_or_callback_changed"); return; }
            if (Expired(f.Tick, progressStarted, ProgressMs))
            { Unknown("divinity_stock_355_timeout"); return; }
            if (progress == null)
            {
                if (!f.SameCounter(committed)) Unknown("divinity_355_return_unverified");
                return;
            }
            if (progress.Count != 1 || progress.Refresh < 0 || progress.Count != f.Count ||
                progress.Refresh != f.Refresh || ReferenceEquals(f.Row, committed.Row))
            { Unknown("divinity_355_category2_cache_mismatch"); return; }
            TaskConfirmed = true;
            Set("SUCCEEDED", "divinity_battle_and_task_verified");
            Detach();
        }

        private bool ActivityHealthy(Frame f)
        {
            return f.TaskError == "" && f.SameTask(committed) && activity != null && activity.Error == null &&
                activity.Attached && ReferenceEquals(f.Handler(ActivityOpcode), committed.Handler(ActivityOpcode)) && progressError == null;
        }

        private void Detach(bool keepPendingBattle = false)
        {
            if (formTap != null) formTap.StopObserving();
            if (battleTap != null) battleTap.StopObserving(keepPendingBattle);
            if (activity != null) activity.StopObserving();
            if (endTap != null) endTap.StopObserving();
            if (!CleanupPending && (formTap != null || battleTap != null || endTap != null || activity != null || natural != null))
            { CleanupPending = true; Advance(); }
            // Removing reply observers does not finish the stock battle UI.
            // Its settlement still verifies this exact scope before opening
            // the result and running the original close/return callbacks.
            if (!resultKnown || !uiHold || stopped || Status == "UNKNOWN") scope = null;
            RetryCleanup();
        }

        // The controller can also call this during teardown. Success means
        // every stopped observer is restored or its exact registration is gone.
        internal bool RetryCleanup()
        {
            if (!CleanupPending) return true;
            if (formTap != null && formTap.TryRestore()) formTap = null;
            if (battleTap != null && battleTap.TryRestore()) battleTap = null;
            if (activity != null && activity.TryRestore()) activity = null;
            if (endTap != null && endTap.TryRestore()) endTap = null;
            if (natural != null)
            {
                try { if (natural.TryDisarmAndRestore()) natural = null; }
                catch (Exception) { } // Restoration remains unverified.
            }
            if (formTap != null || battleTap != null || endTap != null || activity != null || natural != null) return false;
            CleanupPending = false;
            Advance();
            return true;
        }

        private void Unknown(string why)
        {
            Set("UNKNOWN", why);
            Detach();
        }
        private void Set(string status, string reason)
        {
            if (Status == status && Reason == reason) return;
            Status = status; Reason = reason; Advance();
        }
        private void Advance() { Revision = checked(Revision + 1); }
        private static bool Expired(int now, int start, int bound) { return unchecked((uint)(now - start)) >= (uint)bound; }
        private Frame Read(D context)
        {
            Frame f = new Frame { Context = context, Tick = Environment.TickCount };
            try
            {
                if (context == null || context.Paused || context.Character == null || context.GlobalUi == null ||
                    context.UiRoot == null || context.Root == null) throw new ProbeFailure("divinity_context_unavailable");
                f.Character = context.Character;
                f.Client = DailyTransport.CurrentClient(f.Character);
                f.Role = Field<ulong>(f.Character, "id");
                if (!ReferenceEquals(context.Client, f.Client) || f.Role == 0 ||
                    f.Role.ToString(CultureInfo.InvariantCulture) != context.CharacterId || Field<bool>(f.Character, "gm"))
                    throw new ProbeFailure("divinity_role_or_client");
                f.Transport = DailyTransport.Read(f.Client);
                f.Root = context.Root;
                f.GlobalRoot = context.GlobalUi;
                Component loader = D.Static(Model.FirstType("BaseAsset"), "lds") as Component;
                f.Loader = loader;
                Type rootType = Model.FirstType("RootMessenger");
                if (f.Transport == null || loader == null || loader.GetType() != Model.FirstType("LoadScreen") ||
                    loader.gameObject != GameObject.Find("/LoadUI") || context.Root.GetType() != rootType ||
                    context.Root.transform.root != context.Root.transform || context.UiRoot.GetType() != rootType ||
                    !context.UiRoot.gameObject.activeInHierarchy || context.GlobalUi.GetType() != Model.GameType("RootUI") ||
                    !ReferenceEquals(f.Root, D.Read(f.Loader, "currmg")) ||
                    !Owns(context.GlobalUi, context.UiRoot) ||
                    context.GlobalUi.gameObject != GameObject.Find("/Root/RootUI"))
                    throw new ProbeFailure("divinity_loader_or_transport");
                object cachedRoot = D.Static(Model.GameType("BaseUI"), "rootUI");
                if (cachedRoot != null && cachedRoot != f.GlobalRoot) throw new ProbeFailure("divinity_cached_root");

                uint day;
                string refresh = DailyRefreshGate.Read(f.Character, out f.Now, out day, out f.Cycle);
                f.Next = Field<uint>(f.Character, "next_resource_time");
                if (refresh != "") throw new ProbeFailure(refresh);
                try { ReadTask(f); }
                catch (Exception error) { f.TaskError = error is ProbeFailure ? error.Message : "divinity_task_read_failed"; }
                ReadTroops(f);
                ReadUi(f);
                ReadNetwork(f);
            }
            catch (Exception error)
            { f.Error = error is ProbeFailure ? error.Message : "divinity_observation_failed"; }
            return f;
        }

        private static void ReadTask(Frame f)
        {
            IDictionary rows = Map(D.Read(f.Character, "acts"), 255);
            IDictionary rewards = Map(D.Read(f.Character, "act_rewards"), 6);
            object row = rows[(uint)TaskId], reward = rewards[(uint)Category];
            if (row == null || row.GetType() != Model.FirstType("Database.Activity") ||
                reward == null || reward.GetType() != Model.FirstType("Database.Activity_Reward") ||
                Field<byte>(reward, "idact_reward") != Category)
                throw new ProbeFailure("divinity_category2_cache_unavailable");
            f.Task = Field<byte>(row, "idactivity");
            f.TaskTemplate = D.Read(row, "temp");
            f.RewardTemplate = D.Read(reward, "temp");
            if (f.Task != TaskId || f.TaskTemplate == null || f.RewardTemplate == null ||
                !ReferenceEquals(f.TaskTemplate, D.Template("activity", TaskId)) ||
                !ReferenceEquals(f.RewardTemplate, D.Template("activity_reward", Category)) ||
                Field<byte>(f.TaskTemplate, "id") != TaskId || Field<byte>(f.TaskTemplate, "id_uibutton") != 20 ||
                Field<byte>(f.RewardTemplate, "id") != Category || !Field<bool>(f.RewardTemplate, "active") ||
                !Field<bool>(f.TaskTemplate, "active") || Field<string>(f.TaskTemplate, "note") != TaskNote)
                throw new ProbeFailure("divinity_activity_template");
            f.TaskCategory = Field<byte>(f.TaskTemplate, "idreward");
            f.Required = Field<int>(f.TaskTemplate, "cond_param");
            f.Count = Field<byte>(row, "condition_param");
            f.Refresh = Field<int>(row, "refresh_time");
            IDictionary listed = Map(D.Read(reward, "acts"), 255);
            f.Rows = rows; f.Row = row; f.Reward = reward; f.Listed = listed; f.ListedRow = listed[(uint)TaskId];
            if (f.ListedRow == null || !ReferenceEquals(D.Read(f.ListedRow, "temp"), f.TaskTemplate))
                throw new ProbeFailure("divinity_category2_cache_unavailable");
            f.ListedCount = Field<byte>(f.ListedRow, "condition_param");
            f.ListedRefresh = Field<int>(f.ListedRow, "refresh_time");
            if (!f.SameTask(f) || f.Count > f.Required) throw new ProbeFailure("divinity_category2_cache_mismatch");
        }

        private void ReadTroops(Frame f)
        {
            Array troops = D.Read(f.Character, "god_troops") as Array;
            Type troopType = Model.FirstType("Database.GodTroop");
            if (troops == null || troops.GetType().GetElementType() != troopType || troops.Length != TaskTarget + 1)
                throw new ProbeFailure("divinity_roster_schema");
            f.Troops = troops;
            f.ChallengeLevel = Field<byte>(f.Character, "god_challenge_level");
            for (int id = 0; id <= TaskTarget; id++)
            {
                object troop = troops.GetValue(id);
                if (troop == null || troop.GetType() != troopType || Field<ushort>(troop, "idcampaign") != 83 ||
                    Field<byte>(troop, "idtroop") != id ||
                    (Field<string>(troop, "name") == TargetName) != (id == TaskTarget) ||
                    (Field<uint>(troop, "id") == TargetTemplate) != (id == TaskTarget))
                    throw new ProbeFailure("divinity_troop_template");
                object data = D.Read(troop, "data_");
                if (Field<byte>(data, "idtroop") != id) throw new ProbeFailure("divinity_troop_data");
                bool killed = Field<bool>(data, "killed");
                uint cooldown = Field<uint>(data, "cd_time");
                if (killed) f.KilledMask |= 1 << id;
                else if (f.Target < 0) { f.Target = id; f.Cooldown = cooldown; }
                int selectedId = Status == "PENDING" ? Target : f.Target;
                if (id != selectedId) continue;
                f.Selected = troop; f.SelectedData = data; f.SelectedKilled = killed; f.SelectedCooldown = cooldown;
            }
            f.SelectedIdentity = f.Selected != null;
        }

        private void ReadUi(Frame f)
        {
            D context = f.Context;
            Type rootType = Model.FirstType("RootMessenger"), panelType = Model.GameType("DivinityUI"), formType = Model.GameType("FormationUI");
            f.Battling = Field<bool>(f.Loader, "isBattling");
            // BattleUI.Show deactivates its previous root. Keep the observed
            // exact Divinity components through this normal transition.
            bool inactivePrevious = committed != null && (resultKnown || f.Battling && ReferenceEquals(f.Root, committed.Scene));
            Component scene = inactivePrevious ? committed.Scene as Component : Fixed("/Divinity", rootType);
            Component panel = inactivePrevious ? committed.Panel as Component : Fixed("/Divinity/DivinityUI", panelType);
            if (scene == null || panel == null || Field<string>(scene, "LevelID") != "Divinity" || !Owns(panel, scene))
                throw new ProbeFailure("divinity_stock_scene_identity");
            Component formation = panel.GetComponent("FormationUI") as Component;
            if (formation == null || formation.GetType() != formType || !Owns(formation, scene) ||
                panel.GetComponents(panelType).Length != 1 || panel.GetComponents(formType).Length != 1)
                throw new ProbeFailure("divinity_stock_formation_identity");

            f.Scene = scene; f.Panel = panel; f.Formation = formation;
            f.SelectedOwned = f.Selected != null && ReferenceEquals(D.Read(panel, "m_troop"), f.Selected);
            ReadBattle(f, scene, panel, rootType, committed == null ? f.ChallengeLevel : committed.ChallengeLevel);

            Component root = context.Root;
            object sub = context.SubUi;
            string level = Field<string>(root, "LevelID");
            bool home = level == "Capital" || level == "Campaign";
            bool atDivinity = root == scene;
            if ((!home && !atDivinity) || f.Battling || f.BattleVisible || !root.gameObject.activeInHierarchy ||
                ((Behaviour)f.Loader).enabled || Field<bool>(root, "bLoading") || Field<int>(root, "ExpectingCount") != 0 ||
                !ReferenceEquals(D.Read(context.GlobalUi, "level_rmg"), root) ||
                !D.Bool(D.Static(Model.FirstType("Global"), "extern_enabled")) ||
                D.Bool(D.Static(Model.GameType("BuildingBuildUI"), "m_bBuildVisible")) ||
                D.Bool(D.Static(Model.GameType("BuildingBehaviourScript"), "m_bEditVisible")) ||
                D.Bool(D.Static(Model.FirstType("PopupUI"), "popup"))) return;

            if (home)
            {
                if (context.Gate != "PASS" || sub != null || D.Visible(panel) || D.Visible(formation)) return;
                Component main = null, campaign = null;
                foreach (UnityEngine.Object item in context.All)
                {
                    Behaviour ui = item as Behaviour;
                    if (ui == null || ui == context.GlobalUi || !D.Visible(ui)) continue;
                    Type type = ui.GetType();
                    if (type == Model.GameType("MainUI"))
                    {
                        if (main != null || !Owns(ui, root)) return;
                        main = ui;
                    }
                    else if (type == Model.GameType("CampaignUI"))
                    {
                        if (level != "Campaign" || campaign != null || !Owns(ui, root)) return;
                        campaign = ui;
                    }
                    else if (context.Overlay(ui) != null) return;
                }
                if (level == "Campaign" && (!Usable(campaign) ||
                    Field<int>(campaign, "m_campaignID") != Field<ushort>(f.Character, "idcampaign"))) return;
                f.HomeMain = main;
                f.Home = Usable(main) && Field<bool>(main, "m_bInit") &&
                    (bool)D.Method(f.Character.GetType(), "checkUI", new[] { typeof(string) }, false, typeof(bool))
                        .Invoke(f.Character, new object[] { "resource_collect" }) && DivinityAssets(f);
                return;
            }

            if (context.Gate != "development_campaign_or_capital_root_required" && context.Gate != "controller_gate:not_current_root" &&
                context.Gate != "controller_gate:main_not_unique" && context.Gate != "PASS") return;
            // Stock DivinityClearUI confirms with Hide + EnableAll, leaving
            // MainUI.m_subui pointing at the disabled component. Recognize
            // that exact owned, closed dialog without changing the stock slot.
            Component closedClear = sub as Component;
            Type clearType = Model.GameType("DivinityClearUI");
            if (resultKnown && uiHold && atDivinity && closedClear != null &&
                closedClear.GetType() == clearType && closedClear.gameObject == panel.gameObject &&
                panel.GetComponents(clearType).Length == 1 && Owns(closedClear, scene) &&
                closedClear.gameObject.activeInHierarchy && !((Behaviour)closedClear).enabled &&
                !D.Loading(closedClear))
                sub = null;
            if (D.Read(panel, "OnClose") != null || !D.Visible(panel) || D.Loading(panel) ||
                (sub != null && !ReferenceEquals(sub, formation))) return;
            Component sceneMain = null;
            foreach (UnityEngine.Object item in context.All)
            {
                Behaviour ui = item as Behaviour;
                if (ui == null || ui == context.GlobalUi || ui == panel || ui == formation || !D.Visible(ui)) continue;
                Type type = ui.GetType();
                // Divinity has its own stock MainUI. It is scene chrome,
                // with the same exact ownership checks as the home MainUI.
                if (type == Model.GameType("MainUI"))
                {
                    if (sceneMain != null || !Owns(ui, scene) || !Usable(ui) || !Field<bool>(ui, "m_bInit")) return;
                    sceneMain = ui;
                    continue;
                }
                if (type == Model.GameType("DivinityTroopScript"))
                {
                    object troop = D.Read(ui, "Troop");
                    if (ReferenceEquals(D.Read(ui, "ParentUI"), panel) && troop != null &&
                        Array.IndexOf((Array)f.Troops, troop) >= 0 && (GameObject)D.Read(troop, "mesh") == ui.gameObject &&
                        ui.transform.root == scene.transform) continue;
                    return;
                }
                if (type == Model.GameType("DivinityStoneScript") &&
                    ui.gameObject == (GameObject)D.Static(panelType, "stone_mesh") && ui.transform.root == scene.transform) continue;
                if (context.Overlay(ui) != null) return;
            }

            GameObject mesh = D.Static(panelType, "mesh") as GameObject;
            if (sceneMain == null || mesh == null || !mesh.activeInHierarchy || mesh.transform.parent != panel.transform || Camera.main == null) return;
            if (sub == null)
            {
                f.PanelReady = Usable(panel) && !D.Visible(formation);
                return;
            }
            f.OnAttack = D.Read(formation, "OnAttack");
            f.Form = D.Read(formation, "CharFormation");
            if (f.Form != null) f.FormId = Field<int>(f.Form, "formid");
            Delegate attack = f.OnAttack as Delegate;
            f.FormReady = f.SelectedOwned && Usable(formation) && f.FormId > 0 && f.Form != null &&
                attack != null && attack.GetInvocationList().Length == 1 && ReferenceEquals(attack.Target, panel) &&
                attack.Method == D.Method(panelType, "OnAttack", Type.EmptyTypes, false, typeof(bool)) &&
                Field<string>(formation, "DefaultState") == "NPCBattle" && Field<string>(formation, "RightPanel") == "Enemy" &&
                Field<string>(formation, "substate") == "divinity_troop" && Field<long>(formation, "Campaignid") == f.Target &&
                Field<int>(formation, "Troopid") == 0 && Field<int>(formation, "waitstate") == 0 &&
                Field<int>(formation, "drag_gen") == -1 && !Field<bool>(formation, "moving") &&
                !Field<bool>(formation, "onfight") && ValidCurrentFormation(f);
        }

        private static bool ValidCurrentFormation(Frame f)
        {
            if (Field<int>(f.Character, "cur_form") != f.FormId || !Field<bool>(f.Form, "available")) return false;
            IDictionary existing = D.Read(f.Character, "formations_") as IDictionary;
            IList spots = D.Read(f.Form, "dbspot") as IList;
            Array cells = D.Read(f.Form, "formgenpos") as Array;
            object persisted = existing == null ? null : existing[f.FormId];
            IList generals = persisted == null ? null : D.Read(persisted, "spotgen") as IList;
            if (spots == null || generals == null || spots.Count < 1 || spots.Count > 16 || spots.Count != generals.Count ||
                cells == null || cells.Rank != 2 || cells.GetLength(0) != 4 || cells.GetLength(1) != 4) return false;
            int seen = 0;
            for (int i = 0; i < spots.Count; i++)
            {
                if (!(spots[i] is int) || !(generals[i] is int)) return false;
                int spot = (int)spots[i];
                if (spot < 0 || spot > 15 || (seen & (1 << spot)) != 0 ||
                    Field<long>(cells.GetValue(spot / 4, spot % 4), "genid") != (int)generals[i]) return false;
                seen |= 1 << spot;
            }
            return true;
        }

        private static bool DivinityAssets(Frame f)
        {
            if (f.ChallengeLevel < 5 || D.Template("god_yield", (uint)(f.ChallengeLevel - 5)) == null) return false;
            foreach (object troop in (Array)f.Troops)
            {
                object animation = D.Template("troop_animation", Field<uint>(troop, "idmodel"));
                if (animation == null || String.IsNullOrEmpty(Field<string>(animation, "name"))) return false;
            }
            return true;
        }

        private static void ReadBattle(Frame f, Component scene, Component panel, Type rootType, int originalLevel)
        {
            Component battle = Fixed("/Battle/BattleUI", Model.GameType("BattleUI"));
            if (battle == null) return;
            f.Battle = battle;
            f.BattleVisible = D.Visible(battle);
            f.ReportId = Field<ulong>(battle, "m_reportid");
            object interpreter = D.Read(battle, "interpreter");
            f.ReportToken = interpreter == null ? null : D.Read(interpreter, "m_report");
            Component root = Fixed("/Battle", rootType);
            // The installed /Battle RootMessenger has an empty LevelID.
            // Bind the fixed scene and stock caller pointers, not its label.
            f.BattleOwned = root != null && Owns(battle, root) &&
                ReferenceEquals(D.Read(battle, "m_prevUI"), scene) && ReferenceEquals(D.Read(battle, "m_parentUI"), panel);
            f.BattleRoot = root;
            f.Fighting = Field<bool>(battle, "bBattle_fighting");
            f.WaitEnd = Field<bool>(battle, "bNeedWaitForBattleEnd");
            f.UseEnd = Field<bool>(battle, "bUseBattleEnd");
            f.Surrendered = Field<bool>(battle, "bBattle_surrended");
            f.Replay = Field<bool>(battle, "m_is_cross") || Field<bool>(battle, "m_show_defender_result") ||
                D.Read(battle, "m_viewType").ToString() != (originalLevel >= 200 ? "viewDefault" : "viewFull");
        }

        private bool StillCurrent(Frame f, string action)
        {
            if (!ReferenceEquals(f.Client, DailyTransport.CurrentClient(f.Character)) || !f.Transport.StillCurrent() ||
                Field<ulong>(f.Character, "id") != f.Role || Field<uint>(f.Character, "next_resource_time") != f.Next ||
                (uint)D.Static(Model.FirstType("TimeUtil"), "_now") >= f.Next ||
                D.Bool(D.Static(Model.GameType("RootUI"), "m_bRefreshFive")) ||
                Field<uint>(f.SelectedData, "cd_time") != f.Cooldown ||
                Field<bool>(f.SelectedData, "killed") ||
                !ReferenceEquals(f.Loader, D.Static(Model.FirstType("BaseAsset"), "lds")) ||
                !ReferenceEquals(f.Root, D.Read(f.Loader, "currmg")) || Field<bool>(f.Loader, "isBattling") ||
                !ReferenceEquals(D.Read(f.GlobalRoot, "level_rmg"), f.Root) ||
                !D.Bool(D.Static(Model.FirstType("Global"), "extern_enabled")) ||
                !ReferenceEquals(f.Context.SubUi, action == "start" ? f.Formation : null) ||
                ((IList)D.Read(f.Client, "messages")).Count != 0 || ((IList)D.Read(f.Client, "m_sendDatas")).Count != 0 ||
                !ReferenceEquals(f.Handler(ActivityOpcode), action == "start" ?
                    Map(D.Read(f.Client, "m_handlers"), 512)[ActivityOpcode] : StockHandler(f.Client, ActivityOpcode, null))) return false;
            if (action == "start")
                return ReferenceEquals(D.Read(f.Panel, "m_troop"), f.Selected) &&
                    ReferenceEquals(D.Read(f.Formation, "OnAttack"), f.OnAttack) &&
                    ReferenceEquals(D.Read(f.Formation, "CharFormation"), f.Form) &&
                    !Field<bool>(f.Formation, "onfight") && Field<int>(f.Formation, "waitstate") == 0;
            return true;
        }

        private static void BindCall(Frame f, string action, out MethodInfo method, out object receiver, out object[] args, out FieldInfo fight)
        {
            bool open = action == "open", start = action == "start";
            receiver = open ? null : start ? f.Formation : f.Panel;
            Type type = open ? Model.GameType("MainUI") : receiver.GetType();
            string methodName = open ? "_resource_collect" : start ? "OnClickSetCurrentForm" : "wantCombat";
            bool select = !open && !start;
            args = select ? new[] { f.Selected } : null;
            method = D.Method(type, methodName, select ? new[] { f.Selected.GetType() } : Type.EmptyTypes, open, typeof(void));
            fight = start ? type.GetField("onfight", D.Instance) : null;
            if (method.DeclaringType != type || (start ? !method.IsPrivate : !method.IsPublic) ||
                start && (fight == null || fight.FieldType != typeof(bool) || !fight.IsPrivate))
                throw new ProbeFailure("divinity_stock_invocation_schema");
        }

        private void ReadNetwork(Frame f)
        {
            IDictionary handlers = Map(D.Read(f.Client, "m_handlers"), 512);
            IList messages = D.Read(f.Client, "messages") as IList, sends = D.Read(f.Client, "m_sendDatas") as IList;
            if (messages == null || sends == null || messages.Count > 64 || sends.Count > 64)
                throw new ProbeFailure("divinity_network_bound");
            f.NetworkIdle = messages.Count == 0 && sends.Count == 0 && !DevelopmentCallbackCapture.AnyCallbackExecuting;
            Type handlerType = Model.FirstType("game.net.tcp_base+Handler");
            foreach (DictionaryEntry entry in handlers)
            {
                if (!(entry.Key is ushort) || entry.Value == null || entry.Value.GetType() != handlerType)
                    throw new ProbeFailure("divinity_handler_schema");
                float life = Field<float>(entry.Value, "life");
                if (Single.IsNaN(life) || Single.IsInfinity(life)) throw new ProbeFailure("divinity_handler_lifetime");
                if (life > 0) f.NetworkIdle = false;
                ushort op = (ushort)entry.Key;
                if (op == ActivityOpcode)
                {
                    if ((activity == null || !activity.Owns(entry.Value) || !activity.Attached) &&
                        StockHandler(f.Client, op, null) == null) f.TaskError = "divinity_stock_355_identity";
                    else f.Handlers.Add(op, entry.Value);
                }
                else if (op == FormationOpcode || op == BattleOpcode || op == EndOpcode) f.Handlers.Add(op, entry.Value);
            }
        }

        private static object StockHandler(object client, ushort opcode, object owner)
        {
            object handler = Map(D.Read(client, "m_handlers"), 512)[opcode];
            if (handler == null || handler.GetType() != Model.FirstType("game.net.tcp_base+Handler")) return null;
            Delegate callback = D.Read(handler, "handler") as Delegate;
            if (opcode == ActivityOpcode)
                return DevelopmentCallbackCapture.IsDailyActivityPush(handler, callback) ? handler : null;
            object arg = D.Read(handler, "arg");
            if (owner == null || callback == null || callback.Target != owner || callback.GetInvocationList().Length != 1 ||
                Field<float>(handler, "life") <= 0 || (opcode == FormationOpcode ? !(arg is bool) || !(bool)arg : arg != null)) return null;
            if (opcode != FormationOpcode && opcode != BattleOpcode && opcode != EndOpcode) return null;
            string name = opcode == FormationOpcode ? "sc_formupdate" : opcode == BattleOpcode ? "sc_god_battle_0x4B1" : "sc_battle_end_0x408";
            Type type = owner.GetType();
            MethodInfo method = D.Method(type, name, new[] { opcode == EndOpcode ? typeof(Stream) : typeof(MemoryStream), typeof(object) }, false, typeof(void));
            return method.IsPrivate && method.DeclaringType == type && method == callback.Method ? handler : null;
        }

        private static bool IsCleanup(string action)
        {
            return action == "battle_close" || action == "reward_confirm" || action == "clear_confirm" ||
                action == "failed_close" || action == "return";
        }

        private Component StockComponent(string path, string type)
        {
            GameObject game = GameObject.Find(path);
            return game == null ? null : game.GetComponent(type) as Component;
        }

        private Component RewardDialog()
        { return D.Static(Model.FirstType("__BaseUI"), "battle_result_ui") as Component; }

        private bool TargetClosed() { return TargetClosed(frame); }

        private bool TargetClosed(Frame f)
        {
            if (!resultKnown || CleanupPending || f == null || f.Error != "" || f.TaskError != "" ||
                !f.NetworkIdle || f.Battling || f.BattleVisible ||
                (Status != "SUCCEEDED" && Status != "PREREQUISITE_WON" && Status != "LOST")) return false;
            Component result = StockComponent("/Battle/BattleUI", "BattleNormalResultUI");
            Component reward = RewardDialog();
            Component clear = StockComponent("/Divinity/DivinityUI", "DivinityClearUI");
            Component failed = StockComponent("/Root/RootUI", "BattleValueUI");
            foreach (Component dialog in new[] { result, reward, clear, failed })
                if (dialog != null && (D.Visible(dialog) || D.Loading(dialog))) return false;
            return f.SameOwner(origin) && (OwnPanel(f) && f.PanelReady || ReturnedHome(f));
        }

        private bool CanContinue(Frame f)
        {
            return TargetClosed(f) && uiHold && BattleWon == true && OwnPanel(f) && f.PanelReady &&
                committed != null && f.SameTask(committed) && f.Count >= committed.Count &&
                ReferenceEquals(f.Troops, committed.Troops) && f.ChallengeLevel == committed.ChallengeLevel &&
                f.KilledMask == (committed.KilledMask | (1 << Target)) &&
                f.Target >= 0 && f.Target <= TaskTarget && f.SelectedIdentity && !f.SelectedKilled &&
                (attemptedMask & (1 << f.Target)) == 0 && f.Cooldown <= f.Now;
        }

        private bool CleanupAvailable(string action)
        {
            // A caller may explicitly finish a confirmed page visit instead
            // of selecting its next target. The ordinary stock return is the
            // only extra admission; this does not relax any other UI gate.
            return !stopped && Status != "UNKNOWN" && (action == NextAction() || action == "return" && uiHold &&
                TargetClosed() && OwnPanel(frame) && frame.PanelReady);
        }

        private string NextAction()
        {
            if (stopped || frame == null || Status == "UNKNOWN") return "";
            // Opening a stock scene does not commit a battle. A failed open
            // can use its normal return only after the exact panel is ready.
            if (Status == "BLOCKED" && uiHold && !MutationAttempted && origin != null &&
                frame.Error == "" && frame.TaskError == "" && frame.NetworkIdle &&
                frame.SameCounter(targetOrigin) && frame.SameTarget(targetOrigin) && OwnPanel(frame) && frame.PanelReady)
                return "return";
            if (resultKnown && uiHold)
            {
                Component result = StockComponent("/Battle/BattleUI", "BattleNormalResultUI");
                if (ReadyToUse(result)) return "battle_close";
                Component reward = RewardDialog();
                if (ReadyToUse(reward)) return "reward_confirm";
                if (reward != null && (D.Visible(reward) || D.Loading(reward))) return "";
                Component clear = StockComponent("/Divinity/DivinityUI", "DivinityClearUI");
                if (ReadyToUse(clear)) return "clear_confirm";
                if (clear != null && (D.Visible(clear) || D.Loading(clear))) return "";
                Component failed = StockComponent("/Root/RootUI", "BattleValueUI");
                if (ReadyToUse(failed)) return "failed_close";
                if (TargetClosed() && OwnPanel(frame) && frame.PanelReady)
                    return CanContinue(frame) ? "select" : "return";
                return "";
            }
            if (Status == "BLOCKED") return "";
            if (Status == "OPEN") return "select";
            if (Status == "READY") return "start";
            if (!uiHold && frame.Home && frame.Target >= 0 && frame.Cooldown <= frame.Now &&
                Gate(frame, "open", frame.Target) == "") return "open";
            return "";
        }

        private string AvailabilityGate()
        {
            if (frame == null) return "divinity_unobserved";
            if (frame.Error != "") return frame.Error;
            if (frame.TaskError != "") return frame.TaskError;
            if (!uiHold && resultKnown && BattleWon == false) return "divinity_target_already_lost";
            if (!uiHold && frame.Target >= 0 && frame.Cooldown > frame.Now) return "divinity_target_cooldown";
            if (!uiHold && !frame.Home) return "divinity_stock_home_required";
            return "";
        }

        private static bool ReadyToUse(Component ui)
        { return ui != null && D.Visible(ui) && D.Enabled(ui) && !D.Loading(ui); }

        private bool ExecuteCleanup(Intent intent, DailyDivinityContextProvider fresh, DevelopmentPublication publish, DailyDivinityAdmission admit, out string why)
        {
            why = "divinity_cleanup_changed";
            if (!D.Gui || !CleanupAvailable(intent.Action)) return false;
            publish();
            if (!admit(delegate
            {
                frame = Read(fresh());
                return !stopped && frame.Role == intent.Baseline.Role && frame.Cycle == intent.Baseline.Cycle &&
                    CleanupAvailable(intent.Action);
            }, delegate { return !stopped && D.Gui; }))
                return false;
            queued = null;
            if (intent.Action == "battle_close")
            {
                Component result = StockComponent("/Battle/BattleUI", "BattleNormalResultUI");
                Component battle = resultBattle ?? StockComponent("/Battle/BattleUI", "BattleUI");
                D.Call(result, "Hide");
                D.Call(battle, "battle_exit");
                resultBattle = null;
            }
            else if (intent.Action == "reward_confirm")
            {
                Component reward = RewardDialog();
                Delegate close = D.Read(reward, "OnClose") as Delegate;
                D.Call(reward, "Hide");
                if (close != null)
                    ((DevelopmentPublication)Delegate.CreateDelegate(typeof(DevelopmentPublication), close.Target, close.Method))();
            }
            else if (intent.Action == "clear_confirm")
            {
                Component clear = StockComponent("/Divinity/DivinityUI", "DivinityClearUI");
                D.Call(clear, "Hide");
                D.Call(clear, "EnableAll");
            }
            else if (intent.Action == "failed_close")
            {
                D.Call(StockComponent("/Root/RootUI", "BattleValueUI"), "Hide");
                D.Method(Model.GameType("MainUI"), "SetSubUI", new[] { Model.FirstType("__BaseUI") }, true, typeof(void))
                    .Invoke(null, new object[] { null });
            }
            else D.Call(frame.Panel, "_return");
            why = ""; Advance(); return true;
        }

        private bool HarvestClearanceVerified()
        {
            Frame current = frame;
            bool locked = false;
            try
            {
                // A cached model frame is not proof for a later observation.
                // Harvest must follow all five stock killed flags and the
                // normal return, never merely the task-8 checklist counter.
                if (stopped || current == null || capturedFrame != Time.frameCount || Busy ||
                    current.Error != "" || current.TaskError != "" || current.Role == 0 || current.Cycle == 0 ||
                    current.KilledMask != FullRosterMask || !current.Home || !current.NetworkIdle || current.Transport == null)
                    return false;
                locked = Monitor.TryEnter(current.Client, 0);
                if (!locked || !ReferenceEquals(D.Static(Model.FirstType("Global"), "character_"), current.Character) ||
                    Field<ulong>(current.Character, "id") != current.Role ||
                    !ReferenceEquals(DailyTransport.CurrentClient(current.Character), current.Client) || !current.Transport.StillCurrent())
                    return false;
                uint now, day, cycle;
                if (DailyRefreshGate.ReadClock(current.Character, out now, out day, out cycle) != "" || cycle != current.Cycle ||
                    Field<uint>(current.Character, "next_resource_time") != current.Next || now >= current.Next ||
                    !ReferenceEquals(D.Static(Model.FirstType("BaseAsset"), "lds"), current.Loader) ||
                    !ReferenceEquals(D.Read(current.Loader, "currmg"), current.Root) ||
                    !ReferenceEquals(D.Read(current.GlobalRoot, "level_rmg"), current.Root) ||
                    !((Component)current.Root).gameObject.activeInHierarchy || Field<bool>(current.Loader, "isBattling") ||
                    ((Behaviour)current.Loader).enabled || current.Context.SubUi != null)
                    return false;
                Array troops = D.Read(current.Character, "god_troops") as Array;
                if (troops == null || troops.Length != 5 || !ReferenceEquals(troops, current.Troops)) return false;
                for (int id = 0; id < 5; id++)
                {
                    object troop = troops.GetValue(id);
                    object data = troop == null ? null : D.Read(troop, "data_");
                    if (troop == null || data == null || Field<byte>(troop, "idtroop") != id ||
                        Field<byte>(data, "idtroop") != id || !Field<bool>(data, "killed")) return false;
                }
                return ReferenceEquals(frame, current) && capturedFrame == Time.frameCount;
            }
            catch (Exception) { return false; }
            finally { if (locked) Monitor.Exit(current.Client); }
        }

        internal string ViewJson()
        {
            return "{\"task_id\":8,\"role_id\":" + D.Q(frame == null ? "" : frame.Role.ToString(CultureInfo.InvariantCulture)) +
                ",\"refresh_cycle\":" + (frame == null ? 0 : frame.Cycle) + ",\"current\":" + (frame == null ? -1 : frame.Count) +
                ",\"required\":1,\"next_action\":" + D.Q(queued == null ? NextAction() : "") +
                ",\"gate\":" + D.Q(AvailabilityGate()) +
                ",\"target\":" + CandidateTarget + ",\"cooldown_until\":" + (frame == null ? 0 : frame.Cooldown) +
                ",\"continuous_interface\":\"stock-owned-divinity-session-v1\"" +
                ",\"session_open\":" + D.B(uiHold) +
                ",\"killed_mask\":" + (frame == null ? -1 : frame.KilledMask) + ",\"target_total\":5" +
                ",\"clearance_complete\":" + D.B(frame != null && frame.Error == "" &&
                    frame.KilledMask == FullRosterMask && frame.NetworkIdle && !CleanupPending) +
                ",\"harvest_clearance_interface\":\"stock-five-god-harvest-clearance-v1\"" +
                ",\"harvest_clearance_verified\":" + D.B(HarvestClearanceVerified()) +
                ",\"retry_unstarted\":" + D.B(frame != null && capturedFrame == Time.frameCount && !stopped &&
                    frame.Error == "" && frame.TaskError == "" && lastSequence == 0 && queued == null && operation == null && origin == null &&
                    committed == null && !Busy && !CleanupPending) + "}";
        }

        internal string OperationJson()
        {
            return "{\"id\":" + D.Q(operation == null ? "" : operation.Operation) + ",\"sequence\":" + lastSequence +
                ",\"status\":" + D.Q(Status ?? "IDLE") + ",\"reason\":" + D.Q(Reason ?? "") +
                ",\"mutation_attempted\":" + D.B(MutationAttempted) + ",\"closed\":" + D.B(!uiHold && origin != null) +
                ",\"session_closed\":" + D.B(!uiHold && origin != null) +
                ",\"target_closed\":" + D.B(targetClosed || TargetClosed()) +
                ",\"target\":" + Target + ",\"attempted_mask\":" + attemptedMask + ",\"settled_mask\":" + settledMask +
                ",\"session_mutation_attempted\":" + D.B(attemptedMask != 0) +
                ",\"report_id\":" + D.Q(frame == null ? "0" : frame.ReportId.ToString(CultureInfo.InvariantCulture)) +
                ",\"target_progress_before\":" + (committed == null ? -1 : committed.Count) +
                ",\"target_killed_mask_before\":" + (committed == null ? -1 : committed.KilledMask) +
                ",\"won\":" + (BattleWon.HasValue ? D.B(BattleWon.Value) : "null") +
                ",\"account_progress_correlated\":" + D.B(TaskConfirmed) +
                ",\"stock_callback_returned\":" + D.B(resultKnown) +
                ",\"initial_response_binding\":" + D.Q(initialBinding) + ",\"initial_outcome_binding\":" + D.Q(initialOutcomeBinding) +
                ",\"progress_before\":" + (origin == null ? -1 : origin.Count) +
                ",\"progress_after\":" + (frame == null ? -1 : frame.Count) + "}";
        }

        internal string Json()
        { return "{\"schema\":1,\"revision\":" + Revision + ",\"view\":" + ViewJson() + ",\"operation\":" + OperationJson() + "}"; }

        private static bool Usable(Component ui) { return D.Enabled(ui) && !D.Loading(ui) && !Field<bool>(ui, "first"); }
        private static Component Fixed(string path, Type type)
        {
            GameObject gameObject = GameObject.Find(path);
            if (gameObject == null) return null;
            Component[] matches = gameObject.GetComponents(type);
            if (matches.Length != 1 || matches[0] == null || matches[0].GetType() != type)
                throw new ProbeFailure("divinity_fixed_ui_not_unique");
            return matches[0];
        }
        private static bool Owns(Component component, Component root)
        {
            return component != null && root != null && root.transform.root == root.transform &&
                component.transform.root == root.transform && ReferenceEquals(D.Read(component, "rmg"), root);
        }
        private static IDictionary Map(object value, int max)
        {
            IDictionary map = value as IDictionary;
            if (map == null || map.Count > max) throw new ProbeFailure("divinity_map_schema");
            return map;
        }
        private static T Field<T>(object value, string name) { return (T)D.Read(value, name); }
    }

    // Fixed stock 298/1201/355 delegates only. tcp_base.Run holds the
    // client monitor through original callback dispatch. A replacement that
    // handles the packet itself cannot create a Returned witness.
    internal sealed class DailyDivinityReplyTap
    {
        private readonly object client, handler, argument;
        private readonly FieldInfo callback;
        private readonly bool persistent;
        private readonly DailyDivinityLiveHandler liveHandler;
        private readonly DailyDivinityAdmissionCheck before;
        private readonly Action<byte[]> after;
        private readonly Action<byte[]> preflight;
        private readonly Delegate original, wrapper;
        private readonly DevelopmentNormalCallback forward;
        private readonly int thread;
        private volatile bool armed = true;
        private volatile bool drainStopped;
        private int calls, executing, stockCalls;
        internal bool Returned { get; private set; }
        internal string Error { get; private set; }

        private DailyDivinityReplyTap(object client, bool persistent, object handler, FieldInfo callback,
            object argument, DailyDivinityLiveHandler liveHandler, DailyDivinityAdmissionCheck before, Action<byte[]> after, Action<byte[]> preflight, Delegate original)
        {
            this.client = client; this.persistent = persistent; this.handler = handler; this.callback = callback;
            this.argument = argument; this.liveHandler = liveHandler; this.before = before; this.after = after; this.preflight = preflight; this.original = original;
            thread = Thread.CurrentThread.ManagedThreadId;
            forward = (DevelopmentNormalCallback)(original.Method.IsStatic ?
                Delegate.CreateDelegate(typeof(DevelopmentNormalCallback), original.Method) :
                Delegate.CreateDelegate(typeof(DevelopmentNormalCallback), original.Target, original.Method));
            wrapper = Delegate.CreateDelegate(original.GetType(), this,
                typeof(DailyDivinityReplyTap).GetMethod("Dispatch", BindingFlags.NonPublic | BindingFlags.Instance));
        }

        internal static DailyDivinityReplyTap Attach(object client, bool persistent, object handler, FieldInfo callback,
            object argument, DailyDivinityLiveHandler liveHandler, DailyDivinityAdmissionCheck before, Action<byte[]> after, Action<byte[]> preflight = null)
        {
            if (client == null || handler == null || callback == null || !Monitor.TryEnter(client, 0)) return null;
            try
            {
                if (!ReferenceEquals(liveHandler(), handler)) return null;
                Delegate original = callback.GetValue(handler) as Delegate;
                if (original == null || original.GetInvocationList().Length != 1) return null;
                DailyDivinityReplyTap tap = new DailyDivinityReplyTap(client, persistent, handler, callback, argument, liveHandler, before, after, preflight, original);
                callback.SetValue(handler, tap.wrapper);
                return tap;
            }
            finally { Monitor.Exit(client); }
        }

        internal bool Owns(object slot) { return ReferenceEquals(handler, slot); }

        internal bool Attached
        {
            get
            {
                return armed && ReferenceEquals(liveHandler(), handler) &&
                    ReferenceEquals(callback.GetValue(handler), wrapper);
            }
        }

        private void Dispatch(MemoryStream stream, object arg)
        {
            Interlocked.Increment(ref executing);
            try { DispatchCore(stream, arg); }
            finally { Interlocked.Decrement(ref executing); }
        }

        private void DispatchCore(MemoryStream stream, object arg)
        {
            if (preflight != null) { DispatchBattle(stream, arg); return; }
            if (!persistent && Interlocked.Increment(ref stockCalls) != 1)
            { Error = "divinity_stock_callback_duplicate"; return; }
            if (!armed) { forward(stream, arg); return; }
            byte[] body = null;
            bool locked = false;
            try
            {
                locked = Monitor.TryEnter(client, 0);
                if (!locked || Thread.CurrentThread.ManagedThreadId != thread || !ReferenceEquals(arg, argument) ||
                    (persistent ? !Attached : liveHandler() != null) || (before != null && !before()))
                    Error = "divinity_stock_callback_dispatch_context";
                else if (++calls > (persistent ? 32 : 1)) Error = "divinity_stock_callback_duplicate_or_bound";
                else if (stream == null) Error = "divinity_stock_callback_null";
                else if (stream.GetType() != typeof(MemoryStream) || !stream.CanRead || stream.Position != 0 ||
                    stream.Length < 1 || stream.Length > (persistent ? 65536 : 1048576))
                    Error = "divinity_stock_callback_stream";
                else body = stream.ToArray();
            }
            catch (Exception) { Error = "divinity_stock_callback_copy_failed"; }

            try
            {
                try { forward(stream, arg); }
                catch
                {
                    if (armed) Error = "divinity_stock_callback_threw";
                    throw;
                }
                if (armed && Error == null && body != null)
                {
                    try
                    {
                        after(body);
                        Returned = true;
                    }
                    catch (Exception error)
                    { Error = error is ProbeFailure ? error.Message : "divinity_stock_post_callback_observation_failed"; }
                }
            }
            finally { if (locked) Monitor.Exit(client); }
        }

        private void DispatchBattle(MemoryStream stream, object arg)
        {
            if (Interlocked.Increment(ref stockCalls) != 1)
            { if (Error == null) Error = "divinity_stock_callback_duplicate"; return; }
            if (Error != null || !armed && !drainStopped) return;
            bool locked = false;
            try
            {
                locked = Monitor.TryEnter(client, 0);
                if (!locked || !BattleBoundary(arg)) throw new ProbeFailure("divinity_stock_callback_dispatch_context");
                if (stream == null) throw new ProbeFailure("divinity_stock_callback_null");
                if (stream.GetType() != typeof(MemoryStream) || !stream.CanRead || stream.Position != 0 ||
                    stream.Length < 1 || stream.Length > BattleReplyPreflight.MaxPacketBytes)
                    throw new ProbeFailure("divinity_stock_callback_stream");
                byte[] body = stream.ToArray(); preflight(body);
                if (Error != null || !armed && !drainStopped || !BattleBoundary(arg) || stream.Position != 0 ||
                    stream.Length != body.Length || !SameBytes(body, stream.ToArray()))
                    throw new ProbeFailure("divinity_stock_callback_owner_changed_during_decode");
                forward(stream, arg);
                after(body); Returned = true;
            }
            catch (Exception error)
            { Error = error is ProbeFailure ? error.Message : "divinity_initial_reply_unverified"; }
            finally { if (locked) Monitor.Exit(client); }
        }

        private bool BattleBoundary(object arg)
        {
            object current = liveHandler();
            return Thread.CurrentThread.ManagedThreadId == thread && ReferenceEquals(arg, argument) &&
                ReferenceEquals(D.Read(handler, "arg"), argument) && ReferenceEquals(callback.GetValue(handler), wrapper) &&
                (current == null || ReferenceEquals(current, handler)) && before != null && before();
        }

        private static bool SameBytes(byte[] left, byte[] right)
        {
            if (left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
            return true;
        }

        internal void StopObserving(bool keepPendingBattle = false)
        { drainStopped = keepPendingBattle && preflight != null && Error == null; armed = false; }

        internal bool TryRestore()
        {
            if (armed || !Monitor.TryEnter(client, 0)) return false;
            try
            {
                // Run normally holds the same monitor, but teardown can be
                // called reentrantly while an original callback is running.
                if (Interlocked.CompareExchange(ref executing, 0, 0) != 0) return false;
                // A 1201 guard stays as the client's one-shot handler even
                // after helper teardown. Restoring stock would bypass its
                // immutable owner/schema/report checks for a late reply.
                if (preflight != null) return true;
                if (!ReferenceEquals(liveHandler(), handler) || !ReferenceEquals(callback.GetValue(handler), wrapper)) return true;
                callback.SetValue(handler, original);
                return ReferenceEquals(callback.GetValue(handler), original);
            }
            catch (Exception) { return false; } // A retained wrapper forwards the original unconditionally.
            finally { Monitor.Exit(client); }
        }
    }

    // Detached original-wire proof. Optional echoes are compared only when
    // present; absence relies on the exact one-shot native request owner.
    internal sealed class DailyDivinityInitialReply
    {
        internal DailyDivinityWire Response;
        internal object Report;
        internal static DailyDivinityInitialReply Decode(byte[] body, int target)
        {
            BattleReplyPreflight.Deserialize(body, "game.net.data.StoC.SCGodBattle_0x4B1");
            DailyDivinityWire response = DailyDivinityWire.Decode(DailyDivinityBattleModel.BattleOpcode, body);
            if (response.Result != 0 || target < 0 || target > DailyDivinityBattleModel.TaskTarget ||
                response.HasTarget && response.Target != (uint)target || !response.HasReport)
                throw new ProbeFailure("divinity_1201_rejected_or_wrong_target");
            object report = response.HasWin ? BattleReplyPreflight.Validate(response.Report, response.Win) :
                BattleReplyPreflight.Validate(response.Report);
            return new DailyDivinityInitialReply { Response = response, Report = report };
        }
    }

    internal sealed class DailyDivinityWire
    {
        internal uint Result, Type, Target, Cooldown;
        internal bool HasReport, Win, HasTarget, HasWin;
        internal byte[] Report;
        internal ulong ReportId;

        internal static DailyDivinityWire Decode(ushort opcode, byte[] body)
        {
            bool start = opcode == DailyDivinityBattleModel.BattleOpcode;
            if ((!start && opcode != DailyDivinityBattleModel.EndOpcode) || body == null || body.Length < 2 ||
                body.Length > (start ? 1048576 : 65536)) throw new FormatException();
            DailyDivinityWire packet = new DailyDivinityWire();
            int pos = 0, seen = 0;
            while (pos < body.Length)
            {
                ulong tag = Varint(body, ref pos);
                if (tag < 8 || tag > UInt32.MaxValue) throw new FormatException();
                int field = (int)(tag >> 3), wire = (int)(tag & 7);
                bool report = start && field == 4;
                bool scalar = field == 1 || field == 2 || (start ? field == 3 || field == 7 : field == 5 || field == 6 || field == 17);
                if (report || scalar)
                {
                    if ((seen & (1 << field)) != 0 || wire != (report ? 2 : 0)) throw new FormatException();
                    seen |= 1 << field;
                }
                if (wire == 0)
                {
                    ulong value = Varint(body, ref pos);
                    if (scalar && field != 17 && value > UInt32.MaxValue) throw new FormatException();
                    if (field == 1) packet.Result = (uint)value;
                    else if (field == 2) { if (start) { packet.Target = (uint)value; packet.HasTarget = true; } else packet.Type = (uint)value; }
                    else if (start && field == 3)
                    {
                        if (value > 1) throw new FormatException();
                        packet.Win = value == 1; packet.HasWin = true;
                    }
                    else if (start && field == 7) packet.Cooldown = (uint)value;
                    else if (!start && field == 5) packet.Target = (uint)value;
                    else if (!start && field == 6)
                    {
                        if (value > 1) throw new FormatException();
                        packet.Win = value == 1;
                    }
                    else if (!start && field == 17) packet.ReportId = value;
                }
                else
                {
                    ulong count;
                    if (wire == 1) count = 8;
                    else if (wire == 2) count = Varint(body, ref pos);
                    else if (wire == 5) count = 4;
                    else throw new FormatException();
                    if (count > (ulong)(body.Length - pos)) throw new FormatException();
                    if (report)
                    {
                        packet.HasReport = count > 0; packet.Report = new byte[(int)count];
                        Buffer.BlockCopy(body, pos, packet.Report, 0, (int)count);
                    }
                    pos += (int)count;
                }
            }
            if ((seen & 2) == 0) throw new FormatException();
            return packet;
        }

        private static ulong Varint(byte[] body, ref int pos)
        {
            ulong value = 0;
            for (int bit = 0; bit <= 63 && pos < body.Length; bit += 7)
            {
                byte next = body[pos++];
                if (bit == 63 && next > 1) break;
                value |= ((ulong)next & 127) << bit;
                if ((next & 128) == 0)
                {
                    if (bit > 0 && next == 0) break;
                    return value;
                }
            }
            throw new FormatException();
        }
    }
}
