"""Cooperative task execution independent of any desktop or game adapter."""

from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timezone
from enum import Enum
import math
import threading
import time
from typing import Callable

from .event_stream import EventMailbox


class RunnerState(str, Enum):
    IDLE = "idle"
    RUNNING = "running"
    PAUSED = "paused"
    STOPPING = "stopping"


@dataclass(frozen=True)
class RunEvent:
    kind: str
    message: str = ""
    level: str = "INFO"
    timestamp: str = ""
    state: str = ""
    source: str = "runner"
    task_name: str = ""


@dataclass(frozen=True)
class TaskDefinition:
    id: str
    title: str
    category: str
    description: str
    available: bool = False


TASK_CATALOG = (
    TaskDefinition("daily_routine", "日常", "daily", "后续逐项实现日常任务流程。"),
    TaskDefinition("battle_progress", "征战自动推进", "battle", "连续三次确认失败停止；胜利清零，仅推进已审核教学。", True),
)


class Cancelled(Exception):
    """Raised at a cooperative checkpoint when a stop has been requested."""


class RunContext:
    """The only runner interface a task needs; it never calls a GUI."""

    def __init__(self, runner: TaskRunner, cancelled: threading.Event) -> None:
        self._runner = runner
        self._cancelled = cancelled

    def checkpoint(self) -> None:
        with self._runner._condition:
            while True:
                if self._cancelled.is_set():
                    raise Cancelled()
                if self._runner._state != RunnerState.PAUSED:
                    return
                self._runner._condition.wait()

    @property
    def cancelled(self) -> bool:
        """Read cancellation without blocking battle-result reconciliation."""
        return self._cancelled.is_set()

    @property
    def is_paused(self) -> bool:
        return self._runner.state == RunnerState.PAUSED

    def wait(self, seconds: float) -> None:
        """Wait for active execution time, freezing the timer while paused."""
        if isinstance(seconds, bool) or not isinstance(seconds, (int, float)):
            raise ValueError("等待时间必须是有限的非负数。")
        if not math.isfinite(seconds) or seconds < 0:
            raise ValueError("等待时间必须是有限的非负数。")
        remaining = float(seconds)
        with self._runner._condition:
            while True:
                if self._cancelled.is_set():
                    raise Cancelled()
                if self._runner._state == RunnerState.PAUSED:
                    self._runner._condition.wait()
                    continue
                if remaining <= 0:
                    return
                started = time.monotonic()
                self._runner._condition.wait(timeout=remaining)
                remaining -= time.monotonic() - started

    def log(self, message: str, level: str = "INFO") -> None:
        self._runner._emit("log", message, level)


class TaskRunner:
    """Run one cooperative task at a time and publish thread-safe events."""

    def __init__(self, events: EventMailbox | None = None, *, source: str = "runner") -> None:
        self.events = events if events is not None else EventMailbox('runner')
        self._source = source
        self._task_name = ""
        self._condition = threading.Condition()
        self._state = RunnerState.IDLE
        self._thread: threading.Thread | None = None
        self._cancelled = threading.Event()

    @property
    def state(self) -> RunnerState:
        with self._condition:
            return self._state

    @property
    def is_active(self) -> bool:
        return self.state != RunnerState.IDLE

    def _emit(self, kind: str, message: str = "", level: str = "INFO") -> None:
        with self._condition:
            self.events.put(RunEvent(
                kind=kind,
                message=message,
                level=level,
                timestamp=datetime.now(timezone.utc).isoformat(timespec="milliseconds"),
                state=self._state.value,
                source=self._source,
                task_name=self._task_name,
            ))

    def _set_state(self, state: RunnerState) -> None:
        self._state = state
        self._emit("state")
        self._condition.notify_all()

    def start(self, name: str, task: Callable[[RunContext], None], *, checkpoints: bool = True) -> bool:
        """Start one task; reconcilers may manage pause/cancel without checkpoints.

        A reconciler still occupies this runner, but must observe public context
        flags and perform its own bounded cleanup before returning.
        """
        if not isinstance(name, str) or not name.strip():
            raise ValueError("任务名称不能为空。")
        if not callable(task):
            raise TypeError("任务必须可以调用。")
        with self._condition:
            if self._state != RunnerState.IDLE:
                return False
            self._cancelled = threading.Event()
            self._task_name = name
            context = RunContext(self, self._cancelled)
            thread = threading.Thread(
                target=self._run, args=(task, context, checkpoints), name=f"task:{name}", daemon=True,
            )
            self._thread = thread
            self._set_state(RunnerState.RUNNING)
            try:
                thread.start()
            except Exception:
                self._thread = None
                self._set_state(RunnerState.IDLE)
                raise
            return True

    def pause(self) -> bool:
        with self._condition:
            if self._state != RunnerState.RUNNING:
                return False
            self._set_state(RunnerState.PAUSED)
            return True

    def resume(self) -> bool:
        with self._condition:
            if self._state != RunnerState.PAUSED:
                return False
            self._set_state(RunnerState.RUNNING)
            return True

    def stop(self) -> bool:
        with self._condition:
            if self._state not in (RunnerState.RUNNING, RunnerState.PAUSED):
                return False
            self._cancelled.set()
            self._set_state(RunnerState.STOPPING)
            return True

    def join(self, timeout: float | None = None) -> None:
        with self._condition:
            thread = self._thread
        if thread is not None and thread is not threading.current_thread():
            thread.join(timeout)

    def _run(self, task: Callable[[RunContext], None], context: RunContext, checkpoints: bool = True) -> None:
        outcome = "completed"
        try:
            if checkpoints:
                context.checkpoint()
            task(context)
            if checkpoints:
                context.checkpoint()
        except Cancelled:
            outcome = "stopped"
        except BaseException as exc:
            outcome = "failed"
            context.log(f"{type(exc).__name__}: {exc}", "ERROR")
        finally:
            with self._condition:
                if outcome == "completed" and context._cancelled.is_set():
                    outcome = "stopped"
                self._set_state(RunnerState.IDLE)
                self._emit("done", outcome, "ERROR" if outcome == "failed" else "INFO")


class RunnerGroup:
    """Independent cooperative workers with one application event mailbox.

    A paused battle keeps its thread and local state while another worker owns
    the game session. Global stop and join cover every registered worker.
    """

    def __init__(self) -> None:
        self.events = EventMailbox('runner')
        self._lock = threading.RLock()
        self._runners: dict[str, TaskRunner] = {}
        self._diagnostics = self.create('diagnostics')

    def create(self, name: str) -> TaskRunner:
        if not isinstance(name, str) or not name.strip():
            raise ValueError("runner name must be nonempty")
        with self._lock:
            if name not in self._runners:
                self._runners[name] = TaskRunner(self.events, source=name)
            return self._runners[name]

    def _members(self) -> tuple[TaskRunner, ...]:
        with self._lock:
            return tuple(self._runners.values())

    @property
    def state(self) -> RunnerState:
        states = {runner.state for runner in self._members()}
        return next((state for state in (RunnerState.STOPPING, RunnerState.RUNNING,
                                         RunnerState.PAUSED) if state in states), RunnerState.IDLE)

    @property
    def is_active(self) -> bool:
        return self.state != RunnerState.IDLE

    def start(self, name: str, task: Callable[[RunContext], None], *, checkpoints: bool = True) -> bool:
        return self._diagnostics.start(name, task, checkpoints=checkpoints)

    def pause(self) -> bool:
        return self._diagnostics.pause()

    def resume(self) -> bool:
        return self._diagnostics.resume()

    def stop(self) -> bool:
        stopped = False
        for runner in self._members():
            stopped = runner.stop() or stopped
        return stopped

    def join(self, timeout: float | None = None) -> None:
        deadline = None if timeout is None else time.monotonic() + timeout
        for runner in self._members():
            runner.join(None if deadline is None else max(0.0, deadline - time.monotonic()))
