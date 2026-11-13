"""Offline loss and crash-order counterexamples against real Win32 persistence."""
import argparse
import ctypes
from ctypes import wintypes
import hashlib
import json
from pathlib import Path
import queue
import subprocess
import threading
import uuid


OPERATION = '0' * 30 + '43'
HISTORY = (43, 'Campaign', OPERATION, '0' * 30 + '52', 1026, 'a' * 64, 'b' * 64)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--exe', required=True)
    parser.add_argument('--output', required=True)
    parser.add_argument('--baseline-exe')
    args = parser.parse_args()
    output = Path(args.output).resolve()
    records = []
    checks = 0

    def save():
        (output / 'anchor-regression-traces.json').write_text(json.dumps(records, indent=2), encoding='utf-8')

    def fresh(label):
        return output / 'anchor-stores' / (label + '-' + uuid.uuid4().hex)

    def run(root, action, *values, blocked=False, exe=None):
        nonlocal checks
        result = subprocess.run([exe or args.exe, str(root), action, *map(str, values)], capture_output=True, text=True, timeout=15)
        record = dict(root=str(root), exe=exe or args.exe, action=action, values=list(map(str, values)), exit=result.returncode,
                      stdout=result.stdout, stderr=result.stderr)
        records.append(record)
        assert result.returncode == (2 if blocked else 0), record
        if blocked:
            assert 'SEND' not in result.stdout, record
        checks += 1
        return result.stdout

    def remove(path):
        path = path.resolve()
        assert path.is_relative_to(output), path
        path.unlink()

    def key(root):
        return root / 'role-43' / 'mode-Campaign'

    def missing_record_cases(exe, baseline):
        for kind in ('sole-start', 'latest-start-with-confirmed-history', 'sole-unknown', 'unknown-with-confirmed-history'):
            root = fresh(('baseline-' if baseline else 'fixed-') + kind)
            if 'confirmed-history' in kind:
                previous = run(root, 'send', exe=exe).split()[1]
                run(root, 'confirm', 43, 'Campaign', OPERATION, previous, exe=exe)
            if 'unknown' in kind:
                run(root, 'import-historical', *HISTORY, exe=exe)
                missing = next(root.rglob('*.unknown'))
            else:
                generation = run(root, 'send', exe=exe).split()[1]
                missing = key(root) / (generation + '.start')
            remove(missing)
            assert (key(root) / '.lock').is_file()
            result = run(root, 'send', blocked=not baseline, exe=exe)
            records.append(dict(case=kind, baseline=baseline, missing=str(missing), old_sends='SEND' in result))

    def barrier_kill(root, action, values, phase, before_kill=None):
        nonlocal checks
        process = subprocess.Popen([args.exe, str(root), action, *map(str, values), phase], stdin=subprocess.PIPE,
                                   stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, bufsize=1)
        lines = queue.Queue()
        trace = dict(root=str(root), action=action, phase=phase, lines=[])
        records.append(trace)

        def read():
            for line in process.stdout:
                lines.put(line.strip())
        reader = threading.Thread(target=read, daemon=True)
        reader.start()
        try:
            while True:
                line = lines.get(timeout=8)
                trace['lines'].append(line)
                if line == 'BARRIER ' + phase:
                    break
                assert not line.startswith(('BLOCKED ', 'FAILED ')), trace
            assert not any(line == 'SEND' for line in trace['lines']), trace
            if before_kill:
                before_kill()
            checks += 1
        finally:
            if process.poll() is None:
                process.kill()
            process.wait(timeout=8)
            process.stdin.close()
            reader.join(timeout=3)
            process.stdout.close()
            trace['exit'] = process.returncode

    def independent_anchor(directory):
        """Independent encoder, used to exercise semantic orphan validation."""
        names = sorted(path.name for path in directory.iterdir() if path.name not in ('.lock', '.anchor'))
        payload = 'JS_ASSISTANT_NATIVE_BATTLE_ANCHOR_V1\nrole=43\nmode=Campaign\nrecords=' + str(len(names)) + '\n'
        for name in names:
            payload += 'record=' + name + '|' + hashlib.sha256((directory / name).read_bytes()).hexdigest() + '\n'
        encoded = payload.encode('ascii')
        (directory / '.anchor').write_bytes(encoded + b'sha256=' + hashlib.sha256(encoded).hexdigest().encode('ascii') + b'\n')

    try:
        if args.baseline_exe:
            missing_record_cases(args.baseline_exe, True)
        missing_record_cases(args.exe, False)

        for mutation in ('missing-anchor', 'corrupt-anchor', 'extra-record', 'orphan-confirmation'):
            root = fresh(mutation)
            generation = run(root, 'send').split()[1]
            if mutation == 'missing-anchor':
                remove(key(root) / '.anchor')
            elif mutation == 'corrupt-anchor':
                with (key(root) / '.anchor').open('ab') as stream:
                    stream.write(b'corrupt')
            elif mutation == 'extra-record':
                source = key(root) / (generation + '.start')
                (key(root) / ('f' * 32 + '.start')).write_bytes(source.read_bytes())
            else:
                run(root, 'confirm', 43, 'Campaign', OPERATION, generation)
                remove(key(root) / (generation + '.start'))
                independent_anchor(key(root))  # Valid manifest, orphan semantic evidence.
            run(root, 'send', blocked=True)

        for phase in ('lock-created', 'genesis-staged', 'genesis-published', 'start-staged', 'start-published',
                      'start-anchor-staged', 'start-anchor-published'):
            root = fresh(phase)
            barrier_kill(root, 'pause-reserve', (43, 'Campaign', OPERATION), phase)
            run(root, 'send', blocked=phase != 'genesis-published')

        for phase in ('confirmation-staged', 'confirmation-published', 'confirmation-anchor-staged', 'confirmation-anchor-published'):
            root = fresh(phase)
            generation = run(root, 'send').split()[1]
            barrier_kill(root, 'pause-confirm', (43, 'Campaign', OPERATION, generation), phase)
            run(root, 'send', blocked=phase != 'confirmation-anchor-published')

        for phase in ('historical-staged', 'historical-published', 'historical-anchor-staged', 'historical-anchor-published'):
            root = fresh(phase)
            barrier_kill(root, 'pause-import', HISTORY, phase)
            run(root, 'send', blocked=True)

        # The first writer holds the real share=0 handle at a deterministic
        # barrier. Another key still works; the same key cannot enter or send.
        root = fresh('real-lock-isolation')
        def concurrent_checks():
            run(root, 'send', blocked=True)
            run(root, 'send', 44, 'Campaign')
            run(root, 'send', 43, 'Trial')
        barrier_kill(root, 'pause-reserve', (43, 'Campaign', OPERATION), 'start-staged', concurrent_checks)
        run(root, 'send', blocked=True)

        # Real Windows replacement failure, rather than a simulated API result:
        # a read handle without FILE_SHARE_DELETE prevents MoveFileEx replacement.
        kernel = ctypes.WinDLL('kernel32', use_last_error=True)
        kernel.CreateFileW.argtypes = (wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p,
                                      wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE)
        kernel.CreateFileW.restype = wintypes.HANDLE
        kernel.CloseHandle.argtypes = (wintypes.HANDLE,)
        kernel.CloseHandle.restype = wintypes.BOOL
        for kind in ('start', 'confirmation', 'historical'):
            root = fresh('atomic-replacement-' + kind)
            generation = run(root, 'send').split()[1]
            if kind != 'confirmation':
                run(root, 'confirm', 43, 'Campaign', OPERATION, generation)
            anchor = key(root) / '.anchor'
            previous_anchor = anchor.read_bytes()
            handle = kernel.CreateFileW(str(anchor), 0x80000000, 1, None, 3, 0, None)
            assert handle not in (None, ctypes.c_void_p(-1).value), ctypes.get_last_error()
            try:
                if kind == 'start':
                    run(root, 'send', blocked=True)
                elif kind == 'confirmation':
                    run(root, 'confirm', 43, 'Campaign', OPERATION, generation, blocked=True)
                else:
                    run(root, 'import-historical', *HISTORY, blocked=True)
            finally:
                assert kernel.CloseHandle(handle)
            assert previous_anchor == anchor.read_bytes() and (key(root) / '.anchor.tmp').is_file()
            checks += 1
            run(root, 'send', blocked=True)

        # Legacy samples retain their original evidence, but an absent anchor
        # never authorizes silently rebuilding an inventory from surviving files.
        if args.baseline_exe:
            root = fresh('legacy-historical-no-anchor')
            path = Path(run(root, 'import-historical', *HISTORY, exe=args.baseline_exe).strip())
            previous = path.read_bytes()
            run(root, 'validate-historical', *HISTORY[:3], path)
            run(root, 'send', blocked=True)
            run(root, 'import-historical', *HISTORY, blocked=True)
            assert previous == path.read_bytes() and not (key(root) / '.anchor').exists()
            checks += 1

        root = fresh('multi-round')
        previous = None
        for _ in range(12):
            generation = run(root, 'send').split()[1]
            if previous:
                run(root, 'confirm', 43, 'Campaign', OPERATION, previous)
                run(root, 'send', blocked=True)
            run(root, 'confirm', 43, 'Campaign', OPERATION, generation)
            previous = generation
        summary = dict(status='PASS', checks=checks, baseline_reproduced=bool(args.baseline_exe),
                       baseline_exe_sha256=hashlib.sha256(Path(args.baseline_exe).read_bytes()).hexdigest() if args.baseline_exe else None,
                       game_access=False, network_access=False, real_win32_locks=True, real_atomic_replace_failure=True,
                       limits='No hardware power-loss simulation. Loss of every independent local record and anchor is indistinguishable from first use; no automatic legacy migration.')
        (output / 'anchor-regression-summary.json').write_text(json.dumps(summary, indent=2), encoding='utf-8')
        print(json.dumps(summary))
    finally:
        save()


if __name__ == '__main__':
    main()
