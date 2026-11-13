using System;
using System.Reflection;
using JiangShen.AssistantRuntime;

// Exercises the production adapter with deliberately controlled stand-ins. These
// stand-ins do not prove the real battle models' own stop or cleanup behavior.
namespace UnityEngine { public class Component { } }
namespace JiangShen.AssistantRuntime
{
    internal sealed class Model { }
    internal sealed class SharedDevelopmentContext { }
    internal sealed class BattlePresentationOwnership { }
    internal sealed class DevelopmentPublication { }
    internal static class DevelopmentContextReader
    { internal static SharedDevelopmentContext DailyWood(Model model, bool ignored) { return new SharedDevelopmentContext(); } }
    internal sealed class Command
    {
        internal int DailyScriptTaskId, DailyScriptCharacterId, Sequence, ExpectedRevision;
        internal string OperationId, DailyScriptAction;
    }
    internal class FakeDaily
    {
        internal bool Busy, Stopped;
        internal int Captures, LastTask, ExecuteCount;
        internal string Queued;
        internal void Capture(SharedDevelopmentContext context) { Captures++; }
        internal void Capture(Model model, int task, bool ignored) { Captures++; LastTask = task; }
        internal BattlePresentationOwnership OwnedBattlePresentation(UnityEngine.Component battle) { return null; }
        internal bool Queue(int task, int character, string id, int sequence, int revision, string action, out string why)
        {
            why = "fake";
            if (Stopped || Busy) return false;
            Busy = true; Queued = action; return true;
        }
        internal bool ExecuteGui(Func<SharedDevelopmentContext> context, DevelopmentPublication publish,
            Func<Func<bool>, Func<bool>, bool> admission, out string why) { return Execute(out why); }
        internal bool ExecuteGui(DevelopmentPublication publish, Func<bool> admission, out string why) { return Execute(out why); }
        private bool Execute(out string why)
        {
            why = "fake";
            if (Queued == null || Stopped) return false;
            ExecuteCount++; Queued = null; Busy = false; return true;
        }
        internal void Stop() { Stopped = true; Busy = false; Queued = null; }
        internal void Stop(string reason) { Stop(); }
        internal void NotInvoked(string reason) { }
        internal string Json() { return "fake"; }
    }
    internal sealed class DailyArenaBattleModel : FakeDaily { }
    internal sealed class DailyPointRaceModel : FakeDaily { }
    internal sealed class DailyFlowerModel : FakeDaily { }
    internal class DailyOrdinaryBattleModel : FakeDaily { }
    internal sealed class DailyTrialModel : DailyOrdinaryBattleModel { }
    internal sealed class DailyTrainingMonsterModel : DailyOrdinaryBattleModel { }
    internal sealed class DailyGraveModel : FakeDaily { }
    internal sealed class DailyMonsterModel : FakeDaily
    {
        internal UnityEngine.Component OwnedBattleReportSource(UnityEngine.Component battle) { return null; }
        internal bool TryPresentSettledBattle(UnityEngine.Component battle) { return false; }
    }
    internal sealed class DailyDivinityBattleModel : FakeDaily { }
    internal sealed class DailyEventRewardsModel : FakeDaily { }
    internal sealed class DailySignSalaryModel : FakeDaily
    {
        internal bool PopupHandoffReady { get { return false; } }
        internal bool OwnsRewardPopup(UnityEngine.Component popup, Delegate close) { return false; }
    }
    internal sealed class DailyTechBookCopyModel : FakeDaily { }
}
internal static class DailyScriptOwnershipTests
{
    private static int checks;
    private static void Check(bool condition, string message)
    { checks++; if (!condition) throw new Exception(message); }
    private static object Field(DailyScriptAdapter adapter, string name)
    { return typeof(DailyScriptAdapter).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(adapter); }
    private static Command Action(int task, string action)
    { return new Command { DailyScriptTaskId = task, DailyScriptCharacterId = 1, ExpectedRevision = 1,
        DailyScriptAction = action, OperationId = "op", Sequence = 1 }; }
    private static void RetainsBusy(int task, string field, int requested)
    {
        var adapter = new DailyScriptAdapter(); var model = new Model(); string why;
        adapter.Capture(model, task);
        var owner = (FakeDaily)Field(adapter, field);
        Check(owner != null, "owner created for " + task);
        Check(adapter.Queue(Action(task, "battle"), out why), "queue for " + task);
        int captures = owner.Captures;
        adapter.Capture(model, requested);
        Check(Object.ReferenceEquals(owner, Field(adapter, field)), "owner retained for " + task + " -> " + requested);
        Check((int)Field(adapter, "task") == task, "task retained for " + task);
        Check(owner.Captures == captures + 1, "capture routed to owner for " + task);
        Check(!adapter.Queue(Action(requested, "new_battle"), out why), "new task rejected for " + task);
        Check(owner.Queued == "battle", "queued action preserved for " + task);
        Check(adapter.Execute(model, null, out why), "original queued action executable for " + task);
        Check(owner.ExecuteCount == 1, "only original action executed for " + task);
        adapter.Capture(model, requested);
        Check((int)Field(adapter, "task") == requested, "idle owner releases task " + task);
    }
    private static void StopCancelsFakeQueue(int task, string field)
    {
        var adapter = new DailyScriptAdapter(); var model = new Model(); string why;
        adapter.Capture(model, task); var owner = (FakeDaily)Field(adapter, field);
        Check(adapter.Queue(Action(task, "battle"), out why), "stop setup " + task);
        adapter.Capture(model, 999);
        adapter.Stop("test_stop");
        Check(owner.Stopped && owner.Queued == null, "stop forwarded to retained owner " + task);
        Check(!adapter.Execute(model, null, out why) && owner.ExecuteCount == 0, "fake queued battle not executed after stop " + task);
    }
    private static void RetainsHeldOwner(int task, string field)
    {
        var adapter = new DailyScriptAdapter(); var model = new Model(); string why;
        adapter.Capture(model, task); var owner = (FakeDaily)Field(adapter, field);
        // Simulate an active held session with no queued command, such as an
        // uncertain identity observation; the adapter must retain its owner.
        owner.Busy = true;
        adapter.Capture(model, 999);
        Check(Object.ReferenceEquals(owner, Field(adapter, field)), "held owner retained " + task);
        Check((int)Field(adapter, "task") == task, "held identity retained " + task);
        Check(!adapter.Queue(Action(999, "new_battle"), out why), "held owner rejects other task " + task);
    }
    public static int Main()
    {
        try
        {
            int[] ids = { 14, 23, 17, 8 }; string[] fields = { "trial", "trial", "grave", "divinity" };
            for (int i = 0; i < ids.Length; i++)
            {
                RetainsBusy(ids[i], fields[i], 9);
                RetainsBusy(ids[i], fields[i], 999); // unknown observed task
                RetainsHeldOwner(ids[i], fields[i]);
                StopCancelsFakeQueue(ids[i], fields[i]);
            }
            Console.WriteLine("PASS: " + checks + " adapter ownership checks"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
