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
    internal delegate D DailyMingshiContextProvider();
    internal delegate bool DailyMingshiInvocationCheck();
    internal delegate bool DailyMingshiInvocationGate(DailyMingshiInvocationCheck prepare, DailyMingshiInvocationCheck check);
    internal delegate DevelopmentCallbackCapture DailyMingshiCallbackInstall(object client, object handler,
        string id, int sequence, Delegate original, Action<byte[]> observe, out string why);

    internal sealed class DailyMingshiReceipt
    {
        internal const ushort Opcode = 1202;
        internal uint Stones;

        // The request requires harvest_times=0 (08 00). The response requires
        // result and defaults gold_consume, stone_output and harvest_times to 0.
        internal static DailyMingshiReceipt Decode(byte[] body)
        {
            if (body == null || body.Length < 2 || body.Length > 24) throw new FormatException("mingshi_reply_bound");
            uint[] fields = new uint[4];
            int offset = 0, seen = 0;
            while (offset < body.Length)
            {
                int tag = body[offset++], field = (tag >> 3) - 1;
                if ((tag & 7) != 0 || field < 0 || field >= fields.Length || (seen & (1 << field)) != 0)
                    throw new FormatException("mingshi_reply_field");
                seen |= 1 << field;
                fields[field] = UInt(body, ref offset);
            }
            if ((seen & 1) == 0 || fields[0] != 0 || fields[1] != 0 || fields[2] == 0 ||
                fields[2] > Int32.MaxValue || fields[3] != 1) throw new FormatException("mingshi_reply_not_free_success");
            return new DailyMingshiReceipt { Stones = fields[2] };
        }

        private static uint UInt(byte[] body, ref int offset)
        {
            uint value = 0;
            for (int shift = 0; shift <= 28 && offset < body.Length; shift += 7)
            {
                byte part = body[offset++];
                if (shift == 28 && part > 15) break;
                value |= (uint)(part & 127) << shift;
                if ((part & 128) == 0)
                {
                    if (shift == 0 || part != 0) return value;
                    break;
                }
            }
            throw new FormatException("mingshi_reply_scalar");
        }
    }

    internal sealed class DailyMingshiModel
    {
        internal const int TaskId = 7;
        private const uint Category = 2, WaitMs = 18000;
        private const string PageVerified = "mingshi_page_harvest_verified", NoReplyProcessed = "mingshi_harvest_submitted_no_reply";
        private const ushort ActivityOpcode = 355, CampaignEnterOpcode = 1025;
        private static readonly Type[] ResponseArgs = { typeof(MemoryStream), typeof(object) };

        private sealed class Frame
        {
            internal D Context;
            internal DailyTransport Transport;
            internal Component Loader, Main, Campaign, Scene, Panel, OldRoot, OldMain, LevelRoot;
            internal GameObject Mesh, StoneMesh;
            internal Collider Collider;
            internal Camera Camera;
            internal object Rows, Row, Template, Rewards, Reward, Listed, Vip, Prices, Quote, ActivityHandler, HarvestHandler, CampaignEnterHandler;
            internal Delegate ActivityOriginal;
            internal ulong Role;
            internal uint Now, Cycle, Next, GoldConsumed;
            internal int Current, Required, Refresh, Gold, Bound, Stones, QuoteGold, QueueHarvest, CampaignId;
            internal byte Harvests, VipLevel, Entitlement;
            internal DateTime VisibleAt;
            internal bool Idle, Visible, Ready, MainReady, EntryAllowed, GodsCleared;
            internal string Error = "", PriceGate = "", UiGate = "", GodsGate = "";

            internal bool SameOwner(Frame other)
            {
                return other != null && Context != null && other.Context != null && Loader != null &&
                    ReferenceEquals(Loader, other.Loader) && ReferenceEquals(Context.UiRoot, other.Context.UiRoot) &&
                    ReferenceEquals(Context.GlobalUi, other.Context.GlobalUi) && ReferenceEquals(Context.Character, other.Context.Character) &&
                    ReferenceEquals(Context.Client, other.Context.Client) && Role != 0 && Role == other.Role && Transport != null &&
                    Transport.Same(other.Transport) && Cycle != 0 && Cycle == other.Cycle && Next == other.Next && Now >= other.Now;
            }
            internal bool SameTask(Frame other)
            {
                return ReferenceEquals(Rows, other.Rows) && ReferenceEquals(Rewards, other.Rewards) &&
                    ReferenceEquals(Template, other.Template) && ReferenceEquals(Reward, other.Reward) &&
                    ReferenceEquals(Listed, other.Listed) && Required == other.Required;
            }
            internal bool SameCounter(Frame other)
            { return SameTask(other) && ReferenceEquals(Row, other.Row) && Current == other.Current && Refresh == other.Refresh; }
            internal bool SamePremium(Frame other)
            { return Gold == other.Gold && Bound == other.Bound && GoldConsumed == other.GoldConsumed; }
            internal bool SameWallet(Frame other)
            { return SamePremium(other) && Stones == other.Stones && Harvests == other.Harvests; }
            internal bool SameQuote(Frame other)
            {
                return ReferenceEquals(Vip, other.Vip) && ReferenceEquals(Prices, other.Prices) &&
                    ReferenceEquals(Quote, other.Quote) && VipLevel == other.VipLevel &&
                    Entitlement == other.Entitlement && QuoteGold == other.QuoteGold && PriceGate == other.PriceGate;
            }
            internal bool SameScene(Frame other)
            {
                return SameOwner(other) && ReferenceEquals(Scene, other.Scene) && ReferenceEquals(Panel, other.Panel) &&
                    ReferenceEquals(Context.Root, other.Context.Root) && ReferenceEquals(OldRoot, other.OldRoot) &&
                    ReferenceEquals(OldMain, other.OldMain) && ReferenceEquals(LevelRoot, other.LevelRoot) &&
                    ReferenceEquals(Mesh, other.Mesh) && ReferenceEquals(StoneMesh, other.StoneMesh) &&
                    ReferenceEquals(Collider, other.Collider) && ReferenceEquals(Camera, other.Camera) &&
                    VisibleAt == other.VisibleAt;
            }
            internal bool SameAdmission(Frame other)
            {
                return Error == "" && other.Error == "" && SameScene(other) && SameCounter(other) && SameWallet(other) &&
                    SameQuote(other) && ReferenceEquals(Main, other.Main) && ReferenceEquals(Campaign, other.Campaign) && CampaignId == other.CampaignId &&
                    ReferenceEquals(ActivityHandler, other.ActivityHandler) &&
                    ReferenceEquals(ActivityOriginal, other.ActivityOriginal) && ReferenceEquals(HarvestHandler, other.HarvestHandler) &&
                    UiGate == other.UiGate && Ready == other.Ready && Visible == other.Visible && Idle == other.Idle &&
                    MainReady == other.MainReady && EntryAllowed == other.EntryAllowed &&
                    GodsCleared == other.GodsCleared && GodsGate == other.GodsGate;
            }
        }

        private sealed class Intent
        {
            internal Frame Baseline;
            internal string Id, Action;
            internal int Sequence, QueuedAt;
        }

        private readonly DailyMingshiCallbackInstall installResponse, installActivity;
        private readonly DailyRefreshAdmission refresh;
        private readonly Hashtable operationIds = new Hashtable();
        private Frame frame = new Frame(), origin, opened, sent, settled;
        private Component cleanupRoot;
        private Intent queued, operation;
        private DevelopmentCallbackCapture response, activity;
        private object responseHandler, campaignReturnHandler;
        private DailyMingshiReceipt receipt;
        private DailyTimedWire.Progress progress;
        private int revision, lastSequence, started;
        private bool stopped, attempted, returned, observed, closed, capitalReturnStarted, cleanupReturn;
        private bool harvestSubmitted, pageConfirmed, processedWithoutReply;
        private string status = "IDLE", reason = "mingshi_unobserved", callbackError, stamp = "", cleanupReason = "";

        internal DailyMingshiModel(DailyMingshiCallbackInstall harvestInstaller, DailyMingshiCallbackInstall activityInstaller)
            : this(harvestInstaller, activityInstaller, DailyRefreshGate.Read) { }

#if JSB_DAILY_MINGSHI_TEST
        internal
#else
        private
#endif
        DailyMingshiModel(DailyMingshiCallbackInstall harvestInstaller, DailyMingshiCallbackInstall activityInstaller, DailyRefreshAdmission readRefresh)
        { installResponse = harvestInstaller; installActivity = activityInstaller; refresh = readRefresh; }

        internal int Revision { get { return revision; } }
        internal bool MutationAttempted { get { return attempted; } }
        internal bool OutcomeUnknown { get { return status == "UNKNOWN"; } }
        internal bool Unresolved { get { return status == "UNKNOWN" || status == "PENDING"; } }
        internal bool Busy { get { return queued != null || BusyWithoutQueue(); } }
        private bool BusyWithoutQueue()
        {
            // The origin is latched immediately before calling the stock
            // opener. Terminal status alone proves nothing about this scene.
            return origin != null || response != null || activity != null || Unresolved;
        }

        internal void Capture(D context)
        {
            Frame previous = frame;
            object owner = origin == null ? null : origin.Context.Client;
            if (owner != null && !Monitor.TryEnter(owner, 0))
            {
                if (WindowActive() && Expired())
                {
                    EndFailure("mingshi_owned_client_busy");
                    UpdateRevision(previous);
                }
                return;
            }
            try
            {
                frame = Read(context);
                if (stopped || !WindowActive())
                {
                    Detach();
                    // A later manual return may resolve only the UI hold.
                    // An uncertain mutation still requires session recovery.
                    if (!Unresolved && origin != null && ReturnVerified(frame))
                    { closed = true; origin = null; }
                }
                else if (status == "OPENING") ObserveOpen();
                else if (status == "OPEN")
                {
                    if (frame.Error != "") EndFailure(frame.Error);
                    else if (frame.GodsGate != "" || !frame.GodsCleared)
                        EndFailure(frame.GodsGate != "" ? frame.GodsGate : "divinity_clear_required");
                    else if (frame.Error != "" || !frame.SameScene(opened) || !frame.SameCounter(opened) ||
                        !frame.SameWallet(opened) || !frame.SameQuote(opened) || !frame.Ready || Expired())
                        EndFailure("mingshi_owned_panel_changed");
                    else if (!StockActivityOwned(frame))
                        EndFailure("mingshi_stock_355_replaced");
                }
                else if (status == "PENDING") ObserveResult();
                else if (status == "SETTLED") ObserveSettled();
                else if (status == "CLOSING") ObserveClose();
            }
            catch (Exception)
            {
                EndFailure("mingshi_observation_failed");
            }
            finally { if (owner != null) Monitor.Exit(owner); }
            UpdateRevision(previous);
        }

        private bool WindowActive()
        {
            return status == "OPENING" || status == "OPEN" || status == "PENDING" || status == "SETTLED" || status == "CLOSING";
        }
        internal bool Queue(int taskId, ulong role, string id, int sequence, int expectedRevision, string action, out string why)
        {
            why = "mingshi_admission";
            if (taskId != TaskId || role == 0 || role != frame.Role || sequence <= lastSequence || expectedRevision != revision ||
                queued != null || stopped || !new DevelopmentCommand { OperationId = id }.ValidId()) return false;
            if (action != "open" && action != "harvest" && action != "close" && action != "cleanup") return false;
            bool newOperation = action == "open" || action == "cleanup" && origin == null;
            if (newOperation ? operationIds.Count >= 4096 || operationIds.Contains(id) : operation == null || operation.Id != id)
                return false;
            why = action != "open" && action != "cleanup" && Expired() ? "mingshi_gui_deadline_expired" : Gate(frame, action);
            if (why != "") return false;
            lastSequence = sequence;
            queued = new Intent { Baseline = frame, Id = id, Action = action, Sequence = sequence, QueuedAt = Environment.TickCount };
            if (newOperation)
            {
                operationIds.Add(id, true);
                operation = queued;
                origin = opened = sent = settled = null;
                cleanupRoot = null;
                campaignReturnHandler = null;
                attempted = returned = observed = closed = capitalReturnStarted = cleanupReturn = false;
                harvestSubmitted = pageConfirmed = processedWithoutReply = false;
                cleanupReason = "";
                receipt = null; progress = null; callbackError = null;
                status = "IDLE"; reason = "";
            }
            UpdateRevision(frame);
            return true;
        }

        internal bool ExecuteGui(DailyMingshiContextProvider fresh, DevelopmentPublication publish, DailyMingshiInvocationGate admit, out string why)
        {
            why = "mingshi_not_queued";
            Intent intent = queued;
            if (intent == null || stopped) return false;
            if (!D.Gui || fresh == null || publish == null || admit == null)
                return Reject("mingshi_gui_required", out why);
            object client = intent.Baseline.Context.Client;
            if (!Monitor.TryEnter(client, 0)) return Reject("mingshi_client_busy", out why);
            bool controller = false;
            try
            {
                Frame current = Read(fresh());
                why = FinalAdmission(current, intent);
                if (why != "") return Reject(why, out why);
                controller = true; publish(); controller = false;
                MethodInfo method = null;
                MethodInfo stockResponse = null;
                string admissionReason = "mingshi_lease_changed_before_call", admittedStatus = status;
                bool allowed = admit(delegate
                {
                    if (stopped || queued != intent || status != admittedStatus || !D.Gui) return false;
                    current = Read(fresh());
                    admissionReason = FinalAdmission(current, intent);
                    if (admissionReason != "") return false;
                    method = StockMethod(current, intent.Action);
                    if (intent.Action == "harvest")
                    {
                        stockResponse = D.Method(current.Panel.GetType(), "sc_god_harvest_0x4B2", ResponseArgs, false, typeof(void));
                        if (!stockResponse.IsPrivate || stockResponse.DeclaringType != current.Panel.GetType() ||
                            installResponse == null || installActivity == null || !InstallActivity(current, intent, out admissionReason))
                        { if (admissionReason == "") admissionReason = "mingshi_activity_callback_unbound"; return false; }
                    }
                    return true;
                }, delegate { return StillAdmissible(current, intent, admittedStatus); });
                if (stopped || queued != intent) { Detach(); why = "mingshi_cancelled_before_call"; return false; }
                if (!allowed || method == null)
                    return Reject(admissionReason == "" ? "mingshi_lease_changed_before_call" : admissionReason, out why);
                queued = null;
                started = Environment.TickCount;
                if (intent.Action == "open")
                {
                    origin = current;
                    status = "OPENING"; reason = "mingshi_stock_loading";
                    method.Invoke(null, null);
                }
                else if (intent.Action == "harvest") Harvest(current, intent, method, stockResponse);
                else
                {
                    if (intent.Action == "cleanup")
                    {
                        cleanupRoot = origin == null ? ReturnRoot(current) : origin.Context.Root;
                        if (origin == null) origin = current;
                        cleanupReturn = true;
                        cleanupReason = reason == "" ? "mingshi_existing_page_returned" : reason;
                    }
                    InvokeReturn(current, method);
                }
                UpdateRevision(frame);
                why = status == "UNKNOWN" || status == "REJECTED" ? reason : "";
                return why == "";
            }
            catch (Exception)
            {
                if (controller)
                {
                    NotInvoked("mingshi_controller_refused");
                    throw;
                }
                return Reject(attempted && !observed ? "mingshi_send_or_callback_binding_failed" : "mingshi_stock_call_failed", out why);
            }
            finally { Monitor.Exit(client); }
        }

        private bool StillAdmissible(Frame current, Intent intent, string admittedStatus)
        {
            if (stopped || queued != intent || status != admittedStatus || !D.Gui || AdmissionExpired(intent)) return false;
            D context = current.Context;
            object client = context.Client;
            // Check volatile state after the controller's command read.
            // The client monitor spans preparation, admission and the stock call.
            return ReferenceEquals(client, DailyTransport.CurrentClient(context.Character)) && current.Transport.StillCurrent() &&
                Field<ulong>(context.Character, "id") == current.Role &&
                ReferenceEquals(D.Static(Model.FirstType("BaseAsset"), "lds"), current.Loader) &&
                ReferenceEquals(D.Read(current.Loader, "currmg"), context.Root) &&
                ReferenceEquals(D.Read(context.GlobalUi, "level_rmg"), current.LevelRoot) &&
                D.Bool(D.Static(Model.FirstType("Global"), "extern_enabled")) && context.SubUi == null &&
                !DevelopmentCallbackCapture.AnyCallbackExecuting && ((IList)D.Read(client, "messages")).Count == 0 &&
                ((IList)D.Read(client, "m_sendDatas")).Count == 0 &&
                (intent.Action != "harvest" || Installed(activity, ActivityOpcode, intent));
        }

        private string FinalAdmission(Frame current, Intent intent)
        {
            if (AdmissionExpired(intent)) return "mingshi_gui_deadline_expired";
            string why = Gate(current, intent.Action);
            return why != "" ? why : current.SameAdmission(intent.Baseline) ? "" : "mingshi_gui_baseline_changed";
        }

        private static MethodInfo StockMethod(Frame current, string action)
        {
            Type type = action == "open" ? Model.GameType("MainUI") : current.Panel.GetType();
            string name = action == "open" ? "_resource_collect" : action == "harvest" ? "wantHarvest" : "_return";
            MethodInfo method = D.Method(type, name, Type.EmptyTypes, action == "open", typeof(void));
            if (method.DeclaringType != type || (action == "close" || action == "cleanup" ? !method.IsPrivate : !method.IsPublic))
                throw new ProbeFailure("mingshi_stock_method");
            return method;
        }

        private bool InstallActivity(Frame current, Intent intent, out string why)
        {
            activity = installActivity(current.Context.Client, current.ActivityHandler, intent.Id, intent.Sequence,
                current.ActivityOriginal, ReadProgress, out why);
            return Installed(activity, ActivityOpcode, intent);
        }

        private static bool Installed(DevelopmentCallbackCapture value, int opcode, Intent intent)
        {
            return value != null && value.Opcode == opcode && value.Sequence == intent.Sequence && value.OperationId == intent.Id &&
                value.StillInstalled() && value.Read().Calls == 0;
        }

        private void Harvest(Frame current, Intent intent, MethodInfo method, MethodInfo stockResponse)
        {
            sent = current;
            attempted = true;
            status = "PENDING"; reason = "mingshi_waiting_1202_and_355";
            // The normal button sends exactly one 1202. The client monitor
            // excludes dispatch between the idle check, send and wrapping.
            method.Invoke(current.Panel, null);
            harvestSubmitted = true;
            if (!current.Transport.Same(DailyTransport.Read(DailyTransport.CurrentClient(current.Context.Character))))
            { End("UNKNOWN", "mingshi_transport_changed_after_send"); return; }
            responseHandler = Handlers(current.Context.Client)[DailyMingshiReceipt.Opcode];
            Delegate original = Original(responseHandler, stockResponse, current.Panel, 10f);
            if (original == null) { End("UNKNOWN", "mingshi_stock_1202_not_registered"); return; }
            string why;
            response = installResponse(current.Context.Client, responseHandler, intent.Id, intent.Sequence,
                original, ReadReceipt, out why);
            if (!Installed(response, DailyMingshiReceipt.Opcode, intent))
                End("UNKNOWN", why == "" ? "mingshi_stock_1202_unbound" : why);
        }
        private void ObserveOpen()
        {
            if (frame.Error != "" || !OwnScene(frame) || !frame.SameCounter(origin) ||
                !frame.SameWallet(origin) || !frame.SameQuote(origin) || Expired())
            { End("REJECTED", "mingshi_stock_open_unconfirmed"); return; }
            if (!StockActivityOwned(frame))
            { End("REJECTED", "mingshi_stock_355_replaced"); return; }
            if (!frame.Ready) return;
            opened = frame; started = Environment.TickCount;
            status = "OPEN"; reason = "mingshi_stock_panel_ready";
        }

        private void ObserveResult()
        {
            // Read the current role/day projection before asking whether an
            // observer saw either business packet. Missing packets are not a
            // failure once this operation's stock invocation returned.
            if (!RefreshResult()) return;
            status = "SETTLED"; reason = ResultReason(); started = Environment.TickCount;
            if (frame.Idle) Detach();
        }

        private void ObserveSettled()
        {
            if (!RefreshResult()) return;
            if (Expired()) { EndFailure("mingshi_native_handler_or_ui_unreleased"); return; }
            // Keep the normal 1202 callback bound while its native 10-second
            // registration lives. Its null timeout is sufficient; no response
            // body is required. Returning earlier can destroy stone_mesh which
            // a late stock callback still dereferences.
            if (frame.Idle) Detach();
            reason = ResultReason();
        }

        private bool RefreshResult()
        {
            if (!harvestSubmitted || sent == null || frame.Error != "" || !frame.SameScene(sent) ||
                frame.Required != 1 || frame.Required != sent.Required || frame.Harvests > 1 ||
                frame.Current < sent.Current || frame.Harvests < sent.Harvests)
            { End("UNKNOWN", "mingshi_result_context_or_wallet_changed"); return false; }
            bool confirmed = PageCompleted(frame);
            if (!confirmed && !frame.SamePremium(sent))
            { End("UNKNOWN", "mingshi_unconfirmed_wallet_changed"); return false; }
            if (observed && frame.Current != frame.Required)
            { End("UNKNOWN", "mingshi_page_progress_regressed"); return false; }
            if (pageConfirmed && !confirmed)
            { End("UNKNOWN", "mingshi_page_result_regressed"); return false; }
            // A newly read completed page is the primary outcome evidence.
            // In its absence an explicit callback rejection remains a failure.
            if (!confirmed && callbackError != null)
            { End("UNKNOWN", callbackError); return false; }
            if (response != null)
            {
                DevelopmentCallbackWitness stock = response.Read();
                if (!confirmed && (Failed(stock) || stock.Calls > 1))
                { End("UNKNOWN", "mingshi_callback_changed_or_failed"); return false; }
                if (stock.Calls == 1 && stock.Returned && !stock.NullResponse && receipt != null) returned = true;
            }
            if (activity != null && !confirmed && Failed(activity.Read()))
            { End("UNKNOWN", "mingshi_activity_callback_failed"); return false; }
            pageConfirmed = confirmed;
            observed = frame.Current == frame.Required && frame.Required == 1;
            processedWithoutReply = !confirmed;
            settled = frame;
            return true;
        }

        private bool PageCompleted(Frame current)
        {
            return sent != null && current.SameOwner(sent) && current.Required == sent.Required &&
                (current.Current == current.Required && current.Required == 1 ||
                 current.Harvests == 1 && sent.Harvests == 0);
        }

        private string ResultReason()
        { return pageConfirmed ? PageVerified : processedWithoutReply ? NoReplyProcessed : "mingshi_stock_return_verified"; }

        private static bool Failed(DevelopmentCallbackWitness value)
        { return !value.Armed || value.Error != null || value.OriginalThrew; }

        private void ReadReceipt(byte[] body)
        {
            if (receipt != null || !CallbackOwner()) { callbackError = "mingshi_1202_context_changed"; return; }
            try { receipt = DailyMingshiReceipt.Decode(body); }
            catch (FormatException error) { callbackError = error.Message; }
        }

        private void ReadProgress(byte[] body)
        {
            DailyTimedWire.Progress value;
            if (!CallbackOwner() || !DailyTimedWire.ReadProgress(body, TaskId, out value))
            { callbackError = "mingshi_355_context_or_schema"; return; }
            if (value == null) return;
            if (progress != null || value.Count != sent.Required || value.Refresh < 0)
            { callbackError = "mingshi_355_conflicting_progress"; return; }
            progress = value;
        }

        private bool CallbackOwner()
        {
            if (stopped || sent == null || status != "PENDING" && status != "SETTLED" || Expired()) return false;
            object role = sent.Context.Character;
            uint now, day, cycle;
            return sent.Transport.Same(DailyTransport.Read(DailyTransport.CurrentClient(role))) && Field<ulong>(role, "id") == sent.Role &&
                refresh(role, out now, out day, out cycle) == "" && now >= sent.Now && cycle == sent.Cycle &&
                Field<uint>(role, "next_resource_time") == sent.Next &&
                ReferenceEquals(D.Static(Model.FirstType("BaseAsset"), "lds"), sent.Loader) &&
                ReferenceEquals(D.Read(sent.Loader, "currmg"), sent.Scene) && ReferenceEquals(D.Read(sent.Loader, "oldmainrmg"), origin.Context.Root) &&
                ReferenceEquals(D.Read(sent.Context.GlobalUi, "level_rmg"), sent.Scene) &&
                ReferenceEquals(D.Static(Model.GameType("DivinityUI"), "stone_mesh"), sent.StoneMesh) &&
                sent.StoneMesh != null && sent.StoneMesh.collider == sent.Collider && sent.Camera != null && ReferenceEquals(Camera.main, sent.Camera) &&
                Field<int>(role, "gold") == sent.Gold && Field<int>(role, "bind_gold") == sent.Bound &&
                Field<uint>(role, "gold_consume") == sent.GoldConsumed;
        }

        private void InvokeReturn(Frame current, MethodInfo method)
        {
            status = "CLOSING"; reason = harvestSubmitted ? ResultReason() : "mingshi_waiting_stock_return";
            method.Invoke(current.Panel, null);
            if (cleanupReturn ? Field<string>(cleanupRoot, "LevelID") == "Capital" : origin.Campaign == null)
            {
                // The Capital branch selects its destination synchronously,
                // even though its stock assets may finish loading later.
                Component home = cleanupReturn ? cleanupRoot : origin.Context.Root;
                capitalReturnStarted = ReferenceEquals(D.Read(current.Loader, "currmg"), home) &&
                    ReferenceEquals(D.Read(current.Context.GlobalUi, "level_rmg"), home);
                return;
            }
            // Campaign LoadLevel calls the stock read-only 1025 with the
            // Divinity root as arg. Leave its callback installed as-is.
            object handler = Handlers(current.Context.Client)[CampaignEnterOpcode];
            MethodInfo callback = D.Method(Model.GameType("RootUI"), "sc_campaign_enter", ResponseArgs, false, typeof(void));
            if (callback.IsPrivate && Original(handler, callback, current.Context.GlobalUi, 10f, current.Scene) != null)
                campaignReturnHandler = handler;
        }

        private void ObserveClose()
        {
            if (harvestSubmitted && sent != null && frame.Error == "" && PageCompleted(frame))
            {
                pageConfirmed = true; processedWithoutReply = false;
                observed = frame.Current == frame.Required && frame.Required == 1;
            }
            bool unchanged = cleanupReturn ? frame.SameCounter(origin) && frame.SameWallet(origin) : SettledUnchanged(frame);
            Component home = cleanupReturn ? cleanupRoot : origin.Context.Root;
            if (Expired() || frame.Error != "" || !frame.SameOwner(origin) || !unchanged ||
                (!ReferenceEquals(frame.Context.Root, home) && !ReferenceEquals(frame.Context.Root, origin.Scene)) ||
                (frame.CampaignEnterHandler != null && !ReferenceEquals(frame.CampaignEnterHandler, campaignReturnHandler)))
            { EndFailure("mingshi_stock_return_unconfirmed"); return; }
            if (((cleanupReturn ? Field<string>(home, "LevelID") == "Capital" : origin.Campaign == null) ?
                capitalReturnStarted : campaignReturnHandler != null) && ReturnVerified(frame))
            {
                if (Expired()) { EndFailure("mingshi_stock_return_unconfirmed"); return; }
                closed = true;
                origin = null;
                End(cleanupReturn ? "REJECTED" : "SUCCEEDED", cleanupReturn ? cleanupReason : ResultReason());
            }
        }

        // A stock page can survive a previous bridge instance. Adopt only its
        // read-only return destination; never infer permission to harvest it.
        private static Component ReturnRoot(Frame current)
        {
            if (current == null || !current.Ready || current.Scene == null ||
                !ReferenceEquals(current.Context.Root, current.Scene) || !ReferenceEquals(current.LevelRoot, current.Scene) ||
                current.OldMain == null || !ReferenceEquals(current.OldRoot, current.OldMain) ||
                current.OldMain.GetType() != Model.FirstType("RootMessenger") ||
                current.OldMain.transform.root != current.OldMain.transform) return null;
            string level = Field<string>(current.OldMain, "LevelID");
            if (level != "Campaign" && level != "Capital") return null;
            return current.OldMain;
        }

        private bool SettledUnchanged(Frame current)
        {
            return settled != null && sent != null && current.SameOwner(sent) && current.Required == sent.Required &&
                current.Current >= sent.Current && current.Harvests >= sent.Harvests && current.Harvests <= 1 &&
                (pageConfirmed ? PageCompleted(current) : current.SamePremium(sent));
        }

        private bool ReturnVerified(Frame current)
        {
            if (cleanupReturn)
                return origin != null && current.Error == "" && current.SameOwner(origin) &&
                    ReferenceEquals(current.Context.Root, cleanupRoot) && ReferenceEquals(current.LevelRoot, cleanupRoot) &&
                    current.MainReady && ReferenceEquals(current.Scene, origin.Scene) && ReferenceEquals(current.Panel, origin.Panel) &&
                    !current.Visible && !D.Loading(current.Panel) && !Field<bool>(current.Scene, "bLoading") &&
                    Field<int>(current.Scene, "ExpectingCount") == 0 && current.Context.SubUi == null && current.Idle &&
                    current.CampaignEnterHandler == null && StockActivityOwned(current) && response == null && activity == null &&
                    current.SameCounter(origin) && current.SameWallet(origin);
            // Both stock root pointers must return to the original root,
            // and Campaign must project the original map id in the same UI.
            return origin != null && current.Error == "" && current.SameOwner(origin) &&
                ReferenceEquals(current.Context.Root, origin.Context.Root) && ReferenceEquals(current.LevelRoot, origin.Context.Root) &&
                current.MainReady && ReferenceEquals(current.Main, origin.Main) && ReferenceEquals(current.Campaign, origin.Campaign) &&
                current.CampaignId == origin.CampaignId && ReferenceEquals(current.Scene, origin.Scene) &&
                current.Panel != null && ReferenceEquals(current.Panel, origin.Panel) && !current.Visible && !D.Loading(current.Panel) &&
                !Field<bool>(current.Scene, "bLoading") && Field<int>(current.Scene, "ExpectingCount") == 0 &&
                !(bool)D.Method(current.Panel.GetType(), "IsVisible", Type.EmptyTypes, false, typeof(bool)).Invoke(current.Panel, null) &&
                current.Context.SubUi == null && current.Idle && current.CampaignEnterHandler == null &&
                StockActivityOwned(current) &&
                response == null && activity == null &&
                (harvestSubmitted ? SettledUnchanged(current) : current.SameCounter(origin) && current.SameWallet(origin));
        }
        private bool StockActivityOwned(Frame current)
        {
            return ReferenceEquals(current.ActivityHandler, origin.ActivityHandler) &&
                ReferenceEquals(current.ActivityOriginal, origin.ActivityOriginal);
        }

        private bool OwnScene(Frame current)
        {
            return origin != null && current.SameOwner(origin) && ReferenceEquals(current.Panel, origin.Panel) &&
                ReferenceEquals(current.Scene, origin.Scene) && ReferenceEquals(current.Context.Root, origin.Scene) &&
                ReferenceEquals(current.LevelRoot, origin.Scene) && ReferenceEquals(current.OldRoot, origin.Context.Root) &&
                ReferenceEquals(current.OldMain, origin.Context.Root);
        }

        private string Gate(Frame f, string action)
        {
            // Admission/publication must report a failed read as a refusal,
            // rather than throwing through Probe's process recovery boundary.
            try { return GateCore(f, action); }
            catch (Exception error)
            { return error is ProbeFailure ? error.Message : "mingshi_admission_read_failed"; }
        }

        private string GateCore(Frame f, string action)
        {
            if (stopped || f.Error != "") return stopped ? "mingshi_stopped" : f.Error;
            if (f.Context == null || f.Context.Paused || f.Transport == null || f.Role == 0 || f.Cycle == 0 ||
                !f.Idle || f.ActivityOriginal == null) return "mingshi_context_or_network_not_idle";
            if (action == "cleanup")
            {
                if (attempted || response != null || activity != null || Unresolved || !f.Ready)
                    return "mingshi_unmutated_return_unavailable";
                if (origin == null) return status == "IDLE" && ReturnRoot(f) != null ? "" : "mingshi_return_owner_unconfirmed";
                // A definite pre-invocation Queue refusal may leave an OPEN
                // page. Its owner can abandon it through the same stock
                // return, without ever retrying an uncertain harvest.
                return (!WindowActive() || status == "OPEN") && OwnScene(f) && f.SameCounter(origin) && f.SameWallet(origin) &&
                    StockActivityOwned(f) ? "" : "mingshi_return_owner_unconfirmed";
            }
            if (action == "close")
                return status == "SETTLED" && response == null && activity == null && f.Ready && f.SameScene(sent) &&
                    SettledUnchanged(f) && StockActivityOwned(f) ? "" : "mingshi_owned_return_unavailable";
            if (f.GodsGate != "") return f.GodsGate;
            if (f.Current != 0 || f.Required != 1) return "mingshi_daily_progress_already_present";
            if (f.PriceGate != "") return f.PriceGate;
            if (action == "open")
                return !BusyWithoutQueue() && f.MainReady && f.EntryAllowed && f.Panel != null && !f.Visible ? "" : "mingshi_main_entry_unavailable";
            if (origin != null && !StockActivityOwned(f)) return "mingshi_stock_355_replaced";
            if (!f.GodsCleared) return "divinity_clear_required";
            return status == "OPEN" && f.Ready && f.SameScene(opened) && f.SameCounter(opened) &&
                f.SameWallet(opened) && f.SameQuote(opened) ? "" : "mingshi_owned_harvest_unavailable";
        }

        private static bool GodsCleared(object role)
        {
            IList troops = D.Read(role, "god_troops") as IList;
            Type type = Model.FirstType("Database.GodTroop");
            // Stock GodTroop exposes killed as a bool property backed by
            // data_.killed. SharedDevelopmentContext.Read is field-only.
            // Resolve this exact stock shape here, without relaxing D.Read.
            PropertyInfo property = type.GetProperty("killed", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            MethodInfo getter = property == null ? null : property.GetGetMethod(false);
            if (property == null || property.PropertyType != typeof(bool) || property.GetIndexParameters().Length != 0 ||
                getter == null || getter.IsStatic || getter.DeclaringType != type || getter.ReturnType != typeof(bool) ||
                getter.GetParameters().Length != 0) throw new ProbeFailure("mingshi_property_schema:killed");
            if (troops == null || troops.Count != 5) throw new ProbeFailure("mingshi_god_troops_schema");
            bool cleared = true;
            foreach (object troop in troops)
            {
                if (troop == null || troop.GetType() != type) throw new ProbeFailure("mingshi_god_troops_schema");
                object value = getter.Invoke(troop, null);
                if (!(value is bool)) throw new ProbeFailure("mingshi_property_schema:killed");
                if (!(bool)value) cleared = false;
            }
            return cleared;
        }

        private Frame Read(D context)
        {
            Frame f = new Frame { Context = context };
            try
            {
                if (context == null || context.Character == null || context.Paused) throw new ProbeFailure("mingshi_context_unavailable");
                object role = context.Character;
                object client = DailyTransport.CurrentClient(role);
                if (!ReferenceEquals(client, context.Client)) throw new ProbeFailure("mingshi_client_identity");
                f.Transport = DailyTransport.Read(client);
                f.Role = Field<ulong>(role, "id");
                if (f.Transport == null || f.Role == 0 || f.Role.ToString(CultureInfo.InvariantCulture) != context.CharacterId ||
                    Field<bool>(role, "gm")) throw new ProbeFailure("mingshi_role_or_transport");
                uint day;
                string refreshGate = refresh(role, out f.Now, out day, out f.Cycle);
                f.Next = Field<uint>(role, "next_resource_time");
                if (refreshGate != "") throw new ProbeFailure(refreshGate);
                f.Gold = Field<int>(role, "gold"); f.Bound = Field<int>(role, "bind_gold");
                f.GoldConsumed = Field<uint>(role, "gold_consume");
                f.Stones = Field<int>(role, "stone"); f.Harvests = Field<byte>(role, "god_bonus_today");
                if (f.Gold < 0 || f.Bound < 0 || f.Stones < 0) throw new ProbeFailure("mingshi_wallet_unavailable");
                ReadCounter(f, role);
                ReadQuote(f, role);
                ReadUi(f);
                ReadNetwork(f, client);
                // Harvest admission is sampled with the frame. Serialization
                // and Gate must never invoke a reflective stock getter. A
                // deterministic schema error blocks opening/harvest, while
                // the independently verified unmutated return stays usable.
                try { f.GodsCleared = GodsCleared(role); }
                catch (Exception error)
                { f.GodsGate = error is ProbeFailure ? error.Message : "mingshi_god_troops_read_failed"; }
            }
            catch (Exception error)
            { f.Error = error is ProbeFailure ? error.Message : "mingshi_observation_failed"; }
            return f;
        }

        private static void ReadCounter(Frame f, object role)
        {
            IDictionary acts = Map(D.Read(role, "acts"), 255), rewards = Map(D.Read(role, "act_rewards"), 6);
            f.Rows = acts; f.Rewards = rewards;
            f.Row = acts[(uint)TaskId]; f.Reward = rewards[Category];
            if (f.Row == null || f.Row.GetType() != Model.FirstType("Database.Activity") || Field<byte>(f.Row, "idactivity") != TaskId ||
                f.Reward == null || f.Reward.GetType() != Model.FirstType("Database.Activity_Reward") ||
                Field<byte>(f.Reward, "idact_reward") != Category) throw new ProbeFailure("mingshi_activity_identity");
            f.Template = D.Read(f.Row, "temp");
            object rewardTemplate = D.Read(f.Reward, "temp");
            if (f.Template == null || f.Template.GetType().FullName != "Database._activity" ||
                !ReferenceEquals(f.Template, D.Template("activity", TaskId)) || Field<byte>(f.Template, "id") != TaskId ||
                Field<byte>(f.Template, "idreward") != Category || !Field<bool>(f.Template, "active") ||
                rewardTemplate == null || rewardTemplate.GetType().FullName != "Database._activity_reward" ||
                !ReferenceEquals(rewardTemplate, D.Template("activity_reward", Category)) ||
                Field<byte>(rewardTemplate, "id") != Category || !Field<bool>(rewardTemplate, "active"))
                throw new ProbeFailure("mingshi_activity_template");
            IDictionary listed = Map(D.Read(f.Reward, "acts"), 255);
            f.Listed = listed;
            int required = Field<int>(f.Template, "cond_param");
            f.Current = Field<byte>(f.Row, "condition_param");
            f.Refresh = Field<int>(f.Row, "refresh_time");
            // The stock 355 callback puts the same new Activity in both maps.
            if (required != 1 || f.Current > required || !ReferenceEquals(listed[(uint)TaskId], f.Row))
                throw new ProbeFailure("mingshi_activity_cache_mismatch");
            f.Required = required;
        }

        private static void ReadQuote(Frame f, object role)
        {
            if (f.Harvests != 0) { f.PriceGate = "mingshi_first_harvest_used"; return; }
            f.VipLevel = (byte)D.Method(role.GetType(), "get_VIPLEVEL", Type.EmptyTypes, false, typeof(byte)).Invoke(role, null);
            f.Vip = D.Template("vip_template", f.VipLevel);
            if (f.Vip == null || f.Vip.GetType().FullName != "Database._vip_template" ||
                Field<byte>(f.Vip, "level") != f.VipLevel || !ReferenceEquals(D.Read(role, "my_vip_template"), f.Vip))
            { f.PriceGate = "mingshi_vip_template_unavailable"; return; }
            f.Entitlement = Field<byte>(f.Vip, "godharvest_times");
            if (f.Entitlement == 0) { f.PriceGate = "mingshi_no_harvest_entitlement"; return; }

            Type global = Model.FirstType("Global");
            MethodInfo templates = D.Method(global, "getTemplates", new[] { typeof(string) }, true, typeof(Hashtable));
            Hashtable prices = (Hashtable)templates.Invoke(null, new object[] { "buy_template" });
            f.Prices = prices;
            if (prices == null || prices.Count == 0 || prices.Count > 4096)
            { f.PriceGate = "mingshi_stone_quote_unavailable"; return; }
            Type rowType = null;
            object second = null;
            foreach (DictionaryEntry entry in prices)
            {
                object row = entry.Value;
                if (row == null || row.GetType().FullName != "Database._buy_template" ||
                    !(entry.Key is uint) || (uint)entry.Key != Field<uint>(row, "id") || rowType != null && row.GetType() != rowType)
                    throw new ProbeFailure("mingshi_stone_price_schema");
                rowType = row.GetType();
                if (Field<byte>(row, "type") != 7) continue;
                int min = Field<int>(row, "count_min"), max = Field<int>(row, "count_max"), gold = Field<int>(row, "gold");
                if (min <= 0 || max < min || gold < 0) throw new ProbeFailure("mingshi_stone_price_values");
                if (min <= 1 && max >= 1)
                {
                    if (f.Quote != null) throw new ProbeFailure("mingshi_stone_quote_ambiguous");
                    f.Quote = row; f.QuoteGold = gold;
                }
                if (min <= 2 && max >= 2)
                {
                    if (second != null) throw new ProbeFailure("mingshi_stone_quote_ambiguous");
                    second = row;
                }
            }
            Type kind = rowType.GetNestedType("TYPE", BindingFlags.Public);
            if (kind == null || !kind.IsEnum) throw new ProbeFailure("mingshi_stone_quote_enum");
            object stone = Enum.Parse(kind, "STONE", false);
            if (D.Int(stone) != 7) throw new ProbeFailure("mingshi_stone_quote_enum");
            MethodInfo quote = D.Method(global, "getBuyTemplate", new[] { kind, typeof(int) }, true, rowType);
            if (!ReferenceEquals(quote.Invoke(null, new[] { stone, (object)1 }), f.Quote))
                throw new ProbeFailure("mingshi_stone_quote_conflict");
            // The stock asset table starts at paid harvest 2. Cross-check
            // its stock lookup before interpreting no first row as free.
            if (f.Quote == null && (second == null || Field<int>(second, "gold") <= 0 ||
                !ReferenceEquals(quote.Invoke(null, new[] { stone, (object)2 }), second)))
                f.PriceGate = "mingshi_stone_quote_unverified";
            else if (f.QuoteGold != 0) f.PriceGate = "mingshi_paid_quote_forbidden";
        }
        private void ReadUi(Frame f)
        {
            D context = f.Context;
            Type rootType = Model.FirstType("RootMessenger");
            Type panelType = Model.GameType("DivinityUI");
            f.Loader = D.Static(Model.FirstType("BaseAsset"), "lds") as Component;
            GameObject rootHost = GameObject.Find("/Root/RootUI"), loaderHost = GameObject.Find("/LoadUI");
            if (f.Loader == null || f.Loader.GetType() != Model.FirstType("LoadScreen") || loaderHost == null || f.Loader.gameObject != loaderHost ||
                context.Root == null || context.Root.GetType() != rootType || context.Root.transform.root != context.Root.transform ||
                !ReferenceEquals(D.Read(f.Loader, "currmg"), context.Root) ||
                context.UiRoot == null || context.UiRoot.GetType() != rootType || !context.UiRoot.gameObject.activeInHierarchy ||
                context.GlobalUi == null || context.GlobalUi.GetType() != Model.GameType("RootUI") ||
                rootHost == null || context.GlobalUi.gameObject != rootHost || context.GlobalUi.transform.root != context.UiRoot.transform)
                throw new ProbeFailure("mingshi_loader_or_root_identity");
            object cached = D.Static(Model.GameType("BaseUI"), "rootUI");
            if (cached != null && !ReferenceEquals(cached, context.GlobalUi)) throw new ProbeFailure("mingshi_cached_root_identity");

            f.OldRoot = D.Read(f.Loader, "oldrmg") as Component; f.OldMain = D.Read(f.Loader, "oldmainrmg") as Component;
            f.LevelRoot = D.Read(context.GlobalUi, "level_rmg") as Component;
            GameObject host = GameObject.Find("/Divinity"), panelHost = GameObject.Find("/Divinity/DivinityUI");
            if (host != null && panelHost != null)
            {
                f.Scene = host.GetComponent("RootMessenger") as Component;
                f.Panel = panelHost.GetComponent("DivinityUI") as Component;
                if (f.Scene == null || f.Scene.GetType() != rootType || Field<string>(f.Scene, "LevelID") != "Divinity" ||
                    f.Scene.transform.root != f.Scene.transform || f.Panel == null || f.Panel.GetType() != panelType ||
                    f.Panel.transform.root != f.Scene.transform || !ReferenceEquals(D.Read(f.Panel, "rmg"), f.Scene) ||
                    D.Read(f.Panel, "OnClose") != null && ReferenceEquals(context.Root, f.Scene))
                    throw new ProbeFailure("mingshi_divinity_scene_identity");
            }
            f.Visible = D.Visible(f.Panel);
            if (((Behaviour)f.Loader).enabled || Field<bool>(f.Loader, "isBattling") ||
                Field<bool>(context.Root, "bLoading") || Field<int>(context.Root, "ExpectingCount") != 0 ||
                !D.Bool(D.Static(Model.FirstType("Global"), "extern_enabled")))
            { f.UiGate = "mingshi_stock_loading"; return; }
            if (!context.Root.gameObject.activeInHierarchy || context.SubUi != null ||
                D.Bool(D.Static(Model.GameType("BuildingBuildUI"), "m_bBuildVisible")) ||
                D.Bool(D.Static(Model.GameType("BuildingBehaviourScript"), "m_bEditVisible")) ||
                D.Bool(D.Static(Model.FirstType("PopupUI"), "popup")) ||
                (int)D.Static(Model.FirstType("__BaseUI"), "UICount_Visible") != 0)
            { f.UiGate = "mingshi_external_panel_active"; return; }

            string level = Field<string>(context.Root, "LevelID");
            bool mainLevel = level == "Campaign" || level == "Capital";
            f.Mesh = D.Static(panelType, "mesh") as GameObject;
            f.StoneMesh = D.Static(panelType, "stone_mesh") as GameObject;
            int globalSeen = 0, panelSeen = 0;
            foreach (UnityEngine.Object item in context.All)
            {
                Behaviour ui = item as Behaviour;
                if (ui == null) continue;
                if (ReferenceEquals(ui, context.GlobalUi)) { globalSeen++; continue; }
                if (ReferenceEquals(ui, f.Panel))
                {
                    panelSeen++;
                    if (mainLevel && D.Visible(ui)) { f.UiGate = "mingshi_foreign_divinity"; return; }
                    continue;
                }
                if (!D.Visible(ui)) continue;
                Type type = ui.GetType();
                if (type == Model.GameType("CampaignUI"))
                {
                    if (level != "Campaign" || !ReferenceEquals(D.Read(ui, "rmg"), context.Root) ||
                        ui.transform.root != context.Root.transform)
                    { f.UiGate = "mingshi_campaign_root_mismatch"; return; }
                    if (f.Campaign != null)
                    { f.UiGate = "mingshi_campaign_not_unique"; return; }
                    f.Campaign = ui; continue;
                }
                if (type == Model.GameType("MainUI") && f.Main == null &&
                    ReferenceEquals(D.Read(ui, "rmg"), context.Root) && ui.transform.root == context.Root.transform)
                { f.Main = ui; continue; }
                if (!mainLevel && ReferenceEquals(context.Root, f.Scene) && type == Model.GameType("DivinityTroopScript"))
                {
                    object troop = D.Read(ui, "Troop");
                    IList troops = D.Read(context.Character, "god_troops") as IList;
                    if (ReferenceEquals(D.Read(ui, "ParentUI"), f.Panel) && troop != null && troops != null &&
                        troops.Contains(troop) && ReferenceEquals(D.Read(troop, "mesh"), ui.gameObject) &&
                        ui.transform.root == f.Scene.transform) continue;
                    f.UiGate = "mingshi_divinity_troop_owner_mismatch"; return;
                }
                if (!mainLevel && ReferenceEquals(context.Root, f.Scene) && type == Model.GameType("DivinityStoneScript"))
                {
                    if (ReferenceEquals(ui.gameObject, f.StoneMesh) && ui.transform.root == f.Scene.transform) continue;
                    f.UiGate = "mingshi_divinity_stone_owner_mismatch"; return;
                }
                string block = context.Overlay(ui);
                if (block != null) { f.UiGate = "mingshi_overlay:" + block; return; }
            }
            if (globalSeen != 1 || panelSeen > 1) throw new ProbeFailure("mingshi_ui_not_unique");
            if (mainLevel)
            {
                if (level == "Campaign")
                {
                    if (!Usable(f.Campaign)) { f.UiGate = "mingshi_campaign_not_ready"; return; }
                    f.CampaignId = Field<int>(f.Campaign, "m_campaignID");
                    if (f.CampaignId <= 0 || f.CampaignId != Field<ushort>(context.Character, "idcampaign"))
                    { f.UiGate = "mingshi_campaign_identity"; return; }
                }
                f.MainReady = context.Gate == "PASS" && ReferenceEquals(f.LevelRoot, context.Root) &&
                    Usable(f.Main) && Field<bool>(f.Main, "m_bInit");
                f.EntryAllowed = f.MainReady && (bool)D.Method(context.Character.GetType(), "checkUI",
                    new[] { typeof(string) }, false, typeof(bool)).Invoke(context.Character, new object[] { "resource_collect" }) &&
                    StockDataReady(context.Character);
                return;
            }
            if (level != "Divinity" || panelSeen != 1 || !ReferenceEquals(context.Root, f.Scene) ||
                !ReferenceEquals(f.LevelRoot, f.Scene) || !Usable(f.Panel))
            { f.UiGate = "mingshi_divinity_not_ready"; return; }
            if (context.Gate != "development_campaign_or_capital_root_required" && context.Gate != "controller_gate:not_current_root" &&
                context.Gate != "controller_gate:main_not_unique" && context.Gate != "PASS")
            { f.UiGate = context.Gate; return; }

            f.Camera = Camera.main;
            if (f.Mesh == null || f.StoneMesh == null || f.Camera == null || !f.Mesh.activeInHierarchy || !f.StoneMesh.activeInHierarchy ||
                f.Mesh.transform.parent != f.Panel.transform || f.StoneMesh.transform.parent != f.Mesh.transform ||
                (f.Collider = f.StoneMesh.collider) == null)
            { f.UiGate = "mingshi_divinity_assets_unready"; return; }
            f.VisibleAt = Field<DateTime>(f.Panel, "visible_time");
            f.Ready = true;
        }

        private static bool StockDataReady(object role)
        {
            // DivinityUI.PrepareLoading reads exactly five troop animation
            // names. Web_OnGUI dereferences the current yield template.
            IList troops = D.Read(role, "god_troops") as IList;
            int yieldLevel = Field<byte>(role, "god_challenge_level") - 5;
            if (troops == null || troops.Count != 5 || yieldLevel < 0 || D.Template("god_yield", (uint)yieldLevel) == null)
                return false;
            Type type = Model.FirstType("Database.GodTroop");
            foreach (object troop in troops)
            {
                if (troop == null || troop.GetType() != type) return false;
                object animation = D.Template("troop_animation", Field<uint>(troop, "idmodel"));
                if (animation == null || String.IsNullOrEmpty(Field<string>(animation, "name"))) return false;
            }
            return true;
        }

        private static bool Usable(Component ui)
        { return D.Enabled(ui) && !D.Loading(ui) && !Field<bool>(ui, "first"); }

        private static void ReadNetwork(Frame f, object client)
        {
            if (!Monitor.TryEnter(client, 0)) return;
            try
            {
                IDictionary handlers = Handlers(client);
                f.ActivityHandler = handlers[ActivityOpcode];
                MethodInfo push = D.Method(Model.FirstType("RootBase"), "handelActvity", ResponseArgs, true, typeof(void));
                f.ActivityOriginal = push.IsPrivate ? Original(f.ActivityHandler, push, null, -1f) : null;
                f.HarvestHandler = handlers[DailyMingshiReceipt.Opcode];
                f.CampaignEnterHandler = handlers[CampaignEnterOpcode];
                IList messages = D.Read(client, "messages") as IList, sends = D.Read(client, "m_sendDatas") as IList;
                if (messages == null || sends == null || messages.Count > 64 || sends.Count > 64)
                    throw new ProbeFailure("mingshi_network_bound");
                foreach (object message in messages) if (Field<ushort>(message, "op") == DailyMingshiReceipt.Opcode) f.QueueHarvest++;
                if (messages.Count != 0 || sends.Count != 0 || DevelopmentCallbackCapture.AnyCallbackExecuting ||
                    f.HarvestHandler != null || f.CampaignEnterHandler != null) return;
                foreach (DictionaryEntry pair in handlers)
                {
                    if (!(pair.Key is ushort) || pair.Value == null || pair.Value.GetType() != Model.FirstType("game.net.tcp_base+Handler"))
                        throw new ProbeFailure("mingshi_network_handler_schema");
                    float life = Field<float>(pair.Value, "life");
                    if (Single.IsNaN(life) || Single.IsInfinity(life)) throw new ProbeFailure("mingshi_network_handler_life");
                    if (life > 0f) return;
                }
                f.Idle = true;
            }
            finally { Monitor.Exit(client); }
        }

        private static Delegate Original(object handler, MethodInfo method, object target, float life, object argument = null)
        {
            if (handler == null || handler.GetType() != Model.FirstType("game.net.tcp_base+Handler") ||
                !ReferenceEquals(D.Read(handler, "arg"), argument) || Field<float>(handler, "life") != life) return null;
            Delegate original = D.Read(handler, "handler") as Delegate;
            return original != null && original.GetInvocationList().Length == 1 && original.Method == method &&
                ReferenceEquals(original.Target, target) ? original : null;
        }

        private static IDictionary Handlers(object client) { return Map(D.Read(client, "m_handlers"), 512); }
        private static IDictionary Map(object value, int bound)
        {
            IDictionary map = value as IDictionary;
            if (map == null || map.Count > bound) throw new ProbeFailure("mingshi_cache_bound");
            return map;
        }

        private static T Field<T>(object target, string name)
        {
            object value = D.Read(target, name);
            if (!(value is T)) throw new ProbeFailure("mingshi_field_schema:" + name);
            return (T)value;
        }

        private bool AdmissionExpired(Intent intent)
        { return Elapsed(intent.QueuedAt) || (intent.Action != "open" && intent.Action != "cleanup" && Expired()); }
        private bool Expired() { return Elapsed(started); }
        private static bool Elapsed(int since) { return unchecked((uint)(Environment.TickCount - since)) >= WaitMs; }

        private void Detach()
        {
            Detach(ref response);
            Detach(ref activity);
        }

        private static void Detach(ref DevelopmentCallbackCapture capture)
        {
            if (capture == null) return;
            string result = capture.DisarmAndRestore();
            if (!result.EndsWith("forward_only", StringComparison.Ordinal)) capture = null;
        }

        private void End(string next, string why)
        {
            status = next; reason = why;
            Detach();
        }

        private void EndFailure(string why)
        { End(attempted && !observed ? "UNKNOWN" : observed ? "SUCCEEDED" : "REJECTED", why); }

        private bool Reject(string error, out string why)
        {
            why = error; queued = null;
            EndFailure(error);
            UpdateRevision(frame);
            return false;
        }

        internal void NotInvoked(string why)
        {
            if (queued == null) return;
            queued = null;
            EndFailure(why);
            UpdateRevision(frame);
        }

        internal void Stop(string why)
        {
            bool wasActive = queued != null || WindowActive();
            stopped = true; queued = null;
            if (wasActive) EndFailure(why);
            else Detach();
            UpdateRevision(frame);
        }
        private string NextAction(out string gate)
        {
            gate = "";
            if (queued != null) return "";
            string action = status == "OPEN" ? "harvest" : status == "SETTLED" ? "close" :
                !attempted && !closed && (origin != null || status == "IDLE" && frame.Scene != null &&
                    ReferenceEquals(frame.Context.Root, frame.Scene)) ? "cleanup" : "open";
            gate = Gate(frame, action);
            return gate == "" ? action : "";
        }

        private void UpdateRevision(Frame previous)
        {
            string next = ViewJson() + OperationJson();
            if (next == stamp && (ReferenceEquals(frame, previous) || frame.SameAdmission(previous) ||
                frame.Error != "" && previous.Error == frame.Error)) return;
            if (revision == 999999999) throw new ProbeFailure("mingshi_revision_exhausted");
            stamp = next; revision++;
        }

        internal string ViewJson()
        {
            string gate;
            string action = NextAction(out gate);
            gate = frame.Error != "" ? frame.Error : action != "" ? "" : gate != "" ? gate :
                frame.UiGate != "" ? frame.UiGate : reason;
            if (status == "IDLE" && action == "" && frame.Scene != null && ReferenceEquals(frame.Context.Root, frame.Scene))
                gate = "mingshi_existing_page_return_unavailable:" + (gate == "" ? Gate(frame, "cleanup") : gate);
            return "{\"task_id\":" + TaskId + ",\"role_id\":" + D.Q(frame.Role.ToString(CultureInfo.InvariantCulture)) +
                ",\"cycle\":" + frame.Cycle + ",\"current\":" + frame.Current + ",\"required\":" + frame.Required +
                ",\"gold_cost\":" + (action == "harvest" ? "0" : "null") + ",\"next_action\":" + D.Q(action) +
                ",\"reason\":" + D.Q(gate) + "}";
        }

        internal string OperationJson()
        {
            return "{\"status\":" + D.Q(queued == null ? status : "QUEUED") + ",\"reason\":" + D.Q(reason) +
                ",\"operation_id\":" + D.Q(operation == null ? "" : operation.Id) + ",\"sequence\":" + lastSequence +
                ",\"mutation_attempted\":" + D.B(attempted) + ",\"stock_callback_returned\":" + D.B(returned) +
                ",\"account_progress_observed\":" + D.B(observed) + ",\"panel_closed\":" + D.B(closed) + "}";
        }
    }
}
