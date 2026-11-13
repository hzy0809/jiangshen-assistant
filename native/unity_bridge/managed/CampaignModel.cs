using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace JiangShen.AssistantRuntime
{
    internal delegate void CampaignPublication();
    // All type/member names below are compiled constants against pinned game
    // assemblies. The wire protocol never supplies a reflection name or path.
    internal sealed partial class CampaignModel
    {
        private const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private readonly Model model;
        private readonly GeneralTutorialModel general = new GeneralTutorialModel();
        private readonly Type campaignType = Model.GameType("CampaignUI"), troopType = Model.GameType("CampaignTroopUI"), formationType = Model.GameType("FormationUI");
        private readonly Type battleType = Model.GameType("BattleUI"), normalResultType = Model.GameType("BattleNormalResultUI"), scenarioType = Model.GameType("ScenarioUI");
        private readonly Type functionType = Model.GameType("ScenarioFunctionUI");
        private readonly Type notificationType = Model.FirstType("MessageBox");
        private readonly Type lossAdviceType = Model.GameType("BattleValueUI");
        private readonly Type battleGuideType = Model.GameType("BattleGuideUI");
        private readonly Type chapterType = Model.GameType("CampaignChapterClearUI"), resultType = Model.FirstType("BattleResultUI"), mainType = Model.GameType("MainUI");
        private readonly Type rootType = Model.GameType("RootUI"), globalType = Model.FirstType("Global");
        internal readonly CampaignObserver Observer;
        private readonly bool challengeOnly;
        private readonly bool eliteOnly;
        private int AllowedCampaignType { get { return eliteOnly ? 4 : challengeOnly ? 2 : 0; } }
        internal BattleMode NativeMode { get { return eliteOnly ? BattleMode.EliteChallenge : challengeOnly ? BattleMode.NormalChallenge : BattleMode.Campaign; } }
        private string AllowedRoot { get { return challengeOnly ? "Anecdote" : "Campaign"; } }
        internal int Revision;
        internal string Phase = "BLOCKED", Gate = "not_observed";
        internal bool Available;
        private Component campaign, panel, formation, battle, result, scenario, chapter, reward;
        private Component function, functionScenario;
        private Component notification, queuedNotification, queuedNotificationRoot;
        private DateTime queuedNotificationVisibleTime;
        private Component lossAdvice, lossAdviceCampaign, lossAdviceRoot, queuedLossAdvice;
        private Component battleGuide, queuedBattleGuide, queuedBattleGuideCampaign, queuedBattleGuideRootUi, queuedBattleGuideOwner;
        private object queuedBattleGuideTroop, queuedBattleGuideData, queuedBattleGuideClient;
        private DateTime queuedBattleGuideVisibleTime;
        private ulong queuedBattleGuideUser;
        private uint queuedBattleGuideZone;
        private object lossAdviceCharacter;
        private string lossAdviceAttempt = "", lossAdviceCharacterId = "";
        private bool freshLossAdvice;
        private int lossAdviceCampaignId, lossAdviceTroopId;
        private Component freshLossAdvicePage, navigationPage, navigationRoot;
        private object navigationCharacter;
        private string navigationCharacterId;
        private DateTime queuedLossAdviceVisibleTime;
        private Component currentRoot, globalUi;
        private Snapshot common;
        private CampaignFrame frame;
        private int campaignId, troopId;
        private string characterId = "0";
        private string contextGate = "not_observed";
        private bool globalInputEnabled;
        private bool capitalDemonTransition;
        private readonly Hashtable selectionCounts = new Hashtable();
        private string candidateDiagnostics = "[]";
        private bool candidatesTruncated;
        private GameObject globalUiObject, battleUiObject;
        private MethodInfo meshComponents;
        private bool guiInvocation;
        private Component queuedRoot, queuedScenario;
        private object queuedScenarioData;
        private object queuedCharacter;
        private string queuedCharacterId;
        private int queuedSequence, queuedSection;
        private IList queuedSections;
        private int queuedSectionCount;
        private ulong queuedScenarioId;
        private DateTime queuedVisibleTime;
        private string queuedScenarioContinuation;
        private object queuedScenarioCallback;
        private Component queuedFunction, queuedFunctionScenario, queuedGlobalUi, queuedGlobalRoot;
        private object queuedFunctionCallback, queuedFunctionData;
        private uint queuedFunctionId;
        private DateTime queuedFunctionVisibleTime;
        private int queuedFunctionSection;
        private delegate bool CandidateMatch(Component component);
        private string eligible = "[]", tutorial = "{\"kind\":\"NONE\",\"step\":\"\",\"gate\":\"\"}", signature = "", lastCommand = "null", selectionDiagnostics = "null";
        private string capturedPassiveGate = "";
        internal CampaignModel(Model target) : this(target, false) { }
        internal CampaignModel(Model target, bool normalChallenge) : this(target, normalChallenge ? 2 : 0) { }
        internal CampaignModel(Model target, int kind)
        {
            if (kind != 0 && kind != 2 && kind != 4) throw new ArgumentException("campaign_mode");
            model = target; challengeOnly = kind != 0; eliteOnly = kind == 4; Observer = new CampaignObserver(AllowedCampaignType);
        }
        internal bool OwnsBattlePresentation(Component candidate)
        { return frame != null && candidate == battle && BattleOwned(candidate) && Observer.OwnsBattlePresentation(candidate, frame.ReportToken); }

        internal void Capture()
        {
            capitalDemonTransition = false;
            common = model.Capture();
            UObject[] all = model.AllUi();
            SelectContext(all, model.CurrentRoot());
            if (common.Character == null) common.Character = ReadStatic(globalType, "character_");
            characterId = CharacterId(common.Character);
            campaignId = campaign == null ? 0 : Int(Read(campaign, "m_campaignID"));
            troopId = campaign == null ? 0 : Int(Read(campaign, "m_troopID"));
            if (formation != null) { campaignId = Int(Read(formation, "Campaignid")); troopId = Int(Read(formation, "Troopid")); }
            if (panel != null) { object troop = Read(panel, "m_troop"); if (troop != null) { campaignId = Int(Read(troop, "idcampaign")); troopId = Int(Read(troop, "idtroop")); } }
            frame = BattleFrame(); Observer.Observe(frame);
            if (Visible(battle)) { campaignId = frame.CampaignId; troopId = frame.TroopId; }
            if ((result != null || reward != null) && battle != null)
            { campaignId = Int(Read(battle, "idcampaign")); troopId = Int(Read(battle, "idtroop")); }
            if (battleGuide != null)
            {
                campaignId = Int(Read(battleGuide, "m_campaignid"));
                object guideTroop = Read(battleGuide, "m_troop");
                troopId = guideTroop == null ? 0 : Int(Read(guideTroop, "idtroop"));
            }
            if (lossAdvice != null && !freshLossAdvice && Observer.Status == "NONE" && Observer.AttemptId == "" &&
                contextGate == "PASS" && campaign != null && common.Character != null && characterId != "0" &&
                !Bool(Read(common.Character, "gm")) && !Bool(Read(campaign, "m_bDebug")) && CurrentGameIdle())
            {
                // The ordinary close of an already visible advice page needs
                // its current UI identity, not a reconstructed defeat.
                RememberLossAdvice(true); freshLossAdvicePage = lossAdvice;
            }
            if (lossAdvice != null && (freshLossAdvice || lossAdviceAttempt != "" && Observer.AttemptId == lossAdviceAttempt))
            {
                campaignId = lossAdviceCampaignId; troopId = lossAdviceTroopId;
                if (freshLossAdvice && freshLossAdvicePage == null) freshLossAdvicePage = lossAdvice;
            }
            Component page = result != null ? result : reward != null ? reward : chapter != null ? chapter : battleGuide != null ? battleGuide : lossAdvice;
            if (!object.ReferenceEquals(navigationPage, page) || !object.ReferenceEquals(navigationRoot, currentRoot) ||
                !object.ReferenceEquals(navigationCharacter, common.Character) || navigationCharacterId != characterId)
                signature = ""; // A replacement page cannot reuse an older command revision.
            navigationPage = page; navigationRoot = currentRoot; navigationCharacter = common.Character; navigationCharacterId = characterId;
            if (challengeOnly) CaptureChallengeList(all);
            if (challengeOnly) ObserveChallengeEntry(all);
            eligible = Eligible();
            EvaluateContext(all);
            ObserveHomeNavigation();
            selectionDiagnostics = SelectionDiagnostics();
            string current = ContentJson();
            if (signature != current) { if (Revision >= 999999999) throw new ProbeFailure("campaign_revision_exhausted"); Revision++; signature = current; }
        }
        internal void EvaluateContext(UObject[] all)
        {
            general.Capture(all, currentRoot, globalUi, common.Character, characterId);
            Phase = scenario != null || function != null || lossAdvice != null || battleGuide != null || notification != null ? "SCENARIO" : result != null || reward != null || chapter != null ? "RESULT" : formation != null ? "FORMATION" : panel != null ? "TROOP" : Visible(battle) ? "BATTLE" : campaign != null ? "CAMPAIGN" : "MAIN";
            if (general.Active) Phase = "GENERAL_TUTORIAL";
            if (challengeOnly && challengeList != null) Phase = "CHALLENGE_LIST";
            Gate = "PASS"; Available = true;
            if (contextGate != "PASS") { Block(contextGate); Phase = "BLOCKED"; }
            if (common.Character == null || characterId == "0" || (Phase == "MAIN" && common.Main == null)) Block("main_or_character_unavailable");
            if (general.Active && (scenario != null || function != null || lossAdvice != null || battleGuide != null || notification != null || panel != null || formation != null || result != null || reward != null || chapter != null || Visible(battle))) Block("general_conflicting_campaign_ui");
            globalInputEnabled = Bool(ReadStatic(globalType, "extern_enabled"));
            for (int i = 0; i < all.Length; i++)
            {
                Behaviour b = all[i] as Behaviour;
                if (!Visible(b)) continue;
                if (b == common.Main || b == campaign || b == panel || b == formation || b == battle || b == result || b == scenario || b == function || b == lossAdvice || b == battleGuide || b == notification || b == chapter || b == reward || b == globalUi || challengeOnly && b == challengeList) continue;
                if (general.Allows(b)) continue;
                if (BattleChrome(b)) continue;
                if (CampaignChrome(b)) continue;
                if (ChallengeBackdropChrome(b)) continue;
                if (ChallengeVisualEffect(b)) continue;
                if (ChallengeRetiredTroopChrome(b)) continue;
                if (ExactEntryCapitalDemon(b, all)) continue;
                if (RetiredCapitalDemon(b, all)) continue;
                if (Phase == "MAIN" && RootLevel(currentRoot) == "Capital" && b.GetType() == Model.GameType("DemonScript"))
                {
                    try { if (object.ReferenceEquals(b, SharedDevelopmentContext.CapitalDemon(all, currentRoot, common.Character))) continue; }
                    catch (ProbeFailure error)
                    {
                        if (error.Message != "flower_capital_demon_identity") throw;
                        Block("campaign_capital_demon_identity_ambiguous"); continue;
                    }
                }
                string why = model.OverlayGate(b); if (why != null) Block("campaign_" + why + ":" + b.GetType().FullName);
            }
            object data = campaign == null ? null : Read(campaign, "m_campaign");
            if (Phase != "MAIN" && Phase != "CHALLENGE_LIST" && (data == null || Int(Read(data, "idcampaign_type")) != AllowedCampaignType || !Bool(Read(data, "unlocked")))) Block(eliteOnly ? "elite_challenge_type4_required" : challengeOnly ? "normal_challenge_type2_required" : "mainline_campaign_required");
            if (challengeOnly && challengeList != null && ChallengeListGate() != "PASS") Block(ChallengeListGate());
            if (Observer.Status == "UNKNOWN") Block("attempt_unknown:" + Observer.Evidence);
            tutorial = "{\"kind\":\"NONE\",\"step\":\"\",\"gate\":\"\"}";
            // Keep the selected passive page's tutorial and flow on one
            // observation, even across its animation/input time boundary.
            // Recompute on every Capture; command admission still checks live.
            capturedPassiveGate = "";
            if (general.Active && general.Gate != "PASS" && general.Gate != "WAITING_UI" && general.Gate != "WAITING_NETWORK") Block("general_tutorial:" + general.Gate);
            if (formation != null && Bool(Read(formation, "newhint_skill"))) tutorial = "{\"kind\":\"HINT\",\"step\":\"new-skill\",\"gate\":\"\"}";
            if (scenario != null)
            {
                string why = ScenarioGate(); capturedPassiveGate = why;
                tutorial = "{\"kind\":\"SCENARIO\",\"step\":" + Command.Quote(ScenarioStep()) + ",\"gate\":" + Command.Quote(why) + "}";
                if (why != "PASS" && !why.StartsWith("WAITING_")) Block(why);
            }
            if (function != null)
            {
                string why = FunctionGate(); capturedPassiveGate = why;
                tutorial = "{\"kind\":\"SCENARIO\",\"step\":" + Command.Quote("function:" + ((uint)Read(function, "m_idbutton")).ToString(CultureInfo.InvariantCulture)) + ",\"gate\":" + Command.Quote(why) + "}";
                if (why != "PASS" && !why.StartsWith("WAITING_")) Block(why);
            }
            if (lossAdvice != null)
            {
                string why = LossAdviceGate(); capturedPassiveGate = why;
                tutorial = "{\"kind\":\"SCENARIO\",\"step\":" + Command.Quote("loss-advice:" + (freshLossAdvice ? "current-page" : lossAdviceAttempt)) + ",\"gate\":" + Command.Quote(why) + "}";
                if (why != "PASS" && !why.StartsWith("WAITING_")) Block(why);
            }
            if (notification != null)
            {
                string why = NotificationGate(); capturedPassiveGate = why;
                tutorial = "{\"kind\":\"SCENARIO\",\"step\":" + Command.Quote("notification:" + notification.GetInstanceID().ToString(CultureInfo.InvariantCulture) + ":" + ((DateTime)Read(notification, "visible_time")).Ticks.ToString(CultureInfo.InvariantCulture)) + ",\"gate\":" + Command.Quote(why) + "}";
                if (why != "PASS" && !why.StartsWith("WAITING_")) Block(why);
            }
            if (battleGuide != null)
            {
                string why = BattleGuideGate(); capturedPassiveGate = why;
                tutorial = "{\"kind\":\"SCENARIO\",\"step\":" + Command.Quote("battle-guide:" + battleGuide.GetInstanceID().ToString(CultureInfo.InvariantCulture) + ":" + ((DateTime)Read(battleGuide, "visible_time")).Ticks.ToString(CultureInfo.InvariantCulture)) + ",\"gate\":" + Command.Quote(why) + "}";
                if (why != "PASS" && !why.StartsWith("WAITING_")) Block(why);
            }
            // Scenario Skip, icon movement and the function prompt's normal OK
            // branch do not consult the global root flag. Admit those contexts;
            // Execute independently refuses every other action while it is false.
            string scenarioGate = capturedPassiveGate;
            if (!globalInputEnabled && !(Phase == "SCENARIO" && (scenarioGate == "PASS" || scenarioGate.StartsWith("WAITING_"))) && !NormalRewardAcknowledgement()) Block("global_input_disabled");
            if (formation != null && Bool(Read(formation, "isLoading"))) Block("formation_loading");
            if ((Phase == "CAMPAIGN" || Phase == "TROOP" || Phase == "FORMATION") && common.Gate == "loading_or_battle") Block("campaign_loading");
            if ((Phase == "CAMPAIGN" || Phase == "TROOP" || Phase == "FORMATION") &&
                (campaign == null || !OwnedBy(campaign, currentRoot))) Block("campaign_not_current_root");
            if (Phase == "CAMPAIGN" && !Enabled(campaign)) Block("campaign_map_not_interactive");
            // A transient root load must not hide a conflicting UI or an
            // unverified tutorial discovered above.  Once those are clear,
            // the mainline flow may wait for the current root to settle.
            if (general.Active && common.Gate == "loading_or_battle") Block("general_current_root_loading");
            if (challengeOnly && entryStatus == "WAITING" && (Gate == "PASS" || Gate == "global_input_disabled"))
            { Gate = "WAITING_CHALLENGE_ENTER"; Available = false; }
            if (challengeOnly && entryStatus == "WAITING" && Phase == "CAMPAIGN" && object.ReferenceEquals(currentRoot, entryToRoot) &&
                (Gate == "PASS" || Gate == "WAITING_CHALLENGE_ENTER" || Gate == "global_input_disabled" || Gate == "campaign_loading" || Gate == "campaign_map_not_interactive"))
            { Gate = "WAITING_CHALLENGE_MAP"; Available = false; }
            if (challengeOnly && entryStatus == "UNKNOWN") { Gate = "challenge_entry_unknown:" + entryReason; Available = false; }
            if (challengeOnly && entryStatus == "WAITING" && challengeSourceRetirement && Phase == "CAMPAIGN" &&
                (Gate == "PASS" || Gate == "global_input_disabled" || Gate == "campaign_loading" ||
                 Gate == "campaign_map_not_interactive" || Gate == "WAITING_CHALLENGE_ENTER" || Gate == "WAITING_CHALLENGE_MAP"))
            { Gate = "WAITING_CHALLENGE_SOURCE_RETIREMENT"; Available = false; }
        }
        private void Block(string reason) { if (Gate == "PASS") Gate = reason; Available = false; }
        private void ContextBlock(string reason) { if (contextGate == "PASS") contextGate = reason; }
        // Exact uniqueness is required inside the proven ownership context, not
        // across all preloaded Campaign/Anecdote/Fairy/Talent scene components.
        internal void SelectContext(UObject[] all, Component root)
        {
            currentRoot = root; contextGate = "PASS"; selectionCounts.Clear();
            globalUiObject = model.GlobalUiObject(); battleUiObject = model.BattleUiObject();
            if (!ValidRoot(currentRoot)) ContextBlock("campaign_current_root_unavailable");
            if (common != null && common.Root != null && !object.ReferenceEquals(common.Root, currentRoot)) ContextBlock("campaign_current_root_snapshot_mismatch");
            bool mainline = RootLevel(currentRoot) == AllowedRoot;
            globalUi = Select(all, rootType, false, delegate(Component c) { return c.gameObject == globalUiObject && OwnerRoot(c) != null; }, "root_ui");
            campaign = Select(all, campaignType, false, delegate(Component c) { return mainline && OwnedBy(c, currentRoot); }, "campaign");
            battle = Select(all, battleType, true, delegate(Component c) { return mainline && BattleOwned(c); }, "battle");
            int[] battleCounts = (int[])selectionCounts["battle"];
            if (battleCounts[1] != 0 && battleCounts[2] == 0) ContextBlock("campaign_battle_context_mismatch");
            if (battle == null && battleCounts[2] == 0)
                battle = Select(all, battleType, false, delegate(Component c) { return !Visible(c) && mainline && BattleOwned(c); }, "battle_baseline");
            if (battle != null)
            {
                Component parent = Read(battle, "m_parentUI") as Component;
                if (campaign != null && !object.ReferenceEquals(campaign, parent)) ContextBlock("campaign_battle_parent_conflict");
                else campaign = parent; // A naturally inactive previous root is retained by this explicit game link.
            }
            panel = Select(all, troopType, true, delegate(Component c) { return mainline && OwnedBy(c, currentRoot) && campaign != null && c.gameObject == campaign.gameObject && object.ReferenceEquals(Read(c, "m_parent"), campaign); }, "troop");
            formation = Select(all, formationType, true, delegate(Component c) { return mainline && OwnedBy(c, currentRoot); }, "formation");
            chapter = Select(all, chapterType, true, delegate(Component c) { return mainline && campaign != null && OwnedBy(c, currentRoot) && c.gameObject == campaign.gameObject; }, "chapter");
            scenario = Select(all, scenarioType, true, delegate(Component c) { return mainline && campaign != null && ScenarioOwned(c); }, "scenario");
            function = Select(all, functionType, true, delegate(Component c) { return mainline && campaign != null && FunctionOwned(c); }, "function");
            notification = Select(all, notificationType, true, delegate(Component c) { return mainline && campaign != null && NotificationOwned(c); }, "notification");
            lossAdvice = Select(all, lossAdviceType, true, delegate(Component c) { return mainline && campaign != null && globalUi != null && c.gameObject == globalUi.gameObject && OwnedBy(c, OwnerRoot(globalUi)) && RootLevel(OwnerRoot(c)) == "Root"; }, "loss_advice");
            battleGuide = Select(all, battleGuideType, true, delegate(Component c) { return mainline && campaign != null && globalUi != null && c.gameObject == globalUi.gameObject && OwnedBy(c, OwnerRoot(globalUi)) && RootLevel(OwnerRoot(c)) == "Root"; }, "battle_guide");
            functionScenario = function == null ? null : Select(all, scenarioType, false, delegate(Component c) { return ScenarioOwned(c); }, "function_scenario");
            result = Select(all, normalResultType, true, delegate(Component c) { return battle != null && c.gameObject == battle.gameObject && OwnedBy(c, OwnerRoot(battle)) && object.ReferenceEquals(Read(c, "battle_ui"), battle); }, "normal_result");
            reward = Select(all, resultType, true, delegate(Component c) { return mainline && campaign != null && battle != null && c.gameObject == battle.gameObject && OwnedBy(c, OwnerRoot(battle)) && object.ReferenceEquals(Read(c, "m_prevUI"), currentRoot) && RewardCallback(c); }, "reward");
            if (mainline && campaign == null) ContextBlock("campaign_current_root_candidate_missing");
            if (mainline && globalUi == null) ContextBlock("campaign_global_ui_unavailable");
            CampaignDiagnostics(all);
            if (contextGate != "PASS" && Observer.Pending) Observer.Unknown(contextGate);
        }
        private Component Select(UObject[] all, Type type, bool visible, CandidateMatch match, string role)
        {
            Component selected = null; int total = 0, shown = 0, matching = 0;
            for (int i = 0; i < all.Length; i++)
            {
                Component candidate = all[i] as Component;
                if (candidate == null || candidate.GetType() != type) continue;
                total++; if (Visible(candidate)) shown++;
                if ((visible && !Visible(candidate)) || !match(candidate)) continue;
                matching++; selected = candidate;
            }
            selectionCounts[role] = new int[] { total, shown, matching };
            if (matching > 1) { ContextBlock("campaign_context_ambiguous_" + role); return null; }
            return selected;
        }
        private bool ValidRoot(Component root)
        { return root != null && root.GetType() == model.RootMessengerType && root.transform.root == root.transform; }
        private Component OwnerRoot(Component component)
        {
            if (component == null) return null;
            Component owner = Read(component, "rmg") as Component;
            return ValidRoot(owner) && component.transform.root == owner.transform ? owner : null;
        }
        private bool OwnedBy(Component component, Component root)
        { return ValidRoot(root) && object.ReferenceEquals(OwnerRoot(component), root); }
        private bool BattleOwned(Component candidate)
        {
            Component parent = Read(candidate, "m_parentUI") as Component;
            return parent != null && parent.GetType() == campaignType && OwnedBy(parent, currentRoot) &&
                candidate.gameObject == battleUiObject && OwnerRoot(candidate) != null && !object.ReferenceEquals(OwnerRoot(candidate), currentRoot) &&
                object.ReferenceEquals(Read(candidate, "m_prevUI"), currentRoot);
        }
        private bool ScenarioOwned(Component candidate)
        {
            return candidate != null && globalUi != null && candidate.gameObject == globalUi.gameObject &&
                OwnedBy(candidate, OwnerRoot(globalUi)) && object.ReferenceEquals(Read(candidate, "m_prevUI"), currentRoot);
        }
        private bool FunctionOwned(Component candidate)
        {
            return candidate != null && candidate.GetType() == functionType && globalUi != null && candidate.gameObject == globalUiObject &&
                candidate.gameObject == globalUi.gameObject && RootLevel(OwnerRoot(globalUi)) == "Root" && OwnedBy(candidate, OwnerRoot(globalUi));
        }
        private bool NotificationOwned(Component candidate)
        {
            return candidate != null && candidate.GetType() == notificationType && object.ReferenceEquals(Read(candidate, "old_rmg"), currentRoot) &&
                (OwnedBy(candidate, currentRoot) || globalUi != null && candidate.gameObject == globalUi.gameObject &&
                 RootLevel(OwnerRoot(globalUi)) == "Root" && OwnedBy(candidate, OwnerRoot(globalUi)));
        }
        private string NotificationGate()
        {
            if (!Visible(notification) || !NotificationOwned(notification) || campaign == null || !OwnedBy(campaign, currentRoot) || RootLevel(currentRoot) != AllowedRoot)
                return "notification_context_mismatch";
            if (Observer.Pending || !CurrentGameIdle()) return "notification_unsettled_battle";
            if (general.Active || scenario != null || function != null || lossAdvice != null || panel != null || formation != null || result != null || reward != null || chapter != null || Visible(battle))
                return "notification_conflicting_ui";
            // A normal local dismissal has no business callback, purchase,
            // navigation or cancel decision. Its text/art are not classifiers.
            if (Read(notification, "OnOK") != null || Read(notification, "OnClose") != null || Bool(Read(notification, "bCancel")) ||
                Bool(Read(notification, "bGoButton")) || Bool(Read(notification, "isGold")) || Bool(Read(notification, "bModal"))) return "notification_business_action_unknown";
            if (Bool(Invoke(notification, "IsLoading", Type.EmptyTypes, new object[0]))) return "WAITING_UI";
            object visible = Read(notification, "visible_time");
            if (!(visible is DateTime) || (DateTime)visible == DateTime.MinValue) return "notification_visibility_unknown";
            if ((DateTime.Now - (DateTime)visible).TotalSeconds < 0.3) return "WAITING_UI";
            if (!Enabled(notification)) return "WAITING_UI";
            return "PASS";
        }
        private string PassiveGate()
        { return battleGuide != null ? BattleGuideGate() : notification != null ? NotificationGate() : lossAdvice != null ? LossAdviceGate() : function != null ? FunctionGate() : scenario == null ? "" : ScenarioGate(); }
        internal bool BattleGuideContinuation(Command command)
        { return command.CampaignAction == "continue" && (battleGuide != null || queuedSequence == command.Sequence && queuedBattleGuide != null); }
        private string BattleGuideGate()
        {
            if (!Visible(battleGuide) || battleGuide.GetType() != battleGuideType || contextGate != "PASS" ||
                globalUi == null || globalUi.gameObject != globalUiObject || battleGuide.gameObject != globalUiObject ||
                !OwnedBy(battleGuide, OwnerRoot(globalUi)) || RootLevel(OwnerRoot(globalUi)) != "Root" ||
                currentRoot == null || !currentRoot.gameObject.activeInHierarchy || RootLevel(currentRoot) != AllowedRoot ||
                !object.ReferenceEquals(Read(battleGuide, "m_prevUI"), currentRoot) ||
                !object.ReferenceEquals(Read(globalUi, "level_rmg"), currentRoot) ||
                campaign == null || !OwnedBy(campaign, currentRoot) || !Visible(campaign) ||
                !object.ReferenceEquals(Read(battleGuide, "m_parent"), campaign)) return "battle_guide_context_mismatch";
            Component loader = ReadStatic(Model.FirstType("BaseAsset"), "lds") as Component;
            if (loader == null || loader.GetType() != Model.FirstType("LoadScreen") || !loader.gameObject.activeInHierarchy ||
                !object.ReferenceEquals(Invoke(loader, "GetCurUI", Type.EmptyTypes, new object[0]), currentRoot))
                return "battle_guide_loader_identity_changed";
            if (Visible(loader) || Bool(Read(loader, "isBattling")) || Bool(Read(currentRoot, "bLoading")) ||
                Int(Read(currentRoot, "ExpectingCount")) != 0 || Bool(Read(campaign, "loading")) || Bool(Read(campaign, "collecting")) ||
                Bool(Invoke(battleGuide, "IsLoading", Type.EmptyTypes, new object[0]))) return "WAITING_BATTLE_GUIDE_LOADING";
            object data = Read(campaign, "m_campaign"), target = Read(battleGuide, "m_troop");
            if (data == null || Int(Read(data, "idcampaign_type")) != AllowedCampaignType || !Bool(Read(data, "unlocked")) ||
                common.Character == null || characterId == "0" || !object.ReferenceEquals(ReadStatic(globalType, "character_"), common.Character) ||
                Bool(Read(common.Character, "gm")) || Bool(Read(campaign, "m_bDebug")) ||
                campaignId < 1 || troopId < 1 || troopId > 255 || Int(Read(campaign, "m_campaignID")) != campaignId ||
                target == null || Int(Read(target, "idcampaign")) != campaignId || !object.ReferenceEquals(TargetTroop(troopId), target))
                return "battle_guide_target_changed";
            if (Observer.Pending || Observer.Status == "UNKNOWN" || !CurrentGameIdle() ||
                !object.ReferenceEquals(ReadStatic(rootType, "m_client"), frame.QueueClient) ||
                challengeOnly && (entryStatus == "WAITING" || entryStatus == "UNKNOWN")) return "battle_guide_operation_unresolved";
            if (!Bool(Read(battleGuide, "m_bShowWnd"))) return "battle_guide_subpage_open";
            if (general.Active || scenario != null || function != null || lossAdvice != null || notification != null ||
                result != null || reward != null || chapter != null || panel != null || formation != null || Visible(battle) ||
                ReadStatic(mainType, "m_subui") != null) return "battle_guide_conflicting_ui";
            DateTime visible = (DateTime)Read(battleGuide, "visible_time");
            if (visible == DateTime.MinValue) return "battle_guide_visibility_unknown";
            if (!Enabled(battleGuide) || (DateTime.Now - visible).TotalSeconds < 0.3 ||
                DateTime.Now < (DateTime)Read(battleGuide, "m_LoadOverTime")) return "WAITING_BATTLE_GUIDE_INPUT";
            return "PASS";
        }
        private bool RewardCallback(Component candidate)
        {
            Delegate close = Read(candidate, "OnClose") as Delegate;
            return close != null && close.GetInvocationList().Length == 1 && object.ReferenceEquals(close.Target, campaign) && close.Method.Name == "BonusOpen" &&
                !close.Method.IsGenericMethod && close.Method.ReturnType == typeof(void) && close.Method.GetParameters().Length == 0;
        }
        private void RememberLossAdvice(bool fromFreshPage)
        {
            freshLossAdvice = fromFreshPage; freshLossAdvicePage = null;
            lossAdviceAttempt = Observer.Outcome == "LOSS" || eliteOnly && Observer.Status == "CONTINUED" ? Observer.AttemptId : "";
            lossAdviceCampaign = campaign; lossAdviceRoot = currentRoot;
            lossAdviceCharacter = common.Character; lossAdviceCharacterId = characterId;
            lossAdviceCampaignId = campaignId; lossAdviceTroopId = troopId;
        }
        private string LossAdviceGate()
        {
            bool fresh = freshLossAdvice && Observer.Status == "NONE" && Observer.AttemptId == "" && CurrentGameIdle() &&
                object.ReferenceEquals(lossAdvice, freshLossAdvicePage);
            bool ownedEliteContinuation = eliteOnly && Observer.Status == "CONTINUED" && Observer.Evidence == "elite_owned_result_receipt_unconfirmed";
            if (lossAdvice == null || Observer.Pending || (!fresh && (lossAdviceAttempt == "" ||
                !(Observer.Status == "SETTLED" && Observer.Outcome == "LOSS" || ownedEliteContinuation) || Observer.AttemptId != lossAdviceAttempt)))
                return "loss_advice_origin_unavailable";
            if (!object.ReferenceEquals(campaign, lossAdviceCampaign) || !object.ReferenceEquals(currentRoot, lossAdviceRoot) ||
                !object.ReferenceEquals(common.Character, lossAdviceCharacter) || characterId != lossAdviceCharacterId ||
                campaign == null || Int(Read(campaign, "m_campaignID")) != lossAdviceCampaignId || !OwnedBy(campaign, currentRoot))
                return "loss_advice_identity_changed";
            if (scenario != null || function != null || result != null || reward != null || chapter != null || formation != null || panel != null || Visible(battle))
                return "loss_advice_conflicting_ui";
            if (Read(lossAdvice, "title_string") as string != "ui_biaoti2" || !object.ReferenceEquals(ReadStatic(mainType, "m_subui"), lossAdvice) ||
                Read(lossAdvice, "OnClose") != null)
                return "loss_advice_close_unavailable";
            if (!Enabled(lossAdvice)) return "WAITING_LOSS_ADVICE_INPUT";
            if ((DateTime.Now - (DateTime)Read(lossAdvice, "visible_time")).TotalSeconds < 0.3) return "WAITING_LOSS_ADVICE_ANIMATION";
            return "PASS";
        }
        private bool NormalRewardAcknowledgement()
        {
            // The ordinary reward OK branch is Hide -> OnClose, even while
            // Global.extern_enabled disables the underlying campaign root.
            return NormalRewardPage() && ((Observer.CampaignId == campaignId && Observer.TroopId == troopId && Observer.AppliedTo(frame)) ||
                FreshResultNavigation() == "ack_result");
        }
        private bool NormalRewardPage()
        {
            return Phase == "RESULT" && reward != null && result == null && scenario == null && chapter == null &&
                Enabled(reward) && campaignId > 0 && troopId > 0 &&
                Read(reward, "mine_ui") as string == "" && Bool(Read(reward, "m_bCampaign")) &&
                Int(Read(reward, "m_troopID")) == troopId && RewardCallback(reward) && battle != null &&
                Int(Read(battle, "idcampaign")) == campaignId && Int(Read(battle, "idtroop")) == troopId &&
                !Bool(Read(battle, "IsCrossServer")) && !Bool(Read(battle, "IsSurrended")) &&
                !Bool(Read(battle, "bNeedWaitForBattleEnd")) && !Bool(Read(battle, "bUseBattleEnd")) &&
                !Bool(Read(battle, "BattleFighting")) && Read(battle, "ViewType").ToString() != "viewReport";
        }
        private bool CurrentGameIdle()
        {
            return frame != null && frame.QueueObserved && frame.QueueClient != null && frame.QueueError == null &&
                !frame.RefreshPending && !frame.FinalPending && frame.QueuedPackets != null && frame.QueuedPackets.Length == 0 && frame.Packet == null &&
                (battle == null || (!Bool(Read(battle, "BattleFighting")) && !Bool(Read(battle, "bNeedWaitForBattleEnd")) && !Bool(Read(battle, "bUseBattleEnd"))));
        }
        private string FreshResultNavigation()
        {
            // Closing a currently visible ordinary page does not reconstruct a
            // battle, settle the observer or add a loss to this script's run.
            if (Phase != "RESULT" || contextGate != "PASS" || Observer.Status != "NONE" || Observer.AttemptId != "" || Observer.Pending ||
                !CurrentGameIdle() || campaign == null || common.Character == null || characterId == "0" || Bool(Read(common.Character, "gm")) ||
                Bool(Read(campaign, "m_bDebug")) || !OwnedBy(campaign, currentRoot) || scenario != null || function != null || lossAdvice != null ||
                formation != null || panel != null || !object.ReferenceEquals(navigationRoot, currentRoot) ||
                !object.ReferenceEquals(navigationCharacter, common.Character) || navigationCharacterId != characterId)
                return "";
            object data = Read(campaign, "m_campaign");
            if (data == null || Int(Read(data, "idcampaign_type")) != AllowedCampaignType || !Bool(Read(data, "unlocked"))) return "";
            if (chapter != null && result == null && reward == null && object.ReferenceEquals(navigationPage, chapter) &&
                Enabled(chapter) && Bool(Read(chapter, "m_bAnecdote")) == challengeOnly && globalUi != null && Read(chapter, "rmg") != null)
                return "next_chapter";
            if (battle == null || campaignId < 1 || troopId < 1 || Int(Read(campaign, "m_campaignID")) != campaignId || !BattleOwned(battle) ||
                Bool(Read(battle, "IsCrossServer")) || Bool(Read(battle, "IsSurrended")) || Read(battle, "ViewType").ToString() == "viewReport") return "";
            if (result != null && reward == null && chapter == null && object.ReferenceEquals(navigationPage, result) && Enabled(result) &&
                frame.ResultVisible && ULong(Read(result, "m_reportid")) == ULong(Read(battle, "ReportID")) &&
                Int(Read(result, "m_battleLevel")) == Int(Read(battle, "BattleResultLevel")) &&
                !Bool(Read(result, "draw_popup")) && !Bool(Read(result, "draw_url_box"))) return "ack_result";
            return object.ReferenceEquals(navigationPage, reward) && NormalRewardPage() ? "ack_result" : "";
        }
        private string RootLevel(Component root)
        {
            if (!ValidRoot(root)) return "";
            string level = Read(root, "LevelID") as string;
            return level == "Campaign" || level == "Capital" || level == "Anecdote" || level == "Fairy" || level == "TalentCampaign" || level == "Battle" || level == "Root" ? level : "OTHER";
        }
        private void CampaignDiagnostics(UObject[] all)
        {
            StringBuilder b = new StringBuilder("["); int listed = 0, total = 0;
            for (int i = 0; i < all.Length; i++)
            {
                Component c = all[i] as Component; if (c == null || c.GetType() != campaignType) continue;
                total++; if (listed >= 8) continue;
                Component owner = Read(c, "rmg") as Component;
                if (listed++ != 0) b.Append(',');
                b.Append("{\"instance_id\":"); b.Append(c.GetInstanceID());
                b.Append(",\"visible\":"); b.Append(Visible(c) ? "true" : "false");
                b.Append(",\"owner_matches_current\":"); b.Append(currentRoot != null && object.ReferenceEquals(owner, currentRoot) ? "true" : "false");
                b.Append(",\"hierarchy_matches_current\":"); b.Append(currentRoot != null && c.transform.root == currentRoot.transform ? "true" : "false");
                b.Append(",\"owner_root_level\":"); b.Append(Command.Quote(RootLevel(owner))); b.Append('}');
            }
            b.Append(']'); candidateDiagnostics = b.ToString(); candidatesTruncated = total > listed;
        }
        private string ContextJson()
        {
            StringBuilder counts = new StringBuilder("{"); bool comma = false;
            foreach (string role in new string[] { "root_ui", "campaign", "battle", "battle_baseline", "troop", "formation", "chapter", "scenario", "function", "function_scenario", "notification", "battle_guide", "normal_result", "reward", "challenge_list", "challenge_backdrop" })
            {
                int[] c = selectionCounts[role] as int[]; if (c == null) continue;
                if (comma) counts.Append(','); comma = true;
                counts.Append(Command.Quote(role)); counts.Append(":{\"total\":"); counts.Append(c[0]); counts.Append(",\"visible\":"); counts.Append(c[1]); counts.Append(",\"matching\":"); counts.Append(c[2]); counts.Append('}');
            }
            counts.Append('}');
            return "{\"current_root_instance_id\":" + (currentRoot == null ? 0 : currentRoot.GetInstanceID()) + ",\"current_root_level\":" + Command.Quote(RootLevel(currentRoot)) +
                ",\"current_root_active\":" + (currentRoot != null && currentRoot.gameObject.activeInHierarchy ? "true" : "false") +
                ",\"global_input_enabled\":" + (globalInputEnabled ? "true" : "false") + ",\"selection_gate\":" + Command.Quote(contextGate) +
                ",\"counts\":" + counts.ToString() + ",\"campaign_candidates\":" + candidateDiagnostics + ",\"candidates_truncated\":" + (candidatesTruncated ? "true" : "false") + "}";
        }
        internal string Json() { return "{\"schema\":1,\"interface\":\"scoped-campaign-native-flow-v2\",\"revision\":" + Revision + "," + ContentJson() + ",\"settlement_diagnostics\":" + SettlementDiagnostics() + "}"; }
        internal string CommandJson() { return lastCommand; }
        internal string BusinessJson()
        {
            string battleBusiness = Observer.BusinessJson();
            int boundary = battleBusiness.IndexOf(",\"mutation_attempted\":");
            if (boundary < 0) throw new ProbeFailure("campaign_business_identity_schema");
            string identity = Observer.AttemptId == "" && general.BoundCharacterId != "" ? "{\"character_id\":" + Command.Quote(general.BoundCharacterId) : battleBusiness.Substring(0, boundary);
            return identity + ",\"mutation_attempted\":" + (Observer.MutationAttempted || general.MutationAttempted ? "true" : "false") +
                ",\"pending_attempt\":" + (Observer.Pending ? "true" : "false") + ",\"resource_rollback\":false,\"attempt\":" + Observer.Json() + "}";
        }
        private string ContentJson()
        {
            return "\"mode\":" + Command.Quote(eliteOnly ? "ELITE_CHALLENGE" : challengeOnly ? "NORMAL_CHALLENGE" : "MAINLINE") +
                (eliteOnly ? ",\"elite_interface\":\"elite-type4-current-balance-v2\",\"elite_star\":" + (campaign == null || Read(campaign, "m_campaign") == null ? 0 : Int(Read(Read(campaign, "m_campaign"), "star_level"))) + ",\"elite_quota\":" + EliteChallengeQuota.Json(common.Character) : "") +
                ",\"campaign_type\":" + (frame == null || frame.CampaignData == null ? 0 : frame.CampaignType) + ",\"challenge_candidates\":" + (challengeOnly ? challengeCandidates : "[]") + ",\"available\":" + (Available ? "true" : "false") + ",\"gate\":" + Command.Quote(Gate) + ",\"phase\":" + Command.Quote(Phase) +
                ",\"character_id\":" + Command.Quote(characterId) +
                ",\"user_id\":" + Command.Quote(Convert.ToUInt64(ReadStatic(globalType, "userid"), CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)) +
                ",\"zone_id\":" + Command.Quote(Convert.ToUInt32(ReadStatic(globalType, "zoneid"), CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)) +
                ",\"campaign_id\":" + campaignId + ",\"troop_id\":" + troopId + ",\"entry\":" + ChallengeEntryJson() + ",\"home_navigation\":" + HomeNavigationJson() + ",\"challenge_resources\":" + ChallengeResourcesJson() + ",\"result_kind\":" + Command.Quote(chapter != null ? "CHAPTER" : reward != null ? "REWARD" : result != null ? "NORMAL" : "") + ",\"result_navigation\":" + Command.Quote(FreshResultNavigation()) + ",\"eligible_troops\":" + eligible + ",\"tutorial\":" + (general.Active ? general.TutorialJson() : tutorial) + ",\"general_tutorial\":" + general.Json() + ",\"flow\":" + FlowJson() + ",\"attempt\":" + Observer.Json() + ",\"ui_context\":" + ContextJson() + ",\"selection_diagnostics\":" + selectionDiagnostics +
                ",\"pending_story_attempt_id\":" + Command.Quote(OwnedPendingStory() ? Observer.AttemptId : "") +
                ",\"pending_story_continuation\":" + Command.Quote(OwnedPendingStory() ? ScenarioContinuation() : "");
        }
        private bool OwnedPendingStory()
        {
            return Observer.Pending && scenario != null && common != null &&
                BattleLaunch.OwnsScenario(scenario, Observer.AttemptId, common.Character, campaign, TargetTroop(Observer.TroopId));
        }
        private string FlowJson()
        {
            if (challengeOnly && general.Active) return FlowState("UNSUPPORTED", "", "challenge_general_tutorial_unsupported");
            if (!Available)
            {
                string waiting = NativeTransitionWait();
                return FlowState(waiting == "" ? "UNSUPPORTED" : "WAIT", "", waiting == "" ? Gate : waiting);
            }
            if (general.Active)
            {
                if (general.Gate.StartsWith("WAITING_")) return FlowState("WAIT", "", general.Gate);
                return general.Gate == "PASS" ? FlowState("ACTION", general.NextAction, "") : FlowState("UNSUPPORTED", "", general.Gate);
            }
            if (Phase == "SCENARIO")
            {
                string gate = capturedPassiveGate;
                if (gate.StartsWith("WAITING_")) return FlowState("WAIT", "", gate);
                return gate == "PASS" ? FlowState("ACTION", "continue", "") : FlowState("UNSUPPORTED", "", gate == "" ? "passive_flow_unknown" : gate);
            }
            if (Phase == "CAMPAIGN")
            {
                // MainLevelOK moves this stock callback from the campaign to
                // RootBase's UI queue before ChapterClear starts loading its
                // page. An interactive map in that gap is not cleanup idle.
                if (EliteChapterContinuationPending()) return FlowState("WAIT", "", "WAITING_ELITE_CHAPTER_CLEAR");
                string waiting = SelectionTransitionGate();
                if (waiting != "PASS") return FlowState("WAIT", "", waiting);
            }
            return FlowState("IDLE", "", "");
        }
        private bool EliteChapterContinuationPending()
        {
            if (!eliteOnly || contextGate != "PASS" || Observer.Status == "UNKNOWN" ||
                campaign == null || campaign.GetType() != campaignType || currentRoot == null ||
                !currentRoot.gameObject.activeInHierarchy || RootLevel(currentRoot) != AllowedRoot ||
                !OwnedBy(campaign, currentRoot) || common.Character == null || characterId == "0") return false;
            object data = Read(campaign, "m_campaign");
            if (data == null || Int(Read(data, "idcampaign_type")) != 4 || !Bool(Read(data, "unlocked")) ||
                Bool(Read(campaign, "m_bDebug")) || Bool(Read(common.Character, "gm"))) return false;
            MethodInfo continuation = campaignType.GetMethod("ChapterClear", Declared, null, Type.EmptyTypes, null);
            if (continuation == null || continuation.ReturnType != typeof(void) || continuation.IsGenericMethod)
                throw new ProbeFailure("elite_chapter_continuation_schema");
            Delegate source = Read(campaign, "m_chapterClearFunc") as Delegate;
            if (source != null && object.ReferenceEquals(source.Target, campaign) && source.Method.Equals(continuation)) return true;
            IList queued = ReadStatic(Model.FirstType("RootBase"), "vctUIFuncs") as IList;
            if (queued == null) throw new ProbeFailure("elite_chapter_continuation_queue_schema");
            for (int i = 0; i < queued.Count; i++)
            {
                Delegate callback = queued[i] as Delegate;
                if (callback != null && object.ReferenceEquals(callback.Target, campaign) && callback.Method.Equals(continuation)) return true;
            }
            return false;
        }
        private static string FlowState(string status, string action, string reason)
        { return "{\"status\":" + Command.Quote(status) + ",\"action\":" + Command.Quote(action) + ",\"reason\":" + Command.Quote(reason) + "}"; }
        private string NativeTransitionWait()
        {
            // Only a proven current context can turn a blocked command into a
            // bounded wait. Unknown identities and business gates stay blocked.
            if (contextGate != "PASS" || Observer.Status == "UNKNOWN" || currentRoot == null || !currentRoot.gameObject.activeInHierarchy) return "";
            if (Gate == "WAITING_CHALLENGE_ENTER" || Gate == "WAITING_CHALLENGE_MAP" || Gate == "WAITING_CHALLENGE_SOURCE_RETIREMENT") return Gate;
            if (Gate == "global_input_disabled")
            {
                if (Phase == "MAIN" || Phase == "GENERAL_TUTORIAL" || Phase == "CHALLENGE_LIST") return Gate;
            }
            if (challengeOnly && Phase == "CHALLENGE_LIST" && (Gate == "challenge_list_loading" || Gate == "challenge_list_network_pending")) return Gate;
            object data = campaign == null ? null : Read(campaign, "m_campaign");
            if (data == null || !OwnedBy(campaign, currentRoot) || RootLevel(currentRoot) != AllowedRoot ||
                Int(Read(data, "idcampaign_type")) != AllowedCampaignType || !Bool(Read(data, "unlocked")) || Bool(Read(campaign, "m_bDebug"))) return "";
            if (!challengeOnly && Phase == "GENERAL_TUTORIAL" && Gate == "general_current_root_loading" &&
                (general.Gate == "PASS" || general.Gate == "WAITING_UI" || general.Gate == "WAITING_NETWORK"))
                return "WAITING_GENERAL_ROOT";
            if (Gate == "global_input_disabled" || Gate == "campaign_loading" || Gate == "formation_loading" || Gate == "campaign_map_not_interactive") return Gate;
            if (capitalDemonTransition && Phase == "CAMPAIGN" && Gate == "campaign_visible_unknown_ui:DemonScript" &&
                (!Enabled(campaign) || Bool(Read(campaign, "loading")) || common.Gate == "loading_or_battle"))
                return "WAITING_CAPITAL_SCENE_RETIREMENT";
            if ((Phase == "SCENARIO" || Phase == "CAMPAIGN") && Gate == "campaign_visible_unknown_ui:AnimEffect") return Gate;
            return "";
        }
        // Fixed current-frame evidence only. Never feeds settlement or action gates.
        // This raw diagnostic schema does not expose the observer's target-data anchor.
        private string SettlementDiagnostics()
        {
            StringBuilder json = new StringBuilder("{\"anchor_scope\":\"this_helper_instance\",\"character_id\":");
            Component parent = battle == null ? null : Read(battle, "m_parentUI") as Component;
            object interpreter = battle == null ? null : Read(battle, "interpreter");
            CampaignFrame debug = new CampaignFrame { Campaign = battle == null ? campaign : parent, Battle = battle,
                Character = common == null ? null : common.Character, CharacterId = characterId,
                ReportToken = interpreter == null ? null : Read(interpreter, "packet") };
            json.Append(Command.Quote(characterId)); json.Append(",\"observer\":"); json.Append(Observer.DebugJson(debug));
            json.Append(",\"battle\":");
            if (battle == null) json.Append("null");
            else
            {
                json.Append("{\"instance_id\":"); json.Append(battle.GetInstanceID());
                DiagnosticNumber(json, "parent_instance_id", parent == null ? 0 : parent.GetInstanceID());
                Component owner = OwnerRoot(battle), previous = Read(battle, "m_prevUI") as Component;
                DiagnosticNumber(json, "owner_root_instance_id", owner == null ? 0 : owner.GetInstanceID());
                DiagnosticNumber(json, "previous_root_instance_id", previous == null ? 0 : previous.GetInstanceID());
                DiagnosticBool(json, "visible", Visible(battle));
                DiagnosticBool(json, "parent_matches_campaign", parent != null && object.ReferenceEquals(parent, campaign));
                DiagnosticBool(json, "parent_owned_by_current", parent != null && OwnedBy(parent, currentRoot));
                DiagnosticBool(json, "previous_root_matches_current", currentRoot != null && object.ReferenceEquals(Read(battle, "m_prevUI"), currentRoot));
                json.Append(",\"owner_root_level\":"); json.Append(Command.Quote(RootLevel(OwnerRoot(battle))));
                DiagnosticNumber(json, "idcampaign", Read(battle, "idcampaign")); DiagnosticNumber(json, "idtroop", Read(battle, "idtroop"));
                json.Append(",\"report_id\":"); json.Append(Command.Quote(ULong(Read(battle, "ReportID")).ToString(CultureInfo.InvariantCulture)));
                json.Append(",\"report_result_status\":"); json.Append(debug.ReportToken == null ? "null" : Int(Read(debug.ReportToken, "result_status")).ToString(CultureInfo.InvariantCulture));
                DiagnosticBool(json, "show_defender_result", Bool(Read(battle, "InvertDisplaySide")));
                DiagnosticNumber(json, "battle_result_level", Read(battle, "BattleResultLevel"));
                object view = Read(battle, "ViewType"); DiagnosticNumber(json, "view_type", view);
                DiagnosticBool(json, "replay", view.ToString() == "viewReport");
                DiagnosticBool(json, "cross", Bool(Read(battle, "IsCrossServer"))); DiagnosticBool(json, "surrender", Bool(Read(battle, "IsSurrended")));
                DiagnosticBool(json, "waiting", Bool(Read(battle, "bNeedWaitForBattleEnd"))); DiagnosticBool(json, "use_end", Bool(Read(battle, "bUseBattleEnd")));
                DiagnosticBool(json, "fighting", Bool(Read(battle, "BattleFighting")));
                double remaining = Convert.ToDouble(Read(battle, "m_BattleLength"), CultureInfo.InvariantCulture);
                json.Append(",\"playback_remaining\":"); json.Append(Double.IsNaN(remaining) || Double.IsInfinity(remaining) ? "null" : remaining.ToString("R", CultureInfo.InvariantCulture));
                object pending = Read(battle, "m_pktBattleEnd"), packet = pending == null ? null : Read(pending, "pkt");
                DiagnosticBool(json, "pending_present", pending != null); json.Append(",\"packet\":");
                if (packet == null) json.Append("null");
                else
                {
                    json.Append("{\"result\":"); json.Append(Convert.ToUInt32(Read(packet, "result"), CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)); DiagnosticNumber(json, "type", Read(packet, "type"));
                    DiagnosticBool(json, "iswin", Bool(Read(packet, "iswin"))); DiagnosticNumber(json, "idcampaign", Read(packet, "idcampaign")); DiagnosticNumber(json, "idtroop", Read(packet, "idtroop"));
                    json.Append(",\"report_id\":"); json.Append(Command.Quote(ULong(Read(packet, "idbattle_report")).ToString(CultureInfo.InvariantCulture))); json.Append('}');
                }
                json.Append('}');
            }
            json.Append(",\"result\":");
            if (result == null) json.Append("null");
            else
            {
                json.Append("{\"instance_id\":"); json.Append(result.GetInstanceID());
                json.Append(",\"report_id\":"); json.Append(Command.Quote(ULong(Read(result, "m_reportid")).ToString(CultureInfo.InvariantCulture)));
                DiagnosticNumber(json, "battle_level", Read(result, "m_battleLevel"));
                DiagnosticBool(json, "battle_match", battle != null && object.ReferenceEquals(Read(result, "battle_ui"), battle));
                DiagnosticBool(json, "root_match", battle != null && OwnedBy(result, OwnerRoot(battle)));
                DiagnosticBool(json, "battle_view", Bool(Read(result, "m_battle_view_ui"))); json.Append('}');
            }
            json.Append(",\"target\":");
            IDictionary troops = parent == null ? null : Read(parent, "m_troops") as IDictionary;
            int id = battle == null ? 0 : Int(Read(battle, "idtroop"));
            object target = troops == null || id < 1 || id > 255 ? null : troops[(byte)id];
            object campaignData = parent == null ? null : Read(parent, "m_campaign");
            if (target == null) json.Append("null");
            else
            {
                json.Append("{\"idcampaign\":"); json.Append(Int(Read(target, "idcampaign"))); DiagnosticNumber(json, "idtroop", Read(target, "idtroop"));
                DiagnosticBool(json, "matches_battle_target", Int(Read(target, "idcampaign")) == Int(Read(battle, "idcampaign")) && Int(Read(target, "idtroop")) == id);
                DiagnosticNumber(json, "finish_count", Read(target, "finish_count")); DiagnosticNumber(json, "finish_max", Read(target, "finish_max"));
                DiagnosticBool(json, "killed", Bool(Read(target, "killed"))); DiagnosticBool(json, "canattack", Bool(Read(target, "canattack")));
                json.Append(",\"campaign_finished\":"); json.Append(campaignData == null ? "null" : Bool(Read(campaignData, "finished")) ? "true" : "false");
                DiagnosticBool(json, "data_present", Read(target, "data_") != null); json.Append(",\"data_anchor_match\":null}");
            }
            json.Append('}'); return json.ToString();
        }
        private string Eligible()
        {
            if (campaign == null) return "[]";
            IDictionary troops = Read(campaign, "m_troops") as IDictionary;
            if (troops == null || troops.Count > 255) throw new ProbeFailure("campaign_troop_schema");
            StringBuilder b = new StringBuilder("["); bool comma = false;
            for (int id = 1; id <= 255; id++)
            {
                object t = troops[(byte)id]; if (t == null) continue;
                bool visible = Bool(Read(t, "visible")), can = Bool(Read(t, "canattack")), killed = Bool(Read(t, "killed"));
                if (!visible || !can || killed) continue;
                if (comma) b.Append(','); comma = true;
                b.Append("{\"troop_id\":"); b.Append(id); b.Append(",\"canattack\":true,\"visible\":true,\"killed\":false,\"challenge_level\":");
                b.Append(Int(Read(t, "chanllege_level"))); b.Append('}');
            }
            b.Append(']'); return b.ToString();
        }
        private CampaignFrame BattleFrame()
        {
            CampaignFrame f = new CampaignFrame(); f.Campaign = campaign; f.Battle = battle; f.Character = common.Character; f.CharacterId = characterId;
            f.CampaignId = campaignId; f.TroopId = troopId; f.Scene = currentRoot; f.SampleTick = Environment.TickCount;
            if (battle != null) { f.BattleVisible = Visible(battle); f.ReportId = ULong(Read(battle, "ReportID")); }
            if (f.BattleVisible)
            {
                f.Campaign = Read(battle, "m_parentUI"); f.CampaignId = Int(Read(battle, "idcampaign")); f.TroopId = Int(Read(battle, "idtroop"));
                f.Fighting = Bool(Read(battle, "BattleFighting")); f.Surrendered = Bool(Read(battle, "IsSurrended"));
                f.Replay = Read(battle, "ViewType").ToString() == "viewReport" || Bool(Read(battle, "IsCrossServer"));
                f.Waiting = Bool(Read(battle, "bNeedWaitForBattleEnd")); f.UseEnd = Bool(Read(battle, "bUseBattleEnd"));
                object interpreter = Read(battle, "interpreter"); f.ReportToken = interpreter == null ? null : Read(interpreter, "packet");
                if (f.ReportToken != null) f.ReportResultStatus = Int(Read(f.ReportToken, "result_status"));
                f.ShowDefenderResult = Bool(Read(battle, "InvertDisplaySide")); f.BattleResultLevel = Int(Read(battle, "BattleResultLevel"));
                f.PlaybackRemaining = Convert.ToDouble(Read(battle, "m_BattleLength"), CultureInfo.InvariantCulture);
            }
            f.CampaignData = f.Campaign == null ? null : Read(f.Campaign, "m_campaign");
            if (f.CampaignData != null) { f.CampaignType = Int(Read(f.CampaignData, "idcampaign_type")); f.CampaignFinished = Bool(Read(f.CampaignData, "finished")); }
            f.Debug = f.Campaign != null && Bool(Read(f.Campaign, "m_bDebug"));
            f.Gm = f.Character != null && Bool(Read(f.Character, "gm"));
            IDictionary troops = f.Campaign == null ? null : Read(f.Campaign, "m_troops") as IDictionary;
            // sc_campaign_battle clears the selection before BattleUI.Show.
            // The attempt's original troop remains the progress anchor while
            // that hidden loading transition is in flight.
            int targetId = Observer.Pending ? Observer.TroopId : f.TroopId;
            int targetCampaign = Observer.Pending ? Observer.CampaignId : f.CampaignId;
            f.Target = troops == null || targetId < 1 || targetId > 255 ? null : troops[(byte)targetId];
            if (f.Target != null)
            {
                f.TargetData = Read(f.Target, "data_"); f.FinishCount = Int(Read(f.Target, "finish_count")); f.FinishMax = Int(Read(f.Target, "finish_max")); f.Killed = Bool(Read(f.Target, "killed"));
                if (Int(Read(f.Target, "idcampaign")) != targetCampaign || Int(Read(f.Target, "idtroop")) != targetId) f.Target = null;
            }
            SampleSettlementQueue(f);
            object pending = battle == null ? null : Read(battle, "m_pktBattleEnd");
            object packet = pending == null ? null : Read(pending, "pkt");
            if (packet != null) f.Packet = SettlementPacket(packet);
            f.ResultVisible = result != null && object.ReferenceEquals(Read(result, "battle_ui"), battle) && !Bool(Read(result, "m_battle_view_ui"));
            f.ResultUi = result; f.ResultReportId = result == null ? 0 : ULong(Read(result, "m_reportid"));
            f.ResultBattleLevel = result == null ? -1 : Int(Read(result, "m_battleLevel"));
            SampleAppliedLossReceipt(f); return f;
        }
        private void SampleAppliedLossReceipt(CampaignFrame f)
        {
            if (eliteOnly || f.Battle == null || f.ReportToken == null || f.Campaign == null || f.Target == null ||
                String.IsNullOrEmpty(Observer.AttemptId) || f.CampaignType != AllowedCampaignType ||
                Observer.CampaignId != f.CampaignId || Observer.TroopId != f.TroopId) return;
            string operationId = Observer.AttemptId;
            BattlePresentationOwnership ownership = new BattlePresentationOwnership(NativeMode, operationId,
                f.Campaign as Component, f.Target, f.CampaignId + ":" + f.TroopId,
                delegate { return Observer.AttemptId == operationId && Observer.CampaignId == f.CampaignId &&
                    Observer.TroopId == f.TroopId; });
            byte[] body = BattleSettlement.AppliedCampaignLossResponse(f.Battle as Component, f.ReportToken, ownership);
            if (body == null) return;
            try
            {
                // Detached original bytes were retained only after the exact
                // stock callback returned and its post-apply identity matched.
                f.AppliedLossReceipt = SettlementPacket(BattleReplyPreflight.Deserialize(body,
                    "game.net.data.StoC.SCBattleEnd_0x408"));
            }
            catch (Exception) { if (f.QueueError == null) f.QueueError = "applied_loss_receipt_parse_failed"; }
        }
        private static CampaignPacket SettlementPacket(object packet)
        {
            // Invalid UInt32 values remain invalid sentinels instead of throwing
            // a conversion exception that would tear down the helper.
            return new CampaignPacket { Result = PacketInt(Read(packet, "result")), Type = PacketInt(Read(packet, "type")), IsWin = Bool(Read(packet, "iswin")), CampaignId = PacketInt(Read(packet, "idcampaign")), TroopId = PacketInt(Read(packet, "idtroop")), ReportId = ULong(Read(packet, "idbattle_report")) };
        }
        private static int PacketInt(object value)
        { uint number = Convert.ToUInt32(value, CultureInfo.InvariantCulture); return number <= Int32.MaxValue ? (int)number : -1; }
        private void SampleSettlementQueue(CampaignFrame f)
        {
            f.QueueClient = ReadStatic(rootType, "m_client");
            object[] tokens = new object[8]; byte[][] copies = new byte[8][]; int captured = 0;
            bool ownedEarlyEnd = false;
            if (f.QueueClient == null || !Monitor.TryEnter(f.QueueClient, 0)) return;
            try
            {
                // Parse and Run use this exact client monitor. Never consume,
                // seek, close, remove, register or replace a game-owned object.
                IList messages = Read(f.QueueClient, "messages") as IList;
                IDictionary handlers = Read(f.QueueClient, "m_handlers") as IDictionary;
                if (messages == null || handlers == null || messages.Count > 64) { f.QueueError = "queue_schema_or_bound"; return; }
                f.RefreshPending = handlers.Contains((ushort)1025) || handlers.Contains((ushort)979);
                f.FinalPending = handlers.Contains((ushort)1032);
                ownedEarlyEnd = BattleSettlement.OwnsPending(f.QueueClient, battle, f.ReportToken);
                for (int i = 0; i < messages.Count; i++)
                {
                    object message = messages[i]; int op = Int(Read(message, "op"));
                    if (op == 1025 || op == 979) f.RefreshPending = true;
                    if (op != 1032) continue;
                    MemoryStream stream = Read(message, "stream") as MemoryStream;
                    if (captured == tokens.Length || stream == null || !stream.CanRead || stream.Position != 0 || stream.Length < 1 || stream.Length > 65536)
                    { f.QueueError = "queue_stream_or_bound"; return; }
                    tokens[captured] = message; copies[captured] = stream.ToArray(); captured++;
                }
                f.QueueObserved = true;
            }
            catch (Exception) { f.QueueError = "queue_copy_failed"; return; }
            finally { Monitor.Exit(f.QueueClient); }
            try
            {
                CampaignQueuedPacket[] packets = new CampaignQueuedPacket[captured];
                if (captured != 0)
                {
                    // Fixed pinned protobuf API, operating only on detached bytes.
                    Type packetType = battleType.GetField("m_pktBattleEnd", Declared).FieldType.GetField("pkt", Declared).FieldType;
                    object serializer = ReadStatic(Model.FirstType("JSProtoBuf"), "wrapper");
                    MethodInfo parse = null;
                    foreach (MethodInfo method in serializer.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    {
                        ParameterInfo[] parameters = method.GetParameters();
                        if (method.Name != "Deserialize" || !method.IsGenericMethodDefinition || method.GetGenericArguments().Length != 1 || parameters.Length != 1 || parameters[0].ParameterType != typeof(Stream) || !method.ReturnType.IsGenericParameter) continue;
                        if (parse != null) throw new ProbeFailure("settlement_parser_ambiguous"); parse = method;
                    }
                    if (parse == null) throw new ProbeFailure("settlement_parser_missing");
                    parse = parse.MakeGenericMethod(new Type[] { packetType });
                    for (int i = 0; i < captured; i++)
                        using (MemoryStream copy = new MemoryStream(copies[i], false))
                            packets[i] = new CampaignQueuedPacket { Token = tokens[i], Packet = SettlementPacket(parse.Invoke(serializer, new object[] { copy })),
                                RetryableRejection = ownedEarlyEnd };
                }
                f.QueuedPackets = packets;
            }
            catch (Exception) { f.QueueError = "queue_packet_parse_failed"; }
        }
        private bool BattleChrome(Behaviour component)
        {
            if (battle == null || (Phase != "BATTLE" && Phase != "RESULT") || component.gameObject != battle.gameObject) return false;
            string name = component.GetType().FullName;
            string field = name == "BattleTopUI" ? "top_ui" : name == "BattleSpiritTopUI" ? "spirit_top_ui" : name == "BattleWoodwarTopUI" ? "wood_top_ui" : null;
            return field != null && component.GetType() == Model.GameType(name) && object.ReferenceEquals(Read(battle, field), component);
        }
        private bool CampaignChrome(Behaviour component)
        {
            if (campaign == null) return false;
            string name = component.GetType().FullName;
            if (challengeOnly && name == "AnecdoteUI")
                return component.GetType() == Model.GameType("AnecdoteUI") && component.gameObject == campaign.gameObject && OwnedBy(component, currentRoot);
            if (name != "CampaignTroopScript" && name != "CampaignSignScript" && name != "CampaignChestScript") return false;
            return component.GetType() == Model.GameType(name) && object.ReferenceEquals(Read(component, "ParentUI"), campaign) &&
                (!challengeOnly || OwnedBy(campaign, currentRoot) && component.transform.root == currentRoot.transform);
        }

        private bool RetiredCapitalDemon(Behaviour component, UObject[] all)
        {
            // A city actor belongs only to the loader's exact old Capital root;
            // its type alone never identifies harmless campaign chrome.
            if (challengeOnly || component.GetType() != Model.GameType("DemonScript") || Phase != "CAMPAIGN" ||
                contextGate != "PASS" || RootLevel(currentRoot) != "Campaign" || campaign == null ||
                !OwnedBy(campaign, currentRoot) || campaignId < 1 || common.Character == null ||
                panel != null || formation != null || result != null || reward != null || chapter != null || Visible(battle)) return false;
            object data = Read(campaign, "m_campaign");
            if (data == null || Int(Read(data, "idcampaign_type")) != 0 || !Bool(Read(data, "unlocked"))) return false;
            Component loader = SharedDevelopmentContext.Static(Model.FirstType("BaseAsset"), "lds") as Component;
            if (loader == null || loader.GetType() != Model.FirstType("LoadScreen") || !loader.gameObject.activeInHierarchy) return false;
            Component oldRoot = Read(loader, "oldrmg") as Component;
            if (oldRoot == null || object.ReferenceEquals(oldRoot, currentRoot) || RootLevel(oldRoot) != "Capital") return false;
            Component parent = Read(component, "ParentUI") as Component;
            if (parent == null || parent.GetType() != Model.GameType("CapitalUI") || component.transform.root != oldRoot.transform ||
                parent.transform.root != oldRoot.transform || !object.ReferenceEquals(Read(parent, "rmg"), oldRoot)) return false;
            capitalDemonTransition = true;
            try { return object.ReferenceEquals(component, SharedDevelopmentContext.CapitalDemon(all, oldRoot, common.Character)); }
            catch (ProbeFailure error)
            {
                if (error.Message != "flower_capital_demon_identity") throw;
                return false;
            }
        }

        internal bool Execute(Command command, CampaignPublication publishBeforeInvoke, out string why)
        {
            why = ""; bool invoked = false;
            try
            {
                string action = command.CampaignAction;
                if (!CheckCommand(command, out why)) return false;
                if (challengeOnly && HomeNavigationAction(action))
                    return ExecuteHomeNavigation(command, publishBeforeInvoke, out invoked, out why);
                if (GeneralTutorialModel.Action(action)) { why = "general_requires_owned_gui_context"; return false; }
                if (challengeOnly && (action == "enter" || action == "enter_challenge" || action == "next_chapter"))
                    return ExecuteChallengeNavigation(command, publishBeforeInvoke, out invoked, out why);
                if (action == "enter")
                {
                    if (Phase != "MAIN" || !common.Safe || !common.InteractionEnabled || !Bool(Invoke(common.Main, "_campaign_check", new Type[] { typeof(string) }, new object[] { "Campaign" }))) { why = "campaign_entry_gate"; return false; }
                    invoked = true; BeforeInvoke(publishBeforeInvoke); Invoke(common.Main, "_campaign", Type.EmptyTypes, new object[0]);
                }
                else if (action == "select")
                {
                    object t = TargetTroop(command.TroopId);
                    string selection = SelectionGate(t);
                    if (selection != "PASS") { why = "normal_troop_selection_gate:" + selection; return false; }
                    invoked = true; BeforeInvoke(publishBeforeInvoke); Invoke(campaign, "wantCombat", new Type[] { typeof(byte) }, new object[] { (byte)command.TroopId });
                }
                else if (action == "prepare")
                {
                    object t = TargetTroop(command.TroopId);
                    if (Phase != "TROOP" || !Enabled(panel) || !object.ReferenceEquals(Read(panel, "m_parent"), campaign) || !object.ReferenceEquals(Read(panel, "m_troop"), t) || !NormalTroop(t))
                    { why = "formation_prepare_gate"; return false; }
                    int count = Int(Read(t, "limit_count"));
                    if (count != -1 && count <= 0) { why = "formation_prepare_gate:limit_count"; return false; }
                    // Exact ordinary btnAttack branch for unfinished type=0.
                    invoked = true; BeforeInvoke(publishBeforeInvoke); GuideFormationClear(); Invoke(panel, "BattleFormation", new Type[] { typeof(bool), typeof(byte) }, new object[] { true, (byte)command.TroopId });
                }
                else if (action == "start")
                {
                    object t = TargetTroop(command.TroopId); Delegate attack = formation == null ? null : Read(formation, "OnAttack") as Delegate;
                    if (Phase != "FORMATION" || command.TroopId != troopId || !Enabled(formation) || !NormalTroop(t) || Bool(Read(formation, "viewonly")) || !Bool(Read(formation, "isVisible")) || Bool(Read(formation, "isLoading")) || Bool(Read(formation, "onfight")) || Int(Read(formation, "waitstate")) != 0 || Read(formation, "DefaultState") as string != "NPCBattle" || Read(formation, "substate") as string != (challengeOnly ? Read(Read(campaign, "m_campaign"), "subtype") as string : "campaign") || attack == null || attack.GetInvocationList().Length != 1 || attack.Method.Name != "OnAttack" || attack.Target == null || attack.Target.GetType() != troopType || !object.ReferenceEquals(Read(attack.Target, "m_parent"), campaign) || !object.ReferenceEquals(Read(attack.Target, "m_troop"), t))
                    { why = "formation_start_gate"; return false; }
                    if (!Observer.Begin(command.AttemptId, command.Sequence, frame, out why)) return false;
                    // Freeze Begin identity and publish ownership before the
                    // original GUI branch can save formation or request battle.
                    invoked = true; BeforeInvoke(publishBeforeInvoke);
                    Component launchSource = campaign;
                    object launchData = Read(campaign, "m_campaign");
                    BattleLaunch.Formation(NativeMode,
                        formation, launchSource, t, delegate
                        {
                            return Observer.Pending && Observer.AttemptId == command.AttemptId &&
                                object.ReferenceEquals(Read(launchSource, "m_campaign"), launchData) &&
                                object.ReferenceEquals(TargetTroop(command.TroopId), t) &&
                                object.ReferenceEquals(Read(attack.Target, "m_parent"), launchSource) &&
                                object.ReferenceEquals(Read(attack.Target, "m_troop"), t) &&
                                Int(Read(launchSource, "m_troopID")) == command.TroopId &&
                                (!eliteOnly || Int(Read(launchData, "star_level")) == command.EliteStar &&
                                    EliteChallengeQuota.Admission(common.Character, command.EliteTarget) == "PASS");
                        }, null);
                }
                else if (action == "ack_result")
                {
                    if (Phase != "RESULT" || scenario != null) { why = "result_phase_required"; return false; }
                    bool fresh = FreshResultNavigation() == "ack_result";
                    if (!fresh && (!Observer.Finished || command.CampaignId != Observer.CampaignId || command.TroopId != Observer.TroopId)) { why = "settled_attempt_required"; return false; }
                    if (fresh && command.TroopId != troopId) { why = "result_target_changed"; return false; }
                    if (result != null && frame.ResultVisible && (fresh || Observer.AppliedTo(frame) &&
                        ((frame.ReportId != 0 && frame.ResultReportId == frame.ReportId) ||
                        (eliteOnly && Observer.Status == "CONTINUED" && frame.ReportId == 0 && frame.ResultReportId == 0) ||
                        (Observer.Outcome == "LOSS" && (Observer.Evidence == "anchored_current_battle_result_loss" ||
                            Observer.Evidence == CampaignObserver.AppliedLossEvidence) && frame.ReportId == 0 && frame.ResultReportId == 0))))
                    {
                        invoked = true; BeforeInvoke(publishBeforeInvoke); InvokeVoid(result, "Hide", Type.EmptyTypes, new object[0]);
                        InvokeVoid(battle, "battle_exit", Type.EmptyTypes, new object[0]); RememberLossAdvice(fresh);
                    }
                    else if (NormalRewardAcknowledgement())
                    {
                        Delegate close = Read(reward, "OnClose") as Delegate;
                        if (!RewardCallback(reward)) { why = "reward_callback_unknown"; return false; }
                        invoked = true; BeforeInvoke(publishBeforeInvoke); InvokeVoid(reward, "Hide", Type.EmptyTypes, new object[0]); close.DynamicInvoke(new object[0]);
                    }
                    else { why = "normal_result_button_unavailable"; return false; }
                }
                else if (action == "continue")
                {
                    if (battleGuide != null)
                    {
                        if (Phase != "SCENARIO" || BattleGuideGate() != "PASS" || command.CampaignId != campaignId ||
                            command.TroopId != troopId || command.AttemptId != "") { why = "normal_battle_guide_close_unavailable"; return false; }
                        if (!guiInvocation || Event.current == null) { why = "battle_guide_requires_gui_context"; return false; }
                        // The original close button only calls Hide. Its own
                        // override restores m_prevUI and EnableAll; no Step,
                        // sub-page action, result acknowledgement or request.
                        invoked = true; BeforeInvoke(publishBeforeInvoke);
                        InvokeVoid(battleGuide, "Hide", Type.EmptyTypes, new object[0]); return true;
                    }
                    if (notification != null)
                    {
                        if (Phase != "SCENARIO" || NotificationGate() != "PASS") { why = "normal_notification_unavailable"; return false; }
                        if (!guiInvocation || Event.current == null) { why = "notification_requires_gui_context"; return false; }
                        // Native dismissal restores old_rmg input. Both callback
                        // slots are required null by NotificationGate.
                        invoked = true; BeforeInvoke(publishBeforeInvoke);
                        InvokeVoid(notification, "ButtonOK", new Type[] { typeof(bool) }, new object[] { false });
                        return true;
                    }
                    if (lossAdvice != null)
                    {
                        if (Phase != "SCENARIO" || LossAdviceGate() != "PASS" || command.CampaignId != lossAdviceCampaignId || command.TroopId != lossAdviceTroopId)
                        { why = "normal_loss_advice_close_unavailable"; return false; }
                        if (!guiInvocation || Event.current == null) { why = "loss_advice_requires_gui_context"; return false; }
                        // BattleValueUI's ordinary close: EndGUI does Hide and
                        // OnClose (required null here), then SetSubUI(null).
                        invoked = true; BeforeInvoke(publishBeforeInvoke);
                        InvokeVoid(lossAdvice, "Hide", Type.EmptyTypes, new object[0]); SetSubUiNull();
                        lossAdviceAttempt = ""; freshLossAdvice = false; freshLossAdvicePage = null; return true;
                    }
                    if (function != null)
                    {
                        if (Phase != "SCENARIO" || FunctionGate() != "PASS") { why = "normal_function_ack_unavailable"; return false; }
                        if (!guiInvocation || Event.current == null) { why = "function_ack_requires_gui_context"; return false; }
                        // Exact ScenarioFunctionUI.Web_OnGUI normal OK branch:
                        // callback first, then Hide. Never invoke handle_button.
                        Delegate close = Read(function, "OnClose") as Delegate;
                        invoked = true; BeforeInvoke(publishBeforeInvoke);
                        if (close != null) close.DynamicInvoke(new object[0]);
                        Invoke(function, "Hide", Type.EmptyTypes, new object[0]);
                        return true;
                    }
                    if (Phase != "SCENARIO" || scenario == null || ScenarioGate() != "PASS") { why = "reviewed_scenario_required"; return false; }
                    if (!guiInvocation || Event.current == null) { why = "scenario_skip_requires_gui_context"; return false; }
                    string continuation = ScenarioContinuation();
                    if (continuation == "intro")
                    {
                        // Exact ScenarioUI.Update left-click branch. The game
                        // owns movement, newuis_ clearing, Close(Rect),
                        // FunctionLoad, system acknowledgement and OnClose.
                        // Never re-run Skip here: it resets the icon animation.
                        invoked = true; BeforeInvoke(publishBeforeInvoke);
                        Write(scenario, "m_bMoving", true);
                    }
                    else if (continuation == "skip")
                    {
                        Rect next = ScenarioNextRect();
                        invoked = true; BeforeInvoke(publishBeforeInvoke);
                        Invoke(scenario, "scenarioSkip", new Type[] { typeof(Rect) }, new object[] { next });
                    }
                    else if (continuation == "next")
                    {
                        // The other normal ScenarioUI.Update click branch.
                        // Web_OnGUI owns the ensuing close, callback and packets.
                        invoked = true; BeforeInvoke(publishBeforeInvoke);
                        Write(scenario, "m_bNext", true);
                    }
                    else { why = "scenario_continuation_changed"; return false; }
                }
                else if (action == "next_chapter")
                {
                    if (Phase != "RESULT" || scenario != null) { why = "result_phase_required"; return false; }
                    // m_bNextChapter controls only the title/layout. The normal
                    // non-Anecdote button always uses cs_campaign_enter(0).
                    if (chapter == null || (Observer.Status != "SETTLED" && FreshResultNavigation() != "next_chapter") || !Enabled(chapter) || Bool(Read(chapter, "m_bAnecdote"))) { why = "chapter_button_unavailable"; return false; }
                    Component root = globalUi; object rmg = Read(chapter, "rmg");
                    if (root == null || rmg == null) { why = "chapter_root_unavailable"; return false; }
                    invoked = true; BeforeInvoke(publishBeforeInvoke); InvokeVoid(rmg, "ToDisable", Type.EmptyTypes, new object[0]);
                    InvokeVoid(root, "cs_campaign_enter", new Type[] { typeof(ushort), rmg.GetType() }, new object[] { (ushort)0, rmg });
                    InvokeVoid(chapter, "Hide", Type.EmptyTypes, new object[0]); SetSubUiNull();
                }
                else { why = "campaign_action_unknown"; return false; }
                return true;
            }
            catch { if (invoked && command.CampaignAction == "start") Observer.Unknown("start_invocation_exception"); throw; }
            finally
            {
                if (invoked) Observer.MutationAttempted = true;
                RecordCommand(command, invoked, why);
            }
        }
        private bool CheckCommand(Command command, out string why)
        {
            why = "";
            if (command.ExpectedRevision != Revision) { why = "campaign_revision_changed"; return false; }
            if (currentRoot == null || !object.ReferenceEquals(model.CurrentRoot(), currentRoot)) { why = "campaign_current_root_changed"; return false; }
            if (HomeNavigationAction(command.CampaignAction)) return CheckHomeNavigation(command, out why);
            if (HomeNavigationPending) { why = "home_navigation_owned"; return false; }
            if (!Bool(ReadStatic(globalType, "extern_enabled")) && command.CampaignAction != "continue" &&
                !(command.CampaignAction == "ack_result" && NormalRewardAcknowledgement())) { why = "global_input_disabled"; return false; }
            if (Phase == "SCENARIO" && command.CampaignAction != "continue") { why = "scenario_action_only"; return false; }
            if (general.Active != GeneralTutorialModel.Action(command.CampaignAction)) { why = "general_tutorial_action_only"; return false; }
            if (challengeOnly && GeneralTutorialModel.Action(command.CampaignAction)) { why = "challenge_general_tutorial_unsupported"; return false; }
            if (!Available) { why = Gate; return false; }
            if (GeneralTutorialModel.Action(command.CampaignAction))
            { if (command.CampaignId != 0 || command.TroopId != 0 || command.AttemptId != "") { why = "general_target_must_be_local"; return false; } }
            else if (command.CampaignAction != "enter" && command.CampaignAction != "enter_challenge" && command.CampaignId != campaignId) { why = "campaign_target_changed"; return false; }
            if (command.Name != (eliteOnly ? "elite-command" : challengeOnly ? "challenge-command" : "campaign-command")) { why = "campaign_mode_mismatch"; return false; }
            if (eliteOnly && command.CampaignAction != "enter" && command.CampaignAction != "enter_challenge" &&
                (campaign == null || Int(Read(Read(campaign, "m_campaign"), "star_level")) != command.EliteStar))
            { why = "elite_star_changed"; return false; }
            if (eliteOnly && (command.CampaignAction == "start" || command.CampaignAction == "select" || command.CampaignAction == "prepare"))
            { why = EliteChallengeQuota.Admission(common.Character, command.EliteTarget); if (why != "PASS") return false; why = ""; }
            if (Observer.Pending && command.CampaignAction != "continue") { why = "attempt_unresolved"; return false; }
            if (Observer.Pending && command.CampaignAction == "continue" && !OwnedPendingStory())
            { why = "pending_story_ownership_unverified"; return false; }
            return true;
        }
        internal bool QueueGui(Command command, out string why)
        {
            bool accepted = CheckCommand(command, out why);
            if (accepted && queuedSequence != 0) { accepted = false; why = "scenario_gui_command_pending"; }
            if (GeneralTutorialModel.Action(command.CampaignAction))
            {
                if (accepted) accepted = general.Queue(command, out why);
                if (accepted) queuedSequence = command.Sequence;
                RecordCommand(command, false, accepted ? "awaiting_real_gui" : why); return accepted;
            }
            if (accepted && (command.CampaignAction != "continue" || Phase != "SCENARIO" || PassiveGate() != "PASS"))
            { accepted = false; why = "normal_scenario_skip_unavailable"; }
            if (accepted) { queuedFunction = null; queuedLossAdvice = null; queuedNotification = null; queuedBattleGuide = null; }
            if (accepted && battleGuide != null)
            {
                if (command.CampaignId != campaignId || command.TroopId != troopId || command.AttemptId != "")
                { RecordCommand(command, false, "battle_guide_target_changed"); why = "battle_guide_target_changed"; return false; }
                queuedSequence = command.Sequence; queuedRoot = currentRoot; queuedCharacter = common.Character; queuedCharacterId = characterId;
                queuedBattleGuide = battleGuide; queuedBattleGuideCampaign = campaign; queuedBattleGuideTroop = Read(battleGuide, "m_troop");
                queuedBattleGuideData = Read(campaign, "m_campaign"); queuedBattleGuideClient = frame.QueueClient;
                queuedBattleGuideRootUi = globalUi; queuedBattleGuideOwner = OwnerRoot(globalUi);
                queuedBattleGuideVisibleTime = (DateTime)Read(battleGuide, "visible_time");
                queuedBattleGuideUser = ULong(ReadStatic(globalType, "userid"));
                queuedBattleGuideZone = Convert.ToUInt32(ReadStatic(globalType, "zoneid"), CultureInfo.InvariantCulture);
                RecordCommand(command, false, "awaiting_real_gui"); return true;
            }
            if (accepted && notification != null)
            {
                queuedSequence = command.Sequence; queuedRoot = currentRoot; queuedCharacter = common.Character; queuedCharacterId = characterId;
                queuedNotification = notification; queuedNotificationRoot = OwnerRoot(notification); queuedNotificationVisibleTime = (DateTime)Read(notification, "visible_time");
                RecordCommand(command, false, "awaiting_real_gui"); return true;
            }
            if (accepted && lossAdvice != null)
            {
                if (command.CampaignId != lossAdviceCampaignId || command.TroopId != lossAdviceTroopId)
                { RecordCommand(command, false, "loss_advice_target_changed"); why = "loss_advice_target_changed"; return false; }
                queuedSequence = command.Sequence; queuedRoot = currentRoot; queuedCharacter = common.Character; queuedCharacterId = characterId;
                queuedLossAdvice = lossAdvice; queuedLossAdviceVisibleTime = (DateTime)Read(lossAdvice, "visible_time");
                RecordCommand(command, false, "awaiting_real_gui"); return true;
            }
            if (accepted && function != null)
            {
                queuedSequence = command.Sequence; queuedRoot = currentRoot; queuedCharacter = common.Character; queuedCharacterId = characterId;
                queuedFunction = function; queuedFunctionScenario = functionScenario; queuedGlobalUi = globalUi; queuedGlobalRoot = OwnerRoot(globalUi);
                queuedFunctionId = (uint)Read(function, "m_idbutton"); queuedFunctionVisibleTime = (DateTime)Read(function, "visible_time");
                queuedFunctionCallback = Read(function, "OnClose");
                queuedFunctionData = functionScenario == null ? null : Read(functionScenario, "m_scenario");
                queuedFunctionSection = functionScenario == null ? 0 : Int(Read(functionScenario, "m_iSection"));
                RecordCommand(command, false, "awaiting_real_gui"); return true;
            }
            if (accepted)
            {
                queuedSequence = command.Sequence; queuedRoot = currentRoot; queuedScenario = scenario;
                queuedCharacter = common.Character; queuedCharacterId = characterId;
                queuedScenarioData = Read(scenario, "m_scenario"); queuedSection = Int(Read(scenario, "m_iSection")); queuedVisibleTime = (DateTime)Read(scenario, "visible_time");
                IList sections = Read(queuedScenarioData, "sections") as IList;
                queuedSections = sections; queuedSectionCount = sections.Count; queuedScenarioId = ULong(Read(queuedScenarioData, "id"));
                queuedScenarioContinuation = ScenarioContinuation(); queuedScenarioCallback = Read(scenario, "OnClose");
            }
            RecordCommand(command, false, accepted ? "awaiting_real_gui" : why); return accepted;
        }
        internal bool ExecuteGui(Command command, CampaignPublication publish, out string why)
        {
            if (GeneralTutorialModel.Action(command.CampaignAction))
            {
                bool teachingOwned = queuedSequence == command.Sequence; queuedSequence = 0;
                bool teachingInvoked = false; why = "";
                try
                {
                    if (!teachingOwned || !CheckCommand(command, out why)) { general.Cancel(command); if (why == "") why = "general_requires_owned_gui_context"; return false; }
                    teachingInvoked = general.ExecuteGui(command, publish, out why);
                    return teachingInvoked;
                }
                finally { RecordCommand(command, teachingInvoked, why); }
            }
            bool owned = queuedSequence == command.Sequence; queuedSequence = 0;
            if (!owned || command.CampaignAction != "continue" || Event.current == null)
            { why = "scenario_skip_requires_gui_context"; RecordCommand(command, false, why); return false; }
            if (!GUI.enabled) { why = "scenario_gui_input_disabled"; RecordCommand(command, false, why); return false; }
            if (queuedBattleGuide != null)
            {
                if (!object.ReferenceEquals(battleGuide, queuedBattleGuide) || !object.ReferenceEquals(currentRoot, queuedRoot) ||
                    !object.ReferenceEquals(common.Character, queuedCharacter) || characterId != queuedCharacterId ||
                    !object.ReferenceEquals(campaign, queuedBattleGuideCampaign) || !object.ReferenceEquals(globalUi, queuedBattleGuideRootUi) ||
                    !object.ReferenceEquals(OwnerRoot(globalUi), queuedBattleGuideOwner) ||
                    !object.ReferenceEquals(Read(battleGuide, "m_troop"), queuedBattleGuideTroop) ||
                    !object.ReferenceEquals(Read(campaign, "m_campaign"), queuedBattleGuideData) ||
                    !object.ReferenceEquals(frame.QueueClient, queuedBattleGuideClient) ||
                    ULong(ReadStatic(globalType, "userid")) != queuedBattleGuideUser ||
                    Convert.ToUInt32(ReadStatic(globalType, "zoneid"), CultureInfo.InvariantCulture) != queuedBattleGuideZone ||
                    (DateTime)Read(battleGuide, "visible_time") != queuedBattleGuideVisibleTime)
                { why = "battle_guide_gui_context_changed"; RecordCommand(command, false, why); return false; }
                guiInvocation = true;
                try { return Execute(command, publish, out why); }
                finally { guiInvocation = false; queuedBattleGuide = null; }
            }
            if (queuedNotification != null)
            {
                if (!object.ReferenceEquals(notification, queuedNotification) || !object.ReferenceEquals(currentRoot, queuedRoot) ||
                    !object.ReferenceEquals(OwnerRoot(notification), queuedNotificationRoot) || !object.ReferenceEquals(common.Character, queuedCharacter) || characterId != queuedCharacterId ||
                    (DateTime)Read(notification, "visible_time") != queuedNotificationVisibleTime)
                { why = "notification_gui_context_changed"; RecordCommand(command, false, why); return false; }
                guiInvocation = true;
                try { return Execute(command, publish, out why); }
                finally { guiInvocation = false; queuedNotification = null; }
            }
            if (queuedLossAdvice != null)
            {
                if (!object.ReferenceEquals(lossAdvice, queuedLossAdvice) || !object.ReferenceEquals(currentRoot, queuedRoot) ||
                    !object.ReferenceEquals(common.Character, queuedCharacter) || characterId != queuedCharacterId ||
                    (DateTime)Read(lossAdvice, "visible_time") != queuedLossAdviceVisibleTime)
                { why = "loss_advice_gui_context_changed"; RecordCommand(command, false, why); return false; }
                guiInvocation = true;
                try { return Execute(command, publish, out why); }
                finally { guiInvocation = false; queuedLossAdvice = null; }
            }
            if (queuedFunction != null)
            {
                if (!object.ReferenceEquals(function, queuedFunction) || !object.ReferenceEquals(currentRoot, queuedRoot) ||
                    !object.ReferenceEquals(globalUi, queuedGlobalUi) || !object.ReferenceEquals(OwnerRoot(globalUi), queuedGlobalRoot) ||
                    !object.ReferenceEquals(common.Character, queuedCharacter) || characterId != queuedCharacterId ||
                    !object.ReferenceEquals(functionScenario, queuedFunctionScenario) || (uint)Read(function, "m_idbutton") != queuedFunctionId ||
                    (DateTime)Read(function, "visible_time") != queuedFunctionVisibleTime || !object.ReferenceEquals(Read(function, "OnClose"), queuedFunctionCallback) ||
                    (functionScenario != null && (!object.ReferenceEquals(Read(functionScenario, "m_scenario"), queuedFunctionData) || Int(Read(functionScenario, "m_iSection")) != queuedFunctionSection)))
                { why = "function_gui_context_changed"; RecordCommand(command, false, why); return false; }
                guiInvocation = true;
                try { return Execute(command, publish, out why); }
                finally { guiInvocation = false; queuedFunction = null; }
            }
            if (!object.ReferenceEquals(currentRoot, queuedRoot) || !object.ReferenceEquals(scenario, queuedScenario) || scenario == null ||
                !object.ReferenceEquals(common.Character, queuedCharacter) || characterId != queuedCharacterId ||
                !object.ReferenceEquals(Read(scenario, "m_scenario"), queuedScenarioData) || Int(Read(scenario, "m_iSection")) != queuedSection || (DateTime)Read(scenario, "visible_time") != queuedVisibleTime)
            { why = "scenario_gui_context_changed"; RecordCommand(command, false, why); return false; }
            if (!object.ReferenceEquals(Read(queuedScenarioData, "sections"), queuedSections) || queuedSections.Count != queuedSectionCount ||
                ULong(Read(queuedScenarioData, "id")) != queuedScenarioId || ScenarioContinuation() != queuedScenarioContinuation ||
                !object.ReferenceEquals(Read(scenario, "OnClose"), queuedScenarioCallback))
            { why = "scenario_gui_context_changed"; RecordCommand(command, false, why); return false; }
            guiInvocation = true;
            try { return Execute(command, publish, out why); }
            finally { guiInvocation = false; }
        }
        internal void GuiNotInvoked(Command command, string why) { if (queuedSequence == command.Sequence) queuedSequence = 0; if (GeneralTutorialModel.Action(command.CampaignAction)) general.Cancel(command); RecordCommand(command, false, why); }
        internal void GuiFailed(Command command, bool attempted, string why)
        { if (queuedSequence == command.Sequence) queuedSequence = 0; if (GeneralTutorialModel.Action(command.CampaignAction)) general.Failed(command, attempted, why); RecordCommand(command, attempted, why); if (Observer.Pending) Observer.Unknown(why); }
        private void RecordCommand(Command command, bool invoked, string why)
        { lastCommand = "{\"command_seq\":" + command.Sequence + ",\"action\":" + Command.Quote(command.CampaignAction) + ",\"attempt_id\":" + Command.Quote(command.AttemptId) + ",\"invoked\":" + (invoked ? "true" : "false") + ",\"reason\":" + Command.Quote(why) + "}"; }
        private void BeforeInvoke(CampaignPublication publish) { Observer.Invoking(); publish(); }
        private object TargetTroop(int id) { if (campaign == null) return null; IDictionary troops = Read(campaign, "m_troops") as IDictionary; return troops == null ? null : troops[(byte)id]; }
        private bool NormalTroop(object troop)
        { return NormalTroopGate(troop) == "PASS"; }
        private string NormalTroopGate(object troop)
        {
            if (troop == null) return "troop_missing";
            if (campaign == null) return "campaign_missing";
            if (common.Character == null) return "character_missing";
            object data = Read(campaign, "m_campaign");
            if (data == null) return "campaign_data_missing";
            if (Int(Read(data, "idcampaign_type")) != AllowedCampaignType) return "campaign_type";
            if (!Bool(Read(data, "unlocked"))) return "campaign_locked";
            if (Bool(Read(data, "finished"))) return "campaign_finished";
            if (!Bool(Read(troop, "visible"))) return "troop_hidden";
            if (!Bool(Read(troop, "canattack"))) return "troop_unattackable";
            if (Bool(Read(troop, "killed"))) return "troop_killed";
            if (Int(Read(troop, "chanllege_level")) > Int(Read(common.Character, "level"))) return "character_level";
            if (challengeOnly) { string cooldown = ChallengeCooldownGate(troop); if (cooldown != "PASS") return cooldown; }
            return "PASS";
        }
        private Component MeshTroopScript(GameObject mesh, out int count)
        {
            count = 0; if (mesh == null) return null;
            Type type = Model.GameType("CampaignTroopScript");
            if (meshComponents == null)
            {
                // This Unity method's System.Type belongs to desktop mscorlib;
                // the payload targets the game's compact mscorlib. Match its
                // fixed signature by name, as Model does for Unity's finder.
                foreach (MethodInfo method in typeof(GameObject).GetMethods(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (method.Name != "GetComponents" || method.IsGenericMethod || !method.ReturnType.IsArray || method.ReturnType.GetElementType().FullName != "UnityEngine.Component") continue;
                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length != 1 || parameters[0].ParameterType.FullName != "System.Type") continue;
                    if (meshComponents != null) throw new ProbeFailure("campaign_mesh_components_ambiguous");
                    meshComponents = method;
                }
                if (meshComponents == null) throw new ProbeFailure("campaign_mesh_components_signature");
            }
            Array components = meshComponents.Invoke(mesh, new object[] { type }) as Array;
            if (components == null || components.Length > 512) throw new ProbeFailure("campaign_mesh_components_bound");
            count = components.Length;
            Component component = count == 1 ? components.GetValue(0) as Component : null;
            return component != null && component.GetType() == type ? component : null;
        }
        private string SelectionGate(object troop)
        {
            if (Phase != "CAMPAIGN") return "phase";
            if (!Enabled(campaign)) return "campaign_disabled";
            string normal = NormalTroopGate(troop); if (normal != "PASS") return normal;
            // wantTroopUI routes mode 1 straight to OnAttack. The explicitly
            // supported 0/2/3 modes all take its ordinary MyLoading branch.
            int mode = Int(Read(troop, "click_mode"));
            if (mode != 0 && mode != 2 && mode != 3) return mode == 1 ? "direct_mode" : "click_mode_unknown";
            GameObject mesh = Read(troop, "mesh") as GameObject;
            if (mesh == null) return "mesh_missing";
            if (!mesh.activeInHierarchy) return "mesh_inactive";
            if (currentRoot == null || mesh.transform.root != currentRoot.transform) return "mesh_root";
            int count; Component script = MeshTroopScript(mesh, out count);
            if (script == null) return "mesh_script_not_unique";
            if (script.gameObject != mesh || !object.ReferenceEquals(Read(script, "ParentUI"), campaign) || !object.ReferenceEquals(Read(script, "Troop"), troop)) return "mesh_script_owner";
            if (!Enabled(script)) return "mesh_script_disabled";
            if (Bool(Read(campaign, "m_bDebug"))) return "debug";
            // CampaignBaseUI.PostInit sets m_bPostInit=true AFTER calling
            // ScenarioFirstVisible. That chain can synchronously reach
            // BonusClose, enable input and clear the flag, then return to the
            // trailing true assignment. It is not a readiness predicate: the
            // normal CampaignTroopScript click -> wantCombat branch checks
            // IsEnable and m_bDebug, never m_bPostInit. Keep it diagnostic only;
            // input, owned UI, mesh and active transition gates remain above/below.
            return SelectionTransitionGate();
        }
        private string SelectionTransitionGate()
        {
            if (Bool(Read(campaign, "m_bBorning"))) return "borning";
            if (Bool(Read(campaign, "m_bBornWant"))) return "born_want";
            if (Bool(Read(campaign, "m_bScenarioVisible"))) return "scenario_visible";
            if (Bool(Read(campaign, "m_bSignActiving"))) return "sign_activing";
            return "PASS";
        }
        private static void DiagnosticBool(StringBuilder json, string key, bool value)
        { json.Append(','); json.Append(Command.Quote(key)); json.Append(':'); json.Append(value ? "true" : "false"); }
        private static void DiagnosticNumber(StringBuilder json, string key, object value)
        { json.Append(','); json.Append(Command.Quote(key)); json.Append(':'); json.Append(Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)); }
        private string SelectionDiagnostics()
        {
            StringBuilder json = new StringBuilder("{\"character_campaign_id\":");
            json.Append(common.Character == null ? "0" : Int(Read(common.Character, "idcampaign")).ToString(CultureInfo.InvariantCulture));
            DiagnosticNumber(json, "character_level", common.Character == null ? 0 : Int(Read(common.Character, "level")));
            json.Append(",\"campaign\":");
            object data = campaign == null ? null : Read(campaign, "m_campaign");
            if (data == null) json.Append("null");
            else
            {
                json.Append("{\"type\":"); json.Append(Int(Read(data, "idcampaign_type")));
                DiagnosticBool(json, "enabled", Enabled(campaign)); DiagnosticBool(json, "unlocked", Bool(Read(data, "unlocked"))); DiagnosticBool(json, "finished", Bool(Read(data, "finished")));
                foreach (string field in new string[] { "m_bDebug", "m_bPostInit", "m_bBorning", "m_bBornWant", "m_bScenarioVisible", "m_bSignActiving" })
                    DiagnosticBool(json, field, Bool(Read(campaign, field)));
                json.Append('}');
            }
            json.Append(",\"troops\":["); int listed = 0, total = 0;
            IDictionary troops = campaign == null ? null : Read(campaign, "m_troops") as IDictionary;
            if (troops != null)
            {
                if (troops.Count > 255) throw new ProbeFailure("campaign_troop_schema");
                for (int id = 1; id <= 255; id++)
                {
                    object troop = troops[(byte)id];
                    if (troop == null || !Bool(Read(troop, "visible")) || !Bool(Read(troop, "canattack")) || Bool(Read(troop, "killed"))) continue;
                    total++; if (listed >= 8) continue;
                    if (listed++ != 0) json.Append(',');
                    json.Append("{\"troop_id\":"); json.Append(id);
                    foreach (string field in new string[] { "idcampaign", "idtroop", "chanllege_level", "click_mode", "limit_count", "finish_count", "finish_max", "isNewbie", "Newbie_Skill", "Newbie_formation", "m_scenarioClickF", "m_scenarioVisible", "m_scenarioAttackF", "m_scenarioVictoryF", "m_scenarioFailure", "m_scenarioSystem" })
                        DiagnosticNumber(json, field, Read(troop, field));
                    foreach (string field in new string[] { "visible", "canattack", "killed", "clicked", "show_arrow" }) DiagnosticBool(json, field, Bool(Read(troop, field)));
                    GameObject mesh = Read(troop, "mesh") as GameObject; int count; Component script = MeshTroopScript(mesh, out count);
                    DiagnosticBool(json, "mesh_present", mesh != null); DiagnosticBool(json, "mesh_active", mesh != null && mesh.activeInHierarchy);
                    DiagnosticBool(json, "mesh_root_matches", mesh != null && currentRoot != null && mesh.transform.root == currentRoot.transform);
                    DiagnosticNumber(json, "mesh_script_count", Math.Min(count, 512));
                    DiagnosticBool(json, "mesh_script_enabled", script != null && Enabled(script));
                    DiagnosticBool(json, "mesh_script_owner_matches", script != null && script.gameObject == mesh && object.ReferenceEquals(Read(script, "ParentUI"), campaign) && object.ReferenceEquals(Read(script, "Troop"), troop));
                    json.Append(",\"select_gate\":"); json.Append(Command.Quote(SelectionGate(troop))); json.Append('}');
                }
            }
            json.Append("]"); DiagnosticBool(json, "truncated", total > listed); json.Append('}'); return json.ToString();
        }
        private string ScenarioStep()
        { object s = Read(scenario, "m_scenario"); return s == null ? "unknown" : Read(s, "id").ToString() + ":" + Read(scenario, "m_iSection").ToString(); }
        private bool FunctionSchema()
        {
            FieldInfo id = functionType.GetField("m_idbutton", Declared);
            MethodInfo gui = functionType.GetMethod("Web_OnGUI", Declared, null, Type.EmptyTypes, null);
            return functionType.Assembly.GetName().Name == "Assembly-CSharp" && functionType.MetadataToken == 0x02000227 &&
                id != null && id.MetadataToken == 0x04001ac2 && id.FieldType == typeof(uint) &&
                gui != null && gui.MetadataToken == 0x06001f1f && gui.ReturnType == typeof(void) && !gui.IsStatic;
        }
        private string FunctionGate()
        {
            if (!FunctionSchema()) return "function_schema_unknown";
            if (!Visible(function) || !FunctionOwned(function) || campaign == null || !OwnedBy(campaign, currentRoot) || RootLevel(currentRoot) != AllowedRoot) return "function_context_mismatch";
            if (scenario != null || panel != null || formation != null || result != null || reward != null || chapter != null || Visible(battle)) return "function_conflicting_campaign_ui";
            // The normal OK exists with or without the optional Check button.
            // Never invoke Check/handle_button; texture availability is only art.
            object callback = Read(function, "OnClose");
            Delegate close = callback as Delegate;
            if (callback != null && (close == null || close.GetInvocationList().Length != 1 || functionScenario == null ||
                !ScenarioOwned(functionScenario) || !object.ReferenceEquals(close.Target, functionScenario) ||
                close.Method.DeclaringType != scenarioType || close.Method.Name != "FunctionClose" || close.Method.MetadataToken != 0x06001f2a ||
                close.Method.ReturnType != typeof(void) || close.Method.GetParameters().Length != 0)) return "function_callback_unknown";
            if (Bool(Invoke(function, "IsLoading", Type.EmptyTypes, new object[0]))) return "WAITING_UI";
            object visible = Read(function, "visible_time");
            if (!(visible is DateTime) || (DateTime)visible == DateTime.MinValue) return "function_visibility_unknown";
            if ((DateTime.Now - (DateTime)visible).TotalSeconds < 0.30000001192092896) return "WAITING_UI";
            if (!Enabled(function)) return "WAITING_UI";
            return "PASS";
        }
        private string ScenarioGate()
        {
            if (scenario == null || !Visible(scenario)) return "scenario_not_active";
            if (!ScenarioOwned(scenario) || campaign == null || !OwnedBy(campaign, currentRoot) || RootLevel(currentRoot) != AllowedRoot) return "scenario_context_mismatch";
            object data = Read(scenario, "m_scenario");
            IList sections = data == null ? null : Read(data, "sections") as IList;
            if (sections == null || Int(Read(scenario, "m_iSection")) < 0) return "scenario_step_unknown";
            if (Bool(Invoke(scenario, "IsLoading", Type.EmptyTypes, new object[0]))) return "WAITING_UI";
            object visible = Read(scenario, "visible_time");
            if (!(visible is DateTime) || (DateTime)visible == DateTime.MinValue) return "scenario_visibility_unknown";
            if ((DateTime.Now - (DateTime)visible).TotalSeconds < 0.3) return "WAITING_UI";
            if (Bool(Read(scenario, "m_bMoving"))) return "WAITING_MOVEMENT";
            if (Bool(Read(scenario, "m_bNext"))) return "WAITING_SCENARIO_END";
            return ScenarioContinuation() == "" ? "WAITING_SCENARIO_END" : "PASS";
        }
        private string ScenarioContinuation()
        {
            // These are the native input branches, not a reconstruction of the
            // intro's icons, textures, names, coordinates or animation layout.
            // The game's Update/Web_OnGUI retain all movement and close work.
            if (Bool(Read(scenario, "m_bMoving")) || Bool(Read(scenario, "m_bNext"))) return "";
            if (Bool(Read(scenario, "m_bHasNewUI"))) return "intro";
            IList sections = (IList)Read(Read(scenario, "m_scenario"), "sections");
            if (Int(Read(scenario, "m_iSection")) < sections.Count) return "skip";
            if (sections.Count == 0 || Bool(Read(scenario, "m_bSectionEnd"))) return "next";
            return "";
        }
        private Rect ScenarioNextRect()
        {
            object s = Read(scenario, "m_scenario"); IList templates = Read(s, "m_sections") as IList;
            if (templates == null) throw new ProbeFailure("scenario_template_schema");
            float x = templates.Count > 0 && Int(Read(templates[0], "layout")) != 3 ? 250f : 200f;
            for (Type t = mainType; t != null; t = t.BaseType)
            {
                if (t.FullName != "__BaseUI") continue;
                MethodInfo rect = t.GetMethod("_rectRB", Static, null, new Type[] { typeof(float), typeof(float), typeof(float), typeof(float) }, null);
                if (rect == null || rect.ReturnType != typeof(Rect)) throw new ProbeFailure("scenario_rect_signature");
                return (Rect)rect.Invoke(null, new object[] { x, 60f, 154f, 39f });
            }
            throw new ProbeFailure("scenario_rect_base_missing");
        }
        private void GuideFormationClear()
        {
            MethodInfo m = globalType.GetMethod("setGuideCheck", Static, null, new Type[] { typeof(string), typeof(bool) }, null);
            if (m == null || m.ReturnType != typeof(void)) throw new ProbeFailure("guide_method_signature");
            m.Invoke(null, new object[] { "troop_formation", false });
        }
        private void SetSubUiNull()
        {
            MethodInfo m = mainType.GetMethod("SetSubUI", Static);
            if (m == null || m.GetParameters().Length != 1 || m.ReturnType != typeof(void)) throw new ProbeFailure("subui_method_signature");
            m.Invoke(null, new object[] { null });
        }
        private static bool Visible(Component c) { Behaviour b = c as Behaviour; return b != null && b.enabled && b.gameObject.activeInHierarchy; }
        private static bool Enabled(Component c) { return Visible(c) && Bool(Invoke(c, "IsEnable", Type.EmptyTypes, new object[0])); }
        private static object Read(object target, string name)
        {
            if (target == null) throw new ProbeFailure("campaign_null_" + name);
            for (Type t = target.GetType(); t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(name, Declared); if (f != null) return f.GetValue(target);
                PropertyInfo p = t.GetProperty(name, Declared); if (p != null && p.CanRead && p.GetIndexParameters().Length == 0) return p.GetValue(target, null);
            }
            throw new ProbeFailure("campaign_field_missing_" + name);
        }
        private static object ReadStatic(Type type, string name)
        { FieldInfo f = type.GetField(name, Static); if (f == null) throw new ProbeFailure("campaign_static_schema_" + name); return f.GetValue(null); }
        private static string CharacterId(object character)
        {
            if (character == null) return "0";
            if (character.GetType() != Model.FirstType("Database.Character")) throw new ProbeFailure("campaign_character_type");
            for (Type t = character.GetType(); t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField("id", Declared);
                if (f == null) continue;
                if (f.DeclaringType.FullName != "Database._character" || f.DeclaringType.Assembly.GetName().Name != "pbdata" || f.MetadataToken != 0x040001f6 || f.FieldType != typeof(ulong)) throw new ProbeFailure("campaign_character_identity_schema");
                return ((ulong)f.GetValue(character)).ToString(CultureInfo.InvariantCulture);
            }
            throw new ProbeFailure("campaign_character_id_missing");
        }
        private static void Write(object target, string name, object value)
        {
            FieldInfo f = target.GetType().GetField(name, Declared);
            if (f == null || f.FieldType != value.GetType()) throw new ProbeFailure("campaign_write_signature"); f.SetValue(target, value);
        }
        private static object Invoke(object target, string name, Type[] parameters, object[] args)
        {
            if (target == null) throw new ProbeFailure("campaign_invoke_null");
            for (Type t = target.GetType(); t != null; t = t.BaseType)
            { MethodInfo m = t.GetMethod(name, Declared, null, parameters, null); if (m != null && !m.IsGenericMethod) return m.Invoke(target, args); }
            throw new ProbeFailure("campaign_method_signature_" + name);
        }
        private static void InvokeVoid(object target, string name, Type[] parameters, object[] args)
        {
            if (target == null) throw new ProbeFailure("campaign_invoke_null");
            for (Type t = target.GetType(); t != null; t = t.BaseType)
            {
                MethodInfo m = t.GetMethod(name, Declared, null, parameters, null);
                if (m == null) continue;
                if (m.IsGenericMethod || m.ReturnType != typeof(void)) throw new ProbeFailure("campaign_void_signature_" + name);
                m.Invoke(target, args); return;
            }
            throw new ProbeFailure("campaign_method_signature_" + name);
        }
        private static int Int(object value) { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
        private static ulong ULong(object value) { return Convert.ToUInt64(value, CultureInfo.InvariantCulture); }
        private static bool Bool(object value) { if (!(value is bool)) throw new ProbeFailure("campaign_bool_schema"); return (bool)value; }
    }
}
