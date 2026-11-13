# Installed trial route evidence

Read from `C:\Users\yu\AppData\Local\JiangShen\pc\js_Data\Managed` on 2026-11-06. The `.cs.txt` files are raw offline ILSpy output, not rebuildable game sources.

| Assembly | SHA256 |
| --- | --- |
| Assembly-CSharp.dll | c1c8ebac7ddef4173c873a00774975a5ca366f95b19164c4ca775165eb994911 |
| Assembly-CSharp-firstpass.dll | 29d8fa2cb55e800ccdf1cdc6c5b274b924ea7d03c989296dfa569731e02a8d87 |

The extraction used the existing `research/unity-object-layer/tools/ilspycmd-9.1.0.7988/tools/net8.0/any/ilspycmd.dll`, `--disable-updatecheck -t TYPE ASSEMBLY`. Types are TrialUI, TrialLevelUI, BattleValueUI (CSharp), TrialInfo and Database.TrialCampaign (firstpass).

- `TrialInfo` has five chapter slots. `TrialCampaign` creates three troops and sets their identifiers to 1, 2, 3.
- `TrialLevelUI.wantCombat` admits only `finish + 1`; `BattleEnd` marks a won troop killed, increments finish, and deducts one stamina on either result. A loss updates surviving soldiers and opens a failure message. No ordinary trial cooldown is used by this client route.
- `TrialLevelUI.BonusClose` increments cur_level at finish == 3 and invokes the stock chapter chest request. The chapter selector's `sc_trial_load_all` also requests an outstanding chapter chest.
- `TrialUI.Web_OnGUI` exposes the boss once cur_level > 5; it permits the challenge only when bosscd <= TimeUtil.Now and trial_rank <= character level + 5. `callBoss` binds FormationUI to the stock boss OnAttack. That OnAttack checks cooldown and sends 822; neither the boss request callback nor BattleEnd deducts local stamina or gold. This describes client behavior, not a separate claim about undocumented server charging.
- `TrialUI.BattleEnd(true)` sets bosskilled = 1; a loss opens BattleValueUI after the battle closes. The callback records the returned cooldown. `sc_trial_load_all` closes the mode when bosskilled is set; clearing all five ordinary chapters alone is not this end condition.
- The assistant requires chapter 6, all five chapter chests confirmed and bosskilled == 1 before publishing `clearing_complete`. Daily activity count is reported separately.

No game action, build, restart, test suite or code review was performed for this source change. Runtime progression, server acceptance and observed full clearing remain unverified.
