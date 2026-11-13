# Normal daily sign-in and salary routes

This is a historical implementation note. For the current sign-in state and
popup contract, including the fact that 916 response flags are not captured,
see [checkin-state-contract.md](checkin-state-contract.md).

Source-only implementation, 2026-11-06. No tests were written or run. No code review, build, client restart, or game action was performed by this worker. Runtime behavior and real receipt/cleanup acceptance remain unverified.

The raw `.cs.txt` files were extracted offline from `C:\Users\yu\AppData\Local\JiangShen\pc\js_Data\Managed`. Exact input SHA256 values are in `assembly-inputs.json`. `types-*.txt` are type inventories. Extraction used the existing `research/unity-object-layer/tools/ilspycmd-9.1.0.7988/tools/net8.0/any/ilspycmd.dll` with `--disable-updatecheck -t TYPE ASSEMBLY`; these are evidence, not rebuilt game sources.

## Stock behavior and premium boundary

| Route | Stock entry and action | Eligibility and request | Required receipt/state evidence |
|---|---|---|---|
| `301`, `signin` | `MainUI._signin()` instance -> `SignInUI.MyLoading(false)` -> `cs_all_bonus_get_new()` | Normal tab only; `checkin_today == 0`, `sended == false`; opcode 916 / 0x394 | `operate == OK`, `gold_consume == 0`, `checkin == true`, `checkin_today == 1`, `checkin_times` increased exactly once |
| `302`, `salary`, salary only | `MainUI._salary()` instance -> `AllBonusUI`; `getSalary()` delegates to `ReputationUI.cs_reputation_salary()` | `patrol_last_time == 0`, salary/reputation UI unlocked, reputation level positive; opcode 819 / 0x333 | `operate == OK`, positive response `time`, `patrol_last_time == time` |
| `302`, arena only | `AllBonusUI.getArenaBonus()` | `checkArenaToday() >= 0`, battle_arena UI unlocked; opcode 1122 / 0x462 | `result == 0`, `gold_consume == 0`, response `time` equals selected row's time and that exact row is absent after stock callback |
| `302`, both | `AllBonusUI.cs_get_all_bonus_0x348()` | Both previous free qualifications; opcode 840 / 0x348 | `operate == OK`, positive `salarytime` installed and exact selected `arenatime` removed |

The normal sign-in button is a **combined** claim: its stock 916 callback may also mark salary, remove an arena bonus and remove a point-race bonus. The model checks every asserted receipt flag against the corresponding time and resulting role state, in addition to checking sign-in itself. It does not offer the normal button after `checkin_today == 1`, even if other reward types remain; the independent salary route can still run.

`CSAllBonusGetNew_0x394`, `CSReputationSalary_0x333` and `CSGetAllBonus_0x348` are empty requests with no paid option. `AllBonusUI.getArenaBonus()` explicitly supplies `bFindBack:false` and the game's own `checkArenaToday()` index. This implementation calls that exact private stock method; it never constructs a request or supplies a historical index. `SignInUI.cs_newyear_checkin_award_get`, premium find-back, purchase, supplement and doubling paths are outside the action allowlist. Receiving gold is allowed; decreased gold/bound-gold or changed cumulative `gold_consume` stops the operation as unknown. This is source evidence for the normal free path, not a server-side price guarantee or runtime acceptance.

Decisive original methods:

- `MainUI.cs.txt`: `_signin`, `_signin_check`, `_salary`, `_salary_check`; the two entries are **instance** methods.
- `SignInUI.cs.txt`: `drawSignInInfo`, `drawAllBonus`, `cs_all_bonus_get_new`, `sc_all_bonus_get_new`.
- `AllBonusUI.cs.txt`: `Web_OnGUI` selects salary-only, arena-only or combined branch; `getArenaBonus`, `getSalary`, `cs_get_all_bonus_0x348`, `sc_get_all_bonus_0x348`.
- `ReputationUI.cs.txt`: `cs_reputation_salary`, `sc_reputation_salary`; the ordinary reputation page also disables salary when reputation level is zero. The adapter applies that conservative qualification to the salary route.
- `ArenaUI.cs.txt`: `cs_arena_bonusget_0x462`, `sc_arena_bonusget_0x462` expose the explicit `find_back` request switch and receipt consumption field.
- `Database.Character.cs.txt`: `gotSalary()` means `patrol_last_time != 0`; `checkArenaToday()` and `checkPointRaceToday()` choose current claimable rows using the game's own server clock and their distinct daily thresholds.
- `BattleResultUI.cs.txt`: ordinary confirmation hides the result and invokes `OnClose`, otherwise re-enables `m_prevUI`.
- The four named `game.net.data.StoC.*.cs.txt` and `game.net.data.CtoS.*.cs.txt` files preserve the wire schema.

## Integration contract

Owned implementation files:

- `native/unity_bridge/managed/DailySignSalaryModel.cs`
- `jiangshen_assistant/daily_sign_salary.py`

The controller owns all shared integration. Add the C# model to the build source list, allow synthetic tasks 301/302, and route them through one `DailySignSalaryModel` using these exact methods:

```text
Capture(Model model, int task, bool admissionBlocked)
Queue(int task, ulong role, string id, int sequence, int revision, string action, out string why)
ExecuteGui(DevelopmentPublication publish, DailyMonsterInvocationGate begin, out string why)
Json()
Stop(string reason)
NotInvoked(string reason)
Busy / Revision / MutationAttempted / GuiInvocationAttempted
```

`ExecuteGui` follows `DailyMonsterModel`'s nonce-admission signature. Continue capture while busy, and preserve the model/callback ownership until released. Do not replace an active model on a competing task observation. `DailyDivinityReplyTap` from the existing divinity source is a compile dependency; it forwards the original callback unconditionally and observes only that exact temporary registration. The callback must return normally before its packet can establish success. Call `Stop` on session cancellation and teardown, as with existing models. A stopped instance is not reusable; create a fresh adapter/model for a newly authorized session, while retaining any genuinely unresolved live ownership separately.

Task 301 action allowlist: `open`, `resume`, `claim_signin`, `reward_close`, `close`.

Task 302 action allowlist: `open`, `resume`, `claim_salary`, `claim_arena`, `claim_all_salary`, `reward_close`, `close`.

JSON is flat schema 1 with `task_id`, `route`, `role_id`, `refresh_cycle`, `operation_id`, `status`, `reason`, `next_action`, `availability`, `mutation_attempted`, `stock_callback_returned`, `reward_confirmed`, `cleanup_pending`, `closed`, `busy`, `request_count`, `opcode`, `checkin_today`, `checkin_times`, `salary_time`, `arena_time`. `availability == ""` means a free stock action is currently qualified. Terminal availability distinguishes `ALREADY_CLAIMED`, `NO_FREE_REWARD`, `CONDITION_NOT_MET`, `UNKNOWN`. It is re-read from game state; old operation completion is not eligibility evidence for a new operation. The outer workflow must invoke these synthetic items even if a local previous run marked them complete.

Python API:

```python
executor = DailySignSalaryExecutor(objects, token, stopped=..., paused=..., wait=...)
result = executor.execute(301, character_id=role_id, expected_cycle=cycle)
# execute(302, ...) for salary
```

Requires the shared `observe_daily_script` / `submit_daily_script` task/action allowlists and the existing flat snapshot pass-through. Result fields: `status`, `reason`, `mutation_attempted`, `reward_confirmed`, `stock_callback_returned`, `cleanup`, `operation_id`, `route`. Treat `ALREADY_CLAIMED` and `NO_FREE_REWARD` as normal no-op outcomes; retain `CONDITION_NOT_MET` distinctly. `COMPLETED` requires all of mutation attempt, normal stock callback return, matching account-state evidence, and `VERIFIED_CLOSED`. A wire rejection or unknown acknowledgement is not automatically retried. A later operation must observe the current game state again.

## Cleanup and limits

The model adopts only its matching already-open normal entry or opens the stock entry. It binds role identity, client/socket/stream, server day and refresh cycle. A receipt-created reward is bound to its exact cached component, reward list, stock close delegate and visible generation. Confirmation uses the stock Hide + OnClose sequence and requires the resulting entry/overlay to close with home observed. Foreign overlays and changed reward generations are not dismissed.

Unknown packet outcome never reissues a reward request. `UNKNOWN` with `busy` / `cleanup_pending` remains visibly unresolved; do not report it as cleanup success or let a local error reset erase that real ownership. A request bound of 16 prevents an unbounded loop if stock current-row selection behaves unexpectedly. Runtime feature unlocks, receipt callback registration, reward-cache hosting, UI readiness, day rollover and exact game cleanup are all unverified until the controller performs authorized game acceptance. No tests, build or review were used to claim these are working.
