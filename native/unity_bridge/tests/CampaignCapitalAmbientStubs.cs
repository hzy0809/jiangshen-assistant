using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Threading;
using ProtoBuf;
using JiangShen.AssistantRuntime;

// Detached game and Unity boundary for the Campaign capital ambient test.
// CampaignModel, its identity helper, and its other managed dependencies are
// compiled from production sources. No game process or game assembly is used.
namespace UnityEngine
{
    public class Object { private static int next; private readonly int id = ++next; public int GetInstanceID() { return id; } }
    public class Transform { public Transform root; public Transform() { root = this; } }
    public class GameObject : Object
    {
        public bool activeInHierarchy = true; public Transform transform = new Transform();
        public System.Collections.Generic.List<Component> components = new System.Collections.Generic.List<Component>();
        public Component[] GetComponents(Type type) { return components.FindAll(delegate(Component value) { return type.IsAssignableFrom(value.GetType()); }).ToArray(); }
        public Component GetComponent(string name) { return components.Find(delegate(Component value) { return value.GetType().Name == name; }); }
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
    // The current fixture never observes a battle-end request. The campaign
    // observer only asks this unrelated question while copying battle queues.
    internal static class BattleSettlement
    { internal static bool OwnsPending(object client, UnityEngine.Component battle, object report) { return false; } }
    // This fixture never executes a start. Fail loudly if its scope changes;
    // named-event launch coordination is tested in a separate fixture.
    internal enum BattleMode { Campaign, NormalChallenge }
    internal delegate bool BattleLaunchCheck();
    internal static class BattleLaunch
    {
        internal static void Formation(BattleMode mode, UnityEngine.Component formation, UnityEngine.Component source,
            object target, BattleLaunchCheck verify, Action afterAttack)
        { throw new InvalidOperationException("launch is outside this admission fixture"); }
        internal static bool OwnsScenario(UnityEngine.Component scenario, string attemptId, object role,
            UnityEngine.Component source, object target)
        { throw new InvalidOperationException("scenario ownership is outside this admission fixture"); }
    }
}
public class __BaseUI : UnityEngine.Behaviour
{
    public bool interaction = true;
    public RootMessenger rmg;
    public DateTime visible_time = DateTime.Now.AddSeconds(-1);
    public bool loading, collecting;
    public bool IsEnable() { return interaction; }
    public bool IsVisible() { return enabled && gameObject.activeInHierarchy; }
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
namespace Database
{
    public class Character : CharacterData { public ulong id = 42; public object m_demon; }
    public class Demon { public UnityEngine.GameObject mesh; }
}
public class CapitalUI : StubUi { }
public class DemonScript : StubUi { public bool m_bFriend; public object m_demon; public UnityEngine.Component ParentUI; }
public class ConsumeUI : StubUi { }
public class UnknownOverlay : StubUi { }
public class AnimEffect : UnityEngine.Behaviour { public Action OnClose; }
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

