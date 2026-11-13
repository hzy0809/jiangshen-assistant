"""Bounded, tunable stopping rules; server probabilities are not known."""
from __future__ import annotations

from dataclasses import dataclass
from copy import deepcopy
from math import isfinite


@dataclass(frozen=True)
class SoulPolicy:
    version: str = 'whole-board-v2'
    max_growths: int = 3
    protect_level: int = 8
    protect_count: int = 2
    high_level_count: int = 3
    model_path: str = ''
    explore_growths: int = 0
    exploration_mode: str = 'coverage'
    exploration_money_limit: int = 0
    exploration_loss_limit: int = 0
    frontier_money_limit: int = 0

    def __post_init__(self):
        if (self.version not in ('whole-board-v2', 'legacy-v1', 'joint-value-v3')
                or type(self.max_growths) is not int or not 0 <= self.max_growths <= 20
                or type(self.protect_level) is not int or not 1 <= self.protect_level <= 10
                or type(self.protect_count) is not int or not 1 <= self.protect_count <= 8
                or type(self.high_level_count) is not int or not 1 <= self.high_level_count <= 8
                or not isinstance(self.model_path, str) or len(self.model_path) > 4096
                or (self.version == 'joint-value-v3') != bool(self.model_path.strip())
                or type(self.explore_growths) is not int or not 0 <= self.explore_growths <= 3
                or self.exploration_mode not in ('coverage', 'post-burst-v1', 'frontier-post-burst-v2')
                or type(self.exploration_money_limit) is not int or self.exploration_money_limit < 0
                or type(self.exploration_loss_limit) is not int or self.exploration_loss_limit < 0
                or type(self.frontier_money_limit) is not int or not 0 <= self.frontier_money_limit <= 9000
                or self.exploration_mode == 'post-burst-v1' and self.explore_growths > 2
                or self.exploration_mode == 'frontier-post-burst-v2' and (
                    self.explore_growths > 2 or self.exploration_money_limit > 24000
                    or self.exploration_loss_limit > 80)
                or self.exploration_mode != 'frontier-post-burst-v2' and self.frontier_money_limit != 0
                or self.version != 'joint-value-v3' and (self.explore_growths != 0
                    or self.exploration_mode != 'coverage' or self.exploration_money_limit != 0
                    or self.exploration_loss_limit != 0 or self.frontier_money_limit != 0)):
            raise ValueError('soul_policy_invalid')
        if self.version == 'legacy-v1' and (self.max_growths, self.protect_level,
                self.protect_count, self.high_level_count) != (2, 8, 1, 3):
            raise ValueError('soul_legacy_policy_parameters_invalid')


@dataclass(frozen=True)
class SoulDecision:
    schema: int
    action: str
    reason: str
    policy: SoulPolicy
    board: tuple
    growth_count: int
    coins: int | None
    growth_cost: int | None
    budget_remaining: int | None
    occupied_count: int
    protected_count: int
    high_level_count: int
    level_sum: int
    color_counts: tuple
    level_counts: tuple
    legacy_would_harvest: bool
    joint_value: dict | None = None


def decide_soul_action(board, growth_count, coins, growth_cost, *, policy=None,
                       budget_remaining=None, force_harvest_reason='', joint_model=None, start_cost=None,
                       exploration_remaining=0, exploration_history=(), round_growths=(), economic_context=None):
    """Choose from a fresh whole board and record the evidence behind the choice.

    joint-value-v3 uses complete observed board transitions, including every
    cascade within a paid request. Count rules remain explicit older policies.
    """
    policy = SoulPolicy() if policy is None else policy
    if not isinstance(policy, SoulPolicy):
        raise ValueError('soul_policy_invalid')
    if type(growth_count) is not int or growth_count < 0:
        raise ValueError('soul_growth_count_invalid')
    if (coins is not None and (type(coins) is not int or coins < 0)
            or growth_cost is not None and (type(growth_cost) is not int or growth_cost <= 0)
            or budget_remaining is not None and (type(budget_remaining) is not int or budget_remaining < 0)):
        raise ValueError('soul_growth_cost_invalid')
    if not isinstance(board, (list, tuple)) or len(board) != 8:
        raise ValueError('soul_board_invalid')
    for slot in board:
        if not isinstance(slot, (list, tuple)) or len(slot) != 2:
            raise ValueError('soul_board_invalid')
        color, level = slot
        if (type(color) is not int or type(level) is not int or not 0 <= color <= 4
                or not 0 <= level <= 10 or (color == 0) != (level == 0)):
            raise ValueError('soul_board_invalid')
    board = tuple(tuple(slot) for slot in board)
    occupied = [(color, level) for color, level in board if color]
    if not occupied and force_harvest_reason != 'soul_auto_harvest':
        raise ValueError('soul_board_empty')
    protected = sum(color >= 2 and level >= policy.protect_level for color, level in occupied)
    high = sum(level >= policy.protect_level for _, level in occupied)
    all_max = bool(occupied) and all(level == 10 for _, level in occupied)
    legacy = (coins is None or growth_cost is None or coins < growth_cost or growth_count >= 2
              or any(color >= 2 and level >= 8 for color, level in occupied)
              or sum(level >= 8 for _, level in occupied) >= 3 or all_max)
    reason = (force_harvest_reason or
              ('soul_growth_quote_unavailable' if coins is None or growth_cost is None else
               'soul_coin_balance_limit' if coins < growth_cost else
               'soul_plan_budget_limit' if budget_remaining is not None and growth_cost > budget_remaining else
               'soul_growth_cap' if growth_count >= policy.max_growths else
               'soul_board_all_max_level' if all_max else ''))
    joint_value = None
    if policy.version == 'joint-value-v3':
        action = 'harvest'
        hard_reason = reason
        if hard_reason and joint_model is not None:
            joint_value = {'action': 'harvest', 'reason': hard_reason,
                           'model_provenance': getattr(joint_model, 'provenance', None),
                           'target_color': getattr(joint_model, 'mode', None),
                           'entry_mode': getattr(joint_model, 'mode', None),
                           'execution_mode': 'forced_harvest'}
        if not reason:
            if joint_model is None:
                reason = 'soul_joint_model_unavailable'
            else:
                joint_value = joint_model.assess(board, growth_count, growth_cost, start_cost,
                    budget_remaining=coins if budget_remaining is None else min(coins, budget_remaining),
                    remaining_growths=policy.max_growths - growth_count, economic_context=economic_context)
                action, reason = joint_value.get('action'), joint_value.get('reason')
                if action not in ('grow', 'harvest') or not isinstance(reason, str) or not reason:
                    raise ValueError('soul_joint_model_decision_invalid')
                # Prices apply to mixed-board choices, not to the requested
                # actual blue harvest target. Bounds must value whole outcomes
                # before taking extrema; no per-color independent risk fiction.
                opportunity = joint_value.get('opportunity_cost_utility')
                bounds = joint_value.get('expected_delta_bounds_by_color')
                bounds_known = (isinstance(bounds, dict) and all(
                    isinstance(bounds.get(str(color)), dict)
                    and all(type(bounds[str(color)].get(side)) in (int, float)
                            and isfinite(bounds[str(color)][side]) for side in ('lower', 'upper'))
                    and bounds[str(color)]['lower'] <= bounds[str(color)]['upper']
                    for color in range(1, 5)))
                guard = joint_value.get('unpriced_color_loss_boundary')
                utility_bounds = joint_value.get('expected_delta_utility_bounds')
                utility_known = (isinstance(utility_bounds, dict)
                    and all(type(utility_bounds.get(side)) in (int, float)
                            and isfinite(utility_bounds[side]) for side in ('lower', 'upper'))
                    and utility_bounds['lower'] <= utility_bounds['upper'])
                bounded_value_known = (bounds_known
                    and utility_known
                    and joint_value.get('utility_version') == 'user-blue-purple-orange-v1'
                    and joint_value.get('utility_weights_by_color') == {'1': 1, '2': 5, '3': 10}
                    and joint_value.get('forecast_semantics') == 'weighted_partial_identification_bounds_v1'
                    and joint_value.get('decision_value_semantics') == 'conservative_joint_utility_lower_bound_not_point_expectation'
                    and joint_value.get('recommendation_basis') == 'joint_utility_lower_bound_with_engineering_reserve'
                    and joint_value.get('forecast_coverage_supported') is True
                    and isinstance(guard, dict) and guard.get('violating_colors') == [])
                lower = joint_value.get('conservative_delta_utility')
                known = (type(opportunity) in (int, float) and isfinite(opportunity) and opportunity >= 0
                         and type(lower) in (int, float) and isfinite(lower) and lower > opportunity
                         and bounded_value_known)
                if action == 'grow' and policy.explore_growths == 0 and not known:
                    action, reason = 'harvest', 'soul_joint_profit_value_lower_bound_unknown'
                    joint_value = {**joint_value, 'action': action, 'reason': reason}
                if (policy.exploration_mode == 'coverage' and action == 'harvest'
                        and joint_value.get('exploration_eligible') is True
                        and policy.explore_growths > 0 and exploration_remaining > 0):
                    joint_value = {**joint_value, 'estimated_action': action, 'estimated_reason': reason,
                                   'execution_mode': 'budgeted_exploration',
                                   'exploration_remaining_before': exploration_remaining}
                    action, reason = 'grow', 'soul_joint_budgeted_exploration'
        if policy.exploration_mode in ('post-burst-v1', 'frontier-post-burst-v2'):
            from .soul_exploration import frontier_post_burst_context, post_burst_context
            context_options = dict(policy=policy, board=board, growth_cost=growth_cost,
                joint_model=joint_model, joint_value=joint_value, exploration_history=exploration_history,
                round_growths=round_growths, hard_reason=hard_reason)
            context = (frontier_post_burst_context(**context_options, growth_count=growth_count)
                       if policy.exploration_mode == 'frontier-post-burst-v2' else
                       post_burst_context(**context_options))
            joint_value = {**(joint_value or {}), 'estimated_action': action, 'estimated_reason': reason,
                           'post_burst_exploration': context,
                           'execution_mode': 'forced_harvest' if hard_reason else 'model'}
            if policy.exploration_mode == 'frontier-post-burst-v2':
                joint_value.update({key: context[key] for key in (
                    'frontier_exploration', 'verified_frontier_money_spent', 'frontier_remaining',
                    'frontier_eligible', 'frontier_reason')})
            if context['recovery_not_improved'] and action != 'grow' and not force_harvest_reason:
                action, reason = 'harvest', 'soul_joint_recovery_not_improved'
                joint_value['execution_mode'] = 'post_burst_recovery_stop'
            elif context['eligible']:
                action, reason = 'grow', 'soul_joint_post_burst_exploration'
                joint_value['execution_mode'] = 'post_burst_exploration'
            elif context.get('frontier_eligible'):
                action, reason = 'grow', 'soul_joint_frontier_exploration'
                joint_value['execution_mode'] = 'frontier_exploration'
            joint_value['execution_reason'] = reason
        if joint_value is not None:
            joint_value['execution_economic_context'] = deepcopy(economic_context)
            joint_value['execution_objective'] = ('information' if reason in (
                'soul_joint_budgeted_exploration', 'soul_joint_post_burst_exploration',
                'soul_joint_frontier_exploration') else 'profit')
    else:
        reason = reason or ('soul_board_protected_count' if protected >= policy.protect_count else
                            'soul_board_high_level_count' if high >= policy.high_level_count else '')
        action = 'harvest' if reason else 'grow'
        reason = reason or 'soul_bounded_growth'
    return SoulDecision(2 if policy.version == 'joint-value-v3' else 1, action, reason, policy,
                        board, growth_count, coins, growth_cost, budget_remaining, len(occupied), protected,
                        high, sum(level for _, level in occupied),
                        tuple(sum(color == c for color, _ in board) for c in range(5)),
                        tuple(sum(level == lv for _, level in board) for lv in range(11)), bool(legacy), joint_value)



def choose_soul_action(board, growth_count, coins, growth_cost, **options):
    """Compatibility entry point returning the action string."""
    return decide_soul_action(board, growth_count, coins, growth_cost, **options).action
