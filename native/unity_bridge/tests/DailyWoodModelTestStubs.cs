using System;
using JiangShen.AssistantRuntime;

// Model-only boundary: DailyWoodTests supplies owned Frame/stock delegates.
// No Probe or game scene is initialized by this executable.
namespace UnityEngine
{
    public class Object { public int GetInstanceID() { return 1; } }
    public class Transform
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
    public class GameObject
    {
        public bool activeInHierarchy = true;
        public string name = "fixture";
        public readonly Transform transform = new Transform();
        public static GameObject Find(string path) { return null; }
        public Component GetComponent(string name) { return null; }
        public Component GetComponent(Type type) { return null; }
        public Component[] GetComponents(Type type) { return new Component[0]; }
    }
    public sealed class Event { public static Event current = new Event(); }
    public static class GUI { public static bool enabled = true; }
    public static class Time { public static int frameCount = 1; }
    public static class Resources { public static Object[] FindObjectsOfTypeAll(Type type) { return new Object[0]; } }
}
namespace JiangShen.AssistantRuntime
{
    internal sealed class ProbeFailure : Exception { internal ProbeFailure(string message) : base(message) { } }
    internal sealed class Snapshot
    { internal string Gate; internal bool Safe, InteractionEnabled; internal UnityEngine.Component Root; internal object Character; }
    internal sealed class Model
    {
        internal static Type FirstType(string name) { return typeof(Model).Assembly.GetType(name, true); }
        internal static Type GameType(string name) { return FirstType(name); }
        internal UnityEngine.GameObject GlobalUiObject() { return null; }
        internal UnityEngine.Component CurrentRoot() { return null; }
        internal UnityEngine.Object[] AllUi() { return new UnityEngine.Object[0]; }
        internal Type RootMessengerType { get { return typeof(UnityEngine.Component); } }
        internal string OverlayGate(UnityEngine.Behaviour ui) { return "fixture-model-only"; }
        internal Snapshot Capture() { return new Snapshot { Gate = "PASS", Safe = true }; }
    }
}
