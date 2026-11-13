"""Durable zero-replay checks using the real production launch path and Win32 IPC."""
import argparse
import json
import os
from pathlib import Path
import queue
import subprocess
import sys
import threading
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parents[3]))
from jiangshen_assistant.battle_launch import BattleLaunchAdmission


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--exe', required=True)
    parser.add_argument('--output', required=True)
    args = parser.parse_args()
    output = Path(args.output)
    records = []
    checks = 0

    def fresh():
        return output / 'durable-stores' / uuid.uuid4().hex

    class Child:
        def __init__(self, root, kind='formation', fault='', role=123, blocked=False):
            self.guard = BattleLaunchAdmission(uuid.uuid4().hex)
            epoch = self.guard.open_epoch()
            environment = dict(os.environ, JSB_NATIVE_DURABLE_TEST_ROOT=str(root), JSB_NATIVE_DURABLE_TEST_FAULT=fault,
                               JSB_NATIVE_DURABLE_TEST_ROLE=str(role))
            self.process = subprocess.Popen([args.exe, self.guard.nonce, epoch, kind, '0' * 30 + '43'], env=environment,
                stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, bufsize=1,
                creationflags=subprocess.CREATE_NO_WINDOW)
            self.lines = queue.Queue()
            self.trace = dict(root=str(root), kind=kind, fault=fault, role=role, lines=[])
            records.append(self.trace)

            def read():
                for line in self.process.stdout:
                    self.lines.put(line.strip())
            threading.Thread(target=read, daemon=True).start()
            if blocked:
                self.until('FAILED ')
                assert self.process.wait(5) == 2
                assert not any(line.startswith('SEND ') or line == 'READY' for line in self.trace['lines']), self.trace
            else:
                self.until('READY')

        def until(self, prefix):
            while True:
                line = self.lines.get(timeout=5)
                self.trace['lines'].append(line)
                if line.startswith(prefix):
                    return line
                assert not line.startswith('FAILED '), self.trace

        def command(self, instruction):
            self.process.stdin.write(instruction + '\n')
            self.process.stdin.flush()
            state = json.loads(self.until('STATE ')[6:])
            counts = tuple(map(int, self.until('COUNTS ')[7:].split()))
            return state, counts

        def close(self):
            if self.process.poll() is None:
                self.process.kill()
            self.process.wait(5)
            self.process.stdin.close()
            self.process.stdout.close()
            self.guard.close()

    # Each route is killed with an unresolved generation, including starts that
    # deferred through 298, story or stamina. A fresh session/PID must not send.
    for kind in ('formation', 'direct', 'batch', 'scenario', 'stamina-drift', 'challenge'):
        root = fresh()
        child = Child(root, kind)
        if kind not in ('direct', 'batch'):
            state, counts = child.command('formation')
            assert counts[0] == (0 if kind in ('scenario', 'stamina-drift') else 1), child.trace
        child.close()
        denied = Child(root, kind, blocked=True)
        denied.close()
        checks += 1

    root = fresh()
    child = Child(root)
    child.command('formation')
    child.command('reply')
    state, counts = child.command('transfer')
    assert state['report_transferred'] and counts[0] == 1
    child.close()
    denied = Child(root, blocked=True)
    denied.close()
    checks += 1

    root = fresh()
    child = Child(root, 'batch')
    state, _ = child.command('reply')
    assert state['state'] == 'NATIVE_NONREPORT_CONFIRMED', child.trace
    first = state['durable_generation']
    state, counts = child.command('new-attempt')
    assert counts[0] == 2 and state['durable_generation'] != first, child.trace
    state, counts = child.command('old-reply')
    assert counts[0] == 2 and state['state'] == 'REQUEST_OBSERVED', child.trace
    assert len(list(root.rglob('*.start'))) == 2 and len(list(root.rglob('*.confirmed'))) == 1
    child.close()
    denied = Child(root, 'batch', blocked=True)
    denied.close()
    checks += 1

    for fault in ('before-write', 'after-flush'):
        root = fresh()
        denied = Child(root, fault=fault, blocked=True)
        denied.close()
        if fault == 'after-flush':
            denied = Child(root, blocked=True)
            denied.close()
        checks += 1

    root = fresh()
    child = Child(root)
    child.close()
    marker = next(root.rglob('*.start'))
    marker.write_bytes(marker.read_bytes() + b'corrupt')
    denied = Child(root, blocked=True)
    denied.close()
    checks += 1

    root = fresh()
    child = Child(root, 'direct')
    child.close()
    other_mode = Child(root)
    other_mode.close()
    other_role = Child(root, 'direct', role=124)
    other_role.close()
    checks += 1

    (output / 'durable-launch-traces.json').write_text(json.dumps(records, indent=2), encoding='utf-8')
    summary = dict(status='PASS', durable_launch_checks=checks, game_access=False, network_access=False,
                   coverage='Production launch and durable store, real Win32 IPC and cold subprocesses; detached native callbacks.')
    (output / 'durable-launch-summary.json').write_text(json.dumps(summary, indent=2), encoding='utf-8')
    print(json.dumps(summary))


if __name__ == '__main__':
    main()
