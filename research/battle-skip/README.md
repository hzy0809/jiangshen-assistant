# Earliest real battle results

Scope: assistant-owned battles in the pinned installed Windows client. The user authorizes hidden result methods, prohibits premium spending and invented outcomes, and asks to skip playback and obtain the actual result immediately. This worker performed source inspection and implementation only. Tests, review, compilation, restart and game/UI execution belong to the controller and were not performed here.

## Installed source evidence

The `.cs.txt` files here are raw offline ILSpy extractions from `C:/Users/yu/AppData/Local/JiangShen/pc/js_Data/Managed`, taken on 2026-11-07. They are evidence, not rebuildable game sources. `installed-identity.json` records freshly read SHA256 values, matching the existing production manifest pins:

| Assembly | SHA256 |
| --- | --- |
| Assembly-CSharp.dll | c1c8ebac7ddef4173c873a00774975a5ca366f95b19164c4ca775165eb994911 |
| Assembly-CSharp-firstpass.dll | 29d8fa2cb55e800ccdf1cdc6c5b274b924ea7d03c989296dfa569731e02a8d87 |
| pbdata.dll | 905cf19394545e2c4b5d49d994a7d981e13ae38a2a629f7d02329f27c6d2d5ab |

Extraction used the existing `research/unity-object-layer/tools/ilspycmd-9.1.0.7988/tools/net8.0/any/ilspycmd.dll`, `dotnet TOOL --disable-updatecheck -t TYPE ASSEMBLY`. Battle/UI types are from CSharp; `game.net.tcp_base` and `game.net.tcp_client` are from firstpass; `SCBattleEnd_0x408` is from pbdata. A `MuRenUI` type lookup in CSharp and firstpass found no definition and produced no evidence file; wood coverage instead uses the actual `DailyWoodModel` stock fast-complete route and `WoodWarUI`.

## Decisive findings

- `BattleUI.setReport` receives the complete server report before `Show`/`InitTroop`. `InitTroop` initializes armies and only then sets `bBattle_started=true`. The earliest supported result point is a bound real report plus that initialization and `ArmyBehaviourCreated`, with loading finished.
- `BattleReportInterpreter2.setReport` decompresses and deserializes the report. `getArmyNum` sums its `troop_base_num` and `troop_lose_num`. `SetArmyToFinalState` clears playback queues and applies each report legion with `SetToFinal`. It does not simulate or alter the outcome, server progression, rewards or losses.
- `BattleUI.drawUI`'s skip branch changes `bBattle_fighting`, applies `SetArmyToFinalState`, and follows the same embedded settlement or `cs_battle_end_0x408` path. `OpenResultWindow` invokes the configured result function once via `bResult_opened`.
- `showResultBtn` and `canSkipBattle` gate display based on one second, view mode, VIP, old-server status, all-played state, turn count and an optional delegate. They do not establish report readiness. The implementation bypasses these local presentation gates for owned supported modes; it does not modify VIP, battle result, report ID, rewards, losses or premium balances.
- A `0x408` request contains no battle ID/request nonce. The server reply has a required uint `result` and optional mode/result fields. `BattleUI.BattleEnd` applies the mode handler only for `result=0`. `result=1` displays `accel_warning`. It then clears end/wait flags even on rejection. Running that rejection branch would let a result screen appear without an applied settlement, so the early-request owner retains waiting instead.
- `game.net.tcp_base.Send` installs its temporary handler before sending. `Run` holds the client monitor, removes a positive-life handler before invoking it on a non-null response, and invokes it with null at timeout. A missing handler is not proof of rejection. The early requester wraps only the exact registration made by its own stock request under that same monitor.

The client establishes what it does with `result=1`; it does not expose the server's minimum elapsed time or a retry-after value. The exact server acceptance threshold and successful live rejection/retry behavior remain unverified.

## Implemented ownership and settlement

`BattleSettlement` in production `Probe.cs` owns one current report and at most one request at a time. It binds the BattleUI, real report object, interpreter, source parent, battle/previous roots, loader, character ID, client, socket/stream generation and main managed thread. Mainline/challenge additionally require their observer to have actually seen the current report at ID zero before the helper can consume an embedded result or request an end. Unsupported reports and cross-server/replayed reports are excluded; the one stock live `viewReport` exception is the explicitly owned revenge-demon route below.

Once initialized, it applies final presentation and immediately invokes the original `cs_battle_end_0x408` when that mode needs server settlement. It sets the presentation wait flag so the normal result UI cannot be used as a settlement witness before the response. Campaign 70's stock external completion call is retained once.

For a non-null, bounded, well-formed reply whose explicit required `result` is exactly 1, and only while all anchors and the exact registered callback still match, the helper records a nonsettlement rejection and retains waiting. It does not call the destructive stock rejection branch. It retries only that explicitly rejected request after 1, 2, 4, 8 and 16 seconds, at most six total requests. Before every retry it rechecks the consumed rejection handler identity and confirms no 1032 handler or queued response remains. All server sends still use the original client method. The six-request bound is an implementation budget, not evidence of the server's timing rule.

For a valid `result=0`, it forwards the unmodified stream to the original callback exactly once, then exposes the captured raw reply to the divinity model. Real wins and defeats follow the original mode handlers. Unknown results, null responses, timeouts, handler replacement, report/source/transport changes or callback failures never authorize a resend. Exhaustion or uncertainty stops automation with unresolved ownership. Stop/lease expiry disables requests and retries and restores only the exact still-owned callback; an already sent request remains an unresolved mutation until independently settled. A retained stopped wrapper forwards the original callback without performing further automatic work.

The campaign observer remains passive. It distinguishes only an explicitly marked rejection belonging to the central owner's current pending registration from authoritative settlement; all unowned invalid results remain UNKNOWN. Divinity uses the central raw success rather than adding a second wrapper. Training routes its prior independent sender through the same owner, preventing duplicate sends.

## Coverage matrix

| Assistant entry | Original stock path and gates | Earliest result path after this change | Server dependency |
| --- | --- | --- | --- |
| Mainline campaign | `CampaignUI.sc_campaign_battle_0x402`; wait/use-end depend on killed/finished/type/VIP branches; default/full/VIP-skip; optional `newBattleEnd` | Observer-anchored shared final-state path; consume real embedded packet when use-end is false; otherwise request 1032 immediately | Actual embedded result or successful 1032; no fabricated win; reportless defeats retain existing anchored-loss rules |
| Normal challenge | Same CampaignUI path, campaign type 2, normally full view | Same shared owner with challenge observer identity | Same real settlement requirement |
| Task 8 divinity | `DivinityUI.sc_god_battle_0x4B1`; wait=true/use-end=true; default at level >=200, full below | Shared immediate 1032; divinity consumes literal captured raw success | Type 5 reply, original `DivinityUI.BattleEnd`, stock task/progression evidence |
| Task 14 trial troops | `TrialLevelUI`; wait=false/use-end=true/default | Shared immediate 1032; retain wait until real response | Type 2 reply and original TrialLevelUI callback |
| Task 14 trial boss | `TrialUI`; wait=true/use-end=true/full | Shared immediate 1032, bypass full/turn playback gate | Type 3 reply and original TrialUI callback |
| Task 17 grave | `GraveLevelUI`; wait=false/use-end=true/default | Shared immediate 1032; retain wait until real response | Type 4 reply and original GraveLevelUI callback |
| Tasks 10/15 friend/capital monsters | `BuildingMonsterScript`; wait=true/use-end=true/VIP-skip | Shared immediate 1032 | Type 1 reply and original monster reward/defeat handling |
| Task 23 training monsters | `SealMonsterUI`; wait=true/use-end=true/full | Shared immediate 1032; old separate training sender delegates the same owner | Type 7 reply and original SealMonsterUI callback; stock free batch-kill remains separate |
| Task 24 revenge demon | `DemonScript.sc_demon_battle_0x492`; setReport without parent/end handshake; live battle uses viewReport; initial response already applies outcome, cooldown, soldiers/rewards | `DailyMonsterModel.OwnedBattleReportSource` binds the assistant's recorded start/source/target/root/new report; shared local final-state + one-shot result opener | Initial real demon response; no extra 1032 and no replay/report browsing authorization |
| Task 9 arena | `ArenaUI.sc_arena_challenge_0x460`; ID/report supplied; wait=false/use-end=false/default | Existing model-owned `CanShowResult` + `SetArmyToFinalState` + `OpenResultWindow`; already has no display/VIP/time gate | Initial real 0x460 receipt and own ArenaUI.BattleClose binding |
| Task 109 group arena | `PointraceUI` challenge response; ID/report supplied; wait=false/use-end=false/default | Existing model-owned earliest skip; generic helper stays out of its UI ownership | Initial real report/receipt and own PointraceUI.BattleClose binding |
| Task 22 wood trial | Production `DailyWoodModel` invokes stock free `cs_dummy_challenge_fast_complete` (616) | Existing direct fast-complete result; this task does not play report waves | Real stock fast-complete receipt/quota and task push |

The adapter forwarding method for task24 is controller-owned: `DailyScriptAdapter.OwnedBattleReportSource(Component)` delegates to the current monster model. Arena/PointRace repeated-opportunity logic belongs to another worker and is preserved.

## Deliberate limits

- Manual wood-wave playback is different from the assistant's task22 fast route: `WoodWarUI.NextFight` installs per-turn/death callbacks, custom `NextFight` result functions, and waits for a final end request. The generic helper does not act on that unowned null-parent wave sequence.
- General-book battles (`GeneralBookFormationUI`, full view, type 8 settlement), arbitrary saved/cross-server reports and other modes without an assistant-owned entry are source evidence only. Adding automatic operation there needs its own ownership and entry/progression contract.
- Asset loading, original server simulation/response time, actual defeat and server rejection cannot be removed by skipping presentation. The initial request is immediate after the earliest safe initialization; a server that continues rejecting after the bounded retries leaves UNKNOWN rather than a false completed task.
- Static evidence and implementation do not establish compilation, installed bridge compatibility, runtime callback ordering, a server acceptance threshold, real result speed, successful rejection recovery, visible cleanup, or business acceptance.

## Changed production interfaces

- `Probe.cs`: shared `BattleSettlement`, early presentation driver, stop/teardown restoration; events `battle_presentation_skipped`, `battle_settlement_requested`, `battle_settlement_rejected_waiting`, `battle_settlement_applied`, `battle_settlement_failure`.
- `CampaignModel.cs` / `CampaignObserver.cs`: observer-bound presentation ownership and `CampaignQueuedPacket.RetryableRejection` only for the exact owned pending request.
- `DailyDivinityBattleModel.cs`: consume shared raw applied response; avoid conflicting wrappers.
- `DailyTrialModel.cs`: route training sender and diagnostics through the shared owner.
- `DailyMonsterModel.cs`: retain task24 start/source/target/report anchors and expose `OwnedBattleReportSource(Component)`.
- `DailyScriptAdapter.cs`: controller-owned forwarding method; worker did not edit it.

No targeted executable checks were assigned to this worker. Current validation consists of freshly read installed hashes, raw installed-source extraction and the implemented source contracts. All behavioral claims above are implementation intent until the controller observes execution.
