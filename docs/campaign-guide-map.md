# 一次性主线征战：固定通关点、剧情与教学

Status: DONE_WITH_CONCERNS。固定关卡与剧情的关联方式、通关触发链、教学模式和正常Skip的后置行为已由pinned源码/元数据确认；控制器提供的冻结快照进一步确认当前主线 **57:3** 的真实模板/对象字段。未来节点与Scenario647的section/trigger行仍缺失，不能填造其内容或教学完成状态。本席只读静态源码和指定冻结artifact，不读取运行进程/UI、不发输入或业务请求，不修改安装、生产源码或协议；仅维护本报告。

当前业务语义：**未通关主线正常挑战；确认WIN后按游戏真实进度继续；已通关的troop/campaign不重复。失败才可重试，三连败停止。** 剧情在游戏指定的通关点自然出现，再走当前正式OnGUI Skip；不主动设置剧情、不改关卡进度、不伪造教学完成。本次继续使用控制器已冻结的`scoped-campaign-gui-skip-v1`，不扩wire协议。

## 证据边界

本报告的E/指`data/campaign-research/`，O/指`research/unity-object-layer/object-map-`，例如O/Global是`object-map-Global.cs.txt`。完整程序集身份见E/`input-hashes.json`；使用现成ILSpyCmd 9.1.0.7988离线反编译、PEReader读取当前安装DLL，不加载执行目标程序集。新查`db_loader`、`DBLoaderUI`、`Scenario`和chrome类型时仅将命名片段输出到工具结果，按本次权限不新增原始dump。

安装的`C:\Users\yu\AppData\Local\JiangShen\pc\js_Data\StreamingAssets`不存在；`js_Data/Resources`仅有`unity default resources`。`DBLoaderUI.PrepareLoading/Show`表明普通路径通过AssetBundle `db_template1/2/3`各取同名TextAsset，再反序列化成三份模板；没有证据证明这三份模板行位于已授权的StreamingAssets。本席没有扩扫缓存、用户配置或个人目录，也没有下载模板。

精确定位：CSharp `DBLoaderUI` TypeDef020000EF、`PrepareLoading`060008A0/RVA7EEF4、`Show`060008A3/RVA7F080；dbdata `Database.db_loader.post_load` Method060022A7/RVA2F3AC。`db_loader`普通反编译行518–586（见下方命名片段）承载真实映射。

新增实值证据来自`data/acceptance/20261031-campaign/07-selection-diagnostics-connected/{state.json,capture.json}`，控制器冻结时间`2026-10-31T10:25:21.108769+08:00`，build `483023e51f3edfd010f8231d28bd1fb515c905b1bd06161fa322eb0f868a7206`，state/ack序号均85，`paired_state_ack=true`，runtime manifest SHA256=`49fcf093716fe324885c3100e455eb2d89882008f5f561e83550a3c20d01b51f`。本席核对了state.campaign与capture.campaign的下述字段一致；这是对已冻结资料的只读复核，不是本席新增live观测。不在报告复制角色账号标识。

## 固定映射的正式来源

| 固定字段/关系 | 证据 | 判据 |
|---|---|---|
| campaign与troop键为`ushort idcampaign`、`byte idtroop`；教学标记isNewbie、Newbie_Skill、Newbie_formation、NewbieMessage*在troop模板内 | E/Database._campaign_troop.cs.txt:8–12,80–100；Set(db_campaign_troop):187–229 | PASS，schema；一般行数值UNVERIFIED |
| 一个Scenario属于指定主线敌军：`idscenario_type==1`，`param1`解析campaign，`param2`解析troop | pinned dbdata db_loader.post_load反编译:548–563 | PASS，构建关系 |
| `param0=clickf/visible/attackf/victoryf/failure/system`分别写troop的六个m_scenario* ID | 同方法:564–586；E/Database._campaign_troop.cs.txt:114–124 | PASS，不代表当前剧情从这些路由随机选择 |
| 各字段非零时，CampaignTroop构造函数以确切模板ID创建Scenario | E/Database.CampaignTroop.cs.txt:124–161 | PASS，实例来源 |
| Scenario从`scenario_section.idscenario`聚合section；triggers按`,`与`:`解析成idtrigger/param0/param1；Scenario.make按idsection排序再生成展示sections | db_loader.post_load反编译:518–545；firstpass Scenario.make反编译:20–29 | PASS，触发schema；实际trigger ID/参数行UNVERIFIED。wire的section_index是展示列表索引，不能直接当模板idsection |

命名片段（pinned dbdata，post_load）：

```csharp
if (scenario.idscenario_type == 1) {             // line 552
    uint campaign = uint.Parse(scenario.param1); // line 554
    byte troop = byte.Parse(scenario.param2);    // line 555
    // validate campaign.m_troops[troop]
    // param0=="victoryf" -> troop.m_scenarioVictoryF=scenario.id (576-578)
}
```

dbdata `_scenario` Type02000413：id Field04001D93(uint)，param0/1/2 Fields04001D95/96/97(string)，idscenario_type Field04001D9A(byte)，m_sections Field04001D9B。`_scenario_section` Type02000414：idscenario04001D9D(uint)、idsection04001D9E(byte)、triggers04001DA1(string)、m_triggers04001DA3。`_scenario_trigger` Type02000415：id04001DA4(byte)、type04001DA7(string)。firstpass `Scenario` Type02000105继承`Database._scenario`，make Method060005EF/RVA4638C。这些token仅为当前DLL元数据标识，不能当内存偏移。

## 指定关卡通关 → 胜利剧情 → 正常Skip → 推进

1. `BattleUI.BattleEnd`只对`SCBattleEnd.result==0 && type==0`调用`CampaignUI.BattleEnd(pkt)`（O/BattleUI:969–1013）。这里以该次权威`pkt.iswin`驱动进度，不能用初始SCCampaignBattle.iswin/战报等级替代commit。
2. 普通主线type=0进入`else if(!campaignTroop.killed)`：finish_count++，达到finish_max才killed=true，并将该指定troop的`scenarioVictoryF/scenarioSystem`保存到父UI待处理字段（E/CampaignUI:748–770）。Boss同时使campaign.finished=true，遍历模板m_next_campaigns解锁并更新character.idcampaign（:785–796）。**WIN与killed的对应受finish_max约束；未读到实际模板不能假定所有troop一次WIN就killed。**
3. WIN后setTroopAttacks按该troop.m_next_troops激活后续对象，可能安排出生和visible剧情（E/CampaignUI:814–817；E/CampaignBaseUI:1345–1418）。这是图关系，不使用ID+1猜下一关。已killed/finished节点不作为自动重打目标。
4. 返回地图PostInit、开箱/场景转换流程自然进入`ScenarioBattle()`。有待处理胜利剧情时，游戏显示已绑定的scenarioVictoryF，并指定OnClose=`ChangeSignModel`；失败剧情另有分支（E/CampaignBaseUI:784–804）。本席不调用setScenario。
5. 正常Skip会逐个处理尚未执行的section trigger，再执行末段FunctionLoad；`param0==system`发1040，否则Close。Close调用游戏保存的OnClose（E/ScenarioUI:273–307,653–689）。因此跳过文本仍保留指定关卡的进度/功能触发，不能用Hide或直接回地图替代。
6. 胜利剧情结束回调继续`ChangeSignModel→ChangeActiveSign→TroopBorn→ScenarioVisible→BattleResult`，可能经过计时/出生动画和下一段剧情；最终BonusClose在scenario队列为空时恢复根输入（E/CampaignBaseUI:593–610,632–711,743–804）。确认Skip发送不等于该链已经全部结束。等游戏回到稳定可用地图、目标状态更新，再选未通关下一节点。

用户指出“剧情在指定关卡通过后出现”与上述victoryf链吻合。客户端也支持开场/点击等独立剧情路由；这些schema不用于推断当前刚出现的剧情属于哪条路由。必须以实际troop模板行或控制器提供的通关与剧情身份关联为证。

## 目前能给出的固定数值

| 固定值/点位 | 触发与正常后置 | 证据/状态 |
|---|---|---|
| 起始campaign默认ID **70** | `Global.idcampaign_start=70`为静态初始化值，脚本按该字段判断起始章；当前运行值需控制器核实 | O/Global:73，PASS初始化值 |
| 起始章troop **1** | 活动路标切换完成，若该troop尚未killed，游戏自然`wantCombat(1)` | E/CampaignBaseUI:169–172，PASS硬编码；当前是否处于此点UNVERIFIED |
| 起始章troop **2已killed且3未killed** → Scenario **466** | 路标切换计时结束时显示466；正常结束回调TroopBorn | E/CampaignBaseUI:154–167，PASS固定条件；466的section/trigger行未取得，不能据ID宣布全部无副作用 |
| 起始章首次visible剧情后的场景转换 → Scenario **1130** | `ScenarioFirstVisible`给start troop的visible剧情绑定SpecialActiveSign1，后者置flag并SwitchSceneSet(1)；Update在约1秒后自然显示1130，回调ChestOpen | E/CampaignBaseUI:124–133,830–880，PASS硬编码链；初始visible剧情ID、1130内容/触发参数UNVERIFIED |
| campaign **785**首个start troop的visible剧情 | 游戏绑定SpecialActiveSign3作场景转换；具体scenario ID来自模板 | E/CampaignBaseUI:853–859,889–894；PASS分支，数值映射UNVERIFIED；不能当当前“接下来一章” |
| 当前主线 **57:3** | 冻结诊断取得真实字段：finish_count0/finish_max1、isNewbie1、Newbie_Skill2188、Newbie_formation1、m_scenarioSystem647，其他五scenario ID为0 | PASS该快照实值，详见下一节；不把647当作已显示/已完成 |
| 此前控制器见到的“刘备剧情” | 控制器告知已正常Skip并确认奖励；该段身份未出现在本次冻结资料中 | 此前中文名称→数字映射UNVERIFIED，不能把466/1130/647套入 |
| 57:3之后几个主线关卡 | 固定schema已证，未来节点行尚未取得 | UNVERIFIED，等待控制器下一命名冻结快照，不主动读取live或触发 |

### 当前57:3的冻结实值与system647时机

在`state.json`的`campaign.selection_diagnostics`（capture.json同路径副本一致）：

| 范围 | 已确认值 | 能证明/尚不能证明 |
|---|---|---|
| 当前地图 | campaign_id57；角色当前主线idcampaign57；type0、unlocked=true、finished=false、enabled=true；phase=CAMPAIGN | PASS当前未完成主线；顶层troop_id为0，表示未选中，不能误写为3 |
| 唯一eligible节点 | troop_id3、idcampaign57/idtroop3；visible/canattack=true、killed=false、clicked=false；chanllege_level0 | PASS当前可候选的一次性未通关节点；没有证明新战斗或通关 |
| 模板/次数 | click_mode2、limit_count-1、finish_count0、finish_max1 | PASS实值；游戏wantTroopUI的非1分支正常打开敌军面板，limit_count!=0门禁接受-1（E/CampaignBaseUI:1299–1308；E/CampaignTroopUI:222）；无需用假模板值补齐 |
| 教学目标 | isNewbie1、Newbie_Skill2188、Newbie_formation1 | PASS固定关卡教学指针；不证明guide当前显示、具体guide_mode、技能名称/拥有状态、formation1的spot内容、任何训练操作或完成 |
| 六类Scenario | ClickF=0、Visible=0、AttackF=0、VictoryF=0、Failure=0、**System=647** | PASS此节点关联的真实ID；647具体section、trigger和功能副作用UNVERIFIED；此节点没有非零victoryf，可由system链在通关后安排剧情 |
| 场景对象绑定 | show_arrow=true；mesh存在/active/root_matches；mesh_script_count1、enabled/owner_matches=true；select_gate=PASS；相关出生/剧情/路标flag均false | PASS本次冻结快照的绑定与门禁；后续每个动作仍须重新验证上下文 |

对57:3可从pinned代码给出**预期时机**，不据ID声称已执行：若该次权威SCBattleEnd成功且WIN，当前finish_count0→1达到finish_max1，游戏设killed并把troop.scenarioSystem保存到父m_scenarioSystem（E/CampaignUI:748–770）。这里VictoryF=0，所以不安排该troop的直接胜利剧情；后续`BattleResult→MainLevelOK`调用`Global.add_scenario(m_scenarioSystem)`并清待处理引用（E/CampaignBaseUI:573–578,632–635；O/Global:328–331；O/Database.Character:579–582）。RootUI在loading/可见弹窗等阻挡消失后，从character.scenarios_队首自然`PlayScenario`，再绑定`ScenarioEnd`作关闭回调（O/RootUI:1201–1223,1239–1251）。只有队首恰为647时才会显示647，不能跳过队列或手动调用setScenario。

正常Skip如遇system分支会发1040；其正常Close调用`ScenarioEnd`移除队首、恢复level_rmg输入（E/ScenarioUI:653–689,606–650；O/RootUI:1167–1170）。647的实际param0、段落与trigger未包含在本快照中，故这里只解释已证system对象的自然调度链，不宣称647一定无附加功能、一定免费或代表培养完成。若该次失败，isNewbie1可能引出BattleGuide，模式依当前状态选择；没有证据可预先固定它。

## 教学分类与普通操作边界

固定“在哪个关卡有教学”由模板isNewbie/Newbie_*决定；实际BattleGuideUI模式还取决于当前资源、装备、技能和阵型。不能只凭关卡ID固定guide_mode，也不能把所有教学一律当纯文本。

| 已识别种类 | 正常操作/后置状态 | 资源副作用与结论 | 精确证据 |
|---|---|---|---|
| 普通Scenario文本 | 当前正式OnGUI Skip；触发保留，等正常OnClose及后续场景稳定 | 已有scoped skip可继续；实际trigger行缺失时不能提前归类为“只有文本” | E/ScenarioUI:491–570,572–610,653–689 |
| FormationUI“新战技”提示 | 已拥有skills与newskillid生成newhint_skill；开始按钮不依赖它；本次按控制器决定ignoreHint，保留既有技能/兵种/阵型 | ignoreHint本身无动作/扣费。选择技能会发338配置和342提示已读，本次不自动选择 | O/FormationUI:3134–3147,3667–3675,3881–3925；O/Database.Character:3020–3056 |
| BattleGuide mode **3** 升星 | MyLoading先尝试该模式；CheckStarTech要求下一star_tech模板存在且practice>=require_practice、army_star解锁；普通查看进入StarTechUI(isguide) | 有明确require_practice门槛，实际升级扣费/参数未审计；需另审核培养，不自动点击升级 | E/BattleGuideUI:91–109,261–281,538–541 |
| mode **2** 装备 | 前一模式不成立时CheckEquip；已上阵存在空装备槽且仓库有未分配装备，普通查看打开EquipmentUI(bguide) | 是否免费穿戴/是否还带强化尚未审计；需确认确切对象/动作，不伪Step | E/BattleGuideUI:110–119,256–268,543–585 |
| mode **4** 战技 | CheckSkill为troop.Newbie_Skill!=0且主角当前技能不等该ID；普通查看进入当前征战FormationUI(isguide) | 已拥有技能选择链已静态审计为无客户端费用字段，但改技能是配置变更；本次保留现有技能；未拥有技能/学习不在已审计链 | E/BattleGuideUI:120–129,282–305,386–434,587–594 |
| mode **1** 布阵 | 用troop.Newbie_formation模板spot1..8比较当前form；普通查看打开带真实OnAttack委托的NPCBattle | 开始/保存会发298阵型配置；不能用Step/Hide冒充满足模板布阵 | E/BattleGuideUI:130–141,235–255,596–630；O/FormationUI:719–725,2828–2910 |
| mode **6** 强化 / **5** 战报 | 上述模式均不适用时尝试6，general_enchant未开回退5；普通查看分别EquipmentUI(bguide)或CampaignReportUI | 强化消耗未审核；战报只供观察，不当新attempt/不自动回放；需另审核培养 | E/BattleGuideUI:142–153,306–326 |
| NewbieQuestUI | 固定quest_type含升星/装备/强化/布阵/等级/指定troop或campaign通关等；实际quest ID来自模板与触发 | 不能直接finishQuest/cs_newbie_quest_finish伪完成；普通满足条件/领奖仍应逐项核查 | E/NewbieQuestUI:169–294,1478–1535 |

BattleGuide的入口也有正式条件：战报等级<=5且该troop.isNewbie==1时记录guide；返回地图的BattleResult才MyLoading（E/CampaignUI:559–578；E/CampaignBaseUI:654–660）。这是失败后培养建议，与胜利剧情链分别识别；不将其计成新的失败或未经允许改配置。SetMode的顺序是3→2→4→1，完全不适用才尝试6/5；`Step()`只切换模式，不能证明培养完成（E/BattleGuideUI:41–49,91–183）。

### Skip中的功能触发应保留

* `button`：添加/展开正式UI、设置first标记，可能播放新功能图标动画。trigger ui_button ID **50**还初始化grave本地次数与首层记录（E/ScenarioUI:504–558）。这不是购买，但也不是无状态文本。
* `guide`：按正式ui_button名设置GuideCheck（:560–568）。
* 末段`button_function`：FunctionLoad打开ScenarioFunctionUI，不可假设它有通用免费确认（:572–598）。
* system剧情结束发1040；响应可据`newbie` trigger追加具体NewbieQuest。`tutorial`分支只解析整数，不可由解析成功声称教学已完成（:606–650）。
* 特定clickf末段含`quest`时，ScenarioClickEnd不直接打开敌军UI（E/CampaignBaseUI:1264–1297）。应等待该任务真实正常流程，不能再次点敌军绕过。

## 最少补证：固定数值映射与当前推进

已有wire足以记录一次当前观察：同一process/character/session下的`campaign_id`、`troop_id`、`tutorial.kind`、`tutorial.step`（当前实现为`scenario_id:section_index`）、`tutorial.gate`，加本次已确认attempt的campaign/troop/result/report身份及随后的地图可用状态。这能核实“哪个通关后出现哪段剧情”，不能自动恢复尚未经过的未来模板行。

当前57:3的troop实值已补齐，不再要求重取这些字段。要完成其余**固定表**，缺的最小资料是控制器提供的后续命名冻结片段：

1. 57:3之后下一批未通关troop（有界，至多5个）的`idcampaign/idtroop/start/finish_max/isNewbie/Newbie_Skill/Newbie_formation/m_scenarioVisible/m_scenarioClickF/m_scenarioAttackF/m_scenarioVictoryF/m_scenarioFailure/m_scenarioSystem/m_next_troops`。本次57:3的start/m_next_troops也未包含，不能猜下一节点ID。
2. 已知**647**及未来非零scenario对应的`id/idscenario_type/param0/param1/param2`和sections的`idsection/triggers`，以及这些idtrigger的正式`type/param0/param1`。不需要玩家凭据、完整文本/全量模板dump。
3. 若当前实际出现BattleGuide，仅需其`guide_mode`、绑定campaign/troop、新手目标ID与当前目标状态；不要调用Step探测。不改变本次wire协议；可用控制器已授权的单独静态冻结资料或它提供的既有snapshot证据补证。

## 定向核查与未完成项

| 判据 | 结果 |
|---|---|
| 指定关卡模板→六种Scenario固定关系 | PASS，post_load命名方法+字段证据 |
| 一次性通关字段更新、Boss完成、下一节点图、victoryf自然触发 | PASS，已追踪result==0/type==0→iswin→finish阈值→killed/finished→剧情回调 |
| 正常Skip保留触发/OnClose/后续出生与奖励 | PASS，命名方法体；实机效果由控制器验收 |
| 起始默认70、troop1/2/3、Scenario466/1130硬编码 | PASS，条件如表；当前是否命中UNVERIFIED |
| 当前57:3的模板/教学指针、system647及场景绑定 | PASS，build483...冻结state/capture配对一致；未把该快照当新auto运行或通关验收 |
| 647具体段/触发、此前“刘备”身份、未来几个关卡映射 | UNVERIFIED，缺相应模板/后续命名冻结快照 |
| 培养操作的完整费用与服务端结果 | UNVERIFIED，未获得指定动作/模板金额；不自动培养 |
| 运行访问、主线程执行、自动三连败、实际通关/教学 | 本席未测试，控制器独立负责 |

本轮只做命名源码/继承/metadata定向核查和本报告；不运行fullsuite/native build，不重写生产/测试/旧报告。最初Scenario按Database命名空间查找失败，现已由metadata确认它是firstpass全局类型Scenario，且BaseType为Database._scenario；未凭失败推断类型不存在。

附：bridge席所问packet getter已独立PEReader确证：CSharp BattleReportInterpreter2 Type02000296，m_report Field04002134，get_packet Method060023D7/RVA1C7128，IL=`02 7B 34 21 00 04 2A`，即ldarg.0/ldfld/ret，无副作用。Replay不会更换该对象，因此固定packet引用仍需配合排除Replay；此补充已直接发bridge席，不构成新协议。

## 控制器后续实机补证

以下追加来自实际运行，不改写前述静态调查的历史范围。证据均在 `data/acceptance/20261031-campaign/`。

| 通关点/步骤 | 实际观察 | 范围 |
|---|---|---|
| 57:3后647:0 | 正常确认结果及奖励后出现；手动使用游戏 Skip，自然回到稳定地图，原目标仍1/1且killed | `10-settlement-readonly-live/`；旧场次回执仍UNKNOWN，不补写为WIN |
| 57:5后531:0 | 已确认最终1032 WIN，正常奖励后自动Skip | `15-first-confirmed-auto-win/`；首次过渡曾需一次GUI继续，等待缺口随后修复 |
| 57:7后653:0→653:2 | 先自动Skip文本，再展示招财/采石新功能介绍 | 同15；不主动调用这两个功能 |
| 653:2 | 最终版单次正常继续，实际WAITING_MOVEMENT→WAITING_SCENARIO_END→自然关闭，完成后才选择下一未通关目标 | `19-final-live-intro/`；不重复Skip、不直接Close、不点击功能入口 |
| 57:10后556:0 | 已确认1032 WIN，自动Skip、等待过渡、确认奖励后出现SpecialGetUI“明月” | 同19；保留游戏真实节点顺序，未按ID加一 |
| SpecialGetUI“立即参战” | 源码包含提交上阵298、自动装备771及打开布阵；可能移用其他武将装备，关闭布阵也不能回滚 | `20-special-get-action/REPORT.md`；当前未执行，等待是否允许这类配置教学的选择 |

这组实际序列不填造尚未导出的其他模板行，也不证明真实LOSS或连续三败停止。
