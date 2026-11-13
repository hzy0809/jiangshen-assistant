# Unity 对象层接入研究

日期：2026-10-31，Asia/Shanghai。对象：本机安装的 32 位 Unity 4.1.5f1 / Mono 将神客户端。

**结论：直接读取玩家状态已验证；后续也已打通 MainUI 实例定位和 Unity 主线程桥接，实测调用客户端已有的 Disable/Enable 并还原成功。** 当前是固定白名单的研究原型，详见 [直接调用验证](bridge/README.md)，尚未开放通用业务按钮接口。

## 实测结果

2026-10-31 **01:12:19（上海）**，对实际 `js.exe` PID 12844 进行一次有边界的只读观察：

| 字段 | 值 | 验证范围 |
| --- | ---: | --- |
| `Global.character_.level` | 86 | 类型、声明字段和运行时对象身份验证；与当时游戏画面等级一致 |
| `Global.character_.stamina` | 20 | 同上；未在画面中独立核对这个字段 |
| `Global.character_.gold` | 375 | 同上；与当时游戏画面金币数一致 |

原始证据：[live-scalars-probe.json](live-scalars-probe.json)。三个数值的实际载荷共 **8 字节**；数值阶段连同身份、元数据、域和一致性验证共读取 587 字节 / 73 次。前置程序集阶段读取 654 字节，类型阶段读取 524 字节；这些读阶段不构成游戏线程的原子快照。

探针只请求 `PROCESS_QUERY_INFORMATION | PROCESS_VM_READ`，使用模块枚举及 `ReadProcessMemory`。未注入代码、创建远程线程、调用游戏方法、写进程内存、修改客户端文件或向游戏发送操作。游戏安装文件的哈希已固定；布局或类型不匹配时停止读取。

## 判据

| 判据 | 状态 | 证据 / 限制 |
| --- | --- | --- |
| 识别实际 Mono 域和已加载程序集 | PASS | [live-metadata-probe.json](live-metadata-probe.json)，15 个程序集 |
| 定位 `Global`、`MainUI`、`Database.Character` 与基类元数据 | PASS | [live-types-probe.json](live-types-probe.json)，精确 TypeDef、名称、命名空间及所属程序集验证 |
| 取得已初始化的 `Global.character_` 实例并读取三项玩家状态 | PASS | 验证静态字段、AppDomain、vtable、动态类型及字段布局后读取 |
| 所有读取均限定为进程查询和内存读取 | PASS | 三个探针源码经主控审查；不包含写入、注入或远程调用路径 |
| 界面显示与读取值一致 | PASS（局部） | 等级和金币吻合；体力没有独立画面证据 |
| 战斗播放/结算状态的实时读取 | UNVERIFIED | 静态入口已定位，尚未进行战斗验收 |
| 找到真实按钮处理链及调用约束 | PASS（静态） | [object-map.md](object-map.md)，自定义 IMGUI，而非现代 uGUI Button |
| 在 Unity 主线程取得当前 `MainUI` 实例 | PASS（后续实验） | 进程内固定探针等待真实 Update，再调用 Unity 对象查找 API；实例 11752、Capital |
| 直接调用既有界面方法并还原 | PASS（限定操作） | Disable/Enable 各一次，图标变暗并恢复，见 bridge/README.md |
| 各类业务按钮列表、领取、战斗、推图 | UNVERIFIED | 当前验证不代表这些方法已接入或经过验收 |

## 读取路线

```text
实际 mono.dll（版本哈希及代码字节校验）
  → 已加载程序集
  → 精确 TypeDef 的已有缓存
  → 已初始化 AppDomain / Global vtable
  → Global.character_ 静态引用
  → 验证 Character 动态类型
  → 白名单字段 level / stamina / gold
```

采用当前版本 Mono 导出访问器和内部遍历代码的实际反汇编推导布局；没有调用这些函数，也没有扫描整个堆。读取前后复核引用和域注册表；对象为空、未初始化、类型变化、读取不完整或出现歧义时拒绝继续。

这是一条**绑定当前客户端版本**的研究路径。玩家对象会在登录/切换时替换，后续连接器需要每次重新取得引用。数值是客户端当时持有的状态；断线缓存、默认对象和跨字段更新时间差都应保留为观察限制。`money` 等复合属性也不能直接用同名基础字段代替：本客户端的完整余额为 `base.money + money64`。

## 按钮路线与下一步选择

本客户端使用自定义 IMGUI。`MainUI.btnTop` / `btnBottom` 的元素 `_ButtonMain` 保存 `name`、`enable`、`rcScreen` 和 `handler`；正常点击还会检查解锁、全局可交互状态及 UI 输入条件。直接调用 `handler` 会跳过其中部分条件，不能统一代替用户点击。

已定位一个真实链条：`formation` 按钮 → `MainUI._formation()` → `FormationUI` 的加载和显示。也发现具体副作用反例：阵型窗口的关闭处理会保存阵型并发送更新请求，因此本轮没有拿它做“无副作用”的试点。局部动画方法 `luoshuZhankai` / `luoshuShouqi` 可以作为后续候选，但尚未实际调用。

Mono 的方法调用接口是进程内接口。要做直接调用，需要另外建立与此旧版 x86 Mono 兼容的入口，并把 UI 命令安排到 Unity 主线程；给另一个 Python 进程加载同名 DLL，不会获得游戏里的对象。Mono 的线程接入要求和 Unity 的线程约束见 [Mono 官方嵌入说明](https://www.mono-project.com/docs/advanced/embedding/)及 [Unity 的 .NET 线程说明](https://docs.unity3d.com/cn/2023.2/Manual/overview-of-dot-net-in-unity.html)。旧客户端的具体能力仍以本地程序集和原生代码为准。

后续主线程调用实验已完成一类纯界面操作，见 [桥接报告](bridge/README.md)。后续集成建议：

1. 先把已经验证的只读状态接成独立观察模块，为任务提供前置条件和完成条件。
2. 操作先复用正常鼠标/键盘路径；若后续取得可靠的按钮位置/状态，可减少图像识别。
3. 在已经验证的主线程入口上，逐项加入业务操作白名单；每项单独验证条件、结果与恢复，不能用一次 Enable/Disable 成功代替所有按钮验收。

## 交付与复现

- [object-map.md](object-map.md)：玩家、UI、战斗和按钮语义，含字段来源与副作用反例。
- [runtime-routes.md](runtime-routes.md)：准确的 Mono 布局、访问路线、ABI 和主线程限制。
- [tools/probe_mono_readonly.py](tools/probe_mono_readonly.py)：程序集元数据只读探针。
- [tools/probe_mono_types.py](tools/probe_mono_types.py)：四个指定类型的缓存查询。
- [tools/probe_mono_scalars.py](tools/probe_mono_scalars.py)：仅允许三项数值字段的探针。
- `live-*-offline-layout.json` / `live-*-offline-checks.json`：离线布局与拒绝条件验证。

探针默认只做离线检查。运行时读取必须明确传入 `--live --pid <实际 js.exe PID>`；`--output` 只允许写到本研究目录的 `live-*.json`。可从项目根目录运行：

```powershell
python -B .\research\unity-object-layer\tools\probe_mono_scalars.py
# 确认游戏进程后，做一次三字段读取：
python -B .\research\unity-object-layer\tools\probe_mono_scalars.py --live --pid <PID>
```

离线分析工具为 ILSpyCmd 9.1.0.7988（在已有 .NET 8 上运行）和 Capstone 5.0.6；后续桥接使用项目内 Zig 0.13.0 编译 x86 原生入口，并以客户端基础库编译托管载荷。没有增加图形助手的运行依赖。图形助手尚未接入研究桥接，也没有新增自动业务任务。
