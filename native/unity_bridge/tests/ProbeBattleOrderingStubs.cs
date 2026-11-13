using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using JiangShen.AssistantRuntime;

// Derived from LifecycleTests.cs fixture-only stubs. These synthetic models
// intentionally expose calls to production Probe; they are not game objects.

// An independent desktop executable. These are owned synthetic objects, not
// Unity/game assemblies and never a reference to an installed game process.
#if !JSB_ARENA_PROBE_TEST
namespace UnityEngine
{
    public enum HideFlags { HideInHierarchy = 1 }
    public class Object
    {
        public int GetInstanceID() { return 1; }
        public static void DontDestroyOnLoad(Object value) { }
        public static void Destroy(Object value)
        {
            GameObject game = value as GameObject;
            if (game != null) game.PendingDestroy = true;
        }
    }
    public sealed class Transform
    {
#if JSB_MINGSHI_PROBE_TEST
        public Transform parent;
        public Transform root { get { return parent == null ? this : parent.root; } }
#else
        public Transform root { get { return this; } }
#endif
    }
    public class Component : Object
    {
        public GameObject gameObject = new GameObject();
        public Transform transform { get { return gameObject.transform; } }
    }
    public class Behaviour : Component { public bool enabled = true; }
    public class MonoBehaviour : Behaviour { }
    public static class Time { public static int frameCount; }
    public sealed class Event { public static Event current = new Event(); }
    public static class GUI { public static bool enabled = true; }
    public sealed class GameObject : Object
    {
        public static GameObject LastHelper;
        public bool PendingDestroy;
        public HideFlags hideFlags;
        public Probe Probe;
        public readonly Transform transform = new Transform();
        public bool activeInHierarchy = true;
        public Component Panel;
#if JSB_MINGSHI_PROBE_TEST
        public static readonly System.Collections.Hashtable Paths = new System.Collections.Hashtable();
        public static GameObject Find(string path) { return Paths[path] as GameObject; }
        public Collider collider;
        public Component Component;
        public Component GetComponent(string name) { return Component != null && Component.GetType().Name == name ? Component : null; }
#elif JSB_WOOD_PROBE_TEST
        public static readonly System.Collections.Hashtable Paths = new System.Collections.Hashtable();
        public readonly System.Collections.Generic.List<Component> Components = new System.Collections.Generic.List<Component>();
        public static GameObject Find(string path) { return Paths[path] as GameObject; }
        public Component[] GetComponents(Type type) { return Components.FindAll(delegate(Component item) { return type.IsInstanceOfType(item); }).ToArray(); }
        public Component GetComponent(string name) { return Components.Find(delegate(Component item) { return item.GetType().Name == name; }); }
#else
        public Component GetComponent(string name) { return Panel != null && Panel.GetType().Name == name ? Panel : null; }
#endif
        public GameObject(string name = "") { if (name.StartsWith("JSAssistantRuntime_")) LastHelper = this; }
        public Component GetComponent(Type type) { return Panel != null && type.IsInstanceOfType(Panel) ? Panel : null; }
        public T AddComponent<T>() where T : Component, new()
        {
            T value = new T(); value.gameObject = this; Probe = value as Probe; return value;
        }
    }
}
internal sealed class BattleUI : UnityEngine.Component { }
namespace JiangShen.AssistantRuntime
{
    internal enum BattleMode { Campaign, NormalChallenge, Training }
    internal delegate bool BattleSettlementAdmission();
    internal delegate void BattleSettlementEvent(string name, string fields);
    internal sealed class BattlePresentationOwnership
    {
        internal BattlePresentationOwnership(BattleMode mode, string operation, UnityEngine.Component source, object target, string id, BattleSettlementAdmission verify) { }
    }
    // Simulates the settlement contract only; the production settlement's
    // handler and identity guarantees are tested independently elsewhere.
    internal sealed class BattleSettlement
    {
        internal static bool NativePending, Invalid;
        internal static int Observes, Advances, UnownedAdvances, Validates;
        private bool blocked;
        internal BattleSettlement(BattleSettlementEvent emit, BattleSettlementAdmission admit) { }
        internal bool BlocksGuiInvocation { get { return blocked; } }
        internal bool NeedsAdvance { get { return false; } }
        internal void ValidateOwnedAttempt() { Validates++; if (blocked && Invalid) throw new ProbeFailure("fixture_handler_unknown"); }
        internal void ObserveOwned(UnityEngine.Component battle, BattlePresentationOwnership owner) { Observes++; if (NativePending) blocked = true; }
        internal void AdvanceOwnedAttempt() { Advances++; }
        internal void AdvanceOwned(UnityEngine.Component battle, BattlePresentationOwnership owner) { Advances++; }
        internal void Advance(UnityEngine.Component battle) { UnownedAdvances++; }
        internal bool Stop() { return true; }
        internal void Confirm() { blocked = false; NativePending = false; }
    }
    internal static class BattleLaunch
    {
        internal static string Error { get { return ""; } }
        internal static void Initialize(string nonce, BattleSettlementAdmission admit) { }
        internal static void Validate() { }
        internal static void Select(Command command) { }
        internal static string Json() { return "null"; }
        internal static bool Stop() { return true; }
    }
    internal sealed class DailyScriptAdapter
    {
        internal static int Captures, Executes, Ownerships;
        internal bool Busy { get { return true; } }
        internal bool OwnsBattlePresentation { get { return false; } }
        internal bool PopupHandoffReady { get { return false; } }
        internal bool OwnsRewardPopup(UnityEngine.Component popup, Delegate close) { return false; }
        internal BattlePresentationOwnership OwnedBattlePresentation(UnityEngine.Component battle) { Ownerships++; return new BattlePresentationOwnership(BattleMode.Training, "op", null, null, "target", delegate { return true; }); }
        internal bool PresentSettledBattle(UnityEngine.Component battle) { return false; }
        internal void Capture(Model model, int task = 0) { Captures++; }
        internal bool Queue(Command command, out string why) { why = ""; return true; }
        internal bool Execute(Model model, DevelopmentPublication publish, out string why) { Executes++; why = ""; return true; }
        internal void NotInvoked(string why) { }
        internal string Json() { return "{\"fixture_revision\":42}"; }
        internal void Stop(string why) { }
    }
    internal delegate bool DailyPopupOwnershipCheck(UnityEngine.Component popup, Delegate close);
    internal delegate bool DailyMonsterInvocationGate();
    internal sealed class DailyPopupModel
    {
        internal DailyPopupModel(BattleSettlementEvent emit) { }
        internal int Revision { get { return 0; } }
        internal bool GuiInvocationAttempted { get { return false; } }
        internal bool OutcomeUnknown { get { return false; } }
        internal bool HandoffOwned(DailyPopupOwnershipCheck owns) { return false; }
        internal void Capture(Model model, bool busy) { }
        internal bool Queue(Command command, out string why) { why = ""; return true; }
        internal bool ExecuteGui(Model model, bool busy, DevelopmentPublication publish, DailyMonsterInvocationGate begin, out string why) { why = ""; return false; }
        internal void NotInvoked(string why) { }
        internal string Json() { return "null"; }
        internal void Stop() { }
    }
}
#endif
namespace JiangShen.AssistantRuntime
{
#if !JSB_ARENA_PROBE_TEST
    internal sealed class ProbeFailure : Exception { internal ProbeFailure(string text) : base(text) { } }
    internal sealed class Snapshot
    {
        internal UnityEngine.Component Main;
#if JSB_STONE_PROBE_TEST || JSB_MINGSHI_PROBE_TEST || JSB_SOUL_PROBE_TEST || JSB_REFINE_PROBE_TEST || JSB_WOOD_PROBE_TEST
        internal UnityEngine.Component Root;
        internal object Character;
#endif
        internal bool InteractionEnabled, Safe;
        internal int InstanceId = 101;
        internal string Gate, Resources = "{\"level\":86,\"stamina\":20,\"gold\":375}";
        internal long[] ResourceValues = new long[] { 86, 20, 375 };
        internal bool SameTarget(Snapshot other) { return other != null && object.ReferenceEquals(Main, other.Main); }
        internal string Json() { return "\"main_instance_id\":101,\"scene\":1,\"root_level_id\":\"synthetic\",\"gate\":" + Command.Quote(Gate) + ",\"safe\":" + (Safe ? "true" : "false") + ",\"main_enabled\":" + (InteractionEnabled ? "true" : "false") + ",\"formation_set\":1,\"other_visible_ui_types\":[],\"resources\":" + Resources; }
    }
    internal sealed class Model
    {
        internal static UnityEngine.GameObject BattleOwner;
        internal UnityEngine.GameObject BattleUiObject() { return BattleOwner; }
        internal static Type GameType(string name) { return typeof(BattleUI); }
        internal static readonly string[] ResourceNames = new string[] { "level", "stamina", "gold" };
        internal static bool Interaction = true, Unsafe;
        internal static int DisableCalls, EnableCalls;
        private static readonly UnityEngine.Component Main = new UnityEngine.Component();
        internal Snapshot Capture() { return Capture(null); }
        internal Snapshot Capture(UnityEngine.Component own)
        {
#if JSB_WOOD_PROBE_TEST
            return DailyWoodProbeTests.Snapshot();
#elif JSB_SOUL_PROBE_TEST || JSB_REFINE_PROBE_TEST
            return new Snapshot { Main = MainUI.Current, Root = BaseAsset.lds.currmg, Character = Global.character_,
                InteractionEnabled = MainUI.m_subui == null, Safe = true,
                Gate = !Global.extern_enabled ? "global_input_disabled" : MainUI.m_subui != null ? "subui_present" : "PASS" };
#elif JSB_STONE_PROBE_TEST
            return new Snapshot { Main = new MainUI(), Root = BaseAsset.lds.currmg, Character = Global.character_,
                InteractionEnabled = MainUI.m_subui == null, Safe = true,
                Gate = !Global.extern_enabled ? "global_input_disabled" : MainUI.m_subui != null ? "subui_present" : "PASS" };
#elif JSB_MINGSHI_PROBE_TEST
            if (BaseAsset.lds.currmg != MainUI.Current.MainRoot)
                return new Snapshot { Gate = "main_not_unique" };
            return new Snapshot { Main = MainUI.Current.Main, Root = BaseAsset.lds.currmg, Character = Global.character_,
                InteractionEnabled = true, Safe = true, Gate = "PASS" };
#else
            return new Snapshot { Main = Main, InteractionEnabled = Interaction, Safe = !Unsafe, Gate = Unsafe ? "synthetic_overlay" : "PASS" };
#endif
        }
#if JSB_STONE_PROBE_TEST || JSB_MINGSHI_PROBE_TEST || JSB_SOUL_PROBE_TEST || JSB_REFINE_PROBE_TEST || JSB_WOOD_PROBE_TEST
        internal static Type GameType(string name) { return FirstType(name); }
        internal static Type FirstType(string name) { return Type.GetType(name, true); }
        internal UnityEngine.Component CurrentRoot() { return BaseAsset.lds.currmg; }
#if JSB_MINGSHI_PROBE_TEST
        internal UnityEngine.GameObject GlobalUiObject() { return UnityEngine.GameObject.Find("/Root/RootUI"); }
        internal UnityEngine.Object[] AllUi() { return MainUI.Current.AllUi(); }
        internal Type RootMessengerType { get { return typeof(RootMessenger); } }
#elif JSB_WOOD_PROBE_TEST
        internal UnityEngine.GameObject GlobalUiObject() { return BaseUI.rootUI.gameObject; }
        internal UnityEngine.Object[] AllUi() { return DailyWoodProbeTests.AllUi(); }
        internal Type RootMessengerType { get { return typeof(RootUI); } }
#elif JSB_SOUL_PROBE_TEST
        internal UnityEngine.GameObject GlobalUiObject() { return BaseUI.rootUI.gameObject; }
        internal UnityEngine.Object[] AllUi() { return new UnityEngine.Object[] { BaseUI.rootUI, MainUI.Soul, MainUI.Current }; }
#else
        internal UnityEngine.GameObject GlobalUiObject() { return BaseUI.rootUI.gameObject; }
        internal UnityEngine.Object[] AllUi() { return new UnityEngine.Object[] { BaseUI.rootUI, MainUI.Panel }; }
#endif
        internal string OverlayGate(UnityEngine.Behaviour ui) { return "fixture_unexpected_overlay"; }
#endif
        internal void SetInteraction(Snapshot original, bool restore) { Interaction = restore; if (restore) EnableCalls++; else DisableCalls++; }
    }
#endif
    internal sealed class CampaignObserver
    {
        internal string Status = "", AttemptId = "";
        internal int CampaignId;
        internal byte TroopId;
        internal static bool TestPending;
        internal bool MutationAttempted;
        internal bool Pending { get { return TestPending; } }
        internal void Unknown(string why) { }
        internal string BusinessJson() { return "{\"mutation_attempted\":false,\"pending_attempt\":false,\"resource_rollback\":false,\"attempt\":null}"; }
    }
    internal delegate void CampaignPublication();
#if !JSB_STONE_PROBE_TEST && !JSB_MINGSHI_PROBE_TEST && !JSB_ARENA_PROBE_TEST && !JSB_SOUL_PROBE_TEST && !JSB_REFINE_PROBE_TEST && !JSB_WOOD_PROBE_TEST
    internal delegate void DevelopmentPublication();
    internal sealed class SharedDevelopmentContext { internal static object Read(object value, string name) { return null; } }
    internal static class DevelopmentContextReader
    {
        internal static SharedDevelopmentContext DailyReward(Model model, bool blocked) { return new SharedDevelopmentContext(); }
        internal static SharedDevelopmentContext DailyStone(Model model, bool blocked) { return new SharedDevelopmentContext(); }
        internal static SharedDevelopmentContext DailyRefine(Model model, bool blocked) { return new SharedDevelopmentContext(); }
        internal static SharedDevelopmentContext DailyArena(Model model, bool blocked) { return new SharedDevelopmentContext(); }
        internal static SharedDevelopmentContext DailySoul(Model model, bool blocked) { return new SharedDevelopmentContext(); }
        internal static SharedDevelopmentContext Read(Model model, Snapshot common, string external, bool blocked) { return new SharedDevelopmentContext(); }
        internal static string ExternalGate(string gate) { return gate; }
    }
    internal sealed class DevelopmentCallbackCapture
    {
        internal static DevelopmentCallbackCapture InstallDailyReward(object client, object handler,
            string id, int sequence, Delegate original, Action<byte[]> read, out string why) { why = "fixture"; return null; }
        internal static DevelopmentCallbackCapture InstallDailyTimed(object client, object handler, int opcode,
            string id, int sequence, Delegate original, Action<byte[]> read, out string why) { why = "fixture"; return null; }
        internal static DevelopmentCallbackCapture InstallDailyStone(object client, object handler,
            string id, int sequence, Delegate original, Action<byte[]> read, out string why) { why = "fixture"; return null; }
        internal static DevelopmentCallbackCapture InstallDailyRefine(object client, object handler,
            string id, int sequence, Delegate original, Action<byte[]> read, out string why) { why = "fixture"; return null; }
        internal static DevelopmentCallbackCapture InstallDailyMingshi(object client, object handler,
            string id, int sequence, Delegate original, Action<byte[]> read, out string why) { why = "fixture"; return null; }
        internal static DevelopmentCallbackCapture InstallDailyActivityPush(object client, object handler,
            string id, int sequence, Delegate original, Action<byte[]> read, out string why) { why = "fixture"; return null; }
        internal static DevelopmentCallbackCapture InstallDailySoul(object client, object handler, int opcode,
            string id, int sequence, Delegate original, Action<byte[]> read, out string why) { why = "fixture"; return null; }
    }
    internal sealed class DevelopmentCommand { }
#endif
#if !JSB_REFINE_PROBE_TEST
    internal delegate SharedDevelopmentContext DailyRefineContextProvider();
    internal delegate bool DailyRefineCommitCheck();
    internal delegate bool DailyRefineInvocationGate(DailyRefineCommitCheck prepare, DailyRefineCommitCheck check);
    internal delegate DevelopmentCallbackCapture DailyRefineCallbackInstall(object client, object handler, string id,
        int sequence, Delegate original, Action<byte[]> read, out string why);
    internal sealed class DailyRefineModel
    {
        internal DailyRefineModel(DailyRefineCallbackInstall install, DailyRefineCallbackInstall activity) { }
        internal int Revision { get { return 0; } }
        internal bool GuiInvocationAttempted { get { return false; } }
        internal bool MutationAttempted { get { return false; } }
        internal bool PanelClosed { get { return false; } }
        internal bool OutcomeUnknown { get { return false; } }
        internal bool Rejected { get { return false; } }
        internal bool Busy { get { return false; } }
        internal void Capture(SharedDevelopmentContext context) { }
        internal bool Queue(int taskId, ulong role, string id, int sequence, int expected, string action, out string why)
        { why = "fixture"; return false; }
        internal bool ExecuteGui(DailyRefineContextProvider context, DevelopmentPublication publish,
            DailyRefineInvocationGate begin, out string why) { why = "fixture"; return false; }
        internal void NotInvoked(string why) { }
        internal void Stop() { }
        internal string ViewJson() { return "null"; }
        internal string OperationJson() { return "null"; }
    }
#endif
#if !JSB_SOUL_PROBE_TEST
    internal delegate DevelopmentCallbackCapture DailySoulResponseCapture(object client, object handler, int opcode,
        string id, int sequence, Delegate original, Action<byte[]> read, out string why);
    internal delegate SharedDevelopmentContext DailySoulContext();
    internal delegate bool DailySoulMainReady();
    internal delegate bool DailySoulInvocationCheck();
    internal delegate bool DailySoulInvocationGate(DailySoulInvocationCheck prepare, DailySoulInvocationCheck check);
    internal sealed class DailySoulModel
    {
        internal DailySoulModel(DailySoulResponseCapture install, DailySoulMainReady ready, DailySoulInvocationCheck finalWindow) { }
        internal bool Busy { get { return false; } }
        internal bool NeedsUiCleanup { get { return false; } }
        internal bool UiCleanupUnresolved { get { return false; } }
        internal bool Unresolved { get { return false; } }
        internal bool OutcomeUnknown { get { return false; } }
        internal bool MutationAttempted { get { return false; } }
        internal bool GuiInvocationAttempted { get { return false; } }
        internal int Revision { get { return 0; } }
        internal void Capture(SharedDevelopmentContext context) { }
        internal bool Queue(string action, ulong role, string id, int sequence, int revision, int mode, bool freeOnly, out string why)
        { why = "fixture"; return false; }
        internal bool ExecuteGui(DailySoulContext context, DevelopmentPublication publish, DailySoulInvocationGate gate, out string why)
        { why = "fixture"; return false; }
        internal void NotInvoked() { }
        internal void Stop(string why) { }
        internal void TryUiCleanup(DailySoulContext context) { }
        internal void FinishUiCleanup() { }
        internal string Json() { return "null"; }
    }
#endif
#if !JSB_ARENA_PROBE_TEST
    internal delegate bool DailyArenaInvocationDeadline();
    internal delegate bool DailyArenaInvocationGate(DailyArenaInvocationDeadline withinDeadline);
    internal sealed class DailyArenaChestModel
    {
        internal bool Rearm(int sequence, int revision, string id, ulong role, SharedDevelopmentContext context, out DailyArenaChestModel replacement, out string why) { replacement = null; why = "fixture"; return false; }
        internal bool Busy { get { return false; } }
        internal bool MutationAttempted { get { return false; } }
        internal bool GuiInvocationAttempted { get { return false; } }
        internal int Revision { get { return 0; } }
        internal void Capture(SharedDevelopmentContext context) { }
        internal bool Queue(int sequence, int revision, string action, string id, ulong role, out string why)
        { why = "fixture"; return false; }
        internal bool ExecuteGui(int sequence, int revision, string action, string id, ulong role,
            Func<SharedDevelopmentContext> freshContext, DevelopmentPublication publish, DailyArenaInvocationGate begin, out string why)
        { why = "fixture"; return false; }
        internal void NotInvoked() { }
        internal void Failed(string why) { }
        internal void Stop(string why) { }
        internal string Json() { return "null"; }
    }
#endif
#if !JSB_STONE_PROBE_TEST
    internal delegate SharedDevelopmentContext DailyStoneContextProvider();
    internal delegate bool DailyStoneInvocationCommit();
    internal delegate bool DailyStoneInvocationGate(DailyStoneInvocationCommit sample, DailyStoneInvocationCommit check);
    internal delegate DevelopmentCallbackCapture DailyStoneCallbackInstall(object client, object handler,
        string id, int sequence, Delegate original, Action<byte[]> read, out string why);
    internal sealed class DailyStoneModel
    {
        internal DailyStoneModel(DailyStoneCallbackInstall install, DailyStoneCallbackInstall progress) { }
        internal int Revision { get { return 0; } }
        internal bool Busy { get { return false; } }
        internal bool MutationAttempted { get { return false; } }
        internal bool OutcomeUnknown { get { return false; } }
        internal void Capture(SharedDevelopmentContext context) { }
        internal bool Queue(int task, ulong role, string id, int sequence, int revision, string action, out string why)
        { why = "fixture"; return false; }
        internal bool ExecuteGui(DailyStoneContextProvider context, DevelopmentPublication publish, DailyStoneInvocationGate begin, out string why)
        { why = "fixture"; return false; }
        internal void NotInvoked(string why) { }
        internal void Stop() { }
        internal string ViewJson() { return "{}"; }
        internal string OperationJson() { return "{}"; }
    }
#endif
#if !JSB_MINGSHI_PROBE_TEST
    internal delegate SharedDevelopmentContext DailyMingshiContextProvider();
    internal delegate bool DailyMingshiInvocationCheck();
    internal delegate bool DailyMingshiInvocationGate(DailyMingshiInvocationCheck prepare, DailyMingshiInvocationCheck check);
    internal delegate DevelopmentCallbackCapture DailyMingshiCallbackInstall(object client, object handler, string id, int sequence,
        Delegate original, Action<byte[]> read, out string why);
    internal sealed class DailyMingshiModel
    {
        internal DailyMingshiModel(DailyMingshiCallbackInstall install, DailyMingshiCallbackInstall progress) { }
        internal int Revision { get { return 0; } }
        internal bool Busy { get { return false; } }
        internal bool MutationAttempted { get { return false; } }
        internal bool OutcomeUnknown { get { return false; } }
        internal void Capture(SharedDevelopmentContext context) { }
        internal bool Queue(int task, ulong role, string id, int sequence, int revision, string action, out string why)
        { why = "fixture"; return false; }
        internal bool ExecuteGui(DailyMingshiContextProvider context, DevelopmentPublication publish, DailyMingshiInvocationGate begin, out string why)
        { why = "fixture"; return false; }
        internal void NotInvoked(string why) { }
        internal void Stop(string why) { }
        internal string ViewJson() { return "{}"; }
        internal string OperationJson() { return "{}"; }
    }
#endif
    internal delegate SharedDevelopmentContext DailyRewardContextProvider();
    internal delegate bool DailyRewardInvocationGate();
    internal delegate DevelopmentCallbackCapture DailyRewardCallbackInstaller(object client, object handler,
        string id, int sequence, Delegate original, Action<byte[]> read, out string why);
#if !JSB_MINGSHI_PROBE_TEST && !JSB_WOOD_PROBE_TEST
    internal delegate DevelopmentCallbackCapture DailyTimedCallbackInstall(object client, object handler, int opcode,
        string id, int sequence, Delegate original, Action<byte[]> read, out string why);
    internal delegate bool DailyTimedInvocationGate();
    internal sealed class DailyTimedModel
    {
        internal static int Calls;
        internal static Action AfterFinalSample;
        private string status = "IDLE";
        internal int Revision { get; private set; }
        internal bool Unresolved { get { return status == "UNKNOWN"; } }
        internal bool Busy { get { return status == "QUEUED" || status == "PENDING" || Unresolved; } }
        internal DailyTimedModel(Model model, DailyTimedCallbackInstall install) { Revision = 1; }
        internal void Capture(bool blocked) { }
        internal bool Queue(int task, ulong role, string id, int sequence, int revision, string action, out string why)
        {
            why = "";
            if (Busy) return false;
            status = "QUEUED"; Revision++; return true;
        }
        internal bool ExecuteGui(DevelopmentPublication publish, DailyTimedInvocationGate begin, out string why)
        {
            publish();
            if (AfterFinalSample != null) AfterFinalSample();
            why = "daily_timed_gui_final_admission_changed";
            if (!begin()) { NotInvoked(why); return false; }
            Calls++; status = "PENDING"; Revision++; why = ""; return true;
        }
        internal void NotInvoked(string why)
        { if (status == "QUEUED") { status = "REJECTED"; Revision++; } }
        internal void Stop(string why)
        { if (status == "PENDING") { status = "UNKNOWN"; Revision++; } else NotInvoked(why); }
        internal string Json() { return "{\"status\":" + Command.Quote(status) + "}"; }
    }
#endif
    internal sealed class DailyRewardModel
    {
        internal static int Calls;
        internal static Action BeforePublication;
        internal static Action AfterFinalSample;
        internal static bool UnknownAfterClaim, RejectAfterPublication;
        private bool queued, invoked, opened;
        private readonly DailyRewardContextProvider contextProvider;
        private string status = "IDLE";
        internal int Revision { get { return 1; } }
        internal bool Busy { get { return queued || opened || invoked; } }
        internal bool Unresolved { get { return invoked; } }
        internal bool MutationAttempted { get { return invoked; } }
        internal bool OutcomeUnknown { get { return status == "UNKNOWN"; } }
        internal DailyRewardModel(DailyRewardCallbackInstaller install, DailyRewardContextProvider context) { contextProvider = context; }
        internal void Capture(SharedDevelopmentContext context) { if (invoked && UnknownAfterClaim) status = "UNKNOWN"; }
        internal bool Queue(int sequence, int revision, string action, string id, ulong role, int category, out string why)
        { why = ""; queued = true; return true; }
        internal bool ExecuteGui(int sequence, int revision, string action, string id, ulong role, int category,
            DevelopmentPublication publish, DailyRewardInvocationGate begin, out string why)
        {
            why = "";
            if (!queued) return false;
            queued = false;
            if (BeforePublication != null) BeforePublication();
            publish();
            if (contextProvider() == null) { why = "fresh_reward_context_rejected"; return false; }
            if (AfterFinalSample != null) AfterFinalSample();
            if (RejectAfterPublication) { if (invoked) status = "UNKNOWN"; why = "fresh_reward_context_rejected"; return false; }
            if (!begin()) { why = "daily_reward_gui_final_admission_changed"; return false; }
            Calls++;
            opened = true; invoked = action == "claim"; status = "WAITING";
            return true;
        }
        internal void NotInvoked() { queued = false; }
        internal void Failed(string why) { Stop(why); }
        internal void Stop(string why) { queued = opened = false; status = invoked ? "UNKNOWN" : "STOPPED"; }
        internal string Json() { return "{\"status\":" + Command.Quote(status) + "}"; }
    }
    internal static class DailyActivityModel
    {
        internal static string Capture(Model model) { return "{\"schema\":1,\"available\":false,\"reason\":\"synthetic_daily_unavailable\",\"source\":\"client_projection\",\"character_id\":\"\",\"server_now\":null,\"server_day\":null,\"refresh_clear\":null,\"categories\":[]}"; }
    }
    internal sealed class DailyGuildModel
    {
        internal static bool TestBusy;
        internal int Revision { get { return 0; } }
        internal bool Busy { get { return TestBusy; } }
        internal bool Unresolved { get { return false; } }
        internal bool MutationAttempted { get { return false; } }
        internal void Capture(SharedDevelopmentContext context) { }
        internal bool Queue(Command command, out string why) { why = "synthetic_guild_unavailable"; return false; }
        internal bool ExecuteGui(Command command, DevelopmentPublication publish, out string why) { why = "synthetic_guild_unavailable"; return false; }
        internal void NotInvoked() { }
        internal void Failed(string why) { }
        internal void Stop(string why) { }
        internal string Json() { return "null"; }
    }
#if !JSB_STONE_PROBE_TEST && !JSB_MINGSHI_PROBE_TEST && !JSB_ARENA_PROBE_TEST && !JSB_SOUL_PROBE_TEST && !JSB_REFINE_PROBE_TEST && !JSB_WOOD_PROBE_TEST
    internal sealed class PlayerStateRefreshModel
    {
        internal Exception BindingError { get { return null; } }
        internal string BindingStage { get { return ""; } }
        private readonly bool daily;
        internal static bool AllowDaily;
        internal static int DailyCalls;
        internal static Action BeforeDailyPublication;
        internal PlayerStateRefreshModel(bool daily = false) { this.daily = daily; }
        internal string Status = "IDLE";
        internal bool Pending { get { return daily && (Status == "QUEUED" || Status == "WAITING"); } }
        internal bool MutationAttempted { get { return false; } }
        internal void Capture(SharedDevelopmentContext context) { }
        internal bool Queue(DevelopmentCommand command, out string why)
        { if (daily && AllowDaily) { Status = "QUEUED"; why = ""; return true; } why = "synthetic_refresh_unavailable"; return false; }
        internal bool ExecuteGui(DevelopmentCommand command, DevelopmentPublication publish, out string why)
        {
            if (!daily || !AllowDaily || Status != "QUEUED") { why = "synthetic_refresh_unavailable"; return false; }
            if (BeforeDailyPublication != null) BeforeDailyPublication();
            publish(); DailyCalls++; Status = "WAITING"; why = ""; return true;
        }
        internal void NotInvoked(string why) { Status = "REJECTED"; }
        internal void Stop(string why) { }
        internal string Json() { return "null"; }
    }
    internal static class DailyRefreshProof
    {
        internal static readonly object OriginalHandler = new object(), Observer = new object();
        internal static object CurrentHandler = OriginalHandler;
        internal static int Observations, Stops;
        internal static void ObserveNatural() { Observations++; CurrentHandler = Observer; }
        internal static bool Stop() { Stops++; CurrentHandler = OriginalHandler; return true; }
    }
#endif
    internal sealed class DevelopmentModel
    {
        internal static bool TestPending;
        internal static int TestRevision = 1;
        internal static bool AllowGui;
        internal static int GuiCalls;
        internal static Action BeforePublication;
        private bool pending, mutated;
        internal bool Pending { get { return pending || TestPending; } }
        internal bool MutationAttempted { get { return mutated; } }
        internal SharedDevelopmentContext CurrentContext { get { return null; } }
        internal int Revision { get { return TestRevision; } }
        internal static DevelopmentCommand ConvertCommand(Command command) { return new DevelopmentCommand(); }
        internal DevelopmentModel(Model model) { }
        internal void Capture(bool battlePending, bool verificationPending) { }
        internal void ObserveTarget(Command command) { }
        internal bool Queue(Command command, out string why) { why = AllowGui ? "" : "synthetic_development_unavailable"; pending = AllowGui; return AllowGui; }
        internal bool ExecuteGui(Command command, DevelopmentPublication publish, out string why)
        { if (BeforePublication != null) BeforePublication(); publish(); GuiCalls++; mutated = true; pending = false; why = ""; return true; }
        internal void NotInvoked(Command command, string why) { pending = false; }
        internal void Failed(Command command, string why) { pending = true; }
        internal void Stop(string why) { }
        internal string Json() { return "null"; }
    }
    internal sealed class CampaignModel
    {
        internal bool OwnsBattlePresentation(UnityEngine.Component battle) { return false; }
        internal static bool TestNavigation;
        internal static bool AllowGui, ThrowGui;
        internal static int GuiCalls, DirectCalls;
        internal static Action BeforeGuiPublication, AfterGuiPublication;
        private string commandJson = "null";
        internal readonly CampaignObserver Observer = new CampaignObserver();
        internal CampaignModel(Model model, bool challenge = false) { }
        internal bool NavigationPending { get { return TestNavigation; } }
        internal int Revision { get { return 1; } }
        internal string Phase { get { return "MAIN"; } }
        internal bool Available { get { return true; } }
        internal void Capture() { }
        internal bool Execute(Command command, CampaignPublication publish, out string why) { DirectCalls++; why = ""; return true; }
        internal bool QueueGui(Command command, out string why) { why = AllowGui ? "" : "synthetic_campaign_unavailable"; GuiNotInvoked(command, why); return AllowGui; }
        internal bool ExecuteGui(Command command, CampaignPublication publish, out string why)
        {
            if (BeforeGuiPublication != null) BeforeGuiPublication();
            Observer.MutationAttempted = true; publish();
            if (AfterGuiPublication != null) AfterGuiPublication();
            GuiCalls++; if (ThrowGui) throw new InvalidOperationException("owned_gui_fixture");
            why = ""; Record(command, true, why); return true;
        }
        internal void GuiNotInvoked(Command command, string why) { Record(command, false, why); }
        internal void GuiFailed(Command command, bool attempted, string why) { Record(command, attempted, why); }
        private void Record(Command command, bool invoked, string why) { commandJson = "{\"command_seq\":" + command.Sequence + ",\"invoked\":" + (invoked ? "true" : "false") + ",\"reason\":" + Command.Quote(why) + "}"; }
        internal string Json() { return "null"; }
        internal string CommandJson() { return commandJson; }
        internal string BusinessJson() { return Observer.BusinessJson(); }
    }
}
