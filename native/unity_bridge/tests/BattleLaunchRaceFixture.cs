using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using JiangShen.AssistantRuntime;
using game.net.data.StoC;

// Offline stock-boundary fixture. The callback branches are transcribed from
// FormationUI.sc_formupdate, CampaignUI.BattleCampaign, TrialLevelUI.OnAttack
// and tcp_client.Run in the pinned client source. The launch fence itself is
// compiled from production BattleLaunch.cs. No game process or network is used.
namespace UnityEngine
{
    public class Component
    {
        public GameObject gameObject;
        public Component GetComponentInChildren(Type type) { return gameObject.GetComponent(type); }
    }
    public sealed class GameObject
    {
        public static readonly Hashtable Paths = new Hashtable();
        private readonly Hashtable components = new Hashtable();
        public T Attach<T>(T component) where T : Component
        { component.gameObject = this; components[component.GetType()] = component; return component; }
        public Component GetComponent(Type type) { return components[type] as Component; }
        public static GameObject Find(string path) { return Paths[path] as GameObject; }
    }
}
namespace JiangShen.AssistantRuntime
{
    internal sealed class ProbeFailure : Exception { internal ProbeFailure(string why) : base(why) { } }
    internal delegate void DevelopmentNormalCallback(MemoryStream stream, object arg);
    internal sealed class Model
    {
        internal static Type FirstType(string name)
        {
            Type own = typeof(Model).Assembly.GetType(name, false);
            if (own != null) return own;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            { Type type = assembly.GetType(name, false); if (type != null) return type; }
            throw new TypeLoadException(name);
        }
        internal static Type GameType(string name) { return FirstType(name); }
    }
    // Reflection and transport are the detached boundary, not a second launch
    // implementation. Real transport is separately compiled in the payload.
    internal sealed class DailyTransport
    {
        private readonly object client;
        private DailyTransport(object value) { client = value; }
        internal static object CurrentClient(object role) { return ReferenceEquals(role, Global.character_) ? Global.client : null; }
        internal static DailyTransport Read(object value) { return value != null && ReferenceEquals(value, Global.client) ? new DailyTransport(value) : null; }
        internal bool StillCurrent() { return ReferenceEquals(client, Global.client); }
    }
    internal sealed class SharedDevelopmentContext
    {
        internal const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        internal static object Read(object value, string name)
        {
            for (Type type = value.GetType(); type != null; type = type.BaseType)
            {
                FieldInfo field = type.GetField(name, Instance | BindingFlags.DeclaredOnly);
                if (field != null) return field.GetValue(value);
                PropertyInfo property = type.GetProperty(name, Instance | BindingFlags.DeclaredOnly);
                if (property != null && property.GetIndexParameters().Length == 0) return property.GetValue(value, null);
            }
            throw new ProbeFailure("fixture_missing_" + name);
        }
        internal static object Static(Type type, string name) { return type.GetField(name).GetValue(null); }
        internal static void Selection(object value, string name, object selected) { value.GetType().GetField(name).SetValue(value, selected); }
        internal static void Call(object value, string name) { value.GetType().GetMethod(name, Instance).Invoke(value, null); }
        internal static string Q(string value) { return Command.Quote(value); }
        internal static string B(bool value) { return value ? "true" : "false"; }
    }
    internal sealed class BattlePresentationOwnership
    {
        internal BattleMode Mode;
        internal string OperationId;
        internal UnityEngine.Component Source;
        internal object Target;
    }
}
public sealed class Character
{
    public ulong id = 123; public int stamina = 10; public object trialinfo; public SealInfo sealmonsterinfo;
    public Hashtable building_monsters = new Hashtable();
}
public sealed class FixtureTarget
{ public uint id = 1, idtroop = 1, idcampaign = 7; public int Mutations; }
public sealed class FixtureFriend { public ulong id = 987; public Hashtable building_monsters = new Hashtable(); }
public static class CapitalFriendUI { public static FixtureFriend myfriend; }
public sealed class SealInfo
{
    public bool finish1, finish2, finish3, finish4, finish5, finish6;
    public int buff;
}
public static class Global { public static Character character_; public static FixtureClient client; }
public static class BaseAsset { public static Loader lds; }
public sealed class Loader { public UnityEngine.Component currmg; }
public sealed class MainUI : UnityEngine.Component { public delegate bool staminafight(); public staminafight stFight; }
public sealed class BattleUI : UnityEngine.Component
{ public Interpreter interpreter = new Interpreter(); public uint idcampaign, idtroop; public SCBattleEnd_0x408 Embedded; }
public sealed class Interpreter { public object m_report = new object(); }
public sealed class ScenarioUI : UnityEngine.Component
{
    public delegate void Close();
    public Close OnClose;
    public object m_scenario = new object();
}
public sealed class FixtureHandler
{
    public delegate void Callback(MemoryStream stream, object arg);
    public Callback handler;
    public object arg;
}
public sealed class FixtureClient
{
    public readonly Hashtable m_handlers = new Hashtable();
    public FixtureHandler Form, Battle;
    public FixtureHandler.Callback LastDispatchedCallback;
    public object LastDispatchedArgument;
    public int Sends;
    public void Send(ushort opcode, FixtureHandler.Callback callback, object argument)
    {
        FixtureHandler next = new FixtureHandler { handler = callback, arg = argument };
        m_handlers[opcode] = next;
        if (opcode == 298) { Form = next; return; }
        Sends++; Battle = next;
        Console.WriteLine("SEND " + opcode + " " + ((double)Stopwatch.GetTimestamp() / Stopwatch.Frequency).ToString("R", System.Globalization.CultureInfo.InvariantCulture));
    }
    public void Dispatch(ushort opcode, bool duplicate) { Dispatch(opcode, duplicate, ""); }
    public void Dispatch(ushort opcode, bool duplicate, string variant)
    {
        FixtureHandler value = m_handlers[opcode] as FixtureHandler;
        if (value == null) throw new Exception("fixture_handler_missing_" + opcode);
        m_handlers.Remove(opcode);
        FixtureHandler.Callback callback = value.handler;
        LastDispatchedCallback = callback; LastDispatchedArgument = value.arg;
        byte[] body = FixturePackets.Body(opcode, variant);
        MemoryStream stream = new MemoryStream(body);
        if (variant == "position") stream.Position = 1;
        callback(stream, value.arg);
        Console.WriteLine("STREAM " + (Object.ReferenceEquals(stream, FixturePackets.StockStream) ? "same" : "not_forwarded") +
            " " + FixturePackets.StockPosition + " " + stream.Position);
        if (duplicate) callback(new MemoryStream(body), value.arg);
    }
}
public sealed class FormationUI : UnityEngine.Component
{
    public delegate bool Attack();
    public Attack OnAttack;
    public readonly object CharFormation = new object();
    public readonly Hashtable m_lastparam = new Hashtable();
    public bool onfight;
    public int waitstate, NativeCalls;
    private void OnClickSetCurrentForm()
    { waitstate = 2; Global.client.Send(298, sc_formupdate, true); }
    private void sc_formupdate(MemoryStream stream, object arg)
    {
        NativeCalls++;
        if (BattleLaunchRaceFixture.Kind == "hold298")
        { Console.WriteLine("HOLD298"); Console.ReadLine(); }
        waitstate = 0;
        if (onfight && OnAttack != null && !OnAttack()) onfight = false;
    }
}
public class NativeSource : UnityEngine.Component
{
    public object m_troop;
    public object Monster;
    public object rmg;
    public bool m_bFriend;
    public int AttackCalls, ReplyCalls;
    public ushort Opcode;
    private bool drifted;
    public virtual bool OnAttack()
    {
        AttackCalls++;
        if (BattleLaunchRaceFixture.Kind.StartsWith("stamina-drift") && !drifted)
        { Global.character_.stamina = 0; drifted = true; }
        if (Opcode == 0x334 && Global.character_.stamina < 1)
        { BattleLaunchRaceFixture.MainUi.stFight = OnAttack; return true; }
        SendBattle(); return true;
    }
    public void SendBattle()
    {
        if (BattleLaunchRaceFixture.Kind.Contains("hold-send"))
        { Console.WriteLine("HOLD_SEND"); Console.ReadLine(); }
        Global.client.Send(Opcode, sc_battle, null);
    }
    private void sc_battle(MemoryStream stream, object arg)
    {
        ReplyCalls++;
        FixturePackets.StockStream = stream; FixturePackets.StockPosition = stream == null ? -1 : stream.Position;
        if (Opcode == 0x370)
        {
            if (stream != null && BattleLaunchRaceFixture.Kind != "batch-reject")
            {
                SealInfo info = Global.character_.sealmonsterinfo;
                info.finish1 = info.finish2 = info.finish3 = info.finish4 = info.finish5 = info.finish6 = true;
                info.buff = 3;
            }
            return;
        }
        if (stream == null) return;
        byte[] compressed = null;
        SCBattleEnd_0x408 cachedEnd = null;
        if (Opcode == 0x402)
        {
            SCCampaignBattle_0x402 response = ProtoBuf.Serializer.Deserialize<SCCampaignBattle_0x402>(stream);
            if (response.result == 1) return;
            compressed = response.report;
            cachedEnd = response.battle_end;
            BattleLaunchRaceFixture.Battle.idcampaign = response.idcampaign;
            BattleLaunchRaceFixture.Battle.idtroop = response.idtroop;
            // This models the raw stock report/target assignment. Campaign's
            // native report binding can switch the subsequent target to B.
            ((FixtureTarget)((CampaignUI)this).m_troops[(byte)response.idtroop]).Mutations++;
        }
        else if (Opcode == 0x48D)
        {
            SCBuildingMonsterBattle_0x48D response = ProtoBuf.Serializer.Deserialize<SCBuildingMonsterBattle_0x48D>(stream);
            ((FixtureTarget)Global.character_.building_monsters[(byte)response.id]).Mutations++;
            if (response.result == 1) return;
            compressed = response.report;
        }
        else if (Opcode == 0x4C1)
        {
            SCFriendMonsterBattle_0x4C1 response = ProtoBuf.Serializer.Deserialize<SCFriendMonsterBattle_0x4C1>(stream);
            ((FixtureTarget)CapitalFriendUI.myfriend.building_monsters[(byte)response.idmonster]).Mutations++;
            if (response.result == 1 || response.result == 2) return;
            compressed = response.report;
        }
        else if (Opcode == 0x342) compressed = ProtoBuf.Serializer.Deserialize<SCGraveBattle_0x342>(stream).report;
        else if (Opcode == 0x334) compressed = ProtoBuf.Serializer.Deserialize<SCTrialBattle_0x334>(stream).report;
        else if (Opcode == 0x336) compressed = ProtoBuf.Serializer.Deserialize<SCTrialBossBattle_0x336>(stream).report;
        else if (Opcode == 0x368) compressed = ProtoBuf.Serializer.Deserialize<SCSealMonsterBattle_0x368>(stream).report;
        // Stock setReport catches decoding errors and leaves m_report=null,
        // after the callback's earlier native target mutations have happened.
        try
        {
            using (MemoryStream input = new MemoryStream(compressed ?? FixturePackets.Report(false, "")))
            using (Ionic.Zlib.DeflateStream inflate = new Ionic.Zlib.DeflateStream(input, Ionic.Zlib.CompressionMode.Decompress))
                BattleLaunchRaceFixture.Battle.interpreter.m_report = ProtoBuf.Serializer.Deserialize<BattleReport>(inflate);
        }
        catch (Exception) { BattleLaunchRaceFixture.Battle.interpreter.m_report = null; }
        if (Opcode == 0x402) BattleLaunchRaceFixture.Battle.Embedded = cachedEnd;
    }
}
public sealed class CampaignUI : NativeSource
{
    public readonly Hashtable m_troops = new Hashtable();
    public byte m_troopID = 1;
    public uint m_campaignID = 7;
    public override bool OnAttack()
    {
        AttackCalls++;
        if (BattleLaunchRaceFixture.Kind.StartsWith("scenario")) BattleLaunchRaceFixture.Scenario.OnClose = BattleCampaignOK;
        else SendBattle();
        return true;
    }
    public void BattleCampaignOK() { SendBattle(); }
}
// Positive/negative packets use the pinned real pbdata + protobuf-net schemas,
// independently of the guard's parser. Report is opaque bytes in these schemas.
internal static class FixturePackets
{
    internal static MemoryStream StockStream;
    internal static long StockPosition = -1;
    private static byte[] Encode<T>(T value)
    { using (MemoryStream stream = new MemoryStream()) { ProtoBuf.Serializer.Serialize<T>(stream, value); return stream.ToArray(); } }
    internal static byte[] Body(ushort opcode, string variant)
    {
        uint target = variant == "wrong-target" ? 2u : 1u;
        uint result = variant == "reject" ? 1u : variant == "reject-two" ? 2u : 0u;
        byte[] report = Report(opcode == 0x402, variant);
        byte[] bytes;
        if (opcode == 0x402)
        {
            SCBattleEnd_0x408 embedded = variant.StartsWith("embedded") ? new SCBattleEnd_0x408 {
                result = variant == "embedded-rejection" ? 1u : 0u,
                type = variant == "embedded-wrong-type" ? 1u : 0u,
                idcampaign = variant == "embedded-wrong-campaign" ? 8u : 7u,
                idtroop = variant == "embedded-wrong-target" ? 2u : variant == "embedded-missing-target" ? 0u : 1u,
                iswin = variant != "embedded-wrong-win", idbattle_report = variant == "embedded-missing-report-id" ? 0ul : 9001ul } : null;
            bytes = Encode(new SCCampaignBattle_0x402 { idcampaign = variant == "wrong-campaign" ? 8u : 7u,
                idtroop = target, iswin = true, report = report, result = result, battle_end = embedded,
                idbattle_report = variant == "embedded-outer-report-mismatch" ? 9002ul : variant == "embedded-matching-outer" ? 9001ul : 0ul });
        }
        else if (opcode == 0x48D) bytes = Encode(new SCBuildingMonsterBattle_0x48D { result = result, id = target, report = report });
        else if (opcode == 0x4C1) bytes = Encode(new SCFriendMonsterBattle_0x4C1 { result = result,
            idfriend = variant == "wrong-friend" ? 988ul : variant == "without-friend" ? 0ul : 987ul,
            idmonster = variant == "without-target" ? 0u : target, report = report });
        else if (opcode == 0x334) bytes = Encode(new SCTrialBattle_0x334 { result = result,
            idtroop = variant == "without-target" ? 0u : target, report = report, stamina = 9 });
        else if (opcode == 0x342) bytes = Encode(new SCGraveBattle_0x342 { result = result,
            idtroop = variant == "without-target" ? 0u : target, report = report, soldier = 8, showboss = false, endgrave = false });
        else if (opcode == 0x336) bytes = Encode(new SCTrialBossBattle_0x336 { result = result,
            idtroop = variant == "without-target" ? 0u : target, report = report });
        else if (opcode == 0x368) bytes = Encode(new SCSealMonsterBattle_0x368 { result = result,
            idtroop = target, report = report, exp = 2, buff = 3, cd = 4 });
        else if (opcode == 0x370) bytes = Encode(new SCSealMonsterKillall_0x370 {
            exp = BattleLaunchRaceFixture.Kind == "batch-reject" ? 0u : 10u,
            result = BattleLaunchRaceFixture.Kind == "batch-nonzero-result" ? 7u : BattleLaunchRaceFixture.Kind == "batch-reject" ? 1u : 0u,
            buff = BattleLaunchRaceFixture.Kind == "batch-reject" ? 0u : 3u });
        else bytes = new byte[] { 8, 1 };
        if (variant == "empty") return new byte[0];
        if (variant == "packet-oversize") return new byte[1048577];
        if (variant == "missing-required") { byte[] missing = new byte[bytes.Length - 2]; Array.Copy(bytes, 2, missing, 0, missing.Length); return missing; }
        if (variant == "truncated") { Array.Resize(ref bytes, bytes.Length - 1); return bytes; }
        if (variant == "unknown-field") return Append(bytes, new byte[] { 248, 7, 1 });
        if (variant == "duplicate-target") return Append(bytes, new byte[] { 16, 1 });
        if (variant == "wrong-wire") return Append(bytes, new byte[] { 18, 1, 1 });
        if (variant == "overflow") return Append(bytes, new byte[] { 16, 255, 255, 255, 255, 127 });
        return bytes;
    }
    private static byte[] Append(byte[] first, byte[] second)
    { byte[] body = new byte[first.Length + second.Length]; Array.Copy(first, body, first.Length); Array.Copy(second, 0, body, first.Length, second.Length); return body; }
    internal static byte[] Report(bool win, string variant)
    {
        if (variant == "bad-report") return new byte[] { 1, 2, 3, 4 };
        if (variant == "report-mismatch") win = !win;
        BattleReport report = new BattleReport {
            left_army = new BattleReport.ArmyInfo { name = variant == "report-bomb" ? new string('A', 17 * 1024 * 1024) : "A", level = 1, npc = false, initiative = 0 },
            right_army = new BattleReport.ArmyInfo { name = "B", level = 1, npc = false, initiative = 0 },
            result_status = win ? BattleReport.ResultStatus.LEFT_WIN : BattleReport.ResultStatus.LEFT_LOSE };
        byte[] body = variant == "report-missing-required" ? new byte[] { 24, 1 } : Encode(report);
        if (variant == "report-duplicate-status") body = Append(body, new byte[] { 24, 1 });
        return Ionic.Zlib.DeflateStream.CompressBuffer(body);
    }
}
internal static class BattleLaunchRaceFixture
{
    internal static string Kind;
    internal static MainUI MainUi;
    internal static ScenarioUI Scenario;
    internal static BattleUI Battle;
    private static bool validLease = true;
    private static int MainEntry(string[] args)
    {
        string nonce = args[0], epoch = args[1], operation = args[3]; Kind = args[2];
        string durableRoot = Environment.GetEnvironmentVariable("JSB_NATIVE_DURABLE_TEST_ROOT");
        if (!String.IsNullOrEmpty(durableRoot)) NativeBattleStartStore.TestRoot = durableRoot;
        NativeBattleStartStore.TestFault = Environment.GetEnvironmentVariable("JSB_NATIVE_DURABLE_TEST_FAULT") ?? "";
        if (Kind == "abandon")
        {
            Mutex leaked = Mutex.OpenExisting("Local\\JSAssistantBattleLaunch-" + nonce);
            leaked.WaitOne(); Console.WriteLine("ABANDONED"); Environment.Exit(0);
        }
        Global.character_ = new Character(); Global.client = new FixtureClient();
        string durableRole = Environment.GetEnvironmentVariable("JSB_NATIVE_DURABLE_TEST_ROLE");
        if (!String.IsNullOrEmpty(durableRole)) Global.character_.id = UInt64.Parse(durableRole, System.Globalization.CultureInfo.InvariantCulture);
        UnityEngine.GameObject scene = new UnityEngine.GameObject();
        MainUi = scene.Attach(new MainUI()); BaseAsset.lds = new Loader { currmg = MainUi };
        UnityEngine.GameObject global = new UnityEngine.GameObject();
        UnityEngine.GameObject.Paths["Root/RootUI"] = global; Scenario = global.Attach(new ScenarioUI());
        UnityEngine.GameObject battleObject = new UnityEngine.GameObject();
        UnityEngine.GameObject.Paths["Battle/BattleUI"] = battleObject; Battle = battleObject.Attach(new BattleUI());
        NativeSource source = Kind.StartsWith("scenario") || Kind == "campaign" || Kind == "challenge" ? (NativeSource)new CampaignUI() : new NativeSource();
        scene.Attach(source); source.rmg = MainUi; source.m_troop = new FixtureTarget(); source.Monster = source.m_troop;
        FixtureTarget secondTarget = new FixtureTarget { id = 2, idtroop = 2 };
        Global.character_.building_monsters[(byte)1] = source.m_troop; Global.character_.building_monsters[(byte)2] = secondTarget;
        CapitalFriendUI.myfriend = new FixtureFriend();
        CapitalFriendUI.myfriend.building_monsters[(byte)1] = source.m_troop;
        CapitalFriendUI.myfriend.building_monsters[(byte)2] = secondTarget;
        source.m_bFriend = Kind == "friend-building";
        Global.character_.trialinfo = source.m_troop;
        BattleMode mode = Kind == "challenge" ? BattleMode.NormalChallenge : Kind.StartsWith("scenario") || Kind == "campaign" ? BattleMode.Campaign :
            Kind.StartsWith("stamina") ? BattleMode.Trial : Kind == "direct" ? BattleMode.Arena :
            Kind == "own-building" ? BattleMode.OwnBuildingMonster : Kind == "friend-building" ? BattleMode.FriendBuildingMonster :
            Kind == "training" ? BattleMode.TrainingMonster : Kind == "boss" ? BattleMode.TrialBoss : BattleMode.Grave;
        source.Opcode = mode == BattleMode.Campaign || mode == BattleMode.NormalChallenge ? (ushort)0x402 : mode == BattleMode.Trial ? (ushort)0x334 :
            mode == BattleMode.Arena ? (ushort)0x460 : mode == BattleMode.OwnBuildingMonster ? (ushort)0x48D :
            mode == BattleMode.FriendBuildingMonster ? (ushort)0x4C1 : mode == BattleMode.TrainingMonster ? (ushort)0x368 :
            mode == BattleMode.TrialBoss ? (ushort)0x336 : (ushort)0x342;
        if (Kind.StartsWith("batch")) { source.Opcode = 0x370; Global.character_.sealmonsterinfo = new SealInfo(); }
        if (source is CampaignUI) { ((CampaignUI)source).m_troops[(byte)1] = source.m_troop; ((CampaignUI)source).m_troops[(byte)2] = secondTarget; }
        FormationUI formation = scene.Attach(new FormationUI()); formation.OnAttack = source.OnAttack;
        formation.m_lastparam["OnAttack"] = formation.OnAttack;
        BattleLaunch.Initialize(nonce, delegate { return validLease; });
        BattleLaunch.Select(new Command { Name = "daily-script", Sequence = 1, BattleEpoch = epoch, OperationId = operation });
        BattleLaunchAttempt attempt = null;
        if (Kind.StartsWith("batch"))
            attempt = BattleLaunch.NonReport(BattleMode.TrainingMonster, source, Global.character_.sealmonsterinfo,
                delegate { return true; }, source.SendBattle, source.Opcode);
        else if (Kind == "direct")
            attempt = BattleLaunch.Direct(mode, source, source.m_troop, delegate { return true; }, source.SendBattle, source.Opcode);
        else
            attempt = BattleLaunch.Formation(mode, formation, source, source.m_troop, delegate { return true; }, null);
        Console.WriteLine("READY");
        while (true)
        {
            string instruction = Console.ReadLine(); if (instruction == null || instruction == "exit") return 0;
            try
            {
                if (instruction == "formation" || instruction == "duplicate298") Global.client.Dispatch(298, instruction == "duplicate298");
                else if (instruction == "reply" || instruction == "duplicate-reply" || instruction.StartsWith("reply-"))
                {
                    Global.client.Dispatch(source.Opcode, instruction == "duplicate-reply", instruction.StartsWith("reply-") ? instruction.Substring(6) : "");
                    if (Kind == "direct") attempt.InitialResponseApplied(Battle, Battle.interpreter.m_report);
                }
                else if (instruction == "scenario") Scenario.OnClose();
                else if (instruction == "stamina") { Global.character_.stamina = 10; MainUi.stFight(); }
                else if (instruction == "drop-stamina") Global.character_.stamina = 0;
                else if (instruction == "change-role") Global.character_ = new Character();
                else if (instruction == "change-target") source.m_troop = new object();
                else if (instruction == "change-friend") CapitalFriendUI.myfriend = new FixtureFriend { id = 988 };
                else if (instruction == "change-source-root") source.rmg = new MainUI();
                else if (instruction == "change-scenario-data") Scenario.m_scenario = new object();
                else if (instruction == "replace-scenario-callback") Scenario.OnClose = delegate { };
                else if (instruction == "attest")
                    Console.WriteLine("ATTEST " + BattleLaunch.OwnsScenario(Scenario, operation, Global.character_, source, source.m_troop));
                else if (instruction == "lose-handler") Global.client.m_handlers.Remove(source.Opcode);
                else if (instruction == "timeout")
                {
                    FixtureHandler handler = Global.client.m_handlers[source.Opcode] as FixtureHandler;
                    Global.client.m_handlers.Remove(source.Opcode); handler.handler(null, handler.arg);
                }
                else if (instruction == "expire") validLease = false;
                else if (instruction == "stop") { validLease = false; BattleLaunch.Stop(); }
                else if (instruction == "transfer")
                    BattleLaunch.ReportBound(new BattlePresentationOwnership { Mode = mode, OperationId = operation, Source = source, Target = source.m_troop },
                        Global.character_, Global.character_.id, Battle, Battle.interpreter.m_report);
                else if (instruction == "transfer-wrong-operation")
                    BattleLaunch.ReportBound(new BattlePresentationOwnership { Mode = mode, OperationId = new string('f', 32), Source = source, Target = source.m_troop },
                        Global.character_, Global.character_.id, Battle, Battle.interpreter.m_report);
                else if (instruction == "new-attempt")
                {
                    if (Kind.StartsWith("batch")) attempt = BattleLaunch.NonReport(BattleMode.TrainingMonster, source, Global.character_.sealmonsterinfo,
                        delegate { return true; }, source.SendBattle, source.Opcode);
                    else if (Kind == "direct") attempt = BattleLaunch.Direct(mode, source, source.m_troop, delegate { return true; }, source.SendBattle, source.Opcode);
                    else BattleLaunch.Formation(mode, formation, source, source.m_troop, delegate { return true; }, null);
                }
                else if (instruction == "old-reply")
                    Global.client.LastDispatchedCallback(new MemoryStream(FixturePackets.Body(source.Opcode, "")), Global.client.LastDispatchedArgument);
                else if (instruction == "native-end")
                {
                    SCBattleEnd_0x408 cached = Battle.Embedded;
                    if (cached != null && cached.result == 0 && cached.type == 0)
                        ((FixtureTarget)((CampaignUI)source).m_troops[(byte)cached.idtroop]).Mutations++;
                }
                BattleLaunch.Validate();
            }
            catch (Exception error) { Console.WriteLine("ERROR " + error.GetType().Name + " " + error.Message); }
            Console.WriteLine("MUTATIONS " + ((FixtureTarget)source.Monster).Mutations + " " + secondTarget.Mutations +
                " " + Battle.idcampaign + " " + Battle.idtroop);
            Console.WriteLine("EMBEDDED " + (Battle.Embedded == null ? "none" : Battle.Embedded.idcampaign + ":" + Battle.Embedded.idtroop));
            Console.WriteLine("STATE " + attempt.Json());
            Console.WriteLine("COUNTS " + Global.client.Sends + " " + source.AttackCalls + " " + formation.NativeCalls + " " + source.ReplyCalls);
        }
    }
    public static int Main(string[] args)
    {
        try { return MainEntry(args); }
        catch (Exception error) { Console.WriteLine("FAILED " + error.GetType().Name + " " + error.Message); return 2; }
    }
}
