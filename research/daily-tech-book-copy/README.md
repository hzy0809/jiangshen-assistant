# Current ordinary 抄录 route

Source-only implementation, 2026-11-06. This assignment did not write or run tests, review code, build, restart the assistant, or operate the game. Runtime behavior remains unverified. Shared protocol, adapter, build and workflow scheduling belong to the controller.

## Identity and causal evidence

The user's 抄录 names an actual separate game feature: `MainUI` constructs `icon_chaolu` / `tech_book_copy`, and `_techbookcopy()` opens `TechBookCopyUI`. It is not 炼魂. Before this assignment, the assistant's source, saved object-session JSON and available rotating logs contained no `tech_book_copy` / `TechBookCopy` / 抄录 route. Consequently, this evidence establishes a missing automation route; it does not establish which historical manual action the user had to repeat.

Raw game files in this directory were extracted offline from the installed assemblies with the existing `research/unity-object-layer/tools/ilspycmd-9.1.0.7988/tools/net8.0/any/ilspycmd.dll --disable-updatecheck -t TYPE ASSEMBLY`. Exact assembly hashes are in `assembly-inputs.json`. The independent game evidence defines both the iteration bound and premium boundary:

- `MainUI.cs.txt`: `_techbookcopy_count()` returns template `free_copy_times - character.tech_book_copy_free_times`; `_techbookcopy_check` delegates to `checkUI("tech_book_copy")`.
- `TechBookCopyUI.cs.txt`: `Web_OnGUI` computes `max(0, tech_book_copy_finish_time - TimeUtil.Now)` and permits the free button only when this is zero and `tech_book_copy_free_times < free_copy_times`. That button invokes **only** `cs_tech_book_copy_free_0x726()`.
- The successful stock 0x726 callback increments `tech_book_copy_free_times` exactly once, replaces `tech_book_copy_finish_time` from the response, and credits `tech_book_copy_num` residual pages. The finish timestamp therefore gates the **next** free copy in the current UI, rather than deferring receipt of this copy's reward.
- Noncritical rewards are credited inside that callback. Critical rewards are stored in `techBookAdd`, animated in `Update`, and credited later in `Web_OnGUI`. The implementation keeps the exact owned panel open until the expected account credit is visible and its animation/pending-credit collections are empty.
- `RootUI.cs.txt`: `sc_player_refresh_fiveclock_0x24` resets `tech_book_copy_free_times` to zero. Each operation is bound to the current server day and 05:00 refresh cycle, so it does not cross that reset.
- `Database._tech_book_copy_setting.cs.txt` types `free_copy_times` as a byte. The actual live template value is used, with no assumed fixed daily total. A single operation can submit at most `initial free_copy_times - initial free_times` requests, and every successful request must advance the exact account count once.
- `game.net.data.CtoS.CSTechBookCopyFree_0x726.cs.txt` is an empty request. Its response has required operate/finish-time fields and optional number/critical fields. The model observes the exact stock registered callback and its normal return; it never synthesizes a game packet.

The current file still contains 0x728 `free_get` and `is_tech_book_copying`, but the current `Web_OnGUI` does not call that method or use that flag for free-button eligibility. The flag is exposed diagnostically and must remain unchanged by 0x726. No obsolete harvest command is introduced. The gold button uses 0x727 and an explicit consume confirmation; neither that method nor any paid completion is in the action allowlist. Gold, bound gold and cumulative premium consumption must stay unchanged throughout an owned operation.

## Owned interfaces

Files owned by this assignment:

- `native/unity_bridge/managed/DailyTechBookCopyModel.cs`
- `jiangshen_assistant/daily_copy.py`
- This evidence directory.

Native task ID: **303**. Route string: **tech_book_copy**. Native GUI actions: **open**, **resume**, **copy_free**, **close**. The shared observation action is **observe**.

Native adapter interface matches the sign/salary model:

```text
Capture(Model model, int task, bool admissionBlocked)
Queue(int task, ulong role, string id, int sequence, int revision, string action, out string why)
ExecuteGui(DevelopmentPublication publish, DailyMonsterInvocationGate begin, out string why)
Json()
Stop(string reason)
NotInvoked(string reason)
Busy / Revision / MutationAttempted / GuiInvocationAttempted
```

Retain the model while `Busy`: this includes pending GUI commands, owned panels and callback observation. Call `Stop` at session cancellation/teardown. `DailyDivinityReplyTap` is an existing compile dependency. A genuine unresolved owned request remains `UNKNOWN` and busy; an old unowned local failure does not block a fresh live offer.

Python integration:

```python
from .daily_copy import DailyTechBookCopyExecutor

step = DailyTechBookCopyExecutor(
    objects, token, stopped=stopped, paused=paused, wait=wait,
).execute(303, character_id=role_id, expected_cycle=cycle)
```

It uses the existing `observe_daily_script` / `submit_daily_script` methods. One call processes currently ready ordinary copies, waits only for their callback/animation/cleanup, then closes and returns when the next free copy is cooling down or the live free limit is reached. The workflow owns any longer wait and must not retain the game's UI while waiting.

Flat schema 1 publishes `task_id`, `route`, `role_id`, `refresh_cycle`, `operation_id`, `status`, `reason`, `next_action`, `availability`, `mutation_attempted`, `stock_callback_returned`, `reward_confirmed`, `cleanup_pending`, `closed`, `busy`, `copies_completed`, `request_count`, `free_used`, `free_limit`, `free_remaining`, `finish_time`, `server_now`, `next_ready_seconds`, `copying`. `next_ready_seconds` always means `max(0, finish_time - server_now)`; a zero remaining quota still wins over a nonzero cooldown.

`DailyTechBookCopyResult` contains `status`, `reason`, `mutation_attempted`, `reward_confirmed`, `stock_callback_returned`, `cleanup`, `operation_id`, `free_used`, `free_limit`, `free_remaining`, `finish_time`, `server_now`, `next_ready_seconds`, `copying`, `copies_completed`, `refresh_cycle`, `availability`.

- `COMPLETED` confirms the currently processed free copies, their account credits and `VERIFIED_CLOSED` cleanup. It does not assert the whole day's free quota is exhausted. The separate availability and counters answer that question.
- `WAITING_COOLDOWN`, `FREE_LIMIT_REACHED`, `CONDITION_NOT_MET` are ordinary no-op results when no current free action is available. Untouched UI returns `NOT_NEEDED` cleanup.
- `READY` is an eligibility value. During an owned operation, `PENDING` includes native loading/reply/animation; `CLOSING` means its panel is being closed.
- `UNKNOWN`, `STOPPED`, `BLOCKED`, `UNAVAILABLE` preserve the failure boundary. An uncertain copy request is never automatically resent. An owned unresolved operation has `UNVERIFIED` cleanup.

For the user's waiting choice, schedule another operation only after reading the current role/day/availability again, when free attempts remain. A current `COMPLETED` result with `availability == WAITING_COOLDOWN` supplies the next due time without holding the panel. The controller must bind any delayed continuation to the original role and server cycle, and honor stop/pause. Prior local completed/error statuses do not replace live quota eligibility.

## Unverified limits

No runtime snapshot currently proves this route's live free count, cooldown, unlock or callback behavior. The source does prove the real count/cooldown conditions and the ordinary stock request path. Critical animation credit, stock callback registration, panel ownership/cleanup and user-selected delayed continuation require separately authorized runtime acceptance. No tests or build were used to claim them working.
