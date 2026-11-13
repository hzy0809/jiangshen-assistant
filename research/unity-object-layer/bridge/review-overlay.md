# Overlay gate follow-up review

Status: DONE_WITH_CONCERNS. No blocking source defect was identified in the frozen overlay/receipt/resource delta. The controller may proceed with the separately scheduled known-no-action read-only retry, reconcile its snapshot, and only then consider a separate animation command. Review-only source access and this report; no live commands, hooks, UI input or game-memory access by the reviewer.

## Established read-only attempt

The controller's seven-line session receipt `sessions/64731d2da0864727a8d6cf0ce6eecd0c/probe.events.jsonl` shows helper Update on native TID 21756 / managed thread 1 across distinct frames 128073 and 128074, followed by a current Capital MainUI snapshot rejected by `visible_other_ui`. It contains no animation command. Explicit stop produced cleanup_requested with action_used=false and component_destroyed. Duplicate identical frame keys occur in the two Update events; they are an evidence-format defect and should be removed in the new producer, not silently rewritten in the historical receipt.

This supports a controller-selected **known-no-action, component-cleaned** second read-only attempt using a new source-versioned payload/session. It does not mean full rollback: the old assembly, native module references and sealed payload handle remain resident. Entry.started is per loaded payload assembly, not a process-wide lock. An unknown, pending or previously action-bearing attempt is not covered by this retry rationale.

## Conditions to preserve

The initial rejection is a conservative false positive because enabled __BaseUI components include ambient world/HUD hosts. The fix must recognize only exact known types from the already pinned game/firstpass assemblies. Unknown types, same-name types from another assembly, schema mismatch and unavailable state remain reject conditions.

The frozen conditional exceptions are source-supported rather than a blanket enabled-HUD exemption:

| Exact component | Required state before allowing it as ambient/dormant |
|---|---|
| BuildingMonsterScript, SecretShopNPC, CheckLauncherVersion, AnnounceBehaviour, MeditationUI, ChatBehaviour | Source-audited ambient hosts; independent popup/modal gates still apply |
| BuildingBehaviourScript | instance m_bEdit false and static m_bEditVisible false |
| FlowerMessUI | instance fc false and rm false |
| CapitalUI | static curArea non-null and its unlocked field true |
| PopupUI | static popup false |
| MessengerUI | active_sessions and pending_sessions non-null with zero counts; no content reads |
| TooltipUI | TooltipFlag and tttitle both empty; active tooltip rendering remains outside the exception |

Independent spot checks: PopupUI.popup gates the entire draw delegate call (`overlay-PopupUI.cs.txt:59-83`); MessengerUI renders windows for active_sessions and pending UI for pending_sessions (`overlay-MessengerUI.cs.txt:296-323`). CapitalUI draws an actionable central unlock panel if curArea.unlocked is false (`overlay-CapitalUI.cs.txt:652-680`). TooltipUI's drawing branches select nonempty tttitle values; empty TooltipFlag + tttitle is the conservative dormant condition, and active delegated drawers remain unaudited. The source classification worker supplies the complete ambient matrix separately.

Reviewed the completed [overlay-analysis.md](overlay-analysis.md), backed by `overlay-inputs.json` and 14 targeted type extractions. That report covers the complete relevant renderers and identifies ChatBehaviour's alternate popups as PopupUI registrations, MeditationUI's bottom-right HUD and AnnounceBehaviour's top banner. No callbacks from those ambient types are added to the invocation allowlist.

The existing unique-current-MainUI, root/character/scene/formation identity, loading/battle, enable, first/init, build overlay, subUI, visible counter, unlock, valid-status and settled-animation gates remain intact in `Probe.cs:354-420`. RootUI is now also compared as an exact Type, replacing the earlier full-name exemption. Static PopupUI.popup and BuildingBehaviourScript.m_bEditVisible are checked independently of finding an enabled component (`:390-391`). All other active enabled non-MainUI/non-RootUI types flow through OverlayGate; the list is preserved in the diagnostic snapshot, and any unrecognized exact type returns visible_unknown_ui (`:398-448`).

Messenger values must have the exact declared concrete Dictionary/List runtime types, then only IDictionary/IList counts are read; no keys, messages or player content is enumerated (`:439-446`). Its exact generic field schema, and the other new field/type contracts, are covered by the final metadata preflight. Null Tooltip strings do not equal String.Empty and remain rejected (`:428-429`); null/wrong BuildingArea remains rejected (`:434-437`).

The same dynamic conditions run on every Capture during Advance and immediately before restoration. ContextSafe still accepts only PASS or the final animation_unsettled gate; other gates emit restoration_unverified and finish without another target invocation (`:172,213,227-237`). Semantic/original endpoint checks remain in place (`:178-179`). No game animation runs merely because a HUD exception is recognized; parsed toggle-restore and two safe captures remain required.

## Resource evidence and receipt format

The controller notes meditation may autonomously update experience; that is a candidate explanation, not established causality from this receipt. A change in observed fields is not proof of action-caused consumption. The frozen result separates UI endpoint/restore status from allowlisted field equality, logs per-field before/after plus sticky every-observation equality, and leaves consumption causality/global network effects UNVERIFIED (`Probe.cs:199-202`; `Protocol.cs:6-57`). Neither a UI PASS nor unchanged sampled fields establishes zero resource cost. Baseline/preinvoke stability remains enforced (`Probe.cs:157-160`).

ResourceObservation retains a baseline clone and a sticky per-field changed flag, so a temporary change followed by a return to baseline cannot become an every-sample PASS. ObserveResources ignores repeated handling of the same Snapshot object, preserving the count as distinct captured observations including baseline (`Probe.cs:246-249`). Short-lived changes between captures and fields outside the allowlist remain unobserved. These are numeric client-state observations, not server-authoritative balances or a network interception proof. The money field remains the raw base field with money64 separately observed.

The new transaction result uses ui_status, resource_equality_status, per-field resource_comparison, resource_observations and resource_consumption_causality; cleanup_requested also uses ui_status. The controller must parse this source-bound receipt version accordingly. Commands and Bootstrap ABI are unchanged. The two Update events now emit distinct_frames only; common Emit owns the sole frame key (`Probe.cs:98-100,255`). Historical duplicate-key events remain untouched.

## Frozen delivery and validation

| Artifact | SHA256 |
|---|---|
| managed/Probe.cs | 7312C4F6838EE134BC66FE9938EDED8878227570A266CE833C4CEE6DC4345313 |
| managed/Protocol.cs | 396AF9F6FC6EF101F800FE0F5F3FC4E5555F2EC8BBA39B435622900B1AAF2CBD |
| JSUnityProbe.dll | EE8267B06F7A16E60829FC7E4C6789DABDC9DA5BC5D1487FDE8C9BE47A849B69 |

The reviewer independently reread these file hashes and the following saved receipts, whose source hashes match the frozen files:

- `managed-schema-check.json`: 70 PASS / 0 FAIL, including all 11 added fields and 20 exact declaring-assembly type identities.
- `managed-protocol-check.json`: 38 protocol/resource checks PASS; the new pure checks cover unchanged fields, an increase, and a temporary change followed by reversion. No Unity/game code was loaded by those checks.
- `managed-build-check.json`: four metadata checks PASS, compile exit 0, x86/Requires32Bit, metadata v2.0.50727, only exact installed mscorlib 2.0.5.0 and UnityEngine 0.0.0.0 references; internal identity JSUnityProbe_1b17560a74d66532.

The reviewer ran only targeted read/hash operations and inspected the saved checks; no rebuild or test execution was repeated. Remaining work is controller-owned native reseal/staging, new read-only runtime snapshot and separate action/restore/visual/resource/cleanup validation. This report does not establish success of any new live attempt or zero resource consumption.
