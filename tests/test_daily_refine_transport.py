from copy import deepcopy
import threading
from types import SimpleNamespace
import unittest
from unittest.mock import Mock

from jiangshen_assistant import bridge_client as bridge
from jiangshen_assistant.objects import ObjectController, CampaignAdmissionClosed
from tests import test_bridge_client as fixtures

OP = '1234567890abcdef1234567890abcdef'


def refine(sequence=0, operation_id='', status='IDLE'):
    return dict(schema=1, revision=2,
        view=dict(task_id=21, role_id='12345', cycle=100000, current=0, required=3,
                  money=5000, money_cost=1000, coin_exchanges=0, gold_cost=None, next_action='', reason=''),
        operation=dict(status=status, reason='', operation_id=operation_id, sequence=sequence,
                       mutation_attempted=False, stock_callback_returned=False,
                       account_progress_observed=False, panel_closed=False, ui_ownership_held=False,
                       practice_credit=0, coins_spent=0))


class RefineTransportTests(unittest.TestCase):
    setUp = fixtures.BridgeTests.setUp
    client = fixtures.BridgeTests.client
    common = fixtures.BridgeTests.common
    state = fixtures.BridgeTests.state
    ack = fixtures.BridgeTests.ack

    def respond(self, client, value, mutate=None):
        original = client._send
        def send(name, **kwargs):
            seq = original(name, **kwargs)
            state = self.state(client, seq)
            state.update(daily_refine=deepcopy(value), daily_refine_enabled=True,
                         campaign_gui=dict(command_seq=seq, phase='completed', invocation_attempted=True))
            ack = self.ack(client, seq, name, state, 'COMPLETED')
            ack['result'].update(daily_refine=deepcopy(value), daily_refine_enabled=True)
            if mutate:
                mutate(state, ack)
            bridge._atomic_json(client.session_dir / 'state.json', state)
            bridge._atomic_json(client.session_dir / 'ack.json', ack)
            return seq
        client._send = Mock(side_effect=send)

    def test_disabled_or_missing_marker_never_publishes(self):
        client = self.client(); client.connect(timeout=.2)
        for marker in (False, None):
            if marker is None:
                client._last_state.pop('daily_refine_enabled', None)
            else:
                client._last_state['daily_refine_enabled'] = marker
            with self.assertRaises(bridge.BridgeError):
                client.daily_refine_observe(timeout=.2)
            with self.assertRaises(bridge.BridgeError):
                client.request('daily-refine-observe', timeout=.2)
            with self.assertRaises(bridge.BridgeError):
                client.daily_refine_command('open', expected_revision=1, character_id='12345',
                                            task_id=21, operation_id=OP, timeout=.2)
        self.assertEqual(client._seq, 0)

    def test_fixed_command_and_bound_receipt(self):
        client = self.client(); client.connect(timeout=.2)
        client._last_state['daily_refine_enabled'] = True
        self.respond(client, refine(1, OP, 'OPENING'))
        wire = client.daily_refine_command('open', expected_revision=1, character_id='12345',
                                             task_id=21, operation_id=OP, timeout=.2)
        self.assertEqual(wire['ack']['result']['daily_refine']['operation']['operation_id'], OP)
        self.assertEqual((client.session_dir / 'command.txt').read_text().splitlines(),
            ['version=3', 'nonce=' + client.nonce, 'sequence=1', 'command=daily-refine-command',
             'action=open', 'operation_id=' + OP, 'character_id=12345', 'task_id=21', 'expected_revision=1'])

    def test_foreign_operation_and_nonzero_cycle_are_not_accepted(self):
        client = self.client(); client.connect(timeout=.2)
        client._last_state['daily_refine_enabled'] = True
        def change(state, ack):
            state['daily_refine']['revision'] += 1
            state['daily_refine']['view']['cycle'] += 86400
            state['daily_refine']['operation'].update(operation_id='f' * 32, sequence=1, status='OPEN')
        self.respond(client, refine(), mutate=change)
        with self.assertRaises(bridge.BridgeUnknown):
            client.daily_refine_observe(timeout=.2)

    def test_initial_busy_can_recover_to_bound_role(self):
        client = self.client(); client.connect(timeout=.2)
        client._last_state['daily_refine_enabled'] = True
        busy = refine()
        busy['view'].update(role_id='0', cycle=0, current=0, required=0, money=0,
                            money_cost=None, reason='refine_client_busy')
        def recover(state, ack):
            state['daily_refine'] = refine()
            state['daily_refine']['revision'] += 1
        self.respond(client, busy, mutate=recover)
        wire = client.daily_refine_observe(timeout=.2)
        self.assertEqual(wire['state']['daily_refine']['view']['role_id'], '12345')

    def test_completed_busy_receipt_requires_later_readback(self):
        client = self.client(); client.connect(timeout=.2)
        client._last_state['daily_refine_enabled'] = True
        busy = refine(1, OP, 'COMPLETED')
        busy['operation'].update(mutation_attempted=True, stock_callback_returned=True,
                                 account_progress_observed=True, panel_closed=True,
                                 practice_credit=20, coins_spent=1000)
        busy['view'].update(role_id='0', cycle=0, current=0, required=0, money=0,
                            money_cost=None, reason='refine_client_busy')
        def recover(state, ack):
            latest = deepcopy(busy)
            latest['revision'] += 1
            latest['view'].update(role_id='12345', cycle=100000, current=1, required=3,
                                  money=4000, money_cost=1000, coin_exchanges=1, reason='')
            state['daily_refine'] = latest
        self.respond(client, busy, mutate=recover)
        wire = client.daily_refine_command('close', expected_revision=1, character_id='12345',
                                           task_id=21, operation_id=OP, timeout=.2)
        self.assertEqual(wire['state']['daily_refine']['view']['cycle'], 100000)

    def test_newer_contradiction_to_closed_ack_is_unknown(self):
        client = self.client(); client.connect(timeout=.2)
        client._last_state['daily_refine_enabled'] = True
        closed = refine(1, OP, 'COMPLETED')
        closed['operation'].update(mutation_attempted=True, stock_callback_returned=True,
                                   account_progress_observed=True, panel_closed=True,
                                   practice_credit=20, coins_spent=1000)
        def change(state, ack):
            state['daily_refine']['revision'] += 1
            state['daily_refine']['operation'].update(status='UNKNOWN', panel_closed=False,
                                                       ui_ownership_held=True)
        self.respond(client, closed, mutate=change)
        with self.assertRaises(bridge.BridgeUnknown):
            client.daily_refine_command('close', expected_revision=1, character_id='12345',
                                        task_id=21, operation_id=OP, timeout=.2)

    def test_state_revision_cannot_move_back_within_same_command(self):
        client = self.client(); client.connect(timeout=.2)
        state = self.state(client, 0)
        state.update(daily_refine=refine(), daily_refine_enabled=True)
        bridge._atomic_json(client.session_dir / 'state.json', state)
        client._state()
        state['daily_refine']['revision'] -= 1
        bridge._atomic_json(client.session_dir / 'state.json', state)
        with self.assertRaises(bridge.BridgeUnknown):
            client._state()

    def test_published_snapshot_cannot_disappear_and_reset_watermark(self):
        client = self.client(); client.connect(timeout=.2)
        state = self.state(client, 0)
        state.update(daily_refine=refine(), daily_refine_enabled=True)
        bridge._atomic_json(client.session_dir / 'state.json', state)
        client._state()
        state['daily_refine'] = None
        bridge._atomic_json(client.session_dir / 'state.json', state)
        with self.assertRaises(bridge.BridgeUnknown):
            client._state()


class RefineAdmissionTests(unittest.TestCase):
    def test_missing_marker_and_pause_do_not_send(self):
        client = SimpleNamespace(last_state={}, daily_refine_observe=Mock(), daily_refine_command=Mock())
        owner = SimpleNamespace(_io_lock=threading.Lock(), _lock=threading.RLock(), _client=client,
            _campaign_check_locked=Mock(), _campaign_admission=False, _campaign_stopping=False, _busy=False)
        token = SimpleNamespace(context_key='{"character_id":"12345"}')
        self.assertEqual(ObjectController.observe_daily_refine(owner, token), {'enabled': False, 'refine': None})
        with self.assertRaises(CampaignAdmissionClosed):
            ObjectController.submit_daily_refine(owner, token, 'refine', expected_revision=1,
                                                 character_id='12345', task_id=21, operation_id=OP)
        client.daily_refine_observe.assert_not_called()
        client.daily_refine_command.assert_not_called()
