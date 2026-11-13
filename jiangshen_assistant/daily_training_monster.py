"""Clear all training monsters and use every remaining free summon."""
import uuid

from .daily_trial import DailyBattleExecutor, DailyBattleResult


def _verified_home(snapshot, *, character_id, cycle, operation_id):
    return (type(cycle) is int and cycle > 0
            and snapshot.get("role_id") == character_id
            and snapshot.get("refresh_cycle") == cycle
            and snapshot.get("operation_id") == operation_id
            and snapshot.get("home_ready") is True
            and snapshot.get("busy") is False
            and snapshot.get("cleanup_pending") is False)


class DailyTrainingMonsterExecutor(DailyBattleExecutor):
    task_id = 23

    def recover_to_home(self, *, character_id, timeout=15.0, expected_cycle=None):
        """Close an idle training page without admitting any training action."""
        operation = uuid.uuid4().hex
        deadline = self.clock() + timeout
        submitted = False
        operation_adopted = False
        cycle = expected_cycle
        snapshot = {}

        def result(status, reason):
            closed = _verified_home(snapshot, character_id=character_id,
                                    cycle=cycle, operation_id=operation)
            return DailyBattleResult(
                status, reason, mutation_attempted=False,
                cleanup="VERIFIED_CLOSED" if submitted and closed else
                "UNVERIFIED" if submitted or snapshot.get("training_scene") is True
                or snapshot.get("busy") is True or snapshot.get("cleanup_pending") is True
                else "NOT_NEEDED",
                role_id=snapshot.get("role_id"), refresh_cycle=snapshot.get("refresh_cycle"),
                operation_id=snapshot.get("operation_id"),
            )

        while self.clock() < deadline:
            if self.stopped():
                return result("STOPPED", "user_stop")
            if self.paused():
                pause_start = self.clock()
                self.wait()
                deadline += self.clock() - pause_start
                continue
            snapshot = self.objects.observe_daily_script(self.token, self.task_id)
            if not isinstance(snapshot, dict):
                snapshot = {}
                return result("UNKNOWN" if submitted else "BLOCKED", "training_recovery_observation_invalid")
            if not character_id or character_id == "0" or snapshot.get("role_id") != character_id:
                return result("UNKNOWN" if submitted else "BLOCKED", "training_recovery_character_unavailable")
            observed_cycle = snapshot.get("refresh_cycle")
            if (type(observed_cycle) is not int or observed_cycle <= 0
                    or cycle is not None and (type(cycle) is not int or cycle <= 0)):
                return result("UNKNOWN" if submitted else "BLOCKED", "training_recovery_cycle_unavailable")
            if cycle is None:
                cycle = observed_cycle
            if observed_cycle != cycle:
                return result("UNKNOWN" if submitted else "BLOCKED", "training_recovery_cycle_changed")
            same_operation = snapshot.get("operation_id") == operation
            if submitted:
                if not same_operation:
                    if not operation_adopted and snapshot.get("busy") is True:
                        self.wait()
                        continue
                    return result("UNKNOWN", "training_recovery_operation_changed")
                operation_adopted = True
                if (snapshot.get("status") == "CLOSED" and snapshot.get("recovery_complete") is True
                        and _verified_home(snapshot, character_id=character_id,
                                           cycle=cycle, operation_id=operation)):
                    return result("COMPLETED", "training_recovered_to_main")
                if snapshot.get("status") in ("UNKNOWN", "BLOCKED"):
                    return result("UNKNOWN", snapshot.get("reason") or "training_recovery_unknown")
                self.wait()
                continue
            if snapshot.get("training_scene") is not True:
                return result("NOT_NEEDED", "training_recovery_not_needed")
            if snapshot.get("next_action") != "resume_close":
                return result("BLOCKED", snapshot.get("reason") or snapshot.get("recovery_reason")
                              or "training_recovery_unavailable")
            wire = self.objects.submit_daily_script(
                self.token, "resume_close", task_id=self.task_id, character_id=character_id,
                operation_id=operation, expected_revision=snapshot["revision"],
            )
            ack = wire.get("ack") or {}
            if (ack.get("status") == "REJECTED" and ack.get("reason") in
                    ("daily_battle_command_unavailable", "daily_battle_gui_admission_changed", "client_busy")):
                self.wait()
                continue
            submitted = True
            if ack.get("status") not in ("COMPLETED", "ACCEPTED"):
                return result("UNKNOWN", ack.get("reason") or "training_recovery_command_unknown")
            self.wait()
        return result("UNKNOWN" if submitted else "BLOCKED", "training_recovery_timeout")

    def execute(self, task_id, *, character_id, expected_current=None, expected_required=None,
                expected_cycle=None):
        if task_id != self.task_id:
            raise ValueError("daily_training_task_unsupported")
        operation = uuid.uuid4().hex
        baseline = expected_current
        current, required = baseline, expected_required
        submitted = mutation = confirmed = False
        operation_adopted = normal_close_submitted = False
        cycle = expected_cycle
        snapshot = {}
        deadline = self.clock() + self.timeout
        previous = None

        def result(status, reason, *, cooldown_remaining_seconds=None):
            closed = _verified_home(snapshot, character_id=character_id,
                                    cycle=cycle, operation_id=operation)
            return DailyBattleResult(
                status, reason, baseline, current, required, confirmed, mutation,
                "VERIFIED_CLOSED" if submitted and closed else
                "UNVERIFIED" if submitted or snapshot.get("training_scene") is True
                or snapshot.get("busy") is True or snapshot.get("cleanup_pending") is True
                else "NOT_NEEDED",
                cooldown_remaining_seconds=cooldown_remaining_seconds,
                role_id=snapshot.get("role_id"), refresh_cycle=snapshot.get("refresh_cycle"),
                operation_id=snapshot.get("operation_id"),
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
                return result("UNKNOWN" if submitted else "UNAVAILABLE", "training_timeout")
            snapshot = self.objects.observe_daily_script(self.token, task_id)
            if not isinstance(snapshot, dict):
                raise RuntimeError("training_observation_invalid")
            if not character_id or character_id == "0" or snapshot.get("role_id") != character_id:
                return result("UNKNOWN" if submitted else "BLOCKED", "training_character_changed")
            observed_cycle = snapshot.get("refresh_cycle")
            if (type(observed_cycle) is not int or observed_cycle <= 0
                    or cycle is not None and (type(cycle) is not int or cycle <= 0)):
                return result("UNKNOWN" if submitted else "BLOCKED", "training_daily_cycle_unavailable")
            if cycle is None:
                cycle = observed_cycle
            if observed_cycle != cycle:
                return result("UNKNOWN" if submitted else "BLOCKED", "training_daily_cycle_changed")
            current, required = snapshot.get("activity_count"), snapshot.get("required")
            if baseline is None:
                baseline = current
            confirmed = (type(current) is int and type(required) is int
                         and required > 0 and current >= required)
            same_operation = snapshot.get("operation_id") == operation
            if same_operation:
                operation_adopted = True
                mutation |= bool(snapshot.get("mutation_attempted"))
            elif submitted:
                # An accepted entry may still be queued on the native GUI. Do
                # not adopt any action from the previous operation meanwhile.
                if not operation_adopted and snapshot.get("busy") is True:
                    self.wait()
                    continue
                return result("UNKNOWN", "training_operation_changed")

            signature = (snapshot.get("status"), snapshot.get("next_action"), snapshot.get("target_id"),
                         snapshot.get("remaining_monsters"), snapshot.get("summon_times"))
            if signature != previous:
                previous = signature
                deadline = self.clock() + self.timeout
            status, action = snapshot.get("status"), snapshot.get("next_action", "")
            if status == "CLOSED" and same_operation:
                return result("BLOCKED", "training_recovered_to_main")
            if (status == "BLOCKED" and same_operation
                    and snapshot.get("reason") == "training_target_cooldown"):
                if not normal_close_submitted:
                    return result("UNKNOWN", "training_cooldown_close_unverified")
                if not _verified_home(snapshot, character_id=character_id,
                                      cycle=cycle, operation_id=operation):
                    self.wait()
                    continue
                cooldown, server_time = snapshot.get("target_cooldown"), snapshot.get("server_time")
                remaining = (cooldown - server_time
                             if type(cooldown) is int and type(server_time) is int
                             and 0 < server_time < cooldown <= 0xffffffff else None)
                return result("WAITING", "training_target_cooldown",
                              cooldown_remaining_seconds=remaining)
            if (status == "COMPLETED" and snapshot.get("clearing_complete") is True
                    and snapshot.get("home_ready") is True and snapshot.get("busy") is False
                    and snapshot.get("cleanup_pending") is False):
                daily = self.objects.observe_daily(self.token)
                actual = next((task for category in daily.get("categories", ())
                               for task in category.get("tasks", ()) if task.get("task_id") == task_id), None)
                if actual is not None:
                    current, required = actual.get("current"), actual.get("required")
                    confirmed = (type(current) is int and type(required) is int
                                 and required > 0 and current >= required)
                return result("COMPLETED", "training_free_summons_exhausted")
            if status == "UNKNOWN":
                return result("UNKNOWN", snapshot.get("reason") or "training_unknown")
            if status == "BLOCKED" and (not action or same_operation):
                return result("BLOCKED" if submitted else "UNAVAILABLE",
                              snapshot.get("reason") or "training_unavailable")
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
                return result("UNKNOWN", ack.get("reason") or "training_action_unknown")
            if ack.get("status") == "REJECTED":
                self.wait()
                continue
            if ack.get("status") not in ("COMPLETED", "ACCEPTED"):
                raise RuntimeError(ack.get("reason") or "training_command_failed")
            submitted = True
            normal_close_submitted |= action == "close"
