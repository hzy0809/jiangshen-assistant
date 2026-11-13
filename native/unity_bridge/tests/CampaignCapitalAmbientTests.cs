using System;
using System.Collections.Generic;
using System.Reflection;
using JiangShen.AssistantRuntime;

// Exercises the real SelectContext/EvaluateContext admission path of the
// production CampaignModel and the production strict identity helper. The
// test injects the same pre-observation character identity snapshot that the
// existing detached campaign fixtures use, because their stand-in character
// cannot carry the installed pbdata field's pinned metadata token.
internal static class CampaignCapitalAmbientTests
{
    private static int checks;
    private static void Check(bool value, string why)
    { checks++; if (!value) throw new Exception(why); }
    private static string Q(string value) { return Command.Quote(value); }

    private sealed class World
    {
        internal readonly RootMessenger Capital = new RootMessenger { LevelID = "Capital" };
        internal readonly RootMessenger GlobalRoot = new RootMessenger { LevelID = "Root" };
        internal readonly RootUI GlobalUi;
        internal readonly MainUI Main;
        internal readonly CapitalUI Parent;
        internal readonly Database.Character Character = new Database.Character();
        internal readonly Database.Demon Demon = new Database.Demon();
        internal readonly DemonScript Icon;
        internal readonly CampaignModel Runtime;
        internal readonly List<UnityEngine.Object> All = new List<UnityEngine.Object>();

        private static T Attach<T>(T component, RootMessenger owner, UnityEngine.GameObject game = null) where T : StubUi
        {
            component.gameObject = game ?? new UnityEngine.GameObject(); component.rmg = owner;
            component.gameObject.transform.root = owner.transform; component.gameObject.components.Add(component);
            return component;
        }
        internal World(bool challenge)
        {
            Global.extern_enabled = true; Global.character_ = Character;
            MainUI.m_subui = null; RootUI.m_client = new QueueClient(); Global.m_client = RootUI.m_client;
            GlobalUi = Attach(new RootUI(), GlobalRoot); BaseUI.rootUI = GlobalUi;
            Main = Attach(new MainUI(), Capital);
            Parent = Attach(new CapitalUI { enabled = false }, Capital);
            Demon.mesh = new UnityEngine.GameObject(); Demon.mesh.transform.root = Capital.transform;
            Icon = Attach(new DemonScript { m_demon = Demon, ParentUI = Parent }, Capital, Demon.mesh);
            Character.m_demon = Demon;
            Model.ContextRoot = Capital; Model.GlobalObject = GlobalUi.gameObject; Model.BattleObject = null;
            All.Add(GlobalUi); All.Add(Main); All.Add(Icon);
            Runtime = new CampaignModel(new Model(), challenge);
            Set("common", new Snapshot { Main = Main, Root = Capital, Character = Character });
            Set("characterId", "42");
        }
        private void Set(string name, object value)
        { typeof(CampaignModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(Runtime, value); }
        internal void AddOverlay(StubUi overlay)
        { All.Add(Attach(overlay, Capital)); }
        internal void AddSameMeshDuplicate()
        { All.Add(Attach(new DemonScript { m_demon = Demon, ParentUI = Parent }, Capital, Demon.mesh)); }
        internal void AddForeignDemon()
        {
            Database.Demon stranger = new Database.Demon { mesh = new UnityEngine.GameObject() };
            stranger.mesh.transform.root = Capital.transform;
            All.Add(Attach(new DemonScript { m_demon = stranger, ParentUI = Parent }, Capital, stranger.mesh));
        }
        internal void Observe()
        {
            UnityEngine.Object[] all = All.ToArray(); Model.Objects = all;
            Runtime.SelectContext(all, Model.ContextRoot); Runtime.EvaluateContext(all);
        }
        internal string Flow()
        { return (string)typeof(CampaignModel).GetMethod("FlowJson", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(Runtime, new object[0]); }
    }

    private static void Expect(bool challenge, string variant)
    {
        World w = new World(challenge);
        if (variant == "friend") w.Icon.m_bFriend = true;
        else if (variant == "mesh_wrong_root") w.Demon.mesh.transform.root = new RootMessenger { LevelID = "Capital" }.transform;
        else if (variant == "icon_wrong_demon") w.Icon.m_demon = new Database.Demon();
        else if (variant == "parent_wrong_owner") w.Parent.rmg = new RootMessenger { LevelID = "Capital" };
        else if (variant == "parent_wrong_root") w.Parent.gameObject.transform.root = new RootMessenger { LevelID = "Capital" }.transform;
        else if (variant == "duplicate") w.AddSameMeshDuplicate();
        else if (variant == "unrelated_demon") w.AddForeignDemon();
        else if (variant == "unknown_overlay") w.AddOverlay(new UnknownOverlay());
        else if (variant == "consume_overlay") w.AddOverlay(new ConsumeUI());
        else if (variant == "stale_character") Global.character_ = new Database.Character();
        else if (variant == "stale_root") Model.ContextRoot = new RootMessenger { LevelID = "Capital" };
        else if (variant == "not_capital") w.Capital.LevelID = "Other";
        w.Observe();
        bool positive = variant == "own";
        if (positive)
            Check(w.Runtime.Available && w.Runtime.Gate == "PASS" && w.Runtime.Phase == "MAIN" &&
                w.Flow().Contains("\"status\":\"IDLE\""), "owned capital icon must not block the native entry admission");
        else
            Check(!w.Runtime.Available && w.Runtime.Gate.StartsWith("campaign_") &&
                w.Flow().Contains("\"status\":\"UNSUPPORTED\""), "untrusted icon/overlay must remain blocked: " + w.Runtime.Gate);
        Check(w.Runtime.Observer.Status == "NONE" && !w.Runtime.Observer.MutationAttempted && w.Main.Calls == 0,
            "admission observation must not enter, launch, send, or settle a battle");
    }
    private static int Main()
    {
        string[] variants = { "own", "friend", "mesh_wrong_root", "icon_wrong_demon", "parent_wrong_owner",
            "parent_wrong_root", "duplicate", "unrelated_demon", "unknown_overlay", "consume_overlay", "stale_character", "stale_root", "not_capital" };
        int failed = 0;
        for (int mode = 0; mode < 2; mode++) foreach (string variant in variants)
        {
            int before = checks; string name = (mode == 0 ? "campaign_" : "challenge_") + variant;
            try { Expect(mode == 1, variant); Console.WriteLine("{\"scenario\":" + Q(name) + ",\"status\":\"PASS\",\"checks\":" + (checks - before) + "}"); }
            catch (Exception error)
            {
                failed++;
                while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
                Console.WriteLine("{\"scenario\":" + Q(name) + ",\"status\":\"FAIL\",\"error\":" + Q(error.ToString()) + "}");
            }
        }
        Console.WriteLine("{\"status\":" + Q(failed == 0 ? "PASS" : "FAIL") + ",\"scenarios\":" + (variants.Length * 2) +
            ",\"failed\":" + failed + ",\"checks\":" + checks + ",\"game_access\":false}");
        return failed == 0 ? 0 : 1;
    }
}
