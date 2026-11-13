# Native x86 one-shot bootstrap bridge

Scope: source/build artifacts and a synthetic process only. This leaf did not open, hook, inject, invoke, or send input to the game. The controller owns review, live loading and one UI validation. This is a prototype with restart-based complete cleanup.

The production DLL installs one thread-specific `WH_CALLWNDPROC` hook for the supplied HWND owner PID/TID. `JSB_Fire` sends one `WM_NULL`, with a per-arm cookie in `wParam` and `0x4a534231` in `lParam`, using `SMTO_ABORTIFHUNG | SMTO_BLOCK | SMTO_ERRORONEXIT`. The DLL does no work in `DllMain` except remembering its module handle. There are no remote-thread or arbitrary-evaluation interfaces.

Before managed entry, the callback checks exact window/process/thread identity, pinned js.exe and mono.dll SHA256, an existing Mono thread equal to `mono_thread_get_main`, and a nonnull existing root/current domain. It never attaches a thread, creates a Mono runtime, changes domains or changes Mono's main thread. Mono main-thread equality is a bootstrap hypothesis; actual Unity-loop evidence must come from the managed component's subsequent Updates. Native success alone does not establish Unity API validity, game object state or a UI action.

The only managed target is the sealed sibling `JSUnityProbe.dll`, `JiangShen.ManagedProbe.Entry.Bootstrap(string sessionDir, string nonce, int verifiedNativeTID) -> string`. The image filename must match that sibling; flags/parameter/return metadata must match the fixed ABI. The payload is read and SHA256 checked through a handle denying writes and deletion, retained until process exit. The native DLL also retains a reference to itself until process exit. Hash or path changes require a fresh process, and payload files must not be overwritten during a live session.

`sessionDir` is compiled as `G:\development\enjoy\jiangshen-assistant\research\unity-object-layer\bridge\sessions\<nonce>`, where nonce is exactly 32 lowercase hexadecimal characters. The controller creates the directory first. Native rejects a missing directory or a reparse point at that directory. No caller-supplied path, class, method or expression is accepted. All Mono exports are dynamically resolved from the already-loaded `mono.dll` and use legacy x86 cdecl. Returned text is bounded to 255 ASCII bytes; managed exceptions are recorded with a fixed marker without printing their contents.

## Build

Use the controller-provisioned project-local Zig 0.13.0 toolchain (no install):

```powershell
# Only the independent synthetic test; never connects to a game.
.\build.ps1 -CompilerBin ..\tools\zig-windows-x86_64-0.13.0 -SyntheticOnly -RunSynthetic

# After controller builds the final managed payload:
.\build.ps1 -CompilerBin ..\tools\zig-windows-x86_64-0.13.0 -Payload ..\JSUnityProbe.dll
```

The script uses x86 `zig cc`, Windows headers/import libraries, `user32`, `advapi32`, `-Wall -Wextra -Werror`, and project-local compiler caches. An existing i686 MinGW GCC/Clang layout is also accepted. It writes only `native/build`, copies the payload there, embeds its hash via generated `payload_seal.h`, and produces `build-manifest.json`. No live command runs during build. `bridge-preflight-not-live.dll`, if present, used an all-zero payload seal solely to compile-check the production branch; do not use it for live work.

## Public native ABI and lifecycle

All five controller exports are undecorated x86 cdecl; `JSB_STATUS` is defined in `bridge.h`.

| Interface | Meaning |
| --- | --- |
| `JSB_Arm(HWND,pid,tid,nonce)` | Verify HWND identity, create a per-PID/TID shared mapping, reject an existing mapping, arm one CAS gate and install one thread hook. |
| `JSB_Fire(timeout_ms,status*)` | Send once, then attempt unhook. Timeout range 1..10000 ms. Returns true only for synchronous send + hook removal + completed bootstrap. |
| `JSB_Cancel(status*)` | CAS `ARMED -> CANCELLED` and remove hook; cannot abort `RUNNING` work. |
| `JSB_ReadStatus(status*)` | Copy a diagnostic snapshot. Mutable fields during RUNNING are provisional. |
| `JSB_Close()` | Release controller mapping only after no hook and no running/unfinished callback. Never unload target code or managed assembly. |

`state`: 1 ARMED, 2 RUNNING, 3 COMPLETE, 4 CANCELLED, 5 FAILED. `calls` is incremented only by the winning CAS. `callback_finished` records managed/native work and hook chaining completed, immediately before callback-local view/handle cleanup and return; it is not a proof that the last machine instruction has returned. `hook_removed` is separate. The retained target module makes the final unwind safe. A successful synchronous send additionally means the receiving hook/window processing returned.

Timeout does not mean cancellation or completion. A queued message is prevented from starting work only if cancellation wins the ARMED CAS. If already RUNNING, remove the hook, keep controller/mapping/module lifetimes, poll for a terminal state plus `callback_finished`, and do not retry. The host implements that hold/poll behavior. There is no forced-unload, forced-thread-termination, platform-barrier workaround, or hard abort for a stuck Mono call. Complete removal of loaded native/managed code and the sealed file handle requires normal game process exit/restart.

The parent-only host command, after its independent current PID/HWND/TID/hash and manifest checks, is:

```text
native/build/bridge_host.exe --run <HWND hex> <PID decimal> <TID decimal> <nonce32>
```

The host opens the exact target process with `SYNCHRONIZE` only before arming. It prints `native_fire_returned` and `native_final` JSON lines. If the target exits while callback outcome is pending, its signalled process handle proves no callback can remain alive; the host emits `native_target_exited_cleanup_boundary`, exits 7, and lets the OS release its controller-only resources. It never kills the target. It does not create a session directory, write managed commands, enumerate game windows or call UI handlers. A native COMPLETE requires a `BOOTSTRAP_OK` response; actual readiness and any later action are established by managed receipts, not this response. Native FAILED after an assembly load does not imply the assembly was unloaded or all managed startup side effects were rolled back.

## Targeted synthetic validation

`bridge_selftest.exe --self-test` creates only its own child process and message-only `JSBridgeSyntheticWindow`. The separately compiled synthetic DLL rejects other window classes and contains no Mono bootstrap. It validates cross-process callback thread affinity, invalid identity/nonce/cookie rejection, one-shot CAS under duplicate messages while hooked, cancellation without invocation, send/unhook sequencing and timeout lifetime retention. Its child exits normally; retained modules disappear with that child. It does not validate Mono ABI at runtime or Unity/game behavior.

Evidence: `build/synthetic-results.jsonl`. Production branch compile and PE/export checks are reported separately in `../native-validation.md`.

Microsoft documents that a thread hook runs on the thread about to invoke the window procedure, matching the synthetic result: [thread hook semantics](https://devblogs.microsoft.com/oldnewthing/20180926-00/?p=99825), [SetWindowsHookEx](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowshookexw). Unhook may return while another thread is still in the procedure: [UnhookWindowsHookEx](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-unhookwindowshookex). Managed invocation follows the [Mono embedding API](https://www.mono-project.com/docs/advanced/embedding/); the pinned local binary inventory, not current Mono product assumptions, establishes export availability for this legacy runtime.
