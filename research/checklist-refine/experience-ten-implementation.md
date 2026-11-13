# Ordinary experience refine: shared ten-exchange checklist

Authorization now selects the ordinary experience route until the current role's
`practice_exchange + practice_exchange_gold` reaches the stock daily limit of ten.
Existing exchanges count toward that limit. The separate twenty money exchanges
and opcode 861 premium route are not authorized.

## Original client evidence

- `installed-ArmyStarRefineUI.cs.txt:284-301`: the experience-tab button assigns
  `m_refinetype = RefineType.jingyan`; the assignment itself sends no request.
- `installed-ArmyStarRefineUI.cs.txt:312-335`: shared ten-exchange limit and the
  ordinary quote `(int)practice_exchange.exp * practice_exchange_rate.rate / 100`.
- `installed-ArmyStarRefineUI.cs.txt:462-496`: ordinary opcode 860 callback
  increments `practice_exchange`, debits experience by that quote, and starts
  the practice fly effect. Its success reply has no explicit experience cost.
- `installed-ArmyStarRefineUI.cs.txt:508-543`: premium opcode 861 is separate.
- `installed-ArmyStarRefineUI.cs.txt:360-382,597-621`: money's twenty opportunities
  are a separate quota and use opcode 991.
- `installed-Activity.cs.txt:17-20`: the activity constructor clamps displayed
  progress at the task requirement. A completed 1/1 remains 1/1.

## Implemented boundary

`DailyRefineModel` always quotes the ordinary experience route, including when
daily activity progress is already complete. If stock Show opens the money tab,
the bound `experience` action performs the stock experience-tab assignment on the
GUI thread under the same panel/role/cycle/network/quote lease. It uses no gold
sender. A separate fresh observation precedes the `refine` action.

Each exchange retains the stock 860 callback capture and quote identity checks.
Success requires one returned successful callback, exact experience balance
debit, exact ordinary exchange count increment, unchanged premium/money balances
and counts, and exact practice credit after the fly effect finishes. Activity
progress is clamped at its required value. A pre-completion exchange still needs
the corresponding activity push; an already-complete row can settle without a
redundant push, while any received push is decoded and checked.

An overlapping experience credit during a pending operation prevents exact debit
evidence and stops that call as UNKNOWN. Fresh experience accrued between two
confirmed settlements is also treated as a resource-boundary change. No next
exchange is sent using an inferred debit or an unverified balance.

Python `execute(21, ...)` owns one `open` / optional `experience` / repeated
`refine` / final `close` session. The same panel object, visible generation,
role, cycle and transport remain bound across rounds. Each `refine` has its own
command sequence and freshly sampled quote revision. A separate SETTLED
observation confirms the preceding callback, debit, count and completed fly
effect before the next command can be sent. Native round witnesses reset only
after the next quote passes the final GUI commit check.

At the shared ten-exchange cap, or an unavailable next quote such as insufficient
experience, the settled panel offers `close` and Python independently observes
its closure. User stop, admission refusal, or unknown response/identity/
concurrency/ownership state stops further exchanges immediately; closure is then
left UNVERIFIED unless it was independently observed.
No prior Python result is cached as a reason to reject a later call. Live native
unresolved ownership remains a stopping condition until it is actually resolved.

## Same-panel entry-gate correction

Session `data/object_sessions/6dc0e7e41baa4fdbb95d9399d2a6ce1b` records open
command sequence 193, exchange sequence 197, and close sequence 205. Its retained
refine receipt confirms ordinary count 5, experience spent 161000, practice
credit 235 and verified panel closure, with five shared exchanges still left.
The controller's contemporaneous result reported
`UNAVAILABLE/refine_feature_locked`; the later retained state has already moved
past SETTLED and does not preserve its continuation reason.

`installed-Character.cs.txt:516-522` shows that `checkUI("star_refine")` is a
lookup in the menu-entry dictionary. The original experience button at
`installed-ArmyStarRefineUI.cs.txt:312-335` uses quota, templates and affordability
without rechecking that menu entry. The bridge had applied the entry gate to
every action, so a false sampled menu flag at SETTLED caused the safe-close
fallback even though the original panel could offer the next ordinary exchange.
The feature gate now applies only to `open`. Owned-panel identity/window,
settlement, quote-table identity and final commit checks remain required for
each subsequent request. The available records do not establish which upstream
update removed the menu flag. This correction has not been built or exercised.

## Interfaces for controller integration

Shared action allowlists must accept `experience`; observation and command names
remain `daily-refine-observe` and `daily-refine-command`.

Added view fields, schema 1:

- `practice`: observed practice balance.
- `experience_limit`: exactly 10.
- `remaining_exchanges`: `10 - experience_exchanges - gold_exchanges`.
- `continuation_reason`: at SETTLED, the native gate preventing the next ordinary
  exchange, such as `refine_experience_limit` or `refine_insufficient_experience`.
  An available next exchange has an empty continuation reason. This field is
  supplementary; command names, actions and schema remain unchanged.

Added Python result fields:

- `gold_exchanges_before/after`, `practice_before/after`.
- `shared_exchange_limit=10`, `remaining_exchanges`, `exchanges_completed`.
- `checklist_complete`: shared quota is verified exhausted.
- `operation_id`: one session id shared by all exchange receipts.
- `sequence`, `quote_revision`, `final_revision`: each attempt binds its own
  refine command, fresh pre-send quote and separately observed settlement.
  Sequences strictly increase. The next quote revision may equal the preceding
  settlement revision because that observation also exposes the next quote.
  The aggregate sequence/final revision bind the final close; its quote revision
  remains the last exchange's quote revision.
- `attempts`: only independently settled exchanges, in order, each with
  `status=COMPLETED`, `cleanup=PANEL_HELD`, `ui_ownership_held=True` and empty
  nested `attempts`. These receipts confirm exchanges; they do not claim panel
  closure. The aggregate result alone records `VERIFIED_CLOSED` and ownership
  release after final cleanup.

Existing fields report initial/final observed balances and counters; spent
experience and credited practice are totals of verified exchanges. `COMPLETED`
means the whole quota is exhausted, `ALREADY_COMPLETE` means no exchange was
needed, and partial success followed by insufficient experience is `UNAVAILABLE`
with its verified attempt evidence preserved. `account_progress_observed` means
one or more resource exchanges settled, even if daily activity remained 1/1.

If the live shared quota is already ten before opening, the result is
`ALREADY_COMPLETE`, `cleanup=NOT_NEEDED`, ownership false, zero spent/credit and
an empty attempts tuple. The panel is not opened.

The workflow must dispatch task 21 even when its daily row is already complete,
call this executor once, and validate quota completion rather than expect one
activity increment. It must validate contiguous per-attempt resource/count
changes, same session id, increasing refine sequences/revisions, and the final
independent close. Partial attempts never establish checklist completion.

## Validation scope and remaining evidence

Only original client decompilation and implementation source were inspected.
No tests, review, build, restart, or game actions were performed, as requested.
The tab action, post-1/1 settlement, same-panel consecutive exchanges, cap,
insufficient experience, user stop and concurrent experience branches remain
unexercised at runtime.
Compilation, bridge transport, installed-client execution, and actual quota
completion remain unverified. No build, restart or live run is authorized by
the same-panel implementation request.
