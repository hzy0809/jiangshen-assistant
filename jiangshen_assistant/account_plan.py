"""Serial per-account scheduling over the existing game coordinators."""
from __future__ import annotations

import copy
import json
import math
import os
from pathlib import Path
import stat
import tempfile
import time
import uuid

from .account_profiles import AccountProfileError, TASK_CATALOG, validate_document
from .account_recovery_store import (AccountRecoveryError, AccountRecoveryStore,
                                     validate_daily_idle_release, validate_daily_retry_evidence)
from .development import EquipmentTarget, StarTarget, TributeTarget
from .event_stream import EventMailbox
from .mono_runtime import TargetGone, identity_for, process_creation, process_status


# Growth and its reward are part of campaign, not separate plan operations.
# Legacy profile fields remain readable but do not enable a campaign by themselves.
_ORDER = ('campaign', 'challenge', 'elite', 'daily', 'soul', 'tribute', 'equipment', 'star')
_BAD_IDENTITY = ('_expected_character_', '_expected_user_', '_expected_zone_',
                 '_expected_identity_', 'character_changed', 'identity_changed', 'context_changed')
_RECOVERY_LIMIT = 2
_CLEANUP_SECONDS = 45.0
_EARLY_DAILY = frozenset(('copy', 'online_reward', 'online_gift', 'training', 'arena', 'city_shop', 'trial', 'point_race'))
_DAILY_CHECK_SECONDS = 60.0
_DAILY_WAIT_ROWS = {'copy': (7, 303), 'online_reward': (7, 1201), 'online_gift': (7, 309),
                    'training': (4, 23), 'arena': (3, 9),
                    'trial': (4, 14), 'point_race': (3, 109)}
_DAILY_RETRYABLE_WARMUP = frozenset(('copy', 'training', 'arena', 'city_shop', 'trial', 'point_race'))
_DAILY_CONTEXT_RETRYABLE = frozenset(('guild_donation', 'divinity', 'mingshi'))


class AccountPlanCoordinator:
    """Run one operation at a time on the Tk thread and never expose secrets."""

    def __init__(self, app):
        self.app = app
        self.events = EventMailbox('account_plan')
        self._last_event_header = None
        self.active = False
        self.paused = False
        self._stopping = False
        self._profiles = []
        self._records = []
        self._pending_accounts = []
        self._deferred_campaigns = set()
        self._observed_identities = {}
        self._campaign_pass = False
        self._growth_only = False
        self._point_reward_only = False
        self._login_only = False
        self._wait_for_gift = False
        self._gift_wait_until = None
        self._index = -1
        self._task_index = 0
        self._current_task = ''
        self._coordinator = None
        self._driver = None
        self._title_driver = None
        self._title_attempted = False
        self._title_identity = None
        self._game_deadline = 0.0
        self._launcher_pause_pending = False
        self._paused_at = 0.0
        self._switch_closer = None
        self._switch_window = None
        self._switch_identity = None
        self._switch_disconnect_requested = False
        self._switch_last_state = None
        self._owned_game = False
        self._ready_game = None
        self._phase = 'idle'
        self._deadline = 0.0
        self._wait_until = 0.0
        self._waiting_daily = False
        self._daily_waits = {}
        self._daily_last_checks = {}
        self._daily_due = set()
        self._daily_next_scan = 0.0
        self._daily_resume_due = False
        self._daily_selection = frozenset()
        self._daily_dispatch_phase = ''
        self._cleanup_requested = False
        self._cleanup_after_unknown = False
        self._cleanup_disconnect_requested = False
        self._human_required = False
        self._human_code = ''
        self._login_human_assisted = False
        self._run_id = ''
        self._recovery_store = AccountRecoveryStore(app.data_dir)
        self._recovery_locked = False
        self._persistence_failed = False
        self._persistence_error = ''
        self._dispatches = []
        self._frozen_profiles = []
        self._unknown_accounts = []
        self._session_game = None
        self._login_processes = {}
        self._recovery_attempts = {}
        self._recovery_target = None
        self._recovery_skip_account = False
        self._recovery_continue_next = False
        self._recovery_driver_cleanup = False
        self._recovery_binding_cleared = False
        self._recovery_not_before = 0.0
        self._stop_deadline = 0.0
        self._stop_failure = ''
        self._stale_bound = None
        self._task_cleanup_deadline = 0.0
        self._task_requires_rebuild = False
        self._last_serialized = ''
        self._result_path = Path(app.data_dir) / 'account-plan-last.json'
        self._history_path = Path(app.data_dir) / 'account-plan-history'
        self._snapshot = {'active': False, 'paused': False, 'status': 'IDLE',
                          'reason': '', 'current_account': '', 'current_task': '', 'accounts': []}
        # Initial display waits for this application's new round. Opening the
        # application is not authorization to read an earlier round's contents.

    @property
    def is_active(self):
        return self.active

    @property
    def is_paused(self):
        return self.paused

    @property
    def title_worker_pending(self):
        return self._title_driver is not None and self._title_driver.poll().get('phase') != 'DONE'

    @property
    def workflow_account(self):
        profile = self._profiles[self._index] if 0 <= self._index < len(self._profiles) else {}
        return {'id': profile.get('id', ''), 'label': profile.get('label', '当前账号')}

    @property
    def workflow_session_ready(self):
        return not self.active or (not self._stopping and self._phase in
                                   ('next_task', 'task', 'task_cleanup', 'cooldown'))

    @property
    def snapshot(self):
        return copy.deepcopy(self._snapshot)

    @property
    def scheduling_phase(self):
        """Expose the scheduling boundary without exposing account documents."""
        return 'stopping' if self._stopping else 'paused' if self.paused else self._phase

    def next_wakeup_delay(self):
        """Supervision fallback; source events can always request an earlier step.

        Launcher/exit workers do not yet publish lifecycle events, and process
        identity still needs liveness checks. Business timeouts belong to the
        activity. Only deadlines belonging to this phase are relevant here.
        """
        if not self.active and not self.title_worker_pending:
            return None
        now = time.monotonic()
        if self._stopping:
            deadline, interval = self._stop_deadline, .1
        elif self.paused:
            return .25 if self._launcher_pause_pending or self.title_worker_pending else 1.0
        elif self._phase == 'cooldown':
            if self._daily_resume_due or self._daily_due:
                return 0.0
            deadline, interval = self._daily_next_scan, 1.0
        elif self._phase == 'recovery_backoff':
            deadline, interval = self._recovery_not_before, 1.0
        elif self._phase == 'game_loading':
            deadline, interval = self._game_deadline, .25
        elif self._phase in ('identity', 'recovery_cleanup', 'stale_binding_cleanup',
                             'failed_session_prepare', 'cleanup', 'finish_run',
                             'login_failure_cleanup', 'switch_prepare'):
            deadline, interval = self._deadline, .25
        elif self._phase in ('task', 'task_cleanup'):
            # A deadline left by an earlier cleanup must not wake an active
            # new task continuously. Completion arrives through runner events.
            active = self.app.runner.is_active or (self._coordinator is not None
                                                   and self._coordinator.is_active)
            deadline = 0.0 if active else self._task_cleanup_deadline
            interval = 1.0 if active else .25
        else:
            deadline, interval = 0.0, .1
        if deadline > now:
            return min(interval, deadline - now)
        # Expired-but-retained fields cannot create a zero-delay busy loop.
        return interval

    @staticmethod
    def _exact_process(item):
        return (isinstance(item, dict) and type(item.get('pid')) is int and item['pid'] > 0
                and type(item.get('process_creation_filetime')) is int
                and item['process_creation_filetime'] > 0)

    def _stop_workers(self):
        """Close admissions without taking ownership away from a live worker."""
        actions = []
        if self._driver is not None:
            actions.append(lambda: self._driver.stop(close_owned=False))
        if self._title_driver is not None:
            actions.append(self._title_driver.stop)
        if self._switch_closer is not None:
            actions.append(self._switch_closer.stop)
        if self._coordinator is not None and self._coordinator.is_active:
            actions.append(self._coordinator.stop)
        actions.extend((self.app.runner.stop, self.app.objects.disconnect))
        for action in actions:
            try:
                action()
            except Exception:
                self._stop_failure = self._stop_failure or '旧工作线程的停止请求未确认'

    def _mark_dispatch_unknown(self, reason):
        if not 0 <= self._index < len(self._records):
            return
        profile, record = self._profiles[self._index], self._records[self._index]
        keys = set()
        daily = self._task_record('daily')
        routes = daily.get('routes', {})
        affected_routes = {name for name, route in routes.items()
                           if route['status'] in ('DISPATCHING', 'RUNNING', 'WAITING')}
        for name in affected_routes:
            route = routes[name]
            route.update(status='UNKNOWN', reason=reason, remaining_seconds=0.0, ready_at=0.0,
                         requires_online=False, cleanup='')
        if affected_routes:
            for dispatch in self._dispatches:
                if dispatch['account_id'] != profile['id'] or 'selected_items' not in dispatch:
                    continue
                affected = {name for name in affected_routes if routes[name]['dispatch_id'] == dispatch['dispatch_id']}
                if not affected:
                    continue
                if dispatch['state'] == 'DISPATCHING':
                    dispatch['route_results'] = {name: copy.deepcopy(routes[name]) for name in dispatch['selected_items']}
                else:
                    for name in affected:
                        dispatch['route_results'][name] = copy.deepcopy(routes[name])
                dispatch.update(state='UNKNOWN', result_status='UNKNOWN')
            self._daily_waits.pop(profile['id'], None)
            self._daily_due.clear()
            self._daily_status()
            keys.add('daily')
        for dispatch in self._dispatches:
            if 'selected_items' in dispatch:
                continue
            if dispatch['account_id'] == profile['id'] and dispatch['state'] == 'DISPATCHING':
                dispatch.update(state='UNKNOWN', result_status='UNKNOWN')
                keys.add(dispatch['task'])
            elif (dispatch['account_id'] == profile['id'] and dispatch['state'] == 'COMMITTED'
                  and dispatch['result_status'] == 'WAITING'
                  and self._task_record(dispatch['task'])['status'] != 'COMPLETED'):
                # A verified online cooldown is a boundary for the live
                # session, not permission to replay the daily bundle offline.
                keys.add(dispatch['task'])
        if self._current_task and self._current_task != 'daily':
            item = self._task_record(self._current_task)
            if item['status'] in ('DISPATCHING', 'RUNNING'):
                keys.add(self._current_task)
        if self._waiting_daily and not routes:
            keys.add('daily')
        for key in keys:
            if key == 'daily' and routes:
                continue
            self._task_record(key).update(status='UNKNOWN', reason=reason)
            for dispatch in reversed(self._dispatches):
                if dispatch['account_id'] == profile['id'] and dispatch['task'] == key:
                    dispatch.update(state='UNKNOWN', result_status='UNKNOWN')
                    break
        if keys:
            record.update(status='UNKNOWN', reason=reason)
            self._remember_unknown(profile, keys - ({'daily'} if routes else set()))
        return bool(keys)

    def _remember_unknown(self, profile, tasks):
        if not tasks:
            return
        entry = {'account_id': profile['id'], 'user_id': profile.get('user_id', ''),
                 'zone_id': profile.get('zone_id', ''), 'role_id': profile.get('role_id', ''),
                 'tasks': sorted(tasks)}
        if entry not in self._unknown_accounts:
            self._unknown_accounts.append(entry)

    def _unknown_tasks(self, profile, actual=None):
        """Return task fences; a profile label never establishes separation."""
        configured = tuple(profile.get(key, '') for key in ('user_id', 'zone_id', 'role_id'))
        observed = actual if actual is not None else configured if all(configured) else None
        tasks = set()
        for entry in self._unknown_accounts:
            if entry['account_id'] == profile['id']:
                tasks.update(entry['tasks'])
                continue
            if observed is None:
                # Recheck aliases after this login supplies the full identity;
                # prematurely writing UNKNOWN would irreversibly fence a
                # potentially distinct account in this round's journal.
                continue
            known = (entry.get('user_id'), entry.get('zone_id'), entry.get('role_id'))
            if not any(value and value != current for value, current in zip(known, observed)):
                tasks.update(entry['tasks'])
        return tasks

    def _fence_unknown_tasks(self, profile, record, actual=None):
        affected = self._unknown_tasks(profile, actual)
        for item in record['tasks']:
            if item['key'] in affected and item['status'] in ('QUEUED', 'WAITING'):
                item.update(status='UNKNOWN', reason='该任务本轮结果未知；保留证据且不重发，其他任务继续')

    def _has_pending_tasks(self, record):
        return any(item['status'] in ('QUEUED', 'WAITING') or
                   any(route['status'] in ('QUEUED', 'WAITING')
                       or self._context_retry_boundary(name, route, record['id']) is not None
                       for name, route in item.get('routes', {}).items())
                   for item in record['tasks'])

    @staticmethod
    def _daily_route(status='QUEUED', reason='', *, dispatch_id='', checked_at=0.0,
                     remaining_seconds=0.0, ready_at=0.0, requires_online=False, cleanup='', release_evidence=None,
                     processed_without_reply=False, page_confirmed=False):
        route = dict(status=status, reason=reason, dispatch_id=dispatch_id, checked_at=checked_at,
                     remaining_seconds=remaining_seconds, ready_at=ready_at,
                     requires_online=requires_online, cleanup=cleanup)
        if release_evidence is not None:
            route['release_evidence'] = copy.deepcopy(release_evidence)
        if processed_without_reply:
            route['processed_without_reply'] = True
        if page_confirmed:
            route['page_confirmed'] = True
        return route

    def _daily_status(self):
        item = self._task_record('daily')
        states = {route['status'] for route in item.get('routes', {}).values()}
        if 'UNKNOWN' in states:
            status = 'UNKNOWN'
        elif states & {'DISPATCHING', 'RUNNING'}:
            status = 'RUNNING' if 'RUNNING' in states else 'DISPATCHING'
        elif 'WAITING' in states:
            status = 'WAITING'
        elif 'QUEUED' in states:
            status = 'QUEUED'
        elif states == {'COMPLETED'}:
            status = 'COMPLETED'
        elif states and states <= {'COMPLETED', 'PROCESSED'}:
            status = 'PROCESSED'
        else:
            status = 'PARTIAL' if states else 'UNAVAILABLE'
        item['status'] = status
        # A finished subset cannot describe the entire plan while another
        # route is still waiting. Keep the summary aligned with all routes;
        # each route and dispatch retain their original reason and evidence.
        item['reason'] = {
            'UNKNOWN': 'daily_workflows_unknown', 'RUNNING': 'daily_workflows_running',
            'DISPATCHING': 'daily_workflows_running', 'QUEUED': 'daily_workflows_queued',
            'COMPLETED': 'daily_items_observed_complete',
            'PROCESSED': 'daily_items_processed_without_reply',
            'PARTIAL': 'daily_workflows_partial', 'UNAVAILABLE': 'daily_workflows_unavailable',
        }.get(status, 'daily_workflows_pending')
        if status == 'WAITING' and any(route.get('requires_online') is True
                                      or route.get('ready_at', 0) > 0
                                      for route in item.get('routes', {}).values()
                                      if route['status'] == 'WAITING'):
            item['reason'] = 'daily_cooldown_deferred'
        # Gift eligibility may be revisited while this account is already
        # online. It cannot create a new reason to keep the account online.
        self._waiting_daily = any(name != 'online_gift' for name in
                                  self._daily_waits.get(self._profiles[self._index]['id'], {}))
        return status

    def _finish_gift_observation(self):
        """Close an unsent gift wait when the account's other work ends."""
        profile = self._profiles[self._index]
        daily = self._task_record('daily')
        route = daily.get('routes', {}).get('online_gift')
        if route is None or route['status'] != 'WAITING':
            return
        # The committed wait came only from an unsent, verified closed tier.
        # Preserve that observation as PARTIAL; do not infer offline progress
        # or create another dispatch just to cancel a future check.
        finished = self._daily_route('PARTIAL', 'online_gift_current_tier_not_due',
            dispatch_id=route['dispatch_id'], checked_at=route['checked_at'])
        dispatch = next(value for value in self._dispatches if value['dispatch_id'] == route['dispatch_id'])
        dispatch['route_results']['online_gift'] = copy.deepcopy(finished)
        states = {value['status'] for value in dispatch['route_results'].values()}
        dispatch['result_status'] = ('UNKNOWN' if 'UNKNOWN' in states else
                                     'WAITING' if 'WAITING' in states else 'PARTIAL')
        dispatch['state'] = 'UNKNOWN' if dispatch['result_status'] == 'UNKNOWN' else 'COMMITTED'
        daily['routes']['online_gift'] = finished
        self._daily_waits.get(profile['id'], {}).pop('online_gift', None)
        self._daily_due.discard('online_gift')
        self._daily_status()

    def _scan_daily_waits(self, *, resume=False):
        """Read local retry hints only; executors must reread server state."""
        now = time.monotonic()
        if not resume and now < self._daily_next_scan:
            return
        self._daily_next_scan = now + _DAILY_CHECK_SECONDS
        account_id = self._profiles[self._index]['id']
        waits = self._daily_waits.get(account_id, {})
        checks = self._daily_last_checks.get(account_id, {})
        self._daily_due = {name for name, deadline in waits.items() if now >= deadline
                           and (resume or now >= checks.get(name, 0.0) + _DAILY_CHECK_SECONDS)}

    def _warmup_retry_source(self, name, route):
        """One normal-slot reread needs original proof, never a reason match."""
        if name not in _DAILY_RETRYABLE_WARMUP or route['status'] != 'PARTIAL':
            return None
        connection = self.app.objects.connection_status
        if (connection.get('state') != 'ready' or connection.get('owner') or connection.get('busy')
                or connection.get('prelogin_bridge') or connection.get('error')
                or self.app.objects.has_campaign_owner or self.app.runner.is_active):
            return None
        source = next((dispatch for dispatch in self._dispatches
                       if dispatch['dispatch_id'] == route['dispatch_id']), None)
        if (source is None or source.get('phase') != 'warmup' or source['state'] != 'COMMITTED'
                or source.get('route_results', {}).get(name) != route
                or name not in source.get('retry_evidence', {})):
            return None
        return source['dispatch_id']

    def _context_retry_boundary(self, name, route, account_id):
        """A saved opportunity belongs only to this account in this round."""
        if name not in _DAILY_CONTEXT_RETRYABLE or route['status'] != 'PARTIAL':
            return None
        source = next((value for value in self._dispatches if value['dispatch_id'] == route['dispatch_id']), None)
        if (source is None or source['account_id'] != account_id
                or source.get('phase') != 'normal' or source['state'] != 'COMMITTED'
                or source.get('retry_of') or source.get('route_results', {}).get(name) != route
                or source.get('retry_evidence', {}).get(name, {}).get('proof_kind') != 'untouched_native_route'):
            return None
        evidence = source['retry_evidence'][name]
        if evidence.get('account_run_id') != self._run_id or evidence.get('account_id') != account_id:
            return None
        if any(value.get('retry_of', {}).get(name) == source['dispatch_id'] for value in self._dispatches):
            return None
        return source

    def _context_retry_source(self, name, route):
        """One later attempt needs the untouched original dispatch's proof."""
        profile = self._profiles[self._index]
        if self.paused or self._stopping or not self._idle_wait_boundary_confirmed(profile):
            return None
        source = self._context_retry_boundary(name, route, profile['id'])
        if source is None:
            return None
        try:
            validate_daily_retry_evidence(source['retry_evidence'][name], route=name,
                expected_identity={key: profile[key] for key in ('user_id', 'zone_id', 'role_id')},
                expected_account_run=self._run_id, expected_account_id=profile['id'])
        except (AccountRecoveryError, TypeError, ValueError, OverflowError):
            return None
        return source['dispatch_id']

    def _retain_warmup_retry_evidence(self, data, dispatch, item):
        """Bind executor witnesses to the exact released session, or omit them."""
        evidence = data.get('route_retry_evidence')
        connection = self.app.objects.connection_status
        game = self._session_game
        profile = self._profiles[self._index]
        actual = self.app.objects.current_identity()
        expected = (profile['user_id'], profile['zone_id'], profile['role_id'])
        if (dispatch.get('phase') not in ('warmup', 'normal') or data.get('status') != 'PARTIAL'
                or not isinstance(evidence, (tuple, list)) or not evidence
                or connection.get('state') != 'ready' or connection.get('busy')
                or connection.get('owner') or connection.get('prelogin_bridge')
                or connection.get('error') or self.app.objects.has_campaign_owner
                or self.app.runner.is_active or data.get('cleanup_pending') is not False
                or self._ui_cleanup_unconfirmed(data) or not self._exact_process(game)
                or process_status(game['pid'], game['process_creation_filetime']) != 'same'
                or not isinstance(actual, dict)
                or tuple(actual.get(key) for key in ('user_id', 'zone_id', 'character_id')) != expected
                or actual.get('pid') != game['pid'] or actual.get('hwnd') != game.get('hwnd')
                or actual.get('process_creation_filetime') != game['process_creation_filetime']):
            return
        accepted, duplicate = {}, set()
        for witness in evidence:
            if not isinstance(witness, dict):
                continue
            name = witness.get('route')
            allowed = _DAILY_RETRYABLE_WARMUP if dispatch.get('phase') == 'warmup' else _DAILY_CONTEXT_RETRYABLE
            if (type(name) is not str or name not in allowed
                    or name not in dispatch['selected_items'] or item['routes'][name]['status'] != 'PARTIAL'):
                continue
            if dispatch.get('phase') == 'normal' and witness.get('proof_kind') != 'untouched_native_route':
                continue
            if witness.get('proof_kind') == 'untouched_native_route':
                witness = dict(witness, account_run_id=self._run_id, account_id=profile['id'])
            if name in accepted:
                duplicate.add(name)
            try:
                accepted[name] = validate_daily_retry_evidence(witness, route=name,
                    expected_identity=dict(zip(('user_id', 'zone_id', 'role_id'), expected)),
                    expected_session=game, expected_generation=connection.get('generation'),
                    expected_account_run=self._run_id, expected_account_id=profile['id'])
            except (AccountRecoveryError, TypeError, ValueError, OverflowError):
                duplicate.add(name)
        dispatch['retry_evidence'] = {name: witness for name, witness in accepted.items() if name not in duplicate}

    def _idle_wait_boundary_confirmed(self, profile):
        connection = self.app.objects.connection_status
        actual, game = self.app.objects.current_identity(), self._session_game
        return (connection.get('state') == 'ready' and not connection.get('owner')
                and not connection.get('busy') and not connection.get('prelogin_bridge')
                and not connection.get('error') and not self.app.objects.has_campaign_owner
                and not self.app.runner.is_active and self._exact_process(game)
                and process_status(game['pid'], game['process_creation_filetime']) == 'same'
                and isinstance(actual, dict)
                and tuple(actual.get(key) for key in ('user_id', 'zone_id', 'character_id')) ==
                    (profile['user_id'], profile['zone_id'], profile['role_id'])
                and actual.get('pid') == game['pid'] and actual.get('hwnd') == game.get('hwnd')
                and actual.get('process_creation_filetime') == game['process_creation_filetime'])

    @staticmethod
    def _display_rows(data):
        return [{'name': row.get('name', ''), 'kind': row.get('kind', ''), 'task_id': row.get('task_id'),
                 'category_id': row.get('category_id'), 'status': row.get('status', ''),
                 'reason': row.get('reason', ''), 'current': row.get('current'),
                 'required': row.get('required'), 'template_active': row.get('template_active'),
                 'ui_button_id': row.get('ui_button_id'), 'ui_button_unlocked': row.get('ui_button_unlocked'),
                 'entry_unlocked': row.get('entry_unlocked'),
                 **{key: row[key] for key in ('cleanup', 'cleanup_pending', 'operation_id',
                     'processed_without_reply', 'page_confirmed', 'request_issued', 'reply_received', 'continuation_safe',
                     'got_before', 'got_after', 'mana_before', 'mana_after', 'targets_initial', 'targets_confirmed') if key in row},
                 **({'evidence_path': row['evidence_path']} if row.get('evidence_path') else {})}
                for row in data.get('items', []) if row.get('status') != 'SKIPPED']

    @staticmethod
    def _merge_daily_rows(item, rows):
        # A subset snapshot cannot erase rows from another completed dispatch.
        merged = {(row.get('category_id'), row.get('task_id'), row['kind'], row['name']): row
                  for row in item['items']}
        for row in rows:
            merged[row.get('category_id'), row.get('task_id'), row['kind'], row['name']] = row
        item['items'] = list(merged.values())

    def _archive_result_history(self):
        """Preserve the old file by identity without opening its contents."""
        try:
            previous = self._result_path.lstat()
        except FileNotFoundError:
            return
        if not stat.S_ISREG(previous.st_mode):
            raise ValueError('历史账号记录不是常规文件')
        self._history_path.mkdir(parents=True, exist_ok=True)
        destination = self._history_path / ('retained-' + str(time.time_ns()) + '-' + uuid.uuid4().hex + '.json')
        try:
            os.link(self._result_path, destination, follow_symlinks=False)
        except OSError:
            # Same-volume rename also preserves the file without copying or
            # reading it when hard links are unavailable.
            os.rename(self._result_path, destination)
        retained = destination.lstat()
        if (not stat.S_ISREG(retained.st_mode) or
                (retained.st_dev, retained.st_ino, retained.st_size, retained.st_mtime_ns) !=
                (previous.st_dev, previous.st_ino, previous.st_size, previous.st_mtime_ns)):
            raise ValueError('历史账号记录文件身份发生变化')

    def _load_legacy_unknowns(self):
        paths = list(self._history_path.glob('*.json')) if self._history_path.exists() else []
        if self._result_path.exists():
            paths.append(self._result_path)
        for path in paths:
            prior = json.loads(path.read_text(encoding='utf-8'))
            if not isinstance(prior, dict) or not isinstance(prior.get('accounts'), list):
                raise ValueError('历史账号记录无法确认')
            for record in prior['accounts']:
                unknown = [item['key'] for item in record.get('tasks', [])
                           if item.get('status') in ('UNKNOWN', 'RUNNING', 'DISPATCHING')]
                if record.get('status') == 'UNKNOWN' and not unknown:
                    unknown = [item['key'] for item in record.get('tasks', [])
                               if item.get('status') not in ('COMPLETED', 'SKIPPED')]
                if unknown:
                    observed = record.get('observed_identity') or {}
                    self._remember_unknown({'id': record['id'],
                        'user_id': observed.get('user_id', ''), 'zone_id': observed.get('zone_id', ''),
                        'role_id': observed.get('role_id') or record.get('role_id', '')}, unknown)

    def _block_recovery(self, reason):
        self._stop_failure = reason
        self._mark_dispatch_unknown(reason)
        self._stopping, self.paused = True, False
        self._stop_deadline = time.monotonic() + _CLEANUP_SECONDS
        self._human_required, self._human_code = False, ''
        self._stop_workers()
        self._publish('STOPPING', reason)

    def _begin_recovery(self, target):
        # "gone" is an exact process boundary, not a diagnosis of its exit cause.
        # A person closing the game outside this plan is indistinguishable here.
        if not self._exact_process(target) or process_status(target['pid'], target['process_creation_filetime']) != 'gone':
            return False
        record = self._records[self._index]
        self._recovery_continue_next = self._phase == 'next_account'
        self._recovery_target = copy.deepcopy(target)
        self._recovery_skip_account = self._mark_dispatch_unknown('运行中游戏进程已退出，已派发操作的结果未知；不会重放')
        self._recovery_driver_cleanup = False
        self._recovery_binding_cleared = False
        self._waiting_daily = False
        self._human_required, self._human_code = False, ''
        self._deadline = time.monotonic() + _CLEANUP_SECONDS
        self._phase = 'recovery_cleanup'
        self._stop_workers()
        if not self._recovery_skip_account and not self._recovery_continue_next:
            record.update(status='WAITING', reason='运行中进程已退出，正在确认旧工作线程和连接已释放')
        self._publish('RECOVERING', record['reason'])
        return True

    def _observe_process_exit(self):
        if self._phase in ('next_account', 'game_loading', 'title_start', 'identity', 'next_task', 'task', 'task_cleanup', 'cooldown'):
            game = self._session_game
            if self._exact_process(game) and process_status(game['pid'], game['process_creation_filetime']) == 'gone':
                return self._begin_recovery(game)
        if self._phase != 'login' or self._driver is None:
            return False
        result = self._driver.poll().as_dict()
        owned = list(result.get('owned_processes') or [])
        if result.get('launcher'):
            owned.append(result['launcher'])
        if result.get('game'):
            owned.append(result['game'])
        for process in owned:
            if self._exact_process(process):
                self._login_processes[(process['pid'], process['process_creation_filetime'])] = copy.deepcopy(process)
        # The launcher is allowed to disappear after handing off to a game.
        if result.get('code') not in ('LAUNCHER_EXITED', 'GAME_EXITED'):
            return False
        games = [item for item in self._login_processes.values() if item.get('kind') == 'game']
        states = [process_status(item['pid'], item['process_creation_filetime']) for item in games]
        if any(state != 'gone' for state in states):
            return False
        if result.get('code') == 'GAME_EXITED':
            if games:
                self._session_game = copy.deepcopy(games[-1])
                return self._begin_recovery(games[-1])
            return False
        launchers = [item for item in self._login_processes.values() if item.get('kind') == 'launcher']
        if launchers and all(process_status(item['pid'], item['process_creation_filetime']) == 'gone' for item in launchers):
            return self._begin_recovery(launchers[0])
        return False

    def _recover_process(self):
        record = self._records[self._index]
        if time.monotonic() >= self._deadline:
            self._block_recovery('旧任务、登录助手或进程所有权的清理未确认，已停止自动恢复')
            return
        target = self._recovery_target
        if not self._exact_process(target) or process_status(target['pid'], target['process_creation_filetime']) != 'gone':
            self._block_recovery('原进程的退出身份无法确认，已停止自动恢复')
            return
        if self._title_driver is not None:
            if self._title_driver.poll().get('phase') != 'DONE':
                return
        status = self.app.objects.connection_status
        if ((self._coordinator is not None and self._coordinator.is_active) or self.app.runner.is_active
                or status.get('owner') or status.get('busy') or status.get('prelogin_bridge')):
            return
        # The dead process ends its native components, but never resolves a
        # dispatched server operation or deletes the underlying battle guard.
        if status.get('client_present') and not status.get('client_terminal'):
            self.app.objects.disconnect()
            return
        if not self._recovery_binding_cleared:
            bound = self.app.bound
            game = self._session_game
            if bound is not None and (not self._exact_process(game) or bound.process_id != game['pid']
                                      or bound.hwnd != game.get('hwnd')):
                self._block_recovery('发现其他游戏窗口，未接管或关闭该窗口；已停止自动恢复')
                return
            self.app._clear_binding()
            self._recovery_binding_cleared = True
            return
        if status.get('state') != 'disconnected' or status.get('window_present'):
            self.app.objects.unbind()
            return
        if self._driver is not None:
            if not self._recovery_driver_cleanup:
                self._driver.cleanup(cleanup_confirmed=True)
                self._recovery_driver_cleanup = True
                return
            result = self._driver.poll().as_dict()
            if result.get('human_required') or result.get('state') == 'BLOCKED':
                self._block_recovery(result.get('message') or '登录助手或己有进程尚未清理，已停止自动恢复')
                return
            if (result.get('state') != 'STOPPED' or result.get('cleanup_complete') is not True
                    or result.get('owned_processes')):
                return
        self._driver = self._coordinator = self._title_driver = None
        self._owned_game = False
        self._ready_game = self._session_game = None
        self._title_identity = None
        self._title_attempted = False
        self._login_processes.clear()
        self._current_task = ''
        self._recovery_target = None
        if self._recovery_continue_next:
            self._phase = 'next_account'
            self._publish('RUNNING', '已确认原游戏退出；保留已完成结果并继续待处理账号')
            return
        if not self._has_pending_tasks(record):
            self._finish_account()
            self._publish('RUNNING', '未知任务保持隔离；继续待处理账号')
            return
        account_id = self._profiles[self._index]['id']
        attempts = self._recovery_attempts.get(account_id, 0)
        if attempts >= _RECOVERY_LIMIT:
            record.update(status='UNKNOWN' if any(t['status'] == 'UNKNOWN' for t in record['tasks']) else 'BLOCKED',
                          reason='运行中进程反复退出，自动恢复次数已用尽；未完成任务保留原状态')
            self._phase = 'next_account'
            self._publish('RUNNING', record['reason'])
            return
        self._recovery_attempts[account_id] = attempts + 1
        self._recovery_not_before = time.monotonic() + (3.0 * (2 ** attempts))
        self._phase = 'recovery_backoff'
        record.update(status='WAITING', reason='进程清理已确认，准备自动恢复第 %d 次' % (attempts + 1))
        self._publish('RECOVERING', record['reason'])

    def _relaunch_account(self):
        if time.monotonic() < self._recovery_not_before:
            return
        profile, record = self._profiles[self._index], self._records[self._index]
        if not profile['login_name'] or not profile['password']:
            record.update(status='UNKNOWN' if any(t['status'] == 'UNKNOWN' for t in record['tasks']) else 'BLOCKED',
                          reason='缺少本轮账号凭据，无法重新登录')
            self._phase = 'next_account'
            self._publish('RUNNING', record['reason'])
            return
        if self.app.bound is not None:
            self._block_recovery('恢复等待期间出现其他游戏窗口，未接管该窗口')
            return
        try:
            from .launcher_login import LauncherLoginDriver
            self._driver = LauncherLoginDriver(diagnostics_dir=Path(self.app.data_dir) / 'session-cleanup')
            self._driver.begin(login_name=profile['login_name'], password=profile['password'],
                               server_name=profile['server_name'], server_tab=profile['server_tab'] or None)
        except Exception:
            self._block_recovery('重新启动登录流程失败，已停止自动恢复')
            return
        self._login_human_assisted = False
        record['login_state'] = 'CONNECTING'
        record.update(status='RUNNING', reason='正在重新登录原账号；已完成任务不会重跑')
        self._phase = 'login'
        self._publish('RECOVERING', record['reason'])

    def _publish(self, status=None, reason=None):
        account = self._records[self._index] if 0 <= self._index < len(self._records) else None
        self._snapshot = {
            'run_id': self._run_id, 'active': self.active, 'paused': self.paused,
            'growth_only': self._growth_only,
            'point_reward_only': self._point_reward_only,
            'login_only': self._login_only,
            'status': status or ('PAUSED' if self.paused else 'RUNNING' if self.active else self._snapshot.get('status', 'IDLE')),
            'reason': reason if reason is not None else self._snapshot.get('reason', ''),
            'current_account': account['id'] if account else '',
            'current_task': self._current_task,
            'human_required': self._human_required and self.active and not self.paused,
            'human_code': self._human_code if self._human_required else '',
            'recovery': {'attempts': dict(self._recovery_attempts), 'limit': _RECOVERY_LIMIT,
                         'phase': self._phase if self._phase.startswith('recovery_') else ''},
            'accounts': copy.deepcopy(self._records),
        }
        if self._persistence_error:
            self._snapshot['record_error'] = self._persistence_error
        if self._recovery_locked and not self._persistence_failed:
            control = ('STOPPING' if self._stopping and self.active else 'PAUSED' if self.paused and self.active
                       else 'RUNNING' if self.active else self._snapshot['status'])
            try:
                self._recovery_store.commit({
                    'schema_version': 1, 'run_id': self._run_id, 'control': control,
                    'phase': self._phase, 'profiles': copy.deepcopy(self._frozen_profiles),
                    'records': copy.deepcopy(self._records),
                    'pending_accounts': [self._profiles[index]['id'] for index in self._pending_accounts],
                    'deferred_campaigns': [self._profiles[index]['id'] for index in sorted(self._deferred_campaigns)],
                    'current_account': account['id'] if account else '', 'current_task': self._current_task,
                    'task_index': self._task_index, 'campaign_pass': self._campaign_pass,
                    'recovery': {'attempts': dict(self._recovery_attempts), 'limit': _RECOVERY_LIMIT},
                    'dispatches': copy.deepcopy(self._dispatches),
                })
            except Exception as error:
                # This commit might have reached disk. Never fall back to an
                # older unstarted queue, or admit another action after failure.
                # Only static store codes and numeric OS codes are retained;
                # exception messages may contain paths or account data.
                diagnostic = type(error).__name__
                if isinstance(error, AccountRecoveryError):
                    code = str(error)
                    if (len(code) <= 256 and code.startswith('account_recovery_')
                            and all(char in 'abcdefghijklmnopqrstuvwxyz0123456789_' for char in code)):
                        diagnostic += ':' + code
                elif isinstance(error, OSError):
                    for key in ('errno', 'winerror'):
                        value = getattr(error, key, None)
                        if type(value) is int:
                            diagnostic += ':' + key + '=' + str(value)
                self._persistence_error = diagnostic
                self._persistence_failed = True
                self._stop_failure = '账号恢复记录无法确认保存，已关闭后续操作准入（' + diagnostic + '）'
                self._stopping, self.paused = True, False
                self._stop_deadline = time.monotonic() + _CLEANUP_SECONDS
                self._mark_dispatch_unknown(self._stop_failure)
                self._stop_workers()
                self._snapshot.update(status='STOPPING' if self.active else 'UNKNOWN',
                                      reason=self._stop_failure, paused=False, human_required=False,
                                      accounts=copy.deepcopy(self._records), record_error=self._persistence_error)
                self._recovery_store.release()
                self._recovery_locked = False
        try:
            payload = json.dumps(self._snapshot, ensure_ascii=False, sort_keys=True, indent=2) + '\n'
            if payload != self._last_serialized:
                self._result_path.parent.mkdir(parents=True, exist_ok=True)
                with tempfile.NamedTemporaryFile('w', encoding='utf-8', dir=self._result_path.parent,
                                                 prefix='.account-plan-', suffix='.tmp', delete=False) as stream:
                    temporary = stream.name
                    stream.write(payload)
                    stream.flush()
                    os.fsync(stream.fileno())
                try:
                    os.replace(temporary, self._result_path)
                finally:
                    if os.path.exists(temporary):
                        os.unlink(temporary)
                self._last_serialized = payload
        except OSError:
            # Result persistence is not proof of execution and never admits work.
            self._snapshot['record_error'] = '运行结果无法保存'
        if not self.active and self._recovery_locked:
            self._recovery_store.release()
            self._recovery_locked = False
        header = (self._snapshot['status'], self._snapshot['reason'], self._phase, self._run_id)
        if header != self._last_event_header:
            self._last_event_header = header
            self.events.put({'status': header[0], 'reason': header[1],
                             'phase': header[2], 'run_id': header[3]})
        return not self._persistence_failed

    def start(self, document, *, growth_only=False, wait_for_gift=False, point_reward_only=False,
              login_only=False):
        if type(growth_only) is not bool:
            return False, '成长补做模式必须是布尔值。'
        if type(wait_for_gift) is not bool or growth_only and wait_for_gift:
            return False, '礼包等待模式必须是布尔值且不能与成长补做合用。'
        if type(point_reward_only) is not bool or point_reward_only and (growth_only or wait_for_gift):
            return False, '积分宝箱领取模式必须单独执行。'
        if type(login_only) is not bool or login_only and (growth_only or wait_for_gift or point_reward_only):
            return False, '只登录模式必须单独执行。'
        original_document = document
        if (self.active or self.app.closing or self.app.runner.is_active or
                self.app.objects.has_campaign_owner or self.app.objects.is_busy):
            return False, '当前已有任务，请先等待结束。'
        if self._driver is not None and self._driver.poll().as_dict().get('state') != 'STOPPED':
            return False, '上次登录自动化的停止尚未确认，请先处理现有窗口。'
        if self._title_driver is not None:
            previous = self._title_driver.poll()
            if (previous.get('phase') != 'DONE' or previous.get('cleanup_confirmed') is not True or
                    previous.get('outcome') == 'UNKNOWN'):
                identity = previous.get('identity') or {}
                if (previous.get('phase') != 'DONE' or type(identity.get('pid')) is not int or
                        type(identity.get('process_creation_filetime')) is not int or
                        process_status(identity['pid'], identity['process_creation_filetime']) != 'gone'):
                    return False, '上次进入游戏操作或接口清理尚未确认，请先退出当前游戏。'
            self._title_driver = None
        if self._switch_closer is not None:
            previous = self._switch_closer.poll().as_dict()
            if previous.get('observing') or previous.get('handles_retained'):
                return False, '上次游戏退出仍在确认，请稍候。'
            self._switch_closer = None
        try:
            document = validate_document(document)
        except AccountProfileError as exc:
            return False, str(exc)
        chosen = set(document['selected_ids'])
        profiles = [copy.deepcopy(p) for p in document['profiles'] if p['id'] in chosen]
        if not profiles:
            return False, '请选择至少一个账号。'
        if login_only:
            if len(profiles) != 1:
                return False, '只登录模式仅适用于一个已选择账号。'
            for task in profiles[0]['tasks'].values():
                task['enabled'] = False
        if point_reward_only:
            if len(profiles) != 1 or not profiles[0]['tasks']['daily']['enabled'] or not profiles[0]['tasks']['daily']['options']['items'].get('sign'):
                return False, '积分宝箱单项领取需要一个已启用签到及领奖的账号。'
            for key, task in profiles[0]['tasks'].items():
                task['enabled'] = key == 'daily'
            for name in profiles[0]['tasks']['daily']['options']['items']:
                profiles[0]['tasks']['daily']['options']['items'][name] = name == 'sign'
        if wait_for_gift:
            if len(profiles) != 1:
                return False, '单次礼包等待仅适用于一个已选择账号。'
            original = next(profile for profile in original_document['profiles']
                            if profile['id'] == profiles[0]['id'])
            daily = original['tasks']['daily']
            if daily['enabled'] is not True or daily['options']['items'].get('online_gift') is not True:
                return False, '单次礼包等待要求原本已明确启用日常及 online_gift。'
            for key, task in profiles[0]['tasks'].items():
                if key != 'daily':
                    task['enabled'] = False
            for name in profiles[0]['tasks']['daily']['options']['items']:
                if name != 'online_gift':
                    profiles[0]['tasks']['daily']['options']['items'][name] = False
        if growth_only:
            if any(not profile['tasks']['campaign']['enabled'] for profile in profiles):
                return False, '成长补做仅适用于原本已启用征战的所选账号。'
            # This transient queue is private to this explicit invocation.
            # Saved account task choices and every option remain untouched.
            for profile in profiles:
                for key in _ORDER:
                    profile['tasks'][key]['enabled'] = key == 'campaign'
        identities = [(p['user_id'], p['zone_id'], p['role_id']) for p in profiles
                      if p['user_id'] and p['zone_id'] and p['role_id']]
        if len(identities) != len(set(identities)):
            return False, '同一账号、区服和角色不能在同一轮中重复选择。'
        try:
            self._recovery_store.acquire(fresh=True)
            self._recovery_locked = True
            # A user-started round owns a fresh queue and counters. Historical
            # journals remain evidence, never a source of task admission.
            self._unknown_accounts = []
            self._archive_result_history()
        except Exception:
            self._recovery_store.release()
            self._recovery_locked = False
            return False, '无法取得本轮记录写入所有权或保留旧记录，未开始新一轮。'
        self._growth_only = growth_only
        self._point_reward_only = point_reward_only
        self._login_only = login_only
        self._wait_for_gift = wait_for_gift
        # One absolute budget for this explicit invocation, including relogin
        # and pauses. No later observation or session rebuild renews it.
        self._gift_wait_until = time.monotonic() + 1200.0 if wait_for_gift else None
        self._profiles = profiles
        self._frozen_profiles = [{key: copy.deepcopy(value) for key, value in profile.items()
                                  if key not in ('login_name', 'password')} for profile in profiles]
        self._records = []
        for profile in profiles:
            tasks = [{'key': key, 'label': TASK_CATALOG[key],
                      'status': 'QUEUED' if profile['tasks'][key]['enabled'] else 'SKIPPED',
                      'reason': '' if profile['tasks'][key]['enabled'] else '未启用', 'items': []}
                     for key in _ORDER]
            record = {'id': profile['id'], 'label': profile['label'] or '未命名账号',
                                  'server_name': profile['server_name'], 'role_name': profile['role_name'],
                                  'role_id': profile['role_id'], 'status': 'QUEUED',
                                  'reason': '', 'login_state': 'PENDING', 'tasks': tasks}
            if profile['tasks']['daily']['enabled']:
                next(item for item in tasks if item['key'] == 'daily')['routes'] = {
                    name: self._daily_route() for name, enabled in
                    profile['tasks']['daily']['options']['items'].items() if enabled}
            self._fence_unknown_tasks(profile, record)
            self._records.append(record)
        self._index = -1
        self._pending_accounts = list(range(len(profiles)))
        self._deferred_campaigns.clear()
        self._observed_identities.clear()
        self._campaign_pass = False
        self._task_index = 0
        self._current_task = ''
        self._driver = None
        self._title_attempted = False
        self._title_identity = None
        self._game_deadline = 0.0
        self._launcher_pause_pending = False
        self._paused_at = 0.0
        self._switch_window = None
        self._switch_identity = None
        self._switch_disconnect_requested = False
        self._switch_last_state = None
        self._coordinator = None
        self._owned_game = False
        self._ready_game = None
        self._session_game = None
        self._login_processes.clear()
        self._title_driver = None
        self._waiting_daily = False
        self._daily_waits = {}
        self._daily_last_checks = {}
        self._daily_due = set()
        self._daily_next_scan = 0.0
        self._daily_resume_due = False
        self._daily_selection = frozenset()
        self._daily_dispatch_phase = ''
        self._cleanup_requested = False
        self._cleanup_after_unknown = False
        self._cleanup_disconnect_requested = False
        self._human_required = False
        self._human_code = ''
        self._login_human_assisted = False
        self._run_id = uuid.uuid4().hex
        self._dispatches = []
        self._persistence_failed = False
        self._persistence_error = ''
        self._recovery_attempts = {profile['id']: 0 for profile in profiles}
        self._recovery_target = None
        self._recovery_skip_account = False
        self._recovery_continue_next = False
        self._recovery_driver_cleanup = False
        self._recovery_binding_cleared = False
        self._recovery_not_before = 0.0
        self._stop_deadline = 0.0
        self._stop_failure = ''
        self._stale_bound = None
        self._task_cleanup_deadline = 0.0
        self._task_requires_rebuild = False
        self.active, self.paused, self._stopping = True, False, False
        self._phase = 'next_account'
        if not self._publish('RUNNING', ''):
            return False, self._stop_failure
        return True, ''

    def pause(self):
        if not self.active or self.paused or self._stopping:
            return False
        try:
            if self._coordinator is not None and self._coordinator.is_active and not self._coordinator.pause():
                # A coordinator may already have paused its runner before a
                # native admission drain fails. Preserve that uncertainty.
                self._block_recovery('任务暂停准入未确认，已停止后续操作')
                return False
            if self._driver is not None:
                state = self._driver.pause().as_dict().get('state')
                self._launcher_pause_pending = state in ('PAUSING', 'PAUSED')
            if self.title_worker_pending:
                self._title_driver.pause()
            if self._switch_closer is not None:
                self._switch_closer.pause()
        except Exception as error:
            self._block_recovery('暂停操作失败，已停止后续操作：' + str(error))
            return False
        self.paused = True
        self._paused_at = time.monotonic()
        pending = self._launcher_pause_pending or self.title_worker_pending
        self._publish('PAUSING' if pending else 'PAUSED',
                      '正在暂停登录操作' if pending else '已暂停后续操作')
        return True

    def resume(self):
        if not self.active or not self.paused or self._stopping:
            return False
        if (self._launcher_pause_pending and self._driver is not None and
                self._driver.poll().as_dict().get('state') != 'PAUSED'):
            return False
        if self._title_driver is not None:
            title = self._title_driver.poll()
            if title.get('phase') not in ('PAUSED', 'DONE'):
                return False
            if (title.get('cleanup_confirmed') is not True or title.get('outcome') == 'UNKNOWN'
                    or title.get('phase') == 'PAUSED' and title.get('outcome') != 'NOT_INVOKED'):
                self._publish('UNKNOWN', '进入游戏操作或接口清理未确认，未恢复后续操作')
                return False
        elapsed = max(0.0, time.monotonic() - self._paused_at) if self._paused_at else 0.0
        if self._game_deadline:
            self._game_deadline += elapsed
        if self._deadline:
            self._deadline += elapsed
        if self._phase.startswith('recovery_'):
            self._recovery_not_before += elapsed
        self.paused = False
        self._paused_at = 0.0
        # The game wall clock continues during pause. A user resume admits one
        # immediate overdue check, then ordinary 60-second retry fencing resumes.
        self._daily_resume_due = True
        try:
            if not self._publish('RUNNING', ''):
                return False
            if self._observe_process_exit():
                self._launcher_pause_pending = False
                return True
            if self._phase.startswith('recovery_'):
                # The old actors were stopped for cleanup. Resume the plan's
                # cleanup clock, never the interrupted business worker.
                self._launcher_pause_pending = False
                return True
            if self._title_driver is not None and title.get('phase') == 'PAUSED':
                self._title_driver.resume()
            if self._coordinator is not None and self._coordinator.is_active and not self._coordinator.resume():
                self.paused = True
                self._paused_at = time.monotonic()
                self._publish('PAUSED', '任务暂未确认可以继续')
                return False
            if self._driver is not None:
                self._driver.resume()
            if self._switch_closer is not None:
                self._switch_closer.resume()
        except Exception as error:
            # A resume may have admitted work before failing. Do not pretend
            # that every actor is still paused, or leave a false RUNNING plan.
            self._block_recovery('恢复操作失败，已停止后续操作：' + str(error))
            return False
        self._launcher_pause_pending = False
        return True

    def stop(self):
        if not self.active:
            return False
        self._stopping = True
        self._stop_deadline = time.monotonic() + _CLEANUP_SECONDS
        self.paused = False
        self._human_required = False
        self._human_code = ''
        self._mark_dispatch_unknown('用户停止时任务尚未取得确认结果；不会重放')
        self._stop_workers()
        self._publish('STOPPING', '正在停止后续登录和任务')
        return True

    def _finish_stopping(self, *, unknown=False, reason=''):
        reason = reason or self._stop_failure or ('停止后的工作线程或清理状态未确认' if unknown else '已停止后续登录和任务')
        uncertain = unknown or bool(self._stop_failure) or self._persistence_failed
        if 0 <= self._index < len(self._records):
            record = self._records[self._index]
            if any(item['status'] == 'UNKNOWN' for item in record['tasks']):
                uncertain = True
            if record['status'] != 'UNKNOWN':
                record.update(status='UNKNOWN' if uncertain else 'STOPPED', reason=reason)
        self.active, self.paused, self._phase = False, False, 'idle'
        self._human_required, self._human_code = False, ''
        self._publish('UNKNOWN' if uncertain else 'STOPPED', reason)

    def confirm_human_action(self):
        """Resume only the login step that was explicitly presented to the person."""
        if not self.active or self.paused or self._driver is None:
            return False
        result = self._driver.poll().as_dict()
        if not result.get('human_required') or result.get('code') != self._human_code:
            return False
        code = self._human_code
        if self._stopping:
            self._driver.stop(close_owned=False)
            self._human_required, self._human_code = False, ''
            self._publish('STOPPING', '正在重新确认登录操作已停止')
            return True
        if self._phase == 'cleanup':
            self._driver.cleanup(cleanup_confirmed=True)
            self._deadline = time.monotonic() + 30
            self._human_required, self._human_code = False, ''
            self._publish('RUNNING', '正在重新确认前一账号已退出')
            return True
        if self._phase in ('finish_run', 'login_failure_cleanup'):
            self._driver.stop(close_owned=False)
            self._human_required, self._human_code = False, ''
            self._publish('RUNNING', '正在重新确认登录自动化已停止')
            return True
        if self._phase != 'login':
            return False
        if code == 'HELPER_NOT_EXITED':
            return self.stop()
        login_codes = {'SHARED_LOGIN', 'CAPTCHA', 'HUMAN_LOGIN', 'LOGIN_UNCERTAIN',
                       'FORM_UNSUPPORTED'}
        both_codes = {'ORIGIN_UNVERIFIED', 'UNEXPECTED_GAME', 'PATTERN_UNSUPPORTED',
                      'AUTOMATION_UNAVAILABLE', 'HELPER_TIMEOUT', 'HELPER_PROTOCOL'}
        server_codes = {'SERVER_UNCERTAIN'}
        if code in login_codes | both_codes | server_codes:
            self._login_human_assisted = True
        self._driver.resume(human_login_confirmed=code in login_codes | both_codes,
                            manual_server_confirmed=code in server_codes | both_codes)
        self._human_required, self._human_code = False, ''
        self._publish('RUNNING', '正在重新核对登录状态')
        return True

    def _task_record(self, key):
        return next(item for item in self._records[self._index]['tasks'] if item['key'] == key)

    def _finish_account(self):
        self._finish_gift_observation()
        record = self._records[self._index]
        selected = [item for item in record['tasks'] if item['status'] != 'SKIPPED']
        if selected and all(item['status'] in ('COMPLETED', 'PROCESSED') for item in selected):
            handled = any(item['status'] == 'PROCESSED' for item in selected)
            record['status'], record['reason'] = ('PROCESSED', '已处理（未回包）') if handled else ('COMPLETED', '')
        elif self._index in self._deferred_campaigns:
            record['status'], record['reason'] = 'WAITING', '征战已排到其他账号的短任务之后'
        elif any(item['status'] == 'UNKNOWN' for item in selected):
            record['status'], record['reason'] = 'UNKNOWN', '有操作结果或清理状态未知'
        elif not selected:
            record['status'], record['reason'] = 'UNAVAILABLE', '未启用任务'
        else:
            record['status'], record['reason'] = 'PARTIAL', '部分任务未完成'
        self._current_task = ''
        self._release_session_or_next()

    def _release_session_or_next(self):
        self._finish_gift_observation()
        if self._waiting_daily:
            self._mark_dispatch_unknown('在线等待期间会话即将结束，保留子任务未知且不重放')
        if self._login_only and self._records[self._index]['status'] == 'COMPLETED' and not self._pending_accounts:
            # Relinquish the verified game just as the one-off reward scope
            # does, then confirm the login worker stopped without closing it.
            self._owned_game = False
            if self._driver is not None:
                self._driver.stop(close_owned=False)
                self._deadline = time.monotonic() + _CLEANUP_SECONDS
                self._phase = 'finish_run'
            else:
                self._phase = 'next_account'
            return
        if self._point_reward_only and self._owned_game and not self._pending_accounts:
            daily = self._task_record('daily')
            point = next((row for row in daily.get('items', ()) if row.get('task_id') == 1304), None)
            if (daily['status'] != 'UNKNOWN' and point is not None
                    and point.get('cleanup') == 'VERIFIED_CLOSED' and point.get('cleanup_pending') is False
                    and point.get('continuation_safe') is True):
                # A one-off reward request ends at the verified home scene.
                # The existing finish-run path stops the login driver without
                # closing the user's now-idle game or starting another task.
                self._owned_game = False
                self._phase = 'next_account'
                return
        if self._owned_game and self._driver is not None:
            self._phase = 'cleanup'
            self._cleanup_requested = False
            self._cleanup_after_unknown = False
            self._cleanup_disconnect_requested = False
            self._deadline = time.monotonic() + _CLEANUP_SECONDS
        else:
            self._phase = 'next_account'

    @staticmethod
    def _server_matches(title, name):
        return bool(name) and name.strip() in [part.strip() for part in title.split('-')]

    def _discard_exited_binding(self):
        bound = self.app.bound
        if bound is None:
            return False
        try:
            process_creation(bound.process_id)
        except TargetGone:
            # Only a positive process-exit observation authorizes discarding
            # this binding. Unsupported modules/access failures do not.
            self._stale_bound = bound
            self._deadline = time.monotonic() + _CLEANUP_SECONDS
            self._phase = 'stale_binding_cleanup'
            self.app.objects.disconnect()
            self._publish('RUNNING', '原绑定游戏已退出，正在释放过期连接并继续登录')
            return True
        except Exception:
            pass
        return False

    def _clear_stale_binding(self):
        if time.monotonic() >= self._deadline:
            self._block_recovery('原游戏已退出，但过期连接清理未确认')
            return
        if self.app.bound is not None and self.app.bound != self._stale_bound:
            self._block_recovery('清理过期绑定时出现其他游戏绑定，未接管该窗口')
            return
        status = self.app.objects.connection_status
        if (self.app.runner.is_active or status.get('owner') or status.get('busy')
                or status.get('prelogin_bridge')):
            return
        if status.get('client_present') and not status.get('client_terminal'):
            self.app.objects.disconnect()
            return
        if self.app.bound is not None:
            self.app._clear_binding()
            return
        if status.get('state') != 'disconnected' or status.get('window_present'):
            self.app.objects.unbind()
            return
        self._stale_bound = None
        self._pending_accounts.insert(0, self._index)
        self._phase = 'next_account'
        self._publish('RUNNING', '过期绑定已释放，准备登录本轮账号')

    def _begin_account(self):
        if not self._pending_accounts:
            self.active = False
            self._phase = 'idle'
            statuses = [record['status'] for record in self._records]
            if statuses and all(s in ('COMPLETED', 'PROCESSED') for s in statuses):
                status, reason = ('PROCESSED', '已处理（未回包）') if 'PROCESSED' in statuses else ('COMPLETED', '')
            elif 'UNKNOWN' in statuses:
                status, reason = 'UNKNOWN', '部分账号任务结果未知；已保留其他账号的确认结果'
            else:
                status, reason = 'PARTIAL', '部分账号仍需处理'
            self._publish(status, reason)
            self._profiles = []
            return
        self._index = self._pending_accounts.pop(0)
        self._campaign_pass = self._index in self._deferred_campaigns
        self._deferred_campaigns.discard(self._index)
        profile, record = self._profiles[self._index], self._records[self._index]
        self._task_index = _ORDER.index('campaign') if self._campaign_pass else 0
        self._current_task = ''
        self._coordinator = None
        self._owned_game = False
        self._ready_game = None
        self._session_game = None
        self._login_processes.clear()
        self._title_driver = None
        self._title_attempted = False
        self._title_identity = None
        self._game_deadline = 0.0
        self._waiting_daily = False
        self._daily_due.clear()
        self._daily_next_scan = 0.0
        self._daily_resume_due = False
        self._daily_selection = frozenset()
        self._daily_dispatch_phase = ''
        self._human_required, self._human_code = False, ''
        self._login_human_assisted = False
        self._fence_unknown_tasks(profile, record)
        if not self._login_only and not self._has_pending_tasks(record):
            self._finish_account()
            self._publish('RUNNING', record['reason'])
            return
        if not profile['server_name']:
            record['status'], record['reason'] = 'WAITING', '请填写区服名称'
            self._phase = 'next_account'
            self._publish()
            return
        if not self._login_only and not any(profile['tasks'][key]['enabled'] for key in _ORDER):
            self._finish_account()
            self._publish()
            return
        if self.app.bound is not None:
            if self._discard_exited_binding():
                return
            self._prepare_existing_switch(profile, record)
            return
        if not profile['login_name'] or not profile['password']:
            record['status'], record['reason'] = 'WAITING', '请填写登录账号和密码'
            self._phase = 'next_account'
            self._publish()
            return
        try:
            from .launcher_login import LauncherLoginDriver
            self._driver = LauncherLoginDriver(diagnostics_dir=Path(self.app.data_dir) / 'session-cleanup')
            self._driver.begin(login_name=profile['login_name'], password=profile['password'],
                               server_name=profile['server_name'], server_tab=profile['server_tab'] or None)
            record['login_state'] = 'CONNECTING'
            record['reason'] = '正在启动游戏登录器'
            self._phase = 'login'
        except Exception:
            record['status'], record['reason'] = 'FAILED', '启动登录流程失败'
            self._phase = 'next_account'
        self._publish()

    def _prepare_existing_switch(self, profile, record):
        if not profile['login_name'] or not profile['password']:
            record['status'], record['reason'] = 'WAITING', '请填写登录账号和密码后切换游戏'
            self._phase = 'next_account'
            self._publish()
            return
        try:
            window = self.app.bound
            identity = identity_for(window)
        except Exception:
            if self._discard_exited_binding():
                return
            record['status'], record['reason'] = 'BLOCKED', '无法取得当前游戏的精确窗口和创建身份，未关闭身份不明的进程'
            self._phase = 'next_account'
            self._publish()
            return
        self._switch_window, self._switch_identity = window, identity
        self._switch_disconnect_requested = False
        self._switch_last_state = None
        self._deadline = time.monotonic() + _CLEANUP_SECONDS
        self._phase = 'switch_prepare'
        record['status'], record['reason'] = 'RUNNING', '正在清理连接，准备正常退出当前游戏'
        self._publish('RUNNING', record['reason'])

    def _switch_session(self):
        record = self._records[self._index]
        if self._phase == 'switch_prepare':
            connection = self.app.objects.connection_status
            if (self.app.runner.is_active or connection.get('owner') or connection.get('busy')
                    or connection.get('prelogin_bridge')):
                if time.monotonic() >= self._deadline:
                    self._block_recovery('当前会话工作线程或连接所有权未释放，无法安全切换')
                return
            if (connection.get('state') == 'ready' or connection.get('client_present')) and not self._switch_disconnect_requested:
                self._switch_disconnect_requested = True
                self.app.objects.disconnect()
                return
            if connection.get('client_present') and not connection.get('client_terminal'):
                if time.monotonic() >= self._deadline:
                    self.active, self._phase = False, 'idle'
                    record['status'], record['reason'] = 'UNKNOWN', '当前游戏连接清理未确认，未退出游戏'
                    self._publish('UNKNOWN', record['reason'])
                return
            if connection.get('window_present'):
                if not self.app.objects.retire_session_monitor(
                        self._switch_window, self._switch_identity.creation_time):
                    if time.monotonic() >= self._deadline:
                        self._block_recovery('当前游戏的状态监控未能按精确身份退出，未关闭游戏')
                    return
            # The closer keeps the captured process identity. Passive reads
            # and the binding view must stop before normal process teardown.
            self.app._clear_binding()
            try:
                from .account_session_cleanup import AutomaticSessionCloser
                self._switch_closer = AutomaticSessionCloser(diagnostics_dir=Path(self.app.data_dir) / 'session-cleanup')
                self._switch_closer.begin(self._switch_window, self._switch_identity.creation_time,
                                          tid=self._switch_identity.tid)
            except Exception:
                self.active, self._phase = False, 'idle'
                record['status'], record['reason'] = 'WAITING', '无法启动当前游戏的正常退出流程'
                self._publish('WAITING', record['reason'])
                return
            self._phase = 'switch_close'

        result = self._switch_closer.poll().as_dict()
        state, code = result.get('state'), result.get('code')
        game = result.get('game') or {}
        record['session_switch'] = {
            'state': state, 'code': code, 'operation_id': result.get('operation_id'),
            'game_pid': game.get('pid'), 'game_creation': game.get('process_creation_filetime'),
            'normal_close_sent': result.get('normal_close_sent') is True,
            'game_exit_confirmed': result.get('game_exit_confirmed') is True,
            'launcher_count': len(result.get('owned_launchers') or []),
            'complete': result.get('complete') is True,
        }
        if state == 'CLOSED' and result.get('complete') is True and not result.get('observing'):
            self._switch_closer = None
            self._switch_window = self._switch_identity = None
            self.app._clear_binding()
            self.app.refresh_windows(plan_internal=True)
            self._pending_accounts.insert(0, self._index)
            self._phase = 'next_account'
            record['status'], record['reason'] = 'QUEUED', '当前游戏已退出，准备登录目标账号'
            self._publish('RUNNING', record['reason'])
            return
        if state in ('STOPPED', 'BLOCKED') and not result.get('observing'):
            self.active, self._phase = False, 'idle'
            record['status'], record['reason'] = 'BLOCKED', result.get('message') or '游戏退出未完成'
            self._human_required, self._human_code = False, ''
            self._publish('BLOCKED', record['reason'])
            return
        if (state, code) != self._switch_last_state:
            self._switch_last_state = (state, code)
            record['status'] = 'WAITING' if state == 'WAITING' else 'RUNNING'
            record['reason'] = result.get('message') or '正在正常切换游戏'
            self._publish('WAITING' if state == 'WAITING' else 'RUNNING', record['reason'])

    def _connect_for_identity(self):
        status = self.app.objects.connection_status
        if status['state'] == 'ready':
            self._phase = 'identity'
            self._deadline = time.monotonic() + 15
        elif status['state'] in ('disconnected', 'error') and not status['busy']:
            if not self.app.objects.connect():
                self._records[self._index]['status'] = 'WAITING'
                self._records[self._index]['reason'] = '无法连接当前游戏以核对身份'
                self._phase = 'next_account'
            else:
                self._phase = 'identity'
                self._deadline = time.monotonic() + 60
        elif status['state'] == 'connecting':
            self._phase = 'identity'
            self._deadline = time.monotonic() + 60
        else:
            self._records[self._index]['status'] = 'WAITING'
            self._records[self._index]['reason'] = '游戏连接尚未完成清理'
            self._phase = 'next_account'
        self._publish()

    def _wait_owned_game(self):
        """Wait for fresh passive player data before starting the object bridge."""
        game = self._ready_game or {}
        record = self._records[self._index]
        result = self._driver.poll().as_dict() if self._driver is not None else {}
        if self._owned_game and result.get('state') != 'GAME_READY':
            # The launcher publishes WAITING_GAME briefly after resume before
            # its worker rediscovers the same game window.
            if result.get('state') in ('WAITING_GAME', 'STARTING', 'PAUSING', 'PAUSED'):
                if self._game_deadline and time.monotonic() >= self._game_deadline:
                    self._stop_game_wait('等待游戏窗口恢复超时')
                return
            record['status'], record['reason'] = 'WAITING', result.get('message') or '新游戏窗口已失效'
            if self._driver is not None:
                self._driver.stop(close_owned=False)
            self.active, self._phase = False, 'idle'
            self._publish('WAITING', record['reason'])
            return
        if self._owned_game:
            observed_game = result.get('game') or {}
            if any(observed_game.get(key) != game.get(key) for key in ('hwnd', 'pid', 'process_creation_filetime')):
                self._stop_game_wait('本轮游戏窗口身份已变化')
                return
        try:
            bound = self.app.bound
            if not bound or bound.hwnd != game.get('hwnd') or bound.process_id != game.get('pid'):
                raise ValueError('bound game changed')
            current = self.app._checked_window(bound)
        except Exception:
            record['status'], record['reason'] = 'WAITING', '无法确认新游戏窗口，已停止本轮'
            if self._driver is not None:
                self._driver.stop(close_owned=False)
            self.active, self._phase = False, 'idle'
            self._publish('WAITING', record['reason'])
            return
        snapshot = self.app.object_snapshot
        fresh = (isinstance(snapshot, dict) and snapshot.get('pid') == game.get('pid')
                 and snapshot.get('process_creation_filetime') == game.get('process_creation_filetime')
                 and snapshot.get('generation') == self.app.objects.generation
                 and isinstance(snapshot.get('character_id'), str)
                 and snapshot['character_id'].isdecimal() and int(snapshot['character_id']) > 0
                 and time.monotonic() - self.app.object_seen_monotonic <= 4.0)
        if not fresh or not self._server_matches(current.title, self._profiles[self._index]['server_name']):
            if self._game_deadline and time.monotonic() >= self._game_deadline:
                self._stop_game_wait('等待目标区服和角色数据加载超时')
                return
            if (not self._title_attempted and
                    self._server_matches(current.title, self._profiles[self._index]['server_name'])):
                self._begin_title_start(current, game)
            else:
                record['status'], record['reason'] = 'WAITING', '等待目标区服和角色数据加载'
                self._publish('WAITING', record['reason'])
            return
        try:
            identity = identity_for(current)
            if (identity.pid != game.get('pid') or identity.hwnd != game.get('hwnd')
                    or identity.creation_time != game.get('process_creation_filetime')
                    or not self.app.bind_owned_game(game)):
                raise ValueError('game identity changed')
        except Exception:
            self._stop_game_wait('游戏窗口身份已变化，未派发业务任务')
            return
        self._ready_game = None
        record['status'], record['reason'] = 'RUNNING', '正在核对游戏身份'
        self._connect_for_identity()

    def _stop_game_wait(self, reason, *, unknown=False, retry_pending=False):
        profile, record = self._profiles[self._index], self._records[self._index]
        retry = False
        game, bound = self._session_game, self.app.bound
        if (retry_pending and self._owned_game and self._driver is not None
                and self._exact_process(game) and bound is not None
                and bound.process_id == game['pid'] and bound.hwnd == game.get('hwnd')
                and process_status(game['pid'], game['process_creation_filetime']) == 'same'
                and profile['login_name'] and profile['password'] and self._has_pending_tasks(record)):
            attempts = self._recovery_attempts.get(profile['id'], 0)
            if attempts < _RECOVERY_LIMIT:
                # Closing this exact session invalidates online waits, never
                # the untouched queue. The normal cleanup must finish before
                # _begin_account can admit another login for this account.
                self._mark_dispatch_unknown('角色就绪连接失败，正在重建会话；已派发结果未知且不重放')
                if self._has_pending_tasks(record):
                    self._recovery_attempts[profile['id']] = attempts + 1
                    self._pending_accounts = [index for index in self._pending_accounts if index != self._index]
                    self._pending_accounts.insert(0, self._index)
                    retry = True
            else:
                reason += '；连续会话恢复次数已用尽，未派发任务保留原状态'
        status = 'UNKNOWN' if unknown or any(item['status'] == 'UNKNOWN' for item in record['tasks']) else 'FAILED'
        record['status'], record['reason'] = ('WAITING' if retry else status), reason
        if self._title_driver is not None:
            self._title_driver.stop()
        if self._driver is not None:
            self._driver.stop(close_owned=False)
        self.app.runner.stop()
        self.app.objects.disconnect()
        self._phase = 'failed_session_prepare'
        self._deadline = time.monotonic() + _CLEANUP_SECONDS
        self._human_required, self._human_code = False, ''
        self._publish('RUNNING', reason + ('；清理后重新登录原账号并继续未派发任务' if retry
                                         else '；正在自动清理该账号会话'))

    def _retire_owned_session_monitor(self):
        """Retire only this plan's stopped, still-live exact game before close."""
        game, bound = self._session_game, self.app.bound
        driver_game = (self._driver.poll().as_dict().get('game') or {}) if self._driver is not None else {}
        if (not self._owned_game or not self._exact_process(game) or bound is None
                or bound.process_id != game['pid'] or bound.hwnd != game.get('hwnd')
                or any(driver_game.get(key) != game.get(key)
                       for key in ('pid', 'process_creation_filetime', 'hwnd'))):
            self._block_recovery('正常退出前游戏所有权或窗口身份已改变，未关闭其他会话')
            return False
        process_state = process_status(game['pid'], game['process_creation_filetime'])
        if process_state == 'gone':
            # An exit before our close request remains unexpected. Preserve
            # dispatch results and use the original process-recovery boundary.
            self._begin_recovery(game)
            return False
        if process_state != 'same':
            return False
        if not self.app.objects.retire_session_monitor(bound, game['process_creation_filetime']):
            self._block_recovery('正常退出前无法确认精确会话监控已释放，未退出游戏')
            return False
        return True

    def _prepare_failed_session_cleanup(self):
        if time.monotonic() >= self._deadline:
            self._block_recovery('失败账号的工作线程或连接所有权未释放，无法安全关闭会话')
            return
        if self._title_driver is not None and self._title_driver.poll().get('phase') != 'DONE':
            return
        connection = self.app.objects.connection_status
        if (self.app.runner.is_active or connection.get('owner') or connection.get('busy')
                or connection.get('prelogin_bridge')):
            self.app.objects.disconnect()
            return
        if self._driver is None:
            self._block_recovery('失败会话缺少可核实的登录进程所有权')
            return
        result = self._driver.poll().as_dict()
        if result.get('state') != 'STOPPED':
            return
        if (self._owned_game and connection.get('state') == 'disconnected'
                and (not connection.get('client_present') or connection.get('client_terminal'))):
            if not self._retire_owned_session_monitor():
                return
        self._driver.cleanup_failed_session()
        self._phase = 'login_failure_cleanup'
        self._deadline = time.monotonic() + _CLEANUP_SECONDS

    def _begin_title_start(self, window, game):
        status = self.app.objects.connection_status
        if status.get('state') == 'unknown':
            self._stop_game_wait('游戏接口状态未知，未尝试进入游戏', unknown=True)
            return
        if status.get('state') == 'error':
            self._stop_game_wait(status.get('error') or '游戏接口尚未清理，未尝试进入游戏')
            return
        if (status.get('state') != 'disconnected' or status.get('busy') or status.get('owner') or
                status.get('prelogin_bridge') or
                (status.get('client_present') and not status.get('client_terminal'))):
            return
        try:
            identity = identity_for(window)
            if (identity.hwnd != game.get('hwnd') or identity.pid != game.get('pid') or
                    identity.creation_time != game.get('process_creation_filetime')):
                raise ValueError('game identity changed')
            server = self._profiles[self._index]['server_name']

            def read_identity():
                current = self.app._checked_window(window)
                if not self._server_matches(current.title, server):
                    raise ValueError('game server changed')
                return identity_for(current)

            from .title_start import TitleStartDriver
            driver = TitleStartDriver(self.app.objects.project_root, read_identity,
                                      io_guard=self.app.objects.prelogin_bridge_guard,
                                      on_unknown=self.app.objects.mark_prelogin_bridge_unknown,
                                      wait_seconds=90.0)
            operation = driver.begin(identity)
        except Exception as exc:
            self._stop_game_wait('无法启动游戏入口操作：' + str(exc))
            return
        self._title_driver = driver
        self._title_identity = identity
        self._title_attempted = True
        self._records[self._index]['title_start'] = {'operation_id': operation, 'outcome': 'PENDING'}
        self._records[self._index]['status'], self._records[self._index]['reason'] = 'RUNNING', '正在进入游戏'
        self._phase = 'title_start'
        self._publish('RUNNING', '正在进入游戏')

    def _poll_title_start(self):
        result = self._title_driver.poll()
        if result.get('phase') != 'DONE':
            return
        expected = self._title_identity.as_dict()
        record = self._records[self._index]
        record['title_start'] = {
            'operation_id': result.get('operation_id'), 'outcome': result.get('outcome'),
            'code': result.get('code'), 'cleanup_confirmed': result.get('cleanup_confirmed') is True,
            'pid': expected['pid'],
        }
        if result.get('identity') != expected or result.get('cleanup_confirmed') is not True or result.get('outcome') == 'UNKNOWN':
            self._stop_game_wait('进入游戏操作或接口清理未确认', unknown=True)
            return
        outcome, code = result.get('outcome'), result.get('code')
        if outcome == 'INVOKED' and code == 'INVOKED':
            self._game_deadline = time.monotonic() + 120
        elif outcome != 'NOT_INVOKED' or code not in ('ALREADY_STARTED', 'TITLE_NOT_PRESENT', 'WAIT_TIMEOUT'):
            self._stop_game_wait(result.get('reason') or '未能触发游戏入口')
            return
        record['status'], record['reason'] = 'WAITING', '等待目标区服和角色数据加载'
        self._phase = 'game_loading'
        self._publish('WAITING', record['reason'])

    def _identity(self):
        profile, record = self._profiles[self._index], self._records[self._index]
        identity = self.app.objects.current_identity()
        if identity is None:
            status = self.app.objects.connection_status
            if status.get('state') == 'disconnected' and status.get('phase') == 'stopped' and status.get('error'):
                self._stop_game_wait(status['error'], retry_pending=True)
                return
            if status['state'] in ('error', 'unknown') or time.monotonic() >= self._deadline:
                self._stop_game_wait('无法确认游戏账号、区服和角色身份', unknown=status['state'] == 'unknown',
                                     retry_pending=status['state'] != 'unknown')
            return
        actual = (identity.get('user_id'), identity.get('zone_id'), identity.get('character_id'))
        expected = (profile['user_id'], profile['zone_id'], profile['role_id'])
        if (not self.app.bound or identity.get('pid') != self.app.bound.process_id or
                identity.get('hwnd') != self.app.bound.hwnd or
                not self._server_matches(self.app.bound.title, profile['server_name'])):
            record['status'], record['reason'] = 'WAITING', '当前游戏窗口或区服身份无法确认'
            self._release_session_or_next()
            self._publish()
            return
        if any(value and value != observed for value, observed in zip(expected, actual)):
            if not self._owned_game and self.app.bound and profile['login_name'] and profile['password']:
                self._prepare_existing_switch(profile, record)
                return
            # Keep the successfully logged-in game available for identity
            # resolution; a mismatch must not silently close it and move on.
            self._stop_game_wait('实际游戏身份与账号配置不一致，请核对后继续')
            return
        if not all(type(value) is str and value.isascii() and value.isdecimal() and int(value) > 0 for value in actual):
            self._stop_game_wait('当前账号、区服和角色身份不完整，未派发任务')
            return
        if not all(expected):
            # A foreign same-server game cannot identify a newly configured
            # login. Only this run's own login may supply missing identifiers.
            if not self._owned_game or record['login_state'] != 'PENDING_IDENTITY':
                record['status'], record['reason'] = 'WAITING', '请通过该账号登录后识别角色'
                self._release_session_or_next()
                self._publish()
                return
            profile['user_id'], profile['zone_id'], profile['role_id'] = actual
            record['role_id'] = actual[2]
            record['identity_source'] = 'OBSERVED_OWN_LOGIN'
        else:
            record['identity_source'] = 'CONFIGURED_IDS_MATCHED'
        previous = self._observed_identities.get(actual)
        if previous is not None and previous != self._index:
            record['status'], record['reason'] = 'WAITING', '本轮其他账号已使用相同游戏角色'
            self._release_session_or_next()
            self._publish()
            return
        self._observed_identities[actual] = self._index
        record['observed_identity'] = dict(zip(('user_id', 'zone_id', 'role_id'), actual))
        self._fence_unknown_tasks(profile, record, actual)
        record['status'], record['reason'] = 'RUNNING', ''
        if record['login_state'] == 'PENDING_IDENTITY':
            record['login_state'] = 'ASSISTED_LOGIN' if self._login_human_assisted else 'AUTO_LOGIN'
        if self._login_only:
            record['status'], record['reason'] = 'COMPLETED', '登录和身份核对完成；保留游戏，未派发玩法'
            self._release_session_or_next()
            self._publish('RUNNING', record['reason'])
            return
        self._phase = 'next_task'
        self._publish()

    def _start_task(self, key, profile, *, selected_items=None, retry_source=None):
        if self._login_only:
            return None, 'login_only_forbids_tasks'
        if self._growth_only and key != 'campaign':
            return None, 'growth_only_forbids_other_task'
        task = profile['tasks'][key]
        options = task['options']
        identity = dict(expected_character_id=profile['role_id'], expected_user_id=profile['user_id'],
                        expected_zone_id=profile['zone_id'])
        if key == 'daily':
            selected = (frozenset(name for name, enabled in options['items'].items() if enabled)
                        if selected_items is None else frozenset(selected_items))
            if not selected:
                return None, '请至少启用一项日常子任务'
            coordinator = self.app.daily
            accepted = coordinator.start(options['donation_type'], selected_items=selected,
                                         enabled_items=frozenset(name for name, enabled in options['items'].items() if enabled),
                                         tribute_options=profile['tasks']['tribute']['options'],
                                         shop_duration=options['shop_duration'],
                                         defer_cooldowns=True, retry_source=retry_source, popup_scope=self._run_id,
                                         wait_for_gift=self._wait_for_gift, gift_wait_until=self._gift_wait_until,
                                         point_reward_only=self._point_reward_only,
                                         **identity)
        elif key == 'campaign':
            coordinator = self.app.campaign
            # Growth requirements and their rewards belong to an enabled
            # campaign. Legacy hidden options cannot disable that dependency.
            accepted = coordinator.start(allow_growth=True, growth_only=self._growth_only, **identity)
        elif key == 'challenge':
            coordinator = self.app.challenge
            accepted = coordinator.start(allow_growth=False, **identity)
        elif key == 'elite':
            coordinator = self.app.elite
            accepted = coordinator.start(target=options['target'], campaign_id=options['campaign_id'],
                                         star=options['star'], **identity)
        elif key == 'soul':
            if options['blue_count'] + options['purple_count'] == 0:
                return None, '请设置蓝魂或紫魂次数'
            coordinator = self.app.soul
            accepted = coordinator.start(options['blue_count'], options['purple_count'], **identity)
        else:
            coordinator = self.app.development
            try:
                if key == 'tribute':
                    target = TributeTarget(**options)
                elif key == 'equipment':
                    target = EquipmentTarget(**options)
                elif key == 'star':
                    target = StarTarget(**options)
                else:
                    target = None
            except (TypeError, ValueError):
                return None, '任务目标或资源上限无效'
            accepted = coordinator.start(key, target, **identity)
        return (coordinator.for_account(), '') if accepted else (None, '任务未被接受，请检查连接和任务状态')

    def _next_task(self):
        profile, record = self._profiles[self._index], self._records[self._index]
        connection = self.app.objects.connection_status
        if (self.app.runner.is_active or connection.get('owner') or connection.get('busy')
                or connection.get('prelogin_bridge')
                or self._coordinator is not None and self._coordinator.is_active):
            return
        # Ending an account needs no fresh native identity. A stopped campaign
        # may leave a result page that cannot satisfy main-screen readiness.
        # Keep potential daily warmup rereads for the existing selector below;
        # their eligibility still needs its normal live evidence checks.
        possible_warmup_retry = any(name in _DAILY_RETRYABLE_WARMUP and route['status'] == 'PARTIAL'
                                    for name, route in self._task_record('daily').get('routes', {}).items())
        if not self._has_pending_tasks(record) and not possible_warmup_retry:
            self._finish_account()
            self._publish()
            return
        actual_identity = self.app.objects.current_identity()
        expected = (profile['user_id'], profile['zone_id'], profile['role_id'])
        game = self._session_game
        if (self._exact_process(game) and process_status(game['pid'], game['process_creation_filetime']) == 'same'
                and actual_identity is None and self.app.objects.connection_status.get('state') == 'disconnected'):
            self._connect_for_identity()
            return
        if (not self._exact_process(game) or process_status(game['pid'], game['process_creation_filetime']) != 'same'
                or not isinstance(actual_identity, dict)
                or tuple(actual_identity.get(key) for key in ('user_id', 'zone_id', 'character_id')) != expected
                or actual_identity.get('pid') != game['pid'] or actual_identity.get('hwnd') != game.get('hwnd')
                or actual_identity.get('process_creation_filetime') != game['process_creation_filetime']):
            self._block_recovery('任务派发前无法重新确认原游戏与完整角色身份，未派发任务')
            return
        self._fence_unknown_tasks(profile, record, expected)
        # Campaign and challenge form the account's first stage. Neither an
        # overdue daily route nor a new cooldown may preempt either task.
        # Their terminal results remain immutable when a session is rebuilt.
        while self._task_index < _ORDER.index('elite'):
            key = _ORDER[self._task_index]
            self._task_index += 1
            if (profile['tasks'][key]['enabled']
                    and self._task_record(key)['status'] in ('QUEUED', 'WAITING')):
                self._dispatch_task(key, profile)
                return
        self._scan_daily_waits(resume=self._daily_resume_due)
        self._daily_resume_due = False
        daily = self._task_record('daily')
        routes = daily.get('routes', {})
        due = frozenset(name for name in self._daily_due if routes.get(name, {}).get('status') == 'WAITING')
        early = frozenset(name for name, route in routes.items() if name in _EARLY_DAILY
                          and route['status'] == 'QUEUED'
                          and (name != 'city_shop' or profile['tasks']['daily']['options']['shop_duration'] > 0))
        if due:
            self._dispatch_task('daily', profile, selected_items=self._one_daily_route(profile, due), dispatch_phase='resume')
            return
        if early:
            self._dispatch_task('daily', profile, selected_items=self._one_daily_route(profile, early), dispatch_phase='warmup')
            return
        while self._task_index < len(_ORDER):
            key = _ORDER[self._task_index]
            if key != 'daily':
                self._task_index += 1
            if not profile['tasks'][key]['enabled']:
                if key == 'daily':
                    self._task_index += 1
                continue
            item = self._task_record(key)
            selected = (frozenset(name for name, route in routes.items() if route['status'] == 'QUEUED'
                                 or self._warmup_retry_source(name, route) is not None)
                        if key == 'daily' else None)
            if key == 'daily' and not selected:
                if not routes:
                    item.update(status='UNAVAILABLE', reason='请至少启用一项日常子任务')
                self._task_index += 1
                continue
            if key != 'daily' and item['status'] not in ('QUEUED', 'WAITING'):
                # UNKNOWN and all other terminal results retain their evidence;
                # only this task is skipped when the account is revisited.
                continue
            self._dispatch_task(key, profile, selected_items=self._one_daily_route(profile, selected) if key == 'daily' else selected,
                                dispatch_phase='normal' if key == 'daily' else '')
            return
        retry = frozenset(name for name, route in routes.items()
                          if self._context_retry_source(name, route) is not None)
        if retry:
            self._dispatch_task('daily', profile, selected_items=self._one_daily_route(profile, retry), dispatch_phase='retry')
            return
        if self._waiting_daily:
            record['status'], record['reason'] = 'WAITING', '日常冷却中；当前角色需保持在线'
            for later_index in self._pending_accounts:
                later = self._records[later_index]
                if later['status'] == 'QUEUED':
                    later['reason'] = '等待当前角色在线冷却结束'
            self._phase = 'cooldown'
            self._publish('WAITING', record['reason'])
            return
        self._finish_account()
        self._publish()

    @staticmethod
    def _one_daily_route(profile, eligible):
        # Preserve account configuration order and the original dependency
        # order, while giving every route its own durable dispatch boundary.
        names = [name for name in profile['tasks']['daily']['options']['items'] if name in eligible]
        if not names:
            return frozenset()
        name = names[0]
        if name == 'mingshi' and 'divinity' in names:
            name = 'divinity'
        return frozenset((name,))

    def _dispatch_task(self, key, profile, *, selected_items=None, dispatch_phase=''):
        retry_source = None
        retry_of_context = {}
        if key == 'daily' and dispatch_phase == 'retry':
            if selected_items is None or len(selected_items) != 1:
                return
            name = next(iter(selected_items))
            source_id = self._context_retry_source(name, self._task_record('daily')['routes'][name])
            if source_id is None:
                return
            source = next(value for value in self._dispatches if value['dispatch_id'] == source_id)
            retry_source = copy.deepcopy(source['retry_evidence'][name])
            retry_of_context[name] = source_id
        self._current_task = key
        self._task_cleanup_deadline = 0.0
        self._task_requires_rebuild = False
        item = self._task_record(key)
        dispatch = {'account_id': profile['id'], 'task': key, 'dispatch_id': uuid.uuid4().hex,
                    'state': 'DISPATCHING', 'result_status': ''}
        if key == 'daily':
            self._daily_selection = frozenset(selected_items)
            self._daily_dispatch_phase = dispatch_phase
            retry_of = ({name: source for name in selected_items
                         if (source := self._warmup_retry_source(name, item['routes'][name])) is not None}
                        if dispatch_phase == 'normal' else retry_of_context)
            dispatch.update(phase=dispatch_phase, selected_items=sorted(selected_items), route_results={}, items=[],
                            retry_of=retry_of, retry_evidence={})
            checked_at, checked_mono = time.time(), time.monotonic()
            checks = self._daily_last_checks.setdefault(profile['id'], {})
            waits = self._daily_waits.setdefault(profile['id'], {})
            for name in selected_items:
                item['routes'][name] = self._daily_route('DISPATCHING', '已保留子任务派发边界',
                    dispatch_id=dispatch['dispatch_id'], checked_at=checked_at)
                checks[name] = checked_mono
                waits.pop(name, None)
            self._daily_due.difference_update(selected_items)
            self._daily_status()
        else:
            item.update(status='DISPATCHING', reason='已保留任务派发边界，正在启动')
        # A write-ahead selection boundary precedes any worker invocation.
        self._dispatches.append(dispatch)
        if not self._publish('RUNNING', ''):
            return
        try:
            self._coordinator = getattr(self.app, {'daily': 'daily', 'campaign': 'campaign',
                'challenge': 'challenge', 'elite': 'elite', 'soul': 'soul'}.get(key, 'development'))
            coordinator, reason = self._start_task(key, profile, selected_items=selected_items, retry_source=retry_source)
        except Exception:
            self._mark_dispatch_unknown('任务启动调用未确认；本轮不会重放该子任务')
            self._phase = 'task_cleanup'
            self._task_cleanup_deadline = time.monotonic() + _CLEANUP_SECONDS
            if self._coordinator is not None and self._coordinator.is_active:
                self._coordinator.stop()
            self.app.runner.stop()
            self.app.objects.disconnect()
            self._publish('RUNNING', '任务结果保留未知，正在确认清理后继续独立任务')
            return
        if coordinator is None:
            self._coordinator = None
            if key == 'daily':
                self._finish_daily_dispatch({'status': 'UNAVAILABLE', 'reason': reason})
            else:
                item.update(status='UNAVAILABLE', reason=reason)
                dispatch.update(state='COMMITTED', result_status='UNAVAILABLE')
            self._current_task = ''
            self._phase = 'next_task'
            self._publish('RUNNING', reason)
            return
        self._coordinator = coordinator
        # A fresh identity check and an accepted independent task end the
        # consecutive session-failure episode, rather than exhausting one
        # account-wide budget across unrelated task failures.
        self._recovery_attempts[profile['id']] = 0
        if key == 'daily':
            for name in selected_items:
                item['routes'][name]['status'] = 'RUNNING'
            self._daily_status()
        else:
            item.update(status='RUNNING', reason='')
        if key == 'campaign':
            for later_index in self._pending_accounts:
                later = self._records[later_index]
                if later['status'] == 'QUEUED':
                    later['reason'] = '等待当前账号征战结束；耗时由游戏进度决定'
        self._phase = 'task'
        self._publish()

    def _finish_daily_dispatch(self, data, *, forced_unknown=''):
        """Commit only this subset; retain other routes and earlier raw rows."""
        profile = self._profiles[self._index]
        item = self._task_record('daily')
        dispatch = self._dispatches[-1]
        selected = frozenset(dispatch['selected_items'])
        reason = forced_unknown or str(data.get('reason') or '')
        result = 'UNKNOWN' if forced_unknown else data.get('status')
        waits, releases, idle_evidence = {}, {}, {}
        if result == 'WAITING':
            cooldowns = data.get('cooldown_items')
            valid = (isinstance(cooldowns, (tuple, list)) and bool(cooldowns)
                     and data.get('requires_online') is True
                     and data.get('cooldown_cleanup') in ('VERIFIED_CLOSED', 'VERIFIED_RELEASED'))
            if valid:
                for entry in cooldowns:
                    if (not isinstance(entry, (tuple, list)) or len(entry) != 2
                            or type(entry[0]) is not str or entry[0] not in selected
                            or entry[0] not in _DAILY_WAIT_ROWS or entry[0] in waits
                            or type(entry[1]) not in (int, float) or not math.isfinite(entry[1]) or entry[1] <= 0):
                        valid = False
                        break
                    waits[entry[0]] = float(entry[1])
            raw_releases = data.get('cooldown_releases')
            if raw_releases is None and data.get('cooldown_cleanup') == 'VERIFIED_CLOSED':
                # Preserve the original all-closed contract. New mixed releases
                # always require one explicit tag for every selected wait.
                raw_releases = tuple((name, 'VERIFIED_CLOSED') for name in waits)
            if not isinstance(raw_releases, (tuple, list)):
                valid = False
            elif valid:
                for entry in raw_releases:
                    if (not isinstance(entry, (tuple, list)) or len(entry) != 2
                            or type(entry[0]) is not str or entry[0] not in waits or entry[0] in releases
                            or entry[1] not in ('VERIFIED_CLOSED', 'VERIFIED_IDLE')):
                        valid = False
                        break
                    releases[entry[0]] = entry[1]
                valid = valid and set(releases) == set(waits)
            idle_routes = {name for name, release in releases.items() if release == 'VERIFIED_IDLE'}
            raw_evidence = data.get('cooldown_release_evidence', ())
            if not isinstance(raw_evidence, (tuple, list)):
                valid = False
            elif valid:
                for witness in raw_evidence:
                    if (not isinstance(witness, dict) or type(witness.get('route')) is not str
                            or witness['route'] not in idle_routes or witness['route'] in idle_evidence):
                        valid = False
                        break
                    try:
                        idle_evidence[witness['route']] = validate_daily_idle_release(
                            witness, route=witness['route'], expected_role=profile['role_id'])
                    except (AccountRecoveryError, TypeError, ValueError, OverflowError):
                        valid = False
                        break
                valid = valid and set(idle_evidence) == idle_routes
            if valid:
                expected_cleanup = 'VERIFIED_RELEASED' if idle_routes else 'VERIFIED_CLOSED'
                valid = data.get('cooldown_cleanup') == expected_cleanup
                if idle_routes:
                    valid = (valid and self._idle_wait_boundary_confirmed(profile)
                             and data.get('cleanup_pending') is False
                             and len({witness['refresh_cycle'] for witness in idle_evidence.values()}) == 1)
            if not valid:
                result, reason, waits = 'UNKNOWN', '日常子任务等待或界面释放证据无法确认', {}
                releases, idle_evidence = {}, {}
        rows = self._display_rows(data)
        processed_result = (result in ('DONE', 'PROCESSED', 'PARTIAL') and len(selected) == 1
                            and data.get('cleanup_pending') is False and data.get('processed_without_reply') is True
                            and any(row.get('status') == 'PROCESSED' and row.get('processed_without_reply') is True for row in rows))
        page_result = (result in ('DONE', 'PARTIAL') and len(selected) == 1 and data.get('cleanup_pending') is False
                       and any(row.get('status') == 'COMPLETED' and row.get('page_confirmed') is True for row in rows))
        if result == 'PROCESSED' and not processed_result:
            result, reason = 'UNKNOWN', '已处理结果缺少本次请求的未回包标记'
        self._merge_daily_rows(item, rows)
        dispatch['items'] = copy.deepcopy(rows)
        local_waits = self._daily_waits.setdefault(profile['id'], {})
        now, wall_now = time.monotonic(), time.time()
        default_status = {'DONE': 'COMPLETED', 'UNKNOWN': 'UNKNOWN', 'STOPPED': 'STOPPED',
                          'BLOCKED': 'BLOCKED', 'PARTIAL': 'PARTIAL', 'FAILED': 'FAILED',
                          'PROCESSED': 'PROCESSED', 'UNAVAILABLE': 'UNAVAILABLE'}.get(result, 'PARTIAL')
        if processed_result:
            default_status = 'PROCESSED'
        elif page_result:
            default_status = 'COMPLETED'
        for name in selected:
            prior = item['routes'][name]
            route_reason = reason
            if name in waits:
                seconds = waits[name]
                local_waits[name] = now + seconds
                route = self._daily_route('WAITING', reason, dispatch_id=dispatch['dispatch_id'],
                    checked_at=prior['checked_at'], remaining_seconds=seconds, ready_at=wall_now + seconds,
                    requires_online=True, cleanup=releases[name], release_evidence=idle_evidence.get(name))
            else:
                route_status = default_status
                if result == 'WAITING':
                    route_rows = [row for row in rows
                                  if (row.get('category_id'), row.get('task_id')) == _DAILY_WAIT_ROWS.get(name)
                                  or name == 'city_shop' and (row.get('category_id'), row.get('task_id')) == (7, 308)]
                    if route_rows and all(row['status'] in ('COMPLETED', 'ALREADY_COMPLETE', 'ALREADY_CLAIMED')
                                          for row in route_rows):
                        route_status = 'COMPLETED'
                    elif any(row['status'] == 'UNKNOWN' for row in route_rows):
                        route_status = 'UNKNOWN'
                    else:
                        route_status = 'PARTIAL'
                    route_reason = '; '.join(dict.fromkeys(row['reason'] for row in route_rows)) or reason
                route = self._daily_route(route_status, route_reason, dispatch_id=dispatch['dispatch_id'],
                    checked_at=prior['checked_at'], processed_without_reply=route_status == 'PROCESSED',
                    page_confirmed=route_status == 'COMPLETED' and any(row.get('page_confirmed') is True for row in rows))
                local_waits.pop(name, None)
            item['routes'][name] = route
        dispatch['route_results'] = {name: copy.deepcopy(item['routes'][name]) for name in selected}
        if not forced_unknown:
            self._retain_warmup_retry_evidence(data, dispatch, item)
        states = {route['status'] for route in dispatch['route_results'].values()}
        status = ('UNKNOWN' if 'UNKNOWN' in states else 'WAITING' if 'WAITING' in states
                  else 'COMPLETED' if states == {'COMPLETED'}
                  else 'PROCESSED' if states and states <= {'COMPLETED', 'PROCESSED'} else default_status)
        dispatch.update(state='UNKNOWN' if status == 'UNKNOWN' else 'COMMITTED', result_status=status)
        item['reason'] = reason
        self._daily_status()
        return status, reason

    def _task_done(self):
        coordinator = self._coordinator
        if coordinator is None:
            return
        if coordinator.is_active or self.app.runner.is_active:
            data = coordinator.snapshot
            item = self._task_record(self._current_task)
            progress = {key: data[key] for key in
                        ('mode', 'status', 'reason', 'progress', 'stage', 'completed',
                         'target', 'remaining', 'settled_in_run', 'cleanup_pending') if key in data}
            rows = self._display_rows(data)
            prior_rows = copy.deepcopy(item['items'])
            if rows and self._current_task == 'daily':
                self._merge_daily_rows(item, rows)
            elif rows:
                item['items'] = rows
            if item.get('progress') != progress or item['items'] != prior_rows:
                item['progress'] = progress
                self._publish()
            return
        connection = self.app.objects.connection_status
        if not self._task_cleanup_deadline:
            self._task_cleanup_deadline = time.monotonic() + _CLEANUP_SECONDS
        if connection['busy'] or connection['owner'] or connection.get('prelogin_bridge'):
            self.app.objects.disconnect()
            if time.monotonic() >= self._task_cleanup_deadline:
                self._block_recovery('任务已结束，但工作连接未在限定时间内释放')
            return
        data = coordinator.snapshot
        item = self._task_record(self._current_task)
        item['progress'] = {key: data[key] for key in
                            ('mode', 'status', 'reason', 'progress', 'stage', 'completed',
                             'target', 'remaining', 'settled_in_run', 'cleanup_pending') if key in data}
        reason = str(data.get('reason') or '')
        cleanup = data.get('cleanup_state', data.get('cleanup', ''))
        cleanup_unconfirmed = str(cleanup).lower() in ('unknown', 'unverified', 'pending') or bool(data.get('cleanup_error'))
        popup_blocked = self._current_task == 'daily' and self._daily_popup_blocks_session(data)
        ui_unconfirmed = self._ui_cleanup_unconfirmed(data) or popup_blocked
        known_failure = (str(data.get('status') or '').upper() in ('FAILED', 'BLOCKED', 'PARTIAL', 'STOPPED', 'UNAVAILABLE')
                         or str(data.get('mode') or '').lower() in ('blocked', 'loss_limit', 'stopped'))
        if self._current_task == 'daily':
            status, reason = self._finish_daily_dispatch(data, forced_unknown=(
                reason or '游戏界面或连接清理未确认')
                if popup_blocked or ((connection['state'] == 'unknown' or cleanup_unconfirmed or ui_unconfirmed)
                                     and not known_failure) else '')
        elif (connection['state'] == 'unknown' or cleanup_unconfirmed or ui_unconfirmed) and not known_failure:
            status, reason = 'UNKNOWN', reason or '游戏界面或连接清理未确认'
        elif self._current_task in ('campaign', 'challenge', 'elite'):
            mode = str(data.get('mode') or data.get('status') or '').lower()
            pending = data.get('pending')
            if (isinstance(pending, dict) and pending.get('uncertain') is True
                    or mode in ('unknown',) or 'unknown' in reason or 'unconfirmed' in reason):
                status = 'UNKNOWN'
            elif self._current_task == 'elite' and reason == 'elite_no_current_available_option':
                # Exhausting the current choice list is not completing the
                # requested battles when no eligible stage is available.
                performed = data.get('performed_in_run')
                status = 'UNAVAILABLE' if type(performed) is int and performed == 0 else 'PARTIAL'
            elif self._growth_only and reason == 'growth_only_complete':
                status = ('COMPLETED' if str(cleanup).lower() == 'confirmed'
                          and connection['state'] != 'unknown' and not ui_unconfirmed else 'UNKNOWN')
            elif mode in ('complete', 'completed', 'done'):
                status = 'COMPLETED'
            elif mode in ('blocked',):
                status = 'BLOCKED'
            elif mode in ('loss_limit', 'stopped'):
                status = 'PARTIAL' if self._current_task == 'campaign' else 'UNAVAILABLE'
            else:
                status = 'PARTIAL'
        else:
            result = data.get('status')
            if result in ('DONE', 'COMPLETED', 'ALREADY_COMPLETE', 'ALREADY_CLAIMED'):
                status = 'COMPLETED' if data.get('evidence') != 'UNVERIFIED' else 'PARTIAL'
            else:
                status = {'UNKNOWN': 'UNKNOWN', 'STOPPED': 'STOPPED', 'BLOCKED': 'BLOCKED',
                          'PARTIAL': 'PARTIAL', 'FAILED': 'FAILED'}.get(result, 'UNAVAILABLE')
        if self._current_task != 'daily':
            item.update(status=status, reason=reason)
            for dispatch in reversed(self._dispatches):
                if dispatch['account_id'] == self._profiles[self._index]['id'] and dispatch['task'] == self._current_task:
                    dispatch.update(state='UNKNOWN' if status == 'UNKNOWN' else 'COMMITTED', result_status=status)
                    break
        self._task_requires_rebuild = (connection['state'] == 'unknown' or cleanup_unconfirmed or ui_unconfirmed
                                       or any(part in reason for part in _BAD_IDENTITY))
        if status == 'UNKNOWN' or self._task_requires_rebuild:
            if status == 'UNKNOWN' and self._current_task != 'daily':
                self._remember_unknown(self._profiles[self._index], {self._current_task})
            if status == 'UNKNOWN':
                self._records[self._index].update(status='UNKNOWN', reason=reason)
            self._phase = 'task_cleanup'
            self._publish('RUNNING', '保留该任务结果，确认清理或重建会话后继续其他任务')
            return
        self._coordinator = None
        self._current_task = ''
        self._phase = 'next_task'
        self._publish()

    def _daily_popup_blocks_session(self, data):
        """Read current native evidence; a stopped bridge can leave its popup open."""
        if (data.get('popup_status') != 'BLOCKED'
                or data.get('popup_reason') != 'popup_unknown_or_consumption_confirmation'):
            return False
        game = self._session_game
        role = self._profiles[self._index]['role_id']
        if not self._exact_process(game) or not role or data.get('character_id') != role:
            return False
        from .daily_popup import valid_popup
        objects = self.app.objects
        # Ownership is already released here, so workflow_snapshot no longer
        # accepts the retired token. Inspect only this client's retained cache;
        # never reconnect or borrow another worker's token to look for a popup.
        with objects._lock:
            client = objects._client
            if (client is None or client.identity.pid != game['pid']
                    or client.identity.creation_time != game['process_creation_filetime']
                    or client.identity.hwnd != game.get('hwnd')):
                return False
            state = client.last_state
            if (state.get('pid') != game['pid']
                    or state.get('process_creation_filetime') != game['process_creation_filetime']
                    or state.get('nonce') != client.nonce or state.get('session_id') != client.nonce):
                return False
            popup = state.get('daily_popup')
            if not valid_popup(popup) or popup.get('character_id') != role:
                return False
            return any(window['window_type'] == 'MessageBox' and window['visible'] is True
                       and window['loading'] is False and window['status'] == 'BLOCKED'
                       and window['reason'] == 'popup_unknown_or_consumption_confirmation'
                       and window['appearance_generation'] > 0
                       for window in popup['windows'])

    @staticmethod
    def _ui_cleanup_unconfirmed(data):
        pending, seen = [data], set()
        while pending:
            item = pending.pop()
            if isinstance(item, (tuple, list)):
                pending.extend(item)
                continue
            if not isinstance(item, dict):
                continue
            if id(item) in seen:
                continue
            seen.add(id(item))
            for key, value in item.items():
                if key == 'cleanup_pending' and value is True:
                    return True
                if (key == 'cleanup' or key == 'ui_cleanup_state' or key.endswith('_cleanup')):
                    if str(value).lower() in ('unknown', 'unverified', 'pending'):
                        return True
                if key.endswith('ownership_held') and value is True:
                    return True
                if isinstance(value, (dict, list, tuple)):
                    pending.append(value)
        return False

    def _task_cleanup(self):
        """Keep UNKNOWN immutable while waiting for resources, not settlement."""
        if time.monotonic() >= self._task_cleanup_deadline:
            self._block_recovery('任务结果已保留，但连接或界面清理未在限定时间内确认')
            return
        connection = self.app.objects.connection_status
        if ((self._coordinator is not None and self._coordinator.is_active) or self.app.runner.is_active
                or connection.get('owner') or connection.get('busy') or connection.get('prelogin_bridge')):
            self.app.objects.disconnect()
            return
        data = self._coordinator.snapshot if self._coordinator is not None else {}
        disconnected = (connection.get('state') == 'disconnected'
                        and (not connection.get('client_present') or connection.get('client_terminal')))
        ui_unconfirmed = self._ui_cleanup_unconfirmed(data)
        cleanup = str(data.get('cleanup_state', '')).lower()
        cleanup_unconfirmed = bool(data.get('cleanup_error')) or cleanup in ('unknown', 'unverified', 'pending')
        if ((connection.get('state') == 'unknown' or ui_unconfirmed or cleanup_unconfirmed or self._task_requires_rebuild)
                and self._owned_game and self._driver is not None
                and self._exact_process(self._session_game)):
            # The task is already recorded UNKNOWN and all workers above have
            # stopped. End the owned process lifetime before reconnecting; do
            # not turn an unconfirmed server result into a success or replay.
            profile, record = self._profiles[self._index], self._records[self._index]
            self._mark_dispatch_unknown('等待中的在线会话需退出，子任务结果未知且不重放')
            if (self._has_pending_tasks(record) and profile['login_name'] and profile['password']
                    and self._index not in self._pending_accounts):
                self._pending_accounts.insert(0, self._index)
            self._coordinator = None
            self._current_task = ''
            self._waiting_daily = False
            self._phase = 'cleanup'
            self._cleanup_requested = False
            self._cleanup_after_unknown = True
            self._cleanup_disconnect_requested = False
            self._deadline = time.monotonic() + _CLEANUP_SECONDS
            self._publish('RUNNING', '结果保持未知；正在正常退出当前会话后继续其他任务')
            return
        if (connection.get('state') in ('ready', 'disconnected')
                and (connection.get('state') == 'ready' or disconnected)
                and not ui_unconfirmed and not self._task_requires_rebuild
                and (not cleanup_unconfirmed or disconnected)):
            self._coordinator = None
            self._current_task = ''
            self._phase = 'next_task'
            self._publish('RUNNING', '工作线程与连接已释放；保留未知结果并继续其他任务')
            return
        if not disconnected:
            self.app.objects.disconnect()
            return
        # A disconnected bridge cannot prove that a native panel disappeared.
        # Normal session closure ends that UI lifetime; the task itself remains
        # UNKNOWN and is never put back in this round's runnable queue.
        profile, record = self._profiles[self._index], self._records[self._index]
        self._mark_dispatch_unknown('无法确认界面释放且需重建在线会话，等待子任务保留未知')
        self._coordinator = None
        self._current_task = ''
        self._waiting_daily = False
        if self._owned_game and self._driver is not None:
            if (self._has_pending_tasks(record) and profile['login_name'] and profile['password']
                    and self._index not in self._pending_accounts):
                self._pending_accounts.append(self._index)
            self._phase = 'cleanup'
            self._cleanup_requested = False
            self._cleanup_after_unknown = False
            self._cleanup_disconnect_requested = False
            self._deadline = time.monotonic() + _CLEANUP_SECONDS
            self._publish('RUNNING', '界面释放尚未确认，正常退出当前会话后继续待处理任务')
            return
        if self.app.bound is not None and profile['login_name'] and profile['password']:
            self._pending_accounts = [i for i in self._pending_accounts if i != self._index]
            self._deferred_campaigns.discard(self._index)
            self._prepare_existing_switch(profile, record)
            return
        self._block_recovery('无法确认原界面释放，且缺少可正常重建会话的账号凭据')

    def _login(self):
        result = self._driver.poll().as_dict()
        record = self._records[self._index]
        state = result.get('state', '')
        record['login_state'] = state
        record['reason'] = result.get('message', '')
        self._human_required = bool(result.get('human_required'))
        self._human_code = str(result.get('code') or '') if self._human_required else ''
        if (result.get('code') in ('LAUNCHER_EXITED', 'INSTALLATION_UNAVAILABLE', 'UNSUPPORTED_PLATFORM')
                and result.get('cleanup_complete') is True and not result.get('owned_processes')):
            record['status'] = 'FAILED'
            self._driver.stop(close_owned=False)
            self._human_required, self._human_code = False, ''
            self._phase = 'login_failure_cleanup'
            self._deadline = time.monotonic() + _CLEANUP_SECONDS
            self._publish('RUNNING', record['reason'])
            return
        if state in ('WAITING', 'ACTION_REQUIRED', 'BLOCKED') and self._human_required:
            # A login step needing intervention is a failed account attempt,
            # not an indefinite stop for every other enabled account.
            record['status'] = 'FAILED'
            self._human_required, self._human_code = False, ''
            self._driver.cleanup_failed_session()
            self._phase = 'login_failure_cleanup'
            self._deadline = time.monotonic() + _CLEANUP_SECONDS
            self._publish('RUNNING', '该账号登录未完成，自动清理后继续其他账号')
            return
        if state in ('FAILED', 'BLOCKED', 'STOPPED'):
            record['status'] = 'FAILED'
            if result.get('cleanup_complete') is True:
                self._driver.stop(close_owned=False)
                self._phase = 'login_failure_cleanup'
                self._deadline = time.monotonic() + _CLEANUP_SECONDS
            else:
                self._driver.cleanup_failed_session()
                self._phase = 'login_failure_cleanup'
                self._deadline = time.monotonic() + _CLEANUP_SECONDS
                self._human_required, self._human_code = False, ''
            self._publish('RUNNING', '正在自动清理未完成的登录会话')
            return
        elif state == 'GAME_READY':
            game = result.get('game') or {}
            if self._exact_process(game) and process_status(game['pid'], game['process_creation_filetime']) == 'gone':
                self._session_game = copy.deepcopy(game)
                self._begin_recovery(game)
                return
            if not self.app.bind_owned_game(game):
                self._stop_game_wait('无法确认新游戏窗口归属，未派发业务任务')
                return
            self._owned_game = True
            record['login_state'] = 'PENDING_IDENTITY'
            self._human_required, self._human_code = False, ''
            self._ready_game = game
            self._session_game = copy.deepcopy(game)
            self._game_deadline = time.monotonic() + 120
            self._phase = 'game_loading'
            record['status'], record['reason'] = 'WAITING', '等待目标区服和角色数据加载'
            self._publish('WAITING', record['reason'])
        self._publish()

    def _cleanup(self):
        if time.monotonic() >= self._deadline:
            self._block_recovery('前一账号任务或游戏清理超时，已停止后续账号')
            return
        status = self.app.objects.connection_status
        if status['owner'] or status['busy'] or self.app.runner.is_active:
            return
        if self._cleanup_after_unknown:
            game = self._session_game
            bound = self.app.bound
            if (not self._exact_process(game) or bound is None or bound.process_id != game['pid']
                    or bound.hwnd != game.get('hwnd')):
                self._block_recovery('原游戏身份无法确认，未关闭其他会话')
                return
            if not self._cleanup_requested:
                self._driver.cleanup_failed_session()
                self._cleanup_requested = True
                return
            result = self._driver.poll().as_dict()
            if result.get('human_required') or result.get('state') == 'BLOCKED':
                self._block_recovery(result.get('message') or '当前会话的正常退出未确认')
                return
            if (result.get('state') != 'STOPPED' or result.get('cleanup_complete') is not True
                    or result.get('owned_processes')):
                return
            if process_status(game['pid'], game['process_creation_filetime']) != 'gone':
                self._block_recovery('原游戏进程退出身份未确认')
                return
            # Once the exact target is gone, stop() records that process
            # boundary and retires the old bridge without claiming settlement.
            if status.get('client_present') and not status.get('client_terminal'):
                if not self._cleanup_disconnect_requested:
                    self._cleanup_disconnect_requested = True
                    self.app.objects.disconnect()
                return
            self.app._clear_binding()
            self._driver = None
            self._owned_game = False
            self._ready_game = self._session_game = None
            self._human_required, self._human_code = False, ''
            self._cleanup_after_unknown = False
            self.app.refresh_windows(plan_internal=True)
            self._phase = 'next_account'
            self._publish('RUNNING', '原会话已退出；未知结果已保留，继续待处理任务')
            return
        if status['state'] == 'unknown':
            self._driver.stop(close_owned=False)
            self.active = False
            self._phase = 'idle'
            self._publish('UNKNOWN', '前一账号连接清理未确认')
            return
        if status['state'] == 'ready':
            self.app.objects.disconnect()
            return
        if status['state'] != 'disconnected':
            if time.monotonic() >= self._deadline:
                self._driver.stop(close_owned=False)
                self.active = False
                self._phase = 'idle'
                self._publish('UNKNOWN', '前一账号连接清理超时')
            return
        if not self._cleanup_requested:
            if not self._retire_owned_session_monitor():
                return
            self._driver.cleanup(cleanup_confirmed=True)
            self._cleanup_requested = True
        result = self._driver.poll().as_dict()
        if result.get('state') == 'STOPPED' and result.get('cleanup_complete') is True:
            self.app._clear_binding()
            self._driver = None
            self._owned_game = False
            self._ready_game = self._session_game = None
            self._human_required, self._human_code = False, ''
            self.app.refresh_windows(plan_internal=True)
            self._phase = 'next_account'
        elif result.get('state') == 'BLOCKED':
            self._block_recovery(result.get('message') or '精确会话的自动退出未确认')
            return
        elif result.get('human_required') is True:
            self._human_required = True
            self._human_code = str(result.get('code') or '')
            self._records[self._index]['reason'] = result.get('message') or '等待处理前一账号的退出'
            self._publish('WAITING', self._records[self._index]['reason'])
            return
        elif time.monotonic() >= self._deadline:
            self._driver.stop(close_owned=False)
            self.active = False
            self._phase = 'idle'
            self._publish('UNKNOWN', '前一账号游戏退出未确认')

    def _finish_login_worker(self):
        if time.monotonic() >= self._deadline:
            self._block_recovery('登录自动化或进程所有权停止超时，已停止后续账号')
            return
        result = self._driver.poll().as_dict()
        if result.get('state') == 'BLOCKED' and not result.get('human_required'):
            self._block_recovery(result.get('message') or '精确登录会话的自动退出未确认')
            return
        if result.get('state') == 'STOPPED':
            if self._phase == 'login_failure_cleanup' and result.get('cleanup_complete') is not True:
                self.active, self._phase = False, 'idle'
                self._publish('UNKNOWN', '登录失败后的进程状态未确认')
                return
            if self._phase == 'login_failure_cleanup':
                connection = self.app.objects.connection_status
                if (connection.get('owner') or connection.get('busy') or connection.get('prelogin_bridge')
                        or connection.get('client_present') and not connection.get('client_terminal')):
                    self.app.objects.disconnect()
                    return
                self.app._clear_binding()
                self._title_driver = None
            self._driver = None
            self._owned_game = False
            self._ready_game = self._session_game = None
            self._human_required, self._human_code = False, ''
            self._phase = 'next_account'
        elif result.get('human_required'):
            self._block_recovery(result.get('message') or '登录自动化停止状态未确认')

    def tick(self):
        if not self.active:
            return
        if self._stopping:
            if self._stop_deadline and time.monotonic() >= self._stop_deadline:
                self._finish_stopping(unknown=True)
                return
            if self._title_driver is not None:
                title = self._title_driver.poll()
                if title.get('phase') != 'DONE':
                    return
                if title.get('outcome') == 'UNKNOWN' or title.get('cleanup_confirmed') is not True:
                    game = self._session_game
                    if not self._exact_process(game) or process_status(game['pid'], game['process_creation_filetime']) != 'gone':
                        self._finish_stopping(unknown=True, reason='进入游戏操作或接口清理未确认')
                        return
            if self._coordinator is not None and self._coordinator.is_active:
                return
            if self.app.runner.is_active or self.app.objects.has_campaign_owner or self.app.objects.is_busy:
                return
            connection = self.app.objects.connection_status
            if connection.get('prelogin_bridge') or (connection.get('client_present') and not connection.get('client_terminal')):
                return
            if connection.get('state') == 'unknown':
                self._stop_failure = self._stop_failure or '停止后的连接清理未确认'
            if self._switch_closer is not None:
                result = self._switch_closer.poll().as_dict()
                if result.get('state') not in ('STOPPED', 'CLOSED'):
                    return
                if result.get('observing') or result.get('handles_retained'):
                    self._stop_failure = self._stop_failure or '正常退出的进程所有权仍未释放'
            if self._driver is not None:
                result = self._driver.poll().as_dict()
                if result.get('state') not in ('STOPPED', 'BLOCKED'):
                    return
                if result.get('state') == 'BLOCKED':
                    self._finish_stopping(unknown=True, reason=result.get('message') or '登录操作停止状态未确认')
                    return
            self._finish_stopping()
            return
        if self.paused:
            result = self._driver.poll().as_dict() if self._launcher_pause_pending and self._driver else {}
            title = self._title_driver.poll() if self._title_driver is not None else {'phase': 'DONE'}
            if result.get('state') == 'BLOCKED' or (title.get('phase') == 'DONE' and
                    (title.get('outcome') == 'UNKNOWN' or title.get('cleanup_confirmed', True) is not True)):
                self._publish('UNKNOWN', result.get('message') or '登录暂停状态未确认，请停止本轮')
            elif ((not self._launcher_pause_pending or result.get('state') == 'PAUSED') and
                  title.get('phase') in ('PAUSED', 'DONE') and self._snapshot.get('status') != 'PAUSED'):
                self._publish('PAUSED', '已暂停后续登录操作')
            return
        if self._observe_process_exit():
            return
        if self._phase == 'next_account':
            self._begin_account()
        elif self._phase in ('switch_prepare', 'switch_close'):
            self._switch_session()
        elif self._phase == 'identity':
            self._identity()
        elif self._phase == 'login':
            self._login()
        elif self._phase == 'game_loading':
            self._wait_owned_game()
        elif self._phase == 'title_start':
            self._poll_title_start()
        elif self._phase == 'next_task':
            self._next_task()
        elif self._phase == 'task':
            self._task_done()
        elif self._phase == 'task_cleanup':
            self._task_cleanup()
        elif self._phase == 'stale_binding_cleanup':
            self._clear_stale_binding()
        elif self._phase == 'cooldown':
            self._scan_daily_waits(resume=self._daily_resume_due)
            self._daily_resume_due = False
            if self._daily_due:
                self._phase = 'next_task'
        elif self._phase == 'cleanup':
            self._cleanup()
        elif self._phase in ('finish_run', 'login_failure_cleanup'):
            self._finish_login_worker()
        elif self._phase == 'failed_session_prepare':
            self._prepare_failed_session_cleanup()
        elif self._phase == 'recovery_cleanup':
            self._recover_process()
        elif self._phase == 'recovery_backoff':
            self._relaunch_account()
