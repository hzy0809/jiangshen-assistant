from copy import deepcopy
from pathlib import Path
import threading
from types import SimpleNamespace
import unittest
from unittest.mock import Mock

from jiangshen_assistant import bridge_client as bridge
from jiangshen_assistant.daily_wood import DailyWoodExecutor, quota_epoch
from jiangshen_assistant.daily_workflow import DailyWorkflow, _refresh_cycle
from jiangshen_assistant.objects import CampaignAdmissionClosed, ObjectController
from tests import test_bridge_client as fixtures
from tests.test_daily_workflow import activity, by_id, observation


ROLE = '1001'
DAY = 1793754000
NOW = DAY + 36000
CYCLE = DAY + 18000
EPOCH = DAY - 14400
OP = '1234567890abcdef1234567890abcdef'
TOKEN = SimpleNamespace(context_key='{"character_id":"1001"}')
PATH = ['open', 'fast', 'close_result', 'close_panel']


def wood():
    return dict(schema=1, revision=1, role_id=ROLE, operation_id='', status='IDLE', reason='wood_unobserved',
                next_action='open', activity_count=0, fast_quota=None, refresh_cycle=CYCLE, quota_epoch=EPOCH,
                mutation_attempted=False, task_confirmed=False, cleanup_pending=False, busy=False)


def receipt(sequence, fields):
    gui = dict(command_seq=sequence, phase='completed', invocation_attempted=True)
    return gui, dict(fields, command_seq=sequence, invocation_attempted=True)


class WoodObjects:
    def __init__(self, quota=1):
        self.wood = wood()
        self.quota = quota
        self.enabled = True
        self.actions = []
        self.observed = self.last_observation = 0
        self.after_ack = lambda action, wire: None
        self.before_submit = lambda action: None
        self.on_observe = lambda: None
        self.admission_open = True
        self.auto = set(PATH)
        self.reject_fast = False
        self.refuse = None

    def advance(self, **fields):
        self.wood.update(fields)
        self.wood['revision'] += 1

    def observe_daily_wood(self, token):
        assert token is TOKEN
        self.observed += 1
        self.on_observe()
        return dict(enabled=self.enabled, wood=deepcopy(self.wood) if self.enabled else None)

    def settle(self, action):
        if action == 'open':
            if self.quota:
                self.advance(status='READY', reason='wood_612_current_free_quota', next_action='fast', fast_quota=self.quota)
            else:
                self.advance(status='BLOCKED', reason='wood_no_free_quota', next_action='close_panel', fast_quota=0)
        elif action == 'fast':
            if self.reject_fast:
                self.advance(status='BLOCKED', reason='wood_fast_rejected', next_action='close_panel')
            else:
                self.advance(status='RESULT', reason='wood_free_fast_and_activity_observed', next_action='close_result',
                             fast_quota=self.quota - 1, activity_count=1, task_confirmed=True)
        elif action == 'close_result':
            self.advance(next_action='close_panel')
        else:
            if self.wood['task_confirmed']:
                self.advance(status='COMPLETED', reason='wood_free_fast_complete', cleanup_pending=False, busy=False)
            else:
                self.advance(status='BLOCKED', reason='wood_fast_rejected' if self.reject_fast else 'wood_no_free_quota',
                             cleanup_pending=False, busy=False)

    def submit_daily_wood(self, token, *, admission, **fields):
        assert token is TOKEN and fields['character_id'] == ROLE and fields['task_id'] == 22
        assert fields['expected_revision'] == self.wood['revision'] and self.observed > self.last_observation
        action = fields['action']
        assert action == self.wood['next_action']
        if action != 'open':
            assert fields['operation_id'] == self.wood['operation_id']
        self.before_submit(action)
        if not self.admission_open or not admission():
            raise CampaignAdmissionClosed('wood prepublication gate')
        self.last_observation = self.observed
        if self.refuse and action == self.refuse[0]:
            seq = self.observed + 10
            reason = 'wood_final_command_or_context_changed'
            phase = self.refuse[1]
            if phase == 'not_invoked' and not self.wood['busy']:
                self.advance(status='BLOCKED', reason=reason)
            gui = dict(command_seq=seq, phase=phase, invocation_attempted=False)
            current_gui = dict(command_seq=0, phase='none', invocation_attempted=False) if phase == 'none' else gui
            wire = dict(requested_seq=seq, ack=dict(command_seq=seq, command_name='daily-wood-command',
                        status='REJECTED', reason=reason, result=dict(daily_wood_enabled=True,
                        daily_wood=deepcopy(self.wood), campaign_gui=deepcopy(gui),
                        daily_wood_command=dict(fields, command_seq=seq, invocation_attempted=False))),
                        state=dict(daily_wood_enabled=True, daily_wood=deepcopy(self.wood),
                                   last_command_seq=seq, campaign_gui=deepcopy(current_gui)))
            self.after_ack(action, wire)
            return wire
        self.actions.append(action)
        status = {'open': 'OPENING', 'fast': 'PENDING', 'close_result': 'CLOSING', 'close_panel': 'CLOSING'}[action]
        self.advance(status=status, next_action='', operation_id=fields['operation_id'],
                     cleanup_pending=True, busy=True, mutation_attempted=self.wood['mutation_attempted'] or action == 'fast')
        seq = self.observed + 10
        gui, echo = receipt(seq, fields)
        ack = dict(command_seq=seq, command_name='daily-wood-command', status='COMPLETED',
                   result=dict(daily_wood_enabled=True, daily_wood=deepcopy(self.wood),
                               daily_wood_command=echo, campaign_gui=gui))
        if action in self.auto:
            self.settle(action)
        wire = dict(requested_seq=seq, ack=ack, state=dict(daily_wood_enabled=True,
                    daily_wood=deepcopy(self.wood), campaign_gui=deepcopy(gui)))
        self.after_ack(action, wire)
        return wire


class WoodExecutorTests(unittest.TestCase):
    def setUp(self):
        self.objects, self.now = WoodObjects(), 0
        self.paused = self.stopped = False

    def wait(self):
        self.now += 1

    def run_wood(self):
        return DailyWoodExecutor(self.objects, TOKEN, wait=self.wait, timeout=5, clock=lambda: self.now,
            stopped=lambda: self.stopped, paused=lambda: self.paused).execute(22, character_id=ROLE,
            expected_cycle=CYCLE, expected_quota_epoch=EPOCH, expected_current=0, expected_required=1)

    def test_stock_result_requires_fresh_closed_home_and_exact_quota(self):
        self.objects = WoodObjects(2)
        step = self.run_wood()
        self.assertEqual(self.objects.actions, PATH)
        self.assertEqual((step.status, step.progress_after, step.cleanup), ('COMPLETED', 1, 'VERIFIED_CLOSED'))
        self.assertTrue(step.task_confirmed and step.mutation_attempted)
        self.assertEqual(self.objects.wood['fast_quota'], 1)

    def test_no_quota_and_known_negative_fast_close_without_retry(self):
        for quota, reject, expected in ((0, False, ['open', 'close_panel']), (1, True, ['open', 'fast', 'close_panel'])):
            with self.subTest(quota=quota, reject=reject):
                self.objects = WoodObjects(quota)
                self.objects.reject_fast = reject
                step = self.run_wood()
                self.assertEqual((step.status, step.cleanup, step.progress_after), ('UNAVAILABLE', 'VERIFIED_CLOSED', 0))
                self.assertEqual(self.objects.actions, expected)

    def test_unknown_close_preserves_progress_and_holds_workflow(self):
        self.objects.auto.remove('close_panel')
        step = self.run_wood()
        self.assertEqual((step.status, step.progress_after, step.cleanup), ('UNKNOWN', 1, 'UNVERIFIED'))
        self.assertEqual(self.objects.actions, PATH)
        self.assertEqual(self.run_wood().status, 'UNKNOWN')
        self.assertEqual(self.objects.actions, PATH)

    def test_bound_fast_invocation_survives_failed_observation_stop_or_timeout(self):
        for interruption in ('observation', 'stop', 'timeout'):
            with self.subTest(interruption=interruption):
                self.objects, self.now, self.stopped = WoodObjects(), 0, False

                def after_ack(action, wire):
                    if action != 'fast':
                        return
                    if interruption == 'observation':
                        def unavailable():
                            raise RuntimeError('connection replaced')
                        self.objects.on_observe = unavailable
                    elif interruption == 'stop':
                        self.stopped = True
                    else:
                        self.now = 6

                self.objects.after_ack = after_ack
                step = self.run_wood()
                self.assertEqual((step.status, step.mutation_attempted, step.task_confirmed, step.cleanup),
                                 ('UNKNOWN', True, False, 'UNVERIFIED'))
                self.assertEqual(self.objects.actions, PATH[:2])

    def test_exact_first_open_rejection_has_no_cleanup(self):
        for phase in ('not_invoked', 'none'):
            with self.subTest(phase=phase):
                self.objects = WoodObjects()
                self.objects.refuse = ('open', phase)
                step = self.run_wood()
                self.assertEqual((step.status, step.mutation_attempted, step.cleanup),
                                 ('BLOCKED', False, 'NOT_NEEDED'))
                self.assertEqual(self.objects.actions, [])

    def test_ambiguous_or_later_rejection_retains_hold(self):
        for case in ('echo', 'foreign_state', 'busy_state', 'stale_state', 'gui', 'restart_required', 'fast'):
            with self.subTest(case=case):
                self.objects = WoodObjects()
                self.objects.refuse = ('fast' if case == 'fast' else 'open', 'not_invoked')

                def corrupt(action, wire):
                    if wire['ack']['status'] != 'REJECTED':
                        return
                    state, payload = wire['state'], wire['ack']['result']
                    if case == 'echo':
                        payload['daily_wood_command']['expected_revision'] += 1
                    elif case == 'foreign_state':
                        state['daily_wood'].update(role_id='999', revision=state['daily_wood']['revision'] + 1)
                    elif case == 'busy_state':
                        state['daily_wood'].update(busy=True, revision=state['daily_wood']['revision'] + 1)
                    elif case == 'stale_state':
                        state['last_command_seq'] -= 1
                    elif case == 'gui':
                        state['campaign_gui']['invocation_attempted'] = True
                    elif case == 'restart_required':
                        state['lifecycle'] = 'recovery_unknown'

                self.objects.after_ack = corrupt
                step = self.run_wood()
                self.assertEqual((step.status, step.cleanup), ('UNKNOWN', 'UNVERIFIED'))
                self.assertEqual(self.objects.actions, ['open'] if case == 'fast' else [])

    def test_changed_quota_or_foreign_ack_never_authorizes_next_action(self):
        for change in ('quota', 'role', 'operation', 'echo', 'gui'):
            with self.subTest(change=change):
                self.objects = WoodObjects(2)

                def bad_fast(action, wire):
                    if action != 'fast':
                        return
                    if change == 'quota':
                        wire['state']['daily_wood']['fast_quota'] = 0
                    elif change == 'role':
                        wire['state']['daily_wood']['role_id'] = '1002'
                    elif change == 'operation':
                        wire['state']['daily_wood']['operation_id'] = 'f' * 32
                    elif change == 'echo':
                        wire['ack']['result']['daily_wood_command']['task_id'] = 5
                    else:
                        wire['ack']['result']['campaign_gui']['invocation_attempted'] = False

                self.objects.after_ack = bad_fast
                self.assertEqual(self.run_wood().status, 'UNKNOWN')
                self.assertEqual(self.objects.actions, PATH[:2])

    def test_epoch_or_stop_before_fast_and_deadline_inside_admission(self):
        for change in ('epoch', 'stop', 'deadline'):
            with self.subTest(change=change):
                self.objects, self.now, self.stopped = WoodObjects(), 0, False
                if change == 'epoch':
                    self.objects.after_ack = lambda action, wire: self.objects.advance(quota_epoch=EPOCH + 86400)
                else:
                    def blocked(action):
                        if action == 'fast':
                            if change == 'stop':
                                self.stopped = True
                                self.objects.admission_open = False
                            else:
                                self.now = 5
                    self.objects.before_submit = blocked
                self.assertEqual(self.run_wood().status, 'UNKNOWN')
                self.assertEqual(self.objects.actions, ['open'])


class WoodWorkflowTests(unittest.TestCase):
    def run_workflow(self, *, mode=None, opt_in=True):
        self.objects = WoodObjects()
        self.daily_reads = []
        self.objects.observe_daily_guild = Mock(side_effect=AssertionError('later task must not start'))
        if mode == 'uncertain_cleanup':
            def uncertain(action, wire):
                if action == 'close_panel':
                    self.objects.advance(status='UNKNOWN', reason='wood_panel_close_unverified', busy=True, cleanup_pending=True)
                    wire['state']['daily_wood'] = deepcopy(self.objects.wood)
            self.objects.after_ack = uncertain

        def read(token):
            assert token is TOKEN
            count = self.objects.wood['activity_count']
            cached, now = count, NOW
            if count == 1 and mode == 'cache_conflict':
                cached = 0
            if count == 1 and mode == 'quota_rollover':
                now = DAY + 72000
            self.daily_reads.append((count, cached, now))
            entries = [(3, activity(task_id=22, latest=count, cached=cached, required=1))]
            if mode == 'uncertain_cleanup':
                entries.append((5, activity(task_id=20, required=1)))
            value = observation(*entries, character_id=ROLE)
            value.update(server_day=DAY, server_now=now)
            return value

        self.objects.observe_daily = read
        return DailyWorkflow(self.objects, TOKEN, Path('.'), rewards_enabled=False, wood_enabled=opt_in).run()

    def test_default_disabled_and_full_opted_stock_route(self):
        value = self.run_workflow(opt_in=False)
        self.assertEqual(by_id(value)[3, 22].status, 'UNSUPPORTED')
        self.assertEqual(self.objects.actions, [])
        value = self.run_workflow()
        self.assertEqual((by_id(value)[3, 22].status, by_id(value)[3, 22].current), ('COMPLETED', 1))
        self.assertEqual(self.objects.actions, PATH)
        self.assertEqual(self.daily_reads, [(0, 0, NOW), (1, 1, NOW)])

    def test_latest_cache_disagreement_or_20_boundary_refuses_completion(self):
        for mode, reason in (('cache_conflict', 'daily_wood_progress_mismatch'),
                             ('quota_rollover', 'daily_wood_quota_epoch_changed')):
            with self.subTest(mode=mode):
                value = self.run_workflow(mode=mode)
                self.assertEqual((value.status, value.reason), ('UNKNOWN', reason))
                self.assertEqual((by_id(value)[3, 22].status, by_id(value)[3, 22].current), ('UNKNOWN', 1))
                self.assertEqual(self.objects.actions, PATH)

    def test_unverified_cleanup_preserves_progress_and_stops_later_work(self):
        value = self.run_workflow(mode='uncertain_cleanup')
        self.assertEqual((value.status, by_id(value)[3, 22].current), ('UNKNOWN', 1))
        self.assertEqual(self.objects.actions, PATH)
        self.objects.observe_daily_guild.assert_not_called()

    def test_stock_20_cutoff_and_daily_05_cutoff_are_independent(self):
        for offset, daily, quota in ((17999, DAY - 68400, EPOCH), (18000, CYCLE, EPOCH),
                                     (71999, CYCLE, EPOCH), (72000, CYCLE, DAY + 72000)):
            now = DAY + offset
            self.assertEqual(_refresh_cycle(dict(server_now=now, server_day=DAY)), daily)
            self.assertEqual(quota_epoch(now, DAY), quota)
        self.assertIsNone(quota_epoch(DAY - 1, DAY))
        self.assertIsNone(quota_epoch(DAY + 86400, DAY))
        self.assertIsNone(quota_epoch(NOW, 0))


class WoodTransportTests(unittest.TestCase):
    setUp = fixtures.BridgeTests.setUp
    client = fixtures.BridgeTests.client
    common = fixtures.BridgeTests.common
    state = fixtures.BridgeTests.state
    ack = fixtures.BridgeTests.ack

    def respond(self, client, fields, mutate=lambda state, ack: None):
        original = client._send

        def send(name, **kwargs):
            seq = original(name, **kwargs)
            value = dict(wood(), revision=2, operation_id=OP, status='OPENING',
                         next_action='', busy=True, cleanup_pending=True)
            gui, echo = receipt(seq, fields or {})
            state = self.state(client, seq)
            state.update(daily_wood=value, daily_wood_enabled=True, campaign_gui=gui)
            ack = self.ack(client, seq, name, state, 'COMPLETED')
            ack['result'].update(daily_wood=deepcopy(value), daily_wood_enabled=True,
                                 campaign_gui=deepcopy(gui))
            if fields is not None:
                ack['result']['daily_wood_command'] = echo
            mutate(state, ack)
            bridge._atomic_json(client.session_dir / 'state.json', state)
            bridge._atomic_json(client.session_dir / 'ack.json', ack)
            return seq

        client._send = Mock(side_effect=send)

    def test_disabled_and_invalid_commands_never_publish(self):
        client = self.client(); client.connect(timeout=.2)
        with self.assertRaises(bridge.BridgeError):
            client.daily_wood_observe(timeout=.2)
        with self.assertRaises(bridge.BridgeError):
            client.daily_wood_command('open', expected_revision=1, character_id=ROLE, task_id=22, operation_id=OP, timeout=.2)
        client._last_state['daily_wood_enabled'] = True
        for action, task in (('paid', 22), ('fast', 5)):
            with self.assertRaises(bridge.BridgeError):
                client.daily_wood_command(action, expected_revision=1, character_id=ROLE, task_id=task, operation_id=OP, timeout=.2)
        self.assertEqual(client._seq, 0)
        self.assertFalse((client.session_dir / 'command.txt').exists())

    def test_wire_echo_and_gui_are_bound_exactly(self):
        fields = dict(action='open', expected_revision=1, character_id=ROLE, task_id=22, operation_id=OP)
        client = self.client(); client.connect(timeout=.2)
        client._last_state['daily_wood_enabled'] = True
        self.respond(client, fields)
        client.daily_wood_command(**fields, timeout=.2)
        self.assertEqual((client.session_dir / 'command.txt').read_text().splitlines(),
            ['version=3', 'nonce=' + client.nonce, 'sequence=1', 'command=daily-wood-command',
             'action=open', 'operation_id=' + OP, 'character_id=' + ROLE, 'task_id=22', 'expected_revision=1'])

    def assert_bad_receipt(self, bad):
        fields = dict(action='open', expected_revision=1, character_id=ROLE, task_id=22, operation_id=OP)
        client = self.client(); client.connect(timeout=.2)
        client._last_state['daily_wood_enabled'] = True

        def mutate(state, ack):
            if bad == 'echo':
                ack['result']['daily_wood_command']['expected_revision'] = 2
            elif bad == 'operation':
                state['daily_wood'].update(revision=3, operation_id='f' * 32)
            else:
                ack['result']['campaign_gui']['invocation_attempted'] = False

        self.respond(client, fields, mutate)
        with self.assertRaises(bridge.BridgeUnknown):
            client.daily_wood_command(**fields, timeout=.2)
        self.assertTrue(client._unknown)
        self.assertTrue(client.lease_path.exists())

    def test_stale_echo_is_unknown(self):
        self.assert_bad_receipt('echo')

    def test_operation_replacement_is_unknown(self):
        self.assert_bad_receipt('operation')

    def test_gui_invocation_mismatch_is_unknown(self):
        self.assert_bad_receipt('gui')

    def idle_observation(self, client, *, revision=3, role=ROLE, owned=False):
        def mutate(state, ack):
            ack['result']['daily_wood'] = dict(wood(), revision=2, role_id='0', refresh_cycle=None, quota_epoch=None,
                                              activity_count=None, next_action='')
            state['daily_wood'] = dict(wood(), revision=revision, role_id=role)
            if owned:
                state['daily_wood'].update(operation_id=OP, status='OPENING', busy=True, cleanup_pending=True, next_action='')
        self.respond(client, None, mutate)

    def test_newer_unbound_idle_read_is_accepted_and_latest_role_is_checked(self):
        client = self.client(); client.connect(timeout=.2)
        client._last_state['daily_wood_enabled'] = True
        self.idle_observation(client)
        owner = SimpleNamespace(_io_lock=threading.Lock(), _lock=threading.RLock(), _client=client,
                                _campaign_check_locked=Mock(), _runtime_state=Mock())
        observed = ObjectController.observe_daily_wood(owner, TOKEN)
        self.assertEqual((observed['enabled'], observed['wood']['role_id'], observed['wood']['revision']),
                         (True, ROLE, 3))
        self.assertFalse(client._unknown)

    def test_newer_idle_read_cannot_change_owner_role(self):
        client = self.client(); client.connect(timeout=.2)
        client._last_state['daily_wood_enabled'] = True
        self.idle_observation(client, role='999')
        owner = SimpleNamespace(_io_lock=threading.Lock(), _lock=threading.RLock(), _client=client,
                                _campaign_check_locked=Mock(), _runtime_state=Mock(),
                                _campaign_admission=True, _campaign_stopping=False)
        with self.assertRaises(bridge.BridgeError):
            ObjectController.observe_daily_wood(owner, TOKEN)
        self.assertFalse(owner._campaign_admission)
        self.assertTrue(owner._campaign_stopping)

    def test_same_revision_idle_disagreement_remains_unknown(self):
        client = self.client(); client.connect(timeout=.2)
        client._last_state['daily_wood_enabled'] = True
        self.idle_observation(client, revision=2)
        with self.assertRaises(bridge.BridgeUnknown):
            client.daily_wood_observe(timeout=.2)
        self.assertTrue(client._unknown)

    def test_newer_observation_cannot_adopt_an_operation(self):
        client = self.client(); client.connect(timeout=.2)
        client._last_state['daily_wood_enabled'] = True
        self.idle_observation(client, owned=True)
        with self.assertRaises(bridge.BridgeUnknown):
            client.daily_wood_observe(timeout=.2)
        self.assertTrue(client._unknown)

    def test_object_and_bridge_deadline_gate_leave_command_unpublished(self):
        client = self.client(); client.connect(timeout=.2)
        client._last_state['daily_wood_enabled'] = True
        owner = SimpleNamespace(_io_lock=threading.Lock(), _lock=threading.RLock(), _client=client,
            _campaign_check_locked=Mock(), _campaign_admission=True, _campaign_stopping=False,
            _busy=False, _command_event_locked=Mock(), _wake=threading.Event())
        admitted = iter([True, False])
        with self.assertRaises(CampaignAdmissionClosed):
            ObjectController.submit_daily_wood(owner, TOKEN, 'open', expected_revision=1,
                character_id=ROLE, task_id=22, operation_id=OP, admission=lambda: next(admitted))
        self.assertEqual(client._seq, 0)
        self.assertFalse((client.session_dir / 'command.txt').exists())
        self.assertFalse(client._unknown)
        self.assertTrue(owner._campaign_admission)
