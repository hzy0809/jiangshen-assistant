// Detached fixtures. Production model and callback capture, simulated Unity
// owners and dispatcher, fixed wire payloads from the pinned protobuf schema.
using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;
using JiangShen.AssistantRuntime;

namespace UnityEngine
{
    public class Object
    {
        private static int next;
        private readonly int id = ++next;
        public int GetInstanceID() { return id; }
#if JSB_ARENA_PROBE_TEST
        public static void DontDestroyOnLoad(Object value) { }
        public static void Destroy(Object value) { ((GameObject)value).PendingDestroy = true; }
#endif
    }
    public class Transform { public Transform root; }
    public class GameObject : Object
    {
        public bool activeInHierarchy = true;
        public readonly Transform transform = new Transform();
#if JSB_ARENA_PROBE_TEST
        public static GameObject LastHelper;
        public bool PendingDestroy;
        public HideFlags hideFlags;
        public Probe Probe;
        public GameObject(string name = "")
        {
            transform.root = transform;
            if (name.StartsWith("JSAssistantRuntime_", StringComparison.Ordinal)) LastHelper = this;
        }
        public T AddComponent<T>() where T : Component, new()
        { T value = new T(); value.gameObject = this; Probe = value as Probe; return value; }
        public Component GetComponent(string name) { return null; }
#else
        public GameObject() { transform.root = transform; }
#endif
    }
    public class Component : Object { public GameObject gameObject; public Transform transform { get { return gameObject.transform; } } }
    public class Behaviour : Component { public bool enabled = true; }
    public class Event { public static Event current = new Event(); }
    public static class GUI { public static bool enabled = true; }
    public static class Time { public static int frameCount; }
#if JSB_ARENA_PROBE_TEST
    public enum HideFlags { HideInHierarchy = 1 }
    public class MonoBehaviour : Behaviour { }
#endif
}

namespace JiangShen.AssistantRuntime
{
    internal sealed class ProbeFailure : Exception { internal ProbeFailure(string why) : base(why) { } }
    internal sealed class Model
    {
        internal static Type GameType(string name) { return Required(name); }
        internal static Type FirstType(string name)
        {
            if (name == "game.net.tcp_base+Handler") return typeof(ArenaHandler);
            if (name == "game.net.tcp_base" || name == "game.net.tcp_client") return typeof(ArenaFixtureClient);
            return Required(name);
        }
        private static Type Required(string name) { return Type.GetType(name, true); }
#if JSB_ARENA_PROBE_TEST
        internal static readonly string[] ResourceNames = new[] { "gold" };
        internal Snapshot Capture(UnityEngine.Component expected = null)
        {
            ArenaFixture f = ArenaFixture.Active;
            string gate = !Global.extern_enabled ? "global_input_disabled" : MainUI.m_subui == null ? "PASS" : "subui_present";
            return new Snapshot { Main = f.Main, Root = CurrentRoot(), Character = Global.character_, Gate = gate,
                Safe = gate == "PASS", InteractionEnabled = f.Main.interactive, ResourceValues = new long[] { f.Role.gold } };
        }
        internal UnityEngine.Component CurrentRoot() { return BaseAsset.lds.currmg; }
        internal UnityEngine.GameObject GlobalUiObject() { return ArenaFixture.Active.GlobalUi.gameObject; }
        internal UnityEngine.Object[] AllUi() { return ArenaFixture.Active.AllUi(); }
        internal string OverlayGate(UnityEngine.Behaviour ui) { return "foreign_overlay"; }
        internal void SetInteraction(Snapshot original, bool restored) { ((MainUI)original.Main).interactive = restored; }
#endif
    }
#if JSB_ARENA_PROBE_TEST
    internal sealed class Snapshot
    {
        internal UnityEngine.Component Main, Root;
        internal object Character;
        internal string Gate;
        internal bool Safe, InteractionEnabled;
        internal long[] ResourceValues;
        internal int InstanceId { get { return Main.GetInstanceID(); } }
        internal string Resources { get { return "{}"; } }
        internal bool SameTarget(Snapshot other) { return other != null && Main == other.Main; }
        internal string Json() { return "\"gate\":" + Command.Quote(Gate); }
    }
#endif
}

namespace Database
{
    public sealed class _arena_bonus { }
    public class _character_arena_bonus { public uint time, rank; }
    public sealed class WrongArenaBonus : _character_arena_bonus { }
    public sealed class Arena
    {
        public static bool TemplateAvailable = true;
        public byte got;
        public List<_character_arena_bonus> bonuses = new List<_character_arena_bonus>();
        public static _arena_bonus getArenaBonus(uint rank) { return TemplateAvailable && rank == 1 ? new _arena_bonus() : null; }
    }
    // Pinned db_template1 row 3 has idreward=6 and cond_param=1.
    public sealed class ActivityTemplate { public byte id = 3, idreward = 6; public bool active = true; public int cond_param = 1; }
    public sealed class Activity
    {
        public byte idactivity = 3, condition_param;
        public int refresh_time;
        public ActivityTemplate temp;
    }
    public sealed class Character
    {
        public ulong id = 1701;
        public uint next_resource_time;
        public int gold = 500, bind_gold;
        public uint gold_consume;
        public bool gm;
        public readonly Arena arena = new Arena();
        public readonly Dictionary<uint, Activity> acts = new Dictionary<uint, Activity>();
        public readonly Dictionary<uint, Activity_Reward> act_rewards = new Dictionary<uint, Activity_Reward>();
    }
    public sealed class Activity_Reward
    {
        public byte idact_reward = 6;
        public readonly Dictionary<uint, Activity> acts = new Dictionary<uint, Activity>();
    }
}

public static class Global
{
    public static ArenaFixtureClient m_client;
    public static Database.Character character_;
    public static bool extern_enabled = true, relogin;
    public static Database.ActivityTemplate Template;
    public static Action BeforeTemplate;
    public static object getTemplate(string name, uint id)
    { if (BeforeTemplate != null) BeforeTemplate(); return name == "activity" && id == 3 ? Template : null; }
}
public static class TimeUtil { public static uint _now, _thisDay; }
public sealed class LoadScreen
{
    public UnityEngine.Component currmg;
    public bool visible;
    public bool isVisible() { return visible; }
}
public class BaseAsset : UnityEngine.Behaviour
{
    public static LoadScreen lds;
    public List<object> asset_names = new List<object>(), asset_names_sub = new List<object>();
}
public static class RootBase
{
    public static void handelActvity(MemoryStream stream, object arg)
    { ArenaFixture.Active.ApplyActivityPush(); }
}
public class __BaseUI : BaseAsset
{
    private static long visibilityTicks;
    public delegate void delegate_OnClose();
    public delegate_OnClose OnClose;
    public static BattleResultUI battle_result_ui;
    public static bool my_mouseclick;
    public UnityEngine.Component rmg;
    public bool loading, collecting, interactive = true;
    public Action OnDisable;
    public DateTime visible_time;
    public bool IsVisible() { return enabled; }
    public bool IsEnable() { return interactive; }
    public void Enable() { interactive = true; }
    public void Disable() { interactive = false; if (OnDisable != null) OnDisable(); }
    public void DisableAll() { Disable(); Global.extern_enabled = my_mouseclick = false; }
    public void EnableAll() { interactive = true; Global.extern_enabled = my_mouseclick = true; }
    public virtual void Hide() { enabled = false; }
    public void Visible()
    {
        if (!enabled) visible_time = new DateTime(++visibilityTicks);
        enabled = true;
    }
}
public class BaseUI : __BaseUI { public static RootUI rootUI; }
public class RootUI : BaseUI
{
    public static ArenaFixtureClient m_client;
    public static bool m_bRefreshFive;
    public string LevelID = "Campaign";
}
public class MainUI : BaseUI
{
    public bool first, m_bInit = true;
    public static __BaseUI m_subui;
    public static void SetSubUI(__BaseUI ui)
    {
        ArenaFixture fixture = ArenaFixture.Active;
        if (ui == null && fixture.ThrowOnClose) throw new InvalidOperationException("fixture stock close failure");
        if (ui == null && fixture.NoOpClose) return;
        if (m_subui != null && m_subui != ui) m_subui.Hide();
        m_subui = ui;
    }
    public static void _battle_arena() { ArenaFixture.Active.ShowPanel(ArenaFixture.Active.Select); }
}
#if JSB_ARENA_PROBE_TEST
public static class BuildingBuildUI { public static bool m_bBuildVisible; }
public static class PopupUI { public static bool popup; }
public static class BuildingBehaviourScript { public static bool m_bEditVisible; }
public class CampaignUI : BaseUI { }
#endif
public class ArenaSelectUI : BaseUI
{
    public static void arenaMyLoading() { ArenaFixture.Active.OpenArena(); }
}
public class ArenaChestUI : BaseUI
{
    public Database.Arena m_arena;
    public int BonusCloseCalls;
    public void MyLoading(delegate_OnClose close)
    { OnClose = close; m_arena = Global.character_.arena; ArenaFixture.Active.ShowPanel(this); }
    private void BonusGetClose() { BonusCloseCalls++; interactive = true; if (m_arena.bonuses.Count == 0) { Hide(); OnClose(); } }
}
public class BattleResultUI : BaseUI
{
    public Action OnHide;
    public override void Hide() { base.Hide(); if (OnHide != null) OnHide(); }
    public void FinishLoading() { loading = false; Visible(); }
    public void Confirm() { Hide(); OnClose(); }
}
public class MessageBox : BaseUI { public void Confirm() { Hide(); OnClose(); } }
public class ArenaUI : BaseUI
{
    public Database.Arena m_arena;
    public uint RequestTime, ResponseTime;
    public int Requests, OriginalCalls, ImplicitCloses, BonusCloseCalls, ResponseGold, ResponseResult;
    public bool FindBack, RemoveOnResponse = true, DeferRewardShow;
    public Exception CallbackFailure;
    public ArenaHandler.Callback LastStockHandler;
    private void SubClose() { ArenaFixture.Active.OpenArena(); }
    private void BonusGetClose() { BonusCloseCalls++; EnableAll(); }
    public void cs_arena_bonusget_0x462(bool bFindBack, int idx, delegate_OnClose close)
    {
        Requests++; FindBack = bFindBack; RequestTime = Global.character_.arena.bonuses[idx].time;
        LastStockHandler = sc_arena_bonusget_0x462;
        RootUI.m_client.m_handlers[1122] = new ArenaHandler(LastStockHandler, close);
    }
    private void sc_arena_bonusget_0x462(MemoryStream stream, object arg)
    {
        OriginalCalls++;
        delegate_OnClose close = (delegate_OnClose)arg;
        if (stream == null) { ImplicitCloses++; close(); return; }
        if (ResponseResult != 0)
        {
            MessageBox error = ArenaFixture.Active.Error;
            error.OnClose = close; Global.extern_enabled = my_mouseclick = false; error.Visible();
            return;
        }
        if (!RemoveOnResponse) return;
        Global.character_.gold -= ResponseGold;
        Database._character_arena_bonus bonus = m_arena.bonuses.Find(item => item.time == ResponseTime);
        if (bonus != null) m_arena.bonuses.Remove(bonus);
        if (bonus == null || Database.Arena.getArenaBonus(bonus.rank) == null)
        {
            ImplicitCloses++;
            close();
            m_arena.got = 1; // The pinned handler writes this after the direct close.
            return;
        }
        m_arena.got = 1;
        battle_result_ui.OnClose = close;
        Global.extern_enabled = my_mouseclick = false;
        if (DeferRewardShow) battle_result_ui.loading = true;
        else battle_result_ui.FinishLoading();
        if (CallbackFailure != null) throw CallbackFailure;
    }
}
public sealed class ArenaHandler
{
    public delegate void Callback(MemoryStream stream, object arg);
    public Callback handler;
    public object arg;
    public float life;
    public ArenaHandler(Callback cb, object value, float lifetime = 10f) { handler = cb; arg = value; life = lifetime; }
}
public sealed class ArenaFixtureClient
{
    public enum ConnectionState { PREPARE, WAIT, RECONNECTING, OK }
    public sealed class Socket
    {
        public Action BeforeConnected;
        private bool connected;
        public bool Connected { get { if (BeforeConnected != null) BeforeConnected(); return connected; } set { connected = value; } }
    }
    public sealed class Message { public ushort op; }
    public static ConnectionState connection_state;
    public Socket m_client = new Socket { Connected = true };
    public Stream m_stream = new MemoryStream();
    public bool m_bConnectLost;
    public readonly Dictionary<ushort, ArenaHandler> m_handlers = new Dictionary<ushort, ArenaHandler>();
    public readonly List<Message> messages = new List<Message>();
}
public sealed class ArenaFixture
{
    internal const string Id = "1234567890abcdef1234567890abcdef";
    internal const uint Day = 1000000, Free = 1072000, Old = 985600;
    internal static ArenaFixture Active;
    internal readonly DailyArenaChestModel Model;
    internal readonly RootUI Root = new RootUI(), GlobalUi = new RootUI();
    internal readonly MainUI Main = new MainUI();
    internal readonly ArenaSelectUI Select = new ArenaSelectUI();
    internal readonly ArenaUI ArenaUi = new ArenaUI();
    internal readonly ArenaChestUI Chest = new ArenaChestUI();
    internal readonly BattleResultUI Dialog = new BattleResultUI();
    internal readonly MessageBox Error = new MessageBox();
    internal readonly ArenaFixtureClient Client = new ArenaFixtureClient();
    internal readonly Database.Character Role = new Database.Character();
    internal int Sequence, Publications, ActivityPushCalls, LoaderCalls, CommitCalls;
    internal int CommitQuota = Int32.MaxValue;
    internal Action AfterCommitRead;
    internal bool DeferArenaShow, ThrowOnClose, NoOpClose, AllowCommit = true;
    internal BaseUI DeferredPanel, ThrowingLoader;
    internal string ExternalGate = "PASS";
    internal byte PushCount;
    internal int PushRefresh;
    internal bool PushLatest, PushListed, ThrowActivityPush;
    internal ArenaFixture(bool current, bool old) : this(current, old, false) { }
    internal ArenaFixture(bool current, bool old, bool productionGate)
    {
        Model = productionGate ? new DailyArenaChestModel() : new DailyArenaChestModel(DailyRefreshGate.ReadClock);
        Active = this;
        Root.gameObject = new UnityEngine.GameObject();
        BaseAsset.lds = new LoadScreen { currmg = Root };
        GlobalUi.gameObject = new UnityEngine.GameObject();
        GlobalUi.gameObject.transform.root = Root.transform;
        GlobalUi.rmg = Root;
        foreach (__BaseUI ui in new __BaseUI[] { Main, Select, ArenaUi, Chest })
        { ui.gameObject = GlobalUi.gameObject; ui.rmg = Root; ui.enabled = ui == Main; }
        Dialog.gameObject = new UnityEngine.GameObject(); Dialog.enabled = false;
        Error.gameObject = new UnityEngine.GameObject(); Error.enabled = false;
        BaseUI.rootUI = GlobalUi; __BaseUI.battle_result_ui = Dialog; MainUI.m_subui = null;
        Global.m_client = RootUI.m_client = Client; Global.character_ = Role; Global.extern_enabled = true;
        UnityEngine.GUI.enabled = true;
        __BaseUI.my_mouseclick = true;
        Client.m_handlers[355] = new ArenaHandler(RootBase.handelActvity, null, -1f);
        ArenaFixtureClient.connection_state = ArenaFixtureClient.ConnectionState.OK;
        Database.Arena.TemplateAvailable = true;
        Global.relogin = RootUI.m_bRefreshFive = false;
        Global.Template = new Database.ActivityTemplate();
        TimeUtil._thisDay = Day; TimeUtil._now = Day + 72001;
        Role.next_resource_time = Day + 86400 + 18000;
        Role.act_rewards[6] = new Database.Activity_Reward();
        SetCounters(0, (int)(Day + 86400));
        if (current) Bonus(Free);
        if (old) Bonus(Old);
    }
    internal void Bonus(uint time) { Role.arena.bonuses.Add(new Database._character_arena_bonus { time = time, rank = 1 }); }
    internal void OpenArena()
    {
        ArenaUi.m_arena = Role.arena;
        ShowPanel(ArenaUi);
    }

    internal void ShowPanel(BaseUI panel)
    {
        LoaderCalls++;
        MainUI.SetSubUI(panel);
        if (panel == DeferredPanel || panel == ThrowingLoader || panel == ArenaUi && DeferArenaShow)
        {
            panel.DisableAll(); panel.loading = true; panel.asset_names.Add(new object()); BaseAsset.lds.visible = true;
            if (panel == ThrowingLoader) throw new InvalidOperationException("fixture loader failure after enqueue");
            return;
        }
        panel.loading = false; panel.asset_names.Clear(); BaseAsset.lds.visible = false;
        panel.Visible(); panel.EnableAll();
    }

    internal UnityEngine.Object[] AllUi()
    { return new UnityEngine.Object[] { Root, GlobalUi, Main, Select, ArenaUi, Chest, Dialog, Error }; }
    internal SharedDevelopmentContext Context()
    {
        return new SharedDevelopmentContext(AllUi(),
            Root, GlobalUi, Role, Role.id.ToString(), ExternalGate, false, delegate { return "foreign_overlay"; });
    }
    internal void Sample() { Model.Capture(Context()); }
    internal bool ExecuteGui(int sequence, int revision, string action, string id, ulong role, DevelopmentPublication publish, out string why)
    { return Model.ExecuteGui(sequence, revision, action, id, role, Context, publish, Commit, out why); }
    private bool Commit(DailyArenaInvocationDeadline deadline)
    {
        if (++CommitCalls > CommitQuota || !AllowCommit || !deadline()) return false;
        if (AfterCommitRead != null) AfterCommitRead();
        return true;
    }
    internal void RestoreMain()
    {
        MainUI.SetSubUI(null);
        Main.EnableAll();
        BaseAsset.lds.visible = false;
    }
    internal void Expire(string field, int elapsed)
    {
        typeof(DailyArenaChestModel).GetField(field, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .SetValue(Model, unchecked(Environment.TickCount - elapsed));
    }
    internal Dictionary<string, object> State()
    { return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(Model.Json()); }
    internal string Value(string key) { return (string)State()[key]; }
    internal int Revision { get { return (int)State()["revision"]; } }
    internal void Step(string action)
    {
        Sample();
        if (Value("next_action") != action) throw new Exception("expected " + action + ", got " + Model.Json());
        int seq = ++Sequence, revision = Revision;
        string why;
        if (!Model.Queue(seq, revision, action, Id, Role.id, out why) ||
            !ExecuteGui(seq, revision, action, Id, Role.id, delegate { Publications++; }, out why))
            throw new Exception(action + " refused: " + why);
        Sample();
    }
    internal void SetCounters(byte count, int refresh, bool latest = true, bool listed = true)
    {
        if (latest) Role.acts[3] = new Database.Activity { temp = Global.Template, condition_param = count, refresh_time = refresh };
        if (listed) Role.act_rewards[6].acts[3] = new Database.Activity { temp = Global.Template, condition_param = count, refresh_time = refresh };
    }
    internal void Progress(int refresh = (int)(Day + 86400), bool latest = true, bool listed = true)
    {
        PushCount = 1; PushRefresh = refresh; PushLatest = latest; PushListed = listed;
        List<byte> row = new List<byte> { 8, 3, 16, 1, 24 };
        uint zigzag = (uint)((refresh << 1) ^ (refresh >> 31));
        while (zigzag > 127) { row.Add((byte)(zigzag | 128)); zigzag >>= 7; }
        row.Add((byte)zigzag);
        List<byte> bytes = new List<byte> { 10, (byte)row.Count };
        bytes.AddRange(row);
        ArenaHandler push = Client.m_handlers[355];
        using (MemoryStream stream = new MemoryStream(bytes.ToArray())) push.handler(stream, push.arg);
    }
    internal void ApplyActivityPush()
    {
        ActivityPushCalls++;
        SetCounters(PushCount, PushRefresh, PushLatest, PushListed);
        if (ThrowActivityPush) throw new InvalidOperationException("fixture activity callback threw");
    }
    internal void Deliver(byte[] data, uint time, bool removeHandler)
    {
        ArenaHandler handler = Client.m_handlers[1122];
        if (removeHandler) Client.m_handlers.Remove(1122);
        ArenaUi.ResponseTime = time;
        using (MemoryStream body = data == null ? null : new MemoryStream(data)) handler.handler(body, handler.arg);
    }
}

internal static class DailyArenaChestTests
{
    // 0x08 result, 0x10 time (1072000), 0x28 optional gold_consume.
    private static readonly byte[] Free = { 8, 0, 16, 0x80, 0xb7, 0x41 };
    private static readonly byte[] Paid = { 8, 0, 16, 0x80, 0xb7, 0x41, 40, 10 };
    private static readonly byte[] LastAward = { 8, 0, 16, 0x80, 0x94, 0x3c };
    private static readonly byte[] WrongTime = { 8, 0, 16, 1 };
    private static int checks;

    private static void Require(bool condition, string what)
    {
        if (!condition) { Console.Error.WriteLine(what); throw new Exception(what); }
        checks++;
    }
    private static void PrepareClaim(ArenaFixture fixture)
    { fixture.OpenArena(); fixture.Step("claim"); }

    private static ArenaFixture PrepareNavigation(string action)
    {
        ArenaFixture fixture = new ArenaFixture(true, action == "open_chest");
        if (action == "enter") fixture.Step("open");
        else if (action == "open_chest") fixture.OpenArena();
        fixture.Sample();
        return fixture;
    }

    private static bool ExecuteOffer(ArenaFixture fixture, string action, DevelopmentPublication publish, out string why)
    {
        int seq = ++fixture.Sequence, rev = fixture.Revision;
        return fixture.Model.Queue(seq, rev, action, ArenaFixture.Id, fixture.Role.id, out why) &&
            fixture.ExecuteGui(seq, rev, action, ArenaFixture.Id, fixture.Role.id, publish, out why);
    }

    private static ArenaFixture MatchingReplyWithRetainedBonus()
    {
        ArenaFixture fixture = new ArenaFixture(true, false);
        PrepareClaim(fixture);
        fixture.ArenaUi.RemoveOnResponse = false;
        fixture.Deliver(Free, ArenaFixture.Free, true);
        fixture.Role.arena.got = 1;
        fixture.Progress();
        return fixture;
    }

    public static int Main()
    {
        ArenaFixture direct = new ArenaFixture(true, false);
        direct.Step("open"); direct.Step("enter"); direct.Step("claim");
        ArenaHandler directHandler = direct.Client.m_handlers[1122];
        Delegate directArg = (Delegate)directHandler.arg;
        Require(direct.ArenaUi.Requests == 1 && !direct.ArenaUi.FindBack && direct.ArenaUi.RequestTime == ArenaFixture.Free &&
            directArg.Method.Name == "BonusGetClose" && object.ReferenceEquals(directArg.Target, direct.ArenaUi),
            "direct free request uses original stock close receiver and arg");
        direct.Deliver(Free, ArenaFixture.Free, true); direct.Sample();
        Require(direct.Value("next_action") == "" && direct.Model.Unresolved, "callback and removal alone do not authorize confirmation");
        direct.Progress(); direct.Sample();
        Require(direct.Value("status") == "CLAIMED" && direct.Model.Busy && !direct.Model.Unresolved &&
            direct.Value("next_action") == "reward_confirm", "verified claim and ready dialog remain separate");
        direct.Step("reward_confirm");
        Require(direct.ArenaUi.BonusCloseCalls == 1 && Global.extern_enabled && direct.Model.Busy &&
            direct.Value("status") == "CLAIMED" && direct.Value("next_action") == "close", "one stock confirmation admits only the owned arena exit");
        direct.Step("close");
        Require(direct.Value("status") == "COMPLETED" && !direct.Model.Busy && direct.ArenaUi.OriginalCalls == 1 &&
            (uint)Convert.ToInt64(direct.State()["receipt_time"]) == ArenaFixture.Free && (int)direct.State()["counter_after"] == 1 &&
            direct.Role.gold == 500 && direct.ArenaUi.Requests == 1, "full settlement and independent UI exit");

        ArenaFixture mixed = new ArenaFixture(true, true);
        mixed.OpenArena(); mixed.Step("open_chest"); mixed.Step("claim");
        Delegate mixedArg = (Delegate)mixed.Client.m_handlers[1122].arg;
        Require(!mixed.ArenaUi.FindBack && mixed.ArenaUi.RequestTime == ArenaFixture.Free &&
            mixedArg.Method.Name == "BonusGetClose" && object.ReferenceEquals(mixedArg.Target, mixed.Chest),
            "mixed route uses the chest's native delegate and requests only the free record");
        mixed.Progress(); mixed.Deliver(Free, ArenaFixture.Free, true); mixed.Sample();
        mixed.Step("reward_confirm");
        Require(mixed.Chest.interactive && !Global.extern_enabled && mixed.Model.Busy && mixed.Value("next_action") == "close",
            "mixed reward preserves native chest input without implying global input is restored");
        mixed.DeferArenaShow = true;
        mixed.Step("close");
        Require(MainUI.m_subui == mixed.ArenaUi && mixed.ArenaUi.loading && mixed.Value("next_action") == "" &&
            mixed.Value("ui_disposition") == "loading", "normal async arena return must load before another close");
        mixed.DeferArenaShow = false; mixed.OpenArena();
        Require(MainUI.m_subui == mixed.ArenaUi && Global.extern_enabled, "stock chest exit restores arena panel and root input");
        mixed.Step("close");
        Require(mixed.Role.arena.bonuses.Count == 1 && mixed.Role.arena.bonuses[0].time == ArenaFixture.Old &&
            mixed.Value("status") == "COMPLETED" && mixed.ArenaUi.Requests == 1, "paid row preserved");

        string varIgnored;
        ArenaFixture cutoff = new ArenaFixture(false, true);
        cutoff.OpenArena(); cutoff.Sample();
        Require(cutoff.Value("status") == "UNAVAILABLE" && cutoff.Value("reason") == "arena_no_current_free_bonus" &&
            !cutoff.Model.Queue(1, cutoff.Revision, "claim", ArenaFixture.Id, cutoff.Role.id, out varIgnored) &&
            cutoff.ArenaUi.Requests == 0, "paid-only after cutoff never invokes 1122");
        TimeUtil._now = ArenaFixture.Day + 72000; cutoff.Sample();
        Require(cutoff.Value("next_action") == "claim", "original inclusive 20:00 cutoff carries last award");
        TimeUtil._now = 0; cutoff.Sample();
        Require(cutoff.Value("next_action") == "", "unknown clock does not authorize a request");

        ArenaFixture unavailable = new ArenaFixture(true, false);
        unavailable.Progress(); unavailable.Sample();
        Require(unavailable.Value("status") == "UNAVAILABLE" && unavailable.Value("reason") == "arena_activity_complete", "already-complete counter");
        unavailable.Role.acts[3].idactivity = 9; unavailable.Sample();
        Require(unavailable.Value("next_action") == "", "wrong activity row identity");
        ArenaFixture ambiguous = new ArenaFixture(true, false); ambiguous.Bonus(ArenaFixture.Free - 1); ambiguous.Sample();
        Require(ambiguous.Value("reason") == "arena_ambiguous_current_bonus", "two free records are ambiguous");

        ArenaFixture stale = new ArenaFixture(true, false); stale.OpenArena(); stale.Sample();
        string why; int revision = stale.Revision;
        Require(!stale.Model.Queue(1, revision, "claim", ArenaFixture.Id, stale.Role.id + 1, out why), "wrong requested role");
        Require(!stale.Model.Queue(1, revision - 1, "claim", ArenaFixture.Id, stale.Role.id, out why), "wrong revision");
        Require(stale.Model.Queue(1, revision, "claim", ArenaFixture.Id, stale.Role.id, out why), "queue owned claim");
        stale.Role.arena.bonuses[0] = new Database._character_arena_bonus { time = ArenaFixture.Free, rank = 1 };
        Require(!stale.ExecuteGui(1, revision, "claim", ArenaFixture.Id, stale.Role.id, delegate { throw new Exception("published stale row"); }, out why) &&
            stale.ArenaUi.Requests == 0, "replacement of same-value row invalidates the selection");
        ArenaFixture roleChanged = new ArenaFixture(true, false); roleChanged.OpenArena(); roleChanged.Sample();
        revision = roleChanged.Revision;
        Require(roleChanged.Model.Queue(1, revision, "claim", ArenaFixture.Id, roleChanged.Role.id, out why), "queue before role change");
        Global.character_ = new Database.Character { id = roleChanged.Role.id };
        Require(!roleChanged.ExecuteGui(1, revision, "claim", ArenaFixture.Id, roleChanged.Role.id, delegate { }, out why) &&
            roleChanged.ArenaUi.Requests == 0, "same numeric id on foreign character object invalidates the claim");

        ArenaFixture blocked = new ArenaFixture(true, false); blocked.OpenArena();
        blocked.Client.messages.Add(new ArenaFixtureClient.Message { op = 1122 }); blocked.Sample();
        Require(blocked.Value("reason") == "arena_network_pending", "a stale opcode in dispatcher blocks admission");
        blocked.Client.messages.Clear(); blocked.ArenaUi.m_arena = new Database.Arena(); blocked.Sample();
        Require(blocked.Value("reason") == "arena_panel_model_changed", "foreign ArenaUI cache blocks admission");

        ArenaFixture stopped = new ArenaFixture(true, false); stopped.OpenArena(); stopped.Sample();
        revision = stopped.Revision;
        Require(stopped.Model.Queue(1, revision, "claim", ArenaFixture.Id, stopped.Role.id, out why), "queue before explicit stop");
        stopped.Model.Stop("fixture_stop");
        Require(!stopped.ExecuteGui(1, revision, "claim", ArenaFixture.Id, stopped.Role.id, delegate { }, out why) &&
            !stopped.Model.Unresolved && stopped.ArenaUi.Requests == 0, "stop before invocation");

        ArenaFixture cancelled = new ArenaFixture(true, false); cancelled.OpenArena(); cancelled.Sample();
        revision = cancelled.Revision;
        Require(cancelled.Model.Queue(1, revision, "claim", ArenaFixture.Id, cancelled.Role.id, out why), "queue before publication cancellation");
        InvalidOperationException cancelledError = new InvalidOperationException("stop");
        try
        {
            cancelled.ExecuteGui(1, revision, "claim", ArenaFixture.Id, cancelled.Role.id, delegate { throw cancelledError; }, out why);
            throw new Exception("missing cancellation");
        }
        catch (InvalidOperationException error)
        { Require(object.ReferenceEquals(error, cancelledError) && cancelled.ArenaUi.Requests == 0 && !cancelled.Model.MutationAttempted, "pre-invocation publication cancellation is forwarded"); }

        ArenaFixture uncertain = new ArenaFixture(true, false); PrepareClaim(uncertain);
        uncertain.Model.Stop("fixture_stop");
        uncertain.Deliver(Free, ArenaFixture.Free, true); uncertain.Progress(); uncertain.Sample();
        Require(uncertain.Value("status") == "UNKNOWN" && uncertain.Model.Unresolved && uncertain.ArenaUi.OriginalCalls == 1 &&
            !uncertain.Model.Queue(2, uncertain.Revision, "claim", ArenaFixture.Id, uncertain.Role.id, out why), "post-invocation stop preserves normal callback and seals no retry");

        ArenaFixture duplicate = new ArenaFixture(true, false); PrepareClaim(duplicate);
        duplicate.Deliver(Free, ArenaFixture.Free, false); duplicate.Deliver(Free, ArenaFixture.Free, true); duplicate.Progress(); duplicate.Sample();
        Require(duplicate.Value("status") == "UNKNOWN" && duplicate.Model.Unresolved && duplicate.ArenaUi.Requests == 1, "duplicate callback cannot settle");
        ArenaFixture wrong = new ArenaFixture(true, false); PrepareClaim(wrong);
        wrong.Deliver(WrongTime, ArenaFixture.Free, true); wrong.Progress(); wrong.Sample();
        Require(wrong.Value("status") == "UNKNOWN" && wrong.Model.Unresolved, "wrong receipt time cannot settle even with matching readbacks");
        ArenaFixture paid = new ArenaFixture(true, false); PrepareClaim(paid);
        paid.Deliver(Paid, ArenaFixture.Free, true); paid.Progress(); paid.Sample();
        Require(paid.Value("status") == "UNKNOWN" && paid.Model.Unresolved && !paid.ArenaUi.FindBack, "nonzero receipt gold cannot settle");
        ArenaFixture charged = new ArenaFixture(true, false); PrepareClaim(charged);
        charged.ArenaUi.ResponseGold = 10;
        charged.Deliver(Paid, ArenaFixture.Free, true); charged.Progress(); charged.Sample();
        Require(charged.Role.gold == 490 && charged.Value("status") == "UNKNOWN" && charged.Model.Busy &&
            charged.Value("next_action") == "" && charged.ArenaUi.Requests == 1,
            "raw stock balance effects remain visible and a nonzero charge stops automation");
        ArenaFixture noReply = new ArenaFixture(true, false); PrepareClaim(noReply);
        noReply.Deliver(null, 0, true); noReply.Sample();
        Require(noReply.Value("status") == "UNKNOWN" && noReply.Model.Unresolved && noReply.ArenaUi.OriginalCalls == 1 &&
            noReply.ArenaUi.ImplicitCloses == 1 && noReply.ArenaUi.BonusCloseCalls == 1, "null timeout reaches the original stock close and cannot settle");
        ArenaFixture counterOnly = new ArenaFixture(true, false); PrepareClaim(counterOnly);
        counterOnly.Progress(); counterOnly.Sample();
        Require(counterOnly.Value("status") == "WAITING" && counterOnly.Model.Unresolved, "counter alone is never receipt evidence");

        ArenaFixture invalidRow = MatchingReplyWithRetainedBonus();
        invalidRow.Role.arena.bonuses.Insert(0, new Database._character_arena_bonus { time = 0, rank = 1 });
        invalidRow.Sample();
        Require(invalidRow.Role.arena.bonuses[1].time == ArenaFixture.Free && invalidRow.Value("status") == "WAITING" &&
            invalidRow.Model.Unresolved && invalidRow.Value("next_action") == "" && invalidRow.ArenaUi.Requests == 1,
            "an invalid earlier row cannot prove removal of the unscanned target");
        invalidRow.Role.arena.bonuses.RemoveAt(0); invalidRow.Sample();
        Require(invalidRow.Value("status") == "WAITING" && invalidRow.Model.Unresolved, "a full scan still waits while the target remains");
        invalidRow.Role.arena.bonuses.Clear(); invalidRow.Sample();
        Require(invalidRow.Value("status") == "CLAIMED" && !invalidRow.Model.Unresolved && invalidRow.ArenaUi.Requests == 1,
            "a subsequent valid scan can verify actual removal without retrying");

        ArenaFixture beyondBound = MatchingReplyWithRetainedBonus();
        for (uint i = 0; i < 64; i++)
            beyondBound.Role.arena.bonuses.Insert(0, new Database._character_arena_bonus { time = ArenaFixture.Old - i, rank = 1 });
        beyondBound.Sample();
        Require(beyondBound.Role.arena.bonuses[64].time == ArenaFixture.Free && beyondBound.Value("status") == "WAITING" &&
            beyondBound.Model.Unresolved && beyondBound.Value("next_action") == "" && beyondBound.ArenaUi.Requests == 1,
            "a target beyond the supported list bound cannot be treated as removed");

        ArenaFixture wrongBonusType = MatchingReplyWithRetainedBonus();
        wrongBonusType.Role.arena.bonuses.Insert(0, new Database.WrongArenaBonus { time = ArenaFixture.Old, rank = 1 });
        wrongBonusType.Sample();
        Require(wrongBonusType.Role.arena.bonuses[1].time == ArenaFixture.Free && wrongBonusType.Value("status") == "WAITING" &&
            wrongBonusType.Model.Unresolved && wrongBonusType.Value("next_action") == "" && wrongBonusType.ArenaUi.Requests == 1,
            "an unexpected row type prevents any proof of absence or further actions");

        ArenaFixture invalidAfterSettlement = new ArenaFixture(true, false); PrepareClaim(invalidAfterSettlement);
        invalidAfterSettlement.Progress(); invalidAfterSettlement.Deliver(Free, ArenaFixture.Free, true); invalidAfterSettlement.Sample();
        Require(invalidAfterSettlement.Value("next_action") == "reward_confirm", "the valid completed scan permits owned reward handling");
        invalidAfterSettlement.Role.arena.bonuses.Add(new Database.WrongArenaBonus { time = ArenaFixture.Old, rank = 1 });
        invalidAfterSettlement.Sample();
        Require(invalidAfterSettlement.Value("status") == "CLAIMED" && invalidAfterSettlement.Value("next_action") == "" &&
            invalidAfterSettlement.Model.Busy && invalidAfterSettlement.Dialog.enabled && invalidAfterSettlement.ArenaUi.Requests == 1,
            "an invalid current scan withdraws automatic cleanup without undoing earlier verified settlement");

        ArenaFixture refresh = new ArenaFixture(true, false); refresh.OpenArena();
        uint reset = refresh.Role.next_resource_time;
        refresh.Role.next_resource_time = 0; refresh.Sample();
        Require(refresh.Value("next_action") == "" && refresh.State()["current"] == null, "uninitialized daily refresh blocks observation");
        refresh.Role.next_resource_time = TimeUtil._now; refresh.Sample();
        Require(refresh.Value("reason") == "daily_refresh_pending", "next_resource_time equal to now is due");
        refresh.Role.next_resource_time = reset; RootUI.m_bRefreshFive = true; refresh.Sample();
        Require(refresh.Value("next_action") == "" && refresh.Value("reason") == "daily_refresh_pending", "refresh in progress blocks admission");

        ArenaFixture rollover = new ArenaFixture(false, true);
        TimeUtil._now = ArenaFixture.Day + 18000 - 1;
        rollover.Role.next_resource_time = ArenaFixture.Day + 18000;
        PrepareClaim(rollover);
        TimeUtil._now = ArenaFixture.Day + 18000;
        rollover.Role.next_resource_time += 86400; // Reset already finished before the old request's reply.
        rollover.Deliver(LastAward, ArenaFixture.Old, true);
        rollover.Progress(); rollover.Sample();
        Require(rollover.Value("status") == "UNKNOWN" && rollover.Model.Unresolved &&
            rollover.Value("reason") == "arena_settlement_boundary_changed", "04:59 claim and 05:00 reply cannot settle across cycles");

        ArenaFixture refreshDuringResponse = new ArenaFixture(true, false); PrepareClaim(refreshDuringResponse);
        RootUI.m_bRefreshFive = true;
        refreshDuringResponse.Deliver(Free, ArenaFixture.Free, true);
        RootUI.m_bRefreshFive = false;
        refreshDuringResponse.Progress(); refreshDuringResponse.Sample();
        Require(refreshDuringResponse.Value("status") == "UNKNOWN", "callback checks refresh even when the following sample is healthy");

        ArenaFixture reconnect = new ArenaFixture(true, false); PrepareClaim(reconnect);
        reconnect.Client.m_client = new ArenaFixtureClient.Socket { Connected = true };
        reconnect.Deliver(Free, ArenaFixture.Free, true);
        reconnect.Progress(); reconnect.Sample();
        Require(reconnect.Value("status") == "UNKNOWN" && reconnect.Model.Unresolved, "same tcp_client with replaced socket cannot settle");
        ArenaFixture newStream = new ArenaFixture(true, false); PrepareClaim(newStream);
        newStream.Client.m_stream = new MemoryStream();
        newStream.Deliver(Free, ArenaFixture.Free, true);
        newStream.Progress(); newStream.Sample();
        Require(newStream.Value("status") == "UNKNOWN" && newStream.Model.Unresolved, "same socket with replaced stream cannot settle");

        ArenaFixture unhealthy = new ArenaFixture(true, false); unhealthy.OpenArena();
        ArenaFixtureClient.connection_state = ArenaFixtureClient.ConnectionState.RECONNECTING; unhealthy.Sample();
        Require(unhealthy.Value("reason") == "arena_transport_unavailable", "reconnecting state blocks admission");
        ArenaFixtureClient.connection_state = ArenaFixtureClient.ConnectionState.OK;
        unhealthy.Client.m_bConnectLost = true; unhealthy.Sample();
        Require(unhealthy.Value("next_action") == "", "lost connection flag blocks admission");
        unhealthy.Client.m_bConnectLost = false; Global.relogin = true; unhealthy.Sample();
        Require(unhealthy.Value("next_action") == "", "relogin blocks admission");

        ArenaFixture changedAfterPublish = new ArenaFixture(true, false); changedAfterPublish.OpenArena(); changedAfterPublish.Sample();
        revision = changedAfterPublish.Revision;
        Require(changedAfterPublish.Model.Queue(1, revision, "claim", ArenaFixture.Id, changedAfterPublish.Role.id, out why), "queue before published index replacement");
        Require(!changedAfterPublish.ExecuteGui(1, revision, "claim", ArenaFixture.Id, changedAfterPublish.Role.id,
            delegate { changedAfterPublish.Role.arena.bonuses[0] = new Database._character_arena_bonus { time = ArenaFixture.Free, rank = 1 }; }, out why) &&
            changedAfterPublish.ArenaUi.Requests == 0 && !changedAfterPublish.Model.MutationAttempted, "post-publication same-value replacement cannot invoke claim");

        ArenaFixture dueAfterPublish = new ArenaFixture(false, true); dueAfterPublish.OpenArena();
        TimeUtil._now = ArenaFixture.Day + 18000 - 1;
        dueAfterPublish.Role.next_resource_time = ArenaFixture.Day + 18000; dueAfterPublish.Sample();
        revision = dueAfterPublish.Revision;
        Require(dueAfterPublish.Model.Queue(1, revision, "claim", ArenaFixture.Id, dueAfterPublish.Role.id, out why), "queue right before reset boundary");
        Require(!dueAfterPublish.ExecuteGui(1, revision, "claim", ArenaFixture.Id, dueAfterPublish.Role.id,
            delegate { TimeUtil._now++; }, out why) && dueAfterPublish.ArenaUi.Requests == 0, "publish-time refresh boundary blocks stock invocation");

        ArenaFixture asyncReward = new ArenaFixture(true, false); PrepareClaim(asyncReward);
        asyncReward.ArenaUi.DeferRewardShow = true;
        asyncReward.Progress(); asyncReward.Deliver(Free, ArenaFixture.Free, true); asyncReward.Sample();
        Require(asyncReward.Dialog.loading && !asyncReward.Dialog.enabled && !Global.extern_enabled &&
            asyncReward.Value("status") == "CLAIMED" && asyncReward.Value("next_action") == "" && asyncReward.Model.Busy,
            "matching reply seals the claim while the stock result loads asynchronously");
        asyncReward.Dialog.FinishLoading(); asyncReward.Step("reward_confirm");
        Require(asyncReward.ArenaUi.BonusCloseCalls == 1 && Global.extern_enabled && asyncReward.ArenaUi.Requests == 1,
            "exclusive run confirms the asynchronously shown matching stock reward once");
        asyncReward.Step("close");
        Require(asyncReward.Value("status") == "COMPLETED", "async stock reward completes through the owned close");

        ArenaFixture foreignResult = new ArenaFixture(true, false); PrepareClaim(foreignResult);
        foreignResult.ArenaUi.DeferRewardShow = true;
        foreignResult.Progress(); foreignResult.Deliver(Free, ArenaFixture.Free, true);
        int foreignCloses = 0;
        foreignResult.Dialog.OnClose = delegate { foreignCloses++; };
        foreignResult.Dialog.FinishLoading(); foreignResult.Sample();
        Require(foreignResult.Value("status") == "CLAIMED" && foreignResult.Value("next_action") == "" &&
            foreignResult.Dialog.enabled && foreignCloses == 0 && foreignResult.Model.Busy, "foreign result remains on the stock manual path");

        ArenaFixture replacedDuringPublication = new ArenaFixture(true, false); PrepareClaim(replacedDuringPublication);
        replacedDuringPublication.Progress(); replacedDuringPublication.Deliver(Free, ArenaFixture.Free, true); replacedDuringPublication.Sample();
        int dialogSequence = replacedDuringPublication.Sequence + 1; revision = replacedDuringPublication.Revision;
        Require(replacedDuringPublication.Model.Queue(dialogSequence, revision, "reward_confirm", ArenaFixture.Id,
            replacedDuringPublication.Role.id, out why), "queue the observed owned result");
        Require(!replacedDuringPublication.ExecuteGui(dialogSequence, revision, "reward_confirm", ArenaFixture.Id,
            replacedDuringPublication.Role.id, delegate { replacedDuringPublication.Dialog.OnClose = delegate { foreignCloses++; }; }, out why) &&
            replacedDuringPublication.Dialog.enabled && foreignCloses == 0 && replacedDuringPublication.Value("status") == "CLAIMED" &&
            replacedDuringPublication.Model.Busy, "publication-time delegate replacement leaves verified claim but never closes foreign UI");

        ArenaFixture reopened = new ArenaFixture(true, false); PrepareClaim(reopened);
        reopened.Deliver(Free, ArenaFixture.Free, true); reopened.Sample();
        reopened.Dialog.Hide(); reopened.Dialog.Visible(); reopened.Progress(); reopened.Sample();
        Require(reopened.Value("status") == "CLAIMED" && reopened.Value("next_action") == "" && reopened.Dialog.enabled,
            "an observed result reopened with its old delegate cannot be confirmed automatically");

        ArenaFixture nestedChanged = new ArenaFixture(true, true);
        nestedChanged.OpenArena(); nestedChanged.Step("open_chest"); nestedChanged.Step("claim");
        nestedChanged.Progress(); nestedChanged.Deliver(Free, ArenaFixture.Free, true); nestedChanged.Role.arena.bonuses.Clear();
        nestedChanged.Sample();
        dialogSequence = nestedChanged.Sequence + 1; revision = nestedChanged.Revision;
        Require(nestedChanged.Model.Queue(dialogSequence, revision, "reward_confirm", ArenaFixture.Id, nestedChanged.Role.id, out why),
            "queue result close with owned nested delegate");
        Require(!nestedChanged.ExecuteGui(dialogSequence, revision, "reward_confirm", ArenaFixture.Id, nestedChanged.Role.id,
            delegate { nestedChanged.Chest.OnClose = delegate { foreignCloses++; }; }, out why) &&
            nestedChanged.Dialog.enabled && nestedChanged.Chest.enabled && foreignCloses == 0 && nestedChanged.Value("status") == "CLAIMED",
            "assistant refuses a changed nested delegate without replacing native UI behavior");

        ArenaFixture changedInsideHide = new ArenaFixture(true, true);
        changedInsideHide.OpenArena(); changedInsideHide.Step("open_chest"); changedInsideHide.Step("claim");
        changedInsideHide.Progress(); changedInsideHide.Deliver(Free, ArenaFixture.Free, true);
        changedInsideHide.Role.arena.bonuses.Clear(); changedInsideHide.Sample();
        dialogSequence = changedInsideHide.Sequence + 1; revision = changedInsideHide.Revision;
        Require(changedInsideHide.Model.Queue(dialogSequence, revision, "reward_confirm", ArenaFixture.Id, changedInsideHide.Role.id, out why),
            "queue reward before rechecking its nested callback after Hide");
        changedInsideHide.Dialog.OnHide = delegate { changedInsideHide.Chest.OnClose = delegate { foreignCloses++; }; };
        Require(!changedInsideHide.ExecuteGui(dialogSequence, revision, "reward_confirm", ArenaFixture.Id, changedInsideHide.Role.id,
            delegate { }, out why) && changedInsideHide.Chest.enabled && foreignCloses == 0 &&
            changedInsideHide.Chest.BonusCloseCalls == 0 && changedInsideHide.Value("status") == "CLAIMED",
            "ownership loss during Hide cannot execute the now-foreign nested callback");

        ArenaFixture staleExit = new ArenaFixture(true, false); PrepareClaim(staleExit);
        staleExit.Progress(); staleExit.Deliver(Free, ArenaFixture.Free, true); staleExit.Step("reward_confirm");
        int exitSequence = staleExit.Sequence + 1; revision = staleExit.Revision;
        Require(staleExit.Model.Queue(exitSequence, revision, "close", ArenaFixture.Id, staleExit.Role.id, out why),
            "queue the visible stock arena close");
        Require(!staleExit.ExecuteGui(exitSequence, revision, "close", ArenaFixture.Id, staleExit.Role.id,
            MainUI._battle_arena, out why) && MainUI.m_subui == staleExit.Select && staleExit.Select.enabled &&
            staleExit.Value("status") == "CLAIMED" && staleExit.Model.Busy, "foreign page replacement is never closed");

        ArenaFixture production = new ArenaFixture(true, false, true); production.OpenArena(); production.Sample();
        Require(production.Value("reason") == "daily_refresh_unverified" && production.Value("next_action") == "" &&
            production.State()["current"] == null, "production remains gated on verified daily refresh evidence");

        ArenaFixture late = new ArenaFixture(true, false); PrepareClaim(late);
        ArenaHandler lateHandler = late.Client.m_handlers[1122];
        object lateArg = lateHandler.arg;
        late.Model.Stop("fixture_stop");
        Require(object.ReferenceEquals(lateHandler.handler, late.ArenaUi.LastStockHandler) &&
            object.ReferenceEquals(lateHandler.arg, lateArg), "Stop restores exact native callback and leaves arg intact");
        late.ArenaUi.DeferRewardShow = true;
        late.Deliver(Free, ArenaFixture.Free, true); late.Dialog.FinishLoading();
        Require(late.Dialog.enabled && !Global.extern_enabled && object.ReferenceEquals(late.Dialog.OnClose, lateArg),
            "late original result owns the asynchronous stock confirmation");
        late.Dialog.Confirm(); late.Progress(); late.Sample();
        Require(Global.extern_enabled && late.ArenaUi.BonusCloseCalls == 1 && late.Value("status") == "UNKNOWN" &&
            late.Value("next_action") == "" && late.Model.Unresolved && late.Model.Busy && late.ArenaUi.Requests == 1 &&
            !late.Model.Queue(2, late.Revision, "claim", ArenaFixture.Id, late.Role.id, out why),
            "manual confirmation after Stop restores native input but neither settles nor retries");

        ArenaFixture lateMixed = new ArenaFixture(true, true);
        lateMixed.OpenArena(); lateMixed.Step("open_chest"); lateMixed.Step("claim"); lateMixed.Model.Stop("fixture_stop");
        lateMixed.Deliver(Free, ArenaFixture.Free, true); lateMixed.Dialog.Confirm();
        Require(lateMixed.Chest.interactive && !Global.extern_enabled && lateMixed.Chest.BonusCloseCalls == 1,
            "late mixed claim uses normal stock chest confirmation");
        lateMixed.Chest.Hide(); lateMixed.Chest.OnClose(); lateMixed.Sample();
        Require(Global.extern_enabled && lateMixed.ArenaUi.Requests == 1 && lateMixed.Value("status") == "UNKNOWN",
            "normal player chest exit restores root input after Stop");

        ArenaFixture timeout = new ArenaFixture(true, true);
        timeout.OpenArena(); timeout.Step("open_chest"); timeout.Step("claim"); timeout.Model.Stop("fixture_stop");
        timeout.Deliver(null, 0, true);
        Require(timeout.ArenaUi.OriginalCalls == 1 && timeout.Chest.BonusCloseCalls == 1 && timeout.Chest.interactive &&
            timeout.Role.arena.bonuses.Count == 2 && timeout.ArenaUi.Requests == 1 && timeout.Value("status") == "UNKNOWN",
            "stock null response after Stop restores the original chest without a second claim");

        ArenaFixture refused = new ArenaFixture(true, false); PrepareClaim(refused);
        refused.Model.Stop("fixture_stop"); refused.ArenaUi.ResponseResult = 2;
        refused.Deliver(new byte[] { 8, 2 }, 0, true);
        Require(refused.Error.enabled && !Global.extern_enabled && refused.ArenaUi.BonusCloseCalls == 0,
            "native rejection opens its own confirmation");
        refused.Error.Confirm(); refused.Sample();
        Require(Global.extern_enabled && refused.ArenaUi.BonusCloseCalls == 1 && refused.ArenaUi.Requests == 1 &&
            refused.Value("status") == "UNKNOWN", "reject dialog can restore input after Stop");

        ArenaFixture throwingHandler = new ArenaFixture(true, false); PrepareClaim(throwingHandler);
        InvalidOperationException originalFailure = new InvalidOperationException("fixture stock callback failure");
        throwingHandler.ArenaUi.CallbackFailure = originalFailure;
        try { throwingHandler.Deliver(Free, ArenaFixture.Free, true); throw new Exception("stock callback exception swallowed"); }
        catch (InvalidOperationException error) { Require(object.ReferenceEquals(error, originalFailure), "stock callback exception preserves identity"); }
        throwingHandler.Progress(); throwingHandler.Sample();
        Require(throwingHandler.Value("status") == "UNKNOWN" && throwingHandler.ArenaUi.Requests == 1 &&
            throwingHandler.Value("next_action") == "", "stock callback exception cannot settle or authorize UI action");

        Require(wrong.ArenaUi.Requests == 1 && paid.ArenaUi.Requests == 1 && duplicate.ArenaUi.Requests == 1 &&
            !duplicate.Model.Queue(2, duplicate.Revision, "claim", ArenaFixture.Id, duplicate.Role.id, out why),
            "wrong, paid and duplicate response paths issue no extra 1122");

        foreach (string action in new string[] { "open", "enter", "open_chest" })
        {
            ArenaFixture beforeNavigation = PrepareNavigation(action);
            int calls = beforeNavigation.LoaderCalls;
            bool earlierNavigation = (bool)beforeNavigation.State()["navigation_pending"];
            Require(!ExecuteOffer(beforeNavigation, action, delegate { beforeNavigation.Model.Stop("fixture_stop"); }, out why) &&
                beforeNavigation.LoaderCalls == calls && beforeNavigation.Model.Busy == earlierNavigation,
                "stop during publication prevents " + action + " and preserves only earlier ownership");

            ArenaFixture afterNavigation = PrepareNavigation(action);
            afterNavigation.Step(action); afterNavigation.Model.Stop("fixture_stop"); afterNavigation.Sample();
            Require(afterNavigation.Model.Busy && (bool)afterNavigation.State()["navigation_pending"] &&
                afterNavigation.Value("next_action") == "" && afterNavigation.ArenaUi.Requests == 0,
                "stop after stock " + action + " retains native UI ownership");
            afterNavigation.RestoreMain(); afterNavigation.Sample();
            Require(!afterNavigation.Model.Busy && !(bool)afterNavigation.State()["navigation_pending"],
                "only exact restored root and dormant native panels release " + action);
        }

        ArenaFixture throwingLoader = PrepareNavigation("open");
        throwingLoader.ThrowingLoader = throwingLoader.Select;
        Require(!ExecuteOffer(throwingLoader, "open", delegate { }, out why) && throwingLoader.Model.Busy &&
            throwingLoader.Select.loading && throwingLoader.ArenaUi.Requests == 0,
            "stock loader throw after enqueue keeps the owner latched");
        throwingLoader.RestoreMain(); throwingLoader.Sample();
        Require(throwingLoader.Model.Busy, "a clear sub-UI pointer cannot abandon a late stock loader");
        throwingLoader.Select.loading = false;
        throwingLoader.Sample();
        Require(throwingLoader.Model.Busy, "queued native assets keep the owner even with loading false");
        throwingLoader.Select.asset_names.Clear(); throwingLoader.Select.Visible(); throwingLoader.Sample();
        Require(throwingLoader.Model.Busy, "late native Show cannot clear the terminal owner");
        throwingLoader.Select.Hide();
        List<object> originalQueue = throwingLoader.Select.asset_names;
        throwingLoader.Select.asset_names = new List<object>(); throwingLoader.Sample();
        Require(throwingLoader.Model.Busy, "a replacement empty queue cannot release the original stock loader");
        throwingLoader.Select.asset_names = originalQueue; throwingLoader.Sample();
        Require(!throwingLoader.Model.Busy && throwingLoader.Value("status") == "REJECTED", "late Show was eventually hidden without retry");

        ArenaFixture navigationTimeout = PrepareNavigation("open");
        navigationTimeout.DeferredPanel = navigationTimeout.Select;
        navigationTimeout.Step("open");
        navigationTimeout.Expire("deadlineStart", 19000); navigationTimeout.Sample();
        Require(navigationTimeout.Value("status") == "REJECTED" && navigationTimeout.Model.Busy &&
            (bool)navigationTimeout.State()["navigation_pending"], "loader timeout preserves native ownership and suppresses later actions");

        foreach (bool throws in new bool[] { false, true })
        {
            ArenaFixture failedClose = new ArenaFixture(true, false); PrepareClaim(failedClose);
            failedClose.Progress(); failedClose.Deliver(Free, ArenaFixture.Free, true); failedClose.Step("reward_confirm");
            failedClose.NoOpClose = !throws; failedClose.ThrowOnClose = throws;
            Require(!ExecuteOffer(failedClose, "close", delegate { }, out why) && failedClose.Model.Busy &&
                failedClose.Value("status") == "CLAIMED" && MainUI.m_subui == failedClose.ArenaUi &&
                failedClose.Value("next_action") == "" && failedClose.ArenaUi.Requests == 1,
                "no-op or throwing close does not lose ownership or replay");
        }

        foreach (bool latestOnly in new bool[] { false, true })
        {
            ArenaFixture inconsistentBefore = new ArenaFixture(true, false); inconsistentBefore.OpenArena();
            inconsistentBefore.SetCounters(1, (int)(ArenaFixture.Day + 86400), latestOnly, !latestOnly);
            inconsistentBefore.Sample();
            Require(inconsistentBefore.Value("reason") == "arena_activity_cache_mismatch" &&
                inconsistentBefore.Value("next_action") == "" && inconsistentBefore.ArenaUi.Requests == 0,
                "either-direction preclaim counter conflict fails closed");

            ArenaFixture inconsistentAfter = new ArenaFixture(true, false); PrepareClaim(inconsistentAfter);
            inconsistentAfter.Deliver(Free, ArenaFixture.Free, true);
            inconsistentAfter.Progress(latest: latestOnly, listed: !latestOnly);
            inconsistentAfter.Sample();
            Require(inconsistentAfter.Model.Unresolved && inconsistentAfter.Value("status") == "WAITING" &&
                inconsistentAfter.Value("next_action") == "", "either-direction postclaim counter mismatch cannot settle");
            inconsistentAfter.SetCounters(1, (int)(ArenaFixture.Day + 86400));
            inconsistentAfter.Sample();
            Require(!inconsistentAfter.Model.Unresolved && (bool)inconsistentAfter.State()["activity_push_returned"],
                "matching caches and the same returned push can settle without a retry");
        }

        ArenaFixture refreshConflict = new ArenaFixture(true, false); refreshConflict.OpenArena();
        refreshConflict.Role.act_rewards[6].acts[3].refresh_time++;
        refreshConflict.Sample();
        Require(refreshConflict.Value("reason") == "arena_activity_cache_mismatch" && refreshConflict.ArenaUi.Requests == 0,
            "preclaim rows with equal counters and different refresh still refuse");

        ArenaFixture noPush = new ArenaFixture(true, false); PrepareClaim(noPush);
        noPush.Deliver(Free, ArenaFixture.Free, true); noPush.SetCounters(1, (int)(ArenaFixture.Day + 86400)); noPush.Sample();
        Require(noPush.Model.Unresolved && noPush.Value("next_action") == "", "matching caches alone cannot replace an owned activity push");

        ArenaFixture wrongPushRefresh = new ArenaFixture(true, false); PrepareClaim(wrongPushRefresh);
        wrongPushRefresh.Progress(); wrongPushRefresh.Deliver(Free, ArenaFixture.Free, true);
        wrongPushRefresh.SetCounters(1, (int)(ArenaFixture.Day + 86400 + 1)); wrongPushRefresh.Sample();
        Require(wrongPushRefresh.Model.Unresolved && wrongPushRefresh.Value("next_action") == "",
            "both progressed caches must agree with the refresh on the observed push");

        ArenaFixture pushThrows = new ArenaFixture(true, false); PrepareClaim(pushThrows);
        pushThrows.ThrowActivityPush = true;
        try { pushThrows.Progress(); throw new Exception("the fixture stock push should throw"); }
        catch (InvalidOperationException) { }
        pushThrows.Deliver(Free, ArenaFixture.Free, true); pushThrows.Sample();
        Require(pushThrows.Value("status") == "UNKNOWN" && pushThrows.Model.Unresolved &&
            pushThrows.Role.acts[3].condition_param == 1 && pushThrows.Role.act_rewards[6].acts[3].condition_param == 1 &&
            !(bool)pushThrows.State()["activity_push_returned"],
            "a push which updated both caches but threw still has no returned settlement witness");

        ArenaFixture callbackRetained = new ArenaFixture(true, false); PrepareClaim(callbackRetained);
        callbackRetained.Progress(); callbackRetained.Deliver(Free, ArenaFixture.Free, false); callbackRetained.Sample();
        Require(callbackRetained.Value("status") == "UNKNOWN" && callbackRetained.Value("reason") == "arena_stock_callback_handler_present" &&
            callbackRetained.ArenaUi.Requests == 1, "a retained one-shot handler cannot prove native dispatch completion");

        ArenaFixture lateBoth = new ArenaFixture(true, false); PrepareClaim(lateBoth);
        lateBoth.Expire("deadlineStart", 19000);
        lateBoth.Progress(); lateBoth.Deliver(Free, ArenaFixture.Free, true); lateBoth.Sample();
        Require(lateBoth.Value("status") == "UNKNOWN" && lateBoth.Value("next_action") == "" &&
            lateBoth.ArenaUi.OriginalCalls == 1 && lateBoth.ActivityPushCalls == 1 && lateBoth.ArenaUi.Requests == 1,
            "late stock callbacks still forward without sealing settlement");

        ArenaFixture revoked = PrepareNavigation("open");
        Require(!ExecuteOffer(revoked, "open", delegate { revoked.AllowCommit = false; }, out why) &&
            revoked.LoaderCalls == 0 && !revoked.Model.Busy, "revoked controller commit during publication prevents native navigation");

        ArenaFixture revokedClaim = new ArenaFixture(true, false); revokedClaim.OpenArena(); revokedClaim.Sample();
        Delegate originalPush = revokedClaim.Client.m_handlers[355].handler;
        Require(!ExecuteOffer(revokedClaim, "claim", delegate { revokedClaim.AllowCommit = false; }, out why) &&
            revokedClaim.ArenaUi.Requests == 0 && !revokedClaim.Model.Busy &&
            object.ReferenceEquals(revokedClaim.Client.m_handlers[355].handler, originalPush),
            "a denied final claim admission restores the already prepared 355 observer and sends nothing");

        ArenaFixture expiredQueue = PrepareNavigation("open");
        Require(expiredQueue.Model.Queue(1, expiredQueue.Revision, "open", ArenaFixture.Id, expiredQueue.Role.id, out why),
            "queue original stock open before checking local deadline");
        expiredQueue.Expire("queuedAt", 1600);
        Require(!expiredQueue.ExecuteGui(1, expiredQueue.Revision, "open", ArenaFixture.Id, expiredQueue.Role.id,
            delegate { }, out why) && expiredQueue.LoaderCalls == 0 && !expiredQueue.Model.Busy,
            "expired GUI command never invokes stock open");

        ArenaFixture foreignOverlayAfterPublish = new ArenaFixture(true, false); foreignOverlayAfterPublish.OpenArena(); foreignOverlayAfterPublish.Sample();
        Require(!ExecuteOffer(foreignOverlayAfterPublish, "claim", foreignOverlayAfterPublish.Error.Visible, out why) &&
            foreignOverlayAfterPublish.Error.enabled && foreignOverlayAfterPublish.ArenaUi.Requests == 0,
            "fresh postpublication inventory blocks a new foreign overlay");

        ArenaFixture inputAfterPublish = new ArenaFixture(true, false); inputAfterPublish.OpenArena(); inputAfterPublish.Sample();
        Require(!ExecuteOffer(inputAfterPublish, "claim", delegate
        { Global.extern_enabled = false; inputAfterPublish.ExternalGate = "global_input_disabled"; }, out why) &&
            inputAfterPublish.ArenaUi.Requests == 0, "fresh postpublication input gate blocks the claim");

        ArenaFixture guiLostAtCommit = PrepareNavigation("open_chest");
        int priorCalls = guiLostAtCommit.LoaderCalls;
        guiLostAtCommit.AfterCommitRead = delegate { UnityEngine.GUI.enabled = false; };
        Require(!ExecuteOffer(guiLostAtCommit, "open_chest", delegate { }, out why) && guiLostAtCommit.CommitCalls == 1 &&
            guiLostAtCommit.LoaderCalls == priorCalls && guiLostAtCommit.ArenaUi.interactive &&
            Global.extern_enabled && __BaseUI.my_mouseclick && guiLostAtCommit.ArenaUi.Requests == 0,
            "a successful external gate followed by loss of GUI cannot even disable the stock parent");

        ArenaFixture disconnectedAtCommit = new ArenaFixture(true, true);
        disconnectedAtCommit.OpenArena(); disconnectedAtCommit.Step("open_chest");
        int priorCommits = disconnectedAtCommit.CommitCalls;
        originalPush = disconnectedAtCommit.Client.m_handlers[355].handler;
        disconnectedAtCommit.AfterCommitRead = delegate { disconnectedAtCommit.Client.m_bConnectLost = true; };
        Require(!ExecuteOffer(disconnectedAtCommit, "claim", delegate { }, out why) &&
            disconnectedAtCommit.CommitCalls == priorCommits + 1 && disconnectedAtCommit.Chest.interactive &&
            disconnectedAtCommit.ArenaUi.Requests == 0 && Global.extern_enabled && __BaseUI.my_mouseclick &&
            object.ReferenceEquals(disconnectedAtCommit.Client.m_handlers[355].handler, originalPush),
            "a lost transport after the final command read cannot disable the chest or send 1122");

        ArenaFixture streamChangedAtCommit = new ArenaFixture(true, false); PrepareClaim(streamChangedAtCommit);
        streamChangedAtCommit.Progress(); streamChangedAtCommit.Deliver(Free, ArenaFixture.Free, true); streamChangedAtCommit.Sample();
        streamChangedAtCommit.AfterCommitRead = delegate { streamChangedAtCommit.Client.m_stream = new MemoryStream(); };
        Require(!ExecuteOffer(streamChangedAtCommit, "reward_confirm", delegate { }, out why) &&
            streamChangedAtCommit.Dialog.enabled && streamChangedAtCommit.ArenaUi.BonusCloseCalls == 0 &&
            streamChangedAtCommit.ArenaUi.Requests == 1 && !Global.extern_enabled && !__BaseUI.my_mouseclick,
            "an exchanged stream after admission leaves the original reward visible for native handling");

        ArenaFixture stopAtCommit = PrepareNavigation("open");
        stopAtCommit.AfterCommitRead = delegate { stopAtCommit.Model.Stop("fixture_stop_before_stock"); };
        Require(!ExecuteOffer(stopAtCommit, "open", delegate { }, out why) && stopAtCommit.CommitCalls == 1 &&
            stopAtCommit.LoaderCalls == 0 && !stopAtCommit.Model.Busy,
            "Stop during a successful external gate still precedes local stock admission");

        ArenaFixture expiredInsideCommit = PrepareNavigation("open");
        expiredInsideCommit.AfterCommitRead = delegate { System.Threading.Thread.Sleep(1510); };
        Require(!ExecuteOffer(expiredInsideCommit, "open", delegate { }, out why) &&
            expiredInsideCommit.CommitCalls == 1 && expiredInsideCommit.LoaderCalls == 0 && !expiredInsideCommit.Model.Busy,
            "an external gate returning after the local deadline cannot invoke stock");

        ArenaFixture once = PrepareNavigation("open_chest");
        once.CommitQuota = once.CommitCalls + 1;
        priorCalls = once.LoaderCalls;
        once.Step("open_chest");
        Require(once.CommitCalls == once.CommitQuota && once.LoaderCalls == priorCalls + 1 && once.Chest.interactive &&
            MainUI.m_subui == once.Chest && Global.extern_enabled && __BaseUI.my_mouseclick && once.ArenaUi.Requests == 0,
            "one admitted chest-open completes DisableAll and the native loader");
        once.CommitQuota = once.CommitCalls + 1;
        once.Step("claim");
        Require(once.CommitCalls == once.CommitQuota && once.ArenaUi.Requests == 1 && once.Chest.enabled &&
            !once.Chest.interactive && Global.extern_enabled && __BaseUI.my_mouseclick,
            "one admitted stock claim completes Disable and exactly one 1122");
        once.Progress(); once.Deliver(Free, ArenaFixture.Free, true); once.Sample();
        once.CommitQuota = once.CommitCalls + 1;
        once.Step("reward_confirm");
        Require(once.CommitCalls == once.CommitQuota && !once.Dialog.enabled && once.Chest.BonusCloseCalls == 1 &&
            once.Chest.interactive && !Global.extern_enabled && !__BaseUI.my_mouseclick && once.ArenaUi.Requests == 1,
            "one admitted reward confirmation completes Hide and stock chest OnClose");
        once.CommitQuota = once.CommitCalls + 1;
        once.DeferArenaShow = true;
        priorCalls = once.LoaderCalls;
        once.Step("close");
        Require(once.CommitCalls == once.CommitQuota && !once.Chest.enabled && MainUI.m_subui == once.ArenaUi &&
            once.LoaderCalls == priorCalls + 1 && once.ArenaUi.loading && once.ArenaUi.Requests == 1,
            "one admitted chest close completes Hide and the nested arena loader");
        once.DeferArenaShow = false; once.OpenArena();
        once.CommitQuota = once.CommitCalls + 1; once.Step("close");
        Require(once.Value("status") == "COMPLETED" && once.CommitCalls == once.CommitQuota &&
            Global.extern_enabled && __BaseUI.my_mouseclick && once.ArenaUi.Requests == 1,
            "stock loader return restores root and mouse state without another claim");

        ArenaFixture stopInsideAdmittedClaim = PrepareNavigation("open_chest");
        stopInsideAdmittedClaim.Step("open_chest");
        stopInsideAdmittedClaim.Chest.OnDisable = delegate { stopInsideAdmittedClaim.Model.Stop("fixture_stop_after_admission"); };
        priorCommits = stopInsideAdmittedClaim.CommitCalls;
        stopInsideAdmittedClaim.Step("claim");
        Require(stopInsideAdmittedClaim.CommitCalls == priorCommits + 1 && stopInsideAdmittedClaim.ArenaUi.Requests == 1 &&
            stopInsideAdmittedClaim.Value("status") == "UNKNOWN" && stopInsideAdmittedClaim.Value("next_action") == "" &&
            object.ReferenceEquals(stopInsideAdmittedClaim.Client.m_handlers[1122].handler, stopInsideAdmittedClaim.ArenaUi.LastStockHandler),
            "Stop during an admitted stock branch applies after its native send and restores the original callback");

        ArenaFixture stopInsideAdmittedReward = new ArenaFixture(true, false); PrepareClaim(stopInsideAdmittedReward);
        stopInsideAdmittedReward.Progress(); stopInsideAdmittedReward.Deliver(Free, ArenaFixture.Free, true); stopInsideAdmittedReward.Sample();
        stopInsideAdmittedReward.Dialog.OnHide = delegate { stopInsideAdmittedReward.Model.Stop("fixture_stop_after_admission"); };
        stopInsideAdmittedReward.Step("reward_confirm");
        Require(stopInsideAdmittedReward.Value("status") == "STOPPED" && stopInsideAdmittedReward.Value("next_action") == "" &&
            Global.extern_enabled && __BaseUI.my_mouseclick && stopInsideAdmittedReward.ArenaUi.BonusCloseCalls == 1,
            "Stop after admission cannot strand the exact stock reward between Hide and OnClose");

        Console.WriteLine("{\"arena_chest\":\"PASS\",\"assertions\":" + checks + "}");
        return 0;
    }
}
