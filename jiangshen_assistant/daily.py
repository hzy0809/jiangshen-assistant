"""Read-only daily activity projections from the pinned game client.

Progress is a client-side projection. It does not itself prove that an action
or reward was accepted by the server.
"""
from __future__ import annotations


def _number(value: object, *, nullable: bool = False) -> bool:
    return (nullable and value is None) or (type(value) is int and value >= 0)


def _flag(value: object) -> bool:
    return value is None or type(value) is bool


def valid_observation(value: object) -> bool:
    """Reject malformed projections before they reach a workflow or display."""
    if (not isinstance(value, dict) or not {'schema', 'available', 'reason', 'source', 'character_id',
                                          'server_now', 'server_day', 'refresh_clear', 'categories'} <= value.keys()
            or type(value.get('schema')) is not int or value['schema'] != 1):
        return False
    if (type(value.get('available')) is not bool or not isinstance(value.get('reason'), str)
            or value.get('source') != 'client_projection' or not isinstance(value.get('character_id'), str)
            or not _number(value.get('server_now'), nullable=True)
            or not _number(value.get('server_day'), nullable=True) or not isinstance(value.get('categories'), list)):
        return False
    if not value['available']:
        return (bool(value['reason']) and value['character_id'] == '' and value['server_now'] is None
                and value['server_day'] is None and value['refresh_clear'] is None
                and value['categories'] == [])
    identity = value['character_id']
    if (not identity.isascii() or not identity.isdecimal() or identity.startswith('0')
            or len(identity) > 20 or int(identity) > 18446744073709551615):
        return False
    if (value['refresh_clear'] is not True or not _number(value['server_now']) or not _number(value['server_day']) or value['server_day'] == 0
            or not value['server_day'] <= value['server_now'] < value['server_day'] + 86400
            or len(value['categories']) != 6):
        return False
    missing = value.get('missing_tasks', [])
    if not isinstance(missing, list) or len(missing) > 20:
        return False
    missing_ids = set()
    for row in missing:
        if (not isinstance(row, dict) or type(row.get('task_id')) is not int
                or not 1 <= row['task_id'] <= 255 or row['task_id'] in missing_ids
                or type(row.get('category_id')) is not int or not 1 <= row['category_id'] <= 6
                or type(row.get('template_active')) is not bool or type(row.get('ui_unlocked')) is not bool
                or not _flag(row.get('entry_unlocked'))
                or not _number(row.get('ui_button_id'))):
            return False
        missing_ids.add(row['task_id'])
    seen_tasks: set[int] = set()
    for expected_id, category in enumerate(value['categories'], 1):
        if (not isinstance(category, dict) or not {'category_id', 'name', 'display_order', 'visible',
                'current', 'required', 'reward_ready', 'reward_claimed', 'server_finished',
                'reward_refresh', 'tasks'} <= category.keys()
                or type(category.get('category_id')) is not int or category['category_id'] != expected_id):
            return False
        if (not isinstance(category.get('name'), str) or type(category.get('visible')) is not bool
                or not _number(category.get('display_order'), nullable=True)
                or not all(_number(category.get(key), nullable=True) for key in ('current', 'required', 'reward_refresh'))
                or not all(_flag(category.get(key)) for key in ('reward_ready', 'reward_claimed', 'server_finished'))
                or not isinstance(category.get('tasks'), list)):
            return False
        for task in category['tasks']:
            if (not isinstance(task, dict) or not {'task_id', 'name', 'current', 'latest_current',
                    'required', 'complete', 'visible', 'reward_ready', 'availability', 'reason',
                    'ui_button_id'} <= task.keys() or type(task.get('task_id')) is not int
                    or not 1 <= task['task_id'] <= 255):
                return False
            if task['task_id'] in seen_tasks:
                return False
            seen_tasks.add(task['task_id'])
            if (not isinstance(task.get('name'), str) or not isinstance(task.get('reason'), str)
                    or not _number(task.get('current'), nullable=True)
                    or not _number(task.get('latest_current'), nullable=True)
                    or not _number(task.get('required')) or not _flag(task.get('complete'))
                    or type(task.get('visible')) is not bool or task.get('reward_ready', False) is not None
                    or task.get('availability') not in ('available', 'unavailable', 'unknown')
                    or not _number(task.get('ui_button_id'))):
                return False
    if any(task_id in seen_tasks and task_id != 16 for task_id in missing_ids):
        # Stone may be present as a template-only row with null progress.
        return False
    return True
