using System;
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Threading;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace JiangShen.AssistantRuntime
{
    // A single owned GUI teaching transaction. No wire supplied reflection name,
    // general id, formation id or callback is accepted by this class.
    internal sealed class GeneralTutorialModel
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private readonly Type specialType = Model.FirstType("SpecialGetUI"), formationType = Model.GameType("FormationUI");
        private readonly Type mainType = Model.GameType("MainUI"), globalType = Model.FirstType("Global"), rootType = Model.GameType("RootUI");
        private Component special, formation, root, globalRoot, globalUi;
        private object character, client;
        private string characterId = "0", status = "NONE", reason = "", gate = "UNKNOWN", local = "NONE";
        private uint generalId;
        private DateTime visibleTime;
        private bool showClose, networkObserved, networkPending, owned, callStarted, confirmPhase;
        private string sessionKey = "";
        private Anchor queued, session;
        private int queuedSequence;
        private string queuedAction;
        private bool mutationAttempted;
        private Component recruit71Predecessor;
        internal void CaptureRecruit71(UObject[] all, Component currentRoot, Component global, object currentCharacter, string id, Component predecessor)
        {
            recruit71Predecessor = predecessor != null && predecessor.GetType() == Model.GameType("GeneralRecruitUI") && !Visible(predecessor) &&
                global != null && predecessor.gameObject == global.gameObject && OwnedBy(predecessor, Read(global, "rmg") as Component) ? predecessor : null;
            Capture(all, currentRoot, global, currentCharacter, id);
        }
        private bool DeploySubUiOwned()
        {
            object sub = ReadStatic(mainType, "m_subui");
            return sub == null || generalId == 71 && recruit71Predecessor != null && object.ReferenceEquals(sub, recruit71Predecessor) &&
                !Visible(recruit71Predecessor) && OwnedBy(recruit71Predecessor, globalRoot);
        }
        internal bool MutationAttempted { get { return mutationAttempted; } }
        internal string BoundCharacterId { get { return session == null ? "" : session.CharacterId; } }
        internal bool Active { get { return status != "NONE" && status != "COMPLETE"; } }
        internal string Gate { get { return gate; } }
        internal string NextAction { get { return Active ? (confirmPhase ? "formation_confirm" : "general_deploy") : ""; } }
        internal string Status { get { return status; } }
        internal bool Allows(Component value) { return Active && (object.ReferenceEquals(value, special) || (owned && object.ReferenceEquals(value, formation))); }
        internal static bool Action(string action) { return action == "general_deploy" || action == "formation_confirm"; }
        private sealed class Anchor
        {
            internal Component Special, Formation, Root, GlobalRoot, GlobalUi;
            internal object Character, Client, General, Form, FormInfo;
            internal IDictionary Parameters;
            internal IDictionary Generals, Forms;
            internal object[] GeneralKeys, GeneralValues, FormKeys, FormValues;
            internal IList Spots, DbSpots;
            internal Array Positions;
            internal int[] SpotValues, DbValues;
            internal long[] PositionValues;
            internal int GeneralCount, FormCount, CurrentForm, SpotCount;
            internal string CharacterId;
            internal uint GeneralId;
            internal DateTime VisibleTime;
            internal DateTime FormVisibleTime;
            internal bool ShowClose;
            internal Delegate Open;
        }

        internal void Capture(UObject[] all, Component currentRoot, Component global, object currentCharacter, string id)
        {
            root = currentRoot; globalUi = global; globalRoot = global == null ? null : Read(global, "rmg") as Component;
            character = currentCharacter; characterId = id;
            Component foundSpecial = null, foundForm = null; int specials = 0, forms = 0;
            for (int i = 0; i < all.Length; i++)
            {
                Component c = all[i] as Component; if (c == null) continue;
                if (c.GetType() == specialType && Visible(c)) { foundSpecial = c; specials++; }
                if (c.GetType() == formationType && global != null && c.gameObject == global.gameObject && OwnedBy(c, globalRoot)) { foundForm = c; forms++; }
            }
            if (!owned)
            {
                if (foundSpecial == null) { if (status != "COMPLETE") status = "NONE"; return; }
                special = foundSpecial; formation = forms == 1 ? foundForm : null;
                if (status == "COMPLETE" && session != null && special == session.Special && (DateTime)Read(special, "visible_time") == session.VisibleTime)
                { Unknown("general_popup_replayed"); return; }
                status = "DEPLOY"; local = "NONE"; confirmPhase = false; uint popupId = UInt(Read(special, "tempid")); generalId = popupId <= UInt16.MaxValue ? popupId : 0; visibleTime = (DateTime)Read(special, "visible_time");
                showClose = Bool(Read(special, "showFormClose"));
                sessionKey = "general:" + generalId.ToString(CultureInfo.InvariantCulture) + ":" + special.GetInstanceID() + ":" + visibleTime.Ticks.ToString(CultureInfo.InvariantCulture);
                reason = Admission(global, specials, forms);
                if (reason != "")
                {
                    if (reason == "general_popup_not_interactive") gate = "WAITING_UI";
                    else { gate = "UNKNOWN"; status = "UNKNOWN"; }
                    return;
                }
                SampleNetwork();
                gate = !networkObserved || networkPending ? "WAITING_NETWORK" : "PASS";
                reason = gate == "PASS" ? "" : "general_network_not_idle";
                return;
            }
            if (status == "UNKNOWN") return;
            if (specials > 1 || forms != 1 || !object.ReferenceEquals(foundForm, session.Formation) ||
                (foundSpecial != null && !object.ReferenceEquals(foundSpecial, session.Special)) || !SameIdentity(session))
            { Unknown("general_session_context_changed"); return; }
            special = session.Special; formation = session.Formation;
            if (Read(special, "showtype") as string != "general" || UInt(Read(special, "tempid")) != session.GeneralId ||
                (DateTime)Read(special, "visible_time") != session.VisibleTime || Bool(Read(special, "showFormClose")) != session.ShowClose)
            { Unknown("general_popup_context_changed"); return; }
            if (!SameCharacterForm(session, true)) { Unknown("general_owned_configuration_changed"); return; }
            SampleNetwork();
            if (status == "WAITING_FORMATION" || status == "FORMATION_CONFIRM")
            {
                if (!SameOpenContext(session) || !NormalFormation()) { Unknown("general_formation_context_changed"); return; }
                if (Visible(special)) { gate = "WAITING_UI"; reason = "general_popup_still_visible"; return; }
                if (!Visible(formation) || Bool(Read(formation, "isLoading"))) { gate = "WAITING_UI"; reason = "general_formation_loading"; return; }
                if (!networkObserved || networkPending) { gate = "WAITING_NETWORK"; reason = "general_network_not_idle"; return; }
                if (!Enabled(formation) || !Bool(Read(formation, "isVisible")) || Int(Read(formation, "waitstate")) != 0)
                { gate = "WAITING_UI"; reason = "general_formation_not_ready"; return; }
                if (session.FormInfo == null)
                {
                    try { CaptureFormation(session); }
                    catch (ProbeFailure) { Unknown("general_formation_postcondition_missing"); return; }
                }
                else if (!SameFormation(session)) { Unknown("general_formation_selection_changed"); return; }
                status = "FORMATION_CONFIRM"; confirmPhase = true; local = "DEPLOYED"; gate = "PASS"; reason = "";
            }
            else if (status == "WAITING_CLOSE")
            {
                if (!SameOpenContext(session) || !SameFormation(session) || !FormationMode()) { Unknown("general_formation_selection_changed"); return; }
                if (Visible(special) || Visible(formation) || Bool(Read(formation, "isVisible")) || ReadStatic(mainType, "m_subui") != null)
                { Unknown("general_formation_reopened_or_not_closed"); return; }
                if (!networkObserved || networkPending) { gate = "WAITING_NETWORK"; reason = "general_network_not_idle"; return; }
                if (Int(Read(formation, "waitstate")) != 0) { Unknown("general_confirmation_not_observed"); return; }
                status = "COMPLETE"; gate = "PASS"; local = "CLOSED"; reason = ""; owned = false;
            }
        }
        private string Admission(Component global, int specials, int forms)
        {
            if (specials != 1 || global == null || !OwnedBy(special, globalRoot) || special.gameObject != global.gameObject || forms != 1) return "general_ui_owner_or_uniqueness";
            if (root == null || Read(root, "LevelID") as string != "Campaign" || !root.gameObject.activeInHierarchy || character == null || characterId == "0" ||
                !object.ReferenceEquals(ReadStatic(globalType, "character_"), character) || Bool(Read(character, "gm")) || !CachedRootMatches()) return "general_role_or_root";
            if (Read(special, "showtype") as string != "general" || generalId == 0 || generalId > UInt16.MaxValue || showClose != (generalId == 104)) return "general_popup_unreviewed";
            if (!Enabled(special) || Bool(Read(special, "loading")) || Bool(Read(special, "collecting")) || (DateTime.Now - visibleTime).TotalSeconds < 0.30000001192092896) return "general_popup_not_interactive";
            if (Visible(formation) || !DeploySubUiOwned()) return "general_existing_subui";
            Anchor a = NewAnchor();
            if (!ReadCharacterForm(a)) return "general_owned_data_schema";
            int first = -1; bool present = false;
            for (int i = 0; i < a.SpotValues.Length; i++) { if (a.SpotValues[i] == generalId) present = true; if (first == -1 && i < a.SpotCount && a.SpotValues[i] == -1) first = i; }
            if (!present && first < 0) return "general_no_deploy_slot";
            a.Open = OpenDelegate();
            if (a.Open == null) return "general_open_delegate_unreviewed";
            client = ReadStatic(rootType, "m_client");
            if (client == null || !object.ReferenceEquals(client, ReadStatic(globalType, "m_client"))) return "general_client_mismatch";
            return "";
        }
        internal bool Queue(Command command, out string why)
        {
            why = "";
            if (queuedSequence != 0) { why = "general_gui_command_pending"; return false; }
            if (!Active || gate != "PASS" || (command.CampaignAction == "general_deploy" ? status != "DEPLOY" : status != "FORMATION_CONFIRM"))
            { why = "general_action_unavailable:" + status + ":" + gate; return false; }
            if (command.CampaignAction == "formation_confirm" && (!SameOpenContext(session) || !SameFormation(session))) { why = "general_owned_formation_changed"; return false; }
            queued = NewAnchor(); if (!ReadCharacterForm(queued)) { why = "general_owned_data_schema"; return false; }
            queued.Open = OpenDelegate();
            if (command.CampaignAction == "formation_confirm") { queued.Parameters = session.Parameters; CaptureFormation(queued); }
            queuedSequence = command.Sequence; queuedAction = command.CampaignAction; return true;
        }
        internal bool ExecuteGui(Command command, CampaignPublication publish, out string why)
        {
            Anchor a = queued; bool match = queuedSequence == command.Sequence && queuedAction == command.CampaignAction; queuedSequence = 0; queued = null;
            why = "";
            if (!match || a == null || Event.current == null || !GUI.enabled) { why = "general_requires_owned_gui_context"; return false; }
            if (!SameIdentity(a) || !SameCharacterForm(a, false) || !object.ReferenceEquals(special, a.Special) || !object.ReferenceEquals(formation, a.Formation) ||
                !object.Equals(OpenDelegate(), a.Open) || Read(special, "showtype") as string != "general" || UInt(Read(special, "tempid")) != a.GeneralId ||
                (DateTime)Read(special, "visible_time") != a.VisibleTime || Bool(Read(special, "showFormClose")) != a.ShowClose ||
                (command.CampaignAction == "formation_confirm" && (!SameFormation(a) || !SameOpenContext(a))))
            { why = "general_gui_context_changed"; return false; }
            if (command.CampaignAction == "general_deploy" ? !Enabled(special) || !Visible(special) || Bool(Read(special, "loading")) || Bool(Read(special, "collecting")) ||
                (DateTime.Now - visibleTime).TotalSeconds < 0.30000001192092896 || Visible(formation) || !DeploySubUiOwned() :
                !NormalFormation() || !Enabled(formation) || !Bool(Read(formation, "isVisible")) || Bool(Read(formation, "isLoading")) || Int(Read(formation, "waitstate")) != 0 || Visible(special))
            { why = "general_gui_input_changed"; return false; }
            SampleNetwork();
            if (!networkObserved || networkPending) { why = "general_network_not_idle"; return false; }
            if (gate != "PASS" || (command.CampaignAction == "general_deploy" ? status != "DEPLOY" : status != "FORMATION_CONFIRM")) { why = "general_action_changed"; return false; }
            if (command.CampaignAction == "general_deploy")
            {
                // Resolve the complete fixed branch before publication and before
                // the first local write/send. The runtime delegate is also pinned.
                MethodInfo add = Method(character.GetType(), "AddOneGenToCurForm", new Type[] { typeof(uint) }, false, typeof(bool));
                Type mode = Model.FirstType("RootBase").GetNestedType("enumGeneralAutoEquipMode", BindingFlags.Public);
                if (mode == null || !mode.IsEnum || Convert.ToInt32(Enum.Parse(mode, "Attack"), CultureInfo.InvariantCulture) != 0) throw new ProbeFailure("general_equip_mode_schema");
                MethodInfo equip = Method(Model.FirstType("RootBase"), "cs_general_auto_equip", new Type[] { typeof(int), mode }, true, typeof(void));
                MethodInfo hide = Method(specialType, "Hide", Type.EmptyTypes, false, typeof(void));
                session = a; owned = true; status = "WAITING_FORMATION"; gate = "WAITING_UI"; local = "NONE"; callStarted = false;
                publish(); // May cancel. No call to Add/equip/Hide/open precedes it.
                callStarted = true; mutationAttempted = true; local = "INVOKED";
                bool added = (bool)add.Invoke(character, new object[] { generalId });
                if (added) equip.Invoke(null, new object[] { (int)generalId, Enum.Parse(mode, "Attack") });
                hide.Invoke(special, new object[0]); a.Open.DynamicInvoke(new object[] { showClose });
                a.Parameters = Read(formation, "m_lastparam") as IDictionary;
                int slot = -1; bool present = false;
                for (int i = 0; i < a.SpotValues.Length; i++) { if (a.SpotValues[i] == generalId) present = true; if (slot < 0 && i < a.SpotCount && a.SpotValues[i] == -1) slot = i; }
                if (!present && slot >= 0) a.SpotValues[slot] = (int)generalId;
                if (!added || !SameCharacterForm(a, true) || !SameOpenContext(a) || !NormalFormation()) Unknown("general_deploy_postcondition_missing");
            }
            else
            {
                MethodInfo close = Method(formationType, "OnClickClose", Type.EmptyTypes, false, typeof(void));
                status = "WAITING_CLOSE"; gate = "WAITING_UI"; callStarted = false;
                publish(); callStarted = true; mutationAttempted = true; local = "INVOKED";
                close.Invoke(formation, new object[0]);
            }
            return true;
        }
        internal void Cancel(Command command)
        {
            if (queuedSequence == command.Sequence) { queuedSequence = 0; queued = null; }
            if (!callStarted && (status == "WAITING_FORMATION" || status == "WAITING_CLOSE"))
            {
                bool deploy = command.CampaignAction == "general_deploy";
                status = deploy ? "DEPLOY" : "FORMATION_CONFIRM"; gate = "PASS";
                if (deploy) { owned = false; session = null; } else local = "DEPLOYED";
            }
        }
        internal void Failed(Command command, bool attempted, string why)
        { if (queuedSequence == command.Sequence) { queuedSequence = 0; queued = null; } if (attempted || callStarted) Unknown(why); }
        private void Unknown(string why) { status = "UNKNOWN"; gate = "UNKNOWN"; reason = why; owned = true; }
        private Anchor NewAnchor()
        { return new Anchor { Special = special, Formation = formation, Root = root, GlobalRoot = globalRoot, GlobalUi = globalUi, Character = character, CharacterId = characterId, Client = client, GeneralId = generalId, VisibleTime = visibleTime, ShowClose = showClose }; }
        private bool SameIdentity(Anchor a)
        {
            return a != null && object.ReferenceEquals(root, a.Root) && object.ReferenceEquals(globalRoot, a.GlobalRoot) && object.ReferenceEquals(globalUi, a.GlobalUi) && CachedRootMatches() && root != null && root.gameObject.activeInHierarchy &&
                Read(root, "LevelID") as string == "Campaign" && object.ReferenceEquals(character, a.Character) && characterId == a.CharacterId &&
                object.ReferenceEquals(ReadStatic(globalType, "character_"), a.Character) && object.ReferenceEquals(ReadStatic(rootType, "m_client"), a.Client) &&
                object.ReferenceEquals(ReadStatic(globalType, "m_client"), a.Client) && OwnedBy(a.Special, a.GlobalRoot) && OwnedBy(a.Formation, a.GlobalRoot);
        }
        private bool ReadCharacterForm(Anchor a)
        {
            a.Generals = Read(character, "form_generals_") as IDictionary; a.Forms = Read(character, "formations_") as IDictionary;
            if (a.Generals == null || a.Forms == null || a.Generals.Count > 512 || a.Forms.Count > 128 || !a.Generals.Contains((ushort)generalId)) return false;
            a.General = a.Generals[(ushort)generalId]; a.GeneralCount = a.Generals.Count; a.FormCount = a.Forms.Count; a.CurrentForm = Int(Read(character, "cur_form"));
            if (a.General == null || UInt(Read(a.General, "idgeneral_template")) != generalId || !a.Forms.Contains(a.CurrentForm)) return false;
            a.Form = a.Forms[a.CurrentForm]; if (a.Form == null || Int(Read(a.Form, "id")) != a.CurrentForm) return false;
            a.Spots = Read(a.Form, "spotgen") as IList; a.SpotCount = Int(Read(a.Form, "spotnum"));
            if (a.Spots == null || a.Spots.Count != 8 || a.SpotCount < 1 || a.SpotCount > 8) return false;
            a.SpotValues = Values(a.Spots);
            MapValues(a.Generals, out a.GeneralKeys, out a.GeneralValues); MapValues(a.Forms, out a.FormKeys, out a.FormValues);
            return true;
        }
        private bool SameCharacterForm(Anchor a, bool afterDeploy)
        {
            if (a == null || !object.ReferenceEquals(Read(character, "form_generals_"), a.Generals) || !object.ReferenceEquals(Read(character, "formations_"), a.Forms) ||
                a.Generals.Count != a.GeneralCount || a.Forms.Count != a.FormCount || !SameMap(a.Generals, a.GeneralKeys, a.GeneralValues) || !SameMap(a.Forms, a.FormKeys, a.FormValues) ||
                !a.Generals.Contains((ushort)a.GeneralId) || !object.ReferenceEquals(a.Generals[(ushort)a.GeneralId], a.General) ||
                UInt(Read(a.General, "idgeneral_template")) != a.GeneralId || Int(Read(character, "cur_form")) != a.CurrentForm || !a.Forms.Contains(a.CurrentForm) || !object.ReferenceEquals(a.Forms[a.CurrentForm], a.Form) ||
                Int(Read(a.Form, "id")) != a.CurrentForm || Int(Read(a.Form, "spotnum")) != a.SpotCount || !object.ReferenceEquals(Read(a.Form, "spotgen"), a.Spots) || !SameValues(a.Spots, a.SpotValues)) return false;
            // afterDeploy is an explicit marker at call sites; SpotValues itself
            // advances exactly once after the ordinary AddOneGen branch.
            return !afterDeploy || a.Spots.Contains((int)a.GeneralId);
        }
        private void CaptureFormation(Anchor a)
        {
            a.FormVisibleTime = (DateTime)Read(formation, "visible_time");
            a.FormInfo = Read(formation, "CharFormation");
            a.DbSpots = a.FormInfo == null ? null : Read(a.FormInfo, "dbspot") as IList;
            a.Positions = a.FormInfo == null ? null : Read(a.FormInfo, "formgenpos") as Array;
            if (a.FormInfo == null || Int(Read(a.FormInfo, "formid")) != a.CurrentForm || a.DbSpots == null || a.DbSpots.Count != 8 || a.Positions == null || a.Positions.Rank != 2 || a.Positions.GetLength(0) != 4 || a.Positions.GetLength(1) != 4) throw new ProbeFailure("general_formation_schema");
            a.DbValues = Values(a.DbSpots); a.PositionValues = new long[16];
            for (int i = 0; i < 16; i++) a.PositionValues[i] = Convert.ToInt64(Read(a.Positions.GetValue(i / 4, i % 4), "genid"), CultureInfo.InvariantCulture);
            for (int i = 0; i < 8; i++) { int loc = a.DbValues[i]; if (loc < 0 || loc > 15 || a.PositionValues[loc] != a.SpotValues[i]) throw new ProbeFailure("general_formation_content_mismatch"); for (int j = 0; j < i; j++) if (a.DbValues[j] == loc) throw new ProbeFailure("general_formation_duplicate_spot"); }
        }
        private bool SameFormation(Anchor a)
        {
            if ((DateTime)Read(formation, "visible_time") != a.FormVisibleTime || !object.ReferenceEquals(Read(formation, "CharFormation"), a.FormInfo) || a.FormInfo == null || Int(Read(a.FormInfo, "formid")) != a.CurrentForm ||
                !object.ReferenceEquals(Read(a.FormInfo, "dbspot"), a.DbSpots) || !SameValues(a.DbSpots, a.DbValues) || !object.ReferenceEquals(Read(a.FormInfo, "formgenpos"), a.Positions)) return false;
            for (int i = 0; i < 16; i++) if (Convert.ToInt64(Read(a.Positions.GetValue(i / 4, i % 4), "genid"), CultureInfo.InvariantCulture) != a.PositionValues[i]) return false;
            return true;
        }
        private void SampleNetwork()
        {
            networkObserved = false; networkPending = true;
            object current = ReadStatic(rootType, "m_client");
            if (current == null || !object.ReferenceEquals(current, ReadStatic(globalType, "m_client"))) return;
            client = current;
            if (!Monitor.TryEnter(client, 0)) return;
            try
            {
                IDictionary handlers = Read(client, "m_handlers") as IDictionary; IList messages = Read(client, "messages") as IList;
                if (handlers == null || messages == null || handlers.Count > 256 || messages.Count > 64) return;
                networkPending = handlers.Contains((ushort)298) || handlers.Contains((ushort)771);
                for (int i = 0; i < messages.Count; i++) { int op = Int(Read(messages[i], "op")); if (op == 298 || op == 771) networkPending = true; }
                networkObserved = true;
            }
            finally { Monitor.Exit(client); }
        }
        private Delegate OpenDelegate()
        {
            Delegate d = ReadStatic(Model.FirstType("DelegateFunc"), "MainUI_formation_ui") as Delegate;
            if (d == null || d.GetInvocationList().Length != 1 || d.Target != null || d.Method.DeclaringType != Model.GameType("DelegateFuncUI") || d.Method.Name != "MainUI_formation_ui" || !d.Method.IsStatic || d.Method.ReturnType != typeof(void)) return null;
            ParameterInfo[] p = d.Method.GetParameters(); return p.Length == 1 && p[0].ParameterType == typeof(bool) ? d : null;
        }
        private bool NormalFormation()
        {
            return FormationMode() && object.ReferenceEquals(ReadStatic(mainType, "m_subui"), formation);
        }
        private bool FormationMode()
        {
            return Read(formation, "DefaultState") as string == "Formation" && Bool(Read(formation, "viewonly")) &&
                Read(formation, "RightPanel") as string == "GenInfo" && Read(formation, "substate") as string == "" &&
                !Bool(Read(formation, "onfight")) && Read(formation, "OnClose") == null &&
                Bool(Read(formation, "showCloseTip")) == showClose;
        }
        private bool SameOpenContext(Anchor a)
        {
            return a.Parameters != null && object.ReferenceEquals(Read(formation, "m_lastparam"), a.Parameters) &&
                a.Parameters.Count == 1 && a.Parameters.Contains("subui") && a.Parameters["subui"] as string == "Formation";
        }
        private bool CachedRootMatches()
        {
            object cached = ReadStatic(Model.GameType("BaseUI"), "rootUI");
            return cached == null || object.ReferenceEquals(cached, globalUi);
        }
        internal string TutorialJson()
        {
            if (!Active) return "{\"kind\":\"NONE\",\"step\":\"\",\"gate\":\"\"}";
            string suffix = confirmPhase ? ":confirm" : ":deploy";
            return "{\"kind\":\"GENERAL\",\"step\":" + Command.Quote(sessionKey + suffix) + ",\"gate\":" + Command.Quote(gate) + "}";
        }
        internal string Json()
        {
            return "{\"general_id\":" + generalId + ",\"status\":" + Command.Quote(status) + ",\"local_observation\":" + Command.Quote(local) + ",\"server_configuration\":\"UNVERIFIED\",\"special_instance_id\":" +
                (special == null ? 0 : special.GetInstanceID()) + ",\"formation_instance_id\":" + (formation == null ? 0 : formation.GetInstanceID()) + ",\"session_key\":" + Command.Quote(sessionKey) +
                ",\"reason\":" + Command.Quote(reason) + ",\"network_observed\":" + (networkObserved ? "true" : "false") + ",\"network_pending\":" + (networkPending ? "true" : "false") + "}";
        }
        private static bool OwnedBy(Component c, Component owner)
        { return c != null && owner != null && owner.gameObject.activeInHierarchy && object.ReferenceEquals(Read(c, "rmg"), owner) && c.transform.root == owner.transform; }
        private static bool Visible(Component c) { Behaviour b = c as Behaviour; return b != null && b.enabled && b.gameObject.activeInHierarchy; }
        private static bool Enabled(Component c) { return Visible(c) && Bool(Method(c.GetType(), "IsEnable", Type.EmptyTypes, false, typeof(bool)).Invoke(c, new object[0])); }
        private static int[] Values(IList list) { int[] result = new int[list.Count]; for (int i = 0; i < result.Length; i++) result[i] = Int(list[i]); return result; }
        private static void MapValues(IDictionary map, out object[] keys, out object[] values)
        {
            keys = new object[map.Count]; values = new object[map.Count]; int index = 0;
            foreach (DictionaryEntry entry in map) { if (index >= keys.Length) throw new ProbeFailure("general_map_changed"); keys[index] = entry.Key; values[index] = entry.Value; index++; }
            if (index != keys.Length) throw new ProbeFailure("general_map_changed");
        }
        private static bool SameMap(IDictionary map, object[] keys, object[] values)
        { if (keys == null || values == null || map.Count != keys.Length) return false; for (int i = 0; i < keys.Length; i++) if (!map.Contains(keys[i]) || !object.ReferenceEquals(map[keys[i]], values[i])) return false; return true; }
        private static bool SameValues(IList list, int[] values) { if (list == null || values == null || list.Count != values.Length) return false; for (int i = 0; i < values.Length; i++) if (Int(list[i]) != values[i]) return false; return true; }
        private static MethodInfo Method(Type type, string name, Type[] parameters, bool isStatic, Type result)
        { for (Type t = type; t != null; t = t.BaseType) { MethodInfo m = t.GetMethod(name, isStatic ? Static : Instance, null, parameters, null); if (m != null && !m.IsGenericMethod && m.ReturnType == result) return m; } throw new ProbeFailure("general_method_schema_" + name); }
        private static object Read(object target, string name)
        { if (target == null) throw new ProbeFailure("general_null_" + name); for (Type t = target.GetType(); t != null; t = t.BaseType) { FieldInfo f = t.GetField(name, Instance); if (f != null) return f.GetValue(target); } throw new ProbeFailure("general_field_schema_" + name); }
        private static object ReadStatic(Type type, string name) { FieldInfo f = type.GetField(name, Static); if (f == null) throw new ProbeFailure("general_static_schema_" + name); return f.GetValue(null); }
        private static int Int(object value) { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
        private static uint UInt(object value) { return Convert.ToUInt32(value, CultureInfo.InvariantCulture); }
        private static bool Bool(object value) { if (!(value is bool)) throw new ProbeFailure("general_bool_schema"); return (bool)value; }
    }
}
