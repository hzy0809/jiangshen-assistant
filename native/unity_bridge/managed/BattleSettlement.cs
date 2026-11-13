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
        internal bool NeedsAdvance { get { return attempt != null && (!attempt.Opened || attempt.Error != "" ||
            (attempt.NativeRejected || attempt.PolicyProcessed) && !attempt.NativeAdmissionClosed); } }
        internal bool BlocksGuiInvocation { get { return attempt != null && Unresolved(attempt); } }

        private sealed class Attempt
        {
            internal Component Battle, Parent, ParentField, Root, Previous, SourceRoot;
            internal object Report, Interpreter, Client, Character, Loader;
            internal DailyTransport Transport;
            internal ulong ReportId, CharacterId;
            internal int Thread, Requests, RetryTick, RetryDelay, Sequence, UnavailableTick, NativeObservedTick;
            internal bool UseEnd, Retry, Unavailable, NativeRequestStarted, ObservedEnd;
            internal bool NativeRejected, PolicyProcessed, NativeAdmissionClosed;
            internal BattleLifecycle State;
            internal BattlePresentationOwnership Ownership;
            internal NativeBattleStartTicket DurableTicket;
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
        internal static bool EliteNativePageFinished(string operation)
        {
            Attempt a = active == null ? null : active.attempt;
            return a != null && a.Ownership.Mode == BattleMode.EliteChallenge &&
                a.Ownership.OperationId == operation && a.NativeAdmissionClosed && a.Error == "";
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
        internal static byte[] AppliedDivinityLossResponse(Component battle, object report, BattlePresentationOwnership ownership)
        {
            Attempt a = active == null ? null : active.attempt;
            // AppliedBody is retained only after the original 0x408 callback
            // returned and its stock projection passed CompleteStockApply.
            // A battle/report match alone cannot adopt a different operation.
            if (a == null || ownership == null || ownership.Mode != BattleMode.Divinity ||
                !Owns(battle, report) || !a.Applied || a.Error != "" || a.AppliedBody == null ||
                a.State.Mode != BattleMode.Divinity || a.State.Path != BattleSettlementPath.RequestEnd ||
                !SameOwnership(a.Ownership, ownership) || !Same(a) ||
                Convert.ToInt32(SharedDevelopmentContext.Read(a.Report, "_result_status"), CultureInfo.InvariantCulture) != 2)
                return null;
            return a.AppliedBody;
        }
        internal static byte[] AppliedCampaignLossResponse(Component battle, object report, BattlePresentationOwnership ownership)
        {
            Attempt a = active == null ? null : active.attempt;
            // Export the original response only for this operation's applied
            // RequestEnd loss. UI/result visibility cannot supply this proof.
            if (a == null || ownership == null ||
                (ownership.Mode != BattleMode.Campaign && ownership.Mode != BattleMode.NormalChallenge) ||
                !Owns(battle, report) || !a.Applied || a.Error != "" ||
                a.AppliedBody == null || a.AppliedBody.Length == 0 ||
                a.State.Mode != ownership.Mode || a.State.Path != BattleSettlementPath.RequestEnd ||
                a.CurrentReportId != 0 || !SameOwnership(a.Ownership, ownership) || !Same(a) ||
                Convert.ToInt32(SharedDevelopmentContext.Read(a.Report, "_result_status"), CultureInfo.InvariantCulture) != 2)
                return null;
            return a.AppliedBody;
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
            object interpreter = SharedDevelopmentContext.Read(battle, "interpreter");
            if (!ReferenceEquals(interpreter, attempt.Interpreter) || interpreter == null ||
                !ReferenceEquals(SharedDevelopmentContext.Read(interpreter, "m_report"), attempt.Report)) return;
            // BattleUI survives across modes. A completed owner must not offer
            // its old receipt for a new fight while that adapter is binding.
            if (attempt.Opened && !Unresolved(attempt)) return;
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
                if (current.Applied)
                { if (present) { Present(current); Open(current); FinishEliteNativePage(current); } return; }
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
                    try { next.DurableTicket = BattleLaunch.ReportBound(ownership, next.Character, next.CharacterId, next.Battle, next.Report); }
                    catch { next.Error = "battle_settlement_launch_report_unverified_unknown"; throw; }
                }
            }
            Attempt a = attempt;
            if (!SameOwnership(a.Ownership, ownership) || !ReferenceEquals(a.SettledReceipt, settledReceipt) || a.SettledOpcode != settledOpcode)
                Fail(a, "battle_settlement_operation_or_receipt_changed");
            if (a.Error != "") throw new ProbeFailure(a.Error);
            if (!Same(a)) Fail(a, "battle_settlement_identity_changed");
            if (a.Applied) { if (present) { Present(a); Open(a); FinishEliteNativePage(a); } return; }
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
                        ConfirmDurable(a,
                            "stock-settled:" + a.SettledOpcode.ToString(CultureInfo.InvariantCulture) +
                            ":report:" + a.ReportId.ToString(CultureInfo.InvariantCulture));
                        Emit("battle_settlement_applied", a, "\"result\":0,\"opcode\":" + a.SettledOpcode);
                    }
                    Open(a); return;
                }
                if (a.State.Path == BattleSettlementPath.EmbeddedEnd)
                {
                    object pending = SharedDevelopmentContext.Read(battle, "m_pktBattleEnd");
                    object packet = pending == null ? null : SharedDevelopmentContext.Read(pending, "pkt");
                    if (a.NativeRejected || a.PolicyProcessed)
                    {
                        if (!ReferenceEquals(packet, a.Embedded)) Fail(a, "elite_native_packet_replaced");
                        if (a.State.TryBeginApply(packet ?? a.Report))
                        {
                            if (a.NativeRejected)
                            {
                                DateTime before = DateTime.Now;
                                SharedDevelopmentContext.Method(battle.GetType(), "BattleEnd", new[] { packet.GetType() }, false, typeof(void))
                                    .Invoke(battle, new[] { packet });
                                AcknowledgeAccelerationWarning(before);
                            }
                            // Processing the owned native page is not an
                            // accepted settlement and never supplies a win.
                            a.CurrentReportId = Convert.ToUInt64(SharedDevelopmentContext.Read(battle, "m_reportid"), CultureInfo.InvariantCulture);
                            a.Close = SharedDevelopmentContext.Read(battle, "OnClose") as Delegate;
                            a.State.CompleteApply(a.CurrentReportId);
                            Emit(a.NativeRejected ? "battle_native_rejection_applied" : "battle_processed_without_receipt", a,
                                a.NativeRejected ? "\"result\":1" : "\"result\":null");
                        }
                        Open(a); FinishEliteNativePage(a); return;
                    }
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
        { return a.Error != "" || (a.NativeRejected || a.PolicyProcessed) && !a.NativeAdmissionClosed ||
            (!a.Applied || !a.Opened) && HasEffects(a); }
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

        private static void AcknowledgeAccelerationWarning(DateTime before)
        {
            Component box = SharedDevelopmentContext.Static(Model.FirstType("__BaseUI"), "msgBox") as Component;
            string title = (string)SharedDevelopmentContext.Method(Model.FirstType("__BaseUI"), "load_str",
                new[] { typeof(uint) }, true, typeof(string)).Invoke(null, new object[] { (uint)20090 });
            string message = (string)SharedDevelopmentContext.Method(Model.FirstType("__BaseUI"), "get_string",
                new[] { typeof(string) }, true, typeof(string)).Invoke(null, new object[] { "accel_warning" });
            if (box == null || !SharedDevelopmentContext.Visible(box) || SharedDevelopmentContext.Loading(box) ||
                (DateTime)SharedDevelopmentContext.Read(box, "visible_time") < before ||
                SharedDevelopmentContext.Read(box, "Title") as string != title || SharedDevelopmentContext.Read(box, "Message") as string != message ||
                SharedDevelopmentContext.Read(box, "OnOK") != null || SharedDevelopmentContext.Read(box, "OnClose") != null ||
                SharedDevelopmentContext.Read(box, "old_rmg") != null)
                throw new ProbeFailure("elite_native_acceleration_warning_identity_unverified");
            // The stock three-argument setMessage installs no callbacks or
            // owner. Its normal button only hides this exact warning.
            SharedDevelopmentContext.Method(box.GetType(), "ButtonOK", new[] { typeof(bool) }, false, typeof(void))
                .Invoke(box, new object[] { false });
        }

        private static void FinishEliteNativePage(Attempt a)
        {
            if (!(a.NativeRejected || a.PolicyProcessed) || a.NativeAdmissionClosed || !a.Opened || !Same(a)) return;
            if ((bool)SharedDevelopmentContext.Read(a.Battle, "bBattle_fighting") ||
                (bool)SharedDevelopmentContext.Read(a.Battle, "bUseBattleEnd") ||
                (bool)SharedDevelopmentContext.Read(a.Battle, "bNeedWaitForBattleEnd") ||
                SharedDevelopmentContext.Read(a.Battle, "m_pktBattleEnd") != null) return;
            IDictionary handlers = SharedDevelopmentContext.Read(a.Client, "m_handlers") as IDictionary;
            IList messages = SharedDevelopmentContext.Read(a.Client, "messages") as IList;
            IList sends = SharedDevelopmentContext.Read(a.Client, "m_sendDatas") as IList;
            if (handlers == null || handlers.Contains(EndOpcode) || messages == null || messages.Count != 0 ||
                sends == null || sends.Count != 0 || DevelopmentCallbackCapture.AnyCallbackExecuting) return;
            Component result = a.Battle.GetComponent(Model.GameType("BattleNormalResultUI"));
            if (result == null || !SharedDevelopmentContext.Visible(result) || SharedDevelopmentContext.Loading(result) ||
                !ReferenceEquals(SharedDevelopmentContext.Read(result, "battle_ui"), a.Battle) ||
                (bool)SharedDevelopmentContext.Read(result, "m_battle_view_ui") ||
                Convert.ToUInt64(SharedDevelopmentContext.Read(result, "m_reportid"), CultureInfo.InvariantCulture) != a.CurrentReportId) return;
            // Close only this admission generation, with its actual evidence
            // class. This is not a server victory or a reconstructed receipt.
            ConfirmDurable(a, a.NativeRejected ? "native-acceleration-rejection:owned-result-ended" :
                "user-policy-processed-without-receipt:owned-result-ended");
            a.NativeAdmissionClosed = true;
        }

        private static bool SettledContract(BattleMode mode, ushort opcode, Component source)
        {
            if (source == null) return false;
            string expected = mode == BattleMode.Arena && opcode == 0x460 ? "ArenaUI" :
                mode == BattleMode.PointRace && opcode == 0x465 ? "PointraceUI" :
                mode == BattleMode.OwnDemon && opcode == 0x492 ? "DemonScript" : "";
            return expected != "" && source.GetType() == Model.GameType(expected);
        }
        private static bool ArenaSourceIdentity(Component source, Component sourceRoot)
        {
            GameObject fixedUi = GameObject.Find("/Root/RootUI"), fixedRoot = GameObject.Find("/Root");
            Component messenger = fixedRoot == null ? null : fixedRoot.GetComponent("RootMessenger");
            return source != null && source.GetType() == Model.GameType("ArenaUI") && fixedUi != null &&
                source.gameObject == fixedUi && object.ReferenceEquals(fixedUi.GetComponent("ArenaUI"), source) &&
                sourceRoot != null && sourceRoot.GetType() == Model.FirstType("RootMessenger") &&
                object.ReferenceEquals(sourceRoot, messenger) && sourceRoot.transform.root == sourceRoot.transform &&
                source.transform.root == sourceRoot.transform &&
                object.ReferenceEquals(SharedDevelopmentContext.Read(source, "rmg"), sourceRoot) &&
                (string)SharedDevelopmentContext.Read(sourceRoot, "LevelID") == "Root";
        }
        internal static bool TrialBossSourceIdentity(Component source, Component sourceRoot)
        {
            GameObject fixedUi = GameObject.Find("/Root/RootUI"), fixedRoot = GameObject.Find("/Root");
            Component messenger = fixedRoot == null ? null : fixedRoot.GetComponent(Model.FirstType("RootMessenger"));
            Component globalUi = fixedUi == null ? null : fixedUi.GetComponent(Model.GameType("RootUI"));
            return source != null && source.GetType() == Model.GameType("TrialUI") && fixedUi != null &&
                source.gameObject == fixedUi && ReferenceEquals(fixedUi.GetComponent(Model.GameType("TrialUI")), source) &&
                sourceRoot != null && sourceRoot.GetType() == Model.FirstType("RootMessenger") &&
                ReferenceEquals(sourceRoot, messenger) && sourceRoot.transform.root == sourceRoot.transform &&
                source.transform.root == sourceRoot.transform && globalUi != null &&
                ReferenceEquals(SharedDevelopmentContext.Read(globalUi, "rmg"), sourceRoot) &&
                ReferenceEquals(SharedDevelopmentContext.Read(source, "rmg"), sourceRoot) &&
                (string)SharedDevelopmentContext.Read(sourceRoot, "LevelID") == "Root";
        }
        private static bool SourceIdentity(Attempt a)
        {
            if (a.State.Path == BattleSettlementPath.SettledResponse && a.State.Mode == BattleMode.Arena)
                return ArenaSourceIdentity(a.Parent, a.SourceRoot);
            if (a.State.Mode == BattleMode.TrialBoss)
                return TrialBossSourceIdentity(a.Parent, a.SourceRoot);
            return a.Parent.transform.root == a.Previous.transform &&
                ReferenceEquals(SharedDevelopmentContext.Read(a.Parent, "rmg"), a.Previous);
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
            bool arenaSource = settled && ownership.Mode == BattleMode.Arena && settledOpcode == 0x460;
            bool trialBossSource = !settled && ownership.Mode == BattleMode.TrialBoss;
            Component sourceRoot = arenaSource || trialBossSource ? SharedDevelopmentContext.Read(parent, "rmg") as Component : previous;
            if (arenaSource && !ArenaSourceIdentity(parent, sourceRoot))
                throw new ProbeFailure("battle_settlement_arena_source_root_identity_mismatch");
            if (root == null || previous == null || loader == null || character == null || transport == null ||
                root.transform.root != root.transform || battle.transform.root != root.transform ||
                (arenaSource ? !ArenaSourceIdentity(parent, sourceRoot) : trialBossSource ? !TrialBossSourceIdentity(parent, sourceRoot) :
                    parent.transform.root != previous.transform || !ReferenceEquals(SharedDevelopmentContext.Read(parent, "rmg"), previous)) ||
                !ReferenceEquals(SharedDevelopmentContext.Read(loader, "currmg"), previous) ||
                !ReferenceEquals(root, fixedRootMessenger) || !(bool)SharedDevelopmentContext.Read(loader, "isBattling")) return null;
            bool use = (bool)SharedDevelopmentContext.Read(battle, "bUseBattleEnd");
            ulong reportId = Convert.ToUInt64(SharedDevelopmentContext.Read(battle, "m_reportid"), CultureInfo.InvariantCulture);
            object embedded = SharedDevelopmentContext.Read(battle, "m_pktBattleEnd");
            embedded = embedded == null ? null : SharedDevelopmentContext.Read(embedded, "pkt");
            if (ownership.Mode == BattleMode.EliteChallenge && !settled && !use && embedded == null &&
                (bool)SharedDevelopmentContext.Read(battle, "bNeedWaitForBattleEnd")) return null;
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
            Attempt a = new Attempt { Battle = battle, Parent = parent, ParentField = parentField, Root = root, Previous = previous, SourceRoot = sourceRoot,
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
            a.NativeRejected = ownership.Mode == BattleMode.EliteChallenge && !settled && !use && embedded != null &&
                Convert.ToUInt32(SharedDevelopmentContext.Read(embedded, "_result"), CultureInfo.InvariantCulture) == 1 &&
                BattleLaunch.NativeEliteRejection(ownership.OperationId, 0);
            a.PolicyProcessed = ownership.Mode == BattleMode.EliteChallenge && !settled && !use && embedded == null;
            if (!settled && !use && !a.NativeRejected && !a.PolicyProcessed && (embedded == null || !ValidPacket(a, embedded)))
                a.Error = "battle_embedded_settlement_missing_or_invalid_unknown";
            return a;
        }

        private static bool NativeTarget(BattlePresentationOwnership owner, Component battle, object character)
        {
            string expected = owner.Mode == BattleMode.Campaign || owner.Mode == BattleMode.NormalChallenge || owner.Mode == BattleMode.EliteChallenge ? "CampaignUI" :
                owner.Mode == BattleMode.Grave ? "GraveLevelUI" : owner.Mode == BattleMode.Trial ? "TrialLevelUI" :
                owner.Mode == BattleMode.TrialBoss ? "TrialUI" : owner.Mode == BattleMode.Divinity ? "DivinityUI" :
                owner.Mode == BattleMode.TrainingMonster ? "SealMonsterUI" :
                owner.Mode == BattleMode.OwnBuildingMonster || owner.Mode == BattleMode.FriendBuildingMonster ? "BuildingMonsterScript" : "";
            if (expected == "" || owner.Source.GetType() != Model.GameType(expected)) return false;
            if (owner.Mode == BattleMode.Campaign || owner.Mode == BattleMode.NormalChallenge || owner.Mode == BattleMode.EliteChallenge)
            {
                object campaign = SharedDevelopmentContext.Read(owner.Source, "m_campaign");
                IDictionary troops = SharedDevelopmentContext.Read(owner.Source, "m_troops") as IDictionary;
                int type = Convert.ToInt32(SharedDevelopmentContext.Read(campaign, "idcampaign_type"), CultureInfo.InvariantCulture);
                byte troopId = Convert.ToByte(SharedDevelopmentContext.Read(battle, "idtroop"), CultureInfo.InvariantCulture);
                return type == (owner.Mode == BattleMode.Campaign ? 0 : owner.Mode == BattleMode.EliteChallenge ? 4 : 2) && troops != null &&
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
                a.Battle.transform.root == a.Root.transform && SourceIdentity(a) &&
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
            if (packet == null || Convert.ToUInt32(SharedDevelopmentContext.Read(packet, "_result"), CultureInfo.InvariantCulture) != 0) return false;
            // The stock mode callback applies packet.iswin directly. For these
            // attacker-owned reports, LEFT_WIN is the only win; LEFT_LOSE and
            // DRAW are non-wins. Optional iswin legitimately defaults to false.
            // This also runs for embedded objects immediately before BattleEnd;
            // it does not invent evidence about their original wire presence.
            int reportResult = Convert.ToInt32(SharedDevelopmentContext.Read(a.Report, "_result_status"), CultureInfo.InvariantCulture);
            if (reportResult < 1 || reportResult > 3 ||
                (bool)SharedDevelopmentContext.Read(packet, "_iswin") != (reportResult == 1)) return false;
            // The campaign and normal challenge native loss paths accept an unstored
            // report: idbattle_report is optional/default zero in SCBattleEnd,
            // and BattleUI applies it without requiring a saved report ID.
            // The current server simulation, exact owner/type/troop and the
            // original result=0 callback remain required. This is not a
            // substitute result for an absent or rejected response.
            bool reportlessNormalLoss = a.State.Mode == BattleMode.NormalChallenge && reportResult == 2 &&
                Convert.ToUInt64(SharedDevelopmentContext.Read(packet, "_idbattle_report"), CultureInfo.InvariantCulture) == 0;
            bool reportlessCampaignLoss = a.State.Mode == BattleMode.Campaign && reportResult == 2 &&
                Convert.ToUInt64(SharedDevelopmentContext.Read(packet, "_idbattle_report"), CultureInfo.InvariantCulture) == 0;
            // Trial's type-2 and TrialBoss's type-3 callbacks apply only iswin
            // to their already bound target; neither BattleEnd(bool) callback
            // reads the optional saved-report ID.
            bool reportlessTrial = (a.State.Mode == BattleMode.Trial || a.State.Mode == BattleMode.TrialBoss) &&
                Convert.ToUInt64(SharedDevelopmentContext.Read(packet, "_idbattle_report"), CultureInfo.InvariantCulture) == 0;
            // GraveLevelUI applies iswin to the bound troop and uses the
            // initial 0x342 response's rewards/soldiers. It does not require a
            // saved report ID; the live type-4 win omits that optional field.
            // The exact pending callback, live report, type and outcome remain
            // required before this packet can reach the stock callback.
            bool reportlessGrave = a.State.Mode == BattleMode.Grave &&
                Convert.ToUInt64(SharedDevelopmentContext.Read(packet, "_idbattle_report"), CultureInfo.InvariantCulture) == 0;
            // SealMonsterUI's type-7 callback applies its bound troop's win or
            // cooldown branch directly; neither branch reads a saved report ID.
            bool reportlessTraining = a.State.Mode == BattleMode.TrainingMonster &&
                Convert.ToUInt64(SharedDevelopmentContext.Read(packet, "_idbattle_report"), CultureInfo.InvariantCulture) == 0;
            // DivinityUI returns immediately on a loss; its stock type-5
            // callback does not require a stored report ID for that result.
            bool reportlessDivinityLoss = a.State.Mode == BattleMode.Divinity && reportResult == 2 &&
                Convert.ToUInt64(SharedDevelopmentContext.Read(packet, "_idbattle_report"), CultureInfo.InvariantCulture) == 0;
            // BuildingMonsterScript.BattleEnd returns immediately on a loss.
            // The live type-1 loss response can omit its saved report and target
            // echoes; ownership still comes from this launch's validated report.
            bool monsterLoss = (a.State.Mode == BattleMode.OwnBuildingMonster ||
                a.State.Mode == BattleMode.FriendBuildingMonster) && reportResult == 2;
            bool reportlessMonsterLoss = monsterLoss &&
                Convert.ToUInt64(SharedDevelopmentContext.Read(packet, "_idbattle_report"), CultureInfo.InvariantCulture) == 0;
            bool reportlessElite = a.State.Mode == BattleMode.EliteChallenge &&
                Convert.ToUInt64(SharedDevelopmentContext.Read(packet, "_idbattle_report"), CultureInfo.InvariantCulture) == 0;
            if (!reportlessNormalLoss && !reportlessCampaignLoss && !reportlessTrial && !reportlessGrave && !reportlessTraining && !reportlessDivinityLoss && !reportlessMonsterLoss && !reportlessElite &&
                Convert.ToUInt64(SharedDevelopmentContext.Read(packet, "_idbattle_report"), CultureInfo.InvariantCulture) == 0) return false;
            uint type = Convert.ToUInt32(SharedDevelopmentContext.Read(packet, "_type"), CultureInfo.InvariantCulture);
            BattleMode mode = a.State.Mode;
            uint expected = mode == BattleMode.Campaign || mode == BattleMode.NormalChallenge || mode == BattleMode.EliteChallenge ? 0u :
                mode == BattleMode.OwnBuildingMonster || mode == BattleMode.FriendBuildingMonster ? 1u :
                mode == BattleMode.Trial ? 2u : mode == BattleMode.TrialBoss ? 3u : mode == BattleMode.Grave ? 4u :
                mode == BattleMode.Divinity ? 5u : mode == BattleMode.TrainingMonster ? 7u : UInt32.MaxValue;
            if (type != expected) return false;
            if (type == 0)
            {
                uint campaign = Convert.ToUInt32(SharedDevelopmentContext.Read(packet, "_idcampaign"), CultureInfo.InvariantCulture);
                // CampaignUI uses the current campaign on loss. Its optional
                // campaign echo may be absent; a different nonzero echo is
                // still a conflict, and the stock troop lookup must match.
                return (campaign == Convert.ToUInt32(SharedDevelopmentContext.Read(a.Battle, "idcampaign"), CultureInfo.InvariantCulture) ||
                    reportlessNormalLoss && campaign == 0) &&
                    Convert.ToUInt32(SharedDevelopmentContext.Read(packet, "_idtroop"), CultureInfo.InvariantCulture) ==
                    Convert.ToUInt32(SharedDevelopmentContext.Read(a.Battle, "idtroop"), CultureInfo.InvariantCulture);
            }
            if (type == 1)
            {
                ulong friend = Convert.ToUInt64(SharedDevelopmentContext.Read(packet, "_idfriend"), CultureInfo.InvariantCulture);
                uint monster = Convert.ToUInt32(SharedDevelopmentContext.Read(packet, "_idmonster"), CultureInfo.InvariantCulture);
                return (friend == a.TargetOwnerId || monsterLoss && friend == 0) &&
                    (monster == Convert.ToUInt32(SharedDevelopmentContext.Read(a.State.Target, "id"), CultureInfo.InvariantCulture) ||
                        monsterLoss && monster == 0);
            }
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
            ConfirmDurable(a,
                "stock-0x408:report:" + reportId.ToString(CultureInfo.InvariantCulture) +
                ":path:" + a.State.Path.ToString() + (body == null ? "" : ":body:" + NativeBattleStartStore.EvidenceHash(body)));
            a.AppliedBody = body; a.Retry = false;
            Emit("battle_settlement_applied", a, "\"result\":0,\"opcode\":1032");
        }

        private static void ConfirmDurable(Attempt a, string proof)
        {
            try { NativeBattleStartStore.Confirm(a.DurableTicket, proof); }
            catch (Exception) { Fail(a, "battle_durable_confirmation_unverifiable_no_retry"); }
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
                            CaptureResponse(a, packet, body);
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

        private void CaptureResponse(Attempt a, object packet, byte[] body)
        {
            // Preserve the real envelope before admission or native mutation.
            // A rejected callback must not erase the evidence needed to tell an
            // optional default from an actual mode/target/outcome conflict.
            Emit("battle_settlement_response_captured", a,
                "\"operation_id\":" + SharedDevelopmentContext.Q(a.Ownership.OperationId) + ",\"mode\":" + SharedDevelopmentContext.Q(a.State.Mode.ToString()) +
                ",\"result\":" + Convert.ToUInt32(SharedDevelopmentContext.Read(packet, "_result"), CultureInfo.InvariantCulture) +
                ",\"type\":" + Convert.ToUInt32(SharedDevelopmentContext.Read(packet, "_type"), CultureInfo.InvariantCulture) +
                ",\"campaign_id\":" + Convert.ToUInt32(SharedDevelopmentContext.Read(packet, "_idcampaign"), CultureInfo.InvariantCulture) +
                ",\"troop_id\":" + Convert.ToUInt32(SharedDevelopmentContext.Read(packet, "_idtroop"), CultureInfo.InvariantCulture) +
                ",\"iswin\":" + ((bool)SharedDevelopmentContext.Read(packet, "_iswin") ? "true" : "false") +
                ",\"report_id\":" + SharedDevelopmentContext.Q(Convert.ToUInt64(SharedDevelopmentContext.Read(packet, "_idbattle_report"), CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)) +
                ",\"report_result_status\":" + Convert.ToInt32(SharedDevelopmentContext.Read(a.Report, "_result_status"), CultureInfo.InvariantCulture) +
                ",\"body_base64\":" + SharedDevelopmentContext.Q(Convert.ToBase64String(body)));
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
                ",\"source_root_instance_id\":" + (a.SourceRoot == null ? 0 : a.SourceRoot.GetInstanceID()) +
                ",\"previous_root_instance_id\":" + (a.Previous == null ? 0 : a.Previous.GetInstanceID()) +
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
