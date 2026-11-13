# Independent action receipt readback

Status: DONE. Bounded JSONL readback PASS for session `fa0435ba7b134503a973d4a9834c7a70`. Only the two named session JSONL files were read; no game access, rerun, build or input by this reviewer. Changed only this report; no interfaces changed.

## Verified result

- `probe.events.jsonl` contains 15 events and exactly one command: sequence 1, disable-restore. All events have the expected nonce. No failure/drift/unverified-restoration event occurs; timestamps, frame counts and callback counts are nondecreasing. Strict JSON parsing found no duplicate keys.
- Native two-event receipt: owner/callback TID 21756, mono_main_match=1, calls=1, state COMPLETE, callback_finished=1, hook_removed=1, result_code=0 and win32_error=0.
- Unity-generated Update witness frames 192360/192361 precede target discovery. All managed events use native TID 21756 and managed thread 1; all six full snapshots use MainUI instance 11752, scene 0/current Capital root, gate PASS/safe true.

All times below are UTC on 2026-10-30:

| Event / probe.events.jsonl line | Time | Frame | main_enabled |
|---|---|---:|---|
| Pre-action snapshot / 6 | 17:53:17.2929463 | 193966 | true |
| action_invoked / 7 | 17:53:17.2944486 | 193966 | false |
| action_verified / 8 | 17:53:17.3258341 | 193968 | false |
| restore_invoked / 10 | 17:53:23.3323953 | 194328 | true |
| restore_verified / 12 | 17:53:23.3636710 | 194330 | true |

The verified-disabled to restore-invoked interval is 6.0066 seconds; disabled invocation to restore invocation is 6.0379 seconds. Each endpoint is verified two frames after its invocation receipt. Transaction result line 13 has ui_status=PASS, disabled_state_verified=true, restored_enabled_verified=true, restore_method_invoked=true. This is consistent with the previously reviewed fixed MethodInfo invocation path and source-bound assembly identity JSUnityProbe_c3fec61cde08ec23; it is not an instruction-level execution trace.

All six persisted full resource snapshots have the exact same 14 allowlisted numeric fields/values. The final per-field comparison contains exactly those 14 fields, with before==after, final_equal=true and every_sample_equal=true for every field. The aggregator reports 368 captured observations, allowlist_resources_equal=true, allowlist_equal_at_every_sample=true and resource_equality_status=PASS. It explicitly leaves resource_consumption_causality and global_network_effects UNVERIFIED.

Cleanup line 14 records action_used=true, restored=true, ui_status=PASS and transaction_complete; final line 15 is component_destroyed with restored=true. Native module residency is explicitly retained_until_restart=1, and managed cleanup says process_restart_required for assembly unload. Hook/helper cleanup does not establish assembly/native-module unload or release of every retained handle.

## Evidence identity and limits

| Session file | SHA256 |
|---|---|
| native.jsonl | c8a272cff81d33e684e6755df0c21e527a14622d0a6d8737d6da75a47bbe2f0d |
| probe.events.jsonl | 2cfe790eb175d445a283fffce2580170a19c6b8254855111bfe7e663e2eaee9d |

Validation consisted of targeted Python read-only strict parsing, event/command/identity/order checks, independent duration calculation and numeric equality comparisons. No tests were rerun.

The 368 observations are an aggregate runtime receipt; all 368 raw values were not individually persisted, so this readback independently recomputes equality for the six logged snapshots and the final before/after pairs, not every internal sample. Sampling can miss short-lived changes, and fields outside the allowlist are not covered. These observations do not establish server/network causality or universal zero resource cost.

The JSON intentionally reports visual_gray_state=UNVERIFIED. The controller reports separate Computer Use before/during/after visual observations; images were outside this leaf's exact-JSONL-only scope and were not inspected here. The controller owns visual acceptance, source/release seals and final residency/handle/file audit. No additional action or retry is requested by this review.
