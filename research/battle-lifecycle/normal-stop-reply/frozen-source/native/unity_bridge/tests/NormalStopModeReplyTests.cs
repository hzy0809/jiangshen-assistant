using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using JiangShen.AssistantRuntime;
using UnityEngine;
using D = JiangShen.AssistantRuntime.SharedDevelopmentContext;
using W = BattleReplyPreflightWire;

// Production mode models, BattleLaunch and named IPC; detached stock UI/client.
// Each scenario runs in a fresh process and drives the public model entrypoints.
internal static class NormalStopModeReplyTests
{
    private const string Operation = "00000000000000000000000000000073";
    private static int checks;
    private static void Check(bool value, string why)
    { checks++; if (!value) throw new Exception(why); }
    private sealed class Fixture : IDisposable
    {
        internal readonly bool Point;
        internal readonly string Nonce = Guid.NewGuid().ToString("N"), Epoch = Guid.NewGuid().ToString("N");
        internal readonly Mutex Fence;
        internal readonly EventWaitHandle Cancel;
        internal readonly game.net.tcp_client Client;
        internal readonly RootMessenger Home, Scene;
        internal readonly RootUI Root;
        internal readonly ArenaUI Arena;
        internal readonly PointraceUI Race;
        internal readonly BattleUI Battle;
        internal readonly BattleNormalResultUI Result;
        internal readonly DailyArenaBattleModel ArenaModel;
        internal readonly DailyPointRaceModel PointModel;
        internal readonly BattleSettlement Settlement;
        private readonly UnityEngine.Object[] all;
        private int sequence;
        internal bool LeaseAlive = true;
        internal Fixture(bool point)
        {
            Point = point;
            Fence = new Mutex(false, "Local\\JSAssistantBattleLaunch-" + Nonce);
            Cancel = new EventWaitHandle(false, EventResetMode.ManualReset, "Local\\JSAssistantBattleCancel-" + Nonce + "-" + Epoch);
            GameObject.Paths.Clear(); Global.Templates.Clear(); BattleFixtureTrace.Events.Clear();
            Global.character_ = new Database.Character { next_resource_time = 150000 };
            Global.relogin = false; Global.extern_enabled = true; Global.ActivityCalls = 0;
            TimeUtil._now = 106400; TimeUtil._thisDay = 86400; RootUI.m_bRefreshFive = false;
            Client = new game.net.tcp_client(); Global.m_client = RootUI.m_client = Client;
            game.net.tcp_base.connection_state = 3;
            Home = new RootMessenger { LevelID = "Campaign" }; Home.gameObject.Attach(Home);
            Scene = point ? new RootMessenger { LevelID = "PointRace" } : Home; Scene.gameObject.Attach(Scene);
            Root = Home.gameObject.Attach(new RootUI { rmg = Home, level_rmg = Scene }); BaseUI.rootUI = Root;
            Home.gameObject.Attach(new MainUI { rmg = Home });
            ArenaSelectUI select = Home.gameObject.Attach(new ArenaSelectUI { rmg = Home, enabled = false });
            BattleValueCompareUI compare = Home.gameObject.Attach(new BattleValueCompareUI { rmg = Home, enabled = false });
            Arena = Home.gameObject.Attach(new ArenaUI { rmg = Home, enabled = !point });
            Race = new PointraceUI { rmg = Scene, m_pointrace = Global.character_.pointrace, enabled = point };
            Race.transform.parent = Scene.transform; Race.gameObject.Attach(Race);
            GameObject.Paths["/PointRace"] = Scene.gameObject; GameObject.Paths["/PointRace/PointRaceUI"] = Race.gameObject;
            RootMessenger battleRoot = new RootMessenger { LevelID = "Battle" }; battleRoot.gameObject.Attach(battleRoot);
            Battle = new BattleUI { rmg = battleRoot, m_prevUI = Scene, enabled = false };
            Battle.transform.parent = battleRoot.transform; Battle.gameObject.Attach(Battle);
            Result = Battle.gameObject.Attach(new BattleNormalResultUI { rmg = battleRoot, battle_ui = Battle, enabled = false });
            GameObject.Paths["/Battle"] = GameObject.Paths["Battle"] = battleRoot.gameObject;
            GameObject.Paths["/Battle/BattleUI"] = GameObject.Paths["Battle/BattleUI"] = Battle.gameObject;
            BattleModeFixtureObjects.Battle = Battle; BattleModeFixtureObjects.Result = Result; BattleModeFixtureObjects.PointRace = point ? Race : null;
            BaseAsset.lds = new Loader { currmg = Scene, oldmainrmg = Home, isBattling = false, enabled = false };
            MainUI.m_subui = point ? null : (object)Arena; __BaseUI.battle_result_ui = null;
            ActivityTemplate template = new ActivityTemplate(); ActivityRow row = new ActivityRow { temp = template };
            ActivityCategory category = new ActivityCategory(); category.acts[(uint)9] = row;
            Global.character_.acts[(uint)9] = row; Global.character_.act_rewards[(uint)3] = category;
            Global.Templates["activity:9"] = template;
            Global.character_.arena.challengers.Add(new ArenaChallenger());
            Global.character_.pointrace.challengers.Add(new PointRaceChallenger());
            all = new UnityEngine.Object[] { Home, Root, select, compare, Arena, Scene, Race, Battle, Result };
            ArenaModel = new DailyArenaBattleModel(); PointModel = new DailyPointRaceModel();
            Settlement = new BattleSettlement(delegate(string name, string fields) { }, delegate { return LeaseAlive; });
            BattleLaunch.Initialize(Nonce, delegate { return LeaseAlive; });
        }
        internal ushort Opcode { get { return (ushort)(Point ? 0x465 : 0x460); } }
        internal int Callbacks { get { return Point ? Race.CallbackCalls : Arena.CallbackCalls; } }
        internal int Applied { get { return Point ? Race.Applied : Arena.Applied; } }
        internal Component Source { get { return Point ? (Component)Race : Arena; } }
        internal object Target { get { return Point ? Global.character_.pointrace.challengers[0] : Global.character_.arena.challengers[0]; } }
        internal string Json { get { return Point ? PointModel.Json() : ArenaModel.Json(); } }
        internal int Revision { get { return Point ? (int)typeof(DailyPointRaceModel).GetField("revision", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(PointModel) : ArenaModel.Revision; } }
        internal D Context() { return new D(all, Scene, Root, Global.character_, "78", "PASS", false, delegate { return null; }); }
        internal void Capture() { if (Point) PointModel.Capture(Context()); else ArenaModel.Capture(Context()); }
        internal void Action(string action)
        {
            string why; int seq = ++sequence;
            BattleLaunch.Select(new Command { Name = "daily-script-command", OperationId = Operation, Sequence = seq, BattleEpoch = Epoch });
            bool queued = Point ? PointModel.Queue(109, 78, Operation, seq, Revision, action, out why) :
                ArenaModel.Queue(9, 78, Operation, seq, ArenaModel.Revision, action, out why);
            Check(queued, "queue " + action + ": " + why + " " + Json);
            bool invoked = Point ? PointModel.ExecuteGui(Context, delegate { },
                delegate(DailyPointRaceInvocationCheck sample, DailyPointRaceInvocationCheck check) { return sample() && check(); }, out why) :
                ArenaModel.ExecuteGui(Context, delegate { },
                delegate(DailyArenaBattleInvocationCheck sample, DailyArenaBattleInvocationCheck check) { return sample() && check(); }, out why);
            Check(invoked, "execute " + action + ": " + why + " " + Json);
        }
        internal game.net.tcp_base.Handler Start()
        {
            Capture();
            if (Point) { Action("resume"); Capture(); }
            else
            {
                Action("refresh"); Client.Dispatch(0x463, new byte[] { 8, 0 }); Capture();
                Client.m_handlers[(ushort)355] = new game.net.tcp_base.Handler { handler = Global.handelActvity };
            }
            Action("challenge");
            Check(BattleLaunch.Json().Contains("\"battle_request_observed\":true"), "real guard observed native request: " + BattleLaunch.Json());
            return (game.net.tcp_base.Handler)Client.m_handlers[Opcode];
        }
        internal void Stop(string reason)
        {
            // Probe.Finish ordering: controller cancel/drain, launch fence, models.
            Cancel.Set(); LeaseAlive = false;
            Check(!BattleLaunch.Stop(), "already-sent request remains unresolved at Stop");
            Check(Settlement.Stop(), "Finish stops the idle production settlement owner before the models");
#if LEGACY_STOP_API
            if (Point) PointModel.Stop(); else ArenaModel.Stop();
#else
            if (Point) PointModel.Stop(reason); else ArenaModel.Stop(reason);
#endif
        }
        internal void NoAutomation()
        {
            Capture(); string why;
            foreach (string action in new string[] { "challenge", "continue", "skip", "battle_close" })
            {
                bool queued = Point ? PointModel.Queue(109, 78, Operation, 99, Revision, action, out why) :
                    ArenaModel.Queue(9, 78, Operation, 99, ArenaModel.Revision, action, out why);
                Check(!queued, "stop never admits " + action);
            }
            Check(Client.Sent.Count == (Point ? 1 : 2) && Client.EndRequests == 0 && Battle.OpenCalls == 0,
                "stop emits no next request or automatic result window");
            Check(Global.character_.gold == 100 && Global.character_.bind_gold == 200 && Global.character_.gold_consume == 0,
                "premium balances unchanged");
        }
        public void Dispose() { Cancel.Dispose(); Fence.Dispose(); }
    }
    private static void Run(bool point, string scenario)
    {
        using (Fixture f = new Fixture(point))
        {
            game.net.tcp_base.Handler slot = f.Start(); DevelopmentNormalCallback saved = slot.handler;
            bool win = !scenario.Contains("loss");
            byte[] good = point ? W.Point(271, 271, 987, 0, win) : W.Arena(271, 987, 0, win);
            byte[] body = good; bool accepted = true; object arg = null; MemoryStream expected = null;
            if (scenario == "retired-wrapper")
            {
                f.Client.Dispatch(f.Opcode, good); f.Capture(); f.Action("skip");
                Check(BattleLaunch.Json().Contains("REPORT_TRANSFERRED"), "real guard transferred completed first report");
                string epoch = Guid.NewGuid().ToString("N");
                using (EventWaitHandle cancel = new EventWaitHandle(false, EventResetMode.ManualReset, "Local\\JSAssistantBattleCancel-" + f.Nonce + "-" + epoch))
                {
                    BattleLaunch.Select(new Command { Name = "daily-script-command", OperationId = Operation, Sequence = 77, BattleEpoch = epoch });
                    BattleLaunch.Direct(point ? BattleMode.PointRace : BattleMode.Arena, f.Source, f.Target, delegate { return true; },
                        delegate { D.Call(f.Source, point ? "WantChallenge" : "cs_arena_challenge_0x460"); }, f.Opcode);
                    object replacement = f.Client.m_handlers[f.Opcode]; saved(new MemoryStream(good, false), null);
                    Check(f.Callbacks == 1 && f.Applied == 1 && ReferenceEquals(f.Client.m_handlers[f.Opcode], replacement),
                        "retired old wrapper cannot apply again or replace new epoch handler");
                    Console.WriteLine("{\"scenario\":\"retired-wrapper\",\"old_callbacks\":1,\"new_handler_retained\":true,\"real_launch\":" + BattleLaunch.Json() + "}");
                    return;
                }
            }
            if (scenario.StartsWith("during-", StringComparison.Ordinal))
            {
                Action stop = delegate { f.Stop("stop"); };
                if (point) f.Race.DuringCallback = stop; else f.Arena.DuringCallback = stop;
            }
            else if (scenario == "after-reply") { f.Client.Dispatch(f.Opcode, good); expected = point ? f.Race.CallbackStream : f.Arena.CallbackStream; f.Stop("stop"); }
            else
            {
                if (scenario == "unknown-before-stop")
                { saved(new MemoryStream(point ? W.Point(272, 272) : W.Arena(272), false), null); accepted = false; }
                f.Stop(scenario == "abnormal-stop" ? "lease_expired" : "stop");
            }
            if (scenario == "abnormal-stop") accepted = false;
            else if (scenario == "bad-report") { body = point ? W.Point(271, 271, 987, 0, true, true, new byte[] { 8, 1 }) : W.Arena(271, 987, 0, true, true, new byte[] { 8, 1 }); accepted = false; }
            else if (scenario == "wrong-target") { body = point ? W.Point(272, 272) : W.Arena(272); accepted = false; }
            else if (scenario == "premium") { body = point ? W.Point(271, 271, 987, 5) : W.Arena(271, 987, 5); accepted = false; }
            else if (scenario == "timeout") { body = null; accepted = false; }
            else if (scenario == "role-object") { Global.character_ = new Database.Character(); accepted = false; }
            else if (scenario == "role-id") { Global.character_.id++; accepted = false; }
            else if (scenario == "target-object")
            { if (point) Global.character_.pointrace.challengers[0] = new PointRaceChallenger(); else Global.character_.arena.challengers[0] = new ArenaChallenger(); accepted = false; }
            else if (scenario == "target-id")
            { if (point) ((PointRaceChallenger)f.Target).id++; else ((ArenaChallenger)f.Target).Id++; accepted = false; }
            else if (scenario == "target-index") { if (point) f.Race.m_challengeIdx++; else f.Arena.m_challengeIdx++; accepted = false; }
            else if (scenario == "socket") { f.Client.m_client = new game.net.FixtureSocket(); accepted = false; }
            else if (scenario == "transport-stream") { f.Client.m_stream = new MemoryStream(); accepted = false; }
            else if (scenario == "client") { Global.m_client = RootUI.m_client = new game.net.tcp_client(); accepted = false; }
            else if (scenario == "loader") { BaseAsset.lds = new Loader { currmg = f.Scene, isBattling = false, enabled = false }; accepted = false; }
            else if (scenario == "root") { BaseAsset.lds.currmg = new RootMessenger(); accepted = false; }
            else if (scenario == "source-root") { ((__BaseUI)f.Source).rmg = new RootMessenger(); accepted = false; }
            else if (scenario == "handler") { f.Client.m_handlers[f.Opcode] = new game.net.tcp_base.Handler(); accepted = false; }
            else if (scenario == "handler-callback") { slot.handler = delegate { }; accepted = false; }
            else if (scenario == "handler-table") { typeof(game.net.tcp_client).GetField("m_handlers").SetValue(f.Client, new Hashtable()); accepted = false; }
            else if (scenario == "handler-arg") { slot.arg = new object(); accepted = false; }
            else if (scenario == "arg") { arg = new object(); accepted = false; }
            else if (scenario == "cycle") { TimeUtil._thisDay += 86400; TimeUtil._now += 86400; accepted = false; }
            else if (scenario == "new-epoch-blocked")
            {
                BattleLaunch.Initialize(f.Nonce, delegate { return true; });
                BattleLaunch.Select(new Command { Name = "daily-script-command", OperationId = Operation, Sequence = 77, BattleEpoch = Guid.NewGuid().ToString("N") });
                int started = 0;
                try { BattleLaunch.Direct(point ? BattleMode.PointRace : BattleMode.Arena, f.Source, f.Target, delegate { return true; }, delegate { started++; }, f.Opcode); throw new Exception("new battle unexpectedly admitted"); }
                catch (ProbeFailure error) { Check(error.Message == "battle_launch_prior_pending_or_unknown", "real old request blocks new epoch: " + error.Message); }
                Check(started == 0, "new epoch executes no native request");
            }
            MemoryStream stream = body == null ? null : new MemoryStream(body, false);
            if (ReferenceEquals(f.Client.m_handlers[f.Opcode], slot)) f.Client.m_handlers.Remove(f.Opcode);
            saved(stream, arg);
            f.NoAutomation();
            Console.WriteLine("{\"stage\":\"after-native-callback\",\"mode\":" + Command.Quote(point ? "PointRace" : "Arena") +
                ",\"scenario\":" + Command.Quote(scenario) + ",\"stock_callbacks\":" + f.Callbacks + ",\"stock_applied\":" + f.Applied +
                ",\"sent_requests\":" + f.Client.Sent.Count + ",\"end_requests\":" + f.Client.EndRequests + ",\"real_launch\":" + BattleLaunch.Json() + "}");
            Check(f.Callbacks == (accepted ? 1 : 0) && f.Applied == (accepted ? 1 : 0),
                "expected already-sent native application exactly once: " + scenario + " " + f.Json);
            if (accepted)
            {
                Check(ReferenceEquals(point ? f.Race.CallbackStream : f.Arena.CallbackStream, expected ?? stream) &&
                    (point ? f.Race.CallbackPosition : f.Arena.CallbackPosition) == 0 && (point ? f.Race.CallbackArg : f.Arena.CallbackArg) == null,
                    "stock receives the exact original MemoryStream, arg and initial cursor");
                game.net.data.StoC.BattleReport report = (game.net.data.StoC.BattleReport)f.Battle.interpreter.m_report;
                Check(report.result_status == (win ? game.net.data.StoC.BattleReport.ResultStatus.LEFT_WIN : game.net.data.StoC.BattleReport.ResultStatus.LEFT_LOSE), "native report keeps server win/loss");
                Check(ReferenceEquals(f.Battle.OnClose.Target, f.Source) && ReferenceEquals(f.Battle.m_prevUI, f.Scene) && f.Battle.m_parentUI == null,
                    "native callback keeps original mode and destination");
                D.Call(f.Battle, "OpenResultWindow"); f.Battle.battle_exit();
                Check(f.Battle.ResultCalls == 1 && (point ? f.Race.Returns : f.Arena.Returns) == 1 && ReferenceEquals(BaseAsset.lds.currmg, f.Scene),
                    "manual native result/exit can still return original page after stop");
                Check(f.Client.EndRequests == 0 && f.Client.Sent.Count == (point ? 2 : 2), "native PointRace list refresh is the only manual exit packet");
            }
            saved(new MemoryStream(good, false), null);
            Check(f.Callbacks == (accepted ? 1 : 0), "retained wrapper cannot replay after success/failure/timeout");
            Check(f.Json.Contains("\"status\":\"UNKNOWN\""), "stopped automation remains unresolved without claiming business completion");
            Console.WriteLine("{\"scenario\":" + Command.Quote(scenario) + ",\"mode\":" + Command.Quote(point ? "PointRace" : "Arena") +
                ",\"stock_callbacks\":" + f.Callbacks + ",\"native_result_verified\":" + (accepted ? "true" : "false") +
                ",\"model\":" + f.Json + ",\"real_launch\":" + BattleLaunch.Json() + "}");
        }
    }
    private static int Main(string[] args)
    {
        try { Run(args[0] == "point", args[1]); Console.WriteLine("{\"status\":\"PASS\",\"checks\":" + checks + ",\"game_access\":false,\"real_named_ipc\":true}"); return 0; }
        catch (Exception error) { Console.WriteLine(error); Console.WriteLine("{\"status\":\"FAIL\",\"checks\":" + checks + "}"); return 1; }
    }
}
