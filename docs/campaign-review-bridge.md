# 征战 bridge 独立源码审查

Status: DONE_WITH_CONCERNS（首战结算 UNKNOWN 的独立因果切片完成，未签业务 PASS）。BR-1 至 BR-6 与读取重试的既有源码结论保留；首次自动战斗未捕获有效权威 receipt，安全进入 UNKNOWN/保留未决 guard。已确定缺口是“有效 receipt 未被观察器保存”，不能把 Observer.report_id=0 当作游戏原始 ReportID=0；更深原因须新增最小只读诊断后判断。本轮没有实现、放宽门禁、覆盖1032、改 ledger/guard 或操作游戏。

## 身份与授权范围

最新正规入口冻结 leaf source ID `7e98f6e69468db1c9d733f8facd1040a93ddf0f49ff3db70df591346f1e999b4`，build `483023e51f3edfd010f8231d28bd1fb515c905b1bd06161fa322eb0f868a7206`。CampaignModel SHA256 `ae809932a4b649975887fcb4f3595986dc0a3a079071b86b0bb3b3736e87cba8`，CampaignTests `f5afed27265f941bc6547eaa66d9d642afcf4c99968f9324f6bf9b3dad5ddf61`；bridge_client.py `5e585e9a40fdb2c9e20a1d0119a2a335cfb3a22dcfb50024beb2653f38b8161f`，Python tests `e49ed269d9a665947a15949fc75c83c9037d4da141e71afa2d9d304c3737e7d2`。payload `34baf975356f260a287a1cdba21a2bd8861bc7f345b8e656ecc46a7b899f4123`，DLL `ec3676164546ec54f12d4d629d911da264375dbfd565a5e8bc90d836c55c5a85`，host `8d15739fb14e0a2c2dfe0e5ac5a6247ccfa5499af5fe0a4ab5c21fd7603644dc`。26命名源 hashes、12 manifest source hashes 与 build/leaf 均独立重算；15个实现/夹具源原样复用。固定 marker/protocol3/native ABI2 不变，manifest SHA256 `49fcf093716fe324885c3100e455eb2d89882008f5f561e83550a3c20d01b51f`。

读取重试基准 leaf source ID `8d61d2f511c389636257bfa5acc4f9f1227cec3e5ff0830fb29e1e91a0ff1190`；当轮 bridge_client.py SHA256 `53ffde1237af3d61405bcbf020f2e9e9e880fee3901cc604bd35d915ad190bb6`，test_bridge_client.py `17240d79fa80a913430a2fb7fa3b7738f1e6ffb2ab63c210226e0ffbd1659c4b`。该轮25源哈希/原dc398三资产/manifest已查；本次 _read_json 按相同 AST 源片段口径精确复用，hash `b5fa1eccaaecafeb267a4e687d38c8c864e8e3b657ec615d0a1a5e3df3adbff4`。

已 clear 核心基准 leaf source ID `93e7a4f5e15ccbaf3fab3e2612e4fb6f89290a2f43ddb50fe3320e7c91849ca4`，build `dc398b6b41bd75e6f0558e53509ffab522f24806f2af0170e44fc42617119464`，固定能力 marker `scoped-campaign-gui-skip-v1`（protocol3/native ABI2）。其旧 Python hash 为 `792452af9b9403d928341fd97aaeb969b367767f27f86f9f2db3710668000cb2`。CampaignModel `323c5bba7305d207a5a7498a7894b8ffd02d76ba3ea8920973b1ca5416ba7b5b`，Probe `859828a39535c8681b2466a34322c75502175880e06bde54b5d18218d5dba781`。payload `3b5a05b1c5b334bd73e89db52bf7e6a1dec7498460536ff7a4429f34160f57ac`，DLL `169a6433bd202f4095913dda935552398afb1f97985ae5b192b64245fff73275`，host `3ad7df3b5bba84809f85d992c768e31ca4d7ff03e44de6f3de6dd4ed84a4313f`。各次最终结论依据父正式 freeze；moving 预查只用于提前反馈。

上一轮基础版本 leaf `a0c9308d7fdca9c4ec08416d3690df1758c2e05b1764e5b2bf3e14a37f651e7b`、native/managed build `731f07550dc1a2d2424d07994e7c30577a3afbd11b3ab011a6774630e9c4abc2` 关闭了 BR-1/2/3；当前复用其未变化的审查范围，并以最终逐文件 hashes 确认。

初审历史版本为 build `d6ed2c75d294ea35e13e0e7ef986f81ce5c268a5fb6485b85bf0945f676b33e7`；下文初始问题行号指向该版本，`731f0755…` 中间复审行号也明确标识，不应误作最终文件行号。

只读审查 `native/unity_bridge/managed/{CampaignModel,CampaignObserver,Protocol,Probe,Model,Lifecycle,ReceiptStore}.cs`、固定 native admission/manifest/build sealing 关联、`jiangshen_assistant/bridge_client.py`、`tests/test_bridge_client.py`、managed campaign fixtures 与作者的命名 metadata/validation artifacts。独立研究输入是 `docs/campaign-object-map.md`、`data/campaign-research/` 的命名静态代码/metadata，以及 `research/unity-object-layer/object-map-{FormationUI,BattleUI}.cs.txt`。Python domain/coordinator/GUI 由另一 reviewer 负责，本报告不重审其源码。

本 reviewer 只写此报告；没有修改生产源码、测试或配置，没有读取游戏进程、注入、操作游戏 UI、发业务网络请求或构建 native。反例使用测试已有的临时目录/假端点，假端点的 COMPLETED 只证明请求已经通过客户端准入并发布，不证明真实战斗发生。

## BR-4：真实只读 probe 的 CampaignUI 全局唯一性假设失败（CLOSED）

最终修复：`CampaignModel.cs:110–176` 先用独立固定读取的 LoadScreen.currmg、exact RootMessenger、cached rmg 与 transform.root 双重引用限定当前主线，再在此 context 内保持唯一性；同 root 两个候选、错 hierarchy/root、root 快照迁移仍 blocked。固定 `Root/RootUI` 与 `Battle/BattleUI` 来自原游戏 getter 常量；Battle 必须属于独立固定 Battle root，并保持 m_parentUI exact Campaign 与 m_prevUI==currmg 的链，允许正常 previous Campaign root inactive/无 active Main。旧全局断言被收窄，没有删除唯一性或放宽未知 overlay。诊断仅包含有界计数/对象身份/归属（Campaign 候选最多8），不按诊断名字选择第一个。127 个 production Campaign source+owned fixtures 已覆盖这些原反例；后续一次真实候选分布见读取冲突补审章节，不能外推所有场景。

独立读取三个命名会话 `data/object_sessions/8b09d950571e492ea713679146f223b9/`、`fd1ce8a08dd34a04a0c1ea0dac296928/`、`13dc4ed706bc4a2eab1409b8669c1c38/` 的 `probe.events.jsonl` 与 `state.json`：均是 build731f 的同一 PID2648+creation，先 `update_witness`，再 `update_ready`（2 distinct frames），紧接 `campaign_ui_not_unique_CampaignUI`，最后 own component destroyed；last_command_seq=0、action/business mutation_attempted=false。可确认这三次 bridge 曾被加载并进入真实 Update，但未执行征战命令。最终状态 main root_level_id=Campaign、gate=global_input_disabled、main_enabled=false；这些是并存 UI 状态，不是已证明的重复候选身份。

旧 `CampaignModel.Capture:39` 在读取当前 root 归属前先执行 `Unique(all, campaignType, false)`。静态 `research/unity-object-layer/object-map-RootUI.cs.txt:1023,1032,1043,1055` 分别在 `/Campaign`、`/Anecdote`、`/Fairy`、`/TalentCampaign` 获取同 exact CampaignUI，说明全局 exact type 本身不代表主线场景唯一性。上述会话未保存候选数量/instance/root，因此只能证明全局断言失败，不能声称 live 具体重复来自哪两个 root。

固定可验证归属证据：`BaseAsset.cs.txt:163,176` 将每个 UI 的 rmg 设置为其 transform.root 上的 RootMessenger；`LoadScreen.cs.txt:347–349` GetCurUI 直接返回 currmg；`RootUI.cs.txt:1023–1028` 主线从 `/Campaign/CampaignUI` 取组件，并把 `/Campaign` RootMessenger 设为 currmg。因此可先用 currmg 的对象引用、Campaign.rmg 引用和 transform.root 三者一致、准确 LevelID=Campaign 限定主线，再在此边界内保持 exact 唯一。不能仅删唯一性、选第一个、按名字猜、宽泛接受 enabled 或放松全局输入/overlay 门禁。同一边界内多个候选继续 UNKNOWN。

Battle 需要独立保留原有父链：`CampaignUI.cs.txt:547–548` setPrevUI(rmg) 后 setReport(...,this,...)；`BattleUI.cs.txt:678–683` 保存 m_prevUI，而 Show `:623–626` 把该旧 Campaign root 递归 inactive。因此不能以当前 active MainUI 或 Campaign.Visible 作为战斗中父 Campaign 的必需条件。应核对活 Battle 的固定来源/归属、m_parentUI exact Campaign、其 rmg 与 m_prevUI 对象一致及 transform.root 归属，并保持 attempt 原有冻结引用。静态无法排除的候选歧义须保留 UNKNOWN，必要时先收集有上限的只读 candidate identity/root diagnostics。

Battle 当前 root 迁移另已核对：`CampaignUI.cs.txt:666` 调 `RootUI.battle_enter(false)`；`RootUI.cs.txt:1402–1410` 只读 GetCurUI、SetBattling(true)、取 battleUI.get_rmg() 并对 Battle root LevelStart(false)，没有 SetCurUI。`RootMessenger.cs.txt:82–100,129–135` 的加载/Resume 链也不写 currmg，`LoadScreen.cs.txt:405–408` SetBattling 只写 bool。因此本正常主线链中，LoadScreen.currmg 仍是前一个 Campaign root，等于 Battle.m_prevUI；Battle 自身 rmg 则属于其独立 Battle transform.root。此双 root 关系不能混同：Campaign prev root 被递归 inactive 是正常行为，common.Main 不存在不等于 currmg 已切 Battle。

自然 normal result 的 Battle 可见性也已核对：`BattleUI.cs.txt:325–328,1111–1117` 只调用 resultFunc；setReport `:739` 把它绑定到同 GameObject 的 `BattleNormalResultUI.PrepareOpen`。后者 `BattleNormalResultUI.cs.txt:85–92,94–120` 只加载、InitData、显示结果 UI，不隐藏/禁用 BattleUI；只有正规确定按钮 `:232–234` 才 Hide 结果并 battle_exit，Battle `:1050–1051` 才对 Battle root 隐藏/禁用。因此已命名正常链在可显示结果、尚未确认时 BattleUI 仍可见。selector 应保留 disabled baseline 引用以免全局候选丢失；Observer 不应仅因一个隐藏结果对象就把非自然 hidden Battle 判 SETTLED，当前保守 UNKNOWN 仍成立。若 live 出现相反顺序，需新只读事件证据后再审。

本轮 reviewer 没有执行 game/process/UI/network 操作，只有父已提供命名 receipts 的文件读取及自有离线夹具；未修改 production/tests/assets。

同轮最初的 next gate 静态裁定（随后被用户 Skip 要求覆盖）：`CampaignBaseUI.cs.txt:1319–1321`、`:678–680` 等正规剧情入口先对 Campaign rmg.ToDisable 再 setScenario；`RootMessenger.cs.txt:46–49` 的 ToDisable 正是把 Global.extern_enabled 置 false。`ScenarioUI.cs.txt:116–118` 正常下一段 input 分支不检查 extern；`__BaseUI.cs.txt:1194–1196` IsEnable 也只返回该 UI 的本地 enable。因此 extern=false 本身不能否定正规剧情操作。最初只允许无 triggers 的 next 判据已由下段用户指令取代，不能再作为当前实现要求。

后续用户明确要求“有个跳过剧情，直接使用这个”，覆盖上一段关于只能 next/禁止 Skip 的父假设。后续评审以游戏自带正常 Skip 分支为准，不再以所有 section.triggers 为空作为 Skip 必需条件。`ScenarioUI.cs.txt:411–419` 在当前 section 存在且非 moving 时临时将 my_enabled 置 true，再通过 skipStyle.Button 调 `scenarioSkip(rect)`；该正常按钮不要求文字结束/section end。完整 `scenarioSkip:653–688` 自然遍历剩余段 triggerFunction，按 m_bTrigger 避免重复触发，保留解锁/guide；newUI 走正常展示动画，否则 FunctionLoad(null) 后按 system 分支发1040、或 Close 自然调用 OnClose/恢复 prevUI。不能以直接 Close、清 triggers/newUI、伪写 section 代替这个分支。后继未知教学仍须暂停，不能自动培养/购买/改配置。

OnGUI 风险：`scenarioSkip:672–675` 在 newUIList>0 且 !moving 时直接 GUI.DrawTexture，且此前已经执行 triggers/修改 section。桥不能假定任意 Update 调用均安全，不能传空 Rect/清 newUI 规避或把部分修改后的异常当成功。若覆盖此正常分支，需在真实 OnGUI 上下文内最多执行一次并重验 root/revision/ownership，或者在上下文不足时明确阻断。父手动正常点击 Skip 后看到奖励与“点击升级星灵”气泡，只是手动 UI 观察，不证明桥自动 Skip 已实测，也不授权点击后继培养。

BR-5 / P2（CLOSED）：首版 OnGUI 队列只检查 life.StopRequested，而 command.txt 的新 stop 只在下一 Update 的 PollStopOnly 消费。真实合法时序“continue 已 ACCEPTED/queued → Python 写 stop(seq2) → Unity 先 OnGUI 后下一 Update”会在 life 尚未更新时仍调用 Skip。最终 Probe.cs `:169–220` 在 OnGUI 入口及 ownership publication 前后读取 stop、验证 lease；先消费 command 再 fresh Capture，保存 queued root/Scenario/data/section/visible_time/Character 对象与数值 ID，并重验 revision；在真实 Event/GUI.enabled 上下文中最多调用一次完整 scenarioSkip。无回调1.5s超时为 REJECTED/invoked=false，ownership 写失败不调用游戏，stop/expiry 的 GuiAdmissionCancelled 明确未调用并保留较新 Stop ACK；已调用后异常为 UNKNOWN、不重试。149 个 production Probe+own callbacks 包含“写 stop 后直接 OnGUI、无 Update”的原反例和 capture/ownership 之间 stop，均通过。

新能力 marker 也已窄审：`bridge_client.py:503` 拒旧 manifest；`:580–581` 拒无正规 Skip marker 的当前 ready state；`:235–242` campaign parser 只接受 `scoped-campaign-gui-skip-v1`，避免把旧 next-only helper 当成新 Skip 能力。旧 clean receipts/未决业务继承专用路径继续按旧 protocol/identity 与完整 pending 链保存，不因缺新 marker 清除历史 guard。相应55个 Python 定向测试通过。

## 一次性通关规则补充（不改协议）

用户最终明确“征战是一次性的，通过之后就不需要重复进行”。本轮按游戏 `killed`/campaign `finished` 通关进度解释，不把每个 WIN 擅自等同通关。`CampaignModel.cs:228–243` 新观察只把 visible/canattack/!killed 的 troop 放入列表；`:407–411` 的 NormalTroop 还要求 type0、unlocked、campaign !finished、troop !killed 和等级。select/prepare/start 的实际执行点均调用这个实时谓词，未开放 rebattle/重置通关状态动作；新任务不能通过重置助手失败计数绕过已通关门禁。Eligible 本身不额外清空 finished campaign 的列表，因此列表单独不构成开战授权；执行点会拒绝 finished。

Reviewer 使用已经冻结的自有 x86 CampaignTests.exe 夹具与实际 production CampaignModel，先确认 context Available=true、troop 正常可用，再分别将 killed 或 finished 设 true，覆盖 select/prepare/start 六案：全部在各自业务 gate 拒绝，调用计数全0。另设 troop7.killed=true、troop8 未通关，直接 Eligible 只输出 troop8。未编辑生产/测试源码、未新建协议或构造刷关循环。

静态限制：`CampaignUI.cs.txt:748–757` 是 WIN 后 finish_count++，仅 finish_count>=finish_max 才 killed；当前主线模板 finish_max 是否全1未得到离线证明。因此本报告只保证已 killed/finished 不重打，不保证任意一次 WIN 都已经通关；仍未通关的 finish_max 分支可能需要多次 WIN，live 应读实际进度决定。此前尝试用x64 PowerShell反射加载x86自有夹具失败发生在测试执行前，另一次准备夹具的 panel 归属不正确被 overlay 拒绝，均未计为产品 PASS/FAIL；最终六案在明确 x86 Framework 宿主和正确归属、正向 baseline 下完成。

## 同路径读取冲突补审（CLOSED / CLEAR）

独立读取命名会话 `data/object_sessions/dec64aede75d492db5fc486460e7b1c0/` 及已封存 `data/acceptance/20261031-campaign/03-final-contention-confirmed-clean/`：dc398 helper 真实 update_ready，当前 CampaignUI total=4 / visible=1 / matching=1，current_root_level=Campaign，campaign57 的 eligible troop ID=3。该次真实候选归属已经验证；没有征战业务命令，action/business mutation_attempted=false。

关键 UTC 时间：Python first_unknown 在 `2026-10-31T01:48:19.847Z`，stage=ack_wait、snapshot seq3、PermissionError WinError32，路径为本 session 的 ack.json；native 同 seq3 的 ACK 于 `01:48:19.8683163Z` 为 COMPLETED。后续 `01:48:39.7628424Z` heartbeat_timeout 请求清理，`:39.7772569Z` own component destroyed。封存 capture 的 state/ACK 同为 seq3，stopped/destroyed/reconnect_allowed=true、lease_files=[]；Python ledger 后来确认 stopped。首次可证故障是读 ACK 的分享冲突，停止心跳/超时是后续链，不能反过来充当首因。现有证据不识别锁持有者或确切竞争机制。

最终源码 `_read_json` `bridge_client.py:126–175` 在一次调用内使用单调时间固定0.25s deadline、10ms步长；仅 Windows 的 open/read 阶段 WinError32/33 可重开同路径。失败的上下文先关闭再重读，开始下一 attempt 前检查 deadline；不重试 close、ACL5、非 Windows 错误、JSON/UTF8/type/size/schema。预算耗尽或后续非分享错误时保持首个异常对象、errno/winerror/filename，附 read_stage/read_attempts/read_elapsed_ms；`:650–664` 将这些字段保存 first_unknown，同时保留外层 ack_wait，后续诊断不覆盖首错。此函数不调用 command/sequence/lease/publication。

Reviewer 实跑分配的 `JsonReadTests` 十案及 BridgeTests 两个 ACK/no-resend 案：12/12 PASS（0.643s、exit0）。覆盖 open/read 32/33、先关闭失败句柄、单预算、close/其他错误不重试、真实自有临时文件 share=0 短持解除/长持、解析/大小/编码/类型对照，以及成功后 sequence=1/同 command 一次发布；耗尽后首错保留、二次请求在新 sequence/file publication 前拒绝、lease 保留。没有运行 full suite/native build 或连接游戏。

作者完整桥测试67/67 PASS（4.250s、skip0）为单独证据，本席未重复整套。自有 Win32 原始 probe 记录短持60ms后63ms/7次同路径 open 恢复，永久持有250ms/24次 open 后重抛同一个 PermissionError32（errno13、filename、stage=open 均保留）；文件字节不变，释放锁后可读。该实验只证明此类暂时/永久分享冲突的机制，不能据此识别本次 live 锁持有者或声称 live 已恢复。

独立只读9项身份核对全部 PASS：25源 hashes/leaf 重算、baseline预期三处变化、dc398全部 manifest sources/三资产/manifest 不变、原始 validation/probe 文件及 campaign 摘要 SHA。artifact 根 `data/acceptance/20261031-campaign/04-python-read-contention/`：`read-contention-validation.json` SHA256 `198428c77053e015a0a48f4b3c41784cc6a7d1ac6f9472b045270b4f4b914a04`，`win32-read-probes.json` `86575630777eb81fae9feb3888edfecfc0e13ccadc5ee9bb26a0c5d92db24e75`，`targeted-bridge-tests.log`、`baseline-campaign-validation.json`、`verify_read_contention.py`。最新 `native/unity_bridge/build/campaign-validation.json` SHA256 `a26d9adeb6cbfa0a3a6e4736839a623b990802d3172a783b96ad9b53cac7e38a`。

## BR-6：正规 mesh 选择与无限次数被多余门禁拒绝（CLOSED）

最终修复已与父明确授权范围核对。`CampaignModel.cs:456–479` SelectionGate 允许0/2/3，明确拒1/未知；不再用 show_arrow 作 mesh 路径门禁，仍要求当前 root 下 active mesh、GetComponents 的唯一 exact CampaignTroopScript、同 mesh GameObject、ParentUI==campaign/Troop==target 及 Script.Enabled/IsEnable；不依赖 script cached rmg。NormalTroopGate `:415–429` 及全局/context/动画/unknown overlay 守卫保留，拒绝原因拆成固定 allowlist。

prepare `:302–310` 只允许 limit_count==-1 或 >0，0及其他负数拒绝，不走 VIP/购买逻辑。父同轮明确准许的 next_chapter `:344–353` 去掉 m_bNextChapter 展示标记门；静态 `CampaignChapterClearUI.cs.txt:66–83` 证明它仅控制文字/布局，正规按钮只分 !m_bAnecdote。PhaseRESULT、SETTLED、root 归属/local Enabled 与正常 cs_campaign_enter(0) body 均保留，未增动作。

`SelectionDiagnostics:485–525` 为固定字段只读快照，最多8个 visible/canattack/!killed troop，含角色当前 idcampaign、short limit_count、finish_count/max、固定教学/六种 scenario ID、mesh/script 归属和固定 select_gate，额外只给 truncated 标记。Python `_selection_diagnostics_valid:344–387` 严格可选 schema：字段精确、拒未知键/重复ID/第9行、bool与int分离，signed short 与uint32边界覆盖，diag不参与动作授权；生产夹具输出直接进入真实 parser 通过。

父提供首业务会话 `data/object_sessions/0b085de0c9a9459aa89857c19b00b385/`：campaign57 / eligible troop3 / R1 / available=PASS，select seq3 被 REJECTED、invoked=false、reason=normal_troop_selection_gate。后续独立读取仍保存该 command receipt；最终 ledger/state 是 stop、seq3064、first_unknown=null、business mutation_attempted=false/pending_attempt=false、attempt NONE。任务暂停0 loss/pendingNone由父业务记录确认。本席没有操作游戏或重试攻击。此会话说明新 reader 运行稳定、没有本次首错，但未记录成功读取前是否发生过短暂分享重试，不能外推精确 live 锁恢复。

修复后新真实只读证据已独立核对：session `70b352d5c42742609d046cae988fcd98` 的封存 `data/acceptance/20261031-campaign/07-selection-diagnostics-connected/state.json` SHA256 `b192046f2965864eafb619bde51ad1c268d4f6f8f6e6d6ac1ac725ef6ce63298`，483 helper ready、seq85、R1、真实 parser 接受、business mutation=false。角色当前 idcampaign=57 与当前 Campaign57 一致，type0/enabled/unlocked/!finished、六个动画/debug标记全false；唯一 eligible troop3 的 click_mode=2、limit_count=-1、finish_count/max=0/1、show_arrow=true，mesh/当前root/唯一exactScript/localEnabled/owner 全PASS。该当前对象会被旧 mode==3 以及旧 prepare count>0 明确拒绝，且不属于误看旧章节。历史第一次 select 未捕获完整原值，不能把这份后续快照当作当时所有字段的逐值回放；show_arrow 在新快照为true，不把它猜作该次唯一拒因。isNewbie1 / Newbie_Skill2188 / formation1 / system scenario647（其他五类scenario ID0）只是固定诊断，不授权自动培养或改配置；尚无本次 battle/结算证据。

独立源证据：`CampaignTroopScript.cs.txt:45–91` 的 show_arrow 是气泡绘制/气泡按钮前提；真正 mesh MyMouseDown `:180–185` 只检查该 Script.IsEnable 后调用 ParentUI.wantCombat。`CampaignBaseUI.cs.txt:1299–1308` 仅当 canattack && !killed && click_mode==1 才 DirectAttack，其余走正规 CampaignTroopUI.MyLoading；因此旧 select 强制 mode3 且 show_arrow=true 并非正常 mesh 路径必要条件。父明确限定允许已知 nonDirect0/2/3、拒1和未知值；其余 NormalTroop、global、owner/mesh、动画及未知 overlay 门禁保留。旧粗粒度拒因不能凭这份 receipt 唯一定位具体是 mode、arrow 或其他 conjunct，新增有界诊断应保留实际字段再做 live 判断。

正规 prepare 的 `CampaignTroopUI.cs.txt:222` 是 canattack && limit_count!=0（另含本轮排除的 VIP 分支）；`Database.CampaignTroop.cs.txt:19,175–182` 明确 limit_count 是 short，make 的正常默认值为 -1。父授权只接受 -1 或正值、拒0和其他负值，不进入买次数/VIP 等分支；旧 >0 检查会错误拒绝正常 unlimited。

Mesh 路径仍应验证 exact CampaignTroopScript 与 target mesh/ParentUI/Troop 引用及 Script.IsEnable。`CampaignBaseUI.createATroop:326,332–333` 先 AddComponent，再 setTroop(this,troop)，最后才挂到 Campaign mesh；`CampaignTroopScript.Start:19–22` 不调用 base.Start，因此不应照搬 UI 的 cached rmg OwnedBy 去误拒 mesh script。归属可用 same mesh GameObject/当前 root hierarchy/ParentUI==campaign/Troop==target 固定链。新 selection diagnostics 只能包含固定字段/教学ID、当前role.idcampaign及 bounded troop进度/次数，不能接受任意字段、改技能/阵型或重置已通关状态；short次数诊断应能表示未知负值而由动作拒绝，避免把可诊断业务拒绝变成 transport UNKNOWN。

Reviewer 实跑已冻结 production Campaign source+owned fixtures：160 checks PASS，包含0/2/3且arrow=false、direct/unknown拒、mesh missing/inactive/wrongroot/多脚本/错Parent/Troop/localdisabled、动画、killed/finished拒、-1/>0允许与0/其他负拒，以及 next 展示flag=false仍正规body、unsettled/Anecdote/localdisabled/wrongroot拒。实跑3新增 Python parser 测试 PASS（0.090s）；另将本次实际 CampaignTests.exe 生成的8行/truncated诊断直接管道送真实 parser，结果 PASS，无文件/业务写入。

最终只读 identity：26源/15原样复用/12 manifest sources、build与leaf重算、三资产/x86 PE/seals、168 file-backed metadata checks、exact assembly references 全部 PASS。最初草案直接 GetComponents(Type) 引入额外 desktop mscorlib 已被 packaging gate 拒绝；最终按固定方法名、System.Type FullName、Component[] 返回类型唯一匹配再反射调用，保持 legacy 两 refs gate，未放宽验证。该已修草案证据保存在 `first-packaging-reference-failure.json`，不是最终候选失败。初次用 inspect.getsource（含末换行）比较 reader hash 与作者 AST 片段口径不一致；按声明的 AST 源片段重算精确一致，没有实际 reader 漂移。

本轮 artifacts 位于 `data/acceptance/20261031-campaign/06-normal-selection-fix/`：`selection-validation.json` SHA256 `dcbead61f0cdda6975ac639071bcfa93792da325497f217180754655fa000e54`，`synthetic-selection-diagnostics.json`、`python-bridge-tests.log`（作者70/70 PASS4.328s，本席未重复全桥）、`campaign-fixture.log`、`verify_selection_candidate.py`。`native/unity_bridge/build/campaign-validation.json` SHA256 `15895bc5adb08603f61f740624186c8b2e9e5d8e39c958f11f56efad798390d2`。未跑 full/native build/游戏；Probe/OnGUI/reader/nativeC等15原样源沿用其已有矩阵与精确 hash 证据。

## 历史阻断项（BR-1/2/3 均已关闭）

### BR-1 / P1：停止时将原角色的未决战斗归给当前角色，回切后能再开战

最终状态：CLOSED。Observer.Json `:104` / BusinessJson `:111` 序列化 Begin 冻结角色；Python `_business_origin` `:157–170` 从 attempt 取原角色，缺失或 business 顶层冲突返回未知。最终 `_finish_stopped` `:682–699` 与 `_prior_clean` `:389–418` 不以当前 campaign ID 归属，并合并各来源。原 A→B→stop→A 路径和同 ID 更换角色对象回归覆盖。任何原角色不明的 pending 也保留并阻止整个 PID+creation 的业务。

准确证据链：

- `CampaignObserver.cs:48` 在 Begin 冻结原角色对象和 `characterId`；`:64` 在角色对象/ID 变化时进入 UNKNOWN。
- `CampaignObserver.cs:102–112` 的 attempt/business JSON 没有保存该冻结角色 ID。与此同时 `CampaignModel.cs:45` 每帧取当前角色，并在 `:91` 输出当前 `campaign.character_id`。
- `bridge_client.py:604` 的 `_finish_stopped` 用当前 `state.campaign.character_id` 写入未决 guard；`:317–319` 继承旧会话时也从当前 campaign 取 ID。
- `bridge_client.py:711–724` 仅在 guard 角色与当前角色相同（或缺失）时阻止新业务，因此错误角色归属使原角色逃过检查。

最小反例：角色 A=`18446744073709551615` 开始 attempt a → 在未结算时切角色 B=`777`，observer UNKNOWN → clean stop → guard 中 `character_id=777` → 同一 PID+creation 重连 A → `campaign_command("start", expected_revision=3, campaign_id=12, troop_id=7, attempt_id="b"*32)` 发布新命令。

独立纯数据复现输出：`original_attempt_character=A / persisted_pending_character=777 / reconnected_character=A / new_start_published=true`。原始 A 归属仅存在已销毁 observer 的私有字段，持久化数据无法恢复。现有换角色 observer 单测和 reconnect 单测分别通过，未覆盖这条组合路径。

最小修复要求：在 attempt/business 回执中携带 Begin 时冻结的原角色 ID，停止/继承时始终按此归属保留 pending；角色变化后不以当前角色重写原 attempt 所有者。缺失或冲突归属保持 blocked。补 A→B→stop→A 回归，以及同角色重新登录替换对象时的未决保留。

### BR-2 / P2：公开 generic request 可绕过 typed 方法的未决准入

最终状态：CLOSED。公开 request `:705–711` 无条件拒绝 campaign-command；typed API 经 private `_request` `:715–722` 在请求锁内固定 schema copy 与 prior gate，实际 `_send` `:640–649` 再校验，均早于 sequence/ledger/file publication。原公开绕过和 private request/send 路径都在持有 guard 时拒绝，sequence/command bytes 不变，且不使 transport UNKNOWN。

`bridge_client.py:616–627` 的公开 `request` 仅拒绝 `_campaign_fields is None` 的 campaign-command；只要提供非 None 字典，便直接 `_send`。角色未决准入与字段验证只在 `campaign_command`（`:711–736`）。

最小反例：同一 client 已知当前角色 A，同时 `_prior_business.character_id=A`；typed `campaign_command("start", ...)` 正确抛 BridgeError。但调用公开 `request("campaign-command", _campaign_fields={"action":"start", "expected_revision":3, "campaign_id":12, "troop_id":7, "attempt_id":"b"*32})` 发布 start，假端点返回 COMPLETED。复现输出：`typed_interface_blocked=true / new_start_published=true`。

`_campaign_fields` 的下划线只表达调用约定，不能使公开 request 获得与 typed 方法一致的安全语义。未决 guard 是新连接中仅 Python 持有的状态，managed 新 observer 为 NONE，正常合法 wire 不能替 Python 补上此门禁。

最小修复要求：公开 generic request 拒绝 campaign-command；typed API 走私有执行器，并在串行化请求边界统一验证字段及未决归属。新增绕过反例回归，确保失败发生在 sequence/ledger/command publication 前。

### BR-3 / P1：已有旧 guard 使崩溃恢复跳过较新 lease 的未决战斗

最终状态：CLOSED。最终 `_check_campaign_admission` `bridge_client.py:815–817` 在同 PID+creation 任何 prior pending 存在时阻止全部角色/全部 7 种征战业务动作，不再因换角色放行。`_merge_pending_business` `:173–212` 按 session+完整 business 值合并、深拷贝独立记录；重复事实去重，冲突事实两者保留，超界/无效来源阻断且保留原文件。`_prior_clean` `:398–417` 合并现有 guard、ledger、state 和 inherited 链，即使 ledger 已足以证明物理清理也继续读取存在的 state 业务；`_finish_stopped` `:687–695` 在释放 lease 前把恢复来的链重新持久化。原崩溃反例已回归，readonly stop/reconnect 后 A/B 两来源仍保留。

这是 `731f0755…` 复审新确认的 BR-1 继承组合缺口。`_acquire_lease` `bridge_client.py:389–396` 先载入旧 business guard；随后回收旧 lease 时，`_prior_clean.remember` `:357–358` 因 `_prior_business is not None` 直接返回，不读取/合并该 lease 的较新 business pending。

最小反例：

1. 同 PID+creation 已有角色 B=`777` 的 pending guard。
2. 连接角色 A=`18446744073709551615`，不同角色准入允许；A 开始新 attempt。
3. Python owner 崩溃，helper lease 到期后 clean stop，managed state/旧 ledger 保存 A pending；Python 未执行 `_finish_stopped`，因此旧 guard 仍为 B，lease 文件仍指 A 会话。
4. 重连 A：先载 B guard，回收 A lease 时 remember 直接跳过 A pending；检查只剩 B，因此 typed start 被发布。

独立纯数据 fixture 输出：`old_guard_character=777 / newer_crashed_attempt_character=18446744073709551615 / new_start_published=true / fake_ack=COMPLETED`。该复现沿用已有 ledger-recovery fake 流程，仅增加先前另一角色 guard。此路径不要求篡改文件或调用私有发送入口；测试直接写终态仅替代无游戏的 helper crash/lease expiry。

最小修复要求：同时保留磁盘 guard 链及待回收 lease 的 ledger/state pending；现有 guard 不得优先吞掉较新 attempt。多来源不一致保持阻断或保留两者。补 B-old-guard→A-pending→owner crash/helper clean stop→A reconnect 回归，并继续验证 readonly stop/reconnect 不丢链。

## 首次自动战斗结算 UNKNOWN：只读独立因果切片

范围：build483 session `70b352d5c42742609d046cae988fcd98`，run `94aa75b29f264e66b799b26947bbaaad`，attempt `b8dee45dad214598b067c01500a15929`，target57:3，start command186。只读证据为 `data/acceptance/20261031-campaign/08-first-auto-settlement-unknown/`（state/ACK/session/events/run）和 `gui-observation-20261031T022241Z.jsonl`；sampler共644行，仅提取本 attempt 的5次状态变化，没有整文件外发。封存 state SHA256 `f84418a7772e407fa50c60d0ed18630939e58ff188052efd0803265177d9936b`。

可证事实（UTC）：native 于02:27:14.3438533、frame595342接受command186；采样14.349为SUBMITTED，14.823为BATTLE/WAITING，19.568仍WAITING但eligible已由3变5，21.113为RESULT/NORMAL/UNKNOWN。WAITING证据始终 `start_recorded_before_invocation`，attempt.report_id始终"0"；终态原因 `settlement_receipt_missing_or_not_applied`。stop225于21.1139077，own component于21.1159042销毁，未点结果确认、未重打由父操作记录确认。run保持 blocked、pending.uncertain=true、losses=0、consumed=[]；business mutation_attempted=true、pending_attempt=true、resource_rollback=false，物理清理不等于业务结算。父看见“小卒二完胜”属于画面证据，不是本报告的权威结算证据。

决定性语义：`CampaignObserver.Json:109` 输出的是保存的 receipt.ReportId，并非 frame.ReportId/BattleUI.ReportID。receipt只在`:86–93`有效包检查后保存，ReportId必非0；Unknown不清receipt，且同attempt未重新Begin。因此此次可确定没有保存过有效内嵌 receipt，不能断言原始Battle.ReportID为0，也不能由 WAITING 推断 zeroObserved=true（原Battle baseline已绑定时，WAITING可在未见零锚下进入）。同理目前缺少 raw result ID、waiting/useEnd/fighting、零锚、packet存在与否等字段，复合 reason 不能唯一定位更深原因。

正常路由的静态限制：`CampaignUI.cs.txt:502–503,548` 对未killed的type0普通敌军以 flag2=true 调setReport(use_battle_end=true)；`BattleUI.cs.txt:844–858` 此时自然走1032请求，只有 else/!useEnd才应用 m_pktBattleEnd。newBattleEnd `:865–874` 仅保存可选初始包，null是合法输入。另chapter57在VIP>=5的`:541–548`可令wait_for_end=false，而useEnd仍true；`BattleUI:325`用(!needWait || !useEnd)开结果窗口，故正常结果UI可能早于晚到结算。该次VIP/原始flags未采集，不能猜具体走了哪个时间分支。

| 竞争解释 | 当前权重与缺口 |
|---|---|
| 普通主线没有内嵌包、实际走晚到1032 | 与命名源码最吻合，也解释零receipt；实际初始packet是否null未记录，尚非已证根因。 |
| 内嵌包短生命周期在两次采样间消费 | 不能完全排除；但useEnd=true的正常路由会保留内嵌字段到晚包处理，且已有约4.7秒WAITING窗口，不能只凭“同帧”笼统归因。 |
| normal result提前出现，1032尚未成功应用 | 有VIP章节路径支持；raw waiting/useEnd/ReportID缺失，未证该次发生。 |
| 1032已应用，但观察器只轮询内嵌字段 | eligible3→5与此一致；本地进度不是单writer，仍需实际权威包/原始应用锚证明。 |
| 观察到错角色/错目标/回放/非法内嵌包 | 现终态不是这些专门reason；WAITING通过了相应已采样守卫，但不能排除未被观察到的晚包错误/零reportID。 |

1032约束：`tcp_base.handler_add:218–223`是单槽覆盖；`RootUI.Update:702–706`调用Run，Run `:366–382`同次执行run_data、移出messages[0]、移除一次性handler、调用正式handler并Close stream。不得新增/包装/替换1032监听。即使轮询现有队列，正常单消息也可能在一次Run内完全出现并消失，因此不能将队列miss当作“没有权威包”，不能承诺稳定捕获。

最小只读诊断建议：每attempt保固定计数及最多8至16个边界快照（首visible/首零锚/packet出现或消失/resultVisible/首UNKNOWN），首UNKNOWN冻结不被后续覆盖。必要字段是 raw Battle.ReportID、normalResult.m_reportid、PacketPresent及其 result/type/iswin/campaign/troop/reportID、waiting/useEnd/fighting、zeroObserved/battleObserved/receiptSeen、首次包/首次未知frame；另记录角色/父/目标/Battle/report/结果引用匹配布尔与reportToken是否改变。receipt依赖的未执行检查用unknown/not_evaluated，不伪填false。字段固定、数量有界，只复制值，不改游戏对象/slot/流位置，不输出整包奖励或自由反射名。

权威替代证据的必要审计：进度不是唯一writer。`CampaignUI.BattleEnd:748–757`可在赢后写finish_count/killed；`Character.addCampaign:584–605`也会对既有troop原位make，后者调用 `_character_campaign_troop.Set` 直接覆盖killed/finish_count/canattack；Campaign.make `:157–159`可覆盖finished，另有debug/noBattle构造路径。因此若以后考虑进度替代，需冻结troop/data对象epoch、盘点完整writer/调用路径、排除refresh/debug/其他战斗/同目标旧报告，且单独解决LOSS证据；当前未具备，不能凭eligible/画面补WIN。

如果后续另获授权，仅将现有1032队列作正向权威捕获候选：须零等待取得现有monitor、对有上限的现有MemoryStream只读副本（不动Position/不移除message/不改handler），验证现slot的exact方法/target为当次Battle，再校验实际1032 result0/type0/IDs/nonzero reportID与同attempt零锚/角色/父/report对象，并见自然应用。还需源审解码边界、消息与handler竞态、旧响应/错target/重复/回放/零ID/超长/缺包等负例；miss继续UNKNOWN。此方案不保证可观测窗口，本轮不实施或宣称可用。

现有“内嵌包已应用”谓词还有必须补的路由负例：观察器允许useEnd=true时捕获初始good embedded，但该game分支实际应用晚到1032；若晚包result1且同reportID，`BattleUI.BattleEnd:1020–1024`仍写ID并清flags/pending，结果UI仍可出现，初始embedded却未被应用。不能仅补后态ID/flags便放宽成功；embedded-only证据应证明实际选择了!useEnd的内嵌应用分支，晚1032则需要实际权威包证据。该反例是源码可构造的协议边界，不是声称本次出现了result1；本次没有捕获receipt，正确停在UNKNOWN。

结论：首轮因果调查完成，事实支持“权威receipt未被捕获”，不支持唯一指定无包、同帧、零rawReportID或早结果为根因。下一步是有界只读诊断/权威路由审计，不能重放本attempt、确认结果冒充结算、重置guard或修改已封存run。

## 既有已核对范围（不构成新的业务结算 PASS）

| 判据 | 结果与证据 |
|---|---|
| 固定 public wire | managed `Command.Parse` 严格 version=3、nonce、单调 sequence、固定字段数量/顺序/范围、固定 7 动作，未开放反射名称或裸 network。Python typed 边界也校验 bool/int、u64 字符串和 attempt nonce；generic request 拒业务，锁内与发送前统一 schema/准入。 |
| 正常选择/布阵 | 最终 select 允许0/2/3普通mesh路径，拒direct1/未知，精确mesh/root/唯一脚本/Parent/Troop/IsEnable均核验；prepare 检查未完成 type0、等级、visible/canattack/!killed、-1或正次数及当前父/敌军引用。start 保留NPCBattle/campaign、当前Formation、唯一OnAttack目标/父/敌军、onfight/waitstate/viewonly/loading，先发布归属再走 onfight=true+OnClickSetCurrentForm。无直接BattleCampaign/培养/补资源/购买路径。 |
| 权威结果 | Observer `:62–99` 对当前 attempt 的角色/父/敌军/Battle 对象、自然 ReportID=0、固定 report 对象、非回放/投降和播放剩余时间检查；必须捕获 result=0/type=0、匹配目标且 reportid 非0 的内嵌 SCBattleEnd，并在同 Battle/normal result 上见 pending 清空、reportid 匹配、!waiting/!useEnd/!fighting 才 SETTLED。缺失/冲突进入 UNKNOWN，不以初始 iswin/报告等级计输。 |
| 游戏回调所有权 | 未注册/覆盖/移除 1032 handler；静态 tcp_base 单槽证据与实现相符。packet getter 的固定 token `060023D7` 原始 IL `02-7B-34-21-00-04-2A` 是直接字段返回。 |
| 教学/剧情 | newskill 仅 HINT；无改配置动作。用户要求后，continue 在精确当前 Scenario 的真实 OnGUI 中执行完整正规 scenarioSkip，保留 triggers/newUI/1040/OnClose；不伪写进度，不要求文字结束或 triggers 为空；后继未知教学/overlay blocked，不自动购买/培养。global=false 的例外只覆盖该操作，其他动作仍拒。 |
| 线程/并发 | Probe 仅在 bootstrap 验证的 native/managed 线程、至少两不同 Update 帧后读取对象；business attempt 与旧 disable-restore ActionPending 分离，heartbeat/stop 可继续，pending 时拒 disable-restore；Begin 与 pending 防同会话重复 start。 |
| 清理含义 | stopped/组件销毁与 business.pending 独立序列化，resource_rollback=false；旧 `_is_clean` 不把业务结算伪装成恢复。原角色归属/多来源未决事实跨停止与重连保留，任意 prior pending 阻断整个进程的新业务。 |
| 固定身份/打包 | character id 固定 pbdata Database._character UInt64 字段/token040001F6，十进制字符串；manifest protocol3/native ABI2、正规Skip marker、12 manifest/26最新source hashes、3 asset hashes/3份x86 PE头、build/leaf identity、168 metadata/exactrefs、payload/assembly seals 独立只读核对全部 PASS。 |

## 验证与限制

Reviewer 实际运行 `python -B -m unittest tests.test_bridge_client -q`：41/41 PASS，2.486s，exit 0。另运行上述两条自有纯数据最小反例，均确认缺口；只读 bytes/hash 检查 8 项全部 PASS，未加载 DLL 或调用 game。未写入作者验证 artifacts。

`731f0755…` 复审实际重跑同命令：48/48 PASS，3.108s，exit 0；另独立执行 BR-3 组合反例，确认新缺口。原直接修复、新字段/parser/cleanstop/locked preflight 源码已窄复核；本轮不重复无变化范围/native build。

BR-1/2/3 上一轮 leaf `a0c9308…` reviewer 实际重跑同命令：51/51 PASS，4.683s，exit 0。另独立重放 BR-3 原反例并检查所有 7 个 business 动作、public request/private request/send 三入口：全部拒绝，sequence/command bytes 不变；合并来源是 A=`18446744073709551615` 与 B=`777`，readonly stop/第三次 reconnect 后两者仍在；campaign-observe、heartbeat、stop 正常，transport_unknown=false。当轮只读核对 manifest 12 sources、validation 13 sources、3 assets、build identity、native payload/assembly seal、managed payload metadata 和 114 campaign metadata checks 全部 PASS，client/tests hashes 精确等于该父 freeze。

本轮最终 leaf `93e7a4f5…` reviewer 实际运行：`python -B -m unittest tests.test_bridge_client -q`，55/55 PASS（3.549s、exit0）；运行已冻结自有 `CampaignTests.exe`，127 checks PASS；运行已冻结自有 `LifecycleTests.exe`、使用独立 reviewer 临时目录，149 checks PASS。两 managed harness 均只包含 production 源码+自有 synthetic Unity/model，不加载游戏程序集、不连接游戏。另完成上述六个 killed/finished 执行点和 Eligible 独立反例。未执行 native build、full suite/CI 或真实游戏操作；没改写作者 validation 汇总。

本轮只读 identity 检查15项全部 PASS：24 validation sources、12 manifest sources、3 packaged assets、build identity、protocol/marker、payload/assembly seals、3份x86 PE头、133 campaign metadata checks、managed payload metadata hash，以及三个历史会话 state/events 的精确 SHA256。原始证据 `native/unity_bridge/build/root-context-skip-evidence.json` 保存3会话哈希及8个命名方法 raw IL，静态输入身份与已 pin 游戏程序集一致；历史失败只证明候选至少2个，真实分布未被伪称已测。

最终 artifacts：`native/unity_bridge/build/campaign-validation.json`、`root-context-skip-evidence.json`、`managed-campaign.json`（127）、`managed-lifecycle.json`（149）、`receipt-sharing.json`（814，作者结果，本轮未重跑）、`campaign-metadata.json`（133，file-backed）、`managed-metadata.json`、`native-synthetic.jsonl`、`native-paths.json`、`asset-verification.json`。本轮复用未变化 native/ReceiptStore/Protocol/Lifecycle 的先前 clear 范围；记录 author/reviewer 实际运行层次，未将 metadata/fakes 当 live。

真实只读连接与当前 Campaign root 匹配已有 dec64 会话证据；新读取重试的 live 恢复、OnGUI Skip、自然内嵌权威包完整链、真实教学/结果 UI 顺序、资源消费、连续3次 confirmed LOSS 后停止仍 UNVERIFIED，归父控制器验收。本 reviewer 没有安排 full suite/native build/CI，未把 fakes、metadata、health 或组件销毁当成 live 业务成功。

## Changed files / unfinished work

Changed files：本席仅 `docs/campaign-review-bridge.md`，未改生产/接口。被审实现新增可选只读 selection_diagnostics；固定 wire/action allowlist、protocol3/native ABI2及GUI marker 保持。

本席本轮只读因果调查未完成工作：无。未实施新诊断或权威替代方案；若需实现，应另给具体ownership/冻结，再独立复验。当前attempt和业务guard保持UNKNOWN/未决；父仍需取得权威结算证据，不能把既有source clear、画面胜利或物理清理当新业务PASS。

## 只读结算诊断冻结窄审（CLEAR，真实结算仍 UNVERIFIED）

本轮独立审查仅覆盖 `09-settlement-readonly-diagnostics/` 新增诊断包。冻结 build 为 `bb4ef77364d9de6ed84baa2d2aa4247ad4498b15b58c3dd4a452c20075a257db`，payload SHA256 `143c5c13a47eaf3189dd6527b1e7d04bd0c7ecdb1ba0940a8e4aa7de5035279e`；`freeze.json` SHA256 `957d880e35de61023ee0b738af6911d4e173fe45d73657bea19fee514b5018b5`。该目录的 `freeze.json`、`runtime-manifest.json` 和 `REPORT.md` 是本候选身份；先前 `build/campaign-validation.json` 中的 build483 属于历史验证，不是此新候选的汇总。

结论：新诊断源码 CLEAR，无新增 blocker。`CampaignModel.Json` 只追加可选 `settlement_diagnostics`，不进入 `ContentJson`/Revision 或动作准入。固定分组分别记录当前 bound Battle/normal result 的独立 raw ReportID、实例和父/root绑定、flags/view/replay/cross/surrender/playback、pending wrapper 与固定 packet 字段，以及 Battle 原目标的进度。原目标通过当前 Battle 的 `m_parentUI.m_troops[idtroop]` 查找，不经 eligible 过滤；missing target 为 null，匹配布尔单独保留。没有自由反射名、消息遍历、handler/stream操作、setter、奖励整包或无界集合。

`CampaignObserver.DebugJson` 仅比较本实例已有的冻结引用。新 Observer NONE 返回 anchors_available=false，zero/battle/receipt 和全部引用比较均为 null；target.data_anchor_match 始终 null，明确原 Begin 从未冻结该 data_。这些值不能补回旧 helper 的零锚、report token、对象 epoch 或原 attempt。诊断没有 Begin/Observe/Unknown 调用，不改 attempt/outcome/business guard，不把当前进度或 raw packet 解释成 WIN/LOSS。

Python 可选 parser 对所有层级实施精确键集合、固定枚举、bool/int 分离、signed int32 实例/view、实际 u16/u8 target、uint32 raw packet、规范十进制 u64 和有限 float/null 边界；unknown keys、非法 null、伪造历史锚等被拒绝。存在的诊断必须完整有效，缺省仍接受旧协议。schema/interface/protocol3/native ABI2/action allowlist 均未改。`DiagnosticNumber` 实际使用 Convert.ToInt64，完整容纳 UInt32；本席预审曾误认为 Int 链路，已核对源码撤回该非问题，未要求或引入生产修补。

本席实际执行三个新增命名用例，3/3 PASS（0.090s，exit0）：`test_settlement_diagnostics_fixed_nullable_raw_evidence`、`test_settlement_diagnostics_strict_negatives`、`test_settlement_diagnostics_round_trip_does_not_start_or_settle`。另实跑冻结自有 `CampaignTests.exe`，177 checks PASS；将该次进程输出的普通诊断与四个字段均 UInt32.MaxValue/report UInt64.MaxValue 的 raw packet 诊断分别直接送真实 `_settlement_diagnostics_valid` 和 `_campaign_valid`，两份均 PASS，原 attempt 仍 UNKNOWN。夹具只含 production Campaign 源与自有 synthetic 模型，不加载或连接游戏；exe SHA256 `37eb002cf827e25eae514965d5638748a48332289bc300de491ddd3a0a11266c`。

独立只读17项身份/证据核对全部 PASS：六冻结源、12 manifest sources、build重算、三资产及build副本、三份x86 PE、payload/assembly seal、59旧方法逐段精确相等、12复用源与四旧 suite receipt hash、174项 metadata/legacy refs readback。59旧方法包括 Capture/ContentJson/BattleFrame/Begin/Observe/所有动作与门禁，既有方法只改 Json 的诊断序列化。另对四个 pinned DLL 重新计算实际SHA，并以 file-backed PEReader 实读六个 getter 的 raw IL 与字段链，全部 PASS：均为 `ldarg.0; ldfld; ldfld; ret`，无调用或写入。该步没有运行作者会写 artifacts 的验证脚本，也未加载游戏程序集。174项完整 metadata、legacy build和作者34个 CampaignBridgeTests 是作者既有证据 readback，本席未冒称重跑。

本席本轮只修改本报告；未写生产、测试、runtime、作者 artifacts、账本或 guard，未跑 full/native build/真实连接。父控制器独占实际旧现场只读取证与完整集成验证。原 UseEnd/late1032 与 embedded-only 正判条件 concern 保持原范围，本包未修改任何结算判据；此次源码 CLEAR 不解除旧 UNKNOWN，不构成权威结算、真实 LOSS 或业务验收 PASS。本席分配的新诊断窄审与报告已完成，无剩余独立工作。

## 原目标通关后态、旧 attempt 处置与未来进度证据边界

本轮独立读取冻结07/08与 `10-settlement-readonly-live/`，未触游戏、源文件或 guard。07（02:25:20.1849439Z）和10（02:52:55.5810336Z）同 PID2648/creation134378768910968977、角色55290000000275、Campaign root11726；07当前 Campaign 实例11774，10的 Battle11642.parent 仍为11774。07原57:3为 finish_count0/max1、!killed；10通过同 Battle 父对象直接读原57:3，为 finish_count1/max1、killed=true、matches_battle_target=true。Battle和normal result均给 raw ReportID55290000099281，绑定匹配且非replay/cross/surrender，waiting/useEnd/fighting=false、pending absent。原目标 canattack 仍为true，不应误要求它变false；killed 已使它不再 eligible。

该证据明确支持“当前该角色57:3已经通关”，强于单独 eligible3→5。但它不是原 attempt b8dee45dad214598b067c01500a15929 的最终 packet 回执：旧08只有未捕获 receipt，新10 Observer NONE、全部历史锚 unavailable，raw ID相同只能证明新后态内部关联。07/10之间没有原 troop/data_ epoch或979/1025 pending历史；同UI实例不排除原位刷新。旧事件225个command中只有180/183/186三个campaign-command，186后至stop225没有第二个助手业务命令；这排除了该记录范围内的第二次助手开战，不是所有游戏网络回调的日志。控制器提供的无第二次attack/未点结果为操作事实，不伪装成原packet或引用证据。

本席对四个 pinned DLL 的全部方法按真实IL指令边界扫描直接调用/字段写入，并核对命名源码，得到以下实际竞争写者；不是凭对象epoch猜测：

| 写者/调用者 | 对本判据的含义 |
| --- | --- |
| CampaignUI.sc_campaign_battle `06000684` | 初始1026中自增finish_count的分支仅type17；本次type0排除此分支。 |
| CampaignUI.BattleEnd(SCBattleEnd) `06000685` | type0的finish_count++在pkt.iswin下；达到max才killed。正常调用者只有BattleUI.BattleEnd `060003A1`，其result0/type0才进入CampaignUI。 |
| CampaignUI.BattleEnd(debug) `06000686` | 合成iswin=true再调正常BattleEnd；唯一调用者CampaignTroopUI.sc_debug_campaign_no_battle `0600065A`，接979。UI发送按钮要求character.gm，但已排队回包不能只靠当前gmfalse排除。 |
| Character.addCampaign `0600033A` → Troop.make `06000316` → _character_campaign_troop.Set `060000AD` | 对既有troop和原data_原位覆写finish/killed，两个引用完全不变也可0→1。直接调用addCampaign的只有LoginUI.sc_playerinfo `06001830`（16，先替换Character）和RootUI.sc_campaign_enter `06001EBC`（1025，原位更新一个campaign或campaigns集合）。 |
| CampaignTroop.setDebug / CampaignChapterUI.sc_campaignreset | 前者可改killed但不增加finish_count；后者把killed清false，不制造完整0→1且killed=true证据。仍应排除debug和reset路径。 |

所以旧attempt不得按未来新增门槛回溯升级为WIN。控制器提出的 `target_already_cleared` 独立处置在此边界 CLEAR：保留原UNKNOWN、loss0、pending原文和历史锚 unavailable，只认定一次性目标已完成并禁止重打；resolution须绑定完整旧PID/creation/角色/session/seq/attempt/57:3及封存证据hash，不标为captured packet、不手工改旧消费账。确认结果前仍保持旧业务阻断；只允许精确旧目标的独立正常确认处置，不能泛放宽现有ack_result（它要求SETTLED，新Observer NONE并不满足）。确认后必须回稳定地图、重读原目标仍cleared，才可在保留resolution和全部原guard内容的条件下处理该条准入；其他未决来源仍全局阻断。新run只能由显式GUI开启并拥有新计数，不自动转换旧unknown或伪造LOSS/WIN。

10的后续封存快照已独立对齐：02:57:44 NORMAL → 02:57:59 REWARD/global_input_disabled → 02:58:17 SCENARIO647:0 → 02:59:15 CAMPAIGN/PASS/tutorialNONE。最后Battle11642 hidden、resultnull、全部pending/结算flags清空，原57:3仍finish1/max1+killedtrue，eligible仅5。这满足上述“正常确认后回地图并重读旧目标”的后置条件；不补旧receipt。10初始 `state.json` SHA256 `b9fc6d7b1332040768e9d960a6edb11527ed42eea99601e113014875b5074355`、`capture.json` `963a4ccbcf36f97331ece407101f97c4eda1ea520a82e48971e64d8d9295fae6`；末 `after-skip-state.json` `a6741be2e78e7f6fddf1a3e12e82b64807d4c3cc2ddd744e3a3a15d494cacf6c`。

未来新增 `anchored_mainline_progress_applied` 作为独立业务WIN，在控制器明确的“普通单控制器、无外部手工/其他客户端/篡改”运行边界内，设计可以 CLEAR，但不是本轮已实现/已验收。需在本次Begin前实际冻结角色引用+ID、type0父/root/scene、troop/data_、target IDs、finish_count/max和!killed；同monitor原子排除979/1025的handler/messages及前轮1032未决来源，不能只看1032 queue空。全程gm/debug=false，助手在未决时不发送979/1025/enter/next，也无并行控制；角色替换、重连、scene/target/data/token变更或未知检查均UNKNOWN。必须已有本轮自然零ReportID及新的固定report token，再见确切count+1==原max、killed翻转、同Battle/normal result的非零ID匹配和waiting/useEnd/fighting清空、非回放/投降；这才在排除上述已识别其他writer后推导本轮正常成功应用。用独立evidence值且packet_captured=false，不冒称最终packet已捕获；没有精确增长不能推LOSS。exact troop/data_锚本身不足，1025原位make反例必须保留为回归。

未来1032队列正向捕获的最小实现仍是同现有client monitor零等待TryEnter、固定数量/长度上限，只读MemoryStream副本且不动Position/message/handler；确认现slot exact方法和target为本轮Battle，按实际uint32与u64解析固定result/type/iswin/target/reportID，再等待同轮自然应用。只有result0/type0、目标/角色/attempt/零锚/token一致且无冲突才可由iswin记WIN/LOSS；result1、错type/target、零ID、旧/重复冲突、超长、miss均不成功。tcp_base.Run同次run_data可Parse并立即移除/close该消息，故此方案只能保证正向证据正确，不能保证每场可捕获；miss维持UNKNOWN，不能靠加速轮询宣称可靠完整。若将来必须消除该时间盲点，需要另议有持久事件面的被动流证据；本轮不安装、启动或要求网络监控。

本席本轮实际验证限于命名冻结JSON/hash对齐、4个pinned DLL的file-backed IL写者/调用者扫描和命名源码核对；无新增产品测试/full/native build/游戏操作。旧attempt的目标完成处置和未来受限进度WIN设计可CLEAR，旧最终packet及实际新实现/LOSS仍UNVERIFIED。

## 结算捕获与精确目标处置实现最终窄复核（CLEAR）

本轮实现评审结论 CLEAR，无剩余实现 blocker；适用前述普通单控制器、无同时手工/其他客户端/篡改的运行边界。生产 build `ebe69db9e64da4781880b071640bd160290d378ff7d5c9172d544ccc38d63d0c`，payload SHA256 `e92c52630c338362b1c1582986c688b2dfdd98686a9dd52ea2a7454363dc575f`；综合冻结 `13-settlement-integrated/freeze.json` SHA256 `d9319c8118da9ac502429b2abd9c69b7b2ec64574b7b71a473ab1f06b6e53dc3`。本轮明确获准改变 Begin/Observe/BattleFrame，09候选“59旧方法不变”的结论不能冒用于本轮行为改动。

managed实现已落实三条独立证据路径。`UseEnd=true` 看到 embedded 只做失配检查，不保存为可应用receipt；晚1032队列包通过原client monitor零等待读取，复制后锁外用实际固定protobuf API解析。限制64项/8包/每包65536字节，原stream游标/内容/Length/CanRead和queue/handlers不变。绑定要求当前同attempt的零报告、需要late end、result0/type0/目标匹配/非零新report，存值副本；必须再成功读取同队列，确认同item已移出，且同Battle/normal result的新report一致、flags清空，才给queued WIN/LOSS。拒绝包、同item内容改变、不同item/冲突、旧零report或错target均不能被进度覆盖。早结果窗在wait/use/fighting仍有任一true时继续WAITING；早result缓存report0最多等10秒，非零失配或超时UNKNOWN。

进度WIN使用独立 `anchored_mainline_progress_applied`：Begin主线type0、未完成、非GM/debug，并固定角色、scene/currentRoot、Campaign/CampaignData、原troop/data_、count/max与旧report；同锁基线拒旧1032slot/queue和1025/979slot/queue。进行中保引用/目标/reporttoken与自然零锚，拒刷新/debug/GM/replay/surrender/播放重启；只允许count精确+1达到原max且killed翻转，再匹配本轮自然报告后态。无增长不能判LOSS；DebugJson的receipt_captured仍false，不冒称捕获packet。隐藏加载清m_troopID时按原attempt目标保锚。coordinator在pending challenge时只观察，不发送continue/enter/next，满足本运行边界下不新增替代写者请求的条件。

准确限制：实现没有在捕获时读取1032 delegate的exact方法/target。控制器已明确保持sole-controller普通链边界，使用Begin无旧slot、当前item副本/同item移出及自然Battle后态作推断；本报告不声称“已核验handler exact绑定”，也不声称防外部slot修改。Update抽样可能漏掉同次Run内消费的包，尤其LOSS；漏收且无严格完成进度时仍UNKNOWN，未放宽为非零ReportID或画面胜利。

Python共享 `valid_settlement_evidence` 允许embedded/queued两类WIN和LOSS、进度仅WIN；`target_already_cleared` 不被parser或coordinator当成SETTLED证据。独立处置保留旧完整checkpoint/guard/UNKNOWN/loss0/consumed=[]，只把精确已处置fact从业务准入中排除。runtime逐条遍历最多64个prior来源，每条仍须完整有效proof、同PID/creation/session/角色及完整business值，并绑定当前磁盘原checkpoint的精确bytes；别的fact、冲突或来源漂移仍阻断。`_finish_stopped` 在已有guard值完全相同时不重写文件，readonly cleanup保留原bytes。startup只显示独立历史处置且保留historical_pending，不改checkpoint，不自动启动；新run需显式GUI start。

`campaign_recovery.py` 的builder/validator无游戏或持久化副作用，严格检查resolution版本与语义、四份嵌入原bytes/hash、旧UNKNOWN来源和新helper NONE/null锚、NORMAL前态及同Battle稳定地图后态。CLI prepare拒写active目录；publish必须指定既有candidate并匹配固定run路径、四源精确bytes，写前再次读回，以exclusive hard link发布完整内容；相同输出幂等、不同输出不覆盖。哈希只约束bytes，不认证任意供应快照的真实性，真实候选来源另由本次冻结现场证据核对。

本席实际运行20个Python定向用例，20/20 PASS（0.199s，exit0）：recovery/CLI13、resolution integration4、queued/progress parser与coordinator2、readonly guard byte-for-byte1。实跑 `11-settlement-capture/CampaignTests.exe` 300 checks PASS，exe SHA256 `09a419b916ab407487cc765178411184238955ed19fdac86b019334c896590a5`。覆盖晚queued WIN/LOSS、早UI、late reject+旧embedded、同item/报告/身份冲突、原位刷新/GM/debug、进度只WIN和严格增量、队列争用/界限/真实protobuf、隐藏加载保原目标；夹具只用production源和自有模型及纯protobuf库，没有游戏进程访问。

另把真实 `12-completed-target-resolution/candidate-after-cleanup.json` 放入独立临时project，只读核对原四份源并实际调用runtime准入：原精确fact允许；改business、追加另一fact、原checkpoint只多一个换行，三案全部拒绝。`validate_resolution/resolve_checkpoint/resolved_business` 实际通过，原文件复读不变，没有实际publication。候选SHA256 `1ed0df78b1ef2204ca2f752dfc09b5ce6625ed0f666b521281f05f0e5ce0d90f`；其原checkpoint SHA `1089be42e2b39c5927841fe08d77c5cd38e827f93fffcd1029f2fbc5a2fe71ea`、原guard SHA `cd4337865bc8459d645bdc726a275348e75bef174984ff34bdd4dba61b91fe4e`、before/after仍与上一节命名快照匹配。

独立16项身份/证据核对全部PASS：10个Python冻结源、8个定向源、5个frozen副本、12个manifest源和build重算、manifest三处一致、三资产/build副本/x86/seal、candidate、201项metadata与严格legacy refs readback、原reader AST hash复用及root全套测试readback。root的258 Python PASS（12.768s、exit0、inputs_stable）和完整native149/300/814/path/synthetic/legacy build为控制器执行证据，本席未重复full/native build；201项metadata本席本轮仅readback，不冒称重跑。

Changed files：本席本轮仅追加本报告，未改源码、接口、runtime、checkpoint/guard或发布resolution，未进行live操作。实现新增两种settlement evidence与独立resolution接口已在上述边界复核。未完成的真实publication、新attempt正常WIN/LOSS、队列可观测率及连续失败/完整业务验收均归控制器；旧attempt最终packet仍缺失，原UNKNOWN不被此CLEAR改写。本席本轮评审与分配的定向检查已完成。

## Reward 确认后续阻断差量复核（最终 CLEAR）

本节冻结候选取代上一节13的当前实现身份。控制器在真实10 NORMAL→REWARD后态识别到旧门禁会误拒正常下一步：reward的Global.extern_enabled=false、当前选择troop_id=0，旧Available/global gate和按当前选择发送的troop会同时挡住ack_result。命名源码 `BattleResultUI.showResult2` 的普通mine_ui空字符串按钮执行Hide→OnClose，不以该全局flag作前提。此问题属于结果后续流程，不改变胜负证据。

最终 `14-reward-confirm/freeze.json` SHA256 `67dd414b1bbf542cfb7576ea06d78ad4c601ccb6c9b0df55c52c0a1ee0ef3a45`；build `b7539200447015dfd25a0df4233492f088e2610583f793b13b48792ba03f17cf`，payload `a16595d8813739121f1886c7856d61d90956b13f1f420a3d5100d50001d93671`。差量结论 CLEAR，无剩余实现blocker，前述sole-controller和队列miss边界保持。

Observer只新增只读AppliedTo：要求实际SETTLED及私有已结算report非零，当前scene/Campaign/CampaignData/Battle/角色引用与ID仍为原锚，同raw report且无等待/use/fighting/replay/surrender/pending packet。独立逐段比较确认旧Begin/Observe等方法完全不变。Model只改EvaluateContext/CheckCommand/Execute并新增NormalRewardAcknowledgement：仅RESULT、唯一绑定普通reward、无normal result/scenario/chapter、局部Enabled、原campaign/troop、原报告后态及唯一当前Campaign.BonusOpen回调可以使该reward快照Available/PASS；globalfalse时只放行该精确ack_result。其余动作、错目标/owner/callback和非SETTLED仍拒。成功仍执行原Hide→OnClose；隐藏后不再匹配，不能重复确认。Python_submit只对ack_result使用已结算attempt.troop_id，保留当前snapshot选择troop0的原义。

本席实际实跑14冻结CampaignTests.exe，363 checks PASS，SHA256 `34897af503d24745c2ab7c3da5458c5e5099f264e5c9082aa1d10609be7009ac`；新增63项覆盖globalfalse/troop0正例、其他六动作拒绝、troop0命令拒绝、仅一次callback，以及非SETTLED、错troop/report/Battle目标/角色、cross/replay/surrender、wait/use/fighting、非campaign/其他模式、局部disabled、错owner/callback和多回调负例。另实跑 `test_normal_reward_selection_zero_uses_consumed_attempt_before_skipping_story`，1/1 PASS（0.137s，exit0）：NORMAL→REWARD troop0→647剧情Skip→CAMPAIGN无后续目标，只start一次、ack两次、continue一次，loss0且只消费一条原WIN。

独立13项身份核对全部PASS：五源/五frozen副本、363夹具身份、12 manifest源/build重算、三处manifest、三资产/build副本/x86/seals、strict legacy metadata readback、其余八个Python源沿用13精确hash、旧candidate1ed0df...不变，以及root新259 full Python exit0/inputs_stable readback。控制器实跑全套259（12.835s）与新生产build；本席只运行分配的差量定向，不重跑full/nativebuild或连接游戏。resolution candidate、胜负标准、原guard/checkpoint没有因此变动。

本席全部获派窄审与报告现已完成；仅本报告由本席修改。最终CLEAR允许控制器继续其已授权的冻结candidate发布及显式GUI新run验收，不代表本席已经发布、操作游戏或完成实战WIN/LOSS/连续失败验收；旧attempt仍UNKNOWN。

## 首场真实 queued WIN 与过渡等待差量复核（16 范围 CLEAR）

本节只读审查 `15-first-confirmed-auto-win/` 首场冻结证据与 `16-transition-wait/` 三个Python文件。16 freeze SHA256 `80f55c4cab9071cf1f5155f48fadae5266811bb84917f7185e620784e1aefac9`；campaign_task SHA `2d347f3ff2a4183d4a65cf267ff8974ce50cf1a359e84249b585265340ad37c2`、app `3fc79b654f48e88ef3c255a3f27dc2ffab683a85567bc1c9e965c9cf03c9f719`、test_campaign_task `4542650f9a20a07325de942ae7f2dfd6c8cd4b9b7a1d833ce46527e9f06fef01`。16差量 CLEAR，无实现blocker。其他owner正在准备的17 intro native分支不属于本节，不将此CLEAR扩展到它，也不对其源码/构建作身份断言。

真实首场正向证据已独立对齐：PID2648/creation134378768910968977、角色55290000000275、helper e32267df91174b1b9b0dbe066036b374、b753包；新run cd50e31d5c934e90a914f41176d1851d，target57:5，start seq9、attempt86135474b5b84e12aabeeea8e86c18be。15.transitions共16个封存变化：03:20:15.386Z SUBMITTED；15.787 WAITING；20.638已捕获 `captured_queued_authoritative_receipt`、report55290000099282，仍WAITING；22.147同attempt为SETTLED/WIN/`captured_queued_authoritative_receipt_applied`，run精确消费一次、loss0。state/ACK/session一致保留该终态；Observer的zero/battle/receipt及角色/Battle/reporttoken匹配全部true，原目标finish1/max1+killedtrue、raw报告匹配、等待/结算/战斗flags清空。此场证据走实际queued receipt路径，独立于进度WIN；不使用画面等级推胜负。

首场封存native事件只有seq3/6/9/49四个business命令，先后选择/准备/本attempt与正常result确认；最后business receipt为seq49 ack_result invoked=true。此冻结窗口没有第二次start或重打57:3。`run-before-transition-fix.json` 仍pendingnull、consumed仅该attempt/report、loss0，暂停原因是 `campaign_gate: global_input_disabled`。关键SHA：transitions `94d05f4e4d84f299ef8f4625da1844d501ac084bcd76bb44286b2bc78c0459e2`、state `0003e41ef9ed09261df153eb93b8a47afd34a85a4a1e74d3e46d4bc14ea9c90a`、run `d58833d08960d7b1355f102cd175d1feed1ef74c3966f1cc59994eb80c7aabaf`。这些是首场原副本，不与后来同目录three-wins-*记录混合。

暂停发生在已确认WIN之后：22.247变CAMPAIGN/globalfalse且m_bPostInit=true；22.650旧coordinator暂停；23.858真实REWARD/PASS随后自然出现。故这个后续阻断是把已识别原生过渡当作立即停止输入理由，不是本场最终packet缺失或LOSS。另首场row27有TROOP/campaign_loading，row31有FORMATION/campaign_loading；控制器据此明确授权把前者也纳入同一等待上限。

实现只在active、未pause/stop且没有pending intent的导航边界等待。必须available=false、selection_gate=PASS、当前Campaign root active、type精确int0/unlocked/!debug。只认可TROOP/campaign_loading、FORMATION的formation_loading或campaign_loading；CAMPAIGN仅global_input_disabled/campaign_map_not_interactive/campaign_loading且至少一个固定原生动画flag为true。该分支没有submit或业务输入；从首次进入连续过渡起使用monotonic固定30秒预算，不随revision重置。PASS、pause或stop清timer；超时pause为game_transition_timeout，不增loss；未知gate、错绑定/type、无必要动画等仍立即pause。原pending挑战/剧情处理和全部胜负/evidence/guard规则不改。

本席实际运行三个分配用例，3/3 PASS（0.256s、exit0）：完整NORMAL→三次过渡poll无新输入→REWARD troop0→531 Skip→CAMPAIGN；超时没有game input/pending/loss；未知/无绑定/无flag/错type及非特定TROOP gate负例。独立AST差量只有Coordinator.__init__/_run与新增_game_transition，其余方法沿用14；三源与frozen副本精确。将新谓词直接用于15原冻结形状，恰好接纳row27/31/75/79四个已知loading切片。七项只读身份/证据核对全部PASS；本席未重复full/native build，没有live操作。

控制器已发布旧target_already_cleared候选；本席独立只读核对实际resolution SHA仍为已审 `1ed0df78b1ef2204ca2f752dfc09b5ce6625ed0f666b521281f05f0e5ce0d90f`，原checkpoint `1089be42...71ea` 与guard `cd433786...fe4e` 仍与前节完整SHA精确一致，旧UNKNOWN/loss0未改。本席只追加此报告，没有发布或修改这些文件。16获派审查完成；真实LOSS/连续失败、后续intro及完整业务验收仍未由本节确立。

## 新功能图标 intro 正常继续与18生产身份窄审（CLEAR）

17新分支与18整合实现独立窄审 CLEAR，无剩余实现blocker。当前最终生产build `3032af89de9c037d9ab6b08fc08df5ce81b3878acf3c04eaa9f78dc4b46e78dd`，payload SHA256 `89352403254fe6cf97737261a6a815f662db5b2a03de5f1e24f89345bd4a9c2c`；`18-final-campaign/freeze.json` SHA256 `8892762b48ed07c88d861543e68c970495ec58cd6350f95f60736119a54ebafe`。本结论不把17的实际653前态当成新分支已经成功执行。

本席独立阅读pinned ScenarioUI.Update/Close()/Close(Rect)/Web_OnGUI/FunctionLoad/cs/sc_scenario_end/scenarioSkip。普通左键的intro分支在Update中只要求hasNewUI且!moving，并写m_bMoving=true；不直接调用Close。随后游戏Update逐项newUIMove，到齐才m_bNext=true并清newUIList/character.newuis_；原Web_OnGUI再经过Close(Rect)保留FunctionLoad、非system Close或system1040及OnClose。再调scenarioSkip会增加section并重新设置目标/参数，所以不适用于此末段intro。新实现仅在真实GUI且原publication/stop/lease admission通过后写固定m_bMoving字段，不调用Skip/Close/Hide/Send/feature，也不合成Input/Event。后继游戏自身FunctionLoad可能展示功能教学，仍保留其完整路径；未知UI继续受既有门禁阻挡，不代表允许培养、购买或自动点击功能。

新ScenarioGate沿用当前root/Campaign/Scenario归属、visibility/fade/loading门禁；末段intro要求1..8个互异exact ScenarioUI.newUIBtn、非空有界name、固定Texture/正有限尺寸和有限有界cur/target Vector2，以及已绘制/静止/未完成的固定flags。name只作冻结身份，不映射功能。排队还冻结scenario id/分支、sections引用/数量及各section/trigger、templates引用/数量、param0、callback、列表及item/texture/name/size/pos、want/trigger/first等；真实GUI前逐一重验。文本Skip不能静默换成intro；改变任一关键值、GUI/Event不准入或publication取消均不写移动标志，命令被单次消费。动画期WAITING_MOVEMENT，列表自然清空后的WAITING_SCENARIO_END只观察，不能重播Skip或第二次continue。

本席实际实跑17冻结CampaignTests.exe：499 checks PASS（原363+新增136），exe SHA256 `008607eccfade66743455b030efb748687de2fc60f659d7fd5e8fb10601d8d91`。覆盖真实GUI单次启动、global/local input关闭的原左键语义、list/callback/完成标记保留、移动/末段等待、全部排队身份/字段变化、非法项/重复/越界/非有限值/贴图/fade等负例、发布前取消和重放拒绝；只用production源+自有模型/纯protobuf库，无游戏访问。本席另将冻结metadata verifier的唯一artifact写行在内存中移除后执行，实际四pinned DLL的235项file-backed字段/签名/方法IL核对全部PASS，未加载执行游戏程序集，也未写作者artifact。输出 `17/campaign-metadata.json` 只读hash为 `8df0494a04be16f6cef19bb315f97c293a01d8ae8be00210ceda8f3a98cc8dd3`，本席未改该文件。

Python整合只把WAITING_SCENARIO_END纳入Coordinator.SCENARIO_WAITING；相对16所有方法AST完全相同，没有新增输入路径。实际运行两个扩展命名用例，2/2 PASS（0.411s，exit0）：`test_scenario_waits_until_ready_and_confirms_step_before_next_continue`、`test_real_parser_allows_only_known_nonblocking_hint_and_dialogue_waits`。原step等待不发输入；已有pending guide即使同step重新PASS也不重发continue，只在自然step变化或NONE后记录完成。界面显示intro或动画入口成功不能提前算整个教学完成。

最终独立17项身份/证据核对全部PASS：18的16个source hashes、17的7个冻结源码/依赖副本、12 manifest源/build重算、三处manifest、三资产/build副本/x86/seals、严格legacy refs、root262 full readback、原发布resolution/checkpoint/guard不变、Model差量范围及Python方法复用。Model相对14只改EvaluateContext/Execute/QueueGui/ExecuteGui/ScenarioGate并新增四个intro helpers；Observer/Probe/协议/lifecycle/结算算法未变。root生产-RunTests的499/149/814/native/path、严格metadata与全Python262 PASS（13.330s、inputs_stable）为控制器执行证据，本席未重跑full/native build。

实际 `17/actual-653-before.json` SHA256 `d0e6364d3079821a2a41aa4d67d1a39a867ac1334243980c4590c0174670fae6` 只证明旧b753在57:7已确认WIN后、当前653:2被旧scenario_new_ui_unreviewed阻挡，不能证明新intro启动、动画、自然Close/1040/回调或回地图。本席只追加本报告，未写生产/测试/runtime/guard/checkpoint、未执行publication或live。获派17/18窄审已完成；该真实intro后态、后续新run与实际LOSS/连续失败/完整业务验收继续归控制器。

## 新将上阵、自动装备与普通布阵确认源码窄审（21 范围 CLEAR）

Status：DONE；本节获派只读源码与离线检查已完成，21冻结范围 CLEAR，无确认的实现 blocker。依据 controller 转达的当前明确授权“允许自动上阵/装备并确认布阵”，20报告在旧授权下保留配置动作的阻断不再适用。新增培养、成长任务与可复用升级模块由其他 owner 负责；本节未读取或评审未冻结23，也不把这些功能包含在本结论中。

候选仅为 `21-new-general-flow/source-freeze.json`，SHA256 `0d6fc77010b392d28a42c71295c129c3f622649fc0408ac180ae2e7505b8b5ef`。GeneralTutorialModel SHA256 `9ca8c060b7103fb811841f64cd64ab5ba62cd147150b2325f5246d7dc850a9c7`，CampaignModel `62b885955aa4e60f72a1be7b8d8b16cfceed6256c8b7d57c409060494814b2a5`，Probe `b2e97626bed17c62a6434788efc995b6f47f982cff901aba7f38b857377f721c`。这是源码/夹具冻结，尚无该候选的生产构建或 live 身份；18生产包不能冒充21。

独立复核普通按钮源码和固定 raw IL：仅真实 owned 的 `SpecialGetUI.showtype=general/tempid`，在当前 `form_generals_` 中有同ID对象，当前 `cur_form` 有完整八位 `spotgen`，且已在阵或存在 `spotnum` 内空位时，执行实际 `AddOneGenToCurForm(tempid)`，true时走普通 `cs_general_auto_equip(tempid,Attack)`，随后 Hide 和当前 exact 单一静态 `DelegateFuncUI.MainUI_formation_ui(showFormClose)`。104与其他ID的 showFormClose 分支有固定检查；wire 不接受将领ID、阵型ID或反射名。排队绑定 Root/GlobalUI/角色/client、popup实例和visible_time、字典/全部有界成员对象身份、当前form及spot值；真实GUI调用前重验。对普通UI原生异步加载只等待，不能把尚未可见的布阵当成失败后再上阵。

只有本次 open 产生的同一个 `{subui:Formation}` 参数对象、同Formation实例、普通 DefaultState/GenInfo/viewonly/onfight=false/OnClose=null/MainUI.m_subui、可交互和waitstate=0才能确认。ready后另绑定 CharFormation/formid/dbspot/16个位置值/visible_time，再执行本对象正规 OnClickClose；该方法正常保存与关闭，不调用 NPCBattle 的 OnAttack。完成还要求同保存内容、UI隐藏、MainUI subui清空、waitstate归零与网络空闲；响应缺失或上下文/选择变化保持 UNKNOWN，没有自动重复保存或上阵。两个动作均在同一 RootUI/Global client 的原monitor零等待读取 handlers 和messages的298/771状态；锁miss不能当空，不覆盖旧同opcode处理器。

CampaignModel差量只含 General 模块集成、专属phase/门禁、固定JSON、两动作GUI路由/取消失败及 BusinessJson；旧战斗/结算方法和 Observer 沿用18。Protocol保留v3，只加 `general_deploy`/`formation_confirm`，严格要求 campaign=0、troop=0、attempt为空。Probe相对17/18只有GUI动作条件与 BusinessJson转发两处变化。BusinessJson合并 General.MutationAttempted，因此上阵/装备/保存部分失败也保留mutation=true；pending_attempt和嵌套attempt仍只表示原战斗，不伪造fight/LOSS/WIN。GUI publication取消前无游戏调用与新mutation；真正调用前已发布当前教学所有权。`server_configuration` 始终 UNVERIFIED：handler消失、waitstate归零和本地关闭不等于服务端装备配置证明。

Reviewer实际运行冻结 `21/CampaignTests.exe`，608 checks PASS（保留499旧+109新，exit0；exe SHA256 `e4b1623cdad8eb174b2714786473c8514394ec80b407a5867263acb9c4cbafcb`）。覆盖普通完整链、已在阵仍正常装备、后续真实owned ID和复用UI的新visible实例、非战斗、异步加载、无GUI/disabled、queued字段/引用/修订变化、旧handler/queued消息、锁争用、重复/回放、发布前取消、部分异常、缺失确认响应与顶层业务mutation。夹具仅production源码及自有Unity/游戏模型和固定纯protobuf库，不访问游戏进程。

Reviewer另将冻结metadata verifier唯一Set-Content行在内存移除后实际执行，四个pinned DLL的284项file-backed PEReader检查全部PASS（235原+49新，exit0，game_access=false）；没有执行游戏程序集，也未改作者metadata artifact。独立27项身份/结果核对全部PASS：10个冻结source、8个保持18的底层source、6个artifact、metadata总数/全PASS/旧235键值保留。Model/CampaignObserver/Lifecycle/ReceiptStore/native八项均精确沿用18，其中Observer仍 `6cd8400608c3e3527a152334b797c6eb26e86dc7e394843765511a0351776b5c`。build.ps1/test.ps1正确枚举新源码和夹具，LifecycleTests只增加CampaignModel.BusinessJson stub兼容；本席没有运行这些full入口或生产build。

Changed files：本席仅追加 `docs/campaign-review-bridge.md`。被审实现10文件及证据详见21/source-freeze.json/REPORT.md；本席未写源码、测试、包、runtime、guard、checkpoint或作者验证汇总。Python严格shape/actions/pending guide仍待controller冻结后另行窄审；其当前未冻结修改不属于本节CLEAR。准确legacy生产编译、整合/full测试、生产包身份与真实新将教学后态均由controller负责，真实服务端配置和完整业务验收仍UNVERIFIED。
