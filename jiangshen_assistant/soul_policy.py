"""Resolve the saved per-character policy for a soul gameplay mode."""
from __future__ import annotations

import json
from pathlib import Path

from .bridge_client import BridgeError
from .soul_strategy import SoulPolicy


def resolve_soul_policy(data_dir, character_id, mode, explicit=None) -> tuple[SoulPolicy, str]:
    if explicit is not None:
        return explicit, 'explicit'
    if (not isinstance(character_id, str) or not character_id.isascii()
            or not character_id.isdecimal() or not character_id.strip('0')):
        raise BridgeError('soul_saved_policy_character_invalid')
    if type(mode) is not int or mode not in (1, 2):
        raise ValueError('soul_saved_policy_mode_invalid')
    filename = f'{character_id}.mode1.json' if mode == 1 else f'{character_id}.json'
    path = (Path(data_dir) / 'soul-policies' / filename).resolve()
    try:
        raw = path.read_text(encoding='utf-8')
    except FileNotFoundError:
        return SoulPolicy(), 'built_in'
    except (OSError, UnicodeError) as exc:
        raise BridgeError(f'soul_saved_policy_unavailable ({path}: {exc})') from exc
    try:
        document = json.loads(raw)
        if (not isinstance(document, dict) or type(document.get('schema')) is not int
                or document['schema'] != 1 or document.get('character_id') != character_id
                or type(document.get('mode')) is not int or document['mode'] != mode
                or not isinstance(document.get('policy'), dict)):
            raise ValueError('identity_schema_or_mode_invalid')
        selected = SoulPolicy(**document['policy'])
        if selected.version == 'joint-value-v3' and not Path(selected.model_path).is_absolute():
            raise ValueError('model_path_must_be_absolute')
    except (TypeError, ValueError) as exc:
        raise BridgeError(f'soul_saved_policy_invalid ({path}: {exc})') from exc
    return selected, str(path)
