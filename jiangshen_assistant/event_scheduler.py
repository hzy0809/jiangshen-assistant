"""Owner-thread serial dispatch of mailbox facts and named one-shot deadlines.

Producer notifications only populate a locked ready queue. Tk calls dispatch()
and next_delay() from its owner thread; this module never calls Tk or starts a
thread. Per-source FIFO and round-robin sources avoid bulk-drain starvation.
Timers share the same work/time budget, and newly scheduled timers wait for the
next dispatch. This is an in-memory scheduler, not durable action delivery.
"""
from __future__ import annotations

from collections import deque
from dataclasses import dataclass
import heapq
import math
import queue
import threading
import time
from typing import Callable

from .event_stream import Event, EventMailbox


@dataclass(frozen=True)
class _Timer:
    key: str
    deadline: float
    sequence: int
    callback: Callable[[], None]


class EventScheduler:
    def __init__(self, *, on_error: Callable[[str, Exception], None] | None = None,
                 clock: Callable[[], float] = time.monotonic) -> None:
        self._owner = threading.get_ident()
        self._clock = clock
        self._on_error = on_error
        self._lock = threading.RLock()
        self._sources: dict[str, tuple[EventMailbox, Callable[[Event], None]]] = {}
        self._ready: deque[str] = deque()
        self._ready_set: set[str] = set()
        self._handled: dict[str, int] = {}
        self._succeeded: dict[str, int] = {}
        self._timers: dict[str, _Timer] = {}
        self._timer_heap: list[tuple[float, int, str]] = []
        self._timer_sequence = 0
        self._timers_handled = 0
        self._failures = 0
        self._last_error: dict | None = None
        self._recent: deque[dict] = deque(maxlen=64)
        self._prefer_timer = True
        self._closed = False
        self._dispatching = False

    def _require_owner(self) -> None:
        if threading.get_ident() != self._owner:
            raise RuntimeError("event scheduler operation requires its owner thread")

    def register(self, mailbox: EventMailbox, handler: Callable[[Event], None]) -> None:
        self._require_owner()
        if not callable(handler):
            raise TypeError("event handler must be callable")
        with self._lock:
            if self._closed:
                raise RuntimeError("event scheduler is closed")
            if mailbox.source in self._sources:
                raise ValueError("event source is already registered")
            self._sources[mailbox.source] = (mailbox, handler)
            self._handled[mailbox.source] = self._succeeded[mailbox.source] = 0
        mailbox.set_notify(lambda: self._mark_ready(mailbox.source))

    def _mark_ready(self, source: str) -> None:
        # The only scheduler operation executed by publisher threads.
        with self._lock:
            if not self._closed and source in self._sources and source not in self._ready_set:
                self._ready_set.add(source)
                self._ready.append(source)

    def call_later(self, key: str, delay: float, callback: Callable[[], None]) -> None:
        self._require_owner()
        if not isinstance(key, str) or not key:
            raise ValueError("timer key must be a nonempty string")
        if not math.isfinite(delay):
            raise ValueError("timer delay must be finite")
        if not callable(callback):
            raise TypeError("timer callback must be callable")
        with self._lock:
            if self._closed:
                raise RuntimeError("event scheduler is closed")
            self._timer_sequence += 1
            timer = _Timer(key, self._clock() + max(0.0, delay), self._timer_sequence, callback)
            self._timers[key] = timer
            heapq.heappush(self._timer_heap, (timer.deadline, timer.sequence, key))
            self._compact_timers()

    def cancel(self, key: str) -> bool:
        self._require_owner()
        with self._lock:
            cancelled = self._timers.pop(key, None) is not None
            self._compact_timers()
            return cancelled

    def _compact_timers(self) -> None:
        # Replacing one long deadline repeatedly must not grow memory forever.
        if not self._dispatching and len(self._timer_heap) > max(64, len(self._timers) * 2):
            self._timer_heap = [(timer.deadline, timer.sequence, timer.key)
                                for timer in self._timers.values()]
            heapq.heapify(self._timer_heap)

    def _current_timer(self, entry: tuple[float, int, str]) -> _Timer | None:
        timer = self._timers.get(entry[2])
        return timer if timer is not None and timer.sequence == entry[1] else None

    def dispatch(self, max_events: int = 128, budget_seconds: float = .012) -> int:
        """Return attempted handler/timer calls, including failed calls.

        Failed actions are consumed exactly once and never retried here. A slow
        handler cannot be preempted; the time budget applies between calls.
        Legacy consumers may empty a ready mailbox: a stale hint is discarded.
        """
        self._require_owner()
        if not isinstance(max_events, int) or max_events < 0:
            raise ValueError("max_events must be a nonnegative integer")
        if not math.isfinite(budget_seconds) or budget_seconds < 0:
            raise ValueError("dispatch budget must be finite and nonnegative")
        if self._dispatching:
            raise RuntimeError("event dispatch cannot be reentered")
        if self._closed or not max_events or not budget_seconds:
            return 0
        stop_at = self._clock() + budget_seconds
        due: deque[tuple[float, int, str]] = deque()
        calls = 0
        self._dispatching = True
        try:
            # Freeze this turn's eligible timers. Callbacks can replace/cancel
            # these entries, but newly created zero-delay work waits a turn.
            with self._lock:
                now = self._clock()
                while (self._timer_heap and self._timer_heap[0][0] <= now
                       and len(due) < max_events and self._clock() < stop_at):
                    entry = heapq.heappop(self._timer_heap)
                    if self._current_timer(entry) is not None:
                        due.append(entry)
            while calls < max_events and self._clock() < stop_at and not self._closed:
                with self._lock:
                    has_source = bool(self._ready)
                if not due and not has_source:
                    break
                if due and (self._prefer_timer or not has_source):
                    entry = due.popleft()
                    with self._lock:
                        timer = self._current_timer(entry)
                        if timer is not None:
                            del self._timers[timer.key]
                            self._timers_handled += 1
                    if timer is None:
                        continue
                    calls += 1
                    self._prefer_timer = False
                    try:
                        timer.callback()
                    except Exception as exc:
                        self._report_error("timer:" + timer.key, exc)
                    continue
                with self._lock:
                    source = self._ready.popleft()
                    self._ready_set.remove(source)
                    mailbox, handler = self._sources[source]
                try:
                    event = mailbox.get_event_nowait()
                except queue.Empty:
                    continue
                if not mailbox.empty():
                    self._mark_ready(source)
                calls += 1
                self._prefer_timer = True
                with self._lock:
                    self._handled[source] += 1
                    self._recent.append(event.header)
                try:
                    handler(event)
                except Exception as exc:
                    self._report_error(source, exc)
                else:
                    with self._lock:
                        self._succeeded[source] += 1
                finally:
                    mailbox.task_done()
        finally:
            with self._lock:
                for entry in due:
                    if self._current_timer(entry) is not None:
                        heapq.heappush(self._timer_heap, entry)
                self._dispatching = False
                self._compact_timers()
        return calls

    def _report_error(self, source: str, error: Exception) -> None:
        with self._lock:
            self._failures += 1
            # Exception text can include bridge credentials or raw requests.
            self._last_error = {"source": source, "exception": type(error).__name__}
        if self._on_error is not None:
            try:
                self._on_error(source, error)
            except Exception as exc:
                with self._lock:
                    self._failures += 1
                    self._last_error = {"source": source, "phase": "on_error",
                                        "exception": type(exc).__name__}

    def next_delay(self, maximum: float = .08) -> float:
        """Owner pump delay in seconds; no producer thread calls Tk.after()."""
        self._require_owner()
        if not math.isfinite(maximum) or maximum < 0:
            raise ValueError("maximum delay must be finite and nonnegative")
        with self._lock:
            if self._closed:
                return maximum
            if self._ready:
                return 0.0
            while self._timer_heap and self._current_timer(self._timer_heap[0]) is None:
                heapq.heappop(self._timer_heap)
            if not self._timer_heap:
                return maximum
            return min(maximum, max(0.0, self._timer_heap[0][0] - self._clock()))

    def close(self) -> dict:
        """Call after producer cleanup. Pending facts/timers remain inspectable.

        Closing stops execution; it does not prove successful delivery or drain
        queues. Callers must inspect queued/pending counts before reporting done.
        """
        self._require_owner()
        with self._lock:
            self._closed = True
            mailboxes = [mailbox for mailbox, _ in self._sources.values()]
        for mailbox in mailboxes:
            mailbox.set_notify(None)
        return self.snapshot

    @property
    def snapshot(self) -> dict:
        """Thread-safe JSON-ready diagnostics, with bounded payload-free headers."""
        with self._lock:
            sources = {source: {**mailbox.snapshot, "handled": self._handled[source],
                                "succeeded": self._succeeded[source]}
                       for source, (mailbox, _) in self._sources.items()}
            return {"closed": self._closed,
                    "queued": sum(value["queued"] for value in sources.values()),
                    "published": sum(value["published"] for value in sources.values()),
                    "handled": sum(self._handled.values()), "sources": sources,
                    "ready_sources": len(self._ready_set),
                    "timers": {"pending": len(self._timers), "handled": self._timers_handled,
                               "keys": sorted(self._timers)},
                    "failures": self._failures,
                    "last_error": dict(self._last_error) if self._last_error else None,
                    "recent_events": [dict(header) for header in self._recent]}
