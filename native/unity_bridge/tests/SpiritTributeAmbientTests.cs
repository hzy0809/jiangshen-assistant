using System;
using System.Collections.Generic;
using JiangShen.AssistantRuntime;
using UnityEngine;

internal sealed class AmbientFixture
{
    internal readonly RootUI root, uiRoot, globalUi;
    internal readonly SpiritUI ui;
    internal readonly SpiritUI_2 parent2;
    internal readonly SpiritUI_3 parent3;
    internal readonly SpiritRankupUI rankup;
    internal readonly CapitalUI capital;
    internal readonly DemonScript script;
    internal readonly Database.Character character = new Database.Character();
    internal readonly Database.Demon demon = new Database.Demon();
    internal readonly FixtureClient client = new FixtureClient();
    internal readonly List<UnityEngine.Object> all = new List<UnityEngine.Object>();
    internal readonly SpiritTributeModel model = new SpiritTributeModel();
    internal AmbientFixture()
    {
        root = Attach(new RootUI(), null, new GameObject()); root.rmg = root;
        uiRoot = Attach(new RootUI(), null, new GameObject()); uiRoot.rmg = uiRoot;
        globalUi = Attach(new RootUI(), uiRoot, uiRoot.gameObject);
        ui = Attach(new SpiritUI(), uiRoot, uiRoot.gameObject);
        parent2 = Attach(new SpiritUI_2(), uiRoot, uiRoot.gameObject);
        parent3 = Attach(new SpiritUI_3(), uiRoot, uiRoot.gameObject);
        rankup = Attach(new SpiritRankupUI(), uiRoot, uiRoot.gameObject);
        capital = Attach(new CapitalUI(), root, new GameObject()); capital.transform.root = root.transform;
        demon.mesh = new GameObject(); demon.mesh.transform.root = root.transform;
        script = Attach(new DemonScript(), root, demon.mesh); script.visible = true; script.ParentUI = capital; script.m_demon = demon;
        character.m_demon = demon;
        Global.character_ = character; RootUI.m_client = Global.m_client = client; BaseUI.rootUI = globalUi; Global.extern_enabled = true;
        MainUI.m_subui = ui; SpiritUI.my_Subui = null; SpiritUI_2.my_Subui = null; SpiritUI_3.my_Subui = null;
        ui.visible = true; ui.m_spirit = character.spirit;
        all.AddRange(new UnityEngine.Object[] { root, uiRoot, globalUi, ui, parent2, parent3, rankup, capital, script });
    }
    internal static T Attach<T>(T c, Component owner, GameObject gameObject) where T : BaseAsset
    { c.gameObject = gameObject; c.rmg = owner; gameObject.components.Add(c); return c; }
    internal SharedDevelopmentContext Context()
    { return new SharedDevelopmentContext(all.ToArray(), root, globalUi, character, "42", "PASS", false, delegate(Behaviour b) { return "unknown:" + b.GetType().Name; }); }
    internal DevelopmentCommand Command(string action)
    { return new DevelopmentCommand { Action = action, Sequence = 1, ExpectedRevision = 1, OperationId = new string('a', 32), Mode = "fruit", MaxRequests = 2, MaxFood = 5, DesiredLevel = 11 }; }
    internal string Capture()
    { model.ObserveTarget(Command("tribute_train")); model.Capture(Context()); return model.Json(); }
    internal void AddOverlay(__BaseUI overlay)
    { Attach(overlay, uiRoot, uiRoot.gameObject); overlay.visible = true; all.Add(overlay); }
}
internal static class SpiritTributeAmbientTests
{
    static int checks;
    static void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL " + name); checks++; Console.WriteLine("PASS " + name); }
    static bool Field(string json, string field, string value) { return json.Contains("\"" + field + "\":\"" + value + "\""); }
    static void AssertObservation(AmbientFixture f, string name, bool pass)
    {
        string json = f.Capture();
        Check(Field(json, "gate", pass ? "PASS" : "BLOCKED"), name + "_gate");
        Check(Field(json, "available_action", pass ? "tribute_train" : ""), name + "_action");
        Check(f.ui.TrainCalls == 0 && f.client.Sends == 0 && f.character.spirit_food == 5 && f.character.gold == 7 && !f.model.MutationAttempted, name + "_observation_is_read_only");
    }
    static void Run()
    {
        AmbientFixture f = new AmbientFixture();
        Check(object.ReferenceEquals(f.Context().FindCapitalDemon(), f.script), "own_capital_demon_exact_identity");
        AssertObservation(f, "own_demon", true);
        string why; Check(f.model.Queue(f.Command("tribute_close"), out why), "own_demon_close_allowed");
        f = new AmbientFixture(); f.script.m_bFriend = true; Check(f.Context().FindCapitalDemon() == null, "friend_not_accepted"); AssertObservation(f, "friend", false);
        f = new AmbientFixture(); f.script.m_demon = new Database.Demon(); Check(f.Context().FindCapitalDemon() == null, "wrong_demon_not_accepted"); AssertObservation(f, "wrong_demon", false);
        f = new AmbientFixture(); f.character.m_demon = new object(); Check(f.Context().FindCapitalDemon() == null, "wrong_role_type_not_accepted"); AssertObservation(f, "wrong_role_type", false);
        f = new AmbientFixture(); f.demon.mesh = new GameObject(); f.demon.mesh.transform.root = f.root.transform; Check(f.Context().FindCapitalDemon() == null, "wrong_mesh_not_accepted"); AssertObservation(f, "wrong_mesh", false);
        f = new AmbientFixture(); f.demon.mesh.transform.root = new Transform(); Check(f.Context().FindCapitalDemon() == null, "wrong_mesh_root_not_accepted"); AssertObservation(f, "wrong_mesh_root", false);
        f = new AmbientFixture(); f.capital.rmg = f.uiRoot; Check(f.Context().FindCapitalDemon() == null, "wrong_parent_binding_not_accepted"); AssertObservation(f, "wrong_parent_binding", false);
        f = new AmbientFixture(); f.capital.transform.root = new Transform(); Check(f.Context().FindCapitalDemon() == null, "wrong_parent_root_not_accepted"); AssertObservation(f, "wrong_parent_root", false);
        f = new AmbientFixture(); f.all.Add(f.script); bool duplicateRejected = false;
        try { f.Context().FindCapitalDemon(); } catch (ProbeFailure e) { duplicateRejected = e.Message == "flower_capital_demon_identity"; }
        Check(duplicateRejected, "duplicate_demon_fails_closed");
        f = new AmbientFixture(); f.AddOverlay(new UnknownOverlay()); AssertObservation(f, "unknown_overlay", false);
        Check(!f.model.Queue(f.Command("tribute_close"), out why), "unknown_overlay_blocks_close");
        f = new AmbientFixture(); f.AddOverlay(new ConsumeUI()); AssertObservation(f, "consume_overlay", false);
        Check(!f.model.Queue(f.Command("tribute_close"), out why), "consume_overlay_blocks_close");
        f = new AmbientFixture(); f.ui.visible = false; f.parent2.visible = true; f.parent2.m_spirit = f.character.spirit; MainUI.m_subui = f.parent2;
        for (int i = 0; i < 8; i++) { f.parent2.DragonHead.Add(new DragonDescription()); f.parent2.DragonHead2.Add(new DragonDescription()); }
        Check(Field(f.Capture(), "available_action", "tribute_panel_open"), "own_demon_parent_ready");
        Check(f.model.Queue(f.Command("tribute_parent_close"), out why), "own_demon_parent_close_allowed");
        f = new AmbientFixture(); f.ui.visible = false; f.parent2.visible = true; f.parent2.m_spirit = f.character.spirit; MainUI.m_subui = f.parent2;
        for (int i = 0; i < 8; i++) { f.parent2.DragonHead.Add(new DragonDescription()); f.parent2.DragonHead2.Add(new DragonDescription()); }
        f.AddOverlay(new ConsumeUI()); f.Capture();
        Check(!f.model.Queue(f.Command("tribute_parent_close"), out why), "consume_overlay_blocks_parent_close");
        f = new AmbientFixture(); f.rankup.visible = true; f.rankup.m_spirit = f.character.spirit; f.rankup.OnClose = f.ui.EnableAll; f.Capture();
        var stateField = typeof(SpiritTributeModel).GetField("state", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var canConfirm = typeof(SpiritTributeModel).GetMethod("CanConfirm", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Check((bool)canConfirm.Invoke(null, new object[] { f.Context(), stateField.GetValue(f.model) }), "own_demon_confirm_gate_allowed");
        f.AddOverlay(new ConsumeUI());
        Check(!(bool)canConfirm.Invoke(null, new object[] { f.Context(), stateField.GetValue(f.model) }), "consume_overlay_blocks_confirm_gate");
        Check(f.ui.TrainCalls == 0 && f.client.Sends == 0 && f.character.spirit_food == 5 && f.character.gold == 7, "parent_and_confirm_checks_are_read_only");
        f = new AmbientFixture(); f.ui.food_type = 1; string premium = f.Capture();
        Check(Field(premium, "gate", "BLOCKED") && Field(premium, "reason", "tribute_premium_mode_forbidden") && f.ui.TrainCalls == 0 && f.client.Sends == 0 && f.character.gold == 7, "premium_selector_blocked_without_send");
        Console.WriteLine("PASS total=" + checks);
    }
    static int Main() { try { Run(); return 0; } catch (Exception e) { Console.WriteLine("FAIL " + e.GetType().FullName + ": " + e.Message + " " + e.StackTrace); return 1; } }
}



