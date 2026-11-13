using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using UnityEngine;
using D = JiangShen.AssistantRuntime.SharedDevelopmentContext;

namespace JiangShen.AssistantRuntime
{
    internal delegate D DailyStoneContextProvider();
    internal delegate bool DailyStoneInvocationCommit();
    internal delegate bool DailyStoneInvocationGate(DailyStoneInvocationCommit sample, DailyStoneInvocationCommit check);
    internal delegate DevelopmentCallbackCapture DailyStoneCallbackInstall(object client, object handler,
        string operationId, int sequence, Delegate original, Action<byte[]> observe, out string why);

    internal sealed class DailyStoneReceipt
    {
        internal const ushort Opcode = 825;
        internal uint Result, Type;

        // SCEnchantDigSliver_0x339 requires both scalar fields. The request
        // CSEnchantDigSliver_0x339 has an empty body and carries no request id.
        internal static DailyStoneReceipt Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 4 || bytes.Length > 12) throw new FormatException("stone_reply_bound");
            DailyStoneReceipt receipt = new DailyStoneReceipt();
            int offset = 0, fields = 0;
            while (offset < bytes.Length)
            {
                int tag = bytes[offset++];
                int field = tag == 8 ? 1 : tag == 16 ? 2 : 0;
                if (field == 0 || (fields & field) != 0) throw new FormatException("stone_reply_field");
                fields |= field;
                uint value = ReadUInt(bytes, ref offset);
                if (field == 1) receipt.Result = value;
                else receipt.Type = value;
            }
            if (fields != 3 || receipt.Result > 1 || receipt.Result == 0 && (receipt.Type < 1 || receipt.Type > 7))
                throw new FormatException("stone_reply_values");
            return receipt;
        }

        private static uint ReadUInt(byte[] bytes, ref int offset)
        {
            uint value = 0;
            for (int shift = 0; shift <= 28 && offset < bytes.Length; shift += 7)
            {
                byte part = bytes[offset++];
                if (shift == 28 && part > 15) break;
                value |= (uint)(part & 127) << shift;
                if ((part & 128) == 0 && (shift == 0 || part != 0)) return value;
                if ((part & 128) == 0) break;
            }
            throw new FormatException("stone_reply_scalar");
        }
    }

    internal sealed class DailyStoneModel
    {
        internal const int TaskId = 16;
        private const uint WaitMs = 18000;
        private static readonly Type[] CallbackArgs = { typeof(MemoryStream), typeof(object) };

        private sealed class Frame
        {
            internal D Context;
            internal Component Panel;
            internal DailyTransport Transport;
            internal object Sub, Rows, Row, Template, Reward, PanelRows, Listed, Vip, Handler, ActivityHandler;
            internal ulong Role;
            internal uint Now, Cycle, Next, GoldConsumed;
            internal int Current, Required, Refresh, ListedCurrent, ListedRefresh, Silver, Gold, Bound, Stones, Double, Level, GoldPick, Bought;
            internal DateTime VisibleAt, LoadedAt, ResultUntil;
            internal bool Visible, Enabled, Loading, Selected, Quiet, Sending, Idle, Locked, ResultClear, ActivityReady;
            internal string Error = "", Overlay = "";

            internal bool SameOwner(Frame other)
            {
                return other != null && Context != null && Context.SameOwnership(other.Context) && Role != 0 && Role == other.Role &&
                    Panel != null && ReferenceEquals(Panel, other.Panel) && Transport != null && Transport.Same(other.Transport) &&
                    Cycle != 0 && Cycle == other.Cycle && Next == other.Next && Now >= other.Now;
            }

            internal bool SameTask(Frame other)
            {
                return ReferenceEquals(Rows, other.Rows) && ReferenceEquals(Template, other.Template) &&
                    ReferenceEquals(Reward, other.Reward) && ReferenceEquals(PanelRows, other.PanelRows) && Required == other.Required;
            }

            internal bool SameProgress(Frame other)
            {
                return SameTask(other) && ReferenceEquals(Row, other.Row) && Current == other.Current && Refresh == other.Refresh &&
                    ReferenceEquals(Listed, other.Listed) && ListedCurrent == other.ListedCurrent && ListedRefresh == other.ListedRefresh;
            }

            internal bool SameCost(Frame other)
            {
                return Gold == other.Gold && Bound == other.Bound && GoldConsumed == other.GoldConsumed &&
                    Bought == other.Bought && GoldPick == other.GoldPick;
            }

            internal bool SameResources(Frame other)
            { return SameCost(other) && Silver == other.Silver && Stones == other.Stones && Double == other.Double; }

            internal bool SameWindow(Frame other)
            {
                return Selected && other.Selected && ReferenceEquals(Sub, other.Sub) && SameGeneration(other);
            }

            internal bool SameGeneration(Frame other)
            { return VisibleAt == other.VisibleAt && LoadedAt == other.LoadedAt; }

            internal bool SameAdmission(Frame other, bool compareActivityHandler)
            {
                return Error == "" && other.Error == "" && SameOwner(other) && SameProgress(other) && SameResources(other) &&
                    ReferenceEquals(Vip, other.Vip) && Level == other.Level && ReferenceEquals(Sub, other.Sub) && Selected == other.Selected &&
                    Visible == other.Visible && Enabled == other.Enabled && Loading == other.Loading && VisibleAt == other.VisibleAt &&
                    LoadedAt == other.LoadedAt && ResultUntil == other.ResultUntil && ResultClear == other.ResultClear && Overlay == other.Overlay && Quiet == other.Quiet &&
                    Idle == other.Idle && Sending == other.Sending && ReferenceEquals(Handler, other.Handler) &&
                    (!compareActivityHandler || ActivityReady == other.ActivityReady && ReferenceEquals(ActivityHandler, other.ActivityHandler));
            }
        }

        private sealed class Intent
        {
            internal Frame Baseline;
            internal string Id, Action;
            internal int Sequence;
        }

        private readonly DailyStoneCallbackInstall install, installActivity;
        private readonly DailyRefreshAdmission refresh;
        private readonly Hashtable used = new Hashtable();
        private Frame frame = new Frame(), origin, opened, sent;
        private Intent queued, operation;
        private DevelopmentCallbackCapture callback, activity;
        private DailyStoneReceipt receipt;
        private object handler;
        private int revision, lastSequence, since, expectedStones, expectedDouble;
        private int activityDispatch, activityRefresh;
        private string status = "IDLE", reason = "stone_unobserved", replyError, stamp = "", publishedView;
        private bool stopped, openUnverified, attempted, returned, progressObserved, closed;

        internal DailyStoneModel(DailyStoneCallbackInstall installer, DailyStoneCallbackInstall activityInstaller)
        { install = installer; installActivity = activityInstaller; refresh = DailyRefreshGate.Read; }

#if JSB_DAILY_STONE_TEST
        internal DailyStoneModel(DailyStoneCallbackInstall installer, DailyStoneCallbackInstall activityInstaller, DailyRefreshAdmission fixtureRefresh)
        { install = installer; installActivity = activityInstaller; refresh = fixtureRefresh; }
#endif

        internal int Revision { get { return revision; } }
        internal bool MutationAttempted { get { return attempted; } }
        internal bool OutcomeUnknown { get { return status == "UNKNOWN"; } }
        internal bool Unresolved { get { return status == "UNKNOWN" || status == "PENDING"; } }
        internal bool Busy { get { return queued != null || callback != null || activity != null || openUnverified || Unresolved || status == "SETTLED"; } }

        internal void Capture(D context)
        {
            if (stopped || status == "UNKNOWN") { Detach(); return; }
            if (status != "PENDING" && (callback != null || activity != null)) Detach();
            Frame previous = frame;
            try
            {
                frame = Read(context);
                if (openUnverified && frame.Error == "" && frame.SameOwner(origin) && frame.Sub == null &&
                    !frame.Visible && !frame.Loading && !frame.Sending)
                    openUnverified = false;
                if (status == "OPENING") ObserveOpen();
                else if (status == "OPEN" && (frame.Error != "" || !frame.SameOwner(opened) || !frame.SameWindow(opened) ||
                    !frame.SameProgress(opened) || !frame.SameResources(opened) || frame.Sending || !frame.Quiet || Expired()))
                    Finish("REJECTED", "stone_owned_panel_changed");
                else if (status == "PENDING") ObserveReply();
                else if (status == "SETTLED") ObserveCleanup();
            }
            catch (Exception error)
            {
                frame = new Frame { Error = error is ProbeFailure ? error.Message : "stone_observation_failed" };
                if (Busy) Finish(attempted ? "UNKNOWN" : "REJECTED", frame.Error);
            }
            PublishRevision(previous);
        }

        internal bool Queue(int taskId, ulong role, string id, int sequence, int expectedRevision, string action, out string why)
        {
            why = "stone_admission";
            if (taskId != TaskId || role == 0 || role != frame.Role || sequence <= lastSequence || expectedRevision != revision ||
                queued != null || stopped || !new DevelopmentCommand { OperationId = id }.ValidId()) return false;
            if (action != "open" && action != "dig" && action != "close") return false;
            if (action == "open" ? Busy || used.Count >= 4096 || used.Contains(id) : operation == null || operation.Id != id) return false;
            why = Gate(frame, action);
            if (why != "") return false;
            lastSequence = sequence;
            queued = new Intent { Baseline = frame, Id = id, Action = action, Sequence = sequence };
            if (action == "open")
            {
                used.Add(id, true); operation = queued;
                attempted = returned = progressObserved = closed = false;
                activityDispatch = activityRefresh = 0;
                sent = origin = opened = null; handler = null; receipt = null; replyError = null;
                status = "IDLE"; reason = "";
            }
            PublishRevision(frame);
            return true;
        }

        internal bool ExecuteGui(DailyStoneContextProvider freshContext, DevelopmentPublication publish,
            DailyStoneInvocationGate begin, out string why)
        {
            why = "stone_not_queued";
            Intent intent = queued;
            if (intent == null || stopped) return false;
            object client = intent.Baseline.Context.Client;
            if (!D.Gui || freshContext == null || publish == null || begin == null) return Reject(intent, "stone_gui_required", out why);
            if (!Monitor.TryEnter(client, 0)) return Reject(intent, "stone_client_busy", out why);
            bool publishing = false;
            try
            {
                Frame before = Read(freshContext());
                if (!Admit(intent, before, out why)) return Reject(intent, why, out why);
                publishing = true;
                publish();
                publishing = false;
                if (stopped || queued != intent) { why = "stone_cancelled_before_call"; return false; }
                Frame current = Read(freshContext());
                if (!D.Gui || !Admit(intent, current, out why))
                    return Reject(intent, why == "" ? "stone_gui_required" : why, out why);
                bool began = false;
                DailyStoneInvocationGate start = delegate(DailyStoneInvocationCommit sample, DailyStoneInvocationCommit check)
                { return began = begin(sample, check); };
                if (intent.Action == "open") Open(intent, start, freshContext);
                else if (intent.Action == "dig") Dig(current, intent, start, freshContext);
                else Close(intent, start, freshContext);
                why = !began || status == "UNKNOWN" || status == "REJECTED" ? reason : "";
                PublishRevision(frame);
                return began && why == "";
            }
            catch (Exception)
            {
                if (publishing) throw; // Preserve the controller's stop/lease sentinel.
                return Reject(intent, intent.Action == "close" ? "stone_close_failed" :
                    attempted ? "stone_send_or_binding_failed" : "stone_gui_call_failed", out why);
            }
            finally { Monitor.Exit(client); }
        }

        private bool Admit(Intent intent, Frame current, out string why)
        {
            why = Gate(current, intent.Action);
            if (why != "") return false;
            if (!current.SameAdmission(intent.Baseline, intent.Action != "close")) { why = "stone_gui_baseline_changed"; return false; }
            return true;
        }

        private bool BeginCall(Intent intent, DailyStoneInvocationGate begin, DailyStoneContextProvider freshContext, out Frame committed)
        {
            Frame accepted = null;
            string detail = "";
            if (stopped || queued != intent || !begin(delegate
            {
                detail = DeadlineFailure(intent.Action);
                if (detail != "") return false;
                if (!D.Gui) { detail = "stone_gui_required"; return false; }
                Frame candidate = Read(freshContext(), false, intent.Action == "dig" ? activity : null);
                if (!Admit(intent, candidate, out detail)) return false;
                detail = FinalBoundary(intent, candidate);
                if (detail != "") return false;
                accepted = candidate;
                return true;
            }, delegate
            {
                detail = CheckAfterCommandRead(intent, accepted);
                return detail == "";
            }) || accepted == null)
            {
                string ignored;
                committed = null;
                Reject(intent, detail == "" ? "stone_cancelled_before_call" : detail, out ignored);
                return false;
            }
            committed = accepted;
            queued = null;
            if (intent.Action != "close") since = Environment.TickCount;
            return true;
        }

        private string FinalBoundary(Intent intent, Frame current)
        {
            if (stopped || queued != intent || !D.Gui) return "stone_gui_admission_changed";
            if (!CurrentRoot(current.Context) || !current.Context.SameOwnership(intent.Baseline.Context) ||
                !ReferenceEquals(current.Context.SubUi, current.Sub) ||
                current.Selected && Feature() != "enchant") return "stone_gui_owner_changed";
            if (!D.Bool(D.Static(Model.FirstType("Global"), "extern_enabled")) ||
                D.Bool(D.Static(Model.GameType("BuildingBuildUI"), "m_bBuildVisible")) ||
                D.Bool(D.Static(Model.FirstType("PopupUI"), "popup")) ||
                D.Bool(D.Static(Model.GameType("BuildingBehaviourScript"), "m_bEditVisible")))
                return "stone_gui_overlay_changed";
            if (intent.Action == "dig" && !WrappedActivityReady(current.ActivityHandler, activity))
                return "stone_activity_callback_unbound";
            uint now, day, cycle;
            string clock = refresh(current.Context.Character, out now, out day, out cycle);
            if (clock != "") return clock;
            if (cycle != current.Cycle || now < current.Now || Field<uint>(current.Context.Character, "next_resource_time") != current.Next)
                return "stone_refresh_changed_at_call";
            // ConnectLost can change transport health independently of this monitor.
            if (!current.Transport.Same(DailyTransport.Read(current.Context.Client)))
                return "stone_transport_changed_before_call";
            return DeadlineFailure(intent.Action);
        }

        private string CheckAfterCommandRead(Intent intent, Frame sampled)
        {
            if (sampled == null || stopped || queued != intent || !D.Gui)
                return "stone_gui_admission_changed";
            // Do not enumerate Unity objects or read the command file again.
            if (!ReferenceEquals(sampled.Context.Client, DailyTransport.CurrentClient(sampled.Context.Character)) ||
                !sampled.Transport.StillCurrent())
                return "stone_transport_changed_before_call";
            return DeadlineFailure(intent.Action);
        }

        private string DeadlineFailure(string action)
        {
            if (action == "open" || !Expired()) return "";
            return action == "close" ? "stone_cleanup_timed_out" : "stone_open_timed_out";
        }

        private void Open(Intent intent, DailyStoneInvocationGate begin, DailyStoneContextProvider freshContext)
        {
            MethodInfo method = D.Method(Model.GameType("MainUI"), "_enchant", Type.EmptyTypes, true, typeof(void));
            Frame current;
            if (!BeginCall(intent, begin, freshContext, out current)) return;
            origin = current; openUnverified = true;
            method.Invoke(null, null);
            if (!ReferenceEquals(current.Context.SubUi, current.Panel) || Feature() != "enchant")
            { Finish("REJECTED", "stone_loader_did_not_take_ownership"); return; }
            status = "OPENING"; reason = "stone_loader_waiting";
        }

        private void Dig(Frame current, Intent intent, DailyStoneInvocationGate begin, DailyStoneContextProvider freshContext)
        {
            MethodInfo send = D.Method(current.Panel.GetType(), "cs_enchant_dig_sliver", Type.EmptyTypes, false, typeof(void));
            MethodInfo response = D.Method(current.Panel.GetType(), "sc_enchant_dig_sliver", CallbackArgs, false, typeof(void));
            if (install == null || installActivity == null || send.DeclaringType != current.Panel.GetType() || !send.IsPublic ||
                response.DeclaringType != current.Panel.GetType() || !response.IsPublic)
            { Finish("REJECTED", "stone_stock_handler_unavailable"); return; }
            if (!current.Transport.Same(DailyTransport.Read(current.Context.Client)))
            { Finish("REJECTED", "stone_transport_changed_before_send"); return; }
            object progressHandler = current.ActivityHandler;
            Delegate push = progressHandler == null ? null : D.Read(progressHandler, "handler") as Delegate;
            string why;
            activity = installActivity(current.Context.Client, progressHandler, intent.Id, intent.Sequence, push, ReadActivityPush, out why);
            if (activity == null) { Finish("REJECTED", why); return; }
            if (activity.Opcode != 355 || activity.OperationId != intent.Id || activity.Sequence != intent.Sequence || !activity.StillInstalled())
            { Finish("REJECTED", "stone_activity_callback_identity"); return; }
            if (!BeginCall(intent, begin, freshContext, out current)) return;
            sent = current; attempted = true; openUnverified = false;
            // The stock single-send method sets sending and emits 825 only
            // for an existing pick. Do not arm ToDig, AIdig or a purchase.
            send.Invoke(current.Panel, null);
            if (!current.Transport.Same(DailyTransport.Read(current.Context.Client)))
            { Finish("UNKNOWN", "stone_transport_changed_after_send"); return; }
            handler = Handlers(current.Context.Client)[DailyStoneReceipt.Opcode];
            Delegate original = handler == null ? null : D.Read(handler, "handler") as Delegate;
            if (handler == null || handler.GetType() != Model.FirstType("game.net.tcp_base+Handler") ||
                Field<float>(handler, "life") != 10f || D.Read(handler, "arg") != null ||
                original == null || original.GetInvocationList().Length != 1 || original.Method != response ||
                !ReferenceEquals(original.Target, current.Panel) || !Field<bool>(current.Panel, "sending"))
            { Finish("UNKNOWN", "stone_stock_send_unobserved"); return; }
            callback = install(current.Context.Client, handler, intent.Id, intent.Sequence, original, ReadReply, out why);
            if (callback == null) { Finish("UNKNOWN", why); return; }
            if (callback.Opcode != DailyStoneReceipt.Opcode || callback.OperationId != intent.Id || callback.Sequence != intent.Sequence ||
                !callback.StillInstalled()) { Finish("UNKNOWN", "stone_callback_identity"); return; }
            status = "PENDING"; reason = "stone_waiting_stock_callback";
        }

        private void Close(Intent intent, DailyStoneInvocationGate begin, DailyStoneContextProvider freshContext)
        {
            // Same stock path used by EnchantUI's EndGUI close branch.
            // The AI flag is already off and must never be changed here.
            MethodInfo method = D.Method(Model.GameType("MainUI"), "SetSubUI", new Type[] { Model.FirstType("__BaseUI") }, true, typeof(void));
            Frame current;
            if (!BeginCall(intent, begin, freshContext, out current)) return;
            method.Invoke(null, new object[] { null });
            frame = Read(freshContext());
            if (Expired()) { Finish("UNKNOWN", "stone_cleanup_timed_out"); return; }
            if (!CleanupEvidence(frame) || !frame.SameGeneration(current) || frame.Sub != null || frame.Visible ||
                frame.Loading || frame.Sending || !frame.Quiet || callback != null || activity != null)
            { Finish("UNKNOWN", "stone_close_unconfirmed"); return; }
            closed = true;
            Finish("COMPLETED", "stone_progress_observed");
        }

        private void ObserveOpen()
        {
            if (frame.Error != "" || !frame.SameOwner(origin) || !frame.SameProgress(origin) || !frame.SameResources(origin) ||
                !frame.Selected || Expired())
            { Finish("REJECTED", frame.Error != "" ? frame.Error : "stone_loader_changed_or_timed_out"); return; }
            if (frame.Loading || !frame.Visible || !frame.Enabled || frame.LoadedAt > DateTime.Now) return;
            if (frame.VisibleAt <= origin.VisibleAt || frame.LoadedAt <= origin.LoadedAt)
            { Finish("REJECTED", "stone_loader_generation_unproven"); return; }
            opened = frame; status = "OPEN"; reason = "";
        }

        private void ReadActivityPush(byte[] body)
        {
            if (!attempted || sent == null) throw new FormatException("stone_activity_before_send");
            Frame observed = Read(sent.Context, true);
            if (observed.Error != "" || !observed.SameOwner(sent) || !observed.SameTask(sent) ||
                !observed.SameWindow(sent) || !observed.SameCost(sent))
                throw new FormatException("stone_activity_boundary_changed");
            int refreshTime;
            int? count = GuildReceipt.Progress(body, TaskId, out refreshTime);
            if (!count.HasValue) return;
            if (count == sent.Current && refreshTime == sent.Refresh && activityDispatch == 0) return;
            if (count != sent.Current + 1 || activityDispatch != 0 && refreshTime != activityRefresh)
                throw new FormatException("stone_activity_progress_conflict");
            activityRefresh = refreshTime;
            activityDispatch = activity.Read().Calls;
        }

        private void ReadReply(byte[] body)
        {
            // Runs before the original callback. Read the pinned cache and
            // transport directly, without touching the live GUI inventory.
            try
            {
                Frame observed = Read(sent.Context, true);
                if (observed.Error != "" || !observed.SameOwner(sent) || !observed.SameTask(sent) ||
                    !observed.SameWindow(sent) || !observed.SameResources(sent) || observed.Level != sent.Level || !observed.Sending || !observed.Quiet ||
                    observed.Handler != null || observed.Current < sent.Current || observed.Current > sent.Current + 1)
                { replyError = "stone_callback_boundary_changed"; return; }
                receipt = DailyStoneReceipt.Decode(body);
                expectedStones = sent.Stones; expectedDouble = sent.Double;
                if (receipt.Result != 0) return;
                object result = D.Template("enchant_harvest_chance", receipt.Type);
                if (result == null || result.GetType().FullName != "Database._enchant_harvest_chance" ||
                    Field<int>(result, "id") != receipt.Type) { replyError = "stone_result_template"; return; }
                if (receipt.Type == 5) expectedDouble++;
                else
                {
                    expectedDouble = 0;
                    if (receipt.Type != 6)
                    {
                        int amount = Field<byte>(result, "num");
                        if (amount == 0) { replyError = "stone_result_amount"; return; }
                        expectedStones = checked(sent.Stones + (amount << sent.Double));
                    }
                }
            }
            catch (Exception) { replyError = "stone_callback_schema"; }
        }

        private void ObserveReply()
        {
            if (Expired()) { Finish("UNKNOWN", "stone_settlement_timed_out"); return; }
            if (frame.Error == "stone_client_busy") return;
            if (frame.Error != "" || !frame.SameOwner(sent) || !frame.SameTask(sent) || !frame.SameCost(sent) || !frame.SameWindow(sent))
            { Finish("UNKNOWN", frame.Error != "" ? frame.Error : "stone_pending_boundary_changed"); return; }
            DevelopmentCallbackWitness witness = callback.Read();
            DevelopmentCallbackWitness activityWitness = activity.Read();
            if (activityWitness.Error != null || activityWitness.OriginalThrew || activityWitness.NullResponse || !activity.StillInstalled())
            { Finish("UNKNOWN", "stone_activity_callback_changed"); return; }
            if (witness.Error != null || witness.OriginalThrew || witness.NullResponse || witness.Calls > 1 || replyError != null)
            { Finish("UNKNOWN", replyError ?? witness.Error ?? "stone_stock_callback_failed"); return; }
            if (witness.Calls == 0)
            {
                if (!ReferenceEquals(frame.Handler, handler) || !callback.StillInstalled())
                    Finish("UNKNOWN", "stone_stock_callback_lost");
                return;
            }
            if (!witness.Returned) return;
            returned = true;
            if (receipt == null || frame.Handler != null || frame.Sending || !frame.Quiet)
            { Finish("UNKNOWN", "stone_stock_readback_conflict"); return; }
            if (receipt.Result != 0)
            {
                if (Expired()) { Finish("UNKNOWN", "stone_settlement_timed_out"); return; }
                bool unchanged = frame.SameResources(sent) && activityDispatch == 0 && frame.Current == sent.Current &&
                    frame.Refresh == sent.Refresh && frame.ListedCurrent == sent.ListedCurrent && frame.ListedRefresh == sent.ListedRefresh;
                Finish(unchanged ? "REJECTED" : "UNKNOWN", unchanged ? "stone_server_rejected" : "stone_rejected_readback_changed");
                return;
            }
            if (frame.Silver != sent.Silver - 1 || frame.Stones != expectedStones || frame.Double != expectedDouble ||
                frame.Current < sent.Current || frame.Current > sent.Current + 1)
            { Finish("UNKNOWN", "stone_stock_readback_conflict"); return; }
            // 0x106 may rebuild the latest row after 0x163 synchronized both
            // caches. Compare their values to confirm account progress separately
            // from the silver callback, which has no request nonce.
            if (activityDispatch != 0 && activity.ReturnedForDispatch(activityDispatch) && SettledCounters(frame))
            {
                if (Expired()) { Finish("UNKNOWN", "stone_settlement_timed_out"); return; }
                progressObserved = true;
                status = "SETTLED"; reason = "stone_progress_observed"; since = Environment.TickCount;
                Detach(); ObserveCleanup();
            }
        }

        private bool SettledCounters(Frame f)
        {
            return f.Current == sent.Current + 1 && f.ListedCurrent == f.Current &&
                f.Refresh == activityRefresh && f.ListedRefresh == activityRefresh;
        }

        private bool CleanupEvidence(Frame f)
        {
            return f.Error == "" && f.SameOwner(sent) && f.SameTask(sent) && f.SameCost(sent) && SettledCounters(f) &&
                f.Silver == sent.Silver - 1 && f.Stones == expectedStones && f.Double == expectedDouble;
        }

        private void ObserveCleanup()
        {
            if (Expired()) { Finish("UNKNOWN", "stone_cleanup_timed_out"); return; }
            if (frame.Error == "stone_client_busy") return;
            if (!CleanupEvidence(frame) || !frame.SameWindow(opened) || !frame.Visible || frame.Loading || frame.Sending || !frame.Quiet)
                Finish("UNKNOWN", frame.Error != "" ? frame.Error : "stone_cleanup_boundary_changed");
        }

        private string Gate(Frame f, string action)
        {
            if (f.Error != "") return f.Error;
            if (f.Overlay != "PASS") return f.Overlay;
            if (!f.Idle) return "stone_network_busy";
            if (action == "close")
                return status == "SETTLED" && callback == null && activity == null && CleanupEvidence(f) &&
                    f.SameWindow(opened) && f.Visible && f.Enabled &&
                    !f.Loading && !f.Sending && f.Quiet && f.ResultClear ? "" : "stone_cleanup_unavailable";
            if (f.Locked) return "stone_feature_locked";
            if (f.Current != f.ListedCurrent || f.Refresh != f.ListedRefresh) return "stone_activity_panel_stale";
            if (f.Current >= f.Required) return "stone_activity_complete";
            if (f.Silver < 1) return "stone_no_existing_silver_pick";
            if (f.Double < 0 || f.Double >= (f.Level == 16 ? 4 : 3)) return "stone_double_confirmation_required";
            if (!f.ActivityReady) return "stone_activity_callback_unbound";
            if (action == "open")
                return f.Sub == null && !f.Visible && !f.Loading && !f.Sending && f.Quiet ? "" : "stone_clean_stock_entry_required";
            return status == "OPEN" && f.SameOwner(opened) && f.SameWindow(opened) && f.SameProgress(opened) &&
                f.SameResources(opened) && f.Visible && f.Enabled && !f.Loading && !f.Sending && f.Quiet &&
                f.ResultClear ? "" : "stone_owned_idle_panel_required";
        }

        private Frame Read(D context) { return Read(context, false); }

        private Frame Read(D context, bool insideCallback, DevelopmentCallbackCapture ownedActivity = null)
        {
            Frame f = new Frame { Context = context };
            if (context == null || context.Paused || !context.SameOwnership(context) || context.Character.GetType() != Model.FirstType("Database.Character") ||
                !CurrentRoot(context))
            { f.Error = "stone_role_or_root_unavailable"; return f; }
            f.Role = Field<ulong>(context.Character, "id");
            if (!Monitor.TryEnter(context.Client, 0)) { f.Error = "stone_client_busy"; return f; }
            try
            {
                f.Transport = DailyTransport.Read(context.Client);
                if (f.Transport == null) { f.Error = "stone_transport_unavailable"; return f; }
                uint day;
                f.Error = refresh(context.Character, out f.Now, out day, out f.Cycle);
                if (f.Error != "") return f;
                f.Next = Field<uint>(context.Character, "next_resource_time");
                if (!f.Transport.Same(DailyTransport.Read(context.Client)))
                { f.Error = "stone_transport_changed"; return f; }
                IDictionary handlers = Handlers(context.Client);
                f.Handler = handlers[DailyStoneReceipt.Opcode];
                if (!insideCallback)
                {
                    f.Idle = ReadIdle(context.Client, handlers);
                    f.ActivityHandler = handlers[(ushort)355];
                    Delegate push = f.ActivityHandler == null ? null : D.Read(f.ActivityHandler, "handler") as Delegate;
                    f.ActivityReady = ownedActivity == null ? DevelopmentCallbackCapture.IsDailyActivityPush(f.ActivityHandler, push) :
                        WrappedActivityReady(f.ActivityHandler, ownedActivity);
                }
            }
            finally { Monitor.Exit(context.Client); }

            ReadTask(f);
            object role = context.Character;
            f.Silver = Field<int>(role, "sliver_pick"); f.Gold = Field<int>(role, "gold"); f.Bound = Field<int>(role, "bind_gold");
            f.GoldConsumed = Field<uint>(role, "gold_consume"); f.Stones = Field<int>(role, "enchant_stone");
            f.Double = Field<sbyte>(role, "enchant_double"); f.GoldPick = Field<short>(role, "gold_pick_times");
            f.Bought = Field<sbyte>(role, "buy_silverpick");
            uint vip = f.Gold < 0 ? 0u : Field<byte>(role, "vip_level");
            f.Vip = D.Template("vip_template", vip);
            if (f.Vip == null || f.Vip.GetType().FullName != "Database._vip_template" || Field<byte>(f.Vip, "level") != vip ||
                f.Silver < 0 || f.Stones < 0 || f.GoldPick < 0 || f.Bought < 0 || f.Bound < 0 ||
                D.Bool(D.Static(Model.FirstType("BaseAsset"), "_PHONEUI")))
            { f.Error = "stone_stock_quote_schema"; return f; }
            f.Locked = !(bool)D.Method(role.GetType(), "checkUI", new Type[] { typeof(string) }, false, typeof(bool))
                .Invoke(role, new object[] { "enchant_dig" });
            ReadPanel(f, insideCallback);
            return f;
        }

        private static void ReadTask(Frame f)
        {
            object role = f.Context.Character;
            IDictionary rows = Map(D.Read(role, "acts"), 255), rewards = Map(D.Read(role, "act_rewards"), 6);
            Type activityType = Model.FirstType("Database.Activity");
            f.Rows = rows; f.Row = rows[(uint)TaskId];
            f.Template = D.Template("activity", TaskId);
            // Pinned db_template1 row 16 is 采石, category 2, requirement 1.
            // ActivityUI passes idactivity to case 16 (MainUI._enchant).
            if (f.Row == null || f.Row.GetType() != activityType || Field<byte>(f.Row, "idactivity") != TaskId ||
                f.Template == null || f.Template.GetType().FullName != "Database._activity" || !ReferenceEquals(D.Read(f.Row, "temp"), f.Template) ||
                Field<byte>(f.Template, "id") != TaskId || Field<byte>(f.Template, "id_uibutton") != 48 ||
                !Field<bool>(f.Template, "active")) throw new ProbeFailure("stone_activity_identity");
            f.Required = Field<int>(f.Template, "cond_param");
            f.Current = Field<byte>(f.Row, "condition_param"); f.Refresh = Field<int>(f.Row, "refresh_time");
            uint category = Field<byte>(f.Template, "idreward");
            f.Reward = rewards[category];
            if (category != 2 || f.Reward == null || f.Reward.GetType() != Model.FirstType("Database.Activity_Reward") ||
                Field<byte>(f.Reward, "idact_reward") != category || f.Required != 1 || f.Current > f.Required)
                throw new ProbeFailure("stone_activity_counter_schema");
            IDictionary listed = Map(D.Read(f.Reward, "acts"), 255);
            f.PanelRows = listed; f.Listed = listed[(uint)TaskId];
            if (f.Listed == null || f.Listed.GetType() != activityType || Field<byte>(f.Listed, "idactivity") != TaskId ||
                !ReferenceEquals(D.Read(f.Listed, "temp"), f.Template)) throw new ProbeFailure("stone_activity_listing");
            f.ListedCurrent = Field<byte>(f.Listed, "condition_param"); f.ListedRefresh = Field<int>(f.Listed, "refresh_time");
            if (f.ListedCurrent > f.Required) throw new ProbeFailure("stone_activity_listing");
        }

        private static void ReadPanel(Frame f, bool insideCallback)
        {
            D context = f.Context;
            Type type = Model.FirstType("EnchantUI");
            f.Panel = context.GlobalUi.gameObject.GetComponent("EnchantUI") as Component;
            if (f.Panel == null || f.Panel.GetType() != type || !ReferenceEquals(D.Read(f.Panel, "rmg"), context.UiRoot) ||
                f.Panel.transform.root != context.UiRoot.transform) throw new ProbeFailure("stone_stock_panel_identity");
            if (!insideCallback)
            {
                int count = 0;
                foreach (UnityEngine.Object item in context.All)
                {
                    Component c = item as Component;
                    if (c != null && c.GetType() == type && c.gameObject == context.GlobalUi.gameObject) count++;
                }
                if (count != 1) throw new ProbeFailure("stone_stock_panel_unique");
            }
            f.Sub = context.SubUi; f.Selected = ReferenceEquals(f.Sub, f.Panel) && Feature() == "enchant";
            f.Visible = D.Visible(f.Panel); f.Enabled = D.Enabled(f.Panel); f.Loading = D.Loading(f.Panel);
            f.VisibleAt = Field<DateTime>(f.Panel, "visible_time"); f.LoadedAt = Field<DateTime>(f.Panel, "m_LoadOverTime");
            DateTime result = Field<DateTime>(f.Panel, "showTimer"), dragon = Field<DateTime>(f.Panel, "m_EnchantTime");
            f.ResultUntil = result > dragon ? result : dragon;
            f.ResultClear = f.ResultUntil <= DateTime.Now;
            f.Level = Field<int>(f.Panel, "m_diglevel");
            object digging = D.Read(f.Panel, "m_digging");
            if (digging.GetType() != type.GetNestedType("digging_type", BindingFlags.NonPublic) ||
                f.Level != 2 && f.Level != 4 && f.Level != 8 && f.Level != 16) throw new ProbeFailure("stone_stock_panel_schema");
            f.Sending = Field<bool>(f.Panel, "sending");
            f.Quiet = digging.ToString() == "nodigging" && !Field<bool>(f.Panel, "AIdig") && !Field<bool>(f.Panel, "aistate") &&
                Field<bool>(f.Panel, "aiclosed") && !Field<bool>(f.Panel, "vipdig") && !Field<bool>(f.Panel, "subwindow") &&
                !Field<bool>(context.Character, "enchant_dragon_protect");
            if (!insideCallback) f.Overlay = f.Selected ? context.Panels(f.Panel) : context.Panels();
        }

        private static bool CurrentRoot(D context)
        {
            object load = D.Static(Model.FirstType("BaseAsset"), "lds");
            return load != null && ReferenceEquals(D.Read(load, "currmg"), context.Root);
        }

        private static string Feature() { return (string)D.Static(Model.FirstType("__BaseUI"), "subui"); }

        private static IDictionary Handlers(object client) { return Map(D.Read(client, "m_handlers"), 512); }

        private static bool WrappedActivityReady(object registered, DevelopmentCallbackCapture ownedActivity)
        {
            return registered != null && ownedActivity != null && ownedActivity.StillInstalled() &&
                Field<float>(registered, "life") == -1f && D.Read(registered, "arg") == null;
        }

        private static bool ReadIdle(object client, IDictionary handlers)
        {
            IList messages = D.Read(client, "messages") as IList, sends = D.Read(client, "m_sendDatas") as IList;
            if (messages == null || messages.Count > 64 || sends == null || sends.Count > 64)
                throw new ProbeFailure("stone_network_bound");
            if (messages.Count != 0 || sends.Count != 0 || DevelopmentCallbackCapture.AnyCallbackExecuting ||
                handlers.Contains(DailyStoneReceipt.Opcode)) return false;
            Type handlerType = Model.FirstType("game.net.tcp_base+Handler");
            foreach (DictionaryEntry pair in handlers)
            {
                if (!(pair.Key is ushort) || pair.Value == null || pair.Value.GetType() != handlerType)
                    throw new ProbeFailure("stone_network_handler_schema");
                float life = Field<float>(pair.Value, "life");
                if (Single.IsNaN(life) || Single.IsInfinity(life)) throw new ProbeFailure("stone_network_handler_life");
                if (life > 0f) return false;
            }
            return true;
        }

        private static IDictionary Map(object value, int bound)
        {
            IDictionary map = value as IDictionary;
            if (map == null || map.Count > bound) throw new ProbeFailure("stone_cache_bound");
            return map;
        }

        private static T Field<T>(object target, string name)
        {
            object value = D.Read(target, name);
            if (!(value is T)) throw new ProbeFailure("stone_field:" + name);
            return (T)value;
        }

        private bool Expired() { return unchecked((uint)(Environment.TickCount - since)) >= WaitMs; }

        private bool Reject(Intent intent, string failure, out string why)
        {
            if (queued == intent) queued = null;
            if (!stopped) Finish(attempted ? "UNKNOWN" : "REJECTED", failure);
            PublishRevision(frame);
            why = failure;
            return false;
        }

        private void Detach()
        {
            Detach(ref callback);
            Detach(ref activity);
        }

        private static void Detach(ref DevelopmentCallbackCapture capture)
        {
            if (capture != null && !capture.DisarmAndRestore().EndsWith("_forward_only", StringComparison.Ordinal)) capture = null;
        }

        private void Finish(string outcome, string detail)
        { status = outcome; reason = detail; queued = null; Detach(); }

        internal void NotInvoked(string detail)
        {
            if (queued == null) return;
            string ignored;
            Reject(queued, detail, out ignored);
        }

        internal void Stop()
        {
            stopped = true; queued = null;
            if (openUnverified) Finish("UNKNOWN", "stone_open_cleanup_unverified");
            else if (status != "COMPLETED" && status != "REJECTED" && status != "UNKNOWN")
                Finish(attempted ? "UNKNOWN" : "REJECTED", "stone_stopped");
            else Detach();
            PublishRevision(frame);
        }

        private string NextAction()
        {
            if (stopped || queued != null) return "";
            if (status == "OPEN") return Gate(frame, "dig") == "" ? "dig" : "";
            if (status == "SETTLED") return Gate(frame, "close") == "" ? "close" : "";
            return !Busy && Gate(frame, "open") == "" ? "open" : "";
        }

        private void PublishRevision(Frame previous)
        {
            string view = BuildViewJson();
            string next = view + OperationJson();
            if (next == stamp && (ReferenceEquals(frame, previous) || frame.SameAdmission(previous, status != "SETTLED"))) return;
            if (next == stamp && frame.Error != "" && frame.Error == previous.Error) return;
            if (revision == 999999999) throw new ProbeFailure("stone_revision_exhausted");
            stamp = next; publishedView = view; revision++;
        }

        internal string ViewJson()
        { return publishedView ?? BuildViewJson(); }

        private string BuildViewJson()
        {
            string action = NextAction();
            string gate = frame.Error != "" ? frame.Error : action != "" ? "" : status == "OPEN" ? Gate(frame, "dig") :
                status == "SETTLED" ? Gate(frame, "close") : Busy ? reason : Gate(frame, "open");
            return "{\"task_id\":" + TaskId + ",\"role_id\":" + D.Q(frame.Role.ToString(CultureInfo.InvariantCulture)) +
                ",\"cycle\":" + frame.Cycle + ",\"current\":" + frame.Current + ",\"required\":" + frame.Required +
                ",\"silver_picks\":" + Math.Max(0, frame.Silver) + ",\"gold_cost\":" + (action == "dig" ? "0" : "null") +
                ",\"silver_pick_cost\":" + (action == "dig" ? "1" : "null") +
                ",\"next_action\":" + D.Q(action) + ",\"reason\":" + D.Q(gate) + "}";
        }

        internal string OperationJson()
        {
            return "{\"status\":" + D.Q(queued != null ? "QUEUED" : status) + ",\"reason\":" + D.Q(reason) +
                ",\"operation_id\":" + D.Q(operation == null ? "" : operation.Id) + ",\"sequence\":" + lastSequence +
                ",\"mutation_attempted\":" + D.B(attempted) + ",\"stock_callback_returned\":" + D.B(returned) +
                ",\"account_progress_observed\":" + D.B(progressObserved) + ",\"panel_closed\":" + D.B(closed) + "}";
        }
    }
}
