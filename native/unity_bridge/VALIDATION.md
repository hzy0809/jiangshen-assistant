# Normal mesh selection and bounded template diagnostics

Status: DONE_WITH_CONCERNS. This increment removes three source-proven extra preconditions: selection now supports normal non-direct modes 0/2/3 and the mesh-click route without the separate bubble's show_arrow flag; prepare accepts the game's unlimited short count -1 as well as positive counts; the normal non-Anecdote next-chapter button no longer requires its title/layout flag m_bNextChapter. Direct mode 1, unknown modes/counts, completed campaign/troop, level, root, input, animation and unknown overlay gates remain. Selection now explicitly verifies an active current-root mesh, one exact CampaignTroopScript on it, matching ParentUI/Troop and script-local IsEnable. It never relies on the mesh script's cached rmg, which the game's AddComponent/parent ordering need not initialize.

The assigned offline checks pass: 160 production Campaign fixture checks, 168 file-backed metadata checks and 70 Python bridge tests. Tests cover every supported non-direct mode with show_arrow=false, direct/unknown mode rejection before publication, missing/disabled/wrong-owner/duplicate/current-root mesh relationships, completed progress rejection, -1 success/zero and other-negative refusal, and m_bNextChapter=false using the same normal button while unsettled/Anecdote/disabled/wrong-root cases remain blocked. Pure diagnostic reads do not invoke a command or begin an attempt. Fixed inherited template fields, six scenario IDs, signed short limits and the read-only getter field chains are verified against the pinned assemblies without loading them.

Optional strict diagnostics report current character campaign/level, fixed campaign flags and at most eight sorted eligible rows, including finish counters and teaching/scenario IDs. The parser bounds every field and rejects unknown paths/types/oversized rows. Diagnostic values do not grant execution permission. New snapshot data is still needed to identify which original selection condition rejected the real target; this source increment does not claim its live cause is known.

Start was compared with FormationUI's ordinary fight-button branch (onfight=true then OnClickSetCurrentForm); result ACK was compared with BattleNormalResultUI's Hide/battle_exit and BattleResultUI's empty-mine_ui Hide/OnClose branch. No same-kind count or presentation-flag mismatch was found in those named branches; the existing settled-attempt/identity guards are preserved. No additional game action was added. Exact new package/build identities and raw checks are in build/campaign-validation.json and data/acceptance/20261031-campaign/06-normal-selection-fix/. Probe/OnGUI, native C, the receipt reader and unchanged lifecycle/receipt/native-body checks retain their prior source-hash coverage. This leaf performs no game-process, UI or network operation; full integration and live acceptance belong to the controller.

# Python receipt-read contention follow-up

Status: DONE_WITH_CONCERNS. The Python-only follow-up retries a complete same-path open/read for Windows sharing or lock violations (WinError 32/33), with one 250 ms budget and 10 ms steps. Each failed handle closes before reopening. It never retries a command or changes a sequence. Other I/O errors, close failures, malformed/oversized/invalid-type JSON and protocol/schema failures remain immediate failures. Exhaustion raises the first original OSError and records its errno, winerror, filename, read stage, attempts and elapsed time in the first UNKNOWN cause; the existing lease and no-resend rules remain intact.

Targeted Python result: 67/67 PASS, including actual owned-temporary-file `CreateFileW(share=0)` short-release recovery and long-held bounded failure, mock read-stage 32/33 and handle ordering, ACL/other-error and non-Windows negatives, parse/size/type/decode/schema negatives, and matching-ACK/no-resend/UNKNOWN retention. Raw results, measured Win32 samples and exact source hashes are in `data/acceptance/20261031-campaign/04-python-read-contention/`. The first exploratory run's single error was a newly added assertion using ledger `lifecycle` instead of its actual `state` field; it was corrected before the passing run.

The frozen native/managed build remains `dc398b6b41bd75e6f0558e53509ffab522f24806f2af0170e44fc42617119464`, with the same three packaged asset hashes, manifest, protocol 3/native ABI 2 and `scoped-campaign-gui-skip-v1` marker. Native/managed sources and their preceding checks below are reused by exact hash; this follow-up did not rebuild them or run the full Python suite. Live receipt recovery and business acceptance remain controller-owned. The observed external lock holder is unknown.

# Scoped CampaignUI and normal GUI Skip validation

Status: DONE_WITH_CONCERNS. Current source/packaged build and the assigned offline matrix are complete. Root owns independent re-review, full Python integration and live sampling/acceptance. This leaf performed no game-process operation, injection, input or game-network action.

Current build: `dc398b6b41bd75e6f0558e53509ffab522f24806f2af0170e44fc42617119464`; assembly `JSAssistantBridge_dc398b6b41bd75e6`; exact interface `scoped-campaign-gui-skip-v1`; control protocol 3 / native ABI 2.

| Artifact | SHA256 |
| --- | --- |
| bridge_host.exe | `3ad7df3b5bba84809f85d992c768e31ca4d7ff03e44de6f3de6dd4ed84a4313f` |
| bridge.dll | `169a6433bd202f4095913dda935552398afb1f97985ae5b192b64245fff73275` |
| JSAssistantBridge.dll | `3b5a05b1c5b334bd73e89db52bf7e6a1dec7498460536ff7a4429f34160f57ac` |

Proven cause: named sessions `fd1ce8a08dd34a04a0c1ea0dac296928`, `13dc4ed706bc4a2eab1409b8669c1c38` and `8b09d950571e492ea713679146f223b9` all reached real Update readiness, then failed in the same frame with `campaign_ui_not_unique_CampaignUI`, command sequence 0 and no mutation. The old selector threw only for more than one exact-type candidate. Static `RootUI.sc_campaign_enter` references the same CampaignUI class in Campaign, Anecdote, Fairy and TalentCampaign scopes, while BaseAsset Awake/Start binds rmg from transform.root. Thus global uniqueness is an invalid premise. The old receipts do not contain candidate counts/root distribution; which actual copies were present remains UNVERIFIED.

The replacement keeps exact uniqueness after current-root identity/hierarchy/LevelID proof. Same-root duplication, root snapshot mismatch and wrong ownership still block business without destroying the helper merely for UI ambiguity. Battle parent/previous-root links preserve the normally inactive Campaign root. The fixed global Scenario is separately linked by m_prevUI. Bounded `ui_context` is intended for root's first fresh read-only sample.

The user explicitly replaced next-section advancement with normal Skip. Complete game Skip semantics execute only inside the helper's real OnGUI, including natural triggers and server/end callbacks; no direct progress patch or follow-up training is exposed. A short queued command has durable one-shot ownership, root/revision/data/Character rechecks and stop/lease priority. No-OnGUI, publication failure and exceptions preserve noninvocation/UNKNOWN distinctions. Original battle authority and prior-business guards remain in force.

| Offline criterion | Result | Artifact |
| --- | --- | --- |
| BridgeClient typed protocol, marker, bounded diagnostics, prior business, ACCEPTED/final ACK | PASS, 55 tests | `build/python-bridge-validation.txt` |
| Production Probe lifecycle + owned real-callback fixtures | PASS, 149 checks | `build/managed-lifecycle.json` |
| Production CampaignModel/Observer scoped-root, GUI token and counterexamples | PASS, 127 checks | `build/managed-campaign.json` |
| Fixed game metadata and signatures | PASS, 133 checks | `build/campaign-metadata.json` |
| ReceiptStore Windows sharing | PASS, 814 checks | `build/receipt-sharing.json` |
| Native one-shot/creation/cancel/timeout and paths | PASS, paths 6 checks | `build/native-synthetic.jsonl`, `build/native-paths.json` |
| Legacy production compilation, x86 metadata, sealed assets/source hashes | PASS | `build/managed-metadata.json`, `build/asset-verification.json` |

Named counterexamples include cross-root preloads vs same-root duplicates; same LevelID with different root; wrong cached rmg/hierarchy; snapshot/root transition; inactive previous root and missing active Main; wrong Battle previous/own root; external Scenario wrong parent; global-false Skip vs every other action; actual result/chapter behind Scenario; GUI revision/root/data/Character replacement; repeated GUI events; queue→publish stop→OnGUI without Update; stop during capture before ownership; lease expiry; 1.5-second no-callback rejection; ownership-write failure; and GUI exception with no replay. The same unbound new BattleUI may be observed hidden after setReport and before Show: it remains pending and cannot become authority until its visible zero-report witness.

Live candidate distribution, actual helper OnGUI delivery/Skip rendering, embedded authoritative battle receipt capture, real resource consumption and three-consecutive-loss acceptance remain UNVERIFIED/controller-owned. Root's manual Skip/reward observations are not an automatic run or battle receipt. Full identity and source hashes are in `build/campaign-validation.json`; named-session/static source evidence is in `build/root-context-skip-evidence.json`.

## Historical protocol 3 candidates

The following sections are historical; the package and checks above supersede their build/source identities and next-only semantics.

# Protocol 3 campaign bridge leaf validation

Status: DONE_WITH_CONCERNS. Source, packaged production build and assigned offline checks are complete. The controller owns independent review, full integration runs and all live acceptance. This leaf performed no game process inspection, injection, input or business action.

Current build ID: `731f07550dc1a2d2424d07994e7c30577a3afbd11b3ab011a6774630e9c4abc2`; assembly `JSAssistantBridge_731f07550dc1a2d2`. Control protocol 3 strictly rejects protocol 2; native ABI remains 2. Production session root remains `G:\development\enjoy\jiangshen-assistant\data\object_sessions`.

| Packaged artifact | SHA256 |
| --- | --- |
| bridge_host.exe | `67aa6af931bfc3db94505195a1c6bb30abc81c6275ac477cb75b253ae41883ad` |
| bridge.dll | `e9363e32cf09f624e62423ed8d2d202251528bf47be1bc1bfe35fcd213841f6d` |
| JSAssistantBridge.dll | `cf1ccc3b0238e0cf3c3dc4e82cc9cfd132bf6c87da272e0eda2ccb1fcb283f87` |

`build/campaign-validation.json` contains the exact Python, managed, build and test source hashes and all evidence paths. Its validation layer is explicitly `offline_only`.

| Criterion | Result | Evidence and limit |
| --- | --- | --- |
| BridgeClient protocol, typed args, correlation, authority schema and unresolved business guard | PASS | 51 tests; `build/python-bridge-validation.txt`; own files and endpoint fakes |
| Actual production Probe lifecycle | PASS | 116 checks; `build/managed-lifecycle.json`; own Unity/model stubs |
| Actual production CampaignModel/Observer | PASS | 91 checks; `build/managed-campaign.json`; fixed GUI branch gates, duplicate attempts, packet/result/target/character/replay/report conflicts, lost receipt, no-count purchase rejection, typing waits and unchanged configuration; owned fixtures |
| Pinned game metadata and exact fixed signatures | PASS | 114 checks; `build/campaign-metadata.json`; PEReader only, no installed assembly load; character id Field040001F6 is inherited public u64; packet getter Method060023D7 IL `02-7B-34-21-00-04-2A` is a direct field return |
| Native one-shot/process-creation/cancel/timeout and immutable cache paths | PASS | `build/native-synthetic.jsonl`, `build/native-paths.json`; own processes only, six path checks |
| Production ReceiptStore Windows sharing | PASS | 814 checks; `build/receipt-sharing.json`; no Unity-thread retries |
| Production build, metadata, packaged seals/source hashes/exports | PASS | `build/managed-metadata.json`, `build/asset-verification.json`; legacy v2 metadata, x86, exactly two managed references; native source/ABI unchanged |
| Live embedded authoritative packet capture and natural application | UNVERIFIED | If zero transition/packet/identity/normal applied result is missing, status becomes UNKNOWN and another attack is forbidden |
| Real game resource cost, normal dialogue/result/chapter flow and automatic three-loss stop | UNVERIFIED | Controller-owned runtime and business acceptance; offline checks do not establish these |

The schema is documented in `README.md`. Unknown teaching/trigger paths are blocked. The exact nonblocking `new-skill` hint is reported without changing skills/army/formation. Ordinary no-trigger dialogue preserves the normal next-input flag and GUI callbacks; it never invokes skip or direct Close. Business effects are recorded independently of the existing Disable/Restore action; stop does not roll back game resources. Physical component cleanup can be clean while business remains pending. Any pending fact for the same process creation blocks all campaign commands; each fact retains its original role for attribution.

Independent review of the previous protocol-3 candidate `d6ed2c75...` found P1 role attribution loss across A→B→stop→A and P2 generic request bypass of typed admission. That candidate was not loaded by this leaf. The current candidate serializes the original Begin role in attempt/business receipts, persists only that attribution and treats missing/conflicting attribution as unknown. It rejects public generic business requests and shares locked validation with the actual publication boundary. Regression coverage includes the exact role-switch sequence, replacement Character object with the same ID, ledger-only recovery followed by a second clean stop, missing/conflicting origin, public/private/send bypass attempts, and checks before sequence/file allocation. Independent re-review and live acceptance remain controller-owned.

A subsequent P1 re-review found that an older guard B could hide a newer crashed session A during lease reclaim. The Python implementation now merges all pending guard/ledger/state/inherited facts and blocks campaign admission across every role in that process. The exact legacy B-guard + A-pending clean-helper/crashed-Python sequence is covered through another read-only stop/reconnect; both original roles remain recorded, another start is not published, and observe/heartbeat/stop remain usable. Repeated merges deduplicate exact facts; conflicting facts remain blocked. Other process identities and known nonpending history are covered as negatives. This follow-up changes Python source/tests and documentation only; the managed/native build and its preceding targeted check sources remain exactly the `731f0755...` sealed files above. `build/campaign-validation.json` adds a whole-leaf source identity to distinguish the Python follow-up from the unchanged managed build.

## Historical protocol 2 validation

The following record describes the previous verification-only build and is retained as historical evidence. It does not describe the current packaged build.

# Production bridge leaf validation

Status: DONE for source implementation, packaging and assigned offline/synthetic checks. Independent final source review and live GUI/game acceptance belong to the controller. No worker live game operation occurred. Frozen research artifacts were preserved.

Frozen production build ID: `34cc13532caca93ea9575f91fefd583033a0721ad5b7cf42545f41b94de838ed`.

| Packaged artifact | SHA256 |
| --- | --- |
| bridge_host.exe | `7e8a995f76b6cdda8c1cfc31c93d4267742c1f00648bbcdcee39f443114ca93c` |
| bridge.dll | `be7985f08f33c4fc5c1ebaa0aef0b9e38dbd809c57c9d24f7739073ece7831d8` |
| JSAssistantBridge.dll | `15257772268bd61d92e1c127233f046c373be80370a3749681fc331edaeb7c8c` |

Internal assembly identity: `JSAssistantBridge_34cc13532caca93e`. Full source hashes and supported game identity are sealed in `jiangshen_assistant/runtime/win32/manifest.json`. The only production source change after the previous `3fdfc096...` freeze is the explicitly authorized ReceiptStore publication fix; native/Probe/Lifecycle/Protocol source hashes remain unchanged.

| Criterion | Result | Evidence and scope |
| --- | --- | --- |
| Production native/managed compilation | PASS | Zig x86 and legacy csc, warnings as errors; compiled assets require no compiler during GUI launch |
| Exact managed reference profile | PASS | `build/managed-metadata.json`: only mscorlib 2.0.5.0 + UnityEngine 0.0.0.0, v2 metadata, x86 Requires32Bit |
| Manifest/source/file/seal/hash/export coherence | PASS | `build/asset-verification.json`; three PE32 assets, five undecorated native controller exports, fixed payload hash/name embedded in DLL |
| Expected process creation before native admission | PASS | Arm rejects wrong expected creation; callback independently rejects a post-Arm altered creation before Mono or persistent self-retention |
| Cross-process native callback affinity and one-shot | PASS | Final synthetic child PID 40252, TID/callback TID 11168; duplicate message calls=1 |
| Cancel/unhook/timeout lifetime distinctions | PASS | `build/native-synthetic.jsonl`: cancellation calls=0; timeout RUNNING/unfinished despite hook removal; later completed once with retained module |
| Immutable app cache path whitelist | PASS | Six positive/negative production-function checks in `build/native-paths.json` |
| Actual production managed lifecycle under owned synthetic Unity/model | PASS | 115 checks in `build/managed-lifecycle.json`; heartbeat renewal past old 120s limit, readiness handshake, repeat after restored action, stop-during-action restoration, delayed OnDestroy reconnect gate, clean same-process reconnect, lease-expiry queued-action rejection, recovery UNKNOWN and reconnect block |
| Bounded long-session receipts | PASS | Actual ReceiptStore writes 1200 synthetic events, retains two segments <=512 KiB each; latest-state atomic replacement checked |
| Production receipt replacement with open Windows readers | PASS | `build/receipt-sharing.json`: 814 checks, two first publishes, 200 state/ack replacements while share7 readers retain old complete versions; new paths expose new complete versions |
| Receipt failure remains conservative | PASS | Two share3-held targets reject replacement with IOException, preserve old target and new temporary bytes, then publish after the reader closes; no delete/in-place overwrite or main-thread wait was added |
| Packaged native dependency resolution | PASS | `build/native-load-smoke.json`: independent 32-bit own DLL LoadLibrary/GetProcAddress and unarmed local unload; no Arm/Mono callback |
| Source revision covered by targeted checks | PASS | `build/targeted-validation.json` records exact source and harness hashes for the completed run |
| Actual Mono cached-image reconnect behavior | UNVERIFIED live | Fixed prior/current app cache path, same SHA and internal Assembly-table identity are source/compile/path checked; actual game reuse is controller acceptance |
| Unity Update, game state, direct UI action and visible restore | UNVERIFIED by worker | Requires controller's reviewed live run; a synthetic PASS does not establish these layers |
| Native/managed complete unloading | Process exit required | Own component destruction and hook removal do not unload assemblies or retained code/seal handles |

The independent reviewer identified the PID-reuse gap in the original host admission shape. The final native protocol 2 appends expected process creation FILETIME to host arguments, keeps a query/synchronize process handle, and rechecks it in Arm and callback before Mono. That finding is resolved in the frozen source and synthetic negatives. A first draft build also exposed a generated-header PowerShell array-format error; it was corrected before any final asset or live operation.

The first controller-owned live session later retained command48 plus a fully written command49 temporary file and no managed49 acknowledgement. Its original publisher exception was not retained. This supports a failed publication boundary, but does not establish the original WinError or lock holder. Subsequent independent owned-file testing on this host showed `os.replace`/`MoveFileExW(REPLACE_EXISTING)` fail with WinError5 while target readers remain open, even with share7, both with raw HANDLEs and CRT fd wrappers. Closing the reader made the same operations succeed. `ReplaceFileW(target, temporary, NULL, flags0)` succeeded with share7 and preserved the old reader's complete bytes; share3 failed with WinError32. This falsified the earlier assumption that DELETE sharing alone suffices for MoveFileEx replacement on this host. Initial contradictory results are retained separately rather than discarded.

The controller authorized a minimum managed fix: existing receipt targets now use ReplaceFileW; first publishes use a non-replacing WRITE_THROUGH move. The Python owner separately owns shared receipt readers and bounded command publication retries. This leaf did not modify command parsing, sequences, lifecycle/restoration guards, Python or GUI code. Independent 16-case Win32 evidence and initial falsified-hypothesis output are under `data/acceptance/20261031-object-gui/native-io-check/`; the actual production-function test is under this source folder's build reports.

Owned changed paths are `native/unity_bridge/**`, the packaged four files under `jiangshen_assistant/runtime/win32/**`, and the separately assigned owned Win32 fixture under `data/acceptance/20261031-object-gui/native-io-check/**`. Python production code, GUI, business workflow and frozen research files were not edited by this leaf. No gameplay, network/backend, authentication/configuration read, automatic action retry, arbitrary eval or additional business action was added.

Remaining controller work: final source review readback; backend/GUI integration tests; current PID/window/creation admission; read-only bootstrap and genuine Update evidence; action/restoration and visible acceptance; clean reconnect; stop/F8/close/crash lease behavior. Preserve PASS/FAIL/UNKNOWN separately for those outcomes. Recovery UNKNOWN retains ownership until normal game exit and must not be promoted to clean by deleting a receipt or recompiling another assembly version.
# 2026-10-31 current-frame settlement diagnostics (read-only)

Build `bb4ef77364d9de6ed84baa2d2aa4247ad4498b15b58c3dd4a452c20075a257db`
adds optional, fixed `settlement_diagnostics` to campaign snapshots. Evidence is
scoped to `this_helper_instance`: NONE observers expose unavailable historical
flags and reference comparisons as null. The current bound Battle/normal-result
ReportIDs remain separate u64 decimal strings. The fixed parent Campaign troop
is read by Battle target ID even when hidden, killed or ineligible. The original
Begin captured no troop `data_` anchor; `data_anchor_match` therefore stays null.
No packet hooks, action parameters, Begin/Observe calls, guards or settlement
criteria were added. Diagnostics are outside ContentJson and its revision hash.

Assigned offline results: production legacy build PASS; Campaign fixtures 177
PASS (previous 160 retained); CampaignBridgeTests 34 PASS; pinned metadata 174
PASS; packaged assets 3 PASS; exact Compact 2.0.5/UnityEngine reference gate PASS.
Production-source fixture JSON also passed the real Python diagnostic and full
campaign parsers. Fifty-nine existing method source slices match the pre-change
copies exactly. Twelve other source hashes and four historical test receipts
were verified for reuse; those suites were not rerun. Full Python integration
and live probe remain controller owned.

Evidence: `data/acceptance/20261031-campaign/09-settlement-readonly-diagnostics/`.
`readonly-proof.json`, `production-fixture-diagnostics.json`, `freeze.json` and
`REPORT.md` define the delivered scope. A raw ReportID, UI result, or finish-count
sample does not establish a confirmed WIN/LOSS. Real settlement and real LOSS
remain UNVERIFIED. The separate pre-existing UseEnd/pending-packet concern is
outside this diagnostics-only change; all original settlement rules are intact.

# 2026-10-31 final settlement observation

Build `ebe69db9e64da4781880b071640bd160290d378ff7d5c9172d544ccc38d63d0c`
changes Begin/Observe and fixed frame capture. The 09 diagnostic-only no-change
proof remains historical. Current evidence is under
`data/acceptance/20261031-campaign/11-settlement-capture/` and
`13-settlement-integrated/`.

The helper makes bounded immutable copies of queued 1032 payloads under the
game client's zero-wait monitor. It does not change stream position, queue or
handlers. A copy is not an applied receipt until the exact queued item has
gone and the bound battle/result/report and flags match. UseEnd=true embedded
packets cannot substitute for a late final response. Early result visibility
waits for actual settlement rather than immediately declaring UNKNOWN.

Independent one-time mainline progress can prove only WIN: the original
scene, campaign, troop/data, character and report identities stay fixed, the
original count advances by exactly one to its unchanged maximum and killed
becomes true, and the natural report/result/flags agree. GM/debug and pending
979/1025/old1032 are excluded at Begin. This relies on a single controller
without concurrent manual or other-client actions. It has a separate evidence
value and never pretends to be a captured packet. Lack of progress does not prove a loss.
Missed packets without this progress still produce UNKNOWN.

Controller build with exact Compact references PASS; native synthetic/path
checks PASS; production lifecycle149, campaign300 and receipt814 PASS. Pure
protobuf fixtures and pinned file metadata201 PASS. Current packaged metadata
is x86/v2 and references only Compact mscorlib2.0.5 and UnityEngine. Full Python
258 PASS with all source/runtime inputs stable. Independent implementation
review and real new-attempt acceptance remain separately recorded; these
offline results do not establish actual LOSS or three-loss behavior.

# 2026-10-31 integrated package and actual campaign acceptance

Final build `3032af89de9c037d9ab6b08fc08df5ce81b3878acf3c04eaa9f78dc4b46e78dd`
supersedes the package identities above. Exact source/runtime identity is in
`data/acceptance/20261031-campaign/18-final-campaign/freeze.json`.
Production build and strict legacy metadata PASS; campaign499, lifecycle149,
receipt814 and native/path checks PASS; pinned file metadata235 PASS; full
Python262 PASS in13.330s with every input hash stable. Independent final
implementation and package review CLEAR is recorded separately.

Normal campaign reward confirmation is narrowly bound to the actually settled
battle and original troop, including when the game disables global root input.
Scenario text uses the original Skip. A completed text with a ready bounded
new-feature icon intro executes only the original left-click assignment
m_bMoving=true in the real GUI admission; the game retains its animation,
list clearing, FunctionLoad, system acknowledgement and OnClose. Unknown
function/configuration dialogs remain blocked.

Actual evidence15 confirms targets57:5/6/7 under the earlier compatible
settlement/reward build. Evidence19 confirms the final build naturally
completes653:2, then wins57:10, skips556 and confirms reward. Targets5/10 used
captured final1032 receipts;6/7 used the independent progress-WIN path and
explicitly show receipt_captured=false. Each result was consumed once, losses0.
The final GUI is paused at SpecialGetUI for general明月, without a pending
battle. Its button changes deployment/equipment and was not automatically
invoked. This is an explicit remaining user configuration decision, not a
failed battle. Real LOSS, real three-loss stopping and per-battle resource
causality remain UNVERIFIED.

