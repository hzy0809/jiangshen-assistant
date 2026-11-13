from copy import deepcopy
from types import SimpleNamespace
import threading
import unittest
from unittest.mock import Mock

from jiangshen_assistant import bridge_client as bridge
from jiangshen_assistant.objects import ObjectController, CampaignAdmissionClosed
from tests import test_bridge_client as fixtures


OP = '1234567890abcdef1234567890abcdef'


def guild(operation=None):
    return dict(schema=1, revision=1, character_id='12345', guild_id='99', phase='MAIN',
                gate='PASS', reason='', actions=['open'], operation=operation)


def fields(**changes):
    value = dict(action='open', expected_revision=1, character_id='12345', guild_id='99',
                 task_id=20, operation_id=OP, donation_type=2, max_premium=0)
    value.update(changes)
    return value


def operation(**changes):
    value = dict(operation_id=OP, task_id=20, donation_type=2, max_premium=0,
                 status='OPEN', invoked=False, closed=False, receipt_opcode=0,
                 receipt_result=None, progress_before=None, progress_after=None, reason='')
    value.update(changes)
    return value


class GuildTransportTests(unittest.TestCase):
    setUp = fixtures.BridgeTests.setUp
    client = fixtures.BridgeTests.client
    common = fixtures.BridgeTests.common
    state = fixtures.BridgeTests.state
    ack = fixtures.BridgeTests.ack

    def respond(self, client, observed, *, mutate=None):
        original = client._send
        def send(name, **kwargs):
            seq = original(name, **kwargs)
            state = self.state(client, seq)
            state['daily_guild'] = deepcopy(observed)
            state['campaign_gui'] = dict(command_seq=seq, phase='completed', invocation_attempted=True)
            ack = self.ack(client, seq, name, state, 'COMPLETED')
            ack['result']['daily_guild'] = deepcopy(observed)
            if mutate:
                mutate(state, ack)
            bridge._atomic_json(client.session_dir / 'state.json', state)
            bridge._atomic_json(client.session_dir / 'ack.json', ack)
            return seq
        client._send = Mock(side_effect=send)

    def test_fixed_command_fields_and_explicit_premium(self):
        client = self.client(); client.connect(timeout=.2)
        observed = guild(operation(donation_type=3, max_premium=80))
        self.respond(client, observed)
        result = client.daily_guild_command(**fields(donation_type=3, max_premium=80), timeout=.2)
        self.assertEqual(result['ack']['result']['daily_guild'], observed)
        self.assertEqual((client.session_dir / 'command.txt').read_text().splitlines(),
            ['version=3', 'nonce=' + client.nonce, 'sequence=1', 'command=daily-guild-command'] +
            [f'{key}={fields(donation_type=3, max_premium=80)[key]}' for key in bridge.GUILD_FIELDS])

    def test_unsafe_choices_rejected_before_publication(self):
        client = self.client(); client.connect(timeout=.2)
        for value in (fields(donation_type=3, max_premium=0), fields(donation_type=True),
                      fields(action='premium_confirm', donation_type=2), fields(task_id=19),
                      fields(guild_id='0'), fields(operation_id='x\ncommand=stop')):
            with self.subTest(value=value), self.assertRaises(bridge.BridgeError):
                client.daily_guild_command(**value, timeout=.2)
        self.assertEqual(client._seq, 0)
        self.assertFalse((client.session_dir / 'command.txt').exists())

    def test_completed_receipt_must_match_gui_sequence_and_immutable_choice(self):
        for mutate in (lambda state, ack: state['campaign_gui'].update(command_seq=0),
                       lambda state, ack: ack['result']['daily_guild']['operation'].update(donation_type=1)):
            with self.subTest(mutate=mutate):
                client = self.client(); client.connect(timeout=.2)
                self.respond(client, guild(operation()), mutate=mutate)
                with self.assertRaises(bridge.BridgeUnknown):
                    client.daily_guild_command(**fields(), timeout=.2)
                self.assertEqual(client._send.call_count, 1)
                client._finish_stopped(self.state(client, client._seq, stopped=True))

    def test_read_projection_rejects_malformed_actions(self):
        observed = guild(); observed['actions'] = [[]]
        self.assertFalse(bridge._guild_valid(observed))


class GuildAdmissionTests(unittest.TestCase):
    def test_pause_or_changed_role_cannot_submit_a_donation(self):
        client = SimpleNamespace(daily_guild_command=Mock(), last_state={})
        controller = SimpleNamespace(_io_lock=threading.Lock(), _lock=threading.RLock(),
            _client=client, _campaign_check_locked=Mock(), _campaign_admission=False,
            _campaign_stopping=False, _busy=False, _wake=threading.Event(),
            _command_event_locked=Mock(), _runtime_state=Mock())
        token = SimpleNamespace(context_key='{"character_id":"12345"}')
        args = fields(); action = args.pop('action')
        with self.assertRaises(CampaignAdmissionClosed):
            ObjectController.submit_daily_guild(controller, token, action, **args)
        controller._campaign_admission = True
        args['character_id'] = '54321'
        with self.assertRaises(bridge.BridgeError):
            ObjectController.submit_daily_guild(controller, token, action, **args)
        client.daily_guild_command.assert_not_called()
