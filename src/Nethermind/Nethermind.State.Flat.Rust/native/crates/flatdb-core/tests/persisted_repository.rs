//! The persisted tier's snapshots, buckets and loader: ports of `PersistedSnapshotRepositoryTests` (the cases without a
//! repository walk over persisted edges), `PersistedSnapshotLoaderTests`, the validation of a converted snapshot, and
//! the crash cases of the loader.
//!
//! Not ported: `TryAcquire_WhenObservedSnapshotIsRetired_UsesCurrentEntry`, as a bucket hands out `Arc` clones, which
//! cannot be observed retired. Compactions are the test fixture's port of `CompactRange`.

mod common;

use std::path::Path;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};

use common::persisted::StorageTier;
use common::{hex20, hex32};
use flatdb_core::kv::{KvError, KvResult};
use flatdb_core::persisted::bloom_keys;
use flatdb_core::persisted::list::PersistedSnapshotList;
use flatdb_core::persisted::stack::{PersistedLayer, PersistedSnapshotStack};
use flatdb_core::persisted::storage::arena_manager::ArenaConfig;
use flatdb_core::persisted::storage::catalog::{CatalogKv, MemCatalogKv};
use flatdb_core::persisted::validate::validate_persisted_snapshot;
use flatdb_core::persisted::{PersistedTierConfig, TierError};
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
    for &boundary in &boundaries {
        assert!(tier.compact_sized(&schedule, &state(boundary)));
    }
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
/// the in-memory one, the newest state counts persisted tips, and a persisted persist candidate is not returned.
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
    assert!(
        tier.repository
            .find_snapshot_to_persist(&state(3), &state(0), 4)
            .is_none()
    );
    assert_eq!(
        tier.repository
            .find_snapshot_to_persist(&state(3), &state(2), 4)
            .map(|snapshot| snapshot.to()),
        Some(state(3))
    );

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
