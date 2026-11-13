# Initial response P1 repair

Status: DONE_WITH_CONCERNS. All four owned source/fixture files are frozen. No runtime package, game process, UI or network was accessed or deployed. Other adapter, settlement and shared-preflight work remains owned by its assigned workers.

The guard now validates detached bytes before any original initial-response callback: exact captured role/client/handler/arg/source root and target table; strict pinned protobuf schema; every usable target/owner echo; bounded raw-DEFLATE and real BattleReport decoding; outcome consistency. It forwards the exact original stream at position zero and original argument once only after these checks. A valid sent request may still apply its matching callback after stop, but cannot admit another launch. Null, malformed, mismatched or unknown identity stays sticky UNKNOWN without forwarding or retry.

Shared `BattleReplyPreflight.cs` is consumed, not authored by this leaf. Its frozen SHA256 is `9ee98d0944318272488df68ab27b66e3390def402de0d9104b3f3c1b45017e42`. Its API provides strict real-schema `Deserialize`, bounded/pinned report `Validate`, and canonical `Matches`; the guard verifies the actual fresh native report against the detached preflight proof after stock returns.

## Decisive evidence

- Pinned `pbdata.dll` and `protobuf-net.dll` generate the fixture responses. With original guard `64cf5d38...`, request A plus valid response B reached stock in Campaign/own-building/friend-building, and the B state/battle target changed. Wrong friend also reached stock. Final paired traces show zero stock calls and zero A/B effects, then UNKNOWN with no second Send.
- A valid outer response with damaged compressed report reached old stock and changed state before native report decoding returned null. The repaired guard blocks stock for damaged compression, inflated-size overflow, report required-field loss, duplicate report status and report/outcome mismatch.
- With pre-embedded guard `50b39006...`, valid outer A plus embedded settlement B was cached as `7:2`; a native end before common-layer handoff changed B. The repaired guard blocks the initial callback/cache entirely. Matching embedded A, omitted type with legal default 0, absent outer report ID, and matching explicit outer report ID pass.

Raw evidence: `decisive-before-after.json`, `negative-control-final/ipc-traces.jsonl`, `embedded-negative-control/ipc-traces.jsonl`, `final/ipc-traces.jsonl`. Campaign mutation counters model the raw target/report assignment; building counters model the raw dictionary-target updates. Native playback is explicitly simulated before handoff, not claimed observed in a game.

## Pinned schemas and binding

| Opcode | Relevant actual fields | Admission rule / limit |
|---|---|---|
| 0x402 Campaign/normal challenge | required idcampaign=1, idtroop=2, iswin=3, report=9; optional idbattle_report=8, result=13, battle_end=15 | Match both target echoes against the frozen request. Missing result uses the pinned native default 0. Enum TESTER=1 follows the exact native no-battle callback once and remains UNKNOWN/no retry. Unknown result is blocked. |
| 0x48D own building | required result=1, id=2; optional iswin=3, report=4 | Match id before dictionary lookup/native updates. Native result=1 branch may update this exact target once; it is not a successful battle result. Other native report branches require the valid matching compressed report. |
| 0x4C1 friend building | required result=1; optional idfriend=2, idmonster=3, iswin=4, report=5 | Require actual idmonster presence because stock indexes it; never default an omitted target to key 0. Compare idfriend when present. When absent, telemetry says `target-echo;friend-opcode-only` and uses the captured exact friend object/id/table and source. Native result=1/2 no-battle branches remain UNKNOWN/no retry after the matching native callback once. |
| 0x334 normal trial | required result=1, stamina=9; optional idtroop=2, iswin=3, report=4 | Compare optional echo when present; absence is explicitly `opcode-only` with exact request/client/handler/source/role ownership. The native callback ignores result, so no invented success/failure numeric mapping is applied. |
| 0x342 grave | required result=1, soldier=7, showboss=9, endgrave=10; optional idtroop=2, iswin=3, report=4 | Same optional-echo rule and opcode-only limit as trial. |
| 0x336 trial Boss | required result=1; optional idtroop=2, iswin=3, report=4 | The request itself contains no target ID. Absent echo is opcode-only. Present idtroop has no proven mapping to bossid and is conservatively UNKNOWN before stock. This response variant remains unsupported pending mapping evidence. |
| 0x368 training | required result=1, idtroop=2, iswin=3, report=4, exp=5, buff=6, cd=7 | Match the actual requested troop ID and report before stock. Native result is recorded without invented interpretation. |
| 0x370 no-report training batch | required exp=1, result=2, buff=3 | Preserve the existing native exp-driven path. Record result without assigning semantics. Exact native six finish flags/buff are still required; timeout or no stock success is UNKNOWN/no retry. |

SCBattleEnd embedded in 0x402 has required result=1 and optional type=2, idcampaign=4, idtroop=5, iswin=6, idbattle_report=17. Type defaults to 0 and may legitimately be omitted. A non-null embedded packet must have actual type 0, exact request target, authoritative result 0 and positive report ID, matching outcome; an outer positive report ID must agree if provided. Missing target fields remain native default 0 and fail to match, never copied from the outer message. Embedded result=1 is an acceleration warning, not an authoritative settlement, and is conservatively UNKNOWN before cache. An entirely absent embedded packet remains valid.

All schemas came from the pinned installed pbdata file, decompiled into this directory. It has SHA256 `905cf19394545e2c4b5d49d994a7d981e13ae38a2a629f7d02329f27c6d2d5ab`; Ionic.Zlib is `799f6b9630886132564f961031f2908263e55b104e712009eef0d4bfceb80a30`. Assembly-CSharp and firstpass identities remain the previously pinned `c1c8ebac...994911` / `29d8fa2c...a8d87`.

## Validation and frozen result

`powershell -NoProfile -ExecutionPolicy Bypass -File native/unity_bridge/tests/run-battle-launch-race.ps1 -OutputDirectory research/battle-lifecycle/launch-guard-validation/initial-response-p1/final`

PASS: **84/84**, 9.523 seconds, real separate x86 process and Windows named event/mutex, source hashes stable. Coverage includes original pause/stop/epoch/scenario/stamina races plus outer and embedded valid/mismatch cases, role/friend/source-root changes, exact stream forwarding, known native rejection branches, optional echoes, packet/report corruption and size bounds, duplicates, null timeout and stopped late responses.

Negative controls are expected failures: original guard with the 73-case pre-embedded suite produced 27 assertion failures and 8 missing-new-telemetry errors; pre-embedded guard with the 84-case suite produced the 8 new embedded assertion failures and 2 missing-new-telemetry errors. The raw counterexamples, not these aggregate counts, establish the P1 defects.

PASS: all current managed production sources compile against the installed pinned legacy mscorlib/UnityEngine with `/nostdlib+ /warn:4 /warnaserror+`, all existing daily defines, isolated output only. Exact input hashes, compile log and stable-source summary are in `managed-compile-inputs.json`, `managed-compile.log`, `managed-compile-summary.json`.

Final production `BattleLaunch.cs` SHA256: `55fcf9feaaa317c474e0debbd115c99359e0a50d1227821143f5e6db43fde87d`. Full owned/shared identities are in `final-hashes.json`.

## Remaining limits

Unity scenes, UI resources, loading/rendering/rewards/navigation and the complete real mode adapters are detached boundaries in this fixture. Native callbacks reproduce the named raw stock branches, not every client side effect. Actual pbdata/protobuf/Ionic parsing and Win32 IPC are real. There is no live mode acceptance or deployment claim.

Absent optional target/owner echoes cannot independently identify an online response target. These branches rely on the native client's single exact handler plus frozen local ownership and are labelled accordingly. The protocol has no general request nonce or character identity inside BattleReport; a same-target, otherwise indistinguishable server packet cannot gain nonexistent provenance from this guard. The unproven Boss echo mapping remains fail closed. Arena/PointRace/Demon preflight and settlement paths are separate worker scope, not validated as full adapters by this report.
