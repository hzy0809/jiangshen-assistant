"""Incident-scoped refresh transport and canonical admission, without a game."""
import copy
import hashlib
import json
import os
from pathlib import Path
import tempfile
import threading
import unittest
from unittest.mock import Mock

from jiangshen_assistant import bridge_client as bridge
from jiangshen_assistant.campaign_recovery import canonical_bytes, load_json_bytes
from jiangshen_assistant.development_recovery import (
    ORIGINAL_OPERATION, ORIGINAL_SESSION, PID, CREATION, build_equipment_progress_resolution,
)
from jiangshen_assistant.mono_runtime import ProcessIdentity
from jiangshen_assistant.objects import CampaignAdmissionClosed
from tests import test_bridge_client as bf
from tests import test_objects as of
from tests.test_development_recovery import inputs


def idle_refresh():
    value = json.loads(inputs()[2])['player_refresh']
    value.update(status='IDLE', reason='', operation_id='', sequence=0, expected_revision=0,
        character_id='', mutation_attempted=False, receipt_capture=None, receipt_hash='',
        callback_returned=False, server_snapshot=None, context_rebuilt=False,
        root_level='', main_ready=False, normal_responses_idle=False)
    return value


class RefreshTransportTests(unittest.TestCase):
    setUp = bf.BridgeTests.setUp
    client = bf.BridgeTests.client
    common = bf.BridgeTests.common
    state = bf.BridgeTests.state
    ack = bf.BridgeTests.ack

    def ready(self):
        client = self.client()
        client.connect(timeout=.2)
        client._check_refresh_admission = Mock(return_value=[])
        original = client._send
        def send(name, **kwargs):
            seq = original(name, **kwargs)
            dto = idle_refresh()
            dto.update(status='WAITING', operation_id='a'*32, sequence=seq,
                expected_revision=9, character_id='55290000000275', mutation_attempted=True)
            state = self.state(client, seq)
            state['player_refresh'] = dto
            ack = self.ack(client, seq, name, state, 'COMPLETED')
            ack['result']['player_refresh'] = copy.deepcopy(dto)
            if getattr(self, 'change', None):
                self.change(state, ack)
            bridge._atomic_json(client.session_dir/'state.json', state)
            bridge._atomic_json(client.session_dir/'ack.json', ack)
            return seq
        client._send = Mock(side_effect=send)
        return client

    def test_fixed_rpc_zero_fields_and_exact_ack(self):
        client = self.ready()
        wire = client.player_state_refresh(expected_revision=9, operation_id='a'*32, timeout=.2)
        lines = (client.session_dir/'command.txt').read_text().splitlines()
        self.assertEqual(len(lines), 14)
        self.assertIn('command=player-state-refresh', lines)
        self.assertIn('max_requests=0', lines)
        self.assertIn('target_id=0', lines)
        self.assertEqual(wire['ack']['result']['player_refresh'], wire['state']['player_refresh'])
        client._check_refresh_admission.assert_called()
        self.assertEqual(client._send.call_count, 1)

    def test_sidecar_disagreement_is_unknown_without_retry(self):
        self.change = lambda state, ack: ack['result']['player_refresh'].update(sequence=8)
        client = self.ready()
        with self.assertRaises(bridge.BridgeUnknown):
            client.player_state_refresh(expected_revision=9, operation_id='a'*32, timeout=.2)
        self.assertTrue(client.is_unknown)
        self.assertEqual(client._send.call_count, 1)

    def test_generic_or_budget_refresh_cannot_publish(self):
        client = self.ready()
        with self.assertRaises(bridge.BridgeError):
            client.request('player-state-refresh')
        fields = dict(action='player_state_refresh', expected_revision=9, operation_id='a'*32,
            target_id=0, desired_level=0, max_requests=1, max_money=0, max_stone=0, max_food=0, mode='')
        with self.assertRaises(bridge.BridgeError):
            client._send('player-state-refresh', development_fields=fields)
        self.assertEqual(client._seq, 0)
        self.assertFalse((client.session_dir/'command.txt').exists())

    def test_positive_raw_hash_projection_and_boundaries(self):
        dto = json.loads(inputs()[2])['player_refresh']
        self.assertTrue(bridge._player_refresh_valid(dto))
        for change in (
            lambda x: x.update(receipt_hash='0'*64),
            lambda x: x['receipt_capture'].update(bytes=1048577),
            lambda x: x['receipt_capture'].update(data='!'),
            lambda x: x['server_snapshot'].update(money=1),
            lambda x: x.update(callback_returned=False),
        ):
            value = copy.deepcopy(dto)
            change(value)
            self.assertFalse(bridge._player_refresh_valid(value))


class RefreshAdmissionTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.project = Path(self.temp.name)
        self.args = inputs()
        self.proof = build_equipment_progress_resolution(*self.args)
        self.state = json.loads(self.args[2])
        self.client = bridge.BridgeClient(self.project, ProcessIdentity(PID, CREATION, 10, 20), lambda: None)
        self.client.nonce = self.state['session_id']
        self.client._last_state = copy.deepcopy(self.state)
        self.client._prior_business = copy.deepcopy(self.args[3][0])
        self.put('development_runs/'+ORIGINAL_OPERATION+'.json', self.args[0])
        self.put('object_sessions/'+ORIGINAL_SESSION+'/state.json', self.args[1])
        self.capsule = self.put('object_sessions/'+self.state['session_id']+'/player-refresh-observed.json', self.args[2])

    def put(self, name, raw):
        path = self.project/'data'/name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(raw)
        return path

    def publish(self):
        self.put('development_resolutions/'+ORIGINAL_OPERATION+'.json', canonical_bytes(self.proof))

    def test_only_exact_old_fact_may_refresh_but_cannot_enchant(self):
        self.client._last_state['player_refresh'] = idle_refresh()
        self.assertEqual(self.client._check_refresh_admission(), self.args[3])
        with self.assertRaises(bridge.BridgeError):
            self.client._check_campaign_admission()
        self.assertEqual(self.client._seq, 0)
        before = self.args[0]
        self.assertEqual((self.project/'data/development_runs'/ (ORIGINAL_OPERATION+'.json')).read_bytes(), before)

    def test_foreign_or_uncertain_refresh_and_changed_history_block(self):
        self.client._last_state['player_refresh'] = idle_refresh()
        original = copy.deepcopy(self.client._prior_business)
        for change in (
            lambda x: x.update(session_id='e'*32),
            lambda x: x['business'].update(pending_player_refresh=True),
            lambda x: x['business'].update(pending_attempt=True),
            lambda x: x.update(prior_unresolved_business={'session_id':'e'*32,
                'business':{'pending_development':True,'pending_player_refresh':True}}),
        ):
            self.client._prior_business = copy.deepcopy(original)
            change(self.client._prior_business)
            with self.assertRaises(bridge.BridgeError):
                self.client._check_refresh_admission()
        self.client._prior_business = original
        self.put('development_runs/'+ORIGINAL_OPERATION+'.json', self.args[0]+b' ')
        with self.assertRaises(bridge.BridgeError):
            self.client._check_refresh_admission()

    def test_disposition_requires_same_live_epoch_and_canonical_bytes(self):
        self.publish()
        self.client._check_campaign_admission()
        self.client._last_state['player_refresh']['receipt_hash'] = '0'*64
        with self.assertRaises(bridge.BridgeError):
            self.client._check_campaign_admission()
        self.client._last_state = copy.deepcopy(self.state)
        self.client.nonce = 'e'*32
        with self.assertRaises(bridge.BridgeError):
            self.client._check_campaign_admission()
        self.client.nonce = self.state['session_id']
        self.capsule.write_bytes(self.args[2]+b' ')
        with self.assertRaises(bridge.BridgeError):
            self.client._check_campaign_admission()

    def test_every_unrelated_guard_stays_blocked(self):
        self.publish()
        self.client._prior_business['prior_unresolved_business'] = {
            'session_id':'e'*32, 'business':{'pending_development':True,'pending_attempt':False}}
        with self.assertRaises(bridge.BridgeError):
            self.client._check_campaign_admission()

    def test_one_persisted_continuation_cannot_reset_budget_or_identity(self):
        self.publish()
        operation = 'b'*32
        fields = dict(action='equipment_enchant', expected_revision=10, operation_id=operation,
            target_id=1, desired_level=5, max_requests=4, max_money=99400, max_stone=1000, max_food=0, mode='enchant')
        journal = dict(module='equipment', operation_id=operation,
            context_key=json.dumps({'nonce':self.client.nonce}),
            recoveredFromProofHash=hashlib.sha256(canonical_bytes(self.proof)).hexdigest(),
            unresolved=fields)
        self.put('development_runs/'+operation+'.json', canonical_bytes(journal))
        self.client._check_recovery_mutation(fields)
        for change in ({'operation_id':'c'*32}, {'max_money':100000}, {'action':'spirit_train'}):
            bad = dict(fields, **change)
            with self.assertRaises(bridge.BridgeError):
                self.client._check_recovery_mutation(bad)
        with self.assertRaises(bridge.BridgeError):
            self.client._check_recovery_mutation(None)
        # A settled partial journal remains a consumed continuation claim.
        journal['unresolved'] = None
        journal['events'] = [{'kind':'result','result':{'status':'BLOCKED'}}]
        self.put('development_runs/'+operation+'.json', canonical_bytes(journal))
        second = copy.deepcopy(journal)
        second.update(operation_id='c'*32, unresolved=dict(fields, operation_id='c'*32))
        self.put('development_runs/'+('c'*32)+'.json', canonical_bytes(second))
        with self.assertRaises(bridge.BridgeError):
            self.client._check_recovery_mutation(second['unresolved'])

    def test_data_root_and_descendant_aliases_are_rejected(self):
        outside = self.project/'outside'
        outside.mkdir()
        link = self.project/'data'/'alias'
        try:
            os.symlink(outside, link, target_is_directory=True)
        except OSError as exc:
            self.skipTest('symlink creation unavailable: '+str(exc))
        (outside/'source.json').write_text('{}')
        with self.assertRaises(bridge.BridgeError):
            self.client._canonical_data_source('alias', 'source.json')
        other_project = self.project/'other-project'
        other_project.mkdir()
        os.symlink(outside, other_project/'data', target_is_directory=True)
        self.client.project_root = other_project
        with self.assertRaises(bridge.BridgeError):
            self.client._canonical_data_source('source.json')


class RefreshObjectTests(unittest.TestCase):
    setUp = of.ControllerTests.setUp
    cleanup_controller = of.ControllerTests.cleanup_controller
    ready = of.ControllerTests.ready

    def owner(self):
        client = self.ready()
        client.last_state['campaign'] = {'character_id':'12345'}
        client.last_state['player_refresh'] = idle_refresh()
        client.last_state['development'] = {'character_id':'12345'}
        return client, self.controller.acquire_campaign('a'*32)

    def test_exclusive_owner_and_pause_close_refresh_admission(self):
        client, token = self.owner()
        client.player_state_refresh = Mock(return_value={'state':client.last_state})
        self.controller.pause_campaign(token)
        with self.assertRaises(CampaignAdmissionClosed):
            self.controller.submit_player_refresh(token, expected_revision=1, operation_id='a'*32)
        client.player_state_refresh.assert_not_called()
        self.controller.resume_campaign(token)
        self.controller.submit_player_refresh(token, expected_revision=1, operation_id='a'*32)
        client.player_state_refresh.assert_called_once()

    def test_normal_reload_skips_only_passive_graph_reader(self):
        client, token = self.owner()
        self.controller._player_refresh_active = True
        self.reader.error = RuntimeError('object graph transient')
        before = self.identity_reader.call_count
        client.development_observe = Mock(return_value={'state':client.last_state})
        self.controller.refresh_state()
        of.eventually(lambda: self.identity_reader.call_count > before)
        self.assertFalse(client.marked_unknown)
        self.assertEqual(client.stop_calls, 0)
        self.assertEqual(self.controller.bridge_state, 'ready')
        client.last_state['player_refresh']['status'] = 'OBSERVED'
        self.controller._runtime_state(client, client.last_state)
        self.assertFalse(self.controller._player_refresh_active)

    def test_refresh_waits_for_an_already_running_graph_read(self):
        client, token = self.owner()
        entered, release = threading.Event(), threading.Event()
        original = self.reader.read
        def read(*args, **kwargs):
            entered.set()
            if not release.wait(1):
                raise RuntimeError('test graph reader was not released')
            return original(*args, **kwargs)
        self.reader.read = read
        client.player_state_refresh = Mock(return_value={'state':client.last_state})
        self.controller.refresh_state()
        self.assertTrue(entered.wait(1))
        errors = []
        def submit():
            try:
                self.controller.submit_player_refresh(token, expected_revision=1, operation_id='a'*32)
            except Exception as exc:
                errors.append(exc)
        thread = threading.Thread(target=submit)
        thread.start()
        try:
            self.assertFalse(self.controller._player_refresh_active)
            client.player_state_refresh.assert_not_called()
        finally:
            release.set()
        thread.join(1)
        self.assertFalse(thread.is_alive())
        self.assertEqual(errors, [])
        client.player_state_refresh.assert_called_once()


if __name__ == '__main__':
    unittest.main()
