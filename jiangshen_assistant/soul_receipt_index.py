"""Build a source-linked index from explicitly selected, closed actual ledgers.

No directory discovery, persistence, game access, model invocation or learning
occurs here.  Historical evidence never supplies current action authority.
Unknown payoff coverage and structural discrepancies remain visible.
"""
from __future__ import annotations

from collections import defaultdict
from collections.abc import Mapping
from copy import deepcopy
from hashlib import sha256
import json
from pathlib import Path

from .soul_chain_rules import _board, analyze_chain


def _integer(value, minimum=0):
    return type(value) is int and value >= minimum


def _award(value):
    if not isinstance(value, (list, tuple)) or len(value) != 4 or any(
            not _integer(n) for n in value):
        raise ValueError('award_invalid')
    return list(value)


def _fingerprint(value):
    return sha256(json.dumps(value, sort_keys=True, separators=(',', ':'),
                             ensure_ascii=False).encode('utf-8')).hexdigest()


def _slot_payoffs(raw_round, terminal):
    board, slots = raw_round.get('harvest_before_board'), raw_round.get('harvest_slot_awards')
    if board is None or slots is None:
        return [], ['harvest_slot_payoff_evidence_missing']
    board = _board(board)
    if board != terminal:
        raise ValueError('harvest_board_not_terminal_growth_board')
    if not isinstance(slots, (list, tuple)) or len(slots) != 8:
        raise ValueError('harvest_slot_awards_invalid')
    total, pairs = [0] * 4, []
    for index, ((color, level), slot) in enumerate(zip(board, slots), 1):
        if isinstance(slot, Mapping):
            award_color, quantity = slot.get('color'), slot.get('number')
        elif isinstance(slot, (list, tuple)) and len(slot) == 2:
            award_color, quantity = slot
        else:
            raise ValueError('harvest_slot_award_invalid')
        if (type(award_color) is not int or award_color != color or not _integer(quantity)
                or (color == 0 and quantity != 0)):
            raise ValueError('harvest_slot_color_or_quantity_mismatch')
        if color:
            total[color - 1] += quantity
            pairs.append({'slot': index, 'color': color, 'level': level, 'quantity': quantity})
    if total != _award(raw_round.get('award')):
        raise ValueError('harvest_slot_total_mismatch')
    sequence = raw_round.get('harvest_evidence_sequence')
    if sequence is not None and sequence != raw_round['settle_sequence']:
        raise ValueError('harvest_evidence_sequence_mismatch')
    return pairs, []


def _round(raw_round, ledger, source):
    if not isinstance(raw_round, Mapping):
        raise ValueError('round_not_object')
    number = raw_round.get('round')
    if (not _integer(number, 1) or not _integer(raw_round.get('mode'), 1)
            or raw_round.get('start_verified') is not True
            or raw_round.get('harvest_verified') is not True
            or not _integer(raw_round.get('start_cost'))
            or not _integer(raw_round.get('coins_spent'))
            or not _integer(raw_round.get('growth_count'))
            or not _integer(raw_round.get('start_sequence'), 1)
            or not _integer(raw_round.get('settle_sequence'), raw_round['start_sequence'] + 1)
            or type(raw_round.get('auto_harvest')) is not bool):
        raise ValueError('round_start_harvest_sequence_or_cost_unverified')
    operation, character = ledger['operation_id'], ledger['character_id']
    rid = f'{operation}:r{number}'
    normalized = {
        'id': rid, 'round_id': rid, 'operation_id': operation, 'round': number,
        'character_id': character, 'cycle': ledger.get('cycle'), 'mode': raw_round['mode'],
        'gather_color': raw_round.get('color'), 'start_cost': raw_round['start_cost'],
        'coins_spent': raw_round['coins_spent'], 'growth_count': raw_round['growth_count'],
        'start_sequence': raw_round['start_sequence'], 'settle_sequence': raw_round['settle_sequence'],
        'award': _award(raw_round.get('award')), 'auto_harvest': raw_round['auto_harvest'],
        'source': source, 'source_paths': [source['path']], 'evidence_warnings': []}
    initial = _board(raw_round.get('board_before_growth'))
    normalized['board_before_growth'] = initial
    growths = raw_round.get('growths', [])
    global_growths = ledger.get('growths', [])
    if not isinstance(growths, (list, tuple)) or not isinstance(global_growths, (list, tuple)):
        raise ValueError('growth_collection_invalid')
    # Coordinator ledgers can repeat the same growth under both containers.
    combined = list(growths) + [g for g in global_growths
                                if isinstance(g, Mapping) and g.get('round') == number]
    by_sequence = {}
    for growth in combined:
        if not isinstance(growth, Mapping) or not _integer(growth.get('sequence'), 1):
            raise ValueError('growth_sequence_missing')
        sequence = growth['sequence']
        if sequence in by_sequence and growth != by_sequence[sequence]:
            raise ValueError('growth_receipt_id_conflict')
        by_sequence[sequence] = growth
    if len(by_sequence) != raw_round['growth_count']:
        raise ValueError('round_growth_detail_incomplete')
    transitions, previous, previous_sequence = [], initial, raw_round['start_sequence']
    for growth_count, growth in enumerate(sorted(by_sequence.values(), key=lambda g: g['sequence'])):
        sequence = growth['sequence']
        if (growth.get('round') != number or sequence <= previous_sequence
                or sequence > raw_round['settle_sequence']
                or type(growth.get('growth_before')) is not int
                or growth['growth_before'] != growth_count
                or type(growth.get('growth_after')) is not int
                or growth['growth_after'] != growth_count + 1
                or not _integer(growth.get('quoted_cost'), 1)
                or not _integer(growth.get('coins_before'))
                or not _integer(growth.get('coins_after'))
                or growth['coins_before'] - growth['coins_after'] != growth['quoted_cost']
                or growth.get('board_after_source') != 'decoded_response'
                or type(growth.get('auto_harvest')) is not bool):
            raise ValueError('growth_operation_sequence_cost_or_response_unverified')
        before, after = _board(growth.get('board_before')), _board(growth.get('board_after'))
        steps = growth.get('growth_steps')
        if not isinstance(steps, (list, tuple)) or not steps:
            raise ValueError('growth_decoded_steps_missing')
        steps = [_board(step) for step in steps]
        if before != previous or after != steps[-1]:
            raise ValueError('growth_board_continuity_or_terminal_mismatch')
        actual = None
        if growth['auto_harvest']:
            actual = _award(growth.get('award'))
            if (not raw_round['auto_harvest'] or sequence != raw_round['settle_sequence']
                    or actual != normalized['award'] or growth_count + 1 != raw_round['growth_count']):
                raise ValueError('growth_auto_harvest_settlement_mismatch')
        elif sequence >= raw_round['settle_sequence']:
            raise ValueError('manual_growth_not_before_harvest_sequence')
        transitions.append({
            'id': f'{rid}:s{sequence}', 'round_id': rid, 'operation_id': operation,
            'character_id': character, 'mode': raw_round['mode'], 'round': number,
            'sequence': sequence, 'source': source, 'source_paths': [source['path']],
            'growth_count': growth_count, 'growth_cost': growth['quoted_cost'],
            'start_cost': raw_round['start_cost'], 'coins_before': growth['coins_before'],
            'coins_after': growth['coins_after'], 'before': before, 'after': after, 'steps': steps,
            'board_before': before, 'board_after': after, 'growth_steps': steps,
            'auto_harvest': growth['auto_harvest'], 'actual_award': actual,
            'actual_round_award': normalized['award'], 'settle_sequence': raw_round['settle_sequence']})
        previous, previous_sequence = after, sequence
    if raw_round['auto_harvest'] and (not transitions or not transitions[-1]['auto_harvest']):
        raise ValueError('auto_harvest_growth_evidence_missing')
    if (raw_round['start_cost'] + sum(t['growth_cost'] for t in transitions)
            != raw_round['coins_spent']):
        raise ValueError('round_costs_do_not_reconcile')
    pairs, warnings = _slot_payoffs(raw_round, previous)
    normalized.update(terminal_board=previous, payoff_pairs=pairs,
                      harvest_before_board=deepcopy(raw_round.get('harvest_before_board')),
                      harvest_slot_awards=deepcopy(raw_round.get('harvest_slot_awards')),
                      harvest_evidence_sequence=raw_round.get('harvest_evidence_sequence'),
                      growth_costs=[t['growth_cost'] for t in transitions], evidence_warnings=warnings)
    return normalized, transitions


def build_receipt_index(paths, character_id=None):
    """Read only the supplied raw ledger files and return a JSON-safe report.

    payoff_by_mode is {character_id: {mode: {color: {level: quantity}}}} with
    string dictionary keys.  Mapping conflicts omit the disputed pair.  Economic
    groups additionally separate start cost and observed growth-cost context.
    Exclusions explain missing, UNKNOWN, incomplete or conflicting evidence.
    Conforming chain structure does not verify selection probabilities.
    """
    if isinstance(paths, (str, Path)):
        paths = [paths]
    report = {'schema': 'soul-verified-receipt-index-v1', 'status': 'BUILT',
              'character_id_filter': character_id, 'sources': [], 'verified_rounds': [],
              'transitions': [], 'payoff_by_mode': {}, 'payoff_evidence': [],
              'payoff_conflicts': [], 'economic_groups': [], 'exclusions': [],
              'duplicates': [], 'structural_discrepancies': [], 'counts': {}}
    operations = defaultdict(list)
    for value in paths:
        path = Path(value).expanduser().resolve()
        source = {'path': str(path), 'sha256': None}
        report['sources'].append(source)
        try:
            body = path.read_bytes()
            source['sha256'] = sha256(body).hexdigest()
            raw = json.loads(body.decode('utf-8-sig'))
            if (not isinstance(raw, Mapping) or raw.get('status') != 'COMPLETED'
                    or raw.get('cleanup') != 'VERIFIED_CLOSED'
                    or raw.get('pending_action') is not None or raw.get('evidence_error')
                    or not isinstance(raw.get('operation_id'), str) or not raw['operation_id']
                    or not isinstance(raw.get('character_id'), str) or not raw['character_id']):
                raise ValueError('ledger_not_completed_closed_or_evidence_invalid')
            source.update(operation_id=raw['operation_id'], character_id=raw['character_id'])
            if character_id is not None and raw['character_id'] != character_id:
                raise ValueError('character_id_filter_mismatch')
            rounds = raw.get('rounds')
            if not isinstance(rounds, list) or not rounds:
                raise ValueError('completed_rounds_missing')
            if (not _integer(raw.get('coins_spent'))
                    or any(not isinstance(r, Mapping) or not _integer(r.get('coins_spent')) for r in rounds)
                    or sum(r['coins_spent'] for r in rounds) != raw['coins_spent']):
                raise ValueError('ledger_round_costs_do_not_reconcile')
            numbers = [r.get('round') for r in rounds]
            if any(not _integer(n, 1) for n in numbers) or len(set(numbers)) != len(numbers):
                raise ValueError('duplicate_or_invalid_round_number')
            signature = _fingerprint({k: raw.get(k) for k in
                                      ('character_id', 'cycle', 'rounds', 'growths', 'coins_spent')})
            operations[raw['operation_id']].append((signature, source, raw))
        except (OSError, ValueError, TypeError) as exc:
            source['admission'] = 'EXCLUDED'
            report['exclusions'].append({'scope': 'source', 'source': source, 'reason': str(exc)})
    for operation, entries in operations.items():
        if len({entry[0] for entry in entries}) != 1:
            for _, source, _ in entries:
                source['admission'] = 'EXCLUDED'
                report['exclusions'].append({'scope': 'operation', 'operation_id': operation,
                    'source': source, 'reason': 'operation_receipt_id_conflict'})
            continue
        _, source, raw = entries[0]
        source_paths = sorted({entry[1]['path'] for entry in entries})
        for _, duplicate, _ in entries[1:]:
            duplicate['admission'] = 'DUPLICATE'
            report['duplicates'].append({'operation_id': operation, 'source': duplicate,
                                         'retained_source': source})
        source['admission'] = 'INDEXED'
        for raw_round in raw['rounds']:
            try:
                rd, transitions = _round(raw_round, raw, source)
                rd['source_paths'] = source_paths
                for transition in transitions:
                    transition['source_paths'] = source_paths
                report['verified_rounds'].append(rd)
                report['transitions'].extend(transitions)
            except (ValueError, TypeError, KeyError) as exc:
                report['exclusions'].append({'scope': 'round', 'operation_id': operation,
                    'round': raw_round.get('round'), 'source': source, 'reason': str(exc)})
    observed = defaultdict(lambda: defaultdict(list))
    groups = {}
    for rd in report['verified_rounds']:
        for pair in rd['payoff_pairs']:
            key = (rd['character_id'], rd['mode'], pair['color'], pair['level'])
            observed[key][pair['quantity']].append({'round_id': rd['id'],
                'slot': pair['slot'], 'settle_sequence': rd['settle_sequence'], 'source': rd['source']})
        costs = tuple(sorted(set(rd['growth_costs'])))
        key = (rd['character_id'], rd['mode'], rd['start_cost'], costs)
        if key not in groups:
            groups[key] = {'character_id': key[0], 'mode': key[1], 'start_cost': key[2],
                'observed_growth_costs': list(costs), 'round_ids': [], 'rounds': 0,
                'growth_requests': 0, 'coins_spent': 0,
                'actual_reward_by_color': {str(c): 0 for c in range(1, 5)}}
        group = groups[key]
        group['round_ids'].append(rd['id'])
        group['rounds'] += 1
        group['growth_requests'] += rd['growth_count']
        group['coins_spent'] += rd['coins_spent']
        for color, amount in enumerate(rd['award'], 1):
            group['actual_reward_by_color'][str(color)] += amount
    for (character, mode, color, level), quantities in sorted(observed.items()):
        row = {'character_id': character, 'mode': mode, 'color': color, 'level': level,
               'observed_quantities': sorted(quantities),
               'evidence': [dict(quantity=n, **e) for n, rows in sorted(quantities.items()) for e in rows]}
        report['payoff_evidence'].append(row)
        if len(quantities) != 1:
            report['payoff_conflicts'].append(row)
        else:
            mapping = report['payoff_by_mode'].setdefault(character, {}).setdefault(str(mode), {})
            mapping.setdefault(str(color), {})[str(level)] = next(iter(quantities))
    for transition in report['transitions']:
        mapping = report['payoff_by_mode'].get(transition['character_id'], {}).get(str(transition['mode']), {})
        transition['chain'] = analyze_chain(transition['before'], transition['steps'], payoff=mapping)
        if transition['chain']['status'] != 'VERIFIED':
            report['structural_discrepancies'].append({'transition_id': transition['id'],
                'source': transition['source'], 'violations': transition['chain']['violations']})
    report['economic_groups'] = [groups[key] for key in sorted(groups)]
    report['verified_rounds'].sort(key=lambda r: (r['character_id'], r['operation_id'], r['round']))
    report['transitions'].sort(key=lambda t: (t['character_id'], t['operation_id'], t['round'], t['sequence']))
    report['counts'] = {'sources': len(report['sources']),
        'indexed_sources': sum(s.get('admission') == 'INDEXED' for s in report['sources']),
        'duplicates': len(report['duplicates']), 'verified_rounds': len(report['verified_rounds']),
        'verified_growth_requests': len(report['transitions']),
        'two_step_requests': sum(len(t['steps']) == 2 for t in report['transitions']),
        'exclusions': len(report['exclusions']), 'payoff_conflicts': len(report['payoff_conflicts']),
        'structural_discrepancies': len(report['structural_discrepancies'])}
    report['limitations'] = [
        'Only explicitly selected raw ledgers were read; no application directory discovery.',
        'Decoded response steps are historical evidence, not independently preserved wire bytes.',
        'Structural conformity supplies neither victim probabilities nor current action authority.',
        'Missing or conflicting slot payoffs remain unknown; actual harvest awards are preserved.']
    return report
