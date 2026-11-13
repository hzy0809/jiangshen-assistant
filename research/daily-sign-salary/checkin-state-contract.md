# Sign-in state authority and shared reward handoff

Implementation note, 2026-11-07. This note supersedes the sign-in receipt and
private `reward_close` contract in the earlier README. Salary receipt validation
is retained. No tests were written or run; no code review, build, restart,
network request, role switch, reset, or game action was performed by this worker.

## Decisive original evidence

- `SignInUI.cs.txt:570-576`: `cs_all_bonus_get_new` sends the stock empty
  `CSAllBonusGetNew_0x394` request as opcode 916 with its original
  `sc_all_bonus_get_new` callback, guarded by `sended`.
- `SignInUI.cs.txt:580-587`: the original callback owns decoding and resets
  `sended`. `SignInUI.cs.txt:614-617` increments `checkin_times` and sets
  `Global.character_.checkin_today = 1` when `checkin` is asserted.
- `SignInUI.cs.txt:626-631`: only after account mutation does the callback create
  its reward, with `OnClose` bound to that `SignInUI.EnableAll`.
- `AllBonusUI.cs.txt:149-169,246-248`: the arena path supplies `find_back:false`
  and `AllBonusUI.closeArenaBonus`; combined salary uses the same close callback.
  Reputation salary uses its own `ReputationUI.EnableAll` at
  `ReputationUI.cs.txt:533-534`. `ArenaUI.cs.txt:998,1053-1055` passes the exact
  provided close callback into the reward window.
- `BattleResultUI.cs.txt:356-375`: ordinary confirmation hides the reward and
  invokes `OnClose`, with the previous UI fallback when no callback exists.

The installed Assembly-CSharp SHA256 was read on 2026-11-07 as
`C1C8EBAC7DDEF4173C873A00774975A5CA366F95B19164C4CA775165EB994911`;
Assembly-CSharp-firstpass was
`29D8FA2CB55E800CCDF1CDC6C5B274B924EA7D03C989296DFA569731E02A8D87`.
Both match the extraction inputs in `assembly-inputs.json`.

The raw incident artifact
`data/object_sessions/498501bd95d24ff7b86c86b864b3c12b/state.json`, `daily_script`,
records role `55290000000275`, cycle `1793998800`, `checkin_today=1`,
`checkin_times=10`, `request_count=1`, opcode 916, and `mutation_attempted=true`,
while callback/reward confirmation are false and cleanup/busy remain true.
This separates an observed signed state from the helper's local receipt and
cleanup failure. It does not prove all combined rewards or physical UI cleanup.

## State and request contract

Sign-in reads the current `Global.character_`, nonzero role id, and the loaded
game clock through `DailyRefreshGate.ReadClock`, including the refresh-pending
gate. It samples `checkin_today`, then verifies character, role, day, cycle and
clock again. A final identity/day/flag check protects the published observation
even when unrelated UI admission fails. Only literal 0 or 1 is admitted.

The snapshot adds `checkin_state_observed` (bool), `game_day` (uint) and retains
`checkin_today`, `role_id`, `refresh_cycle`. `game_day` is the game's
`TimeUtil._thisDay`; `refresh_cycle` retains the existing 05:00 refresh boundary.
Both must remain equal to the operation baseline, with the same character and
nondecreasing game clock.

- Current state 1 returns `ALREADY_CLAIMED` before old local UNKNOWN/busy state
  in a fresh Python execution, without opening or sending a sign-in action.
- Current state 0 permits the original normal-tab stock route. Native admission
  and Python submission both guard against a second claim in the same operation.
  A missing or ambiguous result is never permission to resend within that run.
- After invocation, a fresh same-role/day state 1 confirms sign-in. State 0
  remains pending and then UNKNOWN; absent, changed or conflicting state is
  UNKNOWN. Sign-in does not install the receipt tap or decode opcode 916.
- `reward_confirmed` and `stock_callback_returned` keep their receipt meanings;
  sign-in does not infer either from `checkin_today`. Salary retains both checks.
- Stop and pause keep their existing controls. The action allowlists include no
  paid or New Year route. Current premium-balance/cumulative-spend changes still
  stop an owned operation as UNKNOWN.

Python `DailySignSalaryResult` adds these defaulted fields without changing the
existing positional fields:

```text
checkin_state_observed: bool = False
checkin_today: int = -1
game_day: int = 0
role_id: str = ""
refresh_cycle: int = 0
```

For task 301, current observed state 1 is the business criterion. A fresh no-op
returns `ALREADY_CLAIMED`; a submitted operation can return `COMPLETED` without
916 receipt fields. The executor still gives owned page cleanup a bounded
opportunity. `cleanup=VERIFIED_CLOSED` requires actual native closed=true,
busy=false and cleanup_pending=false. A confirmed sign-in with unresolved
cleanup reports that fact separately as `UNVERIFIED`; no closed flag is forged.
The controller must handle current-operation premium-spend failures as an
invariant failure even when sign-in itself is observed.

## Popup handoff and remaining integration

The model no longer emits or executes `reward_close` for either route. Shared
popup processing owns exact reward-generation binding and the normal confirmation
interaction. The sign/salary model retains ownership of its parent entry and its
normal `close` action.

The adapter forwards these narrow interfaces:

```text
bool PopupHandoffReady { get; }
bool OwnsRewardPopup(UnityEngine.Component popup, System.Delegate close)
```

Handoff requires a held operation, no queued command/tap, no stop/unknown, a
current owner/day, and no loading or network request. Sign-in needs current
state 1; salary needs its original returned and confirmed evidence. Popup
ownership checks the current exact cache, observed reward component and close
delegate reference, one callback, and the original owner/method:
`SignInUI.EnableAll`, `ReputationUI.EnableAll`, or
`AllBonusUI.closeArenaBonus` for the corresponding route/opcode. An unheld
historical popup is left to the independently scoped shared handler; this
model does not adopt it based on the signed flag.

The controller owns the dedicated executor-wait popup pump, daily workflow
acceptance, build and authorized current-game observation. This worker has not
exercised the unsigned branch, callbacks, actual popup confirmation, day rollover,
or the integrated workflow. Today's account is already signed and was unchanged.
