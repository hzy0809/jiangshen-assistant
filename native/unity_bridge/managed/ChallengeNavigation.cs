using System;
using System.Collections;
using System.Reflection;
using System.Text;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace JiangShen.AssistantRuntime
{
    // Normal challenges share the battle machinery, but never the mainline
    // observer's identity or the hero entry/buying branches.
    internal sealed partial class CampaignModel
    {
        private Component challengeList, challengeBackdrop;
        private string challengeCandidates = "[]";
        private readonly Type challengeListType = Model.GameType("AnecdoteSelectUI");
        private string entryStatus = "NONE", entryReason = "";
        private Component entryFromRoot, entryToRoot, entrySourceMap, entrySourceMain;
        private object entryCharacter, entryCampaign;
        private string entryCharacterId;
        private int entryCampaignId, entrySequence, entryTick;
        private bool challengeSourceRetirement;
        internal bool NavigationPending { get { return challengeOnly && (entryStatus == "WAITING" || entryStatus == "UNKNOWN" || HomeNavigationPending); } }
        private string homeStatus = "NONE", homeReason = "", homeOrigin = "";
        private Component homeSourceRoot, homeTargetRoot, homeSourceMap, homeReturnUi, homeList;
        private object homeCharacter;
        private string homeCharacterId = "", homeDestination = "";
        private int homeReturnSequence, homeCloseSequence, homeTick, homeSourceCampaignId;
        private bool HomeNavigationPending { get { return homeStatus != "NONE" && homeStatus != "READY"; } }
        private object homeDivinityClient;
        private Component divinityObservedUi, divinityObservedRoot, divinityObservedDestination;
        private object divinityObservedClient;
        internal static bool HomeNavigationAction(string action) { return action == "return_home" || action == "close_home_list" || action == "close_challenge_list"; }

        private string HomeNavigationJson()
        {
            Component sourceUi = null, destination = null, list = null;
            string gate = challengeOnly ? HomeReturnGate(out sourceUi, out destination, out list) : "challenge_only";
            bool divinitySource = currentRoot != null && Read(currentRoot, "LevelID") as string == "Divinity";
            if (divinitySource && gate == "PASS")
            {
                divinityObservedUi = sourceUi; divinityObservedRoot = currentRoot;
                divinityObservedDestination = destination; divinityObservedClient = ReadStatic(rootType, "m_client");
            }
            Component returnUi = divinitySource && gate == "PASS" ? sourceUi : homeOrigin == "DIVINITY" ? homeReturnUi : null;
            Component returnDestination = divinitySource && gate == "PASS" ? destination : homeOrigin == "DIVINITY" ? homeTargetRoot : null;
            string divinity = returnUi == null || returnDestination == null ? "null" :
                "{\"ui_instance_id\":" + returnUi.GetInstanceID() + ",\"destination_root_instance_id\":" + returnDestination.GetInstanceID() + "}";
            return "{\"interface\":\"anecdote-home-return-v1\",\"status\":" + Command.Quote(homeStatus) +
                ",\"origin\":" + Command.Quote(homeOrigin) + ",\"existing_list_gate\":" + Command.Quote(challengeOnly ? ExistingHomeListGate() : "challenge_only") +
                ",\"current_list_instance_id\":" + (challengeList == null ? 0 : challengeList.GetInstanceID()) +
                ",\"current_list_root_instance_id\":" + (challengeList == null || OwnerRoot(challengeList) == null ? 0 : OwnerRoot(challengeList).GetInstanceID()) +
                ",\"gate\":" + Command.Quote(gate) + ",\"reason\":" + Command.Quote(homeReason) +
                ",\"return_command_seq\":" + homeReturnSequence + ",\"close_command_seq\":" + homeCloseSequence +
                ",\"character_id\":" + Command.Quote(homeCharacterId) + ",\"source_campaign_id\":" + homeSourceCampaignId +
                ",\"source_root_instance_id\":" + (homeSourceRoot == null ? 0 : homeSourceRoot.GetInstanceID()) +
                ",\"destination_root_instance_id\":" + (homeTargetRoot == null ? 0 : homeTargetRoot.GetInstanceID()) +
                ",\"list_instance_id\":" + (homeList == null ? 0 : homeList.GetInstanceID()) +
                ",\"destination\":" + Command.Quote(homeDestination) + ",\"divinity_return\":" + divinity + "}";
        }

        private string ExistingHomeListGate()
        {
            if (!challengeOnly || HomeNavigationPending || Phase != "CHALLENGE_LIST" || contextGate != "PASS" || !ChallengeListIdentity() ||
                RootLevel(currentRoot) != "Campaign" && RootLevel(currentRoot) != "Capital" ||
                currentRoot == null || !currentRoot.gameObject.activeInHierarchy || common.Character == null || characterId == "0" ||
                common.Main == null || !OwnedBy(common.Main, currentRoot) || entryStatus == "WAITING" || entryStatus == "UNKNOWN" ||
                Observer.Pending || Observer.Status != "NONE" && Observer.Status != "SETTLED") return "home_existing_list_unavailable";
            string listGate = ChallengeListGate();
            if (Gate == "challenge_list_loading" && listGate == "challenge_list_loading" ||
                Gate == "challenge_list_network_pending" && listGate == "challenge_list_network_pending" ||
                Gate == "global_input_disabled" && (listGate == "PASS" || listGate == "challenge_list_not_interactive"))
                return "WAITING_HOME_LIST";
            if (!Available || Gate != "PASS" || !common.InteractionEnabled || !Bool(ReadStatic(globalType, "extern_enabled")))
                return "home_existing_list_unavailable";
            return listGate;
        }

        private string HomeReturnGate(out Component sourceUi, out Component destination, out Component list)
        {
            sourceUi = destination = list = null;
            if (!challengeOnly || HomeNavigationPending) return "home_navigation_owned";
            if (currentRoot != null && Read(currentRoot, "LevelID") as string == "Divinity")
                return DivinityHomeReturnGate(out sourceUi, out destination);
            if (!Available || Gate != "PASS" || Phase != "CAMPAIGN" || contextGate != "PASS" ||
                RootLevel(currentRoot) != "Anecdote" || !currentRoot.gameObject.activeInHierarchy ||
                campaign == null || !OwnedBy(campaign, currentRoot) || !Enabled(campaign) ||
                common.Character == null || characterId == "0" || Bool(Read(currentRoot, "bLoading")) ||
                Int(Read(currentRoot, "ExpectingCount")) != 0 ||
                common.Gate == "loading_or_battle" || !Bool(ReadStatic(globalType, "extern_enabled"))) return "home_source_not_idle";
            if (Observer.Pending || Observer.Status != "NONE" && Observer.Status != "SETTLED" ||
                entryStatus == "WAITING" || entryStatus == "UNKNOWN" || !CurrentGameIdle() ||
                ReadStatic(mainType, "m_subui") != null || SelectionTransitionGate() != "PASS" ||
                Bool(Read(campaign, "loading")) || Bool(Read(campaign, "collecting")) ||
                Bool(Read(common.Character, "gm")) || Bool(Read(campaign, "m_bDebug"))) return "home_source_operation_unresolved";
            if (panel != null || formation != null || Visible(battle) || result != null || reward != null || chapter != null ||
                scenario != null || function != null || lossAdvice != null || notification != null || general.Active)
                return "home_source_conflicting_ui";
            object data = Read(campaign, "m_campaign");
            if (data == null || campaignId < 1 || Int(Read(data, "idcampaign_type")) != AllowedCampaignType ||
                !Bool(Read(data, "unlocked")) || Int(Read(campaign, "m_campaignID")) != campaignId) return "home_source_campaign_changed";
            foreach (UnityEngine.Object item in model.AllUi())
            {
                Component c = item as Component;
                if (c == null || c.gameObject != campaign.gameObject || !OwnedBy(c, currentRoot)) continue;
                if (c.GetType() == Model.GameType("AnecdoteUI"))
                { if (sourceUi != null) return "home_return_ui_ambiguous"; sourceUi = c; }
                if (c.GetType() == challengeListType)
                { if (list != null) return "home_return_list_ambiguous"; list = c; }
            }
            if (sourceUi == null || !Enabled(sourceUi) || list == null || Visible(list)) return "home_return_ui_unavailable";
            object load = ReadStatic(Model.FirstType("BaseAsset"), "lds");
            Component loader = load as Component;
            if (loader == null || !loader.gameObject.activeInHierarchy || Visible(loader) || Bool(Read(load, "isBattling")))
                return "home_loader_not_idle";
            destination = Invoke(load, "GetOldMainUI", Type.EmptyTypes, new object[0]) as Component;
            string level = RootLevel(destination);
            if (!ValidRoot(destination) || destination == currentRoot || level != "Campaign" && level != "Capital")
                return "home_old_destination_unknown";
            return "PASS";
        }

        // DivinityUI._return has no challenge list. Only an idle stock scene
        // with no OnClose delegate can be returned without adopting a battle.
        private string DivinityHomeReturnGate(out Component sourceUi, out Component destination)
        {
            sourceUi = destination = null;
            if (!challengeOnly || HomeNavigationPending || !ValidRoot(currentRoot) ||
                Read(currentRoot, "LevelID") as string != "Divinity" ||
                !object.ReferenceEquals(model.CurrentRoot(), currentRoot) || !currentRoot.gameObject.activeInHierarchy ||
                common.Character == null || characterId == "0" ||
                !object.ReferenceEquals(ReadStatic(globalType, "character_"), common.Character) ||
                CharacterId(common.Character) != characterId || contextGate != "PASS" ||
                !Bool(ReadStatic(globalType, "extern_enabled")) || Bool(Read(currentRoot, "bLoading")) ||
                Int(Read(currentRoot, "ExpectingCount")) != 0 ||
                Bool(ReadStatic(Model.GameType("BuildingBuildUI"), "m_bBuildVisible")) ||
                Bool(ReadStatic(Model.GameType("BuildingBehaviourScript"), "m_bEditVisible")) ||
                Bool(ReadStatic(Model.FirstType("PopupUI"), "popup"))) return "home_divinity_source_not_idle";
            if (Observer.Pending || Observer.Status != "NONE" && Observer.Status != "SETTLED" ||
                entryStatus == "WAITING" || entryStatus == "UNKNOWN" || general.Active ||
                ReadStatic(mainType, "m_subui") != null) return "home_divinity_operation_unresolved";
            Component load = ReadStatic(Model.FirstType("BaseAsset"), "lds") as Component;
            if (load == null || load.GetType() != Model.FirstType("LoadScreen") ||
                !load.gameObject.activeInHierarchy || ((Behaviour)load).enabled || Bool(Read(load, "isBattling")) ||
                !object.ReferenceEquals(Read(load, "currmg"), currentRoot)) return "home_divinity_loader_not_idle";
            destination = Invoke(load, "GetOldMainUI", Type.EmptyTypes, new object[0]) as Component;
            if (!ValidRoot(destination) || destination == currentRoot ||
                RootLevel(destination) != "Capital" && RootLevel(destination) != "Campaign")
                return "home_divinity_old_destination_unknown";
            UObject[] all = model.AllUi(); Component sceneMain = null;
            Type divinityType = Model.GameType("DivinityUI");
            foreach (UObject item in all)
            {
                Component c = item as Component;
                if (c == null || c.GetType() != divinityType || !OwnedBy(c, currentRoot)) continue;
                if (sourceUi != null) return "home_divinity_ui_ambiguous";
                sourceUi = c;
            }
            if (sourceUi == null || !Enabled(sourceUi) || Bool(Read(sourceUi, "first")) ||
                Bool(Read(sourceUi, "loading")) || Bool(Read(sourceUi, "collecting")) || Read(sourceUi, "OnClose") != null)
                return "home_divinity_return_ui_unavailable";
            foreach (UObject item in all)
            {
                Behaviour ui = item as Behaviour; if (ui == null) continue;
                Type type = ui.GetType();
                if (type == battleType && (Visible(ui) || Bool(Read(ui, "BattleFighting")) ||
                    Bool(Read(ui, "bNeedWaitForBattleEnd")) || Bool(Read(ui, "bUseBattleEnd"))))
                    return "home_divinity_battle_unresolved";
                if (!Visible(ui) || ui == sourceUi || ui == currentRoot || ui == globalUi) continue;
                if (type == mainType)
                {
                    if (sceneMain != null || !OwnedBy(ui, currentRoot) || !Enabled(ui) ||
                        Bool(Read(ui, "first")) || !Bool(Read(ui, "m_bInit"))) return "home_divinity_main_unavailable";
                    sceneMain = ui; continue;
                }
                if (type == Model.GameType("DivinityTroopScript"))
                {
                    object troop = Read(ui, "Troop"); Array troops = Read(common.Character, "god_troops") as Array;
                    if (!object.ReferenceEquals(Read(ui, "ParentUI"), sourceUi) || troop == null || troops == null ||
                        Array.IndexOf(troops, troop) < 0 || !object.ReferenceEquals(Read(troop, "mesh"), ui.gameObject) ||
                        ui.transform.root != currentRoot.transform) return "home_divinity_troop_identity_changed";
                    continue;
                }
                if (type == Model.GameType("DivinityStoneScript") &&
                    object.ReferenceEquals(ui.gameObject, ReadStatic(divinityType, "stone_mesh")) &&
                    ui.transform.root == currentRoot.transform) continue;
                if (model.OverlayGate(ui) != null) return "home_divinity_conflicting_ui:" + type.Name;
            }
            if (sceneMain == null) return "home_divinity_main_unavailable";
            object client = ReadStatic(rootType, "m_client");
            return DivinityHomeNetworkIdle(client) ? "PASS" : "home_divinity_network_unresolved";
        }

        private bool DivinityHomeNetworkIdle(object client)
        {
            if (client == null || !object.ReferenceEquals(client, ReadStatic(globalType, "m_client")) ||
                !System.Threading.Monitor.TryEnter(client, 0)) return false;
            try
            {
                IList messages = Read(client, "messages") as IList;
                IDictionary handlers = Read(client, "m_handlers") as IDictionary;
                return messages != null && handlers != null && messages.Count == 0 && handlers.Count == 0;
            }
            finally { System.Threading.Monitor.Exit(client); }
        }

        private string HomeListCloseGate()
        {
            if (homeStatus != "LIST_READY" || currentRoot != homeTargetRoot ||
                !object.ReferenceEquals(common.Character, homeCharacter) || characterId != homeCharacterId ||
                challengeList != homeList || homeList == null || !OwnedBy(homeList, homeSourceRoot) ||
                homeSourceRoot == null || RootLevel(homeSourceRoot) != "Anecdote" ||
                homeReturnUi == null || homeSourceMap == null || homeReturnUi.gameObject != homeSourceMap.gameObject ||
                !OwnedBy(homeReturnUi, homeSourceRoot) || !OwnedBy(homeSourceMap, homeSourceRoot) ||
                homeList.gameObject != homeSourceMap.gameObject)
                return "home_return_list_identity_changed";
            // Normal scene retirement calls CampaignBaseUI.Destroy: it clears
            // m_campaign and m_campaignID before the return callback opens this
            // same list. Retain component/root identity, not retired map data.
            if (!Available || Gate != "PASS" || ChallengeListGate() != "PASS") return "home_return_list_not_ready";
            return "PASS";
        }

        private void ObserveHomeNavigation()
        {
            if (!challengeOnly || !HomeNavigationPending || homeStatus == "UNKNOWN") return;
            if (!object.ReferenceEquals(ReadStatic(globalType, "character_"), homeCharacter))
            { homeStatus = "UNKNOWN"; homeReason = "home_character_changed"; return; }
            if (currentRoot != homeSourceRoot && currentRoot != homeTargetRoot)
            { homeStatus = "UNKNOWN"; homeReason = "home_destination_changed"; return; }
            if (homeStatus != "LIST_READY" && unchecked((uint)(Environment.TickCount - homeTick)) >= 30000U)
            { homeStatus = "UNKNOWN"; homeReason = "home_navigation_timeout"; return; }
            if (homeOrigin == "DIVINITY")
            {
                if (!object.ReferenceEquals(ReadStatic(rootType, "m_client"), homeDivinityClient))
                { homeStatus = "UNKNOWN"; homeReason = "home_divinity_client_changed"; return; }
                if (currentRoot != homeTargetRoot) return;
                if (ReadStatic(mainType, "m_subui") != null)
                { homeStatus = "UNKNOWN"; homeReason = "home_divinity_unowned_popup"; return; }
                if (RootLevel(currentRoot) == homeDestination && Available && Gate == "PASS" &&
                    (Phase == "MAIN" || Phase == "CAMPAIGN") && common.InteractionEnabled &&
                    object.ReferenceEquals(common.Root, homeTargetRoot) && common.Main != null &&
                    OwnedBy(common.Main, homeTargetRoot) && !Visible(homeReturnUi) &&
                    DivinityHomeNetworkIdle(homeDivinityClient))
                { homeStatus = "READY"; homeReason = "home_divinity_return_observed_interactive"; }
                return;
            }
            if (currentRoot != homeTargetRoot) return;
            object sub = ReadStatic(mainType, "m_subui");
            if (sub != null && !object.ReferenceEquals(sub, homeList))
            { homeStatus = "UNKNOWN"; homeReason = "home_unowned_popup"; return; }
            if (homeStatus == "RETURNING" && challengeList == homeList && homeList != null &&
                Available && Gate == "PASS" && ChallengeListGate() == "PASS")
            { homeStatus = "LIST_READY"; homeReason = "matching_return_list_ready"; }
            else if (homeStatus == "LIST_READY" && (!Visible(homeList) || !object.ReferenceEquals(sub, homeList)))
            { homeStatus = "UNKNOWN"; homeReason = "home_return_list_replaced"; }
            else if (homeStatus == "CLOSING" && sub == null && !Visible(homeList) &&
                RootLevel(currentRoot) == homeDestination && common.InteractionEnabled && common.Main != null && OwnedBy(common.Main, homeTargetRoot) &&
                object.ReferenceEquals(common.Root, homeTargetRoot) && object.ReferenceEquals(common.Character, homeCharacter) &&
                Available && Gate == "PASS" && Phase == "MAIN" && CurrentGameIdle())
            { homeStatus = "READY"; homeReason = "home_root_observed_interactive"; }
        }

        private bool CheckHomeNavigation(Command command, out string why)
        {
            why = "";
            if (!challengeOnly || command.Name != (eliteOnly ? "elite-command" : "challenge-command") ||
                command.TroopId != 0 || command.AttemptId != "") { why = "home_command_scope_invalid"; return false; }
            if (command.CampaignAction == "close_challenge_list")
            { why = command.CampaignId == 0 ? ExistingHomeListGate() : "home_list_target_must_be_local"; return why == "PASS"; }
            if (command.CampaignAction == "close_home_list")
            {
                why = command.CampaignId == homeSourceCampaignId ? HomeListCloseGate() : "home_source_campaign_changed";
                return why == "PASS";
            }
            Component sourceUi = null, destination = null, list = null;
            if (currentRoot != null && Read(currentRoot, "LevelID") as string == "Divinity")
            {
                why = command.CampaignId == 0 ? HomeReturnGate(out sourceUi, out destination, out list) : "home_divinity_target_must_be_local";
                if (why != "PASS") return false;
                if (sourceUi != divinityObservedUi || currentRoot != divinityObservedRoot ||
                    destination != divinityObservedDestination || !object.ReferenceEquals(ReadStatic(rootType, "m_client"), divinityObservedClient))
                { why = "home_divinity_observed_instance_changed"; return false; }
                return true;
            }
            why = command.CampaignId == campaignId ? HomeReturnGate(out sourceUi, out destination, out list) : "home_source_campaign_changed";
            return why == "PASS";
        }

        private bool ExecuteHomeNavigation(Command command, CampaignPublication publish, out bool invoked, out string why)
        {
            invoked = false; why = "";
            if (command.CampaignAction == "close_challenge_list")
            {
                why = ExistingHomeListGate(); if (why != "PASS") return false;
                homeOrigin = "EXISTING_LIST"; homeSourceRoot = OwnerRoot(challengeList); homeTargetRoot = currentRoot;
                homeList = challengeList; homeCharacter = common.Character; homeCharacterId = characterId;
                homeSourceMap = homeReturnUi = null; homeSourceCampaignId = homeReturnSequence = 0;
                homeDestination = RootLevel(currentRoot); homeCloseSequence = command.Sequence;
                homeStatus = "CLOSING"; homeReason = "normal_existing_list_close_submitted"; homeTick = Environment.TickCount;
                invoked = true; BeforeInvoke(publish); SetSubUiNull(); return true;
            }
            if (command.CampaignAction == "close_home_list")
            {
                why = HomeListCloseGate(); if (why != "PASS") return false;
                homeStatus = "CLOSING"; homeReason = "normal_return_list_close_submitted";
                homeCloseSequence = command.Sequence; homeTick = Environment.TickCount;
                invoked = true; BeforeInvoke(publish); SetSubUiNull(); return true;
            }
            Component sourceUi, destination, list;
            why = HomeReturnGate(out sourceUi, out destination, out list); if (why != "PASS") return false;
            MethodInfo normalReturn = sourceUi.GetType().GetMethod("_return", Declared, null, Type.EmptyTypes, null);
            if (normalReturn == null || normalReturn.ReturnType != typeof(void)) throw new ProbeFailure("home_normal_return_schema");
            if (Read(currentRoot, "LevelID") as string == "Divinity")
            {
                object client = ReadStatic(rootType, "m_client");
                if (client == null || !System.Threading.Monitor.TryEnter(client, 0))
                { why = "home_divinity_network_unresolved"; return false; }
                try
                {
                    why = DivinityHomeReturnGate(out sourceUi, out destination); if (why != "PASS") return false;
                    if (sourceUi != divinityObservedUi || currentRoot != divinityObservedRoot ||
                        destination != divinityObservedDestination || !object.ReferenceEquals(client, divinityObservedClient))
                    { why = "home_divinity_observed_instance_changed"; return false; }
                    homeSourceRoot = currentRoot; homeTargetRoot = destination; homeSourceMap = homeReturnUi = sourceUi;
                    homeList = null; homeCharacter = common.Character; homeCharacterId = characterId;
                    homeSourceCampaignId = homeCloseSequence = 0; homeReturnSequence = command.Sequence;
                    homeOrigin = "DIVINITY"; homeDestination = RootLevel(destination); homeDivinityClient = client;
                    homeStatus = "RETURNING"; homeReason = "normal_divinity_return_submitted"; homeTick = Environment.TickCount;
                    invoked = true; BeforeInvoke(publish); normalReturn.Invoke(sourceUi, new object[0]); return true;
                }
                finally { System.Threading.Monitor.Exit(client); }
            }
            homeSourceRoot = currentRoot; homeTargetRoot = destination; homeSourceMap = campaign; homeReturnUi = sourceUi; homeList = list;
            homeCharacter = common.Character; homeCharacterId = characterId; homeSourceCampaignId = campaignId;
            homeOrigin = "RETURN"; homeDestination = RootLevel(destination); homeReturnSequence = command.Sequence; homeCloseSequence = 0;
            homeStatus = "RETURNING"; homeReason = "normal_anecdote_return_submitted"; homeTick = Environment.TickCount;
            invoked = true; BeforeInvoke(publish); normalReturn.Invoke(sourceUi, new object[0]); return true;
        }
        private string ChallengeEntryJson()
        { return "{\"status\":" + Command.Quote(entryStatus) + ",\"command_seq\":" + entrySequence + ",\"campaign_id\":" + entryCampaignId + ",\"reason\":" + Command.Quote(entryReason) + "}"; }
        private bool ExactEntryCapitalDemon(Behaviour component, UnityEngine.Object[] all)
        {
            if (!challengeOnly || entryStatus != "WAITING" || entrySequence <= 0 ||
                unchecked((uint)(Environment.TickCount - entryTick)) >= 15000U || contextGate != "PASS" ||
                Observer.Status != "NONE" || Observer.Pending || component == null || component.GetType() != Model.GameType("DemonScript") ||
                !object.ReferenceEquals(currentRoot, entryToRoot) || RootLevel(currentRoot) != "Anecdote" || !currentRoot.gameObject.activeInHierarchy ||
                !object.ReferenceEquals(common.Character, entryCharacter) || characterId != entryCharacterId ||
                campaign == null || !OwnedBy(campaign, currentRoot) || campaignId != entryCampaignId ||
                !object.ReferenceEquals(Read(campaign, "m_campaign"), entryCampaign) ||
                Int(Read(entryCampaign, "idcampaign_type")) != AllowedCampaignType || !Bool(Read(entryCampaign, "unlocked")) ||
                Bool(Read(campaign, "m_bDebug")) || entryFromRoot == currentRoot || RootLevel(entryFromRoot) != "Capital" ||
                entrySourceMain == null || !OwnedBy(entrySourceMain, entryFromRoot)) return false;
            Component load = ReadStatic(Model.FirstType("BaseAsset"), "lds") as Component;
            if (load == null || load.GetType() != Model.FirstType("LoadScreen") || !load.gameObject.activeInHierarchy ||
                !object.ReferenceEquals(Read(load, "currmg"), currentRoot) || !object.ReferenceEquals(Read(load, "oldrmg"), entryFromRoot)) return false;
            try { return object.ReferenceEquals(component, SharedDevelopmentContext.CapitalDemon(all, entryFromRoot, entryCharacter)); }
            catch (ProbeFailure error)
            { if (error.Message != "flower_capital_demon_identity") throw; return false; }
        }
        private void ObserveChallengeEntry(UnityEngine.Object[] all)
        {
            challengeSourceRetirement = false;
            if (entryStatus != "WAITING") return;
            string failure = "";
            if (!object.ReferenceEquals(common.Character, entryCharacter) || characterId != entryCharacterId) failure = "character_changed";
            else if (unchecked((uint)(Environment.TickCount - entryTick)) >= 15000U) failure = "enter_timeout";
            else if (object.ReferenceEquals(currentRoot, entryToRoot) && RootLevel(currentRoot) == "Anecdote" &&
                campaign != null && OwnedBy(campaign, currentRoot) && campaignId == entryCampaignId &&
                object.ReferenceEquals(Read(campaign, "m_campaign"), entryCampaign) && Int(Read(entryCampaign, "idcampaign_type")) == AllowedCampaignType)
            {
                foreach (UnityEngine.Object item in all)
                {
                    Behaviour component = item as Behaviour;
                    if (Visible(component) && ExactEntryCapitalDemon(component, all)) challengeSourceRetirement = true;
                }
                if (challengeSourceRetirement) { entryReason = "matching_challenge_source_retiring"; return; }
                if (ChallengeMapReady()) { entryStatus = "READY"; entryReason = eliteOnly ? "matching_elite_challenge_map_ready" : "matching_normal_challenge_map_ready"; }
                else entryReason = eliteOnly ? "matching_elite_challenge_map_initializing" : "matching_normal_challenge_map_initializing";
                return;
            }
            else if (!object.ReferenceEquals(currentRoot, entryFromRoot)) failure = "enter_destination_mismatch";
            if (failure != "") { entryStatus = "UNKNOWN"; entryReason = failure; }
        }
        private bool ChallengeMapReady()
        {
            if (scenario != null || function != null || panel != null || formation != null || chapter != null || reward != null) return true;
            if (!Visible(campaign) || !Enabled(campaign) || common.Gate == "loading_or_battle" ||
                !Bool(ReadStatic(globalType, "extern_enabled")) || Bool(Read(campaign, "loading")) || Bool(Read(campaign, "collecting"))) return false;
            IDictionary troops = Read(campaign, "m_troops") as IDictionary;
            if (troops == null || troops.Count > 255) return false;
            foreach (DictionaryEntry pair in troops)
            {
                object troop = pair.Value;
                if (troop == null || !Bool(Read(troop, "visible")) || !Bool(Read(troop, "canattack")) || Bool(Read(troop, "killed"))) continue;
                GameObject mesh = Read(troop, "mesh") as GameObject;
                int count; Component script = MeshTroopScript(mesh, out count);
                if (mesh == null || !mesh.activeInHierarchy || mesh.transform.root != currentRoot.transform || script == null ||
                    !object.ReferenceEquals(Read(script, "ParentUI"), campaign) || !object.ReferenceEquals(Read(script, "Troop"), troop)) return false;
            }
            return true;
        }
        private string ChallengeResourcesJson()
        {
            if (!challengeOnly || common == null || common.Character == null) return "null";
            return "{\"gold\":" + Convert.ToInt64(Read(common.Character, "gold")) +
                ",\"bind_gold\":" + Convert.ToInt64(Read(common.Character, "bind_gold")) +
                ",\"stamina\":" + Convert.ToInt64(Read(common.Character, "stamina")) +
                ",\"hero_tries\":" + Convert.ToInt64(Read(common.Character, "anecdote_hero_try")) + "}";
        }

        private void CaptureChallengeList(UnityEngine.Object[] all)
        {
            GameObject page = GameObject.Find("Anecdote/AnecdoteUI");
            challengeList = Select(all, challengeListType, true, delegate(Component c)
            { return c.gameObject == page && RootLevel(OwnerRoot(c)) == "Anecdote"; }, "challenge_list");
            challengeBackdrop = null;
            challengeCandidates = "[]";
            // The normal list is an Anecdote-root modal over the still visible
            // current Campaign map. Keep that backdrop separate from this
            // model's type-2 campaign and observer identity.
            challengeBackdrop = Select(all, campaignType, true, delegate(Component c)
            { return RootLevel(currentRoot) == "Campaign" && OwnedBy(c, currentRoot); }, "challenge_backdrop");
            if (challengeList == null) return;
            IList rows = ChallengeRows();
            StringBuilder json = new StringBuilder("["); bool comma = false;
            for (int i = 0; i < rows.Count; i++)
            {
                object normal = ChallengeCandidate(rows[i]);
                if (normal == null) continue;
                if (eliteOnly && Int(Read(normal, "idcampaign_type")) != 4) continue;
                if (Int(Read(normal, "idcampaign_type")) != AllowedCampaignType) throw new ProbeFailure("challenge_normal_schema");
                if (comma) json.Append(','); comma = true;
                json.Append("{\"campaign_id\":"); json.Append(Int(Read(normal, "id")));
                json.Append(",\"campaign_type\":"); json.Append(AllowedCampaignType);
                if (eliteOnly) { json.Append(",\"star\":"); json.Append(Int(Read(normal, "star_level"))); }
                json.Append(",\"display_index\":"); json.Append(i);
                DiagnosticBool(json, "finished", Bool(Read(normal, "finished")));
                DiagnosticBool(json, "unlocked", Bool(Read(normal, "unlocked")));
                json.Append(",\"gate\":"); json.Append(Command.Quote(ChallengeRowGate(rows[i]))); json.Append('}');
            }
            json.Append(']'); challengeCandidates = json.ToString();
        }
        private IList ChallengeRows()
        {
            object chapterData = Read(challengeList, "m_chapter");
            IList rows = chapterData == null ? null : Read(chapterData, "campaigns") as IList;
            if (rows == null || rows.Count > 512) throw new ProbeFailure("challenge_rows_schema");
            return rows;
        }
        private object ChallengeCandidate(object row) { return Read(row, eliteOnly ? "campaign" : "campaign_elite"); }
        private string ChallengeRowGate(object row)
        {
            object normal = ChallengeCandidate(row);
            if (normal == null || Int(Read(normal, "idcampaign_type")) != AllowedCampaignType) return eliteOnly ? "elite_stage_missing" : "normal_stage_missing";
            if (eliteOnly)
            {
                int star = Int(Read(normal, "star_level"));
                if (star < 1 || star > 3 || !object.ReferenceEquals(Read(row, "campaign_hero" + star), normal)) return "elite_star_identity_unknown";
            }
            if (Bool(Read(normal, "finished"))) return eliteOnly ? "elite_finished" : "normal_finished";
            if (!object.ReferenceEquals(Read(row, "campaign"), normal)) return "displayed_stage_is_not_normal";
            if (!Bool(Read(normal, "unlocked")) || !Bool(Read(normal, "real_unlocked"))) return eliteOnly ? "elite_locked" : "normal_locked";
            return "PASS";
        }
        private bool ChallengeListIdentity()
        {
            return challengeList != null && RootLevel(OwnerRoot(challengeList)) == "Anecdote" &&
                object.ReferenceEquals(ReadStatic(mainType, "m_subui"), challengeList);
        }
        private string ChallengeListGate()
        {
            if (!ChallengeListIdentity()) return "challenge_list_not_interactive";
            if (Bool(Read(challengeList, "loading")) || Bool(Read(challengeList, "collecting"))) return "challenge_list_loading";
            if (campaign != null || panel != null || formation != null || Visible(battle) || result != null || reward != null || chapter != null || scenario != null || function != null || lossAdvice != null)
                return "challenge_list_conflicting_ui";
            if (!CurrentGameIdle() || common.Gate == "loading_or_battle") return "challenge_list_network_pending";
            if (!Enabled(challengeList)) return "challenge_list_not_interactive";
            if (Observer.Pending) return "challenge_list_attempt_pending";
            if (challengeBackdrop != null && (Read(challengeBackdrop, "m_campaign") == null ||
                Int(Read(Read(challengeBackdrop, "m_campaign"), "idcampaign_type")) != 0)) return "challenge_backdrop_type_unknown";
            return "PASS";
        }
        private bool ChallengeBackdropChrome(Behaviour component)
        {
            if (!challengeOnly) return false;
            // RootUI assigns the destination before RootMessenger finishes
            // loading/resuming it. Preserve only the exact entry source page
            // while those known scene roots overlap; no target is selected.
            if (entryStatus == "WAITING" && object.ReferenceEquals(common.Character, entryCharacter) && characterId == entryCharacterId &&
                (object.ReferenceEquals(currentRoot, entryFromRoot) || object.ReferenceEquals(currentRoot, entryToRoot)))
            {
                if (component == entrySourceMain && OwnedBy(component, entryFromRoot)) return true;
                if (entrySourceMap != null && OwnedBy(entrySourceMap, entryFromRoot))
                {
                    if (component == entrySourceMap) return true;
                    string sourceName = component.GetType().FullName;
                    if ((sourceName == "CampaignTroopScript" || sourceName == "CampaignSignScript" || sourceName == "CampaignChestScript") &&
                        component.GetType() == Model.GameType(sourceName) && component.transform.root == entryFromRoot.transform &&
                        object.ReferenceEquals(Read(component, "ParentUI"), entrySourceMap)) return true;
                }
            }
            // Readiness never determines whether an owned scene child is a
            // known component. The full list/entry gates still admit actions.
            bool list = Phase == "CHALLENGE_LIST" && ChallengeListIdentity();
            bool entering = entryStatus == "WAITING" && object.ReferenceEquals(currentRoot, entryFromRoot) &&
                object.ReferenceEquals(common.Character, entryCharacter) && characterId == entryCharacterId;
            bool main = Phase == "MAIN";
            if ((!list && !entering && !main) || challengeBackdrop == null ||
                !OwnedBy(challengeBackdrop, currentRoot) || RootLevel(currentRoot) != "Campaign") return false;
            object backdropData = Read(challengeBackdrop, "m_campaign");
            if (backdropData == null || Int(Read(backdropData, "idcampaign_type")) != 0) return false;
            if (component == challengeBackdrop) return true;
            string name = component.GetType().FullName;
            if (name != "CampaignTroopScript" && name != "CampaignSignScript" && name != "CampaignChestScript") return false;
            if (component.GetType() != Model.GameType(name) || component.transform.root != currentRoot.transform ||
                !object.ReferenceEquals(Read(component, "ParentUI"), challengeBackdrop)) return false;
            return true; // Scene ownership only; action eligibility is checked separately.
        }
        private string ChallengeEntryPageGate()
        {
            if (common.Main == null || !OwnedBy(common.Main, currentRoot) || !common.InteractionEnabled ||
                (RootLevel(currentRoot) != "Campaign" && RootLevel(currentRoot) != "Capital") ||
                ReadStatic(mainType, "m_subui") != null || common.Gate == "loading_or_battle" ||
                Observer.Pending || NavigationPending || !CurrentGameIdle()) return "challenge_entry_gate";
            return "PASS";
        }
        private bool ChallengeVisualEffect(Behaviour component)
        {
            // This pinned class only draws labels. Its optional OnClose can
            // carry behavior, so admit only the two original null-callback
            // effects held by our already scoped campaign/reward owners.
            if (!challengeOnly || component.GetType() != Model.FirstType("AnimEffect") || Read(component, "OnClose") != null) return false;
            return reward != null && component.gameObject == reward.gameObject &&
                    object.ReferenceEquals(Read(reward, "levelup_effect"), component) ||
                campaign != null && component.gameObject == campaign.gameObject &&
                    object.ReferenceEquals(Read(campaign, "clear_effect"), component);
        }
        private bool ChallengeRetiredTroopChrome(Behaviour component)
        {
            // DestroyTroops clears troop.mesh immediately; Unity destroys the
            // old component later. This exact Web_OnGUI returns immediately
            // when Troop.mesh is null, regardless of its stale enabled flag.
            if (!challengeOnly || component.GetType() != Model.GameType("CampaignTroopScript")) return false;
            Component parent = Read(component, "ParentUI") as Component;
            Component owner = parent == null ? null : OwnerRoot(parent);
            if (parent == null || parent.GetType() != campaignType || owner == null || object.ReferenceEquals(owner, currentRoot) ||
                component.transform.root != owner.transform || (RootLevel(owner) != "Campaign" && RootLevel(owner) != "Anecdote")) return false;
            object troop = Read(component, "Troop");
            return troop != null && Read(troop, "mesh") as GameObject == null;
        }
        private string ChallengeCooldownGate(object troop)
        {
            uint key = (uint)((campaignId << 16) | Int(Read(troop, "idtroop")));
            object cooldown = Invoke(common.Character, "getCampaignCooldown", new Type[] { typeof(uint) }, new object[] { key });
            if (cooldown == null) return "PASS";
            Type timeType = Model.FirstType("TimeUtil");
            PropertyInfo now = timeType.GetProperty("Now", Static);
            if (now == null || !now.CanRead) throw new ProbeFailure("challenge_clock_schema");
            return Convert.ToUInt64(Read(cooldown, "time")) > Convert.ToUInt64(now.GetValue(null, null)) ? "challenge_cooldown_active" : "PASS";
        }
        private bool ExecuteChallengeNavigation(Command command, CampaignPublication publish, out bool invoked, out string why)
        {
            invoked = false; why = "";
            if (command.CampaignAction == "enter")
            {
                if (Phase != "MAIN" || ChallengeEntryPageGate() != "PASS") { why = "challenge_entry_gate"; return false; }
                MethodInfo open = mainType.GetMethod("_anecdote", Static, null, Type.EmptyTypes, null);
                if (open == null || open.ReturnType != typeof(void)) throw new ProbeFailure("challenge_entry_schema");
                invoked = true; BeforeInvoke(publish); open.Invoke(null, new object[0]); return true;
            }
            if (command.CampaignAction == "enter_challenge")
            {
                if (Phase != "CHALLENGE_LIST" || ChallengeListGate() != "PASS" || command.TroopId != 0) { why = "challenge_list_entry_gate"; return false; }
                IList rows = ChallengeRows(); object selected = null; int index = -1;
                for (int i = 0; i < rows.Count; i++)
                {
                    object normal = ChallengeCandidate(rows[i]);
                    if (normal == null || Int(Read(normal, "id")) != command.CampaignId) continue;
                    if (selected != null) { why = "challenge_candidate_ambiguous"; return false; }
                    selected = rows[i]; index = i;
                }
                if (selected == null || ChallengeRowGate(selected) != "PASS") { why = eliteOnly ? "elite_challenge_candidate_changed" : "normal_challenge_candidate_changed"; return false; }
                if (eliteOnly && Int(Read(ChallengeCandidate(selected), "star_level")) != command.EliteStar)
                { why = "elite_star_changed"; return false; }
                Component owner = OwnerRoot(challengeList);
                FieldInfo selectedField = challengeListType.GetField("cur_ac", Static), indexField = challengeListType.GetField("cur_idx", Static);
                if (selectedField == null || selectedField.FieldType != selected.GetType() || indexField == null || indexField.FieldType != typeof(int)) throw new ProbeFailure("challenge_selection_schema");
                entryFromRoot = currentRoot; entryToRoot = owner; entryCharacter = common.Character; entryCharacterId = characterId;
                entrySourceMap = challengeBackdrop; entrySourceMain = common.Main;
                entryCampaign = ChallengeCandidate(selected); entryCampaignId = command.CampaignId;
                entrySequence = command.Sequence; entryTick = Environment.TickCount; entryStatus = "WAITING"; entryReason = eliteOnly ? "submitted_elite_challenge_enter" : "submitted_normal_challenge_enter";
                invoked = true; BeforeInvoke(publish);
                InvokeVoid(owner, "ToDisable", Type.EmptyTypes, new object[0]);
                selectedField.SetValue(null, selected); indexField.SetValue(null, index + 1);
                InvokeVoid(globalUi, "cs_campaign_enter", new Type[] { typeof(ushort), owner.GetType() }, new object[] { (ushort)command.CampaignId, owner });
                InvokeVoid(challengeList, "Hide", Type.EmptyTypes, new object[0]); SetSubUiNull(); return true;
            }
            if (Phase != "RESULT" || chapter == null || scenario != null || !Enabled(chapter) || !Bool(Read(chapter, "m_bAnecdote")) || Bool(Read(chapter, "m_talent")) ||
                (!((Observer.Outcome == "WIN" || eliteOnly && Observer.Status == "CONTINUED" && frame.CampaignFinished) &&
                    Observer.SettledCampaignContext(frame)) && FreshResultNavigation() != "next_chapter")) { why = "challenge_return_unavailable"; return false; }
            object load = null;
            for (Type t = chapter.GetType(); t != null; t = t.BaseType)
                if (t.Name == "BaseAsset") { load = ReadStatic(t, "lds"); break; }
            if (load == null || globalUi == null) { why = "challenge_return_root_missing"; return false; }
            Component chapterRoot = Read(chapter, "rmg") as Component;
            Component oldRoot = Invoke(load, "GetOldMainUI", Type.EmptyTypes, new object[0]) as Component;
            string destination = oldRoot == null || (Read(oldRoot, "LevelID") as string) == "Login" ? "Campaign" : Read(oldRoot, "LevelID") as string;
            if (destination != "Campaign" && destination != "Capital") { why = "challenge_return_destination_unknown"; return false; }
            MethodInfo add = null;
            foreach (MethodInfo method in Model.FirstType("RootBase").GetMethods(Static))
                if (method.Name == "add_UIFunc" && method.GetParameters().Length == 1) { if (add != null) throw new ProbeFailure("challenge_return_callback_ambiguous"); add = method; }
            MethodInfo reopen = chapterType.GetMethod("AnecdoteSelect", Declared, null, Type.EmptyTypes, null);
            if (add == null || add.GetParameters().Length != 1 || reopen == null) throw new ProbeFailure("challenge_return_callback_schema");
            Delegate callback = Delegate.CreateDelegate(add.GetParameters()[0].ParameterType, chapter, reopen);
            invoked = true; BeforeInvoke(publish);
            InvokeVoid(chapter, "ShowNewbieQuest", Type.EmptyTypes, new object[0]);
            InvokeVoid(load, "SetOldUI", new Type[] { chapterRoot.GetType() }, new object[] { chapterRoot });
            InvokeVoid(globalUi, "LoadLevel", new Type[] { typeof(string), chapterRoot.GetType() }, new object[] { destination, chapterRoot });
            add.Invoke(null, new object[] { callback }); return true;
        }
    }
}
