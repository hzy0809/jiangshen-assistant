using System;
using System.IO;
using JiangShen.AssistantRuntime;

internal static class DailyWoodDurableRunner
{ public static int Main() { return DailyWoodTests.Run(new string[] { "durable" }); } }

internal static partial class DailyWoodTests
{
    private static string StartRecord()
    {
        string[] files = Directory.GetFiles(NativeBattleStartStore.TestRoot, "*.start", SearchOption.AllDirectories);
        Check(files.Length == 1, "one durable native start before stock 616");
        return files[0];
    }
    private static void DurableBlocked()
    {
        try { NativeBattleStartStore.Reserve(14, "WoodFast", Id); }
        catch (ProbeFailure) { assertions++; return; }
        throw new Exception("unresolved wood start was replayable");
    }
    private static void DurablePaths()
    {
        foreach (bool pushFirst in new bool[] { false, true })
        {
            Fixture f = new Fixture(); f.Start(); string start = StartRecord();
            DurableBlocked();
            if (pushFirst) f.Activity(); else f.FastOk();
            Check(!f.Model.TaskConfirmed && !File.Exists(Path.ChangeExtension(start, ".confirmed")), "616 and exact 355 both required for count zero");
            DurableBlocked();
            if (pushFirst) f.FastOk(); else f.Activity();
            Check(f.Model.TaskConfirmed && f.Model.Status == "RESULT" && File.Exists(Path.ChangeExtension(start, ".confirmed")), "both callback orderings confirm only exact owned result");
            NativeBattleStartTicket next = NativeBattleStartStore.Reserve(14, "WoodFast", Id);
            Check(next.Generation != Path.GetFileNameWithoutExtension(start), "new same-operation wood round gets new generation");
        }
        {
            Fixture f = new Fixture(); f.Current.Count = f.Current.Listed = 1; f.Current.Refresh = f.Current.ListedRefresh = 17;
            f.Observe(); f.Start(); string start = StartRecord(); f.FastOk();
            Check(f.Model.TaskConfirmed && File.Exists(Path.ChangeExtension(start, ".confirmed")), "daily count capped at one confirms from exact 616 quota decrement");
        }
        foreach (string kind in new string[] { "stop", "timeout", "missing-355", "wrong-355", "rejected" })
        {
            Fixture f = new Fixture(); f.Start(); string start = StartRecord();
            if (kind == "stop") { f.Model.Stop(); f.FastOk(); }
            else if (kind == "timeout") { f.Dispatch(Fast, null, f.Current.Copy()); f.Observe(); }
            else if (kind == "rejected") { f.Dispatch(Fast, FastPacket(2), f.Current.Copy()); f.Observe(); }
            else { f.FastOk(); if (kind == "wrong-355") f.Activity(true); }
            Check(!File.Exists(Path.ChangeExtension(start, ".confirmed")), "unknown/rejected/partial wood receipt cannot confirm: " + kind);
            DurableBlocked();
        }
        foreach (string fault in new string[] { "before-write", "after-flush" })
        {
            Fixture f = new Fixture(); f.Open(); f.FreshInfo();
            NativeBattleStartStore.TestFault = fault;
            Check(f.Queue("fast") && !f.Execute("fast") && f.FastCalls == 0 && f.Model.Status == "UNKNOWN", "reservation failure stops before stock and reports UNKNOWN");
            NativeBattleStartStore.TestFault = "";
            if (fault == "after-flush") DurableBlocked();

            f = new Fixture(); f.Start(); string start = StartRecord();
            NativeBattleStartStore.TestFault = fault;
            f.FastOk(); f.Activity();
            NativeBattleStartStore.TestFault = "";
            Check(f.Model.Status == "UNKNOWN" && !f.Model.TaskConfirmed && !File.Exists(Path.ChangeExtension(start, ".confirmed")), "confirmation fault is sticky UNKNOWN");
            Check(!f.Queue("close_result") && f.CloseResultCalls == 0 && f.FastCalls == 1, "durable fault cannot advance GUI or resend");
            DurableBlocked();
        }
    }
}
