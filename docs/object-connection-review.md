# GUI object connection engineering review

Status: DONE. No remaining blocking finding in the bounded source/packaging review of the pinned version below. This is not real-game acceptance. The controller owns GUI/game validation, connect/call/stop/reconnect, idle heartbeat and close acceptance.

This reviewer changed only this report. No implementation edit, game memory read, injection, input, game action, build or live probe was performed. Host metadata proving the effective engineering role/model/sandbox was not supplied; review was read-only by instruction, not claimed as host-enforced isolation.

## Reviewed identity

Packaged manifest: [manifest.json](../jiangshen_assistant/runtime/win32/manifest.json).

- Build ID: `34cc13532caca93ea9575f91fefd583033a0721ad5b7cf42545f41b94de838ed`.
- Manifest SHA256: `71fd341ebbda96d16278ef341e495ff5260fec549cfcdc8de1a513be6ebce72b`.
- Managed payload SHA256: `15257772268bd61d92e1c127233f046c373be80370a3749681fc331edaeb7c8c`.
- ReceiptStore.cs SHA256: `fed4fb8b6e085855b5e45dd9e716c53b1a5ef52c20aaa0024528d06e314d6322`.
- Managed protocol 2; native protocol 2; bridge ID `jiangshen-assistant-win32-v1`.
- Helper namespace `JiangShen.AssistantRuntime`; assembly name includes its source build identity.
- Fixed session root `G:\development\enjoy\jiangshen-assistant\data\object_sessions`.

Python SHA256 at final readback:

| File | SHA256 |
|---|---|
| app.py | 2bee5cc58c90765699cead16c6a14ebd0ffcc1ad2aa2065c39b9f1f12d09e5d2 |
| hotkeys.py | 0a6c93f8c97d26900be9aeca947bb231c91ec920a7a456812d6f288199014892 |
| windows.py | 95fc3ad2f542f270143af054f051d72ff080a1f8b9f9456d477a1db008d11a96 |
| objects.py | 0bd033012a8909189741641436ecdaa5eabdb3afe242999ac194075c6a90ee1e |
| bridge_client.py | c9181585e08acb750224732db188be9f06f9b22485c8651ebad23a249fcdf559 |
| mono_runtime.py | 9c83c0314d3d674e2ba58018a58282f0d65650bf556ab8744145ba00de42f0a8 |

Scope: object GUI integration points, three new backend modules, production native/managed sources under `native/unity_bridge`, packaged assets/manifest and named targeted checks. Existing unrelated task, settings and screenshot internals were inspected only where needed to understand integration. No daily/battle implementation was reviewed or introduced.

The I/O re-review waited for the controller's unified Python/native freeze. That delta was limited to Python publication/error/reader handling in bridge_client.py and objects.py, their targeted tests, and managed ReceiptStore.Write atomic publication plus the rebuilt sealed package. App/Mono reader and native bootstrap/lifecycle/command implementations retained their previous hashes at that freeze; the later hotkey follow-up supersedes the app identity.

A subsequent separately frozen hotkey review covers only hotkeys.py, windows.py, their tests, and app.py/test_app_lifecycle integration. It supersedes the earlier app identity above; Mono state reader, bridge client/controller, and build `34cc1353...` remain unchanged. No native/bridge implementation review or live game action was repeated for this delta.

## Final criteria

| Criterion | Source/packaging result | Decisive evidence |
|---|---|---|
| Passive 1-second monitoring never starts a payload | PASS | `ObjectController._monitor` uses `MonoRuntime`; explicit `connect` is the only path to host launch. The reader exposes query/read-only process access and bounded allowlisted state. |
| Target identity is fixed before native execution | PASS | HWND/PID/TID/creation validation in backend; expected creation passed to host; host keeps query/synchronize handle and compares creation before Arm; Arm independently checks; callback checks again before Mono access and native self-retention. |
| Game and packaged versions are pinned | PASS | Game/Mono/managed hashes and cache layout checks; manifest admission and file hashes; native payload sealing and exact managed entry/assembly identity; managed model pins supported assemblies before business access. |
| One GUI owns one helper lifecycle | PASS | PID/creation lease plus exclusive `.claim` enclosing every acquire/reclaim/release; helper cross-build OS existence guard and per-assembly session guard. UNKNOWN is not automatically stolen. |
| Commands cannot reuse an old success | PASS | Serialized sequence/nonce/identity checks; terminal ack waits for matching state sequence; action result matches originating action sequence and restoration proof. GUI consumes normalized current result. |
| Ready means a real Unity callback | PASS | Two distinct Update frames and native/managed thread checks; native bootstrap return alone is insufficient. |
| Stop/F8/close restore or report UNKNOWN | PASS | All route to controller stop/close; stop supersedes an accepted action only; original instance/root/character/scene/formation revalidated; bounded recovery then UNKNOWN latch. Close settles in `finally` on persistence failure. |
| Clean same-process reconnect | PASS | OnDestroy resets clean guard before terminal receipt; valid clean autonomous stop settles lifecycle without claiming an unacked action; immutable assets reused without overwriting loaded DLLs. |
| No stale green values or late ready event after rebind/stop | PASS | Generation-bound events and queue invalidation; GUI generation filtering, 4-second passive value expiry, request timestamp/sequence validation and action freshness gate. |
| Runtime packaging independent of research imports | PASS | Production imports contain no research/probe runtime references; packaged assets manifest-verified; fixed snapshot/diagnose/heartbeat/disable-restore/stop commands only. |
| Python contention handling cannot resend an action | PASS | Temporary bytes are written/fsynced once; only os.replace is retried on WinError 5/32/33, using the same path/bytes/sequence under the same request lock. Configured monotonic retry budget is 0.6 seconds. Other I/O is not retried. |
| First request error becomes conservative UNKNOWN | PASS | First surfaced error details are captured once before ledger persistence; later generic errors cannot replace first_unknown. Monitor catches its first bridge-request exception, invalidates action eligibility and stops further bridge polling while passive state reads remain separate. |
| Receipt reader/writer replacement is compatible | PASS | Read-only Windows handle explicitly shares READ/WRITE/DELETE; one handle supplies at most 128 KiB+1 bytes, then strict decoding/JSON parsing. Existing managed target uses ReplaceFileW, first publication a non-replacing MoveFileExW; no target deletion, in-place overwrite or Unity-thread sleep/retry. |
| Completed short F8 presses remain pending for GUI stop | PASS, source/fake API | Dedicated RegisterHotKey(NULL, own ID, MOD_NOREPEAT, F8) message thread latches WM_HOTKEY until consumption; GUI no longer samples the high key-state bit or adds a second rising-edge filter. This is not a real-key acceptance result. |
| Global hotkey resources and failure visibility | PASS, source/fake API | Queue created before registration, exact ctypes signatures, GetMessage -1/0 handling, registration/unregistration on the same thread, owned WM_QUIT and bounded join. Registration/message/release errors remain visible; screenshot helper imports do not register a hotkey. |

These PASS entries describe inspected implementation/packaging properties. They do not prove real-game UI effects, restoration, resource causality or acceptance.

## Findings resolved during implementation

1. **Lease deletion race.** JSON compare-then-unlink permitted clean-ledger contenders or a late old owner to delete a replacement lease. `_lease_edit` now exclusively creates a separate per-PID/creation claim around acquisition, reclamation and release. A crashed claim conservatively blocks that identity.
2. **Stop before client publication.** Cancelling connect between identity validation and client publication could leave `stopping` forever with no helper. Worker finalization settles cancelled/no-client or clean-terminal paths to disconnected; a factory-barrier test covers it.
3. **Old state with new ack.** Independently atomic files can be read across a publication boundary. The consumer now waits for current state sequence and consumes the matching ack's immutable action result; previous PASS cannot establish current success.
4. **Autonomous clean stop.** Lease expiry can destroy the helper before a new request is accepted. A validated clean stopped state releases ownership as a lifecycle result without claiming the unacknowledged action succeeded, allowing reconnect.
5. **Incomplete cleanup schema.** Missing/non-dictionary action no longer counts as clean. No-mutation must be explicit; mutation requires PASS/FAIL plus verified restoration and reconnect allowance. Missing/malformed/UNKNOWN stays unclean.
6. **Creation at native admission.** Native protocol 2 adds expected creation through host, Arm and callback. The synthetic negative changes creation after valid Arm and observes rejection before Mono access or native retention.
7. **Close audit-write failure.** A `mark_unknown` write failure could prevent `_shutdown` settling. Close now uses `finally`, preserves lease and logs persistence failure while reporting UNKNOWN without asserting a durable successful record.
8. **Recovery controls and late events.** GUI stop remains available for UNKNOWN; generation-filtered events prevent old success after lifecycle changes. Normalized results separate operation, restoration and resource comparison.
9. **Command publication error was lost behind a later generic error.** The first live session left a complete seq49 temporary file while seq48 remained published. The bounded Python change retries publication only, preserves the first surfaced exception/error number/sequence/stage, and enters UNKNOWN at the first bridge-request failure instead of attempting another heartbeat/snapshot. A real owned-lock test publishes once after release; a held lock reaches the deadline with old target/new temporary intact.
10. **Receipt read ownership and atomic replacement.** `_open_shared_read` transfers ownership from CreateFileW to CRT descriptor only after conversion succeeds, then to the file object only after wrapping succeeds. Conversion failure closes the native handle; wrapper failure closes the descriptor; normal/read/decode/parse failure paths close the file object. The read does not mix versions using later path reads. Sharing DELETE alone did not make the legacy MoveFileEx(REPLACE_EXISTING) held-target fixture succeed, so this reader change is paired with the production writer change rather than asserted sufficient by itself.
11. **Managed held-target publication.** ReceiptStore.Write closes its complete UTF-8 temporary file before a single ReplaceFileW for an existing target, or a first-publication MoveFileExW without replacement. A false return still throws IOException into the pre-existing conservative lifecycle. The actual production method was compiled into the owned-file test: old handles retain old complete JSON and new opens see new complete JSON; tested share3 refusal retains old target and pending temporary. No generic guarantee that every possible underlying ReplaceFile failure leaves all filenames unchanged is asserted.
12. **Short global F8 missed by sampled key state.** The controller observed a completed short F8 press did not stop a real session using the old 80 ms GetAsyncKeyState high-bit sampling. The replacement registers an owned global hotkey on a dedicated queue thread, coalesces pending stop events under a lock, and consumes them even when TaskRunner is idle. The app advertises global availability only after registration succeeds; failure displays stop-button/Esc alternatives and stops further hotkey polling.
13. **Hotkey thread-start failure bypassed fallback.** Initial hotkey source retained a Thread before start and allowed RuntimeError to bypass WindowError handling, with an unstarted join target left behind. Final source catches Thread construction/start exceptions, records HotkeyError, retains a thread only after successful start, and refuses automatic retry after failure. The added adapter-level fake test verifies visible WindowError, no repeated creation/start, no unstarted join, and idempotent close. This P2 is resolved in the pinned hotkey source.

Decisive source: [lease/receipts](../jiangshen_assistant/bridge_client.py), [controller lifecycle](../jiangshen_assistant/objects.py), [GUI integration](../jiangshen_assistant/app.py), [native admission](../native/unity_bridge/native/bridge.c), [managed lifecycle](../native/unity_bridge/managed/Lifecycle.cs), [managed Update/restore/OnDestroy](../native/unity_bridge/managed/Probe.cs).

## Validation scope and results

Reviewer-run I/O-freeze static checks: all 13 current manifest source/artifact hashes plus all six Python/test hashes in the owned-lock evidence matched files (19 entries, zero mismatches); the four reviewed Python files parsed with `ast.parse` without importing/executing runtime paths. Current hotkey/app identities are verified separately below. Production source scan found no research/probe imports. No build, test execution or acceptance run was repeated by this reviewer.

Backend owner reported 44 focused tests PASS: six runtime, twenty-seven bridge, eleven controller. Reviewed test source covers prior lifecycle gates plus recognized-WinError-only retries, immutable temporary bytes/sequence, permanent denial, fsync failure without publication retry, first_unknown retention, immediate cessation of bridge polling, actual Win32 lock release/hold, JSON limits and both HANDLE/descriptor conversion failure branches. Sources: [runtime tests](../tests/test_mono_runtime.py), [bridge tests](../tests/test_bridge_client.py), [controller tests](../tests/test_objects.py).

[owned-lock-validation.json](../data/acceptance/20261031-object-gui/owned-lock-validation.json) records self-created files only, no game access. A released lock completed after 94 ms/10 attempts with exactly one successful publication and unchanged seq49 bytes. A held lock failed after 610 ms/59 attempts, retained seq48 plus complete seq49 temporary, and preserved WinError 5. The configured retry deadline is 600 ms; scheduling/API-call overhead is visible, not hidden by a strict 600 ms wall-clock claim. Legacy MoveFileEx with a share7-held target failed with WinError 5 in this fixture; ReplaceFileW succeeded while the old descriptor retained old complete JSON and new opens returned new complete JSON.

Native owner reports were read from fixed build artifacts:

- [targeted-validation.json](../native/unity_bridge/build/targeted-validation.json): PASS, exact source hashes, `game_access:false`.
- [managed-lifecycle.json](../native/unity_bridge/build/managed-lifecycle.json): 115 checks PASS on production Probe with synthetic owned Unity/model, no game assemblies/process.
- [native-synthetic.jsonl](../native/unity_bridge/build/native-synthetic.jsonl): PASS for one-shot callback, cancellation, pending-callback timeout/lifetime and creation mismatch rejection. Mismatch record: code 27, `mono_main_match:0`, `retained_until_restart:0`.
- [native-paths.json](../native/unity_bridge/build/native-paths.json): six immutable-cache whitelist checks PASS.
- [receipt-sharing.json](../native/unity_bridge/build/receipt-sharing.json): 814 checks PASS using the actual production ReceiptStore method on owned Windows files: two first publications, 200 share7-held replacements, two share3 refusals, old-handle/new-path complete-version checks. [ReceiptSharingTests.cs](../native/unity_bridge/tests/ReceiptSharingTests.cs) and test.ps1 confirm the production source, not a substitute writer, is compiled into this harness.
- [asset-verification.json](../native/unity_bridge/build/asset-verification.json): PASS for packaged current source identity and x86 artifacts.

The controller's [full-python-tests.log](../data/acceptance/20261031-object-gui/full-python-tests.log) ends with 74 tests in 2.581 seconds, OK; the controller reports exit 0. This was read, not rerun. New-build live connect/action/F8/stop/reconnect/idle heartbeat/close acceptance remains the controller's next gate.

### Hotkey follow-up validation

Hotkey follow-up own static readback: the eight scoped Python/test files parsed with `ast.parse`; actual source hashes match the final identities. No Windows hotkey registration, key injection, game call, UI action or test execution was performed by this reviewer. The existing bridge_client.py (`c9181585...`), objects.py (`0bd03301...`) and native manifest build remain unchanged.

The hotkey/windows owner reports 25 targeted tests PASS; these use in-memory fake message APIs and native function bindings, not physical key presses. Reviewed cases include completed short-press latch/coalescing, unrelated message rejection, duplicate lifecycle calls, same-thread release, occupied registration, GetMessage failure/quit, unregister failure, startup timeout, failed quit wakeup, non-Windows import behavior, ctypes argument binding and the added Thread.start failure case. [test_hotkeys.py](../tests/test_hotkeys.py) SHA256 is `686b5da4643ab6eb5524c248d2dc7b0221749e46ad627b71f2d3d72b6fa6fa4e`.

The controller reports 13 app lifecycle tests PASS, including registration failure visibility, consumed short events without a second edge filter, runtime hotkey failure stopping further polling, and close preserving object cleanup on hotkey release error. Existing idle TaskRunner stop still calls objects.stop independently. [test_app_lifecycle.py](../tests/test_app_lifecycle.py) SHA256 is `06b0e703989ec57c537e2017a2445e0072a5adb7d78bc8c501ec1cf0e472ce70`. [test_windows.py](../tests/test_windows.py) includes running windows.py directly with invalid arguments to validate its sibling hotkeys import before any capture API is reached.

The final combined [final-python-tests.log](../data/acceptance/20261031-object-gui/final-python-tests.log) records **96 tests in 2.688 seconds, OK**; controller reports exit 0. The prior 95-test run is retained separately as hotkey-before-review-fix-tests.log and is not the final validation identity. No suite was rerun by the reviewer.

Close order is explicit: first request object service shutdown, then close the hotkey adapter, then wait for object/capture/task shutdown before destroying Tk. If WM_QUIT posting or same-thread unregister/join cannot be confirmed, the UI logs that error and relies on process exit for release, rather than claiming clean hotkey teardown. Direct screenshot entry uses a sibling import when `__package__` is empty and creates no hotkey registration as part of capture.

The controller reports the old sampled-key session stayed ready until an actual GUI close, then reached stopped seq251 with component destroyed/reconnect allowed, lease empty and Pythonw exited. This is close evidence for that session, not proof that the new global-hotkey implementation handled a real short press. A final real-key F8/stop/restoration/reconnect check for the new app identity remains controller-owned and UNVERIFIED in this source-only review.

## Original live evidence and causal boundary

[first-session-result.json](../data/acceptance/20261031-object-gui/first-session-result.json) and the frozen [02-session1-clean](../data/acceptance/20261031-object-gui/02-session1-clean) files belong to the **previous** build `3fdfc096...`, not the new package. The record supports two-frame readiness, seq40 action/restoration PASS and fourteen observed fields equal; steady polling is FAIL because seq49 was not published. Raw command/temporary files show seq48/seq49 respectively, ack/state remain at 48, and the final state is stopped with heartbeat_timeout. Lease-expiry cleanup and later explicit clean readback are separately recorded PASS.

The original first I/O exception and WinError were not retained (`first_unknown` is absent). Therefore the precise original error code/cause remains UNVERIFIED. Owned-file WinError5 reproduces a plausible publication-failure shape; it does not prove that error code or a specific reader caused the live failure. No managed-side reverse-direction live receipt failure was established. The new reader/writer interoperability tests are direct owned-file evidence for the revised mechanisms, not retroactive proof of a live root cause.

## Limits and unfinished work

No open source-review blocker remains for this pinned version, including the resolved hotkey thread-start P2. The final new-app real-key F8 acceptance is controller-owned and UNVERIFIED here; bridge/I/O live results must be judged by their own session identity, rather than upgraded from source/fake-API checks or previous-build evidence. The fixed action remains only existing MainUI Disable/Enable restoration. Generic game button execution, daily/battle automation and arbitrary reflection/RPC remain outside scope. Numeric resource equality is independent from restoration and does not establish network or consumption causality.

UNKNOWN or crashed claim can require normal game restart. Native code/managed assemblies remain loaded until process exit; component destruction does not unload them. Passive reads remain non-atomic despite identity consistency checks.

The previous scalar-only inheritance caveat is superseded for the full pipeline: prerequisite type stage explicitly checks `Character.parent == _character`. Stage read accounting remains distinct; scalar counts are not the total across assembly/type/scalar stages.

## Final frozen live receipt readback

Status: PASS for the four named receipt sets; no identity, sequence or cleanup contradiction found. This follow-up supersedes the earlier receipt-level pending labels. The controller performed all GUI/game/key actions; this reviewer only read copied state/ack/session/capture/events files, without running UI, game code or another probe.

All four sets consistently identify PID **20976**, creation FILETIME **134378576229429474**, assembly **JSAssistantBridge_34cc13532caca93e** and build **34cc13532caca93ea9575f91fefd583033a0721ad5b7cf42545f41b94de838ed**. Each nonce agrees across state, ack, ledger, capture and every event. Final command/state/ack/ledger sequences agree; stop ack is COMPLETED, state/ledger is stopped, component_destroyed and reconnect_allowed are true, and each capture records `lease_files: []`. Accepted command sequences are contiguous in each event set. Distinct new nonces reach bootstrap/readiness in the same unchanged game process, supporting same-process reconnect.

| Frozen case | Session nonce | Stop / action sequence | Receipt result |
|---|---|---|---|
| [05-fixed-disconnected](../data/acceptance/20261031-object-gui/05-fixed-disconnected) | 75b3208dd92346e384124b8a4ab1dfd5 | 93 / 54 | PASS; readiness to stop about 116.09 seconds, action restored, fourteen sampled fields equal. Disconnect trigger is controller-observed. |
| [09-final-f8](../data/acceptance/20261031-object-gui/09-final-f8) | 69e248db3352425b95bdda83c9ccd057 | 20 / 16 | PASS; natural restore at 19:08:39.806562 UTC preceded stop at 19:08:43.5277767. Supports the controller's global-delivery test, not an action-interruption claim. Fourteen sampled fields equal. |
| [10-final-f8-during-action](../data/acceptance/20261031-object-gui/10-final-f8-during-action) | 4718861ff51648e4ac3c3bc835282bc6 | 14 / 13 | PASS; action at 19:10:41.8133068 UTC, stop-driven restore at 19:10:47.0674440, about 5.254 seconds later and before the normal six-second hold. Restore frame 173086 to verified-result/destruction frame 173088 supplies two further frames. Fourteen sampled fields equal. |
| [11-final-close](../data/acceptance/20261031-object-gui/11-final-close) | 81165c70f7c8456d8a9153c0c9bc7393 | 10 / 0 | PASS clean no-action close: NONE, mutation_attempted=false, no_action=true; destruction at 19:11:52.7384177 UTC. restore_verified=false and no resource comparison are N/A here, not failed restoration. |

For the three action cases, state, ack.result.action and capture.action agree; all fourteen comparisons have equal before/after values and true final/every-sample equality. Resource causality and visual fields remain separately UNVERIFIED in machine receipts. The actual F8 presses, foreground window, physical appearance, Pythonw3128 exit and absence of a hotkey-release warning come from the controller's tool/log observations, not independent observation by this reviewer or an OS audit contained in these four receipt sets. Empty lease is the capture-time file inventory, not a new live filesystem inspection.

These new-build receipts remain separate from first-session `0a41ee0f...`/build `3fdfc096...`, whose steady-poll failure and missing original WinError remain historical facts; they are not erased or retroactively assigned a proved sharing-lock cause.
