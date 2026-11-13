"""Receipt-backed whole-board utility bounds; no server probability model.

This module neither grants action authority nor executes game actions.  The caller
owns fresh identity/offer/quote checks, balances, the total budget and the absolute
growth safety cap.  Historical rewards are estimates when applied to a live board.
"""
from __future__ import annotations

from collections import defaultdict
from collections.abc import Mapping
from copy import deepcopy
from hashlib import sha256
from itertools import product
import json
from math import sqrt
from pathlib import Path
from time import perf_counter

from .soul_economics import opening_reference_counts


_COLORS = (1, 2, 3, 4)
_VERSION = 'joint-value-v3-chain-v8-budget-precheck-h2'
_MODE_COLORS = {1: 'blue', 2: 'purple'}
_UTILITY_VERSION = 'user-blue-purple-orange-v1'
_UTILITY_WEIGHTS = {1: 1, 2: 5, 3: 10}
_CONTINUATION_SECONDS = 0.4
_CONTINUATION_NODES = 32
# Engineering coverage limits, not fitted probabilities or confidence levels.
_NEIGHBORS = 12
_RADIUS = 0.24
_NEAREST_LIMIT = 0.20
_MIN_ROUNDS = 3
_MIN_EFFECTIVE_ROUNDS = 2.5
_FIRST_PHASE_COMPLETION_LIMIT = 256
_FIRST_PHASE_TRACE_LIMIT = 16
_OUTCOME_TRACE_LIMIT = 32
_DISTANCE_WEIGHTS = {
    'ordered_levels': 0.35, 'level_distribution': 0.25,
    'harvest_value': 0.20, 'one_level_award_increments': 0.10,
    'growth_count': 0.10,
}


def _integer(value, minimum=0):
    return type(value) is int and value >= minimum


def _board(value):
    if not isinstance(value, (list, tuple)) or len(value) != 8:
        raise ValueError('board_shape_invalid')
    result = []
    for slot in value:
        if isinstance(slot, Mapping):
            color, level = slot.get('color'), slot.get('level')
        elif isinstance(slot, (list, tuple)) and len(slot) == 2:
            color, level = slot
        else:
            raise ValueError('board_slot_invalid')
        if (not _integer(color) or color > 4 or not _integer(level) or level > 10
                or (color == 0) != (level == 0)):
            raise ValueError('board_slot_invalid')
        result.append((color, level))
    return tuple(result)


def _award(value):
    if (not isinstance(value, (list, tuple)) or len(value) != 4
            or any(not _integer(x) for x in value)):
        raise ValueError('award_invalid')
    return tuple(value)


def _by_color(values):
    return {str(color): values[color - 1] for color in _COLORS}


def _utility(values):
    """User-priced utility only; unpriced-color exposure is guarded separately."""
    return sum(_UTILITY_WEIGHTS[color] * values[color - 1] for color in _UTILITY_WEIGHTS)


def _fingerprint(value):
    return sha256(json.dumps(value, sort_keys=True, separators=(',', ':'),
                             ensure_ascii=False).encode('utf-8')).hexdigest()


class JointSoulModel:
    """Conditional nearest receipts, with explicit absence-of-evidence fallbacks.

    ``load`` returns an inert model on missing or conflicting evidence.  Check
    ``ready`` and ``blocking_issues`` before paying for a new round.  Online
    updates accept an entire completed, closed ledger, never an action intent or
    an in-progress round; receipt IDs deduplicate repeated observations.
    """

    def __init__(self, *, character_id, mode=2):
        if not isinstance(character_id, str) or not character_id.isdecimal() or int(character_id) <= 0:
            raise ValueError('soul_joint_character_id_invalid')
        if type(mode) is not int or mode not in _MODE_COLORS:
            raise ValueError('soul_joint_mode_unsupported')
        self.character_id = character_id
        self.mode = mode
        self._issues = []
        self._rejected = []
        self._rounds = {}
        self._candidates = {}
        self._payoff = {}
        self._observed_payoff = {}
        self._shared_level_payoff = {}
        self.shared_game_rules = False
        self.shared_level_quantities = False
        self._samples = []
        self._sources = {}
        self._dataset_path = ''
        self._dataset_hash = ''
        self._additional_paths = []
        self._online_updates = 0
        self._index_summary = {}
        self._index_fingerprint = ''
        self._provenance_cache = None

    @property
    def blocking_issues(self):
        return deepcopy(self._issues)

    @property
    def payoff_mapping(self):
        """Detached effective mapping; provenance separates receipts/shared rules."""
        return deepcopy(self._payoff_nested())

    @property
    def provenance(self):
        """Read-only identity for decisions stopped before a forecast is needed."""
        return self._provenance()

    @property
    def ready(self):
        return (not self._issues and bool(self._samples)
                and any(color == self.mode for color, _ in self._payoff)
                and sum(r['mode'] == self.mode and r['start_cost'] > 0
                        for r in self._rounds.values()) >= _MIN_ROUNDS)

    def _block(self, reason, **detail):
        issue = {'reason': 'soul_joint_' + reason, **detail}
        if issue not in self._issues:
            self._issues.append(issue)
            self._provenance_cache = None

    def _reject(self, reason, **detail):
        item = {'reason': 'soul_joint_' + reason, **detail}
        if item not in self._rejected:
            self._rejected.append(item)
            self._provenance_cache = None

    @classmethod
    def load(cls, path, *, character_id, mode=2):
        """Load a joint-transitions artifact and rebind it to its raw ledgers.

        Aggregated estimates and prose receipt rules are not admission proofs.
        Actual role, mode, verified settlement, costs and slot rewards come from
        the referenced source ledgers, whose hashes are retained in provenance.
        """
        model = cls(character_id=character_id, mode=mode)
        model._dataset_path = str(Path(path).resolve())
        try:
            raw = Path(path).read_bytes()
            dataset = json.loads(raw)
            model._dataset_hash = sha256(raw).hexdigest()
            if (not isinstance(dataset, dict)
                    or dataset.get('schema') not in ('purple-joint-transitions-v1', 'soul-joint-receipts-v1')
                    or dataset.get('character_id') != character_id
                    or dataset.get('schema') == 'soul-joint-receipts-v1'
                        and dataset.get('mode', mode) != mode):
                model._block('dataset_identity_or_schema_invalid')
                return model
            for flag in ('shared_game_rules', 'shared_level_quantities'):
                value = dataset.get(flag, False)
                if type(value) is not bool:
                    raise ValueError(flag + '_must_be_boolean')
                setattr(model, flag, value)
            if dataset.get('mismatches') or dataset.get('coverage', {}).get('conflicting_pairs'):
                model._block('dataset_reports_evidence_conflict')
            rows, transitions = dataset.get('rounds', []), dataset.get('transitions', [])
            if not isinstance(rows, list) or not isinstance(transitions, list):
                model._block('dataset_records_invalid')
                return model
            grouped = defaultdict(list)
            selected = {}
            for row in rows:
                if not isinstance(row, dict) or not isinstance(row.get('source'), str):
                    raise ValueError('dataset_round_source_invalid')
                grouped[row['source']].append(row)
            for transition in transitions:
                if not isinstance(transition, dict):
                    raise ValueError('dataset_transition_invalid')
                key = (transition.get('operation_id'), transition.get('round'), transition.get('sequence'))
                if key in selected and selected[key] != transition:
                    raise ValueError('dataset_transition_id_conflict')
                selected[key] = transition
            for source, summaries in grouped.items():
                try:
                    source_path = Path(source)
                    if not source_path.is_absolute():
                        source_path = Path(path).parent / source_path
                    source_raw = source_path.read_bytes()
                    ledger = json.loads(source_raw)
                    model._sources[str(source_path.resolve())] = sha256(source_raw).hexdigest()
                    model._ingest_ledger(ledger, str(source_path.resolve()),
                                         summaries=summaries if mode == 2 else None,
                                         selected=selected if mode == 2 else None)
                except (OSError, ValueError, TypeError, KeyError) as exc:
                    model._block('source_receipt_invalid', source=source,
                                 detail=str(exc))
            additional = dataset.get('additional_ledgers', [])
            if (not isinstance(additional, list)
                    or any(not isinstance(source, str) or not Path(source).is_absolute()
                           for source in additional)):
                raise ValueError('additional_ledger_paths_must_be_absolute')
            for source in additional:
                source_path = Path(source).resolve()
                normalized_source = str(source_path)
                if normalized_source in model._sources:
                    continue
                try:
                    source_raw = source_path.read_bytes()
                    model._ingest_ledger(json.loads(source_raw), normalized_source)
                    model._sources[normalized_source] = sha256(source_raw).hexdigest()
                    model._additional_paths.append(normalized_source)
                except (OSError, ValueError, TypeError, KeyError) as exc:
                    model._block('additional_ledger_invalid', source=source, detail=str(exc))
            for key, transition in selected.items():
                if (transition.get('mode', 2) == mode and key not in model._candidates):
                    model._block('transition_not_bound_to_verified_receipt',
                                 sample_id=model._sample_id(key))
            model._refresh()
            for item in dataset.get('observed_payoff', []) if mode == 2 else ():
                pair = (item.get('color'), item.get('level'))
                quantities = item.get('quantity_counts', {})
                if (item.get('conflict') or len(quantities) != 1
                        or pair not in model._payoff
                        or str(model._payoff[pair]) not in quantities):
                    model._block('payoff_summary_conflicts_with_receipts', pair=list(pair))
        except (OSError, ValueError, TypeError, KeyError, AttributeError) as exc:
            model._block('dataset_unavailable_or_invalid', detail=str(exc))
        if not model._samples:
            model._block('no_verified_joint_samples')
        if not any(color == mode for color, _ in model._payoff):
            model._block('target_payoff_coverage_absent', target_color=mode)
        if not any(r['mode'] == mode and r['start_cost'] > 0 for r in model._rounds.values()):
            model._block('no_verified_paid_rounds')
        model._refresh_index()
        return model

    @staticmethod
    def _sample_id(key):
        return f'{key[0]}:r{key[1]}:s{key[2]}'

    def _ingest_ledger(self, ledger, source, *, summaries=None, selected=None):
        if (not isinstance(ledger, Mapping) or ledger.get('character_id') != self.character_id
                or ledger.get('status') != 'COMPLETED' or ledger.get('cleanup') != 'VERIFIED_CLOSED'
                or ledger.get('pending_action') is not None or ledger.get('evidence_error')
                or not isinstance(ledger.get('operation_id'), str) or not ledger['operation_id']
                or not isinstance(ledger.get('rounds'), (list, tuple))
                or not _integer(ledger.get('coins_spent'))):
            raise ValueError('ledger_identity_completion_or_close_unverified')
        operation_id = ledger['operation_id']
        rounds = ledger['rounds']
        if (any(not isinstance(r, Mapping) or not _integer(r.get('coins_spent')) for r in rounds)
                or sum(r['coins_spent'] for r in rounds) != ledger['coins_spent']):
            raise ValueError('ledger_round_costs_do_not_reconcile')
        summary_by_round = None
        if summaries is not None:
            summary_by_round = {}
            for summary in summaries:
                if summary.get('operation_id') != operation_id:
                    raise ValueError('summary_operation_mismatch')
                number = summary.get('round')
                if number in summary_by_round:
                    raise ValueError('duplicate_summary_round')
                summary_by_round[number] = summary
        found = set()
        for raw_round in rounds:
            number = raw_round.get('round')
            if summary_by_round is not None and number not in summary_by_round:
                continue
            round_mode = raw_round.get('mode')
            if type(round_mode) is not int or round_mode not in _MODE_COLORS or raw_round.get('color') != _MODE_COLORS[round_mode]:
                if summary_by_round is not None:
                    raise ValueError('summary_round_mode_mismatch')
                continue
            if (not _integer(number, 1) or raw_round.get('start_verified') is not True
                    or raw_round.get('harvest_verified') is not True
                    or not _integer(raw_round.get('start_cost'))
                    or raw_round['coins_spent'] < raw_round['start_cost']
                    or not _integer(raw_round.get('growth_count'))
                    or not _integer(raw_round.get('start_sequence'), 1)
                    or not _integer(raw_round.get('settle_sequence'), raw_round['start_sequence'] + 1)
                    or type(raw_round.get('auto_harvest')) is not bool):
                raise ValueError('round_settlement_or_cost_unverified')
            award = _award(raw_round.get('award'))
            if summary_by_round is not None:
                summary = summary_by_round[number]
                for field in ('round', 'color', 'start_cost', 'start_sequence', 'settle_sequence',
                              'award', 'coins_spent', 'harvest_before_board', 'harvest_slot_awards'):
                    if summary.get(field) != raw_round.get(field):
                        raise ValueError('summary_round_mismatch:' + field)
                if summary.get('growth_count_reported') != raw_round['growth_count']:
                    raise ValueError('summary_growth_count_mismatch')
            key = (operation_id, number)
            harvest_board = raw_round.get('harvest_before_board')
            slots = raw_round.get('harvest_slot_awards')
            pairs = []
            if harvest_board is not None and slots is not None:
                harvest_board = _board(harvest_board)
                if not isinstance(slots, (list, tuple)) or len(slots) != 8:
                    raise ValueError('harvest_slot_awards_invalid')
                total = [0] * 4
                for (color, level), slot in zip(harvest_board, slots):
                    if isinstance(slot, Mapping):
                        award_color, quantity = slot.get('color'), slot.get('number')
                    elif isinstance(slot, (list, tuple)) and len(slot) == 2:
                        award_color, quantity = slot
                    else:
                        raise ValueError('harvest_slot_award_invalid')
                    if (type(award_color) is not int or award_color != color or not _integer(quantity)
                            or (color == 0 and quantity != 0)):
                        raise ValueError('harvest_slot_color_or_quantity_mismatch')
                    if color:
                        total[color - 1] += quantity
                        pairs.append((color, level, quantity))
                if tuple(total) != award:
                    raise ValueError('harvest_slot_total_mismatch')
                evidence_sequence = raw_round.get('harvest_evidence_sequence')
                if evidence_sequence is not None and evidence_sequence != raw_round['settle_sequence']:
                    raise ValueError('harvest_sequence_mismatch')
            else:
                self._reject('round_has_no_slot_payoff_evidence', round_id=f'{operation_id}:r{number}')
            # Optional economic evidence. A final harvested award is never
            # substituted for the payoff of the actually observed start board.
            try:
                initial_board = _board(raw_round.get('board_before_growth'))
                if not any(color for color, _ in initial_board):
                    initial_board = None
            except ValueError:
                initial_board = None
            normalized = {
                'operation_id': operation_id, 'round': number, 'mode': round_mode,
                'start_cost': raw_round['start_cost'], 'coins_spent': raw_round['coins_spent'],
                'award': award, 'start_sequence': raw_round['start_sequence'],
                'settle_sequence': raw_round['settle_sequence'],
                'growth_count': raw_round['growth_count'], 'payoff_pairs': pairs,
                'initial_board': initial_board, 'adopted_board': raw_round.get('adopted_board') is True,
                'source': source,
            }
            if key in self._rounds:
                prior = {k: v for k, v in self._rounds[key].items() if k != 'source'}
                if prior != {k: v for k, v in normalized.items() if k != 'source'}:
                    raise ValueError('settled_round_id_conflict')
            else:
                self._rounds[key] = normalized
            found.add(number)
            growths = list(raw_round.get('growths', ()))
            growths.extend(g for g in ledger.get('growths', ()) if g.get('round') == number)
            admitted = set()
            for growth in growths:
                sequence = growth.get('sequence')
                growth_key = (operation_id, number, sequence)
                if selected is not None and growth_key not in selected:
                    continue
                candidate = self._growth_candidate(growth, normalized)
                if selected is not None:
                    summary = selected[growth_key]
                    for field in ('board_before', 'board_after', 'growth_steps', 'growth_before',
                                  'growth_after', 'quoted_cost', 'coins_before', 'coins_after',
                                  'board_after_source', 'auto_harvest'):
                        if summary.get(field) != growth.get(field):
                            raise ValueError('summary_growth_mismatch:' + field)
                    if (summary.get('settle_sequence') != normalized['settle_sequence']
                            or _award(summary.get('actual_round_award')) != award):
                        raise ValueError('summary_growth_settlement_mismatch')
                if growth_key in self._candidates:
                    prior = {k: v for k, v in self._candidates[growth_key].items() if k != 'source'}
                    if prior != {k: v for k, v in candidate.items() if k != 'source'}:
                        raise ValueError('growth_receipt_id_conflict')
                else:
                    self._candidates[growth_key] = candidate
                admitted.add(sequence)
            if len(admitted) != raw_round['growth_count']:
                self._reject('round_growth_detail_incomplete', round_id=f'{operation_id}:r{number}',
                             reported=raw_round['growth_count'], available=len(admitted))
        if summary_by_round is not None and found != set(summary_by_round):
            raise ValueError('summary_round_not_in_source')

    def _growth_candidate(self, growth, settled_round):
        if (not isinstance(growth, Mapping) or growth.get('round') != settled_round['round']
                or not _integer(growth.get('sequence'), settled_round['start_sequence'] + 1)
                or growth['sequence'] > settled_round['settle_sequence']
                or not _integer(growth.get('growth_before'))
                or growth['growth_before'] >= settled_round['growth_count']
                or type(growth.get('growth_after')) is not int
                or growth['growth_after'] != growth['growth_before'] + 1
                or not _integer(growth.get('quoted_cost'), 1)
                or not _integer(growth.get('coins_before')) or not _integer(growth.get('coins_after'))
                or growth['coins_before'] - growth['coins_after'] != growth['quoted_cost']
                or growth.get('board_after_source') != 'decoded_response'
                or type(growth.get('auto_harvest')) is not bool):
            raise ValueError('growth_cost_or_response_unverified')
        before, after = _board(growth.get('board_before')), _board(growth.get('board_after'))
        steps = growth.get('growth_steps')
        if not isinstance(steps, (list, tuple)) or not 1 <= len(steps) <= 64:
            raise ValueError('growth_steps_incomplete')
        steps = tuple(_board(step) for step in steps)
        if steps[-1] != after or not any(c for c, _ in before):
            raise ValueError('growth_final_board_mismatch')
        actual_award = None
        if growth['auto_harvest']:
            actual_award = _award(growth.get('award'))
            if (actual_award != settled_round['award']
                    or growth['sequence'] != settled_round['settle_sequence']):
                raise ValueError('growth_auto_harvest_receipt_mismatch')
        return {
            'id': self._sample_id((settled_round['operation_id'], settled_round['round'], growth['sequence'])),
            'round_id': f"{settled_round['operation_id']}:r{settled_round['round']}",
            'source': settled_round['source'], 'operation_id': settled_round['operation_id'],
            'round': settled_round['round'], 'sequence': growth['sequence'],
            'mode': settled_round['mode'], 'auto_harvest': growth['auto_harvest'],
            'growth_count': growth['growth_before'], 'growth_cost': growth['quoted_cost'],
            'start_cost': settled_round['start_cost'], 'before': before, 'after': after,
            'steps': steps, 'actual_award': actual_award,
        }

    def _estimate(self, board):
        total = [0] * 4
        unknown = []
        for slot, (color, level) in enumerate(board, 1):
            if not color:
                continue
            quantity = self._payoff.get((color, level))
            if quantity is None:
                unknown.append({'slot': slot, 'color': color, 'level': level})
                total[color - 1] = None
            elif total[color - 1] is not None:
                total[color - 1] += quantity
        return tuple(total), unknown

    def _refresh(self):
        from .soul_chain_rules import analyze_chain

        self._provenance_cache = None
        observed = defaultdict(set)
        for receipt in self._rounds.values():
            for color, level, quantity in receipt['payoff_pairs']:
                observed[color, level].add(quantity)
        self._payoff = {}
        self._observed_payoff = {}
        self._shared_level_payoff = {}
        for pair, quantities in observed.items():
            if len(quantities) != 1:
                self._block('conflicting_verified_slot_payoffs', pair=list(pair), quantities=sorted(quantities))
            else:
                self._payoff[pair] = next(iter(quantities))
                self._observed_payoff[pair] = self._payoff[pair]
        if self.shared_level_quantities:
            by_level = defaultdict(set)
            for (color, level), quantities in observed.items():
                by_level[level].update(quantities)
            for level, quantities in by_level.items():
                if len(quantities) != 1:
                    self._block('shared_level_quantity_conflict', level=level, quantities=sorted(quantities))
                    continue
                quantity = next(iter(quantities))
                self._shared_level_payoff[level] = quantity
                for color in _COLORS:
                    self._payoff[color, level] = quantity
        self._samples = []
        for candidate in self._candidates.values():
            if candidate['mode'] != self.mode and not self.shared_game_rules:
                continue
            chain = analyze_chain(candidate['before'], candidate['steps'], payoff=self._payoff_nested())
            if chain.get('status') != 'VERIFIED':
                self._block('sample_chain_structure_unverified', sample_id=candidate['id'],
                            violations=chain.get('violations', []))
                continue
            before, unknown_before = self._estimate(candidate['before'])
            after, unknown_after = self._estimate(candidate['after'])
            if unknown_before or (unknown_after and candidate['actual_award'] is None):
                self._reject('sample_payoff_unknown', sample_id=candidate['id'])
                continue
            if candidate['actual_award'] is not None:
                after = candidate['actual_award']
            sample = {**candidate, 'chain': chain, 'before_award': before, 'after_award': after,
                      'delta': tuple(a - b for a, b in zip(after, before))}
            self._samples.append(sample)
        self._samples.sort(key=lambda sample: sample['id'])

    def _payoff_nested(self):
        result = {}
        for (color, level), quantity in self._payoff.items():
            result.setdefault(str(color), {})[str(level)] = quantity
        return result

    def _refresh_index(self):
        """Keep the explicit source index identity alongside model admission.

        Online mappings have no file path and stay represented by their receipt
        hash in sources; callers normally supply their durable ledger path.
        """
        from .soul_receipt_index import build_receipt_index

        self._provenance_cache = None
        self._index_summary = {}
        self._index_fingerprint = ''
        paths = sorted(source for source in self._sources if not source.startswith('online:'))
        try:
            index = build_receipt_index(paths, character_id=self.character_id)
            self._index_fingerprint = _fingerprint(index)
            self._index_summary = {key: deepcopy(index.get(key)) for key in
                                   ('schema', 'sources', 'counts', 'economic_groups', 'exclusions',
                                    'payoff_conflicts', 'structural_discrepancies')}
            admitted_rounds = {row['id'] for row in index['verified_rounds']}
            admitted_transitions = {row['id'] for row in index['transitions']}
            for row in self._rounds.values():
                identity = f"{row['operation_id']}:r{row['round']}"
                if not row['source'].startswith('online:') and identity not in admitted_rounds:
                    self._block('round_not_admitted_by_receipt_index', round_id=identity)
            for row in self._candidates.values():
                if not row['source'].startswith('online:') and row['id'] not in admitted_transitions:
                    self._block('transition_not_admitted_by_receipt_index', sample_id=row['id'])
        except (OSError, ValueError, TypeError, KeyError, AttributeError) as exc:
            self._block('receipt_index_unavailable', detail=str(exc))

    def observe_round_verified(self, ledger_or_path):
        """Add a full COMPLETED/VERIFIED_CLOSED raw ledger; do not persist it.

        The caller must durably save/verify its fresh receipts first.  There is
        deliberately no API that learns from an unclosed round or growth intent.
        A conflict disables this model; reload corrected evidence to recover.
        """
        before_rounds, before_samples = len(self._rounds), len(self._samples)
        try:
            if isinstance(ledger_or_path, Mapping):
                ledger = deepcopy(ledger_or_path)
                source = 'online:' + str(ledger.get('operation_id', 'unbound'))
                digest = _fingerprint(ledger)
            else:
                path = Path(ledger_or_path).resolve()
                raw = path.read_bytes()
                ledger, source, digest = json.loads(raw), str(path), sha256(raw).hexdigest()
            self._ingest_ledger(ledger, source)
            self._sources[source] = digest
            self._refresh()
            self._refresh_index()
            self._online_updates += 1
            self._provenance_cache = None
        except (OSError, ValueError, TypeError, KeyError, AttributeError) as exc:
            self._block('online_receipt_invalid', detail=str(exc))
        return {'ready': self.ready, 'added_rounds': len(self._rounds) - before_rounds,
                'added_samples': len(self._samples) - before_samples,
                'blocking_issues': self.blocking_issues}

    def _provenance(self):
        if self._provenance_cache is None:
            self._provenance_cache = self._build_provenance()
        # Decisions receive detached metadata. The full immutable source ledgers
        # are referenced by path/hash instead of being copied into each action.
        return deepcopy(self._provenance_cache)

    def _build_provenance(self):
        return {
            'version': _VERSION, 'character_id': self.character_id, 'mode': self.mode,
            'provenance_schema': 'soul-joint-provenance-compact-v1',
            'diagnostic_trace_schema': 'soul-joint-projection-trace-v2',
            'diagnostic_profile': 'referenced_sources_and_indexed_trace_v1',
            'forecast_engine': 'first-stage-joint-chain-v8-budget-precheck-h2',
            'forecast_semantics': 'weighted_partial_identification_bounds_v1',
            'decision_value_semantics': 'conservative_joint_utility_lower_bound_not_point_expectation',
            'possibility_limits': {'first_phase_completions_per_source': _FIRST_PHASE_COMPLETION_LIMIT,
                                   'first_phase_trace_per_source': _FIRST_PHASE_TRACE_LIMIT,
                                   'outcome_trace_per_source': _OUTCOME_TRACE_LIMIT},
            'mechanics_adapter': 'shared-resource-joint-pattern-v1' if self.shared_game_rules else 'same-entry-v1',
            'admission_semantics': ('shared_rules_growth_stage_and_mechanical_neighborhood_v1'
                if self.shared_game_rules else 'same_entry_reward_envelope_and_neighborhood_v1'),
            'per_color_reward_envelope': ('diagnostic_only' if self.shared_game_rules else 'admission_gate'),
            'distance_quantity_semantics': ('color_blind_level_quantity_preserving_pure_color_scale_v1'
                if self.shared_game_rules and self.shared_level_quantities else 'per_color_raw_quantity_gap_v1'),
            'shared_probability_transfer': 'user_confirmed_shared_rules_model_assumption_not_server_probability_proof',
            'target_color': self.mode,
            'objective': 'finite_budget_harvest_utility',
            'opportunity_semantics': 'finite_open_then_harvest_reference_difference_or_explicit_historical_ratio_approximation',
            'opening_reference': {
                'policy': 'authorized_open_then_immediate_harvest_v1',
                'initial_payoff': 'observed_nonadopted_same_character_entry_and_paid_start_quote_boards',
                'future_growth_included': False, 'global_optimality_proven': False,
                'unspent_money_terminal_utility': 0,
                'mixed_entry_allocation': 'not_modeled',
            },
            'utility_version': _UTILITY_VERSION,
            'utility_weights_by_color': {str(color): weight for color, weight in _UTILITY_WEIGHTS.items()},
            'utility_authority': 'explicit_user_blue_1_purple_5_orange_10',
            'utility_is_goal_quantity': False,
            'higher_color_guard': 'unpriced_color_loss_only',
            'unpriced_colors': [color for color in _COLORS if color not in _UTILITY_WEIGHTS],
            'continuation': {'maximum_paid_request_horizon': 2,
                             'maximum_expanded_states': _CONTINUATION_NODES,
                             'computation_budget_seconds': _CONTINUATION_SECONDS,
                             'unexpanded_successor_lower_action': 'immediate_harvest'},
            'dataset_path': self._dataset_path, 'dataset_sha256': self._dataset_hash,
            'additional_ledgers': list(self._additional_paths),
            'sources': [{'path': path, 'sha256': digest} for path, digest in sorted(self._sources.items())],
            'verified_rounds': len(self._rounds), 'verified_growth_requests': len(self._samples),
            'verified_mode_rounds': sum(row['mode'] == self.mode for row in self._rounds.values()),
            'receipt_index': {
                'schema': self._index_summary.get('schema'),
                'sha256': self._index_fingerprint,
                'hash_meaning': 'canonical_full_index_from_sorted_explicit_source_paths',
                'counts': deepcopy(self._index_summary.get('counts', {})),
                'full_payload_inline': False,
                'source_identity_field': 'model_provenance.sources',
                'evidence_authority': 'immutable_closed_raw_source_ledgers',
            },
            'payoff_scope': 'same_character_shared_color_level_payoff_across_entry_modes',
            'shared_game_rules': self.shared_game_rules,
            'shared_level_quantities': self.shared_level_quantities,
            'shared_rule_authority': 'explicit_user_confirmation_in_manifest' if (
                self.shared_game_rules or self.shared_level_quantities) else None,
            'observed_payoff_by_color_level': {
                f'{c}:{level}': quantity for (c, level), quantity in sorted(self._observed_payoff.items())},
            'shared_level_quantity_mapping': {
                str(level): quantity for level, quantity in sorted(self._shared_level_payoff.items())},
            'shared_payoff_mapping_meaning': 'Derived from observed same-character level quantities and user-confirmed color equivalence; not additional observed color receipts.',
            'source_request_counts_by_mode': {str(mode): sum(row['mode'] == mode for row in self._samples)
                                              for mode in _MODE_COLORS},
            'source_negative_quantity_requests_by_mode': {
                str(mode): sum(row['mode'] == mode and sum(row['delta']) < 0 for row in self._samples)
                for mode in _MODE_COLORS},
            'payoff_evidence_reference': {
                'full_payload_inline': False,
                'authority': 'verified_harvest_slot_awards_in_raw_source_ledgers',
                'source_identity_field': 'model_provenance.sources',
                'observed_round_count': sum(bool(row['payoff_pairs']) for row in self._rounds.values()),
                'observed_slot_pair_count': sum(len(row['payoff_pairs']) for row in self._rounds.values()),
            },
            'inline_online_payoff_evidence': [
                {'round_id': f"{row['operation_id']}:r{row['round']}", 'mode': row['mode'],
                 'source': row['source'], 'pairs': [list(pair) for pair in row['payoff_pairs']]}
                for row in sorted(self._rounds.values(), key=lambda row: (row['operation_id'], row['round']))
                if row['payoff_pairs'] and row['source'].startswith('online:')],
            'payoff_by_color_level': {f'{c}:{level}': quantity for (c, level), quantity in sorted(self._payoff.items())},
            'online_updates': self._online_updates, 'blocking_issues': self.blocking_issues,
            'rejected_evidence': deepcopy(self._rejected),
            'selection_bias': 'Observed actions were selected by previous policies; unchosen branches have no outcomes.',
            'primary_transition_assumption': {
                'version': 'primary-overflow-v1', 'status': 'evidence_backed_candidate_not_server_proven',
                'known_same_color_increment_support': [0, 5], 'overflow_threshold': 10,
                'overflow_result_level': 1,
                'censored_reset_transfer': 'enumerate max(0,11-source_level)..5 as possibilities within the original joint sample; no completion probabilities',
            },
            'server_transition_probabilities': 'unknown',
        }

    def _features(self, board, award):
        increments = []
        for color, level in board:
            if not color or level == 10:
                increments.append(0)
            else:
                next_value = self._payoff.get((color, level + 1))
                value = self._payoff.get((color, level))
                increments.append(None if next_value is None or value is None else next_value - value)
        return {
            'ordered_board': [list(slot) for slot in board],
            'level_counts': [sum(level == candidate for _, level in board) for candidate in range(11)],
            'one_level_award_increments': increments,
            'one_level_award_increment_meaning': 'Valuation of +1 level only; not an expected transition or probability.',
            'current_award_by_color': _by_color(award),
        }

    def _distance(self, board, award, growth_count, sample):
        reference = self._reference_board(board, sample)
        reference_award, unknown = self._estimate(reference)
        if unknown:
            return None
        ordered = sum(abs(a[1] - b[1]) for a, b in zip(board, reference)) / 80
        distribution = sum(abs(a - b) for a, b in zip(
            sorted(level for _, level in board), sorted(level for _, level in reference))) / 80
        if self.shared_game_rules and self.shared_level_quantities:
            # Admission describes growth mechanics, not the color allocation
            # or user prices. Keep the original pure-color scale and weight.
            value = abs(sum(award) - sum(reference_award)) / max(
                1, 8 * max(self._shared_level_payoff.values(), default=0)) / 4
        else:
            value = sum(abs(award[color - 1] - reference_award[color - 1]) /
                        max(1, 8 * max((quantity for (c, _), quantity in self._payoff.items()
                                        if c == color), default=0)) for color in _COLORS) / 4
        current_increments = self._features(board, award)['one_level_award_increments']
        reference_increments = self._features(reference, reference_award)['one_level_award_increments']
        if any(x is None for x in current_increments + reference_increments):
            return None
        scale = max(1, max(abs(x) for x in current_increments + reference_increments))
        increments = sum(abs(a - b) for a, b in zip(current_increments, reference_increments)) / (8 * scale)
        gap = abs(growth_count - sample['growth_count'])
        parts = {'ordered_levels': ordered, 'level_distribution': distribution,
                 'harvest_value': value, 'one_level_award_increments': increments,
                 'growth_count': gap / (1 + gap)}
        return sum(parts[key] * weight for key, weight in _DISTANCE_WEIGHTS.items()), parts

    def _color_context_matches(self, board, sample):
        if self.shared_game_rules:
            return all(bool(current[0]) == bool(old[0]) for current, old in zip(board, sample['before']))
        return tuple(color for color, _ in board) == tuple(color for color, _ in sample['before'])

    def _reference_board(self, board, sample):
        if not self.shared_game_rules:
            return sample['before']
        return tuple((current[0], old[1]) for current, old in zip(board, sample['before']))

    def _cost_context_matches(self, sample, growth_cost, start_cost):
        return sample['growth_cost'] == growth_cost and (self.shared_game_rules or sample['start_cost'] == start_cost)

    def _project_request(self, board, sample, *, successor_value=None, deadline=None, trace=True):
        """Transfer one observed eight-slot first phase as a single sample.

        Under primary-overflow-v1 a known 0..5 increment resets above 10.
        A source reset censors that increment to max(0,11-source_level)..5.
        Enumerate an outer joint support consistent with that one complete
        observed pattern. Unknown latent correlations may exclude possibilities;
        retaining them makes bounds conservative. Completions and secondary
        victims receive no probabilities or additional source weight.
        """
        from .soul_chain_rules import decompose_first_step

        slot_options = []
        censored_increment_evidence = []
        for slot, (current, old, new) in enumerate(zip(board, sample['before'], sample['steps'][0]), 1):
            if current[0] != old[0] and not self.shared_game_rules:
                return {'status': 'UNKNOWN', 'reason': 'ordered_color_source_uncovered', 'slot': slot}
            if bool(current[0]) != bool(old[0]):
                return {'status': 'UNKNOWN', 'reason': 'occupied_slot_source_uncovered', 'slot': slot}
            if old[0] != new[0] and self.shared_game_rules:
                return {'status': 'UNKNOWN', 'reason': 'source_color_change_uncovered', 'slot': slot}
            if old[0] != new[0] or not old[0]:
                if current != old:
                    return {'status': 'UNKNOWN', 'reason': 'color_change_source_uncovered', 'slot': slot}
                slot_options.append((new,))
                continue
            if old[1] > 1 and new[1] == 1:
                minimum_increment = max(0, 11 - old[1])
                possible_levels = sorted({1 if current[1] + increment > 10 else current[1] + increment
                                          for increment in range(minimum_increment, 6)})
                evidence = {'slot': slot, 'source_before_level': old[1],
                            'current_before_level': current[1],
                            'admissible_increment_range': [minimum_increment, 5],
                            'possible_current_first_levels': possible_levels}
                censored_increment_evidence.append(evidence)
                if not possible_levels:
                    return {'status': 'UNKNOWN', 'reason': 'censored_reset_increment_uncovered',
                            'slot': slot, 'censored_increment_evidence': censored_increment_evidence}
                slot_options.append(tuple((current[0], level) for level in possible_levels))
                continue
            increment = new[1] - old[1]
            level = current[1] + increment
            if not 0 <= increment <= 5:
                return {'status': 'UNKNOWN', 'reason': 'primary_increment_outside_observed_support', 'slot': slot}
            slot_options.append(((current[0], 1 if level > 10 else level),))
        completion_count = 1
        for options in slot_options:
            completion_count *= len(options)
        if completion_count > _FIRST_PHASE_COMPLETION_LIMIT:
            return {'status': 'UNKNOWN', 'reason': 'first_phase_possibility_cap_exceeded',
                    'first_phase_completion_count': completion_count,
                    'first_phase_completion_limit': _FIRST_PHASE_COMPLETION_LIMIT,
                    'censored_increment_evidence': censored_increment_evidence}
        lower, upper = [None] * 4, [None] * 4
        utility_lower, utility_upper = None, None
        utility_witnesses = {}
        current_award, _ = self._estimate(board)
        unpriced_losses, unpriced_changes = set(), set()
        continuation_lower, continuation_upper = None, None
        continuation_policy_upper = None
        continuation_upper_unknown = False
        first_lower, first_upper = [None] * 4, [None] * 4
        first_unknown_colors = set()
        witnesses = {str(color): {} for color in _COLORS}
        first_traces, outcomes = [], []
        outcome_count, award_vectors = 0, set()
        sole_first, sole_chain = None, None
        for completion_index, first in enumerate(product(*slot_options), 1):
            if deadline is not None and perf_counter() >= deadline:
                return {'status': 'UNKNOWN', 'reason': 'computation_budget_exhausted'}
            structural = decompose_first_step(board, first)
            alternatives = structural.get('possible_final_boards', [])
            if structural.get('status') != 'VERIFIED' or not alternatives:
                return {'status': 'UNKNOWN', 'reason': 'first_step_structure_uncovered',
                        'completion_index': completion_index, 'chain': structural}
            first_award, unknown_first = self._estimate(first)
            for c in range(4):
                if first_award[c] is None:
                    first_unknown_colors.add(c)
                else:
                    first_lower[c] = first_award[c] if first_lower[c] is None else min(first_lower[c], first_award[c])
                    first_upper[c] = first_award[c] if first_upper[c] is None else max(first_upper[c], first_award[c])
            if completion_count == 1:
                sole_first, sole_chain = first, structural
            if trace and len(first_traces) < _FIRST_PHASE_TRACE_LIMIT:
                first_traces.append({'completion_index': completion_index,
                    'first_step_board': [list(slot) for slot in first],
                    'first_step_award_by_color': _by_color(first_award),
                    'first_step_unknown_payoff_slots': unknown_first,
                    'first_reset_slots': structural['first_reset_slots'], 'k': structural['k'],
                    'tentative_board': structural['tentative_board'],
                    'overflow_slots': structural['overflow_slots'],
                    'structural_victim_count': len(alternatives)})
            for candidate in alternatives:
                if deadline is not None and perf_counter() >= deadline:
                    return {'status': 'UNKNOWN', 'reason': 'computation_budget_exhausted'}
                final = _board(candidate['board'])
                award, unknown = self._estimate(final)
                if unknown:
                    return {'status': 'UNKNOWN', 'reason': 'projected_payoff_uncovered',
                            'completion_index': completion_index,
                            'unknown_payoff_slots': unknown, 'chain': structural}
                outcome = {'completion_index': completion_index,
                           'first_step_board': [list(slot) for slot in first],
                           'victim_slot': candidate['victim_slot'],
                           'board': [list(slot) for slot in final], 'award_by_color': _by_color(award)}
                value = _utility(award)
                if utility_lower is None or value < utility_lower:
                    utility_lower, utility_witnesses['lower'] = value, outcome
                if utility_upper is None or value > utility_upper:
                    utility_upper, utility_witnesses['upper'] = value, outcome
                for color in _COLORS:
                    if color not in _UTILITY_WEIGHTS and award[color - 1] != current_award[color - 1]:
                        unpriced_changes.add(color)
                        if award[color - 1] < current_award[color - 1]:
                            unpriced_losses.add(color)
                if successor_value is not None:
                    continuation = successor_value(final, award)
                    value_lower, value_upper = continuation['lower'], continuation['upper']
                    continuation_lower = (value_lower if continuation_lower is None
                                          else min(continuation_lower, value_lower))
                    continuation_policy_upper = (value_lower if continuation_policy_upper is None
                                                 else max(continuation_policy_upper, value_lower))
                    if value_upper is None:
                        continuation_upper_unknown = True
                    else:
                        continuation_upper = (value_upper if continuation_upper is None
                                              else max(continuation_upper, value_upper))
                outcome_count += 1
                award_vectors.add(award)
                if trace and len(outcomes) < _OUTCOME_TRACE_LIMIT:
                    outcomes.append(outcome)
                for c in range(4):
                    if lower[c] is None or award[c] < lower[c]:
                        lower[c] = award[c]
                        witnesses[str(c + 1)]['lower'] = outcome
                    if upper[c] is None or award[c] > upper[c]:
                        upper[c] = award[c]
                        witnesses[str(c + 1)]['upper'] = outcome
        lower, upper = tuple(lower), tuple(upper)
        expected = tuple(a if a == b else None for a, b in zip(lower, upper))
        forecast = {
            'status': 'KNOWN' if all(x is not None for x in expected) else 'BOUNDED',
            'reason': '' if all(x is not None for x in expected) else 'censored_roll_or_victim_distribution_unidentified',
            'expected_award': expected, 'lower_award': lower, 'upper_award': upper,
            'utility_bounds': {'lower': utility_lower, 'upper': utility_upper},
            'expected_utility': utility_lower if utility_lower == utility_upper else None,
            'unpriced_loss_colors': sorted(unpriced_losses),
            'unpriced_change_colors': sorted(unpriced_changes),
            'continuation_utility_bounds': {
                'lower': continuation_lower,
                'upper': None if continuation_upper_unknown else continuation_upper},
            'continuation_lower_policy_value_bounds': {
                'lower': continuation_lower, 'upper': continuation_policy_upper},
        }
        if not trace:
            return forecast
        first_expected = tuple(first_lower[c] if c not in first_unknown_colors and first_lower[c] == first_upper[c]
                               else None for c in range(4))
        # Trace formatting is separate from the complete bounds calculation
        # above. Intern boards/outcomes once; witnesses are indices, not eight
        # additional copies of the same outcome in the common point case.
        board_table, board_indices = [], {}

        def board_ref(value):
            if value is None:
                return None
            key = tuple(tuple(slot) for slot in value)
            if key not in board_indices:
                board_indices[key] = len(board_table)
                board_table.append([list(slot) for slot in key])
            return board_indices[key]

        compact_first = []
        for row in first_traces:
            compact_first.append({
                **{key: value for key, value in row.items() if key not in ('first_step_board', 'tentative_board')},
                'first_step_board_ref': board_ref(row['first_step_board']),
                'tentative_board_ref': board_ref(row['tentative_board'])})
        compact_outcomes, outcome_indices = [], {}

        def outcome_ref(row):
            key = (row['completion_index'], row['victim_slot'])
            if key not in outcome_indices:
                outcome_indices[key] = len(compact_outcomes)
                compact_outcomes.append({
                    'completion_index': row['completion_index'], 'victim_slot': row['victim_slot'],
                    'first_step_board_ref': board_ref(row['first_step_board']),
                    'board_ref': board_ref(row['board']), 'award_by_color': row['award_by_color']})
            return outcome_indices[key]

        for row in outcomes:
            outcome_ref(row)
        witness_refs = {color: {side: outcome_ref(row) for side, row in by_side.items()}
                        for color, by_side in witnesses.items()}
        utility_witness_refs = {side: outcome_ref(row) for side, row in utility_witnesses.items()}
        compact_chain = None
        if sole_chain is not None:
            compact_chain = {
                'status': sole_chain['status'], 'first_reset_slots': sole_chain['first_reset_slots'],
                'k': sole_chain['k'], 'overflow_slots': sole_chain['overflow_slots'],
                'tentative_board_ref': board_ref(sole_chain['tentative_board']),
                'possible_final_boards': [{'victim_slot': row['victim_slot'], 'board_ref': board_ref(row['board'])}
                                          for row in sole_chain['possible_final_boards']],
                'violations': sole_chain['violations']}
        sole_first_ref = board_ref(sole_first)
        return {**forecast,
                'trace_schema': 'soul-joint-projection-trace-v2',
                'trace_storage': 'zero_based_board_and_outcome_indices',
                'board_table': board_table, 'first_step_board_ref': sole_first_ref,
                'primary_transition_assumption': 'primary-overflow-v1',
                'completion_semantics': 'outer_joint_support_of_one_observed_pattern_not_independent_probabilities',
                'censored_increment_evidence': censored_increment_evidence,
                'first_step_award_by_color': _by_color(first_expected),
                'first_step_award_bounds_by_color': {str(c + 1): {
                    'lower': first_lower[c] if c not in first_unknown_colors else None,
                    'upper': first_upper[c] if c not in first_unknown_colors else None} for c in range(4)},
                'chain': compact_chain, 'first_phase_completions': compact_first,
                'first_phase_completion_count': completion_count,
                'first_phase_trace_truncated': completion_count > len(compact_first),
                'outcomes': compact_outcomes, 'outcome_count': outcome_count,
                'chronological_outcome_trace_count': len(outcomes),
                'additional_bound_witness_outcomes': len(compact_outcomes) - len(outcomes),
                'outcome_trace_truncated': outcome_count > len(compact_outcomes),
                'distinct_award_vector_count': len(award_vectors),
                'award_bounds_witness_refs_by_color': witness_refs,
                'utility_bounds_witness_refs': utility_witness_refs}

    def _opening_reference_opportunity(self, economic_context, budget, growth_cost, reference_cache=None):
        reference = opening_reference_counts(economic_context, self.mode, budget, growth_cost)
        if reference['status'] == 'zero':
            return (0.0,) * 4, {
                **reference, 'kind': 'finite_budget_reference_zero_difference',
                'initial_board_value_required': False,
                'meaning': 'Reference value difference is zero; future growth value is outside this policy.',
            }
        if reference['status'] != 'initial_board_reference_required':
            return None, reference
        key = (self.mode, reference['paid_start_quote'])
        cached = None if reference_cache is None else reference_cache.get(key)
        if cached is None:
            cached = self._paid_initial_board_reference(reference['paid_start_quote'])
            if reference_cache is not None:
                reference_cache[key] = cached
        mean, evidence = cached
        if reference_cache is None:
            reference.update(evidence)
        else:
            reference.update({
                'initial_board_reference_evidence_id': evidence['evidence_id'],
                'initial_board_evidence_location': 'continuation_prediction.opening_reference_evidence',
                'initial_board_round_count': evidence['initial_board_round_count'],
            })
        if mean is None:
            reference.update(status='fallback', reason=evidence['reason'])
            return None, reference
        lost = reference['lost_paid_starts']
        reference.update({
            'status': 'supported_reference', 'kind': 'finite_budget_initial_board_reference',
            'mean_initial_award_by_color': _by_color(mean), 'mean_initial_utility': _utility(mean),
            'N_reference_paid_component_before': reference['affordable_paid_starts_before'] * _utility(mean),
            'N_reference_paid_component_after': reference['affordable_paid_starts_after'] * _utility(mean),
            'free_start_component': 'constant_in_budget_cancels_without_assuming_free_board_payoff',
            'meaning': 'Empirical open-then-harvest reference difference, not optimal future value or historical final-round reward.',
        })
        return tuple(lost * value for value in mean), reference

    def _paid_initial_board_reference(self, paid_start_quote):
        """One evidence summary per entry/quote, shared by one assessment's children."""
        rows = sorted((row for row in self._rounds.values()
                       if row['mode'] == self.mode and not row['adopted_board']
                       and row['start_cost'] == paid_start_quote),
                      key=lambda row: (row['operation_id'], row['round']))
        evidence = {
            'evidence_id': f'paid-initial-boards:mode-{self.mode}:quote-{paid_start_quote}',
            'entry_mode': self.mode, 'paid_start_quote': paid_start_quote,
            'initial_board_evidence_field': 'raw_source_ledger.rounds[].board_before_growth',
            'initial_board_sample_scope': 'same_character_same_entry_same_paid_start_quote_nonadopted_closed_rounds',
            'minimum_initial_board_rounds': _MIN_ROUNDS,
            'initial_board_round_ids': [f"{row['operation_id']}:r{row['round']}" for row in rows],
            'initial_board_round_count': len(rows),
            'initial_board_source_paths': sorted({row['source'] for row in rows}),
            'initial_board_sampling_assumption': 'observed_starts_represent_future_same_entry_paid_starts_not_server_probability_proof',
            'selection_bias': 'only_verified_closed_rounds_are_admitted_missing_or_unknown_starts_are_not_imputed',
            'payoff_authority': 'receipt_backed_model_payoff_mapping_in_model_provenance',
            'source_identity_field': 'model_provenance.sources',
        }
        if len(rows) < _MIN_ROUNDS:
            evidence.update(status='fallback', reason='paid_initial_board_reference_sparse')
            return None, evidence
        values, unavailable = [], []
        for row in rows:
            award, unknown = self._estimate(row['initial_board']) if row['initial_board'] is not None else (None, True)
            if unknown or any(award[color - 1] for color in _COLORS if color not in _UTILITY_WEIGHTS):
                unavailable.append(f"{row['operation_id']}:r{row['round']}")
            else:
                values.append(award)
        if unavailable:
            evidence.update(status='fallback', reason='paid_initial_board_reference_unvalued',
                            unavailable_initial_board_round_ids=unavailable)
            return None, evidence
        mean = tuple(sum(award[color - 1] for award in values) / len(values) for color in _COLORS)
        evidence.update(status='supported_reference', mean_initial_award_by_color=_by_color(mean),
                        mean_initial_utility=_utility(mean))
        return mean, evidence

    def assess(self, board, growth_count, growth_cost, start_cost, budget_remaining=None,
               remaining_growths=None, *, economic_context=None):
        """Compare harvest with at most two paid requests and fresh-state stopping.

        The second request is a forecast, not an action commitment. Unknown or
        unexpanded successors retain their feasible immediate-harvest value.
        Each real request still needs the caller's fresh quote and admission.
        """
        started = perf_counter()
        horizon = (2 if (remaining_growths is None or _integer(remaining_growths, 2))
                   and _integer(growth_cost, 1)
                   and (budget_remaining is None or _integer(budget_remaining, 2 * growth_cost)) else 1)
        context = {'deadline': started + _CONTINUATION_SECONDS, 'projected_child_states': 0,
                   'candidate_states': 0, 'admission_checks': 0, 'cache_hits': 0,
                   'cache': {}, 'fallback_reasons': defaultdict(int), 'child_reasons': defaultdict(int),
                   'limits_reached': set(), 'evidence': [], 'child_seconds': 0.0,
                   'economic_reference_cache': {}}

        def successor_value(next_board, next_award):
            harvest = _utility(next_award)
            context['candidate_states'] += 1
            cached = context['cache'].get(next_board)
            if cached is not None:
                context['cache_hits'] += 1
                return cached
            context['admission_checks'] += 1
            occupied = [(color, level) for color, level in next_board if color]
            if occupied and all(level == 10 for _, level in occupied):
                # This is a hard execution constraint, not a coverage failure:
                # no further paid growth is legal, so harvest is the exact value.
                reason = 'soul_board_all_max_level'
                value = {'lower': harvest, 'upper': harvest}
                context['cache'][next_board] = value
                context['child_reasons'][reason] += 1
                if len(context['evidence']) < 8:
                    context['evidence'].append({'board': [list(slot) for slot in next_board],
                        'action': 'harvest', 'reason': reason, 'harvest_utility': harvest,
                        'net_continuation_credit_lower': 0.0, 'opportunity_cost_utility': None,
                        'budget_remaining': None if budget_remaining is None else budget_remaining - growth_cost,
                        'opportunity_baseline': None, 'conservative_delta_utility': None,
                        'sample_count': 0, 'effective_rounds': 0.0})
                return value
            if perf_counter() >= context['deadline']:
                context['limits_reached'].add('time_budget')
                context['fallback_reasons']['time_budget'] += 1
                return {'lower': harvest, 'upper': None}

            def admit_projection():
                if perf_counter() >= context['deadline']:
                    return 'computation_budget_exhausted'
                if context['projected_child_states'] >= _CONTINUATION_NODES:
                    return 'state_budget_exhausted'
                # Count a child state once when projection starts, even if one
                # of its source projections later fails or times out.
                context['projected_child_states'] += 1
                return None

            child_started = perf_counter()
            child = self._assess_one(next_board, growth_count + 1, growth_cost, start_cost,
                budget_remaining=None if budget_remaining is None else budget_remaining - growth_cost,
                economic_context=economic_context, economic_reference_cache=context['economic_reference_cache'],
                deadline=context['deadline'], trace=False, admit_projection=admit_projection)
            context['child_seconds'] += perf_counter() - child_started
            reason = child['reason']
            context['child_reasons'][reason] += 1
            opportunity = child.get('opportunity_cost_utility')
            conservative = child.get('conservative_delta_utility')
            # An unsupported estimate never adds continuation credit. Harvest
            # remains an available action even when the model cannot value grow.
            credit = (max(0.0, conservative - opportunity) if child['action'] == 'grow' else 0.0)
            comparison = child.get('utility_comparison') or {}
            optimistic = (comparison.get('weighted_delta_bounds') or {}).get('upper')
            supported = child.get('forecast_coverage_supported') is True
            upper = (harvest + max(0.0, optimistic - opportunity)
                     if supported and optimistic is not None and opportunity is not None
                     and not comparison.get('unpriced_color_changes') else None)
            value = {'lower': harvest + credit, 'upper': upper}
            truncation = {'soul_joint_computation_budget_exhausted': 'time_budget',
                          'soul_joint_state_budget_exhausted': 'state_budget'}.get(reason)
            if truncation:
                context['limits_reached'].add(truncation)
                context['fallback_reasons'][truncation] += 1
            else:
                context['cache'][next_board] = value
            if not supported and not truncation:
                context['fallback_reasons'][reason] += 1
            if len(context['evidence']) < 8:
                context['evidence'].append({'board': [list(slot) for slot in next_board],
                    'action': child['action'], 'reason': reason, 'harvest_utility': harvest,
                    'net_continuation_credit_lower': credit, 'opportunity_cost_utility': opportunity,
                    'budget_remaining': None if budget_remaining is None else budget_remaining - growth_cost,
                    'opportunity_baseline': child.get('opportunity_baseline'),
                    'conservative_delta_utility': conservative,
                    'sample_count': child['sample_count'], 'effective_rounds': child['effective_samples']})
            return value

        result = self._assess_one(board, growth_count, growth_cost, start_cost,
            budget_remaining=budget_remaining, economic_context=economic_context,
            economic_reference_cache=context['economic_reference_cache'],
            successor_value=successor_value if horizon == 2 else None)
        result['continuation_prediction'] = {
            'method': 'bounded_horizon_bellman_lower_value', 'paid_request_horizon': horizon,
            'remaining_growth_execution_cap': remaining_growths,
            'execution_cap_is_economic_optimum': False,
            'second_request_quote_assumption': 'same_verified_growth_quote_recheck_before_execution',
            'child_economic_budget': 'root_remaining_budget_minus_current_growth_once',
            'future_start_counts': 'unchanged_until_current_round_is_harvested',
            'state_assumption': 'board_and_growth_count_condition_empirical_kernel_not_server_proven',
            'within_source_unknowns': 'minimum_successor_value_no_victim_or_censored_roll_probabilities',
            'unexpanded_successor_lower_action': 'immediate_harvest',
            'unknown_successor_upper_value': 'unknown',
            'candidate_states': context['candidate_states'],
            'admission_checks': context['admission_checks'],
            'cache_hits': context['cache_hits'],
            'projected_child_states': context['projected_child_states'],
            'expanded_states': context['projected_child_states'],
            'counter_semantics': {
                'candidate_states': 'successor_callback_calls_including_repeats_and_cache_hits',
                'admission_checks': 'cache_miss_successor_calls_checked_for_constraints_or_admission_including_truncations',
                'cache_hits': 'successor_calls_returned_from_per_assessment_board_value_cache',
                'projected_child_states': 'child_states_entering_source_projection_including_later_projection_failures',
                'expanded_states': 'legacy_alias_for_projected_child_states',
            },
            'state_limit': _CONTINUATION_NODES,
            'computation_budget_seconds': _CONTINUATION_SECONDS,
            'budget_scope': ('state_limit_applies_to_projected_child_states_only; deadline_starts_at_assess_entry_and_bounds_'
                             'uncached_child_admission_and_projection_except_cheap_all_max_constraint; '
                             'cache_hits_remain_available; root_projection_retains_all_source_weights'),
            'continuation_elapsed_seconds': context['child_seconds'],
            'limits_reached': sorted(context['limits_reached']),
            'fallback_reasons': dict(context['fallback_reasons']),
            'child_reasons': dict(context['child_reasons']),
            'child_evidence': context['evidence'], 'child_evidence_limit': 8,
            'opening_reference_evidence': [value[1] for _, value in sorted(context['economic_reference_cache'].items())],
            'opening_reference_evidence_scope': 'shared_once_per_entry_quote_within_this_assessment',
            'assessment_elapsed_seconds': perf_counter() - started,
            'global_optimality_proven': False,
        }
        return result

    def _assess_one(self, board, growth_count, growth_cost, start_cost, budget_remaining=None,
                    *, economic_context=None, economic_reference_cache=None,
                    successor_value=None, deadline=None, trace=True, admit_projection=None):
        """Return a JSON-safe ``grow``/``harvest`` recommendation and its evidence.

        Prefer the finite authorized open-then-harvest reference difference.
        Missing scope or evidence retains the historical whole-round ratio as
        an explicit approximation. The already-paid current start stays sunk.
        """
        result = {
            'schema': 1, 'action': 'harvest', 'reason': 'soul_joint_model_unavailable',
            'forecast_semantics': 'weighted_partial_identification_bounds_v1',
            'decision_value_semantics': 'conservative_joint_utility_lower_bound_not_point_expectation',
            'target_color': self.mode, 'entry_mode': self.mode,
            'valuation_policy': 'user_weighted_mixed_board_utility',
            'objective': 'finite_budget_harvest_utility',
            'execution_economic_context': deepcopy(economic_context) if trace else None,
            'utility_version': _UTILITY_VERSION,
            'utility_weights_by_color': {str(color): weight for color, weight in _UTILITY_WEIGHTS.items()},
            'utility_is_goal_quantity': False,
            'current_estimated_utility': None, 'expected_delta_utility': None,
            'expected_delta_utility_bounds': {'lower': None, 'upper': None},
            'conservative_delta_utility': None, 'opportunity_cost_utility': None,
            'exploration_eligible': False,
            'current_estimated_award_by_color': {str(c): None for c in _COLORS},
            'expected_delta_by_color': {str(c): None for c in _COLORS},
            'expected_award_after_by_color': {str(c): None for c in _COLORS},
            'expected_delta_bounds_by_color': {str(c): {'lower': None, 'upper': None} for c in _COLORS},
            'opportunity_cost_target': None, 'opportunity_cost_by_color': {str(c): None for c in _COLORS},
            'opportunity_cost_purple': None, 'empirical_loss_fraction': None,
            'sample_count': 0, 'effective_samples': 0.0, 'coverage_distance': None,
            'model_provenance': self._provenance() if trace else None, 'samples': [],
            'forecast_coverage_supported': False,
            'mechanical_supported': False, 'per_color_envelope_supported': None,
            'admission_semantics': ('shared_rules_growth_stage_and_mechanical_neighborhood_v1'
                if self.shared_game_rules else 'same_entry_reward_envelope_and_neighborhood_v1'),
            'distance_quantity_semantics': ('color_blind_level_quantity_preserving_pure_color_scale_v1'
                if self.shared_game_rules and self.shared_level_quantities else 'per_color_raw_quantity_gap_v1'),
            'growth_count': growth_count if _integer(growth_count) else None,
            'growth_cost': growth_cost if _integer(growth_cost) else None,
            'start_cost': start_cost if _integer(start_cost) else None,
            'uncertainty': {
                'kind': 'empirical_engineering_reserve_not_statistical_confidence',
                'unknowns': ['server joint transition rule', 'unchosen action outcomes',
                             'victim selection distribution where candidate payoff vectors differ',
                             'whether board and growth count summarize all relevant history',
                             'future loss probability, including losses absent from observed samples'],
                'distance_weights': dict(_DISTANCE_WEIGHTS),
                'coverage_limits': {'radius': _RADIUS, 'nearest_distance': _NEAREST_LIMIT,
                                    'minimum_distinct_rounds': _MIN_ROUNDS,
                                    'minimum_effective_rounds': _MIN_EFFECTIVE_ROUNDS},
            },
        }

        def stop(reason):
            result['reason'] = 'soul_joint_' + reason
            result['decision_status'] = ('model_bound_favors_harvest'
                if reason == 'utility_upper_bound_below_opportunity' else
                'constraint_harvest' if reason in ('budget_cannot_fund_next_growth',
                    'unpriced_color_loss_boundary') else 'uncertain_harvest_fallback')
            result['exploration_eligible'] = (
                reason in ('award_outside_observed_support', 'ordered_color_context_uncovered',
                           'board_neighborhood_uncovered', 'nearest_board_too_distant',
                           'independent_round_support_sparse')
                and self.ready and result['opportunity_cost_target'] is not None
                and all(value is not None for value in result['current_estimated_award_by_color'].values()))
            return result

        if deadline is not None and perf_counter() >= deadline:
            return stop('computation_budget_exhausted')
        try:
            board = _board(board)
        except ValueError as exc:
            return stop(str(exc))
        if (not _integer(growth_count) or not _integer(growth_cost, 1)
                or not _integer(start_cost, 0 if self.shared_game_rules else 1)
                or budget_remaining is not None and not _integer(budget_remaining)):
            return stop('quote_count_or_budget_invalid')
        if not any(color for color, _ in board):
            return stop('empty_board')
        award, unknown = self._estimate(board)
        result['current_estimated_award_by_color'] = _by_color(award)
        result['board_features'] = self._features(board, award)
        if not self.ready:
            return stop('model_evidence_unavailable')
        if unknown:
            result['unknown_payoff_slots'] = unknown
            return stop('current_color_or_reward_unknown')
        current_utility = _utility(award)
        result['current_estimated_utility'] = current_utility
        if budget_remaining is not None and growth_cost > budget_remaining:
            return stop('budget_cannot_fund_next_growth')
        target = self.mode - 1
        opportunities, reference = self._opening_reference_opportunity(
            economic_context, budget_remaining, growth_cost, economic_reference_cache)
        result['opportunity_baseline'] = {**reference, 'current_sunk_start_cost': start_cost}
        if opportunities is None:
            # This fallback describes the historical selected whole-round
            # policy. It does not solve the missing future allocation problem.
            paid_start_cost = start_cost
            quote_source = 'same_observed_start_quote'
            quotes = economic_context.get('quotes_by_mode') if isinstance(economic_context, Mapping) else None
            fresh_quote = quotes.get(str(self.mode)) if isinstance(quotes, Mapping) else None
            if (isinstance(fresh_quote, Mapping) and fresh_quote.get('known') is True
                    and _integer(fresh_quote.get('paid_start_cost'), 1)):
                paid_start_cost = fresh_quote['paid_start_cost']
                quote_source = 'fresh_next_same_entry_paid_quote'
            elif start_cost == 0:
                observed_paid_quotes = sorted({r['start_cost'] for r in self._rounds.values()
                                              if r['mode'] == self.mode and r['start_cost'] > 0})
                if len(observed_paid_quotes) != 1:
                    result['observed_paid_start_quotes'] = observed_paid_quotes
                    return stop('free_round_paid_opportunity_quote_unknown_or_ambiguous')
                paid_start_cost = observed_paid_quotes[0]
                quote_source = 'unique_observed_same_mode_paid_quote'
            paid = [r for r in self._rounds.values() if r['mode'] == self.mode
                    and r['start_cost'] == paid_start_cost and r['start_cost'] > 0 and r['coins_spent'] > 0]
            if len(paid) < _MIN_ROUNDS:
                return stop('paid_round_cost_context_uncovered')
            unpriced_paid = [f"{row['operation_id']}:r{row['round']}" for row in paid
                             if any(row['award'][color - 1] for color in _COLORS if color not in _UTILITY_WEIGHTS)]
            if unpriced_paid:
                result['unpriced_opportunity_round_ids'] = unpriced_paid
                return stop('paid_round_opportunity_contains_unpriced_color')
            total_coins = sum(r['coins_spent'] for r in paid)
            totals = tuple(sum(r['award'][c - 1] for r in paid) for c in _COLORS)
            opportunities = tuple(growth_cost * total / total_coins for total in totals)
            result['opportunity_baseline'] = {
                'kind': 'historical_whole_paid_round_ratio_approximation',
                'lambda_basis': 'historical_policy_renewal_ratio', 'approximation': True,
                'reference_fallback': reference,
                'scope': 'same_entry_historical_selected_policy_no_cross_entry_allocation',
                'mode': self.mode, 'target_color': self.mode,
                'paid_start_quote': paid_start_cost, 'current_sunk_start_cost': start_cost,
                'quote_source': quote_source,
                'round_ids': [f"{r['operation_id']}:r{r['round']}" for r in sorted(paid, key=lambda r: (r['operation_id'], r['round']))],
                'round_count': len(paid), 'actual_award_by_color': _by_color(totals),
                'actual_target_award': totals[target], 'actual_coins_spent': total_coins,
                'target_per_coin': totals[target] / total_coins,
                'actual_purple': totals[1] if self.mode == 2 else None,
                'purple_per_coin': totals[1] / total_coins if self.mode == 2 else None,
                'actual_utility': _utility(totals), 'utility_per_coin': _utility(totals) / total_coins,
                'utility_version': _UTILITY_VERSION,
                'meaning': 'Historical full-round ratio times growth quote; approximation, not finite-budget reference value or a sunk-start charge.',
            }
        opportunity = opportunities[target]
        result['opportunity_cost_target'] = opportunity
        result['opportunity_cost_by_color'] = _by_color(opportunities)
        result['opportunity_cost_purple'] = opportunity if self.mode == 2 else None
        utility_opportunity = _utility(opportunities)
        result['opportunity_cost_utility'] = utility_opportunity
        valued_ids = {sample['id'] for sample in self._samples}
        unvalued = [candidate['id'] for candidate in self._candidates.values()
                    if (candidate['mode'] == self.mode or self.shared_game_rules)
                    and self._cost_context_matches(candidate, growth_cost, start_cost)
                    and candidate['growth_count'] == growth_count
                    and self._color_context_matches(board, candidate) and candidate['id'] not in valued_ids]
        if unvalued:
            result['unvalued_context_samples'] = unvalued
            return stop('joint_sample_payoff_context_uncovered')
        cost_matched = [s for s in self._samples
                        if self._cost_context_matches(s, growth_cost, start_cost)]
        eligible = [s for s in cost_matched if self._color_context_matches(board, s)]
        if not eligible:
            return stop('ordered_color_context_uncovered' if cost_matched else 'growth_cost_context_uncovered')
        stage_support = sorted(set(s['growth_count'] for s in eligible))
        result['coverage'] = {'observed_growth_counts': stage_support,
                              'growth_count_exact_match': growth_count in stage_support,
                              'growth_count_extrapolation': growth_count not in stage_support,
                              'mechanical_supported': False}
        eligible = [s for s in eligible if s['growth_count'] == growth_count]
        if not eligible:
            return stop('growth_stage_context_uncovered')
        reference_awards = []
        for sample in eligible:
            reference_award, reference_unknown = self._estimate(self._reference_board(board, sample))
            if reference_unknown:
                result['reference_payoff_unknown'] = {'sample_id': sample['id'], 'slots': reference_unknown}
                return stop('shared_reference_payoff_uncovered')
            reference_awards.append(reference_award)
        ranges = {str(c): [min(value[c - 1] for value in reference_awards),
                          max(value[c - 1] for value in reference_awards)] for c in _COLORS}
        support = ranges[str(self.mode)]
        out_of_envelope = [c for c in _COLORS
                           if not ranges[str(c)][0] <= award[c - 1] <= ranges[str(c)][1]]
        result['per_color_envelope_supported'] = not out_of_envelope
        result['coverage'] = {
            **result['coverage'], 'eligible_samples': len(eligible),
            'award_range_by_color': ranges, 'target_award_range': support,
            'purple_award_range': support if self.mode == 2 else None,
            'growth_count_range': [min(s['growth_count'] for s in eligible), max(s['growth_count'] for s in eligible)],
            'ordered_colors_exact_match': all(tuple(c for c, _ in board) == tuple(c for c, _ in s['before'])
                                              for s in eligible),
            'current_board_colors_preserved': True,
            'shared_game_rules': self.shared_game_rules,
            'source_entry_modes': sorted({s['mode'] for s in eligible}),
            'start_quote_is_mechanics_condition': not self.shared_game_rules,
            'per_color_envelope_supported': not out_of_envelope,
            'per_color_envelope_outside_colors': out_of_envelope,
            'per_color_envelope_is_admission_gate': not self.shared_game_rules,
        }
        if out_of_envelope and not self.shared_game_rules:
            return stop('award_outside_observed_support')
        ranked = []
        for sample in eligible:
            if deadline is not None and perf_counter() >= deadline:
                return stop('computation_budget_exhausted')
            distance = self._distance(board, award, growth_count, sample)
            if distance is not None:
                ranked.append((distance[0], sample['id'], sample, distance[1]))
        ranked.sort(key=lambda item: (item[0], item[1]))
        neighbors = [item for item in ranked if item[0] <= _RADIUS][:_NEIGHBORS]
        if not neighbors:
            return stop('board_neighborhood_uncovered')
        result['coverage_distance'] = neighbors[0][0]
        raw_weights = [1 / (0.05 + item[0]) ** 2 for item in neighbors]
        round_total, round_max = defaultdict(float), defaultdict(float)
        for weight, item in zip(raw_weights, neighbors):
            round_id = item[2]['round_id']
            round_total[round_id] += weight
            round_max[round_id] = max(round_max[round_id], weight)
        # Multiple requests from one round share evidence; cap its total weight
        # to its strongest neighbor, then compute effective *rounds*.
        weights = [weight * round_max[item[2]['round_id']] / round_total[item[2]['round_id']]
                   for weight, item in zip(raw_weights, neighbors)]
        total_weight = sum(weights)
        weights = [weight / total_weight for weight in weights]
        cluster_weights = defaultdict(float)
        for weight, item in zip(weights, neighbors):
            cluster_weights[item[2]['round_id']] += weight
        effective = 1 / sum(weight ** 2 for weight in cluster_weights.values())
        mechanical_supported = (neighbors[0][0] <= _NEAREST_LIMIT
            and len(cluster_weights) >= _MIN_ROUNDS and effective >= _MIN_EFFECTIVE_ROUNDS)
        result['mechanical_supported'] = mechanical_supported
        result['coverage']['mechanical_supported'] = mechanical_supported
        if not trace:
            # Neighbor admission does not require projecting the source samples.
            # Preserve the existing gate order and keep economic values unknown.
            result.update(sample_count=len(neighbors), effective_samples=effective)
            result['coverage'].update({'distinct_rounds': len(cluster_weights)})
            if neighbors[0][0] > _NEAREST_LIMIT:
                return stop('nearest_board_too_distant')
            if len(cluster_weights) < _MIN_ROUNDS or effective < _MIN_EFFECTIVE_ROUNDS:
                return stop('independent_round_support_sparse')
        if admit_projection is not None:
            projection_denial = admit_projection()
            if projection_denial is not None:
                return stop(projection_denial)
        projections = [self._project_request(board, item[2], successor_value=successor_value,
                                             deadline=deadline, trace=trace) for item in neighbors]
        result['request_prediction'] = {
            'method': 'observed_joint_first_phase_then_structural_settlement',
            'trace_schema': 'soul-joint-prediction-compact-v1',
            'source_evidence_inline': False,
            'source_evidence_authority': 'immutable_closed_ledger_at_source_path_with_source_sha256',
            'bound_semantics': 'weighted_source_extrema_with_no_within_source_probabilities',
            'shared_game_rules': self.shared_game_rules,
            'current_board_colors_preserved': True,
            'joint_sample_unit': 'one_complete_eight_slot_first_phase_from_one_paid_request',
            'victim_selection_probabilities': 'unknown',
            'independent_slot_sampling': False, 'independent_internal_step_sampling': False,
            'projected_samples': [{'sample_id': item[2]['id'], 'round_id': item[2]['round_id'],
                                  'source': item[2]['source'], 'growth_count': item[2]['growth_count'],
                                  'source_sha256': self._sources.get(item[2]['source']),
                                  'receipt_identity': {'operation_id': item[2]['operation_id'],
                                                       'round': item[2]['round'], 'sequence': item[2]['sequence']},
                                  'source_entry_mode': item[2]['mode'], 'source_start_cost': item[2]['start_cost'],
                                  'observed_delta_by_color': _by_color(item[2]['delta']),
                                  'weight': weight, **projection}
                                  for item, weight, projection in zip(neighbors, weights, projections)] if trace else [],
        }
        result.update(sample_count=len(neighbors), effective_samples=effective)
        result['coverage'].update({'distinct_rounds': len(cluster_weights)})
        unsupported = [row for row in projections if 'expected_award' not in row]
        if unsupported:
            result['request_prediction']['unsupported_sample_count'] = len(unsupported)
            if any(row.get('reason') == 'first_phase_possibility_cap_exceeded' for row in unsupported):
                return stop('first_phase_possibility_cap_exceeded')
            if any(row.get('reason') == 'computation_budget_exhausted' for row in unsupported):
                return stop('computation_budget_exhausted')
            return stop('joint_first_phase_transplant_uncovered')
        expected_award = tuple(
            sum(weight * projection['expected_award'][c] for weight, projection in zip(weights, projections))
            if all(projection['expected_award'][c] is not None for projection in projections) else None
            for c in range(4))
        mean = tuple(None if expected_award[c] is None else expected_award[c] - award[c] for c in range(4))
        lower_bounds = tuple(sum(weight * projection['lower_award'][c]
                                 for weight, projection in zip(weights, projections)) - award[c] for c in range(4))
        upper_bounds = tuple(sum(weight * projection['upper_award'][c]
                                 for weight, projection in zip(weights, projections)) - award[c] for c in range(4))
        result['expected_award_after_by_color'] = _by_color(expected_award)
        result['expected_delta_by_color'] = _by_color(mean)
        result['expected_delta_bounds_by_color'] = {
            str(c): {'lower': lower_bounds[c - 1], 'upper': upper_bounds[c - 1]} for c in _COLORS}
        result['expected_award_after_bounds_by_color'] = {
            str(c): {'lower': lower_bounds[c - 1] + award[c - 1],
                     'upper': upper_bounds[c - 1] + award[c - 1]} for c in _COLORS}
        result['point_expectation_identified_by_color'] = {str(c): mean[c - 1] is not None for c in _COLORS}
        result['byproduct_expected_delta_by_color'] = {str(c): mean[c - 1] for c in _COLORS if c != self.mode}
        # Retain per-color risk for the receipt. User-priced colors can trade
        # against each other; only exposure without a user price is a veto.
        higher_losses = [c for c in _COLORS if c > self.mode and any(
            projection['lower_award'][c - 1] < award[c - 1] for projection in projections)]
        result['higher_color_loss_boundary'] = {'allowed_loss_by_color': {str(c): 0 for c in _COLORS if c > self.mode},
                                                'violating_colors': higher_losses,
                                                'enforced': False, 'meaning': 'per_color_risk_diagnostic_only'}
        unpriced_losses = sorted({color for projection in projections
                                  for color in projection['unpriced_loss_colors']})
        result['unpriced_color_loss_boundary'] = {
            'unpriced_colors': [color for color in _COLORS if color not in _UTILITY_WEIGHTS],
            'violating_colors': unpriced_losses,
            'changed_colors': sorted({color for projection in projections
                                     for color in projection['unpriced_change_colors']})}
        if unpriced_losses:
            return stop('unpriced_color_loss_boundary')
        distance_mean = sum(weight * item[0] for weight, item in zip(weights, neighbors))
        delta_lowers = [projection['lower_award'][target] - award[target] for projection in projections]
        delta_uppers = [projection['upper_award'][target] - award[target] for projection in projections]
        # For any possible per-source expected values x_i, their variance about
        # their own weighted mean is at most the squared distance about this
        # fixed anchor, bounded in turn by each interval's farther endpoint.
        # A point-identified sample set reduces to the original weighted std.
        dispersion_anchor = lower_bounds[target]
        std = sqrt(sum(weight * max((lo - dispersion_anchor) ** 2, (hi - dispersion_anchor) ** 2)
                       for weight, lo, hi in zip(weights, delta_lowers, delta_uppers)))
        dispersion_reserve = std / sqrt(effective)
        distance_reserve = award[target] * distance_mean
        lower = lower_bounds[target] - dispersion_reserve - distance_reserve
        upper = upper_bounds[target] - dispersion_reserve - distance_reserve
        utility_lowers = [projection['utility_bounds']['lower'] - current_utility for projection in projections]
        utility_uppers = [projection['utility_bounds']['upper'] - current_utility for projection in projections]
        utility_lower = sum(weight * value for weight, value in zip(weights, utility_lowers))
        utility_upper = sum(weight * value for weight, value in zip(weights, utility_uppers))
        utility_expected = (sum(weight * projection['expected_utility']
                                for weight, projection in zip(weights, projections)) - current_utility
                            if all(projection['expected_utility'] is not None for projection in projections) else None)
        utility_std = sqrt(sum(weight * max((lo - utility_lower) ** 2, (hi - utility_lower) ** 2)
                               for weight, lo, hi in zip(weights, utility_lowers, utility_uppers)))
        utility_dispersion_reserve = utility_std / sqrt(effective)
        utility_distance_reserve = current_utility * distance_mean
        utility_reserve = utility_dispersion_reserve + utility_distance_reserve
        utility_conservative = utility_lower - utility_reserve
        selected_utility_lower = utility_conservative
        selected_basis = 'one_request_then_harvest'
        continuation_comparison = None
        unpriced_changes = result['unpriced_color_loss_boundary']['changed_colors']
        continuation_upper = None if unpriced_changes else utility_upper
        if successor_value is not None:
            policy_bounds = [projection['continuation_lower_policy_value_bounds'] for projection in projections]
            policy_lowers = [value['lower'] - current_utility for value in policy_bounds]
            policy_uppers = [value['upper'] - current_utility for value in policy_bounds]
            policy_lower = sum(weight * value for weight, value in zip(weights, policy_lowers))
            policy_upper = sum(weight * value for weight, value in zip(weights, policy_uppers))
            policy_std = sqrt(sum(weight * max((lo - policy_lower) ** 2, (hi - policy_lower) ** 2)
                                  for weight, lo, hi in zip(weights, policy_lowers, policy_uppers)))
            policy_reserve = policy_std / sqrt(effective) + utility_distance_reserve
            policy_conservative = policy_lower - policy_reserve
            optimistic_values = [projection['continuation_utility_bounds']['upper'] for projection in projections]
            continuation_upper = (sum(weight * value for weight, value in zip(weights, optimistic_values))
                                  - current_utility if not unpriced_changes
                                  and all(value is not None for value in optimistic_values) else None)
            continuation_comparison = {
                'weighted_lower_policy_delta_bounds': {'lower': policy_lower, 'upper': policy_upper},
                'engineering_reserve': policy_reserve, 'conservative_delta_utility': policy_conservative,
                'optimistic_delta_utility_upper': continuation_upper,
                'future_request_opportunity_already_deducted': True,
                'current_request_opportunity_already_deducted': False,
                'reserve_semantics': 'joint_outcome_bounds_of_feasible_lower_value_policy',
            }
            if policy_conservative > selected_utility_lower:
                selected_utility_lower = policy_conservative
                selected_basis = 'two_request_fresh_state_stopping_lower_policy'
        result.update({
            'expected_delta_utility': utility_expected,
            'expected_delta_utility_bounds': {'lower': utility_lower, 'upper': utility_upper},
            'conservative_delta_utility': selected_utility_lower,
            'utility_comparison': {
                'weighted_delta_bounds': {'lower': utility_lower, 'upper': utility_upper},
                'engineering_reserve': utility_reserve,
                'dispersion_reserve': utility_dispersion_reserve,
                'distance_reserve': utility_distance_reserve,
                'one_request_conservative_delta_utility': utility_conservative,
                'opportunity_cost_utility': utility_opportunity,
                'selected_lower_policy': selected_basis,
                'selected_conservative_delta_utility': selected_utility_lower,
                'continuation': continuation_comparison,
                'utility_before_bounds': True,
                'unpriced_color_changes': unpriced_changes,
                'unpriced_positive_value_included': False,
                'interval_probabilities': 'not_assumed',
            },
        })
        target_identified = all(lo == hi for lo, hi in zip(delta_lowers, delta_uppers))
        certain_loss_fraction = sum(weight for weight, hi in zip(weights, delta_uppers) if hi < 0)
        possible_loss_fraction = sum(weight for weight, lo in zip(weights, delta_lowers) if lo < 0)
        observed_deltas = [sum(item[2]['delta']) if self.shared_game_rules else item[2]['delta'][target]
                           for item in neighbors]
        result.update({
            'sample_count': len(neighbors), 'effective_samples': effective,
            'expected_delta_by_color': _by_color(mean),
            'empirical_loss_fraction': certain_loss_fraction if certain_loss_fraction == possible_loss_fraction else None,
            'empirical_loss_fraction_bounds': {'lower': certain_loss_fraction, 'upper': possible_loss_fraction},
            'observed_loss_count': sum(delta < 0 for delta in observed_deltas),
            'observed_loss_unit': 'source_full_request_total_quantity' if self.shared_game_rules else 'target_color_quantity',
            'projected_loss_count': sum(delta < 0 for delta in delta_lowers) if target_identified else None,
            'projected_possible_loss_count': sum(delta < 0 for delta in delta_lowers),
            'projected_certain_loss_count': sum(delta < 0 for delta in delta_uppers),
            'conservative_delta_target': lower,
            'conservative_delta_target_bounds': {'lower': lower, 'upper': upper},
            'conservative_delta_purple': lower if self.mode == 2 else None,
            'recommendation_basis': 'joint_utility_lower_bound_with_engineering_reserve',
            'economic_comparison': deepcopy(result['utility_comparison']),
            'target_color_quantity_comparison': {
                'weighted_delta_bounds': {'lower': lower_bounds[target], 'upper': upper_bounds[target]},
                'engineering_reserve': dispersion_reserve + distance_reserve,
                'conservative_delta_bounds': {'lower': lower, 'upper': upper},
                'opportunity_cost_target': opportunity,
                'point_expectation_identified': mean[target] is not None,
            },
        })
        result['coverage'].update({'distinct_rounds': len(cluster_weights), 'mean_distance': distance_mean})
        result['uncertainty'].update({
            'observed_delta_target_range': [min(observed_deltas), max(observed_deltas)] if not self.shared_game_rules else None,
            'observed_source_quantity_delta_range': [min(observed_deltas), max(observed_deltas)],
            'projected_delta_target_range': [min(delta_lowers), max(delta_uppers)],
            'empirical_std_target': std if target_identified else None,
            'dispersion_std_upper_bound_target': std,
            'dispersion_anchor_target': dispersion_anchor,
            'dispersion_reserve_target': dispersion_reserve,
            'distance_reserve_target': distance_reserve,
            'reserve_formula': 'sqrt(sum(w * max((sample_lower - weighted_lower)^2, (sample_upper - weighted_lower)^2))) / sqrt(effective_rounds) + current_target_award * weighted_distance',
            'interval_bounds_are_not_uniform_means': True,
            'unknown_within_source_probabilities': True,
            'zero_observed_losses_is_not_zero_future_risk': True,
        })
        if self.mode == 2:
            result['uncertainty'].update({
                'observed_delta_purple_range': result['uncertainty']['observed_delta_target_range'],
                'empirical_std_purple': std if target_identified else None, 'dispersion_reserve_purple': dispersion_reserve,
                'distance_reserve_purple': distance_reserve,
            })
        for weight, item in (zip(weights, neighbors) if trace else ()):
            distance, _, sample, parts = item
            step_awards = [self._estimate(step)[0] for step in sample['steps']]
            result['samples'].append({
                'id': sample['id'], 'source': sample['source'], 'round_id': sample['round_id'],
                'source_sha256': self._sources.get(sample['source']),
                'source_evidence_inline': False,
                'sequence': sample['sequence'], 'growth_count': sample['growth_count'],
                'weight': weight, 'distance': distance, 'distance_components': parts,
                'before_award_by_color': _by_color(sample['before_award']),
                'after_award_by_color': _by_color(sample['after_award']),
                'delta_by_color': _by_color(sample['delta']),
                'internal_step_awards_by_color': [_by_color(value) for value in step_awards],
                'billed_requests': 1, 'growth_cost': sample['growth_cost'],
            })
        if neighbors[0][0] > _NEAREST_LIMIT:
            return stop('nearest_board_too_distant')
        if len(cluster_weights) < _MIN_ROUNDS or effective < _MIN_EFFECTIVE_ROUNDS:
            return stop('independent_round_support_sparse')
        result['forecast_coverage_supported'] = True
        if selected_utility_lower <= utility_opportunity:
            if continuation_upper is None or max(utility_upper, continuation_upper) > utility_opportunity:
                return stop('utility_continuation_value_unidentified')
            return stop('utility_upper_bound_below_opportunity')
        result['action'] = 'grow'
        result['reason'] = 'soul_joint_conservative_utility_lower_bound_exceeds_opportunity'
        result['decision_status'] = 'supported_utility_growth_within_empirical_model'
        return result
