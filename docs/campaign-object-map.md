# 征战自动推进：固定对象契约与证据

Status: DONE_WITH_CONCERNS。离线对象/方法/结果/教学链已定位；未读取游戏进程、执行方法、输入、发业务请求或修改安装/用户配置。这里只提出固定 allowlist 候选，不证明运行时可调用。生产实现、主线程桥和实机验收归控制器。

用户后续明确选择游戏自带的“跳过剧情”。本报告初版关于不直接调用 `scenarioSkip` 的建议已被替换：使用正常按钮对应的完整分支，保留触发与结束回调，并满足真实 GUI 上下文；后续未知教学另行暂停。`scenarioSkip(Rect)` 的 `newUIList` 分支含 `GUI.DrawTexture`，不能仅把它当成可在任意 Update 中调用的普通函数。控制器已手动点击当前 Skip 并确认奖励后回到主线地图；自动接口和实机业务的验证仍归正式验收记录。

## 证据身份

安装目录取自 `jiangshen_assistant/mono_runtime.py:19`：`C:\Users\yu\AppData\Local\JiangShen\pc\js_Data\Managed`。本次 SHA256 与既有 pin 相符：CSharp `c1c8ebac...994911`，firstpass `29d8fa2c...2a8d87`；完整四程序集哈希见 `data/campaign-research/input-hashes.json`。

新证据统一位于 `data/campaign-research/`（下表简写 E/）。`E/metadata-il.txt` 为 PEReader 独立读取的精确 TypeDef/FieldDef/MethodDef、签名字节、RVA 及命名方法原始 IL_HEX；复现 `E/extract-metadata.ps1`，不加载目标程序集。可读方法体由已有 ILSpyCmd 9.1.0.7988 离线反编译。既有 `research/unity-object-layer/object-map-{MainUI,RootUI,FormationUI,BattleUI,Database.Character}.cs.txt` 下文简写 O/。

| 正式类型（CSharp，除特别注明） | TypeDef | 关键 MethodDef |
|---|---|---|
| MainUI | 020001AF | `_campaign()` 06001952，private instance |
| RootUI | 02000226 | `cs_campaign_enter(ushort,RootMessenger)` 06001EB6，响应 06001EBC |
| CampaignBaseUI | 020000BD | `wantCombat(byte)` 060005F3 |
| CampaignUI | 020000C9 | `BattleCampaign()` 06000689 / `(byte)` 0600068A；`BattleEnd(SCBattleEnd)` 06000685 |
| CampaignTroopUI | 020000C8 | `OnAttack()` 06000655，private bool |
| FormationUI | 02000150 | `OnClickSetCurrentForm()` 0600144D；`Web_OnGUI()` 06001448；`cs_mainchar_custom()` 06001496；响应 06001497 |
| BattleUI | 0200008E | `newBattleEnd` 06000399；`BattleEnd` 060003A1；`battle_exit()` 060003A4 |
| BattleGuideUI | 02000085 | `Step()` 06000322；OnAttack 06000328 |
| Database.Campaign / CampaignTroop（firstpass） | 02000068 / 0200006C | 属性转发到各自 data_，不是独立权威状态 |
| SCBattleEnd / SCCampaignBattle（pbdata） | 020005BA / 020005BB | 包字段与 ProtoMember 见各命名 cs.txt |

这些 token 是离线标识，不是地址或跨版本偏移。必须定位当前域已存在的正确场景实例，检查 Unity native object 有效、活动/可见/IsEnable，并在已证明的 Unity 主线程执行。不得 new UI、按名字盲调、任意反射、调用 Send/网络层或手工填写响应。

## 真实推进链与副作用

| 阶段 | 真实链 / 前提 / 异步完成 | 证据与副作用 |
|---|---|---|
| 进入征战 | MainUI 名为 `Campaign` 的 `_ButtonMain` checker 检查解锁且当前根不为 Campaign；handler `_campaign()` 清 first/guide 提示，disable 当前根，`RootUI.LoadLevel("Campaign",rmg)`。加载中不再次触发。 | O/MainUI `_campaign/_campaign_check`（约2760，按符号定位）；RootUI `cs_campaign_enter` 843–851 / `sc_campaign_enter` 989–1064。正常type=0响应更新character.idcampaign、campaigns，再设置 `/Campaign/CampaignUI`、当前RootMessenger。有网络1025和场景状态变化，无直接费用字段；服务端费用UNKNOWN。 |
| 当前关/敌军 | CampaignUI.m_campaign、CampaignID 与 m_troops 真实对象；只取 type=0、unlocked、未完成主线，troop.visible && canattack && !killed（重复finish_max时仍需其真实状态）；对应 mesh/arrow 和场景就绪。 | E/CampaignUI 399–452；E/CampaignBaseUI 1190–1208；E/Database.Campaign、CampaignTroop 属性。严禁用数字+1假定下一敌军。 |
| 点敌军 | `CampaignTroopScript` 正常鼠标/GUI→`CampaignBaseUI.wantCombat(byte)`；其只检查 !debug、IsEnable，调用方还有教学/visible/箭头等检查。若 !clicked、有scenarioClickF、可攻击，先剧情；`ScenarioClickEnd` 写clicked并发1030。末段有quest trigger时不会直接开敌军UI。 | E/CampaignTroopScript 约60–135；E/CampaignBaseUI 1264–1328。`wantCombat`可用于受强门禁保护的固定动作，不能当通用开战入口。 |
| 攻击→布阵 | wantTroopUI 在 click_mode=1且canattack&&!killed时 DirectAttack；其他显示 CampaignTroopUI。正常按钮判断等级chanllege_level、canattack、limit_count/VIP、是否重打，随后 BattleFormation 建 `NPCBattle/campaignid/troopid/substate/viewonly/OnAttack` 并由当前场景 FormationUI.SubLoading 加载。 | E/CampaignTroopUI 133–279、305–325、377–404。不要自行构造该Hashtable；等级不够会导向训练，其他模式可有刷新/次数/购买逻辑。 |
| 开始战斗 | FormationUI 绘制分支要求 !viewonly、IsEnable；按开始设置 onfight=true，OnClickSetCurrentForm→发298 CSFormationUpdate（cur_formation/formid/genid）。仅响应state=true且waitstate=2、onfight时走既有OnAttack委托。CampaignTroopUI.OnAttack再检查单敌CD、传记次数，随后父BattleCampaign。 | O/FormationUI 719–725、2828–2910、3134–3147；E/CampaignTroopUI 90–115。会保存当前布阵；不能用手工调用OnClickSetCurrentForm替代开始，因为它本身不设置onfight。 |
| 正式战斗请求 | CampaignUI.BattleCampaign先执行首次scenarioAttackF，再 BattleCampaignOK→cs_campaign_battle，发1026 CSCampaignBattle{campaign,troop}，显示加载、禁用。null响应恢复可用，不是输；result=1为测试人员不可打，不是输。 | E/CampaignUI 471–501、984–1018。`BattleCampaign(byte)`绕过上面的等级/次数/CD等门禁，排除直接allowlist。 |
| 结果/播放 | 响应有iswin、report和可能内嵌battle_end；setReport初始化，getBattleLevel映射左方胜6..10、平5、左方败0..4（征战isopponent=false）。BattleFighting是回放状态。结束时可能再发1032，或处理内嵌SCBattleEnd。 | E/CampaignUI 518–670；O/BattleUI 721–814、835–878。服务端返回前不得再发请求、count失败、推进。 |
| 权威结算 | `SCBattleEnd.result==0 && type==0` 才调用 CampaignUI.BattleEnd，iswin为胜负；胜利更改finish_count/killed、下游canattack/visible、奖励、剧情、可能章通关。result=1为accel_warning。 | O/BattleUI 969–1036；E/CampaignUI 672–907。**即使result=1，BattleUI仍写ReportID、清waiting和packet；所以ReportID!=0/!waiting不是成功结算证明。** 包未长期保留；无回调观测时只可将report等级标provisional并等战后UI/状态，不得宣称权威iswin。 |
| 返回/结算弹窗 | 正常BattleNormalResultUI“确定”Hide自己再battle_exit；后者恢复prevUI、重新Prepare/StartLoading/PostInit。CampaignBaseUI.BattleResult可选择战利品、BattleGuide、BattleFailed；BonusClose才恢复。 | E/BattleNormalResultUI 232–237；O/BattleUI 1049–1078；E/CampaignBaseUI 573–671。battle_exit本身不检查结算完成，禁止提前调用。不可通用Hide/Close跳过回调。 |
| 下一敌军/章 | 本关更新走当前troop.m_next_troops→setTroopAttacks设置可攻击/可见，可能剧情出生动画；Boss可能调用ChapterClear。正常主线章通关按钮发`cs_campaign_enter(0,rmg)`，让服务端选下一章；起始章会回Capital。 | E/CampaignBaseUI 1345–1418；E/CampaignUI 939–982；E/CampaignChapterClearUI 73–84。不要自行算next campaign ID；章/关切换和剧情未稳定时继续等待。 |

费用：包有 stamina_consume，但当前CampaignUI主线方法未见将其赋给m_iStaminaConsume；CampaignBaseUI.Show有本地减体力路径，不能据此声称主线免费或消费为零。服务端权威资源刷新和真实每场成本仍UNKNOWN。冷却清除、买体力/次数、训练、重置、GM NoBattleWin、任何培养/购买不在征战固定白名单。

## 已识别的“获得新战技”正常教学/提示

控制器观察到的 FormationUI 气泡可由 `checkallhint()`→`newhint_skill`→`DrawHotPanel()` 的 load_str(21250,技能名)解释（O/FormationUI 3881–3925、3667–3675）。它遍历**已拥有** `Global.character_.skills` 且在 `newskillid` 中的技能，并非阻止开始战斗的强制门禁；开始按钮分支没有检查此提示。具体“破阵一击”的技能ID仍需当前对象/模板读数，不能由中文名称猜ID。

当前控制器决定将它视为nonblocking hint，保留现有技能/兵种/阵型，正常开始战斗。下述只作为后续已审计候选，不加入当前自动动作。正常低风险链（仅主角已拥有、适用当前兵种的确切技能）：

1. 正常点主角战技图标；`OpenHotPanel("skill",249)`只选择主角并展开本地面板，等待hotpanel_timer<0（约0.2s）。从本帧 `hotrects["skill"+id]` / m_dictSkill 取确切条目；不得用硬编码屏幕坐标。
2. 只接受 `skills.ContainsKey(id)` 且 `_skill.limited_army==0 || ==当前兵种` 的条目。点选通过正常 Web_OnGUI 分支，保持armyid不变，设custom_skillselect，再发338 `CSCustomMainChar{armyid,skillid}`；立即 `MainCharCustom()`乐观更新本地主角技能/显示；removehint(3,id)再发342 `CSOldgen{type=3,newid=id}`。关闭这个面板是原点击分支的一部分，不是绕过教学。
3. 该链中无货币/体力/升级/学习/购买字段或客户端扣费调用；这是客户端无显式费用证据，服务端账务仍UNKNOWN。`sc_mainchar_custom` 中 `if(state)` 的方法体为空，无条件checkallhint/Enable；本地技能变化或重新Enable不能证明服务端接受。应观测响应state==true；仅正常UI操作时可报告“界面已选中”，不可声称服务端确认。

证据：O/FormationUI 480–541、3310–3371、3610–3675、3826–3877；O/Database.Character 3020–3056；E/命名CSCustomMainChar/SCCustomMainChar/CSOldgen文件。独立 IL：FormationUI Web_OnGUI=06001448 RVA BE910；cs_mainchar_custom=06001496 RVA C73D4；响应=06001497 RVA C7424；MainCharCustom=06001498 RVA C7464；OpenHotPanel=060014A0 RVA C7DA0；checkallhint=060014A3 RVA C8FBC；Character.removehint=060003B4 RVA33B04，均在E/metadata-il.txt含IL_HEX。

## 其他教学的可区分边界

* ScenarioUI 正常下一段/结束可发1040并触发button/guide/button_function、新增NewbieQuest。末段触发不是装饰文本。正规正常点击应保留；不能直接调用Close()/scenarioSkip()。（E/ScenarioUI 273–307、491–651。）
* `BattleGuideUI`来自失败且isNewbie==1；guide_mode 1布阵、2装备、3升星、4战技、5战报、6强化。战技模式可用上述已拥有技能正常链；其余培养操作费用/必要参数未审计，不得自动升级/装备购买/改兵种。`Step()`只是状态切换，不能冒充完成教学。（E/BattleGuideUI 41–183、185–360、386–436、477–491。）
* NewbieQuestUI有12类任务，含布阵/装备/强化/升级/征战/建造；finish发810、领奖811。已完成任务可通过正常UI领奖，但本报告未逐项审核奖励副作用；禁止用finishQuest/cs_newbie_quest_finish或手工isfinish伪造完成。（E/NewbieQuestUI 169–294、1478–1535。）

## 建议固定接口与运行守卫

### 稳定角色身份

`Global.character_`（firstpass Global TypeDef0200014F / FieldDef04000957）指向`Database.Character`（Type02000071），继承pbdata `Database._character`（Type0200002F）。正式角色ID为该基类 **public instance ulong id**，FieldDef **040001F6**，签名`06-0B`（UInt64）。不要使用LoginUI私有uint id、ACCID或显示名。桥应从当前角色对象和准确声明类字段取已初始化的u64值，输出十进制字符串防止JSON/JavaScript精度丢失，零/空拒绝。

赋值链：LoginUI.sc_playerinfo（CSharp Method06001830 / RVA10D9D4）反序列化SCPlayerInfo（pbdata Type02000437）的playerInfo（get_playerInfo06001486）后，new Character、IL0042替换Global.character_；IL0052调用继承的_character.Set（pbdata Method0600005B / RVA3A70），后者读取db_character.id（get_id0600306F，返回UInt64）并在IL0008写Field040001F6。db_character是pbdata Type02000723，其_id字段040020B6。静态全解码IL字段写扫描覆盖CSharp、firstpass、UnityScript-firstpass、pbdata、dbdata五DLL：Global根仅Global..cctor与LoginUI.sc_playerinfo赋值；_character.id仅Set写；只找到LoginUI.sc_playerinfo直接调用该Set。证据及部分原始IL：`E/character-identity-metadata.txt`，复现`E/extract-character-identity.ps1`退出0。扫描不排除未检查程序集或反射调用。

Character及基类没有`data_`字段，不能冻结虚构的Character.data_。已检查登录路径每次重新分配Character，未原地复用；同进程重登/换号会更换Global根。建议lease同时冻结(process creation、character_id、已存在character对象的受控GC handle/引用)，之后每次重新取Global根并检查一致，任何根变化撤销旧lease，即使重登同一角色。仅冻裸地址有GC/地址重用问题；仅冻结数值ID不能检测同角色重登。每次快照重新核验id还可覆盖对同一对象调用Set导致的原地身份变化；默认new Character的id=0不能作为登录完成。

### SCBattleEnd捕获：没有可独立订阅的回调

`E/game.net.tcp_base.cs.txt:63–80,218–231,285–300,326–402` 确定：m_handlers是`Dictionary<ushort,Handler>`单槽；handler_add直接覆盖，handler_rem移除该op整槽。Send<T>为响应同槽注册10秒处理器。Run先把messages[0]移出队列、删一次性handler，再消费stream，紧接Close。没有独立event/listener或持久settled结果。**不能add1032监听；会覆盖正式游戏回调，remove也无法单独撤销自己的listener。** 对私有handler做包装/替换不属于本契约。每帧轮询messages也不保证捕获，可能在同一帧的Run中完全消费。

已有可限时读取的完整权威包是`BattleUI.m_pktBattleEnd.pkt`（newBattleEnd从初始响应的battle_end存入），它的result/type/iswin与初始SCCampaignBattle.iswin是不同字段。仅捕获不等于已经应用。可以探索以下严格证据链：本次attempt已见ReportID=0；同一BattleUI父为本次CampaignUI、同campaign/troop、ViewType非report；自然播放时捕获内嵌包`result=0,type=0,idcampaign/idtroop匹配,nonzero idbattle_report`；此后无回放/换报/新战斗/投降，最终同实例pending变null、ReportID等于捕获id、!bUseBattleEnd且!bNeedWaitForBattleEnd、正常结果UI。静态上这组条件与自然运行BattleEnd一致，可以标为captured-authoritative-receipt及applied；需要控制器实测，不应先标PASS。

如果内嵌包为null、错过短生命周期或身份/结果不符，1032响应没有稳定副本；不能凭report等级、ReportID、waiting、战后可攻击状态拼出iswin。正常UI战报+状态双证据只作provisional，必须`pauseunknown`，不计失败、不推进。此缺口不通过更换handler解决。

固定snapshot仅包含：当前域/主线程/根UI和subUI类型、加载/可用状态、当前campaign/troop身份及上述可攻击字段、当次battle身份/预测结果/settlement状态、当前教学type/mode/quest、已拥有技能ID/当前兵种/当前activeskill/提示ID/本帧条目几何；不接受任意type/method/field名称。

固定action候选：`campaign.open`（正常Campaign按钮）、`campaign.select_existing_troop`（准确byte且强门禁）、`campaign.attack_open_formation`（正常按钮）、`formation.select_owned_tutorial_skill`（上述确切ID及限制）、`formation.start_battle`（正常按钮完整分支）、`battle.confirm_finished`、`campaign.confirm_result`、`campaign.next_chapter`（只走当前正式按钮）、`scenario.next_normal`（当前段明确、保留触发）。没有已验证正式handler的GUI分支可用正常UI输入或实现精确等价动作；本报告不授权省略检查拼接低层调用。

所有动作必须一进程creationTime+一attempt在途；场景/实例变化重取；请求超时、断线、null响应、未知结果、教学未完成、确认失败都进入等待/停止而非失败。用户最终指定连续失败计数：在当前自动运行内独立保存，每个已确认失败的attempt仅增加一次，确认胜利清零，达到3禁止下一场；`troop.data_.fail_count`是既有冷却相关计数，在战斗响应前置++、胜利--，不能替代此次失败计数。平局level=5客户端进入失败教学分支，但是否计入“失败三次”需控制器规则明确，不应猜。

## 验证和剩余观察

PASS：当前DLL哈希；26个以上命名静态反编译证据；PEReader metadata/IL提取退出0；关键方法token/RVA/IL和费用包字段复核。最初BattleResultUI与_campaign所在程序集假设失败，已改用firstpass/dbdata重取，最终无空证据文件。不存在可用git根，未创建提交。没有执行游戏、构建/CI/实机验收。

控制器仍需确认：当前实例domain/活动/线程；气泡对应技能ID与已拥有/适用兵种；338是否成功（客户端UI成功不够）；点击Start后的1026及1032结果锚定；普通首次/重复关的实际资源变化；结果UI顺序与章切换；真实剧情/教学当前段的可用按钮；draw策略。用户后续已明确选择连续失败3次停止，胜利清零；此处固定对象/包契约不受计数规则改变影响。本报告的DONE表示离线调查完成，以上live/业务判据均UNVERIFIED。
