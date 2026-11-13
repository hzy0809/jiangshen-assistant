from copy import deepcopy
import threading
from types import SimpleNamespace
import unittest
from unittest.mock import Mock

from jiangshen_assistant import bridge_client as bridge
from jiangshen_assistant.objects import ObjectController, CampaignAdmissionClosed
from tests import test_bridge_client as fixtures
from tests.test_daily_arena_chest import ArenaBridge

OP = '1234567890abcdef1234567890abcdef'


def arena(sequence=0, operation_id='', status='READY'):
    snapshot = deepcopy(ArenaBridge().snapshot)
    snapshot.update(revision=2, sequence=sequence, operation_id=operation_id, status=status)
    if status != 'READY':
        snapshot['next_action'] = ''
    return snapshot


class ArenaTransportTests(unittest.TestCase):
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
            gui = dict(command_seq=seq, phase='completed', invocation_attempted=True)
            state.update(daily_arena=deepcopy(value), daily_arena_enabled=True, campaign_gui=deepcopy(gui))
            ack = self.ack(client, seq, name, state, 'COMPLETED')
            ack['result'].update(daily_arena=deepcopy(value), daily_arena_enabled=True, campaign_gui=deepcopy(gui))
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
                client._last_state.pop('daily_arena_enabled', None)
            else:
                client._last_state['daily_arena_enabled'] = marker
            with self.assertRaises(bridge.BridgeError):
                client.daily_arena_observe(timeout=.2)
            with self.assertRaises(bridge.BridgeError):
                client.request('daily-arena-observe', timeout=.2)
            with self.assertRaises(bridge.BridgeError):
                client.daily_arena_command('open', expected_revision=1, character_id='12345',
                                           operation_id=OP, timeout=.2)
        self.assertEqual(client._seq, 0)

    def test_fixed_wire_and_bound_gui_invocation(self):
        client = self.client(); client.connect(timeout=.2)
        client._last_state['daily_arena_enabled'] = True
        value = arena(1, OP, 'WAITING')
        value['navigation_pending'] = True
        self.respond(client, value)
        wire = client.daily_arena_command('open', expected_revision=1, character_id='12345',
                                          operation_id=OP, timeout=.2)
        self.assertEqual(wire['ack']['result']['daily_arena']['operation_id'], OP)
        self.assertEqual((client.session_dir / 'command.txt').read_text().splitlines(),
            ['version=3', 'nonce=' + client.nonce, 'sequence=1', 'command=daily-arena-command',
             'action=open', 'operation_id=' + OP, 'character_id=12345', 'expected_revision=1'])

    def test_completed_receipt_cannot_regress_to_claimed(self):
        client = self.client(); client.connect(timeout=.2)
        client._last_state['daily_arena_enabled'] = True
        value = arena(1, OP, 'COMPLETED')
        value.update(invoked=True, settled=True, callback_returned=True, activity_push_returned=True,
                     ui_disposition='clear', current=1, counter_before=0, counter_after=1,
                     gold_before=426, gold_after=426, receipt_opcode=1122, receipt_result=0,
                     receipt_time=value['bonus_time'], receipt_gold_consume=0)
        def change(state, ack):
            state['daily_arena'].update(revision=3, status='CLAIMED', next_action='reward_confirm',
                                        ui_disposition='confirm_ready')
        self.respond(client, value, mutate=change)
        with self.assertRaises(bridge.BridgeUnknown):
            client.daily_arena_command('claim', expected_revision=1, character_id='12345',
                                       operation_id=OP, timeout=.2)


class ArenaAdmissionTests(unittest.TestCase):
    def test_missing_marker_and_pause_do_not_send(self):
        client = SimpleNamespace(last_state={}, daily_arena_observe=Mock(), daily_arena_command=Mock())
        owner = SimpleNamespace(_io_lock=threading.Lock(), _lock=threading.RLock(), _client=client,
            _campaign_check_locked=Mock(), _campaign_admission=False, _campaign_stopping=False, _busy=False)
        token = SimpleNamespace(context_key='{"character_id":"12345"}')
        self.assertEqual(ObjectController.observe_daily_arena(owner, token), {'enabled': False, 'arena': None})
        with self.assertRaises(CampaignAdmissionClosed):
            ObjectController.submit_daily_arena(owner, token, 'claim', expected_revision=1,
                                                character_id='12345', operation_id=OP)
        client.daily_arena_observe.assert_not_called()
        client.daily_arena_command.assert_not_called()
