//! The persisted tier's snapshots, buckets, loader and the repository over them: ports of
//! `PersistedSnapshotRepositoryTests`, `PersistedSnapshotLoaderTests`, the repository cases of
//! `LongFinalityIntegrationTests` and `PersistenceManagerPersistedTests`, the repository half of the finalized-fork
//! cases of `PersistenceManagerTests` (one `AddToPersistence` with nothing to persist is one
//! `RemoveFinalizedPersistedForks` at the persisted state), the validation of a converted snapshot, and the crash cases
//! of the loader.
//!
//! Not ported: `TryAcquire_WhenObservedSnapshotIsRetired_UsesCurrentEntry`, as a bucket hands out `Arc` clones, which
//! cannot be observed retired.

mod common;

use std::path::Path;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};

use common::persisted::{StorageTier, TemporaryTier};
use common::snapshots::{FixedFinality, SettableFinality, state_id};
use common::{hex20, hex32};
use flatdb_core::keccak::keccak256;
use flatdb_core::kv::{KvError, KvResult};
use flatdb_core::persisted::bloom_keys;
use flatdb_core::persisted::list::PersistedSnapshotList;
use flatdb_core::persisted::stack::{PersistedLayer, PersistedSnapshotStack};
use flatdb_core::persisted::storage::arena_manager::ArenaConfig;
use flatdb_core::persisted::storage::catalog::{CatalogKv, MemCatalogKv};
use flatdb_core::persisted::validate::validate_persisted_snapshot;
use flatdb_core::persisted::{PersistedTierConfig, TierError};
use flatdb_core::repository::Edge;
use flatdb_core::schedule::CompactionSchedule;
use flatdb_core::snapshot::content::SnapshotContentBuilder;
use flatdb_core::snapshot::{Snapshot, SnapshotTier};
use flatdb_core::stats::{TierGaugeRow, TierStats};
use flatdb_core::types::{Account, StateId, TreePath, U256};
use rstest::rstest;

const ADDRESS_A: &str = "0xa000000000000000000000000000000000000001";
const ADDRESS_B: &str = "0xb000000000000000000000000000000000000002";
const ADDRESS_C: &str = "0xc000000000000000000000000000000000000003";

fn state(block: u64) -> StateId {
    StateId::new(block, [block as u8 ^ 0x5A; 32])
}

fn snapshot(from: u64, to: u64, fill: impl FnOnce(&mut SnapshotContentBuilder)) -> Snapshot {
    let mut builder = SnapshotContentBuilder::new();
    fill(&mut builder);
    Snapshot::new(state(from), state(to), builder.build())
}

/// A snapshot setting `address` to an account of `balance`.
fn account_snapshot(from: u64, to: u64, address: [u8; 20], balance: u64) -> Snapshot {
    snapshot(from, to, |builder| {
        builder.set_account(address, Some(Account::new(1, U256::from_u64(balance))));
    })
}

/// The address of base `index` of a chain, each base writing its own.
fn chain_address(index: u64) -> [u8; 20] {
    let mut address = [0x11; 20];
    address[..8].copy_from_slice(&index.to_le_bytes());
    address
}

struct Fixture {
    directory: tempfile::TempDir,
    catalog: MemCatalogKv,
    stats: Arc<TierStats>,
    arena: ArenaConfig,
    config: PersistedTierConfig,
}

impl Fixture {
    fn new(arena_file_size_bytes: i64, bloom_bits_per_key: f64) -> Self {
        Self {
            directory: tempfile::tempdir().unwrap(),
            catalog: MemCatalogKv::default(),
            stats: Arc::default(),
            arena: common::arena::config(arena_file_size_bytes),
            config: PersistedTierConfig {
                bloom_bits_per_key,
                validate_persisted_snapshot: true,
                processor_count: 4,
            },
        }
    }

    fn open(&self) -> StorageTier {
        StorageTier::open(
            self.directory.path(),
            &self.catalog,
            self.arena,
            self.config,
            self.stats.clone(),
        )
        .unwrap()
    }

    /// The tier over the fixture's catalog, failing every row removal while `fail_removes` is set.
    fn open_failing_removes(&self, fail_removes: &Arc<AtomicBool>) -> StorageTier {
        StorageTier::open_with_catalog(
            self.directory.path(),
            &self.catalog.rows(),
            Box::new(FailingRemovals {
                rows: self.catalog.clone(),
                fail_removes: fail_removes.clone(),
            }),
            self.arena,
            self.config,
            self.stats.clone(),
            Arc::new(FixedFinality::default()),
        )
        .unwrap()
    }

    fn files(&self, kind: &str) -> Vec<String> {
        let mut names = std::fs::read_dir(self.directory.path().join("persistedSnapshot").join(kind))
            .unwrap()
            .map(|entry| entry.unwrap().file_name().into_string().unwrap())
            .collect::<Vec<_>>();
        names.sort();
        names
    }

    fn file_length(&self, kind: &str, name: &str) -> u64 {
        std::fs::metadata(self.directory.path().join("persistedSnapshot").join(kind).join(name))
            .unwrap()
            .len()
    }
}

struct FailingRemovals {
    rows: MemCatalogKv,
    fail_removes: Arc<AtomicBool>,
}

impl CatalogKv for FailingRemovals {
    fn put(&self, key: &[u8], value: &[u8]) -> KvResult<()> {
        self.rows.put(key, value)
    }

    fn key_exists(&self, key: &[u8]) -> KvResult<bool> {
        self.rows.key_exists(key)
    }

    fn remove(&self, key: &[u8]) -> KvResult<()> {
        if self.fail_removes.load(Ordering::Relaxed) {
            return Err(KvError::Callback {
                operation: "remove",
                status: 1,
            });
        }
        self.rows.remove(key)
    }
}

fn balance(tier: &StorageTier, to: u64, tier_kind: SnapshotTier, address: &str) -> Option<U256> {
    let snapshot = tier.repository.try_lease_persisted_state(&state(to), tier_kind)?;
    snapshot
        .try_get_account(&hex20(address))
        .unwrap()
        .flatten()
        .map(|account| account.balance)
}

#[rstest]
fn converted_snapshots_read_back_every_kind_of_entry(#[values(0.0, 14.0)] bloom_bits_per_key: f64) {
    let fixture = Fixture::new(4096, bloom_bits_per_key);
    let tier = fixture.open();
    let state_path = TreePath::new(
        hex32("0x1234560000000000000000000000000000000000000000000000000000000000"),
        4,
    );
    let storage_trie = hex32("0x5555555555555555555555555555555555555555555555555555555555555555");
    let storage_path = TreePath::new(
        hex32("0x6600000000000000000000000000000000000000000000000000000000000000"),
        6,
    );
    let slot_value = U256::from_be_slice(&common::hex("0xcdab"));

    tier.convert(&snapshot(0, 1, |builder| {
        builder.set_account(hex20(ADDRESS_A), Some(Account::new(1, U256::from_u64(500))));
        builder.set_account(hex20(ADDRESS_C), None);
        builder.set_storage(hex20(ADDRESS_C), U256::from_u64(42), Some(slot_value));
        builder.set_storage(hex20(ADDRESS_C), U256::from_u64(43), None);
        builder.set_self_destruct(hex20(ADDRESS_B), false);
        builder.set_state_node(state_path, [0x01; 32], &common::hex("0xc0"));
        builder.set_storage_node(storage_trie, storage_path, [0x02; 32], &common::hex("0xc180"));
    }));
    tier.convert(&snapshot(1, 2, |builder| {
        builder.set_state_node(state_path, [0x03; 32], &common::hex("0xc20180"));
    }));

    assert_eq!(tier.repository.persisted_snapshot_count(), 2);
    let first = tier.repository.try_lease_base_persisted_snapshot(&state(1)).unwrap();
    assert_eq!(
        (first.from(), first.to(), first.tier()),
        (state(0), state(1), SnapshotTier::PersistedBase)
    );
    assert!(first.header().may_have_self_destructs);
    assert_eq!(
        first.try_get_account(&hex20(ADDRESS_A)).unwrap(),
        Some(Some(Account::new(1, U256::from_u64(500))))
    );
    assert_eq!(
        first.try_get_account(&hex20(ADDRESS_C)).unwrap(),
        Some(None),
        "a deletion"
    );
    assert_eq!(first.try_get_account(&hex20(ADDRESS_B)).unwrap(), None);
    assert_eq!(
        first.try_get_slot(&hex20(ADDRESS_C), &U256::from_u64(42)).unwrap(),
        Some(Some(slot_value))
    );
    assert_eq!(
        first.try_get_slot(&hex20(ADDRESS_C), &U256::from_u64(43)).unwrap(),
        Some(None)
    );
    assert_eq!(
        first.try_get_self_destruct_flag(&hex20(ADDRESS_B)).unwrap(),
        Some(false)
    );
    assert_eq!(
        first.try_load_storage_node_rlp(&storage_trie, &storage_path).unwrap(),
        Some(common::hex("0xc180"))
    );

    // The newer base holds the newer node.
    let second = tier.repository.try_lease_base_persisted_snapshot(&state(2)).unwrap();
    assert!(!second.header().may_have_self_destructs);
    assert_eq!(
        first.try_load_state_node_rlp(&state_path).unwrap(),
        Some(common::hex("0xc0"))
    );
    assert_eq!(
        second.try_load_state_node_rlp(&state_path).unwrap(),
        Some(common::hex("0xc20180"))
    );
}

#[test]
fn converting_the_same_state_twice_overwrites_net_zero() {
    let fixture = Fixture::new(4096, 14.0);
    let tier = fixture.open();
    tier.convert(&account_snapshot(0, 1, hex20(ADDRESS_A), 1000));
    let newest = tier.convert(&account_snapshot(0, 1, hex20(ADDRESS_A), 2000));

    assert_eq!(tier.repository.persisted_snapshot_count(), 1);
    assert_eq!(
        balance(&tier, 1, SnapshotTier::PersistedBase, ADDRESS_A),
        Some(U256::from_u64(2000))
    );
    assert_eq!(
        fixture.stats.gauges.row(SnapshotTier::PersistedBase, 1),
        TierGaugeRow {
            count: 1,
            memory: newest.size(),
            active: 1
        }
    );
    assert_eq!(fixture.catalog.rows().len(), 1);
}

#[rstest]
fn snapshots_survive_a_reload_with_their_blob_range(
    #[values(false, true)] with_trie_node: bool,
    #[values(0.0, 14.0)] bloom_bits_per_key: f64,
) {
    let fixture = Fixture::new(64 * 1024, bloom_bits_per_key);
    let path = TreePath::new(
        hex32("0xabcd000000000000000000000000000000000000000000000000000000000000"),
        4,
    );
    let tier = fixture.open();
    let converted = tier.convert(&snapshot(0, 1, |builder| {
        builder.set_account(hex20(ADDRESS_A), Some(Account::new(1, U256::from_u64(1000))));
        if with_trie_node {
            builder.set_state_node(path, [0x01; 32], &common::hex("0xc28080"));
        }
    }));
    let blob_range = converted.blob_range();
    assert_eq!(blob_range.is_empty(), !with_trie_node);
    drop(converted);
    tier.shut_down();

    let tier = fixture.open();
    assert_eq!(tier.repository.persisted_snapshot_count(), 1);
    let reloaded = tier.repository.try_lease_base_persisted_snapshot(&state(1)).unwrap();
    assert_eq!(reloaded.blob_range(), blob_range, "read back from the table's metadata");
    assert!(
        reloaded.header().may_have_self_destructs,
        "unknown for a loaded snapshot"
    );
    assert_eq!(
        balance(&tier, 1, SnapshotTier::PersistedBase, ADDRESS_A),
        Some(U256::from_u64(1000))
    );
    // Without blooms nothing is rebuilt: the always-true placeholder stays, with no keys.
    assert_eq!(
        reloaded.persisted_bloom().filter().count() > 0,
        bloom_bits_per_key > 0.0
    );
    if with_trie_node {
        reloaded.advise_will_need_blob_range();
        reloaded.advise_dont_need_blob_range();
        reloaded.demote();
        assert_eq!(
            reloaded.try_load_state_node_rlp(&path).unwrap(),
            Some(common::hex("0xc28080"))
        );
    }
}

#[test]
fn removing_persisted_states_prunes_their_rows_and_releases_their_files() {
    let fixture = Fixture::new(4096, 14.0);
    let tier = fixture.open();
    for block in 1..=3 {
        tier.convert(&snapshot(block - 1, block, |builder| {
            builder.set_state_node(TreePath::from_hex("12"), [block as u8; 32], &[0xC1, block as u8]);
        }));
    }
    assert_eq!(tier.repository.persisted_snapshot_count(), 3);
    let blob_file = fixture.files("blob")[0].clone();

    tier.repository.remove_persisted_states_until(2).unwrap();
    assert_eq!(tier.repository.persisted_snapshot_count(), 2);
    assert_eq!(fixture.catalog.rows().len(), 2);
    assert!(tier.repository.remove_persisted_state_exact(&state(3)).unwrap());
    assert!(!tier.repository.remove_persisted_state_exact(&state(3)).unwrap());
    assert_eq!(fixture.stats.counters.prunes.load(Ordering::Relaxed), 2);
    assert!(fixture.file_length("blob", &blob_file) > 0);

    // The last snapshot naming the blob file leaves it to the manager alone, which resets it to empty.
    assert!(tier.repository.remove_persisted_state_exact(&state(2)).unwrap());
    assert_eq!(fixture.file_length("blob", &blob_file), 0);
    assert!(fixture.catalog.rows().is_empty());
}

#[rstest]
fn many_bases_share_blob_files(#[values(100, 1000)] count: u64) {
    let fixture = Fixture::new(64 * 1024, 14.0);
    let tier = fixture.open();
    for block in 1..=count {
        tier.convert(&account_snapshot(block - 1, block, chain_address(block % 7), block));
    }
    assert_eq!(tier.repository.persisted_snapshot_count(), count as i64);
    assert!((fixture.files("blob").len() as u64) < count);
}

#[test]
fn leasing_bases_in_a_range_tiles_the_window_newest_first() {
    let fixture = Fixture::new(64 * 1024, 14.0);
    let tier = fixture.open();
    for block in 1..=3 {
        tier.convert(&account_snapshot(block - 1, block, chain_address(block), block));
    }
    let bases = tier.repository.lease_base_snapshots_in_range(&state(0), &state(3));
    assert_eq!(
        bases.iter().map(|base| base.to()).collect::<Vec<_>>(),
        [state(3), state(2), state(1)]
    );
    assert_eq!(
        tier.repository
            .lease_base_snapshots_in_range(&state(1), &state(3))
            .len(),
        2
    );
}

/// `LoadFromCatalog_ReconstructsBloom_SharedFromWidest`, `LoadFromCatalog_RoundTripsBaseAndCompactSizedAtSameTo` and
/// `LoadFromCatalog_Parallel_PreservesOrderingAndDicts`: bases and CompactSized snapshots at the same states load back
/// apart, and the widest snapshots' rebuilt blooms are shared by the bases they hold.
#[rstest]
#[case(4, 4)]
#[case(32, 8)]
fn a_reload_shares_the_widest_snapshots_blooms(#[case] count: u64, #[case] compact_size: u64) {
    let fixture = Fixture::new(64 * 1024, 14.0);
    let schedule = CompactionSchedule::new(compact_size, 1024 * 1024, 0).unwrap();
    let boundaries = [compact_size, 2 * compact_size]
        .into_iter()
        .filter(|&boundary| boundary <= count)
        .collect::<Vec<_>>();
    let tier = fixture.open();
    for block in 1..=count {
        tier.convert(&account_snapshot(block - 1, block, chain_address(block), block));
    }
    let compactor = tier.compactor(schedule);
    for &boundary in &boundaries {
        assert!(compactor.do_compact_compact_sized(&state(boundary)).unwrap());
    }
    drop(compactor);
    let expected_count = (count + boundaries.len() as u64) as i64;
    assert_eq!(tier.repository.persisted_snapshot_count(), expected_count);
    tier.shut_down();

    let tier = fixture.open();
    assert_eq!(tier.repository.persisted_snapshot_count(), expected_count);
    // The twins sharing a bloom replaced the snapshots they were made of.
    let bases = fixture.stats.gauges.row(SnapshotTier::PersistedBase, 1);
    assert_eq!((bases.count, bases.active), (count as i64, count as i64));
    assert_eq!(
        tier.repository
            .lease_base_snapshots_in_range(&state(0), &state(count))
            .len() as u64,
        count
    );
    for &boundary in &boundaries {
        let compact_sized = tier
            .repository
            .try_lease_persisted_state(&state(boundary), SnapshotTier::PersistedCompactSized)
            .unwrap();
        assert_eq!(compact_sized.from(), state(boundary - compact_size));
        let shared = compact_sized.persisted_bloom();
        assert!(shared.filter().count() > 0, "rebuilt from the merged table");
        for block in boundary - compact_size + 1..=boundary {
            assert!(
                shared
                    .filter()
                    .might_contain(bloom_keys::address_key(&chain_address(block)))
            );
            let base = tier
                .repository
                .try_lease_base_persisted_snapshot(&state(block))
                .unwrap();
            assert!(
                Arc::ptr_eq(base.persisted_bloom(), shared),
                "base {block} shares the bloom"
            );
        }
    }
}

/// The walks cross from memory into the persisted tier and never back: a read chain lists the persisted part below
/// the in-memory one, the newest state counts persisted tips, and a persist candidate may be persisted.
#[test]
fn walks_cross_from_memory_into_the_persisted_tier() {
    let fixture = Fixture::new(64 * 1024, 14.0);
    let tier = fixture.open();
    for block in 1..=2 {
        tier.convert(&account_snapshot(block - 1, block, chain_address(block), block));
    }
    let in_memory = Arc::new(account_snapshot(2, 3, chain_address(3), 3));
    assert!(tier.repository.try_add(in_memory, SnapshotTier::InMemoryBase));
    tier.repository.add_state_id(state(3));

    let assembled = tier.repository.assemble_snapshots(&state(3), &state(0), 4);
    assert_eq!(
        assembled
            .persisted
            .iter()
            .map(|snapshot| snapshot.to())
            .collect::<Vec<_>>(),
        [state(1), state(2)]
    );
    assert_eq!(assembled.in_memory.len(), 1);
    assert_eq!(assembled.snapshot_count(), 3);
    assert!(matches!(
        tier.repository.find_snapshot_to_persist(&state(3), &state(0), 4),
        Some(Edge::Persisted(snapshot)) if snapshot.to() == state(1)
    ));
    assert!(matches!(
        tier.repository.find_snapshot_to_persist(&state(3), &state(2), 4),
        Some(Edge::InMemory(snapshot)) if snapshot.to() == state(3)
    ));

    tier.convert(&account_snapshot(3, 5, chain_address(5), 5));
    assert_eq!(tier.repository.get_last_snapshot_id(), Some(state(5)));
}

#[test]
fn a_stack_over_a_real_bloom_finds_present_keys_and_skips_absent_addresses() {
    let fixture = Fixture::new(64 * 1024, 14.0);
    let tier = fixture.open();
    let converted = tier.convert(&snapshot(0, 1, |builder| {
        builder.set_account(hex20(ADDRESS_A), Some(Account::new(1, U256::from_u64(123))));
        builder.set_storage(hex20(ADDRESS_A), U256::from_u64(1), Some(U256::from_u64(0x55)));
    }));
    let stack = PersistedSnapshotStack::new(PersistedSnapshotList::new(vec![converted]));

    assert_eq!(
        stack.try_get_account(&hex20(ADDRESS_A)).unwrap(),
        Some(Some(Account::new(1, U256::from_u64(123))))
    );
    assert_eq!(
        stack.try_get_slot(&hex20(ADDRESS_A), &U256::from_u64(1), -1).unwrap(),
        Some(Some(U256::from_u64(0x55)))
    );
    for absent in [ADDRESS_B, ADDRESS_C] {
        assert_eq!(stack.try_get_account(&hex20(absent)).unwrap(), None);
    }
}

/// `Load_FiltersEntriesReferencingMissingArenasBeforeOpen`, and what follows: the rows stay in the catalog, their arena
/// id is never handed out again, and the blob files only they named are swept.
#[test]
fn the_loader_drops_rows_of_missing_arena_files_and_never_reuses_their_ids() {
    let fixture = Fixture::new(64 * 1024, 14.0);
    let tier = fixture.open();
    tier.convert(&snapshot(0, 1, |builder| {
        builder.set_state_node(TreePath::from_hex("1"), [0x01; 32], &[0xC0]);
    }));
    tier.shut_down();
    assert_eq!(fixture.files("arena"), ["small_arena_0000.bin"]);
    std::fs::remove_file(
        fixture
            .directory
            .path()
            .join("persistedSnapshot/arena/small_arena_0000.bin"),
    )
    .unwrap();

    let tier = fixture.open();
    assert_eq!(tier.repository.persisted_snapshot_count(), 0);
    assert_eq!(fixture.catalog.rows().len(), 1, "the catalog is not rewritten");
    assert!(fixture.files("blob").is_empty(), "swept: no loaded snapshot names it");

    tier.convert(&account_snapshot(1, 2, hex20(ADDRESS_A), 1));
    assert_eq!(fixture.files("arena"), ["small_arena_0001.bin"]);
    assert_eq!(fixture.files("blob"), ["blob_0001.bin"]);
}

/// A crash between the data fsyncs and the catalog row of a base: its bytes stay on disk without a row. The next
/// session truncates nothing at open: a blob file no loaded snapshot names is swept, one still named keeps the orphaned
/// tail, and the orphaned arena bytes stay past the frontier. Every read stays exact.
#[rstest]
fn orphans_of_a_crash_before_the_catalog_row_are_ignored_by_the_next_session(
    #[values(false, true)] own_blob_file: bool,
) {
    let fixture_size = if own_blob_file { 8 * 1024 } else { 1024 * 1024 };
    let fixture = Fixture::new(fixture_size, 14.0);
    let tier = fixture.open();
    let first = tier.convert(&snapshot(0, 1, |builder| {
        builder.set_account(hex20(ADDRESS_A), Some(Account::new(1, U256::from_u64(1))));
        builder.set_state_node(TreePath::from_hex("1"), [0x01; 32], &[0xC1, 0x01]);
    }));
    let first_location = first.location();
    drop(first);
    // Large enough to need a blob file of its own when files are small.
    let orphan = tier.convert(&snapshot(1, 2, |builder| {
        for index in 0..40u8 {
            builder.set_state_node(TreePath::from_hex(&format!("{index:02x}")), [index; 32], &[0xC1, index]);
        }
    }));
    let orphan_location = orphan.location();
    drop(orphan);
    let blob_files = fixture.files("blob");
    assert_eq!(blob_files.len(), if own_blob_file { 2 } else { 1 });
    let blob_lengths = blob_files
        .iter()
        .map(|name| fixture.file_length("blob", name))
        .collect::<Vec<_>>();

    // The crash: the files are kept, the row was never written.
    let key = fixture.catalog.rows()[1].0.clone();
    fixture.catalog.remove(&key).unwrap();
    tier.shut_down();

    let tier = fixture.open();
    assert_eq!(tier.repository.persisted_snapshot_count(), 1);
    assert_eq!(fixture.files("blob"), blob_files[..1]);
    assert_eq!(
        fixture.file_length("blob", &blob_files[0]),
        blob_lengths[0],
        "no truncation at open"
    );
    // A loaded arena file never rejoins the pool of files with room, so its orphaned bytes stay past its frontier and
    // the next table goes to a new file.
    let next = tier.convert(&account_snapshot(1, 2, hex20(ADDRESS_B), 2));
    assert!(next.location().arena_id > first_location.arena_id.max(orphan_location.arena_id));
    assert_eq!(
        balance(&tier, 1, SnapshotTier::PersistedBase, ADDRESS_A),
        Some(U256::from_u64(1))
    );
    assert_eq!(
        balance(&tier, 2, SnapshotTier::PersistedBase, ADDRESS_B),
        Some(U256::from_u64(2))
    );
    let first = tier.repository.try_lease_base_persisted_snapshot(&state(1)).unwrap();
    assert_eq!(
        first.try_load_state_node_rlp(&TreePath::from_hex("1")).unwrap(),
        Some(vec![0xC1, 0x01])
    );
}

/// The shutdown mark, which dropping the tier also sets as the C# container disposes the loader, keeps every file of
/// the live snapshots for the next session; a snapshot removed before goes with its files. Either way every gauge and
/// counter of the tier returns to zero.
#[rstest]
#[case::shut_down(StorageTier::shut_down, 2)]
#[case::dropped(drop::<StorageTier>, 2)]
#[case::removed_first(remove_every_snapshot_and_drop, 0)]
fn shutdown_keeps_the_files_of_the_live_snapshots(#[case] release: fn(StorageTier), #[case] kept: i64) {
    let fixture = Fixture::new(64 * 1024, 14.0);
    let tier = fixture.open();
    for block in 1..=2 {
        tier.convert(&snapshot(block - 1, block, |builder| {
            builder.set_state_node(TreePath::from_hex("1"), [block as u8; 32], &[0xC1, block as u8]);
        }));
    }
    release(tier);

    assert_eq!(fixture.files("arena").is_empty(), kept == 0);
    assert_eq!(fixture.files("blob").is_empty(), kept == 0);
    assert_eq!(
        fixture.stats.gauges.row(SnapshotTier::PersistedBase, 1),
        TierGaugeRow::default()
    );
    let counters = &fixture.stats.counters;
    for counter in [
        &counters.bloom_memory,
        &counters.bloom_count,
        &counters.arena_file_count,
        &counters.arena_allocated_bytes,
        &counters.blob_file_count,
        &counters.blob_allocated_bytes,
        &counters.arena_reservation_count,
        &counters.arena_reservation_bytes,
    ] {
        assert_eq!(counter.load(Ordering::Relaxed), 0);
    }
    assert_eq!(fixture.open().repository.persisted_snapshot_count(), kept);
}

fn remove_every_snapshot_and_drop(tier: StorageTier) {
    tier.repository.remove_persisted_states_until(u64::MAX).unwrap();
}

/// A catalog removal that fails leaves the row, so the snapshot keeps the files it names, as C# does by throwing before
/// releasing its lease: the blob file is not reset and the next session reads the snapshot back.
#[test]
fn a_failed_catalog_removal_keeps_the_files_its_row_names() {
    let fixture = Fixture::new(64 * 1024, 14.0);
    let fail_removes = Arc::new(AtomicBool::new(false));
    let tier = fixture.open_failing_removes(&fail_removes);
    tier.convert(&snapshot(0, 1, |builder| {
        builder.set_account(hex20(ADDRESS_A), Some(Account::new(1, U256::from_u64(7))));
        builder.set_state_node(TreePath::from_hex("1"), [0x01; 32], &[0xC1, 0x01]);
    }));
    let blob_file = fixture.files("blob")[0].clone();
    let blob_length = fixture.file_length("blob", &blob_file);

    fail_removes.store(true, Ordering::Relaxed);
    assert!(matches!(
        tier.repository.remove_persisted_state_exact(&state(1)),
        Err(TierError::Catalog(_))
    ));
    assert_eq!(tier.repository.persisted_snapshot_count(), 0);
    assert_eq!(fixture.file_length("blob", &blob_file), blob_length);
    drop(tier);

    let tier = fixture.open();
    assert_eq!(
        balance(&tier, 1, SnapshotTier::PersistedBase, ADDRESS_A),
        Some(U256::from_u64(7))
    );
    let reloaded = tier.repository.try_lease_base_persisted_snapshot(&state(1)).unwrap();
    assert_eq!(
        reloaded.try_load_state_node_rlp(&TreePath::from_hex("1")).unwrap(),
        Some(vec![0xC1, 0x01])
    );
}

/// `ValidatePersistedSnapshot` on a snapshot that is not the converted one: the C# message, and the dump named in it.
#[rstest]
#[case::account(
    |builder: &mut SnapshotContentBuilder| builder.set_account(hex20(ADDRESS_A), Some(Account::new(1, U256::from_u64(9)))),
    "Account 0xa000000000000000000000000000000000000001 mismatch"
)]
#[case::deleted_account(
    |builder: &mut SnapshotContentBuilder| builder.set_account(hex20(ADDRESS_A), None),
    "Account 0xa000000000000000000000000000000000000001 should be null but has RLP data"
)]
#[case::missing_slot(
    |builder: &mut SnapshotContentBuilder| builder.set_storage(hex20(ADDRESS_A), U256([0, 1, 0, 0]), None),
    "Storage 0xa000000000000000000000000000000000000001:18446744073709551616 not found in persisted snapshot"
)]
#[case::self_destruct(
    |builder: &mut SnapshotContentBuilder| builder.set_self_destruct(hex20(ADDRESS_B), true),
    "SelfDestruct 0xb000000000000000000000000000000000000002 mismatch: expected True, got False"
)]
#[case::state_node(
    |builder: &mut SnapshotContentBuilder| builder.set_state_node(TreePath::from_hex("12"), [0x07; 32], &[0xC1, 0x02]),
    "StateNode at path length 2 RLP mismatch"
)]
#[case::storage_node(
    |builder: &mut SnapshotContentBuilder| builder.set_storage_node([0x33; 32], TreePath::from_hex("4"), [0x07; 32], &[0xC0]),
    "StorageNode 0x3333333333333333333333333333333333333333333333333333333333333333 at path length 1 not found in persisted snapshot"
)]
fn validation_dumps_a_snapshot_that_does_not_read_back(
    #[case] differ: fn(&mut SnapshotContentBuilder),
    #[case] message: &str,
) {
    let fixture = Fixture::new(64 * 1024, 14.0);
    let tier = fixture.open();
    let converted = tier.convert(&snapshot(0, 1, |builder| {
        builder.set_account(hex20(ADDRESS_A), Some(Account::new(1, U256::from_u64(1))));
        builder.set_self_destruct(hex20(ADDRESS_B), false);
        builder.set_state_node(TreePath::from_hex("12"), [0x07; 32], &[0xC1, 0x01]);
    }));
    let source = snapshot(0, 1, differ);
    let dump_directory = tempfile::tempdir().unwrap();

    let Err(TierError::Validation(error)) = validate_persisted_snapshot(&source, &converted, dump_directory.path())
    else {
        panic!("validation passed");
    };
    let file = dump_directory.path().join("broken.0.1.json");
    assert_eq!(error, format!("{message}. Dumped snapshot to {}", file.display()));
    let dump: serde_json::Value = serde_json::from_str(&std::fs::read_to_string(&file).unwrap()).unwrap();
    let kinds = dump.as_object().unwrap().keys().cloned().collect::<Vec<_>>();
    assert_eq!(
        kinds,
        ["accounts", "selfDestructed", "stateNodes", "storageNodes", "storages"]
    );
    assert!(validate_persisted_snapshot(&source, &converted, Path::new("/nonexistent")).is_err());
}

/// `Repository_Restart_PreservesAllData`: a restart keeps every snapshot with its trie nodes and only its own accounts,
/// whether each table fills an arena file of its own or the tables share one, holes punched on reclaim.
#[rstest]
fn a_restart_keeps_every_snapshot_and_its_trie_nodes(#[values(4096, 1024 * 1024)] arena_file_size_bytes: i64) {
    let mut fixture = Fixture::new(arena_file_size_bytes, 14.0);
    fixture.arena.punch_hole_on_reclaim = true;
    // Ten nodes of 503 bytes per snapshot take the shared blob file past its first page, which a stray reset zeroes.
    let node_rlp = |fill: u8| [&common::hex("0xb901f4")[..], &[fill; 500]].concat();
    let paths = |block: u64| (1..=10u8).map(move |index| TreePath::new([block as u8 * 16 + index; 32], 4));
    let chain = [(1, 0xAA, ADDRESS_A, ADDRESS_B), (2, 0xBB, ADDRESS_B, ADDRESS_A)];

    let tier = fixture.open();
    for (block, fill, address, _) in chain {
        tier.convert(&snapshot(block - 1, block, |builder| {
            for path in paths(block) {
                builder.set_state_node(path, [fill; 32], &node_rlp(fill));
            }
            builder.set_account(hex20(address), Some(Account::new(1, U256::from_u64(block * 100))));
        }));
    }
    tier.shut_down();

    assert!(
        fixture
            .files("arena")
            .iter()
            .any(|name| name.starts_with("small_arena_"))
    );
    let blob_files = fixture.files("blob");
    assert!(!blob_files.is_empty());
    for name in &blob_files {
        let length = fixture.file_length("blob", name);
        assert!(length > 0 && length <= 1024 * 1024, "{name}: {length} bytes");
    }

    let tier = fixture.open();
    assert_eq!(tier.repository.persisted_snapshot_count(), 2);
    for (block, fill, address, other_address) in chain {
        let reloaded = tier
            .repository
            .try_lease_base_persisted_snapshot(&state(block))
            .unwrap();
        for path in paths(block) {
            assert_eq!(reloaded.try_load_state_node_rlp(&path).unwrap(), Some(node_rlp(fill)));
        }
        assert_eq!(
            balance(&tier, block, SnapshotTier::PersistedBase, address),
            Some(U256::from_u64(block * 100))
        );
        assert_eq!(reloaded.try_get_account(&hex20(other_address)).unwrap(), None);
    }
}

/// `Prune_AfterRestart_Works` and `EmptySnapshot_PersistsAndLoads`.
#[test]
fn pruning_after_a_restart_lasts_into_the_next_session() {
    let fixture = Fixture::new(4096, 14.0);
    let tier = fixture.open();
    let empty = tier.convert(&snapshot(5, 6, |_| {}));
    assert_eq!(empty.try_get_account(&hex20(ADDRESS_A)).unwrap(), None);
    assert_eq!(
        empty.try_load_state_node_rlp(&TreePath::new([0x77; 32], 4)).unwrap(),
        None
    );
    drop(empty);
    for (from, to, address) in [(0, 1, ADDRESS_A), (1, 2, ADDRESS_B), (2, 5, ADDRESS_C)] {
        tier.convert(&account_snapshot(from, to, hex20(address), to));
    }
    tier.shut_down();

    let tier = fixture.open();
    assert_eq!(tier.repository.persisted_snapshot_count(), 4);
    tier.repository.remove_persisted_states_until(3).unwrap();
    assert_eq!(tier.repository.persisted_snapshot_count(), 2);
    tier.shut_down();

    assert_eq!(fixture.open().repository.persisted_snapshot_count(), 2);
}

/// A state of the `RemoveSiblingAndDescendents` cases: its block is the name's last digit, its root the name's hash.
fn named_state(name: &str) -> StateId {
    let block = name[name.len() - 1..].parse().unwrap();
    StateId::new(block, keccak256(name.as_bytes()))
}

fn named_edges(edges: &str) -> impl Iterator<Item = (StateId, StateId)> + '_ {
    edges.split_whitespace().map(|edge| {
        let (from, to) = edge.split_once('-').unwrap();
        (named_state(from), named_state(to))
    })
}

/// The `RemoveSiblingAndDescendents_*` cases of `PersistenceManagerPersistedTests`: over the persisted chain
/// s0-s1-s2 and the `persisted` and `in_memory` edges, persisting `canonical` removes exactly the orphans above it,
/// found through the persisted tier and bounded by its newest state.
#[rstest]
#[case::orphans_reached_through_the_tier("s2-c3 c3-c4 s2-nc3 nc3-nc4", "c4-c5", "c3", "nc4", "c3 c4 nc3 c5")]
#[case::an_orphan_above_the_in_memory_tip("s2-c3 s2-nc3 nc3-nc4", "s2-c3", "c3", "nc4", "c3 nc3")]
#[case::a_linear_chain("s2-s3", "", "s1", "", "s2 s3")]
fn persisting_removes_the_persisted_orphans_above_the_canonical_state(
    #[case] persisted: &str,
    #[case] in_memory: &str,
    #[case] canonical: &str,
    #[case] removed: &str,
    #[case] kept: &str,
) {
    let tier = TemporaryTier::new();
    for (from, to) in named_edges("s0-s1 s1-s2").chain(named_edges(persisted)) {
        tier.convert(&Snapshot::new(from, to, SnapshotContentBuilder::new().build()));
    }
    for (from, to) in named_edges(in_memory) {
        assert!(tier.repository.try_add(
            Arc::new(Snapshot::new(from, to, SnapshotContentBuilder::new().build())),
            SnapshotTier::InMemoryBase
        ));
        tier.repository.add_state_id(to);
    }
    let persisted_count = tier.repository.persisted_snapshot_count();

    tier.repository
        .remove_sibling_and_descendents(&named_state(canonical))
        .unwrap();

    for name in removed.split_whitespace() {
        assert!(!tier.repository.has_state(&named_state(name)), "{name} removed");
    }
    for name in kept.split_whitespace() {
        assert!(tier.repository.has_state(&named_state(name)), "{name} kept");
    }
    assert_eq!(
        tier.repository.persisted_snapshot_count(),
        persisted_count - removed.split_whitespace().count() as i64
    );
}

/// `PersistBase`: a persisted base from `from` to `to` writing account A.
fn persist_base(tier: &TemporaryTier, from: StateId, to: StateId) {
    let mut builder = SnapshotContentBuilder::new();
    builder.set_account(hex20(ADDRESS_A), Some(Account::new(1, U256::from_u64(100))));
    tier.convert(&Snapshot::new(from, to, builder.build()));
}

const BLOCK_0: StateId = StateId::new(0, [0; 32]);

/// `AddToPersistence_PrunesOnlyKnownFinalizedPersistedForks`, direct: the forks at heights whose canonical root is
/// known are removed with their catalog rows; a reader of one keeps reading until it lets go, and a root that becomes
/// known later is asked for again.
#[rstest]
fn only_forks_at_heights_with_a_known_canonical_root_are_removed(
    #[values(0, 1, 2)] finalized_block: u64,
    #[values(0, 1, 2)] root_lookup_ceiling: u64,
) {
    let finality = Arc::new(SettableFinality::default());
    let tier = TemporaryTier::with_finality(finality.clone());
    let (canonical_1, canonical_2) = (state_id(1, 1), state_id(2, 1));
    let (orphan_1, orphan_2) = (state_id(1, 2), state_id(2, 2));
    let (tip, sibling) = (state_id(3, 1), state_id(3, 2));
    for (from, to) in [
        (BLOCK_0, canonical_1),
        (canonical_1, canonical_2),
        (BLOCK_0, orphan_1),
        (orphan_1, orphan_2),
        (canonical_2, tip),
        (canonical_2, sibling),
    ] {
        persist_base(&tier, from, to);
    }
    tier.repository.set_last_committed_state_id(tip);
    finality.set_finalized_block_number(finalized_block);
    finality.set_root_lookup_ceiling(Some(root_lookup_ceiling));
    finality.set_finalized_state_root_at(1, canonical_1.state_root);
    finality.set_finalized_state_root_at(2, canonical_2.state_root);
    let reader = tier.repository.try_lease_base_persisted_snapshot(&orphan_1).unwrap();
    let orphan_reservation = Arc::downgrade(reader.reservation());
    let prune_parent = finalized_block >= 1 && root_lookup_ceiling >= 1;
    let prune_child = finalized_block >= 2 && root_lookup_ceiling >= 2;
    let expected_count = 6 - i64::from(prune_parent) - i64::from(prune_child);

    tier.repository.remove_finalized_persisted_forks(&BLOCK_0).unwrap();

    assert_eq!(tier.repository.persisted_snapshot_count(), expected_count);
    assert_eq!(tier.catalog_kv.rows().len() as i64, expected_count);
    assert_eq!(tier.repository.has_base_persisted_snapshot(&orphan_1), !prune_parent);
    assert_eq!(tier.repository.has_base_persisted_snapshot(&orphan_2), !prune_child);
    assert!(tier.repository.has_base_persisted_snapshot(&sibling));
    assert_eq!(
        tier.repository.assemble_snapshots(&tip, &BLOCK_0, 3).persisted.len(),
        3,
        "the canonical ancestry above the persisted state stays"
    );
    assert_eq!(
        reader.try_get_account(&hex20(ADDRESS_A)).unwrap(),
        Some(Some(Account::new(1, U256::from_u64(100))))
    );
    drop(reader);
    assert_eq!(
        orphan_reservation.upgrade().is_none(),
        prune_parent,
        "released with its last reader"
    );

    if finalized_block == 2 && root_lookup_ceiling < 2 {
        finality.set_root_lookup_ceiling(None);
        tier.repository.remove_finalized_persisted_forks(&BLOCK_0).unwrap();
        assert_eq!(
            tier.repository.persisted_snapshot_count(),
            4,
            "a missing root is asked for again at the same finalized block"
        );
    }
}

/// `AddToPersistence_CachesFinalizedRootsUntilHeightsExpire`: a canonical root is looked up once until its height is
/// pruned below the persisted block or the tier.
#[rstest]
#[case(true, false, true)]
#[case(false, false, true)]
#[case(false, true, true)]
#[case(true, false, false)]
fn canonical_roots_are_cached_until_their_heights_are_pruned(
    #[case] connected_to_persisted_state: bool,
    #[case] has_fork: bool,
    #[case] prune_snapshots: bool,
) {
    let finality = Arc::new(SettableFinality::default());
    let tier = TemporaryTier::with_finality(finality.clone());
    let (parent, tip, orphan) = (state_id(1, 0), state_id(2, 0), state_id(1, 2));
    let from = if connected_to_persisted_state {
        BLOCK_0
    } else {
        state_id(0, 2)
    };
    persist_base(&tier, from, parent);
    persist_base(&tier, parent, tip);
    if has_fork {
        persist_base(&tier, from, orphan);
    }
    tier.repository.set_last_committed_state_id(tip);
    finality.set_finalized_block_number(2);
    finality.set_finalized_state_root_at(1, parent.state_root);
    finality.set_finalized_state_root_at(2, tip.state_root);

    for _ in 0..2 {
        tier.repository.remove_finalized_persisted_forks(&BLOCK_0).unwrap();
    }
    assert_eq!((finality.lookup_count(1), finality.lookup_count(2)), (1, 1));
    assert_eq!(tier.repository.has_base_persisted_snapshot(&orphan), has_fork);

    if prune_snapshots {
        tier.repository.remove_persisted_states_until(2).unwrap();
        persist_base(&tier, BLOCK_0, parent);
    } else {
        tier.repository.remove_finalized_persisted_forks(&tip).unwrap();
    }
    tier.repository.remove_finalized_persisted_forks(&BLOCK_0).unwrap();
    assert_eq!(
        (finality.lookup_count(1), finality.lookup_count(2)),
        (2, 1),
        "the pruned height is asked for again"
    );
}

/// `AddToPersistence_DefersFinalizedForkPruningWhenLocalChainDisagrees`: while the head does not reach the canonical
/// root at the finalized block, or the finalized block is above the head, nothing is removed; once finality agrees,
/// the forks go.
#[rstest]
#[case(2, false)]
#[case(2, true)]
#[case(100, false)]
fn forks_stay_while_the_local_chain_disagrees_with_finality(
    #[case] finalized_block: u64,
    #[case] anchor_matches: bool,
) {
    let finality = Arc::new(SettableFinality::default());
    let tier = TemporaryTier::with_finality(finality.clone());
    let (parent, tip) = (state_id(1, 1), state_id(2, 1));
    persist_base(&tier, BLOCK_0, parent);
    persist_base(&tier, parent, tip);
    tier.repository.set_last_committed_state_id(tip);
    finality.set_finalized_block_number(finalized_block);
    finality.set_finalized_state_root_at(1, [0xA1; 32]);
    finality.set_finalized_state_root_at(2, if anchor_matches { tip.state_root } else { [0xB2; 32] });

    tier.repository.remove_finalized_persisted_forks(&BLOCK_0).unwrap();
    assert_eq!(
        tier.repository.assemble_snapshots(&tip, &BLOCK_0, 2).persisted.len(),
        2,
        "the branch being built keeps its ancestry"
    );

    finality.set_finalized_block_number(2);
    finality.set_finalized_state_root_at(1, parent.state_root);
    finality.set_finalized_state_root_at(2, tip.state_root);
    let orphan = state_id(1, 2);
    persist_base(&tier, BLOCK_0, orphan);
    tier.repository.remove_finalized_persisted_forks(&BLOCK_0).unwrap();
    assert!(!tier.repository.has_base_persisted_snapshot(&orphan));
}

/// `AddToPersistence_FinalizedPruningPreservesCurrentPersistedBase`: the persisted state's base stays, whatever the
/// canonical root at its height, while its sibling goes.
#[rstest]
fn the_persisted_states_base_stays_while_its_sibling_goes(#[values(false, true)] root_matches: bool) {
    let finality = Arc::new(SettableFinality::default());
    let tier = TemporaryTier::with_finality(finality.clone());
    let (persisted, tip, sibling) = (state_id(1, 1), state_id(2, 1), state_id(1, 2));
    persist_base(&tier, BLOCK_0, persisted);
    persist_base(&tier, persisted, tip);
    persist_base(&tier, BLOCK_0, sibling);
    tier.repository.set_last_committed_state_id(tip);
    finality.set_finalized_block_number(2);
    finality.set_finalized_state_root_at(1, if root_matches { persisted.state_root } else { [0xA1; 32] });
    finality.set_finalized_state_root_at(2, tip.state_root);

    tier.repository.remove_finalized_persisted_forks(&persisted).unwrap();

    assert!(tier.repository.has_base_persisted_snapshot(&persisted));
    assert!(!tier.repository.has_base_persisted_snapshot(&sibling));
}

/// `RemoveFinalizedPersistedForks_PrunesAcrossPruneBatches`: forks in the first and in a later batch of 1,000 blocks go.
#[test]
fn finalized_forks_are_removed_across_batches() {
    const TIP_BLOCK: u64 = 1001;
    let finality = Arc::new(SettableFinality::default());
    let tier = TemporaryTier::with_finality(finality.clone());
    let mut parent = BLOCK_0;
    for block in 1..=TIP_BLOCK {
        let canonical = state_id(block, 1);
        persist_base(&tier, parent, canonical);
        finality.set_finalized_state_root_at(block, canonical.state_root);
        parent = canonical;
    }
    let (first_batch_orphan, second_batch_orphan) = (state_id(1, 2), state_id(1000, 2));
    persist_base(&tier, BLOCK_0, first_batch_orphan);
    persist_base(&tier, state_id(999, 1), second_batch_orphan);
    tier.repository.set_last_committed_state_id(parent);
    finality.set_finalized_block_number(TIP_BLOCK);

    tier.repository.remove_finalized_persisted_forks(&BLOCK_0).unwrap();

    assert!(!tier.repository.has_base_persisted_snapshot(&first_batch_orphan));
    assert!(!tier.repository.has_base_persisted_snapshot(&second_batch_orphan));
    assert_eq!(tier.repository.persisted_snapshot_count(), TIP_BLOCK as i64);
    assert_eq!(
        tier.repository
            .assemble_snapshots(&parent, &BLOCK_0, TIP_BLOCK as usize)
            .persisted
            .len() as u64,
        TIP_BLOCK
    );
}

/// `AddToPersistence_ReusesVerifiedAncestryUntilReorg`: a committed ancestor outranks a lagging canonical root while
/// the head extends the head it was verified against; after a reorg the first pass only drops the stale canonical root
/// of the finalized block, and the next removes the old branch.
#[rstest]
fn verified_ancestry_is_reused_until_a_reorg(
    #[values(false, true)] reorg: bool,
    #[values(false, true)] orphan_root_above_parent: bool,
) {
    let finality = Arc::new(SettableFinality::default());
    let tier = TemporaryTier::with_finality(finality.clone());
    // States at one height are visited by root, so one order meets the orphan after the parent dropped the root.
    let (parent_root, orphan_root) = if orphan_root_above_parent { (1, 3) } else { (3, 1) };
    let (parent, orphan, tip) = (state_id(1, parent_root), state_id(1, orphan_root), state_id(2, 1));
    persist_base(&tier, BLOCK_0, parent);
    persist_base(&tier, parent, tip);
    persist_base(&tier, BLOCK_0, orphan);
    tier.repository.set_last_committed_state_id(tip);
    finality.set_finalized_block_number(2);
    finality.set_finalized_state_root_at(1, [0xA1; 32]);
    finality.set_finalized_state_root_at(2, tip.state_root);

    tier.repository.remove_finalized_persisted_forks(&BLOCK_0).unwrap();
    assert!(
        tier.repository.has_base_persisted_snapshot(&parent),
        "a committed ancestor outranks a lagging canonical root"
    );
    assert!(!tier.repository.has_base_persisted_snapshot(&orphan));

    let next = if reorg {
        let (alternative_parent, next) = (state_id(1, 2), state_id(2, 2));
        persist_base(&tier, BLOCK_0, alternative_parent);
        persist_base(&tier, alternative_parent, next);
        finality.set_finalized_state_root_at(1, alternative_parent.state_root);
        finality.set_finalized_state_root_at(2, next.state_root);
        next
    } else {
        let next = state_id(3, 1);
        persist_base(&tier, tip, next);
        next
    };
    tier.repository.set_last_committed_state_id(next);
    for _ in 0..2 {
        tier.repository.remove_finalized_persisted_forks(&BLOCK_0).unwrap();
    }

    assert_eq!(tier.repository.has_base_persisted_snapshot(&parent), !reorg);
    assert_eq!(tier.repository.has_base_persisted_snapshot(&tip), !reorg);
}
