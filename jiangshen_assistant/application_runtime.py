"""Composition root for desktop events, handlers and supervised deadlines.

Only pump() runs from Tk.after. Worker notifications mark ready mailboxes; the
owner thread dispatches facts and named deadlines without scanning every task.
Game commands remain with the existing domain coordinators and their ledgers.
"""
from __future__ import annotations

from . import control_actions
from .application_handlers import ApplicationHandlers
from .auto_connection import AutoConnectionCoordinator
from .event_scheduler import EventScheduler
from .windows import WindowError


class ApplicationRuntime:
    def __init__(self, app) -> None:
        self.app = app
        self.handlers = ApplicationHandlers(app)
        self.scheduler = EventScheduler(on_error=self._failed)
        self._closed = False
        app.auto_connection = AutoConnectionCoordinator(app)
        for name in ('runner', 'objects', 'campaign', 'challenge', 'elite',
                     'development', 'daily', 'soul'):
            component = getattr(app, name)
            handler = getattr(self.handlers, name)
            self.scheduler.register(component.events,
                                    self._source_handler(name, handler))
        self.scheduler.register(app.control_server.requests, self._control_request)
        self.scheduler.register(app.account_plan.events, self._account_changed)
        self.scheduler.register(app.play_runtime.events, self._play_changed)
        self.scheduler.register(app.auto_connection.events, self._connection_discovered)
        self.scheduler.call_later('connection.step', 0, self._connection_step)
        self.scheduler.call_later('play.step', 0, app.play_runtime.tick)
        self.scheduler.call_later('input.stop', .05, self._stop_input)
        self.scheduler.call_later('view.player_age', 1.0, self._player_age)
        self.scheduler.call_later('view.controls', 0, self.handlers.refresh_controls)

    def _source_handler(self, name, handler):
        def consume(event):
            handler(event.payload)
            self.app.play_runtime.on_notification(name, event.payload)
            payload = event.payload
            if name != 'objects' or getattr(payload, 'kind', '') != 'log':
                self.request_account_step()
            if name == 'objects' and getattr(payload, 'kind', '') != 'log':
                self.request_connection_step()
            if not self.app.closing:
                self.scheduler.call_later('view.controls', 0,
                                          self.handlers.refresh_controls)
        return consume

    def _play_changed(self, event) -> None:
        self.handlers.refresh_controls()
        if event.payload.get('state') == 'CLOSED':
            instance = self.app.play_runtime.instances.get(event.payload.get('workflow_id'))
            if instance is not None:
                from .play_workflows import DEFINITIONS
                self.app.log(DEFINITIONS[instance.kind].label + '：' + instance.message,
                             file_message='玩法结束 ' + str(event.payload))
        self.request_account_step()
        self.request_connection_step()

    def _control_request(self, event) -> None:
        self.app.control_server.handle_request(
            event.payload,
            lambda method, params: control_actions.dispatch(self.app, method, params))
        if not self.app.closing and event.payload.method != 'status':
            self.request_account_step()
            self.scheduler.call_later('view.controls', 0,
                                      self.handlers.refresh_controls)

    def _account_changed(self, event) -> None:
        self.app.account_page.update_status(self.app.account_plan.snapshot)
        self.request_account_step()
        self.request_connection_step()

    def _connection_discovered(self, event) -> None:
        self.app.auto_connection.on_discovery(event)
        self.request_connection_step()

    def request_connection_step(self) -> None:
        if not self._closed and not self.app.closing:
            self.scheduler.call_later('connection.step', 0, self._connection_step)

    def _connection_step(self) -> None:
        if self._closed or self.app.closing:
            return
        self.app.auto_connection.tick()
        self.scheduler.call_later('connection.step', 1.0, self._connection_step)

    def request_account_step(self) -> None:
        plan = self.app.account_plan
        if not self.app.closing and (plan.is_active or plan.title_worker_pending):
            self.scheduler.call_later('account.step', 0, self._account_step)

    def _account_step(self) -> None:
        if self.app.closing:
            return
        plan = self.app.account_plan
        before = plan.scheduling_phase
        plan.tick()
        self.app.account_page.update_status(plan.snapshot)
        delay = plan.next_wakeup_delay()
        if delay is not None:
            # A phase transition is an internal continuation, not a request to
            # wait for an unrelated state notification.
            if before != plan.scheduling_phase:
                delay = 0.0
            self.scheduler.call_later('account.step', delay, self._account_step)

    def _stop_input(self) -> None:
        if self.app.closing:
            return
        if self.app.global_stop_available:
            try:
                if self.app.adapter.stop_pressed():
                    self.app.stop()
                    self.request_account_step()
            except WindowError as exc:
                self.app._stop_hotkey_failed(str(exc))
        self.scheduler.call_later('input.stop', .05, self._stop_input)

    def _player_age(self) -> None:
        if self.app.closing:
            return
        self.handlers.refresh_player_age()
        self.app._render_workflow_state()
        self.scheduler.call_later('view.player_age', 1.0, self._player_age)

    def _failed(self, source: str, error: Exception) -> None:
        # Rendering errors must not silently leave account scheduling running.
        # Do not retry a handler: its domain action may already have happened.
        self.app.log('事件处理失败，已停止后续操作。', 'ERROR',
                     file_message=f'事件处理失败 source={source} '
                                  f'{type(error).__name__}: {error}')
        self.app.stop()

    def pump(self) -> None:
        self.scheduler.dispatch(max_events=128, budget_seconds=.012)

    def next_delay(self) -> float:
        return self.scheduler.next_delay(maximum=.05)

    def begin_shutdown(self) -> None:
        self.app.auto_connection.close()
        for key in ('input.stop', 'view.player_age', 'view.controls', 'account.step', 'play.step', 'play.watch', 'connection.step'):
            self.scheduler.cancel(key)
        self.schedule_shutdown_check(0)

    def schedule_shutdown_check(self, delay: float = .08) -> None:
        self.scheduler.call_later('shutdown.check', delay, self.app._finish_close)

    @property
    def has_pending_events(self) -> bool:
        return any(source['queued'] for source in self.scheduler.snapshot['sources'].values())

    @property
    def snapshot(self) -> dict:
        return {**self.scheduler.snapshot,
                'account_phase': self.app.account_plan.scheduling_phase,
                'automatic_connection': self.app.auto_connection.snapshot,
                'transport': 'in_process', 'durable_replay': False}

    def close(self) -> dict:
        self._closed = True
        self.app.auto_connection.close()
        return self.scheduler.close()
