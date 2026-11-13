# Rank arena same-page continuation

Scope: task 9, natural/free challenges for the existing daily money (20000) and
reputation (200) goals. Source implementation only; no tests, review, build,
restart, game/UI operation or network request was performed by this worker.

## Stock evidence

- `research/battle-skip/ArenaUI.cs.txt:492`: `BattleClose` restores the existing
  arena panel with `Visible` and `EnableAll` after a win. Rewards use the panel's
  existing `EnableAll` callback.
- The same method's loss branch hides the panel and opens `BattleValueCompareUI`.
  `native/unity_bridge/build/arena-battle-evidence/BattleValueCompareUI.cs.txt:54`
  binds its close callback to `ArenaSelectUI.arenaMyLoading`. That stock route
  reloads ArenaUI and requests `0x463`; it cannot be omitted without changing
  stock return behavior.
- `ArenaUI.cs.txt:896` updates challengers, cooldown, money, reputation and
  challenge_count from the real `0x460` callback before entering playback.
- `research/battle-skip/BattleUI.cs.txt:1048`: `battle_exit` restores the previous
  root, clears the battling flag, then invokes the arena's `BattleClose`.
- Freshly read installed assembly SHA256 values match
  `research/battle-skip/installed-identity.json`: CSharp `c1c8ebac7ddef4173c873a00774975a5ca366f95b19164c4ca775165eb994911`,
  firstpass `29d8fa2cb55e800ccdf1cdc6c5b274b924ea7d03c989296dfa569731e02a8d87`.
  The ArenaUI and BattleUI evidence copies in battle-skip match the earlier
  arena-battle-evidence copies byte for byte.

## Result and contract

`DailyArenaBattleModel` retains the owned page and operation id between rounds.
After a real callback, exact challenge/reward deltas, correlated activity progress
(until 5/5), closed battle/reward/comparison windows, and a ready original panel,
it exposes `continue` instead of closing. The loss return additionally waits for
its `0x463` callback. Every continuation resamples identity, premium balances,
quota, opponent and cooldown through the existing GUI admission gates before
clearing the proven prior round and invoking the ordinary challenge path.

Additive operation fields `round_settled` and `battles_completed` separate round
completion from `closed`, which still means final UI cleanup. The Python
executor counts completed rounds per owned session and keeps the existing result
dataclass and daily reward/failure/cleanup reporting. The existing arena skip
path is unchanged. Premium purchase and cooldown-clear methods are never called.

The panel closes once at the reward goal, natural quota exhaustion, cooldown or
missing opponent. Cooldown resumption can open a new session later; preserving a
page across a cooldown is outside this change. The original loss comparison
reload remains unavoidable in this implementation.

`daily_workflow.py` does not need a change: its executor result contract and
cooldown result remain compatible. The controller separately added the arena
Busy guard to `DailyScriptAdapter` so task switching retains this ownership.

Validation was limited to source/stock-handler tracing and installed/evidence
hash reads. Compilation, live continuous wins, loss returns, cooldown exits,
actual reward receipts and cleanup are unverified. Existing opcode-only reply
provenance remains a limitation; no ambiguous challenge is retried.
