"""Prepare then publish one reviewed, sealed equipment progress disposition.

Sources are canonical incident/session paths. This tool never talks to the game,
changes a guard or journal, retries a command, or grants a new goal budget.
"""
from __future__ import annotations

import argparse
import os
from pathlib import Path
import sys
import tempfile

PROJECT_ROOT = Path(__file__).resolve().parents[1]
if str(PROJECT_ROOT) not in sys.path:
    sys.path.insert(0, str(PROJECT_ROOT))

from jiangshen_assistant.development_recovery import (
    CREATION, ORIGINAL_OPERATION, ORIGINAL_SESSION, PID, HANDLER_PROOF,
    ResolutionError, build_equipment_progress_resolution, business_fact_hash,
    verify_equipment_progress_resolution,
)
from jiangshen_assistant.campaign_recovery import canonical_bytes, load_json_bytes
import hashlib
import re


def _plain(path):
    path = path.absolute()
    if path.resolve() != path:
        raise ResolutionError('symlink/junction or noncanonical path refused')
    return path


def canonical_sources(refresh_session):
    if not re.fullmatch('[0-9a-f]{32}', refresh_session) or refresh_session == ORIGINAL_SESSION:
        raise ResolutionError('new canonical refresh session required')
    data = PROJECT_ROOT / 'data'
    return {name: _plain(path) for name, path in {
        'journal': data / 'development_runs' / (ORIGINAL_OPERATION + '.json'),
        'original_state': data / 'object_sessions' / ORIGINAL_SESSION / 'state.json',
        'refresh_state': data / 'object_sessions' / refresh_session / 'player-refresh-observed.json',
        'guard': data / 'object_sessions' / 'leases' / f'{PID}-{CREATION}.business.json',
    }.items()}


def matching_facts(guard):
    """Flatten only this origin; other retained campaign facts remain untouched."""
    result, seen, count = [], set(), 0
    while guard is not None:
        count += 1
        if type(guard) is not dict or count > 64:
            raise ResolutionError('guard chain malformed or exceeds bound')
        fact = {k: v for k, v in guard.items() if k != 'prior_unresolved_business'}
        if guard.get('session_id') == ORIGINAL_SESSION:
            business = guard.get('business')
            if type(business) is not dict or business.get('pending_development') is not True:
                raise ResolutionError('original guard no longer matches pending development')
            fact_hash = business_fact_hash(fact)
            if fact_hash not in seen:
                seen.add(fact_hash)
                result.append(fact)
        guard = guard.get('prior_unresolved_business')
    if not result:
        raise ResolutionError('matching original pending business not found')
    return result


def write_exclusive(path, proof):
    verify_equipment_progress_resolution(proof)
    payload = canonical_bytes(proof) + b'\n'
    path = _plain(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    _plain(path)
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(dir=path.parent, prefix=path.name + '.', suffix='.tmp', delete=False) as stream:
            temporary = stream.name
            stream.write(payload)
            stream.flush()
            os.fsync(stream.fileno())
        try:
            os.link(temporary, path)
        except FileExistsError:
            if path.read_bytes() != payload:
                raise ResolutionError('existing disposition/candidate differs; append-only output retained')
            return False
        return True
    finally:
        if temporary is not None:
            os.unlink(temporary)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    modes = parser.add_mutually_exclusive_group(required=True)
    modes.add_argument('--prepare', action='store_true')
    modes.add_argument('--publish', action='store_true')
    parser.add_argument('--refresh-session', required=True)
    parser.add_argument('--candidate', type=Path, required=True)
    parser.add_argument('--reviewed-sha256', help='Exact candidate file SHA256 reviewed before publication')
    args = parser.parse_args(argv)
    try:
        paths = canonical_sources(args.refresh_session)
        candidate = _plain(args.candidate)
        acceptance = _plain(PROJECT_ROOT / 'data' / 'acceptance')
        if not candidate.is_relative_to(acceptance):
            raise ResolutionError('candidate must be a separate artifact under data/acceptance')
        if candidate in paths.values():
            raise ResolutionError('candidate aliases source')
        raw = {name: path.read_bytes() for name, path in paths.items()}
        facts = matching_facts(load_json_bytes(raw['guard']))
        if args.prepare:
            if args.reviewed_sha256 is not None:
                raise ResolutionError('review hash is for publication only')
            proof = build_equipment_progress_resolution(raw['journal'], raw['original_state'],
                raw['refresh_state'], facts, HANDLER_PROOF)
            output = candidate
        else:
            if args.reviewed_sha256 is None or not re.fullmatch('[0-9a-f]{64}', args.reviewed_sha256):
                raise ResolutionError('--publish requires --reviewed-sha256 of existing candidate')
            candidate_bytes = candidate.read_bytes()
            if hashlib.sha256(candidate_bytes).hexdigest() != args.reviewed_sha256:
                raise ResolutionError('reviewed candidate hash mismatch')
            proof = load_json_bytes(candidate_bytes)
            verify_equipment_progress_resolution(proof, **{name + '_bytes': raw[name]
                for name in ('journal', 'original_state', 'refresh_state')})
            current = sorted(business_fact_hash(fact) for fact in facts)
            if current != proof['business_fact_hashes']:
                raise ResolutionError('business facts changed since preparation')
            output = _plain(PROJECT_ROOT / 'data' / 'development_resolutions' / (ORIGINAL_OPERATION + '.json'))
        for name, path in paths.items():
            if _plain(path).read_bytes() != raw[name]:
                raise ResolutionError('source changed during resolution: ' + name)
        if args.publish and candidate.read_bytes() != candidate_bytes:
            raise ResolutionError('candidate changed during publication')
        created = write_exclusive(output, proof)
        print(('prepared' if args.prepare else 'published') + (' new: ' if created else ' unchanged: ') + str(output))
        if args.prepare:
            print('candidate_sha256=' + hashlib.sha256(output.read_bytes()).hexdigest())
        return 0
    except (OSError, ValueError, TypeError, KeyError, RecursionError, AttributeError) as exc:
        print('development resolution refused: ' + str(exc), file=sys.stderr)
        return 2


if __name__ == '__main__':
    raise SystemExit(main())
