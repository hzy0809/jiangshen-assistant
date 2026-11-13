using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace JiangShen.AssistantRuntime
{
    internal sealed class ProbeFailure : Exception
    {
        internal ProbeFailure(string code) : base(code) { }
    }

    internal sealed class Snapshot
    {
        internal Component Main, Root;
        internal object Character;
        internal int InstanceId, Formation, Scene;
        internal bool InteractionEnabled;
        internal string Resources, Gate, LevelId, OtherVisibleUiTypes;
        internal long[] ResourceValues;
        internal bool Safe;
        internal bool SameTarget(Snapshot other)
        {
            return other != null && Main != null && other.Main != null && object.ReferenceEquals(Main, other.Main) &&
                object.ReferenceEquals(Character, other.Character) && Root == other.Root && Formation == other.Formation && Scene == other.Scene;
        }
        internal string Json()
        {
            return "\"action_name\":\"mainui_disable_restore\",\"main_instance_id\":" + InstanceId + ",\"scene\":" + Scene + ",\"root_level_id\":" + Command.Quote(LevelId) + ",\"gate\":" + Command.Quote(Gate) + ",\"safe\":" + (Safe ? "true" : "false") + ",\"main_enabled\":" + (InteractionEnabled ? "true" : "false") + ",\"formation_set\":" + Formation + ",\"other_visible_ui_types\":" + (OtherVisibleUiTypes == null ? "null" : OtherVisibleUiTypes) + ",\"resources\":" + (Resources == null ? "null" : Resources);
        }
    }

    internal sealed class Model
    {
        private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private readonly Type mainType, globalType, baseUiType, assetType, buildingType;
        private readonly Type rootUiType, popupType, tooltipType, messengerType, flowerType, buildingBehaviourType, capitalType, buildingAreaType;
        private readonly Type[] ambientTypes;
        private readonly MethodInfo disable, enable, findObjects;
        internal static readonly string[] ResourceNames = new string[] { "level", "stamina", "gold", "bind_gold", "experience", "practice", "spirit_food", "mana", "stone", "reputation", "money", "money64", "cur_formation_set", "formation_set" };

        internal Model()
        {
            Pin(typeof(object).Assembly, "mscorlib", "2.0.5.0", "3F34C90AB282E8F3546946263087FB5002CF96EF9F412DD4011DAB3F0F7CD8A4");
            Pin(typeof(UObject).Assembly, "UnityEngine", "0.0.0.0", "D479BEFC03AFB0F2071FFCF108847F06A0D560075EB8A76980F0AE929115ED73");
            // UnityEngine's System.Type API signatures refer to desktop mscorlib
            // 2.0.0.0, while this installed profile is 2.0.5.0. Keep that foreign
            // TypeRef out of our payload; invoke only this fixed/pinned API.
            MethodInfo[] unityMethods = typeof(UObject).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
            for (int i = 0; i < unityMethods.Length; i++)
            {
                MethodInfo m = unityMethods[i];
                if (m.Name != "FindObjectsOfType" || m.IsGenericMethod || m.ReturnType != typeof(UObject[])) continue;
                ParameterInfo[] parameters = m.GetParameters();
                if (parameters.Length != 1 || parameters[0].ParameterType.FullName != "System.Type") continue;
                if (findObjects != null) throw new ProbeFailure("unity_find_ambiguous");
                findObjects = m;
            }
            if (findObjects == null) throw new ProbeFailure("unity_find_signature");
            Assembly game = Loaded("Assembly-CSharp"), first = Loaded("Assembly-CSharp-firstpass"), pb = Loaded("pbdata");
            Pin(game, "Assembly-CSharp", null, "C1C8EBAC7DDEF4173C873A00774975A5CA366F95B19164C4CA775165EB994911");
            Pin(first, "Assembly-CSharp-firstpass", null, "29D8FA2CB55E800CCDF1CDC6C5B274B924EA7D03C989296DFA569731E02A8D87");
            Pin(pb, "pbdata", null, "905CF19394545E2C4B5D49D994A7D981E13AE38A2A629F7D02329F27C6D2D5AB");
            Pin(Loaded("dbdata"), "dbdata", null, "D32BB2B6C58B7CAFCD4BE5CBCDABBDB6A76C7003C0E2A160DEF80835BE412A2D");
            mainType = RequiredType(game, "MainUI"); globalType = RequiredType(first, "Global");
            baseUiType = FindInHierarchy(mainType, "__BaseUI"); assetType = FindInHierarchy(mainType, "BaseAsset");
            buildingType = RequiredType(game, "BuildingBuildUI");
            rootUiType = RequiredType(game, "RootUI");
            popupType = RequiredType(first, "PopupUI"); tooltipType = RequiredType(first, "TooltipUI");
            messengerType = RequiredType(game, "MessengerUI"); flowerType = RequiredType(game, "FlowerMessUI");
            buildingBehaviourType = RequiredType(game, "BuildingBehaviourScript"); capitalType = RequiredType(game, "CapitalUI");
            buildingAreaType = RequiredType(first, "Database.BuildingArea");
            ambientTypes = new Type[] { RequiredType(game, "BuildingMonsterScript"), RequiredType(game, "SecretShopNPC"), RequiredType(game, "CheckLauncherVersion"), RequiredType(game, "AnnounceBehaviour"), RequiredType(game, "MeditationUI"), RequiredType(game, "ChatBehaviour"), RequiredType(first, "GeneralSelectUI"), tooltipType };
            if (!typeof(MonoBehaviour).IsAssignableFrom(mainType)) throw new ProbeFailure("main_not_component");
            disable = VoidMethod(baseUiType, "Disable"); enable = VoidMethod(baseUiType, "Enable");
            if (!disable.IsVirtual || !enable.IsVirtual || disable.DeclaringType != baseUiType || enable.DeclaringType != baseUiType) throw new ProbeFailure("interaction_method_signature");
            for (Type t = mainType; t != baseUiType; t = t.BaseType)
            {
                if (t == null) throw new ProbeFailure("interaction_method_hierarchy");
                MethodInfo[] declared = t.GetMethods(AnyInstance);
                for (int i = 0; i < declared.Length; i++)
                    if ((declared[i].Name == "Enable" || declared[i].Name == "Disable") && declared[i].GetParameters().Length == 0) throw new ProbeFailure("interaction_method_override");
            }
        }

        internal Snapshot Capture() { return Capture(null); }
        internal Snapshot Capture(Component ownDisabledMain)
        {
            Snapshot s = new Snapshot(); s.Gate = "main_not_unique";
            s.Scene = Application.loadedLevel;
            UObject[] candidates = Find(mainType);
            int count = 0;
            for (int i = 0; i < candidates.Length; i++)
            {
                Behaviour b = candidates[i] as Behaviour;
                if (b != null && b.enabled && b.gameObject.activeInHierarchy)
                {
                    if (b.GetType() != mainType) { s.Gate = "unexpected_main_type"; return s; }
                    s.Main = b; count++;
                }
            }
            if (count != 1) return s;
            s.InstanceId = s.Main.GetInstanceID();
            object character = Read(globalType, "character_", null, true);
            if (character == null) { s.Gate = "character_null"; return s; }
            s.Character = character;
            s.Resources = ResourceJson(character, out s.ResourceValues);
            s.Formation = Convert.ToInt32(Read(character.GetType(), "formation_set", character, false));
            s.InteractionEnabled = (bool)Read(baseUiType, "enable", s.Main, false);
            object loadScreen = Read(assetType, "lds", null, true);
            Component load = loadScreen as Component;
            if (load == null || !load.gameObject.activeInHierarchy) { s.Gate = "loadscreen_missing"; return s; }
            s.Root = Read(load.GetType(), "currmg", load, false) as Component;
            Component mainRoot = Read(assetType, "rmg", s.Main, false) as Component;
            if (s.Root == null || s.Root != mainRoot || !s.Root.gameObject.activeInHierarchy || s.Main.transform.root != s.Root.transform) { s.Gate = "not_current_root"; return s; }
            s.LevelId = (string)Read(s.Root.GetType(), "LevelID", s.Root, false);
            if (String.IsNullOrEmpty(s.LevelId)) { s.Gate = "root_level_unknown"; return s; }
            if (((Behaviour)load).enabled || (bool)Read(load.GetType(), "isBattling", load, false) || (bool)Read(s.Root.GetType(), "bLoading", s.Root, false) || Convert.ToInt32(Read(s.Root.GetType(), "ExpectingCount", s.Root, false)) != 0)
            { s.Gate = "loading_or_battle"; return s; }
            if (!(bool)Read(globalType, "extern_enabled", null, true)) { s.Gate = "global_input_disabled"; return s; }
            if ((bool)Read(baseUiType, "first", s.Main, false) || !(bool)Read(assetType, "m_bInit", s.Main, false)) { s.Gate = "main_not_initialized"; return s; }
            if ((bool)Read(buildingType, "m_bBuildVisible", null, true)) { s.Gate = "building_overlay"; return s; }
            if ((bool)Read(popupType, "popup", null, true)) { s.Gate = "popup_active"; return s; }
            if ((bool)Read(buildingBehaviourType, "m_bEditVisible", null, true)) { s.Gate = "building_edit_visible"; return s; }
            UObject sub = Read(mainType, "m_subui", null, true) as UObject;
            if (sub != null) { s.Gate = "subui_present"; return s; }
            // Assess the actual components. The global visible counter also
            // includes selectors and hints that can coexist with these actions.
            UObject[] overlays = Find(baseUiType);
            StringBuilder others = new StringBuilder("[");
            bool otherVisible = false;
            string blockedGate = null;
            for (int i = 0; i < overlays.Length; i++)
            {
                Behaviour overlay = overlays[i] as Behaviour;
                if (overlay != null && overlay != s.Main && overlay.enabled && overlay.gameObject.activeInHierarchy && overlay.GetType() != rootUiType)
                {
                    if (otherVisible) others.Append(',');
                    others.Append(Command.Quote(overlay.GetType().FullName)); otherVisible = true;
                    string gate = OverlayGate(overlay);
                    if (blockedGate == null && gate != null) blockedGate = gate;
                }
            }
            others.Append(']'); s.OtherVisibleUiTypes = others.ToString();
            if (blockedGate != null) { s.Gate = blockedGate; return s; }
            if (s.Formation < 1 || s.Formation > 6) { s.Gate = "state_invalid"; return s; }
            if (!s.InteractionEnabled && (ownDisabledMain == null || !object.ReferenceEquals(s.Main, ownDisabledMain))) { s.Gate = "main_interaction_disabled"; return s; }
            s.Safe = true; s.Gate = "PASS";
            return s;
        }

        internal string OverlayGate(Behaviour overlay)
        {
            Type type = overlay.GetType();
            for (int i = 0; i < ambientTypes.Length; i++) if (type == ambientTypes[i]) return null;
            if (type == GameType("DemonScript"))
            {
                // A Capital scene actor is not its separate battle dialog.
                // Accept only the current character's exact mesh/script and
                // parent on the verified current root; every other instance
                // remains an unknown overlay.
                try
                {
                    Component root = CurrentRoot();
                    object character = Read(globalType, "character_", null, true);
                    Component owned = SharedDevelopmentContext.CapitalDemon(AllUi(), root, character);
                    return object.ReferenceEquals(overlay, owned) ? null : "visible_unknown_ui";
                }
                catch (ProbeFailure error)
                {
                    if (error.Message != "flower_capital_demon_identity") throw;
                    return "visible_unknown_ui";
                }
            }
            if (type == popupType) return (bool)Read(popupType, "popup", null, true) ? "popup_active" : null;
            if (type == buildingBehaviourType)
                return !(bool)Read(buildingBehaviourType, "m_bEdit", overlay, false) && !(bool)Read(buildingBehaviourType, "m_bEditVisible", null, true) ? null : "building_edit_active";
            if (type == flowerType)
                return !(bool)Read(flowerType, "fc", overlay, false) && !(bool)Read(flowerType, "rm", overlay, false) ? null : "flower_modal_active";
            if (type == capitalType)
            {
                object area = Read(capitalType, "curArea", null, true);
                return area != null && area.GetType() == buildingAreaType && (bool)Read(buildingAreaType, "unlocked", area, false) ? null : "capital_unlock_panel";
            }
            if (type == messengerType)
            {
                FieldInfo activeField = Field(messengerType, "active_sessions", false), pendingField = Field(messengerType, "pending_sessions", false);
                object active = activeField.GetValue(overlay), pending = pendingField.GetValue(overlay);
                if (active == null || pending == null || active.GetType() != activeField.FieldType || pending.GetType() != pendingField.FieldType) return "messenger_schema";
                IDictionary sessions = active as IDictionary; IList waiting = pending as IList;
                // Counts only: never enumerate keys, player names, sessions or messages.
                return sessions != null && waiting != null && sessions.Count == 0 && waiting.Count == 0 ? null : "messenger_active";
            }
            return "visible_unknown_ui";
        }

        internal void SetInteraction(Snapshot original, bool restore)
        {
            if (original.Main == null || original.Main.GetType() != mainType || !original.InteractionEnabled) throw new ProbeFailure("interaction_target_invalid");
            (restore ? enable : disable).Invoke(original.Main, new object[0]);
        }

        private UObject[] Find(Type type)
        {
            if (type != mainType && type != baseUiType) throw new ProbeFailure("unity_find_type_not_whitelisted");
            UObject[] found = (UObject[])findObjects.Invoke(null, new object[] { type });
            if (found == null || found.Length > 512) throw new ProbeFailure("unity_find_result_bound");
            return found;
        }
        internal UObject[] AllUi() { return Find(baseUiType); }
        internal Type RootMessengerType { get { return Field(assetType, "rmg", false).FieldType; } }
        // BattleUI deactivates the previous scene root. The authoritative
        // LoadScreen pointer must remain observable without an active MainUI.
        // This does not change the verification command's Safe/root gates.
        internal Component CurrentRoot()
        {
            FieldInfo loadField = Field(assetType, "lds", true);
            Component load = loadField.GetValue(null) as Component;
            if (load == null || load.GetType() != loadField.FieldType || !load.gameObject.activeInHierarchy) return null;
            FieldInfo currentField = Field(load.GetType(), "currmg", false);
            if (currentField.FieldType != RootMessengerType) throw new ProbeFailure("current_root_signature");
            Component root = currentField.GetValue(load) as Component;
            if (root == null || root.GetType() != RootMessengerType || root.transform.root != root.transform) return null;
            return root;
        }
        internal GameObject GlobalUiObject() { return GameObject.Find("Root/RootUI"); }
        internal GameObject BattleUiObject() { return GameObject.Find("Battle/BattleUI"); }
        internal static Type GameType(string name) { return RequiredType(Loaded("Assembly-CSharp"), name); }
        internal static Type FirstType(string name) { return RequiredType(Loaded("Assembly-CSharp-firstpass"), name); }

        private static string ResourceJson(object character, out long[] values)
        {
            StringBuilder b = new StringBuilder("{");
            values = new long[ResourceNames.Length];
            for (int i = 0; i < ResourceNames.Length; i++)
            {
                if (i != 0) b.Append(',');
                FieldInfo field = Field(character.GetType(), ResourceNames[i], false);
                Type t = field.FieldType;
                if (t != typeof(short) && t != typeof(int) && t != typeof(uint) && t != typeof(long)) throw new ProbeFailure("resource_type");
                values[i] = Convert.ToInt64(field.GetValue(character), CultureInfo.InvariantCulture);
                b.Append(Command.Quote(ResourceNames[i])); b.Append(':'); b.Append(values[i].ToString(CultureInfo.InvariantCulture));
            }
            b.Append('}'); return b.ToString();
        }
        private static Type RequiredType(Assembly a, string name)
        {
            Type t = a.GetType(name, false); if (t == null) throw new ProbeFailure("type_missing"); return t;
        }
        private static Type FindInHierarchy(Type t, string name)
        {
            for (; t != null; t = t.BaseType) if (t.FullName == name) return t;
            throw new ProbeFailure("base_type_missing");
        }
        private static FieldInfo Field(Type t, string name, bool isStatic)
        {
            for (; t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(name, isStatic ? AnyStatic : AnyInstance);
                if (f != null && f.IsStatic == isStatic) return f;
            }
            throw new ProbeFailure("field_missing_" + name);
        }
        private static object Read(Type t, string name, object target, bool isStatic) { return Field(t, name, isStatic).GetValue(target); }
        private static MethodInfo VoidMethod(Type t, string name)
        {
            MethodInfo m = t.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, null, new Type[0], null);
            if (m == null || m.ReturnType != typeof(void) || m.IsStatic || m.IsGenericMethod) throw new ProbeFailure("method_signature");
            return m;
        }
        private static Assembly Loaded(string name)
        {
            Assembly found = null;
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
                if (assemblies[i].GetName().Name == name)
                { if (found != null) throw new ProbeFailure("duplicate_assembly"); found = assemblies[i]; }
            if (found == null) throw new ProbeFailure("assembly_missing"); return found;
        }
        private static void Pin(Assembly a, string name, string version, string hash)
        {
            AssemblyName n = a.GetName();
            if (n.Name != name || (version != null && n.Version.ToString() != version) || String.IsNullOrEmpty(a.Location)) throw new ProbeFailure("assembly_identity");
            using (FileStream input = File.OpenRead(a.Location))
            using (SHA256Managed sha = new SHA256Managed())
            {
                string actual = BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "");
                if (actual != hash) throw new ProbeFailure("assembly_hash_" + name);
            }
        }
    }
}
