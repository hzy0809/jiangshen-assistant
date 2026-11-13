"""On-demand campaign script with durable admission for battle starts.

For a challenge intent_id is also the planned native attempt_id. Preparing the
intent reserves that identity in memory before I/O; run output is best effort.
Each start also requires a durable role/mode/target reservation in this run.
New explicit runs retain earlier evidence without inheriting its admission.
mark_submitted records a correlated ACK,
never an assumption about whether the game received the call.
The domain owns the consecutive-loss streak: confirmed WIN resets it, and the
third consecutive confirmed LOSS closes admission before another challenge.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path
import copy
import json
import math
import os
import tempfile
import threading
import time
import uuid

from .campaign import (Campaign, CampaignError, GuideKind, Mode, Observation, Outcome, Result,
                       APPLIED_LOSS_EVIDENCE, OBSERVED_LOSS_EVIDENCE, result_record_key, valid_settlement_evidence)
from .campaign_store import CampaignStore, encode
from .battle_start_store import BattleStartKey, BattleStartStore
from .battle_flow import BattleYieldRequested, battle_flow
from .objects import CampaignAdmissionClosed
from .home_navigation import ensure_home_scene
from .event_stream import EventMailbox
from .workflow_wait import WakeSignal


@dataclass(frozen=True)
class CampaignEvent:
    kind: str
    message: str = ''
    data: dict = field(default_factory=dict)
    level: str = 'INFO'


@dataclass
class _PendingStoryNavigation:
    attempt_id: str
    observed_revision: int
    observed_state: tuple | None = None
    submitted_revision: int = -1
    submitted_state: tuple | None = None
    changed: bool = False
    rejected: bool = False


class _GrowthOnlyObjects:
    """Restrict the shared workflow to growth, ordinary gear and map entry."""
    DEVELOPMENT_ACTIONS = frozenset((
        'growth_open', 'growth_check', 'growth_claim', 'growth_reward_confirm', 'growth_close',
        'growth_guide_open', 'growth_requirement_close',
        'growth_soul_recruit', 'growth_soul_open', 'growth_soul_start', 'growth_soul_grow', 'growth_soul_harvest',
        'growth_reputation_open', 'growth_artifact_grant', 'growth_artifact_view', 'growth_artifact_open', 'growth_artifact_wear',
        'growth_recruit_open', 'growth_recruit_right', 'growth_recruit_left', 'growth_recruit_choose',
        'growth_recruit_confirm', 'growth_recruit_deploy', 'growth_recruit_save',
        'growth_weapon_open', 'growth_weapon_wear', 'growth_weapon_close',
        'equipment_open', 'reinforce_open', 'enchant_tab', 'equipment_enchant', 'equipment_close'))

    def __init__(self, objects):
        self._objects = objects

    def __getattr__(self, name):
        if name.startswith('submit_'):
            raise CampaignError('growth_only_command_forbidden: ' + name)
        return getattr(self._objects, name)

    def submit_campaign(self, token, action, **fields):
        if (action != 'enter' or fields.get('campaign_id') != 0 or fields.get('troop_id') != 0
                or fields.get('attempt_id') != ''):
            raise CampaignError('growth_only_campaign_action_forbidden: ' + str(action))
        return self._objects.submit_campaign(token, action, **fields)

    def submit_development(self, token, action, **fields):
        if action not in self.DEVELOPMENT_ACTIONS:
            raise CampaignError('growth_only_development_action_forbidden: ' + str(action))
        return self._objects.submit_development(token, action, **fields)


class CampaignCoordinator:
    TASK_LABEL = '征战'
    RUN_DIRECTORY = 'campaign_runs'
    LOSS_MODE = 'consecutive'
    SUPPORT_GROWTH = True
    GENERAL_ACTIONS = frozenset(('general_deploy', 'formation_confirm'))
    YIELD_CLEANUP_ACTIONS = frozenset(('continue', 'ack_result', 'next_chapter'))
    SELECTION_TRANSIENTS = frozenset(('borning', 'born_want', 'scenario_visible', 'sign_activing'))

    def __init__(self, objects, runner, data_dir: Path, *, battle_timeout=180.0,
                 stop_timeout=15.0, connect_timeout=30.0, transition_timeout=30.0, poll_seconds=.1, clock=time.monotonic):
        if any(isinstance(v, bool) or not isinstance(v, (int, float)) or not math.isfinite(v) or v <= 0
               for v in (battle_timeout, stop_timeout, connect_timeout, transition_timeout, poll_seconds)):
            raise ValueError('timeouts must be positive')
        self.objects, self.runner = objects, runner
        self.data_dir = Path(data_dir) / self.RUN_DIRECTORY
        self._start_intents = BattleStartStore(Path(data_dir) / 'battle_start_intents')
        self._start_intent_path = ''
        self.events = EventMailbox({'campaign_runs': 'campaign', 'challenge_runs': 'challenge',
                                    'elite_runs': 'elite'}[self.RUN_DIRECTORY])
        self._clock = clock
        self._battle_timeout, self._stop_timeout = battle_timeout, stop_timeout
        self._connect_timeout = connect_timeout
        self._transition_timeout = transition_timeout
        self._poll_seconds = poll_seconds
        self._lock = threading.RLock()
        self._wake = WakeSignal()
        self._active = self._paused = self._stopping = self._closing = self._shutdown = False
        self._resume_requested = False
        self._yield_requested = self._yielded = self._yield_resume_requested = False
        self._yield_requested_at = None
        self._battle_flow = {}
        self._handoff_path = ''
        self._decision_epoch = 0
        self._resume_epoch = None
        self._runtime_error = ''
        self._stop_reason = ''
        self._token = None
        self._expected_character_id = None
        self._expected_user_id = None
        self._expected_zone_id = None
        self._allow_growth = True
        self._growth_only = False
        self._stop_deadline = None
        self._snapshot = {'mode': 'idle', 'losses': 0, 'reason': '', 'pending': None}
        self._ui_cleanup = None

    @property
    def is_active(self):
        with self._lock:
            return self._active

    @property
    def is_paused(self):
        with self._lock:
            self._refresh_cleanup_locked()
            return self._paused

    @property
    def is_yielded(self):
        with self._lock:
            return self._yielded

    def request_yield(self):
        """Finish the owned battle and navigation, then lend the live session."""
        with self._lock:
            if (not self._active or self._stopping or self._closing
                    or self._growth_only or self._yield_resume_requested):
                return False
            if self._yield_requested:
                return True
            self._yield_requested = True
            self._yield_requested_at = self._clock()
            self._decision_epoch += 1
            self._snapshot.update(yield_requested=True, yielded=self._yielded)
            self._wake.set()
        self._emit()
        return True

    def resume_yield(self):
        """Called by the session owner only after the temporary owner releases."""
        with self._lock:
            if (not self._active or not self._yielded or self._yield_resume_requested
                    or self._stopping or self._closing):
                return False
            self._yield_resume_requested = True
            self._yield_requested = False
            self._snapshot.update(yield_requested=False, yielded=True, yield_resume_requested=True,
                                  paused=self._paused)
            self._decision_epoch += 1
            if not self._paused:
                self.runner.resume()
            self._wake.set()
            self._emit()
            return True

    @property
    def is_shutdown(self):
        with self._lock:
            return self._shutdown

    @property
    def snapshot(self):
        with self._lock:
            self._refresh_cleanup_locked()
            return copy.deepcopy(self._snapshot)

    def _refresh_cleanup_locked(self):
        """Observe a late cleanup completion using only public memory state.

        The stop operation may outlive our bounded worker wait. Keep unresolved
        results intact. A later explicit task reads the current game state.
        """
        if self._active or self._snapshot.get('cleanup_state') != 'pending':
            return
        bridge_state = self.objects.bridge_state
        busy = self.objects.is_busy
        self._snapshot['bridge_state'] = bridge_state
        if bridge_state == 'disconnected' and not busy:
            self._snapshot['cleanup_state'] = 'confirmed'
            self.events.put(CampaignEvent('campaign', data=copy.deepcopy(self._snapshot)))
        elif bridge_state == 'unknown':
            self._snapshot['cleanup_state'] = 'unknown'
            self.events.put(CampaignEvent('campaign', data=copy.deepcopy(self._snapshot)))

    def _emit(self, message='', level='INFO'):
        self.events.put(CampaignEvent('campaign', message, self.snapshot, level))

    def _publish(self, campaign, path, snap=None):
        with self._lock:
            previous = (self._snapshot.get('mode'), self._snapshot.get('reason'))
            development = self._snapshot.get('development')
            self._snapshot = {**encode(campaign.state), 'ledger_path': str(path),
                              'phase': (snap or {}).get('phase', ''),
                              'stage': '%s:%s' % ((snap or {}).get('campaign_id', 0), (snap or {}).get('troop_id', 0)),
                              'growth_only': self._growth_only,
                              'stopping': self._stopping,
                              'yield_requested': self._yield_requested,
                              'yielded': self._yielded,
                              'yield_resume_requested': self._yield_resume_requested,
                              'paused': self._paused,
                              'flow': copy.deepcopy(self._battle_flow)}
            if self._handoff_path:
                self._snapshot['handoff_path'] = self._handoff_path
            if self._yielded:
                self._snapshot['cleanup_state'] = 'borrowed'
            if development:
                self._snapshot['development'] = development
            if self._start_intent_path:
                self._snapshot['start_intent_path'] = self._start_intent_path
            if self._ui_cleanup is not None:
                self._snapshot.update(ui_cleanup_state=self._ui_cleanup['state'],
                    ui_cleanup_reason=self._ui_cleanup['reason'],
                    ui_cleanup_path=self._ui_cleanup['evidence_path'],
                    cleanup_pending=self._ui_cleanup['state'] != 'confirmed',
                    ui_cleanup={'state': self._ui_cleanup['state'],
                        'reason': self._ui_cleanup['reason'],
                        'attempt_id': self._ui_cleanup['attempt'].get('attempt_id'),
                        'report_id': self._ui_cleanup['attempt'].get('report_id'),
                        'last_revision': self._ui_cleanup['last_observed'].get('revision'),
                        'last_phase': self._ui_cleanup['last_observed'].get('phase'),
                        'evidence_path': self._ui_cleanup['evidence_path']})
            if self._runtime_error:
                self._snapshot.update(mode='blocked', reason=self._runtime_error)
            current = (self._snapshot.get('mode'), self._snapshot.get('reason'))
        message = '' if current == previous else '%s状态：%s；原因：%s' % (self.TASK_LABEL, current[0], current[1] or '正常推进')
        self._emit(message)

    def start(self, *, expected_character_id=None, expected_user_id=None, expected_zone_id=None,
              allow_growth=True, growth_only=False):
        for expected in (expected_character_id, expected_user_id, expected_zone_id):
            if expected is not None and (
                    not isinstance(expected, str) or not expected.isascii()
                    or not expected.isdecimal() or not expected.strip('0')):
                return False
        if type(allow_growth) is not bool or type(growth_only) is not bool:
            return False
        if growth_only and (not self.SUPPORT_GROWTH or self.RUN_DIRECTORY != 'campaign_runs' or not allow_growth
                or any(expected is None for expected in (expected_character_id, expected_user_id, expected_zone_id))):
            return False
        with self._lock:
            self._refresh_cleanup_locked()
            if self._active or self._closing or self.runner.is_active:
                return False
            self._growth_only = growth_only
            return self._launch_locked(expected_character_id=expected_character_id,
                                       expected_user_id=expected_user_id, expected_zone_id=expected_zone_id,
                                       allow_growth=allow_growth)

    def _launch_locked(self, *, expected_character_id=None, expected_user_id=None, expected_zone_id=None,
                       allow_growth=True):
        self._active = True
        self._expected_character_id = expected_character_id
        self._expected_user_id = expected_user_id
        self._expected_zone_id = expected_zone_id
        self._allow_growth = allow_growth
        self._paused = self._stopping = False
        self._wake.clear()
        self._resume_requested = False
        self._yield_requested = self._yielded = self._yield_resume_requested = False
        self._yield_requested_at = None
        self._battle_flow = {}
        self._handoff_path = ''
        self._decision_epoch += 1
        self._resume_epoch = None
        self._runtime_error = ''
        self._stop_reason = ''
        self._stop_deadline = None
        self._start_intent_path = ''
        self._ui_cleanup = None
        self._snapshot = {'mode': 'idle', 'losses': 0, 'reason': '', 'pending': None,
                          'growth_only': self._growth_only}
        try:
            accepted = self.runner.start('征战成长补做' if self._growth_only else self.TASK_LABEL + '自动推进',
                                         self._run, checkpoints=False)
        except Exception:
            self._active = False
            raise
        if not accepted:
            self._active = False
            return False
        return True

    def pause(self, reason='user_pause'):
        with self._lock:
            if not self._active or self._stopping:
                return False
            self._decision_epoch += 1
            self._resume_requested = False
            self._resume_epoch = None
            self._paused = True
            drained = True
            if self._token:
                drained = self.objects.pause_campaign(self._token, reason=reason)
                if not drained:
                    self._runtime_error = 'battle_launch_cancel_unverified'
            self.runner.pause()
            self._wake.set()
            return drained

    def resume(self):
        with self._lock:
            self._refresh_cleanup_locked()
            if self._closing or self._stopping:
                return False
            if not self._active:
                return False
            if not self._paused:
                return False
            if self._yielded:
                # User pause is reversible without reclaiming the game from
                # the temporary owner. Only resume_yield authorizes that step.
                self._paused = False
                if self._yield_resume_requested:
                    self.runner.resume()
                self._wake.set()
                return True
            self._decision_epoch += 1
            self._resume_epoch = self._decision_epoch
            self._resume_requested = True
            self.runner.resume()
            self._wake.set()
            return True

    def stop(self, reason='user_stop'):
        with self._lock:
            if not self._active:
                return False
            if not self._stopping:
                self._stop_reason = reason or 'user_stop'
                self._emit('停止' + self.TASK_LABEL + '：' + self._stop_reason, 'WARNING' if self._stop_reason != 'user_stop' else 'INFO')
            self._decision_epoch += 1
            self._resume_requested = False
            self._resume_epoch = None
            self._stopping = True
            self._paused = False
            if self._stop_deadline is None:
                self._stop_deadline = self._clock() + self._stop_timeout
            if self._token:
                drained = self.objects.stop_campaign(self._token, reason=self._stop_reason)
                if not drained:
                    self._runtime_error = 'battle_launch_cancel_unverified'
            else:
                drained = True
            self.runner.stop()
            self._wake.set()
            return drained

    def close(self):
        with self._lock:
            self._closing = True
            if not self._active:
                self._shutdown = True
                return
        self.stop(reason='assistant_shutdown')

    def _wait(self, timeout=None):
        return self._wake.wait(self._poll_seconds if timeout is None else timeout)

    def _resume_current_locked(self, epoch):
        return (epoch == self._decision_epoch and self._active and self._paused and
                not self._stopping and not self._closing)

    def _begin_yield(self, token, subscription, flow):
        """Retire this capability before exposing the parked instance."""
        with self._lock:
            if (not self._yield_requested or self._yielded or self._stopping or self._runtime_error
                    or self._closing or not flow['handoff_ready'] or self.objects.is_busy):
                return False
            if not self.objects.pause_campaign(token, reason='battle_yield'):
                raise CampaignError('battle_yield_launch_drain_unconfirmed')
            if subscription is not None:
                subscription.close()
            self.objects.release_campaign(token, cleanup=False)
            self._token = None
            self._yielded = True
            self._yield_resume_requested = False
            self.runner.pause()
            self._snapshot.update(yield_requested=True, yielded=True, yield_resume_requested=False,
                                  flow=copy.deepcopy(flow),
                                  cleanup_state='borrowed')
            return True

    def _wait_yield_resume(self, ctx):
        # No old token, observation, subscription or command is consulted here.
        # A user pause is independent of the scheduler's ownership handoff.
        while True:
            with self._lock:
                if self._stopping or self._closing or ctx.cancelled:
                    return False
                if self._yield_resume_requested and not self._paused:
                    return True
            self._wait(1.0)

    def _reacquire_yield(self, run_id, context_key, ctx):
        while True:
            with self._lock:
                if self._stopping or self._closing or ctx.cancelled:
                    return None
                if not self._paused:
                    try:
                        token = self._acquire(run_id)
                    except Exception as exc:
                        raise CampaignError('battle_yield_reacquire_failed: ' + str(exc)) from exc
                    if token.context_key != context_key:
                        self.objects.release_campaign(token, cleanup=False)
                        self._yielded = self._yield_resume_requested = False
                        raise CampaignError('battle_yield_context_changed')
                    self._token = token
                    self._yielded = self._yield_resume_requested = False
                    self._snapshot.update(yield_requested=self._yield_requested, yielded=False,
                                          yield_resume_requested=False)
                    return token
            self._wait(1.0)

    def _record_handoff(self, path, kind, *, context_key, snap, **details):
        """Best-effort evidence beside the original run; never start admission."""
        self._handoff_path = str(path.with_suffix('.handoffs.jsonl'))
        record = dict(kind=kind, run_id=path.stem, context_key=context_key,
                      observed_at=time.time(), observation=copy.deepcopy(snap), **details)
        try:
            path.parent.mkdir(parents=True, exist_ok=True)
            with Path(self._handoff_path).open('a', encoding='utf-8') as output:
                output.write(json.dumps(record, ensure_ascii=False, separators=(',', ':')) + '\n')
        except Exception as exc:
            self._emit('战斗交接记录无法写入：' + str(exc), 'WARNING')

    def _acquire(self, run_id):
        return self.objects.acquire_campaign(run_id)

    def _prepare_start_scene(self, token, ctx, observed):
        if self._growth_only:
            # Do not continue a story, battle guide or old battle to reach the
            # map. This one-off task admits only the currently idle home scene.
            snap = observed.get('campaign') or {}
            if (snap.get('phase') not in ('MAIN', 'CAMPAIGN') or self._flow(snap)['status'] != 'IDLE'
                    or snap.get('available') is not True or snap.get('gate') != 'PASS'
                    or (snap.get('attempt') or {}).get('status') not in ('NONE', 'SETTLED')):
                raise CampaignError('growth_only_idle_map_required')
            return observed
        # Challenge tasks retain their own current map, battle and selector.
        # The shared preflight still closes the exact native battle guide before
        # retaining that activity or returning a different challenge map home.
        own_type = {'challenge': 2, 'elite': 4}.get(token.scope)
        def navigation_paused():
            # Bootstrap precedes the Campaign domain's resume loop. A user
            # resume may reopen only this task's still-current admission;
            # each navigation step will then read and gate its own fresh UI.
            with self._lock:
                epoch = self._resume_epoch if self._resume_requested else None
                if epoch is not None and self._resume_current_locked(epoch):
                    if self.objects.resume_campaign(token):
                        self._paused = self._resume_requested = False
                        self._resume_epoch = None
            return self._paused or ctx.is_paused

        result = ensure_home_scene(self.objects, token,
            stopped=lambda: self._stopping or ctx.cancelled,
            paused=navigation_paused, wait=self._wait,
            timeout=self._transition_timeout, clock=self._clock,
            retain_current_activity=own_type is not None)
        if result.status == 'STOPPED':
            with self._lock:
                self._snapshot.update(mode='stopped', reason=result.reason)
            return result.observed or observed
        if result.status not in ('NOT_NEEDED', 'COMPLETED'):
            raise CampaignError(result.reason)
        return result.observed

    def _loss_message(self, losses):
        return '本场战斗失败，连续失败 %s/3。' % losses

    def _start_key(self, campaign, snap):
        if self._growth_only:
            raise CampaignError('growth_only_battle_intent_forbidden')
        mode = {'campaign_runs': 'MAINLINE', 'challenge_runs': 'NORMAL_CHALLENGE'}.get(self.RUN_DIRECTORY)
        if mode is None:
            raise CampaignError('battle_start_mode_unsupported')
        return BattleStartKey.from_observation(campaign.state.context_key, snap, mode)

    def _rejected_action(self, campaign, reason):
        campaign.pause(reason)
        self.pause(reason=campaign.state.reason or 'workflow_pause')

    def _unpending_error(self, campaign, reason):
        campaign.pause(reason)

    def _navigation_wait(self, campaign, snap):
        return False

    @staticmethod
    def _flow(snap):
        flow = snap['flow']
        tutorial = snap.get('tutorial') or {}
        if (snap.get('phase') == 'SCENARIO' and snap.get('available') is True
                and snap.get('gate') == 'PASS' and tutorial.get('kind') == 'SCENARIO'):
            gate = tutorial.get('gate', '')
            if gate.startswith('WAITING_'):
                return {'status': 'WAIT', 'action': '', 'reason': gate}
            if gate == 'PASS' and flow['status'] != 'WAIT':
                # ScenarioUI owns the current skip/next branch. Its live gate
                # supplies readiness without a catalogue of scenario steps.
                return {'status': 'ACTION', 'action': 'continue', 'reason': ''}
        return flow

    def _observation(self, campaign, snap):
        tutorial = snap.get('tutorial') or {}
        flow = self._flow(snap)
        kind = tutorial.get('kind', 'UNKNOWN')
        guide = GuideKind.NONE
        action_key = ''
        if kind in ('SCENARIO', 'GENERAL'):
            # Native owns readiness and the local continuation action. Keep
            # the step identity while waiting so a pending guide cannot be
            # mistaken for a newly completed or unknown step.
            if flow['status'] in ('ACTION', 'WAIT'):
                guide, action_key = GuideKind.REVIEWED, flow['action']
            else:
                guide = GuideKind.UNKNOWN
        elif kind not in ('NONE', 'HINT'):
            guide = GuideKind.UNKNOWN
        elif kind == 'HINT':
            guide = GuideKind.HINT
        return Observation(campaign.state.run_id, campaign.state.context_key, snap['revision'],
                           ('%s:%s' % (snap.get('campaign_id', 0), snap.get('troop_id', 0)),),
                           guide, tutorial.get('step', ''), action_key)

    def _submit(self, token, snap, action, attempt_id=''):
        if self._growth_only and (action != 'enter' or attempt_id):
            raise CampaignError('growth_only_campaign_action_forbidden: ' + str(action))
        settled = snap.get('attempt') or {}
        loss_advice = (action == 'continue' and
                       str((snap.get('tutorial') or {}).get('step', '')).startswith(('loss-advice:', 'battle-guide:')))
        fields = dict(expected_revision=snap['revision'],
            campaign_id=0 if action == 'enter' or action in self.GENERAL_ACTIONS else snap.get('campaign_id', 0),
            troop_id=((settled.get('troop_id', 0) or snap.get('troop_id', 0)) if loss_advice else
                      0 if action in ('enter', 'continue', 'next_chapter') or action in self.GENERAL_ACTIONS else
                      (settled.get('troop_id', 0) or snap.get('troop_id', 0)) if action == 'ack_result' else snap.get('troop_id', 0)),
            attempt_id=attempt_id)
        return self._submit_owned(token, action, **fields)

    def _submit_owned(self, token, action, **fields):
        if action == 'start' or self._yield_requested:
            # Yield and native start share one linearization point. Preparing
            # a durable intent cannot overtake an already requested handoff.
            with self._lock:
                if self._yield_requested:
                    if action == 'start':
                        raise BattleYieldRequested()
                    if self._paused:
                        if action not in self.YIELD_CLEANUP_ACTIONS or self._runtime_error:
                            raise CampaignAdmissionClosed('paused_yield_cleanup_only')
                        # Open only for this run's existing result/story
                        # cleanup. User pause remains set, and the same lock
                        # still excludes every new battle admission.
                        if not self.objects.resume_campaign(token):
                            raise CampaignAdmissionClosed('paused_yield_cleanup_admission_closed')
                return self.objects.submit_campaign(token, action, **fields)
        return self.objects.submit_campaign(token, action, **fields)

    @staticmethod
    def _game_transition(snap):
        """Only the current native flow may authorize a bounded input-free wait."""
        return CampaignCoordinator._flow(snap)['status'] == 'WAIT'

    @staticmethod
    def _receipt(wire, action, attempt_id):
        receipt = wire.get('ack', {}).get('result', {}).get('campaign_command') or {}
        seq = wire.get('requested_seq', wire.get('seq'))
        if (type(seq) is not int or wire.get('ack', {}).get('command_seq') != seq or receipt.get('command_seq') != seq or
                receipt.get('action') != action or receipt.get('attempt_id') != attempt_id):
            raise CampaignError('campaign_ack_correlation_failed')
        return receipt

    @staticmethod
    def _settled_result(run_id, context_key, attempt, *, stage_key=None, challenge=False):
        attempt_id = attempt.get('attempt_id')
        report_id = attempt.get('report_id')
        outcome = attempt.get('outcome')
        applied_reportless_loss = (report_id == '0' and outcome == 'LOSS'
                                   and attempt.get('evidence') == APPLIED_LOSS_EVIDENCE)
        positive_integer = lambda value: type(value) is int and value > 0
        valid = (attempt.get('status') == 'SETTLED' and
                 type(attempt_id) is str and len(attempt_id) == 32 and
                 all(c in '0123456789abcdef' for c in attempt_id) and
                 type(report_id) is str and bool(report_id) and
                 all('0' <= c <= '9' for c in report_id) and
                 (applied_reportless_loss or int(report_id) > 0 and attempt.get('evidence') != APPLIED_LOSS_EVIDENCE) and
                 positive_integer(attempt.get('command_seq')) and
                 positive_integer(attempt.get('campaign_id')) and positive_integer(attempt.get('troop_id')) and
                 outcome in ('WIN', 'LOSS') and
                 attempt.get('evidence') != OBSERVED_LOSS_EVIDENCE and
                 valid_settlement_evidence(outcome, attempt.get('evidence'), challenge=challenge))
        if stage_key is not None:
            valid = valid and stage_key == '%s:%s' % (attempt.get('campaign_id'), attempt.get('troop_id'))
        return Result(run_id, context_key, attempt_id if type(attempt_id) is str else '',
                      result_record_key(attempt) if type(report_id) is str else '',
                      Outcome(outcome.lower()) if outcome in ('WIN', 'LOSS') else Outcome.UNKNOWN, bool(valid))

    def _plan_action(self, campaign, snap, observation):
        waiting = False
        attempt = snap.get('attempt') or {}
        phase = snap.get('phase')
        action = None
        flow = self._flow(snap)
        if flow['status'] == 'WAIT':
            return None, snap, True
        if flow['status'] == 'UNSUPPORTED':
            campaign.stop('campaign_gate: ' + flow['reason'])
            self.stop(reason=campaign.state.reason)
        elif observation.guide == GuideKind.UNKNOWN:
            campaign.stop('unknown_guide')
            self.stop(reason=campaign.state.reason)
        elif observation.guide == GuideKind.REVIEWED:
            action = flow['action']
        elif phase == 'MAIN':
            action = 'enter'
        elif phase == 'CAMPAIGN':
            troops = [t for t in snap.get('eligible_troops', []) if t.get('canattack') is True and t.get('visible') is True and t.get('killed') is False]
            if troops:
                snap = {**snap, 'troop_id': troops[0]['troop_id']}
                action = 'select'
            else:
                # The final troop disappears before the normal
                # chapter/reward panel is published. Wait for
                # that event instead of permanently pausing in
                # the short gap between these two game states.
                waiting = True
        elif phase == 'TROOP':
            action = 'prepare'
        elif phase == 'FORMATION':
            action = 'start'
        elif phase == 'RESULT':
            fresh_navigation = snap.get('result_navigation', '')
            if (attempt.get('status') == 'NONE' and
                    fresh_navigation in ('ack_result', 'next_chapter')):
                # A normal free page close supplies no battle
                # outcome and does not change this run's losses.
                action = fresh_navigation
            elif attempt.get('status') == 'SETTLED' and any(c.attempt_id == attempt.get('attempt_id') and c.report_id == result_record_key(attempt) for c in campaign.state.consumed):
                action = 'next_chapter' if snap.get('result_kind') == 'CHAPTER' else 'ack_result'
            else:
                campaign.stop('unmatched_result')
                self.stop(reason=campaign.state.reason)
        elif phase == 'BATTLE':
            campaign.stop('unowned_battle')
            self.stop(reason=campaign.state.reason)
        else:
            campaign.stop('unknown_campaign_phase')
            self.stop(reason=campaign.state.reason)
        return action, snap, waiting

    def _continue_pending_story(self, campaign, token, snap, navigation):
        """Navigate only the native story still bound to this exact start.

        This is part of the existing battle, never a second domain intent.
        Native owns the callback attestation and rechecks it in the GUI. A
        command ACK proves invocation only; another continuation also needs a
        newer, independently changed story observation (or proven rejection).
        """
        pending = campaign.state.pending
        if pending is None or pending.kind != 'challenge' or snap.get('phase') != 'SCENARIO':
            return navigation
        attempt = snap.get('attempt') or {}
        revision = snap.get('revision')
        if (pending.uncertain or not pending.attempt_id or pending.attempt_id != pending.intent_id
                or snap.get('pending_story_attempt_id') != pending.attempt_id
                or attempt.get('attempt_id') != pending.attempt_id
                or attempt.get('status') not in ('SUBMITTED', 'WAITING')
                or '%s:%s' % (attempt.get('campaign_id'), attempt.get('troop_id')) != pending.stage_key
                or (snap.get('campaign_id'), snap.get('troop_id')) !=
                   (attempt.get('campaign_id'), attempt.get('troop_id'))
                or not snap.get('character_id')
                or attempt.get('character_id') != snap.get('character_id')):
            raise CampaignError('pending_story_identity_mismatch')
        if type(revision) is not int or revision <= pending.revision:
            raise CampaignError('pending_story_revision_invalid')
        tutorial = snap.get('tutorial') or {}
        flow = snap['flow']
        continuation = snap.get('pending_story_continuation')
        if tutorial.get('kind') != 'SCENARIO' or not tutorial.get('step'):
            raise CampaignError('pending_story_unreviewed')
        if continuation not in ('', 'intro', 'skip', 'next'):
            raise CampaignError('pending_story_continuation_unknown')
        state = (tutorial.get('kind'), tutorial.get('step'), tutorial.get('gate'),
                 flow['status'], flow['action'], flow['reason'], continuation)
        if navigation is None or navigation.attempt_id != pending.attempt_id:
            navigation = _PendingStoryNavigation(pending.attempt_id, pending.revision)
        if (revision < navigation.observed_revision or
                revision == navigation.observed_revision and state != navigation.observed_state):
            raise CampaignError('pending_story_revision_invalid')
        navigation.observed_revision, navigation.observed_state = revision, state
        if navigation.submitted_state is not None and state != navigation.submitted_state:
            navigation.changed = True
        if flow['status'] == 'WAIT':
            return navigation
        if (snap.get('available') is not True or snap.get('gate') != 'PASS'
                or tutorial.get('gate') != 'PASS' or flow['status'] != 'ACTION'
                or flow['action'] != 'continue' or not continuation):
            raise CampaignError('pending_story_gate_closed')
        if (revision <= navigation.submitted_revision or
                navigation.submitted_state is not None and not navigation.changed and not navigation.rejected):
            return navigation
        with self._lock:
            cleanup = self._paused and self._yield_requested and not self._runtime_error
            if (not self._active or self._paused and not cleanup or self._stopping or self._closing
                    or campaign.state.mode not in ((Mode.ACTIVE, Mode.PAUSED) if cleanup else (Mode.ACTIVE,))):
                return navigation
        # Reserve before I/O. Ambiguous delivery must never authorize a replay.
        navigation.submitted_revision, navigation.submitted_state = revision, state
        navigation.changed = navigation.rejected = False
        try:
            wire = self._submit(token, snap, 'continue')
        except CampaignAdmissionClosed:
            campaign.pause('admission_closed_before_submit')
            self.pause(reason=campaign.state.reason)
            return navigation
        if wire.get('cancelled_before_submit') is True:
            campaign.pause('cancelled_before_submit')
            self.pause(reason=campaign.state.reason)
            return navigation
        receipt = self._receipt(wire, 'continue', '')
        status = wire.get('ack', {}).get('status')
        if status == 'REJECTED' and receipt.get('invoked') is False:
            reason = receipt.get('reason')
            if type(reason) is not str or not reason:
                reason = 'bridge_rejected_without_invocation'
            if reason == 'campaign_revision_changed':
                # Only this correlated non-invocation permits retry from a
                # newer revision without a scenario transition. Keep the
                # original battle timeout and intent reservation untouched.
                navigation.rejected = True
            else:
                self._rejected_action(campaign, reason)
            self._emit('continue 未调用：' + reason, 'WARNING')
        elif status != 'COMPLETED' or receipt.get('invoked') is not True:
            raise CampaignError('campaign_invocation_unconfirmed: continue')
        return navigation

    def _persist_ui_cleanup(self, campaign, path, snap):
        """Keep physical UI completion separate from bridge disconnection."""
        record = self._ui_cleanup
        destination = Path(record['evidence_path'])
        temporary = None
        try:
            with tempfile.NamedTemporaryFile(mode='w', encoding='utf-8', dir=destination.parent,
                    prefix=destination.name + '.', suffix='.tmp', delete=False) as output:
                temporary = output.name
                json.dump(record, output, ensure_ascii=False, separators=(',', ':'))
                output.write('\n')
                output.flush()
                os.fsync(output.fileno())
            os.replace(temporary, destination)
            temporary = None
        finally:
            if temporary is not None:
                os.unlink(temporary)
            self._publish(campaign, path, snap)

    def _loss_limit_scene_restored(self):
        """Subclass result-navigation reservations may now be retired."""

    @staticmethod
    def _loss_limit_cleanup_record(campaign, token, path, snap, *, state='pending', reason=''):
        snap = snap or {}
        return {'version': 1, 'state': state, 'reason': reason,
            'run_id': campaign.state.run_id, 'context_key': campaign.state.context_key,
            'scope': token.scope, 'losses': campaign.state.losses,
            'evidence_path': str(path.with_suffix('.ui_cleanup.json')),
            'attempt': copy.deepcopy(snap.get('attempt') or {}),
            'before': copy.deepcopy(snap), 'last_observed': copy.deepcopy(snap), 'commands': []}

    def _cleanup_loss_limit(self, campaign, token, ctx, path, snap):
        """Finish only the final owned loss UI, without reopening battle policy.

        The domain stays LOSS_LIMIT throughout. Neither callback invocation nor
        a bridge shutdown proves that battle_exit restored the physical parent.
        Fresh observations must attest the same role, attempt and scene before
        the shared task loop can release its original token.
        """
        anchor = copy.deepcopy(snap.get('attempt') or {})
        ui = snap.get('ui_context') or {}
        root = (ui.get('current_root_instance_id'), ui.get('current_root_level'))
        self._ui_cleanup = self._loss_limit_cleanup_record(campaign, token, path, snap)
        deadline = self._clock() + self._transition_timeout
        submitted = set()
        rejected_revision = None
        observed_revision = snap.get('revision', -1)
        try:
            self._persist_ui_cleanup(campaign, path, snap)
            last = campaign.state.consumed[-1] if campaign.state.consumed else None
            result = self._settled_result(campaign.state.run_id, campaign.state.context_key, anchor)
            if (campaign.state.mode != Mode.LOSS_LIMIT or campaign.state.losses != 3
                    or campaign.state.pending is not None or token.run_id != campaign.state.run_id
                    or token.context_key != campaign.state.context_key
                    or token.scope not in ('campaign', 'challenge')
                    or root[0] in (None, 0)
                    or root[1] != ('Anecdote' if token.scope == 'challenge' else 'Campaign')
                    or not result.confirmed or result.outcome != Outcome.LOSS or last is None
                    or (last.attempt_id, last.report_id, last.outcome) !=
                       (result.attempt_id, result.report_id, Outcome.LOSS)):
                raise CampaignError('loss_limit_ui_identity_unconfirmed')
            role = json.loads(campaign.state.context_key)['character_id']
            identity_fields = ('attempt_id', 'character_id', 'command_seq', 'campaign_id',
                               'troop_id', 'report_id', 'outcome', 'evidence', 'status')
            while self._clock() < deadline:
                with self._lock:
                    if self._stopping or self._closing or ctx.cancelled:
                        raise CampaignError('loss_limit_ui_cleanup_interrupted')
                    # A user resume reopens only this cleanup port. It must
                    # never resume the LOSS_LIMIT domain or create an intent.
                    epoch = self._resume_epoch if self._resume_requested else None
                    if epoch is not None and self._resume_current_locked(epoch):
                        if self.objects.resume_campaign(token):
                            self._paused = self._resume_requested = False
                            self._resume_epoch = None
                    paused = (self._paused or ctx.is_paused) and not self._yield_requested
                if paused:
                    paused_at = self._clock()
                    self._wait()
                    deadline += max(0.0, self._clock() - paused_at)
                    continue
                observed = self.objects.observe_campaign(token,
                    timeout=min(3.0, max(.01, deadline - self._clock())))
                snap = observed['campaign']
                self._ui_cleanup['last_observed'] = copy.deepcopy(snap)
                if observed.get('stopping') or observed.get('context_key') != campaign.state.context_key:
                    raise CampaignError('loss_limit_ui_context_changed')
                attempt, ui = snap.get('attempt') or {}, snap.get('ui_context') or {}
                if (snap.get('character_id') != role or anchor.get('character_id') != role
                        or any(attempt.get(field) != anchor.get(field) for field in identity_fields)
                        or (ui.get('current_root_instance_id'), ui.get('current_root_level')) != root
                        or snap.get('campaign_id') != anchor.get('campaign_id')
                        or snap.get('campaign_type') != (2 if token.scope == 'challenge' else 0)
                        or snap.get('mode') != ('NORMAL_CHALLENGE' if token.scope == 'challenge' else 'MAINLINE')
                        or type(snap.get('revision')) is not int or snap['revision'] < observed_revision
                        or ui.get('selection_gate') != 'PASS'):
                    raise CampaignError('loss_limit_ui_identity_changed')
                observed_revision = snap['revision']
                flow, tutorial = self._flow(snap), snap.get('tutorial') or {}
                counts = ui.get('counts') or {}
                phase = snap.get('phase')
                active = (ui.get('current_root_active') is True
                          and ui.get('global_input_enabled') is True)
                clear_roles = ('battle', 'normal_result', 'reward', 'chapter', 'troop',
                               'formation', 'scenario', 'function', 'notification', 'battle_guide')
                parent = [candidate for candidate in ui.get('campaign_candidates', [])
                          if candidate.get('owner_matches_current') is True
                          and candidate.get('hierarchy_matches_current') is True
                          and candidate.get('owner_root_level') == root[1]
                          and candidate.get('visible') is True]
                if (phase == 'CAMPAIGN' and active and snap.get('available') is True
                        and snap.get('gate') == 'PASS' and flow['status'] == 'IDLE'
                        and tutorial.get('kind') == 'NONE' and snap.get('result_kind') == ''
                        and all((counts.get(name) or {}).get('matching') == 0 for name in clear_roles)
                        and (counts.get('campaign') or {}).get('matching') == 1
                        and len(parent) == 1 and ui.get('candidates_truncated') is False
                        and (snap.get('entry') or {}).get('status') not in ('WAITING', 'UNKNOWN')):
                    with self._lock:
                        if ((self._paused or ctx.is_paused) and not self._yield_requested
                                or self._stopping or self._closing or ctx.cancelled):
                            raise CampaignError('loss_limit_ui_cleanup_interrupted')
                    self._ui_cleanup.update(state='confirmed', reason='owned_loss_scene_restored')
                    self._persist_ui_cleanup(campaign, path, snap)
                    self._loss_limit_scene_restored()
                    return snap
                action = None
                if phase == 'RESULT':
                    if (snap.get('result_kind') != 'NORMAL'
                            or snap.get('troop_id') != anchor.get('troop_id')
                            or (counts.get('normal_result') or {}).get('matching') != 1):
                        raise CampaignError('loss_limit_ui_result_changed')
                    if snap.get('available') is True and snap.get('gate') == 'PASS' and flow['status'] != 'WAIT':
                        action = 'ack_result'
                elif phase == 'SCENARIO':
                    if (tutorial.get('kind') != 'SCENARIO'
                            or tutorial.get('step') != 'loss-advice:' + anchor['attempt_id']
                            or snap.get('troop_id') != anchor.get('troop_id')):
                        raise CampaignError('loss_limit_ui_unowned_guide')
                    # Stock advice may disable the underlying global input;
                    # its native PASS gate attests this exact local close.
                    if (ui.get('current_root_active') is True
                            and snap.get('available') is True and snap.get('gate') == 'PASS'
                            and tutorial.get('gate') == 'PASS' and flow['status'] == 'ACTION'
                            and flow['action'] == 'continue'):
                        action = 'continue'
                elif phase not in ('BATTLE', 'CAMPAIGN'):
                    raise CampaignError('loss_limit_ui_destination_changed')
                if (snap.get('gate') != 'PASS' or flow['status'] == 'UNSUPPORTED') and flow['status'] != 'WAIT':
                    raise CampaignError('loss_limit_ui_gate_closed: ' + str(snap.get('gate')))
                # One invocation per physical result/advice page. A changing
                # revision does not authorize replay of a completed callback.
                if action and action not in submitted and (rejected_revision is None or snap['revision'] > rejected_revision):
                    with self._lock:
                        if ((self._paused or ctx.is_paused) and not self._yield_requested
                                or self._stopping or self._closing or ctx.cancelled):
                            raise CampaignError('loss_limit_ui_cleanup_interrupted')
                    command = {'action': action, 'revision': snap['revision'], 'state': 'reserved'}
                    self._ui_cleanup['commands'].append(command)
                    submitted.add(action)
                    self._persist_ui_cleanup(campaign, path, snap)
                    wire = self._submit(token, snap, action)
                    command['wire'] = copy.deepcopy(wire)
                    receipt = self._receipt(wire, action, '')
                    status = (wire.get('ack') or {}).get('status')
                    if wire.get('cancelled_before_submit') is True:
                        raise CampaignError('loss_limit_ui_cancelled_before_submit')
                    if status == 'REJECTED' and receipt.get('invoked') is False and receipt.get('reason') == 'campaign_revision_changed':
                        command['state'] = 'rejected_without_invocation'
                        submitted.remove(action)
                        rejected_revision = snap['revision']
                    elif status == 'COMPLETED' and receipt.get('invoked') is True:
                        command['state'] = 'invoked'
                        rejected_revision = None
                    else:
                        raise CampaignError('loss_limit_ui_invocation_unconfirmed: ' + action)
                    self._persist_ui_cleanup(campaign, path, snap)
                self._wait()
            raise CampaignError('loss_limit_ui_restore_timeout')
        except Exception as exc:
            self._ui_cleanup.update(state='unknown', reason=str(exc))
            try:
                self._persist_ui_cleanup(campaign, path, snap)
            except Exception as persistence_error:
                self._ui_cleanup['evidence_error'] = str(persistence_error)
                with self._lock:
                    self._snapshot.update(ui_cleanup_state='unknown', cleanup_pending=True)
                self._publish(campaign, path, snap)
            self._emit('三败后的界面清理未确认：' + str(exc), 'ERROR')
            return snap

    def _run(self, ctx):
        expected_identity = (('character', self._expected_character_id),
                             ('user', self._expected_user_id), ('zone', self._expected_zone_id))
        allow_growth = self._allow_growth
        growth_only = self._growth_only
        growth_objects = _GrowthOnlyObjects(self.objects) if growth_only else self.objects
        campaign = token = path = subscription = None
        start_reservation = None
        connection_token = None
        snap = None
        pending_since = None
        transition_since = None
        selection_since = None
        last_navigation = None
        rejected_navigation = None
        pending_story = None
        campaign_growth_tasks = set()
        growth_cleanup_pending = False
        growth_only_steps = 0
        stale_replan = None
        # New revisions alone do not prove progress. One budget spans all
        # consecutive revision rejections until a command actually completes.
        revision_replan_since = None
        yield_cleanup_since = None
        try:
            deadline = self._clock() + self._connect_timeout
            if self.objects.bridge_state != 'ready':
                connection_token = self.objects.begin_campaign_connection()
                if connection_token is None:
                    with self._lock:
                        self._snapshot['connection'] = getattr(self.objects, 'connection_status', {})
                    raise CampaignError('object_connection_unavailable')
            while self.objects.bridge_state != 'ready' or self.objects.is_busy:
                if self._stopping or ctx.cancelled:
                    return
                if self.objects.bridge_state in ('error', 'unknown', 'disconnected') and not self.objects.is_busy:
                    status = getattr(self.objects, 'connection_status', {})
                    with self._lock:
                        self._snapshot['connection'] = status
                    raise CampaignError('object_connection_failed: ' + str(status.get('error') or status.get('message', self.objects.bridge_state)))
                if self._clock() >= deadline:
                    with self._lock:
                        self._snapshot['connection'] = getattr(self.objects, 'connection_status', {})
                    raise CampaignError('object_connection_timeout')
                self._wait()
            run_id = uuid.uuid4().hex
            self._start_intents = self._start_intents.for_run(run_id)
            token = self._acquire(run_id)
            # The connection attempt was claimed. This local capability must
            # never survive a later release and cancel a different owner.
            connection_token = None
            for field, expected in expected_identity:
                if expected is None:
                    continue
                try:
                    actual = json.loads(token.context_key)[field + '_id']
                except (AttributeError, KeyError, TypeError, ValueError) as exc:
                    raise CampaignError('campaign_expected_' + field + '_context_invalid') from exc
                if actual != expected:
                    raise CampaignError('campaign_expected_' + field + '_mismatch')
            with self._lock:
                self._token = token
                if self._stopping:
                    self.objects.stop_campaign(token, reason=self._stop_reason)
                elif self._paused:
                    self.objects.pause_campaign(token, reason='user_pause')
            if hasattr(self.objects, 'subscribe_workflow') and hasattr(self.objects, 'workflow_snapshot'):
                # Subscribe before the fresh admission observation so changes
                # during bootstrap are retained in the event queue.
                subscription = self.objects.subscribe_workflow(token, notify=self._wake.set)
            observed = self.objects.observe_campaign(token)
            observed = self._prepare_start_scene(token, ctx, observed)
            if self._stopping or ctx.cancelled:
                return
            snap = observed['campaign']
            # New tasks begin at the current page. Run ledgers remain output;
            # this run's separate start-intent store controls start admission.
            path = self.data_dir / (run_id + '.json')
            campaign = Campaign.create(CampaignStore(path), run_id, observed['context_key'],
                loss_mode=self.LOSS_MODE,
                on_record_warning=lambda message: self._emit(message, 'WARNING'))
            if not self._paused and not self._stopping:
                try:
                    campaign.resume(self._observation(campaign, snap))
                except CampaignError:
                    if campaign.state.mode != Mode.PAUSED:
                        raise
                    campaign.stop(campaign.state.reason or 'current_page_unknown')
                    self.stop(reason=campaign.state.reason)
            decision_ready = True
            with self._lock:
                observed_epoch = self._decision_epoch
            if subscription is not None:
                subscription.drain()
                observed = self.objects.workflow_snapshot(token)
                snap = observed['campaign']
            while True:
                if observed.get('context_key') != campaign.state.context_key:
                    raise CampaignError('campaign_context_changed')
                if observed.get('stopping') and not self._stopping:
                    connection = observed.get('connection') or {}
                    self.stop(reason=connection.get('stop_reason') or connection.get('error') or
                              observed.get('control_reason') or 'object_connection_stopped')
                if ctx.cancelled and not self._stopping:
                    self.stop(reason='task_cancelled')
                if ctx.is_paused and not self._paused and not self._stopping:
                    self.pause(reason='task_paused')
                if self._stopping:
                    if campaign.state.mode not in (Mode.STOPPED, Mode.LOSS_LIMIT):
                        campaign.stop(self._stop_reason)
                elif self._paused and campaign.state.mode == Mode.ACTIVE:
                    campaign.pause()
                yield_cleanup = self._paused and self._yield_requested and not self._runtime_error
                p = campaign.state.pending
                attempt = snap.get('attempt') or {}
                if decision_ready and attempt.get('status') == 'SETTLED' and any(
                        c.attempt_id == attempt.get('attempt_id') or c.report_id == result_record_key(attempt)
                        for c in campaign.state.consumed):
                    # Check this run's earlier receipts even while a different
                    # challenge is pending. A conflict closes admission for B.
                    campaign.record_result(self._settled_result(run_id, observed['context_key'], attempt))
                if p and p.kind == 'challenge':
                    if decision_ready and attempt.get('attempt_id') == (p.attempt_id or p.intent_id):
                        if start_reservation is None:
                            raise CampaignError('battle_start_reservation_missing')
                        if (not p.attempt_id and attempt.get('status') in ('SUBMITTED', 'WAITING', 'SETTLED')
                                or attempt.get('status') == 'SETTLED'):
                            self._start_intents.bind_observation(start_reservation, attempt)
                        if not p.attempt_id and attempt.get('status') in ('SUBMITTED', 'WAITING', 'SETTLED'):
                            campaign.mark_submitted(p.intent_id, p.intent_id)
                            p = campaign.state.pending
                        if attempt.get('status') == 'SETTLED':
                            result = self._settled_result(run_id, observed['context_key'], attempt, stage_key=p.stage_key)
                            if (attempt.get('outcome') == 'LOSS'
                                    and attempt.get('evidence') == OBSERVED_LOSS_EVIDENCE):
                                # Native calls this LOSS for UI/retry policy;
                                # it is explicitly not an accepted settlement.
                                # Do not count it or release durable admission.
                                campaign.mark_unknown('loss_observed_settlement_unconfirmed')
                                self._emit('观察到本场战斗失败，但结算尚未确认；已保留本场意图并阻止重发。', 'WARNING')
                            elif not result.confirmed:
                                campaign.mark_unknown('invalid_settlement_evidence')
                            else:
                                self._start_intents.validate_settlement(start_reservation, attempt)
                                if campaign.record_result(result):
                                    self._emit(self._loss_message(campaign.state.losses)
                                               if result.outcome == Outcome.LOSS else '本场战斗胜利。')
                                self._start_intents.complete(start_reservation, attempt)
                                start_reservation = None
                                pending_since = None
                        elif attempt.get('status') == 'UNKNOWN':
                            campaign.mark_unknown('bridge_attempt_unknown')
                    if campaign.state.pending and pending_since is not None and self._clock() - pending_since >= self._battle_timeout:
                        campaign.mark_unknown('battle_timeout_pending_retained')
                elif p and p.kind == 'reviewed_guide':
                    current = self._observation(campaign, snap)
                    if decision_ready and current.revision > p.revision and (current.guide == GuideKind.NONE or current.step_key != p.step_key):
                        campaign.guide_completed(p.intent_id, current)
                        pending_since = None
                    elif pending_since is not None and self._clock() - pending_since >= self._transition_timeout:
                        campaign.mark_unknown('guide_transition_timeout')
                if (not growth_only and decision_ready and (not self._paused or yield_cleanup)
                        and not self._stopping and (campaign.state.mode == Mode.ACTIVE
                        or yield_cleanup and campaign.state.mode == Mode.PAUSED)):
                    pending_story = self._continue_pending_story(campaign, token, snap, pending_story)
                if campaign.state.mode == Mode.PAUSED and not self._paused and not self._stopping:
                    if campaign.state.reason == 'unknown_guide':
                        campaign.stop('unknown_guide')
                        self.stop(reason=campaign.state.reason)
                    else:
                        self.pause(reason=campaign.state.reason or 'workflow_pause')
                navigation_wait = (not self._stopping and (not self._paused or yield_cleanup) and
                    (campaign.state.mode == Mode.ACTIVE or yield_cleanup and campaign.state.mode == Mode.PAUSED)
                    and campaign.state.pending is None and
                    self._navigation_wait(campaign, snap))
                self._battle_flow = battle_flow(observed,
                    pending=campaign.state.pending is not None, reservation=start_reservation is not None,
                    navigation=navigation_wait, paused=yield_cleanup)
                if yield_cleanup and campaign.state.pending is None:
                    if yield_cleanup_since is None:
                        yield_cleanup_since = self._clock()
                    elif (not self._battle_flow['handoff_ready']
                            and self._clock() - yield_cleanup_since >= self._transition_timeout):
                        raise CampaignError('battle_yield_paused_cleanup_timeout')
                else:
                    yield_cleanup_since = None
                self._publish(campaign, path, snap)
                if campaign.state.mode == Mode.LOSS_LIMIT:
                    # The final result must finish while the original native
                    # observer and command admission are still owned. The
                    # ordinary planner and subclass navigation waits never run
                    # again after the third loss.
                    snap = self._cleanup_loss_limit(campaign, token, ctx, path, snap)
                    self.objects.stop_campaign(token, reason=campaign.state.reason or campaign.state.mode.value)
                    break
                if self._stopping:
                    if campaign.state.pending is None:
                        break
                    if self._clock() >= self._stop_deadline:
                        campaign.mark_unknown('stop_timeout_pending_retained')
                        break
                elif campaign.state.mode in (Mode.BLOCKED, Mode.STOPPED):
                    self.objects.stop_campaign(token, reason=campaign.state.reason or campaign.state.mode.value)
                    break
                if (decision_ready and (campaign.state.mode == Mode.ACTIVE
                        or yield_cleanup and campaign.state.mode == Mode.PAUSED)
                        and self._begin_yield(token, subscription, self._battle_flow)):
                    yielded_context = token.context_key
                    # Retire every local capability before waiting. In
                    # particular, stop/close while parked cannot clean up the
                    # temporary owner's connection from this run's finally.
                    token = subscription = connection_token = None
                    self._record_handoff(path, 'yielded', context_key=yielded_context, snap=snap,
                        losses=campaign.state.losses, flow=copy.deepcopy(self._battle_flow))
                    self._publish(campaign, path, snap)
                    if not self._wait_yield_resume(ctx):
                        campaign.stop(self._stop_reason or 'task_cancelled_while_yielded')
                        break
                    token = self._reacquire_yield(run_id, campaign.state.context_key, ctx)
                    if token is None:
                        campaign.stop(self._stop_reason or 'task_cancelled_while_yielded')
                        break
                    if hasattr(self.objects, 'subscribe_workflow') and hasattr(self.objects, 'workflow_snapshot'):
                        subscription = self.objects.subscribe_workflow(token, notify=self._wake.set)
                    observed = self.objects.observe_campaign(token)
                    observed = self._prepare_start_scene(token, ctx, observed)
                    if self._stopping or ctx.cancelled:
                        campaign.stop(self._stop_reason or 'task_cancelled_during_yield_resume')
                        break
                    if subscription is not None:
                        subscription.drain()
                        observed = self.objects.workflow_snapshot(token)
                    snap = observed['campaign']
                    if observed.get('context_key') != campaign.state.context_key:
                        raise CampaignError('battle_yield_context_changed_after_prepare')
                    if campaign.state.mode == Mode.PAUSED and not self._paused:
                        try:
                            campaign.resume(self._observation(campaign, snap))
                        except CampaignError as exc:
                            raise CampaignError('battle_yield_resume_failed: ' + str(exc)) from exc
                    self._record_handoff(path, 'resumed', context_key=token.context_key, snap=snap,
                        losses=campaign.state.losses)
                    pending_since = transition_since = selection_since = None
                    yield_cleanup_since = None
                    last_navigation = rejected_navigation = pending_story = stale_replan = None
                    revision_replan_since = None
                    decision_ready = True
                    with self._lock:
                        observed_epoch = self._decision_epoch
                    continue
                resume_epoch = None
                with self._lock:
                    if self._resume_requested and campaign.state.pending is None:
                        resume_epoch = self._resume_epoch
                        self._resume_requested = False
                if resume_epoch is not None:
                    if campaign.state.mode == Mode.PAUSED:
                        try:
                            # Resume validates the latest cached native state;
                            # the epoch still guards concurrent decisions.
                            observed = (self.objects.workflow_snapshot(token) if subscription is not None
                                        else self.objects.observe_campaign(token))
                            snap = observed['campaign']
                            if snap.get('available') is not True or snap.get('gate') != 'PASS':
                                raise CampaignError('resume_gate_closed: ' + str(snap.get('gate')))
                            with self._lock:
                                current = self._resume_current_locked(resume_epoch)
                            if current:
                                # No coordinator lock spans checkpoint fsync.
                                # A later decision keeps the memory port closed
                                # while this older ACTIVE write is reconciled.
                                campaign.resume(self._observation(campaign, snap))
                                reopened = False
                                with self._lock:
                                    if self._resume_current_locked(resume_epoch):
                                        reopened = self.objects.resume_campaign(token)
                                        if reopened:
                                            self._paused = False
                                            self._resume_epoch = None
                                if reopened:
                                    decision_ready = True
                                    if rejected_navigation == last_navigation:
                                        last_navigation = None
                                        rejected_navigation = None
                                else:
                                    with self._lock:
                                        stopping = self._stopping or self._closing
                                    if stopping:
                                        campaign.stop(self._stop_reason or 'assistant_shutdown')
                                    else:
                                        campaign.pause('resume_superseded_by_newer_decision')
                                    # Do not call public pause here: it would
                                    # invalidate a newer queued resume that
                                    # arrived during either checkpoint write.
                                self._publish(campaign, path, snap)
                        except CampaignError as exc:
                            self._emit(str(exc), 'WARNING')
                if resume_epoch is not None and not self._stopping and not self._paused:
                    navigation_wait = self._navigation_wait(campaign, snap)
                if self._stopping or self._paused and not yield_cleanup:
                    transition_since = None
                    selection_since = None
                if stale_replan is not None:
                    rejected_revision, decision_epoch = stale_replan
                    with self._lock:
                        current = (decision_epoch == self._decision_epoch and not self._paused and
                                   not self._stopping and not self._closing)
                    if not current:
                        stale_replan = None
                        revision_replan_since = None
                    elif observed.get('context_key') != campaign.state.context_key:
                        raise CampaignError('campaign_context_changed_after_stale_rejection')
                    elif (snap.get('available') is True and snap.get('gate') == 'PASS'
                          and not self._game_transition(snap)):
                        revision = snap.get('revision')
                        if type(revision) is not int or revision < 0:
                            raise CampaignError('invalid revision')
                        if ((selection_since is not None and self._clock() - selection_since >= self._transition_timeout) or
                                (revision_replan_since is not None and self._clock() - revision_replan_since >= self._transition_timeout)):
                            campaign.pause('game_transition_timeout')
                            self.pause(reason=campaign.state.reason or 'workflow_pause')
                        elif decision_ready and revision > rejected_revision:
                            stale_replan = None
                            transition_since = None
                        else:
                            if transition_since is None:
                                transition_since = self._clock()
                            elif self._clock() - transition_since >= self._transition_timeout:
                                campaign.pause('game_transition_timeout')
                                self.pause(reason=campaign.state.reason or 'workflow_pause')
                if (self.SUPPORT_GROWTH and not self._yield_requested and not navigation_wait and decision_ready and not self._stopping and not self._paused and campaign.state.mode == Mode.ACTIVE
                        and campaign.state.pending is None and stale_replan is None
                        and self._flow(snap)['status'] == 'IDLE'
                        and snap.get('phase') in ('MAIN', 'CAMPAIGN')
                        and (subscription is not None or hasattr(self.objects, 'observe_development'))):
                    development = (observed.get('development') if subscription is not None
                                   else self.objects.observe_development(token)['development']) or {}
                    growth = development.get('growth') or {}
                    if allow_growth or growth.get('prompt_active') is True or growth.get('window_visible') is True:
                        if not allow_growth:
                            reason = 'campaign_growth_dependency_disabled'
                            campaign.stop(reason)
                            self.stop(reason=reason)
                            self._publish(campaign, path, snap)
                            break
                        from .growth import select_growth_task, _quests
                        from .development_task import DevelopmentWorkflow
                        quests = _quests(growth)
                        selected = select_growth_task(growth)
                        if growth.get('window_visible') is True:
                            selected = next((q for q in quests
                                if q.quest_id == growth.get('ui_selected_quest_id')), selected)
                            candidates = [selected] if selected else []
                        else:
                            # MainUI selects unfinished requirements before
                            # completed rewards. Claim the observed finished
                            # rows explicitly so those rewards are not hidden
                            # behind the next unfinished quest or prompt flag.
                            candidates = [q for q in quests if q.isfinish == 1 and q.isgot == 0]
                            if selected and selected not in candidates:
                                if selected.condition_met is True:
                                    candidates.insert(0, selected)
                                else:
                                    candidates.append(selected)
                        requirement = growth.get('requirement') or {}
                        readiness = tuple(requirement.get(field) for field in (
                            'feature_unlocked', 'reputation_unlocked', 'reputation_level',
                            'artifact_id', 'used_times', 'free_times', 'rate', 'grow_cost'))
                        task_state = tuple((q.quest_id, q.isfinish, q.isgot, q.condition_met) for q in quests)
                        quest = key = None
                        for candidate in candidates:
                            candidate_key = (candidate.quest_id, candidate.type, candidate.param0, candidate.param1,
                                             readiness, snap.get('phase'), task_state)
                            if candidate_key not in campaign_growth_tasks:
                                quest, key = candidate, candidate_key
                                break
                        if quest:
                            if growth_only:
                                # The native quest collection is bounded to
                                # 128. A one-off pass must not become an open
                                # ended resource-consuming progression loop.
                                if growth_only_steps >= 128:
                                    campaign.stop('growth_only_step_limit')
                                    self._publish(campaign, path, snap)
                                    break
                                growth_only_steps += 1
                            claiming_reward = quest.isfinish == 1
                            self._emit(('领取成长奖励：' if claiming_reward else '处理当前成长任务：') + quest.name)
                            workflow = DevelopmentWorkflow(growth_objects, token, self.data_dir.parent,
                                stopped=lambda: self._stopping or ctx.cancelled,
                                paused=lambda: self._paused, wait=lambda: self._wait(1.0),
                                subscription=subscription,
                                publish=lambda result: self._emit('当前成长任务：' + result.status + ' · ' + result.reason))
                            result = (workflow.claim_finished_growth(quest_id=quest.quest_id) if claiming_reward
                                      else workflow.current_growth_task(quest_id=quest.quest_id))
                            observed = (self.objects.workflow_snapshot(token) if subscription is not None
                                        else self.objects.observe_campaign(token))
                            if observed.get('context_key') != campaign.state.context_key:
                                raise CampaignError('campaign_context_changed')
                            snap = observed['campaign']
                            attempt = snap.get('attempt') or {}
                            business = (observed.get('state') or {}).get('business') or {}
                            equipment = (observed.get('development') or {}).get('equipment') or {}
                            equipment_ui = equipment.get('ui') or {}
                            growth_after = (observed.get('development') or {}).get('growth') or {}
                            no_equipment_action = (result.status == 'BLOCKED' and
                                result.reason in ('no_owned_equipped_candidate', 'equipment_capital_actions_unsupported') and
                                (equipment.get('operation') or {}).get('mutation_attempted') is False and
                                (growth_after.get('operation') or {}).get('mutation_attempted') is False and
                                all((equipment_ui.get(name) or {}).get('visible') is False
                                    for name in ('equipment', 'reinforce')) and
                                growth_after.get('window_visible') is False)
                            no_reward_action = (claiming_reward and result.status == 'BLOCKED' and
                                result.reason == 'finished_growth_normal_open_unavailable' and
                                workflow.pending is None and
                                (growth_after.get('operation') or {}).get('mutation_attempted') is False and
                                growth_after.get('window_visible') is False)
                            defer_safe = ((result.status == 'UNSUPPORTED' or no_equipment_action or no_reward_action) and not observed.get('stopping') and
                                self._flow(snap)['status'] == 'IDLE' and
                                snap.get('phase') in ('MAIN', 'CAMPAIGN') and
                                snap.get('available') is True and snap.get('gate') == 'PASS' and
                                all(business.get(field) is False for field in (
                                    'pending_attempt', 'pending_development', 'pending_player_refresh')))
                            if result.status == 'NEEDS_CAMPAIGN' or defer_safe:
                                campaign_growth_tasks.add(key)
                                if growth_only and result.status == 'NEEDS_CAMPAIGN':
                                    campaign.stop('growth_only_needs_campaign: quest=%s; %s' % (quest.quest_id, result.reason))
                                    self._publish(campaign, path, snap)
                                    break
                                if defer_safe:
                                    self._emit('成长任务待办：%s；原因：%s；%s。' % (quest.name, result.reason,
                                        '继续检查成长和奖励' if growth_only else '继续征战'), 'WARNING')
                            elif result.status != 'DONE':
                                if not self._stopping and not ctx.cancelled:
                                    # Release this task rather than leaving the
                                    # entire account plan paused. The child may
                                    # have left a panel or operation in flight;
                                    # the account coordinator rebuilds the game
                                    # session before starting the next task.
                                    growth_cleanup_pending = True
                                    campaign.stop('current_growth_task_%s: %s' %
                                                  (result.status.lower(), result.reason))
                            self._publish(campaign, path, snap)
                            # The child may have drained the borrowed event
                            # queue. Reconsider its latest cached state once.
                            decision_ready = True
                            if growth_cleanup_pending:
                                break
                            if result.status == 'DONE' or defer_safe:
                                # Re-read the actual successor and remaining
                                # rewards before another battle. A deferred
                                # target is retried only after observed quest
                                # state or requirement readiness changes.
                                continue
                if (growth_only and decision_ready and not self._stopping and not self._paused
                        and campaign.state.mode == Mode.ACTIVE):
                    # This branch precedes every ordinary campaign planner.
                    # A battle dependency is unfinished work, never permission
                    # to select a troop, prepare a formation or start a battle.
                    if (self._flow(snap)['status'] != 'IDLE' or snap.get('phase') not in ('MAIN', 'CAMPAIGN')
                            or snap.get('available') is not True or snap.get('gate') != 'PASS'):
                        reason = 'growth_only_idle_map_required'
                    else:
                        from .growth import _quests
                        current = (self.objects.workflow_snapshot(token) if subscription is not None
                                   else self.objects.observe_development(token))
                        if current.get('context_key') != campaign.state.context_key:
                            raise CampaignError('growth_only_context_changed')
                        fresh = current.get('campaign') or {}
                        growth = ((current.get('development') or {}).get('growth') or {})
                        remaining = [q.quest_id for q in _quests(growth) if q.isfinish != 1 or q.isgot != 1]
                        business = (current.get('state') or {}).get('business') or {}
                        if (current.get('stopping') or current.get('admission_open') is not True
                                or fresh.get('phase') not in ('MAIN', 'CAMPAIGN')
                                or self._flow(fresh)['status'] != 'IDLE' or fresh.get('available') is not True
                                or fresh.get('gate') != 'PASS'
                                or (fresh.get('attempt') or {}).get('status') not in ('NONE', 'SETTLED')
                                or growth.get('window_visible') is not False or not all(business.get(field) is False
                                for field in ('pending_attempt', 'pending_development', 'pending_player_refresh'))):
                            reason = 'growth_only_cleanup_unconfirmed'
                        else:
                            snap = fresh
                            reason = ('growth_only_incomplete: quests=' + ','.join(map(str, remaining))
                                      if remaining else 'growth_only_complete')
                    campaign.stop(reason)
                    self._publish(campaign, path, snap)
                    break
                if (not decision_ready and transition_since is not None and not self._stopping and
                        (not self._paused or yield_cleanup) and self._clock() - transition_since >= self._transition_timeout):
                    campaign.pause('game_transition_timeout')
                    self.pause(reason=campaign.state.reason or 'workflow_pause')
                if (decision_ready and not navigation_wait and not self._stopping
                        and (not self._paused or yield_cleanup)
                        and (campaign.state.mode == Mode.ACTIVE or yield_cleanup and campaign.state.mode == Mode.PAUSED) and
                        campaign.state.pending is None and
                        (stale_replan is None or self._flow(snap)['status'] in ('WAIT', 'UNSUPPORTED') or
                         snap.get('available') is not True or snap.get('gate') != 'PASS')):
                    if self._game_transition(snap):
                        # One budget spans all consecutive native waits, even
                        # when their reason/revision changes or Available is
                        # true while the game's local flow awaits input.
                        if transition_since is None:
                            transition_since = self._clock()
                        elif self._clock() - transition_since >= self._transition_timeout:
                            campaign.pause('game_transition_timeout')
                            self.pause(reason=campaign.state.reason or 'workflow_pause')
                    elif self._flow(snap)['status'] == 'UNSUPPORTED' or snap.get('available') is not True or snap.get('gate') != 'PASS':
                        transition_since = None
                        campaign.stop('campaign_gate: ' + (self._flow(snap)['reason'] or str(snap.get('gate'))))
                        self.stop(reason=campaign.state.reason)
                    else:
                        observation = self._observation(campaign, snap)
                        if yield_cleanup and snap.get('phase') not in ('RESULT', 'SCENARIO'):
                            # An idle map may be waiting for a business-port
                            # publication. Pause never grants entry, selection
                            # or formation work while waiting to hand off.
                            action, waiting = None, True
                        else:
                            action, snap, waiting = self._plan_action(campaign, snap, observation)
                        if yield_cleanup and action and action not in self.YIELD_CLEANUP_ACTIONS:
                            raise CampaignError('battle_yield_paused_cleanup_unsupported: ' + action)
                        if action == 'start' and self._yield_requested:
                            # A busy native business port may still need a
                            # fresh publication before the handoff is proven.
                            action, waiting = None, True
                        if waiting:
                            if transition_since is None:
                                transition_since = self._clock()
                            elif self._clock() - transition_since >= self._transition_timeout:
                                campaign.pause('game_transition_timeout')
                                self.pause(reason=campaign.state.reason or 'workflow_pause')
                        else:
                            transition_since = None
                        if action and action != 'select':
                            selection_since = None
                        if action and last_navigation != (snap['revision'], action):
                            intent = None
                            attempt_id = ''
                            if action in ('start', 'continue') or action in self.GENERAL_ACTIONS:
                                if growth_only:
                                    raise CampaignError('growth_only_battle_intent_forbidden')
                                if yield_cleanup and campaign.state.mode == Mode.PAUSED:
                                    # The only new intent allowed here records
                                    # a reviewed cleanup continuation. Restore
                                    # the domain pause before publishing or I/O.
                                    campaign.resume(observation)
                                    try:
                                        intent = campaign.prepare(observation, uuid.uuid4().hex)
                                    finally:
                                        campaign.pause('user_pause')
                                else:
                                    intent = campaign.prepare(observation, uuid.uuid4().hex)
                                if action == 'start':
                                    attempt_id = intent.intent_id
                                    key = self._start_key(campaign, snap)
                                    self._start_intent_path = str(self._start_intents.path_for(key))
                                    # This strong write is the admission boundary;
                                    # a best-effort run-ledger write cannot replace it.
                                    start_reservation = self._start_intents.reserve(
                                        key, campaign.state.run_id, campaign.state.context_key, attempt_id)
                                pending_since = self._clock()
                                self._publish(campaign, path, snap)
                            with self._lock:
                                submission_epoch = self._decision_epoch
                            try:
                                wire = self._submit(token, snap, action, attempt_id)
                            except BattleYieldRequested:
                                self._start_intents.reject(start_reservation,
                                    'admission_closed_before_submit', reason='battle_yield_requested')
                                start_reservation = None
                                campaign.reject_intent(intent.intent_id, 'battle_yield_requested',
                                                       retry_same_revision=True)
                                campaign.resume(observation)
                                pending_since = None
                                self._publish(campaign, path, snap)
                                observed = (self.objects.workflow_snapshot(token) if subscription is not None
                                            else self.objects.observe_campaign(token))
                                snap = observed['campaign']
                                continue
                            except CampaignAdmissionClosed:
                                if action == 'start':
                                    self._start_intents.reject(start_reservation,
                                        'admission_closed_before_submit', reason='owning_port_admission_closed')
                                    start_reservation = None
                                if intent:
                                    campaign.reject_intent(intent.intent_id, 'admission_closed_before_submit',
                                                           retry_same_revision=True)
                                else:
                                    campaign.pause('admission_closed_before_submit')
                                pending_since = None
                                last_navigation = (snap['revision'], action)
                                rejected_navigation = last_navigation
                                self.pause(reason=campaign.state.reason or 'workflow_pause')
                                self._publish(campaign, path, snap)
                                # A pause won admission; only explicit resume
                                # may reuse this proven-unsubmitted revision.
                            else:
                                last_navigation = (snap['revision'], action)
                                if wire.get('cancelled_before_submit') is True:
                                    if action == 'start':
                                        self._start_intents.reject(start_reservation,
                                            'cancelled_before_submit', reason='owning_port_cancelled_before_submit')
                                        start_reservation = None
                                    if intent:
                                        campaign.reject_intent(intent.intent_id, 'cancelled_before_submit',
                                                               retry_same_revision=True)
                                    else:
                                        campaign.pause('cancelled_before_submit')
                                    pending_since = None
                                    rejected_navigation = last_navigation
                                    self.pause(reason=campaign.state.reason or 'workflow_pause')
                                    self._publish(campaign, path, snap)
                                    continue
                                receipt = self._receipt(wire, action, attempt_id)
                                if wire.get('ack', {}).get('status') == 'REJECTED' and receipt.get('invoked') is False:
                                    reason = receipt.get('reason')
                                    if type(reason) is not str or not reason:
                                        reason = 'bridge_rejected_without_invocation'
                                    stale_snapshot = reason == 'campaign_revision_changed'
                                    transient_select = (action == 'select' and reason.startswith('normal_troop_selection_gate:') and
                                                        reason[len('normal_troop_selection_gate:'):] in self.SELECTION_TRANSIENTS)
                                    if action == 'start':
                                        self._start_intents.reject(start_reservation,
                                            'rejected_without_invocation', command_seq=receipt['command_seq'], reason=reason)
                                        start_reservation = None
                                    if intent:
                                        campaign.reject_intent(intent.intent_id, reason,
                                                               retry_same_revision=not stale_snapshot)
                                    rejected_navigation = last_navigation
                                    pending_since = None
                                    if stale_snapshot or transient_select:
                                        # The exact correlated rejection proves no invocation.
                                        # Keep its revision reserved and replan only from a
                                        # newer observation of this same native context.
                                        stale_replan = (snap['revision'], submission_epoch)
                                        if stale_snapshot and revision_replan_since is None:
                                            revision_replan_since = self._clock()
                                        if transient_select and selection_since is None:
                                            selection_since = self._clock()
                                        transition_since = selection_since if transient_select else revision_replan_since
                                    elif (self.RUN_DIRECTORY == 'campaign_runs' and action == 'select'
                                          and reason == 'normal_troop_selection_gate:character_level'
                                          and campaign.state.pending is None):
                                        # A confirmed level prerequisite ends this round's
                                        # available mainline progress; waiting cannot raise it.
                                        campaign.stop(reason)
                                        self.stop(reason=reason)
                                    else:
                                        self._rejected_action(campaign, reason)
                                    self._publish(campaign, path, snap)
                                    self._emit(action + ' 未调用：' + reason, 'WARNING')
                                elif receipt.get('invoked') is True and wire.get('ack', {}).get('status') == 'COMPLETED':
                                    selection_since = None
                                    revision_replan_since = None
                                    if action == 'start':
                                        self._start_intents.submitted(start_reservation, receipt['command_seq'])
                                        campaign.mark_submitted(intent.intent_id, intent.intent_id)
                                elif intent:
                                    campaign.mark_unknown('campaign_invocation_unconfirmed')
                                else:
                                    # Navigation also needs a conclusive current
                                    # receipt. Do not replay an uncertain action.
                                    raise CampaignError('campaign_invocation_unconfirmed: ' + action)
                if subscription is None:
                    self._wait()
                    observed = self.objects.observe_campaign(token)
                    decision_ready = True
                else:
                    # Drain first so an event raised during command handling
                    # cannot be lost while entering the wait.
                    native_events = subscription.drain()
                    if not native_events:
                        now = self._clock()
                        deadlines = [t for t in (
                            pending_since + self._battle_timeout if pending_since is not None else None,
                            transition_since + self._transition_timeout if transition_since is not None else None,
                            self._stop_deadline if self._stopping else None,
                        ) if t is not None and t > now]
                        timeout = min(1.0, min(deadlines) - now) if deadlines else 1.0
                        self._wait(timeout)
                        native_events = subscription.drain()
                    observed = self.objects.workflow_snapshot(token)
                    with self._lock:
                        epoch = self._decision_epoch
                    decision_ready = bool(native_events) or epoch != observed_epoch
                    observed_epoch = epoch
                snap = observed['campaign']
        except Exception as exc:
            if campaign:
                try:
                    if str(exc).startswith('battle_yield_'):
                        campaign.stop(str(exc))
                    elif campaign.state.pending:
                        campaign.mark_unknown(str(exc))
                    else:
                        self._unpending_error(campaign, str(exc))
                    self._publish(campaign, path, snap)
                except Exception as persistence_error:
                    with self._lock:
                        self._runtime_error = 'persistence_error: ' + str(persistence_error)
                        self._snapshot.update(mode='blocked', reason=self._runtime_error)
            else:
                with self._lock:
                    self._snapshot.update(mode='blocked', reason=str(exc))
            self._emit(str(exc), 'ERROR')
        finally:
            try:
                if (campaign and path and token and campaign.state.mode == Mode.LOSS_LIMIT
                        and self._ui_cleanup is None):
                    # An unrelated persistence/port failure after counting the
                    # third loss must not erase the outstanding UI obligation.
                    self._ui_cleanup = self._loss_limit_cleanup_record(campaign, token, path, snap,
                        state='unknown', reason='loss_limit_ui_cleanup_not_reached')
                    try:
                        self._persist_ui_cleanup(campaign, path, snap)
                    except Exception as evidence_error:
                        self._ui_cleanup['evidence_error'] = str(evidence_error)
                if start_reservation is not None:
                    # RESERVED itself already blocks after a crash or a failed
                    # update. UNKNOWN adds a reason, never clears admission.
                    try:
                        self._start_intents.mark_unknown(start_reservation,
                            (campaign.state.reason if campaign else '') or 'run_ended_without_confirmed_result')
                    except Exception as guard_error:
                        with self._lock:
                            self._runtime_error = 'battle_start_persistence_error: ' + str(guard_error)
                            self._snapshot.update(mode='blocked', reason=self._runtime_error)
                if subscription is not None:
                    try:
                        subscription.close()
                    except Exception as close_error:
                        with self._lock:
                            self._snapshot.update(cleanup_error='subscription_close: ' + str(close_error))
                if campaign and path:
                    self._publish(campaign, path, snap)
                if growth_cleanup_pending:
                    with self._lock:
                        self._snapshot['cleanup_pending'] = True
                if token:
                    self.objects.release_campaign(token, cleanup=True)
                    cleanup_deadline = time.monotonic() + 6.0
                    while self.objects.is_busy and time.monotonic() < cleanup_deadline:
                        self._wait()
                    with self._lock:
                        bridge_state = self.objects.bridge_state
                        clean = bridge_state == 'disconnected' and not self.objects.is_busy
                        self._snapshot.update(bridge_state=bridge_state,
                            cleanup_state='confirmed' if clean else
                                          'unknown' if bridge_state == 'unknown' else 'pending')
                elif connection_token is not None:
                    # A different owner or replacement attempt may have won
                    # admission. Conditional cancellation must not affect it.
                    self.objects.cancel_campaign_connection(connection_token)
            except Exception as cleanup_error:
                with self._lock:
                    self._snapshot.update(cleanup_error=str(cleanup_error))
                self._emit(str(cleanup_error), 'ERROR')
            finally:
                with self._lock:
                    self._token = None
                    self._active = False
                    self._paused = False
                    self._yield_requested = self._yielded = self._yield_resume_requested = False
                    self._snapshot.update(yield_requested=False, yielded=False, yield_resume_requested=False)
                    if self._closing:
                        self._shutdown = True
                self._emit()
