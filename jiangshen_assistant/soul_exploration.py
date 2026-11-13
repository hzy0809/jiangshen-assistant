"""Receipt-only quotas and admission for bounded soul exploration.

The executor supplies verified paid-request receipts, never prepared decisions or
pending actions. A request's ordered internal steps identify a burst, but only
its full before/after delta consumes the loss allowance.
"""
from __future__ import annotations

from collections.abc import Mapping
from math import isfinite


EXPLORATION_REASONS = frozenset({
    'soul_joint_budgeted_exploration', 'soul_joint_post_burst_exploration',
    'soul_joint_frontier_exploration',
})
_TARGET_REASON = 'soul_joint_post_burst_exploration'
_FRONTIER_REASON = 'soul_joint_frontier_exploration'
_COVERAGE_REASONS = frozenset({
    'soul_joint_award_outside_observed_support',
    'soul_joint_ordered_color_context_uncovered',
    'soul_joint_board_neighborhood_uncovered',
    'soul_joint_nearest_board_too_distant',
    'soul_joint_independent_round_support_sparse',
})


def _field(value, name, default=None):
    return value.get(name, default) if isinstance(value, Mapping) else getattr(value, name, default)


def _number(value):
    return type(value) in (int, float) and isfinite(value)


def _integer(value, minimum=0):
    return type(value) is int and value >= minimum


def _board(value):
    if not isinstance(value, (list, tuple)) or len(value) != 8:
        return None
    result = []
    for slot in value:
        if not isinstance(slot, (list, tuple)) or len(slot) != 2:
            return None
        color, level = slot
        if (not _integer(color) or color > 4 or not _integer(level) or level > 10
                or (color == 0) != (level == 0)):
            return None
        result.append((color, level))
    return tuple(result)


def _pure_purple(board):
    return board is not None and all(color in (0, 2) for color, _ in board)


def _purple_value(model, board):
    if not _pure_purple(board) or model is None:
        return None
    try:
        # Use the same verified slot payoff mapping as the model assessment;
        # no historical receipt is used as authority for the current board.
        award, unknown = model._estimate(board)
    except (AttributeError, IndexError, KeyError, TypeError, ValueError):
        return None
    if (unknown or not isinstance(award, (list, tuple)) or len(award) != 4
            or any(not _number(value) or value < 0 for value in award)
            or any(award[index] != 0 for index in (0, 2, 3))):
        return None
    return award[1]


def _unique(receipts):
    """Do not charge duplicated top-level and per-round receipt copies twice."""
    unique, conflicts = {}, []
    for receipt in receipts:
        key = (_field(receipt, 'round'), _field(receipt, 'sequence'))
        if not all(_integer(value, 1) for value in key):
            conflicts.append({'round': key[0], 'sequence': key[1], 'reason': 'receipt_identity_invalid'})
            continue
        if key in unique and unique[key] != receipt:
            conflicts.append({'round': key[0], 'sequence': key[1], 'reason': 'receipt_duplicate_conflict'})
        else:
            unique[key] = receipt
    return [unique[key] for key in sorted(unique)], conflicts


def _request(receipt, model):
    before, after = _board(_field(receipt, 'board_before')), _board(_field(receipt, 'board_after'))
    raw_steps = _field(receipt, 'growth_steps')
    steps = tuple(_board(step) for step in raw_steps) if isinstance(raw_steps, (list, tuple)) else ()
    cost = _field(receipt, 'quoted_cost')
    coins_before, coins_after = _field(receipt, 'coins_before'), _field(receipt, 'coins_after')
    growth_before, growth_after = _field(receipt, 'growth_before'), _field(receipt, 'growth_after')
    paid_verified = (_integer(cost, 1) and _integer(coins_before) and _integer(coins_after)
                     and coins_before - coins_after == cost
                     and _integer(growth_before) and _integer(growth_after, 1)
                     and growth_after == growth_before + 1
                     and _integer(_field(receipt, 'revision_before'), 1)
                     and _integer(_field(receipt, 'revision_after'), 1)
                     and _field(receipt, 'revision_after') > _field(receipt, 'revision_before'))
    complete = (paid_verified and _field(receipt, 'board_after_source') == 'decoded_response'
                and before is not None and after is not None
                and 1 <= len(steps) <= 64 and all(step is not None for step in steps)
                and steps[-1] == after)
    before_value, after_value = _purple_value(model, before), _purple_value(model, after)
    after_source = 'frozen_model_verified_slot_payoff_estimate'
    award = _field(receipt, 'award')
    if (_field(receipt, 'auto_harvest') is True and isinstance(award, (list, tuple))
            and len(award) == 4 and all(_integer(value) for value in award)
            and all(award[index] == 0 for index in (0, 2, 3))):
        # Auto settlement may clear the visible board. Its verified award is
        # the final request value, as in JointSoulModel's receipt ingestion.
        after_value, after_source = award[1], 'verified_auto_harvest_award'
    delta = (after_value - before_value if paid_verified
             and before_value is not None and after_value is not None else None)
    bursts = []
    if complete and all(_pure_purple(step) for step in (before, *steps)):
        previous = before
        for index, step in enumerate(steps, 1):
            slots = [slot for slot, (old, new) in enumerate(zip(previous, step), 1)
                     if old[0] == new[0] == 2 and old[1] > 1 and new[1] == 1]
            if slots:
                bursts.append({'step': index, 'slots': slots})
            previous = step
    decision = _field(receipt, 'decision')
    reason = _field(decision, 'reason', '')
    return {
        'round': _field(receipt, 'round'), 'sequence': _field(receipt, 'sequence'),
        'reason': reason, 'exploration': reason in EXPLORATION_REASONS,
        'targeted': reason == _TARGET_REASON, 'frontier': reason == _FRONTIER_REASON,
        'paid_verified': paid_verified, 'complete_response': complete,
        'quoted_cost': cost, 'before_purple': before_value, 'after_purple': after_value,
        'after_value_source': after_source, 'net_delta_purple': delta, 'bursts': bursts,
    }


def post_burst_context(*, policy, board, growth_cost, joint_model, joint_value,
                       exploration_history=(), round_growths=(), hard_reason=''):
    """Return evidence and a gate; it cannot publish, reserve, or train anything."""
    receipts, conflicts = _unique((*exploration_history, *round_growths))
    local, local_conflicts = _unique(round_growths)
    requests = [_request(receipt, joint_model) for receipt in receipts]
    round_requests = [_request(receipt, joint_model) for receipt in local]
    explorations = [row for row in requests if row['exploration']]
    recoveries = [row for row in round_requests if row['targeted']]
    accounting_unknown = [{'round': row['round'], 'sequence': row['sequence']}
                          for row in explorations
                          if not row['paid_verified'] or row['net_delta_purple'] is None]
    spend = sum(row['quoted_cost'] for row in explorations if row['paid_verified'])
    # Gross losses are never offset by gains from another request.
    loss = sum(max(0, -row['net_delta_purple']) for row in explorations
               if row['net_delta_purple'] is not None)
    anchors = [row for row in round_requests if row['complete_response'] and row['bursts']
               and row['net_delta_purple'] is not None and row['net_delta_purple'] < 0]
    anchor = anchors[-1] if anchors else None
    failed_recovery = any(row['net_delta_purple'] is not None and row['net_delta_purple'] <= 0
                          for row in recoveries)
    model = joint_value or {}
    values = model.get('current_estimated_award_by_color') or {}
    current_known = (isinstance(values, Mapping) and all(
        _number(values.get(str(color))) and values[str(color)] >= 0 for color in range(1, 5)))
    current = values.get('2') if current_known and all(values[str(color)] == 0 for color in (1, 3, 4)) else None
    low_slots = sum(color == 2 and 1 <= level <= 3 for color, level in board)
    expected_values = model.get('expected_delta_by_color') or {}
    expected_purple = expected_values.get('2') if isinstance(expected_values, Mapping) else None
    opportunity_purple = model.get('opportunity_cost_purple')
    expected = model.get('expected_delta_utility')
    opportunity = model.get('opportunity_cost_utility')
    round_count = sum(row['exploration'] for row in round_requests)
    remaining_count = max(0, min(2, policy.explore_growths) - round_count)
    remaining_money = max(0, policy.exploration_money_limit - spend)
    remaining_loss = max(0, policy.exploration_loss_limit - loss)
    reason = (
        'recovery_not_improved' if failed_recovery else
        'hard_admission_reason' if hard_reason else
        'research_requires_purple_entry' if getattr(joint_model, 'mode', 2) != 2 else
        'model_grow' if model.get('action') == 'grow' else
        'model_not_coverage_only' if model.get('exploration_eligible') is not True
            or model.get('reason') not in _COVERAGE_REASONS else
        'receipt_accounting_unknown' if conflicts or local_conflicts or accounting_unknown else
        'round_request_limit' if remaining_count == 0 else
        'post_burst_anchor_absent' if anchor is None else
        'current_reward_unknown_or_not_pure_purple' if current is None or not _pure_purple(board) else
        'current_reward_above_limit' if current > 40 else
        'low_purple_slots_below_minimum' if low_slots < 3 else
        'opportunity_cost_unknown' if not _number(opportunity) or opportunity < 0 else
        'expected_delta_not_above_opportunity' if _number(expected) and expected <= opportunity else
        'expected_delta_unknown_after_first_recovery' if expected is None and recoveries else
        'expected_delta_invalid' if expected is not None and not _number(expected) else
        'growth_cost_unknown' if not _integer(growth_cost, 1) else
        'exploration_money_limit' if remaining_money < growth_cost else
        'exploration_loss_limit' if remaining_loss < current else
        'eligible'
    )
    return {
        'mode': 'post-burst-v1', 'eligible': reason == 'eligible', 'reason': reason,
        'recovery_not_improved': failed_recovery,
        'post_burst_anchor_sequence': anchor['sequence'] if anchor else None,
        'post_burst_anchor': anchor,
        'current_reward_exposure_purple': current, 'current_reward_limit_purple': 40,
        'low_purple_slot_count': low_slots, 'low_purple_slot_minimum': 3,
        'expected_delta_purple': expected_purple, 'opportunity_cost_purple': opportunity_purple,
        'expected_delta_utility': expected, 'opportunity_cost_utility': opportunity,
        'utility_version': model.get('utility_version'),
        'recovery_stop_scope': 'information_budget_only_not_a_veto_of_supported_profit_growth',
        'verified_round_exploration_requests': round_count,
        'verified_round_targeted_requests': len(recoveries),
        'exploration_requests_remaining': remaining_count,
        'verified_exploration_money_spent': spend,
        'verified_exploration_gross_loss_purple': loss,
        'exploration_money_limit': policy.exploration_money_limit,
        'exploration_loss_limit': policy.exploration_loss_limit,
        'exploration_money_remaining': remaining_money,
        'exploration_loss_remaining': remaining_loss,
        'previous_recovery_outcomes': recoveries,
        'unknown_exploration_outcomes': accounting_unknown,
        'receipt_conflicts': [*conflicts, *local_conflicts],
        'valuation': 'frozen_model_verified_slot_payoff_estimate',
        'accounting_unit': 'one_verified_paid_request_full_before_after_delta',
    }


def frontier_post_burst_context(*, policy, board, growth_count, growth_cost, joint_model,
                                joint_value, exploration_history=(), round_growths=(), hard_reason=''):
    """Keep v1 recovery gates and add one explicitly funded frontier request.

    A frontier request is a distinct sampling action, never a recovery outcome.
    Both kinds consume the same verified-request money, gross loss and round
    allowances; the controller also retains the global allowance across runs.
    """
    context = post_burst_context(policy=policy, board=board, growth_cost=growth_cost,
        joint_model=joint_model, joint_value=joint_value, exploration_history=exploration_history,
        round_growths=round_growths, hard_reason=hard_reason)
    receipts, conflicts = _unique((*exploration_history, *round_growths))
    local, local_conflicts = _unique(round_growths)
    requests = [_request(receipt, joint_model) for receipt in receipts]
    round_requests = [_request(receipt, joint_model) for receipt in local]

    def verified(row):
        return row['paid_verified'] and row['complete_response'] and row['net_delta_purple'] is not None

    unknown = [{'round': row['round'], 'sequence': row['sequence']} for row in requests if not verified(row)]
    explorations = [row for row in requests if row['exploration'] and verified(row)]
    local_explorations = [row for row in round_requests if row['exploration'] and verified(row)]
    frontiers = [row for row in explorations if row['frontier']]
    local_frontiers = [row for row in round_requests if row['frontier']]
    spend = sum(row['quoted_cost'] for row in explorations)
    loss = sum(max(0, -row['net_delta_purple']) for row in explorations)
    frontier_spend = sum(row['quoted_cost'] for row in frontiers)
    remaining_count = max(0, min(2, policy.explore_growths) - len(local_explorations))
    remaining_money = max(0, policy.exploration_money_limit - spend)
    remaining_loss = max(0, policy.exploration_loss_limit - loss)
    remaining_frontier = max(0, policy.frontier_money_limit - frontier_spend)
    accounting_unknown = bool(conflicts or local_conflicts or unknown)
    # In v2 an incomplete ordinary request could conceal a loss anchor, and an
    # incomplete exploration cannot establish any remaining shared allowance.
    # Either prevents both overrides without changing v1 admission semantics.
    if accounting_unknown:
        context.update(eligible=False, reason='receipt_accounting_unknown')
    context.update({
        'mode': 'frontier-post-burst-v2', 'post_burst_mode': 'post-burst-v1',
        'verified_round_exploration_requests': len(local_explorations),
        'verified_round_targeted_requests': sum(row['targeted'] for row in local_explorations),
        'exploration_requests_remaining': remaining_count,
        'verified_exploration_money_spent': spend,
        'verified_exploration_gross_loss_purple': loss,
        'exploration_money_remaining': remaining_money,
        'exploration_loss_remaining': remaining_loss,
        'unknown_exploration_outcomes': [
            {'round': row['round'], 'sequence': row['sequence']}
            for row in requests if row['exploration'] and not verified(row)],
        'unknown_request_outcomes': unknown,
    })
    model = joint_value or {}
    current = context['current_reward_exposure_purple']
    mapped_current = _purple_value(joint_model, board)
    coverage = model.get('coverage')
    support = coverage.get('purple_award_range') if isinstance(coverage, Mapping) else None
    support_known = (isinstance(support, (list, tuple)) and len(support) == 2
                     and all(_number(value) and value >= 0 for value in support)
                     and support[0] <= support[1])
    support_max = support[1] if support_known else None
    expected, opportunity = context['expected_delta_utility'], context['opportunity_cost_utility']
    reason = (
        'recovery_not_improved' if context['recovery_not_improved'] else
        'hard_admission_reason' if hard_reason else
        'research_requires_purple_entry' if getattr(joint_model, 'mode', 2) != 2 else
        'model_not_frontier_coverage_stop' if model.get('action') != 'harvest'
            or model.get('exploration_eligible') is not True
            or model.get('reason') != 'soul_joint_award_outside_observed_support' else
        'receipt_accounting_unknown' if accounting_unknown else
        'round_request_limit' if remaining_count == 0 else
        'post_burst_anchor_present' if context['post_burst_anchor'] is not None else
        'round_frontier_already_requested' if local_frontiers else
        'growth_count_outside_frontier_range' if growth_count not in (1, 2, 3) else
        'current_reward_unknown_or_not_pure_purple' if current is None or mapped_current is None
            or current != mapped_current or not _pure_purple(board) else
        'current_reward_above_frontier_limit' if current > 55 else
        'low_purple_slots_below_minimum' if context['low_purple_slot_count'] < 2 else
        'observed_support_unknown' if not support_known else
        'current_reward_not_above_observed_support' if current <= support_max else
        'opportunity_cost_unknown' if not _number(opportunity) or opportunity < 0 else
        'expected_delta_not_above_opportunity' if _number(expected) and expected <= opportunity else
        'expected_delta_invalid' if expected is not None and not _number(expected) else
        'frontier_growth_cost_not_3000' if type(growth_cost) is not int or growth_cost != 3000 else
        'exploration_money_limit' if remaining_money < growth_cost else
        'frontier_money_limit' if remaining_frontier < growth_cost else
        'exploration_loss_limit' if remaining_loss < current else
        'eligible'
    )
    frontier = {
        'mode': 'frontier-post-burst-v2', 'eligible': reason == 'eligible', 'reason': reason,
        'model_action': model.get('action'), 'model_reason': model.get('reason'),
        'model_exploration_eligible': model.get('exploration_eligible'),
        'coverage': coverage, 'model_provenance': model.get('model_provenance'),
        'observed_purple_award_range': support, 'observed_purple_award_max': support_max,
        'current_reward_exposure_purple': current, 'current_reward_limit_purple': 55,
        'low_purple_slot_count': context['low_purple_slot_count'], 'low_purple_slot_minimum': 2,
        'growth_count': growth_count, 'allowed_growth_counts': [1, 2, 3], 'required_growth_cost': 3000,
        'expected_delta_purple': expected, 'opportunity_cost_purple': opportunity,
        'verified_round_frontier_requests': sum(verified(row) for row in local_frontiers),
        'verified_frontier_money_spent': frontier_spend,
        'frontier_money_limit': policy.frontier_money_limit, 'frontier_remaining': remaining_frontier,
        'exploration_requests_remaining': remaining_count,
        'exploration_money_remaining': remaining_money, 'exploration_loss_remaining': remaining_loss,
    }
    context.update({
        'frontier_exploration': frontier, 'frontier_eligible': frontier['eligible'],
        'frontier_reason': reason, 'verified_frontier_money_spent': frontier_spend,
        'frontier_remaining': remaining_frontier,
    })
    return context
