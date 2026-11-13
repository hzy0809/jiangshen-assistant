# Unity bridge independent design review

Status: DONE_WITH_CONCERNS. Design judgment only; no bridge implementation was available at this review point. No game process, memory, input, installed files, or live callback was accessed. Report owner: bridge_review; implementation ownership stays with the controller and native/managed workers.

## Judgment

The proposed thread-specific `WH_CALLWNDPROC` callback is an execution entry on a window-owning thread, not an established Unity dispatcher. Native callback TID == captured window TID, non-null `mono_thread_current()` == non-null `mono_thread_get_main()`, and non-null current domain provide useful independent gates. They justify a carefully bounded **bootstrap experiment**, not a prior proof that arbitrary Unity APIs are safe at that point. No attach, set-main, domain switching, runtime initialization, or runtime cleanup should occur.

The first managed entry should create only the small helper component and return. Do not enumerate scene objects, invoke game methods, or wait for Update inside the hook. Unity API calls needed to create that component remain the experimental assumption. A strict guarantee before those calls would require an existing verified Unity callback or independently established native player-loop/thread evidence. No such entry is established in the named evidence.

After the hook returns, require Unity to call the helper's own `Update`; never manually invoke Update. Record native TID, managed thread ID, and increasing `Time.frameCount` over at least two distinct frames, with the native TID matching the bootstrap TID. Only then permit bounded instance discovery; only a later explicit allowlisted request may perform the reversible UI operation. This handshake establishes observed Unity callback affinity for later work; it cannot retroactively prove bootstrap safety or every Unity lifecycle invariant.

## Evidence and competing explanations

| Observation | Supported interpretation | Remaining alternative |
|---|---|---|
| GetWindowThreadProcessId | Identifies the thread that created the window | Window owner need not be the Unity scripting loop |
| WH_CALLWNDPROC | Runs before processing the sent window message | Callback can occur during nested/native message processing, outside a safe engine phase |
| Mono current == Mono main | Existing Mono thread registration/designation agrees | Host-designated Mono main does not itself identify Unity's callback phase |
| Helper receives distinct Unity Update frames on same OS TID | Later work is executing in an actual Unity callback on that thread | Creation of the helper was still an engine API call from the hook |
| Before/after allowlisted resources equal | Those sampled fields did not change between observations | Other resources or server requests are not thereby excluded |

The exact installed `mono_thread_get_main` accessor is a global slot read (`runtime-native-disassembly.txt:448-450`). It contains no Unity dispatch. Upstream Mono 2.6 source implements `mono_thread_current` as a current-thread TLS lookup; that is corroboration for the API semantics, not exact-source identification of Unity's fork. Exact installed current-thread accessor/ABI should be checked before live use.

## Native and lifetime constraints

- Restrict the hook to the captured target thread; require matching HWND, process identity, architecture, loaded Mono identity, message, and per-request nonce. One atomic one-shot/reentrancy gate must become consumed before invoking Mono. Chain all messages through CallNextHookEx, including negative nCode.
- Keep DllMain minimal. No managed code, Mono initialization, LoadLibrary, User32 calls, cross-thread waits, or cleanup synchronization there. Resolve work in the actual callback after DLL initialization completes.
- Keep the hook callback short. The native-to-managed call requires exact x86 cdecl Mono signatures and exception out-pointer handling. No exception/object formatting path should execute Unity/game code unexpectedly.
- A timeout means outcome unknown once delivery has begun. Never retry an action automatically. A request status must distinguish not-started, entered, bootstrapped, Update-confirmed, action-applied, restored, failed, and cleanup-pending.
- Unhook success does not mean an in-flight callback has returned. Do not unload its DLL, free shared request state, remove files required by running code, or close resources still used by callbacks based only on timeout/unhook. Pinning the tiny native module until process exit is safer than claiming unload without a proven callback-rundown protocol.
- Helper destruction stops later Update work only after Unity actually processes destruction. Loaded managed assembly generally persists in its application domain; deleting a file is not an assembly unload. Report temporary component cleanup, hook cleanup, native module residency, and managed assembly residency separately. Process restart is the complete rollback boundary.
- No long-lived unrooted native MonoObject pointers. Keep references in managed fields or use correctly released GC handles if native retention is essential. Revalidate domain, instance lifetime and current UI before action and restore.

## Resource-free candidate gate

`MainUI.luoshuZhankai` and `luoshuShouqi` change only the receiving MainUI's animation fields; Shouqi additionally reads `Global.character_.formation_set`. The bodies contain no network send or player-model writes (`object-map-MainUI.cs.txt:4852-4866`). This supports a bounded local UI action. It does not mean injected code is free of allocations or process-state changes.

Direct calls bypass the draw/button conditions. Before action, require exactly one eligible live current MainUI under `LoadScreen.GetCurUI()`, not merely the first item returned by Resources enumeration. Require component enabled, active scene hierarchy, feature `checkUI(90u)`, `IsEnable()`, no build overlay (`BuildingBuildUI.m_bBuildVisible`), no modal/subUI or current loading/transition, settled animation, valid original SHOUQI/ZHANKAI state, and positive expected formation count within the six-element drawing arrays. Do not call `drawLuoshu`, OnGUI, arbitrary delegates, or formation-item actions to force visibility.

`__BaseUI.my_enabled` and GUI.enabled are drawing-context conditions, not independent proof of availability during Update. `my_enabled` is shared and set by drawing code. MainUI.Web_OnGUI returns before drawLuoshu when the build overlay is visible (`MainUI:1289-1305`); drawLuoshu checks `checkUI(90u)` then IsEnable (`:4768-4785`); `_ButtonStyle.Button` gates its boolean and my_enabled, then calls GUI.Button (`object-map-_ButtonStyle.cs.txt:143-175`). Those facts are why an instance alone is insufficient.

Snapshot original instance identity, root identity, formation count, original status/position and allowlisted level/stamina/gold. Apply the opposite state once from Update; observe completion over later Update/GUI frames and record field result plus independent visible result. Restore by the matching inverse method on the same valid instance, wait for settled original state, and report restoration independently. If user/game state changes or scene is replaced, stop and mark restoration unknown rather than touching a different instance. Formation-list item selection is excluded: drawLuoshu invokes `cs_formation_set_switch` for those items (`MainUI:4808-4816`).

## Minimum validation before a live attempt

1. Offline native/managed compile against x86 and the installed Unity/mscorlib profile; inspect exact imports/exports, no installation changes, no unexpected dependencies. Confirm Mono thread/domain accessor semantics against pinned installed bytes where possible.
2. Source review: first entry creates only helper; no game enumeration/action before distinct Update handshake; one-shot filtering; exception paths; timeout/rundown; exact method allowlist and no arbitrary invocation interface; managed queue size/deadline; cleanup states.
3. Synthetic host check of request identity, one-shot/reentrant callbacks, negative nCode, nonmatching messages, missing Mono exports, null/mismatched current/main, timeout-before/after-entry, duplicate request refusal and exception reporting. It validates bridge protocol, not Unity safety.
4. Controller schedules live **bootstrap/read-only** stage first. Any missing gate, thread mismatch, no Update progression, exception, UI ambiguity, or domain/lifecycle change stops action. No live authorization is inferred from this report.
5. Only after bootstrap/read-only receipt is reconciled, controller schedules one local animation action and explicit restoration, collecting separate execution, field-change, visible-result, allowlisted resource and cleanup evidence.

No full build, acceptance run, injection, or game-facing action was assigned to or executed by this reviewer. Implementation review remains pending.

## Official references

- [GetWindowThreadProcessId](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getwindowthreadprocessid): window creator identity only.
- [WH_CALLWNDPROC callback](https://learn.microsoft.com/en-us/windows/win32/winmsg/callwndproc) and [thread parameter explanation](https://devblogs.microsoft.com/oldnewthing/20180926-00/?p=99825): execution relative to the window procedure; chain behavior.
- [DLL best practices](https://learn.microsoft.com/en-us/windows/win32/dlls/dynamic-link-library-best-practices): loader-lock restrictions.
- [UnhookWindowsHookEx](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-unhookwindowshookex): callbacks can remain in progress.
- [SendMessageTimeout behavior](https://devblogs.microsoft.com/oldnewthing/20110915-00/?p=9643): already-started processing can outlive sender timeout.
- [Mono embedding](https://www.mono-project.com/docs/advanced/embedding/) and [upstream Mono 2.6 thread implementation](https://github.com/mono/mono/blob/mono-2-6/mono/metadata/threads.c#L1330-L1337): embedding/thread registration and current TLS semantics; source does not establish exact Unity fork identity.
- [Unity 4.1 Update reference](https://docs.unity3d.com/410/Documentation/ScriptReference/MonoBehaviour.Update.html): legacy lifecycle reference; current web rendering did not expose the complete description during this review, so installed artifact evidence and observed Update remain necessary.

Execution metadata visible to this leaf: danger-full-access filesystem; approval never. Effective role/model/reasoning-effort host records were unavailable; no archive search or manifest inference was used to fill that gap.
