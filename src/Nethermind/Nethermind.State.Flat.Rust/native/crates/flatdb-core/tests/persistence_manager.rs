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
use common::manager::{BLOCK0, Fixture, Op, PM_CONFIG, account, logged_warnings, state};
use common::snapshots::{address, snapshot};
use flatdb_core::compactor::SnapshotCompactor;
use flatdb_core::events::{PersistedNode, PersistedNodes};
use flatdb_core::host::HostError;
use flatdb_core::keccak::account_path;
use flatdb_core::kv::{KvError, ReadFlags};
use flatdb_core::persistence::{Persistence, PersistenceError, PersistenceReader, PersistenceWriteBatch, ReaderFlags};
use flatdb_core::persistence_manager::{PersistError, PersistenceManagerConfig};
use flatdb_core::snapshot::{Snapshot, SnapshotTier};
use flatdb_core::stats::{Histogram, HistogramLabel, Observation};
use flatdb_core::types::{Account, StateId, TreePath, U256};
use rstest::rstest;

fn fixture() -> Fixture {
    Fixture::new(Backend::Mem, PM_CONFIG, Some(BLOCK0)).unwrap()
}

/// The `(from, to)` of a snapshot.
fn span(snapshot: &Snapshot) -> (StateId, StateId) {
    (snapshot.from(), snapshot.to())
}

fn byte_budget_config(max_in_memory_snapshot_bytes: u64) -> PersistenceManagerConfig {
    PersistenceManagerConfig {
        min_reorg_depth: 64,
        max_reorg_depth: 256,
        max_in_memory_snapshot_bytes,
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
) {
    let fixture = fixture();
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
        candidate.as_deref().map(span),
        expected.map(|(from, to)| (state(from), state(to)))
    );
    if let Some(candidate) = candidate {
        assert!(
            latest_block - candidate.to().block_number >= PM_CONFIG.min_reorg_depth
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

    assert_eq!(candidate.as_deref().map(span), Some((StateId::PRE_GENESIS, BLOCK0)));
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
) {
    let fixture = fixture();
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

    assert_eq!(candidate.as_deref().map(span), Some((BLOCK0, state(expected_to))));
}

/// `DetermineSnapshotAction_BackstopRaisedAboveMinReorgDepth`, `DetermineSnapshotAction_FinalizedGatePassesButSeed
/// Missing_BackstopStillForcesPersist` and `DetermineSnapshotAction_LongFinalityDisabled_SkipsConversionPath`: the
/// backstop is `max(MaxReorgDepth, MinReorgDepth + CompactSize)` and fires even when the finalized trigger ran.
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
) {
    let fixture = fixture();
    let persistence_manager = fixture.persistence_manager_with(PersistenceManagerConfig {
        min_reorg_depth,
        max_reorg_depth,
        max_in_memory_snapshot_bytes: 0,
    });
    fixture.host.set_finalized_block_number(finalized_block);
    fixture.add_edges(edges);

    let candidate = persistence_manager
        .determine_snapshot_action(&state(&latest_block.to_string()))
        .unwrap();

    assert_eq!(candidate.is_some(), expect_persist);
    if let Some(candidate) = candidate {
        assert_eq!(candidate.from(), BLOCK0);
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
    let fixture = fixture();
    let persistence_manager = fixture.persistence_manager_with(byte_budget_config(byte_budget));
    fixture.host.set_finalized_block_number(16);
    fixture.host.set_finalized_state_root_at(16, state("16").state_root);
    fixture.add_edges("0=16");
    assert!(fixture.repository.in_memory_bytes() > 0);

    let candidate = persistence_manager
        .determine_snapshot_action(&state(&latest_block.to_string()))
        .unwrap();

    assert_eq!(
        candidate.as_deref().map(span),
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
    let fixture = fixture();
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
        candidate.as_deref().map(span),
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

/// `DetermineSnapshotAction_WhenBackstopHasNoCandidate_WarnsOnlyIfConversionAlsoFails` with conversion off: the stall
/// is reported once per persisted state.
#[test]
fn determine_snapshot_action_warns_once_when_the_backstop_finds_nothing() {
    logged_warnings();
    let fixture = fixture();
    let persistence_manager = fixture.persistence_manager_with(PersistenceManagerConfig {
        min_reorg_depth: 0,
        max_reorg_depth: 32,
        max_in_memory_snapshot_bytes: 0,
    });
    // Too wide to persist, at a root no other test uses.
    let head = state("64r77");
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

    assert!(persistence_manager.determine_snapshot_action(&head).unwrap().is_none());
    assert_eq!(stall_warnings(), 1);
    assert!(persistence_manager.determine_snapshot_action(&head).unwrap().is_none());
    assert_eq!(stall_warnings(), 1, "warned once per persisted state");
}

/// `SnapshotRepository_InMemoryBytes_ReturnsToZeroAfterRemove`, with the `Metrics` gauges the repository moves.
#[rstest]
fn in_memory_bytes_and_gauges_return_to_zero_after_remove(#[values(false, true)] compacted: bool) {
    let fixture = fixture();
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
#[test]
fn persist_snapshot_of_an_empty_snapshot_creates_a_write_batch() {
    let fixture = fixture();

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
fn persist_snapshot_writes_nothing_when_it_fails(#[values(false, true)] fail_the_flush: bool) {
    let fixture = fixture();
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
fn a_fatal_error_stops_every_later_persist(#[case] message: &str, #[case] fatal: bool) {
    let fixture = fixture();
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
#[test]
fn add_to_persistence_drains_at_most_four_snapshots_per_call() {
    let fixture = fixture();
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
#[test]
fn add_to_persistence_removes_orphaned_forks_before_the_batch() {
    let fixture = fixture();
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
) {
    let fixture = fixture();
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
#[test]
fn reset_persisted_state_id_rereads_the_persistence() {
    let fixture = fixture();
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
