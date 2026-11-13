# Page 46 ZhaoZiLongNewer / 七进七出

Source implementation only; no tests, build, review, runtime reload, or game operations were performed by this assignment.

The controller independently observed page46 VISIBLE and selected its 七进七出 page, showing task4: 交纳2000橙魂. This distinguishes it from page33 SevenDaysLogin.

Fixed route: task201, row premium.zhaoyun.{quest69}. The normal stock method is PremiumUI.cs_zhaoyun_quest_new(Global.isNewNewbieActivity), opcode948, with the unchanged original sc_zhaoyun_quest_new callback. No call to ZhaoyunGoldCostNewer, opcode1833, opcode949, RootUI.Coin, recruit-detail, formation, equip, or share is introduced.

Qualification:
- Global.isNewerZhaoYunQuest, quest69 in 0..4, MainUI.checkUI("army_recruit"), and premium_general_quest(69) must be established from current game data.
- Stage0: level>=70. Stage1: the stock doHaveGeneral(69), including its own higher-form and in/out-squad logic. Stages2/3: the stock checkQuestNeed(startid+stage). Stage4: final recruit branch. Current VIP>=7 follows the stock shortcut, which bypasses ordinary-resource removal in its callback.
- For VIP<7, actual consumption is determined by the template the callback uses: premium_quest_new when isNewNewbieActivity, otherwise premium_quest. Missing/negative costs are not treated as free. Stage0..3 also require the old premium_quest entry required by the page drawing code.
- Positive ordinary costs are authorized only for the explicitly traced resource types 4=money, 10=enchant_stone, 39=generalsoul_purple, 40=generalsoul_orange. Current stock fields must cover the actual selected cost. Type7 gold is EXCLUDED; positive unknown types remain UNSUPPORTED. Zero-amount costs permit the normal claim. The separate gold_cost column is never permission to use the gold shortcut.

Rows expose current (zero-based quest69), limit=5, resource_type, resource_cost, and resource_available. Python EventRewardItem preserves these fields. For the observed task4, the expected row is premium.zhaoyun.3 with type40 and template cost2000; actual available orange soul is read at execution time. Insufficiency yields NOT_ELIGIBLE / event_rewards_zhaoyun_resources_insufficient without sending.

Settlement independently parses the pinned SCZhaoyunQuestNew_0x3B4 response: Operate.OK, the returned step, and type0/36 bonus identity. The live quest69 must match the returned advanced step, bounded by5. Raw receipt bytes remain attached to the exact stage key. The original callback owns reward creation and resource removal; this model never changes quest flags or grants inventory itself.

Special presentation cleanup is explicitly authorized as a non-player-button action. Original SpecialGetUI.Hide calls base.Hide and resets add_to_treasury=false. __BaseUI.Hide only hides the matching tooltip, clears frame animation, and calls Hideme; Hideme updates visible UI accounting and disables that component. clearFrameAnimation only clears its dictionary. None of these paths recruits into the current formation, auto-equips, calls a resource method, or sends a packet. Those effects belong to the visible go-to-formation/equipment buttons, which are never invoked.

A special display is accepted only when newly shown after this request, with showtype general/artifact and tempid matching the type0/36 bonus in this exact receipt. It uses the existing close_reward action, with no protocol change. Unknown/multiple special reward identities remain UNKNOWN rather than closing an unrelated window. A preexisting special display blocks entry. Original BattleResultUI.OnClose must still target the request's PremiumUI; final activity close requires the special/reward/prompt cleanup to finish.

Raw sources: PremiumUI.cs.txt (checkZhaoZiLongNewer, DrawZhaoZiLongNewer, checkQuestNeed, doHaveGeneral, cs/sc_zhaoyun_quest_new); Database._premium_general_quest.cs.txt; Database._premium_quest.cs.txt; Database._premium_quest_new.cs.txt; game.net.data.StoC.SCZhaoyunQuestNew_0x3B4.cs.txt; game.net.data.StoC.Treasure.cs.txt. Existing SpecialGetUI source is data/acceptance/20261031-campaign/20-special-get-action/SpecialGetUI.cs.txt; inherited Hide/Hideme/clearFrameAnimation are in research/unity-object-layer/object-map-__BaseUI.cs.txt.

Limit: no live step, resource count, server response, inventory result, or display cleanup has been independently exercised by this leaf. The controller owns build and real execution.
