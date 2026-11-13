using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using ProtoBuf;

// Detached game/Unity boundary fixtures only. Both mode adapters, the common
// lifecycle, reflection helpers and transport are compiled production sources.
// Stock behaviors below are grounded in research/battle-skip/{BattleUI,ArenaUI,
// GraveLevelUI,game.net.tcp_client}.cs.txt. They do not establish live acceptance.
namespace UnityEngine
{
    public class Object
    {
        private static int next;
        private readonly int id = ++next;
        public int GetInstanceID() { return id; }
    }
    public sealed class Transform
    {
        public Transform parent;
        public Transform root { get { return parent == null ? this : parent.root; } }
    }
    public class Component : Object
    {
        public GameObject gameObject = new GameObject();
        public Transform transform { get { return gameObject.transform; } }
        public Component GetComponent(Type type) { return gameObject.GetComponent(type); }
        public Component GetComponentInChildren(Type type) { return gameObject.GetComponent(type); }
    }
    public class Behaviour : Component { public bool enabled = true; }
    public sealed class GameObject : Object
    {
        public static readonly Dictionary<string, GameObject> Paths = new Dictionary<string, GameObject>();
        public readonly List<Component> Components = new List<Component>();
        public readonly Transform transform = new Transform();
        public string name = "detached";
        public bool activeInHierarchy = true;
        public static GameObject Find(string path) { GameObject value; return Paths.TryGetValue(path, out value) ? value : null; }
        public Component GetComponent(Type type) { return Components.Find(delegate(Component item) { return item.GetType() == type; }); }
        public Component GetComponent(string type) { return Components.Find(delegate(Component item) { return item.GetType().Name == type; }); }
        public Component[] GetComponents(Type type) { return Components.FindAll(delegate(Component item) { return type.IsInstanceOfType(item); }).ToArray(); }
        public T Attach<T>(T component) where T : Component { component.gameObject = this; Components.Add(component); return component; }
    }
    public sealed class Event { public static Event current = new Event(); }
    public static class GUI { public static bool enabled = true; }
    public static class Time { public static int frameCount; }
    public static class Resources { public static Object[] FindObjectsOfTypeAll(Type type) { return new Object[0]; } }
}

namespace JiangShen.AssistantRuntime
{
    internal delegate SharedDevelopmentContext DailyBattleContextProvider();
    internal delegate bool DailyBattleAdmissionCheck();
    internal delegate bool DailyBattleAdmission(DailyBattleAdmissionCheck prepare, DailyBattleAdmissionCheck check);
    internal sealed class ProbeFailure : Exception { internal ProbeFailure(string message) : base(message) { } }
    internal sealed class Model
    {
        internal static Type GameType(string name) { return FirstType(name); }
        internal static Type FirstType(string name)
        {
            if (name.StartsWith("game.net.data.")) return typeof(game.net.data.StoC.SCArenaChallenge_0x460).Assembly.GetType(name, true);
            if (name.StartsWith("ProtoBuf.")) return typeof(Serializer).Assembly.GetType(name, true);
            if (name.StartsWith("Ionic.Zlib.")) return typeof(Ionic.Zlib.DeflateStream).Assembly.GetType(name, true);
            return typeof(Model).Assembly.GetType(name, true);
        }
    }
}

public static class BattleFixtureTrace
{
    public static readonly List<string> Events = new List<string>();
    public static void Add(string item) { Events.Add(item); }
}

public class RootMessenger : UnityEngine.Behaviour
{
    public string LevelID = "Campaign";
    public int Enables, Disables;
    public void ToEnable() { Enables++; enabled = true; BattleFixtureTrace.Add("previous_root_enabled"); }
    public void ToDisable() { Disables++; enabled = false; }
}
public class __BaseUI : UnityEngine.Behaviour
{
    public delegate void delegate_OnClose();
    public static object battle_result_ui;
    public RootMessenger rmg;
    public bool loading, collecting, interaction = true;
    public bool IsVisible() { return enabled; }
    public bool IsEnable() { return interaction; }
    public void Hide() { enabled = false; }
}
public class BaseUI : __BaseUI { public static object rootUI; }
public class MainUI : __BaseUI
{
    public static object m_subui;
    public static void SetSubUI(__BaseUI ui) { m_subui = ui; }
    public static int CalcServerOpenRefreshDays() { return 1; }
    public static void _battle_arena()
    {
        ArenaSelectUI select = (ArenaSelectUI)((RootUI)BaseUI.rootUI).gameObject.GetComponent(typeof(ArenaSelectUI));
        select.enabled = true; m_subui = select;
    }
}
public class RootUI : __BaseUI
{
    public static game.net.tcp_client m_client;
    public static bool m_bRefreshFive;
    public RootMessenger level_rmg;
    public void cs_pointrace_enter(RootMessenger previous, delegate_OnClose onclose)
    {
        BaseAsset.lds.oldmainrmg = previous;
        PointraceUI panel = BattleModeFixtureObjects.PointRace;
        level_rmg = panel.rmg; BaseAsset.lds.currmg = panel.rmg;
        ArenaSelectUI select = (ArenaSelectUI)gameObject.GetComponent(typeof(ArenaSelectUI));
        if (select != null) select.enabled = false;
        MainUI.m_subui = null; panel.enabled = true;
        panel.PrepareLoading();
    }
}
public class Loader : UnityEngine.Behaviour
{
    public RootMessenger currmg;
    public RootMessenger oldmainrmg;
    public bool isBattling = true;
    public bool isVisible() { return enabled; }
    public RootMessenger GetOldMainUI() { return oldmainrmg; }
}
public static class BaseAsset { public static Loader lds; }
public static class Global
{
    public static Database.Character character_;
    public static game.net.tcp_client m_client;
    public static bool extern_enabled = true, relogin;
    public static int ActivityCalls;
    public static readonly Hashtable Templates = new Hashtable();
    public static object getTemplate(string name, uint id) { return Templates[name + ":" + id]; }
    public static void handelActvity(MemoryStream stream, object arg) { ActivityCalls++; }
    public static void ExternalCall(string name, object[] arguments) { BattleFixtureTrace.Add("external:" + name); }
}
namespace Database
{
    public sealed class Character
    {
        public ulong id = 78;
        public bool gm;
        public uint next_resource_time = 104400, gold_consume;
        public int gold = 100, bind_gold = 200, stamina = 20, level = 10;
        public ArenaData arena = new ArenaData();
        public PointRaceData pointrace = new PointRaceData();
        public GraveData grave = new GraveData();
        public GraveCampaign grave_campaign = new GraveCampaign();
        public Hashtable acts = new Hashtable(), act_rewards = new Hashtable(), graves = new Hashtable();
        public Cooldown getCooldown(byte type) { return null; }
        public uint get_battle_score() { return 300; }
    }
    public sealed class Cooldown { public uint time; }
}
public static class TimeUtil { public static uint _now = 100000, _thisDay = 86400; }
public sealed class ArenaData
{
    public int challenge_count, challenge_buy, money, reputation;
    public ArrayList challengers = new ArrayList();
}
public sealed class ArenaChallenger
{
    public ulong Id = 271;
    public uint Rank = 900;
    public ulong get_id() { return Id; }
    public uint get_rank() { return Rank; }
}
public sealed class PointRaceData
{
    public int challenge_count, challenge_buy, refresh_count = 1;
    public ArrayList challengers = new ArrayList();
}
public sealed class PointRaceChallenger
{
    public ulong id { get; set; }
    public uint pos { get; set; }
    public uint battle_score { get; set; }
    public bool killed { get; set; }
    public PointRaceChallenger() { id = 271; pos = 1; battle_score = 100; }
}
public sealed class ActivityTemplate
{ public int id = 9, idreward = 3, cond_param = 5; public bool active = true; }
public sealed class ActivityRow
{
    public object temp;
    public int condition_param = 5, refresh_time = 18000;
    public bool _check_act_closecondition() { return false; }
}
public sealed class ActivityCategory { public Hashtable acts = new Hashtable(); }
public sealed class GraveData { public int times = 1, finishtoday; }
public sealed class GraveCampaign
{
    public int idgrave = 31, soldier = 20, finish;
    public bool showboss;
    public GraveTroop[] troops = { new GraveTroop() };
}
public sealed class GraveTroop { public int idtroop = 1; public bool killed; }
public sealed class OpenGrave { public bool isopen = true, isfinish; public uint idgrave = 31; }
public sealed class GraveTemplate { public int min_level = 1; }
namespace game.net
{
    public class tcp_base
    {
        public static int connection_state = 3;
        public sealed class Handler
        {
            internal JiangShen.AssistantRuntime.DevelopmentNormalCallback handler;
            public object arg;
            public float life = 12f;
        }
    }
    public sealed class FixtureSocket { public bool Connected { get; set; } public FixtureSocket() { Connected = true; } }
    public sealed class QueueMessage { public ushort op; public MemoryStream stream; }
    public sealed class tcp_client : tcp_base
    {
        public readonly Hashtable m_handlers = new Hashtable();
        public readonly ArrayList messages = new ArrayList();
        public readonly ArrayList m_sendDatas = new ArrayList();
        public bool m_bSending;
        public FixtureSocket m_client = new FixtureSocket();
        public Stream m_stream = new MemoryStream();
        public bool m_bConnectLost;
        public int EndRequests;
        public readonly List<ushort> Sent = new List<ushort>();
        internal Handler Send(ushort opcode, JiangShen.AssistantRuntime.DevelopmentNormalCallback callback)
        {
            Sent.Add(opcode);
            Handler slot = new Handler { handler = callback };
            m_handlers.Add(opcode, slot);
            return slot;
        }
        internal Handler SendEnd(JiangShen.AssistantRuntime.DevelopmentNormalCallback callback)
        {
            EndRequests++;
            Sent.Add(0x408);
            Handler slot = new Handler { handler = callback };
            m_handlers.Add((ushort)0x408, slot);
            BattleFixtureTrace.Add("send_0x408");
            return slot;
        }
        // The stock tcp client removes a one-shot handler before invoking it.
        // Keeping this in the queue fixture catches incorrect wrapper ordering.
        public Handler Dispatch(ushort opcode, byte[] body)
        {
            lock (this)
            {
                Handler slot = m_handlers[opcode] as Handler;
                if (slot == null) throw new InvalidOperationException("fixture handler missing");
                MemoryStream stream = body == null ? null : new MemoryStream(body, false);
                QueueMessage message = new QueueMessage { op = opcode, stream = stream };
                messages.Add(message);
                messages.Remove(message);
                if (slot.life > 0) m_handlers.Remove(opcode);
                BattleFixtureTrace.Add("dequeued_" + opcode);
                try { slot.handler(stream, slot.arg); }
                finally { if (stream != null) stream.Close(); }
                return slot;
            }
        }
    }
}

[ProtoContract]
public sealed class EndPacket
{
    [ProtoMember(1, IsRequired = true)] public uint _result;
    [ProtoMember(2)] public uint _type;
    [ProtoMember(4)] public uint _idcampaign;
    [ProtoMember(5)] public uint _idtroop;
    [ProtoMember(6)] public bool _iswin;
    [ProtoMember(17)] public ulong _idbattle_report;
}
public sealed class PendingBattleEnd { public EndPacket pkt; }
public static class JSProtoBuf
{
    public sealed class SerializerWrapper { public T Deserialize<T>(Stream stream) { return Serializer.Deserialize<T>(stream); } }
    public static SerializerWrapper wrapper = new SerializerWrapper();
}
public enum enumBattleViewType { viewDefault, viewReport, viewFull, viewVipSkip }
public sealed class BattleInterpreter
{
    public object m_report = new game.net.data.StoC.BattleReport
        { result_status = game.net.data.StoC.BattleReport.ResultStatus.LEFT_WIN };
    public int FinalStateCalls;
    public bool ThrowFinal;
    public void SetArmyToFinalState()
    {
        FinalStateCalls++; BattleFixtureTrace.Add("final_armies");
        if (ThrowFinal) throw new InvalidOperationException("fixture final state failed");
    }
}
public class CampaignUI : __BaseUI
{
    public CampaignData m_campaign = new CampaignData();
    public Hashtable m_troops = new Hashtable();
    public int Applied, Returns;
    public bool LastWin;
    public void BattleEnd(EndPacket packet) { Applied++; LastWin = packet._iswin; BattleFixtureTrace.Add("campaign_applied"); }
    public void BattleClose() { Returns++; enabled = true; BattleFixtureTrace.Add("campaign_returned"); }
    public void BonusOpen() { Returns++; enabled = true; BattleFixtureTrace.Add("campaign_returned"); }
}
public sealed class CampaignData { public int idcampaign_type; }
public sealed class CampaignTroop { public uint idcampaign = 12, idtroop = 7; }
public class TrialLevelUI : __BaseUI { }
public class TrialUI : __BaseUI { }
public class GraveLevelUI : __BaseUI
{
    public int _graveid = 31, Applied, Returns, InitialCalls;
    public GraveTroop m_troop;
    public FormationUI Formation;
    public void wantCombat(GraveTroop troop)
    { m_troop = troop; Formation.enabled = true; MainUI.m_subui = Formation; }
    public void StartBattle() { RootUI.m_client.Send(0x342, sc_grave_battle_0x342); }
    // Contract: GraveLevelUI.cs.txt:389-418; initial 0x342 only installs report.
    private void sc_grave_battle_0x342(MemoryStream stream, object arg)
    {
        InitialCalls++;
        BattleUI battle = BattleModeFixtureObjects.Battle;
        battle.m_prevUI = rmg; battle.m_parentUI = this; battle.OnClose = null;
        battle.bUseBattleEnd = true; battle.bNeedWaitForBattleEnd = false;
        battle.m_reportid = 0; battle.enabled = true;
        BaseAsset.lds.isBattling = true; Formation.enabled = false; MainUI.m_subui = null;
    }
    // Contract: type 4 dispatch applies once, then installs the source BonusClose.
    public void BattleEnd(EndPacket packet)
    {
        Applied++;
        Global.character_.grave_campaign.soldier--;
        if (packet._iswin) { m_troop.killed = true; Global.character_.grave_campaign.finish++; }
        BattleModeFixtureObjects.Battle.OnClose = BonusClose;
    }
    private void BonusClose() { Returns++; enabled = true; BattleFixtureTrace.Add("grave_returned"); }
}
public class FormationUI : __BaseUI
{
    public object CharFormation = new object();
    public int waitstate;
    public bool onfight;
    public GraveLevelUI Source;
    public void OnClickSetCurrentForm() { Source.StartBattle(); }
}
public class GraveChooseUI : __BaseUI
{ public bool checkGraveFinish() { return false; } public bool checkGraveBoss() { return false; } }
public class MessageBox : __BaseUI { }
public class ConsumeUI : __BaseUI { }
public class BattleResultUI : __BaseUI { public Action OnClose; }
public class ArenaSelectUI : __BaseUI { }
public class BattleValueCompareUI : __BaseUI { public Action OnClose; }
public class BattleNormalResultUI : __BaseUI { public BattleUI battle_ui; }
public static class BattleModeFixtureObjects
{ public static BattleUI Battle; public static BattleNormalResultUI Result; public static PointraceUI PointRace; }
public class DivinityUI : __BaseUI { }
public class BuildingMonsterScript : __BaseUI { }
public class SealMonsterUI : __BaseUI { }
public class DemonScript : __BaseUI { public void BattleClose() { enabled = true; } }
public class ArenaUI : __BaseUI
{
    public int Applied, Returns, CallbackCalls, EnterCalls;
    public int m_challengeIdx;
    public long CallbackPosition = -1;
    public MemoryStream CallbackStream;
    public object CallbackArg;
    public ulong LastReportOpponent;
    public bool ThrowCallback;
    public Action DuringCallback;
    public void Hideme() { enabled = false; }
    public void cs_arena_enter_0x463() { RootUI.m_client.Send(0x463, sc_arena_enter_0x463); }
    private void sc_arena_enter_0x463(MemoryStream stream, object arg) { EnterCalls++; }
    public void cs_arena_challenge_0x460() { RootUI.m_client.Send(0x460, sc_arena_challenge_0x460); }
    // Contract: ArenaUI.cs.txt:894-974. This fake deliberately remains a game
    // boundary, while the receipt decoder and ReplyTap under test are production.
    private void sc_arena_challenge_0x460(MemoryStream stream, object arg)
    {
        CallbackPosition = stream == null ? -1 : stream.Position;
        CallbackStream = stream; CallbackArg = arg;
        CallbackCalls++;
        if (ThrowCallback) throw new InvalidOperationException("fixture arena callback failed");
        if (stream == null) return;
        ArenaPacket packet = Serializer.Deserialize<ArenaPacket>(stream);
        if (packet.Result != 0) return;
        Applied++;
        Global.character_.arena.challenge_count++;
        LastReportOpponent = packet.Envelope == null ? 0 : packet.Envelope.Report.Opponent;
        if (packet.Gold != 0) { Global.character_.arena.challenge_buy++; Global.character_.gold -= checked((int)packet.Gold); }
        if (packet.Bonuses != null) foreach (FixtureTreasure reward in packet.Bonuses.Items)
        {
            if (reward.Type == 4) Global.character_.arena.money += reward.Amount;
            if (reward.Type == 18) Global.character_.arena.reputation += reward.Amount;
        }
        BattleUI battle = BattleModeFixtureObjects.Battle;
        battle.m_prevUI = BaseAsset.lds.currmg; battle.m_parentUI = null;
        battle.m_reportid = packet.ReportId; battle.OnClose = BattleClose;
        battle.interpreter.m_report = StockReport(packet.Report);
        battle.bUseBattleEnd = battle.bNeedWaitForBattleEnd = false;
        battle.enabled = true; BaseAsset.lds.isBattling = true; Hideme();
        if (DuringCallback != null) DuringCallback();
    }
    public void ApplyInitialReceipt() { Applied++; BattleFixtureTrace.Add("arena_initial_applied"); }
    internal static object StockReport(byte[] compressed)
    {
        // The real interpreter catches decompression/parse errors after account
        // mutations. Use its native decode path, independent of the preflight.
        try
        {
            using (MemoryStream body = new MemoryStream(compressed, false))
            using (Ionic.Zlib.DeflateStream zip = new Ionic.Zlib.DeflateStream(body, Ionic.Zlib.CompressionMode.Decompress))
                return Serializer.Deserialize<game.net.data.StoC.BattleReport>(zip);
        }
        catch { return null; }
    }
    public void BattleClose() { Returns++; enabled = true; BattleFixtureTrace.Add("arena_returned"); }
}
public class PointraceUI : __BaseUI
{
    public delegate_OnClose OnClose;
    public int NavigationReturns;
    public bool LeaveReturnIncomplete;
    public PointRaceData m_pointrace;
    public int m_challengeIdx, Applied, Returns, EntryCalls, CallbackCalls;
    public bool ThrowCallback;
    public Action DuringCallback;
    public MemoryStream CallbackStream;
    public object CallbackArg;
    public long CallbackPosition = -1;
    private void WantChallenge() { enabled = false; RootUI.m_client.Send(0x465, sc_pointrace_challenge_0x465); }
    private void sc_pointrace_challenge_0x465(MemoryStream stream, object arg)
    {
        CallbackCalls++;
        CallbackStream = stream; CallbackArg = arg; CallbackPosition = stream == null ? -1 : stream.Position;
        if (ThrowCallback) throw new InvalidOperationException("fixture point race callback failed");
        if (stream == null) { enabled = true; return; }
        PointRacePacket packet = Serializer.Deserialize<PointRacePacket>(stream);
        if (packet.Result != 0) return;
        Applied++; m_pointrace.challenge_count++;
        // Real PointraceUI.cs.txt:646-649 selects the response idother, not m_challengeIdx.
        if (packet.Win) foreach (PointRaceChallenger target in m_pointrace.challengers)
            if (target.id == packet.Opponent) target.killed = true;
        if (packet.Gold != 0) { m_pointrace.challenge_buy++; Global.character_.gold -= checked((int)packet.Gold); }
        BattleUI battle = BattleModeFixtureObjects.Battle;
        battle.m_prevUI = BaseAsset.lds.currmg; battle.m_parentUI = null;
        battle.m_reportid = packet.Envelope.Report.ReportId; battle.OnClose = BattleClose;
        battle.interpreter.m_report = ArenaUI.StockReport(packet.Report);
        battle.bUseBattleEnd = battle.bNeedWaitForBattleEnd = false;
        battle.enabled = true; BaseAsset.lds.isBattling = true; enabled = false;
        if (DuringCallback != null) DuringCallback();
    }
    public void PrepareLoading()
    {
        // Stock PrepareLoading replaces a permanent receiver, then sends with
        // a null callback; this must also work after an assistant detached.
        RootUI.m_client.m_handlers[(ushort)0x464] = new game.net.tcp_base.Handler {
            handler = sc_pointrace_enter_0x464, life = -1f };
        RootUI.m_client.Sent.Add(0x464);
    }
    private void _return()
    {
        NavigationReturns++;
        if (!LeaveReturnIncomplete)
        {
            BaseAsset.lds.currmg = BaseAsset.lds.GetOldMainUI();
            ((RootUI)BaseUI.rootUI).level_rmg = BaseAsset.lds.currmg;
            enabled = false; MainUI.m_subui = null;
        }
        if (OnClose != null) OnClose();
    }
    private void sc_pointrace_enter_0x464(MemoryStream stream, object arg) { EntryCalls++; }
    public void ApplyInitialReceipt() { Applied++; BattleFixtureTrace.Add("pointrace_initial_applied"); }
    public void BattleClose() { Returns++; enabled = true; BattleFixtureTrace.Add("pointrace_returned"); }
}
public class BattleUI : __BaseUI
{
    public __BaseUI m_parentUI;
    public RootMessenger m_prevUI;
    public Action OnClose;
    public bool bBattle_started = true, bBattle_fighting = true, bBattle_surrended, m_is_cross;
    public bool bUseBattleEnd = true, bNeedWaitForBattleEnd = true, bSendBattleEnd = true, ArmiesReady = true;
    public bool ThrowStockCallback, ThrowOpen, SkipStockApply;
    public enumBattleViewType m_viewType = enumBattleViewType.viewVipSkip;
    public ulong m_reportid;
    public ushort idcampaign = 12;
    public byte idtroop = 7;
    public PendingBattleEnd m_pktBattleEnd;
    public BattleInterpreter interpreter = new BattleInterpreter();
    public int CallbackCalls, BattleEndCalls, OpenCalls, ResultCalls, ExitCalls;
    public long CallbackPosition = -1;
    public MemoryStream CallbackStream;
    public object CallbackArg;
    public Action DuringStockCallback;
    private bool resultOpened;
    public bool get_ArmyBehaviourCreated() { return ArmiesReady; }
    private void cs_battle_end_0x408() { RootUI.m_client.SendEnd(sc_battle_end_0x408); }
    private void sc_battle_end_0x408(Stream stream, object arg)
    {
        CallbackCalls++; CallbackStream = stream as MemoryStream; CallbackArg = arg;
        CallbackPosition = stream == null ? -1 : stream.Position;
        BattleFixtureTrace.Add("stock_0x408_enter");
        if (DuringStockCallback != null) DuringStockCallback();
        if (ThrowStockCallback) throw new InvalidOperationException("fixture stock callback failed");
        if (stream != null && !SkipStockApply) BattleEnd(Serializer.Deserialize<EndPacket>(stream));
        BattleFixtureTrace.Add("stock_0x408_returned");
    }
    private void BattleEnd(EndPacket packet)
    {
        BattleEndCalls++;
        if (packet._result == 0 && packet._type == 0 && m_parentUI is CampaignUI)
            ((CampaignUI)m_parentUI).BattleEnd(packet);
        if (packet._result == 0 && packet._type == 4 && m_parentUI is GraveLevelUI)
            ((GraveLevelUI)m_parentUI).BattleEnd(packet);
        m_reportid = packet._idbattle_report;
        bUseBattleEnd = false; bSendBattleEnd = true; bNeedWaitForBattleEnd = false;
        m_pktBattleEnd = null;
        BattleFixtureTrace.Add("stock_end_applied");
    }
    private void OpenResultWindow()
    {
        OpenCalls++; BattleFixtureTrace.Add("open_result");
        if (ThrowOpen) throw new InvalidOperationException("fixture result opening failed");
        if (!resultOpened) { resultOpened = true; ResultCalls++; }
        BattleModeFixtureObjects.Result.enabled = true;
    }
    public void battle_exit()
    {
        ExitCalls++; enabled = false; rmg.ToDisable();
        m_prevUI.ToEnable(); BaseAsset.lds.currmg = m_prevUI; BaseAsset.lds.isBattling = false;
        BattleFixtureTrace.Add("battle_exited");
        if (OnClose != null) { OnClose(); OnClose = null; }
        if (m_prevUI.LevelID == "PointRace" && BattleModeFixtureObjects.PointRace != null)
            BattleModeFixtureObjects.PointRace.PrepareLoading();
    }
}

[ProtoContract]
public sealed class ArenaPacket
{
    [ProtoMember(1, IsRequired = true)] public uint Result;
    [ProtoMember(2)] public bool Win;
    [ProtoMember(4)] public byte[] Report;
    [ProtoMember(5)] public PointRaceEnvelope Envelope;
    [ProtoMember(8)] public uint Gold;
    [ProtoMember(9)] public FixtureTreasures Bonuses;
    [ProtoMember(10)] public ulong ReportId;
}
[ProtoContract]
public sealed class FixtureTreasures { [ProtoMember(1)] public List<FixtureTreasure> Items = new List<FixtureTreasure>(); }
[ProtoContract]
public sealed class FixtureTreasure
{
    [ProtoMember(1)] public uint Type;
    [ProtoMember(3, DataFormat = DataFormat.ZigZag)] public int Amount;
}

[ProtoContract]
public sealed class PointRacePacket
{
    [ProtoMember(1, IsRequired = true)] public uint Result;
    [ProtoMember(2)] public bool Win;
    [ProtoMember(4)] public byte[] Report;
    [ProtoMember(5)] public PointRaceEnvelope Envelope;
    [ProtoMember(8)] public uint Gold;
    [ProtoMember(10)] public ulong Opponent;
}
[ProtoContract]
public sealed class PointRaceEnvelope { [ProtoMember(1)] public PointRaceReport Report; }
[ProtoContract]
public sealed class PointRaceReport
{
    [ProtoMember(1)] public ulong Opponent;
    [ProtoMember(2)] public ulong ReportId;
}
