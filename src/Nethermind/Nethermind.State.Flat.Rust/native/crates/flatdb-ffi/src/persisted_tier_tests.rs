//! The persisted-snapshot tier through the C ABI: a manager created with a tier converts its in-memory bases into it,
//! serves reads from it, reports its gauges and counters, opens it again from the catalog rows C# hands in, reports
//! where a fatal error came from, and takes the faults of the tier.
//!
//! The tests register snapshots and their states as the compaction job does first, without compacting or queueing a
//! persistence job, so nothing runs in the background and every conversion happens in the test's own
//! `fdb_add_to_persistence` calls.

use std::collections::BTreeMap;
use std::ffi::c_void;
use std::ptr;
use std::slice;
use std::sync::Mutex;
use std::sync::atomic::Ordering;
use std::time::{Duration, Instant};

use flatdb_core::bundle::ReadSource;
use flatdb_core::kv::rocks_api::RocksApi;
use flatdb_core::types::{StateId, U256};
use rstest::rstest;

use crate::abi::{
    FATAL_SOURCE_CATALOG, FATAL_SOURCE_VALIDATION, FdbCatalogDesc, FdbCatalogKind, FdbMgrCfg, FdbPersistedEvent,
    FdbStatus, FdbTierCfg, FfiAccount, FfiRobInfo, FfiStateId, FfiTierGaugeRow, ManagedCatalogVTable,
};
use crate::faults::{CORRUPTING_ACCOUNT, Fault};
use crate::fdb_store_destroy;
use crate::manager::FdbManager;
use crate::manager_exports::tests::{
    A, CFG, TestHost, block, counters, create, fdb_host, has_state, inject, last_error, mem_store, parent, records,
    snapshot_of,
};
use crate::manager_exports::{
    fdb_add_to_persistence, fdb_destroy, fdb_persisted_event_release, fdb_persisted_tier_gauges, fdb_shutdown,
    fdb_take_fatal_error, fdb_take_persisted_event,
};
use crate::scope_exports::{
    FdbRob, fdb_add_snapshot, fdb_rob_gather, fdb_rob_get_account, fdb_rob_release, fdb_snapshot_release,
};
use crate::store::FdbStore;

const OK: i32 = FdbStatus::Ok as i32;
const PERSISTED: u8 = ReadSource::Persisted as u8;
/// `MaxInMemoryBaseSnapshotCount` of the tests: of [`BLOCKS`] in-memory bases, all but this many are converted.
const MAX_IN_MEMORY_BASES: u64 = 4;
const BLOCKS: u64 = 12;
/// The catalog tier byte of a base.
const BASE_TIER: u8 = 2;

/// The rows of a managed catalog, as the C# `MemDb` holds them.
#[derive(Default)]
struct CatalogRows(Mutex<BTreeMap<Vec<u8>, Vec<u8>>>);

impl CatalogRows {
    fn of<'a>(ctx: *mut c_void) -> &'a Self {
        // SAFETY: the tests pass the address of rows that outlive the manager.
        unsafe { &*ctx.cast::<CatalogRows>() }
    }

    /// The rows as C# packs them for the tier: each a little-endian `u32` key length, the key, a little-endian `u32`
    /// value length and the value.
    fn packed(&self) -> Vec<u8> {
        let rows = self.0.lock().unwrap();
        let mut packed = Vec::new();
        for (key, value) in rows.iter() {
            for bytes in [key, value] {
                packed.extend_from_slice(&(bytes.len() as u32).to_le_bytes());
                packed.extend_from_slice(bytes);
            }
        }
        packed
    }

    /// The `(arena id, offset)` of every base row, in key order, which is block order.
    fn base_locations(&self) -> Vec<(i32, i64)> {
        self.0
            .lock()
            .unwrap()
            .values()
            .filter(|value| value[100] == BASE_TIER)
            .map(|value| {
                (
                    i32::from_le_bytes(value[80..84].try_into().unwrap()),
                    i64::from_le_bytes(value[84..92].try_into().unwrap()),
                )
            })
            .collect()
    }
}

/// # Safety
/// `ctx` must be live [`CatalogRows`] and the slices valid for their lengths.
unsafe extern "C" fn catalog_put(
    ctx: *mut c_void,
    key: *const u8,
    key_len: usize,
    value: *const u8,
    value_len: usize,
) -> i32 {
    // SAFETY: as the caller vouches.
    let (key, value) = unsafe {
        (
            slice::from_raw_parts(key, key_len),
            slice::from_raw_parts(value, value_len),
        )
    };
    CatalogRows::of(ctx)
        .0
        .lock()
        .unwrap()
        .insert(key.to_vec(), value.to_vec());
    0
}

/// # Safety
/// As [`catalog_put`], and `exists` valid for writes.
unsafe extern "C" fn catalog_key_exists(ctx: *mut c_void, key: *const u8, key_len: usize, exists: *mut u8) -> i32 {
    // SAFETY: as the caller vouches.
    let key = unsafe { slice::from_raw_parts(key, key_len) };
    let found = CatalogRows::of(ctx).0.lock().unwrap().contains_key(key);
    // SAFETY: as the caller vouches.
    unsafe { *exists = found.into() };
    0
}

/// # Safety
/// As [`catalog_put`].
unsafe extern "C" fn catalog_remove(ctx: *mut c_void, key: *const u8, key_len: usize) -> i32 {
    // SAFETY: as the caller vouches.
    let key = unsafe { slice::from_raw_parts(key, key_len) };
    CatalogRows::of(ctx).0.lock().unwrap().remove(key);
    0
}

/// A store with a manager over a persisted-snapshot tier in a temporary directory, its catalog in memory.
struct TierNode {
    store: *mut FdbStore,
    mgr: *mut FdbManager,
    host: Box<TestHost>,
    catalog: Box<CatalogRows>,
    vtable: Box<ManagedCatalogVTable>,
    directory: tempfile::TempDir,
}

impl TierNode {
    /// The node, with a manager created over the tier.
    fn new() -> Self {
        let mut node = Self::without_manager();
        assert_eq!(node.create_manager(), OK, "{}", last_error());
        node
    }

    fn without_manager() -> Self {
        let catalog = Box::new(CatalogRows::default());
        let vtable = Box::new(ManagedCatalogVTable {
            ctx: ptr::from_ref(&*catalog).cast_mut().cast(),
            put: Some(catalog_put),
            key_exists: Some(catalog_key_exists),
            remove: Some(catalog_remove),
        });
        Self {
            store: mem_store(),
            mgr: ptr::null_mut(),
            host: Box::default(),
            catalog,
            vtable,
            directory: tempfile::tempdir().unwrap(),
        }
    }

    fn tier_cfg(&self, catalog: FdbCatalogDesc, rows: &[u8], arena: &str, blob: &str) -> FdbTierCfg {
        FdbTierCfg {
            arena_dir: arena.as_ptr(),
            arena_dir_len: arena.len(),
            blob_dir: blob.as_ptr(),
            blob_dir_len: blob.len(),
            catalog,
            catalog_rows: rows.as_ptr(),
            catalog_rows_len: rows.len(),
            long_finality_max_reorg_depth: 64,
            max_in_memory_base_snapshot_count: MAX_IN_MEMORY_BASES,
            arena_file_size_bytes: 64 * 1024,
            dedicated_arena_threshold_bytes: 16 * 1024,
            arena_page_cache_bytes: 0,
            bloom_bits_per_key: 14.0,
            processor_count: 2,
            punch_hole_on_reclaim: 0,
            validate_persisted_snapshot: 1,
            pad: [0; 2],
        }
    }

    fn managed_catalog(&self) -> FdbCatalogDesc {
        FdbCatalogDesc {
            kind: FdbCatalogKind::Managed as u8,
            verify_checksums: 1,
            wal_sync: 0,
            pad: [0; 5],
            rocks_api: ptr::null(),
            rocks_db: ptr::null_mut(),
            default_column_family: ptr::null_mut(),
            managed: &*self.vtable,
        }
    }

    /// Creates the manager over the tier and the rows the catalog holds, returning the status.
    fn create_manager(&mut self) -> i32 {
        self.create_manager_with(self.managed_catalog(), &self.catalog.packed())
    }

    fn create_manager_with(&mut self, catalog: FdbCatalogDesc, rows: &[u8]) -> i32 {
        let base = self.directory.path().join("persistedSnapshot");
        let (arena, blob) = (
            base.join("arena").to_str().unwrap().to_owned(),
            base.join("blob").to_str().unwrap().to_owned(),
        );
        let tier = self.tier_cfg(catalog, rows, &arena, &blob);
        let (status, mgr) = create(self.store, FdbMgrCfg { tier: &tier, ..CFG }, &fdb_host(&self.host));
        self.mgr = mgr;
        status
    }

    /// Registers the snapshots of blocks 0 to [`BLOCKS`] - 1.
    fn register_blocks(&self) {
        (0..BLOCKS).for_each(|number| self.register_block(number));
    }

    /// Registers the snapshot of block `number` and its state, without compacting it or queueing a persistence job.
    fn register_block(&self, number: u64) {
        let snapshot = snapshot_of(parent(number), block(number), &records(number));
        // SAFETY: the manager is live; the snapshot is released once.
        unsafe {
            assert_eq!(fdb_add_snapshot(self.mgr, snapshot), OK, "{}", last_error());
            assert_eq!(fdb_snapshot_release(snapshot), OK);
            (*self.mgr).manager.repository().add_state_id(block(number));
        }
    }

    /// `AddToPersistence` at the head, with the message of a failure.
    fn persist(&self) -> (i32, String) {
        // SAFETY: the manager is live and the state valid.
        let status = unsafe { fdb_add_to_persistence(self.mgr, &block(BLOCKS - 1).into()) };
        (status, if status == OK { String::new() } else { last_error() })
    }

    /// `AddToPersistence` until it converts nothing more, each call converting at most four bases.
    fn persist_until_converted(&self) {
        for _ in 0..BLOCKS {
            assert_eq!(self.persist(), (OK, String::new()));
        }
    }

    /// The status and source of `fdb_take_fatal_error`; the source is `u8::MAX` when none was written.
    fn take_fatal_error(&self) -> (i32, u8) {
        let mut source = u8::MAX;
        // SAFETY: the manager is live and the out-pointer valid.
        let status = unsafe { fdb_take_fatal_error(self.mgr, &mut source) };
        (status, source)
    }

    /// Every queued event: its kind, its range and its node count.
    fn take_events(&self) -> Vec<(u8, StateId, StateId, usize)> {
        let mut taken = Vec::new();
        loop {
            let mut event = FdbPersistedEvent {
                from: FfiStateId::default(),
                to: FfiStateId::default(),
                nodes: ptr::null(),
                node_count: 0,
                owner: ptr::null_mut(),
                kind: u8::MAX,
                pad: [0; 7],
            };
            // SAFETY: the manager is live and the record valid; the owner is released once.
            unsafe {
                assert_eq!(fdb_take_persisted_event(self.mgr, &mut event), OK);
                if event.owner.is_null() {
                    return taken;
                }
                taken.push((event.kind, event.from.into(), event.to.into(), event.node_count));
                assert_eq!(fdb_persisted_event_release(event.owner), OK);
            }
        }
    }

    /// The gauge rows, read with a first call that only learns how many there are.
    fn gauges(&self) -> Vec<FfiTierGaugeRow> {
        let mut needed = usize::MAX;
        // SAFETY: the manager is live; no row is written with a zero capacity.
        let sized = unsafe { fdb_persisted_tier_gauges(self.mgr, ptr::null_mut(), 0, &mut needed) };
        let mut rows = vec![FfiTierGaugeRow::default(); needed];
        let mut count = usize::MAX;
        // SAFETY: the buffer holds `needed` rows.
        let status = unsafe { fdb_persisted_tier_gauges(self.mgr, rows.as_mut_ptr(), rows.len(), &mut count) };
        assert_eq!(
            (sized, status, count),
            (
                if needed == 0 {
                    OK
                } else {
                    FdbStatus::BufferTooSmall as i32
                },
                OK,
                needed
            )
        );
        rows
    }

    fn gather(&self, base: StateId) -> (*mut FdbRob, FfiRobInfo) {
        let (mut rob, mut info) = (ptr::null_mut(), FfiRobInfo::default());
        // SAFETY: the manager is live and every pointer valid.
        let status = unsafe { fdb_rob_gather(self.mgr, &base.into(), 0, &mut rob, &mut info) };
        assert_eq!(status, OK, "{}", last_error());
        (rob, info)
    }

    /// The status, balance and source of a read of account A.
    fn read_balance(rob: *mut FdbRob) -> (i32, Option<U256>, u8) {
        let (mut account, mut found, mut source) = (FfiAccount::default(), 0u8, 0u8);
        // SAFETY: the bundle is live and every pointer valid.
        let status = unsafe { fdb_rob_get_account(rob, A.as_ptr(), &mut account, &mut found, &mut source) };
        (status, (found != 0).then_some(U256(account.balance)), source)
    }

    fn is_poisoned(&self) -> bool {
        // SAFETY: the store is live.
        unsafe { &*self.store }.poison.is_set()
    }

    /// Shuts the manager down, draining the persisted compactor, and destroys it.
    fn destroy_manager(&mut self) {
        // SAFETY: the manager is live and not used afterwards.
        unsafe {
            assert_eq!(fdb_shutdown(self.mgr), OK);
            assert_eq!(fdb_destroy(self.mgr), OK);
        }
        self.mgr = ptr::null_mut();
    }
}

impl Drop for TierNode {
    fn drop(&mut self) {
        // SAFETY: the handles are released once, the manager before its store.
        unsafe {
            fdb_destroy(self.mgr);
            fdb_store_destroy(self.store);
        }
    }
}

/// With [`BLOCKS`] bases and finality stalled, all but [`MAX_IN_MEMORY_BASES`] are converted, oldest first, each
/// queueing a Converted event with its nodes. The tier serves the reads of a state only it holds, counts its
/// snapshots in the `{tier, size}` gauges and its files and blooms in the counters C# reads, and a manager created
/// again over the catalog rows serves the same reads.
#[test]
fn a_tier_converts_serves_reads_and_reopens_from_the_catalog_rows() {
    let mut node = TierNode::new();
    node.register_blocks();
    node.persist_until_converted();

    let converted = BLOCKS - MAX_IN_MEMORY_BASES;
    let events = node.take_events();
    assert_eq!(
        events
            .iter()
            .map(|&(kind, from, to, _)| (kind, from, to))
            .collect::<Vec<_>>(),
        (0..converted)
            .map(|number| (1, parent(number), block(number)))
            .collect::<Vec<_>>()
    );
    assert!(
        events.iter().all(|event| event.3 == 2),
        "the state and storage root nodes, never a placeholder: {events:?}"
    );
    assert_eq!(node.catalog.base_locations().len(), converted as usize);
    assert!(
        has_state(node.mgr, block(1)) && has_state(node.mgr, block(BLOCKS - 1)),
        "a converted state is still held"
    );

    let (rob, info) = node.gather(block(1));
    assert_eq!(
        (info.persisted_count, info.snapshot_count),
        (2, 2),
        "both bases are in the tier"
    );
    assert_eq!(TierNode::read_balance(rob), (OK, Some(U256::from_u64(1)), PERSISTED));
    // SAFETY: the bundle is released once.
    assert_eq!(unsafe { fdb_rob_release(rob) }, OK);
    let (rob, info) = node.gather(block(BLOCKS - 1));
    assert!(
        info.persisted_count >= 1,
        "merges in the background may have widened the tier's snapshots"
    );
    assert_eq!(
        info.snapshot_count,
        info.persisted_count + MAX_IN_MEMORY_BASES as u32,
        "the self-destruct indexes number the tier first"
    );
    // SAFETY: the bundle is released once.
    assert_eq!(unsafe { fdb_rob_release(rob) }, OK);

    let tier_counters = &counters(node.mgr).tier;
    assert!(tier_counters.arena_reservation_count.load(Ordering::SeqCst) >= converted as i64);
    assert!(tier_counters.bloom_count.load(Ordering::SeqCst) > 0);
    assert!(tier_counters.arena_file_count.load(Ordering::SeqCst) > 0);
    assert!(tier_counters.blob_file_count.load(Ordering::SeqCst) > 0);
    assert_eq!(
        tier_counters.catalog_puts.load(Ordering::SeqCst),
        0,
        "a managed catalog counts its own writes"
    );

    node.destroy_manager();
    assert_eq!(node.create_manager(), OK, "{}", last_error());
    let base_row = node
        .gauges()
        .into_iter()
        .find(|row| (row.tier, row.size) == (BASE_TIER, 1))
        .expect("a base row");
    assert_eq!(base_row.count, converted as i64);
    assert!(base_row.memory > 0 && base_row.active >= base_row.count);
    assert!(has_state(node.mgr, block(1)));
    let (rob, _) = node.gather(block(1));
    assert_eq!(TierNode::read_balance(rob), (OK, Some(U256::from_u64(1)), PERSISTED));
    // SAFETY: the bundle is released once.
    assert_eq!(unsafe { fdb_rob_release(rob) }, OK);
}

/// A manager without a tier reports no gauge row.
#[test]
fn a_manager_without_a_tier_reports_no_gauge() {
    let host = TestHost::default();
    let store = mem_store();
    let (status, mgr) = create(store, CFG, &fdb_host(&host));
    assert_eq!(status, OK);
    let mut count = usize::MAX;
    // SAFETY: the manager is live; no row is written with a zero capacity.
    let status = unsafe { fdb_persisted_tier_gauges(mgr, ptr::null_mut(), 0, &mut count) };
    assert_eq!((status, count), (OK, 0));
    // SAFETY: the handles are released once.
    unsafe {
        assert_eq!(fdb_destroy(mgr), OK);
        assert_eq!(fdb_store_destroy(store), OK);
    }
}

/// The first conversion takes the armed fault: a fatal catalog error and a failed validation fail it and every later
/// persist, and are reported once with their source; a catalog error that is not fatal, an I/O error and a crash
/// before the catalog row fail it alone, and the next persist converts. An I/O error cancels the writes, so the base
/// converted next reuses their bytes; a crash keeps them, so it goes elsewhere. A panic poisons the store.
#[rstest]
#[case::fatal_catalog(
    Fault::CatalogPutError,
    "IO error: injected",
    FdbStatus::RocksDb,
    Some(FATAL_SOURCE_CATALOG)
)]
#[case::busy_catalog(Fault::CatalogPutError, "Busy: injected", FdbStatus::RocksDb, None)]
#[case::validation(
    Fault::CorruptConvertedContent,
    "",
    FdbStatus::ValidationFailed,
    Some(FATAL_SOURCE_VALIDATION)
)]
#[case::write_error(Fault::ArenaWriteError, "No space left on device", FdbStatus::Io, None)]
#[case::crash_before_catalog(Fault::SkipCatalogAdd, "", FdbStatus::InvalidOperation, None)]
#[case::panic(Fault::PanicInConvert, "injected panic", FdbStatus::Panic, None)]
fn conversion_faults_fail_poison_or_are_reported_with_their_source(
    #[case] fault: Fault,
    #[case] message: &str,
    #[case] expected: FdbStatus,
    #[case] fatal_source: Option<u8>,
) {
    let node = TierNode::new();
    node.register_blocks();
    inject(node.store, fault, message);

    let (status, error) = node.persist();
    assert_eq!(status, expected as i32, "{error}");
    match fault {
        Fault::CatalogPutError => assert_eq!(error, message),
        Fault::CorruptConvertedContent => {
            let dump = format!("broken.{}.0.json", u64::MAX);
            let dumped = std::fs::read_to_string(&dump);
            std::fs::remove_file(&dump).unwrap();
            assert!(
                dumped.unwrap().contains(&"de".repeat(CORRUPTING_ACCOUNT.len())),
                "the validated source is dumped"
            );
            assert!(
                error.contains(&format!(
                    "Account 0x{} not found in persisted snapshot",
                    "de".repeat(CORRUPTING_ACCOUNT.len())
                )),
                "{error}"
            );
        }
        _ => {}
    }

    if expected == FdbStatus::Panic {
        assert!(node.is_poisoned());
        assert_eq!(node.persist().0, FdbStatus::Poisoned as i32);
        return;
    }
    match fatal_source {
        Some(source) => {
            assert_eq!(node.take_fatal_error(), (expected as i32, source));
            assert_eq!(node.take_fatal_error(), (OK, u8::MAX), "reported once");
            assert_eq!(node.persist(), (status, error), "nothing more is written");
            assert!(node.take_events().is_empty());
        }
        None => {
            assert_eq!(node.take_fatal_error(), (OK, u8::MAX));
            node.persist_until_converted();
            let first_base = node.catalog.base_locations()[0];
            match fault {
                Fault::ArenaWriteError => assert_eq!(first_base, (0, 0), "the cancelled range is reused"),
                Fault::SkipCatalogAdd => assert_ne!(first_base, (0, 0), "the bytes of the crashed conversion stay"),
                _ => {}
            }
            assert_eq!(
                node.take_events().len() as u64,
                BLOCKS - MAX_IN_MEMORY_BASES,
                "every base converts once the fault is taken"
            );
        }
    }
}

/// A panic in a read the tier serves, in a persisted compaction, or while the tier is opened poisons the store.
#[rstest]
fn tier_panics_poison_the_store(
    #[values(
        Fault::PanicInTierRead,
        Fault::PanicInPersistedCompaction,
        Fault::PanicInPersistedLoad
    )]
    fault: Fault,
) {
    let mut node = TierNode::new();
    node.register_blocks();
    node.persist_until_converted();

    match fault {
        Fault::PanicInTierRead => {
            let (rob, _) = node.gather(block(1));
            inject(node.store, fault, "injected panic");
            assert_eq!(TierNode::read_balance(rob).0, FdbStatus::Panic as i32);
            assert_eq!(TierNode::read_balance(rob).0, FdbStatus::Poisoned as i32);
            // SAFETY: the bundle is released once.
            assert_eq!(unsafe { fdb_rob_release(rob) }, OK);
        }
        Fault::PanicInPersistedCompaction => {
            // The conversions queued their merges before the fault was armed, so a new base queues the merge that
            // takes it.
            inject(node.store, fault, "injected panic");
            node.register_block(BLOCKS);
            assert_eq!(node.persist().0, OK);
            let deadline = Instant::now() + Duration::from_secs(30);
            while !node.is_poisoned() && Instant::now() < deadline {
                std::thread::sleep(Duration::from_millis(10));
            }
            assert!(node.is_poisoned(), "the merge panicked on the compactor thread");
        }
        _ => {
            node.destroy_manager();
            inject(node.store, fault, "injected panic");
            assert_eq!(node.create_manager(), FdbStatus::Panic as i32);
            assert!(node.mgr.is_null());
            assert_eq!(node.create_manager(), FdbStatus::Poisoned as i32);
        }
    }
    assert!(node.is_poisoned());
}

/// A tier that cannot be opened, or whose catalog database cannot be driven, fails the manager's creation with the
/// status of the exception C# throws.
#[rstest]
#[case::in_memory_tier_byte(Some(1), |catalog, _| catalog, FdbStatus::InvalidOperation, "non-persisted tier byte 1")]
#[case::unknown_tier_byte(Some(6), |catalog, _| catalog, FdbStatus::InvalidArgument, "Not a persisted tier.")]
#[case::unknown_catalog_kind(
    None,
    |catalog, _| FdbCatalogDesc { kind: 9, ..catalog },
    FdbStatus::InvalidArgument,
    "unknown catalog backend 9"
)]
#[case::rocks_without_api(None, |catalog, _| rocks(catalog), FdbStatus::InvalidArgument, "null RocksApi")]
#[case::rocks_without_database(
    None,
    |catalog, api| FdbCatalogDesc { rocks_api: api, default_column_family: ptr::dangling_mut(), ..rocks(catalog) },
    FdbStatus::InvalidArgument,
    "null RocksDB database or column family handle"
)]
#[case::rocks_without_column_family(
    None,
    |catalog, api| FdbCatalogDesc { rocks_api: api, rocks_db: ptr::dangling_mut(), ..rocks(catalog) },
    FdbStatus::InvalidArgument,
    "null RocksDB database or column family handle"
)]
fn tiers_that_cannot_be_opened_fail_creation(
    #[case] tier_byte: Option<u8>,
    #[case] break_catalog: fn(FdbCatalogDesc, *const RocksApi) -> FdbCatalogDesc,
    #[case] expected: FdbStatus,
    #[case] message: &str,
) {
    // SAFETY: no entry is ever called, as the null handles are rejected first.
    let complete_api = unsafe { RocksApi::from_resolver(|_| ptr::dangling()) };
    let mut node = TierNode::without_manager();
    let mut rows = Vec::new();
    if let Some(byte) = tier_byte {
        let mut value = [0u8; 101];
        value[100] = byte;
        for bytes in [&[0u8; 48][..], &value[..]] {
            rows.extend_from_slice(&(bytes.len() as u32).to_le_bytes());
            rows.extend_from_slice(bytes);
        }
    }
    let catalog = break_catalog(node.managed_catalog(), &complete_api);
    assert_eq!(node.create_manager_with(catalog, &rows), expected as i32);
    assert!(last_error().contains(message), "{}", last_error());
    assert!(node.mgr.is_null() && !node.is_poisoned());
}

/// `catalog` as a RocksDB catalog, its handles still null.
fn rocks(catalog: FdbCatalogDesc) -> FdbCatalogDesc {
    FdbCatalogDesc {
        kind: FdbCatalogKind::Rocks as u8,
        ..catalog
    }
}
