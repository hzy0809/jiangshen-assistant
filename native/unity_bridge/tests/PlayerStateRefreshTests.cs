// Detached targeted fixtures: compile with production SharedDevelopmentContext
// and PlayerStateRefreshModel only. No installed game assembly is loaded.
using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using JiangShen.AssistantRuntime;

namespace UnityEngine
{
    public class Object { public int GetInstanceID() { return 1; } }
    public class Transform { public Transform root; }
    public class GameObject { public bool activeInHierarchy = true; public Transform transform = new Transform(); public GameObject() { transform.root = transform; } }
    public class Component : Object { public GameObject gameObject; public Transform transform { get { return gameObject.transform; } } }
    public class Behaviour : Component { public bool enabled = true; }
    public class Event { public static Event current = new Event(); }
    public static class GUI { public static bool enabled = true; }
    public static class Time { public static int frameCount; }
}
namespace JiangShen.AssistantRuntime
{
    internal sealed class ProbeFailure : Exception { internal ProbeFailure(string message) : base(message) { } }
    internal static class Model
    {
        internal static Type FirstType(string name)
        {
            if (name == "game.net.tcp_base+Handler") return typeof(RefreshHandler);
            if (name == "game.net.tcp_base" || name == "game.net.tcp_client") return typeof(RefreshClient);
            if (name == "Database.Character") return typeof(RefreshCharacter);
            return GameType(name);
        }
        internal static Type GameType(string name) { Type type = Type.GetType(name); if (type == null) throw new Exception(name); return type; }
    }
    internal static class Command
    {
        internal static string Quote(string s)
        { if (s == null) return "null"; return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n") + "\""; }
    }
}
public class BaseAsset : UnityEngine.Behaviour { public UnityEngine.Component rmg; public bool loading, collecting, m_bInit = true; }
public class BaseUI : BaseAsset
{
    public static RootUI rootUI;
    public bool visible, interactive = true, first;
    public bool IsVisible() { return visible; }
    public bool IsEnable() { return interactive; }
}
public class RootMessenger : UnityEngine.Component { public string LevelID = "Campaign"; public bool bLoading; public int ExpectingCount; }
public class RootUI : BaseUI
{
    public static RefreshClient m_client;
    public static bool m_bRefreshFive;
    public static void BindClock()
    { m_client.m_handlers[(ushort)36] = new RefreshHandler { life = -1f, handler = sc_player_refresh_fiveclock_0x24 }; }
    private static void sc_player_refresh_fiveclock_0x24(MemoryStream stream, object arg)
    {
        m_bRefreshFive = false;
        if (stream == null) return;
        PlayerStateServerSnapshot.Reader r = new PlayerStateServerSnapshot.Reader(stream.ToArray(), 0, (int)stream.Length);
        int wire;
        if (r.Tag(out wire) != 1) throw new FormatException(); uint result = (uint)r.Varint();
        if (r.Tag(out wire) != 2) throw new FormatException(); Global.character_.next_resource_time = (uint)r.Varint();
        if (result != 0) return;
        foreach (Database.Activity row in Global.character_.acts.Values) row.condition_param = 0;
    }
}
public class MainUI : BaseUI { public static object m_subui; }
public static class Global { public static RefreshCharacter character_; public static RefreshClient m_client; public static bool extern_enabled = true, relogin; }
public static class TimeUtil { public static uint _now = 218100, _thisDay = 200000; public static int server_time_zone = -28800; }
namespace Database
{
    public class Spirit { public byte level; }
    public class Activity
    {
        public byte idactivity, condition_param;
        public int refresh_time = 700;
        public Template temp;
        public Activity(byte id = 20, uint progress = 1, int required = 3)
        {
            idactivity = id; condition_param = unchecked((byte)progress);
            temp = new Template { id = id, cond_param = required };
            if (condition_param > temp.cond_param) condition_param = (byte)temp.cond_param;
            else if (_check_act_closecondition()) condition_param = (byte)temp.cond_param;
        }
        public bool _check_act_closecondition()
        {
            if (idactivity == 6) return Global.character_.spirit != null && Global.character_.spirit.level == 100;
            if (idactivity == 17) return Global.character_.isGraveEnd();
            return false;
        }
    }
    public class Template { public byte id = 20, idreward = 5; public bool active = true; public int cond_param; }
    public class Activity_Reward { public byte idact_reward = 5; public uint refresh = 304400; public bool finished_flag = true; public Hashtable acts = new Hashtable(); }
    public class Guild { public ulong id = 90; public bool active = true; public ushort level = 3; }
    public class GuildMember { public ulong id = 55290000000275, idguild = 90; public uint contribution = 900, donate_refresh = 218050; public byte guild_lottery_count = 2, today_donate = 2; }
}
public class RefreshMain { public int id = 1, idequipment_type = 1; }
public class RefreshEquipment { public int id = 1, enchant_level = 1, idgeneral_template = 22, idequipment_main = 1, idequipment_acc1, idequipment_acc2; public RefreshMain main_ = new RefreshMain(); }
public class RefreshGeneral { public int idgeneral_template = 22; public object[] equipments = new object[2]; }
public class RefreshFormation { public ArrayList spotgen = new ArrayList(new object[] { 22 }); }
public class RefreshCharacter
{
    public ulong id = 55290000000275;
    public ulong idguild = 90;
    public uint next_resource_time = 304400;
    public Database.Spirit spirit = new Database.Spirit();
    public bool graveEnd;
    public bool isGraveEnd() { return graveEnd; }
    public bool gm;
    public int cur_form = 1;
    public long money = 56930128, money64, enchant_stone = 19973;
    public Hashtable equipments = new Hashtable(), form_generals_ = new Hashtable(), formations_ = new Hashtable();
    public Hashtable acts = new Hashtable(), act_rewards = new Hashtable(), guilds = new Hashtable(), members = new Hashtable();
    public RefreshCharacter()
    {
        RefreshEquipment e = new RefreshEquipment(); RefreshGeneral g = new RefreshGeneral(); g.equipments[0] = e;
        equipments[(byte)1] = e; form_generals_[(ushort)22] = g; formations_[1] = new RefreshFormation();
        Database.Activity_Reward tab = new Database.Activity_Reward(); act_rewards[5u] = tab;
        tab.acts[20u] = acts[20u] = new Database.Activity();
        guilds[idguild] = new Database.Guild(); members[id] = new Database.GuildMember();
    }
}
public class RefreshHandler
{
    public delegate void Callback(MemoryStream stream, object arg);
    public Callback handler;
    public object arg;
    public float life = 10f;
}
public class RefreshClient
{
    public sealed class QueuedResponse { public ushort op; }
    public sealed class Socket { public bool Connected { get; set; } }
    public Socket m_client = new Socket { Connected = true };
    public Stream m_stream = new MemoryStream();
    public static int connection_state = 3;
    public bool m_bConnectLost;
    public Hashtable m_handlers = new Hashtable();
    public ArrayList messages = new ArrayList();
    public int RefreshSends;
    public void NormalRefresh(LoginUI login)
    {
        RefreshSends++;
        RefreshHandler h = new RefreshHandler(); h.arg = null;
        h.handler = (RefreshHandler.Callback)Delegate.CreateDelegate(typeof(RefreshHandler.Callback), login,
            typeof(LoginUI).GetMethod("sc_playerinfo", BindingFlags.NonPublic | BindingFlags.Instance));
        m_handlers[(ushort)16] = h;
    }
    public void Deliver(MemoryStream stream)
    { RefreshHandler h = (RefreshHandler)m_handlers[(ushort)16]; m_handlers.Remove((ushort)16); h.handler(stream, h.arg); }
    public void DeliverClock(byte[] bytes)
    {
        RefreshHandler h = (RefreshHandler)m_handlers[(ushort)36];
        using (MemoryStream stream = bytes == null ? null : new MemoryStream(bytes, false)) h.handler(stream, h.arg);
    }
}
public class LoginUI : BaseUI
{
    public int RefreshCalls, CallbackCalls, NormalPingCalls, NormalBonusCalls, NormalNextLevelCalls;
    public bool Refuse, DelayRebuild, NoReplacement, Throw, Bonus, ObserveInside, NoHandler;
    public string InsideStatus;
    public bool ReentrantEntryBlocked;
    public Action AfterRebuild;
    public int RebuiltLevel = 1;
    public string NextRoot = "Campaign";
    public MemoryStream ReceivedStream;
    public object ReceivedArg;
    public long ReceivedPosition;
    public bool Refresh()
    { RefreshCalls++; if (Refuse) return false; if (!NoHandler) RootUI.m_client.NormalRefresh(this); return true; }
    private void sc_playerinfo(MemoryStream stream, object arg)
    {
        CallbackCalls++; ReceivedStream = stream; ReceivedArg = arg; ReceivedPosition = stream == null ? -1 : stream.Position;
        if (stream == null) return;
        if (!DelayRebuild) CompleteRebuild();
        if (ObserveInside)
        {
            RefreshFixture f = RefreshFixture.Current; f.Capture(); InsideStatus = f.Model.Status;
            PlayerStateRefreshModel other = new PlayerStateRefreshModel(); other.Capture(f.Context()); string why;
            ReentrantEntryBlocked = !other.Queue(f.Command, out why);
        }
        NormalNextLevelCalls++; NormalPingCalls++; if (Bonus) NormalBonusCalls++;
        if (Throw) throw new InvalidOperationException("normal marker");
    }
    public void CompleteRebuild()
    {
        if (!NoReplacement) Global.character_ = new RefreshCharacter();
        ((RefreshEquipment)Global.character_.equipments[(byte)1]).enchant_level = RebuiltLevel;
        RefreshFixture.Current.Root.LevelID = NextRoot;
        if (AfterRebuild != null) AfterRebuild();
    }
}
public class EquipmentUI : BaseUI { }
public class EquipmentReinforceUI : BaseUI { }
public class UnknownOverlayUI : BaseUI { }
internal sealed class RefreshFixture
{
    internal static RefreshFixture Current;
    internal readonly RefreshClient Client = new RefreshClient();
    internal readonly PlayerStateRefreshModel Model;
    internal readonly RootMessenger Root, GlobalRoot;
    internal readonly RootUI GlobalUi;
    internal readonly MainUI Main;
    internal readonly LoginUI Login;
    internal UnityEngine.Object[] All;
    internal string Gate = "PASS";
    internal readonly DevelopmentCommand Command = new DevelopmentCommand { Action = "player_state_refresh", OperationId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Sequence = 7, ExpectedRevision = 9, TargetId = 0, DesiredLevel = 0, MaxRequests = 0, MaxMoney = 0, MaxStone = 0, MaxFood = 0 };
    internal RefreshFixture(bool daily = false)
    {
        DailyRefreshProof.Stop();
        TimeUtil._now = 218100; TimeUtil._thisDay = 200000; TimeUtil.server_time_zone = -28800;
        RootUI.m_bRefreshFive = Global.relogin = false; RefreshClient.connection_state = 3;
        Model = new PlayerStateRefreshModel(daily);
        if (daily) Command.Action = "daily_state_refresh";
        Current = this; UnityEngine.Time.frameCount = 1; UnityEngine.Event.current = new UnityEngine.Event(); UnityEngine.GUI.enabled = true; Global.extern_enabled = true;
        Root = Attach(new RootMessenger(), new UnityEngine.GameObject()); GlobalRoot = Attach(new RootMessenger(), new UnityEngine.GameObject());
        GlobalUi = Attach(new RootUI(), GlobalRoot.gameObject); GlobalUi.rmg = GlobalRoot;
        Login = Attach(new LoginUI(), GlobalRoot.gameObject); Login.rmg = GlobalRoot;
        Main = Attach(new MainUI(), Root.gameObject); Main.rmg = Root; Main.visible = true;
        BaseUI.rootUI = GlobalUi; Global.character_ = new RefreshCharacter(); RootUI.m_client = Global.m_client = Client; MainUI.m_subui = null;
        EquipmentUI equipment = Attach(new EquipmentUI(), GlobalRoot.gameObject); equipment.rmg = GlobalRoot;
        EquipmentReinforceUI reinforce = Attach(new EquipmentReinforceUI(), GlobalRoot.gameObject); reinforce.rmg = GlobalRoot;
        All = new UnityEngine.Object[] { GlobalUi, Login, Main, equipment, reinforce }; Capture();
    }
    private static T Attach<T>(T c, UnityEngine.GameObject go) where T : UnityEngine.Component { c.gameObject = go; return c; }
    internal SharedDevelopmentContext Context()
    { return new SharedDevelopmentContext(All, Root, GlobalUi, Global.character_, Global.character_.id.ToString(), Gate, false); }
    internal void Capture() { Model.Capture(Context()); }
    internal bool Begin(out string why)
    { if (!Model.Queue(Command, out why)) return false; return Model.ExecuteGui(Command, delegate { PlayerStateRefreshTests.Check(Login.RefreshCalls == 0 && Model.Pending && Model.MutationAttempted, "intent_precedes_normal_refresh"); }, out why); }
    internal void Deliver(byte[] bytes) { Client.Deliver(bytes == null ? null : new MemoryStream(bytes, false)); }
    internal void Settle() { Capture(); UnityEngine.Time.frameCount++; Capture(); }
}
internal static class PlayerStateRefreshTests
{
    private static int checks;
    private static readonly ArrayList names = new ArrayList();
    internal static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL " + name); checks++; names.Add(Command.Quote(name)); }
    private static byte[] Join(params byte[][] chunks)
    { MemoryStream s = new MemoryStream(); foreach (byte[] c in chunks) s.Write(c, 0, c.Length); return s.ToArray(); }
    private static byte[] Var(ulong value)
    { MemoryStream s = new MemoryStream(); while (value >= 128) { s.WriteByte((byte)((value & 127) | 128)); value >>= 7; } s.WriteByte((byte)value); return s.ToArray(); }
    private static byte[] UInt(int f, ulong v) { return Join(Var((ulong)(f << 3)), Var(v)); }
    private static byte[] Signed(int f, long v) { return UInt(f, unchecked((ulong)((v << 1) ^ (v >> 63)))); }
    private static byte[] Message(int f, byte[] v) { return Join(Var((ulong)(f << 3 | 2)), Var((ulong)v.Length), v); }
    private static byte[] Equipment(int id, int level, int owner)
    { return Join(UInt(1, (ulong)id), UInt(2, 1), UInt(3, 0), UInt(4, 0), UInt(6, (ulong)level), UInt(7, (ulong)owner)); }
    private static byte[] Player(ulong id) { return Join(UInt(1, id), Signed(23, 56930128), Signed(33, 19973), Signed(270, 0)); }
    private static byte[] Response() { return Join(Message(3, Player(55290000000275)), UInt(8, 1), Message(13, Equipment(1, 1, 22))); }
    private static byte[] DailyResponse(ulong role = 55290000000275, uint time = 218100, uint next = 304400, uint progress = 1)
    {
        byte[] guild = Message(2, Message(1, Join(UInt(1, 90), UInt(5, 1), UInt(7, 3))));
        byte[] member = Message(3, Join(Message(1, Join(UInt(1, 90), UInt(2, 900), UInt(4, 2), UInt(5, 2), UInt(8, 218050))), UInt(3, 55290000000275), UInt(4, 0)));
        return Join(UInt(1, time), Signed(2, -28800), Message(3, Join(UInt(1, role), UInt(92, next))),
            Message(15, Join(UInt(1, 20), UInt(2, progress), Signed(3, 700))), Message(17, Join(UInt(1, 90), guild, member)),
            Message(48, Join(UInt(1, 5), UInt(2, 304400), UInt(3, 1))));
    }
    private static string DailyGate()
    { uint now, day, cycle; return DailyRefreshGate.Read(Global.character_, out now, out day, out cycle); }

    private static byte[] ActivityRow(byte id, uint progress)
    { return Message(15, Join(UInt(1, id), UInt(2, progress), Signed(3, 700))); }

    private static Database.Activity ActivityFromGame(byte id, uint progress, int required)
    {
        Database.Activity row = new Database.Activity(id, progress, required);
        RefreshCharacter role = Global.character_;
        ((Database.Activity_Reward)role.act_rewards[5u]).acts[(uint)id] = role.acts[(uint)id] = row;
        return row;
    }

    private static void DailyActivityProjection()
    {
        string why;
        byte[] body = Join(DailyResponse(progress: 9), ActivityRow(6, 0), ActivityRow(17, 0), ActivityRow(21, 257));
        foreach (bool updateAfterClosure in new bool[] { false, true })
        {
            RefreshFixture f = new RefreshFixture(true);
            f.Login.AfterRebuild = delegate
            {
                // LoginUI constructs activity rows before loading spirit and graves.
                ActivityFromGame(20, 9, 2); ActivityFromGame(21, 257, 3);
                Database.Activity spirit = ActivityFromGame(6, 0, 3), grave = ActivityFromGame(17, 0, 5);
                Check(spirit.condition_param == 0 && grave.condition_param == 0, "daily_login_builds_rows_before_closure_is_loaded");
                Global.character_.spirit.level = 100; Global.character_.graveEnd = true;
                if (updateAfterClosure)
                {
                    // The normal 355 handler also constructs Activity from wire.
                    spirit = ActivityFromGame(6, 0, 3); grave = ActivityFromGame(17, 0, 5);
                    Check(spirit.condition_param == 3 && grave.condition_param == 5, "daily_355_constructor_marks_closed_rows_complete");
                }
            };
            Check(f.Begin(out why), "daily_normalized_activity_fixture_sent"); f.Deliver(body); f.Settle();
            Check(f.Model.Status == "OBSERVED" && DailyGate() == "" &&
                ((Database.Activity)Global.character_.acts[20u]).condition_param == 2 &&
                ((Database.Activity)Global.character_.acts[21u]).condition_param == 1 &&
                ((Database.Activity)Global.character_.acts[6u]).condition_param == (updateAfterClosure ? 3 : 0) &&
                ((Database.Activity)Global.character_.acts[17u]).condition_param == (updateAfterClosure ? 5 : 0),
                "daily_16_matches_actual_clamp_and_closed_row_timing_without_editing_cache");
        }
        {
            RefreshFixture f = new RefreshFixture(true);
            f.Login.AfterRebuild = delegate { ActivityFromGame(20, 9, 2); ActivityFromGame(21, 257, 3); ActivityFromGame(6, 0, 3).condition_param = 3; ActivityFromGame(17, 0, 5); };
            Check(f.Begin(out why), "daily_false_completion_fixture_sent"); f.Deliver(body); f.Settle();
            Check(f.Model.Status == "UNKNOWN" && DailyGate() == "daily_refresh_unverified" && ((Database.Activity)Global.character_.acts[6u]).condition_param == 3,
                "daily_completion_with_open_predicate_is_not_proof_and_is_never_rewritten");
        }
    }

    private static void DailySnapshotProof()
    {
        string why;
        RefreshFixture f = new RefreshFixture(true);
        Check(DailyGate() == "daily_refresh_unverified", "daily_future_timestamp_without_witness_blocked");
        Check(f.Begin(out why), "daily_fixed_refresh_invoked"); f.Deliver(DailyResponse());
        f.Capture(); Check(DailyGate() == "daily_refresh_unverified", "daily_original_callback_not_sufficient_before_stable_main");
        f.Settle();
        Check(f.Model.Status == "OBSERVED" && DailyGate() == "" && f.Client.RefreshSends == 1 &&
            f.Model.Json().Contains("\"server_cycle\":218000") && !f.Model.Json().Contains("base64"), "daily_full_16_proof_only_after_rebuild_and_two_frames");
        Check(!f.Model.Queue(f.Command, out why), "daily_refresh_once_per_owned_session");
        f.Client.m_stream = new MemoryStream();
        Check(DailyGate() == "daily_refresh_unverified", "daily_proof_invalid_after_stream_replacement");

        byte[][] invalid = new byte[][] { Response(), DailyResponse(role: 9), DailyResponse(time: 217999), DailyResponse(time: 218120),
            DailyResponse(next: 404400), DailyResponse(progress: 2), Join(DailyResponse(), Message(48, UInt(1, 5))) };
        for (int i = 0; i < invalid.Length; i++)
        {
            f = new RefreshFixture(true); Check(f.Begin(out why), "daily_invalid_response_fixture_sent");
            f.Deliver(invalid[i]); f.Settle();
            Check(f.Model.Status == "UNKNOWN" && DailyGate() == "daily_refresh_unverified", "daily_bad_16_fails_closed_" + i);
        }
        f = new RefreshFixture(true); Check(f.Begin(out why), "daily_socket_swap_fixture_sent");
        f.Client.m_stream = new MemoryStream(); f.Deliver(DailyResponse()); f.Settle();
        Check(f.Model.Status == "UNKNOWN" && f.Login.CallbackCalls == 1 && DailyGate() == "daily_refresh_unverified", "daily_old_16_after_reconnect_not_attributed");
        f = new RefreshFixture(true); Check(f.Begin(out why), "daily_duplicate_16_fixture_sent");
        f.Client.messages.Add(new RefreshClient.QueuedResponse { op = 16 }); f.Deliver(DailyResponse()); f.Client.messages.Clear(); f.Settle();
        Check(f.Model.Status == "UNKNOWN" && f.Login.CallbackCalls == 1 && DailyGate() == "daily_refresh_unverified", "daily_duplicate_16_not_attributed_and_original_forwarded");
        f = new RefreshFixture(true); Check(f.Begin(out why), "daily_role_data_mismatch_fixture_sent");
        f.Deliver(DailyResponse()); ((Database.GuildMember)Global.character_.members[Global.character_.id]).contribution++;
        f.Settle(); Check(f.Model.Status == "UNKNOWN" && DailyGate() == "daily_refresh_unverified", "daily_16_new_guild_member_cache_must_match");
        f = new RefreshFixture(true); f.Login.Throw = true; Check(f.Begin(out why), "daily_throw_fixture_sent");
        try { f.Deliver(DailyResponse()); } catch (InvalidOperationException) { }
        f.Settle(); Check(f.Model.Status == "UNKNOWN" && DailyGate() == "daily_refresh_unverified", "daily_original_callback_must_return");
        f = new RefreshFixture(true); MainUI.m_subui = new object(); f.Capture();
        Check(!f.Model.Queue(f.Command, out why) && f.Client.RefreshSends == 0, "daily_no_refresh_over_foreign_panel");
        f = new RefreshFixture(true); RootUI.m_bRefreshFive = true; f.Capture();
        Check(!f.Model.Queue(f.Command, out why) && f.Client.RefreshSends == 0, "daily_no_refresh_during_36");
        f = new RefreshFixture(true); Check(f.Model.Queue(f.Command, out why), "daily_publish_boundary_fixture_queued");
        Check(!f.Model.ExecuteGui(f.Command, delegate { f.Client.m_stream = new MemoryStream(); }, out why) && f.Client.RefreshSends == 0,
            "daily_no_send_when_transport_changes_at_publish");
        f = new RefreshFixture(true); Check(f.Model.Queue(f.Command, out why), "daily_stop_fixture_queued");
        f.Model.NotInvoked("stop");
        Check(!f.Model.ExecuteGui(f.Command, delegate { }, out why) && f.Client.RefreshSends == 0, "daily_stop_consumes_admission_before_gui");
    }

    private static void DailyNaturalClockProof()
    {
        RefreshFixture f = new RefreshFixture(true); RootUI.BindClock();
        RefreshHandler.Callback original = ((RefreshHandler)f.Client.m_handlers[(ushort)36]).handler;
        Check(original.Method.Name == "sc_player_refresh_fiveclock_0x24", "clock_callback_original_before_explicit_observation");
        DailyRefreshProof.ObserveNatural();
        Check(!object.ReferenceEquals(original, ((RefreshHandler)f.Client.m_handlers[(ushort)36]).handler), "explicit_observation_wraps_real_callback");
        Global.character_.next_resource_time = 218000; RootUI.m_bRefreshFive = true;
        f.Client.DeliverClock(Join(UInt(1, 1), UInt(2, 304400))); DailyRefreshProof.ObserveNatural();
        Check(DailyGate() == "daily_refresh_unverified" && ((Database.Activity)Global.character_.acts[20u]).condition_param == 1,
            "daily_nonzero_36_future_timestamp_not_a_proof");
        Global.character_.next_resource_time = 218000; RootUI.m_bRefreshFive = true;
        f.Client.DeliverClock(Join(UInt(1, 0), UInt(2, 304400)));
        Check(DailyGate() == "daily_refresh_unverified", "daily_36_cannot_publish_before_return_witness_is_consumed");
        DailyRefreshProof.ObserveNatural();
        Check(DailyGate() == "" && ((Database.Activity)Global.character_.acts[20u]).condition_param == 0 && f.Client.RefreshSends == 0,
            "daily_successful_owned_natural_36_is_independent_proof_without_send");
        DailyRefreshProof.Stop();
        Check(DailyGate() == "daily_refresh_unverified" && object.ReferenceEquals(original, ((RefreshHandler)f.Client.m_handlers[(ushort)36]).handler),
            "daily_natural_observer_restores_original_on_stop");
        DailyRefreshProof.ObserveNatural();
        using (ManualResetEvent locked = new ManualResetEvent(false))
        using (ManualResetEvent release = new ManualResetEvent(false))
        {
            Thread holder = new Thread(delegate() { lock (f.Client) { locked.Set(); release.WaitOne(); } });
            holder.IsBackground = true; holder.Start();
            try
            {
                Check(locked.WaitOne(2000), "clock_observer_transport_monitor_fixture_held");
                Check(!DailyRefreshProof.Stop(), "clock_observer_does_not_report_restored_with_busy_transport");
            }
            finally { release.Set(); holder.Join(2000); }
        }
        Check(DailyRefreshProof.Stop() && object.ReferenceEquals(original, ((RefreshHandler)f.Client.m_handlers[(ushort)36]).handler),
            "clock_observer_stop_retry_restores_exact_callback_after_lock_release");
    }
    private static void Parser()
    {
        PlayerStateServerSnapshot s = PlayerStateServerSnapshot.Decode(Response());
        Check(s.Error == null && s.CharacterId == "55290000000275" && s.Equipments.Count == 1 && s.Money == 56930128 && s.Stone == 19973, "positive_sc16_fixed_schema_projection");
        byte[][] invalid = new byte[][] { null, new byte[0], new byte[1048577], UInt(8, 1), Join(Message(3, Player(1)), Message(3, Player(1)), UInt(8, 1), Message(13, Equipment(1, 1, 22))),
            Join(Message(3, Player(1)), UInt(8, 1), Message(13, Equipment(1, 1, 22)), Message(13, Equipment(1, 1, 22))),
            Join(Response(), new byte[] { 0 }), Join(Response(), new byte[] { 0x12, 0xff }), Join(Response(), new byte[] { 0x08, 0x80 }),
            Join(Message(3, Player(1)), UInt(8, 1), Message(13, Equipment(256, 1, 22))), Join(Message(3, Player(1)), UInt(8, 1), Message(13, Equipment(1, 256, 22))),
            Join(Message(3, Player(1)), UInt(8, 1), Message(13, Join(Equipment(1, 1, 22), UInt(7, 22)))),
            Join(Message(3, Join(UInt(1, 1), Signed(23, -1))), UInt(8, 1), Message(13, Equipment(1, 1, 22))) };
        for (int i = 0; i < invalid.Length; i++) Check(PlayerStateServerSnapshot.Decode(invalid[i]).Error != null, "parser_reject_" + i);
        s = PlayerStateServerSnapshot.Decode(Join(Message(3, Player(1)), UInt(8, 1), Message(13, Join(UInt(1, 1), UInt(2, 1)))));
        PlayerStateEquipment e = (PlayerStateEquipment)s.Equipments[0];
        Check(s.Error == null && e.Level == 0 && !e.LevelPresent && e.Owner == 0 && !e.OwnerPresent, "optional_zero_presence_is_explicit");
        Check(PlayerStateServerSnapshot.Decode(Join(Response(), Message(99, new byte[70000]))).Error == null, "compound_response_above_old64k_cap_supported");
    }
    private static void NormalAndAsync(string path)
    {
        string why; RefreshFixture f = new RefreshFixture(); f.Login.Bonus = true;
        Check(f.Begin(out why), "normal_fixed_refresh_invoked");
        Check(f.Client.RefreshSends == 1 && f.Login.RefreshCalls == 1 && f.Model.Pending, "only_one_normal_refresh_request");
        byte[] payload = Response(); MemoryStream stream = new MemoryStream(Join(new byte[] { 7, 7 }, payload), false); stream.Position = 2;
        f.Client.Deliver(stream); // no intervening Capture: the fast receipt is retained
        Check(f.Login.CallbackCalls == 1 && object.ReferenceEquals(f.Login.ReceivedStream, stream) && f.Login.ReceivedArg == null && f.Login.ReceivedPosition == 2 && stream.Position == 2, "fast_callback_original_exact_once_and_position_preserved");
        f.Capture(); Check(f.Model.Status == "WAITING" && f.Model.Json().Contains("\"callback_returned\":true"), "positive_callback_return_waits_stable_context");
        f.Capture(); Check(f.Model.Status == "WAITING", "same_frame_does_not_establish_stability");
        UnityEngine.Time.frameCount++; f.Capture(); Check(f.Model.Status == "OBSERVED" && !f.Model.Pending, "server_receipt_and_rebuilt_stable_context_observed");
        Check(f.Login.NormalNextLevelCalls == 1 && f.Login.NormalPingCalls == 1 && f.Login.NormalBonusCalls == 1 && f.Client.RefreshSends == 1, "normal_followups_left_to_game_callback");
        Check(!f.Model.Queue(f.Command, out why), "observed_model_still_never_refreshes_again");
        if (path != null) File.WriteAllText(Path.Combine(path, "dto-observed.json"), f.Model.Json());

        f = new RefreshFixture(); f.Login.DelayRebuild = true; Check(f.Begin(out why), "async_refresh_invoked"); f.Deliver(Response()); f.Capture();
        Check(f.Model.Status == "WAITING" && f.Model.Json().Contains("waiting_refresh_character_replacement"), "unchanged_character_not_server_rebuild_proof");
        f.Model.Capture(null); Check(f.Model.Status == "WAITING", "transient_unknown_context_waits_without_stopping_requests");
        f.Login.CompleteRebuild(); f.Main.loading = true; f.Capture(); Check(f.Model.Status == "WAITING", "async_main_loading_waits");
        f.Main.loading = false; f.Client.m_handlers[(ushort)1025] = new RefreshHandler(); f.Capture(); Check(f.Model.Status == "WAITING", "normal_enter_followup_waits");
        f.Client.m_handlers.Clear(); f.Settle(); Check(f.Model.Status == "OBSERVED", "async_rebuild_and_followups_can_complete");
        f = new RefreshFixture(); f.Login.ObserveInside = true; Check(f.Begin(out why), "callback_inflight_case_started"); f.Deliver(Response());
        Check(f.Login.InsideStatus == "WAITING" && f.Login.ReentrantEntryBlocked, "callback_return_and_execution_barrier_are_required");
        f.Settle(); Check(f.Model.Status == "OBSERVED", "callback_return_promotes_after_normal_return");
    }
    private static void Failures(string path)
    {
        string why; RefreshFixture f = new RefreshFixture(); f.Client.m_handlers[(ushort)4000] = new RefreshHandler();
        Check(!f.Model.Queue(f.Command, out why) && f.Login.RefreshCalls == 0, "all_normal_handlers_gate_refresh");
        f = new RefreshFixture(); f.Client.messages.Add(new object()); Check(!f.Model.Queue(f.Command, out why), "all_normal_queued_messages_gate_refresh");
        f = new RefreshFixture(); DevelopmentCommand budget = f.Command.Copy(); budget.MaxRequests = 1; Check(!f.Model.Queue(budget, out why), "refresh_rejects_business_request_budget");
        f = new RefreshFixture(); Check(f.Model.Queue(f.Command, out why), "queue_original_command"); DevelopmentCommand altered = f.Command.Copy(); altered.ExpectedRevision++;
        Check(!f.Model.ExecuteGui(altered, delegate { }, out why) && f.Login.RefreshCalls == 0, "immutable_expected_revision_bound"); f.Model.Stop("cancel");
        Check(!f.Model.Queue(f.Command, out why), "stopped_queue_cannot_reuse_operation");
        f = new RefreshFixture(); Check(f.Model.Queue(f.Command, out why), "queued_identity_test"); Global.character_ = new RefreshCharacter(); f.Capture();
        Check(!f.Model.ExecuteGui(f.Command, delegate { }, out why) && f.Login.RefreshCalls == 0, "preinvoke_character_reference_change_refused");
        for (int mode = 0; mode < 8; mode++)
        {
            f = new RefreshFixture(); Check(f.Begin(out why), "failure_case_started_" + mode);
            if (mode == 0) f.Deliver(null);
            if (mode == 1) f.Deliver(new byte[0]);
            if (mode == 2) f.Deliver(new byte[1048577]);
            if (mode == 3) f.Deliver(Join(Message(3, Player(99)), UInt(8, 1), Message(13, Equipment(1, 1, 22))));
            if (mode == 4) { f.Login.Throw = true; bool thrown = false; try { f.Deliver(Response()); } catch (InvalidOperationException) { thrown = true; } Check(thrown && f.Login.CallbackCalls == 1, "normal_callback_exception_preserved"); }
            if (mode == 5) { f.Login.RebuiltLevel = 0; f.Deliver(Response()); }
            if (mode == 6) { f.Deliver(Response()); ((RefreshGeneral)Global.character_.form_generals_[(ushort)22]).equipments[0] = new RefreshEquipment(); }
            if (mode == 7) { f.Deliver(Response()); Global.character_.id = 99; }
            f.Settle(); Check(f.Model.Status == "UNKNOWN" && f.Model.Pending, "failed_proof_stays_unknown_pending_" + mode);
            Check(!f.Model.Queue(f.Command, out why) && f.Client.RefreshSends == 1, "unknown_refresh_never_retried_" + mode);
            if (path != null && mode == 4) File.WriteAllText(Path.Combine(path, "dto-callback-threw.json"), f.Model.Json());
        }
        f = new RefreshFixture(); Check(f.Begin(out why), "missing_receipt_started"); f.Client.m_handlers.Clear(); f.Settle();
        Check(f.Model.Status == "WAITING" && f.Model.Json().Contains("\"server_snapshot\":null"), "handler_absence_never_becomes_observed"); f.Model.Stop("no_reply");
        Check(f.Model.Status == "UNKNOWN" && f.Model.Pending && !f.Model.Queue(f.Command, out why), "stop_missing_receipt_retains_unknown_and_blocks_retry");
        f = new RefreshFixture(); Check(f.Begin(out why), "duplicate_callback_started"); RefreshHandler old = (RefreshHandler)f.Client.m_handlers[(ushort)16];
        f.Deliver(Response()); old.handler(new MemoryStream(Response(), false), null); f.Capture();
        Check(f.Login.CallbackCalls == 2 && f.Model.Status == "UNKNOWN" && f.Model.Pending, "duplicate_receipt_unknown_each_original_forwarded_once");
        f = new RefreshFixture(); f.Login.NextRoot = "Capital"; Check(f.Begin(out why), "capital_started"); f.Deliver(Response()); f.Settle(); Check(f.Model.Status == "OBSERVED", "stable_capital_accepted_without_forcing_campaign");
        f = new RefreshFixture(); f.Login.NextRoot = "SealDevil"; Check(f.Begin(out why), "unsupported_root_started"); f.Deliver(Response()); f.Capture();
        Check(f.Model.Status == "BLOCKED" && f.Model.Pending && f.Login.NextRoot == "SealDevil", "unsupported_new_root_blocked_without_navigation");
        f = new RefreshFixture(); f.Login.Refuse = true; Check(!f.Begin(out why) && f.Model.Pending && f.Client.RefreshSends == 0 && !f.Model.Queue(f.Command, out why), "normal_refresh_refusal_not_retried");
    }
    private static void AdmissionAndInvocationRegressions()
    {
        string why;
        for (int mask = 1; mask <= 3; mask++)
        {
            RefreshFixture f = new RefreshFixture();
            ((EquipmentUI)f.All[3]).visible = (mask & 1) != 0;
            ((EquipmentReinforceUI)f.All[4]).visible = (mask & 2) != 0;
            f.Capture();
            Check(f.Begin(out why) && f.Login.RefreshCalls == 1, "known_equipment_panels_admitted_" + mask);
            f.Model.Stop("fixture_cleanup");
        }
        RefreshFixture overlayFixture = new RefreshFixture();
        UnknownOverlayUI overlay = new UnknownOverlayUI(); overlay.gameObject = overlayFixture.GlobalRoot.gameObject;
        overlay.rmg = overlayFixture.GlobalRoot; overlay.visible = true;
        overlayFixture.All = new UnityEngine.Object[] { overlayFixture.All[0], overlayFixture.All[1], overlayFixture.All[2], overlayFixture.All[3], overlayFixture.All[4], overlay };
        overlayFixture.Capture();
        Check(!overlayFixture.Model.Queue(overlayFixture.Command, out why) && why.Contains("development_overlay:") && overlayFixture.Login.RefreshCalls == 0,
            "pass_gate_unknown_visible_overlay_rejected_at_queue");
        overlay.visible = false; overlayFixture.Capture();
        Check(overlayFixture.Model.Queue(overlayFixture.Command, out why), "overlay_revalidation_case_queued");
        overlay.visible = true; overlayFixture.Capture();
        Check(!overlayFixture.Model.ExecuteGui(overlayFixture.Command, delegate { }, out why) && why.Contains("development_overlay:") && overlayFixture.Login.RefreshCalls == 0,
            "pass_gate_unknown_visible_overlay_rejected_at_execute");

        for (int mode = 0; mode < 2; mode++)
        {
            RefreshFixture f = new RefreshFixture();
            Check(f.Model.Queue(f.Command, out why), "publication_failure_case_queued_" + mode);
            Exception marker = mode == 0 ? (Exception)new OperationCanceledException("cancel publication") : new InvalidOperationException("publication failed");
            Exception caught = null;
            try { f.Model.ExecuteGui(f.Command, delegate { throw marker; }, out why); }
            catch (Exception error) { caught = error; }
            Check(object.ReferenceEquals(caught, marker) && f.Login.RefreshCalls == 0 && f.Client.RefreshSends == 0 &&
                !f.Model.MutationAttempted && !f.Model.Pending && f.Model.Status == "REJECTED",
                "pre_method_publication_failure_rethrows_without_refresh_" + mode);
            f.Model.NotInvoked("publication_failed");
            Check(!f.Model.MutationAttempted && !f.Model.Pending && f.Login.RefreshCalls == 0, "publication_failure_not_invoked_" + mode);
        }
        RefreshFixture missing = new RefreshFixture(); missing.Login.NoHandler = true;
        Check(!missing.Begin(out why) && why == "refresh_handler_missing" && missing.Login.RefreshCalls == 1 &&
            missing.Model.MutationAttempted && missing.Model.Status == "UNKNOWN" && missing.Model.Pending,
            "started_refresh_missing_handler_capture_is_unknown");
        missing.Model.NotInvoked("outer_cleanup");
        Check(missing.Model.Status == "UNKNOWN" && missing.Model.Pending && missing.Model.MutationAttempted &&
            !missing.Model.Queue(missing.Command, out why) && missing.Login.RefreshCalls == 1,
            "not_invoked_cannot_clear_started_refresh_without_capture");
    }
    private static void SealedObservedReceipt()
    {
        string why; RefreshFixture f = new RefreshFixture();
        Check(f.Begin(out why), "sealed_receipt_refresh_started");
        f.Deliver(Response()); f.Capture();
        Check(f.Model.Status == "WAITING" && f.Model.Json().Contains("\"normal_responses_idle\":true"),
            "waiting_refresh_initial_idle_witness");
        f.Client.m_handlers[(ushort)1104] = new RefreshHandler { life = 1f };
        UnityEngine.Time.frameCount++; f.Capture();
        Check(f.Model.Status == "WAITING" && f.Model.Pending && f.Model.Json().Contains("\"normal_responses_idle\":false"),
            "waiting_positive_life_1104_updates_idle_and_prevents_promotion");
        f.Client.m_handlers.Clear(); f.Client.messages.Add(new object());
        UnityEngine.Time.frameCount++; f.Capture();
        Check(f.Model.Status == "WAITING" && f.Model.Pending && f.Model.Json().Contains("\"normal_responses_idle\":false"),
            "waiting_queued_message_updates_idle_and_prevents_promotion");
        f.Client.messages.Clear(); f.Settle();
        string sealedJson = f.Model.Json();
        string sealedBytes = Convert.ToBase64String(Encoding.UTF8.GetBytes(sealedJson));
        Check(f.Model.Status == "OBSERVED" && !f.Model.Pending && sealedJson.Contains("\"normal_responses_idle\":true") &&
            !sealedJson.Contains("\"receipt_capture\":null") && !sealedJson.Contains("\"server_snapshot\":null") &&
            !sealedJson.Contains("\"receipt_hash\":\"\""), "observed_receipt_contains_completed_sc16_evidence");
        f.Client.m_handlers[(ushort)1104] = new RefreshHandler { life = 1f }; f.Capture();
        Check(Convert.ToBase64String(Encoding.UTF8.GetBytes(f.Model.Json())) == sealedBytes,
            "observed_receipt_bytes_sealed_during_positive_life_1104");
        f.Client.m_handlers.Clear(); f.Client.messages.Add(new object()); f.Capture();
        Check(Convert.ToBase64String(Encoding.UTF8.GetBytes(f.Model.Json())) == sealedBytes,
            "observed_receipt_bytes_sealed_during_queued_message");
        f.Client.messages.Clear(); f.Model.Capture(null);
        Check(Convert.ToBase64String(Encoding.UTF8.GetBytes(f.Model.Json())) == sealedBytes,
            "observed_receipt_bytes_sealed_during_null_context");
        f.Root.LevelID = "SealDevil"; f.Capture();
        Check(Convert.ToBase64String(Encoding.UTF8.GetBytes(f.Model.Json())) == sealedBytes,
            "observed_receipt_bytes_sealed_after_root_changed");
    }
    private static void PassivePushHandlers()
    {
        string why; RefreshFixture f = new RefreshFixture();
        f.Client.m_handlers[(ushort)19] = new RefreshHandler { life = 0f };
        Check(f.Begin(out why), "permanent_push19_allows_normal_refresh"); f.Deliver(Response()); f.Settle();
        Check(f.Model.Status == "OBSERVED" && f.Client.m_handlers.Contains((ushort)19), "permanent_push19_remains_registered_after_stable_rebuild");
        f = new RefreshFixture(); f.Client.m_handlers[(ushort)19] = new RefreshHandler { life = -1f };
        Check(f.Begin(out why), "negative_life_passive_push_allows_refresh"); f.Model.Stop("test_end");
        f = new RefreshFixture(); f.Client.m_handlers[(ushort)1104] = new RefreshHandler { life = 0f }; f.Capture();
        Check(f.Begin(out why), "passive_handler_in_tracked_opcode_is_not_pending_request"); f.Deliver(Response()); f.Settle();
        Check(f.Model.Status == "OBSERVED", "tracked_passive_handler_does_not_block_stability");
        float[] invalid = new float[] { 1f, Single.NaN, Single.PositiveInfinity, Single.NegativeInfinity };
        for (int i = 0; i < invalid.Length; i++)
        {
            f = new RefreshFixture(); f.Client.m_handlers[(ushort)19] = new RefreshHandler { life = invalid[i] };
            Check(!f.Model.Queue(f.Command, out why) && f.Login.RefreshCalls == 0, "positive_or_unknown_handler_life_rejected_" + i);
        }
        f = new RefreshFixture(); f.Client.m_handlers[(ushort)19] = new object();
        Check(!f.Model.Queue(f.Command, out why), "handler_life_missing_schema_rejected");
        f = new RefreshFixture(); f.Client.m_handlers[19] = new RefreshHandler { life = 0f };
        Check(!f.Model.Queue(f.Command, out why), "handler_dictionary_key_type_rejected");
        f = new RefreshFixture(); Check(f.Begin(out why), "post_callback_positive_handler_case_started"); f.Deliver(Response());
        f.Client.m_handlers[(ushort)19] = new RefreshHandler { life = 1f }; f.Settle();
        Check(f.Model.Status == "WAITING", "positive_response_handler_prevents_stable_observed");
        ((RefreshHandler)f.Client.m_handlers[(ushort)19]).life = 0f; f.Settle();
        Check(f.Model.Status == "OBSERVED", "permanent_handler_allows_stable_context_after_pending_clears");
    }
    public static int Main(string[] args)
    {
        try
        {
            string path = args.Length > 0 ? args[0] : null; if (path != null) Directory.CreateDirectory(path);
            Parser(); NormalAndAsync(path); Failures(path); PassivePushHandlers(); AdmissionAndInvocationRegressions(); SealedObservedReceipt();
            DailySnapshotProof(); DailyActivityProjection(); DailyNaturalClockProof();
            Console.WriteLine("{\"status\":\"PASS\",\"checks\":" + checks + ",\"game_access\":false,\"test_names\":[" + String.Join(",", (string[])names.ToArray(typeof(string))) + "]}"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.ToString()); return 1; }
    }
}
