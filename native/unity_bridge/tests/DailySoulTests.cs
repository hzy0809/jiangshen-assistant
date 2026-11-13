using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Threading;
using JiangShen.AssistantRuntime;
using D = JiangShen.AssistantRuntime.SharedDevelopmentContext;

#if !JSB_SOUL_PROBE_TEST
namespace UnityEngine
{
    public class Object { public int GetInstanceID() { return 1; } }
    public class Transform { public Transform root { get { return this; } } }
    public class GameObject { public bool activeInHierarchy = true; public Transform transform = new Transform(); }
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
        internal static Type GameType(string name) { return FirstType(name); }
        internal static Type FirstType(string name) { return Type.GetType(name, true); }
    }
}
#endif

public class __BaseUI : UnityEngine.Behaviour
{
    public delegate void delegate_OnClose();
    public static string subui = "";
    public UnityEngine.Component rmg;
    public DateTime visible_time;
    public delegate_OnClose OnClose;
    public bool loading, collecting, visible, interactive = true;
    public bool IsVisible() { return visible; }
    public bool IsEnable() { return interactive; }
}
public class BaseUI : __BaseUI { public static RootUI rootUI; }
public class RootUI : BaseUI
{
    public static game.net.tcp_client m_client;
    public static bool m_bRefreshFive;
    public string LevelID = "Capital";
}
public static class BaseAsset
{
    public sealed class LoadScreen { public UnityEngine.Component currmg; }
    public static LoadScreen lds = new LoadScreen();
}
public static class PopupUI { public static bool popup; }
public static class TimeUtil { public static uint _now = 220000, _thisDay = 200000; }
public class MainUI : BaseUI
{
    public static MainUI Current = new MainUI();
    public bool first, m_bInit = true;
    public static bool CloseNoOp;
    public static __BaseUI m_subui;
    public static GeneralSoulGatherUI Soul;
    public static Action OnOpenReturn, OnCloseReturn;
    public static int Opens, Closes;
    private static int times;
    public static void _resource_soul()
    {
        if (m_subui == Soul)
        {
            SetSubUI(null);
            return;
        }
        Opens++;
        subui = "resource_soul";
        m_subui = Soul;
        Soul.visible = true;
        Soul.visible_time = new DateTime(++times);
        Soul.gathered = false;
        Soul.gather_bg = new UnityEngine.GameObject();
        Soul.start_modes[0] = Global.Start;
        if (OnOpenReturn != null) OnOpenReturn();
    }

    public static void SetSubUI(__BaseUI panel)
    {
        if (panel == null && m_subui == Soul)
        {
            Closes++;
            if (CloseNoOp) return;
            Soul.gather_bg = null;
            Soul.visible = false;
        }
        m_subui = panel;
        if (panel == null && OnCloseReturn != null) OnCloseReturn();
    }
}
public static class BuildingBuildUI { public static bool m_bBuildVisible; }
public static class BuildingBehaviourScript { public static bool m_bEditVisible; }
public class GeneralSoulGatherUI : BaseUI
{
    public string page = "gather", subwindow = "";
    public bool gathered, show_exchange_window;
    public UnityEngine.GameObject gather_bg;
    public readonly Database._general_soul_start_mode[] start_modes = new Database._general_soul_start_mode[9];
    public int currentGatherSoulStatus;
    public uint m_curType;
    public bool LastFree;
    public int StartCalls, HarvestCalls, TeachingCalls;
    public bool ThrowOnStart;
    public Action DuringHarvestTimeout, OnStartReturn, OnHarvestReturn;

    private void RequestGather(Database._general_soul_start_mode start, bool free)
    {
        LastFree = free;
        interactive = false;
        Global.m_client.Put(517, sc_refresh_gather_soul, (uint)start.type, 10);
        if (OnStartReturn != null) OnStartReturn();
    }

    private void sc_refresh_gather_soul(MemoryStream stream, object arg)
    {
        StartCalls++;
        if (ThrowOnStart) throw new InvalidOperationException("original_start_exception");
        if (stream != null)
        {
            DailySoulTests.StockRead(stream);
            m_curType = (uint)arg;
            Global.character_.gather_souls.soul0_color = 1;
            Global.character_.gather_souls.soul0_level = 1;
            Global.character_.money = 0;
            Global.character_.money64 = Global.CoinsReply;
            Global.character_.gold = Global.GoldReply;
            Global.character_.generalsoul_blue_times = Global.UsedReply;
            currentGatherSoulStatus = 1;
        }
        interactive = true;
    }

    private void Harvest()
    {
        interactive = false;
        Global.m_client.Put(518, sc_harvest_soul, null, 10);
        currentGatherSoulStatus = 2;
        if (OnHarvestReturn != null) OnHarvestReturn();
    }

    private void sc_harvest_soul(MemoryStream stream, object arg)
    {
        HarvestCalls++;
        if (stream == null)
        {
            if (DuringHarvestTimeout != null) DuringHarvestTimeout();
            return;
        }
        DailySoulTests.StockRead(stream);
        Global.character_.gather_souls.soul0_color = 0;
        Global.character_.gather_souls.soul0_level = 0;
        Global.character_.generalsoul_blue += Global.AwardReply;
        m_curType = 0;
    }

    public void FinishGather()
    {
        currentGatherSoulStatus = 0;
        interactive = true;
        foreach (Database.NewbieQuest q in Global.character_.newbiequests)
            if (q.isfinish != 1) TeachingCalls++;
    }
}
public static class Global
{
    public static Database.Character character_;
    public static game.net.tcp_client m_client;
    public static bool extern_enabled = true, relogin;
    public static Database._general_soul_start_mode Start;
    public static Database.ActivityTemplate Activity;
    public static Database.NewbieTemplate Tutorial;
    public static long CoinsReply;
    public static int GoldReply, AwardReply, ProgressReply, RefreshReply;
    public static byte UsedReply;
    public static object getTemplate(string name, uint id)
    {
        if (name == "general_soul_start_mode" && id == 1) return Start;
        if (name == "activity" && id == 5) return Activity;
        return name == "newbie_quest" && id == 9 ? Tutorial : null;
    }
}
public static class RootBase
{
    public static void Bind() { Global.m_client.Put(355, handelActvity, null, -1); }
    private static void handelActvity(MemoryStream stream, object arg)
    {
        if (stream == null) return;
        DailySoulTests.StockRead(stream);
        if (Global.ProgressReply < 0) return;
        // The stock 355 handler replaces the row and shares the new instance
        // between the latest cache and the displayed category's dictionary.
        Database.Activity row = new Database.Activity { idactivity = 5, condition_param = (byte)Global.ProgressReply,
            refresh_time = Global.RefreshReply, temp = Global.Activity };
        Global.character_.acts[5u] = row;
        ((Database.Activity_Reward)Global.character_.act_rewards[2u]).acts[5u] = row;
    }
}

namespace Database
{
    public sealed class Character
    {
        public ulong id = 42;
        public uint SID = 1, next_resource_time = 304400, gold_consume;
        public int gold = 13, bind_gold = 7, money = 8000;
        public long money64 = 5000000000;
        public bool gm, Unlocked = true;
        public Action OnFeatureRead;
        public readonly Hashtable acts = new Hashtable(), act_rewards = new Hashtable();
        public readonly ArrayList newbiequests = new ArrayList();
        public GatherSoul gather_souls = new GatherSoul();
        public Exchange exchange = new Exchange();
        public byte generalsoul_blue_times, generalsoul_purple_times, generalsoul_orange_times;
        public int generalsoul_blue = 10, generalsoul_purple, generalsoul_orange, generalsoul_red;
        public bool checkUI(string name)
        {
            if (OnFeatureRead != null) OnFeatureRead();
            return Unlocked && name == "resource_soul";
        }
    }
    public sealed class GatherSoul
    {
        public byte soul0_color, soul0_level, soul1_color, soul1_level, soul2_color, soul2_level, soul3_color, soul3_level;
        public byte soul4_color, soul4_level, soul5_color, soul5_level, soul6_color, soul6_level, soul7_color, soul7_level;
    }
    public sealed class Exchange { public uint today_times = 99000; }
    public sealed class Activity
    {
        public byte idactivity, condition_param;
        public int refresh_time;
        public ActivityTemplate temp;
    }
    public sealed class Activity_Reward
    {
        public byte idact_reward = 2;
        public Hashtable acts = new Hashtable();
    }
    public sealed class ActivityTemplate
    {
        public byte id = 5, idreward = 2;
        public int cond_param = 1;
        public bool active = true;
    }
    public sealed class _general_soul_start_mode
    {
        public byte type = 1;
        public uint gather_rate = 1, price = 10000;
        public int free_times = 2;
    }
    public sealed class NewbieQuest { public ushort idnewbie_quest = 9; public sbyte isfinish = 1; }
    public sealed class NewbieTemplate { public short idnewbie_quest = 9; public uint newbie_quest_type = 5; }
}

namespace game.net
{
    public class tcp_base
    {
        public delegate void Callback(MemoryStream stream, object arg);
        public sealed class Handler { public Callback handler; public object arg; public float life; }
        public sealed class Message { public ushort op; public MemoryStream stream; }
        public static int connection_state = 3;
        public bool m_bConnectLost;
        public readonly Hashtable m_handlers = new Hashtable();
        public readonly ArrayList messages = new ArrayList();
        public readonly ArrayList Sent = new ArrayList();
        public void Put(ushort op, Callback handler, object arg, float life)
        { m_handlers[op] = new Handler { handler = handler, arg = arg, life = life }; if (life > 0) Sent.Add((int)op); }
        public void Queue(ushort op, byte[] bytes)
        { messages.Add(new Message { op = op, stream = bytes == null ? null : new MemoryStream(bytes, false) }); }
        public void Receive(ushort op, byte[] bytes) { Queue(op, bytes); RunOne(); }
        public void Timeout(ushort opcode, bool remove = true)
        {
            lock (this)
            {
                Handler handler = (Handler)m_handlers[opcode];
                handler.life = 0;
                handler.handler(null, handler.arg);
                if (remove) m_handlers.Remove(opcode);
            }
        }
        public void RunOne()
        {
            lock (this)
            {
                Message message = (Message)messages[0];
                messages.RemoveAt(0);
                Handler handler = (Handler)m_handlers[message.op];
                if (handler == null) return;
                if (handler.life > 0) m_handlers.Remove(message.op);
                using (message.stream) handler.handler(message.stream, handler.arg);
            }
        }
    }
    public sealed class tcp_client : tcp_base
    {
        public sealed class Socket
        {
            public Action OnConnected;
            private bool connected;
            public bool Connected { get { if (OnConnected != null) OnConnected(); return connected; } set { connected = value; } }
        }
        public Socket m_client = new Socket { Connected = true };
        public Stream m_stream = new MemoryStream();
    }
}

internal static class DailySoulTests
{
    private static int checks;
    private const string Op = "00000000000000000000000000000055";
    private static int stockReads;

    internal static void StockRead(MemoryStream stream)
    {
        Check(stream.Position == 0, "original stream begins unchanged");
        Check(stream.ReadByte() == 10, "original callback receives the full packet");
        stockReads++;
    }

    private static void Check(bool ok, string why)
    { if (!ok) throw new Exception(why); checks++; }

    internal sealed class Fixture
    {
        internal readonly RootUI Root = new RootUI(), GlobalUi = new RootUI();
        internal readonly Database.Character Role = new Database.Character();
        internal readonly GeneralSoulGatherUI Soul = new GeneralSoulGatherUI();
        internal readonly game.net.tcp_client Client = new game.net.tcp_client();
        internal readonly DailySoulModel Model;
        internal readonly game.net.tcp_base.Callback Push;
        internal readonly Database.Activity_Reward Reward = new Database.Activity_Reward();
        internal int Clock, Seq;
        internal uint Cycle = 218000;
        internal string RefreshGate = "", Overlay = "PASS", Operation = Op;
        internal bool Live, MainReady = true;

        internal Fixture(bool production = false)
        {
            BaseUI.rootUI = GlobalUi; GlobalUi.rmg = GlobalUi; Soul.rmg = GlobalUi; Soul.gameObject = GlobalUi.gameObject;
            MainUI.Soul = Soul; MainUI.m_subui = null;
            MainUI.subui = ""; MainUI.Opens = MainUI.Closes = 0; MainUI.OnOpenReturn = MainUI.OnCloseReturn = null; MainUI.CloseNoOp = false;
            TimeUtil._now = 220000; TimeUtil._thisDay = 200000;
            BaseAsset.lds.currmg = Root;
            RootUI.m_client = Global.m_client = Client;
            Global.character_ = Role; RootUI.m_bRefreshFive = false;
            Global.Start = new Database._general_soul_start_mode();
            Global.Activity = new Database.ActivityTemplate();
            Global.Tutorial = new Database.NewbieTemplate();
            Role.newbiequests.Add(new Database.NewbieQuest());
            Database.Activity row = new Database.Activity { idactivity = 5, temp = Global.Activity };
            Role.acts[5u] = row; Reward.acts[5u] = row; Role.act_rewards[2u] = Reward;
            Global.CoinsReply = D.Money(Role); Global.GoldReply = Role.gold; Global.UsedReply = 1; Global.AwardReply = 3;
            Global.ProgressReply = 1; Global.RefreshReply = 219997; PopupUI.popup = false;
            RootBase.Bind(); Push = Handler(355).handler;
            Model = production ? new DailySoulModel(Install, delegate { return MainReady; }) :
                new DailySoulModel(Install, delegate { return MainReady; }, Gate, delegate { return Clock; });
            Live = true;
        }

        private string Gate(object role, out uint now, out uint day, out uint cycle)
        {
            now = TimeUtil._now; day = TimeUtil._thisDay; cycle = Cycle;
            return object.ReferenceEquals(role, Role) ? RefreshGate : "wrong_fixture_role";
        }

        internal game.net.tcp_base.Handler Handler(int opcode)
        { return (game.net.tcp_base.Handler)Client.m_handlers[(ushort)opcode]; }

        internal D Context()
        {
            return Live ? new D(new UnityEngine.Object[] { Root, GlobalUi, Soul }, Root, GlobalUi, Role, "42", Overlay, false) : null;
        }

        internal void Capture() { Model.Capture(Context()); }
        internal void Ready(string action)
        {
            Capture();
            Check(Model.Action == action, "expected " + action + " " + Model.Json());
        }

        internal void Apply(string action)
        {
            Queue(action);
            string why;
            Check(Model.ExecuteGui(Context, delegate { }, Allow, out why), "execute " + action + " " + why);
        }

        internal void Queue(string action)
        {
            Ready(action);
            string why;
            Check(Model.Queue(action, 42, Operation, ++Seq, Model.Revision, out why), "queue " + action + " " + why);
        }

        internal void Start() { Apply("open"); Apply("start"); }
        internal void ReplyStart() { Client.Receive(517, StartWire()); }
        internal void ReplyHarvest() { Client.Receive(518, HarvestWire()); }
        internal void Progress() { Client.Receive(355, ProgressWire(5, Global.ProgressReply, Global.RefreshReply)); }
        internal void Expire() { Clock += 18001; Capture(); }
        internal void Cleanup() { Model.TryUiCleanup(Context); }
        internal void Unknown()
        {
            Capture();
            Check(Model.Action == "" && Model.Unresolved && Model.Json().Contains("\"status\":\"UNKNOWN\""), "unknown terminal " + Model.Json());
            string why;
            Check(!Model.Queue("open", 42, Op, ++Seq, Model.Revision, out why), "unknown cannot replay");
        }
    }

    private static bool Allow(DailySoulInvocationCheck prepare, DailySoulInvocationCheck check)
    { return prepare() && check(); }

    private static DevelopmentCallbackCapture Install(object client, object handler, int opcode, string id, int sequence,
        Delegate original, Action<byte[]> observe, out string why)
    { return DevelopmentCallbackCapture.InstallDailySoul(client, handler, opcode, id, sequence, original, observe, out why); }

    private static byte[] StartWire(int mode = 1, int result = 1)
    {
        byte[] board = Pair(1, 1);
        byte[] exchange = Join(Scalar(1, 0), Scalar(2, Zig(Global.GoldReply)), Scalar(3, Global.character_.exchange.today_times),
            Scalar(4, Zig(Global.CoinsReply)));
        return Join(Child(1, board), Child(2, exchange), Scalar(3, (ulong)mode), Scalar(4, Global.UsedReply), Scalar(5, (ulong)result));
    }

    private static byte[] HarvestWire()
    { return Child(1, Pair(1, (byte)Global.AwardReply)); }

    private static byte[] Pair(byte color, byte value)
    { return Join(Child(1, new byte[] { color, 0, 0, 0, 0, 0, 0, 0 }), Child(2, new byte[] { value, 0, 0, 0, 0, 0, 0, 0 })); }

    private static byte[] ProgressWire(int id, int current, int refresh)
    { return Child(1, Join(Scalar(1, (ulong)id), Scalar(2, (ulong)current), Scalar(3, Zig(refresh)))); }

    private static ulong Zig(long value) { return (ulong)((value << 1) ^ (value >> 63)); }
    private static byte[] Scalar(int field, ulong value) { return Join(Varint((ulong)field * 8), Varint(value)); }
    private static byte[] Child(int field, byte[] body)
    { return Join(Varint((ulong)field * 8 + 2), Varint((ulong)body.Length), body); }
    private static byte[] Varint(ulong value)
    {
        using (MemoryStream stream = new MemoryStream())
        {
            while (value > 127) { stream.WriteByte((byte)(value | 128)); value >>= 7; }
            stream.WriteByte((byte)value);
            return stream.ToArray();
        }
    }
    private static byte[] Join(params byte[][] blocks)
    {
        using (MemoryStream stream = new MemoryStream())
        {
            foreach (byte[] block in blocks) stream.Write(block, 0, block.Length);
            return stream.ToArray();
        }
    }

    private static void GoldenWire()
    {
        // Serialized independently by the pinned protobuf-net and pbdata assemblies.
        DailySoulWire.Receipt start = DailySoulWire.ReadReceipt(517, Hex(
            "0A-14-0A-08-01-00-00-00-00-00-00-00-12-08-01-00-00-00-00-00-00-00-12-0E-08-00-10-1A-18-B8-85-06-20-80-C5-B0-A0-25-18-01-20-01-28-01"));
        DailySoulWire.Receipt harvest = DailySoulWire.ReadReceipt(518, Hex(
            "0A-14-0A-08-01-00-00-00-00-00-00-00-12-08-03-00-00-00-00-00-00-00"));
        DailySoulWire.Progress progress = DailySoulWire.ReadProgress(Hex("0A-08-08-05-10-01-18-BA-ED-1A"));
        Check(start.Board == 17 && start.Used == 1 && start.Coins == 5000008000 && start.Gold == 13 &&
            start.Exchanges == 99000, "pinned serializer 517 fields");
        Check(harvest.Award[0] == 3 && harvest.Award[1] == 0 && harvest.Award[2] == 0 && harvest.Award[3] == 0,
            "pinned serializer 518 award");
        Check(progress.Count == 1 && progress.Refresh == 219997, "pinned serializer 355 ZigZag refresh");
    }

    private static byte[] Hex(string text)
    {
        string[] parts = text.Split('-');
        byte[] bytes = new byte[parts.Length];
        for (int i = 0; i < parts.Length; i++) bytes[i] = Convert.ToByte(parts[i], 16);
        return bytes;
    }

    private static void Completed(bool progressAtStart)
    {
        Fixture f = new Fixture();
        f.Start();
        int reads = stockReads;
        Check(f.Soul.LastFree && f.Client.Sent.Count == 1, "single 517 uses the normal free branch");
        if (progressAtStart) f.Progress();
        f.ReplyStart();
        f.Apply("harvest");
        f.ReplyHarvest();
        f.Ready("");
        if (!progressAtStart) f.Progress();
        f.Ready("");
        f.Soul.FinishGather();
        f.Apply("close");
        f.Ready("");
        Check(f.Model.Json().Contains("\"status\":\"COMPLETED\"") && !f.Model.Busy && !f.Model.Unresolved, "settled after close " + f.Model.Json());
        Check(stockReads == reads + 3 && f.Soul.StartCalls == 1 && f.Soul.HarvestCalls == 1 && f.Soul.TeachingCalls == 0,
            "the two original replies and the original server push each execute once");
        Check(f.Client.Sent.Count == 2 && (int)f.Client.Sent[0] == 517 && (int)f.Client.Sent[1] == 518 &&
            D.Money(f.Role) == 5000008000 && f.Role.gold == 13 && f.Role.bind_gold == 7 &&
            f.Role.generalsoul_blue == 13 && f.Role.generalsoul_blue_times == 1, "normal receipts and zero monetary spend");
        Check(object.ReferenceEquals(f.Push, f.Handler(355).handler), "persistent callback restored");
        Check(MainUI.Opens == 1 && MainUI.Closes == 1, "one stock open and one explicit stock close");
    }

    private static void RejectedBeforeInvoke()
    {
        Fixture f = new Fixture(true);
        f.Ready("");
        Check(f.Model.Json().Contains("daily_refresh_unverified"), "the production constructor uses refresh proof");
        f = new Fixture();
        f.Role.generalsoul_blue_times = 2; f.Ready("");
        f.Role.generalsoul_blue_times = 0;
        f.RefreshGate = "daily_refresh_pending"; f.Ready("");
        f.RefreshGate = ""; f.Role.gather_souls.soul1_color = f.Role.gather_souls.soul1_level = 1; f.Ready("");
        f.Role.gather_souls.soul1_color = f.Role.gather_souls.soul1_level = 0;
        ((Database.NewbieQuest)f.Role.newbiequests[0]).isfinish = 0; f.Ready("");
        ((Database.NewbieQuest)f.Role.newbiequests[0]).isfinish = 1;
        f.Reward.acts[5u] = new Database.Activity { temp = Global.Activity, idactivity = 5 }; f.Ready("");
        f.Reward.acts[5u] = f.Role.acts[5u];
        PopupUI.popup = true; f.Ready(""); PopupUI.popup = false;
        MainUI.m_subui = f.Soul; f.Soul.visible = true; f.Ready("");
        MainUI.m_subui = null; f.Soul.visible = false;
        f.Ready("open");
        string why;
        int stale = f.Model.Revision - 1;
        Check(!f.Model.Queue("open", 42, Op, 1, stale, out why) && !f.Model.Queue("open", 41, Op, 1, f.Model.Revision, out why),
            "stale revision and other role rejected");
        Check(f.Model.Queue("open", 42, Op, 1, f.Model.Revision, out why), "queue after stale request");
        Check(!f.Model.ExecuteGui(f.Context, delegate { f.Cycle++; }, Allow, out why) && f.Client.Sent.Count == 0 && MainUI.m_subui == null,
            "cycle changed between publication and invocation");
    }

    private static void UnknownAfterInvoke()
    {
        Fixture f = new Fixture();
        f.Start();
        Global.GoldReply--;
        f.ReplyStart(); f.Unknown();
        Check(f.Soul.StartCalls == 1 && f.Client.Sent.Count == 1, "paid reply is forwarded without harvest");

        f = new Fixture(); f.Start(); f.Client.Receive(517, null); f.Unknown();
        Check(f.Soul.StartCalls == 1, "null original is forwarded");
        f = new Fixture(); f.Start(); f.Client.Queue(517, StartWire()); f.Client.Queue(517, StartWire()); f.Client.RunOne(); f.Unknown();
        Check(f.Soul.StartCalls == 1, "queued duplicate makes success unknown");
        f = new Fixture(); f.Start(); f.ReplyStart(); f.Client.m_client = new game.net.tcp_client.Socket { Connected = true }; f.Unknown();
        f = new Fixture(); f.Start(); f.ReplyStart(); f.Ready("harvest"); f.Soul.visible_time = f.Soul.visible_time.AddTicks(1); f.Unknown();
        f = new Fixture(); f.Start(); f.ReplyStart(); f.Ready("harvest"); f.Reward.acts = (Hashtable)f.Reward.acts.Clone(); f.Unknown();
        f = new Fixture(); f.Start(); f.ReplyStart(); f.Ready("harvest"); f.Cycle++; f.Unknown();
        f = new Fixture(); f.Start(); f.ReplyStart(); f.Ready("harvest"); f.Role.bind_gold--; f.Unknown();
        f = new Fixture(); f.Start(); f.Expire(); f.Unknown();
        f = new Fixture(); f.Start(); f.Model.Stop("stop"); f.Unknown(); f.ReplyStart();
        Check(f.Soul.StartCalls == 1 && object.ReferenceEquals(f.Push, f.Handler(355).handler), "stop restores originals and does not replay");

        f = new Fixture(); f.Start(); f.ReplyStart(); f.Apply("harvest"); f.ReplyHarvest(); f.Soul.FinishGather(); f.Expire(); f.Unknown();
        Check(f.Client.Sent.Count == 2, "no progress never starts another attempt");
        f = new Fixture(); f.Start(); f.ReplyStart(); f.Apply("harvest"); f.ReplyHarvest(); f.Progress(); f.Progress(); f.Unknown();
        f = new Fixture(); f.Start(); f.ReplyStart(); f.Apply("harvest"); f.ReplyHarvest();
        ((Database.Activity)f.Role.acts[5u]).condition_param = 1; f.Unknown();
        f = new Fixture(); f.Start(); f.ReplyStart(); f.Apply("harvest"); f.ReplyHarvest(); f.Progress();
        ((Database.Activity)f.Role.acts[5u]).refresh_time++; f.Unknown();
        f = new Fixture(); f.Start(); f.Soul.ThrowOnStart = true;
        bool sameException = false;
        try { f.ReplyStart(); } catch (InvalidOperationException error) { sameException = error.Message == "original_start_exception"; }
        Check(sameException, "original exception is forwarded");
        f.Unknown();
    }

    private static void RecoverTimedOutHarvest()
    {
        Fixture f = new Fixture();
        f.Start(); f.ReplyStart(); f.Apply("harvest");
        f.Expire(); f.Unknown();
        f.Cleanup();
        Check(f.Model.NeedsUiCleanup && f.Soul.visible && f.Handler(518) != null, "never close while 518 is registered");
        f.Soul.DuringHarvestTimeout = delegate
        {
            f.Cleanup();
            Check(f.Soul.visible && f.Handler(518) != null, "never close from inside the original timeout callback");
        };
        f.Client.Queue(518, HarvestWire());
        f.Client.Timeout(518);
        f.Cleanup();
        Check(f.Soul.visible, "queued duplicate prevents recovery close");
        f.Client.RunOne();
        Thread wrong = new Thread(new ThreadStart(f.Cleanup));
        wrong.Start(); wrong.Join();
        Check(f.Soul.visible, "recovery may run only on the owning Unity thread");
        f.MainReady = false;
        f.Cleanup(); f.Cleanup();
        Check(!f.Soul.visible && MainUI.m_subui == null && f.Model.NeedsUiCleanup, "hidden alone does not verify input restoration");
        f.MainReady = true;
        f.Cleanup(); f.Unknown();
        Check(!f.Model.UiCleanupUnresolved && f.Model.Json().Contains("\"ui_cleanup\":\"clear\"") && f.Client.Sent.Count == 2,
            "confirmed cleanup retains business UNKNOWN and performs no further request");

        f = new Fixture(); f.Start(); f.ReplyStart(); f.Apply("harvest"); f.Client.Timeout(518); f.Unknown();
        f.Soul.visible_time = f.Soul.visible_time.AddTicks(1); f.Cleanup();
        Check(f.Soul.visible && f.Model.UiCleanupUnresolved && !f.Model.NeedsUiCleanup, "foreign UI opening survives recovery");
    }

    private static void RecoverOnStopAndVerifyMain()
    {
        Fixture f = new Fixture(); f.Apply("open"); f.Ready("start"); f.Model.Stop("stop");
        f.Cleanup(); f.Cleanup();
        Check(!f.Model.UiCleanupUnresolved && !f.Soul.visible && f.Client.Sent.Count == 0, "stopped owned page is cleared");

        f = new Fixture(); f.Apply("open"); f.Ready("start"); f.Model.Stop("stop");
        f.Role.OnFeatureRead = delegate
        {
            f.Role.OnFeatureRead = null;
            f.Client.m_client.OnConnected = delegate
            {
                f.Client.m_client.OnConnected = null;
                f.Clock += 8000;
            };
        };
        f.Cleanup();
        Check(f.Soul.visible && MainUI.Closes == 0 && !f.Model.NeedsUiCleanup && f.Model.UiCleanupUnresolved &&
            f.Model.Json().Contains("\"ui_cleanup\":\"unknown\""), "cleanup expiring after a fresh sample cannot close");

        f = new Fixture(); f.Start(); f.ReplyStart(); f.Apply("harvest"); f.Model.Stop("stop");
        f.ReplyHarvest(); f.Cleanup();
        Check(f.Soul.visible && f.Model.NeedsUiCleanup, "successful reply with stock harvest animation must remain visible");
        f.Soul.FinishGather(); f.Cleanup(); f.Cleanup(); f.Unknown();
        Check(!f.Model.UiCleanupUnresolved && !f.Soul.visible && f.Soul.HarvestCalls == 1, "after stop, close only after natural stock animation");

        f = new Fixture(); f.Start(); f.ReplyStart(); f.Apply("harvest"); f.ReplyHarvest(); f.Soul.FinishGather(); f.Progress();
        f.Ready("close"); f.Model.Stop("stop"); f.Cleanup(); f.Cleanup();
        Check(!f.Model.UiCleanupUnresolved && !f.Model.Unresolved && !f.Soul.visible && f.Client.Sent.Count == 2,
            "settled harvest is cleaned on stop without creating business uncertainty");

        f = new Fixture(); f.Start(); f.ReplyStart(); f.Apply("harvest"); f.ReplyHarvest(); f.Soul.FinishGather(); f.Progress();
        f.Apply("close"); f.MainReady = false; f.Capture();
        Check(f.Model.Json().Contains("\"status\":\"CLOSING\""), "normal closing requires fresh common main PASS");
        f.MainReady = true; f.Capture();
        Check(f.Model.Json().Contains("\"status\":\"COMPLETED\""), "normal close verified when main is usable");

        f = new Fixture(); f.Start(); f.ReplyStart(); f.Apply("harvest"); f.Model.Stop("stop");
        f.Model.FinishUiCleanup();
        Check(f.Soul.visible && f.Model.UiCleanupUnresolved && f.Model.Json().Contains("\"ui_cleanup\":\"unknown\""),
            "forced teardown reports UI unknown without cutting off the request");
        f.ReplyHarvest();
        Check(f.Soul.HarvestCalls == 1, "stock response remains callable after forced teardown");
    }

    private static void RecoverStoppedStart()
    {
        Fixture f = new Fixture();
        f.Start(); f.Model.Stop("stop"); f.Cleanup();
        Check(f.Soul.visible && f.Model.NeedsUiCleanup, "stop waits for the in-flight 517");
        f.Client.Timeout(517, false);
        f.Cleanup();
        Check(f.Soul.visible && f.Soul.StartCalls == 1, "517 null returned but handler is still registered");
        f.Client.m_handlers.Remove((ushort)517);
        f.Cleanup(); f.Cleanup(); f.Unknown();
        Check(!f.Soul.visible && !f.Model.UiCleanupUnresolved && f.Role.generalsoul_blue_times == 0 && f.Client.Sent.Count == 1,
            "stopped 517 timeout restores the main UI without replay");

        f = new Fixture(); f.Start(); f.Model.Stop("stop"); f.ReplyStart(); f.Cleanup(); f.Cleanup(); f.Unknown();
        Check(!f.Soul.visible && !f.Model.UiCleanupUnresolved && f.Soul.StartCalls == 1 && f.Client.Sent.Count == 1 &&
            f.Role.generalsoul_blue_times == 1 && f.Role.gather_souls.soul0_level == 1,
            "late 517 success permits close and preserves the stock unharvested board");

        f = new Fixture(); f.Start(); f.Model.Stop("stop"); f.ReplyStart();
        f.Soul.visible_time = f.Soul.visible_time.AddTicks(1); f.Cleanup();
        Check(f.Soul.visible && !f.Model.NeedsUiCleanup && f.Model.UiCleanupUnresolved && f.Client.Sent.Count == 1,
            "late 517 receipt cannot close another opening of the same component");

        f = new Fixture(); f.Start(); f.Model.Stop("stop");
        f.Clock += 8001; f.Cleanup(); f.ReplyStart(); f.Cleanup(); f.Unknown();
        Check(f.Soul.visible && !f.Model.NeedsUiCleanup && f.Model.UiCleanupUnresolved && f.Soul.StartCalls == 1 && f.Client.Sent.Count == 1,
            "late response after the cleanup budget still reaches stock and leaves UI unknown");

        f = new Fixture(); f.Start(); f.Model.Stop("stop"); f.Client.Timeout(517);
        f.Role.generalsoul_blue_times = 1; f.Cleanup();
        Check(f.Soul.visible && f.Model.NeedsUiCleanup, "null response with inconsistent readback cannot close");
    }

    private static void FinalControllerAdmission()
    {
        foreach (string revoked in new string[] { "stop", "disk_replaced", "lease_expired", "gui_deadline" })
        {
            Fixture f = new Fixture();
            f.Apply("open");
            f.Queue("start");
            string why, disk = "queued";
            bool stop = false, lease = true, guiTime = true, prepared = false;
            Check(!f.Model.ExecuteGui(f.Context, delegate { }, delegate(DailySoulInvocationCheck prepare, DailySoulInvocationCheck check)
            {
                f.Role.OnFeatureRead = delegate
                {
                    if (revoked == "stop") stop = true;
                    if (revoked == "disk_replaced") disk = "foreign_command";
                    if (revoked == "lease_expired") lease = false;
                    if (revoked == "gui_deadline") guiTime = false;
                };
                prepared = prepare();
                return prepared && !stop && disk == "queued" && lease && guiTime && check();
            }, out why) && prepared && f.Client.Sent.Count == 0 && !f.Model.MutationAttempted &&
                object.ReferenceEquals(f.Push, f.Handler(355).handler), revoked + " during full reread blocks the stock call and restores the observer");
        }

        Fixture replaced = new Fixture(); replaced.Apply("open"); replaced.Queue("start");
        string refusal;
        Check(!replaced.Model.ExecuteGui(replaced.Context, delegate { }, delegate(DailySoulInvocationCheck prepare, DailySoulInvocationCheck check)
        {
            replaced.Role.OnFeatureRead = delegate { replaced.Reward.acts = (Hashtable)replaced.Reward.acts.Clone(); };
            return prepare() && check();
        }, out refusal) && replaced.Client.Sent.Count == 0, "dual cache replacement during full reread refuses the call");

        foreach (string drift in new string[] { "stream_swap", "connect_lost", "socket_disconnected", "stream_closed" })
        {
            Fixture f = new Fixture(); f.Apply("open"); f.Queue("start");
            Check(!f.Model.ExecuteGui(f.Context, delegate { }, delegate(DailySoulInvocationCheck prepare, DailySoulInvocationCheck check)
            {
                if (!prepare()) return false;
                if (drift == "stream_swap") f.Client.m_stream = new MemoryStream();
                if (drift == "connect_lost") f.Client.m_bConnectLost = true;
                if (drift == "socket_disconnected") f.Client.m_client.Connected = false;
                if (drift == "stream_closed") f.Client.m_stream.Close();
                return check();
            }, out refusal) && f.Client.Sent.Count == 0 && !f.Model.MutationAttempted, drift + " after full reread");
        }

        Fixture late = new Fixture(); late.Apply("open"); late.Queue("start");
        Check(!late.Model.ExecuteGui(late.Context, delegate { }, delegate(DailySoulInvocationCheck prepare, DailySoulInvocationCheck check)
        {
            bool granted = prepare() && check();
            late.Client.m_stream = new MemoryStream();
            return granted;
        }, out refusal) && late.Client.Sent.Count == 0, "the immediate model check catches drift after the controller check");
    }

    private static void FinalRefreshAdmission()
    {
        foreach (string drift in new string[] { "cycle", "next_resource_time", "proof", "clock_reversed" })
        {
            Fixture f = new Fixture(); f.Apply("open"); f.Queue("start");
            bool prepared = false;
            string why;
            Check(!f.Model.ExecuteGui(f.Context, delegate { }, delegate(DailySoulInvocationCheck prepare, DailySoulInvocationCheck check)
            {
                prepared = prepare();
                if (drift == "cycle") f.Cycle++;
                if (drift == "next_resource_time") f.Role.next_resource_time++;
                if (drift == "proof") f.RefreshGate = "daily_refresh_unverified";
                if (drift == "clock_reversed") TimeUtil._now--;
                return prepared && check();
            }, out why) && prepared && f.Client.Sent.Count == 0 && !f.Model.MutationAttempted && f.Soul.visible &&
                f.Model.UiCleanupUnresolved && object.ReferenceEquals(f.Push, f.Handler(355).handler),
                drift + " after preparation blocks 517 and holds the UI");
        }

        Fixture late = new Fixture(); late.Apply("open"); late.Queue("start");
        string refusal;
        Check(!late.Model.ExecuteGui(late.Context, delegate { }, delegate(DailySoulInvocationCheck prepare, DailySoulInvocationCheck check)
        {
            bool granted = prepare() && check();
            late.Cycle++;
            return granted;
        }, out refusal) && late.Client.Sent.Count == 0, "immediate guard catches a rollover after the controller check");

        Fixture actual = new Fixture(true);
        Check(DailyRefreshProof.RecordSnapshot(actual.Role, DailyTransport.Read(actual.Client), actual.Cycle, actual.Role.next_resource_time),
            "production refresh proof is available");
        actual.Apply("open"); actual.Queue("start");
        Check(!actual.Model.ExecuteGui(actual.Context, delegate { }, delegate(DailySoulInvocationCheck prepare, DailySoulInvocationCheck check)
        {
            Check(prepare(), "production preparation sees the valid proof");
            Check(DailyRefreshProof.Stop(), "invalidate proof with the same clock and next refresh");
            return check();
        }, out refusal) && actual.Client.Sent.Count == 0 && actual.Soul.visible && actual.Model.UiCleanupUnresolved,
            "production final check observes missing proof without rerunning the full sampler");
    }

    private static void InitialUiOwnershipAndPostCallDrift()
    {
        Fixture f = new Fixture(); f.Queue("open");
        MainUI.OnOpenReturn = delegate { MainUI.m_subui = null; f.Soul.visible = false; };
        string why;
        Check(!f.Model.ExecuteGui(f.Context, delegate { }, Allow, out why) && why == "daily_soul_open_not_owned", "synchronous ownership mismatch");
        MainUI.OnOpenReturn = null; MainUI._resource_soul();
        f.Ready(""); f.Cleanup();
        Check(f.Soul.visible && MainUI.Closes == 0 && f.Model.UiCleanupUnresolved && f.Client.Sent.Count == 0,
            "a later manual open is never adopted after synchronous ownership mismatch");

        f = new Fixture(); f.Queue("open");
        MainUI.OnOpenReturn = delegate { MainUI.subui = "foreign_feature"; };
        Check(!f.Model.ExecuteGui(f.Context, delegate { }, Allow, out why) && why == "daily_soul_open_not_owned",
            "same m_subui without the stock feature is not owned");
        f.Ready(""); f.Cleanup();
        Check(f.Soul.visible && MainUI.Closes == 0, "feature mismatch preserves the foreign UI");

        f = new Fixture(); f.Apply("open");
        MainUI._resource_soul(); MainUI._resource_soul();
        f.Ready(""); f.Cleanup();
        Check(f.Soul.visible && MainUI.Closes == 1, "a synchronous opening's replacement generation is never adopted");

        f = new Fixture();
        MainUI.OnOpenReturn = delegate { f.Soul.loading = true; f.Soul.visible = false; f.Soul.gather_bg = null; };
        f.Apply("open"); f.Ready("");
        MainUI.OnOpenReturn = null;
        f.Soul.loading = false; f.Soul.visible = true; f.Soul.visible_time = DateTime.Now;
        f.Soul.gather_bg = new UnityEngine.GameObject(); f.Soul.interactive = false;
        f.Ready("");
        MainUI._resource_soul(); MainUI._resource_soul(); f.Soul.interactive = true;
        f.Ready(""); f.Cleanup();
        Check(f.Soul.visible && MainUI.Closes == 1, "the first visible generation is pinned before input becomes ready");

        f = new Fixture(); f.Apply("open"); f.Queue("start");
        f.Soul.OnStartReturn = delegate { f.Client.m_stream = new MemoryStream(); };
        Check(!f.Model.ExecuteGui(f.Context, delegate { }, Allow, out why) && why == "daily_soul_transport_changed_after_call",
            "stream swap during stock 517 yields UNKNOWN");
        f.ReplyStart(); f.Unknown();
        Check(f.Soul.StartCalls == 1 && f.Client.Sent.Count == 1, "stock callback after send drift remains intact and cannot replay");

        f = new Fixture(); f.Start(); f.ReplyStart(); f.Queue("harvest");
        f.Soul.OnHarvestReturn = delegate { f.Client.m_bConnectLost = true; };
        Check(!f.Model.ExecuteGui(f.Context, delegate { }, Allow, out why) && why == "daily_soul_transport_changed_after_call",
            "ConnectLost during stock 518 yields UNKNOWN");
        f.ReplyHarvest(); f.Unknown();
        Check(f.Soul.HarvestCalls == 1 && f.Client.Sent.Count == 2, "stock harvest callback after send drift remains intact");

        f = new Fixture(); f.Start(); f.ReplyStart(); f.Apply("harvest"); f.ReplyHarvest(); f.Progress(); f.Soul.FinishGather(); f.Queue("close");
        MainUI.OnCloseReturn = delegate { f.Client.m_stream = new MemoryStream(); };
        Check(!f.Model.ExecuteGui(f.Context, delegate { }, Allow, out why) && why == "daily_soul_transport_changed_after_call",
            "post-close stream drift keeps completion unknown");
        f.Unknown(); f.Cleanup();
        Check(!f.Soul.visible && MainUI.Opens == 1 && MainUI.Closes == 1, "explicit close cannot reopen the page");
    }

    private static void RepeatInOneSession(bool quotaForSecond)
    {
        Fixture f = new Fixture();
        Global.Activity.cond_param = 2;
        Global.Start.free_times = quotaForSecond ? 2 : 1;
        f.Start(); f.ReplyStart(); f.Apply("harvest"); f.ReplyHarvest(); f.Progress(); f.Soul.FinishGather(); f.Apply("close");
        f.Ready(quotaForSecond ? "open" : "");
        Check(f.Model.Json().Contains("\"current\":1") && f.Role.generalsoul_blue_times == 1, "first cycle reads 0 to 1");
        if (!quotaForSecond)
        {
            Check(f.Model.Json().Contains("daily_soul_free_blue_exhausted") && f.Client.Sent.Count == 2,
                "insufficient next free quota does not attempt a paid start");
            return;
        }
        f.Operation = "00000000000000000000000000000056";
        Global.UsedReply = 2; Global.ProgressReply = 2;
        f.Start(); f.ReplyStart(); f.Apply("harvest"); f.ReplyHarvest(); f.Soul.FinishGather(); f.Progress(); f.Apply("close");
        f.Ready("");
        Check(f.Model.Json().Contains("\"status\":\"COMPLETED\"") && f.Model.Json().Contains("\"counter_before\":1,\"counter_after\":2") &&
            f.Role.generalsoul_blue_times == 2 && f.Client.Sent.Count == 4 && f.Role.gold == 13 && D.Money(f.Role) == 5000008000,
            "second cycle freshly admits the remaining free turn and reads 1 to 2");
    }

    private static void InitialUnobservedRevision()
    {
        Fixture f = new Fixture();
        f.Model.Capture(null);
        int first = f.Model.Revision;
        Check(first == 1 && f.Model.Action == "" && !f.Model.Busy &&
            f.Model.Json().Contains("\"character_id\":\"0\"") && f.Model.Json().Contains("daily_soul_unobserved"),
            "first null capture publishes an addressable, unavailable revision");
        BaseAsset.lds.currmg = null;
        f.Capture(); f.Model.Capture(null);
        Check(f.Model.Revision == first && f.Model.Action == "", "unchanged invalid contexts keep the first revision");
        BaseAsset.lds.currmg = f.Root;
        f.Ready("open");
        Check(f.Model.Revision > first, "owned frame advances from the unavailable revision");
    }

    private static void InvalidProjection()
    {
        Fixture f = new Fixture();
        Database.Activity row = (Database.Activity)f.Role.acts[5u];
        foreach (int required in new[] { 0, 256, 1 })
        {
            Global.Activity.cond_param = required; row.condition_param = 2;
            f.Capture();
            Check(f.Model.Action == "" && f.Model.Json().Contains("daily_soul_activity_cache_unverified") &&
                f.Model.Json().Contains("\"current\":null,\"required\":null"), "invalid task bounds stay unobserved: " + required);
        }

        f = new Fixture(); f.Role.generalsoul_blue_times = 1;
        foreach (int free in new[] { 257, Int32.MinValue })
        {
            Global.Start.free_times = free; f.Capture();
            Check(f.Model.Action == "" && f.Model.Json().Contains("daily_soul_free_mode_unverified") &&
                f.Model.Json().Contains("\"free_remaining\":0,"), "invalid free quota cannot leak into the receipt: " + free);
        }

        f = new Fixture();
        f.RefreshGate = "daily_refresh_unverified";
        f.Capture(); int revision = f.Model.Revision;
        f.Cycle += 86400; f.Capture();
        Check(f.Model.Revision > revision && f.Model.Action == "" && f.Model.Json().Contains("\"cycle\":304400"),
            "cycle change while blocked still advances the revision");
    }

    internal static int Main()
    {
        try
        {
            GoldenWire();
            Completed(true);
            Completed(false);
            RejectedBeforeInvoke();
            UnknownAfterInvoke();
            RecoverTimedOutHarvest();
            RecoverOnStopAndVerifyMain();
            RecoverStoppedStart();
            FinalRefreshAdmission();
            FinalControllerAdmission();
            InitialUiOwnershipAndPostCallDrift();
            RepeatInOneSession(true);
            RepeatInOneSession(false);
            InitialUnobservedRevision();
            InvalidProjection();
            Console.WriteLine("DailySoulTests PASS (" + checks + " checks)");
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e.Message);
            Console.Error.WriteLine(e.StackTrace);
            return 1;
        }
    }
}
