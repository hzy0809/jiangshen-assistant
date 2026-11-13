using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using UnityEngine;
using D = JiangShen.AssistantRuntime.SharedDevelopmentContext;

namespace JiangShen.AssistantRuntime
{
    // Current stock Web_OnGUI uses only 0x726 for ordinary copying. The old
    // 0x728 harvest method is not an offered button; gold 0x727 is never called.
    internal sealed class DailyTechBookCopyModel
    {
        internal const int Task = 303;
        private const ushort Opcode = 1830;
        private sealed class Frame
        {
            internal D Context;
            internal object Character, Client, Sub, Setting;
            internal DailyTransport Transport;
            internal Component Main, Panel, CapitalDemon;
            internal ulong Role;
            internal uint Now, Day, Cycle, Finish, GoldConsumed;
            internal int Used, Limit, Books, Gold, Bound, PendingBooks;
            internal bool Home, Loading, Ready, NetworkBusy, TransportBusy, Unlocked, Copying, Moving;
            internal string Error = "";
            internal bool SameOwner(Frame before)
            {
                return before != null && Role != 0 && Role == before.Role && Day == before.Day && Cycle == before.Cycle &&
                    Now >= before.Now && Context.SameOwnership(before.Context) && ReferenceEquals(Character, before.Character) &&
                    ReferenceEquals(Client, before.Client) && Transport != null && Transport.Same(before.Transport) &&
                    ReferenceEquals(Main, before.Main) && ReferenceEquals(Panel, before.Panel) && ReferenceEquals(Setting, before.Setting);
            }
            internal bool SameOffer(Frame before)
            {
                return SameOwner(before) && Used == before.Used && Limit == before.Limit && Finish == before.Finish &&
                    Books == before.Books && Gold == before.Gold && Bound == before.Bound && GoldConsumed == before.GoldConsumed &&
                    Copying == before.Copying && Moving == before.Moving && PendingBooks == before.PendingBooks && Unlocked == before.Unlocked;
            }
        }
        private sealed class Intent
        {
            internal string Action, Id;
            internal Frame Baseline;
            internal int Tick;
        }
        private Model model;
        private Frame frame, origin, sent;
        private Intent queued;
        private DailyDivinityReplyTap tap;
        private string status = "IDLE", reason = "not_observed", nextAction = "", operationId = "", stamp;
        private bool blocked, stopped, holding, closing, closed, attempted, returned, confirmed, unknown, creditPending;
        private bool requestIssued, replyObserved, requestRejected, requestPending, processedWithoutReply, pageStateConfirmed;
        private int sequence, stageTick, requests, completed, expectedBooks;
        internal int Revision { get; private set; }
        internal bool Busy { get { return queued != null || holding || tap != null; } }
        internal bool MutationAttempted { get { return attempted; } }
        internal bool GuiInvocationAttempted { get; private set; }

        internal void Capture(Model currentModel, int task, bool admissionBlocked)
        {
            if (task != Task) throw new ArgumentOutOfRangeException("task");
            model = currentModel; blocked = admissionBlocked; nextAction = "";
            try
            {
                frame = Read();
                if (frame.Error != "") { if (holding) Unknown(frame.Error); else Set("BLOCKED", frame.Error); return; }
                if (holding && !frame.SameOwner(origin)) { Unknown("copy_owner_or_game_day_changed"); return; }
                if (holding && (frame.Gold != origin.Gold || frame.Bound != origin.Bound || frame.GoldConsumed != origin.GoldConsumed))
                { Unknown("copy_premium_balance_changed"); return; }
                if (unknown && Busy) { Set("UNKNOWN", reason); return; }
                if (!Busy) unknown = false;
                // Read the same role/day's visible counters and credited books
                // before asking whether our optional callback observer returned.
                if (requestPending)
                {
                    if (requestRejected) { Unknown("copy_game_rejected"); return; }
                    bool idle = !frame.Loading && !frame.TransportBusy && !frame.Moving && frame.PendingBooks == 0;
                    bool advanced = sent != null && frame.SameOwner(sent) && frame.Used == sent.Used + 1 &&
                        frame.Limit == sent.Limit && frame.Finish >= sent.Finish && frame.Books > sent.Books &&
                        (!creditPending || frame.Books == expectedBooks);
                    if (idle && advanced)
                    {
                        completed++; confirmed = pageStateConfirmed = true;
                        requestPending = creditPending = false;
                    }
                    else if (idle && requestIssued && !replyObserved &&
                        (tap == null || tap.Error == null || tap.Error == "divinity_stock_callback_null"))
                    {
                        // The user treats a sent action without a reply as
                        // processed. Do not invent a counter or credited book.
                        processedWithoutReply = true; pageStateConfirmed = false;
                        requestPending = creditPending = false; closing = true;
                    }
                    else if (tap != null && tap.Error != null)
                    { Unknown(tap.Error); return; }
                    else if (replyObserved && Elapsed() > 20000)
                    { Unknown("copy_current_state_unconfirmed_after_reply"); return; }
                    else
                    { Set("PENDING", frame.TransportBusy ? "copy_stock_request_pending" : "copy_current_state_pending"); return; }
                }
                if (tap != null)
                {
                    tap.StopObserving();
                    if (!tap.TryRestore()) { Set("PENDING", "copy_callback_release_pending"); return; }
                    tap = null;
                }
                if (stopped) { Set("STOPPED", "user_stop"); return; }
                if (holding && frame.Loading) { Set("PENDING", "copy_stock_loading"); return; }
                string panels = frame.Context.Panels(frame.Panel, frame.CapitalDemon);
                if (panels != "PASS") { if (holding) Unknown(panels); else Set("BLOCKED", panels); return; }
                if (!holding)
                {
                    string available = Eligibility(frame);
                    // Keep this operation's terminal receipt observable while
                    // deriving a new operation's offer from current game data.
                    if (closed && (completed > 0 || processedWithoutReply))
                    {
                        if (!processedWithoutReply && available == "READY" && !frame.NetworkBusy && !frame.Moving && frame.PendingBooks == 0)
                            nextAction = frame.Home ? "open" :
                            ReferenceEquals(frame.Sub, frame.Panel) && frame.Ready ? "resume" : "";
                        FinishClosed(); return;
                    }
                    if (available != "READY") { Set(available, "copy_" + available.ToLowerInvariant()); return; }
                    if (frame.NetworkBusy || frame.Moving || frame.PendingBooks != 0)
                    { Set("BLOCKED", "copy_existing_request_or_animation_pending"); return; }
                    if (frame.Home) Offer("READY", "open");
                    else if (ReferenceEquals(frame.Sub, frame.Panel) && frame.Ready) Offer("READY", "resume");
                    else Set("BLOCKED", "copy_home_or_own_panel_required");
                    return;
                }
                if (frame.Loading || frame.TransportBusy || !processedWithoutReply && frame.NetworkBusy)
                { Set("PENDING", "copy_stock_loading_or_network"); return; }
                // Crit credits are applied by TechBookCopyUI.Web_OnGUI after
                // its animation. Keep that panel visible until the exact credit
                // is installed; closing it earlier loses the local credit path.
                if (creditPending)
                {
                    if (frame.Books == expectedBooks && !frame.Moving && frame.PendingBooks == 0)
                    { creditPending = false; confirmed = true; completed++; }
                    else if (frame.Books > expectedBooks || Elapsed() > 20000)
                    { Unknown("copy_reward_credit_unverified"); return; }
                    else { Set("PENDING", "copy_reward_animation_pending"); return; }
                }
                if (frame.Limit != origin.Limit || (processedWithoutReply ?
                    frame.Used < origin.Used + completed || frame.Used > origin.Used + requests : frame.Used != origin.Used + completed))
                { Unknown("copy_free_count_changed_outside_operation"); return; }
                if (closing)
                {
                    if (frame.Home)
                    {
                        holding = false; closed = true;
                        FinishClosed();
                    }
                    else if (ReferenceEquals(frame.Sub, frame.Panel) && frame.Ready) Offer("CLOSING", "close");
                    else if (Elapsed() > 20000) Unknown("copy_cleanup_unverified");
                    else Set("CLOSING", "copy_waiting_home");
                    return;
                }
                if (Eligibility(frame) != "READY")
                {
                    closing = true;
                    if (ReferenceEquals(frame.Sub, frame.Panel) && frame.Ready) Offer("CLOSING", "close");
                    else if (frame.Home)
                    { holding = false; closed = true; FinishClosed(); }
                    else Set("PENDING", "copy_waiting_panel");
                    return;
                }
                if (ReferenceEquals(frame.Sub, frame.Panel) && frame.Ready)
                {
                    if (requests >= origin.Limit - origin.Used) { Unknown("copy_free_request_bound"); return; }
                    Offer("READY", "copy_free");
                }
                else if (Elapsed() > 20000) Unknown("copy_panel_timeout");
                else Set("PENDING", "copy_panel_loading");
            }
            catch (Exception error) { if (holding) Unknown(error.Message); else Set("BLOCKED", error.Message); }
        }

        private static string Eligibility(Frame f)
        {
            if (!f.Unlocked) return "CONDITION_NOT_MET";
            if (f.Used >= f.Limit) return "FREE_LIMIT_REACHED";
            if (f.Finish > f.Now) return "WAITING_COOLDOWN";
            return "READY";
        }

        internal bool Queue(int task, ulong role, string id, int seq, int revision, string action, out string why)
        {
            why = "copy_command_unavailable";
            bool start = action == "open" || action == "resume";
            if (task != Task || stopped || blocked || frame == null || frame.Error != "" || role == 0 || role != frame.Role ||
                queued != null || seq <= sequence || !Command.ValidNonce(id) || action == "" ||
                (start ? Busy : !holding || id != operationId)) return false;
            // These two failures are before intent publication, sequence
            // ownership, GUI invocation or a stock request. A caller may
            // re-observe and propose a fresh command after either rejection.
            if (revision != Revision) { why = "copy_queue_quote_changed"; return false; }
            if (action != nextAction) { why = "copy_queue_offer_changed"; return false; }
            queued = new Intent { Action = action, Id = id, Baseline = frame, Tick = Environment.TickCount };
            sequence = seq; GuiInvocationAttempted = false; Revision++; why = ""; return true;
        }

        internal bool ExecuteGui(DevelopmentPublication publish, DailyMonsterInvocationGate begin, out string why)
        {
            why = "copy_not_queued"; Intent intent = queued;
            if (intent == null) return false;
            object client = intent.Baseline.Client;
            if (client == null || !Monitor.TryEnter(client, 0)) { NotInvoked("client_busy"); why = "client_busy"; return false; }
            try
            {
                publish(); Capture(model, Task, blocked);
                if (stopped || blocked || !D.Gui || !frame.SameOffer(intent.Baseline) || intent.Action != nextAction ||
                    unchecked((uint)(Environment.TickCount - intent.Tick)) > 1500 || !begin())
                { why = "copy_gui_admission_changed"; NotInvoked(why); return false; }
                queued = null; stageTick = Environment.TickCount;
                if (intent.Action == "open" || intent.Action == "resume")
                {
                    origin = frame; holding = true; operationId = intent.Id;
                    attempted = returned = confirmed = closed = closing = unknown = creditPending = false;
                    requestIssued = replyObserved = requestRejected = requestPending = processedWithoutReply = pageStateConfirmed = false;
                    requests = completed = 0;
                    if (intent.Action == "open") { GuiInvocationAttempted = true; D.MainBranch("_techbookcopy"); }
                }
                else if (intent.Action == "close")
                {
                    GuiInvocationAttempted = true; D.Call(frame.Panel, "Hide");
                    D.Method(Model.GameType("MainUI"), "SetSubUI", new[] { Model.FirstType("__BaseUI") }, true, typeof(void)).Invoke(null, new object[] { null });
                    closing = true;
                }
                else if (intent.Action == "copy_free") CopyFree();
                else throw new ProbeFailure("copy_action_out_of_scope");
                nextAction = ""; Set("PENDING", "copy_stock_action_invoked"); why = ""; return true;
            }
            catch (Exception error) { queued = null; Unknown(error.Message); why = reason; return false; }
            finally { Monitor.Exit(client); }
        }

        private void CopyFree()
        {
            if (Eligibility(frame) != "READY" || frame.NetworkBusy || frame.Moving || frame.PendingBooks != 0 ||
                requests >= origin.Limit - origin.Used || Handler(frame.Client) != null)
                throw new ProbeFailure("copy_free_not_currently_eligible");
            sent = frame; attempted = true; returned = confirmed = false; requests++; GuiInvocationAttempted = true;
            requestIssued = replyObserved = requestRejected = requestPending = pageStateConfirmed = false;
            D.Call(frame.Panel, "cs_tech_book_copy_free_0x726");
            object handler = Handler(sent.Client);
            Delegate stock = handler == null ? null : D.Read(handler, "handler") as Delegate;
            if (stock == null || stock.GetInvocationList().Length != 1 || !ReferenceEquals(stock.Target, sent.Panel) ||
                stock.Method != D.Method(sent.Panel.GetType(), "sc_tech_book_copy_free_0x726", new[] { typeof(MemoryStream), typeof(object) }, false, typeof(void)))
                throw new ProbeFailure("copy_stock_registration_unverified");
            requestIssued = requestPending = true;
            object argument = D.Read(handler, "arg");
            tap = DailyDivinityReplyTap.Attach(sent.Client, false, handler, handler.GetType().GetField("handler", D.Instance), argument,
                delegate { return Handler(sent.Client); }, CallbackOwner, Receipt);
            // Observation is optional; the original client callback remains
            // installed when a tap could not be attached.
        }

        private bool CallbackOwner()
        {
            if (sent == null || !ReferenceEquals(sent.Character, D.Static(Model.FirstType("Global"), "character_")) ||
                !ReferenceEquals(sent.Client, DailyTransport.CurrentClient(sent.Character)) || !sent.Transport.StillCurrent() ||
                !ReferenceEquals(model.CurrentRoot(), sent.Context.Root) || sent.Context.OwnershipGate() != "PASS") return false;
            uint now, day, cycle;
            return DailyRefreshGate.ReadClock(sent.Character, out now, out day, out cycle) == "" &&
                now >= sent.Now && day == sent.Day && cycle == sent.Cycle;
        }

        private void Receipt(byte[] bytes)
        {
            replyObserved = returned = true;
            Dictionary<int, ulong> wire = Scalars(bytes);
            ulong operate = Value(wire, 1), finish = Value(wire, 2), credit = Optional(wire, 3), crit = Optional(wire, 4);
            Frame after = Read();
            if (after.Error != "" || !after.SameOwner(sent) || after.Gold != sent.Gold || after.Bound != sent.Bound ||
                after.GoldConsumed != sent.GoldConsumed || crit > 1 || finish > UInt32.MaxValue || credit > Int32.MaxValue)
                throw new ProbeFailure("copy_receipt_owner_or_schema_changed");
            if (operate != 0) { requestRejected = true; throw new ProbeFailure("copy_game_rejected:" + operate); }
            if (after.Used != sent.Used + 1 || after.Limit != sent.Limit || after.Finish != (uint)finish ||
                credit == 0 || after.Copying != sent.Copying)
                throw new ProbeFailure("copy_receipt_state_mismatch");
            expectedBooks = checked(sent.Books + (int)credit);
            if (crit == 0 && after.Books != expectedBooks || crit == 1 && after.Books != sent.Books && after.Books != expectedBooks)
                throw new ProbeFailure("copy_receipt_credit_mismatch");
            creditPending = true;
        }

        private Frame Read()
        {
            Frame f = new Frame();
            try
            {
                f.Context = DevelopmentContextReader.DailyWood(model, false); f.Character = f.Context.Character;
                f.Client = DailyTransport.CurrentClient(f.Character); f.Transport = DailyTransport.Read(f.Client);
                if (f.Character == null || f.Client == null || f.Transport == null || f.Context.OwnershipGate() != "PASS")
                    throw new ProbeFailure("copy_character_connection_unavailable");
                f.Role = Convert.ToUInt64(D.Read(f.Character, "id"), CultureInfo.InvariantCulture);
                string clock = DailyRefreshGate.ReadClock(f.Character, out f.Now, out f.Day, out f.Cycle);
                if (clock != "") throw new ProbeFailure(clock);
                f.Setting = D.Template("tech_book_copy_setting", 1u);
                f.Limit = D.Int(D.Read(f.Setting, "free_copy_times")); f.Used = D.Int(D.Read(f.Character, "tech_book_copy_free_times"));
                f.Finish = (uint)D.Read(f.Character, "tech_book_copy_finish_time"); f.Copying = D.Bool(D.Read(f.Character, "is_tech_book_copying"));
                f.Books = D.Int(D.Read(f.Character, "tech_book")); f.Gold = D.Int(D.Read(f.Character, "gold"));
                f.Bound = D.Int(D.Read(f.Character, "bind_gold")); f.GoldConsumed = (uint)D.Read(f.Character, "gold_consume");
                if (f.Limit < 0 || f.Limit > 255 || f.Used < 0 || f.Used > f.Limit || f.Books < 0)
                    throw new ProbeFailure("copy_quota_schema");
                f.Main = f.Context.Root.GetComponentInChildren(Model.GameType("MainUI"));
                if (f.Main == null || !ReferenceEquals(D.Read(f.Main, "rmg"), f.Context.Root) || f.Main.transform.root != f.Context.Root.transform)
                    throw new ProbeFailure("copy_scene_main_ui_unavailable");
                f.Panel = f.Context.Find("TechBookCopyUI"); f.Sub = f.Context.SubUi;
                f.Unlocked = (bool)D.Method(Model.GameType("MainUI"), "checkUI", new[] { typeof(string) }, true, typeof(bool)).Invoke(null, new object[] { "tech_book_copy" });
                f.Moving = D.Bool(D.Read(f.Panel, "m_bMoving"));
                f.PendingBooks = ((IDictionary)D.Read(f.Panel, "techBookAdd")).Count;
                object loader = D.Static(Model.FirstType("BaseAsset"), "lds"); f.Loading = ((Behaviour)loader).enabled;
                f.CapitalDemon = f.Context.FindCapitalDemon();
                f.Home = !f.Loading && f.Sub == null && D.Enabled(f.Main) && f.Context.Panels(f.CapitalDemon) == "PASS";
                f.Ready = !f.Loading && D.Enabled(f.Panel) && !D.Loading(f.Panel);
                f.NetworkBusy = D.Bool(D.Read(f.Panel, "sended"));
                foreach (DictionaryEntry item in (IDictionary)D.Read(f.Client, "m_handlers"))
                    if ((float)D.Read(item.Value, "life") > 0f) { f.TransportBusy = true; break; }
                f.TransportBusy |= ((IList)D.Read(f.Client, "messages")).Count != 0 ||
                    ((IList)D.Read(f.Client, "m_sendDatas")).Count != 0 || D.Bool(D.Read(f.Client, "m_bSending")) ||
                    DevelopmentCallbackCapture.AnyCallbackExecuting;
                f.NetworkBusy |= f.TransportBusy;
            }
            catch (Exception error) { f.Error = error.Message; }
            return f;
        }

        private static object Handler(object client) { return ((IDictionary)D.Read(client, "m_handlers"))[Opcode]; }
        private int Elapsed() { return unchecked((int)((uint)(Environment.TickCount - stageTick))); }
        private void FinishClosed()
        {
            Set(processedWithoutReply ? "PROCESSED" : completed > 0 ? "COMPLETED" : Eligibility(frame),
                processedWithoutReply ? "copy_no_reply_processed" : completed > 0 ? "copy_current_state_confirmed" : "copy_no_current_free_action");
        }
        private void Offer(string value, string action) { nextAction = action; Set(value, ""); }
        private void Unknown(string why)
        {
            unknown = true;
            if (tap != null) { tap.StopObserving(); if (tap.TryRestore()) tap = null; }
            queued = null; nextAction = ""; Set("UNKNOWN", why);
        }
        internal void Stop(string why)
        { stopped = true; queued = null; if (tap != null) { tap.StopObserving(); if (tap.TryRestore()) tap = null; } nextAction = ""; Set("STOPPED", why); }
        internal void NotInvoked(string why) { queued = null; GuiInvocationAttempted = false; nextAction = ""; Set(status, why); }
        private void Set(string value, string why)
        { status = value; reason = why; string next = Body(true); if (next != stamp) { stamp = next; Revision++; } }
        private string Body(bool quoteOnly)
        {
            return "\"task_id\":303,\"route\":\"tech_book_copy\",\"role_id\":" + D.Q(frame == null ? "0" : frame.Role.ToString(CultureInfo.InvariantCulture)) +
                ",\"refresh_cycle\":" + (frame == null ? 0 : frame.Cycle) + ",\"operation_id\":" + D.Q(operationId) +
                ",\"status\":" + D.Q(status) + ",\"reason\":" + D.Q(reason) + ",\"next_action\":" + D.Q(nextAction) +
                ",\"availability\":" + D.Q(frame == null || frame.Error != "" ? "UNKNOWN" : Eligibility(frame)) +
                ",\"entry_unlocked\":" + D.B(frame != null && frame.Error == "" && frame.Unlocked) +
                ",\"gui_invocation_attempted\":" + D.B(GuiInvocationAttempted) +
                ",\"mutation_attempted\":" + D.B(attempted) + ",\"stock_callback_returned\":" + D.B(returned) +
                ",\"reward_confirmed\":" + D.B(confirmed) + ",\"cleanup_pending\":" + D.B(holding) +
                ",\"processed_without_reply\":" + D.B(processedWithoutReply) +
                ",\"page_state_confirmed\":" + D.B(pageStateConfirmed && !processedWithoutReply) +
                ",\"request_issued\":" + D.B(requestIssued) + ",\"reply_received\":" + D.B(replyObserved) +
                ",\"closed\":" + D.B(closed) + ",\"busy\":" + D.B(Busy) +
                ",\"home_ready\":" + D.B(frame != null && frame.Error == "" && frame.Home &&
                    !D.Visible(frame.Panel) && !D.Loading(frame.Panel) && !frame.Moving &&
                    frame.PendingBooks == 0 && !frame.TransportBusy && (!frame.NetworkBusy || processedWithoutReply)) + ",\"copies_completed\":" + completed +
                ",\"request_count\":" + requests + ",\"free_used\":" + (frame == null ? 0 : frame.Used) +
                ",\"free_limit\":" + (frame == null ? 0 : frame.Limit) + ",\"free_remaining\":" + (frame == null ? 0 : frame.Limit - frame.Used) +
                ",\"finish_time\":" + (frame == null ? 0 : frame.Finish) + ",\"server_now\":" + (quoteOnly || frame == null ? 0u : frame.Now) +
                ",\"next_ready_seconds\":" + (quoteOnly || frame == null || frame.Finish <= frame.Now ? 0u : frame.Finish - frame.Now) +
                ",\"copying\":" + D.B(frame != null && frame.Copying);
        }
        // Publish the current server clock/countdown, but only change the
        // action quote when eligibility or another quoted fact changes.
        // Crossing cooldown zero still changes availability and next_action.
        internal string Json() { return "{\"schema\":1,\"revision\":" + Revision + "," + Body(false) + "}"; }

        private static Dictionary<int, ulong> Scalars(byte[] body)
        {
            if (body == null || body.Length == 0 || body.Length > 1048576) throw new FormatException("copy_receipt_size");
            Dictionary<int, ulong> values = new Dictionary<int, ulong>(); int p = 0;
            while (p < body.Length)
            {
                ulong tag = Varint(body, ref p); int field = (int)(tag >> 3), wire = (int)(tag & 7);
                if (field < 1 || tag > UInt32.MaxValue) throw new FormatException("copy_receipt_tag");
                if (wire == 0) { if (values.ContainsKey(field)) throw new FormatException("copy_receipt_duplicate"); values[field] = Varint(body, ref p); }
                else if (wire == 2) { ulong length = Varint(body, ref p); if (length > (ulong)(body.Length - p)) throw new FormatException(); p += (int)length; }
                else if (wire == 1 || wire == 5) { p += wire == 1 ? 8 : 4; if (p > body.Length) throw new FormatException(); }
                else throw new FormatException("copy_receipt_wire");
            }
            return values;
        }
        private static ulong Value(Dictionary<int, ulong> values, int field)
        { ulong value; if (!values.TryGetValue(field, out value)) throw new FormatException("copy_receipt_missing:" + field); return value; }
        private static ulong Optional(Dictionary<int, ulong> values, int field) { ulong value; return values.TryGetValue(field, out value) ? value : 0; }
        private static ulong Varint(byte[] body, ref int p)
        {
            ulong value = 0;
            for (int shift = 0; shift < 70; shift += 7)
            { if (p >= body.Length) throw new FormatException(); byte b = body[p++]; if (shift == 63 && b > 1) throw new FormatException(); value |= (ulong)(b & 127) << shift; if (b < 128) return value; }
            throw new FormatException();
        }
    }
}
