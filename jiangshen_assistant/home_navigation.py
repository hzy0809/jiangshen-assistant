"""Use scoped stock returns and exact list-close branches."""
from __future__ import annotations

from dataclasses import dataclass
import json
import time

from .bridge_client import (BridgeAdmissionClosed, _campaign_valid, _challenge_valid, _elite_valid,
                            _home_navigation_valid, _home_loss_advice_ready, _home_battle_guide_ready)
from .objects import CampaignAdmissionClosed


@dataclass(frozen=True)
class HomeNavigationResult:
    status: str
    reason: str
    observed: dict | None = None


def scene_wait_reason(state):
    """Describe a scene which no new entry may adopt; issue no game command."""
    common = state.get('snapshot') or {}
    views = [state.get(name) or {} for name in ('campaign', 'challenge', 'elite')]
    if any(view.get('phase') == 'RESULT' or view.get('result_kind') in ('NORMAL', 'REWARD', 'CHAPTER')
           for view in views):
        return '等待当前结果页的正常退出；未决战斗仍由原玩法结算'
    if common.get('root_level_id') == 'Battle' or any(
            (view.get('ui_context') or {}).get('current_root_level') == 'Battle' or
            view.get('phase') == 'BATTLE' or
            ((view.get('ui_context') or {}).get('counts') or {}).get('battle', {}).get('visible', 0) > 0
            for view in views):
        return '等待当前战斗结束'
    for view in views:
        attempt = view.get('attempt') or {}
        if attempt.get('status') in ('SUBMITTED', 'WAITING', 'UNKNOWN', 'CONTINUED'):
            return '等待原战斗操作确认：' + str(attempt.get('evidence') or attempt['status'])
        entry = view.get('entry') or {}
        if entry.get('status') in ('WAITING', 'UNKNOWN'):
            return '等待原玩法导航确认：' + str(entry.get('reason') or entry['status'])
        if view.get('pending_story_attempt_id'):
            return '等待原战斗的后续剧情或奖励流程完成'
    return ''


def home_scene_ready(state):
    """Require observed interaction and an idle phase, rather than a root name."""
    current = state.get('campaign') or {}
    common = state.get('snapshot') or {}
    ui = current.get('ui_context') or {}
    return (not scene_wait_reason(state) and _campaign_valid(current)
            and common.get('root_level_id') in ('Campaign', 'Capital')
            and ui.get('current_root_level') == common.get('root_level_id')
            and current.get('phase') in ('MAIN', 'CAMPAIGN')
            and current.get('available') is True and current.get('gate') == 'PASS'
            and ui.get('current_root_active') is True and ui.get('global_input_enabled') is True
            and ui.get('selection_gate') == 'PASS' and common.get('main_enabled') is True
            and common.get('gate') != 'loading_or_battle')


def _scene_exit_unresolved(state):
    views = [state.get(name) or {} for name in ('campaign', 'challenge', 'elite')]
    return any((view.get('attempt') or {}).get('status') in
               ('SUBMITTED', 'WAITING', 'UNKNOWN', 'CONTINUED') or
               (view.get('entry') or {}).get('status') in ('WAITING', 'UNKNOWN') or
               ((view.get('home_navigation') or {}).get('status') in
                ('RETURNING', 'LIST_READY', 'CLOSING', 'UNKNOWN')) or
               view.get('pending_story_attempt_id') for view in views)


def fresh_loss_advice_candidate(state, role):
    """Exact mainline current-page LossAdviceGate, without a tracked defeat."""
    snap = state.get('campaign') or {}
    ui = snap.get('ui_context') or {}
    tutorial = snap.get('tutorial') or {}
    flow = snap.get('flow') or {}
    attempt = snap.get('attempt') or {}
    if (not _scene_exit_unresolved(state) and _campaign_valid(snap)
            and snap.get('character_id') == role and snap.get('campaign_type') == 0
            and snap.get('phase') == 'SCENARIO' and snap.get('available') is True and snap.get('gate') == 'PASS'
            and snap.get('campaign_id', 0) > 0 and attempt.get('status') == 'NONE'
            and attempt.get('attempt_id') == '' and ui.get('current_root_level') == 'Campaign'
            and ui.get('current_root_active') is True and ui.get('selection_gate') == 'PASS'
            and ui.get('current_root_instance_id') and tutorial.get('kind') == 'SCENARIO'
            and tutorial.get('step') == 'loss-advice:current-page' and tutorial.get('gate') == 'PASS'
            and flow.get('status') == 'ACTION' and flow.get('action') == 'continue'):
        return 'campaign', snap
    return None


def result_navigation_candidate(state, role):
    """Select only the native's current, ordinary, untracked result adapter.

    ``result_navigation`` is calculated by FreshResultNavigation; it is not a
    Python inference from a battle outcome. Other scopes' pending attempts
    still belong to their existing battle flow.
    """
    if _scene_exit_unresolved(state):
        return None
    candidates = []
    for scope, validator in (('campaign', _campaign_valid), ('challenge', _challenge_valid), ('elite', _elite_valid)):
        snap = state.get(scope) or {}
        ui = snap.get('ui_context') or {}
        attempt = snap.get('attempt') or {}
        action = snap.get('result_navigation')
        if (validator(snap) and snap.get('character_id') == role
                and snap.get('available') is True and snap.get('gate') == 'PASS'
                and snap.get('phase') == 'RESULT' and attempt.get('status') == 'NONE'
                and attempt.get('attempt_id') == ''
                and action in ('ack_result', 'next_chapter')
                and snap.get('result_kind') in (('CHAPTER',) if action == 'next_chapter' else ('NORMAL', 'REWARD'))
                and snap.get('campaign_id', 0) > 0
                and (action == 'next_chapter' or snap.get('troop_id', 0) > 0)
                and ui.get('current_root_level') == ('Campaign' if scope == 'campaign' else 'Anecdote')
                and ui.get('current_root_active') is True and ui.get('selection_gate') == 'PASS'
                and ui.get('current_root_instance_id')):
            candidates.append((scope, snap))
    return candidates[0] if len(candidates) == 1 else None


def result_page_key(scope, snap):
    """An invocation-local refusal latch, never a historical battle identity."""
    return (scope, (snap.get('ui_context') or {}).get('current_root_instance_id'),
            snap.get('result_kind'), snap.get('campaign_id'), snap.get('troop_id'),
            snap.get('result_navigation'))


def ensure_home_scene(objects, token, *, stopped=lambda: False, paused=lambda: False,
                      wait=None, timeout=30.0, clock=time.monotonic, publish=None,
                      retain_current_activity=False, result_rejections=None):
    """Return an idle map or dismiss an exact list; retain every unknown action.

    Other activities are left to their existing entry policy. A task owns both
    commands through its existing token, battle epoch, pause and stop admission.
    The returned observation is freshly requested after the final local action.
    """
    wait = wait or (lambda: time.sleep(.1))
    publish = publish or (lambda *_args, **_kwargs: None)
    binding = json.loads(token.context_key)
    role = binding['character_id']
    scope = None
    owned = None
    closing = False
    invocation_uncertain = False
    waiting_list = None
    pending_advice = None
    pending_result = None
    result_rejections = {} if result_rejections is None else result_rejections
    advice_attempted = False
    mainline_advice_attempted = False
    waiting_guide = None
    validators = {'campaign': _campaign_valid, 'challenge': _challenge_valid, 'elite': _elite_valid}
    deadline = clock() + timeout
    last_observed = None
    last_wait_reason = None

    def finish(status, reason):
        publish('home_navigation_result', status=status, reason=reason)
        return HomeNavigationResult(status, reason, last_observed)

    def submit(action, snap, source_id):
        nonlocal invocation_uncertain
        if stopped() or paused():
            raise CampaignAdmissionClosed('home_navigation_cancelled_before_submit')
        invocation_uncertain = True
        wire = objects.submit_home_navigation(token, action, scope=scope,
            expected_revision=snap['revision'], campaign_id=source_id)
        ack = wire.get('ack') or {}
        receipt = (ack.get('result') or {}).get(scope + '_command') or {}
        sequence = wire.get('requested_seq', wire.get('seq'))
        publish('home_navigation_ack', action=action, wire=wire)
        if (type(sequence) is not int or ack.get('command_seq') != sequence
                or receipt.get('command_seq') != sequence or receipt.get('action') != action
                or receipt.get('attempt_id') != ''):
            return None, 'home_navigation_ack_correlation_failed', 'UNKNOWN'
        if ack.get('status') == 'REJECTED' and receipt.get('invoked') is False:
            invocation_uncertain = False
            return None, str(receipt.get('reason') or 'home_navigation_rejected'), 'BLOCKED'
        if ack.get('status') != 'COMPLETED' or receipt.get('invoked') is not True:
            return None, 'home_navigation_invocation_unconfirmed', 'UNKNOWN'
        navigation = ((ack.get('result') or {}).get(scope) or {}).get('home_navigation')
        if (not _home_navigation_valid(navigation) or navigation['character_id'] != role
                or navigation['source_campaign_id'] != source_id
                or navigation['close_command_seq' if action != 'return_home' else 'return_command_seq'] != sequence):
            return None, 'home_navigation_ownership_receipt_mismatch', 'UNKNOWN'
        return navigation, '', ''

    def dismiss_loss_advice(snap, kind='loss_advice'):
        nonlocal invocation_uncertain
        if scope == 'campaign' and kind == 'loss_advice':
            return dismiss_result(scope, snap, action='continue')
        if stopped() or paused():
            raise CampaignAdmissionClosed('home_navigation_cancelled_before_submit')
        invocation_uncertain = True
        wire = objects.submit_home_navigation(token, 'continue', scope=scope,
            expected_revision=snap['revision'], campaign_id=snap['campaign_id'])
        ack = wire.get('ack') or {}
        receipt = (ack.get('result') or {}).get(scope + '_command') or {}
        sequence = wire.get('requested_seq', wire.get('seq'))
        prefix = 'home_' + kind
        publish(prefix + '_ack', scope=scope, wire=wire)
        if (type(sequence) is not int or ack.get('command_seq') != sequence
                or receipt.get('command_seq') != sequence or receipt.get('action') != 'continue'
                or receipt.get('attempt_id') != ''):
            return None, prefix + '_ack_correlation_failed', 'UNKNOWN'
        if ack.get('status') == 'REJECTED' and receipt.get('invoked') is False:
            invocation_uncertain = False
            return None, str(receipt.get('reason') or prefix + '_rejected'), 'BLOCKED'
        if ack.get('status') != 'COMPLETED' or receipt.get('invoked') is not True:
            return None, prefix + '_invocation_unconfirmed', 'UNKNOWN'
        return sequence, '', ''

    def dismiss_result(result_scope, snap, action=None):
        nonlocal invocation_uncertain
        if stopped() or paused():
            raise CampaignAdmissionClosed('home_result_cancelled_before_submit')
        action = snap['result_navigation'] if action is None else action
        invocation_uncertain = True
        wire = objects.submit_scene_result(token, action, scope=result_scope,
            expected_revision=snap['revision'], campaign_id=snap['campaign_id'], troop_id=snap['troop_id'],
            root_instance_id=snap['ui_context']['current_root_instance_id'], result_kind=snap['result_kind'])
        ack = wire.get('ack') or {}
        result = ack.get('result') or {}
        receipt = result.get(result_scope + '_command') or {}
        sequence = wire.get('requested_seq', wire.get('seq'))
        publish('home_result_navigation_ack', scope=result_scope, action=action, wire=wire)
        if (type(sequence) is not int or ack.get('command_seq') != sequence
                or ack.get('command_name') != result_scope + '-command'
                or receipt.get('command_seq') != sequence or receipt.get('action') != action
                or receipt.get('attempt_id') != ''):
            return None, 'home_result_navigation_ack_correlation_failed', 'UNKNOWN'
        if ack.get('status') == 'REJECTED' and receipt.get('invoked') is False:
            invocation_uncertain = False
            return None, str(receipt.get('reason') or 'home_result_navigation_rejected'), 'WAITING'
        after = result.get(result_scope) or {}
        if (ack.get('status') != 'COMPLETED' or receipt.get('invoked') is not True
                or not validators[result_scope](after) or after.get('character_id') != role
                or after.get('revision', 0) < snap['revision']
                or (after.get('attempt') or {}).get('status') != 'NONE'
                or (after.get('attempt') or {}).get('attempt_id') != ''
                or any(key in binding and after.get(key) != binding[key] for key in ('user_id', 'zone_id'))):
            return None, 'home_result_navigation_invocation_unconfirmed', 'UNKNOWN'
        return sequence, '', ''

    while True:
        if stopped():
            return finish('STOPPED', 'user_stop_home_navigation_pending_retained' if owned or pending_advice or pending_result else 'user_stop')
        if paused():
            paused_at = clock()
            while paused() and not stopped():
                wait()
            deadline += max(0., clock() - paused_at)
            continue
        try:
            last_observed = objects.observe_home_navigation(token)
            if (last_observed.get('context_key') != token.context_key or last_observed.get('stopping')):
                if stopped():
                    return finish('STOPPED', 'user_stop_home_navigation_pending_retained'
                                  if owned or pending_result or pending_advice else 'user_stop')
                return finish('UNKNOWN' if owned or pending_result else 'BLOCKED', 'home_navigation_context_changed')
            state = last_observed.get('state') or {}
            current = state.get('campaign') or {}
            current_ui = current.get('ui_context') or {}
            root = current_ui.get('current_root_level')
            if current.get('character_id') != role:
                return finish('UNKNOWN' if owned or pending_result else 'BLOCKED', 'home_navigation_character_changed')
            if owned is None:
                if pending_result is not None:
                    result_scope, before_revision, source_key, _sequence = pending_result
                    fresh = state.get(result_scope) or {}
                    if (not validators[result_scope](fresh) or fresh.get('character_id') != role
                            or (fresh.get('attempt') or {}).get('status') != 'NONE'
                            or (fresh.get('attempt') or {}).get('attempt_id') != ''
                            or any(key in binding and fresh.get(key) != binding[key] for key in ('user_id', 'zone_id'))):
                        return finish('UNKNOWN', 'home_result_navigation_identity_changed')
                    fresh_ui = fresh.get('ui_context') or {}
                    if fresh['revision'] <= before_revision or fresh.get('result_kind') == source_key[2]:
                        if clock() >= deadline:
                            return finish('UNKNOWN', 'home_result_navigation_timeout_pending_retained')
                        wait()
                        continue
                    if fresh_ui.get('current_root_level') in (None, '', 'OTHER'):
                        if clock() >= deadline:
                            return finish('UNKNOWN', 'home_result_navigation_timeout_pending_retained')
                        wait()
                        continue
                    if fresh_ui.get('current_root_level') not in ('Campaign', 'Capital', 'Anecdote'):
                        return finish('UNKNOWN', 'home_result_navigation_destination_unknown')
                    publish('home_result_navigation_closed', scope=result_scope, observed=fresh)
                    pending_result = None
                    invocation_uncertain = False
                if pending_advice is not None:
                    advice_scope, source_root, source_campaign, before_revision, before_step, _sequence, kind, source_level = pending_advice
                    prefix = 'home_' + kind
                    fresh = state.get(advice_scope) or {}
                    fresh_ui = fresh.get('ui_context') or {}
                    if (not validators[advice_scope](fresh)
                            or fresh.get('character_id') != role or root != source_level
                            or fresh_ui.get('current_root_instance_id') != source_root
                            or fresh.get('campaign_id') != source_campaign):
                        return finish('UNKNOWN', prefix + '_context_changed')
                    still_advice = (fresh.get('phase') == 'SCENARIO'
                                    and (fresh.get('tutorial') or {}).get('step') == before_step)
                    if fresh['revision'] <= before_revision or still_advice:
                        if clock() >= deadline:
                            return finish('UNKNOWN', prefix + '_close_timeout_pending_retained')
                        wait()
                        continue
                    if fresh.get('phase') != 'CAMPAIGN':
                        return finish('UNKNOWN', prefix + '_destination_changed')
                    if kind == 'battle_guide' and (fresh.get('available') is not True or fresh.get('gate') != 'PASS'
                            or fresh_ui.get('current_root_active') is not True or fresh_ui.get('global_input_enabled') is not True
                            or fresh_ui.get('selection_gate') != 'PASS' or (state.get('snapshot') or {}).get('main_enabled') is not True):
                        return finish('UNKNOWN', prefix + '_destination_not_interactive')
                    publish(prefix + '_closed', scope=advice_scope, observed=fresh)
                    pending_advice = None
                    invocation_uncertain = False
                # This local close precedes every scene shortcut, including a
                # new task whose own battle observer has never seen the loss.
                guides = [(name, state.get(name) or {}) for name, validator in validators.items()
                          if validator(state.get(name) or {})
                          and str(((state.get(name) or {}).get('tutorial') or {}).get('step', '')).startswith('battle-guide:')]
                if guides:
                    if len(guides) != 1 or advice_attempted:
                        return finish('UNKNOWN' if advice_attempted else 'BLOCKED', 'home_battle_guide_identity_ambiguous')
                    scope, snap = guides[0]
                    if snap.get('character_id') != role:
                        return finish('BLOCKED', 'home_battle_guide_character_changed')
                    witness = (scope, snap['ui_context']['current_root_instance_id'], snap['tutorial']['step'])
                    if waiting_guide is not None and witness != waiting_guide:
                        return finish('BLOCKED', 'home_battle_guide_replaced')
                    if _home_battle_guide_ready(snap, scope):
                        publish('home_battle_guide_intent', scope=scope, before=snap)
                        sequence, reason, status = dismiss_loss_advice(snap, 'battle_guide')
                        if status:
                            return finish(status, reason)
                        advice_attempted = True
                        pending_advice = (scope, witness[1], snap['campaign_id'], snap['revision'], witness[2],
                                          sequence, 'battle_guide', snap['ui_context']['current_root_level'])
                        deadline = clock() + timeout
                        wait()
                        continue
                    flow = snap.get('flow') or {}
                    if (flow.get('status') == 'WAIT' and str(flow.get('reason', '')).startswith('WAITING_BATTLE_GUIDE_')
                            and snap.get('available') is True and snap.get('gate') == 'PASS'):
                        waiting_guide = witness
                        if clock() >= deadline:
                            return finish('BLOCKED', 'home_battle_guide_input_timeout')
                        wait()
                        continue
                    return finish('BLOCKED', snap.get('gate') if snap.get('gate') != 'PASS'
                                  else snap['tutorial'].get('gate') or 'home_battle_guide_close_unavailable')
                fresh_advice = fresh_loss_advice_candidate(state, role)
                if fresh_advice is not None:
                    scope, snap = fresh_advice
                    key = result_page_key(scope, snap)
                    if key in result_rejections or mainline_advice_attempted:
                        reason = result_rejections.get(key) or '等待原主线提示关闭操作确认'
                        publish('home_navigation_waiting', status='WAITING', reason=reason)
                        if clock() >= deadline:
                            return finish('WAITING', reason)
                        wait()
                        continue
                    publish('home_loss_advice_intent', scope=scope, before=snap)
                    sequence, reason, status = dismiss_loss_advice(snap)
                    if status == 'UNKNOWN':
                        return finish(status, reason)
                    if status:
                        if reason != 'campaign_revision_changed':
                            reason = '等待主线普通提示允许关闭：' + reason
                            result_rejections[key] = reason
                        else:
                            reason = '主线普通提示观察已更新，等待重新确认'
                        publish('home_navigation_waiting', status='WAITING', reason=reason)
                        if clock() >= deadline:
                            return finish('WAITING', reason)
                        wait()
                        continue
                    mainline_advice_attempted = True
                    pending_advice = (scope, snap['ui_context']['current_root_instance_id'],
                        snap['campaign_id'], snap['revision'], snap['tutorial']['step'], sequence,
                        'loss_advice', 'Campaign')
                    deadline = clock() + timeout
                    wait()
                    continue
                candidate = result_navigation_candidate(state, role)
                if candidate is not None:
                    result_scope, snap = candidate
                    key = result_page_key(result_scope, snap)
                    if key in result_rejections:
                        reason = result_rejections[key]
                        if reason != last_wait_reason:
                            publish('home_navigation_waiting', status='WAITING', reason=reason)
                            last_wait_reason = reason
                        if clock() >= deadline:
                            return finish('WAITING', reason)
                        wait()
                        continue
                    publish('home_result_navigation_intent', scope=result_scope,
                            action=snap['result_navigation'], before=snap)
                    sequence, reason, status = dismiss_result(result_scope, snap)
                    if status == 'UNKNOWN':
                        return finish(status, reason)
                    if status == 'WAITING':
                        # A native non-invocation can be observed again, but an
                        # unsupported page must not be hammered on every wake.
                        if reason != 'campaign_revision_changed':
                            reason = '等待当前结果页允许正常退出：' + reason
                            result_rejections[key] = reason
                        else:
                            reason = '结果页观察已更新，等待重新确认'
                        publish('home_navigation_waiting', status='WAITING', reason=reason)
                        if clock() >= deadline:
                            return finish('WAITING', reason)
                        wait()
                        continue
                    pending_result = (result_scope, snap['revision'], key, sequence)
                    deadline = clock() + timeout
                    wait()
                    continue
                # Root names alone cannot authorize a new task. A manual
                # battle may have no assistant attempt, while other views can
                # still retain an unresolved attempt after the root changed.
                reason = scene_wait_reason(state)
                if reason:
                    if reason != last_wait_reason:
                        publish('home_navigation_waiting', status='WAITING', reason=reason)
                        last_wait_reason = reason
                    if clock() >= deadline:
                        return finish('WAITING', reason)
                    wait()
                    continue
                own_type = {'challenge': 2, 'elite': 4}.get(token.scope)
                own_snap = state.get(token.scope) or {}
                if (retain_current_activity and own_type is not None
                        and own_snap.get('phase') in ('CHALLENGE_LIST', 'CAMPAIGN')
                        and own_snap.get('available') is True and own_snap.get('gate') == 'PASS'
                        and (own_snap.get('phase') == 'CHALLENGE_LIST' or own_snap.get('campaign_type') == own_type)):
                    return finish('NOT_NEEDED', 'home_navigation_current_activity_retained')
                if root == 'OTHER' and (state.get('snapshot') or {}).get('root_level_id') == 'Divinity':
                    # The campaign UI context deliberately groups other scene
                    # names. Admission still needs the native Divinity gate.
                    root = 'Divinity'
                if root == 'Divinity':
                    scope = 'challenge'
                    snap = state.get(scope) or {}
                    navigation = snap.get('home_navigation')
                    if (not _challenge_valid(snap) or not _home_navigation_valid(navigation)
                            or snap.get('character_id') != role or navigation.get('gate') != 'PASS'
                            or navigation.get('divinity_return') is None):
                        return finish('BLOCKED', (navigation or {}).get('gate') or 'home_divinity_source_unavailable')
                    publish('home_navigation_intent', action='return_home', scope=scope, before=snap)
                    navigation, reason, status = submit('return_home', snap, 0)
                    if status:
                        return finish(status, reason)
                    if navigation['origin'] != 'DIVINITY':
                        return finish('UNKNOWN', 'home_divinity_return_receipt_mismatch')
                    if navigation['divinity_return'] != snap['home_navigation']['divinity_return']:
                        return finish('UNKNOWN', 'home_divinity_return_instance_mismatch')
                    owned = navigation
                    deadline = clock() + timeout
                    wait()
                    continue
                if root not in ('Anecdote', 'Campaign', 'Capital'):
                    return finish('NOT_NEEDED', 'home_navigation_other_activity')
                candidates = []
                loading_lists = []
                advice_candidates = []
                list_present = False
                for name, validator in (('challenge', _challenge_valid), ('elite', _elite_valid)):
                    snap = state.get(name) or {}
                    list_present |= snap.get('phase') == 'CHALLENGE_LIST'
                    navigation = snap.get('home_navigation')
                    if not validator(snap) or not _home_navigation_valid(navigation) or snap.get('character_id') != role:
                        continue
                    gate = 'gate' if root == 'Anecdote' else 'existing_list_gate'
                    if navigation[gate] == 'PASS':
                        candidates.append((name, snap))
                    elif root in ('Campaign', 'Capital') and navigation[gate] == 'WAITING_HOME_LIST':
                        loading_lists.append((name, navigation))
                    elif root == 'Anecdote' and not advice_attempted and _home_loss_advice_ready(snap, name):
                        advice_candidates.append((name, snap))
                if root in ('Campaign', 'Capital') and not list_present:
                    if home_scene_ready(state):
                        return finish('NOT_NEEDED', 'home_navigation_observed_interactive')
                    reason = str(current.get('gate') or '等待当前主场景恢复可交互')
                    if reason != last_wait_reason:
                        publish('home_navigation_waiting', status='WAITING', reason=reason)
                        last_wait_reason = reason
                    if clock() >= deadline:
                        return finish('WAITING', reason)
                    wait()
                    continue
                if not candidates:
                    if advice_candidates:
                        scope, snap = advice_candidates[0]
                        publish('home_loss_advice_intent', scope=scope, before=snap)
                        sequence, reason, status = dismiss_loss_advice(snap)
                        if status:
                            return finish(status, reason)
                        advice_attempted = True
                        pending_advice = (scope, snap['ui_context']['current_root_instance_id'],
                            snap['campaign_id'], snap['revision'], snap['tutorial']['step'], sequence, 'loss_advice', 'Anecdote')
                        deadline = clock() + timeout
                        wait()
                        continue
                    if loading_lists:
                        name, navigation = loading_lists[0]
                        witness = (name, current_ui.get('current_root_instance_id'),
                                   navigation['current_list_instance_id'], navigation['current_list_root_instance_id'])
                        if not all(type(value) is int and value != 0 for value in witness[1:]):
                            return finish('BLOCKED', 'home_navigation_loading_list_identity_unavailable')
                        if waiting_list is not None and witness != waiting_list:
                            return finish('BLOCKED', 'home_navigation_loading_list_replaced')
                        waiting_list = witness
                        if clock() >= deadline:
                            return finish('BLOCKED', 'home_navigation_list_loading_timeout')
                        wait()
                        continue
                    return finish('BLOCKED', 'home_navigation_idle_source_unavailable' if root == 'Anecdote'
                                  else 'home_navigation_existing_list_unavailable')
                # The same non-business list is projected by both views. Its
                # normal-challenge view is sufficient; no stage is selected.
                scope, snap = candidates[0]
                if waiting_list is not None:
                    navigation = snap['home_navigation']
                    witness = (scope, current_ui.get('current_root_instance_id'),
                               navigation['current_list_instance_id'], navigation['current_list_root_instance_id'])
                    if witness != waiting_list:
                        return finish('BLOCKED', 'home_navigation_loading_list_replaced')
                action = 'return_home' if root == 'Anecdote' else 'close_challenge_list'
                source_id = snap['campaign_id'] if action == 'return_home' else 0
                publish('home_navigation_intent', action=action, scope=scope, before=snap)
                navigation, reason, status = submit(action, snap, source_id)
                if status:
                    return finish(status, reason)
                owned = navigation
                closing = action == 'close_challenge_list'
                deadline = clock() + timeout
            else:
                snap = state.get(scope) or {}
                navigation = snap.get('home_navigation')
                if not (_elite_valid(snap) if scope == 'elite' else _challenge_valid(snap)) or not _home_navigation_valid(navigation):
                    return finish('UNKNOWN', 'home_navigation_observation_invalid')
                identity = ('origin', 'return_command_seq', 'character_id', 'source_campaign_id',
                            'source_root_instance_id', 'destination_root_instance_id', 'list_instance_id', 'destination')
                if any(navigation[key] != owned[key] for key in identity):
                    return finish('UNKNOWN', 'home_navigation_ownership_changed')
                if closing and navigation['close_command_seq'] != owned['close_command_seq']:
                    return finish('UNKNOWN', 'home_navigation_close_ownership_changed')
                publish('home_navigation_observation', scope=scope, navigation=navigation,
                        campaign=current, frame=state.get('frame'))
                if navigation['status'] == 'UNKNOWN':
                    return finish('UNKNOWN', navigation['reason'] or 'home_navigation_unknown')
                if navigation['status'] == 'LIST_READY' and not closing:
                    navigation, reason, status = submit('close_home_list', snap, owned['source_campaign_id'])
                    if status:
                        return finish(status, reason)
                    owned = navigation
                    closing = True
                    deadline = clock() + timeout
                elif navigation['status'] == 'READY' and (closing or owned['origin'] == 'DIVINITY'):
                    if (root != owned['destination'] or current_ui.get('current_root_instance_id') != owned['destination_root_instance_id']
                            or current_ui.get('current_root_active') is not True or current_ui.get('global_input_enabled') is not True
                            or current.get('available') is not True or current.get('gate') != 'PASS'
                            or current.get('phase') not in ('MAIN', 'CAMPAIGN')
                            or (state.get('snapshot') or {}).get('main_enabled') is not True):
                        return finish('UNKNOWN', 'home_navigation_destination_not_interactive')
                    return finish('COMPLETED', 'home_navigation_observed_interactive')
            if clock() >= deadline:
                return finish('UNKNOWN', 'home_navigation_timeout_pending_retained')
        except (CampaignAdmissionClosed, BridgeAdmissionClosed):
            # Both exceptions prove publication did not occur. A concurrent
            # pause can resume the same unsubmitted step; unknown I/O cannot.
            if paused() or stopped():
                invocation_uncertain = False
                continue
            return finish('UNKNOWN' if owned or pending_result else 'BLOCKED', 'home_navigation_admission_closed')
        except Exception as exc:
            return finish('UNKNOWN' if owned or pending_result or invocation_uncertain else 'BLOCKED', 'home_navigation_failed: ' + str(exc))
        wait()
