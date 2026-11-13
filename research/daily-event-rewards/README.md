# Daily event rewards: source implementation and integration contract

Status: IMPLEMENTED_SOURCE_ONLY. No tests, build, runtime reload, or game actions were run by this assignment.

## Routes and interface

- 201: 精彩活动 / PremiumUI
- 202: VIP福利 / VipWealUI
- 203: 限时活动 / TimeLimitMessageUI

`DailyEventRewardsModel` exposes `Capture(Model,int,bool)`, `Queue(int,ulong,string,int,int,string,out string)`, `ExecuteGui(DevelopmentPublication,DailyEventRewardsInvocationGate,out string)`, `Json()`, `Stop(string)`, `NotInvoked(string)`, `Busy`, `MutationAttempted`, `GuiInvocationAttempted`, `OutcomeUnknown`, and `Revision`.

The controller owns the shared files: instantiate this model for tasks 201-203 in DailyScriptAdapter, retain it while Busy, forward Capture/Queue/ExecuteGui/Json/Stop/NotInvoked exactly as for DailyMonsterModel, add the C# source to build.ps1, and allow these routes plus `open`, `claim`, `close_reward`, `close_message`, `close` through Protocol/objects/bridge validation. The model uses the existing flat daily_script snapshot convention.

Python entry point: `DailyEventRewardsExecutor(objects,token,...).execute(task_id, character_id=..., expected_cycle=...)`. `execute_all` runs the three routes and stops on UNKNOWN, STOPPED, or unverified cleanup. No standalone connection, schedule, or role selection is introduced. Integration with the existing daily workflow and app remains the controller's responsibility.

JSON fields: schema, revision, task_id, role_id, cycle, status, reason, gate, next_action, entry_gate, network_idle, operation_id, sequence, mutation_attempted, gui_invocation_attempted, gui_not_invoked_sequence, task_confirmed, cleanup_pending, reward_closed, target_key, receipt_base64, claimed_keys, receipts, rows, gold_cost. Each row supplies key/name/status/reason/current/limit/remaining_seconds/gold_cost. The scalar progress fields use -1 when inapplicable; Python exposes those as None. Each receipt includes the exact row key and raw callback body as base64. `task_confirmed` means at least one claim received its stock response and the exact claim marker changed; it does not assert full coverage of the page or physical inventory verification.

## Supported fixed claim branches

| Page | Target / stock action | Qualification and settlement |
| --- | --- | --- |
| VIP福利 | daily gift / VipWealUI.cs_vip_daily_gift_get, opcode 1820 | Current `vip_gift_today == false`; stock callback sets it true. VIP0 is allowed by stock UI. |
| VIP福利 | each eligible VIP level 1-10 / PremiumUI.cs_vip_get_reward_new(level), 939 | Template level <= current VIPLEVEL; at least one unclaimed template id; receipt callback adds that level's ids to viprewards. Whole-level requests containing special presentation types 0 or 44 are separately UNSUPPORTED. |
| 精彩活动 | next level award / PremiumUI.cs_level_award, 815 | Lowest level_award template above newbieinfo.award_level and <= current level; receipt advances award_level to the planned level. Repeats until no eligible next row. |
| 精彩活动 | each everyday login day / cs_everyday_login_get(dayid), 2063 | Current start/end time, accumulated login days, missing everydaylogins key; receipt adds that exact day. |
| 精彩活动 | each seven-day login day 1-7 / cs_seven_days_login_bonus_get_0x71A or new_0x721, 1818/1825 | Current isSevenDaysLoginOpen and stock canGetBonus(day); current old/new template decides route. Receipt must change hasGotLoginBonus(day). Type 0 special-character display rows are separately UNSUPPORTED. |
| 精彩活动 | 在线奖励 / cs_online_lottery_enter(false), 1861 then cs_online_lottery_draw(), 1823 | Current count below online_lottery_template count and stock getcdtime()==0. Refresh the stock prize list first. Both raw responses require Operate.OK. Draw response must bind the planned next count; wait for the normal animation and owned reward window, then invoke its stock recall callback. Only the resulting exact lottery_today increment confirms the claim. Cooldown is reported with remaining seconds without waiting for it to expire. |
| 精彩活动 | 新手卡 / CSNewbieGift_0x32, 50 | Only while the current character's `newbie_gift` is false. The fixed request is empty and uses the original `sc_newbie_gift` handler. It does not invoke the account-code method, write the code, or use the separate cash-gift route. Success requires the stock response and the character flag to change. |
| 精彩活动 | 七进七出 / cs_zhaoyun_quest_new, 948 | The current stage and original requirements determine eligibility. Only verified ordinary-resource costs are allowed; insufficient, unknown, and gold costs are not sent. See `page46.md` for receipt and special-window cleanup boundaries. |
| 限时活动 | each current TempEventMessage / cs_tempeventmessage_bonus_0x4E2(message.id), 1250 | Stock current event window and canget() (a quest isfinished && !isgot); a success receipt must name this exact message and at least one previously eligible quest must become isgot. The remaining eligible quest set is re-evaluated after each owned success prompt, permitting another batch in the same operation. Already fulfilled recharge/consumption conditions may qualify; this bridge never performs those conditions. |

Rewards may increase gold or bound gold; these are not costs and are not excluded. No native claim route here requests a purchase, recharge, exchange, share, role change, or spending action.

## Explicitly not covered

PremiumUI defines 54 actual page enum values. This implementation handles page 4 (Chongji), page 11 (Xinshouka), page 33 (SevenDaysLogin), page 36 (OnlineLottery), page 46 (ZhaoZiLongNewer), and page 48 (EverydayLogin). The actual `pageList` determines current visibility; a visible page without an implemented route remains `UNSUPPORTED`, while hidden pages are not treated as currently claimable. An unavailable page list is reported as unobserved rather than assumed complete. A visible unsupported route or online-reward cooldown with unused attempts makes the summary `PARTIAL`.

VIP page 2 is a shop: sc_vip_daily_shop_buy subtracts goldcost. It is reported as EXCLUDED with `event_rewards_purchase_excluded`. This does not prevent processing the other VIP pages and does not by itself make the summary PARTIAL.

TimeLimitMessage rows are announcements without a claim button, emitted as NO_REWARD. TempEventMessage rows distinguish EVENT_CLOSED, ALREADY_CLAIMED, NOT_ELIGIBLE, and AVAILABLE. No current messages is a legitimate empty result after opening the stock UI and its opcode 1249 refresh.

SpecialGetUI character or formation-slot presentation is not automatically dismissed. The known VIP and seven-day template branches producing it are skipped before sending and reported individually. Other server-driven reward variations remain a runtime limitation: an unexpected overlay or unconfirmed marker stops with UNKNOWN and no retry.

## Ownership, receipt, and cleanup evidence

Opening uses the original MainUI _premium/_vipweal/_timelimit branches; PremiumUI opening sends opcode 850 to refresh its consumed-gold counter, not a purchase. TimeLimit opening sends opcode 1249 and refreshes the current server message list. Only ready owned panels and an idle network offer claims. Every command binds role, daily cycle, revision, operation id, and sequence. A running operation retains exact character/client/socket/stream references; unexpected owner/cycle or premium balance decrease produces UNKNOWN.

Observed startup fix: session 41b6f3dbcfe34f5a88cbae1fa95a548a/state.json had Capital/main_enabled=true but snapshot.gate=subui_present and campaign.gate=campaign_visible_unknown_ui:PremiumUI. The former Home() requirement m_subui==null therefore suppressed VIP open despite an AVAILABLE daily gift. Entry now accepts only a ready existing PremiumUI/VipWealUI/TimeLimitMessageUI in addition to no subwindow. A ready same-target page is adopted without calling the stock toggle; another known event page is switched by the original branch. Existing reward/message windows and unknown subwindows remain protected. MainUI._premium and _timelimit are static; _vipweal is an instance method. EntryGate and network_idle are published separately for future diagnosis.

Each claim wraps only the stock handler just installed for its fixed opcode, matching its target component, method, and null callback argument. It forwards the same stream to the original method, retains raw bytes, and requires that specific target's stock claim marker to change. Unknown response, callback exception, timeout, or unconfirmed marker is never retried.

A result can be closed only when its generation is newer than the preclaim panel and OnClose targets the claim's stock component. A TempEvent success prompt must be a new stock message 334 with null callbacks and root. The final original SetSubUI(null) action must actually hide the owned event panel. `reward_closed` is set only at that final confirmed close.

OnlineLottery adds two explicit stock stages using the existing claim action, without a protocol change: read-only prize-list inspection (mutation_attempted remains false), then free drawing. The raw field-1 Operate value is decoded independently for those two callbacks. The draw's normal UpdateOT animation creates BattleResultUI with OnClose=PremiumUI.recall; recall applies m_col to the live counter. At the daily cap, its stock completion message 21646 invokes PremiumUI._return, so that exact owned message is closed before the final cleanup acknowledgement. An unexpected overlay, empty prize list, rejected receipt, or animation/settlement beyond 30 seconds stops UNKNOWN without a second draw.

New runs re-read current data and may reopen the same route; no persisted failure table decides eligibility. In-memory successful target progress only deduplicates the current operation. A multi-quest TempEventMessage uses the sorted preclaim eligible quest ids for progress identity; public claimed_keys remain stable message keys, while claim_count counts confirmed claim responses and receipts retain each batch. See `repeated-trigger.md` for the immediate-repeat boundaries and source-only changes.

## Source identity and raw artifacts

Installed directory: C:/Users/yu/AppData/Local/JiangShen/pc/js_Data/Managed

- Assembly-CSharp.dll SHA256 C1C8EBAC7DDEF4173C873A00774975A5CA366F95B19164C4CA775165EB994911
- Assembly-CSharp-firstpass.dll SHA256 29D8FA2CB55E800CCDF1CDC6C5B274B924EA7D03C989296DFA569731E02A8D87
- pbdata.dll SHA256 905CF19394545E2C4B5D49D994A7D981E13AE38A2A629F7D02329F27C6D2D5AB
- dbdata.dll SHA256 D32BB2B6C58B7CAFCD4BE5CBCDABBDB6A76C7003C0E2A160DEF80835BE412A2D

Hashes were read during this assignment. The `.cs.txt` files in this directory are offline ILSpy output from those installed assemblies. `PremiumUI.cs.txt` includes enum/init routes and the claim/callback bodies; `VipWealUI.cs.txt` includes the three pages and paid-shop callback; `TimeLimitMessageUI.cs.txt` and `Database.TempEventMessage.cs.txt` show the server-provided conditions and exact free claim method. `game.net.data.StoC.TempEventQuest.cs.txt` demonstrates the protobuf property model. Original MainUI navigation, Character fields, Global templates, BattleResultUI and MessageBox methods are also in the existing research/unity-object-layer and research/checklist-other sources.

Validation scope: offline source/contract tracing only. Compilation, full app wiring, package identity, stock execution, reward receipt observation in the live game, and physical UI acceptance remain UNVERIFIED by this leaf assignment, as explicitly requested.
