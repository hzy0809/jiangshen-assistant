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
    internal delegate DevelopmentCallbackCapture DailySoulResponseCapture(object client, object handler, int opcode,
        string operationId, int sequence, Delegate original, Action<byte[]> read, out string why);
    internal delegate D DailySoulContext();
    internal delegate bool DailySoulMainReady();
    internal delegate bool DailySoulInvocationCheck();
    internal delegate bool DailySoulInvocationGate(DailySoulInvocationCheck prepare, DailySoulInvocationCheck check);
#if JSB_DAILY_SOUL_TEST
    internal delegate int DailySoulTestClock();
#endif

    // One panel owns a bounded sequence of ordinary blue/purple gathers.
    // Every round has its own selected counter, quotation and callback witnesses.
    // The caller chooses each growth from the freshly observed board and quote.
    internal sealed class DailySoulModel
    {
        internal const int CategoryId = 2, TaskId = 5;
        private const int Start = 517, Harvest = 518, Grow = 519;
        private const string SoulPage = "resource_soul";
        private const uint Deadline = 18000;
        private const uint CleanupDeadline = 8000;
        private static readonly Type[] ResponseArgs = new Type[] { typeof(MemoryStream), typeof(object) };

        private sealed class ColorQuote
        {
            internal object Template;
            internal int Mode, Used, Free;
            internal uint Price, Rate, RequiredLevel;
            internal byte RequiredVip;
            internal bool Known, Unlocked, StartAvailable;
            internal string Reason = "daily_soul_mode_unverified";
        }

        private sealed class Frame
        {
            internal D Context;
            internal DailyTransport Transport;
            internal Component Panel;
            internal GameObject Background;
            internal object SubUi, SoulData, Template, GrowTemplate, Reward, Rewards, Acts, Listed, Row, ActivityTemplate;
            internal ulong Role;
            internal uint Now, Cycle, Next, Server, GoldSpent, Exchanges;
            internal DateTime Shown;
            internal long Coins, GrowthCost;
            internal int Gold, BoundGold, Used, BlueUsed, PurpleUsed, OrangeUsed, Free, Selected = 1, Current = -1, Required, Refresh, Stage, Mode;
            internal uint Price, GrowthRate;
            internal ColorQuote[] Quotes;
            internal bool Unlocked, StartAvailable, ActivityAvailable;
            internal long Board;
            internal int[] Inventory;
            internal bool Visible, Loading, Enabled, Gathered, StockPage, FreeButton, NetworkReady, AllFull, ResourcesRead, CoinGrowth, GrowthVip;
            internal string Boundary = "daily_soul_unobserved", Gate = "", Feature = "", Room = "", Selection = "", ActivityReason = "daily_soul_activity_unobserved";

            internal bool SameOwner(Frame before)
            {
                return before != null && Boundary == "" && before.Boundary == "" &&
                    object.ReferenceEquals(Context.Root, before.Context.Root) && object.ReferenceEquals(Context.UiRoot, before.Context.UiRoot) &&
                    object.ReferenceEquals(Context.GlobalUi, before.Context.GlobalUi) && object.ReferenceEquals(Context.Character, before.Context.Character) &&
                    Role == before.Role && Server == before.Server && Room == before.Room &&
                    Cycle == before.Cycle && Next == before.Next && Now >= before.Now &&
                    Transport.Same(before.Transport) && object.ReferenceEquals(Panel, before.Panel) &&
                    object.ReferenceEquals(SoulData, before.SoulData) && object.ReferenceEquals(Template, before.Template) &&
                    Selected == before.Selected && Unlocked == before.Unlocked && StartAvailable == before.StartAvailable && Price == before.Price && Free == before.Free && object.ReferenceEquals(GrowTemplate, before.GrowTemplate) &&
                    GrowthCost == before.GrowthCost && GrowthRate == before.GrowthRate && CoinGrowth == before.CoinGrowth;
            }

            internal Frame Select(int mode)
            {
                Frame chosen = (Frame)MemberwiseClone();
                ColorQuote q = Quotes[mode - 1];
                chosen.Selected = mode; chosen.Template = q.Template; chosen.Used = q.Used;
                chosen.Free = q.Free; chosen.Price = q.Price; chosen.GrowthRate = q.Rate; chosen.Unlocked = q.Known && q.Unlocked; chosen.StartAvailable = q.Known && q.StartAvailable;
                return chosen;
            }

            internal bool SamePanel(Frame before)
            {
                return before != null && Visible && Shown != default(DateTime) && Shown == before.Shown &&
                    object.ReferenceEquals(SubUi, Panel) && Selection == SoulPage && Background != null &&
                    object.ReferenceEquals(Background, before.Background);
            }

            internal bool SameQuote(Frame before)
            {
                return Coins == before.Coins && SamePremium(before);
            }

            internal bool SamePremium(Frame before)
            {
                return Gold == before.Gold && BoundGold == before.BoundGold &&
                    GoldSpent == before.GoldSpent && Exchanges == before.Exchanges &&
                    (Selected == 2 ? BlueUsed == before.BlueUsed : PurpleUsed == before.PurpleUsed) && OrangeUsed == before.OrangeUsed;
            }

            internal bool SameStep(Frame before)
            {
                return SameOwner(before) && Gate == before.Gate && Feature == before.Feature && Selection == before.Selection && SameQuote(before) &&
                    object.ReferenceEquals(SubUi, before.SubUi) && object.ReferenceEquals(Background, before.Background) &&
                    Visible == before.Visible && Loading == before.Loading && Enabled == before.Enabled && Shown == before.Shown &&
                    StockPage == before.StockPage && FreeButton == before.FreeButton && Gathered == before.Gathered &&
                    NetworkReady == before.NetworkReady && AllFull == before.AllFull && GrowthVip == before.GrowthVip && Stage == before.Stage && Mode == before.Mode &&
                    Board == before.Board && Used == before.Used && InventoryMatches(before.Inventory, null);
            }

            internal bool InventoryMatches(int[] before, int[] award)
            {
                if (Inventory == null || before == null) return false;
                for (int i = 0; i < Inventory.Length; i++)
                    if (Inventory[i] != (long)before[i] + (award == null ? 0 : award[i])) return false;
                return true;
            }
        }

        private sealed class StockCall
        {
            internal MethodInfo Method, Response;
            internal object Receiver;
            internal object[] Args;
        }

        private readonly DailySoulResponseCapture installResponse;
        private readonly DailyRefreshAdmission refresh;
        private readonly DailySoulMainReady mainReady;
        private readonly DailySoulInvocationCheck invocationWindow;
#if JSB_DAILY_SOUL_TEST
        private readonly DailySoulTestClock fixtureClock;
#endif
        private readonly List<string> usedOperations = new List<string>();
        private Frame frame = new Frame(), origin, admitted, opened, harvested;
        private DevelopmentCallbackCapture response, activity = null;
        private object responseHandler;
        private DailySoulWire.Receipt receipt;
        private string operation = "", status = "IDLE", reason = "", queuedAction = "", stage = "";
        private string uiCleanup = "none";
        private int sequence, revision, queuedRevision, lastSequence, since, opcode, cleanupSince, ownerThread;
        private long startBoard, coinsSpent, startCost;
        private int growthCount, startUsed = -1, selectedMode = 1, round;
        private bool freeOnly = true;
        private DateTime openShown;
        private GameObject openBackground;
        private bool attempted, startVerified, harvestInvoked, harvestVerified, progressVerified, lastInvoked, openInvoked, openBound, closeInvoked;
        private bool growthVerified, growPending, autoHarvest, openReused;
        private int growthEvidenceSequence, harvestEvidenceSequence;
        private long growthBeforeBoard, growthAfterBoard, harvestBeforeBoard;
        private bool harvestBeforeKnown;
        private List<long> growthSteps;
        private int[] harvestColors, harvestNumbers;
        // Frozen evidence is never an input to admission or the current frame.
        // Keep the most recent verified round readable across scene transitions.
        private string harvestReceiptPrefix = "", historicalReceiptJson = "null";
        private Frame adoptionCandidate;
        private SoulBoardAuthority adoptionAuthority;
        private int adoptionFirstFrame = -1, adoptionLastFrame = -1, adoptionSequence;
        private bool adoptionStable, adoptedBoard;
        private long adoptionBeforeBoard;
        private string adoptionSourceSession = "", adoptionSourceOperation = "", adoptionHash = "", adoptionAuthorityJson = "null", adoptionReason = "";

        internal DailySoulModel(DailySoulResponseCapture install, DailySoulMainReady freshMainReady, DailySoulInvocationCheck finalWindow = null)
        { installResponse = install; mainReady = freshMainReady; invocationWindow = finalWindow; refresh = DailyRefreshGate.Read; }

#if JSB_DAILY_SOUL_TEST
        internal DailySoulModel(DailySoulResponseCapture install, DailySoulMainReady freshMainReady,
            DailyRefreshAdmission fixtureRefresh, DailySoulTestClock fixtureTicks)
        { installResponse = install; mainReady = freshMainReady; refresh = fixtureRefresh; fixtureClock = fixtureTicks; }
#endif

        private int Ticks()
        {
#if JSB_DAILY_SOUL_TEST
            if (fixtureClock != null) return fixtureClock();
#endif
            return Environment.TickCount;
        }

        internal bool Busy { get { return queuedAction != "" || status == "OPENING" || status == "READY" || status == "PENDING" ||
            status == "CLOSING" || Unresolved || UiCleanupUnresolved; } }
        internal bool Unresolved { get { return status == "UNKNOWN" || attempted && !harvestVerified; } }
        internal bool NeedsUiCleanup { get { return uiCleanup == "waiting" || uiCleanup == "closing"; } }
        internal bool UiCleanupUnresolved { get { return NeedsUiCleanup || uiCleanup == "unknown"; } }
        internal bool OutcomeUnknown { get { return status == "UNKNOWN"; } }
        internal bool GuiInvocationAttempted { get { return lastInvoked; } }
        internal bool MutationAttempted { get { return attempted; } }
        internal int Revision { get { return revision; } }
        internal string Action { get { return Admission(frame); } }
        internal bool OwnsAdoption(string id) { return adoptedBoard && operation == id; }

        internal void Capture(D context)
        {
            Frame prior = frame;
            string oldStatus = status, oldReason = reason, oldAction = Action;
            string oldAuthority = adoptionAuthorityJson;
            bool oldStart = startVerified, oldHarvest = harvestVerified, oldProgress = progressVerified, oldGrowth = growthVerified;
            try
            {
                frame = Read(context);
                CaptureAdoptionAuthority();
                if (!NeedsUiCleanup && (status == "UNKNOWN" || status == "STOPPED" || status == "REJECTED")) Detach();
                else if (!NeedsUiCleanup && queuedAction == "" && Busy && !UiCleanupUnresolved) Observe(frame);
            }
            catch (Exception error)
            {
                Exception cause = error;
                while (cause is TargetInvocationException && cause.InnerException != null) cause = cause.InnerException;
                frame = new Frame { Boundary = "daily_soul_readback_failed:" + cause.GetType().FullName + ":" + cause.Message };
                if (Busy && !UiCleanupUnresolved && status != "UNKNOWN") End(attempted ? "UNKNOWN" : "REJECTED", frame.Boundary);
            }
            bool causalChange = revision == 0 || frame.Boundary != prior.Boundary || frame.Role != prior.Role || frame.Cycle != prior.Cycle ||
                frame.Boundary == "" && !frame.SameStep(prior) ||
                oldStatus != status || oldReason != reason || oldAction != Action || oldStart != startVerified ||
                oldHarvest != harvestVerified || oldProgress != progressVerified || oldGrowth != growthVerified;
            bool informationChange = frame.ActivityAvailable != prior.ActivityAvailable || frame.ActivityReason != prior.ActivityReason ||
                frame.Current != prior.Current || frame.Required != prior.Required || oldAuthority != adoptionAuthorityJson;
            if (causalChange || informationChange)
            {
                AdvanceRevision();
                // Informational daily counters cannot revoke an already
                // admitted standalone request whose actual baseline is intact.
                if (!causalChange && status == "QUEUED" && queuedAction != "") queuedRevision = revision;
            }
        }

        private void CaptureAdoptionAuthority()
        {
            if (status != "IDLE" && status != "COMPLETED" && status != "REJECTED") return;
            if (frame.Boundary != "" || frame.Gate != "PASS" || frame.Feature != "" || !frame.NetworkReady || frame.Board == 0 || UiCleanupUnresolved)
            { adoptionCandidate = null; adoptionStable = false; adoptionAuthorityJson = "null"; adoptionReason = "daily_soul_adoption_context_unavailable"; return; }
            try
            {
                SoulBoardAuthority current = ProcessRecovery.CaptureBoardAuthority(frame.Context, frame.Board);
                int mode = current.Mode != 0 ? current.Mode : frame.Mode == 1 || frame.Mode == 2 ? frame.Mode : selectedMode;
                // m_curType is the current UI selection. A retired verified
                // START supplies the board's counter provenance independently;
                // 518 has no mode argument. SameStep pins the actual UI mode.
                selectedMode = mode; frame = frame.Select(mode);
                int currentFrame = Time.frameCount;
                if (adoptionCandidate == null || !frame.SameStep(adoptionCandidate) || adoptionAuthority == null || current.Witness != adoptionAuthority.Witness)
                { adoptionCandidate = frame; adoptionFirstFrame = currentFrame; adoptionStable = false; }
                else if (adoptionStable) return; // Keep the proved frame pair immutable.
                else if (currentFrame > adoptionFirstFrame) adoptionStable = true;
                adoptionLastFrame = currentFrame; adoptionAuthority = current;
                adoptionReason = adoptionStable ? "" : "daily_soul_adoption_await_distinct_stable_frames";
                adoptionAuthorityJson = "{\"schema\":1,\"available\":" + D.B(adoptionStable) + ",\"first_frame\":" + adoptionFirstFrame +
                    ",\"last_frame\":" + adoptionLastFrame + ",\"character_id\":" + D.Q(frame.Role.ToString(CultureInfo.InvariantCulture)) +
                    ",\"cycle\":" + frame.Cycle + ",\"next_resource_time\":" + frame.Next + ",\"mode\":" + selectedMode +
                    ",\"board\":" + BoardJson(frame.Board) + ",\"board_packed\":" + frame.Board.ToString(CultureInfo.InvariantCulture) +
                    ",\"resources\":" + ResourcesJson(frame) + ",\"witness\":" + current.Witness + "}";
            }
            catch (Exception error)
            {
                adoptionCandidate = null; adoptionStable = false; adoptionAuthority = null;
                adoptionReason = error is ProbeFailure ? error.Message : "daily_soul_adoption_witness_unavailable";
                adoptionAuthorityJson = "{\"schema\":1,\"available\":false,\"reason\":" + D.Q(adoptionReason) + "}";
            }
        }

        private bool AdoptionCurrent(Frame current)
        {
            if (!adoptionStable || adoptionCandidate == null || adoptionAuthority == null || !current.SameStep(adoptionCandidate) ||
                current.Board == 0 || !current.NetworkReady || adoptionLastFrame <= adoptionFirstFrame) return false;
            try { return ProcessRecovery.CaptureBoardAuthority(current.Context, current.Board).Witness == adoptionAuthority.Witness; }
            catch (Exception) { return false; }
        }

        internal bool Queue(string action, ulong role, string operationId, int commandSequence, int expectedRevision, out string why)
        { return Queue(action, role, operationId, commandSequence, expectedRevision, 1, true, out why); }

        internal bool Queue(string action, ulong role, string operationId, int commandSequence, int expectedRevision, int mode, bool onlyFree, out string why)
        {
            why = "daily_soul_admission";
            string available = Action;
            bool rearm = action == "next";
            bool first = action == "open";
            bool adopt = action == "adopt";
            if (mode < 1 || mode > 2 || frame.Quotes == null || role == 0 || frame.Role != role ||
                expectedRevision != revision || commandSequence <= lastSequence ||
                !new DevelopmentCommand { OperationId = operationId }.ValidId()) return false;
            if (first || rearm || adopt)
            {
                if ((first || adopt) && (status != "IDLE" && status != "COMPLETED" && status != "REJECTED" || Busy) ||
                    rearm && (onlyFree != freeOnly || round >= 100 || available != "close" || !harvestVerified || operationId != operation || !frame.SameStep(harvested))) return false;
                Frame chosen = frame.Select(mode);
                if (adopt)
                { if (available != "adopt" || mode != selectedMode || !AdoptionCurrent(chosen)) return false; }
                else if (first && AdmissionForOpen(chosen) == "" || !CanStart(chosen, onlyFree)) return false;
                if (first || adopt)
                {
                    if (usedOperations.Count >= 255 || usedOperations.Contains(operationId)) return false;
                    usedOperations.Add(operationId); operation = operationId; round = 0;
                    opened = null; openInvoked = openBound = closeInvoked = false;
                    openShown = default(DateTime); openBackground = null; uiCleanup = "none";
                }
                selectedMode = mode; freeOnly = onlyFree; frame = chosen;
                origin = frame; harvested = null; openReused = frame.Visible;
                attempted = startVerified = harvestInvoked = harvestVerified = progressVerified = false;
                adoptedBoard = adopt; adoptionSequence = adopt ? commandSequence : 0;
                adoptionBeforeBoard = adopt ? frame.Board : 0;
                adoptionSourceSession = adopt ? adoptionAuthority.SourceSession : "";
                adoptionSourceOperation = adopt ? adoptionAuthority.SourceOperation : "";
                adoptionHash = adopt && adoptionSourceSession != "" ? SoulRecoveryRequest.Hash(System.Text.Encoding.UTF8.GetBytes(adoptionAuthorityJson)) : "";
                startBoard = adopt ? frame.Board : 0; startCost = adopt ? 0 : StartCost(frame); startUsed = adopt ? frame.Used : -1;
                coinsSpent = 0; growthCount = 0; growthVerified = growPending = autoHarvest = false;
                receipt = null; round++;
                growthEvidenceSequence = harvestEvidenceSequence = 0;
                growthSteps = null; harvestColors = harvestNumbers = null; harvestBeforeKnown = false;
                harvestReceiptPrefix = "";
            }
            else if (available == "" || action != available && !(available == "grow" && action == "harvest") ||
                mode != selectedMode || onlyFree != freeOnly || operationId != operation || !frame.SameOwner(origin)) return false;
            admitted = frame;
            sequence = lastSequence = commandSequence;
            stage = queuedAction = action;
            status = "QUEUED";
            opcode = action == "start" ? Start : action == "harvest" ? Harvest : action == "grow" ? Grow : 0;
            if (action == "grow") growthVerified = false;
            lastInvoked = false; reason = ""; why = "";
            AdvanceRevision();
            queuedRevision = revision;
            return true;
        }

        internal bool ExecuteGui(DailySoulContext freshContext, DevelopmentPublication publish, DailySoulInvocationGate begin, out string why)
        {
            why = "daily_soul_not_queued";
            if (queuedAction == "") return false;
            if (freshContext == null || publish == null || begin == null || !D.Gui)
                return Reject("daily_soul_gui_required", out why);
            string action = queuedAction;
            int queuedSequence = sequence, expectedQueuedRevision = queuedRevision;
            Frame baseline = admitted;
            // Once an intervening capture advances the version, restoring the frame cannot revive this command.
            if (!QueuedCall(action, baseline, queuedSequence, expectedQueuedRevision))
                return Reject("daily_soul_queued_revision_changed", out why);
            object client = baseline.Context.Client;
            if (!Monitor.TryEnter(client, 0)) return Reject("daily_soul_client_busy", out why);
            bool controller = false;
            try
            {
                Frame current = Read(freshContext());
                if (!ReadyToInvoke(current, baseline, action))
                    return Reject("daily_soul_gui_baseline_changed", out why);

                controller = true; publish(); controller = false;
                StockCall stock = null;
                string refusal = "daily_soul_final_admission_changed";
                bool prepared = false, checkedByController = false;
                controller = true;
                bool allowed = begin(delegate
                {
                    if (!QueuedCall(action, baseline, queuedSequence, expectedQueuedRevision)) return false;
                    try
                    {
                        stock = PrepareStock(current, action);
                        if (!PrepareRequest(current, action, out refusal)) return false;
                        current = Read(freshContext());
                        refusal = "daily_soul_publication_baseline_changed";
                        return prepared = ReadyToInvoke(current, baseline, action) &&
                            current.Context.SameIdentity(baseline.Context) && StillAdmissible(current, action);
                    }
                    catch (Exception) { refusal = "daily_soul_preparation_failed"; return false; }
                }, delegate
                {
                    refusal = "daily_soul_final_admission_changed";
                    try
                    {
                        return checkedByController = prepared && stock != null && QueuedCall(action, baseline, queuedSequence, expectedQueuedRevision) &&
                            StillAdmissible(current, action);
                    }
                    catch (Exception) { return false; }
                });
                controller = false;
                if (!QueuedCall(action, baseline, queuedSequence, expectedQueuedRevision))
                {
                    why = "daily_soul_cancelled_before_invoke";
                    if (queuedAction == action && sequence == queuedSequence) return Reject(why, out why);
                    return false;
                }
                bool currentEnough = allowed && checkedByController && StillAdmissible(current, action);
                // Recheck the controller's clock after the final live identity reads.
                controller = true;
                bool windowOpen = invocationWindow == null || invocationWindow();
                controller = false;
                if (!currentEnough || !windowOpen || !QueuedCall(action, baseline, queuedSequence, expectedQueuedRevision))
                    return Reject(refusal, out why);

                queuedAction = ""; since = Ticks();
                if (action == "open" || action == "adopt" || action == "next" || action == "close")
                {
                    status = action != "close" ? "OPENING" : "CLOSING";
                    lastInvoked = true;
                    ownerThread = Thread.CurrentThread.ManagedThreadId;
                    if (action != "close") openInvoked = true;
                    else closeInvoked = true;
                    stock.Method.Invoke(stock.Receiver, stock.Args);
                    if (!TransportCurrent(current)) return UnknownAfterCall("daily_soul_transport_changed_after_call", out why);
                    if (action != "close" && !BindOpen(current)) return Reject("daily_soul_open_not_owned", out why);
                }
                else if (!InvokeRequest(current, stock, out why)) return UnknownAfterCall(why, out why);
                AdvanceRevision();
                why = "";
                return true;
            }
            catch (Exception)
            {
                if (controller) throw; // The controller decides Stop before cancelling the queue.
                return Reject("daily_soul_invoke_failed", out why);
            }
            finally { Monitor.Exit(client); }
        }

        private bool QueuedCall(string action, Frame baseline, int expectedSequence, int expectedRevision)
        {
            return status == "QUEUED" && queuedAction == action && sequence == expectedSequence &&
                revision == expectedRevision && object.ReferenceEquals(admitted, baseline);
        }

        private bool ReadyToInvoke(Frame f, Frame baseline, string action)
        { return D.Gui && f.SameStep(baseline) && f.NetworkReady && (action == "open" || action == "adopt" || action == "next" || !Expired()); }

        private bool StillAdmissible(Frame f, string action)
        {
            if (!D.Gui || action != "open" && action != "adopt" && action != "next" && Expired() || DevelopmentCallbackCapture.AnyCallbackExecuting ||
                !OwnedContextCurrent(f) || !D.Bool(D.Static(Model.FirstType("Global"), "extern_enabled")) ||
                D.Bool(D.Static(Model.FirstType("PopupUI"), "popup")) || !object.ReferenceEquals(f.Context.SubUi, f.SubUi))
                return false;
            if (action != "open" && action != "adopt" && !OwnedPanelCurrent(f)) return false;
            if (action == "adopt" && !AdoptionCurrent(f)) return false;
            return (action != "start" || StartQuoteCurrent(f)) &&
                (action != "grow" || CanGrow(f) && GrowthQuoteCurrent(f)) && RefreshCurrent(f);
        }

        private static long StartCost(Frame f) { return f.Unlocked && f.Used < f.Free ? 0 : f.Price; }

        private static bool CanStart(Frame f, bool onlyFree)
        { return f.StartAvailable && f.Used < Byte.MaxValue &&
            (f.Unlocked && f.Used < f.Free || !onlyFree && f.Price > 0 && f.Coins >= StartCost(f)); }
        private bool CanStart(Frame f) { return CanStart(f, freeOnly); }


        private bool StartQuoteCurrent(Frame f)
        {
            object quote = D.Template("general_soul_start_mode", (uint)selectedMode);
            return CanStart(f) && StartCost(f) == startCost && object.ReferenceEquals(quote, f.Template) &&
                Field<byte>(quote, "type") == selectedMode && Field<uint>(quote, "gather_rate") == 1 &&
                Field<uint>(quote, "price") == f.Price && Field<int>(quote, "free_times") == f.Free && (!freeOnly || QuoteUnlocked(f.Context.Character, quote, selectedMode)) &&
                Field<byte>(f.Context.Character, selectedMode == 2 ? "generalsoul_purple_times" : "generalsoul_blue_times") == f.Used && D.Money(f.Context.Character) == f.Coins;
        }

        private int StartedUses
        { get { return startUsed >= 0 ? startUsed : opcode == Start && receipt != null ? receipt.Used : origin.Used + 1; } }
        private int BoardMode { get { return adoptedBoard && opened != null ? opened.Mode : selectedMode; } }

        private static bool CanGrow(Frame f)
        {
            return f.CoinGrowth && f.GrowthVip && f.GrowthRate == 1 && f.GrowthCost > 0 &&
                f.Coins >= f.GrowthCost && !f.AllFull;
        }

        private static bool GrowthQuoteCurrent(Frame f)
        {
            object quote = D.Template("general_soul_grow_mode", 1);
            return object.ReferenceEquals(quote, f.GrowTemplate) && Field<sbyte>(quote, "grow_mode") == 1 &&
                !Field<bool>(quote, "isgold") && Field<int>(quote, "cost") == f.GrowthCost &&
                VipLevel(f.Context.Character) >= Field<sbyte>(quote, "vip_require") &&
                D.Money(f.Context.Character) == f.Coins && !Field<bool>(f.Panel, "isAllSoulFull");
        }

        private bool CurrentQuote(Frame f)
        {
            return f.SamePremium(origin) && (f.Coins == origin.Coins - coinsSpent ||
                (growPending || opcode == Start && !startVerified) && receipt != null && f.Coins == receipt.Coins);
        }

        private bool RefreshCurrent(Frame f)
        {
            object role = f.Context.Character;
            uint now, day, cycle;
            // The production gate checks the callback proof without rescanning
            // the activity caches or enumerating Unity objects.
            return refresh(role, out now, out day, out cycle) == "" && cycle == f.Cycle && now >= f.Now &&
                Field<uint>(role, "next_resource_time") == f.Next;
        }

        private static bool TransportCurrent(Frame f)
        { return object.ReferenceEquals(DailyTransport.CurrentClient(f.Context.Character), f.Context.Client) && f.Transport.StillCurrent(); }

        private static bool OwnedContextCurrent(Frame f)
        {
            return TransportCurrent(f) && RootMatches(f.Context) && Field<string>(f.Context.Root, "LevelID") == f.Room &&
                Field<ulong>(f.Context.Character, "id") == f.Role && Field<uint>(f.Context.Character, "SID") == f.Server;
        }

        private static bool OwnedPanelCurrent(Frame f)
        {
            return object.ReferenceEquals(f.Context.SubUi, f.Panel) && StockSelection() == SoulPage &&
                Field<DateTime>(f.Panel, "visible_time") == f.Shown && object.ReferenceEquals(D.Read(f.Panel, "gather_bg"), f.Background);
        }


        private bool BindOpen(Frame f)
        {
            if (!object.ReferenceEquals(f.Context.SubUi, f.Panel) || StockSelection() != SoulPage) return false;
            if (D.Visible(f.Panel))
            {
                openShown = Field<DateTime>(f.Panel, "visible_time");
                if (openShown == default(DateTime) || (openReused ? openShown != f.Shown : openShown == f.Shown)) return false;
                openBackground = D.Read(f.Panel, "gather_bg") as GameObject;
                if (openReused) opened = f;
            }
            else if (!D.Loading(f.Panel)) return false;
            return openBound = true;
        }

        internal void Stop(string why)
        {
            if (status == "UNKNOWN" || status == "STOPPED") return;
            if (Busy) { End(attempted && !harvestVerified ? "UNKNOWN" : "STOPPED", why); AdvanceRevision(); }
        }

        internal void NotInvoked()
        {
            if (queuedAction != "") { End(attempted ? "UNKNOWN" : "REJECTED", "daily_soul_command_not_invoked"); AdvanceRevision(); }
        }

        // Controller calls this on the Unity Update thread, including during
        // bounded Stop recovery. The only possible action is closing our panel.
        internal void TryUiCleanup(DailySoulContext freshContext)
        {
            if (!NeedsUiCleanup || Thread.CurrentThread.ManagedThreadId != ownerThread) return;
            if (CleanupExpired()) { FinishUiCleanup(); return; }
            if (DevelopmentCallbackCapture.AnyCallbackExecuting) return;
            try
            {
                object client = opened.Context.Client;
                if (!Monitor.TryEnter(client, 0)) return;
                try
                {
                    Frame f = Read(freshContext());
                    if (!f.SameOwner(origin) || !f.StockPage || f.Shown != opened.Shown)
                    { FinishUiCleanup(); return; }
                    if (ClearUi(f))
                    {
                        if (closeInvoked || RecoveryRequestQuiescent(f))
                        {
                            if (CleanupExpired()) { FinishUiCleanup(); return; }
                            uiCleanup = "clear"; Detach(); AdvanceRevision();
                        }
                        return;
                    }
                    if (!f.SamePanel(opened) && (attempted || !SameOpeningPanel(f)))
                    {
                        // After our close, allow a transient unready main
                        // gate, but never attempt another toggle.
                        if (closeInvoked && f.SubUi == null && !f.Visible) return;
                        FinishUiCleanup();
                        return;
                    }
                    if (closeInvoked || f.Gate != "PASS" || f.Loading || !RecoveryRequestQuiescent(f) || !RecoveryPanelReady(f))
                        return;
                    MethodInfo close = StockClose();
                    if (!OwnedContextCurrent(f) || !OwnedPanelCurrent(f) || !RefreshCurrent(f) ||
                        !D.Bool(D.Static(Model.FirstType("Global"), "extern_enabled")) ||
                        D.Bool(D.Static(Model.FirstType("PopupUI"), "popup"))) return;
                    if (CleanupExpired()) { FinishUiCleanup(); return; }
                    closeInvoked = true;
                    uiCleanup = "closing";
                    close.Invoke(null, new object[] { null });
                    if (!TransportCurrent(f)) { FinishUiCleanup(); return; }
                    AdvanceRevision();
                }
                finally { Monitor.Exit(client); }
            }
            catch (Exception) { FinishUiCleanup(); }
        }

        internal void FinishUiCleanup()
        {
            if (NeedsUiCleanup) { uiCleanup = "unknown"; AdvanceRevision(); }
            Detach();
        }

        private bool ClearUi(Frame f)
        {
            return opened != null && f.Shown == opened.Shown && !f.Visible && !f.Loading && f.SubUi == null &&
                f.Background == null && f.Gate == "PASS" && f.Context.Panels() == "PASS" && mainReady != null && mainReady() &&
                OwnedContextCurrent(f) && RefreshCurrent(f);
        }

        private bool RecoveryRequestQuiescent(Frame f)
        {
            // NetworkReady also excludes queued soul replies and competing
            // soul handlers. Removal alone cannot establish the original's return.
            if (!f.NetworkReady) return false;
            if (!attempted || (startVerified || adoptedBoard) && !harvestInvoked && !growPending || harvestVerified) return true;
            if (response == null || response.Opcode != opcode ||
                response.OperationId != operation || !response.StillInstalled()) return false;
            DevelopmentCallbackWitness w = response.Read();
            return w.Armed && w.CallbackReturned && w.Calls == 1 && !w.OriginalThrew && w.Error == null;
        }

        private bool RecoveryPanelReady(Frame f)
        {
            if (!CurrentQuote(f)) return false;
            if (!attempted)
                return f.Stage == (adoptedBoard ? 1 : 0) && f.Board == (adoptedBoard ? adoptionBeforeBoard : 0) &&
                    (!adoptedBoard || f.Mode == BoardMode) && f.Used == origin.Used && f.InventoryMatches(origin.Inventory, null);
            if ((autoHarvest || growPending && receipt != null && receipt.AutoResult) && receipt != null)
                return f.Enabled && f.Stage == 0 && f.Mode == BoardMode && f.Board == 0 &&
                    f.Used == StartedUses && f.InventoryMatches(origin.Inventory, receipt.Award);
            if (!harvestInvoked)
            {
                if (!f.Enabled || f.Gathered || !f.InventoryMatches(origin.Inventory, null)) return false;
                if (!(startVerified || adoptedBoard) && response.Read().NullResponse)
                    return f.Stage == 0 && f.Mode == 0 && f.Board == 0 && f.Used == origin.Used;
                long expected = growPending && receipt != null ? receipt.Board : startVerified || adoptedBoard ? startBoard : receipt == null ? 0 : receipt.Board;
                return expected != 0 && f.Stage == 1 && f.Mode == BoardMode && f.Board == expected && f.Used == StartedUses;
            }
            if (!harvestVerified && response.Read().NullResponse)
            {
                // Pinned 518 callback does nothing on null. tcp_base.Run
                // removes the expired handler after that call returns. The
                // exact Harvest/disabled state has no result animation.
                return !f.Enabled && f.Stage == 2 && f.Mode == (adoptedBoard ? opened.Mode : selectedMode) && f.Board == startBoard &&
                    f.Used == StartedUses && f.InventoryMatches(origin.Inventory, null);
            }
            return f.Enabled && f.Stage == 0 && f.Mode == 0 && f.Board == 0 && f.Used == StartedUses &&
                receipt != null && f.InventoryMatches(origin.Inventory, receipt.Award);
        }

        private string Admission(Frame f)
        {
            if (queuedAction != "" || UiCleanupUnresolved || f.Boundary != "" || f.Gate != "PASS" || f.Feature != "" ||
                !f.NetworkReady || f.Loading || !f.StockPage) return "";
            if (status == "IDLE" || status == "COMPLETED" || status == "REJECTED")
            {
                if (AdoptionForOpen(f)) return "adopt";
                return AdmissionForOpen(f);
            }
            if (status != "READY" || !f.SameOwner(origin) || !f.SamePanel(opened) || !f.Enabled) return "";
            if (harvestVerified) return f.SameStep(harvested) ? "close" : "";
            if (startVerified || adoptedBoard)
                return f.Stage == 1 && f.Mode == BoardMode && f.Board == startBoard && !harvestInvoked ?
                    CanGrow(f) ? "grow" : "harvest" : "";
            return CanStart(f) && StartCost(f) == startCost && f.Stage == 0 && f.Board == 0 && f.FreeButton && !f.Gathered ? "start" : "";
        }

        private string AdmissionForOpen(Frame f)
        {
            if (UiCleanupUnresolved || f.Boundary != "" || f.Gate != "PASS" || f.Feature != "" || !f.NetworkReady || f.Loading || !f.StockPage) return "";
            bool closed = !f.Visible && f.SubUi == null;
            bool reusable = f.Visible && f.Enabled && object.ReferenceEquals(f.SubUi, f.Panel) && f.Selection == SoulPage &&
                f.Shown != default(DateTime) && f.Background != null && f.Stage == 0 && f.FreeButton && !f.Gathered;
            return f.StartAvailable && f.Board == 0 && (closed || reusable) ? "open" : "";
        }

        private bool AdoptionForOpen(Frame f)
        {
            if (!adoptionStable || UiCleanupUnresolved || f.Boundary != "" || f.Gate != "PASS" || f.Feature != "" ||
                !f.NetworkReady || f.Loading || !f.StockPage || f.Board == 0 || adoptionCandidate == null || !f.SameStep(adoptionCandidate)) return false;
            return !f.Visible && f.SubUi == null || f.Visible && f.Enabled && object.ReferenceEquals(f.SubUi, f.Panel) &&
                f.Selection == SoulPage && f.Shown != default(DateTime) && f.Background != null && f.Stage == 1 && (f.Mode == 1 || f.Mode == 2) && !f.Gathered;
        }

        private bool SameOpeningPanel(Frame f)
        {
            return opened != null && f.Visible && f.Shown != default(DateTime) && f.Shown == opened.Shown &&
                object.ReferenceEquals(f.SubUi, f.Panel) && f.Selection == SoulPage &&
                object.ReferenceEquals(f.Background, opened.Background);
        }

        private void Observe(Frame f)
        {
            bool expired = Expired();
            if (!f.SameOwner(origin) || f.Feature != "" || !f.StockPage || !CurrentQuote(f))
            { End(attempted ? "UNKNOWN" : "REJECTED", "daily_soul_owner_cache_or_quote_changed"); return; }
            if (status == "OPENING")
            {
                if (f.Used != origin.Used || f.Board != (adoptedBoard ? adoptionBeforeBoard : 0) || !f.InventoryMatches(origin.Inventory, null) ||
                    !openBound ||
                    !object.ReferenceEquals(f.SubUi, f.Panel) || f.Selection != SoulPage ||
                    openShown != default(DateTime) && f.Shown != openShown ||
                    openBackground != null && !object.ReferenceEquals(f.Background, openBackground))
                { End("REJECTED", "daily_soul_open_not_owned"); return; }
                if (f.Visible && f.Shown != default(DateTime) && (openReused || f.Shown != origin.Shown))
                {
                    // Loading can finish before the panel is ready for input.
                    // Pin the first visible generation, even in that interval.
                    openShown = f.Shown;
                    if (openBackground == null) openBackground = f.Background;
                    // Own the opened generation before input readiness, so an
                    // unstarted gather can still close normally after a timeout.
                    opened = f;
                }
                if (f.Visible && f.Enabled && !f.Loading && f.Background != null && f.Shown != default(DateTime) &&
                    (openReused || f.Shown != origin.Shown) && f.Gate == "PASS" && !f.Gathered &&
                    (adoptedBoard ? f.Stage == 1 && (f.Mode == 1 || f.Mode == 2) : f.Stage == 0 && f.FreeButton))
                {
                    opened = f; status = "READY"; reason = ""; since = Ticks();
                    if (adoptedBoard) ProcessRecovery.OwnBoardAdoption(operation,
                        SoulRecoveryRequest.Hash(System.Text.Encoding.UTF8.GetBytes(adoptionAuthorityJson)), adoptionSourceSession, adoptionSourceOperation);
                }
                else if (expired) End("REJECTED", "daily_soul_open_timeout");
                return;
            }
            if (status == "CLOSING")
            {
                if (ClearUi(f) && f.Board == 0 &&
                    f.Used == harvested.Used && f.InventoryMatches(harvested.Inventory, null))
                {
                    uiCleanup = "clear";
                    End("COMPLETED", "daily_soul_harvest_and_activity_verified");
                    FreezeCloseReceipt();
                    if (adoptedBoard) ProcessRecovery.BoardHarvestProgress(operation, true, true);
                }
                else if (expired || f.SubUi != null && !f.SamePanel(opened))
                    End("UNKNOWN", "daily_soul_close_unverified");
                return;
            }
            if (!f.SamePanel(opened))
            { End(attempted ? "UNKNOWN" : "REJECTED", "daily_soul_panel_changed"); return; }
            if (f.Gate != "PASS")
            { End(attempted ? "UNKNOWN" : "REJECTED", "daily_soul_overlay_changed"); return; }
            if (!attempted)
            {
                if (!f.SameStep(opened) || expired) End("REJECTED", "daily_soul_start_baseline_changed");
                return;
            }
            if (harvestVerified)
            {
                if (!f.SameStep(harvested) || expired) End("UNKNOWN", "daily_soul_close_baseline_changed");
                return;
            }
            object client = f.Context.Client;
            if (!Monitor.TryEnter(client, 0))
            { if (expired) End("UNKNOWN", "daily_soul_callback_or_progress_timeout"); return; }
            try { ObserveRequests(f, expired); }
            finally { Monitor.Exit(client); }
        }

        private void ObserveRequests(Frame f, bool expired)
        {
            if (expired) { End("UNKNOWN", "daily_soul_callback_or_progress_timeout"); return; }
            IDictionary handlers = D.Read(f.Context.Client, "m_handlers") as IDictionary;
            if (handlers == null)
            { End("UNKNOWN", "daily_soul_handlers_unavailable"); return; }
            if (OtherSoulHandler(handlers, status == "PENDING" ? opcode : 0))
            { End("UNKNOWN", "daily_soul_foreign_request"); return; }

            if (status == "READY")
            {
                if (Failed(response) || response.Read().Calls != 1 || handlers.Contains((ushort)opcode) ||
                    f.Used != StartedUses || f.Stage != 1 || f.Mode != BoardMode || f.Board != startBoard ||
                    !f.Enabled || !f.InventoryMatches(origin.Inventory, null))
                    End("UNKNOWN", "daily_soul_owned_gather_changed");
                return;
            }
            if (Failed(response))
            { End("UNKNOWN", "daily_soul_response_callback_failed"); return; }
            DevelopmentCallbackWitness evidence = response.Read();
            if (!evidence.CallbackReturned)
            {
                if (!response.StillInstalled() || !object.ReferenceEquals(handlers[(ushort)opcode], responseHandler))
                    End("UNKNOWN", "daily_soul_response_callback_changed");
                return;
            }
            if (evidence.Calls != 1 || receipt == null || handlers.Contains((ushort)opcode))
            { End("UNKNOWN", "daily_soul_response_not_unique"); return; }
            if (opcode == Start)
            {
                if (!f.Enabled || f.Stage != 1 || f.Mode != selectedMode || f.Gathered || f.Board != receipt.Board ||
                    f.Used != receipt.Used || f.Coins != receipt.Coins || !f.InventoryMatches(origin.Inventory, null))
                { End("UNKNOWN", "daily_soul_start_readback_mismatch"); return; }
                startUsed = receipt.Used;
                coinsSpent = startCost;
                startVerified = true;
                startBoard = receipt.Board;
                // Keep this witness armed until the next request to detect a
                // duplicate dispatch after the start first appeared settled.
                status = "READY"; since = Ticks();
                return;
            }
            if (opcode == Grow)
            {
                if (f.Coins != receipt.Coins || !f.SamePremium(origin) || f.Used != StartedUses)
                { End("UNKNOWN", "daily_soul_coin_growth_readback_mismatch"); return; }
                // The stock coroutine may show several intermediate boards.
                // Do not offer another action until its final Enable and idle transport.
                if (!f.Enabled || !f.NetworkReady) return;
                if (receipt.AutoResult)
                {
                    // Ordinary growth can exhaust the board and carry its own
                    // result. FinishGather handles it without a 518 request.
                    if (f.Stage != 0 || f.Board != 0 || f.Mode != BoardMode) return;
                    if (!f.InventoryMatches(origin.Inventory, receipt.Award))
                    { End("UNKNOWN", "daily_soul_growth_result_readback_mismatch"); return; }
                    ConfirmGrowth();
                    autoHarvest = harvestVerified = true;
                    ConfirmHarvestEvidence(false);
                    harvested = f; status = "READY"; since = Ticks();
                    if (adoptedBoard) ProcessRecovery.BoardHarvestProgress(operation, true, false);
                    FreezeHarvestReceipt();
                    Detach();
                    return;
                }
                if (f.Stage != 1 || f.Mode != BoardMode || f.Board != receipt.Board || !f.InventoryMatches(origin.Inventory, null))
                { End("UNKNOWN", "daily_soul_coin_growth_readback_mismatch"); return; }
                ConfirmGrowth();
                startBoard = receipt.Board;
                status = "READY"; since = Ticks();
                return;
            }
            if (f.Board != 0 || f.Mode != 0 || f.Used != StartedUses || !f.InventoryMatches(origin.Inventory, receipt.Award))
            { End("UNKNOWN", "daily_soul_harvest_readback_mismatch"); return; }
            // 518 clears the board synchronously, but its coroutine and normal
            // FinishGather complete later. Preserve their stock behavior.
            if (!f.Enabled || f.Stage != 0 || !f.NetworkReady) return;
            harvestVerified = true; ConfirmHarvestEvidence(true); harvested = f; status = "READY"; since = Ticks();
            if (adoptedBoard) ProcessRecovery.BoardHarvestProgress(operation, true, false);
            FreezeHarvestReceipt();
            Detach();
        }

        private void ConfirmGrowth()
        {
            coinsSpent = checked(coinsSpent + admitted.GrowthCost);
            growthCount = checked(growthCount + 1);
            growthVerified = true;
            growPending = false;
            growthBeforeBoard = admitted.Board; growthAfterBoard = receipt.Board;
            growthSteps = receipt.Steps; growthEvidenceSequence = sequence;
        }

        private void ConfirmHarvestEvidence(bool manual)
        {
            harvestBeforeKnown = manual;
            if (manual) harvestBeforeBoard = admitted.Board;
            harvestColors = receipt.AwardColors; harvestNumbers = receipt.AwardNumbers;
            harvestEvidenceSequence = sequence;
        }

        private void FreezeHarvestReceipt()
        {
            // This runs only after the existing callback, progress, inventory,
            // resource and panel readback checks establish the harvest.
            // Serialize now so later captures, rounds and sequence changes
            // cannot alter the identity or witnesses attached to this result.
            harvestReceiptPrefix = "{\"schema\":1,\"character_id\":" + D.Q(origin.Role.ToString(CultureInfo.InvariantCulture)) +
                ",\"server_id\":" + origin.Server + ",\"cycle\":" + origin.Cycle +
                ",\"current\":" + Number(harvested.ActivityAvailable ? harvested.Current : -1) + ",\"required\":" + Number(origin.ActivityAvailable ? origin.Required : -1) +
                ",\"activity_required\":false,\"activity_observation\":" + ActivityObservationJson(harvested) +
                ",\"selected_mode\":" + selectedMode + ",\"free_only\":" + D.B(freeOnly) + ",\"round\":" + round +
                ",\"operation_id\":" + D.Q(operation) + ",\"harvest_sequence\":" + harvestEvidenceSequence +
                ",\"start_verified\":" + D.B(startVerified) + ",\"harvest_verified\":" + D.B(harvestVerified) +
                AdoptionFields() +
                ",\"progress_verified\":" + D.B(progressVerified) + ",\"mutation_attempted\":" + D.B(attempted) +
                ",\"counter_before\":" + Number(origin.ActivityAvailable ? origin.Current : -1) +
                ",\"counter_after\":" + Number(harvested.ActivityAvailable ? harvested.Current : -1) +
                ",\"start_cost\":" + startCost.ToString(CultureInfo.InvariantCulture) +
                ",\"coins_spent\":" + coinsSpent.ToString(CultureInfo.InvariantCulture) + ",\"growth_count\":" + growthCount +
                ",\"award\":" + AwardJson(receipt.Award) + ",\"auto_harvest\":" + D.B(autoHarvest) +
                ",\"resources_before\":" + ResourcesJson(origin) + ",\"resources_after\":" + ResourcesJson(harvested) +
                ",\"growth_before_board\":" + (growthEvidenceSequence != 0 ? BoardJson(growthBeforeBoard) : "null") +
                ",\"growth_after_board\":" + (growthEvidenceSequence != 0 ? BoardJson(growthAfterBoard) : "null") +
                ",\"growth_steps\":" + (growthEvidenceSequence != 0 ? BoardsJson(growthSteps) : "null") +
                ",\"growth_after_source\":" + (growthEvidenceSequence != 0 ? D.Q("decoded_response") : "null") +
                ",\"growth_evidence_sequence\":" + (growthEvidenceSequence != 0 ? growthEvidenceSequence.ToString(CultureInfo.InvariantCulture) : "null") +
                ",\"harvest_before_board\":" + (harvestBeforeKnown ? BoardJson(harvestBeforeBoard) : "null") +
                ",\"harvest_slot_awards\":" + SlotAwardsJson(harvestColors, harvestNumbers) +
                ",\"harvest_evidence_sequence\":" + harvestEvidenceSequence;
            historicalReceiptJson = harvestReceiptPrefix + ",\"phase\":\"HARVEST_VERIFIED\",\"sequence\":" + sequence +
                ",\"stage\":" + D.Q(stage) + ",\"status\":" + D.Q(status) +
                ",\"close_sequence\":null,\"close_verified\":false,\"ui_cleanup\":" + D.Q(uiCleanup) + "}";
        }

        private void FreezeCloseReceipt()
        {
            // Add only the close witness to the already frozen harvest data.
            // A later unavailable frame must never supply replacement identity.
            if (harvestReceiptPrefix == "") return;
            historicalReceiptJson = harvestReceiptPrefix + ",\"phase\":\"CLOSE_VERIFIED\",\"sequence\":" + sequence +
                ",\"stage\":" + D.Q(stage) + ",\"status\":" + D.Q(status) +
                ",\"close_sequence\":" + sequence + ",\"close_verified\":true,\"ui_cleanup\":\"clear\"}";
        }

        private static string ResourcesJson(Frame f)
        {
            return "{\"coins\":" + f.Coins.ToString(CultureInfo.InvariantCulture) + ",\"gold\":" + f.Gold +
                ",\"bound_gold\":" + f.BoundGold + ",\"gold_spent\":" + f.GoldSpent + ",\"exchanges\":" + f.Exchanges +
                ",\"blue_used\":" + f.BlueUsed + ",\"purple_used\":" + f.PurpleUsed + ",\"orange_used\":" + f.OrangeUsed +
                ",\"inventory\":" + AwardJson(f.Inventory) + ",\"board\":" + BoardJson(f.Board) + "}";
        }

        private static string ActivityObservationJson(Frame f)
        { return "{\"available\":" + D.B(f.ActivityAvailable) + ",\"reason\":" + D.Q(f.ActivityReason) + "}"; }

        private string AdoptionFields()
        {
            return ",\"adopted_board\":" + D.B(adoptedBoard) + ",\"adoption_sequence\":" + (adoptedBoard ? adoptionSequence.ToString(CultureInfo.InvariantCulture) : "null") +
                ",\"adoption_before_board\":" + (adoptedBoard ? BoardJson(adoptionBeforeBoard) : "null") +
                ",\"adoption_source_session_id\":" + D.Q(adoptionSourceSession) + ",\"adoption_source_operation_id\":" + D.Q(adoptionSourceOperation) +
                ",\"adoption_resolution_sha256\":" + D.Q(adoptionHash);
        }

        private static StockCall PrepareStock(Frame f, string action)
        {
            if (action == "open" || action == "adopt" || action == "next" || action == "close")
            {
                // SetSubUI is a no-op when this exact panel is already current.
                // Reuse its empty Gather page instead of toggling it closed.
                if ((action == "open" || action == "adopt" || action == "next") && f.Visible)
                    return new StockCall { Method = StockClose(), Args = new object[] { f.Panel } };
                bool opening = action == "open" || action == "adopt";
                MethodInfo method = opening
                    ? D.Method(Model.GameType("MainUI"), "_resource_soul", Type.EmptyTypes, true, typeof(void))
                    : StockClose();
                if (!method.IsPublic || method.DeclaringType != Model.GameType("MainUI"))
                    throw new ProbeFailure("daily_soul_stock_main_binding");
                return new StockCall { Method = method, Args = opening ? null : new object[] { null } };
            }
            Type panelType = f.Panel.GetType();
            bool start = action == "start", grow = action == "grow";
            MethodInfo send = D.Method(panelType, start ? "RequestGather" : grow ? "RequestGrow" : "Harvest",
                start ? new Type[] { f.Template.GetType(), typeof(bool) } : grow ? new Type[] { typeof(uint), typeof(uint) } : Type.EmptyTypes, false, typeof(void));
            MethodInfo response = D.Method(panelType, start ? "sc_refresh_gather_soul" : grow ? "sc_grow_soul" : "sc_harvest_soul", ResponseArgs, false, typeof(void));
            if (!send.IsPrivate || send.DeclaringType != panelType || !response.IsPrivate || response.DeclaringType != panelType)
                throw new ProbeFailure("daily_soul_stock_request_binding");
            return new StockCall { Method = send, Response = response, Receiver = f.Panel,
                Args = start ? new object[] { f.Template, f.Unlocked && f.Used < f.Free } : grow ? new object[] { 1u, 1u } : null };
        }

        private static MethodInfo StockClose()
        {
            MethodInfo close = D.Method(Model.GameType("MainUI"), "SetSubUI", new Type[] { Model.FirstType("__BaseUI") }, true, typeof(void));
            if (!close.IsPublic || close.DeclaringType != Model.GameType("MainUI"))
                throw new ProbeFailure("daily_soul_stock_close_binding");
            return close;
        }

        private bool PrepareRequest(Frame f, string action, out string why)
        {
            why = "";
            if (action != "start" && action != "harvest" && action != "grow") return true;
            why = "daily_soul_response_adapter_unavailable";
            if (installResponse == null) return false;
            bool firstRequest = action == "start" || adoptedBoard && (action == "harvest" || action == "grow") && !attempted;
            if (!firstRequest)
            {
                if (!(startVerified || adoptedBoard) || Failed(response) || response.Read().Calls != 1)
                { why = "daily_soul_start_evidence_changed"; return false; }
                DetachResponse();
                if (response != null) { why = "daily_soul_start_observer_busy"; return false; }
            }
            return true;
        }

        private bool InvokeRequest(Frame f, StockCall stock, out string why)
        {
            receipt = null;
            attempted = lastInvoked = true;
            if (opcode == Harvest) harvestInvoked = true;
            if (opcode == Grow) growPending = true;
            status = "PENDING";
            stock.Method.Invoke(stock.Receiver, stock.Args);
            why = "daily_soul_transport_changed_after_call";
            if (!TransportCurrent(f)) return false;
            int request = opcode;
            response = Bind(f, request, stock.Response, delegate(byte[] body)
            {
                RequireCallbackBoundary();
                if (QueuedNetwork(f.Context.Client, request)) throw new FormatException("daily_soul_duplicate_queued");
                DailySoulWire.Receipt decoded = DailySoulWire.ReadReceipt(request, body, selectedMode);
                if (request == Start && (decoded.Coins != origin.Coins - startCost || decoded.Gold != origin.Gold ||
                    decoded.Exchanges != origin.Exchanges ||
                    decoded.Used != origin.Used + 1 && !(startCost > 0 && decoded.Used == origin.Used)))
                    throw new FormatException("daily_soul_start_quote_mismatch");
                if (request == Grow && (decoded.Coins != admitted.Coins - admitted.GrowthCost ||
                    decoded.Gold != origin.Gold || decoded.Exchanges != origin.Exchanges))
                    throw new FormatException("daily_soul_coin_growth_quote_mismatch");
                receipt = decoded;
            }, out responseHandler, out why);
            if (response == null) return false;
            why = "daily_soul_response_observer_identity";
            return response.Opcode == request && response.OperationId == operation && response.Sequence == sequence && response.StillInstalled();
        }

        private DevelopmentCallbackCapture Bind(Frame f, int op, MethodInfo method, Action<byte[]> read, out object handler, out string why)
        {
            why = "daily_soul_stock_callback_unbound";
            IDictionary handlers = D.Read(f.Context.Client, "m_handlers") as IDictionary;
            handler = handlers == null ? null : handlers[(ushort)op];
            if (handler == null || handler.GetType() != Model.FirstType("game.net.tcp_base+Handler")) return null;
            Delegate callback = D.Read(handler, "handler") as Delegate;
            object target = f.Panel;
            object arg = D.Read(handler, "arg");
            float life = Field<float>(handler, "life");
            if (callback == null || callback.GetInvocationList().Length != 1 || !object.ReferenceEquals(callback.Target, target) ||
                callback.Method != method ||
                (op == Start ? !(arg is uint) || (uint)arg != (uint)selectedMode : arg != null) ||
                life != 10f) return null;
            return installResponse(f.Context.Client, handler, op, operation, sequence, callback, read, out why);
        }

        private void RequireCallbackBoundary()
        {
            D context = origin.Context;
            if (!context.SameOwnership(context) || DailyTransport.CurrentClient(context.Character) != context.Client ||
                !origin.Transport.Same(DailyTransport.Read(context.Client)) ||
                !RefreshCurrent(origin) ||
                !RootMatches(context) || Field<string>(context.Root, "LevelID") != origin.Room ||
                Field<uint>(context.Character, "SID") != origin.Server ||
                !object.ReferenceEquals(context.SubUi, opened.Panel) || StockSelection() != SoulPage || !D.Visible(opened.Panel) ||
                Field<DateTime>(opened.Panel, "visible_time") != opened.Shown ||
                !object.ReferenceEquals(D.Read(opened.Panel, "gather_bg"), opened.Background))
                throw new ProbeFailure("daily_soul_callback_boundary_changed");
        }

        private Frame Read(D ctx)
        {
            Frame f = new Frame { Context = ctx, Selected = selectedMode };
            if (ctx == null || ctx.Character == null || ctx.Client == null || !ctx.SameOwnership(ctx) ||
                DailyTransport.CurrentClient(ctx.Character) != ctx.Client) return f;
            if (!RootMatches(ctx)) return f;
            f.Room = Field<string>(ctx.Root, "LevelID");
            f.Role = Field<ulong>(ctx.Character, "id"); f.Server = Field<uint>(ctx.Character, "SID");
            if (f.Role == 0 || f.Server == 0) return f;
            uint day;
            f.Next = Field<uint>(ctx.Character, "next_resource_time");
            f.Boundary = refresh(ctx.Character, out f.Now, out day, out f.Cycle);
            if (f.Boundary != "") return f;
            if (f.Next != Field<uint>(ctx.Character, "next_resource_time"))
            { f.Boundary = "daily_soul_refresh_boundary_changed"; return f; }
            f.Transport = DailyTransport.Read(ctx.Client);
            if (f.Transport == null) { f.Boundary = "daily_soul_transport_unavailable"; return f; }
            try
            {
                f.ActivityAvailable = ReadTask(f);
                f.ActivityReason = f.ActivityAvailable ? "" : "daily_soul_activity_cache_unverified";
            }
            catch (Exception) { f.ActivityAvailable = false; f.ActivityReason = "daily_soul_activity_readback_unavailable"; }
            if (!f.ActivityAvailable)
            {
                f.Current = -1; f.Required = f.Refresh = 0;
                f.Acts = f.Rewards = f.Reward = f.Listed = f.Row = f.ActivityTemplate = null;
            }
            ReadResources(f);
            ReadPanel(f);
            f.Feature = FeatureGate(f);
            if (Monitor.TryEnter(ctx.Client, 0))
            {
                try
                {
                    IDictionary handlers = D.Read(ctx.Client, "m_handlers") as IDictionary;
                    f.NetworkReady = handlers != null &&
                        !OtherSoulHandler(handlers, 0) &&
                        !QueuedNetwork(ctx.Client, 0) && DevelopmentNetwork.Sample(ctx.Client).Idle;
                }
                finally { Monitor.Exit(ctx.Client); }
            }
            return f;
        }

        private static bool RootMatches(D ctx)
        {
            object load = D.Static(Model.FirstType("BaseAsset"), "lds");
            return load != null && object.ReferenceEquals(D.Read(load, "currmg"), ctx.Root);
        }

        private static bool OtherSoulHandler(IDictionary handlers, int ours)
        {
            return ours != Start && handlers.Contains((ushort)Start) ||
                ours != Harvest && handlers.Contains((ushort)Harvest) || ours != Grow && handlers.Contains((ushort)Grow) ||
                handlers.Contains((ushort)522) || handlers.Contains((ushort)523);
        }

        private static bool ReadTask(Frame f)
        {
            object character = f.Context.Character;
            IDictionary acts = D.Read(character, "acts") as IDictionary, rewards = D.Read(character, "act_rewards") as IDictionary;
            if (acts == null || rewards == null || acts.Count > 255 || rewards.Count > 6) return false;
            object reward = rewards[(uint)CategoryId];
            if (reward == null || reward.GetType() != Model.FirstType("Database.Activity_Reward") ||
                Field<byte>(reward, "idact_reward") != CategoryId) return false;
            IDictionary listed = D.Read(reward, "acts") as IDictionary;
            object row = acts[(uint)TaskId];
            if (listed == null || listed.Count > 255 || row == null || !object.ReferenceEquals(listed[(uint)TaskId], row) ||
                row.GetType() != Model.FirstType("Database.Activity") || Field<byte>(row, "idactivity") != TaskId) return false;
            object template = D.Read(row, "temp");
            if (template == null || !object.ReferenceEquals(D.Template("activity", TaskId), template) ||
                Field<byte>(template, "id") != TaskId || Field<byte>(template, "idreward") != CategoryId ||
                !Field<bool>(template, "active")) return false;
            int current = Field<byte>(row, "condition_param"), required = Field<int>(template, "cond_param");
            if (required < 1 || required > Byte.MaxValue || current > required) return false;
            f.Current = current; f.Required = required;
            f.Refresh = Field<int>(row, "refresh_time");
            f.Acts = acts; f.Rewards = rewards; f.Reward = reward; f.Listed = listed; f.Row = row; f.ActivityTemplate = template;
            return true;
        }

        private void ReadResources(Frame f)
        {
            object role = f.Context.Character;
            f.Coins = D.Money(role); f.Gold = Field<int>(role, "gold"); f.BoundGold = Field<int>(role, "bind_gold");
            f.GoldSpent = Field<uint>(role, "gold_consume");
            f.Exchanges = Field<uint>(D.Read(role, "exchange"), "today_times");
            f.BlueUsed = Field<byte>(role, "generalsoul_blue_times"); f.PurpleUsed = Field<byte>(role, "generalsoul_purple_times");
            f.OrangeUsed = Field<byte>(role, "generalsoul_orange_times");
            f.Inventory = new int[] { Field<int>(role, "generalsoul_blue"), Field<int>(role, "generalsoul_purple"),
                Field<int>(role, "generalsoul_orange"), Field<int>(role, "generalsoul_red") };
            f.SoulData = D.Read(role, "gather_souls");
            if (f.SoulData == null || f.SoulData.GetType() != Model.FirstType("Database.GatherSoul"))
                throw new ProbeFailure("daily_soul_board_type");
            for (int i = 0; i < 8; i++)
            {
                int color = Field<byte>(f.SoulData, "soul" + i + "_color"), level = Field<byte>(f.SoulData, "soul" + i + "_level");
                f.Board |= DailySoulWire.Slot(i, color, level);
            }
            f.Quotes = new ColorQuote[] { ReadColor(role, 1, f.BlueUsed), ReadColor(role, 2, f.PurpleUsed) };
            ColorQuote chosen = f.Quotes[selectedMode - 1];
            f.Template = chosen.Template; f.Used = chosen.Used; f.Free = chosen.Free; f.Price = chosen.Price;
            f.Unlocked = chosen.Known && chosen.Unlocked; f.StartAvailable = chosen.Known && chosen.StartAvailable; f.GrowthRate = chosen.Rate;
            f.GrowTemplate = D.Template("general_soul_grow_mode", 1);
            if (f.GrowTemplate != null && f.GrowTemplate.GetType().FullName == "Database._general_soul_grow_mode" &&
                Field<sbyte>(f.GrowTemplate, "grow_mode") == 1 && !Field<bool>(f.GrowTemplate, "isgold"))
            {
                int cost = Field<int>(f.GrowTemplate, "cost");
                f.GrowthCost = checked((long)cost * f.GrowthRate);
                f.CoinGrowth = cost > 0 && f.GrowthRate == 1;
                f.GrowthVip = VipLevel(role) >= Field<sbyte>(f.GrowTemplate, "vip_require");
            }
            f.ResourcesRead = true;
        }

        private static bool QuoteUnlocked(object role, object quote, int mode)
        {
            return mode == 1 || Field<short>(role, "level") >= Field<uint>(quote, "require_level") ||
                VipLevel(role) >= Field<byte>(quote, "nolimit_viplevel");
        }

        private static ColorQuote ReadColor(object role, int mode, int used)
        {
            ColorQuote q = new ColorQuote { Mode = mode, Used = used, Template = D.Template("general_soul_start_mode", (uint)mode) };
            if (q.Template == null || q.Template.GetType().FullName != "Database._general_soul_start_mode" ||
                Field<byte>(q.Template, "type") != mode || Field<uint>(q.Template, "gather_rate") != 1) return q;
            q.Free = Field<int>(q.Template, "free_times");
            if (q.Free < 0 || q.Free > Byte.MaxValue) return q;
            q.Price = Field<uint>(q.Template, "price"); q.Rate = Field<uint>(q.Template, "gather_rate");
            q.RequiredLevel = Field<uint>(q.Template, "require_level"); q.RequiredVip = Field<byte>(q.Template, "nolimit_viplevel");
            q.Unlocked = QuoteUnlocked(role, q.Template, mode); q.StartAvailable = true; q.Known = true;
            q.Reason = q.Unlocked ? "" : "daily_soul_color_locked";
            return q;
        }

        private static void ReadPanel(Frame f)
        {
            f.Panel = f.Context.Find("GeneralSoulGatherUI");
            f.SubUi = f.Context.SubUi; f.Selection = StockSelection(); f.Visible = D.Visible(f.Panel); f.Loading = D.Loading(f.Panel);
            f.Shown = Field<DateTime>(f.Panel, "visible_time");
            f.StockPage = Field<string>(f.Panel, "page") == "gather" && Field<string>(f.Panel, "subwindow") == "" &&
                !Field<bool>(f.Panel, "show_exchange_window") && D.Read(f.Panel, "OnClose") == null &&
                !D.Bool(D.Static(Model.FirstType("PopupUI"), "popup"));
            f.Gate = f.Context.Panels(f.Panel);
            f.Background = D.Read(f.Panel, "gather_bg") as GameObject;
            f.Mode = checked((int)Field<uint>(f.Panel, "m_curType"));
            if (!f.Visible) return;
            f.Enabled = D.Enabled(f.Panel);
            f.AllFull = Field<bool>(f.Panel, "isAllSoulFull");
            f.Stage = D.Int(D.Read(f.Panel, "currentGatherSoulStatus")); f.Mode = checked((int)Field<uint>(f.Panel, "m_curType"));
            f.Gathered = Field<bool>(f.Panel, "gathered");
            IList modes = D.Read(f.Panel, "start_modes") as IList;
            f.FreeButton = modes != null && modes.Count == 9 && object.ReferenceEquals(modes[f.Selected - 1], f.Template);
        }

        private static string StockSelection() { return (string)D.Static(Model.FirstType("__BaseUI"), "subui"); }

        private static byte VipLevel(object character)
        { return (byte)D.Method(character.GetType(), "get_VIPLEVEL", Type.EmptyTypes, false, typeof(byte)).Invoke(character, null); }

        private string FeatureGate(Frame f)
        {
            if (f.Quotes == null || f.Board == 0 && !adoptedBoard && !f.Quotes[f.Selected - 1].Known) return "daily_soul_free_mode_unverified";
            object role = f.Context.Character;
            if (!(bool)D.Method(role.GetType(), "checkUI", new Type[] { typeof(string) }, false, typeof(bool))
                .Invoke(role, new object[] { "resource_soul" })) return "daily_soul_feature_locked";
            // FinishGather always invokes the tutorial hook. Keep 810 outside
            // the daily action by verifying every template the hook can visit.
            IList quests = D.Read(role, "newbiequests") as IList;
            if (quests == null || quests.Count > 255) return "daily_soul_teaching_state_unverified";
            foreach (object quest in quests)
            {
                if (quest == null || quest.GetType() != Model.FirstType("Database.NewbieQuest")) return "daily_soul_teaching_state_unverified";
                ushort id = Field<ushort>(quest, "idnewbie_quest");
                object template = D.Template("newbie_quest", id);
                if (template == null || Field<short>(template, "idnewbie_quest") != id) return "daily_soul_teaching_state_unverified";
                if (Field<uint>(template, "newbie_quest_type") == TaskId && Field<sbyte>(quest, "isfinish") != 1)
                    return "daily_soul_teaching_task_pending";
            }
            return "";
        }

        private static bool QueuedNetwork(object client, int only)
        {
            IList packets = D.Read(client, "messages") as IList;
            if (packets == null || packets.Count > 64) return true;
            foreach (object item in packets)
            {
                ushort op = Field<ushort>(item, "op");
                if (only != 0 ? op == only : op == 522 || op == 523 || DevelopmentNetwork.Tracked(op)) return true;
            }
            return false;
        }

        private static T Field<T>(object owner, string name)
        {
            object value = D.Read(owner, name);
            if (!(value is T)) throw new ProbeFailure("daily_soul_field:" + owner.GetType().FullName + "." + name +
                ":expected=" + typeof(T).FullName + ":actual=" + (value == null ? "null" : value.GetType().FullName));
            return (T)value;
        }

        private static bool Failed(DevelopmentCallbackCapture capture)
        {
            if (capture == null) return true;
            DevelopmentCallbackWitness w = capture.Read();
            return w.Error != null || w.NullResponse || w.OriginalThrew || !w.Armed;
        }

        private bool Reject(string why, out string output)
        {
            output = why;
            End(attempted ? "UNKNOWN" : "REJECTED", why);
            AdvanceRevision();
            return false;
        }

        private bool UnknownAfterCall(string why, out string output)
        {
            output = why;
            End("UNKNOWN", why);
            AdvanceRevision();
            return false;
        }

        private void End(string result, string why)
        {
            status = result; reason = why; queuedAction = "";
            if (openInvoked && uiCleanup == "none" && result != "COMPLETED")
            {
                uiCleanup = opened == null ? "unknown" : "waiting";
                cleanupSince = Ticks();
            }
            if (!NeedsUiCleanup || !attempted || (startVerified || adoptedBoard) && !harvestInvoked && !growPending || harvestVerified) DetachResponse();
            DetachActivity();
        }

        private void DetachResponse()
        {
            if (response == null || response.DisarmAndRestore().EndsWith("forward_only", StringComparison.Ordinal)) return;
            response = null; responseHandler = null;
        }

        private void Detach()
        {
            DetachResponse();
            DetachActivity();
        }

        private void DetachActivity()
        {
            if (activity == null || activity.DisarmAndRestore().EndsWith("forward_only", StringComparison.Ordinal)) return;
            activity = null;
        }

        private void AdvanceRevision()
        {
            if (revision == 999999999) throw new ProbeFailure("daily_soul_revision_exhausted");
            revision++;
        }

        private bool Expired() { return (status == "OPENING" || status == "PENDING" || status == "CLOSING") && unchecked((uint)(Ticks() - since)) >= Deadline; }
        private bool CleanupExpired() { return unchecked((uint)(Ticks() - cleanupSince)) >= CleanupDeadline; }

        internal string Json()
        {
            bool observationAvailable = frame.Boundary == "" && frame.ResourcesRead;
            string observationReason = observationAvailable ? "" : frame.Boundary != "" ? frame.Boundary : "daily_soul_resources_unverified";
            string unavailable = Action != "" ? "" : frame.Boundary != "" ? frame.Boundary : frame.Feature != "" ? frame.Feature :
                frame.Gate != "PASS" ? frame.Gate : frame.Board != 0 && (status == "IDLE" || status == "COMPLETED" || status == "REJECTED") ?
                    adoptionReason != "" ? adoptionReason : "daily_soul_adoption_awaiting_owned_panel" :
                !CanStart(frame, status == "IDLE" || status == "COMPLETED" || status == "REJECTED" ? false : freeOnly) &&
                    (!attempted || status == "COMPLETED") ? "daily_soul_coin_start_unavailable" :
                !frame.NetworkReady ? "daily_soul_network_waiting" : "daily_soul_awaiting_owned_panel_or_receipt";
            return "{\"schema\":1,\"revision\":" + revision + ",\"category_id\":" + CategoryId + ",\"task_id\":" + TaskId +
                ",\"observation\":{\"available\":" + D.B(observationAvailable) + ",\"reason\":" + D.Q(observationReason) + "}" +
                ",\"activity_observation\":" + ActivityObservationJson(frame) +
                ",\"historical_receipt\":" + historicalReceiptJson +
                ",\"adoption_authority\":" + adoptionAuthorityJson +
                ",\"character_id\":" + D.Q(frame.Role.ToString(CultureInfo.InvariantCulture)) + ",\"cycle\":" + frame.Cycle +
                ",\"action\":" + D.Q(Action) + ",\"reason\":" + D.Q(unavailable) +
                ",\"ui_cleanup\":" + D.Q(uiCleanup) +
                ",\"ui\":{\"visible\":" + D.B(frame.Visible) + ",\"enabled\":" + D.B(frame.Enabled) +
                ",\"loading\":" + D.B(frame.Loading) + ",\"background_ready\":" + D.B(frame.Background != null) +
                ",\"stage\":" + frame.Stage + ",\"mode\":" + frame.Mode + ",\"free_button\":" + D.B(frame.FreeButton) +
                ",\"gate\":" + D.Q(frame.Gate) + ",\"selection\":" + D.Q(frame.Selection) + "}" +
                ",\"current\":" + Number(frame.Current) + ",\"required\":" + Number(frame.Required == 0 ? -1 : frame.Required) +
                ",\"board\":" + BoardJson(frame.Board) +
                ",\"coins\":" + (frame.ResourcesRead ? frame.Coins.ToString(CultureInfo.InvariantCulture) : "null") +
                ",\"start_cost\":" + (frame.ResourcesRead ? StartCost(frame).ToString(CultureInfo.InvariantCulture) : "null") +
                ",\"growth_cost\":" + (frame.ResourcesRead && frame.CoinGrowth ? frame.GrowthCost.ToString(CultureInfo.InvariantCulture) : "null") +
                ",\"selected_mode\":" + selectedMode + ",\"free_only\":" + D.B(freeOnly) + ",\"round\":" + round +
                ",\"quotes\":{\"blue\":" + QuoteJson(0) + ",\"purple\":" + QuoteJson(1) + "}" +
                ",\"growth_count\":" + growthCount +
                ",\"free_remaining\":" + Math.Max(0, frame.Free - frame.Used) + ",\"operation\":{\"operation_id\":" + D.Q(operation) +
                ",\"sequence\":" + sequence + ",\"stage\":" + D.Q(stage) + ",\"status\":" + D.Q(status) + ",\"reason\":" + D.Q(reason) +
                ",\"invoked\":" + D.B(lastInvoked) + ",\"mutation_attempted\":" + D.B(attempted) + ",\"opcode\":" + opcode +
                ",\"activity_required\":false" +
                ",\"start_verified\":" + D.B(startVerified) + ",\"harvest_verified\":" + D.B(harvestVerified) +
                AdoptionFields() +
                ",\"close_verified\":" + D.B(status == "COMPLETED" && closeInvoked && uiCleanup == "clear") +
                ",\"growth_verified\":" + D.B(growthVerified) + ",\"coins_spent\":" + coinsSpent.ToString(CultureInfo.InvariantCulture) +
                ",\"mode\":" + selectedMode + ",\"free_only\":" + D.B(freeOnly) + ",\"round\":" + round +
                ",\"start_cost\":" + startCost.ToString(CultureInfo.InvariantCulture) +
                ",\"award\":" + AwardJson(receipt == null ? null : receipt.Award) +
                ",\"auto_harvest\":" + D.B(autoHarvest) +
                ",\"growth_before_board\":" + (growthEvidenceSequence != 0 && growthEvidenceSequence == sequence ? BoardJson(growthBeforeBoard) : "null") +
                ",\"growth_after_board\":" + (growthEvidenceSequence != 0 && growthEvidenceSequence == sequence ? BoardJson(growthAfterBoard) : "null") +
                ",\"growth_steps\":" + (growthEvidenceSequence != 0 && growthEvidenceSequence == sequence ? BoardsJson(growthSteps) : "null") +
                ",\"growth_after_source\":" + (growthEvidenceSequence != 0 && growthEvidenceSequence == sequence ? D.Q("decoded_response") : "null") +
                ",\"growth_evidence_sequence\":" + (growthEvidenceSequence != 0 && growthEvidenceSequence == sequence ? growthEvidenceSequence.ToString(CultureInfo.InvariantCulture) : "null") +
                ",\"harvest_before_board\":" + (harvestEvidenceSequence != 0 && harvestEvidenceSequence == sequence && harvestBeforeKnown ? BoardJson(harvestBeforeBoard) : "null") +
                ",\"harvest_slot_awards\":" + (harvestEvidenceSequence != 0 && harvestEvidenceSequence == sequence ? SlotAwardsJson(harvestColors, harvestNumbers) : "null") +
                ",\"harvest_evidence_sequence\":" + (harvestEvidenceSequence != 0 && harvestEvidenceSequence == sequence ? harvestEvidenceSequence.ToString(CultureInfo.InvariantCulture) : "null") +
                ",\"counter_before\":" + Number(origin == null || !origin.ActivityAvailable ? -1 : origin.Current) +
                ",\"counter_after\":" + Number(harvestVerified && harvested != null && harvested.ActivityAvailable ? harvested.Current : -1) + "}}";
        }

        private string QuoteJson(int index)
        {
            ColorQuote q = frame.Quotes == null ? null : frame.Quotes[index];
            bool known = frame.ResourcesRead && q != null && q.Known;
            return "{\"known\":" + D.B(known) + ",\"unlocked\":" + (known ? D.B(q.Unlocked) : "null") +
                ",\"free_unlocked\":" + (known ? D.B(q.Unlocked) : "null") + ",\"start_available\":" + (known ? D.B(q.StartAvailable) : "null") +
                ",\"reason\":" + D.Q(known ? q.Reason : "daily_soul_mode_unverified") +
                ",\"used_today\":" + (known ? q.Used.ToString(CultureInfo.InvariantCulture) : "null") +
                ",\"free_remaining\":" + (known ? Math.Max(0, q.Free - q.Used).ToString(CultureInfo.InvariantCulture) : "null") +
                ",\"start_cost\":" + (known ? (q.Unlocked && q.Used < q.Free ? 0L : q.Price).ToString(CultureInfo.InvariantCulture) : "null") +
                ",\"paid_start_cost\":" + (known ? q.Price.ToString(CultureInfo.InvariantCulture) : "null") + "}";
        }

        private static string AwardJson(int[] award)
        {
            return award == null ? "null" : "[" + award[0] + "," + award[1] + "," + award[2] + "," + award[3] + "]";
        }

        private static string BoardsJson(List<long> boards)
        {
            if (boards == null) return "null";
            string result = "[";
            for (int i = 0; i < boards.Count; i++) result += (i == 0 ? "" : ",") + BoardJson(boards[i]);
            return result + "]";
        }

        private static string SlotAwardsJson(int[] colors, int[] numbers)
        {
            if (colors == null || numbers == null) return "null";
            string result = "[";
            for (int i = 0; i < 8; i++)
                result += (i == 0 ? "" : ",") + "{\"color\":" + colors[i] + ",\"number\":" + numbers[i] + "}";
            return result + "]";
        }

        private static string Number(int value) { return value < 0 ? "null" : value.ToString(CultureInfo.InvariantCulture); }

        private static string BoardJson(long board)
        {
            string result = "[";
            for (int i = 0; i < 8; i++)
            {
                int slot = (int)((board >> (i * 6)) & 63);
                result += (i == 0 ? "" : ",") + "{\"color\":" + (slot >> 4) + ",\"level\":" + (slot & 15) + "}";
            }
            return result + "]";
        }
    }

    // Fixed projections of the pinned pbdata contracts. Forwarding still uses
    // the original stream and game delegate. No write uses these projections.
    internal sealed class DailySoulWire
    {
        internal sealed class Receipt
        {
            internal long Board, Coins;
            internal int Gold, Used, Mode;
            internal uint Exchanges;
            internal int[] Award;
            internal int[] AwardColors, AwardNumbers;
            internal List<long> Steps;
            internal bool AutoResult;
        }

        internal sealed class Progress { internal int Count, Refresh; }

        internal static Receipt ReadReceipt(int opcode, byte[] body, int expectedMode = 1)
        {
            if (opcode == 519) return ReadGrowth(body);
            DailySoulWire reader = new DailySoulWire(body, 4096);
            int field, wire, seen = 0;
            Receipt receipt = new Receipt();
            while (reader.Tag(out field, out wire))
            {
                int max = opcode == 517 ? 5 : opcode == 518 ? 1 : 0;
                if (field > max || (seen & (1 << field)) != 0) throw new FormatException("daily_soul_receipt_field");
                seen |= 1 << field;
                if (field == 1)
                {
                    DailySoulWire result = reader.Nested(wire);
                    int[] colors, values;
                    result.Pair(out colors, out values);
                    if (opcode == 517)
                    {
                        bool occupied = false;
                        for (int i = 0; i < 8; i++)
                        {
                            receipt.Board |= Slot(i, colors[i], values[i]);
                            if (colors[i] > 0 && values[i] > 0) occupied = true;
                        }
                        if (!occupied) throw new FormatException("daily_soul_empty_start");
                    }
                    else
                    {
                        receipt.Award = new int[4];
                        for (int i = 0; i < 8; i++)
                        {
                            if (colors[i] < 0 || colors[i] > 4 || values[i] < 0 || values[i] > Byte.MaxValue ||
                                colors[i] == 0 && values[i] != 0) throw new FormatException("daily_soul_award_slot");
                            if (colors[i] != 0) receipt.Award[colors[i] - 1] += values[i];
                        }
                        receipt.AwardColors = colors; receipt.AwardNumbers = values;
                    }
                }
                else if (field == 2)
                {
                    ulong[] exchange = reader.Nested(wire).Scalars(4, 30);
                    receipt.Gold = checked((int)ZigZag(exchange[2]));
                    receipt.Coins = ZigZag(exchange[4]);
                    receipt.Exchanges = checked((uint)exchange[3]);
                    // Legacy money is also a required ZigZag int, but only
                    // the money64 field is used by RootUI.HandleExchangeParam.
                    if (exchange[1] > UInt32.MaxValue) throw new FormatException("daily_soul_legacy_money");
                }
                else
                {
                    uint n = reader.Scalar(wire);
                    if (field == 4) receipt.Used = checked((byte)n);
                    else if (field == 3) { receipt.Mode = checked((int)n); if (receipt.Mode != expectedMode) throw new FormatException("daily_soul_receipt_mode"); }
                    else if (n != 1) throw new FormatException("daily_soul_start_rejected_or_other_mode");
                }
            }
            if (opcode == 517 && seen == 62 || opcode == 518 && seen == 2) return receipt;
            throw new FormatException("daily_soul_receipt_missing_field");
        }

        private static Receipt ReadGrowth(byte[] body)
        {
            DailySoulWire reader = new DailySoulWire(body, 16384);
            Receipt receipt = new Receipt();
            int field, wire, seen = 0, steps = 0;
            bool occupied = false;
            while (reader.Tag(out field, out wire))
            {
                if (field < 1 || field > 4 || field != 1 && (seen & (1 << field)) != 0)
                    throw new FormatException("daily_soul_growth_field");
                seen |= 1 << field;
                if (field == 1)
                {
                    if (++steps > 64) throw new FormatException("daily_soul_growth_steps_bound");
                    int[] colors, levels;
                    reader.Nested(wire).Pair(out colors, out levels);
                    receipt.Board = 0; occupied = false;
                    for (int i = 0; i < 8; i++)
                    {
                        receipt.Board |= Slot(i, colors[i], levels[i]);
                        occupied |= colors[i] != 0 && levels[i] != 0;
                    }
                    if (receipt.Steps == null) receipt.Steps = new List<long>();
                    receipt.Steps.Add(receipt.Board);
                }
                else if (field == 2)
                {
                    ulong[] exchange = reader.Nested(wire).Scalars(4, 30);
                    receipt.Gold = checked((int)ZigZag(exchange[2]));
                    receipt.Coins = ZigZag(exchange[4]);
                    receipt.Exchanges = checked((uint)exchange[3]);
                    if (exchange[1] > UInt32.MaxValue) throw new FormatException("daily_soul_legacy_money");
                }
                else if (field == 3)
                {
                    int[] colors, numbers;
                    reader.Nested(wire).Pair(out colors, out numbers);
                    receipt.Award = new int[4];
                    for (int i = 0; i < 8; i++)
                    {
                        if (colors[i] < 0 || colors[i] > 4 || numbers[i] < 0 || numbers[i] > Byte.MaxValue ||
                            colors[i] == 0 && numbers[i] != 0) throw new FormatException("daily_soul_award_slot");
                        if (colors[i] != 0) receipt.Award[colors[i] - 1] += numbers[i];
                    }
                    receipt.AwardColors = colors; receipt.AwardNumbers = numbers;
                }
                else if (reader.Scalar(wire) != 1)
                    throw new FormatException("daily_soul_growth_premium_mode");
            }
            if ((seen & 22) != 22 || steps == 0 || !occupied && receipt.Award == null)
                throw new FormatException("daily_soul_growth_missing_field");
            receipt.AutoResult = !occupied && receipt.Award != null;
            return receipt;
        }

        internal static long Slot(int slot, int color, int level)
        {
            // ResetSoul indexes 3 stock palette arrays and level 0..10.
            if (color < 0 || color > 3 || level < 0 || level > 10)
                throw new FormatException("daily_soul_board_slot");
            return (long)(color * 16 + level) << (slot * 6);
        }

        internal static Progress ReadProgress(byte[] body)
        {
            DailySoulWire reader = new DailySoulWire(body, 65536);
            int field, wire, rows = 0;
            Progress found = null;
            while (reader.Tag(out field, out wire))
            {
                if (field != 1) { reader.Skip(wire); continue; }
                if (++rows > 255) throw new FormatException("daily_soul_activity_bound");
                DailySoulWire row = reader.Nested(wire);
                int seen = 0;
                uint[] values = new uint[4];
                while (row.Tag(out field, out wire))
                {
                    if (field > 3) { row.Skip(wire); continue; }
                    if ((seen & (1 << field)) != 0) throw new FormatException("daily_soul_activity_field");
                    seen |= 1 << field; values[field] = row.Scalar(wire);
                }
                if (values[1] != DailySoulModel.TaskId) continue;
                if (found != null || values[2] > Byte.MaxValue)
                    throw new FormatException("daily_soul_activity_target");
                found = new Progress { Count = (int)values[2], Refresh = (int)ZigZag(values[3]) };
            }
            return found;
        }

        private readonly byte[] bytes;
        private readonly int end;
        private int position;

        private DailySoulWire(byte[] data, int max)
        {
            if (data == null || data.Length > max) throw new FormatException("daily_soul_packet_bound");
            bytes = data; end = data.Length;
        }

        private DailySoulWire(byte[] data, int begin, int limit) { bytes = data; position = begin; end = limit; }

        private void Pair(out int[] first, out int[] second)
        {
            first = new int[8]; second = new int[8];
            int a = 0, b = 0, field, wire;
            while (Tag(out field, out wire))
            {
                if (field == 1) Vector(wire, first, ref a);
                else if (field == 2) Vector(wire, second, ref b);
                else throw new FormatException("daily_soul_vector_field");
            }
            if (a != 8 || b != 8) throw new FormatException("daily_soul_vector_bound");
        }

        private void Vector(int wire, int[] result, ref int count)
        {
            if (wire == 0)
            {
                if (count == result.Length) throw new FormatException("daily_soul_vector_bound");
                result[count++] = checked((int)Varint());
                return;
            }
            DailySoulWire packed = Nested(wire);
            while (packed.position < packed.end) packed.Vector(0, result, ref count);
        }

        private ulong[] Scalars(int max, int required)
        {
            int field, wire, seen = 0;
            ulong[] values = new ulong[max + 1];
            while (Tag(out field, out wire))
            {
                if (field > max || wire != 0 || (seen & (1 << field)) != 0)
                    throw new FormatException("daily_soul_scalar_field");
                seen |= 1 << field; values[field] = Varint();
            }
            if (seen != required) throw new FormatException("daily_soul_scalar_missing_field");
            return values;
        }

        private static long ZigZag(ulong value) { return (long)(value >> 1) ^ -(long)(value & 1); }

        private bool Tag(out int field, out int wire)
        {
            field = wire = 0;
            if (position == end) return false;
            uint tag = checked((uint)Varint());
            field = (int)(tag >> 3); wire = (int)(tag & 7);
            if (field == 0) throw new FormatException("daily_soul_tag");
            return true;
        }

        private uint Scalar(int wire)
        {
            if (wire != 0) throw new FormatException("daily_soul_scalar_wire");
            return checked((uint)Varint());
        }

        private DailySoulWire Nested(int wire)
        {
            if (wire != 2) throw new FormatException("daily_soul_nested_wire");
            int start = Advance(checked((int)Varint()));
            return new DailySoulWire(bytes, start, position);
        }

        private void Skip(int wire)
        {
            if (wire == 0) Varint();
            else if (wire == 1) Advance(8);
            else if (wire == 2) Advance(checked((int)Varint()));
            else if (wire == 5) Advance(4);
            else throw new FormatException("daily_soul_wire");
        }

        private int Advance(int length)
        {
            if (length < 0 || length > end - position) throw new FormatException("daily_soul_packet_length");
            int start = position; position += length; return start;
        }

        private ulong Varint()
        {
            ulong value = 0;
            for (int shift = 0; shift <= 63 && position < end; shift += 7)
            {
                byte b = bytes[position++];
                if (shift == 63 && b > 1) break;
                value |= (ulong)(b & 127) << shift;
                if ((b & 128) == 0) return value;
            }
            throw new FormatException("daily_soul_varint");
        }
    }
}
