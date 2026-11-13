# Arena free rewards in one page

The installed handlers support successive free rank rewards without returning to
home between rewards. `assembly-inputs.json` records the installed assembly and
decompiler SHA256 values used for the offline source extraction in this folder.

Decisive installed-client evidence:

- `ArenaUI.cs.txt:982`: `BonusGetClose` only calls `EnableAll` and hides the
  loading screen. It leaves the arena page open.
- `ArenaChestUI.cs.txt:61`: the free button disables the current chest page and
  calls `ArenaUI.cs_arena_bonusget_0x462(false, i, BonusGetClose)`. The separately
  named find-back button and its `true` argument are paid branches.
- `ArenaChestUI.cs.txt:95`: `BonusGetClose` re-enables the chest page. It hides
  the chest and invokes its parent close callback only when the entire bonus
  list is empty.
- `ArenaUI.cs.txt:988`: the request uses the selected row's exact time and the
  explicit `find_back` argument. Its success handler removes that exact time,
  creates the reward dialog with the supplied close delegate, and sets `got`.
- `BattleResultUI.cs.txt:366`: ordinary reward confirmation hides the reward
  dialog and invokes its `OnClose`. This is an intermediate reward overlay,
  separate from leaving the arena activity page.

The previous assistant flow required a return to home for every sealed reward.
The native model now offers `CLAIMED` / `ui_disposition=continue_ready` after the
matching free receipt, both activity caches, premium invariants, reward
confirmation, and unchanged page ownership are observed. The old operation's
receipt and bonus time stay sealed in that snapshot.

The existing explicit `rearm` command accepts that exact operation/revision. It
rechecks the frozen bonus-list/selected-row plan and independently qualifies
the next visible free row. It performs no game method invocation. The next
model inherits the page and loader-queue ownership and reports `READY` /
`ui_disposition=retained`, with rearmed operation, revision and sequence
provenance. Its `navigation_pending` remains true even when the original page
was adopted rather than opened by the assistant. A different executor cannot
silently adopt this retained ownership.

The Python executor binds the retained offer to that exact verified handoff,
then creates a new operation ID for the next reward. Each reward still needs
its own opcode 1122 callback, exact receipt time, zero premium consumption,
account-state transition, and reward confirmation. `claimed_bonus_times`,
`operation_ids`, and `claims_completed` include all independently settled
rewards. `page_reuse_count` counts verified transfers that retained the page.

Only the final reward, after a full scan finds no current free rewards, offers
the existing stock page-close sequence. A final chest return to its parent and
the parent's close belong to this one final cleanup phase. Aggregate
`COMPLETED` still requires `VERIFIED_CLOSED` and zero free rewards remaining.
`PAGE_RETAINED` is private to intermediate reward steps; an unfinished run
reports `UNVERIFIED` cleanup. Unknown command outcomes, altered ownership,
changed selected rows, and missing receipt evidence never reissue a reward.

No workflow integration change is needed: the existing aggregate gate already
checks the count of rewards, exact reward times/operation IDs, final cleanup,
and saturated activity progress. The bridge's shared snapshot helper accepts
both closed and retained rearm shapes; the executor supplies the expected mode
from the sealed predecessor rather than inferring it from a reply.

Validation here is source reasoning against the extracted original handlers
and the existing bridge protocol. No tests, test additions, build, review,
game/UI operation, restart, or network mutation were performed. Actual receipt
ordering, installed runtime loading, repeated rewards in one visible page,
and final cleanup remain for the controller's authorized acceptance run.
