using System;
using System.Collections;
using System.IO;
using System.Reflection;
using JiangShen.AssistantRuntime;
using D = JiangShen.AssistantRuntime.SharedDevelopmentContext;

#if !JSB_STONE_PROBE_TEST
namespace UnityEngine
{
    public class Object { public int GetInstanceID() { return 1; } }
    public class Transform { public Transform root { get { return this; } } }
    public class GameObject
    {
        public bool activeInHierarchy = true;
        public readonly Transform transform = new Transform();
        public Component Panel;
        public Component GetComponent(string name) { return name == "EnchantUI" ? Panel : null; }
    }
    public class Component : Object
    {
        public GameObject gameObject = new GameObject();
        public Transform transform { get { return gameObject.transform; } }
    }
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
#endif

public class __BaseUI : UnityEngine.Behaviour
{
    public static string subui = "";
    public UnityEngine.Component rmg;
    public DateTime visible_time;
    public bool loading, collecting, visible, first;
    public bool IsVisible() { return visible; }
    public bool IsEnable() { return true; }
}
public class BaseUI : __BaseUI { public static RootUI rootUI; }
public static class BuildingBuildUI { public static bool m_bBuildVisible; }
public static class PopupUI { public static bool popup; }
public static class BuildingBehaviourScript { public static bool m_bEditVisible; }
public class CampaignUI : BaseUI { }
public sealed class RootUI : BaseUI
{
    public static game.net.tcp_client m_client;
    public static bool m_bRefreshFive;
    public string LevelID = "Capital";
}
public static class BaseAsset
{
    public sealed class LoadScreen { public UnityEngine.Component currmg; }
    public static LoadScreen lds = new LoadScreen();
    public static bool _PHONEUI;
}
public static class TimeUtil { public static uint _now = 220000, _thisDay = 200000; }

public sealed class MainUI : __BaseUI
{
    public bool m_bInit = true;
    public static __BaseUI m_subui;
    public static EnchantUI Panel;
    public static int Opens, Closes;
    public static bool CloseDoesNothing, ThrowAfterClose;
    public static Action BeforeClose;
    public static void _enchant()
    {
        Opens++;
        __BaseUI.subui = "enchant";
        m_subui = Panel;
        Panel.loading = true;
        Global.extern_enabled = false;
    }
    public static void SetSubUI(__BaseUI value)
    {
        Closes++;
        if (BeforeClose != null) BeforeClose();
        if (CloseDoesNothing) return;
        m_subui.visible = false;
        m_subui = value;
        if (ThrowAfterClose) throw new IOException("fixture_close_failure");
    }
}

public static class RootBase
{
    public static int ActivityReplies;
    public static void handelActvity(MemoryStream stream, object arg)
    {
        ActivityReplies++;
        if (stream == null || stream.ReadByte() != 10) return;
        stream.ReadByte();
        if (stream.ReadByte() != 8 || stream.ReadByte() != 16 || stream.ReadByte() != 16) return;
        byte count = (byte)stream.ReadByte();
        if (stream.ReadByte() != 24) return;
        int refresh = 0, shift = 0;
        for (int value; (value = stream.ReadByte()) != -1; shift += 7)
            refresh |= (value & 127) << shift;
        Database.Activity_Reward reward = (Database.Activity_Reward)Global.character_.act_rewards[2u];
        Global.character_.acts[16u] = reward.acts[16u] = new Database.Activity { condition_param = count, refresh_time = refresh >> 1 };
    }
}

public sealed class EnchantUI : __BaseUI
{
    private enum digging_type { nodigging, sliver, gold }
    private digging_type m_digging = digging_type.nodigging;
    public DateTime m_LoadOverTime, showTimer, m_EnchantTime;
    public bool sending, AIdig, aistate, aiclosed = true, vipdig, subwindow;
    public int m_diglevel = 2, Requests, Replies, PaidRequests;
    public bool ThrowOnSend, ThrowOnReply, RefusalBoxOpened;
    public Action InOriginal;

    public void LoadComplete()
    {
        loading = false;
        visible = Global.extern_enabled = true;
        visible_time = DateTime.Now.AddSeconds(-1);
        m_LoadOverTime = DateTime.Now.AddMilliseconds(-800);
    }

    public void cs_enchant_dig_sliver()
    {
        if (m_digging != digging_type.nodigging || Global.character_.sliver_pick <= 0) return;
        Requests++;
        sending = true;
        Global.m_client.Put(825, sc_enchant_dig_sliver);
        if (ThrowOnSend) throw new IOException("fixture_send_failure");
    }

    public void sc_enchant_dig_sliver(MemoryStream stream, object arg)
    {
        Replies++;
        if (InOriginal != null) InOriginal();
        if (stream == null) return;
        if (ThrowOnReply) throw new IOException("fixture_original_failure");
        sending = false;
        if (stream.Length < 4 || stream.ReadByte() != 8) return;
        if (stream.ReadByte() != 0) { RefusalBoxOpened = true; Global.extern_enabled = false; return; }
        if (stream.ReadByte() != 16) return;
        int type = stream.ReadByte();
        if (type == 5) Global.character_.enchant_double++;
        else
        {
            if (type != 6) Global.character_.enchant_stone += Global.Yield.num << Global.character_.enchant_double;
            Global.character_.enchant_double = 0;
        }
        Global.character_.sliver_pick--;
        showTimer = DateTime.Now.AddSeconds(1);
    }

    public void ToDig() { PaidRequests++; }
    public void cs_enchant_dig_gold() { PaidRequests++; }
}

public static class Global
{
    public static Database.Character character_;
    public static game.net.tcp_client m_client;
    public static bool extern_enabled = true, relogin;
    public static Database._activity Task;
    public static Database._vip_template Vip;
    public static Database._enchant_harvest_chance Yield;
    public static object getTemplate(string name, uint id)
    {
        if (name == "activity" && id == 16) return Task;
        if (name == "vip_template" && id == 0) return Vip;
        return name == "enchant_harvest_chance" && Yield != null && id == Yield.id ? Yield : null;
    }
}

namespace Database
{
    public sealed class Character
    {
        public ulong id = 45;
        public uint next_resource_time = 304400, gold_consume;
        public int sliver_pick = 3, gold = 100, bind_gold = 5, enchant_stone = 37;
        public byte vip_level;
        public short gold_pick_times;
        public sbyte enchant_double, buy_silverpick;
        public bool gm, enchant_dragon_protect, Unlocked = true;
        public readonly Hashtable acts = new Hashtable(), act_rewards = new Hashtable();
        public Action BeforeFeatureCheck;
        public bool checkUI(string name) { if (BeforeFeatureCheck != null) BeforeFeatureCheck(); return name == "enchant_dig" && Unlocked; }
    }
    public sealed class _activity { public byte id = 16, idreward = 2, id_uibutton = 48; public bool active = true; public int cond_param = 1; }
    public sealed class _vip_template { public byte level; }
    public sealed class _enchant_harvest_chance { public int id = 1; public byte num = 4; }
    public sealed class Activity
    {
        public byte idactivity = 16, condition_param;
        public int refresh_time = 304400;
        public _activity temp = Global.Task;
    }
    public sealed class Activity_Reward { public byte idact_reward = 2; public readonly Hashtable acts = new Hashtable(); }
}

namespace game.net
{
    public class tcp_base
    {
        public delegate void delegate_handler(MemoryStream stream, object arg);
        public sealed class Handler
        {
            public delegate_handler handler;
            public object arg;
            public float life = 10f;
        }
        public static int connection_state = 3;
        public bool m_bConnectLost;
        public readonly Hashtable m_handlers = new Hashtable();
        public readonly ArrayList messages = new ArrayList();
    }
    public sealed class tcp_client : tcp_base
    {
        public sealed class Socket { public bool Connected { get; set; } }
        public sealed class FeatureStream : MemoryStream
        {
            public bool Readable = true, Writable = true;
            public override bool CanRead { get { return Readable && base.CanRead; } }
            public override bool CanWrite { get { return Writable && base.CanWrite; } }
        }
        public Socket m_client = new Socket { Connected = true };
        public Stream m_stream = new FeatureStream();
        public readonly ArrayList m_sendDatas = new ArrayList();
        public void Put(ushort opcode, delegate_handler callback) { m_handlers[opcode] = new Handler { handler = callback }; }
        public void PushActivity(byte[] body)
        {
            lock (this)
            {
                Handler h = (Handler)m_handlers[(ushort)355];
                h.handler(new MemoryStream(body), h.arg);
            }
        }
        public void Reply(byte[] body, bool remove)
        {
            lock (this)
            {
                Handler h = (Handler)m_handlers[(ushort)825];
                if (remove) m_handlers.Remove((ushort)825);
                h.handler(body == null ? null : new MemoryStream(body), h.arg);
            }
        }
    }
}

internal static class DailyStoneTests
{
    private const string Id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", NewId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly byte[] Ok = { 8, 0, 16, 1 }, Refused = { 8, 1, 16, 0 };
    private static string contextGate = "PASS";

    private static void Check(bool passed, string caseName)
    { if (!passed) throw new InvalidOperationException(caseName); }

    private static readonly DailyStoneInvocationGate AdmitGui =
        delegate(DailyStoneInvocationCommit sample, DailyStoneInvocationCommit check) { return sample() && check(); };

    internal static void ResetGame()
    {
        Global.Task = new Database._activity(); Global.Vip = new Database._vip_template();
        Global.Yield = new Database._enchant_harvest_chance(); Global.character_ = new Database.Character();
        Database.Activity row = new Database.Activity();
        Database.Activity_Reward reward = new Database.Activity_Reward();
        Global.character_.acts[16u] = reward.acts[16u] = row; Global.character_.act_rewards[2u] = reward;
        BaseAsset.lds.currmg = BaseUI.rootUI = new RootUI(); BaseUI.rootUI.rmg = BaseUI.rootUI;
        MainUI.Panel = new EnchantUI { rmg = BaseUI.rootUI, gameObject = BaseUI.rootUI.gameObject };
        BaseUI.rootUI.gameObject.Panel = MainUI.Panel;
        MainUI.m_subui = null; MainUI.Opens = MainUI.Closes = 0; __BaseUI.subui = "";
        MainUI.CloseDoesNothing = MainUI.ThrowAfterClose = false;
        MainUI.BeforeClose = null;
        RootUI.m_client = Global.m_client = new game.net.tcp_client();
        RootBase.ActivityReplies = 0;
        Global.m_client.Put(355, RootBase.handelActvity);
        ((game.net.tcp_base.Handler)Global.m_client.m_handlers[(ushort)355]).life = -1f;
        RootUI.m_bRefreshFive = Global.relogin = BaseAsset._PHONEUI = false;
        PopupUI.popup = false;
        Global.extern_enabled = true; TimeUtil._thisDay = 200000; TimeUtil._now = 220000;
        contextGate = "PASS";
    }

    private static DailyStoneModel Reset(bool productionGate)
    {
        ResetGame();
        DailyStoneModel model = productionGate ? new DailyStoneModel(DevelopmentCallbackCapture.InstallDailyStone, DevelopmentCallbackCapture.InstallDailyActivityPush) :
            new DailyStoneModel(DevelopmentCallbackCapture.InstallDailyStone, DevelopmentCallbackCapture.InstallDailyActivityPush, DailyRefreshGate.ReadClock);
        model.Capture(Context());
        return model;
    }

    internal static D Context()
    {
        return new D(new UnityEngine.Object[] { BaseUI.rootUI, MainUI.Panel }, BaseAsset.lds.currmg, BaseUI.rootUI,
            Global.character_, Global.character_.id.ToString(), contextGate, false);
    }

    private static bool Queue(DailyStoneModel model, string action, int sequence, out string why)
    { return model.Queue(16, 45, Id, sequence, model.Revision, action, out why); }

    private static void Act(DailyStoneModel model, string action, int sequence)
    {
        string why;
        Check(Queue(model, action, sequence, out why), "queue_" + action + "_" + why + "_" + model.ViewJson());
        Check(model.ExecuteGui(Context, delegate { }, AdmitGui, out why), "invoke_" + action + "_" + why);
    }

    private static void Open(DailyStoneModel model)
    {
        Act(model, "open", 1);
        model.Capture(Context());
        Check(MainUI.Panel.loading && model.Busy && model.OperationJson().Contains("OPENING"), "async_loading");
        MainUI.Panel.LoadComplete(); model.Capture(Context());
        Check(model.ViewJson().Contains("\"next_action\":\"dig\"") &&
            model.ViewJson().Contains("\"gold_cost\":0,\"silver_pick_cost\":1"), model.ViewJson());
    }

    private static DailyStoneModel Sent()
    {
        DailyStoneModel model = Reset(false); Open(model); Act(model, "dig", 2);
        Check(model.MutationAttempted && MainUI.Panel.Requests == 1 && model.Unresolved, "single_stock_send");
        return model;
    }

    internal static void Progress()
    {
        // 355 carries row 16, count 1, refresh 304400 (ZigZag 608800).
        Global.m_client.PushActivity(new byte[] { 10, 8, 8, 16, 16, 1, 24, 160, 148, 37 });
    }

    internal static void Expire(DailyStoneModel model)
    { typeof(DailyStoneModel).GetField("since", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(model, unchecked(Environment.TickCount - 19000)); }

    private static void CheckBlocked(DailyStoneModel model, string why)
    {
        string ignored;
        model.Capture(Context());
        Check(model.ViewJson().Contains(why) && !Queue(model, "open", 1, out ignored) && MainUI.Opens == 0 && MainUI.Panel.Requests == 0,
            "blocked_" + why + "_" + model.ViewJson());
    }

    private static void CheckUnknown(DailyStoneModel model, string test)
    {
        string why;
        model.Capture(Context());
        Check(model.OutcomeUnknown && model.Unresolved && model.MutationAttempted &&
            !model.Queue(16, 45, NewId, 3, model.Revision, "open", out why) && MainUI.Panel.Requests == 1 && MainUI.Closes == 0,
            test + "_" + model.OperationJson());
    }

    private static void ExactWire()
    {
        Check(DailyStoneReceipt.Opcode == 0x339 && DailyStoneReceipt.Decode(Ok).Type == 1 &&
            DailyStoneReceipt.Decode(Refused).Result == 1, "fixed_wire_identity");
        foreach (byte[] bytes in new byte[][] { null, new byte[0], new byte[] { 8, 0 }, new byte[] { 16, 1 },
            new byte[] { 8, 2, 16, 1 }, new byte[] { 8, 0, 16, 0 }, new byte[] { 8, 0, 16, 8 },
            new byte[] { 8, 0, 16, 1, 8, 0 }, new byte[] { 8, 0, 18, 1 }, new byte[] { 8, 0, 16, 128 },
            new byte[] { 8, 0, 16, 129, 0 }, new byte[] { 8, 0, 16, 128, 128, 128, 128, 16 } })
        {
            bool rejected = false;
            try { DailyStoneReceipt.Decode(bytes); } catch (FormatException) { rejected = true; }
            Check(rejected, "malformed_wire_accepted");
        }
    }

    private static void Admission()
    {
        string why;
        DailyStoneModel model = Reset(true); CheckBlocked(model, "daily_refresh_unverified");
        model = Reset(false); Global.character_.sliver_pick = 0; CheckBlocked(model, "stone_no_existing_silver_pick");
        model = Reset(false); Global.character_.sliver_pick = -1; CheckBlocked(model, "stone_stock_quote_schema");
        model = Reset(false); Global.Vip = null; CheckBlocked(model, "stone_stock_quote_schema");
        model = Reset(false); Global.Task.active = false; CheckBlocked(model, "stone_activity_identity");
        model = Reset(false); Global.Task.id_uibutton = 20; CheckBlocked(model, "stone_activity_identity");
        model = Reset(false); Global.character_.enchant_double = 3; CheckBlocked(model, "stone_double_confirmation_required");
        model = Reset(false); Global.character_.Unlocked = false; CheckBlocked(model, "stone_feature_locked");
        model = Reset(false); RootUI.m_bRefreshFive = true; CheckBlocked(model, "daily_refresh_pending");
        model = Reset(false); Global.m_client.Put(375, MainUI.Panel.sc_enchant_dig_sliver); CheckBlocked(model, "stone_network_busy");
        model = Reset(false); Global.m_client.m_handlers.Remove((ushort)355); CheckBlocked(model, "stone_activity_callback_unbound");
        model = Reset(false); ((game.net.tcp_base.Handler)Global.m_client.m_handlers[(ushort)355]).arg = new object();
        CheckBlocked(model, "stone_activity_callback_unbound");
        model = Reset(false); ((game.net.tcp_base.Handler)Global.m_client.m_handlers[(ushort)355]).handler = MainUI.Panel.sc_enchant_dig_sliver;
        CheckBlocked(model, "stone_activity_callback_unbound");
        model = Reset(false); MainUI.Panel.aiclosed = false; CheckBlocked(model, "stone_clean_stock_entry_required");
        model = Reset(false); BaseUI.rootUI.gameObject.Panel = new EnchantUI(); CheckBlocked(model, "stone_stock_panel_identity");
        model = Reset(false);
        Check(!model.Queue(7, 45, Id, 1, model.Revision, "open", out why) &&
            !model.Queue(16, 46, Id, 1, model.Revision, "open", out why) &&
            !model.Queue(16, 45, Id, 1, model.Revision + 1, "open", out why) &&
            !Queue(model, "buy", 1, out why), "target_role_revision_action");
    }

    private static void ProgressAdmission()
    {
        DailyStoneModel model = Reset(true);
        Check(DailyRefreshProof.RecordSnapshot(Global.character_, DailyTransport.Read(Global.m_client), 218000, 304400),
            "verified_fixture_cycle");
        // 0x106 can advance only Character.acts while the panel still shows zero.
        Global.character_.acts[16u] = new Database.Activity { condition_param = 1 };
        CheckBlocked(model, "stone_activity_panel_stale");
        DailyRefreshProof.Stop();
        model = Reset(false); Progress();
        Global.character_.acts[16u] = new Database.Activity { condition_param = 1, refresh_time = 304401 };
        CheckBlocked(model, "stone_activity_panel_stale");
        model = Reset(false); Progress();
        Global.character_.acts.Remove(16u);
        CheckBlocked(model, "stone_activity_identity");
        model = Reset(false); Progress();
        ((Database.Activity_Reward)Global.character_.act_rewards[2u]).acts.Remove(16u);
        CheckBlocked(model, "stone_activity_listing");
        model = Reset(false); Progress();
        CheckBlocked(model, "stone_activity_complete");
    }

    private static void Publication()
    {
        string why;
        DailyStoneModel model = Reset(false); Open(model);
        Check(Queue(model, "dig", 2, out why), "queue_before_transport_change");
        int reads = 0;
        DailyStoneContextProvider fresh = delegate { reads++; return Context(); };
        Check(!model.ExecuteGui(fresh, delegate { Global.m_client.m_stream = new MemoryStream(); }, AdmitGui, out why) && reads == 2 &&
            MainUI.Panel.Requests == 0 && !model.MutationAttempted, "post_publication_physical_connection");
        model = Reset(false); Open(model); Check(Queue(model, "dig", 2, out why), "queue_before_role_change");
        Check(!model.ExecuteGui(Context, delegate { Global.character_ = new Database.Character(); }, AdmitGui, out why) && MainUI.Panel.Requests == 0,
            "post_publication_role_replaced_with_same_id");
        model = Reset(false); Open(model); Check(Queue(model, "dig", 2, out why), "queue_before_cutoff");
        Check(!model.ExecuteGui(Context, delegate { TimeUtil._thisDay += 86400; TimeUtil._now += 86400; }, AdmitGui, out why) && MainUI.Panel.Requests == 0,
            "post_publication_cross_05");
        model = Reset(false); Open(model); Check(Queue(model, "dig", 2, out why), "queue_before_106");
        Check(!model.ExecuteGui(Context, delegate { Global.character_.acts[16u] = new Database.Activity { condition_param = 1 }; }, AdmitGui, out why) &&
            why == "stone_activity_panel_stale" && MainUI.Panel.Requests == 0, "post_publication_106_split");
        model = Reset(false); Open(model); Check(Queue(model, "dig", 2, out why), "queue_before_panel_reopen");
        Check(!model.ExecuteGui(Context, delegate { MainUI.Panel.visible_time = MainUI.Panel.visible_time.AddTicks(1); }, AdmitGui, out why) && MainUI.Panel.Requests == 0,
            "post_publication_panel_replaced_generation");
        model = Reset(false); Open(model); Check(Queue(model, "dig", 2, out why), "queue_before_stop");
        DailyStoneModel stopped = model;
        Check(!model.ExecuteGui(Context, stopped.Stop, AdmitGui, out why) && MainUI.Panel.Requests == 0, "stop_during_publication");
        model = Reset(false); Open(model); Check(Queue(model, "dig", 2, out why), "queue_before_sentinel");
        IOException sentinel = new IOException(); bool propagated = false;
        try { model.ExecuteGui(Context, delegate { throw sentinel; }, AdmitGui, out why); }
        catch (IOException error) { propagated = ReferenceEquals(error, sentinel); }
        model.NotInvoked("fixture_cancelled");
        Check(propagated && MainUI.Panel.Requests == 0, "publication_sentinel_preserved");
        model = Reset(false); Open(model); Check(Queue(model, "dig", 2, out why), "queue_before_final_gate");
        reads = 0;
        Check(!model.ExecuteGui(fresh, delegate { }, delegate { Check(reads == 2, "final_read_before_lease"); return false; }, out why) &&
            MainUI.Panel.Requests == 0 && MainUI.Panel.PaidRequests == 0 && !model.MutationAttempted &&
            ((game.net.tcp_base.Handler)Global.m_client.m_handlers[(ushort)355]).handler.Method.Name == "handelActvity",
            "stop_after_final_sample_restores_355");
        model = Reset(false); Open(model); Check(Queue(model, "dig", 2, out why), "queue_before_open_deadline");
        DailyStoneModel expired = model;
        Check(!model.ExecuteGui(Context, delegate { Expire(expired); }, AdmitGui, out why) &&
            why == "stone_open_timed_out" && model.Busy && !model.MutationAttempted && MainUI.Panel.Requests == 0 &&
            ((game.net.tcp_base.Handler)Global.m_client.m_handlers[(ushort)355]).handler.Method.Name == "handelActvity",
            "open_deadline_crossed_during_publication_never_sends");
    }

    private static void CommitBoundary()
    {
        Action[] changes = {
            delegate { Global.m_client.m_stream = new MemoryStream(); },
            delegate { Global.m_client.m_bConnectLost = true; },
            delegate { Global.character_ = new Database.Character(); },
            delegate { TimeUtil._thisDay += 86400; TimeUtil._now += 86400; Global.character_.next_resource_time += 86400; },
            delegate { MainUI.Panel.visible_time = MainUI.Panel.visible_time.AddTicks(1); },
            delegate { contextGate = "foreign_modal"; },
            delegate { Global.character_.sliver_pick--; },
            delegate { UnityEngine.GUI.enabled = false; },
            delegate { ((game.net.tcp_base.Handler)Global.m_client.m_handlers[(ushort)355]).handler = RootBase.handelActvity; }
        };
        for (int i = 0; i < changes.Length; i++)
        {
            string why;
            DailyStoneModel model = Reset(false); Open(model);
            Check(Queue(model, "dig", 2, out why), "queue_commit_race_" + i);
            Action change = changes[i];
            bool invoked = model.ExecuteGui(Context, delegate { },
                delegate(DailyStoneInvocationCommit sample, DailyStoneInvocationCommit check) { change(); return sample() && check(); }, out why);
            UnityEngine.GUI.enabled = true;
            Check(!invoked && MainUI.Panel.Requests == 0 && MainUI.Panel.PaidRequests == 0 && !model.MutationAttempted && model.Busy &&
                ((game.net.tcp_base.Handler)Global.m_client.m_handlers[(ushort)355]).handler.Method.Name == "handelActvity",
                "commit_race_aborts_send_and_restores_355_" + i + "_" + why);
        }
        foreach (string changed in new[] { "replacement", "disconnect", "read-closed", "write-closed" })
        {
            string failure;
            DailyStoneModel postRead = Reset(false); Open(postRead);
            Check(Queue(postRead, "dig", 2, out failure), "queue_after_command_read_race_" + changed);
            Check(!postRead.ExecuteGui(Context, delegate { }, delegate(DailyStoneInvocationCommit sample, DailyStoneInvocationCommit check)
            {
                Check(sample(), "sample_before_final_wire_read");
                if (changed == "replacement") Global.m_client.m_stream = new MemoryStream();
                else if (changed == "disconnect") Global.m_client.m_client.Connected = false;
                else
                {
                    game.net.tcp_client.FeatureStream stream = (game.net.tcp_client.FeatureStream)Global.m_client.m_stream;
                    if (changed == "read-closed") stream.Readable = false;
                    else stream.Writable = false;
                }
                return check();
            }, out failure) && MainUI.Panel.Requests == 0 && postRead.Busy &&
                failure == "stone_transport_changed_before_call", "transport_guard_after_final_wire_read_" + changed);
        }
    }

    private static void Settlement()
    {
        string why;
        DailyStoneModel model = Sent();
        Progress(); model.Capture(Context());
        Check(model.OperationJson().Contains("PENDING") && !model.OperationJson().Contains("\"account_progress_observed\":true"), "activity_without_callback");
        Global.m_client.Reply(Ok, true); model.Capture(Context());
        Check(MainUI.Panel.Replies == 1 && Global.character_.sliver_pick == 2 && Global.character_.enchant_stone == 41 &&
            Global.character_.gold == 100 && model.OperationJson().Contains("SETTLED"), "stock_reply_and_account_readback");
        MainUI.Panel.showTimer = DateTime.Now.AddMilliseconds(350);
        model.Capture(Context());
        Check(!Queue(model, "close", 3, out why), "reward_animation_preserved");
        int revision = model.Revision;
        string beforeTimer = model.ViewJson();
        System.Threading.Thread.Sleep(400);
        Check(model.Revision == revision && model.ViewJson() == beforeTimer && !Queue(model, "close", 3, out why),
            "result_timer_does_not_change_published_revision");
        model.Capture(Context());
        Check(model.Revision > revision && model.ViewJson().Contains("\"next_action\":\"close\""), "timer_crossing_publishes_new_revision");
        Act(model, "close", 3);
        Check(MainUI.Closes == 1 && model.OperationJson().Contains("\"panel_closed\":true") && !model.Busy &&
            !model.Queue(16, 45, Id, 4, model.Revision, "open", out why), "safe_close_and_operation_replay");

        model = Sent(); Global.m_client.Reply(Ok, true); model.Capture(Context());
        Check(model.OperationJson().Contains("PENDING"), "callback_without_activity");
        Expire(model); CheckUnknown(model, "no_activity_after_callback");
        model = Sent(); Global.m_client.Reply(Ok, true);
        Global.character_.acts[16u] = new Database.Activity { condition_param = 1 };
        Expire(model); CheckUnknown(model, "activity_listing_readback_missing");
        model = Sent(); Global.m_client.Reply(Refused, true); model.Capture(Context());
        Check(model.OperationJson().Contains("stone_server_rejected") && !model.Unresolved && MainUI.Panel.Replies == 1 &&
            MainUI.Panel.RefusalBoxOpened && MainUI.Closes == 0, "server_rejection_preserves_stock_modal");

        model = Sent(); Global.m_client.Reply(null, false); Global.m_client.m_handlers.Remove((ushort)825);
        CheckUnknown(model, "stock_timeout"); Check(MainUI.Panel.Replies == 1 && MainUI.Panel.sending, "stock_timeout_behavior_preserved");
        model = Sent(); Global.m_client.Reply(new byte[] { 8, 0, 16, 0 }, true); CheckUnknown(model, "malformed_callback");
        model = Sent(); Global.m_client.Reply(Ok, false); CheckUnknown(model, "handler_not_dequeued");
        model = Sent();
        game.net.tcp_base.Handler h = (game.net.tcp_base.Handler)Global.m_client.m_handlers[(ushort)825];
        Global.m_client.Reply(Ok, true); h.handler(new MemoryStream(Ok), null); CheckUnknown(model, "duplicate_callback");
        model = Sent(); Global.character_.gold--; Global.m_client.Reply(Ok, true); CheckUnknown(model, "premium_readback_conflict");
        model = Sent(); Global.character_.sliver_pick--; Global.m_client.Reply(Ok, true); CheckUnknown(model, "concurrent_silver_readback_conflict");
        model = Sent(); Global.m_client.m_client = new game.net.tcp_client.Socket(); Global.m_client.Reply(Ok, true);
        CheckUnknown(model, "callback_on_new_socket");
        model = Sent(); TimeUtil._thisDay += 86400; TimeUtil._now += 86400; Global.character_.next_resource_time += 86400;
        Global.m_client.Reply(Ok, true); CheckUnknown(model, "callback_cross_05");
        model = Sent(); MainUI.Panel.InOriginal = model.Stop; Progress(); Global.m_client.Reply(Ok, true);
        CheckUnknown(model, "stop_in_original_callback"); Check(MainUI.Panel.Replies == 1 && Global.character_.sliver_pick == 2, "stop_keeps_original_callback");

        model = Sent(); model.Stop(); Global.m_client.Reply(Ok, true);
        CheckUnknown(model, "stop_before_callback"); Check(MainUI.Panel.Replies == 1 && Global.character_.sliver_pick == 2, "stop_restores_original_callback");
        model = Sent(); MainUI.Panel.ThrowOnReply = true; bool threw = false;
        try { Global.m_client.Reply(Ok, true); } catch (IOException) { threw = true; }
        Check(threw, "stock_exception_preserved"); CheckUnknown(model, "throwing_stock_callback");
        model = Reset(false); Open(model); MainUI.Panel.ThrowOnSend = true;
        Check(Queue(model, "dig", 2, out why) && !model.ExecuteGui(Context, delegate { }, AdmitGui, out why), "throwing_stock_send_return");
        CheckUnknown(model, "throwing_stock_send");
    }

    private static void SettlementCacheReconciliation()
    {
        DailyStoneModel model = Sent();
        Global.m_client.Reply(Ok, true);
        Progress();
        Global.character_.acts[16u] = new Database.Activity { condition_param = 1 };
        model.Capture(Context());
        Check(model.OperationJson().Contains("SETTLED"), "matching_distinct_rows_after_callback");

        model = Sent();
        // 0x163 writes both rows, then 0x106 can replace only the latest
        // object with the same progress and refresh before the 825 reply.
        Progress(); model.Capture(Context());
        Global.character_.acts[16u] = new Database.Activity { condition_param = 1 };
        Global.m_client.Reply(Ok, true); model.Capture(Context());
        Check(MainUI.Panel.Requests == 1 && MainUI.Panel.Replies == 1 && RootBase.ActivityReplies == 1 &&
            MainUI.Panel.PaidRequests == 0 && Global.character_.sliver_pick == 2 &&
            Global.character_.enchant_stone == 41 && model.OperationJson().Contains("SETTLED") &&
            model.OperationJson().Contains("\"account_progress_observed\":true"), "355_then_same_value_106_then_825");

        model = Sent(); Progress();
        Global.character_.acts[16u] = new Database.Activity { condition_param = 1, refresh_time = 304401 };
        Global.m_client.Reply(Ok, true); Expire(model); CheckUnknown(model, "settlement_refresh_mismatch");
        model = Sent(); Progress(); Global.m_client.Reply(Ok, true);
        ((Database.Activity_Reward)Global.character_.act_rewards[2u]).acts.Remove(16u);
        CheckUnknown(model, "settlement_listing_removed");
        model = Sent(); Progress(); Global.m_client.Reply(Ok, true);
        Database.Character former = Global.character_;
        Global.character_ = new Database.Character { sliver_pick = 2, enchant_stone = 41 };
        Global.character_.acts[16u] = former.acts[16u]; Global.character_.act_rewards[2u] = former.act_rewards[2u];
        CheckUnknown(model, "settlement_same_id_replaced_role");
        model = Sent(); Global.m_client.Reply(Ok, true);
        ((Database.Activity_Reward)Global.character_.act_rewards[2u]).acts[16u] =
            Global.character_.acts[16u] = new Database.Activity { condition_param = 1 };
        Expire(model); CheckUnknown(model, "cache_changes_without_355");
        model = Sent();
        Global.m_client.PushActivity(new byte[] { 10, 12, 8, 16, 16, 1, 24, 160, 148, 37 });
        Global.m_client.Reply(Ok, true);
        CheckUnknown(model, "malformed_355_remains_unknown");
        Check(RootBase.ActivityReplies == 1 && MainUI.Panel.Replies == 1, "malformed_355_preserves_both_stock_callbacks");
    }

    private static DailyStoneModel Settled()
    {
        DailyStoneModel model = Sent();
        Progress(); Global.m_client.Reply(Ok, true);
        MainUI.Panel.showTimer = DateTime.MinValue;
        model.Capture(Context());
        Check(model.Busy && model.OperationJson().Contains("\"status\":\"SETTLED\""), "settlement_fixture_precondition");
        return model;
    }

    private static void CheckUnverifiedCleanup(DailyStoneModel model, string expectedReason)
    {
        Check(model.Busy && model.OutcomeUnknown && model.OperationJson().Contains(expectedReason) &&
            model.OperationJson().Contains("\"account_progress_observed\":true") &&
            model.OperationJson().Contains("\"panel_closed\":false") &&
            !model.ViewJson().Contains("\"next_action\":\"close\""), "cleanup_uncertain_" + model.OperationJson());
        model.Stop();
        Check(model.Busy && model.OutcomeUnknown, "uncertain_cleanup_survives_stop");
    }

    private static void CleanupMustBeProven()
    {
        string why;
        DailyStoneModel model = Settled(); MainUI.CloseDoesNothing = true;
        Check(Queue(model, "close", 3, out why) && !model.ExecuteGui(Context, delegate { }, AdmitGui, out why) && MainUI.Closes == 1,
            "close_noop_invoked_once_" + why + "_" + MainUI.Closes + "_" + model.ViewJson() + "_" + model.OperationJson());
        CheckUnverifiedCleanup(model, "stone_close_unconfirmed");
        model = Settled(); MainUI.ThrowAfterClose = true;
        Check(Queue(model, "close", 3, out why) && !model.ExecuteGui(Context, delegate { }, AdmitGui, out why) &&
            MainUI.Closes == 1 && MainUI.m_subui == null, "throw_even_after_stock_hide_is_unknown");
        CheckUnverifiedCleanup(model, "stone_close_failed");
        model = Settled(); Expire(model); model.Capture(Context());
        CheckUnverifiedCleanup(model, "stone_cleanup_timed_out");
        model = Settled(); MainUI.m_subui = null; model.Capture(Context());
        CheckUnverifiedCleanup(model, "stone_cleanup_boundary_changed");
        model = Settled(); Global.m_client.m_stream = new MemoryStream(); model.Capture(Context());
        CheckUnverifiedCleanup(model, "stone_cleanup_boundary_changed");
        model = Settled(); TimeUtil._thisDay += 86400; TimeUtil._now += 86400; Global.character_.next_resource_time += 86400;
        model.Capture(Context());
        CheckUnverifiedCleanup(model, "stone_cleanup_boundary_changed");
        model = Settled();
        ((Database.Activity_Reward)Global.character_.act_rewards[2u]).acts[16u] = new Database.Activity { condition_param = 1, refresh_time = 304401 };
        model.Capture(Context());
        Check(!Queue(model, "close", 3, out why) && MainUI.Closes == 0, "split_rows_block_close");
        CheckUnverifiedCleanup(model, "stone_cleanup_boundary_changed");
        model = Settled(); Check(Queue(model, "close", 3, out why), "close_before_stale_106");
        Check(!model.ExecuteGui(Context, delegate { Global.character_.acts[16u] = new Database.Activity(); }, AdmitGui, out why) &&
            MainUI.Closes == 0, "stale_106_after_publication_blocks_close");
        CheckUnverifiedCleanup(model, "stone_cleanup_unavailable");
        model = Settled(); MainUI.BeforeClose = delegate
        {
            Global.character_.acts[16u] = ((Database.Activity_Reward)Global.character_.act_rewards[2u]).acts[16u] =
                new Database.Activity { condition_param = 1, refresh_time = 304401 };
        };
        Check(Queue(model, "close", 3, out why) && !model.ExecuteGui(Context, delegate { }, AdmitGui, out why) &&
            MainUI.Closes == 1 && MainUI.m_subui == null, "both_rows_must_match_witness_after_close");
        CheckUnverifiedCleanup(model, "stone_close_unconfirmed");
        model = Settled(); model.Stop();
        CheckUnverifiedCleanup(model, "stone_stopped");
    }

    private static void SettlementDeadlineAndRefusal()
    {
        string why;
        DailyStoneModel model = Sent(); Expire(model);
        Progress(); Global.m_client.Reply(Ok, true);
        CheckUnknown(model, "late_callbacks_cannot_settle");
        Check(RootBase.ActivityReplies == 1 && MainUI.Panel.Replies == 1 && Global.character_.sliver_pick == 2 &&
            model.OperationJson().Contains("stone_settlement_timed_out") &&
            model.OperationJson().Contains("\"account_progress_observed\":false"), "late_stock_callbacks_still_forwarded");

        model = Sent(); Expire(model); model.Capture(Context());
        Progress(); Global.m_client.Reply(Ok, true);
        CheckUnknown(model, "callbacks_after_timeout_do_not_reopen_operation");
        Check(RootBase.ActivityReplies == 1 && MainUI.Panel.Replies == 1, "timeout_restored_original_callbacks");

        model = Sent(); Global.m_client.Reply(Refused, true);
        Global.character_.acts[16u] = new Database.Activity();
        model.Capture(Context());
        Check(!model.Busy && model.OperationJson().Contains("stone_server_rejected") &&
            MainUI.Panel.RefusalBoxOpened && Global.character_.sliver_pick == 3, "confirmed_refusal_does_not_lock_future_run");
        MainUI.SetSubUI(null); Global.extern_enabled = true; model.Capture(Context());
        Check(!Queue(model, "open", 3, out why) && !Queue(model, "dig", 3, out why) &&
            model.Queue(16, 45, NewId, 4, model.Revision, "open", out why) && MainUI.Panel.Requests == 1,
            "new_id_required_after_explicit_refusal");

        model = Sent(); Progress(); Global.m_client.Reply(Refused, true);
        CheckUnknown(model, "refusal_with_activity_change_blocks_future_run");
    }

    public static int Main()
    {
        try
        {
            ExactWire(); Admission(); ProgressAdmission(); Publication(); CommitBoundary(); Settlement(); SettlementCacheReconciliation();
            CleanupMustBeProven(); SettlementDeadlineAndRefusal();
            Console.WriteLine("{\"daily_stone\":\"passed\"}"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
