"""Exact artifact bindings for the native, connection-only soul reconciliation.

Saved evidence selects the one retired executor to inspect. Only the new native
receipt together with its matching native state can grant connection authority.
"""
from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import re
import stat


KIND = 'soul-connection-reconciliation'
INTERFACE = KIND + '-v1'
REASON = 'daily_soul_request_or_cleanup_unresolved_restart_required'
_HEX32 = re.compile('[0-9a-f]{32}')
_HEX64 = re.compile('[0-9a-f]{64}')
_ASSEMBLY = re.compile('JSAssistantBridge_[0-9a-f]{16}')
_FILES = ('recovery_unknown.json', 'state.json', 'session.json')


class RecoveryRefused(ValueError):
    pass


def _require(predicate, reason):
    if not predicate:
        raise RecoveryRefused(reason)


def read_artifact(root: Path, session: str, name: str) -> tuple[dict, bytes]:
    _require(bool(_HEX32.fullmatch(session)), 'recovery_session_identity')
    root = Path(root).absolute()
    path = root / session / name
    _require(path.resolve() == path, 'recovery_artifact_path_escape')
    for candidate in (root, path.parent, path):
        info = candidate.lstat()
        _require(not candidate.is_symlink() and not (
            getattr(info, 'st_file_attributes', 0) & getattr(stat, 'FILE_ATTRIBUTE_REPARSE_POINT', 0x400)),
            'recovery_artifact_reparse_point')
    _require(path.is_file() and path.stat().st_size <= 4 * 1024 * 1024, 'recovery_artifact_size')
    with path.open('rb') as stream:
        raw = stream.read(4 * 1024 * 1024 + 1)
    _require(len(raw) <= 4 * 1024 * 1024, 'recovery_artifact_size')
    def unique(pairs):
        result = {}
        for key, value in pairs:
            _require(key not in result, 'recovery_artifact_duplicate_key')
            result[key] = value
        return result
    value = json.loads(raw.decode('utf-8-sig'), object_pairs_hook=unique)
    _require(type(value) is dict, 'recovery_artifact_shape')
    return value, raw


def _exact(value, expected, prefix):
    for key, item in expected.items():
        _require(type(value.get(key)) is type(item) and value[key] == item, prefix + ':' + key)


def same_json(left, right) -> bool:
    return json.dumps(left, sort_keys=True, separators=(',', ':'), ensure_ascii=True) == json.dumps(
        right, sort_keys=True, separators=(',', ':'), ensure_ascii=True)


@dataclass(frozen=True)
class SoulRecoveryDescriptor:
    fields: dict
    first_unknown: dict

    @classmethod
    def capture(cls, root: Path, identity, character_id: str, old_session_id: str | None = None,
                *, require_only_marker: bool = True):
        _require(type(character_id) is str and character_id.isascii() and character_id.isdecimal()
                 and 0 < int(character_id) < 2**64, 'recovery_character_identity')
        matches = []
        for path in root.glob('*/recovery_unknown.json'):
            if not _HEX32.fullmatch(path.parent.name):
                continue
            marker, _ = read_artifact(root, path.parent.name, path.name)
            if (marker.get('pid') == identity.pid and
                    marker.get('process_creation_filetime') == identity.creation_time and
                    marker.get('process_restart_required') is True):
                matches.append(path.parent.name)
        if require_only_marker:
            _require(len(matches) == 1, 'recovery_requires_one_exact_unresolved_soul_session')
            session = matches[0]
        else:
            _require(old_session_id in matches, 'recovery_resolution_old_marker_missing')
            session = old_session_id
        _require(old_session_id in (None, '', session), 'recovery_retained_session_mismatch')
        artifacts = {name: read_artifact(root, session, name) for name in _FILES}
        marker, state, ledger = (artifacts[name][0] for name in _FILES)
        common = dict(protocol=3, bridge_id='jiangshen-assistant-win32-v1', nonce=session,
                      session_id=session, pid=identity.pid, process_creation_filetime=identity.creation_time)
        for name, value in ((name, artifacts[name][0]) for name in _FILES):
            _exact(value, common, 'recovery_old_' + name)
        _exact(marker, dict(reason=REASON, process_restart_required=True), 'recovery_old_marker')
        _exact(state, dict(reason=REASON, lifecycle='recovery_unknown', ready=False,
                           component_destroyed=True, process_restart_required=True), 'recovery_old_state')
        assembly = marker.get('assembly')
        _require(type(assembly) is str and bool(_ASSEMBLY.fullmatch(assembly))
                 and state.get('assembly') == assembly and type(ledger.get('build_id')) is str
                 and bool(_HEX64.fullmatch(ledger['build_id']))
                 and assembly == 'JSAssistantBridge_' + ledger['build_id'][:16], 'recovery_old_assembly')
        for value in (marker, state):
            recovery = value.get('recovery')
            _require(type(recovery) is dict, 'recovery_old_recovery_shape')
            _exact(recovery, dict(classification='restart_required', process_restart_required=True,
                                 reason=REASON, replay_allowed=False), 'recovery_old_boundary')
            _require(recovery.get('battle_launch') is None, 'recovery_old_battle_witness')
        soul = state.get('daily_soul')
        _require(type(soul) is dict and soul.get('schema') == 1 and soul.get('character_id') == character_id
                 and soul.get('ui_cleanup') == 'clear', 'recovery_old_soul_identity')
        _require((state.get('campaign') or {}).get('character_id') == character_id,
                 'recovery_old_campaign_character')
        operation = soul.get('operation')
        _require(type(operation) is dict and type(operation.get('operation_id')) is str
                 and bool(_HEX32.fullmatch(operation['operation_id'])), 'recovery_old_operation_identity')
        _exact(operation, dict(stage='grow', status='UNKNOWN', reason='stop', invoked=True,
                              mutation_attempted=True, opcode=519, start_verified=True,
                              harvest_verified=False, close_verified=False, growth_verified=True),
               'recovery_old_operation')
        growth, stop = operation.get('sequence'), state.get('last_command_seq')
        _require(type(growth) is int and growth > 0 and type(stop) is int and stop > growth
                 and operation.get('growth_evidence_sequence') == growth
                 and operation.get('harvest_evidence_sequence') is None, 'recovery_old_sequence')
        _exact(ledger, dict(state='unknown', pending_command='stop'), 'recovery_old_ledger')
        _require(type(ledger.get('last_command_seq')) is int and ledger['last_command_seq'] >= stop,
                 'recovery_old_ledger_sequence')
        first = ledger.get('first_unknown')
        _require(type(first) is dict and first.get('command_name') == 'stop'
                 and first.get('command_seq') == stop, 'recovery_old_first_unknown')
        business = state.get('business')
        _require(type(business) is dict and business.get('pending_daily_soul') is True
                 and all(value is False for key, value in business.items()
                         if key.startswith('pending_') and key != 'pending_daily_soul'),
                 'recovery_old_non_soul_pending')
        _require(state.get('battle_launch') is None and (state.get('action') or {}).get('outcome') == 'NONE',
                 'recovery_old_other_mutation')
        fields = dict(pid=identity.pid, process_creation_filetime=identity.creation_time,
                      character_id=character_id, old_session_id=session,
                      old_operation_id=operation['operation_id'], old_growth_sequence=growth,
                      old_stop_sequence=stop, old_assembly_name=assembly)
        for name, key in zip(_FILES, ('old_marker_sha256', 'old_state_sha256', 'old_session_sha256')):
            fields[key] = hashlib.sha256(artifacts[name][1]).hexdigest()
        return cls(fields, dict(first))

    def revalidate(self, root: Path):
        for name, key in zip(_FILES, ('old_marker_sha256', 'old_state_sha256', 'old_session_sha256')):
            _, raw = read_artifact(root, self.fields['old_session_id'], name)
            _require(hashlib.sha256(raw).hexdigest() == self.fields[key], 'recovery_old_artifact_changed:' + name)

    def request_bytes(self, nonce: str) -> bytes:
        _require(bool(_HEX32.fullmatch(nonce)), 'recovery_new_session_identity')
        fields = dict(version=1, kind=KIND, nonce=nonce, **self.fields)
        return ''.join(f'{key}={value}\n' for key, value in fields.items()).encode('ascii')


def validate_resolution(root: Path, new_session: str, descriptor: SoulRecoveryDescriptor,
                        state: dict, assembly: str, native_tid: int, *, require_fresh: bool = False) -> dict:
    receipt, raw = read_artifact(root, new_session, 'same_process_resolution.json')
    _exact(receipt, dict(schema=1, kind=KIND, status='RECONCILED', reason='',
                        new_session_id=new_session, **descriptor.fields,
                        managed_assembly_name=assembly, native_main_thread_id=native_tid,
                        scope='connection_only', old_outcome='UNKNOWN', replay_allowed=False,
                        old_owner_retired=True, callbacks_quiescent=True, transport_quiescent=True,
                        old_operation_quarantined=True), 'recovery_native_resolution')
    _require(type(receipt.get('soul_mutations_quarantined')) is bool, 'recovery_soul_quarantine_missing')
    first, last, samples = receipt.get('first_frame'), receipt.get('last_frame'), receipt.get('samples')
    _require(type(first) is int and type(last) is int and 0 <= first < last
             and type(samples) is list and len(samples) >= 2, 'recovery_fresh_frame_proof')
    managed_tid = receipt.get('managed_main_thread_id')
    _require(type(managed_tid) is int and managed_tid > 0, 'recovery_managed_thread_proof')
    previous_frame, previous_time, reference = -1, None, None
    for sample in samples:
        _require(type(sample) is dict, 'recovery_sample_shape')
        _exact(sample, dict(native_main_thread_id=native_tid, managed_main_thread_id=managed_tid,
                            character_id=descriptor.fields['character_id'], gate='PASS',
                            transport_quiescent=True, callbacks_quiescent=True,
                            messages=0, send_queue=0, sending=False, soul_handlers_present=False),
               'recovery_sample')
        _require(type(sample.get('frame')) is int and sample['frame'] > previous_frame,
                 'recovery_sample_frame_order')
        _require(type(sample.get('root_instance_id')) is int and sample['root_instance_id'] != 0
                 and type(sample.get('root_level_id')) is str and bool(sample['root_level_id']),
                 'recovery_sample_root_identity')
        for key in ('cycle_id', 'server_now', 'next_resource_time', 'blue_used', 'purple_used', 'orange_used'):
            _require(type(sample.get(key)) is int and 0 <= sample[key] <= 2**32 - 1,
                     'recovery_sample_counter:' + key)
        _require(sample['cycle_id'] > 0 and type(sample.get('resources')) is dict
                 and all(type(sample['resources'].get(key)) is int for key in
                         ('level', 'stamina', 'gold', 'bind_gold', 'money', 'money64', 'mana')),
                 'recovery_sample_resources')
        board = sample.get('board')
        _require(type(board) is list and len(board) == 8 and all(type(slot) is dict and
                 type(slot.get('color')) is int and 0 <= slot['color'] <= 255 and
                 type(slot.get('level')) is int and 0 <= slot['level'] <= 255 for slot in board)
                 and type(sample.get('board_packed')) is int, 'recovery_sample_board')
        try:
            observed = datetime.fromisoformat(sample['observed_utc'].replace('Z', '+00:00'))
            _require(observed.utcoffset() is not None and
                     (previous_time is None or observed >= previous_time), 'recovery_sample_time_order')
            if require_fresh:
                _require(-2 <= (datetime.now(timezone.utc) - observed).total_seconds() <= 10,
                         'recovery_sample_stale')
        except (AttributeError, KeyError, TypeError, ValueError) as exc:
            raise RecoveryRefused('recovery_sample_time:' + str(exc)) from exc
        identity = (sample['root_instance_id'], sample['root_level_id'])
        _require(reference in (None, identity), 'recovery_sample_root_changed')
        reference, previous_frame, previous_time = identity, sample['frame'], observed
    _require(samples[0]['frame'] == first and samples[-1]['frame'] == last
             and receipt.get('fresh') == samples[-1], 'recovery_sample_fresh_link')
    if receipt['soul_mutations_quarantined'] is False:
        stable = ('cycle_id', 'next_resource_time', 'blue_used', 'purple_used', 'orange_used')
        _require(all(sample['board_packed'] == 0 and all(slot['color'] == slot['level'] == 0
                     for slot in sample['board']) and all(sample[key] == samples[0][key] for key in stable)
                     for sample in samples), 'recovery_soul_mutation_grant_without_empty_stable_board')
    linked = dict(receipt, resolution_sha256=hashlib.sha256(raw).hexdigest())
    _require(same_json(state.get('same_process_recovery'), linked), 'recovery_native_state_receipt_mismatch')
    _exact(state, dict(protocol=3, bridge_id='jiangshen-assistant-win32-v1', nonce=new_session,
                       session_id=new_session, pid=descriptor.fields['pid'],
                       process_creation_filetime=descriptor.fields['process_creation_filetime'],
                       verified_native_tid=native_tid, managed_tid=managed_tid, assembly=assembly),
           'recovery_native_state_identity')
    _require((state.get('campaign') or {}).get('character_id') == descriptor.fields['character_id'],
             'recovery_native_state_character')
    descriptor.revalidate(root)
    return linked


def accepted_resolution(root: Path, identity, marker_session: str) -> dict | None:
    """A durable receipt alone never releases a marker: require paired state/ledger."""
    for path in root.glob('*/same_process_resolution.json'):
        if not _HEX32.fullmatch(path.parent.name):
            continue
        receipt, _ = read_artifact(root, path.parent.name, path.name)
        if (receipt.get('old_session_id') != marker_session or receipt.get('pid') != identity.pid
                or receipt.get('process_creation_filetime') != identity.creation_time):
            continue
        descriptor = SoulRecoveryDescriptor.capture(root, identity, receipt.get('character_id'), marker_session,
                                                     require_only_marker=False)
        state, _ = read_artifact(root, path.parent.name, 'state.json')
        ledger, _ = read_artifact(root, path.parent.name, 'session.json')
        assembly = receipt.get('managed_assembly_name')
        _require(type(assembly) is str and bool(_ASSEMBLY.fullmatch(assembly)), 'recovery_resolution_assembly')
        linked = validate_resolution(root, path.parent.name, descriptor, state, assembly, identity.tid)
        _require(same_json(ledger.get('same_process_recovery'), linked) and
                 ledger.get('same_process_recovery_accepted') is True and
                 ledger.get('session_id') == path.parent.name and ledger.get('pid') == identity.pid and
                 ledger.get('process_creation_filetime') == identity.creation_time and
                 ledger.get('build_id', '')[:16] == assembly.removeprefix('JSAssistantBridge_'),
                 'recovery_resolution_ledger_mismatch')
        return linked
    return None
