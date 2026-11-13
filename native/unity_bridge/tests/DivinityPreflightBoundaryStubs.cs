using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace UnityEngine
{
    public class Object { private static int next; private readonly int id=++next; public int GetInstanceID(){return id;} }
    public sealed class Transform { public Transform parent; public Transform root {get{return parent==null?this:parent.root;}} }
    public class Component : Object
    {
        public GameObject gameObject=new GameObject();
        public Transform transform {get{return gameObject.transform;}}
        public Component GetComponent(Type type){return gameObject.GetComponent(type);}
        public Component GetComponent(string type){return gameObject.GetComponent(type);}
        public Component GetComponentInChildren(Type type){return gameObject.GetComponent(type);}
        public Component[] GetComponents(Type type){return gameObject.GetComponents(type);}
    }
    public class Behaviour : Component { public bool enabled=true; }
    public sealed class GameObject : Object
    {
        public readonly List<Component> Components=new List<Component>();
        public readonly Transform transform=new Transform();
        public bool activeInHierarchy=true; public string name="fixture";
        public static GameObject Find(string path){return null;}
        public Component GetComponent(Type type){return Components.Find(delegate(Component c){return type.IsInstanceOfType(c);});}
        public Component GetComponent(string type){return Components.Find(delegate(Component c){return c.GetType().Name==type;});}
        public Component[] GetComponents(Type type){return Components.FindAll(delegate(Component c){return type.IsInstanceOfType(c);}).ToArray();}
    }
    public sealed class Event { public static Event current=new Event(); }
    public static class GUI { public static bool enabled=true; }
    public static class Time { public static int frameCount; }
    public static class Camera { public static object main=new object(); }
    public static class Resources { public static Object[] FindObjectsOfTypeAll(Type type){return new Object[0];} }
}

// Explicit detached boundaries. The full production Divinity model, context,
// clock, transport and pinned protobuf/DEFLATE checks are linked by the runner.
// No Win32 launch guard, settlement, scene loader or real game is exercised.
namespace JiangShen.AssistantRuntime
{
    internal sealed class ProbeFailure : Exception { internal ProbeFailure(string why):base(why){} }
    internal sealed class Snapshot { internal Component Root; internal object Character; internal string Gate; internal bool InteractionEnabled; }
    internal sealed class Model
    {
        internal static Type GameType(string name){return FirstType(name);}
        internal static Type FirstType(string name)
        {
            if(name=="game.net.tcp_client")return DivinityFixtureTypes.ClientType;
            if(name.StartsWith("game.net.data."))return typeof(game.net.data.StoC.SCGodBattle_0x4B1).Assembly.GetType(name,true);
            return typeof(Model).Assembly.GetType(name,true);
        }
        internal Component CurrentRoot(){return null;}
        internal GameObject GlobalUiObject(){return null;}
        internal UnityEngine.Object[] AllUi(){return new UnityEngine.Object[0];}
        internal Type RootMessengerType {get{return typeof(Component);}}
        internal Snapshot Capture(){return null;}
        internal string OverlayGate(Component item){return null;}
    }
    internal delegate bool BattleLaunchCheck();
    internal delegate void BattleLaunchAction();
    internal delegate bool BattleSettlementAdmission();
    internal sealed class BattleLaunchAttempt
    {
        internal void FormationReturned() { }
        internal void InitialResponseApplied(Component battle, object report)
        { if (battle == null || report == null) throw new ProbeFailure("fixture_missing_report"); }
    }
    internal static class BattleLaunch
    {
        internal static BattleLaunchAttempt Formation(BattleMode mode, Component formation, Component source,
            object target, BattleLaunchCheck check, BattleLaunchAction after)
        { throw new ProbeFailure("fixture_uses_test_admission"); }
        internal static bool OwnsAttack(object formation, object attack) { return false; }
    }
    internal sealed class BattlePresentationOwnership
    {
        internal BattlePresentationOwnership(BattleMode mode, string id, Component source, object target,
            string targetId, BattleSettlementAdmission verify) { }
    }
    internal static class BattleSettlement
    {
        internal static byte[] AppliedResponse(Component battle, object report) { return null; }
        internal static bool Owns(Component battle, object report) { return false; }
    }
}

public static class DivinityFixtureTypes { public static Type ClientType; }
public static class Global { public static object character_,m_client; public static bool relogin; }
public static class RootUI { public static object m_client; public static bool m_bRefreshFive; }
public static class BaseAsset { public static object lds; }
public static class TimeUtil { public static uint _now=19500,_thisDay=1000; }
public sealed class DivinityLoader { public object currmg; }
public sealed class DivinityPanel { public object rmg,m_troop; }
public sealed class DivinityTroop { public byte idtroop; public DivinityTroopData data_=new DivinityTroopData(); }
public sealed class DivinityTroopData { public bool killed; public uint cd_time; }
public sealed class DivinitySocket { public bool Connected {get;set;} public DivinitySocket(){Connected=true;} }
namespace game.net { public static class tcp_base { public static int connection_state=3; } }
namespace Database { public sealed class Character { public ulong id=78; public uint next_resource_time=104400; public DivinityTroop[] god_troops=new DivinityTroop[5]; } }
