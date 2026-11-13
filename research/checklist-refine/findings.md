# 炼魂 checklist evidence and pending decision

Historical investigation below predates the user's ordinary experience ten-cap
authorization and same-panel request. Current implementation and evidence
contract are documented in `experience-ten-implementation.md`; the old pending
choice and close-between-exchanges recommendation no longer apply.

Source: installed `C:\Users\yu\AppData\Local\JiangShen\pc\js_Data\Managed\Assembly-CSharp-firstpass.dll`.
Identity: `installed-identity.txt`. Decompiled source is retained alongside this note.

## Established behavior

- `installed-ArmyStarRefineUI.cs.txt:254-264`: stock Show chooses experience after 20 coin exchanges, or when coin feature 91 is locked; otherwise it chooses coin. No free-refine route exists in this panel.
- `installed-ArmyStarRefineUI.cs.txt:312-335`: ordinary experience exchange is available while `practice_exchange + practice_exchange_gold < 10`, costs a live level/ordinal-dependent `exp * rate / 100` quote, and invokes opcode 860.
- `installed-ArmyStarRefineUI.cs.txt:338-351,508-543`: the separate premium route invokes opcode 861 and debits gold. It is outside the authorized ordinary-resource routes.
- `installed-ArmyStarRefineUI.cs.txt:360-382,597-621`: coin exchange is limited to 20 daily opportunities, uses a level/ordinal-dependent money quote, invokes opcode 991, increments `practice_money_exchange_times`, and credits practice through the stock fly effect.
- `installed-Activity.cs.txt:17-20`: displayed/cached daily activity progress is clamped to `temp.cond_param`. A completed 1/1 cannot become 2/1 even after another successful refine.
- `installed-Character.cs.txt:1848-1852,1880-1896`: each activity push constructs a new Activity and updates its reward listing. The client does not guarantee that every post-completion refine receives a new activity push.

## Existing stopping and evidence constraints

`DailyRefineModel.cs` currently skips quote sampling at `Current >= Required`, returns `refine_activity_complete` from Gate, and requires an activity push plus exact `Current + 1` to settle. Python also refuses `before >= required`, reports `progress_after=current+1`, and requires this increment for completed evidence. Simply deleting the completion gate is insufficient and would cause an unknown outcome after a successful post-completion exchange.

The controller currently only dispatches PENDING daily items and validates coin-only debit/count evidence. A checklist extension must dispatch completed daily rows deliberately, accept clamped activity progress after completion, validate the selected ordinary-resource route, and stop after the finite stock quota or an unavailable/unsafe quote. It must keep a fresh operation and verified panel closure between exchanges, and use the resource exchange count (not daily progress) to demonstrate forward movement.

## Pending user choice

The controller has asked whether ordinary resources should only satisfy the daily counter or use all allowed daily opportunities. Until the answer arrives, no production source has been changed and no resource consumption beyond the existing daily goal has been enabled.

If all ordinary opportunities are authorized, the smallest change keeps the stock route choice and current live quote/affordability checks, removes the daily-counter stop, clamps expected activity progress to Required, and permits a successful exact resource debit + exchange-count increment + practice credit to settle an already-complete daily row without requiring a redundant activity push. Gold invariants and opcode restrictions remain in force. It stops on insufficient ordinary resources; it does not rewrite the stock tab to bypass an unaffordable coin quote.

No tests, build, review, restart, or game actions were run, as requested.
