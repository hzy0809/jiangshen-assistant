using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;
using JiangShen.AssistantRuntime;
using UnityEngine;
using Player = game.net.data.StoC.CharacterDummyChallenge;

internal static class DailyWoodProbeTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly string Nonce = Guid.NewGuid().ToString("N"), Other = Guid.NewGuid().ToString("N");
    private static readonly string Operation = Guid.NewGuid().ToString("N");
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static int sequence, checks;

    internal static UnityEngine.Object[] AllUi()
    { return new UnityEngine.Object[] { BaseUI.rootUI, MainUI.Current, MainUI.Panel, MainUI.Panel.Battle, MainUI.Panel.Battle.Result }; }

    internal static Snapshot Snapshot()
    {
        string gate = BaseAsset.lds.enabled ? "loading_or_battle" : !Global.extern_enabled ? "global_input_disabled" :
            MainUI.m_subui != null ? "subui_present" : "PASS";
        return new Snapshot { Main = MainUI.Current, Root = BaseAsset.lds.currmg, Character = Global.character_,
            Gate = gate, Safe = gate == "PASS", InteractionEnabled = true };
    }

    private static void SetUp()
    {
        RootUI scene = new RootUI();
        scene.rmg = scene;
        BaseUI.rootUI = new RootUI { level_rmg = scene };
        BaseUI.rootUI.rmg = BaseUI.rootUI;
        BaseAsset.lds = new LoadScreen { currmg = scene, enabled = false };
        MainUI.Current = new MainUI { rmg = scene, gameObject = scene.gameObject };
        GameObject battleHost = new GameObject();
        BattleWoodwarResultUI result = new BattleWoodwarResultUI { gameObject = battleHost, enabled = false };
        BattleUI battle = new BattleUI { gameObject = battleHost, Result = result, enabled = false };
        battleHost.Components.Add(battle); battleHost.Components.Add(result);
        GameObject.Paths["Battle/BattleUI"] = battleHost;
        MainUI.Panel = new WoodWarUI(battle) { rmg = BaseUI.rootUI, gameObject = BaseUI.rootUI.gameObject, enabled = false };
        BaseUI.rootUI.gameObject.Components.Add(MainUI.Panel);
        RootUI.m_client = Global.m_client = new game.net.tcp_client();
        RootBase.Install();
        Global.character_ = new Database.Character();
        Global.character_.act_rewards[3u] = new Database.Activity_Reward();
        RootBase.SetProgress(0, 0);
    }

    private static string Bootstrap(string nonce)
    {
        string path = Path.Combine(Entry.SessionsRoot, nonce);
        Directory.CreateDirectory(path);
        return Entry.Bootstrap(path, nonce, Entry.GetCurrentThreadId());
    }
    private static string PathFor(string file) { return Path.Combine(Entry.SessionsRoot, Nonce, file); }
    private static Dictionary<string, object> Read(string file) { return Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(PathFor(file))); }
    private static Dictionary<string, object> Dict(object value) { return (Dictionary<string, object>)value; }
    private static Dictionary<string, object> State() { return Read("state.json"); }
    private static Dictionary<string, object> Ack() { return Read("ack.json"); }
    private static Dictionary<string, object> Result() { return Dict(Ack()["result"]); }
    private static Dictionary<string, object> Wood() { return Dict(State()["daily_wood"]); }
    private static bool Busy() { return (bool)Dict(State()["business"])["pending_daily_wood"]; }
    private static Probe Probe { get { return GameObject.LastHelper.Probe; } }
    private static void Invoke(string method) { typeof(Probe).GetMethod(method, Private).Invoke(Probe, null); }
    private static void Update() { Time.frameCount++; Invoke("Update"); }
    private static void Gui() { Invoke("OnGUI"); Invoke("OnGUI"); }
    private static void Publish(string wire) { File.WriteAllText(PathFor("command.txt"), wire); }
    private static string Wire(int seq, string name) { return "version=3\nnonce=" + Nonce + "\nsequence=" + seq + "\ncommand=" + name + "\n"; }
    private static string ActionWire(int seq, string action, int revision, string role = "45")
    {
        return Wire(seq, "daily-wood-command") + "action=" + action + "\noperation_id=" + Operation +
            "\ncharacter_id=" + role + "\ntask_id=22\nexpected_revision=" + revision + "\n";
    }
    private static void Send(string command) { Publish(Wire(++sequence, command)); Update(); }
    private static void KeepReceipt(string label)
    {
        foreach (string name in new[] { "state.json", "ack.json" })
            File.Copy(PathFor(name), PathFor(label + "-" + name), true);
    }
    private static bool Status(Dictionary<string, object> record, string value) { return (string)record["status"] == value; }
    private static bool GuiIs(Dictionary<string, object> parent, string phase, bool invoked)
    {
        Dictionary<string, object> gui = Dict(parent["campaign_gui"]);
        return (int)gui["command_seq"] == sequence && (string)gui["phase"] == phase && (bool)gui["invocation_attempted"] == invoked;
    }
    private static void Check(bool ok, string what)
    {
        if (!ok) throw new Exception(what + "\n" + (File.Exists(PathFor("state.json")) ? File.ReadAllText(PathFor("state.json")) : "") +
            "\n" + (File.Exists(PathFor("ack.json")) ? File.ReadAllText(PathFor("ack.json")) : ""));
        checks++;
    }

    private static void Grammar()
    {
        foreach (string action in new[] { "open", "fast", "close_result", "close_panel" })
        {
            Command cmd = Command.Parse(ActionWire(1, action, 7, UInt64.MaxValue.ToString()), Nonce, 0);
            Check(cmd != null && cmd.DailyWoodTaskId == 22 && cmd.DailyWoodCharacterId == UInt64.MaxValue &&
                cmd.DailyWoodAction == action && cmd.OperationId == Operation && cmd.ExpectedRevision == 7, "exact wood grammar");
        }
        string valid = ActionWire(1, "fast", 7);
        foreach (string bad in new[] { valid.Replace("action=fast", "action=reward"), valid.Replace("task_id=22", "task_id=21"),
            valid.Replace("character_id=45", "character_id=045"), valid.Replace("expected_revision=7", "expected_revision=0"),
            valid.Replace(Operation, Operation.ToUpperInvariant()), valid.Replace(Nonce, Other), valid + "opcode=616\n" })
            Check(Command.Parse(bad, Nonce, 0) == null, "wood grammar rejects unsupported scope");
    }

    private static void Echo(string action, int revision, bool invoked)
    {
        Dictionary<string, object> echo = Dict(Result()["daily_wood_command"]);
        Check((int)echo["command_seq"] == sequence && (string)echo["action"] == action &&
            (string)echo["operation_id"] == Operation && (string)echo["character_id"] == "45" &&
            (int)echo["task_id"] == 22 && (int)echo["expected_revision"] == revision &&
            (bool)echo["invocation_attempted"] == invoked, "exact command echo " + action);
    }

    private static string Queue(string action, out int revision)
    {
        Check((string)Wood()["next_action"] == action, "native offer " + action);
        revision = (int)Wood()["revision"];
        string wire = ActionWire(++sequence, action, revision);
        Publish(wire); Update();
        Check(Status(Ack(), "ACCEPTED") && GuiIs(State(), "queued", false) && Busy(), "queue " + action);
        Echo(action, revision, false);
        return wire;
    }

    private static void Step(string action)
    {
        int revision;
        Queue(action, out revision); Gui();
        Check(Status(Ack(), "COMPLETED") && GuiIs(Result(), "completed", true), "invocation receipt " + action);
        Echo(action, revision, true);
        KeepReceipt(action);
    }

    private static void Finish(bool reconnect, bool forced = false)
    {
        if (!forced && !GameObject.LastHelper.PendingDestroy) Send("stop");
        Check(forced || GameObject.LastHelper.PendingDestroy, "clean Stop schedules only own helper destruction");
        Invoke("OnDestroy");
        Check((bool)State()["component_destroyed"] && (bool)State()["reconnect_allowed"] == reconnect, "reconnect reflects outcome");
        if (reconnect) Check(Bootstrap(Other).StartsWith("BOOTSTRAP_OK;"), "clean session permits later attachment");
        else Check(File.ReadAllText(PathFor("recovery_unknown.json")).Contains("daily_wood_request_or_cleanup_unresolved_restart_required") &&
            Bootstrap(Other).StartsWith("BOOTSTRAP_REJECTED;recovery_unknown"), "unknown retains process fence");
    }

    private static void Disabled()
    {
        Send("daily-wood-observe");
        Check(Status(Ack(), "REJECTED") && !(bool)Result()["daily_wood_enabled"] && Result()["daily_wood"] == null, "default disabled observe");
        foreach (string action in new[] { "open", "fast", "close_result", "close_panel" })
        {
            Publish(ActionWire(++sequence, action, 1)); Update(); Gui();
            Check(Status(Ack(), "REJECTED") && (string)Ack()["reason"] == "daily_wood_disabled" &&
                !(bool)State()["daily_wood_enabled"] && State()["daily_wood"] == null && GuiIs(Result(), "none", false) && !Busy(),
                "default disabled action " + action);
            Echo(action, 1, false);
        }
        Check(MainUI.Opens == 0 && MainUI.Closes == 0 && MainUI.Panel.FastCalls == 0, "default never invokes stock");
        Finish(true);
    }

    private static void Ready()
    {
        Send("daily-wood-observe");
        Check(Status(Ack(), "COMPLETED") && (bool)State()["daily_wood_enabled"] && (bool)Result()["daily_wood_enabled"] &&
            !(bool)State()["daily_refine_enabled"] && !(bool)State()["daily_soul_enabled"] &&
            Wood()["refresh_cycle"] == null && Wood()["fast_quota"] == null, "isolated opt-in requires clock proof");
        KeepReceipt("observe");
        Check(DailyRefreshProof.RecordSnapshot(Global.character_, DailyTransport.Read(Global.m_client), 218000, 304400), "fixture clock proof");
        Update();
        Check((int)Wood()["refresh_cycle"] == 218000 && (int)Wood()["quota_epoch"] == 185600, "both clock fields emitted");
        Publish(ActionWire(++sequence, "open", (int)Wood()["revision"], "46")); Update(); Gui();
        Check(Status(Ack(), "REJECTED") && !Busy(), "wrong role rejected");
        Publish(ActionWire(++sequence, "open", (int)Wood()["revision"] + 1)); Update(); Gui();
        Check(Status(Ack(), "REJECTED") && !Busy(), "wrong revision rejected");
    }

    private static void Open()
    {
        int ownedSamples = 0;
        Global.BeforeTemplate = delegate
        {
            if (MainUI.Opens == 0 && GuiIs(State(), "owned", false)) ownedSamples++;
        };
        Step("open");
        Global.BeforeTemplate = null;
        Check(ownedSamples == 2, "prepare and one final stock check precede invocation");
        Check(Status(Wood(), "OPENING") && MainUI.Opens == 1 && !((bool)Wood()["mutation_attempted"]), "612 is read-only and pending");
        Global.m_client.Deliver(612, DailyWoodTests.InfoPacket(1), true); Update();
        Check(Status(Wood(), "READY") && (int)Wood()["fast_quota"] == 1 && Busy(), "owned info enables exactly one fast route");
    }

    private static void Success()
    {
        Open();
        Send("disable-restore"); Gui();
        Check(Status(Ack(), "REJECTED") && (string)Ack()["reason"] == "daily_wood_operation_owned", "other GUI blocked by wood");
        Step("fast");
        Check(Status(Wood(), "PENDING") && MainUI.Panel.FastCalls == 1 && (bool)Wood()["mutation_attempted"], "616 admitted once");
        Global.m_client.Deliver(616, DailyWoodTests.FastPacket(1), true); Update();
        Check(Status(Wood(), "PENDING") && !(bool)Wood()["task_confirmed"], "616 without 355 is pending");
        Global.m_client.Deliver(355, new byte[] { 10, 6, 8, 22, 16, 1, 24, 34 }, false); Update();
        Check(Status(Wood(), "RESULT") && (bool)Wood()["task_confirmed"] && (int)Wood()["fast_quota"] == 0 &&
            Global.m_client.HandlerFor(355).handler.Method.Name == "handelActvity", "verified progress and restored push");
        Step("close_result"); Step("close_panel");
        Check(Status(Wood(), "COMPLETED") && (string)Wood()["operation_id"] == Operation && !Busy() &&
            MainUI.Panel.ResultCloses == 1 && MainUI.Closes == 1 && MainUI.Panel.FastCalls == 1, "final home required");
        Finish(true);
    }

    private static void FinalGate(string scenario)
    {
        int revision;
        string wire = Queue("open", out revision);
        int samples = 0;
        int cutover = scenario == "commit-command" || scenario == "commit-stop" ? 1 : 2;
        Global.BeforeTemplate = delegate
        {
            if (!GuiIs(State(), "owned", false) || ++samples != cutover) return;
            Global.BeforeTemplate = null;
            if (scenario == "commit-command") Publish(wire.Replace("action=open", "action=fast"));
            else if (scenario == "commit-stop") Publish(Wire(sequence + 1, "stop"));
            else if (scenario == "commit-connection") Global.m_client.m_bConnectLost = true;
            else if (scenario == "commit-stream") Global.m_client.m_stream = new MemoryStream();
            else if (scenario == "commit-gui") GUI.enabled = false;
            else if (scenario == "commit-role") Global.character_ = new Database.Character();
            else if (scenario == "commit-quota-epoch") TimeUtil._now = TimeUtil._thisDay + 72000;
            else if (scenario == "commit-lease")
            {
                object life = typeof(Probe).GetField("life", Private).GetValue(Probe);
                typeof(SessionLifecycle).GetField("touched", Private).SetValue(life, unchecked((uint)(Environment.TickCount - SessionLifecycle.LeaseMilliseconds)));
            }
        };
        Gui();
        Check(samples == cutover && MainUI.Opens == 0 && !Busy(), "no stock branch at final gate");
        if (scenario != "commit-stop")
        {
            Check(Status(Ack(), "REJECTED") && GuiIs(Result(), "not_invoked", false), "final gate refusal receipt");
            Echo("open", revision, false);
        }
        Finish(true);
    }

    private static void Unknown(string scenario)
    {
        Open();
        if (scenario == "missing-handler")
        {
            MainUI.Panel.RegisterFast = false;
            int revision;
            Queue("fast", out revision); Gui();
            Check(Status(Ack(), "UNKNOWN") && GuiIs(Result(), "unknown", true) && MainUI.Panel.FastCalls == 1, "invoked but exact callback missing");
            Echo("fast", revision, true);
        }
        else Step("fast");
        if (scenario != "destroy")
        {
            Send("stop");
            Check(!GameObject.LastHelper.PendingDestroy && Busy() && Status(Wood(), "UNKNOWN") && (bool)Wood()["cleanup_pending"] &&
                MainUI.Closes == 0 && MainUI.Panel.ResultCloses == 0, "Stop holds own helper until UI and callbacks clear");
            if (scenario == "stop")
            {
                Global.m_client.Deliver(616, DailyWoodTests.FastPacket(1), true);
                Check(MainUI.Panel.Battle.Result.enabled && Global.m_client.HandlerFor(355).handler.Method.Name == "handelActvity",
                    "late stock callback forwarded with tap detached");
                MainUI.Panel.Battle.Result.Hide();
                MainUI.SetSubUI(null);
                Update();
                Check(GameObject.LastHelper.PendingDestroy && !((bool)Wood()["cleanup_pending"]) && Status(Wood(), "UNKNOWN"),
                    "manual stock cleanup clears UI hold and preserves unknown business fence");
            }
        }
        Finish(false, !GameObject.LastHelper.PendingDestroy);
    }

    public static int Main(string[] args)
    {
        try
        {
            if (args[0] == "model") return DailyWoodTests.Main();
            SetUp(); Grammar();
            Check(Bootstrap(Nonce).StartsWith("BOOTSTRAP_OK;"), "bootstrap"); Update(); Update();
            if (args[0] == "disabled") Disabled();
            else
            {
                Ready();
                if (args[0] == "success") Success();
                else if (args[0].StartsWith("commit-")) FinalGate(args[0]);
                else Unknown(args[0]);
            }
            Console.WriteLine("{\"daily_wood_probe\":" + Command.Quote(args[0]) + ",\"assertions\":" + checks +
                ",\"session_dir\":" + Command.Quote(Path.Combine(Entry.SessionsRoot, Nonce)) + "}");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}

public class __BaseUI : Behaviour
{
    public Component rmg;
    public DateTime visible_time;
    public bool loading, collecting, first;
    public bool IsVisible() { return enabled; }
    public bool IsEnable() { return true; }
    public void Show() { loading = false; enabled = true; visible_time = DateTime.UtcNow; }
    public void Hide() { enabled = false; }
}
public class BaseUI : __BaseUI { public static RootUI rootUI; }
public static class BaseAsset { public static LoadScreen lds; }
public sealed class LoadScreen : Behaviour { public Component currmg; public bool isBattling; }
public static class TimeUtil { public static uint _now = 220000, _thisDay = 200000; }
public static class BuildingBuildUI { public static bool m_bBuildVisible; }
public static class PopupUI { public static bool popup; }
public static class BuildingBehaviourScript { public static bool m_bEditVisible; }
public sealed class RootUI : BaseUI
{
    public static game.net.tcp_client m_client;
    public static bool m_bRefreshFive;
    public string LevelID = "Capital";
    public Component level_rmg;
}
public sealed class MainUI : BaseUI
{
    public static MainUI Current;
    public static WoodWarUI Panel;
    public static __BaseUI m_subui;
    public static int Opens, Closes;
    public bool m_bInit = true;
    public static void _muren() { Opens++; m_subui = Panel; Panel.RequestInfo(); }
    public static void SetSubUI(__BaseUI ui) { Closes++; m_subui.Hide(); m_subui = ui; }
}
public sealed class BattleUI : BaseUI { public BattleWoodwarResultUI Result; public bool bBattle_fighting; }
public sealed class BattleWoodwarResultUI : BaseUI
{
    public Player resultStatus;
    public Action close_func;
    public bool resultChallengeLevelFinished, used_all_turns;
    public int total_lose_num;
}
public sealed class WoodWarUI : BaseUI
{
    public readonly BattleUI m_battleUI;
    public int temptime, FastCalls, ResultCloses;
    public bool RegisterFast = true;
    public BattleUI Battle { get { return m_battleUI; } }
    public WoodWarUI(BattleUI battle) { m_battleUI = battle; }
    public void RequestInfo() { BaseAsset.lds.enabled = true; loading = true; Global.m_client.Put(612, sc_dummy_challenge_info); }
    private void sc_dummy_challenge_info(MemoryStream stream, object arg)
    {
        Global.character_.dummy_challenge_status = new DummyStatus(new Player(1, false, 7));
        temptime = 9; BaseAsset.lds.enabled = false; Show();
    }
    private void cs_dummy_challenge_fast_complete()
    { FastCalls++; if (RegisterFast) Global.m_client.Put(616, sc_dummy_challenge_fast_complete); }
    private void sc_dummy_challenge_fast_complete(MemoryStream stream, object arg)
    {
        Global.character_.dummy_challenge_status = new DummyStatus(new Player(0, true, 7));
        Battle.Result.resultStatus = new Player(0, false, 0);
        Battle.Result.close_func = _fast_finish_close;
        Battle.Result.Show();
    }
    private void _fast_finish_close() { ResultCloses++; }
}
public sealed class DummyStatus
{
    public readonly Player _player;
    public DummyStatus(Player player) { _player = player; }
}
namespace game.net.data.StoC
{
    public sealed class CharacterDummyChallenge
    {
        public uint _rank = 1, _challenge_level = 2, _wave_count = 3, _enemy_count = 4,
            _total_damage = 5, _next_challenge_level = 6, _level = 7, _fast_challenge_left_times, _sid;
        public bool _can_take;
        public string _name = "fixture";
        public CharacterDummyChallenge(uint quota, bool canTake, uint sid) { _fast_challenge_left_times = quota; _can_take = canTake; _sid = sid; }
    }
}
namespace Database
{
    public sealed class Character
    {
        public ulong id = 45;
        public uint SID = 7, next_resource_time = 304400, gold_consume = 3;
        public int gold = 40, bind_gold = 8;
        public bool gm;
        public string name = "fixture";
        public DummyStatus dummy_challenge_status;
        public readonly Hashtable acts = new Hashtable(), act_rewards = new Hashtable();
    }
    public sealed class ActivityTemplate { public byte id = 22, idreward = 3; public bool active = true; public int cond_param = 1; }
    public sealed class Activity { public byte idactivity = 22, condition_param; public int refresh_time; public ActivityTemplate temp = Global.Template; }
    public sealed class Activity_Reward { public byte idact_reward = 3; public readonly Hashtable acts = new Hashtable(); }
}
public static class Global
{
    public static Database.Character character_;
    public static game.net.tcp_client m_client;
    public static bool extern_enabled = true, relogin;
    public static readonly Database.ActivityTemplate Template = new Database.ActivityTemplate();
    public static Action BeforeTemplate;
    public static object getTemplate(string name, uint id)
    { if (BeforeTemplate != null) BeforeTemplate(); return name == "activity" && id == 22 ? Template : null; }
}
public static class RootBase
{
    public static void Install() { Global.m_client.Put(355, handelActvity).life = -1f; }
    private static void handelActvity(MemoryStream stream, object arg) { SetProgress(1, 17); }
    public static void SetProgress(byte value, int refresh)
    {
        Database.Activity row = new Database.Activity { condition_param = value, refresh_time = refresh };
        ((Database.Activity_Reward)Global.character_.act_rewards[3u]).acts[22u] = Global.character_.acts[22u] = row;
    }
}
namespace game.net
{
    public class tcp_base
    {
        public delegate void delegate_handler(MemoryStream stream, object arg);
        public sealed class Handler { public delegate_handler handler; public object arg; public float life = 10f; }
        public static int connection_state = 3;
        public bool m_bConnectLost;
        public readonly Hashtable m_handlers = new Hashtable();
        public readonly ArrayList messages = new ArrayList();
    }
    public sealed class tcp_client : tcp_base
    {
        public sealed class Socket { public bool Connected { get { return true; } } }
        public Socket m_client = new Socket();
        public Stream m_stream = new MemoryStream();
        public readonly ArrayList m_sendDatas = new ArrayList();
        public Handler Put(ushort opcode, delegate_handler callback)
        { Handler h = new Handler { handler = callback }; m_handlers[opcode] = h; return h; }
        public Handler HandlerFor(ushort opcode) { return (Handler)m_handlers[opcode]; }
        public void Deliver(ushort opcode, byte[] bytes, bool remove)
        {
            lock (this)
            {
                Handler handler = HandlerFor(opcode);
                if (remove) m_handlers.Remove(opcode);
                handler.handler(new MemoryStream(bytes, false), handler.arg);
            }
        }
    }
}
