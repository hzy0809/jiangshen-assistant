"""Collect only the normal client's free sign-in and salary button routes."""
from __future__ import annotations

from dataclasses import dataclass
import time
import uuid


@dataclass(frozen=True)
class DailySignSalaryResult:
    status: str
    reason: str
    mutation_attempted: bool = False
    reward_confirmed: bool = False
    stock_callback_returned: bool = False
    cleanup: str = "NOT_NEEDED"
    operation_id: str = ""
    route: str = ""
    checkin_state_observed: bool = False
    checkin_today: int = -1
    game_day: int = 0
    role_id: str = ""
    refresh_cycle: int = 0
    initial_checkin_today: int | None = None
    initial_checkin_times: int | None = None
    final_checkin_times: int | None = None
    native_request_count: int | None = None
    native_opcode: int | None = None


class DailySignSalaryExecutor:
    routes = {301: "signin", 302: "salary", 304: "salary_only"}
    actions = {
        "signin": frozenset(("open", "resume", "claim_signin", "close")),
        "salary": frozenset(("open", "resume", "claim_salary", "claim_arena", "claim_all_salary", "close")),
        "salary_only": frozenset(("open", "resume", "claim_salary", "close")),
    }

    def __init__(self, objects, token, *, stopped=lambda: False, paused=lambda: False,
                 wait=None, timeout=90.0, clock=time.monotonic):
        self.objects, self.token = objects, token
        self.stopped, self.paused = stopped, paused
        self.wait = wait or (lambda: time.sleep(.1))
        self.timeout, self.clock = timeout, clock

    def execute(self, task_id, *, character_id, expected_cycle=None, expected_game_day=None, **_unused):
        if task_id not in self.routes:
            raise ValueError("daily_sign_salary_task_unsupported")
        route = self.routes[task_id]
        operation = uuid.uuid4().hex
        submitted = attempted = confirmed = returned = closed = False
        checkin_observed = sign_seen = sign_claim_submitted = False
        today, game_day, observed_cycle, observed_role = -1, 0, 0, ""
        initial_today = initial_times = final_times = None
        native_request_count = native_opcode = None
        deadline = self.clock() + self.timeout
        snapshot = {}

        def result(status, reason):
            return DailySignSalaryResult(
                status, reason, attempted, confirmed, returned,
                "VERIFIED_CLOSED" if submitted and closed else
                "UNVERIFIED" if submitted or route == "signin" and snapshot.get("cleanup_pending") is True else "NOT_NEEDED",
                operation if submitted else "", route,
                checkin_observed, today, game_day, observed_role, observed_cycle,
                initial_today, initial_times, final_times, native_request_count, native_opcode,
            )

        def command_failure(status, reason):
            if route == "signin" and checkin_observed and today == 1:
                return result("COMPLETED", "sign_salary_checkin_state_confirmed_cleanup_unverified")
            return result(status, reason)

        while self.clock() < deadline:
            if self.stopped():
                return result("STOPPED", "user_stop")
            if self.paused():
                before = self.clock()
                self.wait()
                deadline += self.clock() - before
                continue
            snapshot = self.objects.observe_daily_script(self.token, task_id)
            checkin_observed = False
            if (not isinstance(snapshot, dict) or snapshot.get("task_id") != task_id
                    or snapshot.get("route") != route):
                snapshot = {}
                return result("UNKNOWN" if submitted or route == "signin" else "UNAVAILABLE", "sign_salary_route_unavailable")
            if snapshot.get("role_id") != character_id:
                return result("UNKNOWN" if submitted or route == "signin" else "BLOCKED", "sign_salary_character_changed")
            cycle = snapshot.get("refresh_cycle")
            if type(cycle) is not int or cycle <= 0:
                return result("UNKNOWN" if submitted or route == "signin" else "UNAVAILABLE", "sign_salary_game_day_unavailable")
            if expected_cycle is None:
                expected_cycle = cycle
            if cycle != expected_cycle:
                return result("UNKNOWN" if submitted or route == "signin" else "BLOCKED", "sign_salary_game_day_changed")
            if route == "signin":
                observed_day, observed_today = snapshot.get("game_day"), snapshot.get("checkin_today")
                if (snapshot.get("checkin_state_observed") is not True or type(observed_day) is not int or observed_day <= 0
                        or type(observed_today) is not int or observed_today not in (0, 1)):
                    return result("UNKNOWN", snapshot.get("reason") or "sign_salary_checkin_state_unavailable")
                if expected_game_day is None:
                    expected_game_day = observed_day
                if observed_day != expected_game_day:
                    return result("UNKNOWN", "sign_salary_game_day_changed")
                if sign_seen and observed_today != 1:
                    return result("UNKNOWN", "sign_salary_checkin_state_conflicting")
                checkin_observed = True
                today, game_day, observed_cycle, observed_role = observed_today, observed_day, cycle, character_id
                times = snapshot.get("checkin_times")
                if type(times) is int and times >= 0:
                    final_times = times
                if initial_today is None:
                    initial_today, initial_times = today, final_times
                sign_seen |= today == 1
                # Fresh current state takes precedence over an old helper's
                # operation id, busy flag, or local UNKNOWN outcome.
                if today == 1 and not submitted:
                    return result("ALREADY_CLAIMED", "sign_salary_current_day_already_signed")
            same_operation = snapshot.get("operation_id") == operation
            if submitted and not same_operation:
                # An ACCEPTED open may be waiting for the GUI callback. A
                # retained previous operation id is not a rejection or licence
                # to resend. Wait for this operation's exact native identity.
                if route == "signin" and today == 1:
                    return result("COMPLETED", "sign_salary_checkin_state_confirmed_cleanup_unverified")
                self.wait()
                continue
            if same_operation:
                attempted |= snapshot.get("mutation_attempted") is True
                count, opcode = snapshot.get("request_count"), snapshot.get("opcode")
                if type(count) is int and count >= 0:
                    native_request_count = count
                if type(opcode) is int and 0 <= opcode <= 65535:
                    native_opcode = opcode
                confirmed |= snapshot.get("reward_confirmed") is True
                returned |= snapshot.get("stock_callback_returned") is True
                closed = (snapshot.get("closed") is True and snapshot.get("busy") is False
                          and snapshot.get("cleanup_pending") is False)
            status = snapshot.get("status")
            reason = snapshot.get("reason") or "sign_salary_unavailable"
            action = snapshot.get("next_action", "")
            if same_operation and status == "COMPLETED":
                if route == "signin":
                    if today != 1:
                        return result("UNKNOWN", "sign_salary_checkin_state_unconfirmed")
                    if closed:
                        return result("COMPLETED", "sign_salary_checkin_state_confirmed")
                if closed and attempted and returned and confirmed:
                    return result("COMPLETED", reason)
                if route != "signin":
                    return result("UNKNOWN", "sign_salary_completion_evidence_missing")
            if same_operation and status in ("ALREADY_CLAIMED", "NO_FREE_REWARD", "CONDITION_NOT_MET"):
                if closed:
                    # Once the owned panel closes, the native projection can
                    # report today's final state as ALREADY_CLAIMED. Preserve
                    # the fact that this operation actually attempted sign-in
                    # after observing an unsigned state for the same role/day.
                    if (route == "signin" and status == "ALREADY_CLAIMED" and attempted
                            and initial_today == 0 and today == 1):
                        return result("COMPLETED", "sign_salary_checkin_state_confirmed")
                    return result(status, reason)
            if status in ("UNKNOWN", "STOPPED") and (same_operation or snapshot.get("busy")):
                if (route == "signin" and today == 1 and status != "STOPPED"
                        and reason != "sign_salary_premium_spend_observed"):
                    return result("COMPLETED", "sign_salary_checkin_state_confirmed_cleanup_unverified")
                return result(status, reason)
            if not submitted:
                if snapshot.get("busy"):
                    return result("BLOCKED", "sign_salary_existing_operation_unresolved")
                # A previous local outcome is not the current eligibility.
                # The native model derives availability from current game data.
                availability = snapshot.get("availability")
                if availability in ("ALREADY_CLAIMED", "NO_FREE_REWARD", "CONDITION_NOT_MET"):
                    if route == "signin":
                        return result("UNKNOWN", "sign_salary_checkin_availability_conflicting")
                    return result(availability, "sign_salary_" + availability.lower())
                if availability == "UNKNOWN" and not action:
                    return result("UNAVAILABLE", reason)
                if status == "BLOCKED" and not action:
                    return result("UNAVAILABLE", reason)
            if action:
                if action not in self.actions[route] or (not submitted and action not in ("open", "resume")):
                    return command_failure("UNKNOWN" if submitted else "BLOCKED", "sign_salary_action_out_of_scope")
                if type(snapshot.get("revision")) is not int:
                    return command_failure("UNKNOWN" if submitted else "UNAVAILABLE", "sign_salary_revision_unavailable")
                if self.stopped():
                    return result("STOPPED", "user_stop")
                if self.paused():
                    continue
                if route == "signin" and action == "claim_signin":
                    if sign_claim_submitted or today != 0:
                        return result("UNKNOWN", "sign_salary_checkin_already_attempted_or_changed")
                    # Even a missing/ambiguous command receipt cannot permit a
                    # second stock sign-in invocation within this execution.
                    sign_claim_submitted = True
                wire = self.objects.submit_daily_script(
                    self.token, action, task_id=task_id, character_id=character_id,
                    operation_id=operation, expected_revision=snapshot["revision"],
                )
                ack = wire.get("ack") if isinstance(wire, dict) else None
                if not isinstance(ack, dict):
                    submitted = True
                    return command_failure("UNKNOWN", "sign_salary_command_receipt_missing")
                if ack.get("status") == "REJECTED":
                    # Re-observe on a later run. A rejection is insufficient
                    # evidence to resend a potentially invoked reward action.
                    return command_failure("UNKNOWN" if submitted or action.startswith("claim_") else "BLOCKED",
                                           ack.get("reason") or "sign_salary_command_rejected")
                if ack.get("status") not in ("ACCEPTED", "COMPLETED"):
                    submitted = True
                    return command_failure("UNKNOWN", ack.get("reason") or "sign_salary_command_unknown")
                submitted = True
            self.wait()
        if route == "signin" and checkin_observed and today == 1:
            return result("COMPLETED", "sign_salary_checkin_state_confirmed_cleanup_unverified")
        return result("UNKNOWN" if submitted else "UNAVAILABLE", "sign_salary_timeout")


@dataclass(frozen=True)
class SignPageRewardReceipt:
    opcode: int
    request_time: int
    receipt_result: int | None = None
    receipt_time: int | None = None
    receipt_gold_consume: int | None = None
    stock_callback_returned: bool = False
    reward_confirmed: bool = False


@dataclass(frozen=True)
class SignPageRewardItem:
    key: str
    name: str
    status: str
    reason: str
    availability: str = "UNKNOWN"
    initial_availability: str = "UNKNOWN"
    request_count: int = 0
    stock_callback_returned: bool = False
    reward_confirmed: bool = False
    receipts: tuple[SignPageRewardReceipt, ...] = ()


@dataclass(frozen=True)
class DailySignPageRewardsResult:
    status: str
    reason: str
    cleanup: str = "NOT_NEEDED"
    items: tuple[SignPageRewardItem, ...] = ()
    mutation_attempted: bool = False
    reward_confirmed: bool = False
    stock_callback_returned: bool = False
    operation_id: str = ""
    role_id: str = ""
    game_day: int = 0
    refresh_cycle: int = 0


class DailySignPageRewardsExecutor:
    """Claim only the three free buttons on the normal stock sign-in page.

    Sign-in itself is task 301. Each request is submitted once, then matched
    to its native request count and callback before another claim is allowed.
    """

    names = {"salary": "俸禄", "arena_reward": "竞技奖励", "point_reward": "积分赛奖励"}
    actions = frozenset(("open", "resume", "claim_salary", "claim_arena", "claim_point", "close"))
    availability_states = frozenset(("AVAILABLE", "ALREADY_CLAIMED", "NO_FREE_REWARD", "CONDITION_NOT_MET"))
    item_states = availability_states | frozenset(("COMPLETED", "PENDING", "UNKNOWN", "UNAVAILABLE"))

    def __init__(self, objects, token, *, stopped=lambda: False, paused=lambda: False,
                 wait=None, timeout=180.0, clock=time.monotonic):
        self.objects, self.token = objects, token
        self.stopped, self.paused = stopped, paused
        self.wait = wait or (lambda: time.sleep(.1))
        self.timeout, self.clock = timeout, clock

    @staticmethod
    def _number(value):
        return value if type(value) is int and value >= 0 else None

    def _items(self, snapshot, *, own_operation):
        rows = snapshot.get("items")
        if not isinstance(rows, list) or len(rows) != len(self.names):
            return None
        indexed = {row.get("key"): row for row in rows if isinstance(row, dict)}
        if set(indexed) != set(self.names):
            return None
        items = []
        for key, name in self.names.items():
            row = indexed[key]
            state, availability = row.get("status"), row.get("availability")
            if (state not in self.item_states or availability not in self.availability_states | {"UNKNOWN"}
                    or not isinstance(row.get("reason"), str)):
                return None
            receipts = []
            if own_operation:
                evidence = row.get("receipts")
                count = self._number(row.get("request_count"))
                if not isinstance(evidence, list) or count is None or count != len(evidence) or count > 16:
                    return None
                for receipt in evidence:
                    if (not isinstance(receipt, dict) or type(receipt.get("opcode")) is not int
                            or receipt["opcode"] != {"salary": 819, "arena_reward": 1122, "point_reward": 1126}[key]
                            or self._number(receipt.get("request_time")) is None
                            or any(receipt.get(field) is not None and self._number(receipt[field]) is None
                                   for field in ("receipt_result", "receipt_time", "receipt_gold_consume"))
                            or any(type(receipt.get(field)) is not bool
                                   for field in ("stock_callback_returned", "reward_confirmed"))):
                        return None
                    receipts.append(SignPageRewardReceipt(
                        receipt["opcode"], receipt["request_time"], receipt.get("receipt_result"),
                        receipt.get("receipt_time"), receipt.get("receipt_gold_consume"),
                        receipt["stock_callback_returned"], receipt["reward_confirmed"]))
                initial = row.get("initial_availability")
                if initial not in self.availability_states | {"UNKNOWN"}:
                    return None
            else:
                # An earlier operation's local result is not a receipt for this
                # execution. Keep only the currently observed button state.
                count, initial, state = 0, availability, "UNAVAILABLE" if state == "UNAVAILABLE" else availability
            items.append(SignPageRewardItem(key, name, state, row["reason"], availability, initial, count,
                bool(receipts) and all(receipt.stock_callback_returned for receipt in receipts),
                bool(receipts) and all(receipt.reward_confirmed for receipt in receipts), tuple(receipts)))
        return tuple(items)

    def execute(self, task_id, *, character_id, expected_cycle=None, expected_game_day=None):
        if task_id != 305:
            raise ValueError("daily_sign_page_rewards_task_unsupported")
        operation = uuid.uuid4().hex
        submitted = attempted = closed = page_open_observed = False
        role, day, cycle = "", 0, 0
        items = tuple(SignPageRewardItem(key, name, "UNKNOWN", "sign_page_rewards_not_observed")
                      for key, name in self.names.items())
        pending_claim_count = None
        close_submitted = False
        deadline = self.clock() + self.timeout

        def result(status, reason):
            receipts = tuple(receipt for item in items for receipt in item.receipts)
            return DailySignPageRewardsResult(
                status, reason, "VERIFIED_CLOSED" if closed else "UNVERIFIED" if submitted or page_open_observed else "NOT_NEEDED", items,
                attempted or bool(receipts), bool(receipts) and all(receipt.reward_confirmed for receipt in receipts),
                bool(receipts) and all(receipt.stock_callback_returned for receipt in receipts),
                operation if submitted else "", role, day, cycle)

        while self.clock() < deadline:
            if self.stopped():
                return result("STOPPED", "user_stop")
            if self.paused():
                before = self.clock()
                self.wait()
                deadline += self.clock() - before
                continue
            try:
                snapshot = self.objects.observe_daily_script(self.token, task_id)
            except Exception as exc:
                return result("UNKNOWN" if submitted else "UNAVAILABLE", f"sign_page_rewards_observation_failed ({exc})")
            if (not isinstance(snapshot, dict) or snapshot.get("task_id") != 305
                    or snapshot.get("route") != "signin_rewards"):
                return result("UNKNOWN" if submitted else "UNAVAILABLE", "sign_page_rewards_route_unavailable")
            page_open_observed = snapshot.get("page_open") is True
            if snapshot.get("role_id") != character_id:
                return result("UNKNOWN" if submitted else "BLOCKED", "sign_page_rewards_character_changed")
            observed_cycle, observed_day = snapshot.get("refresh_cycle"), snapshot.get("game_day")
            if (type(observed_cycle) is not int or observed_cycle <= 0
                    or type(observed_day) is not int or observed_day <= 0
                    or snapshot.get("checkin_state_observed") is not True):
                return result("UNKNOWN" if submitted else "UNAVAILABLE", "sign_page_rewards_game_day_unavailable")
            if expected_cycle is None:
                expected_cycle = observed_cycle
            if expected_game_day is None:
                expected_game_day = observed_day
            if observed_cycle != expected_cycle or observed_day != expected_game_day:
                return result("UNKNOWN" if submitted else "BLOCKED", "sign_page_rewards_game_day_changed")
            role, day, cycle = character_id, observed_day, observed_cycle
            own = snapshot.get("operation_id") == operation
            if submitted and not own:
                if snapshot.get("status") in ("UNKNOWN", "STOPPED"):
                    return result(snapshot["status"], snapshot.get("reason") or "sign_page_rewards_operation_unbound")
                self.wait()
                continue
            parsed = self._items(snapshot, own_operation=own)
            if parsed is None:
                return result("UNKNOWN" if submitted else "UNAVAILABLE", "sign_page_rewards_item_evidence_unavailable")
            items = parsed
            if own:
                attempted |= snapshot.get("mutation_attempted") is True
                closed = (snapshot.get("closed") is True and snapshot.get("busy") is False
                          and snapshot.get("cleanup_pending") is False and snapshot.get("at_home") is True)
            status = snapshot.get("status")
            reason = snapshot.get("reason") or "sign_page_rewards_unavailable"
            if status in ("UNKNOWN", "STOPPED"):
                return result(status, reason)
            if own and status == "UNAVAILABLE" and closed:
                if (reason != "sign_page_rewards_receiver_unavailable"
                        or not any(item.status == "UNAVAILABLE" and item.availability == "AVAILABLE" for item in items)
                        or any(item.status in ("PENDING", "UNKNOWN") or item.request_count and not item.reward_confirmed
                               for item in items)):
                    return result("UNKNOWN", "sign_page_rewards_completion_evidence_missing")
                return result("UNAVAILABLE", reason)
            if own and status in ("COMPLETED", "NO_FREE_REWARD"):
                if closed:
                    if any(item.availability == "AVAILABLE" or item.status in ("PENDING", "UNKNOWN")
                           or item.request_count and not item.reward_confirmed for item in items):
                        return result("UNKNOWN", "sign_page_rewards_completion_evidence_missing")
                    if status == "COMPLETED" and not any(item.reward_confirmed for item in items):
                        return result("UNKNOWN", "sign_page_rewards_completion_evidence_missing")
                    return result(status, reason)
            if not submitted:
                if snapshot.get("busy") is not False:
                    return result("BLOCKED", "sign_page_rewards_existing_operation_unresolved")
                if snapshot.get("at_home") is True and all(item.availability in
                        ("ALREADY_CLAIMED", "NO_FREE_REWARD", "CONDITION_NOT_MET") for item in items):
                    return result("NO_FREE_REWARD", "sign_page_rewards_no_claimable_button")
            action = snapshot.get("next_action", "")
            if pending_claim_count is not None:
                request_count = self._number(snapshot.get("request_count"))
                if request_count is None or request_count < pending_claim_count:
                    self.wait()
                    continue
                pending_claim_count = None
            if close_submitted:
                self.wait()
                continue
            if action:
                if action not in self.actions or (not submitted and action not in ("open", "resume")):
                    return result("UNKNOWN" if submitted else "BLOCKED", "sign_page_rewards_action_out_of_scope")
                if type(snapshot.get("revision")) is not int or snapshot["revision"] <= 0:
                    return result("UNKNOWN" if submitted else "UNAVAILABLE", "sign_page_rewards_revision_unavailable")
                if self.stopped():
                    return result("STOPPED", "user_stop")
                if self.paused():
                    continue
                if action.startswith("claim_"):
                    count = self._number(snapshot.get("request_count"))
                    if count is None or count >= 16:
                        return result("UNKNOWN", "sign_page_rewards_request_count_unavailable")
                    pending_claim_count = count + 1
                    attempted = True
                if action == "close":
                    close_submitted = True
                # Record submission before calling transport: an exception or
                # missing ACK does not authorize a second native invocation.
                submitted = True
                try:
                    wire = self.objects.submit_daily_script(
                        self.token, action, task_id=305, character_id=character_id,
                        operation_id=operation, expected_revision=snapshot["revision"])
                except Exception as exc:
                    return result("UNKNOWN", f"sign_page_rewards_command_unverified ({exc})")
                ack = wire.get("ack") if isinstance(wire, dict) else None
                if not isinstance(ack, dict) or ack.get("status") not in ("ACCEPTED", "COMPLETED"):
                    return result("UNKNOWN", (ack.get("reason") if isinstance(ack, dict) else None)
                                  or "sign_page_rewards_command_unverified")
            elif not submitted and status == "BLOCKED":
                return result("BLOCKED", reason)
            self.wait()
        return result("UNKNOWN" if submitted else "UNAVAILABLE", "sign_page_rewards_timeout")
