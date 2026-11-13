"""Descriptive decomposition of a complete, ordered soul growth response.

The secondary-chain rule is an evidence-backed structural candidate, not a
probability law or action authority.  Rewards require a caller-supplied verified
mapping for the applicable character and gathering mode.  Slot numbers are 1-based.
"""
from __future__ import annotations

from collections.abc import Mapping
from copy import deepcopy


COLORS = (1, 2, 3, 4)


def _board(value):
    if not isinstance(value, (list, tuple)) or len(value) != 8:
        raise ValueError('board_must_have_eight_ordered_slots')
    result = []
    for slot in value:
        if isinstance(slot, Mapping):
            color, level = slot.get('color'), slot.get('level')
        elif isinstance(slot, (list, tuple)) and len(slot) == 2:
            color, level = slot
        else:
            raise ValueError('invalid_board_slot')
        if (type(color) is not int or not 0 <= color <= 4 or type(level) is not int
                or not 0 <= level <= 10 or (color == 0) != (level == 0)):
            raise ValueError('invalid_board_color_or_level')
        result.append([color, level])
    return result


def decompose_first_step(before, first_step):
    """Return deterministic secondary support without choosing a victim.

    This describes a supplied first response step.  It does not infer the first
    step, select a victim, assign probabilities, or attest response completeness.
    Invalid input returns UNVERIFIED with its original data preserved.
    """
    result = {'schema': 'soul-chain-first-step-v1', 'status': 'UNVERIFIED',
              'before': deepcopy(before), 'first_step': deepcopy(first_step),
              'first_reset_slots': [], 'k': None, 'tentative_board': None,
              'overflow_slots': [], 'possible_final_boards': [], 'violations': []}
    try:
        b0, b1 = _board(before), _board(first_step)
    except ValueError as exc:
        result['violations'].append(str(exc))
        return result
    result.update(before=b0, first_step=b1)
    reset = [i for i, (old, new) in enumerate(zip(b0, b1))
             if old[0] and old[1] > 1 and new[1] == 1]
    k = len(reset)
    tentative = [[c, 0 if not c else (1 if i in reset else min(10, level + k))]
                 for i, (c, level) in enumerate(b1)]
    overflow = [i for i, (c, level) in enumerate(b1)
                if c and i not in reset and level + k > 10]
    possibilities = []
    for victim in (overflow if overflow else [None]):
        board = deepcopy(tentative)
        if victim is not None:
            board[victim][1] = 1
        possibilities.append({'victim_slot': None if victim is None else victim + 1,
                              'board': board})
    result.update(first_reset_slots=[i + 1 for i in reset], k=k,
                  tentative_board=tentative, overflow_slots=[i + 1 for i in overflow],
                  possible_final_boards=possibilities)
    if not result['violations']:
        result['status'] = 'VERIFIED'
    return result


def _quantity(payoff, color, level):
    if color == 0:
        return 0
    if not isinstance(payoff, Mapping):
        return None
    value = payoff.get((color, level))
    if value is None:
        levels = payoff.get(color, payoff.get(str(color)))
        if isinstance(levels, Mapping):
            value = levels.get(level, levels.get(str(level)))
    return value if type(value) is int and value >= 0 else None


def _reward(board, payoff):
    totals = {str(color): 0 for color in COLORS}
    unknown = []
    for index, (color, level) in enumerate(board, 1):
        if not color:
            continue
        amount = _quantity(payoff, color, level)
        if amount is None:
            totals[str(color)] = None
            unknown.append({'slot': index, 'color': color, 'level': level})
        elif totals[str(color)] is not None:
            totals[str(color)] += amount
    return {'status': 'UNKNOWN' if unknown else 'VERIFIED_MAPPING',
            'by_color': totals, 'unknown': unknown}


def analyze_chain(before, steps, payoff=None):
    """Describe actual response steps and per-color reward support.

    ``payoff`` is {color: {level: quantity}}, with int or JSON string keys;
    {(color, level): quantity} is also accepted.  Missing values stay unknown.
    Structural failure never overwrites actual boards or an external actual
    harvest.  VERIFIED means conformity to the descriptive structural rule only.
    """
    result = {'schema': 'soul-chain-analysis-v1', 'status': 'UNVERIFIED',
              'before': deepcopy(before), 'steps': deepcopy(steps),
              'first_reset_slots': [], 'k': None, 'tentative_board': None,
              'overflow_slots': [], 'possible_final_boards': [],
              'chosen_victim_slots': [], 'first_step_transitions': [],
              'per_color_payoff_bounds': {}, 'actual_chosen_victim_loss': None,
              'violations': []}
    try:
        b0 = _board(before)
        if not isinstance(steps, (list, tuple)) or not steps:
            raise ValueError('decoded_growth_steps_missing')
        normalized = [_board(step) for step in steps]
    except ValueError as exc:
        result['violations'].append(str(exc))
        return result
    first = decompose_first_step(b0, normalized[0])
    for key in ('first_reset_slots', 'k', 'tentative_board', 'overflow_slots',
                'possible_final_boards'):
        result[key] = first[key]
    result.update(before=b0, steps=normalized, actual_final_board=normalized[-1])
    result['violations'].extend(first['violations'])
    for index, (old, new) in enumerate(zip(b0, normalized[0]), 1):
        result['first_step_transitions'].append({
            'slot': index, 'before': old, 'after': new,
            'reset': index in first['first_reset_slots'],
            'level_delta': new[1] - old[1]})
    expected_count = 2 if first['k'] else 1
    if len(normalized) != expected_count:
        result['violations'].append('step_count_vs_first_reset')
    if first['k'] and len(normalized) >= 2:
        second = normalized[1]
        tentative = first['tentative_board']
        chosen = [i + 1 for i, (a, b) in enumerate(zip(tentative, second))
                  if a != b and a[0] == b[0] and a[1] > 1 and b[1] == 1]
        result['chosen_victim_slots'] = chosen
        if len(chosen) != int(bool(first['overflow_slots'])) or any(
                slot not in first['overflow_slots'] for slot in chosen):
            result['violations'].append('secondary_reset_cardinality_or_membership')
        if any(a != b for i, (a, b) in enumerate(zip(tentative, second), 1)
               if i not in chosen):
            result['violations'].append('secondary_nonselected_value')
    possibilities = first['possible_final_boards']
    for candidate in possibilities:
        candidate['payoff'] = _reward(candidate['board'], payoff)
    for color in COLORS:
        values = [candidate['payoff']['by_color'][str(color)] for candidate in possibilities]
        known = bool(values) and all(value is not None for value in values)
        result['per_color_payoff_bounds'][str(color)] = {
            'status': 'VERIFIED_MAPPING' if known else 'UNKNOWN',
            'min': min(values) if known else None, 'max': max(values) if known else None}
    result['tentative_payoff'] = _reward(first['tentative_board'], payoff)
    result['actual_final_payoff_estimate'] = _reward(normalized[-1], payoff)
    if not result['violations']:
        loss = {str(color): 0 for color in COLORS}
        for slot in result['chosen_victim_slots']:
            color, level = first['tentative_board'][slot - 1]
            high, low = _quantity(payoff, color, level), _quantity(payoff, color, 1)
            loss[str(color)] = None if high is None or low is None else high - low
        result['actual_chosen_victim_loss'] = {
            'status': 'UNKNOWN' if any(v is None for v in loss.values()) else 'VERIFIED_MAPPING',
            'by_color': loss, 'victim_slots': result['chosen_victim_slots']}
        result['status'] = 'VERIFIED'
    return result
