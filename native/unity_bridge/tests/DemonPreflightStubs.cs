using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using ProtoBuf;
using game.net.data.StoC;

// Unity and stock presentation boundary substitutes. DailyMonsterModel, its
// reflection helpers, wire reader, refresh gate and transport are real sources.
// The stock callback below follows research/battle-skip/DemonScript.cs.txt:
// 245-299; BattleReport loading follows BattleReportInterpreter2.cs.txt:356-387.
// pbdata, protobuf-net and Ionic.Zlib are the pinned installed client binaries.
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
        public static readonly Hashtable Paths = new Hashtable();
        public readonly List<Component> Components = new List<Component>();
        public readonly Transform transform = new Transform();
        public string name = "detached";
        public bool activeInHierarchy = true;
        public static GameObject Find(string path) { return Paths[path] as GameObject; }
        public Component GetComponent(Type type) { return Components.Find(delegate(Component c) { return c.GetType() == type; }); }
        public Component GetComponent(string type) { return Components.Find(delegate(Component c) { return c.GetType().Name == type; }); }
        public T Attach<T>(T item) where T : Component { item.gameObject = this; Components.Add(item); return item; }
    }
    public sealed class Event { public static Event current = new Event(); }
    public static class GUI { public static bool enabled = true; }
    public static class Time { public static int frameCount; }
    public static class Resources { public static Object[] FindObjectsOfTypeAll(Type type) { return new Object[0]; } }
}

namespace JiangShen.AssistantRuntime
{
    internal sealed class ProbeFailure : Exception { internal ProbeFailure(string text) : base(text) { } }
    internal sealed class Model
    {
        internal UnityEngine.Component Root;
        internal UnityEngine.GameObject Battle;
        internal UnityEngine.Object[] All;
        internal UnityEngine.Component CurrentRoot() { return Root; }
        internal UnityEngine.GameObject BattleUiObject() { return Battle; }
        internal UnityEngine.Object[] AllUi() { return All; }
        internal static Type GameType(string name) { return FirstType(name); }
        internal static Type FirstType(string name)
        {
            // Production Model.FirstType searches only firstpass, never
            // pbdata/protobuf/Ionic. Keep that restriction observable here.
            return typeof(Model).Assembly.GetType(name, true);
        }
    }
#if !REAL_BATTLE_LAUNCH
    internal enum BattleMode { OwnDemon, OwnBuildingMonster, FriendBuildingMonster }
    internal delegate bool BattleLaunchCheck();
    internal delegate void BattleLaunchAction();
#endif
    internal sealed class BattlePresentationOwnership
    {
        internal readonly BattleMode Mode;
        internal readonly string OperationId;
        internal readonly UnityEngine.Component Source;
        internal readonly object Target;
        internal BattlePresentationOwnership(BattleMode mode, string operation, UnityEngine.Component source, object target,
            string identity, BattleLaunchCheck check) { Mode = mode; OperationId = operation; Source = source; Target = target; }
    }
#if !REAL_BATTLE_LAUNCH
    internal sealed class BattleLaunchAttempt
    {
        internal int InitialCalls;
        internal UnityEngine.Component Battle;
        internal object Report;
        internal void InitialResponseApplied(UnityEngine.Component battle, object report)
        {
            if (InitialCalls != 0 || battle == null || report == null) throw new Exception("launch proof duplicated or absent");
            InitialCalls++; Battle = battle; Report = report;
        }
    }
    internal static class BattleLaunch
    {
        internal static BattleLaunchAttempt Last;
        internal static BattleLaunchAttempt Formation(BattleMode mode, UnityEngine.Component formation, UnityEngine.Component source,
            object target, BattleLaunchCheck check, BattleLaunchAction attach)
        {
            if (mode != BattleMode.OwnDemon || !check() || !ReferenceEquals(((DemonScript)source).m_demon, target))
                throw new Exception("fixture launch identity");
            Last = new BattleLaunchAttempt();
            SharedDevelopmentContext.Call(formation, "OnClickSetCurrentForm");
            attach(); return Last;
        }
    }
#endif
    internal static class BattleSettlement
    {
        internal static int Presentations;
        internal static bool PresentSettled(UnityEngine.Component battle, UnityEngine.Component source, BattleMode mode,
            string operation, object target, string identity, object receipt, ushort opcode, int result, BattleLaunchCheck verify)
        {
            if (mode != BattleMode.OwnDemon || opcode != 0x492 || result != 0 || !verify()) throw new Exception("fixture settled proof");
            Presentations++; return true;
        }
    }
}

public class RootMessenger : UnityEngine.Behaviour
{
    public string LevelID = "Capital";
    public bool bLoading;
    public int ExpectingCount, Enables;
    public void ToEnable() { enabled = true; Enables++; }
}
public class __BaseUI : UnityEngine.Behaviour
{
    public static object battle_result_ui;
    public RootMessenger rmg;
    public bool loading, collecting;
    public bool IsVisible() { return enabled; }
    public bool IsEnable() { return enabled; }
    public void Hide() { enabled = false; }
}
public class BaseUI : __BaseUI { public static object rootUI; }
public class MainUI : __BaseUI
{
    public static object m_subui;
    public static void SetSubUI(__BaseUI ui) { m_subui = ui; }
    public static void _capital() { throw new Exception("unexpected navigation"); }
}
public class CapitalUI : __BaseUI { }
public class CapitalFriendUI : __BaseUI { public static object myfriend; }
public class RootUI : __BaseUI
{
    public static game.net.tcp_client m_client;
    public static bool m_bRefreshFive;
}
public class Loader : UnityEngine.Behaviour { public bool isBattling; public RootMessenger currmg; }
public static class BaseAsset { public static Loader lds; }
public static class TimeUtil { public static uint _now = 106400, _thisDay = 86400; }
public static class Global
{
    public static Database.Character character_;
    public static game.net.tcp_client m_client;
    public static bool extern_enabled = true, relogin;
}
namespace Database
{
    public sealed class Character
    {
        public ulong id = 78;
        public bool gm;
        public uint next_resource_time = 150000, gold_consume;
        public int gold = 100, bind_gold = 200, level = 30;
        public Demon m_demon;
        public Hashtable acts = new Hashtable(), cooldowns = new Hashtable();
    }
    public sealed class Demon
    {
        public bool killed;
        public uint clear_time = 150000;
        public UnityEngine.GameObject mesh;
        public int ArmyUpdates;
        public void updateArmys(IList soldiers) { ArmyUpdates++; }
    }
    public sealed class Cooldown
    {
        public uint time, id, sub_id;
        public bool in_cd;
        public Cooldown(db_character_cooldown value)
        { time = value.time; id = value.id; sub_id = value.sub_id; in_cd = value.in_cd; }
    }
}
public sealed class DemonActivity { public int condition_param; public DemonTemplate temp = new DemonTemplate(); }
public sealed class DemonTemplate { public int cond_param = 1; }
namespace game.net
{
    public class tcp_base
    {
        public static int connection_state = 3;
        public sealed class Handler
        {
            internal JiangShen.AssistantRuntime.DevelopmentNormalCallback handler;
            public object arg;
            public float life = 10;
        }
    }
    public sealed class FixtureSocket { public bool Connected { get; set; } public FixtureSocket() { Connected = true; } }
    public sealed class tcp_client : tcp_base
    {
        public Hashtable m_handlers = new Hashtable();
        public readonly ArrayList messages = new ArrayList(), m_sendDatas = new ArrayList();
        public readonly List<ushort> Sent = new List<ushort>();
        public FixtureSocket m_client = new FixtureSocket();
        public Stream m_stream = new MemoryStream();
        public bool m_bConnectLost;
        internal void Send(ushort opcode, JiangShen.AssistantRuntime.DevelopmentNormalCallback callback)
        { Sent.Add(opcode); m_handlers[opcode] = new Handler { handler = callback }; }
        public MemoryStream Dispatch(ushort opcode, byte[] bytes)
        {
            Handler pending = (Handler)m_handlers[opcode];
            if (pending.life > 0) m_handlers.Remove(opcode);
            MemoryStream stream = bytes == null ? null : new MemoryStream(bytes, false);
            pending.handler(stream, pending.arg); return stream;
        }
    }
}
public static class JSProtoBuf
{
    public sealed class PBSerializerWrapper
    {
        public T Deserialize<T>(Stream input) { return Serializer.Deserialize<T>(input); }
        public void Serialize<T>(Stream output, T instance) { Serializer.Serialize(output, instance); }
    }
    public static PBSerializerWrapper wrapper = new PBSerializerWrapper();
}
public sealed class FormationUI : __BaseUI
{
    public string substate = "demon";
    public bool isVisible = true, isLoading, onfight;
    public int waitstate;
#if REAL_BATTLE_LAUNCH
    public delegate bool Attack();
    public Attack OnAttack;
    public object CharFormation = new object();
    public Hashtable m_lastparam;
    public void OnClickSetCurrentForm() { RootUI.m_client.Send(298, sc_formupdate); }
    private void sc_formupdate(MemoryStream stream, object arg) { if (stream != null) { OnAttack(); enabled = false; } }
#else
    public Action OnAttack;
    public void OnClickSetCurrentForm() { OnAttack(); enabled = false; }
#endif
}
public sealed class BattleInterpreter
{
    public BattleReport m_report;
    public void setReport(byte[] compressed)
    {
        m_report = null;
        try
        {
            using (MemoryStream bytes = new MemoryStream(compressed, false))
            using (Ionic.Zlib.DeflateStream stream = new Ionic.Zlib.DeflateStream(bytes, Ionic.Zlib.CompressionMode.Decompress))
                m_report = JSProtoBuf.wrapper.Deserialize<BattleReport>(stream);
        }
        catch (Exception) { } // Real interpreter swallows parse/decompression errors.
    }
}
public enum DemonViewType { viewReport }
public sealed class BattleUI : __BaseUI
{
    public RootMessenger m_prevUI;
    public object m_parentUI;
    public ulong m_reportid;
    public bool bUseBattleEnd, bNeedWaitForBattleEnd, m_is_cross, bBattle_fighting;
    public DemonViewType m_viewType = DemonViewType.viewReport;
    public Delegate OnClose;
    public BattleInterpreter interpreter = new BattleInterpreter();
    public int ExitCalls;
    public void battle_exit()
    {
        ExitCalls++; enabled = false; BaseAsset.lds.isBattling = false;
        DemonFixtureWorld.Model.Root = m_prevUI;
        if (OnClose != null) OnClose.DynamicInvoke(null); else m_prevUI.ToEnable();
    }
}
public sealed class BattleNormalResultUI : __BaseUI { }
public sealed class BattleResultUI : __BaseUI
{
    public int Configured, RewardItems;
    public Action OnClose;
    public RootMessenger m_prevUI;
}
public static class DemonFixtureWorld
{
    internal static JiangShen.AssistantRuntime.Model Model;
    public static FormationUI Formation;
    public static BattleUI Battle;
    public static BattleResultUI Reward;
}
public sealed class DemonScript : __BaseUI
{
    public Database.Demon m_demon;
    public bool m_bFriend;
    public int CallbackCalls, Rejections, NullCalls, Returns;
    public MemoryStream CallbackStream;
    public object CallbackArg;
    public long CallbackPosition = -1;
    public Action DuringCallback;
    public void DemonAttack(bool accepted, object[] args)
    {
        DemonFixtureWorld.Formation.OnAttack = cs_demon_battle_0x492;
#if REAL_BATTLE_LAUNCH
        DemonFixtureWorld.Formation.m_lastparam = new Hashtable { { "OnAttack", DemonFixtureWorld.Formation.OnAttack } };
#endif
        DemonFixtureWorld.Formation.enabled = true;
        MainUI.m_subui = DemonFixtureWorld.Formation;
    }
#if REAL_BATTLE_LAUNCH
    public bool cs_demon_battle_0x492() { RootUI.m_client.Send(0x492, sc_demon_battle_0x492); return true; }
#else
    public void cs_demon_battle_0x492() { RootUI.m_client.Send(0x492, sc_demon_battle_0x492); }
#endif
    private void sc_demon_battle_0x492(MemoryStream stream, object arg)
    {
        CallbackCalls++; CallbackStream = stream; CallbackArg = arg;
        if (stream == null) { NullCalls++; BaseAsset.lds.enabled = false; return; }
        CallbackPosition = stream.Position;
        SCDemonBattle_0x492 packet = JSProtoBuf.wrapper.Deserialize<SCDemonBattle_0x492>(stream);
        Database.Demon demon = Global.character_.m_demon;
        if (packet.result == 1) { Global.character_.m_demon = null; demon.mesh.activeInHierarchy = false; }
        if (packet.result == 2) { Rejections++; BaseAsset.lds.enabled = false; return; }
        BattleUI battle = DemonFixtureWorld.Battle;
        battle.m_prevUI = rmg; battle.interpreter.setReport(packet.report);
        battle.m_viewType = DemonViewType.viewReport;
        Global.character_.cooldowns[(byte)5] = new Database.Cooldown(packet.cooldown);
        if (packet.iswin)
        {
            // Real stock converts each treasure, marks killed, configures the
            // reward panel, then installs BonusOpen. No report-outcome guard.
            int count = packet.bonuses.bonuses.Count;
            demon.killed = true;
            BattleResultUI reward = DemonFixtureWorld.Reward;
            reward.Configured++; reward.RewardItems = count;
            reward.m_prevUI = rmg; reward.OnClose = BonusClose;
            __BaseUI.battle_result_ui = reward;
            battle.OnClose = (Action)BonusOpen;
        }
        else demon.updateArmys((IList)packet.soldiers);
        battle.enabled = true; BaseAsset.lds.isBattling = true;
        MainUI.SetSubUI(null);
        if (DuringCallback != null) DuringCallback();
    }
    private void BonusOpen() { DemonFixtureWorld.Reward.enabled = true; }
    private void BonusClose()
    {
        Returns++; rmg.ToEnable();
        Database.Demon demon = Global.character_.m_demon;
        if (demon != null) { demon.mesh.activeInHierarchy = false; Global.character_.m_demon = null; }
    }
}
