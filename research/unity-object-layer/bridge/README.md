# Unity 主线程直接调用验证

日期：2026-10-31（Asia/Shanghai）。**已在真实游戏中取得当前 MainUI 实例，并在 Unity 主线程直接调用游戏已有的 `Disable()` / `Enable()`，完成一次临时禁用与还原。**

01:53:17 调用 Disable；01:53:23 调用 Enable。操作由方法调用完成，没有发送鼠标点击或按键。主控直接观察到主菜单图标变暗，随后恢复正常显示。

## 已通过的判据

| 判据 | 结果 | 证据与边界 |
| --- | --- | --- |
| 原生入口一次性执行 | PASS | PID 12844，窗口线程及回调 TID 均为 21756；Mono current/main 匹配，calls=1 |
| 实际 Unity 主线程回调 | PASS | 辅助组件由 Unity 执行 Update，连续不同 frame；native TID 21756，managed TID 1；不是手动调用 Update |
| 当前界面实例定位 | PASS | 唯一精确类型 MainUI，instance ID 11752，当前根 Capital，scene 0 |
| 调用真实游戏方法 | PASS | 反射绑定固定版本 __BaseUI 的公开虚方法，无 MainUI/BaseUI 覆盖；方法体分别只写 enable=false/true |
| 禁用状态持续与恢复 | PASS | 同一实例 true → false → true；禁用跨后续帧确认，实际执行 Enable 后恢复 |
| 可见界面结果 | PASS（局部） | 01:53:17.516 观察主菜单图标变暗；01:53:31.720 观察恢复。不代表每个原始 GUI 控件都经过点击可用性测试 |
| 监测数值未变化 | PASS（白名单） | 14 个字段，368 次观测，每个字段最终和历次值都与操作前相同 |
| 活动探针清理 | PASS | hook_removed=1、callback_finished=1；组件收到 OnDestroy；控制器进程均已退出 |
| 安装文件身份 | PASS（检查范围） | 8 个关键客户端/运行时文件 SHA256 与基线一致 |
| 完整模块卸载 | 等待正常退出游戏 | 3 次研究启动留下的 3 个原生模块与 3 个托管程序集仍在当前进程，随游戏正常退出释放 |
| 任意业务按钮、领取、战斗、推图 | 未验证 | 本轮只执行下述一类纯界面操作，没有开放通用调用接口 |

原始会话：[fa0435ba7b134503a973d4a9834c7a70](sessions/fa0435ba7b134503a973d4a9834c7a70/acceptance.json)。

## 实际执行路径

```mermaid
flowchart LR
    A[外部 x86 控制器] --> B[指定窗口线程的一次性钩子]
    B --> C[固定哈希的托管探针 Bootstrap]
    C --> D[Unity 自己执行 Update 并验证线程和帧]
    D --> E[查找唯一当前 MainUI]
    E --> F[独立命令 disable-restore]
    F --> G[调用 Disable 并读回 false]
    G --> H[保持约 6 秒]
    H --> I[调用 Enable 并读回 true]
    I --> J[销毁辅助组件]
```

窗口线程和 Mono main 一致只作为受限启动条件，不能事先保证任意 Unity API 都安全。实际组件创建成功后，探针等待至少两帧 Unity-generated Update，再查找对象、读取状态；操作又要求单独的明确命令。线程、对象、场景、弹窗或加载条件不满足时拒绝调用。

本轮调用的是客户端已有的方法，不是把目标字段直接写成预期值。完整 IL 已独立检查，每个方法只有一次对同一私有 Boolean 的赋值；没有调用其他代码或写玩家资源。详见[候选方法证据](overlay-resourcefree-candidate.md)。

## 三次启动与动作范围

| 会话 | 结果 | 实际游戏动作 |
| --- | --- | --- |
| [64731d2…](sessions/64731d2da0864727a8d6cf0ce6eecd0c/probe.events.jsonl) | 找到 MainUI；初版检查把常驻 HUD 当成弹窗；停止并销毁组件 | 0 |
| [bebdb0d9…](sessions/bebdb0d9f3b349b48ef191585f2b43b5/probe.events.jsonl) | 修正 HUD 分类后检查到当前角色的洛书功能未解锁；停止并销毁组件 | 0；没有绕过解锁条件 |
| [fa0435ba…](sessions/fa0435ba7b134503a973d4a9834c7a70/probe.events.jsonl) | 改用已存在的 MainUI 交互禁用/启用方法；执行并还原成功 | 1 次 disable-restore 命令，分别执行 Disable 和 Enable 各一次 |

每次都保存原始构建清单和固定哈希，使用独立会话目录及不同托管程序集身份。后两次启动前都确认上一组件已销毁、未执行任何界面动作。没有对未知结果的动作自动重试。

## 状态与资源证据

实际执行时的关键字段：

| 字段 | 操作前 | 还原后 |
| --- | ---: | ---: |
| level | 86 | 86 |
| stamina | 20 | 20 |
| gold | 450 | 450 |
| bind_gold | 375 | 375 |
| money64 | 56926588 | 56926588 |
| formation_set | 2 | 2 |

另外监测 experience、practice、spirit_food、mana、stone、reputation、money、cur_formation_set，共 14 个字段。探针聚合回执记录最终比较和 368 次过程观测全部相等；实际保留的是 6 份带资源明细的快照及逐字段聚合结果，并非 368 份原始样本。独立复核确认已保存的 6 份快照相等，不能离线逐条重放未保存的中间样本。修炼 HUD 自己显示的累计经验继续变化，和玩家模型中的 experience 字段不是同一份观测；没有把 HUD 自主变化当成该方法造成的资源变化。

UI 执行和字段相等分别计分；原始探针保留 `resource_consumption_causality=UNVERIFIED` 与 `global_network_effects=UNVERIFIED`。白名单相等和方法体无资源写入支持本轮受限验证，不构成所有资源、所有网络或未来业务调用的证明。

## 清理与剩余限制

三个会话的回调均结束、钩子均移除、辅助组件均收到销毁回调；进程查询未发现剩余 bridge_host.exe。独立只读元数据回读仍能看到三个探针程序集，模块查询也看到三个 bridge.dll。这些驻留代码与固定载荷文件的只读句柄需游戏进程退出后完全释放；当前已无活动探针循环。没有为清理而强制退出游戏。

资源核对和外部只读观察不是服务器事务或原子快照。这个原型绑定当前 x86 Unity 4.1.5f1、旧版 Mono、特定程序集哈希和精确方法。版本更新、场景替换、弹窗、加载、禁用归属变化等都可能阻止动作。若测试期间出现不安全界面状态，代码会停止而不强行启用别人的界面；组件退出与状态成功还原是两项独立判据。

后续可以把这个已验证的主线程入口和状态读取封装成助手连接器，再按用户逐项指定的流程加入明确的按钮/方法白名单。当前只有固定研究命令，没有任意方法执行、脚本求值或自动业务任务。

## 文件与验证

- [acceptance.json](sessions/fa0435ba7b134503a973d4a9834c7a70/acceptance.json)：主控汇总及可见状态观察时间。
- [probe.events.jsonl](sessions/fa0435ba7b134503a973d4a9834c7a70/probe.events.jsonl)：Update、实例、动作、恢复、资源比较和销毁原始记录。
- [native.jsonl](sessions/fa0435ba7b134503a973d4a9834c7a70/native.jsonl)：单次入口、线程、回调完成和撤钩记录。
- [cleanup-audit.json](sessions/fa0435ba7b134503a973d4a9834c7a70/cleanup-audit.json)、[installed-files-audit.json](sessions/fa0435ba7b134503a973d4a9834c7a70/installed-files-audit.json)：清理范围和 8 个文件身份核对。
- [input-build-manifest.json](sessions/fa0435ba7b134503a973d4a9834c7a70/input-build-manifest.json)：本次实际载荷与原生代码身份。
- [managed-contract.md](managed-contract.md)、[native/README.md](native/README.md)：固定 ABI、协议、构建及生命周期。
- [review-disable.md](review-disable.md)：独立源码复核；其他 review-*.md 保存前序阶段边界。
- [review-session-fa0435ba.md](review-session-fa0435ba.md)：独立回执、状态顺序、资源快照及清理复核。

最终载荷 SHA256：`5CCA9F8804454EA34A6ABA9D3ECB28A74AD06B56A5CA071DA77A02A5FAD9AFF1`；内部身份 `JSUnityProbe_c3fec61cde08ec23`。旧版引用/x86 构建、70 项元数据/IL 检查、39 项协议/资源比较检查通过。原生隔离测试覆盖一次触发、错误身份/随机标记拒绝、取消、超时后回调仍运行及最终完成；其通过不代替本次真实游戏验收。
