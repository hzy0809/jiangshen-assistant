using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using JiangShen.AssistantRuntime;
using D = JiangShen.AssistantRuntime.SharedDevelopmentContext;

namespace UnityEngine
{
    public class Object { public int GetInstanceID() { return 1; } }
    public class Transform { public Transform root { get { return this; } } }
    public class GameObject
    {
        public bool activeInHierarchy = true;
        public Transform transform = new Transform();
        public Component component;
        public static GameObject Battle;
        public static GameObject Find(string path) { return path == "Battle/BattleUI" ? Battle : null; }
        public Component GetComponent(string name) { return component != null && component.GetType().Name == name ? component : null; }
    }
    public class Component : Object
    {
        public GameObject gameObject = new GameObject();
        public Transform transform { get { return gameObject.transform; } }
    }
    public class Behaviour : Component { public bool enabled = true; }
    public static class Time { public static int frameCount; }
    public static class Event { public static object current = new object(); }
    public static class GUI { public static bool enabled = true; }
}

namespace JiangShen.AssistantRuntime
{
    internal sealed class ProbeFailure : Exception { internal ProbeFailure(string why) : base(why) { } }
    internal sealed class Snapshot
    {
        internal UnityEngine.Component Main, Root;
        internal object Character;
        internal string Gate;
        internal bool InteractionEnabled;
    }
    internal sealed class Model
    {
        internal Func<UnityEngine.Object[]> Inventory;
        internal Func<UnityEngine.Component> Root;
        internal UnityEngine.GameObject Owner;
        internal MainUI Main = new MainUI();
        internal Func<string> Gate, Overlay;
        internal Snapshot Capture()
        { return new Snapshot { Main = Main, Root = Root(), Character = Global.character_, Gate = Gate(), InteractionEnabled = Main.interactive }; }
        internal UnityEngine.Object[] AllUi() { return Inventory(); }
        internal UnityEngine.Component CurrentRoot() { return Root(); }
        internal UnityEngine.GameObject GlobalUiObject() { return Owner; }
        internal string OverlayGate(UnityEngine.Behaviour item) { return Overlay(); }
        internal static Type GameType(string name) { return FirstType(name); }
        internal static Type FirstType(string name) { return Type.GetType(name, true); }
    }
}

public class __BaseUI : UnityEngine.Behaviour
{
    public delegate void delegate_OnClose();
    public UnityEngine.Component rmg;
    public bool visible, interactive = true, loading, collecting;
    public delegate_OnClose OnClose;
    public static BattleResultUI battle_result_ui;
    public bool IsVisible() { return visible; }
    public bool IsEnable() { return interactive; }
    public void Hide() { visible = false; }
    public void Disable() { interactive = false; }
}
public class BaseUI : __BaseUI { public static RootUI rootUI; }
public class RootUI : BaseUI
{
    public static game.net.tcp_base m_client;
    public static bool m_bRefreshFive;
    public string LevelID = "Capital";
}
public class MainUI : BaseUI
{
    public bool first, m_bInit = true;
    public static __BaseUI m_subui;
    public static ActivityUI activity;
    public static void _quest_act()
    {
        SetSubUI(activity);
        activity.Open();
    }
    public static void SetSubUI(__BaseUI ui) { if (m_subui != null) m_subui.Hide(); m_subui = ui; }
}
public class CampaignUI : BaseUI { }
public static class BuildingBuildUI { public static bool m_bBuildVisible; }
public static class PopupUI { public static bool popup; }
public static class BuildingBehaviourScript { public static bool m_bEditVisible; }
public class BattleResultUI : __BaseUI
{
    public UnityEngine.Component m_prevUI;
    public bool autoClose;
    public string mine_ui = "";
    public IList m_vctBonuses;
}
public class ActivityUI : BaseUI
{
    public uint reward_select;
    public uint AppliedCategory, AppliedRefresh;
    public bool AppliedFinished, ShowReward = true, Throw, ThrowAfterSend, UpdateCache = true, HoldLoading;
    public int Confirms, Responses;
    public void Open()
    {
        visible = true; interactive = !HoldLoading; loading = HoldLoading;
        foreach (Database.Activity_Reward category in Global.character_.act_rewards.Values) category.Recalculate();
    }
    private void cs_get_reward()
    {
        Global.m_client.Send(356, reward_select, sc_get_reward);
        if (ThrowAfterSend) throw new InvalidOperationException("after stock send");
    }
    private void sc_get_reward(MemoryStream stream, object arg)
    {
        Responses++;
        if (Throw) throw new InvalidOperationException("original callback");
        if (stream == null || stream.ReadByte() != 8 || stream.ReadByte() != 1) { interactive = true; return; }
        if (ShowReward)
        {
            battle_result_ui = (BattleResultUI)UnityEngine.GameObject.Battle.component;
            battle_result_ui.m_prevUI = rmg;
            battle_result_ui.OnClose = OnRecieveReward;
            battle_result_ui.m_vctBonuses = new ArrayList { new object() };
            battle_result_ui.visible = true;
            Global.extern_enabled = false;
        }
        if (UpdateCache)
        {
            Database.Activity_Reward reward = (Database.Activity_Reward)Global.character_.act_rewards[AppliedCategory];
            reward.refresh = AppliedRefresh;
            reward.finished_flag = AppliedFinished;
        }
    }
    private void OnRecieveReward() { Confirms++; interactive = true; Global.extern_enabled = true; }
}
public static class Global
{
    public static Database.Character character_;
    public static game.net.tcp_base m_client;
    public static bool extern_enabled, relogin;
    public static readonly Hashtable ActivityTemplates = new Hashtable(), RewardTemplates = new Hashtable();
    public static Hashtable getTemplates(string table)
    { return table == "activity" ? ActivityTemplates : table == "activity_reward" ? RewardTemplates : null; }
    public static object getTemplate(string table, uint id) { return getTemplates(table)[id]; }
}
public static class TimeUtil { public static uint _now, _thisDay; }

namespace game.net
{
    public class tcp_base
    {
        public delegate void Callback(MemoryStream stream, object arg);
        public sealed class Handler { public Callback handler; public object arg; public float life; }
        public sealed class Message { public ushort op; public byte[] bytes; }
        public static int connection_state = 3;
        public bool m_bConnectLost;
        public readonly Hashtable m_handlers = new Hashtable();
        public readonly ArrayList messages = new ArrayList(), m_sendDatas = new ArrayList();
        public int Sends;
        public uint SentCategory;
        public void Send(ushort opcode, uint category, Callback receiver)
        {
            Sends++;
            SentCategory = category;
            m_handlers[opcode] = new Handler { handler = receiver, life = 10f };
        }
        public void QueueResponse(ushort opcode, byte[] bytes)
        {
            lock (this)
            {
                // Stock Parse accepts packets while the handler is present.
                if (m_handlers.Contains(opcode)) messages.Add(new Message { op = opcode, bytes = bytes });
            }
        }
        public bool DispatchNext()
        {
            lock (this)
            {
                if (messages.Count == 0) return false;
                Message message = (Message)messages[0];
                messages.RemoveAt(0);
                Handler handler = (Handler)m_handlers[message.op];
                if (handler == null) return false;
                if (handler.life > 0) m_handlers.Remove(message.op);
                using (MemoryStream stream = message.bytes == null ? null : new MemoryStream(message.bytes, false))
                    handler.handler(stream, handler.arg);
                return true;
            }
        }
        public void Receive(byte[] bytes) { QueueResponse(356, bytes); DispatchNext(); }
    }
    public sealed class tcp_client : tcp_base
    {
        public sealed class Socket { public bool Connected { get; set; } }
        public Socket m_client = new Socket { Connected = true };
        public Stream m_stream = new MemoryStream();
    }
}

namespace Database
{
    public class Character
    {
        public ulong id = 42;
        public uint next_resource_time = 218000;
        public bool gm, unlocked = true;
        public Hashtable acts = new Hashtable(), act_rewards = new Hashtable();
        public bool checkUI(string feature) { return unlocked && feature == "quest_act"; }
    }
    public class _activity
    {
        public byte id, idreward;
        public int cond_param = 1;
        public bool active = true;
    }
    public class Activity
    {
        public byte idactivity, condition_param = 1;
        public int refresh_time;
        public bool Closed;
        public Action BeforeClosureCheck;
        public _activity temp;
        public bool _check_act_closecondition() { if (BeforeClosureCheck != null) BeforeClosureCheck(); return Closed; }
    }
    public class _activity_reward
    {
        public byte id;
        public bool active = true;
        public IList m_treasures = new ArrayList { new object() };
    }
    public class Activity_Reward
    {
        public byte idact_reward;
        public uint refresh;
        public bool finished_flag = true;
        public _activity_reward temp;
        public readonly Hashtable acts = new Hashtable();
        public int cur_count, total_count;
        public void Recalculate()
        {
            cur_count = total_count = 0;
            foreach (Activity task in acts.Values)
                if (!task.Closed) { cur_count += task.condition_param; total_count += task.temp.cond_param; }
        }
    }
}

internal static class DailyRewardTests
{
    private const string Id = "0123456789abcdef0123456789abcdef", Other = "f123456789abcdef0123456789abcdef";
    private static int checks;
    private static void Check(bool pass, string label) { if (!pass) throw new Exception(label); checks++; }

    private static void UInt(List<byte> bytes, uint value)
    {
        while (value > 127) { bytes.Add((byte)(128 | value & 127)); value >>= 7; }
        bytes.Add((byte)value);
    }

    private static byte[] Accepted(byte category, uint refresh, bool finished)
    {
        List<byte> nested = new List<byte> { 8, category, 16 };
        UInt(nested, refresh);
        if (finished) nested.AddRange(new byte[] { 24, 1 });
        // state=true, one embedded Treasure, one embedded activity reward.
        List<byte> bytes = new List<byte> { 8, 1, 18, 8, 8, 1, 16, 20, 24, 0, 32, 20, 26, (byte)nested.Count };
        bytes.AddRange(nested);
        return bytes.ToArray();
    }

    private sealed class Fixture
    {
        internal readonly Database.Character Role = new Database.Character();
        internal readonly DailyRewardModel Model;
        private readonly JiangShen.AssistantRuntime.Model source;
        internal readonly game.net.tcp_client Client = new game.net.tcp_client();
        internal readonly RootUI Owner = new RootUI();
        internal RootUI Root = new RootUI();
        internal readonly ActivityUI Panel = new ActivityUI();
        internal readonly BattleResultUI Dialog = new BattleResultUI();
        internal readonly __BaseUI Foreign = new __BaseUI();
        internal __BaseUI NewlyCreatedModal;
        internal bool Paused;
        internal bool MainInteractive { set { source.Main.interactive = value; } }
        internal string Overlay, ExternalGate = "PASS";
        internal int Sequence, Published, ContextReads, StockStarts;
        internal string LastRunReason;
        internal Fixture(bool fixtureWitness = true, bool hasContextProvider = true)
        {
            DailyRefreshProof.Stop();
            Model = new DailyRewardModel(DevelopmentCallbackCapture.InstallDailyReward, hasContextProvider ? (DailyRewardContextProvider)Context : null);
            source = new JiangShen.AssistantRuntime.Model
            {
                Owner = Owner.gameObject, Root = delegate { return Root; }, Inventory = Inventory,
                Gate = delegate { return Global.extern_enabled ? ExternalGate : "global_input_disabled"; },
                Overlay = delegate { return Overlay; }
            };
            TimeUtil._now = 210000; TimeUtil._thisDay = 200000;
            RootUI.m_bRefreshFive = Global.relogin = false;
            game.net.tcp_base.connection_state = 3;
            Global.extern_enabled = true;
            BuildingBuildUI.m_bBuildVisible = PopupUI.popup = BuildingBehaviourScript.m_bEditVisible = false;
            Global.character_ = Role;
            RootUI.m_client = Global.m_client = Client;
            MainUI.m_subui = null; MainUI.activity = Panel;
            BaseUI.rootUI = Owner;
            __BaseUI.battle_result_ui = null;
            Owner.rmg = Owner; Panel.rmg = Owner; Panel.gameObject = Owner.gameObject;
            UnityEngine.GameObject.Battle = Dialog.gameObject; Dialog.gameObject.component = Dialog;
            Global.ActivityTemplates.Clear(); Global.RewardTemplates.Clear();
            for (byte id = 1; id <= 6; id++)
            {
                Database._activity_reward template = new Database._activity_reward { id = id };
                Global.RewardTemplates[(uint)id] = template;
                Role.act_rewards[(uint)id] = new Database.Activity_Reward { idact_reward = id, temp = template };
                Task(id, id);
            }
            Task(2, 12);
            Panel.AppliedCategory = 2; Panel.AppliedRefresh = 218000;
            if (fixtureWitness) RecordFixtureEpoch();
            Capture();
        }
        internal void RecordFixtureEpoch()
        {
            uint now, day, cycle;
            // The fixture constructs the same-cycle rebuilt cache. Its
            // synthetic proof exercises the production gate independently
            // from the fixture that supplies only a valid clock.
            if (DailyRefreshGate.ReadClock(Role, out now, out day, out cycle) != "" ||
                !DailyRefreshProof.RecordSnapshot(Role, DailyTransport.Read(Client), cycle, Role.next_resource_time))
                throw new Exception("fixture epoch");
        }
        private void Task(byte category, byte id)
        {
            Database._activity template = new Database._activity { id = id, idreward = category };
            Global.ActivityTemplates[(uint)id] = template;
            ((Database.Activity_Reward)Role.act_rewards[(uint)category]).acts[(uint)id] =
                Role.acts[(uint)id] = new Database.Activity { idactivity = id, temp = template };
        }
        internal Database.Activity_Reward Reward(int id) { return (Database.Activity_Reward)Role.act_rewards[(uint)id]; }
        internal Database.Activity Row(int id) { return (Database.Activity)Role.acts[(uint)id]; }
        internal bool Status(string status) { return Model.Json().Contains("\"status\":\"" + status + "\""); }
        internal bool Next(string action) { return Model.Json().Contains("\"next_action\":\"" + action + "\""); }
        internal bool CategoryReady(int id) { return Model.Json().Contains("{\"id\":" + id + ",\"ready\":true"); }
        private UnityEngine.Object[] Inventory()
        {
            List<UnityEngine.Object> all = new List<UnityEngine.Object> { Root, Owner, Panel, Dialog, Foreign };
            if (NewlyCreatedModal != null) all.Add(NewlyCreatedModal);
            return all.ToArray();
        }
        private D Context() { ContextReads++; return DevelopmentContextReader.DailyReward(source, Paused); }
        internal void Capture() { Model.Capture(Context()); }
        internal bool Queue(string action, int category = 2, string id = Id, ulong role = 42)
        {
            string why;
            return Model.Queue(++Sequence, Model.Revision, action, id, role, category, out why);
        }
        internal bool Run(string action, int revision, int category = 2, string id = Id, Action duringPublication = null,
            DailyRewardInvocationGate canBegin = null)
        {
            return Model.ExecuteGui(Sequence, revision, action, id, 42, category, delegate
            {
                Published++;
                if (duringPublication != null) duringPublication();
            }, delegate
            {
                if (canBegin != null && !canBegin()) return false;
                StockStarts++;
                return true;
            }, out LastRunReason);
        }
        internal void Step(string action, int category = 2, string id = Id)
        {
            int revision = Model.Revision;
            Check(Queue(action, category, id) && Run(action, revision, category, id), action + " " + Model.Json());
            Capture();
        }
        internal void Claim()
        {
            Step("open");
            Step("claim");
            Check(Client.Sends == 1 && Client.SentCategory == 2 && Model.Unresolved && !Status("COMPLETED"), "invocation is pending");
        }
        internal void Reply()
        {
            Client.Receive(Accepted(2, Panel.AppliedRefresh, Panel.AppliedFinished));
            Capture();
        }
        internal void Timeout()
        {
            typeof(DailyRewardModel).GetField("since", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(Model, unchecked(Environment.TickCount - 18001));
            Capture();
        }
    }

    private static int Main()
    {
        try { RunTests(); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
    }

    private static void RunTests()
    {
        Fixture f = new Fixture(false);
        Check(!f.CategoryReady(2) && !f.Queue("open") && f.Model.Json().Contains("daily_refresh_unverified"),
            "production default requires verified refresh provenance");
        f = new Fixture(hasContextProvider: false);
        Check(f.CategoryReady(2) && !f.Queue("open") && f.Model.Json().Contains("daily_reward_fresh_context_unavailable"),
            "verified cache still requires a fresh GUI context provider");
        f = new Fixture();
        Check(!f.Queue("open", 1) && !f.Queue("open", 7) && !f.Queue("open", 2, Other, 43), "bounded role/category");
        Check(f.CategoryReady(2) && f.CategoryReady(6), "ready categories " + f.Model.Json());
        f.Claim();
        f.Reply();
        Check(f.Next("reward_confirm") && f.Panel.Responses == 1 && f.Model.Unresolved && !f.Status("COMPLETED"), "own response and cache, UI still open");
        Check(!f.Queue("reward_confirm", 3) && !f.Queue("reward_confirm", 2, Other), "wrong category/token confirmation rejected");
        f.Step("reward_confirm");
        Check(f.Next("close") && f.Panel.Confirms == 1 && !f.Dialog.visible && f.Model.Unresolved, "original confirmation, panel still open");
        f.Step("close");
        Check(f.Status("COMPLETED") && !f.Model.Unresolved && MainUI.m_subui == null && !f.Panel.visible, "settled and UI closed");
        f.Capture();
        Check(!f.Queue("open", 2, Other) && !f.Queue("open", 3, Id) && f.Queue("open", 3, Other), "claimed category and replay blocked, next category eligible");

        f = new Fixture();
        f.Reward(2).acts.Remove(12u); f.Capture();
        Check(!f.CategoryReady(2) && !f.Queue("open") && f.Client.Sends == 0, "missing panel row");
        f = new Fixture();
        f.Role.acts.Remove(12u); f.Reward(2).acts.Remove(12u); f.Capture();
        Check(!f.CategoryReady(2), "row missing from both caches detected by template");
        f = new Fixture();
        f.Role.acts[12u] = new Database.Activity { idactivity = 12, temp = f.Row(12).temp };
        f.Capture();
        Check(!f.CategoryReady(2), "equivalent but stale panel row");
        f = new Fixture();
        f.Row(12).condition_param = 0; f.Capture();
        Check(!f.CategoryReady(2), "server flag cannot replace newest row");
        f = new Fixture();
        f.Reward(2).finished_flag = false; f.Capture();
        Check(!f.CategoryReady(2), "local counts cannot replace server flag");
        f = new Fixture();
        f.Step("open"); f.Reward(2).cur_count--; f.Capture();
        Check(f.Next("close") && !f.Queue("claim"), "stale panel aggregates");
        f.Step("close");
        Check(f.Client.Sends == 0, "stale panel cleanup sends no request");
        f = new Fixture();
        f.Client.m_sendDatas.Add(new object()); f.Capture();
        Check(!f.Queue("open"), "pending stock send queue");
        f = new Fixture();
        f.MainInteractive = false; f.Capture();
        Check(!f.Queue("open"), "disabled main without owned panel cannot open reward flow");
        f = new Fixture();
        f.Client.m_handlers[(ushort)355] = new game.net.tcp_base.Handler { handler = delegate { }, life = 0f }; f.Capture();
        Check(f.Queue("open"), "zero-life passive subscription is idle");
        f = new Fixture();
        f.Step("open");
        int revision = f.Model.Revision;
        int starts = f.StockStarts;
        Check(f.Queue("claim") && !f.Run("claim", revision, duringPublication: delegate { f.Role.acts = new Hashtable(f.Role.acts); }) &&
            f.Published == 2 && f.StockStarts == starts && f.Client.Sends == 0 && f.LastRunReason == "daily_reward_publication_state_changed",
            "published owner does not cross invocation boundary after failed cache recheck");
        f = new Fixture();
        f.Step("open"); revision = f.Model.Revision;
        string stopFile = Path.GetTempFileName();
        try
        {
            int finalReads = 0;
            Check(f.Queue("claim") && !f.Run("claim", revision, duringPublication: delegate
            {
                f.Row(12).BeforeClosureCheck = delegate
                {
                    finalReads++;
                    File.WriteAllText(stopFile, "version=3\nnonce=" + Id + "\nsequence=3\ncommand=stop\n");
                };
            }, canBegin: delegate
            {
                Command pending = Command.Parse(File.ReadAllText(stopFile), Id, 2);
                return pending == null || pending.Name != "stop";
            }) && finalReads == 1 && f.Published == 2 && f.StockStarts == 1 && f.Client.Sends == 0 &&
                !f.Model.MutationAttempted && f.Panel.reward_select == 0 && f.Panel.interactive &&
                f.LastRunReason == "daily_reward_gui_final_admission_changed",
                "stop published inside final stock row sample wins after successful recheck and publication");
        }
        finally { File.Delete(stopFile); }
        f = new Fixture();
        f.Step("open"); revision = f.Model.Revision;
        Database.Activity priorRow = f.Row(12);
        Check(f.Queue("claim") && !f.Run("claim", revision, duringPublication: delegate
        {
            f.Role.acts[12u] = f.Reward(2).acts[12u] = new Database.Activity
            {
                idactivity = priorRow.idactivity, temp = priorRow.temp, condition_param = priorRow.condition_param,
                refresh_time = priorRow.refresh_time
            };
        }) && f.Client.Sends == 0, "replace aligned selected task with identical values during publication");
        f = new Fixture();
        f.Step("open"); revision = f.Model.Revision;
        Check(f.Queue("claim"), "queue before new modal");
        f.Overlay = "newly_created_modal"; f.NewlyCreatedModal = new __BaseUI { visible = true };
        int published = f.Published, sampled = f.ContextReads;
        Check(!f.Run("claim", revision) && f.Published == published && f.ContextReads == sampled + 1 && f.Client.Sends == 0,
            "new foreign modal absent from queued inventory blocks before publication");
        f = new Fixture();
        f.Step("open"); revision = f.Model.Revision; sampled = f.ContextReads;
        Check(f.Queue("claim") && !f.Run("claim", revision, duringPublication: delegate
        {
            f.Overlay = "created_during_publication";
            f.NewlyCreatedModal = new __BaseUI { visible = true };
        }) && f.ContextReads == sampled + 2 && f.Client.Sends == 0, "new foreign modal blocks after publication");
        f = new Fixture();
        f.Step("open"); revision = f.Model.Revision;
        Check(f.Queue("claim") && !f.Run("claim", revision, duringPublication: delegate { f.ExternalGate = "load_started"; }) &&
            f.Client.Sends == 0, "current external gate replaces captured pass");
        f = new Fixture();
        f.Step("open"); revision = f.Model.Revision;
        RootUI priorRoot = f.Root;
        Check(f.Queue("claim") && !f.Run("claim", revision, duringPublication: delegate { f.Root = new RootUI(); }) &&
            priorRoot.gameObject.activeInHierarchy && f.Client.Sends == 0, "new current root while old root stays active");
        f = new Fixture();
        f.Step("open"); revision = f.Model.Revision;
        game.net.tcp_client otherClient = new game.net.tcp_client();
        Check(f.Queue("claim") && !f.Run("claim", revision, duringPublication: delegate { RootUI.m_client = Global.m_client = otherClient; }) &&
            f.Client.Sends == 0 && otherClient.Sends == 0, "new connected client during publication");
        f = new Fixture();
        f.Step("open");
        revision = f.Model.Revision;
        Check(f.Queue("claim"), "queue before 05:00");
        TimeUtil._now = 218000;
        Check(!f.Run("claim", revision) && f.Client.Sends == 0 && f.Status("REJECTED"), "05:00 expires pending refresh");
        f = new Fixture();
        RootUI.m_bRefreshFive = true;
        f.Capture();
        Check(!f.CategoryReady(2) && !f.Queue("open"), "refresh flag blocks despite future timestamp");
        RootUI.m_bRefreshFive = false; TimeUtil._now = 218000; f.Role.next_resource_time = 304400; f.Capture();
        Check(f.Model.Json().Contains("\"refresh_epoch\":218000"), "05:00 is in the new cycle");
        f = new Fixture();
        f.Step("open"); revision = f.Model.Revision;
        Check(f.Queue("claim"), "claim queued before same-object epoch change");
        TimeUtil._now = 218000; f.Role.next_resource_time = 304400; f.RecordFixtureEpoch();
        Check(!f.Run("claim", revision) && f.Client.Sends == 0 && f.Status("REJECTED"),
            "a future timestamp and unchanged row objects cannot preserve previous-cycle intent");

        f = new Fixture();
        f.Claim(); f.Client.Receive(new byte[] { 8, 0 }); f.Capture();
        Check(f.Status("UNKNOWN") && f.Model.OutcomeUnknown && f.Model.Unresolved && !f.Next("close") && !f.Next("reward_confirm") &&
            f.Panel.Responses == 1 && f.Model.Json().Contains("daily_reward_negative_receipt_unattributed"), "uncorrelated negative is uncertain");
        RootUI.m_client = Global.m_client = new game.net.tcp_client(); f.Capture();
        Check(f.Status("UNKNOWN") && f.Model.Unresolved && !f.Queue("open", 3, Other), "negative uncertainty survives reconnect");

        f = new Fixture();
        f.Claim(); f.Client.Receive(null); f.Capture();
        Check(f.Status("UNKNOWN") && f.Model.Unresolved && !f.Queue("open", 3, Other), "null callback uncertainty blocks every retry");
        f = new Fixture();
        f.Claim(); f.Timeout();
        Check(f.Status("UNKNOWN") && f.Model.Unresolved, "missing callback times out");
        f = new Fixture();
        f.Claim(); f.Client.m_handlers[(ushort)356] = new game.net.tcp_base.Handler { handler = delegate { }, life = 10f }; f.Capture();
        Check(f.Status("UNKNOWN") && f.Model.Unresolved, "replacement callback cannot settle");
        f = new Fixture();
        f.Claim(); f.Panel.UpdateCache = false; f.Reply();
        Check(f.Status("UNKNOWN") && f.Model.Unresolved && !f.Next("reward_confirm"), "receipt requires matching cache readback");
        f = new Fixture();
        f.Claim(); f.Reply(); f.Step("reward_confirm");
        f.Reward(2).refresh = 0; f.Capture();
        Check(f.Status("UNKNOWN") && !f.Next("close"), "post-confirm cache change prevents completion");
        f = new Fixture();
        f.Claim(); f.Client.Receive(Accepted(3, f.Panel.AppliedRefresh, false)); f.Capture();
        Check(f.Status("UNKNOWN") && f.Panel.Confirms == 0, "wrong receipt category");
        f = new Fixture();
        f.Claim();
        game.net.tcp_base.Handler own = (game.net.tcp_base.Handler)f.Client.m_handlers[(ushort)356];
        f.Client.Receive(Accepted(2, f.Panel.AppliedRefresh, false));
        using (MemoryStream duplicate = new MemoryStream(Accepted(2, f.Panel.AppliedRefresh, false))) own.handler(duplicate, own.arg);
        f.Capture();
        Check(f.Status("UNKNOWN") && f.Panel.Confirms == 0, "duplicate callback dispatch");
        f = new Fixture();
        f.Claim();
        own = (game.net.tcp_base.Handler)f.Client.m_handlers[(ushort)356];
        using (MemoryStream premature = new MemoryStream(Accepted(2, f.Panel.AppliedRefresh, false)))
            lock (f.Client) own.handler(premature, own.arg);
        f.Capture();
        Check(f.Model.OutcomeUnknown && f.Model.Json().Contains("daily_reward_callback_not_dequeued") && f.Panel.Responses == 1 && f.Panel.Confirms == 0,
            "adapter forwards premature stock call without treating it as a dequeued response");
        f = new Fixture();
        f.Claim();
        f.Client.QueueResponse(356, Accepted(2, f.Panel.AppliedRefresh, false));
        f.Client.QueueResponse(356, Accepted(2, f.Panel.AppliedRefresh, false));
        f.Capture();
        Check(f.Status("UNKNOWN"), "two queued reward replies");
        f = new Fixture();
        f.Claim();
        f.Client.QueueResponse(356, Accepted(2, f.Panel.AppliedRefresh, false));
        f.Client.QueueResponse(356, new byte[] { 8, 0 });
        Check(f.Client.DispatchNext() && !f.Client.DispatchNext() && f.Client.messages.Count == 0 && f.Panel.Responses == 1,
            "stock dispatcher drains duplicate without second callback between captures");
        f.Capture();
        Check(f.Status("UNKNOWN") && f.Model.Unresolved && f.Model.Json().Contains("daily_reward_duplicate_reply_queued") &&
            !f.Next("reward_confirm") && f.Panel.Confirms == 0, "callback sees duplicate missed by surrounding captures");
        f = new Fixture();
        f.Claim(); f.Panel.Throw = true;
        try { f.Reply(); throw new Exception("expected original throw"); }
        catch (InvalidOperationException) { }
        f.Capture();
        Check(f.Status("UNKNOWN"), "original callback throw forwarded, no confirmation");

        f = new Fixture();
        f.Claim(); f.Client.m_stream = new MemoryStream(); f.Capture();
        Check(f.Status("UNKNOWN"), "underlying stream replacement");
        f = new Fixture();
        f.Claim();
        Stream beforeCallback = f.Client.m_stream;
        f.Client.m_stream = new MemoryStream();
        f.Client.Receive(Accepted(2, f.Panel.AppliedRefresh, false));
        f.Client.m_stream = beforeCallback;
        f.Capture();
        Check(f.Status("UNKNOWN") && f.Panel.Responses == 1 && !f.Next("reward_confirm"), "callback itself rejects replaced transport");
        f = new Fixture();
        f.Claim();
        ((game.net.tcp_base.Handler)f.Client.m_handlers[(ushort)356]).arg = new object();
        f.Reply();
        Check(f.Status("UNKNOWN") && f.Panel.Responses == 1 && !f.Next("reward_confirm"), "foreign callback argument is forwarded without admission");
        f = new Fixture();
        f.Claim(); f.Client.m_client = new game.net.tcp_client.Socket { Connected = true }; f.Capture();
        Check(f.Status("UNKNOWN"), "underlying socket replacement");
        f = new Fixture();
        f.Claim(); Global.character_ = new Database.Character(); f.Capture();
        Check(f.Status("UNKNOWN"), "same ID on replaced role instance");
        f = new Fixture();
        f.Claim(); f.Paused = true; f.Capture();
        Check(f.Status("UNKNOWN") && !f.Queue("reward_confirm"), "pause after claim blocks further UI");
        f = new Fixture();
        f.Claim(); f.Model.Stop("fixture_stop"); f.Reply();
        Check(f.Status("UNKNOWN") && f.Panel.Responses == 1 && f.Panel.Confirms == 0, "stop restores original without claiming completion");
        f = new Fixture();
        f.Step("open"); f.Model.Stop("fixture_stop");
        Check(f.Status("STOPPED") && !f.Queue("claim") && f.Client.Sends == 0, "stop before claim");
        f = new Fixture();
        f.Step("open"); revision = f.Model.Revision;
        Check(f.Queue("claim"), "queue before publication cancellation");
        try { f.Run("claim", revision, duringPublication: delegate { throw new InvalidOperationException("stop"); }); throw new Exception("expected cancellation"); }
        catch (InvalidOperationException) { }
        Check(f.Client.Sends == 0 && !f.Model.MutationAttempted, "publication cancellation before native send");
        f = new Fixture();
        f.Step("open"); revision = f.Model.Revision; f.Panel.ThrowAfterSend = true;
        Check(f.Queue("claim") && !f.Run("claim", revision) && f.StockStarts == 2 && f.Client.Sends == 1 &&
            f.Model.OutcomeUnknown && f.Model.Unresolved, "stock send crossed invocation boundary before exception");
        f.Model.Stop("fixture_stop");
        Check(f.Model.Unresolved && f.Model.OutcomeUnknown && !f.Next("close"), "stop preserves uncertainty after actual send failure");

        f = new Fixture();
        MainUI.m_subui = f.Panel; f.Panel.Open(); f.Capture();
        Check(!f.Queue("open") && !f.Queue("claim"), "pre-existing panel has no ownership");
        f = new Fixture();
        f.Claim(); f.Reply();
        f.Dialog.OnClose = delegate { throw new Exception("foreign dialog close"); }; f.Capture();
        Check(f.Status("UNKNOWN") && !f.Queue("reward_confirm") && f.Panel.Confirms == 0, "foreign dialog close preserved");
        f = new Fixture();
        f.Claim(); f.Reply();
        Check(f.Next("reward_confirm"), "own dialog observed before overlay");
        UnityEngine.GUI.enabled = false; revision = f.Model.Revision;
        Check(f.Queue("reward_confirm") && !f.Run("reward_confirm", revision), "GUI disabled at invoke");
        UnityEngine.GUI.enabled = true;
        f.Overlay = "fixture_overlay"; f.Foreign.visible = true; f.Capture();
        Check(!f.Next("reward_confirm") && !f.Queue("reward_confirm") && f.Panel.Confirms == 0, "foreign overlay blocks owned dialog");
        f.Foreign.visible = false; f.Capture();
        Check(f.Next("reward_confirm"), "own dialog ready once foreign overlay clears");
        foreach (Action staticOverlay in new Action[] {
            delegate { PopupUI.popup = true; }, delegate { BuildingBuildUI.m_bBuildVisible = true; },
            delegate { BuildingBehaviourScript.m_bEditVisible = true; } })
        {
            f = new Fixture();
            f.Claim(); f.Reply(); revision = f.Model.Revision;
            Check(!Global.extern_enabled && f.Queue("reward_confirm") && !f.Run("reward_confirm", revision, duringPublication: staticOverlay) &&
                f.Dialog.visible && f.Panel.Confirms == 0, "fresh static overlay blocks with global input already disabled");
        }
        f = new Fixture();
        f.Claim(); f.Reply(); revision = f.Model.Revision;
        Check(f.Queue("reward_confirm") && !f.Run("reward_confirm", revision, duringPublication: delegate
        {
            f.Overlay = "new_foreign_reward_overlay";
            f.NewlyCreatedModal = new __BaseUI { visible = true };
        }) && f.Dialog.visible && f.Panel.Confirms == 0 && f.Model.Unresolved, "fresh provider preserves newly obstructed owned dialog");
        f = new Fixture();
        f.Claim(); f.Reply(); revision = f.Model.Revision;
        BattleResultUI replacement = new BattleResultUI { visible = true, OnClose = f.Dialog.OnClose,
            m_prevUI = f.Dialog.m_prevUI, m_vctBonuses = f.Dialog.m_vctBonuses };
        Check(f.Queue("reward_confirm") && !f.Run("reward_confirm", revision, duringPublication: delegate
        {
            f.NewlyCreatedModal = replacement;
            replacement.gameObject.component = replacement;
            UnityEngine.GameObject.Battle = replacement.gameObject;
            __BaseUI.battle_result_ui = replacement;
        }) && f.Status("UNKNOWN") && f.Dialog.visible && replacement.visible && f.Panel.Confirms == 0,
            "replacement reward modal cannot reuse an owned delegate and reward list");
        f = new Fixture();
        f.Claim(); f.Panel.ShowReward = false; f.Reply();
        Check(f.Status("UNKNOWN") && f.Model.Unresolved, "accepted receipt without own dialog does not complete");

        DailyRewardReceipt decoded = DailyRewardReceipt.Decode(Accepted(6, 218000, true));
        Check(decoded.Accepted && decoded.Category == 6 && decoded.Refresh == 218000 && decoded.Finished && decoded.Treasures == 1, "wire fields");
        foreach (byte[] malformed in new[] { new byte[] { 8, 2 }, new byte[] { 8, 1 }, new byte[] { 8, 0, 8, 0 },
            new byte[] { 8, 1, 26, 8, 8, 2 }, Accepted(1, 218000, true) })
        {
            try { DailyRewardReceipt.Decode(malformed); throw new Exception("malformed wire accepted"); }
            catch (FormatException) { checks++; }
        }
        Console.WriteLine("{\"status\":\"PASS\",\"assertions\":" + checks + "}");
    }
}
