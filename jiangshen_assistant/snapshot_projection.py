"""Small transport views that leave canonical workflow evidence untouched."""

HISTORY_FIELDS = frozenset(('rounds', 'growths', 'decisions', 'samples', 'not_submitted_actions',
                            'recovery_history', 'historical_unknown'))


def compact_projection(value, *, pending_action=False):
    if isinstance(value, dict):
        result = {}
        for key, item in value.items():
            if key in HISTORY_FIELDS and isinstance(item, (list, tuple)):
                result[key + '_count'] = len(item)
                result[key + '_details_omitted'] = True
            elif pending_action and key == 'decision' and item is not None:
                result['decision_details_omitted'] = True
            else:
                result[key] = compact_projection(item, pending_action=key == 'pending_action')
        return result
    if isinstance(value, (list, tuple)):
        return [compact_projection(item) for item in value]
    return value
