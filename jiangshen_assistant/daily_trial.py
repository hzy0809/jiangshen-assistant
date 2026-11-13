"""Run stock battle routes, including the complete trial through its boss."""
from __future__ import annotations

from dataclasses import dataclass
import time
import uuid


@dataclass(frozen=True)
class DailyBattleResult:
    status: str
    reason: str
    progress_before: int | None = None
    progress_after: int | None = None
    required: int | None = None
    task_confirmed: bool = False
    mutation_attempted: bool = False
    cleanup: str = "NOT_NEEDED"
    clearing_complete: bool = False
    checklist_complete: bool = False
    cooldown_remaining_seconds: int | None = None
    role_id: str | None = None
    refresh_cycle: int | None = None
    operation_id: str | None = None
    release_verified: bool = False
    cleanup_pending: bool = False


class DailyBattleExecutor:
    """Follow the native stock actions and finish against the daily task count."""

    clear_mode = False
    checklist_mode = False

    def __init__(self, objects, token, *, stopped=lambda: False, paused=lambda: False,
                 wait=None, timeout=240.0, clock=time.monotonic):
        self.objects = objects
        self.token = token
        self.stopped = stopped
        self.paused = paused
        self.wait = wait or (lambda: time.sleep(.2))
        self.timeout = timeout
        self.clock = clock

    def execute(self, task_id, *, character_id, expected_current=None, expected_required=None,
                expected_cycle=None, resume_only=False):
        if task_id != self.task_id:
            raise ValueError("daily_battle_task_unsupported")
        operation = uuid.uuid4().hex
        baseline = expected_current
        current, required = baseline, expected_required
        submitted = mutation = confirmed = False
        resume_adopted = False
        operation_adopted = normal_close_submitted = False
        cycle = expected_cycle
        snapshot = {}
        deadline = self.clock() + self.timeout
        previous = None

        def released():
            return (type(cycle) is int and cycle > 0
                    and snapshot.get("role_id") == character_id
                    and snapshot.get("refresh_cycle") == cycle
                    and snapshot.get("home_ready") is True
                    and snapshot.get("release_verified") is True
                    and snapshot.get("busy") is False
                    and snapshot.get("cleanup_pending") is False)

        def remaining_cooldown():
            cooldown, server_time = snapshot.get("boss_cooldown"), snapshot.get("server_time")
            return (cooldown - server_time
                    if type(cooldown) is int and type(server_time) is int
                    and 0 < server_time < cooldown <= 0xffffffff else None)

        def result(status, reason, *, cooldown_remaining_seconds=None):
            cleanup_pending = snapshot.get("cleanup_pending") is True
            cleanup = ("UNVERIFIED" if snapshot.get("cleanup_pending") else
                       "VERIFIED_CLOSED" if submitted else "NOT_NEEDED")
            if self.clear_mode:
                operation_released = released() and snapshot.get("operation_id") == operation
                cleanup_pending |= (snapshot.get("busy") is True
                                    or submitted and not operation_released
                                    or bool(snapshot.get("operation_id"))
                                    and (snapshot.get("home_ready") is False
                                         or snapshot.get("release_verified") is False))
                cleanup = ("UNVERIFIED" if cleanup_pending else
                           "VERIFIED_CLOSED" if submitted and operation_released else "NOT_NEEDED")
            return DailyBattleResult(
                status, reason, baseline, current, required, confirmed, mutation,
                cleanup,
                snapshot.get("clearing_complete") is True if self.clear_mode else False,
                snapshot.get("checklist_complete") is True if self.checklist_mode else False,
                cooldown_remaining_seconds=cooldown_remaining_seconds,
                role_id=snapshot.get("role_id"), refresh_cycle=snapshot.get("refresh_cycle"),
                operation_id=operation if submitted else "",
                release_verified=self.clear_mode and released(),
                cleanup_pending=cleanup_pending,
            )

        while True:
            if self.stopped():
                return result("STOPPED", "user_stop")
            if self.paused():
                pause_start = self.clock()
                self.wait()
                deadline += self.clock() - pause_start
                continue
            if self.clock() >= deadline:
                return result("UNKNOWN" if submitted else "UNAVAILABLE", "daily_battle_timeout")

            snapshot = self.objects.observe_daily_script(self.token, task_id)
            if not isinstance(snapshot, dict):
                raise RuntimeError("daily_battle_observation_invalid")
            if snapshot.get("role_id") not in ("0", character_id):
                return result("UNKNOWN" if submitted else "BLOCKED", "daily_battle_character_changed")
            if self.clear_mode:
                observed_cycle = snapshot.get("refresh_cycle")
                if (snapshot.get("task_id") != task_id
                        or not character_id or character_id == "0" or snapshot.get("role_id") != character_id
                        or type(observed_cycle) is not int or observed_cycle <= 0
                        or cycle is not None and (type(cycle) is not int or cycle <= 0)):
                    return result("UNKNOWN" if submitted else "BLOCKED", "trial_identity_cycle_unavailable")
                if cycle is None:
                    cycle = observed_cycle
                if observed_cycle != cycle:
                    return result("UNKNOWN" if submitted else "BLOCKED", "daily_cycle_changed")
                if snapshot.get("operation_id") == operation:
                    operation_adopted = True
                elif submitted:
                    if not operation_adopted and snapshot.get("busy") is True:
                        self.wait()
                        continue
                    return result("UNKNOWN", "trial_operation_changed")
            current = snapshot.get("activity_count")
            required = snapshot.get("required", required)
            if baseline is None:
                baseline = current
            if (not self.clear_mode and not self.checklist_mode and not submitted and expected_current is not None and current is not None
                    and current != expected_current):
                if required is not None and current >= required:
                    confirmed = True
                    return result("ALREADY_COMPLETE", "daily_task_already_complete")
                return result("BLOCKED", "daily_progress_changed")
            if ((submitted or resume_only or self.clear_mode or self.checklist_mode) and expected_cycle is not None
                    and snapshot.get("refresh_cycle") not in (None, 0, expected_cycle)):
                return result("UNKNOWN", "daily_cycle_changed")
            mutation |= bool(snapshot.get("mutation_attempted"))
            confirmed = (type(current) is int and type(required) is int
                         and required > 0 and current >= required)
            signature = (snapshot.get("status"), current, snapshot.get("next_action"),
                         snapshot.get("target_id"), snapshot.get("trial_level"),
                         snapshot.get("chapter_progress"), snapshot.get("trial_chests"),
                         snapshot.get("boss_killed"))
            if signature != previous:
                previous = signature
                deadline = self.clock() + self.timeout

            status = snapshot.get("status")
            action = snapshot.get("next_action", "")
            boss_pending = (snapshot.get("trial_level") == 6
                            and type(snapshot.get("trial_chests")) is int
                            and snapshot["trial_chests"] >= 5
                            and snapshot.get("boss_killed") is False
                            and snapshot.get("boss_rank_allowed") is True)
            if (self.clear_mode and not submitted and snapshot.get("operation_id") == ""
                    and boss_pending and released() and remaining_cooldown() is not None):
                # Read-only registration has no operation or close receipt.
                return result("WAITING", "trial_boss_cooldown",
                              cooldown_remaining_seconds=remaining_cooldown())
            if (self.clear_mode and status == "BLOCKED" and submitted
                    and snapshot.get("reason") in ("trial_boss_cooldown", "trial_boss_lost")):
                if not normal_close_submitted:
                    return result("UNKNOWN", "trial_cooldown_close_unverified")
                if not released():
                    self.wait()
                    continue
                remaining = remaining_cooldown()
                if (not boss_pending or remaining is None
                        or snapshot.get("reason") == "trial_boss_lost"
                        and (snapshot.get("boss_selected") is not True
                             or snapshot.get("battle_request_attempted") is not True)):
                    return result("UNKNOWN", "trial_cooldown_unverified")
                return result("WAITING", snapshot["reason"], cooldown_remaining_seconds=remaining)
            if (resume_only and action and not submitted and action != "resume"):
                return result("BLOCKED", "existing_battle_resume_unavailable")
            if (resume_only and action in ("open", "enter")
                    and not (task_id == 14 and resume_adopted)):
                return result("BLOCKED", "existing_battle_resume_unavailable")
            if status == "COMPLETED" and not snapshot.get("busy"):
                if self.clear_mode and not released():
                    self.wait()
                    continue
                if self.clear_mode and (snapshot.get("clearing_complete") is not True
                                        or snapshot.get("boss_killed") is not True
                                        or snapshot.get("trial_level") != 6
                                        or snapshot.get("trial_chests", 0) < 5
                                        or snapshot.get("cleanup_pending")):
                    return result("UNKNOWN", "trial_completion_unverified")
                if self.checklist_mode and (snapshot.get("checklist_complete") is not True
                                            or snapshot.get("cleanup_pending")):
                    return result("UNKNOWN", "daily_checklist_completion_unverified")
                # The normal daily reader is the final count used by the workflow.
                daily = self.objects.observe_daily(self.token)
                actual = next((task for category in daily.get("categories", ())
                               for task in category.get("tasks", ())
                               if task.get("task_id") == task_id), None)
                if actual is not None:
                    current, required = actual.get("current"), actual.get("required")
                    confirmed = (type(current) is int and type(required) is int
                                 and required > 0 and current >= required)
                if self.clear_mode:
                    return result("COMPLETED" if submitted else "ALREADY_COMPLETE", "trial_cleared")
                if self.checklist_mode:
                    return result("COMPLETED" if submitted else "ALREADY_COMPLETE",
                                  snapshot.get("reason") or "daily_checklist_complete")
                if confirmed:
                    return result("COMPLETED" if submitted else "ALREADY_COMPLETE", "daily_progress_complete")
                return result("UNAVAILABLE", "daily_progress_not_complete")
            if status == "UNKNOWN":
                return result("UNKNOWN", snapshot.get("reason") or "daily_battle_unknown")
            if status == "BLOCKED" and (not action or self.clear_mode and snapshot.get("operation_id") == operation):
                return result("BLOCKED" if submitted else "UNAVAILABLE",
                              snapshot.get("reason") or "daily_battle_unavailable")
            if not action:
                self.wait()
                continue
            if self.stopped():
                return result("STOPPED", "user_stop")
            if self.paused():
                continue
            wire = self.objects.submit_daily_script(
                self.token, action, task_id=task_id, character_id=character_id,
                operation_id=operation, expected_revision=snapshot["revision"],
            )
            ack = wire.get("ack") or {}
            if ack.get("status") == "UNKNOWN":
                submitted = True
                return result("UNKNOWN", ack.get("reason") or "daily_battle_action_unknown")
            if ack.get("status") == "REJECTED":
                # A changed frame can invalidate a queued action. Re-read the
                # currently offered stock button before attempting it again.
                self.wait()
                continue
            if ack.get("status") not in ("COMPLETED", "ACCEPTED"):
                raise RuntimeError(ack.get("reason") or "daily_battle_command_failed")
            submitted = True
            normal_close_submitted |= action == "close"
            if action == "resume":
                # Trial continues to the next chapter after three opponents.
                # Only the first adoption must avoid the entry request.
                resume_adopted = True


class DailyTrialExecutor(DailyBattleExecutor):
    """Clear five chapters and the boss; daily activity credit is independent."""

    task_id = 14
    clear_mode = True
