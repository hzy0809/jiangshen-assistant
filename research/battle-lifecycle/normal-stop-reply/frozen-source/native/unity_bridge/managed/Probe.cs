using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace JiangShen.AssistantRuntime
{
    public static class Entry
    {
        internal const string BridgeId = "jiangshen-assistant-win32-v1";
#if JSB_WOOD_PROBE_TEST
        internal static readonly string SessionsRoot = Path.Combine(Path.GetTempPath(), "DailyWoodProbeSessions");
#elif JSB_REFINE_PROBE_TEST
        internal static readonly string SessionsRoot = Path.Combine(Path.GetTempPath(), "DailyRefineProbeSessions");
#elif JSB_LIFECYCLE_TEST
        internal const string SessionsRoot = @"G:\development\enjoy\jiangshen-assistant\native\unity_bridge\build\managed-tests\sessions";
#else
        internal const string SessionsRoot = @"G:\development\enjoy\jiangshen-assistant\data\object_sessions";
#endif
        private static Probe activeProbe;
        [DllImport("kernel32.dll", ExactSpelling = true)] internal static extern int GetCurrentThreadId();
        [DllImport("kernel32.dll", ExactSpelling = true)] internal static extern uint GetCurrentProcessId();
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateMutexW(IntPtr attributes, bool initiallyOwned, string name);
        [DllImport("kernel32.dll", ExactSpelling = true)] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", ExactSpelling = true)] private static extern void SetLastError(uint error);

        public static string Bootstrap(string absoluteSessionDir, string nonce, int verifiedUnityNativeThreadId)
        {
            if (verifiedUnityNativeThreadId <= 0 || GetCurrentThreadId() != verifiedUnityNativeThreadId) return "BOOTSTRAP_REJECTED;thread";
            if (!Command.ValidNonce(nonce)) return "BOOTSTRAP_REJECTED;nonce";
            IntPtr guard = IntPtr.Zero; ReceiptStore store = null; GameObject helper = null; Probe probe = null;
            try
            {
                string expected = Path.Combine(SessionsRoot, nonce);
                if (!Path.IsPathRooted(absoluteSessionDir) || !String.Equals(Path.GetFullPath(absoluteSessionDir), expected, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(expected) ||
                    (File.GetAttributes(expected) & FileAttributes.ReparsePoint) != 0) return "BOOTSTRAP_REJECTED;session_path";
                if (activeProbe != null) { activeProbe.RetireForRetry(); activeProbe = null; }
                SessionRegistry.ResetForRetry();
                long creation, exit, kernel, user;
                if (!GetProcessTimes(new IntPtr(-1), out creation, out exit, out kernel, out user)) return "BOOTSTRAP_REJECTED;process_identity";
                string guardName = "Local\\JSAssistantActive-" + GetCurrentProcessId().ToString("x8", CultureInfo.InvariantCulture) + "-" + creation.ToString("x16", CultureInfo.InvariantCulture);
                SetLastError(0); guard = CreateMutexW(IntPtr.Zero, false, guardName);
                if (guard == IntPtr.Zero) return "BOOTSTRAP_REJECTED;active_guard";
                store = new ReceiptStore(expected); // All file prerequisites precede own Unity object creation.
                if (!SessionRegistry.Acquire(nonce)) { store.Close(); CloseHandle(guard); return "BOOTSTRAP_REJECTED;already_started"; }
                helper = new GameObject("JSAssistantRuntime_" + nonce); helper.hideFlags = HideFlags.HideInHierarchy;
                UObject.DontDestroyOnLoad(helper); // Own helper survives ordinary scene changes while the GUI lease is alive.
                probe = helper.AddComponent<Probe>();
                probe.Initialize(store, expected, nonce, verifiedUnityNativeThreadId, Thread.CurrentThread.ManagedThreadId, creation, guard);
                activeProbe = probe;
                return "BOOTSTRAP_OK;protocol=1;native_tid=" + verifiedUnityNativeThreadId.ToString(CultureInfo.InvariantCulture) + ";managed_tid=" + Thread.CurrentThread.ManagedThreadId.ToString(CultureInfo.InvariantCulture) + ";await=update_ready";
            }
            catch
            {
                if (probe != null) { probe.BootstrapFailed(); }
                else
                {
                    if (helper != null) UObject.Destroy(helper);
                    if (store != null) store.Close();
                    if (guard != IntPtr.Zero) CloseHandle(guard);
                }
                return "BOOTSTRAP_REJECTED;initialization";
            }
        }
        internal static void ReleaseFromOnDestroy(string nonce, IntPtr guard, bool unknown)
        {
            SessionRegistry.ReleaseFromOnDestroy(nonce, unknown);
            if (guard != IntPtr.Zero) CloseHandle(guard);
        }
    }

    public sealed class Probe : MonoBehaviour
    {
        internal void RetireForRetry()
        {
            ready = false;
            enabled = false;
            UObject.Destroy(gameObject);
        }
        private ReceiptStore store;
        private SessionLifecycle life;
        private string sessionDir, nonce, lifecycle = "bootstrapping", reason = "", ackName = "", ackStatus = "";
        private int nativeId, managedId, callbacks, lastFrame = -1, verifiedFrames, ackSequence, phase, phaseCallbacks, phaseTick, recoveryTick, nextRecoveryTick;
        private long creationFiletime;
        private IntPtr activeGuard;
        private bool initialized, ready, finishing, destroyed, threadFailureLogged, mutationAttempted, recoveryActive;
        private bool actionObserved, restoreObserved, restoreMethodInvoked, actionFailure;
        private int actionSequence;
        private string actionOutcome = "NONE", resourceStatus = "UNVERIFIED";
        private Model model;
        private Snapshot original, latest, lastResourceObservation;
        private ResourceObservation resources;
        private CampaignModel campaign, challenge;
        private DevelopmentModel development;
        private DailyGuildModel dailyGuild;
        private DailyRewardModel dailyReward;
        private DailyTimedModel dailyTimed;
        private DailyStoneModel dailyStone;
        private DailyMingshiModel dailyMingshi;
        private DailyArenaChestModel dailyArena;
        private DailySoulModel dailySoul;
        private DailyRefineModel dailyRefine;
        private DailyScriptAdapter dailyScript;
        private DailyPopupModel dailyPopup;
        private BattleSettlement battleSettlement;
#if JSB_DAILY_WOOD_ENABLED
        private DailyWoodModel dailyWood;
#endif
        private PlayerStateRefreshModel playerRefresh;
        private PlayerStateRefreshModel dailyRefresh;
        private Command refreshCommand;
        private Command guiCommand;
        private Command lastWoodCommand;
        private int guiQueuedTick, guiSequence, guiConsumedSequence;
        private string guiPhase = "none";
        private bool guiInvocationAttempted;
        private string publishedBusinessSignature = "";
        private string firstFailureJson = "null";
        private bool commandReadFailureLogged;
        private bool guildObserved;
        private bool dailyRefreshObserved;
        private bool dailyRewardObserved;
        private bool dailyTimedMutationAttempted;
        private bool dailyStoneMutationAttempted;
        private bool dailyMingshiMutationAttempted;
        private bool dailySoulMutationAttempted;
        private bool dailyRefineMutationAttempted;
#if JSB_DAILY_REWARD_ENABLED
        private static readonly bool DailyRewardEnabled = true;
#else
        private static readonly bool DailyRewardEnabled = false;
#endif
#if JSB_DAILY_TIMED_ENABLED
        private static readonly bool DailyTimedEnabled = true;
#else
        private static readonly bool DailyTimedEnabled = false;
#endif
#if JSB_DAILY_STONE_ENABLED
        private static readonly bool DailyStoneEnabled = true;
#else
        private static readonly bool DailyStoneEnabled = false;
#endif
#if JSB_DAILY_MINGSHI_ENABLED
        private static readonly bool DailyMingshiEnabled = true;
#else
        private static readonly bool DailyMingshiEnabled = false;
#endif
#if JSB_DAILY_ARENA_ENABLED
        private static readonly bool DailyArenaEnabled = true;
#else
        private static readonly bool DailyArenaEnabled = false;
#endif
#if JSB_DAILY_SOUL_ENABLED
        private static readonly bool DailySoulEnabled = true;
#else
        private static readonly bool DailySoulEnabled = false;
#endif
#if JSB_DAILY_REFINE_ENABLED
        private static readonly bool DailyRefineEnabled = true;
#else
        private static readonly bool DailyRefineEnabled = false;
#endif
#if JSB_DAILY_WOOD_ENABLED
        private static readonly bool DailyWoodEnabled = true;
        private bool dailyWoodMutationAttempted;
#else
        private static readonly bool DailyWoodEnabled = false;
#endif
        private sealed class GuiAdmissionCancelled : Exception { }
        private const int GuiDeadlineMilliseconds = 1500;

        internal void Initialize(ReceiptStore receiptStore, string directory, string sessionNonce, int nativeThread, int managedThread, long creation, IntPtr guard)
        {
            store = receiptStore; sessionDir = directory; nonce = sessionNonce; nativeId = nativeThread; managedId = managedThread;
            creationFiletime = creation; activeGuard = guard; life = new SessionLifecycle(Environment.TickCount); initialized = true;
            BattleLaunch.Initialize(nonce, delegate { return !life.StopRequested && !life.LeaseExpired(Environment.TickCount) && !destroyed; });
            Emit("bootstrap", "\"target_access\":false,\"await_distinct_update_frames\":2"); Publish();
        }
        internal void BootstrapFailed()
        {
            if (!initialized) return; life.RequestStop("bootstrap_failed"); reason = "bootstrap_failed"; Finish();
        }
        private void Update()
        {
            if (!initialized || destroyed || finishing) return;
            try
            {
                if (Entry.GetCurrentThreadId() != nativeId || Thread.CurrentThread.ManagedThreadId != managedId)
                {
                    if (!threadFailureLogged) TryEmit("thread_invariant_failed", "\"target_access\":false");
                    threadFailureLogged = true; life.RequestStop("thread_invariant_failed"); reason = "thread_invariant_failed";
                    return; // Never access a target or Unity API from an unexpected thread.
                }
                callbacks++;
                int frame = Time.frameCount;
                if (frame > lastFrame) verifiedFrames++;
                else if (frame < lastFrame) throw new ProbeFailure("frame_regressed");
                lastFrame = frame;
                // Expiration is evaluated before reading any queued command. A late
                // heartbeat or action cannot resurrect an expired/stopping session.
                if (life.LeaseExpired(Environment.TickCount)) life.RequestStop("heartbeat_timeout");
                BattleLaunch.Validate();
                if (BattleLaunch.Error != "")
                {
                    life.RecoveryUnknown = true; reason = BattleLaunch.Error;
                    life.RequestStop("battle_launch_unknown"); Finish(); return;
                }
                if (!ready)
                {
                    if (life.StopRequested) { Finish(); return; }
                    if (verifiedFrames < 2) { Emit("update_witness", "\"distinct_frames\":" + verifiedFrames); Publish(); return; }
                    ready = true; lifecycle = "ready";
                    Emit("update_ready", "\"distinct_frames\":" + verifiedFrames);
                    model = new Model(); battleSettlement = new BattleSettlement(TryEmit, BattleSettlementAllowed);
                    latest = model.Capture(); campaign = new CampaignModel(model); challenge = new CampaignModel(model, true); CaptureCampaigns();
                    development = new DevelopmentModel(model); playerRefresh = new PlayerStateRefreshModel(); dailyRefresh = new PlayerStateRefreshModel(true); dailyGuild = new DailyGuildModel();
                    dailyReward = new DailyRewardModel(DailyRewardEnabled ? (DailyRewardCallbackInstaller)DevelopmentCallbackCapture.InstallDailyReward : null, ReadDailyRewardContext);
                    dailyPopup = new DailyPopupModel(Emit);
                    CaptureDevelopment(); Emit("initial_snapshot", latest.Json()); Publish(); return;
                }
                if (dailySoul != null && dailySoul.NeedsUiCleanup)
                {
                    dailySoul.TryUiCleanup(ReadDailySoulRecoveryContext);
                    if (life.StopRequested && phase == 0) { Finish(); return; }
                }
                if (guiCommand != null)
                {
                    PollStopOnly();
                    if (life.StopRequested) { Finish(); return; }
                    if (Elapsed(guiQueuedTick) >= GuiDeadlineMilliseconds) CancelGui("scenario_gui_callback_unavailable");
                    else { CaptureCampaigns(); CaptureDevelopment(); PublishBusinessChange(); }
                    return;
                }
                // A business fight is never an ActionPending transaction. Sampling
                // is bounded, main-thread only, and leaves heartbeat/stop runnable.
                CaptureCampaigns();
                CaptureDevelopment();
                if (phase != 0)
                {
                    PollStopOnly();
                    if (life.StopRequested && phase <= 2) TryStartRestore(life.StopReason);
                    else Advance();
                    return;
                }
                if (life.StopRequested) { Finish(); return; }
                Poll();
                PublishBusinessChange();
            }
            catch (Exception error)
            {
                RememberFailure(error, "update");
                reason = Failure(error); TryEmit("failure", "\"reason\":" + Command.Quote(reason) + ",\"failure\":" + firstFailureJson);
                actionFailure = true; life.RequestStop("failure");
                if (life.ActionPending && mutationAttempted) EnterRecovery(reason);
                else { if (life.ActionPending) { actionOutcome = "FAIL"; life.CompleteAction(true); } Finish(); }
            }
        }
        private void OnGUI()
        {
            if (!initialized || !ready || destroyed || finishing || Event.current == null) return;
            if (Entry.GetCurrentThreadId() != nativeId || Thread.CurrentThread.ManagedThreadId != managedId)
            { threadFailureLogged = true; life.RequestStop("thread_invariant_failed"); reason = "thread_invariant_failed"; return; }
            PollStopOnly(); // A published stop can precede this GUI event without an intervening Update.
            if (life.LeaseExpired(Environment.TickCount)) life.RequestStop("heartbeat_timeout");
            if (life.StopRequested) { Finish(); return; }
            // A native callback can settle the mode during Update before this
            // GUI event. Finish its already-owned result before any next GUI
            // command can close that result or start another operation.
            if (battleSettlement != null)
            {
                try
                {
                    ObserveBattleSettlement();
                    if (GUI.enabled && battleSettlement.NeedsAdvance) battleSettlement.AdvanceOwnedAttempt();
                }
                catch (Exception error)
                {
                    RememberFailure(error, "battle_presentation"); reason = Failure(error);
                    TryEmit("battle_settlement_failure", "\"reason\":" + Command.Quote(reason));
                    life.RequestStop("battle_settlement_failure"); Finish(); return;
                }
            }
            if (guiCommand != null && battleSettlement != null && battleSettlement.BlocksGuiInvocation)
            {
                // No GUI command has been consumed or invoked. Preserve the
                // current report while its exact callback/rejection retry owns
                // settlement; an adapter's later select/start cannot bypass it.
                CancelGui("battle_settlement_pending_not_invoked"); return;
            }
            if (guiCommand == null)
            {
                try
                {
                    if ((campaign != null && (campaign.Observer.Status == "SUBMITTED" || campaign.Observer.Status == "WAITING")) ||
                        (challenge != null && (challenge.Observer.Status == "SUBMITTED" || challenge.Observer.Status == "WAITING")) ||
                        (dailyScript != null && dailyScript.Busy && !dailyScript.OwnsBattlePresentation)) SkipBattlePresentation();
                }
                catch (Exception error)
                {
                    RememberFailure(error, "battle_presentation"); reason = Failure(error);
                    TryEmit("battle_settlement_failure", "\"reason\":" + Command.Quote(reason));
                    life.RequestStop("battle_settlement_failure"); Finish();
                }
                return;
            }
            if (Elapsed(guiQueuedTick) >= GuiDeadlineMilliseconds) { CancelGui("scenario_gui_callback_unavailable"); return; }
            Command command = guiCommand;
            guiCommand = null; // Consume before observation, publication or game calls; multiple GUI events cannot replay.
            if (command.Sequence <= guiConsumedSequence) return;
            guiConsumedSequence = command.Sequence; guiPhase = "consumed"; guiInvocationAttempted = false;
            CampaignModel guiCampaign = CampaignFor(command);
            bool previousMutation = guiCampaign.Observer.MutationAttempted;
            bool rewardCommand = command.Name == "daily-reward-command";
            bool timedCommand = command.Name == "daily-timed-command";
            bool stoneCommand = command.Name == "daily-stone-command";
            bool mingshiCommand = command.Name == "daily-mingshi-command";
            bool arenaCommand = command.Name == "daily-arena-command";
            bool soulCommand = command.Name == "daily-soul-command";
            bool refineCommand = command.Name == "daily-refine-command";
            bool woodCommand = command.Name == "daily-wood-command";
            bool scriptCommand = command.Name == "daily-script";
            bool popupCommand = command.Name == "daily-popup-command";
            bool scopedCommand = rewardCommand || timedCommand || stoneCommand || mingshiCommand || arenaCommand || soulCommand || refineCommand || woodCommand || scriptCommand || popupCommand;
            try
            {
                BattleLaunch.Select(command);
                CaptureCampaigns();
                CaptureDevelopment();
                string why;
                bool invoked;
                if (!woodCommand && DailyWoodBusy())
                {
                    why = "daily_wood_operation_owned";
                    GuiNotInvoked(command, why);
                    invoked = false;
                }
                else if (!refineCommand && DailyRefineBusy())
                {
                    why = "daily_refine_operation_owned";
                    GuiNotInvoked(command, why);
                    invoked = false;
                }
                else if (!soulCommand && DailySoulBusy())
                {
                    why = "daily_soul_operation_owned";
                    GuiNotInvoked(command, why);
                    invoked = false;
                }
                else if (!arenaCommand && DailyArenaBusy())
                {
                    why = "daily_arena_operation_owned";
                    GuiNotInvoked(command, why);
                    invoked = false;
                }
                else if (IsRefresh(command.Name) && DailyMingshiBusy())
                {
                    why = "daily_mingshi_operation_owned";
                    RefreshFor(command).NotInvoked(why);
                    invoked = false;
                }
                else if (command.Name == "daily-state-refresh" && (RefreshBusinessBusy() || command.ExpectedRevision != development.Revision))
                { why = "daily_refresh_gui_revision_or_business_changed"; dailyRefresh.NotInvoked(why); invoked = false; }
                else if (IsRefresh(command.Name)) invoked = RefreshFor(command).ExecuteGui(DevelopmentModel.ConvertCommand(command), PublishGuiOwnership, out why);
                else if (command.Name == "daily-guild-command") invoked = dailyGuild.ExecuteGui(command, PublishGuiOwnership, out why);
                else if (rewardCommand)
                {
                    if (!DailyRewardEnabled || DailyRewardOtherBusinessBusy())
                    { why = "daily_reward_gui_disabled_or_business_changed"; dailyReward.NotInvoked(); invoked = false; }
                    else invoked = dailyReward.ExecuteGui(command.Sequence, command.ExpectedRevision, command.DailyRewardAction,
                        command.OperationId, command.DailyRewardCharacterId, command.DailyRewardCategoryId,
                        PublishScopedGuiOwnership, BeginScopedGuiInvocation, out why);
                }
                else if (timedCommand)
                {
                    if (dailyTimed == null || DailyTimedOtherBusinessBusy())
                    { why = "daily_timed_gui_disabled_or_business_changed"; if (dailyTimed != null) dailyTimed.NotInvoked(why); invoked = false; }
                    else invoked = dailyTimed.ExecuteGui(PublishScopedGuiOwnership, BeginDailyTimedGuiInvocation, out why);
                }
                else if (stoneCommand)
                {
                    if (dailyStone == null || DailyStoneOtherBusinessBusy())
                    { why = "daily_stone_gui_disabled_or_business_changed"; if (dailyStone != null) dailyStone.NotInvoked(why); invoked = false; }
                    else
                    {
                        invoked = dailyStone.ExecuteGui(ReadDailyStoneContext, PublishScopedGuiOwnership,
                            delegate(DailyStoneInvocationCommit sample, DailyStoneInvocationCommit check)
                            { return BeginDailyStoneGuiInvocation(command, sample, check); }, out why);
                        dailyStoneMutationAttempted |= dailyStone.MutationAttempted;
                    }
                }
                else if (mingshiCommand)
                {
                    if (dailyMingshi == null || DailyMingshiOtherBusinessBusy())
                    { why = "daily_mingshi_gui_disabled_or_business_changed"; if (dailyMingshi != null) dailyMingshi.NotInvoked(why); invoked = false; }
                    else
                    {
                        invoked = dailyMingshi.ExecuteGui(ReadDailyMingshiContext, PublishScopedGuiOwnership,
                            delegate(DailyMingshiInvocationCheck prepare, DailyMingshiInvocationCheck check)
                            { return BeginDailyMingshiGuiInvocation(command, prepare, check); }, out why);
                        dailyMingshiMutationAttempted |= dailyMingshi.MutationAttempted;
                    }
                }
                else if (arenaCommand)
                {
                    if (dailyArena == null || DailyArenaOtherBusinessBusy())
                    {
                        why = "daily_arena_gui_disabled_or_business_changed";
                        if (dailyArena != null) dailyArena.NotInvoked();
                        invoked = false;
                    }
                    else
                    {
                        try
                        {
                            invoked = dailyArena.ExecuteGui(command.Sequence, command.ExpectedRevision, command.DailyArenaAction,
                                command.OperationId, command.DailyArenaCharacterId, ReadDailyArenaContext, PublishScopedGuiOwnership,
                                delegate(DailyArenaInvocationDeadline withinDeadline)
                                { return BeginDailyArenaGuiInvocation(command, withinDeadline); }, out why);
                        }
                        finally { guiInvocationAttempted = dailyArena.GuiInvocationAttempted; }
                    }
                }
                else if (soulCommand)
                {
                    if (dailySoul == null || DailySoulOtherBusinessBusy())
                    { why = "daily_soul_gui_disabled_or_business_changed"; if (dailySoul != null) dailySoul.NotInvoked(); invoked = false; }
                    else
                    {
                        try
                        {
                            invoked = dailySoul.ExecuteGui(ReadDailySoulContext, PublishScopedGuiOwnership,
                                delegate(DailySoulInvocationCheck prepare, DailySoulInvocationCheck check)
                                { return BeginDailySoulGuiInvocation(command, prepare, check); }, out why);
                        }
                        finally
                        {
                            guiInvocationAttempted = dailySoul.GuiInvocationAttempted;
                            dailySoulMutationAttempted |= dailySoul.MutationAttempted;
                        }
                    }
                }
                else if (refineCommand)
                {
                    if (dailyRefine == null || DailyRefineOtherBusinessBusy())
                    {
                        why = "daily_refine_gui_disabled_or_business_changed";
                        if (dailyRefine != null) dailyRefine.NotInvoked(why);
                        invoked = false;
                    }
                    else
                    {
                        try
                        {
                            invoked = dailyRefine.ExecuteGui(ReadDailyRefineContext, PublishScopedGuiOwnership,
                                delegate(DailyRefineCommitCheck prepare, DailyRefineCommitCheck check)
                                { return BeginDailyRefineGuiInvocation(command, prepare, check); }, out why);
                        }
                        finally
                        {
                            guiInvocationAttempted = dailyRefine.GuiInvocationAttempted;
                            dailyRefineMutationAttempted |= dailyRefine.MutationAttempted;
                        }
                    }
                }
                else if (woodCommand)
                {
#if JSB_DAILY_WOOD_ENABLED
                    if (dailyWood == null || DailyWoodOtherBusinessBusy())
                    { why = "daily_wood_gui_disabled_or_business_changed"; if (dailyWood != null) dailyWood.NotInvoked(why); invoked = false; }
                    else
                    {
                        try
                        {
                            invoked = dailyWood.ExecuteGui(ReadDailyWoodContext, PublishScopedGuiOwnership,
                                delegate(DailyWoodAdmissionCheck prepare, DailyWoodAdmissionCheck check)
                                { return BeginDailyWoodGuiInvocation(command, prepare, check); }, out why);
                        }
                        finally
                        {
                            guiInvocationAttempted = dailyWood.GuiInvocationAttempted;
                            dailyWoodMutationAttempted |= dailyWood.MutationAttempted;
                        }
                    }
#else
                    why = "daily_wood_disabled"; invoked = false;
#endif
                }
                else if (popupCommand)
                {
                    try
                    {
                        invoked = dailyPopup.ExecuteGui(model, DailyPopupOtherBusinessBusy(), PublishScopedGuiOwnership,
                            delegate { return BeginDailyPopupGuiInvocation(command); }, out why);
                    }
                    finally { guiInvocationAttempted = dailyPopup.GuiInvocationAttempted; }
                }
                else if (scriptCommand)
                {
                    guiInvocationAttempted = true;
                    invoked = dailyScript.Execute(model, PublishScopedGuiOwnership, out why);
                }
                else if (command.Name == "development-command") invoked = development.ExecuteGui(command, PublishGuiOwnership, out why);
                else invoked = guiCampaign.ExecuteGui(command, PublishGuiOwnership, out why);
                guiPhase = scopedCommand && !guiInvocationAttempted ? "not_invoked" : invoked ? "completed" : "rejected";
                if (scopedCommand && !guiInvocationAttempted)
                {
                    PollStopOnly();
                    if (life.LeaseExpired(Environment.TickCount)) life.RequestStop("heartbeat_timeout");
                    if (life.StopRequested) throw new GuiAdmissionCancelled();
                }
                CaptureCampaigns(); CaptureDevelopment();
                if (scopedCommand)
                {
                    if (refineCommand && dailyRefine != null && dailyRefine.Rejected) invoked = false;
                    bool unknown = popupCommand ? dailyPopup.OutcomeUnknown : scriptCommand ? false : woodCommand ? guiInvocationAttempted && (!invoked || DailyWoodUnknown()) :
                        refineCommand ? dailyRefine != null && (dailyRefine.OutcomeUnknown ||
                            guiInvocationAttempted && (!invoked && dailyRefine.Busy ||
                                command.DailyRefineAction == "close" && !dailyRefine.PanelClosed)) :
                        soulCommand ? guiInvocationAttempted && (!invoked || dailySoul.OutcomeUnknown) :
                        arenaCommand ? guiInvocationAttempted && !invoked :
                        mingshiCommand ? dailyMingshi != null && dailyMingshi.OutcomeUnknown :
                        stoneCommand ? dailyStone != null && dailyStone.OutcomeUnknown :
                        guiInvocationAttempted && (!invoked || (rewardCommand ? dailyReward.OutcomeUnknown : dailyTimed.Unresolved));
                    if (unknown)
                    { guiPhase = "unknown"; if (why == "") why = woodCommand ? "daily_wood_outcome_unknown" : refineCommand ? "daily_refine_outcome_unknown" : soulCommand ? "daily_soul_outcome_unknown" : arenaCommand ? "daily_arena_outcome_unknown" : mingshiCommand ? "daily_mingshi_outcome_unknown" : stoneCommand ? "daily_stone_outcome_unknown" : rewardCommand ? "daily_reward_outcome_unknown" : "daily_timed_outcome_unknown"; }
                    else if (refineCommand && guiInvocationAttempted && !invoked) guiPhase = "rejected";
                    Ack(unknown ? "UNKNOWN" : invoked ? "COMPLETED" : "REJECTED", why);
                }
                else
                {
                    bool unresolved = IsRefresh(command.Name) && RefreshFor(command).Pending ||
                        command.Name == "daily-guild-command" && dailyGuild.Unresolved && guiInvocationAttempted;
                    Ack(invoked ? "COMPLETED" : unresolved ? "UNKNOWN" : "REJECTED", why);
                }
                Publish();
            }
            catch (GuiAdmissionCancelled)
            {
                guiPhase = "not_invoked"; guiInvocationAttempted = false; guiCampaign.Observer.MutationAttempted = previousMutation;
                if (soulCommand && dailySoul != null) dailySoul.Stop(life.StopReason);
                GuiNotInvoked(command, "stop_or_expiry_before_gui_invocation");
                if (IsGuiCommand(ackName) && ackStatus == "ACCEPTED") TryAck("REJECTED", "stop_or_expiry_before_gui_invocation");
                Finish();
            }
            catch (Exception error)
            {
                RememberFailure(error, "gui");
                bool scopedNotInvoked = scopedCommand && !guiInvocationAttempted;
                reason = "scenario_gui_exception_" + Failure(error); guiPhase = scopedNotInvoked ? "not_invoked" : "unknown";
                if (command.Name == "daily-guild-command") dailyGuild.Failed(reason);
                else if (command.Name == "daily-reward-command") dailyReward.Failed(reason);
                else if (timedCommand) dailyTimed.Stop(reason);
                else if (stoneCommand) StopDailyStone();
                else if (refineCommand) StopDailyRefine();
#if JSB_DAILY_WOOD_ENABLED
                else if (woodCommand) { if (dailyWood != null) dailyWood.Stop(reason); }
#endif
                else if (mingshiCommand) StopDailyMingshi(reason);
                else if (arenaCommand && dailyArena != null) dailyArena.Failed(reason);
                else if (soulCommand && dailySoul != null) dailySoul.Stop(reason);
                else if (IsRefresh(command.Name)) RefreshFor(command).Stop(reason);
                else if (command.Name == "development-command") development.Failed(command, reason);
                else CampaignFor(command).GuiFailed(command, guiInvocationAttempted, reason);
                life.RequestStop("scenario_gui_failure"); TryAck(scopedNotInvoked ? "REJECTED" : "UNKNOWN", reason); TryPublish(); Finish();
            }
            finally { BattleLaunch.Select(null); }
        }

        private void ObserveBattleSettlement()
        {
            if (battleSettlement == null || model == null) return;
            battleSettlement.ValidateOwnedAttempt();
            GameObject owner = model.BattleUiObject();
            Component battle = owner == null ? null : owner.GetComponent(Model.GameType("BattleUI"));
            if (battle == null) return;
            // Daily adapters verify their pinned role, cycle, source and target
            // directly. Observe before their next Capture can infer a loss from
            // a wait=false native result whose 0x408 has not yet been proved.
            BattlePresentationOwnership ownership = CampaignBattleOwnership(battle);
            if (ownership == null && dailyScript != null) ownership = dailyScript.OwnedBattlePresentation(battle);
            if (ownership != null) battleSettlement.ObserveOwned(battle, ownership);
        }
        private void SkipBattlePresentation()
        {
            battleSettlement.ValidateOwnedAttempt();
            if (!GUI.enabled) return;
            GameObject owner = model.BattleUiObject();
            if (owner == null) return;
            Component battle = owner.GetComponent(Model.GameType("BattleUI"));
            if (battle == null) return;
            // Settled modes submit their actual initial-response receipt. Their
            // native view flags alone do not prove a business settlement.
            if (dailyScript != null && dailyScript.PresentSettledBattle(battle)) return;
            BattlePresentationOwnership ownership = CampaignBattleOwnership(battle);
            if (ownership == null && dailyScript != null) ownership = dailyScript.OwnedBattlePresentation(battle);
            if (ownership != null) battleSettlement.AdvanceOwned(battle, ownership);
            else battleSettlement.Advance(battle);
        }
        private BattlePresentationOwnership CampaignBattleOwnership(Component battle)
        {
            CampaignModel owner = challenge != null && challenge.OwnsBattlePresentation(battle) ? challenge :
                campaign != null && campaign.OwnsBattlePresentation(battle) ? campaign : null;
            if (owner == null) return null;
            Component source = SharedDevelopmentContext.Read(battle, "m_parentUI") as Component;
            IDictionary troops = source == null ? null : SharedDevelopmentContext.Read(source, "m_troops") as IDictionary;
            byte troop = Convert.ToByte(SharedDevelopmentContext.Read(battle, "idtroop"), CultureInfo.InvariantCulture);
            int campaignId = Convert.ToInt32(SharedDevelopmentContext.Read(battle, "idcampaign"), CultureInfo.InvariantCulture);
            object target = troops == null ? null : troops[troop];
            string operationId = owner.Observer.AttemptId;
            if (target == null || String.IsNullOrEmpty(operationId)) return null;
            return new BattlePresentationOwnership(ReferenceEquals(owner, challenge) ? BattleMode.NormalChallenge : BattleMode.Campaign,
                operationId, source, target, campaignId + ":" + troop,
                delegate { return owner.Observer.AttemptId == operationId && owner.Observer.CampaignId == campaignId &&
                    owner.Observer.TroopId == troop; });
        }
        private bool BattleSettlementAllowed()
        {
            PollStopOnly();
            if (life.LeaseExpired(Environment.TickCount)) life.RequestStop("heartbeat_timeout");
            return ready && !destroyed && !finishing && !life.StopRequested &&
                Entry.GetCurrentThreadId() == nativeId && Thread.CurrentThread.ManagedThreadId == managedId;
        }
        private void PublishGuiOwnership() { PublishGuiOwnership(true); }
        private void PublishScopedGuiOwnership() { PublishGuiOwnership(false); }
        private void PublishGuiOwnership(bool invocationAttempted)
        {
            PollStopOnly();
            if (life.LeaseExpired(Environment.TickCount)) life.RequestStop("heartbeat_timeout");
            if (life.StopRequested) throw new GuiAdmissionCancelled();
            guiInvocationAttempted = invocationAttempted; guiPhase = "owned"; Publish();
            PollStopOnly();
            if (life.LeaseExpired(Environment.TickCount)) life.RequestStop("heartbeat_timeout");
            if (life.StopRequested) throw new GuiAdmissionCancelled();
        }
        private bool BeginScopedGuiInvocation()
        {
            if (life.StopRequested || life.LeaseExpired(Environment.TickCount)) return false;
            Command published;
            try { published = ReadCommand(false, 0); }
            catch (IOException) { return false; }
            if (published == null || published.Sequence < life.LastSequence || published.Name == "stop" || life.LeaseExpired(Environment.TickCount)) return false;
            // No publication or target reads remain. An owned on-disk record
            // is provisional until the terminal GUI receipt is published.
            guiInvocationAttempted = true;
            return true;
        }
        private bool BeginDailyPopupGuiInvocation(Command consumed)
        {
            PollStopOnly();
            if (life.LeaseExpired(Environment.TickCount)) life.RequestStop("heartbeat_timeout");
            if (life.StopRequested || DailyPopupOtherBusinessBusy() || Elapsed(guiQueuedTick) >= GuiDeadlineMilliseconds) return false;
            Command published;
            try { published = ReadCommand(false, 0); }
            catch (IOException) { return false; }
            if (published == null || published.Name != consumed.Name || published.Sequence != consumed.Sequence || life.LastSequence != consumed.Sequence ||
                published.DailyPopupAction != consumed.DailyPopupAction || published.DailyPopupType != consumed.DailyPopupType ||
                published.DailyPopupInstance != consumed.DailyPopupInstance || published.DailyPopupGeneration != consumed.DailyPopupGeneration ||
                published.DailyPopupCharacterId != consumed.DailyPopupCharacterId || published.ExpectedRevision != consumed.ExpectedRevision ||
                published.OperationId != consumed.OperationId || life.StopRequested || DailyPopupOtherBusinessBusy()) return false;
            return true; // The model sets attempted only after its final window recheck.
        }
        private bool BeginDailyTimedGuiInvocation()
        {
            if (DailyTimedOtherBusinessBusy() || !BeginScopedGuiInvocation()) return false;
            dailyTimedMutationAttempted = true;
            return true;
        }
        private bool BeginDailyStoneGuiInvocation(Command consumed, DailyStoneInvocationCommit sample, DailyStoneInvocationCommit check)
        {
            if (life.StopRequested || life.LeaseExpired(Environment.TickCount) || Elapsed(guiQueuedTick) >= GuiDeadlineMilliseconds ||
                DailyStoneOtherBusinessBusy() || !sample()) return false;
            Command published;
            try { published = ReadCommand(false, 0); } // Parse also requires the exact session nonce.
            catch (IOException) { return false; }
            if (published == null || published.Sequence != consumed.Sequence || life.LastSequence != consumed.Sequence ||
                published.Name != consumed.Name || published.DailyStoneAction != consumed.DailyStoneAction ||
                published.OperationId != consumed.OperationId || published.DailyStoneCharacterId != consumed.DailyStoneCharacterId ||
                published.DailyStoneTaskId != consumed.DailyStoneTaskId || published.ExpectedRevision != consumed.ExpectedRevision)
                return false;
            // This successful exact read orders admission relative to Stop.
            // A later Stop applies to the admitted attempt.
            if (!check() || life.StopRequested || life.LeaseExpired(Environment.TickCount) ||
                Elapsed(guiQueuedTick) >= GuiDeadlineMilliseconds) return false;
            guiInvocationAttempted = true;
            return true;
        }
        private bool BeginDailyMingshiGuiInvocation(Command consumed, DailyMingshiInvocationCheck prepare, DailyMingshiInvocationCheck check)
        {
            if (life.StopRequested || life.LeaseExpired(Environment.TickCount) || Elapsed(guiQueuedTick) >= GuiDeadlineMilliseconds ||
                DailyMingshiOtherBusinessBusy() || !prepare()) return false;
            Command published;
            try { published = ReadCommand(false, 0); } // Parse requires the exact session nonce.
            catch (IOException) { return false; }
            if (published == null || published.Sequence != consumed.Sequence || life.LastSequence != consumed.Sequence ||
                published.Name != consumed.Name || published.DailyMingshiAction != consumed.DailyMingshiAction ||
                published.OperationId != consumed.OperationId || published.DailyMingshiCharacterId != consumed.DailyMingshiCharacterId ||
                published.DailyMingshiTaskId != consumed.DailyMingshiTaskId || published.ExpectedRevision != consumed.ExpectedRevision)
                return false;
            // A stop published after this exact read applies to the admitted attempt.
            if (!check() || life.StopRequested || life.LeaseExpired(Environment.TickCount) ||
                Elapsed(guiQueuedTick) >= GuiDeadlineMilliseconds || DailyMingshiOtherBusinessBusy()) return false;
            guiInvocationAttempted = true;
            return true;
        }
        private bool BeginDailyArenaGuiInvocation(Command consumed, DailyArenaInvocationDeadline withinDeadline)
        {
            if (life.StopRequested || life.LeaseExpired(Environment.TickCount) || !withinDeadline() ||
                Elapsed(guiQueuedTick) >= GuiDeadlineMilliseconds || DailyArenaOtherBusinessBusy()) return false;
            Command published;
            try { published = ReadCommand(false, 0); } // Parse also binds the session nonce.
            catch (IOException) { return false; }
            // The exact read orders a later Stop after admission, provided the
            // model's final GUI and live transport checks still pass.
            return published != null && published.Sequence == consumed.Sequence && life.LastSequence == consumed.Sequence &&
                published.Name == consumed.Name && published.DailyArenaAction == consumed.DailyArenaAction &&
                published.OperationId == consumed.OperationId && published.DailyArenaCharacterId == consumed.DailyArenaCharacterId &&
                published.ExpectedRevision == consumed.ExpectedRevision &&
                !life.StopRequested && !life.LeaseExpired(Environment.TickCount) && withinDeadline() &&
                Elapsed(guiQueuedTick) < GuiDeadlineMilliseconds && !DailyArenaOtherBusinessBusy();
        }
        private bool BeginDailySoulGuiInvocation(Command consumed, DailySoulInvocationCheck prepare, DailySoulInvocationCheck check)
        {
            if (!DailySoulGuiWindow()) return false;
            bool prepared = prepare();
            Command published;
            try { published = ReadCommand(false, 0); } // Parse binds the session nonce.
            catch (IOException) { return false; }
            if (published != null && published.Name == "stop" && published.Sequence > life.LastSequence && Accept(published))
            { reason = "stop"; lifecycle = "stopping"; }
            if (!DailySoulGuiWindow() || !prepared || published == null || published.Sequence != consumed.Sequence || life.LastSequence != consumed.Sequence ||
                published.Name != consumed.Name || published.DailySoulAction != consumed.DailySoulAction ||
                published.OperationId != consumed.OperationId || published.DailySoulCharacterId != consumed.DailySoulCharacterId ||
                published.DailySoulTaskId != consumed.DailySoulTaskId || published.DailySoulMode != consumed.DailySoulMode ||
                published.DailySoulFreeOnly != consumed.DailySoulFreeOnly || published.ExpectedRevision != consumed.ExpectedRevision)
                return false;
            // A stop published after the exact read applies to the admitted attempt.
            return check();
        }
        private bool DailySoulGuiWindow()
        {
            if (life.LeaseExpired(Environment.TickCount)) life.RequestStop("heartbeat_timeout");
            if (life.StopRequested) throw new GuiAdmissionCancelled();
            return Elapsed(guiQueuedTick) < GuiDeadlineMilliseconds && !DailySoulOtherBusinessBusy();
        }
        private bool BeginDailyRefineGuiInvocation(Command consumed, DailyRefineCommitCheck prepare, DailyRefineCommitCheck check)
        {
            if (!DailyRefineGuiWindow()) return false;
            bool prepared = prepare();
            Command published;
            try { published = ReadCommand(false, 0); }
            catch (IOException) { return false; }
            if (published != null && published.Name == "stop" && published.Sequence > life.LastSequence && Accept(published))
            { reason = "stop"; lifecycle = "stopping"; }
            if (!DailyRefineGuiWindow() || !prepared || published == null || published.Sequence != consumed.Sequence ||
                life.LastSequence != consumed.Sequence || published.Name != consumed.Name ||
                published.DailyRefineAction != consumed.DailyRefineAction || published.OperationId != consumed.OperationId ||
                published.DailyRefineCharacterId != consumed.DailyRefineCharacterId ||
                published.DailyRefineTaskId != consumed.DailyRefineTaskId || published.ExpectedRevision != consumed.ExpectedRevision)
                return false;
            // The successful exact read orders a later Stop after admission.
            // The lease and GUI window still have to survive the live check.
            return check() && DailyRefineGuiWindow();
        }
        private bool DailyRefineGuiWindow()
        {
            if (life.LeaseExpired(Environment.TickCount)) life.RequestStop("heartbeat_timeout");
            return !life.StopRequested && Elapsed(guiQueuedTick) < GuiDeadlineMilliseconds && !DailyRefineOtherBusinessBusy();
        }
#if JSB_DAILY_WOOD_ENABLED
        private bool BeginDailyWoodGuiInvocation(Command consumed, DailyWoodAdmissionCheck prepare, DailyWoodAdmissionCheck check)
        {
            if (!DailyWoodGuiWindow()) return false;
            bool prepared = prepare();
            Command published;
            try { published = ReadCommand(false, 0); } // Includes the exact session nonce.
            catch (IOException) { return false; }
            if (published != null && published.Name == "stop" && published.Sequence > life.LastSequence && Accept(published))
            { reason = "stop"; lifecycle = "stopping"; }
            if (!DailyWoodGuiWindow() || !prepared || published == null || published.Sequence != consumed.Sequence ||
                life.LastSequence != consumed.Sequence || published.Name != consumed.Name ||
                published.DailyWoodAction != consumed.DailyWoodAction || published.OperationId != consumed.OperationId ||
                published.DailyWoodCharacterId != consumed.DailyWoodCharacterId || published.DailyWoodTaskId != consumed.DailyWoodTaskId ||
                published.ExpectedRevision != consumed.ExpectedRevision) return false;
            // Later Stop files apply to the admitted action. Check the lease
            // again after the model's final GUI and live transport checks.
            return check() && DailyWoodGuiWindow();
        }
        private bool DailyWoodGuiWindow()
        {
            if (life.LeaseExpired(Environment.TickCount)) life.RequestStop("heartbeat_timeout");
            return !life.StopRequested && Elapsed(guiQueuedTick) < GuiDeadlineMilliseconds && !DailyWoodOtherBusinessBusy();
        }
#endif
        private void CancelGui(string why)
        {
            Command command = guiCommand; if (command == null) return;
            guiCommand = null; guiPhase = "not_invoked"; guiInvocationAttempted = false;
            GuiNotInvoked(command, why);
            if (IsGuiCommand(ackName) && ackStatus == "ACCEPTED") TryAck("REJECTED", why);
            TryPublish();
        }
        private void GuiNotInvoked(Command command, string why)
        {
            if (command.Name == "daily-guild-command") dailyGuild.NotInvoked();
            else if (command.Name == "daily-reward-command") dailyReward.NotInvoked();
            else if (command.Name == "daily-timed-command") dailyTimed.NotInvoked(why);
            else if (command.Name == "daily-stone-command") dailyStone.NotInvoked(why);
            else if (command.Name == "daily-mingshi-command") dailyMingshi.NotInvoked(why);
            else if (command.Name == "daily-arena-command") dailyArena.NotInvoked();
            else if (command.Name == "daily-soul-command" && dailySoul != null) dailySoul.NotInvoked();
            else if (command.Name == "daily-refine-command" && dailyRefine != null) dailyRefine.NotInvoked(why);
            else if (command.Name == "daily-script" && dailyScript != null) dailyScript.NotInvoked(why);
            else if (command.Name == "daily-popup-command" && dailyPopup != null) dailyPopup.NotInvoked(why);
#if JSB_DAILY_WOOD_ENABLED
            else if (command.Name == "daily-wood-command") { if (dailyWood != null) dailyWood.NotInvoked(why); }
#endif
            else if (IsRefresh(command.Name)) RefreshFor(command).NotInvoked(why);
            else if (command.Name == "development-command") development.NotInvoked(command, why);
            else CampaignFor(command).GuiNotInvoked(command, why);
        }
        private static bool IsGuiCommand(string name)
        { return name == "campaign-command" || name == "challenge-command" || name == "development-command" || IsRefresh(name) || name == "daily-guild-command" || name == "daily-reward-command" || name == "daily-timed-command" || name == "daily-stone-command" || name == "daily-mingshi-command" || name == "daily-arena-command" || name == "daily-soul-command" || name == "daily-refine-command" || name == "daily-wood-command" || name == "daily-script" || name == "daily-popup-command"; }
        private static bool IsRefresh(string name) { return name == "player-state-refresh" || name == "daily-state-refresh"; }
        private PlayerStateRefreshModel RefreshFor(Command command) { return command.Name == "daily-state-refresh" ? dailyRefresh : playerRefresh; }
        private bool RefreshBusinessBusy()
        { return DailyWoodBusy() || DailyRefineBusy() || DailySoulBusy() || OtherRefreshBusinessBusy(); }
        private bool OtherRefreshBusinessBusy()
        { return DailyRewardBusy() || DailyTimedBusy() || DailyStoneBusy() || DailyMingshiBusy() || DailyArenaBusy() || OtherBusinessBusy(); }
        private bool OtherBusinessBusy()
        { return life.ActionPending || development == null || development.Pending || BattlePending() || campaign != null && campaign.NavigationPending || dailyGuild != null && dailyGuild.Busy; }
        private bool DailyPopupOtherBusinessBusy()
        {
            bool scriptOwned = dailyScript != null && dailyScript.Busy &&
                !(dailyScript.PopupHandoffReady && dailyPopup != null && dailyPopup.HandoffOwned(dailyScript.OwnsRewardPopup));
            return life.StopRequested || life.RecoveryUnknown || RefreshPending() || RefreshBusinessBusy() || scriptOwned;
        }
        private bool DailyRewardBusy() { return dailyReward != null && dailyReward.Busy; }
        private bool DailyRewardOtherBusinessBusy() { return RefreshPending() || DailyTimedBusy() || DailyStoneBusy() || OtherBusinessBusy(); }
        private bool DailyTimedBusy() { return dailyTimed != null && dailyTimed.Busy; }
        private bool DailyTimedOtherBusinessBusy() { return life.RecoveryUnknown || RefreshPending() || DailyRewardBusy() || DailyStoneBusy() || OtherBusinessBusy(); }
        private bool DailyStoneBusy() { return dailyStone != null && dailyStone.Busy; }
        private bool DailyStoneOtherBusinessBusy() { return life.RecoveryUnknown || RefreshPending() || DailyRewardBusy() || DailyTimedBusy() || OtherBusinessBusy(); }
        private bool DailyMingshiBusy() { return dailyMingshi != null && dailyMingshi.Busy; }
        private bool DailyMingshiOtherBusinessBusy() { return life.RecoveryUnknown || RefreshPending() || DailyRewardBusy() || DailyTimedBusy() || DailyStoneBusy() || OtherBusinessBusy(); }
        private bool DailyArenaBusy() { return dailyArena != null && dailyArena.Busy; }
        private bool DailyArenaOtherBusinessBusy()
        { return life.RecoveryUnknown || RefreshPending() || DailyRewardBusy() || DailyTimedBusy() || DailyStoneBusy() || DailyMingshiBusy() || OtherBusinessBusy(); }
        private bool DailySoulBusy() { return dailySoul != null && dailySoul.Busy; }
        private bool DailySoulOtherBusinessBusy() { return life.RecoveryUnknown || RefreshPending() || OtherRefreshBusinessBusy(); }
        private bool DailyRefineBusy() { return dailyRefine != null && dailyRefine.Busy; }
        private bool DailyRefineOtherBusinessBusy() { return life.RecoveryUnknown || RefreshPending() || DailySoulBusy() || OtherRefreshBusinessBusy(); }
#if JSB_DAILY_WOOD_ENABLED
        private bool DailyWoodBusy() { return dailyWood != null && dailyWood.Busy; }
        private bool DailyWoodUnknown() { return dailyWood != null && dailyWood.Status == "UNKNOWN"; }
        private bool DailyWoodOtherBusinessBusy()
        { return life.RecoveryUnknown || RefreshPending() || DailyRefineBusy() || DailySoulBusy() || OtherRefreshBusinessBusy(); }
        private SharedDevelopmentContext ReadDailyWoodContext()
        { return DevelopmentContextReader.DailyWood(model, life.StopRequested || DailyWoodOtherBusinessBusy()); }
#else
        private bool DailyWoodBusy() { return false; }
        private bool DailyWoodUnknown() { return false; }
#endif
        private SharedDevelopmentContext ReadDailyRefineContext()
        { return DevelopmentContextReader.DailyRefine(model, life.StopRequested || DailyRefineOtherBusinessBusy()); }
        private SharedDevelopmentContext ReadDailySoulContext()
        { return DevelopmentContextReader.DailySoul(model, life.StopRequested || DailySoulOtherBusinessBusy()); }
        private SharedDevelopmentContext ReadDailySoulRecoveryContext()
        { return DevelopmentContextReader.DailySoul(model, DailySoulOtherBusinessBusy()); }
        private bool DailySoulMainReady() { return model.Capture().Gate == "PASS"; }
        private SharedDevelopmentContext ReadDailyArenaContext()
        { return DevelopmentContextReader.DailyArena(model, life.StopRequested || DailyArenaOtherBusinessBusy()); }
        private SharedDevelopmentContext ReadDailyMingshiContext()
        {
            Snapshot common = model.Capture();
            return DevelopmentContextReader.Read(model, common, DevelopmentContextReader.ExternalGate(common.Gate),
                life.StopRequested || DailyMingshiOtherBusinessBusy());
        }
        private SharedDevelopmentContext ReadDailyStoneContext()
        {
            return DevelopmentContextReader.DailyStone(model, life.StopRequested || DailyStoneOtherBusinessBusy());
        }
        private SharedDevelopmentContext ReadDailyRewardContext()
        {
            try
            {
                return DevelopmentContextReader.DailyReward(model, life.StopRequested || life.RecoveryUnknown || DailyRewardOtherBusinessBusy());
            }
            catch (Exception) { return null; } // Unreadable Unity identity fails admission without stopping other capabilities.
        }
        private Command ReadCommand(bool deferAndLog = true, int? previous = null)
        {
            string path = Path.Combine(sessionDir, "command.txt");
            try
            {
                // Metadata and content belong to the same deferrable read. A
                // replacement can race either step; accept no sequence until
                // a later frame has read and parsed one complete command.
                if (!File.Exists(path) || new FileInfo(path).Length > 768) return null;
                return Command.Parse(File.ReadAllText(path, System.Text.Encoding.ASCII), nonce, previous ?? life.LastSequence);
            }
            catch (IOException error)
            {
                if (!deferAndLog) throw;
                if (!commandReadFailureLogged)
                { commandReadFailureLogged = true; TryEmit("command_read_deferred", "\"path\":" + DiagnosticText(path, 512) + ",\"failure\":" + FailureJson(error, "command_read")); }
                return null;
            }
        }
        private bool Accept(Command command)
        {
            string rejected;
            if (!life.Accept(command, Environment.TickCount, out rejected))
            {
                if (command != null && command.Sequence == life.LastSequence)
                {
                    ackSequence = command.Sequence; ackName = command.Name;
                    if (ackName == "daily-wood-command") lastWoodCommand = command;
                    Ack("REJECTED", rejected);
                }
                return false;
            }
            ackSequence = command.Sequence; ackName = command.Name;
            if (ackName == "daily-wood-command") lastWoodCommand = command;
            Ack("ACCEPTED", "");
            Emit("command", "\"command_seq\":" + ackSequence + ",\"command_name\":" + Command.Quote(ackName));
            return true;
        }
        private void PollStopOnly()
        {
            if (life.StopRequested) return;
            Command command = ReadCommand();
            if (command != null && command.Name == "stop" && Accept(command))
            { reason = "stop"; lifecycle = "stopping"; Publish(); }
        }
        private void Poll()
        {
            Command command = ReadCommand(); if (command == null || !Accept(command)) return;
            if (command.Name == "stop") { reason = "stop"; Finish(); return; }
            if (command.Name == "heartbeat") { Ack("COMPLETED", ""); Publish(); return; }
            if (battleSettlement != null && battleSettlement.BlocksGuiInvocation &&
                (IsGuiCommand(command.Name) || command.Name == "disable-restore") &&
                !(command.Name == "daily-script" && command.DailyScriptAction == "observe"))
            {
                // Campaign acknowledgement/start actions may execute directly
                // from Poll. The same report fence applies before those calls,
                // as well as before queued OnGUI commands.
                Ack("REJECTED", "battle_settlement_pending_not_invoked"); Publish(); return;
            }
            if (command.Name == "campaign-observe" || command.Name == "challenge-observe") { Ack("COMPLETED", ""); Publish(); return; }
            if (command.Name == "daily-observe")
            {
                if (!dailyRefreshObserved)
                {
                    dailyRefreshObserved = true;
                    DailyRefreshProof.ObserveNatural();
                }
                Ack("COMPLETED", "", DailyActivityModel.Capture(model)); Publish(); return;
            }
            if (command.Name == "daily-popup-observe" || command.Name == "daily-popup-command")
            {
                dailyPopup.Capture(model, DailyPopupOtherBusinessBusy());
                if (command.Name == "daily-popup-observe") { Ack("COMPLETED", ""); Publish(); return; }
                string why;
                if (!dailyPopup.Queue(command, out why)) { Ack("REJECTED", why); Publish(); return; }
                guiCommand = command; guiQueuedTick = Environment.TickCount; guiSequence = command.Sequence;
                guiPhase = "queued"; guiInvocationAttempted = false; Publish(); return;
            }
            if (command.Name == "daily-script")
            {
                if (dailyScript == null) dailyScript = new DailyScriptAdapter();
                if (command.DailyScriptAction == "observe")
                {
                    if (battleSettlement == null || !battleSettlement.BlocksGuiInvocation)
                        dailyScript.Capture(model, command.DailyScriptTaskId);
                    Ack("COMPLETED", ""); Publish(); return;
                }
                if (battleSettlement != null && battleSettlement.BlocksGuiInvocation)
                { Ack("REJECTED", "battle_settlement_pending_not_invoked"); Publish(); return; }
                string why;
                if (!dailyScript.Queue(command, out why))
                { Ack("REJECTED", why); Publish(); return; }
                guiCommand = command; guiQueuedTick = Environment.TickCount; guiSequence = command.Sequence;
                guiPhase = "queued"; guiInvocationAttempted = false; Publish(); return;
            }
            if (command.Name == "daily-guild-observe")
            {
                guildObserved = true;
                if (dailyGuild != null) dailyGuild.Capture(development == null ? null : development.CurrentContext);
                Ack("COMPLETED", ""); Publish(); return;
            }
            if (command.Name == "daily-wood-observe" || command.Name == "daily-wood-command")
            {
#if JSB_DAILY_WOOD_ENABLED
                PollDailyWood(command);
#else
                Ack("REJECTED", "daily_wood_disabled"); Publish();
#endif
                return;
            }
            if (DailyWoodBusy() && (IsGuiCommand(command.Name) || command.Name == "disable-restore"))
            { Ack("REJECTED", "daily_wood_operation_owned"); Publish(); return; }
            if (command.Name == "daily-refine-observe")
            {
                if (!DailyRefineEnabled) { Ack("REJECTED", "daily_refine_disabled"); Publish(); return; }
                if (!dailyRefreshObserved)
                {
                    dailyRefreshObserved = true;
                    DailyRefreshProof.ObserveNatural();
                }
                if (dailyRefine == null) dailyRefine = new DailyRefineModel(DevelopmentCallbackCapture.InstallDailyRefine,
                    DevelopmentCallbackCapture.InstallDailyActivityPush);
                CaptureDailyRefine();
                Ack("COMPLETED", ""); Publish(); return;
            }
            if (command.Name == "daily-refine-command")
            {
                string why = "";
                if (!DailyRefineEnabled || dailyRefine == null || DailyRefineOtherBusinessBusy() ||
                    !dailyRefine.Queue(command.DailyRefineTaskId, command.DailyRefineCharacterId, command.OperationId,
                        command.Sequence, command.ExpectedRevision, command.DailyRefineAction, out why))
                {
                    Ack("REJECTED", !DailyRefineEnabled ? "daily_refine_disabled" :
                        why == "" ? "daily_refine_observation_or_business_pending" : why);
                    Publish(); return;
                }
                guiCommand = command; guiQueuedTick = Environment.TickCount; guiSequence = command.Sequence;
                guiPhase = "queued"; guiInvocationAttempted = false; Publish(); return;
            }
            if (DailyRefineBusy() && (IsGuiCommand(command.Name) || command.Name == "disable-restore"))
            { Ack("REJECTED", "daily_refine_operation_owned"); Publish(); return; }
            if (command.Name == "daily-soul-observe")
            {
                if (!DailySoulEnabled) { Ack("REJECTED", "daily_soul_disabled"); Publish(); return; }
                if (!dailyRefreshObserved)
                {
                    dailyRefreshObserved = true;
                    DailyRefreshProof.ObserveNatural();
                }
                if (dailySoul == null) dailySoul = new DailySoulModel(DevelopmentCallbackCapture.InstallDailySoul, DailySoulMainReady, DailySoulGuiWindow);
                CaptureDailySoul();
                Ack("COMPLETED", ""); Publish(); return;
            }
            if (command.Name == "daily-soul-command")
            {
                string why = "";
                if (!DailySoulEnabled || dailySoul == null || DailySoulOtherBusinessBusy() ||
                    !dailySoul.Queue(command.DailySoulAction, command.DailySoulCharacterId, command.OperationId,
                        command.Sequence, command.ExpectedRevision, command.DailySoulMode, command.DailySoulFreeOnly == 1, out why))
                {
                    Ack("REJECTED", !DailySoulEnabled ? "daily_soul_disabled" : why == "" ? "daily_soul_observation_or_business_pending" : why);
                    Publish(); return;
                }
                guiCommand = command; guiQueuedTick = Environment.TickCount; guiSequence = command.Sequence;
                guiPhase = "queued"; guiInvocationAttempted = false; Publish(); return;
            }
            if (DailySoulBusy() && (IsGuiCommand(command.Name) || command.Name == "disable-restore"))
            { Ack("REJECTED", "daily_soul_operation_owned"); Publish(); return; }
            if (command.Name == "daily-arena-observe")
            {
                if (!DailyArenaEnabled) { Ack("REJECTED", "daily_arena_disabled"); Publish(); return; }
                if (!dailyRefreshObserved)
                {
                    dailyRefreshObserved = true;
                    DailyRefreshProof.ObserveNatural();
                }
                if (dailyArena == null) dailyArena = new DailyArenaChestModel();
                CaptureDailyArena();
                Ack("COMPLETED", ""); Publish(); return;
            }
            if (command.Name == "daily-arena-command")
            {
                string why = "";
                if (command.DailyArenaAction == "rearm")
                {
                    DailyArenaChestModel replacement = null;
                    if (!DailyArenaEnabled || dailyArena == null || DailyArenaOtherBusinessBusy() ||
                        !dailyArena.Rearm(command.Sequence, command.ExpectedRevision, command.OperationId,
                            command.DailyArenaCharacterId, ReadDailyArenaContext(), out replacement, out why))
                    {
                        Ack("REJECTED", !DailyArenaEnabled ? "daily_arena_disabled" : why == "" ? "arena_rearm_business_pending" : why);
                        Publish(); return;
                    }
                    dailyArena = replacement;
                    Ack("COMPLETED", ""); Publish(); return;
                }
                if (!DailyArenaEnabled || dailyArena == null || DailyArenaOtherBusinessBusy() ||
                    !dailyArena.Queue(command.Sequence, command.ExpectedRevision, command.DailyArenaAction,
                        command.OperationId, command.DailyArenaCharacterId, out why))
                {
                    Ack("REJECTED", !DailyArenaEnabled ? "daily_arena_disabled" : why == "" ? "daily_arena_observation_or_business_pending" : why);
                    Publish(); return;
                }
                guiCommand = command; guiQueuedTick = Environment.TickCount; guiSequence = command.Sequence;
                guiPhase = "queued"; guiInvocationAttempted = false; Publish(); return;
            }
            if (DailyArenaBusy() && (IsGuiCommand(command.Name) || command.Name == "disable-restore"))
            { Ack("REJECTED", "daily_arena_operation_owned"); Publish(); return; }
            if (command.Name == "daily-mingshi-observe")
            {
                if (!DailyMingshiEnabled) { Ack("REJECTED", "daily_mingshi_disabled"); Publish(); return; }
                if (!dailyRefreshObserved)
                {
                    dailyRefreshObserved = true;
                    DailyRefreshProof.ObserveNatural();
                }
                if (dailyMingshi == null) dailyMingshi = new DailyMingshiModel(DevelopmentCallbackCapture.InstallDailyMingshi,
                    DevelopmentCallbackCapture.InstallDailyActivityPush);
                CaptureDailyMingshi();
                Ack("COMPLETED", ""); Publish(); return;
            }
            if (command.Name == "daily-mingshi-command")
            {
                string why = "";
                if (!DailyMingshiEnabled || dailyMingshi == null || DailyMingshiOtherBusinessBusy() ||
                    !dailyMingshi.Queue(command.DailyMingshiTaskId, command.DailyMingshiCharacterId, command.OperationId,
                        command.Sequence, command.ExpectedRevision, command.DailyMingshiAction, out why))
                {
                    Ack("REJECTED", !DailyMingshiEnabled ? "daily_mingshi_disabled" : why == "" ? "daily_mingshi_observation_or_business_pending" : why);
                    Publish(); return;
                }
                guiCommand = command; guiQueuedTick = Environment.TickCount; guiSequence = command.Sequence;
                guiPhase = "queued"; guiInvocationAttempted = false; Publish(); return;
            }
            if (DailyMingshiBusy() && (IsGuiCommand(command.Name) || command.Name == "disable-restore"))
            { Ack("REJECTED", "daily_mingshi_operation_owned"); Publish(); return; }
            if (command.Name == "daily-stone-observe")
            {
                if (!DailyStoneEnabled) { Ack("REJECTED", "daily_stone_disabled"); Publish(); return; }
                if (!dailyRefreshObserved)
                {
                    dailyRefreshObserved = true;
                    DailyRefreshProof.ObserveNatural();
                }
                if (dailyStone == null) dailyStone = new DailyStoneModel(DevelopmentCallbackCapture.InstallDailyStone,
                    DevelopmentCallbackCapture.InstallDailyActivityPush);
                CaptureDailyStone();
                Ack("COMPLETED", ""); Publish(); return;
            }
            if (command.Name == "daily-stone-command")
            {
                string why = "";
                if (!DailyStoneEnabled || dailyStone == null || DailyStoneOtherBusinessBusy() ||
                    !dailyStone.Queue(command.DailyStoneTaskId, command.DailyStoneCharacterId, command.OperationId,
                        command.Sequence, command.ExpectedRevision, command.DailyStoneAction, out why))
                {
                    Ack("REJECTED", !DailyStoneEnabled ? "daily_stone_disabled" : why == "" ? "daily_stone_observation_or_business_pending" : why);
                    Publish(); return;
                }
                guiCommand = command; guiQueuedTick = Environment.TickCount; guiSequence = command.Sequence;
                guiPhase = "queued"; guiInvocationAttempted = false; Publish(); return;
            }
            if (DailyStoneBusy() && (IsGuiCommand(command.Name) || command.Name == "disable-restore"))
            { Ack("REJECTED", "daily_stone_operation_unresolved"); Publish(); return; }
            if (command.Name == "daily-timed-observe")
            {
                if (!DailyTimedEnabled) { Ack("REJECTED", "daily_timed_disabled"); Publish(); return; }
                if (!dailyRefreshObserved)
                {
                    dailyRefreshObserved = true;
                    DailyRefreshProof.ObserveNatural();
                }
                if (dailyTimed == null) dailyTimed = new DailyTimedModel(model, DevelopmentCallbackCapture.InstallDailyTimed);
                dailyTimed.Capture(DailyTimedOtherBusinessBusy());
                Ack("COMPLETED", ""); Publish(); return;
            }
            if (command.Name == "daily-timed-command")
            {
                string why = "";
                if (!DailyTimedEnabled || dailyTimed == null || DailyTimedOtherBusinessBusy() ||
                    !dailyTimed.Queue(command.DailyTimedTaskId, command.DailyTimedCharacterId, command.OperationId,
                        command.Sequence, command.ExpectedRevision, command.DailyTimedAction, out why))
                {
                    Ack("REJECTED", !DailyTimedEnabled ? "daily_timed_disabled" : why == "" ? "daily_timed_observation_or_business_pending" : why);
                    Publish(); return;
                }
                guiCommand = command; guiQueuedTick = Environment.TickCount; guiSequence = command.Sequence;
                guiPhase = "queued"; guiInvocationAttempted = false; Publish(); return;
            }
            if (DailyTimedBusy() && (IsGuiCommand(command.Name) || command.Name == "disable-restore"))
            { Ack("REJECTED", "daily_timed_operation_unresolved"); Publish(); return; }
            if (command.Name == "daily-reward-observe")
            {
                dailyRewardObserved = true;
                if (DailyRewardEnabled && !dailyRefreshObserved)
                {
                    dailyRefreshObserved = true;
                    DailyRefreshProof.ObserveNatural();
                }
                dailyReward.Capture(ReadDailyRewardContext());
                Ack("COMPLETED", ""); Publish(); return;
            }
            if (command.Name == "daily-reward-command")
            {
                string why = "";
                if (!DailyRewardEnabled || !dailyRewardObserved || DailyRewardOtherBusinessBusy() ||
                    !dailyReward.Queue(command.Sequence, command.ExpectedRevision, command.DailyRewardAction,
                        command.OperationId, command.DailyRewardCharacterId, command.DailyRewardCategoryId, out why))
                {
                    Ack("REJECTED", !DailyRewardEnabled ? "daily_reward_disabled" : why == "" ? "daily_reward_observation_or_business_pending" : why);
                    Publish(); return;
                }
                guiCommand = command; guiQueuedTick = Environment.TickCount; guiSequence = command.Sequence;
                guiPhase = "queued"; guiInvocationAttempted = false; Publish(); return;
            }
            if (DailyRewardBusy() && (IsGuiCommand(command.Name) || command.Name == "disable-restore"))
            { Ack("REJECTED", "daily_reward_operation_unresolved"); Publish(); return; }
            if (command.Name == "daily-guild-command")
            {
                string why = "";
                if (dailyGuild == null || !guildObserved || RefreshPending() || BattlePending() || campaign != null && campaign.NavigationPending || development != null && development.Pending || !dailyGuild.Queue(command, out why))
                { Ack("REJECTED", why == "" ? "guild_business_operation_unresolved" : why); Publish(); return; }
                guiCommand = command; guiQueuedTick = Environment.TickCount; guiSequence = command.Sequence;
                guiPhase = "queued"; guiInvocationAttempted = false; Publish(); return;
            }
            if (dailyGuild != null && dailyGuild.Busy &&
                (IsRefresh(command.Name) || command.Name == "development-command" || command.Name == "campaign-command" || command.Name == "challenge-command" || command.Name == "disable-restore"))
            { Ack("REJECTED", "guild_operation_unresolved"); Publish(); return; }
            if (command.Name == "development-observe")
            {
                if (development == null) { Ack("REJECTED", "development_unavailable"); Publish(); return; }
                development.ObserveTarget(command); CaptureDevelopment(); Ack("COMPLETED", ""); Publish(); return;
            }
            if (IsRefresh(command.Name))
            {
                string why = "";
                if (RefreshFor(command) == null || RefreshPending() || RefreshBusinessBusy() || command.ExpectedRevision != development.Revision ||
                    !RefreshFor(command).Queue(DevelopmentModel.ConvertCommand(command), out why))
                {
                    PlayerStateRefreshModel refresh = RefreshFor(command);
                    if (refresh != null && refresh.BindingError != null)
                        RememberFailure(refresh.BindingError, "refresh_queue:" + refresh.BindingStage);
                    Ack("REJECTED", why == "" ? "refresh_context_revision_or_business_pending" : why); Publish(); return;
                }
                if (command.Name == "player-state-refresh") refreshCommand = command;
                else dailyRefreshObserved = true;
                guiCommand = command; guiQueuedTick = Environment.TickCount; guiSequence = command.Sequence;
                guiPhase = "queued"; guiInvocationAttempted = false; Publish(); return;
            }
            if (command.Name == "development-command")
            {
                if (RefreshPending()) { Ack("REJECTED", "player_refresh_pending"); Publish(); return; }
                string why = "";
                if (development == null || !development.Queue(command, out why))
                { Ack("REJECTED", development == null ? "development_unavailable" : why); Publish(); return; }
                guiCommand = command; guiQueuedTick = Environment.TickCount; guiSequence = command.Sequence;
                guiPhase = "queued"; guiInvocationAttempted = false; Publish(); return;
            }
            if (command.Name == "campaign-command" || command.Name == "challenge-command")
            {
                if (RefreshPending() || development != null && development.Pending) { Ack("REJECTED", "development_operation_unresolved"); Publish(); return; }
                CampaignModel selectedCampaign = CampaignFor(command);
                CampaignModel otherCampaign = command.Name == "challenge-command" ? campaign : challenge;
                if (otherCampaign != null && (otherCampaign.Observer.Pending || otherCampaign.NavigationPending)) { Ack("REJECTED", "other_battle_attempt_unresolved"); Publish(); return; }
                string why;
                if (command.CampaignAction == "continue" || command.CampaignAction == "general_deploy" || command.CampaignAction == "formation_confirm")
                {
                    if (!selectedCampaign.QueueGui(command, out why)) { Ack("REJECTED", why); Publish(); return; }
                    guiCommand = command; guiQueuedTick = Environment.TickCount; guiSequence = command.Sequence;
                    guiPhase = "queued"; guiInvocationAttempted = false; Publish(); return;
                }
                bool invoked;
                BattleLaunch.Select(command);
                try { invoked = selectedCampaign.Execute(command, Publish, out why); }
                finally { BattleLaunch.Select(null); }
                CaptureCampaigns(); Ack(invoked ? "COMPLETED" : "REJECTED", why); Publish(); return;
            }
            if (command.Name == "disable-restore" && (RefreshPending() || BattlePending() || development != null && development.Pending))
            { Ack("REJECTED", "business_operation_unresolved"); Publish(); return; }
            latest = model.Capture(); Emit("snapshot", latest.Json());
            if (command.Name != "disable-restore") { Ack("COMPLETED", ""); Publish(); return; }
            if (!latest.Safe || !latest.InteractionEnabled)
            { Ack("REJECTED", latest.Gate); Publish(); return; }
            Snapshot check = model.Capture();
            if (!check.Safe || !check.InteractionEnabled || !latest.SameTarget(check) || latest.Resources != check.Resources)
            { Ack("REJECTED", "preinvoke_snapshot_changed"); Publish(); return; }
            if (!life.BeginAction()) { Ack("REJECTED", "busy_or_stopping"); Publish(); return; }
            original = latest; actionSequence = command.Sequence; actionOutcome = "PENDING"; resourceStatus = "UNVERIFIED";
            actionObserved = restoreObserved = restoreMethodInvoked = actionFailure = mutationAttempted = recoveryActive = false;
            resources = new ResourceObservation(Model.ResourceNames, original.ResourceValues); lastResourceObservation = original;
            phase = 1; phaseTick = Environment.TickCount; phaseCallbacks = callbacks; lifecycle = "action";
            // Publish ownership before the only possible target mutation.
            Publish(); mutationAttempted = true; model.SetInteraction(original, false);
            latest = model.Capture(original.Main); Observe(latest); Emit("action_invoked", latest.Json()); Publish();
            if (!latest.Safe || !original.SameTarget(latest) || latest.InteractionEnabled) throw new ProbeFailure("disable_not_observed");
        }
        private static uint Elapsed(int since) { return unchecked((uint)Environment.TickCount - (uint)since); }
        private void Advance()
        {
            if (recoveryActive && Elapsed(recoveryTick) >= SessionLifecycle.RecoveryMilliseconds) { RecoveryUnknown("restore_deadline"); return; }
            if (phase == 4)
            {
                if (unchecked((int)((uint)Environment.TickCount - (uint)nextRecoveryTick)) < 0) return;
                nextRecoveryTick = unchecked(Environment.TickCount + 250); TryStartRestore("bounded_recovery"); return;
            }
            latest = model.Capture(original.Main);
            if (!original.SameTarget(latest) || !latest.Safe) { EnterRecovery(latest.Gate); return; }
            Observe(latest);
            if (Elapsed(phaseTick) > (phase == 2 ? 12000U : 8000U) || callbacks - phaseCallbacks > (phase == 2 ? 1800 : 360))
            {
                actionFailure = true;
                if (phase <= 2) TryStartRestore("interaction_timeout"); else EnterRecovery("restore_timeout");
                return;
            }
            if (phase <= 2 && latest.InteractionEnabled)
            { actionFailure = true; TryStartRestore("interaction_state_drift"); return; }
            if (phase == 1 && !latest.InteractionEnabled && callbacks - phaseCallbacks >= 2)
            {
                actionObserved = true; phase = 2; phaseTick = Environment.TickCount; phaseCallbacks = callbacks;
                Emit("action_verified", latest.Json()); Publish();
            }
            else if (phase == 2 && Elapsed(phaseTick) >= 6000U) TryStartRestore("normal");
            else if (phase == 3 && latest.InteractionEnabled && callbacks - phaseCallbacks >= 2)
            {
                restoreObserved = true; bool equal = resources.FinalEqual(latest.ResourceValues) && resources.EverySampleEqual();
                resourceStatus = equal ? "PASS" : "FAIL";
                actionOutcome = actionObserved && restoreMethodInvoked && !actionFailure ? "PASS" : "FAIL";
                Emit("transaction_result", "\"action\":" + ActionJson());
                life.CompleteAction(true); phase = 0; recoveryActive = false; lifecycle = life.StopRequested ? "stopping" : "ready";
                if (ackSequence == actionSequence) Ack("COMPLETED", actionOutcome == "PASS" ? "" : "action_failed_but_restored");
                Publish(); if (life.StopRequested) Finish();
            }
        }
        private void TryStartRestore(string why)
        {
            try
            {
                latest = model.Capture(original.Main);
                if (!original.SameTarget(latest) || !latest.Safe) { EnterRecovery(latest.Gate); return; }
                Observe(latest);
                phase = 3; phaseTick = Environment.TickCount; phaseCallbacks = callbacks; lifecycle = "restoring";
                if (!latest.InteractionEnabled) { model.SetInteraction(original, true); restoreMethodInvoked = true; latest = model.Capture(original.Main); Observe(latest); }
                Emit("restore_invoked", "\"reason\":" + Command.Quote(why) + "," + latest.Json()); Publish();
            }
            catch (Exception error) { RememberFailure(error, "restore"); EnterRecovery(Failure(error)); }
        }
        private void EnterRecovery(string why)
        {
            reason = why; actionFailure = true; life.RequestStop("restore_recovery");
            if (!recoveryActive) { recoveryActive = true; recoveryTick = Environment.TickCount; nextRecoveryTick = Environment.TickCount; }
            phase = 4; lifecycle = "recovery";
            TryEmit("restoration_pending", "\"reason\":" + Command.Quote(why)); TryPublish();
        }
        private void RecoveryUnknown(string why)
        {
            reason = why; actionOutcome = "UNKNOWN"; restoreObserved = false; life.CompleteAction(false); phase = 0; lifecycle = "recovery_unknown";
            string record = "{" + Identity() + ",\"reason\":" + Command.Quote(why) + ",\"action\":" + ActionJson() + ",\"process_restart_required\":true}";
            try { store.Write("recovery_unknown.json", record); } catch { }
            if (ackSequence != 0) TryAck("UNKNOWN", why); TryEmit("restoration_unverified", "\"reason\":" + Command.Quote(why)); Finish();
        }
        private void Observe(Snapshot snap)
        {
            if (resources == null || original == null || snap == null || !original.SameTarget(snap) || object.ReferenceEquals(lastResourceObservation, snap)) return;
            resources.Observe(snap.ResourceValues); lastResourceObservation = snap;
        }
        private static string Failure(Exception error)
        {
            ReceiptIoFailure receipt = error as ReceiptIoFailure;
            if (receipt != null) return receipt.InnerException == null ? "IOException" : Failure(receipt.InnerException);
            return error is ProbeFailure ? error.Message : error.GetType().Name;
        }
        private static string DiagnosticText(string value, int limit)
        { return value == null ? "null" : Command.Quote(value.Length <= limit ? value : value.Substring(0, limit)); }
        private static string FailureJson(Exception error, string location)
        {
            ReceiptIoFailure receipt = error as ReceiptIoFailure;
            Exception inner = error.InnerException;
            // Bounds include JSON escaping (up to six bytes per character), so
            // this diagnostic plus the event identity fits the 32768 limit.
            return "{\"location\":" + DiagnosticText(location, 64) +
                ",\"exception_type\":" + DiagnosticText(error.GetType().FullName, 128) +
                ",\"message\":" + DiagnosticText(error.Message, 512) +
                ",\"stack_trace\":" + DiagnosticText(error.StackTrace, 1024) +
                ",\"inner_exception_type\":" + DiagnosticText(inner == null ? null : inner.GetType().FullName, 128) +
                ",\"inner_message\":" + DiagnosticText(inner == null ? null : inner.Message, 512) +
                ",\"inner_stack_trace\":" + DiagnosticText(inner == null ? null : inner.StackTrace, 1024) +
                ",\"io_stage\":" + DiagnosticText(receipt == null ? null : receipt.Stage, 64) +
                ",\"path\":" + DiagnosticText(receipt == null ? null : receipt.ReceiptPath, 512) +
                ",\"win32_error\":" + (receipt == null || receipt.Win32Error < 0 ? "null" : receipt.Win32Error.ToString(CultureInfo.InvariantCulture)) + "}";
        }
        private void RememberFailure(Exception error, string location)
        { if (firstFailureJson == "null") firstFailureJson = FailureJson(error, location); }
        private static string Bool(bool value) { return value ? "true" : "false"; }
        private string Identity()
        {
            return "\"protocol\":3,\"bridge_id\":" + Command.Quote(Entry.BridgeId) + ",\"nonce\":" + Command.Quote(nonce) + ",\"session_id\":" + Command.Quote(nonce) +
                ",\"pid\":" + Entry.GetCurrentProcessId() + ",\"process_creation_filetime\":" + creationFiletime.ToString(CultureInfo.InvariantCulture) +
                ",\"verified_native_tid\":" + nativeId + ",\"managed_tid\":" + managedId + ",\"assembly\":" + Command.Quote(typeof(Entry).Assembly.GetName().Name) +
                ",\"updated_utc\":" + Command.Quote(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        }
        private string SnapshotJson() { return latest == null ? "null" : "{" + latest.Json() + "}"; }
        private string ActionJson()
        {
            string phases = phase == 1 ? "disabled" : phase == 2 ? "holding" : phase == 3 ? "restoring" : phase == 4 ? "recovery" : "none";
            return "{\"command_seq\":" + actionSequence + ",\"phase\":" + Command.Quote(phases) + ",\"outcome\":" + Command.Quote(actionOutcome) +
                ",\"restore_verified\":" + Bool(restoreObserved) + ",\"restore_method_invoked\":" + Bool(restoreMethodInvoked) +
                ",\"mutation_attempted\":" + Bool(mutationAttempted) +
                ",\"disabled_state_verified\":" + Bool(actionObserved) + ",\"resource_equality_status\":" + Command.Quote(resourceStatus) +
                ",\"visual_gray_state\":\"UNVERIFIED\",\"resource_consumption_causality\":\"UNVERIFIED\",\"global_network_effects\":\"UNVERIFIED\"" +
                (resources != null && latest != null && original.SameTarget(latest) ? ",\"resource_comparison\":" + resources.Json(latest.ResourceValues) : "") + "}";
        }
        private string StateJson()
        {
            return "{" + Identity() + ",\"lifecycle\":" + Command.Quote(lifecycle) + ",\"ready\":" + Bool(ready && !finishing && !destroyed && !life.StopRequested) +
                ",\"main_thread_verified\":" + Bool(ready && verifiedFrames >= 2 && !threadFailureLogged) + ",\"component_destroyed\":" + Bool(destroyed) +
                ",\"reconnect_allowed\":" + Bool(destroyed && !life.RecoveryUnknown) + ",\"last_command_seq\":" + life.LastSequence +
                ",\"frame\":" + lastFrame + ",\"callbacks\":" + callbacks + ",\"distinct_frames\":" + verifiedFrames +
                ",\"gate\":" + Command.Quote(latest == null ? "not_observed" : latest.Gate) + ",\"main_instance_id\":" + (latest == null ? 0 : latest.InstanceId) +
                ",\"reason\":" + Command.Quote(reason) + ",\"failure\":" + firstFailureJson + ",\"snapshot\":" + SnapshotJson() + ",\"action\":" + ActionJson() +
                ",\"campaign\":" + CampaignJson() + ",\"challenge\":" + ChallengeJson() + ",\"business\":" + BusinessJson() +
                ",\"development\":" + DevelopmentJson() +
                ",\"player_refresh\":" + PlayerRefreshJson() +
                ",\"daily_refresh\":" + DailyRefreshJson() +
                ",\"daily_guild\":" + GuildJson() +
                ",\"daily_reward_enabled\":" + Bool(DailyRewardEnabled) + ",\"daily_reward\":" + DailyRewardJson() +
                ",\"daily_timed_enabled\":" + Bool(DailyTimedEnabled) + ",\"daily_timed\":" + DailyTimedJson() +
                ",\"daily_stone_enabled\":" + Bool(DailyStoneEnabled) + ",\"daily_stone\":" + DailyStoneJson() +
                ",\"daily_mingshi_enabled\":" + Bool(DailyMingshiEnabled) + ",\"daily_mingshi\":" + DailyMingshiJson() +
                ",\"daily_arena_enabled\":" + Bool(DailyArenaEnabled) + ",\"daily_arena\":" + DailyArenaJson() +
                ",\"daily_soul_enabled\":" + Bool(DailySoulEnabled) + ",\"daily_soul\":" + DailySoulJson() +
                ",\"daily_refine_enabled\":" + Bool(DailyRefineEnabled) + ",\"daily_refine\":" + DailyRefineJson() +
                ",\"daily_wood_enabled\":" + Bool(DailyWoodEnabled) + ",\"daily_wood\":" + DailyWoodJson() +
                ",\"daily_script\":" + (dailyScript == null ? "null" : dailyScript.Json()) +
                ",\"battle_launch\":" + BattleLaunch.Json() +
                ",\"daily_popup\":" + (dailyPopup == null ? "null" : dailyPopup.Json()) +
                ",\"campaign_gui\":" + GuiJson(guiSequence, guiPhase, guiInvocationAttempted) +
                ",\"assembly_unload\":\"normal_process_exit_required\"}";
        }
        private static string GuiJson(int sequence, string phase, bool attempted)
        { return "{\"command_seq\":" + sequence + ",\"phase\":" + Command.Quote(phase) + ",\"invocation_attempted\":" + Bool(attempted) + "}"; }
        private string BusinessSignature()
        { return lifecycle + ":" + ready + ":" + (campaign == null ? -1 : campaign.Revision) + ":" + (challenge == null ? -1 : challenge.Revision) + ":" +
            (development == null ? -1 : development.Revision) + ":" + PlayerRefreshJson() + ":" + DailyRefreshJson() + ":" + (dailyGuild == null ? -1 : dailyGuild.Revision) + ":" +
            (dailyReward == null ? -1 : dailyReward.Revision) + ":" + (dailyTimed == null ? -1 : dailyTimed.Revision) + ":" +
            (dailyStone == null ? -1 : dailyStone.Revision) + ":" + (dailyMingshi == null ? -1 : dailyMingshi.Revision) + ":" +
            (dailyArena == null ? -1 : dailyArena.Revision) + ":" + (dailySoul == null ? -1 : dailySoul.Revision) + ":" +
            (dailyRefine == null ? -1 : dailyRefine.Revision) + ":" + DailyWoodJson() + ":" + (dailyPopup == null ? -1 : dailyPopup.Revision) + ":" + guiSequence + ":" + guiPhase + ":" + BattleLaunch.Json(); }
        private void PublishBusinessChange()
        { if (publishedBusinessSignature != BusinessSignature()) Publish(); }
        private void Publish()
        { store.Write("state.json", StateJson()); publishedBusinessSignature = BusinessSignature(); }
        private void TryPublish() { try { Publish(); } catch { } }
        private void Ack(string status, string why, string daily = null)
        {
            ackStatus = status;
            store.Write("ack.json", "{" + Identity() + ",\"command_seq\":" + ackSequence + ",\"command_name\":" + Command.Quote(ackName) +
                ",\"status\":" + Command.Quote(status) + ",\"reason\":" + Command.Quote(why) + ",\"failure\":" + firstFailureJson + ",\"result\":{\"state\":" + Command.Quote(lifecycle) +
                ",\"restore_verified\":" + Bool(restoreObserved) + ",\"component_destroyed\":" + Bool(destroyed) + ",\"snapshot\":" + SnapshotJson() + ",\"action\":" + ActionJson() +
                ",\"campaign\":" + CampaignJson() + ",\"challenge\":" + ChallengeJson() + ",\"challenge_command\":" + (challenge == null ? "null" : challenge.CommandJson()) + ",\"campaign_command\":" + (campaign == null ? "null" : campaign.CommandJson()) + ",\"business\":" + BusinessJson() + ",\"development\":" + DevelopmentJson() + ",\"player_refresh\":" + PlayerRefreshJson() +
                ",\"daily_refresh\":" + DailyRefreshJson() + ",\"daily_guild\":" + GuildJson() + ",\"daily_reward_enabled\":" + Bool(DailyRewardEnabled) +
                ",\"daily_reward\":" + DailyRewardJson() + ",\"daily_timed_enabled\":" + Bool(DailyTimedEnabled) +
                ",\"daily_timed\":" + DailyTimedJson() + ",\"daily_stone_enabled\":" + Bool(DailyStoneEnabled) +
                ",\"daily_stone\":" + DailyStoneJson() + ",\"daily_mingshi_enabled\":" + Bool(DailyMingshiEnabled) +
                ",\"daily_mingshi\":" + DailyMingshiJson() +
                ",\"daily_arena_enabled\":" + Bool(DailyArenaEnabled) + ",\"daily_arena\":" + DailyArenaJson() +
                ",\"daily_soul_enabled\":" + Bool(DailySoulEnabled) + ",\"daily_soul\":" + DailySoulJson() +
                ",\"daily_refine_enabled\":" + Bool(DailyRefineEnabled) + ",\"daily_refine\":" + DailyRefineJson() +
                ",\"daily_wood_enabled\":" + Bool(DailyWoodEnabled) + ",\"daily_wood\":" + DailyWoodJson() +
                ",\"daily_script\":" + (dailyScript == null ? "null" : dailyScript.Json()) +
                ",\"daily_popup\":" + (dailyPopup == null ? "null" : dailyPopup.Json()) +
                (ackName == "daily-wood-command" ? ",\"daily_wood_command\":" + DailyWoodCommandJson() : "") +
                (ackName == "daily-arena-command" || ackName == "daily-soul-command" || ackName == "daily-refine-command" || ackName == "daily-wood-command" ? ",\"campaign_gui\":" + (ackSequence == guiSequence ?
                    GuiJson(guiSequence, guiPhase, guiInvocationAttempted) : GuiJson(ackSequence, "none", false)) : "") +
                (daily == null ? "" : ",\"daily\":" + daily) + "}}");
        }
        private CampaignModel CampaignFor(Command command) { return command.Name == "challenge-command" ? challenge : campaign; }
        private bool BattlePending() { return campaign != null && campaign.Observer.Pending || challenge != null && (challenge.Observer.Pending || challenge.NavigationPending); }
        private void CaptureCampaigns() { if (!RefreshPending()) { if (campaign != null) campaign.Capture(); if (challenge != null) challenge.Capture(); } }
        private string ChallengeJson() { return challenge == null ? "null" : challenge.Json(); }
        private string CampaignJson() { return campaign == null ? "null" : campaign.Json(); }
        private bool RefreshPending() { return playerRefresh != null && playerRefresh.Pending || dailyRefresh != null && dailyRefresh.Pending; }
        private string PlayerRefreshJson() { return playerRefresh == null ? "null" : playerRefresh.Json(); }
        private string DailyRefreshJson() { return dailyRefresh == null ? "null" : dailyRefresh.Json(); }
        private void CaptureDevelopment()
        {
            if (!life.StopRequested) ObserveBattleSettlement();
            if (dailyScript != null && !life.StopRequested && (battleSettlement == null || !battleSettlement.BlocksGuiInvocation))
                dailyScript.Capture(model);
            if (dailyPopup != null && !life.StopRequested) dailyPopup.Capture(model, DailyPopupOtherBusinessBusy());
            if (dailyRefreshObserved && !life.StopRequested) DailyRefreshProof.ObserveNatural();
            if (development != null) development.Capture(BattlePending(), life != null && life.ActionPending);
            if (playerRefresh != null) playerRefresh.Capture(development == null ? null : development.CurrentContext);
            if (dailyRefresh != null) dailyRefresh.Capture(development == null ? null : development.CurrentContext);
            if (dailyGuild != null && guildObserved) dailyGuild.Capture(development == null ? null : development.CurrentContext);
            if (dailyReward != null && dailyRewardObserved && !life.StopRequested) dailyReward.Capture(ReadDailyRewardContext());
            if (dailyTimed != null && !life.StopRequested) dailyTimed.Capture(DailyTimedOtherBusinessBusy());
            CaptureDailyStone();
            CaptureDailyMingshi();
            CaptureDailyArena();
            CaptureDailySoul();
            CaptureDailyRefine();
#if JSB_DAILY_WOOD_ENABLED
            CaptureDailyWood();
#endif
        }
#if JSB_DAILY_WOOD_ENABLED
        private void PollDailyWood(Command command)
        {
            if (command.Name == "daily-wood-observe")
            {
                if (!dailyRefreshObserved)
                {
                    dailyRefreshObserved = true;
                    DailyRefreshProof.ObserveNatural();
                }
                if (dailyWood == null) dailyWood = new DailyWoodModel();
                CaptureDailyWood();
                Ack("COMPLETED", ""); Publish(); return;
            }
            string why = "";
            if (dailyWood == null || DailyWoodOtherBusinessBusy() ||
                !dailyWood.Queue(command.DailyWoodTaskId, command.DailyWoodCharacterId, command.OperationId,
                    command.Sequence, command.ExpectedRevision, command.DailyWoodAction, out why))
            {
                Ack("REJECTED", why == "" ? "daily_wood_observation_or_business_pending" : why);
                Publish(); return;
            }
            guiCommand = command; guiQueuedTick = Environment.TickCount; guiSequence = command.Sequence;
            guiPhase = "queued"; guiInvocationAttempted = false; Publish();
        }
        private void CaptureDailyWood(bool stopping = false)
        {
            if (dailyWood == null || life.StopRequested && !stopping) return;
            SharedDevelopmentContext context;
            try { context = stopping ? DevelopmentContextReader.DailyWood(model, false) : ReadDailyWoodContext(); }
            catch (Exception) { context = null; }
            dailyWood.Capture(context);
        }
        private string DailyWoodJson() { return dailyWood == null ? "null" : dailyWood.Json(); }
#else
        private string DailyWoodJson() { return "null"; }
#endif
        private string DailyWoodCommandJson()
        {
            Command command = lastWoodCommand;
            if (command == null || command.Sequence != ackSequence) return "null";
            return "{\"command_seq\":" + command.Sequence + ",\"action\":" + Command.Quote(command.DailyWoodAction) +
                ",\"operation_id\":" + Command.Quote(command.OperationId) + ",\"character_id\":" +
                Command.Quote(command.DailyWoodCharacterId.ToString(CultureInfo.InvariantCulture)) + ",\"task_id\":" + command.DailyWoodTaskId +
                ",\"expected_revision\":" + command.ExpectedRevision + ",\"invocation_attempted\":" +
                Bool(guiSequence == command.Sequence && guiInvocationAttempted) + "}";
        }
        private void CaptureDailyRefine()
        {
            if (dailyRefine == null || life.StopRequested) return;
            SharedDevelopmentContext context;
            try { context = ReadDailyRefineContext(); }
            catch (Exception) { context = null; }
            dailyRefine.Capture(context);
        }
        private string DailyRefineJson()
        {
            return dailyRefine == null ? "null" : "{\"schema\":1,\"revision\":" + dailyRefine.Revision +
                ",\"view\":" + dailyRefine.ViewJson() + ",\"operation\":" + dailyRefine.OperationJson() + "}";
        }
        private void CaptureDailySoul()
        {
            if (dailySoul == null || life.StopRequested) return;
            SharedDevelopmentContext context;
            try { context = ReadDailySoulContext(); }
            catch (Exception) { context = null; }
            dailySoul.Capture(context);
        }
        private string DailySoulJson() { return dailySoul == null ? "null" : dailySoul.Json(); }
        private void CaptureDailyArena()
        {
            if (dailyArena == null || life.StopRequested) return;
            SharedDevelopmentContext context;
            try { context = ReadDailyArenaContext(); }
            catch (Exception) { context = null; }
            dailyArena.Capture(context);
        }
        private string DailyArenaJson() { return dailyArena == null ? "null" : dailyArena.Json(); }
        private void CaptureDailyStone()
        {
            if (dailyStone == null || life.StopRequested) return;
            SharedDevelopmentContext context;
            try { context = ReadDailyStoneContext(); }
            catch (Exception) { context = null; }
            dailyStone.Capture(context);
        }
        private void CaptureDailyMingshi()
        {
            if (dailyMingshi == null || life.StopRequested) return;
            SharedDevelopmentContext context;
            try { context = ReadDailyMingshiContext(); }
            catch (Exception) { context = null; }
            dailyMingshi.Capture(context);
        }
        private string GuildJson() { return dailyGuild == null ? "null" : dailyGuild.Json(); }
        private string DailyRewardJson() { return dailyReward == null ? "null" : dailyReward.Json(); }
        private string DailyTimedJson() { return dailyTimed == null ? "null" : dailyTimed.Json(); }
        private string DailyStoneJson()
        {
            return dailyStone == null ? "null" : "{\"schema\":1,\"revision\":" + dailyStone.Revision +
                ",\"view\":" + dailyStone.ViewJson() + ",\"operation\":" + dailyStone.OperationJson() + "}";
        }
        private string DailyMingshiJson()
        {
            return dailyMingshi == null ? "null" : "{\"schema\":1,\"revision\":" + dailyMingshi.Revision +
                ",\"view\":" + dailyMingshi.ViewJson() + ",\"operation\":" + dailyMingshi.OperationJson() + "}";
        }
        private string DevelopmentJson() { return development == null ? "null" : development.Json(); }
        private string BusinessJson()
        {
            CampaignModel businessCampaign = challenge != null && (challenge.Observer.Pending || challenge.NavigationPending) ? challenge :
                campaign != null && campaign.Observer.Pending ? campaign :
                challenge != null && challenge.Observer.MutationAttempted && (challenge.Phase == "CHALLENGE_LIST" || challenge.Phase != "MAIN" && challenge.Available) ? challenge : campaign;
            string value = businessCampaign == null ? "{\"mutation_attempted\":false,\"pending_attempt\":false,\"resource_rollback\":false,\"attempt\":null}" : businessCampaign.BusinessJson();
            if (challenge != null && challenge.NavigationPending) value = value.Replace("\"pending_attempt\":false", "\"pending_attempt\":true");
            if (challenge != null && challenge.Observer.MutationAttempted || campaign != null && campaign.Observer.MutationAttempted || development != null && development.MutationAttempted || playerRefresh != null && playerRefresh.MutationAttempted || dailyGuild != null && dailyGuild.MutationAttempted || dailyReward != null && dailyReward.MutationAttempted || dailyTimedMutationAttempted || dailyStoneMutationAttempted) value = value.Replace("\"mutation_attempted\":false", "\"mutation_attempted\":true");
            if (dailyMingshiMutationAttempted || dailyArena != null && dailyArena.MutationAttempted || dailySoulMutationAttempted || dailyRefineMutationAttempted) value = value.Replace("\"mutation_attempted\":false", "\"mutation_attempted\":true");
#if JSB_DAILY_WOOD_ENABLED
            if (dailyWoodMutationAttempted) value = value.Replace("\"mutation_attempted\":false", "\"mutation_attempted\":true");
#endif
            string refresh = playerRefresh == null ? "null" : "{\"status\":" + Command.Quote(playerRefresh.Status) + ",\"operation_id\":" + Command.Quote(refreshCommand == null ? "" : refreshCommand.OperationId) + ",\"sequence\":" + (refreshCommand == null ? 0 : refreshCommand.Sequence) + "}";
            return value.Substring(0, value.Length - 1) + ",\"pending_development\":" + Bool(RefreshPending() || development != null && development.Pending) + ",\"pending_player_refresh\":" + Bool(RefreshPending()) + ",\"pending_daily_guild\":" + Bool(dailyGuild != null && dailyGuild.Busy) + ",\"pending_daily_reward\":" + Bool(DailyRewardBusy()) + ",\"pending_daily_timed\":" + Bool(DailyTimedBusy()) + ",\"pending_daily_stone\":" + Bool(DailyStoneBusy()) + ",\"pending_daily_mingshi\":" + Bool(DailyMingshiBusy()) + ",\"pending_daily_arena\":" + Bool(DailyArenaBusy()) + ",\"pending_daily_soul\":" + Bool(DailySoulBusy()) + ",\"pending_daily_refine\":" + Bool(DailyRefineBusy()) + ",\"pending_daily_wood\":" + Bool(DailyWoodBusy()) + ",\"development\":" + DevelopmentJson() + ",\"player_refresh\":" + refresh + "}";
        }
        private void TryAck(string status, string why) { try { Ack(status, why); } catch { } }
        private void Emit(string name, string fields) { store.Event("{" + Identity() + ",\"event\":" + Command.Quote(name) + ",\"frame\":" + lastFrame + "," + fields + "}"); }
        private void TryEmit(string name, string fields) { try { Emit(name, fields); } catch { } }
        private void Finish()
        {
            if (finishing || destroyed) return;
            if (life.ActionPending && mutationAttempted) { TryStartRestore("stop_before_cleanup"); return; }
            if (!BattleLaunch.Stop())
            { life.RecoveryUnknown = true; reason = "battle_launch_unresolved"; }
            if (battleSettlement != null && !battleSettlement.Stop())
            { life.RecoveryUnknown = true; reason = "battle_settlement_unresolved"; }
            if (dailySoul != null) dailySoul.Stop(life.StopReason);
            if (dailyScript != null) dailyScript.Stop(life.StopReason);
            if (dailyPopup != null) dailyPopup.Stop();
#if JSB_DAILY_WOOD_ENABLED
            if (dailyWood != null) dailyWood.Stop(life.StopReason);
#endif
            if (guiCommand != null) CancelGui(life.StopReason.Length == 0 ? "component_stopping_before_gui" : life.StopReason);
            if (dailySoul != null && dailySoul.NeedsUiCleanup)
            {
                lifecycle = "stopping";
                if (publishedBusinessSignature != BusinessSignature()) TryPublish();
                return; // Keep Update, the session guard and the daily clock proof alive.
            }
            FinalizeDailySoulStop();
#if JSB_DAILY_WOOD_ENABLED
            if (dailyWood != null && dailyWood.CleanupPending)
            {
                lifecycle = "stopping";
                CaptureDailyWood(true);
                if (dailyWood.CleanupPending)
                {
                    if (publishedBusinessSignature != BusinessSignature()) TryPublish();
                    return;
                }
            }
            FinalizeDailyWoodStop();
#endif
            if (development != null) development.Stop("component_stopping");
            if (playerRefresh != null) playerRefresh.Stop("component_stopping");
            if (dailyRefresh != null) dailyRefresh.Stop("component_stopping");
            if (dailyRefreshObserved) DailyRefreshProof.Stop();
            if (dailyGuild != null) dailyGuild.Stop("guild_component_stopping");
            StopDailyReward("daily_reward_component_stopping");
            StopDailyTimed("daily_timed_component_stopping");
            StopDailyStone();
            StopDailyMingshi("daily_mingshi_component_stopping");
            StopDailyArena("daily_arena_component_stopping");
            StopDailyRefine();
            finishing = true; ready = false; reason = reason.Length == 0 ? life.StopReason : reason;
            lifecycle = life.RecoveryUnknown ? "recovery_unknown" : "stopping";
            TryEmit("cleanup_requested", "\"reason\":" + Command.Quote(reason)); TryPublish();
            enabled = false; UObject.Destroy(gameObject);
        }
        private void OnDestroy()
        {
            if (!BattleLaunch.Stop() && life != null)
            { life.RecoveryUnknown = true; reason = "battle_launch_unresolved"; }
            if (battleSettlement != null && !battleSettlement.Stop() && life != null)
            { life.RecoveryUnknown = true; reason = "battle_settlement_unresolved"; }
            if (!initialized || destroyed) return;
            if (dailySoul != null) dailySoul.Stop("daily_soul_component_destroyed");
            if (guiCommand != null) CancelGui("destroyed_before_gui");
            FinalizeDailySoulStop();
            if (development != null) development.Stop("component_destroyed");
            if (playerRefresh != null) playerRefresh.Stop("component_destroyed");
            if (dailyRefresh != null)
            {
                dailyRefresh.Stop("component_destroyed");
                if (dailyRefresh.MutationAttempted && dailyRefresh.Status != "OBSERVED")
                { life.RecoveryUnknown = true; reason = "daily_refresh_unresolved_restart_required"; }
            }
            if (dailyRefreshObserved && !DailyRefreshProof.Stop() && !life.RecoveryUnknown)
            { life.RecoveryUnknown = true; reason = "daily_refresh_observer_cleanup_unverified"; }
            if (dailyGuild != null)
            {
                dailyGuild.Stop("guild_component_destroyed");
                if (dailyGuild.Unresolved) { life.RecoveryUnknown = true; reason = "guild_request_unresolved_restart_required"; }
            }
            StopDailyReward("daily_reward_component_destroyed");
            StopDailyTimed("daily_timed_component_destroyed");
            StopDailyStone();
            StopDailyMingshi("daily_mingshi_component_destroyed");
            StopDailyArena("daily_arena_component_destroyed");
            StopDailyRefine();
            if (challenge != null && (challenge.Observer.Pending || challenge.NavigationPending))
            {
                challenge.Observer.Unknown("component_destroyed_with_challenge_unresolved");
                life.RecoveryUnknown = true; reason = "challenge_attempt_unresolved_restart_required";
            }
#if JSB_DAILY_WOOD_ENABLED
            FinalizeDailyWoodStop();
#endif
            // Unexpected destruction during an owned mutation cannot be restored
            // from this lifecycle callback; preserve UNKNOWN and the cross-build guard.
            if (life.ActionPending && mutationAttempted) { life.CompleteAction(false); actionOutcome = "UNKNOWN"; reason = "destroyed_during_action"; }
            destroyed = true; life.MarkDestroyed(); ready = false;
            lifecycle = life.RecoveryUnknown ? "recovery_unknown" : "stopped";
            if (life.RecoveryUnknown)
            {
                try { store.Write("recovery_unknown.json", "{" + Identity() + ",\"reason\":" + Command.Quote(reason) + ",\"action\":" + ActionJson() + ",\"process_restart_required\":true}"); } catch { }
            }
            Entry.ReleaseFromOnDestroy(nonce, activeGuard, life.RecoveryUnknown); activeGuard = IntPtr.Zero;
            TryEmit("component_destroyed", "\"own_component\":true,\"restore_verified\":" + Bool(restoreObserved));
            TryPublish();
            bool scopedAlreadyRejected = (ackName == "daily-reward-command" || ackName == "daily-timed-command" || ackName == "daily-stone-command" || ackName == "daily-mingshi-command" || ackName == "daily-arena-command" || ackName == "daily-soul-command" || ackName == "daily-refine-command" || ackName == "daily-wood-command") && ackStatus == "REJECTED";
            if (ackSequence != 0 && !scopedAlreadyRejected && (ackName == "stop" || ackStatus == "ACCEPTED" || life.RecoveryUnknown))
                TryAck(life.RecoveryUnknown ? "UNKNOWN" : "COMPLETED", reason);
            if (store != null) store.Close(); model = null; original = null;
        }
        private void StopDailyReward(string why)
        {
            if (dailyReward == null) return;
            dailyReward.Stop(why);
            if (dailyReward.Unresolved)
            { life.RecoveryUnknown = true; reason = "daily_reward_request_unresolved_restart_required"; }
        }
        private void StopDailyTimed(string why)
        {
            if (dailyTimed == null) return;
            dailyTimed.Stop(why);
            if (dailyTimed.Busy)
            { life.RecoveryUnknown = true; reason = "daily_timed_request_or_cleanup_unresolved_restart_required"; }
        }
        private void StopDailyStone()
        {
            if (dailyStone == null) return;
            dailyStone.Stop();
            dailyStoneMutationAttempted |= dailyStone.MutationAttempted;
            if (dailyStone.Busy)
            { life.RecoveryUnknown = true; reason = "daily_stone_request_or_callback_unresolved_restart_required"; }
        }
        private void StopDailyMingshi(string why)
        {
            if (dailyMingshi == null) return;
            dailyMingshi.Stop(why);
            dailyMingshiMutationAttempted |= dailyMingshi.MutationAttempted;
            if (dailyMingshi.Busy)
            { life.RecoveryUnknown = true; reason = "daily_mingshi_request_or_cleanup_unresolved_restart_required"; }
        }
        private void StopDailyArena(string why)
        {
            if (dailyArena == null) return;
            dailyArena.Stop(why);
            if (dailyArena.Busy)
            { life.RecoveryUnknown = true; reason = "daily_arena_request_or_cleanup_unresolved_restart_required"; }
        }
        private void FinalizeDailySoulStop()
        {
            if (dailySoul == null) return;
            dailySoul.FinishUiCleanup();
            dailySoulMutationAttempted |= dailySoul.MutationAttempted;
            if (dailySoul.Busy || dailySoul.Unresolved || dailySoul.UiCleanupUnresolved)
            { life.RecoveryUnknown = true; reason = "daily_soul_request_or_cleanup_unresolved_restart_required"; }
        }
        private void StopDailyRefine()
        {
            if (dailyRefine == null) return;
            dailyRefine.Stop();
            dailyRefineMutationAttempted |= dailyRefine.MutationAttempted;
            if (dailyRefine.Busy)
            { life.RecoveryUnknown = true; reason = "daily_refine_request_or_cleanup_unresolved_restart_required"; }
        }
#if JSB_DAILY_WOOD_ENABLED
        private void FinalizeDailyWoodStop()
        {
            if (dailyWood == null) return;
            dailyWood.Stop(life.StopReason);
            dailyWoodMutationAttempted |= dailyWood.MutationAttempted;
            if (dailyWood.Unresolved)
            { life.RecoveryUnknown = true; reason = "daily_wood_request_or_cleanup_unresolved_restart_required"; }
        }
#endif
    }

}
