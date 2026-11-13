# Battle launch guard delivery

Status: DONE_WITH_CONCERNS. Production source and targeted fixtures are frozen for controller review. No runtime package, game, network or deployment was changed by this assignment. Existing concurrent settlement work in BattleSettlement/BattleLifecycle/Probe and pending-story coordinator work are separately owned.

## Direct result

- Python closes workflow admission, signals an immutable epoch's manual-reset Win32 event and drains the shared named launch mutex before returning successful pause/stop. Cancellation does not acquire ordinary bridge request/I/O locks or wait for a command ACK. Error/abandonment/missing interface cannot acknowledge success.
- Commands capture their epoch before serialized I/O. Resume creates a new epoch; old events are never reset. Native guards retain that captured epoch through formation 298, Campaign ScenarioUI.OnClose and Trial MainUI.stFight. The mutex remains held through each synchronous native Send enqueue. Already-enqueued requests and their native result callbacks remain distinct from permission to launch another battle.
- Formation adapters cover campaign/challenge, grave, normal trial/Boss/training, divinity and building/demon monsters; Arena/PointRace direct sends are fenced. Original mode result/reward/back-navigation code is retained. Demon 0x492 observation attaches only after the real OnAttack has registered its handler.
- A registered battle handler is only `battle_request_observed`. A launch stays unresolved until its exact original response yields a fresh report compared with the pre-launch report and `ReportBound` accepts mode, operation, role, source, target, battle and report identity. Missing/changed/duplicate/timed-out evidence stays UNKNOWN and is not replayed.
- Training 0x370 stays a no-report route. Its exact one-shot native response must have stock `exp > 0`, the same sealmonsterinfo owner, and native-applied six finish flags plus buff. The wire result is recorded without invented semantics. Empty/no-stock-success/timeout remains UNKNOWN, without retry or fabricated report.
- Campaign ambient allowance uses the exact current character's own Capital demon mesh/root/script/parent identity. Ambiguous duplicates produce a blocking gate. Pending-story projection is read-only and requires an exact launch-owned callback; queued GUI continuation rechecks it.

## Validation

`powershell -NoProfile -ExecutionPolicy Bypass -File native/unity_bridge/tests/run-battle-launch-race.ps1 -OutputDirectory research/battle-lifecycle/launch-guard-validation/ipc-handoff`

PASS: 33 Python tests, 1.625 seconds, production BattleLaunch.cs compiled into a separate x86 process. Real Win32 events/mutexes and explicit pipe barriers test cancellation before/inside/after 298, Send already holding the mutex, delayed scenario/stamina, old epochs after resume, native callback duplicates, role/target mismatch, first-handler loss, fresh-report/operation transfer, lease expiry, missing APIs, damaged event handles, abandoned mutex, and 0x370 success/nonzero-result/no-success/timeout. Reflection/transport and stock UI callbacks are detached fixtures. Raw process traces, source hashes and compile output are in `ipc-handoff/`.

PASS: all managed production sources compile against the pinned installed legacy mscorlib and UnityEngine with `/nostdlib+ /warn:4 /warnaserror+` and all existing daily feature defines. This writes only `JSAssistantBridge-compile-only.dll`. Exact arguments, input hashes, source-stability result and log are in `managed-compile-summary.json`, `managed-compile-inputs.json`, `managed-compile.log`.

PASS: Python compileall for battle_launch.py, bridge_client.py, objects.py and daily_coordinator.py.

Existing `python -m unittest tests.test_objects -q`: 24 cases / 16 failures. The same command with the frozen baseline source package produces the same 16 failing test names, all at old fixture readiness/can_verify boundaries. See `python-objects-comparison.json` and both raw outputs. These failures are not claimed fixed. A broader exploratory coordinator run also showed existing stale FakeObjects/schema failures; it is not an acceptance result for this guard.

Independent controller-owned checks reported Campaign ambient 26 scenarios / 52 assertions PASS, and separate mode fixtures cover native RequestEnd and SettledResponse result paths. Those artifacts and conclusions remain owned by their authors.

## Source identity and remaining limits

Full changed-source identities are in `owned-source-hashes.json`; installed client identities are in `installed-source-hashes.json`. Assembly-CSharp is `c1c8ebac7ddef4173c873a00774975a5ca366f95b19164c4ca775165eb994911`; firstpass is `29d8fa2cb55e800ccdf1cdc6c5b274b924ea7d03c989296dfa569731e02a8d87`.

Actual full Divinity/Demon adapter callback chains and every native page's rendering/loading/navigation are UNVERIFIED by this assignment's fixture. Shared-fence tests and compilation are not live acceptance for those branches. Controller must complete independent review, unified build and any separately authorized mode acceptance.

Pause during an already-pending scenario/stamina continuation keeps the old epoch cancelled. Its later suppressed callback remains CANCEL_PENDING, then UNKNOWN on timeout/stop; it does not silently become a new attempt or auto-resume. Clearing old CampaignObserver pending ownership needs an explicit higher-level decision. Nonempty pre-battle scenario automation required a separate coordinator fix, now owned by the controller's story worker.

Python native-object handles close after verified clean stop or known target process exit. UNKNOWN sessions retain cancellation tombstones until retirement; native completed tokens retire before a subsequent launch, while unresolved tokens retain ownership and block replacement. Saved wrappers remain one-shot and fail closed.
