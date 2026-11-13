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

from jiangshen_assistant.campaign_recovery import (ResolutionError, canonical_bytes, load_json_bytes,
    prepare_resolution, resolve_checkpoint, resolved_business, validate_resolution)
from tools.resolve_cleared_campaign import main, write_exclusive


def proof_inputs():
    role, old, helper, attempt = '55290000000275', '7' * 32, 'f' * 32, 'b' * 32
    context = {'character_id': role, 'creation': 134378768910968977, 'generation': 2, 'nonce': old, 'pid': 2648}
    checkpoint = {'version': 3, 'run_id': '9' * 32, 'context_key': json.dumps(context), 'losses': 0,
        'mode': 'blocked', 'reason': 'bridge_attempt_unknown', 'last_revision': 5, 'guide_step': 'new-skill',
        'completed_guides': [], 'pending': {'intent_id': attempt, 'kind': 'challenge', 'revision': 5,
            'stage_key': '57:3', 'step_key': '', 'action_key': '', 'attempt_id': attempt, 'uncertain': True}, 'consumed': []}
    guard = {'business': {'attempt': {'attempt_id': attempt, 'campaign_id': 57, 'character_id': role,
        'command_seq': 186, 'evidence': 'settlement_receipt_missing_or_not_applied', 'outcome': 'UNKNOWN',
        'report_id': '0', 'status': 'UNKNOWN', 'troop_id': 3}, 'character_id': role,
        'mutation_attempted': True, 'pending_attempt': True, 'resource_rollback': False},
        'character_id': role, 'created_at': '2020-01-01T00:00:00Z', 'pid': 2648,
        'prior_unresolved_business': None, 'process_creation_filetime': context['creation'], 'protocol': 3,
        'retained_at': '2020-01-01T00:00:00Z', 'session_id': old}
    empty = {'attempt_id': '', 'character_id': '', 'command_seq': 0, 'campaign_id': 0, 'troop_id': 0,
             'status': 'NONE', 'report_id': '0', 'outcome': 'UNKNOWN', 'evidence': 'not_started'}
    before = {'protocol': 3, 'bridge_id': 'jiangshen-assistant-win32-v1', 'nonce': helper, 'session_id': helper,
        'pid': 2648, 'process_creation_filetime': context['creation'], 'verified_native_tid': 30488, 'managed_tid': 1,
        'assembly': 'JSAssistantBridge_bb4ef77364d9de6e', 'updated_utc': '2020-01-01T00:01:00Z',
        'lifecycle': 'ready', 'ready': True, 'main_thread_verified': True, 'component_destroyed': False,
        'last_command_seq': 245, 'frame': 701672,
        'action': {'command_seq': 0, 'phase': 'none', 'outcome': 'NONE', 'mutation_attempted': False},
        'business': {'character_id': '', 'mutation_attempted': False, 'pending_attempt': False,
                     'resource_rollback': False, 'attempt': copy.deepcopy(empty)},
        'campaign_gui': {'command_seq': 0, 'phase': 'none', 'invocation_attempted': False},
        'campaign': {'schema': 1, 'interface': 'scoped-campaign-gui-skip-v1', 'revision': 1, 'available': True,
            'gate': 'PASS', 'phase': 'RESULT', 'character_id': role, 'campaign_id': 57, 'troop_id': 3,
            'result_kind': 'NORMAL', 'tutorial': {'kind': 'NONE', 'step': '', 'gate': ''}, 'attempt': copy.deepcopy(empty),
            'ui_context': {'current_root_instance_id': 11726, 'current_root_level': 'Campaign',
                'current_root_active': False, 'global_input_enabled': True, 'selection_gate': 'PASS', 'candidates_truncated': False,
                'campaign_candidates': [], 'counts': {key: {'total': 1, 'visible': visible, 'matching': visible}
                    for key, visible in (('root_ui', 1), ('campaign', 0), ('battle', 1), ('normal_result', 1), ('reward', 0), ('scenario', 0))}},
            'selection_diagnostics': {'character_campaign_id': 57, 'truncated': False, 'campaign': {'type': 0,
                'enabled': False, 'unlocked': True, 'finished': False, 'm_bDebug': False, 'm_bPostInit': False,
                'm_bBorning': False, 'm_bBornWant': False, 'm_bScenarioVisible': False, 'm_bSignActiving': False}},
            'settlement_diagnostics': {'anchor_scope': 'this_helper_instance', 'character_id': role,
                'observer': {'status': 'NONE', 'anchors_available': False, **{key: None for key in ('zero_observed', 'battle_observed',
                    'receipt_captured', 'campaign_match', 'battle_match', 'character_match', 'character_id_match', 'report_token_match')}},
                'battle': {'instance_id': 11642, 'parent_instance_id': 11774, 'owner_root_instance_id': 11656,
                    'previous_root_instance_id': 11726, 'visible': True, 'parent_matches_campaign': True,
                    'parent_owned_by_current': True, 'previous_root_matches_current': True, 'owner_root_level': 'OTHER',
                    'idcampaign': 57, 'idtroop': 3, 'report_id': '55290000099281', 'view_type': 3, 'replay': False,
                    'cross': False, 'surrender': False, 'waiting': False, 'use_end': False, 'fighting': False,
                    'pending_present': False, 'packet': None},
                'result': {'instance_id': -10734, 'report_id': '55290000099281', 'battle_match': True, 'root_match': True, 'battle_view': False},
                'target': {'idcampaign': 57, 'idtroop': 3, 'matches_battle_target': True, 'finish_count': 1, 'finish_max': 1,
                    'killed': True, 'canattack': True, 'campaign_finished': False, 'data_present': True, 'data_anchor_match': None}}}}
    after = copy.deepcopy(before)
    after.update(updated_utc='2020-01-01T00:02:00Z', last_command_seq=319, frame=707040)
    c = after['campaign']
    c.update(phase='CAMPAIGN', result_kind='', troop_id=0, revision=6)
    c['ui_context']['current_root_active'] = True
    for key, visible, matching in (('campaign', 1, 1), ('battle', 0, 0), ('normal_result', 0, 0), ('battle_baseline', 0, 1)):
        c['ui_context']['counts'][key] = {'total': 1, 'visible': visible, 'matching': matching}
    c['ui_context']['campaign_candidates'] = [{'instance_id': 11774, 'visible': True,
        'owner_matches_current': True, 'hierarchy_matches_current': True, 'owner_root_level': 'Campaign'}]
    c['selection_diagnostics']['campaign']['enabled'] = True
    c['settlement_diagnostics']['battle']['visible'] = False
    c['settlement_diagnostics']['result'] = None
    return checkpoint, guard, before, after


def change(data, path, value):
    for key in path[:-1]:
        data = data[key]
    data[path[-1]] = value


class ResolutionTests(unittest.TestCase):
    def setUp(self):
        self.inputs = proof_inputs()
        self.resolution = prepare_resolution(*self.inputs)
        self.process = {'pid': 2648, 'process_creation_filetime': 134378768910968977}

    def test_exact_byte_binding_and_no_history_rewrite(self):
        cp, guard, before, after = self.inputs
        frozen = copy.deepcopy(self.inputs)
        raw = (json.dumps(cp, indent=2) + '\n').encode()
        r = prepare_resolution(cp, guard, before, after, checkpoint_bytes=raw)
        self.assertTrue(resolve_checkpoint(raw, r))
        self.assertFalse(resolve_checkpoint(canonical_bytes(cp), r))
        self.assertEqual(self.inputs, frozen)
        self.assertEqual(r['historical_outcome'], 'UNKNOWN')
        self.assertIs(r['consumes_result'], False)
        self.assertIs(r['resets_losses'], False)
        self.assertEqual(r['sources']['checkpoint']['content']['consumed'], [])
        self.assertTrue(resolved_business(guard, r, **self.process))

    def test_existing_loss_and_consumed_receipts_are_preserved(self):
        cp, guard, before, after = self.inputs
        cp['losses'] = 1
        cp['consumed'] = [{'attempt_id': 'a' * 32, 'report_id': '99', 'outcome': 'loss'}]
        r = prepare_resolution(cp, guard, before, after)
        self.assertEqual(r['sources']['checkpoint']['content'], cp)
        self.assertEqual(cp['losses'], 1)

    def test_binding_bytes_must_describe_supplied_content(self):
        with self.assertRaises(ResolutionError):
            prepare_resolution(*self.inputs, checkpoint_bytes=b'{}')

    def test_original_identity_or_pending_mismatch_refused(self):
        cases = [(('pid',), 1), (('process_creation_filetime',), 42), (('session_id',), 'e' * 32),
                 (('character_id',), '999'), (('protocol',), 4), (('business', 'attempt', 'attempt_id'), 'c' * 32),
                 (('business', 'attempt', 'campaign_id'), 58), (('business', 'attempt', 'troop_id'), 4),
                 (('business', 'attempt', 'status'), 'SETTLED'), (('business', 'attempt', 'outcome'), 'WIN'),
                 (('business', 'attempt', 'command_seq'), True), (('business', 'attempt', 'character_id'), '999'),
                 (('business', 'character_id'), '999')]
        for path, value in cases:
            with self.subTest(path=path), self.assertRaises(ResolutionError):
                cp, guard, before, after = proof_inputs()
                change(guard, path, value)
                prepare_resolution(cp, guard, before, after)
        cp, guard, before, after = proof_inputs()
        cp['pending']['attempt_id'] = 'c' * 32
        with self.assertRaises(ResolutionError):
            prepare_resolution(cp, guard, before, after)

    def test_extra_or_inherited_pending_never_silently_resolved(self):
        for inherited in (copy.deepcopy(self.inputs[1]), {'business': {'pending_attempt': True}}, []):
            with self.subTest(inherited=inherited), self.assertRaises(ResolutionError):
                cp, guard, before, after = proof_inputs()
                guard['prior_unresolved_business'] = inherited
                prepare_resolution(cp, guard, before, after)
        fact = copy.deepcopy(self.inputs[1])
        fact['prior_unresolved_business'] = copy.deepcopy(fact)
        self.assertFalse(resolved_business(fact, self.resolution, **self.process))
        fact = copy.deepcopy(self.inputs[1])
        fact['other_pending'] = {'pending_attempt': True}
        self.assertFalse(resolved_business(fact, self.resolution, **self.process))

    def test_before_negative_proofs(self):
        prefix = ('campaign', 'settlement_diagnostics')
        cases = [(('protocol',), 4), (('pid',), 999), (('process_creation_filetime',), 42), (('nonce',), 'a' * 32),
                 (('session_id',), 'a' * 32), (('managed_tid',), 2), (('verified_native_tid',), None),
                 (('campaign', 'character_id'), '999'), (('business', 'pending_attempt'), True),
                 (('action', 'mutation_attempted'), True), (('campaign_gui', 'invocation_attempted'), True),
                 (('campaign', 'available'), False), (('campaign', 'gate'), 'BLOCKED'), (('campaign', 'result_kind'), 'REPLAY'),
                 (prefix + ('target', 'killed'), False), (prefix + ('target', 'finish_count'), 0),
                 (prefix + ('target', 'finish_max'), 2), (prefix + ('target', 'data_present'), False),
                 (prefix + ('target', 'matches_battle_target'), False), (prefix + ('target', 'idtroop'), 5),
                 (prefix + ('target', 'data_anchor_match'), True), (prefix + ('observer', 'zero_observed'), True),
                 (prefix + ('battle', 'waiting'), True), (prefix + ('battle', 'replay'), True),
                 (prefix + ('battle', 'cross'), True), (prefix + ('battle', 'surrender'), True),
                 (prefix + ('battle', 'use_end'), True), (prefix + ('battle', 'fighting'), True),
                 (prefix + ('battle', 'pending_present'), True), (prefix + ('battle', 'packet'), {'win': True}),
                 (prefix + ('battle', 'report_id'), '0'), (prefix + ('battle', 'previous_root_matches_current'), False),
                 (prefix + ('battle', 'parent_owned_by_current'), False), (prefix + ('result', 'report_id'), '123'),
                 (prefix + ('result', 'root_match'), False), (prefix + ('result', 'battle_match'), False)]
        for path, value in cases:
            with self.subTest(path=path), self.assertRaises(ResolutionError):
                cp, guard, before, after = proof_inputs()
                change(before, path, value)
                prepare_resolution(cp, guard, before, after)

    def test_after_must_be_same_battle_and_stable_map(self):
        prefix = ('campaign', 'settlement_diagnostics')
        cases = [(('session_id',), 'a' * 32), (('assembly',), 'JSAssistantBridge_0123456789abcdef'),
                 (('campaign', 'phase'), 'RESULT'), (('campaign', 'troop_id'), 5),
                 (('campaign', 'ui_context', 'current_root_active'), False),
                 (('campaign', 'ui_context', 'current_root_instance_id'), 123),
                 (('campaign', 'ui_context', 'global_input_enabled'), False),
                 (('campaign', 'selection_diagnostics', 'campaign', 'enabled'), False),
                 (('campaign', 'selection_diagnostics', 'campaign', 'unlocked'), False),
                 (('campaign', 'selection_diagnostics', 'campaign', 'm_bDebug'), True),
                 (('campaign', 'selection_diagnostics', 'campaign', 'm_bBorning'), True),
                 (('campaign', 'ui_context', 'counts', 'scenario', 'visible'), 1),
                 (prefix + ('battle', 'visible'), True), (prefix + ('battle', 'instance_id'), 111),
                 (prefix + ('battle', 'parent_instance_id'), 222), (prefix + ('battle', 'report_id'), '555'),
                 (prefix + ('target', 'idtroop'), 5), (prefix + ('target', 'finish_count'), 0),
                 (prefix + ('result',), {}), (('updated_utc',), '2020-01-01T00:01:00Z'),
                 (('frame',), 701672), (('campaign', 'revision'), 1)]
        for path, value in cases:
            with self.subTest(path=path), self.assertRaises(ResolutionError):
                cp, guard, before, after = proof_inputs()
                change(after, path, value)
                prepare_resolution(cp, guard, before, after)

    def test_hash_content_future_schema_and_semantic_forgery_fail_closed(self):
        for path, value in [(('version',), 2), (('historical_outcome',), 'WIN'), (('consumes_result',), True),
                            (('resets_losses',), True), (('identity', 'command_seq'), 187),
                            (('created_utc',), '2099-01-01T00:00:00Z'), (('sources', 'checkpoint', 'sha256'), '0' * 64),
                            (('sources', 'before', 'content', 'protocol'), 4)]:
            with self.subTest(path=path):
                r = copy.deepcopy(self.resolution)
                change(r, path, value)
                self.assertFalse(resolve_checkpoint(canonical_bytes(self.inputs[0]), r))
                self.assertFalse(resolved_business(self.inputs[1], r, **self.process))
        # Rehashing malformed proof content must not bypass semantic checks.
        r = copy.deepcopy(self.resolution)
        src = r['sources']['after']
        src['content']['campaign']['settlement_diagnostics']['target']['killed'] = False
        raw = canonical_bytes(src['content'])
        src.update(sha256=hashlib.sha256(raw).hexdigest(), raw_base64=base64.b64encode(raw).decode())
        self.assertFalse(resolve_checkpoint(canonical_bytes(self.inputs[0]), r))

    def test_changed_business_and_process_rejected_but_retention_time_ignored(self):
        for path, value in [(('business', 'attempt', 'command_seq'), 187), (('business', 'attempt', 'evidence'), 'changed'),
                            (('business', 'attempt', 'outcome'), 'WIN'), (('character_id',), '999'),
                            (('session_id',), 'a' * 32), (('pid',), 999)]:
            with self.subTest(path=path):
                fact = copy.deepcopy(self.inputs[1])
                change(fact, path, value)
                self.assertFalse(resolved_business(fact, self.resolution, **self.process))
        self.assertFalse(resolved_business(self.inputs[1], self.resolution, pid=2648, process_creation_filetime=42))
        self.assertFalse(resolved_business(self.inputs[1], self.resolution, pid=True, process_creation_filetime=134378768910968977))
        fact = {key: self.inputs[1][key] for key in ('session_id', 'business', 'character_id')}
        fact['retained_at'] = 'changed'
        self.assertTrue(resolved_business(fact, self.resolution, **self.process))

    def test_partial_duplicate_and_unknown_fields_fail_closed(self):
        for r in (None, {}, {'approved': True}, {**self.resolution, 'approved': True}):
            self.assertFalse(resolve_checkpoint(canonical_bytes(self.inputs[0]), r))
        for raw in (b'{"x":1,"x":2}', b'{"x":NaN}', b'\xff'):
            with self.assertRaises(ResolutionError):
                load_json_bytes(raw)
        cp = copy.deepcopy(self.inputs[0])
        cp['pending']['uncertain'] = False
        self.assertFalse(resolve_checkpoint(canonical_bytes(cp), self.resolution))
        for source, path in ((1, ('created_at',)), (1, ('prior_unresolved_business',)),
                             (2, ('campaign', 'settlement_diagnostics', 'battle', 'packet')),
                             (3, ('campaign', 'settlement_diagnostics', 'target', 'data_anchor_match'))):
            with self.subTest(missing=path), self.assertRaises(ResolutionError):
                inputs = proof_inputs()
                parent = inputs[source]
                for key in path[:-1]:
                    parent = parent[key]
                del parent[path[-1]]
                prepare_resolution(*inputs)


class ResolutionCliTests(unittest.TestCase):
    def invoke(self, args):
        with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            return main(args)

    def files(self, root):
        args = []
        paths = []
        for key, value in zip(('checkpoint', 'guard', 'before', 'after'), proof_inputs()):
            path = root / (key + '.json')
            path.write_bytes(json.dumps(value, indent=2).encode())
            args += ['--' + key, str(path)]
            paths.append(path)
        return args, paths

    def test_prepare_has_no_active_effects_and_does_not_modify_inputs(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            args, paths = self.files(root)
            original = [p.read_bytes() for p in paths]
            candidate = root / 'candidate.json'
            self.assertEqual(self.invoke(args + ['--output', str(candidate)]), 0)
            r = load_json_bytes(candidate.read_bytes())
            self.assertTrue(resolve_checkpoint(original[0], r))
            self.assertEqual([p.read_bytes() for p in paths], original)
            self.assertFalse((root / 'data').exists())

    def test_publish_requires_reviewed_candidate_and_unchanged_sources(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            args, paths = self.files(root)
            candidate = root / 'candidate.json'
            output = root / 'data' / 'campaign_resolutions' / ('9' * 32 + '.json')
            with patch('tools.resolve_cleared_campaign.PROJECT_ROOT', root):
                self.assertEqual(self.invoke(args + ['--output', str(output)]), 2)
                for directory in ('campaign_runs', 'object_sessions'):
                    self.assertEqual(self.invoke(args + ['--output', str(root / 'data' / directory / 'new.json')]), 2)
                self.assertEqual(self.invoke(args + ['--output', str(output), '--publish']), 2)
                self.assertEqual(self.invoke(args + ['--output', str(candidate)]), 0)
                paths[0].write_bytes(paths[0].read_bytes() + b'\n')
                publish = args + ['--output', str(output), '--publish', '--candidate', str(candidate)]
                self.assertEqual(self.invoke(publish), 2)
                self.assertFalse(output.exists())
                paths[0].write_bytes(paths[0].read_bytes()[:-1])
                self.assertEqual(self.invoke(publish), 0)
                self.assertEqual(self.invoke(publish), 0)
                self.assertEqual(output.read_bytes(), candidate.read_bytes())
                self.assertEqual(self.invoke(args + ['--output', str(root / 'wrong.json'), '--publish', '--candidate', str(candidate)]), 2)

    def test_exclusive_create_conflict_never_overwrites(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'proof.json'
            r = prepare_resolution(*proof_inputs())
            self.assertTrue(write_exclusive(path, r))
            self.assertFalse(write_exclusive(path, r))
            before = path.read_bytes()
            other = copy.deepcopy(r)
            other['created_utc'] = '2020-01-01T00:03:00+00:00'
            with self.assertRaises(ResolutionError):
                write_exclusive(path, other)
            self.assertEqual(path.read_bytes(), before)
            self.assertEqual(list(path.parent.glob('*.tmp')), [])


if __name__ == '__main__':
    unittest.main()
