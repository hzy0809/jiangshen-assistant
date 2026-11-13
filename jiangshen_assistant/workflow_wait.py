"""Coalesced wakeups for one workflow consumer."""
from __future__ import annotations

import threading


class WakeSignal:
    """Retain a wakeup until a single waiter consumes it under the same lock.

    Several notifications may coalesce: callers must reread their workflow
    state after waking. This signal carries no event or settlement evidence.
    """

    def __init__(self) -> None:
        self._condition = threading.Condition()
        self._pending = False

    def set(self) -> None:
        with self._condition:
            self._pending = True
            self._condition.notify()

    def wait(self, timeout: float | None = None) -> bool:
        """Wait for a notification or timeout, atomically consuming a wakeup."""
        with self._condition:
            if not self._condition.wait_for(lambda: self._pending, timeout):
                return False
            self._pending = False
            return True

    def clear(self) -> None:
        """Discard pending state only when explicitly beginning a new round."""
        with self._condition:
            self._pending = False

    def is_set(self) -> bool:
        with self._condition:
            return self._pending
