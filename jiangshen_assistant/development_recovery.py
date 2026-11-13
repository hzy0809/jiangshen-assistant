"""Offline interpretation of one archived equipment incident.

Used by the explicit historical export tool, never by task startup or admission.
Hashes identify supplied bytes, not their capture provenance. Historical
consumption inference remains conditional on the recorded premises; a captured
SC16 describes only the state at its capture time.
"""
from __future__ import annotations

import base64
import copy
import hashlib
import re
from datetime import datetime

from .campaign_recovery import ResolutionError, canonical_bytes, load_json_bytes

ORIGINAL_OPERATION = '43811cbb86284799ba7e2ebeadcd5661'
ORIGINAL_SESSION = '5ca104750a7a40559ad89dc6ecc075e3'
CHARACTER = '55290000000275'
PID = 2648
CREATION = 134378768910968977
MAX_SOURCE_BYTES = 8 * 1024 * 1024
SOURCE_ANCHORS = {
    'final-PlayerStateRefreshModel.cs': 'e53301bde90f2bf88aa4d4eb4f79ea0993cc262d2331b6a2b687c4886b5830f9',
    'Assembly-CSharp.dll': 'c1c8ebac7ddef4173c873a00774975a5ca366f95b19164c4ca775165eb994911',
    'pbdata.dll': '905cf19394545e2c4b5d49d994a7d981e13ae38a2a629f7d02329f27c6d2d5ab',
    'tcp_base.cs': 'a838a46ccd81d5b61d623deb7d966d0bba0b4f3be4d32222d44cdad87ce002f1',
    'handler-time-callers-Assembly-CSharp.dll.txt': '0966c8d8ad02ba311c8a57aa0821c29eea9618225f4ce085275a542d71612155',
    'handler-time-callers-Assembly-CSharp-firstpass.dll.txt': 'ff43aa796f741cd51b0390e8de9552d0e9798cab70174294e77ea93f7000238d',
    'SharedDevelopmentContext.cs': 'f9ba8f0ad1fd22db3de9e8f63ef07d5125d748a3f7a0c87c9ff98c4fbc0ca0c1',
    'SCPlayerInfo_0x10.cs': 'ff0a881c043b98b78f8839f398f981549676a583f945c1d6dbf8f6115cde7be8',
    'db_character.cs': '3a36f0abe69fca80bab7adddc6e3ce98d2a869ea64dd232bed9b4dce051c73e2',
    'db_character_equipment.cs': '1b2d7b2de399bff50870b7f8b9c5717327aaa3b71e36cc85adc1b64b4c996c69',
    'frozen-PlayerStateRefreshModel.cs': '7e0d4e369e7d00fa2d91d93f632c31afa5687c8fc17c29aa8f69116a8d155091',
    'reviewed-current-PlayerStateRefreshModel.cs': '4a2b0d7d4528b5503d4ee0c0069e8b7166f4badfc77462314583ea16ce13af6f',
    'reviewed-current-SharedDevelopmentContext.cs': '1e9f0c20b9f80713a9ad100d08945b231e75c67a104593a199b2ec088e006791',
}
HANDLER_PROOF = {
    'ack_utc': '2026-10-31T04:54:20.3252388Z',
    'ack_state_utc': '2026-10-31T04:54:20.3288454Z',
    'absence_by_terminal_utc': '2026-10-31T04:54:20.7242502Z',
    'ack_frame': 1078788, 'absence_by_frame': 1078800,
    'ack_to_absence_upper_bound_seconds': 0.3990114,
    'state_to_absence_upper_bound_seconds': 0.3954048, 'frame_difference': 12,
    'same_exported_process_identity': True, 'same_exported_current_root': True,
    'own_handler_reason': 'own_request_handler_observed',
    'absent_reason': 'response_not_captured_before_normal_handler',
    'timing_premises_not_measured': ['UTC no backward jump', 'normal Unity deltaTime/timeScale <=3',
        'one normal RootUI.Update per frame', 'no external dictionary/life/clock manipulation'],
    'correlation_premise_not_decoded': 'no stale or foreign same-opcode1104 response',
    'ack_to_absence_upper_bound_100ns_ticks': 3990114,
    'state_to_absence_upper_bound_100ns_ticks': 3954048,
}


def _need(value, reason):
    if not value:
        raise ResolutionError(reason)


def _same(a, b, reason):
    _need(canonical_bytes(a) == canonical_bytes(b), reason)


def _dict(value):
    _need(type(value) is dict, 'expected object')
    return value


def _int(value, low=0, high=2**63 - 1):
    _need(type(value) is int and low <= value <= high, 'integer out of bounds')
    return value


def _id(value):
    _need(type(value) is str and re.fullmatch('[0-9a-f]{32}', value) is not None, 'invalid canonical ID')
    return value


def _json(raw):
    _need(type(raw) is bytes and 0 < len(raw) <= MAX_SOURCE_BYTES, 'source size/type')
    return _dict(load_json_bytes(raw))


def _hash(raw):
    return hashlib.sha256(raw).hexdigest()


def _source(raw):
    _json(raw)
    return {'sha256': _hash(raw), 'raw_base64': base64.b64encode(raw).decode('ascii')}


def _read_source(source):
    _need(type(source) is dict and set(source) == {'sha256', 'raw_base64'}, 'source schema')
    raw = _base64(source['raw_base64'], MAX_SOURCE_BYTES)
    _same(_hash(raw), source['sha256'], 'source hash mismatch')
    _json(raw)
    return raw


def _base64(value, limit):
    _need(type(value) is str and 0 < len(value) <= ((limit + 2) // 3) * 4, 'base64 bound')
    try:
        raw = base64.b64decode(value, validate=True)
    except ValueError as exc:
        raise ResolutionError('invalid base64') from exc
    _need(0 < len(raw) <= limit, 'decoded bound')
    _same(base64.b64encode(raw).decode('ascii'), value, 'noncanonical base64')
    return raw


class _Reader:
    def __init__(self, raw):
        self.raw, self.pos = raw, 0

    def varint(self):
        result = 0
        for shift in range(0, 70, 7):
            _need(self.pos < len(self.raw), 'truncated varint')
            byte = self.raw[self.pos]
            self.pos += 1
            _need(shift != 63 or byte <= 1, 'varint overflow')
            result |= (byte & 127) << shift
            if byte < 128:
                return result
        raise ResolutionError('varint overflow')

    def take(self, count):
        _need(count <= len(self.raw) - self.pos, 'truncated protobuf length')
        part = self.raw[self.pos:self.pos + count]
        self.pos += count
        return part

    def fields(self):
        while self.pos < len(self.raw):
            tag = self.varint()
            number, wire = tag >> 3, tag & 7
            _need(1 <= number <= 536870911, 'invalid protobuf tag')
            if wire == 0:
                value = self.varint()
            elif wire == 2:
                value = self.take(self.varint())
            elif wire in (1, 5):
                value = self.take(8 if wire == 1 else 4)
            else:
                raise ResolutionError('unsupported protobuf wire')
            yield number, wire, value


def _selected(raw, selected, *, repeated=()):
    result = {}
    for field, wire, value in _Reader(raw).fields():
        if field not in selected:
            continue
        _need(wire == selected[field], 'wrong scalar wire')
        if field in repeated:
            result.setdefault(field, []).append(value)
            _need(len(result[field]) <= 255, 'equipment count bound')
        else:
            _need(field not in result, 'duplicate protobuf field')
            result[field] = value
    return result


def _zig(value, bits):
    _int(value, 0, 2**bits - 1)
    return (value >> 1) ^ -(value & 1)


def decode_sc16_projection(raw: bytes) -> dict:
    """Independent bounded protobuf projection of pinned server fields only."""
    _need(type(raw) is bytes and 0 < len(raw) <= 1048576, 'SC16 packet bound')
    top = _selected(raw, {3: 2, 8: 0, 13: 2}, repeated=(13,))
    _need(3 in top and 8 in top and top.get(13), 'missing SC16 required fields')
    player = _selected(top[3], {1: 0, 23: 0, 33: 0, 270: 0})
    role = _int(player.get(1), 1, 2**64 - 1)
    money = _zig(player.get(23, 0), 32) + _zig(player.get(270, 0), 64)
    stone = _zig(player.get(33, 0), 32)
    _int(money)
    _int(stone)
    equipments, seen = [], set()
    for raw_equipment in top[13]:
        e = _selected(raw_equipment, {1: 0, 2: 0, 3: 0, 4: 0, 6: 0, 7: 0})
        equipment = _int(e.get(1), 1, 255)
        _need(equipment not in seen, 'duplicate equipment ID')
        seen.add(equipment)
        equipments.append({'equipment_id': equipment, 'main_id': _int(e.get(2), 1, 255),
            'level': _int(e.get(6, 0), 0, 255), 'owner_id': _int(e.get(7, 0), 0, 65535),
            'accessory1_id': _int(e.get(3, 0), 0, 255), 'accessory2_id': _int(e.get(4, 0), 0, 255),
            'level_present': 6 in e, 'owner_present': 7 in e,
            'accessory1_present': 3 in e, 'accessory2_present': 4 in e})
    return {'character_id': str(role), 'current_formation': _int(top[8], 0, 2**31 - 1),
        'equipments': equipments, 'money': money, 'stone': stone,
        'money_present': 23 in player, 'money64_present': 270 in player, 'stone_present': 33 in player}


def business_fact_hash(fact):
    _dict(fact)
    _id(fact.get('session_id'))
    _dict(fact.get('business'))
    return _hash(canonical_bytes({'session_id': fact['session_id'], 'business': fact['business']}))


def _header(state, session, *, stopped=False):
    for key, value in {'protocol': 3, 'bridge_id': 'jiangshen-assistant-win32-v1', 'pid': PID,
            'process_creation_filetime': CREATION, 'nonce': session, 'session_id': session,
            'main_thread_verified': not stopped, 'managed_tid': 1}.items():
        _same(state.get(key), value, 'native identity mismatch: ' + key)
    _int(state.get('verified_native_tid'), 1)
    _need(re.fullmatch('JSAssistantBridge_[0-9a-f]{16}', state.get('assembly', '')) is not None, 'assembly identity')
    _int(state.get('frame'))
    _same(state.get('development', {}).get('character_id'), CHARACTER, 'development character mismatch')
    _same(state.get('campaign', {}).get('character_id'), CHARACTER, 'campaign character mismatch')


def _candidate(equipment):
    candidates = equipment.get('candidates')
    _need(type(candidates) is list and 1 <= len(candidates) <= 255, 'candidate count')
    ids = [_int(_dict(x).get('equipment_id'), 1, 255) for x in candidates]
    _need(len(ids) == len(set(ids)), 'duplicate candidate')
    matches = [x for x in candidates if x['equipment_id'] == 1]
    _need(len(matches) == 1, 'missing target')
    target = matches[0]
    for key, value in {'owner_id': 22, 'slot': 0, 'main_id': 1,
            'owner_slot_matches': True, 'in_current_formation': True}.items():
        _same(target.get(key), value, 'target association mismatch: ' + key)
    _int(target.get('level'), 0, 255)
    return target


def _pending(development, *, original):
    _need(set(development) == {'schema', 'revision', 'character_id', 'gate', 'reason',
        'growth', 'equipment', 'spirit', 'last_command'}, 'unexpected development capability')
    _same(development.get('character_id'), CHARACTER, 'fact character mismatch')
    for name in ('growth', 'spirit'):
        op = _dict(development.get(name, {}).get('operation'))
        for key, value in {'status': 'IDLE', 'mutation_attempted': False, 'requests': 0, 'opcode': 0,
                           'operation_id': ''}.items():
            _same(op.get(key), value, 'other development operation pending')
    op = _dict(development.get('equipment', {}).get('operation'))
    values = {'status': 'UNKNOWN', 'reason': 'response_not_captured_before_normal_handler',
        'operation_id': ORIGINAL_OPERATION, 'opcode': 1104, 'action': 'equipment_enchant',
        'mode': 'enchant', 'target_id': 1, 'desired_level': 5, 'sequence': 4,
        'expected_revision': 2, 'requests': 1, 'mutation_attempted': True,
        'receipt_result': None, 'receipt_opcode': 0, 'server': 'UNVERIFIED',
        'server_progress_observed': False, 'result_semantics': 'UNVERIFIED'} if original else {
        'status': 'IDLE', 'mutation_attempted': False, 'requests': 0, 'opcode': 0, 'operation_id': ''}
    for key, value in values.items():
        _same(op.get(key), value, 'unsupported equipment pending: ' + key)


def _fact(fact, final_equipment):
    for key, value in {'session_id': ORIGINAL_SESSION, 'pid': PID, 'process_creation_filetime': CREATION,
                       'protocol': 3, 'character_id': CHARACTER}.items():
        _same(fact.get(key), value, 'fact origin mismatch: ' + key)
    _need(fact.get('prior_unresolved_business') is None, 'fact must be a flattened exact origin')
    business = _dict(fact.get('business'))
    _need(set(business) == {'attempt', 'character_id', 'development', 'mutation_attempted',
        'pending_attempt', 'pending_development', 'resource_rollback'}, 'unexpected business capability')
    for key, value in {'pending_attempt': False, 'pending_development': True,
                       'mutation_attempted': True, 'resource_rollback': False, 'character_id': ''}.items():
        _same(business.get(key), value, 'business mismatch: ' + key)
    _same(business['attempt'], {'attempt_id': '', 'campaign_id': 0, 'character_id': '', 'command_seq': 0,
        'evidence': 'not_started', 'outcome': 'UNKNOWN', 'report_id': '0', 'status': 'NONE', 'troop_id': 0},
        'campaign operation present')
    _pending(_dict(business['development']), original=True)
    _same(business['development']['equipment'], final_equipment, 'conflicting equipment fact')


def _validate(journal, original, refresh, facts, handler):
    _same(handler, HANDLER_PROOF, 'unsupported handler proof or timing premises')
    _header(original, ORIGINAL_SESSION, stopped=True)
    for key, value in {'lifecycle': 'stopped', 'component_destroyed': True,
        'ready': False, 'updated_utc': HANDLER_PROOF['absence_by_terminal_utc'], 'frame': HANDLER_PROOF['absence_by_frame']}.items():
        _same(original.get(key), value, 'original terminal anchor mismatch')
    _need(set(journal) == {'schema', 'module', 'operation_id', 'goal', 'context_key', 'unresolved', 'events'},
          'original journal schema')
    for key, value in {'schema': 1, 'module': 'equipment', 'operation_id': ORIGINAL_OPERATION,
        'goal': {'equipment_id': 1, 'desired_level': 5, 'max_requests': 5, 'max_money': 100000,
                 'max_stone': 1000, 'mode': 'enchant'}}.items():
        _same(journal.get(key), value, 'unsupported original journal')
    context = load_json_bytes(journal.get('context_key', '').encode('utf-8'))
    _same(context, {'character_id': CHARACTER, 'creation': CREATION, 'generation': 3,
        'nonce': ORIGINAL_SESSION, 'pid': PID}, 'journal origin mismatch')
    events = journal.get('events')
    _need(type(events) is list and [x.get('kind') for x in events] == ['created', 'intent', 'ack', 'result'],
          'extra or missing original writes')
    intent, wire = events[1], _dict(events[2].get('wire'))
    command = {'action': 'equipment_enchant', 'desired_level': 5, 'expected_revision': 2, 'max_food': 0,
        'max_money': 100000, 'max_requests': 5, 'max_stone': 1000, 'mode': 'enchant',
        'operation_id': ORIGINAL_OPERATION, 'target_id': 1}
    _same(journal.get('unresolved'), command, 'original unresolved changed')
    _same(intent.get('command'), command, 'intent changed')
    _same(intent.get('policy_state'), {'_before': [0], '_food': 0, '_last_revision': 2,
        '_money': 600, '_requests': 1, '_sequence': 1, '_stone': 0}, 'original cost/budget changed')
    _same(events[3].get('result', {}).get('reason'), 'receipt_unknown', 'original no longer UNKNOWN')
    _same(events[3].get('result', {}).get('status'), 'BLOCKED', 'original no longer blocked')
    before = _dict(intent.get('before'))
    _same(_candidate(before).get('level'), 0, 'before level mismatch')
    _same(_candidate(before).get('cost'), {'cost_status': 'OBSERVED', 'money': 600, 'stone': 0, 'up': 0}, 'original quote')
    ack, ackstate = _dict(wire.get('ack')), _dict(wire.get('state'))
    _header(ackstate, ORIGINAL_SESSION)
    for key in ('protocol', 'bridge_id', 'pid', 'process_creation_filetime', 'session_id', 'nonce',
                'assembly', 'managed_tid', 'verified_native_tid'):
        _same(ack.get(key), ackstate.get(key), 'ACK header mismatch')
        _same(ackstate.get(key), original.get(key), 'original helper identity changed')
    _same(ack.get('updated_utc'), HANDLER_PROOF['ack_utc'], 'ACK timestamp mismatch')
    _same(ack.get('status'), 'COMPLETED', 'ACK did not complete')
    _same(ack.get('command_name'), 'development-command', 'ACK command mismatch')
    _same(wire.get('command'), 'development-command', 'wire command mismatch')
    _same(wire.get('requested_command'), 'development-command', 'requested command mismatch')
    _same(ack.get('command_seq'), 4, 'ACK sequence mismatch')
    _same(wire.get('requested_seq'), 4, 'wire sequence mismatch')
    _same(ackstate.get('updated_utc'), HANDLER_PROOF['ack_state_utc'], 'ACK state timestamp')
    _same(ackstate.get('frame'), HANDLER_PROOF['ack_frame'], 'ACK frame mismatch')
    ae = _dict(ackstate['development'].get('equipment'))
    ao = _dict(ae.get('operation'))
    expected = copy.deepcopy(original['development']['equipment']['operation'])
    expected.update(status='WAITING', reason='own_request_handler_observed')
    _same(ao, expected, 'own immediate handler witness mismatch')
    _same(ack.get('result', {}).get('development', {}).get('equipment', {}).get('operation'), ao,
        'ACK does not positively witness own handler')
    _same(ae.get('ui', {}).get('sended'), True, 'own request not sent')
    _pending(original['development'], original=True)
    final = _dict(original['development']['equipment'])
    _same(_candidate(final).get('level'), 1, 'historical local progress mismatch')
    _same(final.get('ui', {}).get('sended'), False, 'original still sending')
    _same(before.get('available_money') - final.get('available_money'), 600, 'historical money mismatch')
    _same(before.get('available_stone'), final.get('available_stone'), 'historical stone mismatch')
    _same(ackstate.get('campaign', {}).get('ui_context', {}).get('current_root_instance_id'),
        original.get('campaign', {}).get('ui_context', {}).get('current_root_instance_id'), 'original root changed')
    _need(type(facts) is list and 1 <= len(facts) <= 64, 'fact count bound')
    hashes = []
    for fact in facts:
        _fact(_dict(fact), final)
        hashes.append(business_fact_hash(fact))
    _need(len(hashes) == len(set(hashes)), 'duplicate fact')
    # Distinct scalar business facts for this origin are conflicts, not alternate
    # receipts that may be hidden by metadata changes.
    _need(len(hashes) == 1, 'conflicting original business facts')
    session = _id(refresh.get('session_id'))
    _need(session != ORIGINAL_SESSION, 'refresh must use a new helper session')
    _header(refresh, session)
    _same(refresh.get('verified_native_tid'), original.get('verified_native_tid'), 'native main thread changed')
    for key, value in {'lifecycle': 'ready', 'ready': True, 'component_destroyed': False, 'gate': 'PASS'}.items():
        _same(refresh.get(key), value, 'refresh lifecycle mismatch')
    refresh_utc = datetime.fromisoformat(refresh['updated_utc'].replace('Z', '+00:00'))
    _need(refresh_utc.tzinfo is not None and refresh_utc.utcoffset().total_seconds() == 0, 'refresh timestamp must be UTC')
    _need(refresh_utc >
          datetime.fromisoformat(original['updated_utc'].replace('Z', '+00:00')), 'refresh predates original')
    dto = _dict(refresh.get('player_refresh'))
    _need(set(dto) == {'schema', 'status', 'reason', 'operation_id', 'sequence', 'expected_revision', 'character_id',
        'opcode', 'mutation_attempted', 'receipt_capture', 'receipt_hash', 'callback_returned', 'server_snapshot',
        'context_rebuilt', 'root_level', 'main_ready', 'normal_responses_idle', 'outbound_transport_completion'}, 'refresh DTO schema')
    for key, value in {'schema': 1, 'status': 'OBSERVED', 'reason': 'server_snapshot_and_rebuilt_context_observed',
        'character_id': CHARACTER, 'opcode': 16, 'mutation_attempted': True, 'callback_returned': True,
        'context_rebuilt': True, 'main_ready': True, 'normal_responses_idle': True,
        'outbound_transport_completion': 'UNVERIFIED'}.items():
        _same(dto.get(key), value, 'refresh proof incomplete: ' + key)
    _need(dto.get('root_level') in ('Campaign', 'Capital'), 'unsupported completed refresh root')
    refresh_op = _id(dto.get('operation_id'))
    _need(refresh_op != ORIGINAL_OPERATION, 'refresh operation reused')
    _int(dto.get('sequence'), 1)
    _int(dto.get('expected_revision'), 1)
    _need(refresh.get('last_command_seq', -1) >= dto['sequence'], 'refresh command absent')
    cap = _dict(dto.get('receipt_capture'))
    _need(set(cap) == {'encoding', 'bytes', 'data'}, 'capture schema')
    _same(cap['encoding'], 'base64', 'capture encoding')
    raw = _base64(cap['data'], 1048576)
    _same(cap['bytes'], len(raw), 'receipt length mismatch')
    _same(dto.get('receipt_hash'), _hash(raw), 'receipt hash mismatch')
    snapshot = decode_sc16_projection(raw)
    _same(dto.get('server_snapshot'), snapshot, 'native projection differs from independent SC16')
    _same(snapshot['character_id'], CHARACTER, 'SC16 account mismatch')
    target = next((x for x in snapshot['equipments'] if x['equipment_id'] == 1), None)
    _need(target is not None, 'SC16 target missing')
    for key, value in {'level_present': True, 'owner_present': True, 'owner_id': 22, 'main_id': 1,
                       'accessory1_id': 0, 'accessory2_id': 0, 'level': 1}.items():
        _same(target.get(key), value, 'unexpected refreshed target: ' + key)
    _pending(refresh['development'], original=False)
    current = _dict(refresh['development'].get('equipment'))
    candidate = _candidate(current)
    _same(candidate['level'], target['level'], 'fresh candidate does not match SC16')
    quote = _dict(candidate.get('cost'))
    _same(quote.get('cost_status'), 'OBSERVED', 'next quote unobserved')
    _int(quote.get('money'), 1)
    _int(quote.get('stone'))
    _int(quote.get('up'))
    _same(refresh['development'].get('gate'), 'PASS', 'fresh context blocked')
    _same(current.get('gate'), 'PASS', 'fresh equipment unsupported')
    business = _dict(refresh.get('business'))
    _same(business.get('pending_attempt'), False, 'concurrent campaign')
    _same(business.get('pending_development'), False, 'concurrent development/refresh')
    _same(business.get('pending_player_refresh'), False, 'concurrent player refresh')
    _same(business.get('resource_rollback'), False, 'resource rollback')
    _same(business.get('attempt', {}).get('status'), 'NONE', 'concurrent attempt')
    _same(business.get('attempt', {}).get('attempt_id'), '', 'concurrent attempt identity')
    _pending(_dict(business.get('development')), original=False)
    _same(business.get('player_refresh'), {'status': 'OBSERVED', 'operation_id': refresh_op,
        'sequence': dto['sequence']}, 'business refresh identity mismatch')
    campaign = _dict(refresh.get('campaign'))
    for key, value in {'phase': 'CAMPAIGN', 'gate': 'PASS', 'available': True, 'troop_id': 0}.items():
        _same(campaign.get(key), value, 'current campaign not stable')
    ui = refresh.get('campaign', {}).get('ui_context', {})
    for key, value in {'current_root_level': 'Campaign', 'current_root_active': True,
                       'global_input_enabled': True, 'candidates_truncated': False, 'selection_gate': 'PASS'}.items():
        _same(ui.get(key), value, 'fresh Campaign context mismatch')
    _int(ui.get('current_root_instance_id'), 1)
    return {'pid': PID, 'process_creation_filetime': CREATION, 'character_id': CHARACTER,
        'original_operation_id': ORIGINAL_OPERATION, 'original_session_id': ORIGINAL_SESSION,
        'refresh_operation_id': refresh_op, 'refresh_session_id': session, 'equipment_id': 1,
        'owner_id': 22, 'slot': 0, 'main_id': 1, 'before_level': 0, 'refreshed_level': 1,
        'desired_level': 5}, sorted(hashes)


def build_equipment_progress_resolution(journal_bytes, original_state_bytes, refresh_state_bytes,
                                        fact_values: list[dict], handler_proof: dict) -> dict:
    """Build only; no files, game methods, network, retries or guard writes."""
    try:
        return _build(journal_bytes, original_state_bytes, refresh_state_bytes, fact_values, handler_proof)
    except ResolutionError:
        raise
    except (KeyError, TypeError, ValueError, OverflowError, AttributeError, RecursionError) as exc:
        raise ResolutionError('malformed resolution/source') from exc


def _build(journal_bytes, original_state_bytes, refresh_state_bytes, fact_values, handler_proof):
    identity, hashes = _validate(_json(journal_bytes), _json(original_state_bytes), _json(refresh_state_bytes), fact_values, handler_proof)
    return {'schema': 1, 'disposition': 'equipment_progress_refreshed', 'original_outcome': 'UNKNOWN',
        'consumes_original_result': False, 'retries_original': False, 'resets_budget': False,
        'identity': identity, 'remaining_budget': {'max_requests': 4, 'max_money': 99400, 'max_stone': 1000},
        'consumed_budget': {'requests': 1, 'money': 600, 'stone': 0}, 'business_fact_hashes': hashes,
        'facts': copy.deepcopy(fact_values), 'handler_proof': copy.deepcopy(handler_proof),
        'source_anchors': copy.deepcopy(SOURCE_ANCHORS), 'sources': {
            'journal': _source(journal_bytes), 'original_state': _source(original_state_bytes),
            'refresh_state': _source(refresh_state_bytes)}}


def verify_equipment_progress_resolution(proof, *, journal_bytes=None, original_state_bytes=None,
                                         refresh_state_bytes=None) -> None:
    """Rebuild semantics and compare every field; optional bytes pin disk state."""
    try:
        _dict(proof)
        sources = _dict(proof.get('sources'))
        _need(set(sources) == {'journal', 'original_state', 'refresh_state'}, 'source keys')
        raws = [_read_source(sources[name]) for name in ('journal', 'original_state', 'refresh_state')]
        for raw, external in zip(raws, (journal_bytes, original_state_bytes, refresh_state_bytes)):
            if external is not None:
                _need(type(external) is bytes and raw == external, 'current source bytes changed')
        rebuilt = build_equipment_progress_resolution(*raws, proof.get('facts'), proof.get('handler_proof'))
        _same(proof, rebuilt, 'resolution differs from verified semantics')
    except (KeyError, TypeError, OverflowError, AttributeError, RecursionError) as exc:
        raise ResolutionError('malformed resolution/source') from exc


def resolved_development_business(fact, proof, *, pid, process_creation_filetime,
                                  journal_bytes, original_state_bytes, refresh_state_bytes) -> bool:
    """Disregard one exact fact only; all other chain facts retain their gates."""
    try:
        _same(pid, PID, 'wrong process')
        _same(process_creation_filetime, CREATION, 'wrong process epoch')
        _need(all(type(x) is bytes for x in (journal_bytes, original_state_bytes, refresh_state_bytes)), 'current sources required')
        verify_equipment_progress_resolution(proof, journal_bytes=journal_bytes,
            original_state_bytes=original_state_bytes, refresh_state_bytes=refresh_state_bytes)
        _fact(fact, _json(original_state_bytes)['development']['equipment'])
        return business_fact_hash(fact) in proof['business_fact_hashes']
    except (ValueError, KeyError, TypeError, OverflowError, AttributeError, RecursionError):
        return False
