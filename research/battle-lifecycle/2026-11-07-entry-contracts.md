# 已接入战斗入口与客户端契约

核对时间：2026-11-07。本文记录改造前助手源码与已安装客户端的契约；静态契约不代表改造后实机通过。客户端 `Assembly-CSharp.dll` SHA-256 为 `c1c8ebac7ddef4173c873a00774975a5ca366f95b19164c4ca775165eb994911`，`Assembly-CSharp-firstpass.dll` 为 `29d8fa2cb55e800ccdf1cdc6c5b274b924ea7d03c989296dfa569731e02a8d87`，与运行包 manifest 和 `research/battle-skip/installed-identity.json` 一致。源码目录不是 Git 仓库；本次改动前快照保存在 `D:\CodexTask\2026-11-07\battle-lifecycle-baseline-142527`。

表中“回调”指游戏原生处理，公共层不得替玩法代领或擅自导航。战报、服务端结算、业务结果和页面返回是不同证据。

| 助手入口 | 发起及战报 | 原版结算、结果回调和返回 |
|---|---|---|
| 征战，未完成普通主线 type 0 | 原征战地图、选目标和布阵；`CampaignUI` 的 `0x402` 返回 report，可能附带 `battle_end`；wait/use-end 依目标和客户端分支决定。 | 需要时 `0x408`；`CampaignUI.BattleEnd` 更新通关、星级、奖励等；结果确认后走 `battle_exit`，再由原 `BattleResult`/`BonusOpen`/`BonusClose` 处理。见 `data/campaign-research/CampaignUI.cs.txt:471,672`、`CampaignBaseUI.cs.txt:573`、`native/unity_bridge/managed/CampaignObserver.cs:53`。 |
| 普通挑战，未完成普通 type 2 | 挑战列表 `AnecdoteSelectUI` → `0x401` 进地图 → 原布阵与 `0x402`；report 来源及条件分支同 `CampaignUI`，但使用独立挑战身份。 | 原结果和奖励链不变；章节结束使用原挑战分支返回挑战列表，不进入主线下一章。见 `installed-AnecdoteSelectUI.cs.txt:496`、`native/unity_bridge/managed/ChallengeNavigation.cs:209`、`data/campaign-research/CampaignChapterClearUI.cs.txt:85`。 |
| 地宫，task 17 | 原地宫选择/进入 `0x343`、`0x341`；目标 `finish+1` 布阵后 `0x342` 返回 report，wait=false、use-end=true。 | `0x408` type 4 → `GraveLevelUI.BattleEnd` 更新兵力、进度与奖励；结果、奖励、Boss 提示和 `_return` 按原回调处理，返回地宫选择。已进入场景续跑不重发 `0x341`。见 `research/battle-skip/GraveLevelUI.cs.txt:383,421,548`、`native/unity_bridge/managed/DailyGraveModel.cs:226`。 |
| 名将试炼普通关，task 14 | 试炼列表 `0x330`、进入普通章 `0x331`；原目标布阵后 `0x334` 返回 report，wait=false、use-end=true。 | `0x408` type 2 → `TrialLevelUI.BattleEnd`；原奖励关闭回调负责第三胜后的宝箱与升级，不由公共层重复领取；结束由试炼原 `_return` 导航。见 `research/trial-full-clear/TrialLevelUI.cs.txt:258,303,349`。 |
| 名将试炼 Boss，task 14 | 试炼页面 callBoss、布阵后 `0x336` 返回 report，wait=true、use-end=true。 | `0x408` type 3 → `TrialUI.BattleEnd`；胜利和失败各自走原提示/`BattleValueUI` 与返回流程。见 `research/trial-full-clear/TrialUI.cs.txt:279,336`。 |
| 天神，task 8 | 原天神页面选择 troop、布阵，`0x4B1` 返回 report，wait/use-end 均为 true。 | `0x408` type 5 → `DivinityUI.BattleEnd`；原 `BonusOpen`/`FailedOpen`、奖励关闭和页面 `_return` 生效。目标清理以五个目标的 mask 为准，不以日常 1/1 代替。见 `research/battle-skip/DivinityUI.cs.txt:317,391,469`、`native/unity_bridge/managed/DailyDivinityBattleModel.cs:628`。 |
| 排位竞技，task 9 | 原竞技选择 → `0x463` 进入 → 原对手挑战 `0x460`；响应包含 report 和 report ID。 | 胜负、次数、冷却、排名与奖励在 `0x460` 已结算，不发送 `0x408`；`BattleClose` 打开原结果/奖励，失败时走原比较页回调，重复轮次留在竞技页。见 `research/battle-skip/ArenaUI.cs.txt:492,885`、`native/unity_bridge/managed/DailyArenaBattleModel.cs:183,386`。 |
| 分组积分赛，route 109 | 原竞技选择 → `0x464` 进入列表 → 原对手挑战 `0x465`；响应含 report 与 report ID。 | 胜负、积分、次数和冷却在 `0x465` 已结算，不发送 `0x408`；`BattleClose` 恢复积分赛页，战后原 `0x464` 刷新列表；最终原 `_return` 返回。见 `research/battle-skip/PointraceUI.cs.txt:554,618`、`native/unity_bridge/managed/DailyPointRaceModel.cs:190,379`。 |
| 训练妖魔普通战斗，task 23 | 原训练页选怪、布阵，`0x368` 返回 report；初始响应缓存经验、buff、冷却。 | `0x408` type 7 → `SealMonsterUI.BattleEnd`，成功/失败分别更新目标与冷却，保留原结果、奖励和训练页返回。见 `research/battle-skip/SealMonsterUI.cs.txt:261,574`、`native/unity_bridge/managed/DailyTrialModel.cs:431,558`。 |
| 自家建筑怪，task 15 | 原主城目标 mesh 的 `MonsterAction` → 布阵 → `0x48D` 返回 report，parent 为该怪物脚本。 | `0x408` type 1 → 原 `BuildingMonsterScript.BattleEnd`；奖励回调更新目标/下次刷新，失败走原失败路径。已死亡目标的 `0x48E` 直接领奖，无普通战报。见 `research/battle-skip/BuildingMonsterScript.cs.txt:153,426,487`、`native/unity_bridge/managed/DailyMonsterModel.cs:223`。 |
| 好友建筑怪，task 10 | 好友访问 `0x4C0` → 好友怪物 `MonsterAction` → 布阵 → `0x4C1` 返回 report。 | `0x408` type 1 → 原怪物回调；胜利更新击杀归属、协助计数和奖励，之后原回调返回好友场景。见 `research/battle-skip/BuildingMonsterScript.cs.txt:319,426,521`。 |
| 自己的复仇妖魔，task 24 | 主城自己的 demon mesh → `DemonAttack` → 布阵 → `0x492`；响应含 report，播放为 `viewReport`、parent 为空。 | `0x492` 已结算胜负、兵力、奖励和冷却，不发送 `0x408`；只允许与本操作的角色、demon、mesh、root、真实新 report 精确绑定的回放。`0x493` 是已击杀后的无战报领奖。见 `research/battle-skip/DemonScript.cs.txt:237,402`、`native/unity_bridge/managed/DailyMonsterModel.cs:43`。 |
| 木人快速完成，task 22 | 原木人页 `0x264` 读取状态，`0x268` 使用免费快速次数返回结构化结果。 | 无普通战报、无 `0x408`；原 `BattleWoodwarResultUI._fast_finish_close`、奖励 `0x266` 和面板返回保持独立。见 `research/battle-skip/WoodWarUI.cs.txt:366,492,619`、`native/unity_bridge/managed/DailyWoodModel.cs:597`。 |
| 训练妖魔一键完成 | 仅有客户端权限且有可用目标时，原训练页调用 `0x370`。 | 无普通战报、无 `0x408`；原回调直接更新目标、经验/buff 和结果窗口。见 `research/battle-skip/SealMonsterUI.cs.txt:609`、`native/unity_bridge/managed/DailyTrialModel.cs:247`。 |

## 规则与边界

- 征战连续三败停止且胜利清零；普通挑战累计三败停止且胜利不清零。重复结果不重复计数，未知不作为胜败。见 `jiangshen_assistant/campaign.py:290`、`challenge_task.py:12`。
- 排位只使用自然次数、按钱币及声望上限判断；积分赛只用 10 次自然挑战及明确报价为零的一次免费刷新。当前实现遇竞技冷却会以不可用/部分完成退出，不应声称它自动等待该冷却。
- 地宫、试炼保留场景内已有探索的 resume，分别禁止重发 `0x341`、`0x331`。失败、兵力、体力、Boss/宝箱状态和原回调各自决定停止与完成。
- 训练妖魔当前源码有冷却续做缺口：原生模型会关闭并锁定本轮，Python 延长期限并不证明会恢复。改造不得将它误报为已自动续做。
- 木人普通 `0x265` 多战报流程存在于客户端，但当前助手日常只接入 `0x268` 快速路径；好友妖魔 `0x4C2` 和 GeneralBook/type 8 也未发现助手接入，不因公共化而启用。
- 游戏未启用/无自然次数、历史样本、代码和离线测试，均不代表当前版本实机验收通过。改造后各模式单独记录 PASS、FAIL 或 UNVERIFIED。
