//! The persistence manager: port of the long-finality-off parts of `PersistenceManagerTests`, over a real persistence
//! with a recording wrapper and a fake host; the write path also on the managed backend and, with `rocksdb-it`, RocksDB.
//!
//! The C# fixture enables long finality with a 90000-block `LongFinalityMaxReorgDepth`; with it off `MaxReorgDepth` is
//! the backstop, so the port sets that to 90000 ([`PM_CONFIG`]) and every backstop number reads the same.
//!
//! Not ported: the cases of the persisted-snapshot tier and of Phase 2 conversion (`PersistBase`, `TryFindSnapshotTo
//! Convert`, `ConvertCompactedRange`, `RemoveFinalizedPersistedForks`, `PinnedHead`, `RepeatedForks`, the finalized-root
//! cache cases built on persisted forks, `*_PrunesPersistedTier`, `*PersistedOnly*`), which need long finality, and the
//! history capture hook cases, which are out of scope. `DetermineSnapshotAction_LatestSnapshotBelowPersistedBlock_
//! ReturnsNullWithoutUnderflow` lacks its `[Test]` attribute in C# and so never runs there; it passes here.

mod common;

use std::sync::Arc;
use std::sync::atomic::Ordering;
use std::thread;

use common::any_kv::Backend;
use common::manager::{
    BLOCK0, Fixture, FixtureOptions, Op, PM_CONFIG, PM_CONFIG_LF, account, decision, logged_warnings, pm_config, state,
};
use common::snapshots::state_id;
use common::snapshots::{address, snapshot};
use common::{column_entries, column_name};
use flatdb_core::compactor::SnapshotCompactor;
use flatdb_core::events::{EventKind, PersistedNode, PersistedNodes};
use flatdb_core::host::HostError;
use flatdb_core::keccak::{account_path, keccak256};
use flatdb_core::kv::{Column, KvError, KvResult, ReadFlags};
use flatdb_core::persisted::TierError;
use flatdb_core::persisted::codec::{CATALOG_KEY_SIZE, catalog_key, depth};
use flatdb_core::persisted::stack::PersistedLayer;
use flatdb_core::persisted::storage::catalog::{CatalogKv, MemCatalogKv};
use flatdb_core::persistence::{Persistence, PersistenceError, PersistenceReader, PersistenceWriteBatch, ReaderFlags};
use flatdb_core::persistence_manager::{LongFinalityConfig, PersistError, PersistenceManagerConfig, SnapshotAction};
use flatdb_core::snapshot::content::SnapshotContentBuilder;
use flatdb_core::snapshot::{Snapshot, SnapshotTier};
use flatdb_core::stats::{Histogram, HistogramLabel, Observation};
use flatdb_core::types::{Account, StateId, TreePath, U256};
use rstest::rstest;

/// The C# fixture, with long finality on or off.
fn fixture(long_finality: bool) -> Fixture {
    Fixture::new(Backend::Mem, pm_config(long_finality), Some(BLOCK0)).unwrap()
}

/// The `(from, to)` of an in-memory persist; any other decision fails the test.
fn span(action: &SnapshotAction) -> (StateId, StateId) {
    let (kind, from, to) = decision(action);
    assert_eq!(kind, "memory");
    (from, to)
}

fn byte_budget_config(max_in_memory_snapshot_bytes: u64) -> PersistenceManagerConfig {
    PersistenceManagerConfig {
        min_reorg_depth: 64,
        max_reorg_depth: 256,
        max_in_memory_snapshot_bytes,
        long_finality: None,
    }
}

/// The `DetermineSnapshotAction` cases on a single chain from `Block0`: `edges` as in [`Fixture::add_edges`], canonical
/// roots at `roots`, and the candidate expected.
#[rstest]
#[case::insufficient_in_memory_depth(60, 100, &[], "", None)]
#[case::sufficient_depth_and_finalized_compacted(100, 16, &[16], "0=16", Some(("0", "16")))]
#[case::sufficient_depth_and_finalized_base(100, 16, &[16], "0-16", Some(("0", "16")))]
#[case::unfinalized_but_below_force_limit(150, 10, &[], "", None)]
#[case::unfinalized_below_backstop(300, 10, &[], "0-1", None)]
#[case::backstop_exceeded_seeds_from_in_memory_tier(95_000, 10, &[], "0-16", Some(("0", "16")))]
#[case::finalized_beyond_head_seeds_at_boundary(200, 25_128_361, &[16], "0-16", Some(("0", "16")))]
#[case::no_snapshot_available(100, 100, &[16], "", None)]
#[case::snapshot_with_wrong_from_state(100, 100, &[16], "5=16", None)]
#[case::exactly_at_minimum_boundary(79, 100, &[], "", None)]
#[case::fold_keeps_min_reorg_depth_below_floor(79, 16, &[16], "0=16", None)]
#[case::fold_keeps_min_reorg_depth_at_floor(80, 16, &[16], "0=16", Some(("0", "16")))]
fn determine_snapshot_action(
    #[case] latest_block: u64,
    #[case] finalized_block: u64,
    #[case] roots: &[u64],
    #[case] edges: &str,
    #[case] expected: Option<(&str, &str)>,
    #[values(false, true)] long_finality: bool,
) {
    let fixture = fixture(long_finality);
    fixture.host.set_finalized_block_number(finalized_block);
    for &block in roots {
        fixture
            .host
            .set_finalized_state_root_at(block, state(&block.to_string()).state_root);
    }
    fixture.add_edges(edges);

    let candidate = fixture
        .persistence_manager
        .determine_snapshot_action(&state(&latest_block.to_string()))
        .unwrap();

    assert_eq!(
        candidate.as_ref().map(span),
        expected.map(|(from, to)| (state(from), state(to)))
    );
    if let Some(candidate) = candidate {
        assert!(
            latest_block - span(&candidate).1.block_number >= PM_CONFIG.min_reorg_depth
                || latest_block > PM_CONFIG.max_reorg_depth,
            "only the backstop folds into MinReorgDepth"
        );
    }
    assert!(fixture.persistence.batches().is_empty(), "deciding never writes");
}

/// `DetermineSnapshotAction_FreshDb_PersistsGenesisBaseFirst`: from `PreGenesis` the genesis base comes first, as
/// `PreGenesis -> 16` would span 17 blocks.
#[test]
fn determine_snapshot_action_on_a_fresh_db_persists_the_genesis_base_first() {
    let fixture = Fixture::new(Backend::Mem, PM_CONFIG, None).unwrap();
    fixture.host.set_finalized_block_number(100);
    fixture.host.set_finalized_state_root_at(16, state("16").state_root);
    fixture.add_edges("pg-0 0=16");

    let candidate = fixture
        .persistence_manager
        .determine_snapshot_action(&state("100"))
        .unwrap();

    assert_eq!(candidate.as_ref().map(span), Some((StateId::PRE_GENESIS, BLOCK0)));
}

/// `DetermineSnapshotAction_MultipleStatesAtBlock_SelectsCorrectOne`,
/// `DetermineSnapshotAction_UnfinalizedForkAtBoundary_PersistsHeadReachableFork` and
/// `DetermineSnapshotAction_LongerNonCanonicalFork_PersistsCommittedHeadChain`: the candidate is on the finalized
/// chain, else on the committed head's.
#[rstest]
#[case::finalized_root_picks_the_fork(100, 16, Some(16), "0=16r1 0=16r2", None, "16r2")]
#[case::backstop_follows_the_head(95_000, 10, None, "0=16r1 0=16r2 16r2=95000", Some("95000"), "16r2")]
#[case::backstop_ignores_a_longer_fork(
    95_001,
    0,
    None,
    "0=16r2 16r2=95000r2 0=16r1 16r1=95001r1",
    Some("95000r2"),
    "16r2"
)]
fn determine_snapshot_action_follows_the_canonical_fork(
    #[case] latest_block: u64,
    #[case] finalized_block: u64,
    #[case] root_at: Option<u64>,
    #[case] edges: &str,
    #[case] committed: Option<&str>,
    #[case] expected_to: &str,
    #[values(false, true)] long_finality: bool,
) {
    let fixture = fixture(long_finality);
    fixture.host.set_finalized_block_number(finalized_block);
    if let Some(block) = root_at {
        fixture
            .host
            .set_finalized_state_root_at(block, state("16r2").state_root);
    }
    fixture.add_edges(edges);
    if let Some(committed) = committed {
        fixture.repository.set_last_committed_state_id(state(committed));
    }

    let latest = StateId::new(latest_block, state("16r1").state_root);
    let candidate = fixture.persistence_manager.determine_snapshot_action(&latest).unwrap();

    assert_eq!(candidate.as_ref().map(span), Some((BLOCK0, state(expected_to))));
}

/// `DetermineSnapshotAction_BackstopRaisedAboveMinReorgDepth`, `DetermineSnapshotAction_FinalizedGatePassesButSeed
/// Missing_BackstopStillForcesPersist` and `DetermineSnapshotAction_LongFinalityDisabled_SkipsConversionPath`: the
/// backstop is `max(MaxReorgDepth, MinReorgDepth + CompactSize)`, `LongFinalityMaxReorgDepth` standing in for
/// `MaxReorgDepth` with long finality on, and fires even when the finalized trigger ran.
#[rstest]
#[case::raised_below_min_plus_compact_size(90_000, 90_000, 90_001, 5, "0-16", false)]
#[case::raised_above_min_plus_compact_size(90_000, 90_000, 90_017, 5, "0-16", true)]
#[case::finalized_gate_passes_but_seed_missing(90_000, 90_000, 90_017, 90_000, "0-16", true)]
#[case::max_reorg_depth_with_long_finality_off(64, 256, 300, 10, "0-1", true)]
#[case::at_max_reorg_depth(64, 256, 256, 10, "0-1", false)]
fn determine_snapshot_action_backstop(
    #[case] min_reorg_depth: u64,
    #[case] max_reorg_depth: u64,
    #[case] latest_block: u64,
    #[case] finalized_block: u64,
    #[case] edges: &str,
    #[case] expect_persist: bool,
    #[values(false, true)] long_finality: bool,
) {
    let fixture = fixture(long_finality);
    let persistence_manager = fixture.persistence_manager_with(PersistenceManagerConfig {
        min_reorg_depth,
        max_reorg_depth,
        max_in_memory_snapshot_bytes: 0,
        long_finality: long_finality.then_some(LongFinalityConfig {
            max_reorg_depth,
            max_in_memory_base_snapshot_count: 160,
        }),
    });
    fixture.host.set_finalized_block_number(finalized_block);
    fixture.add_edges(edges);

    let candidate = persistence_manager
        .determine_snapshot_action(&state(&latest_block.to_string()))
        .unwrap();

    assert_eq!(candidate.is_some(), expect_persist);
    if let Some(candidate) = candidate {
        assert_eq!(span(&candidate).0, BLOCK0);
    }
}

/// `DetermineSnapshotAction_ByteBudget_FinalizedTriggerKeepsMinReorgDepth`.
#[rstest]
#[case::byte_budget_disabled_finalized_below_floor(0, 40, false)]
#[case::over_byte_budget_finalized_below_floor(1, 40, false)]
#[case::over_byte_budget_finalized_one_below_floor(1, 79, false)]
#[case::over_byte_budget_finalized_at_floor(1, 80, true)]
fn determine_snapshot_action_byte_budget_keeps_min_reorg_depth_with_finality(
    #[case] byte_budget: u64,
    #[case] latest_block: u64,
    #[case] expect_persist: bool,
) {
    let fixture = fixture(false);
    let persistence_manager = fixture.persistence_manager_with(byte_budget_config(byte_budget));
    fixture.host.set_finalized_block_number(16);
    fixture.host.set_finalized_state_root_at(16, state("16").state_root);
    fixture.add_edges("0=16");
    assert!(fixture.repository.in_memory_bytes() > 0);

    let candidate = persistence_manager
        .determine_snapshot_action(&state(&latest_block.to_string()))
        .unwrap();

    assert_eq!(
        candidate.as_ref().map(span),
        expect_persist.then(|| (BLOCK0, state("16")))
    );
}

/// `DetermineSnapshotAction_ByteBudget_ForcesBackstopWhenFinalityStalled`: over the budget the next snapshot is
/// persisted while at least `MinReorgDepth` blocks stay above it, counted from the committed head when there is one.
#[rstest]
#[case::finality_stalled_no_byte_budget(0, 100, false, None)]
#[case::finality_stalled_over_byte_budget_forces_persist_above_floor(1, 100, true, None)]
#[case::finality_stalled_over_byte_budget_floor_holds_below_min_reorg_depth(1, 50, false, None)]
#[case::finality_stalled_over_byte_budget_floor_holds_after_fold(1, 65, false, None)]
#[case::finality_stalled_over_byte_budget_floor_holds_one_below_boundary(1, 79, false, None)]
#[case::finality_stalled_over_byte_budget_persists_at_boundary(1, 80, true, None)]
#[case::queued_height_above_committed_head_keeps_floor(1, 80, false, Some(79))]
#[case::queued_height_above_committed_head_persists_at_floor(1, 81, true, Some(80))]
fn determine_snapshot_action_byte_budget_forces_a_persist_when_finality_stalls(
    #[case] byte_budget: u64,
    #[case] latest_block: u64,
    #[case] expect_persist: bool,
    #[case] committed_block: Option<u64>,
) {
    let fixture = fixture(false);
    let persistence_manager = fixture.persistence_manager_with(byte_budget_config(byte_budget));
    let expected = fixture.add(BLOCK0, state("16"), false);
    fixture.host.set_finalized_block_number(5);
    if let Some(block) = committed_block {
        let head = state(&block.to_string());
        fixture.add(state("16"), head, false);
        fixture.repository.set_last_committed_state_id(head);
    }
    let leases_before = Arc::strong_count(&expected);

    let candidate = persistence_manager
        .determine_snapshot_action(&state(&latest_block.to_string()))
        .unwrap();

    assert_eq!(
        candidate.as_ref().map(span),
        expect_persist.then(|| (BLOCK0, state("16")))
    );
    drop(candidate);
    assert_eq!(
        Arc::strong_count(&expected),
        leases_before,
        "a rejected or returned candidate must not keep a lease"
    );
}

/// `DetermineSnapshotAction_LatestSnapshotBelowPersistedBlock_ReturnsNullWithoutUnderflow`, which C# never runs.
#[test]
fn determine_snapshot_action_below_the_persisted_block_does_not_underflow() {
    logged_warnings();
    let fixture = Fixture::new(Backend::Mem, PM_CONFIG, Some(state("100"))).unwrap();
    fixture.host.set_finalized_block_number(10);
    fixture.add(state("100"), state("101"), false);
    fixture.repository.set_last_committed_state_id(state("101"));

    let candidate = fixture
        .persistence_manager
        .determine_snapshot_action(&state("50"))
        .unwrap();

    assert!(candidate.is_none());
    assert!(
        logged_warnings().iter().any(|message| {
            message.starts_with(&format!("Latest snapshot {} is below persisted state", state("50")))
        })
    );
}

/// `DetermineSnapshotAction_WhenBackstopHasNoCandidate_WarnsOnlyIfConversionAlsoFails`: when the backstop finds nothing
/// the stall is reported once per persisted state, unless a conversion was found.
#[rstest]
fn determine_snapshot_action_warns_once_when_the_backstop_finds_nothing(
    #[values(false, true)] enable_conversion: bool,
) {
    logged_warnings();
    let fixture = fixture(enable_conversion);
    let persistence_manager = fixture.persistence_manager_with(PersistenceManagerConfig {
        min_reorg_depth: 0,
        max_reorg_depth: 32,
        max_in_memory_snapshot_bytes: 0,
        long_finality: enable_conversion.then_some(LongFinalityConfig {
            max_reorg_depth: 32,
            max_in_memory_base_snapshot_count: 0,
        }),
    });
    // Too wide to persist, at a root no other test uses.
    let head = StateId::new(64, state(if enable_conversion { "64r78" } else { "64r77" }).state_root);
    fixture.add(BLOCK0, head, false);
    fixture.repository.set_last_committed_state_id(head);
    let stall_warnings = || {
        logged_warnings()
            .iter()
            .filter(|message| {
                message.contains("neither persistence nor conversion") && message.contains(&format!("{head}"))
            })
            .count()
    };
    let expected_warnings = usize::from(!enable_conversion);

    for _ in 0..2 {
        let decided = persistence_manager.determine_snapshot_action(&head).unwrap();
        assert_eq!(
            decided.as_ref().map(decision),
            enable_conversion.then_some(("base", BLOCK0, head)),
            "conversion is considered before warning"
        );
        assert_eq!(stall_warnings(), expected_warnings, "warned once per persisted state");
    }
}

/// `SnapshotRepository_InMemoryBytes_ReturnsToZeroAfterRemove`, with the `Metrics` gauges the repository moves.
#[rstest]
fn in_memory_bytes_and_gauges_return_to_zero_after_remove(
    #[values(false, true)] compacted: bool,
    #[values(false, true)] long_finality: bool,
) {
    let fixture = fixture(long_finality);
    let counters = &fixture.repository.stats().counters;
    let snapshot = fixture.add(BLOCK0, state("1"), compacted);
    let tier = if compacted {
        SnapshotTier::InMemoryCompacted
    } else {
        SnapshotTier::InMemoryBase
    };
    let (count, memory) = if compacted {
        (&counters.compacted_snapshot_count, &counters.compacted_snapshot_memory)
    } else {
        (&counters.snapshot_count, &counters.snapshot_memory)
    };
    let bytes = fixture.repository.in_memory_bytes();
    assert!(bytes > 0);
    assert_eq!(count.load(Ordering::Relaxed), 1);
    assert_eq!(memory.load(Ordering::Relaxed), bytes);
    assert_eq!(counters.total_snapshot_memory.load(Ordering::Relaxed), bytes);

    fixture
        .repository
        .remove_and_release_in_memory_known_state(&snapshot.to(), tier);

    assert_eq!(fixture.repository.in_memory_bytes(), 0);
    assert_eq!(count.load(Ordering::Relaxed), 0);
    assert_eq!(memory.load(Ordering::Relaxed), 0);
    assert_eq!(counters.total_snapshot_memory.load(Ordering::Relaxed), 0);
}

fn slot(value: u64) -> U256 {
    U256::from_u64(value)
}

/// A block writing two accounts, two slots, two self-destructs (one of a new account), a state node and a storage node
/// each with a placeholder beside it.
fn rich_snapshot(from: StateId, to: StateId) -> Arc<Snapshot> {
    snapshot(from, to, |content| {
        content.set_self_destruct(address(0xA1), false);
        content.set_self_destruct(address(0xB2), true);
        content.set_account(address(0xA1), Some(account(100)));
        content.set_account(address(0xB2), Some(Account::new(2, slot(200))));
        content.set_storage(address(0xA1), slot(1), Some(slot(42)));
        content.set_storage(address(0xA1), slot(2), Some(slot(99)));
        content.set_state_node(TreePath::from_hex(""), [0x11; 32], &[0xC2, 0x80, 0x80]);
        content.set_state_node(TreePath::from_hex("1"), [0x12; 32], &[]);
        content.set_storage_node(
            account_path(&address(0xA1)),
            TreePath::from_hex("2"),
            [0x21; 32],
            &[0xC1, 0x80],
        );
        content.set_storage_node(account_path(&address(0xA1)), TreePath::from_hex("3"), [0x22; 32], &[]);
    })
}

/// The rank of an operation in the order `PersistSnapshot` writes them.
fn rank(op: &Op) -> usize {
    match op {
        Op::CreateBatch { .. } => 0,
        Op::SelfDestruct(_) => 1,
        Op::SetAccount(..) => 2,
        Op::SetStorage(..) => 3,
        Op::SetStateNode(..) => 4,
        Op::SetStorageNode(..) => 5,
        Op::Other(_) => 6,
        Op::Commit => 7,
    }
}

/// `PersistSnapshot_WithAccountsStorageAndTrieNodes_WritesToBatch`, `PersistSnapshot_WithSelfDestructedAddresses_Calls
/// SelfDestruct` and `PersistSnapshot_EmptySnapshot_CreatesWriteBatch`: one batch without write flags, self-destructs
/// of existing accounts, accounts, slots, state nodes, then storage nodes, placeholders skipped, in key order for a
/// compacted snapshot; the written nodes are queued for the `IsPersisted` replay once committed.
#[rstest]
fn persist_snapshot_writes_one_ordered_batch(
    #[values(Backend::Mem, Backend::Managed, Backend::Rocks)] backend: Backend,
    #[values(false, true)] compacted: bool,
) {
    let Some(fixture) = Fixture::new(backend, PM_CONFIG, Some(BLOCK0)) else {
        return;
    };
    let to = state("16");
    let base = rich_snapshot(BLOCK0, to);
    let snapshot = if compacted {
        let compactor = SnapshotCompactor::new(fixture.schedule, Arc::clone(&fixture.repository));
        let next = fixture.add_with_balance(state("16"), state("17"), false, 17);
        Arc::new(compactor.compact_snapshot_bundle(&[base, next]))
    } else {
        base
    };
    let to = snapshot.to();

    fixture.persistence_manager.persist_snapshot(&snapshot).unwrap();

    let ops = fixture.persistence.ops();
    let mut expected = vec![
        Op::CreateBatch {
            from: BLOCK0,
            to,
            flags: 0,
        },
        Op::SelfDestruct(address(0xA1)),
        Op::SetAccount(address(0xA1), Some(account(if compacted { 17 } else { 100 }))),
        Op::SetAccount(address(0xB2), Some(Account::new(2, slot(200)))),
        Op::SetStorage(address(0xA1), slot(1), Some(slot(42))),
        Op::SetStorage(address(0xA1), slot(2), Some(slot(99))),
        Op::SetStateNode(TreePath::from_hex(""), vec![0xC2, 0x80, 0x80]),
        Op::SetStorageNode(account_path(&address(0xA1)), TreePath::from_hex("2"), vec![0xC1, 0x80]),
        Op::Commit,
    ];
    if compacted {
        assert_eq!(ops, expected, "a compacted snapshot persists in key order");
    } else {
        assert!(ops.windows(2).all(|pair| rank(&pair[0]) <= rank(&pair[1])), "{ops:?}");
        let mut sorted_ops = ops.clone();
        sorted_ops.sort_by_key(|op| format!("{op:?}"));
        expected.sort_by_key(|op| format!("{op:?}"));
        assert_eq!(sorted_ops, expected);
    }

    let reader = fixture.persistence.create_reader(ReaderFlags::NONE).unwrap();
    assert_eq!(reader.current_state(), to);
    assert_eq!(reader.try_get_slot(&address(0xA1), &slot(2)).unwrap(), Some(slot(99)));
    assert_eq!(
        reader
            .try_load_state_rlp(&TreePath::from_hex(""), ReadFlags::NONE)
            .unwrap(),
        Some(vec![0xC2, 0x80, 0x80])
    );
    assert_eq!(
        reader
            .try_load_state_rlp(&TreePath::from_hex("1"), ReadFlags::NONE)
            .unwrap(),
        None,
        "a placeholder is never written"
    );

    assert_eq!(
        fixture.events.drain(10),
        [PersistedNodes {
            kind: EventKind::Persisted,
            from: BLOCK0,
            to,
            nodes: vec![
                PersistedNode {
                    address_hash: None,
                    path: TreePath::from_hex(""),
                    hash: [0x11; 32],
                },
                PersistedNode {
                    address_hash: Some(account_path(&address(0xA1))),
                    path: TreePath::from_hex("2"),
                    hash: [0x21; 32],
                },
            ],
        }]
    );
    let observations = fixture.repository.stats().drain_observations(10);
    assert_eq!(
        observations[..2],
        [
            Observation {
                metric: Histogram::FlatPersistenceSnapshotSize as u16,
                label: HistogramLabel::StateNodes as u16,
                value: 3.0
            },
            Observation {
                metric: Histogram::FlatPersistenceSnapshotSize as u16,
                label: HistogramLabel::StorageNodes as u16,
                value: 2.0
            },
        ]
    );
    assert_eq!(observations[2].metric, Histogram::FlatPersistenceTime as u16);
}

/// An empty snapshot still moves the persisted state in its own batch.
#[rstest]
fn persist_snapshot_of_an_empty_snapshot_creates_a_write_batch(#[values(false, true)] long_finality: bool) {
    let fixture = fixture(long_finality);

    fixture
        .persistence_manager
        .persist_snapshot(&snapshot(BLOCK0, state("16"), |_| {}))
        .unwrap();

    assert_eq!(
        fixture.persistence.ops(),
        [
            Op::CreateBatch {
                from: BLOCK0,
                to: state("16"),
                flags: 0
            },
            Op::Commit
        ]
    );
    assert_eq!(fixture.db_state(), state("16"));
    assert_eq!(fixture.events.drain(10)[0].nodes, []);
}

/// A failed block-data flush aborts before the batch, and a failed write discards it (C# would commit the writes made
/// so far and move the persisted state); neither queues persisted nodes.
#[rstest]
fn persist_snapshot_writes_nothing_when_it_fails(
    #[values(false, true)] fail_the_flush: bool,
    #[values(false, true)] long_finality: bool,
) {
    let fixture = fixture(long_finality);
    if fail_the_flush {
        fixture.host.fail_next_flushes([HostError::Failed(7)]);
    } else {
        fixture.persistence.fail_storage_writes();
    }

    let result = fixture
        .persistence_manager
        .persist_snapshot(&rich_snapshot(BLOCK0, state("16")));

    if fail_the_flush {
        assert_eq!(result, Err(PersistError::Host(HostError::Failed(7))));
        assert!(fixture.persistence.batches().is_empty());
    } else {
        assert!(matches!(result, Err(PersistError::Persistence(_))), "{result:?}");
        assert_eq!(fixture.persistence.batches(), [(BLOCK0, state("16"))]);
        assert!(!fixture.persistence.ops().contains(&Op::Commit));
    }
    assert_eq!(fixture.db_state(), BLOCK0);
    let reader = fixture.persistence.create_reader(ReaderFlags::NONE).unwrap();
    assert_eq!(reader.get_account(&address(0xA1)).unwrap(), None);
    assert!(fixture.events.is_empty());
}

/// After a corruption or I/O error, on which `DbOnTheRocks.HandleFatalDbError` exits, no persist writes again and the
/// error is handed to the shell once; after any other error the next persist goes through.
#[rstest]
#[case::corruption("Corruption: block checksum mismatch", true)]
#[case::io_error("IO error: No space left on device", true)]
#[case::busy("Busy: ", false)]
fn a_fatal_error_stops_every_later_persist(
    #[case] message: &str,
    #[case] fatal: bool,
    #[values(false, true)] long_finality: bool,
) {
    let fixture = fixture(long_finality);
    assert!(
        fixture
            .repository
            .try_add(rich_snapshot(BLOCK0, state("16")), SnapshotTier::InMemoryCompacted)
    );
    fixture.repository.add_state_id(state("16"));
    fixture.host.set_finalized_block_number(16);
    fixture.host.set_finalized_state_root_at(16, state("16").state_root);
    let error = PersistError::Persistence(PersistenceError::Kv(KvError::rocks(message.to_owned())));
    let persistence_manager = &fixture.persistence_manager;

    fixture
        .persistence
        .fail_storage_writes_with(Some(PersistenceError::Kv(KvError::rocks(message.to_owned()))));
    let failed = persistence_manager.add_to_persistence(&state("100"));
    fixture.persistence.fail_storage_writes_with(None);
    let retried = (
        persistence_manager.add_to_persistence(&state("100")),
        persistence_manager.flush_to_persistence(),
    );

    assert_eq!(failed, Err(error.clone()));
    if fatal {
        assert_eq!(retried, (Err(error.clone()), Err(error.clone())));
        assert_eq!(
            fixture.persistence.batches().len(),
            1,
            "nothing is written after the fatal error"
        );
        assert_eq!(fixture.db_state(), BLOCK0);
        assert_eq!(
            (
                persistence_manager.take_fatal_error(),
                persistence_manager.take_fatal_error()
            ),
            (Some(error), None)
        );
    } else {
        assert_eq!(retried, (Ok(()), Ok(state("16"))));
        assert_eq!(fixture.db_state(), state("16"));
        assert_eq!(persistence_manager.take_fatal_error(), None);
    }
}

/// `AddToPersistence_WithAvailableSnapshot_PersistsAndUpdatesState`: the persist moves the persisted state and releases
/// every snapshot up to it.
#[rstest]
fn add_to_persistence_persists_and_updates_state(
    #[values(Backend::Mem, Backend::Managed, Backend::Rocks)] backend: Backend,
) {
    let Some(fixture) = Fixture::new(backend, PM_CONFIG, Some(BLOCK0)) else {
        return;
    };
    fixture.add_edges("0=16 0-8");
    fixture.host.set_finalized_block_number(16);
    fixture.host.set_finalized_state_root_at(16, state("16").state_root);

    fixture.persistence_manager.add_to_persistence(&state("100")).unwrap();

    assert_eq!(fixture.persistence.batches(), [(BLOCK0, state("16"))]);
    assert_eq!(
        fixture.persistence_manager.get_current_persisted_state_id().unwrap(),
        state("16")
    );
    assert_eq!(fixture.db_state(), state("16"));
    let reader = fixture.persistence.create_reader(ReaderFlags::NONE).unwrap();
    assert_eq!(reader.get_account(&address(0xA1)).unwrap(), Some(account(16)));
    assert_eq!(
        fixture.repository.in_memory_bytes(),
        0,
        "every snapshot up to block 16 is released"
    );
    assert!(!fixture.repository.has_state(&state("8")));
}

/// Each call persists at most four snapshots, so a backlog is worked off across blocks.
#[rstest]
fn add_to_persistence_drains_at_most_four_snapshots_per_call(#[values(false, true)] long_finality: bool) {
    let fixture = fixture(long_finality);
    let edges: Vec<String> = (0..16).map(|block| format!("{block}-{}", block + 1)).collect();
    fixture.add_edges(&edges.join(" "));
    fixture.host.set_finalized_block_number(16);
    fixture.host.set_finalized_state_root_at(16, state("16").state_root);

    fixture.persistence_manager.add_to_persistence(&state("100")).unwrap();
    assert_eq!(fixture.db_state(), state("4"));
    fixture.persistence_manager.add_to_persistence(&state("100")).unwrap();

    assert_eq!(fixture.db_state(), state("8"));
    assert_eq!(fixture.persistence.batches().len(), 8);
}

/// The descendants of the forks a persist orphans are removed before its batch is written, the persisted state staying
/// unchanged until the batch commits; the forks at the persisted block go with the states the persist supersedes.
#[rstest]
fn add_to_persistence_removes_orphaned_forks_before_the_batch(#[values(false, true)] long_finality: bool) {
    let fixture = fixture(long_finality);
    fixture.add_edges("0-16r1 0-16r2 16r2-17r2");
    fixture.host.set_finalized_block_number(16);
    fixture.host.set_finalized_state_root_at(16, state("16r1").state_root);
    let held = fixture.host.hold_next_flush();

    thread::scope(|scope| {
        let persist = scope.spawn(|| fixture.persistence_manager.add_to_persistence(&state("100")));
        held.started.recv().unwrap();

        assert!(
            !fixture.repository.has_state(&state("17r2")),
            "orphaned before the write"
        );
        assert!(fixture.repository.has_state(&state("16r2")));
        assert!(fixture.persistence.batches().is_empty());
        assert_eq!(fixture.db_state(), BLOCK0);
        assert_eq!(
            fixture.persistence_manager.get_current_persisted_state_id().unwrap(),
            BLOCK0
        );

        held.release.send(Ok(())).unwrap();
        persist.join().unwrap().unwrap();
    });
    assert_eq!(fixture.db_state(), state("16r1"));
    assert!(
        !fixture.repository.has_state(&state("16r2")),
        "superseded by the persist"
    );
}

/// The edges of every flush case: forks `r1`/`r2` at block 16, two compacted chunks, and a base chain.
#[rstest]
#[case::no_snapshots("", None, 0, &[], "0", &[])]
#[case::every_block_has_a_base(
    "0-1 1-2 2-3 3-4 4-5 5-6 6-7 7-8 8-9 9-10 10-11 11-12 12-13 13-14 14-15 15-16 16-17 17-18 18-19 19-20 20-21 21-22 \
     22-23 23-24 24-25 25-26 26-27 27-28 28-29 29-30 30-31 31-32 0=16 16=32",
    Some("32"), 0, &[], "32", &["0>16", "16>32"]
)]
#[case::committed_chunk_conflicts_with_finality("0-1 1-16 0=16", Some("16"), 32, &[(1, "1"), (16, "16r9")], "1", &["0>1"])]
#[case::finalized_first("0=16 16=32", None, 100, &[(16, "16"), (32, "32")], "32", &["0>16", "16>32"])]
#[case::unfinalized_falls_back_to_first_available("0=16", None, 0, &[], "16", &["0>16"])]
#[case::unfinalized_fork_follows_the_head("0=16r1 0=16r2 16r2=32", Some("32"), 0, &[], "32", &["0>16r2", "16r2>32"])]
#[case::longer_non_canonical_fork("0=16r1 0=16r2 16r1=300 16r2=32r2", Some("32r2"), 0, &[], "32r2", &["0>16r2", "16r2>32r2"])]
#[case::prefers_finalized_over_unfinalized("0=16r1 0=16r2", None, 16, &[(16, "16r1")], "16r1", &["0>16r1"])]
#[case::persists_multiple_snapshots_in_order("0-1 1-2 2-3", None, 0, &[], "3", &["0>1", "1>2", "2>3"])]
fn flush_to_persistence(
    #[case] edges: &str,
    #[case] committed: Option<&str>,
    #[case] finalized_block: u64,
    #[case] roots: &[(u64, &str)],
    #[case] expected: &str,
    #[case] expected_batches: &[&str],
    #[values(false, true)] long_finality: bool,
) {
    let fixture = fixture(long_finality);
    fixture.add_edges(edges);
    if let Some(committed) = committed {
        fixture.repository.set_last_committed_state_id(state(committed));
    }
    fixture.host.set_finalized_block_number(finalized_block);
    for (block, root) in roots {
        fixture.host.set_finalized_state_root_at(*block, state(root).state_root);
    }

    let flushed = fixture.persistence_manager.flush_to_persistence().unwrap();

    let expected_batches: Vec<(StateId, StateId)> = expected_batches
        .iter()
        .map(|batch| {
            let (from, to) = batch.split_once('>').unwrap();
            (state(from), state(to))
        })
        .collect();
    assert_eq!(flushed, state(expected));
    assert_eq!(fixture.db_state(), state(expected));
    assert_eq!(fixture.persistence.batches(), expected_batches);
}

/// `DropStateNotReachableFrom_KeepsHeadAncestryOnly`: chain `Block0 -> 1 -> 2 -> 3 -> 4` plus a fork `3 -> (4, r1)`;
/// `remaining` lists the main-chain blocks still held afterwards, the fork surviving exactly when block 4 does.
#[rstest]
#[case::known_head_above_the_persisted_state(None, "3", &[1, 2, 3])]
#[case::unknown_head_is_refused(None, "3r7", &[1, 2, 3, 4])]
#[case::head_at_the_persisted_state(Some("2"), "2", &[1, 2])]
#[case::head_at_a_persisted_state_nothing_is_keyed_at_drops_everything(Some("5"), "5", &[])]
#[case::head_below_the_persisted_state_is_refused(Some("3"), "2", &[1, 2, 3, 4])]
fn drop_state_not_reachable_from_keeps_the_head_ancestry_only(
    #[case] persisted: Option<&str>,
    #[case] head: &str,
    #[case] remaining: &[u64],
) {
    let persisted = persisted.map_or(BLOCK0, state);
    let fixture = Fixture::new(Backend::Mem, PM_CONFIG, Some(persisted)).unwrap();
    fixture.add_edges("0-1 1-2 2-3 3-4 3-4r1");

    fixture
        .persistence_manager
        .drop_state_not_reachable_from(&state(head))
        .unwrap();

    for block in 1..=4 {
        assert_eq!(
            fixture.repository.has_state(&state(&block.to_string())),
            remaining.contains(&block),
            "block {block}"
        );
    }
    assert_eq!(
        fixture.repository.has_state(&state("4r1")),
        remaining.contains(&4),
        "fork"
    );
    assert_eq!(
        fixture.persistence_manager.get_current_persisted_state_id().unwrap(),
        persisted
    );
}

/// `ResetPersistedStateId` re-reads the state another writer moved.
#[rstest]
fn reset_persisted_state_id_rereads_the_persistence(#[values(false, true)] long_finality: bool) {
    let fixture = fixture(long_finality);
    assert_eq!(
        fixture.persistence_manager.get_current_persisted_state_id().unwrap(),
        BLOCK0
    );
    fixture
        .persistence
        .create_write_batch(&BLOCK0, &state("7"), flatdb_core::kv::WriteFlags::NONE)
        .unwrap()
        .commit()
        .unwrap();
    assert_eq!(
        fixture.persistence_manager.get_current_persisted_state_id().unwrap(),
        BLOCK0,
        "cached"
    );

    fixture.persistence_manager.reset_persisted_state_id().unwrap();

    assert_eq!(
        fixture.persistence_manager.get_current_persisted_state_id().unwrap(),
        state("7")
    );
}

// ---- Long finality: the persisted-snapshot tier and Phase 2 conversion ----

/// A state of a fork named by `name`, as the C# tests key them by `Keccak.Compute(name)`.
fn named(block: u64, name: &str) -> StateId {
    StateId::new(block, keccak256(name.as_bytes()))
}

/// Adds an in-memory base setting account `0xA1..` to nonce 1, balance 100 and `0xB2..` to `account_b`.
fn add_with_account_b(fixture: &Fixture, from: StateId, to: StateId, account_b: Account) {
    let added = snapshot(from, to, |content| {
        content.set_account(address(0xA1), Some(account(100)));
        content.set_account(address(0xB2), Some(account_b));
    });
    assert!(fixture.repository.try_add(added, SnapshotTier::InMemoryBase));
    fixture.repository.add_state_id(to);
}

/// The C# fixture's long-finality settings with a byte budget of `max_in_memory_snapshot_bytes` and
/// `max_in_memory_base_snapshot_count`, `CreateByteBudgetManager`'s.
fn long_finality_config(
    max_in_memory_snapshot_bytes: u64,
    max_in_memory_base_snapshot_count: u64,
) -> PersistenceManagerConfig {
    PersistenceManagerConfig {
        max_in_memory_snapshot_bytes,
        long_finality: Some(LongFinalityConfig {
            max_reorg_depth: 90_000,
            max_in_memory_base_snapshot_count,
        }),
        ..PM_CONFIG_LF
    }
}

/// `AddToPersistence_PinnedHead_RetainsUnfinalizedPersistedForks`: with finality stalled at genesis, 400 forks of two
/// blocks are converted down to `MaxInMemoryBaseSnapshotCount` in-memory bases, nothing reaches the persistence, and the
/// newest fork still assembles in two hops.
#[test]
fn add_to_persistence_with_a_pinned_head_retains_the_unfinalized_persisted_forks() {
    const FORK_COUNT: i64 = 400;
    let fixture = fixture(true);
    let mut latest = BLOCK0;
    for i in 1..=FORK_COUNT {
        let setup = named(1, &format!("setup-{i}"));
        latest = named(2, &format!("test-{i}"));
        fixture.add(BLOCK0, setup, false);
        fixture.repository.set_last_committed_state_id(setup);
        fixture.persistence_manager.add_to_persistence(&setup).unwrap();
        fixture.add(setup, latest, false);
        fixture.repository.set_last_committed_state_id(latest);
        fixture.persistence_manager.add_to_persistence(&latest).unwrap();
    }

    let assembled = fixture.repository.assemble_snapshots(&latest, &BLOCK0, 2);
    assert_eq!(
        fixture.persistence_manager.get_current_persisted_state_id().unwrap(),
        BLOCK0
    );
    assert_eq!(fixture.repository.snapshot_count(), 160);
    assert_eq!(assembled.snapshot_count(), 2);
    assert_eq!(fixture.repository.persisted_snapshot_count(), FORK_COUNT * 2 - 160);
    assert!(fixture.persistence.batches().is_empty());
}

/// `AddToPersistence_PrunesOnlyKnownFinalizedPersistedForks`, through the persistence manager or the repository: a fork
/// is pruned only at a height whose canonical root finality knows, a reader keeps a pruned snapshot readable until it
/// lets go, which then releases its table, and a missing root is asked for again on the next pass.
#[rstest]
fn add_to_persistence_prunes_only_known_finalized_persisted_forks(
    #[values(0, 1, 2)] finalized_block: u64,
    #[values(0, 1, 2)] root_lookup_ceiling: u64,
    #[values(false, true)] prune_directly: bool,
) {
    let fixture = fixture(true);
    let (canonical1, canonical2, orphan1, orphan2) = (state("1r1"), state("2r1"), state("1r2"), state("2r2"));
    let (tip, sibling) = (state("3r1"), state("3r2"));
    for (from, to) in [
        (BLOCK0, canonical1),
        (canonical1, canonical2),
        (BLOCK0, orphan1),
        (orphan1, orphan2),
        (canonical2, tip),
        (canonical2, sibling),
    ] {
        fixture.persist_base(from, to);
    }
    fixture.repository.set_last_committed_state_id(tip);
    fixture.host.set_finalized_block_number(finalized_block);
    fixture.host.set_root_lookup_ceiling(root_lookup_ceiling);
    fixture.host.set_finalized_state_root_at(1, canonical1.state_root);
    fixture.host.set_finalized_state_root_at(2, canonical2.state_root);

    let reader = fixture.repository.try_lease_base_persisted_snapshot(&orphan1).unwrap();
    let reservation = Arc::downgrade(reader.reservation());
    let prune_parent = finalized_block >= 1 && root_lookup_ceiling >= 1;
    let prune_child = finalized_block >= 2 && root_lookup_ceiling >= 2;
    let expected_count = 6 - i64::from(prune_parent) - i64::from(prune_child);
    if prune_directly {
        fixture.repository.remove_finalized_persisted_forks(&BLOCK0).unwrap();
    } else {
        fixture.persistence_manager.add_to_persistence(&tip).unwrap();
    }

    let assembled = fixture.repository.assemble_snapshots(&tip, &BLOCK0, 3);
    assert_eq!(
        fixture.persistence_manager.get_current_persisted_state_id().unwrap(),
        BLOCK0
    );
    assert_eq!(fixture.repository.persisted_snapshot_count(), expected_count);
    assert_eq!(fixture.tier().catalog_kv.rows().len() as i64, expected_count);
    assert_eq!(fixture.repository.has_base_persisted_snapshot(&orphan1), !prune_parent);
    assert_eq!(fixture.repository.has_base_persisted_snapshot(&orphan2), !prune_child);
    assert!(fixture.repository.has_base_persisted_snapshot(&sibling));
    assert_eq!(
        assembled.persisted.len(),
        3,
        "canonical ancestry is still needed above the persisted state"
    );
    assert_eq!(
        reader.try_get_account(&address(0xA1)).unwrap(),
        Some(Some(account(100))),
        "an existing reader survives pruning"
    );
    drop((reader, assembled));
    assert_eq!(
        reservation.upgrade().is_none(),
        prune_parent,
        "the orphan's table is released after its last reader"
    );

    if finalized_block == 2 && root_lookup_ceiling < 2 {
        fixture.host.set_root_lookup_ceiling(2);
        fixture.persistence_manager.add_to_persistence(&tip).unwrap();
        assert_eq!(
            fixture.repository.persisted_snapshot_count(),
            4,
            "missing roots must be retried even when the finalized height has not advanced"
        );
    }
    assert!(fixture.persistence.batches().is_empty());
}

/// `AddToPersistence_CachesFinalizedRootsUntilHeightsExpire`: a finalized root is asked for once while its height is
/// cached, and again once a prune expired it.
#[rstest]
#[case(true, false, true)]
#[case(false, false, true)]
#[case(false, true, true)]
#[case(true, false, false)]
fn add_to_persistence_caches_finalized_roots_until_their_heights_expire(
    #[case] connected_to_persisted_state: bool,
    #[case] has_fork: bool,
    #[case] prune_snapshots: bool,
) {
    let fixture = fixture(true);
    let (parent, tip, orphan) = (state("1"), state("2"), state("1r2"));
    let from = if connected_to_persisted_state {
        BLOCK0
    } else {
        state("0r2")
    };
    fixture.persist_base(from, parent);
    fixture.persist_base(parent, tip);
    if has_fork {
        fixture.persist_base(from, orphan);
    }
    fixture.repository.set_last_committed_state_id(tip);
    fixture.host.set_finalized_block_number(2);
    fixture.host.set_finalized_state_root_at(1, parent.state_root);
    fixture.host.set_finalized_state_root_at(2, tip.state_root);

    fixture.persistence_manager.add_to_persistence(&tip).unwrap();
    fixture.persistence_manager.add_to_persistence(&tip).unwrap();
    assert_eq!((fixture.host.lookup_count(1), fixture.host.lookup_count(2)), (1, 1));
    assert_eq!(fixture.repository.has_base_persisted_snapshot(&orphan), has_fork);

    if prune_snapshots {
        fixture.repository.remove_persisted_states_until(2).unwrap();
        fixture.persist_base(BLOCK0, parent);
    } else {
        fixture.repository.remove_finalized_persisted_forks(&tip).unwrap();
    }
    fixture.persistence_manager.add_to_persistence(&tip).unwrap();
    assert_eq!(
        (fixture.host.lookup_count(1), fixture.host.lookup_count(2)),
        (2, 1),
        "the expired height must be evicted from the cache"
    );
}

/// `AddToPersistence_DefersFinalizedForkPruningWhenLocalChainDisagrees`: while finality disagrees with the local chain
/// the branch being built keeps its ancestry; pruning resumes once they agree.
#[rstest]
#[case(2, false)]
#[case(2, true)]
#[case(100, false)]
fn add_to_persistence_defers_finalized_fork_pruning_while_the_local_chain_disagrees(
    #[case] finalized_block: u64,
    #[case] anchor_matches: bool,
) {
    let fixture = fixture(true);
    let (parent, tip, orphan) = (state("1r1"), state("2r1"), state("1r2"));
    fixture.persist_base(BLOCK0, parent);
    fixture.persist_base(parent, tip);
    fixture.repository.set_last_committed_state_id(tip);
    fixture.host.set_finalized_block_number(finalized_block);
    fixture.host.set_finalized_state_root_at(1, [0xAA; 32]);
    fixture
        .host
        .set_finalized_state_root_at(2, if anchor_matches { tip.state_root } else { [0xBB; 32] });

    fixture.persistence_manager.add_to_persistence(&tip).unwrap();
    assert_eq!(
        fixture.repository.assemble_snapshots(&tip, &BLOCK0, 2).persisted.len(),
        2,
        "the branch being built must retain its ancestry"
    );

    fixture.host.set_finalized_block_number(2);
    fixture.host.set_finalized_state_root_at(1, parent.state_root);
    fixture.host.set_finalized_state_root_at(2, tip.state_root);
    fixture.persist_base(BLOCK0, orphan);
    fixture.persistence_manager.add_to_persistence(&tip).unwrap();
    assert!(
        !fixture.repository.has_base_persisted_snapshot(&orphan),
        "pruning resumes after finality agrees with the local chain"
    );
}

/// `AddToPersistence_FinalizedPruningPreservesCurrentPersistedBase`: the base ending at the persisted state survives
/// whatever root finality reports there; its sibling goes.
#[rstest]
fn finalized_pruning_preserves_the_current_persisted_base(#[values(false, true)] root_matches: bool) {
    let fixture = fixture(true);
    let (persisted, tip, sibling) = (state("1r1"), state("2r1"), state("1r2"));
    fixture.persist_base(BLOCK0, persisted);
    fixture.persist_base(persisted, tip);
    fixture.persist_base(BLOCK0, sibling);
    fixture
        .persistence
        .inner()
        .create_write_batch(&BLOCK0, &persisted, flatdb_core::kv::WriteFlags::NONE)
        .unwrap()
        .commit()
        .unwrap();
    fixture.persistence_manager.reset_persisted_state_id().unwrap();
    fixture.repository.set_last_committed_state_id(tip);
    fixture.host.set_finalized_block_number(2);
    fixture
        .host
        .set_finalized_state_root_at(1, if root_matches { persisted.state_root } else { [0xAA; 32] });
    fixture.host.set_finalized_state_root_at(2, tip.state_root);

    fixture.persistence_manager.add_to_persistence(&tip).unwrap();

    assert!(fixture.repository.has_base_persisted_snapshot(&persisted));
    assert!(!fixture.repository.has_base_persisted_snapshot(&sibling));
}

/// `AddToPersistence_ReusesVerifiedAncestryUntilReorg`: a locally committed ancestor outranks a lagging canonical root
/// and stays verified across passes until a reorg moves the head to another branch.
#[rstest]
fn add_to_persistence_reuses_verified_ancestry_until_a_reorg(
    #[values(false, true)] reorg: bool,
    #[values(false, true)] orphan_root_above_parent: bool,
) {
    let fixture = fixture(true);
    // Same-height states enumerate by root, so one variant meets the orphan after the parent invalidated the height.
    let (parent_root, orphan_root) = if orphan_root_above_parent { (1, 3) } else { (3, 1) };
    let (parent, orphan, tip) = (state_id(1, parent_root), state_id(1, orphan_root), state("2r1"));
    fixture.persist_base(BLOCK0, parent);
    fixture.persist_base(parent, tip);
    fixture.persist_base(BLOCK0, orphan);
    fixture.repository.set_last_committed_state_id(tip);
    fixture.host.set_finalized_block_number(2);
    fixture.host.set_finalized_state_root_at(1, [0xAA; 32]);
    fixture.host.set_finalized_state_root_at(2, tip.state_root);
    fixture.persistence_manager.add_to_persistence(&tip).unwrap();
    assert!(
        fixture.repository.has_base_persisted_snapshot(&parent),
        "a locally committed ancestor outranks a lagging canonical root"
    );
    assert!(
        !fixture.repository.has_base_persisted_snapshot(&orphan),
        "a sibling at the same height is still pruned"
    );

    let next = if reorg {
        let (alternative_parent, next) = (state("1r2"), state("2r2"));
        fixture.persist_base(BLOCK0, alternative_parent);
        fixture.persist_base(alternative_parent, next);
        fixture
            .host
            .set_finalized_state_root_at(1, alternative_parent.state_root);
        fixture.host.set_finalized_state_root_at(2, next.state_root);
        next
    } else {
        let next = state("3r1");
        fixture.persist_base(tip, next);
        next
    };
    fixture.repository.set_last_committed_state_id(next);
    // The first pass after a reorg only invalidates the stale cached root at the finalized tip.
    fixture.persistence_manager.add_to_persistence(&next).unwrap();
    fixture.persistence_manager.add_to_persistence(&next).unwrap();

    assert_eq!(fixture.repository.has_base_persisted_snapshot(&parent), !reorg);
    assert_eq!(fixture.repository.has_base_persisted_snapshot(&tip), !reorg);
}

/// `DetermineSnapshotAction_FinalityStalled_OverByteBudget_ConversionRespectsInMemoryFloor`: over the budget, a
/// conversion is preferred once the in-memory bases pass `MaxInMemoryBaseSnapshotCount`; at it the window is kept
/// and the budget persists the next snapshot instead.
#[rstest]
#[case::above_the_in_memory_floor_prefers_conversion(0, true)]
#[case::at_the_in_memory_floor_keeps_the_window_and_persists(1, false)]
fn over_the_byte_budget_conversion_respects_the_in_memory_floor(
    #[case] max_in_memory_base_snapshot_count: u64,
    #[case] expect_conversion: bool,
) {
    let fixture = fixture(true);
    let persistence_manager =
        fixture.persistence_manager_with(long_finality_config(1, max_in_memory_base_snapshot_count));
    fixture.add(BLOCK0, state("16"), false);
    fixture.host.set_finalized_block_number(5);

    let decided = persistence_manager
        .determine_snapshot_action(&state("100"))
        .unwrap()
        .unwrap();

    let kind = if expect_conversion { "base" } else { "memory" };
    assert_eq!(decision(&decided), (kind, BLOCK0, state("16")));
}

/// `DetermineSnapshotAction_FinalityStalled_OverByteBudget_NeverPersistsPersistedTierCandidate`: the budget never
/// persists a snapshot of the tier, which holds none of the bytes it counts, and releases the candidate.
#[test]
fn over_the_byte_budget_a_persisted_tier_candidate_is_never_persisted() {
    let fixture = fixture(true);
    let persistence_manager = fixture.persistence_manager_with(long_finality_config(1, 160));
    let persisted = fixture.persist_base(BLOCK0, state("16"));
    fixture.add(state("16"), state("32"), false);
    fixture.host.set_finalized_block_number(5);
    let leases_before = Arc::strong_count(&persisted);

    assert!(
        persistence_manager
            .determine_snapshot_action(&state("100"))
            .unwrap()
            .is_none()
    );
    assert_eq!(
        Arc::strong_count(&persisted),
        leases_before,
        "the rejected persisted-tier candidate must be released"
    );
}

/// `TryFindSnapshotToConvert_PrefersBoundaryCompactedOverBase`, `DetermineSnapshotAction_FinalizedNoInMemory_
/// FallsBackToPersistedSnapshot` and the gate of Phase 2: a `CompactSize`-wide compacted snapshot wins over an older
/// base, a persisted base is persisted when the finalized walk reaches only it, and with long finality off nothing is
/// converted.
#[rstest]
#[case::boundary_compacted_wins_over_a_base(true, "0-1 0=16", 0, Some(("compacted", "0", "16")))]
#[case::a_narrower_compacted_is_no_boundary(true, "0-1 0=8", 0, Some(("base", "0", "1")))]
#[case::a_base_whose_parent_is_not_on_disk_waits(true, "1-2", 0, None)]
#[case::finalized_walk_reaching_the_tier_persists_from_it(true, "", 16, Some(("persisted", "0", "16")))]
#[case::long_finality_off_converts_nothing(false, "0-1", 0, None)]
fn determine_snapshot_action_with_the_persisted_tier(
    #[case] long_finality: bool,
    #[case] edges: &str,
    #[case] finalized_block: u64,
    #[case] expected: Option<(&str, &str, &str)>,
) {
    let fixture = fixture(true);
    let persistence_manager = fixture.persistence_manager_with(if long_finality {
        long_finality_config(0, 0)
    } else {
        PersistenceManagerConfig {
            long_finality: None,
            ..PM_CONFIG_LF
        }
    });
    fixture.add_edges(edges);
    if finalized_block > 0 {
        fixture.persist_base(BLOCK0, state("16"));
        fixture.host.set_finalized_block_number(finalized_block);
        fixture
            .host
            .set_finalized_state_root_at(finalized_block, state("16").state_root);
    }

    let decided = persistence_manager.determine_snapshot_action(&state("100")).unwrap();

    assert_eq!(
        decided.as_ref().map(decision),
        expected.map(|(kind, from, to)| (kind, state(from), state(to)))
    );
    if !long_finality {
        assert_eq!(fixture.repository.persisted_snapshot_count(), 0);
    }
}

/// `ConvertCompactedRange_BoundaryCompacted_RemovesOnlyConvertedStates_PreservingOutsider`: the bases of the range whose
/// parent is on disk or converted with them are converted and removed from both in-memory tiers, and only they; their
/// states go to the persisted compactor with the persisted block, and their nodes are queued, in block order.
#[test]
fn converting_a_compacted_range_removes_only_the_converted_states() {
    let fixture = fixture(true);
    let (compacted_from, compacted_to) = (state("2"), state("18"));
    let (base_a, base_b, outsider) = (state("5"), state("10"), state("1"));
    fixture.persist_base(BLOCK0, compacted_from);
    let compacted = fixture.add(compacted_from, compacted_to, true);
    fixture.add(compacted_from, base_a, false);
    fixture.add(base_a, base_b, false);
    fixture.add(base_b, compacted_to, false);
    fixture.add(BLOCK0, outsider, false);
    fixture.events.drain(usize::MAX);

    fixture.persistence_manager.convert_compacted_range(&compacted).unwrap();

    let in_memory = |state: StateId, tier| fixture.repository.try_lease_in_memory_state(&state, tier).is_some();
    assert!(
        fixture.repository.has_state(&outsider),
        "a state below the range survives"
    );
    assert!(in_memory(outsider, SnapshotTier::InMemoryBase));
    for converted in [base_a, base_b, compacted_to] {
        assert!(!in_memory(converted, SnapshotTier::InMemoryBase), "{converted}");
        assert!(
            fixture.repository.has_base_persisted_snapshot(&converted),
            "{converted}"
        );
    }
    assert!(
        !in_memory(compacted_to, SnapshotTier::InMemoryCompacted),
        "the boundary compacted is removed"
    );
    assert_eq!(
        fixture.tier().recorder().batches(),
        [(vec![base_a, base_b, compacted_to], 0)]
    );
    let converted: Vec<(EventKind, StateId, StateId)> = fixture
        .events
        .drain(usize::MAX)
        .iter()
        .map(|event| (event.kind, event.from, event.to))
        .collect();
    assert_eq!(
        converted,
        [
            (EventKind::Converted, compacted_from, base_a),
            (EventKind::Converted, base_a, base_b),
            (EventKind::Converted, base_b, compacted_to),
        ]
    );
}

/// `ConvertCompactedRange_ForkWithInMemoryParent_StaysAssemblable`: a fork whose parent below the range is still in
/// memory is not converted, so its tip still assembles down to the persisted state.
#[test]
fn converting_a_compacted_range_keeps_a_fork_with_an_in_memory_parent_assemblable() {
    let fixture = fixture(true);
    let (fork_point, compacted_from, compacted_to) = (state("15"), state("16"), state("32"));
    let (fork_parent, fork_tip) = (state("16r1"), state("17r1"));
    fixture.persist_base(BLOCK0, fork_point);
    fixture.persist_base(fork_point, compacted_from);
    let compacted = fixture.add(compacted_from, compacted_to, true);
    fixture.add(compacted_from, state("17"), false);
    fixture.add(fork_point, fork_parent, false);
    fixture.add(fork_parent, fork_tip, false);

    fixture.persistence_manager.convert_compacted_range(&compacted).unwrap();

    assert!(
        fixture
            .repository
            .assemble_snapshots(&fork_tip, &BLOCK0, 4)
            .snapshot_count()
            > 0,
        "the fork tip must still assemble down to the persisted state"
    );
    assert!(!fixture.repository.has_base_persisted_snapshot(&fork_tip));
}

/// `AddToPersistence_RepeatedForks_ReleasesConvertedCompactedSnapshots`: forks converted one after another release their
/// compacted snapshots before the hand-off to the persisted compactor, a reader of one still reading it, and the
/// persisted bases still cover the newest fork.
#[rstest]
fn converting_repeated_forks_releases_their_compacted_snapshots(#[values(1, 8)] fork_count: u8) {
    let fixture = fixture(true);
    // Keep the conversion threshold reached while each fork stays below a full compaction boundary.
    for i in 0..160u8 {
        fixture.add(BLOCK0, state_id(3, i), false);
    }
    let compactor = SnapshotCompactor::new(fixture.schedule, Arc::clone(&fixture.repository));
    let latest = Arc::new(parking_lot::Mutex::new(BLOCK0));
    let compacted_at_hand_off = Arc::new(parking_lot::Mutex::new(Vec::new()));
    {
        let (latest, compacted_at_hand_off) = (latest.clone(), compacted_at_hand_off.clone());
        let repository = Arc::clone(&fixture.repository);
        fixture.tier().recorder().set_on_enqueue(move |batch| {
            if batch[0] == *latest.lock() {
                compacted_at_hand_off.lock().push(repository.compacted_snapshot_count());
            }
        });
    }

    let mut converted = Vec::new();
    for i in 1..=fork_count {
        let setup = state_id(1, i);
        *latest.lock() = state_id(2, i);
        let setup_account = Account::new(1, U256::from_u64(u64::from(i)));
        add_with_account_b(&fixture, BLOCK0, setup, setup_account);
        fixture.add(setup, *latest.lock(), false);

        let tip = *latest.lock();
        assert!(compactor.do_compact_snapshot(&tip));
        let compacted = fixture
            .repository
            .try_lease_in_memory_state(&tip, SnapshotTier::InMemoryCompacted)
            .unwrap();
        converted.push(Arc::downgrade(&compacted));
        fixture.persistence_manager.add_to_persistence(&tip).unwrap();
        assert_eq!(
            compacted.try_get_account(&address(0xB2)),
            Some(Some(setup_account)),
            "an active reader must survive conversion"
        );
    }

    let latest = *latest.lock();
    let assembled = fixture.repository.assemble_snapshots(&latest, &BLOCK0, 2);
    assert_eq!(fixture.repository.snapshot_count(), 160);
    assert_eq!(
        fixture.repository.compacted_snapshot_count(),
        0,
        "converted forks must not retain compacted snapshots"
    );
    assert_eq!(
        *compacted_at_hand_off.lock(),
        vec![0; usize::from(fork_count)],
        "compacted snapshots must be released before the potentially blocking hand-off"
    );
    assert_eq!((assembled.in_memory.len(), assembled.persisted.len()), (0, 2));
    assert!(
        converted.iter().all(|compacted| compacted.upgrade().is_none()),
        "compacted data must be released after the last reader exits"
    );
}

/// `AddToPersistence_InMemoryPersist_PrunesPersistedTier` and `AddToPersistence_TierSourcePersist_PrunesPersistedTier`:
/// a persist, of an in-memory snapshot or of the tier's, prunes the tier below the new persisted block.
#[rstest]
fn a_persist_prunes_the_persisted_tier_below_it(#[values(false, true)] from_the_tier: bool) {
    let fixture = fixture(true);
    let target = state("16");
    if from_the_tier {
        fixture.persist_base(BLOCK0, target);
    } else {
        fixture.add(BLOCK0, target, true);
    }
    let stale = state("8");
    fixture.persist_base(BLOCK0, stale);
    fixture.host.set_finalized_block_number(16);
    fixture.host.set_finalized_state_root_at(16, target.state_root);

    fixture.persistence_manager.add_to_persistence(&state("100")).unwrap();

    assert!(!fixture.repository.has_base_persisted_snapshot(&stale));
    assert_eq!(fixture.persistence.batches(), [(BLOCK0, target)]);
    assert_eq!(fixture.db_state(), target);
}

/// `FlushToPersistence_WhenBacklogIsPersistedOnly_DrainsConvertedChunks` and `FlushToPersistence_PersistedOnlyTier_
/// WalksAndPrunes`: a flush reaches a backlog only the tier holds, through the committed head or the tier's tips, and
/// prunes what it supersedes.
#[rstest]
#[case::drains_converted_chunks(&[("0", "16"), ("16", "32")], Some("32"), "32", &[("0", "16"), ("16", "32")], &[])]
#[case::walks_and_prunes(&[("0", "8"), ("0", "16")], None, "16", &[("0", "16")], &["8"])]
fn flush_to_persistence_drains_a_backlog_only_the_tier_holds(
    #[case] bases: &[(&str, &str)],
    #[case] committed: Option<&str>,
    #[case] expected: &str,
    #[case] batches: &[(&str, &str)],
    #[case] pruned: &[&str],
) {
    let fixture = fixture(true);
    for (from, to) in bases {
        fixture.persist_base(state(from), state(to));
    }
    if let Some(committed) = committed {
        fixture.repository.set_last_committed_state_id(state(committed));
    }

    assert_eq!(
        fixture.persistence_manager.flush_to_persistence().unwrap(),
        state(expected)
    );

    assert_eq!(
        fixture.persistence.batches(),
        batches
            .iter()
            .map(|(from, to)| (state(from), state(to)))
            .collect::<Vec<_>>()
    );
    for stale in pruned {
        assert!(!fixture.repository.has_base_persisted_snapshot(&state(stale)));
    }
    assert_eq!(
        fixture.repository.persisted_snapshot_count(),
        1,
        "only the snapshots ending below the persisted block are pruned"
    );
}

/// Writing a snapshot of the tier puts in the persistence exactly what writing it from memory does, in every column:
/// a self-destruct of an existing account but not of a new one, accounts and their deletion, slots, deleted slots and
/// zero ones, and the trie nodes but no placeholder; its entry in the persisted node queue lists no node.
#[test]
fn persisting_from_the_tier_writes_what_persisting_from_memory_writes() {
    let content = |content: &mut SnapshotContentBuilder| {
        content.set_self_destruct(address(0xA1), false);
        content.set_self_destruct(address(0xB2), true);
        content.set_account(address(0xA1), Some(account(100)));
        content.set_account(address(0xB2), Some(Account::new(2, slot(200))));
        content.set_account(address(0xC3), None);
        content.set_storage(address(0xA1), slot(1), Some(slot(42)));
        content.set_storage(address(0xA1), slot(2), None);
        content.set_storage(address(0xB2), slot(3), Some(U256::ZERO));
        content.set_state_node(TreePath::from_hex(""), [0x11; 32], &[0xC2, 0x80, 0x80]);
        content.set_state_node(TreePath::from_hex("1"), [0x12; 32], &[]);
        content.set_state_node(TreePath::from_hex("12345678abcdef0123"), [0x13; 32], &[0xC1, 0x80]);
        content.set_storage_node(
            account_path(&address(0xA1)),
            TreePath::from_hex("2"),
            [0x21; 32],
            &[0xC1, 0x80],
        );
        content.set_storage_node(account_path(&address(0xA1)), TreePath::from_hex("3"), [0x22; 32], &[]);
    };
    // Storage both self-destructed accounts had before, which only the self-destruct of the existing one clears.
    let seed = |content: &mut SnapshotContentBuilder| {
        content.set_storage(address(0xA1), slot(9), Some(slot(90)));
        content.set_storage(address(0xB2), slot(9), Some(slot(91)));
    };
    let (from_memory, from_tier) = (fixture(false), fixture(true));
    for fixture in [&from_memory, &from_tier] {
        assert!(
            fixture
                .repository
                .try_add(snapshot(BLOCK0, state("1"), seed), SnapshotTier::InMemoryBase)
        );
        fixture.repository.add_state_id(state("1"));
        assert_eq!(fixture.persistence_manager.flush_to_persistence().unwrap(), state("1"));
    }
    assert!(
        from_memory
            .repository
            .try_add(snapshot(state("1"), state("2"), content), SnapshotTier::InMemoryBase)
    );
    from_memory.repository.add_state_id(state("2"));
    from_tier
        .tier()
        .tier
        .loader
        .convert_and_register(&snapshot(state("1"), state("2"), content))
        .unwrap();
    from_tier.events.drain(usize::MAX);

    for fixture in [&from_memory, &from_tier] {
        assert_eq!(fixture.persistence_manager.flush_to_persistence().unwrap(), state("2"));
    }

    for column in Column::ALL {
        assert_eq!(
            column_entries(from_tier.persistence.inner().kv(), column),
            column_entries(from_memory.persistence.inner().kv(), column),
            "{}",
            column_name(column)
        );
    }
    let reader = from_tier.persistence.create_reader(ReaderFlags::NONE).unwrap();
    assert_eq!(reader.try_get_slot(&address(0xA1), &slot(9)).unwrap(), None);
    assert_eq!(reader.try_get_slot(&address(0xB2), &slot(9)).unwrap(), Some(slot(91)));
    let queued = from_tier.events.drain(usize::MAX);
    assert_eq!(queued.len(), 1);
    assert_eq!(
        (queued[0].kind, queued[0].from, queued[0].to, queued[0].nodes.len()),
        (EventKind::Persisted, state("1"), state("2"), 0)
    );
}

/// `PersistenceManagerPersistedTests.ConvertToPersistedSnapshot_PersistsViaManager` and `PrunePersistedSnapshots_
/// RemovesOldSnapshots`: a converted base is indexed and leasable, and pruning removes the snapshots ending below the
/// block.
#[test]
fn converted_bases_are_indexed_and_pruned_below_a_block() {
    let fixture = fixture(true);
    let (s1, s3, s6) = (named(1, "1"), named(3, "3"), named(6, "6"));
    fixture.persist_base(BLOCK0, s1);
    assert_eq!(fixture.repository.persisted_snapshot_count(), 1);
    assert!(
        fixture
            .repository
            .try_lease_persisted_state(&s1, SnapshotTier::PersistedBase)
            .is_some()
    );
    fixture.persist_base(s1, s3);
    fixture.persist_base(s3, s6);
    assert_eq!(fixture.repository.persisted_snapshot_count(), 3);

    fixture.repository.remove_persisted_states_until(5).unwrap();

    assert_eq!(fixture.repository.persisted_snapshot_count(), 1);
    assert!(fixture.repository.has_base_persisted_snapshot(&s6));
}

/// Dropping the states the head does not reach drops them from the persisted tier too: a persisted side branch goes,
/// the head's persisted ancestry stays.
#[test]
fn drop_state_not_reachable_from_drops_persisted_side_branches() {
    let fixture = fixture(true);
    fixture.persist_base(BLOCK0, state("1"));
    fixture.persist_base(state("1"), state("2"));
    fixture.persist_base(state("1"), state("2r1"));
    fixture.persist_base(state("2r1"), state("3r1"));
    fixture.add(state("2"), state("3"), false);

    fixture
        .persistence_manager
        .drop_state_not_reachable_from(&state("3"))
        .unwrap();

    for kept in ["1", "2", "3"] {
        assert!(fixture.repository.has_state(&state(kept)), "{kept}");
    }
    for dropped in ["2r1", "3r1"] {
        assert!(!fixture.repository.has_state(&state(dropped)), "{dropped}");
    }
}

/// Process exit fails a conversion as cancelled: a range converts no base once it is signalled, and a single base,
/// which C# converts without checking, fails at the hand-off to the persisted compactor, which is told.
#[rstest]
fn process_exit_cancels_a_conversion(#[values(false, true)] compacted_range: bool) {
    let fixture = fixture(true);
    let persistence_manager = fixture.persistence_manager_with(long_finality_config(0, 0));
    let edges: Vec<String> = (0..16).map(|block| format!("{block}-{}", block + 1)).collect();
    fixture.add_edges(&edges.join(" "));
    if compacted_range {
        fixture.add_edges("0=16");
    }
    persistence_manager.signal_exit();

    assert_eq!(
        persistence_manager.add_to_persistence(&state("16")),
        Err(PersistError::Cancelled)
    );
    let converted = fixture.repository.has_base_persisted_snapshot(&state("1"));
    let in_memory = fixture
        .repository
        .try_lease_in_memory_state(&state("1"), SnapshotTier::InMemoryBase)
        .is_some();
    assert_eq!((converted, in_memory), (!compacted_range, compacted_range));
    assert_eq!(
        fixture.repository.persisted_snapshot_count(),
        i64::from(!compacted_range)
    );
    assert_eq!(
        fixture.tier().recorder().exit_signals(),
        1,
        "the persisted compactor is told"
    );
    assert!(fixture.tier().recorder().batches().is_empty());
}

/// A catalog failing the put, or with `fail_removal` the removal, of the row keyed `faulty_key` once with `failure`.
struct OnceFailingCatalog {
    rows: MemCatalogKv,
    faulty_key: [u8; CATALOG_KEY_SIZE],
    fail_removal: bool,
    failure: parking_lot::Mutex<Option<KvError>>,
}

impl OnceFailingCatalog {
    fn fail_once(&self, key: &[u8], removal: bool) -> KvResult<()> {
        match self
            .failure
            .lock()
            .take_if(|_| key == self.faulty_key && removal == self.fail_removal)
        {
            Some(failure) => Err(failure),
            None => Ok(()),
        }
    }
}

impl CatalogKv for OnceFailingCatalog {
    fn put(&self, key: &[u8], value: &[u8]) -> KvResult<()> {
        self.fail_once(key, false)?;
        self.rows.put(key, value)
    }

    fn key_exists(&self, key: &[u8]) -> KvResult<bool> {
        self.rows.key_exists(key)
    }

    fn remove(&self, key: &[u8]) -> KvResult<()> {
        self.fail_once(key, true)?;
        self.rows.remove(key)
    }
}

/// A long-finality fixture whose catalog fails the put, or with `fail_removal` the removal, of the row of the base from
/// `from` to `to` once with `failure`.
fn fixture_failing_catalog_row(from: StateId, to: StateId, fail_removal: bool, failure: &KvError) -> Fixture {
    let failure = failure.clone();
    let faulty_key = catalog_key(&to, depth(&from, &to));
    let options = FixtureOptions {
        wrap_catalog: Arc::new(move |rows| {
            Box::new(OnceFailingCatalog {
                rows,
                faulty_key,
                fail_removal,
                failure: parking_lot::Mutex::new(Some(failure.clone())),
            })
        }),
        ..FixtureOptions::default()
    };
    Fixture::with_options(Backend::Mem, PM_CONFIG_LF, Some(BLOCK0), options).unwrap()
}

/// A conversion range failing on one base (D-14): the bases converted before the failure queue their Converted entries
/// in block order and are timed as `PersistedSnapshotConvertTime`, but nothing leaves memory or reaches the persisted
/// compactor. A fatal catalog error is handed out once and fails every later persist; after another one the next persist
/// converts the whole range.
#[rstest]
#[case::corruption("Corruption: block checksum mismatch", true)]
#[case::busy("Busy: ", false)]
fn a_failed_conversion_keeps_its_range_in_memory(#[case] message: &str, #[case] fatal: bool) {
    let failure = KvError::rocks(message.to_owned());
    let fixture = fixture_failing_catalog_row(state("8"), state("9"), false, &failure);
    let persistence_manager = fixture.persistence_manager_with(long_finality_config(0, 0));
    let bases: Vec<StateId> = (1..=16).map(|block: u64| state(&block.to_string())).collect();
    let edges: Vec<String> = (0..16).map(|block| format!("{block}-{}", block + 1)).collect();
    fixture.add_edges(&edges.join(" "));
    fixture.add_edges("0=16");
    fixture.events.drain(usize::MAX);
    fixture.repository.stats().drain_observations(usize::MAX);
    let expected = PersistError::Persistence(PersistenceError::Catalog(failure));

    assert_eq!(
        persistence_manager.add_to_persistence(&state("16")),
        Err(expected.clone())
    );

    let converted: Vec<StateId> = bases
        .iter()
        .copied()
        .filter(|base| fixture.repository.has_base_persisted_snapshot(base))
        .collect();
    assert!(
        converted.starts_with(&bases[..8]) && !converted.contains(&state("9")),
        "every base pulled before the failed one converts, the failed one does not: {converted:?}"
    );
    let events: Vec<(EventKind, StateId, StateId)> = fixture
        .events
        .drain(usize::MAX)
        .iter()
        .map(|event| (event.kind, event.from, event.to))
        .collect();
    let expected_events: Vec<(EventKind, StateId, StateId)> = converted
        .iter()
        .map(|to| (EventKind::Converted, state(&(to.block_number - 1).to_string()), *to))
        .collect();
    assert_eq!(events, expected_events);
    let convert_times = fixture
        .repository
        .stats()
        .drain_observations(usize::MAX)
        .iter()
        .filter(|observation| observation.metric == Histogram::PersistedSnapshotConvertTime as u16)
        .count();
    assert_eq!(convert_times, converted.len());
    for in_memory in bases
        .iter()
        .map(|base| (base, SnapshotTier::InMemoryBase))
        .chain([(&state("16"), SnapshotTier::InMemoryCompacted)])
    {
        assert!(
            fixture
                .repository
                .try_lease_in_memory_state(in_memory.0, in_memory.1)
                .is_some(),
            "{in_memory:?}"
        );
    }
    assert!(fixture.tier().recorder().batches().is_empty());

    let retried = persistence_manager.add_to_persistence(&state("16"));
    if fatal {
        assert_eq!(retried, Err(expected.clone()));
        assert_eq!(
            (
                persistence_manager.take_fatal_error(),
                persistence_manager.take_fatal_error()
            ),
            (Some(expected), None)
        );
        assert!(fixture.tier().recorder().batches().is_empty());
    } else {
        assert_eq!(retried, Ok(()));
        assert_eq!(persistence_manager.take_fatal_error(), None);
        assert_eq!(fixture.tier().recorder().batches(), [(bases, 0)]);
    }
}

/// A fatal catalog error while dropping what the head does not reach is handed out once and fails every later persist;
/// after another one the drop can be repeated.
#[rstest]
#[case::io_error("IO error: No space left on device", true)]
#[case::busy("Busy: ", false)]
fn a_fatal_catalog_failure_while_dropping_stops_every_later_persist(#[case] message: &str, #[case] fatal: bool) {
    let failure = KvError::rocks(message.to_owned());
    let fixture = fixture_failing_catalog_row(state("2r1"), state("3r1"), true, &failure);
    fixture.persist_base(BLOCK0, state("1"));
    fixture.persist_base(state("1"), state("2"));
    fixture.persist_base(state("1"), state("2r1"));
    fixture.persist_base(state("2r1"), state("3r1"));
    fixture.add(state("2"), state("3"), false);
    let expected = PersistenceError::Catalog(failure);

    assert_eq!(
        fixture.persistence_manager.drop_state_not_reachable_from(&state("3")),
        Err(expected.clone())
    );

    let expected = PersistError::Persistence(expected);
    if fatal {
        assert_eq!(
            (
                fixture.persistence_manager.take_fatal_error(),
                fixture.persistence_manager.take_fatal_error()
            ),
            (Some(expected.clone()), None)
        );
        assert_eq!(
            fixture.persistence_manager.add_to_persistence(&state("3")),
            Err(expected)
        );
    } else {
        assert_eq!(fixture.persistence_manager.take_fatal_error(), None);
        fixture
            .persistence_manager
            .drop_state_not_reachable_from(&state("3"))
            .unwrap();
        assert!(!fixture.repository.has_state(&state("3r1")));
    }
}

/// Only a fatal flat or catalog database error and a failed validation stop every later persist.
#[rstest]
#[case::catalog_corruption(TierError::Catalog(KvError::rocks("Corruption: bad block".to_owned())), true)]
#[case::catalog_busy(TierError::Catalog(KvError::rocks("Busy: ".to_owned())), false)]
#[case::validation(TierError::Validation("Missing account".to_owned()), true)]
#[case::corrupt(TierError::Corrupt("Corrupt table".to_owned()), false)]
#[case::io(TierError::Io(std::io::Error::other("short read")), false)]
fn only_database_failures_and_validation_are_fatal(#[case] error: TierError, #[case] fatal: bool) {
    assert_eq!(PersistError::from(error).is_fatal(), fatal);
}
