# Normal user stop: preserve the already-sent battle response

Status: DONE. Source and tests are frozen; no game process, network, deployment or full payload build was used.

The three battle observers now distinguish the normal Lifecycle reason `stop` from other termination. Normal stop still closes automation and clears queued actions. If a battle request was already sent and has no earlier callback error/UNKNOWN, its existing one-shot preflight wrapper remains in place. A matching response reaches the captured native callback once with its original MemoryStream, null arg and cursor. It can establish the native win/loss report and retain its original return callback/page. It never queues another fight, opens an automatic result window, or sends an end request. All other stop reasons keep the wrapper disarmed. Invalid packets, timeout, earlier UNKNOWN, duplicate invocation, and changed role/target/client/socket/stream/handler/cycle/loader/root stay closed.

The new real-guard replay exposed a separate missing Arena boundary: replacing both Global/RootUI clients still left the cached Frame client healthy, allowing stock before the guard rejected the report. The mode preflight now checks DailyTransport.CurrentClient against the captured client before native forwarding. Both direct modes also pin the loader and source/current root; Demon pins the loader/current root alongside its existing role/source/target checks. The client-change counterexample was retained in `mode-suite/arena-client.log`; the corrected counterpart is `fixed-final/arena-client.log`.

## Changed source and interfaces

- `DailyArenaBattleModel.Stop(string detail = "stop")` and `DailyPointRaceModel.Stop(string detail = "stop")` replace the parameterless internal methods, retaining compatibility for existing source callers. DailyScriptAdapter.Stop forwards its reason to those two methods; these are its only two edits.
- DailyMonsterModel.Stop(string) retains its signature and adds the same normal-stop drain admission for the own-demon 0x492 observer. Ordinary building-monster launch paths are unchanged.
- No BattleLaunch, BattleSettlement, or shared preflight API was changed by this assignment.
- Added `NormalStopModeReplyTests.cs`, `NormalStopDemonReplyTests.cs`, and `run-normal-stop-reply.ps1`. Updated the existing stop assertions in BattleModeAdapterTests and PointRaceBattleAdapterTests. BattleModeAdapterTestStubs has the requested real pbdata LEFT_WIN default report and callback stop hooks. DemonPreflightStubs has conditional real-guard formation/native-298 support; its normal regression path remains the existing guard substitute. DemonPreflightTests now supplies the source root to its loader boundary.

## Decisive evidence

The paired control uses the frozen `.cs.before` models and the same current real-guard fixture. `LEGACY_STOP_API` only adapts the old Arena/PointRace Stop() call signatures in the fixture; no old model behavior is changed. All three baseline cases fail the required normal-stop behavior with stock_callbacks=0 and initial_response_applied=false. With the new models, the matching after-win cases each produce stock_callbacks=1 and initial_response_applied=true. See `decisive-before-after.json`, `negative-control-final/*-after-win.log`, and `fixed-final/*-after-win.log`. Baseline compile succeeds and manifests are stable; its three behavior failures are intentional negative controls.

`fixed-final/summary.json`: PASS, 88 scenarios, 1600 checks, source_hashes_stable=true. Each case is a fresh x86 process compiling the production models, BattleLaunch, transport/reflection/clock helpers and pinned real pbdata/protobuf/Ionic decoder. Named mutex/event IPC is real. Arena/PointRace also compile the production BattleSettlement. Demon drives the actual guard path: formation request 298, captured OnAttack, then one native 0x492 request; its scene/stock callback and settlement presentation boundary are detached fixtures.

The stop replay follows source-observed Probe.Finish order: set cancellation/lease loss, call the real BattleLaunch.Stop, call the production idle BattleSettlement.Stop for Arena/PointRace, then model.Stop("stop"). Lifecycle.cs uses RequestStop("stop") for the stop command; DailyScriptAdapter passes that reason. The actual Probe component itself is not executed by these tests. Pause does not call model.Stop and remains distinct.

Coverage includes valid win/loss replies after stop and stop re-entering from inside the native callback, stop after the initial reply, unchanged stream/arg/cursor, native result and original destination, manual native result/exit without autonomous continuation, non-normal stop, timeout and later replay, bad report, wrong target, premium mismatch, earlier UNKNOWN, duplicate calls, role/target/source/root/loader/client/handler/transport/cycle changes. The three `new-epoch-blocked.log` cases reinitialize/select a new epoch while the old request is unresolved: the real guard throws battle_launch_prior_pending_or_unknown before any new native send, then the old matching response still applies once. Changing epoch alone does not invalidate an already-sent old response. The two `retired-wrapper.log` cases transfer the first direct-mode report, let the real guard retire it while creating a new handler, and prove that the consumed old wrapper cannot mutate state or replace the new handler. Demon separately covers replaced handlers and one-shot saved-wrapper replay, but does not exercise a second post-retirement demon launch.

Final targeted regression results:

| Suite | Scenarios | Checks | Result |
| --- | ---: | ---: | --- |
| Real guard normal-stop replay | 88 | 1600 | PASS |
| Existing Arena + Grave adapter/preflight/settlement | 20 | 458 | PASS |
| Existing PointRace adapter/preflight/settlement | 14 | 372 | PASS |
| Existing Demon preflight | 63 | 659 | PASS |

All four final run manifests still matched the current filesystem during final readback (`final-validation.json`). Existing regression suites use their documented guard substitutes; the real guard stop behavior comes from the dedicated replay. Their earlier reports must not be used to claim this IPC coverage.

## Frozen artifacts and remaining limits

`final-hashes.json` gives every edited file and the read-only dependencies. `frozen-source/` contains the final tested source/evidence/script snapshot, and `.cs.before` preserves the three pre-fix models. Per-case logs, compile logs, executable fixtures and dependency copies are under `fixed-final/`. The control is `negative-control-final/`; final regressions are `arena-final/`, `point-final/`, and `demon-final/`.

The tested shared dependencies are the hashes below. BattleSettlement changed independently from the earlier brief; the final run used and rechecked its current hash. This assignment did not edit it.

- `DailyArenaBattleModel.cs`: `e8f7c399f49b3bb8b92386f3c6966c17db67428c498498a054f0b622a8e4de25`
- `DailyPointRaceModel.cs`: `aecaa64e897a600b161c53f4d192fc68175b45527b897bcd4f76964ee21808f7`
- `DailyMonsterModel.cs`: `f186536d515679598d5b84ea69a6a6f1d3cb9cd91a7ea02b0dd87e90bda20097`
- `DailyScriptAdapter.cs`: `ac3790689d4a1d9149551ab7d3fc7eb49421ad1c8af2ebb1fc40736e94d953cc`
- `BattleLaunch.cs`: `55fcf9feaaa317c474e0debbd115c99359e0a50d1227821143f5e6db43fde87d`
- `BattleReplyPreflight.cs`: `b17db6c7ef58479a67d982b90395132c50dcfbae7605b073fd2ba745ce1e7cf4`
- `BattleSettlement.cs`: `c5728411bf672f838e4128f7acce309279fcd9a0986db3d21dba29c365679c72`

Unfinished work outside this leaf: independent review, controller-scheduled full payload build/integration, and any separately authorized live gameplay/device/server/business acceptance. No claim is made for Unity rendering, live dispatch timing, real server cost, physical interaction or deployment. The native stock boundary methods are source-grounded fixture transcriptions, not the running installed game.
