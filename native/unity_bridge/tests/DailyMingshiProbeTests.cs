using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using JiangShen.AssistantRuntime;
using UnityEngine;

internal static class DailyMingshiProbeTests
{
    private static readonly string Prefix = Guid.NewGuid().ToString("N").Substring(0, 31);
    private static readonly string Nonce = Prefix + "1", OtherNonce = Prefix + "2";
    private static readonly string Operation = Prefix + "3", OtherOperation = Prefix + "4";
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int sequence, checks;

    private static string Wire(string nonce, int seq, string name)
    { return "version=3\nnonce=" + nonce + "\nsequence=" + seq + "\ncommand=" + name + "\n"; }
    private static string ActionWire(int seq, string action, int revision, string operation, string role = "123")
    {
        return Wire(Nonce, seq, "daily-mingshi-command") + "action=" + action + "\noperation_id=" + operation +
            "\ncharacter_id=" + role + "\ntask_id=7\nexpected_revision=" + revision + "\n";
    }
    private static string PathFor(string file, string nonce)
    { return Path.Combine(Path.Combine(Entry.SessionsRoot, nonce), file); }
    private static string Read(string file) { return File.ReadAllText(PathFor(file, Nonce)); }
    private static void Publish(string wire) { File.WriteAllText(PathFor("command.txt", Nonce), wire); }
    private static string Bootstrap(string nonce)
    {
        string directory = Path.Combine(Entry.SessionsRoot, nonce);
        Directory.CreateDirectory(directory);
        return Entry.Bootstrap(directory, nonce, Entry.GetCurrentThreadId());
    }
    private static Probe Current { get { return GameObject.LastHelper.Probe; } }
    private static object Field(object owner, string field)
    { return owner.GetType().GetField(field, PrivateInstance).GetValue(owner); }
    private static void Set(object owner, string field, object value)
    { owner.GetType().GetField(field, PrivateInstance).SetValue(owner, value); }
    private static void Invoke(string method) { typeof(Probe).GetMethod(method, PrivateInstance).Invoke(Current, null); }
    private static void Update() { Time.frameCount++; Invoke("Update"); }
    private static void Send(string name) { Publish(Wire(Nonce, ++sequence, name)); Update(); }
    private static void Check(bool value, string description)
    {
        if (!value) throw new Exception(description + "\n" + Read("state.json") + "\n" +
            (File.Exists(PathFor("ack.json", Nonce)) ? Read("ack.json") : ""));
        checks++;
    }
    private static bool Ack(string status) { return Read("ack.json").Contains("\"status\":\"" + status + "\""); }
    private static bool State(string value) { return Read("state.json").Contains(value); }
    private static int Revision()
    {
        string state = Read("state.json");
        const string prefix = "\"daily_mingshi\":{\"schema\":1,\"revision\":";
        int start = state.IndexOf(prefix, StringComparison.Ordinal);
        Check(start >= 0, "mingshi_revision_shape");
        start += prefix.Length;
        return Int32.Parse(state.Substring(start, state.IndexOf(',', start) - start), CultureInfo.InvariantCulture);
    }
    private static string Queue(string action)
    {
        string wire = ActionWire(++sequence, action, Revision(), Operation);
        Publish(wire); Update();
        Check(Ack("ACCEPTED") && State("\"phase\":\"queued\"") && State("\"pending_daily_mingshi\":true"), action + "_queued");
        return wire;
    }
    private static void RunGui(string action)
    {
        Queue(action); Invoke("OnGUI"); Invoke("OnGUI");
        Check(Ack("COMPLETED") && State("\"phase\":\"completed\",\"invocation_attempted\":true") &&
            Read("ack.json").Contains("\"daily_mingshi_enabled\":true,\"daily_mingshi\":{\"schema\":1"), action + "_gui_completion");
    }
    private static void ReadAndCheckGrammar()
    {
        string wire = ActionWire(1, "harvest", 5, Operation, "18446744073709551615");
        Command parsed = Command.Parse(wire, Nonce, 0);
        Check(parsed != null && parsed.DailyMingshiTaskId == 7 && parsed.DailyMingshiCharacterId == UInt64.MaxValue &&
            parsed.ExpectedRevision == 5 && parsed.DailyMingshiAction == "harvest" && parsed.OperationId == Operation, "fixed_mingshi_wire");
        foreach (string invalid in new[] { wire.Replace("action=harvest", "action=dig"), wire.Replace("task_id=7", "task_id=8"),
            wire.Replace("task_id=7", "task_id=07"), wire.Replace("expected_revision=5", "expected_revision=0"),
            wire.Replace("18446744073709551615", "18446744073709551616"),
            wire.Replace("character_id=18446744073709551615", "character_id=0"),
            wire.Replace("operation_id=" + Operation, "operation_id=gg000000000000000000000000000000"),
            wire.Replace("action=harvest\noperation_id=" + Operation, "operation_id=" + Operation + "\naction=harvest"),
            wire.Replace("daily-mingshi-command", "daily-mingshi-observe"), wire + "max_gold=0\n" })
            Check(Command.Parse(invalid, Nonce, 0) == null, "strict_mingshi_wire");
        Check(Command.Parse(wire, OtherNonce, 0) == null, "wrong_session_nonce_rejected");
    }
    private static void ReadinessAndIdentity(DailyMingshiTests.Fixture f)
    {
        Publish(ActionWire(++sequence, "open", 1, Operation)); Update();
        Check(Ack("REJECTED") && f.OpenCalls == 0, "feature_requires_own_observation");
        Send("daily-mingshi-observe");
        Check(Ack("COMPLETED") && State("\"daily_mingshi_enabled\":true,\"daily_mingshi\":{\"schema\":1") &&
            State("\"next_action\":\"open\""), "enabled_observe_fresh_role_and_cycle");
        int rev = Revision();
        Publish(ActionWire(++sequence, "open", rev, Operation, "124")); Update();
        Check(Ack("REJECTED") && f.OpenCalls == 0, "wrong_character_rejected");
        Publish(ActionWire(++sequence, "open", rev + 1, Operation)); Update();
        Check(Ack("REJECTED") && f.OpenCalls == 0, "wrong_revision_rejected");
    }
    private static void Open(DailyMingshiTests.Fixture f)
    {
        RunGui("open");
        Check(f.OpenCalls == 1 && State("\"status\":\"OPENING\"") && State("\"pending_daily_mingshi\":true"), "stock_open_only_once");
        f.Ready(); Update();
        Check(State("\"next_action\":\"harvest\"") && State("\"gold_cost\":0") && f.Panel.Calls == 0, "stock_owned_ready_free_quote");
        Publish(ActionWire(++sequence, "harvest", Revision(), OtherOperation)); Update();
        Check(Ack("REJECTED") && f.Panel.Calls == 0, "foreign_operation_cannot_harvest");
    }
    private static void ExcludeOtherBusiness(DailyMingshiTests.Fixture f, int calls)
    {
        foreach (string command in new[] { "daily-state-refresh", "player-state-refresh", "development-command",
            "campaign-command", "challenge-command", "daily-guild-command", "daily-reward-command",
            "daily-timed-command", "daily-stone-command", "disable-restore" })
        {
            string suffix = "";
            if (command == "daily-state-refresh" || command == "player-state-refresh" || command == "development-command")
            {
                string action = command == "development-command" ? "star_open" : command.Replace('-', '_');
                suffix = "action=" + action + "\nexpected_revision=1\noperation_id=" + Operation +
                    "\ntarget_id=0\ndesired_level=0\nmax_requests=" + (command == "development-command" ? "1" : "0") +
                    "\nmax_money=0\nmax_stone=0\nmax_food=0\nmode=\n" + (command == "development-command" ? "max_practice=0\n" : "");
            }
            else if (command == "campaign-command" || command == "challenge-command")
                suffix = "action=continue\nexpected_revision=1\ncampaign_id=42\ntroop_id=1\nattempt_id=\n";
            else if (command == "daily-guild-command")
                suffix = "action=open\nexpected_revision=1\ncharacter_id=123\nguild_id=4\ntask_id=18\noperation_id=" + Operation +
                    "\ndonation_type=0\nmax_premium=0\n";
            else if (command == "daily-reward-command")
                suffix = "action=open\nexpected_revision=1\ncharacter_id=123\ncategory_id=2\noperation_id=" + Operation + "\n";
            else if (command == "daily-timed-command" || command == "daily-stone-command")
                suffix = "action=open\noperation_id=" + Operation + "\ncharacter_id=123\ntask_id=" +
                    (command == "daily-timed-command" ? "12" : "16") + "\nexpected_revision=1\n";
            string wire = Wire(Nonce, ++sequence, command) + suffix;
            Check(Command.Parse(wire, Nonce, 0) != null, "fixture_parses_" + command);
            Publish(wire); Update(); Invoke("OnGUI");
            Check(Ack("REJECTED") && Read("ack.json").Contains("\"reason\":\"daily_mingshi_operation_owned\"") &&
                f.Panel.Calls == calls && f.Panel.Returns == 0, "owned_ui_excludes_" + command);
        }
        Send("heartbeat");
        Check(Ack("COMPLETED"), "heartbeat_available_while_owned");
    }
    private static void Harvest(DailyMingshiTests.Fixture f)
    {
        RunGui("harvest");
        Check(f.Panel.Calls == 1 && f.Panel.PremiumModals == 0 && f.Panel.Request.Length == 2 &&
            f.Panel.Request[0] == 8 && f.Panel.Request[1] == 0 && f.Role.gold == 30 && f.Role.bind_gold == 7 &&
            State("\"status\":\"PENDING\"") && State("\"account_progress_observed\":false"), "gui_receipt_is_only_one_free_stock_send");
    }
    private static void Settle(DailyMingshiTests.Fixture f, bool progressFirst)
    {
        f.Client.Dispatch(progressFirst ? (ushort)355 : (ushort)1202, progressFirst ? DailyMingshiTests.Progress : DailyMingshiTests.Harvest);
        Update();
        Check(State("\"status\":\"PENDING\"") && State("\"account_progress_observed\":false"), "either_first_callback_insufficient");
        f.Client.Dispatch(progressFirst ? (ushort)1202 : (ushort)355, progressFirst ? DailyMingshiTests.Harvest : DailyMingshiTests.Progress);
        Update(); Update();
        Check(State("\"status\":\"SETTLED\"") && State("\"next_action\":\"close\"") &&
            State("\"stock_callback_returned\":true,\"account_progress_observed\":true,\"panel_closed\":false") &&
            RootBase.Replies == 1 && f.Panel.Replies == 1 && f.Role.stone == 505 && f.Role.gold == 30 &&
            f.Role.bind_gold == 7 && f.Role.gold_consume == 0 && f.Role.god_bonus_today == 1 &&
            ReferenceEquals(f.Role.acts[7u], ((Database.Activity_Reward)f.Role.act_rewards[2u]).acts[7u]) &&
            ReferenceEquals(f.ActivityHandler.handler, f.StockActivity), "both_originals_return_and_dual_caches_settle_without_gold");
    }
    private static void Reconnect(bool expected, bool destroy, bool alreadyStopping = false)
    {
        if (!destroy && !alreadyStopping) Send("stop");
        if (!destroy) Check(GameObject.LastHelper.PendingDestroy, "cleanup_requests_own_destroy");
        Check(Bootstrap(OtherNonce).StartsWith("BOOTSTRAP_REJECTED;"), "no_rebootstrap_before_own_destroy");
        Invoke("OnDestroy");
        Check(State("\"component_destroyed\":true") && State("\"reconnect_allowed\":" + (expected ? "true" : "false")), "on_destroy_reconnect_witness");
        if (expected) Check(Bootstrap(OtherNonce).StartsWith("BOOTSTRAP_OK;"), "verified_clean_rebootstrap");
        else Check(Read("recovery_unknown.json").Contains("daily_mingshi_request_or_cleanup_unresolved_restart_required") &&
            Bootstrap(OtherNonce).StartsWith("BOOTSTRAP_REJECTED;recovery_unknown"), "unverified_ui_or_callback_blocks_rebootstrap");
    }
    private static void InterfereDuringFinalSample(DailyMingshiTests.Fixture f, string scenario, string action)
    {
        int rev = Revision();
        string consumed = Queue(action);
        bool changed = false;
        game.net.tcp_base.Handler replacement = null;
        Global.BeforeActivityTemplate = delegate
        {
            if (changed || !State("\"phase\":\"owned\"")) return;
            changed = true;
            if (scenario == "same-operation") Publish(ActionWire(sequence, action, rev, OtherOperation));
            else if (scenario == "same-action") Publish(ActionWire(sequence, action == "open" ? "harvest" : "close", rev, Operation));
            else if (scenario == "same-role") Publish(ActionWire(sequence, action, rev, Operation, "124"));
            else if (scenario == "same-revision") Publish(ActionWire(sequence, action, rev + 1, Operation));
            else if (scenario == "same-task") Publish(consumed.Replace("task_id=7", "task_id=8"));
            else if (scenario == "same-nonce") Publish(consumed.Replace(Nonce, OtherNonce));
            else if (scenario == "same-name") Publish(Wire(Nonce, sequence, "heartbeat"));
            else if (scenario == "higher-heartbeat") Publish(Wire(Nonce, ++sequence, "heartbeat"));
            else if (scenario == "stop") Publish(Wire(Nonce, ++sequence, "stop"));
            else if (scenario == "lease") Set(Field(Current, "life"), "touched", unchecked((uint)(Environment.TickCount - 20001)));
            else if (scenario == "deadline") Set(Current, "guiQueuedTick", unchecked(Environment.TickCount - 1501));
            else if (scenario == "stream") f.Client.m_stream = new MemoryStream();
            else if (scenario == "connect-lost") f.Client.m_bConnectLost = true;
            else if (scenario == "gui") GUI.enabled = false;
            else if (scenario == "role") Global.character_ = new Database.Character();
            else if (scenario == "panel") GameObject.Paths.Remove("/Divinity/DivinityUI");
            else if (scenario == "overlay") f.Foreign.visible = true;
            else if (scenario == "wrong-chrome") f.CampaignSign.ParentUI = new CampaignUI();
            else if (scenario == "quote") f.AddQuote(99, 1, 11);
            else if (scenario == "replaced-355") replacement = f.ReplaceActivityRegistration();
            else throw new Exception("unrecognized interference " + scenario);
        };
        Invoke("OnGUI"); Global.BeforeActivityTemplate = null;
        bool stopping = scenario == "stop" || scenario == "lease";
        Check(changed && f.OpenCalls == (action == "open" ? 0 : 1) && f.Panel.Calls == (action == "close" ? 1 : 0) &&
            f.Panel.Returns == 0 && !f.Client.m_handlers.Contains((ushort)1202) &&
            ReferenceEquals(f.ActivityHandler.handler, f.StockActivity) &&
            State("\"phase\":\"not_invoked\",\"invocation_attempted\":false") &&
            (stopping || Ack("REJECTED")), "full_commit_refuses_" + action + "_" + scenario);
        Reconnect(action == "open", false, stopping);
        if (replacement != null) Check(ReferenceEquals(f.Client.m_handlers[(ushort)355], replacement) &&
            ReferenceEquals(replacement.handler, f.StockActivity), "final_sample_preserves_replacement_registration");
    }
    private static void Replace355WhileOwned(DailyMingshiTests.Fixture f, bool beforeReady)
    {
        if (beforeReady)
        {
            RunGui("open");
            Check(State("\"status\":\"OPENING\""), "original_355_owned_during_load");
        }
        else Open(f);
        game.net.tcp_base.Handler replacement = f.ReplaceActivityRegistration();
        Update();
        if (beforeReady) { f.Ready(); Update(); }
        Check(State("\"status\":\"REJECTED\"") && State("\"reason\":\"mingshi_stock_355_replaced\"") &&
            State("\"pending_daily_mingshi\":true") && State("\"next_action\":\"\""), "foreign_355_rejects_while_retaining_ui");
        Publish(ActionWire(++sequence, "harvest", Revision(), Operation)); Update(); Invoke("OnGUI");
        Check(Ack("REJECTED") && f.Panel.Calls == 0 && !f.Client.m_handlers.Contains((ushort)1202), "no_1202_after_replacement");
        Reconnect(false, false);
        Check(ReferenceEquals(f.Client.m_handlers[(ushort)355], replacement) &&
            ReferenceEquals(replacement.handler, f.StockActivity), "stop_preserves_replacement_registration");
    }
    private static bool CampaignPanelBlocked(string scenario)
    {
        return State(scenario.EndsWith("duplicate") ? "mingshi_campaign_not_unique" : "mingshi_campaign_root_mismatch");
    }
    private static void RejectCampaignEntry(DailyMingshiTests.Fixture f, string scenario)
    {
        f.ShowOtherCampaign(scenario.EndsWith("duplicate")); Update();
        Check(CampaignPanelBlocked(scenario) && State("\"next_action\":\"\""), "ambiguous_campaign_refuses_entry");
        Publish(ActionWire(++sequence, "open", Revision(), Operation)); Update(); Invoke("OnGUI");
        Check(Ack("REJECTED") && f.OpenCalls == 0, "ambiguous_campaign_never_opens_stock_panel");
        Reconnect(true, false);
    }

    public static int Main(string[] args)
    {
        string scenario = args[0];
        try
        {
            bool campaign = scenario == "settle-campaign" || scenario == "destroy-closing" || scenario == "commit-open-wrong-chrome" ||
                scenario.StartsWith("entry-campaign-") || scenario.StartsWith("return-campaign-");
            DailyMingshiTests.Fixture f = new DailyMingshiTests.Fixture(campaign);
            game.net.tcp_base.Handler clock = RootUI.BindClock(f.Client);
            Delegate stockClock = clock.handler;
            Check(Bootstrap(Nonce).StartsWith("BOOTSTRAP_OK;"), "boot");
            Update(); Update(); ReadAndCheckGrammar();
            Check(State("\"daily_stone_enabled\":false") && State("\"daily_timed_enabled\":false") &&
                State("\"daily_reward_enabled\":false"), "other_optional_capabilities_off");
            if (scenario == "disabled")
            {
                Send("daily-mingshi-observe");
                Check(Ack("REJECTED") && State("\"daily_mingshi_enabled\":false,\"daily_mingshi\":null") &&
                    Read("ack.json").Contains("\"daily_mingshi_enabled\":false,\"daily_mingshi\":null"), "default_no_mingshi_capability");
                Publish(ActionWire(++sequence, "open", 1, Operation)); Update(); Invoke("OnGUI");
                Check(Ack("REJECTED") && f.OpenCalls == 0 && ReferenceEquals(clock.handler, stockClock), "disabled_command_and_observe_have_no_stock_effects");
                Reconnect(true, false);
            }
            else
            {
                Check(DailyRefreshProof.RecordSnapshot(f.Role, DailyTransport.Read(f.Client), 218000, 304400), "seed_verified_daily_fixture");
                ReadinessAndIdentity(f);
                if (scenario == "pre-open-stop" || scenario == "pre-open-destroy")
                { Queue("open"); Reconnect(true, scenario.EndsWith("destroy")); Check(f.OpenCalls == 0, "pre_open_never_invoked"); }
                else if (scenario.StartsWith("entry-campaign-")) RejectCampaignEntry(f, scenario);
                else if (scenario.StartsWith("replace-355-")) Replace355WhileOwned(f, scenario.EndsWith("opening"));
                else if (scenario.StartsWith("commit-open-"))
                    InterfereDuringFinalSample(f, scenario.Substring(12), "open");
                else
                {
                    Open(f);
                    if (scenario == "stop-open" || scenario == "destroy-open") Reconnect(false, scenario.StartsWith("destroy"));
                    else if (scenario.StartsWith("commit-harvest-"))
                        InterfereDuringFinalSample(f, scenario.Substring(15), "harvest");
                    else
                    {
                        if (scenario == "settle-capital") ExcludeOtherBusiness(f, 0);
                        Harvest(f);
                        if (scenario == "stop-pending" || scenario == "destroy-pending")
                        {
                            Reconnect(false, scenario.StartsWith("destroy"));
                            Check(ReferenceEquals(f.ActivityHandler.handler, f.StockActivity), "stop_restores_activity_callback");
                            f.Client.Dispatch(1202, DailyMingshiTests.Harvest);
                            f.Client.Dispatch(355, DailyMingshiTests.Progress);
                            Check(f.Panel.Replies == 1 && RootBase.Replies == 1 && State("\"account_progress_observed\":false"), "late_originals_forward_only");
                        }
                        else if (scenario == "partial-cache")
                        {
                            RootBase.PartialProjection = true;
                            f.Client.Dispatch(1202, DailyMingshiTests.Harvest);
                            f.Client.Dispatch(355, DailyMingshiTests.Progress);
                            Update(); Check(State("\"status\":\"UNKNOWN\"") && !State("\"account_progress_observed\":true"), "single_cache_cannot_settle");
                            ExcludeOtherBusiness(f, 1); Reconnect(false, false);
                        }
                        else
                        {
                            Settle(f, campaign);
                            if (scenario == "stop-settled" || scenario == "destroy-settled")
                            { ExcludeOtherBusiness(f, 1); Reconnect(false, scenario.StartsWith("destroy")); }
                            else if (scenario.StartsWith("commit-close-"))
                                InterfereDuringFinalSample(f, scenario.Substring(13), "close");
                            else
                            {
                                RunGui("close"); Check(f.Panel.Returns == 1 && f.Panel.Calls == 1, "one_stock_close");
                                if (campaign)
                                {
                                    Check(f.CampaignReturnCalls == 1 && f.Client.m_handlers.Contains((ushort)1025) &&
                                        State("\"status\":\"CLOSING\"") && State("\"panel_closed\":false"), "campaign_awaits_stock_1025");
                                    if (scenario != "destroy-closing")
                                    {
                                        f.Client.Dispatch(1025, new byte[] { 1 });
                                        Update(); Check(State("\"panel_closed\":false"), "callback_without_assets_does_not_release");
                                        f.FinishReturn(); f.GlobalUi.level_rmg = f.DivinityRoot; Update();
                                        Check(State("\"panel_closed\":false"), "both_original_root_pointers_required");
                                        f.GlobalUi.level_rmg = f.MainRoot;
                                        if (scenario.StartsWith("return-campaign-")) f.ShowOtherCampaign(scenario.EndsWith("duplicate"));
                                        Update();
                                    }
                                }
                                if (scenario == "destroy-closing") Reconnect(false, true);
                                else if (scenario.StartsWith("return-campaign-"))
                                {
                                    Check(CampaignPanelBlocked(scenario) && State("\"status\":\"CLOSING\"") &&
                                        State("\"panel_closed\":false") && State("\"pending_daily_mingshi\":true") &&
                                        f.Panel.Returns == 1, "ambiguous_campaign_refuses_verified_return");
                                    Reconnect(false, false);
                                }
                                else
                                {
                                    Check(State("\"status\":\"SUCCEEDED\"") && State("\"panel_closed\":true") &&
                                        State("\"pending_daily_mingshi\":false"), "verified_close_releases_business");
                                    Reconnect(true, campaign);
                                }
                            }
                        }
                    }
                }
            }
            Check(ReferenceEquals(clock.handler, stockClock), "clock_observer_restored");
            Console.WriteLine("{\"status\":\"PASS\",\"scenario\":" + Command.Quote(scenario) + ",\"checks\":" + checks +
                ",\"scope\":\"production Probe, Mingshi model, callback adapters and receipts in synthetic Unity/game objects\"}");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
