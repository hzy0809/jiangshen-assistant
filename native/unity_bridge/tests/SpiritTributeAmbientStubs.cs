using System;
using System.Collections;
using System.Collections.Generic;

namespace UnityEngine
{
    public class Object { static int next; readonly int id = ++next; public int GetInstanceID() { return id; } }
    public class Transform { public Transform root; }
    public class GameObject
    {
        public bool activeInHierarchy = true;
        public Transform transform = new Transform();
        public readonly List<Component> components = new List<Component>();
        public GameObject() { transform.root = transform; }
        public Component GetComponent(string name) { return components.Find(c => c.GetType().Name == name); }
    }
    public class Component : Object { public GameObject gameObject; public Transform transform { get { return gameObject.transform; } } }
    public class Behaviour : Component { public bool enabled = true; }
    public class Event { public static Event current = new Event(); }
    public static class GUI { public static bool enabled = true; }
}
namespace JiangShen.AssistantRuntime
{
    internal sealed class ProbeFailure : Exception { internal ProbeFailure(string message) : base(message) { } }
    internal static class Command { internal static string Quote(string s) { return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""; } }
    internal static class Model
    {
        internal static Type GameType(string name) { return Required(name); }
        internal static Type FirstType(string name) { return Required(name); }
        static Type Required(string name) { Type t = Type.GetType(name); if (t == null) throw new Exception("Unknown fixture type: " + name); return t; }
    }
}
public class BaseAsset : UnityEngine.Behaviour { public UnityEngine.Component rmg; public bool loading, collecting; }
public class __BaseUI : BaseAsset
{
    public delegate void delegate_OnClose();
    public delegate_OnClose OnClose;
    public bool visible, interactive = true;
    public bool IsVisible() { return visible; }
    public bool IsEnable() { return interactive; }
    public void Hide() { visible = false; }
    public void EnableAll() { interactive = true; }
}
public class BaseUI : __BaseUI { public static RootUI rootUI; }
public class RootUI : BaseUI { public static FixtureClient m_client; public string LevelID = "Capital"; }
public class MainUI : BaseUI
{
    public static __BaseUI m_subui;
    public static bool checkSpirit(string name) { return true; }
    public static bool checkSpirit2(string name) { return false; }
    public static bool checkSpirit3(string name) { return false; }
}
public class CapitalUI : BaseUI { }
public class DemonScript : BaseUI { public bool m_bFriend; public object m_demon; public UnityEngine.Component ParentUI; }
public class SpiritUI : __BaseUI
{
    public static __BaseUI my_Subui;
    public object m_spirit;
    public int m_iPage;
    public uint food_type;
    public bool m_bRefreshing;
    public int TrainCalls;
    private void cs_spirit_train_0x470(bool gold, uint count) { TrainCalls++; }
}
public class SpiritUI_2 : __BaseUI { public static __BaseUI my_Subui; public object m_spirit; public bool showskill, flying; public ArrayList DragonHead = new ArrayList(), DragonHead2 = new ArrayList(); }
public class SpiritUI_3 : __BaseUI { public static __BaseUI my_Subui; }
public class SpiritRankupUI : __BaseUI { public object m_spirit; }
public class ConsumeUI : __BaseUI { }
public class UnknownOverlay : __BaseUI { }
public class FixtureClient { public readonly Hashtable m_handlers = new Hashtable(); public readonly ArrayList messages = new ArrayList(); public int Sends; }
public static class Global
{
    public static Database.Character character_;
    public static FixtureClient m_client;
    public static bool extern_enabled = true;
    public static object getTemplate(string name, uint id) { return new Database.Exp { level = (int)id }; }
}
namespace Database
{
    public class Character { public ulong id = 42; public bool gm; public object m_demon; public Spirit spirit = new Spirit(); public long spirit_food = 5, gold = 7; }
    public class Demon { public UnityEngine.GameObject mesh; }
    public class Spirit { public int level = 9; public uint experience = 20; }
    public class Exp { public int level; public uint exp = 100; }
}

public class DragonDescription { public string specdesc = ""; }

