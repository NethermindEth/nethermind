//! The compactor and its schedule: ports of `SnapshotCompactorTests` and of the math and validation of
//! `CompactionScheduleTests`, plus a proptest of compaction against a naive model of the merge.
//!
//! Not ported: the `ResourcePool.Usage` pool reuse tests (Rust contents are not pooled, only counted in its gauges) and
//! the offset resolution tests, which stay in C# (the offset reaches Rust as a number).

mod common;

use std::collections::{BTreeMap, BTreeSet};
use std::sync::Arc;
use std::sync::atomic::Ordering;

use common::hex;
use common::snapshots::{FixedFinality, address, empty_snapshot, snapshot, state_id};
use flatdb_core::compactor::SnapshotCompactor;
use flatdb_core::keccak::account_path;
use flatdb_core::repository::SnapshotRepository;
use flatdb_core::schedule::CompactionSchedule;
use flatdb_core::snapshot::content::SnapshotContentBuilder;
use flatdb_core::snapshot::{Snapshot, SnapshotTier};
use flatdb_core::types::{Account, Address, Hash256, StateId, TreePath, U256};
use proptest::prelude::*;
use proptest::test_runner::TestCaseError;
use rstest::rstest;

const MAX_COMPACT_SIZE: u64 = 8192;

fn schedule(compact_size: u64, offset: u64) -> CompactionSchedule {
    CompactionSchedule::new(compact_size, MAX_COMPACT_SIZE.max(compact_size), offset).unwrap()
}

fn compactor_with(compact_size: u64, offset: u64) -> (SnapshotCompactor, Arc<SnapshotRepository>) {
    let repository = Arc::new(SnapshotRepository::new(Arc::new(FixedFinality::default())));
    (
        SnapshotCompactor::new(schedule(compact_size, offset), Arc::clone(&repository)),
        repository,
    )
}

fn compactor() -> (SnapshotCompactor, Arc<SnapshotRepository>) {
    compactor_with(16, 0)
}

/// `new StateId(block, Keccak.Zero)`.
fn zero_state(block_number: u64) -> StateId {
    StateId::new(block_number, [0; 32])
}

fn add_base(repository: &SnapshotRepository, snapshot: Arc<Snapshot>) {
    let to = snapshot.to();
    assert!(repository.try_add(snapshot, SnapshotTier::InMemoryBase));
    repository.add_state_id(to);
}

/// `BuildSnapshotChain`: empty base snapshots `i -> i + 1` for `i` in `start..end`.
fn build_chain(repository: &SnapshotRepository, start: u64, end: u64) {
    for block in start..end {
        add_base(repository, empty_snapshot(state_id(block, 0), state_id(block + 1, 0)));
    }
}

fn account(nonce: u64, balance: u64) -> Option<Account> {
    Some(Account::new(nonce, U256::from_u64(balance)))
}

fn slot(value: u64) -> Option<U256> {
    Some(U256::from_u64(value))
}

fn path(hex_path: &str) -> TreePath {
    TreePath::from_hex(hex_path)
}

#[test]
fn compacting_a_single_snapshot_preserves_every_kind_of_write() {
    let (compactor, _) = compactor();
    let (address1, address2) = (address(0x11), address(0x22));
    let source = snapshot(zero_state(0), zero_state(1), |c| {
        c.set_account(address1, account(1, 100));
        c.set_account(address2, account(2, 200));
        c.set_storage(address1, U256::from_u64(1), slot(100));
        c.set_storage(address2, U256::from_u64(2), slot(200));
        c.set_state_node(path("abcd"), [0xA1; 32], &hex("0xc180"));
        c.set_state_node(path("ef01"), [0xA2; 32], &[]);
        c.set_storage_node(account_path(&address1), path("1234"), [0xB1; 32], &hex("0xc2a0b0"));
        c.set_storage_node(account_path(&address2), path("5678"), [0xB2; 32], &hex("0xc3010203"));
    });

    let compacted = compactor.compact_snapshot_bundle(&[source]);

    assert_eq!(
        (compacted.from(), compacted.to(), compacted.is_sorted()),
        (zero_state(0), zero_state(1), true)
    );
    assert_eq!(compacted.accounts_count(), 2);
    assert_eq!(compacted.try_get_account(&address1), Some(account(1, 100)));
    assert_eq!(compacted.try_get_account(&address2), Some(account(2, 200)));
    assert_eq!(compacted.storages_count(), 2);
    assert_eq!(
        compacted.try_get_storage(&address2, &U256::from_u64(2)),
        Some(slot(200))
    );
    assert_eq!(compacted.state_nodes_count(), 2);
    let leaf = compacted.try_get_state_node(&path("abcd")).unwrap();
    assert_eq!((leaf.hash, leaf.rlp), (&[0xA1; 32], &hex("0xc180")[..]));
    assert!(
        compacted
            .try_get_state_node(&path("ef01"))
            .unwrap()
            .is_hash_only_placeholder()
    );
    assert_eq!(compacted.storage_nodes_count(), 2);
    let storage_node = compacted
        .try_get_storage_node(&account_path(&address2), &path("5678"))
        .unwrap();
    assert_eq!(storage_node.rlp, &hex("0xc3010203")[..]);
}

#[test]
fn compacting_merges_disjoint_writes_and_lets_the_latest_win() {
    let (compactor, _) = compactor();
    let (address1, address2) = (address(0x11), address(0x22));
    let first = snapshot(zero_state(0), zero_state(1), |c| {
        c.set_account(address1, account(1, 100));
        c.set_storage(address1, U256::from_u64(1), slot(100));
        c.set_state_node(path("abcd"), [0x01; 32], &hex("0xc101"));
        c.set_storage_node(account_path(&address1), path("1234"), [0x02; 32], &hex("0xc102"));
    });
    let second = snapshot(zero_state(1), zero_state(2), |c| {
        c.set_account(address1, account(2, 200));
        c.set_account(address2, account(3, 300));
        c.set_storage(address1, U256::from_u64(1), slot(200));
        c.set_state_node(path("abcd"), [0x03; 32], &hex("0xc20303"));
        c.set_state_node(path("ef01"), [0x04; 32], &hex("0xc104"));
        c.set_storage_node(account_path(&address1), path("1234"), [0x05; 32], &hex("0xc20505"));
    });

    let compacted = compactor.compact_snapshot_bundle(&[first, second]);

    assert_eq!((compacted.from(), compacted.to()), (zero_state(0), zero_state(2)));
    let counts = compacted.counts();
    assert_eq!(
        (
            counts.accounts,
            counts.storages,
            counts.state_nodes,
            counts.storage_nodes
        ),
        (2, 1, 2, 1)
    );
    assert_eq!(compacted.try_get_account(&address1), Some(account(2, 200)));
    assert_eq!(
        compacted.try_get_storage(&address1, &U256::from_u64(1)),
        Some(slot(200))
    );
    assert_eq!(
        compacted.try_get_state_node(&path("abcd")).unwrap().rlp,
        &hex("0xc20303")[..]
    );
    assert_eq!(
        compacted.try_get_state_node(&path("ef01")).unwrap().rlp,
        &hex("0xc104")[..]
    );
    let storage_node = compacted
        .try_get_storage_node(&account_path(&address1), &path("1234"))
        .unwrap();
    assert_eq!(
        (storage_node.hash, storage_node.rlp),
        (&[0x05; 32], &hex("0xc20505")[..])
    );
    let accounts: Vec<Address> = compacted.accounts().map(|(address, _)| *address).collect();
    assert_eq!(
        accounts,
        [address1, address2],
        "a compacted snapshot iterates in key order"
    );
}

#[rstest]
#[case::keeps_writes_at_or_after_the_clear(false)]
#[case::later_clear_raises_the_boundary(true)]
fn self_destruct_boundary(#[case] self_destruct_again_in_last_snapshot: bool) {
    let (compactor, _) = compactor();
    let (a, b) = (address(0x11), address(0x22));
    let (a_hash, b_hash) = (account_path(&a), account_path(&b));
    let marker = |byte: u8| Some(U256([0, 0, 0, u64::from(byte) << 56]));
    let leaf = hex("0xc2a0b0");

    // Block 0 -> 1: A gets a slot and node before it is ever cleared; B is unrelated.
    let s0 = snapshot(zero_state(0), zero_state(1), |c| {
        c.set_storage(a, U256::from_u64(1), marker(0xA0));
        c.set_storage_node(a_hash, path("01"), [1; 32], &leaf);
        c.set_storage(b, U256::from_u64(1), marker(0xBB));
        c.set_storage_node(b_hash, path("04"), [4; 32], &leaf);
    });
    // Block 1 -> 2: A is cleared and written again in the same snapshot.
    let s1 = snapshot(zero_state(1), zero_state(2), |c| {
        c.set_self_destruct(a, false);
        c.set_storage(a, U256::from_u64(2), marker(0xA2));
        c.set_storage_node(a_hash, path("02"), [2; 32], &leaf);
    });
    // Block 2 -> 3: A is written again, optionally cleared again.
    let s2 = snapshot(zero_state(2), zero_state(3), |c| {
        if self_destruct_again_in_last_snapshot {
            c.set_self_destruct(a, false);
        }
        c.set_storage(a, U256::from_u64(3), marker(0xA3));
        c.set_storage_node(a_hash, path("03"), [3; 32], &leaf);
    });

    let compacted = compactor.compact_snapshot_bundle(&[s0, s1, s2]);

    assert_eq!(
        compacted.try_get_storage(&a, &U256::from_u64(1)),
        None,
        "written before the first clear"
    );
    assert!(compacted.try_get_storage_node(&a_hash, &path("01")).is_none());
    assert_eq!(
        compacted.try_get_storage(&a, &U256::from_u64(3)),
        Some(marker(0xA3)),
        "written after the last clear"
    );
    assert!(compacted.try_get_storage_node(&a_hash, &path("03")).is_some());
    let same_block_kept = !self_destruct_again_in_last_snapshot;
    assert_eq!(
        compacted.try_get_storage(&a, &U256::from_u64(2)).is_some(),
        same_block_kept
    );
    assert_eq!(
        compacted.try_get_storage_node(&a_hash, &path("02")).is_some(),
        same_block_kept
    );
    assert_eq!(
        compacted.try_get_storage(&b, &U256::from_u64(1)),
        Some(marker(0xBB)),
        "unrelated address"
    );
    assert!(compacted.try_get_storage_node(&b_hash, &path("04")).is_some());
    assert_eq!(compacted.self_destruct(&a), Some(false));
}

#[rstest]
#[case::cleared_existing(&[false], Some(false))]
#[case::new_account(&[true], Some(true))]
#[case::new_then_existing(&[true, false], Some(false))]
#[case::existing_then_new_stays_existing(&[false, true], Some(false))]
#[case::new_twice(&[true, true], Some(true))]
fn self_destruct_flags_merge(#[case] is_new_per_snapshot: &[bool], #[case] expected: Option<bool>) {
    let (compactor, _) = compactor();
    let address = address(0x11);
    let mut snapshots = vec![empty_snapshot(zero_state(0), zero_state(1))];
    for (index, is_new) in is_new_per_snapshot.iter().enumerate() {
        let block = index as u64 + 1;
        snapshots.push(snapshot(zero_state(block), zero_state(block + 1), |c| {
            c.set_self_destruct(address, *is_new)
        }));
    }
    snapshots[0] = snapshot(zero_state(0), zero_state(1), |c| {
        c.set_storage(address, U256::from_u64(1), slot(5))
    });

    let compacted = compactor.compact_snapshot_bundle(&snapshots);

    assert_eq!(compacted.self_destruct(&address), expected);
    let slot_kept = is_new_per_snapshot.iter().all(|is_new| *is_new);
    assert_eq!(
        compacted.storages_count(),
        usize::from(slot_kept),
        "only a clear of an existing account drops slots"
    );
}

/// `CompactUsage`: a compacted content counts in the `SortedSnapshotContent` gauges of its window's compact size.
#[rstest]
#[case::partial_window(4, 1)]
#[case::full_window(16, 3)]
fn a_compacted_content_counts_under_its_compact_usage(#[case] block_number: u64, #[case] usage: usize) {
    let (compactor, repository) = compactor();
    let sources = [
        empty_snapshot(zero_state(block_number - 2), zero_state(block_number - 1)),
        empty_snapshot(zero_state(block_number - 1), zero_state(block_number)),
    ];
    let content = &repository.stats().counters.content;
    let gauges = || {
        (
            content.sorted_active[usage].load(Ordering::Relaxed),
            content.sorted_created[usage].load(Ordering::Relaxed),
        )
    };

    let compacted = compactor.compact_snapshot_bundle(&sources);
    assert_eq!(gauges(), (1, 1));
    drop(compacted);
    assert_eq!(gauges(), (0, 1));
}

#[test]
fn compacting_compacted_snapshots_keeps_node_rlp_reachable_after_the_sources_are_gone() {
    let (compactor, _) = compactor();
    let sources: Vec<Arc<Snapshot>> = (0..4u8)
        .map(|block| {
            snapshot(zero_state(u64::from(block)), zero_state(u64::from(block) + 1), |c| {
                c.set_state_node(path(&format!("{block:x}")), [block; 32], &[0xC1, block]);
                c.set_storage_node([0x99; 32], path("ab"), [block; 32], &[0xC2, block, block]);
            })
        })
        .collect();
    let low = Arc::new(compactor.compact_snapshot_bundle(&sources[..2]));
    let high = Arc::new(compactor.compact_snapshot_bundle(&sources[2..]));
    drop(sources);

    let all = compactor.compact_snapshot_bundle(&[low, high]);

    for block in 0..4u8 {
        assert_eq!(
            all.try_get_state_node(&path(&format!("{block:x}"))).unwrap().rlp,
            [0xC1, block]
        );
    }
    assert_eq!(
        all.try_get_storage_node(&[0x99; 32], &path("ab")).unwrap().rlp,
        [0xC2, 3, 3]
    );
}

#[rstest]
#[case::compaction_disabled(1, 0, 16)]
#[case::block_zero(16, 0, 0)]
#[case::not_a_compaction_block(16, 0, 5)]
fn get_snapshots_to_compact_is_empty_off_schedule(#[case] compact_size: u64, #[case] from: u64, #[case] to: u64) {
    let (compactor, repository) = compactor_with(compact_size, 0);
    build_chain(&repository, 0, 16);

    assert!(
        compactor
            .get_snapshots_to_compact(&empty_snapshot(zero_state(from), zero_state(to)))
            .is_empty()
    );
}

#[rstest]
#[case(16, 16)]
#[case(4, 4)]
#[case(8, 8)]
#[case(12, 4)]
#[case(2, 2)]
fn get_snapshots_to_compact_takes_the_power_of_two_window(#[case] block_number: u64, #[case] expected_count: usize) {
    let (compactor, repository) = compactor();
    build_chain(&repository, 0, block_number);
    let target = repository
        .try_lease_in_memory_state(&state_id(block_number, 0), SnapshotTier::InMemoryBase)
        .unwrap();

    let snapshots = compactor.get_snapshots_to_compact(&target);

    assert_eq!(snapshots.len(), expected_count);
    assert_eq!(snapshots[0].from().block_number, block_number - expected_count as u64);
    assert_eq!(snapshots.last().unwrap().to(), state_id(block_number, 0));
}

#[rstest]
fn get_snapshots_to_compact_is_empty_at_odd_blocks(#[values(1, 3, 5, 7, 9)] block_number: u64) {
    let (compactor, repository) = compactor();
    build_chain(&repository, 0, block_number);
    let target = empty_snapshot(state_id(block_number - 1, 0), state_id(block_number, 0));

    assert!(compactor.get_snapshots_to_compact(&target).is_empty());
}

#[test]
fn get_snapshots_to_compact_is_empty_for_a_single_snapshot_or_a_gap() {
    let (single, repository) = compactor();
    add_base(&repository, empty_snapshot(zero_state(0), zero_state(16)));
    assert!(
        single
            .get_snapshots_to_compact(&empty_snapshot(zero_state(0), zero_state(16)))
            .is_empty()
    );

    let (gap, repository) = compactor();
    for block in 2..16 {
        add_base(&repository, empty_snapshot(zero_state(block), zero_state(block + 1)));
    }
    assert!(
        gap.get_snapshots_to_compact(&empty_snapshot(zero_state(15), zero_state(16)))
            .is_empty()
    );
}

#[test]
fn get_snapshots_to_compact_with_an_offset_shifts_the_full_compaction() {
    // CompactSize 16, offset 3: full compactions at blocks 13, 29, 45, ...
    let (compactor, repository) = compactor_with(16, 3);
    build_chain(&repository, 0, 29);
    let lease = |block| {
        repository
            .try_lease_in_memory_state(&state_id(block, 0), SnapshotTier::InMemoryBase)
            .unwrap()
    };

    assert_eq!(compactor.get_snapshots_to_compact(&lease(29)).len(), 16);
    assert!(
        compactor.get_snapshots_to_compact(&lease(16)).is_empty(),
        "(16 + 3) & -(16 + 3) = 1"
    );
}

#[test]
fn a_partial_compaction_drops_the_compacted_snapshot_one_compact_size_back() {
    let (compactor, repository) = compactor();
    build_chain(&repository, 0, 24);
    assert!(compactor.do_compact_snapshot(&state_id(8, 0)));
    assert!(compactor.do_compact_snapshot(&state_id(16, 0)));

    assert!(compactor.do_compact_snapshot(&state_id(24, 0)));

    let compacted_at = |block| {
        repository
            .try_lease_in_memory_state(&state_id(block, 0), SnapshotTier::InMemoryCompacted)
            .map(|snapshot| (snapshot.from().block_number, snapshot.to().block_number))
    };
    assert_eq!(
        compacted_at(8),
        None,
        "the size-8 compaction at 24 drops the compacted snapshot at 24 - 16"
    );
    assert_eq!(compacted_at(16), Some((0, 16)), "a full compaction drops nothing");
    assert_eq!(compacted_at(24), Some((16, 24)));
    assert_eq!(repository.compacted_snapshot_count(), 2);
}

#[test]
fn do_compact_snapshot_adds_one_compacted_snapshot_once() {
    let (compactor, repository) = compactor();
    build_chain(&repository, 0, 15);
    add_base(
        &repository,
        snapshot(state_id(15, 0), state_id(16, 0), |c| {
            c.set_account(address(0xB2), account(20, 2000))
        }),
    );

    assert!(compactor.do_compact_snapshot(&state_id(16, 0)));
    assert!(
        !compactor.do_compact_snapshot(&state_id(16, 0)),
        "the chain is now the one compacted snapshot"
    );
    assert!(
        !compactor.do_compact_snapshot(&state_id(99, 0)),
        "no base snapshot to compact"
    );

    assert_eq!(repository.compacted_snapshot_count(), 1);
    let compacted = repository
        .try_lease_in_memory_state(&state_id(16, 0), SnapshotTier::InMemoryCompacted)
        .unwrap();
    assert_eq!((compacted.from(), compacted.accounts_count()), (state_id(0, 0), 1));
    assert_eq!(
        repository.in_memory_bytes(),
        172 + compacted.estimate_compacted_memory(),
        "empty bases count nothing"
    );
}

#[rstest]
#[case(32, 1024 * 1024, true)]
#[case(1, 2, true)]
#[case(32, 32, true)]
#[case(32, 16, false)]
#[case(32, 48, false)]
#[case(32, 2_147_483_648, false)]
#[case(10, 8192, false)]
fn schedule_validates_compact_sizes(#[case] compact_size: u64, #[case] max_compact_size: u64, #[case] valid: bool) {
    assert_eq!(
        CompactionSchedule::new(compact_size, max_compact_size, 0).is_ok(),
        valid
    );
}

#[test]
fn schedule_ignores_the_offset_with_compaction_disabled() {
    let disabled = CompactionSchedule::new(1, 8192, 7).unwrap();
    assert_eq!(disabled.offset(), 0);
    assert_eq!(disabled.next_full_compaction_after(&zero_state(0)), u64::MAX);
    assert_eq!(disabled.get_compact_size(16), 1);
    assert_eq!(schedule(16, 7).offset(), 7);
}

#[rstest]
#[case(0, 1, 1)]
#[case(0, 2, 2)]
#[case(0, 4, 4)]
#[case(0, 6, 2)]
#[case(0, 8, 8)]
#[case(0, 10, 2)]
#[case(0, 12, 4)]
#[case(0, 14, 2)]
#[case(0, 16, 16)]
#[case(0, 32, 16)]
#[case(3, 0, 1)]
#[case(3, 13, 16)]
#[case(3, 16, 1)]
#[case(3, 5, 8)]
#[case(3, 29, 16)]
fn get_compact_size(#[case] offset: u64, #[case] block_number: u64, #[case] expected: u64) {
    assert_eq!(schedule(16, offset).get_compact_size(block_number), expected);
}

#[rstest]
#[case(3, 19)]
#[case(3, 35)]
#[case(7, 1_000_007)]
fn an_offset_is_equivalent_to_itself_modulo_the_compact_size(#[case] small: u64, #[case] large: u64) {
    for block in 1..=64 {
        let state = zero_state(block);
        assert_eq!(
            schedule(16, large).get_compact_size(block),
            schedule(16, small).get_compact_size(block)
        );
        assert_eq!(
            schedule(16, large).next_full_compaction_after(&state),
            schedule(16, small).next_full_compaction_after(&state)
        );
    }
}

#[rstest]
#[case(0, 0, 16)]
#[case(16, 0, 32)]
#[case(15, 0, 16)]
#[case(0, 3, 13)]
#[case(13, 3, 29)]
#[case(7, 5, 11)]
#[case(u64::MAX - 3, 0, u64::MAX)]
fn next_full_compaction_after(#[case] from: u64, #[case] offset: u64, #[case] expected: u64) {
    assert_eq!(
        schedule(16, offset).next_full_compaction_after(&zero_state(from)),
        expected
    );
}

#[rstest]
#[case(0, 16)]
#[case(3, 13)]
#[case(7, 9)]
fn next_full_compaction_after_pre_genesis_anchors_at_genesis(#[case] offset: u64, #[case] expected: u64) {
    assert_eq!(
        schedule(16, offset).next_full_compaction_after(&StateId::PRE_GENESIS),
        expected
    );
}

#[rstest]
#[case(0, 0, 8192, 1, false, false)]
#[case(0, 16, 8192, 16, true, false)]
#[case(0, 8, 8192, 8, false, false)]
#[case(0, 32, 8192, 32, false, true)]
#[case(0, 48, 8192, 16, true, false)]
#[case(0, 64, 8192, 64, false, true)]
#[case(3, 13, 8192, 16, true, false)]
#[case(3, 16, 8192, 1, false, false)]
#[case(3, 29, 8192, 32, false, true)]
#[case(0, 32, 16, 16, true, false)]
#[case(0, 64, 32, 32, false, true)]
#[case(0, 128, 32, 32, false, true)]
fn persisted_snapshot_boundaries(
    #[case] offset: u64,
    #[case] block_number: u64,
    #[case] max_compact_size: u64,
    #[case] window: u64,
    #[case] compact_size_boundary: bool,
    #[case] large_boundary: bool,
) {
    let schedule = CompactionSchedule::new(16, max_compact_size, offset).unwrap();
    assert_eq!(schedule.get_persisted_snapshot_compact_size(block_number), window);
    assert_eq!(schedule.is_compact_size_boundary(block_number), compact_size_boundary);
    assert_eq!(schedule.is_large_compaction_boundary(block_number), large_boundary);
}

/// One block's writes in the naive model.
#[derive(Debug, Clone, Default, PartialEq)]
struct Writes {
    accounts: BTreeMap<u8, Option<u64>>,
    slots: BTreeMap<(u8, u8), Option<u64>>,
    clears: BTreeMap<u8, bool>,
    state_nodes: BTreeMap<u8, Vec<u8>>,
    storage_nodes: BTreeMap<(u8, u8), Vec<u8>>,
}

fn writes_strategy() -> impl Strategy<Value = Writes> {
    let small = 0u8..6;
    (
        prop::collection::btree_map(small.clone(), prop::option::of(0u64..50), 0..4),
        prop::collection::btree_map((small.clone(), 0u8..6), prop::option::of(0u64..50), 0..8),
        prop::collection::btree_map(small.clone(), any::<bool>(), 0..2),
        prop::collection::btree_map(0u8..16, prop::collection::vec(any::<u8>(), 0..4), 0..4),
        prop::collection::btree_map((small, 0u8..16), prop::collection::vec(any::<u8>(), 0..4), 0..4),
    )
        .prop_map(|(accounts, slots, clears, state_nodes, storage_nodes)| Writes {
            accounts,
            slots,
            clears,
            state_nodes,
            storage_nodes,
        })
}

fn node_path(nibble: u8) -> TreePath {
    TreePath::from_hex(&format!("{nibble:x}"))
}

fn node_hash(rlp: &[u8]) -> Hash256 {
    let mut hash = [0xEE; 32];
    hash[..rlp.len()].copy_from_slice(rlp);
    hash
}

fn build(block: u64, writes: &Writes) -> Arc<Snapshot> {
    let mut builder = SnapshotContentBuilder::new();
    for (a, nonce) in &writes.accounts {
        builder.set_account(address(*a), nonce.map(|nonce| Account::new(nonce, U256::ZERO)));
    }
    for ((a, s), value) in &writes.slots {
        builder.set_storage(address(*a), U256::from_u64(u64::from(*s)), value.map(U256::from_u64));
    }
    for (a, is_new) in &writes.clears {
        builder.set_self_destruct(address(*a), *is_new);
    }
    for (nibble, rlp) in &writes.state_nodes {
        builder.set_state_node(node_path(*nibble), node_hash(rlp), rlp);
    }
    for ((a, nibble), rlp) in &writes.storage_nodes {
        builder.set_storage_node(account_path(&address(*a)), node_path(*nibble), node_hash(rlp), rlp);
    }
    Arc::new(Snapshot::new(zero_state(block), zero_state(block + 1), builder.build()))
}

proptest! {
    #![proptest_config(ProptestConfig::with_cases(512))]

    #[test]
    fn compaction_matches_a_naive_replay(blocks in prop::collection::vec(writes_strategy(), 1..7), split in 0usize..7) {
        check_compaction(&blocks, split)?;
    }
}

/// Compacting a run, in two levels when `split` falls inside it, equals a naive replay: the newest write wins, and a
/// clear of an existing account drops the slots and storage nodes of its address written by earlier blocks.
fn check_compaction(blocks: &[Writes], split: usize) -> Result<(), TestCaseError> {
    let (compactor, _) = compactor();
    let sources: Vec<Arc<Snapshot>> = blocks
        .iter()
        .enumerate()
        .map(|(block, writes)| build(block as u64, writes))
        .collect();
    let compacted = if split > 0 && split < sources.len() {
        let mut rest = vec![Arc::new(compactor.compact_snapshot_bundle(&sources[..split]))];
        rest.extend_from_slice(&sources[split..]);
        compactor.compact_snapshot_bundle(&rest)
    } else {
        compactor.compact_snapshot_bundle(&sources)
    };

    let mut expected = Writes::default();
    for writes in blocks {
        for (a, is_new) in &writes.clears {
            if *is_new {
                expected.clears.entry(*a).or_insert(true);
            } else {
                expected.clears.insert(*a, false);
                expected.slots.retain(|(address, _), _| address != a);
                expected.storage_nodes.retain(|(address, _), _| address != a);
            }
        }
        expected.accounts.extend(writes.accounts.clone());
        expected.slots.extend(writes.slots.clone());
        expected.state_nodes.extend(writes.state_nodes.clone());
        expected.storage_nodes.extend(writes.storage_nodes.clone());
    }

    let address_of: BTreeMap<Hash256, u8> = (0u8..6).map(|a| (account_path(&address(a)), a)).collect();
    let actual = Writes {
        accounts: compacted
            .accounts()
            .map(|(a, account)| (a[0], account.map(|account| account.nonce)))
            .collect(),
        slots: compacted
            .storages()
            .map(|(key, value)| ((key.address[0], key.slot.0[0] as u8), value.map(|value| value.0[0])))
            .collect(),
        clears: compacted
            .self_destructed_storage_addresses()
            .map(|(a, is_new)| (a[0], is_new))
            .collect(),
        state_nodes: compacted
            .state_nodes()
            .map(|(path, node)| (path.path[0] >> 4, node.rlp.to_vec()))
            .collect(),
        storage_nodes: compacted
            .storage_nodes()
            .map(|(key, node)| {
                (
                    (address_of[&key.address_hash], key.path.path[0] >> 4),
                    node.rlp.to_vec(),
                )
            })
            .collect(),
    };
    prop_assert_eq!(&actual, &expected);
    prop_assert!(
        compacted
            .state_nodes()
            .all(|(_, node)| *node.hash == node_hash(node.rlp))
    );

    let keys: Vec<(Address, U256)> = compacted.storages().map(|(key, _)| (key.address, key.slot)).collect();
    let sorted: BTreeSet<(Address, U256)> = keys.iter().copied().collect();
    prop_assert_eq!(
        keys,
        sorted.into_iter().collect::<Vec<_>>(),
        "slots iterate in key order"
    );
    Ok(())
}
