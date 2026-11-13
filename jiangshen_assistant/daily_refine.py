"""Complete the shared ten ordinary experience 炼魂 exchanges under bridge ownership."""
from __future__ import annotations

from dataclasses import dataclass, replace
import time
import uuid

from .objects import CampaignAdmissionClosed
from .resource_policy import ResourcePolicyDenied


_TASK_ID = 21
_INT32_MAX = (1 << 31) - 1
_UINT32_MAX = (1 << 32) - 1
_HELD_STATES = {'OPENING', 'OPEN', 'PENDING', 'SETTLED'}
_TERMINAL = {'COMPLETED', 'REJECTED', 'UNKNOWN'}
_STATUSES = _HELD_STATES | _TERMINAL | {'IDLE', 'QUEUED'}
_ACK_PHASES = {
    'open': {'OPENING', 'OPEN'},
    'experience': {'OPEN'},
    'refine': {'PENDING', 'SETTLED'},
    'close': {'COMPLETED'},
}
_TRANSITIONS = {
    'OPENING': {'OPEN', 'REJECTED', 'UNKNOWN'},
    'OPEN': {'REJECTED', 'UNKNOWN'},
    'PENDING': {'SETTLED', *_TERMINAL},
    'SETTLED': {'COMPLETED', 'UNKNOWN'},
}
_WITNESSES = ('mutation_attempted', 'stock_callback_returned', 'account_progress_observed', 'panel_closed')


@dataclass(frozen=True)
class DailyRefineResult:
    status: str
    reason: str
    progress_before: int | None = None
    progress_after: int | None = None
    required: int | None = None
    cycle: int | None = None
    money_before: int | None = None
    money_after: int | None = None
    coin_exchanges_before: int | None = None
    coin_exchanges_after: int | None = None
    coins_spent: int | None = None
    practice_credit: int | None = None
    stock_callback_returned: bool = False
    account_progress_observed: bool = False
    mutation_attempted: bool | None = False
    ui_ownership_held: bool | None = None
    cleanup: str = 'NOT_NEEDED'
    mode: str = 'experience'
    experience_before: int | None = None
    experience_after: int | None = None
    experience_exchanges_before: int | None = None
    experience_exchanges_after: int | None = None
    experience_spent: int | None = None
    gold_exchanges_before: int | None = None
    gold_exchanges_after: int | None = None
    practice_before: int | None = None
    practice_after: int | None = None
    shared_exchange_limit: int = 10
    remaining_exchanges: int | None = None
    exchanges_completed: int = 0
    checklist_complete: bool = False
    operation_id: str = ''
    sequence: int = 0
    quote_revision: int | None = None
    final_revision: int | None = None
    attempts: tuple[DailyRefineResult, ...] = ()


@dataclass(frozen=True)
class _Quote:
    current: int
    required: int
    money: int
    coin_exchanges: int
    cost: int
    mode: str = 'coin'
    experience: int = 0
    experience_exchanges: int = 0
    gold_exchanges: int = 0
    practice: int = 0


def _integer(value, minimum, maximum):
    return type(value) is int and minimum <= value <= maximum


def _role_id(value):
    return (isinstance(value, str) and 0 < len(value) <= 20 and value.isascii() and value.isdecimal()
            and (len(value) == 1 or value[0] != '0') and int(value) < 1 << 64)


def _mode(value):
    return value.get('mode', 'coin')


def _cost(view):
    return view.get('experience_cost') if _mode(view) == 'experience' else view['money_cost']


def _affordable(view):
    cost = _cost(view)
    if _mode(view) == 'experience':
        return (cost is not None and view['money_cost'] == 0 and view['experience'] >= cost
                and view['experience_exchanges'] + view['gold_exchanges'] < 10)
    return cost is not None and cost <= view['money'] and view['coin_exchanges'] < 20


def _spends(op, quote):
    return (_mode(op) == quote.mode
            and op['coins_spent'] == (quote.cost if quote.mode == 'coin' else 0)
            and op.get('experience_spent', 0) == (quote.cost if quote.mode == 'experience' else 0))


def _valid_snapshot(snapshot):
    if (not isinstance(snapshot, dict) or not _integer(snapshot.get('schema'), 1, 1)
            or not _integer(snapshot.get('revision'), 1, 999_999_999)):
        return False
    view, op = snapshot.get('view'), snapshot.get('operation')
    if not isinstance(view, dict) or not isinstance(op, dict):
        return False
    if (not _integer(view.get('task_id'), _TASK_ID, _TASK_ID) or not _role_id(view.get('role_id'))
            or not _integer(view.get('cycle'), 0, _UINT32_MAX)
            or not _integer(view.get('required'), 0, 255)
            or not _integer(view.get('current'), 0, view['required'])
            or not _integer(view.get('money'), 0, (1 << 63) - 1)
            or not _integer(view.get('coin_exchanges'), 0, 20)
            or view.get('next_action') not in ('', 'open', 'experience', 'refine', 'close')
            or not isinstance(view.get('reason'), str)
            or not isinstance(view.get('continuation_reason', ''), str)
            or 'money_cost' not in view or 'gold_cost' not in view
            or _mode(view) not in ('coin', 'experience')
            or not _integer(view.get('practice'), 0, _INT32_MAX)
            or view.get('experience_limit') != 10
            or not _integer(view.get('remaining_exchanges'), 0, 10)
            or view['money_cost'] is not None and not _integer(view['money_cost'], 0 if _mode(view) == 'experience' else 1, _INT32_MAX)
            or view['gold_cost'] is not None and not _integer(view['gold_cost'], 0, 0)):
        return False
    if (_mode(view) == 'experience' or 'experience' in view):
        if (not _integer(view.get('experience'), 0, _INT32_MAX)
                or not _integer(view.get('experience_exchanges'), 0, 10)
                or not _integer(view.get('gold_exchanges'), 0, 10)
                or view['experience_exchanges'] + view['gold_exchanges'] > 10
                or view['remaining_exchanges'] != 10 - view['experience_exchanges'] - view['gold_exchanges']
                or 'experience_cost' not in view
                or view['experience_cost'] is not None and not _integer(view['experience_cost'], 0, _INT32_MAX)):
            return False
    status, operation_id = op.get('status'), op.get('operation_id')
    if (not isinstance(status, str) or status not in _STATUSES or not isinstance(op.get('reason'), str)
            or not isinstance(operation_id, str) or not _integer(op.get('sequence'), 0, 999_999_999)
            or any(type(op.get(key)) is not bool for key in (*_WITNESSES, 'ui_ownership_held'))
            or _mode(op) not in ('coin', 'experience')
            or not _integer(op.get('experience_spent', 0), 0, _INT32_MAX)
            or _mode(op) == 'experience' and 'experience_spent' not in op
            or any(not _integer(op.get(key), 0, _INT32_MAX) for key in ('coins_spent', 'practice_credit'))):
        return False
    if status == 'IDLE':
        return not (operation_id or op['sequence'] or op['ui_ownership_held']
                    or op.get('experience_spent', 0)
                    or any(op[key] for key in (*_WITNESSES, 'coins_spent', 'practice_credit')))
    if op['account_progress_observed']:
        if (_mode(op) == 'coin' and (op['coins_spent'] == 0 or op.get('experience_spent', 0) != 0)
                or _mode(op) == 'experience' and op['coins_spent'] != 0):
            return False
    elif op['coins_spent'] or op.get('experience_spent', 0):
        return False
    return (len(operation_id) == 32 and all(char in '0123456789abcdef' for char in operation_id)
            and op['sequence'] > 0
            and (status not in _HELD_STATES or op['ui_ownership_held'])
            and (status not in ('OPENING', 'OPEN') or not op['mutation_attempted'])
            and (status != 'PENDING' or op['mutation_attempted'] and not op['account_progress_observed'])
            and (status not in ('SETTLED', 'COMPLETED') or op['account_progress_observed'])
            and (status != 'REJECTED' or not op['account_progress_observed'])
            and (not op['panel_closed'] or status == 'COMPLETED' and not op['ui_ownership_held'])
            and all(not op[later] or op[earlier] for earlier, later in zip(_WITNESSES, _WITNESSES[1:]))
            and (op['practice_credit'] > 0) == op['account_progress_observed'])


def _bound(snapshot, operation_id, sequence):
    op = snapshot['operation']
    return op['operation_id'] == operation_id and op['sequence'] == sequence


def _identity(view, role, cycle):
    return view['role_id'] == role and view['cycle'] == cycle and cycle > 0


def _client_busy(view, role):
    return (view['role_id'] in ('0', role) and view['cycle'] == view['required'] == 0
            and view['reason'] == 'refine_client_busy' and not view['next_action']
            and view['money'] == view['coin_exchanges'] == 0
            and view['money_cost'] is None and view['gold_cost'] is None)


def _closed(op):
    return op['status'] == 'COMPLETED' and op['panel_closed'] and not op['ui_ownership_held']


def _completed_gui(wire, sequence):
    state = wire.get('state')
    if not isinstance(state, dict) or state.get('lifecycle') in ('recovery_unknown', 'failed'):
        return False
    gui = state.get('campaign_gui')
    return (isinstance(gui, dict) and _integer(gui.get('command_seq'), sequence, sequence)
            and gui.get('phase') == 'completed' and gui.get('invocation_attempted') is True)


def _preserves(prior, current):
    return (_mode(prior) == _mode(current) and all(not prior[key] or current[key] for key in _WITNESSES)
            and (not prior['account_progress_observed'] or
                 all(prior[key] == current[key] for key in ('coins_spent', 'practice_credit'))
                 and prior.get('experience_spent', 0) == current.get('experience_spent', 0)))


def _continues(prior, op):
    return ((op['status'] == prior['status'] or op['status'] in _TRANSITIONS.get(prior['status'], ()))
            and _preserves(prior, op) and (prior['ui_ownership_held'] or not op['ui_ownership_held'])
            and (prior['status'] != 'COMPLETED' or op['panel_closed'] == prior['panel_closed']))


def _busy_in_stage(view, op, prior, stage, role, quote):
    return (_client_busy(view, role) and
            (op['status'] in _HELD_STATES or stage == 'close' and _closed(prior) and _closed(op))
            and (not op['account_progress_observed'] or _spends(op, quote)))


def _matches_quote(view, quote):
    return (view['current'] == quote.current and view['money'] == quote.money
            and view['coin_exchanges'] == quote.coin_exchanges and _cost(view) == quote.cost
            and _mode(view) == quote.mode and view.get('experience', 0) == quote.experience
            and view.get('experience_exchanges', 0) == quote.experience_exchanges
            and view.get('gold_exchanges', 0) == quote.gold_exchanges
            and view['practice'] == quote.practice)


def _clean_open_rejection(snapshot):
    view, op = snapshot['view'], snapshot['operation']
    if (op['status'] != 'REJECTED' or op['mutation_attempted'] or op['ui_ownership_held']
            or view['gold_cost'] is not None):
        return False
    offer = view['next_action']
    # A rejected command can leave a clean stock entry available.
    return (offer == '' or offer == 'open' and not view['reason'] and _affordable(view))


def _rejection_state_agrees(wire, receipt, baseline, quote):
    if 'state' not in wire:
        return True
    state = wire['state']
    if not isinstance(state, dict) or state.get('lifecycle') in ('recovery_unknown', 'failed'):
        return False
    role, cycle = receipt['view']['role_id'], receipt['view']['cycle']
    operation_id, sequence = receipt['operation']['operation_id'], receipt['operation']['sequence']
    current = state.get('daily_refine')
    if not _valid_snapshot(current) or not _identity(current['view'], role, cycle):
        return False
    older = current['revision'] < receipt['revision']
    if 'campaign_gui' in state:
        gui = state['campaign_gui']
        if (not isinstance(gui, dict) or not _integer(gui.get('command_seq'), 0, sequence)
                or gui.get('phase') == 'unknown'):
            return False
        if gui['command_seq'] == sequence:
            phase, invoked = gui.get('phase'), gui.get('invocation_attempted')
            clean = phase == 'not_invoked' and invoked is False or phase == 'rejected' and invoked is True
            if not clean and not (older and phase in ('queued', 'consumed', 'owned') and invoked is False):
                return False
    # State and acknowledgement are independent atomic publications.
    if older:
        if current == baseline:
            return True
        if current['revision'] <= baseline['revision'] or not _bound(current, operation_id, sequence):
            return False
        op, view = current['operation'], current['view']
        return (_clean_open_rejection(current) or op['status'] == 'QUEUED'
                and not op['mutation_attempted'] and not op['ui_ownership_held']
                and not view['next_action'] and view['gold_cost'] is None
                and _consistent(current, 'open', quote))
    if current['revision'] == receipt['revision']:
        return current == receipt
    return _bound(current, operation_id, sequence) and _clean_open_rejection(current)


def _consistent(snapshot, stage, quote):
    view, op = snapshot['view'], snapshot['operation']
    status = op['status']
    if op['panel_closed'] and stage != 'close' or view['required'] != quote.required:
        return False
    if stage in ('open', 'experience'):
        return not op['mutation_attempted'] and _mode(op) == quote.mode and (status in _TERMINAL or _matches_quote(view, quote))
    experience_route = quote.mode == 'experience'
    protected = (view['money'] == quote.money and view['coin_exchanges'] == quote.coin_exchanges if experience_route else
                 view.get('experience', 0) == quote.experience and view.get('experience_exchanges', 0) == quote.experience_exchanges)
    protected = protected and view.get('gold_exchanges', 0) == quote.gold_exchanges
    debit_observed = protected and (view.get('experience', 0) == quote.experience - quote.cost and
                    view.get('experience_exchanges', 0) == quote.experience_exchanges + 1 if experience_route else
                    view['money'] == quote.money - quote.cost and view['coin_exchanges'] == quote.coin_exchanges + 1)
    if op['account_progress_observed']:
        return (debit_observed and view['current'] == min(quote.current + 1, quote.required)
                and view['practice'] == quote.practice + op['practice_credit'] and _spends(op, quote))
    if status != 'PENDING':
        return True
    unchanged = (protected and view['money'] == quote.money and view['coin_exchanges'] == quote.coin_exchanges
                 and (view.get('experience', 0) == quote.experience if experience_route else
                      view.get('experience', 0) == quote.experience)
                 and view.get('experience_exchanges', 0) == quote.experience_exchanges)
    return (quote.current <= view['current'] <= min(quote.current + 1, quote.required)
            and (debit_observed if op['stock_callback_returned'] else unchanged or debit_observed))


def _command_state(wire, receipt, stage, quote, role, cycle):
    if 'state' not in wire:
        return receipt
    state = wire['state']
    if not isinstance(state, dict) or state.get('lifecycle') in ('recovery_unknown', 'failed'):
        return None
    prior = receipt['operation']
    if 'campaign_gui' in state and not _completed_gui(wire, prior['sequence']):
        return None
    current = state.get('daily_refine')
    if not _valid_snapshot(current):
        return None
    if current['revision'] < receipt['revision']:
        return receipt
    if current['revision'] == receipt['revision']:
        return receipt if current == receipt else None
    view, op = current['view'], current['operation']
    if (not _bound(current, prior['operation_id'], prior['sequence'])
            or op['status'] == 'UNKNOWN' or not _continues(prior, op)):
        return None
    if (_identity(view, role, cycle) and _consistent(current, stage, quote)
            or _busy_in_stage(view, op, prior, stage, role, quote)):
        return current
    return None


class DailyRefineExecutor:
    """Settle fresh exchanges in one owned panel, then verify its final close."""

    def __init__(self, objects, token, *, stopped=lambda: False, paused=lambda: False,
                 wait=None, timeout=22.0, clock=time.monotonic):
        self.objects, self.token = objects, token
        self.stopped, self.paused = stopped, paused
        self.wait = wait if wait is not None else lambda: time.sleep(.1)
        self.timeout, self.clock = timeout, clock

    def execute(self, task_id: int, *, character_id: str, expected_cycle: int | None = None,
                expected_current: int | None = None, expected_required: int | None = None) -> DailyRefineResult:
        return self._execute_once(task_id, character_id=character_id,
            expected_cycle=expected_cycle, expected_current=expected_current, expected_required=expected_required)

    def _execute_once(self, task_id: int, *, character_id: str, expected_cycle: int | None = None,
                      expected_current: int | None = None, expected_required: int | None = None) -> DailyRefineResult:
        if type(task_id) is not int or task_id != _TASK_ID:
            raise ValueError('daily_refine_task_unsupported')
        if (not _role_id(character_id) or character_id == '0'
                or expected_cycle is not None and not _integer(expected_cycle, 1, _UINT32_MAX)
                or expected_current is not None and not _integer(expected_current, 0, 255)
                or expected_required is not None and not _integer(expected_required, 1, 255)):
            return DailyRefineResult('BLOCKED', 'refine_identity_invalid')

        operation_id = uuid.uuid4().hex
        cycle = expected_cycle
        quote = previous = None
        quote_revision = None
        stage, sequence = '', 0
        evidence = {}
        submitted = mutation_unverified = False
        waiting_initial_busy = False
        observed_experience = None
        observed_practice = None
        first_quote = None
        attempts = []
        round_recorded = False
        closing_reason = ''
        resource_refusal = ''
        deadline = self.clock() + self.timeout

        def round_result(status, reason, *, closed=False, after=None, cleanup=None):
            progress = evidence.get('account_progress_observed', False)
            if cleanup is None:
                cleanup = 'VERIFIED_CLOSED' if closed else 'UNVERIFIED' if submitted or status == 'UNKNOWN' else 'NOT_NEEDED'
            result = DailyRefineResult(status=status, reason=reason, cycle=cycle,
                progress_before=quote.current if quote else None, required=quote.required if quote else None,
                progress_after=min(quote.current + 1, quote.required) if progress else after,
                money_before=quote.money if quote else None,
                money_after=quote.money - (quote.cost if quote.mode == 'coin' else 0) if quote else None,
                coin_exchanges_before=quote.coin_exchanges if quote else None,
                coin_exchanges_after=quote.coin_exchanges + int(progress and quote.mode == 'coin') if quote else None,
                coins_spent=evidence['coins_spent'] if progress else None,
                practice_credit=evidence['practice_credit'] if progress else None,
                stock_callback_returned=evidence.get('stock_callback_returned', False),
                account_progress_observed=progress,
                mutation_attempted=None if mutation_unverified else evidence.get('mutation_attempted', False),
                ui_ownership_held=evidence.get('ui_ownership_held'), cleanup=cleanup,
                mode=quote.mode if quote else 'experience', experience_before=quote.experience if quote else None,
                experience_after=observed_experience if quote else None,
                experience_exchanges_before=quote.experience_exchanges if quote else None,
                experience_exchanges_after=quote.experience_exchanges + int(progress and quote.mode == 'experience') if quote else None,
                experience_spent=evidence.get('experience_spent', 0) if progress else None,
                gold_exchanges_before=quote.gold_exchanges if quote else None,
                gold_exchanges_after=quote.gold_exchanges if quote else None,
                practice_before=quote.practice if quote else None,
                practice_after=observed_practice if quote else None,
                remaining_exchanges=(10 - quote.experience_exchanges - quote.gold_exchanges - int(progress)) if quote else None,
                checklist_complete=status == 'ALREADY_COMPLETE', operation_id=operation_id, sequence=sequence,
                quote_revision=quote_revision, final_revision=previous['revision'] if previous else None)
            return result

        def finish(status, reason, *, closed=False, after=None, cleanup=None):
            result = round_result(status, reason, closed=closed, after=after, cleanup=cleanup)
            baseline = first_quote or quote
            complete = (status == 'ALREADY_COMPLETE' or status == 'COMPLETED' and closed
                        and bool(attempts) and attempts[-1].remaining_exchanges == 0)
            # Each attempt is a separately observed settlement. Panel cleanup
            # belongs to this session result, never to an intermediate receipt.
            return replace(result,
                progress_before=baseline.current if baseline else None,
                money_before=baseline.money if baseline else None,
                coin_exchanges_before=baseline.coin_exchanges if baseline else None,
                experience_before=baseline.experience if baseline else None,
                experience_exchanges_before=baseline.experience_exchanges if baseline else None,
                gold_exchanges_before=baseline.gold_exchanges if baseline else None,
                practice_before=baseline.practice if baseline else None,
                coins_spent=0, experience_spent=sum(a.experience_spent for a in attempts),
                practice_credit=sum(a.practice_credit for a in attempts),
                progress_after=result.progress_after if result.progress_after is not None else
                    attempts[-1].progress_after if attempts else after,
                exchanges_completed=len(attempts), attempts=tuple(attempts), checklist_complete=complete,
                stock_callback_returned=bool(attempts) and all(a.stock_callback_returned for a in attempts),
                account_progress_observed=bool(attempts),
                mutation_attempted=None if mutation_unverified else
                    result.mutation_attempted or bool(attempts),
                ui_ownership_held=False if closed or status == 'ALREADY_COMPLETE' else result.ui_ownership_held)

        def fail(reason):
            return finish('STOPPED', 'user_stop') if self.stopped() else finish('UNKNOWN', reason)

        while True:
            if self.stopped():
                return finish('STOPPED', 'user_stop')
            if not stage and self.paused():
                self.wait()
                deadline = self.clock() + self.timeout
                continue
            if self.clock() >= deadline:
                if waiting_initial_busy and not submitted:
                    return finish('UNAVAILABLE', 'refine_client_busy')
                return fail('refine_readback_timeout')
            try:
                observed = self.objects.observe_daily_refine(self.token)
            except Exception as exc:
                return fail(f'refine_observation_failed ({exc})')
            if self.stopped():
                return finish('STOPPED', 'user_stop')
            if not isinstance(observed, dict):
                return fail('refine_observation_invalid')
            if observed.get('enabled') is False:
                return fail('refine_disabled_during_operation') if stage else finish('UNAVAILABLE', 'refine_disabled')
            snapshot = observed.get('refine')
            if observed.get('enabled') is not True or not _valid_snapshot(snapshot):
                return fail('refine_observation_invalid')

            revision, view, op = snapshot['revision'], snapshot['view'], snapshot['operation']
            status, offer = op['status'], view['next_action']
            reason = op['reason'] or view['reason']
            if previous is not None and (revision < previous['revision']
                    or revision == previous['revision'] and snapshot != previous):
                return fail('refine_observation_revision_changed')
            prior = previous['operation'] if previous is not None else None
            if (not submitted and prior is not None and (_closed(prior) or
                    _closed(op) and _client_busy(previous['view'], character_id))
                    and (not _closed(op) or not _bound(snapshot, prior['operation_id'], prior['sequence'])
                         or not _preserves(prior, op))):
                return fail('refine_operation_binding_or_state_changed')
            if (not submitted and _client_busy(view, character_id)
                    and (status == 'IDLE' and view['role_id'] == '0' or _closed(op))):
                waiting_initial_busy = True
                previous = snapshot
                if self.clock() >= deadline:
                    return finish('UNAVAILABLE', 'refine_client_busy')
                self.wait()
                continue
            if self.clock() >= deadline:
                return fail('refine_readback_timeout')
            if stage:
                if not _bound(snapshot, operation_id, sequence) or not _continues(prior, op):
                    return fail('refine_operation_binding_or_state_changed')
                if _busy_in_stage(view, op, prior, stage, character_id, quote):
                    previous = snapshot
                    self.wait()
                    continue
            elif (status not in ('IDLE', 'COMPLETED', 'REJECTED') or op['ui_ownership_held']
                    or op['mutation_attempted'] and not op['panel_closed']):
                return fail('refine_prior_operation_unclosed')

            if cycle is None:
                cycle = view['cycle']
            if not _identity(view, character_id, cycle) or view['required'] == 0:
                return fail('refine_observation_identity_changed')
            if stage:
                if not _consistent(snapshot, stage, quote):
                    return fail('refine_operation_resources_or_progress_changed')
                evidence = op
            observed_experience = view.get('experience')
            observed_practice = view['practice']
            previous = snapshot
            if status == 'UNKNOWN':
                return fail(reason or 'refine_native_operation_unknown')

            if not stage:
                before, required, cost = view['current'], view['required'], _cost(view)
                if (expected_required is not None and required != expected_required
                        or expected_current is not None and before != expected_current):
                    return fail('refine_daily_cache_changed')
                quote = _Quote(before, required, view['money'], view['coin_exchanges'], cost or 0,
                               _mode(view), view.get('experience', 0), view.get('experience_exchanges', 0),
                               view.get('gold_exchanges', 0), view['practice'])
                first_quote = quote
                quote_revision = revision
                if _mode(view) != 'experience':
                    return finish('UNAVAILABLE', 'refine_coin_route_not_authorized', after=before)
                if (view['experience_exchanges'] + view['gold_exchanges'] == 10
                        and not offer and view['reason'] == 'refine_experience_limit'):
                    return finish('ALREADY_COMPLETE', view['reason'], after=before)
                if (offer != 'open' or view['reason'] or view['gold_cost'] is not None
                        or not _affordable(view)):
                    return finish('UNAVAILABLE', view['reason'] or 'refine_open_unavailable', after=before)
                action = 'open'
            elif status == 'REJECTED':
                return finish('REJECTED', reason or 'refine_operation_rejected')
            elif status == 'COMPLETED':
                if stage == 'close' and _closed(prior) and _closed(op):
                    if resource_refusal:
                        return finish('BLOCKED', resource_refusal, closed=True)
                    if attempts and attempts[-1].remaining_exchanges == 0:
                        return finish('COMPLETED', 'refine_experience_limit_verified', closed=True)
                    return finish('UNAVAILABLE', closing_reason or 'refine_exchanges_remaining', closed=True)
                return finish('BLOCKED', reason or 'refine_close_unconfirmed')
            elif stage == 'open' and status == 'OPEN' and offer == 'experience':
                if view['reason'] or view['gold_cost'] is not None or not _matches_quote(view, quote):
                    return fail('refine_experience_tab_offer_invalid')
                action = 'experience'
            elif stage in ('open', 'experience') and status == 'OPEN' and offer == 'refine':
                if (view['reason'] or not _integer(view['gold_cost'], 0, 0) or _mode(view) != quote.mode
                        or _cost(view) != quote.cost or view['money_cost'] != (quote.cost if quote.mode == 'coin' else 0)):
                    return fail('refine_ordinary_resource_quote_invalid')
                action = 'refine'
            elif stage == 'refine' and status == 'SETTLED':
                if not round_recorded:
                    settled = replace(round_result('COMPLETED', 'refine_exchange_verified', cleanup='PANEL_HELD'),
                                      exchanges_completed=1)
                    if (len(attempts) >= 10 or settled.ui_ownership_held is not True
                            or settled.mode != 'experience' or not settled.stock_callback_returned
                            or not settled.account_progress_observed or settled.mutation_attempted is not True
                            or settled.experience_exchanges_after != settled.experience_exchanges_before + 1
                            or settled.gold_exchanges_after != settled.gold_exchanges_before
                            or settled.money_after != settled.money_before
                            or settled.coin_exchanges_after != settled.coin_exchanges_before
                            or settled.experience_after != settled.experience_before - settled.experience_spent
                            or settled.practice_after != settled.practice_before + settled.practice_credit
                            or settled.progress_after != min(settled.progress_before + 1, settled.required)):
                        return fail('refine_exchange_evidence_incomplete')
                    attempts.append(settled)
                    round_recorded = True
                if resource_refusal:
                    # The existing native close accepts a settled exchange
                    # even while its next offered button is another refine.
                    closing_reason = resource_refusal
                    action = 'close'
                elif offer == 'refine':
                    if (view['reason'] or not _integer(view['gold_cost'], 0, 0)
                            or _mode(view) != 'experience' or not _affordable(view)
                            or len(attempts) >= 10):
                        return fail('refine_next_exchange_quote_invalid')
                    action = 'refine'
                elif offer == 'close':
                    if view['reason'] or view['gold_cost'] is not None:
                        return fail('refine_close_offer_invalid')
                    closing_reason = view.get('continuation_reason', '')
                    action = 'close'
                elif offer:
                    return fail('refine_unexpected_action_offer')
                else:
                    self.wait()
                    continue
            else:
                if offer:
                    return fail('refine_unexpected_action_offer')
                self.wait()
                continue

            if self.stopped():
                return finish('STOPPED', 'user_stop')
            if self.paused():
                self.wait()
                continue
            if self.clock() >= deadline:
                return fail('refine_action_wait_timeout')
            continuing_round = action == 'refine' and stage == 'refine' and status == 'SETTLED'
            command_quote = (_Quote(view['current'], view['required'], view['money'], view['coin_exchanges'],
                _cost(view), _mode(view), view['experience'], view['experience_exchanges'],
                view['gold_exchanges'], view['practice']) if continuing_round else quote)
            try:
                wire = self.objects.submit_daily_refine(self.token, action, expected_revision=revision,
                    character_id=character_id, task_id=task_id, operation_id=operation_id)
            except ResourcePolicyDenied as exc:
                resource_refusal = str(exc)
                if stage == 'refine' and status == 'SETTLED' and not mutation_unverified:
                    continue
                # An opened panel before its first exchange has no native
                # cancel command. Do not spend merely to make it closable.
                return finish('BLOCKED', resource_refusal +
                    ('; refine_cancel_capability_unavailable' if submitted else ''))
            except CampaignAdmissionClosed:
                # The object controller raises this before invoking the bridge.
                if self.stopped():
                    return finish('STOPPED', 'user_stop')
                if self.paused():
                    self.wait()
                    continue
                return finish('BLOCKED', 'refine_command_admission_closed') if not submitted else fail('refine_command_admission_closed')
            except Exception as exc:
                submitted = True
                mutation_unverified = action == 'refine'
                return fail(f'refine_command_outcome_unknown ({exc})')
            submitted = True
            mutation_unverified = action == 'refine'

            ack = wire.get('ack') if isinstance(wire, dict) else None
            sent_seq = wire.get('requested_seq') if isinstance(wire, dict) else None
            if (not isinstance(ack, dict) or not _integer(sent_seq, op['sequence'] + 1, 999_999_999)
                    or not _integer(ack.get('command_seq'), sent_seq, sent_seq)
                    or ack.get('command_name') != 'daily-refine-command'):
                return fail('refine_command_binding_invalid')
            details = ack.get('result')
            receipt = details.get('daily_refine') if isinstance(details, dict) else None
            bound = (_valid_snapshot(receipt) and receipt['revision'] > revision
                     and _bound(receipt, operation_id, sent_seq)
                     and (action == 'open' or continuing_round and
                          receipt['operation']['ui_ownership_held'] and not receipt['operation']['panel_closed']
                          and receipt['operation']['mutation_attempted']
                          or not continuing_round and _preserves(op, receipt['operation']))
                     and (_identity(receipt['view'], character_id, cycle) and _consistent(receipt, action, command_quote)
                          or action == 'close' and _closed(receipt['operation'])
                          and _client_busy(receipt['view'], character_id) and _completed_gui(wire, sent_seq)))
            prior_evidence = evidence
            if bound and receipt['operation']['status'] in (_ACK_PHASES[action] | {'UNKNOWN', 'REJECTED'}):
                evidence = receipt['operation']
                mutation_unverified = False
                if action == 'refine':
                    quote, quote_revision = command_quote, revision
                    round_recorded = False
            if ack.get('status') != 'COMPLETED':
                why = ack.get('reason')
                if ack.get('status') == 'REJECTED' and not stage and action == 'open' and bound and _clean_open_rejection(receipt):
                    if not _rejection_state_agrees(wire, receipt, snapshot, quote):
                        evidence = {}
                        mutation_unverified = True
                        return finish('UNKNOWN', 'refine_rejection_state_conflict')
                    if self.stopped():
                        return finish('STOPPED', 'user_stop', cleanup='NOT_NEEDED')
                    return finish('BLOCKED', why if isinstance(why, str) and why else
                                  evidence['reason'] or 'refine_open_rejected', cleanup='NOT_NEEDED')
                return fail(why if isinstance(why, str) and why else 'refine_command_outcome_unknown')
            if not bound or receipt['operation']['status'] not in _ACK_PHASES[action]:
                return fail('refine_command_binding_invalid')
            latest = _command_state(wire, receipt, action, command_quote, character_id, cycle)
            if latest is None:
                evidence = {} if action == 'refine' else prior_evidence
                if action == 'refine':
                    observed_experience = observed_practice = None
                mutation_unverified = action == 'refine'
                return finish('UNKNOWN', 'refine_command_state_conflict')
            if _identity(latest['view'], character_id, cycle):
                evidence = latest['operation']
                observed_experience = latest['view'].get('experience')
                observed_practice = latest['view']['practice']
            if self.clock() >= deadline:
                return fail('refine_command_reply_timed_out')

            # A GUI receipt binds the action. A separate observation is
            # required before sending the ordinary-resource request, closing, or returning success.
            stage, sequence, previous = action, sent_seq, latest
            deadline = self.clock() + self.timeout
