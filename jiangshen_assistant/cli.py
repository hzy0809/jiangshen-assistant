"""Local command-line control of the assistant's GUI process."""
from __future__ import annotations

import argparse
import ctypes
import json
import math
import os
from pathlib import Path
import re
import socket
import subprocess
import sys
import time


ROOT = Path(__file__).resolve().parent.parent
_LIMIT = 65536
_RESPONSE_LIMIT = 8 * 1024 * 1024
_TASKS = ('account_plan', 'campaign', 'challenge', 'daily', 'soul', 'growth', 'equipment', 'check',
          'tribute', 'star', 'claim_growth', 'growth-claim', 'point-reward')


class CliError(RuntimeError):
    pass


class ControlUnavailable(CliError):
    pass


class AssistantNotRunning(CliError):
    pass


def _endpoint(data_dir):
    path = data_dir / 'control.json'
    try:
        raw = path.read_bytes()
    except FileNotFoundError:
        return None
    except OSError as exc:
        raise CliError(f'无法读取控制端点 {path}（{exc}）') from exc
    try:
        value = json.loads(raw.decode('utf-8-sig')) if len(raw) <= _LIMIT else None
    except (ValueError, UnicodeError) as exc:
        raise CliError(f'控制端点内容无效 {path}') from exc
    if (not isinstance(value, dict) or value.get('host') != '127.0.0.1'
            or type(value.get('port')) is not int or not 1 <= value['port'] <= 65535
            or type(value.get('pid')) is not int or not 1 <= value['pid'] <= 0xFFFFFFFF
            or not isinstance(value.get('token'), str) or not 1 <= len(value['token']) <= 256):
        raise CliError(f'控制端点字段无效 {path}')
    return value


def _server_error(data_dir):
    try:
        value = json.loads((data_dir / 'control-status.json').read_text(encoding='utf-8-sig'))
        error = value.get('error')
        return f'（{error}）' if isinstance(error, str) and error else ''
    except (OSError, ValueError, AttributeError):
        return ''


def _request(endpoint, method, params, timeout):
    wire = json.dumps(dict(token=endpoint['token'], method=method, params=params),
                      ensure_ascii=False, allow_nan=False).encode('utf-8') + b'\n'
    if len(wire) > _LIMIT:
        raise CliError('命令超出控制协议长度限制。')
    try:
        deadline = time.monotonic() + timeout
        with socket.create_connection(('127.0.0.1', endpoint['port']), timeout=timeout) as stream:
            stream.sendall(wire)
            data = bytearray()
            while b'\n' not in data:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise TimeoutError('等待助手响应超时')
                stream.settimeout(remaining)
                chunk = stream.recv(min(65536, _RESPONSE_LIMIT + 1 - len(data)))
                if not chunk:
                    raise ControlUnavailable('助手控制连接已关闭，未收到完整响应。')
                data.extend(chunk)
                if len(data) > _RESPONSE_LIMIT:
                    raise CliError('助手响应超出控制协议长度限制。')
    except OSError as exc:
        raise ControlUnavailable(f'无法连接助手控制通道（{exc}）') from exc
    try:
        response = json.loads(bytes(data).split(b'\n', 1)[0].decode('utf-8'))
    except (ValueError, UnicodeError) as exc:
        raise CliError('助手返回了无效的 JSON 响应。') from exc
    if not isinstance(response, dict) or type(response.get('ok')) is not bool:
        raise CliError('助手响应缺少结果标记。')
    if response['ok'] is False:
        raise CliError(str(response.get('error') or '助手拒绝了命令。'))
    if 'result' not in response:
        raise CliError('助手响应缺少结果。')
    return response['result']


def _compact_status(endpoint, timeout):
    try:
        return _request(endpoint, 'status', {'compact': True}, timeout)
    except CliError as exc:
        if str(exc) == '不支持的参数：compact':
            raise CliError('运行中的助手版本不支持紧凑状态；请先更新助手后再使用 --compact。') from exc
        raise


class _Process:
    def __init__(self, pid):
        if os.name != 'nt':
            raise CliError('启动和退出助手需要 Windows。')
        from ctypes import wintypes
        self.kernel = ctypes.WinDLL('kernel32', use_last_error=True)
        self.kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        self.kernel.OpenProcess.restype = wintypes.HANDLE
        self.kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
        self.kernel.WaitForSingleObject.restype = wintypes.DWORD
        self.kernel.CloseHandle.argtypes = [wintypes.HANDLE]
        self.kernel.CloseHandle.restype = wintypes.BOOL
        self.handle = self.kernel.OpenProcess(0x00100000, False, pid)
        if not self.handle and ctypes.get_last_error() != 87:
            raise CliError(f'无法读取助手进程 {pid} 的退出状态。')

    def running(self):
        if not self.handle:
            return False
        state = self.kernel.WaitForSingleObject(self.handle, 0)
        if state not in (0, 258):
            raise CliError('无法确认助手进程是否已经退出。')
        return state == 258

    def wait(self, timeout):
        if self.handle and self.kernel.WaitForSingleObject(self.handle, min(int(timeout * 1000), 0xFFFFFFFE)) != 0:
            raise CliError('等待助手退出超时，请等待窗口完成关闭后再操作。')

    def close(self):
        if self.handle:
            self.kernel.CloseHandle(self.handle)
            self.handle = None


def _legacy_window():
    if os.name != 'nt':
        return None
    from ctypes import wintypes
    user = ctypes.WinDLL('user32', use_last_error=True)
    callback_type = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    user.EnumWindows.argtypes = [callback_type, wintypes.LPARAM]
    user.EnumWindows.restype = wintypes.BOOL
    user.IsWindowVisible.argtypes = [wintypes.HWND]
    user.IsWindowVisible.restype = wintypes.BOOL
    user.GetWindowTextLengthW.argtypes = [wintypes.HWND]
    user.GetWindowTextLengthW.restype = ctypes.c_int
    user.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
    user.GetWindowTextW.restype = ctypes.c_int
    user.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
    user.GetWindowThreadProcessId.restype = wintypes.DWORD
    matches = []

    @callback_type
    def visit(hwnd, _):
        if user.IsWindowVisible(hwnd):
            length = user.GetWindowTextLengthW(hwnd)
            title = ctypes.create_unicode_buffer(length + 1)
            user.GetWindowTextW(hwnd, title, len(title))
            if title.value.startswith('将神助手'):
                pid = wintypes.DWORD()
                user.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
                if pid.value:
                    matches.append((hwnd, pid.value))
        return True

    if not user.EnumWindows(visit, 0):
        raise CliError('无法枚举旧版助手窗口。')
    if len(matches) > 1:
        raise CliError('发现多个“将神助手”窗口，无法确定唯一退出目标。')
    return matches[0] if matches else None


def _close_legacy(window, timeout):
    from ctypes import wintypes
    hwnd, pid = window
    process = _Process(pid)
    try:
        user = ctypes.WinDLL('user32', use_last_error=True)
        user.PostMessageW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
        user.PostMessageW.restype = wintypes.BOOL
        if not process.running():
            return
        if _legacy_window() != window:
            raise CliError('助手窗口已变化，请重新发出退出命令。')
        if not user.PostMessageW(hwnd, 0x0010, 0, 0):
            raise CliError('无法向助手窗口发送正常关闭消息。')
        process.wait(timeout)
    finally:
        process.close()


def _shutdown(data_dir, timeout):
    endpoint = _endpoint(data_dir)
    if endpoint is not None:
        process = _Process(endpoint['pid'])
        try:
            if process.running():
                try:
                    _request(endpoint, 'shutdown', {}, timeout)
                except ControlUnavailable:
                    window = _legacy_window()
                    if window is None or window[1] != endpoint['pid']:
                        raise
                    _close_legacy(window, timeout)
                process.wait(timeout)
                return {'exited': True, 'pid': endpoint['pid']}
        finally:
            process.close()
    window = _legacy_window()
    if window is None:
        raise AssistantNotRunning('助手未运行，未找到控制端点或唯一助手窗口。')
    _close_legacy(window, timeout)
    return {'exited': True, 'pid': window[1]}


def _launch(data_dir, timeout):
    if os.name != 'nt':
        raise CliError('启动助手需要 Windows 和带 Tk 支持的 Python。')
    endpoint = _endpoint(data_dir)
    if endpoint is not None:
        process = _Process(endpoint['pid'])
        try:
            if process.running():
                try:
                    status = _compact_status(endpoint, timeout)
                except ControlUnavailable as exc:
                    raise CliError('助手进程仍在运行，控制通道未响应。可使用 restart 正常重启。' + _server_error(data_dir)) from exc
                return {'launched': False, 'pid': endpoint['pid'], 'status': status}
        finally:
            process.close()
    if _legacy_window() is not None:
        raise CliError('旧版助手已经运行，请使用 restart 关闭旧窗口并启动控制通道。')
    pythonw = Path(sys.executable).resolve().with_name('pythonw.exe')
    entry = ROOT / 'main.pyw'
    if not pythonw.is_file() or not entry.is_file():
        raise CliError(f'找不到助手启动文件或 Python 图形运行时 {entry} / {pythonw}')
    process = subprocess.Popen([str(pythonw), str(entry), '--data-dir', str(data_dir)], cwd=str(ROOT),
                               stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        endpoint = _endpoint(data_dir)
        if endpoint is not None and endpoint['pid'] == process.pid:
            try:
                status = _compact_status(endpoint, min(2.0, max(.1, deadline - time.monotonic())))
                return {'launched': True, 'pid': process.pid, 'status': status}
            except ControlUnavailable:
                pass
        code = process.poll()
        if code is not None:
            raise CliError(f'助手启动后已退出，退出码 {code}。请查看 {data_dir / "startup-error.log"}' + _server_error(data_dir))
        time.sleep(.1)
    raise CliError('等待助手控制通道启动超时。' + _server_error(data_dir))


def _option(text):
    if '=' not in text:
        raise argparse.ArgumentTypeError('参数格式应为 KEY=VALUE')
    key, value = text.split('=', 1)
    if not re.fullmatch('[A-Za-z_][A-Za-z_0-9]*', key):
        raise argparse.ArgumentTypeError('参数名无效')
    if value.lower() in ('true', 'false'):
        value = value.lower() == 'true'
    elif re.fullmatch('[+-]?[0-9]+', value):
        value = int(value)
    return key, value


def _positive_timeout(text):
    try:
        number = float(text)
    except ValueError as exc:
        raise argparse.ArgumentTypeError('等待时间应为正数') from exc
    if not math.isfinite(number) or number <= 0:
        raise argparse.ArgumentTypeError('等待时间应为正数')
    return number


def _hwnd(text):
    try:
        value = int(text, 16 if text.lower().startswith('0x') else 10)
    except ValueError as exc:
        raise argparse.ArgumentTypeError('窗口句柄应为十进制数或 0x 十六进制数') from exc
    if value <= 0:
        raise argparse.ArgumentTypeError('窗口句柄应为正数')
    return value


def _parser():
    parser = argparse.ArgumentParser(description='通过本地控制通道操作将神助手')
    parser.add_argument('--data-dir', type=Path, default=ROOT / 'data', help='助手配置和控制端点目录')
    parser.add_argument('--timeout', type=_positive_timeout, default=40.0, help='等待秒数，默认 40')
    commands = parser.add_subparsers(dest='command')
    for name in ('launch', 'status', 'restart', 'exit', 'run', 'soul-quote', 'soul-harvest', 'reconnect', 'recover-soul', 'pause', 'resume', 'stop', 'bind',
                 'workflows', 'workflow', 'workflow-policy'):
        command = commands.add_parser(name)
        command.add_argument('--data-dir', type=Path, default=argparse.SUPPRESS)
        command.add_argument('--timeout', type=_positive_timeout, default=argparse.SUPPRESS)
        if name in ('status', 'run', 'soul-quote', 'soul-harvest', 'reconnect', 'recover-soul', 'pause', 'resume', 'stop', 'bind',
                    'workflows', 'workflow'):
            command.add_argument('--compact', action='store_true', help='省略历史明细，保留计数和账本路径')
        if name == 'run':
            command.add_argument('task', choices=_TASKS)
            command.add_argument('--donation', choices=('1000', '100000', '80gold'))
            command.add_argument('--option', type=_option, action='append', default=[], metavar='KEY=VALUE')
            command.add_argument('--placement', choices=('switch', 'queue'),
                                 help='switch 在当前操作收尾后切换；queue 加入队列')
            command.add_argument('--policy-file', type=Path, help='本次执行使用的资源策略 JSON')
        elif name == 'workflow':
            command.add_argument('action', choices=('pause', 'resume', 'stop'))
            command.add_argument('workflow_id')
        elif name == 'workflow-policy':
            command.add_argument('kind', help='玩法标识，例如 soul、tribute、daily.arena')
            command.add_argument('--account-id', default='')
            command.add_argument('--file', type=Path, help='保存策略 JSON；不指定则读取')
        elif name == 'bind':
            group = command.add_mutually_exclusive_group()
            group.add_argument('--hwnd', type=_hwnd)
            group.add_argument('--index', type=int, help='status.windows 中从 0 开始的序号')
    return parser


def main(argv=None):
    for output in (sys.stdout, sys.stderr):
        if hasattr(output, 'reconfigure'):
            output.reconfigure(encoding='utf-8')
    args = _parser().parse_args(argv)
    data_dir = args.data_dir.expanduser().resolve()
    command = args.command or 'launch'
    try:
        if command == 'launch':
            value = _launch(data_dir, args.timeout)
        elif command == 'restart':
            try:
                _shutdown(data_dir, args.timeout)
            except AssistantNotRunning:
                pass
            value = _launch(data_dir, args.timeout)
        elif command == 'exit':
            value = _shutdown(data_dir, args.timeout)
        else:
            params = {}
            if command == 'run':
                options = dict(args.option)
                if len(options) != len(args.option):
                    raise CliError('--option 参数名重复。')
                params = dict(task='claim_growth' if args.task == 'growth-claim' else args.task)
                if args.task == 'daily' and args.donation is not None:
                    params['donation'] = args.donation
                elif args.donation is not None:
                    raise CliError('--donation 仅适用于 daily。')
                if options:
                    params['options'] = options
                if args.placement is not None:
                    params['placement'] = args.placement
                if args.policy_file is not None:
                    params['resource_policy'] = json.loads(args.policy_file.read_text(encoding='utf-8-sig'))
            elif command == 'workflow':
                command = 'workflow-control'
                params = dict(workflow_id=args.workflow_id, action=args.action)
            elif command == 'workflow-policy':
                params = dict(kind=args.kind, account_id=args.account_id)
                if args.file is not None:
                    params['policy'] = json.loads(args.file.read_text(encoding='utf-8-sig'))
            elif command == 'bind':
                if args.hwnd is not None:
                    params['hwnd'] = args.hwnd
                elif args.index is not None:
                    if args.index < 0:
                        raise CliError('窗口序号应大于或等于 0。')
                    params['index'] = args.index
            if getattr(args, 'compact', False):
                params['compact'] = True
            endpoint = _endpoint(data_dir)
            if endpoint is None:
                raise CliError(f'助手控制通道未启动，请先运行 launch 或 restart。目录为 {data_dir}')
            if command == 'status' and args.compact:
                value = _compact_status(endpoint, args.timeout)
            else:
                value = _request(endpoint, command, params, args.timeout)
        print(json.dumps(value, ensure_ascii=False, allow_nan=False))
        return 0
    except (CliError, OSError, ValueError) as exc:
        print(json.dumps({'ok': False, 'error': str(exc)}, ensure_ascii=False), file=sys.stderr)
        return 1


if __name__ == '__main__':
    raise SystemExit(main())
