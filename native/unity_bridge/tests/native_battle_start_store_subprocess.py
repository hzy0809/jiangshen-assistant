"""Offline persistence/process checks against the compiled production C# store."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import uuid


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--exe', required=True)
    parser.add_argument('--output', required=True)
    parser.add_argument('--mono-host')
    parser.add_argument('--mono-dll')
    parser.add_argument('--managed-dir')
    args = parser.parse_args()
    output = Path(args.output)
    records = []
    checks = 0
    command = [args.exe]
    if args.mono_host:
        command = [args.mono_host, args.mono_dll, args.managed_dir, args.exe]

    def fresh():
        return output / 'stores' / uuid.uuid4().hex

    def run(root, action, *values, blocked=False):
        nonlocal checks
        result = subprocess.run([*command, str(root), action, *map(str, values)], capture_output=True, text=True, timeout=15)
        records.append(dict(action=action, root=str(root), exit=result.returncode, stdout=result.stdout, stderr=result.stderr))
        assert result.returncode == (2 if blocked else 0), records[-1]
        if blocked:
            assert 'SEND' not in result.stdout, records[-1]
        checks += 1
        return result.stdout

    self_checks = int(run(fresh(), 'self').split('checks=')[-1].strip())
    root = fresh()
    first = run(root, 'send').split()[1]
    run(root, 'send', blocked=True)  # Fresh process, same stable key.
    run(root, 'confirm', 43, 'Campaign', '0' * 30 + '43', first)
    second = run(root, 'send').split()[1]
    assert first != second
    checks += 1
    run(root, 'confirm', 43, 'Campaign', '0' * 30 + '43', first)
    run(root, 'send', blocked=True)  # Old callback cannot clear second.
    run(root, 'inspect', 43, 'Campaign', '0' * 30 + '43', second)
    run(root, 'send', 44, 'Campaign')
    run(root, 'send', 43, 'Trial')

    root = fresh()
    children = [subprocess.Popen([*command, str(root), 'send'], stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True) for _ in range(12)]
    concurrent = [child.communicate(timeout=15) for child in children]
    assert sum('SEND' in stdout for stdout, _ in concurrent) == 1, concurrent
    assert sum(child.returncode == 0 for child in children) == 1
    assert all(child.returncode in (0, 2) for child in children)
    records.append(dict(action='concurrency', results=[dict(exit=c.returncode, stdout=r[0], stderr=r[1]) for c, r in zip(children, concurrent)]))
    checks += 3
    run(root, 'send', blocked=True)

    root = fresh()
    crashed = subprocess.Popen([*command, str(root), 'hold'], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    reserved = crashed.stdout.readline()
    ready = crashed.stdout.readline()
    assert reserved.startswith('RESERVED ') and ready.strip() == 'READY', (reserved, ready)
    crashed.kill()
    tail, stderr = crashed.communicate(timeout=15)
    records.append(dict(action='kill-after-durable-reserve', exit=crashed.returncode, stdout=reserved + ready + tail, stderr=stderr))
    checks += 1
    run(root, 'send', blocked=True)

    for failure in ('write-failure', 'publish-failure'):
        root = fresh()
        run(root, failure, blocked=True)
        if failure == 'publish-failure':
            run(root, 'send', blocked=True)
    root = fresh()
    run(root, 'import-unknown')
    marker = next(root.rglob('*.start'))
    marker.write_bytes(marker.read_bytes() + b'corrupt')
    run(root, 'send', blocked=True)
    root = fresh()
    root.parent.mkdir(parents=True, exist_ok=True)
    root.write_text('cannot be a store directory')
    run(root, 'send', blocked=True)

    history_values = (43, 'Campaign', '0' * 30 + '43', '0' * 30 + '52', 1026, 'a' * 64, 'b' * 64)
    for existing in ('none', 'active', 'confirmed'):
        root = fresh()
        previous = None
        if existing != 'none':
            previous = run(root, 'send').split()[1]
            if existing == 'confirmed':
                run(root, 'confirm', 43, 'Campaign', '0' * 30 + '43', previous)
        run(root, 'import-historical', *history_values)
        original = next(root.rglob('*.unknown')).read_bytes()
        run(root, 'import-historical', *history_values)
        assert original == next(root.rglob('*.unknown')).read_bytes()
        run(root, 'send', blocked=True)
        if previous:
            run(root, 'confirm', 43, 'Campaign', '0' * 30 + '43', previous, blocked=True)
        unknown = next(root.rglob('*.unknown'))
        unknown.write_bytes(original + b'corrupt')
        run(root, 'send', blocked=True)

    # Exact historical incident, supplied by the controller. This sandbox-only
    # blocker asserts UNKNOWN and source response identity; it invents neither
    # a native start generation nor a server/native confirmation receipt.
    root = output / 'historical-incident-sample'
    role = 55290000000275
    mode = 'NormalChallenge'
    attempt = 'f3459f4d9a444bf2b95a2ea84938b57c'
    session = '98487130f5834eaeb945528fca8975ef'
    state_hash = '8c2ba5979cb3d85fde8fae0c5727c9f36e0309abadffd4c952fc4db275368fe1'
    events_hash = '53608ae180a23c467ed109955bf777854b9dbee4c93e47751cd449effbfab40b'
    run(root, 'import-historical', role, mode, attempt, session, 1026, state_hash, events_hash)
    marker = next(root.rglob('*.unknown'))
    body = marker.read_bytes()
    payload, digest_line = body.rsplit(b'sha256=', 1)
    digest = hashlib.sha256(payload).hexdigest()
    assert digest_line == (digest + '\n').encode('ascii')
    assert marker.name == f'historical-{digest}.unknown'
    expected = [
        'JS_ASSISTANT_NATIVE_HISTORICAL_UNKNOWN_V1', f'role={role}', f'mode={mode}',
        f'source_attempt={attempt}', f'source_session={session}', 'response_opcode=1026',
        f'state_sha256={state_hash}', f'events_sha256={events_hash}', 'state=UNKNOWN',
    ]
    assert payload.decode('ascii').splitlines() == expected
    checks += 3
    run(root, 'validate-historical', role, mode, attempt, marker)
    run(root, 'send', role, mode, attempt, blocked=True)
    (output / 'subprocess-results.json').write_text(json.dumps(records, indent=2), encoding='utf-8')
    summary = dict(status='PASS', subprocess_checks=checks, self_checks=self_checks, concurrent_processes=12, game_access=False, network_access=False,
                   runtime='pinned-game-mono-offline' if args.mono_host else 'desktop-clr',
                   limits='Pinned legacy API compile and offline filesystem/process checks. No Unity/gameplay, deployment or power-loss test.')
    (output / 'summary.json').write_text(json.dumps(summary, indent=2), encoding='utf-8')
    print(json.dumps(summary))


if __name__ == '__main__':
    main()
