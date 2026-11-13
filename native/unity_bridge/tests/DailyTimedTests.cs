using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;
using JiangShen.AssistantRuntime;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace UnityEngine
{
    internal class Object
    {
        private static int next;
        private readonly int id = ++next;
        internal int GetInstanceID() { return id; }
    }
    internal sealed class Transform { internal Transform root; }
    internal sealed class GameObject
    {
        internal static readonly Dictionary<string, GameObject> Objects = new Dictionary<string, GameObject>();
        internal readonly List<Component> Components = new List<Component>();
        internal readonly Transform transform = new Transform();
        internal bool activeInHierarchy = true;
        internal GameObject() { transform.root = transform; }
        internal static GameObject Find(string name)
        { GameObject go; return Objects.TryGetValue(name, out go) ? go : null; }
        internal Component GetComponent(string name)
        {
            foreach (Component c in Components) if (c.GetType().Name == name) return c;
            return null;
        }
    }
    internal class Component : Object
    {
        internal GameObject gameObject;
        internal Transform transform { get { return gameObject.transform; } }
        internal T Attach<T>(GameObject go) where T : Component { gameObject = go; go.Components.Add(this); return (T)this; }
    }
    internal class Behaviour : Component { internal bool enabled = true; }
    internal sealed class Event { internal static Event current = new Event(); }
    internal static class GUI { internal static bool enabled = true; }
}

namespace JiangShen.AssistantRuntime
{
    internal sealed class ProbeFailure : Exception { internal ProbeFailure(string reason) : base(reason) { } }
    internal sealed class Snapshot { internal Component Root; internal object Character; internal bool InteractionEnabled = true; internal string Gate = "PASS"; }
    internal sealed class Model
    {
        internal static GameObject Host;
        internal static RootMessenger Current, Main, Brawl;
        internal static RootUI RootUi;
        internal static QuizUI QuizUi;
        internal static BrawlUI BrawlUi;
        internal Component CurrentRoot() { return Current; }
        internal GameObject GlobalUiObject() { return Host; }
        internal UObject[] AllUi() { return new UObject[] { RootUi, QuizUi, BrawlUi }; }
        internal Type RootMessengerType { get { return typeof(RootMessenger); } }
        internal Snapshot Capture() { return new Snapshot { Root = Current, Character = Global.character_, InteractionEnabled = MainUI.InputEnabled }; }
        internal string OverlayGate(Behaviour ui) { return "foreign"; }
        internal static Type GameType(string name) { return Type.GetType("JiangShen.AssistantRuntime." + name, true); }
        internal static Type FirstType(string name)
        {
            switch (name)
            {
                case "Database.Character": return typeof(Role);
                case "Database.Activity": return typeof(Activity);
                case "Database.Activity_Reward": return typeof(ActivityReward);
                case "Database.Quiz": return typeof(Quiz);
                case "Database.QuizRunTime": return typeof(QuizRunTime);
                case "Database.BrawlInfo": return typeof(BrawlInfo);
                case "Database.BrawlInfo+Brawler": return typeof(Brawler);
                case "Database.Brawl": return typeof(Brawl);
                case "game.net.tcp_base": return typeof(Client);
                case "game.net.tcp_client": return typeof(Client);
                case "game.net.tcp_socket": return typeof(AlternateClient);
                case "game.net.tcp_base+Handler": return typeof(Handler);
                default: return GameType(name);
            }
        }
    }
    internal sealed class EventTimer { internal short id; internal byte hour; }
    internal sealed class RootMessenger : Component
    {
        internal bool bLoading = false; internal int ExpectingCount = 0; internal string LevelID;
        internal bool IsVisible() { return gameObject.activeInHierarchy; }
    }
    internal static class TimeUtil
    {
        internal static uint _now, _thisDay;
    }
    // Compile the production DailyRefreshGate.cs into this fixture. Only the
    // asynchronous refresh-proof producer is replaced with a detached witness.
    internal static class DailyRefreshProof
    {
        internal static Role Role;
        internal static uint Cycle, Next;
        internal static bool Verified(object role, uint cycle)
        { return object.ReferenceEquals(role, Role) && Cycle == cycle && Role.next_resource_time == Next; }
    }
    internal static class Global
    {
        internal static Role character_; internal static Client m_client; internal static bool relogin = false, extern_enabled = true;
        internal static object getTemplate(string name, uint id)
        { return new EventTimer { id = (short)id, hour = id == 1002 ? (byte)8 : (byte)23 }; }
    }
    internal sealed class Role
    {
        internal ulong id = 12345;
        internal uint SID = 7, gold_consume = 0, next_resource_time;
        internal short level = 59;
        internal bool gm = false;
        internal int gold = 100, bind_gold = 40, money, reputation;
        internal readonly Hashtable acts = new Hashtable(), act_rewards = new Hashtable();
        internal readonly Quiz quiz = new Quiz();
        internal readonly BrawlInfo brawlinfo = new BrawlInfo();
        internal readonly Brawl brawl = new Brawl();
    }
    internal sealed class Quiz
    { internal bool bonus = false, hasquiz = false; internal uint quiz_refresh = 0, rank = 0; internal QuizRunTime info; }
    internal sealed class QuizRunTime
    {
        internal uint cur_question = 0, right_count = 0, wrong_count = 0;
        internal int cur_filter = -1;
        internal bool waitstate = false, end = false;
        internal readonly object checktime = new object();
        internal readonly bool[] filters = new bool[4];
        internal readonly List<uint> questions = new List<uint> { 5 };
    }
    internal sealed class Brawl { internal bool hasBrawl = true; }
    internal sealed class BrawlInfo
    {
        internal uint phase = 2, round = 1, mypos;
        internal static uint nexttime;
        internal bool joined;
        internal Brawler brawler_self;
    }
    internal sealed class Brawler { internal ulong id; internal uint sid; }
    internal sealed class ActivityTemplate { internal byte id, idreward = 4; internal bool active = true; internal int cond_param = 1; }
    internal sealed class Activity { internal byte idactivity, condition_param; internal int refresh_time; internal ActivityTemplate temp; }
    internal sealed class ActivityReward { internal byte idact_reward = 4; internal readonly Hashtable acts = new Hashtable(); }
    internal sealed class Client
    {
        internal static string connection_state = "OK";
        internal bool m_bConnectLost = false;
        internal object m_client = new object();
        internal object m_stream = new object();
        internal readonly Hashtable m_handlers = new Hashtable();
        internal readonly ArrayList messages = new ArrayList();
    }
    internal sealed class AlternateClient { }
    internal delegate void Callback(MemoryStream stream, object arg);
    internal sealed class Handler
    {
        internal Callback handler;
        internal readonly object arg = null;
        internal readonly float life;
        internal Handler(Callback callback, float lifetime) { handler = callback; life = lifetime; }
    }
    internal static class StockDispatch
    {
        internal static Action<MemoryStream, object> After;
        internal static void Forward(MemoryStream stream, object arg)
        { if (After != null) After(stream, arg); }
    }
    internal static class RootBase
    {
        internal static void handelActvity(MemoryStream stream, object arg) { StockDispatch.Forward(stream, arg); }
        internal static void handleBrawlJoinName(MemoryStream stream, object arg) { StockDispatch.Forward(stream, arg); }
    }
    internal class __BaseUI : Behaviour { }
    internal class TimedUI : __BaseUI
    {
        internal RootMessenger rmg;
        internal bool first = false, m_bInit = false, loading = false, collecting = false, interactionEnabled = true;
        internal bool IsVisible() { return true; }
        internal bool IsEnable() { return interactionEnabled; }
    }
    internal sealed class RootUI : TimedUI
    {
        internal static Client m_client;
        internal static bool m_bRefreshFive;
        internal bool DelayBrawlRefresh;
        internal int BrawlEnters;
        private void brawl_enter()
        {
            BrawlEnters++;
            BaseAsset.lds.oldrmg = Model.Current;
            Model.Current = Model.Brawl;
            Model.BrawlUi.enabled = true;
            BrawlInfo.nexttime = 0;
            if (!DelayBrawlRefresh) Model.BrawlUi.OpenNative();
        }
    }
    internal sealed class QuizUI : TimedUI
    {
        internal Quiz myquiz;
        internal int tip_phase = 0, inquiz_phase = 0, end_phase = 1, Requests, QuickRequests, QuickReturns;
        internal bool QuickStartsRuntime;
        private void SubLoading() { MainUI.m_subui = this; enabled = true; }
        private void Disable() { interactionEnabled = false; }
        private void cs_startquiz()
        {
            Requests++;
            RootUI.m_client.m_handlers[(ushort)312] = new Handler(sc_startquiz, 10f);
        }
        private void sc_startquiz(MemoryStream stream, object arg) { StockDispatch.Forward(stream, arg); }
        private void cs_quizQuickAnwser()
        {
            if (interactionEnabled) throw new InvalidOperationException("quick UI branch failed to disable first");
            QuickRequests++;
            RootUI.m_client.m_handlers[(ushort)1829] = new Handler(sc_quizQuickAnwser, 10f);
        }
        private void sc_quizQuickAnwser(MemoryStream stream, object arg)
        {
            QuickReturns++;
            interactionEnabled = true;
            if (stream == null) { StockDispatch.Forward(stream, arg); return; }
            myquiz.hasquiz = true; myquiz.rank = 4;
            if (QuickStartsRuntime)
            {
                myquiz.info = new QuizRunTime();
                myquiz.info.questions.Add(7);
            }
            else
            {
                myquiz.quiz_refresh = 1700086400u; myquiz.bonus = true;
                Global.character_.money += 3500;
                Global.character_.reputation += 14;
            }
            end_phase = 0;
            StockDispatch.Forward(stream, arg);
        }
        internal void DrawInQuiz()
        {
            if (myquiz.info != null && inquiz_phase == 0) prep_question();
        }
        private void prep_question() { inquiz_phase = 1; }
        internal void FinishQuizFadeIn()
        { if (inquiz_phase == 1) inquiz_phase = 2; }
    }
    internal sealed class BrawlUI : TimedUI
    {
        internal static bool toClose = false;
        internal BrawlInfo mybrawlinfo;
        internal string subwindow = "";
        internal int battle_state = 0, Requests;
        internal void OpenNative()
        {
            if (Global.character_.brawl.hasBrawl) RootUI.m_client.m_handlers[(ushort)323] = new Handler(sc_refresh, 10f);
            else RootUI.m_client.m_handlers[(ushort)319] = new Handler(sc_init, 10f);
        }
        private void sc_refresh(MemoryStream stream, object arg) { StockDispatch.Forward(stream, arg); }
        private void sc_init(MemoryStream stream, object arg) { }
        internal void ManualJoin() { cs_join(); }
        private void cs_join() { Requests++; }
    }
    internal static class BaseUI { internal static RootUI rootUI; }
    internal static class BaseAsset { internal static LoadScreen lds; }
    internal sealed class LoadScreen : Behaviour { internal bool isBattling = false; internal RootMessenger oldrmg = null; }
    internal static class MainUI
    {
        internal static Component m_subui;
        internal static bool InputEnabled, DelayMainRestore;
        internal static int CloseCalls;
        internal static void SetSubUI(__BaseUI ui)
        {
            if (ui != null || m_subui == null) throw new InvalidOperationException("wrong stock close branch");
            CloseCalls++;
            ((Behaviour)m_subui).enabled = false;
            m_subui = null;
            InputEnabled = !DelayMainRestore;
        }
    }
    internal static class BuildingBuildUI { internal static bool m_bBuildVisible = false; }
    internal static class BuildingBehaviourScript { internal static bool m_bEditVisible = false; }
    internal static class PopupUI { internal static bool popup = false; }
}

internal static class DailyTimedTests
{
    private const string Operation = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Other = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly Dictionary<int, DevelopmentCallbackCapture> Captures = new Dictionary<int, DevelopmentCallbackCapture>();
    private static DailyTimedModel model;
    private static Role role;
    private static int sequence, acceptedSequence;
    private static Action onActivityInstall;
    // Serialized by the pinned pbdata SCQuizQuickAnwser_0x725 and its
    // protobuf-net runtime, with a finished dbquiz, rank 4 and reward true.
    private static readonly byte[] QuickSuccess = {
        0x08, 0x00, 0x12, 0x19, 0x0A, 0x13, 0x08, 0x80, 0x85, 0xD5, 0xAA, 0x06, 0x10, 0x24,
        0x18, 0xB6, 0x01, 0x20, 0x07, 0x28, 0x03, 0x30, 0x02, 0x38, 0x01, 0x18, 0x04, 0x28, 0x01
    };
    private static readonly byte[] QuickWithRuntime = {
        0x08, 0x00, 0x12, 0x24, 0x0A, 0x00, 0x12, 0x1C, 0x08, 0x05, 0x08, 0x07, 0x10, 0x0A, 0x18, 0x00,
        0x20, 0x00, 0x28, 0x00, 0x30, 0x00, 0x38, 0x00, 0x40, 0x01, 0x48, 0x00, 0x48, 0x00, 0x48, 0x00,
        0x48, 0x00, 0x50, 0x00, 0x18, 0x04, 0x28, 0x00
    };

    private static DevelopmentCallbackCapture Install(object client, object handler, int opcode, string operationId,
        int seq, Delegate original, Action<byte[]> observe, out string why)
    {
        Check(object.ReferenceEquals(client, Global.m_client) && object.ReferenceEquals(handler, Global.m_client.m_handlers[(ushort)opcode]) &&
            operationId == Operation && seq == acceptedSequence && original != null, "exact callback binding");
        DevelopmentCallbackCapture capture = DevelopmentCallbackCapture.InstallDailyTimed(client, handler, opcode, operationId, seq, original, observe, out why);
        Check(capture != null, "production capture must install: " + opcode + " " + why);
        Captures[opcode] = capture;
        if (onActivityInstall != null && opcode == 355) onActivityInstall();
        return capture;
    }

    private static void Reset()
    {
        Captures.Clear(); GameObject.Objects.Clear(); sequence = 0; acceptedSequence = 0; onActivityInstall = null;
        Global.character_ = role = new Role();
        Global.m_client = RootUI.m_client = new Client();
        RootUI.m_bRefreshFive = false;
        Global.m_client.m_handlers[(ushort)355] = new Handler(RootBase.handelActvity, -1f);
        Global.m_client.m_handlers[(ushort)322] = new Handler(RootBase.handleBrawlJoinName, -1f);
        TimeUtil._thisDay = 1700000000;
        TimeUtil._now = TimeUtil._thisDay + 9 * 3600;
        VerifyRefresh(TimeUtil._thisDay + 18000);
        Model.Current = Model.Main = new RootMessenger { LevelID = "Campaign" }.Attach<RootMessenger>(new GameObject());
        GameObject brawl = new GameObject(), ui = new GameObject(), uiRoot = new GameObject();
        Model.Brawl = new RootMessenger { LevelID = "brawl" }.Attach<RootMessenger>(brawl);
        RootMessenger persistent = new RootMessenger { LevelID = "Global" }.Attach<RootMessenger>(uiRoot);
        Model.Host = new GameObject();
        Model.Host.transform.root = persistent.transform;
        ui.transform.root = brawl.transform;
        GameObject.Objects["/Brawl"] = brawl; GameObject.Objects["/Brawl/BrawlUI"] = ui;
        BaseUI.rootUI = Model.RootUi = new RootUI { rmg = persistent }.Attach<RootUI>(Model.Host);
        Model.QuizUi = new QuizUI { rmg = persistent, myquiz = role.quiz, enabled = false }.Attach<QuizUI>(Model.Host);
        Model.BrawlUi = new BrawlUI { rmg = Model.Brawl, mybrawlinfo = role.brawlinfo, enabled = false }.Attach<BrawlUI>(ui);
        MainUI.m_subui = null; MainUI.InputEnabled = true; MainUI.DelayMainRestore = false; MainUI.CloseCalls = 0; BrawlInfo.nexttime = 0;
        BaseAsset.lds = new LoadScreen { enabled = false }.Attach<LoadScreen>(new GameObject());
        GameObject.Objects["/LoadUI"] = BaseAsset.lds.gameObject;
        role.act_rewards[4u] = new ActivityReward();
        Row(12, 0, new ActivityTemplate { id = 12 }); Row(13, 0, new ActivityTemplate { id = 13 });
        model = new DailyTimedModel(new Model(), Install);
        model.Capture(false);
    }

    private static void VerifyRefresh(uint cycle)
    {
        DailyRefreshProof.Role = role;
        DailyRefreshProof.Cycle = cycle;
        DailyRefreshProof.Next = role.next_resource_time = cycle + 86400;
    }

    private static void EnterNextCycle(bool verified)
    {
        TimeUtil._thisDay += 86400;
        TimeUtil._now = TimeUtil._thisDay + 18000;
        if (verified) VerifyRefresh(TimeUtil._now);
    }

    private static void Row(byte task, byte count, ActivityTemplate template)
    {
        Activity row = new Activity { idactivity = task, condition_param = count, refresh_time = -1, temp = template };
        role.acts[(uint)task] = ((ActivityReward)role.act_rewards[4u]).acts[(uint)task] = row;
    }

    private static void Check(bool yes, string why)
    { if (!yes) { Console.Error.WriteLine(why); throw new Exception(why); } }
    private static IDictionary Json() { return (IDictionary)new JavaScriptSerializer().DeserializeObject(model.Json()); }
    private static object OperationField(string key) { return ((IDictionary)Json()["operation"])[key]; }
    private static object TaskField(int task, string key) { return ((IDictionary)((IDictionary)Json()["tasks"])[task.ToString()])[key]; }
    private static bool Queue(int task, bool open, ulong id, string operation, out string why)
    { return QueueAction(task, open ? "open" : "participate", id, operation, out why); }

    private static bool QueueAction(int task, string action, ulong id, string operation, out string why)
    {
        if (!model.Queue(task, id, operation, ++sequence, (int)Json()["revision"], action, out why)) return false;
        acceptedSequence = sequence;
        return true;
    }
    private static void QueueAndRun(int task, bool open)
    {
        string why;
        Check(Queue(task, open, role.id, Operation, out why), "queue " + task + ": " + why);
        Check(ExecuteGui(delegate { }, out why), "gui " + task + ": " + why);
        model.Capture(false);
    }
    private static void StartQuiz()
    {
        QueueAndRun(12, true);
        Check((string)OperationField("status") == "READY" && !Model.QuizUi.m_bInit, "native Quiz Show leaves m_bInit false");
        string why;
        Check(!Queue(12, false, role.id, Other, out why), "same operation required");
        QueueAndRun(12, false);
        Check(Model.QuizUi.Requests == 1 && (string)OperationField("status") == "PENDING", "one ordinary 312 request");
    }
    private static void StartBrawl()
    {
        OpenBrawl();
        string why;
        Check(!Queue(13, false, role.id, Other, out why), "same brawl operation required");
        int previous = Model.BrawlUi.Requests;
        QueueAndRun(13, false);
        Check(Model.BrawlUi.Requests == previous + 1 && (string)OperationField("status") == "PENDING", "one normal 321 request");
    }
    private static void OpenBrawl()
    {
        QueueAndRun(13, true);
        Check((string)OperationField("status") == "OPENING" && Captures.ContainsKey(323), "owned open binds exact sc_refresh");
        ReplyBrawlRefresh();
        model.Capture(false);
        Check((string)OperationField("status") == "READY" && Model.RootUi.BrawlEnters == 1 &&
            BrawlInfo.nexttime == 0 && !Model.BrawlUi.m_bInit, "native 323 with zero nexttime and false m_bInit opens normally");
    }
    private static void ReplyBrawlRefresh()
    {
        Dispatch(323, new byte[] { 8, 0, 24, 2, 48, 0, 56, 0 }, delegate
        {
            role.brawlinfo.joined = false;
            role.brawlinfo.phase = 2;
            BrawlInfo.nexttime = 0;
        }, true);
    }
    private static void Progress(byte task, bool apply, bool returned)
    {
        Dispatch(355, new byte[] { 10, 6, 8, task, 16, 1, 24, 1 },
            delegate { if (apply) Row(task, 1, ((Activity)role.acts[(uint)task]).temp); }, returned);
    }
    private static void QuizReply()
    {
        Dispatch(312, new byte[] { 10, 8, 10, 0, 18, 4, 8, 5, 56, 0 }, delegate { role.quiz.info = new QuizRunTime(); }, true);
    }
    private static void Join(ulong id, uint server, bool apply, bool returned)
    {
        List<byte> bytes = new List<byte> { 8 };
        ulong value = id;
        while (value >= 128) { bytes.Add((byte)(128 | (value & 127))); value >>= 7; }
        bytes.Add((byte)value);
        bytes.AddRange(new byte[] { 18, 1, 65, 24, 3, 32, 1, 40, 5, 48, 1, 56, 2, 64, 20, 72, (byte)server });
        Dispatch(322, bytes.ToArray(), delegate
        {
            if (!apply) return;
            role.brawlinfo.joined = true; role.brawlinfo.mypos = 3;
            role.brawlinfo.brawler_self = new Brawler { id = id, sid = server };
        }, returned);
    }

    private static bool ExecuteGui(DevelopmentPublication publish, out string why)
    { return model.ExecuteGui(publish, delegate { return true; }, out why); }

    private static void Dispatch(int opcode, byte[] body, Action apply, bool returned)
    {
        IDictionary handlers = Global.m_client.m_handlers;
        Handler stock = (Handler)handlers[(ushort)opcode];
        if (stock.life > 0f) handlers.Remove((ushort)opcode);
        using (MemoryStream stream = body == null ? null : new MemoryStream())
        {
            if (stream != null)
            {
                stream.WriteByte(255);
                stream.Write(body, 0, body.Length);
                stream.Position = 1;
            }
            InvalidOperationException failure = new InvalidOperationException("synthetic stock callback failure");
            int calls = 0;
            StockDispatch.After = delegate(MemoryStream actual, object arg)
            {
                Check(object.ReferenceEquals(actual, stream) && arg == null &&
                    (actual == null || actual.Position == 1), "adapter forwards identical stream, cursor and argument");
                calls++;
                apply();
                if (!returned) throw failure;
            };
            try { stock.handler(stream, null); Check(returned, "expected stock exception"); }
            catch (InvalidOperationException error)
            { Check(!returned && object.ReferenceEquals(error, failure), "adapter forwards original exception identity"); }
            finally { StockDispatch.After = null; }
            Check(calls == 1, "adapter forwards stock callback exactly once");
        }
    }

    private static int Main()
    {
        Reset();
        string why;
        TimeUtil._now = TimeUtil._thisDay + 8 * 3600;
        model.Capture(false);
        Check((string)TaskField(12, "reason") == "quiz_closed", "start boundary");
        TimeUtil._now = TimeUtil._thisDay + 23 * 3600;
        model.Capture(false);
        Check((string)TaskField(12, "reason") == "quiz_closed", "end boundary");
        TimeUtil._now -= 1;
        role.level = 60; role.brawlinfo.phase = 1;
        model.Capture(false);
        Check((string)TaskField(12, "action") == "open" && (string)TaskField(13, "reason") == "brawl_closed", "level 60 uses the ordinary quick route");
        role.level = 59;
        model.Capture(false);
        Check(!Queue(12, true, role.id + 1, Operation, out why), "wrong role");
        Check(Queue(12, true, role.id, Operation, out why), "fresh quiz");
        try { ExecuteGui(delegate { throw new OperationCanceledException(); }, out why); }
        catch (OperationCanceledException) { model.NotInvoked("stop"); }
        Check(!Model.QuizUi.enabled && !model.Busy && (bool)OperationField("invoked") == false, "stop before normal invocation");

        Reset();
        Check(Queue(12, true, role.id, Operation, out why), "queue before same-client reconnect");
        Check(!ExecuteGui(delegate { Global.m_client.m_client = new object(); }, out why) &&
            !Model.QuizUi.enabled && !(bool)OperationField("invoked"), "replaced transport before call");

        CheckBrawl();

        Reset(); StartQuiz();
        QuizReply();
        Progress(12, false, true);
        model.Capture(false);
        Check((string)OperationField("status") == "PENDING", "push without applied counter remains pending");
        model.Stop("test_stop");
        Check(model.Unresolved && !Queue(12, false, role.id, Other, out why), "ambiguous quiz cannot retry");

        Reset(); StartQuiz();
        QuizReply();
        Progress(12, true, false);
        Progress(13, true, true);
        model.Capture(false);
        Check(model.Unresolved && !Captures[355].ReturnedForDispatch(1) && Captures[355].ReturnedForDispatch(2),
            "an unrelated activity return cannot qualify the earlier quiz push");

        Reset(); StartQuiz();
        Global.m_client.m_client = new object();
        model.Capture(false);
        Check(model.Unresolved && (string)OperationField("reason") == "daily_timed_character_or_client_changed",
            "same client object does not survive a replaced transport");

        Reset(); StartQuiz();
        QuizReply(); Progress(12, true, true);
        role.bind_gold--;
        model.Capture(false);
        Check(model.Unresolved && (string)OperationField("reason") == "daily_timed_premium_balance_changed", "paid balance delta refuses success");

        Reset(); StartQuiz();
        Check(Model.QuizUi.Requests == 1 && (int)OperationField("opcode") == 312, "level 59 sends only ordinary quiz start");
        QuizReply();
        model.Capture(false);
        Check((string)OperationField("status") == "PENDING", "quiz start response alone cannot settle");
        Progress(12, true, true); model.Capture(false);
        Check((string)OperationField("status") == "SUCCEEDED", "normal quiz response plus real counter");

        Reset(); StartQuiz();
        Dispatch(312, null, delegate { }, true);
        model.Capture(false);
        Check(model.Unresolved, "null quiz response is ambiguous");
        Reset(); QueueAndRun(12, true);
        Check(Queue(12, false, role.id, Operation, out why), "queue before final lease check");
        Check(!model.ExecuteGui(delegate { }, delegate { return false; }, out why) &&
            why == "daily_timed_gui_final_admission_changed" && Model.QuizUi.Requests == 0 && !Captures[355].Read().Armed &&
            !(bool)OperationField("invoked"), "final admission denial disarms without invoking stock quiz");
        DailyTimedWire.Progress p;
        Check(!DailyTimedWire.ReadProgress(new byte[] { 10, 2, 8 }, 12, out p), "truncated 355");
        DailyTimedWire.Question q;
        Check(!DailyTimedWire.ReadQuestion(new byte[] { 10, 2, 18, 0 }, out q), "missing quiz runtime data");
        CheckRefresh();
        CheckQuickQuiz();
        CheckQuickRuntime();
        CheckQuizClose();
        Console.WriteLine("{\"status\":\"PASS\",\"scope\":\"timed 312/1829, owned quiz close, brawl account evidence without attribution, refresh proof and rollover, stop, role, premium and production callbacks\"}");
        return 0;
    }

    private static void CheckQuizClose()
    {
        string why;
        Reset(); StartQuiz();
        Check(!QueueAction(12, "close", role.id, Operation, out why), "pending quiz may not close");
        QuizReply();
        Model.QuizUi.DrawInQuiz();
        Progress(12, true, true);
        model.Capture(false);
        Check((string)TaskField(12, "action") == "close" && model.Busy &&
            !QueueAction(12, "close", role.id, Other, out why), "close requires matching operation and confirmed participation");
        QuizRunTime saved = role.quiz.info;
        object timer = saved.checktime;
        Model.QuizUi.FinishQuizFadeIn();
        model.Capture(false);
        MainUI.DelayMainRestore = true;
        Check(QueueAction(12, "close", role.id, Operation, out why) && ExecuteGui(delegate { }, out why),
            "owned quiz phase 2 invokes stock main close");
        model.Capture(false);
        Check((string)OperationField("status") == "CLOSING" && MainUI.CloseCalls == 1 &&
            !Model.QuizUi.enabled && MainUI.m_subui == null, "hidden quiz alone does not prove main restored");
        MainUI.InputEnabled = true;
        model.Capture(false);
        Check((string)OperationField("stage") == "close" && (string)OperationField("status") == "SUCCEEDED" &&
            (string)OperationField("reason") == "daily_timed_quiz_closed_main_restored" && (int)OperationField("opcode") == 0 &&
            OperationField("counter_before") == null && OperationField("counter_after") == null && !model.Busy &&
            object.ReferenceEquals(saved, role.quiz.info) && object.ReferenceEquals(timer, role.quiz.info.checktime) &&
            Model.QuizUi.Requests == 1 && role.gold == 100 && role.bind_gold == 40 &&
            !QueueAction(12, "close", role.id, Operation, out why), "one close keeps quiz runtime, daily progress and premium untouched");

        Reset(); StartQuickQuiz();
        ReplyQuickQuiz(QuickSuccess, true); Progress(12, true, true); model.Capture(false);
        Check(QueueAction(12, "close", role.id, Operation, out why) && ExecuteGui(delegate { }, out why), "confirmed quick quiz can close");
        model.Capture(false);
        Check((string)OperationField("status") == "SUCCEEDED" && role.quiz.bonus && role.quiz.info == null &&
            Model.QuizUi.QuickRequests == 1 && MainUI.CloseCalls == 1, "quick quiz close leaves the reward and quiz record intact");

        Reset(); StartQuiz(); QuizReply(); Progress(12, true, true); model.Capture(false);
        Check(QueueAction(12, "close", role.id, Operation, out why), "owned close queues before final owner read");
        Check(!ExecuteGui(delegate { MainUI.m_subui = Model.RootUi; }, out why) && MainUI.CloseCalls == 0 &&
            Model.QuizUi.enabled, "replaced panel slot fails closed");

        Reset(); StartQuiz(); QuizReply(); Progress(12, true, true); model.Capture(false);
        Check(QueueAction(12, "close", role.id, Operation, out why), "owned close queues before final stop gate");
        Check(!model.ExecuteGui(delegate { }, delegate { return false; }, out why) && MainUI.CloseCalls == 0 &&
            Model.QuizUi.enabled && !(bool)OperationField("invoked"), "stop before close leaves stock quiz open");

        Reset(); StartQuiz(); QuizReply(); Progress(12, true, true); model.Capture(false);
        MainUI.DelayMainRestore = true;
        Check(QueueAction(12, "close", role.id, Operation, out why) && ExecuteGui(delegate { }, out why), "closing waits for stock main");
        model.Stop("test_stop_after_close_call");
        Check(model.Unresolved && MainUI.CloseCalls == 1, "stop after unsettled close preserves unknown without replay");
    }

    private static void CheckRefresh()
    {
        string why;
        uint now, day, cycle;
        Reset();
        TimeUtil._now = TimeUtil._thisDay + 17999;
        VerifyRefresh(TimeUtil._thisDay + 18000 - 86400);
        Check(DailyRefreshGate.Read(role, out now, out day, out cycle) == "" && cycle == TimeUtil._now + 1 - 86400,
            "04:59:59 is the previous cycle");
        TimeUtil._now++;
        Check(DailyRefreshGate.Read(role, out now, out day, out cycle) == "daily_refresh_pending" && cycle == TimeUtil._now,
            "05:00 exactly begins the new cycle and its resource timer is due");
        model.Capture(false);
        Check((string)TaskField(12, "reason") == "daily_refresh_pending" && TaskField(12, "current") == null, "do not expose old daily counter");
        role.next_resource_time += 86400;
        model.Capture(false);
        Check((string)TaskField(12, "reason") == "daily_refresh_unverified", "future timer alone is not fresh");
        VerifyRefresh(TimeUtil._now);
        RootUI.m_bRefreshFive = true;
        model.Capture(false);
        Check((string)TaskField(12, "reason") == "daily_refresh_pending", "refresh flag dominates valid timer and proof");

        Reset();
        Check(Queue(12, true, role.id, Operation, out why), "queue before reset");
        Check(!ExecuteGui(delegate { EnterNextCycle(false); }, out why) && why == "daily_refresh_pending" &&
            !Model.QuizUi.enabled && Captures.Count == 0 && !(bool)OperationField("invoked"), "refresh due at publication prevents invocation");

        Reset(); QueueAndRun(12, true);
        Check(Queue(12, false, role.id, Operation, out why), "queue quiz before final refresh gate");
        onActivityInstall = delegate { RootUI.m_bRefreshFive = true; };
        Check(!ExecuteGui(delegate { }, out why) && why == "daily_refresh_pending" &&
            Model.QuizUi.Requests == 0 && !Captures[355].Read().Armed, "late refresh disarms observer before any quiz request");

        Reset(); QueueAndRun(12, true);
        Check(Queue(12, false, role.id, Operation, out why), "queue ordinary quiz in old cycle");
        EnterNextCycle(true);
        model.Capture(false);
        Check((string)OperationField("status") == "REJECTED" && (string)OperationField("reason") == "daily_timed_refresh_cycle_changed" &&
            !ExecuteGui(delegate { }, out why) && Model.QuizUi.Requests == 0, "queued prior-cycle participation cannot dispatch");

        Reset(); StartQuiz(); QuizReply(); Progress(12, true, true);
        EnterNextCycle(true);
        model.Capture(false);
        Check(model.Unresolved && (string)OperationField("reason") == "daily_timed_refresh_cycle_changed" &&
            OperationField("counter_after") == null, "same proof shape in next cycle cannot settle old request");

        Reset(); StartQuiz();
        RootUI.m_bRefreshFive = true;
        model.Capture(false);
        Check(model.Unresolved && (string)OperationField("reason") == "daily_refresh_pending", "refresh during native request is ambiguous");
    }

    private static void OpenQuickQuiz()
    {
        role.level = 87;
        model.Capture(false);
        QueueAndRun(12, true);
        Check((string)OperationField("status") == "READY", "level 87 ordinary quiz view opens");
    }

    private static void StartQuickQuiz()
    {
        OpenQuickQuiz();
        QueueAndRun(12, false);
        Check(Model.QuizUi.QuickRequests == 1 && Model.QuizUi.Requests == 0 && (int)OperationField("opcode") == 1829 &&
            Captures.ContainsKey(1829) && !Model.QuizUi.interactionEnabled, "only original 1829 branch invoked and panel disabled");
    }

    private static void ReplyQuickQuiz(byte[] body, bool returned)
    {
        Dispatch(1829, body, delegate { }, returned);
    }

    private static void CheckQuickQuiz()
    {
        string why;
        DailyTimedWire.QuickQuiz packet;
        Check(DailyTimedWire.ReadQuickQuiz(QuickWithRuntime, out packet) && packet.Question != null &&
            packet.Question.Count == 2 && packet.Question.Index == 0 && packet.Question.Id == 5,
            "real protobuf optional quick runtime");

        Reset(); OpenQuickQuiz();
        Handler paid = new Handler(RootBase.handelActvity, 10f);
        Global.m_client.m_handlers[(ushort)384] = paid;
        model.Capture(false);
        Check(!Queue(12, false, role.id, Operation, out why) && why == "daily_timed_network_not_idle" &&
            Model.QuizUi.QuickRequests == 0, "existing paid 384 blocks normal 1829");
        Check(DevelopmentCallbackCapture.InstallDailyTimed(Global.m_client, paid, 384, Operation, 1, paid.handler,
            delegate { }, out why) == null, "timed adapter cannot wrap paid 384");

        Reset(); StartQuickQuiz();
        ReplyQuickQuiz(QuickSuccess, true);
        model.Capture(false);
        Check(Model.QuizUi.interactionEnabled && Model.QuizUi.QuickReturns == 1 &&
            (string)OperationField("status") == "PENDING", "original quick callback restores the panel and cannot settle without 355");
        Progress(12, true, true);
        model.Capture(false);
        Check((string)OperationField("status") == "SUCCEEDED" && (int)OperationField("counter_after") == 1 &&
            role.gold == 100 && role.bind_gold == 40 && role.money == 3500 && role.reputation == 14,
            "1829 allows native money and reputation rewards with unchanged premium balances");

        Reset(); StartQuickQuiz();
        ReplyQuickQuiz(null, true);
        model.Capture(false);
        Check(model.Unresolved && Model.QuizUi.interactionEnabled && Model.QuizUi.QuickReturns == 1 &&
            !Queue(12, false, role.id, Other, out why), "original timeout restores the panel and does not permit a retry");

        Reset(); StartQuickQuiz();
        model.Stop("user_stop");
        Handler stock = (Handler)Global.m_client.m_handlers[(ushort)1829];
        stock.handler(null, null);
        Check(model.Unresolved && !Captures[1829].Read().Armed && !Captures[355].Read().Armed &&
            Model.QuizUi.interactionEnabled && Model.QuizUi.QuickRequests == 1, "stop detaches observers and stock timeout restores input");

        Reset(); StartQuickQuiz();
        byte[] rejected = (byte[])QuickSuccess.Clone();
        rejected[1] = 1;
        ReplyQuickQuiz(rejected, true);
        Progress(12, true, true);
        model.Capture(false);
        Check(model.Unresolved && (string)OperationField("reason") == "daily_timed_quiz_quick_result_unconfirmed",
            "real operate Error plus apparent progress cannot settle");

        Reset(); StartQuickQuiz();
        ReplyQuickQuiz(new byte[] { 8, 0, 18, 0 }, true);
        model.Capture(false);
        Check(model.Unresolved && (string)OperationField("reason") == "daily_timed_quiz_quick_response_schema",
            "quick OK without required account quiz fields is malformed");

        Reset(); StartQuickQuiz();
        ReplyQuickQuiz(QuickSuccess, false); Progress(12, true, true);
        model.Capture(false);
        Check(model.Unresolved && !Captures[1829].ReturnedForDispatch(1), "a quick receipt does not substitute for callback return");

        Reset(); OpenQuickQuiz();
        Check(Queue(12, false, role.id, Operation, out why), "queue before premium snapshot changes");
        onActivityInstall = delegate { role.gold--; };
        Check(!ExecuteGui(delegate { }, out why) && why == "daily_timed_premium_balance_changed_before_call" &&
            Model.QuizUi.QuickRequests == 0 && Model.QuizUi.interactionEnabled, "paid balance change immediately before send blocks 1829");

        Reset(); OpenQuickQuiz();
        Check(Queue(12, false, role.id, Operation, out why), "queue before daily progress changes");
        onActivityInstall = delegate { Row(12, 1, ((Activity)role.acts[12u]).temp); };
        Check(!ExecuteGui(delegate { }, out why) && why == "daily_timed_counter_changed_before_call" &&
            Model.QuizUi.QuickRequests == 0, "daily progress just before send blocks 1829");

        Reset(); StartQuickQuiz();
        ReplyQuickQuiz(QuickSuccess, true); Progress(12, true, true);
        role.bind_gold--;
        model.Capture(false);
        Check(model.Unresolved && (string)OperationField("reason") == "daily_timed_premium_balance_changed" &&
            !Queue(12, false, role.id, Other, out why), "nonzero bind_gold delta stops the run");

        Reset(); StartQuickQuiz();
        ReplyQuickQuiz(QuickSuccess, true); Progress(12, true, true);
        role.gold++;
        model.Capture(false);
        Check(model.Unresolved, "positive gold delta is also an ambiguous request outcome");

        Reset(); StartQuickQuiz();
        ReplyQuickQuiz(QuickSuccess, true); Progress(12, true, true);
        EnterNextCycle(true);
        model.Capture(false);
        Check(model.Unresolved && (string)OperationField("reason") == "daily_timed_refresh_cycle_changed",
            "quick evidence from a different cycle cannot settle");

        Reset(); StartQuickQuiz();
        ReplyQuickQuiz(QuickSuccess, true); Progress(12, true, true);
        Global.m_client.m_stream = new object();
        model.Capture(false);
        Check(model.Unresolved && (string)OperationField("reason") == "daily_timed_character_or_client_changed",
            "replaced stream on the same client and socket cannot settle the quick request");
    }

    private static void StartQuickRuntime()
    {
        StartQuickQuiz();
        Model.QuizUi.QuickStartsRuntime = true;
        ReplyQuickQuiz(QuickWithRuntime, true);
        Model.QuizUi.DrawInQuiz();
        Check(Model.QuizUi.inquiz_phase == 1 && role.quiz.info != null && Model.QuizUi.interactionEnabled,
            "native quick runtime triggers DrawInQuiz and prep_question");
    }

    private static void CheckQuickRuntime()
    {
        string why;
        for (int phase = 1; phase <= 2; phase++)
        {
            Reset(); StartQuickRuntime();
            model.Capture(false);
            Check((string)OperationField("status") == "PENDING" && (string)TaskField(12, "action") == "",
                "runtime is owned before 355 but offers no second request");
            if (phase == 2) Model.QuizUi.FinishQuizFadeIn();
            Progress(12, true, true);
            model.Capture(false);
            Check(Model.QuizUi.inquiz_phase == phase && (string)OperationField("status") == "SUCCEEDED" &&
                (int)OperationField("counter_after") == 1 && (string)OperationField("request_attribution") == "UNKNOWN",
                "stock prep_question/fade before 355 can confirm account progress without request attribution");
        }

        Reset(); StartQuickRuntime();
        Progress(12, true, true);
        Global.character_ = new Role { id = role.id + 1 };
        model.Capture(false);
        Check(model.Unresolved && OperationField("counter_after") == null, "wrong character cannot settle quick runtime");

        Reset(); StartQuickRuntime();
        model.Capture(false);
        QuizUI prior = Model.QuizUi;
        Model.QuizUi = new QuizUI { rmg = prior.rmg, myquiz = role.quiz, inquiz_phase = 2, end_phase = 0 }.Attach<QuizUI>(Model.Host);
        MainUI.m_subui = Model.QuizUi;
        Progress(12, true, true);
        model.Capture(false);
        Check(model.Unresolved && (string)OperationField("reason") == "daily_timed_participation_view_changed",
            "replacement QuizUI with matching projection is not the owned panel");

        Reset(); StartQuickRuntime();
        role.quiz.info.questions[1] = 99u;
        Progress(12, true, true);
        model.Capture(false);
        Check((string)OperationField("status") == "PENDING" && OperationField("counter_after") == null,
            "matching current question and length cannot substitute for the complete runtime projection");

        Reset(); StartQuickQuiz();
        Model.QuizUi.QuickStartsRuntime = true;
        byte[] incomplete = { 8, 0, 18, 12, 10, 0, 18, 4, 8, 5, 56, 0, 24, 4, 40, 0 };
        ReplyQuickQuiz(incomplete, true);
        Model.QuizUi.DrawInQuiz();
        Progress(12, true, true);
        model.Capture(false);
        Check(model.Unresolved && (string)OperationField("reason") == "daily_timed_quiz_quick_response_schema",
            "incomplete required protobuf runtime cannot settle even when the fixture applies it");

        Reset(); OpenQuickQuiz();
        Check(Queue(12, false, role.id, Operation, out why), "queue before pause");
        Check(!ExecuteGui(delegate { model.Capture(true); }, out why) && Model.QuizUi.QuickRequests == 0,
            "controller pause after publication prevents quick send");

        Reset(); StartQuickRuntime();
        model.Stop("user_pause");
        Progress(12, true, true);
        model.Capture(false);
        Check(model.Unresolved && (string)OperationField("reason") == "user_pause" && OperationField("counter_after") == null &&
            !Captures[355].Read().Armed && !Queue(12, false, role.id, Other, out why),
            "a normal late 355 after a user stop cannot settle or replay");

        Reset(); StartQuickRuntime();
        role.bind_gold--;
        Progress(12, true, true);
        model.Capture(false);
        Check(model.Unresolved && (string)OperationField("reason") == "daily_timed_premium_balance_changed",
            "normal runtime transition does not bypass premium delta detection");
    }

    private static void CheckBrawl()
    {
        string why;
        Reset();
        role.brawlinfo.joined = true;
        model.Capture(false);
        Check(!Queue(13, true, role.id, Operation, out why) && why == "brawl_already_joined", "existing join prevents a request");
        Reset();
        Row(13, 1, new ActivityTemplate { id = 13, cond_param = 2 });
        model.Capture(false);
        Check(!Queue(13, true, role.id, Operation, out why) && why == "brawl_progress_already_present", "existing partial progress prevents a request");

        Reset(); OpenBrawl();
        Check(Queue(13, false, role.id, Operation, out why), "queue brawl before prior manual reply");
        Check(!ExecuteGui(delegate { role.brawlinfo.joined = true; }, out why) && why == "brawl_already_joined" &&
            Model.BrawlUi.Requests == 0 && !Captures.ContainsKey(322), "a join observed before invocation prevents another send");

        Reset();
        // Represent a manual 321 from before the assistant run. It can be
        // outstanding while the role is still unjoined and the inbound queue is empty.
        Model.BrawlUi.ManualJoin();
        Check(Global.m_client.messages.Count == 0 && !Global.m_client.m_handlers.Contains((ushort)321), "old manual request is locally invisible");
        StartBrawl();
        Progress(13, true, true);
        Join(role.id + 1, role.SID, false, true);
        Join(role.id, role.SID + 1, false, true);
        model.Capture(false);
        Check((string)OperationField("status") == "PENDING", "other role or server cannot satisfy brawl");
        Join(role.id, role.SID, true, true);
        Join(role.id + 1, role.SID, false, true);
        Progress(12, true, true);
        model.Capture(false);
        Check((string)OperationField("status") == "SUCCEEDED" && (int)OperationField("counter_after") == 1 &&
            (string)OperationField("reason") == "account_progress_observed_request_attribution_unknown" &&
            (string)OperationField("request_attribution") == "UNKNOWN" &&
            Captures[322].ReturnedForDispatch(3) && Captures[322].ReturnedForDispatch(4) &&
            Captures[355].ReturnedForDispatch(1) && Captures[355].ReturnedForDispatch(2),
            "matching returns survive later unrelated broadcasts and can complete only the account goal");
        Check(!Captures[322].Read().Armed && !Captures[355].Read().Armed && Model.Current == Model.Brawl &&
            Model.BrawlUi.enabled && Model.BrawlUi.Requests == 2, "preserve the native page and detach observers after one automated send");

        Reset(); StartBrawl();
        Progress(13, true, true);
        Join(role.id, role.SID, true, false);
        Join(role.id + 1, role.SID, false, true);
        model.Capture(false);
        Check(model.Unresolved && !Captures[322].ReturnedForDispatch(1) && Captures[322].ReturnedForDispatch(2),
            "a later foreign return cannot qualify the own broadcast");
        model.Stop("test_stop");
        Check(model.Unresolved && !Queue(13, false, role.id, Other, out why) && Model.BrawlUi.Requests == 1 &&
            !Captures[322].Read().Armed && !Captures[355].Read().Armed, "ambiguous brawl cannot retry and leaves callbacks restored");

        Reset(); StartBrawl();
        for (int i = 0; i < 34; i++) Join(role.id + 1, role.SID, false, true);
        model.Capture(false);
        Check(model.Unresolved && Captures[322].Read().Calls == 33 && Captures[322].ReturnedForDispatch(32) &&
            !Captures[322].ReturnedForDispatch(33), "persistent capture stops observing after the bound but forwards every broadcast");

        Reset(); StartBrawl();
        Join(role.id, role.SID, true, true);
        Progress(13, false, true);
        model.Capture(false);
        Check((string)OperationField("status") == "PENDING", "joined without applied 355 is not confirmed");
        typeof(DailyTimedModel).GetField("tick", BindingFlags.NonPublic | BindingFlags.Instance)
            .SetValue(model, unchecked(Environment.TickCount - 18001));
        model.Capture(false);
        Check(model.Unresolved && Model.BrawlUi.Requests == 1 && !Queue(13, false, role.id, Other, out why),
            "elapsed participation expiry cannot retry 321");

        Reset(); StartBrawl();
        Join(role.id, role.SID, true, true); Progress(13, true, true);
        EnterNextCycle(true);
        model.Capture(false);
        Check(model.Unresolved && (string)OperationField("reason") == "daily_timed_refresh_cycle_changed" &&
            Model.BrawlUi.Requests == 1 && OperationField("counter_after") == null, "old-cycle account evidence cannot complete in the new cycle");

        Reset();
        Model.BrawlUi.OpenNative();
        model.Capture(false);
        Check(!Queue(13, true, role.id, Operation, out why) && why == "daily_timed_network_not_idle" &&
            Model.RootUi.BrawlEnters == 0, "a preexisting 323 handler is not an opener witness");

        Reset();
        Model.RootUi.DelayBrawlRefresh = true;
        QueueAndRun(13, true);
        BrawlInfo.nexttime = 123;
        model.Capture(false);
        Check(!Captures.ContainsKey(323) && (string)OperationField("status") == "OPENING" &&
            !Queue(13, false, role.id, Operation, out why) && Model.BrawlUi.Requests == 0, "cached nonzero nexttime cannot establish readiness");
        Model.BrawlUi.OpenNative();
        Model.BrawlUi.collecting = true;
        model.Capture(false);
        Check(Captures.ContainsKey(323), "bind a later normal Show callback during asset completion");
        ReplyBrawlRefresh();
        model.Capture(false);
        Check((string)OperationField("status") == "OPENING", "a verified reply does not bypass page loading");
        Model.BrawlUi.collecting = false;
        Model.BrawlUi.interactionEnabled = false;
        model.Capture(false);
        Check((string)OperationField("status") == "OPENING", "a verified reply does not bypass native input readiness");
        Model.BrawlUi.interactionEnabled = true;
        model.Capture(false);
        Check((string)OperationField("status") == "READY", "finish after exact reply and asset completion");

        Reset();
        Model.RootUi.DelayBrawlRefresh = true;
        QueueAndRun(13, true);
        Model.BrawlUi.OpenNative();
        Global.m_client.m_handlers.Remove((ushort)323);
        BrawlInfo.nexttime = 456;
        model.Capture(false);
        Check(!Captures.ContainsKey(323) && (string)OperationField("status") == "OPENING" && Model.BrawlUi.Requests == 0,
            "a reply dispatched before the observer leaves readiness unproven");

        Reset(); QueueAndRun(13, true);
        Dispatch(323, null, delegate { }, true);
        model.Capture(false);
        Check(model.Unresolved && Model.BrawlUi.Requests == 0, "null refresh response never enables 321");

        Reset(); QueueAndRun(13, true);
        ReplyBrawlRefresh();
        BrawlInfo.nexttime = 123;
        model.Capture(false);
        Check(model.Unresolved && (string)OperationField("reason") == "daily_timed_brawl_refresh_projection_mismatch",
            "a successful callback with a changed projection fails closed");

        Reset();
        role.brawl.hasBrawl = false;
        model.Capture(false);
        QueueAndRun(13, true);
        Check(!Captures.ContainsKey(323) && (string)OperationField("status") == "OPENING" &&
            Global.m_client.m_handlers.Contains((ushort)319), "new brawl record uses stock 319");
        role.brawl.hasBrawl = true;
        Global.m_client.m_handlers.Remove((ushort)319);
        model.Capture(false);
        Check((string)OperationField("status") == "READY", "stock 319 projects a new record before readiness");
    }
}
