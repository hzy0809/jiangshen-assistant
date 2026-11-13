using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using JiangShen.AssistantRuntime;

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
        public T AddComponent<T>() where T : Component, new()
        {
            T value = new T(); value.gameObject = this; Probe = value as Probe; return value;
        }
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
        internal static bool TestPending;
        internal bool MutationAttempted;
        internal bool Pending { get { return TestPending; } }
        internal void Unknown(string why) { }
        internal string BusinessJson() { return "{\"mutation_attempted\":false,\"pending_attempt\":false,\"resource_rollback\":false,\"attempt\":null}"; }
    }
    internal delegate void CampaignPublication();
#if !JSB_STONE_PROBE_TEST && !JSB_MINGSHI_PROBE_TEST && !JSB_ARENA_PROBE_TEST && !JSB_SOUL_PROBE_TEST && !JSB_REFINE_PROBE_TEST && !JSB_WOOD_PROBE_TEST
    internal delegate void DevelopmentPublication();
    internal sealed class SharedDevelopmentContext { }
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
        internal bool Queue(string action, ulong role, string id, int sequence, int revision, out string why)
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
        internal static bool TestNavigation;
        internal static bool AllowGui, ThrowGui;
        internal static int GuiCalls;
        internal static Action BeforeGuiPublication, AfterGuiPublication;
        private string commandJson = "null";
        internal readonly CampaignObserver Observer = new CampaignObserver();
        internal CampaignModel(Model model, bool challenge = false) { }
        internal bool NavigationPending { get { return TestNavigation; } }
        internal int Revision { get { return 1; } }
        internal string Phase { get { return "MAIN"; } }
        internal bool Available { get { return true; } }
        internal void Capture() { }
        internal bool Execute(Command command, CampaignPublication publish, out string why) { why = "synthetic_campaign_unavailable"; return false; }
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
#if !JSB_MINGSHI_PROBE_TEST && !JSB_ARENA_PROBE_TEST && !JSB_SOUL_PROBE_TEST && !JSB_REFINE_PROBE_TEST && !JSB_WOOD_PROBE_TEST
internal static class LifecycleTests
{
    private static int checks;
    private static string testRoot;
    private static readonly string Prefix = Guid.NewGuid().ToString("N").Substring(0, 30) + "a";
    private static readonly string NonceA = Prefix + "1", NonceB = Prefix + "2", NonceC = Prefix + "3", NonceD = Prefix + "4";
    private static void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
    private static Command Cmd(int sequence, string name)
    { return Command.Parse("version=3\nnonce=" + NonceA + "\nsequence=" + sequence + "\ncommand=" + name + "\n", NonceA, 0); }
    private static void PureLifecycle()
    {
        string reason; SessionLifecycle life = new SessionLifecycle(0);
        Check(!life.LeaseExpired(19999) && life.LeaseExpired(20000), "lease_boundary");
        Check(life.Accept(Cmd(1, "heartbeat"), 19000, out reason), "heartbeat_accepted");
        Check(!life.LeaseExpired(38999), "heartbeat_renews");
        for (int i = 2; i < 80; i++) Check(life.Accept(Cmd(i, "heartbeat"), 19000 + (i - 1) * 5000, out reason), "long_lived_heartbeat");
        Check(life.BeginAction() && !life.BeginAction(), "single_action");
        life.CompleteAction(true); Check(life.BeginAction(), "repeat_after_verified_restore"); life.CompleteAction(true);
        Check(life.Accept(Cmd(80, "stop"), 415000, out reason), "stop_accepted");
        Check(!life.Accept(Cmd(81, "heartbeat"), 415001, out reason) && !life.BeginAction(), "stop_not_resurrected");
        life.MarkDestroyed(); Check(!life.Accept(Cmd(82, "disable-restore"), 415002, out reason), "no_command_after_destroy");
        SessionLifecycle expired = new SessionLifecycle(0);
        Check(!expired.Accept(Cmd(1, "disable-restore"), 20001, out reason) && expired.StopRequested, "queued_action_after_expiry");
        SessionLifecycle wrap = new SessionLifecycle(Int32.MaxValue - 10);
        Check(!wrap.LeaseExpired(Int32.MinValue + 10), "tickcount_signed_wrap");
        Check(Command.Parse("version=1\nnonce=" + NonceA + "\nsequence=1\ncommand=stop", NonceA, 0) == null, "old_protocol_rejected");
        Check(Command.Parse("version=2\nnonce=" + NonceA + "\nsequence=1\ncommand=snapshot", NonceA, 0) == null, "v2_protocol_rejected");
        Check(Command.Parse("version=3\nnonce=" + NonceA + "\nsequence=1\ncommand=eval", NonceA, 0) == null, "arbitrary_command_rejected");
        Check(Command.Parse("version=3\nnonce=" + NonceA + "\nsequence=1\ncommand=snapshot", NonceA, 1) == null, "duplicate_rejected");
        string development = "version=3\nnonce=" + NonceA + "\nsequence=1\ncommand=development-command\naction=equipment_enchant\nexpected_revision=1\noperation_id=" + NonceA + "\ntarget_id=1\ndesired_level=2\nmax_requests=1\nmax_money=2147483647\nmax_stone=0\nmax_food=0\nmode=enchant\n";
        Command parsed = Command.Parse(development, NonceA, 0);
        Check(parsed != null && parsed.DevelopmentAction == "equipment_enchant" && parsed.MaxMoney == 2147483647L, "fixed_development_budget_boundary");
        Check(Command.Parse(development.Replace("2147483647", "2147483648"), NonceA, 0) == null, "development_budget_overflow_rejected");
        Check(Command.Parse(development.Replace("equipment_enchant", "equipment_observe"), NonceA, 0) == null, "observe_cannot_mutate_command_shape");
        Check(Command.Parse(development.Replace("command=development-command", "command=development-observe").Replace("equipment_enchant", "equipment_observe"), NonceA, 0) != null, "typed_development_observation");
        Check(Command.Parse(development.Replace("target_id=1", "target_id=-1"), NonceA, 0) == null, "development_negative_target_rejected");
        Check(Command.Parse(development.Replace("max_requests=1", "max_requests=0"), NonceA, 0) == null, "development_empty_budget_rejected");
        Check(Command.Parse(development.Replace("mode=enchant", "mode=method"), NonceA, 0) == null, "development_arbitrary_mode_rejected");
        Check(Command.Parse(development.Replace("max_stone=0", "max_stone=00"), NonceA, 0) == null, "development_noncanonical_rejected");
        Check(Command.Parse(development.Replace("operation_id=" + NonceA, "operation_id=" + NonceA.ToUpperInvariant()), NonceA, 0) == null, "development_uppercase_identity_rejected");
        string refresh = development.Replace("command=development-command", "command=player-state-refresh").Replace("action=equipment_enchant", "action=player_state_refresh").Replace("target_id=1", "target_id=0").Replace("desired_level=2", "desired_level=0").Replace("max_requests=1", "max_requests=0").Replace("max_money=2147483647", "max_money=0").Replace("mode=enchant", "mode=");
        Check(Command.Parse(refresh, NonceA, 0) != null, "fixed_nonconsumptive_refresh_command");
        Check(Command.Parse(refresh.Replace("max_food=0", "max_food=1"), NonceA, 0) == null, "refresh_cannot_spend_resources");
        Check(Command.Parse(refresh.Replace("command=player-state-refresh", "command=development-command"), NonceA, 0) == null, "refresh_separate_from_consumptive_actions");
        string reward = RewardWire(NonceA, 1, "claim");
        parsed = Command.Parse(reward, NonceA, 0);
        Check(parsed != null && parsed.DailyRewardAction == "claim" && parsed.DailyRewardCharacterId == UInt64.MaxValue &&
            parsed.DailyRewardCategoryId == 2 && parsed.OperationId == NonceA && parsed.ExpectedRevision == 1, "reward_fixed_contract");
        foreach (string invalid in new[] { reward.Replace("category_id=2", "category_id=1"), reward.Replace("category_id=2", "category_id=7"),
            reward.Replace("category_id=2", "category_id=02"), reward.Replace("action=claim", "action=buy"),
            reward.Replace("action=claim", "action=collect_task"), reward.Replace("character_id=", "character_id=0"),
            reward.Replace("18446744073709551615", "18446744073709551616"), reward.Replace("expected_revision=1", "expected_revision=0"),
            reward.Replace("operation_id=" + NonceA, "operation_id=" + NonceA.ToUpperInvariant()), reward + "max_premium=1\n",
            reward.Replace("command=daily-reward-command", "command=daily-reward-observe") })
            Check(Command.Parse(invalid, NonceA, 0) == null, "reward_rejects_unbounded_or_noncanonical_wire");
        string timed = TimedWire(NonceA, 1, "participate");
        parsed = Command.Parse(timed, NonceA, 0);
        Check(parsed != null && parsed.DailyTimedAction == "participate" && parsed.DailyTimedCharacterId == UInt64.MaxValue &&
            parsed.DailyTimedTaskId == 13 && parsed.OperationId == NonceA && parsed.ExpectedRevision == 1 &&
            Cmd(1, "daily-timed-observe") != null, "timed_fixed_contract");
        Check(Command.Parse(timed.Replace("task_id=13", "task_id=12").Replace("action=participate", "action=close"), NonceA, 0) != null,
            "only quiz accepts fixed close");
        foreach (string invalid in new[] { timed.Replace("task_id=13", "task_id=14"), timed.Replace("task_id=13", "task_id=013"),
            timed.Replace("action=participate", "action=close"), timed.Replace("action=participate", "action=cs_join"),
            timed.Replace("character_id=", "character_id=0"), timed.Replace("18446744073709551615", "18446744073709551616"),
            timed.Replace("expected_revision=1", "expected_revision=0"),
            timed.Replace("operation_id=" + NonceA, "operation_id=" + NonceA.ToUpperInvariant()), timed + "opcode=324\n",
            timed.Replace("command=daily-timed-command", "command=daily-timed-observe") })
            Check(Command.Parse(invalid, NonceA, 0) == null, "timed_rejects_unbounded_or_noncanonical_wire");
    }
    private static string DirectoryFor(string nonce) { return Path.Combine(Entry.SessionsRoot, nonce); }
    private static string Read(string nonce, string file) { return File.ReadAllText(Path.Combine(DirectoryFor(nonce), file)); }
    private static string Start(string nonce)
    {
        string path = DirectoryFor(nonce); Directory.CreateDirectory(path);
        return Entry.Bootstrap(path, nonce, Entry.GetCurrentThreadId());
    }
    private static void Pulse()
    {
        UnityEngine.Time.frameCount++;
        Probe probe = UnityEngine.GameObject.LastHelper.Probe;
        typeof(Probe).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(probe, null);
    }
    private static void Set(string field, object value)
    { typeof(Probe).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(UnityEngine.GameObject.LastHelper.Probe, value); }
    private static void DestroyOwnHelper()
    {
        Check(UnityEngine.GameObject.LastHelper.PendingDestroy, "cleanup_requested_before_destroy");
        typeof(Probe).GetMethod("OnDestroy", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(UnityEngine.GameObject.LastHelper.Probe, null);
    }
    private static void WriteCommand(string nonce, int sequence, string name)
    { File.WriteAllText(Path.Combine(DirectoryFor(nonce), "command.txt"), "version=3\nnonce=" + nonce + "\nsequence=" + sequence + "\ncommand=" + name + "\n"); }
    private static string RewardWire(string nonce, int sequence, string action)
    {
        return "version=3\nnonce=" + nonce + "\nsequence=" + sequence + "\ncommand=daily-reward-command\naction=" + action +
            "\nexpected_revision=1\ncharacter_id=18446744073709551615\ncategory_id=2\noperation_id=" + nonce + "\n";
    }
    private static void WriteReward(string nonce, int sequence, string action)
    { File.WriteAllText(Path.Combine(DirectoryFor(nonce), "command.txt"), RewardWire(nonce, sequence, action)); }
    private static string TimedWire(string nonce, int sequence, string action)
    {
        return "version=3\nnonce=" + nonce + "\nsequence=" + sequence + "\ncommand=daily-timed-command\naction=" + action +
            "\noperation_id=" + nonce + "\ncharacter_id=18446744073709551615\ntask_id=13\nexpected_revision=1\n";
    }
    private static void WriteTimed(string nonce, int sequence, string action)
    { File.WriteAllText(Path.Combine(DirectoryFor(nonce), "command.txt"), TimedWire(nonce, sequence, action)); }
    private static void RealProbeWithSyntheticUnity()
    {
        Check(Start(NonceA).StartsWith("BOOTSTRAP_OK;protocol=1;"), "bootstrap");
        Check(Read(NonceA, "state.json").Contains("\"lifecycle\":\"bootstrapping\""), "bootstrap_no_target_access");
        Pulse(); Check(!Read(NonceA, "state.json").Contains("\"ready\":true"), "one_frame_not_ready");
        Pulse(); Check(Read(NonceA, "state.json").Contains("\"ready\":true"), "two_distinct_frames_ready");
        WriteCommand(NonceA, 1, "heartbeat"); Pulse(); Check(Read(NonceA, "ack.json").Contains("\"status\":\"COMPLETED\""), "heartbeat_ack");
        WriteCommand(NonceA, 2, "disable-restore"); Pulse(); Check(!Model.Interaction && Model.DisableCalls == 1, "typed_disable_once");
        Pulse(); Pulse(); Set("phaseTick", unchecked(Environment.TickCount - 6001)); Pulse(); Pulse(); Pulse();
        Check(Model.Interaction && Model.EnableCalls == 1, "first_restore");
        Check(Read(NonceA, "ack.json").Contains("\"restore_verified\":true") && Read(NonceA, "ack.json").Contains("\"outcome\":\"PASS\""), "action_completion_ack");
        WriteCommand(NonceA, 3, "disable-restore"); Pulse(); Check(!Model.Interaction && Model.DisableCalls == 2, "second_action_after_restore");
        WriteCommand(NonceA, 4, "stop"); Pulse(); Pulse(); Pulse();
        Check(Model.Interaction && Model.EnableCalls == 2, "stop_restores_owned_action");
        Check(Start(NonceB).StartsWith("BOOTSTRAP_REJECTED;"), "no_reconnect_before_ondestroy");
        DestroyOwnHelper();
        Check(Read(NonceA, "state.json").Contains("\"component_destroyed\":true") && Read(NonceA, "ack.json").Contains("\"command_seq\":4"), "stop_ack_after_destroy");
        Check(Start(NonceB).StartsWith("BOOTSTRAP_OK;"), "same_process_clean_reconnect"); Pulse(); Pulse();
        Probe current = UnityEngine.GameObject.LastHelper.Probe;
        SessionLifecycle life = (SessionLifecycle)typeof(Probe).GetField("life", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(current);
        typeof(SessionLifecycle).GetField("touched", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(life, unchecked((uint)(Environment.TickCount - 20001)));
        WriteCommand(NonceB, 1, "disable-restore"); Pulse();
        Check(Model.DisableCalls == 2 && UnityEngine.GameObject.LastHelper.PendingDestroy, "expiry_wins_over_queued_action"); DestroyOwnHelper();
        Check(Start(NonceC).StartsWith("BOOTSTRAP_OK;"), "reconnect_after_lease_cleanup"); Pulse(); Pulse();
        WriteCommand(NonceC, 1, "disable-restore"); Pulse(); Model.Unsafe = true; Pulse();
        Set("recoveryTick", unchecked(Environment.TickCount - 10001)); Pulse();
        Check(Read(NonceC, "state.json").Contains("\"lifecycle\":\"recovery_unknown\"") && File.Exists(Path.Combine(DirectoryFor(NonceC), "recovery_unknown.json")), "unsafe_restore_durable_unknown");
        DestroyOwnHelper();
        Check(Start(NonceD).StartsWith("BOOTSTRAP_REJECTED;recovery_unknown"), "unknown_blocks_reconnect");
    }
    private static void Rotation()
    {
        string directory = Path.Combine(testRoot, "rotation"); Directory.CreateDirectory(directory);
        ReceiptStore store = new ReceiptStore(directory);
        for (int i = 0; i < 1200; i++) store.Event("{\"padding\":\"" + new string('x', 2048) + "\"}");
        store.Write("state.json", "{\"value\":1}"); store.Write("state.json", "{\"value\":2}"); store.Close();
        Check(new FileInfo(Path.Combine(directory, "probe.events.jsonl")).Length <= ReceiptStore.MaximumEventBytes, "current_log_bounded");
        Check(new FileInfo(Path.Combine(directory, "probe.events.jsonl.1")).Length <= ReceiptStore.MaximumEventBytes, "previous_log_bounded");
        Check(Directory.GetFiles(directory, "probe.events.jsonl*").Length == 2, "two_log_segments_only");
        Check(File.ReadAllText(Path.Combine(directory, "state.json")).Contains("\"value\":2"), "atomic_latest_state_replace");
    }
    private static void GuiPulse()
    { typeof(Probe).GetMethod("OnGUI", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(UnityEngine.GameObject.LastHelper.Probe, null); }
    private static void GuiQueueRules()
    {
        for (int i = 0; i < 7; i++)
        {
            string nonce = Prefix.Substring(0, 24) + (100 + i).ToString("x8");
            CampaignModel.AllowGui = true; CampaignModel.ThrowGui = false; CampaignModel.BeforeGuiPublication = CampaignModel.AfterGuiPublication = null;
            int calls = CampaignModel.GuiCalls;
            Check(Start(nonce).StartsWith("BOOTSTRAP_OK;"), "gui_fixture_bootstrap"); Pulse(); Pulse();
            File.WriteAllText(Path.Combine(DirectoryFor(nonce), "command.txt"), "version=3\nnonce=" + nonce + "\nsequence=1\ncommand=campaign-command\naction=continue\nexpected_revision=1\ncampaign_id=12\ntroop_id=7\nattempt_id=\n");
            Pulse(); Check(Read(nonce, "ack.json").Contains("\"status\":\"ACCEPTED\"") && CampaignModel.GuiCalls == calls, "gui_queue_is_not_completion");
            if (i == 0)
            {
                CampaignModel.AfterGuiPublication = delegate { Check(Read(nonce, "state.json").Contains("\"phase\":\"owned\"") && Read(nonce, "state.json").Contains("\"invocation_attempted\":true"), "gui_ownership_persisted_before_game_call"); };
                GuiPulse(); GuiPulse(); Check(CampaignModel.GuiCalls == calls + 1 && Read(nonce, "ack.json").Contains("\"status\":\"COMPLETED\""), "multiple_gui_events_invoke_once");
            }
            else if (i == 1)
            {
                Set("guiQueuedTick", unchecked(Environment.TickCount - 1600)); Pulse(); GuiPulse();
                Check(CampaignModel.GuiCalls == calls && Read(nonce, "ack.json").Contains("\"status\":\"REJECTED\"") && Read(nonce, "ack.json").Contains("scenario_gui_callback_unavailable"), "no_gui_bounded_noninvocation");
            }
            else if (i == 2)
            {
                WriteCommand(nonce, 2, "stop"); GuiPulse();
                Check(CampaignModel.GuiCalls == calls && UnityEngine.GameObject.LastHelper.PendingDestroy, "published_stop_before_gui_without_update_wins");
            }
            else if (i == 3)
            {
                SessionLifecycle life = (SessionLifecycle)typeof(Probe).GetField("life", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(UnityEngine.GameObject.LastHelper.Probe);
                typeof(SessionLifecycle).GetField("touched", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(life, unchecked((uint)(Environment.TickCount - 20001)));
                GuiPulse(); Check(CampaignModel.GuiCalls == calls && UnityEngine.GameObject.LastHelper.PendingDestroy, "expiry_before_gui_wins");
            }
            else if (i == 4)
            {
                CampaignModel.ThrowGui = true; GuiPulse(); GuiPulse();
                Check(CampaignModel.GuiCalls == calls + 1 && Read(nonce, "ack.json").Contains("\"status\":\"UNKNOWN\""), "gui_exception_never_completed_or_replayed");
            }
            else if (i == 5)
            {
                CampaignModel.BeforeGuiPublication = delegate { File.Delete(Path.Combine(DirectoryFor(nonce), "state.json")); Directory.CreateDirectory(Path.Combine(DirectoryFor(nonce), "state.json")); };
                GuiPulse(); Check(CampaignModel.GuiCalls == calls && Read(nonce, "ack.json").Contains("\"status\":\"UNKNOWN\""), "ownership_write_failure_never_calls_game");
                Directory.Delete(Path.Combine(DirectoryFor(nonce), "state.json"));
            }
            else
            {
                CampaignModel.BeforeGuiPublication = delegate { WriteCommand(nonce, 2, "stop"); };
                GuiPulse(); Check(CampaignModel.GuiCalls == calls && UnityEngine.GameObject.LastHelper.PendingDestroy, "stop_published_during_gui_capture_before_ownership_wins");
            }
            CampaignModel.BeforeGuiPublication = CampaignModel.AfterGuiPublication = null;
            if (!UnityEngine.GameObject.LastHelper.PendingDestroy) { WriteCommand(nonce, 2, "stop"); Pulse(); }
            DestroyOwnHelper();
            if (i == 2 || i == 6)
            {
                Check(Read(nonce, "ack.json").Contains("\"command_name\":\"stop\"") && Read(nonce, "ack.json").Contains("\"command_seq\":2") && Read(nonce, "state.json").Contains("\"last_command_seq\":2"), "gui_cancel_preserves_newer_stop_ack_sequence");
                Check(Read(nonce, "ack.json").Contains("\"command_seq\":1,\"invoked\":false"), "cancelled_gui_receipt_stays_bound_to_original_sequence");
            }
        }
        CampaignModel.AllowGui = CampaignModel.ThrowGui = false;
    }
    private static void DevelopmentGuiRules()
    {
        for (int i = 0; i < 4; i++)
        {
            string nonce = Prefix.Substring(0, 24) + (200 + i).ToString("x8");
            DevelopmentModel.AllowGui = true; DevelopmentModel.BeforePublication = null;
            int calls = DevelopmentModel.GuiCalls;
            Check(Start(nonce).StartsWith("BOOTSTRAP_OK;"), "development_bootstrap"); Pulse(); Pulse();
            File.WriteAllText(Path.Combine(DirectoryFor(nonce), "command.txt"), "version=3\nnonce=" + nonce + "\nsequence=1\ncommand=development-command\naction=equipment_open\nexpected_revision=1\noperation_id=" + nonce + "\ntarget_id=7\ndesired_level=2\nmax_requests=1\nmax_money=10\nmax_stone=2\nmax_food=0\nmode=enchant\n");
            Pulse(); Check(DevelopmentModel.GuiCalls == calls && Read(nonce, "ack.json").Contains("\"status\":\"ACCEPTED\"") && Read(nonce, "state.json").Contains("\"pending_development\":true"), "development_queued_pending_before_game_call");
            if (i == 0)
            { GuiPulse(); GuiPulse(); Check(DevelopmentModel.GuiCalls == calls + 1 && Read(nonce, "ack.json").Contains("\"status\":\"COMPLETED\""), "development_gui_consumed_once"); }
            else if (i == 1)
            { WriteCommand(nonce, 2, "stop"); GuiPulse(); Check(DevelopmentModel.GuiCalls == calls && UnityEngine.GameObject.LastHelper.PendingDestroy, "development_stop_before_gui_wins"); }
            else if (i == 2)
            { Set("guiQueuedTick", unchecked(Environment.TickCount - 1600)); Pulse(); GuiPulse(); Check(DevelopmentModel.GuiCalls == calls && Read(nonce, "ack.json").Contains("\"status\":\"REJECTED\""), "development_gui_deadline_never_calls"); }
            else
            { DevelopmentModel.BeforePublication = delegate { WriteCommand(nonce, 2, "stop"); }; GuiPulse(); Check(DevelopmentModel.GuiCalls == calls && UnityEngine.GameObject.LastHelper.PendingDestroy, "development_stop_during_publication_wins"); }
            DevelopmentModel.BeforePublication = null;
            if (!UnityEngine.GameObject.LastHelper.PendingDestroy) { WriteCommand(nonce, 2, "stop"); Pulse(); }
            DestroyOwnHelper();
        }
        DevelopmentModel.AllowGui = false;
    }
#if !JSB_STONE_PROBE_TEST
    private static void DailyRefreshGuiRules()
    {
        for (int i = 0; i < 8; i++)
        {
            string nonce = Prefix.Substring(0, 24) + (240 + i).ToString("x8");
            int calls = PlayerStateRefreshModel.DailyCalls;
            PlayerStateRefreshModel.AllowDaily = true;
            Check(Start(nonce).StartsWith("BOOTSTRAP_OK;"), "daily_refresh_bootstrap"); Pulse(); Pulse();
            CampaignObserver.TestPending = i == 3; CampaignModel.TestNavigation = i == 4;
            DevelopmentModel.TestPending = i == 5; DailyGuildModel.TestBusy = i == 6;
            string wire = "version=3\nnonce=" + nonce + "\nsequence=1\ncommand=daily-state-refresh\naction=daily_state_refresh\nexpected_revision=1\noperation_id=" + nonce + "\ntarget_id=0\ndesired_level=0\nmax_requests=0\nmax_money=0\nmax_stone=0\nmax_food=0\nmode=\n";
            if (i == 0) Check(Command.Parse(wire, nonce, 0) != null &&
                Command.Parse(wire.Replace("command=daily-state-refresh", "command=player-state-refresh"), nonce, 0) == null &&
                Command.Parse(wire.Replace("action=daily_state_refresh", "action=player_state_refresh"), nonce, 0) == null &&
                Command.Parse(wire.Replace("max_money=0", "max_money=1"), nonce, 0) == null, "daily_refresh_grammar_disjoint_and_no_spend");
            File.WriteAllText(Path.Combine(DirectoryFor(nonce), "command.txt"), wire);
            Pulse();
            if (i >= 3 && i <= 6)
                Check(Read(nonce, "ack.json").Contains("\"status\":\"REJECTED\""), "daily_refresh_rejects_existing_business_" + i);
            else
                Check(Read(nonce, "ack.json").Contains("\"status\":\"ACCEPTED\"") && PlayerStateRefreshModel.DailyCalls == calls, "daily_refresh_queues_without_invoking");
            if (i == 1) WriteCommand(nonce, 2, "stop");
            if (i == 2) PlayerStateRefreshModel.BeforeDailyPublication = delegate { WriteCommand(nonce, 2, "stop"); };
            if (i == 7) DevelopmentModel.TestRevision = 2;
            GuiPulse(); GuiPulse();
            Check(PlayerStateRefreshModel.DailyCalls == calls + (i == 0 ? 1 : 0), "daily_refresh_consumes_once_and_never_late_" + i);
            if (i == 0)
            {
                Check(object.ReferenceEquals(DailyRefreshProof.CurrentHandler, DailyRefreshProof.Observer), "explicit_daily_refresh_arms_natural_observer");
                WriteCommand(nonce, 2, "disable-restore"); Pulse();
                Check(Read(nonce, "ack.json").Contains("business_operation_unresolved"), "verification_cannot_run_during_daily_refresh");
            }
            PlayerStateRefreshModel.BeforeDailyPublication = null;
            CampaignObserver.TestPending = CampaignModel.TestNavigation = DevelopmentModel.TestPending = DailyGuildModel.TestBusy = false;
            DevelopmentModel.TestRevision = 1;
            if (!UnityEngine.GameObject.LastHelper.PendingDestroy) { WriteCommand(nonce, 3, "stop"); Pulse(); }
            DestroyOwnHelper();
            Check(object.ReferenceEquals(DailyRefreshProof.CurrentHandler, DailyRefreshProof.OriginalHandler), "daily_refresh_stop_restores_natural_observer_" + i);
        }
        PlayerStateRefreshModel.AllowDaily = false;
    }
    private static void DailyNaturalObserverScope()
    {
        for (int explicitDaily = 0; explicitDaily <= 1; explicitDaily++)
        {
            string nonce = Prefix.Substring(0, 24) + (270 + explicitDaily).ToString("x8");
            int observations = DailyRefreshProof.Observations, stops = DailyRefreshProof.Stops;
            Check(Start(nonce).StartsWith("BOOTSTRAP_OK;"), "clock_observer_bootstrap"); Pulse(); Pulse();
            WriteCommand(nonce, 1, "snapshot"); Pulse(); WriteCommand(nonce, 2, "daily-guild-observe"); Pulse();
            Check(DailyRefreshProof.Observations == observations && object.ReferenceEquals(DailyRefreshProof.CurrentHandler, DailyRefreshProof.OriginalHandler),
                "ordinary_connection_does_not_wrap_clock_handler");
            if (explicitDaily != 0)
            {
                WriteCommand(nonce, 3, "daily-observe"); Pulse();
                Check(DailyRefreshProof.Observations == observations + 1 && object.ReferenceEquals(DailyRefreshProof.CurrentHandler, DailyRefreshProof.Observer),
                    "daily_observe_arms_clock_handler_in_same_update");
                WriteCommand(nonce, 4, "heartbeat"); Pulse();
                Check(DailyRefreshProof.Observations > observations + 1, "clock_observer_remains_owned_until_stop");
            }
            WriteCommand(nonce, 5, "stop"); Pulse(); DestroyOwnHelper();
            Check(object.ReferenceEquals(DailyRefreshProof.CurrentHandler, DailyRefreshProof.OriginalHandler) &&
                (explicitDaily == 0 ? DailyRefreshProof.Stops == stops : DailyRefreshProof.Stops > stops), "clock_handler_cleanup_only_after_daily_use");
        }
    }
    private static void DailyRewardDisabled()
    {
        Check(Start(NonceD).StartsWith("BOOTSTRAP_OK;"), "reward_disabled_bootstrap"); Pulse(); Pulse();
        int observations = DailyRefreshProof.Observations;
        WriteCommand(NonceD, 1, "daily-reward-observe"); Pulse();
        Check(Read(NonceD, "state.json").Contains("\"daily_reward_enabled\":false") &&
            Read(NonceD, "ack.json").Contains("\"daily_reward_enabled\":false") &&
            DailyRefreshProof.Observations == observations, "disabled_reward_observation_does_not_wrap_stock_clock");
        int sequence = 2;
        foreach (string action in new[] { "open", "claim", "reward_confirm", "close" })
        {
            WriteReward(NonceD, sequence++, action); Pulse(); GuiPulse();
            Check(Read(NonceD, "ack.json").Contains("daily_reward_disabled") && DailyRewardModel.Calls == 0, "disabled_direct_wire_" + action);
        }
        WriteCommand(NonceD, sequence, "stop"); Pulse(); DestroyOwnHelper();
    }
    private static void DailyTimedDisabled()
    {
        string nonce = Prefix.Substring(0, 24) + 310.ToString("x8");
        Check(Start(nonce).StartsWith("BOOTSTRAP_OK;"), "timed_disabled_bootstrap"); Pulse(); Pulse();
        int observations = DailyRefreshProof.Observations;
        WriteCommand(nonce, 1, "daily-timed-observe"); Pulse();
        Check(Read(nonce, "state.json").Contains("\"daily_timed_enabled\":false") &&
            Read(nonce, "ack.json").Contains("\"daily_timed\":null") &&
            DailyRefreshProof.Observations == observations, "disabled timed observation does not wrap stock clock");
        WriteTimed(nonce, 2, "open"); Pulse(); GuiPulse();
        WriteTimed(nonce, 3, "participate"); Pulse(); GuiPulse();
        Check(Read(nonce, "ack.json").Contains("daily_timed_disabled") && DailyTimedModel.Calls == 0 &&
            Read(nonce, "state.json").Contains("\"invocation_attempted\":false"), "disabled direct wire cannot invoke");
        WriteCommand(nonce, 4, "stop"); Pulse(); DestroyOwnHelper();
    }
    private static void DailyTimedEnabledRules(bool unexpectedDestroy)
    {
        for (int i = 0; i < 3; i++)
        {
            string nonce = Prefix.Substring(0, 24) + (320 + i).ToString("x8");
            Check(Start(nonce).StartsWith("BOOTSTRAP_OK;"), "timed clean bootstrap or reconnect"); Pulse(); Pulse();
            WriteTimed(nonce, 1, "open"); Pulse(); GuiPulse();
            Check(DailyTimedModel.Calls == 0 && Read(nonce, "ack.json").Contains("daily_timed_observation_or_business_pending"),
                "timed requires observation");
            WriteCommand(nonce, 2, "daily-timed-observe"); Pulse();
            Check(Read(nonce, "state.json").Contains("\"daily_timed_enabled\":true") &&
                object.ReferenceEquals(DailyRefreshProof.CurrentHandler, DailyRefreshProof.Observer), "timed enabled uses stock clock proof");
            WriteTimed(nonce, 3, "open"); Pulse();
            DailyTimedModel.AfterFinalSample = delegate
            {
                string published = Read(nonce, "state.json");
                Check(published.Contains("\"phase\":\"owned\"") && published.Contains("\"invocation_attempted\":false") &&
                    published.Contains("\"mutation_attempted\":false"), "timed publication is provisional");
                if (i == 0) WriteCommand(nonce, 4, "stop");
                else if (i == 1)
                {
                    SessionLifecycle life = (SessionLifecycle)typeof(Probe).GetField("life", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(UnityEngine.GameObject.LastHelper.Probe);
                    typeof(SessionLifecycle).GetField("touched", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(life, unchecked((uint)(Environment.TickCount - 20001)));
                }
            };
            if (i == 2) Set("guiQueuedTick", unchecked(Environment.TickCount - 1600));
            GuiPulse(); DailyTimedModel.AfterFinalSample = null;
            Check(DailyTimedModel.Calls == 0 && Read(nonce, "state.json").Contains("\"phase\":\"not_invoked\""),
                "timed stop, lease expiry or GUI deadline prevents dispatch");
            if (i == 2) { WriteCommand(nonce, 4, "stop"); Pulse(); }
            DestroyOwnHelper();
            Check(Read(nonce, "state.json").Contains("\"reconnect_allowed\":true"), "timed pre-call cancellation permits clean reconnect");
        }
        Check(Start(NonceA).StartsWith("BOOTSTRAP_OK;"), "timed pending bootstrap"); Pulse(); Pulse();
        WriteCommand(NonceA, 1, "daily-timed-observe"); Pulse();
        DailyGuildModel.TestBusy = true;
        WriteTimed(NonceA, 2, "open"); Pulse(); GuiPulse();
        DailyGuildModel.TestBusy = false;
        Check(DailyTimedModel.Calls == 0 && Read(NonceA, "ack.json").Contains("daily_timed_observation_or_business_pending"),
            "timed refuses another active business owner");
        WriteTimed(NonceA, 3, "participate"); Pulse(); GuiPulse(); GuiPulse();
        string current = Read(NonceA, "state.json");
        Check(DailyTimedModel.Calls == 1 && current.Contains("\"pending_daily_timed\":true") &&
            current.Contains("\"invocation_attempted\":true") && current.Contains("\"mutation_attempted\":true"), "timed invocation is consumed once");
        string[] commands = { "campaign-command\naction=enter\nexpected_revision=1\ncampaign_id=0\ntroop_id=0\nattempt_id=",
            "challenge-command\naction=enter_challenge\nexpected_revision=1\ncampaign_id=1\ntroop_id=0\nattempt_id=",
            "development-command\naction=equipment_open\nexpected_revision=1\noperation_id=" + NonceA + "\ntarget_id=7\ndesired_level=1\nmax_requests=1\nmax_money=0\nmax_stone=0\nmax_food=0\nmode=enchant",
            "daily-guild-command\naction=open\nexpected_revision=1\ncharacter_id=42\nguild_id=9\ntask_id=18\noperation_id=" + NonceA + "\ndonation_type=0\nmax_premium=0",
            "daily-reward-command\naction=open\nexpected_revision=1\ncharacter_id=42\ncategory_id=2\noperation_id=" + NonceA,
            "daily-state-refresh\naction=daily_state_refresh\nexpected_revision=1\noperation_id=" + NonceA + "\ntarget_id=0\ndesired_level=0\nmax_requests=0\nmax_money=0\nmax_stone=0\nmax_food=0\nmode=",
            "player-state-refresh\naction=player_state_refresh\nexpected_revision=1\noperation_id=" + NonceA + "\ntarget_id=0\ndesired_level=0\nmax_requests=0\nmax_money=0\nmax_stone=0\nmax_food=0\nmode=",
            "disable-restore" };
        int sequence = 4;
        foreach (string command in commands)
        {
            WriteCommand(NonceA, sequence++, command); Pulse();
            Check(Read(NonceA, "ack.json").Contains("daily_timed_operation_unresolved"), "timed busy blocks " + command.Split('\n')[0]);
        }
        if (unexpectedDestroy)
            typeof(Probe).GetMethod("OnDestroy", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(UnityEngine.GameObject.LastHelper.Probe, null);
        else
        {
            WriteCommand(NonceA, sequence, "stop"); Pulse(); DestroyOwnHelper();
        }
        Check(Read(NonceA, "state.json").Contains("\"reconnect_allowed\":false") &&
            Read(NonceA, "recovery_unknown.json").Contains("daily_timed_request_or_cleanup_unresolved_restart_required"),
            "unresolved timed operation requires process restart");
        Check(Start(NonceB).StartsWith("BOOTSTRAP_REJECTED;recovery_unknown"), "timed unknown blocks reconnect");
    }
    private static void DailyRewardEnabledRules(bool unexpectedDestroy)
    {
        Check(Start(NonceA).StartsWith("BOOTSTRAP_OK;"), "reward_enabled_bootstrap"); Pulse(); Pulse();
        WriteReward(NonceA, 1, "open"); Pulse(); GuiPulse();
        Check(DailyRewardModel.Calls == 0 && Read(NonceA, "ack.json").Contains("daily_reward_observation_or_business_pending"), "reward_requires_observation");
        WriteCommand(NonceA, 2, "daily-reward-observe"); Pulse();
        Check(Read(NonceA, "state.json").Contains("\"daily_reward_enabled\":true") &&
            object.ReferenceEquals(DailyRefreshProof.CurrentHandler, DailyRefreshProof.Observer), "reward_observes_stock_clock_when_enabled");
        WriteReward(NonceA, 3, "open"); Pulse();
        Check(Read(NonceA, "ack.json").Contains("\"status\":\"ACCEPTED\"") && DailyRewardModel.Calls == 0, "reward_queue_is_not_invocation");
        WriteCommand(NonceA, 4, "stop"); GuiPulse();
        Check(DailyRewardModel.Calls == 0 && UnityEngine.GameObject.LastHelper.PendingDestroy, "reward_stop_before_gui_wins");
        DestroyOwnHelper();
        Check(Read(NonceA, "state.json").Contains("\"reconnect_allowed\":true"), "reward_clean_reconnect_before_invocation");

        Check(Start(NonceB).StartsWith("BOOTSTRAP_OK;"), "reward_reconnect"); Pulse(); Pulse();
        WriteCommand(NonceB, 1, "daily-reward-observe"); Pulse();
        WriteReward(NonceB, 2, "open"); Pulse();
        Set("guiQueuedTick", unchecked(Environment.TickCount - 1600)); GuiPulse();
        Check(DailyRewardModel.Calls == 0 && Read(NonceB, "ack.json").Contains("scenario_gui_callback_unavailable"), "reward_deadline_cancels_queued_intent");
        WriteReward(NonceB, 3, "open"); Pulse();
        DailyRewardModel.BeforePublication = delegate { WriteCommand(NonceB, 4, "stop"); };
        GuiPulse(); DailyRewardModel.BeforePublication = null;
        Check(DailyRewardModel.Calls == 0 && UnityEngine.GameObject.LastHelper.PendingDestroy, "reward_stop_during_gui_wins");
        DestroyOwnHelper();

        for (int i = 0; i < 4; i++)
        {
            string nonce = Prefix.Substring(0, 24) + (290 + i).ToString("x8");
            Check(Start(nonce).StartsWith("BOOTSTRAP_OK;"), "reward_final_context_bootstrap"); Pulse(); Pulse();
            WriteCommand(nonce, 1, "daily-reward-observe"); Pulse();
            WriteReward(nonce, 2, "open"); Pulse();
            DailyRewardModel.AfterFinalSample = delegate
            {
                string published = Read(nonce, "state.json");
                Check(published.Contains("\"phase\":\"owned\"") && published.Contains("\"invocation_attempted\":false"), "reward_publishes_intent_without_stock_start");
                if (i == 0) WriteCommand(nonce, 3, "stop");
                else if (i == 1)
                {
                    SessionLifecycle life = (SessionLifecycle)typeof(Probe).GetField("life", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(UnityEngine.GameObject.LastHelper.Probe);
                    typeof(SessionLifecycle).GetField("touched", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(life, unchecked((uint)(Environment.TickCount - 20001)));
                }
                else if (i == 3) File.WriteAllText(Path.Combine(DirectoryFor(nonce), "command.txt"), "unreadable_command_fixture");
            };
            DailyRewardModel.RejectAfterPublication = i == 2;
            GuiPulse();
            DailyRewardModel.AfterFinalSample = null;
            DailyRewardModel.RejectAfterPublication = false;
            string finalState = Read(nonce, "state.json");
            Check(DailyRewardModel.Calls == 0 && finalState.Contains("\"phase\":\"not_invoked\"") && finalState.Contains("\"invocation_attempted\":false"),
                "reward_terminal_noninvocation_proven_" + i);
            if (i >= 2)
            {
                string ack = Read(nonce, "ack.json");
                Check(ack.Contains("\"status\":\"REJECTED\"") && ack.Contains(i == 2 ? "fresh_reward_context_rejected" : "daily_reward_gui_final_admission_changed"),
                    "reward_recheck_or_unreadable_mailbox_is_rejected_without_unknown_" + i);
                WriteCommand(nonce, 3, "stop"); Pulse();
            }
            DestroyOwnHelper();
        }

        Check(Start(NonceC).StartsWith("BOOTSTRAP_OK;"), "reward_reconnect_after_gui_cancellation"); Pulse(); Pulse();
        WriteCommand(NonceC, 1, "daily-reward-observe"); Pulse();
        WriteReward(NonceC, 2, "open"); Pulse(); GuiPulse(); GuiPulse();
        Check(DailyRewardModel.Calls == 1, "reward_open_invokes_once");
        string[] commands = { "campaign-command\naction=enter\nexpected_revision=1\ncampaign_id=0\ntroop_id=0\nattempt_id=",
            "challenge-command\naction=enter_challenge\nexpected_revision=1\ncampaign_id=1\ntroop_id=0\nattempt_id=",
            "development-command\naction=equipment_open\nexpected_revision=1\noperation_id=" + NonceC + "\ntarget_id=7\ndesired_level=1\nmax_requests=1\nmax_money=0\nmax_stone=0\nmax_food=0\nmode=enchant",
            "daily-guild-command\naction=open\nexpected_revision=1\ncharacter_id=42\nguild_id=9\ntask_id=18\noperation_id=" + NonceC + "\ndonation_type=0\nmax_premium=0",
            "daily-state-refresh\naction=daily_state_refresh\nexpected_revision=1\noperation_id=" + NonceC + "\ntarget_id=0\ndesired_level=0\nmax_requests=0\nmax_money=0\nmax_stone=0\nmax_food=0\nmode=",
            "player-state-refresh\naction=player_state_refresh\nexpected_revision=1\noperation_id=" + NonceC + "\ntarget_id=0\ndesired_level=0\nmax_requests=0\nmax_money=0\nmax_stone=0\nmax_food=0\nmode=",
            "disable-restore" };
        int sequence = 3;
        foreach (string command in commands)
        {
            WriteCommand(NonceC, sequence++, command); Pulse();
            Check(Read(NonceC, "ack.json").Contains("daily_reward_operation_unresolved"), "reward_busy_rejects_competing_" + command.Split('\n')[0]);
        }
        DailyRewardModel.UnknownAfterClaim = !unexpectedDestroy;
        WriteReward(NonceC, sequence++, "claim"); Pulse(); GuiPulse(); GuiPulse();
        Check(DailyRewardModel.Calls == 2 && Read(NonceC, "state.json").Contains("\"pending_daily_reward\":true") &&
            Read(NonceC, "state.json").Contains("\"invocation_attempted\":true"), "reward_claim_invocation_is_pending");
        Check(Read(NonceC, "ack.json").Contains(unexpectedDestroy ? "\"status\":\"COMPLETED\"" : "\"status\":\"UNKNOWN\""),
            "reward_immediate_uncertainty_is_never_acknowledged_completed");
        if (unexpectedDestroy)
        {
            DailyRewardModel.RejectAfterPublication = true;
            WriteReward(NonceC, sequence, "reward_confirm"); Pulse(); GuiPulse();
            DailyRewardModel.RejectAfterPublication = false;
            Check(Read(NonceC, "ack.json").Contains("\"status\":\"REJECTED\"") && Read(NonceC, "state.json").Contains("\"status\":\"UNKNOWN\""),
                "reward_noninvoked_confirm_is_rejected_even_when_prior_claim_becomes_unknown");
            typeof(Probe).GetMethod("OnDestroy", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(UnityEngine.GameObject.LastHelper.Probe, null);
        }
        else
        {
            WriteCommand(NonceC, sequence, "stop"); Pulse(); DestroyOwnHelper();
        }
        Check(Read(NonceC, "state.json").Contains("\"reconnect_allowed\":false") &&
            Read(NonceC, "ack.json").Contains(unexpectedDestroy ? "\"status\":\"REJECTED\"" : "\"status\":\"UNKNOWN\"") &&
            Read(NonceC, "recovery_unknown.json").Contains("daily_reward_request_unresolved_restart_required"), "reward_unresolved_durable_restart_required");
        Check(Start(NonceD).StartsWith("BOOTSTRAP_REJECTED;recovery_unknown"), "reward_unknown_blocks_reconnect");
    }
#endif
#if JSB_STONE_PROBE_TEST
    private static void WriteStone(string nonce, int sequence, string action, int revision, string role = "45")
    {
        WriteCommand(nonce, sequence, "daily-stone-command\naction=" + action + "\noperation_id=" + nonce +
            "\ncharacter_id=" + role + "\ntask_id=16\nexpected_revision=" + revision);
    }

    private static int StoneRevision(string nonce)
    {
        string state = Read(nonce, "state.json");
        const string prefix = "\"daily_stone\":{\"schema\":1,\"revision\":";
        int start = state.IndexOf(prefix, StringComparison.Ordinal);
        Check(start >= 0, "stone_state_revision");
        start += prefix.Length;
        return Int32.Parse(state.Substring(start, state.IndexOf(',', start) - start), CultureInfo.InvariantCulture);
    }

    private static void DailyStoneRules(string ending)
    {
        string nonce = NonceA;
        string wire = "version=3\nnonce=" + nonce + "\nsequence=1\ncommand=daily-stone-command\naction=dig\noperation_id=" + nonce +
            "\ncharacter_id=45\ntask_id=16\nexpected_revision=1\n";
        Command parsed = Command.Parse(wire, nonce, 0);
        Check(parsed != null && parsed.DailyStoneTaskId == 16 && parsed.DailyStoneCharacterId == 45 &&
            parsed.DailyStoneAction == "dig" && parsed.ExpectedRevision == 1, "stone_fixed_contract");
        foreach (string invalid in new[] { wire.Replace("action=dig", "action=buy"), wire.Replace("task_id=16", "task_id=7"),
            wire.Replace("character_id=45", "character_id=045"), wire.Replace("task_id=16", "task_id=016"),
            wire.Replace("operation_id=" + nonce, "operation_id=" + nonce.ToUpperInvariant()), wire + "opcode=825\n",
            wire.Replace("command=daily-stone-command", "command=daily-stone-observe") })
            Check(Command.Parse(invalid, nonce, 0) == null, "stone_strict_wire");
        DailyStoneTests.ResetGame();
        string bootstrap = Start(nonce);
        Check(bootstrap.StartsWith("BOOTSTRAP_OK;"), "stone_bootstrap:" + bootstrap);
        Pulse(); Pulse();
        WriteStone(nonce, 1, "open", 1); Pulse(); GuiPulse();
        Check(MainUI.Opens == 0 && Read(nonce, "ack.json").Contains("daily_stone_" +
            (ending == "disabled" ? "disabled" : "observation_or_business_pending")), "stone_requires_opt_in_and_observe");
        WriteCommand(nonce, 2, "daily-stone-observe"); Pulse();
        string initial = Read(nonce, "state.json");
        Check(initial.Contains("\"daily_reward_enabled\":false") && initial.Contains("\"daily_timed_enabled\":false"), "stone_opt_in_is_independent");
        if (ending == "disabled")
        {
            Check(initial.Contains("\"daily_stone_enabled\":false,\"daily_stone\":null"), "stone_default_off");
            WriteStone(nonce, 3, "dig", 1); Pulse(); GuiPulse();
            WriteStone(nonce, 4, "close", 1); Pulse(); GuiPulse();
            Check(Read(nonce, "ack.json").Contains("daily_stone_disabled") && MainUI.Opens == 0 &&
                MainUI.Panel.Requests == 0 && MainUI.Closes == 0, "stone_disabled_wire_never_invokes");
            WriteCommand(nonce, 5, "stop"); Pulse(); DestroyOwnHelper();
            return;
        }
        Check(initial.Contains("\"daily_stone_enabled\":true") && initial.Contains("daily_refresh_unverified"), "stone_unverified_clock_blocks");
        Check(DailyRefreshProof.RecordSnapshot(Global.character_, DailyTransport.Read(Global.m_client), 218000, 304400),
            "stone_fixture_current_epoch");
        Pulse();
        WriteStone(nonce, 3, "open", StoneRevision(nonce), "46"); Pulse(); GuiPulse();
        Check(MainUI.Opens == 0 && Read(nonce, "ack.json").Contains("stone_admission"), "stone_wrong_role_rejected");
        WriteStone(nonce, 4, "open", StoneRevision(nonce)); Pulse();
        Check(MainUI.Opens == 0 && Read(nonce, "state.json").Contains("\"phase\":\"queued\",\"invocation_attempted\":false"),
            "stone_queue_is_provisional");
        if (ending == "pre-open-stop") { CheckStoneFinalGate(nonce, ending, true); return; }
        if (ending == "open-commit-overlay")
        {
            WatchStoneCommit(nonce, delegate { PopupUI.popup = true; });
            GuiPulse();
            Check(PopupUI.popup && MainUI.Opens == 0 && MainUI.m_subui == null &&
                Read(nonce, "state.json").Contains("\"phase\":\"not_invoked\",\"invocation_attempted\":false"),
                "stone_final_overlay_blocks_open");
            EndStoneSession(nonce, 5, false, true);
            return;
        }
        GuiPulse(); GuiPulse();
        Check(MainUI.Opens == 1 && Read(nonce, "ack.json").Contains("\"status\":\"OPENING\""), "stone_open_once");
        MainUI.Panel.LoadComplete(); Pulse();
        if (ending == "stale-open" || ending == "open-dismissed" || ending == "open-stop")
        {
            if (ending == "stale-open")
            {
                MainUI.Panel.visible_time = MainUI.Panel.visible_time.AddTicks(1); Pulse();
                Check(Read(nonce, "state.json").Contains("stone_owned_panel_changed") &&
                    Read(nonce, "state.json").Contains("\"pending_daily_stone\":true"), "stone_stale_open_still_owned");
            }
            if (ending == "open-dismissed") { MainUI.SetSubUI(null); Pulse(); }
            EndStoneSession(nonce, 5, false, ending == "open-dismissed");
            Check(MainUI.Panel.Requests == 0, "stone_no_dig_when_stopped_after_open");
            return;
        }
        WriteStone(nonce, 5, "dig", StoneRevision(nonce)); Pulse();
        if (ending == "final-stop" || ending == "final-expiry" || ending == "dig-open-expiry")
            CheckStoneFinalGate(nonce, ending, false);
        else if (ending == "dig-commit-stream" || ending == "dig-commit-disconnect")
            CheckStoneCommitTransport(nonce, ending == "dig-commit-stream");
        else if (ending == "dig-commit-stop" || ending == "dig-commit-heartbeat" || ending == "dig-commit-replace")
            CheckStoneCommitCommand(nonce, ending);
        else CheckStoneDispatched(nonce, ending);
    }

    private static void WatchStoneCommit(string nonce, Action change)
    {
        int ownedReads = 0;
        Global.character_.BeforeFeatureCheck = delegate
        {
            // The second owned read is the full sample immediately before
            // the final exact command-file check.
            if (Read(nonce, "state.json").Contains("\"phase\":\"owned\"") && ++ownedReads == 2) change();
        };
    }

    private static void CheckStoneCommitTransport(string nonce, bool replaceStream)
    {
        bool heldMonitor = false;
        WatchStoneCommit(nonce, delegate
        {
            game.net.tcp_client client = Global.m_client;
            Thread network = new Thread(delegate()
            {
                if (Monitor.TryEnter(client, 0)) Monitor.Exit(client);
                else heldMonitor = true;
                if (replaceStream) client.m_stream = new MemoryStream();
                else client.m_bConnectLost = true;
            });
            network.Start(); network.Join();
        });
        GuiPulse(); GuiPulse();
        string state = Read(nonce, "state.json");
        Check(heldMonitor && MainUI.Panel.Requests == 0 && MainUI.Panel.PaidRequests == 0 && MainUI.Panel.visible &&
            state.Contains("\"phase\":\"not_invoked\",\"invocation_attempted\":false") &&
            state.Contains("\"pending_daily_stone\":true"), "stone_transport_can_change_under_monitor_before_commit");
        Check(((game.net.tcp_base.Handler)Global.m_client.m_handlers[(ushort)355]).handler.Method.Name == "handelActvity",
            "stone_transport_rejection_restores_stock_push");
        EndStoneSession(nonce, 6, false, false);
    }

    private static void CheckStoneCommitCommand(string nonce, string ending)
    {
        bool replaced = false;
        WatchStoneCommit(nonce, delegate
        {
            if (ending == "dig-commit-stop") WriteCommand(nonce, 6, "stop");
            else if (ending == "dig-commit-heartbeat") WriteCommand(nonce, 6, "heartbeat");
            else
            {
                string changed = Read(nonce, "command.txt").Replace("operation_id=" + nonce, "operation_id=" + NonceB);
                File.WriteAllText(Path.Combine(DirectoryFor(nonce), "command.txt"), changed);
                Command parsed = Command.Parse(changed, nonce, 0);
                Check(parsed != null && parsed.Sequence == 5 && parsed.OperationId == NonceB, "same_sequence_changed_tuple_is_well_formed");
            }
            replaced = true;
        });
        GuiPulse(); GuiPulse();
        string state = Read(nonce, "state.json");
        Check(replaced && MainUI.Panel.Requests == 0 && MainUI.Panel.visible &&
            state.Contains("\"phase\":\"not_invoked\",\"invocation_attempted\":false") &&
            state.Contains("\"pending_daily_stone\":true"), "stone_replaced_command_during_final_sample_never_invokes");
        Check(((game.net.tcp_base.Handler)Global.m_client.m_handlers[(ushort)355]).handler.Method.Name == "handelActvity",
            "stone_replaced_command_restores_stock_push");
        if (ending == "dig-commit-stop")
        {
            DestroyOwnHelper();
            CheckStoneReconnect(nonce, false);
        }
        else EndStoneSession(nonce, 7, false, false);
    }

    private static void CheckStoneFinalGate(string nonce, string ending, bool beforeOpen)
    {
        bool sampled = false;
        Global.character_.BeforeFeatureCheck = delegate
        {
            if (sampled || !Read(nonce, "state.json").Contains("\"phase\":\"owned\",\"invocation_attempted\":false")) return;
            sampled = true;
            if (ending == "dig-open-expiry") ExpireStone();
            else if (ending == "final-expiry")
            {
                object life = typeof(Probe).GetField("life", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(UnityEngine.GameObject.LastHelper.Probe);
                typeof(SessionLifecycle).GetField("touched", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(life, unchecked((uint)(Environment.TickCount - 20001)));
            }
            else WriteCommand(nonce, beforeOpen ? 5 : 6, "stop");
        };
        GuiPulse();
        Check(sampled && MainUI.Panel.Requests == 0 && MainUI.Opens == (beforeOpen ? 0 : 1) &&
            Read(nonce, "state.json").Contains("\"phase\":\"not_invoked\",\"invocation_attempted\":false"),
            "stone_stop_or_lease_expiry_during_final_ui_sample");
        Check(((game.net.tcp_base.Handler)Global.m_client.m_handlers[(ushort)355]).handler.Method.Name == "handelActvity",
            "stone_pre_call_cancellation_restores_stock_push");
        if (ending == "dig-open-expiry")
        {
            Check(Read(nonce, "ack.json").Contains("stone_open_timed_out") && MainUI.Panel.visible &&
                Read(nonce, "state.json").Contains("\"pending_daily_stone\":true"), "stone_expired_open_retains_owned_panel");
            WriteCommand(nonce, 6, "disable-restore"); Pulse();
            Check(Read(nonce, "ack.json").Contains("daily_stone_operation_unresolved"), "stone_expired_open_interlocks_other_mutations");
            MainUI.SetSubUI(null); Pulse();
            Check(Read(nonce, "state.json").Contains("\"pending_daily_stone\":false"), "stone_exact_hidden_panel_discharges_ownership");
            EndStoneSession(nonce, 7, false, true);
            return;
        }
        DestroyOwnHelper();
        CheckStoneReconnect(nonce, beforeOpen);
    }

    private static void CheckStoneDispatched(string nonce, string ending)
    {
        GuiPulse(); GuiPulse();
        string ack = Read(nonce, "ack.json");
        Check(MainUI.Panel.Requests == 1 && MainUI.Panel.PaidRequests == 0 &&
            ack.Contains("\"status\":\"COMPLETED\",\"reason\":\"\"") && ack.Contains("\"status\":\"PENDING\"") &&
            ack.Contains("\"account_progress_observed\":false"), "stone_gui_ack_is_not_business_success");
        string[] otherBusiness = { "disable-restore", "campaign-command\naction=enter\nexpected_revision=1\ncampaign_id=0\ntroop_id=0\nattempt_id=",
            "challenge-command\naction=enter_challenge\nexpected_revision=1\ncampaign_id=1\ntroop_id=0\nattempt_id=",
            "development-command\naction=equipment_open\nexpected_revision=1\noperation_id=" + nonce + "\ntarget_id=7\ndesired_level=1\nmax_requests=1\nmax_money=0\nmax_stone=0\nmax_food=0\nmode=enchant",
            "daily-guild-command\naction=open\nexpected_revision=1\ncharacter_id=42\nguild_id=9\ntask_id=18\noperation_id=" + nonce + "\ndonation_type=0\nmax_premium=0",
            "daily-reward-command\naction=open\nexpected_revision=1\ncharacter_id=42\ncategory_id=2\noperation_id=" + nonce,
            "daily-timed-command\naction=open\noperation_id=" + nonce + "\ncharacter_id=45\ntask_id=13\nexpected_revision=1",
            "daily-state-refresh\naction=daily_state_refresh\nexpected_revision=1\noperation_id=" + nonce + "\ntarget_id=0\ndesired_level=0\nmax_requests=0\nmax_money=0\nmax_stone=0\nmax_food=0\nmode=",
            "player-state-refresh\naction=player_state_refresh\nexpected_revision=1\noperation_id=" + nonce + "\ntarget_id=0\ndesired_level=0\nmax_requests=0\nmax_money=0\nmax_stone=0\nmax_food=0\nmode=" };
        int sequence = 6;
        foreach (string command in otherBusiness)
        {
            WriteCommand(nonce, sequence++, command); Pulse();
            Check(Read(nonce, "ack.json").Contains("daily_stone_operation_unresolved"), "stone_interlock_" + command.Split('\n')[0]);
        }
        WriteStone(nonce, sequence++, "dig", StoneRevision(nonce)); Pulse(); GuiPulse();
        Check(MainUI.Panel.Requests == 1, "stone_pending_never_replays");
        if (ending == "refusal")
        {
            Global.m_client.Reply(new byte[] { 8, 1, 16, 0 }, true); Pulse();
            Check(Read(nonce, "state.json").Contains("stone_server_rejected") &&
                Read(nonce, "state.json").Contains("\"pending_daily_stone\":false") &&
                MainUI.Panel.RefusalBoxOpened && Global.character_.sliver_pick == 3, "stone_confirmed_refusal_can_end_cleanly");
            EndStoneSession(nonce, sequence, false, true);
            return;
        }
        if (ending == "late-settlement")
        {
            ExpireStone();
            DailyStoneTests.Progress();
            Global.m_client.Reply(new byte[] { 8, 0, 16, 1 }, true);
            Pulse();
            Check(Read(nonce, "state.json").Contains("stone_settlement_timed_out") &&
                Read(nonce, "state.json").Contains("\"account_progress_observed\":false") &&
                MainUI.Panel.Requests == 1 && MainUI.Panel.Replies == 1 && RootBase.ActivityReplies == 1,
                "stone_late_stock_callbacks_forward_without_settlement");
            EndStoneSession(nonce, sequence, false, false);
            return;
        }
        if (ending != "stop" && ending != "destroy")
        {
            DailyStoneTests.Progress();
            Global.character_.acts[16u] = new Database.Activity { condition_param = 1 };
            Global.m_client.Reply(new byte[] { 8, 0, 16, 1 }, true);
            MainUI.Panel.showTimer = DateTime.MinValue;
            Pulse();
            string state = Read(nonce, "state.json");
            Check(state.Contains("\"status\":\"SETTLED\"") && state.Contains("\"account_progress_observed\":true") &&
                RootBase.ActivityReplies == 1 && MainUI.Panel.Replies == 1 && Global.character_.sliver_pick == 2 &&
                Global.character_.gold == 100 && Global.character_.bind_gold == 5, "stone_stock_355_106_825_and_no_premium");
            foreach (string command in otherBusiness)
            {
                WriteCommand(nonce, sequence++, command); Pulse();
                Check(Read(nonce, "ack.json").Contains("daily_stone_operation_unresolved"), "stone_cleanup_interlock_" + command.Split('\n')[0]);
            }
            CheckStoneCleanup(nonce, sequence, ending);
            return;
        }
        EndStoneSession(nonce, sequence, ending == "destroy", false);
        Global.m_client.Reply(new byte[] { 8, 0, 16, 1 }, true);
        Check(MainUI.Panel.Requests == 1 && MainUI.Panel.Replies == 1 && MainUI.Closes == 0, "stone_destroy_or_stop_preserves_stock_reply");
    }

    private static void ExpireStone()
    {
        Probe probe = UnityEngine.GameObject.LastHelper.Probe;
        DailyStoneTests.Expire((DailyStoneModel)typeof(Probe).GetField("dailyStone", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(probe));
    }

    private static void CheckStoneCleanup(string nonce, int sequence, string ending)
    {
        bool calledClose = ending == "settle" || ending == "close-noop" || ending == "close-throw" || ending == "close-race";
        __BaseUI foreign = new __BaseUI { visible = true };
        if (ending == "close-noop") MainUI.CloseDoesNothing = true;
        if (ending == "close-throw") MainUI.ThrowAfterClose = true;
        if (ending == "close-race") MainUI.BeforeClose = delegate { Global.character_.acts[16u] = new Database.Activity(); };
        if (ending == "cleanup-expiry") ExpireStone();
        if (ending == "stale-rows")
            ((Database.Activity_Reward)Global.character_.act_rewards[2u]).acts[16u] = new Database.Activity { condition_param = 1, refresh_time = 304401 };
        if (ending == "cleanup-frame-failure") Global.character_.act_rewards.Remove(2u);
        if (calledClose || ending == "stale-rows" || ending == "close-final-expiry" || ending == "close-commit-foreign")
        {
            WriteStone(nonce, sequence++, "close", StoneRevision(nonce)); Pulse();
            if (ending == "close-final-expiry") Global.character_.BeforeFeatureCheck = delegate
            {
                if (Read(nonce, "state.json").Contains("\"phase\":\"owned\"")) ExpireStone();
            };
            if (ending == "close-commit-foreign") WatchStoneCommit(nonce, delegate { MainUI.m_subui = foreign; });
            GuiPulse(); GuiPulse();
            Check(MainUI.Closes == (calledClose ? 1 : 0), "stone_close_invocation_count");
            if (ending == "close-commit-foreign")
                Check(ReferenceEquals(MainUI.m_subui, foreign) && foreign.visible && MainUI.Panel.visible &&
                    Read(nonce, "state.json").Contains("\"invocation_attempted\":false"), "stone_commit_never_closes_foreign_subui");
        }
        else if (ending != "cleanup-stop" && ending != "cleanup-destroy") Pulse();
        if (ending == "settle")
            Check(Read(nonce, "state.json").Contains("\"panel_closed\":true"), "stone_owned_cleanup");
        else if (ending != "cleanup-stop" && ending != "cleanup-destroy")
        {
            string unresolved = Read(nonce, "state.json");
            Check(unresolved.Contains("\"status\":\"UNKNOWN\"") && unresolved.Contains("\"pending_daily_stone\":true"),
                "stone_cleanup_is_already_blocked_before_stop_" + ending);
        }
        EndStoneSession(nonce, sequence, ending == "cleanup-destroy", ending == "settle");
        if (ending != "settle")
        {
            string final = Read(nonce, "state.json");
            Check(final.Contains("\"status\":\"UNKNOWN\"") && final.Contains("\"account_progress_observed\":true") &&
                final.Contains("\"panel_closed\":false"), "stone_cleanup_witnesses_remain_uncertain_" + ending);
        }
    }

    private static void EndStoneSession(string nonce, int sequence, bool unexpectedDestroy, bool reconnect)
    {
        if (unexpectedDestroy)
            typeof(Probe).GetMethod("OnDestroy", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(UnityEngine.GameObject.LastHelper.Probe, null);
        else { WriteCommand(nonce, sequence, "stop"); Pulse(); DestroyOwnHelper(); }
        CheckStoneReconnect(nonce, reconnect);
    }

    private static void CheckStoneReconnect(string nonce, bool reconnect)
    {
        string final = Read(nonce, "state.json");
        Check(final.Contains("\"reconnect_allowed\":" + (reconnect ? "true" : "false")), "stone_reconnect_witness");
        if (reconnect)
        {
            Check(Start(NonceB).StartsWith("BOOTSTRAP_OK;"), "stone_verified_cleanup_permits_rebootstrap");
            Pulse(); Pulse(); WriteCommand(NonceB, 1, "stop"); Pulse(); DestroyOwnHelper();
        }
        else Check(Read(nonce, "recovery_unknown.json").Contains("daily_stone_request_or_callback_unresolved_restart_required") &&
            Start(NonceB).StartsWith("BOOTSTRAP_REJECTED;recovery_unknown"), "stone_unresolved_requires_restart");
    }
#endif
    public static int Main(string[] args)
    {
        testRoot = args[0]; Directory.CreateDirectory(testRoot);
        try
        {
#if JSB_STONE_PROBE_TEST
            DailyStoneRules(args[1]);
#else
            if (args.Length > 1 && args[1].StartsWith("timed-")) DailyTimedEnabledRules(args[1] == "timed-destroy");
            else if (args.Length > 1) DailyRewardEnabledRules(args[1] == "destroy");
            else { PureLifecycle(); Rotation(); GuiQueueRules(); DevelopmentGuiRules(); DailyRefreshGuiRules(); DailyNaturalObserverScope(); DailyRewardDisabled(); DailyTimedDisabled(); RealProbeWithSyntheticUnity(); }
#endif
            Console.WriteLine("{\"status\":\"PASS\",\"checks\":" + checks + ",\"scope\":\"actual production Probe lifecycle with owned synthetic Unity/model; no game assemblies/process\"}"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
#endif
