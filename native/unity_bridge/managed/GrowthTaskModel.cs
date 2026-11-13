using System;
using System.Collections;
using System.Text;
using UnityEngine;
using D = JiangShen.AssistantRuntime.SharedDevelopmentContext;

namespace JiangShen.AssistantRuntime
{
    internal sealed class GrowthTaskModel
    {
        private sealed class Quest
        {
            internal object Data, Template;
            internal int Id, Finished, Got, Type, Param0, Param1, Exp, Money;
            internal string Name, Description, Dispatch, Reason;
            internal bool? Condition;
            internal string Json()
            {
                return "{\"quest_id\":" + Id + ",\"isfinish\":" + Finished + ",\"isgot\":" + Got + ",\"template\":" + (Template == null ? "null" : "{\"quest_id\":" + Id + ",\"type\":" + Type + ",\"param0\":" + Param0 + ",\"param1\":" + Param1 + ",\"exp\":" + Exp + ",\"money\":" + Money + ",\"name\":" + D.Q(Name) + ",\"desc\":" + D.Q(Description) + "}") +
                    ",\"condition_met\":" + (Condition.HasValue ? D.B(Condition.Value) : "null") + ",\"dispatch\":" + D.Q(Dispatch) + ",\"action_reason\":" + D.Q(Reason) + "}";
            }
        }
        private sealed class RewardWait
        {
            internal string Phase = "NONE", Reason = "", ClaimOperationId = "";
            internal bool ClaimConfirmed, Bound;
            internal int QuestId, ClaimSequence, DialogInstanceId;
            internal bool? Loading, Collecting, ShowVisiblePending;
            internal int? AssetNamesCount;
            internal string Json()
            {
                return "{\"phase\":" + D.Q(Phase) + ",\"reason\":" + D.Q(Reason) + ",\"claim_confirmed\":" + D.B(ClaimConfirmed) +
                    ",\"bound\":" + D.B(Bound) + ",\"quest_id\":" + QuestId + ",\"claim_operation_id\":" + D.Q(ClaimOperationId) +
                    ",\"claim_sequence\":" + ClaimSequence + ",\"dialog_instance_id\":" + DialogInstanceId +
                    ",\"loading\":" + (Loading.HasValue ? D.B(Loading.Value) : "null") +
                    ",\"collecting\":" + (Collecting.HasValue ? D.B(Collecting.Value) : "null") +
                    ",\"asset_names_count\":" + (AssetNamesCount.HasValue ? AssetNamesCount.Value.ToString() : "null") +
                    ",\"show_visible_pending\":" + (ShowVisiblePending.HasValue ? D.B(ShowVisiblePending.Value) : "null") + "}";
            }
        }
        private sealed class State
        {
            internal Component Ui, Reward, Recruit, Soul, Equipment, Reputation, Special, Dialog, Formation;
            internal IList List;
            internal readonly ArrayList Quests = new ArrayList();
            internal int Selected, UiSelected;
            internal bool ShowWindow, PromptActive, RewardConfirm, RequirementBlocked;
            internal int RewardQuest;
            internal readonly RewardWait RewardLoading = new RewardWait();
            internal string Gate = "PASS", Reason = "", Step = "NONE", Action = "", Level;
            internal string Requirement = "null", RequirementAction = "";
            internal object RecruitCandidate;
            internal object WeaponCandidate, WeaponOwner, WeaponFormation;
            internal int WeaponId;
            internal Quest Find(int id) { foreach (Quest q in Quests) if (q.Id == id) return q; return null; }
            internal string Json(DevelopmentOperation operation)
            {
                StringBuilder list = new StringBuilder("["); foreach (Quest q in Quests) { if (list.Length > 1) list.Append(','); list.Append(q.Json()); } list.Append(']');
                return "{\"schema\":1,\"gate\":" + D.Q(D.GateKind(Gate)) + ",\"reason\":" + D.Q(D.GateReason(Gate, Reason)) + ",\"selected_quest_id\":" + Selected + ",\"ui_selected_quest_id\":" + UiSelected + ",\"window_visible\":" + D.B(D.Visible(Ui)) + ",\"window_enabled\":" + D.B(D.Enabled(Ui)) + ",\"show_window\":" + D.B(ShowWindow) + ",\"prompt_active\":" + D.B(PromptActive) + ",\"reward_dialog\":" + D.UiJson(Reward) + ",\"reward_quest_id\":" + RewardQuest + ",\"reward_wait\":" + RewardLoading.Json() + ",\"requirement\":" + Requirement + ",\"step\":" + D.Q(Step) + ",\"tasks\":" + list + ",\"available_action\":" + D.Q(Action) + ",\"operation\":" + operation.Json() + "}";
            }
            internal string Stamp() { return Json(new DevelopmentOperation()) + ":" + Level; }
        }
        private SharedDevelopmentContext context;
        private State state = new State();
        private readonly DevelopmentSession session = new DevelopmentSession();
        private DevelopmentOperation operation = new DevelopmentOperation();
        private bool opening;
        private State openingBefore;
        private SharedDevelopmentContext openingContext;
        private SharedDevelopmentContext claimContext, rewardContext;
        private int claimedQuest;
        private Quest claimedTarget;
        private IList claimedList;
        private DevelopmentCommand claimedGoal;
        private Component claimedUi, claimedReward;
        private object claimedRewardOwner;
        private Delegate claimedRewardClose;
        private bool claimedReceiptConfirmed;
        private DevelopmentCommand rewardQueued;
        private Component queuedReward;
        private DevelopmentCommand observedClaimGoal;
        private SharedDevelopmentContext observedClaimContext;
        private Quest observedClaimQuest;
        private IList observedClaimList;
        private Quest flowQuest;
        private SharedDevelopmentContext flowContext;
        private int flowStage, flowArtifact, flowUsed;
        private long flowMoney, flowGold;
        private long flowReputation;
        private bool grantRequested, grantViewed;
        private Component grantPopup;
        private object grantTemplate;
        private string grantReceipt = "null";
        private int recruitRepair, recruitBlue, recruitPurple, recruitOrange, recruitRed;
        private object recruitInfo;
        private Component recruitPopup, recruitDialog;
        private GeneralTutorialModel recruitTutorial;
        private string recruitReceipt = "null", formationReceipt = "null";
        private readonly Hashtable recruitOriginal = new Hashtable();
        private string flowError = "";
        private string flowOperationId = "";
        private DevelopmentOperation finishOperation;
        private bool finishArmed;
        private string startReceipt = "null", growReceipt = "null", harvestReceipt = "null";
        private Quest weaponQuest;
        private SharedDevelopmentContext weaponContext;
        private object weaponEquipment, weaponOwner, weaponFormation;
        private IList weaponList;
        private int weaponId, weaponStage;
        private long weaponMoney, weaponStone, weaponGold;
        private string weaponOperationId = "", weaponReceipt = "null";
        internal bool Pending { get { return operation.Pending || finishOperation != null && finishOperation.Pending && (finishArmed || finishOperation.Status == "UNKNOWN"); } }
        internal bool MutationAttempted { get { return operation.MutationAttempted; } }
        internal string OperationJson() { return operation.Json(); }
        internal bool Allows(Component ui) { return state != null && ui == state.Ui; }
        internal bool RewardReady { get { return state != null && state.RewardConfirm; } }
        internal void ObserveTarget(DevelopmentCommand command)
        {
            // A finished preparation stays visible to its policy until a fresh
            // growth transaction explicitly takes over. Its receipt remains in
            // the caller's ledger; later upgrades have separate budgets.
            if (command != null && command.Action == "growth_observe" && weaponQuest != null &&
                command.OperationId != weaponOperationId && weaponStage == 2 && !operation.Pending &&
                operation.Status == "OBSERVED" && operation.Action == "growth_weapon_close" &&
                context != null && !D.Visible(context.Find("EquipmentUI")))
            {
                weaponQuest = null; weaponContext = null; weaponEquipment = weaponOwner = weaponFormation = null;
                weaponList = null; weaponId = weaponStage = 0; weaponOperationId = ""; weaponReceipt = "null";
            }
            observedClaimGoal = null; observedClaimContext = null; observedClaimQuest = null; observedClaimList = null;
            if (context == null || command == null || command.Action != "growth_observe" || command.DesiredLevel != 0 || command.Mode != "" ||
                command.MaxMoney != 0 || command.MaxStone != 0 || command.MaxFood != 0 || command.MaxRequests != 10) return;
            // TargetId comes from the observed selection or an explicit claim
            // goal. ReadState already rejects duplicate quest IDs; other
            // completed rows do not make this exact data/template ambiguous.
            State current = ReadState(context); Quest q = current.Find(command.TargetId);
            if (q == null || q.Template == null || q.Finished != 1 || q.Got != 0) return;
            observedClaimGoal = command.Copy(); observedClaimContext = context; observedClaimQuest = q; observedClaimList = current.List;
        }
        internal void Capture(SharedDevelopmentContext ctx)
        {
            context = ctx; if (opening) ObserveOpening(ctx); else operation.Observe(ctx);
            ObserveRequirement(ctx); state = ReadState(ctx);
            if (operation.Status == "OBSERVED" && operation.Action == "growth_claim" && operation.Server == "PASS" &&
                !ClaimedTargetMatches(ctx, state)) operation.Unknown("growth_claim_target_or_postcondition_changed");
            if (operation.Pending || operation.Status == "ERROR") { state.Gate = operation.Status; state.Reason = operation.Reason; state.Action = ""; }
        }
        private void ObserveOpening(SharedDevelopmentContext ctx)
        {
            if (operation.Status != "WAITING") { opening = false; return; }
            if (!ctx.SameOwnership(openingContext)) { operation.Unknown("growth_open_identity_changed"); opening = false; return; }
            DevelopmentNetwork n = ctx.Network; if (!n.Observed) return;
            if (n.Error != null) { operation.Unknown(n.Error); opening = false; return; }
            IList finish = D.Read(openingBefore.Ui, "m_listFinish") as IList;
            if (finish == null || finish.Count > 128 || !object.ReferenceEquals(D.Read(ctx.Character, "newbiequests"), openingBefore.List))
            { operation.Unknown("growth_open_finish_list_or_tasks_changed"); opening = false; return; }
            if (n.Handlers.Contains((ushort)810))
            {
                // Opening has proved no other quest can auto-complete. A
                // deferred request is a race, never a positive receipt witness.
                operation.Unknown("growth_open_unexpected_completion_request"); opening = false; return;
            }
            foreach (DevelopmentPacket p in n.Packets) if (p.Opcode == 810) { operation.Unknown("growth_open_finish_packet_unbound"); opening = false; return; }
            if (D.Loading(openingBefore.Ui) || !D.Visible(openingBefore.Ui) || !D.Enabled(openingBefore.Ui)) return;
            opening = false;
            if (finish.Count != 0) operation.Unknown("growth_open_completion_response_missed");
            else operation.UiObservedWithoutRequest();
        }
        private State ReadState(SharedDevelopmentContext ctx)
        {
            State s = new State(); s.Ui = ctx.Find("NewbieQuestUI"); s.Gate = ctx.Panels(s.Ui); s.Level = D.Read(ctx.Root, "LevelID") as string;
            s.Reward = ctx.GrowthRewardDialog();
            s.List = D.Read(ctx.Character, "newbiequests") as IList;
            if (s.List == null || s.List.Count > 128) throw new ProbeFailure("growth_task_collection_schema_or_bound");
            Hashtable ids = new Hashtable();
            foreach (object data in s.List)
            {
                Quest q = new Quest(); q.Data = data; q.Id = D.Int(D.Read(data, "idnewbie_quest")); q.Finished = D.Int(D.Read(data, "isfinish")); q.Got = D.Int(D.Read(data, "isgot"));
                if (q.Id < 1 || q.Id > 32767 || ids.Contains(q.Id)) throw new ProbeFailure("growth_task_identity_or_duplicate"); ids[q.Id] = true;
                q.Template = D.Template("newbie_quest", (uint)q.Id);
                if (q.Template == null) { q.Dispatch = "UNSUPPORTED"; q.Reason = "growth_template_unavailable"; s.Quests.Add(q); continue; }
                if (D.Int(D.Read(q.Template, "idnewbie_quest")) != q.Id) throw new ProbeFailure("growth_template_identity");
                q.Type = D.Int(D.Read(q.Template, "newbie_quest_type")); q.Param0 = D.Int(D.Read(q.Template, "param0")); q.Param1 = D.Int(D.Read(q.Template, "param1")); q.Exp = D.Int(D.Read(q.Template, "exp")); q.Money = D.Int(D.Read(q.Template, "money"));
                q.Name = D.Read(q.Template, "name") as string; q.Description = D.Read(q.Template, "desc") as string;
                Describe(ctx, q); s.Quests.Add(q);
            }
            // These two passes are intentionally different directions, matching
            // MainUI.DrawNewbieQuest. Never infer the next quest as ID + 1.
            for (int i = s.Quests.Count - 1; i >= 0; i--) { Quest q = (Quest)s.Quests[i]; if (q.Finished != 1) { s.Selected = q.Id; break; } }
            if (s.Selected == 0) foreach (Quest q in s.Quests) if (q.Got != 1) { s.Selected = q.Id; break; }
            s.UiSelected = D.Int(D.Read(s.Ui, "m_questSelected")); s.ShowWindow = D.Bool(D.Read(s.Ui, "m_bShowWnd"));
            DescribeRequirement(ctx, s);
            DescribeWeaponPreparation(ctx, s);
            DescribeReward(ctx, s);
            foreach (UnityEngine.Object item in ctx.All)
            {
                Component main = item as Component;
                if (main != null && main.GetType() == Model.GameType("MainUI") && D.Visible(main) &&
                    object.ReferenceEquals(D.Read(main, "rmg"), ctx.Root))
                    s.PromptActive = D.Bool(D.Static(Model.GameType("MainUI"), "isShowNewbieQuest"));
            }
            if (s.RewardConfirm) { s.Gate = "PASS"; s.Step = "REWARD"; s.Action = "growth_reward_confirm"; return s; }
            if (s.RewardLoading.ClaimConfirmed)
            {
                s.Gate = s.RewardLoading.Phase == "LOADING" ? "WAITING_UI" : "BLOCKED";
                s.Reason = s.RewardLoading.Reason; s.Step = "REWARD"; return s;
            }
            if (CanOpenObservedClaim(ctx, s, s.Find(observedClaimGoal == null ? 0 : observedClaimGoal.TargetId)))
            {
                // This action belongs to the explicit observed claim goal.
                // Selected/UiSelected remain the real game's selections.
                s.Gate = "PASS"; s.Step = "CLAIM"; s.Action = "growth_open"; return s;
            }
            if (s.Gate != "PASS") { if (s.Reason == "") s.Reason = s.Gate; return s; }
            if (D.Loading(s.Ui)) { s.Gate = "WAITING_UI"; s.Reason = "growth_normal_ui_loading"; return s; }
            if (!ctx.Network.Idle) { s.Gate = "WAITING_NETWORK"; s.Reason = "growth_network_not_idle"; return s; }
            if (s.RequirementBlocked) { s.Step = "REQUIREMENT"; return s; }
            if (s.RequirementAction != "") { s.Step = "REQUIREMENT"; s.Action = s.RequirementAction; return s; }
            Quest selected = s.Find(D.Visible(s.Ui) ? s.UiSelected : s.Selected);
            if (selected == null) { s.Reason = s.Selected == 0 ? "no_unfinished_or_unclaimed_task" : "growth_ui_selected_task_missing"; return s; }
            if (selected.Template == null || (selected.Finished != 0 && selected.Finished != 1) || (selected.Got != 0 && selected.Got != 1)) { s.Gate = "UNKNOWN"; s.Reason = "growth_task_schema_or_state_unknown"; return s; }
            if (selected.Got == 1) { s.Step = "CLAIMED_LOCAL"; s.Reason = "local_claim_flag_is_not_server_receipt"; return s; }
            if (D.Visible(s.Ui) && (!D.Enabled(s.Ui) || D.Loading(s.Ui))) { s.Gate = "WAITING_UI"; s.Reason = "growth_ui_not_interactive"; return s; }
            if (selected.Finished == 1)
            {
                s.Step = "CLAIM";
                if (D.Visible(s.Ui) && s.ShowWindow && (selected.Exp > 0 || selected.Money > 0)) s.Action = "growth_claim";
                else if (!D.Visible(s.Ui) && ctx.SubUi == null && AllFinished(s)) s.Action = "growth_open";
                else s.Reason = "growth_claim_button_not_available";
            }
            else if (selected.Condition == true) { s.Step = "CHECK"; s.Action = "growth_check"; }
            else { s.Step = "REQUIREMENT"; s.Reason = selected.Reason; }
            return s;
        }
        private static bool OnlyBoundAutoCheck(State s, Quest bound)
        {
            foreach (Quest q in s.Quests) if (q.Id != bound.Id && q.Finished != 1 &&
                (q.Condition == true || !q.Condition.HasValue && q.Type != 5 && q.Type != 10 && q.Type != 13 && q.Type != 14)) return false;
            return true;
        }
        private static bool WeaponAction(string action)
        { return action == "growth_weapon_open" || action == "growth_weapon_wear" || action == "growth_weapon_close"; }
        private void DescribeWeaponPreparation(SharedDevelopmentContext ctx, State s)
        {
            Quest q = s.Find(weaponQuest == null ? (D.Visible(s.Ui) ? s.UiSelected : s.Selected) : weaponQuest.Id);
            if (q == null || q.Id != 3 || q.Type != 3 || q.Param0 != 5 || q.Param1 != 0 || q.Got != 0 ||
                weaponQuest == null && (q.Finished != 0 || q.Condition == true)) return;
            s.Equipment = ctx.Find("EquipmentUI");
            IDictionary generals = Dictionary(ctx.Character, "form_generals_"), equipments = Dictionary(ctx.Character, "equipments");
            IDictionary forms = Dictionary(ctx.Character, "formations_"); int formId = D.Int(D.Read(ctx.Character, "cur_form"));
            object owner = generals.Contains((ushort)0) ? generals[(ushort)0] : null;
            object formation = forms.Contains(formId) ? forms[formId] : null;
            IList slots = owner == null ? null : D.Read(owner, "equipments") as IList;
            IList spots = formation == null ? null : D.Read(formation, "spotgen") as IList;
            bool inForm = false; if (spots != null && spots.Count <= 8) foreach (object spot in spots) if (D.Int(spot) == 0) inForm = true;
            bool empty = slots != null && slots.Count == 2 && slots[0] == null;
            // Existing equipped targets continue through the original upgrade
            // policy. This descriptor never replaces or transfers equipment.
            if (weaponQuest == null && !empty && owner != null) return;
            string phase = "READY", why = ""; int id = 0; object equipment = null;
            long money = D.Money(ctx.Character), stone = D.Long(D.Read(ctx.Character, "enchant_stone")), gold = D.Long(D.Read(ctx.Character, "gold"));
            s.Gate = ctx.Panels(s.Ui, s.Equipment);
            if (flowQuest != null || !Feature(ctx, "general_person")) why = "growth_weapon_equipment_entry_unavailable";
            else if (s.Level != "Campaign" || D.Visible(s.Ui) || s.Selected != 3 || q.Finished != 0) why = "growth_weapon_current_quest_or_map_changed";
            else if (owner == null || D.Int(D.Read(owner, "idgeneral_template")) != 0 || slots == null || slots.Count != 2 || !inForm) why = "growth_weapon_protagonist_formation_unavailable";
            else if (weaponQuest != null && (!ctx.SameOwnership(weaponContext) || !object.ReferenceEquals(s.List, weaponList) ||
                !object.ReferenceEquals(q.Data, weaponQuest.Data) || !object.ReferenceEquals(q.Template, weaponQuest.Template) ||
                !object.ReferenceEquals(owner, weaponOwner) || !object.ReferenceEquals(formation, weaponFormation))) why = "growth_weapon_identity_changed";
            else if (weaponQuest != null && (money != weaponMoney || stone != weaponStone || gold != weaponGold)) why = "growth_weapon_free_action_resources_changed";
            else
            {
                id = weaponQuest == null ? D.Int(D.Method(s.Equipment.GetType(), "CheckBestItem", new Type[] { typeof(byte) }, false, typeof(byte)).Invoke(s.Equipment, new object[] { (byte)1 })) : weaponId;
                if (id < 1 || id > 255 || !equipments.Contains((byte)id)) why = "growth_weapon_owned_inventory_candidate_unavailable";
                else
                {
                    equipment = equipments[(byte)id]; object main = D.Read(equipment, "main_");
                    if (D.Int(D.Read(equipment, "id")) != id || D.Int(D.Read(main, "idequipment_type")) != 1 ||
                        weaponQuest != null && !object.ReferenceEquals(equipment, weaponEquipment)) why = "growth_weapon_candidate_identity_changed";
                    else if (weaponStage == 1)
                    {
                        if (operation.Pending) phase = "WAITING";
                        else if (operation.Status != "OBSERVED" || operation.Action != "growth_weapon_wear" || operation.OperationId != weaponOperationId ||
                            operation.Receipt == null || operation.Receipt.Opcode != 856 || operation.Receipt.Result != 0 || operation.Receipt.Id != id || operation.Server != "PASS" ||
                            !object.ReferenceEquals(slots[0], equipment) || D.Int(D.Read(equipment, "idgeneral_template")) != 0) why = "growth_weapon_receipt_or_owner_slot_mismatch";
                        else { weaponReceipt = operation.Json(); weaponStage = 2; }
                    }
                    else if (weaponStage == 0 && (!empty || D.Int(D.Read(equipment, "idgeneral_template")) != 65535)) why = "growth_weapon_empty_slot_or_inventory_changed";
                    if (why == "" && phase != "WAITING")
                    {
                        if (weaponStage == 2 && (!object.ReferenceEquals(slots[0], equipment) || D.Int(D.Read(equipment, "idgeneral_template")) != 0 || weaponReceipt == "null")) why = "growth_weapon_postcondition_changed";
                        else if (D.Loading(s.Equipment)) phase = "WAITING";
                        else if (weaponStage == 2 && !D.Visible(s.Equipment) && ctx.SubUi == null) phase = "DONE";
                        else if (!D.Visible(s.Equipment))
                        {
                            if (weaponStage != 0 || ctx.SubUi != null) why = "growth_weapon_entry_subui_not_owned";
                            else s.RequirementAction = "growth_weapon_open";
                        }
                        else if (!D.Enabled(s.Equipment)) phase = "WAITING";
                        else if (!object.ReferenceEquals(ctx.SubUi, s.Equipment) || D.Read(s.Equipment, "OnClose") != null ||
                            D.Bool(D.Read(s.Equipment, "sended")) || D.Int(D.Read(s.Equipment, "m_DragState")) != 0) why = "growth_weapon_equipment_ui_not_owned";
                        else s.RequirementAction = weaponStage == 2 ? "growth_weapon_close" : "growth_weapon_wear";
                    }
                }
            }
            bool unowned = equipment != null && D.Int(D.Read(equipment, "idgeneral_template")) == 65535;
            bool matches = equipment != null && slots != null && slots.Count == 2 && object.ReferenceEquals(slots[0], equipment) && D.Int(D.Read(equipment, "idgeneral_template")) == 0;
            s.WeaponCandidate = equipment; s.WeaponOwner = owner; s.WeaponFormation = formation; s.WeaponId = id;
            if (why != "")
            {
                if (weaponQuest != null) s.Gate = "BLOCKED";
                s.Reason = why; s.RequirementAction = ""; s.RequirementBlocked = true; phase = "BLOCKED";
            }
            else if (phase == "WAITING") { s.Gate = "WAITING_UI"; s.Reason = "growth_weapon_normal_ui_or_receipt_pending"; }
            s.Requirement = "{\"quest_id\":3,\"type\":3,\"param0\":5,\"param1\":0,\"phase\":" + D.Q(phase) + ",\"stage\":" + weaponStage +
                ",\"equipment_id\":" + id + ",\"owner_id\":0,\"slot\":0,\"in_current_formation\":" + D.B(inForm) + ",\"slot_empty\":" + D.B(empty) +
                ",\"candidate_unowned\":" + D.B(unowned) + ",\"owner_slot_matches\":" + D.B(matches) + ",\"available_money\":" + money + ",\"available_stone\":" + stone +
                ",\"gold\":" + gold + ",\"wear_receipt\":" + weaponReceipt + ",\"ui\":{\"equipment\":" + D.UiJson(s.Equipment) + "}}";
        }
        private bool ClaimGoalMatches(SharedDevelopmentContext ctx, State s, Quest q)
        {
            return observedClaimGoal != null && observedClaimQuest != null && q != null && q.Template != null &&
                q.Id == observedClaimGoal.TargetId && q.Finished == 1 && q.Got == 0 && ctx.SameOwnership(observedClaimContext) &&
                object.ReferenceEquals(s.List, observedClaimList) && object.ReferenceEquals(q.Data, observedClaimQuest.Data) &&
                object.ReferenceEquals(q.Template, observedClaimQuest.Template) && q.Type == observedClaimQuest.Type &&
                q.Param0 == observedClaimQuest.Param0 && q.Param1 == observedClaimQuest.Param1;
        }
        private bool CanOpenObservedClaim(SharedDevelopmentContext ctx, State s, Quest q)
        {
            bool completedRecruit = flowQuest != null && q != null && flowQuest.Id == q.Id && flowQuest.Type == 6 && flowStage == 4 &&
                recruitTutorial != null && recruitTutorial.Status == "COMPLETE" && finishOperation != null && finishOperation.Status == "OBSERVED" && finishOperation.Server == "PASS";
            return (flowQuest == null && finishOperation == null || completedRecruit) && !operation.Pending && operation.Status != "ERROR" &&
                ClaimGoalMatches(ctx, s, q) && !D.Visible(s.Ui) && !D.Loading(s.Ui) && ctx.SubUi == null &&
                ctx.Panels(s.Ui) == "PASS" && ctx.Network.Idle && OnlyBoundAutoCheck(s, q);
        }
        private bool FlowIdentity(SharedDevelopmentContext ctx, State s, Quest q)
        {
            return flowQuest == null || ctx.SameOwnership(flowContext) && q != null &&
                q.Id == flowQuest.Id && q.Type == flowQuest.Type && q.Param0 == flowQuest.Param0 && q.Param1 == flowQuest.Param1 &&
                object.ReferenceEquals(q.Data, flowQuest.Data) && object.ReferenceEquals(q.Template, flowQuest.Template);
        }
        private static bool Feature(SharedDevelopmentContext ctx, string name)
        { return (bool)D.Method(ctx.Character.GetType(), "checkUI", new Type[] { typeof(string) }, false, typeof(bool)).Invoke(ctx.Character, new object[] { name }); }
        private static bool RequirementUnavailable(string why)
        {
            return why == "artifact_equipment_feature_locked" || why == "artifact_reputation_feature_locked" || why == "artifact_owned_candidate_unavailable" ||
                why == "artifact_initial_rank_grant_unavailable" || why == "artifact_initial_reputation_insufficient" ||
                why == "recruit71_initial_party_or_slot_unavailable" || why == "recruit71_blue_budget_insufficient" || why == "recruit71_candidate_unavailable" ||
                why == "recruit71_campaign_required" ||
                why == "soul_recruit_or_gather_feature_locked" || why == "soul_free_single_blue_unavailable" || why == "soul_growth_coin_budget_insufficient" ||
                why == "soul_single_blue_3000_coin_mode_unavailable" || why == "soul_exactly_one_unfinished_quest_required" ||
                why == "soul_existing_gather_session_not_owned" || why == "growth_guide_would_check_other_quest";
        }
        private static bool ExistingSouls(SharedDevelopmentContext ctx)
        {
            object souls = D.Read(ctx.Character, "gather_souls");
            for (int i = 0; i < 8; i++) if (D.Int(D.Read(souls, "soul" + i + "_color")) > 0 && D.Int(D.Read(souls, "soul" + i + "_level")) > 0) return true;
            return false;
        }
        private void DescribeRequirement(SharedDevelopmentContext ctx, State s)
        {
            Quest q = s.Find(flowQuest == null ? (D.Visible(s.Ui) ? s.UiSelected : s.Selected) : flowQuest.Id);
            if (q == null || q.Template == null || q.Type != 4 && q.Type != 5 && q.Type != 6 || q.Got == 1) return;
            if (flowQuest == null && (q.Finished == 1 || q.Condition == true)) return;
            s.Recruit = ctx.Find("GeneralRecruitUI"); s.Soul = ctx.Find("GeneralSoulGatherUI"); s.Equipment = ctx.Find("EquipmentUI");
            s.Reputation = ctx.Find("ReputationUI"); s.Special = ctx.Find("SpecialGetUI");
            if (q.Type == 6) { s.Dialog = ctx.Find("MessageBox"); s.Formation = ctx.Find("FormationUI"); }
            s.Gate = q.Type == 6 ? ctx.Panels(s.Ui, s.Recruit, s.Special, s.Dialog, s.Formation) : q.Type == 5 ? ctx.Panels(s.Ui, s.Recruit, s.Soul) : ctx.Panels(s.Ui, s.Equipment, s.Reputation, s.Special);
            string phase = "READY", why = ""; long money = D.Money(ctx.Character), gold = D.Long(D.Read(ctx.Character, "gold"));
            int used = D.Int(D.Read(ctx.Character, "generalsoul_blue_times")), free = 0, rate = 0, cost = 0, mode = 0;
            // The recorded route is recruit -> its always-present gather
            // button; the separate MainUI resource_soul icon has another gate.
            bool feature = q.Type == 5 || q.Type == 6 ? Feature(ctx, "army_recruit") : Feature(ctx, "general_person");
            bool reputationUnlocked = Feature(ctx, "reputation");
            if (!FlowIdentity(ctx, s, q)) why = "growth_requirement_identity_changed";
            else if (flowError != "") why = flowError;
            else if (!feature) why = q.Type == 5 || q.Type == 6 ? "soul_recruit_or_gather_feature_locked" : "artifact_equipment_feature_locked";
            else if (q.Type == 6) DescribeRecruit(ctx, s, q, ref phase, ref why);
            else if (q.Type == 5)
            {
                object start = D.Template("general_soul_start_mode", 1), grow = D.Template("general_soul_grow_mode", 1);
                if (start == null || grow == null) why = "soul_templates_unavailable";
                else
                {
                    free = D.Int(D.Read(start, "free_times")); rate = D.Int(D.Read(start, "gather_rate")); cost = D.Int(D.Read(grow, "cost"));
                    mode = D.Int(D.Read(s.Soul, "m_curType"));
                    int unfinished = 0; foreach (Quest row in s.Quests) if (row.Type == 5 && row.Finished != 1) unfinished++;
                    if (flowStage == 0 && unfinished != 1) why = "soul_exactly_one_unfinished_quest_required";
                    else if (rate != 1 || D.Int(D.Read(start, "type")) != 1 || D.Bool(D.Read(grow, "isgold")) || cost != 3000 ||
                        (gold >= 0 ? D.Int(D.Read(ctx.Character, "vip_level")) : 0) < D.Int(D.Read(grow, "vip_require"))) why = "soul_single_blue_3000_coin_mode_unavailable";
                    else if (flowStage == 0 && used >= free) why = "soul_free_single_blue_unavailable";
                    else if (flowStage == 0 && money < cost) why = "soul_growth_coin_budget_insufficient";
                    else if (flowStage == 0 && (ExistingSouls(ctx) || D.Visible(s.Soul) &&
                        (D.Int(D.Read(s.Soul, "currentGatherSoulStatus")) != 0 || D.Bool(D.Read(s.Soul, "gathered"))))) why = "soul_existing_gather_session_not_owned";
                    else if (q.Finished == 1)
                    {
                        if (finishOperation == null || finishOperation.Status != "OBSERVED" || finishOperation.Server != "PASS") why = "soul_finish_receipt_unverified";
                        else if (money != flowMoney - 3000 || gold != flowGold || used != flowUsed + 1 || startReceipt == "null" || growReceipt == "null") why = "soul_completed_budget_or_receipts_changed";
                        else if (D.Visible(s.Soul) || D.Visible(s.Recruit)) s.RequirementAction = "growth_requirement_close";
                        else phase = "DONE";
                    }
                    else if (!D.Visible(s.Ui) || s.ShowWindow)
                    {
                        if (flowStage != 0) phase = "WAITING";
                        else if (!OnlyBoundAutoCheck(s, q)) why = "growth_guide_would_check_other_quest";
                        else s.RequirementAction = "growth_guide_open";
                    }
                    else if (D.Loading(s.Recruit) || D.Loading(s.Soul)) phase = "WAITING";
                    else if (!D.Visible(s.Soul))
                    {
                        if (flowStage > 0) phase = "WAITING";
                        else if (D.Visible(s.Recruit))
                        {
                            if (!D.Enabled(s.Recruit)) phase = "WAITING";
                            else if (!object.ReferenceEquals(ctx.SubUi, s.Recruit) || D.Read(s.Recruit, "page") as string != "recruit") why = "soul_recruit_ui_not_owned";
                            else s.RequirementAction = "growth_soul_open";
                        }
                        else if (ctx.SubUi != null) why = "soul_entry_subui_not_owned";
                        else s.RequirementAction = "growth_soul_recruit";
                    }
                    else if (D.Read(s.Soul, "page") as string != "gather" || D.Read(s.Soul, "subwindow") as string != "" || D.Bool(D.Read(s.Soul, "show_exchange_window"))) why = "soul_other_mode_or_modal";
                    else if (!object.ReferenceEquals(ctx.SubUi, s.Soul)) why = "soul_gather_ui_not_owned";
                    else if (D.Loading(s.Soul) || !D.Enabled(s.Soul) || D.Read(s.Soul, "gather_bg") == null) phase = "WAITING";
                    else if (flowStage == 0)
                    {
                        if (D.Int(D.Read(s.Soul, "currentGatherSoulStatus")) != 0 || D.Bool(D.Read(s.Soul, "gathered"))) why = "soul_existing_gather_session_not_owned";
                        else s.RequirementAction = "growth_soul_start";
                    }
                    else if (flowStage == 1)
                    {
                        if (mode != 1 || used != flowUsed + 1 || money != flowMoney || gold != flowGold) why = "soul_free_start_postcondition_changed";
                        else if (D.Int(D.Read(s.Soul, "currentGatherSoulStatus")) != 1 || D.Bool(D.Read(s.Soul, "isAllSoulFull"))) why = "soul_growth_not_available";
                        else s.RequirementAction = "growth_soul_grow";
                    }
                    else if (flowStage == 2 && D.Int(D.Read(s.Soul, "currentGatherSoulStatus")) == 1)
                    {
                        if (money != flowMoney - 3000 || gold != flowGold || !D.Bool(D.Read(s.Soul, "gathered"))) why = "soul_growth_budget_postcondition_changed";
                        else s.RequirementAction = "growth_soul_harvest";
                    }
                    else phase = "WAITING";
                }
            }
            else DescribeArtifact(ctx, s, q, ref phase, ref why);
            if (why != "")
            {
                // Missing capability does not prevent the ordinary close of an
                // already open task window. Execution still has no action.
                if (!RequirementUnavailable(why) || flowQuest != null) s.Gate = "BLOCKED";
                s.Reason = why; s.RequirementAction = ""; s.RequirementBlocked = true; phase = "BLOCKED";
            }
            else if (phase == "WAITING") { s.Gate = "WAITING_UI"; s.Reason = "growth_requirement_animation_or_callback"; }
            s.Requirement = "{\"quest_id\":" + q.Id + ",\"type\":" + q.Type + ",\"param0\":" + q.Param0 + ",\"param1\":" + q.Param1 + ",\"phase\":" + D.Q(phase) +
                ",\"stage\":" + flowStage + ",\"artifact_id\":" + flowArtifact + ",\"owner_id\":0,\"used_times\":" + used + ",\"free_times\":" + free + ",\"rate\":" + rate + ",\"grow_cost\":" + cost + ",\"mode\":" + mode +
                ",\"available_money\":" + money + ",\"gold\":" + gold + ",\"start_receipt\":" + startReceipt + ",\"grow_receipt\":" + growReceipt + ",\"harvest_receipt\":" + harvestReceipt + ",\"finish_receipt\":" + (finishOperation == null ? "null" : finishOperation.Json()) +
                ",\"feature_unlocked\":" + D.B(feature) + ",\"reputation_unlocked\":" + D.B(reputationUnlocked) + ",\"reputation_level\":" + D.Int(D.Read(ctx.Character, "reputation_level")) +
                ",\"reputation\":" + D.Long(D.Read(ctx.Character, "reputation")) + ",\"grant_stage\":" + (grantViewed ? 2 : grantRequested ? 1 : 0) + ",\"grant_receipt\":" + grantReceipt +
                ",\"blue_souls\":" + D.Int(D.Read(ctx.Character, "generalsoul_blue")) + ",\"general_count\":" + Dictionary(ctx.Character, "form_generals_").Count + ",\"repair_stage\":" + recruitRepair +
                ",\"recruit_receipt\":" + recruitReceipt + ",\"formation_receipt\":" + formationReceipt + ",\"recruit_tutorial_status\":" + D.Q(recruitTutorial == null ? "NONE" : recruitTutorial.Status) +
                ",\"ui\":{\"recruit\":" + D.UiJson(s.Recruit) + ",\"soul\":" + D.UiJson(s.Soul) + ",\"equipment\":" + D.UiJson(s.Equipment) + ",\"reputation\":" + D.UiJson(s.Reputation) + ",\"special\":" + D.UiJson(s.Special) + ",\"dialog\":" + D.UiJson(s.Dialog) + ",\"formation\":" + D.UiJson(s.Formation) + "}}";
        }
        private void ObserveRequirement(SharedDevelopmentContext ctx)
        {
            if (flowQuest == null) return;
            if (!ctx.SameOwnership(flowContext)) { flowError = "growth_requirement_identity_changed"; return; }
            if (operation.Status == "OBSERVED")
            {
                if (operation.Action == "growth_soul_start") startReceipt = operation.Json();
                if (operation.Action == "growth_soul_grow") growReceipt = operation.Json();
                if (operation.Action == "growth_soul_harvest") harvestReceipt = operation.Json();
                if (operation.Action == "growth_artifact_grant") grantReceipt = operation.Json();
                if (operation.Action == "growth_recruit_confirm") recruitReceipt = operation.Json();
                if (operation.Action == "growth_recruit_save") formationReceipt = operation.Json();
            }
            if (flowQuest.Type == 6 && recruitReceipt != "null")
            {
                Component popup = ctx.Find("SpecialGetUI");
                if (recruitTutorial == null && D.Visible(popup) && D.Read(popup, "showtype") as string == "general" && D.Int(D.Read(popup, "tempid")) == 71)
                    recruitTutorial = new GeneralTutorialModel();
                if (recruitTutorial != null) recruitTutorial.CaptureRecruit71(ctx.All, ctx.Root, ctx.GlobalUi, ctx.Character, ctx.CharacterId, ctx.Find("GeneralRecruitUI"));
            }
            if (finishOperation == null || !finishOperation.Pending) return;
            if (!finishArmed)
            {
                if (ctx.Network.Handlers.Contains((ushort)810))
                { finishOperation.Invoked(ctx, ctx.Find("NewbieQuestUI"), "sc_newbie_quest_finish"); finishArmed = true; }
                else if (D.Int(D.Read(flowQuest.Data, "isfinish")) == 1)
                { finishOperation.Unknown("growth_deferred_finish_receipt_missed"); flowError = finishOperation.Reason; }
            }
            if (finishArmed) finishOperation.Observe(ctx);
            if (finishOperation.Status == "UNKNOWN" || finishOperation.Status == "ERROR") flowError = finishOperation.Reason;
        }
        private void DescribeArtifact(SharedDevelopmentContext ctx, State s, Quest q, ref string phase, ref string why)
        {
            if (why != "") return;
            IDictionary generals = Dictionary(ctx.Character, "form_generals_"), artifacts = Dictionary(ctx.Character, "artifacts");
            if (!generals.Contains((ushort)0)) { why = "artifact_protagonist_unavailable"; return; }
            object owner = generals[(ushort)0];
            if (q.Finished == 1)
            {
                if (finishOperation == null || finishOperation.Status != "OBSERVED" || finishOperation.Server != "PASS") why = "artifact_finish_receipt_unverified";
                else if (operation.Receipt == null || operation.Receipt.Opcode != 863 || operation.Receipt.Result != 0 || operation.Receipt.Id != flowArtifact ||
                    !artifacts.Contains((ushort)flowArtifact) || !object.ReferenceEquals(D.Read(owner, "artifact"), artifacts[(ushort)flowArtifact]) ||
                    D.Int(D.Read(artifacts[(ushort)flowArtifact], "idgeneral_template")) != 0) why = "artifact_completed_owner_or_receipt_changed";
                else if (grantRequested && (grantReceipt == "null" || D.Int(D.Read(ctx.Character, "reputation_level")) != 1 ||
                    D.Long(D.Read(ctx.Character, "reputation")) != flowReputation || D.Money(ctx.Character) != flowMoney || D.Long(D.Read(ctx.Character, "gold")) != flowGold)) why = "artifact_completed_grant_resources_changed";
                else if (D.Visible(s.Equipment)) s.RequirementAction = "growth_requirement_close";
                else phase = "DONE";
                return;
            }
            if (flowStage > 0)
            {
                if (operation.Status == "OBSERVED" && operation.Action == "growth_artifact_wear" &&
                    (operation.Receipt == null || operation.Receipt.Id != flowArtifact || operation.Receipt.Result != 0 ||
                    !artifacts.Contains((ushort)flowArtifact) || !object.ReferenceEquals(D.Read(owner, "artifact"), artifacts[(ushort)flowArtifact]) ||
                    D.Int(D.Read(artifacts[(ushort)flowArtifact], "idgeneral_template")) != 0)) why = "artifact_equip_receipt_or_owner_mismatch";
                else phase = "WAITING";
                return;
            }
            if (D.Read(owner, "artifact") != null) { why = "artifact_protagonist_slot_not_empty"; return; }
            if (!grantRequested && D.Loading(s.Special)) { phase = "WAITING"; return; }
            if (grantRequested && !grantViewed)
            {
                if (operation.Pending || D.Loading(s.Special)) { phase = "WAITING"; return; }
                if (grantReceipt == "null" || D.Int(D.Read(ctx.Character, "reputation_level")) != 1 ||
                    D.Long(D.Read(ctx.Character, "reputation")) != flowReputation || D.Money(ctx.Character) != flowMoney || D.Long(D.Read(ctx.Character, "gold")) != flowGold ||
                    !object.ReferenceEquals(grantTemplate, D.Template("reputation", 1)) || artifacts.Count != 1 || !artifacts.Contains((ushort)1))
                { why = "artifact_initial_grant_postcondition_changed"; return; }
                object granted = artifacts[(ushort)1];
                if (D.Int(D.Read(granted, "idartifact_template")) != 1 || D.Int(D.Read(granted, "idgeneral_template")) != 65535)
                { why = "artifact_initial_grant_owner_changed"; return; }
                object forge = ArtifactForge();
                if (forge == null || !object.ReferenceEquals(grantPopup, s.Special) || !D.Enabled(s.Special) ||
                    D.Read(s.Special, "showtype") as string != "artifact" || D.Read(s.Special, "iconstr") as string != "item" + D.Read(forge, "icon") ||
                    D.Read(s.Special, "msg2") as string != D.Read(forge, "desc") as string || D.Read(s.Special, "OnClose") != null ||
                    D.Bool(D.Read(s.Special, "add_to_treasury")) || ArtifactView() == null)
                { why = "artifact_initial_grant_popup_not_owned"; return; }
                flowArtifact = 1; s.RequirementAction = "growth_artifact_view"; return;
            }
            if (D.Visible(s.Special)) { why = "artifact_unowned_special_popup"; return; }
            int candidate = D.Int(D.Method(s.Equipment.GetType(), "CheckBestArtifact", new Type[] { typeof(ushort) }, false, typeof(ushort)).Invoke(s.Equipment, new object[] { (ushort)0 }));
            if (candidate < 1 || candidate > 255 || !artifacts.Contains((ushort)candidate))
            {
                if (artifacts.Count == 0 && !grantRequested) DescribeInitialGrant(ctx, s, q, ref phase, ref why);
                else why = Feature(ctx, "reputation") ? "artifact_owned_candidate_unavailable" : "artifact_reputation_feature_locked";
                return;
            }
            if (grantRequested && (candidate != 1 || D.Int(D.Read(ctx.Character, "reputation_level")) != 1 ||
                D.Long(D.Read(ctx.Character, "reputation")) != flowReputation || D.Money(ctx.Character) != flowMoney || D.Long(D.Read(ctx.Character, "gold")) != flowGold))
            { why = "artifact_granted_candidate_or_resources_changed"; return; }
            object artifact = artifacts[(ushort)candidate];
            if (D.Int(D.Read(artifact, "idartifact_template")) != candidate || D.Int(D.Read(artifact, "idgeneral_template")) != 65535) { why = "artifact_candidate_not_unowned"; return; }
            flowArtifact = candidate;
            if (!D.Visible(s.Ui) || s.ShowWindow)
            {
                if (!OnlyBoundAutoCheck(s, q)) why = "growth_guide_would_check_other_quest";
                else s.RequirementAction = "growth_guide_open";
            }
            else if (D.Loading(s.Equipment)) phase = "WAITING";
            else if (!D.Visible(s.Equipment))
            {
                if (ctx.SubUi != null) why = "artifact_entry_subui_not_owned";
                else s.RequirementAction = "growth_artifact_open";
            }
            else if (!D.Enabled(s.Equipment) || D.Loading(s.Equipment)) phase = "WAITING";
            else if (!object.ReferenceEquals(ctx.SubUi, s.Equipment) || D.Bool(D.Read(s.Equipment, "sended")) || D.Int(D.Read(s.Equipment, "m_DragState")) != 0) why = "artifact_equipment_ui_not_owned";
            else s.RequirementAction = "growth_artifact_wear";
        }
        private static object ArtifactForge()
        {
            System.Reflection.MethodInfo method = Model.FirstType("Database.Artifact").GetMethod("get_forge_info", D.StaticFlags, null, new Type[] { typeof(uint), typeof(byte) }, null);
            if (method == null || method.IsGenericMethod || method.ReturnType.FullName != "Database._artifact_forge_info" || method.ReturnType.Assembly.GetName().Name != "dbdata") throw new ProbeFailure("artifact_forge_info_schema");
            return method.Invoke(null, new object[] { 1u, (byte)0 });
        }
        private static Delegate ArtifactView()
        {
            Delegate view = D.Static(Model.FirstType("DelegateFunc"), "MainUI_equipment_ui") as Delegate;
            return view != null && view.GetInvocationList().Length == 1 && view.Target == null &&
                view.Method.DeclaringType == Model.GameType("DelegateFuncUI") && view.Method.Name == "MainUI_equipment_ui" &&
                view.Method.ReturnType == typeof(void) && view.Method.GetParameters().Length == 0 ? view : null;
        }
        private bool RecruitResources(SharedDevelopmentContext ctx, bool spent)
        {
            return flowQuest == null || D.Int(D.Read(ctx.Character, "generalsoul_blue")) == recruitBlue - (spent ? 250 : 0) &&
                D.Int(D.Read(ctx.Character, "generalsoul_purple")) == recruitPurple && D.Int(D.Read(ctx.Character, "generalsoul_orange")) == recruitOrange &&
                D.Int(D.Read(ctx.Character, "generalsoul_red")) == recruitRed && D.Money(ctx.Character) == flowMoney && D.Long(D.Read(ctx.Character, "gold")) == flowGold;
        }
        private void DescribeRecruit(SharedDevelopmentContext ctx, State s, Quest q, ref string phase, ref string why)
        {
            if (D.Read(ctx.Root, "LevelID") as string != "Campaign") { why = "recruit71_campaign_required"; return; }
            IDictionary generals = Dictionary(ctx.Character, "form_generals_");
            object template = D.Template("general_template", 71);
            if (template == null || D.Int(D.Read(template, "id")) != 71 || D.Int(D.Read(template, "cost_soul_type")) != 3 ||
                D.Int(D.Read(template, "cost_value")) != 250 || D.Int(D.Read(template, "unlock_level")) > D.Int(D.Read(ctx.Character, "level")))
            { why = "recruit71_candidate_unavailable"; return; }
            if (flowStage >= 2)
            {
                if (recruitReceipt == "null") { phase = "WAITING"; return; }
                if (D.Visible(s.Dialog)) { why = "recruit71_unexpected_modal_after_recruit"; return; }
                if (generals.Count != 5 || !generals.Contains((ushort)71) || !RecruitResources(ctx, true)) { why = "recruit71_receipt_party_or_resources_changed"; return; }
                foreach (DictionaryEntry old in recruitOriginal) if (!generals.Contains(old.Key) || !object.ReferenceEquals(generals[old.Key], old.Value)) { why = "recruit71_replaced_existing_general"; return; }
                if (recruitTutorial == null) { phase = "WAITING"; return; }
                if (recruitTutorial.Status == "UNKNOWN") { why = "recruit71_tutorial_unknown"; return; }
                if (recruitTutorial.Status == "COMPLETE")
                {
                    if (formationReceipt == "null" || finishOperation == null || finishOperation.Status != "OBSERVED" || finishOperation.Server != "PASS" || q.Finished != 1)
                    { phase = "WAITING"; return; }
                    phase = "DONE"; return;
                }
                if (recruitTutorial.Gate != "PASS") { phase = "WAITING"; return; }
                if (recruitTutorial.Status == "DEPLOY")
                {
                    if (!object.ReferenceEquals(s.Special, recruitPopup) || D.Read(s.Special, "showtype") as string != "general" || D.Int(D.Read(s.Special, "tempid")) != 71)
                    { why = "recruit71_special_popup_changed"; return; }
                    s.RequirementAction = "growth_recruit_deploy";
                }
                else if (recruitTutorial.Status == "FORMATION_CONFIRM") s.RequirementAction = "growth_recruit_save";
                else phase = "WAITING";
                return;
            }
            if (q.Finished == 1) { why = "recruit71_task_finished_without_owned_run"; return; }
            int capacity = D.Int(D.Method(Model.FirstType("Global"), "CalcLevelSlot", Type.EmptyTypes, true, typeof(int)).Invoke(null, new object[0])) + D.Int(D.Read(ctx.Character, "general_carry_slot_bought"));
            IDictionary forms = Dictionary(ctx.Character, "formations_"); int current = D.Int(D.Read(ctx.Character, "cur_form"));
            if (generals.Count != 4 || generals.Contains((ushort)71) || capacity < 5 || !forms.Contains(current) || D.Int(D.Read(forms[current], "spotnum")) < 5)
            { why = "recruit71_initial_party_or_slot_unavailable"; return; }
            IList outside = D.Read(ctx.Character, "GeneralsOutSquad") as IList;
            if (outside == null || outside.Count > 512) { why = "recruit71_outside_party_unknown"; return; }
            foreach (object gen in outside) if (D.Int(D.Read(gen, "id")) == 71) { why = "recruit71_candidate_unavailable"; return; }
            if (D.Int(D.Read(ctx.Character, "generalsoul_blue")) < 250) { why = "recruit71_blue_budget_insufficient"; return; }
            if (!RecruitResources(ctx, false)) { why = "recruit71_baseline_resources_changed"; return; }
            if (flowStage == 1)
            {
                s.RecruitCandidate = recruitInfo;
                Delegate ok = D.Read(s.Dialog, "OnOK") as Delegate;
                if (!D.Enabled(s.Dialog) || D.Loading(s.Dialog)) { phase = "WAITING"; return; }
                if (!object.ReferenceEquals(s.Dialog, recruitDialog) || !object.ReferenceEquals(D.Read(s.Recruit, "torecruit_geninfo"), recruitInfo) ||
                    ok == null || ok.GetInvocationList().Length != 1 || !object.ReferenceEquals(ok.Target, s.Recruit) || ok.Method.Name != "OnRecruit" ||
                    D.Read(s.Dialog, "OnOK_objs") != null || D.Read(s.Dialog, "OnClose") != null || !object.ReferenceEquals(D.Read(s.Dialog, "old_rmg"), ctx.UiRoot) ||
                    !D.Bool(D.Read(s.Dialog, "bCancel")) || D.Bool(D.Read(s.Dialog, "isGold")) || D.Bool(D.Read(s.Dialog, "bModal")))
                { why = "recruit71_confirmation_not_owned"; return; }
                s.RequirementAction = "growth_recruit_confirm"; return;
            }
            if (D.Visible(s.Dialog) || D.Visible(s.Special) || D.Visible(s.Formation)) { why = "recruit71_foreign_modal_or_formation"; return; }
            if (!D.Visible(s.Ui) || s.ShowWindow)
            {
                if (!OnlyBoundAutoCheck(s, q)) why = "growth_guide_would_check_other_quest";
                else s.RequirementAction = "growth_guide_open";
                return;
            }
            if (D.Loading(s.Recruit)) { phase = "WAITING"; return; }
            if (!D.Visible(s.Recruit))
            {
                if (ctx.SubUi != null) why = "recruit71_entry_subui_changed";
                else s.RequirementAction = "growth_recruit_open";
                return;
            }
            if (!D.Enabled(s.Recruit) || D.Bool(D.Read(s.Recruit, "paging"))) { phase = "WAITING"; return; }
            if (!object.ReferenceEquals(ctx.SubUi, s.Recruit) || D.Read(s.Recruit, "page") as string != "recruit") { why = "recruit71_page_not_owned"; return; }
            IList indices = D.Read(s.Recruit, "blue_level_index") as IList;
            IDictionary groups = D.Read(s.Recruit, "blue_gens") as IDictionary;
            if (indices == null || indices.Count < 1 || indices.Count > 32 || D.Int(indices[0]) != 2 || groups == null || !groups.Contains(2u)) { why = "recruit71_blue20_group_unavailable"; return; }
            IList group = groups[2u] as IList; object info = null;
            if (group == null || group.Count > 6) { why = "recruit71_group_schema"; return; }
            foreach (object row in group) if (D.Int(D.Read(D.Read(row, "temp"), "id")) == 71) { if (info != null) { why = "recruit71_duplicate_candidate"; return; } info = row; }
            if (info == null || !object.ReferenceEquals(D.Read(info, "temp"), template) || D.Bool(D.Read(info, "alreadygot")) || D.Bool(D.Read(info, "locked")) || D.Bool(D.Read(info, "insquad")) ||
                !(bool)D.Method(s.Recruit.GetType(), "check_recruit_campaign", new Type[] { info.GetType() }, false, typeof(bool)).Invoke(s.Recruit, new object[] { info }))
            { why = "recruit71_candidate_unavailable"; return; }
            int level = D.Int(D.Read(s.Recruit, "genlevel")); float offset = Convert.ToSingle(D.Read(s.Recruit, "posx"));
            if (D.Read(s.Recruit, "genpage") as string != "blue" || level != (recruitRepair == 1 ? 1 : 0))
            {
                if (D.Bool(D.Read(s.Ui, "m_firstshow"))) phase = "WAITING";
                else why = "recruit71_blue_page_selection_changed";
                return;
            }
            if (recruitRepair == 1)
            {
                if (offset != -762f) { why = "recruit71_right_page_postcondition"; return; }
                s.RequirementAction = "growth_recruit_left"; return;
            }
            if (offset != 0f)
            {
                if (recruitRepair != 0 || indices.Count < 2 || D.Int(D.Read(s.Recruit, "levelcount")) != indices.Count) why = "recruit71_page_repair_unavailable";
                else s.RequirementAction = "growth_recruit_right";
                return;
            }
            recruitInfo = info; s.RecruitCandidate = info; s.RequirementAction = "growth_recruit_choose";
        }
        private void DescribeInitialGrant(SharedDevelopmentContext ctx, State s, Quest q, ref string phase, ref string why)
        {
            if (!Feature(ctx, "reputation")) { why = "artifact_reputation_feature_locked"; return; }
            object first = D.Template("reputation", 1); int artifact;
            if (D.Int(D.Read(ctx.Character, "reputation_level")) != 0 || D.Template("reputation", 0) == null || first == null ||
                D.Int(D.Read(first, "level")) != 1 || D.Int(D.Read(first, "trigger")) != 1 ||
                !Int32.TryParse(D.Read(first, "param") as string, out artifact) || artifact != 1 || D.Template("artifact_template", 1) == null || ArtifactForge() == null)
            { why = "artifact_initial_rank_grant_unavailable"; return; }
            if (D.Long(D.Read(first, "reputation")) > D.Long(D.Read(ctx.Character, "reputation")))
            { why = "artifact_initial_reputation_insufficient"; return; }
            flowArtifact = 1;
            if (!D.Visible(s.Ui) || s.ShowWindow)
            {
                if (!OnlyBoundAutoCheck(s, q)) why = "growth_guide_would_check_other_quest";
                else s.RequirementAction = "growth_guide_open";
            }
            else if (D.Loading(s.Reputation)) phase = "WAITING";
            else if (!D.Visible(s.Reputation))
            {
                if (ctx.SubUi != null) why = "artifact_reputation_entry_subui_not_owned";
                else s.RequirementAction = "growth_reputation_open";
            }
            else if (!D.Enabled(s.Reputation) || (DateTime)D.Read(s.Reputation, "m_LoadOverTime") > DateTime.Now) phase = "WAITING";
            else if (!object.ReferenceEquals(ctx.SubUi, s.Reputation) || D.Read(s.Reputation, "OnClose") != null || D.Bool(D.Read(s.Reputation, "sended"))) why = "artifact_reputation_ui_not_owned";
            else if (flowQuest != null && (D.Money(ctx.Character) != flowMoney || D.Long(D.Read(ctx.Character, "gold")) != flowGold || D.Long(D.Read(ctx.Character, "reputation")) != flowReputation)) why = "artifact_grant_baseline_resources_changed";
            else s.RequirementAction = "growth_artifact_grant";
        }
        private static bool RequirementAction(string action)
        { return action == "growth_guide_open" || action == "growth_soul_recruit" || action == "growth_soul_open" || action == "growth_soul_start" || action == "growth_soul_grow" || action == "growth_soul_harvest" || action == "growth_reputation_open" || action == "growth_artifact_grant" || action == "growth_artifact_view" || action == "growth_artifact_open" || action == "growth_artifact_wear" || action == "growth_requirement_close" ||
            action == "growth_recruit_open" || action == "growth_recruit_right" || action == "growth_recruit_left" || action == "growth_recruit_choose" || action == "growth_recruit_confirm" || action == "growth_recruit_deploy" || action == "growth_recruit_save"; }
        private static Command RecruitTutorialCommand(DevelopmentCommand c)
        { return new Command { Sequence = c.Sequence, CampaignAction = c.Action == "growth_recruit_deploy" ? "general_deploy" : "formation_confirm" }; }
        private void DescribeReward(SharedDevelopmentContext ctx, State s)
        {
            RewardWait wait = s.RewardLoading;
            wait.QuestId = claimedQuest; wait.ClaimOperationId = claimedGoal == null ? "" : claimedGoal.OperationId;
            wait.ClaimSequence = claimedGoal == null ? 0 : claimedGoal.Sequence;
            wait.DialogInstanceId = s.Reward == null ? 0 : s.Reward.GetInstanceID();
            if (s.Reward != null)
            {
                wait.Loading = D.Bool(D.Read(s.Reward, "loading"));
                wait.Collecting = D.Bool(D.Read(s.Reward, "collecting"));
                wait.ShowVisiblePending = D.Bool(D.Read(s.Reward, "m_bShowVisible"));
                IList assets = D.Read(s.Reward, "asset_names") as IList;
                if (assets != null && assets.Count <= 4096) wait.AssetNamesCount = assets.Count;
            }
            // OBSERVED/PASS is set only after this exact 811 capture has
            // forwarded the original callback once and observed its return.
            // Retain that claim fact even if later UI ownership is lost.
            if (claimedGoal != null && operation.OperationId == claimedGoal.OperationId &&
                operation.Sequence == claimedGoal.Sequence && operation.TargetId == claimedQuest &&
                operation.Status == "OBSERVED" && operation.Action == "growth_claim" && operation.Opcode == 811 &&
                operation.Server == "PASS" && operation.Reason == "own_normal_callback_captured_and_returned" &&
                operation.Receipt != null && operation.Receipt.Opcode == 811 && operation.Receipt.Result == 0)
                claimedReceiptConfirmed = true;
            wait.ClaimConfirmed = claimedReceiptConfirmed;
            if (!wait.ClaimConfirmed) return;
            wait.Phase = "BLOCKED"; wait.Reason = "growth_claim_succeeded_reward_binding_changed";
            if (!ClaimedTargetMatches(ctx, s) || s.Reward == null ||
                claimedReward != null && !object.ReferenceEquals(claimedReward, s.Reward)) return;
            Delegate close = D.Read(s.Reward, "OnClose") as Delegate;
            object owner = D.Read(s.Reward, "rmg"); Component ownerUi = owner as Component;
            if (D.Read(s.Reward, "mine_ui") as string != "" || D.Bool(D.Read(s.Reward, "autoClose")) ||
                !object.ReferenceEquals(D.Read(s.Reward, "m_prevUI"), ctx.UiRoot) ||
                close == null || close.GetInvocationList().Length != 1 || !object.ReferenceEquals(close.Target, claimedUi) ||
                // NewbieQuestUI inherits the original EnableAll callback;
                // Method.DeclaringType is __BaseUI even when Target is the
                // exact claimed NewbieQuestUI (which may normally be hidden).
                close.Method.DeclaringType != Model.FirstType("__BaseUI") || close.Method.Name != "EnableAll" ||
                ownerUi == null || s.Reward.transform.root != ownerUi.transform.root ||
                claimedRewardOwner != null && !object.ReferenceEquals(claimedRewardOwner, owner) ||
                claimedRewardClose != null && !object.ReferenceEquals(claimedRewardClose, close)) return;
            // Pin the callback's exact cached dialog and delegate. Subsequent
            // loading samples cannot silently adopt another reward window.
            claimedReward = s.Reward; claimedRewardOwner = owner; claimedRewardClose = close;
            wait.Bound = true; s.RewardQuest = claimedQuest;
            s.RewardConfirm = CanConfirmReward(ctx, s);
            if (s.RewardConfirm) { wait.Phase = "READY"; wait.Reason = ""; return; }
            string panels = ctx.RewardPanels(s.Ui, s.Reward);
            if (wait.Loading == true && wait.Collecting == false && wait.AssetNamesCount.HasValue && wait.AssetNamesCount.Value > 0 && wait.ShowVisiblePending == true &&
                (panels == "PASS" || panels == "controller_gate:loading_or_battle"))
            { wait.Phase = "LOADING"; wait.Reason = "growth_claim_reward_assets_loading"; return; }
            wait.Reason = "growth_claim_succeeded_reward_not_ready";
        }
        private bool CanConfirmReward(SharedDevelopmentContext ctx, State s)
        {
            Delegate close = s.Reward == null ? null : D.Read(s.Reward, "OnClose") as Delegate;
            return ClaimedTargetMatches(ctx, s) &&
                claimedReceiptConfirmed && object.ReferenceEquals(s.Reward, claimedReward) && object.ReferenceEquals(close, claimedRewardClose) &&
                object.ReferenceEquals(D.Read(s.Reward, "rmg"), claimedRewardOwner) &&
                operation.Status == "OBSERVED" && operation.Action == "growth_claim" && operation.TargetId == claimedQuest &&
                operation.Receipt != null && operation.Receipt.Opcode == 811 && operation.Receipt.Result == 0 &&
                D.Enabled(s.Reward) && !D.Loading(s.Reward) &&
                D.Read(s.Reward, "mine_ui") as string == "" && !D.Bool(D.Read(s.Reward, "autoClose")) &&
                object.ReferenceEquals(D.Read(s.Reward, "m_prevUI"), ctx.UiRoot) &&
                close != null && close.GetInvocationList().Length == 1 && object.ReferenceEquals(close.Target, s.Ui) && close.Method.Name == "EnableAll" &&
                ctx.RewardPanels(s.Ui, s.Reward) == "PASS";
        }
        private bool ClaimedTargetMatches(SharedDevelopmentContext ctx, State s)
        {
            Quest q = s.Find(claimedQuest);
            return claimedQuest > 0 && claimedTarget != null && claimedGoal != null && ctx.SameOwnership(claimContext) &&
                object.ReferenceEquals(s.Ui, claimedUi) &&
                session.Goal != null && session.Goal.GoalKey() == claimedGoal.GoalKey() &&
                operation.OperationId == claimedGoal.OperationId && operation.TargetId == claimedQuest &&
                object.ReferenceEquals(s.List, claimedList) && q != null && q.Finished == 1 && q.Got == 1 &&
                object.ReferenceEquals(q.Data, claimedTarget.Data) && object.ReferenceEquals(q.Template, claimedTarget.Template) &&
                q.Type == claimedTarget.Type && q.Param0 == claimedTarget.Param0 && q.Param1 == claimedTarget.Param1;
        }
        private static bool AllFinished(State s)
        { foreach (Quest q in s.Quests) if (q.Finished != 1) return false; return true; }
        private static void Describe(SharedDevelopmentContext ctx, Quest q)
        {
            q.Dispatch = "UNSUPPORTED"; q.Reason = "growth_action_chain_unverified"; object ch = ctx.Character;
            switch (q.Type)
            {
                case 1: q.Dispatch = "STAR_TECH"; q.Condition = D.Int(D.Read(ch, "star_tech")) >= q.Param0; break;
                case 2: q.Dispatch = "EQUIPMENT_WEAR"; q.Condition = AnyOwned(ch, "equipments"); break;
                case 3:
                    q.Dispatch = "EQUIPMENT_UPGRADE"; q.Condition = false;
                    foreach (object e in Dictionary(ch, "equipments").Values) if (D.Int(D.Read(e, "enchant_level")) >= q.Param0) q.Condition = true;
                    q.Reason = q.Condition == true ? "requirement_observed" : "use_reusable_equipment_capability"; break;
                case 4: q.Dispatch = "ARTIFACT_WEAR"; q.Condition = AnyOwned(ch, "artifacts"); break;
                case 5: q.Dispatch = "SOUL_GATHER"; q.Reason = "normal_gather_callback_required"; break;
                case 6: q.Dispatch = "GENERAL_RECRUIT"; q.Condition = Dictionary(ch, "form_generals_").Count > 4; break;
                case 7: q.Dispatch = "CHARACTER_LEVEL"; q.Condition = D.Int(D.Read(ch, "level")) >= q.Param0; q.Reason = "normal_experience_progress_required"; break;
                case 8:
                case 11:
                    q.Dispatch = q.Type == 8 ? "CAMPAIGN_TROOP" : "CAMPAIGN_CHAPTER";
                    if (q.Param0 < 1 || q.Param0 > 65535 || (q.Type == 8 && (q.Param1 < 0 || q.Param1 > 255))) { q.Reason = "growth_campaign_target_bounds"; break; }
                    object campaign = D.Method(ch.GetType(), "getCampaign", new Type[] { typeof(ushort) }, false, Model.FirstType("Database.Campaign")).Invoke(ch, new object[] { (ushort)q.Param0 });
                    if (campaign == null) { q.Condition = false; break; }
                    if (q.Type == 11) q.Condition = D.Bool(D.Read(campaign, "finished"));
                    else { IDictionary troops = Dictionary(campaign, "troops_"); q.Condition = troops.Contains((byte)q.Param1) && D.Bool(D.Read(troops[(byte)q.Param1], "killed")); }
                    q.Reason = "use_existing_campaign_capability"; break;
                case 9:
                    q.Dispatch = "FORMATION_GENERAL"; q.Condition = false;
                    IDictionary forms = Dictionary(ch, "formations_"); int cur = D.Int(D.Read(ch, "cur_form"));
                    if (!forms.Contains(cur)) { q.Condition = null; q.Reason = "growth_current_formation_unavailable"; break; }
                    IList spots = D.Read(forms[cur], "spotgen") as IList;
                    if (spots == null || spots.Count > 8) throw new ProbeFailure("growth_formation_spots_schema"); foreach (object spot in spots) if (D.Int(spot) == q.Param0) q.Condition = true;
                    q.Reason = "use_normal_formation_save_capability"; break;
                case 10: q.Dispatch = "CAPITAL_BUILDING"; q.Reason = "normal_build_and_return_callback_required"; break;
                case 12:
                    q.Dispatch = "EQUIPMENT_PART_CHANGE"; q.Condition = false;
                    IDictionary generals = Dictionary(ch, "form_generals_"); object weapon = D.Template("equipment_main", 1), armor = D.Template("equipment_main", 11);
                    if (!generals.Contains((ushort)0) || weapon == null || armor == null) { q.Condition = null; q.Reason = "growth_part_baseline_unavailable"; break; }
                    IList slots = D.Read(generals[(ushort)0], "equipments") as IList;
                    if (slots == null || slots.Count != 2) throw new ProbeFailure("growth_main_equipment_schema");
                    q.Condition = PartsDiffer(slots[0], weapon) || PartsDiffer(slots[1], armor); q.Reason = "equipment_part_change_action_unverified"; break;
                case 13: q.Dispatch = "CAPITAL_MINE"; q.Reason = "normal_capital_mining_callback_required"; break;
                case 14: q.Dispatch = "CAPITAL_PRODUCT"; q.Reason = "normal_capital_product_callback_required"; break;
            }
        }
        private static IDictionary Dictionary(object obj, string field)
        { IDictionary d = D.Read(obj, field) as IDictionary; if (d == null || d.Count > 512) throw new ProbeFailure("growth_dictionary_schema_or_bound:" + field); return d; }
        private static bool AnyOwned(object ch, string name)
        { foreach (object e in Dictionary(ch, name).Values) if (D.Int(D.Read(e, "idgeneral_template")) != 65535) return true; return false; }
        private static bool PartsDiffer(object e, object baseline)
        { return e != null && (D.Int(D.Read(e, "idequipment_main")) != D.Int(D.Read(baseline, "id")) || D.Int(D.Read(e, "idequipment_acc1")) != D.Int(D.Read(baseline, "accessory1")) || D.Int(D.Read(e, "idequipment_acc2")) != D.Int(D.Read(baseline, "accessory2"))); }
        private static bool CanClose(SharedDevelopmentContext ctx, State s)
        { return ctx.Panels(s.Ui) == "PASS" && ctx.Network.Idle && D.Enabled(s.Ui) && !D.Loading(s.Ui) && s.ShowWindow && D.Read(s.Ui, "OnClose") == null; }
        private bool Validate(DevelopmentCommand c, State s, out string why)
        {
            why = "";
            bool requirement = RequirementAction(c.Action);
            if (c.DesiredLevel != 0 || c.Mode != "" || c.TargetId < 1 || c.TargetId > 32767 || c.MaxMoney != (requirement && s.Find(c.TargetId) != null && s.Find(c.TargetId).Type == 5 ? 3000 : 0) || c.MaxStone != 0 || c.MaxFood != 0 || c.MaxRequests < 1 || c.MaxRequests > 10) { why = "growth_command_bounds"; return false; }
            if (operation.Pending || operation.Status == "ERROR") { why = "growth_operation_unresolved"; return false; }
            Quest q = s.Find(c.TargetId); if (q == null || q.Template == null) { why = "growth_exact_task_unavailable"; return false; }
            if (WeaponAction(c.Action))
            {
                if (s.Gate != "PASS" || s.RequirementAction != c.Action || c.TargetId != 3 || q.Type != 3 || q.Param0 != 5 || q.Param1 != 0 ||
                    q.Finished != 0 || q.Got != 0 || s.Selected != 3 || D.Visible(s.Ui) || c.MaxRequests != 1 ||
                    s.WeaponCandidate == null || s.WeaponOwner == null || s.WeaponFormation == null ||
                    weaponQuest != null && (weaponOperationId != c.OperationId || !context.SameOwnership(weaponContext)))
                { why = "growth_weapon_bound_action_changed"; return false; }
                return true;
            }
            if (requirement)
            {
                if (s.Gate != "PASS" || s.RequirementAction != c.Action || !FlowIdentity(context, s, q) || flowQuest != null && flowOperationId != c.OperationId ||
                    (flowQuest == null ? (D.Visible(s.Ui) ? s.UiSelected : s.Selected) != c.TargetId : flowQuest.Id != c.TargetId) ||
                    q.Got != 0 || c.MaxRequests != 4) { why = "growth_requirement_target_or_action_changed"; return false; }
                return true;
            }
            if (c.Action == "growth_reward_confirm") return s.RewardConfirm && s.RewardQuest == c.TargetId && rewardQueued == null;
            if (c.Action == "growth_close") return s.UiSelected == c.TargetId && CanClose(context, s);
            if (c.Action == "growth_open")
            {
                bool boundClaim = CanOpenObservedClaim(context, s, q) && observedClaimGoal.GoalKey() == c.GoalKey();
                bool selectedClaim = !D.Visible(s.Ui) && s.Selected == c.TargetId && q.Finished == 1 && q.Got == 0 && AllFinished(s);
                if (s.Gate != "PASS" || s.Action != c.Action || (!boundClaim && !selectedClaim)) { why = "growth_open_finished_target_or_auto_check_unsafe"; return false; }
                if (session.Goal != null && session.Goal.OperationId == c.OperationId && operation.Requests >= c.MaxRequests) { why = "growth_request_budget"; return false; }
                return true;
            }
            if (s.Gate != "PASS" || s.Action != c.Action || (D.Visible(s.Ui) ? s.UiSelected : s.Selected) != c.TargetId) { why = "growth_action_or_selected_task_changed"; return false; }
            if (c.Action == "growth_claim" && observedClaimGoal != null && observedClaimGoal.OperationId == c.OperationId &&
                (observedClaimGoal.GoalKey() != c.GoalKey() || !ClaimGoalMatches(context, s, q)))
            { why = "growth_claim_observed_goal_changed"; return false; }
            if (session.Goal != null && session.Goal.OperationId == c.OperationId && operation.Requests >= c.MaxRequests) { why = "growth_request_budget"; return false; }
            if (c.Action != "growth_open" && c.Action != "growth_check" && c.Action != "growth_claim") { why = "growth_action_unsupported"; return false; }
            return true;
        }
        internal bool Queue(DevelopmentCommand command, out string why)
        {
            why = ""; if (context == null || command == null) { why = "growth_not_observed"; return false; }
            State s = ReadState(context); if (!Validate(command, s, out why)) return false;
            if (command.Action == "growth_reward_confirm")
            { rewardQueued = command.Copy(); rewardContext = context; queuedReward = s.Reward; return true; }
            return session.Queue(command, context, s, s.Stamp(), out why);
        }
        internal bool ExecuteGui(DevelopmentCommand command, DevelopmentPublication publish, out string why)
        {
            if (command.Action == "growth_reward_confirm") return ConfirmReward(command, publish, out why);
            object anchor; string stamp; if (!session.Take(command, context, out anchor, out stamp, out why)) return false;
            State before = anchor as State, now = ReadState(context);
            if (before == null || now.Stamp() != stamp || !object.ReferenceEquals(before.List, now.List) || !object.ReferenceEquals(before.Find(command.TargetId).Data, now.Find(command.TargetId) == null ? null : now.Find(command.TargetId).Data) ||
                !object.ReferenceEquals(before.Find(command.TargetId).Template, now.Find(command.TargetId) == null ? null : now.Find(command.TargetId).Template) ||
                ((command.Action == "growth_recruit_choose" || command.Action == "growth_recruit_confirm") && !object.ReferenceEquals(before.RecruitCandidate, now.RecruitCandidate)) || !Validate(command, now, out why))
            { why = "growth_gui_task_or_state_changed"; return false; }
            if (WeaponAction(command.Action) && (!object.ReferenceEquals(before.WeaponCandidate, now.WeaponCandidate) ||
                !object.ReferenceEquals(before.WeaponOwner, now.WeaponOwner) || !object.ReferenceEquals(before.WeaponFormation, now.WeaponFormation) ||
                !object.ReferenceEquals(before.Equipment, now.Equipment)))
            { why = "growth_weapon_gui_identity_changed"; return false; }
            if (WeaponAction(command.Action) && weaponQuest == null)
            {
                weaponQuest = now.Find(command.TargetId); weaponContext = context; weaponList = now.List;
                weaponEquipment = now.WeaponCandidate; weaponOwner = now.WeaponOwner; weaponFormation = now.WeaponFormation; weaponId = now.WeaponId;
                weaponOperationId = command.OperationId; weaponStage = 0; weaponReceipt = "null";
                weaponMoney = D.Money(context.Character); weaponStone = D.Long(D.Read(context.Character, "enchant_stone")); weaponGold = D.Long(D.Read(context.Character, "gold"));
            }
            if (RequirementAction(command.Action) && flowQuest == null)
            {
                flowQuest = now.Find(command.TargetId); flowContext = context; flowOperationId = command.OperationId;
                flowMoney = D.Money(context.Character); flowGold = D.Long(D.Read(context.Character, "gold"));
                flowReputation = D.Long(D.Read(context.Character, "reputation"));
                grantPopup = now.Special; grantTemplate = D.Template("reputation", 1);
                if (flowQuest.Type == 6)
                {
                    recruitPopup = now.Special; recruitDialog = now.Dialog; recruitOriginal.Clear();
                    foreach (DictionaryEntry entry in Dictionary(context.Character, "form_generals_")) recruitOriginal[entry.Key] = entry.Value;
                    recruitBlue = D.Int(D.Read(context.Character, "generalsoul_blue")); recruitPurple = D.Int(D.Read(context.Character, "generalsoul_purple"));
                    recruitOrange = D.Int(D.Read(context.Character, "generalsoul_orange")); recruitRed = D.Int(D.Read(context.Character, "generalsoul_red"));
                }
                flowUsed = D.Int(D.Read(context.Character, "generalsoul_blue_times"));
            }
            if (session.Goal == null || session.Goal.OperationId != command.OperationId) operation = new DevelopmentOperation { OperationId = command.OperationId };
            int opcode = command.Action == "growth_check" || command.Action == "growth_open" || command.Action == "growth_guide_open" ? 810 : command.Action == "growth_claim" ? 811 :
                command.Action == "growth_weapon_wear" ? 856 : command.Action == "growth_soul_start" ? 517 : command.Action == "growth_soul_grow" ? 519 : command.Action == "growth_soul_harvest" ? 518 : command.Action == "growth_artifact_grant" ? 818 : command.Action == "growth_artifact_wear" ? 863 : command.Action == "growth_recruit_confirm" ? 305 : command.Action == "growth_recruit_save" ? 298 : 0;
            if (!operation.Begin(context, command, opcode, command.MaxRequests, delegate { session.Commit(command); state = now; publish(); }, out why)) return false;
            try
            {
                if (WeaponAction(command.Action))
                {
                    if (command.Action == "growth_weapon_open") D.MainBranch("_general_person");
                    else if (command.Action == "growth_weapon_close")
                    {
                        D.Call(now.Equipment, "Hide");
                        D.Method(Model.GameType("MainUI"), "SetSubUI", new Type[] { Model.FirstType("__BaseUI") }, true, typeof(void)).Invoke(null, new object[] { null });
                    }
                    else
                    {
                        weaponStage = 1;
                        if (!operation.InvokeNormal(context, now.Equipment, "sc_equip_auto_choose", delegate {
                            D.Method(now.Equipment.GetType(), "cs_equip_auto_choose", new Type[] { typeof(ushort), typeof(byte) }, false, typeof(void)).Invoke(now.Equipment, new object[] { (ushort)0, (byte)1 });
                        }, out why)) return false;
                    }
                }
                else if (RequirementAction(command.Action) && command.Action != "growth_guide_open")
                {
                    if (command.Action == "growth_soul_recruit") D.MainBranch("_army_recruit");
                    else if (command.Action == "growth_recruit_open") D.MainBranch("_army_recruit");
                    else if (command.Action == "growth_recruit_right" || command.Action == "growth_recruit_left")
                    {
                        int level = command.Action == "growth_recruit_right" ? 1 : 0;
                        D.Selection(now.Recruit, "paging", true); D.Selection(now.Recruit, "genlevel", level); D.Selection(now.Recruit, "paging_timer", 0.3f);
                        IList levels = D.Read(now.Recruit, "blue_level_index") as IList;
                        D.Method(context.Character.GetType(), "removehint", new Type[] { typeof(uint), typeof(uint) }, false, typeof(void)).Invoke(context.Character, new object[] { 6u, D.UInt(levels[level]) });
                        recruitRepair = level == 1 ? 1 : 2;
                    }
                    else if (command.Action == "growth_recruit_choose")
                    {
                        flowStage = 1; D.Call(now.Recruit, "Disable");
                        D.Method(now.Recruit.GetType(), "ToRecruit", new Type[] { recruitInfo.GetType() }, false, typeof(void)).Invoke(now.Recruit, new object[] { recruitInfo });
                    }
                    else if (command.Action == "growth_recruit_deploy")
                    {
                        Command adapted = RecruitTutorialCommand(command);
                        if (recruitTutorial == null || !recruitTutorial.Queue(adapted, out why) || !recruitTutorial.ExecuteGui(adapted, delegate { }, out why)) throw new ProbeFailure("recruit71_deploy_not_invoked");
                        flowStage = 3;
                    }
                    else if (command.Action == "growth_soul_open") D.Call(now.Recruit, "go_gather");
                    else if (command.Action == "growth_artifact_open") D.MainBranch("_general_person");
                    else if (command.Action == "growth_reputation_open") D.MainBranch("_reputation");
                    else if (command.Action == "growth_artifact_view")
                    {
                        Delegate view = ArtifactView();
                        if (view == null) throw new ProbeFailure("artifact_view_delegate_changed");
                        grantViewed = true; D.Call(now.Special, "Hide"); view.DynamicInvoke(new object[0]);
                    }
                    else if (command.Action == "growth_requirement_close")
                    {
                        Component panel = D.Visible(now.Soul) ? now.Soul : D.Visible(now.Recruit) ? now.Recruit : now.Equipment;
                        if (!D.Enabled(panel) || D.Loading(panel) || D.Read(panel, "OnClose") != null) throw new ProbeFailure("growth_requirement_close_not_owned");
                        D.Call(panel, "Hide");
                        D.Method(Model.GameType("MainUI"), "SetSubUI", new Type[] { Model.FirstType("__BaseUI") }, true, typeof(void)).Invoke(null, new object[] { null });
                    }
                    else
                    {
                        if (command.Action == "growth_soul_grow" || command.Action == "growth_artifact_wear" || command.Action == "growth_recruit_confirm")
                        {
                            finishOperation = new DevelopmentOperation(); finishArmed = false;
                            if (!finishOperation.Begin(context, command, 810, 1, delegate { }, out why)) throw new ProbeFailure("growth_deferred_finish_not_reserved");
                        }
                        if (command.Action == "growth_recruit_confirm")
                        {
                            flowStage = 2; operation.ExpectedArgument = recruitInfo;
                            if (!operation.InvokeNormal(context, now.Recruit, "sc_recruit", delegate {
                                D.Method(now.Dialog.GetType(), "ButtonOK", new Type[] { typeof(bool) }, false, typeof(void)).Invoke(now.Dialog, new object[] { true });
                            }, out why)) return false;
                        }
                        else if (command.Action == "growth_recruit_save")
                        {
                            Command adapted = RecruitTutorialCommand(command);
                            if (recruitTutorial == null || !recruitTutorial.Queue(adapted, out why)) throw new ProbeFailure("recruit71_save_not_queued");
                            flowStage = 4;
                            if (!operation.InvokeNormal(context, now.Formation, "sc_formupdate", delegate {
                                string failure; if (!recruitTutorial.ExecuteGui(adapted, delegate { }, out failure)) throw new ProbeFailure("recruit71_save_not_invoked");
                            }, out why)) return false;
                        }
                        else if (command.Action == "growth_artifact_grant")
                        {
                            grantRequested = true;
                            if (!operation.InvokeNormal(context, now.Reputation, "sc_reputation_up", delegate { D.Call(now.Reputation, "cs_reputation_up"); }, out why)) return false;
                        }
                        else if (command.Action == "growth_soul_start")
                        {
                            flowStage = 1;
                            if (!operation.InvokeNormal(context, now.Soul, "sc_refresh_gather_soul", delegate {
                                object template = D.Template("general_soul_start_mode", 1);
                                D.Method(now.Soul.GetType(), "RequestGather", new Type[] { template.GetType(), typeof(bool) }, false, typeof(void)).Invoke(now.Soul, new object[] { template, true });
                            }, out why)) return false;
                        }
                        else if (command.Action == "growth_soul_grow")
                        {
                            flowStage = 2;
                            if (!operation.InvokeNormal(context, now.Soul, "sc_grow_soul", delegate {
                                D.Method(now.Soul.GetType(), "RequestGrow", new Type[] { typeof(uint), typeof(uint) }, false, typeof(void)).Invoke(now.Soul, new object[] { 1u, 1u });
                                D.Selection(now.Soul, "gathered", true);
                            }, out why)) return false;
                        }
                        else if (command.Action == "growth_soul_harvest")
                        { flowStage = 3; if (!operation.InvokeNormal(context, now.Soul, "sc_harvest_soul", delegate { D.Call(now.Soul, "Harvest"); }, out why)) return false; }
                        else if (command.Action == "growth_artifact_wear")
                        {
                            flowStage = 1;
                            if (!operation.InvokeNormal(context, now.Equipment, "sc_equip_auto_artifact", delegate {
                                D.Method(now.Equipment.GetType(), "cs_equip_auto_artifact", new Type[] { typeof(ushort) }, false, typeof(void)).Invoke(now.Equipment, new object[] { (ushort)0 });
                            }, out why)) return false;
                        }
                    }
                }
                else if (command.Action == "growth_open" || command.Action == "growth_guide_open")
                {
                    opening = true; openingBefore = now; openingContext = context;
                    // An observed claim or guide opening separately proves no
                    // other quest can auto-check; the map fallback requires all
                    // quests finished.
                    // Neither opening may emit an unbound completion request.
                    D.Method(Model.GameType("MainUI"), "SetSubUI", new Type[] { Model.FirstType("__BaseUI") }, true, typeof(void)).Invoke(null, new object[] { null });
                    D.Method(now.Ui.GetType(), "MyLoading", new Type[] { typeof(ushort), typeof(string) }, false, typeof(void)).Invoke(now.Ui, new object[] { (ushort)command.TargetId, now.Level });
                }
                else if (command.Action == "growth_check")
                { if (!operation.InvokeNormal(context, now.Ui, "sc_newbie_quest_finish", delegate { D.Method(now.Ui.GetType(), "CheckQuest", new Type[] { typeof(ushort) }, false, typeof(void)).Invoke(now.Ui, new object[] { (ushort)command.TargetId }); }, out why)) return false; }
                else if (command.Action == "growth_claim")
                {
                    claimedQuest = command.TargetId; claimContext = context; claimedTarget = now.Find(command.TargetId);
                    claimedList = now.List; claimedGoal = command.Copy();
                    claimedUi = now.Ui; claimedReward = now.Reward; claimedRewardClose = null; claimedReceiptConfirmed = false;
                    claimedRewardOwner = now.Reward == null ? null : D.Read(now.Reward, "rmg");
                    if (!operation.InvokeNormal(context, now.Ui, "sc_newbie_quest_award", delegate { D.Call(now.Ui, "cs_newbie_quest_award"); }, out why)) return false;
                }
                else
                {
                    D.Call(now.Ui, "Hide");
                    D.Method(Model.GameType("MainUI"), "SetSubUI", new Type[] { Model.FirstType("__BaseUI") }, true, typeof(void)).Invoke(null, new object[] { null });
                }
                if (!opening && opcode == 0) operation.Invoked(context, now.Ui, ""); return true;
            }
            catch (Exception) { operation.Unknown("growth_normal_branch_threw"); why = operation.Reason; return false; }
        }
        private bool ConfirmReward(DevelopmentCommand command, DevelopmentPublication publish, out string why)
        {
            DevelopmentCommand queued = rewardQueued; rewardQueued = null; why = "growth_reward_context_changed";
            State now = ReadState(context);
            if (queued == null || queued.Sequence != command.Sequence || queued.ExpectedRevision != command.ExpectedRevision || queued.GoalKey() != command.GoalKey() ||
                !context.SameOwnership(rewardContext) || !D.Gui || !now.RewardConfirm || now.RewardQuest != command.TargetId || !object.ReferenceEquals(queuedReward, now.Reward)) return false;
            Delegate close = D.Read(now.Reward, "OnClose") as Delegate;
            operation.Stop("growth_reward_handoff");
            operation = new DevelopmentOperation { Action = command.Action, OperationId = command.OperationId, Sequence = command.Sequence,
                ExpectedRevision = command.ExpectedRevision, TargetId = command.TargetId, Status = "WAITING", MutationAttempted = true };
            publish();
            try
            {
                D.Call(now.Reward, "Hide"); close.DynamicInvoke(new object[0]);
                operation.Invoked(context, now.Reward, ""); claimedQuest = 0; claimContext = null; claimedTarget = null; claimedList = null; claimedGoal = null;
                claimedUi = claimedReward = null; claimedRewardOwner = null; claimedRewardClose = null; claimedReceiptConfirmed = false;
                if (finishOperation != null) finishOperation.Stop("growth_requirement_completed");
                flowQuest = null; flowContext = null; flowOperationId = ""; finishOperation = null; finishArmed = false; flowStage = flowArtifact = flowUsed = 0; flowError = "";
                startReceipt = growReceipt = harvestReceipt = "null";
                grantRequested = grantViewed = false; grantPopup = null; grantTemplate = null; grantReceipt = "null"; flowReputation = 0;
                recruitRepair = 0; recruitInfo = null; recruitPopup = recruitDialog = null; recruitTutorial = null; recruitOriginal.Clear(); recruitReceipt = formationReceipt = "null";
                why = ""; return true;
            }
            catch (Exception) { operation.Unknown("growth_reward_confirmation_threw"); why = operation.Reason; return false; }
        }
        internal void Stop(string reason) { session.Stop(); rewardQueued = null; operation.Stop(reason); if (finishOperation != null) finishOperation.Stop(reason); opening = false; observedClaimGoal = null; observedClaimContext = null; observedClaimQuest = null; observedClaimList = null; }
        internal string Json() { return state.Json(operation); }
    }
}




