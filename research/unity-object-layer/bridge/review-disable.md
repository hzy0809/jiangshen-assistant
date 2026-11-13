# MainUI Disable/Enable candidate review

Status: DONE_WITH_CONCERNS. No blocking source defect identified in the frozen candidate delta. Source/report review only; no live actions, hooks, memory access or input by this reviewer. The controller owns the selected candidate, native reseal, read-only admission, and live action/restore stages.

## Candidate and effect

The second read-only session `bebdb0d9f3b349b48ef191585f2b43b5` passed native/Update/current-instance/overlay discovery but rejected the original Luoshu operation at feature_locked. Explicit stop and component_destroyed receipts show action_used=false. The new operation changes the candidate; it does not bypass or remove the lock in order to invoke Luoshu.

ChatBehaviour is not an existing method alternative: its m_showChat assignments are inline in Web_OnGUI (`overlay-ChatBehaviour.cs.txt:511-528`). Calling that entire renderer would also process chat send/filter/popup paths, and direct bridge field writes would not be a game-method-call proof. Neither route is part of this candidate.

The controller selected temporary **MainUI interaction disable and restore** using existing inherited methods:

- `__BaseUI.Disable()` writes only instance enable=false.
- `__BaseUI.Enable()` writes only instance enable=true.

Source: `object-map-__BaseUI.cs.txt:1184-1196`. Independent metadata/raw IL artifact `overlay-method-metadata.txt` binds the firstpass __BaseUI methods to tokens 0x06000F8A/0x06000F8B, HASTHIS, zero arguments, void, public virtual NewSlot. Their IL is only load-this, load-boolean, store field 0x04001673 and return. Declared zero-argument Enable/Disable method count is zero on CSharp BaseUI and MainUI. Thus invoking these exact existing inherited methods on MainUI has no direct resource/model/configuration/network/audio/counter effect. EnableAll/DisableAll are excluded because they propagate to other components.

This operation changes interaction state, not component.enabled or GameObject visibility. MainUI.Web_OnGUI assigns __BaseUI.my_enabled=IsEnable (`MainUI:1296`); `_ButtonStyle.Button:143-162` renders the disabled style/gray box and returns false; `_ButtonMain.Button:353-380` darkens/draws a label and suppresses its handler. This is a source-backed expectation of disabled rendering, not a live visual acceptance. Screenshots/independent observation must separately show any actual visible change and restoration.

## Required narrow managed behavior

The fixed command should be disable-restore only. Require original enable=true, same unique active initialized current MainUI, and all existing root/character/scene/loading/battle/global-extern/modal/overlay guards before action. Bind the exact declared __BaseUI virtual void methods, verifying the hierarchy has no overriding implementation. Invoke the game methods; do not use FieldInfo.SetValue or a generic arbitrary-method interface.

During the owned transaction, enable=false is expected. It must not cause a generic main_disabled bypass: the old gate returned early before checking modal/overlay conditions. The revised Capture must always evaluate Global.extern_enabled and all context guards, then permit own enable=false only for the exact originally admitted MainUI during the active action. False on another MainUI, an already-disabled original, external/global disable or any other unsafe context remains rejection. Advance/restore must still verify same target/character/root/scene/formation.

Observe false across legitimate Updates, hold six seconds, then call Enable on the same safe original instance and observe true on later Updates. MainUI has game callback Enable callers, so unexpected early re-enabling during the hold must not become a successful full-duration hold. Restoration/context failure and user/game state drift remain explicit. There is no animation endpoint or Luoshu-unlock claim for this candidate.

Resource observations and UI status remain separate; allowlisted equality is sampled client state and consumption/network causality remain UNVERIFIED. Do not infer action-caused resource consumption from autonomous game updates, or zero cost from UI success.

## Frozen implementation readback

| Artifact | SHA256 |
|---|---|
| managed/Probe.cs | 54075567B039E7C63282ED88B1D265C969E628323F0A8C9DA14D6D5FEDA7DDC7 |
| managed/Protocol.cs | 04C3EAE1F58E40B77FD0F1EB598094F07F434E03052BDBDE948CBB7A43C65409 |
| JSUnityProbe.dll | 5CCA9F8804454EA34A6ABA9D3ECB28A74AD06B56A5CA071DA77A02A5FAD9AFF1 |

Independent final hash readback matches the author's freeze and saved receipts. Internal assembly name is JSUnityProbe_c3fec61cde08ec23.

- `Probe.cs:150-167` admits only fixed disable-restore, consumes the one-shot guard, requires two safe originally-enabled captures and checks the immediate disabled result. `Protocol.cs:89` rejects retired toggle-restore.
- `Probe.cs:353-360` binds declared __BaseUI Enable/Disable and rejects a declared zero-argument name on every intervening type. The exact target must be MainUI rather than a subclass (`:374-377,460-463`). SetInteraction invokes the existing MethodInfo; it does not write the game's enable field itself.
- `Capture:398-427` evaluates global input, init, build/popup/edit/subUI/count/exact ambient/conditional overlay and state guards before its final own-disabled allowance. The allowance requires reference equality to the original MainUI supplied only by active action/restore paths. `ContextSafe:236-240` accepts only Safe, without a generic ignored gate.
- `Advance:172-205` requires false in phases 1/2, marks unexpected early true as failure and observes true only after entering restore phase. Phase 2 holds false for six seconds. `TryStartRestore:215-227` rechecks safe same context and skips inverse invocation if true is already observed; the receipt distinguishes restore_already_observed. UI PASS explicitly requires actionObserved, restoreMethodInvoked and no failure (`:205`), so external restoration cannot become method-pair proof.
- Resource equality and UI status remain separated, visual_gray_state remains UNVERIFIED in machine receipts, and the original animation/feature-90 code and fields are absent from this operation.

Inspected saved offline receipts, bound to these hashes: `managed-schema-check.json` **70 PASS / 0 FAIL**, including 40 fields, three methods, 20 type assembly identities, no MainUI/BaseUI overrides, exact virtual declarations, exact field-only Enable/Disable IL bodies and hierarchy; `managed-protocol-check.json` **39 PASS**, including rejection of the retired action and resource-observation cases; `managed-build-check.json` **4 PASS**, legacy v2.0.50727, I386/Requires32Bit and exactly installed mscorlib/UnityEngine references. No build or test execution was repeated by this reviewer.

## Limits and remaining work

Two earlier action-free helper destruction receipts support only the controller's bounded retry decision, not assembly/module unload or full rollback. New source identity/session and matching native payload seal must be established before the next read-only snapshot; the controller must reconcile that snapshot before a separate disable-restore command.

On unsafe context, the implementation reports restoration_unverified and destroys its helper without overriding the new busy/modal state. For this candidate, that can leave MainUI's interaction flag false until a normal game Enable or controller-directed recovery/restart. Helper destruction is not restoration. The controller was notified of this material limit before live action; a deferred-restoration policy would be a separate design decision. The short controlled interval and repeated guards reduce exposure but do not exclude asynchronous state changes.

The expected visible result concerns normal MainUI button rendering. This is not a claim that every debug/custom direct GUI path or every other HUD component becomes disabled. A field transition/MethodInfo receipt does not establish screenshot-visible gray state; independent visible before/disabled/restored evidence is still required. Numeric resource equality likewise does not establish zero consumption or network causality.

Changed by this review: this report only; no source or game-facing interfaces. Implementation interface changes reviewed: fixed action command disable-restore, action_name mainui_disable_restore, main_enabled snapshots and disabled/restored-enabled receipts; retired Luoshu/toggle/animation fields removed. Remaining live admission, method pair, six-second hold, visual acceptance, resource observations, restoration and cleanup belong to the controller and remain UNVERIFIED here.
