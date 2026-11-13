using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using UnityEngine;
using D = JiangShen.AssistantRuntime.SharedDevelopmentContext;

namespace JiangShen.AssistantRuntime
{
    internal delegate D DailyArenaContextProvider();
    internal delegate bool DailyArenaInvocationDeadline();
    internal delegate bool DailyArenaInvocationGate(DailyArenaInvocationDeadline withinDeadline);

    internal sealed class DailyArenaChestReceipt
    {
        internal const ushort Opcode = 1122;
        internal uint Result, Time, Gold, Money, Reputation;

        // Pinned SCArenaBonusGet_0x462. Optional scalar fields use the
        // protobuf zero default. Only a success for the exact time can settle.
        internal static DailyArenaChestReceipt Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > 128) throw new FormatException("arena_receipt_bound");
            int position = 0, seen = 0;
            DailyArenaChestReceipt receipt = new DailyArenaChestReceipt();
            while (position < bytes.Length)
            {
                uint tag = Varint(bytes, ref position), field = tag >> 3;
                if ((tag & 7) != 0 || field == 0 || field > 9 || (seen & (1 << (int)field)) != 0)
                    throw new FormatException("arena_receipt_field");
                seen |= 1 << (int)field;
                uint value = Varint(bytes, ref position);
                if (field == 1) receipt.Result = value;
                if (field == 2) receipt.Time = value;
                if (field == 3) receipt.Money = value;
                if (field == 4) receipt.Reputation = value;
                if (field == 5) receipt.Gold = value;
            }
            if ((seen & 2) == 0 || receipt.Result > 2) throw new FormatException("arena_receipt_result");
            return receipt;
        }

        private static uint Varint(byte[] bytes, ref int position)
        {
            uint value = 0;
            for (int shift = 0; shift <= 28 && position < bytes.Length; shift += 7)
            {
                byte part = bytes[position++];
                if (shift == 28 && part > 15) break;
                value |= (uint)(part & 127) << shift;
                if ((part & 128) == 0) return value;
            }
            throw new FormatException("arena_receipt_varint");
        }
    }

    internal sealed class DailyArenaChestModel
    {
        private const uint DaySeconds = 86400, Cutoff = 72000, Lookback = 6 * DaySeconds;
        private const uint ActivityId = 3, CategoryId = 6;
        private const uint WaitMilliseconds = 18000, GuiWaitMilliseconds = 1500;

        private sealed class State
        {
            internal D Context;
            internal Component Select, ArenaUi, Chest, Active;
            internal object Arena, Bonus, ActivityTemplate, ActivityRows, Category, ListedRows, LoadScreen;
            internal IList Bonuses;
            internal Transport Connection;
            internal ulong CharacterId;
            internal uint Now, Day, Cycle, FreeSince, BonusTime, Rank, GoldConsumed;
            internal int Index = -1, Current, Required, ActivityRefresh, ListedCurrent, ListedRefresh, Gold, BoundGold, FreeCount, PaidCount;
            internal DateTime ActiveShownAt;
            internal byte Got;
            internal bool HasPaid, HasClaimedTime, BonusScanComplete;
            internal string Action = "", Reason = "", ListStamp = "";
            internal bool ProgressAgrees { get { return Current == ListedCurrent && ActivityRefresh == ListedRefresh; } }
            internal bool SameGold(State before)
            { return Gold == before.Gold && BoundGold == before.BoundGold && GoldConsumed == before.GoldConsumed; }
            internal bool SameActivity(State before)
            {
                return object.ReferenceEquals(ActivityTemplate, before.ActivityTemplate) &&
                    object.ReferenceEquals(ActivityRows, before.ActivityRows) && object.ReferenceEquals(Category, before.Category) &&
                    object.ReferenceEquals(ListedRows, before.ListedRows) && Required == before.Required;
            }

            internal bool SameBoundary(State before)
            {
                return before != null && Connection != null && Connection.Same(before.Connection) &&
                    Context.SameOwnership(before.Context) && object.ReferenceEquals(LoadScreen, before.LoadScreen) &&
                    CharacterId == before.CharacterId && Cycle == before.Cycle && Day == before.Day &&
                    FreeSince == before.FreeSince && Now >= before.Now;
            }

            internal bool SamePlan(State before)
            {
                return SameBoundary(before) &&
                    object.ReferenceEquals(Arena, before.Arena) && object.ReferenceEquals(Bonuses, before.Bonuses) &&
                    object.ReferenceEquals(Select, before.Select) && object.ReferenceEquals(ArenaUi, before.ArenaUi) &&
                    object.ReferenceEquals(Chest, before.Chest) && object.ReferenceEquals(Bonus, before.Bonus) &&
                    SameActivity(before) && Current == before.Current && ActivityRefresh == before.ActivityRefresh &&
                    ListedCurrent == before.ListedCurrent && ListedRefresh == before.ListedRefresh && SameGold(before) && Got == before.Got &&
                    (!object.ReferenceEquals(Active, before.Active) || ActiveShownAt == before.ActiveShownAt) &&
                    Index == before.Index && BonusTime == before.BonusTime &&
                    Rank == before.Rank && ListStamp == before.ListStamp;
            }
        }

        // RootUI and Global keep the same tcp_client object across reconnects.
        // The native bridge has no System.dll reference, so bind the pinned
        // transport fields and TcpClient.Connected getter by their signatures.
        private sealed class Transport
        {
            private readonly object socket;
            private readonly Stream stream;

            private Transport(object socket, Stream stream) { this.socket = socket; this.stream = stream; }
            internal bool Same(Transport other)
            { return other != null && object.ReferenceEquals(socket, other.socket) && object.ReferenceEquals(stream, other.stream); }

            internal static Transport Read(object client)
            {
                if (client == null || client.GetType() != Model.FirstType("game.net.tcp_client") || !Healthy(client)) return null;
                object socket = D.Read(client, "m_client");
                Stream stream = D.Read(client, "m_stream") as Stream;
                if (socket == null || stream == null || !stream.CanRead || !stream.CanWrite ||
                    !(bool)D.Method(socket.GetType(), "get_Connected", Type.EmptyTypes, false, typeof(bool)).Invoke(socket, null) ||
                    !object.ReferenceEquals(socket, D.Read(client, "m_client")) ||
                    !object.ReferenceEquals(stream, D.Read(client, "m_stream")) || !Healthy(client)) return null;
                return new Transport(socket, stream);
            }

            private static bool Healthy(object client)
            {
                return D.Static(Model.FirstType("game.net.tcp_base"), "connection_state").ToString() == "OK" &&
                    !Value<bool>(client, "m_bConnectLost") && !D.Bool(D.Static(Model.FirstType("Global"), "relogin"));
            }
        }

        private readonly DailyRefreshAdmission refresh;
        private State state, plan, queuedState, claim, navigationOwner;
        private IList[] navigationQueues;
        private Transport revisionConnection;
        private Component pendingPanel, priorReward, ownedReward, exitPanel;
        private DateTime priorRewardShownAt, ownedRewardShownAt, exitShownAt, previousArenaShownAt;
        private DevelopmentCallbackCapture callback, activity;
        private DailyArenaChestReceipt receipt;
        private Delegate stockBonusClose, chestClose;
        private int sequence, queuedSequence, queuedRevision, queuedAt, revision, deadlineStart, afterCounter = -1, afterGold;
        private int activityDispatch, activityRefresh;
        private string operation = "", queuedOperation, queuedAction, status = "IDLE", reason = "arena_not_observed", stamp = "", uiDisposition = "";
        private ulong queuedRole;
        private bool navigating, stopped, callbackReturned, callbackBoundaryChanged, settled, mutationAttempted, autoUiAbandoned;
        private bool rewardConfirmInvoked, awaitingArenaReturn, stockBranchAdmitted;
        private string deferredStop;
        private string rearmedOperation = "";
        private int rearmedSequence, rearmedRevision;
        internal DailyArenaChestModel() : this(DailyRefreshGate.Read) { }
        internal DailyArenaChestModel(DailyRefreshAdmission refreshAdmission) { refresh = refreshAdmission; }
        internal bool Busy { get { return queuedState != null || stockBranchAdmitted || navigationOwner != null || callback != null || activity != null ||
            (mutationAttempted && status != "COMPLETED") || plan != null && !stopped &&
            status != "COMPLETED" && status != "REJECTED" && status != "UNAVAILABLE"; } }
        internal bool Unresolved { get { return mutationAttempted && !settled; } }
        internal bool MutationAttempted { get { return mutationAttempted; } }
        internal bool GuiInvocationAttempted { get; private set; }
        internal int Revision { get { return revision; } }
        // Explicit, read-only replacement of a settled operation. A confirmed
        // reward may hand the exact retained page to the next free reward;
        // otherwise the previous operation must have returned to home.
        internal bool Rearm(int nextSequence, int expectedRevision, string operationId, ulong characterId,
            D context, out DailyArenaChestModel replacement, out string why)
        {
            bool retained = status == "CLAIMED" && uiDisposition == "continue_ready";
            replacement = null; why = "arena_rearm_completed_operation_required";
            if (stopped || (!retained && (status != "COMPLETED" || Busy)) || Unresolved || !settled || !callbackReturned ||
                queuedState != null || callback != null || activity != null || stockBranchAdmitted || navigating ||
                (!retained && navigationOwner != null) ||
                state == null || claim == null || operationId != operation || expectedRevision != revision ||
                nextSequence <= Math.Max(sequence, rearmedSequence) || characterId == 0 || characterId != state.CharacterId)
                return false;
            why = retained ? "arena_rearm_owned_page_unverified" : "arena_rearm_main_screen_unverified";
            if (context == null || context.Gate != "PASS" || !context.SameOwnership(claim.Context) ||
                (!retained && (context.SubUi != null || context.Panels() != "PASS")) ||
                !D.Bool(D.Static(Model.FirstType("Global"), "extern_enabled")) ||
                !D.Bool(D.Static(Model.FirstType("__BaseUI"), "my_mouseclick"))) return false;
            object client = context.Client;
            if (!Monitor.TryEnter(client, 0)) { why = "arena_rearm_client_busy"; return false; }
            try
            {
                State live = Sample(context);
                why = "arena_rearm_boundary_or_cleanup_changed";
                if (!live.BonusScanComplete || !live.SameBoundary(claim) || live.CharacterId != characterId || live.HasClaimedTime ||
                    !SettledProgress(live) ||
                    !object.ReferenceEquals(D.Read(live.LoadScreen, "currmg"), context.Root) ||
                    (bool)D.Method(live.LoadScreen.GetType(), "isVisible", Type.EmptyTypes, false, typeof(bool)).Invoke(live.LoadScreen, null) ||
                    !Idle(client, true)) return false;
                if (retained && (!live.SamePlan(state) || !RetainedPageReady(context) || live.FreeCount == 0)) return false;
                DailyArenaChestModel next = new DailyArenaChestModel(refresh);
                next.revision = revision;
                next.rearmedOperation = operation;
                next.rearmedSequence = nextSequence;
                next.rearmedRevision = revision;
                next.Capture(context);
                if (retained)
                {
                    // Re-sample eligibility without the old claim's plan. No
                    // opening, closing or reward request occurs during rearm.
                    if (next.status != "READY" || next.state == null || next.state.Action != "claim" ||
                        !next.state.SamePlan(live) ||
                        next.state.FreeCount != live.FreeCount || next.state.BonusTime == claim.BonusTime ||
                        !object.ReferenceEquals(next.state.Active, exitPanel) || next.state.ActiveShownAt != exitShownAt ||
                        !object.ReferenceEquals(next.state.Select, claim.Select) ||
                        !object.ReferenceEquals(next.state.ArenaUi, claim.ArenaUi) ||
                        !object.ReferenceEquals(next.state.Chest, claim.Chest) ||
                        !object.ReferenceEquals(next.state.Arena, live.Arena) || !object.ReferenceEquals(next.state.Bonuses, live.Bonuses))
                        return false;
                    next.plan = next.state;
                    next.navigationOwner = navigationOwner ?? claim;
                    next.navigationQueues = navigationQueues ?? ReadNavigationQueues(claim);
                    next.uiDisposition = "retained";
                    next.RefreshRevision();
                }
                replacement = next;
                why = ""; return true;
            }
            catch (Exception error)
            { why = error is ProbeFailure ? error.Message : "arena_rearm_observation_failed"; return false; }
            finally { Monitor.Exit(client); }
        }
        internal void Capture(D context)
        {
            if (stopped || status == "UNKNOWN" || status == "REJECTED" || status == "COMPLETED")
            {
                Detach();
                ReleaseRestoredNavigation(context);
                RefreshRevision();
                return;
            }
            if (settled)
            {
                Detach();
                ObserveSettledUi(context);
                RefreshRevision();
                return;
            }
            try
            {
                state = Sample(context);
                if (navigating) ObserveNavigation();
                if (mutationAttempted) ObserveClaim();
                else if (queuedState == null && !navigating && status != "REJECTED")
                {
                    status = state.Action != "" ? "READY" : state.Reason == "arena_no_current_free_bonus" ||
                        state.Reason == "arena_activity_complete" || state.Reason == "arena_activity_disabled" ? "UNAVAILABLE" :
                        state.Reason == "arena_panel_loading" || state.Reason == "arena_network_pending" ||
                        state.Reason == "daily_refresh_pending" ? "WAITING" : "BLOCKED";
                    reason = state.Reason;
                }
            }
            catch (Exception error)
            {
                state = null;
                if (mutationAttempted) Unknown("arena_observation_failed");
                else if (plan == null && queuedState == null)
                { status = "BLOCKED"; reason = error is ProbeFailure ? error.Message : "arena_binding_schema"; }
                else Reject(error is ProbeFailure ? error.Message : "arena_binding_schema");
            }
            RefreshRevision();
        }

        private bool ReleaseRestoredNavigation(D context)
        {
            State owner = navigationOwner;
            if (owner == null) return true;
            if (callback != null || activity != null || context == null) return false;
            try
            {
                if (context.Gate != "PASS" || !context.SameOwnership(owner.Context) || context.SubUi != null ||
                    !D.Bool(D.Static(Model.FirstType("Global"), "extern_enabled")) || context.Panels() != "PASS" ||
                    !D.Bool(D.Static(Model.FirstType("__BaseUI"), "my_mouseclick")) ||
                    !object.ReferenceEquals(D.Static(Model.FirstType("BaseAsset"), "lds"), owner.LoadScreen) ||
                    !object.ReferenceEquals(D.Read(owner.LoadScreen, "currmg"), context.Root) ||
                    (bool)D.Method(owner.LoadScreen.GetType(), "isVisible", Type.EmptyTypes, false, typeof(bool)).Invoke(owner.LoadScreen, null) ||
                    !object.ReferenceEquals(context.Find("ArenaSelectUI"), owner.Select) ||
                    !object.ReferenceEquals(context.Find("ArenaUI"), owner.ArenaUi) ||
                    !object.ReferenceEquals(context.Find("ArenaChestUI"), owner.Chest) ||
                    !Dormant(owner.Select, 0) || !Dormant(owner.ArenaUi, 2) || !Dormant(owner.Chest, 4) ||
                    !Monitor.TryEnter(context.Client, 0)) return false;
                try
                {
                    if (owner.Connection == null || !owner.Connection.Same(Transport.Read(context.Client)) || !Idle(context.Client, true))
                        return false;
                    navigationOwner = null;
                    navigationQueues = null;
                    return true;
                }
                finally { Monitor.Exit(context.Client); }
            }
            catch (Exception) { return false; }
        }

        private bool Dormant(Component panel, int offset)
        {
            // LoadSuccess can only show this panel while its main queue is
            // live. Refuse release if either stock queue can still run.
            IList main = navigationQueues[offset], sub = navigationQueues[offset + 1];
            return !D.Visible(panel) && !D.Loading(panel) && main.Count == 0 && sub.Count == 0 &&
                object.ReferenceEquals(D.Read(panel, "asset_names"), main) &&
                object.ReferenceEquals(D.Read(panel, "asset_names_sub"), sub);
        }

        private bool ReadBoundary(State s)
        {
            D context = s.Context;
            object load = D.Static(Model.FirstType("BaseAsset"), "lds");
            if (context == null || !context.SameOwnership(context) || context.Character.GetType() != Model.FirstType("Database.Character") ||
                load == null || !object.ReferenceEquals(D.Read(load, "currmg"), context.Root))
            { s.Reason = "arena_role_unavailable"; return false; }
            s.LoadScreen = load;
            s.CharacterId = Value<ulong>(context.Character, "id");
            s.Reason = refresh(context.Character, out s.Now, out s.Day, out s.Cycle);
            if (s.Reason != "") return false;
            if (s.Day < Lookback + DaySeconds) { s.Reason = "arena_server_clock_unavailable"; return false; }
            // At exactly 20:00 both stock claim views still use yesterday's
            // midnight as the free boundary. The following second uses today.
            s.FreeSince = s.Now <= (ulong)s.Day + Cutoff ? s.Day - DaySeconds : s.Day;
            s.Connection = object.ReferenceEquals(D.Static(Model.GameType("RootUI"), "m_client"), context.Client) ?
                Transport.Read(context.Client) : null;
            if (s.Connection == null) { s.Reason = "arena_transport_unavailable"; return false; }
            return true;
        }

        private State Sample(D context)
        {
            State s = new State { Context = context };
            if (!ReadBoundary(s)) return s;
            if (!ReadActivity(s)) return s;
            if (!mutationAttempted && !s.ProgressAgrees) { s.Reason = "arena_activity_cache_mismatch"; return s; }
            s.Arena = D.Read(context.Character, "arena");
            Type arenaType = Model.FirstType("Database.Arena");
            if (s.Arena == null || s.Arena.GetType() != arenaType) { s.Reason = "arena_data_unavailable"; return s; }
            s.Got = Value<byte>(s.Arena, "got");
            ReadGold(s);
            FieldInfo listField = arenaType.GetField("bonuses", D.Instance);
            s.Bonuses = listField == null ? null : listField.GetValue(s.Arena) as IList;
            if (s.Bonuses == null || s.Bonuses.GetType() != listField.FieldType || !listField.FieldType.IsGenericType ||
                listField.FieldType.GetGenericTypeDefinition() != typeof(List<>) || s.Bonuses.Count > 64)
            { s.Reason = "arena_bonus_list_schema"; return s; }
            Type bonusType = listField.FieldType.GetGenericArguments()[0];
            if (bonusType.FullName != "Database._character_arena_bonus") { s.Reason = "arena_bonus_type"; return s; }
            Dictionary<uint, bool> times = new Dictionary<uint, bool>();
            StringBuilder listStamp = new StringBuilder();
            int currentCount = 0; bool pastDisplayBreak = false;
            for (int index = 0; index < s.Bonuses.Count; index++)
            {
                object bonus = s.Bonuses[index];
                if (bonus == null || bonus.GetType() != bonusType) { s.Reason = "arena_bonus_type"; return s; }
                uint time = Value<uint>(bonus, "time"), rank = Value<uint>(bonus, "rank");
                if (time == 0 || time > s.Now || rank == 0 || times.ContainsKey(time)) { s.Reason = "arena_bonus_identity"; return s; }
                times.Add(time, true);
                listStamp.Append(time).Append('/').Append(rank).Append(',');
                if (claim != null && time == claim.BonusTime) s.HasClaimedTime = true;
                if (time < s.FreeSince) s.PaidCount++;
                if (time < s.FreeSince - Lookback) { pastDisplayBreak = true; continue; }
                if (time < s.FreeSince) { s.HasPaid = true; continue; }
                currentCount++;
                if (index <= 3 && !pastDisplayBreak && s.Index < 0)
                { s.Bonus = bonus; s.BonusTime = time; s.Rank = rank; s.Index = index; }
            }
            s.ListStamp = listStamp.ToString();
            s.FreeCount = currentCount;
            s.BonusScanComplete = true;
            if (!mutationAttempted)
            {
                if (currentCount == 0) { s.Reason = "arena_no_current_free_bonus"; return s; }
                if (s.Index < 0 || !s.HasPaid && s.Index != 0) { s.Reason = "arena_current_free_bonus_not_visible"; return s; }
            }
            s.Select = context.Find("ArenaSelectUI"); s.ArenaUi = context.Find("ArenaUI"); s.Chest = context.Find("ArenaChestUI");
            s.Active = context.SubUi as Component;
            if (s.Active == s.Select || s.Active == s.ArenaUi || s.Active == s.Chest)
                s.ActiveShownAt = ShownAt(s.Active);
            if (mutationAttempted) return s;
            MethodInfo getTemplate = arenaType.GetMethod("getArenaBonus", D.StaticFlags, null, new Type[] { typeof(uint) }, null);
            if (getTemplate == null || getTemplate.ReturnType.FullName != "Database._arena_bonus" ||
                getTemplate.Invoke(null, new object[] { s.Rank }) == null)
            { s.Reason = "arena_bonus_template_unavailable"; return s; }
            if (plan != null && !s.SamePlan(plan)) { s.Reason = "arena_plan_changed"; return s; }
            if (context.SubUi != null && s.Active == null || s.Active != null && s.Active != s.Select && s.Active != s.ArenaUi && s.Active != s.Chest)
            { s.Reason = "arena_foreign_subui"; return s; }
            s.Reason = context.Panels(s.Active);
            if (s.Reason != "PASS") return s;
            if (s.Active != null && (!D.Enabled(s.Active) || D.Loading(s.Active))) { s.Reason = "arena_panel_loading"; return s; }
            if (s.Active == s.ArenaUi || s.Active == s.Chest)
            {
                if (!object.ReferenceEquals(D.Read(s.ArenaUi, "m_arena"), s.Arena) ||
                    s.Active == s.Chest && !object.ReferenceEquals(D.Read(s.Chest, "m_arena"), s.Arena))
                { s.Reason = "arena_panel_model_changed"; return s; }
            }
            if (!Monitor.TryEnter(context.Client, 0)) { s.Reason = "arena_network_pending"; return s; }
            try { if (!Idle(context.Client, false)) { s.Reason = "arena_network_pending"; return s; } }
            finally { Monitor.Exit(context.Client); }
            s.Action = s.Active == null ? "open" : s.Active == s.Select ? "enter" :
                s.Active == s.ArenaUi && s.HasPaid ? "open_chest" : "claim";
            if (s.Active == s.Chest && !s.HasPaid) { s.Action = ""; s.Reason = "arena_unexpected_chest_branch"; }
            else s.Reason = "";
            return s;
        }

        private static bool ReadActivity(State s)
        {
            object role = s.Context.Character;
            IDictionary acts = D.Read(role, "acts") as IDictionary, categories = D.Read(role, "act_rewards") as IDictionary;
            object latest = acts == null ? null : acts[ActivityId];
            s.Category = categories == null ? null : categories[CategoryId];
            Type activityType = Model.FirstType("Database.Activity");
            if (latest == null || latest.GetType() != activityType || Value<byte>(latest, "idactivity") != ActivityId ||
                s.Category == null || s.Category.GetType() != Model.FirstType("Database.Activity_Reward") ||
                Value<byte>(s.Category, "idact_reward") != CategoryId)
            { s.Reason = "arena_activity_unavailable"; return false; }
            s.ActivityRows = acts;
            IDictionary listedRows = D.Read(s.Category, "acts") as IDictionary;
            s.ListedRows = listedRows;
            object listed = listedRows == null ? null : listedRows[ActivityId];
            s.ActivityTemplate = D.Read(latest, "temp");
            if (listed == null || listed.GetType() != activityType || Value<byte>(listed, "idactivity") != ActivityId ||
                s.ActivityTemplate == null || !object.ReferenceEquals(D.Read(listed, "temp"), s.ActivityTemplate) ||
                !object.ReferenceEquals(s.ActivityTemplate, D.Template("activity", ActivityId)) ||
                Value<byte>(s.ActivityTemplate, "id") != ActivityId || Value<byte>(s.ActivityTemplate, "idreward") != CategoryId)
            { s.Reason = "arena_activity_identity"; return false; }
            if (!Value<bool>(s.ActivityTemplate, "active")) { s.Reason = "arena_activity_disabled"; return false; }
            s.Required = Value<int>(s.ActivityTemplate, "cond_param");
            s.Current = Value<byte>(latest, "condition_param"); s.ActivityRefresh = Value<int>(latest, "refresh_time");
            s.ListedCurrent = Value<byte>(listed, "condition_param"); s.ListedRefresh = Value<int>(listed, "refresh_time");
            if (s.Required < 1 || s.Required > Byte.MaxValue || s.Current > s.Required || s.ListedCurrent > s.Required)
            { s.Reason = "arena_activity_counter_schema"; return false; }
            return true;
        }

        private static void ReadGold(State s)
        {
            object role = s.Context.Character;
            s.Gold = Value<int>(role, "gold"); s.BoundGold = Value<int>(role, "bind_gold");
            s.GoldConsumed = Value<uint>(role, "gold_consume");
        }

        private static T Value<T>(object target, string field)
        {
            object value = D.Read(target, field);
            if (!(value is T)) throw new ProbeFailure("arena_field_schema:" + field);
            return (T)value;
        }

        // Pinned __BaseUI.Visible sets this on the disabled-to-visible edge.
        // It is a wall-clock sample, so equal timestamps alone cannot prove
        // that an unseen hide/reopen within one clock tick never happened.
        private static DateTime ShownAt(Component panel) { return Value<DateTime>(panel, "visible_time"); }

        private void ObserveNavigation()
        {
            if (state == null || !state.Context.SameOwnership(plan.Context) || !state.SamePlan(plan))
            { Reject("arena_navigation_identity_changed"); return; }
            if (state.Active == pendingPanel && state.Action != "")
            { navigating = false; pendingPanel = null; return; }
            if (Expired) Reject("arena_navigation_timeout");
        }

        private void ObserveClaim()
        {
            if (Expired) { Unknown("arena_settlement_timed_out"); return; }
            if (state == null || !state.SameBoundary(claim) || callbackBoundaryChanged)
            { Unknown("arena_settlement_boundary_changed"); return; }
            if (state.Arena == null || !object.ReferenceEquals(state.Arena, claim.Arena) ||
                !object.ReferenceEquals(state.Bonuses, claim.Bonuses) || !state.SameActivity(claim) || !state.SameGold(claim))
            { Unknown("arena_settlement_identity_or_gold_changed"); return; }

            status = "WAITING"; reason = "arena_waiting_for_settlement";
            afterCounter = state.Current; afterGold = state.Gold;
            if (state.BonusScanComplete) ObservePendingUi(state.Context);
            DevelopmentCallbackWitness witness = callback == null ? null : callback.Read();
            DevelopmentCallbackWitness pushed = activity == null ? null : activity.Read();
            if (pushed == null || pushed.Error != null || pushed.OriginalThrew || pushed.NullResponse)
            { Unknown("arena_activity_callback_unverified"); return; }
            object client = state.Context.Client;
            if (!Monitor.TryEnter(client, 0)) return;
            try
            {
                if (!activity.StillInstalled()) { Unknown("arena_activity_callback_replaced"); return; }
                if (witness != null && witness.Returned &&
                    ((IDictionary)D.Read(client, "m_handlers")).Contains(DailyArenaChestReceipt.Opcode))
                { Unknown("arena_stock_callback_handler_present"); return; }
            }
            finally { Monitor.Exit(client); }
            if (witness == null || witness.Error != null || witness.NullResponse || witness.OriginalThrew || witness.Calls > 1)
            { Unknown(witness == null ? "arena_callback_unobserved" : witness.Error ?? "arena_callback_null_or_threw"); return; }
            if (!witness.Returned)
            {
                if (!CallbackStillPending()) Unknown("arena_callback_replaced");
                return;
            }
            callbackReturned = true;
            if (receipt == null || receipt.Time != claim.BonusTime || receipt.Gold != 0 || receipt.Result != 0)
            { Unknown("arena_receipt_does_not_confirm_free_claim"); return; }
            if (state.Current < claim.Current || state.Current > claim.Current + 1 ||
                state.ListedCurrent < claim.ListedCurrent || state.ListedCurrent > claim.ListedCurrent + 1)
            { Unknown("arena_counter_unexpected_delta"); return; }
            if (!state.BonusScanComplete)
            {
                reason = "arena_bonus_scan_unverified:" + state.Reason;
                return;
            }
            if (!state.HasClaimedTime && state.Got == 1 && SettledProgress(state) &&
                (claim.Current >= claim.Required || activityDispatch != 0 && activity.ReturnedForDispatch(activityDispatch)))
            {
                settled = true;
                deadlineStart = Environment.TickCount;
                Detach();
                ObserveSettledUi(state.Context);
                return;
            }
            reason = state.HasClaimedTime || state.Got != 1 ? "arena_waiting_bonus_removal" : "arena_waiting_activity_push_and_caches";
        }

        private bool SettledProgress(State live)
        {
            return live.SameActivity(claim) && live.SameGold(claim) && live.ProgressAgrees &&
                (claim.Current >= claim.Required ? live.Current == claim.Current :
                    live.Current == claim.Current + 1 && live.ActivityRefresh == activityRefresh);
        }

        private void ReadActivityPush(byte[] bytes)
        {
            State live = new State { Context = claim.Context };
            if (!mutationAttempted || Expired || !ReadBoundary(live) || !live.SameBoundary(claim) ||
                !ReadActivity(live) || !live.SameActivity(claim))
                throw new FormatException("arena_activity_boundary_changed");
            ReadGold(live);
            if (!live.SameGold(claim)) throw new FormatException("arena_activity_gold_changed");
            int refreshTime;
            int? count = GuildReceipt.Progress(bytes, (int)ActivityId, out refreshTime);
            if (claim.Current >= claim.Required && count == claim.Current) return;
            if (!count.HasValue || count == claim.Current && refreshTime == claim.ActivityRefresh && activityDispatch == 0) return;
            if (count != claim.Current + 1 || activityDispatch != 0 && refreshTime != activityRefresh)
                throw new FormatException("arena_activity_push_conflict");
            activityRefresh = refreshTime;
            activityDispatch = activity.Read().Calls;
        }

        private bool OwnsClaimPanel(D context)
        { return OwnsPanel(context, claim.Active, claim.ActiveShownAt, true); }

        private void ObservePendingUi(D context)
        {
            if (autoUiAbandoned) return;
            try
            {
                Component reward = context.GrowthRewardDialog();
                if (!OwnsClaimPanel(context) || (D.Visible(reward) ? !BindVisibleReward(reward) : ownedReward != null))
                    autoUiAbandoned = true;
            }
            catch (Exception) { autoUiAbandoned = true; }
        }

        private bool BindVisibleReward(Component reward)
        {
            DateTime shownAt = ShownAt(reward);
            // Under exclusive use, the exact single-use stock delegate and a
            // fresh visibility edge corroborate ownership. Neither is a
            // protocol nonce or detects arbitrary concurrent user actions.
            if (!object.ReferenceEquals(D.Read(reward, "OnClose"), stockBonusClose) || shownAt == default(DateTime) ||
                object.ReferenceEquals(reward, priorReward) && shownAt == priorRewardShownAt ||
                ownedReward != null && (!object.ReferenceEquals(reward, ownedReward) || shownAt != ownedRewardShownAt))
                return false;
            ownedReward = reward; ownedRewardShownAt = shownAt;
            return true;
        }

        private bool OwnsPanel(D context, Component panel, DateTime shownAt, bool visible)
        {
            return panel != null && object.ReferenceEquals(context.SubUi, panel) && D.Visible(panel) == visible && !D.Loading(panel) &&
                shownAt != default(DateTime) && ShownAt(panel) == shownAt &&
                object.ReferenceEquals(D.Read(panel, "m_arena"), claim.Arena) &&
                object.ReferenceEquals(D.Read(claim.ArenaUi, "m_arena"), claim.Arena) &&
                object.ReferenceEquals(D.Read(claim.Arena, "bonuses"), claim.Bonuses) &&
                D.Read(claim.ArenaUi, "OnClose") == null &&
                (panel != claim.Chest || chestClose != null && object.ReferenceEquals(chestClose.Target, claim.ArenaUi) &&
                    object.ReferenceEquals(D.Read(panel, "OnClose"), chestClose));
        }

        private void ObserveSettledUi(D context)
        {
            status = "CLAIMED"; reason = "arena_free_claim_ui_unconfirmed"; uiDisposition = "unconfirmed";
            if (state != null) state.Action = "";
            try
            {
                State current = Sample(context);
                if (!current.SameBoundary(claim)) { autoUiAbandoned = true; return; }
                state = current;
                if (callback != null || activity != null || !current.BonusScanComplete || current.HasClaimedTime || !SettledProgress(current)) return;
                Component reward = context.GrowthRewardDialog();
                if (context.SubUi == null && !D.Visible(reward) && (reward == null || !D.Loading(reward)) &&
                    D.Bool(D.Static(Model.FirstType("Global"), "extern_enabled")) && context.Panels() == "PASS" &&
                    ReleaseRestoredNavigation(context))
                {
                    status = "COMPLETED"; reason = "arena_free_claim_observed"; uiDisposition = "clear";
                    return;
                }
                reason = "arena_free_claim_waiting_user"; uiDisposition = "manual";
                if (autoUiAbandoned) return;
                if (current.Current != afterCounter) { autoUiAbandoned = true; return; }
                if (rewardConfirmInvoked) { ObserveOwnedExit(context, reward); return; }
                if (!OwnsClaimPanel(context))
                { autoUiAbandoned = true; return; }
                if (!D.Visible(reward))
                {
                    if (ownedReward != null || Expired) autoUiAbandoned = true;
                    else if (reward != null && D.Loading(reward))
                    { reason = "arena_stock_reward_loading"; uiDisposition = "loading"; }
                    return; // Stock result can become visible after asynchronous loading.
                }
                if (!BindVisibleReward(reward))
                { autoUiAbandoned = true; return; }
                if (D.Loading(reward) || !D.Enabled(reward)) return;
                if (context.RewardPanels(claim.Active, reward) != "PASS") { autoUiAbandoned = true; return; }
                state.Action = "reward_confirm";
                reason = "arena_free_claim_reward_ready"; uiDisposition = "confirm_ready";
            }
            catch (Exception) { autoUiAbandoned = true; }
        }

        private void ObserveOwnedExit(D context, Component reward)
        {
            if (D.Visible(reward)) { autoUiAbandoned = true; return; }
            if (awaitingArenaReturn)
            {
                if (!object.ReferenceEquals(context.SubUi, claim.ArenaUi) || Expired)
                { autoUiAbandoned = true; return; }
                if (!D.Enabled(claim.ArenaUi) || D.Loading(claim.ArenaUi))
                { reason = "arena_stock_return_loading"; uiDisposition = "loading"; return; }
                DateTime shownAt = ShownAt(claim.ArenaUi);
                if (shownAt == previousArenaShownAt) { autoUiAbandoned = true; return; }
                exitPanel = claim.ArenaUi; exitShownAt = shownAt; awaitingArenaReturn = false;
            }
            if (!ExitStillOwned(context, true)) { autoUiAbandoned = true; return; }
            if (!Monitor.TryEnter(context.Client, 0)) return;
            try { if (!Idle(context.Client, true)) return; }
            finally { Monitor.Exit(context.Client); }
            if (state.FreeCount > 0)
            {
                // Stock BonusGetClose has restored this same page. Keep it
                // open while another visible free row is independently
                // qualified; the old receipt remains sealed until rearm.
                state.Action = "";
                reason = "arena_next_free_bonus_unverified"; uiDisposition = "manual";
                if (RetainedPageReady(context))
                { reason = "arena_next_free_bonus_same_page"; uiDisposition = "continue_ready"; }
                return;
            }
            state.Action = "close"; reason = "arena_stock_close_ready"; uiDisposition = "close_ready";
        }

        private bool RetainedPageReady(D context)
        {
            if (autoUiAbandoned || !rewardConfirmInvoked || awaitingArenaReturn ||
                !object.ReferenceEquals(exitPanel, claim.Active) || exitShownAt != claim.ActiveShownAt ||
                !ExitStillOwned(context, true)) return false;
            Component reward = context.GrowthRewardDialog();
            if (D.Visible(reward) || reward != null && D.Loading(reward)) return false;
            DailyArenaChestModel probe = new DailyArenaChestModel(refresh);
            State next = probe.Sample(context);
            return next.Action == "claim" && next.BonusScanComplete && next.FreeCount > 0 &&
                next.BonusTime != claim.BonusTime && next.SameBoundary(claim) &&
                object.ReferenceEquals(next.Bonus, state.Bonus) && next.BonusTime == state.BonusTime &&
                next.Index == state.Index && next.Rank == state.Rank && next.ListStamp == state.ListStamp &&
                next.FreeCount == state.FreeCount &&
                next.SameActivity(claim) && next.SameGold(claim) &&
                object.ReferenceEquals(next.Select, claim.Select) && object.ReferenceEquals(next.ArenaUi, claim.ArenaUi) &&
                object.ReferenceEquals(next.Chest, claim.Chest) &&
                object.ReferenceEquals(next.Arena, claim.Arena) && object.ReferenceEquals(next.Bonuses, claim.Bonuses) &&
                object.ReferenceEquals(next.Active, exitPanel) && next.ActiveShownAt == exitShownAt;
        }

        private bool ExitStillOwned(D context, bool visible)
        {
            State live = Sample(context);
            return !stopped && live.SameBoundary(claim) && live.BonusScanComplete && !live.HasClaimedTime && SettledProgress(live) &&
                OwnsPanel(context, exitPanel, exitShownAt, visible) && (!visible || D.Enabled(exitPanel)) &&
                (exitPanel == claim.Chest ? context.RewardPanels(exitPanel) : context.Panels(exitPanel)) == "PASS";
        }

        private bool RewardStillOwned(bool visible) { return RewardStillOwned(visible, state.Context); }

        private bool RewardStillOwned(bool visible, D context)
        {
            State live = Sample(context);
            return !stopped && live.SameBoundary(claim) && live.BonusScanComplete && !live.HasClaimedTime && SettledProgress(live) && OwnsClaimPanel(live.Context) &&
                ownedReward != null && object.ReferenceEquals(ownedReward, live.Context.GrowthRewardDialog()) &&
                D.Visible(ownedReward) == visible && !D.Loading(ownedReward) && ShownAt(ownedReward) == ownedRewardShownAt &&
                object.ReferenceEquals(D.Read(ownedReward, "OnClose"), stockBonusClose) &&
                live.Context.RewardPanels(claim.Active, ownedReward) == "PASS";
        }

        private bool CallbackStillPending()
        {
            object client = claim.Context.Client;
            if (!Monitor.TryEnter(client, 0)) return true;
            try
            {
                IDictionary handlers = D.Read(client, "m_handlers") as IDictionary;
                return handlers != null && object.ReferenceEquals(handlers[DailyArenaChestReceipt.Opcode], ownHandler) && callback.StillInstalled();
            }
            finally { Monitor.Exit(client); }
        }

        private object ownHandler;
        private bool Expired { get { return unchecked((uint)(Environment.TickCount - deadlineStart)) >= WaitMilliseconds; } }

        internal bool Queue(int nextSequence, int expectedRevision, string action, string operationId, ulong characterId, out string why)
        {
            why = "arena_command_or_revision_changed";
            if (stopped || stockBranchAdmitted || !ActionReady(action) || queuedState != null || state == null || action == "" || action != state.Action ||
                nextSequence <= Math.Max(sequence, rearmedSequence) || expectedRevision != revision || !Command.ValidNonce(operationId) ||
                characterId == 0 || characterId != state.CharacterId || operation != "" && operation != operationId) return false;
            queuedState = state; queuedSequence = nextSequence; queuedRevision = expectedRevision; queuedAction = action;
            queuedOperation = operationId; queuedRole = characterId; queuedAt = Environment.TickCount;
            why = ""; return true;
        }

        internal bool ExecuteGui(int nextSequence, int expectedRevision, string action, string operationId, ulong characterId,
            DailyArenaContextProvider freshContext, DevelopmentPublication publish, DailyArenaInvocationGate begin, out string why)
        {
            GuiInvocationAttempted = false;
            State before = queuedState;
            queuedState = null;
            why = "arena_gui_command_not_owned";
            if (before == null || stopped || freshContext == null || publish == null || begin == null || !D.Gui || nextSequence != queuedSequence ||
                expectedRevision != queuedRevision || expectedRevision != revision || action != queuedAction ||
                operationId != queuedOperation || characterId != queuedRole) return false;
            object client = before.Context.Client;
            if (!Monitor.TryEnter(client, 0)) { why = "arena_client_busy"; return false; }
            bool publicationStarted = false, publicationReturned = false;
            int admittedAt = queuedAt;
            DailyArenaInvocationDeadline withinDeadline = delegate
            { return !stopped && unchecked((uint)(Environment.TickCount - admittedAt)) < GuiWaitMilliseconds; };
            DevelopmentPublication commit = delegate
            {
                State expected = state;
                if (stockBranchAdmitted || !D.Gui || !withinDeadline() || !begin(withinDeadline) ||
                    !TransportStillOwned(expected) ||
                    (action == "claim" && (activity == null || !activity.StillInstalled() || !Idle(client, false))) ||
                    !D.Gui || !withinDeadline())
                    throw new ProbeFailure("arena_final_gui_admission_closed");
                // Linearize the entire stock button branch once. Stop after
                // this point is applied after its contiguous stock calls.
                stockBranchAdmitted = true;
                GuiInvocationAttempted = true;
            };
            DevelopmentPublication publishOwned = delegate { publicationStarted = true; publish(); publicationReturned = true; };
            try
            {
                if (!withinDeadline()) throw new ProbeFailure("arena_gui_deadline_expired");
                Capture(freshContext());
                if (expectedRevision != revision || state == null || state.Action != action || !ActionReady(action) || !state.SamePlan(before) ||
                    state.Active != before.Active || !Idle(client, action == "reward_confirm" || action == "close"))
                { why = "arena_preinvoke_state_changed_or_network_busy"; return false; }
                operation = operationId; sequence = nextSequence;
                if (plan == null) plan = state;
                if (action == "claim") InvokeClaim(freshContext, publishOwned, commit);
                else if (action == "reward_confirm")
                {
                    if (!RewardStillOwned(true)) throw new ProbeFailure("arena_reward_close_unowned");
                    publishOwned();
                    RecheckPublishedClose(before, action, freshContext);
                    if (!RewardStillOwned(true)) throw new ProbeFailure("arena_reward_close_unowned");
                    bool closesChest = exitPanel == claim.Chest && claim.Bonuses.Count == 0;
                    MethodInfo clear = closesChest ? StockClearMethod() : null;
                    CommitNavigation(state, commit);
                    rewardConfirmInvoked = true; // A UI confirmation is attempted at most once.
                    D.Call(ownedReward, "Hide");
                    if (!RewardStillOwned(false) || closesChest != (exitPanel == claim.Chest && claim.Bonuses.Count == 0))
                        throw new ProbeFailure("arena_reward_close_unowned");
                    if (closesChest) BeginStockChestReturn();
                    InvokeClose(stockBonusClose);
                    if (closesChest) FinishStockChestClose(clear);
                    state.Action = "";
                    reason = "arena_free_claim_ui_unconfirmed"; uiDisposition = "unconfirmed";
                }
                else if (action == "close")
                {
                    if (!ExitStillOwned(state.Context, true)) throw new ProbeFailure("arena_stock_close_unowned");
                    publishOwned();
                    RecheckPublishedClose(before, action, freshContext);
                    if (!ExitStillOwned(state.Context, true)) throw new ProbeFailure("arena_stock_close_unowned");
                    MethodInfo clear = StockClearMethod();
                    if (exitPanel == claim.Chest)
                    {
                        BeginStockChestReturn();
                        CommitNavigation(state, commit);
                        D.Call(exitPanel, "Hide");
                        if (!ExitStillOwned(state.Context, false)) throw new ProbeFailure("arena_stock_close_unowned");
                        InvokeClose(chestClose);
                        FinishStockChestClose(clear);
                    }
                    else
                    {
                        CommitNavigation(state, commit);
                        ClearStockSubUi(clear);
                    }
                    state.Action = "";
                    reason = "arena_free_claim_ui_unconfirmed"; uiDisposition = "unconfirmed";
                }
                else OpenPanel(action, freshContext, publishOwned, commit);
                why = ""; return true;
            }
            catch (Exception error)
            {
                // The controller's stop/lease exception must reach its GUI
                // handoff. No stock method ran when publication did not return.
                if (publicationStarted && !publicationReturned)
                {
                    if (action == "claim") { mutationAttempted = false; claim = null; stockBonusClose = null; }
                    if (settled)
                    { autoUiAbandoned = true; reason = "arena_publication_stopped_before_invocation"; uiDisposition = "manual"; }
                    else if (!stopped) Reject("arena_publication_stopped_before_invocation");
                    throw;
                }
                if (settled && status != "COMPLETED")
                {
                    autoUiAbandoned = true; status = "CLAIMED"; if (state != null) state.Action = "";
                    reason = "arena_free_claim_ui_unconfirmed"; uiDisposition = "unconfirmed";
                }
                else if (!settled && mutationAttempted) Unknown(error is ProbeFailure ? error.Message : "arena_normal_action_threw");
                else if (!settled && !stopped) Reject(error is ProbeFailure ? error.Message : "arena_normal_action_threw");
                why = reason; return false;
            }
            finally
            {
                stockBranchAdmitted = false;
                Monitor.Exit(client);
                if (deferredStop != null)
                {
                    string stop = deferredStop;
                    deferredStop = null;
                    Stop(stop);
                }
                RefreshRevision();
            }
        }

        private static bool TransportStillOwned(State expected)
        {
            if (expected == null || expected.Context == null || expected.Connection == null) return false;
            object client = expected.Context.Client;
            return object.ReferenceEquals(D.Static(Model.GameType("RootUI"), "m_client"), client) &&
                object.ReferenceEquals(D.Static(Model.FirstType("Global"), "m_client"), client) &&
                object.ReferenceEquals(D.Static(Model.FirstType("BaseAsset"), "lds"), expected.LoadScreen) &&
                object.ReferenceEquals(D.Read(expected.LoadScreen, "currmg"), expected.Context.Root) &&
                expected.Context.SameOwnership(expected.Context) && expected.Connection.Same(Transport.Read(client));
        }

        private void RecheckPublishedClose(State admitted, string action, DailyArenaContextProvider freshContext)
        {
            Capture(freshContext());
            if (stopped || !D.Gui || !ActionReady(action) || state == null || state.Action != action ||
                !object.ReferenceEquals(state.Active, admitted.Active) || !state.SamePlan(admitted) || !Idle(admitted.Context.Client, true))
                throw new ProbeFailure("arena_close_changed_after_publication");
        }

        private void CommitNavigation(State owner, DevelopmentPublication commit)
        {
            if (navigationOwner != null) { commit(); return; }
            IList[] queues = ReadNavigationQueues(owner);
            commit();
            navigationQueues = queues;
            navigationOwner = owner;
        }

        private static IList[] ReadNavigationQueues(State owner)
        {
            IList[] queues = new IList[6];
            Component[] panels = { owner.Select, owner.ArenaUi, owner.Chest };
            for (int i = 0; i < panels.Length; i++)
            {
                queues[i * 2] = D.Read(panels[i], "asset_names") as IList;
                queues[i * 2 + 1] = D.Read(panels[i], "asset_names_sub") as IList;
                if (queues[i * 2] == null || queues[i * 2 + 1] == null)
                    throw new ProbeFailure("arena_loader_queue_identity");
            }
            return queues;
        }

        private void BeginStockChestReturn()
        {
            awaitingArenaReturn = chestClose.Method.Name == "SubClose";
            previousArenaShownAt = ShownAt(claim.ArenaUi);
            deadlineStart = Environment.TickCount;
        }

        private void FinishStockChestClose(MethodInfo clear)
        {
            if (awaitingArenaReturn)
            {
                if (!object.ReferenceEquals(state.Context.SubUi, claim.ArenaUi))
                    throw new ProbeFailure("arena_stock_return_unobserved");
                return;
            }
            // EnableAll is the other stock chest close. It leaves a hidden
            // chest pointer, which can be cleared while the exact owner holds.
            if (!ExitStillOwned(state.Context, false)) throw new ProbeFailure("arena_stock_close_unowned");
            ClearStockSubUi(clear);
        }

        private static MethodInfo StockClearMethod()
        { return D.Method(Model.GameType("MainUI"), "SetSubUI", new Type[] { Model.FirstType("__BaseUI") }, true, typeof(void)); }

        private void ClearStockSubUi(MethodInfo close)
        {
            close.Invoke(null, new object[] { null });
            if (state.Context.SubUi != null || D.Visible(exitPanel)) throw new ProbeFailure("arena_stock_close_unobserved");
            exitPanel = null;
        }

        private void RecheckPublishedPlan(State admitted, string action, DailyArenaContextProvider freshContext)
        {
            State current = Sample(freshContext());
            if (stopped || !D.Gui || current.Action != action ||
                !object.ReferenceEquals(current.Active, admitted.Active) || !current.SamePlan(admitted))
                throw new ProbeFailure("arena_plan_changed_after_publication");
            state = current;
        }

        private void RecheckDisabledPlan(State admitted)
        {
            State current = Sample(state.Context);
            if (!object.ReferenceEquals(current.Active, admitted.Active) ||
                !current.SamePlan(admitted) || D.Enabled(current.Active) ||
                current.Context.RewardPanels(current.Active) != "PASS" || !Idle(current.Context.Client, false))
                throw new ProbeFailure("arena_disabled_panel_changed");
            state = current;
        }

        private static bool Idle(object client, bool afterClaim)
        {
            IDictionary handlers = D.Read(client, "m_handlers") as IDictionary;
            IList messages = D.Read(client, "messages") as IList;
            if (handlers == null || messages == null || messages.Count > 64 ||
                handlers.Contains(DailyArenaChestReceipt.Opcode) || handlers.Contains((ushort)1123)) return false;
            for (int i = 0; i < messages.Count; i++)
            {
                ushort op = Value<ushort>(messages[i], "op");
                if (op == DailyArenaChestReceipt.Opcode || op == 1123 || !afterClaim && op == 355 || DevelopmentNetwork.Tracked(op)) return false;
            }
            return DevelopmentNetwork.Sample(client).Idle;
        }

        private static void InvokeClose(Delegate close)
        { ((DevelopmentPublication)Delegate.CreateDelegate(typeof(DevelopmentPublication), close.Target, close.Method))(); }

        private void OpenPanel(string action, DailyArenaContextProvider freshContext, DevelopmentPublication publish, DevelopmentPublication commit)
        {
            State admitted = state;
            status = "WAITING"; reason = "arena_opening";
            pendingPanel = action == "open" ? state.Select : action == "enter" ? state.ArenaUi : state.Chest;
            navigating = true; deadlineStart = Environment.TickCount;
            if (action == "open")
            {
                MethodInfo open = D.Method(Model.GameType("MainUI"), "_battle_arena", Type.EmptyTypes, true, typeof(void));
                publish(); RecheckPublishedPlan(admitted, action, freshContext);
                CommitNavigation(state, commit); open.Invoke(null, null);
            }
            else if (action == "enter")
            {
                MethodInfo enter = D.Method(state.Select.GetType(), "arenaMyLoading", Type.EmptyTypes, true, typeof(void));
                publish(); RecheckPublishedPlan(admitted, action, freshContext);
                CommitNavigation(state, commit); enter.Invoke(null, null);
            }
            else
            {
                Type closeType = CloseType();
                Delegate close = Delegate.CreateDelegate(closeType, state.ArenaUi, NormalMethod(state.ArenaUi.GetType(), "SubClose", Type.EmptyTypes, false));
                MethodInfo load = NormalMethod(state.Chest.GetType(), "MyLoading", new Type[] { closeType }, true);
                publish(); RecheckPublishedPlan(admitted, action, freshContext);
                CommitNavigation(state, commit); D.Call(state.ArenaUi, "DisableAll");
                RecheckDisabledPlan(admitted);
                load.Invoke(state.Chest, new object[] { close });
            }
        }

        private static Type CloseType()
        {
            Type type = Model.FirstType("__BaseUI").GetField("OnClose", D.Instance).FieldType;
            if (!typeof(Delegate).IsAssignableFrom(type) || type.GetMethod("Invoke").ReturnType != typeof(void) ||
                type.GetMethod("Invoke").GetParameters().Length != 0) throw new ProbeFailure("arena_close_delegate_schema");
            return type;
        }

        private static MethodInfo NormalMethod(Type type, string name, Type[] args, bool isPublic)
        {
            MethodInfo method = D.Method(type, name, args, false, typeof(void));
            if (method.DeclaringType != type || method.IsPublic != isPublic) throw new ProbeFailure("arena_normal_method_schema:" + name);
            return method;
        }

        private void InvokeClaim(DailyArenaContextProvider freshContext, DevelopmentPublication publish, DevelopmentPublication commit)
        {
            if (mutationAttempted || state.Index < 0 || state.BonusTime < state.FreeSince || state.BonusTime > state.Now)
                throw new ProbeFailure("arena_free_branch_unavailable");
            bool chest = state.Active == state.Chest;
            Type arenaType = state.ArenaUi.GetType(), closeType = CloseType();
            if (chest)
            {
                Delegate panelClose = D.Read(state.Chest, "OnClose") as Delegate;
                if (panelClose == null || panelClose.GetInvocationList().Length != 1 || !object.ReferenceEquals(panelClose.Target, state.ArenaUi) ||
                    panelClose.Method != NormalMethod(arenaType, "SubClose", Type.EmptyTypes, false) &&
                    panelClose.Method != D.Method(arenaType, "EnableAll", Type.EmptyTypes, false, typeof(void)))
                    throw new ProbeFailure("arena_chest_panel_close_unowned");
                chestClose = panelClose;
            }
            MethodInfo normal = NormalMethod(arenaType, "cs_arena_bonusget_0x462", new Type[] { typeof(bool), typeof(int), closeType }, true);
            MethodInfo receive = NormalMethod(arenaType, "sc_arena_bonusget_0x462", new Type[] { typeof(MemoryStream), typeof(object) }, false);
            stockBonusClose = Delegate.CreateDelegate(closeType, state.Active, NormalMethod(state.Active.GetType(), "BonusGetClose", Type.EmptyTypes, false));
            State admitted = state;
            status = "WAITING"; reason = "arena_free_claim_published"; uiDisposition = "";
            publish();
            RecheckPublishedPlan(admitted, "claim", freshContext);
            claim = state;
            if (!OwnsClaimPanel(state.Context))
            { claim = null; throw new ProbeFailure("arena_claim_page_ownership_lost"); }
            exitPanel = state.Active; exitShownAt = state.ActiveShownAt;
            priorReward = state.Context.GrowthRewardDialog();
            priorRewardShownAt = priorReward == null ? default(DateTime) : ShownAt(priorReward);
            ArmActivityPush();
            if (chest)
            {
                if (!object.ReferenceEquals(D.Read(state.Chest, "OnClose"), chestClose))
                    throw new ProbeFailure("arena_chest_close_changed_after_publication");
                CommitNavigation(state, commit);
                D.Call(state.Chest, "Disable");
                RecheckDisabledPlan(admitted);
                if (!OwnsClaimPanel(state.Context) || !activity.StillInstalled())
                    throw new ProbeFailure("arena_claim_page_ownership_lost");
            }
            else commit();
            mutationAttempted = true; deadlineStart = Environment.TickCount;
            normal.Invoke(state.ArenaUi, new object[] { false, state.Index, stockBonusClose });
            object client = claim.Context.Client;
            IDictionary handlers = D.Read(client, "m_handlers") as IDictionary;
            ownHandler = handlers == null ? null : handlers[DailyArenaChestReceipt.Opcode];
            if (ownHandler == null || ownHandler.GetType() != Model.FirstType("game.net.tcp_base+Handler") ||
                Value<float>(ownHandler, "life") != 10f || !object.ReferenceEquals(D.Read(ownHandler, "arg"), stockBonusClose))
                throw new ProbeFailure("arena_callback_argument_unowned");
            Delegate original = D.Read(ownHandler, "handler") as Delegate;
            if (original == null || original.GetInvocationList().Length != 1 ||
                !object.ReferenceEquals(original.Target, state.ArenaUi) || original.Method != receive)
                throw new ProbeFailure("arena_callback_unowned");
            string why;
            State dispatched = claim;
            callback = DevelopmentCallbackCapture.InstallArenaChest(client, ownHandler, operation, sequence, original,
                delegate(byte[] bytes)
                {
                    State response = new State { Context = dispatched.Context };
                    if (Expired || !ReadBoundary(response) || !response.SameBoundary(dispatched))
                    { callbackBoundaryChanged = true; return; }
                    receipt = DailyArenaChestReceipt.Decode(bytes);
                }, out why);
            if (callback == null) throw new ProbeFailure(why);
            reason = "arena_matching_callback_armed";
        }

        private void ArmActivityPush()
        {
            object client = claim.Context.Client;
            IDictionary handlers = D.Read(client, "m_handlers") as IDictionary;
            object handler = handlers == null ? null : handlers[(ushort)355];
            Delegate original = handler == null ? null : D.Read(handler, "handler") as Delegate;
            string why;
            activity = DevelopmentCallbackCapture.InstallDailyActivityPush(client, handler, operation, sequence, original, ReadActivityPush, out why);
            if (activity == null || activity.Opcode != 355 || activity.OperationId != operation ||
                activity.Sequence != sequence || !activity.StillInstalled())
                throw new ProbeFailure(why == "" ? "arena_activity_callback_identity" : why);
        }

        internal void NotInvoked() { queuedState = null; }
        internal void Failed(string why)
        {
            if (settled) { autoUiAbandoned = true; reason = why; status = "CLAIMED"; uiDisposition = "unconfirmed"; }
            else if (mutationAttempted) Unknown(why);
            else Reject(why);
            RefreshRevision();
        }
        internal void Stop(string why)
        {
            if (stockBranchAdmitted)
            {
                if (deferredStop == null) deferredStop = why ?? "arena_stop_requested";
                return;
            }
            stopped = true; queuedState = null; navigating = false;
            if (status == "UNKNOWN") Detach();
            else if (Unresolved) Unknown(why);
            else if (status != "COMPLETED")
            { status = "STOPPED"; reason = why; if (settled) uiDisposition = "manual"; Detach(); }
            RefreshRevision();
        }
        private void Reject(string why) { queuedState = null; navigating = false; status = "REJECTED"; reason = why; Detach(); }
        private void Unknown(string why)
        {
            if (state != null) state.Action = "";
            status = "UNKNOWN"; reason = why; uiDisposition = "manual"; Detach();
        }
        private void Detach() { Detach(ref callback); Detach(ref activity); }

        private static void Detach(ref DevelopmentCallbackCapture capture)
        {
            if (capture != null && !capture.DisarmAndRestore().EndsWith("forward_only", StringComparison.Ordinal)) capture = null;
        }

        private bool ActionReady(string action)
        { return status == "READY" || status == "CLAIMED" && (action == "reward_confirm" || action == "close") && !autoUiAbandoned; }
        private string NextAction { get { return !stopped && state != null && ActionReady(state.Action) ? state.Action : ""; } }
        private string Content()
        {
            State s = state;
            return ",\"status\":" + D.Q(status) + ",\"reason\":" + D.Q(reason) + ",\"next_action\":" + D.Q(NextAction) +
                ",\"character_id\":" + D.Q(s == null || s.CharacterId == 0 ? "" : s.CharacterId.ToString(CultureInfo.InvariantCulture)) +
                ",\"operation_id\":" + D.Q(operation) + ",\"sequence\":" + sequence +
                ",\"rearmed_operation_id\":" + D.Q(rearmedOperation) + ",\"rearmed_sequence\":" + rearmedSequence +
                ",\"rearmed_revision\":" + rearmedRevision +
                ",\"activity_id\":" + ActivityId + ",\"current\":" + (s == null || s.Required == 0 ? "null" : s.Current.ToString(CultureInfo.InvariantCulture)) +
                ",\"required\":" + (s == null || s.Required == 0 ? "null" : s.Required.ToString(CultureInfo.InvariantCulture)) +
                ",\"refresh_cycle\":" + (s == null ? 0 : s.Cycle) + ",\"free_since\":" + (s == null ? 0 : s.FreeSince) +
                ",\"bonus_time\":" + (claim != null ? claim.BonusTime : s == null ? 0 : s.BonusTime) +
                ",\"invoked\":" + D.B(mutationAttempted) + ",\"unresolved\":" + D.B(Unresolved) +
                ",\"navigation_pending\":" + D.B(navigationOwner != null) +
                ",\"callback_returned\":" + D.B(callbackReturned) + ",\"settled\":" + D.B(settled) +
                ",\"activity_push_returned\":" + D.B(settled && activityDispatch != 0) +
                ",\"ui_disposition\":" + D.Q(uiDisposition) +
                ",\"gold_before\":" + (claim == null ? "null" : claim.Gold.ToString(CultureInfo.InvariantCulture)) +
                ",\"gold_after\":" + (afterCounter < 0 ? "null" : afterGold.ToString(CultureInfo.InvariantCulture)) +
                ",\"counter_before\":" + (claim == null ? "null" : claim.Current.ToString(CultureInfo.InvariantCulture)) +
                ",\"counter_after\":" + (afterCounter < 0 ? "null" : afterCounter.ToString(CultureInfo.InvariantCulture)) +
                ",\"receipt_opcode\":" + (receipt == null ? 0 : DailyArenaChestReceipt.Opcode) +
                ",\"receipt_result\":" + (receipt == null ? "null" : receipt.Result.ToString(CultureInfo.InvariantCulture)) +
                ",\"receipt_time\":" + (receipt == null ? 0 : receipt.Time) +
                ",\"free_rewards_remaining\":" + (s == null ? 0 : s.FreeCount) +
                ",\"paid_rewards_skipped\":" + (s == null ? 0 : s.PaidCount) +
                ",\"bonus_scan_complete\":" + D.B(s != null && s.BonusScanComplete) +
                ",\"bonus_rank\":" + (claim != null ? claim.Rank : s == null ? 0 : s.Rank) +
                ",\"receipt_money\":" + (receipt == null ? "null" : receipt.Money.ToString(CultureInfo.InvariantCulture)) +
                ",\"receipt_reputation\":" + (receipt == null ? "null" : receipt.Reputation.ToString(CultureInfo.InvariantCulture)) +
                ",\"receipt_gold_consume\":" + (receipt == null ? "null" : receipt.Gold.ToString(CultureInfo.InvariantCulture));
        }

        private void RefreshRevision()
        {
            string content = Content() + (state == null ? "" : state.ListStamp + ":" + state.ActivityRefresh + ":" +
                state.ListedCurrent + ":" + state.ListedRefresh + ":" +
                state.Gold + ":" + state.BoundGold + ":" + state.GoldConsumed + ":" +
                (state.Active == null ? 0 : state.Active.GetInstanceID()) + ":" +
                state.ActiveShownAt.Ticks + ":" + ownedRewardShownAt.Ticks);
            Transport connection = state == null ? null : state.Connection;
            bool changedConnection = connection == null ? revisionConnection != null : !connection.Same(revisionConnection);
            if (content != stamp || changedConnection)
            { revision = checked(revision + 1); stamp = content; revisionConnection = connection; }
        }
        internal string Json() { return "{\"schema\":1,\"revision\":" + revision + Content() + "}"; }
    }
}
