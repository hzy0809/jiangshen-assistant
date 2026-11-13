using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;
using JiangShen.AssistantRuntime;
using UnityEngine;
using Fixture = DailySoulTests.Fixture;
using Handler = game.net.tcp_base.Handler;

internal static class DailySoulProbeTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly string Nonce = Guid.NewGuid().ToString("N"), Other = Guid.NewGuid().ToString("N");
    private static readonly string Operation = Guid.NewGuid().ToString("N");
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static int sequence, checks;

    private static string Bootstrap(string nonce)
    {
        string path = Path.Combine(Entry.SessionsRoot, nonce);
        Directory.CreateDirectory(path);
        return Entry.Bootstrap(path, nonce, Entry.GetCurrentThreadId());
    }
    private static string FilePath(string file) { return Path.Combine(Entry.SessionsRoot, Nonce, file); }
    private static string Read(string file) { return File.ReadAllText(FilePath(file)); }
    private static Dictionary<string, object> Record(string file) { return Json.Deserialize<Dictionary<string, object>>(Read(file)); }
    private static Dictionary<string, object> Dict(object value) { return (Dictionary<string, object>)value; }
    private static Dictionary<string, object> State() { return Record("state.json"); }
    private static Dictionary<string, object> Ack() { return Record("ack.json"); }
    private static Dictionary<string, object> Result() { return Dict(Ack()["result"]); }
    private static Dictionary<string, object> Soul() { return Dict(State()["daily_soul"]); }
    private static Dictionary<string, object> Op() { return Dict(Soul()["operation"]); }
    private static bool Busy() { return (bool)Dict(State()["business"])["pending_daily_soul"]; }
    private static object ProbeField(string name) { return typeof(Probe).GetField(name, Private).GetValue(GameObject.LastHelper.Probe); }
    private static void Invoke(string name) { typeof(Probe).GetMethod(name, Private).Invoke(GameObject.LastHelper.Probe, null); }
    private static void Update() { Time.frameCount++; Invoke("Update"); }
    private static string Wire(int seq, string name) { return "version=3\nnonce=" + Nonce + "\nsequence=" + seq + "\ncommand=" + name + "\n"; }
    private static string ActionWire(int seq, string action, int revision, string op, string role = "42", int task = 5)
    {
        return Wire(seq, "daily-soul-command") + "action=" + action + "\noperation_id=" + op +
            "\ncharacter_id=" + role + "\ntask_id=" + task + "\nexpected_revision=" + revision + "\n";
    }
    private static void Publish(string wire) { File.WriteAllText(FilePath("command.txt"), wire); }
    private static void Send(string name, string args = "") { Publish(Wire(++sequence, name) + args); Update(); }
    private static bool Status(Dictionary<string, object> row, string status) { return (string)row["status"] == status; }
    private static void Check(bool condition, string why)
    {
        if (!condition) throw new Exception(why + "\n" + Read("state.json") +
            (File.Exists(FilePath("ack.json")) ? "\n" + Read("ack.json") : ""));
        checks++;
    }
    private static bool Gui(Dictionary<string, object> parent, string phase, bool attempted)
    {
        Dictionary<string, object> gui = Dict(parent["campaign_gui"]);
        return (int)gui["command_seq"] == sequence && (string)gui["phase"] == phase && (bool)gui["invocation_attempted"] == attempted;
    }
    private static void ExactProjection()
    {
        string model = ((DailySoulModel)ProbeField("dailySoul")).Json();
        Check(Json.Serialize(Soul()) == model && Json.Serialize(Result()["daily_soul"]) == model, "exact model JSON in state and ACK");
    }
    private static void Prove(Fixture fixture, uint cycle = 218000)
    {
        Check(DailyRefreshProof.RecordSnapshot(fixture.Role, DailyTransport.Read(fixture.Client), cycle,
            fixture.Role.next_resource_time), "synthetic player refresh established proof");
    }
    private static void Observe()
    {
        Send("daily-soul-observe");
        Check(Status(Ack(), "COMPLETED") && (bool)State()["daily_soul_enabled"] && (bool)Result()["daily_soul_enabled"], "soul observer enabled");
        ExactProjection();
    }
    private static string Queue(string action, string op = null)
    {
        Check((string)Soul()["action"] == action, "expected action " + action);
        string wire = ActionWire(++sequence, action, (int)Soul()["revision"], op ?? Operation);
        Publish(wire); Update();
        Check(Status(Ack(), "ACCEPTED") && Gui(State(), "queued", false) && Busy(), "queue " + action);
        return wire;
    }
    private static void Step(string action, string op = null)
    {
        Queue(action, op); Invoke("OnGUI"); Invoke("OnGUI");
        Check(Status(Ack(), "COMPLETED") && Gui(Result(), "completed", true) && (bool)Op()["invoked"] &&
            (int)Op()["sequence"] == sequence && (string)Op()["operation_id"] == (op ?? Operation), "GUI invokes " + action + " once");
        ExactProjection();
    }
    private static void Open(Fixture fixture)
    {
        Prove(fixture); Observe(); Step("open");
        Check(MainUI.Opens == 1 && fixture.Client.Sent.Count == 0 && (string)Soul()["action"] == "start", "bound stock panel opened");
    }
    private static void End(bool reconnect)
    {
        Invoke("OnDestroy");
        Check((bool)State()["component_destroyed"] && (bool)State()["reconnect_allowed"] == reconnect, "reconnect reflects actual ownership");
        if (reconnect) Check(Bootstrap(Other).StartsWith("BOOTSTRAP_OK;"), "clean helper permits a new session");
        else Check(Busy() && Read("recovery_unknown.json").Contains("daily_soul_request_or_cleanup_unresolved_restart_required") &&
            Bootstrap(Other).StartsWith("BOOTSTRAP_REJECTED;recovery_unknown"), "unverified request or panel retains restart guard");
    }
    private static void Grammar()
    {
        foreach (string action in new[] { "open", "start", "harvest", "close" })
        {
            Command command = Command.Parse(ActionWire(1, action, 7, Operation, UInt64.MaxValue.ToString()), Nonce, 0);
            Check(command != null && command.DailySoulAction == action && command.OperationId == Operation &&
                command.DailySoulCharacterId == UInt64.MaxValue && command.DailySoulTaskId == 5 && command.ExpectedRevision == 7,
                "exact soul grammar for " + action);
        }
        string wire = ActionWire(1, "start", 7, Operation);
        foreach (string invalid in new[] { wire.Replace("action=start", "action=grow"), wire.Replace("task_id=5", "task_id=7"),
            wire.Replace("character_id=42", "character_id=042"), wire.Replace("expected_revision=7", "expected_revision=0"),
            wire.Replace(Operation, "z" + Operation.Substring(1)), wire.Replace(Nonce, Other), wire + "max_gold=0\n",
            wire.Replace("task_id=5\nexpected_revision=7", "expected_revision=7\ntask_id=5") })
            Check(Command.Parse(invalid, Nonce, 0) == null, "reject alternative command grammar");
    }
    private static void Disabled(Fixture fixture, bool available)
    {
        if (available) Prove(fixture);
        Send("daily-soul-observe");
        Check(Status(Ack(), "REJECTED") && !(bool)Result()["daily_soul_enabled"] && Result()["daily_soul"] == null, "disabled observe");
        Publish(ActionWire(++sequence, "open", 1, Operation)); Update(); Invoke("OnGUI");
        Check(Status(Ack(), "REJECTED") && (string)Ack()["reason"] == "daily_soul_disabled" && !(bool)State()["daily_soul_enabled"] &&
            State()["daily_soul"] == null && MainUI.Opens == 0 && fixture.Client.Sent.Count == 0 && !Busy(), "disabled GUI untouched");
        End(true);
    }
    private static void Competing()
    {
        string refresh = "action={0}\nexpected_revision=1\noperation_id=" + Operation +
            "\ntarget_id=0\ndesired_level=0\nmax_requests=0\nmax_money=0\nmax_stone=0\nmax_food=0\nmode=\n";
        string otherDaily = "action=open\noperation_id=" + Operation + "\ncharacter_id=42\ntask_id={0}\nexpected_revision=1\n";
        string fight = "action=enter\nexpected_revision=1\ncampaign_id=0\ntroop_id=0\nattempt_id=\n";
        Owned("player-state-refresh", String.Format(refresh, "player_state_refresh"));
        Owned("daily-state-refresh", String.Format(refresh, "daily_state_refresh"));
        Owned("campaign-command", fight);
        Owned("challenge-command", fight);
        Owned("development-command", "action=equipment_open\nexpected_revision=1\noperation_id=" + Operation +
            "\ntarget_id=1\ndesired_level=1\nmax_requests=1\nmax_money=0\nmax_stone=0\nmax_food=0\nmode=\n");
        Owned("daily-guild-command", "action=open\nexpected_revision=1\ncharacter_id=42\nguild_id=1\ntask_id=18\noperation_id=" +
            Operation + "\ndonation_type=0\nmax_premium=0\n");
        Owned("daily-reward-command", "action=open\nexpected_revision=1\ncharacter_id=42\ncategory_id=2\noperation_id=" + Operation + "\n");
        Owned("daily-timed-command", String.Format(otherDaily, 12));
        Owned("daily-stone-command", String.Format(otherDaily, 16));
        Owned("daily-mingshi-command", String.Format(otherDaily, 7));
        Owned("daily-arena-command", "action=open\noperation_id=" + Operation + "\ncharacter_id=42\nexpected_revision=1\n");
        Owned("disable-restore", "");
    }
    private static void Owned(string command, string payload)
    {
        Send(command, payload); Invoke("OnGUI");
        Check(Status(Ack(), "REJECTED") && (string)Ack()["reason"] == "daily_soul_operation_owned", "owned soul blocks " + command);
    }
    private static void Progression(Fixture fixture, bool oneFree)
    {
        Global.Activity.cond_param = 2; Global.Start.free_times = oneFree ? 1 : 2;
        Prove(fixture); Observe();
        for (int n = 1; n <= (oneFree ? 1 : 2); n++)
        {
            string op = n == 1 ? Operation : Other;
            Global.UsedReply = (byte)n; Global.ProgressReply = n;
            Step("open", op); Step("start", op);
            Check(Status(Op(), "PENDING") && !(bool)Op()["start_verified"] && (int)Op()["opcode"] == 517 &&
                fixture.Soul.LastFree && !object.ReferenceEquals(fixture.Push, fixture.Handler(355).handler), "517 GUI is invocation, not settlement");
            if (n == 1) Competing();
            fixture.ReplyStart(); Update();
            Check((bool)Op()["start_verified"] && (string)Soul()["action"] == "harvest", "pinned 517 applied by stock callback");
            Step("harvest", op); fixture.ReplyHarvest(); Update();
            Check(Status(Op(), "PENDING") && !(bool)Op()["harvest_verified"] && (int)Op()["opcode"] == 518, "518 alone leaves settlement pending");
            fixture.Progress(); Update();
            Check(!(bool)Op()["harvest_verified"] && (string)Soul()["action"] == "", "stock harvest animation must finish");
            fixture.Soul.FinishGather(); Update();
            Check((bool)Op()["harvest_verified"] && (int)Op()["counter_before"] == n - 1 && (int)Op()["counter_after"] == n &&
                (int)Soul()["current"] == n && object.ReferenceEquals(fixture.Push, fixture.Handler(355).handler),
                "pinned 355 updates both caches and is restored after harvest");
            Step("close", op);
            Check(Status(Op(), "COMPLETED") && (string)Soul()["ui_cleanup"] == "clear" && !Busy(), "verified stock panel cleanup");
        }
        Check((string)Soul()["reason"] == (oneFree ? "daily_soul_free_blue_exhausted" : "daily_soul_activity_complete") &&
            fixture.Client.Sent.Count == (oneFree ? 2 : 4) && !fixture.Client.Sent.Contains(519) &&
            fixture.Role.gold == 13 && fixture.Role.bind_gold == 7 && fixture.Role.gold_consume == 0 &&
            fixture.Role.money64 + fixture.Role.money == 5000008000L && fixture.Soul.TeachingCalls == 0, "remaining free quota and no premium");
        Send("stop"); Check(GameObject.LastHelper.PendingDestroy, "clean stop requests destruction"); End(true);
    }
    private static void FinalCommit(Fixture fixture, string drift)
    {
        Open(fixture);
        int revision = (int)Soul()["revision"];
        string queued = Queue("start");
        bool hit = false;
        fixture.Role.OnFeatureRead = delegate
        {
            if (!Gui(State(), "owned", false)) return;
            hit = true; fixture.Role.OnFeatureRead = null;
            if (drift == "stop") Publish(Wire(++sequence, "stop"));
            else if (drift == "lease") typeof(SessionLifecycle).GetField("touched", Private).SetValue(ProbeField("life"),
                unchecked((uint)(Environment.TickCount - SessionLifecycle.LeaseMilliseconds)));
            else if (drift == "deadline") typeof(Probe).GetField("guiQueuedTick", Private).SetValue(GameObject.LastHelper.Probe,
                unchecked(Environment.TickCount - 1500));
            else if (drift == "name") Publish(Wire(sequence, "heartbeat"));
            else if (drift == "same-sequence-stop") Publish(Wire(sequence, "stop"));
            else if (drift == "sequence") Publish(Wire(sequence + 1, "heartbeat"));
            else if (drift == "nonce") Publish(queued.Replace(Nonce, Other));
            else if (drift == "action") Publish(ActionWire(sequence, "harvest", revision, Operation));
            else if (drift == "operation") Publish(ActionWire(sequence, "start", revision, Other));
            else if (drift == "role-field") Publish(ActionWire(sequence, "start", revision, Operation, "43"));
            else if (drift == "task") Publish(ActionWire(sequence, "start", revision, Operation, "42", 7));
            else if (drift == "revision") Publish(ActionWire(sequence, "start", revision + 1, Operation));
            else if (drift == "role") fixture.Role.id++;
            else if (drift == "reset") RootUI.m_bRefreshFive = true;
            else if (drift == "cycle") { TimeUtil._now += 86400; TimeUtil._thisDay += 86400; fixture.Role.next_resource_time += 86400; }
            else if (drift == "socket") fixture.Client.m_client.Connected = false;
            else if (drift == "stream") fixture.Client.m_stream.Close();
            else if (drift == "last-deadline" || drift == "last-lease" || drift == "last-stream")
            {
                int healthReads = 0;
                fixture.Client.m_client.OnConnected = delegate
                {
                    // Two transport checks each in prepare and in the commit
                    // gate. The fifth runs in the model immediately before stock.
                    if (++healthReads != 5) return;
                    fixture.Client.m_client.OnConnected = null;
                    if (drift == "last-deadline") typeof(Probe).GetField("guiQueuedTick", Private).SetValue(GameObject.LastHelper.Probe,
                        unchecked(Environment.TickCount - 1500));
                    else if (drift == "last-lease") typeof(SessionLifecycle).GetField("touched", Private).SetValue(ProbeField("life"),
                        unchecked((uint)(Environment.TickCount - SessionLifecycle.LeaseMilliseconds)));
                    else fixture.Client.m_stream.Close();
                };
            }
            else throw new Exception(drift);
        };
        Invoke("OnGUI"); Invoke("OnGUI");
        Check(hit && fixture.Client.Sent.Count == 0 && !(bool)Op()["invoked"] && !(bool)Op()["mutation_attempted"] &&
            object.ReferenceEquals(fixture.Push, fixture.Handler(355).handler), "final prepared sample drift refuses stock call " + drift);
        bool clean = drift != "role" && drift != "cycle" && drift != "reset" && drift != "socket" && drift != "stream" && drift != "last-stream";
        if (drift == "stop" || drift == "lease" || drift == "last-lease")
            Check(Status(Op(), "STOPPED") && (string)Soul()["ui_cleanup"] == "waiting" && !GameObject.LastHelper.PendingDestroy,
                "Stop precedes cancellation and preserves cleanup");
        else Check(Status(Ack(), "REJECTED") && (string)Ack()["command_name"] == "daily-soul-command" &&
            Gui(Result(), "not_invoked", false), "refusal ACK distinguishes no invocation");
        Update(); Update(); End(clean);
    }
    private static void StopAndDestroy(Fixture fixture, string ending)
    {
        Open(fixture);
        if (ending == "pre-gui") Queue("start");
        if (ending == "noop") MainUI.CloseNoOp = true;
        bool pending = ending == "late-start" || ending == "late-harvest" || ending == "start-timeout" || ending == "harvest-timeout" ||
            ending == "cleanup-timeout" || ending == "destroy-pending" || ending == "original-throws";
        if (pending) Step("start");
        if (ending == "late-harvest" || ending == "harvest-timeout")
        { fixture.ReplyStart(); Update(); Step("harvest"); }
        if (ending == "original-throws")
        {
            fixture.Soul.ThrowOnStart = true;
            bool forwarded = false;
            try { fixture.ReplyStart(); } catch (InvalidOperationException e) { forwarded = e.Message == "original_start_exception"; }
            Check(forwarded && fixture.Soul.StartCalls == 1, "original exception forwards with its type and message");
            Update(); Check(Status(Op(), "UNKNOWN"), "throwing original is unverified");
        }
        if (ending == "destroy-open" || ending == "destroy-pending")
        {
            End(false);
            Check(MainUI.Closes == 0, "OnDestroy never performs stock panel action");
            if (pending) { fixture.ReplyStart(); Check(fixture.Soul.StartCalls == 1, "destroyed observer forwards late original"); }
            return;
        }
        Send("stop");
        Check(!GameObject.LastHelper.PendingDestroy && (string)State()["lifecycle"] == "stopping" &&
            (string)Soul()["ui_cleanup"] == "waiting", "stop retains proof, helper and panel ownership");
        if (ending == "cleanup-final-deadline")
        {
            bool crossed = false;
            fixture.Role.OnFeatureRead = delegate
            {
                fixture.Role.OnFeatureRead = null;
                fixture.Client.m_client.OnConnected = delegate
                {
                    fixture.Client.m_client.OnConnected = null;
                    crossed = true;
                    typeof(DailySoulModel).GetField("cleanupSince", Private).SetValue(ProbeField("dailySoul"), unchecked(Environment.TickCount - 8000));
                };
            };
            Update();
            Check(crossed && MainUI.Closes == 0 && fixture.Soul.visible && (string)Soul()["ui_cleanup"] == "unknown" &&
                GameObject.LastHelper.PendingDestroy, "late deadline before recovery invoke retains ownership without closing");
            End(false);
            return;
        }
        if (pending)
        {
            Update(); Check(MainUI.Closes == 0, "no stock close while original is unresolved");
            if (ending == "late-start") fixture.ReplyStart();
            else if (ending == "late-harvest") { fixture.ReplyHarvest(); fixture.Soul.FinishGather(); }
            else if (ending == "start-timeout") fixture.Client.Timeout(517);
            else if (ending == "harvest-timeout") fixture.Client.Timeout(518);
        }
        Update(); Update();
        bool uiClear = ending == "pre-gui" || ending == "late-start" || ending == "late-harvest" ||
            ending == "start-timeout" || ending == "harvest-timeout";
        if (uiClear)
            Check((string)Soul()["ui_cleanup"] == "clear" && MainUI.Closes == 1 && MainUI.m_subui == null &&
                GameObject.LastHelper.PendingDestroy, "only main Update confirms safe cleanup");
        else
        {
            typeof(DailySoulModel).GetField("cleanupSince", Private).SetValue(ProbeField("dailySoul"), unchecked(Environment.TickCount - 8000));
            Update();
            Check((string)Soul()["ui_cleanup"] == "unknown" && GameObject.LastHelper.PendingDestroy &&
                MainUI.Closes == (ending == "noop" ? 1 : 0), "8-second wall deadline preserves cleanup unknown with no replay");
        }
        End(ending == "pre-gui");
        if (ending == "cleanup-timeout") fixture.Client.Timeout(517);
        if (pending) Check(fixture.Soul.StartCalls == 1 && fixture.Client.Sent.Count <= 2 && !fixture.Client.Sent.Contains(519),
            "one original start and no resend or premium");
    }
    private static void RefusedAfterMutation(Fixture fixture, string action)
    {
        Open(fixture); Step("start"); fixture.ReplyStart(); Update();
        bool settled = action == "close";
        if (settled)
        {
            Step("harvest"); fixture.ReplyHarvest(); fixture.Progress(); fixture.Soul.FinishGather(); Update();
            Check((bool)Op()["harvest_verified"], "earlier stock harvest settled independently");
        }
        string wire = Queue(action);
        bool replaced = false;
        fixture.Role.OnFeatureRead = delegate
        {
            if (!Gui(State(), "owned", false)) return;
            fixture.Role.OnFeatureRead = null;
            replaced = true;
            Publish(wire.Replace("operation_id=" + Operation, "operation_id=" + Other));
        };
        Invoke("OnGUI"); Invoke("OnGUI");
        Check(replaced && Status(Ack(), "REJECTED") && (string)Ack()["command_name"] == "daily-soul-command" &&
            Gui(Result(), "not_invoked", false) && Gui(State(), "not_invoked", false) && !(bool)Op()["invoked"],
            "refused " + action + " reports no invocation on the exact command");
        Check(Status(Op(), "UNKNOWN") && (bool)Op()["mutation_attempted"] && (bool)Op()["start_verified"] &&
            (bool)Op()["harvest_verified"] == settled && (string)Soul()["ui_cleanup"] == "waiting" && Busy(),
            "prior mutation and owned panel retain the operation hold");
        Check(fixture.Client.Sent.Count == (settled ? 2 : 1) && MainUI.Closes == 0 && fixture.Soul.StartCalls == 1 &&
            fixture.Soul.HarvestCalls == (settled ? 1 : 0) && object.ReferenceEquals(fixture.Push, fixture.Handler(355).handler),
            "stock callbacks preserve prior evidence without replaying or closing");
        ExactProjection(); End(false);
    }
    private static void QueuedCapture(Fixture fixture, string change)
    {
        Open(fixture); Queue("start");
        int queued = (int)Soul()["revision"];
        if (change == "stable")
        {
            TimeUtil._now++; Update(); TimeUtil._now++; Update();
            Check((int)Soul()["revision"] == queued && Status(Op(), "QUEUED"), "ordinary time progression keeps the queued revision");
            Invoke("OnGUI"); Invoke("OnGUI");
            Check(Status(Ack(), "COMPLETED") && Gui(Result(), "completed", true) && fixture.Client.Sent.Count == 1,
                "stable queued samples allow exactly one 517 invocation");
            fixture.ReplyStart(); Update(); Step("harvest");
            fixture.ReplyHarvest(); fixture.Progress(); fixture.Soul.FinishGather(); Update(); Step("close");
            End(true);
            return;
        }

        int free = Global.Start.free_times;
        if (change == "quota") Global.Start.free_times = 257;
        else if (change == "role") fixture.Role.id++;
        else if (change == "cycle") { TimeUtil._thisDay += 86400; TimeUtil._now += 86400; fixture.Role.next_resource_time += 86400; }
        else throw new Exception(change);
        Update();
        int changed = (int)Soul()["revision"];
        Check(changed > queued && Status(Op(), "QUEUED"), "queued Update publishes the changed " + change + " revision");
        if (change == "quota") Global.Start.free_times = free;
        else if (change == "role") fixture.Role.id--;
        else { TimeUtil._thisDay -= 86400; TimeUtil._now -= 86400; fixture.Role.next_resource_time -= 86400; }
        Prove(fixture); Update();
        Check((int)Soul()["revision"] > changed && Status(Op(), "QUEUED"), "queued Update sees the original frame restored");

        Invoke("OnGUI"); Invoke("OnGUI");
        Check(Status(Ack(), "REJECTED") && (string)Ack()["reason"] == "daily_soul_queued_revision_changed" &&
            Gui(Result(), "not_invoked", false) && fixture.Client.Sent.Count == 0 && !(bool)Op()["mutation_attempted"] &&
            object.ReferenceEquals(fixture.Push, fixture.Handler(355).handler), "old revision stays rejected after the original frame returns");
        ExactProjection(); Update(); Update(); End(true);
    }
    private static void Bindings(Fixture fixture, string failure)
    {
        Open(fixture);
        bool harvest = failure == "harvest-arg" || failure == "harvest-life";
        if (harvest)
        {
            Step("start"); fixture.ReplyStart(); Update();
            fixture.Soul.OnHarvestReturn = delegate
            {
                Handler handler = fixture.Handler(518);
                if (failure == "harvest-arg") handler.arg = 1u;
                else handler.life = -1;
            };
        }
        if (failure == "355") fixture.Handler(355).life = 10;
        else if (!harvest) fixture.Soul.OnStartReturn = delegate
        {
            Handler handler = fixture.Handler(517);
            if (failure == "life") handler.life = 9;
            else if (failure == "arg") handler.arg = 2u;
            else if (failure == "target") handler.handler = delegate { };
        };
        Queue(harvest ? "harvest" : "start"); Invoke("OnGUI");
        Check(fixture.Client.Sent.Count == (harvest ? 2 : failure == "355" ? 0 : 1) &&
            Status(Ack(), failure == "355" ? "REJECTED" : "UNKNOWN") && object.ReferenceEquals(fixture.Push, fixture.Handler(355).handler),
            "reject foreign callback identity " + failure);
        if (failure == "355") { Update(); Update(); End(true); }
        else End(false);
    }
    private static void Readiness(Fixture fixture)
    {
        Observe();
        Check((string)Soul()["reason"] == "daily_refresh_unverified" && (string)Soul()["action"] == "", "no clock proof");
        Prove(fixture);
        CampaignModel.TestNavigation = true; Update();
        Check((string)Soul()["action"] == "", "prior business pauses soul");
        CampaignModel.TestNavigation = false; Update();
        Check((string)Soul()["action"] == "open", "clean role can open");
        fixture.Reward.acts[5u] = new Database.Activity { idactivity = 5, temp = Global.Activity }; Update();
        Check((string)Soul()["reason"] == "daily_soul_activity_cache_unverified", "dual caches must share the current row");
        fixture.Reward.acts[5u] = fixture.Role.acts[5u]; RootUI.m_bRefreshFive = true; Update();
        Check((string)Soul()["reason"] == "daily_refresh_pending" && fixture.Client.Sent.Count == 0, "refresh reset blocks mutations");
        End(true);
    }
    private static void Unobserved(Fixture fixture)
    {
        Observe();
        Dictionary<string, object> unavailable = Soul();
        int first = (int)unavailable["revision"];
        Check(first == 1 && (string)unavailable["character_id"] == "0" && (int)unavailable["cycle"] == 0 &&
            (string)unavailable["reason"] == "daily_soul_unobserved" && (string)unavailable["action"] == "" &&
            unavailable["current"] == null && unavailable["required"] == null && Status(Op(), "IDLE") && !Busy(),
            "unavailable first observation has valid revision and no claimed identity, progress or action");
        Observe();
        Check((int)Soul()["revision"] == first, "repeated unavailable observation preserves revision");
        Publish(ActionWire(++sequence, "open", first, Operation)); Update(); Invoke("OnGUI");
        Check(Status(Ack(), "REJECTED") && MainUI.Opens == 0 && fixture.Client.Sent.Count == 0,
            "valid unavailable receipt cannot admit an action");
        BaseAsset.lds.currmg = fixture.Root;
        Global.character_ = fixture.Role;
        RootUI.m_client = Global.m_client = fixture.Client;
        Prove(fixture); Observe();
        Check((int)Soul()["revision"] > first && (string)Soul()["action"] == "open", "restored owned state advances revision");
        End(true);
    }
    private static void InvalidProjection(Fixture fixture)
    {
        Observe(); int revision = (int)Soul()["revision"];
        Check((string)Soul()["reason"] == "daily_refresh_unverified", "no proof on first observation");
        fixture.Role.id = 43; Observe();
        Check((int)Soul()["revision"] > revision && (string)Soul()["character_id"] == "43" &&
            (string)Soul()["reason"] == "daily_refresh_unverified" && (string)Soul()["action"] == "", "same gate, new role and revision");
        revision = (int)Soul()["revision"];
        TimeUtil._thisDay += 86400; TimeUtil._now += 86400; fixture.Role.next_resource_time += 86400;
        Observe();
        Check((int)Soul()["revision"] > revision && (int)Soul()["cycle"] == 304400 &&
            (string)Soul()["reason"] == "daily_refresh_unverified" && (string)Soul()["action"] == "", "same gate, new cycle and revision");
        revision = (int)Soul()["revision"];

        Prove(fixture, 304400);
        Database.Activity row = (Database.Activity)fixture.Role.acts[5u];
        row.condition_param = 2; Observe();
        Check((int)Soul()["revision"] > revision && (string)Soul()["reason"] == "daily_soul_activity_cache_unverified" &&
            Soul()["current"] == null && Soul()["required"] == null && (string)Soul()["action"] == "", "over-count never reaches the receipt");
        revision = (int)Soul()["revision"];
        Global.Activity.cond_param = 256; Observe();
        Check((int)Soul()["revision"] == revision && Soul()["current"] == null && Soul()["required"] == null, "unchanged unavailable projection");
        Global.Activity.cond_param = 2; row.condition_param = 1; Observe();
        Check((int)Soul()["revision"] > revision && (int)Soul()["current"] == 1 && (int)Soul()["required"] == 2 &&
            (string)Soul()["action"] == "open" && fixture.Client.Sent.Count == 0, "valid projection resumes without a stock call");
        revision = (int)Soul()["revision"];
        fixture.Role.generalsoul_blue_times = 1;
        foreach (int free in new[] { 257, Int32.MinValue })
        {
            Global.Start.free_times = free; Observe();
            Check((int)Soul()["revision"] > revision && (int)Soul()["free_remaining"] == 0 &&
                (string)Soul()["reason"] == "daily_soul_free_mode_unverified" && (string)Soul()["action"] == "" &&
                fixture.Client.Sent.Count == 0, "invalid free quota blocks stock and preserves receipt schema");
        }
        End(true);
    }
    public static int Main(string[] args)
    {
        try
        {
            Fixture fixture = new Fixture(true);
            string name = args[0];
            bool noGameState = name == "no-game-state" || name == "disabled-no-game-state";
            if (noGameState || name == "root-unavailable") BaseAsset.lds.currmg = null;
            if (noGameState) { Global.character_ = null; RootUI.m_client = Global.m_client = null; }
            if (!Bootstrap(Nonce).StartsWith("BOOTSTRAP_OK;")) throw new Exception("bootstrap");
            Update(); Update(); Grammar();
            if (name == "disabled" || name == "disabled-no-game-state") Disabled(fixture, !noGameState);
            else if (name == "root-unavailable" || name == "no-game-state") Unobserved(fixture);
            else if (name == "invalid-projection") InvalidProjection(fixture);
            else if (name == "readiness") Readiness(fixture);
            else if (name == "progression" || name == "one-free") Progression(fixture, name == "one-free");
            else if (name.StartsWith("commit-")) FinalCommit(fixture, name.Substring(7));
            else if (name.StartsWith("binding-")) Bindings(fixture, name.Substring(8));
            else if (name.StartsWith("refused-")) RefusedAfterMutation(fixture, name.Substring(8));
            else if (name.StartsWith("queued-")) QueuedCapture(fixture, name.Substring(7));
            else StopAndDestroy(fixture, name);
            Console.WriteLine(Json.Serialize(new { status = "PASS", scenario = name, assertions = checks, game_access = false }));
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
