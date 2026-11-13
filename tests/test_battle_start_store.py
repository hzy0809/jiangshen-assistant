"""Cross-process start admission and real coordinator integration, all offline."""
from dataclasses import replace
import copy
import hashlib
import json
import multiprocessing
from pathlib import Path
import subprocess
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

from jiangshen_assistant.battle_start_store import BattleStartError, BattleStartKey, BattleStartStore
from jiangshen_assistant.campaign import OBSERVED_LOSS_EVIDENCE
from jiangshen_assistant.campaign_store import CampaignStore
from jiangshen_assistant.campaign_task import CampaignCoordinator
from jiangshen_assistant.challenge_task import ChallengeCoordinator
from jiangshen_assistant.objects import CampaignAdmissionClosed
from tests.test_campaign_pending_story import NativeStory, Runner


ROLE = '55290000000275'
KEY = BattleStartKey(ROLE, 'NORMAL_CHALLENGE', 143, 3)
RUN = '1' * 32
ATTEMPT = '2' * 32


def context(pid=696, role=ROLE):
    return json.dumps(dict(character_id=role, pid=pid, creation=134384970393143907 + pid,
                           generation=2, nonce='a' * 32), sort_keys=True, separators=(',', ':'))


def result(attempt_id=ATTEMPT, *, outcome='WIN', report_id='777', **changes):
    return dict(status='SETTLED', attempt_id=attempt_id, character_id=ROLE, command_seq=7,
                campaign_id=143, troop_id=3, report_id=report_id, outcome=outcome,
                evidence='captured_authoritative_receipt_applied', **changes)


def legacy_unknown():
    return dict(version=1, identity=KEY.encode(), closed=[], active=dict(
        intent_id=ATTEMPT, run_id=RUN, context_key=context(), status='UNKNOWN', command_seq=7,
        outcome='UNKNOWN', report_id='0', evidence='', reason='native_lifecycle_or_thread_unverified'))


def write_legacy(directory, *, with_lock=False, data=None):
    store = BattleStartStore(Path(directory) / 'battle_start_intents')
    store.directory.mkdir(parents=True, exist_ok=True)
    store.path_for(KEY).write_text(json.dumps(legacy_unknown() if data is None else data), encoding='utf-8')
    if with_lock:
        store.path_for(KEY).with_suffix('.lock').write_bytes(b'\0')
    return store


def inject_publication_failure(store, stage, *, enabled=lambda: True, prefix='publication_'):
    boundary, timing = stage.rsplit('_', 1)
    method = {'anchor': '_write_anchor', 'record': '_write_record', 'readback': '_readback_pair'}[boundary]
    original = getattr(store, method)
    def fail(*args, **kwargs):
        if enabled() and timing == 'before':
            raise OSError(prefix + stage)
        result = original(*args, **kwargs)
        if enabled() and timing == 'after':
            raise OSError(prefix + stage)
        return result
    setattr(store, method, fail)


def _contend(directory, ready, go, replies, index):
    store = BattleStartStore(directory)
    ready.put(index)
    go.wait(5)
    try:
        store.reserve(KEY, '%032x' % (index + 1), context(900 + index), '%032x' % (index + 30))
        replies.put('reserved')
    except BattleStartError:
        replies.put('blocked')


def _hold_lock(directory, connection):
    with BattleStartStore(directory)._locked(KEY):
        connection.send('locked')
        connection.recv()


class BattleStartStoreTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.store = BattleStartStore(Path(self.temp.name) / 'battle_start_intents')

    def reserve(self):
        return self.store.reserve(KEY, RUN, context(), ATTEMPT)

    def test_new_run_and_pid_cannot_clear_reserved_unknown_or_submitted(self):
        reservation = self.reserve()
        for phase in ('RESERVED', 'SUBMITTED', 'UNKNOWN'):
            if phase == 'SUBMITTED':
                self.store.submitted(reservation, 7)
            if phase == 'UNKNOWN':
                self.store.mark_unknown(reservation, 'timeout')
            with self.subTest(phase=phase):
                fresh = BattleStartStore(self.store.directory)
                with self.assertRaisesRegex(BattleStartError, 'unresolved'):
                    fresh.reserve(KEY, '3' * 32, context(999), '4' * 32)
                self.assertEqual(fresh.read(KEY)['active']['status'], phase)

    def test_process_exit_after_durable_reserve_never_reopens_admission(self):
        script = ('import os,sys; from tests.test_battle_start_store import KEY,RUN,ATTEMPT,context; '
                  'from jiangshen_assistant.battle_start_store import BattleStartStore; '
                  'BattleStartStore(sys.argv[1]).reserve(KEY,RUN,context(),ATTEMPT); os._exit(23)')
        exited = subprocess.run([sys.executable, '-c', script, str(self.store.directory)],
                                capture_output=True, text=True, timeout=10)
        self.assertEqual(exited.returncode, 23, exited.stderr)
        self.assertEqual(self.store.read(KEY)['active']['status'], 'RESERVED')
        with self.assertRaisesRegex(BattleStartError, 'unresolved'):
            self.store.reserve(KEY, '3' * 32, context(999), '4' * 32)

    def test_two_processes_have_at_most_one_reservation(self):
        spawn = multiprocessing.get_context('spawn')
        ready, replies, go = spawn.Queue(), spawn.Queue(), spawn.Event()
        workers = [spawn.Process(target=_contend, args=(str(self.store.directory), ready, go, replies, i))
                   for i in range(2)]
        try:
            for worker in workers:
                worker.start()
            self.assertEqual({ready.get(timeout=10), ready.get(timeout=10)}, {0, 1})
            go.set()
            answers = [replies.get(timeout=10), replies.get(timeout=10)]
            self.assertEqual(sorted(answers), ['blocked', 'reserved'])
        finally:
            go.set()
            for worker in workers:
                worker.join(5)
                if worker.is_alive():
                    worker.terminate()
                    worker.join(5)
            ready.close()
            replies.close()
        self.assertTrue(self.store.read(KEY)['active'])

    def test_os_lock_rejects_contender_and_process_death_releases_only_lock(self):
        self.store.read(KEY)  # A verified empty target is safe after lock-owner death.
        spawn = multiprocessing.get_context('spawn')
        parent, child = spawn.Pipe()
        worker = spawn.Process(target=_hold_lock, args=(str(self.store.directory), child))
        worker.start()
        try:
            self.assertTrue(parent.poll(10))
            self.assertEqual(parent.recv(), 'locked')
            with self.assertRaisesRegex(BattleStartError, 'lock_unavailable'):
                self.reserve()
        finally:
            worker.terminate()
            worker.join(5)
            parent.close()
            child.close()
        self.reserve()

    def test_role_mode_and_target_are_distinct_but_context_pid_is_not(self):
        self.reserve()
        alternatives = (replace(KEY, character_id='55290000000276'),
                        replace(KEY, mode='MAINLINE'), replace(KEY, troop_id=4))
        for key in alternatives:
            self.store.reserve(key, '3' * 32, context(999, key.character_id), '4' * 32)
        self.assertEqual(len(list(self.store.directory.glob('*.json'))), 4)

    def test_exact_success_duplicate_and_later_reservation_are_independent(self):
        reservation = self.reserve()
        self.store.submitted(reservation, 7)
        self.assertTrue(self.store.complete(reservation, result()))
        later = self.store.reserve(KEY, '3' * 32, context(999), '4' * 32)
        self.assertFalse(self.store.complete(reservation, result()))
        self.assertEqual(self.store.read(KEY)['active']['intent_id'], later.intent_id)
        conflict = result(outcome='LOSS')
        with self.assertRaisesRegex(BattleStartError, 'conflicting_result'):
            self.store.complete(reservation, conflict)
        self.assertEqual(self.store.read(KEY)['active']['intent_id'], later.intent_id)
        with self.assertRaisesRegex(BattleStartError, 'mismatch'):
            self.store.complete(later, result())

    def test_reportless_observed_loss_cannot_close_admission(self):
        reservation = self.reserve()
        self.store.submitted(reservation, 7)
        loss = result(outcome='LOSS', report_id='0')
        loss['evidence'] = OBSERVED_LOSS_EVIDENCE
        before = self.store.path_for(KEY).read_bytes()
        for _ in range(2):
            with self.assertRaisesRegex(BattleStartError, 'loss_observed_settlement_unconfirmed'):
                self.store.complete(reservation, loss)
            self.assertEqual(self.store.path_for(KEY).read_bytes(), before)
        self.assertEqual(self.store.read(KEY)['active']['intent_id'], reservation.intent_id)
        self.assertEqual(self.store.read(KEY)['closed'], [])
        with self.assertRaisesRegex(BattleStartError, 'unresolved'):
            self.store.reserve(KEY, '3' * 32, context(999), '4' * 32)

    def test_old_terminal_ui_loss_record_blocks_instead_of_reopening(self):
        reservation = self.reserve()
        self.store.submitted(reservation, 7)
        data = self.store.read(KEY)
        data['closed'].append(dict(data['active'], status='COMPLETED', outcome='LOSS',
                                  report_id='0', evidence=OBSERVED_LOSS_EVIDENCE))
        data['active'] = None
        self.store.path_for(KEY).write_text(json.dumps(data), encoding='utf-8')
        before = self.store.path_for(KEY).read_bytes()
        with self.assertRaisesRegex(BattleStartError, 'loss_observed_settlement_unconfirmed'):
            self.store.reserve(KEY, '3' * 32, context(999), '4' * 32)
        self.assertEqual(self.store.path_for(KEY).read_bytes(), before)

    def test_unknown_and_identity_evidence_seq_mismatch_never_complete(self):
        reservation = self.reserve()
        self.store.submitted(reservation, 7)
        negatives = [dict(status='UNKNOWN'), dict(character_id='55290000000276'),
                     dict(campaign_id=144), dict(troop_id=4), dict(command_seq=8),
                     dict(command_seq=True), dict(evidence='balance_unchanged'),
                     dict(evidence='anchored_mainline_progress_applied'), dict(report_id='0'),
                     dict(attempt_id='5' * 32)]
        for changes in negatives:
            attempt = result()
            attempt.update(changes)
            with self.subTest(changes=changes), self.assertRaises(BattleStartError):
                self.store.complete(reservation, attempt)
            self.assertIsNotNone(self.store.read(KEY)['active'])

    def test_only_exact_unsubmitted_rejection_can_release(self):
        reservation = self.reserve()
        with self.assertRaises(BattleStartError):
            self.store.reject(reservation, 'balance_unchanged', reason='same resources')
        self.store.reject(reservation, 'rejected_without_invocation', command_seq=7, reason='native_no_resource')
        later = self.store.reserve(KEY, '3' * 32, context(999), '4' * 32)
        self.store.submitted(later, 8)
        with self.assertRaisesRegex(BattleStartError, 'possible_invocation'):
            self.store.reject(later, 'cancelled_before_submit', reason='cancel requested')
        self.assertEqual(self.store.read(KEY)['active']['intent_id'], later.intent_id)

    def test_corruption_dirty_schema_duplicate_keys_and_wrong_identity_block(self):
        self.reserve()
        original = self.store.path_for(KEY).read_bytes()
        dirty = [b'{', b'{"version":1,"version":1}', b'[]']
        data = json.loads(original)
        for mutate in (lambda d: d['identity'].update(character_id='999'),
                       lambda d: d['active'].update(command_seq=True),
                       lambda d: d['active'].update(uncertain=True),
                       lambda d: d.update(active={}),
                       lambda d: d['active'].update(context_key=context(role='999'))):
            changed = copy.deepcopy(data)
            mutate(changed)
            dirty.append(json.dumps(changed).encode())
        for blob in dirty:
            self.store.path_for(KEY).write_bytes(blob)
            with self.subTest(blob=blob), self.assertRaises(BattleStartError):
                self.store.reserve(KEY, '3' * 32, context(999), '4' * 32)
            self.assertEqual(self.store.path_for(KEY).read_bytes(), blob)

    def test_malformed_terminal_rejection_never_reopens_admission(self):
        reservation = self.reserve()
        self.store.reject(reservation, 'cancelled_before_submit', reason='owning port cancelled')
        valid = self.store.read(KEY)
        for change in (dict(reason='balance_unchanged'), dict(command_seq=7),
                       dict(reason='rejected_without_invocation: no invocation', command_seq=0)):
            data = copy.deepcopy(valid)
            data['closed'][0].update(change)
            self.store.path_for(KEY).write_text(json.dumps(data), encoding='utf-8')
            with self.subTest(change=change), self.assertRaises(BattleStartError):
                self.store.reserve(KEY, '3' * 32, context(999), '4' * 32)

    def test_fsync_or_replace_failure_never_publishes_initial_or_terminal_state(self):
        for operation in ('fsync', 'replace'):
            with self.subTest(operation=operation), tempfile.TemporaryDirectory() as directory:
                self.store = BattleStartStore(Path(directory) / 'failed_initialization')
                with patch('jiangshen_assistant.battle_start_store.os.' + operation,
                           side_effect=OSError('disk failure')):
                    with self.assertRaises(OSError):
                        self.reserve()
                self.assertFalse(self.store.path_for(KEY).exists())
                with self.assertRaisesRegex(BattleStartError, 'record_missing'):
                    self.reserve()
                # Use a different, genuinely new target root for terminal checks.
                self.store = BattleStartStore(Path(directory) / 'terminal_failure')
                reservation = self.reserve()
                self.store.submitted(reservation, 7)
                before = self.store.path_for(KEY).read_bytes()
                with patch('jiangshen_assistant.battle_start_store.os.' + operation,
                           side_effect=OSError('disk failure')):
                    with self.assertRaises(OSError):
                        self.store.complete(reservation, result())
                self.assertEqual(self.store.path_for(KEY).read_bytes(), before)

    def test_first_read_durably_initializes_empty_target_then_reserve_works(self):
        empty = self.store.read(KEY)
        self.assertEqual(empty['version'], 3)
        self.assertIsNone(empty['active'])
        anchor = json.loads(self.store.anchor_path_for(KEY).read_text())
        self.assertEqual(empty['anchor_id'], anchor['anchor_id'])
        self.assertEqual(anchor['generation'], empty['generation'])
        self.assertEqual(anchor['record_sha256'], hashlib.sha256(self.store.path_for(KEY).read_bytes()).hexdigest())
        reservation = self.reserve()
        self.assertEqual(self.store.read(KEY)['active']['intent_id'], reservation.intent_id)

    def test_unknown_json_loss_with_surviving_anchor_or_legacy_lock_never_initializes_empty(self):
        reservation = self.reserve()
        self.store.mark_unknown(reservation, 'timeout')
        anchor_before = self.store.anchor_path_for(KEY).read_bytes()
        self.store.path_for(KEY).unlink()
        with self.assertRaisesRegex(BattleStartError, 'record_missing'):
            self.store.reserve(KEY, '3' * 32, context(999), '4' * 32)
        self.assertFalse(self.store.path_for(KEY).exists())
        self.assertEqual(self.store.anchor_path_for(KEY).read_bytes(), anchor_before)
        self.store.anchor_path_for(KEY).unlink()
        # Legacy files have no anchor: the surviving old lock still blocks.
        with self.assertRaisesRegex(BattleStartError, 'record_missing'):
            self.store.read(KEY)
        self.assertFalse(self.store.path_for(KEY).exists())

    def test_v3_missing_or_mismatching_anchor_is_not_implicitly_repaired(self):
        self.reserve()
        original = self.store.anchor_path_for(KEY).read_bytes()
        for blob in (None, b'{', original.replace(b'"anchor_id":"', b'"extra":true,"anchor_id":"')):
            if blob is None:
                self.store.anchor_path_for(KEY).unlink()
            else:
                self.store.anchor_path_for(KEY).write_bytes(blob)
            with self.subTest(blob=blob), self.assertRaises(BattleStartError):
                self.store.reserve(KEY, '3' * 32, context(999), '4' * 32)
            self.store.anchor_path_for(KEY).write_bytes(original)
        anchor = json.loads(original)
        anchor['anchor_id'] = '9' * 32
        self.store.anchor_path_for(KEY).write_text(json.dumps(anchor), encoding='utf-8')
        with self.assertRaisesRegex(BattleStartError, 'anchor_mismatch'):
            self.store.read(KEY)

    def test_legacy_unknown_migrates_with_or_without_old_lock_and_cannot_be_replayed(self):
        for with_lock in (False, True):
            with self.subTest(with_lock=with_lock), tempfile.TemporaryDirectory() as directory:
                store = write_legacy(directory, with_lock=with_lock)
                before = store.path_for(KEY).read_bytes()
                expected = legacy_unknown()['active']
                migrated = store.read(KEY)
                self.assertEqual(migrated['version'], 3)
                self.assertEqual(migrated['active'], expected)
                self.assertEqual(migrated['closed'], [])
                self.assertEqual(json.loads(store.anchor_path_for(KEY).read_text())['record_sha256'],
                                 hashlib.sha256(store.path_for(KEY).read_bytes()).hexdigest())
                with self.assertRaisesRegex(BattleStartError, 'unresolved'):
                    store.reserve(KEY, '3' * 32, context(999), '4' * 32)
                store.path_for(KEY).write_bytes(before)
                with self.assertRaisesRegex(BattleStartError, 'legacy_anchor_mismatch'):
                    store.read(KEY)

    def test_full_legacy_schema_and_identity_are_validated_before_any_anchor_write(self):
        invalid = []
        for mutate in (lambda d: d.update(extra=True), lambda d: d['identity'].update(character_id='999'),
                       lambda d: d['active'].update(command_seq=True),
                       lambda d: d['closed'].append(dict(d['active'], status='COMPLETED', outcome='LOSS',
                                                       report_id='0', evidence=OBSERVED_LOSS_EVIDENCE))):
            data = legacy_unknown()
            mutate(data)
            invalid.append(data)
        for data in invalid:
            with self.subTest(data=data), tempfile.TemporaryDirectory() as directory:
                store = write_legacy(directory, with_lock=True, data=data)
                before = store.path_for(KEY).read_bytes()
                with self.assertRaises(BattleStartError):
                    store.read(KEY)
                self.assertEqual(store.path_for(KEY).read_bytes(), before)
                self.assertFalse(store.anchor_path_for(KEY).exists())

    def test_old_verified_empty_json_cannot_roll_back_a_later_active_intent(self):
        self.store.read(KEY)
        empty_bytes = self.store.path_for(KEY).read_bytes()
        reservation = self.reserve()
        self.store.mark_unknown(reservation, 'timeout')
        anchor_before = self.store.anchor_path_for(KEY).read_bytes()
        self.store.path_for(KEY).write_bytes(empty_bytes)
        with self.assertRaisesRegex(BattleStartError, 'record_anchor_mismatch'):
            self.store.reserve(KEY, '3' * 32, context(999), '4' * 32)
        self.assertEqual(self.store.anchor_path_for(KEY).read_bytes(), anchor_before)
        self.assertEqual(self.store.path_for(KEY).read_bytes(), empty_bytes)

    def test_unbound_old_v2_and_inactive_v1_cannot_be_upgraded_to_permission(self):
        for data in (dict(version=2, identity=KEY.encode(), anchor_id='a' * 32, active=None, closed=[]),
                     dict(version=1, identity=KEY.encode(), active=None, closed=[])):
            with self.subTest(version=data['version']), tempfile.TemporaryDirectory() as directory:
                store = write_legacy(directory, data=data)
                before = store.path_for(KEY).read_bytes()
                with self.assertRaises(BattleStartError):
                    store.reserve(KEY, '3' * 32, context(999), '4' * 32)
                self.assertEqual(store.path_for(KEY).read_bytes(), before)
                self.assertFalse(store.anchor_path_for(KEY).exists())


class AdmissionBattle(NativeStory):
    """Live-shaped DTO with separately counted port calls and native invocations."""
    def __init__(self, *, challenge=False, role=ROLE, pid=696, outcomes=(), behavior='normal'):
        super().__init__(challenge=challenge, story=False)
        self.role, self.pid, self.behavior = role, pid, behavior
        self.outcomes = list(outcomes)
        self.invocations = 0
        self.request_stop = False
        self.mutate_settlement = None
        self.snap.update(character_id=role, campaign_id=143, troop_id=3)

    def acquire_campaign(self, run_id, **kwargs):
        self.owner = SimpleNamespace(context_key=context(self.pid, self.role))
        return self.owner

    def observe_campaign(self, token):
        assert token is self.owner
        self.now += .1
        attempt = self.snap['attempt']
        if attempt['status'] == 'WAITING' and self.outcomes:
            outcome = self.outcomes.pop(0)
            if outcome == 'UNKNOWN':
                attempt.update(status='UNKNOWN')
            else:
                attempt.update(status='SETTLED', outcome=outcome, report_id=str(100 + self.invocations),
                               evidence='captured_authoritative_receipt_applied')
                if self.mutate_settlement:
                    self.mutate_settlement(attempt)
                self.publish(phase='RESULT', result_kind='NORMAL')
        return dict(context_key=token.context_key, campaign=copy.deepcopy(self.snap), stopping=self.stopping)

    def submit_campaign(self, token, action, **kwargs):
        if self.before_submit:
            self.before_submit(self, action)
        if action == 'start' and self.behavior == 'admission_closed':
            self.request_stop = True
            raise CampaignAdmissionClosed('closed before admission')
        self.commands.append((action, copy.deepcopy(kwargs)))
        if action == 'start' and self.behavior in ('reject', 'cancel'):
            self.request_stop = True
            return (dict(cancelled_before_submit=True) if self.behavior == 'cancel' else
                    self.receipt(action, kwargs, status='REJECTED', invoked=False, reason='no_resource'))
        if action == 'start':
            self.invocations += 1
            self.snap['attempt'].update(status='WAITING', attempt_id=kwargs['attempt_id'],
                character_id=self.role, command_seq=len(self.commands), campaign_id=143, troop_id=3)
            self.publish(phase='BATTLE')
            if self.behavior == 'exception':
                raise RuntimeError('start_ack_timeout')
        elif action in ('ack_result', 'next_chapter'):
            if self.outcomes:
                self.snap['attempt'] = dict(status='NONE', attempt_id='', character_id='', command_seq=0,
                    campaign_id=0, troop_id=0, report_id='0', outcome='UNKNOWN', evidence='')
                self.publish(phase='FORMATION', result_kind='')
            else:
                self.publish(available=False, gate='NO_RESOURCE',
                             flow=dict(status='UNSUPPORTED', action='', reason='NO_RESOURCE'))
        else:
            raise AssertionError('unexpected command: ' + action)
        return self.receipt(action, kwargs)


class CoordinatorStartAdmissionTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def run_battle(self, battle, configure=None):
        runner = Runner()
        coordinator_type = ChallengeCoordinator if battle.challenge else CampaignCoordinator
        coordinator = coordinator_type(battle, runner, self.root, poll_seconds=.001,
            clock=lambda: battle.now, battle_timeout=.5, stop_timeout=.3, transition_timeout=.4)
        battle.coordinator = coordinator
        coordinator._wait = lambda timeout=None: None
        if configure:
            configure(coordinator)
        self.assertTrue(coordinator.start())
        class Context:
            is_paused = False
            @property
            def cancelled(self):
                return battle.request_stop
        runner.task(Context())
        self.assertFalse(coordinator.is_active)
        return coordinator

    def test_reservation_is_durable_before_real_coordinator_submit_and_unknown_blocks_new_pid(self):
        for challenge in (False, True):
            with self.subTest(challenge=challenge):
                battle = AdmissionBattle(challenge=challenge, outcomes=['UNKNOWN'])
                def inspect(native, action):
                    if action == 'start':
                        store = native.coordinator._start_intents
                        mode = 'NORMAL_CHALLENGE' if challenge else 'MAINLINE'
                        active = store.read(replace(KEY, mode=mode))['active']
                        self.assertEqual(active['status'], 'RESERVED')
                        self.assertEqual(active['intent_id'], native.coordinator.snapshot['pending']['intent_id'])
                battle.before_submit = inspect
                first = self.run_battle(battle)
                self.assertEqual(battle.invocations, 1)
                self.assertEqual(first.snapshot['losses'], 0)
                later = AdmissionBattle(challenge=challenge, pid=999, outcomes=['WIN'])
                current = self.run_battle(later)
                self.assertEqual(later.invocations, 0)
                self.assertIn('battle_start_unresolved', current.snapshot['reason'])

    def test_success_closes_marker_and_allows_new_run_same_target(self):
        first = AdmissionBattle(challenge=True, outcomes=['WIN'])
        initial = self.run_battle(first)
        self.assertEqual(initial.snapshot['losses'], 0)
        self.assertEqual(len(initial.snapshot['consumed']), 1)
        self.assertIsNone(initial._start_intents.read(KEY)['active'])
        # A different report establishes a new real result, not a replay.
        later = AdmissionBattle(challenge=True, pid=999, outcomes=['WIN'])
        later.mutate_settlement = lambda attempt: attempt.update(report_id='102')
        current = self.run_battle(later)
        self.assertEqual(later.invocations, 1)
        self.assertEqual(len(current._start_intents.read(KEY)['closed']), 2)

    def test_reportless_ui_loss_is_unknown_uncounted_and_blocks_same_and_new_pid_runs(self):
        for challenge in (False, True):
            with self.subTest(challenge=challenge), tempfile.TemporaryDirectory() as directory:
                self.root = Path(directory)
                battle = AdmissionBattle(challenge=challenge, outcomes=['LOSS', 'WIN'])
                battle.mutate_settlement = lambda a: a.update(report_id='0', evidence=OBSERVED_LOSS_EVIDENCE)
                coordinator = self.run_battle(battle)
                key = replace(KEY, mode='NORMAL_CHALLENGE' if challenge else 'MAINLINE')
                self.assertEqual(battle.invocations, 1)
                self.assertEqual(coordinator.snapshot['losses'], 0)
                self.assertEqual(coordinator.snapshot['consumed'], [])
                self.assertEqual(coordinator.snapshot['reason'], 'loss_observed_settlement_unconfirmed')
                self.assertTrue(coordinator.snapshot['pending']['uncertain'])
                active = coordinator._start_intents.read(key)['active']
                self.assertEqual(active['status'], 'UNKNOWN')
                self.assertEqual(active['reason'], 'loss_observed_settlement_unconfirmed')
                self.assertEqual(active['intent_id'], coordinator.snapshot['pending']['intent_id'])
                self.assertEqual(coordinator._start_intents.read(key)['closed'], [])
                before = coordinator._start_intents.path_for(key).read_bytes()
                # The same projected loss never becomes a confirmed Result.
                for _ in range(2):
                    projected = coordinator._settled_result(coordinator.snapshot['run_id'],
                        coordinator.snapshot['context_key'], battle.snap['attempt'], stage_key='143:3')
                    self.assertFalse(projected.confirmed)
                # Reobserving the old result in another run also cannot count it.
                repeat = AdmissionBattle(challenge=challenge, outcomes=())
                repeat.snap = copy.deepcopy(battle.snap)
                repeated = self.run_battle(repeat)
                self.assertEqual(repeat.invocations, 0)
                self.assertEqual(repeated.snapshot['losses'], 0)
                self.assertEqual(repeated.snapshot['consumed'], [])
                for pid in (696, 999):
                    later = AdmissionBattle(challenge=challenge, pid=pid, outcomes=['WIN'])
                    current = self.run_battle(later)
                    self.assertNotEqual(current.snapshot['run_id'], coordinator.snapshot['run_id'])
                    self.assertEqual(later.invocations, 0)
                    self.assertEqual(current.snapshot['losses'], 0)
                    self.assertEqual(current.snapshot['consumed'], [])
                    self.assertIn('battle_start_unresolved', current.snapshot['reason'])
                    self.assertEqual(coordinator._start_intents.path_for(key).read_bytes(), before)

    def test_confirmed_loss_then_ui_loss_preserves_only_the_confirmed_count(self):
        for challenge in (False, True):
            with self.subTest(challenge=challenge), tempfile.TemporaryDirectory() as directory:
                self.root = Path(directory)
                battle = AdmissionBattle(challenge=challenge, outcomes=['LOSS', 'LOSS', 'WIN'])
                def mutate(attempt):
                    if battle.invocations == 2:
                        attempt.update(report_id='0', evidence=OBSERVED_LOSS_EVIDENCE)
                battle.mutate_settlement = mutate
                coordinator = self.run_battle(battle)
                self.assertEqual(battle.invocations, 2)
                self.assertEqual(coordinator.snapshot['losses'], 1)
                self.assertEqual(len(coordinator.snapshot['consumed']), 1)
                self.assertEqual(coordinator.snapshot['reason'], 'loss_observed_settlement_unconfirmed')
                key = replace(KEY, mode='NORMAL_CHALLENGE' if challenge else 'MAINLINE')
                record = coordinator._start_intents.read(key)
                self.assertEqual(len(record['closed']), 1)
                self.assertEqual(record['active']['status'], 'UNKNOWN')

    def test_confirmed_native_loss_and_win_still_allow_next_round_and_preserve_loss_policies(self):
        for challenge, expected_losses in ((False, 0), (True, 1)):
            with self.subTest(challenge=challenge), tempfile.TemporaryDirectory() as directory:
                self.root = Path(directory)
                battle = AdmissionBattle(challenge=challenge, outcomes=['LOSS', 'WIN'])
                coordinator = self.run_battle(battle)
                self.assertEqual(battle.invocations, 2)
                self.assertEqual(coordinator.snapshot['losses'], expected_losses)
                self.assertEqual(len(coordinator.snapshot['consumed']), 2)
                key = replace(KEY, mode='NORMAL_CHALLENGE' if challenge else 'MAINLINE')
                record = coordinator._start_intents.read(key)
                self.assertIsNone(record['active'])
                self.assertEqual(len(record['closed']), 2)
                self.assertTrue(all(c['status'] == 'COMPLETED' and int(c['report_id']) > 0
                    and c['evidence'] == 'captured_authoritative_receipt_applied' for c in record['closed']))

    def test_marker_write_failure_prevents_native_start(self):
        battle = AdmissionBattle()
        with patch.object(BattleStartStore, '_write', side_effect=OSError('marker_write_denied')):
            coordinator = self.run_battle(battle)
        self.assertEqual(battle.invocations, 0)
        self.assertIn('marker_write_denied', coordinator.snapshot['reason'])

    def test_best_effort_run_ledger_failure_still_has_strong_start_guard(self):
        battle = AdmissionBattle(challenge=True, outcomes=['UNKNOWN'])
        with patch.object(CampaignStore, 'initialize', side_effect=OSError('ledger_denied')):
            coordinator = self.run_battle(battle)
        self.assertEqual(battle.invocations, 1)
        self.assertEqual(coordinator._start_intents.read(KEY)['active']['status'], 'UNKNOWN')
        later = AdmissionBattle(challenge=True, pid=999, outcomes=['WIN'])
        self.run_battle(later)
        self.assertEqual(later.invocations, 0)

    def test_later_run_ledger_save_failure_does_not_remove_start_protection(self):
        battle = AdmissionBattle(challenge=True, outcomes=['UNKNOWN'])
        with patch.object(CampaignStore, 'save', side_effect=OSError('ledger_update_denied')):
            coordinator = self.run_battle(battle)
        self.assertEqual(battle.invocations, 1)
        self.assertEqual(coordinator._start_intents.read(KEY)['active']['status'], 'UNKNOWN')
        later = AdmissionBattle(challenge=True, pid=999, outcomes=['WIN'])
        self.run_battle(later)
        self.assertEqual(later.invocations, 0)

    def test_submitted_or_unknown_marker_update_failure_still_blocks_new_process(self):
        for status in ('SUBMITTED', 'UNKNOWN'):
            with self.subTest(status=status), tempfile.TemporaryDirectory() as directory:
                self.root = Path(directory)
                battle = AdmissionBattle(challenge=True, outcomes=['UNKNOWN'])
                def configure(coordinator):
                    original = coordinator._start_intents._write
                    def fail_update(key, data):
                        if data['active'] is not None and data['active']['status'] == status:
                            raise OSError('marker_update_denied')
                        return original(key, data)
                    coordinator._start_intents._write = fail_update
                coordinator = self.run_battle(battle, configure)
                self.assertEqual(battle.invocations, 1)
                self.assertIsNotNone(coordinator._start_intents.read(KEY)['active'])
                later = AdmissionBattle(challenge=True, pid=999, outcomes=['WIN'])
                self.run_battle(later)
                self.assertEqual(later.invocations, 0)

    def test_process_crash_after_native_invocation_before_ack_keeps_reserved_marker(self):
        script = '''import os,sys
from pathlib import Path
from types import SimpleNamespace
from tests.test_battle_start_store import AdmissionBattle, Runner
from jiangshen_assistant.challenge_task import ChallengeCoordinator
directory=Path(sys.argv[1])
battle=AdmissionBattle(challenge=True)
runner=Runner()
coordinator=ChallengeCoordinator(battle,runner,directory,clock=lambda:battle.now)
battle.coordinator=coordinator
coordinator._wait=lambda timeout=None:None
original=battle.submit_campaign
def crash_after_call(*args,**kwargs):
    reply=original(*args,**kwargs)
    (directory/'native-invocations.txt').write_text(str(battle.invocations))
    os._exit(42)
battle.submit_campaign=crash_after_call
coordinator.start()
runner.task(SimpleNamespace(cancelled=False,is_paused=False))
'''
        exited = subprocess.run([sys.executable, '-c', script, str(self.root)],
                                capture_output=True, text=True, timeout=10)
        self.assertEqual(exited.returncode, 42, exited.stderr)
        self.assertEqual((self.root / 'native-invocations.txt').read_text(), '1')
        store = BattleStartStore(self.root / 'battle_start_intents')
        self.assertEqual(store.read(KEY)['active']['status'], 'RESERVED')
        later = AdmissionBattle(challenge=True, pid=999, outcomes=['WIN'])
        self.run_battle(later)
        self.assertEqual(later.invocations, 0)

    def test_wrong_ack_or_ambiguous_invocation_never_release_start(self):
        for wrong_id in (False, True):
            with self.subTest(wrong_id=wrong_id), tempfile.TemporaryDirectory() as directory:
                self.root = Path(directory)
                battle = AdmissionBattle(challenge=True)
                original = battle.receipt
                def receipt(*args, **kwargs):
                    wire = original(*args, **kwargs)
                    if wrong_id:
                        wire['ack']['result']['challenge_command']['attempt_id'] = 'f' * 32
                    else:
                        wire['ack']['status'] = 'RUNNING'
                    return wire
                battle.receipt = receipt
                coordinator = self.run_battle(battle)
                self.assertEqual(battle.invocations, 1)
                self.assertEqual(coordinator.snapshot['losses'], 0)
                self.assertIsNotNone(coordinator._start_intents.read(KEY)['active'])
                later = AdmissionBattle(challenge=True, pid=999, outcomes=['WIN'])
                self.run_battle(later)
                self.assertEqual(later.invocations, 0)

    def test_legal_noninvocation_can_release_in_both_coordinators(self):
        for challenge in (False, True):
            for behavior in ('reject', 'cancel', 'admission_closed'):
                with self.subTest(challenge=challenge, behavior=behavior):
                    battle = AdmissionBattle(challenge=challenge, behavior=behavior)
                    coordinator = self.run_battle(battle)
                    key = replace(KEY, mode='NORMAL_CHALLENGE' if challenge else 'MAINLINE')
                    self.assertEqual(battle.invocations, 0)
                    self.assertIsNone(coordinator._start_intents.read(key)['active'])
                    self.assertEqual(coordinator.snapshot['losses'], 0)

    def test_exception_timeout_and_identity_mismatch_all_retain_block(self):
        cases = (('exception', (), None), ('normal', (), None),
                 ('normal', ('WIN',), lambda a: a.update(character_id='999')),
                 ('normal', ('WIN',), lambda a: a.update(command_seq=999)),
                 ('normal', ('WIN',), lambda a: a.update(evidence='unchanged_balance')))
        for index, (behavior, outcomes, mutation) in enumerate(cases):
            with self.subTest(index=index), tempfile.TemporaryDirectory() as directory:
                self.root = Path(directory)
                battle = AdmissionBattle(challenge=True, behavior=behavior, outcomes=outcomes)
                battle.mutate_settlement = mutation
                coordinator = self.run_battle(battle)
                self.assertEqual(battle.invocations, 1)
                self.assertEqual(coordinator.snapshot['losses'], 0)
                self.assertIsNotNone(coordinator._start_intents.read(KEY)['active'])
                later = AdmissionBattle(challenge=True, pid=999, outcomes=['WIN'])
                self.run_battle(later)
                self.assertEqual(later.invocations, 0)

    def test_terminal_marker_write_failure_preserves_confirmed_counts_and_blocks_restart(self):
        battle = AdmissionBattle(challenge=True, outcomes=['LOSS'])
        def configure(coordinator):
            original = coordinator._start_intents._write
            def fail_terminal(key, data):
                if data['active'] is None and data['closed'] and data['closed'][-1]['status'] == 'COMPLETED':
                    raise OSError('terminal_write_denied')
                return original(key, data)
            coordinator._start_intents._write = fail_terminal
        coordinator = self.run_battle(battle, configure)
        self.assertEqual(coordinator.snapshot['losses'], 1)
        self.assertEqual(len(coordinator.snapshot['consumed']), 1)
        self.assertIsNotNone(coordinator._start_intents.read(KEY)['active'])
        later = AdmissionBattle(challenge=True, pid=999, outcomes=['WIN'])
        self.run_battle(later)
        self.assertEqual(later.invocations, 0)

    def test_existing_loss_policies_stop_at_their_own_third_loss(self):
        for challenge, expected in ((False, 7), (True, 5)):
            with self.subTest(challenge=challenge):
                battle = AdmissionBattle(challenge=challenge,
                    outcomes=['LOSS', 'WIN', 'LOSS', 'WIN', 'LOSS', 'LOSS', 'LOSS'])
                coordinator = self.run_battle(battle)
                self.assertEqual(battle.invocations, expected)
                self.assertEqual(coordinator.snapshot['losses'], 3)
                self.assertEqual(coordinator.snapshot['mode'], 'loss_limit')

    def test_old_run_archives_are_not_implicitly_imported_and_other_role_is_independent(self):
        archive = self.root / 'challenge_runs' / ('9' * 32 + '.json')
        archive.parent.mkdir()
        archive.write_text('{"mode":"blocked","pending":{"uncertain":true}}', encoding='utf-8')
        first = AdmissionBattle(challenge=True, outcomes=['UNKNOWN'])
        self.run_battle(first)
        self.assertEqual(first.invocations, 1)
        other = AdmissionBattle(challenge=True, pid=999, role='55290000000276', outcomes=['WIN'])
        coordinator = self.run_battle(other)
        self.assertEqual(other.invocations, 1)
        self.assertEqual(len(coordinator.snapshot['consumed']), 1)
        self.assertTrue(archive.exists())

    def test_read_before_real_coordinator_start_is_a_valid_empty_target(self):
        store = BattleStartStore(self.root / 'battle_start_intents')
        self.assertIsNone(store.read(KEY)['active'])
        battle = AdmissionBattle(challenge=True, outcomes=['WIN'])
        coordinator = self.run_battle(battle)
        self.assertEqual(battle.invocations, 1)
        self.assertEqual(len(coordinator.snapshot['consumed']), 1)

    def test_json_or_anchor_loss_never_reopens_real_coordinators(self):
        for challenge in (False, True):
            for missing in ('json', 'anchor'):
                with self.subTest(challenge=challenge, missing=missing), tempfile.TemporaryDirectory() as directory:
                    self.root = Path(directory)
                    first = self.run_battle(AdmissionBattle(challenge=challenge, outcomes=['UNKNOWN']))
                    key = replace(KEY, mode='NORMAL_CHALLENGE' if challenge else 'MAINLINE')
                    store = first._start_intents
                    path = store.path_for(key) if missing == 'json' else store.anchor_path_for(key)
                    path.unlink()
                    later = AdmissionBattle(challenge=challenge, pid=999, outcomes=['WIN'])
                    coordinator = self.run_battle(later)
                    self.assertEqual(later.invocations, 0)
                    self.assertEqual(coordinator.snapshot['losses'], 0)
                    self.assertEqual(coordinator.snapshot['consumed'], [])
                    self.assertIn('battle_start_' + ('record_missing' if missing == 'json' else 'anchor_missing'),
                                  coordinator.snapshot['reason'])
                    self.assertFalse(path.exists())

    def test_old_empty_record_replay_is_blocked_by_both_real_coordinators(self):
        for challenge in (False, True):
            with self.subTest(challenge=challenge), tempfile.TemporaryDirectory() as directory:
                self.root = Path(directory)
                mode = 'NORMAL_CHALLENGE' if challenge else 'MAINLINE'
                key = replace(KEY, mode=mode)
                store = BattleStartStore(self.root / 'battle_start_intents')
                store.read(key)
                empty = store.path_for(key).read_bytes()
                battle = AdmissionBattle(challenge=challenge, outcomes=['UNKNOWN'])
                self.run_battle(battle)
                self.assertEqual(battle.invocations, 1)
                anchor_before = store.anchor_path_for(key).read_bytes()
                store.path_for(key).write_bytes(empty)
                later = AdmissionBattle(challenge=challenge, pid=999, outcomes=['WIN'])
                current = self.run_battle(later)
                self.assertEqual(later.invocations, 0)
                self.assertEqual(current.snapshot['consumed'], [])
                self.assertIn('record_anchor_mismatch', current.snapshot['reason'])
                self.assertEqual(store.anchor_path_for(key).read_bytes(), anchor_before)

    def test_reserve_and_confirm_failures_at_every_publication_boundary_do_not_duplicate_io(self):
        stages = ('anchor_before', 'anchor_after', 'record_before', 'record_after',
                  'readback_before', 'readback_after')
        for operation in ('reserve', 'confirm'):
            for stage in stages:
                with self.subTest(operation=operation, stage=stage), tempfile.TemporaryDirectory() as directory:
                    self.root = Path(directory)
                    def configure(coordinator):
                        store = coordinator._start_intents
                        original = store._write
                        inside = [False]
                        def mutation(key, data):
                            active = data['active']
                            inside[0] = ((operation == 'reserve' and active is not None and active['status'] == 'RESERVED')
                                or (operation == 'confirm' and active is None and bool(data['closed'])
                                    and data['closed'][-1]['status'] == 'COMPLETED'))
                            try:
                                return original(key, data)
                            finally:
                                inside[0] = False
                        store._write = mutation
                        inject_publication_failure(store, stage, enabled=lambda: inside[0])
                    first = AdmissionBattle(challenge=True, outcomes=['LOSS'] if operation == 'confirm' else [])
                    coordinator = self.run_battle(first, configure)
                    self.assertEqual(first.invocations, 1 if operation == 'confirm' else 0)
                    self.assertTrue(any('publication_' + stage in event.message for event in coordinator.events.queue))
                    later = AdmissionBattle(challenge=True, pid=999, outcomes=['WIN'])
                    later.mutate_settlement = lambda attempt: attempt.update(report_id='999')
                    current = self.run_battle(later)
                    # No start occurred before a failed reserve. A verified old
                    # empty pair may allow its first start. A fully published,
                    # genuinely confirmed terminal pair may allow the next.
                    permitted = ((operation == 'reserve' and stage == 'anchor_before') or
                                 (operation == 'confirm' and stage in ('record_after', 'readback_before', 'readback_after')))
                    self.assertEqual(later.invocations, int(permitted))
                    if not permitted:
                        self.assertEqual(current.snapshot['consumed'], [])

    def test_reserve_and_confirm_process_crash_after_each_boundary_is_safe(self):
        script = '''import os,sys
from pathlib import Path
from types import SimpleNamespace
from tests.test_battle_start_store import AdmissionBattle,Runner
from jiangshen_assistant.challenge_task import ChallengeCoordinator
root=Path(sys.argv[1]); operation=sys.argv[2]; stage=sys.argv[3]
battle=AdmissionBattle(challenge=True,outcomes=['LOSS'] if operation=='confirm' else [])
runner=Runner(); coordinator=ChallengeCoordinator(battle,runner,root,clock=lambda:battle.now,battle_timeout=.5)
battle.coordinator=coordinator; coordinator._wait=lambda timeout=None:None
original_submit=battle.submit_campaign
def submit(*args,**kwargs):
    result=original_submit(*args,**kwargs)
    (root/'native-invocations.txt').write_text(str(battle.invocations))
    return result
battle.submit_campaign=submit
store=coordinator._start_intents; original_mutation=store._write; inside=[False]
def mutation(key,data):
    active=data['active']
    inside[0]=((operation=='reserve' and active is not None and active['status']=='RESERVED') or
               (operation=='confirm' and active is None and bool(data['closed']) and data['closed'][-1]['status']=='COMPLETED'))
    try: return original_mutation(key,data)
    finally: inside[0]=False
store._write=mutation
method={'anchor':'_write_anchor','record':'_write_record','readback':'_readback_pair'}[stage]
original=getattr(store,method)
def crash(*args,**kwargs):
    result=original(*args,**kwargs)
    if inside[0]: os._exit(36)
    return result
setattr(store,method,crash)
coordinator.start(); runner.task(SimpleNamespace(cancelled=False,is_paused=False))
'''
        for operation in ('reserve', 'confirm'):
            for stage in ('anchor', 'record', 'readback'):
                with self.subTest(operation=operation, stage=stage), tempfile.TemporaryDirectory() as directory:
                    self.root = Path(directory)
                    exited = subprocess.run([sys.executable, '-c', script, str(self.root), operation, stage],
                                            capture_output=True, text=True, timeout=10)
                    self.assertEqual(exited.returncode, 36, exited.stderr)
                    calls = self.root / 'native-invocations.txt'
                    self.assertEqual(int(calls.read_text()) if calls.exists() else 0, int(operation == 'confirm'))
                    later = AdmissionBattle(challenge=True, pid=999, outcomes=['WIN'])
                    later.mutate_settlement = lambda attempt: attempt.update(report_id='999')
                    self.run_battle(later)
                    self.assertEqual(later.invocations, int(operation == 'confirm' and stage != 'anchor'))

    def test_legacy_migration_write_failure_at_each_step_never_loses_unknown(self):
        stages = ('anchor_before', 'anchor_after', 'record_before', 'record_after',
                  'readback_before', 'readback_after')
        for stage in stages:
            with self.subTest(stage=stage), tempfile.TemporaryDirectory() as directory:
                self.root = Path(directory)
                store = write_legacy(self.root, with_lock=True)
                def configure(coordinator):
                    inject_publication_failure(coordinator._start_intents, stage, prefix='migration_')
                first = AdmissionBattle(challenge=True, pid=999, outcomes=['WIN'])
                failed = self.run_battle(first, configure)
                self.assertEqual(first.invocations, 0)
                self.assertIn('migration_' + stage, failed.snapshot['reason'])
                retained = json.loads(store.path_for(KEY).read_text())
                self.assertEqual(retained['active'], legacy_unknown()['active'])
                self.assertEqual(retained['closed'], [])
                later = AdmissionBattle(challenge=True, pid=1000, outcomes=['WIN'])
                self.run_battle(later)
                self.assertEqual(later.invocations, 0)

    def test_legacy_migration_process_crash_after_each_publication_preserves_unknown(self):
        script = '''import os,sys
from pathlib import Path
from tests.test_battle_start_store import KEY
from jiangshen_assistant.battle_start_store import BattleStartStore
store=BattleStartStore(Path(sys.argv[1])/'battle_start_intents')
stage=sys.argv[2]
original_anchor=store._write_anchor
original_record=store._write_record
original_readback=store._readback_pair
def anchor_write(key,data):
    original_anchor(key,data)
    if stage=='anchor':
        os._exit(33)
def record_write(key,data):
    original_record(key,data)
    if stage=='record':
        os._exit(33)
def readback(key,data):
    result=original_readback(key,data)
    if stage=='readback':
        os._exit(33)
    return result
store._write_anchor=anchor_write
store._write_record=record_write
store._readback_pair=readback
store.read(KEY)
'''
        for stage in ('anchor', 'record', 'readback'):
            with self.subTest(stage=stage), tempfile.TemporaryDirectory() as directory:
                self.root = Path(directory)
                store = write_legacy(self.root, with_lock=True)
                exited = subprocess.run([sys.executable, '-c', script, str(self.root), stage],
                                        capture_output=True, text=True, timeout=10)
                self.assertEqual(exited.returncode, 33, exited.stderr)
                self.assertEqual(json.loads(store.path_for(KEY).read_text())['active'], legacy_unknown()['active'])
                later = AdmissionBattle(challenge=True, pid=999, outcomes=['WIN'])
                self.run_battle(later)
                self.assertEqual(later.invocations, 0)

    def test_initialization_crash_after_anchor_cannot_become_an_empty_target(self):
        script = '''import os,sys
from pathlib import Path
from tests.test_battle_start_store import KEY
from jiangshen_assistant.battle_start_store import BattleStartStore
store=BattleStartStore(Path(sys.argv[1])/'battle_start_intents')
original=store._write_anchor
def crash(key,data):
    original(key,data)
    os._exit(34)
store._write_anchor=crash
store.read(KEY)
'''
        exited = subprocess.run([sys.executable, '-c', script, str(self.root)],
                                capture_output=True, text=True, timeout=10)
        self.assertEqual(exited.returncode, 34, exited.stderr)
        later = AdmissionBattle(challenge=True, pid=999, outcomes=['WIN'])
        coordinator = self.run_battle(later)
        self.assertEqual(later.invocations, 0)
        self.assertIn('battle_start_record_missing', coordinator.snapshot['reason'])

    def test_unanchored_legacy_terminal_is_not_proof_that_new_admission_is_safe(self):
        data = legacy_unknown()
        data['closed'] = [dict(data['active'], status='COMPLETED', outcome='LOSS', report_id='777',
                               evidence='captured_authoritative_receipt_applied', reason='')]
        data['active'] = None
        store = write_legacy(self.root, with_lock=True, data=data)
        battle = AdmissionBattle(challenge=True, pid=999, outcomes=['WIN'])
        coordinator = self.run_battle(battle)
        self.assertEqual(battle.invocations, 0)
        self.assertEqual(coordinator.snapshot['consumed'], [])
        self.assertIn('legacy_inactive_requires_review', coordinator.snapshot['reason'])
        self.assertFalse(store.anchor_path_for(KEY).exists())


if __name__ == '__main__':
    unittest.main()
