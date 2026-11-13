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
    internal delegate D DailyPointRaceContextProvider();
    internal delegate bool DailyPointRaceInvocationCheck();
    internal delegate bool DailyPointRaceInvocationGate(DailyPointRaceInvocationCheck sample, DailyPointRaceInvocationCheck check);

    // 109 is an assistant route, not a server activity ID. PointRace has its
    // own ten natural attempts and no corresponding daily activity row.
    internal sealed class DailyPointRaceModel
    {
        internal const int TaskId = 109, Required = 10;
        private const ushort EnterOp = 1124, BattleOp = 1125, RefreshOp = 1127;
        private const uint PendingActionTimeoutMilliseconds = 15000, CleanupTimeoutMilliseconds = 5000;
        private sealed class Frame
        {
            internal D Context;
            internal object Role, Client, Race, Loader, CurrentRoot, Sub, Target, Candidates;
            internal Component Select, Panel, Scene, Main, HomeRoot, ReturnRoot;
            internal object ReturnClose;
            internal DailyTransport Transport;
            internal ulong RoleId, Opponent;
            internal uint Now, Day, Cycle, Cooldown, GoldConsumed, Score, PlayerScore;
            internal string CandidateState = "";
            internal int Count, Bought, Refreshes, Gold, Bound, Index = -1;
            internal bool Home, InScene, Loading, Battling, Open;
            internal bool SameOwner(Frame old)
            { return old != null && ReferenceEquals(Role, old.Role) && ReferenceEquals(Client, old.Client) &&
                ReferenceEquals(Race, old.Race) && ReferenceEquals(Loader, old.Loader) &&
                ReferenceEquals(Context.Root, old.Context.Root) && ReferenceEquals(HomeRoot, old.HomeRoot) &&
                ReferenceEquals(Context.GlobalUi, old.Context.GlobalUi) && ReferenceEquals(Context.UiRoot, old.Context.UiRoot) &&
                RoleId == old.RoleId && Day == old.Day && Cycle == old.Cycle && Transport.Same(old.Transport); }
            internal bool SamePremium(Frame old)
            { return Gold == old.Gold && Bound == old.Bound && GoldConsumed == old.GoldConsumed && Bought == old.Bought; }
            internal bool SameOffer(Frame old)
            { return SameOwner(old) && SamePremium(old) && Count == old.Count && Refreshes == old.Refreshes &&
                Cooldown == old.Cooldown && Open == old.Open && ReferenceEquals(CurrentRoot, old.CurrentRoot) &&
                ReferenceEquals(Panel, old.Panel) && ReferenceEquals(Scene, old.Scene) &&
                ReferenceEquals(ReturnRoot, old.ReturnRoot) && ReferenceEquals(ReturnClose, old.ReturnClose) &&
                ReferenceEquals(Sub, old.Sub) && ReferenceEquals(Target, old.Target) && ReferenceEquals(Candidates, old.Candidates) &&
                Index == old.Index && Opponent == old.Opponent && Score == old.Score && PlayerScore == old.PlayerScore &&
                CandidateState == old.CandidateState && Home == old.Home &&
                InScene == old.InScene && Loading == old.Loading && Battling == old.Battling; }
        }
        private Frame frame, queued, owner, baseline, entryReply, progressBaseline;
        private string baselineSource = "";
        private string failureReason = "", cleanupReason = "";
        private bool entryTimeoutCleanup, cleanupAttempted, entryCallbackReturned, entryListenerReleased;
        private int entrySequence, baselineSequence, cleanupStarted;
        private string id = "", action = "", next = "", status = "IDLE", reason = "", signature = "", finishReason = "";
        private int revision, sequence, started, challengesSubmitted, challengesCompleted, challengeSequence, completedSequence, completedCount;
        private bool touched, attempted, entered, closed, stopped, normalStopDrain, available, returning, battleClosed, skipped, battleCallbackReturned, settlementInvalid;
        private object battleReport;
        private Component battle, result, ownedPanel, ownedScene;
        private ReplyTap enterTap, firstEnterTap, battleTap;
        private Receipt receipt;
        private Receipt completedReceipt;
        private readonly Hashtable completedReports = new Hashtable();
        private RefreshRequest refresh;
        private bool refreshAttempted;
        private bool freshHomeSample;
        private bool challengeRecovery, challengeRejectedIdle, challengeRejectedTransient;
        private int challengeRecoveryStarted, challengeRejectedSequence;
        private string challengeRejectedReason = "";
        private int capturedFrame = -1;
        internal bool Busy { get { return queued != null || touched || enterTap != null || battleTap != null ||
            firstEnterTap != null && !firstEnterTap.Returned || refresh != null && !refresh.Returned; } }
        private bool CleanupPending { get { return Busy || failureReason != "" && !closed; } }
        private bool ReleaseVerified
        { get { return freshHomeSample && capturedFrame == Time.frameCount && !stopped &&
            (failureReason == "" && status != "UNKNOWN" || EntryTimeoutReleaseReady()) &&
            HomeReady() && !Busy && !CleanupPending &&
            ReferenceEquals(frame.Context.Root, frame.HomeRoot) && frame.Context.Panels() == "PASS" &&
            (id == "" && owner == null && !entered && entrySequence == 0 && challengesSubmitted == 0 &&
                !refreshAttempted || closed); } }

        internal void Capture(D context)
        {
            if (stopped) { Detach(); return; }
            available = false;
            freshHomeSample = false;
            capturedFrame = -1;
            try
            {
                frame = Read(context); available = true;
                capturedFrame = Time.frameCount;
                freshHomeSample = ReferenceEquals(frame.Context, context);
                if (owner != null && !closed && (!frame.SameOwner(owner) || !frame.SamePremium(owner)))
                    throw new ProbeFailure("pointrace_owner_cycle_or_premium_changed");
                if (ownedPanel != null && !returning && !closed &&
                    (!ReferenceEquals(frame.Panel, ownedPanel) || !ReferenceEquals(frame.Scene, ownedScene)))
                    throw new ProbeFailure("pointrace_owned_scene_changed");
                Observe();
            }
            catch (Exception error)
            {
                if (attempted) settlementInvalid = true;
                Fail(error is ProbeFailure ? error.Message : "pointrace_observation_failed");
                Detach();
            }
            PublishRevision();
        }

        private void Observe()
        {
            next = "";
            Check(enterTap); Check(firstEnterTap); Check(battleTap);
            if (refresh != null && refresh.Error != "") throw new ProbeFailure(refresh.Error);
            if (status == "UNKNOWN")
            { if (entryTimeoutCleanup) ObserveEntryTimeoutCleanup(); return; }
            if (enterTap != null && enterTap.Returned && enterTap.Restore()) enterTap = null;
            if (closed)
            {
                Detach();
                if (ReleaseVerified && frame.Count < Required && frame.Open && frame.Cooldown <= frame.Now)
                    next = "open";
                return;
            }
            if (challengeRecovery && !returning)
            {
                if (!ChallengeIdle(frame)) throw new ProbeFailure("pointrace_rejected_challenge_state_changed");
                if (!challengeRejectedTransient ||
                    unchecked((uint)(Environment.TickCount - challengeRecoveryStarted)) >= CleanupTimeoutMilliseconds)
                {
                    // A refused command did not start a round. Stop retrying,
                    // then use only the owned page's stock return when idle.
                    Finish(challengeRejectedReason);
                    if (!RequestTransportIdle(frame, EnterOp) || !RequestTransportIdle(frame, BattleOp) ||
                        !RefreshTransportIdle(frame)) next = "";
                    return;
                }
            }
            if (touched && unchecked((uint)(Environment.TickCount - started)) >= PendingActionTimeoutMilliseconds)
            {
                if (EntryCallbackPending())
                {
                    Fail("pointrace_entry_callback_timeout"); entryTimeoutCleanup = true;
                    cleanupStarted = Environment.TickCount;
                    Detach(); ObserveEntryTimeoutCleanup(); return;
                }
                throw new ProbeFailure("pointrace_operation_timeout");
            }
            if (queued != null) return;
            if (returning)
            {
                if (frame.Home && ReferenceEquals(frame.Sub, frame.Select) && Ready(frame.Select))
                { Offer("close_selector"); return; }
                if (HomeReady())
                {
                    closed = true; touched = false; challengeRecovery = false;
                    status = challengesCompleted == 0 ? "UNAVAILABLE" : frame.Count >= Required ? "COMPLETED" : "PARTIAL";
                    reason = finishReason; Detach();
                }
                else { status = "PENDING"; reason = "pointrace_returning_home"; }
                return;
            }
            if (!attempted)
            {
                // Only the first owned entry callback may replace the cached
                // pre-entry count. A later capture must agree with its model.
                if (progressBaseline == null && entryReply != null && entryCallbackReturned)
                {
                    Frame settled = Read(null);
                    if (challengesSubmitted != 0 || challengesCompleted != 0 || refreshAttempted ||
                        !settled.SameOwner(entryReply) || !settled.SamePremium(entryReply) ||
                        settled.Count != entryReply.Count || settled.Refreshes != entryReply.Refreshes ||
                        !ReferenceEquals(settled.Panel, entryReply.Panel) || !ReferenceEquals(settled.Scene, entryReply.Scene))
                        throw new ProbeFailure("pointrace_entry_baseline_changed");
                    frame = settled;
                    if (frame.InScene && Ready(frame.Panel) && !frame.Loading && !frame.Battling)
                    { progressBaseline = frame; baselineSource = "entry_callback"; baselineSequence = entrySequence; }
                }
                Frame progress = progressBaseline ?? entryReply ?? owner;
                if (progress != null && frame.Count != progress.Count + challengesCompleted)
                    throw new ProbeFailure("pointrace_between_round_progress_changed");
                if (refreshAttempted)
                {
                    if (refresh == null || !refresh.Returned)
                    { status = "PENDING"; reason = "pointrace_waiting_free_refresh_reply"; return; }
                    if (!refresh.Verified) { Finish("pointrace_free_refresh_rejected"); return; }
                    if (frame.Refreshes != 1) throw new ProbeFailure("pointrace_refresh_count_changed");
                }
                if (EntryCallbackPending())
                { status = "WAITING"; reason = "pointrace_waiting_entry_callback"; return; }
                string stop = !frame.Open ? "pointrace_closed" : frame.Count >= Required ? "pointrace_natural_attempts_exhausted" :
                    frame.Cooldown > frame.Now ? "pointrace_cooldown" : "";
                if (stop != "") { Finish(stop); return; }
                if (frame.Loading || frame.Battling) { status = "WAITING"; reason = "pointrace_loading"; return; }
                if (!touched)
                {
                    if (frame.InScene)
                    {
                        if (CanResume(frame)) Offer("resume");
                        else { status = "UNAVAILABLE"; reason = "pointrace_resume_unavailable"; }
                    }
                    else if (HomeReady() && frame.Context.Gate == "PASS") Offer("open");
                    else { status = "UNAVAILABLE"; reason = "pointrace_home_required"; }
                }
                else if (frame.Home && ReferenceEquals(frame.Sub, frame.Select) && Ready(frame.Select) && entrySequence == 0)
                {
                    if (RequestTransportIdle(frame, EnterOp)) Offer("enter");
                    else { status = "WAITING"; reason = "pointrace_waiting_enter_transport"; }
                }
                else if (frame.InScene && Ready(frame.Panel) && entered)
                {
                    if (progressBaseline == null)
                    { status = "WAITING"; reason = "pointrace_waiting_entry_baseline"; return; }
                    if (!ReferenceEquals(D.Read(frame.Panel, "m_pointrace"), frame.Race))
                        throw new ProbeFailure("pointrace_panel_owner_changed");
                    if (ownedPanel == null) { ownedPanel = frame.Panel; ownedScene = frame.Scene; }
                    if (ShouldRefresh(frame) && !refreshAttempted)
                    {
                        if (RefreshTransportIdle(frame)) Offer("refresh_free");
                        else { status = "WAITING"; reason = "pointrace_waiting_free_refresh_transport"; }
                    }
                    else if (frame.Index < 0) Finish("pointrace_no_free_opponent");
                    else if (RequestTransportIdle(frame, BattleOp)) Offer("challenge");
                    else { status = "WAITING"; reason = "pointrace_waiting_challenge_transport"; }
                }
                else { status = "WAITING"; reason = EntryCallbackPending() ?
                    "pointrace_waiting_entry_callback" : "pointrace_waiting_scene"; }
                return;
            }
            if (receipt == null || battleTap == null || !battleTap.Returned)
            { status = "PENDING"; reason = "pointrace_waiting_reply"; return; }
            if (receipt.Result != 0)
            {
                // The stock rejected callback leaves its loading curtain up.
                // Do not hide it or claim navigation cleanup without evidence.
                throw new ProbeFailure("pointrace_server_rejected_cleanup_unresolved");
            }
            if (receipt.Gold != 0 || frame.Count != baseline.Count + 1 ||
                receipt.Opponent != 0 && receipt.Opponent != baseline.Opponent ||
                receipt.ReportOpponent != 0 && receipt.ReportOpponent != baseline.Opponent)
                throw new ProbeFailure("pointrace_cost_attempt_or_opponent_changed");
            BindBattle();
            object activeInterpreter = battle == null ? null : D.Read(battle, "interpreter");
            if (activeInterpreter == null || !ReferenceEquals(D.Read(activeInterpreter, "m_report"), battleReport) ||
                !BattleReplyPreflight.Matches(receipt.DetachedReport, battleReport))
                throw new ProbeFailure("pointrace_callback_report_changed");
            if (Ready(result) && ReferenceEquals(D.Read(result, "battle_ui"), battle) && OwnBattle())
            { Offer("battle_close"); return; }
            if (frame.Battling || Visible(battle) || Visible(result))
            {
                if (!skipped && CanShowResult()) Offer("skip");
                else { status = "BATTLE"; reason = "pointrace_waiting_result"; }
                return;
            }
            if (battleClosed && frame.InScene && Ready(frame.Panel) && !frame.Loading && entered)
            {
                // battle_exit restores this same scene and PrepareLoading
                // reloads its server list. A round ends here, not at home.
                if (!ReferenceEquals(frame.Panel, ownedPanel) || !ReferenceEquals(frame.Scene, ownedScene) ||
                    completedReports.Contains(receipt.Report) || challengesSubmitted != challengesCompleted + 1)
                    throw new ProbeFailure("pointrace_round_completion_changed");
                completedReports[receipt.Report] = true;
                challengesCompleted++; completedSequence = challengeSequence; completedCount = frame.Count; completedReceipt = receipt;
                if (battleTap != null && !battleTap.Restore())
                    throw new ProbeFailure("pointrace_round_callback_release_failed");
                battleTap = null; baseline = null; receipt = null; attempted = battleClosed = skipped = battleCallbackReturned = settlementInvalid = false;
                battleReport = null;
                battle = result = null; started = Environment.TickCount;
                Observe(); return;
            }
            status = "BATTLE"; reason = "pointrace_waiting_result";
        }
        private void Finish(string detail)
        {
            finishReason = detail; reason = detail; status = "UNAVAILABLE";
            if (!touched) return;
            if (frame.InScene && Ready(frame.Panel) && !frame.Loading && !frame.Battling) next = "return";
            else if (frame.Home && ReferenceEquals(frame.Sub, frame.Select) && Ready(frame.Select)) next = "close_selector";
            else if (HomeReady()) { returning = true; }
        }
        private void Fail(string detail)
        {
            next = "";
            if (touched || attempted || failureReason != "")
            {
                if (failureReason == "") failureReason = detail;
                status = "UNKNOWN"; reason = failureReason;
            }
            else { status = "UNAVAILABLE"; reason = detail; }
        }
        private bool UnmutatedEntryPending()
        { return owner != null && entrySequence > 0 && !entryCallbackReturned &&
            !entered && entryReply == null && progressBaseline == null && !attempted && !refreshAttempted &&
            challengesSubmitted == 0 && challengesCompleted == 0; }
        private bool EntryCallbackPending() { return touched && UnmutatedEntryPending(); }
        private bool EntryTimeoutReleaseReady()
        {
            if (stopped || !entryTimeoutCleanup || failureReason != "pointrace_entry_callback_timeout" ||
                status != "UNKNOWN" || !cleanupAttempted || !returning || !entryListenerReleased ||
                !UnmutatedEntryPending() || enterTap != null || firstEnterTap != null || battleTap != null ||
                refresh != null || receipt != null || queued != null || frame == null ||
                !frame.SameOwner(owner) || !frame.SamePremium(owner) ||
                frame.Count != owner.Count || frame.Refreshes != owner.Refreshes ||
                !ReferenceEquals(frame.Panel, ownedPanel) || !ReferenceEquals(frame.Scene, ownedScene) ||
                !ReferenceEquals(D.Read(frame.Panel, "m_pointrace"), frame.Race)) return false;
            GameObject battleObject = GameObject.Find("/Battle/BattleUI");
            if (battleObject != null && (Visible(battleObject.GetComponent(Model.GameType("BattleUI"))) ||
                Visible(battleObject.GetComponent(Model.GameType("BattleNormalResultUI"))))) return false;
            return HomeReady() && RequestTransportIdle(frame, EnterOp) &&
                RequestTransportIdle(frame, BattleOp) && RefreshTransportIdle(frame);
        }
        private bool CanReturnAfterEntryTimeout(Frame current)
        {
            GameObject battleObject = GameObject.Find("/Battle/BattleUI");
            if (battleObject != null && (Visible(battleObject.GetComponent(Model.GameType("BattleUI"))) ||
                Visible(battleObject.GetComponent(Model.GameType("BattleNormalResultUI"))))) return false;
            return !stopped && entryTimeoutCleanup && !cleanupAttempted && EntryCallbackPending() &&
                current != null && current.SameOwner(owner) && current.SamePremium(owner) &&
                current.Count == owner.Count && current.Refreshes == owner.Refreshes &&
                current.InScene && !current.Loading && !current.Battling && Ready(current.Panel) &&
                ReferenceEquals(current.Panel, ownedPanel) && ReferenceEquals(current.Scene, ownedScene) &&
                ReferenceEquals(D.Read(current.Panel, "m_pointrace"), current.Race) &&
                IsHomeRoot(owner.HomeRoot) && ReferenceEquals(current.ReturnRoot, owner.HomeRoot) &&
                current.ReturnClose == null && !Visible(battle) && !Visible(result) &&
                !DevelopmentCallbackCapture.AnyCallbackExecuting && current.Transport.StillCurrent();
        }
        private void ObserveEntryTimeoutCleanup()
        {
            // Navigation cleanup cannot settle an entry request or authorize a
            // new one. Native late replies retain their stock cache semantics.
            status = "UNKNOWN"; reason = failureReason; next = "";
            if (closed)
            {
                Detach();
                cleanupReason = EntryTimeoutReleaseReady() ? "pointrace_cleanup_home_verified" :
                    "pointrace_cleanup_release_unverified";
                return;
            }
            if (unchecked((uint)(Environment.TickCount - cleanupStarted)) >= CleanupTimeoutMilliseconds)
            { cleanupReason = "pointrace_cleanup_timeout_session_restart_required"; return; }
            if (returning)
            {
                Detach();
                if (EntryTimeoutReleaseReady())
                { closed = true; touched = false; cleanupReason = "pointrace_cleanup_home_verified"; }
                else cleanupReason = "pointrace_cleanup_waiting_home";
            }
            else if (CanReturnAfterEntryTimeout(frame))
            { next = "return"; cleanupReason = "pointrace_cleanup_return_ready"; }
            else cleanupReason = "pointrace_cleanup_return_unverified";
        }
        private void Offer(string value) { status = "READY"; reason = ""; next = value; }
        private static void Check(ReplyTap tap)
        { if (tap != null && tap.Error != "") throw new ProbeFailure(tap.Error); }

        internal bool Queue(int task, ulong role, string operation, int seq, int expected, string requested, out string why)
        {
            why = "pointrace_queue_unavailable";
            if (stopped || !available || frame == null || queued != null || task != TaskId || role != frame.RoleId ||
                seq <= sequence || !Command.ValidNonce(operation)) return false;
            why = "pointrace_queue_revision_changed:" + expected + "->" + revision;
            if (expected != revision) return false;
            why = "pointrace_queue_action_changed";
            if (requested != next || requested == "") return false;
            if (closed)
            {
                if (failureReason != "") return false;
                if (requested != "open" || operation == id || !ReleaseVerified) return false;
                owner = baseline = entryReply = progressBaseline = null; receipt = completedReceipt = null;
                firstEnterTap = null; entrySequence = baselineSequence = 0; baselineSource = "";
                failureReason = cleanupReason = "";
                challengeRecovery = false; challengeRejectedSequence = 0; challengeRejectedReason = "";
                challengeRejectedIdle = challengeRejectedTransient = false;
                entryTimeoutCleanup = cleanupAttempted = entryCallbackReturned = entryListenerReleased = false;
                entered = attempted = closed = returning = battleClosed = skipped = battleCallbackReturned = settlementInvalid = false;
                battleReport = null;
                refresh = null; refreshAttempted = false;
                battle = result = ownedPanel = ownedScene = null; id = "";
                challengesSubmitted = challengesCompleted = challengeSequence = completedSequence = completedCount = 0;
                completedReports.Clear();
            }
            why = "pointrace_queue_operation_changed";
            if (id != "" && id != operation) return false;
            challengeRejectedSequence = 0; challengeRejectedIdle = false;
            id = operation; sequence = seq; action = requested; queued = frame; why = ""; return true;
        }
        internal bool ExecuteGui(DailyPointRaceContextProvider fresh, DevelopmentPublication publish,
            DailyPointRaceInvocationGate begin, out string why)
        {
            why = "pointrace_not_queued";
            Frame before = queued; string requested = action;
            if (before == null || stopped || !D.Gui) return false;
            if (!Monitor.TryEnter(before.Client, 0)) { why = "pointrace_client_busy"; return false; }
            try
            {
                Frame sampled = Read(fresh());
                if (!sampled.SameOffer(before)) { NotInvoked("pointrace_offer_changed"); why = reason; return false; }
                if (requested == "resume" && (touched || owner != null || progressBaseline != null ||
                    entrySequence != 0 || challengesSubmitted != 0 || refreshAttempted || !CanResume(sampled)))
                { NotInvoked("pointrace_resume_unavailable"); why = reason; return false; }
                if (requested == "enter" && (entrySequence != 0 || progressBaseline != null ||
                    challengesSubmitted != 0 || refreshAttempted))
                { NotInvoked("pointrace_duplicate_entry"); why = reason; return false; }
                if (requested == "enter" && !RequestTransportIdle(sampled, EnterOp))
                { NotInvoked("pointrace_enter_transport_busy"); why = reason; return false; }
                if (requested == "return" && entryTimeoutCleanup && !CanReturnAfterEntryTimeout(sampled))
                { NotInvoked("pointrace_cleanup_return_unverified"); why = reason; return false; }
                if (requested == "challenge")
                {
                    string refusal = ChallengeRefusal(sampled);
                    if (refusal != "")
                    {
                        frame = sampled; challengeRejectedSequence = sequence;
                        challengeRejectedReason = refusal; challengeRejectedIdle = ChallengeIdle(sampled);
                        challengeRejectedTransient = refusal == "pointrace_challenge_transport_busy" ||
                            refusal == "pointrace_challenge_page_busy";
                        if (challengeRejectedIdle && !challengeRecovery)
                        { challengeRecovery = true; challengeRecoveryStarted = Environment.TickCount; }
                        NotInvoked("pointrace_free_challenge_unavailable"); why = reason; return false;
                    }
                }
                if (requested == "refresh_free" && (progressBaseline == null ||
                    sampled.Count != progressBaseline.Count + challengesCompleted || attempted || refreshAttempted || !ShouldRefresh(sampled) ||
                    !sampled.InScene || !Ready(sampled.Panel) || !RefreshTransportIdle(sampled)))
                { NotInvoked("pointrace_free_refresh_unavailable"); why = reason; return false; }
                if (requested == "skip" && !CanShowResult() || requested == "battle_close" && !OwnBattle())
                { NotInvoked("pointrace_report_changed"); why = reason; return false; }
                publish();
                Frame accepted = null;
                if (!begin(delegate { accepted = Read(fresh()); return !stopped && D.Gui && accepted.SameOffer(before); },
                    delegate
                    {
                        if (stopped || !D.Gui || accepted == null || !accepted.Transport.StillCurrent()) return false;
                        if (requested != "return" || !entryTimeoutCleanup) return true;
                        Frame latest = Read(fresh());
                        return latest.SameOffer(accepted) && CanReturnAfterEntryTimeout(latest);
                    }))
                { NotInvoked("pointrace_gui_cancelled"); why = reason; return false; }
                queued = null; action = ""; frame = accepted; next = "";
                if (!touched) { owner = frame; touched = true; started = Environment.TickCount; }
                Invoke(requested); PublishRevision(); why = ""; return true;
            }
            catch (Exception error)
            {
                queued = null; action = next = "";
                if (attempted) settlementInvalid = true;
                Fail(error is ProbeFailure ? error.Message : "pointrace_stock_call_failed");
                Detach(); PublishRevision(); why = reason; return false;
            }
            finally { Monitor.Exit(before.Client); }
        }
        private void Invoke(string requested)
        {
            // Bound each dispatched action separately. A gameplay cooldown is
            // returned to the scheduler and never extends this pending wait.
            started = Environment.TickCount;
            if (requested == "open") D.MainBranch("_battle_arena");
            else if (requested == "resume")
            {
                // No request or callback is invented for an already open scene.
                // SameOffer binds this baseline to the accepted idle frame.
                if (!CanResume(frame)) throw new ProbeFailure("pointrace_resume_unavailable");
                progressBaseline = frame; baselineSource = "scene_resume"; baselineSequence = sequence;
                ownedPanel = frame.Panel; ownedScene = frame.Scene; entered = true;
            }
            else if (requested == "enter")
            {
                Frame requestFrame = frame; string requestId = id; int requestSequence = sequence;
                entrySequence = requestSequence; started = Environment.TickCount;
                D.Method(frame.Context.GlobalUi.GetType(), "cs_pointrace_enter",
                    new Type[] { Model.FirstType("RootMessenger"), Model.FirstType("__BaseUI+delegate_OnClose") }, false, typeof(void))
                    .Invoke(frame.Context.GlobalUi, new object[] { frame.CurrentRoot, null });
                GameObject scenePanel = GameObject.Find("/PointRace/PointRaceUI");
                frame.Panel = scenePanel == null ? null : scenePanel.GetComponent(Model.GameType("PointraceUI"));
                Component requestPanel = frame.Panel;
                ownedPanel = requestPanel; ownedScene = requestPanel == null ? null : D.Read(requestPanel, "rmg") as Component;
                enterTap = Attach(EnterOp, requestPanel, "sc_pointrace_enter_0x464", delegate(byte[] body)
                    {
                        Frame after = Read(null);
                        if (!after.SameOwner(requestFrame) || !after.SamePremium(requestFrame) ||
                            !ReferenceEquals(after.Panel, requestPanel))
                            throw new ProbeFailure("pointrace_entry_callback_owner_changed");
                        entryReply = after; entered = true; entryCallbackReturned = true;
                    }, delegate
                    {
                        Frame prior = Read(null);
                        if (stopped || id != requestId || sequence != requestSequence || entrySequence != requestSequence ||
                            entryReply != null || progressBaseline != null || attempted || challengesSubmitted != 0 ||
                            challengesCompleted != 0 || refreshAttempted || !prior.SameOwner(requestFrame) ||
                            !prior.SamePremium(requestFrame) || prior.Count != requestFrame.Count ||
                            prior.Refreshes != requestFrame.Refreshes || !ReferenceEquals(prior.Panel, requestPanel))
                            throw new ProbeFailure("pointrace_entry_callback_owner_changed");
                    });
                firstEnterTap = enterTap;
            }
            else if (requested == "challenge")
            {
                challengeRecovery = false;
                baseline = frame; attempted = true; challengesSubmitted++; challengeSequence = sequence;
                started = Environment.TickCount;
                Frame requestFrame = frame; string requestId = id; int requestSequence = sequence;
                object requestRoot = D.Read(frame.Loader, "currmg"), requestSourceRoot = D.Read(frame.Panel, "rmg");
                D.Selection(frame.Panel, "m_challengeIdx", frame.Index);
                Receipt validated = null;
                // This is the stock direct model-click path. No inspection,
                // attempt purchase or cooldown-clear callback runs.
                BattleLaunchAttempt requestLaunch = BattleLaunch.Direct(BattleMode.PointRace, frame.Panel, frame.Target, delegate
                { return !stopped && ReferenceEquals(baseline, requestFrame) && frame.SameOffer(requestFrame); },
                    delegate { D.Call(frame.Panel, "WantChallenge"); }, BattleOp);
                battleTap = Attach(BattleOp, frame.Panel, "sc_pointrace_challenge_0x465",
                    delegate(byte[] body)
                    {
                        receipt = validated;
                        if (receipt == null) throw new ProbeFailure("pointrace_preflight_missing");
                        receipt.RequestedOpponent = requestFrame.Opponent;
                        receipt.OperationId = requestId; receipt.Sequence = requestSequence;
                        if (receipt.Result == 0)
                        {
                            BindBattle();
                            object interpreter = battle == null ? null : D.Read(battle, "interpreter");
                            battleReport = interpreter == null ? null : D.Read(interpreter, "m_report");
                            if (battleReport == null) throw new ProbeFailure("pointrace_callback_report_missing");
                            if (!BattleReplyPreflight.Matches(receipt.DetachedReport, battleReport))
                                throw new ProbeFailure("pointrace_callback_report_changed");
                            requestLaunch.InitialResponseApplied(battle, battleReport);
                        }
                        receipt.CallbackReturned = battleCallbackReturned = true;
                    }, delegate
                    {
                        Frame prior = Read(null);
                        IList targets = D.Read(requestFrame.Race, "challengers") as IList;
                        if (stopped && !normalStopDrain || id != requestId || sequence != requestSequence ||
                            !ReferenceEquals(baseline, requestFrame) || !attempted ||
                            !ReferenceEquals(DailyTransport.CurrentClient(requestFrame.Role), requestFrame.Client) ||
                            !requestFrame.Transport.StillCurrent() ||
                            !ReferenceEquals(D.Static(Model.FirstType("BaseAsset"), "lds"), requestFrame.Loader) ||
                            !ReferenceEquals(D.Read(requestFrame.Loader, "currmg"), requestRoot) ||
                            !ReferenceEquals(D.Read(requestFrame.Panel, "rmg"), requestSourceRoot) ||
                            !prior.SameOwner(requestFrame) || !prior.SamePremium(requestFrame) ||
                            !ReferenceEquals(prior.Target, requestFrame.Target) || prior.Opponent != requestFrame.Opponent ||
                            prior.Score != requestFrame.Score || prior.CandidateState != requestFrame.CandidateState ||
                            !ReferenceEquals(prior.Candidates, requestFrame.Candidates) ||
                            prior.Count != requestFrame.Count || prior.Refreshes != requestFrame.Refreshes ||
                            !ReferenceEquals(prior.Panel, requestFrame.Panel) || !ReferenceEquals(prior.Scene, requestFrame.Scene) ||
                            targets == null || requestFrame.Index < 0 || requestFrame.Index >= targets.Count ||
                            !ReferenceEquals(targets[requestFrame.Index], requestFrame.Target) ||
                            D.Int(D.Read(requestFrame.Panel, "m_challengeIdx")) != requestFrame.Index)
                            throw new ProbeFailure("pointrace_challenge_callback_owner_changed");
                    }, delegate(byte[] body)
                    {
                        validated = Receipt.Decode(body);
                        validated.Preflight(requestFrame.Opponent);
                    });
            }
            else if (requested == "refresh_free")
            {
                refresh = new RefreshRequest(this, frame, id, sequence);
                // Commit before Send: uncertain dispatch must never be retried.
                refreshAttempted = true;
                refresh.Send();
            }
            else if (requested == "skip")
            {
                if (!BattleSettlement.PresentSettled(battle, baseline.Panel, BattleMode.PointRace, id,
                    baseline.Target, baseline.Opponent.ToString(CultureInfo.InvariantCulture), receipt,
                    BattleOp, receipt.Report, VerifySettledBattle))
                    throw new ProbeFailure("pointrace_shared_presentation_unverified");
                skipped = true;
            }
            else if (requested == "battle_close")
            {
                D.Call(result, "Hide"); D.Call(battle, "battle_exit"); battleClosed = true;
                // The stock exit broadcasts PrepareLoading on the retained
                // PointRace root, which sends a new enter/list request.
                entered = false;
                if (enterTap != null && !enterTap.Restore())
                    throw new ProbeFailure("pointrace_enter_callback_release_failed");
                enterTap = Attach(EnterOp, frame.Panel, "sc_pointrace_enter_0x464", delegate(byte[] body) { entered = true; });
            }
            else if (requested == "return")
            {
                // Commit before invoking stock navigation; an uncertain return
                // must not be retried either.
                if (entryTimeoutCleanup) cleanupAttempted = true;
                D.Call(frame.Panel, "_return"); returning = true;
            }
            else if (requested == "close_selector")
            {
                D.Call(frame.Select, "Hide");
                D.Method(Model.GameType("MainUI"), "SetSubUI", new Type[] { Model.FirstType("__BaseUI") }, true, typeof(void))
                    .Invoke(null, new object[] { null });
                returning = true;
            }
            status = entryTimeoutCleanup ? "UNKNOWN" : "PENDING";
        }
        private Frame Read(D supplied)
        {
            if (supplied != null && supplied.Paused) throw new ProbeFailure("pointrace_paused");
            D context = touched && owner != null ? owner.Context : supplied;
            if (context == null || context.Character == null) throw new ProbeFailure("pointrace_context_unavailable");
            object cachedUi = D.Static(Model.GameType("BaseUI"), "rootUI");
            if (context.Root == null || context.GlobalUi == null || context.UiRoot == null ||
                !ReferenceEquals(D.Read(context.GlobalUi, "rmg"), context.UiRoot) ||
                cachedUi != null && !ReferenceEquals(cachedUi, context.GlobalUi))
                throw new ProbeFailure("pointrace_root_owner_changed");
            object role = D.Static(Model.FirstType("Global"), "character_");
            if (!ReferenceEquals(role, context.Character)) throw new ProbeFailure("pointrace_character_changed");
            Frame f = new Frame { Context = context, Role = role, Client = context.Client,
                Race = D.Read(role, "pointrace"), RoleId = Convert.ToUInt64(D.Read(role, "id"), CultureInfo.InvariantCulture),
                Loader = D.Static(Model.FirstType("BaseAsset"), "lds"), Sub = context.SubUi,
                Gold = D.Int(D.Read(role, "gold")), Bound = D.Int(D.Read(role, "bind_gold")),
                GoldConsumed = D.UInt(D.Read(role, "gold_consume")) };
            if (f.RoleId == 0 || !ReferenceEquals(f.Client, D.Static(Model.FirstType("Global"), "m_client")) ||
                !ReferenceEquals(f.Client, D.Static(Model.GameType("RootUI"), "m_client")))
                throw new ProbeFailure("pointrace_client_or_role_changed");
            f.Transport = DailyTransport.Read(f.Client);
            if (f.Transport == null) throw new ProbeFailure("pointrace_transport_unavailable");
            string clock = DailyRefreshGate.Read(role, out f.Now, out f.Day, out f.Cycle);
            if (clock != "") throw new ProbeFailure(clock);
            f.Open = f.Now >= f.Day + 18000L && f.Now < f.Day + 75600L &&
                (int)D.Method(Model.GameType("MainUI"), "CalcServerOpenRefreshDays", Type.EmptyTypes, true, typeof(int)).Invoke(null, null) != 0;
            f.Count = D.Int(D.Read(f.Race, "challenge_count")); f.Bought = D.Int(D.Read(f.Race, "challenge_buy"));
            f.Refreshes = D.Int(D.Read(f.Race, "refresh_count"));
            if (f.Count < 0 || f.Bought < 0 || f.Count > 20 || f.Bought > 10 || f.Refreshes < 0)
                throw new ProbeFailure("pointrace_attempt_schema_changed");
            f.PlayerScore = (uint)D.Method(role.GetType(), "get_battle_score", Type.EmptyTypes, false, typeof(uint)).Invoke(role, null);
            object cd = D.Method(role.GetType(), "getCooldown", new Type[] { typeof(byte) }, false, Model.FirstType("Database.Cooldown"))
                .Invoke(role, new object[] { (byte)6 });
            f.Cooldown = cd == null ? 0 : D.UInt(D.Read(cd, "time"));
            f.CurrentRoot = D.Read(f.Loader, "currmg");
            f.HomeRoot = context.Root;
            if (!IsHomeRoot(f.HomeRoot))
            {
                // This is the same retained destination used by stock _return.
                f.HomeRoot = D.Method(f.Loader.GetType(), "GetOldMainUI", Type.EmptyTypes, false,
                    Model.FirstType("RootMessenger")).Invoke(f.Loader, null) as Component;
                if (!IsHomeRoot(f.HomeRoot)) throw new ProbeFailure("pointrace_resume_home_unavailable");
            }
            f.Battling = D.Bool(D.Read(f.Loader, "isBattling"));
            f.Loading = (bool)D.Method(f.Loader.GetType(), "isVisible", Type.EmptyTypes, false, typeof(bool)).Invoke(f.Loader, null);
            f.Select = context.Find("ArenaSelectUI");
            GameObject panel = GameObject.Find("/PointRace/PointRaceUI");
            // The inactive retained scene is no longer found by GameObject.Find
            // after stock return. Keep its exact identity for fresh home release
            // samples; visibility, owner and transport are still re-read.
            f.Panel = panel == null ? ((touched || closed && entryTimeoutCleanup) && frame != null ? frame.Panel : null) :
                panel.GetComponent(Model.GameType("PointraceUI"));
            f.Scene = f.Panel == null ? null : D.Read(f.Panel, "rmg") as Component;
            if (f.Panel != null)
            {
                f.ReturnRoot = D.Method(f.Loader.GetType(), "GetOldMainUI", Type.EmptyTypes, false,
                    Model.FirstType("RootMessenger")).Invoke(f.Loader, null) as Component;
                f.ReturnClose = D.Read(f.Panel, "OnClose");
            }
            if (f.Panel != null && (f.Scene == null || D.Read(f.Scene, "LevelID") as string != "PointRace" ||
                f.Scene.GetType() != Model.FirstType("RootMessenger") || f.Panel.transform.root != f.Scene.transform))
                throw new ProbeFailure("pointrace_scene_owner_changed");
            f.InScene = !f.Battling && f.Scene != null && ReferenceEquals(f.CurrentRoot, f.Scene) && f.Scene.gameObject.activeInHierarchy &&
                ReferenceEquals(D.Read(context.GlobalUi, "level_rmg"), f.Scene);
            f.Home = !f.Battling && ReferenceEquals(f.CurrentRoot, f.HomeRoot) && f.HomeRoot.gameObject.activeInHierarchy;
            f.Main = f.Home ? f.HomeRoot.GetComponentInChildren(Model.GameType("MainUI")) : null;
            IList list = D.Read(f.Race, "challengers") as IList; f.Candidates = list;
            if (list != null && list.Count > 5) throw new ProbeFailure("pointrace_challenger_schema_changed");
            if (list != null)
                for (int i = 0; i < list.Count && i < 5; i++)
                {
                    object target = list[i];
                    ulong opponent = (ulong)Getter(target, "id", typeof(ulong));
                    bool killed = (bool)Getter(target, "killed", typeof(bool));
                    uint score = (uint)Getter(target, "battle_score", typeof(uint));
                    uint pos = (uint)Getter(target, "pos", typeof(uint));
                    f.CandidateState += Candidate(opponent, pos, score, killed);
                    if (killed || opponent == 0 || opponent == f.RoleId || f.Index >= 0 && score >= f.Score) continue;
                    f.Target = target; f.Opponent = opponent; f.Score = score; f.Index = i;
                }
            return f;
        }
        private static bool ShouldRefresh(Frame f)
        { return f.Open && f.Count < Required && f.Cooldown <= f.Now && f.Refreshes == 0 && f.Candidates != null &&
            (f.Index < 0 || f.PlayerScore > 0 && f.Score > f.PlayerScore); }
        private string ChallengeRefusal(Frame f)
        {
            if (challengeRecovery &&
                unchecked((uint)(Environment.TickCount - challengeRecoveryStarted)) >= CleanupTimeoutMilliseconds)
                return "pointrace_challenge_retry_expired";
            if (progressBaseline == null || f.Count != progressBaseline.Count + challengesCompleted)
                return "pointrace_challenge_progress_changed";
            if (attempted) return "pointrace_challenge_round_pending";
            if (!f.Open) return "pointrace_closed";
            if (f.Count >= Required) return "pointrace_natural_attempts_exhausted";
            if (f.Cooldown > f.Now) return "pointrace_cooldown";
            if (f.Index < 0) return "pointrace_no_free_opponent";
            if (!f.InScene || !ReferenceEquals(f.Panel, ownedPanel) || !ReferenceEquals(f.Scene, ownedScene))
                return "pointrace_challenge_scene_changed";
            if (!entered) return "pointrace_challenge_entry_pending";
            if (!Ready(f.Panel)) return "pointrace_challenge_page_busy";
            if (!RequestTransportIdle(f, BattleOp)) return "pointrace_challenge_transport_busy";
            if (ShouldRefresh(f) && !refreshAttempted) return "pointrace_challenge_free_refresh_required";
            if (refreshAttempted && (refresh == null || !refresh.Verified)) return "pointrace_challenge_refresh_unverified";
            return "";
        }
        private bool ChallengeIdle(Frame f)
        {
            if (f == null || owner == null || progressBaseline == null || failureReason != "" || settlementInvalid ||
                !f.SameOwner(owner) || !f.SamePremium(owner) ||
                f.Count != progressBaseline.Count + challengesCompleted || challengesSubmitted != challengesCompleted ||
                attempted || receipt != null || battleTap != null || battle != null || result != null || battleReport != null ||
                !entered || !f.InScene || f.Battling ||
                !ReferenceEquals(f.Panel, ownedPanel) || !ReferenceEquals(f.Scene, ownedScene) ||
                enterTap != null && (!enterTap.Returned || enterTap.Error != "") ||
                firstEnterTap != null && (!firstEnterTap.Returned || firstEnterTap.Error != "") ||
                refreshAttempted && (refresh == null || !refresh.Returned || !refresh.Verified || refresh.Error != "")) return false;
            GameObject battleObject = GameObject.Find("/Battle/BattleUI");
            return battleObject == null ||
                !Visible(battleObject.GetComponent(Model.GameType("BattleUI"))) &&
                !Visible(battleObject.GetComponent(Model.GameType("BattleNormalResultUI")));
        }
        private static bool IsHomeRoot(Component root)
        { return root != null && root.GetType() == Model.FirstType("RootMessenger") && root.transform.root == root.transform &&
            (D.Read(root, "LevelID") as string == "Capital" || D.Read(root, "LevelID") as string == "Campaign"); }
        private static bool CanResume(Frame f)
        { return f.InScene && Ready(f.Panel) && !f.Loading && !f.Battling && IsHomeRoot(f.HomeRoot) &&
            ReferenceEquals(f.Context.Root, f.Scene) && ReferenceEquals(D.Read(f.Panel, "m_pointrace"), f.Race) &&
            D.Bool(D.Static(Model.FirstType("Global"), "extern_enabled")) &&
            RequestTransportIdle(f, EnterOp) && RequestTransportIdle(f, BattleOp) && RefreshTransportIdle(f); }
        private static string Candidate(ulong opponent, uint pos, uint score, bool killed)
        { return opponent.ToString(CultureInfo.InvariantCulture) + ":" + pos.ToString(CultureInfo.InvariantCulture) + ":" +
            score.ToString(CultureInfo.InvariantCulture) + ":" + (killed ? "1;" : "0;"); }
        private static bool RefreshTransportIdle(Frame f)
        { return RequestTransportIdle(f, RefreshOp); }
        private static bool RequestTransportIdle(Frame f, ushort opcode)
        {
            IDictionary handlers = D.Read(f.Client, "m_handlers") as IDictionary;
            IList messages = D.Read(f.Client, "messages") as IList, sends = D.Read(f.Client, "m_sendDatas") as IList;
            if (handlers == null || messages == null || messages.Count != 0 || sends == null || sends.Count != 0 ||
                D.Bool(D.Read(f.Client, "m_bSending")) || DevelopmentCallbackCapture.AnyCallbackExecuting ||
                !f.Transport.StillCurrent()) return false;
            if (!handlers.Contains(opcode)) return true;
            if (opcode != EnterOp) return false;
            // PrepareLoading registers 1124 with life=-1. Run deliberately
            // retains that receiver after replies; its presence is not a send.
            if (f.Panel == null) throw new ProbeFailure("pointrace_enter_handler_owner_unavailable");
            object handler = handlers[EnterOp];
            Delegate callback = handler == null ? null : D.Read(handler, "handler") as Delegate;
            MethodInfo stock = D.Method(Model.GameType("PointraceUI"), "sc_pointrace_enter_0x464",
                new Type[] { typeof(MemoryStream), typeof(object) }, false, typeof(void));
            if (handler == null || handler.GetType() != Model.FirstType("game.net.tcp_base+Handler") ||
                f.Panel.GetType() != Model.GameType("PointraceUI") || callback == null ||
                callback.GetInvocationList().Length != 1 || !ReferenceEquals(callback.Target, f.Panel) ||
                !callback.Method.Equals(stock) || D.Read(handler, "arg") != null || (float)D.Read(handler, "life") != -1f ||
                !ReferenceEquals(D.Read(f.Panel, "m_pointrace"), f.Race))
                throw new ProbeFailure("pointrace_enter_handler_changed");
            return true;
        }
        private static object Getter(object value, string property, Type type)
        { return D.Method(value.GetType(), "get_" + property, Type.EmptyTypes, false, type).Invoke(value, null); }
        private bool HomeReady()
        { return available && frame != null && frame.Home && !frame.Loading && !frame.Battling && frame.Sub == null &&
            Ready(frame.Main) && D.Bool(D.Static(Model.FirstType("Global"), "extern_enabled")) &&
            ReferenceEquals(D.Read(frame.Context.GlobalUi, "level_rmg"), frame.HomeRoot) &&
            !D.Bool(D.Read(frame.HomeRoot, "bLoading")) && D.Int(D.Read(frame.HomeRoot, "ExpectingCount")) == 0 &&
            HomeTransportIdle() &&
            !Visible(frame.Select) && !D.Loading(frame.Select) && !Visible(frame.Panel) && (frame.Panel == null || !D.Loading(frame.Panel)) &&
            !Visible(battle) && !Visible(result); }
        private bool HomeTransportIdle()
        {
            IDictionary handlers = D.Read(frame.Client, "m_handlers") as IDictionary;
            IList messages = D.Read(frame.Client, "messages") as IList, sends = D.Read(frame.Client, "m_sendDatas") as IList;
            if (handlers == null || messages == null || messages.Count != 0 || sends == null || sends.Count != 0 ||
                D.Bool(D.Read(frame.Client, "m_bSending")) || DevelopmentCallbackCapture.AnyCallbackExecuting ||
                !frame.Transport.StillCurrent()) return false;
            foreach (DictionaryEntry entry in handlers)
                if ((float)D.Read(entry.Value, "life") > 0f) return false;
            return true;
        }
        private static bool Visible(Component c) { return c != null && D.Visible(c); }
        private static bool Ready(Component c) { return Visible(c) && D.Enabled(c) && !D.Loading(c); }
        private void BindBattle()
        {
            if (battle != null) return;
            GameObject game = GameObject.Find("/Battle/BattleUI");
            if (game == null) return;
            battle = game.GetComponent(Model.GameType("BattleUI"));
            result = game.GetComponent(Model.GameType("BattleNormalResultUI"));
            if (battle != null && !OwnBattle()) throw new ProbeFailure("pointrace_report_owner_changed");
        }
        private bool OwnBattle()
        {
            if (battle == null || receipt == null || receipt.Report == 0 || frame == null) return false;
            Delegate close = D.Read(battle, "OnClose") as Delegate;
            return receipt.Report == Convert.ToUInt64(D.Read(battle, "m_reportid"), CultureInfo.InvariantCulture) &&
                close != null && ReferenceEquals(close.Target, frame.Panel) && close.Method.Name == "BattleClose" &&
                close.Method.DeclaringType == Model.GameType("PointraceUI");
        }
        private bool CanShowResult()
        {
            if (stopped || !VerifySettledBattle() || !Ready(battle) || frame.Loading || battleClosed ||
                !D.Bool(D.Read(battle, "bBattle_started")) || D.Bool(D.Read(battle, "bUseBattleEnd")) ||
                D.Bool(D.Read(battle, "bNeedWaitForBattleEnd"))) return false;
            object interpreter = D.Read(battle, "interpreter");
            return interpreter != null && D.Read(interpreter, "m_report") != null &&
                (bool)D.Method(battle.GetType(), "get_ArmyBehaviourCreated", Type.EmptyTypes, false, typeof(bool)).Invoke(battle, null);
        }
        private bool VerifySettledBattle()
        {
            if (settlementInvalid || status == "UNKNOWN" && !stopped || baseline == null || receipt == null || receipt.Result != 0 ||
                !battleCallbackReturned || battleTap != null && (!battleTap.Returned || battleTap.Error != "") ||
                receipt.OperationId != id || receipt.Sequence != challengeSequence || receipt.Report == 0 ||
                receipt.RequestedOpponent != baseline.Opponent || baseline.Target == null ||
                battleReport == null || !OwnBattle()) return false;
            Frame current = Read(null);
            object interpreter = D.Read(battle, "interpreter");
            return current.SameOwner(baseline) && current.SamePremium(baseline) &&
                ReferenceEquals(current.Panel, baseline.Panel) && ReferenceEquals(current.Scene, baseline.Scene) &&
                receipt.Gold == 0 && current.Count == baseline.Count + 1 &&
                (receipt.Opponent == 0 || receipt.Opponent == baseline.Opponent) &&
                (receipt.ReportOpponent == 0 || receipt.ReportOpponent == baseline.Opponent) &&
                interpreter != null && ReferenceEquals(D.Read(interpreter, "m_report"), battleReport) &&
                BattleReplyPreflight.Matches(receipt.DetachedReport, battleReport);
        }
        internal void NotInvoked(string detail)
        { queued = null; action = ""; reason = failureReason != "" ? failureReason : detail; PublishRevision(); }
        internal void Stop(string detail = "stop")
        {
            // A repeated normal stop preserves the same pending admission,
            // without reviving one revoked by failure or abnormal stop.
            normalStopDrain = detail == "stop" &&
                (stopped ? normalStopDrain && reason == "pointrace_stopped" : status != "UNKNOWN") && !settlementInvalid &&
                battleTap != null && !battleTap.Returned && battleTap.Error == "";
            stopped = true; queued = null; action = next = "";
            if (!closed) { status = touched || failureReason != "" ? "UNKNOWN" : "STOPPED";
                reason = failureReason != "" ? failureReason : "pointrace_stopped"; }
            Detach(); PublishRevision();
        }
        private void Detach()
        {
            if (battleTap != null && battleTap.Error != "") settlementInvalid = true;
            if (firstEnterTap != null && firstEnterTap.Restore())
            {
                entryCallbackReturned |= firstEnterTap.Returned;
                entryListenerReleased = true;
                if (ReferenceEquals(enterTap, firstEnterTap)) enterTap = null;
                firstEnterTap = null;
            }
            if (enterTap != null && enterTap.Restore()) enterTap = null;
            // Leave only the already-sent battle response behind the same
            // one-shot preflight after a normal user stop.
            if ((!normalStopDrain || settlementInvalid) && battleTap != null && battleTap.Restore()) battleTap = null;
        }
        private void PublishRevision()
        { string value = ViewJson() + OperationJson(); if (value != signature) { signature = value; revision++; } }
        private string ViewJson()
        {
            bool seen = available && frame != null;
            return "{\"task_id\":109,\"mode\":\"pointrace\",\"role_id\":" + D.Q(seen ? frame.RoleId.ToString(CultureInfo.InvariantCulture) : "") +
                ",\"observation_available\":" + D.B(seen) + ",\"home_ready\":" + D.B(HomeReady()) +
                ",\"continuous_scene\":true,\"in_scene\":" + D.B(seen && frame.InScene) +
                ",\"refresh_cycle\":" + (seen ? frame.Cycle : 0) + ",\"current\":" + (seen ? frame.Count : -1) +
                ",\"required\":10,\"natural_remaining\":" + (seen ? Math.Max(0, Required - frame.Count) : 0) +
                ",\"open\":" + D.B(seen && frame.Open) + ",\"cooldown_until\":" + (seen ? frame.Cooldown : 0) +
                ",\"server_now\":" + (seen ? frame.Now : 0) +
                ",\"refresh_count\":" + (seen ? frame.Refreshes : -1) +
                ",\"free_refresh_available\":" + D.B(seen && frame.Refreshes == 0 && !refreshAttempted) +
                ",\"player_score\":" + (seen ? frame.PlayerScore : 0) +
                ",\"gold\":" + (seen ? frame.Gold : -1) + ",\"bound_gold\":" + (seen ? frame.Bound : -1) +
                ",\"gold_consumed\":" + (seen ? frame.GoldConsumed : 0) + ",\"bought\":" + (seen ? frame.Bought : -1) +
                ",\"next_action\":" + D.Q(next) + ",\"gate\":" + D.Q(reason) +
                ",\"opponent_id\":" + D.Q(seen ? frame.Opponent.ToString(CultureInfo.InvariantCulture) : "") +
                ",\"opponent_score\":" + (seen ? frame.Score : 0) + "}";
        }
        private string OperationJson()
        {
            return "{\"id\":" + D.Q(id) + ",\"sequence\":" + sequence + ",\"status\":" + D.Q(status) +
                ",\"reason\":" + D.Q(reason) + ",\"ui_touched\":" + D.B(touched || closed) +
                ",\"mutation_attempted\":" + D.B(challengesSubmitted != 0 || refreshAttempted) + ",\"closed\":" + D.B(closed) +
                ",\"busy\":" + D.B(Busy) + ",\"cleanup_pending\":" + D.B(CleanupPending) +
                ",\"release_verified\":" + D.B(ReleaseVerified) +
                ",\"entry_timeout_cleanup\":" + D.B(entryTimeoutCleanup) +
                ",\"cleanup_attempted\":" + D.B(cleanupAttempted) + ",\"cleanup_reason\":" + D.Q(cleanupReason) +
                ",\"baseline_ready\":" + D.B(progressBaseline != null) +
                ",\"baseline_count\":" + (progressBaseline == null ? "null" : progressBaseline.Count.ToString(CultureInfo.InvariantCulture)) +
                ",\"baseline_source\":" + D.Q(baselineSource) + ",\"baseline_sequence\":" + baselineSequence +
                ",\"entry_request_sequence\":" + entrySequence +
                ",\"entry_callback_returned\":" + D.B(entryCallbackReturned) +
                ",\"entry_listener_released\":" + D.B(entryListenerReleased) +
                ",\"challenges_submitted\":" + challengesSubmitted + ",\"challenges_completed\":" + challengesCompleted +
                ",\"challenge_request_sequence\":" + challengeSequence +
                ",\"round_pending\":" + D.B(attempted || challengesSubmitted != challengesCompleted) +
                ",\"challenge_rejection_sequence\":" + challengeRejectedSequence +
                ",\"challenge_rejection_noninvoked\":" + D.B(challengeRejectedSequence != 0) +
                ",\"challenge_rejection_idle\":" + D.B(challengeRejectedIdle) +
                ",\"challenge_rejection_reason\":" + D.Q(challengeRejectedReason) +
                ",\"challenge_rejection_transient\":" + D.B(challengeRejectedTransient) +
                ",\"challenge_recovery_pending\":" + D.B(challengeRecovery) +
                ",\"last_completed_sequence\":" + completedSequence + ",\"last_completed_count\":" + completedCount +
                ",\"last_completed_report_id\":" + D.Q(completedReceipt == null ? "" : completedReceipt.Report.ToString(CultureInfo.InvariantCulture)) +
                ",\"last_completed_opponent_id\":" + D.Q(completedReceipt == null ? "" : completedReceipt.RequestedOpponent.ToString(CultureInfo.InvariantCulture)) +
                ",\"last_completed_receipt_gold\":" + (completedReceipt == null ? "null" : completedReceipt.Gold.ToString(CultureInfo.InvariantCulture)) +
                ",\"free_refresh_attempted\":" + D.B(refreshAttempted) +
                ",\"free_refresh_callback_returned\":" + D.B(refresh != null && refresh.Returned) +
                ",\"free_refresh_verified\":" + D.B(refresh != null && refresh.Verified) +
                ",\"free_refresh_receipt_gold\":" + (refresh == null || refresh.Receipt == null ? "null" : refresh.Receipt.Gold.ToString(CultureInfo.InvariantCulture)) +
                ",\"free_refresh_request_sequence\":" + (refresh == null ? 0 : refresh.sequence) +
                ",\"free_refresh_result\":" + (refresh == null || refresh.Receipt == null ? "null" : refresh.Receipt.Result.ToString(CultureInfo.InvariantCulture)) +
                ",\"free_refresh_response_provenance\":\"operation_sequence_callback\"" +
                ",\"skip_invoked\":" + D.B(skipped) + ",\"battle_closed\":" + D.B(battleClosed) +
                ",\"stock_callback_returned\":" + D.B(battleCallbackReturned || completedReceipt != null && completedReceipt.CallbackReturned) +
                ",\"account_progress_correlated\":" + D.B(challengesCompleted != 0 || receipt != null && receipt.Result == 0 && baseline != null && available && frame.Count == baseline.Count + 1) +
                ",\"won\":" + (receipt != null ? D.B(receipt.Win) : completedReceipt != null ? D.B(completedReceipt.Win) : "null") +
                ",\"response_provenance\":\"request_handler_operation_sequence\"}";
        }
        internal string Json()
        { return "{\"schema\":1,\"revision\":" + revision + ",\"view\":" + ViewJson() + ",\"operation\":" + OperationJson() + "}"; }

        // Unlike the existing opcode tap, this callback is supplied to the
        // exact Send<T> invocation and carries this request as its callback arg.
        // It forwards the stock callback once, preserving its list/model update.
        private sealed class RefreshRequest
        {
            private readonly DailyPointRaceModel model;
            private readonly Frame before;
            private readonly string operation;
            internal readonly int sequence;
            private readonly DevelopmentNormalCallback stock;
            private int calls;
            internal bool Returned, Verified;
            internal string Error = "";
            internal RefreshReceipt Receipt;

            internal RefreshRequest(DailyPointRaceModel model, Frame before, string operation, int sequence)
            {
                this.model = model; this.before = before; this.operation = operation; this.sequence = sequence;
                MethodInfo callback = D.Method(before.Panel.GetType(), "sc_pointrace_refresh_0x467",
                    new Type[] { typeof(MemoryStream), typeof(object) }, false, typeof(void));
                stock = (DevelopmentNormalCallback)Delegate.CreateDelegate(typeof(DevelopmentNormalCallback), before.Panel, callback);
            }
            internal void Send()
            {
                Type packetType = null;
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                    if (assembly.GetName().Name == "pbdata")
                        packetType = assembly.GetType("game.net.data.CtoS.CSPointraceRefresh_0x467", false);
                if (packetType == null) throw new ProbeFailure("pointrace_refresh_packet_missing");
                PropertyInfo cost = packetType.GetProperty("gold_consume");
                if (cost == null || cost.PropertyType != typeof(uint) || !cost.CanWrite)
                    throw new ProbeFailure("pointrace_refresh_packet_schema_changed");
                object packet = Activator.CreateInstance(packetType);
                cost.SetValue(packet, (uint)0, null);
                Type callbackType = Model.FirstType("game.net.tcp_base+delegate_handler");
                Delegate callback = Delegate.CreateDelegate(callbackType, this, D.Method(GetType(), "Dispatch",
                    new Type[] { typeof(MemoryStream), typeof(object) }, false, typeof(void)));
                MethodInfo send = null;
                foreach (MethodInfo method in Model.FirstType("game.net.tcp_base").GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    ParameterInfo[] p = method.GetParameters();
                    if (method.Name != "Send" || !method.IsGenericMethodDefinition || method.GetGenericArguments().Length != 1 ||
                        p.Length != 4 || p[0].ParameterType != typeof(ushort) || !p[1].ParameterType.IsGenericParameter ||
                        p[2].ParameterType != callbackType || p[3].ParameterType != typeof(object) || method.ReturnType != typeof(void)) continue;
                    if (send != null) throw new ProbeFailure("pointrace_refresh_send_ambiguous");
                    send = method.MakeGenericMethod(packetType);
                }
                if (send == null || !model.frame.SameOffer(before) || !model.Read(null).SameOffer(before) ||
                    model.id != operation || model.sequence != sequence || !ShouldRefresh(before) || !RefreshTransportIdle(before))
                    throw new ProbeFailure("pointrace_refresh_send_boundary_changed");
                D.Call(before.Panel, "DisableAll");
                send.Invoke(before.Client, new object[] { RefreshOp, packet, callback, this });
            }
            private void Dispatch(MemoryStream stream, object arg)
            {
                try
                {
                    if (++calls != 1 || !ReferenceEquals(arg, this) || model.id != operation || model.sequence != sequence ||
                        !ReferenceEquals(model.refresh, this)) throw new ProbeFailure("pointrace_refresh_callback_owner_changed");
                    Frame prior = model.Read(null);
                    if (!Unchanged(prior) || prior.Refreshes != 0 || prior.CandidateState != before.CandidateState)
                        throw new ProbeFailure("pointrace_refresh_callback_boundary_changed");
                    if (stream == null) throw new ProbeFailure("pointrace_refresh_reply_timeout");
                    Receipt = RefreshReceipt.Decode(stream.ToArray());
                    stock(stream, null);
                    Frame after = model.Read(null);
                    if (!Unchanged(after) || Receipt.Gold != 0 ||
                        after.Refreshes != (Receipt.Result == 0 ? 1 : 0) ||
                        (Receipt.Result == 0 ? !Receipt.HasChallengers || after.CandidateState != Receipt.Candidates :
                            after.CandidateState != before.CandidateState))
                        throw new ProbeFailure("pointrace_refresh_cost_count_or_list_changed");
                    Verified = Receipt.Result == 0;
                    Returned = true;
                }
                catch (Exception error)
                { Error = error is ProbeFailure ? error.Message : "pointrace_refresh_reply_invalid"; }
            }
            private bool Unchanged(Frame value)
            { return value.SameOwner(before) && value.SamePremium(before) && value.Count == before.Count &&
                value.Cooldown == before.Cooldown && ReferenceEquals(value.Panel, before.Panel) &&
                value.InScene && !value.Loading && !value.Battling; }
        }
        private sealed class RefreshReceipt
        {
            internal uint Result, Gold;
            internal bool HasChallengers;
            internal string Candidates = "";
            internal static RefreshReceipt Decode(byte[] body)
            {
                RefreshReceipt value = new RefreshReceipt(); Reader r = new Reader(body, 0, body.Length);
                bool result = false, gold = false;
                while (!r.End)
                {
                    int wire, tag = r.Tag(out wire);
                    if ((tag == 1 || tag == 2) && wire == 0)
                    {
                        uint n = checked((uint)r.Varint());
                        if (tag == 1) { if (result) throw new FormatException(); value.Result = n; result = true; }
                        else { if (gold) throw new FormatException(); value.Gold = n; gold = true; }
                    }
                    else if (tag == 3 && wire == 2)
                    {
                        if (value.HasChallengers) throw new FormatException();
                        value.HasChallengers = true;
                        Reader list = r.Message(); int count = 0;
                        while (!list.End)
                        {
                            int itemWire, item = list.Tag(out itemWire);
                            if (item != 1 || itemWire != 2) { list.Skip(itemWire); continue; }
                            if (++count > 5) throw new FormatException();
                            Reader challenger = list.Message(); ulong opponent = 0; uint pos = 0, score = 0; bool killed = false;
                            int fields = 0;
                            while (!challenger.End)
                            {
                                int fieldWire, field = challenger.Tag(out fieldWire);
                                if (fieldWire != 0 || field != 1 && field != 2 && field != 5 && field != 8)
                                { challenger.Skip(fieldWire); continue; }
                                int bit = 1 << field;
                                if ((fields & bit) != 0) throw new FormatException();
                                fields |= bit; ulong n = challenger.Varint();
                                if (field == 1) opponent = n;
                                else if (field == 2) pos = checked((uint)n);
                                else if (field == 5) score = checked((uint)n);
                                else { if (n > 1) throw new FormatException(); killed = n != 0; }
                            }
                            if (fields != ((1 << 1) | (1 << 2) | (1 << 5) | (1 << 8))) throw new FormatException();
                            value.Candidates += Candidate(opponent, pos, score, killed);
                        }
                    }
                    else r.Skip(wire);
                }
                if (!result) throw new FormatException();
                return value;
            }
        }

        private ReplyTap Attach(ushort opcode, Component receiver, string method, Action<byte[]> read, DevelopmentPublication check = null,
            Action<byte[]> preflight = null)
        {
            IDictionary handlers = D.Read(frame.Client, "m_handlers") as IDictionary;
            object handler = handlers == null ? null : handlers[opcode];
            Delegate original = handler == null ? null : D.Read(handler, "handler") as Delegate;
            if (original == null || original.GetInvocationList().Length != 1 || !ReferenceEquals(original.Target, receiver) ||
                original.Method.Name != method || D.Read(handler, "arg") != null) throw new ProbeFailure("pointrace_handler_changed");
            return new ReplyTap(frame.Client, handlers, opcode, handler, original, read, check, preflight);
        }
        private sealed class ReplyTap
        {
            private readonly object client, handler;
            private readonly IDictionary handlers;
            private readonly ushort opcode;
            private readonly Delegate original, wrapper;
            private readonly FieldInfo field;
            private readonly DevelopmentNormalCallback forward;
            private readonly Action<byte[]> read;
            private readonly Action<byte[]> preflight;
            private readonly DevelopmentPublication check;
            private int calls;
            private volatile bool armed = true;
            internal bool Returned;
            internal string Error = "";
            internal ReplyTap(object client, IDictionary handlers, ushort opcode, object handler, Delegate original, Action<byte[]> read, DevelopmentPublication check,
                Action<byte[]> preflight)
            {
                this.client = client; this.handlers = handlers; this.opcode = opcode; this.handler = handler; this.original = original; this.read = read; this.check = check; this.preflight = preflight;
                field = handler.GetType().GetField("handler", D.Instance);
                forward = (DevelopmentNormalCallback)Delegate.CreateDelegate(typeof(DevelopmentNormalCallback), original.Target, original.Method);
                wrapper = Delegate.CreateDelegate(original.GetType(), this, GetType().GetMethod("Dispatch", D.Instance));
                field.SetValue(handler, wrapper);
            }
            private void Dispatch(MemoryStream stream, object arg)
            {
                if (opcode == BattleOp) { DispatchBattle(stream, arg); return; }
                byte[] body = armed && stream != null ? stream.ToArray() : null;
                if (armed && body == null) Error = "pointrace_reply_timeout";
                if (armed)
                {
                    try
                    {
                        if (arg != null || handlers.Contains(opcode) && !ReferenceEquals(handlers[opcode], handler))
                            throw new ProbeFailure("pointrace_callback_handler_changed");
                        if (check != null) check();
                    }
                    catch (Exception error) { Error = error is ProbeFailure ? error.Message : "pointrace_callback_boundary_changed"; return; }
                }
                try { forward(stream, arg); }
                catch { if (armed) Error = "pointrace_stock_callback_failed"; throw; }
                if (!armed || body == null || Error != "") return;
                try { read(body); Returned = true; }
                catch { Error = "pointrace_reply_invalid"; }
            }
            private void DispatchBattle(MemoryStream stream, object arg)
            {
                lock (client)
                {
                    if (Interlocked.Increment(ref calls) != 1)
                    { if (Error == "") Error = "pointrace_duplicate_callback"; return; }
                    if (!armed) { if (Error == "") Error = "pointrace_callback_disarmed"; return; }
                    byte[] body;
                    try
                    {
                        if (stream == null) throw new ProbeFailure("pointrace_reply_timeout");
                        if (!stream.CanRead || stream.Position != 0 || stream.Length == 0 || stream.Length > BattleReplyPreflight.MaxPacketBytes)
                            throw new ProbeFailure("pointrace_reply_stream_invalid");
                        if (arg != null || D.Read(handler, "arg") != null || !ReferenceEquals(D.Read(client, "m_handlers"), handlers) ||
                            !ReferenceEquals(field.GetValue(handler), wrapper) ||
                            handlers.Contains(opcode) && !ReferenceEquals(handlers[opcode], handler))
                            throw new ProbeFailure("pointrace_callback_handler_changed");
                        if (check == null || preflight == null) throw new ProbeFailure("pointrace_preflight_missing");
                        body = stream.ToArray(); check(); preflight(body);
                    }
                    catch (Exception error)
                    { Error = error is ProbeFailure ? error.Message : "pointrace_reply_invalid"; return; }
                    if (!armed || Error != "") return;
                    try { forward(stream, arg); }
                    catch { Error = "pointrace_stock_callback_failed"; throw; }
                    try { read(body); Returned = true; }
                    catch (Exception error) { Error = error is ProbeFailure ? error.Message : "pointrace_reply_invalid"; }
                }
            }
            internal bool Restore()
            {
                armed = false;
                if (!Monitor.TryEnter(client, 0)) return false;
                try
                {
                    // This tap replaced one delegate, not the persistent stock
                    // registration. Never remove an opcode or adopt a changed
                    // handler as proof that our wrapper was released.
                    if (opcode == EnterOp)
                    {
                        if (!ReferenceEquals(D.Read(client, "m_handlers"), handlers) || !handlers.Contains(opcode) ||
                            !ReferenceEquals(handlers[opcode], handler) || D.Read(handler, "arg") != null ||
                            (float)D.Read(handler, "life") != -1f) return false;
                        object current = field.GetValue(handler);
                        if (ReferenceEquals(current, wrapper)) field.SetValue(handler, original);
                        else if (!ReferenceEquals(current, original)) return false;
                        return ReferenceEquals(field.GetValue(handler), original);
                    }
                    // Pending battle replies retain the closed wrapper and its
                    // one-shot preflight; their settlement is still unresolved.
                    return true;
                }
                finally { Monitor.Exit(client); }
            }
        }
        private sealed class Receipt
        {
            internal uint Result, Gold;
            internal bool Win;
            internal ulong Report, Opponent, ReportOpponent, RequestedOpponent;
            internal object DetachedReport;
            private byte[] compressedReport;
            internal string OperationId;
            internal int Sequence;
            internal bool CallbackReturned;
            internal static Receipt Decode(byte[] bytes)
            {
                object native = BattleReplyPreflight.Deserialize(bytes, "game.net.data.StoC.SCPointraceChallenge_0x465");
                Reader r = new Reader(bytes, 0, bytes.Length); Receipt value = new Receipt(); int fields = 0;
                value.compressedReport = native.GetType().GetProperty("report").GetValue(native, null) as byte[];
                while (!r.End)
                {
                    int wire, tag = r.Tag(out wire);
                    if (wire == 0 && (tag == 1 || tag == 2 || tag == 8 || tag == 10))
                    {
                        int bit = 1 << tag;
                        if ((fields & bit) != 0) throw new FormatException();
                        fields |= bit;
                        ulong n = r.Varint();
                        if (tag == 1) value.Result = checked((uint)n);
                        else if (tag == 2) { if (n > 1) throw new FormatException(); value.Win = n != 0; }
                        else if (tag == 8) value.Gold = checked((uint)n);
                        else value.Opponent = n;
                    }
                    else if (wire == 2 && tag == 5)
                    {
                        if ((fields & (1 << 5)) != 0) throw new FormatException();
                        fields |= 1 << 5;
                        Reader envelope = r.Message();
                        bool hasReport = false;
                        while (!envelope.End)
                        {
                            int innerWire, inner = envelope.Tag(out innerWire);
                            if (inner != 1 || innerWire != 2) { envelope.Skip(innerWire); continue; }
                            if (hasReport) throw new FormatException();
                            hasReport = true;
                            Reader report = envelope.Message();
                            int reportFields = 0;
                            while (!report.End)
                            {
                                int reportWire, field = report.Tag(out reportWire);
                                if ((field == 1 || field == 2) && reportWire == 0)
                                {
                                    int reportBit = 1 << field;
                                    if ((reportFields & reportBit) != 0) throw new FormatException();
                                    reportFields |= reportBit;
                                    ulong n = report.Varint();
                                    if (field == 1) value.ReportOpponent = n; else value.Report = n;
                                }
                                else report.Skip(reportWire);
                            }
                        }
                    }
                    else r.Skip(wire);
                }
                if ((fields & (1 << 1)) == 0 || value.Result == 0 && value.Report == 0) throw new FormatException();
                return value;
            }
            internal void Preflight(ulong opponent)
            {
                if (Gold != 0) throw new ProbeFailure("pointrace_reply_premium_unexpected");
                if (Result == 1)
                {
                    // This known rejection returns before every native mutation.
                    // Any available identity must still agree with the request.
                    if (Opponent != 0 && Opponent != opponent || ReportOpponent != 0 && ReportOpponent != opponent)
                        throw new ProbeFailure("pointrace_reply_target_changed");
                    return;
                }
                if (Result != 0) throw new ProbeFailure("pointrace_reply_result_unknown");
                if (opponent == 0 || Opponent != opponent || ReportOpponent != opponent || Report == 0)
                    throw new ProbeFailure("pointrace_reply_target_changed");
                DetachedReport = BattleReplyPreflight.Validate(compressedReport, Win);
            }
        }
    }
}
