# Enabled UI component classification

Status: DONE. Offline source inspection supports a bounded replacement of the blanket `enabled __BaseUI => modal` rule. An enabled component can be a scene interaction controller or a persistent HUD host. Preserve the existing loading, battle, current-root, initialization, main-enable, building-overlay, sub-UI, visible-counter and animation gates. Unknown exact types remain rejected.

## Scope and input identity

Only installed `Assembly-CSharp.dll` and `Assembly-CSharp-firstpass.dll` were read through ILSpy 9.1.0.7988. Game assemblies were not executed. No live process access, injection, UI input, network, configuration, credentials or account data were used. This leaf owns this report, `overlay-*.cs.txt`, `overlay-inputs.json` and `tools/overlay_extract.ps1`; it does not own managed/native implementation.

`overlay-inputs.json` records exact input/artifact SHA256 and assembly-to-type assignments. Reproduction: run `tools/overlay_extract.ps1`. The source classes have no namespace except `Database.BuildingArea`.

## Minimal allow rules

Resolve exact Type objects from pinned assemblies, then compare `GetType()` equality. Do not use an `is` subclass test or a full-name-only allow rule. Each candidate must still be an active enabled component, and the overall existing gates must pass. This classification establishes only that the component is not an unrecognized active overlay; it grants no permission to call its buttons or callbacks.

| Exact type | Assembly | Allow condition and decisive source |
|---|---|---|
| BuildingMonsterScript | Assembly-CSharp | Scene HUD. `overlay-BuildingMonsterScript.cs.txt:99-151` renders world-position monster markers/buttons at UIDepth_Scene. Dialogs are separate MessageBox components (`:154-192`), which remain subject to unknown-type/visible-counter gates. |
| SecretShopNPC | Assembly-CSharp | Scene NPC marker. `overlay-SecretShopNPC.cs.txt:54-91` projects world position and draws one UIDepth_Scene button. Shop activation calls a separate MainUI route; the probe never calls it. |
| CheckLauncherVersion | Assembly-CSharp | Background controller, not a GUI host. Its `Start` is empty (`overlay-CheckLauncherVersion.cs.txt:51-53`); the complete class has no OnGUI/Web_OnGUI/Phone_OnGUI override. Inherited `__BaseUI.Web_OnGUI` is empty (`../object-map-__BaseUI.cs.txt:1587-1590`). |
| AnnounceBehaviour | Assembly-CSharp | Banner HUD. `overlay-AnnounceBehaviour.cs.txt:95-131` renders only the top scrolling announcement group, conditional on scrollBgAlpha. It has no alternate confirmation/window renderer. `visible_count=false` at `:44`. |
| MeditationUI | Assembly-CSharp | Persistent meditation HUD button, not a dialog host. Entire renderer `overlay-MeditationUI.cs.txt:55-78` is the fixed bottom-right panel with one experience-collection button and labels, `EndGUI(hasClose:false)`. Static toshow/waitflag gate loading, not an alternate panel. Source includes resource-changing callbacks, but they are not invoked by this operation. |
| ChatBehaviour | Assembly-CSharp | Chat HUD, including expanded/collapsed chat history. `overlay-ChatBehaviour.cs.txt:407-534` anchors chat/input at screen bottom; m_showChat toggles that HUD box. Alternate channel, emote and player-context popups register with PopupUI (`:470`, `:503`, `:934`); require the independent PopupUI false gate. Separate InspectUI/MessengerUI/sub-UI routes are still gated separately. |
| BuildingBehaviourScript | Assembly-CSharp | Only when instance `m_bEdit==false` and static `m_bEditVisible==false`. Normal `Web_OnGUI` is world-position UIDepth_Scene content (`overlay-BuildingBehaviourScript.cs.txt:116-224`); edit mode performs scene placement in Update (`:64-113`) and opens BuildingBuildUI (`:528-539`). |
| FlowerMessUI | Assembly-CSharp | Only when instance `fc==false && rm==false`. Main renderer contains notification badges (`overlay-FlowerMessUI.cs.txt:220-309`), but it also calls internal centered confirmation/report panels (`:309-314`). `DrawClickFlower` is gated by fc (`:325-354`); `DrawClickRob` by rm (`:357-379`). Never blanket-allow this type. |
| CapitalUI | Assembly-CSharp | Only when static `curArea` is nonnull and exact `Database.BuildingArea.unlocked==true`. Normal scene HUD is `overlay-CapitalUI.cs.txt:615-652`; otherwise a centered area-unlock panel is drawn (`:653-685`). The area field is public bool in firstpass `overlay-Database.BuildingArea.cs.txt:11`. |
| PopupUI | Assembly-CSharp-firstpass | Only when static `popup==false`. This is the complete renderer's direct condition for invoking drawpopup (`overlay-PopupUI.cs.txt:58-84`); Register sets true (`:19-36`), EndPopup clears it (`:38-49`). Test the static flag independently of finding an enabled host, so a registered popup with an unusual host state is still rejected. |
| MessengerUI | Assembly-CSharp | Only when nonnull, exact collection fields `active_sessions.Count==0 && pending_sessions.Count==0`. Actual chat windows are the active_sessions GUI.Window loop (`overlay-MessengerUI.cs.txt:296-303`); pending-session icons/groups are `:309-325`. Show unconditionally Visible/Enable (`:292-294`), so enabled alone does not imply an active chat window. Rejecting pending icons is deliberately conservative. Read counts only; never enumerate session contents. |
| TooltipUI | Assembly-CSharp-firstpass | Dormant-only: private instance `TooltipFlag==String.Empty && tttitle==String.Empty`, both nonnull. All actual draw calls in complete Web_OnGUI (`overlay-TooltipUI.cs.txt:324-608`) dispatch by recognized nonempty tttitle values. Update assigns tttitle=TooltipFlag (`:248`); HideTooltip clears TooltipFlag only (`:1025-1034`). Requiring both empty rejects stale title for at most a subsequent Update and queued new flags. Do not infer active tooltip delegates are harmless; they remain rejected. |

Existing RootUI exemption is unchanged. Its root-level renderer and current-root relationship were outside this leaf's change ownership; no new broad RootUI exemption is proposed here.

## Additional field contracts

All reads must be declared metadata field reads. Missing/null/unexpected field or type fails closed. No property getters, methods, GUI functions or UI callbacks are needed.

| Declaring exact type | Field | Storage and exact field type |
|---|---|---|
| BuildingBehaviourScript | m_bEdit | instance System.Boolean |
| BuildingBehaviourScript | m_bEditVisible | static System.Boolean |
| FlowerMessUI | fc, rm | instance System.Boolean |
| CapitalUI | curArea | static Database.BuildingArea, from firstpass |
| Database.BuildingArea | unlocked | instance System.Boolean |
| PopupUI | popup | static System.Boolean |
| MessengerUI | active_sessions | instance System.Collections.Generic.Dictionary`2[System.Int32,MessengerUI+ChatSession] |
| MessengerUI | pending_sessions | instance System.Collections.Generic.List`1[MessengerUI+PendingChatSession] |
| TooltipUI | TooltipFlag, tttitle | instance System.String |

The two Messenger nested element types are private classes. Verify generic definition and exact element Type identities; use the known framework collection Count through non-generic IDictionary/IList without reading keys, values, names or messages. For extra conservatism, per-component `m_bInit`/`first` checks may apply to types that call base.Start, but cannot be shared across scene MeshScript types: MeshScript.Start is intentionally empty (`overlay-MeshScript.cs.txt:11-13`).

## Validation and limits

PASS: all 14 bounded target types (the 12 observed component types plus MeshScript and Database.BuildingArea) decompiled successfully from their stated input assemblies. Input identities match the previous object-map SHA256: CSharp `C1C8EBAC7DDEF4173C873A00774975A5CA366F95B19164C4CA775165EB994911`, firstpass `29D8FA2CB55E800CCDF1CDC6C5B274B924EA7D03C989296DFA569731E02A8D87`.

PASS: complete relevant renderers and conditional branches inspected, including FlowerMessUI's internal panels, CapitalUI's lock panel, BuildingBehaviourScript's placement state, Messenger windows and pending icons, Popup's delegate activation, and all Tooltip Web_OnGUI title dispatch branches. No target DLL was loaded for execution.

UNVERIFIED: runtime values of the new conditional fields, revised managed schema/build/tests, second bootstrap and the single authorized non-resource operation. These belong to their assigned owners/controller. This report does not establish runtime success or business acceptance. Unknown enabled __BaseUI types and active Tooltip content remain rejected; count==0 alone remains insufficient.
