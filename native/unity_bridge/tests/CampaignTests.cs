using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Threading;
using ProtoBuf;
using JiangShen.AssistantRuntime;

// Own offline fixtures only. Production Protocol/Observer/CampaignModel sources
// are compiled unchanged. Only the pinned pure protobuf-net library is loaded;
// no Unity, CSharp, firstpass, pbdata or dbdata assembly is loaded here.
namespace UnityEngine
{
    public class Object { private static int next; private readonly int id = ++next; public int GetInstanceID() { return id; } }
    public class Transform { public Transform root; public Transform() { root = this; } }
    public class GameObject : Object
    {
        public bool activeInHierarchy = true; public Transform transform = new Transform();
        public System.Collections.Generic.List<Component> components = new System.Collections.Generic.List<Component>();
        public Component[] GetComponents(Type type) { return components.FindAll(delegate(Component value) { return type.IsAssignableFrom(value.GetType()); }).ToArray(); }
        // This detached scene has no globally named Anecdote panel.
        public static GameObject Find(string path) { return null; }
    }
    public class Component : Object { public GameObject gameObject = new GameObject(); public Transform transform { get { return gameObject.transform; } } }
    public class Behaviour : Component { public bool enabled = true; }
    public class Event { public static Event current = new Event(); }
    public static class GUI { public static bool enabled = true; }
    public struct Vector2 { public float x, y; public Vector2(float a, float b) { x = a; y = b; } }
    public struct Rect { public float x, y, width, height; public Rect(float a, float b, float c, float d) { x = a; y = b; width = c; height = d; } }
}
namespace JiangShen.AssistantRuntime
{
    internal sealed class ProbeFailure : Exception { internal ProbeFailure(string text) : base(text) { } }
    internal sealed class Snapshot
    {
        internal UnityEngine.Component Main;
        internal UnityEngine.Component Root = new UnityEngine.Component();
        internal object Character;
        internal bool Safe = true, InteractionEnabled = true;
        internal string Gate = "PASS";
    }
    internal sealed class Model
    {
        internal static UnityEngine.Object[] Objects = new UnityEngine.Object[0];
        internal static Snapshot Current = new Snapshot();
        internal static UnityEngine.Component ContextRoot = new RootMessenger { LevelID = "Campaign" };
        internal static UnityEngine.GameObject GlobalObject, BattleObject;
        internal Type RootMessengerType { get { return typeof(RootMessenger); } }
        internal UnityEngine.Component CurrentRoot() { return ContextRoot; }
        internal UnityEngine.GameObject GlobalUiObject() { return GlobalObject; }
        internal UnityEngine.GameObject BattleUiObject() { return BattleObject; }
        internal Snapshot Capture() { return Current; }
        internal UnityEngine.Object[] AllUi() { return Objects; }
        internal string OverlayGate(UnityEngine.Behaviour value) { return "visible_unknown_ui"; }
        internal static Type GameType(string name) { return typeof(Model).Assembly.GetType(name, true); }
        internal static Type FirstType(string name) { return GameType(name); }
    }
}
public class __BaseUI : UnityEngine.Behaviour
{
    public bool interaction = true;
    public RootMessenger rmg;
    public DateTime visible_time = DateTime.Now.AddSeconds(-1);
    public bool loading, collecting;
    public bool IsEnable() { return interaction; }
    public void Hide() { enabled = false; }
    public static UnityEngine.Rect _rectRB(float x, float y, float width, float height) { return new UnityEngine.Rect(x, y, width, height); }
}
public class StubUi : __BaseUI { }
public class MainUI : StubUi
{
    public static object m_subui;
    public bool Unlocked = true;
    public int Calls;
    private bool _campaign_check(string name) { return Unlocked && name == "Campaign"; }
    private void _campaign() { Calls++; }
    public static void SetSubUI(StubUi value) { }
}
public class RootMessenger : UnityEngine.Component { public string LevelID = ""; public int Disables; public void ToDisable() { Disables++; } }
public class RootUI : StubUi { public static QueueClient m_client = new QueueClient(); public int Calls; public void cs_campaign_enter(ushort id, RootMessenger root) { if (id != 0) throw new Exception("chapter_id"); Calls++; } }
public class QueueClient { public ArrayList messages = new ArrayList(); public Hashtable m_handlers = new Hashtable(); }
public class QueueMessage { public ushort op; public MemoryStream stream; }
public class JSProtoBuf
{
    public class PBSerializerWrapper { public T Deserialize<T>(Stream stream) { return Serializer.Deserialize<T>(stream); } }
    public static PBSerializerWrapper wrapper = new PBSerializerWrapper();
}
public class CampaignData { public int idcampaign_type; public bool unlocked = true, finished; }
public class TroopTemplate
{
    public ushort idcampaign = 12, chanllege_level = 6, Newbie_Skill;
    public byte idtroop = 7, click_mode = 3, finish_max = 1, isNewbie;
    public uint Newbie_formation, m_scenarioClickF, m_scenarioVisible, m_scenarioAttackF, m_scenarioVictoryF, m_scenarioFailure, m_scenarioSystem;
}
public class TroopData : TroopTemplate
{
    public object data_ = new object();
    public bool visible = true, canattack = true, killed, show_arrow = true, clicked;
    public byte finish_count;
    public short limit_count = -1;
    public UnityEngine.GameObject mesh = new UnityEngine.GameObject();
}
public class CampaignTroopScript : StubUi { public CampaignUI ParentUI; public TroopData Troop; }
public class CharacterData
{
    public int level = 12; public ushort idcampaign = 12; public bool gm;
    public Hashtable form_generals_ = new Hashtable(), formations_ = new Hashtable(); public int cur_form = 1;
    public int AddCalls;
    public bool AddOneGenToCurForm(uint id)
    {
        AddCalls++;
        TutorialFixture.Form f = (TutorialFixture.Form)formations_[cur_form];
        if (f.spotgen.Contains((int)id)) return true;
        for (int i = 0; i < f.spotnum; i++) if ((int)f.spotgen[i] == -1) { f.spotgen[i] = (int)id; return true; }
        return false;
    }
}
public static class Global
{
    public static QueueClient m_client = RootUI.m_client;
    public static bool extern_enabled = true;
    public static object character_;
    public static int GuideClears;
    public static void setGuideCheck(string key, bool check) { if (key != "troop_formation" || check) throw new Exception("guide_arguments"); GuideClears++; }
}
public class CampaignUI : StubUi
{
    public long m_campaignID = 12;
    public byte m_troopID = 7;
    public CampaignData m_campaign = new CampaignData();
    public Hashtable m_troops = new Hashtable();
    public bool m_bDebug, m_bPostInit, m_bBorning, m_bBornWant, m_bScenarioVisible, m_bSignActiving;
    public int SelectCalls, BonusCalls;
    public void wantCombat(byte id) { if (id != 7) throw new Exception("troop_id"); SelectCalls++; }
    public void BonusOpen() { BonusCalls++; }
}
public delegate bool AttackDelegate();
public class CampaignTroopUI : StubUi
{
    public CampaignUI m_parent;
    public TroopData m_troop;
    public int PrepareCalls;
    private void BattleFormation(bool can, byte id) { if (!can || id != 7) throw new Exception("formation_args"); PrepareCalls++; }
    private bool OnAttack() { return true; }
    public AttackDelegate Attack() { return OnAttack; }
}
public partial class FormationUI : StubUi
{
    public string DefaultState = "NPCBattle", substate = "campaign";
    public bool viewonly, isVisible = true, isLoading, onfight, newhint_skill;
    public int waitstate, StartCalls, currentSkill = 99, currentArmy = 55, currentForm = 2;
    public long Campaignid = 12;
    public int Troopid = 7;
    public AttackDelegate OnAttack;
    private void OnClickSetCurrentForm() { StartCalls++; waitstate = 2; }
}
public enum enumBattleViewType { viewDefault, viewReport, viewFull, viewVipSkip }
public class PendingBattleEnd { public EndPacket pkt; }
[ProtoContract]
public class EndPacket
{
    [ProtoMember(1, IsRequired = true)] public uint result;
    [ProtoMember(2)] public uint type;
    [ProtoMember(4)] public uint idcampaign;
    [ProtoMember(5)] public uint idtroop;
    [ProtoMember(6)] public bool iswin;
    [ProtoMember(17)] public ulong idbattle_report;
}
public class BattleInterpreter { public object packet = new object(); }
public class BattleUI : StubUi
{
    public int ExitCalls; public StubUi m_parentUI; public RootMessenger m_prevUI;
    public ushort idcampaign = 12; public byte idtroop = 7;
    public ulong ReportID; public bool BattleFighting, IsSurrended, IsCrossServer, bNeedWaitForBattleEnd, bUseBattleEnd;
    public enumBattleViewType ViewType = enumBattleViewType.viewVipSkip;
    public float m_BattleLength; public PendingBattleEnd m_pktBattleEnd;
    public BattleInterpreter interpreter = new BattleInterpreter();
    public void battle_exit() { ExitCalls++; enabled = false; }
}
public class BattleNormalResultUI : StubUi { public BattleUI battle_ui; public bool m_battle_view_ui; public ulong m_reportid; }
public class BattleResultUI : StubUi { public RootMessenger m_prevUI; public Action OnClose; public string mine_ui = ""; public bool m_bCampaign = true; public int m_troopID = 7; }
public class CampaignChapterClearUI : StubUi { public bool m_bAnecdote, m_bNextChapter = true; }
// Type bindings are resolved even though these panels are absent from the detached scenes.
public class ScenarioFunctionUI : StubUi { }
public class MessageBox : StubUi { }
public class BattleValueUI : StubUi { }
public class AnecdoteSelectUI : StubUi { }
public class ScenarioSection { public string triggers = ""; }
public class ScenarioTemplate { public int layout = 3; }
public class Scenario { public uint id = 12; public string param0 = "victoryf"; public ArrayList sections = new ArrayList(new object[] { new ScenarioSection() }); public ArrayList m_sections = new ArrayList(new object[] { new ScenarioTemplate() }); }
public class ScenarioUI : StubUi
{
    public class newUIBtn
    {
        public string name = "fixed_intro";
        public float width = 64, height = 64;
        public object tex = new object();
        public UnityEngine.Vector2 curPos = new UnityEngine.Vector2(500, 300), targetPos = new UnityEngine.Vector2(50, 50);
        public bool bMovingEnd;
    }
    public Scenario m_scenario = new Scenario();
    public ArrayList newUIList = new ArrayList();
    public RootMessenger m_prevUI;
    public object skipStyle = new object();
    public int SkipCalls;
    public float LastRectX;
    public int m_iSection;
    public bool m_bSectionEnd = true, m_bHasNewUI, m_bMoving, m_bNext, m_bWantNewUI, m_bTrigger, m_bFirst;
    public Action OnClose;
    public void scenarioSkip(UnityEngine.Rect rect) { SkipCalls++; LastRectX = rect.x; m_iSection = m_scenario.sections.Count; }
}

internal static class CampaignTests
{
    private static int checks;
    private static readonly string A = new string('a', 32), B = new string('b', 32);
    private static void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
    private static CampaignFrame Baseline()
    { return new CampaignFrame { Campaign = new object(), Battle = new object(), Character = new object(), CharacterId = "18446744073709551615", CampaignId = 12, TroopId = 7, ReportId = 44,
        Scene = new object(), CampaignData = new object(), Target = new object(), TargetData = new object(), FinishMax = 1, QueueClient = new object(), QueueObserved = true, QueuedPackets = new CampaignQueuedPacket[0] }; }
    private static CampaignFrame Active(CampaignFrame before)
    { return new CampaignFrame { Campaign = before.Campaign, Battle = before.Battle, Character = before.Character, CharacterId = before.CharacterId, CampaignId = 12, TroopId = 7, BattleVisible = true, Waiting = true, Fighting = true, ReportToken = new object(), PlaybackRemaining = 30,
        Scene = before.Scene, CampaignData = before.CampaignData, Target = before.Target, TargetData = before.TargetData, FinishMax = before.FinishMax, FinishCount = before.FinishCount,
        QueueClient = before.QueueClient, QueueObserved = true, QueuedPackets = new CampaignQueuedPacket[0] }; }
    private static CampaignPacket Packet(bool win)
    { return new CampaignPacket { Result = 0, Type = 0, CampaignId = 12, TroopId = 7, ReportId = 987, IsWin = win }; }
    private static CampaignObserver Started(CampaignFrame before)
    { string why; CampaignObserver o = new CampaignObserver(); Check(o.Begin(A, 1, before, out why), "begin_" + why); o.Invoking(); return o; }
    private static void Settle(CampaignObserver observer, CampaignFrame active)
    {
        observer.Observe(active); active.ReportId = 987; active.Packet = null;
        active.ResultVisible = true; active.ResultReportId = 987; active.Waiting = active.UseEnd = active.Fighting = false;
        observer.Observe(active);
    }
    private static void ObserverRules()
    {
        foreach (bool win in new bool[] { true, false })
        {
            CampaignFrame before = Baseline(), active = Active(before); CampaignObserver o = Started(before);
            active.Packet = Packet(win); Settle(o, active);
            Check(o.Status == "SETTLED" && o.Outcome == (win ? "WIN" : "LOSS"), "authoritative_outcome");
            Check(o.Evidence == "captured_authoritative_receipt_applied" && o.Json().Contains("\"report_id\":\"987\""), "applied_evidence");
            string why; Check(!o.Begin(A, 2, before, out why) && why == "attempt_already_used", "duplicate_attempt_never_restarts");
            Check(o.Begin(B, 2, before, out why), "fresh_attempt_after_settlement");
            Check(o.Pending && !o.Begin(new string('c', 32), 3, before, out why), "inflight_single_attempt");
        }
        string[] failures = new string[] { "no_zero", "bad_result", "bad_type", "wrong_packet_target", "replay", "surrender", "different_battle", "different_parent", "character_changed", "character_id_changed", "missing_packet", "wrong_report", "result_report_mismatch", "conflict", "report_replaced", "replay_same_viewtype", "battle_disappeared" };
        foreach (string failure in failures)
        {
            CampaignFrame before = Baseline(), f = Active(before); CampaignObserver o = Started(before); f.Packet = Packet(true);
            if (failure == "no_zero") f.ReportId = 44;
            if (failure == "bad_result") f.Packet.Result = 1;
            if (failure == "bad_type") f.Packet.Type = 1;
            if (failure == "wrong_packet_target") f.Packet.TroopId = 8;
            if (failure == "replay") f.Replay = true;
            if (failure == "surrender") f.Surrendered = true;
            if (failure == "different_battle") f.Battle = new object();
            if (failure == "different_parent") f.Campaign = new object();
            if (failure == "character_changed") f.Character = new object();
            if (failure == "character_id_changed") { f.Character = new object(); f.CharacterId = "777"; }
            if (failure == "missing_packet") f.Packet = null;
            o.Observe(f);
            if (failure == "report_replaced") { f.ReportToken = new object(); o.Observe(f); }
            if (failure == "replay_same_viewtype") { f.PlaybackRemaining = 31; o.Observe(f); }
            if (failure == "battle_disappeared") { f.BattleVisible = false; o.Observe(f); f.BattleVisible = true; }
            if (failure == "wrong_report") f.ReportId = 988;
            else f.ReportId = 987;
            if (failure == "conflict") { f.Packet = Packet(false); o.Observe(f); }
            f.Packet = null; f.ResultVisible = true; f.ResultReportId = failure == "result_report_mismatch" ? 988UL : 987UL;
            f.Waiting = f.UseEnd = f.Fighting = false; o.Observe(f);
            Check(o.Status == "UNKNOWN" && o.Outcome == "UNKNOWN", failure + "_fail_closed");
            if (failure == "character_changed" || failure == "character_id_changed")
            {
                Check(o.Json().Contains("\"character_id\":\"18446744073709551615\""), "attempt_keeps_begin_role");
                Check(o.BusinessJson().Contains("\"character_id\":\"18446744073709551615\""), "business_keeps_begin_role");
            }
            string why; Check(!o.Begin(B, 2, before, out why), failure + "_cannot_retry");
        }
        CampaignFrame absent = Baseline(); absent.Battle = null;
        CampaignObserver delayed = Started(absent); CampaignFrame newBattle = Active(absent); newBattle.Battle = new object(); newBattle.Packet = Packet(true);
        newBattle.BattleVisible = false; delayed.Observe(newBattle);
        Check(delayed.Status == "SUBMITTED" && delayed.Outcome == "UNKNOWN", "new_hidden_battle_waits_for_actual_visible_zero_witness");
        newBattle.BattleVisible = true;
        Settle(delayed, newBattle); Check(delayed.Status == "SETTLED", "first_loaded_battle_binds_only_at_zero");
        SessionLifecycle life = new SessionLifecycle(0); string reason;
        Check(!life.ActionPending && life.Accept(new Command { Sequence = 1, Name = "heartbeat" }, 1, out reason), "business_does_not_block_heartbeat");
    }
    private static void Applied(CampaignFrame f, bool progress)
    {
        f.Packet = null; f.QueuedPackets = new CampaignQueuedPacket[0]; f.ReportId = f.ResultReportId = 987;
        f.ResultVisible = true; f.Waiting = f.UseEnd = f.Fighting = false;
        if (progress) { f.FinishCount++; f.Killed = true; }
    }
    private static void SettlementCaptureRules()
    {
        foreach (bool win in new bool[] { true, false })
        {
            CampaignFrame before = Baseline(), f = Active(before); f.UseEnd = true; CampaignObserver o = Started(before); o.Observe(f);
            f.ResultVisible = true; o.Observe(f);
            Check(o.Status == "WAITING" && o.Outcome == "UNKNOWN", "early_result_waits_for_late1032_" + win);
            object token = new object(); CampaignPacket source = Packet(win);
            f.QueuedPackets = new CampaignQueuedPacket[] { new CampaignQueuedPacket { Token = token, Packet = source } }; o.Observe(f);
            Check(o.Status == "WAITING" && o.Evidence == "captured_queued_authoritative_receipt", "queued_is_not_yet_applied_" + win);
            source.IsWin = !win; // An externally mutable fixture packet cannot rewrite the saved receipt.
            Applied(f, win); o.Observe(f);
            Check(o.Status == "SETTLED" && o.Outcome == (win ? "WIN" : "LOSS") && o.Evidence == "captured_queued_authoritative_receipt_applied", "late1032_applied_" + win);
        }
        foreach (string failure in new string[] { "rejected", "type", "target", "old_report", "zero_report", "conflict", "mutated", "identity", "missed", "old_embedded_failed", "old_embedded_missed", "refresh", "debug", "gm", "replay", "queue_error", "queued_still_present", "loss_progress" })
        {
            CampaignFrame before = Baseline(), f = Active(before); f.UseEnd = true; CampaignObserver o = Started(before);
            if (failure.StartsWith("old_embedded")) f.Packet = Packet(true);
            o.Observe(f); f.Packet = null;
            CampaignPacket packet = Packet(failure != "loss_progress"); object token = new object();
            if (failure == "rejected" || failure == "old_embedded_failed") packet.Result = 1;
            if (failure == "type") packet.Type = 1;
            if (failure == "target") packet.TroopId++;
            if (failure == "old_report") packet.ReportId = 44;
            if (failure == "zero_report") packet.ReportId = 0;
            f.QueuedPackets = failure == "missed" || failure == "old_embedded_missed" ? new CampaignQueuedPacket[0] : new CampaignQueuedPacket[] { new CampaignQueuedPacket { Token = token, Packet = packet } };
            o.Observe(f);
            if (failure == "conflict") f.QueuedPackets = new CampaignQueuedPacket[] { new CampaignQueuedPacket { Token = new object(), Packet = Packet(true) } };
            if (failure == "mutated") packet.IsWin = !packet.IsWin;
            if (failure == "identity") f.CharacterId = "777";
            if (failure == "refresh") f.RefreshPending = true;
            if (failure == "debug") f.Debug = true;
            if (failure == "gm") f.Gm = true;
            if (failure == "replay") f.Replay = true;
            if (failure == "queue_error") f.QueueError = "fixture";
            if (failure != "queued_still_present") o.Observe(f);
            CampaignQueuedPacket[] stillQueued = f.QueuedPackets; Applied(f, failure == "loss_progress" || failure == "rejected");
            if (failure == "queued_still_present") f.QueuedPackets = stillQueued;
            o.Observe(f);
            Check(o.Status == "UNKNOWN" && o.Outcome == "UNKNOWN", "queued_" + failure + "_fail_closed");
        }
        foreach (string failure in new string[] { "none", "no_progress", "not_killed", "jump", "max_changed", "troop_replaced", "data_replaced", "campaign_data_replaced", "scene_replaced", "report_replaced", "report_old", "result_mismatch", "refresh", "debug", "gm", "replay", "surrender" })
        {
            CampaignFrame before = Baseline(), f = Active(before); f.UseEnd = true; CampaignObserver o = Started(before); o.Observe(f); Applied(f, true);
            if (failure == "no_progress") { f.FinishCount = 0; f.Killed = false; }
            if (failure == "not_killed") f.Killed = false;
            if (failure == "jump") f.FinishCount = 2;
            if (failure == "max_changed") f.FinishMax = 2;
            if (failure == "troop_replaced") f.Target = new object();
            if (failure == "data_replaced") f.TargetData = new object();
            if (failure == "campaign_data_replaced") f.CampaignData = new object();
            if (failure == "scene_replaced") f.Scene = new object();
            if (failure == "report_replaced") f.ReportToken = new object();
            if (failure == "report_old") f.ReportId = f.ResultReportId = 44;
            if (failure == "result_mismatch") f.ResultReportId = 988;
            if (failure == "refresh") f.RefreshPending = true;
            if (failure == "debug") f.Debug = true;
            if (failure == "gm") f.Gm = true;
            if (failure == "replay") f.Replay = true;
            if (failure == "surrender") f.Surrendered = true;
            o.Observe(f);
            if (failure == "none") Check(o.Status == "SETTLED" && o.Outcome == "WIN" && o.Evidence == "anchored_mainline_progress_applied" && o.Json().Contains("\"report_id\":\"987\"") && o.DebugJson(f).Contains("\"receipt_captured\":false"), "anchored_progress_can_only_prove_win");
            else Check(o.Status == "UNKNOWN" && o.Outcome == "UNKNOWN", "progress_" + failure + "_fail_closed");
        }
        foreach (string failure in new string[] { "queue_unavailable", "refresh", "gm", "debug", "already_killed", "finished", "no_target_data", "pending_final", "pending_final_handler" })
        {
            CampaignFrame before = Baseline();
            if (failure == "queue_unavailable") before.QueueObserved = false;
            if (failure == "refresh") before.RefreshPending = true;
            if (failure == "gm") before.Gm = true;
            if (failure == "debug") before.Debug = true;
            if (failure == "already_killed") before.Killed = true;
            if (failure == "finished") before.CampaignFinished = true;
            if (failure == "no_target_data") before.TargetData = null;
            if (failure == "pending_final") before.QueuedPackets = new CampaignQueuedPacket[] { new CampaignQueuedPacket { Token = new object(), Packet = Packet(true) } };
            if (failure == "pending_final_handler") before.FinalPending = true;
            string reason; Check(!new CampaignObserver().Begin(A, 1, before, out reason), "baseline_" + failure + "_refused");
        }
        CampaignFrame earlyBefore = Baseline(), early = Active(earlyBefore); early.UseEnd = true; CampaignObserver waiting = Started(earlyBefore); waiting.Observe(early); Applied(early, true); early.ResultReportId = 0;
        waiting.Observe(early); Check(waiting.Status == "WAITING", "early_zero_result_report_waits");
        early.SampleTick += 9999; waiting.Observe(early); Check(waiting.Status == "WAITING", "zero_result_report_grace_is_bounded");
        early.SampleTick++; waiting.Observe(early); Check(waiting.Status == "UNKNOWN" && waiting.Evidence == "result_report_not_updated", "zero_result_report_never_silently_relaxes");
    }
    private static CampaignFrame ReadBattleFrame(World w)
    { return (CampaignFrame)typeof(CampaignModel).GetMethod("BattleFrame", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(w.Runtime, new object[0]); }
    private static QueueMessage QueueEnd(EndPacket packet)
    {
        MemoryStream bytes = new MemoryStream(); Serializer.Serialize(bytes, packet);
        return new QueueMessage { op = 1032, stream = new MemoryStream(bytes.ToArray()) };
    }
    private static void QueueCaptureRules()
    {
        World w = null; QueueMessage queued;
        foreach (bool win in new bool[] { true, false })
        {
        w = new World(); RootUI.m_client = new QueueClient();
        w.Campaign.m_troops[(byte)7] = new TroopData(); w.Observe(w.Global, w.Campaign, w.Battle);
        CampaignFrame baseline = ReadBattleFrame(w);
        Check(baseline.QueueObserved && baseline.QueuedPackets.Length == 0 && baseline.TargetData != null && object.ReferenceEquals(baseline.Scene, w.Root), "production_frame_freezes_real_target_and_empty_queue");
        CampaignObserver o = new CampaignObserver(); string reason;
        Check(o.Begin(A, 1, baseline, out reason), "production_queue_baseline_begin_" + reason);
        w.Battle.enabled = true; w.Battle.BattleFighting = true; w.Battle.bUseBattleEnd = w.Battle.bNeedWaitForBattleEnd = true; w.Battle.m_BattleLength = 30;
        w.Observe(w.Global, w.Campaign, w.Battle); CampaignFrame active = ReadBattleFrame(w); o.Observe(active);
        queued = QueueEnd(new EndPacket { idcampaign = 12, idtroop = 7, idbattle_report = 987, iswin = win }); RootUI.m_client.messages.Add(queued);
        long originalPosition = queued.stream.Position, originalLength = queued.stream.Length; byte[] originalBytes = queued.stream.ToArray();
        active = ReadBattleFrame(w); o.Observe(active);
        Check(active.QueuedPackets.Length == 1 && active.QueuedPackets[0].Packet.IsWin == win && active.QueuedPackets[0].Packet.ReportId == 987, "production_queue_actual_protobuf_parser");
        Check(queued.stream.Position == originalPosition && queued.stream.Length == originalLength && queued.stream.CanRead && Convert.ToBase64String(queued.stream.ToArray()) == Convert.ToBase64String(originalBytes) && RootUI.m_client.messages.Count == 1, "queue_stream_and_queue_are_unchanged");
        EndPacket independentlyParsed = Serializer.Deserialize<EndPacket>(queued.stream);
        Check(independentlyParsed.idbattle_report == 987 && independentlyParsed.iswin == win, "normal_handler_can_parse_original_stream_after_observer");
        RootUI.m_client.messages.Remove(queued); queued.stream.Close();
        w.Battle.ReportID = 987; w.Battle.BattleFighting = w.Battle.bNeedWaitForBattleEnd = w.Battle.bUseBattleEnd = false;
        BattleNormalResultUI result = Attach(new BattleNormalResultUI { battle_ui = w.Battle, m_reportid = 987 }, w.BattleRoot, w.Battle.gameObject);
        w.Observe(w.Global, w.Campaign, w.Battle, result); o.Observe(ReadBattleFrame(w));
        Check(o.Status == "SETTLED" && o.Outcome == (win ? "WIN" : "LOSS") && o.Evidence == "captured_queued_authoritative_receipt_applied", "production_queue_to_observer_late1032");
        }
        foreach (ushort op in new ushort[] { 1025, 979 })
        {
            RootUI.m_client.m_handlers[op] = new object(); Check(ReadBattleFrame(w).RefreshPending, "pending_handler_blocks_progress_" + op); RootUI.m_client.m_handlers.Clear();
            RootUI.m_client.messages.Add(new QueueMessage { op = op }); Check(ReadBattleFrame(w).RefreshPending, "pending_queue_blocks_progress_" + op); RootUI.m_client.messages.Clear();
        }
        RootUI.m_client.m_handlers[(ushort)1032] = new object(); Check(ReadBattleFrame(w).FinalPending, "old_final_handler_blocks_new_attempt"); RootUI.m_client.m_handlers.Clear();
        foreach (string failure in new string[] { "position", "closed", "empty", "oversize", "malformed", "many", "bound", "uintmax" })
        {
            RootUI.m_client.messages.Clear(); queued = QueueEnd(new EndPacket { idcampaign = 12, idtroop = 7, idbattle_report = 987 });
            if (failure == "position") queued.stream.Position = 1;
            if (failure == "closed") queued.stream.Close();
            if (failure == "empty") queued.stream = new MemoryStream();
            if (failure == "oversize") queued.stream = new MemoryStream(new byte[65537]);
            if (failure == "malformed") queued.stream = new MemoryStream(new byte[] { 128 });
            if (failure == "uintmax") queued = QueueEnd(new EndPacket { result = UInt32.MaxValue, type = UInt32.MaxValue, idcampaign = UInt32.MaxValue, idtroop = UInt32.MaxValue, idbattle_report = UInt64.MaxValue });
            RootUI.m_client.messages.Add(queued);
            if (failure == "many") for (int i = 0; i < 8; i++) RootUI.m_client.messages.Add(QueueEnd(new EndPacket()));
            if (failure == "bound") for (int i = 0; i < 64; i++) RootUI.m_client.messages.Add(new QueueMessage { op = 1 });
            CampaignFrame read = ReadBattleFrame(w);
            if (failure == "uintmax") Check(read.QueueError == null && read.QueuedPackets[0].Packet.Result == -1 && read.QueuedPackets[0].Packet.ReportId == UInt64.MaxValue, "queue_uint32max_is_invalid_without_helper_failure");
            else Check(read.QueueError != null, "queue_" + failure + "_fail_closed");
        }
        RootUI.m_client.messages.Clear(); QueueClient client = RootUI.m_client;
        ManualResetEvent locked = new ManualResetEvent(false), release = new ManualResetEvent(false);
        Thread owner = new Thread(delegate() { lock (client) { locked.Set(); release.WaitOne(); } }); owner.Start();
        Check(locked.WaitOne(1000), "fixture_owns_client_monitor");
        try { Check(!ReadBattleFrame(w).QueueObserved, "zero_wait_contention_does_not_fake_empty_queue"); }
        finally { release.Set(); owner.Join(); locked.Close(); release.Close(); }
        CampaignFrame before = Baseline(), f = Active(before); f.UseEnd = true; CampaignObserver pending = Started(before); pending.Observe(f); Applied(f, true); f.QueueObserved = false;
        pending.Observe(f); Check(pending.Status == "WAITING", "terminal_requires_successful_queue_witness"); f.QueueObserved = true; pending.Observe(f); Check(pending.Outcome == "WIN", "terminal_can_recover_after_queue_contention");
        World staged = new World(); staged.Campaign.m_troops[(byte)7] = new TroopData(); staged.Observe(staged.Global, staged.Campaign, staged.Battle);
        CampaignFrame stageBefore = ReadBattleFrame(staged); string stageReason;
        Check(staged.Runtime.Observer.Begin(A, 1, stageBefore, out stageReason), "hidden_transition_begin_" + stageReason);
        staged.Campaign.m_troopID = 0; Set(staged.Runtime, "troopId", 0);
        CampaignFrame hidden = ReadBattleFrame(staged); staged.Runtime.Observer.Observe(hidden);
        Check(object.ReferenceEquals(hidden.Target, stageBefore.Target) && object.ReferenceEquals(hidden.TargetData, stageBefore.TargetData) && staged.Runtime.Observer.Status == "SUBMITTED", "hidden_battle_selection_clear_keeps_original_target_anchor");
    }
    private static void Set(object target, string name, object value)
    { typeof(CampaignModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value); }
    private static T Get<T>(object target, string name) { return (T)typeof(CampaignModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target); }
    private static T Attach<T>(T value, RootMessenger root, UnityEngine.GameObject game) where T : StubUi
    { value.rmg = root; value.gameObject = game == null ? new UnityEngine.GameObject() : game; value.transform.root = root.transform; return value; }
    private static Command Cmd(string action)
    { return new Command { Sequence = 2, Name = "campaign-command", CampaignAction = action, ExpectedRevision = 1, CampaignId = 12, TroopId = 7, AttemptId = action == "start" ? A : "" }; }
    private static void ModelRules()
    {
        Global.extern_enabled = true;
        RootMessenger root = new RootMessenger { LevelID = "Campaign" }, globalRoot = new RootMessenger { LevelID = "Root" };
        Model.ContextRoot = root; RootUI global = Attach(new RootUI(), globalRoot, null); Model.GlobalObject = global.gameObject;
        CampaignUI campaign = new CampaignUI(); TroopData troop = new TroopData(); campaign.m_troops.Add((byte)7, troop);
        Attach(campaign, root, null);
        CampaignTroopScript script = new CampaignTroopScript { ParentUI = campaign, Troop = troop, gameObject = troop.mesh };
        troop.mesh.components.Add(script); troop.mesh.transform.root = root.transform;
        CampaignTroopUI panel = new CampaignTroopUI { m_parent = campaign, m_troop = troop };
        FormationUI form = new FormationUI { OnAttack = panel.Attack() };
        Attach(panel, root, campaign.gameObject); Attach(form, root, null);
        CampaignFrame before = Baseline(); before.Campaign = campaign;
        CampaignModel model = new CampaignModel(new Model()); model.Revision = 1; model.Available = true; model.Phase = "CAMPAIGN";
        Set(model, "common", new Snapshot { Main = new MainUI(), Root = root, Character = new CharacterData() });
        Set(model, "currentRoot", root); Set(model, "globalUi", global); Set(model, "characterId", "18446744073709551615");
        Set(model, "campaign", campaign); Set(model, "panel", panel); Set(model, "formation", form); Set(model, "campaignId", 12); Set(model, "troopId", 7); Set(model, "frame", before);
        int publications = 0; CampaignPublication publish = delegate { publications++; }; string why;
        troop.click_mode = 1;
        Check(!model.Execute(Cmd("select"), publish, out why) && campaign.SelectCalls == 0 && publications == 0 && why == "normal_troop_selection_gate:direct_mode", "direct_mode_never_selects");
        troop.click_mode = 255;
        Check(!model.Execute(Cmd("select"), publish, out why) && campaign.SelectCalls == 0 && publications == 0 && why == "normal_troop_selection_gate:click_mode_unknown", "unknown_mode_never_selects");
        foreach (byte mode in new byte[] { 0, 2, 3 })
        {
            troop.click_mode = mode; troop.show_arrow = false;
            int calls = campaign.SelectCalls;
            Check(model.Execute(Cmd("select"), publish, out why) && campaign.SelectCalls == calls + 1, "mesh_non_direct_mode_" + mode + "_needs_no_bubble_arrow");
        }
        campaign.SelectCalls = 0; publications = 0; troop.show_arrow = true;
        troop.click_mode = 3;
        Check(model.Execute(Cmd("select"), publish, out why) && campaign.SelectCalls == 1, "normal_selection");
        Command stale = Cmd("select"); stale.ExpectedRevision = 2;
        Check(!model.Execute(stale, publish, out why) && campaign.SelectCalls == 1 && why == "campaign_revision_changed", "stale_context_rejected");
        int selected = campaign.SelectCalls;
        UnityEngine.GameObject savedMesh = troop.mesh; troop.mesh = null;
        Check(!model.Execute(Cmd("select"), publish, out why) && why == "normal_troop_selection_gate:mesh_missing" && campaign.SelectCalls == selected, "missing_mesh_never_selects");
        troop.mesh = savedMesh; troop.mesh.components.Clear();
        Check(!model.Execute(Cmd("select"), publish, out why) && why == "normal_troop_selection_gate:mesh_script_not_unique" && campaign.SelectCalls == selected, "missing_mesh_script_never_selects");
        troop.mesh.components.Add(script); script.gameObject = new UnityEngine.GameObject();
        Check(!model.Execute(Cmd("select"), publish, out why) && why == "normal_troop_selection_gate:mesh_script_owner" && campaign.SelectCalls == selected, "script_must_belong_to_actual_mesh");
        script.gameObject = troop.mesh;
        troop.mesh.activeInHierarchy = false;
        Check(!model.Execute(Cmd("select"), publish, out why) && why == "normal_troop_selection_gate:mesh_inactive" && campaign.SelectCalls == selected, "inactive_mesh_never_selects");
        troop.mesh.activeInHierarchy = true; script.interaction = false;
        Check(!model.Execute(Cmd("select"), publish, out why) && why == "normal_troop_selection_gate:mesh_script_disabled" && campaign.SelectCalls == selected, "disabled_mesh_script_never_selects");
        script.interaction = true; script.ParentUI = new CampaignUI();
        Check(!model.Execute(Cmd("select"), publish, out why) && why == "normal_troop_selection_gate:mesh_script_owner" && campaign.SelectCalls == selected, "wrong_mesh_parent_never_selects");
        script.ParentUI = campaign; script.Troop = new TroopData();
        Check(!model.Execute(Cmd("select"), publish, out why) && why == "normal_troop_selection_gate:mesh_script_owner" && campaign.SelectCalls == selected, "wrong_mesh_troop_never_selects");
        script.Troop = troop; troop.mesh.components.Add(new CampaignTroopScript());
        Check(!model.Execute(Cmd("select"), publish, out why) && why == "normal_troop_selection_gate:mesh_script_not_unique" && campaign.SelectCalls == selected, "duplicate_mesh_script_never_selects");
        troop.mesh.components.RemoveAt(1); troop.mesh.transform.root = globalRoot.transform;
        Check(!model.Execute(Cmd("select"), publish, out why) && why == "normal_troop_selection_gate:mesh_root" && campaign.SelectCalls == selected, "wrong_mesh_hierarchy_never_selects");
        troop.mesh.transform.root = root.transform; script.enabled = false;
        Check(!model.Execute(Cmd("select"), publish, out why) && why == "normal_troop_selection_gate:mesh_script_disabled" && campaign.SelectCalls == selected, "disabled_mesh_component_never_selects");
        script.enabled = true; campaign.m_bPostInit = true;
        Check(!model.Execute(Cmd("select"), publish, out why) && why == "normal_troop_selection_gate:post_init", "animation_gate_retained_with_exact_reason");
        campaign.m_bPostInit = false; troop.killed = true;
        Check(!model.Execute(Cmd("select"), publish, out why) && why == "normal_troop_selection_gate:troop_killed", "completed_troop_never_selects");
        troop.killed = false; campaign.m_campaign.finished = true;
        Check(!model.Execute(Cmd("select"), publish, out why) && why == "normal_troop_selection_gate:campaign_finished", "completed_campaign_never_selects");
        campaign.m_campaign.finished = false;
        Check(publications == 1, "selection_negatives_never_publish");
        model.Phase = "TROOP"; troop.limit_count = 0;
        Check(!model.Execute(Cmd("prepare"), publish, out why) && panel.PrepareCalls == 0, "no_count_purchase_path");
        troop.limit_count = -2;
        Check(!model.Execute(Cmd("prepare"), publish, out why) && panel.PrepareCalls == 0 && why == "formation_prepare_gate:limit_count", "unknown_negative_count_never_prepares");
        troop.limit_count = -1;
        Check(model.Execute(Cmd("prepare"), publish, out why) && panel.PrepareCalls == 1, "ordinary_unlimited_minus_one_prepares");
        panel.PrepareCalls = 0; Global.GuideClears = 0;
        troop.limit_count = 1;
        Check(model.Execute(Cmd("prepare"), publish, out why) && panel.PrepareCalls == 1 && Global.GuideClears == 1, "ordinary_attack_branch");
        model.Phase = "FORMATION"; form.viewonly = true;
        Check(!model.Execute(Cmd("start"), publish, out why) && form.StartCalls == 0, "viewonly_never_starts");
        form.viewonly = false;
        Check(model.Execute(Cmd("start"), publish, out why) && form.StartCalls == 1 && form.onfight && form.waitstate == 2, "ordinary_start_branch");
        Check(form.currentSkill == 99 && form.currentArmy == 55 && form.currentForm == 2, "configuration_preserved");
        Check(model.Observer.Status == "SUBMITTED" && model.Observer.MutationAttempted, "attempt_owned_before_start");
        Check(!model.Execute(Cmd("start"), publish, out why) && form.StartCalls == 1, "duplicate_start_rejected");
        Check(publications == 4, "only_actual_commands_publish_before_invoke");
        ScenarioUI dialog = Attach(new ScenarioUI { m_prevUI = root }, globalRoot, global.gameObject); Set(model, "scenario", dialog); model.Phase = "SCENARIO";
        dialog.m_scenario.sections[0] = new ScenarioSection { triggers = "unreviewed" };
        Check(!model.Execute(Cmd("continue"), publish, out why) && dialog.SkipCalls == 0, "update_cannot_invoke_gui_skip");
        dialog.m_bSectionEnd = false;
        Check(model.QueueGui(Cmd("continue"), out why), "visible_skip_does_not_wait_for_text_or_reject_normal_triggers");
        Check(model.ExecuteGui(Cmd("continue"), publish, out why) && dialog.SkipCalls == 1 && dialog.LastRectX == 200, "normal_skip_handler_in_gui");
        Check(!model.ExecuteGui(Cmd("continue"), publish, out why) && dialog.SkipCalls == 1, "same_gui_command_consumed_once");
    }
    private static void ProtocolRules()
    {
        string prefix = "version=3\nnonce=" + A + "\nsequence=1\ncommand=campaign-command\n";
        string fields = "action=start\nexpected_revision=1\ncampaign_id=12\ntroop_id=7\nattempt_id=" + A + "\n";
        Command parsed = Command.Parse(prefix + fields, A, 0);
        Check(parsed != null && parsed.AttemptId == A && parsed.CampaignId == 12 && parsed.TroopId == 7, "typed_parser");
        foreach (string damaged in new string[] { fields.Replace("action=start", "action=eval"), fields.Replace("revision=1", "revision=01"), fields.Replace("troop_id=7", "troop_id=256"), fields + "method=Send\n", fields.Replace("attempt_id=" + A, "attempt_id=") })
            Check(Command.Parse(prefix + damaged, A, 0) == null, "typed_negative");
        Check(Command.Parse(prefix.Replace("version=3", "version=2") + fields, A, 0) == null, "v2_rejected");
    }
    private sealed class World
    {
        internal RootMessenger Root = new RootMessenger { LevelID = "Campaign" }, GlobalRoot = new RootMessenger { LevelID = "Root" }, BattleRoot = new RootMessenger { LevelID = "Battle" };
        internal RootUI Global;
        internal MainUI Main;
        internal CampaignUI Campaign;
        internal BattleUI Battle;
        internal ScenarioUI Scenario;
        internal CampaignModel Runtime;
        internal Snapshot Snapshot;
        internal World()
        {
            global::Global.extern_enabled = true;
            Global = Attach(new RootUI(), GlobalRoot, null); Main = Attach(new MainUI(), Root, null);
            Campaign = Attach(new CampaignUI(), Root, null);
            Battle = Attach(new BattleUI { m_prevUI = Root, m_parentUI = Campaign }, BattleRoot, null);
            Battle.enabled = false;
            Scenario = Attach(new ScenarioUI { m_prevUI = Root }, GlobalRoot, Global.gameObject); Scenario.enabled = false;
            Snapshot = new Snapshot { Main = Main, Root = Root, Character = new CharacterData() };
            Runtime = new CampaignModel(new Model()); Runtime.Revision = 1;
            Set(Runtime, "common", Snapshot); Set(Runtime, "characterId", "18446744073709551615"); Set(Runtime, "campaignId", 12); Set(Runtime, "troopId", 7);
        }
        internal void Observe(params UnityEngine.Object[] objects)
        {
            Model.ContextRoot = Root; Model.GlobalObject = Global.gameObject; Model.BattleObject = Battle.gameObject;
            Model.Objects = objects; Runtime.SelectContext(objects, Root); Runtime.EvaluateContext(objects);
        }
    }
    private static string sampleDiagnostics = "null";
    private static string sampleSettlement = "null";
    private static string sampleSettlementPacket = "null";
    private static void SettlementDiagnosticsRules()
    {
        World w = new World();
        w.Campaign.m_troops[(byte)7] = new TroopData { killed = true, canattack = false, visible = false, finish_count = 1, finish_max = 1 };
        w.Campaign.m_campaign.finished = true;
        w.Battle.ReportID = UInt64.MaxValue; w.Battle.m_BattleLength = -0.125f;
        BattleNormalResultUI result = Attach(new BattleNormalResultUI { battle_ui = w.Battle, m_reportid = 123 }, w.BattleRoot, w.Battle.gameObject);
        w.Observe(w.Global, w.Campaign, w.Battle, result);
        MethodInfo method = typeof(CampaignModel).GetMethod("SettlementDiagnostics", BindingFlags.Instance | BindingFlags.NonPublic);
        sampleSettlement = (string)method.Invoke(w.Runtime, new object[0]);
        Check(sampleSettlement.Contains("\"report_id\":\"18446744073709551615\"") && sampleSettlement.Contains("\"report_id\":\"123\""), "diagnostic_raw_reports_are_separate_u64_strings");
        Check(sampleSettlement.Contains("\"finish_count\":1,\"finish_max\":1,\"killed\":true,\"canattack\":false") && sampleSettlement.Contains("\"campaign_finished\":true") && sampleSettlement.Contains("\"matches_battle_target\":true"), "diagnostic_target_ignores_eligible_filter");
        Check(sampleSettlement.Contains("\"anchors_available\":false,\"zero_observed\":null") && sampleSettlement.Contains("\"data_anchor_match\":null"), "replacement_helper_does_not_invent_old_anchors");
        Check(sampleSettlement.Contains("\"pending_present\":false,\"packet\":null") && sampleSettlement.Contains("\"playback_remaining\":-0.125"), "diagnostic_current_packet_absence_and_finite_playback");
        Check(w.Runtime.Observer.Status == "NONE" && !w.Runtime.Observer.MutationAttempted && w.Battle.ExitCalls == 0, "diagnostic_does_not_start_settle_or_ack");
        foreach (float invalid in new float[] { Single.NaN, Single.PositiveInfinity, Single.NegativeInfinity })
        {
            w.Battle.m_BattleLength = invalid;
            Check(((string)method.Invoke(w.Runtime, new object[0])).Contains("\"playback_remaining\":null"), "nonfinite_diagnostic_is_null");
        }
        w.Battle.m_pktBattleEnd = new PendingBattleEnd { pkt = new EndPacket { result = 1, type = 255, idcampaign = 57, idtroop = 3, idbattle_report = UInt64.MaxValue, iswin = false } };
        string packet = (string)method.Invoke(w.Runtime, new object[0]);
        Check(packet.Contains("\"pending_present\":true,\"packet\":{\"result\":1,\"type\":255,\"iswin\":false,\"idcampaign\":57,\"idtroop\":3"), "diagnostic_keeps_invalid_authority_packet_raw");
        Check(w.Runtime.Observer.Status == "NONE", "raw_packet_cannot_promote_observer");
        w.Battle.m_pktBattleEnd.pkt = new EndPacket { result = UInt32.MaxValue, type = UInt32.MaxValue, idcampaign = UInt32.MaxValue, idtroop = UInt32.MaxValue, idbattle_report = UInt64.MaxValue };
        sampleSettlementPacket = (string)method.Invoke(w.Runtime, new object[0]);
        Check(sampleSettlementPacket.Contains("\"result\":4294967295,\"type\":4294967295,\"iswin\":false,\"idcampaign\":4294967295,\"idtroop\":4294967295,\"report_id\":\"18446744073709551615\""), "production_diagnostic_preserves_all_packet_uint32_maxima");
        w.Battle.m_pktBattleEnd.pkt = null;
        Check(((string)method.Invoke(w.Runtime, new object[0])).Contains("\"pending_present\":true,\"packet\":null"), "empty_pending_wrapper_is_distinct");
        w.Battle.idtroop = 255;
        Check(((string)method.Invoke(w.Runtime, new object[0])).Contains("\"target\":null"), "diagnostic_missing_exact_target_is_null");
        Set(w.Runtime, "battle", null); Set(w.Runtime, "result", null);
        Check(((string)method.Invoke(w.Runtime, new object[0])).Contains("\"battle\":null,\"result\":null,\"target\":null"), "diagnostic_missing_views_are_null");
        CampaignFrame before = Baseline(), active = Active(before); CampaignObserver o = Started(before); o.Observe(active);
        string debug = o.DebugJson(active);
        Check(debug.Contains("\"zero_observed\":true,\"battle_observed\":true,\"receipt_captured\":false") && debug.Contains("\"report_token_match\":true"), "debug_only_exposes_existing_observed_anchors");
        active.Character = new object();
        Check(o.DebugJson(active).Contains("\"character_match\":false") && o.Status == "WAITING", "debug_mismatch_does_not_observe_or_change_status");
    }
    private static void DiagnosticsAndChapterRules()
    {
        World w = new World(); TroopData first = null;
        for (byte id = 1; id <= 9; id++)
        {
            TroopData troop = new TroopData { idtroop = id, click_mode = 0, show_arrow = false, limit_count = -2,
                finish_count = 2, finish_max = 3, isNewbie = 1, Newbie_Skill = 249, Newbie_formation = UInt32.MaxValue,
                m_scenarioClickF = 11, m_scenarioVisible = 12, m_scenarioAttackF = 13, m_scenarioVictoryF = 14, m_scenarioFailure = 15, m_scenarioSystem = UInt32.MaxValue };
            troop.mesh.transform.root = w.Root.transform;
            troop.mesh.components.Add(new CampaignTroopScript { ParentUI = w.Campaign, Troop = troop, gameObject = troop.mesh });
            w.Campaign.m_troops.Add(id, troop); if (first == null) first = troop;
        }
        w.Campaign.m_troops.Add((byte)10, new TroopData { killed = true });
        w.Campaign.m_troops.Add((byte)11, new TroopData { visible = false });
        w.Observe(w.Global, w.Main, w.Campaign);
        MethodInfo diagnostics = typeof(CampaignModel).GetMethod("SelectionDiagnostics", BindingFlags.Instance | BindingFlags.NonPublic);
        sampleDiagnostics = (string)diagnostics.Invoke(w.Runtime, new object[0]);
        Check(sampleDiagnostics.Contains("\"character_campaign_id\":12") && sampleDiagnostics.Contains("\"limit_count\":-2"), "diagnostics_keep_character_campaign_and_signed_unknown_count");
        Check(sampleDiagnostics.Contains("\"Newbie_formation\":4294967295") && sampleDiagnostics.Contains("\"m_scenarioSystem\":4294967295"), "diagnostics_keep_fixed_uint_template_ids");
        Check(sampleDiagnostics.Contains("\"finish_count\":2,\"finish_max\":3") && sampleDiagnostics.Contains("\"m_scenarioVictoryF\":14"), "diagnostics_keep_actual_completion_and_victory_route");
        Check(sampleDiagnostics.Contains("\"troop_id\":8") && !sampleDiagnostics.Contains("\"troop_id\":9") && sampleDiagnostics.Contains("\"truncated\":true"), "diagnostics_only_eight_sorted_eligible_rows");
        Check(!sampleDiagnostics.Contains("\"troop_id\":10") && !sampleDiagnostics.Contains("\"troop_id\":11"), "diagnostics_exclude_killed_and_hidden_rows");
        Check(sampleDiagnostics.Contains("\"show_arrow\":false") && sampleDiagnostics.Contains("\"select_gate\":\"PASS\""), "diagnostics_prove_mesh_selection_independent_from_arrow");
        Check(w.Campaign.SelectCalls == 0 && !w.Runtime.Observer.MutationAttempted && w.Runtime.Observer.Status == "NONE", "diagnostics_read_does_not_invoke_or_start_attempt");
        first.limit_count = Int16.MinValue; first.mesh.activeInHierarchy = false;
        string negative = (string)diagnostics.Invoke(w.Runtime, new object[0]);
        Check(negative.Contains("\"limit_count\":-32768") && negative.Contains("\"select_gate\":\"mesh_inactive\""), "diagnostics_preserve_failed_mesh_and_full_signed_short");
        CampaignChapterClearUI chapter = Attach(new CampaignChapterClearUI { m_bNextChapter = false }, w.Root, w.Campaign.gameObject);
        w.Observe(w.Global, w.Main, w.Campaign, chapter);
        int publications = 0; string why; CampaignPublication publish = delegate { publications++; };
        Check(!w.Runtime.Execute(Cmd("next_chapter"), publish, out why) && w.Global.Calls == 0 && publications == 0, "chapter_unsettled_still_rejected");
        w.Runtime.Observer.Status = "SETTLED"; chapter.m_bAnecdote = true;
        Check(!w.Runtime.Execute(Cmd("next_chapter"), publish, out why) && w.Global.Calls == 0 && publications == 0, "chapter_anecdote_still_rejected");
        chapter.m_bAnecdote = false; chapter.interaction = false;
        Check(!w.Runtime.Execute(Cmd("next_chapter"), publish, out why) && w.Global.Calls == 0 && publications == 0, "chapter_local_disable_still_rejected");
        chapter.interaction = true; chapter.rmg = w.GlobalRoot;
        w.Observe(w.Global, w.Main, w.Campaign, chapter);
        Check(!w.Runtime.Execute(Cmd("next_chapter"), publish, out why) && w.Global.Calls == 0 && publications == 0, "chapter_wrong_root_still_rejected");
        chapter.rmg = w.Root; chapter.enabled = true; w.Observe(w.Global, w.Main, w.Campaign, chapter);
        Check(w.Runtime.Execute(Cmd("next_chapter"), publish, out why) && w.Global.Calls == 1 && w.Root.Disables == 1 && publications == 1 && !chapter.enabled, "chapter_false_display_flag_uses_same_normal_button_branch");
    }
    private static World RewardWorld(out BattleResultUI reward)
    {
        World w = new World(); RootUI.m_client = new QueueClient();
        w.Campaign.m_troops.Add((byte)7, new TroopData()); w.Battle.ReportID = 44;
        w.Observe(w.Global, w.Main, w.Campaign, w.Battle);
        MethodInfo capture = typeof(CampaignModel).GetMethod("BattleFrame", BindingFlags.Instance | BindingFlags.NonPublic);
        CampaignFrame before = (CampaignFrame)capture.Invoke(w.Runtime, new object[0]); string why;
        Check(w.Runtime.Observer.Begin(A, 1, before, out why), "reward_begin_" + why); w.Runtime.Observer.Invoking();
        w.Battle.enabled = true; w.Battle.ReportID = 0; w.Battle.BattleFighting = w.Battle.bNeedWaitForBattleEnd = true;
        w.Battle.m_BattleLength = 10; w.Battle.m_pktBattleEnd = new PendingBattleEnd { pkt = new EndPacket { idcampaign = 12, idtroop = 7, idbattle_report = 987, iswin = true } };
        w.Observe(w.Global, w.Campaign, w.Battle);
        w.Runtime.Observer.Observe((CampaignFrame)capture.Invoke(w.Runtime, new object[0]));
        w.Battle.ReportID = 987; w.Battle.BattleFighting = w.Battle.bNeedWaitForBattleEnd = false; w.Battle.m_pktBattleEnd = null;
        BattleNormalResultUI normal = Attach(new BattleNormalResultUI { battle_ui = w.Battle, m_reportid = 987 }, w.BattleRoot, w.Battle.gameObject);
        w.Observe(w.Global, w.Campaign, w.Battle, normal);
        w.Runtime.Observer.Observe((CampaignFrame)capture.Invoke(w.Runtime, new object[0]));
        Check(w.Runtime.Observer.Status == "SETTLED", "reward_requires_actual_observer_settlement");
        normal.enabled = w.Battle.enabled = false; w.Campaign.m_troopID = 0; Set(w.Runtime, "troopId", 0);
        reward = Attach(new BattleResultUI { m_prevUI = w.Root, OnClose = w.Campaign.BonusOpen }, w.BattleRoot, w.Battle.gameObject);
        Global.extern_enabled = false; RewardObserve(w, reward); return w;
    }
    private static void RewardObserve(World w, BattleResultUI reward)
    {
        w.Observe(w.Global, w.Main, w.Campaign, w.Battle, reward);
        MethodInfo capture = typeof(CampaignModel).GetMethod("BattleFrame", BindingFlags.Instance | BindingFlags.NonPublic);
        Set(w.Runtime, "frame", capture.Invoke(w.Runtime, new object[0])); w.Runtime.EvaluateContext(Model.Objects);
    }
    private static void RewardRules()
    {
        BattleResultUI reward; World w = RewardWorld(out reward); int publications = 0; string why;
        Check(w.Runtime.Available && w.Runtime.Gate == "PASS" && w.Runtime.Phase == "RESULT" && Get<int>(w.Runtime, "troopId") == 0, "real_reward_global_false_and_selection_zero_is_available");
        foreach (string action in new string[] { "enter", "select", "prepare", "start", "continue", "next_chapter" })
            Check(!w.Runtime.Execute(Cmd(action), delegate { publications++; }, out why) && publications == 0, "reward_global_false_does_not_admit_" + action);
        Command zero = Cmd("ack_result"); zero.TroopId = 0;
        Check(!w.Runtime.Execute(zero, delegate { publications++; }, out why) && publications == 0, "reward_uses_original_attempt_troop_not_zero_selection");
        Check(w.Runtime.Execute(Cmd("ack_result"), delegate { publications++; }, out why) && publications == 1 && w.Campaign.BonusCalls == 1 && !reward.enabled, "reward_normal_ok_hides_then_invokes_owned_callback_once");
        RewardObserve(w, reward);
        Check(!w.Runtime.Execute(Cmd("ack_result"), delegate { publications++; }, out why) && publications == 1 && w.Campaign.BonusCalls == 1, "hidden_reward_cannot_confirm_twice");
        foreach (string invalid in new string[] { "not_settled", "wrong_troop", "wrong_report", "wrong_battle_target", "wrong_character", "cross", "replay", "surrender", "waiting", "use_end", "fighting", "noncampaign", "other_mode", "disabled", "wrong_callback", "wrong_owner", "callback_multiple" })
        {
            w = RewardWorld(out reward); publications = 0;
            if (invalid == "not_settled") w.Runtime.Observer.Status = "UNKNOWN";
            if (invalid == "wrong_troop") reward.m_troopID = 8;
            if (invalid == "wrong_report") w.Battle.ReportID = 988;
            if (invalid == "wrong_battle_target") w.Battle.idtroop = 8;
            if (invalid == "wrong_character") w.Snapshot.Character = new CharacterData();
            if (invalid == "cross") w.Battle.IsCrossServer = true;
            if (invalid == "replay") w.Battle.ViewType = enumBattleViewType.viewReport;
            if (invalid == "surrender") w.Battle.IsSurrended = true;
            if (invalid == "waiting") w.Battle.bNeedWaitForBattleEnd = true;
            if (invalid == "use_end") w.Battle.bUseBattleEnd = true;
            if (invalid == "fighting") w.Battle.BattleFighting = true;
            if (invalid == "noncampaign") reward.m_bCampaign = false;
            if (invalid == "other_mode") reward.mine_ui = "skytower";
            if (invalid == "disabled") reward.interaction = false;
            if (invalid == "wrong_callback") reward.OnClose = new CampaignUI().BonusOpen;
            if (invalid == "wrong_owner") reward.rmg = w.Root;
            if (invalid == "callback_multiple") reward.OnClose += w.Campaign.BonusOpen;
            RewardObserve(w, reward);
            Check(!w.Runtime.Available && !w.Runtime.Execute(Cmd("ack_result"), delegate { publications++; }, out why) && publications == 0 && w.Campaign.BonusCalls == 0, "invalid_reward_never_exempts_global_gate_" + invalid);
        }
        Global.extern_enabled = true;
    }
    private static void ContextRules()
    {
        World w = new World(); w.Campaign.enabled = false;
        CampaignUI anecdote = Attach(new CampaignUI(), new RootMessenger { LevelID = "Anecdote" }, null), fairy = Attach(new CampaignUI(), new RootMessenger { LevelID = "Fairy" }, null), talent = Attach(new CampaignUI(), new RootMessenger { LevelID = "TalentCampaign" }, null);
        anecdote.enabled = fairy.enabled = talent.enabled = false;
        w.Observe(w.Global, w.Main, w.Campaign, anecdote, fairy, talent);
        Check(object.ReferenceEquals(Get<UnityEngine.Component>(w.Runtime, "campaign"), w.Campaign), "preloaded_cross_root_campaigns_do_not_ambig_current");
        Check(w.Runtime.Json().Contains("\"campaign\":{\"total\":4,\"visible\":0,\"matching\":1}"), "candidate_diagnostics_report_scoped_counts");
        CampaignUI duplicate = Attach(new CampaignUI(), w.Root, null); duplicate.enabled = false;
        w.Observe(w.Global, w.Main, w.Campaign, duplicate, anecdote);
        Check(!w.Runtime.Available && w.Runtime.Gate == "campaign_context_ambiguous_campaign", "same_root_duplicate_stays_blocked_without_crashing_helper");
        World bad = new World(); bad.Campaign.transform.root = bad.GlobalRoot.transform;
        bad.Observe(bad.Global, bad.Main, bad.Campaign);
        Check(!bad.Runtime.Available && Get<UnityEngine.Component>(bad.Runtime, "campaign") == null, "cached_rmg_alone_cannot_select_wrong_hierarchy");
        bad.Campaign.transform.root = bad.Root.transform; bad.Campaign.rmg = new RootMessenger { LevelID = "Campaign" };
        bad.Observe(bad.Global, bad.Main, bad.Campaign);
        Check(!bad.Runtime.Available, "same_level_name_is_not_root_identity");
        World changed = new World(); changed.Snapshot.Root = changed.GlobalRoot;
        changed.Observe(changed.Global, changed.Main, changed.Campaign);
        Check(!changed.Runtime.Available && changed.Runtime.Gate == "campaign_current_root_snapshot_mismatch", "scene_transition_root_mismatch_blocks");
        World fighting = new World(); fighting.Root.gameObject.activeInHierarchy = false; fighting.Campaign.gameObject.activeInHierarchy = false;
        fighting.Snapshot.Main = null; fighting.Snapshot.Root = null; fighting.Battle.enabled = true;
        fighting.Observe(fighting.Global, fighting.Battle);
        Check(fighting.Runtime.Phase == "BATTLE" && fighting.Runtime.Available && object.ReferenceEquals(Get<UnityEngine.Component>(fighting.Runtime, "campaign"), fighting.Campaign), "inactive_prev_root_and_no_active_main_preserve_bound_battle");
        fighting.Battle.m_prevUI = fighting.GlobalRoot; fighting.Observe(fighting.Global, fighting.Battle);
        Check(!fighting.Runtime.Available && Get<UnityEngine.Component>(fighting.Runtime, "battle") == null, "wrong_battle_previous_root_rejected");
        fighting.Battle.m_prevUI = fighting.Root; fighting.Battle.rmg = fighting.Root; fighting.Battle.transform.root = fighting.Root.transform;
        fighting.Observe(fighting.Global, fighting.Battle);
        Check(!fighting.Runtime.Available, "battle_owner_must_be_separate_fixed_battle_root");
        World story = new World(); story.Scenario.enabled = true; story.Scenario.m_bSectionEnd = false; story.Scenario.interaction = false;
        story.Scenario.m_scenario.sections[0] = new ScenarioSection { triggers = "normal_story_trigger" };
        story.Observe(story.Global, story.Main, story.Campaign, story.Scenario); Global.extern_enabled = false;
        story.Runtime.EvaluateContext(Model.Objects);
        Check(story.Runtime.Available && story.Runtime.Phase == "SCENARIO", "normal_skip_remains_available_when_root_global_and_local_input_disabled");
        int publications = 0; string why; Command skip = Cmd("continue");
        Check(story.Runtime.QueueGui(skip, out why) && story.Runtime.ExecuteGui(skip, delegate { publications++; }, out why), "external_scenario_owned_by_prev_root_can_skip");
        Check(story.Scenario.SkipCalls == 1 && publications == 1, "skip_calls_original_handler_once");
        foreach (string action in new string[] { "enter", "select", "prepare", "start", "ack_result", "next_chapter" })
            Check(!story.Runtime.Execute(Cmd(action), delegate { publications++; }, out why) && why == "global_input_disabled", "global_false_never_exempts_" + action);
        Global.extern_enabled = true; story.Runtime.Phase = "SCENARIO"; story.Runtime.Available = true;
        BattleNormalResultUI behindResult = Attach(new BattleNormalResultUI { battle_ui = story.Battle }, story.BattleRoot, story.Battle.gameObject);
        CampaignChapterClearUI behindChapter = Attach(new CampaignChapterClearUI(), story.Root, story.Campaign.gameObject);
        story.Scenario.m_iSection = 0;
        story.Observe(story.Global, story.Main, story.Campaign, story.Scenario, story.Battle, behindResult, behindChapter);
        Check(Get<UnityEngine.Component>(story.Runtime, "result") == behindResult && Get<UnityEngine.Component>(story.Runtime, "chapter") == behindChapter && story.Runtime.Phase == "SCENARIO", "background_result_and_chapter_are_actually_present_in_fixture");
        foreach (string action in new string[] { "ack_result", "next_chapter" })
            Check(!story.Runtime.Execute(Cmd(action), delegate { publications++; }, out why) && why == "scenario_action_only", "visible_background_result_cannot_bypass_scenario_" + action);
        story.Scenario.m_iSection = 0; story.Scenario.m_prevUI = story.GlobalRoot;
        story.Observe(story.Global, story.Main, story.Campaign, story.Scenario);
        Check(!story.Runtime.Available && Get<UnityEngine.Component>(story.Runtime, "scenario") == null, "external_scenario_wrong_previous_root_blocks");
        World queued = new World(); queued.Scenario.enabled = true; queued.Observe(queued.Global, queued.Main, queued.Campaign, queued.Scenario);
        Check(queued.Runtime.QueueGui(Cmd("continue"), out why), "gui_context_freeze_queued");
        queued.Snapshot.Character = new CharacterData();
        Check(!queued.Runtime.ExecuteGui(Cmd("continue"), delegate { publications++; }, out why) && queued.Scenario.SkipCalls == 0, "same_id_character_object_replacement_blocks_gui");
        World revision = new World(); revision.Scenario.enabled = true; revision.Observe(revision.Global, revision.Main, revision.Campaign, revision.Scenario);
        Check(revision.Runtime.QueueGui(Cmd("continue"), out why), "gui_revision_queued"); revision.Runtime.Revision++;
        Check(!revision.Runtime.ExecuteGui(Cmd("continue"), delegate { publications++; }, out why) && revision.Scenario.SkipCalls == 0 && why == "campaign_revision_changed", "gui_callback_rechecks_revision");
        World data = new World(); data.Scenario.enabled = true; data.Observe(data.Global, data.Main, data.Campaign, data.Scenario);
        Check(data.Runtime.QueueGui(Cmd("continue"), out why), "gui_data_queued"); data.Scenario.m_scenario = new Scenario();
        Check(!data.Runtime.ExecuteGui(Cmd("continue"), delegate { publications++; }, out why) && data.Scenario.SkipCalls == 0, "same_id_plot_replacement_is_not_the_queued_plot");
        World moved = new World(); moved.Scenario.enabled = true; moved.Observe(moved.Global, moved.Main, moved.Campaign, moved.Scenario);
        Check(moved.Runtime.QueueGui(Cmd("continue"), out why), "gui_root_queued"); Model.ContextRoot = moved.GlobalRoot;
        Check(!moved.Runtime.ExecuteGui(Cmd("continue"), delegate { publications++; }, out why) && moved.Scenario.SkipCalls == 0, "gui_callback_rechecks_current_root");
        World fade = new World(); fade.Scenario.enabled = true; fade.Scenario.visible_time = DateTime.Now; fade.Observe(fade.Global, fade.Main, fade.Campaign, fade.Scenario);
        Check(!fade.Runtime.QueueGui(Cmd("continue"), out why), "skip_button_does_not_exist_during_normal_gui_fade");
        World disabledGui = new World(); disabledGui.Scenario.enabled = true; disabledGui.Observe(disabledGui.Global, disabledGui.Main, disabledGui.Campaign, disabledGui.Scenario);
        Check(disabledGui.Runtime.QueueGui(Cmd("continue"), out why), "gui_enabled_checked_at_callback_not_update"); UnityEngine.GUI.enabled = false;
        Check(!disabledGui.Runtime.ExecuteGui(Cmd("continue"), delegate { publications++; }, out why) && disabledGui.Scenario.SkipCalls == 0, "disabled_gui_cannot_invoke_skip"); UnityEngine.GUI.enabled = true;
        World many = new World(); ArrayList list = new ArrayList(new object[] { many.Global, many.Main, many.Campaign });
        for (int i = 0; i < 12; i++) { CampaignUI c = Attach(new CampaignUI(), new RootMessenger { LevelID = "Anecdote" }, null); c.enabled = false; list.Add(c); }
        many.Observe((UnityEngine.Object[])list.ToArray(typeof(UnityEngine.Object)));
        Check(many.Runtime.Json().Contains("\"candidates_truncated\":true"), "campaign_diagnostics_are_bounded_to_eight");
    }
    private static World IntroWorld()
    {
        World w = new World(); w.Scenario.enabled = true; w.Scenario.m_scenario.id = 653;
        w.Scenario.m_iSection = w.Scenario.m_scenario.sections.Count + 1;
        w.Scenario.m_bSectionEnd = false; w.Scenario.m_bHasNewUI = true; w.Scenario.m_bWantNewUI = true; w.Scenario.m_bTrigger = true;
        w.Scenario.newUIList.Add(new ScenarioUI.newUIBtn()); w.Scenario.newUIList.Add(new ScenarioUI.newUIBtn { name = "another_fixed_intro" });
        w.Observe(w.Global, w.Main, w.Campaign, w.Scenario); return w;
    }
    private static void IntroRules()
    {
        int publications = 0, callbacks = 0; string why;
        World ready = IntroWorld(); ready.Scenario.OnClose = delegate { callbacks++; };
        Action callback = ready.Scenario.OnClose; object list = ready.Scenario.newUIList;
        Global.extern_enabled = false; ready.Scenario.interaction = false; ready.Runtime.EvaluateContext(Model.Objects);
        Check(ready.Runtime.Available && ready.Runtime.Json().Contains("\"gate\":\"PASS\""), "intro_is_normal_left_click_even_when_underlying_input_disabled");
        Check(!ready.Runtime.Execute(Cmd("continue"), delegate { publications++; }, out why) && !ready.Scenario.m_bMoving, "intro_update_cannot_run_gui_branch");
        Check(ready.Runtime.QueueGui(Cmd("continue"), out why) && !ready.Scenario.m_bMoving && ready.Scenario.SkipCalls == 0, "intro_queue_has_no_game_mutation");
        Check(!ready.Runtime.QueueGui(Cmd("continue"), out why) && why == "scenario_gui_command_pending", "intro_single_pending_command");
        Check(ready.Runtime.ExecuteGui(Cmd("continue"), delegate { Check(!ready.Scenario.m_bMoving, "intro_ownership_published_before_first_mutation"); publications++; }, out why), "intro_real_gui_starts_animation");
        Check(ready.Scenario.m_bMoving && ready.Scenario.SkipCalls == 0 && publications == 1, "intro_only_sets_original_moving_flag_once");
        Check(object.ReferenceEquals(ready.Scenario.newUIList, list) && ready.Scenario.newUIList.Count == 2 && object.ReferenceEquals(ready.Scenario.OnClose, callback) && callbacks == 0 && !ready.Scenario.m_bNext, "intro_preserves_list_close_callback_and_completion_for_game");
        Check(!ready.Runtime.ExecuteGui(Cmd("continue"), delegate { publications++; }, out why) && publications == 1, "intro_consumed_command_never_replays");
        ready.Runtime.EvaluateContext(Model.Objects);
        Check(ready.Runtime.Available && ready.Runtime.Json().Contains("WAITING_MOVEMENT") && !ready.Runtime.QueueGui(Cmd("continue"), out why), "intro_animation_waits_without_resubmitting_skip");
        // Emulate only the game's documented post-animation state. The bridge
        // must wait; it must not call Close, clear lists or fire OnClose itself.
        ready.Scenario.newUIList.Clear(); ready.Scenario.m_bNext = true; ready.Runtime.EvaluateContext(Model.Objects);
        Check(ready.Runtime.Json().Contains("WAITING_SCENARIO_END") && callbacks == 0, "intro_end_waits_for_natural_game_close_chain");
        foreach (string changed in new string[] { "list", "item", "count", "name", "position", "target", "width", "height", "texture", "ended", "moving", "has", "next", "section_end", "want", "trigger", "first", "callback", "section", "sections", "section_item", "section_trigger", "param0", "scenario_id", "templates", "data", "scenario", "role", "role_id", "root", "previous_root", "visible", "revision", "gui_disabled", "no_event" })
        {
            World w = IntroWorld(); w.Scenario.OnClose = delegate { callbacks++; };
            Check(w.Runtime.QueueGui(Cmd("continue"), out why), "intro_negative_queued_" + changed);
            ScenarioUI.newUIBtn item = (ScenarioUI.newUIBtn)w.Scenario.newUIList[0];
            if (changed == "list") w.Scenario.newUIList = new ArrayList(w.Scenario.newUIList);
            else if (changed == "item") w.Scenario.newUIList[0] = new ScenarioUI.newUIBtn();
            else if (changed == "count") w.Scenario.newUIList.RemoveAt(1);
            else if (changed == "name") item.name += "_changed";
            else if (changed == "position") item.curPos.x++;
            else if (changed == "target") item.targetPos.x++;
            else if (changed == "width") item.width++;
            else if (changed == "height") item.height++;
            else if (changed == "texture") item.tex = new object();
            else if (changed == "ended") item.bMovingEnd = true;
            else if (changed == "moving") w.Scenario.m_bMoving = true;
            else if (changed == "has") w.Scenario.m_bHasNewUI = false;
            else if (changed == "next") w.Scenario.m_bNext = true;
            else if (changed == "section_end") w.Scenario.m_bSectionEnd = true;
            else if (changed == "want") w.Scenario.m_bWantNewUI = !w.Scenario.m_bWantNewUI;
            else if (changed == "trigger") w.Scenario.m_bTrigger = !w.Scenario.m_bTrigger;
            else if (changed == "first") w.Scenario.m_bFirst = !w.Scenario.m_bFirst;
            else if (changed == "callback") w.Scenario.OnClose = null;
            else if (changed == "section") w.Scenario.m_iSection--;
            else if (changed == "sections") w.Scenario.m_scenario.sections.Add(new ScenarioSection());
            else if (changed == "section_item") w.Scenario.m_scenario.sections[0] = new ScenarioSection();
            else if (changed == "section_trigger") ((ScenarioSection)w.Scenario.m_scenario.sections[0]).triggers = "changed";
            else if (changed == "param0") w.Scenario.m_scenario.param0 = "system";
            else if (changed == "scenario_id") w.Scenario.m_scenario.id++;
            else if (changed == "templates") w.Scenario.m_scenario.m_sections = new ArrayList(w.Scenario.m_scenario.m_sections);
            else if (changed == "data") w.Scenario.m_scenario = new Scenario();
            else if (changed == "scenario") Set(w.Runtime, "scenario", new ScenarioUI());
            else if (changed == "role") w.Snapshot.Character = new CharacterData();
            else if (changed == "role_id") Set(w.Runtime, "characterId", "1");
            else if (changed == "root") Model.ContextRoot = w.GlobalRoot;
            else if (changed == "previous_root") w.Scenario.m_prevUI = w.GlobalRoot;
            else if (changed == "visible") w.Scenario.visible_time = DateTime.Now.AddSeconds(-2);
            else if (changed == "revision") w.Runtime.Revision++;
            else if (changed == "gui_disabled") UnityEngine.GUI.enabled = false;
            else if (changed == "no_event") UnityEngine.Event.current = null;
            int before = publications;
            Check(!w.Runtime.ExecuteGui(Cmd("continue"), delegate { publications++; }, out why) && w.Scenario.SkipCalls == 0 && publications == before, "intro_changed_context_never_invokes_" + changed);
            Check(!w.Runtime.ExecuteGui(Cmd("continue"), delegate { publications++; }, out why) && publications == before, "intro_rejected_command_is_consumed_" + changed);
            UnityEngine.GUI.enabled = true; UnityEngine.Event.current = new UnityEngine.Event();
        }
        foreach (string invalid in new string[] { "unknown_item", "duplicate", "oversized", "empty_name", "nan_position", "infinite_target", "zero_width", "nan_height", "no_texture", "early_draw", "loading", "collecting", "fade", "bad_flags" })
        {
            World w = IntroWorld(); ScenarioUI.newUIBtn item = (ScenarioUI.newUIBtn)w.Scenario.newUIList[0];
            if (invalid == "unknown_item") w.Scenario.newUIList[0] = new object();
            else if (invalid == "duplicate") w.Scenario.newUIList[1] = item;
            else if (invalid == "oversized") for (int i = 0; i < 7; i++) w.Scenario.newUIList.Add(new ScenarioUI.newUIBtn());
            else if (invalid == "empty_name") item.name = "";
            else if (invalid == "nan_position") item.curPos.x = Single.NaN;
            else if (invalid == "infinite_target") item.targetPos.y = Single.PositiveInfinity;
            else if (invalid == "zero_width") item.width = 0;
            else if (invalid == "nan_height") item.height = Single.NaN;
            else if (invalid == "no_texture") item.tex = null;
            else if (invalid == "early_draw") w.Scenario.m_bHasNewUI = false;
            else if (invalid == "loading") w.Scenario.loading = true;
            else if (invalid == "collecting") w.Scenario.collecting = true;
            else if (invalid == "fade") w.Scenario.visible_time = DateTime.Now;
            else if (invalid == "bad_flags") w.Scenario.m_bNext = true;
            w.Runtime.EvaluateContext(Model.Objects);
            Check(!w.Runtime.QueueGui(Cmd("continue"), out why) && !w.Scenario.m_bMoving && w.Scenario.SkipCalls == 0, "intro_invalid_state_not_admitted_" + invalid);
        }
        World stopped = IntroWorld(); Check(stopped.Runtime.QueueGui(Cmd("continue"), out why), "intro_stop_before_mutation_queued");
        bool cancelled = false;
        try { stopped.Runtime.ExecuteGui(Cmd("continue"), delegate { throw new OperationCanceledException(); }, out why); }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && !stopped.Scenario.m_bMoving && stopped.Scenario.SkipCalls == 0, "intro_publication_stop_or_failure_prevents_first_mutation");
        stopped.Runtime.GuiNotInvoked(Cmd("continue"), "stop_or_expiry_before_gui_invocation");
        Check(!stopped.Runtime.ExecuteGui(Cmd("continue"), delegate { publications++; }, out why) && !stopped.Scenario.m_bMoving, "intro_cancelled_command_never_replays");
        World differentId = IntroWorld(); differentId.Scenario.m_scenario.id = 999;
        differentId.Runtime.EvaluateContext(Model.Objects);
        Check(differentId.Runtime.QueueGui(Cmd("continue"), out why) && differentId.Runtime.ExecuteGui(Cmd("continue"), delegate { }, out why) && differentId.Scenario.m_bMoving && differentId.Scenario.SkipCalls == 0, "reviewed_animation_behavior_independent_of_scenario_id_or_feature_name");
        World changedBranch = new World(); changedBranch.Scenario.enabled = true;
        changedBranch.Observe(changedBranch.Global, changedBranch.Main, changedBranch.Campaign, changedBranch.Scenario);
        Check(changedBranch.Runtime.QueueGui(Cmd("continue"), out why), "text_skip_branch_queued");
        changedBranch.Scenario.m_scenario.sections.Clear();
        Check(!changedBranch.Runtime.ExecuteGui(Cmd("continue"), delegate { publications++; }, out why) && changedBranch.Scenario.SkipCalls == 0 && !changedBranch.Scenario.m_bMoving, "queued_skip_cannot_silently_become_intro_branch");
    }
    public static int Main()
    {
        try { ProtocolRules(); ObserverRules(); SettlementCaptureRules(); QueueCaptureRules(); ModelRules(); ContextRules(); DiagnosticsAndChapterRules(); SettlementDiagnosticsRules(); RewardRules(); IntroRules(); checks += GeneralTutorialTests.Run(); Console.WriteLine("{\"status\":\"PASS\",\"checks\":" + checks + ",\"scope\":\"production campaign sources with owned fixtures\",\"game_access\":false,\"sample_selection_diagnostics\":" + sampleDiagnostics + ",\"sample_settlement_diagnostics\":" + sampleSettlement + ",\"sample_settlement_packet_diagnostics\":" + sampleSettlementPacket + "}"); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
