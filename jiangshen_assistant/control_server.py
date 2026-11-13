"""Authenticated local control transport; application actions run in ``drain``."""
from __future__ import annotations

from contextlib import contextmanager
from dataclasses import dataclass, field
import json
import os
from pathlib import Path
import queue
import secrets
import socketserver
import threading
from typing import Any, Callable, Iterator

from .event_stream import EventMailbox


MAX_REQUEST_BYTES = 64 * 1024
MAX_RESPONSE_BYTES = 8 * 1024 * 1024
REQUEST_TIMEOUT = 30.0


@dataclass
class _Request:
    method: str
    params: dict[str, Any]
    done: threading.Event = field(default_factory=threading.Event)
    lock: Any = field(default_factory=threading.Lock)
    response: dict[str, Any] | None = None
    started: bool = False
    cancelled: bool = False

    def begin(self) -> bool:
        with self.lock:
            if self.cancelled:
                return False
            self.started = True
            return True

    def finish(self, response: dict[str, Any]) -> None:
        with self.lock:
            self.response = response
            self.done.set()

    def expire(self) -> dict[str, Any]:
        with self.lock:
            if self.response is not None:
                return self.response
            self.cancelled = True
            message = ("命令结果等待超时，请先查询助手状态。" if self.started else
                       "助手主线程未及时处理命令；本次命令已取消。")
            return {"ok": False, "error": message}


class _TCPServer(socketserver.ThreadingTCPServer):
    daemon_threads = True
    block_on_close = False


class _Handler(socketserver.StreamRequestHandler):
    def handle(self) -> None:
        owner: ControlServer = self.server.owner  # type: ignore[attr-defined]
        self.request.settimeout(5.0)
        try:
            line = self.rfile.readline(MAX_REQUEST_BYTES + 1)
            if not line.endswith(b"\n") or len(line) > MAX_REQUEST_BYTES:
                raise ValueError("请求必须是一行 UTF-8 JSON，且不超过 64 KiB。")
            request = json.loads(line.decode("utf-8"))
            if not isinstance(request, dict):
                raise ValueError("请求必须是 JSON 对象。")
            token = request.get("token")
            if not isinstance(token, str) or not secrets.compare_digest(token, owner.token):
                raise ValueError("控制令牌无效，请重新读取助手端点。")
            method = request.get("method")
            params = request.get("params", {})
            if not isinstance(method, str) or not method:
                raise ValueError("method 必须是非空字符串。")
            if not isinstance(params, dict):
                raise ValueError("params 必须是 JSON 对象。")
            response = owner.submit(method, params)
        except Exception as exc:
            response = {"ok": False, "error": str(exc) or type(exc).__name__}
        try:
            try:
                encoded = json.dumps(response, ensure_ascii=False, allow_nan=False).encode("utf-8")
                if len(encoded) + 1 > MAX_RESPONSE_BYTES:
                    raise ValueError("响应过大")
            except (TypeError, ValueError, RecursionError):
                encoded = json.dumps({"ok": False, "error": "命令结果超过 8 MiB 或无法编码为 JSON。"}, ensure_ascii=False).encode("utf-8")
            self.wfile.write(encoded + b"\n")
            self.wfile.flush()
        except OSError:
            pass  # The client may close while its action is completing.


class ControlServer:
    def __init__(self, data_dir: Path) -> None:
        self.endpoint_path = Path(data_dir) / "control.json"
        self.status_path = Path(data_dir) / "control-status.json"
        self._lock_path = Path(data_dir) / "control.lock"
        self.token = secrets.token_urlsafe(32)
        self._instance = secrets.token_hex(16)
        self._requests = EventMailbox('control', maxsize=128, detach=False)
        self._closed = threading.Event()
        self._state_lock = threading.Lock()
        self._server: _TCPServer | None = None
        self._thread: threading.Thread | None = None
        self._running = False
        self._error = ""
        self._port: int | None = None

    @property
    def requests(self) -> EventMailbox:
        """Mailbox consumed by the application main thread."""
        return self._requests

    @property
    def status(self) -> dict[str, Any]:
        with self._state_lock:
            return {"running": self._running, "pid": os.getpid(), "host": "127.0.0.1",
                    "port": self._port, "error": self._error,
                    "endpoint": str(self.endpoint_path)}

    def start(self) -> bool:
        if self._running:
            return True
        if self._closed.is_set():
            return False
        try:
            self.endpoint_path.parent.mkdir(parents=True, exist_ok=True)
            server = _TCPServer(("127.0.0.1", 0), _Handler)
            self._server = server
            server.owner = self  # type: ignore[attr-defined]
            self._port = int(server.server_address[1])
            with self._state_lock:
                self._running = True
                self._error = ""
            with self._endpoint_lock():
                self._atomic_json(self.endpoint_path, {"host": "127.0.0.1", "port": self._port,
                                                      "token": self.token, "pid": os.getpid()})
                self._write_status_locked()
            self._thread = threading.Thread(target=self._serve, name="assistant-control", daemon=True)
            self._thread.start()
            return True
        except Exception as exc:
            self._set_error(exc)
            if self._server is not None:
                try:
                    self._server.server_close()
                except OSError:
                    pass
            self._retire_endpoint()
            self._persist_status()
            return False

    def _serve(self) -> None:
        try:
            assert self._server is not None
            self._server.serve_forever(poll_interval=0.1)
        except Exception as exc:
            self._set_error(exc)
            self._closed.set()
            self._fail_pending()
            self._retire_endpoint()
            self._persist_status()
        finally:
            with self._state_lock:
                self._running = False

    def submit(self, method: str, params: dict[str, Any]) -> dict[str, Any]:
        if self._closed.is_set():
            return {"ok": False, "error": "助手正在关闭。"}
        request = _Request(method, params)
        try:
            self._requests.put_nowait(request)
        except queue.Full:
            return {"ok": False, "error": "控制请求过多，请稍后重试。"}
        if self._closed.is_set():
            request.finish({"ok": False, "error": "助手正在关闭。"})
        if not request.done.wait(REQUEST_TIMEOUT):
            return request.expire()
        return request.response or {"ok": False, "error": "命令未返回结果。"}

    def drain(self, dispatch: Callable[[str, dict[str, Any]], Any], limit: int = 16) -> None:
        """Call only from the application main thread; network workers never dispatch."""
        for _ in range(limit):
            try:
                request = self._requests.get_nowait()
            except queue.Empty:
                return
            try:
                self.handle_request(request, dispatch)
            finally:
                self._requests.task_done()

    def handle_request(self, request: _Request, dispatch: Callable[[str, dict[str, Any]], Any]) -> None:
        """Handle one dequeued request on the application main thread."""
        if self._closed.is_set():
            request.finish({"ok": False, "error": "助手正在关闭。"})
            return
        if not request.begin():
            return
        try:
            result = dispatch(request.method, request.params)
            response = {"ok": True, "result": result}
        except Exception as exc:
            response = {"ok": False, "error": str(exc) or type(exc).__name__}
        request.finish(response)

    def close(self) -> None:
        self._closed.set()
        self._fail_pending()
        server, thread = self._server, self._thread
        if server is not None:
            try:
                if thread is not None and thread.is_alive() and thread is not threading.current_thread():
                    server.shutdown()
                server.server_close()
            except OSError as exc:
                self._set_error(exc)
        with self._state_lock:
            self._running = False
        self._retire_endpoint()
        self._persist_status()

    def _fail_pending(self) -> None:
        while True:
            try:
                request = self._requests.get_nowait()
            except queue.Empty:
                return
            try:
                request.finish({"ok": False, "error": "助手正在关闭。"})
            finally:
                self._requests.task_done()

    def _set_error(self, exc: Exception) -> None:
        with self._state_lock:
            self._running = False
            self._error = str(exc) or type(exc).__name__

    @contextmanager
    def _endpoint_lock(self) -> Iterator[None]:
        with self._lock_path.open("a+b") as handle:
            handle.seek(0, os.SEEK_END)
            if handle.tell() == 0:
                handle.write(b"\0")
                handle.flush()
            handle.seek(0)
            if os.name == "nt":
                import msvcrt
                msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                import fcntl
                fcntl.flock(handle.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
            try:
                yield
            finally:
                handle.seek(0)
                if os.name == "nt":
                    msvcrt.locking(handle.fileno(), msvcrt.LK_UNLCK, 1)
                else:
                    fcntl.flock(handle.fileno(), fcntl.LOCK_UN)

    def _atomic_json(self, path: Path, value: dict[str, Any]) -> None:
        temporary = path.with_name(f".{path.name}.{self._instance}.tmp")
        try:
            with temporary.open("w", encoding="utf-8", newline="\n") as handle:
                json.dump(value, handle, ensure_ascii=False, allow_nan=False)
                handle.write("\n")
                handle.flush()
                os.fsync(handle.fileno())
            os.replace(temporary, path)
        finally:
            temporary.unlink(missing_ok=True)

    @staticmethod
    def _read_json(path: Path) -> dict[str, Any]:
        try:
            value = json.loads(path.read_text(encoding="utf-8"))
            return value if isinstance(value, dict) else {}
        except (OSError, ValueError):
            return {}

    def _write_status_locked(self) -> None:
        self._atomic_json(self.status_path, {**self.status, "instance_id": self._instance})

    def _persist_status(self) -> None:
        try:
            with self._endpoint_lock():
                owner = self._read_json(self.status_path).get("instance_id")
                if owner in (None, self._instance):
                    self._write_status_locked()
        except OSError as exc:
            self._set_error(exc)

    def _retire_endpoint(self) -> None:
        try:
            with self._endpoint_lock():
                endpoint = self._read_json(self.endpoint_path)
                if endpoint.get("token") == self.token and endpoint.get("pid") == os.getpid():
                    self.endpoint_path.unlink(missing_ok=True)
        except OSError as exc:
            self._set_error(exc)
