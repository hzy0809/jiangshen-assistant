using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using UnityEngine;
using D = JiangShen.AssistantRuntime.SharedDevelopmentContext;

namespace JiangShen.AssistantRuntime
{
    // One current, due online-award tier. This route never enters PremiumUI,
    // supplies a payment parameter, waits for a later tier, or replays a send.
    internal sealed class DailyOnlineGiftModel
    {
        internal const int Task = 309;
        private const ushort Opcode = 791;
        private const int WaitMs = 20000;

        private sealed class Frame
        {
            internal D Context;
            internal DailyTransport Transport;
            internal object Character, Client, Online, Template, Icon, Sub, RewardBonuses;
            internal Component Main, Gift, Reward, Demon;
            internal Delegate IconHandler, IconChecker, RewardClose;
            internal ulong Role;
            internal uint Now, Day, Cycle;
            internal int Got, NextGet, TierMinutes;
            internal string Scene = "", Error = "", TemplateStamp = "";
            internal bool Loading, NetworkBusy, Home, CleanMain, IconReady;
            internal bool Due { get { return Template != null && (long)Now >= NextGet; } }
            internal bool SameConnection(Frame before)
            {
                return before != null && Role != 0 && Role == before.Role && Day == before.Day && Cycle == before.Cycle && Now >= before.Now &&
                    ReferenceEquals(Character, before.Character) && ReferenceEquals(Client, before.Client) &&
                    ReferenceEquals(Online, before.Online) && Transport != null && Transport.Same(before.Transport);
            }
            internal bool SameOwner(Frame before)
            {
                return SameConnection(before) && Context.SameOwnership(before.Context) && ReferenceEquals(Main, before.Main) &&
                    ReferenceEquals(Gift, before.Gift) && ReferenceEquals(Icon, before.Icon) &&
                    ReferenceEquals(IconHandler, before.IconHandler) && ReferenceEquals(IconChecker, before.IconChecker);
            }
            internal bool SameTier(Frame before)
            {
                return before != null && Got == before.Got && NextGet == before.NextGet &&
                    ReferenceEquals(Template, before.Template) && TemplateStamp == before.TemplateStamp;
            }
            internal bool SameQuote(Frame before)
            {
                return SameOwner(before) && SameTier(before) && Due == before.Due && Scene == before.Scene && Home == before.Home &&
                    Loading == before.Loading && NetworkBusy == before.NetworkBusy && IconReady == before.IconReady &&
                    ReferenceEquals(Sub, before.Sub) && ReferenceEquals(Reward, before.Reward) &&
                    ReferenceEquals(RewardBonuses, before.RewardBonuses) && ReferenceEquals(RewardClose, before.RewardClose);
            }
        }
        private sealed class Intent
        {
            internal string Action, Id;
            internal Frame Quote;
            internal int Tick, Sequence, Revision;
        }
        private sealed class Receipt
        {
            internal Frame Sent;
            internal string Operation;
            internal int Sequence, Revision, GotAfter, NextAfter;
            internal uint Operate;
            internal int[] Types = new int[0];
            internal uint[] Amounts = new uint[0];
            internal bool HasOperate, Matched, Returned, ProgressConfirmed, NextTimeConfirmed, RewardOwned, RewardClosed, CloseInvoked, CloseReturned;
            internal bool UnavailableOwned, UnavailableCloseInvoked, VisibleBound;
            internal bool Submitted, PageConfirmed, NullTimeout, NullCleanupInvoked, NullCleanupReturned;
            internal Component Reward;
            internal Delegate Close, Stock, Wrapper;
            internal object Bonuses, Handler, Argument;
            internal DateTime VisibleTime;
        }

        private Model model;
        private Frame frame, origin, initial;
        private Intent queued, notInvoked;
        private string notInvokedReason = "";
        private Receipt receipt;
        private DailyDivinityReplyTap tap;
        private int sequence, tick;
        private long observation;
        private string operationId = "", lastAction = "", nextAction = "", status = "IDLE", reason = "not_observed", stamp;
        private bool blocked, stopped, holding, begun, attempted, closed, unsafeState;
        internal int Revision { get; private set; }
        internal bool Busy { get { return holding || queued != null || tap != null; } }
        internal bool MutationAttempted { get { return attempted; } }
        internal bool GuiInvocationAttempted { get; private set; }

        internal void Capture(Model current, int task, bool admissionBlocked)
        {
            if (task != Task) throw new ArgumentOutOfRangeException("task");
            model = current; blocked = admissionBlocked; nextAction = ""; observation++;
            try
            {
                frame = Read();
                if (frame.Error != "") { if (holding || attempted) Unknown(frame.Error); else Set("BLOCKED", frame.Error); return; }
                if (holding && !(begun ? frame.SameOwner(origin) : frame.SameConnection(origin)))
                { Unknown("online_gift_owner_connection_or_game_day_changed"); return; }
                if (receipt != null && receipt.Submitted && frame.SameOwner(receipt.Sent))
                {
                    receipt.GotAfter = frame.Got; receipt.NextAfter = frame.NextGet;
                    if (frame.Got > receipt.Sent.Got)
                    {
                        receipt.PageConfirmed = receipt.ProgressConfirmed = true;
                        receipt.NextTimeConfirmed = frame.NextGet != receipt.Sent.NextGet || frame.Template == null;
                    }
                }
                if (receipt != null && receipt.Submitted && !receipt.HasOperate &&
                    (receipt.NullTimeout || tap != null && tap.Error == "divinity_stock_callback_null"))
                {
                    receipt.NullTimeout = true;
                    if (!receipt.NullCleanupReturned)
                    {
                        if (!stopped && !blocked && NoReplyCloseOwned(frame, receipt)) Offer("cleanup_no_reply");
                        else WaitCleanup("online_gift_no_reply_close_unavailable");
                        return;
                    }
                    if (!ReleaseTap(receipt)) { Set("PENDING", "online_gift_callback_release_pending"); return; }
                    if (!frame.Home || frame.NetworkBusy || RewardPending(frame.Reward) || D.Visible(frame.Gift) || D.Loading(frame.Gift))
                    { WaitCleanup("online_gift_no_reply_close_unverified"); return; }
                    Finish(receipt.PageConfirmed ? "COMPLETED" : "PROCESSED",
                        receipt.PageConfirmed ? "online_gift_page_claim_verified" : "online_gift_claim_submitted_no_reply");
                    return;
                }
                if (tap != null)
                {
                    if (tap.Error != null) { Unknown(tap.Error); return; }
                    if (!tap.Returned)
                    {
                        if (!tap.Attached || Elapsed() > WaitMs) Unknown("online_gift_receipt_unverified_no_replay");
                        else Set(stopped ? "STOPPED" : "PENDING", stopped ? "user_stop" : "online_gift_reply_pending");
                        return;
                    }
                    if (!ReleaseTap(receipt)) { Set("PENDING", "online_gift_callback_release_pending"); return; }
                }
                if (unsafeState) { Set("UNKNOWN", reason); return; }
                if (stopped) { Set("STOPPED", "user_stop"); return; }
                if (!holding)
                {
                    if (blocked || frame.Loading || frame.NetworkBusy || frame.Sub != null || !D.Enabled(frame.Main))
                    { Set("BLOCKED", "online_gift_clean_main_required"); return; }
                    string gate = frame.Context.Panels(frame.Demon);
                    if (gate != "PASS") { Set("BLOCKED", gate); return; }
                    if (closed)
                    {
                        // A new explicit operation may quote the current tier;
                        // a pending/ambiguous operation never reaches here.
                        nextAction = frame.Scene == "Capital" ? "begin" : "open"; Set(status, reason); return;
                    }
                    Offer(frame.Scene == "Capital" ? "begin" : "open"); return;
                }
                if (!begun)
                {
                    if (frame.Home) Offer("begin");
                    else if (Elapsed() > WaitMs) Unknown("online_gift_capital_open_unverified");
                    else Set("PENDING", "online_gift_opening_capital");
                    return;
                }
                if (receipt == null)
                {
                    if (!frame.Home || frame.NetworkBusy || !frame.SameTier(initial))
                    { ReleaseUnsent("online_gift_current_tier_or_home_changed"); return; }
                    if (frame.Template == null) { Finish("NO_FREE_REWARD", "online_gift_no_next_tier"); return; }
                    if (!frame.Due) { Finish("NO_FREE_REWARD", "online_gift_current_tier_not_due"); return; }
                    if (!frame.IconReady) { Finish("NO_FREE_REWARD", "online_gift_source_icon_unavailable"); return; }
                    Offer("claim"); return;
                }
                if (!receipt.Matched || !receipt.Returned || !receipt.HasOperate)
                { Unknown("online_gift_receipt_unverified_no_replay"); return; }
                if (receipt.Operate == 0)
                {
                    if (!receipt.ProgressConfirmed || !receipt.NextTimeConfirmed || frame.Got != initial.Got + 1)
                    { Unknown("online_gift_exact_got_increment_unverified"); return; }
                    if (receipt.CloseInvoked)
                    {
                        if (!receipt.CloseReturned || !ClosedRewardOwner(frame, receipt))
                        { Unknown("online_gift_reward_close_owner_changed"); return; }
                        if (!frame.Home || frame.NetworkBusy || RewardPending(frame.Reward) || D.Visible(frame.Gift) || D.Loading(frame.Gift))
                        { WaitCleanup("online_gift_reward_cleanup_unverified"); return; }
                        receipt.RewardClosed = true; Finish("COMPLETED", "online_gift_one_current_tier_claimed"); return;
                    }
                    if (!OwnReward(frame, receipt)) { Unknown("online_gift_reward_owner_changed"); return; }
                    if (!RewardReady(frame.Reward) || frame.Loading)
                    { WaitCleanup("online_gift_reward_loading_unverified"); return; }
                    string gate = frame.Context.RewardPanels(frame.Demon, frame.Reward);
                    if (gate != "PASS") { Unknown(gate); return; }
                    if (!receipt.VisibleBound)
                    { receipt.VisibleTime = (DateTime)D.Read(frame.Reward, "visible_time"); receipt.VisibleBound = true; }
                    Offer("close_reward"); return;
                }
                if (receipt.Operate != 1 || frame.Got != initial.Got || frame.NextGet != initial.NextGet)
                { Unknown("online_gift_rejection_progress_changed"); return; }
                if (receipt.UnavailableCloseInvoked)
                {
                    if (!frame.Home || frame.NetworkBusy || D.Visible(frame.Gift) || D.Loading(frame.Gift))
                    { WaitCleanup("online_gift_unavailable_cleanup_unverified"); return; }
                    Finish("NO_FREE_REWARD", "online_gift_server_current_tier_unavailable"); return;
                }
                if (!OwnUnavailable(frame, receipt)) { Unknown("online_gift_unavailable_panel_not_owned"); return; }
                if (frame.Loading || !D.Enabled(frame.Gift) || D.Loading(frame.Gift))
                { WaitCleanup("online_gift_unavailable_panel_loading"); return; }
                string unavailableGate = frame.Context.Panels(frame.Demon, frame.Gift);
                if (unavailableGate != "PASS") { Unknown(unavailableGate); return; }
                Offer("close_unavailable");
            }
            catch (Exception error) { if (holding || attempted) Unknown(error.Message); else Set("BLOCKED", error.Message); }
        }

        internal bool Queue(int task, ulong character, string id, int seq, int revision, string action, out string why)
        {
            why = "online_gift_command_unavailable";
            bool start = !holding && (action == "open" || action == "begin");
            if (task != Task || stopped || blocked || unsafeState || frame == null || frame.Error != "" ||
                character == 0 || character != frame.Role || !Command.ValidNonce(id) || seq <= sequence || queued != null ||
                (start ? Busy || id == operationId : !holding || id != operationId)) return false;
            if (revision != Revision) { why = "online_gift_queue_quote_changed"; return false; }
            if (action == "" || action != nextAction) { why = "online_gift_queue_offer_changed"; return false; }
            queued = new Intent { Action = action, Id = id, Quote = frame, Tick = Environment.TickCount, Sequence = seq, Revision = revision };
            notInvoked = null; notInvokedReason = "";
            sequence = seq; lastAction = action; GuiInvocationAttempted = false; Revision++; why = ""; return true;
        }

        internal bool ExecuteGui(DevelopmentPublication publish, DailyMonsterInvocationGate admission, out string why)
        {
            why = "online_gift_not_queued"; Intent intent = queued;
            if (intent == null) return false;
            object client = intent.Quote.Client;
            if (client == null || !Monitor.TryEnter(client, 0)) { NotInvoked("online_gift_client_busy"); why = "online_gift_client_busy"; return false; }
            try
            {
                publish(); Capture(model, Task, blocked);
                if (stopped || blocked || !D.Gui || frame.Error != "" || !frame.SameQuote(intent.Quote) || intent.Action != nextAction ||
                    unchecked((uint)(Environment.TickCount - intent.Tick)) > 1500 || !admission())
                { why = "online_gift_gui_admission_changed"; NotInvoked(why); return false; }
                queued = null; tick = Environment.TickCount;
                if (intent.Action == "open" || intent.Action == "begin")
                {
                    if (!holding)
                    {
                        origin = frame; operationId = intent.Id; holding = true;
                        initial = null; receipt = null; begun = attempted = closed = false;
                    }
                    if (intent.Action == "open")
                    {
                        if (begun || frame.Sub != null || frame.NetworkBusy || frame.Loading || frame.Context.Panels(frame.Demon) != "PASS")
                            throw new ProbeFailure("online_gift_open_not_admitted");
                        GuiInvocationAttempted = true; D.MainBranch("_capital");
                    }
                    else
                    {
                        if (begun || !frame.Home || frame.NetworkBusy) throw new ProbeFailure("online_gift_begin_not_home");
                        origin = initial = frame; begun = true;
                        if (frame.Template == null) { Finish("NO_FREE_REWARD", "online_gift_no_next_tier"); why = ""; return true; }
                        if (!frame.Due) { Finish("NO_FREE_REWARD", "online_gift_current_tier_not_due"); why = ""; return true; }
                        if (!frame.IconReady) { Finish("NO_FREE_REWARD", "online_gift_source_icon_unavailable"); why = ""; return true; }
                    }
                }
                else if (intent.Action == "claim") Claim(intent);
                else if (intent.Action == "close_reward")
                {
                    if (receipt == null || receipt.CloseInvoked || !receipt.VisibleBound || !OwnReward(frame, receipt) || !RewardReady(frame.Reward) ||
                        frame.Context.RewardPanels(frame.Demon, frame.Reward) != "PASS")
                        throw new ProbeFailure("online_gift_reward_close_not_owned");
                    // Same sequence as the stock empty-mine_ui confirmation.
                    receipt.CloseInvoked = true; GuiInvocationAttempted = true;
                    D.Call(receipt.Reward, "Hide"); receipt.Close.DynamicInvoke(null); receipt.CloseReturned = true;
                }
                else if (intent.Action == "close_unavailable")
                {
                    if (receipt == null || receipt.UnavailableCloseInvoked || !OwnUnavailable(frame, receipt) ||
                        !D.Enabled(frame.Gift) || D.Loading(frame.Gift) || frame.Context.Panels(frame.Demon, frame.Gift) != "PASS")
                        throw new ProbeFailure("online_gift_unavailable_close_not_owned");
                    receipt.UnavailableCloseInvoked = true; GuiInvocationAttempted = true;
                    // OnlineAwardsUI.EndGUI closes through this stock branch.
                    D.Method(Model.GameType("MainUI"), "SetSubUI", new[] { Model.FirstType("__BaseUI") }, true, typeof(void)).Invoke(null, new object[] { null });
                }
                else if (intent.Action == "cleanup_no_reply")
                {
                    if (!NoReplyCloseOwned(frame, receipt) || receipt.NullCleanupInvoked)
                        throw new ProbeFailure("online_gift_no_reply_close_not_owned");
                    receipt.NullCleanupInvoked = true; GuiInvocationAttempted = true;
                    // The guarded null timeout did not forward stock. Replay
                    // only that null lifecycle once; it does not claim again.
                    receipt.Stock.DynamicInvoke(new object[] { null, receipt.Argument });
                    if (ReferenceEquals(D.Static(Model.GameType("MainUI"), "m_subui"), receipt.Sent.Gift))
                        D.Method(Model.GameType("MainUI"), "SetSubUI", new[] { Model.FirstType("__BaseUI") }, true, typeof(void)).Invoke(null, new object[] { null });
                    receipt.NullCleanupReturned = true;
                }
                else throw new ProbeFailure("online_gift_action_out_of_scope");
                nextAction = ""; Set("PENDING", "online_gift_stock_action_invoked"); why = ""; return true;
            }
            catch (Exception error) { queued = null; Unknown(error.Message); why = reason; return false; }
            finally { Monitor.Exit(client); }
        }

        private void Claim(Intent intent)
        {
            if (!begun || attempted || receipt != null || !frame.Home || frame.NetworkBusy || !frame.SameTier(initial) || !frame.Due || !frame.IconReady)
                throw new ProbeFailure("online_gift_claim_not_admitted_no_replay");
            if (Handler(frame.Client) != null) throw new ProbeFailure("online_gift_opcode_already_owned");
            receipt = new Receipt { Sent = frame, Operation = operationId, Sequence = intent.Sequence, Revision = intent.Revision,
                GotAfter = frame.Got, NextAfter = frame.NextGet };
            // Set before invoking a stock method which may have sent even when
            // an exception or a missing registration prevents acknowledgment.
            attempted = true; GuiInvocationAttempted = true;
            frame.IconHandler.DynamicInvoke(null);
            receipt.Submitted = true;
            object handler = Handler(frame.Client);
            Delegate stock = handler == null ? null : D.Read(handler, "handler") as Delegate;
            if (stock == null || stock.GetInvocationList().Length != 1 || !ReferenceEquals(stock.Target, frame.Gift) ||
                stock.Method != D.Method(frame.Gift.GetType(), "sc_online_get", new[] { typeof(MemoryStream), typeof(object) }, false, typeof(void)))
                throw new ProbeFailure("online_gift_stock_registration_unverified_no_replay");
            Receipt request = receipt;
            request.Stock = stock; request.Handler = handler; request.Argument = D.Read(handler, "arg");
            tap = DailyDivinityReplyTap.Attach(frame.Client, false, handler, handler.GetType().GetField("handler", D.Instance), D.Read(handler, "arg"),
                delegate { return Handler(request.Sent.Client); }, delegate { return CallbackOwner(request); },
                delegate(byte[] body) { Returned(request); }, delegate(byte[] body) { Preflight(request, body); });
            if (tap == null || !tap.Attached) throw new ProbeFailure("online_gift_reply_guard_unavailable_no_replay");
            request.Wrapper = D.Read(handler, "handler") as Delegate;
        }

        private bool NoReplyCloseOwned(Frame f, Receipt request)
        {
            if (request == null || !request.Submitted || !request.NullTimeout || request.HasOperate ||
                request.Stock == null || request.NullCleanupInvoked || !f.SameOwner(request.Sent) ||
                !request.Sent.Transport.StillCurrent() || Handler(f.Client) != null || RewardPending(f.Reward) ||
                f.Sub != null && !ReferenceEquals(f.Sub, request.Sent.Gift) || f.Loading)
                return false;
            return f.Context.Panels(f.Demon, request.Sent.Gift) == "PASS";
        }

        private bool ReleaseTap(Receipt request)
        {
            if (tap == null) return true;
            if (request == null || request.Handler == null || request.Stock == null || request.Wrapper == null ||
                !Monitor.TryEnter(request.Sent.Client, 0)) return false;
            try
            {
                tap.StopObserving(); if (!tap.TryRestore()) return false;
                if (ReferenceEquals(Handler(request.Sent.Client), request.Handler) &&
                    ReferenceEquals(D.Read(request.Handler, "handler"), request.Wrapper))
                {
                    request.Handler.GetType().GetField("handler", D.Instance).SetValue(request.Handler, request.Stock);
                    if (!ReferenceEquals(D.Read(request.Handler, "handler"), request.Stock)) return false;
                }
                tap = null; return true;
            }
            finally { Monitor.Exit(request.Sent.Client); }
        }

        private bool CallbackOwner(Receipt request)
        {
            if (model == null || request == null || !ReferenceEquals(request, receipt) || request.Operation != operationId ||
                request.Sequence <= 0 || !request.Sent.Transport.StillCurrent() ||
                !ReferenceEquals(D.Static(Model.FirstType("Global"), "character_"), request.Sent.Character) ||
                !ReferenceEquals(DailyTransport.CurrentClient(request.Sent.Character), request.Sent.Client) ||
                !ReferenceEquals(model.CurrentRoot(), request.Sent.Context.Root) || request.Sent.Context.OwnershipGate() != "PASS" ||
                !ReferenceEquals(D.Read(request.Sent.Character, "OnlineGift"), request.Sent.Online) ||
                D.Int(D.Read(request.Sent.Online, "got")) != request.Sent.Got || D.Int(D.Read(request.Sent.Online, "next_get_time")) != request.Sent.NextGet ||
                !ReferenceEquals(D.Static(Model.GameType("MainUI"), "m_subui"), request.Sent.Gift)) return false;
            uint now, day, cycle;
            if (DailyRefreshGate.ReadClock(request.Sent.Character, out now, out day, out cycle) != "" ||
                now < request.Sent.Now || day != request.Sent.Day || cycle != request.Sent.Cycle) return false;
            object template = D.Template("online_awards_template", (uint)(request.Sent.Got + 1));
            return ReferenceEquals(template, request.Sent.Template) && TierStamp(template) == request.Sent.TemplateStamp &&
                ReferenceEquals(D.Read(request.Sent.Icon, "handler"), request.Sent.IconHandler) &&
                ReferenceEquals(D.Read(request.Sent.Icon, "checker"), request.Sent.IconChecker) &&
                !RewardPending(request.Sent.Context.GrowthRewardDialog());
        }

        private void Preflight(Receipt request, byte[] body)
        {
            // Detached schema decoding requires the required operate field,
            // known enum, and valid repeated values before forwarding stock.
            object packet = BattleReplyPreflight.Deserialize(body, "game.net.data.StoC.SCOnlineGet_0x317");
            request.Operate = Convert.ToUInt32(Value(packet, "operate"), CultureInfo.InvariantCulture); request.HasOperate = true;
            if (request.Operate > 1) throw new ProbeFailure("online_gift_unknown_operate");
            IList types = Value(packet, "type_id") as IList, amounts = Value(packet, "amount") as IList;
            if (types == null || amounts == null || types.Count != amounts.Count) throw new ProbeFailure("online_gift_reward_vector_schema");
            request.Types = new int[types.Count]; request.Amounts = new uint[amounts.Count];
            for (int i = 0; i < types.Count; i++)
            {
                request.Types[i] = D.Int(types[i]); request.Amounts[i] = Convert.ToUInt32(amounts[i], CultureInfo.InvariantCulture);
                if (request.Types[i] < -1 || request.Types[i] > Byte.MaxValue || request.Types[i] != -1 && request.Amounts[i] > Int32.MaxValue)
                    throw new ProbeFailure("online_gift_stock_reward_value_unrepresentable");
            }
            request.Matched = true;
        }

        private void Returned(Receipt request)
        {
            request.Returned = true;
            Frame after = Read();
            if (after.Error != "" || !after.SameOwner(request.Sent)) throw new ProbeFailure("online_gift_stock_callback_owner_changed");
            request.GotAfter = after.Got; request.NextAfter = after.NextGet;
            if (request.Operate == 1)
            {
                if (after.Got != request.Sent.Got || after.NextGet != request.Sent.NextGet || !ReferenceEquals(after.Sub, after.Gift) || RewardPending(after.Reward))
                    throw new ProbeFailure("online_gift_rejection_or_panel_unverified");
                request.NextTimeConfirmed = true; request.UnavailableOwned = true; return;
            }
            if (request.Operate != 0 || after.Got != request.Sent.Got + 1 || after.Sub != null)
                throw new ProbeFailure("online_gift_exact_got_increment_unverified");
            long expectedNext = after.Template == null ? request.Sent.NextGet : (long)after.Now + (long)after.TierMinutes * 60;
            if (expectedNext < 0 || expectedNext > Int32.MaxValue || after.NextGet != expectedNext)
                throw new ProbeFailure("online_gift_stock_next_time_unverified");
            request.ProgressConfirmed = true; request.NextTimeConfirmed = true; request.Reward = after.Reward;
            request.Close = after.RewardClose; request.Bonuses = after.RewardBonuses;
            if (request.Bonuses == null || ReferenceEquals(request.Bonuses, request.Sent.RewardBonuses) || !OwnReward(after, request))
                throw new ProbeFailure("online_gift_stock_reward_unowned");
            IList bonuses = request.Bonuses as IList;
            int count = 0; foreach (int type in request.Types) if (type != -1) count++;
            if (bonuses == null || bonuses.Count != count) throw new ProbeFailure("online_gift_stock_reward_count_changed");
            request.RewardOwned = true;
        }

        private bool RewardIdentity(Frame f, Receipt request)
        {
            Delegate close = f.RewardClose;
            return request != null && request.Operate == 0 && request.Matched && request.Returned && request.ProgressConfirmed &&
                f.SameOwner(request.Sent) && f.Got == request.Sent.Got + 1 && f.Reward != null && ReferenceEquals(f.Reward, request.Reward) &&
                ReferenceEquals(f.Reward, D.Static(Model.FirstType("__BaseUI"), "battle_result_ui")) &&
                close != null && close.GetInvocationList().Length == 1 && ReferenceEquals(close, request.Close) &&
                ReferenceEquals(close.Target, request.Sent.Gift) && close.Method == D.Method(request.Sent.Gift.GetType(), "EnableAll", Type.EmptyTypes, false, typeof(void)) &&
                ReferenceEquals(f.RewardBonuses, request.Bonuses) && (string)D.Read(f.Reward, "mine_ui") == "" &&
                ReferenceEquals(D.Read(f.Reward, "m_prevUI"), D.Read(request.Sent.Gift, "rmg")) && !D.Bool(D.Read(f.Reward, "autoClose"));
        }
        private bool OwnReward(Frame f, Receipt request)
        {
            return RewardIdentity(f, request) && f.Sub == null && !D.Visible(f.Gift) && !D.Loading(f.Gift) &&
                (!request.VisibleBound || (DateTime)D.Read(f.Reward, "visible_time") == request.VisibleTime) && RewardPending(f.Reward);
        }
        private bool ClosedRewardOwner(Frame f, Receipt request)
        { return RewardIdentity(f, request) && (DateTime)D.Read(f.Reward, "visible_time") == request.VisibleTime; }
        private bool OwnUnavailable(Frame f, Receipt request)
        {
            return request != null && request.Operate == 1 && request.Matched && request.Returned && request.UnavailableOwned &&
                f.SameOwner(request.Sent) && f.SameTier(request.Sent) && ReferenceEquals(f.Sub, request.Sent.Gift) &&
                !RewardPending(f.Reward) && ReferenceEquals(f.Reward, request.Sent.Reward) &&
                ReferenceEquals(f.RewardClose, request.Sent.RewardClose) && ReferenceEquals(f.RewardBonuses, request.Sent.RewardBonuses);
        }

        private Frame Read()
        {
            Frame f = new Frame();
            try
            {
                f.Context = DevelopmentContextReader.DailyWood(model, false); f.Character = f.Context.Character;
                f.Client = DailyTransport.CurrentClient(f.Character); f.Transport = DailyTransport.Read(f.Client);
                if (f.Transport == null || f.Context.OwnershipGate() != "PASS") throw new ProbeFailure("online_gift_character_connection_unavailable");
                f.Role = Convert.ToUInt64(D.Read(f.Character, "id"), CultureInfo.InvariantCulture);
                string clock = DailyRefreshGate.ReadClock(f.Character, out f.Now, out f.Day, out f.Cycle);
                if (clock != "") throw new ProbeFailure(clock);
                f.Scene = (string)D.Read(f.Context.Root, "LevelID");
                f.Main = f.Context.Root.GetComponentInChildren(Model.GameType("MainUI"));
                if (f.Main == null || !ReferenceEquals(D.Read(f.Main, "rmg"), f.Context.Root)) throw new ProbeFailure("online_gift_main_owner_unavailable");
                f.Sub = f.Context.SubUi;
                f.Reward = f.Context.GrowthRewardDialog(); f.Demon = f.Context.FindCapitalDemon();
                f.RewardBonuses = f.Reward == null ? null : D.Read(f.Reward, "m_vctBonuses");
                f.RewardClose = f.Reward == null ? null : D.Read(f.Reward, "OnClose") as Delegate;
                f.Online = D.Read(f.Character, "OnlineGift"); f.Got = D.Int(D.Read(f.Online, "got")); f.NextGet = D.Int(D.Read(f.Online, "next_get_time"));
                if (f.Got < 0 || f.Got == Int32.MaxValue || f.NextGet < 0) throw new ProbeFailure("online_gift_tier_state_schema");
                f.Template = D.Template("online_awards_template", (uint)(f.Got + 1));
                if (f.Template != null)
                {
                    if (D.UInt(Value(f.Template, "id")) != (uint)(f.Got + 1)) throw new ProbeFailure("online_gift_template_id_changed");
                    f.TierMinutes = D.Int(Value(f.Template, "time")); f.TemplateStamp = TierStamp(f.Template);
                    if (f.TierMinutes < 0) throw new ProbeFailure("online_gift_template_time_schema");
                }
                // Exhausted/not-due tiers need no entry component or icon.
                // After sending, retain the original source identity even if
                // the stock checker hides the exhausted icon.
                if (f.Due || attempted)
                {
                    int gifts = 0, icons = 0;
                    foreach (UnityEngine.Object item in f.Context.All)
                    {
                        Component candidate = item as Component;
                        if (candidate != null && candidate.GetType() == Model.GameType("OnlineAwardsUI") &&
                            ReferenceEquals(D.Read(candidate, "rmg"), f.Context.UiRoot) && candidate.gameObject == f.Context.GlobalUi.gameObject &&
                            candidate.transform.root == f.Context.UiRoot.transform) { f.Gift = candidate; gifts++; }
                    }
                    if (gifts != 1) f.Gift = null;
                    foreach (object candidate in (IList)D.Read(f.Main, "btnTop"))
                    {
                        if (candidate == null || candidate.GetType().FullName != "_ButtonMain" || (string)D.Read(candidate, "name") != "online") continue;
                        f.Icon = candidate; icons++;
                    }
                    if (icons != 1) f.Icon = null;
                    if (f.Icon != null)
                    {
                        f.IconHandler = D.Read(f.Icon, "handler") as Delegate; f.IconChecker = D.Read(f.Icon, "checker") as Delegate;
                        GUIStyle style = D.Read(f.Icon, "btn") as GUIStyle;
                        f.IconReady = f.Gift != null && ExactDelegate(f.IconHandler, f.Main, "_online_reward", Type.EmptyTypes, typeof(void)) &&
                            ExactDelegate(f.IconChecker, f.Main, "_online_check", new[] { typeof(string) }, typeof(bool)) &&
                            D.Bool(D.Read(f.Icon, "enable")) && style != null && style.normal.background != null &&
                            style.normal.background.name == "icon_libao_normal" && D.Int(D.Read(f.Icon, "btnMain_type")) == 0 && f.Template != null;
                    }
                }
                object loader = D.Static(Model.FirstType("BaseAsset"), "lds");
                f.Loading = loader == null || ((Behaviour)loader).enabled || D.Bool(D.Read(loader, "isBattling")) ||
                    D.Bool(D.Read(f.Context.Root, "bLoading")) || D.Int(D.Read(f.Context.Root, "ExpectingCount")) != 0;
                f.NetworkBusy = ((IList)D.Read(f.Client, "messages")).Count != 0 || ((IList)D.Read(f.Client, "m_sendDatas")).Count != 0;
                foreach (DictionaryEntry item in (IDictionary)D.Read(f.Client, "m_handlers"))
                    if (Convert.ToSingle(D.Read(item.Value, "life"), CultureInfo.InvariantCulture) > 0f) { f.NetworkBusy = true; break; }
                f.Home = f.Scene == "Capital" && !f.Loading && f.Sub == null && D.Enabled(f.Main) && !RewardPending(f.Reward) &&
                    !D.Visible(f.Gift) && (f.Gift == null || !D.Loading(f.Gift)) && f.Context.Panels(f.Demon) == "PASS";
                f.CleanMain = !f.Loading && !f.NetworkBusy && f.Sub == null && D.Enabled(f.Main) && !RewardPending(f.Reward) &&
                    !D.Visible(f.Gift) && (f.Gift == null || !D.Loading(f.Gift)) && f.Context.Panels(f.Demon) == "PASS";
            }
            catch (Exception error) { f.Error = error.Message; }
            return f;
        }

        private static bool ExactDelegate(Delegate value, Component owner, string method, Type[] args, Type result)
        { return value != null && value.GetInvocationList().Length == 1 && ReferenceEquals(value.Target, owner) && value.Method == D.Method(owner.GetType(), method, args, false, result); }
        private static string TierStamp(object value)
        {
            if (value == null) return "";
            StringBuilder result = new StringBuilder();
            foreach (string key in new[] { "id", "time", "award1_type", "award1_amout", "award2_type", "award2_amout", "award3_type", "award3_amout" })
                result.Append(Convert.ToString(Value(value, key), CultureInfo.InvariantCulture)).Append(':');
            return result.ToString();
        }
        private static object Value(object value, string name)
        {
            if (value != null) for (Type type = value.GetType(); type != null; type = type.BaseType)
            {
                FieldInfo field = type.GetField(name, D.Instance | BindingFlags.DeclaredOnly); if (field != null) return field.GetValue(value);
                PropertyInfo property = type.GetProperty(name, D.Instance | BindingFlags.DeclaredOnly);
                if (property != null && property.GetIndexParameters().Length == 0) return property.GetValue(value, null);
            }
            throw new ProbeFailure("online_gift_value_schema:" + name);
        }
        private static object Handler(object client) { return ((IDictionary)D.Read(client, "m_handlers"))[Opcode]; }
        private static bool RewardPending(Component reward) { return reward != null && (D.Visible(reward) || D.Loading(reward)); }
        private static bool RewardReady(Component reward)
        { return reward != null && D.Visible(reward) && D.Enabled(reward) && !D.Loading(reward) && (DateTime)D.Read(reward, "m_LoadOverTime") <= DateTime.Now; }
        private int Elapsed() { return unchecked((int)((uint)(Environment.TickCount - tick))); }
        private void Offer(string action) { nextAction = action; Set("READY", ""); }
        private void WaitCleanup(string why)
        { if (Elapsed() > WaitMs) Unknown(why); else Set("PENDING", why); }
        private void Finish(string value, string why) { holding = false; closed = true; nextAction = ""; Set(value, why); }
        private void ReleaseUnsent(string why) { holding = false; closed = true; nextAction = ""; Set("BLOCKED", why); }
        private void Unknown(string why)
        {
            unsafeState = true; queued = null; nextAction = "";
            // Keep an ambiguous operation busy. A late response retains the
            // exact guarded stock callback; no new owner adopts or retries it.
            if (tap != null) { tap.StopObserving(true); if (tap.TryRestore()) tap = null; }
            Set("UNKNOWN", why);
        }
        internal void Stop(string why)
        {
            stopped = true; queued = null; nextAction = "";
            if (tap != null) { tap.StopObserving(true); if (tap.TryRestore()) tap = null; }
            if (!attempted && begun && frame != null && frame.Home) { holding = false; closed = true; }
            Set("STOPPED", why);
        }
        internal void NotInvoked(string why)
        {
            // Preserve the exact cancelled request before dropping the queue.
            // An unrelated adapter cancellation cannot rewrite this witness or
            // erase an invocation which already happened.
            if (queued != null && !GuiInvocationAttempted)
            { notInvoked = queued; notInvokedReason = why; }
            queued = null; nextAction = ""; Set(status, why);
        }
        private bool CleanInitialNotInvoked()
        {
            return notInvoked != null && (notInvoked.Action == "begin" || notInvoked.Action == "open") &&
                !GuiInvocationAttempted && !Busy && !begun && !attempted && !unsafeState && !stopped &&
                operationId == "" && initial == null && receipt == null && tap == null &&
                frame != null && frame.Error == "" && frame.CleanMain && frame.SameQuote(notInvoked.Quote);
        }
        private void Set(string value, string why)
        { status = value; reason = why; string body = Body(); if (body != stamp) { stamp = body; Revision++; } }
        private string Body()
        {
            Frame tier = initial ?? frame;
            StringBuilder rows = new StringBuilder("[");
            if (receipt != null)
            {
                StringBuilder types = new StringBuilder("["), amounts = new StringBuilder("[");
                for (int i = 0; i < receipt.Types.Length; i++) { if (i != 0) { types.Append(','); amounts.Append(','); } types.Append(receipt.Types[i]); amounts.Append(receipt.Amounts[i]); }
                types.Append(']'); amounts.Append(']');
                rows.Append("{\"operation_id\":").Append(D.Q(receipt.Operation)).Append(",\"sequence\":").Append(receipt.Sequence).Append(",\"revision\":").Append(receipt.Revision)
                    .Append(",\"opcode\":791,\"tier\":").Append(receipt.Sent.Got + 1).Append(",\"got_before\":").Append(receipt.Sent.Got).Append(",\"got_after\":").Append(receipt.GotAfter)
                    .Append(",\"next_get_time_before\":").Append(receipt.Sent.NextGet).Append(",\"next_get_time_after\":").Append(receipt.NextAfter)
                    .Append(",\"operate\":").Append(receipt.HasOperate ? receipt.Operate.ToString(CultureInfo.InvariantCulture) : "null")
                    .Append(",\"receipt_matched\":").Append(D.B(receipt.Matched)).Append(",\"stock_callback_returned\":").Append(D.B(receipt.Returned))
                    .Append(",\"request_submitted\":").Append(D.B(receipt.Submitted)).Append(",\"page_confirmed\":").Append(D.B(receipt.PageConfirmed))
                    .Append(",\"no_reply_cleanup_returned\":").Append(D.B(receipt.NullCleanupReturned))
                    .Append(",\"progress_confirmed\":").Append(D.B(receipt.ProgressConfirmed)).Append(",\"next_time_confirmed\":").Append(D.B(receipt.NextTimeConfirmed)).Append(",\"reward_owned\":").Append(D.B(receipt.RewardOwned))
                    .Append(",\"reward_closed\":").Append(D.B(receipt.RewardClosed)).Append(",\"normal_close_invoked\":").Append(D.B(receipt.CloseInvoked))
                    .Append(",\"normal_close_returned\":").Append(D.B(receipt.CloseReturned)).Append(",\"unavailable_panel_owned\":").Append(D.B(receipt.UnavailableOwned))
                    .Append(",\"unavailable_close_invoked\":").Append(D.B(receipt.UnavailableCloseInvoked)).Append(",\"type_id\":").Append(types).Append(",\"amount\":").Append(amounts).Append('}');
            }
            rows.Append(']');
            string cancellation = notInvoked == null ? "null" :
                "{\"operation_id\":" + D.Q(notInvoked.Id) + ",\"sequence\":" + notInvoked.Sequence +
                ",\"action\":" + D.Q(notInvoked.Action) + ",\"revision\":" + notInvoked.Revision +
                ",\"role_id\":" + D.Q(notInvoked.Quote.Role.ToString(CultureInfo.InvariantCulture)) +
                ",\"refresh_cycle\":" + notInvoked.Quote.Cycle + ",\"reason\":" + D.Q(notInvokedReason) +
                ",\"initial_clean\":" + D.B(CleanInitialNotInvoked()) + "}";
            return "\"task_id\":309,\"route\":\"online_gift\",\"role_id\":" + D.Q(frame == null ? "0" : frame.Role.ToString(CultureInfo.InvariantCulture)) +
                ",\"refresh_cycle\":" + (frame == null ? 0 : frame.Cycle) + ",\"server_now\":" + (frame == null ? 0 : frame.Now) +
                ",\"operation_id\":" + D.Q(operationId) + ",\"sequence\":" + sequence + ",\"last_action\":" + D.Q(lastAction) +
                ",\"invocation_attempted\":" + D.B(GuiInvocationAttempted) + ",\"not_invoked\":" + cancellation +
                ",\"status\":" + D.Q(status) + ",\"reason\":" + D.Q(reason) + ",\"next_action\":" + D.Q(nextAction) +
                ",\"mutation_attempted\":" + D.B(attempted) + ",\"busy\":" + D.B(Busy) + ",\"closed\":" + D.B(closed) +
                ",\"cleanup_pending\":" + D.B(Busy) + ",\"home_ready\":" + D.B(frame != null && frame.Home) + ",\"initial_frozen\":" + D.B(begun) +
                ",\"tier\":" + (tier == null ? 0 : tier.Got + 1) + ",\"got_before\":" + (tier == null ? 0 : tier.Got) +
                ",\"got_after\":" + (receipt == null ? tier == null ? 0 : tier.Got : receipt.GotAfter) + ",\"next_get_time\":" + (tier == null ? 0 : tier.NextGet) +
                ",\"tier_minutes\":" + (tier == null ? 0 : tier.TierMinutes) + ",\"template_exists\":" + D.B(tier != null && tier.Template != null) +
                ",\"tier_due\":" + D.B(tier != null && tier.Due) + ",\"source_icon\":\"icon_libao\",\"source_handler\":\"MainUI._online_reward\",\"source_opcode\":791" +
                ",\"source_icon_ready\":" + D.B(frame != null && frame.IconReady) + ",\"receipts\":" + rows;
        }
        internal string Json()
        { return "{\"schema\":1,\"revision\":" + Revision + ",\"observation_seq\":" + observation + "," + Body() + "}"; }
    }
}
