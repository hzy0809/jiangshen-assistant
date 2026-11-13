using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;
using JiangShen.AssistantRuntime;
using UnityEngine;
using Handler = game.net.tcp_base.Handler;

internal static class DailyRefineProbeTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly string Nonce = Guid.NewGuid().ToString("N"), Other = Guid.NewGuid().ToString("N");
    private static readonly string Operation = Guid.NewGuid().ToString("N");
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static readonly byte[] Reply = { 8, 0, 16, 40, 26, 8, 8, 2, 16, 0, 24, 26, 32, 0 };
    private static int sequence, checks;
    private static Probe Probe { get { return GameObject.LastHelper.Probe; } }
    private static game.net.tcp_client Client { get { return Global.m_client; } }
    private static ArmyStarRefineUI Panel { get { return MainUI.Panel; } }
    private static Handler Push { get { return Client.HandlerFor(355); } }

    private static string Bootstrap(string nonce)
    {
        string path = Path.Combine(Entry.SessionsRoot, nonce);
        Directory.CreateDirectory(path);
        return Entry.Bootstrap(path, nonce, Entry.GetCurrentThreadId());
    }
    private static string FilePath(string file) { return Path.Combine(Entry.SessionsRoot, Nonce, file); }
    private static string Read(string file) { return File.ReadAllText(FilePath(file)); }
    private static Dictionary<string, object> Dict(object value) { return (Dictionary<string, object>)value; }
    private static Dictionary<string, object> State() { return Json.Deserialize<Dictionary<string, object>>(Read("state.json")); }
    private static Dictionary<string, object> Ack() { return Json.Deserialize<Dictionary<string, object>>(Read("ack.json")); }
    private static Dictionary<string, object> Result() { return Dict(Ack()["result"]); }
    private static Dictionary<string, object> Refine() { return Dict(State()["daily_refine"]); }
    private static Dictionary<string, object> View() { return Dict(Refine()["view"]); }
    private static Dictionary<string, object> Op() { return Dict(Refine()["operation"]); }
    private static bool Busy() { return (bool)Dict(State()["business"])["pending_daily_refine"]; }
    private static object ProbeField(string name) { return typeof(Probe).GetField(name, Private).GetValue(Probe); }
    private static void Invoke(string name) { typeof(Probe).GetMethod(name, Private).Invoke(Probe, null); }
    private static void Update() { Time.frameCount++; Invoke("Update"); }
    private static void Gui() { Invoke("OnGUI"); Invoke("OnGUI"); }

    private static string Wire(int seq, string name) { return "version=3\nnonce=" + Nonce + "\nsequence=" + seq + "\ncommand=" + name + "\n"; }
    private static string ActionWire(int seq, string action, int revision, string op, string role = "45")
    {
        return Wire(seq, "daily-refine-command") + "action=" + action + "\noperation_id=" + op +
            "\ncharacter_id=" + role + "\ntask_id=21\nexpected_revision=" + revision + "\n";
    }
    private static void Publish(string wire) { File.WriteAllText(FilePath("command.txt"), wire); }
    private static void Send(string command) { Publish(Wire(++sequence, command)); Update(); }

    private static bool Status(Dictionary<string, object> record, string status) { return (string)record["status"] == status; }
    private static bool GuiIs(Dictionary<string, object> parent, string phase, bool attempted)
    {
        Dictionary<string, object> gui = Dict(parent["campaign_gui"]);
        return (int)gui["command_seq"] == sequence && (string)gui["phase"] == phase && (bool)gui["invocation_attempted"] == attempted;
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name + "\n" + (File.Exists(FilePath("state.json")) ? Read("state.json") : "") +
            "\n" + (File.Exists(FilePath("ack.json")) ? Read("ack.json") : ""));
        checks++;
    }

    private static void Grammar()
    {
        foreach (string action in new[] { "open", "refine", "close" })
        {
            string wire = ActionWire(1, action, 7, Operation, UInt64.MaxValue.ToString());
            Command parsed = Command.Parse(wire, Nonce, 0);
            Check(parsed != null && parsed.DailyRefineAction == action && parsed.OperationId == Operation &&
                parsed.DailyRefineCharacterId == UInt64.MaxValue && parsed.DailyRefineTaskId == 21 && parsed.ExpectedRevision == 7,
                "exact refine wire");
        }
        string valid = ActionWire(1, "refine", 7, Operation);
        foreach (string invalid in new[] { valid.Replace("action=refine", "action=gold"), valid.Replace("task_id=21", "task_id=5"),
            valid.Replace("character_id=45", "character_id=045"), valid.Replace("expected_revision=7", "expected_revision=0"),
            valid.Replace(Operation, Operation.ToUpperInvariant()), valid.Replace(Nonce, Other), valid + "opcode=991\n",
            valid.Replace("daily-refine-command", "daily-refine-observe") })
            Check(Command.Parse(invalid, Nonce, 0) == null, "reject other refinement wire");
    }

    private static string Queue(string action)
    {
        Check((string)View()["next_action"] == action, "expected offer " + action);
        string wire = ActionWire(++sequence, action, (int)Refine()["revision"], Operation);
        Publish(wire); Update();
        Check(Status(Ack(), "ACCEPTED") && GuiIs(State(), "queued", false) && Busy(), "queue " + action);
        return wire;
    }

    private static void Step(string action)
    {
        Queue(action); Gui();
        Dictionary<string, object> ack = Ack(), op = Op();
        Check(Status(ack, "COMPLETED") && GuiIs(Result(), "completed", true) && GuiIs(State(), "completed", true) &&
            (int)op["sequence"] == sequence && (string)op["operation_id"] == Operation, "bound GUI " + action);
        Check(Json.Serialize(Refine()) == Json.Serialize(Result()["daily_refine"]), "exact native scope echoed in state and ACK");
    }

    private static void End(bool reconnect, bool destroy = false)
    {
        if (!destroy && !GameObject.LastHelper.PendingDestroy) Send("stop");
        if (!destroy) Check(GameObject.LastHelper.PendingDestroy, "stop scheduled helper destruction");
        Invoke("OnDestroy");
        Check((bool)State()["component_destroyed"] && (bool)State()["reconnect_allowed"] == reconnect, "reconnect reflects held state");
        if (reconnect) Check(Bootstrap(Other).StartsWith("BOOTSTRAP_OK;"), "verified cleanup permits new helper");
        else Check(Busy() && Read("recovery_unknown.json").Contains("daily_refine_request_or_cleanup_unresolved_restart_required") &&
            Bootstrap(Other).StartsWith("BOOTSTRAP_REJECTED;recovery_unknown"), "unverified cleanup requires restart");
    }

    private static void Disabled()
    {
        Send("daily-refine-observe");
        Check(Status(Ack(), "REJECTED") && !(bool)Result()["daily_refine_enabled"] && Result()["daily_refine"] == null, "disabled observe");
        foreach (string action in new[] { "open", "refine", "close" })
        {
            Publish(ActionWire(++sequence, action, 1, Operation)); Update(); Gui();
            Check(Status(Ack(), "REJECTED") && (string)Ack()["reason"] == "daily_refine_disabled" &&
                !(bool)State()["daily_refine_enabled"] && State()["daily_refine"] == null && !Busy(), "disabled action " + action);
        }
        Check(MainUI.Opens == 0 && Panel.Requests == 0 && MainUI.Closes == 0, "disabled does not touch stock UI");
        End(true);
    }

    private static void ReadyForOpen()
    {
        Send("daily-refine-observe");
        Check(Status(Ack(), "COMPLETED") && (bool)State()["daily_refine_enabled"] && (bool)Result()["daily_refine_enabled"] &&
            !(bool)State()["daily_soul_enabled"] && !(bool)State()["daily_stone_enabled"] &&
            (string)View()["reason"] == "daily_refresh_unverified", "opt-in isolated and clock gate requires proof");
        Check(DailyRefreshProof.RecordSnapshot(Global.character_, DailyTransport.Read(Client), 218000, 304400), "fixture clock proof");
        Update();
        Publish(ActionWire(++sequence, "open", (int)Refine()["revision"], Operation, "46")); Update(); Gui();
        Check(Status(Ack(), "REJECTED") && MainUI.Opens == 0 && !Busy(), "wrong role rejects before stock open");
    }

    private static void Open()
    {
        ReadyForOpen();
        Step("open");
        Check(Status(Op(), "OPENING") && MainUI.Opens == 1 && Panel.Requests == 0, "stock loader only");
        Panel.Show(); Update();
        Check(Status(Op(), "OPEN") && (bool)Op()["ui_ownership_held"] && Busy(), "owned coin tab open");
    }

    private static void CleanOpenRejection()
    {
        ReadyForOpen();
        string command = Queue("open");
        int samples = 0;
        Global.BeforeQuotes = delegate
        {
            if (!GuiIs(State(), "owned", false) || ++samples != 2) return;
            Global.BeforeQuotes = null;
            Publish(command.Replace("action=open", "action=refine"));
        };
        Gui();
        Dictionary<string, object> op = Op();
        Check(samples == 2 && Status(Ack(), "REJECTED") && GuiIs(Result(), "not_invoked", false) &&
            Status(op, "REJECTED") && (string)op["operation_id"] == Operation && (int)op["sequence"] == sequence &&
            (string)View()["next_action"] == "open" && !(bool)op["mutation_attempted"] && !(bool)op["ui_ownership_held"] &&
            MainUI.Opens == 0 && MainUI.m_subui == null && !Busy(), "exact initial open refusal is clean");
        End(true);
    }

    private static void Settle()
    {
        Step("refine");
        Check(Status(Op(), "PENDING") && Panel.Requests == 1 && Push.handler.Method.Name != "handelActvity", "one stock 991, 355 observed");
        DailyRefineTests.Advance(); Update();
        Check(Status(Op(), "PENDING") && !(bool)Op()["account_progress_observed"], "355 alone does not settle");
        Client.Deliver(991, Reply, true); Update();
        Check(Status(Op(), "PENDING") && (bool)Op()["stock_callback_returned"] &&
            (string)View()["next_action"] == "" && Global.character_.practice == 90, "credit animation remains outstanding");
        Panel.DrainFlyEffects(); Update();
        Check(Status(Op(), "SETTLED") && (int)Op()["coins_spent"] == 40 && (int)Op()["practice_credit"] == 13 &&
            (int)View()["current"] == 1 && Global.character_.practice == 103 && Global.character_.money64 == 4999999985L &&
            Global.character_.gold == 100 && Global.character_.bind_gold == 5 && Global.character_.experience == 22 &&
            Panel.XpRequests == 0 && Panel.GoldRequests == 0 && Push.handler.Method.Name == "handelActvity", "exact coin, credit and activity evidence");
        Update();
    }

    private static void Competing()
    {
        Send("disable-restore"); Gui();
        Check(Status(Ack(), "REJECTED") && (string)Ack()["reason"] == "daily_refine_operation_owned", "owned refine blocks other GUI");
        string refresh = "action=daily_state_refresh\nexpected_revision=1\noperation_id=" + Operation +
            "\ntarget_id=0\ndesired_level=0\nmax_requests=0\nmax_money=0\nmax_stone=0\nmax_food=0\nmode=\n";
        Publish(Wire(++sequence, "daily-state-refresh") + refresh); Update(); Gui();
        Check(Status(Ack(), "REJECTED") && (string)Ack()["reason"] == "daily_refine_operation_owned", "owned refine blocks refresh");
    }

    private static void Success()
    {
        Competing(); Settle(); Step("close");
        Check(Status(Op(), "COMPLETED") && (bool)Op()["panel_closed"] && !(bool)Op()["ui_ownership_held"] &&
            MainUI.Closes == 1 && !Busy() && Panel.Requests == 1, "verified stock close, no replay");
        End(true);
    }

    private static void FinalGate(string scenario)
    {
        string command = Queue("refine");
        int samples = 0;
        bool entered = false;
        Global.BeforeQuotes = delegate
        {
            if (!GuiIs(State(), "owned", false) || ++samples != 2) return;
            entered = true; Global.BeforeQuotes = null;
            if (scenario == "commit-stop") Publish(Wire(++sequence, "stop"));
            else if (scenario == "commit-command") Publish(command.Replace("action=refine", "action=close"));
            else if (scenario == "commit-lease")
            {
                int healthReads = 0;
                Client.m_client.BeforeConnected = delegate
                {
                    // One final transport read in prepare, then the verified
                    // clock and physical transport reads in the cheap check.
                    if (++healthReads != 3) return;
                    Client.m_client.BeforeConnected = null;
                    typeof(SessionLifecycle).GetField("touched", Private).SetValue(ProbeField("life"),
                        unchecked((uint)(Environment.TickCount - SessionLifecycle.LeaseMilliseconds)));
                };
            }
        };
        Gui();
        Check(entered && Panel.Requests == 0 && MainUI.Closes == 0 && !(bool)Op()["mutation_attempted"] &&
            (bool)Op()["ui_ownership_held"] && Push.handler.Method.Name == "handelActvity", "final commit cancels stock send");
        if (scenario == "commit-command")
            Check(Status(Ack(), "REJECTED") && GuiIs(Result(), "not_invoked", false), "changed exact wire rejected");
        else Check(GameObject.LastHelper.PendingDestroy, "stop or lease expiry preserves hold and finishes");
        End(false);
    }

    private static void Unknown(string scenario)
    {
        if (scenario == "close-unverified")
        {
            Settle(); MainUI.CloseKeepsVisible = true; Queue("close"); Gui();
            Check(Status(Ack(), "UNKNOWN") && GuiIs(Result(), "unknown", true) && (bool)Op()["ui_ownership_held"] &&
                !(bool)Op()["panel_closed"] && Panel.Requests == 1, "unverified stock close ACK is unknown");
        }
        else
        {
            Step("refine");
            if (scenario == "timeout")
            {
                object refine = ProbeField("dailyRefine");
                refine.GetType().GetField("since", Private).SetValue(refine, unchecked(Environment.TickCount - 20000));
                Update();
                Check(Status(Op(), "UNKNOWN") && Push.handler.Method.Name == "handelActvity", "timeout published without retry");
                Competing();
            }
        }
        End(false, scenario == "destroy");
        if (scenario != "close-unverified")
        {
            Client.Deliver(991, Reply, true);
            Check(Panel.Requests == 1 && Panel.Replies == 1 && MainUI.Closes == 0 &&
                Push.handler.Method.Name == "handelActvity", "stock callbacks forward after stop");
        }
    }

    public static int Main(string[] args)
    {
        try
        {
            Grammar(); DailyRefineTests.ResetGame();
            Check(Bootstrap(Nonce).StartsWith("BOOTSTRAP_OK;"), "bootstrap actual Probe");
            Update(); Update();
            string scenario = args[0];
            if (scenario == "disabled") Disabled();
            else if (scenario == "open-commit-command") CleanOpenRejection();
            else
            {
                Open();
                if (scenario == "settle") Success();
                else if (scenario.StartsWith("commit-")) FinalGate(scenario);
                else if (scenario == "open-stop") End(false);
                else Unknown(scenario);
            }
            Console.WriteLine("{\"daily_refine_probe\":\"passed\",\"scenario\":" + Command.Quote(scenario) +
                ",\"checks\":" + checks + ",\"session\":" + Command.Quote(FilePath("")) + "}");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
