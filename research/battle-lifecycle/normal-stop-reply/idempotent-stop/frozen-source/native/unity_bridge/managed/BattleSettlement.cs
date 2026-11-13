using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using UnityEngine;

namespace JiangShen.AssistantRuntime
{
    internal delegate bool BattleSettlementAdmission();
    internal delegate void BattleSettlementEvent(string name, string fields);

    internal sealed class BattlePresentationOwnership
    {
        internal readonly BattleMode Mode;
        internal readonly string OperationId, TargetId;
        internal readonly Component Source;
        internal readonly object Target;
        internal readonly BattleSettlementAdmission Verify;
        internal BattlePresentationOwnership(BattleMode mode, string operationId, Component source,
            object target, string targetId, BattleSettlementAdmission verify)
        { Mode = mode; OperationId = operationId; Source = source; Target = target; TargetId = targetId; Verify = verify; }
    }

    // One owner for early battle-end requests. It changes presentation only;
    // result=0 is applied exclusively by the exact stock response callback.
    // result=1 is the stock acceleration rejection, never a settled outcome.
    internal sealed class BattleSettlement
    {
        private const ushort EndOpcode = 1032;
        private const int MaximumRequests = 6, ResponseMs = 12000, AvailabilityMs = 25000;
        private static BattleSettlement active;
        private readonly BattleSettlementEvent emit;
        private readonly BattleSettlementAdmission admitted;
        private Attempt attempt;
        private readonly ArrayList history = new ArrayList();
        private int sequence;
        private bool stopped;
        internal bool NeedsAdvance { get { return attempt != null && (!attempt.Opened || attempt.Error != ""); } }
        internal bool BlocksGuiInvocation { get { return attempt != null && Unresolved(attempt); } }

        private sealed class Attempt
        {
            internal Component Battle, Parent, ParentField, Root, Previous;
            internal object Report, Interpreter, Client, Character, Loader;
            internal DailyTransport Transport;
            internal ulong ReportId, CharacterId;
            internal int Thread, Requests, RetryTick, RetryDelay, Sequence, UnavailableTick, NativeObservedTick;
            internal bool UseEnd, Retry, Unavailable, NativeRequestStarted, ObservedEnd;
            internal BattleLifecycle State;
            internal BattlePresentationOwnership Ownership;
            internal object Embedded, SettledReceipt;
            internal object TargetOwner;
            internal Delegate Close;
            internal ushort SettledOpcode;
            internal ulong CurrentReportId;
            internal ulong TargetOwnerId;
            internal string NativeTargetKey, ViewType;
            internal byte[] AppliedBody;
            internal string Error { get { return State.Error; } set { State.Unknown(value); } }
            internal bool Applied { get { return State.Applied; } }
            internal bool Presented { get { return State.Presented; } }
            internal bool Opened { get { return State.Opened; } }
            internal Request Pending;
        }

        private sealed class Request
        {
            internal BattleSettlement Owner;
            internal Attempt Attempt;
            internal object Handler;
            internal FieldInfo Callback;
            internal Delegate Original, Wrapper;
            internal DevelopmentNormalCallback Forward;
            internal int Tick, Calls, Executing;
            internal bool Returned;
            private void Response(MemoryStream stream, object arg) { Owner.Response(this, stream, arg); }
            internal void Bind()
            {
                Forward = (DevelopmentNormalCallback)Delegate.CreateDelegate(typeof(DevelopmentNormalCallback), Original.Target, Original.Method);
                Wrapper = Delegate.CreateDelegate(Original.GetType(), this,
                    typeof(Request).GetMethod("Response", BindingFlags.NonPublic | BindingFlags.Instance));
                Callback.SetValue(Handler, Wrapper);
            }
        }

        internal BattleSettlement(BattleSettlementEvent events, BattleSettlementAdmission admission)
        {
            Attempt prior = active == null ? null : active.attempt;
            if (prior != null && Unresolved(prior))
                throw new ProbeFailure("battle_settlement_prior_owner_unresolved");
            emit = events; admitted = admission;
            if (active != null) history.AddRange(active.history);
            active = this;
        }

        internal static bool Owns(Component battle, object report)
        {
            Attempt a = active == null ? null : active.attempt;
            return a != null && ReferenceEquals(a.Battle, battle) && ReferenceEquals(a.Report, report);
        }

        // Queue observers can distinguish this exact pending request's explicit
        // rejection from an unowned/invalid authoritative settlement packet.
        internal static bool OwnsPending(object client, Component battle, object report)
        {
            BattleSettlement owner = active;
            Attempt a = owner == null ? null : owner.attempt;
            if (owner == null || a == null || a.Error != "" || a.Pending == null ||
                a.Pending.Returned || !ReferenceEquals(a.Client, client) || !Owns(battle, report)) return false;
            IDictionary handlers = SharedDevelopmentContext.Read(client, "m_handlers") as IDictionary;
            Request request = a.Pending;
            return handlers != null && ReferenceEquals(handlers[EndOpcode], request.Handler) &&
                ReferenceEquals(request.Callback.GetValue(request.Handler), request.Wrapper);
        }

        internal static byte[] AppliedResponse(Component battle, object report)
        {
            Attempt a = active == null ? null : active.attempt;
            return Owns(battle, report) && a.Applied && a.Error == "" ? a.AppliedBody : null;
        }
        internal static bool WasPresented(Component battle)
        { return active != null && active.attempt != null && ReferenceEquals(active.attempt.Battle, battle) && active.attempt.Presented; }
        internal static int RequestCount(Component battle)
        { return WasPresented(battle) ? active.attempt.Requests : 0; }

        internal static bool PresentOwned(Component battle)
        {
            if (active == null) return false;
            active.Advance(battle);
            return active.attempt != null && ReferenceEquals(active.attempt.Battle, battle) && active.attempt.Presented;
        }
        internal static bool PresentOwned(Component battle, BattlePresentationOwnership ownership)
        {
            BattleSettlement owner = active;
            if (owner == null || ownership == null) return false;
            owner.AdvanceOwned(battle, ownership);
            return owner.attempt != null && ReferenceEquals(owner.attempt.Battle, battle) &&
                SameOwnership(owner.attempt.Ownership, ownership) && owner.attempt.Presented;
        }

        internal static bool PresentSettled(Component battle, Component source, BattleMode mode,
            string operationId, object target, string targetId, object receipt, ushort opcode,
            ulong reportId, BattleSettlementAdmission verify)
        {
            BattleSettlement owner = active;
            if (owner == null || receipt == null || verify == null ||
                !SettledContract(mode, opcode, source) ||
                (mode != BattleMode.OwnDemon && reportId == 0)) return false;
            BattlePresentationOwnership ownership = new BattlePresentationOwnership(mode, operationId, source, target, targetId, verify);
            owner.AdvanceCore(battle, ownership, receipt, opcode, reportId);
            Attempt a = owner.attempt;
            return a != null && ReferenceEquals(a.Battle, battle) && ReferenceEquals(a.SettledReceipt, receipt) && a.Opened;
        }

        internal void Advance(Component battle) { Advance(battle, null); }
        internal void Advance(Component battle, Component reportSource)
        {
            // Observing an arbitrary native page is not ownership of its fight.
            // Legacy callers may only continue the report already bound by its adapter.
            ValidateOwnedAttempt();
            if (attempt == null || !ReferenceEquals(attempt.Battle, battle)) return;
            AdvanceCore(battle, attempt.Ownership, attempt.SettledReceipt, attempt.SettledOpcode, attempt.ReportId);
        }
        internal void AdvanceOwnedAttempt()
        {
            ValidateOwnedAttempt();
            Attempt a = attempt;
            if (a != null && Unresolved(a))
                AdvanceCore(a.Battle, a.Ownership, a.SettledReceipt, a.SettledOpcode, a.ReportId);
        }
        internal void ValidateOwnedAttempt()
        {
            Attempt a = attempt;
            if (a == null) return;
            if (a.Error != "") throw new ProbeFailure(a.Error);
            if (!Unresolved(a)) return;
            try
            {
                // Validate the owned report even when a fresh UI lookup cannot
                // see it. Surrender/replay/reset cannot become an early return
                // that silently admits a second fight.
                if (!Same(a)) Fail(a, "battle_settlement_owned_identity_changed");
                if (a.NativeRequestStarted && a.Pending == null &&
                    unchecked((uint)(Environment.TickCount - a.NativeObservedTick)) >= ResponseMs)
                    Fail(a, "battle_settlement_native_handler_unobserved_unknown_no_retry");
                if (a.Pending != null && !a.Pending.Returned &&
                    unchecked((uint)(Environment.TickCount - a.Pending.Tick)) >= ResponseMs)
                    Fail(a, "battle_settlement_response_unknown_no_retry");
                if (a.Pending != null && !a.Pending.Returned &&
                    Interlocked.CompareExchange(ref a.Pending.Executing, 0, 0) == 0 && Monitor.TryEnter(a.Client, 0))
                {
                    try
                    {
                        IDictionary handlers = SharedDevelopmentContext.Read(a.Client, "m_handlers") as IDictionary;
                        Request pending = a.Pending;
                        if (handlers == null || !ReferenceEquals(handlers[EndOpcode], pending.Handler) ||
                            !ReferenceEquals(pending.Callback.GetValue(pending.Handler), pending.Wrapper))
                            Fail(a, "battle_settlement_pending_handler_changed_unknown_no_retry");
                    }
                    finally { Monitor.Exit(a.Client); }
                }
                bool loading = SharedDevelopmentContext.Loading(a.Battle);
                bool started = (bool)SharedDevelopmentContext.Read(a.Battle, "bBattle_started");
                if (!started && !loading) Fail(a, "battle_settlement_owned_battle_reset_unknown");
                bool unavailable = loading || !started || !SharedDevelopmentContext.Visible(a.Battle);
                if (!unavailable) a.Unavailable = false;
                else if (!a.Unavailable) { a.Unavailable = true; a.UnavailableTick = Environment.TickCount; }
                else if (unchecked((uint)(Environment.TickCount - a.UnavailableTick)) >= AvailabilityMs)
                    Fail(a, "battle_settlement_owned_ui_unavailable_unknown");
            }
            catch
            {
                a.Error = "battle_settlement_owned_identity_unverifiable";
                throw new ProbeFailure(a.Error);
            }
        }
        internal void AdvanceOwned(Component battle, BattlePresentationOwnership ownership)
        { if (ownership != null) AdvanceCore(battle, ownership, null, 0, 0); }
        internal void ObserveOwned(Component battle, BattlePresentationOwnership ownership)
        {
            ValidateOwnedAttempt();
            if (ownership == null || battle == null || battle.GetType() != Model.GameType("BattleUI")) return;
            // Update may observe a native-finished battle before OnGUI. Claim
            // that exact adapter's outstanding settlement before its result can
            // be mistaken for a loss, without presenting or sending from Update.
            if ((bool)SharedDevelopmentContext.Read(battle, "bUseBattleEnd") &&
                (bool)SharedDevelopmentContext.Read(battle, "bSendBattleEnd")) return;
            AdvanceCore(battle, ownership, null, 0, 0, false);
        }

        private void AdvanceCore(Component battle, BattlePresentationOwnership ownership, object settledReceipt,
            ushort settledOpcode, ulong settledReportId)
        { AdvanceCore(battle, ownership, settledReceipt, settledOpcode, settledReportId, true); }
        private void AdvanceCore(Component battle, BattlePresentationOwnership ownership, object settledReceipt,
            ushort settledOpcode, ulong settledReportId, bool present)
        {
            ValidateOwnedAttempt();
            Attempt current = attempt;
            if (current != null && Unresolved(current))
            {
                if (battle != null && !ReferenceEquals(current.Battle, battle))
                    Fail(current, "battle_settlement_replaced_before_resolution");
                if (!SameOwnership(current.Ownership, ownership) ||
                    !ReferenceEquals(current.SettledReceipt, settledReceipt) || current.SettledOpcode != settledOpcode)
                    Fail(current, "battle_settlement_operation_or_receipt_changed");
                // The native callback may already have started the final result
                // while BattleUI is hidden/loading. Opening this exact result is
                // guarded by stock and our one-shot state, and completes before
                // any queued cleanup command can discard its owner.
                if (current.Applied) { if (present) { Present(current); Open(current); } return; }
                if (current.Unavailable) return;
            }
            if (battle == null || battle.GetType() != Model.GameType("BattleUI") ||
                !SharedDevelopmentContext.Visible(battle) || SharedDevelopmentContext.Loading(battle) ||
                !(bool)SharedDevelopmentContext.Read(battle, "bBattle_started") ||
                (bool)SharedDevelopmentContext.Read(battle, "bBattle_surrended") ||
                (bool)SharedDevelopmentContext.Read(battle, "m_is_cross")) return;
            object interpreter = SharedDevelopmentContext.Read(battle, "interpreter");
            object report = interpreter == null ? null : SharedDevelopmentContext.Read(interpreter, "m_report");
            if (report == null || !(bool)SharedDevelopmentContext.Method(battle.GetType(), "get_ArmyBehaviourCreated",
                Type.EmptyTypes, false, typeof(bool)).Invoke(battle, null)) return;
            if (attempt == null || !ReferenceEquals(attempt.Battle, battle) || !ReferenceEquals(attempt.Report, report))
            {
                if (attempt != null && Unresolved(attempt))
                    Fail(attempt, "battle_settlement_replaced_before_resolution");
                if (stopped || !admitted()) return;
                foreach (Attempt prior in history)
                    if ((HasEffects(prior) || prior.Error != "") &&
                        (ReferenceEquals(prior.Report, report) || settledReceipt != null && ReferenceEquals(prior.SettledReceipt, settledReceipt)))
                        throw new ProbeFailure("battle_settlement_previous_report_or_receipt_reused");
                Attempt next = Bind(battle, interpreter, report, ownership, settledReceipt, settledOpcode, settledReportId);
                if (next == null) return;
                next.Sequence = ++sequence;
                next.ObservedEnd = !present;
                attempt = next; history.Add(next);
                if (next.Error == "")
                {
                    try { BattleLaunch.ReportBound(ownership, next.Character, next.CharacterId, next.Battle, next.Report); }
                    catch { next.Error = "battle_settlement_launch_report_unverified_unknown"; throw; }
                }
            }
            Attempt a = attempt;
            if (!SameOwnership(a.Ownership, ownership) || !ReferenceEquals(a.SettledReceipt, settledReceipt) || a.SettledOpcode != settledOpcode)
                Fail(a, "battle_settlement_operation_or_receipt_changed");
            if (a.Error != "") throw new ProbeFailure(a.Error);
            if (!Same(a)) Fail(a, "battle_settlement_identity_changed");
            if (a.Applied) { if (present) { Present(a); Open(a); } return; }
            if (a.State.Path == BattleSettlementPath.RequestEnd)
                SharedDevelopmentContext.Selection(battle, "bNeedWaitForBattleEnd", true);
            if (a.Pending != null && !a.Pending.Returned)
            {
                if (unchecked((uint)(Environment.TickCount - a.Pending.Tick)) >= ResponseMs)
                    Fail(a, "battle_settlement_response_unknown_no_retry");
                if (!present || a.Presented) return;
            }
            if (stopped) return;
            if (!present && a.State.Path != BattleSettlementPath.RequestEnd) return;
            if (a.Retry && unchecked((uint)(Environment.TickCount - a.RetryTick)) < a.RetryDelay) return;
            if (!Monitor.TryEnter(a.Client, 0)) return;
            try
            {
                if (!Same(a)) Fail(a, "battle_settlement_identity_changed");
                // Admission controls new ownership. An already-owned fight may
                // finish while its mode is paused; Stop separately forbids sends.
                if (stopped) return;
                IDictionary handlers = SharedDevelopmentContext.Read(a.Client, "m_handlers") as IDictionary;
                IList messages = SharedDevelopmentContext.Read(a.Client, "messages") as IList;
                if (handlers == null || messages == null || messages.Count > 64) Fail(a, "battle_settlement_queue_schema");
                if (a.State.Path == BattleSettlementPath.RequestEnd)
                {
                    if (a.Pending != null && !a.Pending.Returned)
                    { Present(a); return; }
                    if (a.Retry && (a.Pending == null || !a.Pending.Returned || a.Pending.Calls != 1 ||
                        !ReferenceEquals(a.Pending.Callback.GetValue(a.Pending.Handler), a.Pending.Wrapper)))
                        Fail(a, "battle_settlement_rejection_handler_changed_no_retry");
                    foreach (object message in messages)
                        if (Convert.ToInt32(SharedDevelopmentContext.Read(message, "op"), CultureInfo.InvariantCulture) == EndOpcode)
                            Fail(a, "battle_settlement_unbound_response_pending");
                    if (a.NativeRequestStarted && a.Pending == null)
                    {
                        // Stock fight()/OnGUI can send first. Its exact, live
                        // one-shot slot is observable only while this client is
                        // locked and before any 0x408 response is queued. An
                        // absent, timed-out or replaced slot is UNKNOWN; never
                        // send a second request to discover what happened.
                        BindRequest(a, handlers[EndOpcode]);
                        a.Requests = 1;
                        Emit("battle_settlement_native_request_observed", a, "\"opcode\":1032");
                        if (present) Present(a);
                        return;
                    }
                    if (handlers.Contains(EndOpcode)) Fail(a, "battle_settlement_foreign_request_pending");
                    if (!present) return;
                    SharedDevelopmentContext.Selection(battle, "bSendBattleEnd", false);
                }
                Present(a);
                if (!Same(a)) Fail(a, "battle_settlement_post_presentation_identity_changed");
                if (a.State.Path == BattleSettlementPath.SettledResponse)
                {
                    if (!a.Ownership.Verify()) Fail(a, "battle_settlement_response_evidence_changed");
                    if (a.State.TryBeginApply(a.SettledReceipt))
                    {
                        a.State.CompleteApply(a.ReportId);
                        Emit("battle_settlement_applied", a, "\"result\":0,\"opcode\":" + a.SettledOpcode);
                    }
                    Open(a); return;
                }
                if (a.State.Path == BattleSettlementPath.EmbeddedEnd)
                {
                    object pending = SharedDevelopmentContext.Read(battle, "m_pktBattleEnd");
                    object packet = pending == null ? null : SharedDevelopmentContext.Read(pending, "pkt");
                    if (!ReferenceEquals(packet, a.Embedded) || !ValidPacket(a, packet))
                        Fail(a, "battle_embedded_settlement_identity_unknown");
                    if (a.State.TryBeginApply(packet))
                    {
                        SharedDevelopmentContext.Method(battle.GetType(), "BattleEnd", new[] { packet.GetType() }, false, typeof(void))
                            .Invoke(battle, new[] { packet });
                        CompleteStockApply(a, packet, null);
                    }
                    Open(a); return;
                }
                if (a.Requests >= MaximumRequests) Fail(a, "battle_settlement_explicit_rejection_retry_bound");
                SharedDevelopmentContext.Selection(battle, "bNeedWaitForBattleEnd", true);
                SharedDevelopmentContext.Selection(battle, "bSendBattleEnd", false);
                if (a.Requests == 0 && a.State.Mode == BattleMode.Campaign &&
                    Convert.ToInt32(SharedDevelopmentContext.Read(battle, "idcampaign"), CultureInfo.InvariantCulture) == 70)
                    SharedDevelopmentContext.Method(Model.FirstType("Global"), "ExternalCall",
                        new[] { typeof(string), typeof(object[]) }, true, typeof(void)).Invoke(null,
                            new object[] { "CampaignComplete", new object[] { SharedDevelopmentContext.Read(battle, "idtroop").ToString() } });
                a.Retry = false; a.Requests++;
                SharedDevelopmentContext.Call(battle, "cs_battle_end_0x408");
                BindRequest(a, handlers[EndOpcode]);
                Emit("battle_settlement_requested", a, "\"opcode\":1032");
            }
            catch
            {
                a.Error = "battle_settlement_request_or_presentation_unknown_no_retry";
                throw;
            }
            finally { Monitor.Exit(a.Client); }
        }

        private static bool SameOwnership(BattlePresentationOwnership first, BattlePresentationOwnership second)
        {
            return first != null && second != null && first.Mode == second.Mode && first.OperationId == second.OperationId &&
                first.TargetId == second.TargetId && ReferenceEquals(first.Source, second.Source) && ReferenceEquals(first.Target, second.Target);
        }
        private static bool HasEffects(Attempt a)
        { return a.State.PresentationStarted || a.State.ApplyStarted || a.Requests != 0 || a.NativeRequestStarted || a.ObservedEnd; }
        private static bool Unresolved(Attempt a)
        { return a.Error != "" || (!a.Applied || !a.Opened) && HasEffects(a); }
        private static void Fail(Attempt a, string reason)
        { a.Error = reason; throw new ProbeFailure(a.Error); }
        private void BindRequest(Attempt a, object handler)
        {
            if (handler == null) Fail(a, "battle_settlement_stock_handler_missing_unknown_no_retry");
            FieldInfo field = handler.GetType().GetField("handler", SharedDevelopmentContext.Instance);
            Delegate original = field == null ? null : field.GetValue(handler) as Delegate;
            float life = Convert.ToSingle(SharedDevelopmentContext.Read(handler, "life"), CultureInfo.InvariantCulture);
            if (original == null || original.GetInvocationList().Length != 1 || !ReferenceEquals(original.Target, a.Battle) ||
                original.Method.DeclaringType != Model.GameType("BattleUI") || original.Method.Name != "sc_battle_end_0x408" ||
                SharedDevelopmentContext.Read(handler, "arg") != null || life <= 0f || Single.IsNaN(life) || Single.IsInfinity(life))
                Fail(a, "battle_settlement_stock_callback_identity_unknown_no_retry");
            Request request = new Request { Owner = this, Attempt = a, Handler = handler, Callback = field,
                Original = original, Tick = Environment.TickCount };
            a.Pending = request; request.Bind();
        }
        private void Present(Attempt a)
        {
            if (!a.State.TryBeginPresentation()) return;
            try
            {
                SharedDevelopmentContext.Selection(a.Battle, "bBattle_fighting", false);
                SharedDevelopmentContext.Call(a.Interpreter, "SetArmyToFinalState");
                a.State.CompletePresentation();
                Emit("battle_presentation_skipped", a, "\"server_end_required\":" + (a.UseEnd ? "true" : "false"));
            }
            catch { a.Error = "battle_settlement_presentation_unknown_no_retry"; throw; }
        }
        private void Open(Attempt a)
        {
            if (!Same(a)) Fail(a, "battle_settlement_result_identity_changed");
            if (!a.State.TryBeginOpen()) return;
            try
            {
                SharedDevelopmentContext.Call(a.Battle, "OpenResultWindow");
                a.State.CompleteOpen();
            }
            catch { a.Error = "battle_settlement_result_open_unknown"; throw; }
        }

        private static bool SettledContract(BattleMode mode, ushort opcode, Component source)
        {
            if (source == null) return false;
            string expected = mode == BattleMode.Arena && opcode == 0x460 ? "ArenaUI" :
                mode == BattleMode.PointRace && opcode == 0x465 ? "PointraceUI" :
                mode == BattleMode.OwnDemon && opcode == 0x492 ? "DemonScript" : "";
            return expected != "" && source.GetType() == Model.GameType(expected);
        }
        private static Attempt Bind(Component battle, object interpreter, object report, BattlePresentationOwnership ownership,
            object settledReceipt, ushort settledOpcode, ulong settledReportId)
        {
            if (ownership == null || ownership.Source == null || ownership.Target == null || ownership.Verify == null ||
                String.IsNullOrEmpty(ownership.OperationId) || String.IsNullOrEmpty(ownership.TargetId) || !ownership.Verify()) return null;
            Component parentField = SharedDevelopmentContext.Read(battle, "m_parentUI") as Component;
            Component parent = ownership.Source;
            Component root = SharedDevelopmentContext.Read(battle, "rmg") as Component;
            GameObject fixedRoot = GameObject.Find("/Battle");
            Component fixedRootMessenger = fixedRoot == null ? null : fixedRoot.GetComponent(Model.FirstType("RootMessenger"));
            Component previous = SharedDevelopmentContext.Read(battle, "m_prevUI") as Component;
            object loader = SharedDevelopmentContext.Static(Model.FirstType("BaseAsset"), "lds");
            object character = SharedDevelopmentContext.Static(Model.FirstType("Global"), "character_");
            object client = DailyTransport.CurrentClient(character);
            DailyTransport transport = DailyTransport.Read(client);
            bool settled = settledReceipt != null;
            if (root == null || previous == null || loader == null || character == null || transport == null ||
                root.transform.root != root.transform || battle.transform.root != root.transform ||
                parent.transform.root != previous.transform || !ReferenceEquals(SharedDevelopmentContext.Read(parent, "rmg"), previous) ||
                !ReferenceEquals(SharedDevelopmentContext.Read(loader, "currmg"), previous) ||
                !ReferenceEquals(root, fixedRootMessenger) || !(bool)SharedDevelopmentContext.Read(loader, "isBattling")) return null;
            bool use = (bool)SharedDevelopmentContext.Read(battle, "bUseBattleEnd");
            ulong reportId = Convert.ToUInt64(SharedDevelopmentContext.Read(battle, "m_reportid"), CultureInfo.InvariantCulture);
            object embedded = SharedDevelopmentContext.Read(battle, "m_pktBattleEnd");
            embedded = embedded == null ? null : SharedDevelopmentContext.Read(embedded, "pkt");
            if (settled)
            {
                if (!SettledContract(ownership.Mode, settledOpcode, parent) || parentField != null || use ||
                    (bool)SharedDevelopmentContext.Read(battle, "bNeedWaitForBattleEnd") || reportId != settledReportId) return null;
                if (ownership.Mode != BattleMode.OwnDemon)
                {
                    Delegate callback = SharedDevelopmentContext.Read(battle, "OnClose") as Delegate;
                    if (callback == null || !ReferenceEquals(callback.Target, parent) || callback.Method.Name != "BattleClose" ||
                        callback.Method.DeclaringType != parent.GetType()) return null;
                }
            }
            else if (!ReferenceEquals(parentField, parent) || !NativeTarget(ownership, battle, character)) return null;
            BattleSettlementPath path = settled ? BattleSettlementPath.SettledResponse : use ? BattleSettlementPath.RequestEnd : BattleSettlementPath.EmbeddedEnd;
            Attempt a = new Attempt { Battle = battle, Parent = parent, ParentField = parentField, Root = root, Previous = previous,
                Report = report, Interpreter = interpreter, Client = client, Character = character, Loader = loader, Transport = transport,
                CharacterId = Convert.ToUInt64(SharedDevelopmentContext.Read(character, "id"), CultureInfo.InvariantCulture),
                ReportId = reportId, CurrentReportId = reportId, Thread = Thread.CurrentThread.ManagedThreadId, UseEnd = use,
                NativeRequestStarted = !settled && use && !(bool)SharedDevelopmentContext.Read(battle, "bSendBattleEnd"),
                NativeObservedTick = Environment.TickCount,
                Ownership = ownership, Embedded = embedded, SettledReceipt = settledReceipt, SettledOpcode = settledOpcode,
                Close = SharedDevelopmentContext.Read(battle, "OnClose") as Delegate,
                ViewType = SharedDevelopmentContext.Read(battle, "m_viewType").ToString() };
            a.NativeTargetKey = settled ? ownership.TargetId : TargetKey(ownership);
            if (ownership.Mode == BattleMode.FriendBuildingMonster)
            {
                a.TargetOwner = SharedDevelopmentContext.Static(Model.GameType("CapitalFriendUI"), "myfriend");
                if (a.TargetOwner == null) return null;
                a.TargetOwnerId = Convert.ToUInt64(SharedDevelopmentContext.Read(a.TargetOwner, "id"), CultureInfo.InvariantCulture);
                if (a.TargetOwnerId == 0) return null;
            }
            a.State = new BattleLifecycle(ownership.Mode, path, ownership.OperationId, character, a.CharacterId,
                ownership.Target, ownership.TargetId, parent, previous, battle, report, reportId);
            if (!settled && !use && (embedded == null || !ValidPacket(a, embedded)))
                a.Error = "battle_embedded_settlement_missing_or_invalid_unknown";
            return a;
        }

        private static bool NativeTarget(BattlePresentationOwnership owner, Component battle, object character)
        {
            string expected = owner.Mode == BattleMode.Campaign || owner.Mode == BattleMode.NormalChallenge ? "CampaignUI" :
                owner.Mode == BattleMode.Grave ? "GraveLevelUI" : owner.Mode == BattleMode.Trial ? "TrialLevelUI" :
                owner.Mode == BattleMode.TrialBoss ? "TrialUI" : owner.Mode == BattleMode.Divinity ? "DivinityUI" :
                owner.Mode == BattleMode.TrainingMonster ? "SealMonsterUI" :
                owner.Mode == BattleMode.OwnBuildingMonster || owner.Mode == BattleMode.FriendBuildingMonster ? "BuildingMonsterScript" : "";
            if (expected == "" || owner.Source.GetType() != Model.GameType(expected)) return false;
            if (owner.Mode == BattleMode.Campaign || owner.Mode == BattleMode.NormalChallenge)
            {
                object campaign = SharedDevelopmentContext.Read(owner.Source, "m_campaign");
                IDictionary troops = SharedDevelopmentContext.Read(owner.Source, "m_troops") as IDictionary;
                int type = Convert.ToInt32(SharedDevelopmentContext.Read(campaign, "idcampaign_type"), CultureInfo.InvariantCulture);
                byte troopId = Convert.ToByte(SharedDevelopmentContext.Read(battle, "idtroop"), CultureInfo.InvariantCulture);
                return type == (owner.Mode == BattleMode.Campaign ? 0 : 2) && troops != null &&
                    ReferenceEquals(troops[troopId], owner.Target) &&
                    Convert.ToUInt32(SharedDevelopmentContext.Read(owner.Target, "idcampaign"), CultureInfo.InvariantCulture) ==
                    Convert.ToUInt32(SharedDevelopmentContext.Read(battle, "idcampaign"), CultureInfo.InvariantCulture) &&
                    Convert.ToUInt32(SharedDevelopmentContext.Read(owner.Target, "idtroop"), CultureInfo.InvariantCulture) == troopId;
            }
            if (owner.Mode == BattleMode.TrialBoss)
                return ReferenceEquals(SharedDevelopmentContext.Read(character, "trialinfo"), owner.Target);
            if (owner.Mode == BattleMode.OwnBuildingMonster || owner.Mode == BattleMode.FriendBuildingMonster)
                return ReferenceEquals(SharedDevelopmentContext.Read(owner.Source, "Monster"), owner.Target) &&
                    (bool)SharedDevelopmentContext.Read(owner.Source, "m_bFriend") == (owner.Mode == BattleMode.FriendBuildingMonster);
            return ReferenceEquals(SharedDevelopmentContext.Read(owner.Source, "m_troop"), owner.Target);
        }
        private static bool Same(Attempt a)
        {
            return a.Battle != null && a.Parent != null && a.Root != null && a.Previous != null &&
                Thread.CurrentThread.ManagedThreadId == a.Thread &&
                ReferenceEquals(SharedDevelopmentContext.Static(Model.FirstType("Global"), "character_"), a.Character) &&
                Convert.ToUInt64(SharedDevelopmentContext.Read(a.Character, "id"), CultureInfo.InvariantCulture) == a.CharacterId &&
                ReferenceEquals(DailyTransport.CurrentClient(a.Character), a.Client) && a.Transport.Same(DailyTransport.Read(a.Client)) &&
                ReferenceEquals(SharedDevelopmentContext.Static(Model.FirstType("BaseAsset"), "lds"), a.Loader) &&
                ReferenceEquals(SharedDevelopmentContext.Read(a.Loader, "currmg"), a.Previous) &&
                ReferenceEquals(SharedDevelopmentContext.Read(a.Battle, "m_parentUI"), a.ParentField) &&
                ReferenceEquals(SharedDevelopmentContext.Read(a.Battle, "m_prevUI"), a.Previous) &&
                ReferenceEquals(SharedDevelopmentContext.Read(a.Battle, "rmg"), a.Root) &&
                a.Battle.transform.root == a.Root.transform && a.Parent.transform.root == a.Previous.transform &&
                ReferenceEquals(SharedDevelopmentContext.Read(a.Parent, "rmg"), a.Previous) &&
                ReferenceEquals(SharedDevelopmentContext.Read(a.Battle, "interpreter"), a.Interpreter) &&
                ReferenceEquals(SharedDevelopmentContext.Read(a.Interpreter, "m_report"), a.Report) &&
                Convert.ToUInt64(SharedDevelopmentContext.Read(a.Battle, "m_reportid"), CultureInfo.InvariantCulture) == a.CurrentReportId &&
                !(bool)SharedDevelopmentContext.Read(a.Battle, "bBattle_surrended") &&
                !(bool)SharedDevelopmentContext.Read(a.Battle, "m_is_cross") &&
                SharedDevelopmentContext.Read(a.Battle, "m_viewType").ToString() == a.ViewType &&
                (bool)SharedDevelopmentContext.Read(a.Battle, "bUseBattleEnd") == a.UseEnd &&
                System.Object.Equals(SharedDevelopmentContext.Read(a.Battle, "OnClose"), a.Close) &&
                (a.State.Path == BattleSettlementPath.SettledResponse ? a.Ownership.Verify() :
                    NativeTarget(a.Ownership, a.Battle, a.Character) && TargetKey(a.Ownership) == a.NativeTargetKey) &&
                (a.State.Mode != BattleMode.FriendBuildingMonster ||
                    ReferenceEquals(SharedDevelopmentContext.Static(Model.GameType("CapitalFriendUI"), "myfriend"), a.TargetOwner) &&
                    Convert.ToUInt64(SharedDevelopmentContext.Read(a.TargetOwner, "id"), CultureInfo.InvariantCulture) == a.TargetOwnerId) &&
                a.Ownership.Verify();
        }

        private static string TargetKey(BattlePresentationOwnership owner)
        {
            string field = owner.Mode == BattleMode.TrialBoss ? "bossid" :
                owner.Mode == BattleMode.OwnBuildingMonster || owner.Mode == BattleMode.FriendBuildingMonster ? "id" : "idtroop";
            return Convert.ToUInt64(SharedDevelopmentContext.Read(owner.Target, field), CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        }

        private static bool ValidPacket(Attempt a, object packet)
        {
            if (packet == null || Convert.ToUInt32(SharedDevelopmentContext.Read(packet, "_result"), CultureInfo.InvariantCulture) != 0 ||
                Convert.ToUInt64(SharedDevelopmentContext.Read(packet, "_idbattle_report"), CultureInfo.InvariantCulture) == 0) return false;
            // The stock mode callback applies packet.iswin directly. For these
            // attacker-owned reports, LEFT_WIN is the only win; LEFT_LOSE and
            // DRAW are non-wins. Optional iswin legitimately defaults to false.
            // This also runs for embedded objects immediately before BattleEnd;
            // it does not invent evidence about their original wire presence.
            int reportResult = Convert.ToInt32(SharedDevelopmentContext.Read(a.Report, "_result_status"), CultureInfo.InvariantCulture);
            if (reportResult < 1 || reportResult > 3 ||
                (bool)SharedDevelopmentContext.Read(packet, "_iswin") != (reportResult == 1)) return false;
            uint type = Convert.ToUInt32(SharedDevelopmentContext.Read(packet, "_type"), CultureInfo.InvariantCulture);
            BattleMode mode = a.State.Mode;
            uint expected = mode == BattleMode.Campaign || mode == BattleMode.NormalChallenge ? 0u :
                mode == BattleMode.OwnBuildingMonster || mode == BattleMode.FriendBuildingMonster ? 1u :
                mode == BattleMode.Trial ? 2u : mode == BattleMode.TrialBoss ? 3u : mode == BattleMode.Grave ? 4u :
                mode == BattleMode.Divinity ? 5u : mode == BattleMode.TrainingMonster ? 7u : UInt32.MaxValue;
            if (type != expected) return false;
            if (type == 0)
                return Convert.ToUInt32(SharedDevelopmentContext.Read(packet, "_idcampaign"), CultureInfo.InvariantCulture) ==
                    Convert.ToUInt32(SharedDevelopmentContext.Read(a.Battle, "idcampaign"), CultureInfo.InvariantCulture) &&
                    Convert.ToUInt32(SharedDevelopmentContext.Read(packet, "_idtroop"), CultureInfo.InvariantCulture) ==
                    Convert.ToUInt32(SharedDevelopmentContext.Read(a.Battle, "idtroop"), CultureInfo.InvariantCulture);
            if (type == 1)
                return Convert.ToUInt64(SharedDevelopmentContext.Read(packet, "_idfriend"), CultureInfo.InvariantCulture) == a.TargetOwnerId &&
                    Convert.ToUInt32(SharedDevelopmentContext.Read(packet, "_idmonster"), CultureInfo.InvariantCulture) ==
                    Convert.ToUInt32(SharedDevelopmentContext.Read(a.State.Target, "id"), CultureInfo.InvariantCulture);
            return true; // Other stock 0x408 packets carry type, not a target ID.
        }
        private static object ParsePacket(byte[] body)
        {
            // Native protobuf deserialization alone accepts missing required
            // nested Treasure fields. Scan the pinned complete schema on the
            // detached original bytes before any successful stock mutation.
            return BattleReplyPreflight.Deserialize(body, "game.net.data.StoC.SCBattleEnd_0x408");
        }
        private void CompleteStockApply(Attempt a, object packet, byte[] body)
        {
            ulong reportId = Convert.ToUInt64(SharedDevelopmentContext.Read(packet, "_idbattle_report"), CultureInfo.InvariantCulture);
            if (Convert.ToUInt64(SharedDevelopmentContext.Read(a.Battle, "m_reportid"), CultureInfo.InvariantCulture) != reportId ||
                (bool)SharedDevelopmentContext.Read(a.Battle, "bUseBattleEnd") ||
                (bool)SharedDevelopmentContext.Read(a.Battle, "bNeedWaitForBattleEnd"))
                Fail(a, "battle_settlement_stock_application_unverified");
            Delegate close = SharedDevelopmentContext.Read(a.Battle, "OnClose") as Delegate;
            if (close != null && (!ReferenceEquals(close.Target, a.Parent) ||
                close.Method.DeclaringType != a.Parent.GetType() ||
                close.Method.Name != "BonusOpen" && close.Method.Name != "BonusClose" && close.Method.Name != "FailedOpen"))
                Fail(a, "battle_settlement_stock_close_owner_changed");
            a.Close = close;
            a.CurrentReportId = reportId; a.UseEnd = false;
            // Native application may synchronously invoke mode code. Its return
            // is not proof that the same character, transport, page and target
            // still own this report; validate the complete post-callback state.
            if (!Same(a)) Fail(a, "battle_settlement_post_callback_identity_changed");
            a.State.CompleteApply(reportId);
            a.AppliedBody = body; a.Retry = false;
            Emit("battle_settlement_applied", a, "\"result\":0,\"opcode\":1032");
        }

        private void Response(Request request, MemoryStream stream, object arg)
        {
            // A duplicate notification is a no-op, even after a successful callback.
            if (Interlocked.CompareExchange(ref request.Calls, 1, 0) != 0) return;
            Interlocked.Increment(ref request.Executing);
            Attempt a = request.Attempt;
            bool locked = false, validSuccess = false;
            byte[] body = null; object packet = null;
            try
            {
                try
                {
                    locked = Monitor.TryEnter(a.Client, 0);
                    IDictionary handlers = locked ? SharedDevelopmentContext.Read(a.Client, "m_handlers") as IDictionary : null;
                    if (a.Error != "" || !locked || handlers == null || handlers.Contains(EndOpcode) || arg != null ||
                        !ReferenceEquals(a.Pending, request) || !ReferenceEquals(request.Callback.GetValue(request.Handler), request.Wrapper) ||
                        !Same(a) || stream == null || stream.GetType() != typeof(MemoryStream) ||
                        !stream.CanRead || stream.Position != 0 || stream.Length < 1 || stream.Length > 65536)
                        a.Error = "battle_settlement_callback_unknown_no_retry";
                    else
                    {
                        body = stream.ToArray();
                        uint result = Result(body);
                        if (result == 1)
                        {
                            // A rejection authorizes another request, so its
                            // complete pinned schema must be valid too. Optional
                            // target/report/iswin fields may retain defaults.
                            packet = ParsePacket(body);
                            // Only this exact explicit rejection is retryable.
                            // Stock treats it as final; suppress that acceleration
                            // branch so the true settlement remains outstanding.
                            request.Returned = true;
                            a.Retry = !stopped; a.RetryTick = Environment.TickCount;
                            a.RetryDelay = 1000 << Math.Min(a.Requests - 1, 4);
                            if (stopped) a.Error = "battle_settlement_stopped_after_rejection_unknown";
                            else if (a.Requests >= MaximumRequests) a.Error = "battle_settlement_explicit_rejection_retry_bound";
                            Emit("battle_settlement_rejected_waiting", a, "\"result\":1,\"retry_after_ms\":" + a.RetryDelay);
                        }
                        else if (result == 0)
                        {
                            packet = ParsePacket(body);
                            if (!ValidPacket(a, packet)) a.Error = "battle_settlement_packet_mode_or_target_mismatch";
                            else
                            {
                                validSuccess = a.State.TryBeginApply(request);
                                if (!validSuccess) a.Error = "battle_settlement_apply_state_unknown_no_retry";
                            }
                        }
                        else a.Error = "battle_settlement_unexpected_result_no_retry";
                    }
                }
                catch (Exception) { a.Error = "battle_settlement_response_parse_or_identity_unknown_no_retry"; }
                if (validSuccess && a.Error == "")
                {
                    // Stock mutates mode/target business state before clearing
                    // settlement flags. Forward only a proved success for this
                    // exact live owner; untrusted opcode-only replies must not
                    // reach that mutation boundary. The valid original stream
                    // and argument remain untouched, including after Stop.
                    try { request.Forward(stream, arg); }
                    catch { a.Error = "battle_settlement_original_callback_threw"; throw; }
                    request.Returned = true;
                    if (validSuccess && a.Error == "") CompleteStockApply(a, packet, body);
                }
            }
            finally
            {
                request.Returned = true;
                if (locked) Monitor.Exit(a.Client);
                Interlocked.Decrement(ref request.Executing);
            }
        }

        // Required result field, one occurrence, uint32 and a complete bounded
        // protobuf envelope. Missing/default fields are not an explicit reply.
        private static uint Result(byte[] bytes)
        {
            int position = 0, count = 0; uint result = 0;
            uint identities = 0;
            while (position < bytes.Length)
            {
                ulong tag = Varint(bytes, ref position);
                if (tag < 8 || tag > UInt32.MaxValue) throw new FormatException();
                int wire = (int)(tag & 7), field = (int)(tag >> 3);
                if (field == 1 || field == 2 || field == 3 || field == 4 || field == 5 || field == 6 || field == 15 || field == 17)
                {
                    uint bit = 1u << field;
                    if (wire != 0 || (identities & bit) != 0) throw new FormatException();
                    identities |= bit;
                }
                if (field == 1)
                {
                    if (wire != 0 || ++count != 1) throw new FormatException();
                    ulong value = Varint(bytes, ref position);
                    if (value > UInt32.MaxValue) throw new FormatException();
                    result = (uint)value;
                }
                else if (wire == 0) Varint(bytes, ref position);
                else
                {
                    ulong length = wire == 1 ? 8uL : wire == 5 ? 4uL : wire == 2 ? Varint(bytes, ref position) : UInt64.MaxValue;
                    if (length > (ulong)(bytes.Length - position)) throw new FormatException();
                    position += (int)length;
                }
            }
            if (count != 1) throw new FormatException();
            return result;
        }
        private static ulong Varint(byte[] bytes, ref int position)
        {
            ulong value = 0;
            for (int shift = 0; shift < 70; shift += 7)
            {
                if (position >= bytes.Length) throw new FormatException();
                byte next = bytes[position++];
                if (shift == 63 && next > 1) throw new FormatException();
                value |= (ulong)(next & 127) << shift;
                if ((next & 128) == 0) return value;
            }
            throw new FormatException();
        }
        private void Emit(string name, Attempt a, string fields)
        {
            emit(name, "\"battle_skip_sequence\":" + a.Sequence + ",\"battle_instance_id\":" + a.Battle.GetInstanceID() +
                ",\"initial_report_id\":" + a.ReportId.ToString(CultureInfo.InvariantCulture) + ",\"requests\":" + a.Requests + "," + fields);
        }
        internal bool Stop()
        {
            stopped = true;
            Attempt a = attempt;
            if (a == null) return true;
            // Keep the exact wrapper for an already-owned pending request. It
            // still forwards at most once; stopping never sends a new request
            // and never hands unresolved ownership to a different callback.
            Request request = a.Pending;
            if (request != null && (!request.Returned || Interlocked.CompareExchange(ref request.Executing, 0, 0) != 0))
                return false;
            return (a.Applied || !HasEffects(a)) && a.Error == "";
        }
    }
}
