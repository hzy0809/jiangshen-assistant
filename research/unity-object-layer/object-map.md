# 将神 Unity 对象层离线映射

状态：DONE。结论：**安装包的托管对象层确实包含可用的玩家状态、当前界面、战斗回放状态与按钮处理链，静态研究 PASS；运行中实例定位、数值读取和方法调用均未由本报告验证。** 不能把类型/字段存在当成当前进程可访问的证明。

本叶任务只读取安装目录 `C:\Users\yu\AppData\Local\JiangShen\pc\js_Data\Managed` 中的 `Assembly-CSharp.dll`、`Assembly-CSharp-firstpass.dll`、`pbdata.dll`、`dbdata.dll`。使用控制器提供的 ILSpyCmd 9.1.0.7988 离线反编译；未执行游戏 DLL、读取进程内存、注入、发按键、读取实际账户/配置/日志、连接游戏服务器或修改安装目录。下面的字段名是程序声明，不是用户数据。

## 1. 玩家状态：有明确入口，且登录时会更换对象

| 对象路径 / 类型 | 声明与语义 | 本目录证据 |
|---|---|---|
| `Global.character_` | `public static Character character_ = new Character();`，定义于 `Assembly-CSharp-firstpass.dll`，`Global` 无命名空间 | `object-map-Global.cs.txt:97` |
| `Database.Character` | `public class Character : _character`，定义于 firstpass；基类为 `[pbdata]Database._character` | `object-map-Database.Character.cs.txt:11`；`object-map-Character.il.txt:3` |
| `Global.character_.level` | 继承的 **public instance `short` 字段** | `object-map-character-base.cs.txt:35` |
| `Global.character_.stamina` | 继承的 **public instance `short` 字段** | `object-map-character-base.cs.txt:43` |
| `Global.character_.gold` | 继承的 **public instance `int` 字段** | `object-map-character-base.cs.txt:47` |
| `experience / bind_gold / practice / spirit_food / mana / stone / reputation` | 分别为 `int / int / int / int / int / int / uint` 的实例字段 | `object-map-character-base.cs.txt:33–77` |
| `Global.character_.money` | `Database.Character` 的 **public instance `long` 属性**，值为 `base.money + money64`；基类同时存在 `int money` 与 `long money64` 字段。直接读取同名基类字段会漏掉余额 | `object-map-Database.Character.cs.txt:439`；`object-map-character-base.cs.txt:51,545`；IL `object-map-Character.il.txt:7–22` |
| `Global.character_.checkUI(string)` | 查询私有 `Dictionary<string,int> uis_`，空名称返回 true；它是功能解锁集合，不是当前可见 UI 清单 | `object-map-Database.Character.cs.txt:25,503–536` |

来源链不是命名猜测：`LoginUI.sc_playerinfo(MemoryStream, object)` 反序列化 `SCPlayerInfo_0x10`，执行 `Global.character_ = new Character()`，随后 `Global.character_.Set(response.playerInfo)`；`Database._character.Set(db_character)` 对 level/stamina/gold 等字段赋值。因此后续读探针应从静态根重新取对象，不应跨登录或重连永久缓存对象地址。证据：`object-map-LoginUI.cs.txt:939–960`、`object-map-character-base.cs.txt:547–574,818`。

原始 PE 元数据 TypeDef token 已由 `System.Reflection.Metadata`/`PEReader` 独立读取（不加载目标程序集）：firstpass `Global`（空命名空间）=`0x0200014F`、`Database.Character`=`0x02000071`；pbdata `Database._character`=`0x0200002F`；CSharp `MainUI`（空命名空间）=`0x020001AF`。firstpass `_ButtonMain`=`0x0200001C`、`_ButtonStyle`=`0x02000022`。证据：`object-map-typedef-tokens.txt`；复现脚本 `tools/object_tokens.ps1`。这些是离线 metadata token，不是对象地址或固定内存偏移。

更新并非只有首次登录：`RootUI.sc_player_refresh_0x23` 反序列化服务端刷新结果，更新 `next_stamina_time`，成功时写入 stamina；其他响应也会修改资源。证据：`object-map-RootUI.cs.txt:1936–1948`、`:63–71`。这是客户端持有的会话状态，不能据此声称服务器权威状态始终同步；初始 `new Character()` 默认值也不能证明已登录。

## 2. 当前 UI：场景根、子窗口和独立弹窗需分开观察

| 入口 | 明确路径 / 返回值 | 证据 |
|---|---|---|
| `BaseAsset.lds` | protected static `LoadScreen`；初始化通过 `GameObject.Find("/LoadUI").GetComponent<LoadScreen>()` | `object-map-BaseAsset.cs.txt:25,159–174` |
| `LoadScreen.GetCurUI()` | 返回私有当前 `RootMessenger currmg`；`RootMessenger.LevelID` 是 public instance string | `object-map-LoadScreen.cs.txt:341–350`；`object-map-RootMessenger.cs.txt:20` |
| `LoadScreen.GetOldUI()/GetOldMainUI()` | 前一个根/主界面根；不能误认为当前界面 | `object-map-LoadScreen.cs.txt:331–339` |
| `BaseUI.GetRootUI()` | 缓存 RootUI；通过 `GameObject.Find("Root/RootUI").GetComponent<RootUI>()` 查找，找不到返回 null | `object-map-BaseUI.cs.txt:29–40` |
| `BaseUI.GetMainUI()` | `BaseAsset.lds.GetCurUI().GetComponentInChildren<MainUI>()`，该方法本身没有空值防护 | `object-map-BaseUI.cs.txt:66–69` |
| `MainUI.GetSubUI()` | 返回 `private static __BaseUI m_subui as BaseUI`；真实存储类型更宽，返回 null 不能单独证明没有子窗口 | `object-map-MainUI.cs.txt:84,419–463` |
| `__BaseUI.IsVisible()/IsEnable()` | 分别返回 Unity component `enabled` 与自身 private bool `enable`；可见和可交互是不同状态 | `object-map-__BaseUI.cs.txt:1159–1197` |
| `__BaseUI.cuis / UICount_Visible` | public static 可见计数/字符串列表；仅覆盖参与 visible_count 且满足初始化条件的 UI，不是完整树 | `object-map-__BaseUI.cs.txt:738,780,792,1137–1166` |

`MainUI.SetSubUI(__BaseUI)` 会隐藏旧子 UI，并向当前根广播摄像头/输入相关消息；它只更新子 UI 指针，调用者还要执行相应 SubLoading 初始化。直接赋字段、调用 Visible 或调用 handler 并不天然等价于正常点击。证据：`object-map-MainUI.cs.txt:424–450`。根 `RootMessenger` 也会广播 Enable/Disable/Visible/Hide；这些入口跨多个接收组件，不能统一视作无业务副作用。证据：`object-map-RootMessenger.cs.txt:37–65`。

## 3. 战斗：对象层有丰富本地解释状态，不能混同服务器结算

`BaseUI.GetBattleUI()` 查找 `GameObject.Find("Battle/BattleUI").GetComponent<BattleUI>()`，找不到返回 null（`object-map-BaseUI.cs.txt:86–93`）。

可读候选：`BattleUI.BattleFighting`、`BattleType`、`BattleResultLevel`、`IsSurrended`、`ReportID`、`IsCrossServer`、`ArmyBehaviourCreated`；军队状态方法 `getArmyInfo(int)`、`getArmyCurrentNum(int)`、`getArmyTotalNum(int)`、`getArmyLoseNum(int)`。证据：`object-map-BattleUI.cs.txt:174–204,1080–1104`。方法的 index 在部分实现中只是 `index != 0` 区分左右，不能当成任意单位 ID。

`BattleUI` 私有实例字段 `BattleReportInterpreter2 interpreter` 保存回放解释器。其 public getter 包括 `packet`、`isFinished`、`MainQueueFinished`、`CurrentAction`、`PlayedTurnCount`，证据 `object-map-BattleReportInterpreter2.cs.txt:310–322`。这是本地战报解释/动画进度；部分 getter 依赖初始化对象或 enumerator，不能在任意时点盲调。

竞争解释已区分：`BattleFighting` 表示本地播放循环推进状态，并不单独证明服务器战斗完成。`fight()` 在解释器结束时仍可能发送 battle_end，`newBattleEnd(SCBattleEnd_0x408)` 独立保存服务端结束包。证据：`object-map-BattleUI.cs.txt:835–876`。`LoadScreen.IsBattling()` 还提供另一层 UI 模式标记，证据 `object-map-LoadScreen.cs.txt:405–413`；它与播放标记应一起解释。

## 4. 按钮框架与具体处理链

这里使用自定义 IMGUI：继承链为 `MainUI/BattleUI -> BaseUI -> __BaseUI -> BaseAsset -> EventDispatcher -> MonoBehaviour`。`__BaseUI.OnGUI()` 分发 Web/Phone 绘制；`_ButtonStyle.Button(...)` 包装 `GUI.Button/GUI.RepeatButton`，不是现代 uGUI 的 `UnityEngine.UI.Button.onClick`。证据：`object-map-EventDispatcher.cs.txt:3`、`object-map-__BaseUI.cs.txt:1575–1593`、`object-map-_ButtonStyle.cs.txt:142–176`。

`_ButtonMain` 是普通托管 class，包含 public `string name`、`delegate_handle handler` (`void()`)、`delegate_check checker` (`bool(string)`) 、`List<_ButtonMain> btnSubs`、`bool enable/expand/current/handled/repeated`、`Rect rcScreen`、`btnParent`。主界面的 `btnTop`、`btnBottom` 是 public `List<_ButtonMain>`，其余若干按钮组是 private。证据：`object-map-_ButtonMain.cs.txt:39–89`、`object-map-MainUI.cs.txt:112,155`。

`_ButtonMain.Button(Rect,bool)` 在绘制时更新 rcScreen，随后依次检查 checker、`Global.character_.newuis_`、`__BaseUI.my_enabled`、enable、GUI 输入事件；只有通过后调用 handler 并更新展开状态。证据：`object-map-_ButtonMain.cs.txt:327–394`。因此 rcScreen 是最近绘制的几何信息；`Check()` 只调用 checker，不覆盖所有点击条件；直接执行 handler 会跳过点击门禁和部分 UI 状态处理。

一个完整真实链：`MainUI` 创建 name=`"formation"` 的 `_ButtonMain(...,_formation,checkUI)` → `MainUI._formation()` → `BaseUI.GetRootUI().GetComponent<FormationUI>()` → `handle_sub_ui(component,"Formation")` → `FormationUI.FormSubLoading()` → `SubLoading(Hashtable)`，其中至少需要键 `"subui"`。其他模式如 `"NPCBattle"` 还要求 campaignid/troopid/viewonly 等参数，不能随意构造。证据：`object-map-MainUI.cs.txt:1018,1970–1979`、`object-map-FormationUI.cs.txt:727–776`。

**反例：关闭不是通用安全动作。** `FormationUI.OnClickClose()` 在非 PresetDuel 模式调用 `OnClickSetCurrentForm()`，后者更新 `Global.character_.cur_form` 并调用 `cs_formupdate(true)`，最终 `RootUI.m_client.Send(298, CSFormationUpdate_0x12a,...)`。单独 `Hide()` 又跳过这些正常业务语义。证据：`object-map-FormationUI.cs.txt:719–725,1734–1766,2827–2839`。

## 5. 后续最小本地 UI 操作候选：洛书栏展开/收起（本任务未操作）

`MainUI.luoshuZhankai()` / `MainUI.luoshuShouqi()` 是 public instance `void()`。前者只赋值自身 target_pos/start_pos/turningEndTime/luoshustatus；后者多读取 `Global.character_.formation_set` 计算 target_pos，同样只改动自身动画状态。方法体只调用 DateTime，不发网络请求、不写玩家模型。证据：`object-map-MainUI.cs.txt:4852–4866`，字段声明 `:303–323`。

调用来源 `drawLuoshu()` 先检查 `Global.character_.checkUI(90u)`，随后根据 SHOUQI/ZHANKAI 与 `IsEnable()` 展示对应按钮，调用上述两个方法；绘制后把 startposx 插值到 target_pos。证据 `object-map-MainUI.cs.txt:4768–4833`。同一栏的**阵型条目点击**另有 `cs_formation_set_switch(...)` 网络操作（`:4811–4816`），不能和展开/收起混用。

几何访问的限制：这两个按钮分别有持久化的 private `_ButtonStyle btn_luoshu_zhan/btn_luoshu_shou`（MainUI `:319–321`），**不是 `_ButtonMain`**；`_ButtonStyle` 没有 name/enable/rcScreen。每帧直接绘制 `__BaseUI._rectB(11f,70f,57f,62f)`，其公式是 `(11*s, _rcCur.height-70*s,57*s,62*s)`，其中 `s=__BaseUI._screenScale`；当分组栈为空时 `_rcCur` 是整个游戏 Screen（`object-map-__BaseUI.cs.txt:832–861`）。所以这个候选不能通过读取按钮 rcScreen 获得坐标；如后续采用正常 UI 输入，应额外确认当前绘制上下文、游戏窗口客户区、缩放、可见性和实际画面，不能直接使用桌面坐标。MainUI.Web_OnGUI 在 BuildingBuildUI.m_bBuildVisible 为 true 时还会提前返回（MainUI `:1289–1305`）。

候选限于后续验证设计：先证明实际 MainUI 实例存在且功能已解锁、界面可用，再记录原状态；仅在单独获准的主线程调用实验中使用对应方法，并验证 UI 动画和恢复。这里没有执行方法、生成注入器或证明调用能力。`FPS_Script.StartFPS()` 不作为替代候选，因为它启用的 Update 会根据 `OptionUI.quality_auto` 调整画质并修改设置状态，证据 `object-map-FPS_Script.cs.txt:18–68`。

## 6. 验证、限制与交付

| 判据 | 结果 |
|---|---|
| 状态模型入口、继承、字段类型、来源链可从当前 DLL 定位 | PASS |
| 当前 UI、子窗口、按钮条件和真实 handler 路径可定位 | PASS |
| 战斗本地播放与服务端结束包的差异有代码支持 | PASS |
| 找到无直接网络/业务模型写入的局部 UI 操作候选 | PASS，静态方法体与绘制调用方检查 |
| 运行时 assembly/domain/实例/字段偏移、实际值 | UNVERIFIED，控制器另行安排 |
| 外部访问和方法调用、Unity 主线程调度、实际 UI 结果与恢复 | UNVERIFIED，未读取进程或操作 UI |

Unity API、OnGUI、协程/加载和组件生命周期贯穿操作链；后续执行需要在游戏所属 Unity 主线程和合法生命周期中安排。静态程序集不能证明外部线程调用被安全支持，也未发现/验证面向外部工具的现成对象 RPC。可读候选应使用运行时元数据定位，禁止从静态名字猜偏移；只读实验先白名单 level/stamina/gold 等数值字段，避免遍历无关个人字段。模型读取还存在对象替换、并发更新、未登录默认值和断线缓存等解释限制。

输入身份已保存 `object-map-input-hashes.json`（SHA256）：Assembly-CSharp `C1C8EBAC7DDEF4173C873A00774975A5CA366F95B19164C4CA775165EB994911`；firstpass `29D8FA2CB55E800CCDF1CDC6C5B274B924EA7D03C989296DFA569731E02A8D87`；pbdata `905CF19394545E2C4B5D49D994A7D981E13AE38A2A629F7D02329F27C6D2D5AB`；dbdata 身份见 JSON。

离线一致性验证：`python tools/object_verify.py`，退出码 0，**16 PASS / 0 FAIL**；覆盖 4 个输入哈希复核与 12 组关键声明/调用链/IL/元数据标记。结果 `object-map-validation.json`。这不是编译、运行时验证或业务验收。

复现反编译命令：`dotnet tools/ilspycmd-9.1.0.7988/tools/net8.0/any/ilspycmd.dll --disable-updatecheck -t <完整类型名> <Managed目录内DLL>`；类型目录使用 `-l c`。`-il -t Database.Character` 实际输出整程序集 IL，已由 `tools/object_extract_il.py` 缩减为继承与 money getter/setter 的 41 行证据，保留原输出行号和 RVA。其余 `object-map-*.cs.txt` 为各目标类的原始离线反编译内容；没有把它们当作可重建源码。

交付仅新增本报告、同目录 `object-map-*` 证据和 `tools/object_strings.py`、`tools/object_extract_il.py`、`tools/object_verify.py`、`tools/object_tokens.ps1`、`tools/object_routes.ps1`；没有改变游戏或项目接口。未完成项仅为本叶任务范围外的运行中读取与操作验证。

## 7. 后续补查：字段元数据与 MainUI 的纯托管引用边界

以下由 PEReader 读取原始 FieldDef，ordinal 为**声明类自身字段的零基序号**，包括该类 static 字段，不计继承字段；不能当成字节偏移或 Mono 的已验证字段布局。

| 声明字段 | FieldDef | declaredOrdinal0 | Attributes | 原始字段签名 |
|---|---|---:|---|---|
| firstpass `Global.character_` | `0x04000957` | 41 | Public, Static | `06-12-81-C4`，FIELD + CLASS + TypeDefOrRef(Database.Character) |
| pbdata `Database._character.level` | `0x04000204` | 14 | Public，instance | `06-06`，FIELD + I2 |
| pbdata `Database._character.stamina` | `0x04000208` | 18 | Public，instance | `06-06`，FIELD + I2 |
| pbdata `Database._character.gold` | `0x0400020A` | 20 | Public，instance | `06-08`，FIELD + I4 |
| CSharp `MainUI.btnTop` | `0x0400144A` | 21 | Public，instance | `06-15-12-31-01-12-84-4D`，List&lt;_ButtonMain&gt; |
| CSharp `MainUI.btnBottom` | `0x0400145B` | 38 | Public，instance | 与 btnTop 相同 |

证据 `object-map-typedef-tokens.txt`。这是对未来只读探针的白名单元数据支持；本叶任务未进行进程读取。

在“不调用 Unity GameObject/Component 方法、不扫描堆、不借助原生组件容器”的限制下，**本次没有建立现有静态根到当前 MainUI 实例的完整路径**。有以下具体支持及界限：

- `BaseAsset.lds` 是已知 static 根候选（firstpass TypeDef `0x0200001A`，FieldDef `0x040000A0`，ordinal 3，Family/Static），`lds -> LoadScreen.currmg` 可到 `RootMessenger`（LoadScreen TypeDef `0x0200017E`，currmg FieldDef `0x04000CF9`，ordinal 30）。但 RootMessenger 自身只保存 LoadScreen 等字段，不保存 MainUI；正常 `GetMainUI()` 下一步是 `GetComponentInChildren<MainUI>()`。不能把 RootMessenger 对象当 MainUI 解读。
- `BaseUI.rootUI` 是 private static RootUI 缓存（CSharp TypeDef `0x02000083`，FieldDef `0x04000353`，ordinal 0）。RootUI 会通过 AddComponent 创建多个 MainUI，返回值保存在局部变量，用来配置 first_visible，没有对应的持久 MainUI 字段。例见 `object-map-RootUI.cs.txt:396–407,411–420`。
- 对四个授权 DLL 的全部字段签名进行离线解码：CSharp 790 TypeDef / 9036 字段，firstpass 697 / 6122，pbdata 2404 / 11805，dbdata 1195 / 8673；没有声明为 MainUI、MainUI 数组或带 MainUI 元素类型的泛型字段。原始候选及计数在 `object-map-field-route-candidates.txt`；脚本 `tools/object_routes.ps1`。此结果不排除 Object/BaseUI 字段或委托在某一临时时点持有 MainUI，不能作绝对不可达证明。
- `DelegateFunc` 的 MainUI_* static 回调看起来像桥接入口，实际赋值是 `MainUI.GetSubUI/_spirit2/_grave/_capital` 等 static 方法，或 `DelegateFuncUI.MainUI_*` static 包装；这些委托没有 MainUI instance target。证据 `object-map-DelegateFuncUI.cs.txt:17–60,131–198`、`object-map-DelegateFunc.cs.txt:95–151`。包装内部再次使用 GetRootUI/GetComponent，而不是返回已保存的 MainUI。
- `RootBase.vctUIFuncs` 是 static List&lt;delegate_OnClose&gt;，可能保存某些 UI 回调；已检查的 MainUI 源码不调用 add_UIFunc 或向全局注册自身 instance handler。`MainUI` 的按钮列表里确实有指向自身的委托，但在尚未找到 MainUI 之前，这些回指不能解决根入口问题。网络请求回调或消息框回调也可能临时引用 UI，本次没有把它们建立为稳定、现成、白名单可读的 MainUI 路径。

因此可以把“精确字段元数据已知”和“现有 MainUI 实例已拿到”分开结论：前者 PASS，后者本叶任务仍为 UNVERIFIED。下一步若必须读取 MainUI.btnTop/btnBottom，需要控制器另行安排一个经验证的实例定位入口；静态分析结果本身不授权扩大为堆扫描、原生组件遍历或调用 Unity API。
