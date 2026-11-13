using System;
using System.Collections;
using System.IO;
using System.Reflection;
using JiangShen.AssistantRuntime;
using UnityEngine;
using D = JiangShen.AssistantRuntime.SharedDevelopmentContext;
using U = UnityEngine.Object;

namespace UnityEngine
{
#if !JSB_MINGSHI_PROBE_TEST
    public class Object { public int GetInstanceID() { return 1; } }
    public sealed class Transform
    {
        public Transform parent;
        public Transform root { get { return parent == null ? this : parent.root; } }
    }
    public sealed class GameObject
    {
        public static readonly Hashtable Paths = new Hashtable();
        public bool activeInHierarchy = true;
        public readonly Transform transform = new Transform();
        public Collider collider;
        public Component Component;
        public static GameObject Find(string path) { return Paths[path] as GameObject; }
        public Component GetComponent(string name) { return Component != null && Component.GetType().Name == name ? Component : null; }
    }
    public class Component : Object
    {
        public GameObject gameObject = new GameObject();
        public Transform transform { get { return gameObject.transform; } }
    }
    public class Behaviour : Component { public bool enabled = true; }
    public static class Event { public static object current = new object(); }
    public static class GUI { public static bool enabled = true; }
    public static class Time { public static int frameCount = 1; }
#endif
    public sealed class Collider : Component { }
    public sealed class Camera : Behaviour { public static Camera main; }
}

#if !JSB_MINGSHI_PROBE_TEST
namespace JiangShen.AssistantRuntime
{
    internal sealed class ProbeFailure : Exception { internal ProbeFailure(string reason) : base(reason) { } }
    internal sealed class Snapshot
    {
        internal Component Root;
        internal object Character;
        internal bool InteractionEnabled = true;
        internal string Gate = "PASS";
    }
    internal sealed class Model
    {
        internal static Type FirstType(string name) { return Type.GetType(name, true); }
        internal static Type GameType(string name) { return FirstType(name); }
        internal Component CurrentRoot() { return BaseAsset.lds.currmg; }
        internal GameObject GlobalUiObject() { return GameObject.Find("/Root/RootUI"); }
        internal Type RootMessengerType { get { return typeof(RootMessenger); } }
        internal U[] AllUi() { return new U[0]; }
        internal Snapshot Capture() { return new Snapshot { Root = CurrentRoot(), Character = Global.character_ }; }
        internal string OverlayGate(Behaviour overlay) { return overlay.GetType().Name; }
    }
}
#endif

namespace Database
{
    public sealed class _buy_template
    {
        public enum TYPE { STONE = 7 }
        public uint id;
        public byte type = 7;
        public int count_min, count_max, gold;
    }
    public sealed class _vip_template { public byte level, godharvest_times = 1; }
    public sealed class _activity { public byte id = 7, idreward = 2; public bool active = true; public int cond_param = 1; }
    public sealed class _activity_reward { public byte id = 2; public bool active = true; }
    public sealed class Activity { public byte idactivity = 7, condition_param; public int refresh_time; public _activity temp; }
    public sealed class Activity_Reward
    {
        public byte idact_reward = 2;
        public readonly Hashtable acts = new Hashtable();
        public _activity_reward temp;
    }
    public sealed class GodTroop { public uint idmodel = 1; }
    public sealed class Animation { public string name = "fixture"; }
    public sealed class Character
    {
        public ulong id = 123;
        public ushort idcampaign = 42;
        public bool gm, unlocked = true;
        public uint next_resource_time = 304400, gold_consume;
        public int gold = 30, bind_gold = 7, stone = 500;
        public byte god_bonus_today, god_challenge_level = 5;
        public readonly GodTroop[] god_troops = { new GodTroop(), new GodTroop(), new GodTroop(), new GodTroop(), new GodTroop() };
        public _vip_template my_vip_template;
        public byte VIPLEVEL { get { return 0; } }
        public readonly Hashtable acts = new Hashtable(), act_rewards = new Hashtable();
        public bool checkUI(string name) { return name == "resource_collect" && unlocked; }
    }
}

namespace game.net
{
    public class tcp_base
    {
        public delegate void HandlerDelegate(MemoryStream body, object arg);
        public sealed class Handler
        {
            public HandlerDelegate handler;
            public float life;
            public object arg;
        }
        public sealed class HandleMessage { public ushort op; }
        public static int connection_state = 3;
        public bool m_bConnectLost;
        public readonly Hashtable m_handlers = new Hashtable();
        public readonly ArrayList messages = new ArrayList(), m_sendDatas = new ArrayList();

        public void Dispatch(ushort opcode, byte[] bytes, object foreignArg = null, bool remove = true)
        {
            lock (this)
            {
                Handler own = (Handler)m_handlers[opcode];
                if (remove && own.life > 0) m_handlers.Remove(opcode);
                using (MemoryStream stream = bytes == null ? null : new MemoryStream(bytes))
                    own.handler(stream, foreignArg ?? own.arg);
            }
        }
        public Handler Bind(ushort opcode, HandlerDelegate original, float life, object arg = null)
        {
            Handler binding = new Handler { handler = original, life = life, arg = arg };
            m_handlers[opcode] = binding;
            return binding;
        }
    }
    public sealed class tcp_client : tcp_base
    {
        public sealed class Socket { public bool Connected { get; set; } }
        public sealed class FeatureStream : MemoryStream
        {
            public bool Readable = true, Writable = true;
            public override bool CanRead { get { return Readable && base.CanRead; } }
            public override bool CanWrite { get { return Writable && base.CanWrite; } }
        }
        public readonly Socket m_client = new Socket { Connected = true };
        public Stream m_stream = new FeatureStream();
    }
}

public static class Global
{
    public static Database.Character character_;
    public static game.net.tcp_client m_client;
    public static bool relogin, extern_enabled = true;
    public static readonly Hashtable Tables = new Hashtable();
    public static Action BeforeActivityTemplate;
    public static Hashtable getTemplates(string name) { return Tables[name] as Hashtable; }
    public static object getTemplate(string name, uint id)
    {
        if (name == "activity" && BeforeActivityTemplate != null) BeforeActivityTemplate();
        Hashtable table = getTemplates(name);
        return table == null ? null : table[id];
    }
    public static Database._buy_template getBuyTemplate(Database._buy_template.TYPE type, int count)
    {
        foreach (Database._buy_template row in getTemplates("buy_template").Values)
            if (row.type == (byte)type && count >= row.count_min && count <= row.count_max) return row;
        return null;
    }
}

public sealed class RootMessenger : Component
{
    public string LevelID;
    public bool bLoading;
    public int ExpectingCount;
}
public static class BaseAsset { public static LoadScreen lds; }
public sealed class LoadScreen : Behaviour
{
    public bool isBattling;
    public RootMessenger currmg, oldrmg, oldmainrmg;
}
public class __BaseUI : Behaviour
{
    public static int UICount_Visible;
    public RootMessenger rmg;
    public bool first, visible, loading, collecting, m_bInit = true;
    public DateTime visible_time;
    public object OnClose;
    public bool IsVisible() { return visible; }
    public bool IsEnable() { return visible; }
}
public class BaseUI : __BaseUI { public static RootUI rootUI; }
public sealed class RootUI : BaseUI
{
    public static game.net.tcp_client m_client;
    public static bool m_bRefreshFive;
    public RootMessenger level_rmg;
    public static game.net.tcp_base.Handler BindClock(game.net.tcp_client client)
    { return client.Bind(36, sc_player_refresh_fiveclock_0x24, -1f); }
    private static void sc_player_refresh_fiveclock_0x24(MemoryStream body, object arg) { }
    public void BeginCampaignReturn(RootMessenger from)
    {
        __BaseUI.UICount_Visible++;
        m_client.Bind(1025, sc_campaign_enter, 10f, from);
    }
    private void sc_campaign_enter(MemoryStream stream, object arg)
    {
        __BaseUI.UICount_Visible--;
        if (stream == null || !ReferenceEquals(arg, MainUI.Current.DivinityRoot)) return;
        if (stream.ReadByte() == 1) MainUI.Current.BeginReturnLoad();
        else PopupUI.popup = true;
    }
}
public sealed class MainUI : BaseUI
{
    public static __BaseUI m_subui;
    public static DailyMingshiTests.Fixture Current;
    public static void _resource_collect() { Current.Open(); }
}
public sealed class DivinityUI : BaseUI
{
    public static GameObject mesh, stone_mesh;
    public int Calls, Replies, PremiumModals, Returns;
    public byte[] Request;
    public void wantHarvest()
    {
        Calls++;
        Database._buy_template quote = Global.getBuyTemplate(Database._buy_template.TYPE.STONE, Global.character_.god_bonus_today + 1);
        if (quote != null && quote.gold > 0) { PremiumModals++; return; }
        Request = new byte[] { 8, Global.character_.god_bonus_today };
        Global.m_client.Bind(1202, sc_god_harvest_0x4B2, 10f);
    }
    private void sc_god_harvest_0x4B2(MemoryStream body, object arg)
    {
        Replies++;
        if (body == null) return;
        // Fixed stock fixture cases. Keep the original's projection independent
        // from the production observer's stricter validation.
        if (body.ReadByte() != 8 || body.ReadByte() != 0) return;
        int field = body.ReadByte();
        if (field == 16)
        {
            Global.character_.gold -= body.ReadByte();
            field = body.ReadByte();
        }
        if (field != 24) return;
        Global.character_.stone += body.ReadByte();
        if (body.ReadByte() == 32) Global.character_.god_bonus_today = (byte)body.ReadByte();
    }
    private void _return() { Returns++; MainUI.Current.Return(); }
}
public static class RootBase
{
    public static int Replies;
    public static bool PartialProjection;
    public static game.net.tcp_base.Handler Bind(game.net.tcp_client client)
    { return client.Bind(355, handelActvity, -1f); }

    private static void handelActvity(MemoryStream stream, object arg)
    {
        Replies++;
        DailyTimedWire.Progress value;
        if (stream == null || !DailyTimedWire.ReadProgress(stream.ToArray(), 7, out value) || value == null) return;
        Database.Character character = Global.character_;
        Database.Activity row = new Database.Activity { temp = (Database._activity)Global.getTemplate("activity", 7),
            condition_param = (byte)value.Count, refresh_time = value.Refresh };
        character.acts[7u] = row;
        if (!PartialProjection) ((Database.Activity_Reward)character.act_rewards[2u]).acts[7u] = row;
    }
}
public sealed class CampaignUI : BaseUI { public int m_campaignID = 42; }
public sealed class CampaignSignScript : BaseUI { public CampaignUI ParentUI; }
public static class BuildingBuildUI { public static bool m_bBuildVisible; }
public static class BuildingBehaviourScript { public static bool m_bEditVisible; }
public static class PopupUI { public static bool popup; }
public static class TimeUtil { public static uint _now = 220000, _thisDay = 200000; }

public static class DailyMingshiTests
{
    private const string Id = "00000000000000000000000000000071";
    internal static readonly byte[] Harvest = { 8, 0, 24, 5, 32, 1 };
    internal static readonly byte[] Progress = { 10, 4, 8, 7, 16, 1 };

    public sealed class Fixture
    {
        internal readonly DailyMingshiModel Daily;
        internal readonly game.net.tcp_client Client = new game.net.tcp_client();
        internal readonly Database.Character Role = new Database.Character();
        internal readonly RootMessenger MainRoot = new RootMessenger { LevelID = "Capital" };
        internal readonly RootMessenger DivinityRoot = new RootMessenger { LevelID = "Divinity" };
        internal readonly RootUI GlobalUi = new RootUI();
        internal readonly LoadScreen Load = new LoadScreen { enabled = false };
        internal readonly DivinityUI Panel = new DivinityUI();
        internal readonly MainUI Main = new MainUI { visible = true };
        internal readonly CampaignUI CampaignMap = new CampaignUI();
        internal readonly CampaignUI OtherCampaignMap = new CampaignUI { m_campaignID = 43 };
        internal readonly CampaignSignScript CampaignSign = new CampaignSignScript();
        internal readonly BaseUI Foreign = new BaseUI();
        internal readonly game.net.tcp_base.Handler ActivityHandler;
        internal readonly game.net.tcp_base.HandlerDelegate StockActivity;
        internal int Sequence;
        internal uint Cycle = 218000;
        internal string RefreshGate = "", ExternalGate = "PASS";
        internal bool Paused, OpenThrows, ReturnThrows, ReturnNoOp;
        internal int OpenCalls, CampaignReturnCalls;

        internal Fixture(bool campaign = false)
        {
            Global.character_ = Role;
            Global.m_client = RootUI.m_client = Client;
            Global.extern_enabled = true; Global.relogin = false;
            PopupUI.popup = false; MainUI.m_subui = null; RootBase.PartialProjection = false;
            RootBase.Replies = 0; __BaseUI.UICount_Visible = 0;
#if JSB_MINGSHI_PROBE_TEST
            Event.current = new Event();
#else
            Event.current = new object();
#endif
            GUI.enabled = true; Global.BeforeActivityTemplate = null;
            Global.Tables.Clear();
            Global.Tables["buy_template"] = new Hashtable();
            Global.Tables["god_yield"] = new Hashtable { { 0u, new object() } };
            Global.Tables["troop_animation"] = new Hashtable { { 1u, new Database.Animation() } };
            AddQuote(19, 2, 100);
            Role.my_vip_template = new Database._vip_template();
            Global.Tables["vip_template"] = new Hashtable { { 0u, Role.my_vip_template } };
            Database._activity template = new Database._activity();
            Database._activity_reward rewardTemplate = new Database._activity_reward();
            Global.Tables["activity"] = new Hashtable { { 7u, template } };
            Global.Tables["activity_reward"] = new Hashtable { { 2u, rewardTemplate } };
            Database.Activity row = new Database.Activity { temp = template };
            Database.Activity_Reward reward = new Database.Activity_Reward { temp = rewardTemplate };
            Role.acts[7u] = reward.acts[7u] = row;
            Role.act_rewards[2u] = reward;
            if (campaign)
            {
                MainRoot.LevelID = "Campaign";
                CampaignMap.visible = true;
            }
            CampaignMap.rmg = MainRoot; CampaignMap.transform.parent = MainRoot.transform;
            CampaignSign.ParentUI = CampaignMap; CampaignSign.visible = campaign;
            Load.currmg = GlobalUi.level_rmg = MainRoot;
            BaseAsset.lds = Load; BaseUI.rootUI = GlobalUi; MainUI.Current = this;
            GlobalUi.rmg = new RootMessenger { LevelID = "Root" };
            GlobalUi.transform.parent = GlobalUi.rmg.transform;
            Panel.rmg = DivinityRoot; Panel.transform.parent = DivinityRoot.transform;
            Main.rmg = MainRoot; Main.transform.parent = MainRoot.transform;
            GameObject.Paths.Clear();
            Register("/Root/RootUI", GlobalUi); Register("/LoadUI", Load);
            Register("/Divinity", DivinityRoot); Register("/Divinity/DivinityUI", Panel);
            DivinityUI.mesh = DivinityUI.stone_mesh = null;
            Camera.main = new Camera();
            ActivityHandler = RootBase.Bind(Client);
            StockActivity = ActivityHandler.handler;
            Daily = new DailyMingshiModel(DevelopmentCallbackCapture.InstallDailyMingshi,
                DevelopmentCallbackCapture.InstallDailyActivityPush, ReadRefresh);
        }

        internal void AddQuote(uint id, int count, int gold)
        {
            Global.getTemplates("buy_template").Add(id,
                new Database._buy_template { id = id, count_min = count, count_max = count, gold = gold });
        }
        private string ReadRefresh(object role, out uint now, out uint day, out uint cycle)
        {
            now = 220000; day = 200000; cycle = Cycle; return RefreshGate;
        }
        private static void Register(string path, Component component)
        {
            component.gameObject.Component = component;
            GameObject.Paths[path] = component.gameObject;
        }
        internal U[] AllUi()
        { return new U[] { GlobalUi, Panel, Main, CampaignMap, OtherCampaignMap, CampaignSign, Foreign }; }
        internal void ShowOtherCampaign(bool sameRoot)
        {
            OtherCampaignMap.rmg = sameRoot ? MainRoot : new RootMessenger { LevelID = "Campaign" };
            OtherCampaignMap.transform.parent = OtherCampaignMap.rmg.transform;
            OtherCampaignMap.visible = true;
        }
        internal game.net.tcp_base.Handler ReplaceActivityRegistration()
        { return Client.Bind(355, StockActivity, -1f); }
        internal D Read()
        {
            return new D(AllUi(), Load.currmg, GlobalUi, Global.character_,
                Global.character_.id.ToString(), ExternalGate, Paused, delegate(Behaviour ui)
                { return ReferenceEquals(ui, CampaignSign) && ReferenceEquals(CampaignSign.ParentUI, CampaignMap) ? null : "foreign"; });
        }
        internal void Capture() { Daily.Capture(Read()); }
        internal bool Queue(string action)
        {
            string why;
            return Daily.Queue(7, Role.id, Id, ++Sequence, Daily.Revision, action, out why);
        }
        internal bool Execute(DevelopmentPublication publish = null, DailyMingshiInvocationCheck beforePrepare = null, Action afterPrepare = null)
        {
            string why;
            bool ok = Daily.ExecuteGui(Read, publish ?? delegate { }, delegate(DailyMingshiInvocationCheck prepare, DailyMingshiInvocationCheck check)
            {
                if ((beforePrepare != null && !beforePrepare()) || !prepare()) return false;
                if (afterPrepare != null) afterPrepare();
                return check();
            }, out why);
            if (!ok && why == "") throw new Exception("missing rejection reason");
            return ok;
        }
        internal void Open()
        {
            OpenCalls++;
            Load.oldrmg = Load.oldmainrmg = MainRoot;
            Load.currmg = GlobalUi.level_rmg = DivinityRoot;
            Load.enabled = Panel.loading = DivinityRoot.bLoading = true;
            Global.extern_enabled = Main.visible = CampaignMap.visible = false;
            CampaignSign.visible = false;
            if (OpenThrows) throw new IOException("fixture_open_threw");
        }
        internal void Ready()
        {
            Load.enabled = Panel.loading = DivinityRoot.bLoading = false;
            Panel.visible = Global.extern_enabled = true;
            Panel.visible_time = new DateTime(2026, 1, 1);
            DivinityUI.mesh = new GameObject(); DivinityUI.mesh.transform.parent = Panel.transform;
            DivinityUI.stone_mesh = new GameObject(); DivinityUI.stone_mesh.transform.parent = DivinityUI.mesh.transform;
            DivinityUI.stone_mesh.collider = new Collider();
        }
        internal void Return()
        {
            if (ReturnThrows) throw new IOException("fixture_return_threw");
            if (ReturnNoOp) return;
            if (MainRoot.LevelID == "Campaign")
            {
                CampaignReturnCalls++;
                GlobalUi.BeginCampaignReturn(DivinityRoot);
            }
            else
                FinishReturn();
        }
        internal void BeginReturnLoad()
        {
            Load.oldrmg = DivinityRoot;
            Load.currmg = GlobalUi.level_rmg = MainRoot;
            Load.enabled = MainRoot.bLoading = true;
            Global.extern_enabled = false;
        }
        internal void FinishReturn()
        {
            Load.oldrmg = DivinityRoot;
            Load.currmg = GlobalUi.level_rmg = MainRoot;
            Load.enabled = MainRoot.bLoading = DivinityRoot.bLoading = Panel.loading = false;
            Panel.visible = false;
            Global.extern_enabled = Main.visible = true;
            CampaignMap.visible = MainRoot.LevelID == "Campaign";
            CampaignSign.visible = CampaignMap.visible;
        }
        internal void Settle()
        {
            Send();
            Client.Dispatch(1202, Harvest);
            Client.Dispatch(355, Progress);
            Capture();
            Check(Has("SETTLED") && Daily.Busy && AccountObserved, "settled_owns_panel", this);
            Capture();
        }
        internal bool AccountObserved { get { return Daily.OperationJson().Contains("\"account_progress_observed\":true"); } }
        internal bool PanelClosed { get { return Daily.OperationJson().Contains("\"panel_closed\":true"); } }        internal void OpenAndReady()
        {
            Capture();
            Check(Queue("open"), "open_queued", this);
            Check(Execute(), "stock_open_invoked", this);
            Capture();
            Check(Has("OPENING"), "wait_for_assets", this);
            Ready(); Capture();
            Check(Has("OPEN"), "view_ready", this);
        }
        internal void Send()
        {
            OpenAndReady();
            Check(Queue("harvest") && Execute(), "stock_send_invoked", this);
            Check(Panel.Calls == 1 && Panel.Request.Length == 2 && Panel.Request[0] == 8 && Panel.Request[1] == 0 &&
                Panel.PremiumModals == 0 && Daily.MutationAttempted, "one_normal_first_request", this);
        }
        internal bool Has(string status)
        { return Daily.OperationJson().Contains("\"status\":\"" + status + "\""); }
        internal void AssertUnknown()
        {
            Capture();
            Check(Daily.OutcomeUnknown && Daily.Busy && Panel.Calls == 1 && !Queue("harvest") &&
                ReferenceEquals(ActivityHandler.handler, StockActivity), "unknown_preserves_stock_and_blocks_retry", this);
        }
    }

    private static void Check(bool pass, string code, Fixture fixture = null)
    {
        if (!pass) throw new Exception(code + (fixture == null ? "" : " " + fixture.Daily.OperationJson() + " " + fixture.Daily.ViewJson()));
    }

    private static void Happy(bool pushFirst)
    {
        Fixture f = new Fixture();
        if (pushFirst) f.AddQuote(99, 1, 0);
        f.Send();
        f.Client.Dispatch(pushFirst ? (ushort)355 : (ushort)1202, pushFirst ? Progress : Harvest);
        f.Capture();
        Check(f.Has("PENDING"), "one_witness_insufficient", f);
        f.Client.Dispatch(pushFirst ? (ushort)1202 : (ushort)355, pushFirst ? Harvest : Progress);
        f.Capture();
        Check(f.Has("SETTLED") && f.Role.stone == 505 && f.Role.god_bonus_today == 1 &&
            f.Role.gold == 30 && f.Role.bind_gold == 7 && f.Panel.Replies == 1 && RootBase.Replies == 1 &&
            ReferenceEquals(f.ActivityHandler.handler, f.StockActivity), "both_originals_projected", f);
        f.Capture();
        Check(f.Queue("close") && f.Execute(), "stock_close_invoked", f);
        f.Capture();
        Check(f.Has("SUCCEEDED") && !f.Daily.Busy && f.Panel.Returns == 1 && f.Daily.OperationJson().Contains("\"panel_closed\":true"), "stock_return_verified", f);
        Check(!f.Queue("open"), "completed_cycle_stays_closed", f);
    }

    private static void Admission()
    {
        Fixture f = new Fixture(); f.AddQuote(99, 1, 1); f.Capture();
        Check(!f.Queue("open") && f.Panel.Calls == 0 && f.Daily.ViewJson().Contains("mingshi_paid_quote_forbidden"), "paid_first_forbidden", f);
        f = new Fixture(); Global.getTemplates("buy_template").Clear(); f.Capture();
        Check(!f.Queue("open"), "unloaded_prices_not_free", f);
        f = new Fixture(); f.AddQuote(98, 1, 0); f.AddQuote(99, 1, 0); f.Capture();
        Check(!f.Queue("open"), "ambiguous_quote_refused", f);
        f = new Fixture(); f.Role.my_vip_template.godharvest_times = 0; f.Capture();
        Check(!f.Queue("open"), "entitlement_required", f);
        f = new Fixture(); f.RefreshGate = "daily_refresh_unverified"; f.Capture();
        Check(!f.Queue("open"), "refresh_proof_required", f);
        f = new Fixture();
        ((Database.Activity_Reward)f.Role.act_rewards[2u]).acts[7u] = new Database.Activity { temp = ((Database.Activity)f.Role.acts[7u]).temp };
        f.Capture();
        Check(!f.Queue("open"), "same_counts_with_foreign_cache_row_refused", f);
        f = new Fixture(); f.Capture(); Check(f.Queue("open") && f.Execute(), "asset_wait_setup", f);
        f.Ready(); DivinityUI.stone_mesh.collider = null; f.Capture();
        Check(!f.Queue("harvest") && f.Has("OPENING"), "missing_collider_waits", f);
        f = new Fixture(); f.OpenAndReady(); Check(f.Queue("harvest"), "fresh_quote_setup", f);
        Check(!f.Execute(delegate { f.AddQuote(99, 1, 100); }) && f.Panel.Calls == 0, "paid_quote_changed_during_publication", f);
        f = new Fixture(); f.OpenAndReady(); Check(f.Queue("harvest"), "external_panel_setup", f);
        Check(!f.Execute(delegate { f.Foreign.visible = true; }) && f.Panel.Calls == 0, "external_ui_after_publication", f);
        f = new Fixture(); f.OpenAndReady(); Check(f.Queue("harvest"), "final_lease_setup", f);
        Check(!f.Execute(null, delegate { return false; }) && f.Panel.Calls == 0 &&
            ReferenceEquals(f.ActivityHandler.handler, f.StockActivity), "final_lease_refuses_and_restores", f);
        f = new Fixture(); f.OpenAndReady(); Check(f.Queue("harvest"), "stop_setup", f);
        Check(!f.Execute(null, delegate { f.Daily.Stop("stop"); return true; }) && f.Panel.Calls == 0 &&
            ReferenceEquals(f.ActivityHandler.handler, f.StockActivity), "stop_wins_before_stock_action", f);
    }

    private static void CallbackFailures()
    {
        Fixture f = new Fixture(); f.Send();
        f.Client.Dispatch(1202, new byte[] { 8, 0, 24, 5, 32, 1, 8, 0 });
        f.AssertUnknown(); Check(f.Panel.Replies == 1, "malformed_still_forwarded");
        f = new Fixture(); f.Send();
        f.Client.Dispatch(1202, new byte[] { 8, 0, 16, 1, 24, 5, 32, 1 });
        f.AssertUnknown();
        f = new Fixture(); f.Send();
        f.Client.Dispatch(1202, Harvest, new object());
        f.AssertUnknown(); Check(f.Panel.Replies == 1, "foreign_argument_forwarded");
        f = new Fixture(); f.Send();
        f.Client.Dispatch(1202, Harvest, null, false);
        f.AssertUnknown(); Check(f.Panel.Replies == 1, "nondequeued_callback_forwarded");
        f = new Fixture(); f.Send();
        game.net.tcp_base.Handler foreign = f.Client.Bind(1202, delegate { }, 10f);
        f.AssertUnknown(); Check(ReferenceEquals(f.Client.m_handlers[(ushort)1202], foreign), "foreign_handler_preserved");
        f = new Fixture(); f.Send();
        f.Client.Dispatch(355, Progress); f.Client.Dispatch(355, Progress);
        f.AssertUnknown();
        f = new Fixture(); f.Send();
        f.Client.Dispatch(355, new byte[] { 10, 4, 8, 8, 16, 1 });
        f.Capture(); Check(f.Has("PENDING"), "foreign_task_does_not_verify_progress", f);
        f.Client.Dispatch(1202, Harvest);
        f.Capture(); Check(f.Has("PENDING"), "wrong_task_and_harvest_insufficient", f);
        f = new Fixture(); f.Send();
        RootBase.PartialProjection = true;
        f.Client.Dispatch(355, Progress);
        f.AssertUnknown();
        f = new Fixture(); f.Send();
        f.Role.bind_gold--;
        f.AssertUnknown();
        f = new Fixture(); f.Send();
        f.Daily.Stop("stop");
        f.Client.Dispatch(1202, Harvest); f.Client.Dispatch(355, Progress);
        f.AssertUnknown();
        Check(f.Role.stone == 505 && f.Panel.Replies == 1 && RootBase.Replies == 1 && f.Panel.Returns == 0,
            "late_replies_use_original_and_do_not_claim_completion", f);
        f = new Fixture(); f.Send();
        f.Client.Dispatch(1202, null);
        f.AssertUnknown();
        f = new Fixture(); f.Send();
        typeof(DailyMingshiModel).GetField("started", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(f.Daily, unchecked(Environment.TickCount - 18001));
        f.AssertUnknown();
        f.Client.Dispatch(1202, Harvest);
        Check(f.Panel.Replies == 1 && f.Role.stone == 505 && f.Daily.OutcomeUnknown, "late_response_does_not_restart_observation", f);
        f = new Fixture(); f.Send();
        f.Client.Dispatch(1202, Harvest);
        f.Client.Dispatch(355, new byte[] { 10, 6, 8, 7, 16, 1, 24, 20 });
        f.Capture();
        Check(f.Has("SETTLED"), "changed_activity_refresh_matches_355", f);
    }

    private static void OwnershipChanges()
    {
        Fixture f = new Fixture(); f.Send();
        f.Client.m_stream = new MemoryStream(); f.AssertUnknown();
        f = new Fixture(); f.Send();
        Global.character_ = new Database.Character();
        f.AssertUnknown();
        f = new Fixture(); f.Send();
        f.Cycle += 86400; f.AssertUnknown();
        f = new Fixture(); f.Send();
        f.Role.next_resource_time++; f.AssertUnknown();
        f = new Fixture(); f.Send();
        f.Foreign.visible = true; f.AssertUnknown();
        f = new Fixture(); f.OpenAndReady();
        Check(f.Queue("harvest") && !f.Execute(delegate { f.Client.m_stream = new MemoryStream(); }) &&
            f.Panel.Calls == 0, "stream_changed_during_publication", f);
        f = new Fixture(); f.OpenAndReady();
        Check(f.Queue("harvest") && !f.Execute(delegate { f.Cycle += 86400; }) &&
            f.Panel.Calls == 0, "cycle_changed_during_publication", f);
    }

    private static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int ExpiredTick() { return unchecked(Environment.TickCount - 18001); }
    private static void ExpireWindow(Fixture f)
    { typeof(DailyMingshiModel).GetField("started", PrivateInstance).SetValue(f.Daily, ExpiredTick()); }
    private static void ExpireQueue(Fixture f)
    {
        object queued = typeof(DailyMingshiModel).GetField("queued", PrivateInstance).GetValue(f.Daily);
        queued.GetType().GetField("QueuedAt", PrivateInstance).SetValue(queued, ExpiredTick());
    }

    private static void RecoveryHolds()
    {
        foreach (string reason in new[] { "stop", "destroy" })
        {
            Fixture f = new Fixture(); f.Capture();
            Check(f.Queue("open"), "queue_before_stop", f);
            f.Daily.Stop(reason);
            Check(f.Has("REJECTED") && f.Daily.OperationJson().Contains("\"sequence\":1") &&
                f.Daily.OperationJson().Contains(Id) && !f.Daily.Busy && f.OpenCalls == 0 &&
                !f.Execute(), "queued_open_cancelled_without_hold", f);

            f = new Fixture(); f.OpenAndReady();
            f.Daily.Stop(reason);
            f.Daily.Capture(null);
            Check(f.Has("REJECTED") && f.Daily.Busy && f.Panel.Returns == 0 && !f.Queue("harvest"), "owned_panel_held_on_stop", f);
            f.FinishReturn(); f.Capture();
            Check(!f.Daily.Busy && !f.PanelClosed, "manual_return_can_release_ui_hold_after_stop", f);
        }

        Fixture opened = new Fixture { OpenThrows = true }; opened.Capture();
        Check(opened.Queue("open") && !opened.Execute() && opened.Has("REJECTED") &&
            opened.Daily.Busy && opened.OpenCalls == 1, "open_throw_latches_before_invocation", opened);
        opened.Capture();
        Check(opened.Daily.Busy && !opened.Queue("open"), "open_failure_cannot_repeat", opened);

        Fixture timed = new Fixture(); timed.Capture();
        Check(timed.Queue("open") && timed.Execute(), "open_timeout_setup", timed);
        ExpireWindow(timed); timed.Capture();
        Check(timed.Has("REJECTED") && timed.Daily.Busy && timed.OpenCalls == 1, "open_timeout_keeps_hold", timed);

        Fixture owner = new Fixture(); owner.OpenAndReady();
        owner.Foreign.visible = true; owner.Capture();
        Check(owner.Has("REJECTED") && owner.Daily.Busy, "external_overlay_keeps_hold", owner);
        owner.FinishReturn(); owner.Capture();
        Check(owner.Daily.Busy, "exact_root_under_overlay_is_not_verified", owner);
        owner.Foreign.visible = false; owner.Capture();
        Check(!owner.Daily.Busy, "restored_exact_ui_releases_hold", owner);

        owner = new Fixture(); owner.OpenAndReady();
        Global.character_ = new Database.Character();
        owner.FinishReturn(); owner.Capture();
        Check(owner.Has("REJECTED") && owner.Daily.Busy, "same_id_new_role_does_not_release", owner);

        owner = new Fixture(); owner.OpenAndReady(); owner.Cycle += 86400;
        owner.FinishReturn(); owner.Capture();
        Check(owner.Has("REJECTED") && owner.Daily.Busy, "other_cycle_does_not_release", owner);

        owner = new Fixture(); owner.OpenAndReady();
        owner.Load.currmg = owner.GlobalUi.level_rmg = new RootMessenger { LevelID = "Capital" };
        owner.Capture();
        Check(owner.Has("REJECTED") && owner.Daily.Busy, "similar_but_foreign_root_keeps_hold", owner);

        owner = new Fixture(); owner.Send();
        owner.Daily.Stop("stop"); owner.FinishReturn();
        owner.Client.Dispatch(1202, Harvest); owner.Client.Dispatch(355, Progress); owner.Capture();
        Check(owner.Daily.OutcomeUnknown && owner.Daily.Busy && !owner.PanelClosed, "unknown_mutation_keeps_recovery_after_return", owner);
    }

    private static void CampaignReturn()
    {
        Fixture f = new Fixture(true); f.Settle();
        Check(f.Queue("close") && f.Execute(), "campaign_return_queued", f);
        f.Capture();
        Check(f.Has("CLOSING") && f.Daily.Busy && !f.PanelClosed && f.CampaignReturnCalls == 1, "campaign_waits_1025", f);
        f.Client.Dispatch(1025, new byte[] { 1 }); f.Capture();
        Check(f.Has("CLOSING") && f.Daily.Busy && !f.PanelClosed, "campaign_waits_loader", f);
        f.FinishReturn(); f.GlobalUi.level_rmg = f.DivinityRoot; f.Capture();
        Check(f.Has("CLOSING") && f.Daily.Busy, "campaign_needs_both_root_pointers", f);
        f.GlobalUi.level_rmg = f.MainRoot;
        f.Panel.visible = true; f.Panel.enabled = false; f.Capture();
        Check(f.Has("CLOSING") && f.Daily.Busy, "disabled_divinity_still_needs_actual_hide", f);
        f.Panel.visible = false; f.Capture();
        Check(f.Has("SUCCEEDED") && f.PanelClosed && !f.Daily.Busy &&
            f.Panel.Calls == 1 && f.Panel.Returns == 1, "campaign_return_verified", f);

        f = new Fixture(true); f.Settle(); Check(f.Queue("close") && f.Execute(), "campaign_null_setup", f);
        f.Client.Dispatch(1025, null); f.Capture();
        Check(f.Has("CLOSING") && f.Daily.Busy && !f.PanelClosed, "null_1025_does_not_finish_return", f);
        ExpireWindow(f); f.Capture();
        Check(f.Has("SUCCEEDED") && f.AccountObserved && f.Daily.Busy && !f.PanelClosed &&
            f.CampaignReturnCalls == 1 && !f.Queue("close"), "null_1025_timeout_preserves_hold_and_progress", f);

        f = new Fixture(true); f.Settle(); Check(f.Queue("close") && f.Execute(), "campaign_failure_setup", f);
        f.Client.Dispatch(1025, new byte[] { 0 }); f.Capture();
        Check(PopupUI.popup && f.Has("CLOSING") && f.Daily.Busy && !f.PanelClosed, "stock_1025_failure_keeps_ownership", f);
        ExpireWindow(f); f.Capture();
        Check(f.Has("SUCCEEDED") && f.AccountObserved && f.Daily.Busy && !f.PanelClosed && !f.Queue("close"),
            "failed_campaign_return_never_retried", f);

        f = new Fixture(true); f.Settle(); Check(f.Queue("close") && f.Execute(), "campaign_stop_setup", f);
        f.Daily.Stop("destroy");
        f.Client.Dispatch(1025, new byte[] { 1 }); f.Capture();
        Check(f.Has("SUCCEEDED") && f.AccountObserved && f.Daily.Busy && !f.PanelClosed, "destroy_preserves_progress_during_1025", f);
        f.FinishReturn(); f.Capture();
        Check(!f.Daily.Busy && !f.PanelClosed, "late_return_after_destroy_releases_only_ui_hold", f);

        f = new Fixture(true); f.Settle(); Check(f.Queue("close") && f.Execute(), "other_campaign_setup", f);
        f.Client.Dispatch(1025, new byte[] { 1 }); f.FinishReturn();
        f.CampaignMap.m_campaignID = f.Role.idcampaign = 43; f.Capture();
        Check(f.Has("CLOSING") && f.Daily.Busy && !f.PanelClosed, "other_campaign_map_not_restoration", f);
        ExpireWindow(f); f.Capture(); f.Capture();
        Check(f.Has("SUCCEEDED") && f.AccountObserved && f.Daily.Busy && !f.PanelClosed, "wrong_map_keeps_hold", f);

        f = new Fixture(true); f.Settle(); Check(f.Queue("close") && f.Execute(), "campaign_conflict_setup", f);
        f.Client.Bind(1025, delegate { }, 10f);
        f.Capture();
        Check(f.Has("SUCCEEDED") && f.AccountObserved && f.Daily.Busy && !f.PanelClosed &&
            f.Panel.Returns == 1, "foreign_1025_preserved", f);
    }

    private static void CleanupAndDeadline()
    {
        foreach (bool shouldThrow in new[] { false, true })
        {
            Fixture f = new Fixture { ReturnThrows = shouldThrow, ReturnNoOp = !shouldThrow };
            f.Settle();
            Check(f.Queue("close") && f.Execute() != shouldThrow && f.Daily.Busy &&
                f.Panel.Returns == 1, "incomplete_return_setup", f);
            ExpireWindow(f); f.Capture();
            Check(f.Has("SUCCEEDED") && f.Daily.Busy && f.AccountObserved && !f.PanelClosed &&
                !f.Queue("close"), "no_op_or_throw_return_keeps_hold_without_retry", f);
        }

        Fixture cache = new Fixture(); cache.Settle();
        ((Database.Activity_Reward)cache.Role.act_rewards[2u]).acts[7u] = new Database.Activity();
        cache.Capture();
        Check(cache.Has("SUCCEEDED") && cache.AccountObserved && cache.Daily.Busy && !cache.Queue("close"),
            "stale_settled_cache_blocks_return", cache);

        cache = new Fixture(true); cache.Settle();
        Check(cache.Queue("close") && cache.Execute(), "return_cache_conflict_setup", cache);
        cache.Client.Dispatch(1025, new byte[] { 1 }); cache.FinishReturn();
        ((Database.Activity)cache.Role.acts[7u]).condition_param = 0; cache.Capture();
        Check(cache.Has("SUCCEEDED") && cache.AccountObserved && cache.Daily.Busy && !cache.PanelClosed,
            "changed_cache_at_return_blocks_release", cache);

        Fixture receipt = new Fixture(); receipt.Send();
        receipt.Client.Dispatch(1202, Harvest); receipt.Client.Dispatch(355, Progress);
        ExpireWindow(receipt); receipt.AssertUnknown();
        Check(!receipt.AccountObserved, "late_settlement_never_promoted", receipt);

        foreach (string action in new[] { "open", "harvest", "close" })
        {
            Fixture f = new Fixture();
            if (action == "open") f.Capture();
            else if (action == "harvest") f.OpenAndReady();
            else f.Settle();
            Check(f.Queue(action), "final_deadline_setup:" + action, f);
            Check(!f.Execute(null, delegate
            {
                if (action == "open") ExpireQueue(f);
                else ExpireWindow(f);
                return true;
            }) && f.OpenCalls == (action == "open" ? 0 : 1) &&
                f.Panel.Calls == (action == "close" ? 1 : 0) && f.Panel.Returns == 0 &&
                f.Daily.Busy == (action != "open"), "last_lease_boundary_refuses_expired_" + action, f);
        }

        Fixture gui = new Fixture(); gui.OpenAndReady(); Check(gui.Queue("harvest"), "final_gui_setup", gui);
        Check(!gui.Execute(null, delegate { GUI.enabled = false; return true; }) &&
            gui.Panel.Calls == 0 && gui.Daily.Busy, "final_gui_boundary_blocks_harvest", gui);
    }

    private static void LastLeaseRevalidation()
    {
        string[] cases = { "stream", "connect_lost", "foreign_panel", "overlay", "handler", "role", "cycle", "price" };
        foreach (string changed in cases)
        {
            Fixture f = new Fixture(); f.OpenAndReady();
            Check(f.Queue("harvest"), "last_lease_setup:" + changed, f);
            game.net.tcp_base.Handler foreignHandler = null;
            bool invoked = f.Execute(null, delegate
            {
                if (changed == "stream") f.Client.m_stream = new MemoryStream();
                else if (changed == "connect_lost") f.Client.m_bConnectLost = true;
                else if (changed == "foreign_panel")
                {
                    DivinityUI other = new DivinityUI { rmg = f.DivinityRoot, visible = true };
                    other.transform.parent = f.DivinityRoot.transform;
                    other.gameObject.Component = other;
                    GameObject.Paths["/Divinity/DivinityUI"] = other.gameObject;
                }
                else if (changed == "overlay") f.Foreign.visible = true;
                else if (changed == "handler") foreignHandler = f.Client.Bind(355, delegate { }, -1f);
                else if (changed == "role") Global.character_ = new Database.Character();
                else if (changed == "cycle") f.Cycle += 86400;
                else f.AddQuote(99, 1, 100);
                return true;
            });
            Check(!invoked && f.Has("REJECTED") && f.Daily.Busy && !f.Daily.MutationAttempted &&
                f.Panel.Calls == 0 && !f.Client.m_handlers.Contains((ushort)1202) && !f.Queue("harvest") &&
                ReferenceEquals(f.ActivityHandler.handler, f.StockActivity) &&
                (foreignHandler == null || ReferenceEquals(f.Client.m_handlers[(ushort)355], foreignHandler)),
                "last_lease_revalidates_" + changed, f);
        }
        foreach (string changed in new[] { "disconnect", "read-closed", "write-closed" })
        {
            Fixture f = new Fixture(); f.OpenAndReady();
            Check(f.Queue("harvest"), "post_read_health_setup_" + changed, f);
            bool reached = false;
            bool invoked = f.Execute(afterPrepare: delegate
            {
                reached = true;
                if (changed == "disconnect") f.Client.m_client.Connected = false;
                else
                {
                    game.net.tcp_client.FeatureStream stream = (game.net.tcp_client.FeatureStream)f.Client.m_stream;
                    if (changed == "read-closed") stream.Readable = false;
                    else stream.Writable = false;
                }
            });
            Check(reached && !invoked && f.Panel.Calls == 0 && !f.Daily.MutationAttempted &&
                ReferenceEquals(f.ActivityHandler.handler, f.StockActivity),
                "transport_guard_after_final_wire_read_" + changed, f);
        }
    }

    public static int Main()
    {
        Happy(false); Happy(true); Admission(); CallbackFailures(); OwnershipChanges();
        RecoveryHolds(); CampaignReturn(); CleanupAndDeadline(); LastLeaseRevalidation();
        Console.WriteLine("{\"daily_mingshi\":\"PASS\",\"scope\":\"production model, pinned dispatch shapes, stock projection and recovery fixtures\"}");
        return 0;
    }
}
