using System;
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Threading;
using UnityEngine;
using D = JiangShen.AssistantRuntime.SharedDevelopmentContext;

namespace JiangShen.AssistantRuntime
{
    internal sealed class DailyGraveModel
    {
        internal const int TaskId = 17;
        private sealed class Frame
        {
            internal object Role, Client, Loader, Sub, Grave, Campaign, Target;
            internal Component Root, Choose, Panel, Formation, Battle, NormalResult, Reward, Message, Consume;
            internal ulong RoleId;
            internal uint Cycle;
            internal int Current, Required, Stamina, Times, Soldiers, GraveId, TargetId, Finish;
            internal bool Loading, NetworkBusy, Home, Scene, CanEnter, BossVisible, NeedsAttempt, DailyFinished, BossBlocked, OpenGrave;
            internal string Error = "";
        }

        private sealed class Intent
        {
            internal string Action, Id;
            internal ulong RoleId;
            internal int Tick;
        }

        private Frame frame;
        private Intent queued;
        private object owner, selected;
        private Component battleSource, battlePrevious;
        private ulong roleId;
        private uint cycle;
        private int sequence, selectedId, stageTick;
        private string operationId = "", nextAction = "", terminalReason = "", signature;
        private bool holding, returning, stopped, resumedExistingRun, entryRequestAttempted, battleRequestAttempted, battleReturned, checklistComplete;
        private bool battleSelectionStarted;
        internal int Revision { get; private set; }
        internal string Status { get; private set; }
        internal string Reason { get; private set; }
        internal bool MutationAttempted { get; private set; }
        internal bool GuiInvocationAttempted { get; private set; }
        internal bool TaskConfirmed { get; private set; }
        internal bool CleanupPending { get { return holding; } }
        internal bool Busy { get { return queued != null || holding; } }
        internal bool Unresolved { get { return holding || Status == "UNKNOWN"; } }

        internal BattlePresentationOwnership OwnedBattlePresentation(Component battle)
        {
            if (stopped) return null;
            object target = selected, role = owner;
            Component source = battleSource, previous = battlePrevious;
            string operation = operationId;
            int targetId = selectedId;
            ulong expectedRole = roleId;
            uint expectedCycle = cycle;
            BattleSettlementAdmission verify = delegate
            {
                try
                {
                    uint now, day, currentCycle;
                    return holding && Status != "UNKNOWN" && battleSelectionStarted &&
                        operation != "" && operationId == operation && ReferenceEquals(owner, role) &&
                        roleId == expectedRole && expectedRole != 0 && cycle == expectedCycle &&
                        ReferenceEquals(selected, target) && selectedId == targetId && targetId > 0 &&
                        ReferenceEquals(battleSource, source) && ReferenceEquals(battlePrevious, previous) &&
                        battle != null && source != null && previous != null && target != null &&
                        ReferenceEquals(D.Static(Model.FirstType("Global"), "character_"), role) &&
                        Convert.ToUInt64(D.Read(role, "id"), CultureInfo.InvariantCulture) == expectedRole &&
                        DailyRefreshGate.ReadClock(role, out now, out day, out currentCycle) == "" && currentCycle == expectedCycle &&
                        D.Int(D.Read(target, "idtroop")) == targetId && ReferenceEquals(D.Read(source, "m_troop"), target) &&
                        ReferenceEquals(D.Read(source, "rmg"), previous) &&
                        ReferenceEquals(D.Read(battle, "m_parentUI"), source) &&
                        ReferenceEquals(D.Read(battle, "m_prevUI"), previous);
                }
                catch (Exception) { return false; }
            };
            return verify() ? new BattlePresentationOwnership(BattleMode.Grave, operation, source, target,
                targetId.ToString(CultureInfo.InvariantCulture), verify) : null;
        }

        internal DailyGraveModel() { Status = "IDLE"; Reason = "not_observed"; Revision = 1; }

        internal void Capture(D context)
        {
            frame = Read(context);
            nextAction = "";
            if (stopped) { Set("BLOCKED", "stopped"); return; }
            if (frame.Error != "") { Set(holding ? "PENDING" : "BLOCKED", frame.Error); return; }
            if (holding && (!ReferenceEquals(frame.Role, owner) || frame.RoleId != roleId || frame.Cycle != cycle))
            { Set("UNKNOWN", "grave_role_or_daily_cycle_changed"); return; }
            TaskConfirmed = frame.Current >= frame.Required;
            if (!holding)
            {
                if (checklistComplete && ReferenceEquals(frame.Role, owner) && frame.Cycle == cycle)
                    Set("COMPLETED", terminalReason);
                else if (CanResume(frame)) Offer("READY", "resume");
                else if (frame.Home) Offer("IDLE", "open");
                else Set("BLOCKED", "main_screen_required");
                return;
            }
            if (frame.Consume != null && D.Visible(frame.Consume))
            { Set("BLOCKED", "premium_confirmation_visible"); return; }
            if (frame.Loading || frame.NetworkBusy)
            { Set("PENDING", frame.Loading ? "stock_loading" : "stock_reply_pending"); return; }
            if (frame.Message != null && D.Visible(frame.Message))
            { Offer("MESSAGE", "dismiss"); return; }
            if (frame.Reward != null && D.Visible(frame.Reward))
            { Offer("REWARD", "reward_close"); return; }
            if (frame.NormalResult != null && D.Visible(frame.NormalResult))
            { Offer("RESULT", "battle_exit"); return; }
            if (frame.Battle != null && D.Visible(frame.Battle))
            { Set("PENDING", "stock_battle_playing"); return; }
            if (returning && frame.Home)
            {
                // GraveLevelUI._return queues MainUI._grave. Give the original
                // queued callback time to reopen its selection window.
                if (unchecked((uint)(Environment.TickCount - stageTick)) < 500)
                { Set("CLOSING", "stock_returning"); return; }
                holding = false;
                Set(checklistComplete ? "COMPLETED" : "BLOCKED", terminalReason);
                return;
            }
            if (battleReturned && frame.Scene && frame.Finish < selectedId)
            {
                terminalReason = "grave_battle_lost";
                returning = true;
            }
            if (returning)
            {
                if (frame.Scene || frame.Sub != null) Offer("CLOSING", "close");
                else
                {
                    holding = false;
                    Set(checklistComplete ? "COMPLETED" : "BLOCKED", terminalReason);
                }
                return;
            }
            if (frame.Formation != null && ReferenceEquals(frame.Sub, frame.Formation) && D.Enabled(frame.Formation))
            {
                if (D.Loading(frame.Formation) || D.Read(frame.Formation, "CharFormation") == null ||
                    D.Int(D.Read(frame.Formation, "waitstate")) != 0)
                    Set("PENDING", "formation_loading");
                else Offer("FORMATION", "start");
                return;
            }
            if (frame.Choose != null && ReferenceEquals(frame.Sub, frame.Choose) && D.Enabled(frame.Choose))
            {
                if (frame.CanEnter) Offer("OPEN", "enter");
                else
                {
                    checklistComplete = !frame.OpenGrave || frame.DailyFinished && frame.BossBlocked ||
                        frame.Times <= 0 && frame.NeedsAttempt;
                    terminalReason = !frame.OpenGrave ? "grave_no_open_grave" : frame.DailyFinished && frame.BossBlocked ?
                        "grave_finished_today" : frame.Times <= 0 ? "grave_no_remaining_attempts" : "grave_not_available_today";
                    Offer("CLOSING", "close");
                }
                return;
            }
            if (frame.Scene && frame.Panel != null && D.Enabled(frame.Panel))
            {
                if (frame.Target != null && frame.Soldiers > 0) Offer("READY", "select");
                else
                {
                    checklistComplete = frame.Finish >= (frame.BossVisible ? 6 : 5) && frame.DailyFinished;
                    terminalReason = checklistComplete ? "grave_finished_today" : frame.Soldiers <= 0 ?
                        "grave_soldiers_exhausted" : "grave_target_unavailable";
                    Offer("CLOSING", "close");
                }
                return;
            }
            Set(unchecked((uint)(Environment.TickCount - stageTick)) > 45000 ? "UNKNOWN" : "PENDING", "stock_transition");
        }

        private void Offer(string status, string action) { nextAction = action; Set(status, ""); }
        private void Set(string status, string reason)
        {
            Status = status; Reason = reason;
            string current = JsonBody();
            if (signature != current) { signature = current; Revision++; }
        }

        internal bool Queue(int task, ulong role, string id, int nextSequence, int revision, string action, out string why)
        {
            why = "grave_command_unavailable";
            if (task != TaskId || stopped || frame == null || frame.Error != "" || queued != null || role == 0 ||
                role != frame.RoleId || nextSequence <= sequence || revision != Revision || action == "" ||
                action != nextAction || !Command.ValidNonce(id) ||
                (action == "open" || action == "resume" ? Busy : !holding || id != operationId)) return false;
            queued = new Intent { Action = action, Id = id, RoleId = role, Tick = Environment.TickCount };
            sequence = nextSequence; GuiInvocationAttempted = false; Revision++;
            why = "";
            return true;
        }

        internal bool ExecuteGui(DailyBattleContextProvider fresh, DevelopmentPublication publish, DailyBattleAdmission admit, out string why)
        {
            why = "grave_not_queued";
            Intent intent = queued;
            if (intent == null) return false;
            Frame current = null;
            DevelopmentPublication stock = null;
            bool committed = false;
            object client = frame.Client;
            if (client == null || !Monitor.TryEnter(client, 0))
            { NotInvoked("client_busy"); why = "client_busy"; return false; }
            try
            {
                publish();
                if (!admit(delegate
                {
                    if (queued != intent || stopped || !D.Gui || unchecked((uint)(Environment.TickCount - intent.Tick)) > 1500) return false;
                    Capture(fresh()); current = frame;
                    if (current.Error != "" || current.RoleId != intent.RoleId || nextAction != intent.Action) return false;
                    stock = Bind(current, intent.Action);
                    return stock != null;
                }, delegate
                {
                    committed = stock != null && queued == intent && !stopped && D.Gui &&
                        ReferenceEquals(current.Role, D.Static(Model.FirstType("Global"), "character_")) &&
                        ReferenceEquals(current.Client, D.Static(Model.GameType("RootUI"), "m_client"));
                    return committed;
                }) || !committed)
                { why = "grave_gui_admission_changed"; NotInvoked(why); return false; }
                queued = null; stageTick = Environment.TickCount;
                if (intent.Action == "open" || intent.Action == "resume")
                {
                    holding = true; returning = MutationAttempted = false; terminalReason = "";
                    checklistComplete = battleReturned = false;
                    owner = current.Role; roleId = current.RoleId; cycle = current.Cycle; operationId = intent.Id;
                    resumedExistingRun = intent.Action == "resume";
                    entryRequestAttempted = battleRequestAttempted = false;
                    battleSource = battlePrevious = null; battleSelectionStarted = false;
                }
                if (intent.Action == "select")
                {
                    selected = current.Target; selectedId = current.TargetId; battleReturned = false;
                    battleSource = current.Panel; battlePrevious = current.Root; battleSelectionStarted = false;
                }
                if (intent.Action == "battle_exit") battleReturned = true;
                if (intent.Action == "start" || intent.Action == "enter") MutationAttempted = true;
                if (intent.Action == "enter") entryRequestAttempted = true;
                if (intent.Action == "start") battleRequestAttempted = battleSelectionStarted = true;
                if (intent.Action == "close") returning = true;
                GuiInvocationAttempted = intent.Action != "resume";
                stock();
                nextAction = ""; Set(returning ? "CLOSING" : "PENDING",
                    resumedExistingRun && intent.Action == "resume" ? "existing_grave_resumed" : "stock_action_invoked");
                why = "";
                return true;
            }
            catch (Exception error)
            {
                queued = null;
                Set(GuiInvocationAttempted ? "UNKNOWN" : "BLOCKED", error is TargetInvocationException && error.InnerException != null ?
                    error.InnerException.Message : error.Message);
                why = Reason;
                return false;
            }
            finally { Monitor.Exit(client); }
        }

        private DevelopmentPublication Bind(Frame f, string action)
        {
            // Adopt the already entered run. Do not call _grave, startGrave or
            // cs_grave_load_campaign (833): those are entry, not continuation.
            if (action == "resume") return CanResume(f) ? (DevelopmentPublication)delegate { } : null;
            if (action == "open") return delegate { D.MainBranch("_grave"); };
            if (action == "enter")
            {
                if (!f.CanEnter) return null;
                return delegate { D.Call(f.Choose, "DisableAll"); D.Call(f.Choose, "cs_grave_load_campaign"); };
            }
            if (action == "select")
            {
                MethodInfo select = D.Method(f.Panel.GetType(), "wantCombat", new[] { f.Target.GetType() }, false, typeof(void));
                return delegate { select.Invoke(f.Panel, new[] { f.Target }); };
            }
            if (action == "start")
            {
                if (selected == null || f.Formation == null || !ReferenceEquals(D.Read(f.Panel, "m_troop"), selected)) return null;
                object target = selected;
                return delegate
                {
                    BattleLaunch.Formation(BattleMode.Grave, f.Formation, f.Panel, target, delegate
                    {
                        return !stopped && holding && ReferenceEquals(owner, f.Role) && ReferenceEquals(selected, target) &&
                            ReferenceEquals(D.Read(f.Panel, "m_troop"), target) && ReferenceEquals(D.Read(f.Panel, "rmg"), f.Root);
                    }, null);
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
                MethodInfo dismiss = D.Method(f.Message.GetType(), "ButtonOK", new[] { typeof(bool) }, false, typeof(void));
                return delegate { dismiss.Invoke(f.Message, new object[] { false }); };
            }
            if (action == "close")
            {
                MethodInfo close = D.Method(Model.GameType("MainUI"), "SetSubUI", new[] { Model.FirstType("__BaseUI") }, true, typeof(void));
                return delegate
                {
                    if (f.Sub != null) close.Invoke(null, new object[] { null });
                    if (f.Scene) D.Call(f.Panel, "_return");
                };
            }
            return null;
        }

        private Frame Read(D context)
        {
            Frame f = new Frame();
            if (context == null) { f.Error = "context_unavailable"; return f; }
            try
            {
                if (context.Paused) { f.Error = "paused"; return f; }
                f.Role = D.Static(Model.FirstType("Global"), "character_");
                f.Client = D.Static(Model.GameType("RootUI"), "m_client");
                if (f.Role == null || f.Client == null) { f.Error = "character_or_connection_unavailable"; return f; }
                f.RoleId = Convert.ToUInt64(D.Read(f.Role, "id"), CultureInfo.InvariantCulture);
                f.Stamina = D.Int(D.Read(f.Role, "stamina"));
                f.Loader = D.Static(Model.FirstType("BaseAsset"), "lds");
                f.Root = D.Read(f.Loader, "currmg") as Component;
                f.Sub = D.Static(Model.GameType("MainUI"), "m_subui");
                uint now = (uint)D.Static(Model.FirstType("TimeUtil"), "_now"), day = (uint)D.Static(Model.FirstType("TimeUtil"), "_thisDay");
                f.Cycle = now >= day + 18000L ? day + 18000u : day - 68400u;
                f.Loading = ((Behaviour)f.Loader).enabled;
                string level = f.Root == null ? "" : D.Read(f.Root, "LevelID") as string;
                f.Home = !f.Loading && (level == "Campaign" || level == "Capital") && f.Sub == null;
                f.Panel = Ui(context, "GraveLevelUI");
                f.Choose = Ui(context, "GraveChooseUI", true);
                f.Formation = f.Panel == null ? null : f.Panel.GetComponent(Model.GameType("FormationUI"));
                f.Battle = Ui(context, "BattleUI"); f.NormalResult = Ui(context, "BattleNormalResultUI");
                f.Reward = Ui(context, "BattleResultUI", true); f.Message = Ui(context, "MessageBox", true); f.Consume = Ui(context, "ConsumeUI", true);
                IDictionary acts = D.Read(f.Role, "acts") as IDictionary;
                object row = acts == null ? null : acts[(uint)TaskId];
                if (row == null) { f.Error = "daily_task_unavailable"; return f; }
                f.Current = D.Int(D.Read(row, "condition_param"));
                f.Required = D.Int(D.Read(D.Read(row, "temp"), "cond_param"));
                if (f.Required < 1) { f.Error = "daily_task_disabled"; return f; }
                if ((bool)D.Method(row.GetType(), "_check_act_closecondition", Type.EmptyTypes, false, typeof(bool)).Invoke(row, null))
                    f.Current = f.Required;
                f.Grave = D.Read(f.Role, "grave"); f.Campaign = D.Read(f.Role, "grave_campaign");
                f.Times = D.Int(D.Read(f.Grave, "times"));
                f.DailyFinished = D.Int(D.Read(f.Grave, "finishtoday")) == 1;
                f.OpenGrave = HasOpenGrave(f.Role);
                if (f.Campaign != null)
                {
                    f.GraveId = D.Int(D.Read(f.Campaign, "idgrave")); f.Soldiers = D.Int(D.Read(f.Campaign, "soldier"));
                    // The installed /Grave RootMessenger is labelled Divinity.
                    // RootUI.startGrave instead binds these exact scene objects.
                    GameObject scene = GameObject.Find("/Grave"), panel = GameObject.Find("/Grave/GraveUI");
                    f.Scene = f.Root != null && scene != null && panel != null && f.Panel != null &&
                        f.Root.gameObject == scene && f.Root.GetType() == Model.FirstType("RootMessenger") &&
                        f.Panel.gameObject == panel && f.Panel.transform.root == f.Root.transform &&
                        ReferenceEquals(D.Read(f.Panel, "rmg"), f.Root) && ReferenceEquals(context.Root, f.Root) &&
                        context.GlobalUi != null && ReferenceEquals(D.Read(context.GlobalUi, "level_rmg"), f.Root) &&
                        f.GraveId > 0 && D.Int(D.Read(f.Panel, "_graveid")) == f.GraveId;
                    int finished = D.Int(D.Read(f.Campaign, "finish"));
                    bool boss = (bool)D.Read(f.Campaign, "showboss");
                    f.Finish = finished; f.BossVisible = boss;
                    Array troops = D.Read(f.Campaign, "troops") as Array;
                    foreach (object troop in troops ?? new object[0])
                    {
                        if (troop == null) continue;
                        int id = D.Int(D.Read(troop, "idtroop"));
                        if (!(bool)D.Read(troop, "killed") && id == finished + 1 && (id <= 5 || boss))
                        { f.Target = troop; f.TargetId = id; break; }
                    }
                }
                if (f.Choose != null && f.Campaign != null)
                {
                    bool needsAttempt = (bool)D.Method(f.Choose.GetType(), "checkGraveFinish", Type.EmptyTypes, false, typeof(bool)).Invoke(f.Choose, null);
                    bool bossBlocked = (bool)D.Method(f.Choose.GetType(), "checkGraveBoss", Type.EmptyTypes, false, typeof(bool)).Invoke(f.Choose, null);
                    f.NeedsAttempt = needsAttempt; f.BossBlocked = bossBlocked;
                    f.CanEnter = (f.Times > 0 || !needsAttempt) && (!f.DailyFinished || !bossBlocked) && f.OpenGrave;
                }
                IDictionary handlers = D.Read(f.Client, "m_handlers") as IDictionary;
                if (handlers != null)
                    foreach (DictionaryEntry handler in handlers)
                        if ((float)D.Read(handler.Value, "life") > 0f) { f.NetworkBusy = true; break; }
                if (f.Panel != null && D.Loading(f.Panel) || f.Choose != null && D.Loading(f.Choose) ||
                    f.Reward != null && D.Loading(f.Reward)) f.Loading = true;
            }
            catch (Exception error) { f.Error = error.Message; }
            return f;
        }

        private static bool CanResume(Frame f)
        {
            return f.Scene && f.Sub == null && f.Target != null && f.Soldiers > 0 &&
                !f.Loading && !f.NetworkBusy && D.Enabled(f.Panel) &&
                (f.Consume == null || !D.Visible(f.Consume)) && (f.Message == null || !D.Visible(f.Message)) &&
                (f.Reward == null || !D.Visible(f.Reward)) && (f.NormalResult == null || !D.Visible(f.NormalResult)) &&
                (f.Battle == null || !D.Visible(f.Battle));
        }

        private static bool HasOpenGrave(object role)
        {
            IDictionary graves = D.Read(role, "graves") as IDictionary;
            int level = D.Int(D.Read(role, "level"));
            if (graves == null) return false;
            foreach (DictionaryEntry entry in graves)
            {
                object grave = entry.Value;
                if (!(bool)D.Read(grave, "isopen") || (bool)D.Read(grave, "isfinish")) continue;
                object template = D.Template("grave", Convert.ToUInt32(D.Read(grave, "idgrave"), CultureInfo.InvariantCulture));
                if (template != null && D.Int(D.Read(template, "min_level")) <= level) return true;
            }
            return false;
        }

        private static Component Ui(D context, string name, bool first = false)
        {
            Type type = first ? Model.FirstType(name) : Model.GameType(name);
            Component found = null;
            foreach (UnityEngine.Object item in context.All)
            {
                Component candidate = item as Component;
                if (candidate != null && candidate.GetType() == type &&
                    (found == null || candidate.gameObject.activeInHierarchy && ((Behaviour)candidate).enabled)) found = candidate;
            }
            return found;
        }

        internal void NotInvoked(string reason)
        { queued = null; GuiInvocationAttempted = false; Reason = reason; Revision++; }
        internal void Stop() { Stop("stopped"); }
        internal void Stop(string reason)
        { stopped = true; queued = null; nextAction = ""; Set("BLOCKED", reason); }
        private string JsonBody()
        {
            return "\"task_id\":17,\"role_id\":" + D.Q(frame == null ? "0" : frame.RoleId.ToString(CultureInfo.InvariantCulture)) +
                ",\"operation_id\":" + D.Q(operationId) + ",\"status\":" + D.Q(Status) + ",\"reason\":" + D.Q(Reason) +
                ",\"next_action\":" + D.Q(nextAction) + ",\"activity_count\":" + (frame == null || frame.Error != "" ? "null" : frame.Current.ToString(CultureInfo.InvariantCulture)) +
                ",\"required\":" + (frame == null || frame.Error != "" ? "null" : frame.Required.ToString(CultureInfo.InvariantCulture)) +
                ",\"refresh_cycle\":" + (frame == null ? 0u : frame.Cycle) + ",\"grave_id\":" + (frame == null ? 0 : frame.GraveId) +
                ",\"target_id\":" + (selectedId == 0 ? frame == null ? 0 : frame.TargetId : selectedId) +
                ",\"remaining_attempts\":" + (frame == null ? 0 : frame.Times) + ",\"soldiers\":" + (frame == null ? 0 : frame.Soldiers) +
                ",\"grave_finish\":" + (frame == null ? 0 : frame.Finish) + ",\"boss_visible\":" + D.B(frame != null && frame.BossVisible) +
                ",\"checklist_complete\":" + D.B(checklistComplete && !holding) + ",\"stop_reason\":" + D.Q(terminalReason) +
                ",\"stamina\":" + (frame == null ? 0 : frame.Stamina) + ",\"mutation_attempted\":" + D.B(MutationAttempted) +
                ",\"resumed_existing_run\":" + D.B(resumedExistingRun) +
                ",\"entry_request_attempted\":" + D.B(entryRequestAttempted) +
                ",\"battle_request_attempted\":" + D.B(battleRequestAttempted) +
                ",\"task_confirmed\":" + D.B(TaskConfirmed) + ",\"cleanup_pending\":" + D.B(CleanupPending) + ",\"busy\":" + D.B(Busy);
        }
        internal string Json() { return "{\"schema\":1,\"revision\":" + Revision + "," + JsonBody() + "}"; }
    }
}
