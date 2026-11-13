using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Threading;
using JiangShen.AssistantRuntime;

namespace UnityEngine
{
    public class Object { public int GetInstanceID() { return 1; } }
    public class Transform { public Transform root { get { return this; } } }
    public class GameObject { public bool activeInHierarchy = true; public Transform transform = new Transform(); }
    public class Component : Object { public GameObject gameObject = new GameObject(); public Transform transform { get { return gameObject.transform; } } }
    public class Behaviour : Component { public bool enabled = true; }
    public static class Event { public static object current = new object(); }
    public static class GUI { public static bool enabled = true; }
    public static class Time { public static int frameCount = 1; }
}
namespace JiangShen.AssistantRuntime
{
    internal sealed class ProbeFailure : Exception { internal ProbeFailure(string why) : base(why) { } }
    internal static class Model
    {
        internal static Type GameType(string name) { return FirstType(name); }
        internal static Type FirstType(string name) { return Type.GetType(name, true); }
    }
}
public class __BaseUI : UnityEngine.Behaviour
{
    public delegate void delegate_OnOK(bool ok, object[] args);
    public delegate void delegate_OnClose();
    public UnityEngine.Component rmg;
    public bool visible, interactive = true, loading, collecting;
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
    public static void ApplyFiveClock(uint result, uint next)
    {
        m_bRefreshFive = false;
        Global.character_.next_resource_time = next;
        if (result != 0) return;
        foreach (Database.Activity act in Global.character_.acts.Values) act.condition_param = 0;
    }
}
public class MainUI : BaseUI
{
    public static __BaseUI m_subui;
    public static GuildUI_3 guild;
    public static void _guild() { m_subui = guild; guild.visible = true; }
    public static void SetSubUI(__BaseUI ui) { if (m_subui != null) m_subui.Hide(); m_subui = ui; }
}
public class BattleResultUI : __BaseUI
{
    public string mine_ui = "";
    public bool autoClose;
    public UnityEngine.Component m_prevUI;
    public delegate_OnClose OnClose;
}
public class ConsumeUI : __BaseUI
{
    public int gold;
    public bool notip;
    public object tip, OnOK_objs;
    public delegate_OnOK OnOK;
    public void setConsume(int cost, byte id, delegate_OnOK ok)
    {
        if (Global.character_.consume_tips_.Contains((uint)id)) ok(true, null);
        else { gold = cost; tip = Global.getTemplate("consume_tip", id); OnOK = ok; visible = true; }
    }
}
public class GuildUI_3 : __BaseUI
{
    public bool show_list, need_close, lotting, lotend, ShowLotteryReward;
    public string popup_type = "", page = "", subwindow = "", subpage = "";
    public int ResponseCalls;
    public UnityEngine.GameObject m_guild_tree;
    public Database.Guild char_guild;
    public Database.GuildMember char_member;
    public ArrayList ids = new ArrayList(), lots = new ArrayList(), lot_quality = new ArrayList();
    public int guild_level, cur_highlight = -1, SentType;
    private void make_lottery()
    {
        guild_level = char_guild.level;
        for (int i = 1; i <= 8; i++) { ids.Add(i); lots.Add(new object()); lot_quality.Add(3u); }
    }
    private void cs_donate(int type) { SentType = type; Global.m_client.Put(301, sc_donate, type, 10f); }
    private void cs_lottery() { Global.m_client.Put(302, sc_lottery, null, 10f); }
    private void GoldDonateOK(bool ok, object[] args) { if (ok) cs_donate(3); }
    private void sc_donate(MemoryStream stream, object arg)
    {
        ResponseCalls++;
        if (stream == null) return;
        GuildReceipt receipt = GuildReceipt.Read(301, stream.ToArray());
        if (receipt.Accepted)
        {
            long total = Global.character_.money + Global.character_.money64 - receipt.Money;
            Global.character_.money = 0; Global.character_.money64 = total;
            Global.character_.gold -= receipt.Gold;
        }
        interactive = true;
    }
    private void sc_lottery(MemoryStream stream, object arg)
    {
        ResponseCalls++;
        if (stream != null && GuildReceipt.Read(302, stream.ToArray()).Accepted)
        {
            char_member.guild_lottery_count--; char_member.contribution -= 400;
            if (ShowLotteryReward)
            {
                cur_highlight = 0;
                battle_result_ui.m_prevUI = rmg;
                battle_result_ui.OnClose = ResumeUI;
                battle_result_ui.visible = true;
            }
        }
        interactive = true;
    }
    private void ResumeUI() { cur_highlight = -1; interactive = true; }
}
public static class RootBase
{
    public static void Bind(game.net.tcp_base client) { client.Put(355, handelActvity, null, -1f); }
    private static void handelActvity(MemoryStream stream, object arg)
    {
        if (stream == null) return;
        foreach (uint task in new uint[] { 18, 20 })
        {
            int? progress = GuildReceipt.Progress(stream.ToArray(), (int)task);
            if (progress.HasValue) ((Database.Activity)Global.character_.acts[task]).condition_param = (byte)progress.Value;
        }
    }
}
public static class Global
{
    public static Database.Character character_;
    public static game.net.tcp_base m_client;
    public static bool extern_enabled = true;
    public static bool relogin;
    private static readonly object Tip = new object();
    public static object getTemplate(string table, uint id) { return table == "consume_tip" && id == 123 ? Tip : null; }
}
public static class TimeUtil
{
    public static uint _now = 210000, _thisDay = 200000;
}
namespace game.net
{
    public class tcp_base
    {
        public delegate void Callback(MemoryStream stream, object arg);
        public sealed class Handler { public Callback handler; public object arg; public float life; }
        public sealed class HandleMessage { public ushort op; public byte[] bytes; }
        public static int connection_state = 3;
        public bool m_bConnectLost;
        public readonly Hashtable m_handlers = new Hashtable();
        public readonly ArrayList messages = new ArrayList();
        public int Sends;
        public void Put(ushort op, Callback handler, object arg, float life)
        { m_handlers[op] = new Handler { handler = handler, arg = arg, life = life }; if (op != 355) Sends++; }
        public void Receive(ushort op, byte[] bytes)
        {
            Queue(op, bytes); Run();
        }
        public void Queue(ushort op, byte[] bytes) { messages.Add(new HandleMessage { op = op, bytes = bytes }); }
        public void Run()
        {
            lock (this)
            {
                while (messages.Count > 0)
                {
                    HandleMessage message = (HandleMessage)messages[0]; messages.RemoveAt(0);
                    Handler h = (Handler)m_handlers[message.op];
                    if (h == null) continue;
                    if (h.life > 0) m_handlers.Remove(message.op);
                    using (MemoryStream stream = message.bytes == null ? null : new MemoryStream(message.bytes, false)) h.handler(stream, h.arg);
                }
            }
        }
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
        public ulong id = 42, idguild = 99;
        public uint next_resource_time = 218000;
        public int gold = 80, bind_gold = 9, money = 100000;
        public long money64;
        public bool gm;
        public readonly Hashtable guilds = new Hashtable(), members = new Hashtable(), acts = new Hashtable(), act_rewards = new Hashtable(), consume_tips_ = new Hashtable();
        public void make_member_sort() { }
    }
    public class Guild { public ulong id = 99; public bool active = true; public ushort level = 4; public static bool kicked; }
    public class GuildMember { public ulong id = 42, idguild = 99; public uint donate_refresh, contribution = 400; public byte guild_lottery_count = 1; }
    public class Activity { public byte idactivity, condition_param; public int refresh_time = 218000; public ActivityTemplate temp; }
    public class ActivityTemplate { public byte id, idreward = 5, id_uibutton = 18; public int cond_param = 1; public bool active = true; }
    public class Activity_Reward { public byte idact_reward = 5; public readonly Hashtable acts = new Hashtable(); }
}

internal static class DailyGuildTests
{
    private const string Nonce = "0123456789abcdef0123456789abcdef";
    private static int checks;
    private static void Check(bool pass, string label) { if (!pass) throw new Exception(label); checks++; }
    private static bool Status(DailyGuildModel model, string status) { return model.Json().Contains("\"status\":\"" + status + "\""); }
    private static byte[] Donate(int type)
    {
        if (type == 1) return new byte[] { 8,1,16,0,24,0,32,0,40,208,15,48,0,56,0 };
        if (type == 2) return new byte[] { 8,1,16,0,24,0,32,0,40,192,154,12,48,0,56,0 };
        return new byte[] { 8,1,16,0,24,0,32,0,40,0,48,160,1,56,0 };
    }
    private static byte[] Push(byte task) { return new byte[] { 10,4,8,task,16,1 }; }

    private sealed class Fixture
    {
        internal readonly Database.Character Role = new Database.Character();
        internal readonly Database.GuildMember Member = new Database.GuildMember();
        internal readonly DailyGuildModel Model;
        internal readonly RootUI Root = new RootUI(), Owner = new RootUI();
        internal readonly GuildUI_3 Ui = new GuildUI_3();
        internal readonly ConsumeUI Premium = new ConsumeUI();
        internal readonly BattleResultUI Reward = new BattleResultUI();
        internal readonly game.net.tcp_client Client = new game.net.tcp_client();
        internal int Sequence;
        internal Fixture(bool verified = true)
        {
            Model = verified ? new DailyGuildModel(DailyRefreshGate.ReadClock) : new DailyGuildModel();
            TimeUtil._now = 210000; TimeUtil._thisDay = 200000;
            game.net.tcp_base.connection_state = 3; RootUI.m_bRefreshFive = Global.relogin = Database.Guild.kicked = false;
            Global.character_ = Role; RootUI.m_client = Global.m_client = Client;
            MainUI.m_subui = null; MainUI.guild = Ui; BaseUI.rootUI = Owner;
            __BaseUI.battle_result_ui = Reward;
            Owner.rmg = Owner; Ui.rmg = Premium.rmg = Reward.rmg = Owner; Ui.gameObject = Premium.gameObject = Reward.gameObject = Owner.gameObject;
            Ui.char_guild = new Database.Guild(); Ui.char_member = Member;
            Role.guilds[Role.idguild] = Ui.char_guild; Role.members[Role.id] = Member;
            Database.Activity_Reward reward = new Database.Activity_Reward();
            Role.act_rewards[5u] = reward;
            foreach (byte id in new byte[] { 18, 20 })
                reward.acts[(uint)id] = Role.acts[(uint)id] = new Database.Activity { idactivity = id, temp = new Database.ActivityTemplate { id = id } };
            RootBase.Bind(Client);
            Capture();
        }
        internal void Capture()
        { Model.Capture(new SharedDevelopmentContext(new UnityEngine.Object[] { Root, Owner, Ui, Premium, Reward }, Root, Owner, Role, Role.id.ToString(), "PASS", false)); }
        internal Command Command(string action, int type)
        { return new Command { GuildAction = action, Sequence = ++Sequence, ExpectedRevision = Model.Revision, OperationId = Nonce, GuildCharacterId = Role.id, GuildId = Role.idguild, GuildTaskId = type == 0 ? 18 : 20, DonationType = type, MaxPremium = type == 3 ? 80 : 0 }; }
        internal void Run(string action, int type)
        {
            Command c = Command(action, type);
            string why;
            Check(Model.Queue(c, out why), action + " queue " + why);
            Check(Model.ExecuteGui(c, delegate { }, out why), action + " invoke " + why);
            Capture();
        }
        internal game.net.tcp_base.Callback Callback(ushort op)
        { return ((game.net.tcp_base.Handler)Client.m_handlers[op]).handler; }
        internal void DonatePanel() { Run("open", 2); Run("donate_panel", 2); }
        internal void SetPanelRefresh(int task, int offset)
        {
            uint key = (uint)task;
            Database.Activity latest = (Database.Activity)Role.acts[key];
            Database.Activity_Reward reward = (Database.Activity_Reward)Role.act_rewards[5u];
            reward.acts[key] = offset == 0 ? latest : new Database.Activity { idactivity = latest.idactivity,
                condition_param = latest.condition_param, temp = latest.temp, refresh_time = latest.refresh_time + offset };
        }
        internal void ReplaceCaches()
        {
            Ui.char_guild = new Database.Guild();
            Ui.char_member = new Database.GuildMember();
            Role.guilds[Role.idguild] = Ui.char_guild; Role.members[Role.id] = Ui.char_member;
        }
    }

    private static void RejectRefreshChangeAtPublication(Fixture f, string action, int type, bool observesProgress, bool both = false)
    {
        string why;
        Command command = f.Command(action, type);
        game.net.tcp_base.Callback stock = f.Callback(355);
        bool published = false;
        bool interactive = f.Ui.interactive, premiumVisible = f.Premium.visible;
        object subUi = MainUI.m_subui;
        string page = f.Ui.page, subwindow = f.Ui.subwindow;
        Check(f.Model.Queue(command, out why), "guild_coherent_command_queued:" + action + ":" + type);
        bool invoked = f.Model.ExecuteGui(command, delegate
        {
            published = true;
            bool hookActive = !object.ReferenceEquals(stock, f.Callback(355));
            Check(hookActive == observesProgress, "guild_355_ownership_at_publication");
            if (both) ((Database.Activity)f.Role.acts[(uint)command.GuildTaskId]).refresh_time++;
            else f.SetPanelRefresh(command.GuildTaskId, 1);
        }, out why);
        Check(published && !invoked && why == "guild_daily_task_unverified" && f.Client.Sends == 0 && !f.Model.MutationAttempted &&
            !f.Client.m_handlers.Contains((ushort)301) && !f.Client.m_handlers.Contains((ushort)302) &&
            f.Role.gold == 80 && f.Role.bind_gold == 9 && f.Member.contribution == 400 &&
            object.ReferenceEquals(stock, f.Callback(355)) && object.ReferenceEquals(subUi, MainUI.m_subui) &&
            f.Ui.interactive == interactive && f.Premium.visible == premiumVisible && f.Ui.page == page && f.Ui.subwindow == subwindow,
            "guild_equal_count_refresh_change_blocks_published_stock_branch:" + action + ":" + type + ":" + both);
        f.Capture();
    }

    private static void ActivityRefreshCoherence()
    {
        foreach (int type in new[] { 0, 2, 3 })
        {
            Fixture f = new Fixture();
            string why;
            int task = type == 0 ? 18 : 20;
            string panel = type == 0 ? "lottery_panel" : "donate_panel", request = type == 0 ? "lottery" : "donate";
            f.SetPanelRefresh(task, 1);
            f.Capture();
            Check(!f.Model.Queue(f.Command("open", type), out why) && why == "guild_daily_task_unverified" &&
                f.Client.Sends == 0 && MainUI.m_subui == null && !f.Model.Busy, "guild_inconsistent_preflight_" + type);
            f.SetPanelRefresh(task, 0);
            f.Capture();
            Command oldFrame = f.Command("open", type);
            Check(f.Model.Queue(oldFrame, out why), "guild_queued_at_original_task_refresh");
            ((Database.Activity)f.Role.acts[(uint)task]).refresh_time++; // The stock push can update both aliases.
            f.Capture();
            bool published = false;
            Check(f.Model.Revision != oldFrame.ExpectedRevision &&
                !f.Model.ExecuteGui(oldFrame, delegate { published = true; }, out why) && !published && !f.Model.Busy,
                "guild_coherent_task_refresh_change_invalidates_queued_frame");
            RejectRefreshChangeAtPublication(f, "open", type, false);
            Check(!f.Model.Busy, "guild_unopened_refusal_needs_no_cleanup");
            f.SetPanelRefresh(task, 0);
            f.Capture();
            f.Run("open", type);
            RejectRefreshChangeAtPublication(f, panel, type, false);
            Check(!f.Model.Queue(f.Command(panel, type), out why) && f.Model.Busy && !f.Model.Unresolved,
                "guild_inconsistent_owned_entry_can_only_close");
            f.SetPanelRefresh(task, 0);
            f.Capture();
            f.Run(panel, type);
            f.SetPanelRefresh(task, 1);
            f.Capture();
            Check(!f.Model.Queue(f.Command(request, type), out why) && f.Client.Sends == 0,
                "guild_inconsistent_request_preflight_" + type);
            f.SetPanelRefresh(task, 0);
            f.Capture();
            RejectRefreshChangeAtPublication(f, request, type, type != 3);
            f.SetPanelRefresh(task, 0);
            f.Capture();
            RejectRefreshChangeAtPublication(f, request, type, type != 3, both: true);
            if (type != 3)
            {
                f.SetPanelRefresh(task, 1);
                f.Capture();
                f.Run("close", type);
                Check(!f.Model.Busy && !f.Model.Unresolved, "guild_close_remains_available_after_no_send");
                continue;
            }

            f.SetPanelRefresh(task, 0);
            f.Capture();
            f.Run(request, type);
            f.SetPanelRefresh(task, 1);
            f.Capture();
            Check(!f.Model.Queue(f.Command("premium_confirm", 3), out why) && f.Client.Sends == 0 &&
                f.Premium.visible && f.Model.Unresolved, "guild_inconsistent_premium_confirmation_preflight");
            f.SetPanelRefresh(task, 0);
            f.Capture();
            RejectRefreshChangeAtPublication(f, "premium_confirm", 3, true);
            f.SetPanelRefresh(task, 0);
            f.Capture();
            RejectRefreshChangeAtPublication(f, "premium_confirm", 3, true, both: true);
            Check(f.Model.Busy && f.Model.Unresolved && !f.Model.Queue(f.Command("close", 3), out why),
                "guild_owned_premium_hold_survives_refused_confirmation");
            f.Model.Stop("fixture_stop");
            Check(Status(f.Model, "UNKNOWN") && f.Model.Unresolved && f.Premium.visible && f.Client.Sends == 0,
                "guild_stop_retains_unresolved_premium_hold");
        }

        Fixture skip = new Fixture();
        skip.Role.consume_tips_[123u] = true;
        skip.Capture();
        skip.Run("open", 3);
        skip.Run("donate_panel", 3);
        RejectRefreshChangeAtPublication(skip, "donate", 3, true);
        Check(skip.Model.Busy && !skip.Model.Unresolved && !skip.Premium.visible,
            "guild_premium_opt_out_still_requires_consistent_activity");
    }

    private static void RefreshBoundaries()
    {
        Fixture f = new Fixture();
        string why;
        f.DonatePanel();
        game.net.tcp_base.Callback original = f.Callback(355);
        Command c = f.Command("donate", 2);
        Check(f.Model.Queue(c, out why), "refresh_boundary_queued");
        Check(!f.Model.ExecuteGui(c, delegate { TimeUtil._now = 218000; }, out why) && why == "daily_refresh_pending" &&
            f.Client.Sends == 0 && object.ReferenceEquals(original, f.Callback(355)), "refresh_due_after_publication_restores_hook_before_send");

        f = new Fixture(); f.DonatePanel(); original = f.Callback(355);
        f.Run("donate", 2);
        f.Client.Receive(301, Donate(2)); f.Capture();
        TimeUtil._now = 218000; f.Role.next_resource_time = 304400;
        f.Client.Receive(355, Push(20)); f.Capture();
        Check(Status(f.Model, "UNKNOWN") && f.Model.Unresolved && f.Client.Sends == 1 &&
            f.Model.Json().Contains("\"progress_after\":null") && object.ReferenceEquals(original, f.Callback(355)),
            "exact_05_cycle_invalidates_old_request_even_if_refresh_already_clear");

        f = new Fixture();
        f.Member.donate_refresh = 217900; TimeUtil._now = 218000; f.Role.next_resource_time = 304400; f.Capture();
        Check(!f.Model.Queue(f.Command("open", 2), out why) && why == "guild_donate_button_waiting_reset", "normal_ui_boundary_quirk_blocks_one_tick");
        TimeUtil._now++; f.Capture();
        Check(f.Model.Queue(f.Command("open", 2), out why), "new_cycle_after_pinned_ui_tick");

        f = new Fixture(false);
        TimeUtil._now = 218000; RootUI.m_bRefreshFive = true;
        Database.Activity act = (Database.Activity)f.Role.acts[20u]; act.condition_param = 1;
        RootUI.ApplyFiveClock(1, 304400); f.Capture();
        Check(act.condition_param == 1 && f.Role.next_resource_time > TimeUtil._now && !RootUI.m_bRefreshFive &&
            f.Model.Json().Contains("daily_refresh_unverified") && !f.Model.Queue(f.Command("open", 2), out why) && f.Client.Sends == 0,
            "failed_36_with_future_timestamp_cannot_enable_production_actions");
        RootUI.ApplyFiveClock(0, 304400); f.Capture();
        Check(act.condition_param == 0 && !f.Model.Queue(f.Command("open", 2), out why), "unwitnessed_36_success_also_fails_closed");
    }

    private static void TransportAndCleanup()
    {
        Fixture f = new Fixture(); string why;
        f.DonatePanel();
        game.net.tcp_base.Callback original = f.Callback(355);
        Command c = f.Command("donate", 2);
        Check(f.Model.Queue(c, out why), "socket_race_queued");
        Check(!f.Model.ExecuteGui(c, delegate { f.Client.m_stream = new MemoryStream(); }, out why) && why == "guild_transport_changed" &&
            f.Client.Sends == 0 && object.ReferenceEquals(original, f.Callback(355)), "transport_replacement_after_publication_restores_hook_before_send");

        foreach (Action<Fixture> cut in new Action<Fixture>[] {
            delegate(Fixture x) { x.Client.m_client = new game.net.tcp_client.Socket { Connected = true }; },
            delegate(Fixture x) { x.Client.m_stream = new MemoryStream(); },
            delegate(Fixture x) { x.Client.m_client.Connected = false; },
            delegate(Fixture x) { x.Client.m_stream.Dispose(); },
            delegate(Fixture x) { x.Client.m_bConnectLost = true; },
            delegate(Fixture x) { game.net.tcp_base.connection_state = 2; },
            delegate(Fixture x) { Global.relogin = true; },
        })
        {
            f = new Fixture(); f.DonatePanel(); original = f.Callback(355);
            f.Run("donate", 2); cut(f); f.Capture();
            Check(Status(f.Model, "UNKNOWN") && f.Model.Unresolved && !f.Model.Queue(f.Command("donate", 2), out why) &&
                f.Client.Sends == 1 && object.ReferenceEquals(original, f.Callback(355)) && f.Callback(301).Method.Name == "sc_donate",
                "pending_transport_break_disarms_restores_and_never_repeats");
        }

        f = new Fixture(); f.DonatePanel(); original = f.Callback(355);
        f.Run("donate", 2);
        f.Client.m_client = new game.net.tcp_client.Socket { Connected = true }; f.Client.m_stream = new MemoryStream();
        f.Client.Receive(301, Donate(2)); f.Client.Receive(355, Push(20)); f.Capture();
        Check(Status(f.Model, "UNKNOWN") && f.Model.Json().Contains("\"receipt_opcode\":0") && f.Role.money == 0 &&
            object.ReferenceEquals(original, f.Callback(355)), "stale_transport_payload_is_forwarded_but_never_attributed");

        f = new Fixture(); f.DonatePanel(); f.Run("donate", 2);
        RootBase.Bind(f.Client); game.net.tcp_base.Handler foreign = (game.net.tcp_base.Handler)f.Client.m_handlers[(ushort)355];
        f.Capture();
        Check(Status(f.Model, "UNKNOWN") && object.ReferenceEquals(foreign, f.Client.m_handlers[(ushort)355]) &&
            f.Callback(301).Method.Name == "sc_donate", "foreign_handler_is_preserved_when_owned_capture_is_removed");

        f = new Fixture(); f.DonatePanel(); original = f.Callback(355); f.Run("donate", 2);
        Fixture blocked = f;
        using (ManualResetEvent entered = new ManualResetEvent(false))
        using (ManualResetEvent release = new ManualResetEvent(false))
        {
            Thread owner = new Thread(delegate() { lock (blocked.Client) { entered.Set(); release.WaitOne(); } });
            owner.IsBackground = true; owner.Start();
            try
            {
                Check(entered.WaitOne(2000), "transport_monitor_held_by_worker");
                f.Client.m_bConnectLost = true; f.Capture();
                Check(Status(f.Model, "UNKNOWN") && !object.ReferenceEquals(original, f.Callback(355)), "busy_cleanup_disarms_and_defers_restoration");
            }
            finally { release.Set(); owner.Join(2000); }
        }
        f.Client.Receive(355, Push(20)); f.Capture();
        Check(object.ReferenceEquals(original, f.Callback(355)) && f.Model.Json().Contains("\"progress_after\":null") && f.Client.Sends == 1,
            "deferred_cleanup_forwards_without_observing_and_restores_original_on_next_frame");
    }

    private static void CacheAndClose()
    {
        Fixture f = new Fixture(); string why;
        f.DonatePanel();
        Command c = f.Command("donate", 2);
        Check(f.Model.Queue(c, out why), "cache_replacement_queued");
        f.ReplaceCaches(); f.Capture();
        Check(!f.Model.ExecuteGui(c, delegate { }, out why) && c.ExpectedRevision != f.Model.Revision && f.Client.Sends == 0,
            "equal_values_in_replaced_cache_need_fresh_gui_admission");
        f.Run("donate", 2); f.ReplaceCaches();
        f.Client.Receive(301, Donate(2)); f.Client.Receive(355, Push(20)); f.Capture();
        Check(Status(f.Model, "COMPLETED"), "trusted_cache_replacement_can_settle_own_request");
        f.Run("close", 2); Check(!f.Model.Busy, "owned_close_after_replaced_cache");

        f = new Fixture(); f.DonatePanel(); MainUI.SetSubUI(null); f.Capture();
        Check(Status(f.Model, "REJECTED") && !f.Model.Busy && f.Client.Sends == 0, "passive_close_before_request_no_spend");
        foreach (bool settled in new bool[] { false, true })
        {
            f = new Fixture(); f.DonatePanel(); game.net.tcp_base.Callback original = f.Callback(355);
            f.Run("donate", 2);
            if (settled) { f.Client.Receive(301, Donate(2)); f.Client.Receive(355, Push(20)); f.Capture(); }
            f.ReplaceCaches(); MainUI.SetSubUI(null); f.Capture();
            Check(Status(f.Model, "UNKNOWN") && f.Model.Unresolved && f.Model.Busy &&
                f.Client.Sends == 1 && object.ReferenceEquals(original, f.Callback(355)), "passive_close_after_spend_never_counts_as_owned");
        }
    }

    private static void LotteryBoardAndMembership()
    {
        Fixture f = new Fixture(); string why;
        f.Run("open", 0); f.Run("lottery_panel", 0);
        Database.Guild updated = new Database.Guild { level = 5 };
        f.Role.guilds[f.Role.idguild] = updated; f.Ui.char_guild = updated; f.Capture();
        Check(!f.Model.Queue(f.Command("lottery", 0), out why) && f.Client.Sends == 0, "old_board_blocks_new_draw_after_guild_level_change");
        f.Run("close", 0);

        f = new Fixture(); f.Ui.ShowLotteryReward = true;
        f.Run("open", 0); f.Run("lottery_panel", 0); f.Run("lottery", 0);
        ArrayList ids = f.Ui.ids, lots = f.Ui.lots;
        object firstPrize = lots[0];
        updated = new Database.Guild { level = 5 };
        f.Role.guilds[f.Role.idguild] = updated; // 278 updates the dictionary before GuildUI.Web_OnGUI refreshes its field.
        f.Client.Receive(302, new byte[] { 8,1 }); f.Client.Receive(355, Push(18)); f.Capture();
        Check(Status(f.Model, "COMPLETED") && !f.Model.Queue(f.Command("reward_confirm", 0), out why), "level_up_receipt_settles_but_stale_ui_cache_blocks_confirmation");
        f.Ui.char_guild = updated; f.Capture();
        f.Run("reward_confirm", 0); f.Run("close", 0);
        Check(!f.Model.Busy && f.Client.Sends == 1 && f.Ui.guild_level == 4 && object.ReferenceEquals(ids, f.Ui.ids) &&
            object.ReferenceEquals(lots, f.Ui.lots) && object.ReferenceEquals(firstPrize, f.Ui.lots[0]) && (int)ids[0] == 1,
            "bound_reward_and_close_after_level_up_without_rewriting_prize_board");

        foreach (bool kicked in new bool[] { false, true })
        {
            f = new Fixture(); f.DonatePanel(); f.Run("donate", 2);
            if (kicked) Database.Guild.kicked = true; else f.Role.idguild = 100;
            f.Client.Receive(301, Donate(2)); f.Client.Receive(355, Push(20));
            Database.Guild.kicked = false; f.Role.idguild = 99; f.Capture();
            Check(Status(f.Model, "UNKNOWN") && f.Model.Unresolved && f.Model.Json().Contains("\"receipt_opcode\":0") &&
                f.Model.Json().Contains("\"progress_after\":null") && f.Client.Sends == 1 && f.Role.money == 0,
                "membership_loss_at_callback_prevents_attribution_even_if_next_frame_looks_valid");
        }
    }

    private static void AmbiguousResponse()
    {
        string why;
        foreach (int type in new int[] { 0, 2 })
        {
            ushort op = type == 0 ? (ushort)302 : (ushort)301;
            Fixture f = new Fixture();
            f.Run("open", type); f.Run(type == 0 ? "lottery_panel" : "donate_panel", type); f.Run(type == 0 ? "lottery" : "donate", type);
            byte[] negative = type == 0 ? new byte[] { 8,255,1 } : new byte[] { 8,0,16,0,24,0,32,0,40,0,48,0,56,0 };
            f.Client.Receive(op, negative); f.Capture();
            Check(Status(f.Model, "UNKNOWN") && f.Model.Unresolved && f.Ui.ResponseCalls == 1 &&
                !f.Model.Queue(f.Command(type == 0 ? "lottery" : "donate", type), out why) && f.Client.Sends == 1,
                "bare_negative_never_proves_own_request_was_rejected");

            f = new Fixture();
            f.Run("open", type); f.Run(type == 0 ? "lottery_panel" : "donate_panel", type); f.Run(type == 0 ? "lottery" : "donate", type);
            byte[] positive = type == 0 ? new byte[] { 8,1 } : Donate(type);
            f.Client.Queue(op, positive); f.Client.Queue(op, positive); f.Client.Queue(355, Push(type == 0 ? (byte)18 : (byte)20));
            f.Client.Run(); f.Capture();
            Check(Status(f.Model, "UNKNOWN") && f.Model.Unresolved && f.Ui.ResponseCalls == 1 && f.Client.Sends == 1 &&
                f.Model.Json().Contains("\"receipt_opcode\":0"), "queued_duplicate_is_seen_before_one_shot_handler_is_consumed");
        }
    }

    private static int Main()
    {
        string wire = "version=3\nnonce=" + Nonce + "\nsequence=1\ncommand=daily-guild-command\naction=donate\nexpected_revision=1\ncharacter_id=42\nguild_id=99\ntask_id=20\noperation_id=" + Nonce + "\ndonation_type=3\nmax_premium=80\n";
        Check(Command.Parse(wire, Nonce, 0) != null && Command.Parse(wire.Replace("max_premium=80", "max_premium=0"), Nonce, 0) == null &&
            Command.Parse(wire.Replace("donation_type=3", "donation_type=2"), Nonce, 0) == null &&
            Command.Parse(wire.Replace("task_id=20", "task_id=19"), Nonce, 0) == null &&
            Command.Parse(wire.Replace("action=donate", "action=GoldDonateOK"), Nonce, 0) == null, "wire_scope");
        foreach (int type in new int[] { 1, 2, 3 })
        {
            Fixture f = new Fixture();
            f.Run("open", type); f.Run("donate_panel", type); f.Run("donate", type);
            if (type == 3)
            {
                Check(f.Client.Sends == 0 && f.Premium.gold == 80, "premium_quote_before_send");
                f.Premium.gold = 81;
                string why;
                Check(!f.Model.Queue(f.Command("premium_confirm", 3), out why) && f.Client.Sends == 0, "premium_tampered_quote");
                f.Premium.gold = 80;
                f.Run("premium_confirm", 3);
            }
            Check(f.Ui.SentType == type && f.Client.Sends == 1, "fixed_type");
            f.Client.Receive(301, Donate(type)); f.Capture();
            Check(Status(f.Model, "PENDING"), "receipt_does_not_complete_without_progress");
            f.Client.Receive(355, Push(18)); f.Capture();
            Check(Status(f.Model, "PENDING"), "foreign_task_push_does_not_complete");
            f.Client.Receive(355, Push(20)); f.Capture();
            Check(Status(f.Model, "COMPLETED") && f.Role.bind_gold == 9, "own_301_and_355");
            f.Run("close", type); Check(!f.Model.Busy, "close");
        }
        {
            Fixture f = new Fixture();
            f.Run("open", 0); f.Run("lottery_panel", 0); f.Run("lottery", 0);
            f.Client.Receive(355, Push(18)); f.Capture();
            Check(Status(f.Model, "PENDING"), "progress_does_not_complete_without_receipt");
            f.Client.Receive(302, new byte[] { 8,1 }); f.Capture();
            Check(Status(f.Model, "COMPLETED") && f.Member.contribution == 0 && f.Member.guild_lottery_count == 0 && f.Role.gold == 80, "own_302_and_355");
        }
        {
            Fixture f = new Fixture();
            f.Role.money = 90000; f.Role.money64 = 5000000000L; f.Capture();
            f.Run("open", 2); f.Run("donate_panel", 2); f.Run("donate", 2);
            f.Client.Receive(301, Donate(2)); f.Client.Receive(355, Push(20)); f.Capture();
            Check(Status(f.Model, "COMPLETED") && f.Role.money == 0 &&
                  f.Role.money64 == 4999990000L, "mixed_64bit_wallet_setter");
        }
        {
            Fixture f = new Fixture(); string why;
            Command open = f.Command("open", 2); open.ExpectedRevision--;
            Check(!f.Model.Queue(open, out why), "stale_revision");
            open = f.Command("open", 2); open.GuildCharacterId = 41;
            Check(!f.Model.Queue(open, out why), "wrong_role");
            f.Role.gold = 79; f.Capture();
            Check(!f.Model.Queue(f.Command("open", 3), out why) && why == "guild_premium_insufficient", "paid_balance");
            f.Run("open", 2); f.Run("donate_panel", 2);
            Check(!f.Model.Queue(f.Command("donate", 1), out why), "immutable_tier");
            Command c = f.Command("donate", 2);
            Check(f.Model.Queue(c, out why), "cancel_queue");
            try { f.Model.ExecuteGui(c, delegate { throw new OperationCanceledException(); }, out why); }
            catch (OperationCanceledException) { f.Model.NotInvoked(); }
            Check(f.Client.Sends == 0 && !f.Model.MutationAttempted && !f.Model.Unresolved, "cancel_before_send");
            f.Run("donate", 2);
            f.Client.Receive(301, Donate(3)); f.Capture();
            Check(Status(f.Model, "UNKNOWN") && f.Model.Unresolved, "wrong_receipt_never_completes");
            Check(!f.Model.Queue(f.Command("donate", 2), out why) && f.Client.Sends == 1, "unknown_never_retries");
        }
        ActivityRefreshCoherence(); RefreshBoundaries(); TransportAndCleanup(); CacheAndClose(); LotteryBoardAndMembership(); AmbiguousResponse();
        Console.WriteLine("{\"status\":\"PASS\",\"checks\":" + checks + ",\"scope\":\"guild wire and costs, fixed callbacks, dual-cache refresh at preflight and publication, transport, cleanup and unknown\"}");
        return 0;
    }
}
