# Idempotent normal stop follow-up

Status: DONE. Three model Stop transitions and their dedicated stop fixtures/runner are frozen. No other production source or regression fixture was changed in this follow-up.

A repeated normal Stop now preserves only an admission already granted to the same pending battle response. The first Stop still requires no earlier UNKNOWN/failure; subsequent Stop calls require the existing permission and the model's original normal-stop reason. A non-normal reason, independent UNKNOWN/error, timeout, invalid settlement, completed callback, or missing pending request revokes permission. Later normal Stop cannot restore a revoked admission. Existing packet, role, target, transport, handler, root and premium preflight remains in force.

The old frozen models were compiled unchanged with the current test fixture, real production BattleLaunch and real named mutex/event IPC before changing the models. Stop -> Stop -> valid win/loss yielded six intended behavior failures (Arena, PointRace, own Demon), all stock_callbacks=0, all source hashes stable. With the new models, each paired case yields stock_callbacks=1 and initial_response_applied=true; the original stream/arg/cursor, native report outcome and manual native return are checked. See decisive-before-after.json and negative-control/*repeat-stop*.log paired with fixed-first/*repeat-stop*.log.

The real-guard full suite is PASS: 127 scenarios / 2434 checks, zero failures, stable source manifests. The 39 added cases cover the three modes across repeated stop before win/loss, double stop re-entering inside stock for win/loss, normal -> abnormal, abnormal -> normal, normal -> abnormal -> normal, timeout/error -> normal, changed target after repeated stop, target failure then identity restored before normal stop, valid reply then normal stop/duplicate, and duplicate error then normal stop. No case starts another automated fight, sends 0x408, reopens automatic presentation, or revives a failed callback. The original 88 scenarios remain part of this run, including unresolved prior request blocking a new epoch and retired wrapper protection.

Final targeted regressions:

| Scope | Scenarios | Checks | Result |
| --- | ---: | ---: | --- |
| Real BattleLaunch + named IPC full stop suite | 127 | 2434 | PASS |
| Arena + Grave adapter/preflight/settlement | 20 | 458 | PASS |
| PointRace adapter/preflight/settlement | 14 | 372 | PASS |
| Own-demon preflight | 63 | 659 | PASS |

All final source manifests match the current filesystem (final-validation.json). final-hashes.json lists the six changed files; frozen-source/ preserves the tested inputs. model-changes.diff isolates this follow-up's production diff against the prior frozen delivery. No interface signatures changed.

The stop fixture executes real BattleLaunch in source-observed Probe.Finish ordering; actual Probe is not running. Unity, stock callback/scene operations, dispatch transport and native result rendering remain detached boundaries. Demon uses the documented settlement presentation substitute; Arena/PointRace compile production BattleSettlement. No full payload build, actual game, network/server, deployment or business acceptance was run. Independent review and controller-scheduled integration are outside this leaf.

Final production hashes:

- DailyArenaBattleModel.cs: `80aef1468ad04ac1aa744f844f3195a69521e4a4af9ef8403367ee03adb1a489`
- DailyPointRaceModel.cs: `9dd4df2ad36c4bbf7516d182bdc45666c55fdbf18b09f1e6724dc9717ddf9185`
- DailyMonsterModel.cs: `9deab8d4bb862b76e4c20683e7632079fbdd43db6d59822ab1b3abf429ec69bc`
