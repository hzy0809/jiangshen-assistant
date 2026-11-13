"""Live resource admission for the existing fixed commands.

Reservations are conservative admission accounting, never claims of observed
spending. Uncertain outcomes survive policy edits; exact not-invoked refusals
can release their correlated reservation. A runtime
budget_owner keeps the same allowance when its workflow reacquires a token.
"""
from __future__ import annotations

from .bridge_client import BridgeAdmissionClosed


RESOURCES = frozenset(('money', 'gold', 'peaches', 'practice', 'stamina', 'stone', 'mana',
                       'experience', 'contribution', 'silver_pick', 'gold_pick'))
CAPABILITIES = {
    'purchase': 'The existing paid soul-start opportunity is supported; growth and donations are ordinary resource spends.',
    'accelerate': 'No fixed acceleration command is available.',
    'replenish': 'No fixed resource replenishment command is available.',
}


class ResourcePolicyDenied(BridgeAdmissionClosed):
    """The live policy refused a command before any bridge publication."""


def read_policy(provider):
    try:
        value = provider() if provider is not None else None
    except Exception as exc:
        raise ResourcePolicyDenied('resource_policy_provider_unavailable') from exc
    if value is None:
        return None
    if not isinstance(value, dict):
        raise ResourcePolicyDenied('resource_policy_invalid')
    allowed, limits = value.get('allowed_resources'), value.get('limits')
    if (not isinstance(allowed, (tuple, list, set, frozenset))
            or any(not isinstance(key, str) or key not in RESOURCES for key in allowed)
            or not isinstance(limits, dict)
            or any(key not in RESOURCES or amount is not None and
                   (type(amount) is not int or amount < 0) for key, amount in limits.items())
            or any(type(value.get(key)) is not bool for key in
                   ('allow_purchase', 'allow_accelerate', 'allow_replenish'))):
        raise ResourcePolicyDenied('resource_policy_invalid')
    owner = value.get('budget_owner')
    if owner is not None and (not isinstance(owner, str) or not owner):
        raise ResourcePolicyDenied('resource_policy_budget_owner_invalid')
    return value


def check_costs(policy, costs, *, reserved=None, purchase=False):
    if policy is None:
        return
    reserved = reserved or {}
    if purchase and any(costs.values()) and not policy['allow_purchase']:
        raise ResourcePolicyDenied('resource_policy_purchase_disabled')
    for resource, cost in costs.items():
        if resource not in RESOURCES or type(cost) is not int or cost < 0:
            raise ResourcePolicyDenied('resource_policy_cost_unknown:' + resource)
        if cost == 0:
            continue
        if resource not in policy['allowed_resources']:
            raise ResourcePolicyDenied('resource_policy_resource_disabled:' + resource)
        limit = policy['limits'].get(resource)
        if limit is not None and reserved.get(resource, 0) + cost > limit:
            raise ResourcePolicyDenied('resource_policy_budget_exhausted:' + resource)


def soul_money_budget(provider, fallback, *, free_only=False):
    policy = read_policy(provider)
    if policy is None:
        return fallback
    # free_only selects a free *start*. Ordinary growth is an independent coin
    # spend and does not buy another start or replenish a resource.
    limit = policy['limits'].get('money') if 'money' in policy['allowed_resources'] else 0
    # An explicit edit is the current user ceiling, rather than the launch-time
    # copy. The caller subtracts its existing observed plan spending.
    return limit


def soul_paid_allowed(provider):
    policy = read_policy(provider)
    return bool(policy is not None and policy['allow_purchase']
                and 'money' in policy['allowed_resources']
                and policy['limits'].get('money') != 0)


def _amount(value, resource):
    if type(value) is not int or value < 0:
        raise ResourcePolicyDenied('resource_policy_cost_unknown:' + resource)
    return value


def _frame(state, domain, fields):
    frame = state.get(domain)
    if not isinstance(frame, dict) or frame.get('revision') != fields['expected_revision']:
        raise ResourcePolicyDenied('resource_policy_current_revision_changed:' + domain)
    role = frame.get('character_id', frame.get('role_id', (frame.get('view') or {}).get('role_id')))
    if 'character_id' in fields and role != fields['character_id']:
        raise ResourcePolicyDenied('resource_policy_current_identity_changed:' + domain)
    observation = frame.get('observation')
    if isinstance(observation, dict) and observation.get('available') is False:
        raise ResourcePolicyDenied('resource_policy_current_observation_unavailable:' + domain)
    if frame.get('observation_available') is False or (frame.get('view') or {}).get('observation_available') is False:
        raise ResourcePolicyDenied('resource_policy_current_observation_unavailable:' + domain)
    return frame


def command_costs(state, domain, action, fields):
    """Quote only current projections and established fixed-command contracts."""
    frame = _frame(state, domain, fields)
    view = frame.get('view') or frame
    purchase = False
    if domain == 'development':
        if action == 'equipment_enchant':
            candidates = [row for row in frame.get('equipment', {}).get('candidates', ())
                          if row.get('equipment_id') == fields['target_id']]
            if len(candidates) != 1:
                raise ResourcePolicyDenied('resource_policy_equipment_quote_unavailable')
            cost = candidates[0].get('cost') or {}
            return {key: _amount(cost.get(key), key) for key in ('money', 'stone')}, False
        if action == 'tribute_train':
            tribute = frame.get('tribute') or {}
            if tribute.get('food_type') != 0 or _amount(tribute.get('food'), 'peaches') < 1:
                raise ResourcePolicyDenied('resource_policy_tribute_peach_quote_changed')
            # The established single tribute branch invokes false, 1u and
            # SpiritTribute verifies receipt_food and the inventory delta == 1.
            return {'peaches': 1}, False
        if action == 'spirit_train':
            spirit = frame.get('spirit') or {}
            if spirit.get('food_type') != 0:
                raise ResourcePolicyDenied('resource_policy_spirit_peach_quote_changed')
            # This older training branch has no exact quote; reserve its known
            # inventory upper bound rather than invent a zero or one cost.
            return {'peaches': _amount(spirit.get('food'), 'peaches')}, False
        if action == 'star_activate':
            return {'practice': _amount((frame.get('star') or {}).get('next_cost'), 'practice')}, False
        if action == 'growth_soul_grow':
            requirement = (frame.get('growth') or {}).get('requirement') or {}
            return {'money': _amount(requirement.get('grow_cost'), 'money')}, False
        if action == 'growth_recruit_confirm':
            raise ResourcePolicyDenied('resource_policy_cost_capability_unavailable:' + action)
        if action in ('growth_soul_start',):
            # The growth adapter admits only the native single free blue start.
            requirement = (frame.get('growth') or {}).get('requirement') or {}
            used, free = requirement.get('used_times'), requirement.get('free_times')
            if (requirement.get('rate') != 1 or type(used) is not int
                    or type(free) is not int or used >= free):
                raise ResourcePolicyDenied('resource_policy_growth_soul_free_quote_unavailable')
        return {}, False
    if domain == 'daily_soul':
        if action == 'start':
            color = 'blue' if fields['soul_mode'] == 1 else 'purple'
            cost = _amount(((frame.get('quotes') or {}).get(color) or {}).get('start_cost'), 'money')
            if fields['free_only'] and cost:
                raise ResourcePolicyDenied('resource_policy_soul_free_only')
            return {'money': cost}, bool(cost)
        if action == 'grow':
            return {'money': _amount(frame.get('growth_cost'), 'money')}, False
        return {}, False
    if domain == 'daily_guild':
        if action in ('donate', 'premium_confirm'):
            kind = fields['donation_type']
            if kind not in (1, 2, 3):
                raise ResourcePolicyDenied('resource_policy_guild_donation_unknown')
            return ({'gold': 80} if kind == 3 else {'money': 1000 if kind == 1 else 100000}), False
        if action == 'lottery':
            return {'contribution': 400}, False
        return {}, False
    if domain == 'daily_refine' and action == 'refine':
        if view.get('mode') == 'experience':
            return {'experience': _amount(view.get('experience_cost'), 'experience')}, False
        return {'money': _amount(view.get('money_cost'), 'money'),
                'gold': _amount(view.get('gold_cost'), 'gold')}, False
    if domain == 'daily_refine' and action == 'experience':
        # This command selects the experience tab; refine sends the exchange.
        return {}, False
    if domain == 'daily_stone' and action == 'dig':
        return {'gold': _amount(view.get('gold_cost'), 'gold'),
                'silver_pick': _amount(view.get('silver_pick_cost'), 'silver_pick')}, False
    if domain == 'daily_mingshi' and action == 'harvest':
        return {'gold': _amount(view.get('gold_cost'), 'gold')}, False
    if domain in ('daily_reward', 'daily_arena', 'daily_timed', 'daily_wood'):
        # These fixed ports admit only existing reward/free-attempt branches;
        # native guards prohibit premium retries, buying attempts or cooldowns.
        return {}, False
    if domain == 'daily_script':
        task = fields['task_id']
        if view.get('task_id') != task:
            raise ResourcePolicyDenied('resource_policy_script_task_changed')
        if task == 308 and action.startswith('produce_'):
            # The same button first opens/scans the shop, then sends a quoted
            # production request only from product_ready.
            if view.get('phase') == 'product_ready':
                if not view.get('selected_product_id'):
                    raise ResourcePolicyDenied('resource_policy_shop_quote_unavailable')
                return {'mana': _amount(view.get('quoted_mana_cost'), 'mana')}, False
            if view.get('status') != 'IDLE':
                raise ResourcePolicyDenied('resource_policy_shop_charge_phase_unknown')
            return {}, False
        if task == 14 and action == 'start':
            if type(view.get('boss_selected')) is not bool:
                raise ResourcePolicyDenied('resource_policy_trial_cost_unknown')
            return {'stamina': 0 if view['boss_selected'] else 1}, False
        if task == 17 and action in ('enter', 'start'):
            raise ResourcePolicyDenied('resource_policy_cost_capability_unavailable:grave_stamina')
        if task in (201, 202, 203, 204, 205) and action == 'claim':
            rows = [row for row in view.get('rows', ()) if row.get('key') == view.get('target_key')]
            if len(rows) == 1 and rows[0].get('resource_cost', 0) not in (0, -1):
                raise ResourcePolicyDenied('resource_policy_cost_capability_unavailable:event_resource')
        if task in (8, 9, 10, 11, 14, 15, 17, 23, 24, 109, 201, 202, 203, 204, 205,
                    301, 302, 303, 304, 305, 306, 307, 308, 309):
            # These compiled routes use their existing inventory/free-attempt
            # guards. No premium retry, recharge, or acceleration is admitted.
            return {}, False
        raise ResourcePolicyDenied('resource_policy_cost_capability_unavailable:daily_script:' + str(task))
    return {}, purchase


def cleanup_action(domain, action):
    return (action in ('close', 'cleanup', 'reward_confirm', 'harvest', 'confirm', 'rearm',
                       'battle_exit', 'battle_close', 'reward_close', 'close_reward', 'close_message',
                       'close_product', 'close_battle', 'close_result', 'close_failed',
                       'close_panel', 'return_home', 'finish', 'dismiss', 'dismiss_prompt',
                       'clear_confirm', 'failed_close', 'return', 'resume_close', 'compare_close')
            and not (domain == 'daily_mingshi' and action == 'harvest')
            or domain == 'development' and (action.endswith('_close')
                or action.endswith('_reward_confirm') or action.endswith('_rankup_confirm')
                or action == 'growth_soul_harvest'))


class ResourceAdmission:
    def __init__(self):
        self.reserved = {}
        self._guild_reservations = {}
        self._token_owners = {}
        self._tickets = {}
        self._next_ticket = 0

    def authorize(self, provider, token, state, domain, action, fields):
        if cleanup_action(domain, action):
            return
        policy = read_policy(provider)
        try:
            costs, purchase = command_costs(state, domain, action, fields)
        except ResourcePolicyDenied:
            if policy is None:
                # Retain old behavior for commands with no live policy. They
                # cannot contribute a fabricated amount to this ledger.
                return
            raise
        token_key = (token.generation, token.run_id)
        old_owner = self._token_owners.get(token_key, token.run_id)
        owner = policy.get('budget_owner') or old_owner if policy is not None else old_owner
        if owner != old_owner:
            old = self.reserved.pop(old_owner, {})
            current = self.reserved.setdefault(owner, {})
            for resource, amount in old.items():
                current[resource] = current.get(resource, 0) + amount
            for (previous_owner, operation), amount in tuple(self._guild_reservations.items()):
                if previous_owner == old_owner:
                    self._guild_reservations[(owner, operation)] = amount
                    del self._guild_reservations[(previous_owner, operation)]
            for record in self._tickets.values():
                if record['owner'] == old_owner:
                    record['owner'] = owner
                    if record['guild_key'] is not None:
                        record['guild_key'] = (owner, record['guild_key'][1])
        self._token_owners[token_key] = owner
        reserved = self.reserved.setdefault(owner, {})
        if policy is not None and action == 'open':
            # Refuse an unaffordable route before taking its page when that
            # protocol has no pre-mutation cancel. No reservation is consumed
            # until the actual charge command below.
            upcoming = None
            if domain == 'daily_guild':
                upcoming = 'lottery' if fields['task_id'] == 18 else 'donate'
            elif domain == 'daily_soul':
                upcoming = 'start'
            if upcoming is not None:
                future_costs, future_purchase = command_costs(state, domain, upcoming, fields)
                check_costs(policy, future_costs, reserved=reserved, purchase=future_purchase)
            elif domain == 'daily_stone':
                check_costs(policy, {'silver_pick': 1}, reserved=reserved)
            elif domain == 'daily_refine':
                future_costs, future_purchase = command_costs(state, domain, 'refine', fields)
                check_costs(policy, future_costs, reserved=reserved, purchase=future_purchase)
        # Premium donate may show a dialog or invoke immediately. Hold one 80
        # gold allowance for that exact operation, then recheck permission and
        # the complete reserved total at premium_confirm without double booking.
        guild_key = (owner, fields.get('operation_id'))
        shared = domain == 'daily_guild' and action in ('donate', 'premium_confirm')
        already = shared and guild_key in self._guild_reservations
        if already and self._guild_reservations[guild_key] != costs:
            raise ResourcePolicyDenied('resource_policy_guild_reserved_choice_changed')
        baseline = {key: value - (costs.get(key, 0) if already else 0)
                    for key, value in reserved.items()}
        check_costs(policy, costs, reserved=baseline, purchase=purchase)
        if not already:
            for key, value in costs.items():
                reserved[key] = reserved.get(key, 0) + value
            if shared:
                self._guild_reservations[guild_key] = dict(costs)
            if any(costs.values()):
                self._next_ticket += 1
                ticket = self._next_ticket
                self._tickets[ticket] = {'owner': owner, 'domain': domain, 'action': action,
                    'fields': dict(fields), 'costs': dict(costs),
                    'character_id': ((state.get(domain) or {}).get('character_id')
                        or (state.get(domain) or {}).get('role_id')
                        or ((state.get(domain) or {}).get('view') or {}).get('role_id')),
                    'guild_key': guild_key if shared else None}
                return ticket
        return None

    def release_not_submitted(self, ticket, *, domain, action, fields):
        """Undo this soul call's ticket after a proven prepublication refusal.

        Only ObjectController's direct BridgeAdmissionClosed handler may use
        this path, before any sequence or wire exists. Published, returned or
        unknown commands must keep using receipt-based release_not_invoked.
        """
        record = self._tickets.get(ticket)
        if (domain != 'daily_soul' or record is None or record['domain'] != domain
                or record['action'] != action or record['fields'] != fields
                or record['guild_key'] is not None):
            return False
        reserved = self.reserved.get(record['owner'], {})
        if any(reserved.get(key, 0) < cost for key, cost in record['costs'].items()):
            return False
        for key, cost in record['costs'].items():
            reserved[key] -= cost
        del self._tickets[ticket]
        return True

    def release_not_invoked(self, ticket, wire):
        """Release only this call's exactly correlated local/native refusal.

        The port saves authorize()'s ticket and calls this with its returned
        wire, under the same admission lock. Exceptions, missing correlation,
        server rejection after invocation and UNKNOWN keep the reservation.
        Return True only when a reservation was actually released.
        """
        record = self._tickets.get(ticket)
        if record is None or not isinstance(wire, dict):
            return False
        domain, action, fields = record['domain'], record['action'], record['fields']
        ack = wire.get('ack') or {}
        sequence = wire.get('requested_seq', wire.get('seq'))
        command = domain.replace('_', '-') + '-command' if domain != 'development' else 'development-command'
        if domain == 'daily_script':
            command = 'daily-script'
        if (ack.get('status') != 'REJECTED' or type(sequence) is not int or sequence < 1
                or ack.get('command_seq') != sequence or ack.get('command_name') != command):
            return False
        frame = (ack.get('result') or {}).get(domain)
        if not isinstance(frame, dict):
            return False
        role = (frame.get('character_id') or frame.get('role_id')
                or (frame.get('view') or {}).get('role_id'))
        if role != record['character_id']:
            return False
        refused = False
        if domain == 'development':
            receipt = frame.get('last_command') or {}
            if (receipt.get('sequence') != sequence or receipt.get('action') != action
                    or receipt.get('operation_id') != fields.get('operation_id')):
                return False
            # These statuses/reasons are emitted before this fixed command can
            # invoke. Other REJECTED publications still need mutation evidence.
            refused = (receipt.get('status') == 'NOT_INVOKED' or
                receipt.get('status') == 'REJECTED' and
                receipt.get('reason') == 'development_context_revision_or_pending')
            operation = (frame.get(action.split('_', 1)[0]) or {}).get('operation') or {}
            refused = refused or (receipt.get('status') == 'REJECTED'
                and operation.get('sequence') == sequence
                and operation.get('operation_id') == fields.get('operation_id')
                and operation.get('action') == action
                and operation.get('expected_revision') == fields.get('expected_revision')
                and operation.get('mutation_attempted') is False)
        else:
            operation = frame.get('operation') or frame
            operation_id = operation.get('operation_id', operation.get('id'))
            if operation_id != fields.get('operation_id') or operation.get('sequence') != sequence:
                return False
            # Native mutation_attempted/invoked are cumulative for these owned
            # operations. A prior paid action therefore prevents an unsafe
            # release even when a later command is rejected.
            refused = (operation.get('mutation_attempted') is not True
                       and operation.get('invoked') is not True
                       and (operation.get('mutation_attempted') is False
                            or operation.get('invoked') is False)
                       or frame.get('gui_not_invoked_sequence') == sequence
                       and frame.get('gui_invocation_attempted') is False)
        if not refused:
            return False
        owner = record['owner']
        reserved = self.reserved.get(owner, {})
        if any(reserved.get(key, 0) < cost for key, cost in record['costs'].items()):
            return False
        for key, cost in record['costs'].items():
            reserved[key] -= cost
        if record['guild_key'] is not None:
            self._guild_reservations.pop(record['guild_key'], None)
        del self._tickets[ticket]
        return True
