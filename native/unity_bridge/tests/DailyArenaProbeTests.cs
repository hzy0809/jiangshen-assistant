using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;
using JiangShen.AssistantRuntime;
using UnityEngine;

internal static class DailyArenaProbeTests
{
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly string Nonce = Guid.NewGuid().ToString("N"), OtherNonce = Guid.NewGuid().ToString("N");
    private static readonly byte[] Free = { 8, 0, 16, 0x80, 0xb7, 0x41 };
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static int sequence, checks;

    private sealed class CapabilityStream : MemoryStream
    {
        internal bool Readable = true, Writable = true;
        internal Action BeforeCanRead;
        public override bool CanRead { get { if (BeforeCanRead != null) BeforeCanRead(); return Readable && base.CanRead; } }
        public override bool CanWrite { get { return Writable && base.CanWrite; } }
    }

    private static string Bootstrap(string nonce)
    {
        string path = Path.Combine(Entry.SessionsRoot, nonce);
        Directory.CreateDirectory(path);
        return Entry.Bootstrap(path, nonce, Entry.GetCurrentThreadId());
    }
    private static string PathFor(string file) { return Path.Combine(Entry.SessionsRoot, Nonce, file); }
    private static string Read(string file) { return File.ReadAllText(PathFor(file)); }
    private static Dictionary<string, object> Record(string file) { return Json.Deserialize<Dictionary<string, object>>(Read(file)); }
    private static Dictionary<string, object> Dict(object value) { return (Dictionary<string, object>)value; }
    private static Dictionary<string, object> State() { return Record("state.json"); }
    private static Dictionary<string, object> Arena() { return Dict(State()["daily_arena"]); }
    private static Dictionary<string, object> Ack() { return Record("ack.json"); }
    private static Dictionary<string, object> AckResult() { return Dict(Ack()["result"]); }
    private static string Wire(int seq, string name) { return "version=3\nnonce=" + Nonce + "\nsequence=" + seq + "\ncommand=" + name + "\n"; }
    private static string ActionWire(int seq, string action, int rev, string operation = ArenaFixture.Id, string role = "1701")
    { return Wire(seq, "daily-arena-command") + "action=" + action + "\noperation_id=" + operation + "\ncharacter_id=" + role + "\nexpected_revision=" + rev + "\n"; }
    private static void Invoke(string name) { typeof(Probe).GetMethod(name, PrivateInstance).Invoke(GameObject.LastHelper.Probe, null); }
    private static object ProbeField(string name) { return typeof(Probe).GetField(name, PrivateInstance).GetValue(GameObject.LastHelper.Probe); }
    private static void Update() { Time.frameCount++; Invoke("Update"); }
    private static void Publish(string wire) { File.WriteAllText(PathFor("command.txt"), wire); }
    private static void Send(string name, string payload = "") { Publish(Wire(++sequence, name) + payload); Update(); }
    private static bool Status(Dictionary<string, object> record, string value) { return (string)record["status"] == value; }
    private static bool Busy() { return (bool)Dict(State()["business"])["pending_daily_arena"]; }
    private static void Check(bool condition, string why)
    {
        if (!condition) throw new Exception(why + "\n" + Read("state.json") +
            (File.Exists(PathFor("ack.json")) ? "\n" + Read("ack.json") : ""));
        checks++;
    }
    private static bool Gui(Dictionary<string, object> parent, string phase, bool attempted)
    {
        Dictionary<string, object> gui = Dict(parent["campaign_gui"]);
        return (int)gui["command_seq"] == sequence && (string)gui["phase"] == phase &&
            (bool)gui["invocation_attempted"] == attempted;
    }
    private static void ExactSnapshot()
    {
        Check(Json.Serialize(Arena()) == Json.Serialize(AckResult()["daily_arena"]) &&
            Json.Serialize(Arena()) == ((DailyArenaChestModel)ProbeField("dailyArena")).Json(), "exact model JSON is published");
    }
    private static void Grammar()
    {
        foreach (string action in new[] { "open", "enter", "open_chest", "claim", "reward_confirm", "close" })
        {
            Command parsed = Command.Parse(ActionWire(1, action, 9, ArenaFixture.Id, UInt64.MaxValue.ToString()), Nonce, 0);
            Check(parsed != null && parsed.DailyArenaAction == action && parsed.OperationId == ArenaFixture.Id &&
                parsed.DailyArenaCharacterId == UInt64.MaxValue && parsed.ExpectedRevision == 9, "exact arena command");
        }
        string wire = ActionWire(1, "claim", 1);
        foreach (string invalid in new[] { wire.Replace("action=claim", "action=paid"), wire + "task_id=3\n",
            wire.Replace("1701", "01701"), wire.Replace("1701", "0"), wire.Replace("1701", "18446744073709551616"),
            wire.Replace("revision=1", "revision=0"), wire.Replace(ArenaFixture.Id, "g" + ArenaFixture.Id.Substring(1)),
            wire.Replace("action=claim\noperation_id=" + ArenaFixture.Id, "operation_id=" + ArenaFixture.Id + "\naction=claim"),
            wire.Replace("arena-command", "arena-observe"), wire.Replace(Nonce, OtherNonce) })
            Check(Command.Parse(invalid, Nonce, 0) == null, "strict arena grammar");
        Check(Command.Parse(Wire(1, "daily-arena-observe"), Nonce, 0) != null, "exact arena observe");
    }
    private static void Observe(ArenaFixture fixture, bool proveClock = true)
    {
        if (proveClock) Check(DailyRefreshProof.RecordSnapshot(fixture.Role, DailyTransport.Read(fixture.Client),
            ArenaFixture.Day + 18000, fixture.Role.next_resource_time), "fixture establishes refresh evidence");
        Send("daily-arena-observe");
        Check(Status(Ack(), "COMPLETED") && (bool)AckResult()["daily_arena_enabled"], "arena observation enabled");
        ExactSnapshot();
    }
    private static string Queue(string action)
    {
        Check((string)Arena()["next_action"] == action, "expected offered action " + action);
        string wire = ActionWire(++sequence, action, (int)Arena()["revision"]);
        Publish(wire); Update();
        Check(Status(Ack(), "ACCEPTED") && Gui(State(), "queued", false) && Busy(), "queued arena action " + action);
        return wire;
    }
    private static void Step(string action)
    {
        Queue(action);
        Invoke("OnGUI"); Invoke("OnGUI");
        Check(Status(Ack(), "COMPLETED") && Gui(AckResult(), "completed", true) && Gui(State(), "completed", true) &&
            (int)Arena()["sequence"] == sequence && (string)Arena()["operation_id"] == ArenaFixture.Id,
            "one admitted GUI branch " + action);
        ExactSnapshot();
    }
    private static void End(bool clean, bool stop)
    {
        if (stop)
        {
            Send("stop");
            Check(GameObject.LastHelper.PendingDestroy, "stop requests helper destruction");
        }
        Invoke("OnDestroy");
        Check((bool)State()["component_destroyed"] && (bool)State()["reconnect_allowed"] == clean, "reconnect depends on actual ownership");
        if (clean) Check(Bootstrap(OtherNonce).StartsWith("BOOTSTRAP_OK;"), "clean helper rebootstrap");
        else Check(Busy() && Read("recovery_unknown.json").Contains("daily_arena_request_or_cleanup_unresolved_restart_required") &&
            Bootstrap(OtherNonce).StartsWith("BOOTSTRAP_REJECTED;recovery_unknown"), "unresolved arena blocks rebootstrap");
    }
    private static void CompetingActions()
    {
        string refresh = "action={0}\nexpected_revision=1\noperation_id=" + ArenaFixture.Id +
            "\ntarget_id=0\ndesired_level=0\nmax_requests=0\nmax_money=0\nmax_stone=0\nmax_food=0\nmode=\n";
        string fight = "action=enter\nexpected_revision=1\ncampaign_id=0\ntroop_id=0\nattempt_id=\n";
        string otherDaily = "action=open\noperation_id=" + ArenaFixture.Id + "\ncharacter_id=1701\ntask_id={0}\nexpected_revision=1\n";
        Send("player-state-refresh", String.Format(refresh, "player_state_refresh"));
        CheckOwned();
        Send("daily-state-refresh", String.Format(refresh, "daily_state_refresh"));
        CheckOwned();
        Send("campaign-command", fight);
        CheckOwned();
        Send("challenge-command", fight);
        CheckOwned();
        Send("development-command", "action=equipment_open\nexpected_revision=1\noperation_id=" + ArenaFixture.Id +
            "\ntarget_id=1\ndesired_level=1\nmax_requests=1\nmax_money=0\nmax_stone=0\nmax_food=0\nmode=\n");
        CheckOwned();
        Send("daily-guild-command", "action=open\nexpected_revision=1\ncharacter_id=1701\nguild_id=1\ntask_id=18\noperation_id=" +
            ArenaFixture.Id + "\ndonation_type=0\nmax_premium=0\n");
        CheckOwned();
        Send("daily-reward-command", "action=open\nexpected_revision=1\ncharacter_id=1701\ncategory_id=2\noperation_id=" +
            ArenaFixture.Id + "\n");
        CheckOwned();
        Send("daily-timed-command", String.Format(otherDaily, 12));
        CheckOwned();
        Send("daily-stone-command", String.Format(otherDaily, 16));
        CheckOwned();
        Send("daily-mingshi-command", String.Format(otherDaily, 7));
        CheckOwned();
        Send("disable-restore");
        CheckOwned();
    }
    private static void CheckOwned()
    {
        Check((int)Ack()["command_seq"] == sequence && Status(Ack(), "REJECTED") &&
            (string)Ack()["reason"] == "daily_arena_operation_owned" && Busy(), "competing mutation blocked");
    }
    private static void Happy(ArenaFixture fixture, bool pushFirst)
    {
        Observe(fixture);
        Step("open");
        CompetingActions();
        Step("enter");
        if (pushFirst) Step("open_chest");
        ArenaHandler.Callback stock355 = fixture.Client.m_handlers[355].handler;
        Step("claim");
        Check(fixture.ArenaUi.Requests == 1 && !fixture.ArenaUi.FindBack && fixture.ArenaUi.RequestTime == ArenaFixture.Free &&
            !Status(Arena(), "COMPLETED") && !(bool)Arena()["settled"] && Busy(), "GUI completion is invocation evidence only");
        Check(!object.ReferenceEquals(stock355, fixture.Client.m_handlers[355].handler), "permanent 355 is observed");
        if (pushFirst) fixture.Progress(); else fixture.Deliver(Free, ArenaFixture.Free, true);
        Update();
        Check(!(bool)Arena()["settled"] && Busy(), "one reply does not settle");
        if (pushFirst) fixture.Deliver(Free, ArenaFixture.Free, true); else fixture.Progress();
        Update();
        Check((bool)Arena()["settled"] && (bool)Arena()["callback_returned"] && (bool)Arena()["activity_push_returned"] &&
            (int)Arena()["receipt_opcode"] == 1122 && (int)Arena()["receipt_gold_consume"] == 0 &&
            (int)Arena()["counter_before"] == 0 && (int)Arena()["counter_after"] == 1 && Busy() &&
            fixture.Role.gold == 500 && fixture.Role.acts[3].condition_param == 1 && fixture.Role.act_rewards[6].acts[3].condition_param == 1 &&
            object.ReferenceEquals(stock355, fixture.Client.m_handlers[355].handler), "settled correlated replies and restored observer");
        CompetingActions();
        Step("reward_confirm"); Step("close");
        if (pushFirst) Step("close");
        Check(Status(Arena(), "COMPLETED") && !Busy() && MainUI.m_subui == null && Global.extern_enabled &&
            fixture.ArenaUi.Requests == 1 && fixture.Role.arena.bonuses.Count == (pushFirst ? 1 : 0), "verified stock cleanup, paid rows untouched");
        End(true, true);
    }
    private static void FinalBoundary(ArenaFixture fixture, string scenario)
    {
        CapabilityStream stream = new CapabilityStream();
        fixture.Client.m_stream = stream;
        fixture.OpenArena();
        Observe(fixture);
        int rev = (int)Arena()["revision"];
        string wire = Queue("claim");
        bool reached = false, postRead = false;
        int loads = fixture.LoaderCalls;
        ArenaHandler.Callback stock355 = fixture.Client.m_handlers[355].handler;
        Global.BeforeTemplate = delegate
        {
            if (reached || !Gui(State(), "owned", false)) return;
            reached = true;
            if (scenario == "stop-sample") Publish(Wire(sequence + 1, "stop"));
            else if (scenario == "expired-lease")
                typeof(SessionLifecycle).GetField("touched", PrivateInstance).SetValue(ProbeField("life"),
                    unchecked((uint)(Environment.TickCount - SessionLifecycle.LeaseMilliseconds)));
            else if (scenario == "expired-gui")
                typeof(Probe).GetField("guiQueuedTick", PrivateInstance).SetValue(GameObject.LastHelper.Probe, unchecked(Environment.TickCount - 1500));
            else if (scenario == "competing-navigation") CampaignModel.TestNavigation = true;
            else if (scenario == "higher-sequence") Publish(Wire(sequence + 1, "heartbeat"));
            else if (scenario == "same-action") Publish(ActionWire(sequence, "close", rev));
            else if (scenario == "same-operation") Publish(ActionWire(sequence, "claim", rev, OtherNonce));
            else if (scenario == "same-role") Publish(ActionWire(sequence, "claim", rev, ArenaFixture.Id, "1702"));
            else if (scenario == "same-revision") Publish(ActionWire(sequence, "claim", rev + 1));
            else if (scenario == "same-name") Publish(Wire(sequence, "heartbeat"));
            else if (scenario == "wrong-nonce") Publish(wire.Replace(Nonce, OtherNonce));
            else if (scenario == "disconnect" || scenario == "swap-stream" || scenario == "gui-after-read" || scenario == "stop-after-read")
                fixture.Client.m_client.BeforeConnected = delegate
                {
                    fixture.Client.m_client.BeforeConnected = null;
                    postRead = true;
                    if (scenario == "disconnect") fixture.Client.m_client.Connected = false;
                    else if (scenario == "swap-stream") fixture.Client.m_stream = new MemoryStream();
                    else if (scenario == "gui-after-read") GUI.enabled = false;
                    else Publish(Wire(sequence + 1, "stop"));
                };
            else
                stream.BeforeCanRead = delegate
                {
                    stream.BeforeCanRead = null;
                    postRead = true;
                    if (scenario == "read-closed") stream.Readable = false;
                    else if (scenario == "write-closed") stream.Writable = false;
                    else throw new Exception("unknown final boundary " + scenario);
                };
        };
        Invoke("OnGUI");
        Global.BeforeTemplate = null;
        CampaignModel.TestNavigation = false;
        GUI.enabled = true;
        if (scenario == "stop-after-read")
        {
            Check(reached && postRead && fixture.ArenaUi.Requests == 1 && Status(Ack(), "COMPLETED") &&
                Gui(AckResult(), "completed", true), "later stop orders after admitted branch");
            sequence++;
            Update();
            Check(GameObject.LastHelper.PendingDestroy && (string)Ack()["command_name"] == "stop", "later stop handled by normal lifecycle");
            End(false, false);
            return;
        }
        Check(reached && fixture.LoaderCalls == loads && fixture.ArenaUi.Requests == 0 && !fixture.Client.m_handlers.ContainsKey(1122) &&
            object.ReferenceEquals(stock355, fixture.Client.m_handlers[355].handler) && Gui(State(), "not_invoked", false),
            "final sample or post-read health prevents all stock calls");
        if (scenario != "stop-sample")
            Check(Status(Ack(), "REJECTED") && Gui(AckResult(), "not_invoked", false), "rejected receipt binds no invocation");
        else Check(GameObject.LastHelper.PendingDestroy && (string)Ack()["command_name"] == "stop", "earlier stop takes ownership");
        if (scenario == "expired-lease") Check(GameObject.LastHelper.PendingDestroy, "expired lease requests helper cleanup");
        if (scenario == "disconnect" || scenario == "swap-stream" || scenario == "gui-after-read" || scenario.EndsWith("-closed", StringComparison.Ordinal))
            Check(postRead, "interference occurred at live post-read transport check");
        End(true, false);
    }
    public static int Main(string[] args)
    {
        string scenario = args[0];
        try
        {
            ArenaFixture fixture = new ArenaFixture(scenario != "paid-only", scenario == "paid-only" ||
                scenario == "push-first" || scenario == "chest-disable-loss");
            Check(Bootstrap(Nonce).StartsWith("BOOTSTRAP_OK;"), "synthetic bootstrap");
            Update(); Update();
            if (scenario == "disabled")
            {
                Grammar();
                Send("daily-arena-observe");
                Check(Status(Ack(), "REJECTED") && !(bool)State()["daily_arena_enabled"] && !(bool)AckResult()["daily_arena_enabled"] &&
                    State()["daily_arena"] == null && AckResult()["daily_arena"] == null, "disabled observer is inert");
                Publish(ActionWire(++sequence, "open", 1)); Update(); Invoke("OnGUI");
                Check(Status(Ack(), "REJECTED") && Gui(AckResult(), "none", false) && !Busy() &&
                    fixture.LoaderCalls == 0 && fixture.ArenaUi.Requests == 0 && fixture.Client.m_handlers.Count == 1,
                    "disabled command performs no game calls or callback changes");
                End(true, true);
            }
            else if (scenario == "push-first" || scenario == "reply-first") Happy(fixture, scenario == "push-first");
            else if (scenario == "paid-only" || scenario == "unproven-clock")
            {
                fixture.OpenArena(); Observe(fixture, scenario != "unproven-clock");
                Check((string)Arena()["reason"] == (scenario == "paid-only" ? "arena_no_current_free_bonus" : "daily_refresh_unverified"), "model refuses unsafe input");
                Publish(ActionWire(++sequence, "claim", (int)Arena()["revision"])); Update(); Invoke("OnGUI");
                Check(Status(Ack(), "REJECTED") && fixture.ArenaUi.Requests == 0 && !fixture.Client.m_handlers.ContainsKey(1122), "no paid/unproven 1122");
                End(true, true);
            }
            else if (scenario == "cache-mismatch" || scenario == "pending-callback")
            {
                Observe(fixture); Step("open"); Step("enter"); Step("claim");
                ArenaHandler owned = fixture.Client.m_handlers[1122];
                if (scenario == "cache-mismatch")
                {
                    fixture.Role.act_rewards[2] = new Database.Activity_Reward { idact_reward = 2 };
                    fixture.Progress(listed: false); fixture.Deliver(Free, ArenaFixture.Free, true); Update();
                    fixture.Role.act_rewards[2].acts[3] = fixture.Role.acts[3];
                    Update();
                    Check(!(bool)Arena()["settled"] && (bool)Arena()["unresolved"] && Busy(), "matching latest and wrong category cannot settle without category 6");
                    CompetingActions();
                }
                End(false, true);
                Check(fixture.Client.m_handlers[355].handler.Method.Name == "handelActvity" &&
                    (scenario != "pending-callback" || object.ReferenceEquals(owned.handler, fixture.ArenaUi.LastStockHandler)),
                    "both callbacks restored on stop when still registered");
            }
            else if (scenario == "wrong-category")
            {
                fixture.Role.act_rewards[2] = new Database.Activity_Reward { idact_reward = 2 };
                fixture.Role.act_rewards[2].acts[3] = fixture.Role.acts[3];
                fixture.Role.act_rewards.Remove(6);
                fixture.OpenArena(); Observe(fixture);
                Check((string)Arena()["reason"] == "arena_activity_unavailable", "pinned activity 3 requires category 6 even if category 2 lists the row");
                Publish(ActionWire(++sequence, "claim", (int)Arena()["revision"])); Update(); Invoke("OnGUI");
                Check(Status(Ack(), "REJECTED") && fixture.ArenaUi.Requests == 0, "wrong-category cache cannot admit 1122");
                End(true, true);
            }
            else if (scenario == "loader-stop" || scenario == "loader-destroy")
            {
                fixture.DeferredPanel = fixture.Select; Observe(fixture); Step("open");
                Check((bool)Arena()["navigation_pending"] && fixture.Select.loading && !Global.extern_enabled && Busy(), "native stock loader owns UI");
                CompetingActions();
                End(false, scenario == "loader-stop");
                Check(fixture.Select.asset_names.Count == 1 && !Global.extern_enabled, "stock loader state retained under hold");
            }
            else if (scenario == "partial-ui-stop" || scenario == "partial-ui-destroy")
            {
                Observe(fixture); Step("open"); Step("enter"); Step("claim");
                fixture.Deliver(Free, ArenaFixture.Free, true); fixture.Progress(); Update();
                Check((bool)Arena()["settled"] && fixture.Dialog.enabled && !Global.extern_enabled && Busy(), "settled reward UI still owned");
                fixture.Client.m_stream = new MemoryStream();
                Update();
                End(false, scenario == "partial-ui-stop");
                Check(fixture.Dialog.enabled && !Global.extern_enabled, "transport loss leaves stock UI for manual recovery");
            }
            else if (scenario == "chest-disable-loss")
            {
                Observe(fixture); Step("open"); Step("enter"); Step("open_chest");
                fixture.Chest.OnDisable = delegate { fixture.Client.m_stream = new MemoryStream(); };
                Queue("claim"); Invoke("OnGUI");
                Check(Status(Ack(), "UNKNOWN") && Gui(AckResult(), "unknown", true) && fixture.ArenaUi.Requests == 0 &&
                    !(bool)Arena()["invoked"] && (bool)Arena()["navigation_pending"] && !fixture.Chest.interactive && Busy(),
                    "post-admission stock disable can remain visible without sending 1122");
                End(false, true);
                Check(!fixture.Chest.interactive && fixture.Client.m_handlers[355].handler.Method.Name == "handelActvity",
                    "partial stock mutation holds reconnect with callback restored");
            }
            else if (scenario == "business-before-arena")
            {
                Observe(fixture);
                foreach (object pending in new[] { ProbeField("playerRefresh"), ProbeField("dailyRefresh") })
                {
                    PlayerStateRefreshModel refresh = (PlayerStateRefreshModel)pending;
                    refresh.Status = "QUEUED";
                    Publish(ActionWire(++sequence, "open", (int)Arena()["revision"])); Update();
                    Check(Status(Ack(), "REJECTED") && fixture.LoaderCalls == 0, "arena blocked by either refresh");
                    refresh.Status = "IDLE";
                    Update();
                }
                CampaignModel.TestNavigation = true;
                Publish(ActionWire(++sequence, "open", (int)Arena()["revision"])); Update();
                Check(Status(Ack(), "REJECTED") && fixture.LoaderCalls == 0, "arena blocked by campaign navigation");
                CampaignModel.TestNavigation = false;
                End(true, true);
            }
            else FinalBoundary(fixture, scenario);
            Console.WriteLine("{\"status\":\"PASS\",\"scenario\":" + Command.Quote(scenario) + ",\"checks\":" + checks +
                ",\"scope\":\"production Probe, arena model, callback adapters and receipts with synthetic game objects\"}");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
