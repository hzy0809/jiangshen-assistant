using System;
using System.Collections;
using System.IO;
using System.Reflection;
using ProtoBuf;
using game.net.data.StoC;
using JiangShen.AssistantRuntime;
using UnityEngine;

internal static class DemonPreflightTests
{
    private const string Operation = "11112222333344445555666677778888";
    private static int checks;
    private static string directory;
    private static void Check(bool value, string message)
    { checks++; if (!value) throw new Exception(message); }
    private static object Get(object target, string name)
    { return target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance).GetValue(target); }
    private static byte[] Serialize<T>(T packet)
    { using (MemoryStream body = new MemoryStream()) { Serializer.Serialize(body, packet); return body.ToArray(); } }
    private static object Message(Type type, params object[] fields)
    {
        object packet = Activator.CreateInstance(type);
        for (int i = 0; i < fields.Length; i += 2)
        {
            PropertyInfo p = type.GetProperty((string)fields[i]);
            p.SetValue(packet, Convert.ChangeType(fields[i + 1], p.PropertyType), null);
        }
        return packet;
    }
    private static byte[] ReportBytes(int status)
    { return ReportBytes(status, "own-role"); }
    private static byte[] ReportBytes(int status, string leftName)
    {
        BattleReport report = new BattleReport();
        report.left_army = new BattleReport.ArmyInfo { name = leftName, level = 30, npc = false, initiative = 1 };
        report.right_army = new BattleReport.ArmyInfo { name = "own-demon", level = 30, npc = true, initiative = 1 };
        report.result_status = (BattleReport.ResultStatus)(status < 1 || status > 3 ? 1 : status);
        byte[] bytes = Serialize(report);
        return status < 1 || status > 3 ? Append(Without(bytes, 3), 24, (byte)status) : bytes;
    }
    private static byte[] Compress(byte[] bytes)
    {
        using (MemoryStream result = new MemoryStream())
        {
            using (Ionic.Zlib.DeflateStream stream = new Ionic.Zlib.DeflateStream(result, Ionic.Zlib.CompressionMode.Compress, true))
                stream.Write(bytes, 0, bytes.Length);
            return result.ToArray();
        }
    }
    private static byte[] Packet(bool win, int status, bool bonuses = true, byte[] report = null, uint result = 0)
    {
        SCDemonBattle_0x492 packet = new SCDemonBattle_0x492();
        packet.result = result; packet.iswin = win; packet.report = report ?? Compress(ReportBytes(status));
        PropertyInfo cooldown = typeof(SCDemonBattle_0x492).GetProperty("cooldown");
        cooldown.SetValue(packet, Message(cooldown.PropertyType, "id", 5, "sub_id", 0, "time", 106500, "in_cd", true), null);
        if (bonuses)
        {
            PropertyInfo property = typeof(SCDemonBattle_0x492).GetProperty("bonuses");
            property.SetValue(packet, Activator.CreateInstance(property.PropertyType), null);
            packet.bonuses.bonuses.Add(new Treasure { idtreasure_type = 1, param0 = 10, param1 = 0, total_value = 10 });
        }
        return Serialize(packet);
    }
    private static byte[] Without(byte[] bytes, int remove)
    {
        using (MemoryStream output = new MemoryStream())
        {
            int offset = 0;
            while (offset < bytes.Length)
            {
                int begin = offset; ulong tag = Varint(bytes, ref offset); int wire = (int)(tag & 7);
                if (wire == 0) Varint(bytes, ref offset);
                else if (wire == 2) { int length = checked((int)Varint(bytes, ref offset)); offset += length; }
                else if (wire == 1) offset += 8; else if (wire == 5) offset += 4; else throw new Exception("fixture unsupported wire");
                if ((int)(tag >> 3) != remove) output.Write(bytes, begin, offset - begin);
            }
            return output.ToArray();
        }
    }
    private static ulong Varint(byte[] bytes, ref int offset)
    {
        ulong value = 0; int bits = 0;
        while (true) { byte b = bytes[offset++]; value |= (ulong)(b & 127) << bits; if (b < 128) return value; bits += 7; }
    }
    private static byte[] Append(byte[] left, params byte[] right)
    { byte[] result = new byte[left.Length + right.Length]; Buffer.BlockCopy(left, 0, result, 0, left.Length); Buffer.BlockCopy(right, 0, result, left.Length, right.Length); return result; }
    private sealed class Fixture
    {
        internal readonly RootMessenger Home;
        internal readonly Model Environment;
        internal readonly DailyMonsterModel Model;
        internal readonly Database.Character Character;
        internal readonly Database.Demon Target;
        internal readonly DemonScript Script;
        internal readonly BattleUI Battle;
        internal readonly BattleNormalResultUI Result;
        internal readonly BattleResultUI Reward;
        internal readonly game.net.tcp_client Client;
        internal game.net.tcp_base.Handler Handler;
        internal DevelopmentNormalCallback Wrapper;
        private int sequence;
        internal Fixture()
        {
            BattleSettlement.Presentations = 0; BattleLaunch.Last = null;
            TimeUtil._now = 106400; TimeUtil._thisDay = 86400;
            Global.relogin = false; Global.extern_enabled = true; RootUI.m_bRefreshFive = false;
            Character = new Database.Character(); Global.character_ = Character;
            Character.acts[(uint)24] = new DemonActivity();
            Client = new game.net.tcp_client(); Global.m_client = RootUI.m_client = Client;
            game.net.tcp_base.connection_state = 3;
            Home = new RootMessenger(); Home.gameObject.Attach(Home);
            RootUI rootUi = Home.gameObject.Attach(new RootUI { rmg = Home });
            Home.gameObject.Attach(new MainUI { rmg = Home });
            BaseUI.rootUI = rootUi; MainUI.m_subui = null; __BaseUI.battle_result_ui = null;
            Target = new Database.Demon { mesh = new GameObject() }; Character.m_demon = Target;
            Target.mesh.transform.parent = Home.transform;
            Script = Target.mesh.Attach(new DemonScript { rmg = Home, m_demon = Target });
            FormationUI formation = new FormationUI { rmg = Home, enabled = false };
            formation.gameObject.Attach(formation);
            Battle = new BattleUI { rmg = new RootMessenger { LevelID = "Battle" }, enabled = false };
            Battle.gameObject.Attach(Battle);
            Result = Battle.gameObject.Attach(new BattleNormalResultUI { rmg = Battle.rmg, enabled = false });
            Reward = new BattleResultUI { rmg = Home, enabled = false }; Reward.gameObject.Attach(Reward);
            BaseAsset.lds = new Loader { enabled = false, isBattling = false, currmg = Home };
            Environment = new Model { Root = Home, Battle = Battle.gameObject,
                All = new UnityEngine.Object[] { Home, rootUi, Script, formation, Battle, Result, Reward } };
            DemonFixtureWorld.Model = Environment; DemonFixtureWorld.Formation = formation;
            DemonFixtureWorld.Battle = Battle; DemonFixtureWorld.Reward = Reward;
            Model = new DailyMonsterModel();
        }
        internal void Capture() { Model.Capture(Environment, 24, false); }
        internal void Action(string action)
        {
            string why;
            Check(Model.Queue(24, 78, Operation, ++sequence, Model.Revision, action, out why), "queue " + action + ": " + why + " " + Model.Json());
            Check(Model.ExecuteGui(delegate { }, delegate { return true; }, out why), "execute " + action + ": " + why);
        }
        internal void Start()
        {
            Capture(); Action("open"); Capture(); Action("prepare"); Capture(); Action("start");
            Check(Client.Sent.Count == 1 && Client.Sent[0] == 0x492, "only own-demon stock 0x492 sent");
            Handler = (game.net.tcp_base.Handler)Client.m_handlers[(ushort)0x492]; Wrapper = Handler.handler;
        }
        internal void Unknown(string label)
        {
            Capture();
            Check(Model.OutcomeUnknown && Model.Json().Contains("\"next_action\":\"\""), label + " retains UNKNOWN and no retry: " + Model.Json());
            Check(Script.CallbackCalls == 0 && !Target.killed && Reward.Configured == 0 && Character.cooldowns.Count == 0,
                label + " blocks stock kill/cooldown/reward mutation");
            Check(Client.Sent.Count == 1 && Client.Sent[0] == 0x492, label + " never sends extra/0x408/paid packets");
            Console.WriteLine("{\"scenario\":" + Command.Quote(label) + ",\"stock_callbacks\":" + Script.CallbackCalls +
                ",\"target_killed\":" + (Target.killed ? "true" : "false") + ",\"reward_configured\":" + Reward.Configured +
                ",\"cooldown_count\":" + Character.cooldowns.Count + ",\"sent_opcodes\":[1170],\"model\":" + Model.Json() + "}");
        }
    }
    private static void Baseline()
    {
        Fixture f = new Fixture(); f.Start();
        byte[] bytes = Packet(true, 2); File.WriteAllBytes(Path.Combine(directory, "wrong-win-envelope-loss-report.bin"), bytes);
        f.Client.Dispatch(0x492, bytes); f.Capture();
        Check(f.Script.CallbackCalls == 1 && f.Target.killed && f.Reward.Configured == 1 && f.Character.cooldowns.Count == 1,
            "baseline counterexample must mutate actual stock targets before mismatch detection");
        Check(f.Battle.interpreter.m_report.result_status == BattleReport.ResultStatus.LEFT_LOSE && f.Model.OutcomeUnknown,
            "baseline report is real protobuf LEFT_LOSE, despite killed/reward mutation and late UNKNOWN");
        Console.WriteLine("{\"scenario\":\"baseline-wrong-win\",\"stock_callbacks\":1,\"target_killed\":true,\"reward_configured\":1,\"cooldown_applied\":true,\"report_status\":2,\"model_unknown\":true}");
    }
    private static void Fixed()
    {
        Fixture f = new Fixture(); f.Start();
        byte[] bytes = Packet(true, 2); File.WriteAllBytes(Path.Combine(directory, "wrong-win-envelope-loss-report.bin"), bytes);
        f.Client.Dispatch(0x492, bytes); f.Unknown("wrong-win");
        Console.WriteLine("{\"scenario\":\"fixed-wrong-win\",\"stock_callbacks\":0,\"target_killed\":false,\"reward_configured\":0,\"cooldown_applied\":false,\"model_unknown\":true}");
    }
    private static void Positive(bool win, int status, bool omitIsWin)
    {
        Fixture f = new Fixture(); f.Start();
        byte[] bytes = Packet(win, status);
        if (omitIsWin) bytes = Without(bytes, 2);
        byte[] before = (byte[])bytes.Clone();
        MemoryStream stream = f.Client.Dispatch(0x492, bytes);
        Check(f.Script.CallbackCalls == 1 && ReferenceEquals(f.Script.CallbackStream, stream) &&
            f.Script.CallbackPosition == 0 && f.Script.CallbackArg == null, "valid stock receives original stream, cursor and arg once: " + f.Model.Json());
        Check(Convert.ToBase64String(bytes) == Convert.ToBase64String(before), "preflight leaves the packet unchanged");
        Check(f.Target.killed == win && f.Reward.Configured == (win ? 1 : 0) &&
            f.Target.ArmyUpdates == (win ? 0 : 1), "stock retains win/reward versus non-win army side effect ownership");
        Check(f.Battle.interpreter.m_report.result_status == (BattleReport.ResultStatus)status &&
            BattleLaunch.Last.InitialCalls == 1 && ReferenceEquals(BattleLaunch.Last.Battle, f.Battle) &&
            ReferenceEquals(BattleLaunch.Last.Report, f.Battle.interpreter.m_report), "actual parsed report binds once to launch proof");
        f.Capture();
        Check(!f.Model.OutcomeUnknown && f.Model.TryPresentSettledBattle(f.Battle) && BattleSettlement.Presentations == 1,
            "valid callback yields own-demon settled evidence without 0x408: " + f.Model.Json());
        f.Result.enabled = true; f.Capture(); f.Action("close_battle");
        Check(f.Battle.ExitCalls == 1 && ReferenceEquals(f.Battle.m_prevUI, f.Home) &&
            ReferenceEquals(f.Environment.Root, f.Home) && (win ? f.Reward.enabled : f.Home.Enables == 1), "valid battle returns to original own mode/page");
        if (win) { f.Capture(); f.Action("close_result"); Check(f.Script.Returns == 1 && f.Home.Enables == 1, "stock reward close returns original own root"); }
        Check(f.Client.Sent.Count == 1 && f.Client.Sent[0] == 0x492 && f.Character.gold == 100 && f.Character.bind_gold == 200 &&
            f.Character.gold_consume == 0, "valid path preserves premium and sends only initial settled 0x492");
        Console.WriteLine("{\"scenario\":\"positive\",\"win\":" + (win ? "true" : "false") + ",\"report_status\":" + status +
            ",\"stock_callbacks\":" + f.Script.CallbackCalls + ",\"reward_items\":" + f.Reward.RewardItems + ",\"battle_exits\":" + f.Battle.ExitCalls +
            ",\"original_page\":true,\"premium_unchanged\":true,\"sent_opcodes\":[1170]}");
    }
    private static void Invalid(string name)
    {
        Fixture f = new Fixture(); f.Start(); byte[] bytes = Packet(true, 1);
        if (name == "wrong-win") bytes = Packet(true, 2);
        else if (name == "wrong-loss") bytes = Packet(false, 1);
        else if (name == "missing-status") bytes = Packet(false, 2, true, Compress(Without(ReportBytes(2), 3)));
        else if (name == "missing-left-army") bytes = Packet(true, 1, true, Compress(Without(ReportBytes(1), 1)));
        else if (name == "missing-right-army") bytes = Packet(true, 1, true, Compress(Without(ReportBytes(1), 2)));
        else if (name == "missing-army-required") bytes = Packet(true, 1, true, Compress(new byte[] { 10, 0, 18, 0, 24, 1 }));
        else if (name == "invalid-status") bytes = Packet(false, 0);
        else if (name == "malformed-report") bytes = Packet(true, 1, true, new byte[] { 255, 1, 2, 3 });
        else if (name == "truncated-deflate") { byte[] compressed = Compress(ReportBytes(1)); Array.Resize(ref compressed, compressed.Length - 1); bytes = Packet(true, 1, true, compressed); }
        else if (name == "trailing-deflate") bytes = Packet(true, 1, true, Append(Compress(ReportBytes(1)), 0));
        else if (name == "empty-inflate") bytes = Packet(false, 2, true, Compress(new byte[0]));
        else if (name == "truncated-protobuf") bytes = Packet(true, 1, true, Compress(new byte[] { 10, 255 }));
        else if (name == "inflate-limit") bytes = Packet(true, 1, true, Compress(ReportBytes(1, new string('x', 16777217))));
        else if (name == "missing-result") bytes = Without(bytes, 1);
        else if (name == "missing-report") bytes = Without(bytes, 3);
        else if (name == "missing-cooldown") bytes = Without(bytes, 5);
        else if (name == "missing-bonuses") bytes = Packet(true, 1, false);
        else if (name == "duplicate-result") bytes = Append(bytes, 8, 0);
        else if (name == "duplicate-win") bytes = Append(bytes, 16, 0);
        else if (name == "invalid-win-bool") bytes = Append(Without(bytes, 2), 16, 2);
        else if (name == "invalid-result-uint32") bytes = Append(Without(bytes, 1), 8, 255, 255, 255, 255, 31);
        else if (name == "bad-cooldown-id") bytes = Append(Without(bytes, 5), 42, 2, 8, 4);
        else if (name == "duplicate-cooldown-id") bytes = Append(Without(bytes, 5), 42, 4, 8, 5, 8, 4);
        else if (name == "invalid-cooldown-bool") bytes = Append(Without(bytes, 5), 42, 4, 8, 5, 16, 2);
        else if (name == "unknown-result") bytes = new byte[] { 8, 3 };
        else if (name == "unsafe-rejection-one") bytes = new byte[] { 8, 1 };
        else if (name == "rejection-bad-report") bytes = Packet(false, 2, true, new byte[] { 255, 1, 2, 3 }, 2);
        else if (name == "rejection-inconsistent-report") bytes = Packet(false, 1, true, null, 2);
        else if (name == "empty-packet") bytes = new byte[0];
        else if (name == "packet-limit") bytes = new byte[4194305];
        else if (name == "timeout") bytes = null;
        else throw new Exception("unrecognized invalid scenario: " + name);
        if (bytes != null) File.WriteAllBytes(Path.Combine(directory, name + ".bin"), bytes);
        f.Client.Dispatch(0x492, bytes); f.Unknown(name);
    }
    private static void Boundary(string name)
    {
        Fixture f = new Fixture(); f.Start();
        if (name == "role-object") Global.character_ = new Database.Character { m_demon = f.Target };
        else if (name == "role-id") f.Character.id++;
        else if (name == "target-object") f.Character.m_demon = new Database.Demon { mesh = f.Target.mesh };
        else if (name == "source-target") f.Script.m_demon = new Database.Demon();
        else if (name == "target-mesh") f.Target.mesh = new GameObject();
        else if (name == "clear-time") f.Target.clear_time++;
        else if (name == "source-root") f.Script.rmg = new RootMessenger();
        else if (name == "friend-mode") f.Script.m_bFriend = true;
        else if (name == "handler") f.Client.m_handlers[(ushort)0x492] = new game.net.tcp_base.Handler { handler = delegate { throw new Exception("replacement invoked"); } };
        else if (name == "handler-callback") f.Handler.handler = delegate { throw new Exception("replacement invoked"); };
        else if (name == "handler-table") f.Client.m_handlers = new Hashtable();
        else if (name == "socket") f.Client.m_client = new game.net.FixtureSocket();
        else if (name == "transport-stream") f.Client.m_stream = new MemoryStream();
        else if (name == "disconnected") f.Client.m_client.Connected = false;
        else if (name == "clock-cycle") { TimeUtil._now += 86400; TimeUtil._thisDay += 86400; }
        else if (name == "gold") f.Character.gold++;
        else if (name == "bound-gold") f.Character.bind_gold++;
        else if (name == "gold-consume") f.Character.gold_consume++;
        else if (name == "launch-missing") typeof(DailyMonsterModel).GetField("roundLaunch", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(f.Model, null);
        else throw new Exception("unrecognized boundary scenario: " + name);
        f.Wrapper(new MemoryStream(Packet(true, 1), false), null); f.Unknown(name);
    }
    private static void TimeoutCleanup()
    {
        Fixture f = new Fixture(); f.Start();
        f.Wrapper(null, null); f.Unknown("timeout-cleanup");
        Check(ReferenceEquals(f.Handler.handler, f.Wrapper), "UNKNOWN cleanup keeps an inert wrapper instead of restoring an untrusted native callback");
        f.Client.Dispatch(0x492, Packet(true, 1)); f.Unknown("late-after-timeout");
    }
    private static void StopCleanup()
    {
        Fixture f = new Fixture(); f.Start(); f.Model.Stop("fixture-stop");
        Check(ReferenceEquals(f.Handler.handler, f.Wrapper), "stop leaves pending wrapper inert for late response");
        f.Client.Dispatch(0x492, Packet(true, 1)); f.Unknown("late-after-stop");
    }
    private static void Duplicate()
    {
        Fixture f = new Fixture(); f.Start(); byte[] bytes = Packet(true, 1); f.Client.Dispatch(0x492, bytes);
        f.Wrapper(new MemoryStream(bytes, false), null); f.Capture();
        Check(f.Model.OutcomeUnknown && f.Script.CallbackCalls == 1 && f.Reward.Configured == 1 && BattleLaunch.Last.InitialCalls == 1,
            "duplicate cannot apply second stock callback/proof and stays UNKNOWN");
        Check(!f.Model.TryPresentSettledBattle(f.Battle), "duplicate cannot produce presentation success");
    }
    private static void Rejection()
    {
        Fixture f = new Fixture(); f.Start(); MemoryStream stream = f.Client.Dispatch(0x492, new byte[] { 8, 2 }); f.Capture();
        Check(f.Script.CallbackCalls == 1 && f.Script.Rejections == 1 && ReferenceEquals(f.Script.CallbackStream, stream) &&
            f.Model.OutcomeUnknown && !f.Target.killed && f.Reward.Configured == 0 && f.Character.cooldowns.Count == 0 &&
            BattleLaunch.Last.InitialCalls == 0, "proved result=2 rejection forwards stock control restoration once but remains UNKNOWN");
        Console.WriteLine("{\"scenario\":\"safe-rejection-2\",\"stock_callbacks\":1,\"rejections\":1,\"model\":" + f.Model.Json() + "}");
    }
    private sealed class DuringCopyStream : MemoryStream
    {
        internal Action Mutation;
        internal int Copies;
        internal DuringCopyStream(byte[] bytes) : base(bytes, false) { }
        public override byte[] ToArray()
        {
            byte[] copy = base.ToArray(); Copies++;
            if (Copies == 1 && Mutation != null) Mutation();
            return copy;
        }
    }
    private static void BoundaryDuringDecode()
    {
        Fixture f = new Fixture(); f.Start();
        DuringCopyStream stream = new DuringCopyStream(Packet(true, 1));
        stream.Mutation = delegate { f.Character.gold++; };
        f.Wrapper(stream, null); f.Unknown("boundary-after-copy");
        Check(stream.Copies == 2 && stream.Position == 0, "post-decode boundary recheck catches changed state before callback without consuming input");
    }
    private static void PostSubstitution()
    {
        Fixture f = new Fixture(); f.Start();
        f.Script.DuringCallback = delegate { f.Battle.interpreter.m_report = Serializer.Deserialize<BattleReport>(new MemoryStream(ReportBytes(2))); };
        f.Client.Dispatch(0x492, Packet(true, 1)); f.Capture();
        Check(f.Model.OutcomeUnknown && f.Script.CallbackCalls == 1 && BattleLaunch.Last.InitialCalls == 0 &&
            !f.Model.TryPresentSettledBattle(f.Battle), "post-callback report substitution cannot establish proof or success");
    }
    private static void PostProofMutation()
    {
        Fixture f = new Fixture(); f.Start(); f.Client.Dispatch(0x492, Packet(true, 1));
        Check(f.Script.CallbackCalls == 1 && BattleLaunch.Last.InitialCalls == 1, "valid report initially established proof");
        f.Battle.interpreter.m_report.result_status = BattleReport.ResultStatus.LEFT_LOSE;
        f.Capture(); Check(!f.Model.TryPresentSettledBattle(f.Battle) && f.Model.OutcomeUnknown && BattleSettlement.Presentations == 0,
            "same-object report mutation after proof cannot produce success");
    }
    private sealed class OversizeStream : MemoryStream
    {
        internal int Copies;
        public override long Length { get { return 4194305; } }
        public override byte[] ToArray() { Copies++; throw new Exception("oversize body copied"); }
    }
    private static void InputLimitBeforeCopy()
    {
        Fixture f = new Fixture(); f.Start(); OversizeStream stream = new OversizeStream();
        f.Wrapper(stream, null); f.Unknown("input-limit-before-copy"); Check(stream.Copies == 0, "packet length bound enforced before body allocation");
    }
    private static void NonzeroCursor()
    {
        Fixture f = new Fixture(); f.Start(); MemoryStream stream = new MemoryStream(Packet(true, 1), false); stream.Position = 2;
        f.Wrapper(stream, null); f.Unknown("nonzero-cursor"); Check(stream.Position == 2, "rejected input cursor is unchanged");
    }
    private static void Schema()
    {
        foreach (Type type in new[] { typeof(SCDemonBattle_0x492), typeof(SCDemonBattle_0x492).GetProperty("cooldown").PropertyType,
            typeof(SCDemonBattle_0x492).GetProperty("bonuses").PropertyType, typeof(BattleReport), typeof(BattleReport.ArmyInfo) })
        {
            Console.WriteLine(type.FullName);
            foreach (PropertyInfo property in type.GetProperties())
                foreach (ProtoMemberAttribute member in property.GetCustomAttributes(typeof(ProtoMemberAttribute), false))
                    Console.WriteLine("  " + member.Tag + " " + property.Name + " " + property.PropertyType.FullName + " required=" + member.IsRequired);
        }
    }
    private static void CodecTrace()
    {
        byte[] compressed = Compress(ReportBytes(1));
        Console.WriteLine("compressed=" + BitConverter.ToString(compressed));
        byte[] truncated = new byte[compressed.Length - 1]; Buffer.BlockCopy(compressed, 0, truncated, 0, truncated.Length);
        byte[][] variants = new[] { compressed, Append(compressed, 0), truncated, Append(truncated, 0) };
        string[] names = new[] { "raw", "raw-zero-sentinel", "truncated", "truncated-zero-sentinel" };
        for (int variant = 0; variant < variants.Length; variant++)
        {
            Ionic.Zlib.ZlibCodec codec = new Ionic.Zlib.ZlibCodec();
            Console.WriteLine("variant=" + names[variant] + " init=" + codec.InitializeInflate(false));
            codec.InputBuffer = variants[variant]; codec.NextIn = 0; codec.AvailableBytesIn = variants[variant].Length;
            using (MemoryStream output = new MemoryStream())
            {
                for (int i = 0; i < 3; i++)
                {
                    codec.OutputBuffer = new byte[8192]; codec.NextOut = 0; codec.AvailableBytesOut = 8192;
                    int result = codec.Inflate(Ionic.Zlib.FlushType.None);
                    output.Write(codec.OutputBuffer, 0, codec.NextOut);
                    Console.WriteLine("step=" + i + " result=" + result + " in=" + codec.AvailableBytesIn + " nextIn=" + codec.NextIn +
                        " totalIn=" + codec.TotalBytesIn + " out=" + codec.NextOut + " output=" + BitConverter.ToString(output.ToArray()) + " msg=" + codec.Message);
                }
            }
            codec.EndInflate();
        }
    }
    private static int Main(string[] args)
    {
        try
        {
            directory = args[0]; Directory.CreateDirectory(directory);
            if (args[1] == "schema") Schema();
            else if (args[1] == "codec-trace") CodecTrace();
            else if (args[1] == "helper-win")
            {
                object packet = BattleReplyPreflight.Deserialize(Packet(true, 1), "game.net.data.StoC.SCDemonBattle_0x492");
                object proof = BattleReplyPreflight.Validate((byte[])Get(packet, "_report"), true);
                Check(proof is BattleReport, "helper returns actual BattleReport");
            }
            else if (args[1] == "baseline") Baseline();
            else if (args[1] == "fixed") Fixed();
            else if (args[1] == "win") Positive(true, 1, false);
            else if (args[1] == "loss") Positive(false, 2, false);
            else if (args[1] == "draw") Positive(false, 3, false);
            else if (args[1] == "loss-default-win") Positive(false, 2, true);
            else if (args[1] == "timeout-cleanup") TimeoutCleanup();
            else if (args[1] == "stop-cleanup") StopCleanup();
            else if (args[1] == "duplicate") Duplicate();
            else if (args[1] == "rejection") Rejection();
            else if (args[1] == "post-substitution") PostSubstitution();
            else if (args[1] == "post-proof-mutation") PostProofMutation();
            else if (args[1] == "input-limit-before-copy") InputLimitBeforeCopy();
            else if (args[1] == "nonzero-cursor") NonzeroCursor();
            else if (args[1] == "boundary-during-decode") BoundaryDuringDecode();
            else if (args[1].StartsWith("boundary-", StringComparison.Ordinal)) Boundary(args[1].Substring(9));
            else Invalid(args[1]);
            Console.WriteLine("{\"status\":\"PASS\",\"checks\":" + checks + "}"); return 0;
        }
        catch (Exception error) { Console.WriteLine(error); return 1; }
    }
}
