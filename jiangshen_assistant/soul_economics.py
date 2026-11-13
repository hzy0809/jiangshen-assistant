"""Bounded opening counts for an explicit open-then-harvest reference policy.

This is an economic comparison, never permission to open or spend. Future
growth, cross-entry allocation and terminal value for unspent money are outside
this reference. The executor must recheck every real action independently.
"""
from __future__ import annotations

from collections.abc import Mapping


ECONOMIC_CONTEXT_SCHEMA = 'soul-execution-economics-v1'
REFERENCE_POLICY = 'authorized_open_then_immediate_harvest_v1'


def _integer(value):
    return type(value) is int and value >= 0


def opening_reference_counts(context, mode, budget, growth_cost):
    """Return a reference count difference in O(number of supported entries).

    A zero count difference needs no invented initial-board payoff. A positive
    difference requires actual paid initial-board evidence from the caller.
    Multiple money-dependent entries need an allocation model; report fallback
    rather than assigning their authorized budget to the current entry.
    """
    result = {
        'reference_policy': REFERENCE_POLICY,
        'value_function': 'N_reference_not_optimal_future_value',
        'formula': 'N_reference(b)-N_reference(b-growth_cost)',
        'scope': 'remaining_authorized_executor_starts_current_round_excluded',
        'future_growth_in_reference': False,
        'unspent_money_terminal_utility': 0,
        'status': 'fallback', 'reason': 'execution_economic_context_missing',
        'budget_before': budget, 'growth_cost': growth_cost,
    }
    if not isinstance(context, Mapping):
        return result
    counts = context.get('future_starts_by_mode')
    if (context.get('schema') != ECONOMIC_CONTEXT_SCHEMA
            or context.get('current_round_excluded') is not True
            or context.get('entry_mode') != mode
            or type(context.get('free_only')) is not bool
            or not isinstance(counts, Mapping) or set(counts) != {'1', '2'}
            or any(not _integer(count) or count > 100 for count in counts.values())):
        result['reason'] = 'execution_economic_context_invalid'
        return result
    result['future_starts_by_mode'] = dict(counts)
    if not _integer(budget) or not _integer(growth_cost) or growth_cost > budget:
        result['reason'] = 'finite_reference_budget_unavailable'
        return result
    after = budget - growth_cost
    result['budget_after'] = after

    def zero(reason):
        result.update(status='zero', reason=reason, lost_paid_starts=0)
        return result

    if not any(counts.values()):
        return zero('no_remaining_authorized_starts')
    # All permitted reference starts cost zero here. Their reward can be
    # unknown and still cancels; future growth is deliberately not included.
    if context['free_only'] or context.get('paid_starts_allowed') is False:
        return zero('reference_has_no_permitted_positive_cost_starts')
    if context.get('paid_starts_allowed') is not True:
        result['reason'] = 'future_paid_start_permission_unknown'
        return result
    quotes = context.get('quotes_by_mode')
    if not isinstance(quotes, Mapping):
        result['reason'] = 'future_start_quotes_missing'
        return result
    entries = []
    for entry in ('1', '2'):
        count = counts[entry]
        if not count:
            continue
        quote = quotes.get(entry)
        if (not isinstance(quote, Mapping) or quote.get('known') is not True
                or type(quote.get('free_unlocked')) is not bool
                or not _integer(quote.get('free_remaining'))
                or not _integer(quote.get('paid_start_cost'))
                or not _integer(quote.get('next_start_cost'))
                or quote['next_start_cost'] != (0 if quote['free_unlocked']
                    and quote['free_remaining'] else quote['paid_start_cost'])):
            result.update(reason='future_start_quote_unknown_or_inconsistent', quote_entry=entry)
            return result
        free = min(count, quote['free_remaining']) if quote['free_unlocked'] else 0
        paid = count - free
        price = quote['paid_start_cost']
        entries.append({'entry_mode': int(entry), 'authorized_starts': count,
                        'free_starts': free, 'positive_cost_starts': paid if price else 0,
                        'paid_start_quote': price})
    result['entry_counts'] = entries
    active = [entry for entry in entries if entry['positive_cost_starts']
              and entry['paid_start_quote'] <= budget]
    if not active:
        return zero('no_affordable_permitted_positive_cost_reference_starts')
    total_cost = sum(entry['positive_cost_starts'] * entry['paid_start_quote'] for entry in entries)
    if total_cost <= after:
        return zero('all_authorized_reference_starts_still_affordable')
    if len(active) != 1:
        result['reason'] = 'mixed_entry_budget_allocation_not_modeled'
        return result
    entry = active[0]
    price, count = entry['paid_start_quote'], entry['positive_cost_starts']
    before_count, after_count = min(count, budget // price), min(count, after // price)
    result.update(reference_entry_mode=entry['entry_mode'], paid_start_quote=price,
                  affordable_paid_starts_before=before_count,
                  affordable_paid_starts_after=after_count,
                  lost_paid_starts=before_count - after_count)
    if before_count == after_count:
        return zero('same_affordable_reference_start_count')
    if entry['entry_mode'] != mode:
        result['reason'] = 'other_entry_reference_value_not_modeled'
        return result
    result.update(status='initial_board_reference_required', reason='paid_opening_count_decreases')
    return result
