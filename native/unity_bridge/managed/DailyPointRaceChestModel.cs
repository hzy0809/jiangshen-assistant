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
    // Reward-only owner. It never adopts the battle model or invokes a battle,
    // refresh, chance purchase, paid find-back, or a direct transport send.
    internal sealed class DailyPointRaceChestModel
    {
        internal const int TaskId = 310;
        private const ushort EnterOp = 1124, ClaimOp = 1126;
        private const uint Timeout = 15000;
        private sealed class Frame
        {
            internal D Context, Fresh;
            internal object Role, Client, Race, Loader, Current, Sub, Rows, Row, ReturnClose;
            internal object[] BonusRows;
            internal object RewardItems;
            internal Component RewardPrevious;
            internal Delegate RewardClose;
            internal DateTime RewardShown;
            internal bool OrdinaryReward;
            internal Component Home, Select, Panel, Scene, Chest, Main, Reward, ReturnRoot;
            internal DailyTransport Transport;
            internal ulong RoleId;
            internal uint Now, Day, Cycle, Boundary, Time, GoldConsumed;
            internal int Index = -1, Eligible, Historical, Count, Gold, Bound, Challenges, Bought, Refreshes;
            internal bool InScene, AtHome, Loading, Battling, Got;
            internal string RowsKey = "";
            internal bool SameOwner(Frame old)
            {
                return old != null && ReferenceEquals(Role, old.Role) && ReferenceEquals(Client, old.Client) &&
                    ReferenceEquals(Race, old.Race) && ReferenceEquals(Loader, old.Loader) &&
                    ReferenceEquals(Context.GlobalUi, old.Context.GlobalUi) && ReferenceEquals(Context.UiRoot, old.Context.UiRoot) &&
                    ReferenceEquals(Home, old.Home) && RoleId == old.RoleId && Day == old.Day && Cycle == old.Cycle &&
                    Boundary == old.Boundary && Now >= old.Now && Transport.Same(old.Transport);
            }
            internal bool SamePremium(Frame old)
            { return Gold == old.Gold && Bound == old.Bound && GoldConsumed == old.GoldConsumed; }
            internal bool SameOffer(Frame old)
            {
                return SameOwner(old) && SamePremium(old) && ReferenceEquals(Current, old.Current) &&
                    ReferenceEquals(Sub, old.Sub) && ReferenceEquals(Panel, old.Panel) && ReferenceEquals(Scene, old.Scene) &&
                    ReferenceEquals(Chest, old.Chest) && ReferenceEquals(ReturnRoot, old.ReturnRoot) &&
                    ReferenceEquals(ReturnClose, old.ReturnClose) && RowsKey == old.RowsKey && ReferenceEquals(Rows, old.Rows) &&
                    ReferenceEquals(Reward, old.Reward) && ReferenceEquals(RewardItems, old.RewardItems) &&
                    ReferenceEquals(RewardPrevious, old.RewardPrevious) && ReferenceEquals(RewardClose, old.RewardClose) &&
                    RewardShown == old.RewardShown && OrdinaryReward == old.OrdinaryReward &&
                    SameRows(BonusRows, old.BonusRows) &&
                    ReferenceEquals(Row, old.Row) && Index == old.Index && Got == old.Got &&
                    Challenges == old.Challenges && Bought == old.Bought && Refreshes == old.Refreshes &&
                    Loading == old.Loading && Battling == old.Battling && InScene == old.InScene && AtHome == old.AtHome;
            }
            private static bool SameRows(object[] current, object[] old)
            {
                if (current == null || old == null || current.Length != old.Length) return false;
                for (int i = 0; i < current.Length; i++) if (!ReferenceEquals(current[i], old[i])) return false;
                return true;
            }
        }

        private Frame frame, queued, owner, sent, revisionFrame;
        private DailyDivinityReplyTap entryTap, claimTap;
        private object entryHandler;
        private Delegate entryOriginal;
        private Component ownedPanel, ownedChest, ownedScene;
        private string id = "", action = "", next = "", status = "IDLE", reason = "", stamp = "", finish = "";
        private int revision, sequence, started, entrySequence, requestSequence, requests, confirmed, beforeCount, afterCount;
        private uint requestedTime;
        private int requestedIndex = -1;
        private uint? receiptResult, receiptTime, receiptGold;
        private bool available, touched, entered, entryReturned, entryReleased, callbackReturned, stateConfirmed;
        private bool returning, closed, stopped, unknown, chestOpened, chestClosed, claimPending, finishRequested;
        private readonly Hashtable attemptedTimes = new Hashtable();
        private sealed class ClaimReceipt
        {
            internal int Sequence, Index, Before, After = -1;
            internal uint RequestedTime;
            internal uint? Result, Time, Gold;
            internal bool Returned, Confirmed;
            internal string Json()
            {
                return "{\"request_sequence\":" + Sequence + ",\"requested_time\":" + RequestedTime + ",\"requested_index\":" + Index +
                    ",\"receipt_result\":" + Nullable(Result) + ",\"receipt_time\":" + Nullable(Time) + ",\"receipt_gold\":" + Nullable(Gold) +
                    ",\"callback_returned\":" + D.B(Returned) + ",\"state_confirmed\":" + D.B(Confirmed) +
                    ",\"before_bonus_count\":" + Before + ",\"after_bonus_count\":" + After + "}";
            }
        }
        private readonly ArrayList receipts = new ArrayList();
        private ClaimReceipt currentReceipt;
        private Component rewardUi;
        private Delegate requestRewardClose;
        private object rewardItems;
        private DateTime rewardShown;
        private bool rewardAppearanceBound, rewardCloseAttempted, rewardClosed;
        private int rewardCloseRequestSequence, rewardCloseCommandSequence;
        private sealed class RewardClosure
        {
            internal int RequestSequence, CommandSequence;
            internal long Ticks;
            internal bool CallbackReturned, Closed;
            internal string Json()
            { return "{\"request_sequence\":" + RequestSequence + ",\"command_sequence\":" + CommandSequence + ",\"appearance_ticks\":" + D.Q(Ticks.ToString(CultureInfo.InvariantCulture)) +
                ",\"callback_returned\":" + D.B(CallbackReturned) + ",\"reward_closed\":" + D.B(Closed) + "}"; }
        }
        private readonly ArrayList rewardClosures = new ArrayList();
        private int capturedFrame = -1;

        internal bool Busy { get { return queued != null || touched && !closed || entryTap != null || claimTap != null; } }
        private bool HomeReady()
        {
            return available && frame != null && frame.Fresh != null && frame.AtHome && !frame.Loading && !frame.Battling &&
                ReferenceEquals(frame.Fresh.Root, frame.Home) && frame.Fresh.Gate == "PASS" && frame.Fresh.Panels() == "PASS" &&
                frame.Sub == null && Ready(frame.Main) && D.Bool(D.Static(Model.FirstType("Global"), "extern_enabled")) &&
                ReferenceEquals(D.Read(frame.Context.GlobalUi, "level_rmg"), frame.Home) &&
                !D.Bool(D.Read(frame.Home, "bLoading")) && D.Int(D.Read(frame.Home, "ExpectingCount")) == 0 &&
                !Visible(frame.Panel) && !Visible(frame.Chest) && !Visible(frame.Select) && !Visible(frame.Reward) &&
                (frame.Panel == null || !D.Loading(frame.Panel)) && (frame.Chest == null || !D.Loading(frame.Chest)) &&
                TransportIdle(frame, true);
        }
        private bool ReleaseVerified
        { get { return closed && !unknown && !stopped && capturedFrame == Time.frameCount && HomeReady() &&
            !Busy && entryReleased && chestClosed && !claimPending; } }
        internal bool PopupHandoffReady
        { get { return available && !stopped && !unknown && claimPending && callbackReturned && stateConfirmed &&
            claimTap == null && frame != null && sent != null && frame.SameOwner(sent) &&
            frame.InScene && !frame.Loading && !frame.Battling; } }
        internal bool OwnsRewardPopup(Component popup, Delegate close)
        {
            return PopupHandoffReady && popup != null && ReferenceEquals(popup, frame.Reward) &&
                ReferenceEquals(popup, D.Static(Model.FirstType("__BaseUI"), "battle_result_ui")) &&
                ReferenceEquals(popup, rewardUi) && frame.OrdinaryReward && ReferenceEquals(frame.RewardItems, rewardItems) &&
                ReferenceEquals(close, requestRewardClose) && frame.RewardPrevious == null &&
                close != null && close.GetInvocationList().Length == 1 && ReferenceEquals(close, D.Read(popup, "OnClose")) &&
                ReferenceEquals(close.Target, ownedChest) && close.Method == D.Method(ownedChest.GetType(), "BonusGetClose", Type.EmptyTypes, false, typeof(void));
        }

        internal void Capture(D context)
        {
            available = false; capturedFrame = -1;
            try
            {
                frame = Read(context); available = true; capturedFrame = Time.frameCount;
                if (owner != null && (!frame.SameOwner(owner) || !frame.SamePremium(owner)))
                    throw new ProbeFailure("point_chest_owner_cycle_or_premium_changed");
                if (ownedPanel != null && (!ReferenceEquals(frame.Panel, ownedPanel) || !ReferenceEquals(frame.Scene, ownedScene) ||
                    !ReferenceEquals(frame.Chest, ownedChest))) throw new ProbeFailure("point_chest_scene_identity_changed");
                if (stopped || unknown) { next = ""; return; }
                Observe();
            }
            catch (Exception error) { Fail(error is ProbeFailure ? error.Message : "point_chest_observation_failed"); }
            finally { Publish(); }
        }
        private void Observe()
        {
            next = "";
            if (entryTap != null && entryTap.Error != null) throw new ProbeFailure(entryTap.Error);
            if (claimTap != null && claimTap.Error != null) throw new ProbeFailure(claimTap.Error);
            if (entryTap != null && entryTap.Returned) ReleaseEntry();
            if (claimTap != null && claimTap.Returned)
            { claimTap.StopObserving(); if (claimTap.TryRestore()) claimTap = null; }
            if (closed) return;
            if (queued != null) return;
            if (touched && unchecked((uint)(Environment.TickCount - started)) >= Timeout)
                throw new ProbeFailure("point_chest_operation_timeout");
            if (returning)
            {
                if (frame.AtHome && ReferenceEquals(frame.Sub, frame.Select) && Ready(frame.Select)) Offer("close_selector");
                else if (HomeReady())
                {
                    closed = true; touched = false; chestClosed = !Visible(frame.Chest) && (frame.Chest == null || !D.Loading(frame.Chest));
                    if (entrySequence == 0) entryReleased = true;
                    status = finishRequested && frame.Eligible > 0 ? confirmed > 0 ? "PARTIAL" : "UNAVAILABLE" :
                        confirmed > 0 ? "COMPLETED" : frame.Got ? "ALREADY_DONE" : "UNAVAILABLE";
                    reason = finish;
                }
                else Wait("point_chest_returning_home");
                return;
            }
            if (!touched)
            {
                if (HomeReady()) Offer("open");
                else { status = "UNAVAILABLE"; reason = "point_chest_home_required"; }
                return;
            }
            if (entrySequence == 0)
            {
                if (finishRequested)
                { returning = true; finish = "point_chest_finished"; if (frame.AtHome && ReferenceEquals(frame.Sub, frame.Select) && Ready(frame.Select)) Offer("close_selector"); }
                else if (frame.AtHome && ReferenceEquals(frame.Sub, frame.Select) && Ready(frame.Select) && TransportIdle(frame, false)) Offer("enter");
                else Wait("point_chest_waiting_selector");
                return;
            }
            if (!entered || !entryReturned || !entryReleased) { Wait("point_chest_waiting_entry_receipt"); return; }
            if (!frame.InScene || frame.Loading || frame.Battling) { Wait("point_chest_waiting_scene"); return; }
            if (!ReferenceEquals(D.Read(frame.Panel, "m_pointrace"), frame.Race) || !ReferenceEquals(frame.ReturnRoot, frame.Home) || frame.ReturnClose != null)
                throw new ProbeFailure("point_chest_return_contract_changed");
            if (claimPending)
            {
                if (!callbackReturned || !stateConfirmed || claimTap != null) { Wait("point_chest_waiting_claim_receipt"); return; }
                if (Visible(frame.Reward) || frame.Reward != null && D.Loading(frame.Reward))
                {
                    Wait("point_chest_waiting_reward_close");
                    if (Ready(frame.Reward) && RewardOwned())
                    {
                        if (!rewardAppearanceBound) { rewardShown = frame.RewardShown; rewardAppearanceBound = true; }
                        if (rewardShown != frame.RewardShown) throw new ProbeFailure("point_chest_reward_appearance_changed");
                        if (rewardCloseAttempted) throw new ProbeFailure("point_chest_reward_close_unresolved");
                        Offer("close_reward");
                    }
                    return;
                }
                if (Visible(frame.Chest) && !Ready(frame.Chest) || !Visible(frame.Chest) && !Ready(frame.Panel))
                { Wait("point_chest_waiting_stock_close"); return; }
                claimPending = false;
                rewardClosed = true;
            }
            if (!TransportIdle(frame, false)) { Wait("point_chest_waiting_transport"); return; }
            if (frame.Eligible == 0 || finishRequested)
            {
                finish = finishRequested && frame.Eligible > 0 ? "point_chest_finished" : confirmed > 0 ? "point_chest_free_reward_confirmed" : frame.Got ? "point_chest_already_claimed" :
                    frame.Historical > 0 ? "point_chest_historical_paid_only" : "point_chest_no_free_reward";
                if (Visible(frame.Chest)) { if (ChestReady()) Offer("close_chest"); else Wait("point_chest_waiting_chest"); }
                else if (Ready(frame.Panel)) { chestClosed = true; Offer("return"); }
                else Wait("point_chest_waiting_point_page");
                return;
            }
            if (attemptedTimes.ContainsKey(frame.Time)) throw new ProbeFailure("point_chest_attempted_row_reappeared");
            if (!Visible(frame.Chest))
            {
                if (Ready(frame.Panel) && !Visible(frame.Reward)) Offer("open_chest");
                else Wait("point_chest_waiting_point_page");
            }
            else if (ChestReady()) Offer("claim");
            else Wait("point_chest_waiting_chest");
        }
        private bool ChestReady()
        {
            Delegate close = frame.Chest == null ? null : D.Read(frame.Chest, "OnClose") as Delegate;
            return Ready(frame.Chest) && ReferenceEquals(frame.Sub, frame.Chest) &&
                ReferenceEquals(D.Read(frame.Chest, "m_pointrace"), frame.Race) && close != null && close.GetInvocationList().Length == 1 &&
                ReferenceEquals(close.Target, frame.Panel) && close.Method == D.Method(frame.Panel.GetType(), "EnableAll", Type.EmptyTypes, false, typeof(void));
        }
        private void Offer(string value) { status = "READY"; reason = ""; next = value; }
        private void Wait(string value) { status = "PENDING"; reason = value; }
        private void Fail(string value)
        { unknown = true; status = "UNKNOWN"; reason = value; next = action = ""; queued = null; Detach(); }
        internal void NotInvoked(string value)
        { queued = null; action = next = ""; reason = value; status = "WAITING"; Publish(); }
        internal void Stop(string detail = "stop")
        { stopped = true; queued = null; next = action = ""; status = "UNKNOWN"; reason = "point_chest_stopped:" + detail; Detach(); Publish(); }

        internal bool Queue(int task, ulong role, string operation, int seq, int expected, string requested, out string why)
        {
            why = "point_chest_queue_unavailable";
            if (task != TaskId || stopped || unknown || closed || !available || frame == null || queued != null ||
                role != frame.RoleId || seq <= sequence || !Command.ValidNonce(operation)) return false;
            why = "point_chest_revision_changed"; if (expected != revision) return false;
            why = "point_chest_action_changed";
            if (requested == "finish") { if (!touched || owner == null || finishRequested) return false; }
            else if (requested == "" || requested != next) return false;
            why = "point_chest_operation_changed"; if (id != "" && id != operation) return false;
            id = operation; sequence = seq; action = requested; queued = frame; why = ""; return true;
        }
        internal bool ExecuteGui(DailyPointRaceContextProvider fresh, DevelopmentPublication publish, DailyPointRaceInvocationGate begin, out string why)
        {
            why = "point_chest_not_queued"; Frame before = queued; string requested = action;
            if (before == null || stopped || unknown || !D.Gui) return false;
            if (!Monitor.TryEnter(before.Client, 0)) { why = "point_chest_client_busy"; return false; }
            try
            {
                Frame sample = Read(fresh());
                if (!sample.SameOffer(before)) { NotInvoked("point_chest_offer_changed"); why = reason; return false; }
                publish(); Frame accepted = null;
                if (!begin(delegate { accepted = Read(fresh()); return !stopped && !unknown && D.Gui && accepted.SameOffer(before); },
                    delegate
                    {
                        if (stopped || unknown || accepted == null || !accepted.Transport.StillCurrent()) return false;
                        Frame latest = Read(fresh());
                        if (!latest.SameOffer(accepted)) return false;
                        accepted = latest;
                        return requested != "close_reward" || rewardAppearanceBound && !rewardCloseAttempted &&
                            latest.RewardShown == rewardShown && Ready(latest.Reward);
                    }))
                { NotInvoked("point_chest_gui_cancelled"); why = reason; return false; }
                frame = accepted; queued = null; next = action = "";
                if (!touched) { owner = frame; touched = true; }
                started = Environment.TickCount;
                Invoke(requested); Publish(); why = ""; return true;
            }
            catch (Exception error) { Fail(error is ProbeFailure ? error.Message : "point_chest_stock_call_failed"); Publish(); why = reason; return false; }
            finally { Monitor.Exit(before.Client); }
        }
        private void Invoke(string requested)
        {
            if (requested == "finish") finishRequested = true;
            else if (requested == "open") D.MainBranch("_battle_arena");
            else if (requested == "enter")
            {
                if (finishRequested || entrySequence != 0 || !TransportIdle(frame, false)) throw new ProbeFailure("point_chest_entry_already_attempted");
                entrySequence = sequence;
                D.Method(frame.Context.GlobalUi.GetType(), "cs_pointrace_enter", new[] { Model.FirstType("RootMessenger"), Model.FirstType("__BaseUI+delegate_OnClose") }, false, typeof(void))
                    .Invoke(frame.Context.GlobalUi, new object[] { frame.Home, null });
                Frame after = Read(null); ownedPanel = after.Panel; ownedChest = after.Chest; ownedScene = after.Scene;
                if (ownedPanel == null || ownedChest == null || !after.SameOwner(owner)) throw new ProbeFailure("point_chest_entry_scene_missing");
                entryHandler = Handler(frame.Client, EnterOp); entryOriginal = Stock(entryHandler, ownedPanel, "sc_pointrace_enter_0x464", null);
                if ((float)D.Read(entryHandler, "life") != -1f) throw new ProbeFailure("point_chest_entry_not_persistent");
                string requestId = id; int seq = sequence;
                entryTap = DailyDivinityReplyTap.Attach(frame.Client, true, entryHandler, entryHandler.GetType().GetField("handler", D.Instance), null,
                    delegate { return Handler(owner.Client, EnterOp); },
                    delegate { return !stopped && !unknown && id == requestId && entrySequence == seq && !entryReturned && Read(null).SameOwner(owner); },
                    delegate(byte[] body)
                    {
                        BattleReplyPreflight.Deserialize(body, "game.net.data.StoC.SCPointraceEnter_0x464");
                        Frame current = Read(null);
                        if (!current.SameOwner(owner) || !current.SamePremium(owner) || !ReferenceEquals(current.Panel, ownedPanel))
                            throw new ProbeFailure("point_chest_entry_receipt_identity");
                        entered = entryReturned = true;
                    });
                if (entryTap == null || !entryTap.Attached) throw new ProbeFailure("point_chest_entry_callback_unobserved");
            }
            else if (requested == "open_chest")
            {
                if (finishRequested || !entered || frame.Eligible == 0 || !Ready(frame.Panel)) throw new ProbeFailure("point_chest_open_not_ready");
                D.Call(frame.Panel, "DisableAll");
                Delegate close = Delegate.CreateDelegate(Model.FirstType("__BaseUI+delegate_OnClose"), frame.Panel,
                    D.Method(frame.Panel.GetType(), "EnableAll", Type.EmptyTypes, false, typeof(void)));
                chestOpened = true; chestClosed = false;
                D.Method(frame.Chest.GetType(), "MyLoading", new[] { close.GetType() }, false, typeof(void)).Invoke(frame.Chest, new object[] { close });
            }
            else if (requested == "claim") Claim();
            else if (requested == "close_reward") CloseReward();
            else if (requested == "close_chest")
            {
                if (!ChestReady() || claimPending) throw new ProbeFailure("point_chest_close_not_ready");
                Delegate close = D.Read(frame.Chest, "OnClose") as Delegate;
                D.Call(frame.Chest, "Hide"); close.DynamicInvoke(null); chestClosed = true;
            }
            else if (requested == "return")
            {
                if (claimPending || Visible(frame.Chest) || !Ready(frame.Panel) || !ReferenceEquals(frame.ReturnRoot, frame.Home) || frame.ReturnClose != null)
                    throw new ProbeFailure("point_chest_return_unverified");
                returning = true; D.Call(frame.Panel, "_return");
            }
            else if (requested == "close_selector")
            {
                if (!returning || !frame.AtHome || !ReferenceEquals(frame.Sub, frame.Select)) throw new ProbeFailure("point_chest_selector_unowned");
                D.Call(frame.Select, "Hide");
                D.Method(Model.GameType("MainUI"), "SetSubUI", new[] { Model.FirstType("__BaseUI") }, true, typeof(void)).Invoke(null, new object[] { null });
            }
            else throw new ProbeFailure("point_chest_action_unknown");
            status = "PENDING";
        }
        private void Claim()
        {
            if (finishRequested || !ChestReady() || frame.Eligible == 0 || frame.Index < 0 || frame.Index > 3 || frame.Row == null ||
                attemptedTimes.ContainsKey(frame.Time) || claimPending || Handler(frame.Client, ClaimOp) != null || !TransportIdle(frame, false))
                throw new ProbeFailure("point_chest_claim_unavailable");
            sent = frame; requestedTime = frame.Time; requestedIndex = frame.Index; beforeCount = frame.Count; afterCount = -1;
            requestSequence = sequence; string requestId = id; int seq = sequence;
            requests++; attemptedTimes[requestedTime] = true; claimPending = true; callbackReturned = stateConfirmed = false;
            receiptResult = receiptTime = receiptGold = null;
            currentReceipt = new ClaimReceipt { Sequence = seq, Index = requestedIndex, RequestedTime = requestedTime, Before = beforeCount };
            receipts.Add(currentReceipt);
            rewardUi = null; rewardItems = null; rewardAppearanceBound = rewardCloseAttempted = rewardClosed = false;
            rewardCloseRequestSequence = rewardCloseCommandSequence = 0;
            Delegate close = Delegate.CreateDelegate(Model.FirstType("__BaseUI+delegate_OnClose"), frame.Chest,
                D.Method(frame.Chest.GetType(), "BonusGetClose", Type.EmptyTypes, false, typeof(void)));
            requestRewardClose = close;
            D.Call(frame.Chest, "Disable");
            // Exactly the free button branch in stock PointraceChestUI.Web_OnGUI.
            D.Method(frame.Panel.GetType(), "cs_pointrace_bonusget_0x466", new[] { typeof(bool), typeof(int), close.GetType() }, false, typeof(void))
                .Invoke(frame.Panel, new object[] { false, frame.Index, close });
            object handler = Handler(sent.Client, ClaimOp);
            Stock(handler, sent.Panel, "sc_pointrace_bonusget_0x466", close);
            claimTap = DailyDivinityReplyTap.Attach(sent.Client, false, handler, handler.GetType().GetField("handler", D.Instance), close,
                delegate { return Handler(sent.Client, ClaimOp); },
                delegate { return !stopped && !unknown && id == requestId && requestSequence == seq &&
                    Read(null).SameOffer(sent); }, Receipt,
                delegate(byte[] body)
                {
                    object packet = BattleReplyPreflight.Deserialize(body, "game.net.data.StoC.SCPointraceBonusGet_0x466");
                    uint result = Number(packet, "result"), time = Number(packet, "time"), gold = Number(packet, "gold_consume");
                    if (time != requestedTime || gold != 0 || result > 2) throw new ProbeFailure("point_chest_receipt_target_or_premium");
                });
            if (claimTap == null || !claimTap.Attached) throw new ProbeFailure("point_chest_claim_callback_unobserved");
        }
        private void Receipt(byte[] body)
        {
            callbackReturned = true;
            currentReceipt.Returned = true;
            object packet = BattleReplyPreflight.Deserialize(body, "game.net.data.StoC.SCPointraceBonusGet_0x466");
            receiptResult = Number(packet, "result"); receiptTime = Number(packet, "time"); receiptGold = Number(packet, "gold_consume");
            currentReceipt.Result = receiptResult; currentReceipt.Time = receiptTime; currentReceipt.Gold = receiptGold;
            Frame after = Read(null); afterCount = after.Count;
            currentReceipt.After = afterCount;
            if (receiptResult != 0) throw new ProbeFailure("point_chest_game_rejected:" + receiptResult);
            if (!after.SameOwner(sent) || !after.SamePremium(sent) || receiptTime != requestedTime || receiptGold != 0 ||
                !after.Got || after.Count != beforeCount - 1 || ContainsTime(after.Rows as IList, requestedTime) ||
                after.Challenges != sent.Challenges || after.Bought != sent.Bought || after.Refreshes != sent.Refreshes)
                throw new ProbeFailure("point_chest_receipt_state_mismatch");
            // Every other row must survive with its original reference and data.
            IList oldRows = sent.Rows as IList, rows = after.Rows as IList;
            if (!ReferenceEquals(oldRows, rows)) throw new ProbeFailure("point_chest_bonus_list_replaced");
            int pos = 0;
            foreach (object old in sent.BonusRows)
            {
                if (ReferenceEquals(old, sent.Row)) continue;
                if (pos >= rows.Count || !ReferenceEquals(rows[pos++], old)) throw new ProbeFailure("point_chest_unrelated_bonus_replaced");
            }
            string expected = "";
            foreach (object old in sent.BonusRows) if (!ReferenceEquals(old, sent.Row)) expected += RowKey(old);
            if (after.RowsKey != expected) throw new ProbeFailure("point_chest_unrelated_bonus_changed");
            stateConfirmed = currentReceipt.Confirmed = true; confirmed++;
            Component popup = after.Reward;
            // A missing template invokes BonusGetClose directly and creates no
            // popup. A newly created popup must carry this exact claim delegate.
            if (popup != null && ReferenceEquals(D.Read(popup, "OnClose"), requestRewardClose))
            { rewardUi = popup; rewardItems = D.Read(popup, "m_vctBonuses"); }
        }
        private void CloseReward()
        {
            Frame latest = Read(null);
            if (!latest.SameOffer(frame) || !rewardAppearanceBound || rewardCloseAttempted ||
                frame.RewardShown != rewardShown || !Ready(frame.Reward) || !RewardOwned())
                throw new ProbeFailure("point_chest_reward_close_identity_changed");
            RewardClosure closure = new RewardClosure { RequestSequence = requestSequence, CommandSequence = sequence, Ticks = rewardShown.Ticks };
            rewardClosures.Add(closure); rewardCloseAttempted = true;
            rewardCloseRequestSequence = requestSequence; rewardCloseCommandSequence = sequence;
            // The ordinary BattleResultUI OK branch is Hide then OnClose.
            // Consume the exact appearance before either call; never replay it.
            D.Call(frame.Reward, "Hide"); requestRewardClose.DynamicInvoke(new object[0]);
            closure.CallbackReturned = true;
            Frame after = Read(null);
            closure.Closed = rewardClosed = !Visible(after.Reward) && (after.Reward == null || !D.Loading(after.Reward));
            if (!after.SameOwner(sent) || !after.SamePremium(sent) || !rewardClosed ||
                !ReferenceEquals(after.Panel, ownedPanel) || !ReferenceEquals(after.Chest, ownedChest) ||
                (after.Count == 0 ? !Ready(after.Panel) || Visible(after.Chest) : !Ready(after.Chest)))
                throw new ProbeFailure("point_chest_reward_close_unverified");
        }
        private static uint Number(object packet, string name)
        { return Convert.ToUInt32(packet.GetType().GetProperty(name).GetValue(packet, null), CultureInfo.InvariantCulture); }
        private static Delegate Stock(object handler, Component target, string method, object arg)
        {
            Delegate value = handler == null ? null : D.Read(handler, "handler") as Delegate;
            if (handler == null || handler.GetType() != Model.FirstType("game.net.tcp_base+Handler") || value == null ||
                value.GetInvocationList().Length != 1 || !ReferenceEquals(value.Target, target) ||
                value.Method != D.Method(target.GetType(), method, new[] { typeof(MemoryStream), typeof(object) }, false, typeof(void)) ||
                !ReferenceEquals(D.Read(handler, "arg"), arg)) throw new ProbeFailure("point_chest_stock_registration_changed");
            return value;
        }
        private void ReleaseEntry()
        {
            entryTap.StopObserving();
            if (!entryTap.TryRestore()) return;
            if (!ReferenceEquals(Handler(owner.Client, EnterOp), entryHandler) ||
                !ReferenceEquals(D.Read(entryHandler, "handler"), entryOriginal) || D.Read(entryHandler, "arg") != null ||
                (float)D.Read(entryHandler, "life") != -1f) throw new ProbeFailure("point_chest_entry_listener_release_unverified");
            entryReleased = true; entryTap = null;
        }
        private void Detach()
        {
            if (entryTap != null) { entryTap.StopObserving(); if (entryTap.TryRestore()) entryTap = null; }
            // Preserve the one-shot preflight for an unresolved claim. A stop or
            // unknown outcome cannot restore an unguarded late claim callback.
            if (claimTap != null) { claimTap.StopObserving(); if (claimTap.Returned && claimTap.TryRestore()) claimTap = null; }
        }
        private static object Handler(object client, ushort opcode)
        { IDictionary handlers = D.Read(client, "m_handlers") as IDictionary; return handlers == null ? null : handlers[opcode]; }
        private static bool TransportIdle(Frame f, bool all)
        {
            IDictionary handlers = D.Read(f.Client, "m_handlers") as IDictionary;
            IList messages = D.Read(f.Client, "messages") as IList, sends = D.Read(f.Client, "m_sendDatas") as IList;
            if (handlers == null || messages == null || messages.Count != 0 || sends == null || sends.Count != 0 ||
                D.Bool(D.Read(f.Client, "m_bSending")) || DevelopmentCallbackCapture.AnyCallbackExecuting || !f.Transport.StillCurrent()) return false;
            foreach (DictionaryEntry item in handlers) if ((float)D.Read(item.Value, "life") > 0f) return false;
            if (handlers.Contains(ClaimOp)) return false;
            return true;
        }
        private Frame Read(D supplied)
        {
            if (supplied != null && supplied.Paused) throw new ProbeFailure("point_chest_paused");
            D context = owner == null ? supplied : owner.Context;
            if (context == null || context.Character == null || context.Root == null || context.GlobalUi == null || context.UiRoot == null)
                throw new ProbeFailure("point_chest_context_missing");
            if (supplied != null && (!ReferenceEquals(supplied.Character, context.Character) ||
                !ReferenceEquals(supplied.Client, context.Client) || !ReferenceEquals(supplied.GlobalUi, context.GlobalUi) ||
                !ReferenceEquals(supplied.UiRoot, context.UiRoot))) throw new ProbeFailure("point_chest_fresh_context_identity_changed");
            object role = D.Static(Model.FirstType("Global"), "character_");
            object cache = D.Static(Model.GameType("BaseUI"), "rootUI");
            if (!ReferenceEquals(role, context.Character) || !ReferenceEquals(D.Read(context.GlobalUi, "rmg"), context.UiRoot) ||
                cache != null && !ReferenceEquals(cache, context.GlobalUi)) throw new ProbeFailure("point_chest_root_or_role_changed");
            Frame f = new Frame { Context = context, Fresh = supplied, Role = role, Client = context.Client,
                Race = D.Read(role, "pointrace"), Loader = D.Static(Model.FirstType("BaseAsset"), "lds"),
                RoleId = Convert.ToUInt64(D.Read(role, "id"), CultureInfo.InvariantCulture), Home = context.Root,
                Sub = context.SubUi, Gold = D.Int(D.Read(role, "gold")), Bound = D.Int(D.Read(role, "bind_gold")),
                GoldConsumed = D.UInt(D.Read(role, "gold_consume")) };
            if (!IsHome(f.Home) || f.RoleId == 0 || !ReferenceEquals(f.Client, D.Static(Model.FirstType("Global"), "m_client")) ||
                !ReferenceEquals(f.Client, D.Static(Model.GameType("RootUI"), "m_client"))) throw new ProbeFailure("point_chest_home_or_client_changed");
            f.Transport = DailyTransport.Read(f.Client); if (f.Transport == null) throw new ProbeFailure("point_chest_transport_missing");
            string clock = DailyRefreshGate.Read(role, out f.Now, out f.Day, out f.Cycle); if (clock != "") throw new ProbeFailure(clock);
            f.Boundary = f.Now <= (long)f.Day + 72000 ? checked(f.Day - 86400) : f.Day;
            f.Got = D.Bool(D.Read(f.Race, "isgot")); f.Rows = D.Read(f.Race, "bonuses"); IList rows = f.Rows as IList;
            if (rows == null || rows.Count > 32) throw new ProbeFailure("point_chest_bonus_schema");
            Hashtable times = new Hashtable(); f.Count = rows.Count; f.BonusRows = new object[rows.Count]; bool displayed = true;
            for (int i = 0; i < rows.Count; i++)
            {
                object row = rows[i]; f.BonusRows[i] = row; uint time = D.UInt(D.Read(row, "time"));
                if (time == 0 || times.ContainsKey(time)) throw new ProbeFailure("point_chest_bonus_identity_ambiguous");
                times[time] = true; f.RowsKey += RowKey(row);
                if (i > 3 || !displayed) continue;
                if (time < (long)f.Boundary - 518400) { displayed = false; continue; }
                if (time < f.Boundary) { f.Historical++; continue; }
                f.Eligible++; if (f.Index < 0) { f.Index = i; f.Time = time; f.Row = row; }
            }
            f.Challenges = D.Int(D.Read(f.Race, "challenge_count")); f.Bought = D.Int(D.Read(f.Race, "challenge_buy")); f.Refreshes = D.Int(D.Read(f.Race, "refresh_count"));
            f.Current = D.Read(f.Loader, "currmg"); f.Battling = D.Bool(D.Read(f.Loader, "isBattling"));
            f.Loading = (bool)D.Method(f.Loader.GetType(), "isVisible", Type.EmptyTypes, false, typeof(bool)).Invoke(f.Loader, null);
            f.Select = context.Find("ArenaSelectUI"); GameObject go = GameObject.Find("/PointRace/PointRaceUI");
            f.Panel = go == null ? ownedPanel : go.GetComponent(Model.GameType("PointraceUI"));
            f.Chest = go == null ? ownedChest : go.GetComponent(Model.GameType("PointraceChestUI"));
            f.Scene = f.Panel == null ? null : D.Read(f.Panel, "rmg") as Component;
            if (f.Panel != null && (f.Scene == null || D.Read(f.Scene, "LevelID") as string != "PointRace" ||
                f.Panel.transform.root != f.Scene.transform || f.Chest == null || f.Chest.gameObject != f.Panel.gameObject ||
                !ReferenceEquals(D.Read(f.Chest, "rmg"), f.Scene))) throw new ProbeFailure("point_chest_colocated_scene_required");
            f.ReturnRoot = f.Panel == null ? null : D.Method(f.Loader.GetType(), "GetOldMainUI", Type.EmptyTypes, false, Model.FirstType("RootMessenger")).Invoke(f.Loader, null) as Component;
            f.ReturnClose = f.Panel == null ? null : D.Read(f.Panel, "OnClose");
            f.InScene = !f.Battling && f.Scene != null && ReferenceEquals(f.Current, f.Scene) && f.Scene.gameObject.activeInHierarchy &&
                ReferenceEquals(D.Read(context.GlobalUi, "level_rmg"), f.Scene);
            f.AtHome = !f.Battling && ReferenceEquals(f.Current, f.Home) && f.Home.gameObject.activeInHierarchy;
            f.Main = f.AtHome ? f.Home.GetComponentInChildren(Model.GameType("MainUI")) : null;
            f.Reward = D.Static(Model.FirstType("__BaseUI"), "battle_result_ui") as Component;
            if (f.Reward != null)
            {
                f.RewardShown = (DateTime)D.Read(f.Reward, "visible_time"); f.RewardClose = D.Read(f.Reward, "OnClose") as Delegate;
                f.RewardItems = D.Read(f.Reward, "m_vctBonuses"); f.RewardPrevious = D.Read(f.Reward, "m_prevUI") as Component;
                f.OrdinaryReward = f.Reward.GetType() == Model.FirstType("BattleResultUI") && f.RewardItems is IList &&
                    !D.Bool(D.Read(f.Reward, "m_bCampaign")) && !D.Bool(D.Read(f.Reward, "m_bBattleRank")) &&
                    D.Int(D.Read(f.Reward, "m_iTreasureCount")) == 0 && (string)D.Read(f.Reward, "mine_ui") == "" &&
                    D.Read(f.Reward, "m_clit") == null && !D.Bool(D.Read(f.Reward, "autoClose"));
            }
            return f;
        }
        private static string RowKey(object row)
        { return D.UInt(D.Read(row, "time")).ToString(CultureInfo.InvariantCulture) + ":" + D.UInt(D.Read(row, "rank")).ToString(CultureInfo.InvariantCulture) + ";"; }
        private static bool ContainsTime(IList rows, uint time)
        { foreach (object row in rows) if (D.UInt(D.Read(row, "time")) == time) return true; return false; }
        private static bool IsHome(Component c)
        { return c != null && c.GetType() == Model.FirstType("RootMessenger") && c.transform.root == c.transform &&
            (D.Read(c, "LevelID") as string == "Capital" || D.Read(c, "LevelID") as string == "Campaign"); }
        private static bool Visible(Component c) { return c != null && D.Visible(c); }
        private static bool Ready(Component c) { return Visible(c) && D.Enabled(c) && !D.Loading(c); }
        private void Publish()
        {
            // The clock remains visible in Json(), but an elapsed second does
            // not change an otherwise identical offer. Compare the actual
            // frame as well as public state: SameOffer binds clock-derived
            // day/cycle/free boundary, transport, roots, scene, rows and costs.
            string value = View(false) + Operation();
            bool sameFrame = !available ? revisionFrame == null : frame != null && revisionFrame != null && frame.SameOffer(revisionFrame);
            if (value != stamp || !sameFrame) { stamp = value; revision++; }
            revisionFrame = available ? frame : null;
        }
        private string View(bool includeClock = true)
        {
            bool seen = available && frame != null;
            return "{\"task_id\":310,\"mode\":\"pointrace_chest\",\"role_id\":" + D.Q(seen ? frame.RoleId.ToString(CultureInfo.InvariantCulture) : "") +
                ",\"observation_available\":" + D.B(seen) + ",\"home_ready\":" + D.B(HomeReady()) + ",\"in_scene\":" + D.B(seen && frame.InScene) +
                ",\"chest_open\":" + D.B(seen && Visible(frame.Chest)) + ",\"eligible_count\":" + (seen ? frame.Eligible : -1) +
                ",\"historical_count\":" + (seen ? frame.Historical : -1) + ",\"eligible_index\":" + (seen ? frame.Index : -1) +
                ",\"eligible_time\":" + (seen ? frame.Time : 0) + ",\"bonus_count\":" + (seen ? frame.Count : -1) +
                ",\"point_got\":" + D.B(seen && frame.Got) + (includeClock ? ",\"server_now\":" + (seen ? frame.Now : 0) : "") +
                ",\"refresh_cycle\":" + (seen ? frame.Cycle : 0) + ",\"next_action\":" + D.Q(next) + ",\"gate\":" + D.Q(reason) + "}";
        }
        private string Operation()
        {
            return "{\"id\":" + D.Q(id) + ",\"task_id\":310,\"sequence\":" + sequence + ",\"status\":" + D.Q(status) + ",\"reason\":" + D.Q(reason) +
                ",\"ui_touched\":" + D.B(touched || closed) + ",\"mutation_attempted\":" + D.B(requests > 0) + ",\"request_count\":" + requests +
                ",\"confirmed_count\":" + confirmed + ",\"entry_request_sequence\":" + entrySequence + ",\"entry_callback_returned\":" + D.B(entryReturned) +
                ",\"entry_listener_released\":" + D.B(entryReleased) + ",\"callback_returned\":" + D.B(callbackReturned) + ",\"state_confirmed\":" + D.B(stateConfirmed) +
                ",\"request_sequence\":" + requestSequence + ",\"requested_time\":" + requestedTime + ",\"requested_index\":" + requestedIndex +
                ",\"receipt_result\":" + Nullable(receiptResult) + ",\"receipt_time\":" + Nullable(receiptTime) + ",\"receipt_gold\":" + Nullable(receiptGold) +
                ",\"before_bonus_count\":" + beforeCount + ",\"after_bonus_count\":" + afterCount + ",\"closed\":" + D.B(closed) + ",\"busy\":" + D.B(Busy) +
                ",\"receipts\":" + ReceiptsJson() +
                ",\"cleanup_pending\":" + D.B(touched && !closed || claimPending || entryTap != null || claimTap != null) + ",\"release_verified\":" + D.B(ReleaseVerified) +
                ",\"chest_opened\":" + D.B(chestOpened) + ",\"chest_closed\":" + D.B(chestClosed) + ",\"round_pending\":" + D.B(claimPending) +
                ",\"popup_handoff_ready\":" + D.B(PopupHandoffReady) + ",\"reward_popup_owned\":" + D.B(RewardOwned()) +
                ",\"finish_requested\":" + D.B(finishRequested) +
                ",\"reward_close_attempted\":" + D.B(rewardCloseAttempted) + ",\"reward_closed\":" + D.B(rewardClosed) +
                ",\"reward_close_request_sequence\":" + rewardCloseRequestSequence + ",\"reward_close_command_sequence\":" + rewardCloseCommandSequence +
                ",\"reward_closures\":" + RewardClosuresJson() +
                ",\"response_provenance\":\"request_handler_operation_sequence\"}";
        }
        private static string Nullable(uint? n) { return n.HasValue ? n.Value.ToString(CultureInfo.InvariantCulture) : "null"; }
        private bool RewardOwned()
        { return frame != null && frame.Reward != null && OwnsRewardPopup(frame.Reward, D.Read(frame.Reward, "OnClose") as Delegate); }
        private string ReceiptsJson()
        { string value = "["; foreach (ClaimReceipt r in receipts) { if (value.Length > 1) value += ","; value += r.Json(); } return value + "]"; }
        private string RewardClosuresJson()
        { string value = "["; foreach (RewardClosure r in rewardClosures) { if (value.Length > 1) value += ","; value += r.Json(); } return value + "]"; }
        internal string Json() { return "{\"schema\":1,\"revision\":" + revision + ",\"view\":" + View() + ",\"operation\":" + Operation() + "}"; }
    }
}
