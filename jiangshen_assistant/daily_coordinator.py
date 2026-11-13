"""Coordinate the daily workflow with the shared runner and bridge owner."""
from __future__ import annotations

from dataclasses import asdict, replace
import copy
import json
import math
import threading
import time
import uuid

from .bridge_client import BridgeError
from .core import RunContext, TaskRunner
from .daily_workflow import (DailyResult, DailyWorkflow, normalize_selected_items,
                             normalize_enabled_items, normalize_tribute_options)
from .account_profiles import SHOP_DURATIONS
from .event_stream import EventMailbox
from .workflow_wait import WakeSignal
from .scene_navigation import SceneGoal, ensure_scene
from .home_navigation import scene_wait_reason


class DailyCoordinator:
    CONNECT_TIMEOUT = 30.0

    def __init__(self, objects, runner: TaskRunner, data_dir) -> None:
        self.objects, self.runner, self.data_dir = objects, runner, data_dir
        self.events = EventMailbox('daily')
        self._lock = threading.RLock()
        self._wake = WakeSignal()
        self._cooldown_wake = WakeSignal()
        self._cooldown_event_signature = None
        self._active = self._paused = self._stopping = self._closing = False
        self._token = None
        self._result = DailyResult('IDLE', 'ready')
        self._navigation = {}

    @property
    def is_active(self) -> bool:
        with self._lock:
            return self._active

    @property
    def is_paused(self) -> bool:
        with self._lock:
            return self._paused

    @property
    def is_shutdown(self) -> bool:
        """Whether task ownership and connection cleanup have been handed back."""
        return not self.is_active

    @property
    def snapshot(self) -> dict:
        with self._lock:
            return dict(asdict(self._result), **copy.deepcopy(self._navigation))

    def _navigation_progress(self, kind, **values) -> None:
        if kind != 'scene_preparation':
            return
        with self._lock:
            self._navigation = dict(navigation_status=values['status'],
                navigation_reason=values['reason'], navigation_flow=dict(values['flow']))
            self._publish(replace(self._result, status='RUNNING' if values['status'] == 'READY'
                                  else values['status'], reason=values['reason']))

    def _publish(self, result: DailyResult) -> None:
        with self._lock:
            if self._result != result:
                self._result = result
                self.events.put(result)

    def _publish_progress(self, result: DailyResult) -> None:
        # The returned result is published after releasing ownership.
        if result.status == 'RUNNING':
            self._publish(result)

    def _retain_trial_cleanup(self, result: DailyResult, token) -> DailyResult:
        # Command failures can bypass the executor's result. Read this exact
        # owner's retained state before release retires the workflow token;
        # bridge/component shutdown alone does not release the native panel.
        try:
            observation = self.objects.workflow_snapshot(token)
        except Exception as exc:
            return replace(result, status='UNKNOWN', cleanup_pending=True, route_retry_evidence=(),
                           reason=result.reason if result.status == 'UNKNOWN' else
                           f'trial_cleanup_observation_unavailable ({exc})')
        native = (observation.get('state') or {}).get('daily_script')
        role = json.loads(token.context_key)['character_id']
        if (not isinstance(native, dict) or native.get('task_id') != 14
                or native.get('role_id') != role):
            return result
        operation = native.get('operation_id')
        pending = (native.get('cleanup_pending') is True or native.get('busy') is True
                   or bool(operation) and (native.get('home_ready') is False
                                           or native.get('release_verified') is False))
        if not pending:
            return result
        reason = (result.reason if result.status in ('UNKNOWN', 'STOPPED') else
                  native.get('reason') or 'trial_cleanup_unverified')
        rows = tuple(replace(item, status='UNKNOWN', reason=reason, current=None, required=None,
                             cleanup='UNVERIFIED', cleanup_pending=True,
                             operation_id=operation or item.operation_id)
                     if (item.category_id, item.task_id) == (4, 14) else item for item in result.items)
        return replace(result, status='UNKNOWN', reason=reason, character_id=role, items=rows,
                       cleanup_pending=True, route_retry_evidence=())

    def start(self, donation_type: int = 2, selected_items=None, expected_character_id=None,
              expected_user_id=None, expected_zone_id=None, defer_cooldowns=False, tribute_options=None,
              shop_duration=3600, enabled_items=None, retry_source=None, popup_scope=None,
              wait_for_gift=False, gift_wait_until=None, point_reward_only=False) -> bool:
        popup_scope = uuid.uuid4().hex if popup_scope is None else popup_scope
        if (not isinstance(popup_scope, str) or len(popup_scope) != 32
                or any(char not in '0123456789abcdef' for char in popup_scope)):
            return False
        if type(donation_type) is not int or donation_type not in (1, 2, 3):
            return False
        try:
            selected_items = normalize_selected_items(selected_items)
            enabled_items = normalize_enabled_items(enabled_items, selected_items)
            tribute_options = normalize_tribute_options(tribute_options)
            if retry_source is not None:
                from .account_recovery_store import validate_daily_retry_evidence
                if selected_items is None or len(selected_items) != 1:
                    return False
                route = next(iter(selected_items))
                if route not in ('guild_donation', 'divinity', 'mingshi'):
                    return False
                retry_source = validate_daily_retry_evidence(retry_source, route=route,
                    expected_identity=dict(user_id=expected_user_id, zone_id=expected_zone_id, role_id=expected_character_id),
                    expected_account_run=popup_scope)
                if retry_source['proof_kind'] != 'untouched_native_route':
                    return False
        except ValueError:
            return False
        if type(defer_cooldowns) is not bool:
            return False
        if (type(point_reward_only) is not bool or point_reward_only and
                (selected_items != frozenset(('sign',)) or enabled_items != selected_items
                 or retry_source is not None or wait_for_gift)):
            return False
        if type(wait_for_gift) is not bool:
            return False
        if wait_for_gift:
            if (selected_items != frozenset(('online_gift',)) or enabled_items != selected_items
                    or retry_source is not None or type(gift_wait_until) not in (int, float)
                    or not math.isfinite(gift_wait_until) or gift_wait_until > time.monotonic() + 1200.0):
                return False
        elif gift_wait_until is not None:
            return False
        if type(shop_duration) is not int or shop_duration not in SHOP_DURATIONS:
            return False
        for expected_id in (expected_character_id, expected_user_id, expected_zone_id):
            if expected_id is not None and (
                    not isinstance(expected_id, str) or not expected_id.isascii()
                    or not expected_id.isdecimal() or not expected_id.strip('0')):
                return False
        with self._lock:
            if self._active or self._closing or self.runner.is_active or self.objects.has_campaign_owner:
                return False
            self._active = True
            self._paused = self._stopping = False
            self._wake.clear()
            self._cooldown_wake.clear()
            self._cooldown_event_signature = None
            self._navigation = {}
            try:
                accepted = self.runner.start('积分宝箱' if point_reward_only else '日常', lambda ctx: self._run(
                    ctx, donation_type, selected_items, expected_character_id,
                    expected_user_id, expected_zone_id, defer_cooldowns, tribute_options, shop_duration,
                    enabled_items, retry_source, popup_scope, wait_for_gift, gift_wait_until, point_reward_only),
                                             checkpoints=False)
            except Exception as exc:
                self._publish(DailyResult('BLOCKED', f'daily_runner_start_failed ({exc})'))
                accepted = False
            if accepted:
                self._publish(DailyResult('CONNECTING', 'connecting'))
            else:
                self._active = False
            return accepted

    def pause(self) -> bool:
        with self._lock:
            if not self._active or self._paused or self._stopping:
                return False
            self._paused = True
            self.runner.pause()
            drained = self._token is None or self.objects.pause_campaign(self._token)
            if not drained:
                self._publish(DailyResult('UNKNOWN', 'battle_launch_cancel_unverified'))
            self._wake.set()
            self._cooldown_wake.set()
            return drained

    def resume(self) -> bool:
        with self._lock:
            if not self._active or not self._paused or self._stopping:
                return False
            try:
                if self._token is not None and not self.objects.resume_campaign(self._token):
                    return False
            except BridgeError:
                return False
            self._paused = False
            self.runner.resume()
            self._wake.set()
            self._cooldown_wake.set()
            return True

    def stop(self) -> bool:
        with self._lock:
            if not self._active or self._stopping:
                return False
            self._stopping, self._paused = True, False
            if self._token is not None:
                drained = self.objects.stop_campaign(self._token)
            else:
                drained = True
            if not drained:
                self._publish(DailyResult('UNKNOWN', 'battle_launch_cancel_unverified'))
            self.runner.stop()
            self._wake.set()
            self._cooldown_wake.set()
            return drained

    def close(self) -> None:
        with self._lock:
            self._closing = True
            self.stop()

    def _stopped(self, ctx: RunContext) -> bool:
        with self._lock:
            return self._stopping or ctx.cancelled

    def _wait(self) -> None:
        self._wake.wait(.1)

    def _wait_cooldown(self, timeout: float) -> None:
        # Cooldown scheduling belongs to the workflow. A game event can wake
        # it for popup/control handling without starting another route check.
        self._cooldown_wake.wait(min(60.0, max(0.0, timeout)))

    def _notify_workflow(self, subscription) -> None:
        self._wake.set()
        if subscription is None:
            self._cooldown_wake.set()
            return
        # Ordinary state/clock publications need no cooldown wake. Preserve
        # popup transitions and connection/control faults as immediate hints;
        # the worker still obtains fresh native admission before acting.
        snapshot = subscription.latest()
        popup = (snapshot.get('state') or {}).get('daily_popup') or {}
        connection = snapshot.get('connection') or {}
        signature = (
            connection.get('state'), connection.get('error'), snapshot.get('error'),
            snapshot.get('stopping'), snapshot.get('admission_open'),
            popup.get('event_sequence'), popup.get('status'), popup.get('reason'),
            (popup.get('command') or {}).get('status'),
            tuple((row.get('window_type'), row.get('instance_id'), row.get('appearance_generation'),
                   row.get('status'), row.get('visible'), row.get('loading'), row.get('attempted'))
                  for row in popup.get('windows', ()) if isinstance(row, dict)),
        )
        with self._lock:
            changed = signature != self._cooldown_event_signature
            self._cooldown_event_signature = signature
        if changed:
            self._cooldown_wake.set()

    def _await_connection(self, ctx: RunContext, generation: int) -> bool:
        deadline = time.monotonic() + self.CONNECT_TIMEOUT
        while not self._stopped(ctx):
            connection = self.objects.connection_status
            if connection['generation'] != generation:
                raise BridgeError('object_connection_replaced')
            if not connection['busy']:
                if connection['state'] == 'ready':
                    return True
                if connection['state'] in ('error', 'unknown', 'disconnected'):
                    reason = connection.get('error') or connection['state']
                    raise BridgeError(f'object_connection_failed ({reason})')
            if time.monotonic() >= deadline:
                raise BridgeError('object_connection_timeout')
            self._wait()
        return False

    def _run(self, ctx: RunContext, donation_type: int, selected_items=None, expected_character_id=None,
             expected_user_id=None, expected_zone_id=None, defer_cooldowns=False, tribute_options=None,
             shop_duration=3600, enabled_items=None, retry_source=None, popup_scope=None,
             wait_for_gift=False, gift_wait_until=None, point_reward_only=False) -> None:
        connection = token = popup_subscription = None
        result = DailyResult('STOPPED', 'user_stop')
        workflow_started = False
        try:
            with self._lock:
                if self._stopped(ctx):
                    return
                status = self.objects.connection_status
                generation = status['generation']
                if status['state'] != 'ready':
                    connection = self.objects.begin_campaign_connection()
                    if connection is None:
                        refusal = self.objects.connection_status.get('last_refusal')
                        raise BridgeError(f'object_connection_unavailable ({refusal})' if refusal
                                          else 'object_connection_unavailable')
                    generation = connection.generation
            if not self._await_connection(ctx, generation):
                return
            with self._lock:
                if self._stopped(ctx):
                    return
                token = self.objects.acquire_campaign(uuid.uuid4().hex)
                if token.generation != generation:
                    raise BridgeError('object_connection_replaced')
                self._token = token
                expected_identity = (
                    ('character_id', expected_character_id, 'character'),
                    ('user_id', expected_user_id, 'user'),
                    ('zone_id', expected_zone_id, 'zone'),
                )
                if any(expected is not None for _, expected, _ in expected_identity):
                    try:
                        actual_identity = json.loads(token.context_key)
                        if not isinstance(actual_identity, dict):
                            raise ValueError('identity_object_required')
                    except (AttributeError, KeyError, TypeError, ValueError) as exc:
                        raise BridgeError('daily_expected_identity_context_invalid') from exc
                    for field, expected, label in expected_identity:
                        if expected is None:
                            continue
                        if field not in actual_identity:
                            raise BridgeError(f'daily_expected_{label}_missing')
                        if actual_identity[field] != expected:
                            raise BridgeError(f'daily_expected_{label}_mismatch')
                if self._paused and not self.objects.pause_campaign(token):
                    raise BridgeError('object_admission_pause_failed')

            # Subscribe before the initial scan so an appearance between the
            # scan and the next activity is retained for the daily worker.
            popup_subscription = self.objects.subscribe_workflow(
                token, notify=lambda: self._notify_workflow(popup_subscription))

            # Training and other special scenes keep their existing activity
            # recovery adapters inside DailyWorkflow. Ordinary entries share
            # the same battle/result wait and stock return as soul/development.
            state = self.objects.workflow_snapshot(token).get('state') or {}
            root = (state.get('snapshot') or {}).get('root_level_id')
            if enabled_items != frozenset() and (root in ('Capital', 'Campaign', 'Anecdote', 'Divinity', 'Battle')
                                                or scene_wait_reason(state)):
                navigation = ensure_scene(self.objects, token, SceneGoal.ACTIVITY,
                    stopped=lambda: self._stopped(ctx), paused=lambda: self.is_paused,
                    wait=self._wait, publish=self._navigation_progress, timeout=None)
                if navigation.status != 'READY':
                    result = replace(self._result, status=navigation.status, reason=navigation.reason)
                    return

            workflow = DailyWorkflow(self.objects, token, self.data_dir,
                stopped=lambda: self._stopped(ctx), paused=lambda: self.is_paused,
                wait=self._wait, publish=self._publish_progress, donation_type=donation_type,
                popup_subscription=popup_subscription, selected_items=selected_items, enabled_items=enabled_items,
                defer_cooldowns=defer_cooldowns, tribute_options=tribute_options, shop_duration=shop_duration,
                cooldown_wait=self._wait_cooldown, retry_source=retry_source, popup_scope=popup_scope,
                wait_for_gift=wait_for_gift, gift_wait_until=gift_wait_until, point_reward_only=point_reward_only)
            self._publish(DailyResult('RUNNING', 'daily_observing'))
            workflow_started = True
            result = workflow.run()
        except Exception as exc:
            status = 'UNKNOWN' if workflow_started else 'BLOCKED'
            result = replace(self._result, status=status, reason=str(exc))
        finally:
            with self._lock:
                try:
                    if (workflow_started and token is not None
                            and (selected_items is None or 'trial' in selected_items)):
                        result = self._retain_trial_cleanup(result, token)
                    retry_owner_verified = False
                    if result.route_retry_evidence and token is not None:
                        # Validate that this exact token still owns the session
                        # before retiring it. release_campaign is otherwise an
                        # intentional no-op for an obsolete token.
                        try:
                            self.objects.workflow_snapshot(token)
                            retry_owner_verified = True
                        except Exception:
                            result = replace(result, route_retry_evidence=())
                    if popup_subscription is not None:
                        popup_subscription.close()
                    owns_connection = (connection is not None and token is not None and
                                       connection.generation == token.generation)
                    if token is not None:
                        # An interrupted or unknown activity may still own a
                        # native panel even when this run reused a connection.
                        # Request the normal bridge cleanup before another task.
                        self.objects.release_campaign(token, cleanup=owns_connection or
                                                      result.status in ('UNKNOWN', 'STOPPED') or
                                                      result.cleanup_pending)
                    if connection is not None and not owns_connection:
                        self.objects.cancel_campaign_connection(connection)
                    if result.route_retry_evidence:
                        released = self.objects.connection_status
                        retry_released = (retry_owner_verified and result.status == 'PARTIAL'
                            and not result.cleanup_pending and not self.objects.has_campaign_owner
                            and released.get('generation') == token.generation
                            and released.get('state') == 'ready' and released.get('busy') is False
                            and not released.get('error'))
                        result = replace(result, route_retry_evidence=tuple(
                            dict(record, coordinator_released=True) for record in result.route_retry_evidence
                        ) if retry_released else ())
                except Exception as exc:
                    result = replace(result, status='UNKNOWN', reason=f'cleanup_unconfirmed ({exc})',
                                     cleanup_pending=True, route_retry_evidence=())
                finally:
                    self._token = None
                    self._active = self._paused = False
                    self._publish(result)
