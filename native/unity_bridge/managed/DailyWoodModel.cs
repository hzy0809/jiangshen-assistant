using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using UnityEngine;
using D = JiangShen.AssistantRuntime.SharedDevelopmentContext;
using Reader = JiangShen.AssistantRuntime.PlayerStateServerSnapshot.Reader;

namespace JiangShen.AssistantRuntime
{
    internal delegate D DailyWoodContextProvider();
    internal delegate bool DailyWoodAdmissionCheck();
    internal delegate bool DailyWoodAdmission(DailyWoodAdmissionCheck prepare, DailyWoodAdmissionCheck check);

    internal sealed class DailyWoodModel
    {
        internal const int TaskId = 22, Category = 3;
        internal const ushort Info = 612, Reward = 614, Fast = 616, Activity = 355;
        private const uint GuiMs = 1500, SettleMs = 25000;
        private static readonly Type[] ResponseArgs = { typeof(MemoryStream), typeof(object) };

        internal sealed class Frame
        {
            internal object Character, Client, Root, GlobalRoot, Loader, Main, Panel, Result, Battle, Sub, PushHandler, InfoHandler, FastHandler;
            internal object StatusCache, PlayerCache, ResultCache, Rows, CategoryRows, TaskTemplate, CategoryCache, Row, ListedRow, RewardHandler, RewardBonuses;
            internal Component RewardUi, Prompt;
            internal DailyTransport Transport;
            internal D Context;
#if JSB_DAILY_WOOD_TEST
            internal object StreamIdentity;
            internal Frame Copy() { return (Frame)MemberwiseClone(); }
#endif
            internal DailyWoodPlayer Player, ResultPlayer;
            internal Delegate ResultClose, RewardClose;
            internal DateTime PanelShownAt, ResultShownAt, RewardShownAt, PromptShownAt;
            internal ulong Role;
            internal uint Now, Cycle, Epoch, Next, Sid, GoldConsumed;
            internal int Gold, BoundGold, FinishTimestamp, Lose, Count, Listed, Refresh, ListedRefresh, Tick;
            internal bool Valid, TaskValid, Idle, Home, PanelReady, PanelHidden, ResultReady, ResultHidden, Loading, ResultFinished, AllTurns;
            internal bool RewardReady, RewardHidden = true, PromptReady, PromptHidden = true;

            internal bool SameSession(Frame other)
            {
                if (other == null || Role == 0 || Role != other.Role || Sid != other.Sid ||
                    !ReferenceEquals(Character, other.Character) || !ReferenceEquals(Client, other.Client) ||
                    !ReferenceEquals(Root, other.Root) || !ReferenceEquals(GlobalRoot, other.GlobalRoot) ||
                    !ReferenceEquals(Loader, other.Loader) || !ReferenceEquals(Main, other.Main) ||
                    !ReferenceEquals(Panel, other.Panel) || !ReferenceEquals(Result, other.Result) ||
                    !ReferenceEquals(Battle, other.Battle)) return false;
#if JSB_DAILY_WOOD_TEST
                if (Transport == null) return StreamIdentity != null && ReferenceEquals(StreamIdentity, other.StreamIdentity);
#endif
                return Transport != null && Transport.Same(other.Transport);
            }

            internal bool SameBoundary(Frame other)
            {
                return Valid && SameSession(other) && other.Valid && Now >= other.Now && Cycle == other.Cycle &&
                    Epoch == other.Epoch && Next == other.Next;
            }

            internal bool SameGold(Frame other)
            { return Gold == other.Gold && BoundGold == other.BoundGold && GoldConsumed == other.GoldConsumed; }

            internal bool SameTask(Frame other)
            {
                return TaskValid && other.TaskValid && ReferenceEquals(Rows, other.Rows) &&
                    ReferenceEquals(CategoryRows, other.CategoryRows) && ReferenceEquals(CategoryCache, other.CategoryCache) &&
                    ReferenceEquals(TaskTemplate, other.TaskTemplate);
            }

            internal bool SameCounterValues(Frame other)
            {
                return SameTask(other) && Count == other.Count && Listed == other.Listed &&
                    Refresh == other.Refresh && ListedRefresh == other.ListedRefresh;
            }

            internal bool SameCount(Frame other)
            { return SameCounterValues(other) && ReferenceEquals(Row, other.Row) && ReferenceEquals(ListedRow, other.ListedRow); }

            internal bool SamePlayer(Frame other)
            {
                return ReferenceEquals(StatusCache, other.StatusCache) && ReferenceEquals(PlayerCache, other.PlayerCache) &&
                    FinishTimestamp == other.FinishTimestamp && (Player == null ? other.Player == null : Player.Same(other.Player));
            }

            internal bool SameAdmission(Frame other, bool sealedProgress = false)
            {
                return SameBoundary(other) && SameGold(other) && SamePlayer(other) &&
                    (sealedProgress ? SameCounterValues(other) : SameCount(other)) &&
                    ReferenceEquals(Sub, other.Sub) && ReferenceEquals(PushHandler, other.PushHandler) &&
                    ReferenceEquals(InfoHandler, other.InfoHandler) && ReferenceEquals(FastHandler, other.FastHandler) &&
                    ReferenceEquals(RewardHandler, other.RewardHandler) && ReferenceEquals(RewardUi, other.RewardUi) &&
                    ReferenceEquals(Prompt, other.Prompt) && ReferenceEquals(RewardClose, other.RewardClose) &&
                    ReferenceEquals(RewardBonuses, other.RewardBonuses) &&
                    RewardShownAt == other.RewardShownAt && PromptShownAt == other.PromptShownAt &&
                    RewardReady == other.RewardReady && RewardHidden == other.RewardHidden &&
                    PromptReady == other.PromptReady && PromptHidden == other.PromptHidden &&
                    ReferenceEquals(ResultCache, other.ResultCache) && ReferenceEquals(ResultClose, other.ResultClose) &&
                    PanelShownAt == other.PanelShownAt && ResultShownAt == other.ResultShownAt &&
                    Home == other.Home && Idle == other.Idle && Loading == other.Loading && PanelReady == other.PanelReady &&
                    PanelHidden == other.PanelHidden && ResultReady == other.ResultReady && ResultHidden == other.ResultHidden;
            }
        }

        private sealed class Intent
        {
            internal Frame Before;
            internal string Id, Action;
            internal int Sequence;
        }

        private readonly Hashtable operations = new Hashtable();
        private readonly Hashtable attempts = new Hashtable();
        private Frame frame, opened, ready, goldBaseline, infoReturned, sent, fastReturned, activityView, resultShown, ownedPanel, resultClosing, rewardSent, rewardReturned;
        private Intent queued, operation;
        private DailyWoodTap reply, activity;
        private DailyWoodPacket receipt;
        private NativeBattleStartTicket durableTicket;
        private string durableReceiptHash;
        private int lastSequence, started, freeAttempts, rewardsClaimed;
        private bool stopped, uiHold, closingPanel, activityAdvanced, infoFailed, rewardConfirmed, promptDismissed, mutationAttempted;
        private ushort replyOpcode;
        private string callbackError, terminalReason;
        internal int Revision { get; private set; }
        internal string Status { get; private set; }
        internal string Reason { get; private set; }
        internal bool MutationAttempted { get { return mutationAttempted; } }
        internal bool GuiInvocationAttempted { get; private set; }
        internal bool TaskConfirmed { get; private set; }
        internal bool CleanupPending { get { return uiHold || reply != null || activity != null; } }
        internal bool Unresolved { get { return Status == "UNKNOWN" || Status == "PENDING" || CleanupPending; } }
        internal bool Busy { get { return queued != null || Unresolved; } }

        internal DailyWoodModel() { Set("IDLE", "wood_unobserved"); }

        internal void Capture(D context)
        {
            object client = context == null ? null : context.Client;
            if (client == null || !Monitor.TryEnter(client, 0))
            {
                if (Status == "UNKNOWN" || stopped) Restore();
                else if (opened != null && uiHold && (client == null || Waiting() && Expired(Environment.TickCount, started, SettleMs)))
                    Unknown("wood_client_or_observation_timeout");
                return;
            }
            try { Observe(Read(context)); }
            finally { Monitor.Exit(client); }
        }

        internal void Observe(Frame next)
        {
            Frame previous = frame;
            frame = next;
            if (previous == null || !next.SameAdmission(previous, TaskConfirmed)) Revision++;
            if (opened == null || !uiHold) { Restore(); return; }
            if (Status == "UNKNOWN" || stopped)
            {
                Restore();
                if (SafeHome(next)) { uiHold = false; Revision++; }
                return;
            }
            if (!next.SameBoundary(opened)) { Unknown("wood_role_stream_or_clock_changed"); return; }
            if (!GoldSafe(next)) { Unknown("wood_gold_changed"); return; }
            if (Waiting() && Expired(next.Tick, started, SettleMs)) { Unknown("wood_settlement_timeout"); return; }
            if (infoFailed && !closingPanel) { Restore(); ObserveInfoFailure(next); return; }
            if (callbackError != null || reply != null && reply.Error != null || activity != null && activity.Error != null)
            {
                string error = callbackError ?? (reply == null ? null : reply.Error) ?? activity.Error;
                if (!MutationAttempted) FailInfo(next, error); else Unknown(error);
                return;
            }
            object currentReply = Handler(next, replyOpcode);
            if (reply != null && (reply.Returned ? currentReply != null : !reply.Attached || !reply.Owns(currentReply)))
            { Unknown("wood_one_shot_callback_changed"); return; }
            if (activity != null && (!activity.Attached || !activity.Owns(next.PushHandler))) { Unknown("wood_355_callback_changed"); return; }

            if (closingPanel)
            {
                if (!ReadyMatches(next)) Unknown("wood_panel_close_state_changed");
                else if (SafeHome(next)) Complete();
                return;
            }
            if (Status == "OPENING") ObserveInfo(next);
            else if (Status == "CLAIMING") ObserveReward(next);
            else if (Status == "REFRESHING") ObserveRewardRefresh(next);
            else if (Status == "REWARD")
            {
                if (!OwnReward(next) || !next.RewardReady || !next.SameCount(rewardSent))
                    Unknown("wood_reward_window_changed");
            }
            else if (Status == "READY")
            {
                if (!OwnPanel(next) || !ReadyMatches(next))
                    Unknown("wood_open_panel_or_task_changed");
            }
            else if (Status == "PENDING") ObserveFast(next);
            else if (Status == "RESULT")
            {
                if (!OwnPanel(next) || !OwnResult(next) || !next.SamePlayer(fastReturned) || !next.ResultReady ||
                    next.ResultShownAt != resultShown.ResultShownAt || !ProgressMatches(next))
                    Unknown("wood_result_or_activity_changed");
            }
            else if (Status == "CLOSING")
            {
                if (ready != null && sent == null && rewardSent == null)
                {
                    if (!OwnPanel(next) || !ReadyMatches(next)) Unknown("wood_settled_state_changed");
                }
                else if (!OwnPanel(next) || !next.ResultHidden || !OwnResult(next) || !next.SamePlayer(fastReturned) || !ProgressMatches(next))
                    Unknown("wood_result_close_unverified");
                else if (next.ResultFinished && !promptDismissed)
                {
                    if (OwnPrompt(next) && next.PromptReady) Set("PROMPT", "wood_level_finished_prompt");
                    else Unknown("wood_result_prompt_unverified");
                }
                else if (next.PanelReady && next.Idle && next.RewardHidden && next.PromptHidden)
                    Ready(next, "wood_free_fast_settled");
            }
            else if (Status == "PROMPT" && (!OwnPrompt(next) || !next.PromptReady || !next.SamePlayer(fastReturned) || !ProgressMatches(next)))
                Unknown("wood_result_prompt_changed");
            else if (Status == "BLOCKED" && SafeHome(next)) Complete();
        }

        private void ObserveInfo(Frame f)
        {
            if (infoReturned == null) return;
            if (!ReferenceEquals(f.Sub, opened.Panel) || !f.SamePlayer(infoReturned) || !f.SameCount(opened))
            { FailInfo(f, "wood_612_cache_or_owner_changed"); return; }
            if (!f.PanelReady || !f.ResultHidden || !f.Idle) return;
            ownedPanel = f;
            Restore();
            Ready(f, "wood_612_current_free_quota");
        }

        private void FailInfo(Frame f, string reason)
        {
            // 612 only reads status. Its failure does not make a challenge
            // uncertain, but the stock callback still owns its UI until closed.
            infoFailed = true;
            Restore();
            callbackError = null;
            Block(reason);
            ObserveInfoFailure(f);
        }

        private void ObserveInfoFailure(Frame f)
        {
            if (!f.SameCount(opened)) { Unknown("wood_612_failure_activity_changed"); return; }
            if (SafeHome(f)) { Complete(); return; }
            if (ReferenceEquals(f.Sub, opened.Panel) && f.PanelReady && f.ResultHidden && f.Idle &&
                f.InfoHandler == null && f.FastHandler == null && reply == null && activity == null)
                ready = ownedPanel = f;
        }

        private void ObserveFast(Frame f)
        {
            if (!OwnPanel(f) || !f.SameTask(sent)) { Unknown("wood_fast_panel_or_task_changed"); return; }
            if (!f.SameCount(activityView))
            { Unknown("wood_355_progress_or_caches_disagree"); return; }
            if (fastReturned == null) { Set("PENDING", "wood_waiting_616_receipt"); return; }
            if (receipt.State != 1)
            {
                if (activityAdvanced || !f.ResultHidden || receipt.State == 3 && !f.SamePlayer(fastReturned))
                { Unknown("wood_fast_rejection_conflict"); return; }
                Restore();
                if (receipt.State == 3) Ready(f, "wood_reward_pending");
                else Block("wood_fast_rejected");
                return;
            }
            if (!f.SamePlayer(fastReturned) || !OwnResult(f)) { Unknown("wood_616_result_or_quota_changed"); return; }
            if (!activityAdvanced && sent.Count == 0) { Set("PENDING", "wood_waiting_355_progress"); return; }
            if (!f.ResultReady) { Set("PENDING", "wood_waiting_result_ui"); return; }
            if (!f.Idle) { Set("PENDING", "wood_waiting_network_idle"); return; }
            try
            {
                if (durableTicket == null || durableReceiptHash == null)
                    throw new ProbeFailure("wood_durable_ticket_or_receipt_missing");
                // The first daily use requires the exact 355 increment. Later
                // free uses retain the capped 1/1 row and prove the 616 quota
                // decrement against this captured send instead.
                NativeBattleStartStore.Confirm(durableTicket,
                    "stock-616:quota:" + fastReturned.Player.Quota.ToString(CultureInfo.InvariantCulture) +
                    ":count:" + f.Count.ToString(CultureInfo.InvariantCulture) +
                    ":refresh:" + f.Refresh.ToString(CultureInfo.InvariantCulture) +
                    ":progress:" + (activityAdvanced ? "355" : "capped-count-1") + ":body:" + durableReceiptHash);
            }
            catch (Exception) { Unknown("wood_durable_confirmation_unverifiable_no_retry"); return; }
            TaskConfirmed = true;
            freeAttempts++;
            resultShown = f;
            Restore();
            Set("RESULT", "wood_free_fast_and_activity_observed");
        }

        private bool GoldSafe(Frame f)
        {
            return rewardSent == null ? f.SameGold(goldBaseline ?? opened) : f.Gold >= rewardSent.Gold &&
                f.BoundGold >= rewardSent.BoundGold && f.GoldConsumed == rewardSent.GoldConsumed;
        }

        private bool OwnReward(Frame f)
        {
            return rewardReturned != null && f.RewardUi != null && ReferenceEquals(f.RewardUi, rewardReturned.RewardUi) &&
                (Status == "CLAIMING" ? f.RewardShownAt > rewardSent.RewardShownAt : f.RewardShownAt == rewardReturned.RewardShownAt) &&
                ReferenceEquals(f.RewardClose, rewardReturned.RewardClose) && ReferenceEquals(f.RewardBonuses, rewardReturned.RewardBonuses) &&
                ExactRewardClose(f.Panel, f.RewardClose);
        }

        private void ObserveReward(Frame f)
        {
            if (!OwnPanel(f) || !f.SameCount(rewardSent) || !f.SamePlayer(rewardSent))
            { Unknown("wood_614_owner_or_activity_changed"); return; }
            if (rewardReturned == null || f.Loading || !f.Idle) return;
            if (!OwnReward(f) || !f.RewardReady) { Unknown("wood_614_reward_unverified"); return; }
            rewardReturned = f;
            Restore();
            Set("REWARD", "wood_614_stock_reward_observed");
        }

        private void ObserveRewardRefresh(Frame f)
        {
            if (infoReturned == null || f.Loading || !f.Idle) return;
            if (rewardReturned == null || !ReferenceEquals(f.Sub, opened.Panel) || !f.SameCount(rewardSent) || !f.SamePlayer(infoReturned) ||
                !f.RewardHidden || !f.PromptHidden || !f.PanelReady || f.Player.CanTake)
            { Unknown("wood_reward_claim_not_confirmed"); return; }
            // 614 carries bonuses, not a player status. Only its exact stock
            // reward followed by this 612 establishes both the claim and the
            // next free quote; the old quote need not survive that refresh.
            rewardConfirmed = true; rewardsClaimed++;
            Restore(); Ready(f, "wood_reward_claimed");
        }

        private bool ReadyMatches(Frame f)
        {
            return ready != null && f.SamePlayer(ready) && (TaskConfirmed ?
                f.Row != null && ReferenceEquals(f.Row, f.ListedRow) && f.SameCounterValues(ready) : f.SameCount(ready));
        }

        private void Ready(Frame f, string reason)
        {
            // Keep the same page and operation. Rebase only after a correlated
            // stock status and all owned result/prompt windows have settled.
            ready = ownedPanel = goldBaseline = infoReturned = f;
            sent = fastReturned = activityView = resultShown = resultClosing = rewardSent = rewardReturned = null;
            receipt = null;
            activityAdvanced = promptDismissed = false;
            terminalReason = null;
            if (f.Player.CanTake) Set("READY", "wood_reward_pending");
            else if (f.Player.Quota != 0) Set("READY", reason);
            else if (MutationAttempted) Set("CLOSING", "wood_free_quota_exhausted");
            else Block("wood_no_free_quota");
        }

        private bool OwnPanel(Frame f)
        { return ownedPanel != null && ReferenceEquals(f.Sub, ownedPanel.Panel) && f.PanelShownAt == ownedPanel.PanelShownAt; }

        private bool OwnResult(Frame f)
        {
            return fastReturned != null && f.ResultCache != null && ReferenceEquals(f.ResultCache, fastReturned.ResultCache) &&
                ReferenceEquals(f.ResultClose, fastReturned.ResultClose) && f.ResultPlayer != null &&
                receipt.Result.Same(f.ResultPlayer) && f.ResultFinished == receipt.LevelFinished &&
                f.AllTurns == receipt.AllTurns && f.Lose == receipt.Lose;
        }

        private bool ProgressMatches(Frame f)
        {
            // After settlement stock 355 can rebuild the shared row while
            // leaving the verified activity values and task identity intact.
            return (activityAdvanced || sent != null && sent.Count == 1 && f.Count == 1) &&
                f.Row != null && ReferenceEquals(f.Row, f.ListedRow) && f.SameCounterValues(activityView);
        }

        private bool SafeHome(Frame f)
        {
            return f.SameSession(opened) && f.Home && f.Idle && f.InfoHandler == null && f.FastHandler == null && f.RewardHandler == null &&
                reply == null && activity == null;
        }

        private void Complete()
        {
            uiHold = false;
            Set(terminalReason == null ? "COMPLETED" : "BLOCKED", terminalReason ??
                "wood_free_quota_exhausted");
        }

        private void Block(string reason) { terminalReason = reason; Set("BLOCKED", reason); }
        private void Unknown(string reason) { Set("UNKNOWN", reason); Restore(); }
        private bool Waiting() { return Status == "OPENING" || Status == "PENDING" || Status == "CLAIMING" || Status == "REFRESHING" || closingPanel; }
        private void Set(string status, string reason)
        { if (Status != status || Reason != reason) { Status = status; Reason = reason; Revision++; } }
        private static bool Expired(int now, int before, uint ms) { return unchecked((uint)(now - before)) >= ms; }

        internal bool Queue(int task, ulong role, string id, int sequence, int revision, string action, out string why)
        {
            why = "wood_command_identity";
            if (task != TaskId || stopped || frame == null || role == 0 || frame.Role != role || queued != null ||
                revision != Revision || sequence <= lastSequence || !new DevelopmentCommand { OperationId = id }.ValidId()) return false;
            bool open = action == "open";
            if (open ? Busy || operations.Contains(id) || operations.Count >= 1024 : operation == null || operation.Id != id) return false;
            why = Gate(frame, action);
            if (why != "") return false;
            queued = new Intent { Before = frame, Id = id, Action = action, Sequence = sequence };
            GuiInvocationAttempted = false;
            if (open) operations.Add(id, true);
            lastSequence = sequence;
            Revision++;
            return true;
        }

        private string Gate(Frame f, string action)
        {
            if (f == null || !f.Valid || !f.TaskValid || !f.Idle || f.InfoHandler != null || f.FastHandler != null || f.RewardHandler != null ||
                f.PushHandler == null || stopped || Status == "UNKNOWN") return "wood_ui_activity_or_network_unavailable";
            if (action == "open")
                return f.Home ? "" : "wood_main_screen_required";
            if (!f.SameBoundary(opened) || !GoldSafe(f) || !OwnPanel(f) || !f.PanelReady)
                return "wood_owned_panel_unavailable";
            if (action == "fast" && Status == "READY" && sent == null && reply == null && activity == null && f.ResultHidden &&
                f.RewardHidden && f.PromptHidden &&
                ReadyMatches(f) && !f.Player.CanTake && f.Player.Quota > 0 &&
                !attempts.Contains(AttemptKey(f))) return "";
            if (action == "claim_reward" && Status == "READY" && rewardSent == null && sent == null && reply == null &&
                activity == null && f.ResultHidden && f.RewardHidden && f.PromptHidden && f.Player.CanTake &&
                ReadyMatches(f)) return "";
            if (action == "close_reward" && Status == "REWARD" && reply == null && activity == null &&
                f.RewardReady && OwnReward(f) && f.SameCount(rewardSent)) return "";
            if (action == "dismiss_prompt" && Status == "PROMPT" && reply == null && activity == null &&
                OwnPrompt(f) && f.PromptReady && f.SamePlayer(fastReturned) && ProgressMatches(f)) return "";
            if (action == "close_result" && Status == "RESULT" && reply == null && activity == null &&
                f.RewardHidden && f.PromptHidden && f.ResultReady && OwnResult(f) && f.SamePlayer(fastReturned) &&
                f.ResultShownAt == resultShown.ResultShownAt && ProgressMatches(f)) return "";
            if (action == "close_panel" && !closingPanel && f.ResultHidden && f.RewardHidden && f.PromptHidden && reply == null && activity == null &&
                (Status == "BLOCKED" || Status == "CLOSING" && sent == null && rewardSent == null &&
                ReadyMatches(f) && !f.Player.CanTake && f.Player.Quota == 0)) return "";
            return "wood_action_not_ready";
        }

        internal bool ExecuteGui(DailyWoodContextProvider fresh, DevelopmentPublication publish, DailyWoodAdmission admit, out string why)
        {
            return ExecuteGuiCore(delegate { return Read(fresh()); }, BindStock, StillCurrent, BindTap, publish, admit, D.Gui, out why);
        }

        private delegate DailyWoodTap TapBinder(Frame f, ushort opcode);
        internal delegate Frame FrameProvider();
        private delegate DevelopmentPublication StockBinder(Frame f, string action);
        internal delegate bool StockGate(Frame f, string action);
        private bool ExecuteGuiCore(FrameProvider fresh, StockBinder bind,
            StockGate live, TapBinder tap, DevelopmentPublication publish, DailyWoodAdmission admit, bool gui, out string why)
        {
            why = "wood_not_queued";
            Intent intent = queued;
            if (intent == null) return false;
            object client = intent.Before.Client;
            if (!gui || publish == null || admit == null || !Monitor.TryEnter(client, 0))
            { why = "wood_gui_or_client_unavailable"; NotInvoked(why); return false; }
            bool publishing = false, committed = false;
            try
            {
                Frame current = null;
                DevelopmentPublication stock = null;
                bool fast = intent.Action == "fast";
                bool open = intent.Action == "open";
                bool claim = intent.Action == "claim_reward", closeReward = intent.Action == "close_reward";
                DailyWoodAdmissionCheck check = delegate
                {
                    committed = queued == intent && !stopped && current != null && stock != null &&
                        !Expired(Environment.TickCount, intent.Before.Tick, GuiMs) && live(current, intent.Action) &&
                        (!fast || activity != null && activity.Attached && activity.Calls == 0);
                    return committed;
                };
                publishing = true; publish(); publishing = false;
                if (!admit(delegate
                {
                    if (queued != intent || stopped || Expired(Environment.TickCount, intent.Before.Tick, GuiMs)) return false;
                    current = fresh();
                    if (Gate(current, intent.Action) != "" || !current.SameAdmission(intent.Before, TaskConfirmed && !open && !fast)) return false;
                    stock = bind(current, intent.Action);
                    if (fast) activity = tap(current, Activity);
                    return stock != null && (!fast || activity != null && activity.Attached);
                }, check) || !committed || queued != intent || stopped || Expired(Environment.TickCount, intent.Before.Tick, GuiMs))
                { why = "wood_final_command_or_context_changed"; NotInvoked(why); return false; }

                queued = null;
                started = Environment.TickCount;
                if (open)
                {
                    operation = intent; opened = goldBaseline = current; uiHold = true;
                    ready = null; freeAttempts = rewardsClaimed = 0; mutationAttempted = false;
                    infoReturned = sent = fastReturned = activityView = resultShown = ownedPanel = resultClosing = null;
                    callbackError = terminalReason = null; receipt = null;
                    closingPanel = activityAdvanced = TaskConfirmed = infoFailed = false;
                    rewardSent = rewardReturned = null; rewardConfirmed = promptDismissed = false;
                    replyOpcode = Info;
                    Set("OPENING", "wood_waiting_612");
                }
                else if (fast)
                {
                    // This path does not use BattleLaunch. Admission has just
                    // revalidated the exact role/session and zero-cost stock
                    // action, so persist before stock() can be entered.
                    try { durableTicket = NativeBattleStartStore.Reserve(current.Role, "WoodFast", intent.Id, null, intent.Sequence); }
                    catch (ProbeFailure failure) { why = failure.Message; Unknown(why); return false; }
                    durableReceiptHash = null;
                    mutationAttempted = true;
                    sent = activityView = current;
                    attempts[AttemptKey(current)] = true;
                    replyOpcode = Fast;
                    Set("PENDING", "wood_waiting_616_and_355");
                }
                else if (claim)
                {
                    mutationAttempted = true;
                    rewardSent = current; replyOpcode = Reward;
                    Set("CLAIMING", "wood_waiting_614");
                }
                else if (closeReward)
                {
                    infoReturned = null; replyOpcode = Info;
                    Set("REFRESHING", "wood_reward_waiting_612");
                }
                else if (intent.Action == "dismiss_prompt")
                { promptDismissed = true; Set("CLOSING", "wood_prompt_dismissed"); }
                else if (intent.Action == "close_result") { resultClosing = current; Set("CLOSING", "wood_verifying_result_close"); }
                else { closingPanel = true; Set("CLOSING", "wood_verifying_panel_close"); }
                GuiInvocationAttempted = true;
                stock();
                if (open || fast || claim || closeReward)
                {
                    reply = tap(current, replyOpcode);
                    if (reply == null || !reply.Attached)
                    { why = "wood_exact_callback_unobserved"; Unknown(why); return false; }
                }
                why = "";
                return true;
            }
            catch (Exception)
            {
                why = "wood_stock_or_admission_failed";
                if (GuiInvocationAttempted) Unknown(why); else NotInvoked(why);
                if (publishing) throw;
                return false;
            }
            finally { Monitor.Exit(client); }
        }

        internal void NotInvoked(string why)
        {
            if (queued == null) return;
            string action = queued.Action;
            queued = null;
            if (action == "fast") Restore();
            Revision++;
            if (opened == null || !uiHold) Set("BLOCKED", why);
        }

        internal void Stop() { Stop("wood_stopped"); }
        internal void Stop(string why)
        {
            bool changed = !stopped || queued != null;
            stopped = true;
            queued = null;
            if (uiHold && Status != "UNKNOWN") Unknown(String.IsNullOrEmpty(why) ? "wood_stopped" : why);
            else Restore();
            if (changed) Revision++;
        }

        private void Restore()
        {
            if (reply != null && reply.TryRestore()) reply = null;
            if (activity != null && activity.TryRestore()) activity = null;
        }

        private void OnReply(Frame owner, ushort opcode, byte[] body, Frame after)
        {
            if (!after.SameBoundary(owner) || !GoldSafe(after) || !ReferenceEquals(after.Sub, owner.Panel) ||
                Expired(after.Tick, started, SettleMs))
                throw new ProbeFailure("wood_callback_owner_or_gold_changed");
            if (opcode == Activity)
            {
                DailyTimedWire.Progress value;
                if (sent == null || !ReferenceEquals(sent, owner) || Status != "PENDING" ||
                    !DailyTimedWire.ReadProgress(body, TaskId, out value)) throw new ProbeFailure("wood_355_dispatch_or_schema");
                if (value == null) return;
                int count = Math.Min(value.Count, 1);
                if (!after.SameTask(activityView) || after.Count != count || after.Listed != count ||
                    after.Refresh != value.Refresh || after.ListedRefresh != value.Refresh || !ReferenceEquals(after.Row, after.ListedRow))
                    throw new ProbeFailure("wood_355_progress_or_caches_disagree");
                // Stock reconstructs the row even when its count and refresh
                // are unchanged. Track that identity without awarding progress.
                if (value.Count == activityView.Count && value.Refresh == activityView.Refresh)
                { activityView = after; return; }
                // Activity's constructor caps the daily row at one. Later free
                // attempts settle against 616 and the exact quota decrement.
                if (sent.Count == 1 && count == 1 && value.Refresh >= activityView.Refresh)
                { activityView = after; return; }
                if (activityAdvanced || value.Count != sent.Count + 1 || value.Refresh < activityView.Refresh ||
                    ReferenceEquals(after.Row, activityView.Row) || ReferenceEquals(after.Row, sent.Row))
                    throw new ProbeFailure("wood_355_unexpected_progress");
                activityAdvanced = true;
                Revision++;
                activityView = after;
                return;
            }

            if (opcode == Reward)
            {
                int bonuses = RewardBonusCount(body);
                IList stock = after.RewardBonuses as IList;
                if (Status != "CLAIMING" || rewardSent == null || !after.SameCount(rewardSent) ||
                    !after.SamePlayer(rewardSent) || after.RewardUi == null || !ExactRewardClose(after.Panel, after.RewardClose) ||
                    bonuses < 1 || stock == null || stock.Count != bonuses || ReferenceEquals(after.RewardBonuses, owner.RewardBonuses))
                    throw new ProbeFailure("wood_614_stock_reward_mismatch");
                rewardReturned = after;
                return;
            }

            DailyWoodPacket parsed = DailyWoodPacket.Decode(opcode, body);
            if (opcode == Info)
            {
                if (Status != "OPENING" && Status != "REFRESHING") throw new ProbeFailure("wood_612_dispatch_phase");
                if (parsed.Player == null) throw new ProbeFailure("wood_612_player_missing");
                if (!parsed.Player.Same(after.Player)) throw new ProbeFailure("wood_612_player_cache_mismatch");
                if (ReferenceEquals(after.StatusCache, owner.StatusCache)) throw new ProbeFailure("wood_612_status_cache_not_replaced");
                if (after.FinishTimestamp != unchecked((int)parsed.Finish)) throw new ProbeFailure("wood_612_finish_timestamp_mismatch");
                infoReturned = after;
                return;
            }

            if (Status != "PENDING") throw new ProbeFailure("wood_616_dispatch_phase");
            if (parsed.State == 1 || parsed.State == 3)
            {
                if (parsed.Player == null || !parsed.Player.Same(after.Player) || ReferenceEquals(after.StatusCache, owner.StatusCache))
                    throw new ProbeFailure("wood_616_status_unverified");
                if (parsed.State == 3 ? !parsed.Player.CanTake || parsed.Player.Quota != owner.Player.Quota :
                    parsed.Player.Quota + 1UL != owner.Player.Quota)
                    throw new ProbeFailure("wood_616_quota_unverified");
            }
            receipt = parsed;
            durableReceiptHash = NativeBattleStartStore.EvidenceHash(body);
            fastReturned = after;
            if (parsed.State == 1 && (parsed.Result == null || ReferenceEquals(after.ResultCache, owner.ResultCache) ||
                ReferenceEquals(after.ResultClose, owner.ResultClose) || !ExactClose(after.Panel, after.ResultClose) || !OwnResult(after)))
                throw new ProbeFailure("wood_616_result_unverified");
            Revision++;
        }

        private static object Handler(Frame f, ushort opcode)
        { return opcode == Info ? f.InfoHandler : opcode == Fast ? f.FastHandler : opcode == Reward ? f.RewardHandler : f.PushHandler; }

        private DailyWoodTap BindTap(Frame f, ushort opcode)
        {
            IDictionary handlers = Map(D.Read(f.Client, "m_handlers"), 512);
            object slot = handlers[opcode];
            if (slot == null || slot.GetType() != Model.FirstType("game.net.tcp_base+Handler")) return null;
            Delegate callback = D.Read(slot, "handler") as Delegate;
            if (opcode == Activity)
            {
                if (!ReferenceEquals(f.PushHandler, slot) || !DevelopmentCallbackCapture.IsDailyActivityPush(slot, callback)) return null;
            }
            else if (callback == null || !ReferenceEquals(callback.Target, f.Panel) || callback.Method != ReplyMethod(f.Panel.GetType(), opcode) ||
                Field<float>(slot, "life") != 10f || D.Read(slot, "arg") != null) return null;
            return DailyWoodTap.Attach(f.Client, handlers, opcode, slot, callback, delegate(byte[] body)
            {
                try { OnReply(f, opcode, body, Read(f.Context, false, activity)); }
                catch (Exception error)
                {
                    callbackError = error is ProbeFailure ? error.Message : "wood_callback_evidence_invalid";
                    throw;
                }
            });
        }

        private static MethodInfo ReplyMethod(Type panel, ushort op)
        {
            return ExactMethod(panel, op == Info ? "sc_dummy_challenge_info" : op == Reward ?
                "sc_dummy_challenge_get_reward" : "sc_dummy_challenge_fast_complete", ResponseArgs, false);
        }

        private static MethodInfo ExactMethod(Type type, string name, Type[] args, bool isStatic)
        {
            MethodInfo method = D.Method(type, name, args, isStatic, typeof(void));
            if (method.DeclaringType != type || (isStatic ? !method.IsPublic : !method.IsPrivate))
                throw new ProbeFailure("wood_stock_method_schema");
            return method;
        }

        private static bool ExactClose(object panel, Delegate close)
        {
            return panel != null && close != null && ReferenceEquals(close.Target, panel) && close.GetInvocationList().Length == 1 &&
                close.Method == ExactMethod(panel.GetType(), "_fast_finish_close", Type.EmptyTypes, false);
        }

        private static bool ExactRewardClose(object panel, Delegate close)
        {
            return panel != null && close != null && ReferenceEquals(close.Target, panel) && close.GetInvocationList().Length == 1 &&
                close.Method == ExactMethod(panel.GetType(), "closeRewardResult", Type.EmptyTypes, false);
        }

        private static int RewardBonusCount(byte[] body)
        {
            if (body == null || body.Length == 0 || body.Length > 65536) throw new FormatException();
            Reader reader = new Reader(body, 0, body.Length);
            int count = 0, wire;
            while (!reader.End)
            {
                int field = reader.Tag(out wire);
                if (field != 1) { reader.Skip(wire); continue; }
                if (wire != 2 || ++count > 256) throw new FormatException();
                Reader bonus = reader.Message();
                while (!bonus.End) { bonus.Tag(out wire); bonus.Skip(wire); }
            }
            return count;
        }

        private bool OwnPrompt(Frame f)
        {
            if (resultClosing == null || !resultClosing.ResultFinished || f.Prompt == null || !f.ResultHidden ||
                f.PromptShownAt <= resultClosing.PromptShownAt || D.Read(f.Prompt, "OnOK") != null ||
                D.Read(f.Prompt, "OnClose") != null || Field<bool>(f.Prompt, "bCancel") || Field<bool>(f.Prompt, "isGold")) return false;
            if (receipt.Result.Level == receipt.Result.Next)
            {
                MethodInfo text = D.Method(Model.FirstType("__BaseUI"), "get_string", new[] { typeof(string) }, true, typeof(string));
                return !Field<bool>(f.Prompt, "isPicMode") && D.Read(f.Prompt, "old_rmg") == null &&
                    (string)D.Read(f.Prompt, "Message") == (string)text.Invoke(null, new object[] { "no_dummy_troop" });
            }
            return Field<bool>(f.Prompt, "isPicMode") && ReferenceEquals(D.Read(f.Prompt, "titlePic"), D.Read(f.Panel, "zi10")) &&
                ReferenceEquals(D.Read(f.Prompt, "old_rmg"), D.Read(f.Panel, "rmg"));
        }

        private static DevelopmentPublication BindStock(Frame f, string action)
        {
            Type main = Model.GameType("MainUI");
            if (action == "open")
            {
                ReplyMethod(f.Panel.GetType(), Info);
                MethodInfo open = ExactMethod(main, "_muren", Type.EmptyTypes, true);
                return delegate { open.Invoke(null, null); };
            }
            if (action == "fast")
            {
                ReplyMethod(f.Panel.GetType(), Fast);
                MethodInfo fast = ExactMethod(f.Panel.GetType(), "cs_dummy_challenge_fast_complete", Type.EmptyTypes, false);
                return delegate { fast.Invoke(f.Panel, null); };
            }
            if (action == "claim_reward")
            {
                ReplyMethod(f.Panel.GetType(), Reward);
                MethodInfo claim = ExactMethod(f.Panel.GetType(), "cs_dummy_challenge_get_reward", Type.EmptyTypes, false);
                return delegate { claim.Invoke(f.Panel, null); };
            }
            if (action == "close_reward")
            {
                if (!ExactRewardClose(f.Panel, f.RewardClose)) return null;
                return delegate { D.Call(f.RewardUi, "Hide"); f.RewardClose.DynamicInvoke(); };
            }
            if (action == "dismiss_prompt")
            {
                MethodInfo dismiss = D.Method(f.Prompt.GetType(), "ButtonOK", new[] { typeof(bool) }, false, typeof(void));
                return delegate { dismiss.Invoke(f.Prompt, new object[] { false }); };
            }
            if (action == "close_result")
            {
                if (!ExactClose(f.Panel, f.ResultClose)) return null;
                Component ui = (Component)f.Result;
                MethodInfo hide = D.Method(ui.GetType(), "Hide", Type.EmptyTypes, false, typeof(void));
                return delegate { hide.Invoke(ui, null); f.ResultClose.DynamicInvoke(); };
            }
            MethodInfo close = ExactMethod(main, "SetSubUI", new[] { Model.FirstType("__BaseUI") }, true);
            return delegate { close.Invoke(null, new object[] { null }); };
        }

        internal static uint QuotaEpoch(uint now, uint day)
        {
            if (day == 0 || now < day || now >= day + 86400L) return 0;
            long cutoff = day + 72000L;
            long current = now >= cutoff ? cutoff : cutoff - 86400L;
            return current > 0 && current <= UInt32.MaxValue ? (uint)current : 0;
        }

        private string AttemptKey(Frame f)
        { return f.Role.ToString(CultureInfo.InvariantCulture) + "/" + f.Sid.ToString(CultureInfo.InvariantCulture) + "/" +
            f.Cycle.ToString(CultureInfo.InvariantCulture) + "/" + f.Epoch.ToString(CultureInfo.InvariantCulture) + "/" +
            rewardsClaimed.ToString(CultureInfo.InvariantCulture) + "/" + f.Player.Quota.ToString(CultureInfo.InvariantCulture); }

        private bool StillCurrent(Frame before, string action)
        {
            uint now, day, cycle;
            bool auxiliary = action == "close_reward" || action == "dismiss_prompt" || action == "close_result";
            if (!D.Gui || !ReferenceEquals(before.Client, DailyTransport.CurrentClient(before.Character)) ||
                !before.Transport.StillCurrent() || Field<ulong>(before.Character, "id") != before.Role ||
                Field<uint>(before.Character, "SID") != before.Sid || Field<uint>(before.Character, "next_resource_time") != before.Next ||
                DailyRefreshGate.Read(before.Character, out now, out day, out cycle) != "" || now < before.Now ||
                cycle != before.Cycle || QuotaEpoch(now, day) != before.Epoch ||
                !ReferenceEquals(before.Loader, D.Static(Model.FirstType("BaseAsset"), "lds")) ||
                !ReferenceEquals(D.Read(before.Loader, "currmg"), before.Root) || Field<bool>(before.Loader, "isBattling") ||
                ((Behaviour)before.Loader).enabled || !ReferenceEquals(D.Read(before.GlobalRoot, "level_rmg"), before.Root) ||
                !auxiliary && !D.Bool(D.Static(Model.FirstType("Global"), "extern_enabled")) ||
                !ReferenceEquals(before.Context.SubUi, before.Sub) ||
                !ReferenceEquals(D.Read(before.Panel, "m_battleUI"), before.Battle) || Field<bool>(before.Battle, "bBattle_fighting") ||
                !ReferenceEquals(D.Read(before.Character, "dummy_challenge_status"), before.StatusCache) ||
                Field<int>(before.Character, "gold") != before.Gold || Field<int>(before.Character, "bind_gold") != before.BoundGold ||
                Field<uint>(before.Character, "gold_consume") != before.GoldConsumed) return false;

            Frame network = new Frame { Client = before.Client };
            ReadNetwork(network, activity);
            if (!network.Idle || network.InfoHandler != null || network.FastHandler != null || network.RewardHandler != null ||
                !ReferenceEquals(network.PushHandler, before.PushHandler)) return false;
            Frame task = new Frame { Character = before.Character };
            ReadActivity(task);
            if (TaskConfirmed && (action == "close_result" || action == "dismiss_prompt"))
            { if (!ProgressMatches(task)) return false; }
            else if (TaskConfirmed)
            {
                if (!task.SameCounterValues(before) || task.Row == null || !ReferenceEquals(task.Row, task.ListedRow)) return false;
            }
            else if (!task.SameCount(before)) return false;
            Component panel = (Component)before.Panel, result = (Component)before.Result;
            if (D.Loading(panel) || D.Loading(result) || Field<DateTime>(panel, "visible_time") != before.PanelShownAt ||
                Field<DateTime>(result, "visible_time") != before.ResultShownAt ||
                !ReferenceEquals(D.Read(result, "resultStatus"), before.ResultCache) ||
                !ReferenceEquals(D.Read(result, "close_func"), before.ResultClose)) return false;
            if (action == "open") return !((Behaviour)panel).enabled && !((Behaviour)result).enabled &&
                D.Enabled((Component)before.Main) && CommitBoundary(before);
            if (!auxiliary && !D.Enabled(panel)) return false;
            if ((action == "fast" || action == "claim_reward") && (!ReferenceEquals(D.Read(before.StatusCache, "_player"), before.PlayerCache) ||
                !before.Player.Same(DailyWoodPlayer.FromStock(before.PlayerCache)) || Field<int>(panel, "temptime") != before.FinishTimestamp))
                return false;
            if (UiGate(before, auxiliary) != "PASS") return false;
            if (action == "close_reward") return before.RewardReady && OwnReward(before) &&
                ReferenceEquals(D.Read(before.RewardUi, "OnClose"), before.RewardClose) &&
                Field<DateTime>(before.RewardUi, "visible_time") == before.RewardShownAt && CommitBoundary(before);
            if (action == "dismiss_prompt") return before.PromptReady && OwnPrompt(before) &&
                Field<DateTime>(before.Prompt, "visible_time") == before.PromptShownAt && CommitBoundary(before);
            bool ready = action == "close_result" ? D.Visible(result) &&
                before.ResultPlayer.Same(DailyWoodPlayer.FromStock(before.ResultCache)) &&
                Field<bool>(result, "used_all_turns") == before.AllTurns && Field<int>(result, "total_lose_num") == before.Lose &&
                ExactClose(panel, before.ResultClose) : !((Behaviour)result).enabled;
            return ready && CommitBoundary(before);
        }

        private static string UiGate(Frame f, bool auxiliary)
        {
            Component panel = (Component)f.Panel, result = (Component)f.Result;
            return auxiliary ? f.Context.RewardPanels(panel, result, f.RewardUi, f.Prompt) :
                f.Context.Panels(panel, result, f.RewardUi, f.Prompt);
        }

        private static bool CommitBoundary(Frame before)
        {
            // Cache and UI assessment above can outlast the connection/GUI
            // sample taken at its start. No cache traversal follows this.
            uint now, day, cycle;
            return before.Transport.StillCurrent() && ReferenceEquals(before.Client, DailyTransport.CurrentClient(before.Character)) &&
                Field<ulong>(before.Character, "id") == before.Role && Field<uint>(before.Character, "SID") == before.Sid &&
                DailyRefreshGate.ReadClock(before.Character, out now, out day, out cycle) == "" && now >= before.Now &&
                cycle == before.Cycle && QuotaEpoch(now, day) == before.Epoch &&
                Field<uint>(before.Character, "next_resource_time") == before.Next &&
                ReferenceEquals(before.Context.SubUi, before.Sub) && D.Gui;
        }

        private Frame Read(D context) { return Read(context, true, activity); }
        private static Frame Read(D context, bool assessUi, DailyWoodTap ownedPush = null)
        {
            Frame f = new Frame { Context = context, Tick = Environment.TickCount };
            if (context == null) return f;
            try
            {
                f.Character = context.Character; f.Client = context.Client;
                f.Root = context.Root; f.GlobalRoot = context.GlobalUi;
                f.Role = Field<ulong>(f.Character, "id"); f.Sid = Field<uint>(f.Character, "SID");
                if (f.Role == 0 || f.Sid == 0 || !ReferenceEquals(f.Client, DailyTransport.CurrentClient(f.Character))) return f;
                f.Transport = DailyTransport.Read(f.Client);
                f.Loader = D.Static(Model.FirstType("BaseAsset"), "lds");
                if (f.Transport == null || !(f.Loader is Behaviour) || !ReferenceEquals(D.Read(f.Loader, "currmg"), f.Root) ||
                    Field<bool>(f.Loader, "isBattling") || !ReferenceEquals(D.Read(f.GlobalRoot, "level_rmg"), f.Root) ||
                    !context.SameOwnership(context)) return f;
                uint day;
                if (DailyRefreshGate.Read(f.Character, out f.Now, out day, out f.Cycle) != "") return f;
                f.Epoch = QuotaEpoch(f.Now, day);
                f.Next = Field<uint>(f.Character, "next_resource_time");
                if (f.Cycle == 0 || f.Epoch == 0 || f.Next != f.Cycle + 86400L) return f;
                f.Main = MainInScene(context);
                f.Panel = context.Find("WoodWarUI");
                f.Battle = D.Read(f.Panel, "m_battleUI");
                Component battle = f.Battle as Component;
                if (battle == null || battle.GetType() != Model.GameType("BattleUI") ||
                    !ReferenceEquals(battle, Unique(context, battle.gameObject, "BattleUI")) ||
                    battle.gameObject != GameObject.Find("Battle/BattleUI") || Field<bool>(battle, "bBattle_fighting")) return f;
                f.Result = Unique(context, battle.gameObject, "BattleWoodwarResultUI");
                f.RewardUi = context.GrowthRewardDialog();
                f.Prompt = D.Static(Model.FirstType("__BaseUI"), "msgBox") as Component;
                if (f.Prompt != null && (f.Prompt.GetType() != Model.FirstType("MessageBox") ||
                    f.Prompt.gameObject != ((Component)f.GlobalRoot).gameObject)) return f;
                if (f.RewardUi != null)
                {
                    f.RewardClose = D.Read(f.RewardUi, "OnClose") as Delegate;
                    f.RewardBonuses = D.Read(f.RewardUi, "m_vctBonuses");
                    f.RewardShownAt = Field<DateTime>(f.RewardUi, "visible_time");
                    f.RewardHidden = !D.Visible(f.RewardUi);
                    f.RewardReady = !f.RewardHidden && D.Enabled(f.RewardUi) && !D.Loading(f.RewardUi);
                }
                if (f.Prompt != null)
                {
                    f.PromptShownAt = Field<DateTime>(f.Prompt, "visible_time");
                    f.PromptHidden = !D.Visible(f.Prompt);
                    f.PromptReady = !f.PromptHidden && D.Enabled(f.Prompt);
                }
                f.Sub = context.SubUi;
                f.Loading = ((Behaviour)f.Loader).enabled || D.Loading((Component)f.Panel) || D.Loading((Component)f.Result) ||
                    f.RewardUi != null && D.Loading(f.RewardUi);
                f.Valid = true;
                f.Gold = Field<int>(f.Character, "gold"); f.BoundGold = Field<int>(f.Character, "bind_gold");
                f.GoldConsumed = Field<uint>(f.Character, "gold_consume");
                f.StatusCache = D.Read(f.Character, "dummy_challenge_status");
                f.PlayerCache = f.StatusCache == null ? null : D.Read(f.StatusCache, "_player");
                if (f.PlayerCache != null && DailyWoodPlayer.MatchesRole(Field<string>(f.PlayerCache, "_name"),
                    Field<uint>(f.PlayerCache, "_sid"), Field<string>(f.Character, "name"), f.Sid))
                    f.Player = DailyWoodPlayer.FromStock(f.PlayerCache);
                f.FinishTimestamp = Field<int>(f.Panel, "temptime");
                f.ResultCache = D.Read(f.Result, "resultStatus");
                f.ResultPlayer = f.ResultCache == null ? null : DailyWoodPlayer.FromStock(f.ResultCache);
                f.ResultClose = D.Read(f.Result, "close_func") as Delegate;
                f.ResultFinished = Field<bool>(f.Result, "resultChallengeLevelFinished");
                f.AllTurns = Field<bool>(f.Result, "used_all_turns");
                f.Lose = Field<int>(f.Result, "total_lose_num");
                f.PanelShownAt = Field<DateTime>(f.Panel, "visible_time"); f.ResultShownAt = Field<DateTime>(f.Result, "visible_time");
                ReadActivity(f);
                ReadNetwork(f, ownedPush);
                if (!assessUi) return f;
                Component panel = (Component)f.Panel, result = (Component)f.Result, main = (Component)f.Main;
                f.PanelHidden = !((Behaviour)panel).enabled;
                f.ResultHidden = !((Behaviour)result).enabled;
                if (f.Loading) return f;
                f.Home = f.Sub == null && f.PanelHidden && f.ResultHidden && f.RewardHidden && f.PromptHidden && D.Enabled(main) && context.Panels() == "PASS";
                // BattleWoodwarResultUI's stock OK uses Button(bBtn: true),
                // not IsEnable(). Its owned modal can be closed while the
                // underlying wood page/global input is disabled.
                bool resultVisible = D.Visible(result);
                bool auxiliary = f.RewardReady || f.PromptReady || resultVisible;
                f.PanelReady = ReferenceEquals(f.Sub, panel) && (D.Enabled(panel) || auxiliary) && UiGate(f, auxiliary) == "PASS";
                f.ResultReady = f.PanelReady && resultVisible;
            }
            catch (Exception) { f.Valid = f.TaskValid = f.Idle = false; }
            return f;
        }

        private static void ReadActivity(Frame f)
        {
            IDictionary rows = Map(D.Read(f.Character, "acts"), 255), rewards = Map(D.Read(f.Character, "act_rewards"), 6);
            f.Rows = rows; f.CategoryCache = rewards[(uint)Category];
            if (f.CategoryCache == null || f.CategoryCache.GetType() != Model.FirstType("Database.Activity_Reward") ||
                Field<byte>(f.CategoryCache, "idact_reward") != Category) return;
            IDictionary listed = Map(D.Read(f.CategoryCache, "acts"), 255);
            f.CategoryRows = listed; f.Row = rows[(uint)TaskId]; f.ListedRow = listed[(uint)TaskId];
            if (f.Row == null || f.ListedRow == null || f.Row.GetType() != Model.FirstType("Database.Activity") ||
                f.ListedRow.GetType() != f.Row.GetType() || Field<byte>(f.Row, "idactivity") != TaskId ||
                Field<byte>(f.ListedRow, "idactivity") != TaskId) return;
            f.TaskTemplate = D.Read(f.Row, "temp");
            if (f.TaskTemplate == null || !ReferenceEquals(f.TaskTemplate, D.Template("activity", TaskId)) ||
                !ReferenceEquals(f.TaskTemplate, D.Read(f.ListedRow, "temp")) || Field<byte>(f.TaskTemplate, "id") != TaskId ||
                Field<byte>(f.TaskTemplate, "idreward") != Category || Field<int>(f.TaskTemplate, "cond_param") != 1 ||
                !Field<bool>(f.TaskTemplate, "active")) return;
            f.Count = Field<byte>(f.Row, "condition_param"); f.Listed = Field<byte>(f.ListedRow, "condition_param");
            f.Refresh = Field<int>(f.Row, "refresh_time"); f.ListedRefresh = Field<int>(f.ListedRow, "refresh_time");
            f.TaskValid = f.Count <= 1 && f.Count == f.Listed && f.Refresh >= 0 && f.Refresh == f.ListedRefresh;
        }

        private static void ReadNetwork(Frame f, DailyWoodTap ownedPush)
        {
            IDictionary handlers = Map(D.Read(f.Client, "m_handlers"), 512);
            f.InfoHandler = handlers[Info]; f.FastHandler = handlers[Fast]; f.RewardHandler = handlers[Reward];
            object push = handlers[Activity];
            if (push != null && (ownedPush != null && ownedPush.Attached && ownedPush.Owns(push) ||
                DevelopmentCallbackCapture.IsDailyActivityPush(push, D.Read(push, "handler") as Delegate))) f.PushHandler = push;
            IList messages = D.Read(f.Client, "messages") as IList, sends = D.Read(f.Client, "m_sendDatas") as IList;
            if (messages == null || sends == null || messages.Count != 0 || sends.Count != 0 || DevelopmentCallbackCapture.AnyCallbackExecuting) return;
            Type type = Model.FirstType("game.net.tcp_base+Handler");
            foreach (DictionaryEntry entry in handlers)
            {
                if (!(entry.Key is ushort) || entry.Value == null || entry.Value.GetType() != type) return;
                float life = Field<float>(entry.Value, "life");
                if (Single.IsNaN(life) || Single.IsInfinity(life) || life >= 0f) return;
            }
            f.Idle = true;
        }

        private static Component Unique(D context, GameObject owner, string typeName)
        {
            Type type = Model.GameType(typeName);
            Component[] matching = owner.GetComponents(type);
            if (matching.Length != 1 || matching[0] == null || matching[0].GetType() != type)
                throw new ProbeFailure("wood_ui_not_unique:" + typeName);
            foreach (UnityEngine.Object item in context.All)
                if (ReferenceEquals(item, matching[0])) return matching[0];
            throw new ProbeFailure("wood_ui_not_observed:" + typeName);
        }

        private static Component MainInScene(D context)
        {
            Type type = Model.GameType("MainUI");
            Component found = null;
            foreach (UnityEngine.Object item in context.All)
            {
                Behaviour candidate = item as Behaviour;
                if (candidate == null || candidate.GetType() != type || !candidate.enabled || !candidate.gameObject.activeInHierarchy ||
                    candidate.transform.root != context.Root.transform || !ReferenceEquals(D.Read(candidate, "rmg"), context.Root)) continue;
                if (found != null) throw new ProbeFailure("wood_scene_main_not_unique");
                found = candidate;
            }
            if (found == null || Field<bool>(found, "first") || !Field<bool>(found, "m_bInit"))
                throw new ProbeFailure("wood_scene_main_unavailable");
            return found;
        }

        private static IDictionary Map(object data, int limit)
        {
            IDictionary map = data as IDictionary;
            if (map == null || map.Count > limit) throw new ProbeFailure("wood_map_schema");
            return map;
        }
        private static T Field<T>(object value, string field) { return (T)D.Read(value, field); }

        internal string Json()
        {
            string action = "";
            if (queued == null && frame != null)
            {
                string candidate = !Busy ? "open" : Status == "READY" ?
                    frame.Player != null && frame.Player.CanTake ? "claim_reward" : "fast" :
                    Status == "RESULT" ? "close_result" : Status == "REWARD" ? "close_reward" :
                    Status == "PROMPT" ? "dismiss_prompt" : "close_panel";
                if (Gate(frame, candidate) == "") action = candidate;
            }
            return "{\"schema\":1,\"revision\":" + Revision + ",\"role_id\":" + D.Q((frame == null ? 0UL : frame.Role).ToString(CultureInfo.InvariantCulture)) +
                ",\"operation_id\":" + D.Q(operation == null ? "" : operation.Id) + ",\"status\":" + D.Q(Status) + ",\"reason\":" + D.Q(Reason) +
                ",\"next_action\":" + D.Q(action) + ",\"activity_count\":" + (frame == null || !frame.TaskValid ? "null" : frame.Count.ToString(CultureInfo.InvariantCulture)) +
                ",\"fast_quota\":" + (ready == null && infoReturned == null || frame == null || frame.Player == null ? "null" : frame.Player.Quota.ToString(CultureInfo.InvariantCulture)) +
                ",\"reward_pending\":" + D.B(frame != null && frame.Player != null && frame.Player.CanTake) +
                ",\"reward_confirmed\":" + D.B(rewardConfirmed) +
                ",\"free_attempts\":" + freeAttempts.ToString(CultureInfo.InvariantCulture) +
                ",\"rewards_claimed\":" + rewardsClaimed.ToString(CultureInfo.InvariantCulture) +
                ",\"refresh_cycle\":" + (frame == null || !frame.Valid ? "null" : frame.Cycle.ToString(CultureInfo.InvariantCulture)) +
                ",\"quota_epoch\":" + (frame == null || !frame.Valid ? "null" : frame.Epoch.ToString(CultureInfo.InvariantCulture)) +
                ",\"mutation_attempted\":" + D.B(MutationAttempted) + ",\"task_confirmed\":" + D.B(TaskConfirmed) +
                ",\"cleanup_pending\":" + D.B(CleanupPending) + ",\"busy\":" + D.B(Busy) +
                ",\"evidence\":{\"fast_receipt_state\":" + (fastReturned == null || receipt == null ? "null" : receipt.State.ToString(CultureInfo.InvariantCulture)) +
                ",\"activity_before\":" + (sent == null ? "null" : sent.Count.ToString(CultureInfo.InvariantCulture)) +
                ",\"activity_progress_observed\":" + D.B(activityAdvanced) +
                ",\"result_ready\":" + D.B(frame != null && frame.ResultReady) +
                ",\"panel_ready\":" + D.B(frame != null && frame.PanelReady) +
                ",\"network_idle\":" + D.B(frame != null && frame.Idle) + "}}";
        }

#if JSB_DAILY_WOOD_TEST
        internal delegate void TestReply(byte[] body, Frame after);
        internal delegate DailyWoodTap TestTapBinder(Frame f, ushort opcode, TestReply after);
        internal bool ExecuteForTest(FrameProvider fresh, DevelopmentPublication stock, StockGate current,
            TestTapBinder fixture, DailyWoodAdmission admit, out string why)
        {
            return ExecuteGuiCore(fresh, delegate { return stock; }, current, delegate(Frame owner, ushort opcode)
            {
                return fixture(owner, opcode, delegate(byte[] body, Frame after) { OnReply(owner, opcode, body, after); });
            }, delegate { }, admit, true, out why);
        }
#endif
    }

    // Pinned protobuf projection. Name identity comes from the cache just
    // rebuilt by the exact original stock callback, which decoded that field.
    internal sealed class DailyWoodPlayer
    {
        internal uint Rank, Level, Wave, Enemy, Damage, Next, RoleLevel, Sid, Quota;
        internal bool CanTake;

        internal bool Same(DailyWoodPlayer other)
        {
            return other != null && Rank == other.Rank && Level == other.Level && Wave == other.Wave && Enemy == other.Enemy &&
                Damage == other.Damage && Next == other.Next && RoleLevel == other.RoleLevel && Sid == other.Sid &&
                Quota == other.Quota && CanTake == other.CanTake;
        }

        internal static DailyWoodPlayer FromStock(object value)
        {
            FieldInfo result = Model.GameType("BattleWoodwarResultUI").GetField("resultStatus", D.Instance);
            if (value == null || result == null || result.FieldType.FullName != "game.net.data.StoC.CharacterDummyChallenge" ||
                value.GetType() != result.FieldType)
                throw new ProbeFailure("wood_player_stock_schema");
            return new DailyWoodPlayer
            {
                Rank = (uint)D.Read(value, "_rank"), Level = (uint)D.Read(value, "_challenge_level"), Wave = (uint)D.Read(value, "_wave_count"),
                Enemy = (uint)D.Read(value, "_enemy_count"), Damage = (uint)D.Read(value, "_total_damage"), Next = (uint)D.Read(value, "_next_challenge_level"),
                RoleLevel = (uint)D.Read(value, "_level"), Sid = (uint)D.Read(value, "_sid"), Quota = (uint)D.Read(value, "_fast_challenge_left_times"),
                CanTake = (bool)D.Read(value, "_can_take")
            };
        }

        internal static bool MatchesRole(string name, uint sid, string roleName, uint roleSid)
        {
            // Both fields are optional in CharacterDummyChallenge. The exact
            // reply and current role/stream establish ownership when omitted.
            return roleSid != 0 && (sid == 0 || sid == roleSid) &&
                (String.IsNullOrEmpty(name) || name == roleName);
        }

        internal static DailyWoodPlayer Decode(Reader reader)
        {
            uint[] values = new uint[13];
            int seen = 0, wire;
            while (!reader.End)
            {
                int field = reader.Tag(out wire);
                if (field <= 12 && field != 9)
                {
                    DailyWoodPacket.Mark(ref seen, field);
                    if (field != 8) { values[field] = DailyWoodPacket.UInt(reader, wire); continue; }
                    if (wire != 2) throw new FormatException();
                }
                reader.Skip(wire);
            }
            if ((seen & 62) != 62 || values[10] > 1) throw new FormatException();
            return new DailyWoodPlayer { Rank = values[1], Level = values[2], Wave = values[3], Enemy = values[4], Damage = values[5],
                Next = values[6], RoleLevel = values[7], CanTake = values[10] != 0, Quota = values[11], Sid = values[12] };
        }
    }

    internal sealed class DailyWoodPacket
    {
        internal DailyWoodPlayer Player, Result;
        internal uint State = 2, Finish;
        internal int Lose;
        internal bool LevelFinished, AllTurns;

        internal static DailyWoodPacket Decode(ushort opcode, byte[] body)
        {
            bool info = opcode == DailyWoodModel.Info;
            if ((!info && opcode != DailyWoodModel.Fast) || body == null || body.Length == 0 || body.Length > 65536)
                throw new FormatException();
            Reader reader = new Reader(body, 0, body.Length);
            DailyWoodPacket packet = new DailyWoodPacket();
            int seen = 0, wire;
            while (!reader.End)
            {
                int field = reader.Tag(out wire);
                if (field > (info ? 2 : 6)) { reader.Skip(wire); continue; }
                Mark(ref seen, field);
                if (field == 1 || !info && field == 2)
                {
                    if (wire != 2) throw new FormatException();
                    Reader nested = reader.Message();
                    if (field == 1) packet.Player = ReadStatus(nested);
                    else packet.Result = DailyWoodPlayer.Decode(nested);
                    continue;
                }
                uint value = UInt(reader, wire);
                if (info) packet.Finish = value;
                else if (field == 4) packet.State = value;
                else if (field == 6) packet.Lose = (int)(value >> 1) ^ -(int)(value & 1u);
                else
                {
                    if (value > 1) throw new FormatException();
                    if (field == 3) packet.LevelFinished = value == 1; else packet.AllTurns = value == 1;
                }
            }
            if (packet.State < 1 || packet.State > 3) throw new FormatException();
            return packet;
        }

        private static DailyWoodPlayer ReadStatus(Reader reader)
        {
            DailyWoodPlayer player = null;
            int wire;
            while (!reader.End)
            {
                int field = reader.Tag(out wire);
                if (field != 1) { reader.Skip(wire); continue; }
                if (wire != 2 || player != null) throw new FormatException();
                player = DailyWoodPlayer.Decode(reader.Message());
            }
            return player;
        }

        internal static uint UInt(Reader reader, int wire)
        {
            if (wire != 0) throw new FormatException();
            ulong value = reader.Varint();
            if (value > UInt32.MaxValue) throw new FormatException();
            return (uint)value;
        }

        internal static void Mark(ref int seen, int field)
        {
            int bit = 1 << field;
            if ((seen & bit) != 0) throw new FormatException();
            seen |= bit;
        }
    }

    // The pinned tcp_base.Run dispatches under the client monitor. It removes
    // one-shot registrations before a non-null delivery. An expired handler
    // can instead invoke with null before removal. Always forward the exact
    // original call, including malformed, late and disarmed deliveries.
    internal sealed class DailyWoodTap
    {
        private readonly object client, slot;
        private readonly IDictionary slots;
        private readonly ushort opcode;
        private readonly FieldInfo field;
        private readonly Delegate original, wrapper;
        private readonly DevelopmentNormalCallback forward;
        private readonly Action<byte[]> after;
        private readonly int thread;
        private int executing;
        private volatile bool armed = true;
        internal int Calls { get; private set; }
        internal string Error { get; private set; }
        internal bool Returned { get; private set; }

        private DailyWoodTap(object client, IDictionary slots, ushort opcode, object slot, FieldInfo field, Delegate original, Action<byte[]> after)
        {
            this.client = client; this.slots = slots; this.opcode = opcode; this.slot = slot;
            this.field = field; this.original = original; this.after = after;
            thread = Thread.CurrentThread.ManagedThreadId;
            forward = (DevelopmentNormalCallback)(original.Method.IsStatic ?
                Delegate.CreateDelegate(typeof(DevelopmentNormalCallback), original.Method) :
                Delegate.CreateDelegate(typeof(DevelopmentNormalCallback), original.Target, original.Method));
            wrapper = Delegate.CreateDelegate(original.GetType(), this, typeof(DailyWoodTap).GetMethod("Dispatch", D.Instance));
        }

        internal static DailyWoodTap Attach(object client, IDictionary slots, ushort opcode, object slot, Delegate original, Action<byte[]> after)
        {
            if (original == null || original.GetInvocationList().Length != 1 || !Monitor.TryEnter(client, 0)) return null;
            try
            {
                FieldInfo field = slot.GetType().GetField("handler", D.Instance);
                if (field == null || !ReferenceEquals(slots[opcode], slot) || !ReferenceEquals(field.GetValue(slot), original)) return null;
                DailyWoodTap tap = new DailyWoodTap(client, slots, opcode, slot, field, original, after);
                field.SetValue(slot, tap.wrapper);
                return tap;
            }
            finally { Monitor.Exit(client); }
        }

        internal bool Owns(object item) { return ReferenceEquals(item, slot); }
        internal bool Attached
        {
            get { return armed && Owns(slots[opcode]) && ReferenceEquals(field.GetValue(slot), wrapper); }
        }

        private void Dispatch(MemoryStream stream, object argument)
        {
            Interlocked.Increment(ref executing);
            bool locked = false;
            try
            {
                byte[] bytes = null;
                if (armed)
                {
                    try
                    {
                        locked = Monitor.TryEnter(client, 0);
                        bool push = opcode == DailyWoodModel.Activity;
                        if (!locked || Thread.CurrentThread.ManagedThreadId != thread || argument != null ||
                            (push ? !Attached : slots[opcode] != null)) Error = "wood_callback_registration_or_thread";
                        else if (++Calls > (push ? 32 : 1)) Error = "wood_callback_duplicate_or_bound";
                        else if (stream == null) Error = "wood_callback_null";
                        else if (stream.GetType() != typeof(MemoryStream) || !stream.CanRead ||
                            stream.Position != 0 || stream.Length < 1 || stream.Length > 65536) Error = "wood_callback_stream";
                        else bytes = stream.ToArray();
                    }
                    catch (Exception) { Error = "wood_callback_observation_failed"; }
                }
                try { forward(stream, argument); }
                catch { if (armed) Error = "wood_original_callback_threw"; throw; }
                if (armed && Error == null && bytes != null)
                {
                    try { after(bytes); Returned = true; }
                    catch (Exception error)
                    { Error = error is ProbeFailure ? error.Message : "wood_callback_evidence_invalid"; }
                }
            }
            finally
            {
                if (locked) Monitor.Exit(client);
                Interlocked.Decrement(ref executing);
            }
        }

        internal bool TryRestore()
        {
            armed = false;
            if (!Monitor.TryEnter(client, 0)) return false;
            try
            {
                if (Interlocked.CompareExchange(ref executing, 0, 0) != 0) return false;
                if (!ReferenceEquals(field.GetValue(slot), wrapper)) return true;
                field.SetValue(slot, original);
                return ReferenceEquals(field.GetValue(slot), original);
            }
            catch (Exception) { return false; }
            finally { Monitor.Exit(client); }
        }
    }
}
