# Assistant Unity bridge assets

This folder owns the application bridge sources, build and independent targeted checks. Runtime assets are already packaged under `jiangshen_assistant/runtime/win32`; GUI launch requires no compiler and no research folder. The controller owns live game admission and acceptance. This implementation worker did not inject, invoke or operate the game.

The managed namespace is `JiangShen.AssistantRuntime`, bridge ID `jiangshen-assistant-win32-v1`, control protocol 3 and unchanged native ABI 2. It is separate from the research probe. The payload filename is fixed as `JSAssistantBridge.dll`; its internal assembly name includes the production source identity. `manifest.json` pins the three packaged assets, production sources, exact supported game hashes, fixed session root and lifecycle durations.

The application is an on-demand game script. Each explicit task starts by observing the current game's page and objects. Prior task journals, business guards, result dispositions and recovery seals do not participate in startup. Run files are diagnostic output; process lease checks only prevent concurrent ownership of a live helper. Current in-flight commands still require their own observations and are never replayed automatically.

## Runtime contract

The backend stages the three assets into an immutable shared bundle directory:

```text
G:\development\enjoy\jiangshen-assistant\data\object_sessions\<bundlehash32>\bin\
    bridge_host.exe
    bridge.dll
    JSAssistantBridge.dll
```

Each control session uses a separate random 32 lowercase hex nonce directory under that fixed root. The host computes that directory from the nonce; its DLL location does not select the control session. The Python owner serializes GUI access through its per-process-creation ledger. A separate process-wide named existence guard in the managed helper prevents a second app-specific helper across assembly versions; it is released only by the actual own-component `OnDestroy` callback after clean cleanup. Unresolved mutation retains that guard until game process exit.

The host entry is:

```text
bridge_host.exe --run <HWND hex> <PID decimal> <TID decimal> <nonce32> <expected creation FILETIME decimal>
```

Native status protocol is 2. The host opens only query-limited-information and synchronization process rights, checks the exact expected creation timestamp before arming, and retains that handle through callback outcome resolution. Arm and the target callback independently recheck creation identity. The one thread-specific `WH_CALLWNDPROC` hook receives one cookie-tagged `WM_NULL`; there is no generic remote-thread loader or arbitrary method invocation API. Existing native TID, Mono current/main-thread equality, current/root domains, pinned executable/Mono hashes and the sealed fixed payload are required. No thread attach, runtime initialization, main-thread setter or domain switch is used.

The fixed managed entry remains `Entry.Bootstrap(string sessionDir,string nonce,int verifiedNativeTID) -> string`; its bounded ASCII return uses the native bootstrap response format `BOOTSTRAP_OK;protocol=1;...`. That response is separate from protocol-3 control files. Bootstrap creates only the own persistent helper. Actual target discovery waits for at least two increasing, genuine Unity Update frames on the independently verified native and managed thread. The helper is marked `DontDestroyOnLoad` so ordinary scene transitions do not end a healthy GUI lease.

For reconnect, Mono may return a previously cached assembly. Native accepts its current or previous image only under the exact fixed own `object_sessions/<32hex>/bin/JSAssistantBridge.dll` layout, with the same SHA256 and exact internal Assembly-table identity. The current payload and any accepted cached payload are sealed by read-only handles denying writes/deletion. Shared immutable bundle staging reuses the same DLL path across clean reconnects rather than accumulating new pinned native modules. A still-active helper or unconfirmed physical cleanup prevents concurrent installation; historical task outcomes do not.

## Commands and acknowledgements

The backend atomically replaces `command.txt` with four ASCII lines for ordinary commands, or nine lines for the fixed campaign command, at most 768 bytes:

```text
version=3
nonce=<nonce32>
sequence=<positive monotonic integer, at most 9 digits>
command=<snapshot|diagnose|heartbeat|disable-restore|stop|campaign-observe|campaign-command>
```

Only `campaign-command` adds exactly these five ordered lines:

```text
action=<enter|select|prepare|start|continue|ack_result|next_chapter>
expected_revision=<positive canonical integer>
campaign_id=<observed uint16, zero only for enter>
troop_id=<observed byte, zero only for enter/continue/next_chapter>
attempt_id=<32 lowercase hex only for start; empty for other actions>
```

Control protocol 2 is rejected. The native process creation check, native thread gate, bootstrap return format and ABI remain unchanged.

`BridgeClient.campaign_observe()` and `campaign_command(action, *, expected_revision, campaign_id=0, troop_id=0, attempt_id="", timeout=5, cancel=None)` return the normal exact-sequence request envelope. `state.campaign` and `ack.result.campaign` contain schema 1 and exact interface marker `scoped-campaign-gui-skip-v1`, semantic `revision`, `available`, `gate`, `phase`, `character_id` as a decimal u64 string, current campaign/troop, `result_kind`, eligible real troops, typed tutorial and attempt. The manifest and every current ready state must match this marker: an older protocol-3 next-only payload is not a Skip implementation. Old clean receipts and unresolved business facts remain readable for cleanup and inheritance. A completed command has `ack.result.campaign_command` containing the exact command sequence, action, attempt ID, `invoked` and reason. This receipt proves only short invocation. It never proves a win or loss.

`attempt.character_id` and `business.character_id` retain the role frozen by Begin even after the current snapshot observes a different role. Missing or conflicting original attribution blocks every role; the current role and a guard's outer ID cannot reconstruct it. Public generic `request` rejects campaign commands. The typed executor validates the exact fixed fields and prior-business gate inside its request lock, and `_send` repeats the checks before allocating a sequence or publishing a file.

`CampaignModel` resolves fixed members of pinned assemblies, including dbdata, on the verified Unity thread. Current `LoadScreen.currmg` is read independently of active MainUI. Mainline CampaignUI selection requires LevelID Campaign, exact cached `rmg` identity and matching transform root, then still requires uniqueness inside that context. Preloaded Anecdote/Fairy/Talent CampaignUI instances do not become mainline targets. BattleUI has a separate root: it is the fixed `Battle/BattleUI` object with valid own root, previous root equal to currmg and exact CampaignUI parent owned by that root; the previous root may naturally be inactive. Global ScenarioUI belongs to the fixed `Root/RootUI` object and must point back to the current Campaign root. Ambiguity blocks business while retaining observation/heartbeat/stop. `ui_context` supplies fixed counts, root relationships and at most eight CampaignUI candidate IDs; it contains no names, messages or arbitrary object data.

Mainline unfinished normal troops only are admitted. Selection supports the ordinary non-direct modes 0/2/3 and rejects direct mode 1 and unknown modes. It follows the normal mesh-click path: active current-root mesh, one exact CampaignTroopScript on that mesh, matching ParentUI/Troop and script-local IsEnable, plus the existing mainline/progress/level/input/animation gates. `show_arrow` controls the separate GUI bubble and is diagnostic only. Prepare accepts normal unlimited `limit_count=-1` or a positive count; zero and other negative values remain blocked without entering the VIP purchase branch. Start retains `onfight=true` followed by `OnClickSetCurrentForm`, leaving the game's formation response and original `OnAttack` delegate to apply the normal gates. Next-chapter follows the normal non-Anecdote button independently of `m_bNextChapter`, which only controls its title/layout. No arbitrary type/member/RPC parameters, network handlers, purchases or configuration changes are exposed. Known scene chrome is allowed only by exact pinned type plus its actual owning CampaignUI/BattleUI reference.

Optional `selection_diagnostics` contains fixed typed values only: the current character's campaign/level, campaign input/progress/animation flags and at most eight eligible troop rows in ID order. Rows preserve signed limit_count, finish_count/finish_max, fixed teaching/scenario template IDs, mesh/script ownership and the fixed first failed selection condition. Unknown negative counts remain observable even though actions reject them. No player text, object names, arbitrary reflection path, template mutation or scenario invocation is exposed by these observations.

`CampaignObserver` runs independently of `ActionPending`. Before start it records the attempt and baseline; the first real report must be observed at ReportID zero with the same campaign/target and BattleUI identity. It captures only the embedded SCBattleEnd with result=0, type=0, matching target and nonzero report ID. SETTLED requires its natural disappearance, matching applied report and normal result UI with playback/wait flags cleared. Packet loss, report replacement, playback restart, surrender, identity change or conflicting evidence gives UNKNOWN and prevents another start. The observer remains passive; the shared battle-settlement owner temporarily wraps only the callback registered by its own early 1032 request. It retries only an explicitly bound `result=1` rejection, with a finite backoff, and forwards the real successful response to the original callback. Initial campaign iswin and report grades are never authoritative outcomes.

Tutorial NONE and the exact nonblocking HINT `new-skill` preserve the current configuration. Per the user's explicit Skip instruction, wire action `continue` now means the visible normal Scenario Skip button. It calls the complete pinned `scenarioSkip(Rect)` method, retaining normal remaining-section triggers, new-UI animation/drawing, FunctionLoad, server scenario-end response and Close callbacks. The Rect is calculated with the game's same `_rectRB` helper/layout branch. Text completion and local IsEnable are not Skip prerequisites; the source temporarily enables its button independently. The normal fade, loading/movement, visible current-section and exact ownership gates remain. Global input false exempts only this action, and Scenario phase forbids ACK/next-chapter even with those windows behind it. Newly opened unknown teaching or training remains blocked; no additional teaching button is exposed.

Skip is ACCEPTED and queued for at most 1.5 seconds, independently of the verification transaction. The helper's real OnGUI verifies thread, stop/lease, current root, revision, scenario/data/section/visibility epoch and Character reference/ID. It consumes the command before any GUI event can repeat it, publishes ownership, checks stop/lease again, then invokes the original method. Multiple OnGUI events cannot replay one command. A stop file is consumed before an intervening Update is needed; cancelling GUI seq 1 preserves stop seq 2. Missing OnGUI gives REJECTED with invoked=false; publication failure never calls the game; GUI exceptions give UNKNOWN, never success. `campaign_gui` records the originating sequence and queued/owned/final phase. ACCEPTED alone is not completion. No game progress, sections or newUI list is manually patched, and game effects are not rolled back.

`business` separately records mutation, pending attempt and `resource_rollback:false`. Clean component destruction does not imply that a game battle ended or that resources were restored. Before releasing a clean physical lease, Python durably retains unresolved business facts in a process-creation-scoped `.business.json` record and the session ledger, using each attempt's frozen role. Any unresolved business record blocks every campaign command for the same process creation, including after a role change. Role identity is attribution, never a bypass. Guard, reclaimed-session ledger, managed state and inherited facts are merged; exact repeated facts are deduplicated and conflicting values are retained. A read-only recovery session preserves all inherited pending records before releasing its lease. Heartbeat, observation and stop remain available. No old session or business record is deleted by this boundary.

Only one command is outstanding. An explicit stop may supersede an action only after that action was acknowledged ACCEPTED. No automatic action retry is supported. Stop and lease expiry prevent a queued action from starting. An expired/stopping/terminal session cannot be revived by a heartbeat.

Daily stone actions sample the owned Unity context and then reread the command file. Admission requires the same session nonce, sequence, command, action, operation ID, role, task and revision as the consumed request. After that exact read, only in-memory GUI, transport and deadline checks precede the stock call. A stop published after the exact read applies to the admitted attempt and its cleanup.

`state.json` and `ack.json` are independent latest-value JSON files replaced atomically. Forensic `probe.events.jsonl` rotates to `.1`; each segment is at most 512 KiB, with two segments retained. There is no 256-event or 120-second lifetime limit. `recovery_unknown.json` is an additional durable terminal mutation record when recovery cannot be confirmed.

Existing receipt targets are published with `ReplaceFileW(destination, temporary, no backup, flags 0)`. The first publish uses `MoveFileExW(temporary, destination, WRITE_THROUGH)` without replacement. No target is deleted or overwritten in place. A reader of an existing target must share DELETE; its old handle continues reading the complete old file object while a new open sees the complete new version. On this host, `MoveFileExW(REPLACE_EXISTING)` and Python `os.replace` rejected held destinations even with DELETE sharing, so they are not used for replacing existing managed receipts. Errors still enter the existing conservative lifecycle; there is no retry or sleep on Unity's thread.

Both state and acknowledgement contain:

```text
protocol, bridge_id, nonce, session_id, pid, process_creation_filetime,
verified_native_tid, managed_tid, assembly, updated_utc
```

`session_id` equals nonce. Native timestamps are integer Windows FILETIME values; consumers must preserve integer precision.

`state.json` additionally contains:

```text
lifecycle: bootstrapping|ready|action|restoring|recovery|stopping|stopped|recovery_unknown|failed
ready, main_thread_verified, component_destroyed, reconnect_allowed,
last_command_seq, frame, callbacks, distinct_frames, gate, main_instance_id,
reason, snapshot, action, campaign, business, assembly_unload
```

`snapshot` is null or an object containing `main_instance_id`, `scene`, `root_level_id`, `gate`, `safe`, `main_enabled`, `formation_set`, `other_visible_ui_types` and the fixed 14-field resource observation. This observation does not establish network or resource-consumption causality.

`action` contains its originating `command_seq`, `phase`, `outcome` (`NONE|PENDING|PASS|FAIL|UNKNOWN`), `mutation_attempted`, `restore_verified`, `restore_method_invoked`, `disabled_state_verified`, `resource_equality_status`, fixed UNVERIFIED visual/causality/network fields and, where available, the allowlisted resource comparison. Previous action history remains visible during later snapshots and heartbeats; consumers must never treat that previous PASS as a new action result.

`ack.json` adds `command_seq`, `command_name`, `status` (`ACCEPTED|COMPLETED|REJECTED|UNKNOWN`), `reason` and `result`. Result is always an object with `state`, `restore_verified`, `component_destroyed`, `snapshot` and `action` captured for that acknowledgement. Correlate exact nonce, PID creation, command sequence/name and originating action sequence. When consuming state together with an ack, reread/poll until `state.last_command_seq == ack.command_seq`; use the embedded `ack.result.action` for the action result. Stop's command sequence differs from the interrupted action sequence retained inside that action object.

## Lease, action and cleanup

The GUI sends a heartbeat every five idle seconds. Valid commands renew a 20-second lease. Lease expiration is checked before queued commands and follows the same owned-instance restoration path as explicit stop. No total-session or frame-count expiry exists.

The verification command uses the fixed, reviewed inherited MainUI Disable/Enable pair. MainUI uniqueness/current root, scene, player identity, formation, global input, initialization, known overlays/modals, unknown visible UI and the narrowly owned disabled-state allowance are all checked. A new verification transaction can start only after the prior transaction completed and restoration was observed; an unresolved campaign also blocks this transaction in the active helper. Normal disabled-state holding lasts six seconds, then restoration is checked over two subsequent Updates. The separately scoped campaign commands and business receipts are described above.

Stop during a transaction restores the exact original still-valid instance before cleanup. Unsafe restoration context never triggers an invocation on a replacement instance. Bounded recovery keeps the original ownership, retries only under the same guards, and ends within ten seconds as UNKNOWN if restoration cannot be confirmed. UNKNOWN is durable, retains the cross-build OS guard, and requires normal game process exit before reconnect. A clean stop writes final stopped/component-destroyed receipts after the actual own `OnDestroy` callback resets the registry and releases the guard. A known pre-invoke failure with `mutation_attempted=false` can cleanly stop without claiming a mutation or restoration occurred.

Native `hook_removed` and `callback_finished` are distinct. Timeout cancels only work that has not entered the one-shot gate; running work is retained and observed without retries/unload. A proven target process exit ends the host wait as `native_target_exited_cleanup_boundary`/exit 7. Native DLLs, managed assemblies and seal handles remain until normal target process exit; component cleanup is not full assembly unloading.

## Rebuild and targeted checks

```powershell
# Build against installed pinned legacy references; uses existing project-local Zig.
.\native\unity_bridge\build.ps1 -RunTests
.\native\unity_bridge\verify-assets.ps1
.\native\unity_bridge\verify-campaign-metadata.ps1
python -B .\native\unity_bridge\verify_assets.py

# Own unarmed packaged DLL dependency smoke, separate 32-bit host; never arms a hook.
C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe -NoProfile -File .\native\unity_bridge\tests\load-smoke.ps1
```

Build-time tool paths may point to the existing research tool cache; runtime code imports no research module. Exact metadata checks require only mscorlib 2.0.5.0 and UnityEngine 0.0.0.0 references, metadata v2.0.50727, x86 and Requires32Bit. Independent managed tests compile the actual production Probe/Lifecycle/ReceiptStore against owned fake Unity/model types and a test-only fixed session root under this folder. A separate real-Windows test compiles the actual ReceiptStore and holds target read handles through 200 state/ack replacements, checking old/new complete versions and conservative rejection without DELETE sharing. They execute no installed game assembly or game process. These tests validate lifecycle/protocol and file-publication behavior, not Unity/game execution or visible restoration. See [VALIDATION.md](VALIDATION.md).
