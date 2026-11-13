using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using JiangShen.AssistantRuntime;
using D = JiangShen.AssistantRuntime.SharedDevelopmentContext;

namespace UnityEngine
{
    public class Object { public int GetInstanceID() { return 1; } }
    public class Transform { public Transform root { get { return this; } } }
    public class GameObject
    {
        public bool activeInHierarchy = true;
        public readonly Transform transform = new Transform();
        public readonly Dictionary<string, Component> Parts = new Dictionary<string, Component>();
        public Component GetComponent(string name) { return Parts[name]; }
    }
    public class Component : Object
    {
        public GameObject gameObject = new GameObject();
        public Transform transform { get { return gameObject.transform; } }
    }
    public class Behaviour : Component { public bool enabled; }
    public static class Event { public static object current = new object(); }
    public static class GUI { public static bool enabled = true; }
    public static class Time { public static int frameCount = 1; }
}

namespace JiangShen.AssistantRuntime
{
    internal sealed class ProbeFailure : Exception { internal ProbeFailure(string why) : base(why) { } }
    internal static class Model
    {
        internal static Type GameType(string name) { return Type.GetType(name, true); }
        internal static Type FirstType(string name) { return Type.GetType(name, true); }
    }
}

public class __BaseUI : UnityEngine.Behaviour
{
    public static string subui = "";
    public static MessageBox msgBox;
    public UnityEngine.Component rmg;
    public DateTime visible_time;
    public bool loading, collecting, input = true;
    public bool IsVisible() { return enabled; }
    public bool IsEnable() { return input; }
    public void ShowAt(DateTime when) { visible_time = when; enabled = true; loading = false; }
    public static string get_string(uint id) { return "already"; }
    public static string load_str(uint id) { return "error"; }
    public static string load_str(uint id, params object[] args) { return "gift:" + args[0]; }
}
public class BaseUI : __BaseUI { public static RootUI rootUI; }
public sealed class RootUI : BaseUI { public static game.net.tcp_client m_client; public static bool m_bRefreshFive; }
public sealed class CapitalRoot : __BaseUI { public string LevelID = "Capital"; }
public static class BaseAsset
{
    public sealed class LoadScreen { public UnityEngine.Component currmg; }
    public static readonly LoadScreen lds = new LoadScreen();
    public static bool _PHONEUI;
}
public static class TimeUtil { public static uint _now = 220000, _thisDay = 200000; }
public static class BuildingBuildUI { public static bool m_bBuildVisible; }
public static class PopupUI { public static bool popup; }
public static class BuildingBehaviourScript { public static bool m_bEditVisible; }

public sealed class MainUI : BaseUI
{
    public static __BaseUI m_subui;
    public static BuddyUI Buddy;
    public static int Opens, Closes;
    public static bool DelayBuddy;
    public static void _social_friend()
    {
        Opens++;
        if (!handle_sub_ui(Buddy, "buddy")) return;
        Buddy.loading = true;
        if (!DelayBuddy) Buddy.CompleteLoading();
    }
    public static bool handle_sub_ui(__BaseUI ui, string feature)
    {
        if (m_subui == ui) { SetSubUI(null); return false; }
        __BaseUI.subui = feature;
        SetSubUI(ui);
        return true;
    }
    public static void SetSubUI(__BaseUI next)
    {
        if (m_subui != null) m_subui.enabled = false;
        if (next == null) Closes++;
        m_subui = next;
    }
}

public sealed class Buddy
{
    public enum Kind { Buddy, Black }
    public Kind type;
    public ulong id;
    public string name;
    public bool isonline, isfan, isspecial, ismonster;
    public Buddy(ulong id, string name) { this.id = id; this.name = name; }
}

public sealed class BuddyUI : __BaseUI
{
    public DateTime m_LoadOverTime;
    public readonly ArrayList Buddys = new ArrayList(), Blacklist = new ArrayList();
    public Buddy[] Next = new Buddy[0], Blocks = new Buddy[0];
    public int ListReplies, EventReplies;
    public bool Stale, DoNotShow;
    public void CompleteLoading()
    {
        loading = false;
        m_LoadOverTime = DateTime.Now.AddSeconds(-1);
        Global.m_client.Put(798, sc_buddy_load, null);
        Global.m_client.Put(806, sc_buddy_load_event, 1);
    }
    public void sc_buddy_load(MemoryStream stream, object arg)
    {
        ListReplies++;
        if (stream == null || Stale) return;
        Buddys.Clear();
        Blacklist.Clear();
        for (int g = 0; g < 6; g++)
            foreach (Buddy b in Next)
            {
                int rank = b.isspecial ? 0 : b.isfan ? 2 : 4;
                if (rank + (b.isonline ? 0 : 1) == g) Buddys.Add(b);
            }
        foreach (Buddy b in Blocks) Blacklist.Add(b);
        if (!DoNotShow) ShowAt(DateTime.Now);
    }
    public void sc_buddy_load_event(MemoryStream stream, object arg) { EventReplies++; }
}
public sealed class BuddyFlowerUI : __BaseUI
{
    public DateTime m_LoadOverTime;
    public ulong m_BuddyId;
    public string m_BuddyName = "";
    public ulong ReplyId;
    public string ReplyName;
    public uint ReplyTime;
    public int InfoReplies, SendReplies, InfoRequests, FreeRequests, PaidRequests, NameRequests;
    public ulong LastRequested;
    public bool ThrowReply, ChangeGold, WrongMode;
    public void cs_buddy_flower_info(ulong id)
    {
        LastRequested = id; InfoRequests++;
        Global.m_client.Put(808, sc_buddy_flower_info, null);
    }
    public void cs_buddy_flower_info(string name) { NameRequests++; }
    public void sc_buddy_flower_info(MemoryStream stream, object arg)
    {
        InfoReplies++;
        if (stream == null) return;
        m_BuddyId = ReplyId;
        m_BuddyName = ReplyName;
        uint cycle = TimeUtil._now >= TimeUtil._thisDay + 18000 ? TimeUtil._thisDay + 18000 : TimeUtil._thisDay - 68400;
        if (ReplyTime >= cycle)
        {
            __BaseUI.msgBox.Set("already", rmg);
            MainUI.SetSubUI(null);
        }
        else loading = true;
    }
    public void CompleteLoading()
    {
        ShowAt(DateTime.Now.AddSeconds(-1));
        m_LoadOverTime = visible_time.AddMilliseconds(200);
    }
    public void sendflower(int mode)
    {
        if (mode != 0) { PaidRequests++; return; }
        FreeRequests++;
        Global.m_client.Put(809, sc_buddy_send_flower, WrongMode ? 1 : 0);
    }
    public void sc_buddy_send_flower(MemoryStream stream, object arg)
    {
        SendReplies++;
        if (ThrowReply) throw new IOException("original");
        if (stream == null || stream.ReadByte() != 8) return;
        bool ok = stream.ReadByte() == 0;
        __BaseUI.msgBox.Set(ok ? "gift:" + m_BuddyName : "error", rmg);
        if (ok) Global.character_.reputation += 10;
        if (ChangeGold) Global.character_.gold--;
        MainUI.SetSubUI(null);
    }
}

public sealed class MessageBox : __BaseUI
{
    public object OnOK, OnOK_objs, OnClose, Tex, Text, old_rmg;
    public string Title = "", Message = "";
    public bool bNoButton, bCancel, bModal, bGoButton, isPicMode, isGold, m_bWantClose, m_bMouseDown;
    public int Closes;
    public void Set(string text, object root)
    {
        Message = text;
        Text = new object();
        old_rmg = root;
        bNoButton = true;
        ShowAt(DateTime.Now > visible_time ? DateTime.Now : visible_time.AddTicks(1));
    }
    public void ButtonOK(bool ok)
    {
        if (ok || OnOK != null || OnClose != null) throw new InvalidOperationException("foreign close");
        Closes++;
        enabled = false;
    }
}
public static class Global
{
    public static Database.Character character_;
    public static game.net.tcp_client m_client;
    public static bool extern_enabled = true, relogin;
    public static Database._activity Task = new Database._activity();
    public static object getTemplate(string name, uint id) { return name == "activity" && id == 11 ? Task : null; }
}
namespace Database
{
    public sealed class Character
    {
        public ulong id = 45;
        public uint next_resource_time = 304400, reputation = 100, gold_consume;
        public int gold = 999, bind_gold = 30;
        public bool gm;
        public readonly Hashtable acts = new Hashtable(), act_rewards = new Hashtable();
    }
    public sealed class _activity { public byte id = 11, idreward = 2; public bool active = true; public int cond_param = 1; }
    public sealed class Activity { public byte idactivity = 11, condition_param; public int refresh_time; public _activity temp = Global.Task; }
    public sealed class Activity_Reward { public byte idact_reward = 2; public readonly Hashtable acts = new Hashtable(); }
}
public static class RootBase
{
    public static bool StaleCategory;
    public static int Replies;
    public static void Register() { Global.m_client.Put(355, handelActvity, null, -1f); }
    private static void handelActvity(MemoryStream stream, object arg)
    {
        Replies++;
        if (stream == null || stream.ReadByte() != 10) return;
        stream.ReadByte();
        if (stream.ReadByte() != 8 || stream.ReadByte() != 11 || stream.ReadByte() != 16) return;
        byte count = (byte)stream.ReadByte();
        if (stream.ReadByte() != 24) return;
        int refresh = 0, shift = 0;
        for (int value; (value = stream.ReadByte()) != -1; shift += 7) refresh |= (value & 127) << shift;
        var row = new Database.Activity { condition_param = count, refresh_time = refresh >> 1 };
        Global.character_.acts[11u] = row;
        if (!StaleCategory) ((Database.Activity_Reward)Global.character_.act_rewards[2u]).acts[11u] = row;
    }
}

namespace game.net
{
    public class tcp_base
    {
        public delegate void delegate_handler(MemoryStream stream, object arg);
        public sealed class Handler { public delegate_handler handler; public object arg; public float life; }
        public sealed class HandleMessage { public ushort op; }
        public static int connection_state = 3;
        public bool m_bConnectLost, m_bSending;
        public readonly Hashtable m_handlers = new Hashtable();
        public readonly ArrayList messages = new ArrayList(), m_sendDatas = new ArrayList();
    }
    public sealed class tcp_client : tcp_base
    {
        public sealed class Socket { public bool Connected { get; set; } }
        public Socket m_client = new Socket { Connected = true };
        public Stream m_stream = new MemoryStream();
        public Action<ushort, Handler> OnRegister;
        public Action<ushort, Handler, bool> OnReplyBegin;
        public Action<ushort, bool> OnReplyEnd;
        public void Put(ushort op, delegate_handler d, object arg, float life = 10f)
        {
            var handler = new Handler { handler = d, arg = arg, life = life };
            m_handlers[op] = handler;
            if (OnRegister != null) OnRegister(op, handler);
        }
        public void Reply(ushort op, byte[] body, bool fresh = true)
        {
            lock (this)
            {
                Handler h = (Handler)m_handlers[op];
                if (h.life > 0) m_handlers.Remove(op);
                if (OnReplyBegin != null) OnReplyBegin(op, h, fresh && body != null);
                try { h.handler(body == null ? null : new MemoryStream(body), h.arg); }
                finally { if (OnReplyEnd != null) OnReplyEnd(op, fresh && body != null); }
            }
        }
    }
}

internal static class DailyFlowerTests
{
    private const string Id = "00000000000000000000000000000011";
    private const uint Cycle = 218000;
    private static int assertions;

    // The fake transport supplies independent request/response provenance.
    // Its fresh marker is fixture ground truth, never inferred from stock
    // opcode, elapsed time, handler identity, or the callback body.
    private sealed class ProvenEpoch : DailyFlowerEpoch
    {
        private readonly object role;
        private readonly game.net.tcp_client client;
        private readonly DailyTransport transport;
        private readonly Dictionary<ushort, object> pending = new Dictionary<ushort, object>();
        private ushort dispatch;
        private object replyHandler;
        internal bool Exclusive = true;
        internal bool Tainted;

        internal ProvenEpoch(object role, game.net.tcp_client client)
        {
            this.role = role;
            this.client = client;
            transport = DailyTransport.Read(client);
            client.OnRegister = Registered;
            client.OnReplyBegin = BeginningReply;
            client.OnReplyEnd = FinishedReply;
        }

        internal DailyFlowerEpoch Read(object owner, object candidate, DailyTransport observed)
        {
            return Exclusive && !Tainted && ReferenceEquals(role, owner) && ReferenceEquals(client, candidate) &&
                transport.Same(observed) ? this : null;
        }

        public bool CanInvoke(ushort opcode) { return Exclusive && !Tainted && !pending.ContainsKey(opcode); }
        public bool OwnsHandler(ushort opcode, object handler)
        {
            object expected;
            return Exclusive && !Tainted && pending.TryGetValue(opcode, out expected) && ReferenceEquals(handler, expected);
        }
        public bool OwnsReply(ushort opcode, object handler)
        { return OwnsHandler(opcode, handler) && dispatch == opcode && ReferenceEquals(replyHandler, handler); }

        private void Registered(ushort opcode, game.net.tcp_base.Handler handler)
        {
            if (opcode != 798 && opcode != 806 && opcode != 808 && opcode != 809) return;
            if (pending.ContainsKey(opcode)) Tainted = true;
            pending[opcode] = handler;
        }
        private void BeginningReply(ushort opcode, game.net.tcp_base.Handler handler, bool fresh)
        {
            dispatch = opcode;
            replyHandler = fresh ? handler : null;
        }
        private void FinishedReply(ushort opcode, bool fresh)
        {
            if (pending.ContainsKey(opcode))
            {
                if (fresh) pending.Remove(opcode);
                else Tainted = true;
            }
            dispatch = 0;
            replyHandler = null;
        }
    }

    private sealed class Game
    {
        internal readonly BuddyUI Buddy = new BuddyUI();
        internal readonly BuddyFlowerUI Flower = new BuddyFlowerUI();
        internal readonly MessageBox Box = new MessageBox();
        internal readonly CapitalRoot Root = new CapitalRoot { enabled = true };
        internal readonly RootUI GlobalUi = new RootUI { enabled = true };
        internal readonly game.net.tcp_client Client = new game.net.tcp_client();
        internal readonly DailyFlowerModel Model;
        internal readonly ProvenEpoch Epoch;
        internal int Sequence, Linearizations;

        internal Game(params Buddy[] buddies) : this(true, buddies) { }

        internal Game(bool proven, params Buddy[] buddies)
        {
            MainUI.m_subui = null;
            MainUI.Opens = MainUI.Closes = 0;
            MainUI.Buddy = Buddy;
            MainUI.DelayBuddy = false;
            __BaseUI.subui = "";
            __BaseUI.msgBox = Box;
            RootBase.StaleCategory = false;
            RootBase.Replies = 0;
            Global.extern_enabled = true;
            TimeUtil._now = 220000;
            TimeUtil._thisDay = 200000;
            Global.character_ = new Database.Character();
            var category = new Database.Activity_Reward();
            Global.character_.acts[11u] = category.acts[11u] = new Database.Activity();
            Global.character_.act_rewards[2u] = category;
            game.net.tcp_base.connection_state = 3;
            Global.m_client = RootUI.m_client = Client;
            BaseUI.rootUI = GlobalUi;
            BaseAsset.lds.currmg = Root;
            foreach (__BaseUI ui in new __BaseUI[] { Buddy, Flower, Box })
            {
                ui.gameObject = GlobalUi.gameObject;
                ui.rmg = GlobalUi;
                ui.gameObject.Parts.Add(ui.GetType().Name, ui);
            }
            GlobalUi.rmg = GlobalUi;
            Buddy.Next = buddies;
            RootBase.Register();
            Epoch = new ProvenEpoch(Global.character_, Client);
            Model = new DailyFlowerModel(proven ? (DailyFlowerEpochProvider)Epoch.Read : null, DailyRefreshGate.ReadClock);
        }

        internal D Context()
        {
            return new D(new UnityEngine.Object[] { Root, GlobalUi, Buddy, Flower, Box }, Root, GlobalUi,
                Global.character_, Global.character_.id.ToString(), "PASS", false, delegate { return "foreign"; });
        }

        internal bool TryAction(string action, Action afterSample = null, bool lease = true)
        {
            Model.Capture(Context());
            string why;
            if (!Model.Queue(11, Global.character_.id, Id, ++Sequence, Model.Revision, action, out why)) return false;
            return Model.ExecuteGui(Context, delegate { }, delegate(DailyFlowerInvocationCheck sample, DailyFlowerInvocationCheck check)
            {
                if (!sample()) return false;
                if (afterSample != null) afterSample();
                if (!lease || !check()) return false;
                Linearizations++;
                return true;
            }, out why);
        }

        internal void Act(string action)
        {
            Check(TryAction(action), action + ": " + Model.OperationJson() + " " + Model.ViewJson());
        }
        internal void State(string state)
        {
            Model.Capture(Context());
            Check(Model.OperationJson().Contains("\"status\":\"" + state + "\""), state + ": " + Model.OperationJson());
        }
        internal void List(bool delayed = false, bool eventsFirst = false)
        {
            MainUI.DelayBuddy = delayed;
            Act("open");
            if (delayed) { State("OPENING"); Buddy.CompleteLoading(); State("OPENING"); }
            if (eventsFirst) Client.Reply(806, new byte[] { 8, 0, 24, 0, 32, 0, 40, 0, 48, 0 });
            Check(!Buddy.enabled && ReferenceEquals(MainUI.m_subui, Buddy), "selected stock buddy page remains hidden until 798");
            Client.Reply(798, Friends(Buddy.Next, Buddy.Blocks));
            if (!Buddy.Stale && !Buddy.DoNotShow) Check(Buddy.enabled, "original 798 opens the buddy page");
            if (!eventsFirst) Client.Reply(806, new byte[] { 8, 0, 24, 0, 32, 0, 40, 0, 48, 0 });
        }
        internal void Info(ulong id, string name, uint when, bool fresh = true)
        {
            Flower.ReplyId = id;
            Flower.ReplyName = name;
            Flower.ReplyTime = when;
            Client.Reply(808, InfoBytes(id, name, when), fresh);
        }
        internal void Ready()
        {
            List();
            State("CANDIDATE");
            Act("inspect");
            Info(Flower.LastRequested, Buddy.Next[0].name, Cycle - 1);
            State("LOADING");
            Check(!TryAction("send"), "blocked while loading");
            Flower.CompleteLoading();
            State("READY");
        }
        internal void Send()
        {
            Ready();
            Act("send");
            Check(Flower.FreeRequests == 1 && Flower.PaidRequests == 0 && Flower.NameRequests == 0, "only stock 809 mode zero");
        }
        internal void Push(uint refresh = 221000)
        {
            var row = new List<byte> { 8, 11, 16, 1 };
            Field(row, 3, refresh * 2);
            var body = new List<byte>();
            Child(body, 1, row.ToArray());
            Client.Reply(355, body.ToArray());
        }
    }

    private static void Check(bool condition, string label)
    {
        assertions++;
        if (!condition) throw new InvalidOperationException(label);
    }
    private static byte[] Friends(Buddy[] buddies, Buddy[] blocks)
    {
        var body = new List<byte> { 8, 0 };
        foreach (Buddy b in buddies) Child(body, 2, Friend(b, false));
        foreach (Buddy b in blocks) Child(body, 4, Friend(b, true));
        for (int i = 7; i <= 10; i++) Field(body, i, 0);
        return body.ToArray();
    }
    private static byte[] Friend(Buddy b, bool block)
    {
        var row = new List<byte>();
        Field(row, 1, b.id);
        Child(row, 2, Encoding.UTF8.GetBytes(b.name));
        Field(row, 3, 1); Field(row, 4, b.isonline ? 1u : 0u);
        Field(row, 5, !block && b.isfan ? 1u : 0u);
        Field(row, 6, 0); Field(row, 7, 0);
        if (!block) { Field(row, 8, 0); Field(row, 9, b.ismonster ? 1u : 0u); Field(row, 10, b.isspecial ? 1u : 0u); }
        return row.ToArray();
    }
    private static byte[] InfoBytes(ulong id, string name, uint when)
    {
        var body = new List<byte>();
        Field(body, 1, id); Child(body, 2, Encoding.UTF8.GetBytes(name));
        Field(body, 3, 0); Field(body, 4, 0); Field(body, 5, when);
        return body.ToArray();
    }
    private static void Field(List<byte> bytes, int field, ulong value)
    { Varint(bytes, (ulong)(field << 3)); Varint(bytes, value); }
    private static void Child(List<byte> bytes, int field, byte[] value)
    { Varint(bytes, (ulong)(field << 3 | 2)); Varint(bytes, (ulong)value.Length); bytes.AddRange(value); }
    private static void Varint(List<byte> bytes, ulong value)
    {
        for (; value >= 128; value >>= 7) bytes.Add((byte)((value & 127) | 128));
        bytes.Add((byte)value);
    }

    private static void Happy(bool pushFirst)
    {
        var game = new Game(new Buddy(99, "offline"));
        game.Send();
        if (pushFirst) { game.Push(); game.State("PENDING"); }
        game.Client.Reply(809, new byte[] { 8, 0 });
        if (!pushFirst) { game.State("PENDING"); game.Push(); }
        game.State("RESULT_PROMPT");
        game.Act("close");
        game.State("COMPLETED");
        Check(game.Box.Closes == 1 && !game.Model.Busy && !game.Model.Unresolved && game.Linearizations == 4, "fully released");
        Check(game.Model.OperationJson().Contains("\"account_progress_correlated\":true"), "correlated progress");
        Check(Global.character_.gold == 999 && Global.character_.bind_gold == 30 && Global.character_.reputation == 110 &&
            RootBase.Replies == 1 && game.Flower.SendReplies == 1, "resource and original callbacks");
    }

    private static void EligibilityAndSkip()
    {
        var none = new Game(new Buddy(0, "zero"), new Buddy(45, "self"), new Buddy(70, "blocked"),
            new Buddy(90, "duplicate"), new Buddy(90, "duplicate"), new Buddy(91, "ambiguous"), new Buddy(92, "ambiguous"),
            new Buddy(93, "npc") { ismonster = true });
        none.Buddy.Blocks = new[] { new Buddy(70, "blocked") { type = Buddy.Kind.Black } };
        none.List(true, true);
        none.State("EMPTY");
        none.Act("close");
        none.State("NO_CANDIDATES");
        Check(none.Flower.InfoRequests == 0 && none.Flower.FreeRequests == 0 && !none.Model.Busy, "empty owned entry closed");

        var skip = new Game(new Buddy(500, "offline"), new Buddy(501, "special") { isspecial = true });
        skip.List();
        skip.Act("inspect");
        Check(skip.Flower.LastRequested == 501, "follow stock order");
        skip.Info(501, "special", Cycle);
        skip.State("SKIP_PROMPT");
        Check(!skip.TryAction("inspect"), "no alternate while message remains");
        skip.Act("close");
        skip.Act("inspect");
        Check(skip.Box.Closes == 1 && skip.Flower.LastRequested == 500 && skip.Flower.FreeRequests == 0, "skip only after close");
        skip.Info(500, "offline", Cycle);
        skip.State("SKIP_PROMPT");
        skip.Act("close");
        skip.State("NO_CANDIDATES");
    }

    private static void RefuseAndKeepUnknown()
    {
        var error = new Game(new Buddy(99, "A"));
        error.Send(); error.Client.Reply(809, new byte[] { 8, 1 }); error.State("ERROR_PROMPT");
        error.Act("close"); error.State("REJECTED");
        Check(!error.TryAction("inspect") && !error.TryAction("send") && error.Flower.FreeRequests == 1, "definite rejection terminal");
        var timeout = new Game(new Buddy(99, "A"));
        timeout.Send(); timeout.Client.Reply(809, null); timeout.State("UNKNOWN");
        Check(timeout.Flower.SendReplies == 1 && !timeout.TryAction("inspect") && !timeout.TryAction("send"), "null forwarded and no retry");
        var malformed = new Game(new Buddy(99, "A"));
        malformed.Send(); malformed.Client.Reply(809, new byte[] { 8, 0, 8, 0 }); malformed.State("UNKNOWN");
        Check(malformed.Flower.SendReplies == 1 && !malformed.TryAction("close") && malformed.Box.Closes == 0, "malformed still forwarded");
        var noActivity = new Game(new Buddy(99, "A"));
        noActivity.Send(); noActivity.Client.Reply(809, new byte[] { 8, 0 }); noActivity.State("PENDING");
        typeof(DailyFlowerModel).GetField("since", BindingFlags.NonPublic | BindingFlags.Instance)
            .SetValue(noActivity.Model, unchecked(Environment.TickCount - 19000));
        noActivity.State("UNKNOWN");
        Check(!noActivity.TryAction("close"), "809 without 355 is not settled");
        var stale = new Game(new Buddy(99, "A"));
        stale.Send(); RootBase.StaleCategory = true;
        stale.Push(); stale.Client.Reply(809, new byte[] { 8, 0 }); stale.State("UNKNOWN");
        Check(!stale.TryAction("close"), "stale category cache rejected");
        var spend = new Game(new Buddy(99, "A"));
        spend.Send(); spend.Flower.ChangeGold = true; spend.Client.Reply(809, new byte[] { 8, 0 }); spend.State("UNKNOWN");
        var throwReply = new Game(new Buddy(99, "A"));
        throwReply.Send(); throwReply.Flower.ThrowReply = true;
        bool same = false;
        try { throwReply.Client.Reply(809, new byte[] { 8, 0 }); }
        catch (IOException exception) { same = exception.Message == "original"; }
        Check(same, "original exception forwarded");
        throwReply.State("UNKNOWN");
    }

    private static void Boundaries()
    {
        var mismatch = new Game(new Buddy(99, "A"), new Buddy(101, "B"));
        mismatch.List(); mismatch.Act("inspect"); mismatch.Info(101, "B", Cycle - 1); mismatch.State("REJECTED");
        Check(mismatch.Flower.InfoReplies == 1 && !mismatch.TryAction("inspect") && mismatch.Flower.FreeRequests == 0, "recipient mismatch");

        var missed = new Game(new Buddy(99, "A"));
        MainUI.DelayBuddy = true;
        missed.Act("open");
        missed.Buddy.CompleteLoading();
        missed.Client.Reply(798, Friends(missed.Buddy.Next, missed.Buddy.Blocks));
        missed.State("REJECTED");
        Check(missed.Buddy.ListReplies == 1 && !missed.TryAction("inspect") && missed.Flower.InfoRequests == 0,
            "no rebinding to a fresh-looking cached list after the initial 798 is missed");

        var staleFriends = new Game(new Buddy(99, "A"));
        staleFriends.Buddy.Stale = true; staleFriends.List(); staleFriends.State("REJECTED");
        Check(!staleFriends.TryAction("inspect"), "798 must update the observed cache");

        var rollover = new Game(new Buddy(99, "A"));
        rollover.Ready();
        TimeUtil._thisDay += 86400; TimeUtil._now += 86400; Global.character_.next_resource_time += 86400;
        rollover.State("REJECTED"); Check(!rollover.TryAction("send") && rollover.Flower.FreeRequests == 0, "daily cycle changed");

        var role = new Game(new Buddy(99, "A"));
        role.Ready(); Global.character_.id++;
        role.State("REJECTED"); Check(role.Flower.FreeRequests == 0, "role ID drift");

        var overlay = new Game(new Buddy(99, "A"));
        overlay.Ready();
        Check(!overlay.TryAction("send", delegate { PopupUI.popup = true; }) && overlay.Flower.FreeRequests == 0, "final overlay");
        PopupUI.popup = false;

        var lease = new Game(new Buddy(99, "A"));
        lease.Ready();
        Check(!lease.TryAction("send", null, false) && lease.Flower.FreeRequests == 0 &&
            ((game.net.tcp_base.Handler)lease.Client.m_handlers[(ushort)355]).handler.Target == null, "lost lease restores prepared callback");

        var stopped = new Game(new Buddy(99, "A"));
        stopped.Ready();
        Check(!stopped.TryAction("send", stopped.Model.Stop) && stopped.Flower.FreeRequests == 0 && stopped.Model.Busy, "stop at final command read");

        var late = new Game(new Buddy(99, "A"));
        late.List(); late.Act("inspect"); late.Model.Stop(); late.Info(99, "A", Cycle - 1); late.Flower.CompleteLoading();
        Check(late.Flower.InfoReplies == 1 && late.Flower.enabled && late.Model.Busy && !late.TryAction("close") &&
            !late.TryAction("send") && late.Box.Closes == 0, "late original loader remains unverified");

        var roleAfter = new Game(new Buddy(99, "A"));
        roleAfter.Send(); Global.character_.id++;
        roleAfter.Client.Reply(809, new byte[] { 8, 0 }); roleAfter.State("UNKNOWN");
        Check(!roleAfter.TryAction("close"), "foreign role's original effect remains unowned");

        var finalRole = new Game(new Buddy(99, "A"));
        finalRole.Ready();
        Check(!finalRole.TryAction("send", delegate { Global.character_.id++; }) && finalRole.Flower.FreeRequests == 0,
            "last role identity check rejects same-object role drift");

        var disconnected = new Game(new Buddy(99, "A"));
        disconnected.Send(); disconnected.Client.m_bConnectLost = true; disconnected.State("UNKNOWN");
        Check(!disconnected.TryAction("inspect"), "transport loss terminates");

        var replaced = new Game(new Buddy(99, "A"));
        replaced.Ready();
        game.net.tcp_base.delegate_handler other = delegate { };
        Check(!replaced.TryAction("send", delegate { replaced.Client.Put(355, other, null, -1f); }) &&
            ReferenceEquals(((game.net.tcp_base.Handler)replaced.Client.m_handlers[(ushort)355]).handler, other) &&
            replaced.Flower.FreeRequests == 0, "foreign callback preserved");
        var replaced809 = new Game(new Buddy(99, "A"));
        replaced809.Send(); replaced809.Client.Put(809, other, 0); replaced809.State("UNKNOWN");
        Check(ReferenceEquals(((game.net.tcp_base.Handler)replaced809.Client.m_handlers[(ushort)809]).handler, other), "foreign 809 preserved");

        var boxedMode = new Game(new Buddy(99, "A"));
        boxedMode.Ready(); boxedMode.Flower.WrongMode = true;
        Check(!boxedMode.TryAction("send") && boxedMode.Model.OutcomeUnknown && boxedMode.Flower.PaidRequests == 0, "wrong stock arg is not owned");

        var duplicate = new Game(new Buddy(99, "A"));
        duplicate.Send();
        duplicate.Client.messages.Add(new game.net.tcp_base.HandleMessage { op = 809 });
        duplicate.Client.Reply(809, new byte[] { 8, 0 });
        duplicate.State("UNKNOWN");
        Check(duplicate.Flower.SendReplies == 1 && duplicate.Flower.FreeRequests == 1 && !duplicate.TryAction("close"),
            "duplicate queued 809 makes the stock reply unowned");

        var lostSend = new Game(new Buddy(99, "A"));
        lostSend.Send(); lostSend.Model.Stop(); lostSend.Client.Reply(809, new byte[] { 8, 0 });
        lostSend.State("UNKNOWN");
        Check(lostSend.Flower.SendReplies == 1 && !lostSend.TryAction("close") && lostSend.Box.enabled && lostSend.Model.Busy,
            "late original 809 on Stop forwards and leaves its prompt untouched");

        var competing = new Game(new Buddy(99, "A"));
        competing.Ready();
        Check(!competing.TryAction("send", delegate { competing.Client.messages.Add(new game.net.tcp_base.HandleMessage { op = 809 }); }) &&
            competing.Flower.FreeRequests == 0, "queued competing response");
    }

    private static void ProvenanceRequired()
    {
        var noProof = new Game(false, new Buddy(99, "A"));
        Check(!noProof.TryAction("open") && MainUI.Opens == 0 && noProof.Flower.InfoRequests == 0 &&
            noProof.Flower.FreeRequests == 0 && noProof.Model.ViewJson().Contains("flower_clean_transport_provenance_unavailable"),
            "default missing epoch permits zero stock actions");

        foreach (ushort opcode in new ushort[] { 798, 808, 809 })
        {
            var expired = new Game(new Buddy(99, "A"));
            expired.Client.Put(opcode, delegate { }, null);
            expired.Client.Reply(opcode, null);
            Check(!expired.Client.m_handlers.Contains(opcode) && expired.Client.messages.Count == 0 &&
                expired.Client.m_sendDatas.Count == 0 && !expired.TryAction("open") && MainUI.Opens == 0,
                "stock timeout and empty queues do not clear old request " + opcode);
            expired.Client.m_stream = new MemoryStream();
            Check(!expired.TryAction("open") && MainUI.Opens == 0, "stream replacement on reused client is unproved");
        }

        var lostAtCommit = new Game(new Buddy(99, "A"));
        lostAtCommit.Ready();
        Check(!lostAtCommit.TryAction("send", delegate { lostAtCommit.Epoch.Exclusive = false; }) &&
            lostAtCommit.Flower.FreeRequests == 0 && ((game.net.tcp_base.Handler)lostAtCommit.Client.m_handlers[(ushort)355]).handler.Target == null,
            "revoked epoch before stock send preserves the original 355 registration");

        var noShow = new Game(new Buddy(99, "A"));
        noShow.Buddy.DoNotShow = true;
        noShow.List();
        noShow.State("REJECTED");
        Check(!noShow.TryAction("inspect") && noShow.Flower.InfoRequests == 0, "798 needs its original hidden-to-visible readback");

        var staleList = new Game(new Buddy(99, "A"));
        staleList.Act("open");
        staleList.Client.Reply(798, Friends(staleList.Buddy.Next, staleList.Buddy.Blocks), false);
        staleList.State("REJECTED");
        Check(staleList.Buddy.ListReplies == 1 && staleList.Buddy.enabled &&
            !staleList.TryAction("inspect") && staleList.Flower.InfoRequests == 0, "unattributed 798 forwarded without authorizing selection");

        var staleInfo = new Game(new Buddy(99, "A"));
        staleInfo.List(); staleInfo.Act("inspect");
        staleInfo.Info(99, "A", Cycle - 1, false);
        staleInfo.State("REJECTED");
        Check(staleInfo.Flower.InfoReplies == 1 && staleInfo.Flower.loading &&
            !staleInfo.TryAction("send") && staleInfo.Flower.FreeRequests == 0, "valid-looking old 808 remains unowned");

        var staleSend = new Game(new Buddy(99, "A"));
        staleSend.Send();
        staleSend.Client.Reply(809, new byte[] { 8, 0 }, false);
        staleSend.Push();
        staleSend.State("UNKNOWN");
        Check(staleSend.Flower.SendReplies == 1 && RootBase.Replies == 1 && staleSend.Flower.FreeRequests == 1 &&
            !staleSend.TryAction("close") && !staleSend.TryAction("inspect") && staleSend.Box.Closes == 0 &&
            staleSend.Model.OperationJson().Contains("\"account_progress_correlated\":false"),
            "valid-looking old 809 and matching account progress do not establish an owned result");
    }

    private static void DialogOwnership()
    {
        var foreign = new Game(new Buddy(99, "A"), new Buddy(100, "B"));
        foreign.List(); foreign.Act("inspect"); foreign.Info(99, "A", Cycle); foreign.State("SKIP_PROMPT");
        Check(!foreign.TryAction("close", delegate { foreign.Box.Text = new object(); }) && foreign.Box.Closes == 0 &&
            !foreign.TryAction("inspect"), "same text and timestamp with new message generation");
        var foreignRoot = new Game(new Buddy(99, "A"));
        foreignRoot.Send(); foreignRoot.Client.Reply(809, new byte[] { 8, 0 }); foreignRoot.Push(); foreignRoot.State("RESULT_PROMPT");
        Check(!foreignRoot.TryAction("close", delegate { foreignRoot.Box.old_rmg = foreignRoot.Root; }) &&
            foreignRoot.Box.Closes == 0 && foreignRoot.Model.OutcomeUnknown, "wrong prompt root");
        var foreignAction = new Game(new Buddy(99, "A"));
        foreignAction.Send(); foreignAction.Client.Reply(809, new byte[] { 8, 0 }); foreignAction.Push(); foreignAction.State("RESULT_PROMPT");
        Check(!foreignAction.TryAction("close", delegate { foreignAction.Box.OnOK = new object(); }) &&
            foreignAction.Box.Closes == 0, "changed prompt callback");
    }

    public static int Main()
    {
        try
        {
            Happy(false); Happy(true); EligibilityAndSkip(); RefuseAndKeepUnknown(); Boundaries(); ProvenanceRequired(); DialogOwnership();
            Console.WriteLine("DailyFlowerTests passed " + assertions + " assertions");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
