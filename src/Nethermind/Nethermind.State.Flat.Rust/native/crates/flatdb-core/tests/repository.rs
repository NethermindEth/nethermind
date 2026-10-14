//! The snapshot repository: port of `SnapshotRepositoryTests`, `FindSnapshotToPersist` cases, and proptests of the
//! backward walks against a naive shortest-path model, over in-memory snapshots alone and over a graph whose older
//! part is in the persisted tier, as long finality builds it.

mod common;

use std::collections::{HashMap, HashSet, VecDeque};
use std::sync::Arc;

use common::persisted::TemporaryTier;
use common::snapshots::{FixedFinality, address, empty_snapshot, snapshot, state_id};
use flatdb_core::repository::{AssembledSnapshots, Edge, SnapshotRepository};
use flatdb_core::snapshot::{Snapshot, SnapshotTier};
use flatdb_core::types::{Account, StateId, U256};
use proptest::prelude::*;
use proptest::test_runner::TestCaseError;
use rstest::rstest;

const BASE: SnapshotTier = SnapshotTier::InMemoryBase;
const COMPACTED: SnapshotTier = SnapshotTier::InMemoryCompacted;

fn repository() -> SnapshotRepository {
    SnapshotRepository::new(Arc::new(FixedFinality::default()))
}

fn tier(compacted: bool) -> SnapshotTier {
    if compacted { COMPACTED } else { BASE }
}

/// `AddSnapshotToRepository`: adds the snapshot and, for a base one, registers its state.
fn add(repository: &SnapshotRepository, from: StateId, to: StateId, compacted: bool) -> Arc<Snapshot> {
    let snapshot = snapshot(from, to, |c| {
        c.set_account(address(0xA1), Some(Account::new(1, U256::from_u64(100))))
    });
    assert!(
        repository.try_add(Arc::clone(&snapshot), tier(compacted)),
        "failed to add {from:?}->{to:?}"
    );
    if !compacted {
        repository.add_state_id(to);
    }
    snapshot
}

fn add_blocks(repository: &SnapshotRepository, from_block: u64, to_block: u64, compacted: bool) {
    add(repository, state_id(from_block, 0), state_id(to_block, 0), compacted);
}

/// `BuildSnapshotChain`: base snapshots from `start` up to `end_block`, the new states rooted at `root_byte`.
fn build_chain(repository: &SnapshotRepository, start: StateId, end_block: u64, root_byte: u8) {
    let mut previous = start;
    for block in start.block_number + 1..=end_block {
        let next = state_id(block, root_byte);
        add(repository, previous, next, false);
        previous = next;
    }
}

/// The in-memory snapshot a walk found; a repository without a persisted tier finds no other.
fn in_memory(edge: Edge) -> Arc<Snapshot> {
    match edge {
        Edge::InMemory(snapshot) => snapshot,
        Edge::Persisted(snapshot) => panic!("a persisted snapshot {:?}", snapshot.to()),
    }
}

fn spans(snapshots: &[Arc<Snapshot>]) -> Vec<(u64, u64)> {
    snapshots
        .iter()
        .map(|snapshot| (snapshot.from().block_number, snapshot.to().block_number))
        .collect()
}

#[rstest]
fn try_add_rejects_a_duplicate_and_leases_until_removed(#[values(false, true)] compacted: bool) {
    let repository = repository();
    let added = add(&repository, state_id(0, 0), state_id(1, 0), compacted);
    assert!(!repository.try_add(empty_snapshot(state_id(0, 0), state_id(1, 0)), tier(compacted)));

    let first = repository
        .try_lease_in_memory_state(&state_id(1, 0), tier(compacted))
        .unwrap();
    let second = repository
        .try_lease_in_memory_state(&state_id(1, 0), tier(compacted))
        .unwrap();
    assert!(Arc::ptr_eq(&first, &second) && Arc::ptr_eq(&first, &added));
    assert!(
        repository
            .try_lease_in_memory_state(&state_id(999, 0), tier(compacted))
            .is_none()
    );
    assert!(
        repository
            .try_lease_in_memory_state(&state_id(1, 0), tier(!compacted))
            .is_none()
    );

    assert!(repository.remove_and_release_in_memory_known_state(&state_id(1, 0), tier(compacted)));
    assert!(
        repository
            .try_lease_in_memory_state(&state_id(1, 0), tier(compacted))
            .is_none()
    );
    // The leases taken before the removal keep the snapshot alive: the repository's own lease is gone.
    assert_eq!(Arc::strong_count(&added), 3);
}

#[rstest]
#[case(BASE, 1)]
#[case(COMPACTED, 0)]
fn removal_updates_the_counters_once(#[case] tier: SnapshotTier, #[case] expected_removed_base: i64) {
    let repository = repository();
    let snapshot = add(&repository, state_id(0, 0), state_id(1, 0), tier == COMPACTED);
    let (count, bytes) = if tier == BASE {
        (repository.snapshot_count(), snapshot.estimate_memory())
    } else {
        (
            repository.compacted_snapshot_count(),
            snapshot.estimate_compacted_memory(),
        )
    };
    assert_eq!((count, repository.in_memory_bytes()), (1, bytes));

    assert!(repository.remove_and_release_in_memory_known_state(&state_id(1, 0), tier));
    assert!(!repository.remove_and_release_in_memory_known_state(&state_id(1, 0), tier));

    assert_eq!(repository.removed_base_snapshot_count(), expected_removed_base);
    assert_eq!(
        (
            repository.snapshot_count(),
            repository.compacted_snapshot_count(),
            repository.in_memory_bytes()
        ),
        (0, 0, 0)
    );
    assert_eq!(
        repository.get_last_snapshot_id(),
        None,
        "a base removal unregisters its state"
    );
}

#[test]
fn has_state_only_sees_base_snapshots() {
    let repository = repository();
    add_blocks(&repository, 0, 1, false);
    add_blocks(&repository, 0, 2, true);

    assert!(repository.has_state(&state_id(1, 0)));
    assert!(!repository.has_state(&state_id(2, 0)));
    assert!(!repository.has_state(&state_id(999, 0)));
}

#[rstest]
#[case(&[], 10, &[])]
#[case(&[10], 5, &[])]
#[case(&[1, 3, 5, 7, 10], 6, &[1, 3, 5])]
#[case(&[1, 3, 5], 5, &[1, 3, 5])]
#[case(&[1], u64::MAX, &[])]
#[case(&[1, 3], i64::MAX as u64, &[1, 3])]
fn get_states_up_to_block(#[case] registered: &[u64], #[case] block: u64, #[case] expected: &[u64]) {
    let repository = repository();
    for block in registered {
        repository.add_state_id(state_id(*block, 0));
    }

    let blocks: Vec<u64> = repository
        .get_states_up_to_block(block)
        .iter()
        .map(|state| state.block_number)
        .collect();

    assert_eq!(blocks, expected);
}

#[test]
fn states_at_block_and_last_ids() {
    let repository = repository();
    for state in [state_id(4, 1), state_id(4, 0), state_id(5, 0), state_id(3, 9)] {
        repository.add_state_id(state);
    }

    assert_eq!(
        repository.get_states_at_block_number(4),
        [state_id(4, 0), state_id(4, 1)]
    );
    assert_eq!(repository.get_last_snapshot_id(), Some(state_id(5, 0)));
    assert_eq!(repository.get_last_committed_state_id(), None);
    repository.set_last_committed_state_id(state_id(5, 0));
    repository.set_last_committed_state_id(state_id(3, 9));
    assert_eq!(
        repository.get_last_committed_state_id(),
        Some(state_id(3, 9)),
        "no monotonic guard"
    );
}

/// Adds the snapshots of `edges`, `"a-b"` a base snapshot from block `a` to block `b` and `"a=b"` a compacted one.
fn add_edges(repository: &SnapshotRepository, edges: &str) {
    for edge in edges.split_whitespace() {
        let compacted = edge.contains('=');
        let (from, to) = edge.split_once(['-', '=']).unwrap();
        add_blocks(repository, from.parse().unwrap(), to.parse().unwrap(), compacted);
    }
}

/// The block spans of `snapshots` as `"a-b"`, space separated.
fn span_text(snapshots: &[Arc<Snapshot>]) -> String {
    let spans: Vec<String> = spans(snapshots)
        .iter()
        .map(|(from, to)| format!("{from}-{to}"))
        .collect();
    spans.join(" ")
}

#[rstest]
#[case::empty("", 10, 0, "")]
#[case::single("0-1", 1, 0, "0-1")]
#[case::linear_chain("0-1 1-2 2-3 3-4", 4, 0, "0-1 1-2 2-3 3-4")]
#[case::stops_at_starting_block("0-1 1-2 2-3 3-4 4-5", 4, 2, "2-3 3-4")]
#[case::prefers_compacted("0-1 1-2 0=2", 2, 0, "0-2")]
#[case::overshooting_compacted_yields_to_base("0-1 1-2 2-3 0=3", 3, 1, "1-2 2-3")]
fn assemble_in_memory_snapshots_for_compaction(
    #[case] edges: &str,
    #[case] to_block: u64,
    #[case] min_block: u64,
    #[case] expected: &str,
) {
    let repository = repository();
    add_edges(&repository, edges);

    let assembled = repository.assemble_in_memory_snapshots_for_compaction(&state_id(to_block, 0), min_block, 10);

    assert_eq!(span_text(&assembled), expected);
}

#[rstest]
#[case::linear_chain("0-1 1-2 2-3 3-4 4-5", 5, 0, "0-1 1-2 2-3 3-4 4-5")]
#[case::compacted_takes_wide_hop("0=5", 5, 0, "0-5")]
#[case::in_memory_overshoot_rejected("0=5", 5, 2, "")]
#[case::compacted_overshoot_falls_back_to_base("0-1 1-2 2-3 3-4 4-5 0=5", 5, 2, "2-3 3-4 4-5")]
#[case::base_equals_target("0-1 1-2 2-3", 3, 3, "")]
#[case::unreachable_target("1-2 2-3 3-4", 4, 0, "")]
#[case::self_referencing_snapshot("1-1", 1, 0, "")]
fn assemble_snapshots(#[case] edges: &str, #[case] base_block: u64, #[case] target_block: u64, #[case] expected: &str) {
    let repository = repository();
    add_edges(&repository, edges);

    let assembled = repository.assemble_snapshots(&state_id(base_block, 0), &state_id(target_block, 0), 10);

    assert_eq!(span_text(&assembled.in_memory), expected);
    assert_eq!(assembled.snapshot_count(), expected.split_whitespace().count());
}

/// `AssembleSnapshots_PersistedSpanning_BelowTarget_AcceptedAsTerminal` and
/// `AssembleSnapshots_ExactPersistedMatch_AcceptedAsWinner`: a persisted snapshot ending the walk may start at or below
/// the target.
#[rstest]
#[case::spanning_below_the_target(0)]
#[case::starting_at_the_target(2)]
fn a_persisted_snapshot_reaching_the_target_ends_a_read_chain(#[case] from_block: u64) {
    let tier = TemporaryTier::new();
    tier.convert(&empty_snapshot(state_id(from_block, 0), state_id(5, 0)));

    let assembled = tier.repository.assemble_snapshots(&state_id(5, 0), &state_id(2, 0), 4);

    assert!(assembled.in_memory.is_empty());
    assert_eq!(
        assembled
            .persisted
            .iter()
            .map(|snapshot| snapshot.from())
            .collect::<Vec<_>>(),
        [state_id(from_block, 0)]
    );
}

#[test]
fn walks_of_equal_length_take_the_compacted_edge_first() {
    let repository = repository();
    build_chain(&repository, state_id(0, 0), 2, 0);
    let compacted = add(&repository, state_id(1, 0), state_id(2, 0), true);

    let read = repository
        .assemble_snapshots(&state_id(2, 0), &state_id(1, 0), 4)
        .in_memory;
    let compaction = repository.assemble_in_memory_snapshots_for_compaction(&state_id(2, 0), 1, 4);
    let persist = in_memory(
        repository
            .find_snapshot_to_persist(&state_id(2, 0), &state_id(1, 0), 16)
            .unwrap(),
    );

    assert!(Arc::ptr_eq(&read[0], &compacted) && read.len() == 1);
    assert!(Arc::ptr_eq(&compaction[0], &compacted) && compaction.len() == 1);
    assert!(Arc::ptr_eq(&persist, &compacted));
}

#[test]
fn assemble_snapshots_skips_a_sibling_at_the_target_block() {
    let repository = repository();
    build_chain(&repository, state_id(0, 0), 2, 0);
    add(&repository, state_id(1, 0), state_id(2, 1), false);
    add(&repository, state_id(2, 1), state_id(3, 0), false);

    let to_sibling = repository.assemble_snapshots(&state_id(3, 0), &state_id(2, 0), 4);
    let to_fork_root = repository.assemble_snapshots(&state_id(3, 0), &state_id(1, 0), 4);

    assert!(to_sibling.in_memory.is_empty());
    assert_eq!(spans(&to_fork_root.in_memory), [(1, 2), (2, 3)]);
    assert_eq!(to_fork_root.in_memory[0].to(), state_id(2, 1));
}

#[test]
fn remove_sibling_and_descendents_keeps_a_linear_chain_and_both_branches_of_a_fork_above() {
    let repository = repository();
    build_chain(&repository, state_id(0, 0), 10, 0);
    repository.remove_sibling_and_descendents(&state_id(5, 0)).unwrap();
    assert!((1..=10).all(|block| repository.has_state(&state_id(block, 0))));

    let forked = repository_with_fork_above(6);
    forked.remove_sibling_and_descendents(&state_id(3, 0)).unwrap();
    assert!(forked.has_state(&state_id(7, 0)) && forked.has_state(&state_id(7, 1)));
}

fn repository_with_fork_above(block: u64) -> SnapshotRepository {
    let repository = repository();
    build_chain(&repository, state_id(0, 0), block, 0);
    add(&repository, state_id(block, 0), state_id(block + 1, 0), false);
    add(&repository, state_id(block, 0), state_id(block + 1, 1), false);
    repository
}

#[test]
fn remove_sibling_and_descendents_prunes_unreachable_descendants_above_the_persisted_block() {
    // Common 0->3, then canonical and non-canonical branches both diverging at block 3.
    let repository = repository();
    build_chain(&repository, state_id(0, 0), 3, 0);
    build_chain(&repository, state_id(3, 0), 7, 0);
    build_chain(&repository, state_id(3, 0), 7, 1);
    add(&repository, state_id(4, 1), state_id(7, 1), true);

    repository.remove_sibling_and_descendents(&state_id(5, 0)).unwrap();

    assert!(!repository.has_state(&state_id(6, 1)), "orphan NC(6) should be pruned");
    assert!(!repository.has_state(&state_id(7, 1)), "orphan NC(7) should be pruned");
    assert!(
        repository
            .try_lease_in_memory_state(&state_id(7, 1), COMPACTED)
            .is_none(),
        "and its compacted twin"
    );
    assert!(
        repository.has_state(&state_id(6, 0)) && repository.has_state(&state_id(7, 0)),
        "canonical kept"
    );
    assert!(
        repository.has_state(&state_id(5, 1)),
        "NC(5) at the persisted block is left to RemoveStatesUntil"
    );
    assert!(
        repository.has_state(&state_id(4, 1)),
        "NC(4) below the persisted block is left to RemoveStatesUntil"
    );
}

/// With `side_branch_persisted`, the side branch's last base is also in the persisted tier, which long finality turns
/// on.
#[rstest]
fn remove_unreachable_from_keeps_only_the_head_ancestry(
    #[values(false, true)] known_head: bool,
    #[values(false, true)] side_branch_persisted: bool,
) {
    let (tier, without_tier) = (TemporaryTier::new(), repository());
    let repository = if side_branch_persisted {
        &*tier.repository
    } else {
        &without_tier
    };
    // Main chain 0->8, side branch (3,0)->(6,1); a compacted on the ancestry and one off it.
    build_chain(repository, state_id(0, 0), 8, 0);
    build_chain(repository, state_id(3, 0), 6, 1);
    add(repository, state_id(0, 0), state_id(4, 0), true);
    add(repository, state_id(3, 0), state_id(6, 1), true);
    if side_branch_persisted {
        tier.convert(&empty_snapshot(state_id(5, 1), state_id(6, 1)));
    }
    let head = state_id(5, if known_head { 0 } else { 7 });
    let counts_before = (repository.snapshot_count(), repository.persisted_snapshot_count());

    let pruned = repository
        .remove_unreachable_from(&head, &StateId::PRE_GENESIS)
        .unwrap();

    if !known_head {
        assert_eq!(pruned, 0);
        assert_eq!(
            (repository.snapshot_count(), repository.persisted_snapshot_count()),
            counts_before
        );
        assert_eq!(repository.get_last_committed_state_id(), None);
        return;
    }

    assert_eq!(pruned, if side_branch_persisted { 7 } else { 6 });
    assert!(
        (1..=5).all(|block| repository.has_state(&state_id(block, 0))),
        "ancestors kept"
    );
    assert!(
        repository
            .try_lease_in_memory_state(&state_id(4, 0), COMPACTED)
            .is_some(),
        "compacted on the ancestry"
    );
    for removed in [
        state_id(6, 0),
        state_id(7, 0),
        state_id(8, 0),
        state_id(4, 1),
        state_id(5, 1),
        state_id(6, 1),
    ] {
        assert!(!repository.has_state(&removed), "{removed:?} should be removed");
    }
    assert!(
        repository
            .try_lease_in_memory_state(&state_id(6, 1), COMPACTED)
            .is_none(),
        "compacted off the ancestry"
    );
    assert!(!repository.has_base_persisted_snapshot(&state_id(6, 1)));
    assert_eq!(
        repository.get_states_up_to_block(i64::MAX as u64),
        (1..=5).map(|b| state_id(b, 0)).collect::<Vec<_>>()
    );
    assert_eq!(repository.get_last_committed_state_id(), Some(head));
}

#[test]
fn remove_unreachable_from_refuses_a_head_off_the_persisted_state() {
    let repository = repository();
    build_chain(&repository, state_id(0, 0), 4, 0);
    build_chain(&repository, state_id(1, 0), 3, 1);

    assert_eq!(
        repository
            .remove_unreachable_from(&state_id(3, 1), &state_id(2, 0))
            .unwrap(),
        0
    );
    assert_eq!(repository.snapshot_count(), 6);
    assert_eq!(
        repository
            .remove_unreachable_from(&state_id(2, 0), &state_id(2, 0))
            .unwrap(),
        2 + 2
    );
    assert_eq!(repository.get_last_committed_state_id(), Some(state_id(2, 0)));
}

#[test]
fn remove_states_until_drops_both_tiers_up_to_the_block() {
    let repository = repository();
    build_chain(&repository, state_id(0, 0), 6, 0);
    add(&repository, state_id(0, 0), state_id(4, 0), true);
    add(&repository, state_id(4, 0), state_id(6, 0), true);

    repository.remove_states_until(4).unwrap();

    assert_eq!(repository.get_states_up_to_block(10), [state_id(5, 0), state_id(6, 0)]);
    assert!(
        repository
            .try_lease_in_memory_state(&state_id(4, 0), COMPACTED)
            .is_none()
    );
    assert!(
        repository
            .try_lease_in_memory_state(&state_id(6, 0), COMPACTED)
            .is_some()
    );
    assert_eq!(
        (repository.snapshot_count(), repository.compacted_snapshot_count()),
        (2, 1)
    );
    assert_eq!(repository.removed_base_snapshot_count(), 4);
}

#[rstest]
// A chunk spanning at most CompactSize whose From is the persisted state wins.
#[case::compacted_chunk(16, None, &[(0, 16)])]
// A chunk wider than CompactSize is only a skip pointer; the narrow base edge wins.
#[case::too_wide(8, None, &[(0, 1)])]
// A chunk conflicting with a finalized root is skipped for the canonical base edge.
#[case::finality_conflict(16, Some(0xFF), &[(0, 1)])]
#[case::finality_agrees(16, Some(0x00), &[(0, 16)])]
fn find_snapshot_to_persist(
    #[case] compact_size: u64,
    #[case] finalized_root_byte: Option<u8>,
    #[case] expected: &[(u64, u64)],
) {
    let mut finality = FixedFinality {
        finalized_block_number: 100,
        ..FixedFinality::default()
    };
    if let Some(root_byte) = finalized_root_byte {
        finality.roots.insert(16, state_id(16, root_byte).state_root);
    }
    let repository = SnapshotRepository::new(Arc::new(finality));
    build_chain(&repository, state_id(0, 0), 20, 0);
    add(&repository, state_id(0, 0), state_id(16, 0), true);

    let found = repository
        .find_snapshot_to_persist(&state_id(20, 0), &state_id(0, 0), compact_size)
        .map(in_memory);

    assert_eq!(spans(found.as_slice()), expected);
}

#[test]
fn find_snapshot_to_persist_from_pre_genesis_and_below_the_persisted_state() {
    let repository = repository();
    add(&repository, StateId::PRE_GENESIS, state_id(0, 0), false);
    build_chain(&repository, state_id(0, 0), 3, 0);

    let genesis = in_memory(
        repository
            .find_snapshot_to_persist(&state_id(3, 0), &StateId::PRE_GENESIS, 16)
            .unwrap(),
    );
    assert_eq!((genesis.from(), genesis.to()), (StateId::PRE_GENESIS, state_id(0, 0)));
    assert!(
        repository
            .find_snapshot_to_persist(&state_id(2, 0), &state_id(2, 0), 16)
            .is_none()
    );
    assert!(
        repository
            .find_snapshot_to_persist(&state_id(1, 0), &state_id(2, 0), 16)
            .is_none()
    );
    assert!(
        repository
            .find_snapshot_to_persist(&state_id(3, 0), &state_id(1, 7), 16)
            .is_none()
    );
}

/// A random snapshot graph: base edges `(b-1, fork) -> (b, fork)` plus forks and compacted edges.
#[derive(Debug, Clone)]
struct Graph {
    edges: Vec<(StateId, StateId, bool)>,
}

fn graph_strategy() -> impl Strategy<Value = Graph> {
    let base_edges = prop::collection::vec((1u64..14, 0u8..3, 0u8..3), 4..40);
    let compacted_edges = prop::collection::vec((0u64..12, 2u64..6, 0u8..3), 0..8);
    (base_edges, compacted_edges).prop_map(|(bases, compacted)| {
        let mut edges = Vec::new();
        let mut seen = HashSet::new();
        // A main chain, so most walks have something to find.
        for block in 1..=14 {
            edges.push((state_id(block - 1, 0), state_id(block, 0), false));
            seen.insert((state_id(block, 0), false));
        }
        for (block, from_fork, to_fork) in bases {
            if seen.insert((state_id(block, to_fork), false)) {
                edges.push((state_id(block - 1, from_fork), state_id(block, to_fork), false));
            }
        }
        for (from, span, fork) in compacted {
            if seen.insert((state_id(from + span, fork), true)) {
                edges.push((state_id(from, fork), state_id(from + span, fork), true));
            }
        }
        Graph { edges }
    })
}

fn repository_of(graph: &Graph) -> SnapshotRepository {
    let repository = repository();
    for (from, to, compacted) in &graph.edges {
        add(&repository, *from, *to, *compacted);
    }
    repository
}

/// The fewest hops from `start` to a node `wins` accepts, over the edges `usable` accepts: the naive model of a walk.
fn shortest_hops(
    graph: &Graph,
    start: StateId,
    usable: impl Fn(&StateId) -> bool,
    wins: impl Fn(&StateId) -> bool,
) -> Option<usize> {
    let edges = graph
        .edges
        .iter()
        .map(|&(from, to, compacted)| (from, to, tier(compacted)));
    shortest_hops_by_verdict(edges, start, |from, _| {
        if !usable(from) {
            Verdict::Skip
        } else if wins(from) {
            Verdict::Win
        } else {
            Verdict::Traverse
        }
    })
}

/// What a walk does with the edge to a parent.
enum Verdict {
    Skip,
    Traverse,
    Win,
}

/// The fewest hops from `start` over the edges `decide` traverses to one it lets win.
fn shortest_hops_by_verdict(
    edges: impl Iterator<Item = (StateId, StateId, SnapshotTier)>,
    start: StateId,
    decide: impl Fn(&StateId, SnapshotTier) -> Verdict,
) -> Option<usize> {
    let mut parents: HashMap<StateId, Vec<(StateId, SnapshotTier)>> = HashMap::new();
    for (from, to, tier) in edges {
        parents.entry(to).or_default().push((from, tier));
    }
    let mut queue = VecDeque::from([(start, 0usize)]);
    let mut seen = HashSet::from([start]);
    while let Some((node, hops)) = queue.pop_front() {
        for (from, tier) in parents.get(&node).into_iter().flatten() {
            match decide(from, *tier) {
                Verdict::Skip => {}
                Verdict::Win => return Some(hops + 1),
                Verdict::Traverse => {
                    if seen.insert(*from) {
                        queue.push_back((*from, hops + 1));
                    }
                }
            }
        }
    }
    None
}

/// The chain is contiguous, ends at `end` and every element is a snapshot of the graph.
fn assert_chain(graph: &Graph, chain: &[Arc<Snapshot>], end: StateId) {
    for (snapshot, next) in chain.iter().zip(chain.iter().skip(1)) {
        assert_eq!(snapshot.to(), next.from());
    }
    assert_eq!(chain.last().unwrap().to(), end);
    for snapshot in chain {
        assert!(
            graph
                .edges
                .iter()
                .any(|(from, to, _)| *from == snapshot.from() && *to == snapshot.to())
        );
    }
}

/// A state at a block of `blocks` on one of the graph's three forks.
fn state_strategy(blocks: std::ops::Range<u64>) -> impl Strategy<Value = StateId> {
    (blocks, 0u8..3).prop_map(|(block, fork)| state_id(block, fork))
}

proptest! {
    #![proptest_config(ProptestConfig::with_cases(512))]

    #[test]
    fn assemble_snapshots_finds_a_shortest_chain(
        graph in graph_strategy(),
        base in state_strategy(1..15),
        target in state_strategy(0..14),
    ) {
        check_assemble_snapshots(&graph, base, target)?;
    }

    #[test]
    fn assemble_for_compaction_finds_a_shortest_chain_to_the_minimum_block(
        graph in graph_strategy(),
        to in state_strategy(1..15),
        min_block in 0u64..14,
    ) {
        check_assemble_for_compaction(&graph, to, min_block)?;
    }

    #[test]
    fn remove_unreachable_from_keeps_exactly_the_ancestry(graph in graph_strategy(), head in state_strategy(1..15)) {
        check_remove_unreachable_from(&graph, head)?;
    }
}

fn check_assemble_snapshots(graph: &Graph, base: StateId, target: StateId) -> Result<(), TestCaseError> {
    let repository = repository_of(graph);

    let assembled = repository.assemble_snapshots(&base, &target, 4).in_memory;

    // An edge below the target overshoots, and another state at the target's block is a sibling fork.
    let usable = |from: &StateId| {
        from.block_number > target.block_number || (from.block_number == target.block_number && *from == target)
    };
    let expected = if base == target {
        None
    } else {
        shortest_hops(graph, base, usable, |from| *from == target)
    };
    prop_assert_eq!(assembled.len(), expected.unwrap_or(0));
    if !assembled.is_empty() {
        prop_assert_eq!(assembled[0].from(), target);
        assert_chain(graph, &assembled, base);
    }
    Ok(())
}

fn check_assemble_for_compaction(graph: &Graph, to: StateId, min_block: u64) -> Result<(), TestCaseError> {
    let repository = repository_of(graph);

    let assembled = repository.assemble_in_memory_snapshots_for_compaction(&to, min_block, 4);

    let usable = |from: &StateId| from.block_number >= min_block;
    let expected = shortest_hops(graph, to, usable, |from| from.block_number == min_block);
    prop_assert_eq!(assembled.len(), expected.unwrap_or(0));
    if !assembled.is_empty() {
        prop_assert_eq!(assembled[0].from().block_number, min_block);
        assert_chain(graph, &assembled, to);
    }
    Ok(())
}

fn check_remove_unreachable_from(graph: &Graph, head: StateId) -> Result<(), TestCaseError> {
    let repository = repository_of(graph);
    let mut ancestry = HashSet::from([head]);
    let mut stack = vec![head];
    while let Some(node) = stack.pop() {
        for (from, to, _) in &graph.edges {
            if *to == node && ancestry.insert(*from) {
                stack.push(*from);
            }
        }
    }

    repository
        .remove_unreachable_from(&head, &StateId::PRE_GENESIS)
        .unwrap();

    if repository.get_last_committed_state_id() == Some(head) {
        for (_, to, compacted) in &graph.edges {
            let kept = repository.try_lease_in_memory_state(to, tier(*compacted)).is_some();
            prop_assert_eq!(kept, ancestry.contains(to), "{:?} compacted={}", to, compacted);
        }
    } else {
        prop_assert!(!graph.edges.iter().any(|(_, to, compacted)| !compacted && *to == head));
    }
    Ok(())
}

/// A random graph as long finality builds it: the snapshots ending at or below block `split` are persisted, of every
/// persisted tier, and the newer ones are in memory, each part with forks and wider edges of its own.
#[derive(Debug, Clone)]
struct TieredGraph {
    edges: Vec<(StateId, StateId, SnapshotTier)>,
}

const WIDE_PERSISTED_TIERS: [SnapshotTier; 3] = [
    SnapshotTier::PersistedSmallCompacted,
    SnapshotTier::PersistedCompactSized,
    SnapshotTier::PersistedLargeCompacted,
];

fn tiered_graph_strategy() -> impl Strategy<Value = TieredGraph> {
    let base_edges = prop::collection::vec((1u64..15, 0u8..3, 0u8..3), 4..40);
    let wide_edges = prop::collection::vec((0u64..13, 2u64..6, 0u8..3, 0usize..3), 0..12);
    (2u64..12, base_edges, wide_edges).prop_map(|(split, bases, wide)| {
        let base_tier = |block: u64| {
            if block <= split {
                SnapshotTier::PersistedBase
            } else {
                BASE
            }
        };
        // A main chain, so most walks have something to find.
        let main_chain = (1..=14).map(|block| (state_id(block - 1, 0), state_id(block, 0), base_tier(block)));
        let forks = bases.into_iter().map(|(block, from_fork, to_fork)| {
            (
                state_id(block - 1, from_fork),
                state_id(block, to_fork),
                base_tier(block),
            )
        });
        let wide = wide.into_iter().filter_map(|(from, span, fork, kind)| {
            let tier = if from + span <= split {
                WIDE_PERSISTED_TIERS[kind]
            } else if from >= split {
                COMPACTED
            } else {
                return None;
            };
            Some((state_id(from, fork), state_id(from + span, fork), tier))
        });
        // A tier holds one snapshot per state.
        let mut seen = HashSet::new();
        let edges = main_chain
            .chain(forks)
            .chain(wide)
            .filter(|(_, to, tier)| seen.insert((*to, *tier)))
            .collect();
        TieredGraph { edges }
    })
}

fn tier_of(graph: &TieredGraph) -> TemporaryTier {
    let tier = TemporaryTier::new();
    for &(from, to, snapshot_tier) in &graph.edges {
        if snapshot_tier.is_persisted() {
            tier.register(from, to, snapshot_tier);
        } else {
            add(&tier.repository, from, to, snapshot_tier == COMPACTED);
        }
    }
    tier
}

/// The chain is contiguous, ends at `end`, and every element is a snapshot of the graph in its tier, the persisted ones
/// below the in-memory ones.
fn assert_tiered_chain(graph: &TieredGraph, assembled: &AssembledSnapshots, end: StateId) {
    let chain = assembled
        .persisted
        .iter()
        .map(|snapshot| (snapshot.from(), snapshot.to(), Some(snapshot.tier())))
        .chain(
            assembled
                .in_memory
                .iter()
                .map(|snapshot| (snapshot.from(), snapshot.to(), None)),
        )
        .collect::<Vec<_>>();
    for (link, next) in chain.iter().zip(chain.iter().skip(1)) {
        assert_eq!(link.1, next.0);
    }
    assert_eq!(chain.last().unwrap().1, end);
    for (from, to, tier) in chain {
        assert!(graph.edges.iter().any(|&(edge_from, edge_to, edge_tier)| {
            (edge_from, edge_to) == (from, to) && tier.map_or(!edge_tier.is_persisted(), |tier| tier == edge_tier)
        }));
    }
}

proptest! {
    #![proptest_config(ProptestConfig::with_cases(256))]

    #[test]
    fn assemble_snapshots_over_the_persisted_tier_finds_a_shortest_chain(
        graph in tiered_graph_strategy(),
        base in state_strategy(1..15),
        target in state_strategy(0..14),
    ) {
        check_tiered_assemble_snapshots(&graph, base, target)?;
    }

    #[test]
    fn assemble_for_persisted_compaction_reaches_the_deepest_block_by_a_shortest_chain(
        graph in tiered_graph_strategy(),
        to in state_strategy(1..13),
        min_block in 0u64..12,
    ) {
        check_tiered_assemble_for_compaction(&graph, to, min_block)?;
    }

    #[test]
    fn remove_unreachable_from_keeps_exactly_the_ancestry_in_both_tiers(
        graph in tiered_graph_strategy(),
        head in state_strategy(1..15),
    ) {
        check_tiered_remove_unreachable_from(&graph, head)?;
    }
}

fn check_tiered_assemble_snapshots(graph: &TieredGraph, base: StateId, target: StateId) -> Result<(), TestCaseError> {
    let tier = tier_of(graph);

    let assembled = tier.repository.assemble_snapshots(&base, &target, 4);

    // A persisted edge below the target ends the chain, an in-memory one overshoots, and another state at the target's
    // block is a sibling fork.
    let decide = |from: &StateId, edge_tier: SnapshotTier| {
        if from.block_number < target.block_number {
            if edge_tier.is_persisted() {
                Verdict::Win
            } else {
                Verdict::Skip
            }
        } else if *from == target {
            Verdict::Win
        } else if from.block_number == target.block_number {
            Verdict::Skip
        } else {
            Verdict::Traverse
        }
    };
    let expected = if base == target {
        None
    } else {
        shortest_hops_by_verdict(graph.edges.iter().copied(), base, decide)
    };
    prop_assert_eq!(assembled.snapshot_count(), expected.unwrap_or(0));
    if assembled.snapshot_count() > 0 {
        let oldest = assembled
            .persisted
            .first()
            .map_or_else(|| assembled.in_memory[0].from(), |snapshot| snapshot.from());
        prop_assert!(
            oldest == target || (!assembled.persisted.is_empty() && oldest.block_number < target.block_number)
        );
        assert_tiered_chain(graph, &assembled, base);
    }
    Ok(())
}

fn check_tiered_assemble_for_compaction(graph: &TieredGraph, to: StateId, min_block: u64) -> Result<(), TestCaseError> {
    let tier = tier_of(graph);

    let assembled = tier
        .repository
        .assemble_persisted_snapshots_for_compaction(&to, min_block);

    // The fewest hops to each state the persisted edges at or above the minimum block reach.
    let mut hops = HashMap::from([(to, 0usize)]);
    let mut queue = VecDeque::from([to]);
    while let Some(node) = queue.pop_front() {
        for &(from, edge_to, edge_tier) in &graph.edges {
            if edge_to == node
                && edge_tier.is_persisted()
                && from.block_number >= min_block
                && !hops.contains_key(&from)
            {
                hops.insert(from, hops[&node] + 1);
                queue.push_back(from);
            }
        }
    }
    // The deepest block reached wins, by its shortest chain.
    let deepest = hops
        .keys()
        .filter(|state| **state != to)
        .map(|state| state.block_number)
        .min();
    let expected = deepest.map_or(0, |deepest| {
        hops.iter()
            .filter(|(state, _)| state.block_number == deepest && **state != to)
            .map(|(_, hop_count)| *hop_count)
            .min()
            .unwrap()
    });
    let expected = if expected < 2 { 0 } else { expected };
    prop_assert_eq!(assembled.len(), expected);
    if !assembled.is_empty() {
        prop_assert_eq!(Some(assembled[0].from().block_number), deepest);
        let chain = AssembledSnapshots {
            in_memory: Vec::new(),
            persisted: assembled,
        };
        assert_tiered_chain(graph, &chain, to);
    }
    Ok(())
}

fn check_tiered_remove_unreachable_from(graph: &TieredGraph, head: StateId) -> Result<(), TestCaseError> {
    let tier = tier_of(graph);
    let repository = &tier.repository;
    let mut ancestry = HashSet::from([head]);
    let mut stack = vec![head];
    while let Some(node) = stack.pop() {
        for (from, to, _) in &graph.edges {
            if *to == node && ancestry.insert(*from) {
                stack.push(*from);
            }
        }
    }

    repository
        .remove_unreachable_from(&head, &StateId::PRE_GENESIS)
        .unwrap();

    if repository.get_last_committed_state_id() == Some(head) {
        for &(_, to, edge_tier) in &graph.edges {
            let kept = if edge_tier.is_persisted() {
                repository.try_lease_persisted_state(&to, edge_tier).is_some()
            } else {
                repository.try_lease_in_memory_state(&to, edge_tier).is_some()
            };
            prop_assert_eq!(kept, ancestry.contains(&to), "{:?} {:?}", to, edge_tier);
        }
    } else {
        prop_assert!(!graph.edges.iter().any(|(_, to, edge_tier)| matches!(
            edge_tier,
            SnapshotTier::InMemoryBase | SnapshotTier::PersistedBase
        ) && *to == head));
    }
    Ok(())
}
