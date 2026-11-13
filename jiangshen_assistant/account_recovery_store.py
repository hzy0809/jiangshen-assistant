"""Fail-closed, credential-free account-round journal; never executes work.

Hold ``acquire()`` ownership for the entire round, including child-process
recovery, and ``commit()`` a DISPATCHING entry *before* invoking a task. Only a
separately observed authoritative result may change it to COMMITTED. ``load()``
is a recovery read: unresolved dispatches become UNKNOWN in its returned copy,
never QUEUED. It neither restarts the assistant nor authorizes resuming a round.

Each run has its own file. A separate current-content anchor is published first
so an interrupted pair publication blocks recovery instead of admitting an old
queue. All successful publications include file fsync and exact readback. On
POSIX the containing directory is fsynced too; Windows file fsync does not claim
power-loss durability of directory metadata. Removing all journal files, or
restoring a matching older file and anchor together, cannot be detected locally.
"""
from __future__ import annotations

import copy
import hashlib
import json
import math
import os
from pathlib import Path
import re
import stat
import tempfile
import threading
import uuid

from .account_profiles import AccountProfileError, CITY_DAILY_KEYS, validate_document


TASK_ORDER = ('campaign', 'challenge', 'elite', 'daily', 'soul', 'tribute', 'equipment', 'star')
_LEGACY_TASK_ORDER = ('soul', 'tribute', 'equipment', 'star', 'challenge', 'elite', 'daily', 'campaign')
TERMINAL_CONTROLS = frozenset(('COMPLETED', 'PROCESSED', 'PARTIAL', 'STOPPED', 'UNKNOWN', 'FAILED', 'WAITING', 'BLOCKED'))
_CONTROLS = TERMINAL_CONTROLS | {'RUNNING', 'PAUSED', 'STOPPING'}
_RESULTS = frozenset(('COMPLETED', 'PROCESSED', 'PARTIAL', 'STOPPED', 'BLOCKED', 'FAILED', 'UNAVAILABLE', 'WAITING'))
_STATUSES = _RESULTS | {'QUEUED', 'SKIPPED', 'RUNNING', 'DISPATCHING', 'UNKNOWN'}
_PROFILE_FIELDS = {'id', 'label', 'server_name', 'server_tab', 'role_name', 'user_id', 'zone_id', 'role_id', 'tasks'}
_FIELDS = {'schema_version', 'run_id', 'control', 'phase', 'profiles', 'records',
           'pending_accounts', 'deferred_campaigns', 'current_account', 'current_task',
           'task_index', 'campaign_pass', 'recovery', 'dispatches'}
_ACCOUNT_FIELDS = {'id', 'label', 'server_name', 'role_name', 'role_id', 'status', 'reason', 'login_state', 'tasks'}
_PROGRESS_FIELDS = {'mode', 'status', 'reason', 'progress', 'stage', 'completed', 'target', 'remaining', 'settled_in_run', 'cleanup_pending'}
_ROW_FIELDS = {'name', 'kind', 'task_id', 'status', 'reason', 'current', 'required',
               'template_active', 'ui_button_id', 'ui_button_unlocked', 'entry_unlocked'}
_MAX_BYTES = 8 * 1024 * 1024
_MAX_RECORDS = 10000
_DAILY_EARLY = frozenset(('copy', 'online_reward', 'online_gift', 'training', 'arena', 'city_shop', 'trial', 'point_race'))
_DAILY_WAITABLE = _DAILY_EARLY - {'city_shop'}
_ROUTE_FIELDS = {'status', 'reason', 'dispatch_id', 'checked_at', 'remaining_seconds',
                 'ready_at', 'requires_online', 'cleanup'}
_PHASE_FIELDS = {'phase', 'selected_items', 'route_results', 'items'}
_RETRY_FIELDS = {'retry_evidence', 'retry_of'}
_DAILY_CONTEXT_RETRYABLE = frozenset(('guild_donation', 'divinity', 'mingshi'))
_DAILY_RETRYABLE = frozenset(('copy', 'training', 'arena', 'city_shop', 'trial', 'point_race')) | _DAILY_CONTEXT_RETRYABLE


class AccountRecoveryError(ValueError):
    """A credential-free failure: the caller must not admit another action."""


def _fail(code):
    raise AccountRecoveryError('account_recovery_' + code)


def _object(value, required, optional=()):
    if (type(value) is not dict or any(type(key) is not str for key in value)
            or not set(required) <= set(value) or set(value) - set(required) - set(optional)):
        _fail('schema_invalid')
    return value


def _text(value, *, empty=True, limit=8192):
    if type(value) is not str or len(value) > limit or (not empty and not value):
        _fail('text_invalid')
    return value


def _integer(value, minimum=0, maximum=2**63 - 1):
    if type(value) is not int or not minimum <= value <= maximum:
        _fail('integer_invalid')
    return value


def _boolean(value):
    if type(value) is not bool:
        _fail('boolean_invalid')


def _hex(value):
    if type(value) is not str or re.fullmatch('[0-9a-f]{32}', value) is None:
        _fail('id_invalid')
    return value


def _account_id(value):
    _text(value, empty=False, limit=36)
    try:
        if str(uuid.UUID(value)) != value:
            _fail('account_id_invalid')
    except ValueError:
        _fail('account_id_invalid')
    return value


def _identity(value):
    _text(value, limit=128)
    if value and (not value.isascii() or not value.isdecimal() or int(value) == 0):
        _fail('identity_invalid')


def _scalar(value):
    # The existing display progress fields contain scalar values only. Do not
    # admit arbitrary nested objects that could become a credential side channel.
    if value is None or type(value) is bool:
        return
    if type(value) is str:
        _text(value)
    elif type(value) is int:
        _integer(value, -(2**63))
    elif type(value) is float and math.isfinite(value):
        return
    else:
        _fail('display_value_invalid')


def _number(value):
    if type(value) not in (int, float) or not math.isfinite(value) or value < 0:
        _fail('number_invalid')


def _route(value, name):
    _object(value, _ROUTE_FIELDS, {'release_evidence', 'processed_without_reply', 'page_confirmed'})
    if type(value['status']) is not str or value['status'] not in _STATUSES - {'SKIPPED'}:
        _fail('daily_route_status_invalid')
    _text(value['reason'])
    if value['dispatch_id']:
        _hex(value['dispatch_id'])
    elif value['status'] != 'QUEUED':
        _fail('daily_route_boundary_missing')
    for field in ('checked_at', 'remaining_seconds', 'ready_at'):
        _number(value[field])
    _boolean(value['requires_online'])
    _text(value['cleanup'], limit=128)
    for field in ('processed_without_reply', 'page_confirmed'):
        if field in value:
            _boolean(value[field])
    if ((value['status'] == 'PROCESSED') != (value.get('processed_without_reply') is True)
            or value.get('page_confirmed') is True and value['status'] != 'COMPLETED'):
        _fail('daily_handled_evidence_invalid')
    if value['status'] == 'WAITING':
        if (name not in _DAILY_WAITABLE or value['remaining_seconds'] <= 0 or value['ready_at'] <= 0
                or not value['requires_online'] or value['cleanup'] not in ('VERIFIED_CLOSED', 'VERIFIED_IDLE')):
            _fail('daily_wait_evidence_invalid')
        if value['cleanup'] == 'VERIFIED_IDLE':
            validate_daily_idle_release(value.get('release_evidence'), route=name)
        elif 'release_evidence' in value:
            _fail('daily_closed_with_idle_evidence')
    elif (value['remaining_seconds'] or value['ready_at'] or value['requires_online'] or value['cleanup']):
        _fail('daily_nonwait_evidence_invalid')
    if value['status'] != 'WAITING' and 'release_evidence' in value:
        if value['status'] != 'UNKNOWN':
            _fail('daily_nonwait_release_evidence_invalid')
        # Retain the original observed release when later online continuity is
        # lost; this historical proof never changes the UNKNOWN result.
        validate_daily_idle_release(value['release_evidence'], route=name)
    if value['status'] == 'QUEUED' and (value['dispatch_id'] or value['checked_at']):
        _fail('daily_queued_boundary_invalid')


def _rows(values):
    if type(values) is not list or len(values) > _MAX_RECORDS:
        _fail('task_items_invalid')
    for item in values:
        _object(item, _ROW_FIELDS, {'evidence_path', 'category_id', 'cleanup', 'cleanup_pending', 'operation_id',
            'processed_without_reply', 'page_confirmed', 'request_issued', 'reply_received', 'continuation_safe',
            'got_before', 'got_after', 'mana_before', 'mana_after', 'targets_initial', 'targets_confirmed'})
        for field in ('name', 'kind', 'status', 'reason'):
            _text(item[field])
        for field in _ROW_FIELDS - {'name', 'kind', 'status', 'reason'}:
            _scalar(item[field])
        if 'category_id' in item:
            _scalar(item['category_id'])
        if 'evidence_path' in item:
            _text(item['evidence_path'])
        if 'cleanup' in item:
            _text(item['cleanup'], limit=128)
        if 'cleanup_pending' in item:
            _boolean(item['cleanup_pending'])
        if 'operation_id' in item:
            _text(item['operation_id'], limit=128)
        for field in ('processed_without_reply', 'page_confirmed', 'request_issued', 'reply_received', 'continuation_safe'):
            if field in item:
                _boolean(item[field])
        for field in ('got_before', 'got_after', 'mana_before', 'mana_after', 'targets_initial', 'targets_confirmed'):
            if field in item and item[field] is not None:
                _integer(item[field])
        if ((item['status'] == 'PROCESSED') != (item.get('processed_without_reply') is True)
                or item.get('page_confirmed') is True and item['status'] != 'COMPLETED'):
            _fail('daily_handled_row_evidence_invalid')


def validate_daily_retry_evidence(value, *, route, expected_identity=None, expected_session=None,
                                  expected_generation=None, expected_account_run=None, expected_account_id=None):
    """Validate a no-command witness; a status/reason alone never admits retry."""
    _object(value, {'schema_version', 'route', 'proof_kind', 'operation_admitted', 'queue_started',
        'gui_invoked', 'mutation_started', 'executor_entered', 'recovery_entered', 'cleanup',
        'boundary', 'coordinator_released', 'character_id', 'user_id', 'zone_id', 'context_key',
        'run_id', 'generation', 'refresh_cycle', 'server_now', 'workflow_command_attempts',
        'proof', 'boundary_proof'}, {'account_run_id', 'account_id'})
    if type(value['schema_version']) is not int or value['schema_version'] != 1:
        _fail('daily_retry_evidence_version_invalid')
    if route not in _DAILY_RETRYABLE or value['route'] != route:
        _fail('daily_retry_evidence_route_invalid')
    for field in ('operation_admitted', 'queue_started', 'gui_invoked', 'mutation_started'):
        if value[field] is not False:
            _fail('daily_retry_command_may_have_started')
    for field in ('executor_entered', 'recovery_entered'):
        _boolean(value[field])
    untouched = value['proof_kind'] == 'untouched_native_route'
    if untouched:
        _hex(value.get('account_run_id'))
        _account_id(value.get('account_id'))
        if (expected_account_run is not None and value['account_run_id'] != expected_account_run
                or expected_account_id is not None and value['account_id'] != expected_account_id):
            _fail('daily_retry_account_round_mismatch')
    elif 'account_run_id' in value or 'account_id' in value:
        _fail('daily_retry_legacy_scope_changed')
    if (value['cleanup'] != 'NOT_NEEDED' or value['boundary'] != ('ROUTE_UNTOUCHED' if untouched else 'VERIFIED_CLEAN')
            or value['coordinator_released'] is not True
            or type(value['workflow_command_attempts']) is not int or value['workflow_command_attempts'] != 0):
        _fail('daily_retry_boundary_unconfirmed')
    _hex(value['run_id'])
    _integer(value['generation'])
    _integer(value['refresh_cycle'], 1)
    _integer(value['server_now'], 1)
    if not value['refresh_cycle'] <= value['server_now'] < value['refresh_cycle'] + 86400:
        _fail('daily_retry_server_cycle_unconfirmed')
    for field in ('user_id', 'zone_id', 'character_id'):
        _identity(value[field])
        if not value[field]:
            _fail('daily_retry_identity_missing')
    _text(value['context_key'], empty=False)
    context = _object(_decode(value['context_key'].encode('utf-8')),
                      {'pid', 'creation', 'nonce', 'generation', 'character_id', 'user_id', 'zone_id'})
    _integer(context['pid'], 1)
    _integer(context['creation'], 1, 2**64 - 1)
    _integer(context['generation'])
    _text(context['nonce'], empty=False, limit=128)
    if any(context[field] != value[field] for field in ('generation', 'character_id', 'user_id', 'zone_id')):
        _fail('daily_retry_context_mismatch')
    for field, configured in (expected_identity or {}).items():
        observed_field = 'character_id' if field == 'role_id' else field
        if configured and value.get(observed_field) != configured:
            _fail('daily_retry_identity_mismatch')
    if expected_session is not None and (context['pid'] != expected_session.get('pid')
            or context['creation'] != expected_session.get('process_creation_filetime')):
        _fail('daily_retry_session_mismatch')
    if expected_generation is not None and value['generation'] != expected_generation:
        _fail('daily_retry_generation_mismatch')
    # Native proof and the final home boundary are checked below by exact kind.
    _validate_daily_retry_proof(value)
    return copy.deepcopy(value)


def validate_daily_idle_release(value, *, route, expected_role=None):
    """An untouched native operation is released, never a closed operation."""
    _object(value, {'route', 'release', 'role_id', 'refresh_cycle', 'operation_id', 'cleanup', 'release_verified'})
    if (route not in ('trial', 'point_race') or value['route'] != route
            or value['release'] != 'VERIFIED_IDLE' or value['operation_id'] != ''
            or value['cleanup'] != 'NOT_NEEDED' or value['release_verified'] is not True):
        _fail('daily_idle_release_unconfirmed')
    _identity(value['role_id'])
    if not value['role_id'] or expected_role is not None and value['role_id'] != expected_role:
        _fail('daily_idle_release_identity_mismatch')
    _integer(value['refresh_cycle'], 1)
    return copy.deepcopy(value)


def _validate_daily_retry_proof(value):
    route, kind = value['route'], value['proof_kind']

    def observed(native, *, cycle='refresh_cycle'):
        _integer(native['revision'], 1)
        _integer(native[cycle], 1)
        if native['role_id'] != value['character_id'] or native[cycle] != value['refresh_cycle']:
            _fail('daily_retry_native_identity_mismatch')
        _text(native['status'], empty=False, limit=128)
        if native['status'] in ('UNKNOWN', 'STOPPED'):
            _fail('daily_retry_native_result_unknown')
        _text(native['operation_id'], limit=128)

    def absent(native, fields):
        for field in fields:
            if native[field] is not False:
                _fail('daily_retry_native_command_may_have_started')

    proof = value['proof']
    if kind == 'untouched_native_route' and route in _DAILY_CONTEXT_RETRYABLE:
        common = {'revision', 'role_id', 'refresh_cycle', 'task_id'}
        extra = ({'guild_id', 'operation_absent', 'retry_unstarted'} if route == 'guild_donation' else
                 {'operation_id', 'sequence', 'status', 'mutation_attempted', 'stock_callback_returned',
                  'retry_unstarted'} if route == 'divinity' else
                 {'operation_id', 'sequence', 'status', 'mutation_attempted', 'stock_callback_returned',
                  'account_progress_observed', 'panel_closed'})
        _object(proof, common | extra)
        _integer(proof['revision'], 1)
        if (proof['role_id'] != value['character_id'] or proof['refresh_cycle'] != value['refresh_cycle']
                or type(proof['task_id']) is not int
                or proof['task_id'] != {'guild_donation': 20, 'divinity': 8, 'mingshi': 7}[route]
                or value['boundary_proof'] != {} or value['recovery_entered'] is not False):
            _fail('daily_retry_untouched_identity_invalid')
        if route == 'guild_donation':
            _identity(proof['guild_id'])
            if not proof['guild_id'] or proof['operation_absent'] is not True or proof['retry_unstarted'] is not True:
                _fail('daily_retry_guild_operation_may_have_started')
        else:
            _integer(proof['sequence'], 0, 0)
            if proof['operation_id'] != '' or proof['status'] != 'IDLE':
                _fail('daily_retry_native_operation_may_have_started')
            absent(proof, ('mutation_attempted', 'stock_callback_returned'))
            if route == 'divinity':
                if proof['retry_unstarted'] is not True:
                    _fail('daily_retry_divinity_operation_may_have_started')
            else:
                absent(proof, ('account_progress_observed', 'panel_closed'))
        return
    if kind == 'daily_row_absent_no_command' and route in ('arena', 'training', 'trial'):
        _object(proof, {'category_id', 'task_id', 'daily_row_count', 'native'})
        expected = {'arena': (3, 9), 'training': (4, 23), 'trial': (4, 14)}[route]
        if (proof['category_id'], proof['task_id']) != expected:
            _fail('daily_retry_absent_row_mismatch')
        _integer(proof['daily_row_count'], 0, 0)
        native = proof['native']
        common = {'revision', 'role_id', 'refresh_cycle', 'operation_id', 'status', 'mutation_attempted', 'busy', 'home_ready'}
        extra = ({'sequence', 'closed'} if route == 'arena' else
                 {'task_id', 'cleanup_pending', 'training_scene'} if route == 'training' else
                 {'task_id', 'cleanup_pending', 'release_verified', 'entry_request_attempted',
                  'battle_request_attempted', 'resumed_existing_run'})
        _object(native, common | extra)
        observed(native)
        absent(native, ('mutation_attempted', 'busy'))
        _boolean(native['home_ready'])
        if value['executor_entered'] is not False or native['operation_id'] != '':
            _fail('daily_retry_executor_may_have_started')
        if route == 'arena':
            _integer(native['sequence'], 0, 0)
            _boolean(native['closed'])
        elif route == 'training':
            if type(native['task_id']) is not int or native['task_id'] != 23:
                _fail('daily_retry_training_task_invalid')
            absent(native, ('cleanup_pending', 'training_scene'))
        else:
            if type(native['task_id']) is not int or native['task_id'] != 14:
                _fail('daily_retry_trial_task_invalid')
            _boolean(native['release_verified'])
            absent(native, ('cleanup_pending', 'entry_request_attempted', 'battle_request_attempted',
                            'resumed_existing_run'))
    elif kind == 'point_race_unavailable_no_command' and route == 'point_race':
        _object(proof, {'revision', 'role_id', 'refresh_cycle', 'operation_id', 'sequence', 'status',
            'mutation_attempted', 'busy', 'cleanup_pending', 'closed', 'home_ready', 'observation_available'})
        observed(proof)
        _integer(proof['sequence'], 0, 0)
        _boolean(proof['closed'])
        _boolean(proof['home_ready'])
        absent(proof, ('mutation_attempted', 'busy', 'cleanup_pending', 'observation_available'))
        if proof['operation_id'] != '' or value['executor_entered'] is not True:
            _fail('daily_retry_point_race_may_have_started')
    elif kind == 'copy_locked_no_command' and route == 'copy':
        _object(proof, {'task_id', 'revision', 'role_id', 'refresh_cycle', 'entry_unlocked', 'operation_id',
            'status', 'availability', 'gui_invocation_attempted', 'mutation_attempted', 'busy',
            'cleanup_pending', 'closed', 'home_ready'})
        observed(proof)
        absent(proof, ('entry_unlocked', 'gui_invocation_attempted', 'mutation_attempted', 'busy', 'cleanup_pending'))
        _boolean(proof['closed'])
        if (type(proof['task_id']) is not int or proof['task_id'] != 303 or proof['operation_id'] != ''
                or proof['status'] != 'CONDITION_NOT_MET' or proof['availability'] != 'CONDITION_NOT_MET'
                or proof['home_ready'] is not True or value['executor_entered'] is not True):
            _fail('daily_retry_copy_precondition_invalid')
    elif kind == 'city_shop_idle_no_command' and route == 'city_shop':
        _object(proof, {'schema', 'task_id', 'revision', 'role_id', 'cycle', 'status', 'operation_id',
            'sequence', 'observation_available', 'gui_invocation_attempted', 'mutation_attempted', 'busy',
            'cleanup_pending', 'reward_pending', 'stock_callback_returned', 'receipts', 'processed_candidates',
            'collected_buildings', 'produced_buildings', 'gold_cost', 'mana_cost', 'money_received'})
        observed(proof, cycle='cycle')
        for field in ('sequence', 'processed_candidates', 'collected_buildings', 'produced_buildings',
                      'gold_cost', 'mana_cost', 'money_received'):
            _integer(proof[field], 0, 0)
        absent(proof, ('gui_invocation_attempted', 'mutation_attempted', 'busy', 'cleanup_pending',
                       'reward_pending', 'stock_callback_returned'))
        if (type(proof['schema']) is not int or proof['schema'] != 1
                or type(proof['task_id']) is not int or proof['task_id'] != 308
                or proof['status'] != 'IDLE' or proof['operation_id'] != '' or proof['receipts'] != []
                or type(proof['receipts']) is not list or proof['observation_available'] is not True
                or value['executor_entered'] is not True):
            _fail('daily_retry_city_operation_may_have_started')
    else:
        _fail('daily_retry_proof_kind_invalid')
    boundary = _object(value['boundary_proof'], {'task_id', 'revision', 'role_id', 'refresh_cycle',
        'home_ready', 'busy', 'cleanup_pending', 'status', 'operation_id', 'closed',
        'mutation_attempted', 'gui_invocation_attempted'})
    observed(boundary)
    for field in ('closed', 'mutation_attempted', 'gui_invocation_attempted'):
        _boolean(boundary[field])
    absent(boundary, ('busy', 'cleanup_pending'))
    if type(boundary['task_id']) is not int or boundary['task_id'] != 303 or boundary['home_ready'] is not True:
        _fail('daily_retry_home_boundary_unconfirmed')
    if boundary['operation_id']:
        if boundary['closed'] is not True:
            _fail('daily_retry_previous_operation_unclosed')
    else:
        absent(boundary, ('mutation_attempted', 'gui_invocation_attempted'))


def _pairs(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            _fail('duplicate_json_field')
        result[key] = value
    return result


def _constant(_value):
    _fail('json_constant_invalid')


def _decode(raw):
    try:
        return json.loads(raw.decode('utf-8'), object_pairs_hook=_pairs, parse_constant=_constant)
    except (ValueError, UnicodeError, RecursionError):
        _fail('json_invalid')


def _bytes(value):
    try:
        result = (json.dumps(value, ensure_ascii=True, sort_keys=True,
                             separators=(',', ':'), allow_nan=False) + '\n').encode('ascii')
    except (ValueError, TypeError, RecursionError):
        _fail('serialization_invalid')
    if len(result) > _MAX_BYTES:
        _fail('size_limit')
    return result


def _profiles(values):
    if type(values) is not list or not 1 <= len(values) <= 100:
        _fail('profiles_invalid')
    for profile in values:
        _object(profile, _PROFILE_FIELDS)
        for key in _PROFILE_FIELDS - {'tasks'}:
            _text(profile[key])
    # Reuse the established closed task-option schema, with credentials created
    # empty internally. Credential fields in the supplied frozen plan are errors.
    try:
        validated = validate_document({'schema_version': 1,
            'selected_ids': [p['id'] for p in values],
            'profiles': [dict(p, login_name='', password='') for p in values]})
    except (AccountProfileError, RecursionError):
        _fail('frozen_plan_invalid')
    canonical = [{key: p[key] for key in _PROFILE_FIELDS} for p in validated['profiles']]
    for original, normalized in zip(values, canonical):
        # Older journals freeze the plan before city routes existed.  Accept
        # those exact bytes without silently adding selections to the journal.
        original_items = original['tasks']['daily']['options']['items']
        original_options = original['tasks']['daily']['options']
        normalized_options = normalized['tasks']['daily']['options']
        normalized_items = normalized_options['items']
        for key in CITY_DAILY_KEYS - set(original_items):
            del normalized_items[key]
        if 'shop_duration' not in original_options:
            del normalized_options['shop_duration']
    if canonical != values:
        # Recovery never silently applies the profile store's legacy migrations.
        _fail('frozen_plan_not_canonical')
    return values


def _records(values, profiles):
    if type(values) is not list or len(values) != len(profiles):
        _fail('records_invalid')
    tasks = {}
    for record, profile in zip(values, profiles):
        _object(record, _ACCOUNT_FIELDS, {'identity_source', 'observed_identity', 'title_start', 'session_switch'})
        if (record['id'] != profile['id'] or type(record['status']) is not str
                or record['status'] not in _STATUSES):
            _fail('record_identity_or_status_invalid')
        for key in ('label', 'server_name', 'role_name', 'reason', 'login_state'):
            _text(record[key])
        _identity(record['role_id'])
        if profile['role_id'] and record['role_id'] != profile['role_id']:
            _fail('record_role_mismatch')
        if 'observed_identity' in record:
            observed = _object(record['observed_identity'], {'user_id', 'zone_id', 'role_id'})
            for field, identity in observed.items():
                _identity(identity)
                if not identity or profile[field] and identity != profile[field]:
                    _fail('observed_identity_invalid')
            if observed['role_id'] != record['role_id']:
                _fail('observed_role_mismatch')
        if 'identity_source' in record and record['identity_source'] not in (
                'OBSERVED_OWN_LOGIN', 'CONFIGURED_IDS_MATCHED'):
            _fail('identity_source_invalid')
        if 'title_start' in record:
            title = _object(record['title_start'], {'operation_id', 'outcome'}, {'code', 'cleanup_confirmed', 'pid'})
            _text(title['operation_id'], empty=False, limit=128)
            _text(title['outcome'], empty=False, limit=128)
            if 'code' in title:
                _text(title['code'], limit=128)
            if 'cleanup_confirmed' in title:
                _boolean(title['cleanup_confirmed'])
            if 'pid' in title:
                _integer(title['pid'], 1)
        if 'session_switch' in record:
            switch = _object(record['session_switch'], {'state', 'code', 'operation_id', 'game_pid',
                'game_creation', 'normal_close_sent', 'game_exit_confirmed', 'launcher_count', 'complete'})
            for key in ('state', 'code', 'operation_id'):
                if switch[key] is not None:
                    _text(switch[key], limit=128)
            for key in ('game_pid', 'game_creation'):
                if switch[key] is not None:
                    _integer(switch[key], 1, 2**64 - 1)
            _integer(switch['launcher_count'], 0, 100)
            for key in ('normal_close_sent', 'game_exit_confirmed', 'complete'):
                _boolean(switch[key])
        rows = record['tasks']
        if type(rows) is not list or len(rows) != len(TASK_ORDER):
            _fail('task_records_invalid')
        order = tuple(row.get('key') if type(row) is dict else None for row in rows)
        if order not in (TASK_ORDER, _LEGACY_TASK_ORDER):
            _fail('task_order_invalid')
        for task in rows:
            key = task['key']
            _object(task, {'key', 'label', 'status', 'reason', 'items'}, {'progress', 'routes'})
            if (task['key'] != key or type(task['status']) is not str
                    or task['status'] not in _STATUSES):
                _fail('task_record_invalid')
            _text(task['label'])
            _text(task['reason'])
            if not profile['tasks'][key]['enabled'] and task['status'] != 'SKIPPED':
                _fail('disabled_task_admitted')
            _rows(task['items'])
            if 'routes' in task:
                if key != 'daily' or not profile['tasks'][key]['enabled']:
                    _fail('daily_route_task_invalid')
                selected = {name for name, enabled in profile['tasks']['daily']['options']['items'].items() if enabled}
                _object(task['routes'], selected)
                for name, route in task['routes'].items():
                    _route(route, name)
                    if route.get('release_evidence', {}).get('role_id', record['role_id']) != record['role_id']:
                        _fail('daily_idle_release_record_identity_mismatch')
            if 'progress' in task:
                progress = _object(task['progress'], (), _PROGRESS_FIELDS)
                for value in progress.values():
                    _scalar(value)
                if 'cleanup_pending' in progress:
                    _boolean(progress['cleanup_pending'])
            tasks[record['id'], key] = task
    return tasks


def _phased_daily(dispatch, task, profile, latest_routes, account_run_id):
    """Validate independent daily route history; an UNKNOWN route never replays."""
    if dispatch['task'] != 'daily' or 'routes' not in task or not _PHASE_FIELDS <= set(dispatch):
        _fail('daily_phase_invalid')
    selected = dispatch['selected_items']
    if (type(selected) is not list or not selected or any(type(name) is not str for name in selected)
            or selected != sorted(set(selected)) or not set(selected) <= set(task['routes'])):
        _fail('daily_selection_invalid')
    phase = dispatch['phase']
    if phase not in ('warmup', 'normal', 'resume', 'retry'):
        _fail('daily_phase_invalid')
    if phase == 'warmup' and (not set(selected) <= _DAILY_EARLY
            or 'city_shop' in selected and profile['tasks']['daily']['options']['shop_duration'] <= 0):
        _fail('daily_warmup_invalid')
    retry_of = _object(dispatch.get('retry_of', {}), (), selected)
    retry_evidence = _object(dispatch.get('retry_evidence', {}), (), selected)
    if retry_of and phase not in ('normal', 'retry') or retry_evidence and phase not in ('warmup', 'normal'):
        _fail('daily_retry_phase_invalid')
    if phase == 'retry' and (len(selected) != 1 or set(retry_of) != set(selected)
            or not set(selected) <= _DAILY_CONTEXT_RETRYABLE):
        _fail('daily_context_retry_selection_invalid')
    if retry_evidence and (dispatch['state'] != 'COMMITTED' or dispatch['result_status'] != 'PARTIAL'):
        _fail('daily_retry_without_partial_result')
    for name, source in retry_of.items():
        _hex(source)
        if name not in _DAILY_RETRYABLE:
            _fail('daily_retry_route_invalid')
    for name in selected:
        identity = dispatch['account_id'], name
        prior = latest_routes.get(identity)
        if phase == 'resume':
            if (name not in _DAILY_WAITABLE or prior is None or prior[1] is None
                    or prior[1]['status'] != 'WAITING'):
                _fail('daily_resume_without_wait')
        elif prior is not None:
            context_retry = (phase == 'retry' and name in _DAILY_CONTEXT_RETRYABLE
                             and prior[0].get('phase') == 'normal' and not prior[0].get('retry_of')
                             and prior[0].get('retry_evidence', {}).get(name, {}).get('proof_kind') == 'untouched_native_route')
            warmup_retry = phase == 'normal' and prior[0].get('phase') == 'warmup'
            if (not (context_retry or warmup_retry) or retry_of.get(name) != prior[0]['dispatch_id']
                    or prior[0]['state'] != 'COMMITTED'
                    or prior[1] is None or prior[1]['status'] != 'PARTIAL'
                    or name not in prior[0].get('retry_evidence', {})):
                _fail('daily_route_replayed')
        elif name in retry_of:
            _fail('daily_retry_source_missing')
    results = _object(dispatch['route_results'], () if dispatch['state'] == 'DISPATCHING' else selected)
    _rows(dispatch['items'])
    if dispatch['state'] == 'DISPATCHING':
        if results or dispatch['items'] or retry_evidence:
            _fail('daily_dispatch_result_early')
    else:
        for name, result in results.items():
            _route(result, name)
            if (result['dispatch_id'] != dispatch['dispatch_id'] or result['checked_at'] <= 0
                    or result['status'] in ('QUEUED', 'RUNNING', 'DISPATCHING')):
                _fail('daily_route_result_invalid')
        for name, evidence in retry_evidence.items():
            if name not in _DAILY_RETRYABLE or results[name]['status'] != 'PARTIAL':
                _fail('daily_retry_evidence_result_invalid')
            if (phase == 'normal' and (name not in _DAILY_CONTEXT_RETRYABLE
                    or evidence.get('proof_kind') != 'untouched_native_route')):
                _fail('daily_context_retry_proof_invalid')
            validate_daily_retry_evidence(evidence, route=name,
                expected_identity={key: profile[key] for key in ('user_id', 'zone_id', 'role_id')},
                expected_account_run=account_run_id, expected_account_id=dispatch['account_id'])
        states = {result['status'] for result in results.values()}
        if ('UNKNOWN' in states and dispatch['result_status'] != 'UNKNOWN'
                or 'UNKNOWN' not in states and 'WAITING' in states and dispatch['result_status'] != 'WAITING'
                or states == {'COMPLETED'} and dispatch['result_status'] != 'COMPLETED'
                or dispatch['result_status'] == 'COMPLETED' and states != {'COMPLETED'}
                or dispatch['result_status'] == 'WAITING' and 'WAITING' not in states
                or dispatch['result_status'] == 'UNKNOWN' and 'UNKNOWN' not in states):
            _fail('daily_aggregate_result_invalid')
        if (states and states <= {'COMPLETED', 'PROCESSED'} and 'PROCESSED' in states
                and dispatch['result_status'] != 'PROCESSED'
                or dispatch['result_status'] == 'PROCESSED' and not (states and states <= {'COMPLETED', 'PROCESSED'} and 'PROCESSED' in states)):
            _fail('daily_processed_aggregate_invalid')
    for name in selected:
        latest_routes[dispatch['account_id'], name] = dispatch, results.get(name)


def _daily_projection(task, account_id, latest_routes, current, current_task):
    for name, route in task['routes'].items():
        latest = latest_routes.get((account_id, name))
        if latest is None:
            if route['status'] != 'QUEUED':
                _fail('daily_route_without_dispatch')
            continue
        dispatch, result = latest
        if result is not None:
            if route != result:
                _fail('daily_route_result_changed')
        elif (route['dispatch_id'] != dispatch['dispatch_id']
                or route['status'] not in ('RUNNING', 'DISPATCHING')
                or (account_id, 'daily') != (current, current_task)):
            _fail('daily_route_dispatch_boundary_missing')
    states = {route['status'] for route in task['routes'].values()}
    expected = ('UNKNOWN' if 'UNKNOWN' in states else
                'RUNNING' if 'RUNNING' in states else 'DISPATCHING' if 'DISPATCHING' in states else
                'WAITING' if 'WAITING' in states else 'QUEUED' if 'QUEUED' in states else
                'COMPLETED' if states == {'COMPLETED'} else
                'PROCESSED' if states and states <= {'COMPLETED', 'PROCESSED'} else 'PARTIAL' if states else 'UNAVAILABLE')
    if task['status'] != expected and not (not states and task['status'] == 'QUEUED'):
        _fail('daily_task_projection_invalid')


def _retired_wait(before, after):
    """Only a latest online wait may lose continuity; its result stays unknown."""
    if ('selected_items' not in before or before['state'] not in ('COMMITTED', 'UNKNOWN')
            or after['state'] != 'UNKNOWN' or after['result_status'] != 'UNKNOWN'):
        return False
    if any(before[key] != after[key] for key in set(before) - {'state', 'result_status', 'route_results'}):
        return False
    changed = False
    for name, result in before['route_results'].items():
        updated = after['route_results'].get(name)
        if result == updated:
            continue
        if (updated is None or result['status'] != 'WAITING' or updated['status'] != 'UNKNOWN'
                or result['dispatch_id'] != updated['dispatch_id']
                or result['checked_at'] != updated['checked_at']):
            return False
        changed = True
    return changed


def _finished_gift_observation(before, after):
    """An unsent gift wait may end without extending account online time."""
    if (before.get('selected_items') != ['online_gift'] or before['state'] != 'COMMITTED'
            or before['result_status'] != 'WAITING' or after['state'] != 'COMMITTED'
            or after['result_status'] != 'PARTIAL'
            or any(before[key] != after[key] for key in set(before) - {'result_status', 'route_results'})):
        return False
    route = before['route_results'].get('online_gift')
    if route is None or route['status'] != 'WAITING' or route['cleanup'] != 'VERIFIED_CLOSED':
        return False
    rows = [row for row in before['items']
            if (row.get('category_id'), row.get('task_id')) == (7, 309)]
    if len(rows) != 1:
        return False
    row = rows[0]
    # A confirmed request, unknown result or incomplete cleanup cannot use
    # this exception to journal immutability, even when its UI is now closed.
    if (row['status'] != 'PENDING'
            or not row['reason'].startswith('online_gift_current_tier_not_due:')
            or row.get('cleanup') != 'VERIFIED_CLOSED' or row.get('cleanup_pending') is not False
            or row.get('request_issued') is not False or row.get('reply_received') is not False
            or row.get('processed_without_reply') is not False or row.get('page_confirmed') is not False
            or type(row.get('got_before')) is not int or row.get('got_after') != row['got_before']):
        return False
    expected = dict(route, status='PARTIAL', reason='online_gift_current_tier_not_due',
                    remaining_seconds=0.0, ready_at=0.0, requires_online=False, cleanup='')
    return after['route_results'] == {'online_gift': expected}


def validate_snapshot(value):
    """Return a deep, strict v1 copy, without migration or credential fields.

    ``profiles`` freeze every noncredential profile field except login_name and
    password. Queues contain profile UUIDs, not unstable list indexes. Dispatch
    records contain account_id, task, dispatch_id, state, result_status. Phased
    daily records additionally freeze their selection, per-route results and
    raw item rows. A normal batch may reread one warmup PARTIAL route only with
    a validated zero-command witness and retry_of pointing at that exact source;
    the original dispatch and evidence remain immutable. A repeat normal retry,
    UNKNOWN route, or potentially admitted command is never accepted.
    Recovery is exactly {attempts: {profile_uuid: count}, limit}, with every
    selected profile represented and a fixed maximum limit of ten per account.
    """
    _object(value, _FIELDS)
    if type(value['schema_version']) is not int or value['schema_version'] != 1:
        _fail('version_unsupported')
    _hex(value['run_id'])
    if type(value['control']) is not str or value['control'] not in _CONTROLS:
        _fail('control_invalid')
    _text(value['phase'], empty=False, limit=128)
    profiles = _profiles(value['profiles'])
    ids = {p['id'] for p in profiles}
    tasks = _records(value['records'], profiles)
    for key in ('pending_accounts', 'deferred_campaigns'):
        queue = value[key]
        if (type(queue) is not list or any(type(item) is not str or item not in ids for item in queue)
                or len(queue) != len(set(queue))):
            _fail('queue_invalid')
    if not set(value['deferred_campaigns']) <= set(value['pending_accounts']):
        _fail('deferred_queue_invalid')
    current = _text(value['current_account'], limit=36)
    task_key = _text(value['current_task'], limit=32)
    if (current and current not in ids or task_key and (not current or task_key not in TASK_ORDER)):
        _fail('current_identity_invalid')
    _integer(value['task_index'], 0, len(TASK_ORDER))
    _boolean(value['campaign_pass'])
    recovery = _object(value['recovery'], {'attempts', 'limit'})
    _integer(recovery['limit'], 0, 10)
    attempts = _object(recovery['attempts'], ids)
    for count in attempts.values():
        _integer(count, 0, recovery['limit'])
    dispatches = value['dispatches']
    if type(dispatches) is not list or len(dispatches) > _MAX_RECORDS:
        _fail('dispatches_invalid')
    seen, latest, unknown, unresolved = set(), {}, set(), []
    latest_routes = {}
    profile_map = {profile['id']: profile for profile in profiles}
    for dispatch in dispatches:
        _object(dispatch, {'account_id', 'task', 'dispatch_id', 'state', 'result_status'}, _PHASE_FIELDS | _RETRY_FIELDS)
        identity = dispatch['account_id'], dispatch['task']
        if (any(type(item) is not str for item in identity) or identity not in tasks
                or tasks[identity]['status'] == 'SKIPPED'):
            _fail('dispatch_identity_invalid')
        identifier = _hex(dispatch['dispatch_id'])
        phased = bool(set(dispatch) & _PHASE_FIELDS)
        if set(dispatch) & _RETRY_FIELDS and not phased:
            _fail('daily_retry_without_phase')
        if identifier in seen or identity in unknown:
            _fail('dispatch_reused_or_unknown_replayed')
        seen.add(identifier)
        state, result = dispatch['state'], dispatch['result_status']
        if type(state) is not str or type(result) is not str:
            _fail('dispatch_state_invalid')
        if state == 'DISPATCHING':
            if result != '':
                _fail('dispatch_result_invalid')
            unresolved.append(identity)
        elif state == 'COMMITTED':
            if result not in _RESULTS:
                _fail('dispatch_result_invalid')
        elif state == 'UNKNOWN':
            if result != 'UNKNOWN':
                _fail('dispatch_result_invalid')
            if not phased:
                unknown.add(identity)
        else:
            _fail('dispatch_state_invalid')
        if phased:
            _phased_daily(dispatch, tasks[identity], profile_map[identity[0]], latest_routes, value['run_id'])
        else:
            previous = latest.get(identity)
            if previous and (previous['state'] != 'COMMITTED' or previous['result_status'] != 'WAITING'):
                _fail('task_replayed')
        latest[identity] = dispatch
    if len(unresolved) > 1:
        _fail('parallel_dispatches')
    for identity, dispatch in latest.items():
        if 'selected_items' in dispatch:
            continue
        status = tasks[identity]['status']
        if dispatch['state'] == 'DISPATCHING':
            if identity != (current, task_key) or status not in ('RUNNING', 'DISPATCHING'):
                _fail('dispatch_boundary_missing')
        elif dispatch['state'] == 'UNKNOWN':
            if status != 'UNKNOWN':
                _fail('unknown_task_requeued')
        elif status != dispatch['result_status'] and not (dispatch['result_status'] == 'WAITING' and status == 'QUEUED'):
            _fail('committed_result_changed')
    for identity, task in tasks.items():
        if 'routes' in task:
            _daily_projection(task, identity[0], latest_routes, current, task_key)
            continue
        if task['status'] in ('RUNNING', 'DISPATCHING') and (
                identity not in latest or latest[identity]['state'] != 'DISPATCHING'):
            _fail('task_without_write_ahead_dispatch')
    # Serialize before deepcopy to reject oversized input and non-JSON objects.
    _bytes(value)
    return copy.deepcopy(value)


class AccountRecoveryStore:
    """One process-owned round journal. No fallback history or automatic retry.

    ``acquire`` raises on contention; every load/commit requires its OS lock.
    A write or readback failure poisons this ownership instance until release
    and reacquire. Callers must stop dispatch immediately on any store error.
    ``acquire(fresh=True)`` starts an explicitly requested new round without
    reading old task states or requiring an old round to be terminal. Old
    per-run files remain untouched, including UNKNOWN evidence. The OS lock and
    anchor metadata fence exclude concurrent changes before the first new
    commit. Subsequent commits verify this round's own anchor and contents.
    """

    SCHEMA_VERSION = 1
    validate_snapshot = staticmethod(validate_snapshot)

    def __init__(self, data_dir):
        self.directory = Path(data_dir) / 'account-round-recovery'
        self.path = self.directory / 'latest.json'
        self.lock_path = self.directory / 'ownership.lock'
        self._mutex = threading.RLock()
        self._lock = None
        self._pid = None
        self._lock_identity = None
        self._baseline = None
        self._snapshot = None
        self._generation = 0
        self._failed = False
        self._recovered_unknowns = set()
        self._fresh_round = False

    @property
    def owned(self):
        return self._lock is not None and self._pid == os.getpid() and not self._failed

    def path_for(self, run_id):
        return self.directory / (_hex(run_id) + '.json')

    def acquire(self, *, fresh=False):
        """Acquire round ownership; fresh admission never loads old progress."""
        with self._mutex:
            if self._lock is not None:
                self._assert_owned()
                if fresh:
                    _fail('fresh_round_ownership_already_held')
                return
            descriptor = None
            try:
                self.directory.mkdir(parents=True, exist_ok=True)
                if self.directory.is_symlink():
                    _fail('directory_unsafe')
                flags = os.O_RDWR | os.O_CREAT | getattr(os, 'O_NOFOLLOW', 0)
                descriptor = os.open(self.lock_path, flags, 0o600)
                info = os.fstat(descriptor)
                if not stat.S_ISREG(info.st_mode) or info.st_nlink != 1:
                    _fail('lock_unsafe')
                # Windows byte-range locking permits a byte past EOF; writing
                # the byte first would race a competing owner of an empty file.
                if os.name == 'nt':
                    import msvcrt
                    os.lseek(descriptor, 0, os.SEEK_SET)
                    msvcrt.locking(descriptor, msvcrt.LK_NBLCK, 1)
                else:
                    import fcntl
                    fcntl.flock(descriptor, fcntl.LOCK_EX | fcntl.LOCK_NB)
                self._lock = descriptor
                descriptor = None
                self._pid = os.getpid()
                self._lock_identity = info.st_dev, info.st_ino
                self._failed = False
                self._recovered_unknowns.clear()
                self._fresh_round = fresh
                if fresh:
                    # The initial fence is metadata only. A fresh round never
                    # opens the old anchor or a previous run's contents.
                    self._snapshot, self._generation = None, 0
                    self._baseline = self._metadata(self.path)
                else:
                    self._snapshot, self._generation, self._baseline = self._read_pair()
                self._assert_owned()
            except (OSError, AccountRecoveryError):
                if descriptor is not None:
                    os.close(descriptor)
                self.release()
                raise AccountRecoveryError('account_recovery_ownership_or_data_unavailable') from None

    def release(self):
        """Release ownership; the persistent lock file must never be deleted."""
        with self._mutex:
            descriptor, self._lock = self._lock, None
            if descriptor is not None:
                try:
                    # A fork must not unlock the parent's flock/open-file state.
                    if self._pid == os.getpid():
                        if os.name == 'nt':
                            import msvcrt
                            os.lseek(descriptor, 0, os.SEEK_SET)
                            msvcrt.locking(descriptor, msvcrt.LK_UNLCK, 1)
                        else:
                            import fcntl
                            fcntl.flock(descriptor, fcntl.LOCK_UN)
                except OSError:
                    # Closing the descriptor also releases the kernel lock.
                    pass
                finally:
                    os.close(descriptor)
            self._pid = self._lock_identity = self._baseline = self._snapshot = None
            self._failed = False
            self._recovered_unknowns.clear()
            self._fresh_round = False

    def _assert_owned(self):
        if not self.owned:
            _fail('ownership_required_or_failed')
        try:
            info = self.lock_path.lstat()
            if (not stat.S_ISREG(info.st_mode) or info.st_nlink != 1
                    or (info.st_dev, info.st_ino) != self._lock_identity):
                self._failed = True
                _fail('ownership_fence_changed')
        except OSError:
            self._failed = True
            _fail('ownership_fence_unavailable')

    @staticmethod
    def _metadata(path):
        try:
            info = path.lstat()
        except FileNotFoundError:
            return None
        if not stat.S_ISREG(info.st_mode) or info.st_nlink != 1:
            _fail('file_unsafe')
        return (info.st_dev, info.st_ino, info.st_mode, info.st_nlink, info.st_size,
                info.st_mtime_ns, info.st_ctime_ns)

    @staticmethod
    def _read(path):
        try:
            if path.is_symlink():
                _fail('file_unsafe')
            descriptor = os.open(path, os.O_RDONLY | getattr(os, 'O_NOFOLLOW', 0))
        except FileNotFoundError:
            return None
        with os.fdopen(descriptor, 'rb') as stream:
            info = os.fstat(stream.fileno())
            if not stat.S_ISREG(info.st_mode) or info.st_nlink != 1 or info.st_size > _MAX_BYTES:
                _fail('file_unsafe_or_too_large')
            raw = stream.read(_MAX_BYTES + 1)
        if len(raw) > _MAX_BYTES:
            _fail('size_limit')
        return raw

    def _read_pair(self):
        raw_anchor = self._read(self.path)
        if raw_anchor is None:
            if any(re.fullmatch('[0-9a-f]{32}\\.json', p.name) for p in self.directory.iterdir()):
                _fail('anchor_missing')
            return None, 0, None
        anchor = _object(_decode(raw_anchor), {'schema_version', 'run_id', 'generation', 'sha256'})
        if type(anchor['schema_version']) is not int or anchor['schema_version'] != 1:
            _fail('anchor_version_invalid')
        _hex(anchor['run_id'])
        _integer(anchor['generation'], 1)
        if type(anchor['sha256']) is not str or re.fullmatch('[0-9a-f]{64}', anchor['sha256']) is None:
            _fail('anchor_hash_invalid')
        raw = self._read(self.path_for(anchor['run_id']))
        if raw is None or hashlib.sha256(raw).hexdigest() != anchor['sha256']:
            _fail('record_missing_or_anchor_mismatch')
        snapshot = validate_snapshot(_decode(raw))
        if snapshot['run_id'] != anchor['run_id']:
            _fail('record_run_mismatch')
        return snapshot, anchor['generation'], (raw_anchor, raw)

    def _unchanged(self):
        self._assert_owned()
        try:
            if self._fresh_round:
                if self._metadata(self.path) != self._baseline:
                    _fail('external_change')
                return None, 0
            snapshot, generation, baseline = self._read_pair()
            if baseline != self._baseline:
                _fail('external_change')
            return snapshot, generation
        except (OSError, AccountRecoveryError):
            self._failed = True
            raise AccountRecoveryError('account_recovery_read_or_fence_failed') from None

    def load(self):
        """Return a conservative recovery copy; never rewrites or queues work.

        A surviving DISPATCHING or online WAITING entry makes the control and
        matching account/task UNKNOWN. This projection must be committed
        explicitly if retained. Once observed by this owner it cannot be changed
        back to DISPATCHING or WAITING either.
        Do not call this recovery API merely to poll an actively executing task.
        """
        with self._mutex:
            snapshot, _generation = self._unchanged()
            if snapshot is None:
                return None
            result = copy.deepcopy(snapshot)
            unknowns = set()
            tasks = {(record['id'], task['key']): task for record in result['records'] for task in record['tasks']}
            by_dispatch = {dispatch['dispatch_id']: dispatch for dispatch in result['dispatches']}
            for identity, task in tasks.items():
                if 'routes' not in task:
                    continue
                for name, route in task['routes'].items():
                    if route['status'] not in ('DISPATCHING', 'RUNNING', 'WAITING'):
                        continue
                    dispatch = by_dispatch[route['dispatch_id']]
                    self._recovered_unknowns.add(dispatch['dispatch_id'])
                    route.update(status='UNKNOWN', reason='上次在线等待或派发的连续性未知；不会自动重放',
                                 remaining_seconds=0.0, ready_at=0.0, requires_online=False, cleanup='')
                    dispatch['route_results'][name] = copy.deepcopy(route)
                    dispatch.update(state='UNKNOWN', result_status='UNKNOWN')
                    unknowns.add(identity)
            for dispatch in result['dispatches']:
                if 'selected_items' in dispatch:
                    continue
                if (dispatch['state'] == 'DISPATCHING'
                        or dispatch['state'] == 'COMMITTED' and dispatch['result_status'] == 'WAITING'):
                    self._recovered_unknowns.add(dispatch['dispatch_id'])
                    dispatch.update(state='UNKNOWN', result_status='UNKNOWN')
                    unknowns.add((dispatch['account_id'], dispatch['task']))
            if unknowns:
                result['control'] = 'UNKNOWN'
                for record in result['records']:
                    for task in record['tasks']:
                        if (record['id'], task['key']) in unknowns:
                            task.update(status='UNKNOWN', reason='上次任务已进入提交边界，结果未确认；不会自动重放')
                            record.update(status='UNKNOWN', reason='上次任务结果未确认')
            return validate_snapshot(result)

    def _all_unresolved(self):
        """Read all run evidence under ownership; one invalid run blocks use."""
        result = []
        for path in sorted(self.directory.iterdir()):
            if re.fullmatch('[0-9a-f]{32}\\.json', path.name) is None:
                continue
            raw = self._read(path)
            if raw is None:
                _fail('historical_record_disappeared')
            snapshot = validate_snapshot(_decode(raw))
            if path.name != snapshot['run_id'] + '.json':
                _fail('historical_run_identity_invalid')
            profiles = {p['id']: p for p in snapshot['profiles']}
            latest = {(d['account_id'], d['task']): d for d in snapshot['dispatches']}
            for record in snapshot['records']:
                for task in record['tasks']:
                    dispatch = latest.get((record['id'], task['key']))
                    route_pending = [route for route in task.get('routes', {}).values()
                                     if route['status'] in ('DISPATCHING', 'RUNNING', 'WAITING', 'UNKNOWN')]
                    if 'routes' in task:
                        if not route_pending:
                            continue
                        dispatch = next((entry for entry in snapshot['dispatches']
                                         if entry['dispatch_id'] == route_pending[0]['dispatch_id']), None)
                    unresolved = bool(route_pending) or dispatch is not None and (dispatch['state'] in ('DISPATCHING', 'UNKNOWN')
                        or dispatch['state'] == 'COMMITTED' and dispatch['result_status'] == 'WAITING')
                    if not unresolved and task['status'] not in ('DISPATCHING', 'RUNNING', 'UNKNOWN'):
                        continue
                    result.append({'run_id': snapshot['run_id'], 'account_id': record['id'],
                        'task': task['key'], 'dispatch_id': dispatch['dispatch_id'] if dispatch else '',
                        'state': 'UNKNOWN', 'profile': copy.deepcopy(profiles[record['id']]),
                        'observed_role_id': record['role_id'],
                        'observed_identity': copy.deepcopy(record.get('observed_identity')),
                        'source_state': dispatch['state'] if dispatch else 'UNKNOWN',
                        'result_status': dispatch['result_status'] if dispatch else 'UNKNOWN'})
        return result

    def unresolved(self):
        """Return UNKNOWN account/task evidence from every retained run.

        Results contain run_id, account_id, task, dispatch_id (or empty), state,
        frozen noncredential profile, observed_role_id, observed_identity (or
        None), source_state, and result_status. DISPATCHING and COMMITTED WAITING
        are conservatively reported as UNKNOWN. No legacy display history is read.
        The controller must also check aliases of the same game identity and
        must never infer a terminal result from this local journal alone.
        """
        with self._mutex:
            self._unchanged()
            try:
                return self._all_unresolved()
            except (OSError, AccountRecoveryError):
                self._failed = True
                raise AccountRecoveryError('account_recovery_unresolved_evidence_unavailable') from None

    @staticmethod
    def _started_task_boundary(previous, candidate, account_id):
        """A retained write-ahead dispatch was accepted after session recovery."""
        if (previous['control'] != 'RUNNING' or candidate['control'] != 'RUNNING'
                or previous['phase'] != 'next_task' or candidate['phase'] != 'task'
                or previous['current_account'] != account_id or candidate['current_account'] != account_id
                or not previous['dispatches'] or previous['dispatches'] != candidate['dispatches']):
            return False
        dispatch = candidate['dispatches'][-1]
        key = dispatch['task']
        if (dispatch['account_id'] != account_id or dispatch['state'] != 'DISPATCHING'
                or previous['current_task'] != key or candidate['current_task'] != key):
            return False
        before = next(task for record in previous['records'] if record['id'] == account_id
                      for task in record['tasks'] if task['key'] == key)
        after = next(task for record in candidate['records'] if record['id'] == account_id
                     for task in record['tasks'] if task['key'] == key)
        if 'selected_items' in dispatch:
            return all(before['routes'][name]['status'] == 'DISPATCHING'
                       and after['routes'][name]['status'] == 'RUNNING'
                       and before['routes'][name]['dispatch_id'] == dispatch['dispatch_id']
                       and after['routes'][name]['dispatch_id'] == dispatch['dispatch_id']
                       for name in dispatch['selected_items'])
        return before['status'] == 'DISPATCHING' and after['status'] == 'RUNNING'

    def _transition(self, previous, candidate):
        if previous is None or previous['run_id'] != candidate['run_id']:
            if previous is not None and previous['control'] not in TERMINAL_CONTROLS:
                _fail('previous_round_not_terminal')
            if self.path_for(candidate['run_id']).exists():
                _fail('run_id_reused')
            if candidate['dispatches'] or any(candidate['recovery']['attempts'].values()):
                _fail('new_round_not_empty')
            return
        if previous['profiles'] != candidate['profiles']:
            _fail('frozen_plan_changed')
        recovered_terminal_unknown = (candidate['control'] == 'UNKNOWN' and bool(self._recovered_unknowns))
        if (previous['control'] in TERMINAL_CONTROLS and candidate['control'] != previous['control']
                and not recovered_terminal_unknown
                or previous['control'] == 'STOPPING' and candidate['control'] in ('RUNNING', 'PAUSED')):
            _fail('control_reopened')
        old_recovery, recovery = previous['recovery'], candidate['recovery']
        if recovery['limit'] != old_recovery['limit']:
            _fail('recovery_budget_reset')
        for account_id, count in old_recovery['attempts'].items():
            updated = recovery['attempts'][account_id]
            reset_after_start = (count > 0 and updated == 0
                                 and self._started_task_boundary(previous, candidate, account_id))
            if not count <= updated <= count + 1 and not reset_after_start:
                _fail('recovery_budget_reset')
        old, new = previous['dispatches'], candidate['dispatches']
        if len(new) < len(old) or len(new) > len(old) + 1:
            _fail('dispatch_history_changed')
        for before, after in zip(old, new):
            if (any(before[key] != after[key] for key in ('account_id', 'task', 'dispatch_id'))
                    or any(before.get(key) != after.get(key) for key in ('phase', 'selected_items', 'retry_of'))):
                _fail('dispatch_identity_changed')
            waiting_to_unknown = (before['state'] == 'COMMITTED' and before['result_status'] == 'WAITING'
                and 'selected_items' not in before
                and after['state'] == 'UNKNOWN' and after['result_status'] == 'UNKNOWN')
            if (before['state'] != 'DISPATCHING' and before != after and not waiting_to_unknown
                    and not _retired_wait(before, after) and not _finished_gift_observation(before, after)):
                _fail('terminal_dispatch_changed')
            if (before['state'] == 'DISPATCHING' and after['state'] == 'DISPATCHING' and before != after
                    or before['dispatch_id'] in self._recovered_unknowns and after['state'] != 'UNKNOWN'):
                _fail('unresolved_dispatch_replayed')
        if len(new) > len(old):
            if (new[-1]['state'] != 'DISPATCHING' or candidate['control'] != 'RUNNING'
                    or previous['control'] not in ('RUNNING', 'PAUSED')
                    or any(item['state'] == 'DISPATCHING' or item['state'] == 'UNKNOWN'
                           and 'selected_items' not in item
                           and (item['account_id'], item['task']) ==
                               (new[-1]['account_id'], new[-1]['task']) for item in old)):
                _fail('dispatch_not_admitted')
        old_records = {r['id']: r for r in previous['records']}
        for record in candidate['records']:
            old_record = old_records[record['id']]
            if [task['key'] for task in old_record['tasks']] != [task['key'] for task in record['tasks']]:
                _fail('task_order_changed')
            if old_record['role_id'] and old_record['role_id'] != record['role_id']:
                _fail('observed_role_changed')
            if ('observed_identity' in old_record
                    and old_record['observed_identity'] != record.get('observed_identity')):
                _fail('observed_identity_changed')
            for before, after in zip(old_record['tasks'], record['tasks']):
                if 'routes' in before:
                    if set(before['routes']) != set(after.get('routes', {})):
                        _fail('daily_routes_changed')
                    for name, route in before['routes'].items():
                        if (route['status'] in ('UNKNOWN', 'COMPLETED', 'PROCESSED')
                                and after['routes'][name] != route):
                            _fail('daily_terminal_route_changed')
                if before['status'] == 'UNKNOWN' and after['status'] != 'UNKNOWN':
                    _fail('unknown_task_requeued')
                if before['status'] == 'COMPLETED' and after['status'] != 'COMPLETED':
                    _fail('completed_task_requeued')

    def _atomic_bytes(self, path, raw):
        name = None
        try:
            descriptor, name = tempfile.mkstemp(prefix='.' + path.name + '.', suffix='.tmp', dir=self.directory)
            with os.fdopen(descriptor, 'wb') as stream:
                stream.write(raw)
                stream.flush()
                os.fsync(stream.fileno())
            self._assert_owned()
            os.replace(name, path)
            name = None
            if os.name != 'nt':
                descriptor = os.open(self.directory, os.O_RDONLY | getattr(os, 'O_DIRECTORY', 0))
                try:
                    os.fsync(descriptor)
                finally:
                    os.close(descriptor)
        finally:
            if name is not None:
                try:
                    os.unlink(name)
                except OSError:
                    pass

    def commit(self, snapshot):
        """Durably commit one protocol-valid snapshot or raise and admit no I/O.

        Validate the supplied copy and current on-disk pair under the retained
        lock. Commit a DISPATCHING record before action; commit COMMITTED only
        after authoritative task completion. The store validates that protocol,
        but the controller remains responsible for the truth of result_status.
        """
        with self._mutex:
            self._assert_owned()
            candidate = validate_snapshot(snapshot)
            previous, generation = self._unchanged()
            try:
                self._transition(previous, candidate)
            except OSError:
                self._failed = True
                raise AccountRecoveryError('account_recovery_history_unavailable_no_further_actions') from None
            raw = _bytes(candidate)
            anchor = {'schema_version': 1, 'run_id': candidate['run_id'],
                      'generation': generation + 1, 'sha256': hashlib.sha256(raw).hexdigest()}
            try:
                # A torn pair is deliberately blocked rather than rolling back
                # to an older queued task after a possible external invocation.
                self._atomic_bytes(self.path, _bytes(anchor))
                self._atomic_bytes(self.path_for(candidate['run_id']), raw)
                self._assert_owned()
                committed, actual_generation, baseline = self._read_pair()
                if committed != candidate or actual_generation != generation + 1:
                    _fail('readback_mismatch')
                self._snapshot = committed
                self._generation = actual_generation
                self._baseline = baseline
                self._fresh_round = False
                return copy.deepcopy(committed)
            except (OSError, AccountRecoveryError):
                self._failed = True
                raise AccountRecoveryError('account_recovery_commit_failed_no_further_actions') from None
