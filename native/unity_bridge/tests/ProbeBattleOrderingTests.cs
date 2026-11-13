using System;
using System.IO;
using System.Reflection;
using System.Threading;
using JiangShen.AssistantRuntime;
using UnityEngine;

// Runs production Probe.Update/OnGUI against synthetic collaborators. It does
// not exercise the production BattleSettlement or real adapter identity checks.
internal static class ProbeBattleOrderingTests
{
    private static int checks;
    private static Probe probe;
    private static string sessionDirectory;
    private const string Nonce = "0123456789abcdef0123456789abcdef";
    private static void Check(bool condition, string name)
    { if (!condition) throw new Exception(name); checks++; }
    private static void Set(string name, object value)
    { typeof(Probe).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(probe, value); }
    private static object Get(string name)
    { return typeof(Probe).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(probe); }
    private static void Call(string name)
    { typeof(Probe).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(probe, null); }
    private static void SetUp(string directory)
    {
        Directory.CreateDirectory(directory); sessionDirectory = directory;
        probe = new Probe();
        probe.Initialize(new ReceiptStore(directory), directory, Nonce, Entry.GetCurrentThreadId(), Thread.CurrentThread.ManagedThreadId, 1, IntPtr.Zero);
        Time.frameCount = 1; Call("Update");
        Time.frameCount = 2; Call("Update");
        Check((bool)Get("ready"), "probe_ready");
        Set("dailyScript", new DailyScriptAdapter());
        Model.BattleOwner = new GameObject(); Model.BattleOwner.Panel = new BattleUI();
        BattleSettlement.NativePending = true;
    }
    private static void Queue(int sequence)
    {
        Set("guiCommand", new Command { Name = "campaign-command", Sequence = sequence });
        Set("guiQueuedTick", Environment.TickCount);
        Set("guiSequence", sequence); Set("guiPhase", "queued");
    }
    private static void UpdateBeforeCapture()
    {
        Time.frameCount = 3; Call("Update");
        Check(DailyScriptAdapter.Captures == 0, "pending_blocks_daily_capture");
        Check(BattleSettlement.Observes == 1, "observe_native_report_before_capture");
        Check(((BattleSettlement)Get("battleSettlement")).BlocksGuiInvocation, "native_request_owned");
    }
    private static void GuiBlocksAndResumes()
    {
        Queue(1); Call("OnGUI");
        Check(CampaignModel.GuiCalls == 0, "pending_command_not_invoked");
        Check(BattleSettlement.Observes == 1, "gui_observes_before_command");
        Check((string)Get("guiPhase") == "not_invoked" && Get("guiCommand") == null, "pending_command_cancelled");
        Call("OnGUI");
        Check(CampaignModel.GuiCalls == 0, "duplicate_gui_does_not_invoke_cancelled_command");
        ((BattleSettlement)Get("battleSettlement")).Confirm();
        Queue(2); Call("OnGUI"); Call("OnGUI");
        Check(CampaignModel.GuiCalls == 1, "confirmed_fresh_command_invoked_once");
    }
    private static void UnknownBlocks()
    {
        Time.frameCount = 3; Call("Update");
        BattleSettlement.Invalid = true;
        Queue(1); Call("OnGUI"); Call("OnGUI");
        Check(CampaignModel.GuiCalls == 0, "unknown_does_not_invoke");
        Check(((SessionLifecycle)Get("life")).StopRequested, "unknown_stops_session");
    }
    private static void FirstPresentationOwnership()
    {
        Call("OnGUI");
        Check(DailyScriptAdapter.Ownerships >= 2, "adapter_ownership_requested_for_observation_and_presentation");
        Check(BattleSettlement.Observes == 1, "first_report_observed");
        Check(BattleSettlement.Advances == 1 && BattleSettlement.UnownedAdvances == 0, "first_presentation_uses_adapter_ownership");
    }
    private static void WriteCommand(string name, string fields)
    {
        string wire = "version=3\nnonce=" + Nonce + "\nsequence=1\ncommand=" + name + "\n" + fields;
        Check(Command.Parse(wire, Nonce, 0) != null, "test_command_parses");
        File.WriteAllText(Path.Combine(sessionDirectory, "command.txt"), wire);
    }
    private static string Ack() { return File.ReadAllText(Path.Combine(sessionDirectory, "ack.json")); }
    private static void DirectPoll(string action, bool unknown, bool confirmed)
    {
        Time.frameCount = 3; Call("Update");
        Check(((BattleSettlement)Get("battleSettlement")).BlocksGuiInvocation, "settlement_blocked_before_poll");
        if (unknown) BattleSettlement.Invalid = true;
        if (confirmed) ((BattleSettlement)Get("battleSettlement")).Confirm();
        string attempt = action == "start" ? Nonce : "";
        WriteCommand("campaign-command", "action=" + action + "\nexpected_revision=1\ncampaign_id=12\ntroop_id=7\nattempt_id=" + attempt + "\n");
        if (unknown) Call("Poll"); // Direct boundary check with a simulated UNKNOWN state.
        else { Time.frameCount = 4; Call("Update"); }
        if (confirmed)
        {
            Check(CampaignModel.DirectCalls == 1, "confirmed_direct_command_invoked");
            Check(Ack().Contains("\"status\":\"COMPLETED\""), "confirmed_direct_command_completed");
        }
        else
        {
            Check(CampaignModel.DirectCalls == 0, "pending_direct_command_not_invoked");
            Check(Ack().Contains("\"status\":\"REJECTED\"") && Ack().Contains("battle_settlement_pending_not_invoked"), "pending_direct_command_rejected");
        }
    }
    private static void PollObserve()
    {
        Time.frameCount = 3; Call("Update");
        WriteCommand("daily-script", "action=observe\noperation_id=" + Nonce + "\ncharacter_id=1\ntask_id=1\nexpected_revision=1\n");
        Time.frameCount = 4; Call("Update");
        Check(DailyScriptAdapter.Captures == 0, "pending_observe_does_not_recapture");
        Check(Ack().Contains("\"status\":\"COMPLETED\"") && Ack().Contains("\"fixture_revision\":42"), "pending_observe_returns_current_state");
    }
    private static void PollDisable()
    {
        Time.frameCount = 3; Call("Update");
        WriteCommand("disable-restore", "");
        Time.frameCount = 4; Call("Update");
        Check(Model.DisableCalls == 0, "pending_disable_not_invoked");
        Check(Ack().Contains("\"status\":\"REJECTED\"") && Ack().Contains("battle_settlement_pending_not_invoked"), "pending_disable_rejected");
    }
    private static void UnknownUpdate()
    {
        Time.frameCount = 3; Call("Update");
        BattleSettlement.Invalid = true;
        WriteCommand("campaign-command", "action=ack_result\nexpected_revision=1\ncampaign_id=12\ntroop_id=7\nattempt_id=\n");
        Time.frameCount = 4; Call("Update");
        Check(CampaignModel.DirectCalls == 0, "unknown_update_no_direct_invocation");
        Check(((SessionLifecycle)Get("life")).StopRequested, "unknown_update_stops_before_poll");
    }
    public static int Main(string[] args)
    {
        try
        {
            SetUp(Path.Combine(args[0], Guid.NewGuid().ToString("N")));
            if (args[1] == "update") UpdateBeforeCapture();
            else if (args[1] == "gui") GuiBlocksAndResumes();
            else if (args[1] == "unknown") UnknownBlocks();
            else if (args[1] == "presentation") FirstPresentationOwnership();
            else if (args[1] == "poll-start") DirectPoll("start", false, false);
            else if (args[1] == "poll-ack") DirectPoll("ack_result", false, false);
            else if (args[1] == "poll-start-unknown") DirectPoll("start", true, false);
            else if (args[1] == "poll-ack-unknown") DirectPoll("ack_result", true, false);
            else if (args[1] == "poll-confirmed") DirectPoll("start", false, true);
            else if (args[1] == "poll-observe") PollObserve();
            else if (args[1] == "poll-disable") PollDisable();
            else if (args[1] == "unknown-update") UnknownUpdate();
            else throw new Exception("unknown_scenario");
            Console.WriteLine("PASS " + args[1] + " checks=" + checks); return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL " + args[1] + " checks=" + checks + " " + ex); return 1; }
    }
}
