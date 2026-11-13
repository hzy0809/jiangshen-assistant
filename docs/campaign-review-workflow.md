# 征战自动推进：Python 工作流独立审查

Status: **DONE（F4 epoch 修复的组合窄复验完成；F4 已关闭，本轮无剩余阻断项）**。DONE 表示本轮审查工作完成，不代表真实业务验收通过。本报告不批准实际业务运行。

## 最新范围：F4 同 revision 已证未执行后的显式重试

本轮只读 `campaign.py/campaign_task.py/tests/test_campaign.py/tests/test_campaign_task.py`，仅写本报告。未运行 full/native/game/network，未修改生产源码或派生子代理。controller 报告的新 Skip 在缺少 OnGUI 上下文时精确 REJECTED+invokedFalse 且 native revision 不变；本轮审查领域归还未执行 reservation 与协调器暂停/继续的组合行为。

| 路径 | 本轮初审冻结 SHA-256 |
| --- | --- |
| `jiangshen_assistant/campaign.py` | `31C5BEDAF40E74F48A6DB47CC43A65CD754FF78B2336A8C6B6D5540C706F38FF` |
| `jiangshen_assistant/campaign_task.py` | `32563B291109364CAA0507C2933B3065B434B553A135C744F5A89BD72DA4FD8C` |
| `tests/test_campaign.py` | `22DA12AD2CA153AF8B8477914371150568D72CD79F6964F43710FB9F22E3164D` |
| `tests/test_campaign_task.py` | `7270BBDE44E260B274BEF35C64EFCC07E445EE50C13A2BA6F23635C73832E9CF` |

### F4 / P1 / FIXED（最终 epoch 冻结版已复验）：旧 resume 决定会越过后来的拒绝暂停或用户暂停

位置：`campaign_task.py:154-178,376-396,474-485`；领域 opt-in 位于 `campaign.py:213-241`。

领域 opt-in 将精确未提交/未调用 intent 的 pending 清除、模式设为 PAUSED、reason 与 last_revision=R-1 同次保存。该原子暂停不能防止协调器随后消费更早的 `_resume_requested`，也不能防止 fresh port read 期间的较晚 pause 被恢复流程覆盖。两条独立 pure-fake 反例均已复现，且已先交 controller 与作者。

**反例 A：拒绝之前的旧 resume 越过拒绝后的新暂停。**

1. 使用 `FakeObjects`：phase SCENARIO、revision=7、tutorial SCENARIO/step `10:1`/gate PASS。
2. 首次 continue 已准备 intent 后，submit 使用 Event 阻塞回执；主线程调用 `c.pause()`、`c.resume()`，两者均 True。此时 resume 只排入请求，尚未由 worker 消费。
3. 放行精确关联回执：requested_seq=ack.command_seq=receipt.command_seq=1，action=continue、attempt_id 空，REJECTED、invoked=False、reason=`scenario_skip_ongui_unavailable`，native revision 保持 7。
4. 此后不再调用 resume；第二次提交若发生则返回 COMPLETED/invokedTrue，保持同 R，以阻止后续观察自然推进掩盖重试。

实际输出：

```text
before_rejection_pause True resume True
without_resume_after_rejection submit_count 2 mode active is_paused False native_revision 7
```

拒绝路径 `479-484` 的 `self.pause()` 不撤销旧的 `_resume_requested`；下一轮 `376-393` 用旧决定读取 R7 并重新开门。没有在新拒绝之后取得一次新的显式继续决定。

**反例 B：fresh read 期间确认的较晚 pause 被旧 resume 覆盖。**

1. 首次 R7 continue 明确 REJECTED+invokedFalse；等任务 PAUSED、pending=None。
2. 用户调用 resume；在 `383` 的强制 observe 中用 Event 阻塞（测试探针确认该 resume 请求已在 `377` 被消费）。
3. 主线程此时再调用 `c.pause()`，返回 True；随后放行 fresh observe，仍给出 R7/PASS。
4. 没有再次调用 resume；恢复流程在 `387-393` 直接恢复领域/port、设 `_paused=False`，在同一轮 `396` 后提交第二个 continue。

实际输出：

```text
later_pause_during_resume_read True
after_later_pause submit_count 2 native_revision 7
```

这违反“确认较晚 pause 后不得新发业务”的排序不变量；仅在 pause 时清 queued bool 不能修复已在途 fresh read 的第二例。需要新 pause/rejection 使更早的 queued/in-flight resume 决定失效，并在读取、持久化与重新打开公开 admission 的边界维护同一决定顺序；不能持 GUI/协调器锁等待桥接或磁盘 I/O。

初审验证是上述两条独立反例，均失败；当时作者 75 项或父领域 28 项绿测未被当作关闭依据。按 controller 要求，初审在该不变量失败后冻结，没有继续扩大或运行普通测试掩盖阻断。以下另列最终冻结版的修复与独立复验，保留原始失败证据。

### F4 最终修复与直接证据

最终冻结身份由本席 readback 确认：

| 路径 | 最终 SHA-256 |
| --- | --- |
| `jiangshen_assistant/campaign.py` | `31C5BEDAF40E74F48A6DB47CC43A65CD754FF78B2336A8C6B6D5540C706F38FF` |
| `jiangshen_assistant/campaign_task.py` | `61B99B2377B66D57D1E1273F8BB35C797D537656B8EAF325E877581FE8571883` |
| `tests/test_campaign.py` | `22DA12AD2CA153AF8B8477914371150568D72CD79F6964F43710FB9F22E3164D` |
| `tests/test_campaign_task.py` | `DED78748B1B6E61BDFA62F8113E6DE18F2C2EA671AFF03349F0124E2D8E3B000` |

静态链路核对：

- `campaign_task.py:158-205` 的新 pause/stop 决定递增 epoch 并撤销更早 queued resume；显式 resume 记录自己的 epoch。后来的拒绝暂停同样使旧决定失效。
- `392-408` 在消费请求后重新读取 port，再检查该 epoch 是否仍是当前决定；`413` 的领域 ACTIVE 保存不持协调器锁；`415-420` 在保存返回后再次检查 epoch，只有当前决定才在短锁内调用公开内存 admission、更新 paused 标志。桥接读取和磁盘 fsync 都不在这段锁内。
- 若较晚 pause/stop 使已经落盘的 ACTIVE 决定失效，port 仍保持 closed，`425-435` 在锁外保存纠正 PAUSED/STOPPED。该路径不调用公开 pause 来撤销更新的 resume，因此纠正写盘期间到来的新决定可以保留并在下一轮处理。
- 领域 `reject_intent(...retry_same_revision=True)` 只对 exact、未提交、非 uncertain、仍占有同一 last_revision reservation 的 intent 归还 R；原子保存 PAUSED/reason/pending=None/last_revision=R-1。仅拥有者明确在任何 I/O/admission 前拒绝或精确 REJECTED+invokedFalse 可作为 proof；一般取消请求、超时、未知/缺失/无关联回执不构成 proof。
- 默认 `retry_same_revision=False` 仍保留原来的“清除精确未提交 intent，但不归还 revision”语义。已提交/uncertain intent、STOPPED/BLOCKED、写盘失败均不获得重新发令权限；checkpoint schema 3 与连续三败规则不变。

本席独立重放原两条脚本反例，输出：

```text
original_A_replay submit_count 1 paused True admission False R 7 PASS
A_after_new_explicit_resume submit_count 2 PASS
original_B_replay submit_count 1 paused True admission False R 7 PASS
B_after_new_explicit_resume submit_count 2 PASS
```

即拒绝前的旧 resume 和 fresh-read 期间的较晚 pause 均不会提前重发；只有新的显式 resume 后才提交一次新 intent，native revision 保持 7。

本轮另独立运行 **17 项定向测试，PASS，2.034s**：

```text
tests.test_campaign.CampaignTests.test_rejected_unsubmitted_intent_needs_matching_id_and_new_observation
tests.test_campaign.CampaignTests.test_proven_guide_rejection_allows_same_revision_only_after_explicit_resume
tests.test_campaign.CampaignTests.test_proven_challenge_rejection_preserves_streak_and_never_clears_submitted_attempt
tests.test_campaign.CampaignTests.test_retry_permission_never_reopens_stopped_run_or_returns_another_reservation
tests.test_campaign.CampaignTests.test_retry_permission_persistence_failure_keeps_pending_and_blocks_admission
tests.test_campaign_task.CoordinatorTests.test_new_rejection_invalidates_resume_queued_before_rejected_ack
tests.test_campaign_task.CoordinatorTests.test_later_pause_during_resume_observation_cannot_be_overwritten
tests.test_campaign_task.CoordinatorTests.test_later_pause_during_resume_checkpoint_keeps_admission_closed
tests.test_campaign_task.CoordinatorTests.test_later_stop_during_resume_checkpoint_remains_terminal
tests.test_campaign_task.CoordinatorTests.test_newer_resume_survives_corrective_pause_checkpoint
tests.test_campaign_task.CoordinatorTests.test_skip_rejection_pauses_same_revision_until_explicit_resume_then_once
tests.test_campaign_task.CoordinatorTests.test_navigation_rejection_pauses_and_resume_rechecks_gate_before_retry
tests.test_campaign_task.CoordinatorTests.test_skip_unknown_timeout_or_missing_receipt_keeps_pending_and_never_retries
tests.test_campaign_task.CoordinatorTests.test_proven_no_submission_can_resume_same_revision_once_with_new_intent
tests.test_campaign_task.CoordinatorTests.test_stop_winning_pre_submission_proof_does_not_reopen_run
tests.test_campaign_task.CoordinatorTests.test_wrong_ack_cannot_clear_or_submit_pending_intent
tests.test_campaign_task.CoordinatorTests.test_seven_results_stop_at_three_consecutive_losses_and_old_win_cannot_reset
```

持久化竞态测试实际先调用 `CampaignStore.save` 落盘 ACTIVE 再阻塞返回，确认后来 pause/stop 在 50ms 内返回、port closed、没有第二次提交，随后盘上状态纠正为 PAUSED/STOPPED；纠正 PAUSED 的 save 阻塞期间再到来的新 resume 也仅成功一次。UNKNOWN/timeout/missing receipt 保留 uncertain pending，错误 ACK 不可清除/提交 intent，stop 不能复活，连续三败无第八场。

**F4 对上述最终冻结组合关闭。** 唯一修改仍为本报告，生产代码/接口未由本席修改；本轮指派范围无剩余 OPEN 项。未运行 full/native/live，真实 Skip/OnGUI 上下文、资源消耗、权威结算与实机连续三败仍 **UNVERIFIED**；假测试不替代 controller 的真实业务验收。

## 此前范围：连接终局 reason 分类与原错保留

本轮只读连接 clean-stop reason 分类/保留，以及 F3 capability 不退化；仅修改本报告。未运行 full/native/live，也未审查另一 owner 正在处理的 current-root 界面选择。

| 路径 | 本轮冻结 SHA-256 |
| --- | --- |
| `jiangshen_assistant/objects.py` | `AE59BA9918300CBBDFF05E9202ECE34D836AF7D0587C68D0898B588CB3252A18` |
| `tests/test_objects.py` | `FF9D33EBB226D985DC7A563CE40FB4380F87D1FBFD7DCE2F1D4D3D8E908C9E86` |

独立核对结果：

- `objects.py:590-595` 从 connect 返回的 raw state/envelope state 及 last_state 提取原始 reason，并通过只读 `connection_status.stop_reason`（`115-127`）公开。
- `596-609` 将 `stop`、`cancelled_before_bootstrap` 或“已取消且无 reason”归为正常 INFO；其他终局保留首次 `_connection_error`，输出 WARNING。无取消且无 reason 的 terminal stop 仍显示 `connection_stopped_before_ready`，不伪装成用户取消。
- 晚到 cancel 不覆盖 `heartbeat_timeout` 等非预期原始 reason；`507-510` 对已分类为 stopped 的终局保留详细 message，避免 finally 改写为泛化停止提示。
- F3 的 exact token/generation/无 owner/未 closing 原子条件（`549-566`）和 claim 后永久作废 startup capability（`221-231`）保持，旧能力不能影响替换连接或其他 owner。

本轮 8 项定向检查 **PASS，0.137s**：

```text
tests.test_objects.ControllerTests.test_unexpected_clean_connect_stop_preserves_native_reason
tests.test_objects.ControllerTests.test_cancel_before_bootstrap_remains_normal_without_connection_error
tests.test_objects.ControllerTests.test_explicit_stop_envelope_remains_normal_and_keeps_stop_reason
tests.test_objects.ControllerTests.test_late_cancel_does_not_replace_native_failure_with_generic_stop
tests.test_objects.ControllerTests.test_bootstrap_not_started_failure_is_distinct_from_user_cancellation
tests.test_objects.ControllerTests.test_stale_connection_capability_cannot_cancel_replacement_attempt
tests.test_objects.ControllerTests.test_business_claim_permanently_retires_startup_cancel_capability
tests.test_campaign_task.ControllerCoordinatorIntegrationTests.test_other_owner_winning_ready_callback_keeps_admission_and_connection
```

另独立分类探针均 PASS：

```text
reason='' cancelled=False -> error='connection_stopped_before_ready'
reason='cancelled_before_bootstrap' cancelled=False -> error=''
reason='stop' cancelled=False -> error=''
```

**对早先“无 session”推断的纠正：** controller 后续关联取证已确认实际存在 3 个加载 session，两帧后以 `campaign_ui_not_unique_CampaignUI` 干净停止，seq=0、无 action；证据为 `data/acceptance/20261031-campaign/connection-failure-correlated.json`。本席引用 controller 提供的运行时事实，没有执行该真实探针。早先“无 session”不能继续作为事实或根因使用；本轮 fake 检查只证明该 reason 能被保留、分类和公开。

本轮没有新增阻断项；唯一写入仍为本报告，生产代码/接口未由本席修改。controller 的 current-root 修复、后续真实连接和业务验收是独立工作；权威 WIN/LOSS、消耗、教学后置条件与实机连续三败仍 **UNVERIFIED**。下方各阶段 hash 只标识各自当时的审查版本，不能当作当前整个仓库的冻结身份。

## 此前范围：首次连接与 token 前清理（F3 在本轮保持关闭）

本轮只读 `objects.py/campaign_task.py/tests/test_objects.py/tests/test_campaign_task.py`；唯一写入仍为本报告。未读取 native 实现、未运行全套或真实游戏。下方连续失败规则结论不受本轮问题影响。

### F3 / P1 / FIXED（最终冻结版已复验）：token 前通用 stop 会关闭后来取得 owner 的其他任务

初审冻结身份：

| 路径 | SHA-256 |
| --- | --- |
| `jiangshen_assistant/objects.py` | `8E86B24499AE6C2DAC4DFB51CEE2770813F28F535702E85C3171C06543A51574` |
| `jiangshen_assistant/campaign_task.py` | `35FF8A507EAE9D8E4C8435A2B332F1703D5DE5C9D9C19FD916F7ABDE2459401E` |
| `tests/test_objects.py` | `CFF0C9674C0FEC0935045E77CE8D347002F0BA98FB6634F4D0B549EE792244DC` |
| `tests/test_campaign_task.py` | `8C309FF80929EFAF525C5530EE5D0477B339F203C438226E60380C2F21898586` |

位置：初审 `campaign_task.py:259,270,494-498` 与 `objects.py:641-646`。

`started_connection` 布尔只证明本任务此前启动过一次连接，不能证明收尾当刻仍拥有该连接或后来的 campaign owner。另一个调用者在 ready 后先取得 owner 时，本任务 `acquire_campaign` 被拒、token 仍为 None；finally 的通用 `objects.stop()` 会关闭对方 owner 的 admission 并设置 stopping。

独立纯 fake 反例使用真实 ObjectController/TaskRunner/Coordinator，仅进程/bridge 为 fake：

```python
case = ControllerCoordinatorIntegrationTests()
case.setUp()
other = []
original = case.objects.acquire_campaign
def acquire_after_other_owner(run_id):
    other.append(original('other-run'))
    return original(run_id)
case.objects.acquire_campaign = acquire_after_other_owner
case.coordinator.start()
eventually(lambda: not case.coordinator.is_active)
case.runner.join(1)
state = case.objects.campaign_snapshot(other[0])
```

输出：

```text
coordinator_reason 对象连接尚未就绪或已被占用。
other_owner_retained True other_owner_stopping True client_stop_calls 0
```

这不是“原先已有 busy connect、本任务 connect 被拒”的路径；本任务确实发起成功，但随后失去控制权。需要与连接 attempt/identity/generation 绑定的原子 conditional cancel，并确保取得其他 owner 后取消能力永久失效。已先通知 controller，并把精确反例交作者；原全套绿测不能关闭该问题。

最终修复的 additive 接口是 `begin_campaign_connection()` / `cancel_campaign_connection(token)`：

- `objects.py:544-549` 在同一个 owner RLock 中发起 connect 并捕获 opaque exact token；现有 GUI `connect() -> bool` 接口保留。
- `551-561` 在同一锁内验证 token 对象身份、generation、无 campaign owner、未 closing，再允许取消；None、旧 token 或替换后的 attempt 均不具备取消权限。
- `219-229` 中任何成功的 `acquire_campaign` 都永久废弃 startup capability；其他 owner 后续 release 也不会让旧能力复活。
- `campaign_task.py:259,265-270,494-497` 保存自己的 connection token，并只使用 conditional cancel；不再以“曾启动成功”布尔调用通用 stop。

本席独立重放原始精确反例，结果：

```text
original_F3_replay other_owner_stopping False client_stop_calls 0 public_submit enter PASS
```

同一另 owner 的公开 submit 确实成功，不只检查物理 stop 计数。新冻结版 3 个关键回归 **PASS，0.062s**：

```text
tests.test_campaign_task.ControllerCoordinatorIntegrationTests.test_other_owner_winning_ready_callback_keeps_admission_and_connection
tests.test_objects.ControllerTests.test_stale_connection_capability_cannot_cancel_replacement_attempt
tests.test_objects.ControllerTests.test_business_claim_permanently_retires_startup_cancel_capability
```

这些检查覆盖他 owner 抢先 claim 后发令门不受影响、旧 token 不停止新 attempt、当前 token 只取消一次、owner claim 后即使 release 旧 capability 仍无效，以及 cancel(None) 无副作用。另独立探针确认 `connect_timeout` 的 NaN/Inf/0/-1/True 均被拒绝。**F3 只对下面最终冻结身份关闭。**

| 路径 | 最终 SHA-256 |
| --- | --- |
| `jiangshen_assistant/objects.py` | `CC47F5AE8F1C3C11F43874E23078F29DE82F3C54ABFDE78B0477B901F47C2383` |
| `jiangshen_assistant/campaign_task.py` | `983E5F4725F4E1FF985B259D6E37154A1798247DF2A2549DC1D4C7171CCB4D0B` |
| `tests/test_objects.py` | `4523C602A6C0315B025DDC054AA21927BC53565D520B0494B3BBF35B728E9BD4` |
| `tests/test_campaign_task.py` | `C3F4CEC2116038257C60EDCD58ECEC6488FBBA911A1F16BDEEF91E4427C01ADF` |

### 本轮其余独立边界

除 F3 外，初审未发现其他阻断：

- `campaign_task.py:37-47,264-283` 的 connect 预算默认 30 秒、可注入单调时钟，拒 bool/非有限/非正参数；ready 但对象 worker busy 时等待释放，不提前拿 token。
- error/unknown/disconnected 且 worker idle 时读取原始 connection error 并快速失败，避免统一等待 generic timeout；identity/factory/bridge_connect 阶段和拒绝原因可以定位失败发生在哪里。
- `objects.py:106-118` 的 `connection_status` 仅短锁读取内存字段，提供 state/phase/started_at/generation/error/refusal/owner/closing 等信息；不等待身份检查或桥接 I/O。
- identity/factory 晚返回使用 generation/closing gate，超时或 stop 后不能再通过旧 factory 启动 bridge；已存在的 busy connect 被拒时，本任务不将其当作自己启动的连接清理。F3 是后续所有权发生变化的独立缺口。

在作者修改过程中执行了 10 项原连接定向回归，**PASS，0.597s**；执行前后 candidate hash 一致（objects `A379428E75F1A060E1AD8952555B1CE09F31B8ECE51B0982051BE91295932D7A`、task `983E5F4725F4E1FF985B259D6E37154A1798247DF2A2549DC1D4C7171CCB4D0B`、test_objects `645A0776F795A6AF7D2C4E7D067B173D3B62A7F5C91D5EA3D87B921A6FB9CC8F`、test_task `C3F4CEC2116038257C60EDCD58ECEC6488FBBA911A1F16BDEEF91E4427C01ADF`）。这是候选版本回归证据，**不是 F3 关闭依据**：

```text
tests.test_campaign_task.ControllerCoordinatorIntegrationTests.test_first_start_connects_once_and_runs_through_real_owner_port
tests.test_campaign_task.ControllerCoordinatorIntegrationTests.test_connect_timeout_cancels_unowned_connect_before_factory_returns
tests.test_campaign_task.ControllerCoordinatorIntegrationTests.test_real_owner_connection_error_is_reported_without_waiting_generic_timeout
tests.test_campaign_task.ControllerCoordinatorIntegrationTests.test_failure_to_acquire_already_connecting_owner_does_not_stop_other_connection
tests.test_campaign_task.ControllerCoordinatorIntegrationTests.test_stop_before_owner_cancels_connect_without_later_bridge_start
tests.test_campaign_task.ControllerCoordinatorIntegrationTests.test_ready_callback_before_connect_returns_does_not_acquire_busy_owner
tests.test_campaign_task.ControllerCoordinatorIntegrationTests.test_identity_preflight_timeout_preserves_phase_and_cannot_reach_factory
tests.test_campaign_task.ControllerCoordinatorIntegrationTests.test_unbound_first_connect_refusal_is_observable_without_starting_client
tests.test_objects.ControllerTests.test_live_client_connect_refusal_keeps_state_and_exposes_exact_reason
tests.test_objects.ControllerTests.test_cancel_between_identity_and_factory_return_settles_disconnected
```

该阶段独立初审和冻结版窄复验完成，无剩余 OPEN 项；唯一修改是本报告，生产代码/接口未由本席修改。当时“无 session”仅为未核定推断，后续已被 controller 的 3-session 关联证据纠正，见本报告最新范围。这些 fake 测试不构成真实现象的根因证据，也不证明真实 native 连接或业务运行成功。真实 identity/bridge 阻塞、进程连接、GUI 启动链和 native session 落盘行为仍归 controller 的运行时取证与验收。

## 当前结论：连续失败三次停止，WIN 清零

用户已明确改为**连续**失败三次停止。本节是当前规则的结论；下方累计失败/schema 2 的审查仅保留为历史，不作为新规则验收依据。

- `campaign.py:260-264` 先判 exact duplicate 并直接返回，旧 WIN 重复不会执行清零；只有首次消费、可信的当前结果进入 `274`：LOSS 将 streak 加一，WIN 归零。UNKNOWN/未确认结果在此前保留 pending，不增加或清零 streak。
- 第三次连续 LOSS 在 `277-283` 与 consumed、清除 pending 同一次持久化提交进入 LOSS_LIMIT/reason `three_consecutive_losses`；既有准入门阻止后续发起。持久化失败仍 poison 后续准入。
- `campaign_store.py:51,65-66` 使用 checkpoint **schema 3**；`103-114` 按 consumed 的尾部连续 LOSS 重新核验 `losses`，拒绝三连败之后仍追加结果、loss limit 与 mode 不一致、三连败仍有 pending 等不一致历史。
- **兼容性限制：schema 1/2 账本明确 fail closed，不自动迁移，也不把原累计值重新解释为连续值。** 旧账本会进入待审查路径；本轮不授权删除或重写旧记录。
- 当前字段仍名为 `losses`，含义改为当前连续失败数；checkpoint 版本用于区分语义。typed 业务 API、对象所有权、关闭顺序未扩展。`objects.py` 与 GUI 生命周期测试文件身份保持原值。
- `app.py` 的总览、征战说明、计数标题、开始日志、终止状态/原因均改为连续失败；成功说明明确“确认胜利后连续失败次数归零”。`core.py` 任务目录同样说明连续三败与胜利清零。

关键反例在新冻结版通过：

| 输入/边界 | 当前结果 |
| --- | --- |
| `LOSS, WIN, LOSS, WIN, LOSS` | streak=1，保持 ACTIVE，不因历史三次 LOSS 停止。 |
| 第六场在途时重复第四场已消费 WIN | consumed 不增加，保留第六场 pending；第五场产生的 streak=1 不清零。 |
| 再追加 `LOSS, LOSS` | 第七次结算后 streak=3/LOSS_LIMIT，账本七条结果，start 总数为 7，无第八次提交。 |
| UNKNOWN、暂停、重启 | 不伪造胜负，不自动清零；pending 与当前 streak 保留。 |
| 原 F1 旧冲突与 F2 晚来清理 | 在新版本关键回归仍 PASS；未因新规则重开旧冲突或放宽未决/错误恢复门。 |

本轮独立执行 **14 项定向测试，PASS，1.301s**：

```text
python -B -m unittest
  tests.test_campaign.CampaignTests.test_only_three_consecutive_losses_stop
  tests.test_campaign.CampaignTests.test_duplicate_old_win_does_not_reset_later_streak
  tests.test_campaign.CampaignTests.test_unknown_does_not_reset_streak_and_restart_preserves_it
  tests.test_campaign.CampaignTests.test_store_strict_and_existing_run_never_overwritten
  tests.test_campaign.CampaignTests.test_store_rejects_history_that_continues_after_loss_limit
  tests.test_campaign.CampaignTests.test_third_loss_must_persist_before_any_further_admission
  tests.test_campaign.CampaignTests.test_result_persistence_failure_does_not_open_admission
  tests.test_campaign_task.CoordinatorTests.test_seven_results_stop_at_three_consecutive_losses_and_old_win_cannot_reset
  tests.test_campaign_task.CoordinatorTests.test_old_conflicting_settlement_blocks_during_different_pending_attempt
  tests.test_campaign_task.CoordinatorTests.test_old_exact_duplicate_is_idempotent_while_next_attempt_is_pending
  tests.test_campaign_task.CoordinatorTests.test_late_cleanup_rechecks_public_boundaries_without_io_or_losing_ledger
  tests.test_campaign_task.CoordinatorTests.test_late_clean_disconnect_preserves_unresolved_or_error_recovery
  tests.test_app_lifecycle.CampaignUiSafetyTests.test_f8_routes_to_campaign_without_tearing_down_observation
  tests.test_app_lifecycle.CampaignUiSafetyTests.test_close_waits_for_campaign_before_closing_object_observer
```

另做独立纯内存短序列模型校验（未写生产代码或新测试文件）：

```text
schema3_binary_histories 255 accepted 191 rejected 64 PASS
domain_seven_result_sequences 128 old_win_duplicates_preserve_state PASS
```

枚举 0–7 条 WIN/LOSS 的全部 255 个历史；允许尚未触发三连败的历史，以及恰在最后一条首次达到三连败的历史，拒绝所有触发后继续追加的历史。对全部 128 个七结果序列逐步运行领域/encode/decode，并在每次结果之后重放任意已消费 WIN，streak 与状态均符合独立模型，三连败后准入拒绝。

### 当前规则冻结身份

| 路径 | SHA-256 |
| --- | --- |
| `jiangshen_assistant/campaign.py` | `8F75155EE968F7511DA42FFC440787F8EEF79309F63820ECD0CB2796D579010C` |
| `jiangshen_assistant/campaign_store.py` | `8893F919ED2DB150B720462CFCD72847B0C9C121300D275E12F1267A8AB68AAA` |
| `jiangshen_assistant/campaign_task.py` | `16E1A45EC572E2C1B0642DC015317680455E479AD9E36DFD45CFD04A39F1EBD1` |
| `jiangshen_assistant/core.py` | `171C89AC4905A0BAF994118EDB5A7CC3110AA13C3569EDC3E222CB21A0047C2F` |
| `jiangshen_assistant/objects.py` | `0526A1260471C65F8969A7498CA5A007D5C8C8CC91090C76EF18DB4BE428E8DB` |
| `jiangshen_assistant/app.py` | `BB6ACAACD930485C78AD86071712F9AD78AF4CDF470889F82B36011704DCC2A1` |
| `tests/test_campaign.py` | `1BCB984273EC35A274F89C5ADBAD0C3AFF0F48A0EC1186CDFAB318643F491017` |
| `tests/test_campaign_task.py` | `3F7776AED46802FA1EB3982281FC5FD17B8D7DC41B192F02A3E0234BF2FF7C2B` |
| `tests/test_app_lifecycle.py` | `08DC458AC68BF1D173678BE6D32DC4B1C4710DE3F0B72EAB1BE0FF1B904E0EDF` |
| `tests/test_core.py` | `BCD94A45B3CF657E92B51943F96306BFAB4F7A9569683597F67208F14395847D` |

本席仍只修改本报告，生产代码和接口未由本席修改。本轮未重读 native 实现、未验证正在其他 owner 范围内处理的 BR3 组合 guard 路径，也未沿用下方历史 bridge hash 作为当前 native 通过证据。真实权威 WIN/LOSS、消耗、教学后置条件和实机连续三败验收保持 **UNVERIFIED**；完整构建、整套测试和业务验收由 controller 调度。本轮指派的连续规则窄审查无剩余工作。

## 历史记录：累计失败规则/schema 2（已被上述新规则取代）

以下保留初审 finding、失败反例及当时冻结身份。历史 PASS、累计三败/WIN 不清零描述及“最终”字样只对应当时版本，不能外推为当前连续失败规则的结论。

## 授权与覆盖

- 只读生产源码；本席唯一写入 `docs/campaign-review-workflow.md`。未修改作者代码，未读取游戏实现，未连接/注入游戏进程，未操作游戏 UI、网络或业务。
- 审查 `campaign.py`、`campaign_store.py`、`campaign_task.py`、`objects.py`、`core.py`、`app.py` 及对应测试。`bridge_client.py` 仅核对当前类型化 port/解析契约；native 与桥接实现的批量审查由其他 owner 负责。
- `docs/campaign-integration-plan.md` 作为设计背景，具体安全准则来自本次 brief：累计三个已确认 LOSS 停止，WIN 不清零；未知暂停；已审核教学正常推进。
- 本目录没有 Git 元数据（`git rev-parse HEAD` 返回 not a git repository），以下以 SHA-256 标识初审与最终冻结版本。初始 findings 与中间失败反例保留，关闭结论仅适用于本文最后列出的冻结身份。

## Findings

### F1 / P1 / FIXED（冻结版已复验）：新 attempt 在途会吞掉旧已消费结果的矛盾结算

位置：初审 `campaign_task.py:268-306`，尤其 `270-288` 与 `298-306` 的互斥 `if/elif`。

当 B challenge pending 时，代码先进入 `if p and p.kind == 'challenge'`，只处理与 B 匹配的 `attempt_id`。旧 A 的 SETTLED 即使与已消费记录具有相同 attempt/report、却把 LOSS 改为 WIN，也不会进入后面的 consumed 冲突检查。B 随后正常结算后仍可继续提交 C。领域层的冲突保护本身存在，但协调器未把这类事件送进去。

纯 fake 独立反例（未修改生产文件）：

1. `FakeObjects(['LOSS'])` 启动 coordinator；在第一次 SETTLED 时保存 A 的 attempt 副本。
2. 等两次 `start`：A 的 LOSS 已消费，B 已在途；保存 B 的 WAITING attempt。
3. 把观察 attempt 换成 A 副本，仅将 outcome 改为 WIN，保持相同 attempt/report；等待至少三次观察。
4. 恢复 B 并给出可信 SETTLED LOSS、独立 report `999`，phase RESULT、revision 加一。

实际输出：

```text
after_conflicting_old_result active  1
new_start_count 3 mode active losses 2
```

这不导致同一结果计两次，但违反“冲突应封闭后续发令且后续结算不得重开”的规则。已先通知 controller，并把最小反例直接交给协调器 owner。

复查要求：任何与 consumed 的 attempt/report 相交的 SETTLED 应先经过领域幂等/冲突判定；B 的 pending 和账本保留；即使随后收到 B 的可信结果，也不能再发 C。同时保留 exact duplicate 不增计、不破坏 B 的反例。

复查结果：`campaign_task.py` SHA-256 `2235641EB1BD24F0F2B794C38117A4EBB3BC8D2182DACE89B0884325B43FFA17` 的 `293-298` 已先检查任何 consumed 交集，再在 `299` 进入当前 pending 分支。独立运行两项回归均 PASS：旧 A 冲突立即 BLOCKED、保留 B；只读归账 B 后仍保留 conflicting_result，start 总数仍为 2。旧 A 精确重复不增计，B 可正常归账。

最终复核：`AE66F2...D4E2C1` 的对应检查位于 `320-325`，原冲突与 exact duplicate 两项回归再次 PASS；F1 保持关闭。

### F2 / P2 / FIXED（最终冻结版已复验）：已完成恢复任务仍占用恢复入口

位置：初审 `campaign_task.py:74-76,101-105,138-140,418-421`。

`_recovery_path` 在构造或收尾时被赋值，但成功恢复后从未清空。恢复任务最终已 STOPPED 且 pending=None 时，`is_paused` 仍返回 True，`start()` 仍返回 False。GUI 因而只显示“继续”旧的 terminal run，不能显式开始新一轮；重启助手才会重新判定 terminal 账本。

纯 fake 反例：磁盘创建 PAUSED、无 pending 的 run；`c.resume()`；正常结算一场 WIN；`c.stop()` 并等 inactive。实际输出：

```text
resume_recovered True
terminal stopped pending None is_paused True new_run_start False
```

复查要求：只在 terminal、无 pending 且持久化无错误时释放恢复入口；PAUSED/BLOCKED/未决或持久化异常必须继续保留。已通知 controller。

中间修复：冻结版 `2235641...43FFA17` 在 `448-451` 仅当 terminal、无 pending、无 persistence_error，且当刻 bridge 为 disconnected 时清除恢复引用。正常快速清理的新增回归 PASS。

**中间版仍失败的异步反例：** coordinator 的 `440-442` 最多等清理 6 秒，但当前 `BridgeClient.stop()` 请求期限为 12 秒。若清理在第 6 秒后才确认 disconnected，finally 当刻仍保留 `_recovery_path`；稍后的真实清理终态没有再次检查该引用，因而依旧阻止新一轮。独立 fake（6.5s）先在 `release_campaign` 设置 stopping/is_busy=True，等 coordinator 退出后再设置 disconnected/is_busy=False，得到：

```text
at_cleanup_wait_cutoff stopped pending True
after_late_cleanup bridge disconnected is_paused True new_run_start False
```

第一行的 pending 是 cleanup_state，不是业务 pending；业务 pending 为 None。该反例已交 owner/controller，初次修复未据此误标通过。

最终修复 `AE66F2...D4E2C1` 的 `92-113` 新增 `_refresh_cleanup_locked`：只读取对象层公开内存属性；inactive、cleanup pending，且当前 disconnected/not busy 才更新 cleanup confirmed；仅 terminal、无业务 pending、无 persistence_error、无 cleanup_error 时清恢复引用。`snapshot/is_paused/start/resume` 均调用此刷新。未决、BLOCKED/PAUSED 或错误记录不释放恢复入口；unknown 仍保持待确认。

最终复验：新增公开边界/保护回归 PASS。另重新执行原六秒真实等待反例（6.4s），晚来清理后首先调用 `resume()`，输出：

```text
at_cleanup_wait_cutoff stopped pending True
resume_after_late_cleanup False cleanup confirmed is_paused False
explicit_new_run True
```

`resume=False` 表示未重新运行已结束的旧任务；只有显式 start 创建新 run。刷新前后旧账本 bytes、命令数和观察数保持一致，未做 I/O；断开先到但对象 worker 仍 busy 的情况继续保留 pending。**F2 仅对最终冻结 hash 关闭。**

### C1 / CLOSED（最终 parser 接口已核对）：HINT 分类在 fake 与实际 parser 中不同

初审 `campaign_task.py:177-184` 和 `tests/test_campaign_task.py:385-392` 接受 HINT；初审 `bridge_client.py:181-184` 仅接受 NONE/SCENARIO。若真实 native 发 HINT，解析会进入 BridgeUnknown；若约定将非强制提示规范化为 NONE，需记录并测试该契约。已交桥接 owner 对齐，不在本席批量审查范围。

Controller 另通知 native 的 `WAITING_TEXT/WAITING_MOVEMENT/WAITING_NEXT` 正在交协调器实现只读等待；这些移动中的接口须在双方冻结后复查。

中间版曾因尚未收到最终 bridge hash 而保持 C1 开放。最终 `bridge_client.py` SHA-256 `3AB028308EDC31BCCFA7DACEEDB6E509FBD086B4AD6D426522DE21140F20391E` 已与冻结 `campaign-validation.json` 中 source_hashes 对应项一致；Python 协议为 3。`181-188` 真实 parser 接受 HINT 的限定为 FORMATION/new-skill/空 gate，genuine NONE 必须空 step/空 gate。两项 parser-only 测试及七组 NONE/HINT 正负探针 PASS。

最终 coordinator 的四种 SCENARIO waiting 状态（含 WAITING_UI）再次运行 `test_scenario_waits_until_ready_and_confirms_step_before_next_continue`：PASS；等待时继续观察、不暂停为未知、不提交 continue，同一步仅发一次，看到真实步骤转换才记完成。未知分类或 gate 仍进入领域暂停。此结论只关闭本席的 Python parser/coordinator 契约问题；native 实现审查继续归另席，不据此宣称真实教学或游戏结算正确。

## 已核对的安全链路

| 准则 | 初审结果与证据 |
| --- | --- |
| 累计三个已确认 LOSS，WIN 不清零 | PASS；`campaign.py:252-283` 在消费结果同一次 save 中保存累计值、consumed、清除 pending；第三次进入 LOSS_LIMIT。`prepare:178-179` 拒绝后续准入。 |
| 账本先于 challenge I/O | PASS；`campaign_task.py:371-378` 先 prepare/save，intent UUID 同 planned native attempt UUID，再 submit；`campaign.py:127-135` 保存失败 poison 后续准入。 |
| 未知 ACK 不重发、不清 pending | PASS（初审）；异常进入 mark_unknown，保留 intent/attempt；相关 reject 仅用于 owner gate 证明未准入、显式未发布返回或精确 REJECTED+invoked=False。 |
| 可信 SETTLED 与上下文 | PASS（初审正常路径）；`objects.py:177-219` 将 PID/creation/nonce/generation/character_id 固定进 token context；`campaign_task.py:270-285` 检查 attempt、stage、report、evidence、seq；typed parser 检查 envelope。F1 单列旧冲突分流缺口。 |
| stop/pause 与新提交 gate | PASS；`objects.py:249-297` 在锁内线性化准入，I/O 不持状态锁；已准入请求可完成，确认 gate 关闭后后续 submit 拒绝。GUI 调 coordinator，不持锁等待桥接 I/O。 |
| 暂停继续心跳/结果 | PASS；coordinator 不使用会冻结观察的 checkpoint，`objects.py:365-402` 独立维护 heartbeat/只读观察；结算可以在暂停时落盘。 |
| restart pending 不自动攻击/不跨 context 迁移 | PASS；`campaign.py:114-121` 不自动 reopen；初审 `campaign_task.py:241-248` 比较完整 context，pending 路径只读；F2 terminal 后恢复入口缺口已在最终冻结版复验关闭。 |
| close 先业务收尾后 objects.close | PASS；`app.py:898-925` 等 coordinator shutdown，再关闭对象层；pending stop 超时保留 unknown。 |
| GUI 无 owner 旁路且不把 unknown 当 clean | PASS；手工操作在 objects 再次检查 owner；`app.py:457-480` 将 cleanup unknown/error 显示为待确认。未把战斗消耗宣称成可回滚或验证恢复。 |
| 已审核/未知教学 | PASS（本席 Python 范围）；已审核 SCENARIO 可提交一次并看步骤变化；未知暂停；最终 HINT/parser 与四种 WAITING 契约已复验。 |

## 本席验证

运行 12 个有针对性的纯 Python/fake 测试：**PASS，0.298s**。

```text
python -B -m unittest
  tests.test_campaign.CampaignTests.test_accumulated_losses_stop_before_fourth
  tests.test_campaign.CampaignTests.test_exact_duplicate_and_conflicting_result
  tests.test_campaign.CampaignTests.test_conflict_during_later_pending_attempt_remains_blocked_after_reconciliation
  tests.test_campaign.CampaignTests.test_stale_context_unmatched_and_unconfirmed_preserve_pending
  tests.test_campaign.CampaignTests.test_restart_keeps_pending_and_never_reopens_admission
  tests.test_campaign.CampaignTests.test_third_loss_must_persist_before_any_further_admission
  tests.test_campaign.CampaignTests.test_result_persistence_failure_does_not_open_admission
  tests.test_campaign.CampaignTests.test_atomic_replace_failure_preserves_old_file
  tests.test_app_lifecycle.CampaignUiSafetyTests.test_f8_routes_to_campaign_without_tearing_down_observation
  tests.test_app_lifecycle.CampaignUiSafetyTests.test_close_waits_for_campaign_before_closing_object_observer
  tests.test_app_lifecycle.CampaignUiSafetyTests.test_cleanup_owner_blocks_manual_work_even_after_runner_is_idle
  tests.test_app_lifecycle.CampaignUiSafetyTests.test_recovery_record_exposes_resume_instead_of_new_run
```

上方多行是单条 unittest 命令的参数列表。另独立执行 F1、F2 的内存 fake 反例并得到上述输出。未运行完整套件、native 构建、CI 或 live 验收；这些由 controller 调度。

冻结协调器复查：以下 6 个定向测试 **PASS，1.299s**（测试文件 SHA-256 `5860490A83929B60C7362CA5B983AAE8343BDDCCA9AE6EDF5096F74ED04B42D9`）：

```text
tests.test_campaign_task.CoordinatorTests.test_old_conflicting_settlement_blocks_during_different_pending_attempt
tests.test_campaign_task.CoordinatorTests.test_old_exact_duplicate_is_idempotent_while_next_attempt_is_pending
tests.test_campaign_task.CoordinatorTests.test_successful_recovery_terminal_clear_allows_explicit_new_run
tests.test_campaign_task.CoordinatorTests.test_scenario_waits_until_ready_and_confirms_step_before_next_continue
tests.test_campaign_task.CoordinatorTests.test_settlement_proof_rejects_missing_fields_and_invalid_types
tests.test_campaign_task.CoordinatorTests.test_malformed_settlement_through_coordinator_keeps_pending_without_loss
```

新增 source-proof helper 对 attempt 格式、正整数 seq/章节/敌军、正十进制 report、权威 evidence 和已知 WIN/LOSS 作保守校验；helper 缺字段/错误类型及 coordinator 六种畸形输入均覆盖，未确认结果不增失败、pending 保留、无新 start。

另独立执行对象层与 runner 的 4 个定向测试 **PASS，0.120s**：

```text
tests.test_objects.ControllerTests.test_campaign_pause_gate_returns_before_admitted_io_and_blocks_next
tests.test_objects.ControllerTests.test_campaign_pause_still_observes_and_heartbeats_without_gui_events_consumption
tests.test_objects.ControllerTests.test_campaign_subscription_is_independent_bounded_and_identity_checked
tests.test_core.TaskRunnerTests.test_reconciler_observes_paused_and_cancelled_flags_without_blocking
```

GUI 纯 mock 探针（不创建 Tk 窗口）确认：STOPPED + cleanup unknown 显示“接口清理待确认”，详情明确“清理尚未确认”；confirmed 对照显示“已停止”。上述初审和中间复查共执行 22 个命名定向测试；中间版 F2 晚来清理反例当时仍失败，未因绿测误标通过。

最终窄复查另外执行以下 6 项，**PASS，0.953s**；保留原 F1/F2 反例，不重跑完整套件：

```text
tests.test_campaign_task.CoordinatorTests.test_old_conflicting_settlement_blocks_during_different_pending_attempt
tests.test_campaign_task.CoordinatorTests.test_old_exact_duplicate_is_idempotent_while_next_attempt_is_pending
tests.test_campaign_task.CoordinatorTests.test_successful_recovery_terminal_clear_allows_explicit_new_run
tests.test_campaign_task.CoordinatorTests.test_late_cleanup_rechecks_public_boundaries_without_io_or_losing_ledger
tests.test_campaign_task.CoordinatorTests.test_late_clean_disconnect_preserves_unresolved_or_error_recovery
tests.test_campaign_task.CoordinatorTests.test_scenario_waits_until_ready_and_confirms_step_before_next_continue
```

最终 parser-only 两项，**PASS，0.016s**：

```text
tests.test_bridge_client.CampaignBridgeTests.test_real_parser_allows_only_known_nonblocking_hint_and_dialogue_waits
tests.test_bridge_client.CampaignBridgeTests.test_provisional_or_inconsistent_result_cannot_claim_settled
```

额外独立探针：genuine NONE 空字段接受，非空 step/gate 拒绝；HINT 仅 FORMATION/new-skill/空 gate 接受，MAIN、其他 step、非空 gate 拒绝。F2 原真实六秒 cutoff + `resume` 首个公开刷新入口重放 PASS；刷新没有改变账本或调用 bridge。完整 Python、native 构建和 live 验收仍由 controller 调度，本席未执行或代签。

## 初审源码身份（SHA-256）

| 路径 | SHA-256 |
| --- | --- |
| `jiangshen_assistant/campaign.py` | `F0138B1ED86A97951B965E4F8397FAAF1C32FA37FA6F122887B9850DD816D30E` |
| `jiangshen_assistant/campaign_store.py` | `748C0FCE9C7EE46A8E4A26742DC02F657176B38A37100816F985BEBCFC39E60D` |
| `jiangshen_assistant/campaign_task.py` | `DC623C90E76397588B48C2AB6E2BF116A43786C1E653A32BFEB5E01A820B90C9` |
| `jiangshen_assistant/objects.py` | `0526A1260471C65F8969A7498CA5A007D5C8C8CC91090C76EF18DB4BE428E8DB` |
| `jiangshen_assistant/core.py` | `3B85EEA940C60A9F830CCED571EF40006FA4872982F6808EAAAAC6F8ABCBC6B1` |
| `jiangshen_assistant/app.py` | `D5565399DA14CECD63BC671956B6BF0BD8B8F2E167C1E4ADE3DC8F0D6BCBCB62` |
| `jiangshen_assistant/bridge_client.py`（仅接口） | `D9BD43CF38F553E4A57DB85FB26E20D796B28765BF2B864EBE155F2D1C4E1D11` |
| `tests/test_campaign.py` | `A5B48BC1D6DC3D4067AB3935415D156C2D4A71EA91B36EB0FA604C706AC2CEB9` |
| `tests/test_campaign_task.py` | `92D7DC61F6F93580C9973F777C04BF9562BACB150F33D2DD483D5388A8F5F8D0` |
| `tests/test_objects.py` | `82499768F5F1CDECFA7579A9EC5F6AAFEAEC873AE8C20FD0AF913BD8A7F08FDC` |
| `tests/test_core.py` | `BCD94A45B3CF657E92B51943F96306BFAB4F7A9569683597F67208F14395847D` |
| `tests/test_app_lifecycle.py` | `08DC458AC68BF1D173678BE6D32DC4B1C4710DE3F0B72EAB1BE0FF1B904E0EDF` |

## 历史最终冻结身份（累计规则，已失效）

| 路径 | 最终 SHA-256 |
| --- | --- |
| `jiangshen_assistant/campaign_task.py` | `AE66F2D70DB69C46ADB576A5F65426C0A4D775FC01F008228FA008F4F4D4E2C1` |
| `tests/test_campaign_task.py` | `C95E953737661C83BF28A968634F0E3E8294B660E2637E4E3E8EF0E963186285` |
| `jiangshen_assistant/bridge_client.py`（仅接口） | `3AB028308EDC31BCCFA7DACEEDB6E509FBD086B4AD6D426522DE21140F20391E` |
| `tests/test_bridge_client.py`（仅 parser 用例） | `FEBC6909F008F5FA6B542F8091FEA18DFBAA3342DA9B91334BA9D9DA9641E5D9` |
| `native/unity_bridge/build/campaign-validation.json`（仅身份 readback） | `432B7990EFB29F587454A6C1ED60549C076DBB373993029FF0B0E79C3B0DC7EF` |

`campaign.py/campaign_store.py/objects.py/core.py/app.py/tests/test_app_lifecycle.py` 最终 readback hash 与初审表一致。离线构建身份为 `d6ed2c75d294ea35e13e0e7ef986f81ce5c268a5fb6485b85bf0945f676b33e7`；本席只核对身份文件，未重新构建或批量审查 native。

## 未完成工作与验收界限

- 本席指派的只读审查、命名报告和窄复验已完成，F1/F2/C1 无剩余 OPEN 项；生产源码与接口未由本席修改。
- native 实现最终审查归其他 owner；真实 authority receipt 生命周期不在本席验证范围。
- 假测试证明工作流规则和反例，不证明真实游戏发起、正常资源消耗、教学后置条件、权威 WIN/LOSS 或实机三败停止。真实业务验收保持 **UNVERIFIED**。
