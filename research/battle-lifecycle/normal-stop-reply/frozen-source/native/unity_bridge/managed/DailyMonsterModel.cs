using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using UnityEngine;
using D = JiangShen.AssistantRuntime.SharedDevelopmentContext;

namespace JiangShen.AssistantRuntime
{
    internal delegate bool DailyMonsterInvocationGate();

    // Fixed normal-button paths for own monsters, friend monsters and revenge
    // demons. Stock callbacks retain all packet handling and battle settlement.
    internal sealed class DailyMonsterModel
    {
        private sealed class Intent
        {
            internal string Action;
        }

        private Model model;
        private object character, friend, buddy, target;
        private Component root, buddyUi, formation, battle, normalResult, reward, failed, source;
        private Component roundSource, roundRoot;
        private object roundTarget, roundOwner, roundReport, roundReportBaseline;
        private DemonBattleRequest demonRequest;
        private BattleLaunchAttempt roundLaunch;
        private readonly Hashtable visited = new Hashtable();
        private Intent queued;
        private ulong roleId, friendId;
        private uint cycle;
        private int taskId, current, required, baseline, sequence, gold, boundGold, started, returnedAt;
        private int targetId, roundBaseline, completedTargets;
        private int notInvokedSequence;
        private bool blocked, stopped, normalStopDrain, hold, attempted, battleReturned, terminal, rewardClosed;
        private bool roundActive, roundAttempted, roundCollect;
        private string scene = "", operationId = "", status = "IDLE", reason = "", phase = "", stamp = "", unavailableReason = "";

        internal int Revision { get; private set; }
        internal bool Busy { get { return queued != null || hold; } }
        internal bool MutationAttempted { get { return attempted; } }
        internal bool GuiInvocationAttempted { get; private set; }
        internal bool OutcomeUnknown { get { return status == "UNKNOWN"; } }
        internal Component OwnedBattleReportSource(Component candidate)
        {
            // A report alone never establishes a successful 0x492 settlement.
            return null;
        }

        internal BattlePresentationOwnership OwnedBattlePresentation(Component candidate)
        {
            if (taskId == 24 || stopped || !OwnOrdinaryBattle(candidate)) return null;
            Component ownedBattle = candidate; object ownedTarget = roundTarget;
            string ownedOperation = operationId;
            return new BattlePresentationOwnership(taskId == 10 ? BattleMode.FriendBuildingMonster : BattleMode.OwnBuildingMonster,
                ownedOperation, roundSource, ownedTarget, (taskId == 10 ? friendId.ToString(CultureInfo.InvariantCulture) + ":" : "") +
                Int(ownedTarget, "id").ToString(CultureInfo.InvariantCulture), delegate
                { return operationId == ownedOperation && ReferenceEquals(roundTarget, ownedTarget) && OwnOrdinaryBattle(ownedBattle); });
        }

        private bool OwnOrdinaryBattle(Component candidate)
        {
            if (taskId != 10 && taskId != 15 || blocked || terminal || status == "UNKNOWN" && !stopped || !hold ||
                !roundActive || !roundAttempted || roundCollect || battleReturned || phase != "battle" ||
                String.IsNullOrEmpty(operationId) || candidate == null || !ReferenceEquals(candidate, battle) ||
                roundSource == null || roundRoot == null || roundTarget == null || roundReport == null ||
                !ReferenceEquals(D.Static(Model.FirstType("Global"), "character_"), character) || ULong(character, "id") != roleId ||
                !RoundTargetReferenceOwned() || !ReferenceEquals(D.Read(candidate, "m_prevUI"), roundRoot) ||
                !ReferenceEquals(D.Read(candidate, "m_parentUI"), roundSource)) return false;
            object interpreter = D.Read(candidate, "interpreter");
            uint now, day, observedCycle;
            return DailyRefreshGate.ReadClock(character, out now, out day, out observedCycle) == "" && observedCycle == cycle &&
                interpreter != null && ReferenceEquals(D.Read(interpreter, "m_report"), roundReport);
        }

        internal bool TryPresentSettledBattle(Component candidate)
        {
            if (taskId != 24 || stopped || blocked || status == "UNKNOWN" || demonRequest == null ||
                candidate == null || !ReferenceEquals(candidate, battle) || !D.Gui) return false;
            try
            {
                if (demonRequest.Error != "") throw new ProbeFailure(demonRequest.Error);
                if (!demonRequest.Returned) return false;
                if (!demonRequest.Verify()) throw new ProbeFailure("monster_demon_settlement_evidence_changed");
                return BattleSettlement.PresentSettled(candidate, roundSource, BattleMode.OwnDemon, operationId,
                    roundTarget, demonRequest.TargetId, demonRequest.Receipt, 1170, 0, demonRequest.Verify);
            }
            catch (Exception error)
            {
                status = "UNKNOWN"; reason = error is ProbeFailure ? error.Message : "monster_demon_presentation_unverified";
                Publish(); return false;
            }
        }

        internal void Capture(Model currentModel, int activityId, bool admissionBlocked)
        {
            model = currentModel;
            blocked = admissionBlocked;
            if (!Supported(activityId)) throw new ArgumentOutOfRangeException("activityId");
            if (hold && taskId != activityId) return;
            if (!hold && taskId != activityId)
            {
                terminal = attempted = battleReturned = rewardClosed = false;
                operationId = ""; status = "IDLE"; phase = "";
            }
            taskId = activityId;
            try
            {
                character = D.Static(Model.FirstType("Global"), "character_");
                ulong observedRole = ULong(character, "id");
                if (hold && roleId != observedRole) throw new InvalidOperationException("monster_role_changed");
                roleId = observedRole;
                uint now, day, observedCycle;
                string refresh = DailyRefreshGate.ReadClock(character, out now, out day, out observedCycle);
                if (hold && cycle != observedCycle) throw new InvalidOperationException("monster_daily_cycle_changed");
                cycle = observedCycle;
                ReadActivity();
                if (demonRequest != null && demonRequest.Error != "") throw new ProbeFailure(demonRequest.Error);
                root = model.CurrentRoot();
                scene = root == null ? "" : (string)D.Read(root, "LevelID");
                buddyUi = formation = battle = normalResult = reward = failed = source = null;
                foreach (UnityEngine.Object item in model.AllUi())
                {
                    Component ui = item as Component;
                    if (ui == null) continue;
                    string name = ui.GetType().Name;
                    if (name == "BuddyUI") buddyUi = ui;
                    else if (name == "FormationUI" && D.Visible(ui)) formation = ui;
                    else if (name == "BattleUI" && D.Visible(ui)) battle = ui;
                    else if (name == "BattleNormalResultUI" && D.Visible(ui)) normalResult = ui;
                    else if (name == "BattleResultUI" && D.Visible(ui)) reward = ui;
                    else if (name == "BattleValueUI" && D.Visible(ui)) failed = ui;
                }
                // BonusOpen starts loading the stock cached reward while it is
                // still hidden. Retain that component until its close callback
                // restores the main UI; visibility alone misses this interval.
                Component cachedReward = D.Static(Model.FirstType("__BaseUI"), "battle_result_ui") as Component;
                if (cachedReward != null)
                {
                    if (cachedReward.GetType() != Model.FirstType("BattleResultUI"))
                        throw new InvalidOperationException("monster_reward_cache_type_changed");
                    reward = cachedReward;
                }
                if (refresh != "") { reason = refresh; Publish(); return; }
                if (stopped || terminal || status == "UNKNOWN") { Publish(); return; }
                if (!hold) { status = "IDLE"; reason = current >= required ? "monster_activity_complete" : ""; Publish(); return; }
                if (unavailableReason != "")
                {
                    status = attempted ? "BLOCKED" : "UNAVAILABLE"; reason = unavailableReason;
                    if (Home() && Idle()) Finish(status, unavailableReason);
                    Publish(); return;
                }
                if (Int(character, "gold") < gold || Int(character, "bind_gold") < boundGold)
                    throw new InvalidOperationException("monster_premium_balance_decreased");
                if (unchecked((uint)(Environment.TickCount - started)) > 240000)
                    throw new InvalidOperationException("monster_operation_timeout");

                reason = "";
                if (phase == "opening_capital")
                {
                    if (scene != "Capital" || !D.Bool(D.Static(Model.FirstType("Global"), "extern_enabled")))
                    { status = "OPENING"; Publish(); return; }
                    phase = "";
                }
                if (battle != null)
                {
                    if (roundAttempted) ObserveRoundBattle();
                    status = normalResult != null ? "RESULT" : "BATTLE";
                    Publish(); return;
                }
                if (RewardPending() || failed != null)
                {
                    status = "RESULT";
                    Publish(); return;
                }
                if (phase == "battle" && !battleReturned)
                {
                    status = "BATTLE";
                    Publish(); return;
                }
                if (phase == "collecting")
                { status = "OPENING"; Publish(); return; }
                if (phase == "formation" && formation == null)
                { status = "OPENING"; Publish(); return; }
                if (phase == "formation" && roundActive)
                {
                    source = roundSource; target = roundTarget;
                    if (!RoundTargetCurrent()) throw new InvalidOperationException("monster_prepared_target_changed");
                    status = "OPENING"; Publish(); return;
                }
                if (phase == "returning" && roundActive)
                {
                    if (!PageReady(roundRoot, taskId == 10 ? "CapitalFriend" : "Capital") || !Idle())
                    { status = "RETURNING"; Publish(); return; }
                    if (current <= roundBaseline)
                    {
                        if (unchecked((uint)(Environment.TickCount - returnedAt)) > 2000)
                            Block("monster_battle_did_not_advance_task");
                        Publish(); return;
                    }
                    if (!RoundTargetCleared()) throw new InvalidOperationException("monster_target_settlement_unverified");
                    completedTargets++;
                    ClearRound();
                    phase = ""; started = Environment.TickCount;
                }
                if (phase == "returning_home")
                {
                    if (Home() && Idle())
                    {
                        if (unavailableReason != "") Finish(attempted ? "BLOCKED" : "UNAVAILABLE", unavailableReason);
                        else Finish(current >= required ? "COMPLETED" : "BLOCKED",
                            current >= required ? "monster_activity_complete" : "monster_battle_did_not_advance_task");
                    }
                    Publish(); return;
                }
                if (current >= required)
                {
                    if (Home() && Idle()) Finish("COMPLETED", "monster_activity_complete");
                    else status = "RETURNING";
                    Publish(); return;
                }
                if (scene == "Battle") { status = "BATTLE"; Publish(); return; }
                if (taskId == 10)
                {
                    try { ObserveFriend(now); }
                    catch (Exception error)
                    {
                        if (attempted) throw;
                        Block(error.Message);
                    }
                }
                else if (scene == "Capital") SelectTarget(character, now);
                else status = "OPENING";
            }
            catch (Exception error)
            {
                reason = error.Message;
                status = attempted ? "UNKNOWN" : "BLOCKED";
                if (demonRequest != null) demonRequest.Restore();
            }
            Publish();
        }

        private void ReadActivity()
        {
            IDictionary acts = (IDictionary)D.Read(character, "acts");
            object row = acts[(uint)taskId];
            if (row == null) throw new InvalidOperationException("monster_activity_missing");
            current = Int(row, "condition_param");
            required = Int(D.Read(row, "temp"), "cond_param");
        }

        private void ObserveFriend(uint now)
        {
            if (Int(character, "monster_assist_count") >= 5)
            { Block("monster_friend_daily_limit"); return; }
            if (phase == "visiting")
            {
                object arrived = D.Static(Model.GameType("CapitalFriendUI"), "myfriend");
                if (scene != "CapitalFriend" || arrived == null || ULong(arrived, "id") != friendId || LoadingTransition())
                { status = "OPENING"; return; }
                phase = "";
            }
            if (scene == "CapitalFriend" && phase != "opening_buddy" &&
                (buddyUi == null || !D.Visible(buddyUi)))
            {
                friend = D.Static(Model.GameType("CapitalFriendUI"), "myfriend");
                if (friend == null || ULong(friend, "id") != friendId) throw new InvalidOperationException("monster_friend_changed");
                if (!Bool(friend, "isfans")) { Block("monster_friend_not_mutual"); return; }
                SelectTarget(friend, now);
                if (source != null && phase == "next_friend") phase = "";
                if (source == null && Idle() && !LoadingTransition())
                {
                    phase = "next_friend";
                    status = "RETURNING";
                }
                return;
            }
            if (scene != "Capital" && scene != "CapitalFriend") { status = "OPENING"; return; }
            if (buddyUi == null || !D.Visible(buddyUi) || D.Loading(buddyUi)) { status = "OPENING"; return; }
            if (!Idle()) { status = "OPENING"; return; }
            if (phase == "opening_buddy") phase = "";
            buddy = null;
            foreach (object candidate in (IList)D.Read(buddyUi, "Buddys"))
            {
                ulong id = ULong(candidate, "id");
                if (id != roleId && !visited.Contains(id) && Bool(candidate, "isfan") && Bool(candidate, "ismonster"))
                { buddy = candidate; friendId = id; break; }
            }
            if (buddy == null) Block("monster_no_friend_target");
            else status = "READY";
        }

        private void SelectTarget(object owner, uint now)
        {
            target = null; source = null; targetId = 0;
            if (taskId == 24)
            {
                target = D.Read(owner, "m_demon");
                if (target == null) { Block("monster_no_revenge_demon"); return; }
                if (!Bool(target, "killed"))
                {
                    if (Int(character, "level") < 25) { Block("monster_demon_level_required"); return; }
                    if (Cooldown(5, now)) { Block("monster_demon_cooldown"); return; }
                }
                GameObject mesh = D.Read(target, "mesh") as GameObject;
                source = mesh == null ? null : mesh.GetComponent("DemonScript");
            }
            else
            {
                if (taskId == 10 && Cooldown(4, now)) { Block("monster_friend_battle_cooldown"); return; }
                foreach (DictionaryEntry entry in (IDictionary)D.Read(owner, "building_monsters"))
                {
                    object monster = entry.Value;
                    bool dead = ULong(monster, "idkiller") != 0;
                    if (taskId == 10 && dead || !dead && UInt(monster, "cd_time") > now) continue;
                    GameObject mesh = D.Read(monster, "mesh") as GameObject;
                    Component script = mesh == null ? null : mesh.GetComponent("BuildingMonsterScript");
                    int id = Int(monster, "id");
                    bool preferAlive = target != null && ULong(target, "idkiller") != 0 && !dead;
                    bool sameState = target != null && (ULong(target, "idkiller") != 0) == dead;
                    if (script != null && (target == null || preferAlive || sameState && id < targetId))
                    { target = monster; source = script; targetId = id; }
                }
                if (source == null && taskId != 10) { Block("monster_no_capital_target"); return; }
            }
            if (source != null && D.Enabled(source) && !D.Loading(source)) status = "READY";
            else status = "OPENING";
        }

        private bool Cooldown(byte id, uint now)
        {
            IDictionary cooldowns = (IDictionary)D.Read(character, "cooldowns");
            object value = cooldowns[id];
            return value != null && UInt(value, "time") > now;
        }

        internal bool Queue(int activityId, ulong role, string id, int commandSequence, int expectedRevision,
            string action, out string why)
        {
            why = "monster_admission";
            if (stopped || blocked || queued != null || activityId != taskId || role != roleId ||
                commandSequence <= sequence || !Command.ValidNonce(id))
                return false;
            if (expectedRevision != Revision) { why = "monster_revision_changed"; return false; }
            if (action != Offer()) { why = "monster_action_changed"; return false; }
            if (hold && id != operationId) return false;
            if (!hold)
            {
                if (action != "open") return false;
                operationId = id; baseline = current; gold = Int(character, "gold"); boundGold = Int(character, "bind_gold");
                attempted = battleReturned = terminal = rewardClosed = false;
                phase = unavailableReason = ""; visited.Clear(); friendId = 0; started = Environment.TickCount;
                ClearRound(); completedTargets = 0;
            }
            sequence = commandSequence;
            notInvokedSequence = 0; GuiInvocationAttempted = false;
            queued = new Intent { Action = action };
            Publish(); why = ""; return true;
        }

        internal bool ExecuteGui(DevelopmentPublication publish, DailyMonsterInvocationGate begin, out string why)
        {
            GuiInvocationAttempted = false;
            why = "monster_not_queued";
            Intent intent = queued;
            if (intent == null) return false;
            queued = null;
            Capture(model, taskId, blocked);
            if (stopped || blocked || !D.Gui || intent.Action != Offer())
            { why = "monster_action_changed"; Publish(); return false; }
            publish();
            if (stopped || !begin()) { why = "monster_cancelled"; Publish(); return false; }
            GuiInvocationAttempted = true;
            hold = true;
            switch (intent.Action)
            {
                case "open":
                    phase = ""; status = "OPENING";
                    if (taskId == 10 && (scene == "Capital" || scene == "CapitalFriend"))
                    {
                        phase = "opening_buddy";
                        if (buddyUi == null || !D.Visible(buddyUi))
                        {
                            // A successful visit hides BuddyUI but leaves it in
                            // m_subui. Clear that stale registration so the stock
                            // toggle opens the list on this same friend page.
                            if (buddyUi != null && ReferenceEquals(D.Static(Model.GameType("MainUI"), "m_subui"), buddyUi))
                                SetSubUiNull();
                            D.MainBranch("_social_friend");
                        }
                    }
                    else if (scene != "Capital") { phase = "opening_capital"; D.MainBranch("_capital"); }
                    break;
                case "visit":
                    visited[friendId] = true;
                    D.Method(buddyUi.GetType(), "cs_friend_visit_0x4C0", new[] { Model.FirstType("Buddy") }, false, typeof(void))
                        .Invoke(buddyUi, new[] { buddy });
                    phase = "visiting"; status = "OPENING";
                    break;
                case "prepare":
                    bool collect = taskId == 24 ? Bool(target, "killed") : taskId == 15 && ULong(target, "idkiller") != 0;
                    roundSource = source; roundRoot = root; roundTarget = target;
                    roundOwner = taskId == 10 ? friend : character;
                    roundBaseline = current; roundActive = true; roundCollect = collect;
                    roundAttempted = collect; battleReturned = rewardClosed = false;
                    if (!RoundTargetCurrent()) throw new InvalidOperationException("monster_prepared_target_changed");
                    if (collect) attempted = true;
                    if (taskId == 24)
                        D.Method(source.GetType(), collect ? "DemonClear" : "DemonAttack",
                            new[] { typeof(bool), typeof(object[]) }, false, typeof(void)).Invoke(source, new object[] { true, null });
                    else D.Call(source, "MonsterAction");
                    phase = collect ? "collecting" : "formation"; status = "OPENING";
                    break;
                case "start":
                    if (!roundActive || current != roundBaseline || !RoundTargetCurrent())
                        throw new InvalidOperationException("monster_prepared_target_changed");
                    GameObject priorBattle = model.BattleUiObject();
                    Component priorUi = priorBattle == null ? null : priorBattle.GetComponent(Model.GameType("BattleUI"));
                    object priorInterpreter = priorUi == null ? null : D.Read(priorUi, "interpreter");
                    roundReport = null;
                    roundReportBaseline = priorInterpreter == null ? null : D.Read(priorInterpreter, "m_report");
                    attempted = roundAttempted = true; phase = "battle"; status = "BATTLE";
                    if (taskId == 24) demonRequest = new DemonBattleRequest(this);
                    BattleMode launchMode = taskId == 24 ? BattleMode.OwnDemon : taskId == 10 ?
                        BattleMode.FriendBuildingMonster : BattleMode.OwnBuildingMonster;
                    DemonBattleRequest ownedRequest = demonRequest;
                    object launchTarget = roundTarget;
                    Component launchSource = roundSource;
                    roundLaunch = BattleLaunch.Formation(launchMode, formation, launchSource, launchTarget, delegate
                    {
                        return !stopped && hold && roundActive && ReferenceEquals(roundTarget, launchTarget) &&
                            ReferenceEquals(roundSource, launchSource) && RoundTargetCurrent();
                    }, ownedRequest == null ? null : (BattleLaunchAction)delegate { ownedRequest.Attach(); });
                    break;
                case "close_battle":
                    D.Call(normalResult, "Hide");
                    D.Call(battle, "battle_exit");
                    ResultClosed();
                    break;
                case "close_result":
                    Delegate close = D.Read(reward, "OnClose") as Delegate;
                    Component previous = D.Read(reward, "m_prevUI") as Component;
                    D.Call(reward, "Hide");
                    if (close != null) close.DynamicInvoke(null);
                    else if (previous != null) D.Call(previous, "ToEnable");
                    rewardClosed = true;
                    ResultClosed();
                    break;
                case "close_failed":
                    D.Call(failed, "Hide");
                    SetSubUiNull();
                    ResultClosed();
                    break;
                case "return_home":
                    object sub = D.Static(Model.GameType("MainUI"), "m_subui");
                    if (sub != null)
                    {
                        string type = sub.GetType().Name;
                        if (type != "BuddyUI" && type != "FormationUI" && type != "BattleValueUI")
                            throw new InvalidOperationException("monster_unexpected_panel");
                        SetSubUiNull();
                    }
                    D.MainBranch("_capital");
                    phase = "returning_home"; returnedAt = Environment.TickCount; status = "RETURNING";
                    break;
            }
            Publish(); why = ""; return true;
        }

        private void ResultClosed()
        {
            // A previous daily task can leave its result panel visible. Closing
            // that panel must not finish a monster task that has not started.
            battleReturned = roundAttempted;
            phase = roundAttempted ? "returning" : "";
            returnedAt = Environment.TickCount;
            status = roundAttempted ? "RETURNING" : "OPENING";
        }

        private string Offer()
        {
            if (stopped || blocked || queued != null || status == "UNKNOWN" || reason == "daily_refresh_pending") return "";
            if (!hold) return current < required && (status == "IDLE" || status == "COMPLETED") ? "open" : "";
            if (reward != null && D.Enabled(reward) && !D.Loading(reward) && (!roundActive || OwnReward())) return "close_result";
            if (normalResult != null && battle != null && !Bool(battle, "bBattle_fighting") &&
                !Bool(battle, "bNeedWaitForBattleEnd") && !Bool(battle, "bUseBattleEnd")) return "close_battle";
            if (failed != null && D.Enabled(failed)) return "close_failed";
            if (battle != null || scene == "Battle" || RewardPending() || LoadingTransition() || !Idle()) return "";
            if (unavailableReason != "") return Home() ? "" : "return_home";
            if (phase == "returning" && roundActive) return "";
            if (phase == "opening_capital" || phase == "opening_buddy" || phase == "returning_home") return "";
            if (phase == "next_friend") return "open";
            if (status == "BLOCKED" || current >= required)
                return Home() ? "" : "return_home";
            if (formation != null && D.Enabled(formation) && Bool(formation, "isVisible") &&
                !Bool(formation, "isLoading") && !Bool(formation, "onfight") && Int(formation, "waitstate") == 0)
            {
                string expected = taskId == 24 ? "demon" : taskId == 10 ? "friend_monster" : "building_monster";
                Delegate attack = D.Read(formation, "OnAttack") as Delegate;
                if ((string)D.Read(formation, "substate") == expected && attack != null &&
                    roundActive && ReferenceEquals(attack.Target, roundSource) && RoundTargetCurrent())
                    return "start";
                return "";
            }
            if (source != null && status == "READY") return "prepare";
            if (taskId == 10 && buddy != null && (scene == "Capital" || scene == "CapitalFriend") && D.Visible(buddyUi)) return "visit";
            if (phase == "visiting" || phase == "battle" || phase == "collecting") return "";
            if (scene != "Capital" || taskId == 10 && (buddyUi == null || !D.Visible(buddyUi))) return "open";
            return "";
        }

        private bool Home()
        {
            return PageReady(root, "Capital");
        }

        private bool PageReady(Component expectedRoot, string expectedScene)
        {
            if (scene != expectedScene || root == null || !ReferenceEquals(root, expectedRoot) ||
                !root.gameObject.activeInHierarchy || LoadingTransition() ||
                !D.Bool(D.Static(Model.FirstType("Global"), "extern_enabled")) ||
                D.Static(Model.GameType("MainUI"), "m_subui") != null || battle != null || normalResult != null ||
                RewardPending() || failed != null) return false;
            if (expectedScene == "CapitalFriend" && !ReferenceEquals(
                D.Static(Model.GameType("CapitalFriendUI"), "myfriend"), roundOwner)) return false;
            Component main = root.GetComponentInChildren(Model.GameType("MainUI"));
            return main != null && D.Enabled(main);
        }

        private bool RoundTargetCurrent()
        {
            return ReferenceEquals(root, roundRoot) && RoundTargetReferenceOwned();
        }

        private bool RoundTargetReferenceOwned()
        {
            if (!roundActive || roundSource == null || roundRoot == null || roundTarget == null ||
                !ReferenceEquals(D.Read(roundSource, "rmg"), roundRoot) ||
                Bool(roundSource, "m_bFriend") != (taskId == 10)) return false;
            if (taskId == 24)
                return ReferenceEquals(D.Read(character, "m_demon"), roundTarget) &&
                    ReferenceEquals(D.Read(roundSource, "m_demon"), roundTarget);
            object owner = taskId == 10 ? D.Static(Model.GameType("CapitalFriendUI"), "myfriend") : character;
            return ReferenceEquals(owner, roundOwner) && ReferenceEquals(D.Read(roundSource, "Monster"), roundTarget) &&
                ReferenceEquals(((IDictionary)D.Read(owner, "building_monsters"))[(byte)Int(roundTarget, "id")], roundTarget);
        }

        private bool RoundTargetCleared()
        {
            if (taskId == 24) return D.Read(character, "m_demon") == null && rewardClosed;
            object owner = taskId == 10 ? D.Static(Model.GameType("CapitalFriendUI"), "myfriend") : character;
            if (!ReferenceEquals(owner, roundOwner)) return false;
            object remaining = ((IDictionary)D.Read(owner, "building_monsters"))[(byte)Int(roundTarget, "id")];
            return rewardClosed && (taskId == 10 ? ReferenceEquals(remaining, roundTarget) &&
                ULong(roundTarget, "idkiller") == roleId : remaining == null);
        }

        private void ObserveRoundBattle()
        {
            if (!roundActive || roundCollect || !ReferenceEquals(D.Read(battle, "m_prevUI"), roundRoot) ||
                (taskId == 24 ? D.Read(battle, "m_parentUI") != null :
                    !ReferenceEquals(D.Read(battle, "m_parentUI"), roundSource)))
                throw new InvalidOperationException("monster_battle_owner_changed");
            object interpreter = D.Read(battle, "interpreter");
            object packet = interpreter == null ? null : D.Read(interpreter, "m_report");
            if (packet == null || ReferenceEquals(packet, roundReportBaseline))
                throw new InvalidOperationException("monster_battle_report_not_fresh");
            if (roundReport == null) roundReport = packet;
            if (!ReferenceEquals(packet, roundReport)) throw new InvalidOperationException("monster_battle_report_changed");
            Delegate close = D.Read(battle, "OnClose") as Delegate;
            if (close != null && (!ReferenceEquals(close.Target, roundSource) ||
                close.Method.Name != "BonusOpen" && close.Method.Name != "FailedOpen"))
                throw new InvalidOperationException("monster_battle_close_owner_changed");
        }

        private bool OwnReward()
        {
            Delegate close = D.Read(reward, "OnClose") as Delegate;
            return roundAttempted && close != null && ReferenceEquals(close.Target, roundSource) &&
                close.Method.Name == (taskId == 10 ? "AssistClose" : "BonusClose");
        }

        private void ClearRound()
        {
            if (demonRequest != null) { demonRequest.Restore(); demonRequest = null; }
            roundActive = roundAttempted = roundCollect = battleReturned = false;
            roundSource = roundRoot = null;
            roundTarget = roundOwner = roundReport = roundReportBaseline = null;
        }

        private bool RewardPending()
        { return reward != null && (D.Visible(reward) || D.Loading(reward)); }

        private bool LoadingTransition()
        {
            object loader = D.Static(Model.FirstType("BaseAsset"), "lds");
            return loader == null || ((Behaviour)loader).enabled || Bool(loader, "isBattling") ||
                root != null && (Bool(root, "bLoading") || Int(root, "ExpectingCount") != 0);
        }

        private bool Idle()
        {
            object client = D.Static(Model.FirstType("Global"), "m_client");
            if (((IList)D.Read(client, "messages")).Count != 0 || ((IList)D.Read(client, "m_sendDatas")).Count != 0) return false;
            foreach (DictionaryEntry entry in (IDictionary)D.Read(client, "m_handlers"))
                if (Convert.ToSingle(D.Read(entry.Value, "life"), CultureInfo.InvariantCulture) > 0) return false;
            return true;
        }

        private static void SetSubUiNull()
        {
            D.Method(Model.GameType("MainUI"), "SetSubUI", new[] { Model.FirstType("__BaseUI") }, true, typeof(void))
                .Invoke(null, new object[] { null });
        }

        private void Block(string detail)
        {
            status = attempted ? "BLOCKED" : "UNAVAILABLE"; reason = detail;
            unavailableReason = detail;
            if (Home()) Finish(status, detail);
        }

        private void Finish(string outcome, string detail)
        { status = outcome; reason = detail; hold = false; terminal = true; }

        internal void Stop(string detail)
        {
            normalStopDrain = !stopped && detail == "stop" && !blocked && !terminal && status != "UNKNOWN" &&
                demonRequest != null && !demonRequest.Returned && demonRequest.Error == "";
            stopped = true; queued = null;
            if (demonRequest != null && !normalStopDrain) demonRequest.Restore();
            status = attempted && hold ? "UNKNOWN" : "STOPPED"; reason = detail; Publish();
        }

        internal void NotInvoked(string detail)
        {
            queued = null; notInvokedSequence = sequence; GuiInvocationAttempted = false;
            reason = detail; status = "BLOCKED"; Publish();
        }

        private void Publish()
        {
            string value = Json();
            value = value.Substring(value.IndexOf(",\"task_id\"", StringComparison.Ordinal));
            if (stamp != value) { stamp = value; Revision++; }
        }

        internal string Json()
        {
            return "{\"schema\":1,\"revision\":" + Revision + ",\"task_id\":" + taskId +
                ",\"role_id\":" + D.Q(roleId.ToString(CultureInfo.InvariantCulture)) + ",\"cycle\":" + cycle +
                ",\"current\":" + current + ",\"required\":" + required + ",\"status\":" + D.Q(queued == null ? status : "QUEUED") +
                ",\"next_action\":" + D.Q(Offer()) + ",\"operation_id\":" + D.Q(operationId) + ",\"sequence\":" + sequence +
                ",\"reason\":" + D.Q(reason) + ",\"mutation_attempted\":" + D.B(attempted) +
                ",\"gui_invocation_attempted\":" + D.B(GuiInvocationAttempted) +
                ",\"gui_not_invoked_sequence\":" + notInvokedSequence +
                ",\"task_confirmed\":" + D.B(attempted && current > baseline) +
                ",\"cleanup_pending\":" + D.B(hold) + ",\"busy\":" + D.B(Busy) +
                ",\"home_ready\":" + D.B(Home()) + ",\"reward_pending\":" + D.B(RewardPending()) +
                ",\"reward_closed\":" + D.B(rewardClosed) +
                ",\"friend_id\":" + D.Q(friendId.ToString(CultureInfo.InvariantCulture)) + ",\"target_id\":" + targetId +
                ",\"completed_targets\":" + completedTargets + ",\"round_active\":" + D.B(roundActive) +
                ",\"settled_receipt_success\":" + D.B(demonRequest != null && demonRequest.Receipt != null && demonRequest.Receipt.Result == 0) +
                ",\"stock_callback_returned\":" + D.B(demonRequest != null && demonRequest.Returned) +
                ",\"receipt_report_verified\":" + D.B(demonRequest != null && demonRequest.ReportVerified) +
                ",\"gold_cost\":0}";
        }

        // One stock 0x492 callback, captured after the normal formation action
        // registers it. Detached parsing never consumes the native stream and
        // no report is installed into the real interpreter by this observer.
        private sealed class DemonBattleRequest
        {
            private const ushort Opcode = 1170;
            private readonly DailyMonsterModel owner;
            private readonly object character, target, client, beforeReport, loader;
            private readonly Component source, root;
            private readonly DailyTransport transport;
            private readonly string operationId;
            private readonly int sequence, taskCount, gold, bound;
            private readonly uint cycle, goldConsumed, clearTime;
            private readonly ulong roleId;
            private IDictionary handlers;
            private object handler;
            private FieldInfo callback;
            private Delegate original, wrapper, close;
            private DevelopmentNormalCallback forward;
            private Component battle;
            private object report;
            private int calls;
            private volatile bool armed = true;
            internal readonly string TargetId;
            internal DemonReceipt Receipt;
            internal bool Returned, ReportVerified;
            internal string Error = "";

            internal DemonBattleRequest(DailyMonsterModel owner)
            {
                this.owner = owner; character = owner.character; target = owner.roundTarget;
                source = owner.roundSource; root = owner.roundRoot; beforeReport = owner.roundReportBaseline;
                operationId = owner.operationId; sequence = owner.sequence; cycle = owner.cycle; roleId = owner.roleId;
                taskCount = owner.roundBaseline; gold = Int(character, "gold"); bound = Int(character, "bind_gold");
                goldConsumed = UInt(character, "gold_consume");
                clearTime = UInt(target, "clear_time");
                client = DailyTransport.CurrentClient(character); transport = DailyTransport.Read(client);
                loader = D.Static(Model.FirstType("BaseAsset"), "lds");
                TargetId = roleId.ToString(CultureInfo.InvariantCulture) + ":" + UInt(target, "clear_time").ToString(CultureInfo.InvariantCulture) +
                    ":" + source.gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture);
                if (!Owned() || transport == null || Bool(target, "killed"))
                    throw new ProbeFailure("monster_demon_request_identity_changed");
            }

            internal void Attach()
            {
                if (!Owned() || !transport.StillCurrent()) throw new ProbeFailure("monster_demon_request_owner_changed");
                handlers = D.Read(client, "m_handlers") as IDictionary;
                handler = handlers == null ? null : handlers[Opcode];
                original = handler == null ? null : D.Read(handler, "handler") as Delegate;
                if (original == null || original.GetInvocationList().Length != 1 || !ReferenceEquals(original.Target, source) ||
                    original.Method.Name != "sc_demon_battle_0x492" || original.Method.DeclaringType != Model.GameType("DemonScript") ||
                    D.Read(handler, "arg") != null || Convert.ToSingle(D.Read(handler, "life"), CultureInfo.InvariantCulture) <= 0)
                    throw new ProbeFailure("monster_demon_callback_owner_changed");
                callback = handler.GetType().GetField("handler", D.Instance);
                if (callback == null || callback.FieldType != original.GetType()) throw new ProbeFailure("monster_demon_callback_signature");
                forward = (DevelopmentNormalCallback)Delegate.CreateDelegate(typeof(DevelopmentNormalCallback), original.Target, original.Method);
                wrapper = Delegate.CreateDelegate(original.GetType(), this, GetType().GetMethod("Dispatch", D.Instance));
                callback.SetValue(handler, wrapper);
            }

            private bool Owned()
            {
                return owner.taskId == 24 && owner.hold && owner.roundActive && owner.roundAttempted && !owner.roundCollect &&
                    owner.operationId == operationId && owner.cycle == cycle && owner.roleId == roleId &&
                    ReferenceEquals(owner.character, character) && ReferenceEquals(owner.roundOwner, character) &&
                    ReferenceEquals(owner.roundSource, source) && ReferenceEquals(owner.roundRoot, root) &&
                    ReferenceEquals(owner.roundTarget, target) && source != null && source.GetType() == Model.GameType("DemonScript") &&
                    ReferenceEquals(D.Static(Model.FirstType("Global"), "character_"), character) && ULong(character, "id") == roleId &&
                    ReferenceEquals(D.Read(character, "m_demon"), target) && ReferenceEquals(D.Read(source, "m_demon"), target) &&
                    ReferenceEquals(D.Read(target, "mesh"), source.gameObject) && UInt(target, "clear_time") == clearTime &&
                    ReferenceEquals(D.Read(source, "rmg"), root) &&
                    ReferenceEquals(D.Static(Model.FirstType("BaseAsset"), "lds"), loader) &&
                    ReferenceEquals(D.Read(loader, "currmg"), root) &&
                    !Bool(source, "m_bFriend") && ReferenceEquals(DailyTransport.CurrentClient(character), client);
            }

            private void Dispatch(MemoryStream stream, object arg)
            {
                if (Interlocked.Increment(ref calls) != 1) { Unknown("monster_demon_duplicate_callback"); return; }
                if (!armed) return;
                try
                {
                    if (!CallbackBoundaryCurrent(arg))
                        throw new ProbeFailure("monster_demon_callback_boundary_changed");
                    if (stream == null)
                    {
                        Unknown("monster_demon_reply_timeout"); return;
                    }
                    long position = stream.Position, length = stream.Length;
                    if (position != 0 || length == 0 || length > 4194304)
                        throw new ProbeFailure("monster_demon_reply_stream_invalid");
                    byte[] body = stream.ToArray();
                    // A successful envelope already settles own-demon state.
                    // Validate its real compressed report before giving the
                    // original callback any opportunity to kill/reward/update.
                    Receipt = DemonReceipt.Decode(body);
                    if (stream.Position != position || stream.Length != length || !SameBytes(body, stream.ToArray()))
                        throw new ProbeFailure("monster_demon_reply_stream_changed");
                    if (!CallbackBoundaryCurrent(arg))
                        throw new ProbeFailure("monster_demon_callback_boundary_changed");
                    // In the pinned stock callback only result=2 returns after
                    // restoring controls. Result=1 destroys the demon and falls
                    // through, and other values enter settlement without a
                    // recognized success. Neither is safe to forward.
                    if (Receipt.Result != 0 && Receipt.Result != 2)
                    { Unknown("monster_demon_server_result_unverified"); return; }
                    // The only call that applies server state is this exact
                    // stock delegate, and this wrapper invokes it at most once.
                    forward(stream, arg);
                    Returned = true;
                    if (Receipt.Result == 2) { Unknown("monster_demon_server_rejected_unverified"); return; }
                    if (!Owned()) throw new ProbeFailure("monster_demon_callback_owner_changed");
                    GameObject game = owner.model.BattleUiObject();
                    battle = game == null ? null : game.GetComponent(Model.GameType("BattleUI"));
                    object interpreter = battle == null ? null : D.Read(battle, "interpreter");
                    report = interpreter == null ? null : D.Read(interpreter, "m_report");
                    if (report == null || ReferenceEquals(report, beforeReport) || !BattleReplyPreflight.Matches(Receipt.DetachedReport, report))
                        throw new ProbeFailure("monster_demon_receipt_report_unverified");
                    close = D.Read(battle, "OnClose") as Delegate;
                    if (Receipt.Win ? close == null || !ReferenceEquals(close.Target, source) || close.Method.Name != "BonusOpen" ||
                        close.Method.DeclaringType != Model.GameType("DemonScript") : close != null)
                        throw new ProbeFailure("monster_demon_result_callback_changed");
                    if ((Convert.ToInt32(D.Read(report, "_result_status"), CultureInfo.InvariantCulture) == 1) != Receipt.Win)
                        throw new ProbeFailure("monster_demon_receipt_outcome_changed");
                    if (!AppliedStateCurrent()) throw new ProbeFailure("monster_demon_settlement_evidence_changed");
                    ReportVerified = true;
                    owner.roundLaunch.InitialResponseApplied(battle, report);
                }
                catch (Exception error)
                { Unknown(error is ProbeFailure ? error.Message : "monster_demon_callback_unverified"); }
            }

            private void Unknown(string detail)
            {
                Error = detail; armed = false;
                owner.status = "UNKNOWN"; owner.reason = detail;
            }

            private bool CallbackBoundaryCurrent(object arg)
            {
                uint now, day, observedCycle;
                return armed && (!owner.stopped || owner.normalStopDrain) && !owner.blocked && !owner.terminal &&
                    (owner.status != "UNKNOWN" || owner.normalStopDrain) &&
                    Owned() && owner.sequence == sequence && owner.phase == "battle" && !owner.battleReturned &&
                    owner.roundBaseline == taskCount && owner.roundLaunch != null && ReferenceEquals(owner.demonRequest, this) &&
                    transport.StillCurrent() && arg == null && D.Read(handler, "arg") == null &&
                    ReferenceEquals(D.Read(client, "m_handlers"), handlers) &&
                    (!handlers.Contains(Opcode) || ReferenceEquals(handlers[Opcode], handler)) &&
                    ReferenceEquals(callback.GetValue(handler), wrapper) &&
                    Int(character, "gold") == gold && Int(character, "bind_gold") == bound && UInt(character, "gold_consume") == goldConsumed &&
                    Int(((IDictionary)D.Read(character, "acts"))[(uint)24], "condition_param") >= taskCount &&
                    Int(((IDictionary)D.Read(character, "acts"))[(uint)24], "condition_param") <= taskCount + 1 && !Bool(target, "killed") &&
                    DailyRefreshGate.ReadClock(character, out now, out day, out observedCycle) == "" && observedCycle == cycle;
            }

            private bool AppliedStateCurrent()
            {
                if (owner.stopped && !owner.normalStopDrain || owner.blocked || owner.terminal ||
                    owner.status == "UNKNOWN" && !owner.normalStopDrain || owner.sequence != sequence ||
                    owner.roundLaunch == null || Receipt == null || Receipt.Result != 0 || !Owned() || !transport.StillCurrent() ||
                    !ReferenceEquals(owner.demonRequest, this) || owner.roundCollect || owner.battleReturned ||
                    owner.phase != "battle" || owner.roundBaseline != taskCount ||
                    Int(character, "gold") != gold || Int(character, "bind_gold") != bound || UInt(character, "gold_consume") != goldConsumed ||
                    Bool(target, "killed") != Receipt.Win || battle == null ||
                    !ReferenceEquals(D.Read(battle, "m_prevUI"), root) || D.Read(battle, "m_parentUI") != null ||
                    ULong(battle, "m_reportid") != 0 || Bool(battle, "bUseBattleEnd") || Bool(battle, "bNeedWaitForBattleEnd") ||
                    Bool(battle, "m_is_cross") || D.Read(battle, "m_viewType").ToString() != "viewReport" ||
                    !ReferenceEquals(D.Read(battle, "OnClose"), close)) return false;
                uint now, day, observedCycle;
                if (DailyRefreshGate.ReadClock(character, out now, out day, out observedCycle) != "" || observedCycle != cycle) return false;
                object cooldown = ((IDictionary)D.Read(character, "cooldowns"))[(byte)5];
                object interpreter = D.Read(battle, "interpreter");
                return cooldown != null && UInt(cooldown, "time") == Receipt.CooldownTime &&
                    UInt(cooldown, "id") == Receipt.CooldownId && UInt(cooldown, "sub_id") == Receipt.CooldownSubId &&
                    Bool(cooldown, "in_cd") == Receipt.CooldownActive &&
                    interpreter != null && ReferenceEquals(D.Read(interpreter, "m_report"), report) &&
                    BattleReplyPreflight.Matches(Receipt.DetachedReport, report);
            }

            internal bool Verify()
            {
                return Error == "" && Returned && ReportVerified && ReferenceEquals(owner.battle, battle) && AppliedStateCurrent();
            }

            internal void Restore()
            {
                // A pending 0x492 can still arrive after stop/UNKNOWN. Never
                // reinstall the native delegate where that late reply could
                // bypass preflight. The client's one-shot handler/timeout owns
                // removal; this inert wrapper drains it without applying state.
                armed = false;
            }
        }

        private sealed class DemonReceipt
        {
            internal uint Result, CooldownTime, CooldownId, CooldownSubId;
            internal bool Win, CooldownActive;
            internal byte[] Report;
            internal object DetachedReport;
            internal static DemonReceipt Decode(byte[] body)
            {
                // This schema has no role/demon/clear_time echo. Correlation is
                // the captured 0x492 handler and exact local owner/mesh only.
                object packet = BattleReplyPreflight.Deserialize(body, "game.net.data.StoC.SCDemonBattle_0x492");
                DemonReceipt receipt = new DemonReceipt { Result = UInt(packet, "_result"), Win = Bool(packet, "_iswin"),
                    Report = D.Read(packet, "_report") as byte[] };
                object cooldown = D.Read(packet, "_cooldown");
                if (receipt.Result == 0)
                {
                    if (receipt.Report == null || receipt.Report.Length == 0 || cooldown == null ||
                        receipt.Win && D.Read(packet, "_bonuses") == null) throw new FormatException();
                    receipt.CooldownTime = UInt(cooldown, "_time");
                    receipt.CooldownId = UInt(cooldown, "_id"); receipt.CooldownSubId = UInt(cooldown, "_sub_id");
                    receipt.CooldownActive = Bool(cooldown, "_in_cd");
                    if (receipt.CooldownId != 5) throw new FormatException();
                    receipt.DetachedReport = BattleReplyPreflight.Validate(receipt.Report, receipt.Win);
                }
                else if (receipt.Result == 2 && receipt.Report != null)
                {
                    // The supported rejection can omit settlement data. If a
                    // report is supplied, it must still be internally valid;
                    // the safe early return is no reason to trust bad bytes.
                    receipt.DetachedReport = BattleReplyPreflight.Validate(receipt.Report, receipt.Win);
                }
                return receipt;
            }
        }

        private static bool SameBytes(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
            return true;
        }

        private static bool Supported(int task) { return task == 10 || task == 15 || task == 24; }
        private static int Int(object value, string name) { return D.Int(D.Read(value, name)); }
        private static uint UInt(object value, string name) { return D.UInt(D.Read(value, name)); }
        private static ulong ULong(object value, string name) { return Convert.ToUInt64(D.Read(value, name), CultureInfo.InvariantCulture); }
        private static bool Bool(object value, string name) { return D.Bool(D.Read(value, name)); }
    }
}
