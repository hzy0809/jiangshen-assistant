"""Close one explicitly bound game through its normal Windows close path.

The controller must first stop its tasks and bridge work, then pass the original
``WindowInfo`` and ``identity_for(window).creation_time`` to ``begin``. Passing
the captured ``tid`` is recommended. This is a single-use operation; retain the
same closer while it is WAITING so a close request cannot be retried implicitly.
``begin``, ``poll``, ``pause``, ``resume`` and ``stop`` do not enumerate processes
or wait for exits on the calling thread. ``poll`` returns an immutable,
credential-free snapshot. ``pause`` prevents new close requests while retaining
read-only observation; ``resume`` continues the same one-attempt ledger.

Only the original HWND may receive the game's one WM_CLOSE attempt. Successor
launchers must be direct children of the held game process, use the configured
installation executable (or a stably read SHA256-identical Launcher.exe), and
have been born after the close attempt began and within the game's actual
creation/exit interval. A snapshot parent PID alone is never
enough. Missing intermediates, pre-existing sessions and unreadable identities
remain unowned. WAITING continues observation and can progress when its cause
disappears; it never retries an attempted WM_CLOSE or forces process termination.

``stop`` prevents further close requests. Requests already posted cannot be
withdrawn. STOPPED acknowledges that no more window actions will occur; if
``handles_retained`` is true, a daemon worker continues only read-only exit
observation until the held processes exit. This preserves lifetime evidence even
if the controller stops polling. ``complete`` is true only in CLOSED, after exact
process exits and a quiet interval of successful relevant-process snapshots.
It does not exclude a future detached launch; the next login must still perform
its own existing-session preflight.

The component deliberately cannot adopt a launcher left by a game that exited
before its handle was acquired. No login, credentials, task cleanup, server/role
inference, process start, UI confirmation click or TerminateProcess is provided.
"""

from __future__ import annotations

import ctypes
from ctypes import wintypes
from dataclasses import dataclass, replace
from enum import Enum
import hashlib
import math
import ntpath
import os
from pathlib import Path
import sys
import stat
import threading
import time
import uuid

from .launcher_login import (
    LauncherProcess, _NativeWindows, _ProcessRecord, _Unavailable, _same_path,
)
from .windows import WindowInfo


__all__ = [
    "NormalSessionCloser", "NormalSessionCloseError", "NormalCloseState",
    "NormalCloseStatus", "UnownedSession",
]


class NormalCloseState(str, Enum):
    IDLE = "IDLE"
    STARTING = "STARTING"
    CLOSING_GAME = "CLOSING_GAME"
    CLOSING_LAUNCHER = "CLOSING_LAUNCHER"
    SETTLING = "SETTLING"
    WAITING = "WAITING"
    PAUSED = "PAUSED"
    CLOSED = "CLOSED"
    STOPPING = "STOPPING"
    STOPPED = "STOPPED"


_MESSAGES = {
    "IDLE": "尚未开始正常退出当前会话。",
    "STARTING": "正在重新核对原绑定游戏的窗口、进程和安装路径。",
    "CLOSING_GAME": "已请求原绑定游戏正常退出，正在确认进程退出。",
    "CLOSING_LAUNCHER": "正在正常关闭已证明由原游戏启动的后继登录器并确认退出。",
    "SETTLING": "原游戏和已归属登录器已退出，正在观察是否仍有游戏或登录器。",
    "CLOSED": "原游戏及已归属后继登录器已确认退出，当前未发现其他游戏或登录器。",
    "STOPPING": "正在停止正常退出操作；已经发送的关闭请求无法撤回。",
    "STOPPED": "已停止发送关闭请求，保留仍运行的进程并只读等待已持有进程退出。",
    "PAUSED": "已暂停新的正常关闭请求，继续只读观察当前会话。",
    "RESUMING": "正在恢复原会话的正常退出观察，不会重复已经尝试的关闭请求。",
    "UNSUPPORTED_PLATFORM": "正常退出组件仅支持 Windows。",
    "ORIGINAL_IDENTITY_UNAVAILABLE": "无法取得原游戏的进程句柄和创建身份；不会根据历史 PID 接管其他窗口。",
    "ORIGINAL_IDENTITY_CHANGED": "原游戏的创建身份或安装路径已改变；不会关闭当前窗口。",
    "ORIGINAL_WINDOW_CHANGED": "原绑定 HWND、PID 或窗口线程已改变；等待本人处理原游戏。",
    "FOREIGN_SESSION": "存在无法归属本次退出的游戏或登录器；等待本人处理，不会接管或关闭它。",
    "DISCOVERY_CHANGED": "进程在枚举期间发生变化，正在重新只读观察。",
    "IDENTITY_UNAVAILABLE": "暂时无法核实相关进程的身份或真实生命周期，暂停发送关闭请求。",
    "GAME_EXIT_NOT_CONFIRMED": "尚未确认原游戏退出；请本人处理游戏的正常退出提示，不会重复关闭或强制结束。",
    "LAUNCHER_WINDOW_UNAVAILABLE": "已归属登录器没有唯一可核实的正常关闭窗口，等待本人处理。",
    "LAUNCHER_EXIT_NOT_CONFIRMED": "尚未确认已归属登录器退出；请本人处理退出提示，不会重复关闭或强制结束。",
    "CLOSE_UNCERTAIN": "本次正常关闭调用未能确认，正在只读等待退出；不会重复发送关闭请求。",
    "DRIVER_UNAVAILABLE": "正常退出组件无法继续核实现场，已停止新关闭操作；请本人处理当前会话。",
}


class NormalSessionCloseError(RuntimeError):
    """Fixed, credential-free caller error."""


@dataclass(frozen=True)
class UnownedSession:
    pid: int
    parent_pid: int
    process_name: str
    creation_time: int
    exe_path: str
    reason: str

    def as_dict(self) -> dict:
        return {"pid": self.pid, "parent_pid": self.parent_pid,
                "process_name": self.process_name,
                "process_creation_filetime": self.creation_time,
                "exe_path": self.exe_path, "reason": self.reason}


@dataclass(frozen=True)
class NormalCloseStatus:
    state: NormalCloseState = NormalCloseState.IDLE
    code: str = "IDLE"
    message: str = _MESSAGES["IDLE"]
    operation_id: str | None = None
    game: LauncherProcess | None = None
    game_exit_confirmed: bool = False
    game_exit_filetime: int | None = None
    owned_launchers: tuple[LauncherProcess, ...] = ()
    launcher_image_sha256: str | None = None
    foreign_processes: tuple[UnownedSession, ...] = ()
    close_attempted_processes: tuple[LauncherProcess, ...] = ()
    close_posted_processes: tuple[LauncherProcess, ...] = ()
    complete: bool = False
    human_required: bool = False
    handles_retained: bool = False
    observing: bool = False

    @property
    def normal_close_sent(self) -> bool:
        return any(item.kind == "game" for item in self.close_posted_processes)

    @property
    def cleanup_complete(self) -> bool:
        return self.complete

    def as_dict(self) -> dict:
        return {"state": self.state.value, "code": self.code, "message": self.message,
                "operation_id": self.operation_id,
                "game": self.game.as_dict() if self.game else None,
                "game_exit_confirmed": self.game_exit_confirmed,
                "game_exit_filetime": self.game_exit_filetime,
                "owned_launchers": [item.as_dict() for item in self.owned_launchers],
                "launcher_image_sha256": self.launcher_image_sha256,
                "foreign_processes": [item.as_dict() for item in self.foreign_processes],
                "close_attempted_processes": [item.as_dict() for item in self.close_attempted_processes],
                "close_posted_processes": [item.as_dict() for item in self.close_posted_processes],
                "normal_close_sent": self.normal_close_sent,
                "complete": self.complete, "cleanup_complete": self.complete,
                "human_required": self.human_required,
                "handles_retained": self.handles_retained, "observing": self.observing}


class NormalSessionCloser:
    """One bound game's normal exit; all native discovery runs on its worker."""

    def __init__(self, launcher_path: str | Path | None = None,
                 game_path: str | Path | None = None, *, close_timeout: float = 20.0,
                 quiet_interval: float = 2.0, poll_interval: float = 0.25) -> None:
        install = Path(os.environ.get("LOCALAPPDATA", "")) / "JiangShen"
        self._launcher_path = Path(launcher_path) if launcher_path is not None else install / "launcher" / "launcher" / "Launcher.exe"
        self._game_path = Path(game_path) if game_path is not None else install / "pc" / "js.exe"
        if (not self._launcher_path.is_absolute() or not self._game_path.is_absolute()
                or _same_path(self._launcher_path, self._game_path)):
            raise NormalSessionCloseError("游戏和登录器必须使用各自明确的安装绝对路径。") from None
        for value in (close_timeout, quiet_interval, poll_interval):
            if type(value) not in (int, float) or not math.isfinite(value) or value <= 0:
                raise NormalSessionCloseError("退出等待和观察间隔必须是有限正数。") from None
        self._close_timeout = float(close_timeout)
        self._quiet_interval = float(quiet_interval)
        self._poll_interval = min(1.0, max(0.05, float(poll_interval)))
        self._lock = threading.RLock()
        self._stop_requested = threading.Event()
        self._pause_requested = threading.Event()
        self._resume_state = (NormalCloseState.STARTING, "STARTING", False)
        self._actions_finished = False
        self._status = NormalCloseStatus()
        self._thread: threading.Thread | None = None
        self._native: _NativeWindows | None = None
        self._root: _ProcessRecord | None = None
        self._window: WindowInfo | None = None
        self._expected_creation = 0
        self._expected_tid: int | None = None
        self._close_boundary: int | None = None
        self._launchers: dict[tuple[int, int], _ProcessRecord] = {}
        self._attempted: dict[tuple[int, int], tuple[LauncherProcess, float]] = {}
        self._posted: dict[tuple[int, int], LauncherProcess] = {}
        self._uncertain: set[tuple[int, int]] = set()
        self._launcher_image_seals: dict[tuple[int, int], tuple[tuple, tuple]] = {}
        self._launcher_image_sha256: str | None = None

    def begin(self, window: WindowInfo, creation_time: int, *, tid: int | None = None) -> NormalCloseStatus:
        """Begin once after controller cleanup; ``creation_time`` comes from identity_for."""
        if (not isinstance(window, WindowInfo)
                or type(window.hwnd) is not int or not 0 < window.hwnd < 1 << (8 * ctypes.sizeof(ctypes.c_void_p))
                or type(window.process_id) is not int or not 0 < window.process_id <= 0xffffffff
                or type(creation_time) is not int or not 0 < creation_time < 1 << 64
                or (tid is not None and (type(tid) is not int or not 0 < tid <= 0xffffffff))):
            raise NormalSessionCloseError("必须提供原绑定窗口和已核实的进程创建身份。") from None
        with self._lock:
            if self._thread is not None or self._stop_requested.is_set():
                raise NormalSessionCloseError("正常退出组件只能开始一次，请保留并轮询当前操作。") from None
            self._window, self._expected_creation, self._expected_tid = window, creation_time, tid
            target = LauncherProcess(window.process_id, creation_time, str(self._game_path),
                                     window.hwnd, tid or 0, "game")
            self._status = NormalCloseStatus(
                state=NormalCloseState.STARTING, code="STARTING", message=_MESSAGES["STARTING"],
                operation_id=str(uuid.uuid4()), game=target, observing=True)
            self._thread = threading.Thread(target=self._run, name="jiangshen-normal-session-close", daemon=True)
            self._thread.start()
            return self._status

    def poll(self) -> NormalCloseStatus:
        """Memory-only snapshot, suitable for Tk's ``after`` callback."""
        with self._lock:
            return self._status

    def pause(self) -> NormalCloseStatus:
        """Freeze new WM_CLOSE requests before returning; observation continues."""
        with self._lock:
            if (self._thread is None or self._stop_requested.is_set()
                    or self._status.state in (NormalCloseState.CLOSED, NormalCloseState.STOPPED)
                    or self._pause_requested.is_set()):
                return self._status
            self._resume_state = (self._status.state, self._status.code, self._status.human_required)
            self._pause_requested.set()
            self._set(NormalCloseState.PAUSED, "PAUSED", human_required=False)
            return self._status

    def resume(self) -> NormalCloseStatus:
        """Continue the same ownership evidence and one-attempt close ledger."""
        with self._lock:
            if not self._pause_requested.is_set() or self._stop_requested.is_set():
                return self._status
            self._pause_requested.clear()
            if self._actions_finished:
                state, code, human_required = self._resume_state
                self._set(state, code, human_required=human_required)
            else:
                self._set(NormalCloseState.STARTING, "RESUMING", human_required=False)
            return self._status

    def stop(self) -> NormalCloseStatus:
        """Cancel future WM_CLOSE requests without joining the worker or ending processes."""
        with self._lock:
            if self._status.state == NormalCloseState.CLOSED:
                return self._status
            self._stop_requested.set()
            active = self._thread is not None and self._thread.is_alive()
            self._set(NormalCloseState.STOPPING if active else NormalCloseState.STOPPED,
                      "STOPPING" if active else "STOPPED", human_required=False,
                      observing=active)
            return self._status

    def _set(self, state: NormalCloseState, code: str, **changes) -> None:
        with self._lock:
            if (self._pause_requested.is_set() and not self._stop_requested.is_set()
                    and state not in (NormalCloseState.CLOSED, NormalCloseState.STOPPED,
                                      NormalCloseState.STOPPING, NormalCloseState.PAUSED)):
                self._resume_state = (state, code, changes.get("human_required", self._status.human_required))
                state, code = NormalCloseState.PAUSED, "PAUSED"
                changes["human_required"] = False
            self._status = replace(self._status, state=state, code=code,
                                   message=_MESSAGES[code], **changes)

    def _wait(self, code: str) -> None:
        self._set(NormalCloseState.WAITING, code,
                  human_required=code != "DISCOVERY_CHANGED")

    @staticmethod
    def _key(record: _ProcessRecord) -> tuple[int, int]:
        return record.identity.pid, record.identity.creation_time

    def _acquire_root(self) -> None:
        native, window = self._native, self._window
        assert native is not None and window is not None
        handle = native.kernel.OpenProcess(0x101000, False, window.process_id)
        if not handle:
            raise _Unavailable("ORIGINAL_IDENTITY_UNAVAILABLE")
        keep = False
        try:
            record = native._record(window.process_id, handle, 0)
            if record.identity.creation_time != self._expected_creation or record.identity.kind != "game":
                raise _Unavailable("ORIGINAL_IDENTITY_CHANGED")
            native.records[self._key(record)] = record
            self._root, keep = record, True
            with self._lock:
                self._status = replace(self._status, handles_retained=True)
        finally:
            if not keep:
                native.kernel.CloseHandle(handle)

    def _root_alive(self) -> bool:
        native, root = self._native, self._root
        assert native is not None and root is not None
        alive = native.alive(root)
        born, died = native.times(root)
        if born != self._expected_creation or (not alive and not died):
            raise _Unavailable()
        if not alive:
            with self._lock:
                self._status = replace(self._status, game_exit_confirmed=True, game_exit_filetime=died)
        return alive

    @staticmethod
    def _file_identity(value) -> tuple:
        if not stat.S_ISREG(value.st_mode) or not value.st_ino:
            raise _Unavailable()
        return (value.st_dev, value.st_ino, value.st_size,
                value.st_mtime_ns, value.st_ctime_ns)

    def _sealed_launcher_hash(self, path: Path) -> tuple[tuple, str]:
        """Read only an executable, rejecting replacement or mutation while hashing."""
        try:
            with path.open("rb") as stream:
                before = self._file_identity(os.fstat(stream.fileno()))
                digest = hashlib.sha256()
                while True:
                    if self._stop_requested.is_set():
                        raise _Unavailable()
                    block = stream.read(1024 * 1024)
                    if not block:
                        break
                    digest.update(block)
                after = self._file_identity(os.fstat(stream.fileno()))
                current = self._file_identity(path.stat())
            if before != after or before != current:
                raise _Unavailable()
            return before, digest.hexdigest()
        except (OSError, ValueError):
            raise _Unavailable() from None

    def _verified_launcher_image(self, record: _ProcessRecord, *, acquire: bool = False) -> bool:
        """Binary equality supplements proven ancestry; it never establishes ancestry."""
        path, key = Path(record.identity.exe_path), self._key(record)
        if _same_path(path, self._launcher_path):
            return True
        if ntpath.basename(str(path)).casefold() != self._launcher_path.name.casefold():
            return False
        seals = self._launcher_image_seals.get(key)
        try:
            if seals is not None:
                if (self._file_identity(self._launcher_path.stat()) != seals[0]
                        or self._file_identity(path.stat()) != seals[1]):
                    raise _Unavailable()
                return True
            if not acquire:
                return False
            installed_seal, installed_hash = self._sealed_launcher_hash(self._launcher_path)
            candidate_seal, candidate_hash = self._sealed_launcher_hash(path)
            if (candidate_hash != installed_hash
                    or (self._launcher_image_sha256 is not None and installed_hash != self._launcher_image_sha256)):
                return False
            self._launcher_image_seals[key] = (installed_seal, candidate_seal)
            self._launcher_image_sha256 = installed_hash
            with self._lock:
                self._status = replace(self._status, launcher_image_sha256=installed_hash)
            return True
        except (OSError, ValueError):
            raise _Unavailable() from None

    def _discover(self) -> bool:
        """Acquire only direct successor launchers; no recursive ownership inference."""
        native, root = self._native, self._root
        assert native is not None and root is not None
        stamp, rows = native.snapshot()
        names = {self._game_path.name.casefold(), self._launcher_path.name.casefold(),
                 "js.exe", "launcher.exe"}
        foreign: list[UnownedSession] = []
        seen_root = False
        for pid, parent_pid, name in rows:
            if name.casefold() not in names:
                continue
            handle = native.kernel.OpenProcess(0x101000, False, pid)
            if not handle:
                # Even a disappeared row invalidates this observation's quiet interval.
                raise _Unavailable("DISCOVERY_CHANGED" if ctypes.get_last_error() == 87 else "IDENTITY_UNAVAILABLE")
            keep = False
            try:
                candidate = native._record(pid, handle, parent_pid)
                identity, key = candidate.identity, self._key(candidate)
                if identity.creation_time > stamp or ntpath.basename(identity.exe_path).casefold() != name.casefold():
                    raise _Unavailable("DISCOVERY_CHANGED")
                if key == self._key(root):
                    if identity.kind != "game":
                        raise _Unavailable()
                    seen_root = True
                    continue
                if key in self._launchers:
                    if (parent_pid != root.identity.pid
                            or not _same_path(identity.exe_path, self._launchers[key].identity.exe_path)
                            or not self._verified_launcher_image(candidate)):
                        raise _Unavailable()
                    continue
                reason = "PARENT_UNVERIFIED"
                possible_launcher = name.casefold() == self._launcher_path.name.casefold()
                if identity.kind not in ("game", "launcher") and not possible_launcher:
                    reason = "UNEXPECTED_INSTALLATION"
                elif identity.kind == "game":
                    reason = "OTHER_GAME"
                elif self._close_boundary is None or identity.creation_time < self._close_boundary:
                    reason = "PREDATES_CLOSE_ATTEMPT"
                elif parent_pid == root.identity.pid:
                    # Re-read actual exit FILETIME from the original held handle.
                    # If still live *after* the snapshot, stamp is an upper bound
                    # already within its lifetime. If exited, its real exit is used.
                    parent_alive = self._root_alive()
                    born, died = native.times(root)
                    if (born <= identity.creation_time <= stamp
                            and ((parent_alive and not died) or (died and identity.creation_time <= died))):
                        if self._verified_launcher_image(candidate, acquire=True):
                            candidate.identity = replace(identity, kind="launcher")
                            native.records[key] = candidate
                            self._launchers[key] = candidate
                            keep = True
                            continue
                        reason = "LAUNCHER_IMAGE_MISMATCH"
                    else:
                        reason = "OUTSIDE_GAME_LIFETIME"
                foreign.append(UnownedSession(pid, parent_pid, name, identity.creation_time,
                                              identity.exe_path, reason))
            finally:
                if not keep:
                    native.kernel.CloseHandle(handle)
        if native.alive(root) and not seen_root:
            raise _Unavailable("DISCOVERY_CHANGED")
        with self._lock:
            self._status = replace(self._status, foreign_processes=tuple(foreign),
                                   owned_launchers=tuple(item.identity for item in self._launchers.values()))
        return not foreign

    def _post_close(self, record: _ProcessRecord, target: LauncherProcess) -> bool:
        """A narrow variant of native.close_window that records the actual post result.

        Reuse its handles, lifetime/path reader, window predicates and API
        signatures. The cancellation gate and one-attempt ledger share the same
        short lock as stop(), so no close can begin after stop acknowledges it.
        """
        native = self._native
        assert native is not None
        key = self._key(record)
        current = native._record(record.identity.pid, record.handle, record.parent_pid)
        image_verified = (current.identity.kind == "game" and _same_path(current.identity.exe_path, self._game_path)
                          if record is self._root else self._verified_launcher_image(current))
        if (self._key(current) != key or not image_verified
                or not _same_path(current.identity.exe_path, record.identity.exe_path)
                or native.records.get(key) is not record):
            raise _Unavailable()
        with self._lock:
            if (self._stop_requested.is_set() or self._pause_requested.is_set()
                    or key in self._attempted):
                return False
            self._attempted[key] = (target, time.monotonic())
            if record is self._root:
                stamp = wintypes.FILETIME()
                native.kernel.GetSystemTimeAsFileTime(ctypes.byref(stamp))
                self._close_boundary = native._filetime(stamp)
            self._status = replace(self._status,
                                   close_attempted_processes=tuple(item[0] for item in self._attempted.values()))
            try:
                if not native.alive(record):
                    return False
                pid = wintypes.DWORD()
                actual_tid = native.user.GetWindowThreadProcessId(target.hwnd, ctypes.byref(pid))
                if (not target.hwnd or not native.user.IsWindow(target.hwnd)
                        or pid.value != target.pid or actual_tid != target.tid
                        or native.times(record)[0] != target.creation_time):
                    raise _Unavailable()
                if not native.user.PostMessageW(target.hwnd, 0x0010, 0, 0):
                    raise _Unavailable()
            except Exception:
                self._uncertain.add(key)
                raise
            self._posted[key] = target
            self._status = replace(self._status, close_posted_processes=tuple(self._posted.values()))
            return True

    def _close_game(self) -> None:
        native, root, window = self._native, self._root, self._window
        assert native is not None and root is not None and window is not None
        key = self._key(root)
        if key in self._attempted:
            self._wait_for_attempt(root, "GAME_EXIT_NOT_CONFIRMED", NormalCloseState.CLOSING_GAME)
            return
        pid = wintypes.DWORD()
        tid = int(native.user.GetWindowThreadProcessId(window.hwnd, ctypes.byref(pid)))
        if (not native.user.IsWindow(window.hwnd) or pid.value != window.process_id or not tid
                or (self._expected_tid is not None and tid != self._expected_tid)):
            self._wait("ORIGINAL_WINDOW_CHANGED")
            return
        target = replace(root.identity, hwnd=window.hwnd, tid=tid)
        with self._lock:
            self._status = replace(self._status, game=target)
        self._post_close(root, target)
        self._wait_for_attempt(root, "GAME_EXIT_NOT_CONFIRMED", NormalCloseState.CLOSING_GAME)

    def _wait_for_attempt(self, record: _ProcessRecord, timeout_code: str, state: NormalCloseState) -> None:
        key = self._key(record)
        attempt = self._attempted.get(key)
        if key in self._uncertain:
            self._wait("CLOSE_UNCERTAIN")
        elif attempt is not None and time.monotonic() - attempt[1] >= self._close_timeout:
            self._wait(timeout_code)
        else:
            self._set(state, state.value, human_required=False)

    def _close_launcher(self, record: _ProcessRecord) -> None:
        native = self._native
        assert native is not None
        key = self._key(record)
        if key not in self._attempted:
            windows = native.windows(record)
            if len(windows) != 1:
                self._wait("LAUNCHER_WINDOW_UNAVAILABLE")
                return
            self._post_close(record, windows[0])
        self._wait_for_attempt(record, "LAUNCHER_EXIT_NOT_CONFIRMED", NormalCloseState.CLOSING_LAUNCHER)

    def _run(self) -> None:
        quiet_since: float | None = None
        try:
            if self._stop_requested.is_set():
                return
            if sys.platform != "win32":
                self._wait("UNSUPPORTED_PLATFORM")
                return
            self._native = _NativeWindows(self._launcher_path, self._game_path)
            if self._stop_requested.is_set():
                return
            self._acquire_root()
            while not self._stop_requested.is_set():
                try:
                    game_alive = self._root_alive()
                    isolated = self._discover()
                    if self._stop_requested.is_set():
                        break
                    if self._pause_requested.is_set():
                        quiet_since = None
                        self._set(NormalCloseState.PAUSED, "PAUSED", human_required=False)
                    elif not isolated:
                        quiet_since = None
                        self._wait("FOREIGN_SESSION")
                    elif game_alive:
                        quiet_since = None
                        self._close_game()
                    else:
                        live = [item for item in self._launchers.values() if self._native.alive(item)]
                        if live:
                            quiet_since = None
                            self._close_launcher(live[0])
                        else:
                            now = time.monotonic()
                            quiet_since = now if quiet_since is None else quiet_since
                            if now - quiet_since >= self._quiet_interval:
                                # All owned records have been observed signalled.
                                with self._lock:
                                    if self._stop_requested.is_set():
                                        break
                                    if not self._pause_requested.is_set():
                                        self._native.close_handles()
                                        self._set(NormalCloseState.CLOSED, "CLOSED", complete=True,
                                                  handles_retained=False, observing=False, human_required=False)
                                        return
                                    quiet_since = None
                            self._set(NormalCloseState.SETTLING, "SETTLING", human_required=False)
                except _Unavailable as error:
                    quiet_since = None
                    self._wait(error.code if error.code in _MESSAGES else "IDENTITY_UNAVAILABLE")
                self._stop_requested.wait(self._poll_interval)
        except _Unavailable as error:
            self._wait(error.code if error.code in _MESSAGES else "IDENTITY_UNAVAILABLE")
        except Exception:
            # No underlying exception or arbitrary process data is logged.
            self._wait("DRIVER_UNAVAILABLE")
        finally:
            with self._lock:
                self._actions_finished = True
            if self._status.state != NormalCloseState.CLOSED:
                self._observe_retained()

    def _observe_retained(self) -> None:
        """No more UI actions: retain acquired handles until their exits are known."""
        native = self._native
        while native is not None and native.records:
            stopped = self._stop_requested.is_set()
            if stopped:
                self._set(NormalCloseState.STOPPED, "STOPPED", human_required=False, observing=True)
            try:
                if self._root is not None:
                    self._root_alive()
                if not any(native.alive(item) for item in native.records.values()):
                    native.close_handles()
                    break
            except Exception:
                # Access failure is not exit evidence, including after stop.
                pass
            # Event.wait would spin once stop is set; use a separate unsignalled
            # event for this read-only retention period.
            threading.Event().wait(max(0.25, self._poll_interval))
        if native is not None and not native.records:
            native.close_handles()
        with self._lock:
            stopped = self._stop_requested.is_set()
            self._status = replace(self._status, handles_retained=False, observing=False)
            if stopped:
                self._set(NormalCloseState.STOPPED, "STOPPED", human_required=False)
