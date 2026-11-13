"""Own a bounded soul run or a read-only stock quotation through the shared runner."""
from __future__ import annotations

from dataclasses import asdict, dataclass, replace
import json
import logging
from pathlib import Path
import threading
import time
import uuid

from .bridge_client import BridgeError
from .core import RunContext, TaskRunner
from .soul_executor import (SoulExecutor, DailySoulResult, SoulRoundReceipt, SoulSkip,
                         SoulActionEvidence, SoulGrowthReceipt, SoulDecisionEvidence,
                         SoulEvidenceWriteError, _valid, soul_quote)
from .scene_navigation import SceneGoal, ensure_scene
from .soul_strategy import SoulPolicy
from .event_stream import EventMailbox
from .workflow_wait import WakeSignal


@dataclass(frozen=True)
class SoulRunResult:
    status: str
    reason: str
    blue_requested: int = 0
    purple_requested: int = 0
    blue_completed: int = 0
    purple_completed: int = 0
    coins_spent: int = 0
    cleanup: str = 'NOT_NEEDED'
    character_id: str = ''
    cycle: int = 0
    operation_id: str = ''
    current_color: str = ''
    rounds: tuple[SoulRoundReceipt, ...] = ()
    skipped: tuple[SoulSkip, ...] = ()
    quote: dict | None = None
    ledger_path: str = ''
    growths: tuple[SoulGrowthReceipt, ...] = ()
    decisions: tuple[SoulDecisionEvidence, ...] = ()
    pending_action: SoulActionEvidence | None = None
    not_submitted_actions: tuple[SoulActionEvidence, ...] = ()
    policy: SoulPolicy | None = None
    coin_budget: int | None = None
    ledger_schema: int = 2
    evidence_path: str = ''
    evidence_error: str = ''
    navigation_status: str = ''
    navigation_reason: str = ''
    navigation_flow: dict | None = None
    policy_source: str = ''
    model_update_error: str = ''
    harvest_existing: bool = False
    failure_evidence_path: str = ''


class SoulCoordinator:
    CONNECT_TIMEOUT = 30.0
    MAX_TOTAL = 100

    def __init__(self, objects, runner: TaskRunner, data_dir) -> None:
        self.objects, self.runner, self.data_dir = objects, runner, Path(data_dir)
        self.events = EventMailbox('soul')
        self._lock = threading.RLock()
        self._wake = WakeSignal()
        self._active = self._paused = self._stopping = self._closing = False
        self._token = None
        self._result = SoulRunResult('IDLE', 'ready')

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
        return not self.is_active

    @property
    def snapshot(self) -> dict:
        with self._lock:
            result = self._result
        return asdict(result)

    @property
    def summary_snapshot(self) -> dict:
        """Do not expand histories while UI/control observers hold coordinator locks."""
        with self._lock:
            result = self._result
        histories = {key: getattr(result, key) for key in
                     ('rounds', 'growths', 'decisions', 'not_submitted_actions')}
        pending = result.pending_action
        thin = replace(result, **{key: () for key in histories},
                       pending_action=replace(pending, decision=None) if pending is not None else None)
        value = asdict(thin)
        for key, items in histories.items():
            value.pop(key, None)
            value[key + '_count'] = len(items)
            value[key + '_details_omitted'] = True
        if pending is not None and pending.decision is not None:
            value['pending_action'].pop('decision', None)
            value['pending_action']['decision_details_omitted'] = True
        return value

    def _publish(self, result: SoulRunResult) -> None:
        with self._lock:
            if result != self._result:
                self._result = result
                self.events.put(result)

    def _navigation_progress(self, kind, **values) -> None:
        if kind != 'scene_preparation':
            return
        with self._lock:
            status = values['status']
            self._publish(replace(self._result,
                status='RUNNING' if status == 'READY' else status,
                reason=values['reason'], navigation_status=status,
                navigation_reason=values['reason'], navigation_flow=values['flow']))

    def start(self, blue_count: int, purple_count: int, *, expected_character_id=None,
              expected_user_id=None, expected_zone_id=None, coin_budget: int | None = None,
              policy: SoulPolicy | None = None) -> bool:
        if (coin_budget is not None and (type(coin_budget) is not int or not 0 <= coin_budget <= 0x7FFFFFFFFFFFFFFF)
                or policy is not None and not isinstance(policy, SoulPolicy)):
            return False
        for expected in (expected_character_id, expected_user_id, expected_zone_id):
            if expected is not None and (
                    not isinstance(expected, str) or not expected.isascii()
                    or not expected.isdecimal() or not expected.strip('0')):
                return False
        if (type(blue_count) is not int or type(purple_count) is not int
                or not 0 <= blue_count <= self.MAX_TOTAL or not 0 <= purple_count <= self.MAX_TOTAL
                or not 1 <= blue_count + purple_count <= self.MAX_TOTAL):
            return False
        return self._start(blue_count, purple_count, quote_only=False,
                           expected_character_id=expected_character_id,
                           expected_user_id=expected_user_id, expected_zone_id=expected_zone_id,
                           coin_budget=coin_budget, policy=policy)

    def refresh_quote(self) -> bool:
        """Connect, observe loaded stock state and release ownership without opening a game page."""
        return self._start(0, 0, quote_only=True)

    def harvest_existing(self, *, expected_character_id=None, expected_user_id=None,
                         expected_zone_id=None) -> bool:
        """Settle the current board without requesting a start or growth."""
        return self._start(0, 0, quote_only=False, harvest_existing=True, coin_budget=0,
            expected_character_id=expected_character_id, expected_user_id=expected_user_id,
            expected_zone_id=expected_zone_id)

    def _start(self, blue_count: int, purple_count: int, *, quote_only: bool,
               expected_character_id=None, expected_user_id=None, expected_zone_id=None,
               coin_budget=None, policy=None, harvest_existing=False) -> bool:
        with self._lock:
            if self._active or self._closing or self.runner.is_active or self.objects.has_campaign_owner:
                return False
            self._active = True
            self._paused = self._stopping = False
            self._wake.clear()
            initial = SoulRunResult('QUOTING' if quote_only else 'CONNECTING',
                                    'soul_quote_observing' if quote_only else 'connecting',
                                    blue_count, purple_count, coin_budget=coin_budget,
                                    policy=policy,
                                    policy_source='explicit' if policy is not None else '',
                                    harvest_existing=harvest_existing)
            self._publish(initial)
            try:
                accepted = self.runner.start('聚魂报价' if quote_only else '收获当前牌面' if harvest_existing else '聚魂',
                    lambda ctx: self._run(ctx, blue_count, purple_count, quote_only,
                                          expected_character_id, expected_user_id,
                                          expected_zone_id, coin_budget, policy, harvest_existing), checkpoints=False)
            except Exception as exc:
                accepted = False
                self._publish(replace(initial, status='BLOCKED', reason=f'soul_runner_start_failed ({exc})'))
            if not accepted:
                self._active = False
                if self._result == initial:
                    self._publish(replace(initial, status='BLOCKED', reason='soul_runner_unavailable'))
            return accepted

    def pause(self) -> bool:
        with self._lock:
            if not self._active or self._paused or self._stopping:
                return False
            if self._token is not None and not self.objects.pause_campaign(self._token):
                return False
            self._paused = True
            self.runner.pause()
            self._wake.set()
            return True

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
            return True

    def stop(self) -> bool:
        with self._lock:
            if not self._active or self._stopping:
                return False
            self._stopping, self._paused = True, False
            if self._token is not None:
                self.objects.stop_campaign(self._token)
            self.runner.stop()
            self._wake.set()
            return True

    def close(self) -> None:
        with self._lock:
            self._closing = True
            self.stop()

    def _stopped(self, ctx: RunContext) -> bool:
        with self._lock:
            return self._stopping or ctx.cancelled

    def _wait(self) -> None:
        self._wake.wait(.1)

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
                    raise BridgeError(f"object_connection_failed ({connection.get('error') or connection['state']})")
            if time.monotonic() >= deadline:
                raise BridgeError('object_connection_timeout')
            self._wait()
        return False

    def _from_step(self, step: DailySoulResult) -> SoulRunResult:
        with self._lock:
            return replace(self._result, status=step.status, reason=step.reason,
                blue_completed=step.blue_completed, purple_completed=step.purple_completed,
                coins_spent=step.coins_spent, cleanup=step.cleanup, operation_id=step.operation_id,
                current_color=step.current_color or (step.rounds[-1].color if step.rounds else ''),
                rounds=step.rounds, skipped=step.skipped, quote=step.quote,
                growths=step.growths, decisions=step.decisions, pending_action=step.pending_action,
                not_submitted_actions=step.not_submitted_actions,
                policy=step.policy, coin_budget=step.coin_budget,
                evidence_path=step.evidence_path, evidence_error=step.evidence_error,
                failure_evidence_path=step.failure_evidence_path,
                policy_source=step.policy_source or self._result.policy_source)

    def _persist_step(self, step: DailySoulResult) -> None:
        # Signal persistence failure distinctly so the executor can refuse new
        # charges while still settling and closing its already-owned gather.
        result = self._from_step(step)
        try:
            recorded = self._record(result)
            # Completion is published after release and the final ledger/model
            # write, so a consumer cannot race a manual append against learning.
            if not (result.status == 'COMPLETED' and result.cleanup == 'VERIFIED_CLOSED'):
                self._publish(recorded)
        except (OSError, TypeError, ValueError) as exc:
            self._publish(replace(result, evidence_error=f'soul_evidence_write_failed ({exc})'))
            raise SoulEvidenceWriteError(str(exc)) from exc

    def _record(self, result: SoulRunResult) -> SoulRunResult:
        """Retain only observations made during this run; never reconstruct a past board."""
        target = (self.data_dir / 'soul-runs' / f'{result.operation_id or uuid.uuid4().hex}.json').resolve()
        target.parent.mkdir(parents=True, exist_ok=True)
        result = replace(result, ledger_path=str(target))
        began = time.perf_counter()
        document = asdict(result)
        expanded = time.perf_counter()
        timing = self._atomic_json(target, document)
        timing.update(operation_id=result.operation_id, status=result.status, reason=result.reason,
                      asdict_ms=(expanded - began) * 1000,
                      total_ms=(time.perf_counter() - began) * 1000,
                      observed_at=time.time())
        try:
            with target.with_suffix('.timing.jsonl').open('a', encoding='utf-8') as stream:
                stream.write(json.dumps(timing, ensure_ascii=False, separators=(',', ':')) + '\n')
        except OSError:
            logging.getLogger(__name__).warning('Soul evidence timing unavailable', exc_info=True)
        return result

    @staticmethod
    def _atomic_json(target: Path, document) -> dict:
        began = time.perf_counter()
        encoded = json.dumps(document, ensure_ascii=False, separators=(',', ':'))
        prepared = time.perf_counter()
        temporary = target.with_name(target.name + '.' + uuid.uuid4().hex + '.tmp')
        try:
            temporary.write_text(encoded, encoding='utf-8')
            temporary.replace(target)
        finally:
            temporary.unlink(missing_ok=True)
        return dict(bytes=len(encoded.encode('utf-8')), encode_ms=(prepared - began) * 1000,
                    io_ms=(time.perf_counter() - prepared) * 1000)

    def _learn_closed_ledger(self, result: SoulRunResult) -> None:
        """Index a durably closed single-entry run; raw receipts stay authoritative."""
        if not (result.policy is not None and result.policy.version == 'joint-value-v3'
                and result.status == 'COMPLETED' and result.cleanup == 'VERIFIED_CLOSED'):
            return
        requested = {mode: count for mode, count in
                     ((1, result.blue_requested), (2, result.purple_requested)) if count > 0}
        if len(requested) != 1 or result.harvest_existing or any(r.adopted_board for r in result.rounds):
            return
        mode = next(iter(requested)) if len(requested) == 1 else None
        completed = result.blue_completed if mode == 1 else result.purple_completed
        other_completed = result.purple_completed if mode == 1 else result.blue_completed
        if (mode is None or not result.operation_id or not result.rounds or result.pending_action is not None
                or result.evidence_error or other_completed != 0 or completed != len(result.rounds)
                or completed != requested.get(mode)
                or any(r.color != ('blue' if mode == 1 else 'purple') or r.mode != mode or r.start_verified is not True
                       or r.harvest_verified is not True for r in result.rounds)):
            raise ValueError('soul_model_update_receipt_not_closed')
        ledger = Path(result.ledger_path)
        if not ledger.is_absolute():
            raise ValueError('soul_model_update_ledger_path_not_absolute')
        target = Path(result.policy.model_path).resolve()
        document = json.loads(target.read_text(encoding='utf-8'))
        if (not isinstance(document, dict)
                or document.get('schema') not in ('purple-joint-transitions-v1', 'soul-joint-receipts-v1')
                or document.get('schema') == 'purple-joint-transitions-v1' and mode != 2
                or document.get('mode', mode) != mode
                or document.get('character_id') != result.character_id):
            raise ValueError('soul_model_update_identity_or_schema_invalid')
        additional = document.get('additional_ledgers', [])
        if (not isinstance(additional, list)
                or any(not isinstance(p, str) or not Path(p).is_absolute() for p in additional)):
            raise ValueError('soul_model_update_additional_ledgers_invalid')
        source = str(ledger.resolve())
        if source in {str(Path(p).resolve()) for p in additional}:
            return
        document['additional_ledgers'] = [*additional, source]
        self._atomic_json(target, document)

    def _run(self, ctx: RunContext, blue_count: int, purple_count: int, quote_only: bool,
             expected_character_id=None, expected_user_id=None, expected_zone_id=None,
             coin_budget=None, policy=None, harvest_existing=False) -> None:
        connection = token = subscription = None
        result = replace(self._result, status='STOPPED', reason='user_stop')
        execution_started = False
        try:
            with self._lock:
                if self._stopped(ctx):
                    return
                status = self.objects.connection_status
                generation = status['generation']
                if status['state'] != 'ready':
                    connection = self.objects.begin_campaign_connection()
                    if connection is None:
                        raise BridgeError('object_connection_unavailable')
                    generation = connection.generation
            if not self._await_connection(ctx, generation):
                return
            with self._lock:
                if self._stopped(ctx):
                    return
                token = self.objects.acquire_campaign(uuid.uuid4().hex)
                if token.generation != generation:
                    raise BridgeError('object_connection_replaced')
                for field, expected in (('character', expected_character_id),
                                        ('user', expected_user_id), ('zone', expected_zone_id)):
                    if expected is None:
                        continue
                    try:
                        actual = json.loads(token.context_key)[field + '_id']
                    except (AttributeError, KeyError, TypeError, ValueError) as exc:
                        raise BridgeError('soul_expected_' + field + '_context_invalid') from exc
                    if actual != expected:
                        raise BridgeError('soul_expected_' + field + '_mismatch')
                self._token = token
                if self._paused and not self.objects.pause_campaign(token):
                    raise BridgeError('object_admission_pause_failed')
            while self.is_paused and not self._stopped(ctx):
                self._wait()
            if self._stopped(ctx):
                return
            if not quote_only:
                subscription = self.objects.subscribe_workflow(token, notify=self._wake.set)
                if not harvest_existing:
                    navigation = ensure_scene(self.objects, token, SceneGoal.SOUL_ENTRY,
                        stopped=lambda: self._stopped(ctx), paused=lambda: self.is_paused,
                        wait=self._wait, publish=self._navigation_progress, timeout=None)
                    if navigation.status != 'READY':
                        pending = (navigation.status == 'UNKNOWN'
                                   or 'pending_retained' in navigation.reason)
                        result = replace(self._result, status=navigation.status, reason=navigation.reason,
                                         cleanup='UNVERIFIED' if pending else 'NOT_NEEDED')
                        return
                while self.is_paused and not self._stopped(ctx):
                    self._wait()
                if self._stopped(ctx):
                    return
            observed = self.objects.observe_daily_soul(token)
            s = observed.get('soul') if observed.get('enabled') is True else None
            adoption_deadline = time.monotonic() + 22.0
            while (harvest_existing and isinstance(s, dict)
                    and s.get('reason') == 'daily_soul_adoption_await_distinct_stable_frames'):
                self._publish(replace(self._result, status='RUNNING',
                    reason='soul_existing_board_awaiting_stable_frames'))
                if self._stopped(ctx):
                    return
                if time.monotonic() >= adoption_deadline:
                    raise BridgeError('soul_existing_board_stability_timeout')
                self._wait()
                observed = self.objects.observe_daily_soul(token)
                s = observed.get('soul') if observed.get('enabled') is True else None
            if not _valid(s):
                raise BridgeError('soul_color_quote_unavailable')
            quote = soul_quote(s)
            self._publish(replace(self._result, quote=quote, character_id=s['character_id'], cycle=s['cycle']))
            if quote_only:
                result = replace(self._result, status='QUOTED', reason='soul_quote_observed')
                return
            if quote is None or s['character_id'] == '0':
                raise BridgeError(s['reason'] or 'soul_color_quote_unavailable')
            role = json.loads(token.context_key)['character_id']
            if role != s['character_id']:
                raise BridgeError('soul_character_context_changed')
            if harvest_existing:
                if s['action'] != 'adopt' or s['reason'] or not any(slot['color'] for slot in s['board']):
                    raise BridgeError(s['reason'] or 'soul_existing_board_not_adoptable')
                mode = s['selected_mode']
                blue_count, purple_count = int(mode == 1), int(mode == 2)
                policy, policy_source = SoulPolicy(), 'existing_board_harvest'
                self._publish(replace(self._result, blue_requested=blue_count, purple_requested=purple_count))
            else:
                policy_source = 'explicit' if policy is not None else 'shared_per_entry'
            self._publish(replace(self._result, policy=policy, policy_source=policy_source))
            executor = SoulExecutor(self.objects, token, stopped=lambda: self._stopped(ctx),
                paused=lambda: self.is_paused, wait=self._wait,
                resource_policy_provider=lambda: (self.objects.resource_policy_provider()
                    if getattr(self.objects, 'resource_policy_provider', None) else None),
                publish=self._persist_step)
            self._publish(replace(self._result, status='RUNNING', reason='soul_plan_observing'))
            execution_started = True
            if harvest_existing:
                step = executor.harvest_existing(character_id=role, expected_cycle=s['cycle'],
                    expected_current=s['current'], expected_required=s['required'], mode=mode)
            else:
                step = executor.execute(character_id=role, expected_cycle=s['cycle'],
                    expected_current=s['current'], expected_required=s['required'],
                    blue_count=blue_count, purple_count=purple_count, free_only=False,
                    coin_budget=coin_budget, policy=policy)
            result = self._from_step(step)
        except Exception as exc:
            result = replace(self._result, status='UNKNOWN' if execution_started else 'BLOCKED', reason=str(exc),
                             cleanup='UNVERIFIED' if execution_started else 'NOT_NEEDED')
        finally:
            with self._lock:
                try:
                    if subscription is not None:
                        subscription.close()
                    owns_connection = (connection is not None and token is not None
                                       and connection.generation == token.generation)
                    needs_recovery = not quote_only and (result.status in ('UNKNOWN', 'STOPPED')
                                                        or result.cleanup == 'UNVERIFIED')
                    if token is not None:
                        self.objects.release_campaign(token, cleanup=owns_connection or needs_recovery)
                    if connection is not None and not owns_connection:
                        self.objects.cancel_campaign_connection(connection)
                except Exception as exc:
                    result = replace(result, status='UNKNOWN', reason=f'cleanup_unconfirmed ({exc})', cleanup='UNVERIFIED')
                finally:
                    if not quote_only:
                        try:
                            result = self._record(result)
                        except Exception as exc:
                            result = replace(result, reason=f'{result.reason}; soul_ledger_write_failed ({exc})')
                        else:
                            try:
                                self._learn_closed_ledger(result)
                            except Exception as exc:
                                result = replace(result, model_update_error=f'soul_model_update_failed ({exc})')
                                try:
                                    result = self._record(result)
                                except Exception as write_exc:
                                    result = replace(result, model_update_error=(
                                        f'{result.model_update_error}; diagnostic_ledger_write_failed ({write_exc})'))
                    self._token = None
                    self._active = self._paused = False
                    self._publish(result)
