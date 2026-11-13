# Immediate consecutive event claims

Status: source changes delivered; no tests, review, build, restart, or game operation performed by this leaf. Runtime behavior of these changes remains UNVERIFIED. The controller owns integration and authorized game execution.

## Decisive evidence and competing explanations

- Existing DailyEventRewardsModel.Capture re-reads the rows after each stock callback and owned-overlay cleanup. Candidate is not restricted to one claim. Level rewards and 七进七出 use advancing stage keys; each currently eligible login/day row has its own key. The source does not support a blanket explanation that every route intentionally claims only once per execute.
- Database.OnlineLottery.getcdtime uses the next online_lottery_template.time and getOnlineTime, whose anchor includes last_lottery_time. PremiumUI.recall commits m_col only after the reward closes. The controller supplied a live observation of 2/5 and a later approximately ten-minute cooldown; this leaf did not independently observe that game run. A later online claim being unavailable is consistent with a real cooldown, not proof of an omitted immediate claim. No cooldown wait or paid shortcut is introduced here.
- PremiumUI.recall shows completion message 21646 at the daily cap. Its exact _return callback calls MainUI.SetSubUI(null) and hides PremiumUI. Previously Offer then returned close before considering a candidate newly enabled by the final reward, such as a resource-dependent 七进七出 step. This is a concrete source path that can omit a now-eligible supported item, but no supplied runtime trace establishes that it happened in the reported run.
- TimeLimitMessageUI.cs_tempeventmessage_bonus_0x4E2 sends a message id, not a quest id. Its callback accepts an entire updated TempEventMessage and updates the corresponding live objects using Set. TempEventMessage contains a repeated quests field; each quest carries id/isfinished/isgot. A single response can therefore encode multiple claimed quests. The server implementation is unavailable: the client schema does not establish whether the server always batches every eligible quest, settles a subset, or varies by activity. The old model required canget()==false after one success and deduplicated the entire message key, which cannot handle a partial batch. This remains a source-supported boundary defect rather than an observed cause of the reported game run.
- Python formerly imposed a total 260-second execute deadline even when consecutive claims kept making confirmed progress. A sufficiently long immediately eligible sequence could therefore require another trigger. No supplied timing trace establishes that this limit was reached in the reported run.

## Changed behavior and invariants

- Each confirmed claim records one progress identity. Ordinary one-shot/stage rows retain their existing key. A TempEventMessage uses its sorted preclaim eligible quest ids, so a success settling one or many quests can leave a distinct next batch eligible.
- TempEvent confirmation requires raw result field 1 to be zero, raw nested message id to equal the requested message, an unchanged quest-id set, no regression of previously claimed quests, and at least one previously eligible quest changing to finished+got. A success receipt or prompt alone does not establish progress. Unknown identity, no progress, schema changes, or callback failure stop without retry.
- Closing the exact owned online completion message records that its stock _return closed the page. Only that known closure may offer another open inside the same operation when a current supported candidate remains. Role/cycle/client/socket/stream ownership, accumulated claims/receipts, and original premium-balance baseline remain bound across reopening.
- 七进七出 now requires the returned and observed stage to equal the requested stage plus one, matching the single-stage resource qualification rather than accepting a multi-stage jump.
- Python extends the settlement deadline only when confirmed claim_count increases. Polling, command rejection, and cooldown do not extend it. The native per-request watchdog remains bounded.
- A native COMPLETED receipt still requires cleanup_pending=false and reward_closed=true. If any final row remains AVAILABLE, Python returns UNKNOWN/event_rewards_claims_remaining_after_close rather than falsely declaring coverage complete. It does not auto-retry an unknown outcome.
- Existing ordinary-resource qualification and premium-currency exclusions remain in force. No new page, request opcode, purchase path, resource type, or player button gains permission.

## Integration contract

- Additive native daily_script.claim_count is a nonnegative integer reset for each new operation. It counts confirmed claim responses, which may each settle several quest rewards. It is not an inventory-item count.
- Python DailyEventRewardsResult.claim_count is appended as a defaulted field. Older native snapshots fall back to len(claimed_keys). Existing positional fields remain in order.
- claimed_keys remains a deduplicated list of stable row keys, so one message with two successful batches contributes one claimed key and claim_count=2. A workflow displaying actual claim-response count should use claim_count rather than len(claimed_keys).
- receipts[].key remains the stable row key for existing routes. TempEvent batch receipts use timed.message.ID.quests.QUEST_ID... so an additional successful batch does not overwrite its earlier receipt. No existing Python consumer interprets these receipt keys.
- EventRewardItem current/limit/remaining_seconds/resource_type/resource_cost/resource_available remain unchanged. Negative remaining_seconds now maps to None instead of an invalid wait. The controller can schedule a later pass from a COOLDOWN row without re-running the whole daily workflow.
- Valid cooldown continues to produce PARTIAL/event_rewards_online_cooldown_remaining after current supported claims finish. Unsupported visible routes also remain PARTIAL. An insufficient-resource row remains NOT_ELIGIBLE with its concrete reason and resource amounts; the controller must preserve that distinction rather than marking the reward itself claimed.
- The new terminal AVAILABLE outcome is UNKNOWN and stops execute_all. Shared workflow must retain its existing unknown/cleanup blocking behavior; it must not convert this result to a successful retry or silently mark all rewards claimed.

## Changed files and unverified work

Owned source: native/unity_bridge/managed/DailyEventRewardsModel.cs and jiangshen_assistant/daily_event_rewards.py. Evidence: this note, README.md, game.net.data.StoC.SCTempEventMessageBonus_0x4E2.cs.txt, game.net.data.StoC.TempEventMessage.cs.txt. The two new raw source files were extracted offline using the existing ILSpy tool from the installed pbdata.dll; no game process was touched.

Acceptance still requires the controller's authorized live run to bind the built artifact and operation, observe each exact claim response and owned cleanup, confirm the returned online counter/cooldown, and establish that no immediately eligible supported row remains. The simultaneous multi-quest and online-cap-unlocks-resource branches have not been exercised by this leaf. New visible unsupported pages remain explicitly unsupported until separately traced and authorized.
