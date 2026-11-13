"""One bounded title-screen action, followed by confirmed bridge teardown.

This driver never decides whether login succeeded. The account coordinator must
obtain a fresh player identity independently. DONE/PAUSED are published only after
both the worker and out-of-band cancellation thread have exited.
"""
from __future__ import annotations

from contextlib import AbstractContextManager
from pathlib import Path
import threading
import time
from typing import Callable
import uuid

from .bridge_client import BridgeClient, BridgeError, BridgeUnknown, _is_clean
from .mono_runtime import ProcessIdentity


class TitleStartDriver:
    def __init__(self, project_root: Path, identity_reader: Callable[[], ProcessIdentity], *,
                 io_guard: Callable[[ProcessIdentity], AbstractContextManager],
                 on_unknown: Callable[[ProcessIdentity, str], None] | None = None,
                 client_factory=BridgeClient, wait_seconds: float = 15.0) -> None:
        self.project_root = Path(project_root)
        self.identity_reader, self.io_guard = identity_reader, io_guard
        self.on_unknown = on_unknown or (lambda identity, reason: None)
        self.client_factory = client_factory
        self.wait_seconds = min(120.0, max(0.1, float(wait_seconds)))
        self._lock = threading.RLock()
        self._cancel = threading.Event()
        self._thread = self._cancel_thread = None
        self._client = None
        self._identity = None
        self._operation = ''
        self._intent = ''
        self._cancel_error = ''
        self._final = {'outcome': 'NOT_INVOKED', 'cleanup_confirmed': True,
                       'code': 'IDLE', 'reason': ''}

    def begin(self, identity: ProcessIdentity) -> str:
        """Start once for this process identity. No bridge wait occurs here."""
        with self._lock:
            if self._running():
                raise BridgeError('title_driver_busy')
            if (self._identity == identity and self._final['outcome'] in ('UNKNOWN', 'INVOKED')):
                raise BridgeError('title_driver_replay_forbidden')
            if (not isinstance(identity, ProcessIdentity)
                    or any(type(value) is not int or value <= 0 for value in
                           (identity.pid, identity.creation_time, identity.hwnd, identity.tid))):
                raise BridgeError('title_identity_invalid')
            self._identity, self._operation = identity, uuid.uuid4().hex
            self._launch()
            return self._operation

    def _launch(self) -> None:
        self._cancel = threading.Event()
        self._client = None
        self._intent = self._cancel_error = ''
        self._cancel_thread = None
        self._final = {'outcome': 'PENDING', 'cleanup_confirmed': False,
                       'code': 'RUNNING', 'reason': 'observing_title'}
        self._thread = threading.Thread(target=self._run, name='title-start', daemon=True)
        self._thread.start()

    def _running(self) -> bool:
        return bool((self._thread and self._thread.is_alive()) or
                    (self._cancel_thread and self._cancel_thread.is_alive()))

    def poll(self) -> dict:
        with self._lock:
            running = self._running()
            if not self._operation:
                phase = 'IDLE'
            elif running:
                phase = 'PAUSING' if self._intent == 'pause' else 'STOPPING' if self._intent == 'stop' else 'RUNNING'
            else:
                # PAUSED certifies no action in flight and successful teardown.
                phase = ('PAUSED' if self._intent == 'pause' and self._final['outcome'] == 'NOT_INVOKED'
                         and self._final['cleanup_confirmed'] and not self._cancel_error else 'DONE')
            value = dict(self._final)
            if self._cancel_error:
                value.update(outcome='UNKNOWN', code='UNKNOWN', reason=self._cancel_error)
            return {'phase': phase, 'operation_id': self._operation,
                    'identity': self._identity.as_dict() if self._identity else {}, **value}

    def pause(self) -> None:
        self._interrupt('pause')

    def stop(self) -> None:
        self._interrupt('stop')

    def _interrupt(self, intent: str) -> None:
        with self._lock:
            # A completed one-shot operation must never become resumable again.
            if self.poll()['phase'] in ('IDLE', 'DONE'):
                return
            if self._intent == 'stop' and intent == 'pause':
                return
            self._intent = intent
            self._cancel.set()  # Local admission closes before any bridge wait.
            client = self._client
            if client is not None and self._thread and self._thread.is_alive() and not self._cancel_thread:
                self._cancel_thread = threading.Thread(target=self._drain, args=(client,),
                    name='title-start-cancel', daemon=True)
                self._cancel_thread.start()

    def _drain(self, client) -> None:
        try:
            # The same monotonic event and native mutex cover both stock calls.
            client.cancel_battle_admission()
        except Exception:
            with self._lock:
                self._cancel_error = 'title_cancellation_unconfirmed'

    def resume(self) -> None:
        with self._lock:
            value = self.poll()
            if (value['phase'] != 'PAUSED' or value['outcome'] != 'NOT_INVOKED'
                    or not value['cleanup_confirmed']):
                raise BridgeError('title_resume_requires_clean_not_invoked_pause')
            # Keep the operation ID: a consumed native ticket cannot be replayed.
            self._launch()

    def _revalidate(self) -> None:
        if self.identity_reader() != self._identity:
            raise BridgeError('title_identity_changed')

    def _run(self) -> None:
        result = {'outcome': 'NOT_INVOKED', 'cleanup_confirmed': True,
                  'code': 'CANCELLED', 'reason': 'cancelled_before_bridge'}
        client = None
        try:
            # Shared with ObjectController passive Mono reads, normal bootstrap,
            # and binding changes. It remains held through teardown/quarantine.
            with self.io_guard(self._identity):
                try:
                    self._revalidate()
                    if not self._cancel.is_set():
                        client = self.client_factory(self.project_root, self._identity, self._revalidate,
                            title_operation=self._operation, title_epoch=uuid.uuid4().hex)
                        client.title_epoch = client.open_battle_admission()
                        with self._lock:
                            self._client = client
                        if not self._cancel.is_set():
                            state = client.connect(cancel=self._cancel)
                            if not client.terminal:
                                result = self._observe_and_start(client, state)
                            elif not self._cancel.is_set():
                                result = dict(outcome='NOT_INVOKED', cleanup_confirmed=False,
                                              code='ADMISSION_FAILED', reason=state.get('reason') or 'title_bridge_stopped_before_ready')
                except Exception as error:
                    result = {'outcome': 'UNKNOWN' if client and client._bootstrap_started else 'NOT_INVOKED',
                              'cleanup_confirmed': False,
                              'code': 'IDENTITY_CHANGED' if str(error) == 'title_identity_changed' else 'ADMISSION_FAILED',
                              'reason': str(error)}
                finally:
                    if client is not None:
                        result = self._cleanup(client, result)
                    # Quarantine is installed before releasing the shared I/O
                    # guard, so passive readers cannot race unknown teardown.
                    if not result['cleanup_confirmed']:
                        try:
                            self.on_unknown(self._identity, result['reason'])
                        except Exception:
                            result.update(outcome='UNKNOWN', code='UNKNOWN', reason='title_quarantine_callback_failed')
        except Exception as error:
            result = {'outcome': 'NOT_INVOKED', 'cleanup_confirmed': client is None,
                      'code': 'IDENTITY_CHANGED' if str(error) == 'title_identity_changed' else 'ADMISSION_FAILED',
                      'reason': str(error)} if client is None else {
                          'outcome': 'UNKNOWN', 'cleanup_confirmed': False, 'code': 'UNKNOWN',
                          'reason': 'title_guard_or_cleanup_unconfirmed'}
        with self._lock:
            self._final = result

    def _observe_and_start(self, client, state: dict) -> dict:
        deadline = time.monotonic() + self.wait_seconds
        observed_title = False
        while True:
            self._revalidate()
            if self._cancel.is_set():
                return dict(outcome='NOT_INVOKED', cleanup_confirmed=False,
                            code='CANCELLED', reason='title_admission_cancelled')
            title = client._validate_title_state(state)
            gate = title['gate']
            if gate == 'PASS':
                # The native callback rechecks the full gate under its mutex.
                reply = client.title_start()
                state = reply.get('state') or client.last_state
                title = client._validate_title_state(state)
                outcome = title['outcome']
                code = ('INVOKED' if outcome == 'INVOKED' else 'UNKNOWN' if outcome == 'UNKNOWN' else
                        'ALREADY_STARTED' if title['reason'] == 'already_started' else
                        'TITLE_NOT_PRESENT' if title['reason'] == 'title_not_present' else
                        'CANCELLED' if title['reason'] in ('title_admission_cancelled', 'stopping_before_invocation') else
                        'ADMISSION_FAILED')
                return dict(outcome=outcome, cleanup_confirmed=False,
                            code=code,
                            reason=title['reason'])
            if gate == 'already_started':
                return dict(outcome='NOT_INVOKED', cleanup_confirmed=False,
                            code='ALREADY_STARTED', reason=gate)
            if gate not in ('title_not_present', 'stock_timer_gate'):
                raise BridgeError('title_gate_' + gate)
            observed_title |= gate == 'stock_timer_gate'
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                return dict(outcome='NOT_INVOKED', cleanup_confirmed=False,
                            code='WAIT_TIMEOUT' if observed_title else 'TITLE_NOT_PRESENT', reason=gate)
            if self._cancel.wait(min(0.2, remaining)):
                continue
            reply = client.title_observe()
            state = reply.get('state') or client.last_state

    def _cleanup(self, client, result: dict) -> dict:
        result = dict(result)
        with self._lock:
            self._client = None  # New cancellation callers use local stop only.
            cancellation = self._cancel_thread
        if cancellation is not None:
            cancellation.join()
        try:
            if client._bootstrap_started:
                stopped = client.stop()
                state = stopped.get('state') or client.last_state
                clean_receipt = _is_clean(state)
                result['cleanup_confirmed'] = clean_receipt or client.target_exited()
                if result['cleanup_confirmed'] and state.get('title_start') is not None:
                    title = client._validate_title_state(state)
                    # A fresh final native receipt can resolve an interrupted
                    # ACK wait; old session/operation identities are rejected.
                    if title['outcome'] in ('INVOKED', 'UNKNOWN'):
                        result.update(outcome=title['outcome'], reason=title['reason'],
                                      code='INVOKED' if title['outcome'] == 'INVOKED' else 'UNKNOWN')
                    elif clean_receipt and not title['consumed'] and result['outcome'] == 'UNKNOWN':
                        # A final one-shot model plus destruction proves no
                        # future invocation even if the action ACK was lost.
                        result.update(outcome='NOT_INVOKED', reason=title['reason'],
                                      code='CANCELLED' if self._cancel.is_set() else result['code'])
            else:
                result['cleanup_confirmed'] = True
                if client._owns_lease:
                    client._release_lease()
            if not result['cleanup_confirmed']:
                result.update(outcome='UNKNOWN', code='UNKNOWN', reason='title_cleanup_unconfirmed')
            client._close_battle_guard(target_gone=client._target_status() == 'gone')
            if client._title_admitted:
                client.record_title_outcome(result['outcome'], cleanup_confirmed=result['cleanup_confirmed'])
            if result['cleanup_confirmed']:
                # A final component-destroyed receipt also resolves a drain
                # failure: no callback from this session can execute again.
                with self._lock:
                    self._cancel_error = ''
        except Exception:
            result.update(outcome='UNKNOWN', cleanup_confirmed=False, code='UNKNOWN', reason='title_cleanup_or_persistence_unconfirmed')
        return result
