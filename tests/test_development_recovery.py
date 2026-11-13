import base64
import contextlib
import copy
import hashlib
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from jiangshen_assistant.campaign_recovery import canonical_bytes, load_json_bytes
from jiangshen_assistant.development_recovery import (
    CREATION, PID, ORIGINAL_OPERATION, ORIGINAL_SESSION, HANDLER_PROOF, ResolutionError,
    build_equipment_progress_resolution, business_fact_hash, decode_sc16_projection,
    resolved_development_business, verify_equipment_progress_resolution,
)
from tools.resolve_development_progress import main, matching_facts

FIXTURES = Path(__file__).resolve().parents[1] / 'data/acceptance/20261031-campaign/31-development-receipts/resolution-model'
SESSION = 'f' * 32


def inputs():
    journal = (FIXTURES / 'original-journal.json').read_bytes()
    original_bytes = (FIXTURES / 'original-state.json').read_bytes()
    original = load_json_bytes(original_bytes)
    guard = load_json_bytes((FIXTURES / 'original-guard.json').read_bytes())
    facts = matching_facts(guard)
    refresh = copy.deepcopy(original)
    refresh.update(session_id=SESSION, nonce=SESSION, lifecycle='ready', ready=True,
        component_destroyed=False, main_thread_verified=True, gate='PASS', updated_utc='2026-10-31T05:00:00Z', frame=1079000, last_command_seq=7)
    refresh['player_refresh'] = load_json_bytes((FIXTURES / 'frozen-native-dto.json').read_bytes())
    development = refresh['development']
    idle = copy.deepcopy(development['spirit']['operation'])
    development['equipment'].update(operation=idle, gate='PASS', reason='')
    refresh['business'].update(pending_attempt=False, pending_development=False, pending_player_refresh=False,
        player_refresh={'status':'OBSERVED','operation_id':refresh['player_refresh']['operation_id'],
                        'sequence':refresh['player_refresh']['sequence']}, resource_rollback=False)
    refresh['business']['development'] = copy.deepcopy(development)
    refresh['campaign'].update(gate='PASS',available=True)
    return journal, original_bytes, canonical_bytes(refresh), facts, copy.deepcopy(HANDLER_PROOF)


def vi(value):
    result = bytearray()
    while value > 127:
        result.append((value & 127) | 128)
        value >>= 7
    result.append(value)
    return bytes(result)


def scalar(field, value):
    return vi(field << 3) + vi(value)


def message(field, value):
    return vi((field << 3) | 2) + vi(len(value)) + value


def packet(*, eq=None, player=None, form=True):
    player = scalar(1, 55290000000275) if player is None else player
    eq = b''.join(scalar(f, v) for f, v in [(1, 1), (2, 1), (3, 0), (4, 0), (6, 1), (7, 22)]) if eq is None else eq
    return message(3, player) + (scalar(8, 1) if form else b'') + message(13, eq)


class RecoveryTests(unittest.TestCase):
    def proof(self):
        return build_equipment_progress_resolution(*inputs())

    def test_frozen_native_projection_independently_decoded(self):
        dto = load_json_bytes((FIXTURES / 'frozen-native-dto.json').read_bytes())
        raw = base64.b64decode(dto['receipt_capture']['data'])
        self.assertEqual(hashlib.sha256(raw).hexdigest(), dto['receipt_hash'])
        self.assertEqual(decode_sc16_projection(raw), dto['server_snapshot'])
        self.assertNotIn('result', decode_sc16_projection(raw))

    def test_separate_disposition_preserves_sources_and_budget(self):
        args = inputs()
        saved = copy.deepcopy(args)
        proof = build_equipment_progress_resolution(*args)
        verify_equipment_progress_resolution(proof)
        self.assertEqual(args, saved)
        self.assertEqual(proof['original_outcome'], 'UNKNOWN')
        self.assertFalse(proof['consumes_original_result'])
        self.assertFalse(proof['retries_original'])
        self.assertFalse(proof['resets_budget'])
        self.assertEqual(proof['remaining_budget'], {'max_requests': 4, 'max_money': 99400, 'max_stone': 1000})
        self.assertTrue(resolved_development_business(args[3][0], proof, pid=PID,
            process_creation_filetime=CREATION, journal_bytes=args[0], original_state_bytes=args[1], refresh_state_bytes=args[2]))

    def test_resolution_field_tampering_rejected(self):
        for key, value in [('resets_budget', True), ('original_outcome', 'OBSERVED'), ('retries_original', True),
            ('remaining_budget', {'max_requests': 5, 'max_money': 100000, 'max_stone': 1000}),
            ('source_anchors', {}), ('consumes_original_result', True), ('extra_original_writes', [])]:
            with self.subTest(key=key):
                proof = self.proof()
                proof[key] = value
                with self.assertRaises(ResolutionError):
                    verify_equipment_progress_resolution(proof)

    def test_current_source_bytes_are_required_and_exact(self):
        args, proof = inputs(), self.proof()
        for slot in range(3):
            altered = list(args[:3])
            altered[slot] += b' '
            self.assertFalse(resolved_development_business(args[3][0], proof, pid=PID,
                process_creation_filetime=CREATION, journal_bytes=altered[0], original_state_bytes=altered[1], refresh_state_bytes=altered[2]))
        self.assertFalse(resolved_development_business(args[3][0], proof, pid=PID,
            process_creation_filetime=CREATION + 1, journal_bytes=args[0], original_state_bytes=args[1], refresh_state_bytes=args[2]))
        proof['sources']['journal']['sha256'] = '0' * 64
        with self.assertRaises(ResolutionError):verify_equipment_progress_resolution(proof)

    def test_handler_premises_and_anchors_cannot_be_relaxed(self):
        for key, value in [('ack_to_absence_upper_bound_seconds', 0.3), ('frame_difference', 7),
            ('ack_frame', 1078789), ('correlation_premise_not_decoded', 'proven'),
            ('timing_premises_not_measured', []), ('same_exported_current_root', False)]:
            with self.subTest(key=key):
                args = list(inputs())
                args[4][key] = value
                with self.assertRaises(ResolutionError):build_equipment_progress_resolution(*args)

    def test_unknown_reason_opcode_module_and_extra_intent_rejected(self):
        mutations = [lambda j: j.update(module='spirit'), lambda j: j['events'].append(copy.deepcopy(j['events'][1])),
            lambda j: j['events'][1]['policy_state'].update(_requests=0),
            lambda j: j['events'][2]['wire']['state']['development']['equipment']['operation'].update(opcode=1105),
            lambda j: j['events'][2]['wire']['ack'].update(command_seq=5)]
        for mutate in mutations:
            args = list(inputs())
            journal = load_json_bytes(args[0])
            mutate(journal)
            args[0] = canonical_bytes(journal)
            with self.assertRaises(ResolutionError):build_equipment_progress_resolution(*args)
        args = list(inputs())
        original = load_json_bytes(args[1])
        original['development']['equipment']['operation']['reason'] = 'identity_changed'
        args[1] = canonical_bytes(original)
        with self.assertRaises(ResolutionError):build_equipment_progress_resolution(*args)

    def test_fact_conflicts_other_capability_and_metadata(self):
        args, proof = inputs(), self.proof()
        fact = copy.deepcopy(args[3][0])
        fact['retained_at'] = '2030-01-01T00:00:00Z'
        self.assertEqual(business_fact_hash(fact), business_fact_hash(args[3][0]))
        self.assertTrue(resolved_development_business(fact, proof, pid=PID, process_creation_filetime=CREATION,
            journal_bytes=args[0], original_state_bytes=args[1], refresh_state_bytes=args[2]))
        for mutate in [lambda f:f['business'].update(pending_attempt=True),
            lambda f:f['business']['development']['equipment']['operation'].update(opcode=1105),
            lambda f:f['business']['development']['spirit']['operation'].update(status='UNKNOWN'),
            lambda f:f['business'].update(player_refresh={'status':'UNKNOWN'}),
            lambda f:f.update(session_id='d'*32)]:
            fact = copy.deepcopy(args[3][0])
            mutate(fact)
            self.assertFalse(resolved_development_business(fact, proof, pid=PID, process_creation_filetime=CREATION,
                journal_bytes=args[0], original_state_bytes=args[1], refresh_state_bytes=args[2]))
            changed = list(inputs())
            changed[3].append(fact)
            with self.assertRaises(ResolutionError):build_equipment_progress_resolution(*changed)
        args = list(inputs())
        args[3] *= 65
        with self.assertRaises(ResolutionError):build_equipment_progress_resolution(*args)

    def test_refresh_requires_positive_capture_and_current_supported_context(self):
        changes = [('status', 'UNKNOWN'), ('callback_returned', False), ('context_rebuilt', False),
            ('main_ready', False), ('normal_responses_idle', False), ('root_level', 'OTHER'),
            ('opcode', 1104), ('receipt_capture', None), ('receipt_hash', '0'*64),
            ('operation_id', ORIGINAL_OPERATION), ('character_id', '1')]
        for key, value in changes:
            with self.subTest(key=key):
                args = list(inputs())
                refresh = load_json_bytes(args[2])
                refresh['player_refresh'][key] = value
                args[2] = canonical_bytes(refresh)
                with self.assertRaises(ResolutionError):build_equipment_progress_resolution(*args)
        for mutate in [lambda s:s.update(session_id=ORIGINAL_SESSION,nonce=ORIGINAL_SESSION),
            lambda s:s.update(process_creation_filetime=CREATION+1),
            lambda s:s.update(verified_native_tid=1),
            lambda s:s.update(updated_utc='2026-10-31T04:54:19Z'),
            lambda s:s.update(updated_utc='2026-10-31T05:00:00'),
            lambda s:s.pop('player_refresh'),
            lambda s:s['development'].update(character_id='1'),
            lambda s:s['development']['equipment']['candidates'][0].update(owner_slot_matches=False),
            lambda s:s['development']['equipment']['candidates'][0].update(level=0),
            lambda s:s['business'].update(pending_attempt=True),
            lambda s:s['business'].update(pending_player_refresh=True),
            lambda s:s['business']['player_refresh'].update(operation_id='e'*32),
            lambda s:s['campaign']['ui_context'].update(global_input_enabled=False)]:
            args = list(inputs())
            refresh = load_json_bytes(args[2]);mutate(refresh);args[2] = canonical_bytes(refresh)
            with self.assertRaises(ResolutionError):build_equipment_progress_resolution(*args)

    def test_target_raw_values_and_missing_capture_fields_rejected(self):
        for values in [[(1,1),(2,1),(6,0),(7,22)],[(1,1),(2,1),(6,1),(7,23)],
                       [(1,1),(2,2),(6,1),(7,22)],[(1,2),(2,1),(6,1),(7,22)]]:
            raw=packet(eq=b''.join(scalar(f,v) for f,v in values))
            args=list(inputs());refresh=load_json_bytes(args[2]);dto=refresh['player_refresh']
            dto.update(receipt_capture={'encoding':'base64','bytes':len(raw),'data':base64.b64encode(raw).decode()},
                receipt_hash=hashlib.sha256(raw).hexdigest(),server_snapshot=decode_sc16_projection(raw))
            args[2]=canonical_bytes(refresh)
            with self.assertRaises(ResolutionError):build_equipment_progress_resolution(*args)
        for key in ('bytes','data','encoding'):
            args=list(inputs());refresh=load_json_bytes(args[2]);refresh['player_refresh']['receipt_capture'].pop(key)
            args[2]=canonical_bytes(refresh)
            with self.assertRaises(ResolutionError):build_equipment_progress_resolution(*args)

    def test_raw_projection_mismatch_rejected_even_with_valid_receipt_hash(self):
        args = list(inputs())
        refresh = load_json_bytes(args[2])
        refresh['player_refresh']['server_snapshot']['equipments'][0]['level'] = 2
        args[2] = canonical_bytes(refresh)
        with self.assertRaises(ResolutionError):build_equipment_progress_resolution(*args)

    def test_completed_capital_refresh_requires_current_campaign(self):
        args=list(inputs());refresh=load_json_bytes(args[2])
        refresh['player_refresh']['root_level']='Capital'
        args[2]=canonical_bytes(refresh)
        build_equipment_progress_resolution(*args)
        refresh['campaign']['ui_context']['current_root_level']='Capital'
        args[2]=canonical_bytes(refresh)
        with self.assertRaises(ResolutionError):build_equipment_progress_resolution(*args)

    def test_target_level_owner_must_be_present_and_positive(self):
        for omitted in (1, 2, 6, 7):
            eq = b''.join(scalar(f, v) for f, v in [(1,1),(2,1),(6,1),(7,22)] if f != omitted)
            raw = packet(eq=eq)
            if omitted in (1,2):
                with self.assertRaises(ResolutionError):decode_sc16_projection(raw)
                continue
            projection = decode_sc16_projection(raw)
            self.assertFalse(projection['equipments'][0]['level_present' if omitted == 6 else 'owner_present'])
            args = list(inputs());refresh=load_json_bytes(args[2]);dto=refresh['player_refresh']
            dto.update(receipt_capture={'encoding':'base64','bytes':len(raw),'data':base64.b64encode(raw).decode()},
                       receipt_hash=hashlib.sha256(raw).hexdigest(),server_snapshot=projection)
            args[2]=canonical_bytes(refresh)
            with self.assertRaises(ResolutionError):build_equipment_progress_resolution(*args)

    def test_protobuf_negatives_duplicate_overflow_bad_lengths_and_missing(self):
        cases = [b'', b'\x1a\xff', packet(form=False), packet()+scalar(8,1),
            packet()+message(13,b''.join(scalar(f,v) for f,v in [(1,1),(2,1)])),
            packet(player=scalar(1,55290000000275)+scalar(1,2)),
            packet(eq=scalar(1,256)+scalar(2,1)),
            packet(eq=scalar(1,1)+scalar(2,1)+scalar(6,256)),
            packet(player=scalar(1,55290000000275)+scalar(23,2**32)),
            packet(player=scalar(1,55290000000275)+scalar(270,2**64)),
            packet()+b'\x00', packet()+b'\x0b', packet()+b'\xff'*12,
            packet(player=scalar(1,55290000000275)+scalar(33,1)),
            packet(player=scalar(1,55290000000275)+scalar(23,2)+scalar(270,2**64-2)),
            packet()+message(3,scalar(1,1)), b'\0'*1048577]
        for raw in cases:
            with self.subTest(raw=raw[:24]):
                with self.assertRaises(ResolutionError):decode_sc16_projection(raw)

    def test_optional_defaults_retain_presence(self):
        projection = decode_sc16_projection(packet(eq=scalar(1,1)+scalar(2,1)+scalar(6,1)+scalar(7,22)))
        self.assertEqual(projection['money'],0)
        self.assertFalse(projection['money_present'])
        self.assertFalse(projection['money64_present'])
        self.assertFalse(projection['equipments'][0]['accessory1_present'])
        self.assertTrue(projection['equipments'][0]['level_present'])

    def test_duplicate_json_keys_refused(self):
        args = list(inputs());args[0] = b'{"schema":1,"schema":1}'
        with self.assertRaises(ResolutionError):build_equipment_progress_resolution(*args)


class ResolverTests(unittest.TestCase):
    def setup_workspace(self, root):
        args = inputs()
        paths = {'development_runs/'+ORIGINAL_OPERATION+'.json':args[0],
            'object_sessions/'+ORIGINAL_SESSION+'/state.json':args[1],
            'object_sessions/'+SESSION+'/player-refresh-observed.json':args[2],
            f'object_sessions/leases/{PID}-{CREATION}.business.json':canonical_bytes(args[3][0])}
        for name, raw in paths.items():
            path=root/'data'/name;path.parent.mkdir(parents=True,exist_ok=True);path.write_bytes(raw)
        return paths

    def invoke(self, *args):
        with contextlib.redirect_stdout(io.StringIO()),contextlib.redirect_stderr(io.StringIO()):
            return main(list(args))

    def test_prepare_review_publish_is_append_only_in_detached_workspace(self):
        with tempfile.TemporaryDirectory() as directory,patch('tools.resolve_development_progress.PROJECT_ROOT',Path(directory)):
            root=Path(directory);paths=self.setup_workspace(root)
            candidate=root/'data/acceptance/candidate.json'
            shared=['--refresh-session',SESSION,'--candidate',str(candidate)]
            self.assertEqual(self.invoke('--prepare',*shared),0)
            destination=root/'data/development_resolutions'/f'{ORIGINAL_OPERATION}.json'
            self.assertFalse(destination.exists())
            self.assertEqual(self.invoke('--publish',*shared),2)
            self.assertEqual(self.invoke('--publish',*shared,'--reviewed-sha256','0'*64),2)
            seal=hashlib.sha256(candidate.read_bytes()).hexdigest()
            self.assertEqual(self.invoke('--publish',*shared,'--reviewed-sha256',seal),0)
            self.assertEqual(destination.read_bytes(),candidate.read_bytes())
            self.assertEqual(self.invoke('--publish',*shared,'--reviewed-sha256',seal),0)
            for path,raw in paths.items():self.assertEqual((root/'data'/path).read_bytes(),raw)
            destination.write_bytes(b'conflicting original output')
            self.assertEqual(self.invoke('--publish',*shared,'--reviewed-sha256',seal),2)
            self.assertEqual(destination.read_bytes(),b'conflicting original output')

    def test_publish_changed_source_and_path_escapes_refused(self):
        with tempfile.TemporaryDirectory() as directory,patch('tools.resolve_development_progress.PROJECT_ROOT',Path(directory)):
            root=Path(directory);self.setup_workspace(root)
            candidate=root/'data/acceptance/candidate.json';shared=['--refresh-session',SESSION,'--candidate',str(candidate)]
            self.assertEqual(self.invoke('--prepare',*shared),0)
            seal=hashlib.sha256(candidate.read_bytes()).hexdigest()
            journal=root/'data/development_runs'/f'{ORIGINAL_OPERATION}.json'
            journal.write_bytes(journal.read_bytes()+b' ')
            self.assertEqual(self.invoke('--publish',*shared,'--reviewed-sha256',seal),2)
            self.assertEqual(self.invoke('--prepare','--refresh-session','../bad','--candidate',str(candidate)),2)
            self.assertEqual(self.invoke('--prepare','--refresh-session',SESSION,'--candidate',str(root/'data/development_runs/new.json')),2)
            self.assertFalse((root/'data/development_resolutions').exists())


if __name__ == '__main__':
    unittest.main()
