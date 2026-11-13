from copy import deepcopy
import unittest

from jiangshen_assistant.daily_soul import DailySoulExecutor
from jiangshen_assistant.objects import CampaignAdmissionClosed


ROLE, CYCLE, TOKEN = '12345', 1793826000, object()
PATH = ['open', 'start', 'harvest', 'close']
OPCODE = {'open': 0, 'start': 517, 'harvest': 518, 'close': 0}


class SoulBridge:
    def __init__(self, required=2, free=2):
        self.snapshot = dict(schema=1, revision=1, category_id=2, task_id=5, character_id=ROLE,
                             cycle=CYCLE, action='open', reason='', ui_cleanup='none', current=0,
                             required=required, free_remaining=free,
                             operation=self._op('IDLE', '', '', 0, None))
        self.enabled = True
        self.admission_open = True
        self.inside_admission = False
        self.actions, self.operation_ids = [], []
        self.observed = self.last_submit_observation = 0
        self.auto = set(PATH)
        self.on_observe = lambda: None
        self.before_submit = lambda action: None
        self.before_publish = lambda action: None
        self.on_ack = lambda action, wire: None

    @staticmethod
    def _op(status, stage, operation_id, sequence, before):
        return dict(status=status, reason='', stage=stage, operation_id=operation_id, sequence=sequence,
                    opcode=OPCODE.get(stage, 0), invoked=bool(stage), mutation_attempted=False,
                    start_verified=False, harvest_verified=False, counter_before=before, counter_after=None)

    def advance(self, *, op=None, **fields):
        self.snapshot['revision'] += 1
        self.snapshot.update(fields)
        if op:
            self.snapshot['operation'].update(op)

    def settle(self, action):
        s = self.snapshot
        if action == 'open':
            self.advance(action='start', op={'status': 'READY'})
        elif action == 'start':
            self.advance(action='harvest', free_remaining=s['free_remaining'] - 1,
                         op={'status': 'READY', 'start_verified': True})
        elif action == 'harvest':
            self.advance(action='close', current=s['operation']['counter_before'] + 1,
                         op={'status': 'READY', 'harvest_verified': True,
                             'counter_after': s['operation']['counter_before'] + 1})
        else:
            again = s['current'] < s['required'] and s['free_remaining'] > 0
            reason = '' if again else 'daily_soul_activity_complete' if s['current'] == s['required'] else 'daily_soul_free_blue_exhausted'
            self.advance(action='open' if again else '', reason=reason, ui_cleanup='clear',
                         op={'status': 'COMPLETED'})

    def observe_daily_soul(self, token):
        assert token is TOKEN
        self.observed += 1
        self.on_observe()
        return dict(enabled=self.enabled, soul=deepcopy(self.snapshot) if self.enabled else None)

    def check_admission(self, admission):
        self.inside_admission = True
        try:
            return self.admission_open and admission()
        finally:
            self.inside_admission = False

    def submit_daily_soul(self, token, action, *, expected_revision, character_id, task_id, operation_id, admission):
        assert token is TOKEN and character_id == ROLE and task_id == 5
        assert self.observed > self.last_submit_observation
        s = self.snapshot
        assert action == s['action'] == PATH[len(self.actions) % len(PATH)]
        assert expected_revision == s['revision']
        if action != 'open':
            assert operation_id == s['operation']['operation_id']
        self.before_submit(action)
        if not self.check_admission(admission):
            raise CampaignAdmissionClosed('admission closed after I/O lock')
        self.before_publish(action)
        if not self.check_admission(admission):
            raise CampaignAdmissionClosed('admission closed before publication')
        self.actions.append(action)
        self.operation_ids.append(operation_id)
        self.last_submit_observation = self.observed
        seq = 100 + self.observed
        status = {'open': 'OPENING', 'start': 'PENDING', 'harvest': 'PENDING', 'close': 'CLOSING'}[action]
        if action == 'open':
            s['operation'] = self._op(status, action, operation_id, seq, s['current'])
        self.advance(action='', reason='', ui_cleanup='none',
                     op=dict(stage=action, opcode=OPCODE[action], sequence=seq, status=status,
                             mutation_attempted=action != 'open'))
        wire = dict(requested_seq=seq, ack=dict(command_name='daily-soul-command', command_seq=seq,
                    status='COMPLETED', result=dict(daily_soul_enabled=True, daily_soul=deepcopy(s),
                    campaign_gui=dict(command_seq=seq, phase='completed', invocation_attempted=True))))
        if action in self.auto:
            self.settle(action)
        self.on_ack(action, wire)
        return wire


class DailySoulTests(unittest.TestCase):
    def setUp(self):
        self.bridge = SoulBridge()
        self.stopped = self.paused = False
        self.now = 0

    def wait(self):
        self.now += 1

    def executor(self, wait=None):
        def read_control(name):
            self.assertFalse(self.bridge.inside_admission, 'coordinator controls cannot run under the object lock')
            return getattr(self, name)

        return DailySoulExecutor(self.bridge, TOKEN, stopped=lambda: read_control('stopped'), paused=lambda: read_control('paused'),
                                 wait=wait or self.wait, timeout=5, clock=lambda: self.now)

    def execute(self, *, current=0, required=None, executor=None):
        return (executor or self.executor()).execute(5, character_id=ROLE, expected_cycle=CYCLE,
            expected_current=current, expected_required=required or self.bridge.snapshot['required'])

    def test_each_stock_command_requires_fresh_observation_and_new_cycle_executor(self):
        first = self.executor()
        result = self.execute(executor=first)
        self.assertEqual(self.bridge.actions, PATH)
        self.assertEqual((result.status, result.progress_before, result.progress_after, result.required, result.cleanup),
                         ('COMPLETED', 0, 1, 2, 'VERIFIED_CLOSED'))
        self.assertTrue(result.start_verified and result.harvest_verified and result.account_progress_observed)
        self.assertEqual(len(set(self.bridge.operation_ids)), 1)
        self.assertEqual(self.execute(executor=first).status, 'BLOCKED')
        self.assertEqual(self.bridge.actions, PATH)
        # The caller obtains a new coherent observation and a fresh executor.
        latest = self.bridge.observe_daily_soul(TOKEN)['soul']
        result = self.execute(current=latest['current'])
        self.assertEqual(self.bridge.actions, PATH * 2)
        self.assertEqual(len(set(self.bridge.operation_ids)), 2)
        self.assertEqual((result.status, result.progress_before, result.progress_after, result.required), ('COMPLETED', 1, 2, 2))

    def test_second_cycle_unavailable_when_free_quota_exhausted(self):
        self.bridge = SoulBridge(free=1)
        self.assertEqual(self.execute().status, 'COMPLETED')
        result = self.execute(current=1)
        self.assertEqual((result.status, result.reason, result.progress_after), ('UNAVAILABLE', 'daily_soul_free_blue_exhausted', 1))
        self.assertEqual(self.bridge.actions, PATH)

    def test_initial_owner_cycle_and_counter_require_exact_match(self):
        for field, value in (('character_id', '12'), ('cycle', CYCLE + 86400), ('current', 1), ('required', 3)):
            with self.subTest(field=field):
                self.bridge = SoulBridge()
                self.bridge.advance(**{field: value})
                result = self.execute(required=2)
                self.assertEqual((result.status, result.cleanup), ('BLOCKED', 'NOT_NEEDED'))
                self.assertFalse(self.bridge.actions)
        self.bridge = SoulBridge()
        self.bridge.enabled = False
        self.assertEqual(self.execute().status, 'UNAVAILABLE')
        self.bridge.enabled = True
        self.bridge.advance(current=2, action='')
        self.assertEqual(self.execute(current=2).status, 'ALREADY_COMPLETE')

    def test_foreign_or_unresolved_prior_operation_blocks_repeat(self):
        result = self.execute()
        self.assertEqual(result.status, 'COMPLETED')
        self.bridge.advance(ui_cleanup='unknown', op={'status': 'UNKNOWN'})
        repeat = self.execute(current=1)
        self.assertEqual((repeat.status, repeat.cleanup), ('UNKNOWN', 'UNVERIFIED'))
        self.assertEqual(self.bridge.actions, PATH)

    def test_quota_and_opcode_mismatch_never_send_followup(self):
        for key, value in (('opcode', 519), ('operation_id', 'a' * 32), ('stage', 'harvest')):
            with self.subTest(key=key):
                self.bridge = SoulBridge()
                self.bridge.on_ack = lambda action, wire: self.bridge.advance(op={key: value}) if action == 'start' else None
                self.assertEqual(self.execute().status, 'UNKNOWN')
                self.assertEqual(self.bridge.actions, PATH[:2])
        self.bridge = SoulBridge()
        self.bridge.on_ack = lambda action, wire: self.bridge.advance(free_remaining=2) if action == 'start' else None
        self.assertEqual(self.execute().status, 'UNKNOWN')
        self.assertEqual(self.bridge.actions, PATH[:2])

    def test_ack_binding_and_authoritative_newer_state(self):
        def corrupt_ack(kind, wire):
            ack = wire['ack']
            if kind == 'sequence':
                ack['command_seq'] += 1
            elif kind == 'enabled':
                ack['result']['daily_soul_enabled'] = 1
            elif kind == 'name':
                ack['command_name'] = 'daily-arena-command'
            elif kind == 'invocation':
                ack['result']['campaign_gui']['invocation_attempted'] = False
            elif kind == 'latest_regressed':
                wire['state']['daily_soul']['revision'] = 1
            elif kind == 'same_revision_disagrees':
                wire['state']['daily_soul']['revision'] = ack['result']['daily_soul']['revision']
            elif kind == 'latest_gui_changed':
                wire['state']['campaign_gui']['command_seq'] += 1
            else:
                wire['state']['daily_soul']['operation']['operation_id'] = 'a' * 32

        for kind in ('sequence', 'enabled', 'name', 'invocation', 'latest_regressed',
                     'same_revision_disagrees', 'latest_foreign', 'latest_gui_changed'):
            with self.subTest(kind=kind):
                self.bridge = SoulBridge()

                def intercept(action, wire):
                    wire['state'] = dict(daily_soul_enabled=True, daily_soul=deepcopy(self.bridge.snapshot),
                                         campaign_gui=deepcopy(wire['ack']['result']['campaign_gui']))
                    corrupt_ack(kind, wire)

                self.bridge.on_ack = intercept
                self.assertEqual(self.execute().status, 'UNKNOWN')
                self.assertEqual(self.bridge.actions, ['open'])

        self.bridge = SoulBridge()
        self.bridge.on_ack = lambda action, wire: wire.update(
            state=dict(daily_soul_enabled=True, daily_soul=deepcopy(self.bridge.snapshot),
                       campaign_gui=deepcopy(wire['ack']['result']['campaign_gui'])))
        self.assertEqual(self.execute().status, 'COMPLETED')

    def test_early_progress_still_requires_stock_harvest_and_close(self):
        self.bridge = SoulBridge(required=1)
        self.bridge.on_ack = lambda action, wire: self.bridge.advance(current=1, op={'counter_after': 1}) if action == 'start' else None
        result = self.execute()
        self.assertEqual((result.status, result.progress_after, result.cleanup), ('COMPLETED', 1, 'VERIFIED_CLOSED'))
        self.assertEqual(self.bridge.actions, PATH)

    def test_stale_observation_cannot_undo_newer_wire_state(self):
        def intercept(action, wire):
            if action == 'start':
                wire['state'] = dict(daily_soul_enabled=True, daily_soul=deepcopy(self.bridge.snapshot))
                self.bridge.snapshot = deepcopy(wire['ack']['result']['daily_soul'])

        self.bridge.on_ack = intercept
        self.assertEqual(self.execute().status, 'UNKNOWN')
        self.assertEqual(self.bridge.actions, PATH[:2])

    def test_confirmed_progress_survives_close_failure(self):
        def reject_close(action, wire):
            if action == 'close':
                wire['ack'].update(status='UNKNOWN', reason='daily_soul_close_unverified')

        self.bridge.on_ack = reject_close
        result = self.execute()
        self.assertEqual(self.bridge.actions, PATH)
        self.assertEqual((result.status, result.progress_after, result.cleanup), ('UNKNOWN', 1, 'UNVERIFIED'))
        self.assertTrue(result.harvest_verified and result.account_progress_observed)

    def test_losing_proven_progress_in_a_newer_harvest_receipt_blocks_close(self):
        def intercept(action, wire):
            if action == 'start':
                self.bridge.advance(current=1, op={'counter_after': 1})
            elif action == 'harvest':
                receipt = wire['ack']['result']['daily_soul']
                receipt['current'] = 0
                receipt['operation']['counter_after'] = None

        self.bridge.on_ack = intercept
        result = self.execute()
        self.assertEqual((result.status, result.progress_after, result.account_progress_observed), ('UNKNOWN', 1, True))
        self.assertEqual(self.bridge.actions, PATH[:3])

    def test_role_cycle_or_session_changes_abort_after_admission(self):
        for kind in ('role', 'cycle', 'session'):
            with self.subTest(kind=kind):
                self.bridge = SoulBridge()

                def intercept(action, wire):
                    if action == 'start':
                        if kind == 'role':
                            self.bridge.advance(character_id='12')
                        if kind == 'cycle':
                            self.bridge.advance(cycle=CYCLE + 86400)
                        if kind == 'session':
                            def replaced():
                                raise RuntimeError('object_connection_replaced')
                            self.bridge.on_observe = replaced

                self.bridge.on_ack = intercept
                self.assertEqual(self.execute().status, 'UNKNOWN')
                self.assertEqual(self.bridge.actions, PATH[:2])

    def test_timeout_after_blocking_observation_and_late_callbacks_never_send(self):
        self.bridge.on_observe = lambda: setattr(self, 'now', 6)
        self.assertEqual(self.execute().status, 'BLOCKED')
        self.assertFalse(self.bridge.actions)

        self.bridge = SoulBridge()
        self.now = 0
        self.bridge.auto.remove('harvest')

        def after_observe():
            if self.bridge.actions == PATH[:3]:
                self.now += 6
                self.bridge.settle('harvest')

        self.bridge.on_observe = after_observe
        executor = self.executor()
        result = self.execute(executor=executor)
        self.assertEqual((result.status, result.reason, result.progress_after), ('UNKNOWN', 'soul_readback_timeout', 1))
        self.assertEqual(self.execute(executor=executor), result)
        self.assertEqual(self.bridge.actions, PATH[:3])

    def test_paused_pending_callback_is_observed_without_sending(self):
        self.bridge.auto.remove('start')
        observed_before_pause = 0

        def intercept(action, wire):
            nonlocal observed_before_pause
            if action == 'start':
                observed_before_pause = self.bridge.observed
                self.paused = True

        def wait():
            self.now += 1
            self.assertEqual(self.bridge.actions, PATH[:2])
            self.assertGreater(self.bridge.observed, observed_before_pause)
            self.bridge.settle('start')
            self.paused = False

        self.bridge.on_ack = intercept
        result = self.execute(executor=self.executor(wait))
        self.assertEqual(result.status, 'COMPLETED')
        self.assertEqual(self.bridge.actions, PATH)

    def test_stop_and_admission_closed_do_not_send(self):
        self.stopped = True
        self.assertEqual(self.execute().status, 'STOPPED')
        self.assertFalse(self.bridge.actions)
        self.stopped = False

        def request_stop(action):
            if action == 'start':
                self.stopped = True
                self.bridge.admission_open = False

        self.bridge.before_submit = request_stop
        result = self.execute()
        self.assertEqual((result.status, result.reason), ('UNKNOWN', 'user_stop'))
        self.assertEqual(self.bridge.actions, ['open'])

    def test_deadline_expires_before_publication_without_a_late_send(self):
        for hook in ('before_submit', 'before_publish'):
            for target, actions, status, reason, progress in (
                    ('open', [], 'BLOCKED', 'soul_admission_timeout', 0),
                    ('start', ['open'], 'UNKNOWN', 'soul_readback_timeout', 0),
                    ('close', PATH[:3], 'UNKNOWN', 'soul_readback_timeout', 1)):
                with self.subTest(hook=hook, action=target):
                    self.bridge, self.now = SoulBridge(), 0

                    def blocked(action):
                        if action == target:
                            self.now = 5

                    setattr(self.bridge, hook, blocked)
                    executor = self.executor()
                    result = self.execute(executor=executor)
                    self.assertEqual((result.status, result.reason, result.progress_after), (status, reason, progress))
                    self.assertEqual(self.bridge.actions, actions)
                    self.execute(executor=executor)
                    self.assertEqual(self.bridge.actions, actions)

    def test_pause_after_io_lock_requires_fresh_read_before_resuming(self):
        reads = None

        def pause(action):
            nonlocal reads
            if action == 'start' and reads is None:
                reads = self.bridge.observed
                self.paused = True
                self.bridge.admission_open = False

        def wait():
            self.now += 1
            self.assertGreater(self.bridge.observed, reads)
            self.assertEqual(self.bridge.actions, ['open'])
            self.paused = False
            self.bridge.admission_open = True

        self.bridge.before_submit = pause
        self.assertEqual(self.execute(executor=self.executor(wait)).status, 'COMPLETED')
        self.assertEqual(self.bridge.actions, PATH)

    def test_unknown_517_never_retries_after_late_stock_callback(self):
        self.bridge.auto.remove('start')
        executor = self.executor()
        result = self.execute(executor=executor)
        self.assertEqual(result.status, 'UNKNOWN')
        self.bridge.settle('start')
        self.assertEqual(self.execute(executor=executor), result)
        self.assertEqual(self.bridge.actions, PATH[:2])
