"""Owner-thread automatic discovery and idle connection admission.

Only discovery runs in a worker. Binding and normal ObjectController.connect()
remain on the application owner thread; no login, game launch or game action
is performed here. Business evidence never determines connection availability.
"""
from __future__ import annotations

from copy import deepcopy
import threading
import time

from .event_stream import EventMailbox
from .mono_runtime import process_creation


class AutoConnectionCoordinator:
    DISCOVERY_SECONDS = 1.0
    RETRY_DELAYS = (5.0, 15.0)
    PLAYER_LOADING_ERROR = '游戏主界面或角色尚未就绪'
    PASSIVE_LOADING_ERRORS = (
        'Required types not all cached; no scalar reads',
        'Global has no existing runtime_info; no initialization attempted',
        'Expected exactly one initialized nonnull Character reference across registered domains',
        'Root domain pointer null or outside x86 user bounds',
        'Class cache size outside bounds or not initialized',
    )

    def __init__(self, app) -> None:
        self.app = app
        self.events = EventMailbox('auto_connection')
        self._owner = threading.get_ident()
        self._closed = False
        self._discovery_pending = False
        self._next_discovery = 0.0
        self._windows = []
        self._identities = {}
        self._discovery_error = ''
        self._has_discovery = False
        self._published = None
        self._selected = None
        self._ambiguous_keys = set()
        self._target = None
        self._held = set()
        self._hold_before_creation = 0
        self._hold_reason = ''
        self._failures = {}
        self._retry_at = {}
        self._attempt = None
        self._attempt_generation = None
        self._last_attempt_at = 0.0
        self._state = {'stage': 'discovering', 'label': '正在查找游戏', 'reason': 'startup',
                       'message': '正在查找游戏窗口。'}

    def _require_owner(self) -> None:
        if threading.get_ident() != self._owner:
            raise RuntimeError('自动连接只能在应用主线程推进。')

    @staticmethod
    def _identity_key(identity):
        if not identity:
            return None
        pid = identity.get('pid')
        creation = identity.get('process_creation_filetime')
        hwnd = identity.get('hwnd')
        return (pid, creation, hwnd) if all(type(v) is int and v > 0 for v in (pid, creation, hwnd)) else None

    def _bound_key(self):
        bound = self.app.bound
        if bound is None:
            return None
        key = self._identities.get((bound.process_id, bound.hwnd))
        if key is not None:
            return key
        key = self._identity_key(self.app.objects.connection_preflight.get('identity'))
        return key if key and (key[0], key[2]) == (bound.process_id, bound.hwnd) else None

    def hold(self, reason: str = 'explicit_stop') -> None:
        """Explicit stop fences this process, including discovery already in flight."""
        self._require_owner()
        key = self._bound_key() or self._target
        if key is not None:
            self._held.add(key)
        else:
            self._held.update(self._identities.values())
        if self._selected is not None:
            self._held.add(self._selected)
        # This also holds games already running while the first discovery is
        # in flight. A genuinely new process may resume automatic connection.
        self._hold_before_creation = time.time_ns() // 100 + 116444736000000000
        self._hold_reason = reason
        self._set('stopped', reason, '已停止；下次开始玩法时继续连接。')

    def resume(self) -> None:
        """An explicit new start releases the user's connection stop hold."""
        self._require_owner()
        if self._closed:
            return
        self._held.clear()
        self._hold_before_creation = 0
        self._hold_reason = ''
        self._next_discovery = 0.0

    def choose(self, hwnd: int) -> bool:
        self._require_owner()
        selected = next((key for key in self._identities.values() if key[2] == hwnd), None)
        if self._closed or selected is None or self._external_owner():
            return False
        status = self.app.objects.connection_status
        if (status.get('busy') or status.get('state') in ('connecting', 'stopping') or
                status.get('closing') or status.get('shutdown')):
            self._set('waiting_owner', 'selection_busy', '当前连接处理完成后才能切换游戏。')
            return False
        self._selected = selected
        self._published = None
        self.resume()
        if selected != self._bound_key() and status.get('client_present') and status.get('client_terminal') is not True:
            # Explicit selection may request normal cleanup, but may bind only
            # after that exact old client reports terminal without uncertainty.
            if not self.app.objects.disconnect():
                self._set('blocked', 'selection_disconnect_refused', '原游戏尚未接受断开请求，不能切换。')
                return False
            self._set('waiting_cleanup', 'selection_cleanup', '正在断开原游戏，清理完成后切换。')
            return True
        self.tick()
        return True

    def _external_owner(self) -> str:
        # Only another live owner of the connection transport delays attach.
        # Account/gameplay outcomes belong to their own executors.
        status = self.app.objects.connection_status
        if status.get('prelogin_bridge'):
            return 'prelogin_bridge_owned'
        return ''

    def _discover(self, keyword: str) -> None:
        """No Tk calls and no bridge work; one bounded enumeration at a time."""
        try:
            windows = [window for window in self.app.adapter.find_windows(keyword)
                       if window.process_name.casefold() == 'js.exe']
            identities = {(window.process_id, window.hwnd):
                          (window.process_id, process_creation(window.process_id), window.hwnd)
                          for window in windows}
            result = {'keyword': keyword, 'windows': windows, 'identities': identities, 'error': ''}
        except Exception as exc:
            result = {'keyword': keyword, 'windows': [], 'identities': {}, 'error': str(exc)}
        self.events.put(result)

    def on_discovery(self, event) -> None:
        self._require_owner()
        self._discovery_pending = False
        if self._closed or self.app.closing:
            return
        result = event.payload
        if result['keyword'] != self.app.settings.window_keyword:
            self._next_discovery = 0.0
            return
        self._discovery_error = result['error']
        self._has_discovery = True
        if not self._discovery_error:
            self._windows, self._identities = result['windows'], result['identities']
            if len(self._identities) > 1:
                self._ambiguous_keys.update(self._identities.values())
            elif not self._identities:
                self._ambiguous_keys.clear()

    def _set(self, stage: str, reason: str, message: str) -> None:
        labels = {'discovering': '正在查找游戏', 'waiting_game': '等待打开游戏',
                  'selecting_window': '请选择游戏', 'waiting_player': '等待进入角色',
                  'waiting_owner': '游戏任务处理中', 'stopped': '已停止',
                  'waiting_cleanup': '正在切换游戏',
                  'connecting': '正在连接游戏', 'ready': '游戏已连接',
                  'retry_wait': '等待重连', 'blocked': '游戏连接待处理',
                  'closed': '助手正在关闭'}
        self._state = {'stage': stage, 'label': labels[stage], 'reason': reason, 'message': message}
        render = getattr(self.app, 'render_auto_connection', None)
        if render is not None:
            render(self.snapshot)

    def _publish_windows(self, target) -> bool:
        signature = (tuple((w.hwnd, w.process_id, w.title, w.width, w.height) for w in self._windows), target)
        if signature != self._published:
            bound = self.app.bound
            if target and bound is not None and (bound.process_id, bound.hwnd) == (target[0], target[2]):
                # Reconnecting the selected process does not rebind its window
                # or wait for unrelated gameplay UI ownership.
                self._published = signature
                return True
            accepted = self.app.apply_auto_windows(self._windows, target[2] if target else None)
            if accepted is False:
                return False
            self._published = signature
        return True

    def tick(self) -> None:
        self._require_owner()
        app = self.app
        if self._closed or app.closing:
            return
        now = time.monotonic()
        owner = self._external_owner()
        if owner:
            self._set('waiting_owner', owner, '当前任务正在使用游戏。')
            return
        status = app.objects.connection_status
        if status.get('shutdown') or status.get('closing'):
            self._set('blocked', 'controller_closing', '游戏连接正在关闭。')
            return
        # Enumerating windows performs process inspection, never on the Tk thread.
        if not self._discovery_pending and now >= self._next_discovery:
            self._discovery_pending = True
            self._next_discovery = now + self.DISCOVERY_SECONDS
            try:
                threading.Thread(target=self._discover, args=(app.settings.window_keyword,),
                                 name='game-window-discovery', daemon=True).start()
            except Exception:
                self._discovery_pending = False
                raise
        if status.get('state') == 'ready':
            key = self._bound_key()
            if key:
                self._failures.pop(key, None)
                self._retry_at.pop(key, None)
            self._attempt = self._attempt_generation = None
            self._target = key
            if key in set(self._identities.values()):
                self._publish_windows(key)
            self._set('ready', 'connected', '游戏已连接。')
            return
        if status.get('busy') or status.get('state') in ('connecting', 'stopping'):
            self._set('connecting' if status.get('state') == 'connecting' else 'waiting_owner',
                      status.get('phase') or 'connection_busy', status.get('message') or '正在等待游戏连接。')
            return
        if self._discovery_error:
            self._set('blocked', 'discovery_failed', '无法查找游戏窗口：' + self._discovery_error)
            return
        if not self._has_discovery:
            self._set('discovering', 'window_discovery', '正在查找游戏窗口。')
            return
        bound_key = self._bound_key()
        available = set(self._identities.values())
        target = (self._selected if self._selected in available else
                  bound_key if bound_key in available else
                  next(iter(available)) if len(available) == 1 else None)
        if (target is not None and target != self._selected and bound_key not in available
                and app.bound is not None and target in self._ambiguous_keys):
            # If the selected game disappears, another previously coexisting
            # game does not become the user's selection by elimination.
            target = None
        self._target = target
        if self._attempt is not None:
            if self._attempt_generation == status.get('generation'):
                count = self._failures.get(self._attempt, 0) + 1
                self._failures[self._attempt] = count
                delay = self.RETRY_DELAYS[min(count - 1, len(self.RETRY_DELAYS) - 1)]
                self._retry_at[self._attempt] = now + delay
            self._attempt = self._attempt_generation = None
        if target is None:
            self._publish_windows(None)
            self._set('selecting_window' if available else 'waiting_game',
                      'multiple_windows' if available else 'game_not_running',
                      '请选择要使用的游戏窗口。' if available else '请打开游戏，进入角色后会自动连接。')
            return
        if target in self._held or target[1] <= self._hold_before_creation:
            self._set('stopped', self._hold_reason or 'explicit_stop', '已停止；下次开始玩法时继续连接。')
            return
        if not self._publish_windows(target):
            self._set('waiting_owner', 'binding_not_admitted', '等待当前游戏连接释放。')
            return
        status = app.objects.connection_status
        recovery = status.get('recovery') or {}
        if (recovery.get('process_restart_required') is True
                and (recovery.get('pid'), recovery.get('process_creation_filetime')) == target[:2]):
            self._retry_at.pop(target, None)
            self._set('blocked', 'game_process_restart_required',
                      '当前游戏进程的调用接口尚未恢复，需要退出并重新打开游戏；原操作不会重放。')
            return
        retry_at = self._retry_at.get(target, 0.0)
        if now < retry_at or now < self._last_attempt_at + self.RETRY_DELAYS[0]:
            self._set('retry_wait', 'connection_retry', '正在等待重连。')
            return
        if self._external_owner():
            self._set('waiting_owner', 'owner_changed', '当前任务正在使用游戏。')
            return
        self._last_attempt_at = now
        if app.objects.connect():
            self._attempt, self._attempt_generation = target, app.objects.generation
            self._set('connecting', 'connection_started', '正在连接游戏。')
        else:
            self._set('waiting_owner', 'connection_not_admitted', '等待游戏连接准入。')

    @property
    def snapshot(self) -> dict:
        target = self._target
        return deepcopy({**self._state, 'enabled': not self._closed,
                         'identity': dict(pid=target[0], process_creation_filetime=target[1], hwnd=target[2]) if target else None,
                         'retry_count': self._failures.get(target, 0),
                         'held': bool(target and (target in self._held or target[1] <= self._hold_before_creation)),
                         'max_attempts': None,
                         'retry_limited': False,
                         'retry_in_seconds': round(max(0.0, self._retry_at.get(target, 0.0) - time.monotonic()), 1),
                         'discovery_pending': self._discovery_pending,
                         'candidates': [{'hwnd': window.hwnd, 'pid': window.process_id,
                                         'label': window.label} for window in self._windows]})

    def close(self) -> None:
        self._require_owner()
        self._closed = True
        self._set('closed', 'application_shutdown', '助手正在关闭。')
