# Wood, stone and grave checklist evidence

Read-only offline ILSpy extraction from the installed client on 2026-11-06. Source assembly identities are recorded in `assembly-inputs.json`. The `.cs.txt` files are raw decompiler output and are not rebuilt game sources. No game action, tests, code review, build, restart or acceptance run was performed.

## Decisive source evidence

- `Database.Activity.cs.txt:17-24`: activity rows clamp `condition_param` to the requirement. A repeated gameplay action cannot be required to produce a fresh daily count increment after the cap.
- `WoodWarUI.cs.txt:346-382`: `can_take` selects the reward state; otherwise free fast challenge is available while `fast_challenge_left_times != 0`.
- `WoodWarUI.cs.txt:492-569`: the reward operation is a separate 614 request and its reward window's close callback requests fresh 612 info. Fast completion is 616, replaces the player status and opens its dedicated result, or changes to the reward state.
- `WoodWarUI.cs.txt:619-640`: `level_finished` causes the stock close callback to open one of two message windows. The follow-up implementation closes only that newly shown, exact stock prompt, with no OnOK/OnClose callbacks, no cancellation and no gold confirmation.
- `EnchantUI.cs.txt:670-709`: stock 825 only sends with an existing `sliver_pick > 0`; an OK callback consumes exactly one silver pick. `EnchantUI.cs.txt:453-548` shows that replenishment is a separate confirmation and 375 request which deducts gold. This evidence does not justify buying picks or selecting a premium dig.
- `GraveChooseUI.cs.txt:640-708`: fresh entry is disallowed when finished today unless an outstanding boss can be continued. Zero attempts prevents a new run only when `checkGraveFinish()` is true; existing unfinished runs can continue with zero attempts. An outstanding boss at finish 5, positive soldiers and reseted != 1 remains available.
- `Database.GraveCampaign.cs.txt:19-71`: the loaded campaign has five ordinary troops and a sixth boss; the 833 response sets the actual remaining attempts.
- `GraveLevelUI.cs.txt:451-489`: win progression increments finish; finish 5 sets today's completion, while the boss can remain; finish 6 finishes the grave and can unlock its successor.
- `GraveLevelUI.cs.txt:506-546`: defeat preserves enemy survivor state and opens a failure message. The assistant now stops on that defeat rather than automatically replaying the same target.

## Changed files and interfaces

Implementation ownership was limited to these four files, now frozen:

- `native/unity_bridge/managed/DailyWoodModel.cs`
- `jiangshen_assistant/daily_wood.py`
- `native/unity_bridge/managed/DailyGraveModel.cs`
- `jiangshen_assistant/daily_grave.py`

Stone files were not changed: the controller paused consumption beyond the daily requirement pending the user's resource decision.

Wood opens a stock info request even when daily activity count is already 1. Each free challenge has an operation identity and its own info / challenge / result close / panel close cycle. The native repeated-action witness is the exact bound 616 callback, current player/cache identity, unchanged gold and quota decrement by exactly one. Once the activity count has already reached its cap, unchanged daily count no longer prevents settlement. Activity pushes, if received, still have to agree with both caches. The attempt key includes role, server, daily cycle, quota epoch and quota before mutation; the same observed opportunity cannot be retried.

`DailyWoodResult` adds `checklist_complete: bool`, `remaining_quota: int | None`, `free_attempts: int`, `rewards_claimed: int` and `reward_pending: bool`. `execute()` drains successive verified free attempts and pending rewards internally. It aggregates mutation/progress witnesses and the initial progress baseline. Only clean closure with zero free quota and no pending reward sets `checklist_complete`. The native snapshot also adds `reward_pending` and `reward_confirmed`.

The follow-up implementation adds `claim_reward`, `close_reward` and `dismiss_prompt`. Pending rewards use the exact stock 614 handler; its nonempty repeated-bonus response must agree with a newly assigned stock reward list and the exact `closeRewardResult` callback. After the owned reward window is visible, the assistant hides it and invokes that original callback, which sends 612. The refreshed player must have `can_take=false` and unchanged free quota before `reward_confirmed=true` is published. Reward grants may increase gold or bound gold, but neither balance may decrease and `gold_consume` must stay unchanged. Unexpected changes stop as unknown.

The reward stages are `CLAIMING -> REWARD -> REFRESHING -> CLOSING -> COMPLETED`. They have independent reward confirmation; a reward claim alone does not falsely claim a newly earned daily point. Special fast results use `RESULT -> CLOSING -> PROMPT -> CLOSING -> COMPLETED`. The prompt must be newer than the result-close invocation, have no OnOK/OnClose callback, no cancellation and no gold flag, and match either the localized `no_dummy_troop` message or the original `zi10` picture and root. The original `MessageBox.ButtonOK(false)` closes it. Unknown or unrelated windows are not dismissed.

The ordinary 613 battle path is not newly automated. `WoodWarUI.cs.txt:164-224` exposes it through `BattleFormation` / `OnAttack`, with no finite attempt counter or local resource deduction. The button at lines 368-370 uses only `IsEnable()`, whereas the fast button explicitly checks the remaining free quota. `game.net.data.StoC.SCDummyChallengeFight_0x265.cs.txt` contains report, starting turn indexes and optional reward status; `CharacterDummyChallenge` exposes only the fast remaining-times field, and `_dummy_challenge_setting` has `fast_challenge_times` alongside turn and refresh settings. Thus source shows an ordinary battle entry without a displayed price, but does not establish a separate finite, exhaustible pool or a safe rule to repeat it until depleted. Server charging and ordinary cooldown semantics are unexercised. Treat this as a documented boundary, not proof that every possible ordinary challenge was executed.

Grave no longer closes merely because its activity count reached the daily requirement. `daily_grave.py` sets `checklist_mode = True`; the controller owns the corresponding hook in shared `daily_trial.py`. The native snapshot adds `checklist_complete`, `grave_finish`, `boss_visible` and `stop_reason` and retains `remaining_attempts` and `soldiers`.

Grave terminal states:

| Observed boundary | Before closure | After verified stock return and panel closure |
| --- | --- | --- |
| finished today with no outstanding boss; no open grave; no attempts and stock needs a new attempt | `CLOSING`, `next_action=close`, busy / cleanup pending true | `COMPLETED`, `checklist_complete=true`, busy / cleanup pending false |
| finished five ordinary targets and no boss, or finished the sixth target, with today's finish recorded | same | same |
| defeat; soldiers exhausted; unexplained missing target | same | `BLOCKED`, `checklist_complete=false`, busy / cleanup pending false |

The stock return can queue the grave selection window after loading the home scene. The existing 500ms return wait and repeated close action remain; the terminal snapshot is only published after this closure boundary.

## Controller integration and limits

- Invoke wood and grave even when the daily task count is already full.
- Accept checklist completion from the explicit checklist witness and clean closure. Do not require `progress_after == progress_before + 1` once the daily count is capped.
- Preserve `UNKNOWN` and cleanup-pending stops. A verified daily point is not proof that all free activity content ran.
- Shared grave executor completion must require `checklist_complete=true`, `busy=false` and `cleanup_pending=false`, independent of daily count.
- Stone remains unchanged pending the separate resource decision.
- The controller owns adding the three new wood actions to shared `Protocol.cs`; Python transport validators import the action/state definitions from `daily_wood.py`. No additional shared file is owned by this leaf.
- Source implementation is unvalidated: the user excluded tests and review, and no runtime or server behavior was exercised. Ordinary 613 battle remains outside the finite-free-quota automation; mismatched reward refresh, quota, gold or prompt identity stops explicitly.
