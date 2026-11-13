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
    // Fixed stock button routes. No packet is synthesized, no paid find-back,
    // new-year tab, purchase, double reward or supplement action is admitted.
    internal sealed class DailySignSalaryModel
    {
        internal const int SignTask = 301, SalaryTask = 302, SalaryOnlyTask = 304, SignPageRewardsTask = 305;
        private sealed class Frame
        {
            internal D Context;
            internal object Character, Client, Sub, ArenaRow, PointRow;
            internal DailyTransport Transport;
            internal Component Panel, Main, Sign, Salary, Reputation, Arena, Point, Reward;
            internal ulong Role;
            internal uint Cycle, Now, Day, ArenaTime, PointTime, Checkins, GoldConsumed;
            internal int Today, SalaryTime, ArenaIndex, PointIndex, ReputationLevel, Gold, Bound;
            internal bool Home, Loading, NetworkBusy, Ready, SalaryOpen, ArenaOpen, CheckinObserved, PremiumObserved;
            internal bool SignSalaryOpen, PointGot, PageRewards;
            internal string Error = "", CheckinError = "";
            internal bool SameSignDay(Frame f)
            {
                return f != null && CheckinObserved && f.CheckinObserved && Role != 0 && Role == f.Role &&
                    ReferenceEquals(Character, f.Character) && Day == f.Day && Cycle == f.Cycle && Now >= f.Now;
            }
            internal bool SameOwner(Frame f)
            {
                // Compare the current frame to an earlier baseline: the
                // server clock may advance while ownership stays unchanged.
                return f != null && Role != 0 && Role == f.Role && Cycle == f.Cycle && Day == f.Day && Now >= f.Now &&
                    ReferenceEquals(Character, f.Character) && ReferenceEquals(Client, f.Client) &&
                    Transport != null && Transport.Same(f.Transport) && Context.SameOwnership(f.Context) &&
                    ReferenceEquals(Main, f.Main) && ReferenceEquals(Sign, f.Sign) && ReferenceEquals(Salary, f.Salary) &&
                    SameReceiver(Reputation, f.Reputation, f) && SameReceiver(Arena, f.Arena, f) && SameReceiver(Point, f.Point, f);
            }
            private bool SameReceiver(Component current, Component before, Frame f)
            {
                // A receiver absent at page adoption may be instantiated by
                // normal loading. Read() still binds any new instance to the
                // exact RootUI. An existing receiver may never be replaced.
                return ReferenceEquals(current, before) || PageRewards && f.PageRewards && ReferenceEquals(before, null);
            }
            internal bool SameOffer(Frame f)
            {
                return SameOwner(f) && Today == f.Today && Checkins == f.Checkins && SalaryTime == f.SalaryTime &&
                    ArenaIndex == f.ArenaIndex && ArenaTime == f.ArenaTime && ReferenceEquals(ArenaRow, f.ArenaRow) &&
                    PointIndex == f.PointIndex && PointTime == f.PointTime && ReferenceEquals(PointRow, f.PointRow) &&
                    Gold == f.Gold && Bound == f.Bound && GoldConsumed == f.GoldConsumed &&
                    SalaryOpen == f.SalaryOpen && ArenaOpen == f.ArenaOpen && ReputationLevel == f.ReputationLevel &&
                    SignSalaryOpen == f.SignSalaryOpen && PointGot == f.PointGot &&
                    ReferenceEquals(Reputation, f.Reputation) && ReferenceEquals(Arena, f.Arena) && ReferenceEquals(Point, f.Point);
            }
        }
        private sealed class Intent
        {
            internal string Action, Id;
            internal Frame Baseline;
            internal int Tick;
        }

        private sealed class PageReceipt
        {
            internal ushort Opcode;
            internal uint RequestTime;
            internal uint? Result, Time, GoldConsume;
            internal bool CallbackReturned, StateConfirmed;
        }
        private sealed class PageItem
        {
            internal string Key, Name, Status = "UNKNOWN", Reason = "sign_page_rewards_not_observed", Availability = "UNKNOWN";
            internal string InitialAvailability = "";
            internal readonly List<PageReceipt> Receipts = new List<PageReceipt>();
            internal bool Confirmed
            {
                get { foreach (PageReceipt receipt in Receipts) if (receipt.StateConfirmed) return true; return false; }
            }
        }
        private PageItem[] pageItems = NewPageItems();
        private PageItem pageItem;
        private PageReceipt pageReceipt;
        private Component ownedPageReputation, ownedPageArena, ownedPagePoint;
        private static PageItem[] NewPageItems()
        {
            return new[] { new PageItem { Key = "salary", Name = "俸禄" },
                new PageItem { Key = "arena_reward", Name = "竞技奖励" },
                new PageItem { Key = "point_reward", Name = "积分赛奖励" } };
        }

        private Model model;
        private Frame frame, origin, sent;
        private Intent queued;
        private DailyDivinityReplyTap tap;
        private string status = "IDLE", reason = "not_observed", nextAction = "", operationId = "", stamp;
        private string terminalStatus = "", terminalReason = "";
        private int taskId, sequence, stageTick, requests;
        private ushort opcode;
        private bool blocked, stopped, holding, attempted, returned, confirmed, closing, closed, unknown;
        internal int Revision { get; private set; }
        internal bool Busy { get { return queued != null || holding || tap != null; } }
        internal bool MutationAttempted { get { return attempted; } }
        internal bool GuiInvocationAttempted { get; private set; }
        internal bool PopupHandoffReady
        {
            get
            {
                return holding && queued == null && tap == null && !stopped && !unknown && frame != null &&
                    frame.Error == "" && !frame.Loading && !frame.NetworkBusy &&
                    (taskId == SignTask ? frame.SameSignDay(origin) && frame.Today == 1 :
                        frame.SameOwner(origin) && returned && confirmed);
            }
        }

        internal bool OwnsRewardPopup(Component popup, Delegate close)
        {
            if (!PopupHandoffReady || popup == null || close == null || close.GetInvocationList().Length != 1 ||
                !ReferenceEquals(popup, frame.Reward) ||
                !ReferenceEquals(popup, D.Static(Model.FirstType("__BaseUI"), "battle_result_ui")) ||
                !ReferenceEquals(close, D.Read(popup, "OnClose"))) return false;
            Component owner = taskId == SignTask ? frame.Sign : opcode == 819 ? frame.Reputation :
                taskId == SignPageRewardsTask ? frame.Sign : frame.Salary;
            string method = taskId == SignTask || opcode == 819 ? "EnableAll" :
                taskId == SignPageRewardsTask ? "closeBonus" : "closeArenaBonus";
            return ReferenceEquals(close.Target, owner) &&
                close.Method == D.Method(owner.GetType(), method, Type.EmptyTypes, false, typeof(void));
        }

        internal void Capture(Model currentModel, int task, bool admissionBlocked)
        {
            model = currentModel; blocked = admissionBlocked;
            if (task != SignTask && task != SalaryTask && task != SalaryOnlyTask && task != SignPageRewardsTask) throw new ArgumentOutOfRangeException("task");
            if (Busy && taskId != task) return;
            if (!Busy && taskId != task)
            {
                terminalStatus = terminalReason = operationId = "";
                closed = attempted = returned = confirmed = unknown = false;
            }
            taskId = task;
            try
            {
                frame = Read(); nextAction = "";
                if (taskId == SignTask) { CaptureSign(); return; }
                if (taskId == SignPageRewardsTask) { CapturePageRewards(); return; }
                if (frame.Error != "") { Set(holding ? "UNKNOWN" : "BLOCKED", frame.Error); return; }
                if (holding && !frame.SameOwner(origin)) { Unknown("sign_salary_owner_or_game_day_changed"); return; }
                if (holding && (frame.Gold < origin.Gold || frame.Bound < origin.Bound || frame.GoldConsumed != origin.GoldConsumed))
                { Unknown("sign_salary_premium_spend_observed"); return; }
                if (unknown) { Set("UNKNOWN", reason); return; }
                if (tap != null)
                {
                    if (tap.Error != null) { Unknown(tap.Error); return; }
                    if (!tap.Returned)
                    {
                        if (!tap.Attached || Elapsed() > 20000) Unknown("sign_salary_reply_unverified");
                        else Set("PENDING", "sign_salary_stock_reply_pending");
                        return;
                    }
                    tap.StopObserving();
                    if (!tap.TryRestore()) { Set("PENDING", "sign_salary_callback_release_pending"); return; }
                    tap = null;
                }
                if (stopped) { Set("STOPPED", "user_stop"); return; }
                // Consume and release the salary callback before handing its
                // matching reward to shared popup processing.
                if (holding && frame.Loading)
                { Set("PENDING", "sign_salary_stock_loading_or_network"); return; }
                Component ownReward = frame.Reward != null &&
                    OwnsRewardPopup(frame.Reward, D.Read(frame.Reward, "OnClose") as Delegate) ? frame.Reward : null;
                string panels = frame.Context.RewardPanels(frame.Sign, frame.Salary, ownReward);
                if (panels != "PASS") { Set(holding ? "UNKNOWN" : "BLOCKED", panels); return; }
                if (!holding)
                {
                    if (terminalStatus != "" && closed)
                    {
                        if (Eligibility(frame) == "") nextAction = frame.Home ? "open" :
                            ReferenceEquals(frame.Sub, frame.Panel) && frame.Ready ? "resume" : "";
                        Set(terminalStatus, terminalReason); return;
                    }
                    string unavailable = Eligibility(frame);
                    if (unavailable != "") { Set(unavailable, "sign_salary_" + unavailable.ToLowerInvariant()); return; }
                    if (frame.Home) Offer("IDLE", "open");
                    else if (ReferenceEquals(frame.Sub, frame.Panel) && frame.Ready) Offer("IDLE", "resume");
                    else Set("BLOCKED", "sign_salary_home_or_own_entry_required");
                    return;
                }
                if (frame.Loading || frame.NetworkBusy) { Set("PENDING", "sign_salary_stock_loading_or_network"); return; }
                if (ownReward != null && (D.Visible(ownReward) || D.Loading(ownReward)))
                {
                    Set("PENDING", "sign_salary_shared_reward_confirmation_pending"); return;
                }
                if (closing)
                {
                    if (frame.Home && (frame.Reward == null || !D.Visible(frame.Reward) && !D.Loading(frame.Reward)))
                    {
                        holding = false; closed = true;
                        Set(terminalStatus == "" ? "COMPLETED" : terminalStatus,
                            terminalReason == "" ? "sign_salary_free_rewards_claimed" : terminalReason);
                    }
                    else if (ReferenceEquals(frame.Sub, frame.Panel) && frame.Ready) Offer("CLOSING", "close");
                    else if (Elapsed() > 20000) Unknown("sign_salary_cleanup_unverified");
                    else Set("CLOSING", "sign_salary_waiting_home");
                    return;
                }
                string eligibility = Eligibility(frame);
                if (attempted && returned && confirmed && (taskId == SignTask || eligibility != ""))
                { terminalStatus = "COMPLETED"; terminalReason = "sign_salary_free_rewards_claimed"; closing = true; }
                else if (eligibility != "")
                { terminalStatus = eligibility; terminalReason = "sign_salary_" + eligibility.ToLowerInvariant(); closing = true; }
                if (closing)
                {
                    if (frame.Home) { holding = false; closed = true; Set(terminalStatus, terminalReason); }
                    else if (ReferenceEquals(frame.Sub, frame.Panel) && frame.Ready) Offer("CLOSING", "close");
                    else Set("PENDING", "sign_salary_waiting_entry");
                }
                else if (ReferenceEquals(frame.Sub, frame.Panel) && frame.Ready)
                {
                    if (requests >= 16) { Unknown("sign_salary_request_bound"); return; }
                    Offer("READY", taskId == SignTask ? "claim_signin" : taskId == SalaryOnlyTask ? "claim_salary" :
                        frame.SalaryTime == 0 && frame.SalaryOpen && frame.ReputationLevel > 0 ?
                            frame.ArenaIndex >= 0 && frame.ArenaOpen ? "claim_all_salary" : "claim_salary" : "claim_arena");
                }
                else if (Elapsed() > 20000) Unknown("sign_salary_entry_timeout");
                else Set("PENDING", "sign_salary_entry_loading");
            }
            catch (Exception error)
            { if (holding && attempted) Unknown(error.Message); else Set(holding ? "UNKNOWN" : "BLOCKED", error.Message); }
        }

        private void CaptureSign()
        {
            if (stopped) { Set("STOPPED", "user_stop"); return; }
            if (!frame.CheckinObserved) { Set("UNKNOWN", frame.CheckinError); return; }
            if (holding && !frame.SameSignDay(origin)) { Unknown("sign_salary_owner_or_game_day_changed"); return; }
            if (holding && origin.PremiumObserved && frame.PremiumObserved &&
                (frame.Gold < origin.Gold || frame.Bound < origin.Bound || frame.GoldConsumed != origin.GoldConsumed))
            { Unknown("sign_salary_premium_spend_observed"); return; }

            // The stock callback owns decoding and account mutation. Its
            // current role/day projection decides sign-in, even when this
            // helper did not observe the 916 callback or its reward window.
            if (frame.Today == 1)
            {
                unknown = false;
                if (!holding) { Set("ALREADY_CLAIMED", "sign_salary_current_day_already_signed"); return; }
                terminalStatus = attempted ? "COMPLETED" : "ALREADY_CLAIMED";
                terminalReason = "sign_salary_checkin_state_confirmed";
                closing = true;
                if (frame.Error != "") { Set(terminalStatus, terminalReason); return; }
                bool rewardBusy = frame.Reward != null && (D.Visible(frame.Reward) || D.Loading(frame.Reward));
                string panels = frame.Context.RewardPanels(frame.Sign, frame.Salary, null);
                if (!frame.Loading && !frame.NetworkBusy && !rewardBusy && panels == "PASS")
                {
                    if (frame.Home) { holding = false; closed = true; Set(terminalStatus, terminalReason); return; }
                    if (ReferenceEquals(frame.Sub, frame.Sign) && frame.Ready) { Offer("CLOSING", "close"); return; }
                }
                // Shared popup processing owns reward confirmation. Business
                // confirmation is published separately from actual cleanup.
                Set(terminalStatus, terminalReason); return;
            }

            if (attempted && holding)
            {
                if (Elapsed() > 20000) Unknown("sign_salary_checkin_state_unconfirmed");
                else Set("PENDING", "sign_salary_checkin_state_pending");
                return;
            }
            if (unknown && holding) { Set("UNKNOWN", reason); return; }
            if (frame.Error != "") { Set(holding ? "UNKNOWN" : "BLOCKED", frame.Error); return; }
            string admission = frame.Context.RewardPanels(frame.Sign, frame.Salary, null);
            if (admission != "PASS") { Set(holding ? "PENDING" : "BLOCKED", admission); return; }
            if (frame.Loading || frame.NetworkBusy) { Set(holding ? "PENDING" : "BLOCKED", "sign_salary_stock_loading_or_network"); return; }
            if (!holding)
            {
                // A prior outcome is not an eligibility decision for a new
                // operation. Only an unresolved live operation stays held.
                unknown = false;
                if (frame.Home) Offer("IDLE", "open");
                else if (ReferenceEquals(frame.Sub, frame.Sign) && frame.Ready) Offer("IDLE", "resume");
                else Set("BLOCKED", "sign_salary_home_or_own_entry_required");
            }
            else if (ReferenceEquals(frame.Sub, frame.Sign) && frame.Ready) Offer("READY", "claim_signin");
            else Set("PENDING", "sign_salary_entry_loading");
        }

        private string PageAvailability(Frame f, int index)
        {
            // These are SignInUI.drawAllBonus's branches, including its
            // distinct disabled and already-claimed point-race appearances.
            if (index == 0) return f.SalaryTime != 0 ? "ALREADY_CLAIMED" :
                f.SignSalaryOpen ? "AVAILABLE" : "CONDITION_NOT_MET";
            if (index == 1) return !f.ArenaOpen ? "CONDITION_NOT_MET" :
                f.ArenaIndex < 0 ? "NO_FREE_REWARD" : "AVAILABLE";
            if (f.PointIndex < 0) return f.PointGot ? "ALREADY_CLAIMED" : "NO_FREE_REWARD";
            return f.ArenaOpen ? "AVAILABLE" : "CONDITION_NOT_MET";
        }

        private static Component PageReceiver(Frame f, int index)
        { return index == 0 ? f.Reputation : index == 1 ? f.Arena : f.Point; }

        private static bool BindPageReceiver(ref Component owned, Component current)
        {
            if (ReferenceEquals(owned, null)) { owned = current; return true; }
            return ReferenceEquals(owned, current);
        }

        private void UpdatePageItems()
        {
            if (frame == null || frame.Error != "") return;
            for (int i = 0; i < pageItems.Length; i++)
            {
                PageItem item = pageItems[i]; item.Availability = PageAvailability(frame, i);
                if (item.InitialAvailability == "") item.InitialAvailability = item.Availability;
                if (ReferenceEquals(item, pageItem) && pageReceipt != null && !pageReceipt.StateConfirmed)
                {
                    if (unknown) { item.Status = "UNKNOWN"; item.Reason = reason; }
                    else { item.Status = "PENDING"; item.Reason = "sign_page_rewards_stock_reply_pending"; }
                }
                else if (item.Confirmed)
                { item.Status = "COMPLETED"; item.Reason = "sign_page_rewards_receipt_and_state_confirmed"; }
                else if (item.Availability == "AVAILABLE" && PageReceiver(frame, i) == null)
                { item.Status = "UNAVAILABLE"; item.Reason = "sign_page_rewards_receiver_unavailable:" + item.Key; }
                else
                {
                    item.Status = item.Availability;
                    item.Reason = "sign_page_rewards_" + item.Key + "_" + item.Availability.ToLowerInvariant();
                }
            }
        }

        private void CapturePageRewards()
        {
            if (frame.Error != "") { if (holding) Unknown(frame.Error); else Set("BLOCKED", frame.Error); return; }
            if (!frame.CheckinObserved) { if (holding) Unknown(frame.CheckinError); else Set("BLOCKED", frame.CheckinError); return; }
            if (holding && !frame.SameOwner(origin)) { Unknown("sign_page_rewards_owner_or_game_day_changed"); return; }
            if (holding && (!BindPageReceiver(ref ownedPageReputation, frame.Reputation) ||
                !BindPageReceiver(ref ownedPageArena, frame.Arena) || !BindPageReceiver(ref ownedPagePoint, frame.Point)))
            { Unknown("sign_page_rewards_receiver_identity_changed"); return; }
            if (holding && (!frame.PremiumObserved || frame.Gold < origin.Gold || frame.Bound < origin.Bound || frame.GoldConsumed != origin.GoldConsumed))
            { Unknown("sign_salary_premium_spend_observed"); return; }
            UpdatePageItems();
            if (unknown) { Set("UNKNOWN", reason); return; }
            if (tap != null)
            {
                if (tap.Error != null) { Unknown(tap.Error); return; }
                if (!tap.Returned)
                {
                    if (!tap.Attached || Elapsed() > 20000) Unknown("sign_page_rewards_reply_unverified");
                    else Set("PENDING", "sign_page_rewards_stock_reply_pending");
                    return;
                }
                tap.StopObserving();
                if (!tap.TryRestore()) { Set("PENDING", "sign_page_rewards_callback_release_pending"); return; }
                tap = null;
            }
            if (stopped) { Set("STOPPED", "user_stop"); return; }
            if (frame.Loading || frame.NetworkBusy)
            { Set(holding ? "PENDING" : "BLOCKED", "sign_page_rewards_stock_loading_or_network"); return; }
            Component ownReward = frame.Reward != null && OwnsRewardPopup(frame.Reward, D.Read(frame.Reward, "OnClose") as Delegate) ? frame.Reward : null;
            string panels = frame.Context.RewardPanels(frame.Sign, ownReward);
            if (panels != "PASS") { if (holding) Unknown(panels); else Set("BLOCKED", panels); return; }
            if (!holding)
            {
                // An observed open sign-in page, including today's signed
                // page, may be explicitly adopted without issuing sign-in.
                if (frame.Home) nextAction = "open";
                else if (ReferenceEquals(frame.Sub, frame.Sign) && frame.Ready) nextAction = "resume";
                if (terminalStatus != "" && closed) { Set(terminalStatus, terminalReason); return; }
                if (nextAction != "") Set("IDLE", "");
                else Set("BLOCKED", "sign_page_rewards_home_or_signin_required");
                return;
            }
            if (ownReward != null && (D.Visible(ownReward) || D.Loading(ownReward)))
            { Set("PENDING", "sign_page_rewards_shared_reward_confirmation_pending"); return; }
            if (closing)
            {
                if (frame.Home && (frame.Reward == null || !D.Visible(frame.Reward) && !D.Loading(frame.Reward)))
                { holding = false; closed = true; Set(terminalStatus, terminalReason); }
                else if (ReferenceEquals(frame.Sub, frame.Sign) && frame.Ready) Offer("CLOSING", "close");
                else if (Elapsed() > 20000) Unknown("sign_page_rewards_cleanup_unverified");
                else Set("CLOSING", "sign_page_rewards_waiting_home");
                return;
            }
            if (!ReferenceEquals(frame.Sub, frame.Sign) || !frame.Ready)
            {
                if (Elapsed() > 20000) Unknown("sign_page_rewards_entry_timeout");
                else Set("PENDING", "sign_page_rewards_entry_loading");
                return;
            }
            int selected = -1; bool receiverUnavailable = false;
            for (int i = 0; i < pageItems.Length; i++)
            {
                if (PageAvailability(frame, i) != "AVAILABLE") continue;
                if (PageReceiver(frame, i) == null) { receiverUnavailable = true; continue; }
                selected = i; break;
            }
            if (selected >= 0)
            {
                if (requests >= 16) { Unknown("sign_page_rewards_request_bound"); return; }
                Offer("READY", selected == 0 ? "claim_salary" : selected == 1 ? "claim_arena" : "claim_point");
                return;
            }
            terminalStatus = receiverUnavailable ? "UNAVAILABLE" : attempted ? "COMPLETED" : "NO_FREE_REWARD";
            terminalReason = receiverUnavailable ? "sign_page_rewards_receiver_unavailable" :
                attempted ? "sign_page_rewards_free_rewards_claimed" : "sign_page_rewards_no_claimable_button";
            closing = true; Offer("CLOSING", "close");
        }

        private string Eligibility(Frame f)
        {
            if (taskId == SignTask) return f.Today == 1 ? "ALREADY_CLAIMED" : f.Today == 0 ? "" : "UNKNOWN";
            if (taskId == SignPageRewardsTask)
            {
                for (int i = 0; i < pageItems.Length; i++) if (PageAvailability(f, i) == "AVAILABLE") return "";
                return "NO_FREE_REWARD";
            }
            if (taskId == SalaryOnlyTask)
                return f.SalaryTime != 0 ? "ALREADY_CLAIMED" : f.SalaryOpen && f.ReputationLevel > 0 ? "" : "CONDITION_NOT_MET";
            if (f.ArenaIndex >= 0 && f.ArenaOpen) return "";
            if (f.SalaryTime == 0 && f.SalaryOpen && f.ReputationLevel > 0) return "";
            if (f.SalaryTime == 0 && (!f.SalaryOpen || f.ReputationLevel <= 0)) return "CONDITION_NOT_MET";
            return f.ArenaIndex < 0 ? "NO_FREE_REWARD" : "CONDITION_NOT_MET";
        }

        internal bool Queue(int task, ulong role, string id, int seq, int revision, string action, out string why)
        {
            why = "sign_salary_command_unavailable";
            bool start = action == "open" || action == "resume";
            // A new operation re-reads present game state. Prior local completion
            // is never evidence that today's game reward was already collected.
            if (stopped || blocked || frame == null || frame.Error != "" || task != taskId || role == 0 || role != frame.Role ||
                queued != null || seq <= sequence || revision != Revision || !Command.ValidNonce(id) || action == "" || action != nextAction ||
                (start ? Busy : !holding || id != operationId)) return false;
            queued = new Intent { Action = action, Id = id, Baseline = frame, Tick = Environment.TickCount };
            sequence = seq; GuiInvocationAttempted = false; Revision++; why = ""; return true;
        }

        internal bool ExecuteGui(DevelopmentPublication publish, DailyMonsterInvocationGate begin, out string why)
        {
            why = "sign_salary_not_queued";
            Intent intent = queued;
            if (intent == null) return false;
            object client = intent.Baseline.Client;
            if (client == null || !Monitor.TryEnter(client, 0)) { NotInvoked("client_busy"); why = "client_busy"; return false; }
            try
            {
                publish(); Capture(model, taskId, blocked);
                if (stopped || blocked || !D.Gui || !frame.SameOffer(intent.Baseline) || intent.Action != nextAction ||
                    unchecked((uint)(Environment.TickCount - intent.Tick)) > 1500 || !begin())
                { why = "sign_salary_gui_admission_changed"; NotInvoked(why); return false; }
                queued = null; stageTick = Environment.TickCount;
                string action = intent.Action;
                if (action == "open" || action == "resume")
                {
                    origin = frame; holding = true; operationId = intent.Id; attempted = returned = confirmed = closed = closing = unknown = false;
                    terminalStatus = terminalReason = ""; requests = 0;
                    if (taskId == SignPageRewardsTask)
                    {
                        pageItems = NewPageItems(); pageItem = null; pageReceipt = null;
                        ownedPageReputation = frame.Reputation; ownedPageArena = frame.Arena; ownedPagePoint = frame.Point;
                        UpdatePageItems();
                    }
                    if (action == "open") { GuiInvocationAttempted = true; D.Call(frame.Main, taskId == SignTask || taskId == SignPageRewardsTask ? "_signin" : "_salary"); }
                }
                else if (action == "close")
                {
                    GuiInvocationAttempted = true; D.Call(frame.Panel, "Hide");
                    D.Method(Model.GameType("MainUI"), "SetSubUI", new[] { Model.FirstType("__BaseUI") }, true, typeof(void)).Invoke(null, new object[] { null });
                    closing = true;
                }
                else Claim(action);
                nextAction = ""; Set("PENDING", "sign_salary_stock_action_invoked"); why = ""; return true;
            }
            catch (Exception error) { queued = null; Unknown(error.Message); why = reason; return false; }
            finally { Monitor.Exit(client); }
        }

        private void Claim(string action)
        {
            if (taskId == SignPageRewardsTask) { ClaimPageReward(action); return; }
            if (taskId == SalaryOnlyTask && action != "claim_salary")
                throw new ProbeFailure("sign_salary_action_out_of_scope");
            if (frame.NetworkBusy || frame.Reward != null && (D.Visible(frame.Reward) || D.Loading(frame.Reward)))
                throw new ProbeFailure("sign_salary_request_or_reward_busy");
            Component receiver;
            string method, callback;
            if (action == "claim_signin") { receiver = frame.Sign; opcode = 916; method = "cs_all_bonus_get_new"; callback = "sc_all_bonus_get_new"; }
            else if (action == "claim_all_salary") { receiver = frame.Salary; opcode = 840; method = "cs_get_all_bonus_0x348"; callback = "sc_get_all_bonus_0x348"; }
            else if (action == "claim_salary") { receiver = frame.Reputation; opcode = 819; method = "cs_reputation_salary"; callback = "sc_reputation_salary"; }
            else if (action == "claim_arena") { receiver = frame.Arena; opcode = 1122; method = "getArenaBonus"; callback = "sc_arena_bonusget_0x462"; }
            else throw new ProbeFailure("sign_salary_unknown_claim");
            if (Handler(frame.Client, opcode) != null) throw new ProbeFailure("sign_salary_handler_busy");
            if (action == "claim_signin" && (attempted || requests != 0 || !frame.CheckinObserved || frame.Today != 0))
                throw new ProbeFailure("sign_salary_checkin_already_attempted_or_changed");
            sent = frame; attempted = true; returned = confirmed = false; requests++; GuiInvocationAttempted = true;
            // AllBonusUI.getArenaBonus fixes bFindBack:false and the game's own
            // checkArenaToday index; never call ArenaUI with a supplied index.
            D.Call(action == "claim_arena" ? frame.Salary : receiver, method);
            // Do not replace or depend on the normal sign-in callback. A
            // subsequent same-role/day checkin_today observation decides it.
            if (action == "claim_signin") return;
            object handler = Handler(sent.Client, opcode);
            Delegate stock = handler == null ? null : D.Read(handler, "handler") as Delegate;
            if (handler == null || stock == null || stock.GetInvocationList().Length != 1 || !ReferenceEquals(stock.Target, receiver) ||
                stock.Method != D.Method(receiver.GetType(), callback, new[] { typeof(MemoryStream), typeof(object) }, false, typeof(void)))
                throw new ProbeFailure("sign_salary_stock_registration_unverified");
            object argument = D.Read(handler, "arg");
            tap = DailyDivinityReplyTap.Attach(sent.Client, false, handler, handler.GetType().GetField("handler", D.Instance), argument,
                delegate { return Handler(sent.Client, opcode); },
                CallbackOwner, Receipt);
            if (tap == null || !tap.Attached) throw new ProbeFailure("sign_salary_callback_unobserved");
        }

        private bool CallbackOwner()
        {
            // A natural callback may run while the stock UI disables input or
            // starts loading its result. Bind the account/transport/root/day,
            // independently of visibility and of our not-yet-bound reward.
            if (sent == null || !ReferenceEquals(sent.Character, D.Static(Model.FirstType("Global"), "character_")) ||
                !ReferenceEquals(sent.Client, DailyTransport.CurrentClient(sent.Character)) || !sent.Transport.StillCurrent() ||
                !ReferenceEquals(model.CurrentRoot(), sent.Context.Root) || sent.Context.OwnershipGate() != "PASS") return false;
            uint now, day, cycle;
            return DailyRefreshGate.ReadClock(sent.Character, out now, out day, out cycle) == "" &&
                now >= sent.Now && day == sent.Day && cycle == sent.Cycle;
        }

        private void ClaimPageReward(string action)
        {
            int index = action == "claim_salary" ? 0 : action == "claim_arena" ? 1 : action == "claim_point" ? 2 : -1;
            if (index < 0 || !ReferenceEquals(frame.Sub, frame.Sign) || !frame.Ready || PageAvailability(frame, index) != "AVAILABLE")
                throw new ProbeFailure("sign_page_rewards_button_not_claimable");
            if (frame.NetworkBusy || frame.Reward != null && (D.Visible(frame.Reward) || D.Loading(frame.Reward)) ||
                pageReceipt != null && !pageReceipt.StateConfirmed)
                throw new ProbeFailure("sign_page_rewards_previous_request_unresolved");
            Component receiver = index == 0 ? frame.Reputation : index == 1 ? frame.Arena : frame.Point;
            if (receiver == null) throw new ProbeFailure("sign_page_rewards_receiver_unavailable");
            string callback = index == 0 ? "sc_reputation_salary" : index == 1 ? "sc_arena_bonusget_0x462" : "sc_pointrace_bonusget_0x466";
            opcode = (ushort)(index == 0 ? 819 : index == 1 ? 1122 : 1126);
            if (Handler(frame.Client, opcode) != null) throw new ProbeFailure("sign_page_rewards_handler_busy");
            sent = frame; pageItem = pageItems[index];
            pageReceipt = new PageReceipt { Opcode = opcode, RequestTime = index == 0 ? 0 : index == 1 ? frame.ArenaTime : frame.PointTime };
            pageItem.Receipts.Add(pageReceipt);
            attempted = true; returned = confirmed = false; requests++; GuiInvocationAttempted = true;
            // Invoke SignInUI's actual button methods. They choose the current
            // game row and hard-code find_back:false; no index or payment flag
            // comes from the bridge command or the Python caller.
            D.Call(frame.Sign, index == 0 ? "getSalary" : index == 1 ? "getArenaBonus" : "getPointRaceBonus");
            object handler = Handler(sent.Client, opcode);
            Delegate stock = handler == null ? null : D.Read(handler, "handler") as Delegate;
            if (handler == null || stock == null || stock.GetInvocationList().Length != 1 || !ReferenceEquals(stock.Target, receiver) ||
                stock.Method != D.Method(receiver.GetType(), callback, new[] { typeof(MemoryStream), typeof(object) }, false, typeof(void)))
                throw new ProbeFailure("sign_page_rewards_stock_registration_unverified");
            object argument = D.Read(handler, "arg");
            Delegate close = argument as Delegate;
            if (index == 0 ? argument != null : close == null || close.GetInvocationList().Length != 1 ||
                !ReferenceEquals(close.Target, frame.Sign) || close.Method != D.Method(frame.Sign.GetType(), "closeBonus", Type.EmptyTypes, false, typeof(void)))
                throw new ProbeFailure("sign_page_rewards_stock_close_callback_unverified");
            tap = DailyDivinityReplyTap.Attach(sent.Client, false, handler, handler.GetType().GetField("handler", D.Instance), argument,
                delegate { return Handler(sent.Client, opcode); }, CallbackOwner, ReceiptPageReward);
            if (tap == null || !tap.Attached) throw new ProbeFailure("sign_page_rewards_callback_unobserved");
            UpdatePageItems();
        }

        private static uint PacketNumber(object packet, string name)
        {
            PropertyInfo property = packet.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property == null || !property.CanRead || property.GetIndexParameters().Length != 0)
                throw new ProbeFailure("sign_page_rewards_receipt_schema:" + name);
            return Convert.ToUInt32(property.GetValue(packet, null), CultureInfo.InvariantCulture);
        }

        private void ReceiptPageReward(byte[] bytes)
        {
            if (pageItem == null || pageReceipt == null || pageReceipt.Opcode != opcode)
                throw new ProbeFailure("sign_page_rewards_receipt_request_unbound");
            // This observer runs only after the exact original callback has
            // returned. Preserve that fact even if receipt/state matching fails.
            pageReceipt.CallbackReturned = returned = true;
            object packet = BattleReplyPreflight.Deserialize(bytes, opcode == 819 ? "game.net.data.StoC.SCReputationSalary_0x333" :
                opcode == 1122 ? "game.net.data.StoC.SCArenaBonusGet_0x462" : "game.net.data.StoC.SCPointraceBonusGet_0x466");
            uint result = PacketNumber(packet, opcode == 819 ? "operate" : "result");
            uint time = PacketNumber(packet, "time");
            pageReceipt.Result = result; pageReceipt.Time = time;
            pageReceipt.GoldConsume = opcode == 819 ? 0 : PacketNumber(packet, "gold_consume");
            if (pageReceipt.GoldConsume != 0) throw new ProbeFailure("sign_page_rewards_receipt_premium_spend");
            if (result != 0) throw new ProbeFailure("sign_page_rewards_game_rejected:" + result.ToString(CultureInfo.InvariantCulture));
            Frame after = Read();
            if (after.Error != "" || !after.SameOwner(sent)) throw new ProbeFailure("sign_page_rewards_receipt_owner_changed");
            if (!after.PremiumObserved || after.Gold < sent.Gold || after.Bound < sent.Bound || after.GoldConsumed != sent.GoldConsumed)
                throw new ProbeFailure("sign_salary_premium_spend_observed");
            confirmed = opcode == 819 ? time > 0 && time <= Int32.MaxValue && sent.SalaryTime == 0 && after.SalaryTime == (int)time :
                time == pageReceipt.RequestTime && !BonusContains(after.Character, opcode == 1122 ? "arena" : "pointrace", time) &&
                    (opcode != 1126 || after.PointGot);
            pageReceipt.StateConfirmed = confirmed;
            if (!confirmed) throw new ProbeFailure("sign_page_rewards_receipt_state_mismatch");
        }

        private void Receipt(byte[] bytes)
        {
            Dictionary<int, ulong> wire = Scalars(bytes);
            if (Value(wire, 1) != 0) { terminalStatus = "CONDITION_NOT_MET"; terminalReason = "sign_salary_game_rejected"; closing = true; }
            else
            {
                Frame after = Read();
                if (!after.SameOwner(sent)) throw new ProbeFailure("sign_salary_receipt_owner_changed");
                if (opcode == 819)
                    confirmed = Value(wire, 3) > 0 && after.SalaryTime == (int)Value(wire, 3) && sent.SalaryTime == 0;
                else if (opcode == 840)
                    confirmed = Value(wire, 3) > 0 && after.SalaryTime == (int)Value(wire, 3) &&
                        Value(wire, 2) == sent.ArenaTime && !BonusContains(after.Character, "arena", sent.ArenaTime);
                else
                {
                    if (Optional(wire, 5) != 0) throw new ProbeFailure("sign_salary_receipt_premium_spend");
                    confirmed = Value(wire, 2) == sent.ArenaTime && !BonusContains(after.Character, "arena", sent.ArenaTime);
                }
                if (!confirmed) throw new ProbeFailure("sign_salary_receipt_state_mismatch");
            }
            returned = true;
        }

        private Frame Read()
        {
            Frame f = new Frame { Today = -1, PageRewards = taskId == SignPageRewardsTask };
            try
            {
                // Read business state before UI/transport admission. Neither
                // an old helper outcome nor a reward overlay changes today's
                // sign-in flag on the currently loaded character.
                f.Character = D.Static(Model.FirstType("Global"), "character_");
                if (f.Character == null) throw new ProbeFailure("sign_salary_character_unavailable");
                f.Role = Convert.ToUInt64(D.Read(f.Character, "id"), CultureInfo.InvariantCulture);
                if (f.Role == 0) throw new ProbeFailure("sign_salary_character_unavailable");
                string clock = DailyRefreshGate.ReadClock(f.Character, out f.Now, out f.Day, out f.Cycle);
                if (clock != "") throw new ProbeFailure(clock);
                f.Today = D.Int(D.Read(f.Character, "checkin_today"));
                uint now, day, cycle;
                clock = DailyRefreshGate.ReadClock(f.Character, out now, out day, out cycle);
                if (clock != "" || !ReferenceEquals(f.Character, D.Static(Model.FirstType("Global"), "character_")) ||
                    f.Role != Convert.ToUInt64(D.Read(f.Character, "id"), CultureInfo.InvariantCulture) ||
                    now < f.Now || day != f.Day || cycle != f.Cycle)
                    throw new ProbeFailure(clock == "" ? "sign_salary_checkin_state_changed" : clock);
                if (f.Today != 0 && f.Today != 1)
                {
                    f.CheckinError = "sign_salary_checkin_state_unavailable";
                    if (taskId == SignTask) throw new ProbeFailure(f.CheckinError);
                }
                else f.CheckinObserved = true;
                f.Checkins = (uint)D.Read(f.Character, "checkin_times");
                f.Gold = D.Int(D.Read(f.Character, "gold")); f.Bound = D.Int(D.Read(f.Character, "bind_gold"));
                f.GoldConsumed = (uint)D.Read(f.Character, "gold_consume"); f.PremiumObserved = true;
                f.Context = DevelopmentContextReader.DailyWood(model, false);
                if (!ReferenceEquals(f.Context.Character, f.Character))
                { f.CheckinObserved = false; throw new ProbeFailure("sign_salary_character_changed"); }
                f.Client = DailyTransport.CurrentClient(f.Character);
                f.Transport = DailyTransport.Read(f.Client);
                if (f.Character == null || f.Client == null || f.Transport == null || f.Context.OwnershipGate() != "PASS")
                    throw new ProbeFailure("sign_salary_character_connection_unavailable");
                f.SalaryTime = D.Int(D.Read(f.Character, "patrol_last_time")); f.ReputationLevel = D.Int(D.Read(f.Character, "reputation_level"));
                f.ArenaIndex = (int)D.Method(f.Character.GetType(), "checkArenaToday", Type.EmptyTypes, false, typeof(int)).Invoke(f.Character, null);
                f.PointIndex = (int)D.Method(f.Character.GetType(), "checkPointRaceToday", Type.EmptyTypes, false, typeof(int)).Invoke(f.Character, null);
                if (f.ArenaIndex >= 0)
                {
                    IList rows = (IList)D.Read(D.Read(f.Character, "arena"), "bonuses");
                    f.ArenaRow = rows[f.ArenaIndex]; f.ArenaTime = (uint)D.Read(f.ArenaRow, "time");
                }
                if (f.PointIndex >= 0)
                {
                    IList rows = (IList)D.Read(D.Read(f.Character, "pointrace"), "bonuses");
                    f.PointRow = rows[f.PointIndex]; f.PointTime = (uint)D.Read(f.PointRow, "time");
                }
                // MainUI belongs to the current Campaign/Capital scene, while
                // Context.Find binds panels hosted by the global Root/RootUI.
                // Match the arena/point-race models' scene-local lookup.
                f.Main = f.Context.Root.GetComponentInChildren(Model.GameType("MainUI"));
                if (f.Main == null || !ReferenceEquals(D.Read(f.Main, "rmg"), f.Context.Root) ||
                    f.Main.transform.root != f.Context.Root.transform)
                    throw new ProbeFailure("sign_salary_scene_main_ui_unavailable");
                f.Sign = f.Context.Find("SignInUI");
                if (taskId == SignPageRewardsTask)
                {
                    // SignInUI's three button methods use GetComponent and
                    // return without sending when the receiver is absent.
                    // AllBonusUI is not part of this route at all.
                    f.Reputation = FindPageReceiver(f.Context, "ReputationUI");
                    f.Arena = FindPageReceiver(f.Context, "ArenaUI");
                    f.Point = FindPageReceiver(f.Context, "PointraceUI");
                    f.SignSalaryOpen = CheckUi("reputation"); f.PointGot = (bool)D.Read(D.Read(f.Character, "pointrace"), "isgot");
                }
                else
                {
                    f.Salary = f.Context.Find("AllBonusUI");
                    f.Reputation = f.Context.Find("ReputationUI"); f.Arena = f.Context.Find("ArenaUI");
                }
                f.Panel = taskId == SignTask || taskId == SignPageRewardsTask ? f.Sign : f.Salary; f.Sub = f.Context.SubUi;
                f.Reward = f.Context.GrowthRewardDialog();
                f.SalaryOpen = CheckUi("salary") && CheckUi("reputation"); f.ArenaOpen = CheckUi("battle_arena");
                object loader = D.Static(Model.FirstType("BaseAsset"), "lds"); f.Loading = ((Behaviour)loader).enabled;
                f.Home = !f.Loading && f.Sub == null && D.Enabled(f.Main) && f.Context.Panels() == "PASS";
                f.Ready = !f.Loading && D.Enabled(f.Panel) && !D.Loading(f.Panel);
                if ((taskId == SignTask || taskId == SignPageRewardsTask) && ReferenceEquals(f.Sub, f.Sign) && D.Int(D.Read(f.Sign, "page")) != 0)
                    throw new ProbeFailure("sign_salary_normal_signin_tab_required");
                if (taskId != SignTask && taskId != SignPageRewardsTask && f.Ready) f.Ready = (DateTime)D.Read(f.Panel, "m_LoadOverTime") <= DateTime.Now;
                if ((taskId == SignTask || taskId == SignPageRewardsTask) && (bool)D.Read(f.Sign, "sended") ||
                    f.Reputation != null && (bool)D.Read(f.Reputation, "sended")) f.NetworkBusy = true;
                foreach (DictionaryEntry item in (IDictionary)D.Read(f.Client, "m_handlers"))
                    if ((float)D.Read(item.Value, "life") > 0f) { f.NetworkBusy = true; break; }
                // Read is also used by the normal callback observer. Overlay
                // admission belongs in Capture, after that callback has bound
                // its own reward, and before any action can be offered.
            }
            catch (Exception error) { f.Error = error.Message; if (!f.CheckinObserved) f.CheckinError = error.Message; }
            if (f.CheckinObserved)
            {
                try
                {
                    uint now, day, cycle;
                    string clock = DailyRefreshGate.ReadClock(f.Character, out now, out day, out cycle);
                    if (clock != "" || !ReferenceEquals(f.Character, D.Static(Model.FirstType("Global"), "character_")) ||
                        f.Role != Convert.ToUInt64(D.Read(f.Character, "id"), CultureInfo.InvariantCulture) ||
                        D.Int(D.Read(f.Character, "checkin_today")) != f.Today || now < f.Now || day != f.Day || cycle != f.Cycle)
                        throw new ProbeFailure(clock == "" ? "sign_salary_checkin_state_changed" : clock);
                }
                catch (Exception error) { f.CheckinObserved = false; f.CheckinError = error.Message; }
            }
            return f;
        }

        private static bool CheckUi(string name)
        { return (bool)D.Method(Model.GameType("MainUI"), "checkUI", new[] { typeof(string) }, true, typeof(bool)).Invoke(null, new object[] { name }); }
        private static Component FindPageReceiver(D context, string name)
        {
            Type type = name == "ReputationUI" ? Model.FirstType(name) : Model.GameType(name);
            Component[] local = context.GlobalUi.GetComponents(type);
            if (local.Length == 0) return null;
            if (local.Length != 1) throw new ProbeFailure("development_ui_uniqueness:" + name + ":" + local.Length);
            Component receiver = local[0];
            if (receiver == null || receiver.GetType() != type || receiver.gameObject != context.GlobalUi.gameObject ||
                !ReferenceEquals(D.Read(receiver, "rmg"), context.UiRoot) || receiver.transform.root != context.UiRoot.transform)
                throw new ProbeFailure("sign_page_rewards_receiver_ownership:" + name);
            int observed = 0;
            foreach (UnityEngine.Object item in context.All) if (ReferenceEquals(item, receiver)) observed++;
            if (observed != 1) throw new ProbeFailure("sign_page_rewards_receiver_not_observed:" + name + ":" + observed);
            return receiver;
        }
        private static object Handler(object client, ushort op) { return ((IDictionary)D.Read(client, "m_handlers"))[op]; }
        private static bool BonusContains(object character, string kind, uint time)
        {
            foreach (object row in (IList)D.Read(D.Read(character, kind), "bonuses")) if ((uint)D.Read(row, "time") == time) return true;
            return false;
        }
        private int Elapsed() { return unchecked((int)((uint)(Environment.TickCount - stageTick))); }
        private void Offer(string value, string action) { nextAction = action; Set(value, ""); }
        private void Unknown(string why)
        {
            unknown = true;
            if (tap != null) { tap.StopObserving(); if (tap.TryRestore()) tap = null; }
            if (taskId == SignPageRewardsTask && pageItem != null && pageReceipt != null && !pageReceipt.StateConfirmed)
            { pageItem.Status = "UNKNOWN"; pageItem.Reason = why; }
            nextAction = ""; Set("UNKNOWN", why);
        }
        internal void Stop(string why)
        { stopped = true; queued = null; if (tap != null) { tap.StopObserving(); if (tap.TryRestore()) tap = null; } nextAction = ""; Set("STOPPED", why); }
        internal void NotInvoked(string why) { queued = null; GuiInvocationAttempted = false; Set(status, why); }
        private void Set(string value, string why)
        { status = value; reason = why; string next = Body(); if (next != stamp) { stamp = next; Revision++; } }
        private string Body()
        {
            return "\"task_id\":" + taskId + ",\"route\":" + D.Q(taskId == SignTask ? "signin" : taskId == SalaryOnlyTask ? "salary_only" :
                taskId == SignPageRewardsTask ? "signin_rewards" : "salary") +
                ",\"role_id\":" + D.Q(frame == null ? "0" : frame.Role.ToString(CultureInfo.InvariantCulture)) +
                ",\"refresh_cycle\":" + (frame == null ? 0 : frame.Cycle) + ",\"operation_id\":" + D.Q(operationId) +
                ",\"status\":" + D.Q(status) + ",\"reason\":" + D.Q(reason) + ",\"next_action\":" + D.Q(nextAction) +
                ",\"availability\":" + D.Q(frame == null || (taskId == SignTask ? !frame.CheckinObserved : frame.Error != "") ? "UNKNOWN" : Eligibility(frame)) +
                ",\"mutation_attempted\":" + D.B(attempted) + ",\"stock_callback_returned\":" + D.B(returned) +
                ",\"reward_confirmed\":" + D.B(confirmed) + ",\"cleanup_pending\":" + D.B(holding) +
                ",\"closed\":" + D.B(closed) + ",\"busy\":" + D.B(Busy) + ",\"request_count\":" + requests +
                ",\"opcode\":" + opcode + ",\"checkin_today\":" + (frame == null ? -1 : frame.Today) +
                ",\"game_day\":" + (frame == null ? 0 : frame.Day) + ",\"checkin_state_observed\":" + D.B(frame != null && frame.CheckinObserved) +
                ",\"checkin_times\":" + (frame == null ? 0 : frame.Checkins) + ",\"salary_time\":" + (frame == null ? 0 : frame.SalaryTime) +
                ",\"arena_time\":" + (frame == null ? 0 : frame.ArenaTime) +
                (taskId == SignPageRewardsTask ? ",\"point_time\":" + (frame == null ? 0 : frame.PointTime) +
                    ",\"point_isgot\":" + D.B(frame != null && frame.PointGot) +
                    ",\"at_home\":" + D.B(frame != null && frame.Home) +
                    ",\"page_open\":" + D.B(frame != null && ReferenceEquals(frame.Sub, frame.Sign)) + ",\"items\":" + PageItemsJson() : "");
        }
        internal string Json() { return "{\"schema\":1,\"revision\":" + Revision + "," + Body() + "}"; }

        private string PageItemsJson()
        {
            string json = "[";
            for (int i = 0; i < pageItems.Length; i++)
            {
                PageItem item = pageItems[i]; int callbacks = 0, confirmations = 0;
                string receipts = "[";
                for (int n = 0; n < item.Receipts.Count; n++)
                {
                    PageReceipt receipt = item.Receipts[n];
                    if (receipt.CallbackReturned) callbacks++;
                    if (receipt.StateConfirmed) confirmations++;
                    if (n != 0) receipts += ",";
                    receipts += "{\"opcode\":" + receipt.Opcode + ",\"request_time\":" + receipt.RequestTime +
                        ",\"receipt_result\":" + NullableNumber(receipt.Result) + ",\"receipt_time\":" + NullableNumber(receipt.Time) +
                        ",\"receipt_gold_consume\":" + NullableNumber(receipt.GoldConsume) +
                        ",\"stock_callback_returned\":" + D.B(receipt.CallbackReturned) + ",\"reward_confirmed\":" + D.B(receipt.StateConfirmed) + "}";
                }
                if (i != 0) json += ",";
                json += "{\"key\":" + D.Q(item.Key) + ",\"name\":" + D.Q(item.Name) + ",\"status\":" + D.Q(item.Status) +
                    ",\"reason\":" + D.Q(item.Reason) + ",\"availability\":" + D.Q(item.Availability) +
                    ",\"initial_availability\":" + D.Q(item.InitialAvailability) + ",\"request_count\":" + item.Receipts.Count +
                    ",\"receipt_count\":" + callbacks + ",\"confirmed_count\":" + confirmations +
                    ",\"stock_callback_returned\":" + D.B(item.Receipts.Count > 0 && callbacks == item.Receipts.Count) +
                    ",\"reward_confirmed\":" + D.B(item.Receipts.Count > 0 && confirmations == item.Receipts.Count) +
                    ",\"receipts\":" + receipts + "]}";
            }
            return json + "]";
        }
        private static string NullableNumber(uint? value)
        { return value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "null"; }

        // Read only scalar receipt fields; stock JSProtoBuf still owns complete
        // decoding and every account/UI mutation. Reject duplicate scalar tags.
        private static Dictionary<int, ulong> Scalars(byte[] body)
        {
            if (body == null || body.Length == 0 || body.Length > 1048576) throw new FormatException("sign_salary_receipt_size");
            Dictionary<int, ulong> values = new Dictionary<int, ulong>(); int p = 0;
            while (p < body.Length)
            {
                ulong tag = Varint(body, ref p); int field = (int)(tag >> 3), wire = (int)(tag & 7);
                if (field < 1 || tag > UInt32.MaxValue) throw new FormatException("sign_salary_receipt_tag");
                if (wire == 0) { if (values.ContainsKey(field)) throw new FormatException("sign_salary_receipt_duplicate"); values[field] = Varint(body, ref p); }
                else if (wire == 2) { ulong length = Varint(body, ref p); if (length > (ulong)(body.Length - p)) throw new FormatException(); p += (int)length; }
                else if (wire == 1 || wire == 5) { p += wire == 1 ? 8 : 4; if (p > body.Length) throw new FormatException(); }
                else throw new FormatException("sign_salary_receipt_wire");
            }
            return values;
        }
        private static ulong Value(Dictionary<int, ulong> values, int field)
        { ulong value; if (!values.TryGetValue(field, out value)) throw new FormatException("sign_salary_receipt_missing:" + field); return value; }
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
