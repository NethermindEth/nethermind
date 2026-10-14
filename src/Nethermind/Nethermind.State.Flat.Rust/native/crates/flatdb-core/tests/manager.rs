//! The manager: port of `FlatDbManagerTests` and `FlatDbManagerPersistedTests` over the
//! real repository, compactor and persistence manager with a fake host, and a stress test of adds, gathers, flushes and
//! drops from many threads checked against a model of the chain.
//!
//! The C# tests mock the persistence manager and the repository; here their effects are observed instead: the readers
//! the recording persistence created, the bundles' identity, the host's reads and the persisted state.
//!
//! With long finality, the persisted tier is real, in a temporary directory: the `FlatDbManagerPersistedTests` and the
//! manager cases of `LongFinalityIntegrationTests`, shutdown with the persisted compactor busy, its fatal catalog
//! failures, and the next session opening what shutdown left.
//!
//! Not ported: the history cases (`*_below_barrier_*`, `*_floor_*`, `HistoricalFlatDbManager`), which are out of scope.

mod common;

use std::collections::{HashMap, HashSet};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering};
use std::thread;
use std::time::{Duration, Instant};

use common::any_kv::Backend;
use common::manager::{
    BLOCK0, FakeHost, Fixture, FixtureOptions, PM_CONFIG, PM_CONFIG_LF, TestPersistence, WrapCatalog, account,
    decision, logged_warnings, state, tier_settings,
};
use common::persisted::pack_rows;
use common::snapshots::{address, read_slot, snapshot};
use flatdb_core::bundle::ReadSource;
use flatdb_core::compactor::{Compactor, SnapshotCompactor};
use flatdb_core::host::HostError;
use flatdb_core::keccak::account_path;
use flatdb_core::kv::ReadFlags;
use flatdb_core::kv::{KvError, KvResult};
use flatdb_core::manager::{AddSnapshotOutcome, FlatDbManager, ManagerConfig, ManagerError};
use flatdb_core::persisted::storage::catalog::{CatalogKv, MemCatalogKv};
use flatdb_core::persisted::tier::PersistedTier;
use flatdb_core::persistence::PersistenceError;
use flatdb_core::persistence::{Persistence, PersistenceReader, ReaderFlags};
use flatdb_core::persistence_manager::{LongFinalityConfig, PersistError, PersistenceManagerConfig};
use flatdb_core::schedule::CompactionSchedule;
use flatdb_core::snapshot::SnapshotTier;
use flatdb_core::stats::{Histogram, HistogramLabel};
use flatdb_core::types::{Account, Address, EMPTY_TREE_HASH, Hash256, StateId, TreePath, U256};
use parking_lot::{Mutex, RwLock};
use rstest::rstest;

const WAIT_LIMIT: Duration = Duration::from_secs(10);

/// `CompactSize = 16, MaxInFlightCompactJob = 4` of the C# fixture.
fn config(inline_compaction: bool) -> ManagerConfig {
    ManagerConfig {
        max_in_flight_compact_job: 4,
        inline_compaction,
        ..ManagerConfig::default()
    }
}

fn commit(
    manager: &FlatDbManager<TestPersistence>,
    from: StateId,
    to: StateId,
    balance: u64,
) -> Result<AddSnapshotOutcome, ManagerError> {
    manager.add_snapshot(snapshot(from, to, |content| {
        content.set_account(address(0xA1), Some(account(balance)))
    }))
}

const ADDED: AddSnapshotOutcome = AddSnapshotOutcome::Added { stall: None };

/// Polls `condition` until it holds.
fn wait_until(what: &str, condition: impl Fn() -> bool) {
    let deadline = Instant::now() + WAIT_LIMIT;
    while !condition() {
        assert!(Instant::now() < deadline, "timed out waiting until {what}");
        thread::sleep(Duration::from_millis(1));
    }
}

/// `HasStateForBlock_FoundInRepository_ReturnsTrue`, `HasStateForBlock_FoundInPersistence_ReturnsTrue` and
/// `HasStateForBlock_NotFound_ReturnsFalse`.
#[test]
fn has_state_for_block_sees_base_snapshots_and_the_persisted_state() {
    let fixture = Fixture::new(Backend::Mem, PM_CONFIG, Some(state("5"))).unwrap();
    let manager = fixture.manager(config(true));
    fixture.add_edges("5-10 10=12");

    assert!(manager.has_state_for_block(&state("10")).unwrap(), "in the repository");
    assert!(manager.has_state_for_block(&state("5")).unwrap(), "the persisted state");
    assert!(
        !manager.has_state_for_block(&state("12")).unwrap(),
        "only base snapshots count"
    );
    assert!(!manager.has_state_for_block(&state("11")).unwrap());
}

/// `AddSnapshot_BlockBelowPersistedState_ReturnsEarlyAndLogsWarning`, `AddSnapshot_ValidSnapshot_AddsToRepository` and
/// `AddSnapshot_DuplicateSnapshot_DisposesSnapshotAndReturnsResource`: a rejected snapshot is released and never
/// becomes the committed state.
#[rstest]
fn add_snapshot_adds_rejects_and_refuses_duplicates(#[values(false, true)] inline_compaction: bool) {
    let fixture = Fixture::new(Backend::Mem, PM_CONFIG, Some(state("5"))).unwrap();
    let manager = fixture.manager(config(inline_compaction));

    let below = snapshot(state("3"), state("4"), |_| {});
    assert_eq!(
        manager.add_snapshot(Arc::clone(&below)),
        Ok(AddSnapshotOutcome::Rejected)
    );
    assert_eq!(Arc::strong_count(&below), 1, "released");
    assert!(!fixture.repository.has_state(&state("4")));
    assert_eq!(fixture.repository.get_last_committed_state_id(), None);

    assert_eq!(commit(&manager, state("5"), state("6"), 1), Ok(ADDED));
    assert!(fixture.repository.has_state(&state("6")));
    assert_eq!(fixture.repository.get_last_committed_state_id(), Some(state("6")));

    fixture.repository.set_last_committed_state_id(state("99"));
    let duplicate = snapshot(state("5"), state("6"), |_| {});
    assert_eq!(
        manager.add_snapshot(Arc::clone(&duplicate)),
        Ok(AddSnapshotOutcome::Duplicate)
    );
    assert_eq!(Arc::strong_count(&duplicate), 1, "released");
    assert_eq!(fixture.repository.get_last_committed_state_id(), Some(state("99")));
}

/// `AddSnapshot_QueuedCompactionAndPersistence_AreDrainedOnDispose` and `DisposeAsync_DoesNotFlushToPersistence`:
/// shutdown drains both queues, compaction first, but never persists the unfinalized tail.
#[rstest]
fn shutdown_drains_queued_jobs_without_flushing(#[values(false, true)] process_exit_already_signalled: bool) {
    let fixture = Fixture::new(Backend::Mem, PM_CONFIG, Some(state("5"))).unwrap();
    let manager = fixture.manager(config(false));
    if process_exit_already_signalled {
        manager.signal_exit();
    }

    assert_eq!(commit(&manager, state("10"), state("11"), 1), Ok(ADDED));
    manager.shutdown();

    assert_eq!(
        fixture.repository.get_states_at_block_number(11),
        [state("11")],
        "compaction ran"
    );
    assert!(fixture.host.finalized_number_reads() >= 1, "persistence ran");
    assert!(fixture.persistence.batches().is_empty());
    assert_eq!(fixture.db_state(), state("5"));
}

/// `GatherReadOnlySnapshotBundle_CacheClearedPeriodically`.
#[test]
fn gather_shares_bundles_until_the_periodic_clear() {
    let fixture = Fixture::new(Backend::Mem, PM_CONFIG, Some(state("10"))).unwrap();
    let manager = fixture.manager(ManagerConfig {
        bundle_cache_clear_interval: Duration::from_millis(500),
        ..config(true)
    });
    fixture.persistence_manager.get_current_persisted_state_id().unwrap();
    fixture.persistence.clear_ops();

    let first = manager
        .gather_read_only_snapshot_bundle(&state("10"), ReaderFlags::NONE)
        .unwrap();
    let second = manager
        .gather_read_only_snapshot_bundle(&state("10"), ReaderFlags::NONE)
        .unwrap();
    assert!(Arc::ptr_eq(&first, &second));
    assert_eq!(fixture.persistence.reader_flags().len(), 1);

    thread::sleep(Duration::from_millis(1200));
    let third = manager
        .gather_read_only_snapshot_bundle(&state("10"), ReaderFlags::NONE)
        .unwrap();

    assert!(!Arc::ptr_eq(&first, &third));
    assert_eq!(fixture.persistence.reader_flags().len(), 2);
}

/// Adds the snapshot of the next block and waits until its persistence job asked for the finality, by which time every
/// earlier persistence job has completed.
fn add_and_await_persist_job(fixture: &Fixture, manager: &FlatDbManager<TestPersistence>, block: u64) {
    let reads = fixture.host.finalized_number_reads();
    let (from, to) = (state(&(block - 1).to_string()), state(&block.to_string()));
    assert_eq!(
        commit(manager, if block == 1 { BLOCK0 } else { from }, to, block),
        Ok(ADDED)
    );
    wait_until("the persistence job ran", || {
        fixture.host.finalized_number_reads() > reads
    });
}

/// `PersistJob_ClearsReadBundleCacheOnlyWhenSnapshotsAreReleased`: a persistence job clears the cache when the
/// persisted state moved or base snapshots were removed since its last clear.
#[rstest]
#[case::without_persisted_state_advance_or_base_removal_keeps_the_cache(false, false, false)]
#[case::with_persisted_state_advance_clears_the_cache(true, false, true)]
#[case::with_base_snapshot_removal_only_clears_the_cache(false, true, true)]
fn persist_job_clears_the_bundle_cache_only_when_snapshots_are_released(
    #[case] advance_persisted_state: bool,
    #[case] remove_base_snapshot: bool,
    #[case] expect_cleared: bool,
) {
    let fixture = Fixture::new(Backend::Mem, PM_CONFIG, Some(BLOCK0)).unwrap();
    let manager = fixture.manager(config(false));
    // The first job clears: nothing was cleared at the persisted state yet.
    add_and_await_persist_job(&fixture, &manager, 1);
    add_and_await_persist_job(&fixture, &manager, 2);
    fixture.add_edges("0-1r9");

    let gathered = manager
        .gather_read_only_snapshot_bundle(&state("2"), ReaderFlags::NONE)
        .unwrap();
    if advance_persisted_state {
        fixture.persistence_manager.flush_to_persistence().unwrap();
    }
    if remove_base_snapshot {
        fixture
            .repository
            .remove_and_release_in_memory_known_state(&state("1r9"), SnapshotTier::InMemoryBase);
    }
    for block in 3..=5 {
        add_and_await_persist_job(&fixture, &manager, block);
    }

    let again = manager
        .gather_read_only_snapshot_bundle(&state("2"), ReaderFlags::NONE)
        .unwrap();
    assert_eq!(!Arc::ptr_eq(&gathered, &again), expect_cleared);
}

/// `GatherReadOnlySnapshotBundle_FullScanNeverSharesCachedReaders`.
#[rstest]
fn gather_with_reader_flags_never_shares_cached_readers(#[values(false, true)] populate_cache_first: bool) {
    let fixture = Fixture::new(Backend::Mem, PM_CONFIG, Some(state("10"))).unwrap();
    let manager = fixture.manager(config(true));
    fixture.persistence_manager.get_current_persisted_state_id().unwrap();
    fixture.persistence.clear_ops();
    let gather = |flags| manager.gather_read_only_snapshot_bundle(&state("10"), flags).unwrap();
    if populate_cache_first {
        gather(ReaderFlags::NONE);
    }

    let full_scan = gather(ReaderFlags::FULL_SCAN);
    let normal = gather(ReaderFlags::NONE);
    let another_full_scan = gather(ReaderFlags::FULL_SCAN);
    let cached_normal = gather(ReaderFlags::NONE);

    assert!(!Arc::ptr_eq(&full_scan, &normal));
    assert!(!Arc::ptr_eq(&another_full_scan, &full_scan));
    assert!(Arc::ptr_eq(&cached_normal, &normal));
    let created = fixture.persistence.reader_flags();
    assert_eq!(created.iter().filter(|flags| **flags == ReaderFlags::NONE).count(), 1);
    assert_eq!(
        created.iter().filter(|flags| **flags == ReaderFlags::FULL_SCAN).count(),
        2
    );
}

/// `GatherReadOnlySnapshotBundle_orphaned_state_throws_state_not_retained`, the `PreGenesis` sentinel, and the give-up
/// deadline of a gather that keeps finding no chain to the reader's state while the state is still held.
#[test]
fn gather_fails_for_missing_states_and_gives_up_on_a_broken_chain() {
    let fixture = Fixture::new(Backend::Mem, PM_CONFIG, Some(state("20"))).unwrap();
    let manager = fixture.manager(ManagerConfig {
        gather_give_up_deadline: Duration::from_millis(50),
        ..config(true)
    });
    fixture.add_edges("21-22");

    assert_eq!(
        manager
            .gather_read_only_snapshot_bundle(&state("10r10"), ReaderFlags::NONE)
            .err(),
        Some(ManagerError::StateNotRetained(state("10r10")))
    );
    let pre_genesis = manager
        .gather_read_only_snapshot_bundle(&StateId::PRE_GENESIS, ReaderFlags::NONE)
        .unwrap();
    assert_eq!(
        (pre_genesis.snapshot_count(), pre_genesis.reader_state()),
        (0, StateId::new(0, EMPTY_TREE_HASH)),
        "PreGenesis reads an empty state"
    );
    assert_eq!(
        pre_genesis.get_account(&address(0xA1)).unwrap(),
        (None, ReadSource::Persistence)
    );
    match manager.gather_read_only_snapshot_bundle(&state("22"), ReaderFlags::NONE) {
        Err(ManagerError::GatherTimeout {
            state: gathered,
            attempts,
            elapsed,
        }) => {
            assert_eq!(gathered, state("22"));
            assert!(
                attempts > 1 && elapsed > Duration::from_millis(50),
                "{attempts} {elapsed:?}"
            );
        }
        other => panic!("expected a timeout, got {:?}", other.map(|_| ())),
    }
    assert_eq!(fixture.persistence.live_readers(), 0, "every retry released its reader");
}

/// `GatherReadOnlySnapshotBundle_state_persisted_after_reader_lease_is_served_by_fresh_reader`.
#[test]
fn gather_after_a_persist_under_the_leased_reader_retries_with_a_fresh_one() {
    let fixture = Fixture::new(Backend::Mem, PM_CONFIG, Some(state("10r10"))).unwrap();
    let manager = fixture.manager(config(true));
    fixture.persistence_manager.get_current_persisted_state_id().unwrap();
    fixture.persistence.clear_ops();
    fixture.persistence.script_reader_states([state("5r5")]);

    let bundle = manager
        .gather_read_only_snapshot_bundle(&state("10r10"), ReaderFlags::NONE)
        .unwrap();

    assert_eq!(fixture.persistence.reader_flags().len(), 2);
    assert_eq!(
        fixture.persistence.live_readers(),
        1,
        "the stale reader is released, the fresh one kept"
    );
    assert_eq!(bundle.reader_state(), state("10r10"));
}

/// `GatherReadOnlySnapshotBundle_RemainsReadableAfterSourceSnapshotRemoval`, with the bundle metrics.
#[test]
fn gather_remains_readable_after_its_snapshots_are_removed() {
    let fixture = Fixture::new(Backend::Mem, PM_CONFIG, None).unwrap();
    let manager = fixture.manager(config(true));
    let to = state("1r42");
    fixture.add_with_balance(StateId::PRE_GENESIS, to, false, 42);
    fixture.repository.stats().drain_observations(usize::MAX);

    let bundle = manager
        .gather_read_only_snapshot_bundle(&to, ReaderFlags::NONE)
        .unwrap();
    assert_eq!(bundle.get_account(&address(0xA1)).unwrap().0, Some(account(42)));
    assert!(
        fixture
            .repository
            .remove_and_release_in_memory_known_state(&to, SnapshotTier::InMemoryBase)
    );

    assert_eq!(bundle.get_account(&address(0xA1)).unwrap().0, Some(account(42)));
    let stats = fixture.repository.stats();
    assert_eq!(stats.counters.snapshot_bundle_size.load(Ordering::Relaxed), 1);
    let depths: Vec<(u16, f64)> = stats
        .drain_observations(usize::MAX)
        .into_iter()
        .filter(|observation| observation.metric == Histogram::SnapshotBundleBlockNumberDepth as u16)
        .map(|observation| (observation.label, observation.value))
        .collect();
    assert_eq!(
        depths,
        [
            (HistogramLabel::InMemory as u16, 2.0),
            (HistogramLabel::Persisted as u16, 0.0)
        ],
        "PreGenesis -> 1 spans two blocks"
    );
}

/// The slot filter of a shared bundle counts in `InMemorySlotFilterBuilds` and `InMemorySlotFilterMemory` while the
/// bundle lives; a flagged bundle gets none.
#[test]
fn the_slot_filter_of_a_shared_bundle_is_counted_while_it_lives() {
    let fixture = Fixture::new(Backend::Mem, PM_CONFIG, Some(BLOCK0)).unwrap();
    let manager = fixture.manager(config(true));
    fixture.add_edges("0-1 1-2");
    let counters = &fixture.repository.stats().counters;
    let slot = U256::from_u64(1);

    let full_scan = manager
        .gather_read_only_snapshot_bundle(&state("2"), ReaderFlags::FULL_SCAN)
        .unwrap();
    assert_eq!(read_slot(&full_scan, &address(0xA1), &slot, true).unwrap(), None);
    assert_eq!(counters.in_memory_slot_filter_builds.load(Ordering::Relaxed), 0);

    let shared = manager
        .gather_read_only_snapshot_bundle(&state("2"), ReaderFlags::NONE)
        .unwrap();
    assert_eq!(read_slot(&shared, &address(0xA1), &slot, true).unwrap(), None);
    assert_eq!(counters.in_memory_slot_filter_builds.load(Ordering::Relaxed), 1);
    assert!(counters.in_memory_slot_filter_memory.load(Ordering::Relaxed) > 0);

    drop(shared);
    manager.clear_bundle_cache();
    assert_eq!(counters.in_memory_slot_filter_memory.load(Ordering::Relaxed), 0);
    assert_eq!(counters.in_memory_slot_filter_build_failures.load(Ordering::Relaxed), 0);
}

/// `DisposeAsync_CompletesPromptlyAndIsIdempotent`.
#[test]
fn shutdown_is_prompt_and_idempotent() {
    let fixture = Fixture::new(Backend::Mem, PM_CONFIG, Some(BLOCK0)).unwrap();
    let manager = fixture.manager(config(false));
    let started = Instant::now();

    manager.shutdown();
    manager.shutdown();

    assert!(started.elapsed() < Duration::from_secs(2), "{:?}", started.elapsed());
    assert_eq!(commit(&manager, BLOCK0, state("1"), 1), Err(ManagerError::ShutDown));
}

/// Every block persists itself: the byte budget is exceeded by any snapshot and no depth is kept.
const PERSIST_EVERY_BLOCK: PersistenceManagerConfig = PersistenceManagerConfig {
    min_reorg_depth: 0,
    max_reorg_depth: 90_000,
    max_in_memory_snapshot_bytes: 1,
    long_finality: None,
};

/// How the first, held persist ends.
#[derive(Clone, Copy, Debug)]
enum Completion {
    Succeeds,
    IsCancelled,
    Fails,
}

/// `DisposeAsync_WhenPersistenceStops_ReleasesBlockedCompactor`: the first persist is held with the persistence queue
/// full and the compactor waiting for room; shutdown waits for it, then drains everything after a success or a
/// failure, while a cancellation stops the workers and releases the compactor.
#[rstest]
fn shutdown_waits_for_a_held_persist_and_releases_the_blocked_compactor(
    #[values(Completion::Succeeds, Completion::IsCancelled, Completion::Fails)] completion: Completion,
) {
    let fixture = Fixture::new(Backend::Mem, PERSIST_EVERY_BLOCK, Some(BLOCK0)).unwrap();
    let manager = fixture.manager(config(false));
    let held = fixture.host.hold_next_flush();
    let blocked_block = 4 + 2;

    for block in 1..=blocked_block {
        let from = if block == 1 {
            BLOCK0
        } else {
            state(&(block - 1).to_string())
        };
        let outcome = commit(&manager, from, state(&block.to_string()), block);
        assert!(matches!(outcome, Ok(AddSnapshotOutcome::Added { .. })), "{outcome:?}");
        if block == 1 {
            held.started.recv_timeout(WAIT_LIMIT).unwrap();
        }
    }
    wait_until("the compactor took the last job", || {
        !fixture.repository.get_states_at_block_number(blocked_block).is_empty()
    });

    thread::scope(|scope| {
        let shutdown = scope.spawn(|| manager.shutdown());
        thread::sleep(Duration::from_millis(50));
        assert!(!shutdown.is_finished(), "the held persist keeps shutdown waiting");

        if let Completion::IsCancelled = completion {
            manager.signal_exit();
        }
        held.release
            .send(match completion {
                Completion::Succeeds => Ok(()),
                Completion::IsCancelled => Err(HostError::Cancelled),
                Completion::Fails => Err(HostError::Failed(5)),
            })
            .unwrap();
        wait_until("shutdown completed", || shutdown.is_finished());
    });

    let expected = match completion {
        Completion::IsCancelled => BLOCK0,
        _ => state(&blocked_block.to_string()),
    };
    assert_eq!(
        fixture.db_state(),
        expected,
        "a stopped consumer must not strand the compactor"
    );
    let reader = fixture.persistence.create_reader(ReaderFlags::NONE).unwrap();
    let persisted_balance = reader.get_account(&address(0xA1)).unwrap();
    assert_eq!(persisted_balance, (expected != BLOCK0).then(|| account(blocked_block)));
}

/// How a stalled add ends.
#[derive(Clone, Copy, Debug)]
enum Stall {
    Released,
    ExitWhileWaiting,
    ExitBefore,
}

/// The compactor backpressure of `AddSnapshot`: with the compaction queue full, block processing waits, warning at the
/// stall timeout, and records the stall; process exit ends the wait, or skips it when signalled first.
#[rstest]
fn add_snapshot_waits_for_room_in_the_compaction_queue(
    #[values(Stall::Released, Stall::ExitWhileWaiting, Stall::ExitBefore)] stall: Stall,
) {
    logged_warnings();
    let fixture = Fixture::new(Backend::Mem, PERSIST_EVERY_BLOCK, Some(BLOCK0)).unwrap();
    let manager = fixture.manager(ManagerConfig {
        max_in_flight_compact_job: 1,
        seconds_per_slot: 0,
        ..config(false)
    });
    let held = fixture.host.hold_next_flush();
    let add = |block: u64| {
        let from = if block == 1 {
            BLOCK0
        } else {
            state(&(block - 1).to_string())
        };
        commit(&manager, from, state(&block.to_string()), block)
    };
    let compactor_took = |block: u64| {
        wait_until("the compactor took the job", || {
            !fixture.repository.get_states_at_block_number(block).is_empty()
        })
    };

    // The persist of 1 is held, 2 fills the persistence queue, 3 waits in the compactor, 4 fills its queue.
    assert_eq!(add(1), Ok(ADDED));
    held.started.recv_timeout(WAIT_LIMIT).unwrap();
    assert_eq!(add(2), Ok(ADDED));
    compactor_took(2);
    assert_eq!(add(3), Ok(ADDED));
    compactor_took(3);
    assert_eq!(add(4), Ok(ADDED));

    if let Stall::ExitBefore = stall {
        manager.signal_exit();
        assert_eq!(add(5), Ok(ADDED), "added without queueing");
        held.release.send(Ok(())).unwrap();
        return;
    }

    thread::scope(|scope| {
        let stalled = scope.spawn(|| add(5));
        thread::sleep(Duration::from_millis(50));
        assert!(!stalled.is_finished(), "the add waits for room");
        match stall {
            Stall::Released => held.release.send(Ok(())).unwrap(),
            _ => manager.signal_exit(),
        }

        let outcome = stalled.join().unwrap();
        match stall {
            Stall::Released => {
                let Ok(AddSnapshotOutcome::Added { stall: Some(waited) }) = outcome else {
                    panic!("expected a stalled add, got {outcome:?}");
                };
                assert!(waited > Duration::ZERO);
                let observations = fixture.repository.stats().drain_observations(usize::MAX);
                assert!(observations.iter().any(|observation| {
                    observation.metric == Histogram::CompactorStallTime as u16
                        && observation.value >= waited.as_nanos() as f64
                }));
            }
            _ => {
                assert_eq!(outcome, Err(ManagerError::Cancelled));
                held.release.send(Ok(())).unwrap();
            }
        }
        // The stall timeout is 0 with 0 seconds per slot, so the wait warned at once.
        assert!(
            logged_warnings()
                .iter()
                .any(|message| message == "Compactor job stall! Persistence is too slow for the network.")
        );
    });
}

/// A compactor that panics at one state.
struct FailingAt(StateId, SnapshotCompactor);

impl Compactor for FailingAt {
    fn do_compact_snapshot(&self, state_id: &StateId) -> bool {
        assert_ne!(*state_id, self.0, "compaction failed");
        self.1.do_compact_snapshot(state_id)
    }
}

/// `AddSnapshot_WhenACompactionFails_CompactsAndPersistsTheNextSnapshot`: the failed job is logged and skipped, and the
/// worker takes the next one.
#[test]
fn a_failed_compaction_skips_its_persist_and_the_next_snapshot_goes_through() {
    let fixture = Fixture::new(Backend::Mem, PM_CONFIG, Some(BLOCK0)).unwrap();
    let compactor = SnapshotCompactor::new(fixture.schedule, Arc::clone(&fixture.repository));
    let manager = FlatDbManager::new(
        config(false),
        Arc::new(FailingAt(state("1"), compactor)),
        Arc::clone(&fixture.persistence_manager),
    );

    assert_eq!(commit(&manager, BLOCK0, state("1"), 1), Ok(ADDED));
    assert_eq!(commit(&manager, state("1"), state("2"), 2), Ok(ADDED));
    manager.shutdown();

    assert_eq!(
        fixture.host.finalized_number_reads(),
        1,
        "only the next snapshot reached persistence"
    );
    assert!(
        fixture
            .repository
            .try_lease_in_memory_state(&state("2"), SnapshotTier::InMemoryCompacted)
            .is_some(),
        "and was compacted"
    );
}

/// `InlineCompaction` compacts on the adding thread, so the compacted snapshot exists when the add returns.
#[test]
fn inline_compaction_compacts_before_the_add_returns() {
    let fixture = Fixture::new(Backend::Mem, PM_CONFIG, Some(BLOCK0)).unwrap();
    let manager = fixture.manager(config(true));

    assert_eq!(commit(&manager, BLOCK0, state("1"), 1), Ok(ADDED));
    assert_eq!(commit(&manager, state("1"), state("2"), 2), Ok(ADDED));

    let compacted = fixture
        .repository
        .try_lease_in_memory_state(&state("2"), SnapshotTier::InMemoryCompacted)
        .expect("compacted inline");
    assert_eq!((compacted.from(), compacted.to()), (BLOCK0, state("2")));
}

/// `DropStateNotReachableFrom_ThenCommitOnResetHead_DropsAbandonedBranchAndServesNewOne`.
#[rstest]
fn drop_state_not_reachable_from_then_commit_on_the_reset_head(
    #[values(Backend::Mem, Backend::Managed, Backend::Rocks)] backend: Backend,
) {
    let Some(fixture) = Fixture::new(backend, PM_CONFIG, Some(BLOCK0)) else {
        return;
    };
    let manager = fixture.manager(config(true));
    let (abandoned1, abandoned2, reprocessed1) = (state("1r1"), state("2r1"), state("1r2"));

    assert_eq!(commit(&manager, BLOCK0, abandoned1, 1), Ok(ADDED));
    assert_eq!(commit(&manager, abandoned1, abandoned2, 2), Ok(ADDED));
    let cached = manager
        .gather_read_only_snapshot_bundle(&abandoned2, ReaderFlags::NONE)
        .unwrap();
    manager.drop_state_not_reachable_from(&BLOCK0).unwrap();
    assert_eq!(commit(&manager, BLOCK0, reprocessed1, 3), Ok(ADDED));

    let bundle = manager
        .gather_read_only_snapshot_bundle(&reprocessed1, ReaderFlags::NONE)
        .unwrap();
    assert!(!manager.has_state_for_block(&abandoned1).unwrap());
    assert!(!manager.has_state_for_block(&abandoned2).unwrap());
    assert!(manager.has_state_for_block(&reprocessed1).unwrap());
    assert_eq!(bundle.get_account(&address(0xA1)).unwrap().0, Some(account(3)));
    assert_eq!(
        Arc::strong_count(&cached),
        1,
        "the cached bundle over the dropped branch is released"
    );
}

/// `FlushCache` persists up to the committed head and clears the bundle cache, unless cancelled.
#[rstest]
fn flush_cache_persists_to_the_head_and_clears_the_bundle_cache(
    #[values(Backend::Mem, Backend::Managed, Backend::Rocks)] backend: Backend,
    #[values(false, true)] cancelled: bool,
) {
    let Some(fixture) = Fixture::new(backend, PM_CONFIG, Some(BLOCK0)) else {
        return;
    };
    let manager = fixture.manager(config(true));
    assert_eq!(commit(&manager, BLOCK0, state("1"), 1), Ok(ADDED));
    assert_eq!(commit(&manager, state("1"), state("2"), 2), Ok(ADDED));
    // The first persistence job clears the cache, the second one finds nothing to clear.
    wait_until("both persistence jobs ran", || {
        fixture.host.finalized_number_reads() >= 2
    });
    let cached = manager
        .gather_read_only_snapshot_bundle(&state("2"), ReaderFlags::NONE)
        .unwrap();

    let cleared = manager.flush_cache(&AtomicBool::new(cancelled)).unwrap();

    assert_eq!(cleared, !cancelled);
    assert_eq!(fixture.db_state(), state("2"));
    let again = manager
        .gather_read_only_snapshot_bundle(&state("2"), ReaderFlags::NONE)
        .unwrap();
    assert_eq!(Arc::ptr_eq(&cached, &again), cancelled);
    assert_eq!(again.get_account(&address(0xA1)).unwrap().0, Some(account(2)));
}

// ---- Stress ----

/// SplitMix64, so every run of a seed writes the same chain.
struct Rng(u64);

impl Rng {
    fn next(&mut self) -> u64 {
        self.0 = self.0.wrapping_add(0x9E37_79B9_7F4A_7C15);
        let mut z = self.0;
        z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
        z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
        z ^ (z >> 31)
    }

    fn below(&mut self, bound: u64) -> u64 {
        self.next() % bound
    }

    fn chance(&mut self, percent: u64) -> bool {
        self.below(100) < percent
    }
}

const ADDRESSES: u64 = 12;
const SLOTS: u64 = 6;
const PATHS: u64 = 8;

fn stress_address(index: u64) -> Address {
    address(0x10 + index as u8)
}

fn stress_path(index: u64) -> TreePath {
    TreePath::from_hex(&format!("{index:x}{index:x}"))
}

/// The state after a block, every key the chain ever wrote.
#[derive(Clone, Default)]
struct ModelState {
    accounts: HashMap<Address, Option<Account>>,
    slots: HashMap<(Address, U256), Option<U256>>,
    state_nodes: HashMap<TreePath, Vec<u8>>,
    storage_nodes: HashMap<(Hash256, TreePath), Vec<u8>>,
}

struct ModelBlock {
    to: StateId,
    snapshot: Arc<flatdb_core::snapshot::Snapshot>,
    after: ModelState,
}

/// A block on top of `before`: a few accounts, slots, self-destructs of existing accounts and trie nodes.
fn random_block(rng: &mut Rng, from: StateId, to: StateId, before: &ModelState) -> ModelBlock {
    let mut after = before.clone();
    let tag = to.block_number as u8 ^ to.state_root[0];
    let block = snapshot(from, to, |content| {
        for _ in 0..3 {
            let address = stress_address(rng.below(ADDRESSES));
            let account = (!rng.chance(10)).then(|| Account::new(to.block_number, U256::from_u64(rng.next())));
            content.set_account(address, account);
            after.accounts.insert(address, account);
        }
        if rng.chance(15) {
            // Clears the slots and the storage trie; the old nodes of a cleared trie are unreachable, so unchecked.
            let address = stress_address(rng.below(ADDRESSES));
            content.set_self_destruct(address, false);
            after.slots.retain(|(owner, _), _| *owner != address);
            after
                .storage_nodes
                .retain(|(owner, _), _| *owner != account_path(&address));
        }
        for _ in 0..4 {
            let key = (stress_address(rng.below(ADDRESSES)), U256::from_u64(rng.below(SLOTS)));
            let value = (!rng.chance(10)).then(|| U256::from_u64(1 + rng.below(1 << 40)));
            content.set_storage(key.0, key.1, value);
            after.slots.insert(key, value);
        }
        for _ in 0..2 {
            let path = stress_path(rng.below(PATHS));
            let rlp = vec![0xC2, tag, rng.next() as u8];
            content.set_state_node(path, [tag; 32], &rlp);
            after.state_nodes.insert(path, rlp);

            let key = (
                account_path(&stress_address(rng.below(ADDRESSES))),
                stress_path(rng.below(PATHS)),
            );
            let rlp = vec![0xC2, rng.next() as u8, tag];
            content.set_storage_node(key.0, key.1, [tag; 32], &rlp);
            after.storage_nodes.insert(key, rlp);
        }
    });
    ModelBlock {
        to,
        snapshot: block,
        after,
    }
}

/// Every read the model knows of at `state` through `bundle_reads`: accounts, slots (filtered or not) and nodes.
fn check_reads<R: PersistenceReader>(
    bundle: &flatdb_core::bundle::ReadOnlySnapshotBundle<R>,
    expected: &ModelState,
    rng: &mut Rng,
    state: StateId,
) {
    for index in 0..ADDRESSES {
        let address = stress_address(index);
        let (account, _) = bundle.get_account(&address).unwrap();
        assert_eq!(
            account,
            expected.accounts.get(&address).copied().flatten(),
            "account {index} at {state:?}"
        );
        for slot in 0..SLOTS {
            let slot = U256::from_u64(slot);
            let value = read_slot(bundle, &address, &slot, rng.chance(50)).unwrap();
            assert_eq!(
                value,
                expected.slots.get(&(address, slot)).copied().flatten(),
                "slot at {state:?}"
            );
        }
    }
    for (path, rlp) in &expected.state_nodes {
        let (loaded, _) = bundle
            .load_state_rlp_unchecked(path, ReadFlags::NONE, <[u8]>::to_vec)
            .unwrap();
        assert_eq!(loaded.as_ref(), Some(rlp), "state node at {state:?}");
    }
    for ((address_hash, path), rlp) in &expected.storage_nodes {
        let (loaded, _) = bundle
            .load_storage_rlp_unchecked(address_hash, path, ReadFlags::NONE, <[u8]>::to_vec)
            .unwrap();
        assert_eq!(loaded.as_ref(), Some(rlp), "storage node at {state:?}");
    }
}

/// Block processing adds a chain with forks below its head while readers gather and check bundles at recent states,
/// flushers flush the whole chain and drops prune the forks; every read must match the model of its state, and the
/// persisted state must end at the head with the head's content.
#[rstest]
fn concurrent_adds_gathers_flushes_and_drops_agree_with_the_model(
    #[values(Backend::Mem, Backend::Rocks)] backend: Backend,
    #[values(1, 2, 3)] seed: u64,
    #[values(false, true)] inline_compaction: bool,
) {
    const BLOCKS: u64 = 200;
    const READERS: u64 = 4;

    // Finality persists every CompactSize blocks keeping 4 in memory; the backstop never fires.
    let Some(fixture) = Fixture::new(
        backend,
        PersistenceManagerConfig {
            min_reorg_depth: 4,
            max_reorg_depth: 90_000,
            max_in_memory_snapshot_bytes: 0,
            long_finality: None,
        },
        Some(BLOCK0),
    ) else {
        return;
    };
    let manager = fixture.manager(ManagerConfig {
        max_in_flight_compact_job: 2,
        bundle_cache_clear_interval: Duration::from_millis(20),
        ..config(inline_compaction)
    });

    let mut rng = Rng(seed);
    let mut chain: Vec<ModelBlock> = Vec::new();
    let mut forks: HashMap<u64, ModelBlock> = HashMap::new();
    let mut head = BLOCK0;
    for block in 1..=BLOCKS {
        let before = chain.last().map(|block| block.after.clone()).unwrap_or_default();
        if rng.chance(20) {
            let fork = StateId::new(block, [0xF0 | (seed as u8); 32]);
            forks.insert(block, random_block(&mut rng, head, fork, &before));
        }
        let to = StateId::new(block, [block as u8; 32]);
        chain.push(random_block(&mut rng, head, to, &before));
        head = to;
    }

    let chain = &chain;
    let published = AtomicU64::new(0);
    let done = AtomicBool::new(false);
    // Block processing holds it to add a block, exclusively with a fork, which a flush must not persist as the
    // committed head; a drop, which resets the head, holds it exclusively too.
    let block_processing = RwLock::new(());
    let gathers = AtomicUsize::new(0);
    let chain_gathers = AtomicUsize::new(0);
    let not_retained = AtomicUsize::new(0);
    let persisted_heads = Mutex::new(Vec::new());

    thread::scope(|scope| {
        scope.spawn(|| {
            for (index, block) in chain.iter().enumerate() {
                let number = block.to.block_number;
                let _processing = match forks.get(&number) {
                    Some(fork) => {
                        let processing = block_processing.write();
                        let outcome = manager.add_snapshot(Arc::clone(&fork.snapshot)).unwrap();
                        assert!(
                            matches!(outcome, AddSnapshotOutcome::Added { .. } | AddSnapshotOutcome::Rejected),
                            "{outcome:?}"
                        );
                        Ok(processing)
                    }
                    None => Err(block_processing.read()),
                };
                let outcome = manager.add_snapshot(Arc::clone(&block.snapshot)).unwrap();
                assert!(matches!(outcome, AddSnapshotOutcome::Added { .. }), "{outcome:?}");
                fixture.host.set_finalized_state_root_at(number, block.to.state_root);
                fixture.host.set_finalized_block_number(number.saturating_sub(6));
                published.store(index as u64 + 1, Ordering::Release);
                thread::sleep(Duration::from_millis(1));
            }
            done.store(true, Ordering::Release);
        });

        for reader in 0..READERS {
            let (published, done, gathers, chain_gathers, not_retained) =
                (&published, &done, &gathers, &chain_gathers, &not_retained);
            let manager = &manager;
            scope.spawn(move || {
                let mut rng = Rng(seed * 100 + reader);
                while !done.load(Ordering::Acquire) {
                    let count = published.load(Ordering::Acquire);
                    if count == 0 {
                        thread::yield_now();
                        continue;
                    }
                    let index = count - 1 - rng.below(count.min(12));
                    let block = &chain[index as usize];
                    let flags = if rng.chance(20) {
                        ReaderFlags::FULL_SCAN
                    } else {
                        ReaderFlags::NONE
                    };
                    match manager.gather_read_only_snapshot_bundle(&block.to, flags) {
                        Ok(bundle) => {
                            check_reads(&bundle, &block.after, &mut rng, block.to);
                            gathers.fetch_add(1, Ordering::Relaxed);
                            if bundle.snapshot_count() > 1 {
                                chain_gathers.fetch_add(1, Ordering::Relaxed);
                            }
                        }
                        Err(ManagerError::StateNotRetained(gathered)) => {
                            let persisted = manager.persistence_manager().get_current_persisted_state_id().unwrap();
                            assert!(
                                persisted.block_number > gathered.block_number,
                                "{gathered:?} lost below {persisted:?}"
                            );
                            not_retained.fetch_add(1, Ordering::Relaxed);
                        }
                        Err(error) => panic!("gathering {:?}: {error}", block.to),
                    }
                }
            });
        }

        for flusher in 0..2u64 {
            let (done, block_processing, persisted_heads) = (&done, &block_processing, &persisted_heads);
            let manager = &manager;
            scope.spawn(move || {
                let mut rng = Rng(seed * 1000 + flusher);
                while !done.load(Ordering::Acquire) {
                    thread::sleep(Duration::from_millis(15 + rng.below(25)));
                    if rng.chance(70) {
                        let _processing = block_processing.read();
                        manager.flush_cache(&AtomicBool::new(false)).unwrap();
                        let persisted = manager.persistence_manager().get_current_persisted_state_id().unwrap();
                        persisted_heads.lock().push(persisted);
                    } else {
                        let _processing = block_processing.write();
                        if let Some(head) = manager.repository().get_last_committed_state_id() {
                            manager.drop_state_not_reachable_from(&head).unwrap();
                        }
                    }
                }
            });
        }
    });

    assert!(manager.flush_cache(&AtomicBool::new(false)).unwrap());
    let head = chain.last().unwrap();
    assert_eq!(fixture.db_state(), head.to);
    let reader = fixture.persistence.create_reader(ReaderFlags::NONE).unwrap();
    let persisted = flatdb_core::bundle::ReadOnlySnapshotBundle::new(Vec::new(), Vec::new(), reader, 0.0);
    check_reads(&persisted, &head.after, &mut Rng(seed), head.to);

    manager.shutdown();
    let persisted_heads = persisted_heads.into_inner();
    assert!(
        persisted_heads
            .windows(2)
            .all(|pair| pair[0].block_number <= pair[1].block_number),
        "the persisted state only moves forward: {persisted_heads:?}"
    );
    assert!(
        persisted_heads
            .iter()
            .all(|state| chain.iter().any(|block| block.to == *state)),
        "only canonical states are persisted"
    );
    assert!(
        chain_gathers.load(Ordering::Relaxed) > 0,
        "reads went through snapshot chains"
    );

    // Every persisted node event names a node the chain wrote, and the gauges track the repository.
    let written: HashSet<(Option<Hash256>, TreePath)> = chain
        .iter()
        .flat_map(|block| {
            let state_nodes = block.after.state_nodes.keys().map(|path| (None, *path));
            let storage_nodes = block
                .after
                .storage_nodes
                .keys()
                .map(|(hash, path)| (Some(*hash), *path));
            state_nodes.chain(storage_nodes).collect::<Vec<_>>()
        })
        .collect();
    let events = fixture.events.drain(usize::MAX);
    assert!(!events.is_empty());
    for event in &events {
        assert!(chain.iter().any(|block| block.to == event.to));
        for node in &event.nodes {
            assert!(written.contains(&(node.address_hash, node.path)), "{node:?}");
        }
    }
    let counters = &fixture.repository.stats().counters;
    assert_eq!(
        counters.snapshot_count.load(Ordering::Relaxed),
        fixture.repository.snapshot_count()
    );
    assert_eq!(
        counters.total_snapshot_memory.load(Ordering::Relaxed),
        fixture.repository.in_memory_bytes()
    );
}

// ---- Long finality: the persisted-snapshot tier under the manager ----

/// A long-finality fixture with the real persisted compactor and the catalog of `wrap_catalog`.
fn long_finality_fixture(schedule: CompactionSchedule, wrap_catalog: WrapCatalog) -> Fixture {
    let options = FixtureOptions {
        schedule,
        record_persisted_compactions: false,
        wrap_catalog,
    };
    Fixture::with_options(Backend::Mem, PM_CONFIG_LF, Some(BLOCK0), options).unwrap()
}

fn default_schedule() -> CompactionSchedule {
    CompactionSchedule::new(16, 1 << 20, 0).unwrap()
}

/// Converts the base `from -> to` setting account `0xA1..` to `balance` and the state node at `path`.
fn convert(fixture: &Fixture, from: StateId, to: StateId, balance: u64, path: &str) {
    let base = snapshot(from, to, |content| {
        content.set_account(address(0xA1), Some(account(balance)));
        content.set_state_node(TreePath::from_hex(path), [balance as u8; 32], &[0xC1, balance as u8]);
    });
    fixture.tier().tier.loader.convert_and_register(&base).unwrap();
}

/// `GatherReadOnlySnapshotBundle_IncludesPersistedSnapshots` and `FlatDbManager_EndToEnd_WithPersistedSnapshots`: a
/// gather above the persisted state reaches into the tier, numbers its snapshots first and reads a node only the tier
/// holds; the bundle metrics count the tier's snapshots, their bytes and their depth.
#[test]
fn gather_reads_through_the_persisted_tier_and_reports_it() {
    let fixture = long_finality_fixture(default_schedule(), FixtureOptions::default().wrap_catalog);
    let manager = fixture.manager(config(true));
    convert(&fixture, BLOCK0, state("1"), 1, "ab");
    convert(&fixture, state("1"), state("2"), 2, "cd");
    fixture.add(state("2"), state("3"), false);
    fixture.repository.stats().drain_observations(usize::MAX);

    let bundle = manager
        .gather_read_only_snapshot_bundle(&state("3"), ReaderFlags::NONE)
        .unwrap();

    assert_eq!((bundle.persisted().len(), bundle.snapshot_count()), (2, 3));
    assert_eq!(
        bundle
            .load_state_rlp_unchecked(&TreePath::from_hex("ab"), ReadFlags::NONE, <[u8]>::to_vec)
            .unwrap(),
        (Some(vec![0xC1, 1]), ReadSource::Persisted)
    );
    assert_eq!(
        bundle.get_account(&address(0xA1)).unwrap(),
        (Some(account(100)), ReadSource::Snapshot)
    );
    let persisted_bytes: i64 = bundle
        .persisted()
        .snapshots()
        .iter()
        .map(|snapshot| snapshot.size())
        .sum();
    let counters = &fixture.repository.stats().counters;
    assert_eq!(
        [
            counters.snapshot_bundle_size.load(Ordering::Relaxed),
            counters.snapshot_bundle_persisted_snapshot_size.load(Ordering::Relaxed),
            counters
                .snapshot_bundle_persisted_snapshot_memory
                .load(Ordering::Relaxed),
        ],
        [1, 2, persisted_bytes]
    );
    let depths: Vec<(u16, f64)> = fixture
        .repository
        .stats()
        .drain_observations(usize::MAX)
        .into_iter()
        .filter(|observation| observation.metric == Histogram::SnapshotBundleBlockNumberDepth as u16)
        .map(|observation| (observation.label, observation.value))
        .collect();
    assert_eq!(
        depths,
        [
            (HistogramLabel::InMemory as u16, 1.0),
            (HistogramLabel::Persisted as u16, 2.0)
        ]
    );
    assert!(
        manager.has_state_for_block(&state("1")).unwrap(),
        "a persisted base is a state"
    );
    manager.shutdown();
}

/// `MaxInMemoryBaseSnapshotCount_GatesConversionToPersistedTier`, `LongFinalityMaxReorgDepth_TriggersForcePersist
/// Backstop` and `FinalizedBoundary_PersistsWithinLongFinalityRange`: Phase 2 converts only past
/// `MaxInMemoryBaseSnapshotCount`, `LongFinalityMaxReorgDepth` is the backstop, and a finalized boundary deep inside
/// the long-finality window still persists, the repository pruning against the same boundary.
///
/// `settings` are `CompactSize`, `MinReorgDepth`, `MaxInMemoryBaseSnapshotCount` and `LongFinalityMaxReorgDepth`; the
/// chain has a base at each of blocks `1..=bases`.
#[rstest]
#[case::conversion_at_the_in_memory_floor([16, 64, 4, 90_000], 0, 4, 4, None)]
#[case::conversion_past_the_in_memory_floor([16, 64, 4, 90_000], 0, 5, 5, Some("base"))]
#[case::depth_at_the_long_finality_backstop([16, 8, 100_000, 100], 0, 1, 100, None)]
#[case::depth_past_the_long_finality_backstop([16, 8, 100_000, 100], 0, 1, 101, Some("memory"))]
#[case::finalized_boundary_inside_the_window([4, 0, 100_000, 90_000], 4, 4, 4, Some("memory"))]
fn long_finality_settings_decide_conversion_and_persistence(
    #[case] settings: [u64; 4],
    #[case] finalized_block: u64,
    #[case] bases: u64,
    #[case] latest_block: u64,
    #[case] expected: Option<&str>,
) {
    let [
        compact_size,
        min_reorg_depth,
        max_in_memory_base_snapshot_count,
        long_finality_max_reorg_depth,
    ] = settings;
    let config = PersistenceManagerConfig {
        min_reorg_depth,
        max_reorg_depth: 256,
        max_in_memory_snapshot_bytes: 0,
        long_finality: Some(LongFinalityConfig {
            max_reorg_depth: long_finality_max_reorg_depth,
            max_in_memory_base_snapshot_count,
        }),
    };
    let options = FixtureOptions {
        schedule: CompactionSchedule::new(compact_size, 1 << 20, 0).unwrap(),
        ..FixtureOptions::default()
    };
    let fixture = Fixture::with_options(Backend::Mem, config, Some(BLOCK0), options).unwrap();
    let edges: Vec<String> = (0..bases).map(|block| format!("{block}-{}", block + 1)).collect();
    fixture.add_edges(&edges.join(" "));
    fixture.host.set_finalized_block_number(finalized_block);
    fixture
        .host
        .set_finalized_state_root_at(finalized_block, state(&finalized_block.to_string()).state_root);

    let decided = fixture
        .persistence_manager
        .determine_snapshot_action(&state(&latest_block.to_string()))
        .unwrap();

    assert_eq!(decided.as_ref().map(|action| decision(action).0), expected);
    if let Some(action) = &decided {
        assert_eq!(decision(action).1, BLOCK0);
    }
    if finalized_block > 0 {
        let orphan = state(&format!("{finalized_block}r9"));
        fixture.persist_base(BLOCK0, orphan);
        fixture
            .repository
            .set_last_committed_state_id(state(&bases.to_string()));
        fixture.repository.remove_finalized_persisted_forks(&BLOCK0).unwrap();
        assert!(
            !fixture.repository.has_base_persisted_snapshot(&orphan),
            "the repository must use the same finalized boundary as the manager"
        );
    }
}

/// A catalog that counts in `entered` the rows of merged snapshots it is asked to write, waits on `gate` before writing
/// one, and fails such writes with `failure`.
fn compaction_catalog(gate: Arc<Mutex<()>>, entered: Arc<AtomicUsize>, failure: Option<KvError>) -> WrapCatalog {
    Arc::new(move |rows| {
        Box::new(CompactionCatalog {
            rows,
            gate: gate.clone(),
            entered: entered.clone(),
            failure: failure.clone(),
        })
    })
}

struct CompactionCatalog {
    rows: MemCatalogKv,
    gate: Arc<Mutex<()>>,
    entered: Arc<AtomicUsize>,
    failure: Option<KvError>,
}

impl CatalogKv for CompactionCatalog {
    fn put(&self, key: &[u8], value: &[u8]) -> KvResult<()> {
        // The tier byte ends the row; 2 is a base, written by a conversion.
        if value.last() != Some(&2) {
            self.entered.fetch_add(1, Ordering::SeqCst);
            drop(self.gate.lock());
            if let Some(failure) = &self.failure {
                return Err(failure.clone());
            }
        }
        self.rows.put(key, value)
    }

    fn key_exists(&self, key: &[u8]) -> KvResult<bool> {
        self.rows.key_exists(key)
    }

    fn remove(&self, key: &[u8]) -> KvResult<()> {
        self.rows.remove(key)
    }
}

/// Shutdown with the persisted compactor busy: it waits for the merge under way, then for the batches queued after it,
/// unless process exit was signalled first, which drops them.
#[rstest]
fn shutdown_waits_for_the_persisted_compactor_unless_process_exit_drops_its_queue(
    #[values(false, true)] process_exit: bool,
) {
    let (gate, entered) = (Arc::new(Mutex::new(())), Arc::new(AtomicUsize::new(0)));
    let fixture = long_finality_fixture(
        CompactionSchedule::new(4, 16, 0).unwrap(),
        compaction_catalog(gate.clone(), entered.clone(), None),
    );
    let manager = fixture.manager(config(true));
    for block in 1..=8u64 {
        convert(
            &fixture,
            state(&(block - 1).to_string()),
            state(&block.to_string()),
            block,
            &format!("{block:x}"),
        );
    }
    let compactor = &fixture.tier().tier.compactor;
    let small = |block: &str| {
        fixture
            .repository
            .try_lease_persisted_state(&state(block), SnapshotTier::PersistedSmallCompacted)
            .is_some()
    };

    let closed = gate.lock();
    compactor.enqueue(vec![state("2")], 0).unwrap();
    wait_until("the merge writes its row", || entered.load(Ordering::SeqCst) == 1);
    compactor.enqueue(vec![state("6")], 0).unwrap();
    if process_exit {
        manager.signal_exit();
    }
    thread::scope(|scope| {
        let shutdown = scope.spawn(|| manager.shutdown());
        thread::sleep(Duration::from_millis(20));
        assert!(!shutdown.is_finished(), "the merge under way holds the shutdown");
        drop(closed);
        shutdown.join().unwrap();
    });

    assert!(small("2"), "the merge under way completes");
    assert_eq!(small("6"), !process_exit, "process exit drops the queued batches");
}

/// A fatal catalog failure of a persisted compaction is recorded as the manager's fatal error, handed out once, after
/// which every persist fails with it; a failure that is not fatal is only logged.
#[rstest]
#[case::fatal("IO error: No space left on device", true)]
#[case::not_fatal("Busy: ", false)]
fn a_fatal_catalog_failure_of_a_persisted_compaction_stops_every_later_persist(
    #[case] message: &str,
    #[case] fatal: bool,
) {
    let failure = KvError::rocks(message.to_owned());
    let fixture = long_finality_fixture(
        CompactionSchedule::new(4, 16, 0).unwrap(),
        compaction_catalog(Arc::default(), Arc::default(), Some(failure.clone())),
    );
    for block in 1..=2u64 {
        convert(
            &fixture,
            state(&(block - 1).to_string()),
            state(&block.to_string()),
            block,
            &format!("{block:x}"),
        );
    }
    fixture.add(state("2"), state("3"), false);

    fixture.tier().tier.compactor.enqueue(vec![state("2")], 0).unwrap();
    fixture.tier().tier.compactor.dispose();

    let expected = PersistError::Persistence(PersistenceError::Catalog(failure));
    let persist = fixture.persistence_manager.add_to_persistence(&state("3"));
    if fatal {
        assert_eq!(persist, Err(expected.clone()));
        assert_eq!(fixture.persistence_manager.take_fatal_error(), Some(expected.clone()));
        assert_eq!(fixture.persistence_manager.take_fatal_error(), None, "handed out once");
        assert_eq!(fixture.persistence_manager.flush_to_persistence(), Err(expected));
        assert_eq!(fixture.db_state(), BLOCK0);
    } else {
        assert_eq!(persist, Ok(()));
        assert_eq!(fixture.persistence_manager.take_fatal_error(), None);
        assert_eq!(fixture.persistence_manager.flush_to_persistence(), Ok(state("3")));
    }
}

/// The next session opens what shutdown left: every snapshot of the tier and its reads; a snapshot pruned while a
/// bundle held it stays readable through the bundle across shutdown, is released with it, and is not loaded again.
#[test]
fn shutdown_leaves_the_tier_for_the_next_session_but_what_was_pruned() {
    let fixture = long_finality_fixture(default_schedule(), FixtureOptions::default().wrap_catalog);
    let manager = fixture.manager(config(true));
    for block in 1..=3u64 {
        convert(
            &fixture,
            state(&(block - 1).to_string()),
            state(&block.to_string()),
            block,
            &format!("{block:x}"),
        );
    }
    let held = manager
        .gather_read_only_snapshot_bundle(&state("2"), ReaderFlags::NONE)
        .unwrap();
    let pruned = Arc::downgrade(&held.persisted().snapshots().iter().next().unwrap().clone());
    fixture.repository.remove_persisted_states_until(2).unwrap();

    manager.shutdown();
    drop(manager);
    let Fixture {
        tier,
        persistence_manager,
        repository,
        ..
    } = fixture;
    let tier = tier.unwrap();
    let (directory, catalog_kv) = (tier.directory, tier.catalog_kv);
    drop((persistence_manager, repository, tier.tier, tier.recorder));

    assert_eq!(
        held.get_account(&address(0xA1)).unwrap(),
        (Some(account(2)), ReadSource::Persisted)
    );
    assert!(pruned.upgrade().is_some(), "the bundle holds the pruned snapshot");
    drop(held);
    assert!(pruned.upgrade().is_none(), "released with the bundle");

    let (repository, reopened) = PersistedTier::open(
        &tier_settings(directory.path(), default_schedule()),
        Box::new(catalog_kv.clone()),
        &pack_rows(&catalog_kv.rows()),
        Arc::new(FakeHost::default()),
        Arc::new(flatdb_core::stats::FlatStats::default()),
        Arc::default(),
        Box::new(|job| panic!("{job} panicked")),
    )
    .unwrap();
    assert_eq!(
        repository.persisted_snapshot_count(),
        2,
        "every snapshot but the pruned one"
    );
    let bundle = flatdb_core::bundle::ReadOnlySnapshotBundle::new(
        Vec::new(),
        repository.assemble_snapshots(&state("3"), &state("1"), 2).persisted,
        common::snapshots::DictionaryReader::default(),
        0.0,
    );
    assert_eq!(
        bundle.get_account(&address(0xA1)).unwrap(),
        (Some(account(3)), ReadSource::Persisted)
    );
    assert_eq!(
        bundle
            .load_state_rlp_unchecked(&TreePath::from_hex("2"), ReadFlags::NONE, <[u8]>::to_vec)
            .unwrap(),
        (Some(vec![0xC1, 2]), ReadSource::Persisted)
    );
    reopened.loader.mark_for_shutdown();
}
