using System;
using System.IO;
using System.Reflection;
using UnityEngine;
using JiangShen.AssistantRuntime;

internal static partial class PointRaceBattleAdapterTests
{
    private static void SetPoint(DailyPointRaceModel model, string name, object value)
    { typeof(DailyPointRaceModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(model, value); }
    private static Fixture PendingEntry()
    {
        Fixture f = new Fixture(); f.EntryFromHome = true;
        BaseAsset.lds.currmg = f.Home; f.GlobalUi.level_rmg = f.Home; f.Race.enabled = false;
        f.Capture(); f.Action("open"); f.Capture(); f.Action("enter"); f.Capture();
        Check(f.Client.Sent.Count == 1 && f.Client.Sent[0] == 0x464, "one stock entry request only");
        return f;
    }
    private static void ExpireEntry(Fixture f)
    { SetPoint(f.Model, "started", unchecked(Environment.TickCount - 180001)); f.Capture(); }
    private static bool Has(Fixture f, string field, string value)
    { return f.Model.Json().Contains("\"" + field + "\":" + value); }
    private static void UnknownEntry(Fixture f)
    {
        Check(Has(f, "status", Q("UNKNOWN")) && Has(f, "reason", Q("pointrace_entry_callback_timeout")), "entry timeout remains the first failure");
        Check(Has(f, "baseline_ready", "false") && Has(f, "challenges_submitted", "0") &&
            Has(f, "challenges_completed", "0") && Has(f, "mutation_attempted", "false"), "cleanup supplies no business evidence");
        Check(!f.CanQueue("enter") && !f.CanQueue("challenge") && !f.CanQueue("refresh_free"), "unknown entry cannot be resubmitted or mutated");
    }
    private static void EntryCallbackDistinctWait()
    {
        Fixture f = PendingEntry();
        Check(Has(f, "in_scene", "true") && Has(f, "reason", Q("pointrace_waiting_entry_callback")), "visible scene does not imply callback");
        SetPoint(f.Model, "started", Environment.TickCount - 179000); f.Capture();
        Check(Has(f, "next_action", Q("")) && !f.CanQueue("return") && !f.CanQueue("enter"), "no cleanup or retry before existing timeout bound");
        Global.character_.next_resource_time = TimeUtil._thisDay + 86400;
        TimeUtil._now = TimeUtil._thisDay + 75600; f.Capture();
        Check(Has(f, "reason", Q("pointrace_waiting_entry_callback")) && !f.CanQueue("return"),
            "activity closing cannot turn a missing callback into ordinary finish cleanup");
        TimeUtil._now = 106400;
        f.Client.Dispatch(0x464, new byte[] { 8, 0 });
        BaseAsset.lds.currmg = f.Home; f.GlobalUi.level_rmg = f.Home; f.Capture();
        Check(Has(f, "reason", Q("pointrace_waiting_scene")), "returned callback with absent scene has a different wait");
    }
    private static void EntryTimeoutSafeReturn()
    {
        Fixture f = PendingEntry(); ExpireEntry(f); UnknownEntry(f);
        Check(Has(f, "next_action", Q("return")), "verified owned scene offers stock return after timeout");
        f.Action("return"); f.Capture(); f.Capture(); UnknownEntry(f);
        Check(f.Race.NavigationReturns == 1 && f.Client.Sent.Count == 1 &&
            Has(f, "closed", "true") && Has(f, "home_ready", "true") && Has(f, "next_action", Q("")), "verified home is cleanup only, never reopen");
        Check(!f.CanQueue("open"), "verified UNKNOWN cleanup cannot reset the operation");
        f.Model.Stop(); f.Model.Stop("component_destroyed"); UnknownEntry(f);
    }
    private static void EntryTimeoutSafetyNegatives()
    {
        Action<Fixture>[] changes = {
            delegate(Fixture f) { f.Race.OnClose = delegate { throw new Exception("foreign OnClose"); }; },
            delegate(Fixture f) { BaseAsset.lds.oldmainrmg = new RootMessenger { LevelID = "Capital" }; },
            delegate(Fixture f) { f.GlobalUi.level_rmg = f.Home; },
            delegate(Fixture f) { f.Race.interaction = false; },
            delegate(Fixture f) { BaseAsset.lds.enabled = true; },
            delegate(Fixture f) { BaseAsset.lds.isBattling = true; },
            delegate(Fixture f) { f.Battle.enabled = true; },
            delegate(Fixture f) { f.Result.enabled = true; },
            delegate(Fixture f) { SetPoint(f.Model, "challengesSubmitted", 1); },
            delegate(Fixture f) { SetPoint(f.Model, "refreshAttempted", true); },
            delegate(Fixture f) { f.Race.m_pointrace = new PointRaceData(); },
            delegate(Fixture f) { f.Race.rmg = new RootMessenger { LevelID = "PointRace" }; },
            delegate(Fixture f) { Global.character_ = new Database.Character(); }
        };
        foreach (Action<Fixture> change in changes)
        {
            Fixture f = PendingEntry(); ExpireEntry(f); change(f); f.Capture();
            Check(Has(f, "status", Q("UNKNOWN")) && Has(f, "reason", Q("pointrace_entry_callback_timeout")), "changed state preserves first failure");
            Check(!f.CanQueue("return") && f.Race.NavigationReturns == 0 && f.Client.Sent.Count == 1, "unsafe cleanup cannot navigate or send");
        }
    }
    private static void EntryTimeoutReturnRechecked()
    {
        Fixture f = PendingEntry(); ExpireEntry(f);
        string why;
        Check(f.Model.Queue(109, 78, Operation, 100, f.Revision(), "return", out why), "queue safe return");
        f.Race.OnClose = delegate { throw new Exception("must not call"); };
        Check(!f.Model.ExecuteGui(f.Context, delegate { }, delegate(DailyPointRaceInvocationCheck sample, DailyPointRaceInvocationCheck check)
            { return sample() && check(); }, out why), "queued return rechecks changed OnClose");
        UnknownEntry(f); Check(f.Race.NavigationReturns == 0, "changed offer never calls stock");
        f = PendingEntry(); ExpireEntry(f);
        Check(f.Model.Queue(109, 78, Operation, 100, f.Revision(), "return", out why), "queue return before final gate race");
        Check(!f.Model.ExecuteGui(f.Context, delegate { }, delegate(DailyPointRaceInvocationCheck sample, DailyPointRaceInvocationCheck check)
        {
            if (!sample()) return false;
            f.Race.OnClose = delegate { throw new Exception("must not call"); };
            return check();
        }, out why), "final invocation gate re-reads target and OnClose after acceptance");
        UnknownEntry(f); Check(f.Race.NavigationReturns == 0, "last-moment OnClose mutation never calls stock");
    }
    private static void EntryTimeoutStopPause()
    {
        foreach (bool stop in new bool[] { true, false })
        {
            Fixture f = PendingEntry(); ExpireEntry(f); string why;
            Check(f.Model.Queue(109, 78, Operation, 100, f.Revision(), "return", out why), "queue before stop/pause");
            if (stop) f.Model.Stop(); else f.Paused = true;
            Check(!f.Model.ExecuteGui(f.Context, delegate { }, delegate(DailyPointRaceInvocationCheck sample, DailyPointRaceInvocationCheck check)
                { return sample() && check(); }, out why), "stop/pause wins over queued navigation");
            UnknownEntry(f); Check(f.Race.NavigationReturns == 0 && f.Client.Sent.Count == 1, "stop/pause does no UI work");
        }
    }
    private static void EntryTimeoutLateCallback()
    {
        Fixture f = PendingEntry();
        game.net.tcp_base.Handler old = (game.net.tcp_base.Handler)f.Client.m_handlers[(ushort)0x464];
        DevelopmentNormalCallback retained = old.handler;
        ExpireEntry(f); f.Client.Dispatch(0x464, new byte[] { 8, 0 });
        retained(new MemoryStream(new byte[] { 8, 0 }, false), null); f.Capture(); UnknownEntry(f);
        Check(f.Race.EntryCalls == 2 && Has(f, "entry_callback_returned", "false") && f.Client.Sent.Count == 1,
            "late native cache callbacks forward without assistant attribution or retry");
        f.Action("return"); f.Capture();
        f.Race.PrepareLoading();
        object replacement = f.Client.m_handlers[(ushort)0x464];
        f.Client.Dispatch(0x464, new byte[] { 8, 0 }); f.Capture();
        Check(!ReferenceEquals(old, replacement) && f.Race.EntryCalls == 3 && Has(f, "next_action", Q("")),
            "fresh manual stock receiver is not swallowed or overwritten by cleanup");
    }
    private static void EntryTimeoutIncompleteReturn()
    {
        Fixture f = PendingEntry(); ExpireEntry(f); f.Race.LeaveReturnIncomplete = true;
        f.Action("return"); f.Capture(); f.Capture(); UnknownEntry(f);
        Check(Has(f, "closed", "false") && Has(f, "cleanup_attempted", "true") && Has(f, "next_action", Q("")) &&
            !f.CanQueue("return") && f.Race.NavigationReturns == 1, "invocation alone never proves cleanup and is never repeated");
        f = PendingEntry(); ExpireEntry(f); f.Action("return");
        f.GlobalUi.level_rmg = f.Scene; f.Capture();
        Check(Has(f, "closed", "false") && !f.CanQueue("return"), "home loader alone cannot prove global UI returned home");
    }
    private static void EntryCallbackNormalPath()
    {
        Fixture f = PendingEntry(); f.Client.Dispatch(0x464, new byte[] { 8, 0 }); f.Capture();
        Check(Has(f, "baseline_ready", "true") && Has(f, "baseline_source", Q("entry_callback")) &&
            Has(f, "entry_callback_returned", "true") && Has(f, "next_action", Q("challenge")), "normal entry callback still creates the one owned baseline");
        Check(f.Client.Sent.Count == 1 && !f.CanQueue("enter"), "normal callback cannot repeat entry");
    }
}
