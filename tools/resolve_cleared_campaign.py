"""Prepare a candidate, or exclusively publish an already reviewed candidate.

No game calls, guard edits or campaign ledger edits are performed. Publication
requires --publish plus --candidate; the explicit output must be the fixed
data/campaign_resolutions/<run>.json path under this project.
"""
from __future__ import annotations

import argparse
import hashlib
import os
from pathlib import Path
import sys
import tempfile

PROJECT_ROOT = Path(__file__).resolve().parents[1]
if str(PROJECT_ROOT) not in sys.path:
    sys.path.insert(0, str(PROJECT_ROOT))

from jiangshen_assistant.campaign_recovery import (ResolutionError, canonical_bytes, load_json_bytes,
                                                   prepare_resolution, validate_resolution)


def write_exclusive(path: Path, resolution: dict) -> bool:
    """Atomically create a whole proof. An identical existing proof is a no-op."""
    validate_resolution(resolution)
    payload = canonical_bytes(resolution) + b'\n'
    path.parent.mkdir(parents=True, exist_ok=True)
    name = None
    try:
        with tempfile.NamedTemporaryFile(dir=path.parent, prefix=path.name + '.', suffix='.tmp', delete=False) as stream:
            name = stream.name
            stream.write(payload)
            stream.flush()
            os.fsync(stream.fileno())
        try:
            os.link(name, path)
        except FileExistsError:
            existing_raw = path.read_bytes()
            existing = load_json_bytes(existing_raw)
            validate_resolution(existing)
            if existing_raw != payload:
                raise ResolutionError('existing output differs; append-only output cannot be overwritten')
            return False
        return True
    finally:
        if name is not None:
            os.unlink(name)


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    for key in ('checkpoint', 'guard', 'before', 'after', 'output'):
        parser.add_argument('--' + key, type=Path, required=True)
    parser.add_argument('--publish', action='store_true')
    parser.add_argument('--candidate', type=Path, help='Required for publish: the existing reviewed candidate, never rebuilt.')
    args = parser.parse_args(argv)
    try:
        paths = {key: getattr(args, key).resolve() for key in ('checkpoint', 'guard', 'before', 'after')}
        output = args.output.resolve()
        if output in paths.values() or (args.candidate is not None and output == args.candidate.resolve()):
            raise ResolutionError('output must be separate from every input')
        raw = {key: path.read_bytes() for key, path in paths.items()}
        content = {key: load_json_bytes(value) for key, value in raw.items()}
        if args.publish:
            if args.candidate is None:
                raise ResolutionError('--publish requires an existing reviewed --candidate')
            candidate_raw = args.candidate.read_bytes()
            resolution = load_json_bytes(candidate_raw)
            validate_resolution(resolution)
            destination = PROJECT_ROOT / 'data' / 'campaign_resolutions' / (resolution['run_id'] + '.json')
            if output != destination.resolve():
                raise ResolutionError('publish output must be data/campaign_resolutions/<run_id>.json')
            for key in paths:
                if hashlib.sha256(raw[key]).hexdigest() != resolution['sources'][key]['sha256']:
                    raise ResolutionError('source differs from reviewed candidate: ' + key)
        else:
            if args.candidate is not None:
                raise ResolutionError('--candidate is only accepted together with --publish')
            active_directories = ('campaign_resolutions', 'campaign_runs', 'object_sessions')
            if any(output.is_relative_to((PROJECT_ROOT / 'data' / name).resolve()) for name in active_directories):
                raise ResolutionError('prepare output cannot be an active resolution, ledger or session path')
            resolution = prepare_resolution(content['checkpoint'], content['guard'], content['before'], content['after'],
                                            checkpoint_bytes=raw['checkpoint'], guard_bytes=raw['guard'],
                                            before_bytes=raw['before'], after_bytes=raw['after'])
        # Recheck the reviewed input bytes immediately before the only write.
        for key, path in paths.items():
            if path.read_bytes() != raw[key]:
                raise ResolutionError('source changed during preparation: ' + key)
        if args.publish and args.candidate.read_bytes() != candidate_raw:
            raise ResolutionError('candidate changed during publication')
        created = write_exclusive(output, resolution)
        print(('published' if args.publish else 'prepared') + (' new: ' if created else ' unchanged: ') + str(output))
        return 0
    except (ResolutionError, OSError, ValueError, TypeError, KeyError, RecursionError) as exc:
        print('resolution refused: ' + str(exc), file=sys.stderr)
        return 2


if __name__ == '__main__':
    raise SystemExit(main())
