using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using ProtoBuf;

// Owned, detached game/Unity boundary fixtures only. The common lifecycle,
// settlement adapter, reflection helpers and transport are production sources.
// Stock behaviors below are grounded in research/battle-skip/{BattleUI,ArenaUI,
// PointraceUI}.cs.txt. They do not establish live Unity/game acceptance.
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
    // No launch guard is initialized in this isolated settlement fixture. The
    // guard's request-to-report transfer contract is validated by its own suite.
    internal static class BattleLaunch
    {
        internal static bool RejectReport;
        internal static NativeBattleStartTicket Ticket;
        internal static NativeBattleStartTicket ReportBound(BattlePresentationOwnership ownership, object role, ulong roleId,
            UnityEngine.Component battle, object report)
        { if (RejectReport) throw new ProbeFailure("fixture launch receipt unavailable"); return Ticket; }
    }
    internal sealed class ProbeFailure : Exception { internal ProbeFailure(string message) : base(message) { } }
    internal sealed class Model
    {
        internal static Type GameType(string name) { return FirstType(name); }
        internal static Type FirstType(string name) { return typeof(Model).Assembly.GetType(name, true); }
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
    public RootMessenger rmg;
    public bool loading, collecting, interaction = true;
    public bool IsVisible() { return enabled; }
    public bool IsEnable() { return interaction; }
    public void Hide() { enabled = false; }
}
public class BaseUI : __BaseUI { public static object rootUI; }
public class MainUI : __BaseUI { public static object m_subui; }
public class RootUI : __BaseUI
{
    public static game.net.tcp_client m_client;
    public static bool m_bRefreshFive;
}
public class Loader
{
    public RootMessenger currmg;
    public bool isBattling = true;
}
public static class BaseAsset { public static Loader lds; }
public static class Global
{
    public static Database.Character character_;
    public static game.net.tcp_client m_client;
    public static bool extern_enabled = true, relogin;
    public static void ExternalCall(string name, object[] arguments) { BattleFixtureTrace.Add("external:" + name); }
}
namespace Database
{
    public sealed class Character { public ulong id = 78; public bool gm; public uint next_resource_time = 104400; }
}
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
        public FixtureSocket m_client = new FixtureSocket();
        public Stream m_stream = new MemoryStream();
        public bool m_bConnectLost;
        public int EndRequests;
        public MemoryStream DispatchedStream;
        internal Handler SendEnd(JiangShen.AssistantRuntime.DevelopmentNormalCallback callback)
        {
            EndRequests++;
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
                DispatchedStream = stream;
                QueueMessage message = new QueueMessage { op = opcode, stream = stream };
                messages.Add(message);
                messages.Remove(message);
                m_handlers.Remove(opcode);
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
    [ProtoMember(18)] public uint _star_level;
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
    public object m_report = new object();
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
    public CampaignTroop m_troopWin;
    public void BattleEnd(EndPacket packet)
    {
        Applied++; LastWin = packet._iswin;
        // Source-grounded victim effects: CampaignUI.BattleEnd indexes by the
        // response's idtroop, then writes star/finish/killed and m_troopWin.
        CampaignTroop target = m_troops[(byte)packet._idtroop] as CampaignTroop;
        if (packet._iswin)
        {
            target.star_level = packet._star_level;
            if (!target.killed)
            {
                if (target.finish_count <= target.finish_max) target.finish_count++;
                if (target.finish_count >= target.finish_max) target.killed = true;
            }
            m_troopWin = target;
        }
        BattleFixtureTrace.Add("campaign_applied");
    }
    public void BattleClose() { Returns++; enabled = true; BattleFixtureTrace.Add("campaign_returned"); }
    public void BonusOpen() { Returns++; enabled = true; BattleFixtureTrace.Add("campaign_returned"); }
}
public sealed class CampaignData { public int idcampaign_type; }
public sealed class CampaignTroop
{
    public uint idcampaign = 12, idtroop = 7, star_level;
    public int finish_count, finish_max = 1;
    public bool killed;
}
public sealed class OrdinaryTroop { public int idtroop = 7; }
public class TrialLevelUI : __BaseUI
{
    public object m_troop;
    public int Applied, Returns;
    public bool LastWin;
    public void BattleEnd(EndPacket packet) { Applied++; LastWin = packet._iswin; BattleFixtureTrace.Add("trial_applied"); }
    public void BonusOpen() { Returns++; enabled = true; BattleFixtureTrace.Add("trial_returned"); }
}
public class TrialUI : __BaseUI { }
public class GraveLevelUI : __BaseUI
{
    public object m_troop;
    public int Applied, Returns;
    public bool LastWin;
    public void BattleEnd(EndPacket packet) { Applied++; LastWin = packet._iswin; BattleFixtureTrace.Add("grave_applied"); }
    public void BonusOpen() { Returns++; enabled = true; BattleFixtureTrace.Add("grave_returned"); }
}
public class DivinityUI : __BaseUI { }
public class BuildingMonsterScript : __BaseUI { }
public class SealMonsterUI : __BaseUI
{
    public object m_troop;
    public int Applied, Returns;
    public void BattleEnd(EndPacket packet) { Applied++; BattleFixtureTrace.Add("training_applied"); }
    public void BonusOpen() { Returns++; enabled = true; BattleFixtureTrace.Add("training_returned"); }
}
public class DemonScript : __BaseUI { public void BattleClose() { enabled = true; } }
public class ArenaUI : __BaseUI
{
    public int Applied, Returns;
    public void ApplyInitialReceipt() { Applied++; BattleFixtureTrace.Add("arena_initial_applied"); }
    public void BattleClose() { Returns++; enabled = true; BattleFixtureTrace.Add("arena_returned"); }
}
public class PointraceUI : __BaseUI
{
    public int Applied, Returns;
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
    public void NativeFinish()
    {
        bBattle_fighting = false;
        if (bUseBattleEnd && bSendBattleEnd) { cs_battle_end_0x408(); bSendBattleEnd = false; }
    }
    public void NativeResultGui()
    {
        if (!bBattle_fighting && bBattle_started && (!bNeedWaitForBattleEnd || !bUseBattleEnd)) OpenResultWindow();
    }
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
        else if (packet._result == 0 && packet._type == 2 && m_parentUI is TrialLevelUI)
            ((TrialLevelUI)m_parentUI).BattleEnd(packet);
        else if (packet._result == 0 && packet._type == 4 && m_parentUI is GraveLevelUI)
            ((GraveLevelUI)m_parentUI).BattleEnd(packet);
        else if (packet._result == 0 && packet._type == 7 && m_parentUI is SealMonsterUI)
            ((SealMonsterUI)m_parentUI).BattleEnd(packet);
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
    }
    public void battle_exit()
    {
        ExitCalls++; enabled = false; rmg.ToDisable();
        m_prevUI.ToEnable(); BaseAsset.lds.currmg = m_prevUI; BaseAsset.lds.isBattling = false;
        BattleFixtureTrace.Add("battle_exited");
        if (OnClose != null) { Action close = OnClose; OnClose = null; close(); }
    }
}
