"""Execute one native daily category reward under the caller's bridge owner."""
from __future__ import annotations

from dataclasses import dataclass
import time
import uuid

from .bridge_client import _reward_valid
from .objects import CampaignAdmissionClosed


_COMMAND = 'daily-reward-command'
_WITNESS_FLAGS = ('invoked', 'callback_returned', 'settled', 'reward_closed')
_RECEIPT_FIELDS = ('receipt_category', 'receipt_refresh')


@dataclass(frozen=True)
class DailyRewardResult:
    status: str
    reason: str
    category_id: int
    character_id: str
    operation_id: str = ''
    revision: int = 0
    sequence: int = 0
    claim_sequence: int = 0
    refresh_epoch: int = 0
    invoked: bool = False
    unresolved: bool = False
    callback_returned: bool = False
    settled: bool = False
    reward_closed: bool = False
    panel_closed: bool = False
    receipt_category: int = 0
    receipt_refresh: int = 0


def _command_reply(wire, after_sequence):
    """Correlate the GUI invocation acknowledgement to the submitted command."""
    if not isinstance(wire, dict) or wire.get('command') != _COMMAND:
        return None
    sequence = wire.get('requested_seq')
    ack, state = wire.get('ack'), wire.get('state')
    if (type(sequence) is not int or sequence <= after_sequence
            or not isinstance(ack, dict) or ack.get('command_seq') != sequence
            or ack.get('command_name') != _COMMAND or not isinstance(state, dict)
            or state.get('last_command_seq') != sequence):
        return None
    gui = state.get('campaign_gui')
    if not isinstance(gui, dict):
        return None
    gui_sequence = gui.get('command_seq')
    if type(gui_sequence) is not int or gui_sequence > sequence:
        return None
    # The final ACK can precede publication of the GUI's not_invoked phase.
    if ack.get('status') == 'REJECTED' and (gui_sequence < sequence
            or gui.get('invocation_attempted') is False
            and gui.get('phase') in ('queued', 'consumed', 'owned', 'not_invoked')):
        return sequence, None, ack.get('reason') or 'daily_reward_command_rejected'
    reply = ack.get('result')
    if (ack.get('status') != 'COMPLETED' or gui_sequence != sequence
            or gui.get('phase') != 'completed' or gui.get('invocation_attempted') is not True
            or not isinstance(reply, dict) or reply.get('daily_reward_enabled') is not True
            or not _reward_valid(reply.get('daily_reward'))):
        return None
    return sequence, reply['daily_reward'], ''


def _client_busy(state):
    # The native observer reads the role before taking the client monitor.
    # While busy it publishes no epoch or categories and offers no action.
    return (state['refresh_epoch'] == 0 and not state['next_action']
            and all(item['reason'] == 'daily_reward_client_busy' and item['claimed'] is None
                    for item in state['categories']))


class DailyRewardExecutor:
    """Drive category rewards without repeating a claim that may have been invoked."""

    def __init__(self, objects, token, *, stopped=lambda: False, paused=lambda: False,
                 wait=None, publish=None, timeout=95.0, clock=time.monotonic):
        self.objects, self.token = objects, token
        self.stopped, self.paused = stopped, paused
        self.wait = wait if wait is not None else lambda: time.sleep(.1)
        self.publish = publish if publish is not None else lambda result: None
        self.timeout, self.clock = timeout, clock
        self._unresolved = False

    def execute(self, category_id: int, *, character_id: str) -> DailyRewardResult:
        if type(category_id) is not int or category_id not in range(2, 7):
            raise ValueError('daily_reward_category_unsupported')
        if self._unresolved:
            return DailyRewardResult('UNKNOWN', 'daily_reward_previous_operation_unresolved',
                                     category_id, character_id)

        operation_id = ''
        epoch = claim_sequence = sequence = rejected_sequence = 0
        last = None
        sent = set()
        rejection = clean_close_reason = ''
        rejected_revision = None
        recovered_panel = False
        recovered_epoch = 0
        deadline = self.clock() + self.timeout

        def report(status, reason, *, panel_closed=False):
            if status == 'UNKNOWN':
                self._unresolved = True
            evidence = last if last is not None else {}
            result = DailyRewardResult(status, reason, category_id, character_id,
                operation_id=operation_id, revision=evidence.get('revision', 0),
                sequence=evidence.get('sequence', 0), claim_sequence=claim_sequence,
                refresh_epoch=epoch, panel_closed=panel_closed,
                **{key: evidence.get(key, False) for key in (*_WITNESS_FLAGS, 'unresolved')},
                receipt_category=evidence.get('receipt_category', 0),
                receipt_refresh=evidence.get('receipt_refresh', 0))
            self.publish(result)
            return result

        def matches(state):
            if (state['character_id'] != character_id or state['operation_id'] != operation_id
                    or state['category_id'] != category_id or state['sequence'] not in (sequence, rejected_sequence)
                    or state['claim_sequence'] != claim_sequence or state['invoked'] != bool(claim_sequence)
                    or state['refresh_epoch'] != epoch and not _client_busy(state)):
                return False
            if not claim_sequence and any(state[key] for key in (*_WITNESS_FLAGS, *_RECEIPT_FIELDS)):
                return False
            return (last is None or state['revision'] >= last['revision'] and state['sequence'] >= last['sequence']
                    and (state['revision'] > last['revision']
                         or (state['status'], state['next_action']) == (last['status'], last['next_action']))
                    and all(not last[key] or state[key] for key in _WITNESS_FLAGS)
                    and all(not last[key] or state[key] == last[key] for key in _RECEIPT_FIELDS))

        while True:
            if not sent and self.stopped():
                return report('STOPPED', 'user_stop', panel_closed=recovered_panel)
            if self.clock() >= deadline:
                return report('UNKNOWN' if sent else 'BLOCKED', 'daily_reward_observation_timeout',
                              panel_closed=recovered_panel and not sent)
            try:
                observation = self.objects.observe_daily_reward(self.token)
            except Exception as exc:
                return report('UNKNOWN' if sent else 'BLOCKED', f'daily_reward_observation_failed ({exc})')
            if not isinstance(observation, dict) or type(observation.get('enabled')) is not bool:
                return report('UNKNOWN' if sent else 'BLOCKED', 'daily_reward_observation_invalid')
            if not observation['enabled']:
                return report('UNKNOWN' if sent else 'DISABLED', 'daily_reward_disabled')
            state = observation.get('reward')
            if not _reward_valid(state):
                return report('UNKNOWN' if sent else 'BLOCKED', 'daily_reward_observation_invalid')
            status, action, reason = state['status'], state['next_action'], state['reason']
            category = state['categories'][category_id - 2]

            if not sent:
                if status == 'UNKNOWN' or state['unresolved'] or state['invoked'] and status != 'COMPLETED':
                    return report('UNKNOWN', reason or 'daily_reward_previous_operation_unresolved')
                if not character_id or state['character_id'] and state['character_id'] != character_id:
                    return report('BLOCKED', 'daily_reward_character_changed')
                if not state['character_id']:
                    return report('BLOCKED', category['reason'] or reason or 'daily_reward_role_unavailable')
                if recovered_panel and _client_busy(state):
                    self.wait()
                    continue
                if recovered_panel and state['refresh_epoch'] != recovered_epoch:
                    return report('BLOCKED', 'daily_reward_recovery_cycle_changed', panel_closed=True)
                # A live native operation retains its UI owner and request history.
                # Attach only before any claim invocation or receipt witness.
                if (status in ('READY', 'WAITING') and action in ('', 'claim', 'close')
                        and state['operation_id'] and state['category_id'] == category_id and state['sequence'] > 0
                        and state['refresh_epoch'] > 0 and not state['claim_sequence']
                        and not any(state[key] for key in (*_WITNESS_FLAGS, *_RECEIPT_FIELDS))):
                    operation_id, epoch, sequence = state['operation_id'], state['refresh_epoch'], state['sequence']
                    sent.add('open')
                    last = state
                    self.wait()
                    continue
                if category['claimed'] is True and category['reason'] == 'daily_reward_already_claimed':
                    return report('ALREADY_CLAIMED', category['reason'], panel_closed=recovered_panel)
                if (status not in ('READY', 'COMPLETED', 'REJECTED') or action != 'open'
                        or not category['ready'] or category['claimed'] is not False
                        or category['reason'] or not state['revision'] or not state['refresh_epoch']):
                    return report('BLOCKED', category['reason'] or reason or 'daily_reward_open_unavailable')
            else:
                if not matches(state):
                    return report('UNKNOWN', 'daily_reward_operation_binding_changed')
                last = state
                if status == 'UNKNOWN':
                    return report('UNKNOWN', reason or 'daily_reward_native_outcome_unknown')
                if status in ('REJECTED', 'STOPPED'):
                    if claim_sequence or state['unresolved']:
                        return report('UNKNOWN', reason or 'daily_reward_invoked_without_completion')
                    closed_without_claim = ('close' in sent and reason == clean_close_reason
                                            and not any(state[key] for key in (*_WITNESS_FLAGS, *_RECEIPT_FIELDS)))
                    if (status == 'REJECTED' and reason == 'daily_reward_recover_existing_panel'
                            and closed_without_claim and not self.stopped()):
                        if recovered_panel:
                            return report('BLOCKED', 'daily_reward_panel_reopened', panel_closed=True)
                        # Closing is not reward evidence. Start afresh only after
                        # another observation confirms the current role, cycle,
                        # category state and native idle/open admission gate.
                        recovered_panel, recovered_epoch = True, epoch
                        operation_id = ''
                        epoch = claim_sequence = sequence = rejected_sequence = 0
                        rejection = clean_close_reason = ''
                        rejected_revision = None
                        last = None
                        sent.clear()
                        self.wait()
                        continue
                    outcome = 'STOPPED' if status == 'STOPPED' or self.stopped() else 'BLOCKED'
                    return report(outcome, rejection or reason or 'daily_reward_operation_rejected',
                                  panel_closed=action == 'open' or closed_without_claim)

                busy = _client_busy(state)
                claimed = (category['claimed'] is True and not category['ready']
                           and category['reason'] == 'daily_reward_already_claimed')
                receipt = (state['callback_returned'] and state['settled']
                           and state['receipt_category'] == category_id and state['receipt_refresh'] > epoch
                           and (claimed or busy))
                if status == 'COMPLETED' and not busy:
                    if ('close' in sent and 'reward_confirm' in sent and receipt and state['reward_closed']
                            and not state['unresolved'] and action in ('', 'open')):
                        return report('COMPLETED', reason or 'daily_reward_claim_confirmed', panel_closed=True)
                    return report('UNKNOWN', 'daily_reward_completion_evidence_missing')
                if (state['unresolved'] != (bool(claim_sequence) and status != 'COMPLETED')
                        or state['settled'] and not receipt
                        or state['reward_closed'] and 'reward_confirm' not in sent):
                    return report('UNKNOWN', 'daily_reward_witness_inconsistent')

                waiting = not action and (busy or status == ('CLOSING' if 'close' in sent else 'WAITING'))
                can_claim = status == 'READY' and action == 'claim' and not claim_sequence and 'close' not in sent
                can_confirm = (status == 'READY' and action == 'reward_confirm' and claim_sequence
                               and receipt and action not in sent)
                can_close = (status == 'READY' and action == 'close' and action not in sent
                             and (not claim_sequence or 'reward_confirm' in sent and receipt and state['reward_closed']))
                if can_claim:
                    if rejection:
                        # A stale admission rejection did not invoke the claim.
                        # Only a newer, independently observed offer with this
                        # exact operation binding and no claim witnesses can retry.
                        if (rejected_revision is None or state['revision'] <= rejected_revision
                                or state['unresolved'] or any(state[key] for key in (*_WITNESS_FLAGS, *_RECEIPT_FIELDS))):
                            return report('BLOCKED', rejection)
                        rejection, rejected_revision = '', None
                    if not category['ready'] or category['claimed'] is not False or category['reason']:
                        return report('UNKNOWN', 'daily_reward_claim_offer_inconsistent')
                elif not (waiting or can_confirm or can_close):
                    return report('UNKNOWN', 'daily_reward_action_out_of_order')

            if self.stopped():
                return report('UNKNOWN' if claim_sequence else 'STOPPED',
                              'daily_reward_unresolved_after_stop' if claim_sequence else 'user_stop')
            if self.clock() >= deadline:
                return report('UNKNOWN' if sent else 'BLOCKED', 'daily_reward_observation_timeout')
            if not action or self.paused():
                self.wait()
                continue

            if action == 'open':
                operation_id, epoch = uuid.uuid4().hex, state['refresh_epoch']
            if action == 'close' and not claim_sequence:
                clean_close_reason = reason or 'daily_reward_closed_without_claim'
            report('RUNNING', f'daily_reward_{action}')
            # The object controller's admission lock covers the remaining race.
            if self.stopped() or self.paused() or self.clock() >= deadline:
                continue
            try:
                wire = self.objects.submit_daily_reward(self.token, action, expected_revision=state['revision'],
                    character_id=character_id, category_id=category_id, operation_id=operation_id)
            except CampaignAdmissionClosed:
                if claim_sequence:
                    return report('UNKNOWN', 'daily_reward_command_admission_closed_with_pending_claim')
                return report('STOPPED' if self.stopped() else 'BLOCKED', 'daily_reward_command_admission_closed')
            except Exception as exc:
                return report('UNKNOWN', f'daily_reward_command_outcome_unknown ({exc})')
            reply = _command_reply(wire, state['sequence'])
            if reply is None:
                return report('UNKNOWN', 'daily_reward_command_outcome_unconfirmed')
            requested_sequence, invoked_state, rejected_reason = reply
            if invoked_state is None:
                if action == 'claim':
                    rejection, rejected_sequence = rejected_reason, requested_sequence
                    rejected_revision = (state['revision'] if not claim_sequence and rejected_reason in
                                         ('daily_reward_revision_changed', 'daily_reward_action_changed',
                                          'daily_reward_command_identity_or_revision') else None)
                    self.wait()
                    continue  # Reobserve before a safe retry or an offered close.
                if claim_sequence:
                    return report('UNKNOWN', rejected_reason)
                return report('STOPPED' if self.stopped() else 'BLOCKED', rejected_reason)

            sent.add(action)
            sequence, rejected_sequence = requested_sequence, 0
            if action == 'claim':
                claim_sequence = sequence
            if invoked_state['revision'] <= state['revision'] or not matches(invoked_state):
                return report('UNKNOWN', 'daily_reward_invocation_binding_changed')
            last = invoked_state
