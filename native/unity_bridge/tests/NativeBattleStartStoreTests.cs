using System;
using System.IO;
using System.Globalization;
using JiangShen.AssistantRuntime;

namespace JiangShen.AssistantRuntime
{
    internal sealed class ProbeFailure : Exception { internal ProbeFailure(string message) : base(message) { } }
}

// The same store is compiled against the game's pinned legacy mscorlib. All
// filesystem effects are restricted to the caller's isolated test directory.
internal static class NativeBattleStartStoreTests
{
    private const string Operation = "00000000000000000000000000000043";
    private static int checks;
    private delegate void CheckAction();
    private static void Check(bool value, string why) { checks++; if (!value) throw new Exception(why); }
    private static void Block(CheckAction action)
    {
        try { action(); } catch (ProbeFailure) { checks++; return; }
        throw new Exception("unsafe action was allowed");
    }
    private static void Fresh(string root)
    {
        NativeBattleStartStore.TestRoot = Path.Combine(root, Guid.NewGuid().ToString("N"));
        NativeBattleStartStore.TestFault = "";
    }
    private static NativeBattleStartTicket Reserve() { return NativeBattleStartStore.Reserve(43, "Campaign", Operation); }
    private static void Self(string root)
    {
        Fresh(root);
        NativeBattleStartTicket first = Reserve(); NativeBattleStartStore.RequirePending(first);
        Block(delegate { Reserve(); });
        Check(File.Exists(Path.Combine(first.Directory, first.Generation + ".start")), "published before send");
        NativeBattleStartStore.Confirm(first, "stock-0x408:report:57");
        NativeBattleStartTicket second = Reserve();
        Check(first.Generation != second.Generation && first.OperationId == second.OperationId, "new generation for repeated operation");
        NativeBattleStartStore.Confirm(first, "stock-0x408:report:57");
        NativeBattleStartStore.RequirePending(second); Block(delegate { Reserve(); });
        Block(delegate { NativeBattleStartStore.Confirm(first, "different-proof"); });
        NativeBattleStartStore.RequirePending(second);
        NativeBattleStartTicket wrong = new NativeBattleStartTicket(second.RoleId, second.Mode, second.Generation,
            second.OperationId, second.SourceSession, second.Epoch, second.CommandSequence, first.StartHash, second.Directory);
        Block(delegate { NativeBattleStartStore.Confirm(wrong, "forged"); });
        NativeBattleStartStore.RequirePending(second);
        Check(File.Exists(Path.Combine(first.Directory, first.Generation + ".confirmed")), "terminal evidence retained");
        NativeBattleStartStore.Reserve(43, "Trial", Operation); NativeBattleStartStore.Reserve(44, "Campaign", Operation);
        checks += 2;

        Fresh(root); first = Reserve(); File.AppendAllText(Path.Combine(first.Directory, first.Generation + ".start"), "broken");
        Block(delegate { Reserve(); }); Block(delegate { NativeBattleStartStore.RequirePending(first); });
        Fresh(root); first = Reserve(); NativeBattleStartStore.Confirm(first, "verified");
        File.AppendAllText(Path.Combine(first.Directory, first.Generation + ".confirmed"), "broken"); Block(delegate { Reserve(); });
        Fresh(root); first = Reserve(); NativeBattleStartStore.Confirm(first, "verified");
        File.Delete(Path.Combine(first.Directory, first.Generation + ".start")); Block(delegate { Reserve(); });
        Fresh(root); first = Reserve(); NativeBattleStartStore.Confirm(first, "verified");
        File.WriteAllText(Path.Combine(first.Directory, "unknown.tmp"), "ambiguous"); Block(delegate { Reserve(); });
        Fresh(root); first = Reserve(); NativeBattleStartStore.Confirm(first, "verified");
        using (FileStream held = new FileStream(Path.Combine(first.Directory, ".lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Block(delegate { Reserve(); });
        Fresh(root); NativeBattleStartStore.TestFault = "before-write"; Block(delegate { Reserve(); });
        Fresh(root); NativeBattleStartStore.TestFault = "after-flush"; Block(delegate { Reserve(); });
        NativeBattleStartStore.TestFault = ""; Block(delegate { Reserve(); });
        Fresh(root); Block(delegate { NativeBattleStartStore.Reserve(0, "Campaign", Operation); });
        Block(delegate { NativeBattleStartStore.Reserve(43, "Unknown", Operation); });
        Block(delegate { NativeBattleStartStore.Reserve(43, "Campaign", "not-an-operation"); });
        Fresh(root); first = Reserve(); NativeBattleStartStore.TestFault = "after-flush";
        Block(delegate { NativeBattleStartStore.Confirm(first, "verified"); });
        NativeBattleStartStore.TestFault = ""; Block(delegate { Reserve(); });
        Fresh(root);
        string unknown = NativeBattleStartStore.ImportHistoricalUnknown(43, "Campaign", Operation,
            "00000000000000000000000000000052", 1026, new string('a', 64), new string('b', 64));
        Check(!File.ReadAllText(unknown).Contains("generation="), "historical UNKNOWN never fabricates a native generation");
        Check(unknown == NativeBattleStartStore.ImportHistoricalUnknown(43, "Campaign", Operation,
            "00000000000000000000000000000052", 1026, new string('a', 64), new string('b', 64)), "duplicate historical import is idempotent");
        Block(delegate { Reserve(); });
        NativeBattleStartStore.Reserve(44, "Campaign", Operation); NativeBattleStartStore.Reserve(43, "Trial", Operation); checks += 2;
        Fresh(root); first = Reserve(); NativeBattleStartStore.Confirm(first, "verified");
        unknown = NativeBattleStartStore.ImportHistoricalUnknown(43, "Campaign", Operation,
            "00000000000000000000000000000052", 1026, new string('a', 64), new string('b', 64));
        Block(delegate { Reserve(); });
        Block(delegate { NativeBattleStartStore.Confirm(first, "verified"); });
        Fresh(root); first = Reserve();
        unknown = NativeBattleStartStore.ImportHistoricalUnknown(43, "Campaign", Operation,
            "00000000000000000000000000000052", 1026, new string('a', 64), new string('b', 64));
        Block(delegate { NativeBattleStartStore.RequirePending(first); });
        Block(delegate { NativeBattleStartStore.Confirm(first, "verified"); });
        Block(delegate { Reserve(); });
        Check(File.Exists(unknown) && !File.Exists(Path.Combine(first.Directory, first.Generation + ".confirmed")), "native confirmation cannot clear coexisting historical UNKNOWN");
        File.AppendAllText(unknown, "broken"); Block(delegate { Reserve(); });
        Console.WriteLine("PASS checks=" + checks);
    }
    private static NativeBattleStartTicket Reopen(ulong role, string mode, string operation, string generation)
    {
        string directory = Path.Combine(Path.Combine(NativeBattleStartStore.TestRoot, "role-" + role.ToString(CultureInfo.InvariantCulture)), "mode-" + mode);
        string[] lines = File.ReadAllText(Path.Combine(directory, generation + ".start")).Split('\n');
        return new NativeBattleStartTicket(role, mode, generation, operation, lines[5].Substring(8), lines[6].Substring(6),
            Int32.Parse(lines[7].Substring(12), CultureInfo.InvariantCulture), lines[11].Substring(7), directory);
    }
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length < 2) throw new Exception("root and action required");
            string root = Path.GetFullPath(args[0]);
            NativeBattleStartStore.TestRoot = root;
            string action = args[1];
            if (action == "self") { Self(root); return 0; }
            ulong role = args.Length > 2 ? UInt64.Parse(args[2], CultureInfo.InvariantCulture) : 43;
            string mode = args.Length > 3 ? args[3] : "Campaign";
            string operation = args.Length > 4 ? args[4] : Operation;
            if (action == "import-historical")
            {
                Console.WriteLine(NativeBattleStartStore.ImportHistoricalUnknown(role, mode, operation, args[5],
                    UInt16.Parse(args[6], CultureInfo.InvariantCulture), args[7], args[8]));
                return 0;
            }
            if (action == "pause-import")
            {
                NativeBattleStartStore.TestFault = "pause:" + args[9];
                Console.WriteLine(NativeBattleStartStore.ImportHistoricalUnknown(role, mode, operation, args[5],
                    UInt16.Parse(args[6], CultureInfo.InvariantCulture), args[7], args[8]));
                return 0;
            }
            if (action == "validate-historical")
            { NativeBattleStartStore.ValidateHistoricalUnknownFile(args[5], role, mode); Console.WriteLine("OK historical"); return 0; }
            if (action == "confirm" || action == "inspect" || action == "pause-confirm")
            {
                NativeBattleStartTicket ticket = Reopen(role, mode, operation, args[5]);
                if (action == "pause-confirm") NativeBattleStartStore.TestFault = "pause:" + args[6];
                if (action == "confirm" || action == "pause-confirm") NativeBattleStartStore.Confirm(ticket, "stock-0x408:report:57");
                else NativeBattleStartStore.RequirePending(ticket);
                Console.WriteLine("OK " + action); return 0;
            }
            if (action == "write-failure") NativeBattleStartStore.TestFault = "before-write";
            if (action == "publish-failure") NativeBattleStartStore.TestFault = "after-flush";
            if (action == "pause-reserve") NativeBattleStartStore.TestFault = "pause:" + args[5];
            NativeBattleStartTicket reserved = NativeBattleStartStore.Reserve(role, mode, operation);
            Console.WriteLine("RESERVED " + reserved.Generation + " " + reserved.StartHash);
            Console.Out.Flush();
            if (action == "hold") { Console.WriteLine("READY"); Console.Out.Flush(); Console.ReadLine(); return 0; }
            if (action != "import-unknown") Console.WriteLine("SEND");
            return 0;
        }
        catch (ProbeFailure failure) { Console.WriteLine("BLOCKED " + failure.Message); return 2; }
        catch (Exception failure) { Console.WriteLine("FAILED " + failure.GetType().Name + " " + failure.Message); return 3; }
    }
}
