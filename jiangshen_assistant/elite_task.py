"""Type-4 challenges selected from the live list with a fresh run budget.

Current remaining attempts admit play; daily totals stay unknown without a
witnessed reset. Missing server receipts stay distinct from confirmed results.
"""
from __future__ import annotations

from pathlib import Path
import copy
import json
import time
import uuid

from .battle_start_store import BattleStartKey
from .battle_flow import BattleYieldRequested, battle_flow
from .bridge_client import BridgeError, _atomic_json, _elite_valid
from .campaign import CampaignError
from .campaign_task import CampaignCoordinator
from .home_navigation import ensure_home_scene
from .objects import CampaignAdmissionClosed


class EliteCoordinator(CampaignCoordinator):
    TASK_LABEL = '星级挑战'
    RUN_DIRECTORY = 'elite_runs'
    SUPPORT_GROWTH = False
    GENERAL_ACTIONS = frozenset()

    def __init__(self, objects, runner, data_dir: Path, **kwargs):
        super().__init__(objects, runner, data_dir, **kwargs)
        self._selection = None
        self._active_selection = None

    def start(self, *, target=10, campaign_id=None, star=None, expected_character_id=None,
              expected_user_id=None, expected_zone_id=None):
        with self._lock:
            self._refresh_cleanup_locked()
            if self._active or self._closing or self.runner.is_active:
                return False
            automatic = type(campaign_id) is int and campaign_id == 0 and type(star) is int and star == 0
            explicit = (type(campaign_id) is int and 1 <= campaign_id <= 65535
                        and type(star) is int and star in (1, 2, 3))
            if (type(target) is not int or not 1 <= target <= 10 or not (automatic or explicit)
                    or any(value is not None and (type(value) is not str or not value.isascii()
                        or not value.isdecimal() or not 0 < int(value) <= 2**64 - 1)
                        for value in (expected_character_id, expected_user_id, expected_zone_id))):
                self._snapshot = dict(mode='blocked', reason='elite_stage_and_star_required',
                    target=target, campaign_id=campaign_id, star=star, completed=None,
                    remaining=None, settled_in_run=0, pending=None, cleanup_state='not_started')
                self._emit('星级挑战需设置 1 至 10 次目标；关卡和星级均为 0 时自动选择。', 'WARNING')
                return False
            self._selection = dict(target=target, campaign_id=campaign_id, star=star, automatic=automatic,
                expected_character_id=expected_character_id, expected_user_id=expected_user_id,
                expected_zone_id=expected_zone_id)
            self._active_selection = None if automatic else dict(campaign_id=campaign_id, star=star)
            return self._launch_locked()

    def resume(self):
        # Restarting is a new selection, never an implicit replay of
        # an ended/unknown task from its run-output ledger.
        with self._lock:
            if not self._active:
                return False
        return super().resume()

    def _prepare_start_scene(self, token, ctx, observed):
        snap = observed.get('campaign') or {}
        selected = self._active_selection
        # Automatic selection needs a fresh list even on an existing type-4
        # map. Only an explicitly matching stage may retain that map.
        if (snap.get('phase') == 'CHALLENGE_LIST'
                or (snap.get('ui_context') or {}).get('current_root_level') != 'Anecdote'
                or (selected is not None and snap.get('campaign_type') == 4
                    and snap.get('campaign_id') == selected['campaign_id']
                    and snap.get('elite_star') == selected['star'])):
            return observed
        result = self._navigate_home(token, ctx)
        if result.status == 'STOPPED':
            return result.observed or observed
        if result.status not in ('NOT_NEEDED', 'COMPLETED'):
            raise CampaignError(result.reason)
        return result.observed

    def _acquire(self, run_id):
        return self.objects.acquire_campaign(run_id, scope='elite')

    def _navigate_home(self, token, ctx, *, publish=None):
        def navigation_paused():
            with self._lock:
                epoch = self._resume_epoch if self._resume_requested else None
                if epoch is not None and self._resume_current_locked(epoch):
                    if self.objects.resume_campaign(token):
                        self._paused = self._resume_requested = False
                        self._resume_epoch = None
            return self._paused or ctx.is_paused

        return ensure_home_scene(self.objects, token,
            stopped=lambda: self._stopping or ctx.cancelled,
            paused=navigation_paused, wait=self._wait,
            timeout=self._transition_timeout, clock=self._clock, publish=publish)

    @staticmethod
    def _page(snap):
        return (snap['phase'], snap.get('campaign_id'), snap.get('troop_id'),
                snap.get('result_kind'), (snap.get('tutorial') or {}).get('step'),
                (snap.get('ui_context') or {}).get('current_root_instance_id'))

    def _next_action(self, snap, completed_ids, finished_selections=()):
        phase = snap['phase']
        flow = self._flow(snap)
        if flow['status'] == 'WAIT':
            return None, snap
        if snap.get('available') is not True or snap.get('gate') != 'PASS':
            raise CampaignError('elite_gate: ' + str(snap.get('gate')))
        if flow['status'] == 'UNSUPPORTED':
            raise CampaignError('elite_flow: ' + flow['reason'])
        if (snap.get('tutorial') or {}).get('kind') == 'GENERAL':
            raise CampaignError('elite_general_tutorial_unsupported')
        # An old visible result may be dismissed without counting or adopting
        # it as this run's selected stage.
        if (phase == 'RESULT' and snap['attempt']['status'] == 'NONE'
                and snap.get('result_navigation') in ('ack_result', 'next_chapter')):
            return snap['result_navigation'], snap
        selected = self._active_selection
        if phase not in ('MAIN', 'CHALLENGE_LIST') and (selected is None or
                snap['campaign_type'] != 4 or snap['campaign_id'] != selected['campaign_id']
                or snap['elite_star'] != selected['star']):
            raise CampaignError('elite_selected_stage_or_star_changed')
        if flow['status'] == 'ACTION':
            if flow['action'] != 'continue':
                raise CampaignError('elite_guide_action_unsupported')
            return 'continue', snap
        if phase == 'MAIN':
            return 'enter', {**snap, 'campaign_id': 0, 'troop_id': 0}
        if phase == 'CHALLENGE_LIST':
            rows = [row for row in snap['challenge_candidates']
                    if row['campaign_type'] == 4
                    and not row['finished'] and row['unlocked'] and row['gate'] == 'PASS'
                    and (row['campaign_id'], row['star']) not in finished_selections]
            if not self._selection['automatic']:
                rows = [row for row in rows if row['campaign_id'] == selected['campaign_id']
                        and row['star'] == selected['star']]
            if not rows and self._selection['automatic']:
                return 'no_available_elite', snap
            if not rows or not self._selection['automatic'] and len(rows) != 1:
                raise CampaignError('elite_selected_stage_not_current_unlocked_unfinished')
            row = min(rows, key=lambda row: row['display_index'])
            self._active_selection = dict(campaign_id=row['campaign_id'], star=row['star'])
            return 'enter_challenge', {**snap, 'campaign_id': row['campaign_id'], 'elite_star': row['star'], 'troop_id': 0}
        if phase == 'CAMPAIGN':
            deferred = getattr(self, '_handled_unconfirmed_targets', set())
            troops = [row for row in snap['eligible_troops']
                      if (snap['campaign_id'], row['troop_id']) not in deferred]
            if not troops:
                if (snap['eligible_troops'] and deferred and flow['status'] == 'IDLE'
                        and (snap.get('home_navigation') or {}).get('gate') == 'PASS'):
                    return 'return_finished_elite', snap
                if (self._selection['automatic'] and flow['status'] == 'IDLE'
                        and (snap.get('ui_context') or {}).get('current_root_level') == 'Anecdote'
                        and ((snap.get('selection_diagnostics') or {}).get('campaign') or {}).get('finished') is True
                        and (snap.get('home_navigation') or {}).get('gate') == 'PASS'):
                    return 'return_finished_elite', snap
                raise CampaignError('elite_selected_stage_has_no_eligible_troop')
            return 'select', {**snap, 'troop_id': troops[0]['troop_id']}
        if phase == 'TROOP':
            return 'prepare', snap
        if phase == 'FORMATION':
            return 'start', snap
        if phase == 'RESULT':
            attempt = snap['attempt']
            if attempt['status'] == 'NONE' and snap.get('result_navigation') in ('ack_result', 'next_chapter'):
                return snap['result_navigation'], snap
            if attempt['status'] in ('SETTLED', 'CONTINUED') and attempt['attempt_id'] in completed_ids:
                return ('next_chapter' if snap['result_kind'] == 'CHAPTER' else 'ack_result'), snap
            raise CampaignError('elite_unmatched_result')
        raise CampaignError('elite_unowned_battle' if phase == 'BATTLE' else 'elite_unknown_phase')

    @staticmethod
    def _navigation_done(action, before, snap):
        if snap.get('available') is not True or snap.get('gate') != 'PASS':
            return False
        phase = snap['phase']
        if action == 'enter':
            return phase == 'CHALLENGE_LIST'
        if action == 'enter_challenge':
            return (phase not in ('MAIN', 'CHALLENGE_LIST', 'BLOCKED')
                    and snap.get('campaign_id') == before[1]
                    and (snap.get('entry') or {}).get('status') == 'READY')
        if action == 'select':
            return phase in ('TROOP', 'FORMATION', 'SCENARIO')
        if action == 'prepare':
            return phase in ('FORMATION', 'SCENARIO')
        if action == 'next_chapter':
            return phase == 'CHALLENGE_LIST'
        if action == 'ack_result':
            return phase != 'RESULT' or EliteCoordinator._page(snap) != before
        return EliteCoordinator._page(snap) != before

    def _run(self, ctx):
        token = connection_token = subscription = reservation = None
        pending = navigation = None
        completed_ids, reports = set(), set()
        self._handled_unconfirmed_targets = set()
        finished_selections = set()
        bound_cycle = expected_remaining = expected_bought = None
        yield_cleanup_since = None
        run_id = uuid.uuid4().hex
        self._start_intents = self._start_intents.for_run(run_id)
        path = self.data_dir / (run_id + '.json')
        records = []
        state = dict(mode='active', reason='', pending=None, completed=None, remaining=None,
                     settled_in_run=0, started_in_run=0, consumed_in_run=0, successful_in_run=0,
                     receipt_missing_in_run=0, native_rejected_in_run=0, page_confirmed_in_run=0,
                     performed_in_run=0, cycle=None,
                     cleanup_state='pending', ui_cleanup_state='not_needed', run_id=run_id,
                     ledger_path=str(path), **self._selection)
        snap = {}

        def publish(**updates):
            state.update(updates)
            if snap:
                quota = snap.get('elite_quota') or {}
                state.update(phase=snap.get('phase'), revision=snap.get('revision'),
                    completed=quota.get('completed'), remaining=quota.get('remaining'),
                    attempts_left=min(max(0, state['target'] - state['started_in_run']), quota['remaining'])
                        if type(quota.get('remaining')) is int else None,
                    selected=copy.deepcopy(self._active_selection),
                    quota=copy.deepcopy(quota), candidates=copy.deepcopy(snap.get('challenge_candidates', [])))
            with self._lock:
                state['paused'], state['stopping'] = self._paused, self._stopping
                state.update(yield_requested=self._yield_requested, yielded=self._yielded,
                             yield_resume_requested=self._yield_resume_requested,
                             flow=copy.deepcopy(self._battle_flow))
                self._snapshot = copy.deepcopy(state)
            self.data_dir.mkdir(parents=True, exist_ok=True)
            _atomic_json(path, dict(schema=1, state=state, records=records))
            self._emit()

        def record(kind, **values):
            records.append(dict(kind=kind, **values))
            publish()

        try:
            publish()
            deadline = self._clock() + self._connect_timeout
            if self.objects.bridge_state != 'ready':
                connection_token = self.objects.begin_campaign_connection()
                if connection_token is None:
                    raise CampaignError('elite_object_connection_unavailable')
            while self.objects.bridge_state != 'ready' or self.objects.is_busy:
                if self._stopping or ctx.cancelled:
                    state.update(mode='stopped', reason=self._stop_reason or 'task_cancelled')
                    return
                if self._clock() >= deadline:
                    raise CampaignError('elite_object_connection_timeout')
                if self.objects.bridge_state in ('error', 'unknown', 'disconnected') and not self.objects.is_busy:
                    raise CampaignError('elite_object_connection_failed')
                self._wait()
            token = self._acquire(run_id)
            connection_token = None
            context = json.loads(token.context_key)
            for key in ('character_id', 'user_id', 'zone_id'):
                expected = self._selection['expected_' + key]
                if expected is not None and context.get(key) != expected:
                    raise CampaignError('elite_expected_identity_mismatch: ' + key)
            with self._lock:
                self._token = token
                if self._stopping:
                    self.objects.stop_campaign(token, reason=self._stop_reason)
                elif self._paused:
                    self.objects.pause_campaign(token)
            subscription = self.objects.subscribe_workflow(token, notify=self._wake.set)
            observed = self.objects.observe_campaign(token)
            snap = observed['campaign']
            record('initial_observation', context_key=token.context_key, observation=copy.deepcopy(snap))
            prepared = self._prepare_start_scene(token, ctx, observed)
            if self._stopping or ctx.cancelled:
                state.update(mode='stopped', reason=self._stop_reason or 'task_cancelled')
                return
            if prepared is not observed:
                observed = prepared
                snap = observed['campaign']
                record('start_scene_ready', context_key=token.context_key, observation=copy.deepcopy(snap))
            transition_since = self._clock()
            rejected_revision = None
            finish_after_navigation = False
            while True:
                if observed.get('context_key') != token.context_key or not _elite_valid(snap):
                    raise CampaignError('elite_observation_or_context_changed')
                if observed.get('stopping') and not self._stopping:
                    self.stop(reason=observed.get('control_reason') or 'elite_connection_stopped')
                if ctx.cancelled and not self._stopping:
                    self.stop(reason='task_cancelled')
                if ctx.is_paused and not self._paused and not self._stopping:
                    self.pause(reason='task_paused')
                quota, attempt = snap['elite_quota'], snap['attempt']
                if quota['verified']:
                    if bound_cycle is None:
                        bound_cycle = quota['cycle']
                        expected_remaining, expected_bought = quota['remaining'], quota['bought']
                        state['cycle'] = bound_cycle
                    elif quota['cycle'] != bound_cycle:
                        raise CampaignError('elite_daily_cycle_changed')
                else:
                    raise CampaignError(quota['reason'])
                if attempt['status'] == 'UNKNOWN':
                    raise CampaignError('elite_attempt_unknown: ' + attempt['evidence'])
                if pending is not None:
                    if attempt['attempt_id'] != pending['attempt_id']:
                        raise CampaignError('elite_pending_attempt_identity_changed')
                    if attempt['status'] in ('SETTLED', 'CONTINUED'):
                        unconfirmed = attempt['status'] == 'CONTINUED'
                        if (attempt['command_seq'] != pending['command_seq']
                                or attempt['character_id'] != snap['character_id']
                                or pending['stage_key'] != '%s:%s' % (attempt['campaign_id'], attempt['troop_id'])
                                or attempt.get('native_rejection') is not True
                                and attempt['report_id'] != '0' and attempt['report_id'] in reports):
                            raise CampaignError('elite_result_identity_unconfirmed')
                        consumed = pending['remaining_before'] - quota['remaining']
                        if (quota['cycle'] != bound_cycle or quota['bought'] != expected_bought
                                or consumed not in ((0, 1) if unconfirmed else (1,))):
                            raise CampaignError('elite_attempt_count_settlement_mismatch')
                        if unconfirmed:
                            self._start_intents.continue_elite_unconfirmed(reservation, attempt)
                            if attempt.get('native_rejection') is True:
                                state['native_rejected_in_run'] += 1
                                self._emit('游戏已显示加速警告，本场不计胜；按当前次数继续其他可用目标。', 'WARNING')
                            elif attempt.get('page_state_confirmed') is True:
                                state['page_confirmed_in_run'] += 1
                                state['successful_in_run'] += int(attempt.get('page_outcome') == 'WIN')
                                self._emit('本场已由当前关卡进度确认，继续读取下一目标。')
                            else:
                                state['receipt_missing_in_run'] += 1
                                self._emit('本场界面已结束，结算回包未确认；按用户规则已处理并继续。', 'WARNING')
                            if attempt.get('page_state_confirmed') is not True:
                                self._handled_unconfirmed_targets.add((attempt['campaign_id'], attempt['troop_id']))
                        else:
                            result = self._settled_result(run_id, token.context_key, attempt,
                                stage_key=pending['stage_key'], challenge=True)
                            if not result.confirmed or attempt['evidence'] != 'captured_authoritative_receipt_applied':
                                raise CampaignError('elite_settlement_evidence_unconfirmed')
                            self._start_intents.bind_observation(reservation, attempt)
                            self._start_intents.complete(reservation, attempt)
                            state['settled_in_run'] += 1
                            state['successful_in_run'] += int(attempt['outcome'] == 'WIN')
                        reservation = None
                        completed_ids.add(attempt['attempt_id'])
                        if attempt['report_id'] != '0' and attempt.get('native_rejection') is not True:
                            reports.add(attempt['report_id'])
                        expected_remaining = quota['remaining']
                        state['consumed_in_run'] += consumed
                        state['performed_in_run'] += 1
                        record('continued_without_receipt' if unconfirmed else 'settled',
                            attempt=copy.deepcopy(attempt), quota=copy.deepcopy(quota), consumed=consumed)
                        pending = None
                        state['pending'] = None
                        transition_since = self._clock()
                    elif self._clock() - pending['started'] >= self._battle_timeout:
                        raise CampaignError('elite_battle_timeout_pending_retained')
                elif (attempt['status'] not in ('NONE', 'SETTLED', 'CONTINUED')
                      or attempt['status'] in ('SETTLED', 'CONTINUED') and attempt['attempt_id'] not in completed_ids):
                    raise CampaignError('elite_unowned_attempt')
                if quota['bought'] != expected_bought:
                    raise CampaignError('elite_purchase_count_changed_outside_run')
                if pending is None and quota['remaining'] != expected_remaining:
                    raise CampaignError('elite_attempt_count_changed_outside_run')
                yield_cleanup = self._paused and self._yield_requested and not self._runtime_error
                self._battle_flow = battle_flow(observed, pending=pending is not None,
                    reservation=reservation is not None, navigation=navigation is not None, paused=yield_cleanup)
                if yield_cleanup and pending is None:
                    if yield_cleanup_since is None:
                        yield_cleanup_since = self._clock()
                    elif (not self._battle_flow['handoff_ready']
                            and self._clock() - yield_cleanup_since >= self._transition_timeout):
                        raise CampaignError('battle_yield_paused_cleanup_timeout')
                else:
                    yield_cleanup_since = None
                with self._lock:
                    if self._resume_requested and not self._stopping:
                        epoch = self._resume_epoch
                        self._resume_requested = False
                        if self._resume_current_locked(epoch) and quota['verified'] and self.objects.resume_campaign(token):
                            self._paused = False
                            self._resume_epoch = None
                            transition_since = self._clock()
                if self._stopping:
                    publish(mode='stopped', reason=self._stop_reason)
                    if pending is None:
                        break
                    if self._clock() >= self._stop_deadline:
                        raise CampaignError('elite_stop_timeout_pending_retained')
                elif not self._paused or yield_cleanup:
                    target_met = pending is None and state['performed_in_run'] >= state['target']
                    exhausted = pending is None and quota['remaining'] <= 0
                    finished = target_met or exhausted
                    finish_reason = 'elite_run_target_reached' if target_met else 'elite_attempts_exhausted'
                    if navigation is not None:
                        action, before, started = navigation
                        if self._navigation_done(action, before, snap):
                            navigation = None
                            transition_since = self._clock()
                            if finish_after_navigation:
                                publish(mode='complete',
                                    reason=finish_reason if finished else 'elite_selected_stage_finished',
                                    ui_cleanup_state='confirmed')
                                break
                        elif self._clock() - max(started, self._yield_requested_at or started) >= self._transition_timeout:
                            raise CampaignError('elite_navigation_timeout: ' + action)
                    self._battle_flow = battle_flow(observed, pending=pending is not None,
                        reservation=reservation is not None, navigation=navigation is not None, paused=yield_cleanup)
                    if not finished and self._begin_yield(token, subscription, self._battle_flow):
                        yielded_context = token.context_key
                        token = subscription = connection_token = None
                        record('yielded', context_key=yielded_context, observation=copy.deepcopy(snap),
                               flow=copy.deepcopy(self._battle_flow))
                        if not self._wait_yield_resume(ctx):
                            state.update(mode='stopped', reason=self._stop_reason or 'task_cancelled_while_yielded',
                                         cleanup_state='borrowed')
                            break
                        token = self._reacquire_yield(run_id, yielded_context, ctx)
                        if token is None:
                            state.update(mode='stopped', reason=self._stop_reason or 'task_cancelled_while_yielded',
                                         cleanup_state='borrowed')
                            break
                        subscription = self.objects.subscribe_workflow(token, notify=self._wake.set)
                        observed = self.objects.observe_campaign(token)
                        observed = self._prepare_start_scene(token, ctx, observed)
                        if self._stopping or ctx.cancelled:
                            state.update(mode='stopped', reason=self._stop_reason or 'task_cancelled_during_yield_resume')
                            break
                        subscription.drain()
                        observed = self.objects.workflow_snapshot(token)
                        snap = observed['campaign']
                        if observed.get('context_key') != yielded_context:
                            raise CampaignError('battle_yield_context_changed_after_prepare')
                        record('resumed', context_key=yielded_context, observation=copy.deepcopy(snap))
                        transition_since = self._clock()
                        yield_cleanup_since = None
                        rejected_revision = None
                        continue
                    owned_story = (pending is not None and snap.get('pending_story_attempt_id') == pending['attempt_id']
                        and self._flow(snap)['status'] == 'ACTION' and self._flow(snap)['action'] == 'continue')
                    if (pending is None or owned_story) and navigation is None and (rejected_revision is None or snap['revision'] > rejected_revision):
                        if (finished and snap.get('available') is True and snap.get('gate') == 'PASS'
                                and snap['phase'] in ('MAIN', 'CAMPAIGN', 'CHALLENGE_LIST', 'TROOP', 'FORMATION')
                                and self._flow(snap)['status'] == 'IDLE'):
                            publish(mode='complete', reason=finish_reason, ui_cleanup_state='confirmed')
                            break
                        if yield_cleanup and snap.get('phase') not in ('RESULT', 'SCENARIO'):
                            action, planned = None, snap
                        else:
                            action, planned = self._next_action(snap, completed_ids, finished_selections)
                        if yield_cleanup and action and action not in self.YIELD_CLEANUP_ACTIONS:
                            raise CampaignError('battle_yield_paused_cleanup_unsupported: ' + action)
                        if action == 'start' and self._yield_requested:
                            action = None
                        if action == 'return_finished_elite':
                            record('finished_stage_return_intent', observation=copy.deepcopy(planned))
                            result = self._navigate_home(token, ctx, publish=record)
                            if result.status == 'STOPPED':
                                state.update(mode='stopped', reason=self._stop_reason or result.reason)
                                break
                            if result.status != 'COMPLETED':
                                raise CampaignError('elite_finished_stage_return: ' + result.reason)
                            if not self._selection['automatic']:
                                publish(mode='blocked', reason='elite_selected_targets_processed_without_confirmation',
                                        ui_cleanup_state='confirmed')
                                break
                            finished_selections.add((planned['campaign_id'], planned['elite_star']))
                            self._active_selection = None
                            observed = result.observed
                            snap = observed['campaign']
                            record('finished_stage_returned', observation=copy.deepcopy(snap),
                                campaign_id=planned['campaign_id'], star=planned['elite_star'])
                            transition_since = self._clock()
                            rejected_revision = None
                            continue
                        if action == 'no_available_elite':
                            publish(mode='complete', reason='elite_no_current_available_option', ui_cleanup_state='confirmed')
                            break
                        if finished and action not in (None, 'continue', 'ack_result', 'next_chapter'):
                            raise CampaignError('elite_target_reached_cleanup_unconfirmed')
                        if action is None:
                            if self._clock() - transition_since >= self._transition_timeout:
                                raise CampaignError('elite_transition_timeout')
                        else:
                            attempt_id = uuid.uuid4().hex if action == 'start' else ''
                            if action == 'start':
                                key = BattleStartKey.from_observation(token.context_key, planned, 'ELITE_CHALLENGE')
                                reservation = self._start_intents.reserve(key, run_id, token.context_key, attempt_id)
                                state['start_intent_path'] = str(self._start_intents.path_for(key))
                            record('command_intent', action=action, attempt_id=attempt_id,
                                observation=copy.deepcopy(planned))
                            try:
                                state['ui_cleanup_state'] = 'unverified'
                                wire = self._submit_owned(token, action,
                                    expected_revision=planned['revision'], campaign_id=planned['campaign_id'],
                                    troop_id=0 if action in ('enter', 'enter_challenge', 'next_chapter') else planned['troop_id'],
                                    attempt_id=attempt_id,
                                    star=(self._active_selection or {}).get('star', planned.get('elite_star', 0)),
                                    daily_target=state['target'])
                            except BattleYieldRequested:
                                self._start_intents.reject(reservation, 'admission_closed_before_submit',
                                                          reason='battle_yield_requested')
                                reservation = None
                                record('command_not_submitted', action=action, attempt_id=attempt_id,
                                       reason='battle_yield_requested')
                                observed = self.objects.workflow_snapshot(token)
                                snap = observed['campaign']
                                continue
                            except CampaignAdmissionClosed:
                                if reservation is not None:
                                    self._start_intents.reject(reservation, 'admission_closed_before_submit',
                                                              reason='elite_port_admission_closed')
                                    reservation = None
                                self.pause(reason='admission_closed_before_submit')
                                continue
                            if wire.get('cancelled_before_submit') is True:
                                if reservation is not None:
                                    self._start_intents.reject(reservation, 'cancelled_before_submit',
                                                              reason='elite_port_cancelled_before_submit')
                                    reservation = None
                                self.pause(reason='cancelled_before_submit')
                                continue
                            ack = wire.get('ack') or {}
                            receipt = (ack.get('result') or {}).get('elite_command') or {}
                            sequence = wire.get('requested_seq', wire.get('seq'))
                            if (type(sequence) is not int or ack.get('command_seq') != sequence
                                    or receipt.get('command_seq') != sequence or receipt.get('action') != action
                                    or receipt.get('attempt_id') != attempt_id):
                                raise CampaignError('elite_ack_correlation_failed')
                            record('command_receipt', action=action, receipt=copy.deepcopy(receipt))
                            if ack.get('status') == 'REJECTED' and receipt.get('invoked') is False:
                                why = receipt.get('reason') or ack.get('reason') or 'elite_rejected_without_invocation'
                                if reservation is not None:
                                    self._start_intents.reject(reservation, 'rejected_without_invocation',
                                                              command_seq=sequence, reason=why)
                                    reservation = None
                                transient_select = (action == 'select' and why.startswith('normal_troop_selection_gate:')
                                    and why.partition(':')[2] in self.SELECTION_TRANSIENTS)
                                if why != 'campaign_revision_changed' and not transient_select:
                                    raise CampaignError(why)
                                rejected_revision = planned['revision']
                            elif ack.get('status') == 'COMPLETED' and receipt.get('invoked') is True:
                                rejected_revision = None
                                if action == 'start':
                                    self._start_intents.submitted(reservation, sequence)
                                    pending = dict(attempt_id=attempt_id, command_seq=sequence,
                                        stage_key='%s:%s' % (planned['campaign_id'], planned['troop_id']),
                                        remaining_before=quota['remaining'],
                                        started=self._clock())
                                    state['pending'] = copy.deepcopy(pending)
                                    state['started_in_run'] += 1
                                else:
                                    navigation = (action, self._page(planned), self._clock())
                                    finish_after_navigation = action == 'next_chapter' and not state['automatic']
                            else:
                                raise CampaignError('elite_invocation_unconfirmed: ' + action)
                    if rejected_revision is not None and self._clock() - transition_since >= self._transition_timeout:
                        raise CampaignError('elite_revision_replan_timeout')
                    publish(mode='paused' if self._paused else 'active',
                            reason='user_pause' if self._paused else '')
                else:
                    publish(mode='paused', reason='user_pause')
                native_events = subscription.drain()
                if not native_events:
                    now = self._clock()
                    deadlines = []
                    if pending is not None:
                        deadlines.append(pending['started'] + self._battle_timeout)
                    if self._stopping and self._stop_deadline is not None:
                        deadlines.append(self._stop_deadline)
                    elif not self._paused:
                        if navigation is not None:
                            deadlines.append(navigation[2] + self._transition_timeout)
                        if rejected_revision is not None or pending is None and navigation is None:
                            deadlines.append(transition_since + self._transition_timeout)
                    # Events prompt a fresh observation; a bounded heartbeat
                    # still coordinates cancellation and business deadlines.
                    timeout = max(0.0, min(1.0, min(deadlines) - now)) if deadlines else 1.0
                    self._wait(timeout)
                    subscription.drain()
                observed = self.objects.workflow_snapshot(token)
                snap = observed['campaign']
        except Exception as exc:
            state.update(mode='blocked', reason=str(exc))
            try:
                publish()
            except Exception as persistence_error:
                with self._lock:
                    self._snapshot.update(mode='blocked', reason=str(exc), persistence_error=str(persistence_error))
            self._emit(str(exc), 'ERROR')
        finally:
            try:
                if reservation is not None:
                    self._start_intents.mark_unknown(reservation, state.get('reason') or 'elite_result_unconfirmed')
                if subscription is not None:
                    subscription.close()
                if token is not None:
                    self.objects.release_campaign(token, cleanup=True)
                    deadline = time.monotonic() + 6.0
                    while self.objects.is_busy and time.monotonic() < deadline:
                        self._wait()
                    bridge_state = self.objects.bridge_state
                    state.update(bridge_state=bridge_state,
                        cleanup_state='confirmed' if bridge_state == 'disconnected' and not self.objects.is_busy else
                                      'unknown' if bridge_state == 'unknown' else 'pending')
                elif connection_token is not None:
                    self.objects.cancel_campaign_connection(connection_token)
                    state['cleanup_state'] = 'pending' if self.objects.is_busy else 'not_started'
                else:
                    state['cleanup_state'] = 'borrowed' if self._yielded else 'not_started'
            except Exception as exc:
                state.update(cleanup_state='unknown', cleanup_error=str(exc))
            finally:
                with self._lock:
                    self._token = None
                    self._active = self._paused = False
                    self._yield_requested = self._yielded = self._yield_resume_requested = False
                    if self._closing:
                        self._shutdown = True
                try:
                    publish()
                except Exception as exc:
                    with self._lock:
                        self._snapshot.update(persistence_error=str(exc))
                    self._emit(str(exc), 'ERROR')
