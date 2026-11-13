using System;
using System.Collections;
using System.Reflection;
using System.Threading;
using JiangShen.AssistantRuntime;

// Owned fixtures for the production GeneralTutorialModel; no game assembly or
// process is loaded. Fixture callbacks deliberately retain async handlers.
public class SpecialGetUI : StubUi
{
    public string showtype = "general"; public uint tempid = 104; public bool showFormClose = true;
    public int HideCalls;
    public new void Hide() { HideCalls++; base.Hide(); }
}
public class BaseUI : StubUi { public static RootUI rootUI; }
public static class RootBase
{
    public enum enumGeneralAutoEquipMode { Attack, Defense, Spell, Unequip }
    public static int EquipCalls;
    public static bool ThrowEquip;
    public static void cs_general_auto_equip(int id, enumGeneralAutoEquipMode mode)
    {
        if (mode != enumGeneralAutoEquipMode.Attack || !((CharacterData)Global.character_).form_generals_.ContainsKey((ushort)id)) throw new Exception("equip_args");
        EquipCalls++; if (ThrowEquip) throw new Exception("fixture_partial_equip");
        Global.m_client.m_handlers[(ushort)771] = new object();
    }
}
public static class DelegateFunc { public static Action<bool> MainUI_formation_ui = DelegateFuncUI.MainUI_formation_ui; }
public static class DelegateFuncUI
{
    public static FormationUI Target;
    public static void MainUI_formation_ui(bool showClose)
    {
        Target.DefaultState = "Formation"; Target.substate = ""; Target.viewonly = true; Target.RightPanel = "GenInfo";
        Target.isVisible = Target.enabled = true; Target.OnClose = null; Target.showCloseTip = showClose;
        Target.m_lastparam = new Hashtable(); Target.m_lastparam["subui"] = "Formation";
        MainUI.m_subui = Target;
        TutorialFixture.Form form = (TutorialFixture.Form)((CharacterData)Global.character_).formations_[1];
        Target.CharFormation = new TutorialFixture.FormInfo(form);
    }
}
public partial class FormationUI
{
    public string RightPanel = "Enemy";
    public bool showCloseTip;
    public Action OnClose;
    public TutorialFixture.FormInfo CharFormation;
    public Hashtable m_lastparam;
    public int CloseCalls;
    private void OnClickClose()
    {
        CloseCalls++; OnClickSetCurrentForm();
        RootUI.m_client.m_handlers[(ushort)298] = new object();
        isVisible = false; enabled = false; MainUI.m_subui = null;
    }
}
public class TutorialFixture
{
    public class General { public ushort idgeneral_template = 104; }
    public class Form { public int id = 1, spotnum = 2; public ArrayList spotgen = new ArrayList(new object[] { 0, -1, -1, -1, -1, -1, -1, -1 }); }
    public struct Position { public long genid; }
    public class FormInfo
    {
        public int formid = 1; public ArrayList dbspot = new ArrayList(); public Position[,] formgenpos = new Position[4,4];
        public FormInfo(Form form)
        {
            for (int i = 0; i < 16; i++) formgenpos[i / 4, i % 4].genid = -2;
            for (int i = 0; i < 8; i++) { dbspot.Add(i); formgenpos[i / 4, i % 4].genid = (int)form.spotgen[i]; }
        }
    }
    internal RootMessenger Root = new RootMessenger { LevelID = "Campaign" }, GlobalRoot = new RootMessenger { LevelID = "Root" };
    internal CharacterData Character = new CharacterData();
    internal RootUI GlobalUi;
    internal CampaignUI Campaign;
    internal MainUI Main;
    internal SpecialGetUI Special;
    internal FormationUI Formation;
    internal CampaignModel Runtime = new CampaignModel(new Model());
    internal Form Data = new Form();
    internal UnityEngine.Object[] Objects;
    internal TutorialFixture()
    {
        RootUI.m_client = new QueueClient(); Global.m_client = RootUI.m_client; Global.character_ = Character; Global.extern_enabled = true;
        MainUI.m_subui = null; RootBase.EquipCalls = 0; RootBase.ThrowEquip = false; DelegateFunc.MainUI_formation_ui = DelegateFuncUI.MainUI_formation_ui;
        UnityEngine.GUI.enabled = true; UnityEngine.Event.current = new UnityEngine.Event();
        GlobalUi = Attach(new RootUI(), GlobalRoot, new UnityEngine.GameObject());
        BaseUI.rootUI = GlobalUi;
        Main = Attach(new MainUI(), Root, null); Campaign = Attach(new CampaignUI(), Root, null);
        Special = Attach(new SpecialGetUI(), GlobalRoot, GlobalUi.gameObject);
        Formation = Attach(new FormationUI { enabled = false, isVisible = false }, GlobalRoot, GlobalUi.gameObject);
        Character.form_generals_[(ushort)104] = new General(); Character.formations_[1] = Data;
        DelegateFuncUI.Target = Formation;
        Set("common", new Snapshot { Main = Main, Root = Root, Character = Character }); Set("characterId", "42");
        Set("campaignId", 12); Set("troopId", 7); Runtime.Revision = 1;
        Objects = new UnityEngine.Object[] { GlobalUi, Main, Campaign, Special, Formation }; Observe();
    }
    internal static T Attach<T>(T ui, RootMessenger owner, UnityEngine.GameObject game) where T : StubUi
    { ui.rmg = owner; ui.gameObject = game ?? new UnityEngine.GameObject(); ui.transform.root = owner.transform; return ui; }
    internal void Set(string name, object value) { typeof(CampaignModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(Runtime, value); }
    internal void Observe()
    {
        Model.ContextRoot = Root; Model.GlobalObject = GlobalUi.gameObject; Model.Objects = Objects;
        Runtime.SelectContext(Objects, Root); Runtime.EvaluateContext(Objects);
    }
    internal Command Cmd(string action) { return new Command { Name = "campaign-command", CampaignAction = action, Sequence = action == "general_deploy" ? 2 : 3, ExpectedRevision = 1 }; }
    internal string Json() { return Runtime.Json(); }
    internal string Key()
    { string json = Json(), marker = "\"session_key\":\""; int start = json.IndexOf(marker) + marker.Length; return json.Substring(start, json.IndexOf('"', start) - start); }
    internal bool Invoke(string action)
    { string why; Command c = Cmd(action); if (!Runtime.QueueGui(c, out why)) throw new Exception("queue:" + why); return Runtime.ExecuteGui(c, delegate {}, out why); }
    internal void Deployed()
    { if (!Invoke("general_deploy")) throw new Exception("fixture_deploy"); RootUI.m_client.m_handlers.Clear(); Observe(); }
    internal void Confirmed()
    { Deployed(); if (!Invoke("formation_confirm")) throw new Exception("fixture_confirm"); RootUI.m_client.m_handlers.Clear(); Formation.waitstate = 0; Observe(); }
}
internal static class GeneralTutorialTests
{
    private static int checks;
    private static void Check(bool okay, string name) { if (!okay) throw new Exception(name); checks++; }
    private static void Changed(string name, Action<TutorialFixture> change, bool confirm)
    {
        TutorialFixture w = new TutorialFixture(); if (confirm) w.Deployed();
        string why; Command command = w.Cmd(confirm ? "formation_confirm" : "general_deploy");
        Check(w.Runtime.QueueGui(command, out why), "general_queue_" + name); change(w);
        int calls = w.Character.AddCalls, equip = RootBase.EquipCalls, close = w.Formation.CloseCalls, published = 0;
        Check(!w.Runtime.ExecuteGui(command, delegate { published++; }, out why) && w.Character.AddCalls == calls && RootBase.EquipCalls == equip && w.Formation.CloseCalls == close && published == 0, "general_changed_" + name);
    }
    internal static int Run()
    {
        string why;
        foreach (string action in new string[] { "general_deploy", "formation_confirm" })
        {
            string wire = "version=3\nnonce=" + new string('a', 32) + "\nsequence=4\ncommand=campaign-command\naction=" + action + "\nexpected_revision=1\ncampaign_id=0\ntroop_id=0\nattempt_id=\n";
            Check(Command.Parse(wire, new string('a', 32), 3) != null, "general_fixed_wire_" + action);
            Check(Command.Parse(wire.Replace("campaign_id=0", "campaign_id=12"), new string('a', 32), 3) == null, "general_no_campaign_parameter");
            Check(Command.Parse(wire.Replace("troop_id=0", "troop_id=7"), new string('a', 32), 3) == null, "general_no_troop_parameter");
            Check(Command.Parse(wire.Replace("attempt_id=", "attempt_id=" + new string('b', 32)), new string('a', 32), 3) == null, "general_no_attempt_parameter");
        }
        TutorialFixture w = new TutorialFixture();
        Check(w.Runtime.Available && w.Runtime.Phase == "GENERAL_TUTORIAL" && w.Json().Contains("\"status\":\"DEPLOY\""), "general_exact_popup_admitted");
        Check(!w.Runtime.Execute(w.Cmd("general_deploy"), delegate {}, out why) && w.Character.AddCalls == 0, "general_update_cannot_invoke");
        Check(!w.Runtime.Execute(w.Cmd("start"), delegate {}, out why) && w.Runtime.Observer.Status == "NONE", "general_not_combat");
        Command deploy = w.Cmd("general_deploy"); Check(w.Runtime.QueueGui(deploy, out why), "general_queued");
        Check(!w.Runtime.QueueGui(deploy, out why), "general_single_queued_command");
        int published = 0;
        Check(w.Runtime.ExecuteGui(deploy, delegate { published++; Check(w.Character.AddCalls == 0 && RootBase.EquipCalls == 0 && w.Special.HideCalls == 0 && !w.Formation.enabled, "general_publish_before_all_calls"); Check(w.Json().Contains("WAITING_FORMATION"), "general_published_ownership"); }, out why), "general_branch_invoked");
        Check(published == 1 && w.Character.AddCalls == 1 && RootBase.EquipCalls == 1 && w.Special.HideCalls == 1 && w.Formation.enabled && MainUI.m_subui == w.Formation, "general_normal_branch_order_and_open");
        w.Observe(); Check(w.Json().Contains("\"status\":\"WAITING_FORMATION\"") && w.Json().Contains("WAITING_NETWORK"), "general_equip_handler_must_finish");
        Check(!w.Runtime.QueueGui(w.Cmd("formation_confirm"), out why), "general_cannot_override_equip_handler");
        RootUI.m_client.m_handlers.Clear(); w.Observe();
        Check(w.Json().Contains("\"status\":\"FORMATION_CONFIRM\"") && w.Json().Contains(":confirm"), "general_owned_formation_ready");
        Check(!w.Runtime.QueueGui(w.Cmd("general_deploy"), out why) && w.Character.AddCalls == 1, "general_deploy_exactly_once");
        Check(w.Invoke("formation_confirm") && w.Formation.CloseCalls == 1 && !w.Formation.enabled && !w.Formation.isVisible && MainUI.m_subui == null, "general_normal_save_close");
        w.Observe(); Check(w.Json().Contains("WAITING_CLOSE") && w.Json().Contains(":confirm") && w.Runtime.Observer.Status == "NONE", "general_save_wait_keeps_same_step_no_combat");
        Check(w.Runtime.BusinessJson().Contains("\"character_id\":\"42\"") && w.Runtime.BusinessJson().Contains("\"mutation_attempted\":true") && w.Runtime.BusinessJson().Contains("\"pending_attempt\":false") && w.Runtime.Observer.AttemptId == "", "general_only_stop_business_mutation_truthful_without_combat_attempt");
        RootUI.m_client.m_handlers.Clear(); w.Formation.waitstate = 0; w.Observe();
        Check(w.Runtime.Phase == "CAMPAIGN" && w.Json().Contains("\"status\":\"COMPLETE\"") && w.Json().Contains("\"local_observation\":\"CLOSED\"") && w.Json().Contains("\"server_configuration\":\"UNVERIFIED\""), "general_local_complete_is_not_backend_proof");
        Check(!w.Runtime.QueueGui(w.Cmd("formation_confirm"), out why) && w.Formation.CloseCalls == 1, "general_confirm_not_replayed");
        string priorStep = w.Key();
        w.Special.visible_time = w.Special.visible_time.AddTicks(1); w.Special.enabled = true; w.Observe();
        Check(w.Runtime.Available && w.Key() != priorStep && w.Json().Contains(":deploy") && w.Invoke("general_deploy"), "general_reused_ui_new_visible_instance_can_teach_again");
        w = new TutorialFixture(); w.Confirmed();
        w.Special.enabled = true; w.Observe(); Check(w.Json().Contains("general_popup_replayed"), "general_same_popup_replay_unknown");
        w = new TutorialFixture(); w.Data.spotgen[1] = 104; w.Deployed(); Check(w.Character.AddCalls == 1 && RootBase.EquipCalls == 1, "general_already_deployed_still_normal_equip");
        w = new TutorialFixture(); w.Special.tempid = 105; w.Special.showFormClose = false; w.Character.form_generals_.Clear(); w.Character.form_generals_[(ushort)105] = new TutorialFixture.General { idgeneral_template = 105 }; w.Observe();
        Check(w.Invoke("general_deploy") && w.Data.spotgen.Contains(105) && !w.Formation.showCloseTip, "general_future_owned_general_uses_its_real_popup_id");
        foreach (string failure in new string[] { "showtype", "zero_id", "wide_id", "owned_missing", "owned_id", "full", "duplicate", "wrong_owner", "wrong_root", "show_close", "client" })
        {
            w = new TutorialFixture();
            if (failure == "showtype") w.Special.showtype = "artifact";
            if (failure == "zero_id") w.Special.tempid = 0;
            if (failure == "wide_id") w.Special.tempid = 65536;
            if (failure == "owned_missing") w.Character.form_generals_.Clear();
            if (failure == "owned_id") ((TutorialFixture.General)w.Character.form_generals_[(ushort)104]).idgeneral_template = 105;
            if (failure == "full") w.Data.spotgen[1] = 5;
            if (failure == "duplicate") w.Objects = new UnityEngine.Object[] { w.GlobalUi, w.Main, w.Campaign, w.Special, w.Formation, TutorialFixture.Attach(new SpecialGetUI(), w.GlobalRoot, w.GlobalUi.gameObject) };
            if (failure == "wrong_owner") w.Special.rmg = w.Root;
            if (failure == "wrong_root") w.Root.LevelID = "Capital";
            if (failure == "show_close") w.Special.showFormClose = false;
            if (failure == "client") Global.m_client = new QueueClient();
            w.Observe(); Check(!w.Runtime.QueueGui(w.Cmd("general_deploy"), out why) && w.Character.AddCalls == 0, "general_admission_reject_" + failure);
        }
        Changed("disabled", delegate(TutorialFixture x) { x.Special.interaction = false; }, false);
        Changed("global_disabled", delegate(TutorialFixture x) { Global.extern_enabled = false; }, false);
        Changed("id", delegate(TutorialFixture x) { x.Special.tempid = 105; }, false);
        Changed("type", delegate(TutorialFixture x) { x.Special.showtype = "skill"; }, false);
        Changed("role", delegate(TutorialFixture x) { Global.character_ = new CharacterData(); }, false);
        Changed("root", delegate(TutorialFixture x) { Model.ContextRoot = new RootMessenger(); }, false);
        Changed("general_object", delegate(TutorialFixture x) { x.Character.form_generals_[(ushort)104] = new TutorialFixture.General(); }, false);
        Changed("generals_list", delegate(TutorialFixture x) { x.Character.form_generals_ = new Hashtable(x.Character.form_generals_); }, false);
        Changed("general_count", delegate(TutorialFixture x) { x.Character.form_generals_[(ushort)105] = new TutorialFixture.General(); }, false);
        Changed("current_form", delegate(TutorialFixture x) { x.Character.cur_form = 2; }, false);
        Changed("spots", delegate(TutorialFixture x) { x.Data.spotgen[2] = 3; }, false);
        Changed("revision", delegate(TutorialFixture x) { x.Runtime.Revision++; }, false);
        Changed("gui_disabled", delegate(TutorialFixture x) { UnityEngine.GUI.enabled = false; }, false);
        Changed("not_gui", delegate(TutorialFixture x) { UnityEngine.Event.current = null; }, false);
        Changed("network_handler", delegate(TutorialFixture x) { RootUI.m_client.m_handlers[(ushort)298] = new object(); }, false);
        Changed("network_message", delegate(TutorialFixture x) { RootUI.m_client.messages.Add(new QueueMessage { op = 771 }); }, false);
        Changed("cached_root", delegate(TutorialFixture x) { BaseUI.rootUI = new RootUI(); }, false);
        Changed("formation_state", delegate(TutorialFixture x) { x.Formation.DefaultState = "NPCBattle"; }, true);
        Changed("formation_viewonly", delegate(TutorialFixture x) { x.Formation.viewonly = false; }, true);
        Changed("formation_callback", delegate(TutorialFixture x) { x.Formation.OnClose = delegate {}; }, true);
        Changed("formation_data", delegate(TutorialFixture x) { x.Formation.CharFormation.formgenpos[0,1].genid = 0; }, true);
        Changed("formation_list", delegate(TutorialFixture x) { x.Formation.CharFormation.dbspot[1] = 5; }, true);
        Changed("formation_current", delegate(TutorialFixture x) { x.Formation.CharFormation.formid = 2; }, true);
        Changed("formation_parameters", delegate(TutorialFixture x) { x.Formation.m_lastparam = new Hashtable(x.Formation.m_lastparam); }, true);
        Changed("formation_reopen_time", delegate(TutorialFixture x) { x.Formation.visible_time = x.Formation.visible_time.AddTicks(1); }, true);
        w = new TutorialFixture(); w.Character.form_generals_[(ushort)105] = new TutorialFixture.General { idgeneral_template = 105 }; w.Observe(); deploy = w.Cmd("general_deploy");
        Check(w.Runtime.QueueGui(deploy, out why), "general_other_member_queue"); w.Character.form_generals_[(ushort)105] = new TutorialFixture.General { idgeneral_template = 105 };
        Check(!w.Runtime.ExecuteGui(deploy, delegate {}, out why) && w.Character.AddCalls == 0, "general_same_count_other_owned_member_changed");
        w = new TutorialFixture(); deploy = w.Cmd("general_deploy"); Check(w.Runtime.QueueGui(deploy, out why), "general_cancel_queue"); w.Runtime.GuiNotInvoked(deploy, "cancel");
        Check(!w.Runtime.ExecuteGui(deploy, delegate {}, out why) && w.Character.AddCalls == 0, "general_cancel_before_gui_no_mutation");
        w = new TutorialFixture(); deploy = w.Cmd("general_deploy"); Check(w.Runtime.QueueGui(deploy, out why), "general_cancel_publication_queue");
        try { w.Runtime.ExecuteGui(deploy, delegate { throw new OperationCanceledException(); }, out why); } catch (OperationCanceledException) { w.Runtime.GuiNotInvoked(deploy, "cancel_before_invoke"); }
        Check(w.Character.AddCalls == 0 && RootBase.EquipCalls == 0 && w.Json().Contains("\"status\":\"DEPLOY\"") && w.Runtime.BusinessJson().Contains("\"mutation_attempted\":false"), "general_cancel_in_publication_no_mutation");
        w = new TutorialFixture(); RootBase.ThrowEquip = true; deploy = w.Cmd("general_deploy"); Check(w.Runtime.QueueGui(deploy, out why), "general_partial_queue");
        try { w.Runtime.ExecuteGui(deploy, delegate {}, out why); } catch (TargetInvocationException) { w.Runtime.GuiFailed(deploy, true, "partial"); }
        Check(w.Character.AddCalls == 1 && w.Json().Contains("\"status\":\"UNKNOWN\"") && !w.Runtime.QueueGui(deploy, out why) && w.Runtime.BusinessJson().Contains("\"mutation_attempted\":true"), "general_partial_failure_never_retries");
        w = new TutorialFixture(); w.Deployed(); Check(w.Invoke("formation_confirm"), "general_no_response_confirm"); RootUI.m_client.m_handlers.Clear(); w.Observe();
        Check(w.Json().Contains("general_confirmation_not_observed") && w.Runtime.Phase == "GENERAL_TUTORIAL", "general_handler_timeout_not_success");
        w = new TutorialFixture(); w.Deployed(); w.Formation.m_lastparam = new Hashtable(w.Formation.m_lastparam); w.Observe();
        Check(w.Json().Contains("general_formation_context_changed") && !w.Runtime.QueueGui(w.Cmd("formation_confirm"), out why), "general_unrelated_reopen_not_owned");
        w = new TutorialFixture(); Check(w.Invoke("general_deploy"), "general_async_open_invoked"); w.Formation.isLoading = true; w.Formation.enabled = false; Global.extern_enabled = false; w.Observe();
        Check(w.Json().Contains("WAITING_FORMATION") && w.Json().Contains("WAITING_UI") && w.Json().Contains(":deploy"), "general_async_loading_keeps_deploy_pending");
        w.Formation.isLoading = false; w.Formation.enabled = true; Global.extern_enabled = true; RootUI.m_client.m_handlers.Clear(); w.Observe();
        Check(w.Json().Contains("FORMATION_CONFIRM") && w.Json().Contains(":confirm"), "general_async_open_ready_advances_phase");
        w = new TutorialFixture(); Check(w.Invoke("general_deploy"), "general_postcondition_fixture_invoked"); RootUI.m_client.m_handlers.Clear(); w.Formation.CharFormation.formgenpos[0,1].genid = 3; w.Observe();
        Check(w.Json().Contains("general_formation_postcondition_missing") && !w.Runtime.QueueGui(w.Cmd("formation_confirm"), out why), "general_missing_formation_postcondition_unknown");
        w = new TutorialFixture(); RootUI.m_client.m_handlers[(ushort)298] = new object(); w.Observe();
        Check(w.Json().Contains("WAITING_NETWORK") && !w.Runtime.QueueGui(w.Cmd("general_deploy"), out why), "general_old_formation_handler_must_finish_before_add");
        w = new TutorialFixture(); ManualResetEvent locked = new ManualResetEvent(false), release = new ManualResetEvent(false);
        Thread thread = new Thread(delegate() { lock (RootUI.m_client) { locked.Set(); release.WaitOne(); } }); thread.Start(); locked.WaitOne();
        try { w.Observe(); Check(w.Json().Contains("\"network_observed\":false") && !w.Runtime.QueueGui(w.Cmd("general_deploy"), out why), "general_lock_miss_is_wait_not_empty"); }
        finally { release.Set(); thread.Join(); locked.Close(); release.Close(); }
        Console.WriteLine("{\"general_tutorial_checks\":" + checks + ",\"status\":\"PASS\",\"game_access\":false}");
        return checks;
    }
}
