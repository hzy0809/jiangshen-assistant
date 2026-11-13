# Managed probe contract

Status: source delivered; protocol/resource and metadata offline checks PASS.
Controller owns native integration, live loading, the explicit action command and
visual acceptance; it assigned the final payload compile to the managed author.
The author has performed no live loading or game operations. The controller's
first diagnosis with the earlier build proved Update callbacks and MainUI lookup,
then stopped with `action_used:false` and `component_destroyed`; see session
`sessions/64731d2da0864727a8d6cf0ce6eecd0c/probe.events.jsonl`. The present build
corrects its conservative ambient-component rejection and requires a new sealed
identity/session. A second controller diagnosis passed the overlay checks but
found the original Luoshu feature locked; it also stopped with no action and
destroyed its helper. The current independently selected operation is the
existing MainUI interaction Disable/Enable pair, not a way around that unlock.
Neither diagnosis executed or proved a UI action.

Current payload SHA256:
`5CCA9F8804454EA34A6ABA9D3ECB28A74AD06B56A5CA071DA77A02A5FAD9AFF1`;
internal identity `JSUnityProbe_c3fec61cde08ec23`.

## Bootstrap ABI

Assembly file `JSUnityProbe.dll`, type `JiangShen.ManagedProbe.Entry`, public static:

```csharp
string Bootstrap(string absoluteSessionDir, string nonce, int verifiedUnityNativeThreadId)
```

The sealed filename stays `JSUnityProbe.dll`; its internal assembly identity is
`JSUnityProbe_<16-hex-source-identity>`, derived from the two payload source hashes.
The `bootstrap` event records that internal identity. The build retains the
identically named unique DLL in `managed/build/` and copies its bytes to the
fixed filename for the native seal. The parent stages each attempt at a unique
session/bin path. A changed build can be loaded with a distinct Mono identity
only after a failed **known-no-action** diagnosis, with the prior helper's
destruction established. This does not unload the old assembly, authorize a
second action or permit retry when action outcome is unknown. Rebuilding the
same source produces the same internal name and is not a reload strategy.

The native caller must independently establish the Unity window owner thread,
`mono_thread_current() == mono_thread_get_main()` and a valid current domain.
Call synchronously on that verified thread; do not use a remote worker thread for
Unity calls. Managed code independently compares Win32 `GetCurrentThreadId()` to
the supplied thread ID before any Unity API. `kernel32!GetCurrentThreadId` is the
only P/Invoke; there is no callback or import into the custom native bridge.

`nonce` is exactly 32 lowercase hexadecimal characters. The directory must already
exist and resolve exactly to
`G:\development\enjoy\jiangshen-assistant\research\unity-object-layer\bridge\sessions\<nonce>`.
The bootstrap uses `FileMode.CreateNew` for `probe.events.jsonl`; no game directory
is written. This fixed path and per-session nonce identify a command channel, not
an authentication boundary against another process running as the same Windows user.

Success returns ASCII, fewer than 256 bytes:

```text
BOOTSTRAP_OK;protocol=1;native_tid=123;managed_tid=1;await=update_ready
```

Failure returns `BOOTSTRAP_REJECTED;thread|nonce|already_started|session_path|initialization`.
Bootstrap creates only its own hidden GameObject/MonoBehaviour and writes identity
metadata. It performs no game type lookup, target enumeration, resource read or
UI action. Return from the native hook before waiting for receipts.

The first Unity-generated `Update` writes `update_witness`; only after two
callbacks with increasing `Time.frameCount` and matching OS/managed thread IDs
does the component write `update_ready` and create the read-only game model.
The initial snapshot follows. `update_ready` alone proves callback execution,
not that object discovery or action gates passed. The component never establishes
Unity main-thread identity merely from `mono_thread_attach` or the bootstrap call.

## Fixed command protocol

Write an ASCII temporary file in the session directory and atomically replace
`command.txt`. Exactly four lines, LF or CRLF; one trailing newline is permitted:

```text
version=1
nonce=0123456789abcdef0123456789abcdef
sequence=1
command=snapshot
```

Sequences are positive decimal integers without leading zeros, at most nine
digits, strictly increasing from the last consumed sequence. Input is capped at
256 bytes/characters. Invalid, stale, wrong-nonce and extra-argument commands do
nothing. No arbitrary member name, object path, script, argument or evaluation
payload is accepted.

| Command | Effect |
| --- | --- |
| `diagnose` | Read-only fresh snapshot. |
| `snapshot` | Read-only fresh snapshot. |
| `disable-restore` | One explicit transaction: require original MainUI interaction enabled; invoke inherited Disable, observe false across Updates, hold six seconds, invoke inherited Enable, observe true, destroy helper. |
| `stop` | Destroy helper if idle; if action is active, start same-instance restoration immediately, then destroy helper. |

Bootstrap and initial observation never create an action command. Only the
controller may submit `disable-restore` after reviewing evidence. The retired
`toggle-restore` command is rejected. A valid action
request consumes the session's one-shot budget even if its preconditions reject
the action. It cannot be repeated through that assembly's entrypoint.
During a transaction only `stop` is consumed; snapshot requests wait until idle,
and a successful transaction ends the component before further commands.

## Lookup and action gates

All checks and reflection run from the witnessed `Update` thread. The component
resolves loaded assemblies only; it does not load proprietary assemblies or
reference them at compile time. Before discovery it checks exact SHA256 of the
files backing the loaded mscorlib, UnityEngine, Assembly-CSharp, firstpass and
pbdata assemblies. These hashes establish backing-file identity; they do not
prove that no third-party in-memory patch exists. Required profile identities:

| Assembly | Identity / SHA256 |
| --- | --- |
| mscorlib | Version 2.0.5.0, `3F34C90AB282E8F3546946263087FB5002CF96EF9F412DD4011DAB3F0F7CD8A4` |
| UnityEngine | Version 0.0.0.0, `D479BEFC03AFB0F2071FFCF108847F06A0D560075EB8A76980F0AE929115ED73` |
| Assembly-CSharp | `C1C8EBAC7DDEF4173C873A00774975A5CA366F95B19164C4CA775165EB994911` |
| Assembly-CSharp-firstpass | `29D8FA2CB55E800CCDF1CDC6C5B274B924EA7D03C989296DFA569731E02A8D87` |
| pbdata | `905CF19394545E2C4B5D49D994A7D981E13AE38A2A629F7D02329F27C6D2D5AB` |

`Object.FindObjectsOfType(MainUI)` must yield exactly one enabled component on
an active GameObject whose concrete type is exactly the pinned MainUI type;
unseen subclasses are rejected. Its `BaseAsset.rmg`, transform root and
`BaseAsset.lds.currmg` must agree. The current root must be active with a known
LevelID. LoadScreen must be hidden, not battling, and RootMessenger must not be
loading or waiting for assets. MainUI must have completed Start and asset
initialization. `Global.extern_enabled` must remain true in every phase; the
original `__BaseUI.enable` must be true before the action. The probe's own false
value is permitted only for that exact saved MainUI during its active transaction,
and only after all remaining context/overlay checks have passed. No generic
disabled-context bypass is used. `__BaseUI.my_enabled` is deliberately not a gate
because it belongs to the last GUI drawing context.

BuildingBuildUI visibility must be false; raw `MainUI.m_subui` must be null;
UICount_Visible must be zero. Independent enumeration uses exact `Type` objects
from the pinned assemblies, including the RootUI exemption; a same-named type
from another assembly does not pass. The source-audited conditions are:

| Exact type | Required condition |
| --- | --- |
| CSharp RootUI | Session controller. |
| CSharp BuildingMonsterScript, SecretShopNPC, CheckLauncherVersion, AnnounceBehaviour, MeditationUI, ChatBehaviour | Audited ambient scene/background components; enabled alone does not denote a modal window. |
| CSharp BuildingBehaviourScript | Both instance `m_bEdit` and static `m_bEditVisible` false. |
| CSharp FlowerMessUI | Both centered-panel flags `fc` and `rm` false. |
| CSharp CapitalUI | `curArea` nonnull, exact firstpass Database.BuildingArea, `unlocked` true. |
| firstpass PopupUI | Static `popup` false (also checked globally). |
| CSharp MessengerUI | Exact declared dictionary/list objects, active and pending session counts both zero. Counts only; no keys, sessions or messages enumerated. |
| firstpass TooltipUI | Both `TooltipFlag` and `tttitle` exactly empty; no flag/title values logged. |

These predicates come from `overlay-*.cs.txt` source slices and are rerun every
transaction phase before the final owned interaction-state allowance. Tooltip's Hide/Update ordering
can conservatively block one extra frame. Unknown enabled active `__BaseUI`,
null state, unexpected collection/object types or an active conditional panel
reject the action. Visible type names may be logged; player names, object names,
account identifiers and inventory are not read. The enabled MeditationUI is
observed background activity, not something this probe enables or disables.

The only invoked game methods are `__BaseUI.Disable()` and `__BaseUI.Enable()`
on that MainUI. They must be exact declared public virtual instance `void()`
methods on the pinned __BaseUI type, with no zero-argument Enable/Disable
declaration or override on MainUI or BaseUI. The concrete MainUI type check also
excludes unseen subclass overrides. A second capture immediately before invoke
must preserve enabled=true, component, character, root, scene, formation and the
resource allowlist. Formation count remains constrained to 1..6. No field is
assigned reflectively. There is no Luoshu method, enum/animation mutation or
`checkUI(90)` invocation in this payload: it exposes a different local UI operation.
The fixed Unity lookup API is invoked through a signature-checked reflection
wrapper, restricted to the two hardcoded MainUI/__BaseUI types. Generic
`AddComponent<Probe>()` creates the helper. This avoids importing UnityEngine's
desktop-mscorlib 2.0.0.0 `System.Type` signature into the payload; metadata
readback must show only exact mscorlib 2.0.5.0 and UnityEngine references.
No formation selection, gameplay handler, model setter, resource-consuming
operation or network sender is exposed. Source basis:
`../object-map-__BaseUI.cs.txt:1184-1192`; exact metadata/IL evidence is in
`overlay-method-metadata.txt`. Each method body only assigns the private `enable`
field to false/true and returns. MainUI and button render consumers are documented
in `overlay-resourcefree-candidate.md`.

## Evidence and lifecycle

Every JSONL record has nonce, UTC, actual native/managed thread IDs, callback
count and one observed `frame` key owned by the common emitter; Update receipts
do not add a duplicate `frame` key. Snapshots include instance ID, scene, root
LevelID, gate, `action_name: mainui_disable_restore`, `main_enabled`, formation and numeric
resource allowlist. The allowlist is level, stamina, gold, bind_gold, experience,
practice, spirit_food, mana, stone, reputation, base money, money64,
cur_formation_set and formation_set. `money` is explicitly the base numeric field;
the effective user balance is `money + money64`, as the source property defines.
Comparing both fields avoids invoking any property or losing the 64-bit balance.

Important receipts: `initial_snapshot`, `command`, `action_invoked`,
`action_verified`, `interaction_evidence`, `restore_invoked`, `restore_verified`,
`transaction_result`, `cleanup_requested`, `component_destroyed`.
State checks require multiple Update callbacks. The six-second hold continuously
requires the owned enabled=false state; an unexpected game Enable callback makes
UI verification FAIL. If restoration is already true before our Enable call, the
probe records `restore_already_observed`, skips a redundant call and cannot
report UI PASS for the required method pair. `restore_method_invoked` records the
distinction. Gray rendering remains explicitly `visual_gray_state: UNVERIFIED`
until the controller independently checks the screen; there is no animation claim.

Resources are compared before/after and at every sampled transaction Update.
`transaction_result.ui_status` assesses only action/restore/context evidence;
`resource_equality_status` separately reports PASS only when the entire numeric
allowlist stayed equal. Every field records its original/final number,
`final_equal` and sticky `every_sample_equal`; a temporary change followed by a
reversion is still visible. `resource_observations` counts distinct captured
Snapshot objects plus the baseline; repeated logging of one capture does not
inflate it and it is not a count of independent experiments.

A resource difference makes that equality criterion FAIL while successful UI
states may still have `ui_status: PASS`. Background meditation accrual and
server refreshes are competing causes; the component does not attribute either
an increase or decrease to the UI action. Even all unchanged sampled fields do
not establish zero consumption, zero packets or zero global game mutations.
Each transaction explicitly preserves `resource_consumption_causality` and
`global_network_effects` as UNVERIFIED. `cleanup_requested` also uses `ui_status`;
these replace the earlier ambiguous transaction/cleanup `status` wire field.

Transition, hold and restoration recheck the full context. Only a passing gate
permits further transaction handling; expected owned interaction=false is accepted
inside Capture only after every other check. A hidden component, globally
disabled input, loading or modal context ends the helper with
`restoration_unverified` and makes no further target invocation. UI PASS requires
observed disabled state, our actual Enable invocation and verified restored=true
on the same context, with no observed state drift or failure.

Idle lifetime is at most 120 seconds or 7200 Update callbacks. Transition and
restore phases each cap at eight seconds / 360 callbacks; the six-second visible
hold caps at twelve seconds / 1800 callbacks. Expiry or stop during the action
requests restoration. Restore never targets a new instance: component, character,
root, scene and formation must still match the original snapshot. If identity
changes, the component reports failure and does not modify the replacement UI.
Scene destruction can prevent restoration; `component_destroyed` records whether
it was verified. A frozen/paused Unity main thread cannot execute either a
watchdog or cleanup until Update resumes; wall-clock limits are evaluated then.

The helper has no global persistent patch and does not call DontDestroyOnLoad.
Cleanup disables/destroys its own GameObject and logs actual OnDestroy separately
from the request. Loading a managed assembly into Unity's default AppDomain is
not reversible by destroying a component: each assembly and its one-shot static
guard remain until process restart. A child AppDomain is unsuitable for live Unity
engine objects; no assembly-unload claim is made.

## Build and offline checks

The controller runs `managed/build.ps1`, using installed Framework v4 csc with
`/target:library /platform:x86 /nostdlib+` and references ONLY the game's exact
mscorlib 2.0.5.0 and UnityEngine. Explicit source inputs are Protocol.cs and
Probe.cs. No System.Core, tuples, tasks, async, LINQ, JSON library or proprietary
DLL reference is required. Keep ProtocolTests.cs out of the payload.

The author ran `managed/test-protocol.ps1`: **39 PASS**, exit 0. It compiles only
Protocol.cs plus ProtocolTests.cs against desktop framework, checks nonce,
monotonic/replay sequences, unknown commands, extra arguments, integer overflow,
line endings, retired action rejection, JSON escaping, unchanged resources, practice accrual, per-field
changes, change/reversion, schema rejection and 64-bit extrema without subtraction
overflow. The executable loads no Unity or game assembly. This proves protocol
and numeric observation behavior only; it is not the exact legacy payload compile,
runtime discovery, UI operation, visible result or restoration acceptance.

`managed/check-schema.ps1` independently decodes installed assembly metadata with
PEReader without loading/executing game assemblies. Result
`managed-schema-check.json`: **70 PASS / 0 FAIL** across all 40 reflection fields
(including inherited resources and conditional panel fields), three method
signatures, twenty declaring-assembly identities, two no-override checks, two
exact virtual declarations, two exact field-only IL bodies and the
MainUI/BaseAsset hierarchy. The receipt
pins input file and payload source hashes; rerun it after a source/installation
change. Controller-assigned final payload compilation is recorded separately in
`managed-build-check.json`.
