import unittest

from jiangshen_assistant.daily_guild import DailyGuildExecutor
from jiangshen_assistant.objects import CampaignAdmissionClosed


def frame(phase, actions=(), *, status=None, gate='PASS', reason='', **fields):
    operation = None if status is None else dict(status=status, **fields)
    return dict(schema=1, character_id='1001', guild_id='25', phase=phase,
                gate=gate, reason=reason, actions=list(actions), operation=operation)


def main():
    return frame('MAIN', ['open'])


def donation_route():
    return [main(), frame('GUILD', ['donate_panel', 'close'], status='OPEN'),
            frame('DONATE', ['donate', 'close'], status='OPEN')]


def verified(phase='DONATE', *, opcode=301, reward=1, **fields):
    actions = ['open'] if phase == 'MAIN' else ['close']
    operation = dict(invoked=True, closed=phase == 'MAIN', receipt_opcode=opcode,
                     receipt_result=reward, progress_before=0, progress_after=1)
    operation.update(fields)
    return frame(phase, actions, status='COMPLETED', **operation)


class GuildBridge:
    """Replay independently published native states and command ACKs."""

    def __init__(self, observations, *, acks=None):
        self.token = object()
        self.observations = iter(observations)
        self.acks = acks or {}
        self.calls = []
        self.binding = None
        self.revision = 0
        self.time = 0.0
        self.paused = self.stopped = False
        self.on_wait = self.on_observe = lambda: None
        self.observed = None

    def wait(self):
        self.time += .25
        self.on_wait()

    def observe_daily_guild(self, token):
        assert token is self.token
        observation = next(self.observations, self.observed)
        state = dict(observation)
        if observation is not self.observed:
            self.revision += 1
        state.setdefault('revision', self.revision)
        self.observed = observation
        if state['operation'] is not None:
            state['operation'] = dict(self.binding, invoked=False, closed=False,
                receipt_opcode=0, receipt_result=None, progress_before=None,
                progress_after=None, reason='') | state['operation']
        self.on_observe()
        return state

    def submit_daily_guild(self, token, action, *, expected_revision, character_id, guild_id,
                           task_id, operation_id, donation_type, max_premium):
        assert token is self.token
        assert expected_revision == self.revision
        assert (character_id, guild_id) == ('1001', '25')
        assert action in self.observed['actions']
        assert not self.stopped and not self.paused
        binding = dict(task_id=task_id, operation_id=operation_id,
                       donation_type=donation_type, max_premium=max_premium)
        if action == 'open':
            assert len(operation_id) == 32 and operation_id.isalnum()
            self.binding = binding
        else:
            assert binding == self.binding
        self.calls.append((action, donation_type, max_premium))
        ack = self.acks.get(action, {'status': 'COMPLETED'})
        return {'ack': ack() if callable(ack) else ack}

    def executor(self, **kwargs):
        return DailyGuildExecutor(self, self.token, stopped=lambda: self.stopped,
            paused=lambda: self.paused, wait=self.wait, timeout=1.0,
            clock=lambda: self.time, **kwargs)

    def run(self, task_id=20, **kwargs):
        return self.executor(**kwargs).execute(task_id, character_id='1001')

    @property
    def actions(self):
        return [action for action, _, _ in self.calls]


class DailyGuildTests(unittest.TestCase):
    def test_donation_binds_explicit_choice_and_waits_for_delayed_progress(self):
        pending = frame('WAITING', status='PENDING', gate='WAITING_NETWORK', invoked=True)
        reward = frame('REWARD', ['reward_confirm'], status='PENDING', invoked=True,
                       receipt_opcode=301, receipt_result=1, progress_before=0)
        for choice, cap in [(1, 0), (2, 0), (3, 80)]:
            with self.subTest(choice=choice):
                premium = ([frame('PREMIUM', ['premium_confirm'], status='OPEN')]
                           if choice == 3 else [])
                bridge = GuildBridge(donation_route() + premium +
                    [pending, pending, reward, pending, verified(), verified('MAIN')])
                result = bridge.run(**({'donation_type': choice} if choice != 2 else {}))
                actions = ['open', 'donate_panel', 'donate']
                if choice == 3:
                    actions.append('premium_confirm')
                actions += ['reward_confirm', 'close']
                self.assertEqual(bridge.calls, [(action, choice, cap) for action in actions])
                self.assertEqual((result.status, result.closed, result.receipt_opcode),
                                 ('COMPLETED', True, 301))
                self.assertEqual((result.progress_before, result.progress_after), (0, 1))

    def test_lottery_keeps_premium_at_zero_even_if_donation_selection_is_paid(self):
        bridge = GuildBridge([main(), frame('GUILD', ['lottery_panel', 'close'], status='OPEN'),
            frame('LOTTERY', ['lottery', 'close'], status='OPEN'),
            verified('LOTTERY', opcode=302, reward=27), verified('MAIN', opcode=302, reward=27)])
        result = bridge.run(18, donation_type=3)
        self.assertEqual(bridge.calls, [(action, 0, 0)
            for action in ('open', 'lottery_panel', 'lottery', 'close')])
        self.assertEqual((result.status, result.receipt_result), ('COMPLETED', 27))

    def test_coin_donation_cannot_confirm_a_premium_dialog(self):
        bridge = GuildBridge(donation_route() + [frame('PREMIUM', ['premium_confirm'], status='OPEN')])
        self.assertEqual(bridge.run().status, 'UNKNOWN')
        self.assertEqual(bridge.actions, ['open', 'donate_panel', 'donate'])

    def test_blocked_open_issues_no_commands(self):
        bridge = GuildBridge([frame('MAIN', gate='BLOCKED', reason='guild_foreign_panel')])
        result = bridge.run()
        self.assertEqual((result.status, result.reason), ('BLOCKED', 'guild_foreign_panel'))
        self.assertEqual(bridge.actions, [])

    def test_known_blocker_at_task_panel_closes_only_owned_panel(self):
        bridge = GuildBridge(donation_route()[:2] +
            [frame('DONATE', ['close'], status='OPEN'),
             frame('MAIN', ['open'], status='REJECTED', closed=True)])
        result = bridge.run()
        self.assertEqual((result.status, result.reason, result.closed),
                         ('BLOCKED', 'guild_task_action_unavailable', True))
        self.assertEqual(bridge.actions, ['open', 'donate_panel', 'close'])

    def test_rejected_send_can_close_without_resending(self):
        closed = frame('MAIN', ['open'], status='REJECTED', closed=True)
        bridge = GuildBridge(donation_route() + [donation_route()[-1], closed],
            acks={'donate': {'status': 'REJECTED', 'reason': 'guild_network_busy_or_unverified'}})
        result = bridge.run()
        self.assertEqual((result.status, result.reason, result.closed),
                         ('REJECTED', 'guild_network_busy_or_unverified', True))
        self.assertEqual(bridge.actions, ['open', 'donate_panel', 'donate', 'close'])

    def test_command_ack_alone_cannot_prove_cleanup(self):
        final = verified()
        bridge = GuildBridge(donation_route() + [final, final])
        result = bridge.run()
        self.assertEqual((result.status, result.closed, result.progress_after), ('UNKNOWN', False, 1))
        self.assertEqual(bridge.actions, ['open', 'donate_panel', 'donate', 'close'])

    def test_rejected_close_does_not_return_to_the_spending_route(self):
        bridge = GuildBridge(donation_route()[:2] +
            [frame('DONATE', ['close'], status='OPEN'), donation_route()[-1]],
            acks={'close': {'status': 'REJECTED', 'reason': 'guild_revision_role_or_gate_changed'}})
        self.assertEqual(bridge.run().status, 'UNKNOWN')
        self.assertEqual(bridge.actions, ['open', 'donate_panel', 'close'])

    def test_inconsistent_completion_is_unknown(self):
        for invalid in (verified(opcode=302), verified(reward=0),
                        verified(progress_after=0), verified(progress_after=2),
                        verified(invoked=False)):
            with self.subTest(invalid=invalid):
                bridge = GuildBridge(donation_route() + [invalid])
                self.assertEqual(bridge.run().status, 'UNKNOWN')
                self.assertEqual(bridge.actions, ['open', 'donate_panel', 'donate'])

    def test_changed_operation_choice_identity_or_revision_cannot_be_cleaned_up(self):
        for invalid in (verified(operation_id='f' * 32), verified(donation_type=1),
                        dict(verified(), guild_id='26'), dict(verified(), revision=1)):
            with self.subTest(invalid=invalid):
                bridge = GuildBridge(donation_route() + [invalid])
                self.assertEqual(bridge.run().status, 'UNKNOWN')
                self.assertEqual(bridge.actions, ['open', 'donate_panel', 'donate'])

    def test_stale_ack_while_waiting_for_new_native_state_never_reissues(self):
        route = donation_route()
        bridge = GuildBridge([route[0], route[0], route[1], route[1], route[2],
                             route[2], verified(), verified('MAIN')])
        self.assertEqual(bridge.run().status, 'COMPLETED')
        self.assertEqual(bridge.actions, ['open', 'donate_panel', 'donate', 'close'])

    def test_paused_admission_race_reobserves_before_submitting(self):
        route = donation_route()
        bridge = GuildBridge(route + [route[2], route[2], verified(), verified('MAIN')])

        def pause_race():
            del bridge.acks['donate']
            bridge.calls.pop()
            bridge.paused = True
            raise CampaignAdmissionClosed('paused before admission')

        bridge.acks['donate'] = pause_race
        bridge.on_wait = lambda: setattr(bridge, 'paused', False)
        self.assertEqual(bridge.run().status, 'COMPLETED')
        self.assertEqual(bridge.actions, ['open', 'donate_panel', 'donate', 'close'])

    def test_stop_before_open_and_stop_after_request(self):
        bridge = GuildBridge([main()])
        bridge.on_observe = lambda: setattr(bridge, 'stopped', True)
        self.assertEqual(bridge.run().status, 'STOPPED')
        self.assertEqual(bridge.actions, [])

        pending = frame('WAITING', status='PENDING', gate='WAITING_NETWORK', invoked=True)
        bridge = GuildBridge(donation_route() + [pending, verified()])

        def stop_in_flight():
            if bridge.observed is pending:
                bridge.stopped = True

        bridge.on_observe = stop_in_flight
        result = bridge.run()
        self.assertEqual((result.status, result.progress_after, result.closed), ('UNKNOWN', 1, False))
        self.assertEqual(bridge.actions, ['open', 'donate_panel', 'donate'])

    def test_unresolved_request_is_not_retried_on_a_later_execute(self):
        pending = frame('WAITING', status='PENDING', gate='WAITING_NETWORK', invoked=True)
        bridge = GuildBridge(donation_route() + [pending])
        executor = bridge.executor()
        self.assertEqual(executor.execute(20, character_id='1001').status, 'UNKNOWN')
        result = executor.execute(20, character_id='1001')
        self.assertEqual((result.status, result.reason), ('UNKNOWN', 'guild_previous_operation_unresolved'))
        self.assertEqual(bridge.actions, ['open', 'donate_panel', 'donate'])

    def test_transport_unknown_is_neither_retried_nor_cleaned_up(self):
        def transport_failed():
            raise OSError('receipt read failed')

        bridge = GuildBridge(donation_route(), acks={'donate': transport_failed})
        result = bridge.run()
        self.assertEqual(result.status, 'UNKNOWN')
        self.assertIn('receipt read failed', result.reason)
        self.assertEqual(bridge.actions, ['open', 'donate_panel', 'donate'])

    def test_new_operation_after_verified_close_uses_new_binding(self):
        closed = verified('MAIN')
        bridge = GuildBridge(donation_route() + [verified(), closed, closed,
            frame('GUILD', ['lottery_panel', 'close'], status='OPEN'),
            frame('LOTTERY', ['lottery', 'close'], status='OPEN'),
            verified('LOTTERY', opcode=302, reward=27), verified('MAIN', opcode=302, reward=27)])
        executor = bridge.executor()
        self.assertEqual(executor.execute(20, character_id='1001').status, 'COMPLETED')
        first = bridge.binding['operation_id']
        self.assertEqual(executor.execute(18, character_id='1001').status, 'COMPLETED')
        self.assertNotEqual(first, bridge.binding['operation_id'])


if __name__ == '__main__':
    unittest.main()
