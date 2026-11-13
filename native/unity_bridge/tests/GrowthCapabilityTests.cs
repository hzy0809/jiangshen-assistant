// Standalone detached fixture harness. Never references/loads game assemblies.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using JiangShen.AssistantRuntime;
using D = JiangShen.AssistantRuntime.SharedDevelopmentContext;

namespace UnityEngine
{
    public class Object { private static int next; private int id = ++next; public int GetInstanceID() { return id; } }
    public class Transform { public Transform root; }
    public class GameObject { public bool activeInHierarchy = true; public Transform transform = new Transform(); public GameObject() { transform.root = transform; } }
    public class Component : Object { public GameObject gameObject; public Transform transform { get { return gameObject.transform; } } }
    public class Behaviour : Component { public bool enabled = true; }
    public class Event { public static Event current = new Event(); }
    public static class GUI { public static bool enabled = true; }
}
namespace JiangShen.AssistantRuntime
{
    internal sealed class ProbeFailure : Exception { internal ProbeFailure(string why) : base(why) { } }
    internal static class Model
    {
        internal static Type GameType(string name)
        { if (name == "SpiritUI" || name == "SpiritRankupUI" || name == "__BaseUI") throw new Exception("Wrong game assembly: " + name); return Required(name); }
        internal static Type FirstType(string name) { return name == "game.net.tcp_base+Handler" ? typeof(FixtureHandler) : Required(name); }
        private static Type Required(string name) { Type t = Type.GetType(name); if (t == null) throw new Exception("Unknown fixture type " + name); return t; }
    }
    // GeneralTutorialModel uses the campaign GUI publication signature.
    internal delegate void CampaignPublication();
}
public class BaseAsset : UnityEngine.Behaviour { public UnityEngine.Component rmg; public bool loading, collecting; }
public class __BaseUI : BaseAsset
{
    public delegate void delegate_OnClose();
    public delegate_OnClose OnClose;
    public bool visible, interactive = true;
    public int HideCalls, EnableCalls;
    public bool IsVisible() { return visible; }
    public bool IsEnable() { return interactive; }
    public virtual void Hide() { visible = false; HideCalls++; }
    public void EnableAll() { interactive = true; EnableCalls++; }
}
public class BaseUI : __BaseUI { public static RootUI rootUI; }
public class RootUI : BaseUI { public static FixtureClient m_client; public string LevelID = "Campaign"; }
public class MainUI : BaseUI
{
    public static __BaseUI m_subui;
    public static bool Unlocked = true;
    public static int OpenCalls;
    public static bool checkUI(string name) { return Unlocked; }
    public static bool checkSpirit(string name) { return Unlocked && Global.character_.spirit != null && Global.character_.spirit.level < 41; }
    public static void SetSubUI(__BaseUI ui) { if (m_subui != null) m_subui.Hide(); m_subui = ui; }
    public static bool handle_sub_ui(__BaseUI ui, string name) { if (m_subui == ui) { SetSubUI(null); return false; } SetSubUI(ui); return true; }
    public static void _general_person() { OpenCalls++; if (handle_sub_ui(Fixture.Current.Equipment, "equipment")) Fixture.Current.Equipment.MyLoading(); }
    public static void _army_spirit() { OpenCalls++; if (handle_sub_ui(Fixture.Current.Spirit, "army_spirit")) Fixture.Current.Spirit.MyLoading(); }
}
public static class DelegateFunc
{
    public delegate void SetUI(__BaseUI ui);
    public static SetUI MainUI_SetSubUI = DelegateFuncUI.MainUI_SetSubUI;
}
public static class DelegateFuncUI
{
    public static int Spirit2Calls;
    public static void MainUI_SetSubUI(__BaseUI ui) { MainUI.SetSubUI(ui); }
    public static void MainUI_spirit2() { Spirit2Calls++; MainUI.m_subui = null; }
}
public static class Global
{
    public static Database.Character character_;
    public static FixtureClient m_client;
    public static bool extern_enabled = true;
    public static readonly Hashtable Templates = new Hashtable();
    public static object getTemplate(string name, uint id) { return Templates[name + ":" + id]; }
}
public class NewbieQuestUI : BaseUI
{
    public ushort m_questSelected;
    public bool m_bShowWnd = true;
    public int OpenCalls, CheckCalls, ClaimCalls;
    public bool DeferShow;
    public List<ushort> m_listFinish = new List<ushort>();
    public string levelId = "Campaign";
    public void MyLoading(ushort id, string level) { OpenCalls++; m_questSelected = id; levelId = level; if (DeferShow) { loading = true; visible = false; } else CompleteOpen(); }
    public void CompleteOpen(params ushort[] ids)
    {
        loading = false; visible = true; m_listFinish.Clear(); m_listFinish.AddRange(ids);
        foreach (Database.NewbieQuest q in Global.character_.newbiequests) if (m_listFinish.Contains(q.idnewbie_quest)) q.isfinish = 1;
        if (ids.Length > 0) RootUI.m_client.Send(810, this, "sc_newbie_quest_finish", m_listFinish);
    }
    public void CheckQuest(ushort id)
    {
        CheckCalls++;
        foreach (Database.NewbieQuest q in Global.character_.newbiequests) if (q.idnewbie_quest == id && q.isfinish != 1) { q.isfinish = 1; RootUI.m_client.Send(810, this, "sc_newbie_quest_finish", new List<ushort> { id }); }
    }
    public void cs_newbie_quest_award() { ClaimCalls++; RootUI.m_client.Send(811, this, "sc_newbie_quest_award", m_questSelected); }
    private void sc_newbie_quest_finish(MemoryStream stream, object arg) { FixtureCallbackLog.Record(810, stream, arg); }
    private void sc_newbie_quest_award(MemoryStream stream, object arg) { FixtureCallbackLog.Record(811, stream, arg); }
}
public class EquipmentUI : BaseUI
{
    public ushort Selected;
    public int SelectCalls;
    public void MyLoading() { visible = true; }
    private void selectItem(Database.Equipment e) { Selected = e.id; SelectCalls++; }
}
public class EquipmentReinforceUI : BaseUI
{
    public enum pageItem { weapon, armor, artifact, amulet }
    public enum EquipmentPage { Enchant, Forge, Insert }
    public ushort m_curgen;
    public pageItem m_curItem;
    public EquipmentPage m_curEquipPage;
    public bool sended;
    public int OpenCalls, EnchantCalls, ClearCalls;
    public void MyLoading(ushort id, pageItem page) { OpenCalls++; m_curgen = id; m_curItem = page; visible = true; sended = false; }
    public void setOnClose(delegate_OnClose close) { OnClose = close; }
    public Database.Equipment getCurEquip() { return Global.character_.form_generals_[m_curgen].equipments[(int)m_curItem]; }
    private void ClearRune() { ClearCalls++; }
    private void callEnchant()
    {
        Database.Equipment e = getCurEquip(); Database.Price p = (Database.Price)Global.getTemplate("enchant_price", (uint)e.enchant_level + 1);
        if (e != null && !sended && p.weapon <= Global.character_.money + Global.character_.money64 && p.enchant <= Global.character_.enchant_stone && e.enchant_level < Global.character_.level) { EnchantCalls++; RootUI.m_client.Send(1104, this, "sc_equipment_enchant_0x450", null); sended = true; }
    }
    private void sc_equipment_enchant_0x450(MemoryStream stream, object arg) { FixtureCallbackLog.Record(1104, stream, arg); }
}
public class SpiritUI : __BaseUI
{
    public Database.Spirit m_spirit;
    public int m_iPage;
    public uint food_type;
    public bool m_bRefreshing;
    public static __BaseUI my_Subui;
    public int OpenCalls, TrainCalls, RankCalls;
    public void MyLoading() { OpenCalls++; m_spirit = Global.character_.spirit; food_type = Global.character_.spirit_food == 0 ? 1u : 0u; visible = true; }
    private void cs_spirit_train_0x470(bool use_gold, uint count) { TrainCalls++; m_bRefreshing = true; Global.m_client.Send(1136, this, "sc_spirit_train_0x470", null); }
    private void cs_spirit_levelup_0x473() { RankCalls++; Global.m_client.Send(1139, this, "sc_spirit_levelup_0x473", null); }
    private void sc_spirit_train_0x470(MemoryStream stream, object arg) { FixtureCallbackLog.Record(1136, stream, arg); }
    private void sc_spirit_levelup_0x473(MemoryStream stream, object arg) { FixtureCallbackLog.Record(1139, stream, arg); }
}
public class SpiritRankupUI : __BaseUI { public Database.Spirit m_spirit; }
public class UnknownOverlay : BaseUI { }
public class FixtureClient
{
    public readonly Hashtable m_handlers = new Hashtable();
    public readonly ArrayList messages = new ArrayList();
    public int Sends;
    public void Send(ushort op, object target, string name, object arg) { Sends++; m_handlers[op] = new FixtureHandler { handler = (FixtureHandler.Callback)Delegate.CreateDelegate(typeof(FixtureHandler.Callback), target, target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)), arg = arg }; }
    public void Packet(int op, byte[] bytes) { messages.Add(new FixtureMessage { op = (ushort)op, stream = new MemoryStream(bytes, false) }); }
    public void Consumed(int op)
    {
        lock (this)
        {
            FixtureHandler h = m_handlers[(ushort)op] as FixtureHandler; FixtureMessage message = null;
            foreach (FixtureMessage m in messages) if (m.op == op) { message = m; break; }
            if (message != null)
            {
                messages.Remove(message); m_handlers.Remove((ushort)op);
                if (h != null) { h.handler(message.stream, h.arg); message.stream.Close(); }
            }
            else { if (h != null) h.handler(null, h.arg); m_handlers.Remove((ushort)op); }
        }
    }
}
public class FixtureMessage { public ushort op; public MemoryStream stream; }
public class FixtureHandler { public delegate void Callback(MemoryStream stream, object arg); public Callback handler; public object arg; }
public static class FixtureCallbackLog
{
    public static int Calls, Opcode;
    public static MemoryStream Stream;
    public static object Arg;
    public static long Position;
    public static bool ExecutingSeen;
    public static bool AdvanceStream;
    public static Exception Throw;
    public static void Reset() { Calls = Opcode = 0; Stream = null; Arg = null; Position = -1; Throw = null; ExecutingSeen = AdvanceStream = false; }
    public static void Record(int op, MemoryStream stream, object arg)
    { Calls++; Opcode = op; Stream = stream; Arg = arg; Position = stream == null || !stream.CanRead ? -1 : stream.Position; ExecutingSeen = DevelopmentCallbackCapture.AnyCallbackExecuting; if (AdvanceStream && stream != null && stream.CanRead) stream.Position = stream.Length; if (Throw != null) throw Throw; }
}
namespace Database
{
    public class Character
    {
        public ulong id = 42;
        public bool gm;
        public int level = 30, star_tech, cur_form = 1, equipment_forge, spirit_food = 5, gold = 7;
        public long money = 100, money64, enchant_stone = 20;
        public Dictionary<byte, Equipment> equipments = new Dictionary<byte, Equipment>();
        public Dictionary<ushort, General> form_generals_ = new Dictionary<ushort, General>();
        public Dictionary<int, Formation> formations_ = new Dictionary<int, Formation>();
        public List<NewbieQuest> newbiequests = new List<NewbieQuest>();
        public Hashtable artifacts = new Hashtable();
        public Spirit spirit = new Spirit();
        public bool Unlocked = true;
        public bool checkUI(uint id) { return Unlocked; }
        public Campaign getCampaign(ushort id) { return new Campaign(); }
    }
    public class Equipment { public byte id = 7, enchant_level = 1, idequipment_main = 1, idequipment_acc1, idequipment_acc2; public ushort idgeneral_template = 0; public Main main_ = new Main(); }
    public class Main { public byte id = 1, idequipment_type = 1, next_id = 2, accessory1 = 3, accessory2 = 4; }
    public class Price { public uint level = 2, weapon = 10, armor = 6, enchant = 2; public byte up; }
    public class General { public ushort idgeneral_template; public Equipment[] equipments = new Equipment[2]; }
    public class Formation { public List<int> spotgen = new List<int> { 0, 104, -1, -1, -1, -1, -1, -1 }; }
    public class NewbieQuest { public ushort idnewbie_quest; public sbyte isfinish, isgot; }
    public class QuestTemplate { public short idnewbie_quest; public uint newbie_quest_type = 3; public int param0 = 2, param1, exp = 10, money = 20; public string name = "强化测试", desc = "按实际条件"; }
    public class Spirit { public byte level = 9, idspirit_beast = 1; public uint experience = 20; }
    public class Exp { public byte level = 9; public uint exp = 100; }
    public class Campaign { public bool finished; public Dictionary<byte, Troop> troops_ = new Dictionary<byte, Troop> { { 1, new Troop() } }; }
    public class Troop { public bool killed; }
}
internal sealed class Fixture
{
    internal static Fixture Current;
    internal readonly RootUI Root, GlobalRoot, GlobalUi;
    internal readonly MainUI Main;
    internal readonly NewbieQuestUI Growth;
    internal readonly EquipmentUI Equipment;
    internal readonly EquipmentReinforceUI Reinforce;
    internal readonly SpiritUI Spirit;
    internal readonly SpiritRankupUI Rankup;
    internal readonly Database.Character Character = new Database.Character();
    internal UnityEngine.Object[] All;
    internal readonly GrowthTaskModel GrowthModel = new GrowthTaskModel();
    internal readonly EquipmentUpgradeModel EquipmentModel = new EquipmentUpgradeModel();
    internal readonly SpiritBeastUpgradeModel SpiritModel = new SpiritBeastUpgradeModel();
    internal readonly FixtureClient Client = new FixtureClient();
    internal string ExternalGate = "PASS";
    internal bool Paused;
    internal int Sequence;
    internal Fixture()
    {
        Current = this; FixtureCallbackLog.Reset(); Global.Templates.Clear(); MainUI.m_subui = null; MainUI.OpenCalls = 0; MainUI.Unlocked = true; Global.extern_enabled = true; DelegateFuncUI.Spirit2Calls = 0;
        DelegateFunc.MainUI_SetSubUI = DelegateFuncUI.MainUI_SetSubUI; SpiritUI.my_Subui = null; UnityEngine.Event.current = new UnityEngine.Event(); UnityEngine.GUI.enabled = true;
        Root = Attach(new RootUI(), null, new UnityEngine.GameObject()); Root.rmg = Root;
        GlobalRoot = Attach(new RootUI(), null, new UnityEngine.GameObject()); GlobalRoot.rmg = GlobalRoot;
        UnityEngine.GameObject uiObject = GlobalRoot.gameObject;
        GlobalUi = Attach(new RootUI(), GlobalRoot, uiObject); Main = Attach(new MainUI(), Root, Root.gameObject); Growth = Attach(new NewbieQuestUI(), GlobalRoot, uiObject);
        Equipment = Attach(new EquipmentUI(), GlobalRoot, uiObject); Reinforce = Attach(new EquipmentReinforceUI(), GlobalRoot, uiObject); Spirit = Attach(new SpiritUI(), GlobalRoot, uiObject); Rankup = Attach(new SpiritRankupUI(), GlobalRoot, uiObject);
        BaseUI.rootUI = GlobalUi; Global.character_ = Character; RootUI.m_client = Global.m_client = Client;
        Database.Equipment e = new Database.Equipment(); Character.equipments[7] = e; Database.General g = new Database.General(); g.equipments[0] = e; Character.form_generals_[0] = g; Character.formations_[1] = new Database.Formation();
        Global.Templates["enchant_price:2"] = new Database.Price(); Global.Templates["enchant_price:3"] = new Database.Price { level = 3, weapon = 12, enchant = 3 };
        Global.Templates["equipment_main:1"] = new Database.Main(); Global.Templates["equipment_main:2"] = new Database.Main { id = 2 }; Global.Templates["equipment_main:11"] = new Database.Main { id = 11 };
        foreach (byte level in new byte[] { 9, 10, 11, 40, 41, 42, 100 }) Global.Templates["exp_require_spirit:" + level] = new Database.Exp { level = level };
        Quest(19, 3, 2, 0, 0);
        All = new UnityEngine.Object[] { Root, GlobalRoot, GlobalUi, Main, Growth, Equipment, Reinforce, Spirit, Rankup };
    }
    internal static T Attach<T>(T ui, UnityEngine.Component owner, UnityEngine.GameObject go) where T : BaseAsset { ui.gameObject = go; ui.rmg = owner; return ui; }
    internal void Quest(ushort id, uint type, int param, sbyte finish, sbyte got)
    { Character.newbiequests.Add(new Database.NewbieQuest { idnewbie_quest = id, isfinish = finish, isgot = got }); Global.Templates["newbie_quest:" + id] = new Database.QuestTemplate { idnewbie_quest = (short)id, newbie_quest_type = type, param0 = param, param1 = 1 }; }
    internal SharedDevelopmentContext Context()
    { return new SharedDevelopmentContext(All, Root, GlobalUi, Character, Character.id.ToString(), ExternalGate, Paused, delegate(UnityEngine.Behaviour b) { return b == GlobalRoot ? null : "unknown:" + b.GetType().Name; }); }
    internal void Capture() { SharedDevelopmentContext c = Context(); GrowthModel.Capture(c); EquipmentModel.Capture(c); SpiritModel.Capture(c); }
    internal DevelopmentCommand Equip(string action)
    { return new DevelopmentCommand { Action = action, Sequence = ++Sequence, ExpectedRevision = 5, OperationId = new string('a', 32), TargetId = 7, DesiredLevel = 2, Mode = "enchant", MaxRequests = 2, MaxMoney = 100, MaxStone = 20, MaxPractice = 0 }; }
    internal DevelopmentCommand Grow(string action)
    { return new DevelopmentCommand { Action = action, Sequence = ++Sequence, ExpectedRevision = 5, OperationId = new string('b', 32), TargetId = 19, MaxRequests = 5 }; }
    internal DevelopmentCommand Pet(string action)
    { return new DevelopmentCommand { Action = action, Sequence = ++Sequence, ExpectedRevision = 5, OperationId = new string('c', 32), DesiredLevel = 11, MaxRequests = 3, MaxFood = 5 }; }
    internal bool EquipInvoke(DevelopmentCommand c) { string why; Capture(); if (!EquipmentModel.Queue(c, out why)) return false; return EquipmentModel.ExecuteGui(c, delegate { }, out why); }
    internal bool SpiritInvoke(DevelopmentCommand c) { string why; Capture(); if (!SpiritModel.Queue(c, out why)) return false; return SpiritModel.ExecuteGui(c, delegate { }, out why); }
    internal bool GrowthInvoke(DevelopmentCommand c) { string why; Capture(); if (!GrowthModel.Queue(c, out why)) return false; return GrowthModel.ExecuteGui(c, delegate { }, out why); }
    internal void EquipPage() { Equipment.visible = true; MainUI.m_subui = Equipment; }
    internal void ReinforcePage() { EquipPage(); MainUI.SetSubUI(Reinforce); Reinforce.MyLoading(0, EquipmentReinforceUI.pageItem.weapon); Reinforce.setOnClose(MainUI._general_person); }
    internal void SpiritPage() { MainUI.m_subui = Spirit; Spirit.MyLoading(); }
    internal void EquipResult(int result, int id, int level, bool capture)
    {
        Client.Packet(1104, GrowthCapabilityTests.Wire(1, (uint)result, 2, (uint)id, 3, (uint)level, 4, 10)); if (capture) Capture();
        Client.Consumed(1104); Reinforce.sended = false; Database.Equipment e = Character.equipments[7]; Database.Price price = (Database.Price)Global.getTemplate("enchant_price", (uint)e.enchant_level + 1);
        if (price.up == 1) { e.idequipment_main = e.main_.next_id; e.main_ = (Database.Main)Global.getTemplate("equipment_main", e.idequipment_main); e.idequipment_acc1 = e.main_.accessory1; e.idequipment_acc2 = e.main_.accessory2; }
        e.enchant_level = (byte)level; Character.money64 = Character.money + Character.money64 - price.weapon; Character.money = 0; Character.enchant_stone -= price.enchant; Capture();
    }
    internal void SpiritResult(int op, int result, byte level, uint exp, uint food, uint gold, bool capture)
    {
        Client.Packet(op, op == 1136 ? GrowthCapabilityTests.Wire(1, (uint)result, 2, food, 3, gold, 4, exp, 5, level, 6, 50) : GrowthCapabilityTests.Wire(1, (uint)result, 2, level, 3, exp));
        if (capture) Capture(); Client.Consumed(op); Spirit.m_bRefreshing = false;
        if (result == 0)
        {
            Character.spirit.level = level; Character.spirit.experience = exp; Character.spirit_food -= (int)food; Character.gold -= (int)gold;
            if (Character.spirit_food == 0) Spirit.food_type = 1;
            if (op == 1139) { Character.spirit.idspirit_beast++; Rankup.m_spirit = Character.spirit; Rankup.visible = true; Rankup.OnClose = level == 41 ? (__BaseUI.delegate_OnClose)DelegateFuncUI.MainUI_spirit2 : Spirit.EnableAll; if (level == 41) Spirit.Hide(); }
        }
        Capture();
    }
}
internal static class GrowthCapabilityTests
{
    private static int checks;
    private static readonly List<string> names = new List<string>();
    private static void Check(bool value, string name) { checks++; if (!value) throw new Exception("FAIL " + name); names.Add(name); }
    internal static byte[] Wire(params uint[] fields)
    { List<byte> b = new List<byte>(); for (int i = 0; i < fields.Length; i += 2) { Varint(b, fields[i] << 3); Varint(b, fields[i + 1]); } return b.ToArray(); }
    private static void Varint(List<byte> bytes, uint v) { while (v >= 128) { bytes.Add((byte)((v & 127) | 128)); v >>= 7; } bytes.Add((byte)v); }
    private static bool Has(string json, string field, string value) { return json.Contains("\"" + field + "\":" + value); }
    private static void ReadOnlyAndGrowth(string output)
    {
        Fixture f = new Fixture(); f.Quest(2, 7, 100, 0, 0); f.Quest(99, 9, 104, 0, 0); f.Capture();
        Check(Has(f.GrowthModel.Json(), "selected_quest_id", "99"), "growth_reverse_unfinished_priority");
        Check(f.Client.Sends == 0 && MainUI.OpenCalls == 0 && f.Growth.CheckCalls == 0, "all_snapshots_are_read_only");
        foreach (Database.NewbieQuest q in f.Character.newbiequests) q.isfinish = 1; f.Capture(); Check(Has(f.GrowthModel.Json(), "selected_quest_id", "19"), "growth_forward_unclaimed_fallback");
        f = new Fixture(); f.Character.newbiequests.Clear(); for (ushort i = 1; i <= 14; i++) f.Quest(i, i, i == 9 ? 104 : 2, 0, 0); f.Capture();
        for (int i = 1; i <= 14; i++) Check(f.GrowthModel.Json().Contains("\"type\":" + i + ","), "growth_type_" + i + "_requirements_exported");
        Check(f.GrowthModel.Json().Contains("normal_capital_mining_callback_required") && f.Client.Sends == 0, "growth_callback_types_never_fake_finish");
        if (output != null) File.WriteAllText(Path.Combine(output, "growth-snapshot.json"), f.GrowthModel.Json(), Encoding.UTF8);
        f = new Fixture(); f.Character.equipments[7].enchant_level = 2; f.Capture(); string why; DevelopmentCommand c = f.Grow("growth_check");
        string before = f.GrowthModel.Json(); Check(f.GrowthModel.Queue(c, out why) && before == f.GrowthModel.Json(), "growth_queue_does_not_change_snapshot");
        Check(f.GrowthModel.ExecuteGui(c, delegate { Check(f.Growth.CheckCalls == 0 && f.Client.Sends == 0 && f.GrowthModel.Json().Contains("\"operation_id\":\"" + c.OperationId), "growth_publish_before_check"); }, out why), "growth_normal_condition_check");
        f.Client.Packet(810, new byte[0]); f.Capture(); f.Client.Consumed(810); f.Capture();
        Check(f.GrowthModel.Json().Contains("\"server\":\"PASS\"") && f.Client.Sends == 1, "growth_empty_response_correlated_to_owned_check");
        f.Growth.visible = true; f.Growth.m_questSelected = 19; c = f.Grow("growth_claim"); Check(f.GrowthInvoke(c), "growth_normal_claim");
        f.Client.Packet(811, Wire(1, 1)); f.Capture(); f.Client.Consumed(811); f.Character.newbiequests[0].isgot = 1; f.Capture();
        Check(f.GrowthModel.Json().Contains("\"server\":\"FAIL\"") && f.GrowthModel.Json().Contains("\"status\":\"ERROR\""), "growth_ignored_operate_cannot_fake_authority");
        Check(!f.GrowthModel.Queue(f.Grow("growth_claim"), out why) && f.Growth.ClaimCalls == 1, "growth_error_no_extra_claim");
        f = new Fixture(); f.Root.LevelID = "Capital"; f.Capture(); Check(f.GrowthModel.Json().Contains("\"gate\":\"PASS\""), "growth_capital_snapshot_allowed");
    }
    private static void Equipment(string output)
    {
        Fixture f = new Fixture(); f.Capture(); if (output != null) File.WriteAllText(Path.Combine(output, "equipment-snapshot.json"), f.EquipmentModel.Json(), Encoding.UTF8);
        DevelopmentCommand c = f.Equip("equipment_open"); string why; string old = f.EquipmentModel.Json();
        Check(f.EquipmentModel.Queue(c, out why) && old == f.EquipmentModel.Json(), "equipment_queue_does_not_switch_public_goal");
        Check(f.EquipmentModel.ExecuteGui(c, delegate { Check(MainUI.OpenCalls == 0, "equipment_open_publish_before_calls"); }, out why), "equipment_normal_main_entry");
        c = f.Equip("reinforce_open"); Check(f.EquipInvoke(c) && f.Equipment.SelectCalls == 1 && f.Reinforce.OpenCalls == 1 && f.Reinforce.OnClose != null, "equipment_normal_select_open_bind_callback");
        f.Reinforce.m_curEquipPage = EquipmentReinforceUI.EquipmentPage.Forge; Check(f.EquipInvoke(f.Equip("enchant_tab")) && f.Reinforce.ClearCalls == 1, "equipment_normal_tab_clears_runes");
        c = f.Equip("equipment_enchant"); Check(f.EquipInvoke(c) && f.Reinforce.EnchantCalls == 1 && f.Client.Sends == 1, "equipment_single_normal_enchant");
        Check(!f.EquipmentModel.Queue(f.Equip("equipment_enchant"), out why), "equipment_handler_single_flight");
        f.EquipResult(77, 7, 2, true);
        Check(Has(f.EquipmentModel.Json(), "server_progress_observed", "true") && f.EquipmentModel.Json().Contains("\"server\":\"UNVERIFIED\"") && f.EquipmentModel.Json().Contains("\"result_semantics\":\"UNVERIFIED\""), "equipment_exact_progress_does_not_invent_result_constant");
        Check(f.EquipInvoke(f.Equip("equipment_close")) && MainUI.m_subui == f.Equipment && f.Reinforce.OnClose == null, "equipment_close_preserves_normal_callback");
        Check(f.EquipInvoke(f.Equip("equipment_close")) && MainUI.m_subui == null && f.Client.Sends == 1, "equipment_second_close_returns_map_no_request");
        foreach (string failure in new string[] { "money", "stone", "owner", "slot", "forge", "level", "mode", "money_budget", "stone_budget" })
        {
            f = new Fixture(); f.ReinforcePage(); c = f.Equip("equipment_enchant");
            if (failure == "money") f.Character.money = 0; if (failure == "stone") f.Character.enchant_stone = 0; if (failure == "owner") f.Character.equipments[7].idgeneral_template = 5;
            if (failure == "slot") f.Character.form_generals_[0].equipments[0] = new Database.Equipment(); if (failure == "forge") f.Character.equipment_forge = 1; if (failure == "level") f.Character.level = 1;
            if (failure == "mode") ((Database.Price)Global.Templates["enchant_price:2"]).up = 1; if (failure == "money_budget") c.MaxMoney = 9; if (failure == "stone_budget") c.MaxStone = 1;
            f.Capture(); Check(!f.EquipmentModel.Queue(c, out why) && f.Client.Sends == 0, "equipment_reject_" + failure);
        }
        f = new Fixture(); f.ReinforcePage(); ((Database.Price)Global.Templates["enchant_price:2"]).up = 1; c = f.Equip("equipment_enchant"); c.Mode = "evolve"; Check(f.EquipInvoke(c), "equipment_evolve_is_explicit_normal_button_mode");
        f = new Fixture(); f.ReinforcePage(); c = f.Equip("equipment_enchant"); f.Capture(); Check(f.EquipmentModel.Queue(c, out why), "equipment_queue_for_cost_change"); f.Character.money--;
        Check(!f.EquipmentModel.ExecuteGui(c, delegate { }, out why) && f.Client.Sends == 0, "equipment_rechecks_cost_resources_at_gui");
        foreach (string failure in new string[] { "handler_lost", "wrong_id", "no_progress" })
        { f = new Fixture(); f.ReinforcePage(); Check(f.EquipInvoke(f.Equip("equipment_enchant")), "equipment_receipt_setup_" + failure); if (failure == "handler_lost") f.Client.m_handlers.Remove((ushort)1104); f.EquipResult(0, failure == "wrong_id" ? 8 : 7, failure == "no_progress" ? 1 : 2, true); Check(f.EquipmentModel.Json().Contains("\"status\":\"UNKNOWN\"") && !f.EquipmentModel.Queue(f.Equip("equipment_enchant"), out why) && f.Client.Sends == 1, "equipment_unknown_no_retry_" + failure); }
    }
    private static void Spirit(string output)
    {
        Fixture f = new Fixture(); f.Capture(); if (output != null) File.WriteAllText(Path.Combine(output, "spirit-snapshot.json"), f.SpiritModel.Json(), Encoding.UTF8);
        Check(f.SpiritInvoke(f.Pet("spirit_open")), "spirit_normal_low_level_entry");
        DevelopmentCommand c = f.Pet("spirit_train"); Check(f.SpiritInvoke(c) && f.Spirit.TrainCalls == 1, "spirit_fruit_single_request"); f.SpiritResult(1136, 0, 9, 70, 2, 0, true);
        Check(f.SpiritModel.Json().Contains("\"server\":\"PASS\"") && Has(f.SpiritModel.Json(), "server_progress_observed", "true") && Has(f.SpiritModel.Json(), "level", "9"), "spirit_experience_only_progress_is_valid");
        foreach (string failure in new string[] { "strict_food", "zero_food", "premium", "refresh", "page", "level_cap", "high_entry" })
        {
            f = new Fixture(); f.SpiritPage(); c = f.Pet("spirit_train");
            if (failure == "strict_food") c.MaxFood = 4; if (failure == "zero_food") f.Character.spirit_food = 0; if (failure == "premium") f.Spirit.food_type = 1;
            if (failure == "refresh") f.Spirit.m_bRefreshing = true; if (failure == "page") f.Spirit.m_iPage = 1; if (failure == "level_cap") { f.Character.spirit.level = 100; c.DesiredLevel = 100; }
            if (failure == "high_entry") { f.Character.spirit.level = 41; f.Spirit.visible = false; MainUI.m_subui = null; c.DesiredLevel = 42; c.Action = "spirit_open"; }
            f.Capture(); Check(!f.SpiritModel.Queue(c, out stringDiscard) && f.Client.Sends == 0, "spirit_reject_" + failure);
        }
        f = new Fixture(); f.Character.spirit.level = 10; f.Character.spirit.experience = 100; f.SpiritPage(); c = f.Pet("spirit_rankup"); c.MaxFood = 0;
        Check(f.SpiritInvoke(c) && f.Spirit.RankCalls == 1, "spirit_rankup_exact_exp_gate_without_food_spend"); f.SpiritResult(1139, 0, 11, 0, 0, 0, true);
        c = f.Pet("spirit_rankup_confirm"); c.MaxFood = 0; Check(f.SpiritInvoke(c) && !f.Rankup.visible && f.Spirit.EnableCalls == 1 && f.Client.Sends == 1, "spirit_rankup_confirm_preserves_callback_without_new_request");
        c = f.Pet("spirit_close"); c.MaxFood = 0; Check(f.SpiritInvoke(c) && MainUI.m_subui == null, "spirit_normal_close_returns_map");
        foreach (string failure in new string[] { "handler_lost", "error", "gold", "no_progress", "food_mismatch" })
        {
            f = new Fixture(); f.SpiritPage(); Check(f.SpiritInvoke(f.Pet("spirit_train")), "spirit_receipt_setup_" + failure);
            if (failure == "handler_lost") f.Client.m_handlers.Remove((ushort)1136);
            f.SpiritResult(1136, failure == "error" ? 1 : 0, 9, failure == "no_progress" ? 20u : 70u, failure == "food_mismatch" ? 6u : 2u, failure == "gold" ? 1u : 0u, true);
            Check(f.SpiritModel.Json().Contains(failure == "error" ? "\"status\":\"ERROR\"" : "\"status\":\"UNKNOWN\"") && !f.SpiritModel.Queue(f.Pet("spirit_train"), out stringDiscard) && f.Client.Sends == 1, "spirit_terminal_no_retry_" + failure);
        }
        f = new Fixture(); f.Character.spirit.level = 40; f.Character.spirit.experience = 100; f.SpiritPage(); c = f.Pet("spirit_rankup"); c.DesiredLevel = 41;
        Check(f.SpiritInvoke(c), "spirit_level_41_setup"); f.SpiritResult(1139, 0, 41, 0, 0, 0, true); c = f.Pet("spirit_rankup_confirm"); c.DesiredLevel = 41;
        Check(f.SpiritInvoke(c) && DelegateFuncUI.Spirit2Calls == 1, "spirit_41_special_callback_preserved");
    }
    private static string stringDiscard;
    private static void DeferredOpeningAndReuse(string output)
    {
        Fixture f = new Fixture(); string why;
        f.Character.newbiequests[0].isfinish = 1; f.Growth.DeferShow = true;
        DevelopmentCommand c = f.Grow("growth_open"); Check(f.GrowthInvoke(c), "growth_deferred_open_invoked_once");
        f.Capture(); Check(f.GrowthModel.Pending && f.GrowthModel.Json().Contains("\"status\":\"WAITING\"") && f.Growth.OpenCalls == 1, "growth_deferred_open_not_immediately_observed");
        Check(!f.GrowthModel.Queue(f.Grow("growth_open"), out why), "growth_deferred_open_single_flight");
        f.Growth.CompleteOpen(); f.Capture(); Check(!f.GrowthModel.Pending && f.GrowthModel.Json().Contains("normal_open_observed_without_completion_request") && f.Client.Sends == 0, "growth_deferred_open_no_request_witness");
        foreach (string mode in new string[] { "unexpected", "missed", "stop" })
        {
            f = new Fixture(); f.Character.newbiequests[0].isfinish = 1; f.Growth.DeferShow = true;
            Check(f.GrowthInvoke(f.Grow("growth_open")), "growth_deferred_race_setup_" + mode);
            if (mode == "stop") f.GrowthModel.Stop("user_stop");
            f.Growth.CompleteOpen(19);
            f.Client.Packet(810, new byte[0]); if (mode != "missed") f.Capture(); f.Client.Consumed(810); f.Capture();
            Check(f.GrowthModel.Json().Contains("\"status\":\"UNKNOWN\"") && !f.GrowthModel.Queue(f.Grow("growth_open"), out why) && f.Growth.OpenCalls == 1, "growth_deferred_810_unknown_no_retry_" + mode);
        }
        f = new Fixture(); f.Capture(); Check(!f.GrowthModel.Queue(f.Grow("growth_open"), out why) && f.Growth.OpenCalls == 0, "growth_open_rejects_unfinished_requirement");
        f = new Fixture(); f.Character.equipments[7].enchant_level = 2; f.Capture(); Check(!f.GrowthModel.Queue(f.Grow("growth_open"), out why) && Has(f.GrowthModel.Json(), "available_action", "\"growth_check\""), "growth_satisfied_unfinished_task_uses_explicit_check");
        f = new Fixture(); f.Character.newbiequests[0].isfinish = 1; f.Capture(); c = f.Grow("growth_open"); Check(f.GrowthModel.Queue(c, out why), "growth_open_all_finished_queue"); f.Character.newbiequests[0].isfinish = 0;
        Check(!f.GrowthModel.ExecuteGui(c, delegate { }, out why) && f.Growth.OpenCalls == 0, "growth_open_revalidates_all_finished_before_gui");
        f = new Fixture(); f.ReinforcePage(); f.Character.money = 0; f.Character.money64 = 100; Check(f.EquipInvoke(f.Equip("equipment_enchant")), "equipment_money64_only_matches_normal_money_getter"); f.EquipResult(0, 7, 2, true);
        Check(f.EquipmentModel.Json().Contains("\"available_money\":90") && Has(f.EquipmentModel.Json(), "server_progress_observed", "true"), "equipment_money_getter_sum_and_postcost");
        f = new Fixture(); f.ReinforcePage(); ((Database.Price)Global.Templates["enchant_price:2"]).up = 1; c = f.Equip("equipment_enchant"); c.Mode = "evolve"; Check(f.EquipInvoke(c), "equipment_evolve_receipt_setup"); f.EquipResult(0, 7, 2, true);
        Check(Has(f.EquipmentModel.Json(), "server_progress_observed", "true") && f.Character.equipments[7].idequipment_main == 2 && f.Character.equipments[7].idequipment_acc1 == 3, "equipment_evolve_normal_parts_postcondition");
        f = new Fixture(); f.Character.formations_[1].spotgen[0] = 5; f.Capture(); Check(f.EquipmentModel.Json().Contains("\"in_current_formation\":false"), "equipment_protagonist_not_assumed_in_current_form");
        f = new Fixture(); f.ReinforcePage(); c = f.Equip("equipment_enchant"); c.MaxRequests = 1; c.DesiredLevel = 3; Check(f.EquipInvoke(c), "equipment_reuse_first_goal"); f.EquipResult(0, 7, 2, true);
        c = f.Equip("equipment_enchant"); c.OperationId = new string('d', 32); c.MaxRequests = 1; c.DesiredLevel = 3; c.MaxMoney = 12; c.MaxStone = 3;
        string operationBeforeObserve = f.EquipmentModel.OperationJson();
        f.EquipmentModel.Capture(f.Context(), new EquipmentUpgradeTarget { OperationId = c.OperationId, EquipmentId = c.TargetId, DesiredLevel = c.DesiredLevel, Mode = c.Mode, MaxRequests = c.MaxRequests, MaxMoney = c.MaxMoney, MaxStone = c.MaxStone });
        Check(Has(f.EquipmentModel.Json(), "gate", "\"PASS\"") && Has(f.EquipmentModel.Json(), "available_action", "\"equipment_enchant\"") && f.EquipmentModel.OperationJson() == operationBeforeObserve, "equipment_typed_new_operation_observe_does_not_inherit_or_reset_old_budget");
        Check(f.EquipInvoke(c) && f.Client.Sends == 2, "equipment_fresh_operation_owns_fresh_request_and_resource_budgets");
        f = new Fixture(); f.SpiritPage(); c = f.Pet("spirit_train"); c.MaxRequests = 1; Check(f.SpiritInvoke(c), "spirit_reuse_first_goal"); f.SpiritResult(1136, 0, 9, 70, 2, 0, true);
        c = f.Pet("spirit_train"); c.OperationId = new string('d', 32); c.MaxRequests = 1; c.MaxFood = 3; c.DesiredLevel = 10;
        operationBeforeObserve = f.SpiritModel.OperationJson();
        f.SpiritModel.Capture(f.Context(), new SpiritBeastUpgradeTarget { OperationId = c.OperationId, DesiredLevel = c.DesiredLevel, MaxRequests = c.MaxRequests, MaxFood = c.MaxFood });
        Check(Has(f.SpiritModel.Json(), "gate", "\"PASS\"") && Has(f.SpiritModel.Json(), "available_action", "\"spirit_train\"") && f.SpiritModel.OperationJson() == operationBeforeObserve, "spirit_typed_new_operation_observe_does_not_inherit_or_reset_old_budget");
        Check(f.SpiritInvoke(c) && f.Client.Sends == 2, "spirit_fresh_operation_owns_fresh_request_and_food_budgets");
        f = new Fixture(); Check(f.EquipInvoke(f.Equip("equipment_open")), "equipment_async_setup"); f.Equipment.loading = true; f.Equipment.visible = false; f.Capture();
        Check(Has(f.EquipmentModel.Json(), "gate", "\"WAITING_UI\"") && Has(f.EquipmentModel.Json(), "available_action", "\"\"") && MainUI.OpenCalls == 1, "equipment_async_wait_does_not_offer_repeat_open");
        f = new Fixture(); Check(f.SpiritInvoke(f.Pet("spirit_open")), "spirit_async_setup"); f.Spirit.loading = true; f.Spirit.visible = false; f.Capture();
        Check(Has(f.SpiritModel.Json(), "gate", "\"WAITING_UI\"") && Has(f.SpiritModel.Json(), "available_action", "\"\""), "spirit_async_wait_does_not_offer_repeat_open");
        f = new Fixture(); f.ReinforcePage(); Check(f.EquipInvoke(f.Equip("equipment_enchant")), "equipment_response_overlay_setup"); f.Client.Packet(1104, Wire(1, 0, 2, 7, 3, 2, 4, 10)); f.Capture(); f.Client.Consumed(1104);
        f.Reinforce.sended = false; f.Character.equipments[7].enchant_level = 2; f.Character.money -= 10; f.Character.enchant_stone -= 2; f.ExternalGate = "expected_result_overlay"; f.Capture();
        Check(Has(f.EquipmentModel.Json(), "server_progress_observed", "true") && !f.EquipmentModel.Pending, "receipt_observation_survives_expected_interaction_gate");
        if (output != null) File.WriteAllText(Path.Combine(output, "equipment-progress-snapshot.json"), f.EquipmentModel.Json(), Encoding.UTF8);
        f = new Fixture(); f.ReinforcePage(); c = f.Equip("equipment_enchant"); c.MaxRequests = 1; Check(f.EquipInvoke(c), "request_budget_stop_setup"); f.EquipResult(0, 7, 2, true); c = f.Equip("equipment_enchant"); c.DesiredLevel = 3;
        Check(!f.EquipmentModel.Queue(c, out why) && f.Client.Sends == 1, "same_operation_cannot_extend_goal_or_request_budget");
        f = new Fixture(); f.Client.Packet(1104, Wire(1, 0, 2, 7, 3, 2, 4, 10)); DevelopmentNetwork net = DevelopmentNetwork.Sample(f.Client);
        Check(net.Observed && net.Packets.Count == 1 && f.Client.messages.Count == 1 && ((FixtureMessage)f.Client.messages[0]).stream.Position == 0, "queue_sampling_copies_without_consuming_or_seeking");
        f = new Fixture(); f.SpiritPage(); f.Rankup.visible = true; f.Rankup.m_spirit = f.Character.spirit; f.Rankup.OnClose = f.Spirit.EnableAll; f.Capture();
        Check(!f.SpiritModel.Queue(f.Pet("spirit_rankup_confirm"), out why) && f.Rankup.HideCalls == 0, "spirit_manual_or_stale_rankup_popup_not_owned");
    }
    private static void Safety()
    {
        string why; Fixture f;
        foreach (string failure in new string[] { "pause", "stop", "no_gui", "character", "global", "unknown_ui", "preexisting_packet", "handler_replace" })
        {
            f = new Fixture(); f.ReinforcePage(); DevelopmentCommand c = f.Equip("equipment_enchant"); f.Capture();
            if (failure == "unknown_ui") { UnknownOverlay overlay = Fixture.Attach(new UnknownOverlay(), f.GlobalRoot, f.GlobalUi.gameObject); overlay.visible = true; List<UnityEngine.Object> all = new List<UnityEngine.Object>(f.All); all.Add(overlay); f.All = all.ToArray(); f.Capture(); Check(!f.EquipmentModel.Queue(c, out why), "development_unknown_overlay_blocks"); continue; }
            if (failure == "preexisting_packet") { f.Client.Packet(1104, Wire(1, 0, 2, 7, 3, 2, 4, 10)); f.Capture(); Check(!f.EquipmentModel.Queue(c, out why), "development_old_packet_cannot_bind_new_request"); continue; }
            Check(f.EquipmentModel.Queue(c, out why), "development_safety_queue_" + failure);
            if (failure == "pause") { f.Paused = true; f.Capture(); } if (failure == "stop") f.EquipmentModel.Stop("user_stop");
            if (failure == "no_gui") UnityEngine.Event.current = null; if (failure == "character") Global.character_ = new Database.Character(); if (failure == "global") Global.extern_enabled = false;
            if (failure == "handler_replace") { Check(f.EquipmentModel.ExecuteGui(c, delegate { }, out why), "development_handler_original_invoked"); f.Client.m_handlers[(ushort)1104] = new object(); f.Capture(); Check(f.EquipmentModel.Json().Contains("operation_handler_replaced") && f.Client.Sends == 1, "development_replaced_handler_unknown"); continue; }
            Check(!f.EquipmentModel.ExecuteGui(c, delegate { }, out why) && f.Client.Sends == 0, "development_safety_no_effect_" + failure);
        }
        f = new Fixture(); f.ReinforcePage(); DevelopmentCommand first = f.Equip("equipment_enchant"); Check(f.EquipInvoke(first), "development_goal_lock_setup"); f.EquipResult(0, 7, 2, true);
        DevelopmentCommand changed = f.Equip("equipment_close"); changed.MaxMoney++; f.Capture(); Check(!f.EquipmentModel.Queue(changed, out why), "development_operation_goal_immutable");
        Check(DevelopmentPacket.Decode(1104, Wire(1, 0, 2, 7, 3, 2)).Error != null, "packet_required_fields_not_defaulted");
        Check(DevelopmentPacket.Decode(1136, Wire(1, 0, 1, 0)).Error != null, "packet_duplicate_scalar_rejected");
        Check(DevelopmentPacket.Decode(1139, new byte[] { 8, 128 }).Error != null, "packet_truncated_varint_rejected");
        Check(DevelopmentPacket.Decode(810, new byte[0]).Error == null && DevelopmentPacket.Decode(810, new byte[] { 0 }).Error != null, "packet_empty_810_schema_exact");
        Check(DevelopmentPacket.Decode(1139, new byte[] { 8, 0, 18, 2, 1, 2 }).Error == null, "packet_unknown_length_field_bounded_skip");
    }
    private static void ControllerLoadingCapture()
    {
        foreach (string external in new string[] { "loading_or_battle", "global_input_disabled", "unknown_overlay", "loading_or_battle:unknown" })
        {
            bool waitable = external == "loading_or_battle" || external == "global_input_disabled"; string expected = waitable ? "WAITING_UI" : "BLOCKED"; string why;
            Fixture f = new Fixture(); Check(f.EquipInvoke(f.Equip("equipment_open")) && f.EquipInvoke(f.Equip("reinforce_open")), "controller_gate_equipment_normal_ui_setup_" + external);
            string oldOperation = f.EquipmentModel.OperationJson(); DevelopmentCommand pending = f.Equip("equipment_enchant"); f.Capture(); Check(f.EquipmentModel.Queue(pending, out why), "controller_gate_equipment_queue_before_transition_" + external);
            f.ExternalGate = external; SharedDevelopmentContext ctx = f.Context(); f.EquipmentModel.Capture(ctx);
            Check(ctx.Gate == "controller_gate:" + external && Has(f.EquipmentModel.Json(), "gate", Command.Quote(expected)) && Has(f.EquipmentModel.Json(), "reason", Command.Quote(ctx.Gate)) && f.EquipmentModel.OperationJson() == oldOperation, "controller_gate_equipment_capture_preserves_observed_operation_" + external);
            Check(!f.EquipmentModel.ExecuteGui(pending, delegate { throw new Exception("Unexpected publication while input blocked"); }, out why) && !f.EquipmentModel.Queue(f.Equip("equipment_enchant"), out why) && f.Client.Sends == 0 && f.Reinforce.EnchantCalls == 0, "controller_gate_equipment_never_mutates_during_transition_" + external);
            f = new Fixture(); Check(f.SpiritInvoke(f.Pet("spirit_open")), "controller_gate_spirit_normal_ui_setup_" + external); oldOperation = f.SpiritModel.OperationJson();
            f.ExternalGate = external; ctx = f.Context(); f.SpiritModel.Capture(ctx);
            Check(ctx.Gate == "controller_gate:" + external && Has(f.SpiritModel.Json(), "gate", Command.Quote(expected)) && Has(f.SpiritModel.Json(), "reason", Command.Quote(ctx.Gate)) && f.SpiritModel.OperationJson() == oldOperation, "controller_gate_spirit_capture_preserves_observed_operation_" + external);
            Check(!f.SpiritModel.Queue(f.Pet("spirit_train"), out why) && f.Client.Sends == 0 && f.Spirit.TrainCalls == 0, "controller_gate_spirit_never_mutates_during_transition_" + external);
        }
    }
    private static FixtureHandler PrepareCallback(Fixture f, int opcode)
    {
        object target = opcode == 810 || opcode == 811 ? (object)f.Growth : opcode == 1104 ? (object)f.Reinforce : f.Spirit;
        string method = opcode == 810 ? "sc_newbie_quest_finish" : opcode == 811 ? "sc_newbie_quest_award" : opcode == 1104 ? "sc_equipment_enchant_0x450" : opcode == 1136 ? "sc_spirit_train_0x470" : "sc_spirit_levelup_0x473";
        object arg = opcode == 810 ? (object)new List<ushort> { 19 } : opcode == 811 ? (object)(ushort)19 : null;
        f.Client.Send((ushort)opcode, target, method, arg); return (FixtureHandler)f.Client.m_handlers[(ushort)opcode];
    }
    private static byte[] ReplyBody(int opcode, uint result)
    { return opcode == 810 ? new byte[0] : opcode == 1104 ? Wire(1, result, 2, 7, 3, 2, 4, 10) : opcode == 1136 ? Wire(1, result, 2, 2, 3, 0, 4, 70, 5, 9) : opcode == 1139 ? Wire(1, result, 2, 11, 3, 0) : Wire(1, result); }
    private static void CallbackCapturePrototypeChecks()
    {
        string why; string id = new string('e', 32);
        foreach (int opcode in new int[] { 810, 811, 1104, 1136, 1139 })
        {
            Fixture f = new Fixture(); FixtureHandler h = PrepareCallback(f, opcode); Delegate original = h.handler;
            DevelopmentCallbackCapture cap = DevelopmentCallbackCapture.Install(f.Client, h, opcode, id, 17, original, out why);
            Check(cap != null && cap.StillInstalled() && cap.OperationId == id && cap.Sequence == 17 && cap.Opcode == opcode, "callback_prototype_bound_identity_" + opcode);
            byte[] body = ReplyBody(opcode, 0), prefixed = new byte[body.Length + 2]; prefixed[0] = 99; prefixed[1] = 88; Buffer.BlockCopy(body, 0, prefixed, 2, body.Length);
            MemoryStream stream = new MemoryStream(prefixed, false); stream.Position = 2; object arg = h.arg;
            f.Client.m_handlers.Remove((ushort)opcode); h.handler(stream, arg); DevelopmentCallbackWitness w = cap.Read();
            Check(w.Calls == 1 && w.Returned && w.Error == null && w.Packet != null && w.Packet.Opcode == opcode && stream.Position == 2 && FixtureCallbackLog.Calls == 1 && FixtureCallbackLog.Opcode == opcode && object.ReferenceEquals(FixtureCallbackLog.Stream, stream) && object.ReferenceEquals(FixtureCallbackLog.Arg, arg) && FixtureCallbackLog.Position == 2 && FixtureCallbackLog.ExecutingSeen && !DevelopmentCallbackCapture.AnyCallbackExecuting, "callback_prototype_fast_original_forward_no_position_change_" + opcode);
            Check(cap.DisarmAndRestore() == "callback_handler_already_removed" && !cap.Read().Armed, "callback_prototype_removed_handler_not_recreated_" + opcode);
            f = new Fixture(); h = PrepareCallback(f, opcode); cap = DevelopmentCallbackCapture.Install(f.Client, h, opcode, id, 18, h.handler, out why); h.handler(null, h.arg); w = cap.Read();
            Check(w.Calls == 1 && w.Returned && w.NullResponse && w.Packet == null && FixtureCallbackLog.Calls == 1 && FixtureCallbackLog.Stream == null, "callback_prototype_null_forward_exactly_once_" + opcode); cap.DisarmAndRestore();
            if (opcode != 810)
            {
                f = new Fixture(); h = PrepareCallback(f, opcode); cap = DevelopmentCallbackCapture.Install(f.Client, h, opcode, id, 19, h.handler, out why); h.handler(new MemoryStream(ReplyBody(opcode, 1), false), h.arg); w = cap.Read();
                Check(w.Packet != null && w.Packet.Result == 1 && w.Returned && FixtureCallbackLog.Calls == 1, "callback_prototype_server_error_not_swallowed_" + opcode); cap.DisarmAndRestore();
            }
        }
        {
            Fixture f = new Fixture(); FixtureHandler h = PrepareCallback(f, 1104); FixtureHandler.Callback original = h.handler;
            DevelopmentCallbackCapture cap = DevelopmentCallbackCapture.Install(f.Client, h, 1104, id, 20, original, out why); FixtureHandler.Callback wrapper = h.handler;
            Check(cap.DisarmAndRestore() == "callback_restored" && object.ReferenceEquals(h.handler, original) && object.ReferenceEquals(f.Client.m_handlers[(ushort)1104], h), "callback_prototype_cleanup_restores_only_delegate");
            wrapper(new MemoryStream(ReplyBody(1104, 0), false), h.arg); Check(cap.Read().Calls == 0 && FixtureCallbackLog.Calls == 1, "callback_prototype_cleanup_disarms_but_forwards_inflight");
            f = new Fixture(); h = PrepareCallback(f, 1104); cap = DevelopmentCallbackCapture.Install(f.Client, h, 1104, id, 21, h.handler, out why); FixtureHandler replacement = new FixtureHandler { handler = h.handler };
            f.Client.m_handlers[(ushort)1104] = replacement; Check(cap.DisarmAndRestore() == "foreign_handler_preserved" && object.ReferenceEquals(f.Client.m_handlers[(ushort)1104], replacement), "callback_prototype_cleanup_preserves_foreign_handler");
            f = new Fixture(); h = PrepareCallback(f, 1104); original = h.handler; cap = DevelopmentCallbackCapture.Install(f.Client, h, 1104, id, 22, original, out why);
            FixtureHandler.Callback foreign = (FixtureHandler.Callback)Delegate.CreateDelegate(typeof(FixtureHandler.Callback), original.Target, original.Method); h.handler = foreign;
            Check(cap.DisarmAndRestore() == "foreign_callback_preserved" && object.ReferenceEquals(h.handler, foreign), "callback_prototype_cleanup_preserves_foreign_delegate");
            f = new Fixture(); h = PrepareCallback(f, 1136); cap = DevelopmentCallbackCapture.Install(f.Client, h, 1136, id, 23, h.handler, out why); MemoryStream stream = new MemoryStream(ReplyBody(1136, 0), false); h.handler(stream, h.arg); h.handler(stream, h.arg);
            Check(cap.Read().Calls == 2 && cap.Read().Error == "normal_callback_duplicate" && FixtureCallbackLog.Calls == 2, "callback_prototype_duplicate_is_unknown_each_original_still_once"); cap.DisarmAndRestore();
            f = new Fixture(); h = PrepareCallback(f, 811); cap = DevelopmentCallbackCapture.Install(f.Client, h, 811, id, 24, h.handler, out why); h.arg = (ushort)20; h.handler(new MemoryStream(ReplyBody(811, 0), false), h.arg);
            Check(cap.Read().Error == "normal_callback_argument_changed" && (ushort)FixtureCallbackLog.Arg == 20 && FixtureCallbackLog.Calls == 1, "callback_prototype_wrong_arg_still_forwarded"); cap.DisarmAndRestore();
            f = new Fixture(); h = PrepareCallback(f, 1104); cap = DevelopmentCallbackCapture.Install(f.Client, h, 1104, id, 25, h.handler, out why); InvalidOperationException marker = new InvalidOperationException("normal callback marker"); FixtureCallbackLog.Throw = marker; Exception caught = null;
            try { h.handler(new MemoryStream(ReplyBody(1104, 0), false), h.arg); } catch (Exception error) { caught = error; }
            Check(object.ReferenceEquals(caught, marker) && cap.Read().OriginalThrew && !cap.Read().Returned && FixtureCallbackLog.Calls == 1 && !DevelopmentCallbackCapture.AnyCallbackExecuting, "callback_prototype_original_exception_identity_and_type_preserved"); cap.DisarmAndRestore();
            f = new Fixture(); h = PrepareCallback(f, 1104); cap = DevelopmentCallbackCapture.Install(f.Client, h, 1104, id, 26, h.handler, out why); stream = new MemoryStream(new byte[65537], false); stream.Position = 5; h.handler(stream, h.arg);
            Check(cap.Read().Error == "normal_callback_stream_bound" && stream.Position == 5 && FixtureCallbackLog.Calls == 1, "callback_prototype_bound_error_never_suppresses_normal_callback"); cap.DisarmAndRestore();
            f = new Fixture(); h = PrepareCallback(f, 1104); Check(DevelopmentCallbackCapture.Install(f.Client, h, 1104, "wrong", 27, h.handler, out why) == null, "callback_prototype_invalid_operation_id_refused");
            f = new Fixture(); f.Client.Send(16, f.Growth, "sc_newbie_quest_finish", null); h = (FixtureHandler)f.Client.m_handlers[(ushort)16];
            cap = DevelopmentCallbackCapture.InstallRaw16(f.Client, h, id, 28, h.handler, out why); byte[] large = new byte[100001]; large[100000] = 123; stream = new MemoryStream(large, false); stream.Position = 1; h.handler(stream, h.arg);
            DevelopmentCallbackWitness raw = cap.Read(); Check(raw.RawBytes != null && raw.RawBytes.Length == 100000 && raw.RawBytes[99999] == 123 && raw.Packet == null && raw.Error == null && raw.Returned && raw.CallbackReturned && stream.Position == 1, "callback_prototype_raw16_fixed_large_bound_and_return_witness");
            raw.RawBytes[99999] = 0; Check(cap.Read().RawBytes[99999] == 123, "callback_prototype_raw16_witness_bytes_detached"); cap.DisarmAndRestore();
            f = new Fixture(); f.Client.Send(16, f.Growth, "sc_newbie_quest_finish", null); h = (FixtureHandler)f.Client.m_handlers[(ushort)16]; cap = DevelopmentCallbackCapture.InstallRaw16(f.Client, h, id, 29, h.handler, out why); h.handler(new MemoryStream(new byte[1048577], false), h.arg);
            Check(cap.Read().Error == "normal_callback_stream_bound" && cap.Read().RawBytes == null && FixtureCallbackLog.Calls == 1 && cap.Read().Returned, "callback_prototype_raw16_excess_bound_still_forwards"); cap.DisarmAndRestore();
        }
    }
    private static void OwnedCallbackIntegration()
    {
        string why;
        Fixture f = new Fixture(); f.ReinforcePage(); Check(f.EquipInvoke(f.Equip("equipment_enchant")), "callback_equipment_fast_request_normal_handler"); f.EquipResult(0, 7, 2, false);
        Check(Has(f.EquipmentModel.Json(), "server_progress_observed", "true") && Has(f.EquipmentModel.Json(), "status", "\"OBSERVED\"") && FixtureCallbackLog.Calls == 1 && f.Client.Sends == 1 && f.EquipmentModel.Json().Contains("normal_callback_returned_result_semantics_unverified"), "callback_equipment_fast_response_before_any_queue_sample");
        f = new Fixture(); f.SpiritPage(); Check(f.SpiritInvoke(f.Pet("spirit_train")), "callback_spirit_fast_request"); f.SpiritResult(1136, 0, 9, 70, 2, 0, false);
        Check(Has(f.SpiritModel.Json(), "server", "\"PASS\"") && Has(f.SpiritModel.Json(), "server_progress_observed", "true") && FixtureCallbackLog.Calls == 1 && f.Client.Sends == 1, "callback_spirit_fast_response_before_any_queue_sample");
        f = new Fixture(); f.Character.spirit.level = 10; f.Character.spirit.experience = 100; f.SpiritPage(); Check(f.SpiritInvoke(f.Pet("spirit_rankup")), "callback_rankup_fast_request"); f.SpiritResult(1139, 0, 11, 0, 0, 0, false);
        Check(Has(f.SpiritModel.Json(), "server", "\"PASS\"") && Has(f.SpiritModel.Json(), "receipt_opcode", "1139") && FixtureCallbackLog.Calls == 1, "callback_rankup_fast_response_before_any_queue_sample");
        f = new Fixture(); f.Character.equipments[7].enchant_level = 2; Check(f.GrowthInvoke(f.Grow("growth_check")), "callback_growth_fast_check_request"); f.Client.Packet(810, new byte[0]); f.Client.Consumed(810); f.Capture();
        Check(Has(f.GrowthModel.Json(), "server", "\"PASS\"") && Has(f.GrowthModel.Json(), "receipt_opcode", "810") && FixtureCallbackLog.Calls == 1, "callback_growth_empty_response_before_any_queue_sample");
        f.Growth.visible = true; f.Growth.m_questSelected = 19; Check(f.GrowthInvoke(f.Grow("growth_claim")), "callback_growth_fast_claim_request"); f.Client.Packet(811, Wire(1, 0)); f.Client.Consumed(811); f.Character.newbiequests[0].isgot = 1; f.Capture();
        Check(Has(f.GrowthModel.Json(), "server", "\"PASS\"") && Has(f.GrowthModel.Json(), "receipt_opcode", "811") && FixtureCallbackLog.Calls == 2, "callback_growth_claim_response_before_any_queue_sample");
        foreach (string mode in new string[] { "null", "duplicate", "callback_changed", "stop", "throw" })
        {
            f = new Fixture(); f.ReinforcePage(); Check(f.EquipInvoke(f.Equip("equipment_enchant")), "callback_operation_failure_setup_" + mode);
            FixtureHandler h = (FixtureHandler)f.Client.m_handlers[(ushort)1104]; FixtureHandler.Callback wrapper = h.handler;
            if (mode == "null") { f.Client.Consumed(1104); f.Capture(); }
            else if (mode == "duplicate") { wrapper(new MemoryStream(ReplyBody(1104, 0), false), h.arg); wrapper(new MemoryStream(ReplyBody(1104, 0), false), h.arg); f.Capture(); }
            else if (mode == "callback_changed") { h.handler = (FixtureHandler.Callback)Delegate.CreateDelegate(typeof(FixtureHandler.Callback), f.Reinforce, f.Reinforce.GetType().GetMethod("sc_equipment_enchant_0x450", BindingFlags.Instance | BindingFlags.NonPublic)); f.Capture(); Check(!object.ReferenceEquals(h.handler, wrapper), "callback_foreign_delegate_preserved_by_unknown_cleanup"); }
            else if (mode == "stop")
            {
                f.EquipmentModel.Stop("component_stopping"); Check(object.ReferenceEquals(f.Client.m_handlers[(ushort)1104], h) && h.handler.Method.Name == "sc_equipment_enchant_0x450" && h.handler.Target == f.Reinforce, "callback_stop_restores_original_without_removing_game_handler");
                f.Client.Packet(1104, ReplyBody(1104, 0)); f.Client.Consumed(1104); f.Capture(); Check(FixtureCallbackLog.Calls == 1, "callback_original_still_runs_after_stop");
            }
            else
            {
                Exception marker = new InvalidOperationException("callback exact marker"), caught = null; FixtureCallbackLog.Throw = marker; f.Client.Packet(1104, ReplyBody(1104, 0));
                try { f.Client.Consumed(1104); } catch (Exception error) { caught = error; }
                Check(object.ReferenceEquals(marker, caught), "callback_exception_preserved_through_owned_operation"); f.Capture();
            }
            Check(Has(f.EquipmentModel.Json(), "status", "\"UNKNOWN\"") && f.EquipmentModel.Pending && !f.EquipmentModel.Queue(f.Equip("equipment_enchant"), out why) && f.Client.Sends == 1, "callback_operation_unknown_never_retries_" + mode);
        }
        f = new Fixture(); FixtureHandler own = PrepareCallback(f, 1104); DevelopmentCallbackCapture cap = DevelopmentCallbackCapture.Install(f.Client, own, 1104, new string('e', 32), 31, own.handler, out why);
        byte[] body = ReplyBody(1104, 0), prefix = new byte[body.Length + 1]; Buffer.BlockCopy(body, 0, prefix, 1, body.Length); MemoryStream stream = new MemoryStream(prefix, false); stream.Position = 1; FixtureCallbackLog.AdvanceStream = true; own.handler(stream, own.arg);
        Check(cap.Read().Packet != null && cap.Read().Returned && FixtureCallbackLog.Position == 1 && stream.Position == stream.Length, "callback_observer_preserves_original_stream_consumption"); cap.DisarmAndRestore();
        f = new Fixture(); own = PrepareCallback(f, 1104); cap = DevelopmentCallbackCapture.Install(f.Client, own, 1104, new string('e', 32), 32, own.handler, out why);
        System.Threading.ManualResetEvent locked = new System.Threading.ManualResetEvent(false), release = new System.Threading.ManualResetEvent(false);
        System.Threading.Thread t = new System.Threading.Thread(delegate() { lock (f.Client) { locked.Set(); release.WaitOne(2000); } }); t.Start(); Check(locked.WaitOne(1000), "callback_cleanup_contention_fixture_locked");
        string restore = cap.DisarmAndRestore(); release.Set(); t.Join(); locked.Close(); release.Close();
        Check(restore == "callback_restore_busy_forward_only" && !cap.Read().Armed, "callback_cleanup_contention_disarms_observer_without_foreign_write"); own.handler(new MemoryStream(ReplyBody(1104, 0), false), own.arg);
        Check(cap.Read().Calls == 0 && FixtureCallbackLog.Calls == 1 && !DevelopmentCallbackCapture.AnyCallbackExecuting, "callback_cleanup_forward_only_residual_preserves_normal_callback");
    }
    public static int Main(string[] args)
    {
        try
        {
            string output = args.Length == 0 ? null : args[0]; if (output != null) Directory.CreateDirectory(output);
            ReadOnlyAndGrowth(output); Equipment(output); Spirit(output); DeferredOpeningAndReuse(output); Safety(); ControllerLoadingCapture(); CallbackCapturePrototypeChecks(); OwnedCallbackIntegration();
            string result = "{\"status\":\"PASS\",\"checks\":" + checks + ",\"game_access\":false,\"test_names\":[" + String.Join(",", names.ConvertAll(delegate(string n) { return Command.Quote(n); }).ToArray()) + "]}";
            Console.WriteLine(result); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e.ToString()); return 1; }
    }
}

