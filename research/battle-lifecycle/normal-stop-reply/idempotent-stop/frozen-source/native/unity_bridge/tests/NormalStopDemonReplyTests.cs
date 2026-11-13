using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Threading;
using ProtoBuf;
using game.net.data.StoC;
using JiangShen.AssistantRuntime;
using UnityEngine;

// Real BattleLaunch formation 298/attack/0x492 path with detached native UI.
internal static class NormalStopDemonReplyTests
{
    private const string Operation = "00000000000000000000000000000074";
    private static int checks;
    private static void Check(bool value, string why) { checks++; if (!value) throw new Exception(why); }
    private static byte[] Serialize<T>(T value)
    { using (MemoryStream output = new MemoryStream()) { Serializer.Serialize(output, value); return output.ToArray(); } }
    private static byte[] Packet(bool win, byte[] reportOverride = null)
    {
        BattleReport report = new BattleReport
        {
            result_status = win ? BattleReport.ResultStatus.LEFT_WIN : BattleReport.ResultStatus.LEFT_LOSE,
            left_army = new BattleReport.ArmyInfo { name = "role", level = 30, npc = false, initiative = 1 },
            right_army = new BattleReport.ArmyInfo { name = "demon", level = 30, npc = true, initiative = 1 }
        };
        byte[] compressed;
        using (MemoryStream output = new MemoryStream())
        {
            using (Ionic.Zlib.DeflateStream zip = new Ionic.Zlib.DeflateStream(output, Ionic.Zlib.CompressionMode.Compress, true))
            { byte[] plain = Serialize(report); zip.Write(plain, 0, plain.Length); }
            compressed = output.ToArray();
        }
        SCDemonBattle_0x492 packet = new SCDemonBattle_0x492 { result = 0, iswin = win, report = reportOverride ?? compressed };
        PropertyInfo cooldown = packet.GetType().GetProperty("cooldown");
        object cd = Activator.CreateInstance(cooldown.PropertyType);
        foreach (object[] pair in new object[][] { new object[] { "id", 5 }, new object[] { "sub_id", 0 }, new object[] { "time", 106500 }, new object[] { "in_cd", true } })
        {
            PropertyInfo field = cd.GetType().GetProperty((string)pair[0]); field.SetValue(cd, Convert.ChangeType(pair[1], field.PropertyType), null);
        }
        cooldown.SetValue(packet, cd, null);
        PropertyInfo bonuses = packet.GetType().GetProperty("bonuses"); bonuses.SetValue(packet, Activator.CreateInstance(bonuses.PropertyType), null);
        packet.bonuses.bonuses.Add(new Treasure { idtreasure_type = 1, param0 = 10, param1 = 0, total_value = 10 });
        return Serialize(packet);
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly string Nonce = Guid.NewGuid().ToString("N"), Epoch = Guid.NewGuid().ToString("N");
        internal readonly Mutex Fence;
        internal readonly EventWaitHandle Cancel;
        internal readonly RootMessenger Home;
        internal readonly Model Environment;
        internal readonly DailyMonsterModel Model;
        internal readonly Database.Character Character;
        internal readonly Database.Demon Target;
        internal readonly DemonScript Script;
        internal readonly BattleUI Battle;
        internal readonly BattleNormalResultUI Result;
        internal readonly BattleResultUI Reward;
        internal readonly FormationUI Formation;
        internal readonly game.net.tcp_client Client;
        internal game.net.tcp_base.Handler Handler;
        internal DevelopmentNormalCallback Wrapper;
        private int sequence;
        private bool lease = true;
        internal Fixture()
        {
            Fence = new Mutex(false, "Local\\JSAssistantBattleLaunch-" + Nonce);
            Cancel = new EventWaitHandle(false, EventResetMode.ManualReset, "Local\\JSAssistantBattleCancel-" + Nonce + "-" + Epoch);
            GameObject.Paths.Clear(); BattleSettlement.Presentations = 0;
            TimeUtil._now = 106400; TimeUtil._thisDay = 86400;
            Global.relogin = false; Global.extern_enabled = true; RootUI.m_bRefreshFive = false;
            Character = new Database.Character(); Global.character_ = Character; Character.acts[(uint)24] = new DemonActivity();
            Client = new game.net.tcp_client(); Global.m_client = RootUI.m_client = Client; game.net.tcp_base.connection_state = 3;
            Home = new RootMessenger(); Home.gameObject.Attach(Home);
            RootUI rootUi = Home.gameObject.Attach(new RootUI { rmg = Home }); Home.gameObject.Attach(new MainUI { rmg = Home });
            BaseUI.rootUI = rootUi; MainUI.m_subui = null; __BaseUI.battle_result_ui = null;
            Target = new Database.Demon { mesh = new GameObject() }; Character.m_demon = Target; Target.mesh.transform.parent = Home.transform;
            Script = Target.mesh.Attach(new DemonScript { rmg = Home, m_demon = Target });
            Formation = new FormationUI { rmg = Home, enabled = false }; Formation.gameObject.Attach(Formation);
            Battle = new BattleUI { rmg = new RootMessenger { LevelID = "Battle" }, enabled = false }; Battle.gameObject.Attach(Battle);
            GameObject.Paths["Battle/BattleUI"] = Battle.gameObject;
            Result = Battle.gameObject.Attach(new BattleNormalResultUI { rmg = Battle.rmg, enabled = false });
            Reward = new BattleResultUI { rmg = Home, enabled = false }; Reward.gameObject.Attach(Reward);
            BaseAsset.lds = new Loader { enabled = false, isBattling = false, currmg = Home };
            Environment = new Model { Root = Home, Battle = Battle.gameObject,
                All = new UnityEngine.Object[] { Home, rootUi, Script, Formation, Battle, Result, Reward } };
            DemonFixtureWorld.Model = Environment; DemonFixtureWorld.Formation = Formation; DemonFixtureWorld.Battle = Battle; DemonFixtureWorld.Reward = Reward;
            Model = new DailyMonsterModel(); BattleLaunch.Initialize(Nonce, delegate { return lease; });
        }
        internal void Capture() { Model.Capture(Environment, 24, false); }
        internal void Action(string action)
        {
            string why; int seq = ++sequence;
            BattleLaunch.Select(new Command { Name = "daily-script-command", OperationId = Operation, Sequence = seq, BattleEpoch = Epoch });
            Check(Model.Queue(24, 78, Operation, seq, Model.Revision, action, out why), "queue " + action + ": " + why + " " + Model.Json());
            Check(Model.ExecuteGui(delegate { }, delegate { return true; }, out why), "execute " + action + ": " + why);
        }
        internal void Start()
        {
            Capture(); Action("open"); Capture(); Action("prepare"); Capture(); Action("start");
            Check(Client.Sent.Count == 1 && Client.Sent[0] == 298, "real guard waits behind native formation 298");
            Client.Dispatch(298, new byte[] { 8, 0 });
            Check(Client.Sent.Count == 2 && Client.Sent[1] == 0x492 && BattleLaunch.Json().Contains("\"battle_request_observed\":true"),
                "real guard admits one captured formation attack and observes only own-demon 0x492: " + BattleLaunch.Json());
            Handler = (game.net.tcp_base.Handler)Client.m_handlers[(ushort)0x492]; Wrapper = Handler.handler;
        }
        internal void Stop(string reason)
        {
            Cancel.Set(); lease = false;
            Check(!BattleLaunch.Stop(), "real launch remains unresolved when stopped after send"); Model.Stop(reason);
        }
        internal void NoAutomation()
        {
            Capture(); string why;
            foreach (string action in new string[] { "open", "prepare", "start", "close_battle", "close_result" })
                Check(!Model.Queue(24, 78, Operation, 99, Model.Revision, action, out why), "stopped model refuses " + action);
            Check(!Model.TryPresentSettledBattle(Battle) && BattleSettlement.Presentations == 0 && !Result.enabled && !Reward.enabled,
                "stopped model never presents or automatically returns/rewards");
            Check(Client.Sent.Count == 2, "only native formation 298 and initial 0x492; no extra battle or end request");
        }
        public void Dispose() { Cancel.Dispose(); Fence.Dispose(); }
    }
    private static void Run(string scenario)
    {
        using (Fixture f = new Fixture())
        {
            f.Start(); bool win = !scenario.Contains("loss"), accepted = true;
            byte[] good = Packet(win), body = good; object arg = null; MemoryStream expected = null;
            if (scenario.StartsWith("during-", StringComparison.Ordinal)) f.Script.DuringCallback = delegate
            {
                f.Stop("stop");
                if (scenario.StartsWith("during-repeat-", StringComparison.Ordinal)) f.Stop("stop");
            };
            else if (scenario == "after-reply") { expected = f.Client.Dispatch(0x492, good); f.Stop("stop"); }
            else
            {
                if (scenario == "unknown-before-stop") { f.Wrapper(new MemoryStream(Packet(true, new byte[] { 8, 1 }), false), null); accepted = false; }
                f.Stop(scenario == "abnormal-stop" || scenario == "abnormal-then-stop" ? "lease_expired" : "stop");
            }
            if (scenario == "repeat-stop-win" || scenario == "repeat-stop-loss" || scenario == "repeat-stop-target-change") f.Stop("stop");
            else if (scenario == "stop-then-abnormal" || scenario == "stop-abnormal-stop")
            {
                f.Stop("lease_expired");
                if (scenario == "stop-abnormal-stop") f.Stop("stop");
                accepted = false;
            }
            else if (scenario == "abnormal-then-stop") { f.Stop("stop"); accepted = false; }
            else if (scenario == "stop-timeout-stop" || scenario == "stop-invalid-stop")
            {
                f.Wrapper(scenario == "stop-timeout-stop" ? null : new MemoryStream(Packet(true, new byte[] { 8, 1 }), false), null);
                f.Capture(); f.Stop("stop"); accepted = false;
            }
            else if (scenario == "stop-target-invalid-stop")
            {
                f.Target.clear_time++; f.Wrapper(new MemoryStream(good, false), null); f.Capture(); f.Target.clear_time--;
                f.Stop("stop"); accepted = false;
            }
            else if (scenario == "stop-valid-stop-duplicate" || scenario == "stop-duplicate-stop")
            {
                expected = new MemoryStream(good, false); f.Wrapper(expected, null);
                if (scenario == "stop-duplicate-stop") { f.Wrapper(new MemoryStream(good, false), null); f.Capture(); }
                f.Stop("stop");
            }
            if (scenario == "repeat-stop-target-change") { f.Target.clear_time++; accepted = false; }
            if (scenario == "abnormal-stop") accepted = false;
            else if (scenario == "bad-report") { body = Packet(win, new byte[] { 8, 1 }); accepted = false; }
            else if (scenario == "wrong-target" || scenario == "target-object") { f.Character.m_demon = new Database.Demon(); accepted = false; }
            else if (scenario == "premium") { f.Character.gold--; accepted = false; }
            else if (scenario == "timeout") { body = null; accepted = false; }
            else if (scenario == "role-object") { Global.character_ = new Database.Character(); accepted = false; }
            else if (scenario == "role-id") { f.Character.id++; accepted = false; }
            else if (scenario == "target-id") { f.Target.clear_time++; accepted = false; }
            else if (scenario == "socket") { f.Client.m_client = new game.net.FixtureSocket(); accepted = false; }
            else if (scenario == "transport-stream") { f.Client.m_stream = new MemoryStream(); accepted = false; }
            else if (scenario == "client") { Global.m_client = RootUI.m_client = new game.net.tcp_client(); accepted = false; }
            else if (scenario == "loader") { BaseAsset.lds = new Loader { currmg = f.Home, isBattling = false, enabled = false }; accepted = false; }
            else if (scenario == "root") { BaseAsset.lds.currmg = new RootMessenger(); accepted = false; }
            else if (scenario == "source-root") { f.Script.rmg = new RootMessenger(); accepted = false; }
            else if (scenario == "handler") { f.Client.m_handlers[(ushort)0x492] = new game.net.tcp_base.Handler(); accepted = false; }
            else if (scenario == "handler-callback") { f.Handler.handler = delegate { }; accepted = false; }
            else if (scenario == "handler-table") { f.Client.m_handlers = new Hashtable(); accepted = false; }
            else if (scenario == "handler-arg") { f.Handler.arg = new object(); accepted = false; }
            else if (scenario == "arg") { arg = new object(); accepted = false; }
            else if (scenario == "cycle") { TimeUtil._thisDay += 86400; TimeUtil._now += 86400; accepted = false; }
            else if (scenario == "new-epoch-blocked")
            {
                BattleLaunch.Initialize(f.Nonce, delegate { return true; });
                BattleLaunch.Select(new Command { Name = "daily-script-command", OperationId = Operation, Sequence = 77, BattleEpoch = Guid.NewGuid().ToString("N") });
                int sent = 0;
                try { BattleLaunch.Direct(BattleMode.OwnDemon, f.Script, f.Target, delegate { return true; }, delegate { sent++; }, 0x492); throw new Exception("new epoch unexpectedly admitted"); }
                catch (ProbeFailure error) { Check(error.Message == "battle_launch_prior_pending_or_unknown", "old unresolved 0x492 blocks new epoch: " + error.Message); }
                Check(sent == 0, "new epoch never reaches native send");
            }
            MemoryStream stream = body == null ? null : new MemoryStream(body, false);
            if (ReferenceEquals(f.Client.m_handlers[(ushort)0x492], f.Handler)) f.Client.m_handlers.Remove((ushort)0x492);
            f.Wrapper(stream, arg); f.NoAutomation();
            Console.WriteLine("{\"stage\":\"after-native-callback\",\"mode\":\"OwnDemon\",\"scenario\":" + Command.Quote(scenario) +
                ",\"stock_callbacks\":" + f.Script.CallbackCalls + ",\"target_killed\":" + (f.Target.killed ? "true" : "false") +
                ",\"reward_configured\":" + f.Reward.Configured + ",\"cooldown_count\":" + f.Character.cooldowns.Count + ",\"real_launch\":" + BattleLaunch.Json() + "}");
            Check(f.Script.CallbackCalls == (accepted ? 1 : 0), "expected stock callback count: " + scenario + " " + f.Model.Json());
            if (accepted)
            {
                Check(ReferenceEquals(f.Script.CallbackStream, expected ?? stream) && f.Script.CallbackPosition == 0 && f.Script.CallbackArg == null,
                    "stock receives the original MemoryStream, cursor and arg");
                Check(f.Target.killed == win && f.Reward.Configured == (win ? 1 : 0) && f.Target.ArmyUpdates == (win ? 0 : 1),
                    "stock retains native win reward and non-win soldier behavior");
                Check(f.Battle.interpreter.m_report.result_status == (win ? BattleReport.ResultStatus.LEFT_WIN : BattleReport.ResultStatus.LEFT_LOSE) &&
                    ReferenceEquals(f.Battle.m_prevUI, f.Home) && f.Battle.m_parentUI == null &&
                    f.Model.Json().Contains("\"stock_callback_returned\":true") && f.Model.Json().Contains("\"receipt_report_verified\":true") &&
                    BattleLaunch.Json().Contains("\"initial_response_applied\":true"), "actual report/result binds to the cancelled real launch once");
                f.Battle.battle_exit();
                Check(ReferenceEquals(f.Environment.Root, f.Home) && f.Battle.ExitCalls == 1 && (win ? f.Reward.enabled : f.Home.Enables == 1),
                    "manual native battle exit keeps the original own-demon destination/reward callback");
                if (win) { f.Reward.OnClose(); Check(f.Script.Returns == 1 && f.Home.Enables == 1, "manual native reward callback returns home"); }
                Check(f.Client.Sent.Count == 2 && f.Character.gold == 100 && f.Character.bind_gold == 200 && f.Character.gold_consume == 0,
                    "late valid settlement preserves premium without another battle or end request");
            }
            else Check(!f.Target.killed && f.Reward.Configured == 0 && f.Character.cooldowns.Count == 0,
                "invalid/changed request never mutates stock settlement state");
            f.Wrapper(new MemoryStream(good, false), null); Check(f.Script.CallbackCalls == (accepted ? 1 : 0), "retained wrapper never replays");
            Check(f.Model.OutcomeUnknown, "stopped automation does not claim completion");
            Console.WriteLine("{\"scenario\":" + Command.Quote(scenario) + ",\"stock_callbacks\":" + f.Script.CallbackCalls +
                ",\"model\":" + f.Model.Json() + ",\"real_launch\":" + BattleLaunch.Json() + "}");
        }
    }
    private static int Main(string[] args)
    {
        try { Run(args[0]); Console.WriteLine("{\"status\":\"PASS\",\"checks\":" + checks + ",\"game_access\":false,\"real_named_ipc\":true}"); return 0; }
        catch (Exception error) { Console.WriteLine(error); Console.WriteLine("{\"status\":\"FAIL\",\"checks\":" + checks + "}"); return 1; }
    }
}
