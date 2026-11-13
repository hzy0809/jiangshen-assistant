"""Offline coordinator decisions against a live-shaped native story projection.

The native-owned marker is supplied as an input contract, not fabricated by
the coordinator. These tests do not execute Unity, a game, or network calls.
"""
from pathlib import Path
import copy
import json
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

from jiangshen_assistant.bridge_client import CAMPAIGN_INTERFACE, _challenge_valid
from jiangshen_assistant.campaign import Campaign
from jiangshen_assistant.campaign_task import CampaignCoordinator
from jiangshen_assistant.challenge_task import ChallengeCoordinator
from jiangshen_assistant.objects import CampaignAdmissionClosed


class Runner:
    is_active = False

    def start(self, name, task, **kwargs):
        self.task = task
        self.is_active = True
        return True

    def pause(self):
        pass

    def resume(self):
        pass

    def stop(self):
        pass


class NativeStory:
    """Deterministic bridge double; only the real coordinator chooses commands."""
    def __init__(self, *, challenge=False, story=True):
        self.challenge, self.story = challenge, story
        self.now = 0.
        self.bridge_state, self.is_busy = 'ready', False
        self.owner = None
        self.stopping = False
        self.admission = True
        self.commands, self.captured_pending = [], []
        self.on_observe = self.on_continue = self.before_submit = None
        self.reads = 0
        self.snap = {
            'schema': 1, 'interface': CAMPAIGN_INTERFACE, 'revision': 1,
            'character_id': '123', 'available': True, 'gate': 'PASS',
            'phase': 'FORMATION', 'campaign_id': 10, 'troop_id': 20,
            'result_kind': '', 'result_navigation': '', 'eligible_troops': [],
            'tutorial': {'kind': 'NONE', 'step': '', 'gate': ''},
            'flow': {'status': 'IDLE', 'action': '', 'reason': ''},
            'pending_story_attempt_id': '', 'pending_story_continuation': '',
            'attempt': {'status': 'NONE', 'attempt_id': '', 'character_id': '',
                        'command_seq': 0, 'campaign_id': 0, 'troop_id': 0,
                        'report_id': '0', 'outcome': 'UNKNOWN', 'evidence': ''},
        }
        if challenge:
            self.snap.update(mode='NORMAL_CHALLENGE', campaign_type=2, challenge_candidates=[])

    def acquire_campaign(self, run_id, **kwargs):
        self.owner = SimpleNamespace(context_key=json.dumps({'character_id': '123', 'run': run_id}))
        return self.owner

    def view(self, token):
        assert token is self.owner
        self.now += .1
        self.reads += 1
        if self.on_observe:
            self.on_observe(self)
        return {'context_key': token.context_key, 'campaign': copy.deepcopy(self.snap),
                'stopping': self.stopping}

    observe_campaign = view

    def publish(self, **changes):
        self.snap.update(changes)
        self.snap['revision'] += 1

    def ready(self, step='700:0', continuation='skip'):
        self.publish(phase='SCENARIO', available=True, gate='PASS',
                     pending_story_continuation=continuation,
                     tutorial={'kind': 'SCENARIO', 'step': step, 'gate': 'PASS'},
                     flow={'status': 'ACTION', 'action': 'continue', 'reason': ''})

    def waiting(self, reason='WAITING_SCENARIO_END'):
        self.publish(tutorial={**self.snap['tutorial'], 'gate': reason},
                     pending_story_continuation='',
                     flow={'status': 'WAIT', 'action': '', 'reason': reason})

    def battle(self):
        self.publish(phase='BATTLE', pending_story_attempt_id='', pending_story_continuation='',
                     tutorial={'kind': 'NONE', 'step': '', 'gate': ''},
                     flow={'status': 'IDLE', 'action': '', 'reason': ''})

    def settle(self, kind='NORMAL'):
        self.battle()
        self.snap['attempt'].update(status='SETTLED', outcome='WIN', report_id='77',
                                    evidence='captured_authoritative_receipt_applied')
        self.publish(phase='RESULT', result_kind=kind)

    def receipt(self, action, kwargs, *, status='COMPLETED', invoked=True, reason=''):
        seq = len(self.commands)
        name = 'challenge_command' if self.challenge else 'campaign_command'
        return {'requested_seq': seq, 'ack': {'command_seq': seq, 'status': status,
                'result': {name: {'command_seq': seq, 'action': action,
                          'attempt_id': kwargs['attempt_id'], 'invoked': invoked, 'reason': reason}}}}

    def submit_campaign(self, token, action, **kwargs):
        if self.before_submit:
            self.before_submit(self, action)
        if not self.admission:
            raise CampaignAdmissionClosed('closed')
        assert kwargs['expected_revision'] == self.snap['revision']
        self.commands.append((action, copy.deepcopy(kwargs)))
        if action == 'start':
            self.snap['attempt'].update(status='SUBMITTED', attempt_id=kwargs['attempt_id'],
                character_id='123', command_seq=len(self.commands), campaign_id=10, troop_id=20)
            if self.story:
                self.snap['pending_story_attempt_id'] = kwargs['attempt_id']
                self.ready()
            else:
                self.battle()
        elif action == 'continue':
            self.captured_pending.append(copy.deepcopy(self.coordinator._snapshot.get('pending')))
            if self.on_continue:
                response = self.on_continue(self, kwargs)
                if response is not None:
                    return response
        elif action in ('ack_result', 'next_chapter'):
            self.publish(phase='CAMPAIGN', result_kind='', available=False, gate='NO_RESOURCE',
                         flow={'status': 'UNSUPPORTED', 'action': '', 'reason': 'NO_RESOURCE'})
        else:
            raise AssertionError('unexpected command: ' + action)
        return self.receipt(action, kwargs)

    def pause_campaign(self, token, **kwargs):
        self.admission = False
        return True

    def resume_campaign(self, token):
        self.admission = True
        return True

    def stop_campaign(self, token, **kwargs):
        self.admission, self.stopping = False, True
        return True

    def release_campaign(self, token, **kwargs):
        self.owner = None
        self.bridge_state = 'disconnected'


class EventStory(NativeStory):
    def __init__(self, **kwargs):
        super().__init__(**kwargs)
        self.events = []

    def subscribe_workflow(self, token, **kwargs):
        class Subscription:
            def drain(inner):
                events, self.events = self.events, []
                return events

            def close(inner):
                pass
        return Subscription()

    def publish(self, **changes):
        super().publish(**changes)
        self.events.append({'revision': self.snap['revision']})

    workflow_snapshot = NativeStory.view


class PendingStoryTests(unittest.TestCase):
    def run_native(self, native=None, *, coordinator_type=CampaignCoordinator,
                   battle_timeout=2., before_run=None):
        native = native or NativeStory()
        runner = Runner()
        with tempfile.TemporaryDirectory() as directory:
            coordinator = coordinator_type(native, runner, Path(directory),
                clock=lambda: native.now, poll_seconds=.001, battle_timeout=battle_timeout,
                transition_timeout=.6, stop_timeout=.5)
            native.coordinator = coordinator
            coordinator._wait = lambda timeout=None: None
            if before_run:
                before_run(coordinator)
            self.assertTrue(coordinator.start())
            # Count actual domain intent preparation independently of commands.
            prepared = []
            original = Campaign.prepare

            def record_prepare(campaign, *args, **kwargs):
                intent = original(campaign, *args, **kwargs)
                prepared.append(intent)
                return intent

            with patch.object(Campaign, 'prepare', record_prepare):
                runner.task(SimpleNamespace(cancelled=False, is_paused=False))
            self.assertFalse(coordinator.is_active)
            return coordinator.snapshot, native, prepared

    def assert_retained(self, state, native, prepared, *, continues=1, uncertain=True):
        self.assertEqual([action for action, _ in native.commands].count('start'), 1)
        self.assertEqual([action for action, _ in native.commands].count('continue'), continues)
        self.assertEqual(len(prepared), 1)
        pending = state['pending']
        self.assertEqual(pending['kind'], 'challenge')
        self.assertEqual(pending['intent_id'], prepared[0].intent_id)
        self.assertEqual(pending['attempt_id'], prepared[0].intent_id)
        self.assertEqual(pending['uncertain'], uncertain)
        self.assertEqual(state['losses'], 0)

    def test_exact_story_continues_without_second_intent_or_start(self):
        state, native, prepared = self.run_native()
        self.assert_retained(state, native, prepared)
        action, fields = native.commands[1]
        self.assertEqual(action, 'continue')
        self.assertEqual(fields, {'expected_revision': 2, 'campaign_id': 10, 'troop_id': 0, 'attempt_id': ''})
        self.assertEqual(state['reason'], 'battle_timeout_pending_retained')

    def test_duplicate_and_unrelated_revision_cannot_repeat_continue(self):
        native = NativeStory()
        native.on_observe = lambda n: n.publish() if len(n.commands) >= 2 else None
        state, native, prepared = self.run_native(native)
        self.assertGreater(native.snap['revision'], 5)
        self.assert_retained(state, native, prepared)

    def test_subsequent_step_and_observed_wait_allow_exact_new_continuations(self):
        native = NativeStory()
        def observe(n):
            count = sum(action == 'continue' for action, _ in n.commands)
            if count == 1 and n.snap['tutorial']['step'] == '700:0':
                n.ready('700:1')
            elif count == 2 and n.snap['flow']['status'] == 'ACTION':
                n.waiting('WAITING_MOVEMENT')
            elif count == 2 and n.snap['flow']['status'] == 'WAIT':
                n.ready('700:1')
        native.on_observe = observe
        state, native, prepared = self.run_native(native)
        self.assert_retained(state, native, prepared, continues=3)
        self.assertEqual([fields['expected_revision'] for action, fields in native.commands if action == 'continue'], [2, 3, 5])

    def test_identity_mismatch_and_missing_attestation_are_unknown_without_input(self):
        mutations = (
            lambda n: n.snap.pop('pending_story_attempt_id'),
            lambda n: n.snap.update(pending_story_attempt_id=''),
            lambda n: n.snap.update(pending_story_attempt_id='f' * 32),
            lambda n: n.snap['attempt'].update(attempt_id='f' * 32),
            lambda n: n.snap['attempt'].update(character_id='456'),
            lambda n: n.snap['attempt'].update(troop_id=21),
            lambda n: n.snap.update(campaign_id=11),
            lambda n: n.snap.update(troop_id=21),
        )
        for mutate in mutations:
            with self.subTest(mutation=mutate):
                native = NativeStory()
                native.on_observe = lambda n: mutate(n) if n.commands else None
                state, native, prepared = self.run_native(native)
                self.assert_retained(state, native, prepared, continues=0)
                self.assertEqual(state['reason'], 'pending_story_identity_mismatch')

    def test_missing_unknown_or_empty_ready_branch_is_unknown(self):
        for value in (None, '', 'automatic', True):
            with self.subTest(continuation=value):
                native = NativeStory()
                def observe(n):
                    if n.commands:
                        if value is None:
                            n.snap.pop('pending_story_continuation', None)
                        else:
                            n.snap['pending_story_continuation'] = value
                native.on_observe = observe
                state, native, prepared = self.run_native(native)
                self.assert_retained(state, native, prepared, continues=0)
                self.assertIn('pending_story_', state['reason'])

    def test_same_section_native_branch_change_advances_after_missed_wait(self):
        native = NativeStory()
        def observe(n):
            if len(n.commands) == 2 and n.snap['pending_story_continuation'] == 'skip':
                n.ready(continuation='intro')
            elif len(n.commands) == 3 and n.snap['pending_story_continuation'] == 'intro':
                n.ready(continuation='next')
        native.on_observe = observe
        state, native, prepared = self.run_native(native)
        self.assert_retained(state, native, prepared, continues=3)
        self.assertEqual([fields['expected_revision'] for action, fields in native.commands if action == 'continue'], [2, 3, 4])

    def test_unsupported_raw_native_flow_cannot_be_promoted_to_continue(self):
        native = NativeStory()
        native.on_observe = lambda n: n.snap.update(flow={
            'status': 'UNSUPPORTED', 'action': '', 'reason': 'unknown_branch'}) if n.commands else None
        state, native, prepared = self.run_native(native)
        self.assert_retained(state, native, prepared, continues=0)
        self.assertEqual(state['reason'], 'pending_story_gate_closed')

    def test_context_change_closes_pending_story_without_input(self):
        native = NativeStory()
        original = native.observe_campaign
        def observe(token):
            value = original(token)
            if native.commands:
                value['context_key'] = 'another-process-session-role'
            return value
        native.observe_campaign = observe
        state, native, prepared = self.run_native(native)
        self.assert_retained(state, native, prepared, continues=0)
        self.assertEqual(state['reason'], 'campaign_context_changed')

    def test_explicit_cancel_before_submit_keeps_original_pending(self):
        native = NativeStory()
        native.on_continue = lambda n, fields: {'cancelled_before_submit': True}
        state, native, prepared = self.run_native(native)
        self.assert_retained(state, native, prepared)
        self.assertFalse(native.admission)

    def test_stale_or_changed_same_revision_is_unknown(self):
        for after_continue in (False, True):
            with self.subTest(after_continue=after_continue):
                native = NativeStory()
                def observe(n):
                    if after_continue and len(n.commands) == 2:
                        n.snap['tutorial']['step'] = '700:1'
                    elif not after_continue and n.commands:
                        n.snap['revision'] = 1
                native.on_observe = observe
                state, native, prepared = self.run_native(native)
                self.assert_retained(state, native, prepared, continues=int(after_continue))
                self.assertEqual(state['reason'], 'pending_story_revision_invalid')

    def test_noninvoked_stale_rejection_needs_new_revision_and_keeps_battle(self):
        native = NativeStory()
        def continue_story(n, fields):
            if len(n.commands) == 2:
                return n.receipt('continue', fields, status='REJECTED', invoked=False,
                                 reason='campaign_revision_changed')
        native.on_continue = continue_story
        def observe(n):
            if len(n.commands) == 2 and n.reads == 5:
                n.publish()
        native.on_observe = observe
        state, native, prepared = self.run_native(native)
        self.assert_retained(state, native, prepared, continues=2)
        self.assertEqual([fields['expected_revision'] for action, fields in native.commands if action == 'continue'], [2, 3])

    def test_other_noninvoked_rejection_pauses_preserving_pending(self):
        native = NativeStory()
        native.on_continue = lambda n, f: n.receipt('continue', f, status='REJECTED', invoked=False,
                                                   reason='scenario_gui_context_changed')
        state, native, prepared = self.run_native(native)
        self.assert_retained(state, native, prepared)
        self.assertEqual(state['reason'], 'battle_timeout_pending_retained')
        self.assertFalse(native.admission)

    def test_ambiguous_and_wrong_ack_never_replay_or_clear_pending(self):
        for status, invoked, corrupt in (
                ('LATE', True, False), ('COMPLETED', False, False),
                ('REJECTED', True, False), ('COMPLETED', True, True)):
            with self.subTest(status=status, invoked=invoked, corrupt=corrupt):
                native = NativeStory()
                def response(n, fields):
                    wire = n.receipt('continue', fields, status=status, invoked=invoked)
                    if corrupt:
                        wire['ack']['command_seq'] += 1
                    return wire
                native.on_continue = response
                state, native, prepared = self.run_native(native)
                self.assert_retained(state, native, prepared)
                self.assertIn('correlation_failed' if corrupt else 'invocation_unconfirmed', state['reason'])

    def test_pause_stop_and_admission_race_prevent_continuation(self):
        for decision, at_submit in (('pause', False), ('stop', False), ('pause', True), ('stop', True)):
            with self.subTest(decision=decision, at_submit=at_submit):
                native = NativeStory()
                def decide(n, action=None):
                    if n.commands and (not at_submit or action == 'continue'):
                        getattr(n.coordinator, decision)()
                if at_submit:
                    native.before_submit = decide
                else:
                    native.on_observe = decide
                state, native, prepared = self.run_native(native)
                self.assert_retained(state, native, prepared, continues=0)
                self.assertIn('timeout_pending_retained', state['reason'])

    def test_pause_during_success_ack_never_continues_new_ready_step(self):
        native = NativeStory()
        def response(n, fields):
            n.coordinator.pause()
            n.ready('700:1')
        native.on_continue = response
        state, native, prepared = self.run_native(native)
        self.assert_retained(state, native, prepared)

    def test_native_unknown_precedes_ready_scenario_and_cannot_be_overridden(self):
        native = NativeStory()
        native.on_observe = lambda n: n.snap['attempt'].update(status='UNKNOWN') if n.commands else None
        state, native, prepared = self.run_native(native)
        self.assert_retained(state, native, prepared, continues=0)
        self.assertEqual(state['reason'], 'bridge_attempt_unknown')

    def test_command_time_exception_remains_unknown(self):
        native = NativeStory()
        def fail(n, fields):
            raise TimeoutError('no conclusive story ACK')
        native.on_continue = fail
        state, native, prepared = self.run_native(native)
        self.assert_retained(state, native, prepared)
        self.assertEqual(state['reason'], 'no conclusive story ACK')

    def test_real_normal_challenge_coordinator_uses_its_own_receipt(self):
        native = NativeStory(challenge=True)
        self.assertTrue(_challenge_valid(native.snap))
        state, native, prepared = self.run_native(native, coordinator_type=ChallengeCoordinator)
        self.assert_retained(state, native, prepared)
        self.assertEqual(state['loss_mode'], 'cumulative')

    def test_story_close_settles_same_attempt_then_result_and_reward_navigation(self):
        for coordinator_type in (CampaignCoordinator, ChallengeCoordinator):
            for kind, expected in (('NORMAL', 'ack_result'), ('REWARD', 'ack_result'), ('CHAPTER', 'next_chapter')):
                with self.subTest(coordinator=coordinator_type, kind=kind):
                    native = NativeStory(challenge=coordinator_type is ChallengeCoordinator)
                    native.on_continue = lambda n, f: n.settle(kind)
                    state, native, prepared = self.run_native(native, coordinator_type=coordinator_type)
                    self.assertEqual([action for action, _ in native.commands], ['start', 'continue', expected])
                    self.assertEqual(len(prepared), 1)
                    self.assertIsNone(state['pending'])
                    self.assertEqual(len(state['consumed']), 1)

    def test_no_story_battle_and_settlement_do_not_gain_continue(self):
        native = NativeStory(story=False)
        native.on_observe = lambda n: n.settle() if len(n.commands) == 1 else None
        state, native, prepared = self.run_native(native)
        self.assertEqual([action for action, _ in native.commands], ['start', 'ack_result'])
        self.assertIsNone(state['pending'])
        self.assertEqual(len(prepared), 1)

    def test_event_delivery_during_ack_uses_latest_snapshot_once(self):
        native = EventStory()
        def response(n, fields):
            if len(n.commands) == 2:
                n.ready('700:1')
                n.events.extend([{'revision': 2}, {'revision': 2}])
        native.on_continue = response
        state, native, prepared = self.run_native(native)
        self.assert_retained(state, native, prepared, continues=2)

    def test_no_native_event_does_not_authorize_changed_cached_scenario(self):
        native = EventStory()
        def response(n, fields):
            n.ready('700:1')
            n.events.clear()
        native.on_continue = response
        state, native, prepared = self.run_native(native)
        self.assert_retained(state, native, prepared)

    def test_negative_control_old_pending_path_times_out_without_continuation(self):
        # Disabling only the new pending path reproduces old source behavior:
        # _plan_action still cannot run while the original challenge is pending.
        with patch.object(CampaignCoordinator, '_continue_pending_story', lambda *args: None):
            state, native, prepared = self.run_native()
        self.assert_retained(state, native, prepared, continues=0)
        self.assertEqual(state['reason'], 'battle_timeout_pending_retained')


if __name__ == '__main__':
    unittest.main()
