using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using JiangShen.AssistantRuntime;
using D = JiangShen.AssistantRuntime.SharedDevelopmentContext;

#if !JSB_REFINE_PROBE_TEST
namespace UnityEngine
{
    public class Object { public int GetInstanceID() { return 1; } }
    public class Transform { public Transform root { get { return this; } } }
    public class GameObject
    {
        public bool activeInHierarchy = true;
        public readonly Transform transform = new Transform();
        public Component Panel;
        public Component GetComponent(Type type) { return Panel != null && Panel.GetType() == type ? Panel : null; }
        public Component GetComponent(string name) { return Panel != null && Panel.GetType().Name == name ? Panel : null; }
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
}

namespace JiangShen.AssistantRuntime
{
    internal sealed class ProbeFailure : Exception { internal ProbeFailure(string why) : base(why) { } }
    internal static class Model
    {
        internal static Type FirstType(string name) { return Type.GetType(name, true); }
        internal static Type GameType(string name) { return FirstType(name); }
    }
}

#endif

public class __BaseUI : UnityEngine.Behaviour
{
    public static string subui = "";
    public UnityEngine.Component rmg;
    public DateTime visible_time;
    public Action OnClose;
    public bool loading, collecting, visible, first;
    public bool IsVisible() { return visible; }
    public bool IsEnable() { return true; }
}

public class BaseUI : __BaseUI { public static RootUI rootUI; }
public static class BuildingBuildUI { public static bool m_bBuildVisible; }
public static class PopupUI { public static bool popup; }
public static class BuildingBehaviourScript { public static bool m_bEditVisible; }
public sealed class RootUI : BaseUI
{
    public static game.net.tcp_client m_client;
    public static bool m_bRefreshFive;
    public string LevelID = "Capital";
}

public static class BaseAsset
{
    public sealed class LoadScreen { public UnityEngine.Component currmg; }
    public static LoadScreen lds = new LoadScreen();
    public static bool _PHONEUI;
}

public static class TimeUtil { public static uint _now = 220000, _thisDay = 200000; }

public sealed class MainUI : __BaseUI
{
    public static MainUI Current;
    public bool m_bInit = true;
    public static __BaseUI m_subui;
    public static ArmyStarRefineUI Panel;
    public static int Opens, Closes;
    public static bool ThrowAfterOpen, CloseKeepsSelection, CloseKeepsVisible, CloseThrows;

    public static void _starRefine()
    {
        Opens++;
        __BaseUI.subui = "star_refine";
        m_subui = Panel;
        Panel.loading = true;
        Global.extern_enabled = false;
        if (ThrowAfterOpen) throw new IOException("fixture_open_failed");
    }

    public static void SetSubUI(__BaseUI value)
    {
        Closes++;
        if (CloseThrows) throw new IOException("fixture_close_failed");
        if (!CloseKeepsVisible) m_subui.visible = false;
        if (!CloseKeepsSelection) m_subui = value;
    }
}

public static class RootBase
{
    public static int Pushes;
    public static Action InActivity;
    public static bool ListedToo = true;

    private static void handelActvity(MemoryStream stream, object arg)
    {
        Pushes++;
        if (InActivity != null) InActivity();
        if (stream == null || stream.ReadByte() != 10) return;
        stream.ReadByte();
        if (stream.ReadByte() != 8 || stream.ReadByte() != 21 || stream.ReadByte() != 16) return;
        Database.Activity row = new Database.Activity { condition_param = (byte)stream.ReadByte() };
        if (stream.ReadByte() != 24) return;
        int refresh = 0, shift = 0;
        for (int value; (value = stream.ReadByte()) != -1; shift += 7)
            refresh |= (value & 127) << shift;
        row.refresh_time = refresh >> 1;
        Global.character_.acts[21u] = row;
        if (ListedToo) DailyRefineTests.Listing[21u] = row;
    }

    public static void Install() { Global.m_client.Put(355, handelActvity).life = -1f; }
}

public sealed class ArmyStarRefineUI : __BaseUI
{
    public enum RefineType { jingyan, qianbi }
    private sealed class FlyEffectGroup { public int practice; }
    private readonly Dictionary<int, FlyEffectGroup> fly_effects = new Dictionary<int, FlyEffectGroup>();
    public RefineType m_refinetype;
    public bool sended, ThrowOnSend;
    public Action InOriginal;
    public int Requests, Replies, XpRequests, GoldRequests;

    public void Show()
    {
        loading = false;
        visible = Global.extern_enabled = true;
        visible_time = DateTime.Now;
        m_refinetype = RefineType.qianbi;
    }

    public void cs_practice_money_exchange()
    {
        if (sended) return;
        Requests++; sended = true;
        Global.m_client.Put(991, sc_practice_money_exchange);
        if (ThrowOnSend) throw new IOException("fixture_send_failed");
    }

    public void sc_practice_money_exchange(MemoryStream stream, object arg)
    {
        Replies++;
        if (InOriginal != null) InOriginal();
        sended = false;
        if (stream == null || stream.ReadByte() != 8 || stream.ReadByte() != 0 || stream.ReadByte() != 16) return;
        int cost = stream.ReadByte();
        if (stream.ReadByte() != 26 || stream.ReadByte() != 8 || stream.ReadByte() != 8 ||
            stream.ReadByte() != 2 || stream.ReadByte() != 16 || stream.ReadByte() != 0 || stream.ReadByte() != 24) return;
        int practice = stream.ReadByte() >> 1;
        Database.Character character = Global.character_;
        character.practice_money_exchange_times++;
        character.money64 += character.money - cost;
        character.money = 0;
        fly_effects.Add(1, new FlyEffectGroup { practice = practice });
    }

    public void DrainFlyEffects()
    {
        foreach (FlyEffectGroup group in fly_effects.Values) Global.character_.practice += group.practice;
        fly_effects.Clear();
    }

    public void cs_practice_exchange() { XpRequests++; Global.m_client.Put(860, delegate { }); }
    public void cs_practice_exchange_gold() { GoldRequests++; Global.m_client.Put(861, delegate { }); }
}

public static class Global
{
    public static Database.Character character_;
    public static game.net.tcp_client m_client;
    public static bool extern_enabled = true, relogin;
    public static Database._activity Task
    {
        get { return (Database._activity)((Hashtable)Database.db_loader.Global["activity"])[21u]; }
        set { Database.db_loader.Global["activity"] = new Hashtable { { 21u, value } }; }
    }
    public static Hashtable Prices
    {
        get { return (Hashtable)Database.db_loader.Global["practice_money_exchange"]; }
        set { Database.db_loader.Global["practice_money_exchange"] = value; }
    }
    public static Action BeforeQuotes;
    public static object getTemplate(string name, uint id) { return name == "activity" && id == 21 ? Task : null; }
    public static Hashtable getTemplates(string name)
    {
        if (BeforeQuotes != null) BeforeQuotes();
        return name == "practice_money_exchange" ? Prices : null;
    }
}

namespace Database
{
    public static class db_loader { public static readonly Hashtable Global = new Hashtable(); }

    public sealed class Character
    {
        public ulong id = 45;
        public uint next_resource_time = 304400, gold_consume;
        public int gold = 100, bind_gold = 5, experience = 22, practice = 90, money = 25, practice_money_exchange_times;
        public long money64 = 5000000000;
        public short level = 85, practice_exchange = 1, practice_exchange_gold = 2;
        public bool gm, Unlocked = true;
        public readonly Hashtable acts = new Hashtable(), act_rewards = new Hashtable();
        public bool checkUI(uint id) { return id == 91 && Unlocked; }
    }
    public sealed class _activity { public byte id = 21, idreward = 2; public bool active = true; public int cond_param = 1; }
    public sealed class _practice_money_exchange { public ushort level = 85, times = 1; public uint money = 40; }
    public sealed class Activity
    {
        public byte idactivity = 21, condition_param;
        public int refresh_time = 304400;
        public _activity temp = Global.Task;
    }
    public sealed class Activity_Reward { public byte idact_reward = 2; public readonly Hashtable acts = new Hashtable(); }
}

namespace game.net
{
    public class tcp_base
    {
        public delegate void delegate_handler(MemoryStream stream, object arg);
        public sealed class Handler
        {
            public delegate_handler handler;
            public object arg;
            public float life = 10f;
        }
        public sealed class Message { public ushort op; }
        public static int connection_state = 3;
        public bool m_bConnectLost;
        public readonly Hashtable m_handlers = new Hashtable();
        public readonly ArrayList messages = new ArrayList();
    }
    public sealed class tcp_client : tcp_base
    {
        public sealed class Socket
        {
            public bool Healthy = true;
            public Action BeforeConnected;
            public bool Connected { get { if (BeforeConnected != null) BeforeConnected(); return Healthy; } }
        }
        public Socket m_client = new Socket();
        public Stream m_stream = new MemoryStream();
        public readonly ArrayList m_sendDatas = new ArrayList();
        public Handler Put(ushort opcode, delegate_handler callback)
        {
            Handler handler = new Handler { handler = callback };
            m_handlers[opcode] = handler;
            return handler;
        }
        public Handler HandlerFor(ushort opcode) { return (Handler)m_handlers[opcode]; }
        public void Deliver(ushort opcode, byte[] bytes, bool remove)
        {
            lock (this)
            {
                Handler registered = HandlerFor(opcode);
                if (remove) m_handlers.Remove(opcode);
                registered.handler(bytes == null ? null : new MemoryStream(bytes), registered.arg);
            }
        }
    }
}


internal static class DailyRefineTests
{
    private const string Id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", OtherId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly byte[] Ok = { 8, 0, 16, 40, 26, 8, 8, 2, 16, 0, 24, 26, 32, 0 };
    private static readonly byte[] Refused = { 8, 1, 16, 0, 26, 8, 8, 2, 16, 0, 24, 0, 32, 0 };
    private static ArmyStarRefineUI Panel { get { return MainUI.Panel; } }
    private static Database.Character Role { get { return Global.character_; } }
    internal static Hashtable Listing { get { return ((Database.Activity_Reward)Role.act_rewards[2u]).acts; } }
    private static game.net.tcp_client Client { get { return Global.m_client; } }
    private static Database._practice_money_exchange Quote { get { return (Database._practice_money_exchange)Global.Prices[1u]; } }

    private static void Check(bool condition, string name)
    { if (!condition) throw new InvalidOperationException(name); }

    private static string controllerGate = "PASS";

    private static D Context()
    {
        return new D(new UnityEngine.Object[] { BaseUI.rootUI, Panel }, BaseUI.rootUI, BaseUI.rootUI, Role, "45",
            Global.extern_enabled ? controllerGate : "loading_or_battle", false);
    }

    internal static void ResetGame()
    {
        Global.Task = new Database._activity();
        Global.Prices = new Hashtable { { 1u, new Database._practice_money_exchange() } };
        Global.BeforeQuotes = null;
        Global.character_ = new Database.Character();
        Role.act_rewards[2u] = new Database.Activity_Reward();
        Role.acts[21u] = Listing[21u] = new Database.Activity();
        BaseAsset.lds.currmg = BaseUI.rootUI = new RootUI();
        BaseUI.rootUI.rmg = BaseUI.rootUI;
        MainUI.Current = new MainUI { rmg = BaseUI.rootUI };
        MainUI.Panel = new ArmyStarRefineUI { gameObject = BaseUI.rootUI.gameObject, rmg = BaseUI.rootUI };
        BaseUI.rootUI.gameObject.Panel = Panel;
        MainUI.m_subui = null; MainUI.Opens = MainUI.Closes = 0; __BaseUI.subui = "";
        MainUI.ThrowAfterOpen = MainUI.CloseKeepsSelection = MainUI.CloseKeepsVisible = MainUI.CloseThrows = false;
        RootUI.m_client = Global.m_client = new game.net.tcp_client();
        RootBase.Pushes = 0; RootBase.InActivity = null; RootBase.ListedToo = true; RootBase.Install();
        Global.extern_enabled = true; Global.relogin = RootUI.m_bRefreshFive = BaseAsset._PHONEUI = false;
        BuildingBuildUI.m_bBuildVisible = PopupUI.popup = BuildingBehaviourScript.m_bEditVisible = false;
        UnityEngine.GUI.enabled = true;
        controllerGate = "PASS";
        TimeUtil._thisDay = 200000; TimeUtil._now = 220000;
    }

    private static DailyRefineModel Reset()
    {
        ResetGame();
        DailyRefineModel model = new DailyRefineModel(DevelopmentCallbackCapture.InstallDailyRefine,
            DevelopmentCallbackCapture.InstallDailyActivityPush, DailyRefreshGate.ReadClock);
        model.Capture(Context());
        return model;
    }

    private static bool Queue(DailyRefineModel model, string action, int sequence, out string why)
    { return model.Queue(21, 45, Id, sequence, model.Revision, action, out why); }

    private static bool PermitCommit(DailyRefineCommitCheck prepare, DailyRefineCommitCheck check) { return prepare() && check(); }

    private static bool Act(DailyRefineModel model, string action, int sequence)
    {
        string why;
        Check(Queue(model, action, sequence, out why), "queue " + action + " " + why + " " + model.ViewJson());
        Check(model.ExecuteGui(Context, delegate { }, PermitCommit, out why), "invoke " + action + " " + why);
        return true;
    }

    private static void Open(DailyRefineModel model)
    {
        Act(model, "open", 1);
        model.Capture(Context());
        Check(model.OperationJson().Contains("OPENING") && Panel.Requests == 0 && MainUI.Opens == 1, "waits for owned load");
        Panel.Show(); model.Capture(Context());
        Check(model.ViewJson().Contains("\"next_action\":\"refine\""), "refine available after stock load");
    }

    private static DailyRefineModel Sent()
    {
        DailyRefineModel model = Reset();
        Open(model); Act(model, "refine", 2);
        Check(model.MutationAttempted && model.Unresolved && Panel.Requests == 1 &&
            Panel.GoldRequests == 0 && Panel.XpRequests == 0, "single stock 991");
        return model;
    }

    internal static void Advance(byte progress = 1, int refresh = 304400)
    {
        int zigzag = refresh << 1;
        Client.Deliver(355, new byte[] { 10, 8, 8, 21, 16, progress, 24, (byte)((zigzag & 127) | 128),
            (byte)(((zigzag >> 7) & 127) | 128), (byte)(zigzag >> 14) }, false);
    }

    private static void Unknown(DailyRefineModel model, string name)
    {
        model.Capture(Context());
        string why;
        Check(model.Busy && model.OutcomeUnknown && Panel.Requests == 1 && MainUI.Closes == 0 &&
            !Queue(model, "close", 3, out why) && !Queue(model, "refine", 3, out why) &&
            !model.Queue(21, 45, OtherId, 3, model.Revision, "open", out why), name + " " + model.OperationJson());
        Check(Client.HandlerFor(355).handler.Method.Name == "handelActvity", "355 restored " + name);
    }

    private static void Expire(DailyRefineModel model)
    {
        typeof(DailyRefineModel).GetField("since", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(model, unchecked(Environment.TickCount - 20000));
    }

    private static void Admission()
    {
        DailyRefineModel model = Reset(); Global.Prices.Clear(); Blocked(model, "refine_quote_missing");
        model = Reset(); Quote.level++; Blocked(model, "refine_quote_missing");
        model = Reset(); Quote.times++; Blocked(model, "refine_quote_missing");
        model = Reset(); Global.Prices[2u] = new Database._practice_money_exchange { money = 41 };
        Blocked(model, "refine_ambiguous_quote");
        model = Reset(); Quote.money = 0; Blocked(model, "refine_quote_cost");
        model = Reset(); Quote.money = (uint)Int32.MaxValue + 1; Blocked(model, "refine_quote_cost");
        model = Reset(); Role.money64 = 14; Blocked(model, "refine_insufficient_coins");
        model = Reset(); Role.Unlocked = false; Blocked(model, "refine_coin_feature_locked");
        model = Reset(); Role.practice_money_exchange_times = 20; Blocked(model, "refine_coin_limit");
        model = Reset(); Client.Put(860, delegate { }); Blocked(model, "refine_network_busy");
        model = Reset(); Client.Put(861, delegate { }); Blocked(model, "refine_network_busy");
        model = Reset(); Client.Put(991, delegate { }); Blocked(model, "refine_network_busy");
        model = Reset(); Role.acts[21u] = new Database.Activity { condition_param = 1 }; Blocked(model, "refine_activity_panel_stale");
        model = Reset(); Panel.Show(); MainUI.m_subui = Panel; __BaseUI.subui = "star_refine";
        Blocked(model, "refine_clean_stock_entry_required");

        model = Reset(); Role.money64 = 15; model.Capture(Context()); Open(model); Act(model, "refine", 2);
        Advance(); Client.Deliver(991, Ok, true); Panel.DrainFlyEffects(); model.Capture(Context());
        Check(model.OperationJson().Contains("SETTLED") && Role.money64 == 0, "exact balance including inherited money");
    }

    private static void Blocked(DailyRefineModel model, string expected)
    {
        model.Capture(Context()); string why;
        Check(!Queue(model, "open", 1, out why) && why == expected && Panel.Requests == 0 && MainUI.Opens == 0,
            "blocked " + expected + " actual " + why);
    }

    private static void Publication()
    {
        CheckPreSend(delegate { Quote.money++; }, "quote changes");
        CheckPreSend(delegate { Global.character_ = new Database.Character(); }, "role changes");
        CheckPreSend(delegate { Client.m_stream = new MemoryStream(); }, "transport changes");
        CheckPreSend(CrossFive, "cycle changes");
        CheckPreSend(delegate { Panel.visible_time = Panel.visible_time.AddTicks(1); }, "panel generation changes");
        CheckPreSend(delegate { Panel.m_refinetype = ArmyStarRefineUI.RefineType.jingyan; }, "tab changes");
        DailyRefineModel model = Reset(); Open(model); string why;
        Check(Queue(model, "refine", 2, out why) &&
            !model.ExecuteGui(Context, delegate { }, delegate(DailyRefineCommitCheck prepare, DailyRefineCommitCheck check)
                { model.Stop(); return false; }, out why) &&
            !model.MutationAttempted && Panel.Requests == 0 && Client.HandlerFor(355).handler.Method.Name == "handelActvity",
            "stop at lease restores original observer");
    }

    private static void CheckPreSend(DevelopmentPublication change, string name)
    {
        DailyRefineModel model = Reset(); Open(model); string why;
        Check(Queue(model, "refine", 2, out why) &&
            !model.ExecuteGui(Context, change, PermitCommit, out why) &&
            !model.MutationAttempted && Panel.Requests == 0 && MainUI.Closes == 0, name);
    }

    private static void CrossFive()
    {
        TimeUtil._thisDay += 86400; TimeUtil._now += 86400; Role.next_resource_time += 86400;
    }

    private static void CommitBoundary()
    {
        foreach (string action in new[] { "refine", "close" })
        {
            FinalBoundary(action, Expire, "deadline crossed inside begin");
            FinalBoundary(action, delegate { UnityEngine.GUI.enabled = false; }, "GUI disabled inside begin");
            __BaseUI foreign = new __BaseUI { visible = true };
            FinalBoundary(action, delegate { MainUI.m_subui = foreign; }, "foreign panel selected inside begin");
            Check(ReferenceEquals(MainUI.m_subui, foreign) && foreign.visible, "foreign panel preserved");
            FinalBoundary(action, delegate { Client.m_bConnectLost = true; }, "connection lost inside begin");
            FinalBoundary(action, delegate { Client.m_stream = new MemoryStream(); }, "connection replaced inside begin");
            FinalBoundary(action, delegate { CrossFive(); }, "cycle changed inside begin");
            FinalBoundary(action, delegate { Global.character_ = new Database.Character(); }, "same-ID role replaced inside begin");
        }
        FinalBoundary("refine", delegate { Quote.money++; }, "quote changes inside begin");
        FinalBoundary("refine", delegate { Advance(); }, "355 wrapper called before stock send");
        FinalBoundary("close", delegate { Listing[21u] = new Database.Activity(); }, "listed cache changes inside begin");
        FinalBoundary("close", delegate { MainUI.m_subui = new __BaseUI { visible = true }; }, "selection changes after commit", true);
        FinalBoundary("refine", delegate { UnityEngine.GUI.enabled = false; }, "GUI changes after commit", true);
        FinalBoundary("refine", delegate { CrossFive(); }, "cycle changes after prepare", true);
        FinalBoundary("refine", delegate { Quote.money++; }, "quoted debit changes after prepare", true);
        FinalBoundary("refine", delegate { Quote.times++; }, "quoted ordinal changes after prepare", true);
        FinalBoundary("refine", delegate { Global.Prices = new Hashtable(Global.Prices); }, "quote table replaced after prepare", true);
        FinalBoundary("refine", delegate { Global.Prices[2u] = new Database._practice_money_exchange(); }, "quote count changes after prepare", true);
        FinalBoundary("refine", delegate { Role.practice_money_exchange_times++; }, "role ordinal changes after prepare", true);
        FinalBoundary("refine", delegate { Role.money64 = 0; Role.money = 0; }, "wallet changes after prepare", true);
        FinalBoundary("refine", delegate { Role.acts[21u] = new Database.Activity(); }, "live task row replaced after prepare", true);
        FinalBoundary("refine", delegate { Global.Task.cond_param++; }, "task template changes after prepare", true);
        FinalBoundary("close", delegate { Listing[21u] = new Database.Activity(); }, "listed cache changes after prepare", true);
        FinalBoundary("close", delegate { ((Database.Activity)Listing[21u]).refresh_time++; }, "same listed row changes after prepare", true);
        FinalBoundary("refine", delegate { Client.Deliver(355, new byte[0], false); }, "355 wrapper dispatched after prepare", true);
        FinalBoundary("refine", delegate { RootBase.Install(); }, "355 handler replaced after prepare", true);
        FinalBoundary("refine", delegate { Client.HandlerFor(355).life = 1f; }, "355 handler lifetime changes after prepare", true);
        FinalBoundary("refine", delegate { Panel.m_refinetype = ArmyStarRefineUI.RefineType.jingyan; }, "tab changes after prepare", true);
        FinalBoundary("close", delegate { Panel.OnClose = delegate { }; }, "stock close callback changes after prepare", true);

        DailyRefineModel model = Reset(); Open(model); string why;
        Check(Queue(model, "refine", 2, out why) &&
            !model.ExecuteGui(Context, delegate { }, delegate(DailyRefineCommitCheck prepare, DailyRefineCommitCheck check)
                { return prepare(); }, out why) &&
            !model.MutationAttempted && model.UiOwnershipHeld && Client.HandlerFor(355).handler.Method.Name == "handelActvity",
            "a controller that skips commit cannot send");
    }

    private static void FinalBoundary(string action, Action<DailyRefineModel> change, string name)
    { FinalBoundary(action, change, name, false); }

    private static void FinalBoundary(string action, Action<DailyRefineModel> change, string name, bool afterCommit)
    {
        bool refine = action == "refine";
        DailyRefineModel model = refine ? Reset() : Settled();
        if (refine) Open(model);
        string why;
        Check(Queue(model, action, refine ? 2 : 3, out why), "queue " + name);
        bool enteredBegin = false;
        Check(!model.ExecuteGui(Context, delegate { }, delegate(DailyRefineCommitCheck prepare, DailyRefineCommitCheck check)
        {
            enteredBegin = true;
            if (afterCommit)
            {
                Check(prepare(), "fresh commit before " + name);
                change(model);
                return check();
            }
            change(model);
            return prepare() && check();
        }, out why), "final commit rejection " + name);
        Check(enteredBegin && !model.GuiInvocationAttempted && model.UiOwnershipHeld && model.Busy && model.MutationAttempted == !refine &&
            Panel.Requests == (refine ? 0 : 1) && MainUI.Closes == 0 &&
            Client.HandlerFor(355).handler.Method.Name == "handelActvity", "no new stock action and 355 detached " + name);
    }

    private static void Settlement()
    {
        DailyRefineModel model = Sent(); string why;
        Advance(); model.Capture(Context());
        Check(model.Unresolved && !model.OutcomeUnknown && Role.practice == 90, "push before 991 waits");
        Client.Deliver(991, Ok, true); model.Capture(Context());
        Check(model.ViewJson().Contains("refine_waiting_practice_credit") && !Queue(model, "close", 3, out why) &&
            Role.practice_money_exchange_times == 1 && Role.money64 == 4999999985L && Role.practice == 90, "delay after callback");
        Panel.DrainFlyEffects(); model.Capture(Context()); model.Capture(Context());
        Check(model.OperationJson().Contains("SETTLED") && model.OperationJson().Contains("\"practice_credit\":13") &&
            model.OperationJson().Contains("\"coins_spent\":40") && RootBase.Pushes == 1 && Panel.Replies == 1 &&
            Role.practice == 103 && Role.gold == 100 && Role.bind_gold == 5 && Role.experience == 22, "exact final readback");
        Act(model, "close", 3);
        Check(!model.Busy && MainUI.Closes == 1 && model.OperationJson().Contains("\"panel_closed\":true") &&
            !Queue(model, "open", 4, out why), "owned cleanup, replay refused");

        model = Sent(); Client.Deliver(991, Ok, true); Panel.DrainFlyEffects(); model.Capture(Context());
        Check(model.ViewJson().Contains("refine_waiting_activity_progress"), "991 before push waits");
        Advance(); Role.acts[21u] = new Database.Activity { condition_param = 1 }; model.Capture(Context());
        Check(model.OperationJson().Contains("SETTLED"), "matching 106 replacement after both 355 caches");

        model = Sent(); Client.Deliver(991, Refused, true); model.Capture(Context());
        Check(model.OperationJson().Contains("refine_server_rejected") && !model.Unresolved &&
            Role.money64 == 5000000000L && Panel.Replies == 1 && MainUI.Closes == 0, "definite refusal");
    }

    private static void Conflicts()
    {
        DailyRefineModel model = Sent(); Advance(); Client.Deliver(991, Ok, true);
        Expire(model); Unknown(model, "effect not drained before deadline");
        Panel.DrainFlyEffects(); Check(Role.practice == 103, "late stock effect preserved");
        model = Sent(); Expire(model); Unknown(model, "no 991 response");
        Client.Deliver(991, Ok, true); Check(Panel.Replies == 1, "late original callback retained");
        model = Sent(); Advance(); Client.Deliver(991, Ok, true); Panel.DrainFlyEffects(); Expire(model);
        Unknown(model, "late observation does not authorize cleanup");
        model = Sent(); Client.Deliver(991, Ok, true); Panel.DrainFlyEffects();
        Role.acts[21u] = Listing[21u] = new Database.Activity { condition_param = 1 };
        Expire(model); Unknown(model, "cache update without 355");
        model = Sent(); RootBase.ListedToo = false; Advance(); Client.Deliver(991, Ok, true); Panel.DrainFlyEffects();
        Expire(model); Unknown(model, "missing listed progress");
        model = Sent(); Client.Deliver(991, Ok, true); Panel.DrainFlyEffects(); Advance(1, 304401);
        Role.acts[21u] = new Database.Activity { condition_param = 1 };
        Expire(model); Unknown(model, "refresh mismatch");
        model = Sent(); Client.Deliver(991, Ok, true); Role.practice += 13; Unknown(model, "credit while effect remains");
        model = Sent(); byte[] repriced = (byte[])Ok.Clone(); repriced[3]++; Client.Deliver(991, repriced, true);
        Unknown(model, "991 receipt cost differs from quote"); Check(Panel.Replies == 1, "repriced original forwarded");
        model = Sent(); Role.gold--; Client.Deliver(991, Ok, true); Unknown(model, "premium debit");
        model = Sent(); Role.bind_gold--; Client.Deliver(991, Ok, true); Unknown(model, "bound premium debit");
        model = Sent(); Role.experience--; Client.Deliver(991, Ok, true); Unknown(model, "xp debit");
        model = Sent(); Role.practice_exchange_gold++; Unknown(model, "premium exchange count");
        model = Sent(); Client.Put(860, delegate { }); Unknown(model, "concurrent 860");
        model = Sent(); Client.Put(861, delegate { }); Unknown(model, "concurrent 861");
        model = Sent(); Client.Put(991, delegate { }); Unknown(model, "foreign 991");
        model = Sent(); Client.messages.Add(new game.net.tcp_base.Message { op = 861 }); Unknown(model, "queued foreign 861");
        model = Sent(); Client.messages.Add(new game.net.tcp_base.Message { op = 991 });
        Client.Deliver(991, Ok, true); Unknown(model, "duplicate 991 waiting in receive queue");
        model = Sent(); Client.Deliver(991, Ok, false); Unknown(model, "991 callback without dequeue");
        model = Sent(); Client.Deliver(991, null, true); Unknown(model, "stock timeout callback");
        model = Sent(); Client.m_client = new game.net.tcp_client.Socket(); Client.Deliver(991, Ok, true);
        Unknown(model, "socket replaced before callback");
        model = Sent(); CrossFive(); Client.Deliver(991, Ok, true); Unknown(model, "cycle replaced before callback");
        model = Sent(); Global.character_ = new Database.Character(); Client.Deliver(991, Ok, true);
        Unknown(model, "same id new object");
        model = Sent(); model.Stop(); Advance(); Client.Deliver(991, Ok, true);
        Unknown(model, "stop during in-flight request"); Check(Panel.Replies == 1 && RootBase.Pushes == 1, "stock callbacks survive stop");
        model = Sent(); Panel.InOriginal = model.Stop; Advance(); Client.Deliver(991, Ok, true);
        Unknown(model, "stop during original"); Check(Panel.Replies == 1, "original finishes after reentrant stop");
        model = Sent(); RootBase.InActivity = delegate { throw new IOException("fixture_355"); };
        bool threw = false;
        try { Advance(); } catch (IOException) { threw = true; }
        Check(threw, "355 preserves original exception"); Unknown(model, "355 must return");
        model = Reset(); Open(model); Panel.ThrowOnSend = true; string why;
        Check(Queue(model, "refine", 2, out why) &&
            !model.ExecuteGui(Context, delegate { }, PermitCommit, out why), "stock send exception");
        Unknown(model, "stock send uncertain");
    }

    private static DailyRefineModel Settled()
    {
        DailyRefineModel model = Sent();
        Client.Deliver(991, Ok, true); Advance(); Panel.DrainFlyEffects();
        model.Capture(Context()); model.Capture(Context());
        Check(model.OperationJson().Contains("SETTLED") && model.UiOwnershipHeld, "held through settled");
        return model;
    }

    private static void Ownership()
    {
        DailyRefineModel model = Reset(); string why;
        Act(model, "open", 1); model.Stop();
        Check(model.Busy && model.UiOwnershipHeld && !model.MutationAttempted && MainUI.Closes == 0, "stop during opening");

        model = Reset(); Open(model); model.Stop();
        Check(model.Busy && model.UiOwnershipHeld && !model.MutationAttempted && MainUI.Closes == 0, "stop before 991");

        model = Reset(); MainUI.ThrowAfterOpen = true;
        Check(Queue(model, "open", 1, out why) && !model.ExecuteGui(Context, delegate { }, PermitCommit, out why),
            "open can fail after selection");
        model.Capture(Context());
        Check(model.Busy && model.UiOwnershipHeld && !model.MutationAttempted && MainUI.Closes == 0, "open exception retains hold");

        model = Reset(); Act(model, "open", 1); Expire(model); model.Capture(Context());
        Check(model.Busy && model.UiOwnershipHeld && !model.MutationAttempted && MainUI.Closes == 0, "loading timeout retains hold");
        Panel.loading = false; MainUI.m_subui = null;
        model.Capture(Context());
        Check(!model.Busy && !model.UiOwnershipHeld, "fresh same-owner hidden and deselected releases hold");

        model = Reset(); Open(model); Check(Queue(model, "refine", 2, out why), "queue before overlay");
        Check(!model.ExecuteGui(Context, delegate { controllerGate = "foreign_overlay"; }, PermitCommit, out why) &&
            model.Busy && model.UiOwnershipHeld && !model.MutationAttempted && MainUI.Closes == 0, "overlay retains hold");

        model = Reset(); Open(model); Check(Queue(model, "refine", 2, out why), "queue before send deadline");
        Expire(model);
        Check(!model.ExecuteGui(Context, delegate { }, PermitCommit, out why) &&
            model.Busy && model.UiOwnershipHeld && !model.MutationAttempted && MainUI.Closes == 0, "send deadline retains hold");

        model = Settled(); Listing[21u] = new Database.Activity { condition_param = 1, refresh_time = 304401 };
        model.Capture(Context());
        Check(model.Busy && model.UiOwnershipHeld && !Queue(model, "close", 3, out why) &&
            MainUI.Closes == 0 && model.OperationJson().Contains("\"account_progress_observed\":true"), "late cache divergence blocks cleanup");

        model = Settled(); Check(Queue(model, "close", 3, out why), "queue before cache divergence");
        Check(!model.ExecuteGui(Context, delegate { Listing[21u] = new Database.Activity(); }, PermitCommit, out why) &&
            model.Busy && model.UiOwnershipHeld && MainUI.Closes == 0, "final close sample requires both caches");

        model = Settled(); MainUI.CloseKeepsSelection = true; Act(model, "close", 3);
        Check(model.Busy && model.UiOwnershipHeld && model.OperationJson().Contains("refine_close_unconfirmed"), "hidden but selected retains hold");
        model.Stop(); Check(model.Busy, "stop after unconfirmed close");

        model = Settled(); MainUI.CloseKeepsVisible = true; Act(model, "close", 3);
        Check(model.Busy && model.UiOwnershipHeld, "deselected but visible retains hold");
        Panel.visible = false; Client.m_stream = new MemoryStream(); model.Capture(Context());
        Check(model.Busy && model.UiOwnershipHeld, "foreign transport cannot release hold");

        model = Settled(); MainUI.CloseThrows = true;
        Check(Queue(model, "close", 3, out why) && !model.ExecuteGui(Context, delegate { }, PermitCommit, out why) &&
            model.Busy && model.UiOwnershipHeld && MainUI.Closes == 1, "stock close exception retains hold");
    }

    private static void Wire()
    {
        DailyRefineReceipt decoded = DailyRefineReceipt.Decode(Ok);
        Check(decoded.Result == 0 && decoded.Cost == 40 && decoded.Practice == 13, "required tags and ZigZag practice");
        byte[] negativePractice = (byte[])Ok.Clone(); negativePractice[11] = 1;
        foreach (byte[] malformed in new[] { null, new byte[] { 8, 0, 16, 40 }, negativePractice,
            new byte[] { 8, 0, 16, 40, 26, 8, 8, 2, 16, 0, 24, 26, 32 }, new byte[] { 8, 0, 8, 0 } })
        {
            bool rejected = false;
            try { DailyRefineReceipt.Decode(malformed); } catch (FormatException) { rejected = true; }
            Check(rejected, "malformed or invalid 991 receipt");
        }
    }

    public static int Main()
    {
        try
        {
            Wire(); Admission(); Publication(); CommitBoundary(); Settlement(); Conflicts(); Ownership();
            Console.WriteLine("{\"daily_refine\":\"passed\"}");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
