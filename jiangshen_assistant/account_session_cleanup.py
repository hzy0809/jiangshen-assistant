"""Bounded automatic shutdown of the exact installed JiangShen session set.

The controller must first stop all game/bridge workers. The initial process
snapshot is captured with held handles; later processes require a held direct
parent and creation within that parent's actual lifetime. Only exact installed
images (or a stably hashed identical successor launcher) are actionable.
Normal WM_CLOSE is attempted once per verified HWND before a bounded fallback
on an independently rechecked, retained process handle. Exit observations, not
PostMessage/TerminateProcess return values, establish completion. No result of
a game operation is inferred from ending its process lifetime.
"""

from __future__ import annotations

import ctypes
from ctypes import wintypes
from dataclasses import dataclass, replace
import hashlib
import json
import math
import ntpath
import os
from pathlib import Path
import stat
import sys
import threading
import time
import uuid

from .launcher_login import LauncherProcess, _NativeWindows, _ProcessRecord, _Unavailable, _same_path


_MESSAGES = {
    'STARTING': '正在核实并自动关闭已有游戏和登录器。',
    'CLOSING_GAME': '正在关闭已核实的游戏，并等待精确进程退出。',
    'CLOSING_LAUNCHER': '正在关闭已核实的登录器，并等待精确进程退出。',
    'FORCE_EXIT_WAIT': '正常退出未完成，已结束精确核实的进程，正在确认退出。',
    'SETTLING': '已观察到会话进程退出，正在核实后继登录器。',
    'CLOSED': '已确认原会话及已证明归属的后继进程退出。',
    'PAUSED': '已暂停新的会话关闭操作。',
    'STOPPING': '正在停止会话关闭操作。',
    'STOPPED': '会话关闭操作已停止，未完成的退出不计为成功。',
    'UNSUPPORTED_PLATFORM': '自动会话清理仅支持 Windows。',
    'INSTALLATION_UNAVAILABLE': '无法核实明确的游戏和登录器安装路径。',
    'IDENTITY_UNAVAILABLE': '无法核实会话进程的路径、创建时间或退出身份；已停止新操作。',
    'IDENTITY_WAIT': '正在有界等待会话关闭期间出现的暂不可读进程消失或取得可核实身份。',
    'IDENTITY_WAIT_TIMEOUT': '会话关闭期间的进程身份在限定时间内仍无法核实；清理未完成。',
    'DISCOVERY_CHANGED': '会话进程枚举持续变化，无法取得完整的精确身份集合。',
    'SUCCESSOR_UNVERIFIED': '出现无法证明属于原会话的后续进程；已停止新操作。',
    'ORIGINAL_IDENTITY_CHANGED': '原绑定游戏的精确创建身份已改变；未操作替代进程。',
    'FORCE_EXIT_FAILED': '精确进程的自动结束未能执行；退出未确认。',
    'EXIT_NOT_CONFIRMED': '精确进程的退出未在限定时间内确认。',
    'CLEANUP_TIMEOUT': '会话清理未在限定时间内完成；未继续登录。',
    'PROCESS_LIMIT': '会话后继进程数量超过本次自动清理界限。',
    'DIAGNOSTIC_UNAVAILABLE': '无法保存会话清理凭据，未继续关闭进程。',
    'DRIVER_UNAVAILABLE': '自动会话清理发生未确认异常；已停止新操作。',
}


@dataclass(frozen=True)
class AutomaticCloseStatus:
    state: str = 'IDLE'
    code: str = 'STARTING'
    message: str = _MESSAGES['STARTING']
    operation_id: str | None = None
    game: LauncherProcess | None = None
    game_exit_confirmed: bool = False
    game_exit_filetime: int | None = None
    preexisting_processes: tuple[LauncherProcess, ...] = ()
    owned_launchers: tuple[LauncherProcess, ...] = ()
    owned_processes: tuple[LauncherProcess, ...] = ()
    close_attempted_processes: tuple[LauncherProcess, ...] = ()
    close_posted_processes: tuple[LauncherProcess, ...] = ()
    forced_attempted_processes: tuple[LauncherProcess, ...] = ()
    forced_terminated_processes: tuple[LauncherProcess, ...] = ()
    exited_processes: tuple[LauncherProcess, ...] = ()
    complete: bool = False
    human_required: bool = False
    handles_retained: bool = False
    observing: bool = False
    diagnostics_path: str | None = None
    pending_identity_count: int = 0

    def as_dict(self) -> dict:
        result = {key: value for key, value in vars(self).items()
                  if key not in ('game', 'preexisting_processes', 'owned_launchers', 'owned_processes',
                                 'close_attempted_processes', 'close_posted_processes',
                                 'forced_attempted_processes', 'forced_terminated_processes', 'exited_processes')}
        result['game'] = self.game.as_dict() if self.game else None
        for key in ('preexisting_processes', 'owned_launchers', 'owned_processes',
                    'close_attempted_processes', 'close_posted_processes',
                    'forced_attempted_processes', 'forced_terminated_processes', 'exited_processes'):
            result[key] = [item.as_dict() for item in getattr(self, key)]
        result['normal_close_sent'] = any(item.kind == 'game' for item in self.close_posted_processes)
        result['cleanup_complete'] = self.complete
        return result


class AutomaticSessionCloser:
    """One asynchronous cleanup operation; stop/pause gate every mutation."""

    def __init__(self, launcher_path: str | Path | None = None,
                 game_path: str | Path | None = None, *, diagnostics_dir: str | Path | None = None,
                 close_timeout: float = 10.0, force_timeout: float = 5.0,
                 operation_timeout: float = 40.0, quiet_interval: float = 2.0) -> None:
        install = Path(os.environ.get('LOCALAPPDATA', '')) / 'JiangShen'
        self._launcher_path = Path(launcher_path) if launcher_path is not None else install / 'launcher' / 'launcher' / 'Launcher.exe'
        self._game_path = Path(game_path) if game_path is not None else install / 'pc' / 'js.exe'
        self._diagnostics_dir = Path(diagnostics_dir) if diagnostics_dir is not None else Path(__file__).resolve().parents[1] / 'data' / 'session-cleanup'
        for value in (close_timeout, force_timeout, operation_timeout, quiet_interval):
            if type(value) not in (int, float) or not math.isfinite(value) or value <= 0:
                raise ValueError('退出等待时间必须为有限正数。')
        self._close_timeout, self._force_timeout = float(close_timeout), float(force_timeout)
        self._operation_timeout, self._quiet_interval = float(operation_timeout), float(quiet_interval)
        self._lock, self._wake = threading.RLock(), threading.Event()
        self._stopped = self._paused = False
        self._paused_at = 0.0
        self._thread = None
        self._native = None
        self._status = AutomaticCloseStatus()
        self._expected = None
        self._initial_stamp = 0
        self._initial_rows = set()
        self._pending_unreadable = {}
        self._deadline = 0.0
        self._normal = {}
        self._posted = {}
        self._window_attempts = set()
        self._forced = {}
        self._terminated = {}
        self._exited = {}
        self._image_seals = {}
        self._stream = None
        self._phase = 'initializing'
        self._phase_record = None
        self._phase_pid = None

    def begin(self, window=None, creation_time=None, *, tid=None) -> AutomaticCloseStatus:
        if window is not None:
            if (type(getattr(window, 'process_id', None)) is not int
                    or type(creation_time) is not int or creation_time <= 0):
                raise ValueError('必须提供原绑定进程的精确创建身份。')
            self._expected = (window.process_id, creation_time)
        with self._lock:
            if self._thread is not None or self._stopped:
                raise ValueError('会话清理只能开始一次。')
            self._status = AutomaticCloseStatus(state='STARTING', operation_id=uuid.uuid4().hex, observing=True)
            self._thread = threading.Thread(target=self._run, name='jiangshen-automatic-session-close', daemon=True)
            self._thread.start()
            return self._status

    def poll(self) -> AutomaticCloseStatus:
        with self._lock:
            return self._status

    def pause(self) -> AutomaticCloseStatus:
        with self._lock:
            if self._status.state not in ('CLOSED', 'BLOCKED', 'STOPPED') and not self._paused:
                self._paused, self._paused_at = True, time.monotonic()
                self._set('PAUSED', 'PAUSED')
            self._wake.set()
            return self._status

    def resume(self) -> AutomaticCloseStatus:
        with self._lock:
            if self._paused and not self._stopped:
                elapsed = time.monotonic() - self._paused_at
                self._deadline += elapsed
                self._normal = {key: (item, stamp + elapsed) for key, (item, stamp) in self._normal.items()}
                self._forced = {key: (item, stamp + elapsed) for key, (item, stamp) in self._forced.items()}
                self._paused, self._paused_at = False, 0.0
                self._set('STARTING', 'STARTING')
            self._wake.set()
            return self._status

    def stop(self) -> AutomaticCloseStatus:
        with self._lock:
            self._stopped = True
            if self._status.state not in ('CLOSED', 'BLOCKED', 'STOPPED'):
                self._set('STOPPING', 'STOPPING')
            self._wake.set()
            return self._status

    def _set(self, state, code, **changes):
        with self._lock:
            if self._paused and not self._stopped and state not in ('CLOSED', 'BLOCKED', 'STOPPED', 'STOPPING'):
                state, code = 'PAUSED', 'PAUSED'
            self._status = replace(self._status, state=state, code=code, message=_MESSAGES[code], **changes)

    @staticmethod
    def _key(record):
        return record.identity.pid, record.identity.creation_time

    def _event(self, event, record=None, **details):
        try:
            data = {'event': event, 'operation_id': self._status.operation_id,
                    'at_unix': time.time(), **details}
            if record is not None:
                data['process'] = record.identity.as_dict()
                data['parent_pid'] = record.parent_pid
            self._stream.write(json.dumps(data, ensure_ascii=False, separators=(',', ':')) + '\n')
            self._stream.flush()
        except (OSError, ValueError, AttributeError):
            raise _Unavailable('DIAGNOSTIC_UNAVAILABLE') from None

    def _failure(self, event, error):
        # Exception text can contain arbitrary input. Keep only fixed stage,
        # exception type and the native error number, alongside held identity.
        native_error = ctypes.get_last_error()
        try:
            self._event(event, self._phase_record, stage=self._phase,
                        exception_type=type(error).__name__,
                        error_code=error.code if isinstance(error, _Unavailable) else None,
                        snapshot_pid=self._phase_pid,
                        win32_error=native_error,
                        exception_winerror=getattr(error, 'winerror', None),
                        exception_errno=getattr(error, 'errno', None))
        except Exception:
            pass

    def _open(self, pid, parent, name, stamp):
        native = self._native
        handle = native.kernel.OpenProcess(0x101000, False, pid)
        if not handle:
            native_error = ctypes.get_last_error()
            return self._unavailable_open(pid, parent, name, native_error)
        try:
            record = native._record(pid, handle, parent)
            if (record.identity.creation_time > stamp
                    or ntpath.basename(record.identity.exe_path).casefold() != name.casefold()):
                raise _Unavailable('DISCOVERY_CHANGED')
            return record
        except Exception as error:
            self._failure('process_identity_read_failure', error)
            native.kernel.CloseHandle(handle)
            raise

    def _defer_unreadable(self, pid, parent, name, native_error):
        """Observe a new row in a closing-parent context; never claim ownership.

        The snapshot parent is only permission to wait, not proof of ancestry.
        The row receives no window message or termination until normal held
        handle, image and exact lifetime checks have independently succeeded.
        """
        key = pid, parent, name.casefold()
        closing = [item for item in self._native.records.values()
                   if item.identity.pid == parent and self._key(item) in self._normal]
        if key in self._initial_rows or not closing:
            return False
        if key not in self._pending_unreadable:
            if len(self._pending_unreadable) >= 16:
                raise _Unavailable('PROCESS_LIMIT')
            self._pending_unreadable[key] = time.monotonic()
            self._event('unreadable_session_row_wait_started', closing[0], snapshot_pid=pid,
                        snapshot_parent_pid=parent, snapshot_name=name,
                        win32_error=native_error, ownership_proven=False,
                        wait_bound='operation_deadline')
            with self._lock:
                self._status = replace(self._status, pending_identity_count=len(self._pending_unreadable))
        return True

    def _resolve_unreadable(self, pid, parent, name, resolution):
        key = pid, parent, name.casefold()
        started = self._pending_unreadable.get(key)
        if started is None:
            return
        self._event('unreadable_session_row_wait_resolved', snapshot_pid=pid,
                    snapshot_parent_pid=parent, snapshot_name=name, resolution=resolution,
                    observed_wall_seconds=time.monotonic() - started)
        self._pending_unreadable.pop(key)
        with self._lock:
            self._status = replace(self._status, pending_identity_count=len(self._pending_unreadable))

    def _unavailable_open(self, pid, parent, name, native_error):
        """Reconcile an unreadable snapshot row without adopting or acting on it.

        Access denied can occur after a short-lived process has begun exiting.
        A fresh complete snapshot can show the row gone. A new unreadable row
        in a held closing-parent context may be observed until the operation
        deadline, without being adopted. A still-live row is excluded only with a queried different
        image path outside the possible held launcher ancestry. A query-only
        handle never enters the actionable process set.
        """
        native = self._native
        self._event('process_open_failed', snapshot_pid=pid, snapshot_parent_pid=parent,
                    snapshot_name=name, access_mask=0x101000, win32_error=native_error)
        self._phase = 'reconcile_failed_open_snapshot'
        fresh_stamp, rows = native.snapshot()
        current = next((row for row in rows if row[0] == pid), None)
        if current is None:
            self._event('unreadable_snapshot_row_gone', snapshot_pid=pid,
                        snapshot_parent_pid=parent, snapshot_name=name,
                        fresh_snapshot_filetime=fresh_stamp, original_win32_error=native_error)
            raise _Unavailable('DISCOVERY_CHANGED')
        if current[1:] != (parent, name) or native_error == 87:
            self._event('unreadable_snapshot_row_changed', snapshot_pid=pid,
                        snapshot_parent_pid=parent, snapshot_name=name,
                        current_parent_pid=current[1], current_name=current[2],
                        fresh_snapshot_filetime=fresh_stamp, original_win32_error=native_error)
            raise _Unavailable('DISCOVERY_CHANGED')
        # SYNCHRONIZE may be denied independently of the limited image-query
        # right. This lesser handle can prove an unrelated image, but cannot
        # prove exit and is never used for closing or terminating anything.
        self._phase = 'query_only_classify_unreadable_row'
        probe_handle = native.kernel.OpenProcess(0x1000, False, pid)
        if not probe_handle:
            probe_error = ctypes.get_last_error()
            if native_error == 5 and probe_error == 5 and self._defer_unreadable(pid, parent, name, probe_error):
                return None
            raise _Unavailable('IDENTITY_UNAVAILABLE')
        try:
            try:
                probe = native._record(pid, probe_handle, parent)
            except _Unavailable as error:
                probe_error = ctypes.get_last_error()
                self._failure('query_only_identity_read_failure', error)
                if native_error == 5 and probe_error == 5 and self._defer_unreadable(pid, parent, name, probe_error):
                    return None
                raise
            if (probe.identity.creation_time > fresh_stamp
                    or ntpath.basename(probe.identity.exe_path).casefold() != name.casefold()):
                raise _Unavailable('DISCOVERY_CHANGED')
            exact = (_same_path(probe.identity.exe_path, self._game_path)
                     or _same_path(probe.identity.exe_path, self._launcher_path))
            possible_successor = (name.casefold() == 'launcher.exe'
                                  and any(item.identity.pid == parent for item in native.records.values()))
            if not exact and not possible_successor:
                self._event('unrelated_unreadable_process_excluded', probe,
                            original_win32_error=native_error, query_access_mask=0x1000)
                self._resolve_unreadable(pid, parent, name, 'verified_unrelated_image')
                return None
            self._event('target_process_access_incomplete', probe,
                        original_win32_error=native_error, query_access_mask=0x1000,
                        possible_held_successor=possible_successor)
            if native_error == 5 and self._defer_unreadable(pid, parent, name, native_error):
                return None
            raise _Unavailable('IDENTITY_UNAVAILABLE')
        finally:
            native.kernel.CloseHandle(probe_handle)

    @staticmethod
    def _file_seal(value):
        if not stat.S_ISREG(value.st_mode) or not value.st_ino:
            raise _Unavailable()
        return value.st_dev, value.st_ino, value.st_size, value.st_mtime_ns, value.st_ctime_ns

    def _path_file_seal(self, path):
        # Windows Python can expose the file's change time via fstat but its
        # birth time via stat in st_ctime_ns. Reopen the pathname and compare
        # handle metadata with handle metadata, retaining the change-time
        # guard as well as file identity, size and modification time.
        with Path(path).open('rb') as stream:
            return self._checked_file_seal(os.fstat(stream.fileno()), path, 'path_reopened_handle')

    def _hash(self, path):
        try:
            self._phase = 'successor_image_open'
            with Path(path).open('rb') as stream:
                self._phase = 'successor_image_fstat_before'
                before = self._checked_file_seal(os.fstat(stream.fileno()), path, 'handle_before')
                digest = hashlib.sha256()
                self._phase = 'successor_image_read'
                for block in iter(lambda: stream.read(1024 * 1024), b''):
                    if self._stopped:
                        raise _Unavailable()
                    digest.update(block)
                self._phase = 'successor_image_fstat_after'
                after = self._checked_file_seal(os.fstat(stream.fileno()), path, 'handle_after')
                self._phase = 'successor_image_path_reopen'
                current = self._path_file_seal(path)
                self._phase = 'successor_image_seal_compare'
                if before != after or before != current:
                    self._event('successor_image_seal_changed', self._phase_record,
                                image_path=str(path), handle_before=before,
                                handle_after=after, path_after=current)
                    raise _Unavailable()
            return before, digest.digest()
        except OSError as error:
            self._failure('successor_image_read_failure', error)
            raise _Unavailable() from None

    def _checked_file_seal(self, value, path, source):
        try:
            return self._file_seal(value)
        except _Unavailable:
            self._event('successor_image_seal_unavailable', self._phase_record,
                        image_path=str(path), stat_source=source,
                        mode=value.st_mode, device=value.st_dev, inode=value.st_ino,
                        size=value.st_size, modified_ns=value.st_mtime_ns,
                        changed_ns=value.st_ctime_ns)
            raise

    def _image(self, record, *, successor=False):
        path = record.identity.exe_path
        if _same_path(path, self._game_path):
            record.identity = replace(record.identity, kind='game')
            return True
        if _same_path(path, self._launcher_path):
            record.identity = replace(record.identity, kind='launcher')
            return True
        if successor and ntpath.basename(path).casefold() == 'launcher.exe':
            installed, candidate = self._hash(self._launcher_path), self._hash(path)
            if installed[1] == candidate[1]:
                record.identity = replace(record.identity, kind='launcher')
                self._image_seals[self._key(record)] = (installed[0], candidate[0])
                return True
        return False

    def _capture(self):
        native = self._native
        stamp, rows = native.snapshot()
        self._initial_stamp = stamp
        self._initial_rows = {(pid, parent, name.casefold()) for pid, parent, name in rows}
        for pid, parent, name in rows:
            if name.casefold() not in ('launcher.exe', 'js.exe'):
                continue
            self._phase, self._phase_record, self._phase_pid = 'capture_open_identity', None, pid
            record = self._open(pid, parent, name, stamp)
            if record is None:
                continue
            if self._image(record):
                native.records[self._key(record)] = record
                self._event('preexisting_captured', record)
            else:
                native.kernel.CloseHandle(record.handle)
        if self._expected is not None:
            expected_pid, expected_creation = self._expected
            if any(item.identity.pid == expected_pid and item.identity.creation_time != expected_creation
                   for item in native.records.values()):
                raise _Unavailable('ORIGINAL_IDENTITY_CHANGED')
        if len(native.records) > 16:
            raise _Unavailable('PROCESS_LIMIT')
        games = [item.identity for item in native.records.values() if item.identity.kind == 'game']
        game = next((item for item in games if (item.pid, item.creation_time) == self._expected), games[0] if games else None)
        with self._lock:
            self._status = replace(self._status, game=game,
                                   preexisting_processes=tuple(item.identity for item in native.records.values()),
                                   handles_retained=bool(native.records))

    def _discover(self):
        native = self._native
        self._phase, self._phase_record = 'discover_snapshot', None
        stamp, rows = native.snapshot()
        current_rows = {(pid, parent, name.casefold()) for pid, parent, name in rows}
        for pid, parent, name in tuple(self._pending_unreadable):
            if (pid, parent, name) not in current_rows:
                # This proves only that the unreadable snapshot row vanished
                # or changed. It does not claim its process identity or exit.
                self._resolve_unreadable(pid, parent, name, 'row_absent_from_fresh_snapshot')
        for pid, parent_pid, name in rows:
            if name.casefold() not in ('launcher.exe', 'js.exe'):
                continue
            self._phase_pid = pid
            # A verified executable image cannot change in a live process.
            # Reopening that same PID and rereading its path during teardown
            # adds a failure window before the held handle becomes signalled.
            # A live held lifetime covering this snapshot is enough to match
            # the row. Exited handles still follow the open/creation check
            # below, so a reused PID cannot be silently ignored or adopted.
            held_live = []
            for held in native.records.values():
                if held.identity.pid != pid:
                    continue
                self._phase, self._phase_record = 'discover_held_lifetime', held
                if native.alive(held):
                    born, _ = native.times(held)
                    if born != held.identity.creation_time:
                        raise _Unavailable()
                    held_live.append(held)
            if held_live:
                if (len(held_live) != 1 or held_live[0].identity.creation_time > stamp
                        or ntpath.basename(held_live[0].identity.exe_path).casefold() != name.casefold()):
                    raise _Unavailable('DISCOVERY_CHANGED')
                continue
            self._phase, self._phase_record = 'discover_open_identity', None
            try:
                record = self._open(pid, parent_pid, name, stamp)
            except _Unavailable as error:
                # A held process can exit between the snapshot and the image
                # read. Observe its held lifetime on a fresh pass; do not turn
                # that ordinary exit race into an ownership failure.
                held = [item for item in native.records.values() if item.identity.pid == pid]
                if held and all(not native.alive(item) for item in held):
                    self._phase_record = held[0]
                    self._failure('held_exit_discovery_race', error)
                    raise _Unavailable('DISCOVERY_CHANGED')
                raise
            if record is None:
                continue
            key, keep = self._key(record), False
            try:
                self._phase, self._phase_record = 'discover_classify_identity', record
                if key in native.records:
                    if not _same_path(record.identity.exe_path, native.records[key].identity.exe_path):
                        raise _Unavailable()
                    self._resolve_unreadable(pid, parent_pid, name, 'verified_held_identity')
                    continue
                direct = [item for item in native.records.values() if item.identity.pid == parent_pid]
                proven = False
                for parent in direct:
                    self._phase, self._phase_record = 'discover_parent_liveness', parent
                    parent_alive = native.alive(parent)
                    self._phase = 'discover_parent_times'
                    born, died = native.times(parent)
                    if (born <= record.identity.creation_time <= stamp
                            and ((parent_alive and not died) or (died and record.identity.creation_time <= died))):
                        proven = True
                        break
                self._phase, self._phase_record = 'discover_successor_image', record
                exact = _same_path(record.identity.exe_path, self._game_path) or _same_path(record.identity.exe_path, self._launcher_path)
                if not exact and not (proven and name.casefold() == 'launcher.exe'):
                    self._resolve_unreadable(pid, parent_pid, name, 'verified_unrelated_image')
                    continue  # Unrelated installations never receive a close or kill.
                if record.identity.creation_time <= self._initial_stamp or not proven:
                    raise _Unavailable('SUCCESSOR_UNVERIFIED')
                if not self._image(record, successor=True):
                    raise _Unavailable('SUCCESSOR_UNVERIFIED')
                if len(native.records) >= 16:
                    raise _Unavailable('PROCESS_LIMIT')
                native.records[key], keep = record, True
                self._event('successor_captured', record)
                self._resolve_unreadable(pid, parent_pid, name, 'verified_successor_identity')
            finally:
                if not keep:
                    native.kernel.CloseHandle(record.handle)

    def _verify(self, record):
        current = self._native._record(record.identity.pid, record.handle, record.parent_pid)
        if (self._key(current) != self._key(record)
                or not _same_path(current.identity.exe_path, record.identity.exe_path)):
            raise _Unavailable()
        seals = self._image_seals.get(self._key(record))
        if seals is not None:
            try:
                if (self._path_file_seal(self._launcher_path), self._path_file_seal(record.identity.exe_path)) != seals:
                    raise _Unavailable()
            except OSError:
                raise _Unavailable() from None
        elif not self._image(current):
            raise _Unavailable()

    def _close(self, record):
        native, key = self._native, self._key(record)
        self._phase, self._phase_record = 'close_held_liveness', record
        if not native.alive(record):
            return
        if key not in self._normal:
            self._phase = 'normal_close_verify_identity'
            try:
                self._verify(record)
            except _Unavailable:
                # The exact retained handle can become signalled after _observe.
                # Its exit is recorded on the next pass, never as a close result.
                if not native.alive(record):
                    return
                raise
            self._phase = 'normal_close_windows'
            try:
                windows = native.windows(record)
            except _Unavailable:
                if not native.alive(record):
                    return
                raise
            with self._lock:
                if self._stopped or self._paused:
                    return
                self._normal[key] = (record.identity, time.monotonic())
                self._event('normal_close_begin', record, window_count=len(windows))
                for window in windows:
                    window_key = (key, window.hwnd, window.tid)
                    if window_key in self._window_attempts:
                        continue
                    self._window_attempts.add(window_key)
                    self._event('wm_close_attempt', record, hwnd=window.hwnd, tid=window.tid)
                    self._phase = 'normal_close_post'
                    try:
                        native.close_window(window)
                    except _Unavailable:
                        self._event('wm_close_unconfirmed', record, hwnd=window.hwnd, tid=window.tid)
                    else:
                        self._posted[key] = window
                        self._event('wm_close_posted', record, hwnd=window.hwnd, tid=window.tid)
        # After the one normal-close pass, observe the original process handle
        # throughout the grace period. Do not query a dismantling image again
        # or repeat a window action. Forced termination below still requires a
        # fresh complete identity check and a separate verified handle.
        if time.monotonic() - self._normal[key][1] < self._close_timeout:
            return
        if key in self._forced:
            if time.monotonic() - self._forced[key][1] >= self._force_timeout:
                raise _Unavailable('EXIT_NOT_CONFIRMED')
            self._set('CLOSING_GAME' if record.identity.kind == 'game' else 'CLOSING_LAUNCHER', 'FORCE_EXIT_WAIT')
            return
        if not native.alive(record):
            return
        self._phase = 'force_verify_identity'
        try:
            self._verify(record)
        except _Unavailable:
            if not native.alive(record):
                return
            raise
        handle = native.kernel.OpenProcess(0x101001, False, record.identity.pid)
        if not handle:
            if not native.alive(record):
                return
            raise _Unavailable('FORCE_EXIT_FAILED')
        try:
            self._phase = 'force_opened_identity'
            force_record = native._record(record.identity.pid, handle, record.parent_pid)
            if (self._key(force_record) != key or not _same_path(force_record.identity.exe_path, record.identity.exe_path)):
                raise _Unavailable()
            with self._lock:
                if self._stopped or self._paused or not native.alive(record):
                    return
                self._forced[key] = (record.identity, time.monotonic())
                self._event('terminate_exact_attempt', record, reason='normal_close_timeout')
                self._phase = 'force_terminate'
                if not native.kernel.TerminateProcess(handle, 0xE0000001):
                    if native.alive(record):
                        raise _Unavailable('FORCE_EXIT_FAILED')
                else:
                    self._terminated[key] = record.identity
                    self._event('terminate_exact_requested', record)
        finally:
            native.kernel.CloseHandle(handle)

    def _observe(self):
        native, live = self._native, []
        for key, record in native.records.items():
            self._phase, self._phase_record = 'observe_held_liveness', record
            alive = native.alive(record)
            self._phase = 'observe_held_times'
            born, died = native.times(record)
            if born != record.identity.creation_time or (not alive and not died):
                self._event('lifetime_observation_rejected', record, alive=alive,
                            observed_creation=born, observed_exit=died)
                raise _Unavailable()
            if alive:
                live.append(record)
            elif key not in self._exited:
                self._exited[key] = record.identity
                self._event('exit_observed', record, exit_filetime=died)
                if self._status.game == record.identity:
                    with self._lock:
                        self._status = replace(self._status, game_exit_confirmed=True, game_exit_filetime=died)
        with self._lock:
            self._status = replace(self._status,
                owned_processes=tuple(item.identity for item in live),
                owned_launchers=tuple(item.identity for item in native.records.values() if item.identity.kind == 'launcher'),
                close_attempted_processes=tuple(item[0] for item in self._normal.values()),
                close_posted_processes=tuple(self._posted.values()),
                forced_attempted_processes=tuple(item[0] for item in self._forced.values()),
                forced_terminated_processes=tuple(self._terminated.values()),
                exited_processes=tuple(self._exited.values()), handles_retained=bool(native.records))
        return live

    def _run(self):
        quiet_since = None
        try:
            if sys.platform != 'win32':
                raise _Unavailable('UNSUPPORTED_PLATFORM')
            if (not self._launcher_path.is_absolute() or not self._game_path.is_absolute()
                    or self._launcher_path.name.casefold() != 'launcher.exe'
                    or self._game_path.name.casefold() != 'js.exe'
                    or _same_path(self._launcher_path, self._game_path)):
                raise _Unavailable('INSTALLATION_UNAVAILABLE')
            try:
                self._diagnostics_dir.mkdir(parents=True, exist_ok=True)
                path = self._diagnostics_dir / (self._status.operation_id + '.jsonl')
                self._stream = path.open('x', encoding='utf-8')
                with self._lock:
                    self._status = replace(self._status, diagnostics_path=str(path))
            except OSError:
                raise _Unavailable('DIAGNOSTIC_UNAVAILABLE') from None
            self._native = _NativeWindows(self._launcher_path, self._game_path)
            terminate = self._native.kernel.TerminateProcess
            terminate.argtypes, terminate.restype = [wintypes.HANDLE, wintypes.UINT], wintypes.BOOL
            self._deadline = time.monotonic() + self._operation_timeout
            self._phase = 'capture_initial_set'
            self._capture()
            while not self._stopped:
                if self._paused:
                    quiet_since = None
                else:
                    if time.monotonic() >= self._deadline:
                        if self._pending_unreadable:
                            self._event('unreadable_session_rows_timed_out',
                                        snapshot_rows=[{'pid': pid, 'parent_pid': parent, 'name': name}
                                                       for pid, parent, name in self._pending_unreadable])
                            raise _Unavailable('IDENTITY_WAIT_TIMEOUT')
                        raise _Unavailable('CLEANUP_TIMEOUT')
                    try:
                        self._discover()
                    except _Unavailable as error:
                        if error.code != 'DISCOVERY_CHANGED':
                            raise
                        self._failure('discovery_snapshot_retry', error)
                        quiet_since = None
                        self._wake.wait(0.25)
                        self._wake.clear()
                        continue
                    live = self._observe()
                    if live:
                        quiet_since = None
                        candidates = [item for item in live if item.identity.kind == 'game']
                        state = 'CLOSING_GAME' if candidates else 'CLOSING_LAUNCHER'
                        self._set(state, state)
                        for record in candidates or live:
                            self._close(record)
                    elif self._pending_unreadable:
                        # Continue observing and closing already verified
                        # records above, but never admit relaunch or a quiet
                        # completion while any unreadable row remains.
                        quiet_since = None
                        self._set('SETTLING', 'IDENTITY_WAIT')
                    else:
                        now = time.monotonic()
                        quiet_since = now if quiet_since is None else quiet_since
                        if now - quiet_since >= self._quiet_interval:
                            with self._lock:
                                if self._paused or self._stopped:
                                    continue
                                self._event('cleanup_complete')
                                self._set('CLOSED', 'CLOSED', complete=True)
                            return
                        self._set('SETTLING', 'SETTLING')
                self._wake.wait(0.25)
                self._wake.clear()
        except _Unavailable as error:
            self._failure('operation_failure', error)
            self._set('BLOCKED', error.code if error.code in _MESSAGES else 'IDENTITY_UNAVAILABLE')
        except Exception as error:
            self._failure('operation_failure', error)
            self._set('BLOCKED', 'DRIVER_UNAVAILABLE')
        finally:
            if self._native is not None:
                try:
                    self._observe()
                except Exception as error:
                    self._failure('final_observation_failure', error)
                self._native.close_handles()
            with self._lock:
                if self._stopped and self._status.state != 'CLOSED':
                    self._set('STOPPED', 'STOPPED')
                self._status = replace(self._status, handles_retained=False, observing=False)
            if self._stream is not None:
                try:
                    self._event('operation_finished', state=self._status.state, code=self._status.code,
                                complete=self._status.complete)
                    self._stream.close()
                except Exception:
                    pass
