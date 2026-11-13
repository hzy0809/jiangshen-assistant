# Sign-in interruption: Python flow evidence

Observed local artifacts on 2026-11-07, without running tests, reviewing code changes, building, restarting, sending game commands, or operating the game UI.

## Result

The 09:50:51 daily run ended at 09:50:53 with `UNKNOWN / sign_salary_receipt_owner_changed`. It did not remain in the Python executor's 90-second wait. One native sign-in request was attempted; the persisted game values show today's sign-in already recorded. Normal callback confirmation and UI cleanup were not established. Python stopped the remaining daily work because the native outcome was unknown, as its existing contract requires.

No Python implementation change is supported by this incident's evidence. The native receipt-owner failure is assigned to the native-model owner. The controller owns cleanup and any new live run.

## Run identity and raw evidence

- Assistant log: `data/logs/assistant.log:699` starts daily work at local 09:50:51; lines 723 and 725 show task end and `UNKNOWN / sign_salary_receipt_owner_changed` at 09:50:53. Workflow run ID: `bf843da6226544d38a013decc461ffd7`, generation 7.
- Object session: `data/object_sessions/498501bd95d24ff7b86c86b864b3c12b/`.
- Process: PID `43296`, creation FILETIME `134384898163153388`, character `55290000000275`.
- Native assembly: `JSAssistantBridge_f0784d7bee5e207b`; session build ID `f0784d7bee5e207baf1724a9b252edb1ea32ca3116cab2cf633e6c654482758c`.
- `probe.events.jsonl:5-13`: bridge commands progress from sequence 1 at 01:50:52.0302966 UTC through stop sequence 9 at 01:50:53.9266631 UTC. The events retain command names, not individual daily-script action payloads; they alone cannot establish which action each sequence carried.
- Final `state.json:1`, `daily_script`: task 301, route `signin`, operation `4674cd1ad0bf4071a240b3d18f071906`, cycle `1793998800`, `opcode=916`, `request_count=1`, `mutation_attempted=true`, `checkin_today=1`, `checkin_times=10`, `availability=ALREADY_CLAIMED`, `stock_callback_returned=false`, `reward_confirmed=false`, `cleanup_pending=true`, `closed=false`, `busy=true`.
- Final script status/reason is `STOPPED / stop` because coordinator teardown followed the original unknown outcome. The earlier reason remains in the assistant log. `ack.json:1` confirms bridge stop sequence 9 completed; that confirms bridge teardown, not reward/UI cleanup.
- `session.json:1`: `state=stopped`, `component_destroyed=true`, `last_command_seq=9`, no pending command. This distinguishes ended helper ownership from the unresolved game overlay.

The observed final `checkin_today` and count support a recorded sign-in, but the final artifact alone does not preserve the pre-request count or the response bytes. It therefore does not independently establish every combined reward, exact count increment, or callback semantics. The controller separately observed the signed-in UI and reward window; this worker did not operate or inspect the UI.

## Propagation and restart behavior

1. `objects.py:985` observes native daily-script state from the exact command acknowledgement's result. `objects.py:1000` submits through the current campaign owner and admission gate. `bridge_client.py:2020` binds acknowledgements to command sequence/name/status; `bridge_client.py:2729` sends the fixed daily-script fields.
2. `daily_sign_salary.py:95` immediately returns a same-operation native `UNKNOWN`; it does not resubmit a claim or turn `availability=ALREADY_CLAIMED` into success after that unknown.
3. `daily_workflow.py:390` returns the whole workflow on `UNKNOWN`, `STOPPED`, or unverified cleanup. Task 302 and following rewards are downstream of 301 in the loop at line 418, so they were intentionally not reached in this run.
4. `daily_coordinator.py:179-197` receives the workflow result, releases ownership/connection, clears active/paused state, and publishes the terminal result. Log termination and native stop match this path.
5. A fresh clean run always executes synthetic task 301 followed by 302 and 202/201/203. Existing Python code does not skip the routes based on old saved completion. Before submitting a new operation, `daily_sign_salary.py:98-106` reads current eligibility, refuses unresolved busy ownership, and returns `ALREADY_CLAIMED` without a mutation if current native eligibility says already signed. The workflow treats that as a normal no-op and proceeds to salary, which independently reads its current eligibility.

A restarted helper cannot safely adopt the old reward window solely from today's sign-in flag. Current native ownership checks can reject foreign/unbound overlays before publishing clean eligibility. Clearing that overlay is a separate controller-owned prerequisite; suppressing Python's unknown/cleanup gate would hide the incomplete operation and is not justified.

## Competing explanations and remaining limits

- Python timeout or frozen loop: contradicted by the roughly two-second start-to-terminal log interval and explicit native failure reason.
- Transport/acknowledgement deadlock: not supported; the retained command sequence reached native stop acknowledgement, and the logged failure is native receipt interpretation. Intermediate ack payloads are overwritten, so their exact content is unavailable here.
- Python incorrectly assumes sign-in also completed all salary rewards: contradicted by the unconditional independent 302 observation on a clean successful/no-op 301 path.
- Native callback ownership rejection: directly supported by the exact logged reason, emitted from `DailySignSalaryModel.Receipt`. The native owner is diagnosing and repairing that path; this report does not duplicate that repair or claim runtime acceptance of it.

## Next controller-owned action and acceptance

Integrate the native correction, resolve the existing reward/entry UI through an authorized owner, then start a fresh session. Observe 301 report current already-signed eligibility with zero new 916 requests, progression into independent 302, and subsequent daily work. Preserve the new session identity and action/receipt evidence. This worker performed no live acceptance, so progression after the native correction and final daily completion remain unverified.
