using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using ProtoBuf;
using UnityEngine;
using JiangShen.AssistantRuntime;
using D = JiangShen.AssistantRuntime.SharedDevelopmentContext;

// Black-box Queue/Capture/ExecuteGui mode routes. Reflection is used only to
// inspect the private shared lifecycle, never to manufacture mode ownership.
internal static partial class BattleModeAdapterTests
{
    private const string Operation = "00000000000000000000000000000031";
    private static int checks;
    private static void Check(bool condition, string message)
    { checks++; if (!condition) throw new Exception(message); }
    private static object Field(object value, string name)
    { return value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(value); }
    private static BattleLifecycle State(Fixture f)
    { object attempt = Field(f.Owner, "attempt"); return attempt == null ? null : (BattleLifecycle)Field(attempt, "State"); }
    private static byte[] Packet<T>(T value)
    { using (MemoryStream stream = new MemoryStream()) { Serializer.Serialize(stream, value); return stream.ToArray(); } }
    private static byte[] ArenaReceipt()
    {
        // Fixed wire fixture: result=0, win=true, nonempty report, reportId=987,
        // arena_report.report=(opponent 271, report 987), required envelope name.
        // Keep this independent of the fake packet serializer and production decoder.
        return BattleReplyPreflightWire.Arena();
    }
    private static byte[] GraveReceipt(uint result)
    { return Packet(new EndPacket { _result = result, _type = 4, _iswin = true, _idbattle_report = 987 }); }
    private static string Q(string value) { return Command.Quote(value); }
    private static void ExpectFailure(Action action)
    {
        try { action(); }
        catch (Exception) { checks++; return; }
        throw new Exception("expected fail-closed production exception");
    }

    private sealed class Fixture
    {
        internal readonly RootMessenger Previous, BattleRoot;
        internal readonly RootUI GlobalUi;
        internal readonly BattleUI Battle;
        internal readonly BattleNormalResultUI Result;
        internal readonly ArenaUI Arena;
        internal readonly GraveLevelUI Grave;
        internal readonly FormationUI Formation;
        internal readonly ArenaSelectUI Select;
        internal readonly BattleValueCompareUI Compare;
        internal readonly game.net.tcp_client Client;
        internal readonly BattleSettlement Owner;
        internal readonly DailyArenaBattleModel ArenaModel;
        internal readonly DailyGraveModel GraveModel;
        internal readonly List<string> Events = new List<string>();
        internal readonly UnityEngine.Object[] All;
        internal bool Paused;
        private int sequence;
        internal Fixture(bool grave)
        {
            // Fresh detached process per case. No game assembly is loaded.
            typeof(BattleSettlement).GetField("active", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, null);
            BattleLaunch.Reset();
            GameObject.Paths.Clear(); BattleFixtureTrace.Events.Clear(); Global.Templates.Clear();
            Global.character_ = new Database.Character(); Global.relogin = false; Global.extern_enabled = true; Global.ActivityCalls = 0;
            TimeUtil._now = 100000; TimeUtil._thisDay = 86400; RootUI.m_bRefreshFive = false;
            Client = new game.net.tcp_client(); RootUI.m_client = Global.m_client = Client;
            game.net.tcp_base.connection_state = 3;
            Previous = new RootMessenger { LevelID = grave ? "Divinity" : "Campaign" };
            Previous.gameObject.name = grave ? "Grave" : "Campaign";
            Previous.gameObject.Attach(Previous);
            GlobalUi = Previous.gameObject.Attach(new RootUI { rmg = Previous, level_rmg = Previous });
            Previous.gameObject.Attach(new MainUI { rmg = Previous });
            BaseUI.rootUI = GlobalUi; __BaseUI.battle_result_ui = null;
            Select = Previous.gameObject.Attach(new ArenaSelectUI { rmg = Previous, enabled = false });
            Compare = Previous.gameObject.Attach(new BattleValueCompareUI { rmg = Previous, enabled = false });
            Arena = Previous.gameObject.Attach(new ArenaUI { rmg = Previous, enabled = !grave });
            BattleRoot = new RootMessenger { LevelID = "Battle" };
            BattleRoot.gameObject.Attach(BattleRoot);
            GameObject.Paths["/Battle"] = GameObject.Paths["Battle"] = BattleRoot.gameObject;
            Battle = new BattleUI { rmg = BattleRoot, m_prevUI = Previous, enabled = false };
            Battle.transform.parent = BattleRoot.transform; Battle.gameObject.Attach(Battle);
            Result = Battle.gameObject.Attach(new BattleNormalResultUI { rmg = BattleRoot, battle_ui = Battle, enabled = false });
            GameObject.Paths["/Battle/BattleUI"] = GameObject.Paths["Battle/BattleUI"] = Battle.gameObject;
            BattleModeFixtureObjects.Battle = Battle; BattleModeFixtureObjects.Result = Result;
            Grave = new GraveLevelUI { rmg = Previous, enabled = grave };
            Grave.transform.parent = Previous.transform; Grave.gameObject.Attach(Grave);
            Formation = Grave.gameObject.Attach(new FormationUI { rmg = Previous, Source = Grave, enabled = false });
            Grave.Formation = Formation;
            if (grave)
            {
                GameObject.Paths["/Grave"] = Previous.gameObject;
                GameObject.Paths["/Grave/GraveUI"] = Grave.gameObject;
            }
            BaseAsset.lds = new Loader { currmg = Previous, isBattling = false, enabled = false };
            MainUI.m_subui = grave ? null : (object)Arena;
            ActivityTemplate arenaTemplate = new ActivityTemplate();
            ActivityRow arenaRow = new ActivityRow { temp = arenaTemplate };
            ActivityCategory category = new ActivityCategory(); category.acts[(uint)9] = arenaRow;
            Global.character_.acts[(uint)9] = arenaRow; Global.character_.act_rewards[(uint)3] = category;
            Global.character_.arena.challengers.Add(new ArenaChallenger());
            Global.Templates["activity:9"] = arenaTemplate;
            Global.character_.acts[(uint)17] = new ActivityRow { temp = new ActivityTemplate { id = 17 }, condition_param = 0 };
            Global.character_.graves[(uint)31] = new OpenGrave(); Global.Templates["grave:31"] = new GraveTemplate();
            All = new UnityEngine.Object[] { Previous, GlobalUi, Select, Compare, Arena, Grave, Formation, Battle, Result };
            Owner = new BattleSettlement(delegate(string name, string fields) { Events.Add(name); }, delegate { return !Paused; });
            ArenaModel = new DailyArenaBattleModel(); GraveModel = new DailyGraveModel();
        }
        internal D Context()
        { return new D(All, Previous, GlobalUi, Global.character_, "78", "PASS", Paused, delegate { return null; }); }
        internal void CaptureArena() { ArenaModel.Capture(Context()); }
        internal void ArenaAction(string action, bool succeeds)
        {
            string why;
            Check(ArenaModel.Queue(9, 78, Operation, ++sequence, ArenaModel.Revision, action, out why), "queue Arena " + action + ": " + why + " " + ArenaModel.Json());
            bool invoked = ArenaModel.ExecuteGui(Context, delegate { }, delegate(DailyArenaBattleInvocationCheck sample, DailyArenaBattleInvocationCheck check)
            { return !Paused && sample() && check(); }, out why);
            Check(invoked == succeeds, "execute Arena " + action + ": " + why + " " + ArenaModel.Json());
        }
        internal game.net.tcp_base.Handler StartArena()
        {
            CaptureArena(); ArenaAction("refresh", true); Client.Dispatch(0x463, new byte[] { 8, 0 });
            CaptureArena();
            Client.m_handlers[(ushort)355] = new game.net.tcp_base.Handler { handler = Global.handelActvity };
            ArenaAction("challenge", true);
            Check(Client.Sent.Count == 2 && Client.Sent[0] == 0x463 && Client.Sent[1] == 0x460, "actual Arena adapter invokes enter/challenge branches only");
            Check(BattleLaunch.DirectCalls == 1 && BattleLaunch.FormationCalls == 0 && BattleLaunch.CheckCalls == 1 &&
                BattleLaunch.Last != null && BattleLaunch.Last.Mode == BattleMode.Arena && BattleLaunch.Last.Opcode == 0x460 &&
                ReferenceEquals(BattleLaunch.Last.Source, Arena) && ReferenceEquals(BattleLaunch.Last.Target, Global.character_.arena.challengers[0]),
                "actual Arena challenge passes its exact panel, opponent and opcode to the guard substitute");
            return (game.net.tcp_base.Handler)Client.m_handlers[(ushort)0x460];
        }
        internal void GraveAction(string action, bool succeeds = true)
        {
            GraveModel.Capture(Context()); string why;
            Check(GraveModel.Queue(17, 78, Operation, ++sequence, GraveModel.Revision, action, out why), "queue Grave " + action + ": " + why + " " + GraveModel.Json());
            bool invoked = GraveModel.ExecuteGui(Context, delegate { }, delegate(DailyBattleAdmissionCheck prepare, DailyBattleAdmissionCheck check)
            { return !Paused && prepare() && check(); }, out why);
            Check(invoked == succeeds, "execute Grave " + action + ": " + why + " " + GraveModel.Json());
        }
        internal BattlePresentationOwnership StartGrave()
        {
            Check(GraveModel.OwnedBattlePresentation(Battle) == null, "observed Grave scene alone cannot claim a fight");
            GraveAction("resume"); GraveAction("select"); GraveAction("start");
            Check(Client.Sent.Count == 1 && Client.Sent[0] == 0x342, "real Grave resume/select/start never reenters a run");
            Check(BattleLaunch.FormationCalls == 1 && BattleLaunch.DirectCalls == 0 && BattleLaunch.CheckCalls == 1 &&
                BattleLaunch.Last != null && BattleLaunch.Last.Mode == BattleMode.Grave &&
                ReferenceEquals(BattleLaunch.Last.Formation, Formation) && ReferenceEquals(BattleLaunch.Last.Source, Grave) &&
                ReferenceEquals(BattleLaunch.Last.Target, Global.character_.grave_campaign.troops[0]),
                "actual Grave start passes exact formation, source and selected troop to guard substitute");
            Client.Dispatch(0x342, new byte[] { 8, 1 });
            Check(Grave.InitialCalls == 1 && Grave.Applied == 0, "initial Grave report does not settle the fight");
            BattlePresentationOwnership ownership = GraveModel.OwnedBattlePresentation(Battle);
            Check(ownership != null && ownership.Mode == BattleMode.Grave && ReferenceEquals(ownership.Source, Grave) &&
                ReferenceEquals(ownership.Target, Global.character_.grave_campaign.troops[0]), "actual Grave adapter binds chosen source and troop");
            return ownership;
        }
        internal void CheckOrigin(__BaseUI source)
        {
            Check(ReferenceEquals(BaseAsset.lds.currmg, Previous) && !BaseAsset.lds.isBattling && source.enabled,
                "stock boundary exits to exact original root and source page");
            Check(Previous.Enables == 1 && Battle.ExitCalls == 1 && Battle.OnClose == null, "original callback consumed once");
            Check(Arena.Returns + Grave.Returns == 1, "exactly one originating mode callback ran");
        }
    }

    private static void ArenaRealRoute()
    {
        Fixture f = new Fixture(false); f.StartArena();
        f.Client.Dispatch(0x460, ArenaReceipt()); f.CaptureArena();
        Check(f.Arena.Applied == 1 && f.Arena.CallbackPosition == 0, "stock initial callback receives original stream once");
        Check(BattleLaunch.Last.InitialCalls == 1 && ReferenceEquals(BattleLaunch.Last.InitialBattle, f.Battle) &&
            ReferenceEquals(BattleLaunch.Last.InitialReport, f.Battle.interpreter.m_report), "Arena forwards exact initial stock report to guard substitute once");
        Check(f.ArenaModel.Json().Contains("\"stock_callback_returned\":true"), "production adapter records successful initial callback");
        f.ArenaAction("skip", true);
        Check(State(f).Path == BattleSettlementPath.SettledResponse && State(f).Mode == BattleMode.Arena && State(f).Opened,
            "real Arena route invokes shared settled presentation");
        Check(BattleLaunch.ReportBoundCalls == 1 && ReferenceEquals(BattleLaunch.LastOwnership.Source, f.Arena) &&
            ReferenceEquals(BattleLaunch.LastOwnership.Target, Global.character_.arena.challengers[0]), "Arena settlement binds same owner and opponent to guard substitute");
        Check(f.Client.EndRequests == 0 && !f.Client.Sent.Contains(0x408) && f.Battle.BattleEndCalls == 0,
            "Arena immediate route never sends or applies 0x408");
        for (int i = 0; i < 4; i++) f.Owner.Advance(f.Battle);
        Check(f.Battle.OpenCalls == 1 && f.Arena.Applied == 1, "repeated captures do not settle or open twice");
        f.CaptureArena(); f.ArenaAction("battle_close", true); f.CaptureArena();
        f.CheckOrigin(f.Arena);
        Check(f.Arena.Returns == 1 && f.Grave.Returns == 0 && f.ArenaModel.Json().Contains("\"round_settled\":true"),
            "real Arena battle_close observes the original mode return");
    }
    private static void ArenaDuplicateCallback()
    {
        Fixture f = new Fixture(false); game.net.tcp_base.Handler slot = f.StartArena();
        DevelopmentNormalCallback callback = slot.handler;
        f.Client.Dispatch(0x460, ArenaReceipt());
        callback(new MemoryStream(ArenaReceipt(), false), null); f.CaptureArena();
        Check(f.Arena.Applied == 1 && Global.character_.arena.challenge_count == 1, "duplicate initial receipt cannot call stock or increment account twice");
        Check(f.ArenaModel.OutcomeUnknown && !f.ArenaModel.Json().Contains("\"next_action\":\"skip\""), "duplicate initial receipt preserves UNKNOWN and suppresses presentation");
        Check(f.Battle.OpenCalls == 0 && f.Client.EndRequests == 0 && f.Arena.Returns == 0, "duplicate uncertainty cannot force a return");
    }
    private static void ArenaCallbackIdentityMismatch()
    {
        Fixture f = new Fixture(false); f.StartArena();
        Global.character_.arena.challengers[0] = new ArenaChallenger();
        f.Client.Dispatch(0x460, ArenaReceipt()); f.CaptureArena();
        Check(f.Arena.Applied == 0 && Global.character_.arena.challenge_count == 0, "same-ID replacement challenger fails before stock initial callback");
        Check(f.ArenaModel.OutcomeUnknown && f.Battle.OpenCalls == 0 && f.Arena.Returns == 0, "callback owner mismatch cannot present or return another page");
    }
    private static void ArenaWrongCloseTarget()
    {
        Fixture f = new Fixture(false); f.StartArena(); f.Client.Dispatch(0x460, ArenaReceipt());
        ArenaUI stranger = new ArenaUI(); f.Battle.OnClose = stranger.BattleClose;
        f.CaptureArena();
        Check(!f.ArenaModel.Json().Contains("\"next_action\":\"skip\""), "same-type wrong instance callback is never a skip offer");
        Check(f.Battle.OpenCalls == 0 && stranger.Returns == 0 && f.Arena.Returns == 0 && f.Client.EndRequests == 0,
            "foreign page callback remains untouched");
    }
    private static void ArenaPauseAndResume()
    {
        Fixture f = new Fixture(false); f.StartArena(); f.Client.Dispatch(0x460, ArenaReceipt()); f.CaptureArena();
        f.Paused = true; f.ArenaAction("skip", false);
        Check(f.Battle.OpenCalls == 0 && f.Client.EndRequests == 0 && f.Arena.Returns == 0, "paused GUI gate cannot begin result presentation or return");
        f.Paused = false; f.CaptureArena(); f.ArenaAction("skip", true);
        Check(f.Battle.OpenCalls == 1 && f.Client.EndRequests == 0, "resume consumes same authoritative receipt without a new request");
    }
    private static void ArenaStopPending()
    {
        Fixture f = new Fixture(false); game.net.tcp_base.Handler slot = f.StartArena();
        DevelopmentNormalCallback retained = slot.handler; f.ArenaModel.Stop();
        f.Client.Dispatch(0x460, ArenaReceipt()); retained(new MemoryStream(ArenaReceipt(), false), null); f.CaptureArena();
        string why;
        Check(!f.ArenaModel.Queue(9, 78, Operation, 99, f.ArenaModel.Revision, "skip", out why), "stopped adapter refuses further commands");
        Check(f.Arena.CallbackCalls == 1 && f.Arena.Applied == 1 && f.Battle.OpenCalls == 0 && f.Client.EndRequests == 0 && f.Arena.Returns == 0,
            "normal stop applies only the already-sent native response once without automation");
    }
    private static void ArenaThrowUnknown()
    {
        Fixture f = new Fixture(false); f.StartArena(); f.Arena.ThrowCallback = true;
        ExpectFailure(delegate { f.Client.Dispatch(0x460, ArenaReceipt()); }); f.CaptureArena();
        Check(f.ArenaModel.OutcomeUnknown && f.Battle.OpenCalls == 0 && f.Arena.Returns == 0, "stock initial failure remains UNKNOWN without presentation");
    }
    private static void ArenaRepeatedNonbattleCallbacks()
    {
        Fixture f = new Fixture(false); f.CaptureArena(); f.ArenaAction("refresh", true);
        game.net.tcp_base.Handler enter = (game.net.tcp_base.Handler)f.Client.m_handlers[(ushort)0x463];
        f.Client.Dispatch(0x463, new byte[] { 8, 0 }); enter.handler(new MemoryStream(new byte[] { 8, 0 }, false), null);
        f.CaptureArena();
        Check(f.Arena.EnterCalls == 2 && !f.ArenaModel.OutcomeUnknown && f.ArenaModel.Json().Contains("\"next_action\":\"challenge\""),
            "repeat entry callbacks remain stock-owned and do not poison the challenge receipt guard");
        f.Client.m_handlers[(ushort)355] = new game.net.tcp_base.Handler { handler = Global.handelActvity };
        f.ArenaAction("challenge", true);
        game.net.tcp_base.Handler activity = (game.net.tcp_base.Handler)f.Client.m_handlers[(ushort)355];
        byte[] cappedProgress = { 10, 4, 8, 9, 16, 5 };
        activity.handler(new MemoryStream(cappedProgress, false), null);
        activity.handler(new MemoryStream(cappedProgress, false), null);
        f.Client.Dispatch(0x460, ArenaReceipt()); f.CaptureArena();
        Check(Global.ActivityCalls == 2 && !f.ArenaModel.OutcomeUnknown && f.ArenaModel.Json().Contains("\"next_action\":\"skip\""),
            "repeat permanent activity callbacks forward twice without triggering duplicate-battle invalidation");
    }
    private static void ArenaTimeoutNoReplay()
    {
        Fixture f = new Fixture(false); f.StartArena(); f.Client.Dispatch(0x460, null);
        for (int i = 0; i < 4; i++) f.CaptureArena();
        string why;
        Check(f.Arena.CallbackCalls == 0 && f.Arena.Applied == 0 && f.ArenaModel.OutcomeUnknown,
            "null timeout is UNKNOWN before stock mutation");
        Check(!f.ArenaModel.Queue(9, 78, Operation, 99, f.ArenaModel.Revision, "challenge", out why) &&
            !f.ArenaModel.Queue(9, 78, Operation, 100, f.ArenaModel.Revision, "skip", out why),
            "UNKNOWN timeout cannot replay challenge or invent a skip");
        Check(f.Client.Sent.Count == 2 && f.Client.EndRequests == 0 && f.Battle.OpenCalls == 0 && f.Arena.Returns == 0,
            "timeout observation never reissues a request or returns a mode page");
    }
    private static void ArenaGuardDenialDoesNotCallStock()
    {
        Fixture f = new Fixture(false); f.CaptureArena(); f.ArenaAction("refresh", true);
        f.Client.Dispatch(0x463, new byte[] { 8, 0 }); f.CaptureArena();
        f.Client.m_handlers[(ushort)355] = new game.net.tcp_base.Handler { handler = Global.handelActvity };
        BattleLaunch.ForceDeny = true; f.ArenaAction("challenge", false); f.CaptureArena();
        Check(BattleLaunch.DirectCalls == 1 && f.Client.Sent.Count == 1 && f.Client.Sent[0] == 0x463 &&
            f.Arena.CallbackCalls == 0 && f.ArenaModel.OutcomeUnknown, "denied direct guard prevents stock challenge and stays unresolved");
    }
    private static void GraveDelayedRoute()
    {
        Fixture f = new Fixture(true); BattlePresentationOwnership ownership = f.StartGrave();
        f.Owner.AdvanceOwned(f.Battle, ownership);
        Check(State(f).Path == BattleSettlementPath.RequestEnd && State(f).Mode == BattleMode.Grave && f.Client.EndRequests == 1,
            "real Grave ownership authorizes exactly one delayed 0x408");
        Check(BattleLaunch.ReportBoundCalls == 1 && ReferenceEquals(BattleLaunch.LastOwnership.Source, f.Grave) &&
            ReferenceEquals(BattleLaunch.LastOwnership.Target, Global.character_.grave_campaign.troops[0]), "Grave settlement binds the same page and selected troop to guard substitute");
        Check(f.Battle.OpenCalls == 0 && f.Grave.Applied == 0 && !State(f).Applied, "Grave presentation waits for real end receipt");
        game.net.tcp_base.Handler pending = f.Client.Dispatch(0x408, GraveReceipt(0));
        pending.handler(new MemoryStream(GraveReceipt(0), false), null);
        f.Owner.Advance(f.Battle); f.Owner.Advance(f.Battle);
        Check(f.Grave.Applied == 1 && Global.character_.grave_campaign.finish == 1 && f.Battle.CallbackCalls == 1,
            "duplicate delayed receipt applies Grave state once through stock callback");
        Check(State(f).Opened && f.Battle.OpenCalls == 1 && f.Client.EndRequests == 1, "Grave opens result once after confirmed stock apply");
        f.GraveAction("battle_exit"); f.CheckOrigin(f.Grave);
        Check(f.Grave.Returns == 1 && f.Arena.Returns == 0, "real Grave battle_exit preserves Grave return callback");
    }
    private static void GraveUnknownResult()
    {
        Fixture f = new Fixture(true); f.Owner.AdvanceOwned(f.Battle, f.StartGrave());
        f.Client.Dispatch(0x408, GraveReceipt(2));
        ExpectFailure(delegate { f.Owner.Advance(f.Battle); });
        Check(State(f).Error != "" && !State(f).Applied && f.Battle.OpenCalls == 0 && f.Client.EndRequests == 1,
            "unrecognized end result remains unresolved without retry or early result");
        Check(f.Arena.Returns == 0 && f.Grave.Returns == 0, "unknown result cannot return any page");
        f.GraveModel.Capture(f.Context()); string why;
        Check(f.GraveModel.Unresolved && !f.GraveModel.Queue(17, 78, Operation, 99, f.GraveModel.Revision, "start", out why),
            "real Grave adapter cannot enqueue another start while its settlement remains unresolved");
        Check(f.Client.Sent.FindAll(delegate(ushort opcode) { return opcode == 0x342; }).Count == 1,
            "unresolved real Grave route has exactly one stock battle start");
    }
    private static void GraveIdentityMismatch()
    {
        Fixture f = new Fixture(true); BattlePresentationOwnership ownership = f.StartGrave();
        f.Grave.m_troop = new GraveTroop();
        Check(f.GraveModel.OwnedBattlePresentation(f.Battle) == null && !ownership.Verify(), "real Grave adapter rejects replaced same-ID troop");
        f.Owner.AdvanceOwned(f.Battle, ownership);
        Check(f.Client.EndRequests == 0 && f.Battle.OpenCalls == 0 && f.Grave.Returns == 0, "invalid Grave ownership cannot cause effects or a wrong return");
    }
    private static void GravePauseAndStop()
    {
        Fixture paused = new Fixture(true); BattlePresentationOwnership held = paused.StartGrave();
        paused.Paused = true; paused.Owner.AdvanceOwned(paused.Battle, held);
        Check(paused.Client.EndRequests == 0 && paused.Battle.OpenCalls == 0, "paused controller refuses new Grave settlement ownership");
        Fixture stopped = new Fixture(true); BattlePresentationOwnership ownership = stopped.StartGrave();
        stopped.GraveModel.Stop(); stopped.Owner.Stop(); stopped.Owner.AdvanceOwned(stopped.Battle, ownership);
        Check(stopped.GraveModel.OwnedBattlePresentation(stopped.Battle) == null && stopped.Client.EndRequests == 0 &&
            stopped.Grave.Returns == 0 && stopped.Arena.Returns == 0, "mode stop prevents new ownership, sends and return");
    }
    private static void GraveGuardDenialDoesNotCallStock()
    {
        Fixture f = new Fixture(true); f.GraveAction("resume"); f.GraveAction("select");
        BattleLaunch.ForceDeny = true; f.GraveAction("start", false); f.GraveModel.Capture(f.Context());
        Check(BattleLaunch.FormationCalls == 1 && f.Client.Sent.Count == 0 && f.Grave.InitialCalls == 0 &&
            f.GraveModel.Unresolved, "denied formation guard prevents stock battle and remains unresolved");
    }

    private static int Main()
    {
        KeyValuePair<string, Action>[] cases = {
            new KeyValuePair<string, Action>("arena_reply_wrong_opponent_blocked", ArenaReplyWrongOpponent),
            new KeyValuePair<string, Action>("arena_reply_premium_blocked", ArenaReplyPremium),
            new KeyValuePair<string, Action>("arena_reply_wire_and_identity_negatives", ArenaReplyNegatives),
            new KeyValuePair<string, Action>("arena_reply_valid_defaults_loss_rewards", ArenaReplyValidVariants),
            new KeyValuePair<string, Action>("arena_reply_report_mutation_unknown", ArenaReplyReportMutation),
            new KeyValuePair<string, Action>("arena_real_adapter_settled_return", ArenaRealRoute),
            new KeyValuePair<string, Action>("arena_duplicate_receipt_unknown", ArenaDuplicateCallback),
            new KeyValuePair<string, Action>("arena_pre_callback_target_replaced", ArenaCallbackIdentityMismatch),
            new KeyValuePair<string, Action>("arena_wrong_close_instance", ArenaWrongCloseTarget),
            new KeyValuePair<string, Action>("arena_pause_resume", ArenaPauseAndResume),
            new KeyValuePair<string, Action>("arena_stop_pending_callback", ArenaStopPending),
            new KeyValuePair<string, Action>("arena_stock_callback_unknown", ArenaThrowUnknown),
            new KeyValuePair<string, Action>("arena_repeated_nonbattle_callbacks", ArenaRepeatedNonbattleCallbacks),
            new KeyValuePair<string, Action>("arena_timeout_no_replay", ArenaTimeoutNoReplay),
            new KeyValuePair<string, Action>("arena_guard_denial_no_stock_start", ArenaGuardDenialDoesNotCallStock),
            new KeyValuePair<string, Action>("grave_real_adapter_request_end_return", GraveDelayedRoute),
            new KeyValuePair<string, Action>("grave_unknown_end_no_retry", GraveUnknownResult),
            new KeyValuePair<string, Action>("grave_replaced_troop_identity", GraveIdentityMismatch),
            new KeyValuePair<string, Action>("grave_pause_stop_no_ownership", GravePauseAndStop),
            new KeyValuePair<string, Action>("grave_guard_denial_no_stock_start", GraveGuardDenialDoesNotCallStock)
        };
        int failed = 0;
        foreach (KeyValuePair<string, Action> test in cases)
        {
            int before = checks;
            try { test.Value(); Console.WriteLine("{\"scenario\":" + Q(test.Key) + ",\"status\":\"PASS\",\"checks\":" + (checks - before) + "}"); }
            catch (Exception error)
            {
                failed++;
                while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
                Console.WriteLine("{\"scenario\":" + Q(test.Key) + ",\"status\":\"FAIL\",\"error\":" + Q(error.ToString()) + "}");
            }
        }
        Console.WriteLine("{\"status\":" + Q(failed == 0 ? "PASS" : "FAIL") + ",\"scenarios\":" + cases.Length +
            ",\"failed\":" + failed + ",\"checks\":" + checks +
            ",\"scope\":\"production Arena/Grave adapters and settlement; fake client/Unity and instrumented launch-guard substitute; no actual IPC or guard race\",\"game_access\":false}");
        return failed == 0 ? 0 : 1;
    }
}
