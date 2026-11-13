"""Prepare a worker's entry scene using ordinary, owner-scoped navigation.

The cache projection is also safe to use from the UI/scheduler. Only
``ensure_scene`` and ``ObjectController.observe_scene`` perform bridge I/O.
"""
from __future__ import annotations

from dataclasses import dataclass
from enum import Enum
import json
import time

from .home_navigation import (ensure_home_scene, home_scene_ready, scene_wait_reason,
                              result_navigation_candidate)


class SceneGoal(str, Enum):
    HOME = 'HOME'
    SOUL_ENTRY = 'SOUL_ENTRY'
    ACTIVITY = 'ACTIVITY'


@dataclass(frozen=True)
class ScenePreparation:
    status: str
    reason: str
    goal: str
    phase: str
    observed: dict | None = None
    flow: dict | None = None


def scene_context_from_state(state, *, identity=None, generation=None,
                             connection='unknown', owner_run_id=None, stopping=False):
    """Project named facts only: never return context keys, nonces or raw state."""
    current = state.get('campaign') or {}
    active = next((state.get(scope) for scope in ('campaign', 'challenge', 'elite')
                   if (state.get(scope) or {}).get('phase') in ('BATTLE', 'RESULT')), current)
    common = state.get('snapshot') or {}
    ui = current.get('ui_context') or {}
    pending = []
    for scope in ('campaign', 'challenge', 'elite'):
        view = state.get(scope) or {}
        attempt = view.get('attempt') or {}
        if attempt.get('status') in ('SUBMITTED', 'WAITING', 'UNKNOWN', 'CONTINUED'):
            pending.append(dict(scope=scope, kind='battle', status=attempt['status'],
                                attempt_id=attempt.get('attempt_id'), command_seq=attempt.get('command_seq')))
        for name, statuses in (('entry', ('WAITING', 'UNKNOWN')),
                               ('home_navigation', ('RETURNING', 'LIST_READY', 'CLOSING', 'UNKNOWN'))):
            operation = view.get(name) or {}
            if operation.get('status') in statuses:
                pending.append(dict(scope=scope, kind=name, status=operation['status'],
                                    command_seq=operation.get('command_seq', operation.get('return_command_seq'))))
        if view.get('pending_story_attempt_id'):
            pending.append(dict(scope=scope, kind='story', status='WAITING',
                                attempt_id=view['pending_story_attempt_id']))
    domains = {name: value for name, value in state.items()
               if name.startswith('daily_') and isinstance(value, dict)}
    domains.update({name: value for name, value in (state.get('development') or {}).items()
                    if isinstance(value, dict)})
    for scope, value in domains.items():
        operation = value.get('operation') or {}
        unresolved = operation.get('status') in ('QUEUED', 'OPENING', 'PENDING', 'CLOSING', 'UNKNOWN',
                                                'WAITING', 'READY', 'OPEN', 'STOPPED')
        if scope == 'daily_soul' and operation.get('status') in ('REJECTED', 'STOPPED'):
            unresolved = not (operation.get('mutation_attempted') is False and
                (value.get('ui_cleanup') == 'clear' or value.get('ui_cleanup') == 'none'
                 and operation.get('invoked') is False))
        if unresolved and operation.get('operation_id'):
            pending.append(dict(scope=scope, kind='operation', status=operation['status'],
                                operation_id=operation.get('operation_id'), command_seq=operation.get('sequence')))
        if value.get('busy') is True or value.get('cleanup_pending') is True:
            pending.append(dict(scope=scope, kind='cleanup', status=value.get('status'),
                                operation_id=value.get('operation_id')))
    reason = scene_wait_reason(state)
    ready = (identity is not None and connection == 'ready' and not stopping
             and not pending and home_scene_ready(state))
    if not reason:
        reason = ('场景已可交互' if ready else '等待原操作完成' if pending else
                  '连接或身份未就绪' if identity is None or connection != 'ready' else
                  '正在停止' if stopping else str(current.get('gate') or '场景可执行性尚未确认'))
        if reason == 'PASS':
            reason = '当前场景的目标入口尚未确认可执行'
    # loading_or_battle cannot distinguish loading from an active battle.
    loading = True if any((state.get(scope) or {}).get('gate') in
        ('formation_loading', 'challenge_list_loading')
        for scope in ('campaign', 'challenge', 'elite')) else False if ready else None
    # Retained receipts can outlive the connection. Their last page is not a
    # current scene observation after the role/session has been released.
    observable = identity is not None and connection == 'ready'
    return dict(identity=identity, session_binding=dict(generation=generation,
                bound=observable), connection=connection,
                root=(common.get('root_level_id') or ui.get('current_root_level')) if observable else None,
                scene=common.get('scene') if observable else None,
                phase=active.get('phase') if observable else None,
                gate=active.get('gate') if observable else None,
                loading=loading if observable else None, interactable=(True if ready else False if
                common.get('main_enabled') is False or ui.get('global_input_enabled') is False else None)
                if observable else None,
                owner_run_id=owner_run_id, pending_operation=pending or None,
                handoff_ready=ready, observed_revision=active.get('revision') if observable else None,
                observed_frame=state.get('frame') if observable else None, reason=reason)


def ensure_scene(objects, token, goal=SceneGoal.HOME, *, stopped=lambda: False,
                 paused=lambda: False, wait=None, publish=None, timeout=30.,
                 clock=time.monotonic, retain_current_activity=False):
    """Wait for a safe entry; use only explicit native result-exit capabilities.

    ``timeout=None`` waits for an external scene change until stop/pause. An
    invoked normal return keeps the existing bounded ownership protocol; an
    UNKNOWN return is terminal here and is never retried.
    """
    goal = SceneGoal(goal)
    wait = wait or (lambda: time.sleep(.1))
    publish = publish or (lambda *_args, **_kwargs: None)
    deadline = None if timeout is None else clock() + max(0., timeout)
    observed = None
    last_progress = None
    next_observe = 0.
    result_rejections = {}
    role = json.loads(token.context_key)['character_id']

    def progress(phase, reason, status=None):
        nonlocal last_progress
        status = status or ('READY' if phase == 'READY' else 'WAITING' if phase == 'WAITING' else 'PREPARING')
        flow = dict(goal=goal.value, phase=phase, status=status, reason=reason,
                    root=((observed or {}).get('state') or {}).get('snapshot', {}).get('root_level_id'))
        signature = (phase, status, reason, flow['root'])
        if signature != last_progress:
            publish('scene_preparation', status=status, reason=reason, flow=flow)
            last_progress = signature
        return ScenePreparation(status, reason, goal.value, phase, observed, flow)

    def home_progress(kind, **values):
        if kind == 'home_navigation_intent' or kind.endswith('_intent'):
            progress('RETURNING', '正在通过当前玩法的正常入口返回')
        elif kind == 'home_navigation_waiting':
            progress('WAITING', values['reason'])
        elif kind == 'home_navigation_observation' or kind.endswith('_closed'):
            progress('VERIFYING', '等待导航目的场景恢复可交互')

    progress('PLAN', '正在确认目标入口场景')
    while True:
        if stopped():
            return progress('BLOCKED', 'user_stop', 'STOPPED')
        if paused():
            started = clock()
            progress('WAITING', '已暂停场景准备')
            while paused() and not stopped():
                wait()
            if deadline is not None:
                deadline += max(0., clock() - started)
            next_observe = 0.
            continue
        if deadline is not None and clock() >= deadline and observed is not None:
            return progress('WAITING', last_progress[2] if last_progress else '等待场景可执行')
        if clock() < next_observe:
            wait()
            continue
        try:
            observed = objects.observe_scene(token)
            next_observe = clock() + 1.
        except Exception as exc:
            if stopped():
                return progress('BLOCKED', 'user_stop', 'STOPPED')
            if paused():
                continue
            return progress('UNKNOWN', '场景观察不可用：' + str(exc), 'UNKNOWN')
        if observed.get('context_key') != token.context_key or observed.get('stopping'):
            return progress('UNKNOWN', '场景会话已改变或正在停止', 'UNKNOWN')
        state = observed.get('state') or {}
        reason = scene_wait_reason(state)
        result_candidate = result_navigation_candidate(state, role)
        if reason and result_candidate is None:
            progress('WAITING', reason)
            wait()
            continue
        # Never resume a previously invoked return by issuing it again from a
        # fresh caller, even if a destination root has already become visible.
        navigation_pending = any(((state.get(scope) or {}).get('home_navigation') or {}).get('status')
            in ('RETURNING', 'LIST_READY', 'CLOSING', 'UNKNOWN') for scope in ('challenge', 'elite'))
        if navigation_pending:
            progress('WAITING', '等待原返回操作确认；保留其导航归属')
            wait()
            continue
        pending = scene_context_from_state(state).get('pending_operation') or []
        foreign_pending = [item for item in pending
                           if not (goal == SceneGoal.SOUL_ENTRY and item['scope'] == 'daily_soul')]
        if foreign_pending:
            progress('WAITING', '等待原操作或页面清理确认：' + foreign_pending[0]['scope'])
            wait()
            continue
        # Soul supports both Capital and Campaign through its existing native
        # SharedDevelopmentContext. Its observed UI capability can admit a
        # reusable stock soul panel even when campaign's generic gate sees it.
        if goal == SceneGoal.SOUL_ENTRY and (state.get('snapshot') or {}).get('root_level_id') in ('Capital', 'Campaign'):
            if stopped() or paused():
                continue
            try:
                from .daily_soul import _valid, _clean_prior
                soul_observation = objects.observe_daily_soul(token)
                soul = soul_observation.get('soul') or {}
                if soul_observation.get('enabled') is not True:
                    return progress('BLOCKED', '聚魂原生入口未启用', 'BLOCKED')
                if not _valid(soul):
                    return progress('UNKNOWN', '聚魂入口观察无效', 'UNKNOWN')
                ui = soul.get('ui') or {}
                if (ui.get('gate') == 'PASS' and ui.get('loading') is False
                        and (soul.get('observation') or {}).get('available') is True
                        and _clean_prior(soul)
                        and (ui.get('visible') is False or soul.get('action') == 'open')):
                    return progress('READY', '聚魂入口已观察为可执行')
                soul_reason = str(soul.get('reason') or '等待聚魂入口恢复可执行')
            except Exception as exc:
                if stopped():
                    return progress('BLOCKED', 'user_stop', 'STOPPED')
                if paused():
                    continue
                return progress('UNKNOWN', '聚魂入口观察不可用：' + str(exc), 'UNKNOWN')
        else:
            soul_reason = ''
        if home_scene_ready(state) and goal != SceneGoal.SOUL_ENTRY:
            return progress('READY', '目标入口场景已可交互')
        progress('VERIFYING', soul_reason or '正在确认已有正常导航路径')
        remaining = 30. if deadline is None else max(.1, deadline - clock())
        navigation = ensure_home_scene(objects, token, stopped=stopped, paused=paused,
            wait=wait, publish=home_progress, timeout=min(30., remaining), clock=clock,
            retain_current_activity=retain_current_activity, result_rejections=result_rejections)
        observed = navigation.observed or observed
        if navigation.status in ('UNKNOWN', 'STOPPED'):
            return progress('UNKNOWN' if navigation.status == 'UNKNOWN' else 'BLOCKED',
                            navigation.reason, navigation.status)
        if navigation.status == 'BLOCKED':
            # No invocation occurred. An unavailable scene is preparation,
            # rather than a failed soul/business operation.
            progress('WAITING', navigation.reason)
        elif navigation.status == 'WAITING':
            progress('WAITING', navigation.reason)
        elif goal == SceneGoal.ACTIVITY and retain_current_activity and navigation.reason == 'home_navigation_current_activity_retained':
            return progress('READY', navigation.reason)
        else:
            progress('WAITING', soul_reason or '等待目标入口场景可执行')
        next_observe = clock() + 1.
        wait()
