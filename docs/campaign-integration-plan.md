# 征战自动推进：最小集成方案

状态：设计已完成；本文不启用业务调用。仅检查助手源码与相关测试，不读取游戏实现、不连接游戏进程。

最新用户决策：使用游戏自带的“跳过剧情”，替换初版逐段推进策略。自动接口必须执行正常 Skip 分支，保留游戏自己的触发、回调和后续教学；不直接写段落或任务完成字段。需要 GUI 上下文的分支在真实 Unity `OnGUI` 中执行一次，后继未知培养或购买界面仍暂停。下面初版有关普通“下一段”的限制不再作为禁止 Skip 的依据，历史检查记录保持原范围。

用户补充的业务模型：教学位于固定关卡，剧情随指定关卡通关/进度自然出现；征战为一次性主线，通关后不重复挑战。以游戏当前进度筛选未通关节点，正常通关回调连接对应剧情、Skip 与下一节点。失败重试和连续三败计数独立于一次性通关进度，不把显式新任务理解为授权重刷已通关关卡。

## 目标与规则

- 显式启动一次征战任务，使用当前角色的真实进度选择已解锁、可挑战的关卡；胜利后重新观察进度再决定下一步。
- 本次任务连续三场已证实的失败即停止；确认胜利将连续失败数归零，未知结果不改变计数。暂停、继续、桥接重连保留当前连续失败数。这是用户在可选问题中明确的最终规则，替换初始累计假定。只有显式新任务创建新运行；旧任务记录保留。
- 一次只允许一个业务战斗在途。结果未知、超时、身份改变、未知教学或资源不足时，不发下一次攻击。
- 允许已确认的正常征战消耗；禁止购买/补充/重置体力、使用高级货币、任意领取或购买、任意反射调用。可消耗资源及上限须在业务能力审核中明确，未审核时不发攻击。
- 教学仅在步骤已识别、行为已审核且前后条件可验证时推进；未知步骤暂停，保存进度和阻断原因。

## 当前事实与不能直接复用的能力

| 组件 | 当前证据 | 集成影响 |
| --- | --- | --- |
| `core.py:50–84,90–194` | `TaskRunner`单工作线程；`checkpoint/wait`合作暂停和停止；暂停冻结等待时间 | 继续负责任务调度，不负责游戏识别、桥接租约、资源或胜负判断。暂停不能冻结游戏实际结算期限 |
| `app.py:304–327,354–364,630–695,767–793` | 手工对象操作和任务互斥；F8同时请求objects与runner停止；关闭异步等待对象关闭 | GUI只显示业务状态并发意图；保留统一停止入口，新增任务不能绕过关闭/绑定互斥 |
| `objects.py:207–270,272–305,410–489` | 单操作worker与generation隔离；空闲时监控心跳/快照；关闭保留未知状态 | 此组件继续独占BridgeClient与会话；征战需要独立业务所有权，不能把整个战斗占为`_busy` |
| `bridge_client.py:26,474–603` | v2只接受snapshot/diagnose/heartbeat/disable-restore/stop；请求锁、单文件单序号、精确ACK关联；超时转unknown且不自动重试 | 现版本没有攻击、教学或结算能力；不得借字符串或通用反射绕过协议 |
| `Lifecycle.cs:7–40; Probe.cs:106–199` | 20秒租约；短事务期间只读stop；过期优先于命令；主线程检查 | 长战斗不能持续占据现有ActionPending；暂停期间也须维护生命周期 |
| `Model.cs:103–156,187–191` | 主界面gate拒绝加载/战斗/子界面/未知UI；唯一写操作为MainUI.Disable/Enable | `Safe/PASS`只授权当前验证动作，不代表关卡或教学可操作；需另建上下文专用gate |
| `bridge_client.py:137–148; Probe.cs:280–306` | 现动作回执描述禁用与恢复，干净停止要求对应恢复证据 | 攻击的正常体力消耗/游戏战斗不能伪称可恢复；不能修改旧判定将业务消费混为验证通过 |
| `ReceiptStore.cs:11,29–62` | 事件日志轮转；state/ack独立原子替换；Unity线程不重试/休眠 | 轮转日志不适合做唯一业务去重账本；业务任务记录须另存，不能靠最新ACK判断胜负 |

当前桥接验证的`PASS/FAIL`是调用事务结果，**不是游戏胜利/失败**。`COMPLETED`也仅描述该命令完成。

## 最小职责边界

1. 新增纯Python领域模块`campaign.py`：不可变观察/意图/事件、状态转移、连续失败、结果去重、关卡选择策略、教学策略。禁止依赖Tk、Unity、进程句柄或反射。
2. 新增`campaign_task.py`：由TaskRunner运行，调用类型化port、消费专属观察订阅、将领域事件交给GUI。不能读取`objects.events`抢走GUI事件，不能直接访问`objects._client`。策略计时使用注入的单调时钟。
3. `ObjectController`：唯一目标身份/连接所有者；发放任务令牌，串行提交所有命令，维持心跳和快照；验证身份、generation、关闭状态、bridge unknown和在途业务请求。任务令牌阻止并发手工验证与第二个任务，心跳和只读观察仍可运行。每个短请求才设置`_busy`，业务战斗在途记录独立保留。
4. `BridgeClient`及managed桥接：固定协议与类型化能力；在Unity主线程重新验证具体业务前置条件后执行一次已审核入口，记录结果与观察。不决定“三败停止”或自动选下一关。
5. `app.py`：任务开始/暂停/继续/停止，展示当前关卡、连续失败`n/3`、战斗等待、教学阻断原因与证据路径。`TaskRunner.IDLE`不是桥接清理完成或业务结算确认；分别显示。

## 建议的内部port（设计名，不是已有游戏方法）

```text
acquire_campaign(identity, generation, run_id) -> owner_token
subscribe_campaign(owner_token) -> 独立、有界、带revision的观察流
submit_intent(owner_token, intent_id, expected_revision, typed_intent)
    -> Rejected(reason) | Submitted(origin_request, business_attempt_id)
request_pause(owner_token) -> admission_closed
request_stop(owner_token) -> admission_closed + cleanup_handle
release_campaign(owner_token)  # 需业务检查点已写入；不丢弃未决attempt
```

类型化意图初步只有`ChallengeObservedStage(stage_key)`及`AdvanceReviewedGuideStep(step_key, action_key)`。字段范围、真实游戏入口、方法签名、资源上限目前未确立，因此上述port只能由fake实现，不能接入live。

观察至少包含：目标进程创建身份、角色标识（不暴露无关数据）、run_id、会话nonce、generation、revision、观察时间、章节/关卡及解锁状态、当前界面种类、正常资源可用性、教学步骤标识、业务attempt/战斗标识、终局结果及其来源。角色标识与终局标识的实际字段仍需证据。

## 状态与结果处理

```text
READY -> SUBMITTING -> WAITING_BATTLE -> APPLY_RESULT -> READY
  |          |              |                |
  +----------+--------------+----------------+--> PAUSED / STOPPING
任意未知/超时/冲突 -> BLOCKED_UNKNOWN（停止业务发令）
可识别教学 -> WAITING_REVIEWED_GUIDE -> 复核后READY
未知教学 -> PAUSED_GUIDE；第三次已确认loss -> STOPPING_LOSS_LIMIT
```

- 开始前：取得controller令牌，核对fresh观察、身份、版本能力及无未决attempt。先原子保存run及intent，再允许业务提交。持久化失败必须阻止发令。
- 串行：至多一个未终结intent/attempt。发布命令前再次检查暂停/停止门、令牌、观察revision和上下文；managed执行瞬间复核真实对象与资格，过期revision拒绝，不自动换目标。
- 传输ACK与业务结算分离：短命令仅提交一次动作并返回关联标识；随后的只读观察确认战斗与终局，不把整场战斗放进现有禁用/恢复事务。若真实游戏调用无法短时返回且允许主线程继续Update，此设计不成立，需回报控制器重新决策。
- 一次结果仅计一次：key必须绑定目标身份/角色、run_id及权威业务attempt或战斗ID，并关联原始intent/命令。snapshot命令自身的seq不能替代battle ID。旧关卡、上一战、旧nonce、旧generation、无关联结果均不计数。
- 仅可接受明确终局`WIN/LOSS`。失败计数和“该result已消费”在同一次持久化提交中完成；重复相同结果幂等。冲突结果进入BLOCKED_UNKNOWN；不得覆盖已确认结果或多计失败。
- `losses := losses + 1`仅发生于首次可信LOSS；WIN将losses归零。第三次连续LOSS落盘后立即封闭业务发令门，不允许下一场攻击。测试序列`LOSS, WIN, LOSS, WIN, LOSS`后仅为1，再两场LOSS才停止；重复旧WIN不得清掉后续连续失败。
- 超时、ACK未知、REJECTED、进程退出、取消、断线、结算缺失都不是LOSS。超时禁止自动重发，保留未决attempt；恢复只能先只读核对，不能将“未找到结果”当作未执行。没有可靠关联ID时，live能力保持禁用。
- 胜利后用新观察确认进度，再选择下一可挑战关卡；不得简单把旧关卡ID加一。失败后同样重新检查资格和正常资源，策略允许时才重试；体力不足则停止/暂停，不购买或重置。
- 任务检查点保存run_id、规则版本、目标身份/角色、losses、消费结果key、未决intent/attempt、已确认关卡进度、教学步骤和阻断原因。重启只读恢复这些记录，**不自动恢复攻击**；显式继续前先核对未决状态。

## 暂停、停止与在途边界

- `RunContext.checkpoint()`与下一次submit之间存在竞态；仅增加checkpoint不够。controller统一发令门必须与暂停/停止意图串行，保证“暂停请求已确认”之后无新业务命令提交。请求刚进入时已有一次命令可能在途，GUI应显示“暂停待当前提交确认”。不能持runner/GUI锁等待桥接I/O。
- 普通暂停关闭业务发令，冻结策略推进；只读观察、结果落盘、桥接心跳继续。实际游戏战斗继续，不承诺冻结游戏。终局可在暂停期间入账，但不能继续下一战。业务结算期限按真实单调时间计算，不用`context.wait()`的暂停冻结计时。
- 必须避免暂停让runner阻塞后连带停止观察：由ObjectController维护生命周期与订阅，或任务在进入checkpoint前先保存当前工作；结果处理由不受runner暂停阻塞的观察/账本路径负责。
- 停止先关闭业务发令门并记录stop意图。逻辑battle已在途且桥接健康时，显示“正在停止后续发起，等待当前战斗结束”，保持心跳与只读结果观察，终局入账后再销毁桥接组件。该收尾路径不能依赖已取消runner的checkpoint；由controller持有观察与账本责任。
- 现`app.stop()`先调用`objects.stop()`会立即进入组件停止，无法直接满足上述battle收尾。集成时统一F8入口应先询问公开的征战协调状态：活跃征战走`request_stop`关闭业务门并安排收尾，其他情况保留现有对象停止；controller按安全期限决定真正发送桥接stop。相应按钮必须区分“停止后续发起”与“组件清理待确认”。
- 实际结算期限须由游戏证据确定且有界；超过期限保存未决attempt并进入unknown，不发新攻击，再按会话安全规则清理组件。助手关闭也保留有界收尾；不能无限等待战斗。临时UI恢复事务、安全失败或桥接unknown仍优先走既有恢复/停止规则。
- 保留单命令协议：未ACK文件不得覆盖；只有已ACCEPTED事务可按既有规则被stop替代。若无法确认，保持unknown/租约，禁止重连绕过。
- 不承诺撤销服务器已接受的攻击、回滚体力或中止游戏战斗。桥接组件销毁与业务战斗完成分别记录。停止时未决battle必须留在账本，下次只读核对归回原run，不能算作新run的失败或自动再打。
- 关闭、解绑、身份改变都会封闭业务门，停止旧run、失效旧token；旧事件仍可作为原run取证，不能推进新绑定。保留当前异步关闭和首次unknown原因，不因GUI退出抹掉未决状态。

## 教学处理

每个已审核教学步骤需固定：游戏版本/对象类型、步骤标识、可执行入口与参数schema、允许上下文、资源影响、预期下一步骤/后置状态、最长观察期限。必须看到当前已审核步骤的真实标识，不能从“有弹窗”猜操作。

控制器提供的本次实机观察（本设计任务未独立复验）：点击“征战 → 小卒一普通6级 → 攻击”后停在FormationUI，气泡“获得新战技【破阵一击】”指向主角战技“新”，尚未开始战斗。这说明“攻击入口已点击”不能等同“battle已提交”；需要单独识别进入布阵、已审核教学、最终提交与真实战斗阶段。该气泡可作为待审核步骤候选，具体对象标识、无额外花费和可接受配置影响仍由游戏对象调查确认；不得仅根据文案自动点击。

- 仅对允许列表步骤执行一次；确认步骤变化后才继续。重复观察不会再次触发同一步。
- 若教学要求发起战斗，仍走同一业务attempt和三败规则，不能另开旁路。
- 已识别、已审核且无额外花费、行为影响可审定的步骤正常推进；不将所有教学一概阻断。购买、补充体力、高级货币消费、未经审核的配置变更、跨上下文或未知选择一律暂停。
- 未知教学保存已知进度和步骤原始标识；人工处理后重新读取状态，不能只按“继续”按钮越过gate。
- 不把教学UI加入全局ambient豁免；建立该已审核步骤自己的gate。未知UI仍阻断。

## 协议与生命周期的必要扩展

业务协议须独立版本化（建议v3），同步更新Python/managed parser、类型化payload、manifest/哈希与回执校验；保持v2禁用/恢复验证及拒绝未知命令的语义。

需要区分：传输`ACCEPTED/COMPLETED/UNKNOWN`、业务`SUBMITTED/WAITING/WIN/LOSS/UNKNOWN`、自有临时UI状态的恢复证据、组件销毁证据。业务动作不得伪填`restore_verified`。新业务动作的干净停止条件是自有临时状态恢复、组件销毁、未决业务事实持久化；游戏进度和已发生消费不回滚。未知桥接执行/恢复仍阻断重连，正常未决战斗则阻断新攻击直到核对。

不要只改`COMMANDS`列表接入业务：当前`request`取消特判、`_is_clean`、`_action_result`、`Probe.ActionJson`都专用于disable-restore，必须按动作种类审阅迁移。不能任意加长20秒租约来掩盖单命令阻塞，也不能让暂停触发租约过期。

## 后续文件所有权建议

| 单一owner任务 | 可写范围 | 与其他owner协调的接口 |
| --- | --- | --- |
| 领域状态机 | 新`campaign.py`、新`campaign_store.py`、新`tests/test_campaign.py` | 冻结的事件/意图/检查点schema |
| 任务与controller集成 | 新`campaign_task.py`、`core.py`、`objects.py`、对应tests | typed port、原子admission、专属订阅、token生命周期；不直接改GUI |
| 桥接业务能力 | `bridge_client.py`、managed `Protocol/Probe/Model/Lifecycle/ReceiptStore.cs`及对应tests和构建身份文件 | 经游戏证据审核的固定能力、结果schema、版本拒绝和清理语义 |
| GUI集成 | `app.py`、GUI生命周期tests | 通过公开控制器方法与业务事件交互 |

本文只拥有`docs/campaign-integration-plan.md`。上表是未来派工建议，未授权实施这些文件；并行工作不得共写对象生命周期或协议文件。

## 可先实施与待证据事项

可先实施纯状态机、fake port、业务账本与离线测试：连续三败、胜利清零、重复/冲突结果、旧身份/会话、超时、取消、暂停与提交竞态、未知教学、持久化失败、重启未决恢复；此时业务功能入口保持不可用。

live接入仍缺：真实征战入口及签名、章节/关卡与解锁关系、一次攻击对应的权威attempt/战斗ID、终局WIN/LOSS来源与生命周期、正常资源消耗与失败消耗规则、教学步骤标识/入口/后置条件、业务在途时组件安全销毁的证据。没有这些不能将快照验证成功视为可以自动征战。

## 本次验证与验收界限

2026-10-31运行9项现有定向Python测试，全部PASS（1.026s）：暂停checkpoint可停止、暂停计时、精确命令/结果关联、旧PASS拒绝、未ACK不得覆盖、已接受动作停止待销毁、controller在途停止、换绑定旧事件隔离、runner闲置时F8仍到达对象层。

命令：

```text
python -m unittest tests.test_core.TaskRunnerTests.test_paused_checkpoint_is_interruptible_by_stop tests.test_core.TaskRunnerTests.test_wait_freezes_while_paused_and_resumes tests.test_bridge_client.BridgeTests.test_exact_command_sequence_ack_and_action_result_correlation tests.test_bridge_client.BridgeTests.test_previous_pass_cannot_prove_new_action tests.test_bridge_client.BridgeTests.test_unacked_action_not_overwritten_by_stop_or_retried tests.test_bridge_client.BridgeTests.test_stop_supersedes_only_accepted_action_and_waits_for_destroyed tests.test_objects.ControllerTests.test_stop_during_action_requests_one_stop_without_retry tests.test_objects.ControllerTests.test_binding_switch_suppresses_late_previous_player_and_clears_queued_old_events tests.test_app_lifecycle.ShutdownTests.test_stop_reaches_object_call_when_task_runner_is_idle
```

测试只验证现有安全边界；未实现或验证新状态机、新协议、真实教学、征战推进、真实胜负/资源或三败停止。未运行完整构建、managed套件、CI或游戏验收，均由controller另行安排。
