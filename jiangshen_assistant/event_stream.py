"""Process-local event mailboxes; wake-up hints never replace queued facts.

The default queue is unbounded and is not a durable journal. Producers own the
original value until put(), which detaches it with deepcopy. A frozen Event
protects its header, not the nested payload from mutation by its consumer.
The explicit detach=False option is for owned request objects containing locks
or completion Events; those objects are transferred, not copied domain facts.
"""
from __future__ import annotations

import copy
from dataclasses import dataclass
from datetime import datetime, timezone
from enum import Enum
import queue
from typing import Any, Callable
import uuid


def _field(value: Any, name: str) -> Any:
    return value.get(name) if isinstance(value, dict) else getattr(value, name, None)


def _scalar(value: Any) -> str | int | float | bool | None:
    if isinstance(value, Enum):
        value = value.value
    return value if type(value) in (str, int, float, bool) else None


@dataclass(frozen=True)
class Event:
    event_id: str
    source: str
    source_sequence: int
    kind: str
    occurred_at: str
    payload: Any
    run_id: str | int | float | bool | None = None
    context_key: str | int | float | bool | None = None
    generation: str | int | float | bool | None = None
    operation_id: str | int | float | bool | None = None
    schema_version: int = 1

    @property
    def header(self) -> dict:
        """Safe diagnostics: no payload, context/session tokens or credentials."""
        return {"schema_version": self.schema_version, "event_id": self.event_id,
                "source": self.source, "source_sequence": self.source_sequence,
                "kind": self.kind, "occurred_at": self.occurred_at}


class EventMailbox(queue.Queue):
    """Queue-compatible FIFO with publication envelopes and optional wake-ups.

    Queue's mutex serializes source sequence assignment with insertion, so
    concurrent producers cannot reorder the assigned sequence. Legacy get()
    returns the detached payload; the scheduler consumes get_event_nowait().
    Both use normal Queue task_done()/join() accounting. Re-enqueuing a legacy
    payload creates a fresh publication and a fresh sequence.
    """

    def __init__(self, source: str, maxsize: int = 0, *, detach: bool = True) -> None:
        if not isinstance(source, str) or not source:
            raise ValueError("mailbox source must be a nonempty string")
        super().__init__(maxsize=maxsize)
        self._source = source
        self._detach = detach
        self._notify: Callable[[], None] | None = None
        self._published = 0
        self._dequeued = 0
        self._notify_failures = 0
        self._last_notify_error: str | None = None

    @property
    def source(self) -> str:
        return self._source

    def _put(self, item: Any) -> None:
        # A failed copy rejects publication; never silently fall back to shared
        # mutable state. Dataclasses retain their original runtime type.
        payload = copy.deepcopy(item) if self._detach else item
        data = _field(payload, "data")
        workflow = _field(data, "workflow_event") or _field(payload, "workflow_event")
        candidates = (workflow, payload, data)
        metadata = {}
        for name in ("run_id", "context_key", "generation", "operation_id"):
            metadata[name] = next((value for candidate in candidates
                                   if (value := _scalar(_field(candidate, name))) is not None), None)
        kind = next((value for candidate in candidates for name in ("kind", "status")
                     if (value := _scalar(_field(candidate, name))) is not None), "event")
        sequence = self._published + 1
        event = Event(event_id=uuid.uuid4().hex, source=self._source,
                      source_sequence=sequence, kind=str(kind),
                      occurred_at=datetime.now(timezone.utc).isoformat(),
                      payload=payload, **metadata)
        super()._put(event)
        self._published = sequence

    def _get(self) -> Event:
        event = super()._get()
        self._dequeued += 1
        return event

    def put(self, item: Any, block: bool = True, timeout: float | None = None) -> None:
        super().put(item, block=block, timeout=timeout)
        with self.mutex:
            notify = self._notify
        self._signal(notify)

    def get(self, block: bool = True, timeout: float | None = None) -> Any:
        return super().get(block=block, timeout=timeout).payload

    def get_event_nowait(self) -> Event:
        return super().get(block=False)

    def set_notify(self, callback: Callable[[], None] | None) -> None:
        """Bind a wake-up hint, including facts published before registration.

        Notifications run outside Queue's lock on the publishing/binding thread.
        They must only mark scheduler readiness, never invoke business or Tk.
        Duplicate hints and a hint racing with unbinding are harmless.
        """
        if callback is not None and not callable(callback):
            raise TypeError("mailbox notify must be callable or None")
        with self.mutex:
            self._notify = callback
            pending = self._qsize() > 0
        if pending:
            self._signal(callback)

    def _signal(self, callback: Callable[[], None] | None) -> None:
        if callback is None:
            return
        try:
            callback()
        except Exception as exc:
            # Publication already succeeded. Raising here could cause a caller
            # to repeat an action; preserve the fact and expose the hint failure.
            with self.mutex:
                self._notify_failures += 1
                self._last_notify_error = type(exc).__name__

    @property
    def snapshot(self) -> dict:
        with self.mutex:
            return {"source": self._source, "queued": self._qsize(),
                    "published": self._published, "dequeued": self._dequeued,
                    "unfinished_tasks": self.unfinished_tasks, "maxsize": self.maxsize,
                    "detached": self._detach, "notify_failures": self._notify_failures,
                    "last_notify_error": self._last_notify_error}
