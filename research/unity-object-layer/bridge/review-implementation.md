# Unity bridge implementation review

Status: DONE_WITH_CONCERNS. Both blocking source findings below are resolved in the frozen source. No additional blocking source defect was identified within the reviewed boundary. This permits the controller to consider the bounded bootstrap experiment; it is not proof that Unity bootstrap, UI action or restore will work live. No live game operations were performed by this reviewer. Only this review report is owned/changed here. Controller and workers retain implementation ownership.

Review boundary: `managed/Probe.cs`, `Protocol.cs`; `native/bridge.c`, `bridge.h`, `host.c`, `bridge.def`, `build.ps1`; named offline decompilation and runtime identity artifacts. Controller reports managed compilation and synthetic native harness PASS; these runs have not been repeated by this reviewer and do not establish Unity runtime compatibility.

## Findings sent for correction

1. **Block action until context is checked during progress and immediately before restore.** Probe.cs SHA256 `4268ADFC4E47E3ABB7380A7BF521B50B66577E3A3788DBF0229CE533536874BB`, `Advance:166-172`, `TryStartRestore:203-206`: only SameTarget was checked after initial action admission. Same object/root/formation/scene can persist while loading, feature lock, disable, build overlay, modal/subUI or other enabled UI gates change. Consequently the inverse method could execute against a hidden/busy UI and field endpoint checks could still produce PASS. Accept only PASS or the specially identified animation_unsettled gate during an owned animation; stop and report restoration UNVERIFIED on other context gates. Do not invoke a new target as compensation.
2. **Require the semantic endpoint for action and exact original endpoint for restore.** Same version, `Advance:169-194`: settled originally meant status matches and Position==Target, without checking Target equals the expected expanded/collapsed endpoint or original Position/Target at restore. A wrong but stationary target could qualify. Require expected target from status/formation and compare restore position/target against the original settled snapshot, alongside the strengthened context gate.

Both findings were corrected by the managed worker and independently reread here. In frozen `Probe.cs`, `Advance:166-175` now rejects other context gates, checks the status-specific target and the original position/target during restore. `TryStartRestore:206-210` independently checks target identity and ContextSafe before inverse invocation. `ContextSafe:221-225` accepts only Safe or the final animation_unsettled gate; all object/root/loading/enable/modal/unlock/formation checks execute before that gate. `AbandonRestore:228-232` emits restoration_unverified, marks failure and stops without another target invocation. No source defect remains open from these two findings.

## Frozen version and final readback

| File | SHA256 |
|---|---|
| managed/Probe.cs | AC06AD7DE1D1666EFBE830D574204E87C645AD7EEF8DE87D093C00A1E3573B4E |
| managed/Protocol.cs | 76F857445B9FAC4915AD309D5B579FEA7411E7ABE20A6D788A85DBB613CA5C22 |
| JSUnityProbe.dll | C951DFABCD7D9AE3D2A5F823262461C55339E15AA6E68F39266393E4620111AF |
| native/bridge.c | B3DD34E05C54DD07F1FF9CF11DCEC1DAD1759C23EEE7B896865197F7F8B50DC0 |
| native/bridge.h | 9B745B414429911A670230D1EC268D0E506184E64C588EEDD6F7FD0A0594C473 |
| native/host.c | FC2D8FCBEAACE1E3C19D47496E6041E4375A89B49D1D9591593EEC78187156E0 |
| native/bridge.def | C11315D9E48BB63B0D8587E68F6ECE0514B4B1536D27CAC1A37A12128E98D52E |
| native/build.ps1 | 21A1443D3FD233226A53D3E7EC0A1AA4F9989B506CA87DA28A1F81C86BD60F32 |

Reviewed saved metadata receipts `managed-schema-check.json`: 45 PASS / 0 FAIL, source hashes match frozen source, pinned input hashes match the named assemblies. All 33 fields, four methods including the exact Unity FindObjectsOfType overload, enum, six declaring assemblies and relevant hierarchy are covered by a PEReader-only checker; source inspection of that checker confirmed no game assembly execution.

Reviewed `managed-build-check.json`: compile exit 0, four metadata checks PASS, legacy v2.0.50727, I386/Requires32Bit, exactly installed mscorlib 2.0.5.0 and UnityEngine 0.0.0.0 references, frozen payload hash above. An interim extra mscorlib 2.0.0.0 reference was investigated and removed, not accepted as a runtime assumption. Frozen Bootstrap uses `AddComponent<Probe>()`; only a fixed signature-checked Unity FindObjectsOfType MethodInfo can be invoked, with the type argument restricted to MainUI or __BaseUI (`Probe:298-312,400-403`). There is no caller-selected reflection target. The internal assembly identity `JSUnityProbe_e3462b1627879c4c` matches a separate recomputation of the two source hashes using the build script's algorithm. Source-versioned identity reduces stale-assembly confusion; it does not authorize another bootstrap attempt in an already touched process.

The native frozen delta was also reviewed: disk-only nonempty <=32 MiB hash input via GetFileSizeEx (`bridge.c:43-59`), module path truncation rejection (`:40-41`), loaded managed image filename matches the sealed payload path before entry lookup (`:111-116`), and a target process SYNCHRONIZE handle distinguishes proven target exit from a target-alive pending callback (`host.c:117-131`). These changes preserve the earlier one-shot/lifetime gates. Native final production seal/compile is controller-scheduled and was pending at review completion; it must use exactly the frozen payload hash above.

## Confirmed behavior in reviewed native version

Native `bridge.c` SHA256 `DECFA33377A34D10FECBA7BB68A6C55E9D735AD52DBC2F6D3F7EE01EA718557D`:

- DllMain only records self (`:23-25`); no Mono/User32/loader work there.
- Target HWND/PID/TID, message tag/cookie and one-shot CAS precede bootstrap (`:131-157`). Negative hook codes chain (`:134`); normal paths chain as well (`:160`).
- Native/Mono thread and current-domain gate precedes fixed managed assembly entry (`:88-119`). It does not attach, change domains, set main, initialize or clean up Mono.
- Fixed payload filename/hash plus retained deny-write/delete file handle bind the load to the sealed payload (`:98-106`); there is no arbitrary class/method/path invocation argument. Return/parameter signature and managed exception out-pointer are checked (`:109-127`).
- GetModuleHandleEx retains an unbalanced module reference for process lifetime before bootstrap (`:147-149`); timeout does not cause unload. Fire cancels only unentered ARMED state, unhooks, then exposes current state (`:208-216`). Close refuses RUNNING, retained hook, or an entered callback whose tail receipt is absent (`:218-221`). Host polls pending outcome without retrying bootstrap/action, and never FreeLibrary's the local module.

Thread limits from review-design.md remain: Mono/window equality supports a bounded bootstrap experiment, not prior proof of safe Unity phase. Managed requires two increasing Unity Update frames on the same native+managed thread before model creation/instance discovery. Helper creation itself is still the experimental step.

## Offline field mapping and command boundary

The inspected Probe.cs binds resource fields through declared-field lookup along the hierarchy, which correctly reaches base `_character.money` and money64 rather than the derived money property. Reported money is the raw base field; their combination represents effective balance, as documented in object-map.md.

The fields lds/rmg/m_bInit, currmg/isBattling, bLoading/ExpectingCount/LevelID, enable/first/UICount_Visible, Global.extern_enabled/character_, and resource fields match the named decompilation. The final 45-PASS PE metadata preflight closes the exact schema check, including m_bBuildVisible and method/enum signatures. No target assembly was executed here.

Bootstrap records started before creating the writer/component (`Probe:36-42`). Model construction/first Capture failure causes cleanup while Entry.started remains true. This is a deliberate one-attempt-per-process boundary, not a retry mechanism; the payload file is intentionally retained/locked by native code. Offline preflight and final sealed source/binary identity must precede first live bootstrap. A failed bootstrap cannot be repaired in place by overwriting the same DLL or resetting started; restart is the stated reset boundary.

The only callers of the game animation methods are the fixed Toggle method; first action originates only after parsed `toggle-restore` and two safe captures (`Probe:141-161`). Snapshot/diagnose/stop commands do not select arbitrary methods. Bootstrap creates the helper but does not capture target objects or call either animation method; initial Capture occurs only after Update handshake (`:81-101`). Read-only `checkUI(90)` is invoked during Capture, including its initial snapshot, so "no game method call before action command" would be too broad a claim; "no game animation action before explicit command" is supported.

Stop while idle requests helper destruction; stop during action attempts same-instance restoration through the newly checked path (`:104-116,199-232`). Unexpected native/managed thread logs failure and performs no Unity/target call from that thread; a later correct callback may finish/restore (`:81-87`). If no legitimate callback returns, cleanup remains unverified. Finish disables/destroys only the helper and OnDestroy supplies a separate lifecycle receipt (`:244-258`). It does not claim to unload the assembly/native DLL or release the sealed payload lock. Native hook removal and callback-tail completion remain separate from managed helper cleanup. A scene/domain/process change or user interaction can prevent restoration, and the implementation correctly has a failure/unverified outcome rather than selecting another instance.

## Validation limits

No native or managed binary was rebuilt, no synthetic harness was rerun, and no Unity/game acceptance run was performed by this review. Saved native synthetic receipts show cross-process owner TID, cookie rejection, one-shot duplicate suppression, cancellation, successful Fire, timeout with callback still RUNNING/unhooked/retained, then callback completion; the reviewer inspected those receipts and corresponding source rather than rerunning. Controller additionally reports 31 protocol checks PASS. These are protocol/source evidence only. Source inspection covers the code named above and the static operation bodies; live invocation, visible animation, observed resource equality, restore and cleanup remain unverified until separate controller-scheduled stages. Global network/other resource effects cannot be inferred from sampled allowlisted fields. The next permitted execution stage should be bootstrap/read-only receipt reconciliation before any separate toggle-restore command.
