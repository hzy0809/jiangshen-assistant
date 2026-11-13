# Resource-free existing-method candidate

Status: DONE. Recommended candidate is **MainUI interaction disable, then restore**, using the existing inherited game methods `__BaseUI.Disable()` and `__BaseUI.Enable()`. This is a different operation from Luoshu expansion; a locked Luoshu must remain locked and uncalled. Implementation and live execution remain with the controller and assigned managed owner.

## Exact callable contract

Target is the already identified, initialized, current-root `MainUI` instance. Its hierarchy is `MainUI` (Assembly-CSharp) -> `BaseUI` (Assembly-CSharp) -> `__BaseUI` (Assembly-CSharp-firstpass). Both intermediate classes have no Enable/Disable override. Resolve exact public instance parameterless void methods declared by pinned firstpass `__BaseUI`; require declaring type, instance/static, return, argument and virtual-base identity to match. Do not resolve an arbitrary type or invoke a name supplied from a command.

The complete bodies are `../object-map-__BaseUI.cs.txt:1184-1192`:

```csharp
public virtual void Enable() { enable = true; }
public virtual void Disable() { enable = false; }
```

`enable` is the already schema-verified private instance System.Boolean declared on __BaseUI (`../object-map-__BaseUI.cs.txt:778`). It is distinct from Unity `Behaviour.enabled`, which must remain true. `IsEnable()` merely returns it (`:1194-1197`). Calling the actual existing game methods is the operation; the bridge must not substitute its own field assignments.

The two method bodies make no calls, touch no other state and contain no branches. They do not read chat text/account data, send network requests, invoke feature checks, modify resources/settings/PlayerPrefs, load assets, play sound, change UI counters or run animations.

Independent raw metadata/IL verification is in `overlay-method-metadata.txt`, reproduced by `tools/overlay_method_metadata.ps1` with PEReader (no target assembly load). Enable MethodDef `0x06000F8A` and Disable `0x06000F8B` are Public, Virtual, HideBySig, NewSlot (the flags enum prints NewSlot as VtableLayoutMask). Signature `20-00-01` is HASTHIS, zero parameters, void. Their entire IL is `02-17-7D-73-16-00-04-2A` / `02-16-7D-73-16-00-04-2A`: load this, load true/false, store FieldDef `0x04001673` (__BaseUI.enable), return. The field is Private with bool signature `06-02`. BaseUI/MainUI each declare zero Enable/Disable methods, establishing no intervening override or same-name shadow.

## Actual UI meaning

This is an existing interaction gate with rendering consequences, not an inert test field. `MainUI.Web_OnGUI` copies `IsEnable()` to `__BaseUI.my_enabled` (`../object-map-MainUI.cs.txt:1296`). `_ButtonStyle.Button` checks that gate (`../object-map-_ButtonStyle.cs.txt:143-162`); when false it renders a gray/disabled box and returns false without executing a button. `_ButtonMain.Button` uses the same gate to suppress hover/interaction and render disabled content (`../object-map-_ButtonMain.cs.txt:353-380`). MainUI also supplies `IsEnable()` directly to its character and top-bar buttons (`../object-map-MainUI.cs.txt:4587`, `:5148`, `:5172`). This does not claim every raw GUI.Button in MainUI is disabled.

## Gates and exact restoration

1. Require the existing common current-root, same-instance, active/enabled component, initialized main, no-loading/battle, external-enable, building/sub-UI, overlay and relevant stable-scene gates. Require original __BaseUI.enable=true. Do not accept an originally disabled main, since that could represent someone else's workflow.
2. Snapshot only authorized scalar state and existing resource invariants. No animation/state changes are required for this operation. Existing Luoshu scalars may remain observed and must not change because of the operation; checkUI(90) is irrelevant to this interaction-gate operation and its result is not changed or bypassed.
3. Invoke the exact inherited Disable() once on the original MainUI instance. Observe the same object's enable=false across the selected subsequent Unity Update frames; Unity Behaviour.enabled remains true. This proves existing-method mutation and sustained state, not physical rendering by itself.
4. Restore by invoking exact inherited Enable() once on that same original instance, including best-effort finally/stop cleanup if state was changed. Observe enable=true afterward, same scene/instance/root and resource invariants. The inverse restores the only field mutated by Disable().
5. For the temporary disabled phase, the operation-specific expected-state gate accepts enable=false only for this operation on the captured target. Do not globally weaken the main-disabled gate. Continue to reject changes in other safety invariants; do not discover or enable replacement objects during restoration.

No need to call `EnableAll`/`DisableAll`: those invoke scene callbacks and mutate global mouse-click state (`../object-map-__BaseUI.cs.txt:1850-1870`) and are explicitly excluded. Also exclude Hide/Hideme/Visible: they affect UI counters and may trigger sound/loading (`:1137-1176`).

## Rejected alternatives

Chat HUD expand/collapse is not a standalone existing method. `m_showChat` changes only inside Web_OnGUI (`overlay-ChatBehaviour.cs.txt:511-528`). Calling the entire Web_OnGUI is unsuitable because it handles text input/send and other GUI actions (`:407-534`); writing m_showChat directly would not be an existing method call. CloseEmoteBox/CloseChannelSelect (`:572-597`) only clear popup flags and do not provide the inverse of chat HUD expansion.

MainUI.expandUI(string) (`../object-map-MainUI.cs.txt:495-534`) sets multiple button states, collapses other open groups and starts animation. It has no matched inverse with exact restoration. MainUI top-bar shrink/expand similarly performs inline assignments (`:5144-5177`). Neither is needed for the recommended simpler pair.

## Validation and limits

PASS: targeted offline source/body/hierarchy inspection and independent ECMA-335 metadata/raw IL readback confirmed the exact method flags/bodies, lack of overrides in MainUI/BaseUI/ChatBehaviour, UI consumers and excluded propagation methods. Relevant firstpass and CSharp input identities match `overlay-inputs.json`; no target DLL was executed and no live action occurred in this leaf.

UNVERIFIED: acceptance of this candidate by the controller, revised managed metadata/schema/build/tests, runtime method invocation, frame observations, rendering and restoration. New live work requires the controller's existing authorized-operation boundary; this report itself performs none.
