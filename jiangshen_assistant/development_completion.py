"""Offline verification of archived equipment continuation evidence.

This legacy evidence format has no authority over task startup or admission.
It cannot reconstruct the original UNKNOWN response or establish current state.
"""
from __future__ import annotations

import copy
import hashlib

from .campaign_recovery import canonical_bytes
from .development_recovery import (
    ResolutionError, ORIGINAL_OPERATION, _dict, _id, _int, _json, _need,
    _read_source, _same, _source, verify_equipment_progress_resolution,
)


def _candidate(equipment):
    found = [c for c in equipment.get('candidates', []) if c.get('equipment_id') == 1]
    _need(len(found) == 1, 'completion target missing or duplicated')
    c = found[0]
    for key, value in {'owner_id': 22, 'slot': 0, 'main_id': 1,
                       'owner_slot_matches': True, 'in_current_formation': True}.items():
        _same(c.get(key), value, 'completion target ownership changed')
    return c


def build_equipment_completion(resolution, continuation_journal_bytes, final_state_bytes):
    """Validate a completed fresh operation and embed its exact positive evidence."""
    try:
        return _build(resolution, continuation_journal_bytes, final_state_bytes)
    except ResolutionError:
        raise
    except (KeyError, TypeError, ValueError, AttributeError, OverflowError, RecursionError) as exc:
        raise ResolutionError('malformed equipment completion') from exc


def _build(resolution, journal_bytes, final_bytes):
    verify_equipment_progress_resolution(resolution)
    journal, final = _json(journal_bytes), _json(final_bytes)
    refresh = _json(_read_source(resolution['sources']['refresh_state']))
    proof_hash = hashlib.sha256(canonical_bytes(resolution)).hexdigest()
    _same(journal.get('schema'), 1, 'continuation journal schema')
    _same(journal.get('module'), 'equipment', 'continuation module')
    op_id = _id(journal.get('operation_id'))
    _need(op_id not in (ORIGINAL_OPERATION, resolution['identity']['refresh_operation_id']), 'operation identity reused')
    _same(journal.get('recoveredFromProofHash'), proof_hash, 'continuation resolution hash')
    _same(journal.get('goal'), {'equipment_id': 1, 'desired_level': 5, 'mode': 'enchant',
        **resolution['remaining_budget']}, 'continuation goal or budget reset')
    _same(journal.get('unresolved'), None, 'continuation unresolved')
    # Match complete pairs in journal order. No missing ACK, observation, or
    # settlement may be replaced by the final scalar level.
    events = journal.get('events')
    _need(type(events) is list and 1 <= len(events) <= 2048, 'continuation event bound')
    pending = None
    count = money = stone = 0
    last_sequence = 0
    last_revision = 0
    positive = []
    done = False
    last_settled = None
    for event in events:
        kind = event.get('kind')
        if kind == 'created':
            _need(event is events[0], 'duplicate created event')
        elif kind == 'intent':
            _need(pending is None and not done, 'overlapping continuation intent')
            command = _dict(event.get('command'))
            _same(command.get('operation_id'), op_id, 'foreign continuation command')
            for key, value in {'target_id': 1, 'desired_level': 5, 'mode': 'enchant',
                'max_food': 0, **resolution['remaining_budget']}.items():
                _same(command.get(key), value, 'continuation command changed: ' + key)
            revision = _int(command.get('expected_revision'), 1)
            _need(revision > last_revision, 'continuation revision not increasing')
            last_revision = revision
            action = command.get('action')
            _need(action in ('equipment_open', 'reinforce_open', 'enchant_tab',
                             'equipment_enchant', 'equipment_close'), 'unsupported continuation command')
            before = _dict(event.get('before'))
            c = _candidate(before)
            _same(c.get('level'), 1 + count, 'continuation level chain')
            if action == 'equipment_enchant':
                _need(count < 4, 'continuation request budget exceeded')
                quote = _dict(c.get('cost'))
                _same(quote.get('cost_status'), 'OBSERVED', 'continuation quote unobserved')
                _same(quote.get('up'), 0, 'continuation mode changed')
                money += _int(quote.get('money'))
                stone += _int(quote.get('stone'))
                count += 1
            state = _dict(event.get('policy_state'))
            for key, value in {'_requests': count, '_money': money, '_stone': stone, '_food': 0}.items():
                _same(state.get(key), value, 'continuation reservation changed')
            _need(money <= 99400 and stone <= 1000, 'continuation resource budget exceeded')
            pending = {'command': command, 'ack': None, 'observed': None}
        elif kind == 'ack':
            _need(pending is not None and pending['ack'] is None, 'orphan or duplicate ACK')
            wire = _dict(event.get('wire'))
            ack = _dict(wire.get('ack'))
            seq = _int(wire.get('requested_seq', wire.get('seq')), 1)
            _need(seq > last_sequence, 'continuation ACK sequence not increasing')
            last_sequence = seq
            _same(ack.get('command_seq'), seq, 'continuation ACK sequence')
            _same(ack.get('status'), 'COMPLETED', 'continuation invocation unconfirmed')
            receipt = ack.get('result', {}).get('development', {}).get('last_command', {})
            for key, value in {'sequence': seq, 'status': 'INVOKED', 'operation_id': op_id,
                               'action': pending['command']['action']}.items():
                _same(receipt.get(key), value, 'continuation ACK receipt mismatch')
            pending['ack'] = seq
        elif kind == 'observation':
            _need(pending is not None and pending['ack'] is not None and pending['observed'] is None,
                  'orphan or duplicate observation')
            equipment = _dict(event.get('observed'))
            op = _dict(equipment.get('operation'))
            for key in ('operation_id', 'expected_revision', 'action', 'target_id', 'desired_level', 'mode'):
                _same(op.get(key), pending['command'][key], 'continuation observation identity')
            _same(op.get('sequence'), pending['ack'], 'continuation observation sequence')
            _same(op.get('status'), 'OBSERVED', 'continuation observation not positive')
            c = _candidate(equipment)
            _same(c.get('level'), 1 + count, 'continuation observed level chain')
            if pending['command']['action'] == 'equipment_enchant':
                for key, value in {'receipt_opcode': 1104, 'receipt_id': 1,
                    'receipt_level': 1 + count, 'server_progress_observed': True}.items():
                    _same(op.get(key), value, 'continuation positive receipt missing')
                positive.append(copy.deepcopy(op))
            pending['observed'] = op
        elif kind == 'settled':
            _need(pending is not None and pending['observed'] is not None, 'unobserved settlement')
            last_settled = pending['observed']
            pending = None
        elif kind == 'result':
            result = event.get('result', {})
            _need(pending is None, 'result while pending')
            _same(result.get('status'), 'DONE', 'continuation did not finish')
            _same(result.get('evidence'), 'SERVER_PROGRESS_OBSERVED', 'continuation lacks own server progress')
            done = True
        else:
            raise ResolutionError('unexpected continuation event: ' + str(kind))
    _need(done and pending is None and count == 4 and len(positive) == 4, 'four own NEXT receipts required')
    for key in ('protocol', 'bridge_id', 'pid', 'process_creation_filetime', 'session_id', 'nonce',
                'assembly', 'managed_tid', 'verified_native_tid'):
        _same(final.get(key), refresh.get(key), 'completion helper identity changed')
    for key, value in {'ready': True, 'main_thread_verified': True, 'component_destroyed': False,
                       'lifecycle': 'ready', 'gate': 'PASS'}.items():
        _same(final.get(key), value, 'completion helper not ready')
    _same(final.get('player_refresh'), refresh.get('player_refresh'), 'completion refresh epoch changed')
    _need(_int(final.get('frame'), 1) > refresh['frame'], 'completion frame not advancing')
    development = _dict(final.get('development'))
    _same(development.get('character_id'), resolution['identity']['character_id'], 'completion account changed')
    _same(_candidate(development.get('equipment', {})).get('level'), 5, 'completion final target not met')
    _same(development.get('equipment', {}).get('operation'), last_settled,
          'completion final operation differs from own settled receipt')
    _need(_int(final.get('last_command_seq'), 1) >= last_sequence, 'completion final command absent')
    for module in ('equipment', 'spirit', 'growth'):
        _need(development.get(module, {}).get('operation', {}).get('status') in ('IDLE', 'OBSERVED'),
              'completion contains pending capability')
    business = _dict(final.get('business'))
    for key in ('pending_attempt', 'pending_development', 'pending_player_refresh', 'resource_rollback'):
        _same(business.get(key), False, 'completion business still pending')
    _same(business.get('development'), development, 'completion business development differs')
    _same(business.get('attempt', {}).get('status'), 'NONE', 'completion campaign attempt present')
    _same(business.get('attempt', {}).get('attempt_id'), '', 'completion campaign identity present')
    return {'schema': 1, 'disposition': 'equipment_goal_completed', 'original_outcome': 'UNKNOWN',
        'retries_original': False, 'resets_budget': False, 'resolution_sha256': proof_hash,
        'resolution': copy.deepcopy(resolution), 'continuation_operation_id': op_id,
        'identity': copy.deepcopy(resolution['identity']), 'completed_level': 5,
        'consumed_continuation_budget': {'requests': count, 'money': money, 'stone': stone},
        'positive_receipts': positive, 'sources': {'continuation_journal': _source(journal_bytes),
                                                 'final_state': _source(final_bytes)}}


def verify_equipment_completion(proof, *, resolution=None, continuation_journal_bytes=None,
                                final_state_bytes=None):
    """Rebuild the entire seal, optionally pinning all current canonical bytes."""
    try:
        _dict(proof)
        sources = _dict(proof.get('sources'))
        _need(set(sources) == {'continuation_journal', 'final_state'}, 'completion source keys')
        raws = [_read_source(sources[k]) for k in ('continuation_journal', 'final_state')]
        for raw, supplied in zip(raws, (continuation_journal_bytes, final_state_bytes)):
            if supplied is not None:
                _need(type(supplied) is bytes and raw == supplied, 'completion source bytes changed')
        if resolution is not None:
            _same(proof.get('resolution'), resolution, 'completion resolution changed')
        _same(proof, build_equipment_completion(proof.get('resolution'), *raws), 'completion semantics changed')
    except ResolutionError:
        raise
    except (KeyError, TypeError, ValueError, AttributeError, OverflowError, RecursionError) as exc:
        raise ResolutionError('malformed equipment completion') from exc
