# Native bridge implementation and targeted validation

Status: DONE. Frozen native source, sealed production build, independent synthetic execution and offline artifact checks are complete. This leaf has not accessed or operated the game process or changed installed game files. DONE refers to this implementation assignment, not live Unity/UI acceptance.

## Direct result and owned files

Implemented one x86 thread-specific `WH_CALLWNDPROC` bootstrap DLL, a controller host, a separate synthetic build and its cross-process harness. Fixed native/managed ABI and lifecycle details: [native/README.md](native/README.md), [native/bridge.h](native/bridge.h).

Owned source: `native/bridge.c`, `bridge.h`, `bridge.def`, `host.c`, `build.ps1`, `check_build.py`, `README.md`; generated build artifacts/caches are under `native/build`. This report is the only report outside that directory owned by this leaf. Managed payload implementation and live execution remain with their respective owners.

## Validation evidence

| Check | Result | Decisive evidence |
| --- | --- | --- |
| Native synthetic DLL/host x86 compile, warnings as errors | PASS | Provisioned Zig 0.13.0 `cc -target x86-windows-gnu -std=c11 -O2 -Wall -Wextra -Werror` |
| Correct native callback thread in a newly created independent process | PASS | Final saved synthetic run child PID 55860, window TID 33320, callback TID 33320 |
| Incorrect HWND identity, malformed nonce, incorrect message cookie | PASS | Harness refuses invalid arm or preserves ARMED/calls=0 after the wrong cookie |
| Duplicate correct messages while hook still installed | PASS | `synthetic_one_shot`: state COMPLETE, calls=1, callback_finished=1 |
| Cancel before invocation | PASS | `synthetic_cancelled`: state CANCELLED, calls=0, hook_removed=1 |
| Fire sends once and removes hook | PASS | `synthetic_fire`: calls=1, callback_finished=1, hook_removed=1 |
| Timeout while callback already running | PASS | `synthetic_timeout_pending`: state RUNNING, callback_finished=0, hook_removed=1, retained_until_restart=1, error 1460; Close refuses |
| Completion after timeout without duplicate or unload | PASS | `synthetic_timeout_finished`: state COMPLETE, callback_finished=1, calls=1, retained_until_restart=1 |
| Synthetic cleanup | PASS | Harness waits for its own child to exit normally; no forced termination |
| Production Mono/bootstrap C branch compile | PASS | Final sealed `bridge.dll`, Zig x86, warnings as errors; preflight artifact was never used live |
| PE32 architecture, five undecorated controller exports, selected forbidden import absence | PASS | Four production/synthetic artifacts checked in `native/build/offline-validation.json` |
| Sealed production payload/source/artifact identities | PASS | `native/build/build-manifest.json`, generated seal and source/artifact SHA256 readback agree |
| Production DLL Windows dependency resolution in a separate 32-bit host | PASS | `native/build/load-smoke.json`: own DLL LoadLibrary, five GetProcAddress checks, unarmed local FreeLibrary; no Arm/Mono/game call |
| Runtime Mono API execution/Unity Update readiness/UI operation/visible restoration | UNVERIFIED | No game-facing operation is authorized to this leaf |

Raw targeted synthetic evidence: [native/build/synthetic-results.jsonl](native/build/synthetic-results.jsonl). It contains the final successful synthetic summary, elapsed 297 ms, and distinct timeout/rundown observations. Offline artifact checks: [native/build/offline-validation.json](native/build/offline-validation.json).

Final production artifacts, all under `native/build`:

| Artifact | SHA256 |
| --- | --- |
| bridge.dll | `c9f3d09be295c1acc82f122320899c6a984d8f543e4ba48adb1349985f5c6577` |
| bridge_host.exe | `bb5c6b7b666d801582a63f5b75e31e38b5ca1a1e798c56d4c5d4a452f8d4402b` |
| JSUnityProbe.dll (copied from managed owner's frozen payload) | `c951dfabcd7d9ae3d2a5f823262461c55339e15aa6e68f39266393e4620111af` |

The payload's internal assembly identity is `JSUnityProbe_e3462b1627879c4c`; the allowed file and fixed public entry remain `JSUnityProbe.dll` and `JiangShen.ManagedProbe.Entry.Bootstrap`. Native C/headers/host/build script remained frozen during the final build. `build-manifest.json` records their exact hashes. The controller may stage the three sealed production artifacts together into a unique session `bin` directory; the native sibling-image path check and payload hash remain valid there.

The first synthetic build rejected Zig's unsupported GNU linker `--kill-at` option before compilation; removing that option and using the `.def` controller exports resolved the toolchain compatibility issue. This was a build error, not a game/runtime failure.

## Limits and handoff

The native callback validates exact HWND/PID/TID identity, existing Mono current/main equality, current/root domains, pinned disk identities and a sealed fixed payload. The disk identity checks do not independently prove that every loaded code page is unchanged; the controller retains the current runtime/binary pin checks. The image path and read-only retained payload handle prevent accidental alternate-path/copy loading and seal/load replacement during this session. Loaded managed assembly removal and target DLL removal require normal process exit/restart.

Mono main equality is not Unity player-loop proof. Bootstrap is limited to creating the helper; the managed owner requires increasing actual Unity Update frames before model discovery and any later explicit UI command. No arbitrary eval, generic Mono injection thread, `mono_thread_attach`, `mono_jit_init`, main-thread setter, domain switch or unload interface was added. No backend/auth/config read or gameplay operation was executed by this leaf.

On a timeout, unhook is not completion: keep mappings and native code while RUNNING or before `callback_finished`. The host waits and does not retry. It holds a `SYNCHRONIZE`-only process handle; a proven target exit ends the wait with `native_target_exited_cleanup_boundary`/exit 7 without claiming callback completion or forcing termination. There is no forced interruption for an indefinitely stuck Mono API in a still-live target, and no automatic restoration claim. Pending bootstrap, managed probe stop, observed helper destruction, visible UI restoration and full restart cleanup must be reported independently by the controller.
