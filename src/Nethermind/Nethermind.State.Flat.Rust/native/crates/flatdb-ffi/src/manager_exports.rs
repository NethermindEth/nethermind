//! The exports of the snapshot manager (`IFlatDbManager` and `IPersistenceManager`), the snapshot builder, the persisted
//! node events and the metrics.
//!
//! A gathered bundle is handed out as an [`FdbReader`], so every `fdb_reader_*` export reads through it.

use std::ffi::c_void;
use std::ptr;
use std::sync::Arc;

use flatdb_core::manager::AddSnapshotOutcome;
use flatdb_core::persistence::ReaderFlags;
use flatdb_core::snapshot::Snapshot;
use flatdb_core::snapshot::content::{SnapshotContentBuilder, SnapshotContentCounts};
use flatdb_core::stats::{FlatCounters, Observation};
use flatdb_core::types::StateId;

use crate::abi::{FdbHost, FdbMgrCfg, FdbPersistedEvent, FdbStatus, FfiError, FfiPersistedNode, FfiStateId};
use crate::calls::Export;
use crate::guard::ffi_guard;
use crate::manager::{BundleReader, FdbBuilder, FdbManager, refused};
use crate::snapshot_stream;
use crate::store::{FdbReader, FdbStore};
use crate::{bytes, out, store_ref};

/// Starts a snapshot manager over the persistence stack of `store`, calling `host` for finality and the deferred
/// block-data flush. The manager holds a handle on the store, which therefore outlives it.
///
/// # Safety
/// `store` must be a live store and `cfg`, `host` and `mgr` valid. The host callbacks run on the manager's persistence
/// thread or the thread flushing it until [`fdb_destroy`] returns; they must not unwind or call into the library.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_mgr_create(
    store: *const FdbStore,
    cfg: *const FdbMgrCfg,
    host: *const FdbHost,
    mgr: *mut *mut FdbManager,
) -> i32 {
    ffi_guard(Export::MgrCreate, || {
        // SAFETY: the caller passes a live store and valid pointers.
        let (store, cfg, host, mgr) = unsafe { (store_ref(store)?, cfg.as_ref(), host.as_ref(), out(mgr)?) };
        let (Some(cfg), Some(host)) = (cfg, host) else {
            return Err(FfiError::invalid_argument("null argument"));
        };
        let created = store.poison.guard(|| FdbManager::create(store, cfg, host))?;
        *mgr = Box::into_raw(Box::new(created));
        Ok(())
    })
}

/// Process exit started (`IProcessExitSource.Token`): a block waiting for room in the compaction queue gives up.
///
/// # Safety
/// `mgr` must be a live manager.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_signal_exit(mgr: *const FdbManager) -> i32 {
    ffi_guard(Export::SignalExit, || {
        // SAFETY: the caller passes a live manager.
        unsafe { mgr_ref(mgr)? }.manager.signal_exit();
        Ok(())
    })
}

/// `DisposeAsync` of `FlatDbManager`: drains the compaction and persistence queues, then stops the workers. Idempotent;
/// later adds fail with [`FdbStatus::ShutDown`].
///
/// # Safety
/// `mgr` must be a live manager.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_shutdown(mgr: *const FdbManager) -> i32 {
    ffi_guard(Export::Shutdown, || {
        // SAFETY: the caller passes a live manager.
        let mgr = unsafe { mgr_ref(mgr)? };
        mgr.poison.log.scope(|| mgr.manager.shutdown());
        Ok(())
    })
}

/// Shuts the manager down if it is not yet, and frees it. Bundles gathered from it stay readable.
///
/// # Safety
/// `mgr` must be null or a manager from [`fdb_mgr_create`] that is not used afterwards.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_destroy(mgr: *mut FdbManager) -> i32 {
    ffi_guard(Export::Destroy, || {
        if !mgr.is_null() {
            // SAFETY: the caller hands back ownership of a manager from `fdb_mgr_create`.
            let mgr = unsafe { Box::from_raw(mgr) };
            // Dropping shuts the workers down, which can log.
            let poison = Arc::clone(&mgr.poison);
            poison.log.scope(|| drop(mgr));
        }
        Ok(())
    })
}

/// The manager's counters, valid until [`fdb_destroy`]; C# reads each as an atomic `long`. Null for a null manager.
///
/// # Safety
/// `mgr` must be null or a live manager.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_stats(mgr: *const FdbManager) -> *const FlatCounters {
    // SAFETY: the caller passes null or a live manager.
    match unsafe { mgr.as_ref() } {
        Some(mgr) => &mgr.manager.stats().counters,
        None => ptr::null(),
    }
}

/// Takes up to `capacity` queued histogram observations, oldest first, and stores how many in `count`.
///
/// # Safety
/// `mgr` must be a live manager, `buffer` valid for writes of `capacity` observations and `count` for writes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_drain_observations(
    mgr: *const FdbManager,
    buffer: *mut Observation,
    capacity: usize,
    count: *mut usize,
) -> i32 {
    ffi_guard(Export::DrainObservations, || {
        // SAFETY: the caller passes a live manager and valid out-pointers.
        let (mgr, count) = unsafe { (mgr_ref(mgr)?, out(count)?) };
        if buffer.is_null() && capacity != 0 {
            return Err(FfiError::invalid_argument("null pointer with a non-zero capacity"));
        }
        let drained = mgr.manager.stats().drain_observations(capacity);
        if !drained.is_empty() {
            // SAFETY: the caller guarantees room for `capacity` observations, and at most that many were drained.
            unsafe { ptr::copy_nonoverlapping(drained.as_ptr(), buffer, drained.len()) };
        }
        *count = drained.len();
        Ok(())
    })
}

/// Takes the oldest queued persist into `event`; its `owner` is null when none is queued, otherwise it keeps the node
/// array alive until [`fdb_persisted_event_release`].
///
/// # Safety
/// `mgr` must be a live manager and `event` valid for writes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_take_persisted_event(mgr: *const FdbManager, event: *mut FdbPersistedEvent) -> i32 {
    ffi_guard(Export::TakePersistedEvent, || {
        // SAFETY: the caller passes a live manager and a valid out-pointer.
        let (mgr, event) = unsafe { (mgr_ref(mgr)?, out(event)?) };
        *event = match mgr.take_persisted_event() {
            None => FdbPersistedEvent {
                from: FfiStateId::default(),
                to: FfiStateId::default(),
                nodes: ptr::null(),
                node_count: 0,
                owner: ptr::null_mut(),
            },
            Some(taken) => {
                let nodes: Box<Vec<FfiPersistedNode>> = Box::new(taken.nodes);
                FdbPersistedEvent {
                    from: taken.from,
                    to: taken.to,
                    nodes: nodes.as_ptr(),
                    node_count: nodes.len(),
                    owner: Box::into_raw(nodes).cast::<c_void>(),
                }
            }
        };
        Ok(())
    })
}

/// Frees the nodes of a persist taken with [`fdb_take_persisted_event`].
///
/// # Safety
/// `owner` must be null or the owner of a taken event that is not used afterwards.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_persisted_event_release(owner: *mut c_void) -> i32 {
    ffi_guard(Export::PersistedEventRelease, || {
        if !owner.is_null() {
            // SAFETY: the caller hands back the owner `fdb_take_persisted_event` created.
            drop(unsafe { Box::from_raw(owner.cast::<Vec<FfiPersistedNode>>()) });
        }
        Ok(())
    })
}

/// Starts building the snapshot of the writes from `from` to `to`, with room for the accounts, slots, state trie nodes
/// and storage trie nodes the caller is about to append.
///
/// # Safety
/// `from` and `to` must be valid and `bld` valid for writes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_bld_new(
    from: *const FfiStateId,
    to: *const FfiStateId,
    accounts: usize,
    storages: usize,
    state_nodes: usize,
    storage_nodes: usize,
    bld: *mut *mut FdbBuilder,
) -> i32 {
    ffi_guard(Export::BldNew, || {
        // SAFETY: the caller passes valid pointers.
        let (from, to, bld) = unsafe { (from.as_ref(), to.as_ref(), out(bld)?) };
        let (Some(from), Some(to)) = (from, to) else {
            return Err(FfiError::invalid_argument("null state"));
        };
        *bld = Box::into_raw(Box::new(FdbBuilder {
            from: (*from).into(),
            to: (*to).into(),
            content: SnapshotContentBuilder::with_capacity(SnapshotContentCounts {
                accounts,
                storages,
                self_destructed_storage_addresses: 0,
                state_nodes,
                storage_nodes,
            }),
        }));
        Ok(())
    })
}

/// Adds `len` bytes of snapshot records (see [`snapshot_stream`]) to the builder, stopping at the first malformed one.
///
/// # Safety
/// `bld` must be a live builder, used by one thread at a time, and `records` valid for `len` bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_bld_append(bld: *mut FdbBuilder, records: *const u8, len: usize) -> i32 {
    ffi_guard(Export::BldAppend, || {
        // SAFETY: the caller passes a live builder and a valid record stream.
        let (bld, records) = unsafe { (bld_mut(bld)?, bytes(records, len)?) };
        snapshot_stream::append(&mut bld.content, records)
    })
}

/// Moves every record of `from`, which is freed, into `into`, so the content of one block can be serialized on
/// several threads.
///
/// # Safety
/// `into` must be a live builder and `from` a builder from [`fdb_bld_new`] that is not used afterwards, neither used by
/// another thread.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_bld_absorb(into: *mut FdbBuilder, from: *mut FdbBuilder) -> i32 {
    ffi_guard(Export::BldAbsorb, || {
        if from.is_null() {
            return Err(FfiError::invalid_argument("null builder"));
        }
        // SAFETY: the caller hands back ownership of `from`.
        let from = unsafe { Box::from_raw(from) };
        // SAFETY: the caller passes a live builder.
        let into = unsafe { bld_mut(into)? };
        into.content.absorb(from.content);
        Ok(())
    })
}

/// Frees a builder that was not added.
///
/// # Safety
/// `bld` must be null or a builder from [`fdb_bld_new`] that is not used afterwards.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_bld_release(bld: *mut FdbBuilder) -> i32 {
    ffi_guard(Export::BldRelease, || {
        if !bld.is_null() {
            // SAFETY: the caller hands back ownership of a builder.
            drop(unsafe { Box::from_raw(bld) });
        }
        Ok(())
    })
}

/// `AddSnapshot` up to its compaction: adds the built snapshot, which the call consumes whatever it returns, for
/// [`fdb_queue_compaction`] to compact. [`FdbStatus::Rejected`] when its block is at or below the persisted block,
/// [`FdbStatus::Duplicate`] when its state is already added.
///
/// # Safety
/// `bld` must be a builder from [`fdb_bld_new`] that is not used afterwards and `mgr` a live manager.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_add_snapshot(mgr: *const FdbManager, bld: *mut FdbBuilder) -> i32 {
    ffi_guard(Export::AddSnapshot, || {
        if bld.is_null() {
            return Err(FfiError::invalid_argument("null builder"));
        }
        // SAFETY: the caller hands over ownership of the builder.
        let bld = unsafe { Box::from_raw(bld) };
        // SAFETY: the caller passes a live manager.
        let mgr = unsafe { mgr_ref(mgr)? };
        let FdbBuilder { from, to, content } = *bld;
        let outcome = mgr.poison.guard(|| {
            let snapshot = Arc::new(Snapshot::new(from, to, content.build()));
            Ok(mgr.manager.register_snapshot(snapshot)?)
        })?;
        match outcome {
            AddSnapshotOutcome::Added { .. } => Ok(()),
            AddSnapshotOutcome::Rejected => Err(refused(FdbStatus::Rejected, &to)),
            AddSnapshotOutcome::Duplicate => Err(refused(FdbStatus::Duplicate, &to)),
        }
    })
}

/// The rest of `AddSnapshot` for the snapshot ending at `to` that [`fdb_add_snapshot`] added: compacts it on this thread
/// with inline compaction, else queues its compaction, waiting for room while the queue is full. `stall_ns` is how long
/// the call waited, or -1 when it did not. [`FdbStatus::Cancelled`] when process exit ends the wait,
/// [`FdbStatus::ShutDown`] once the manager is shut down; the snapshot stays added either way.
///
/// # Safety
/// `mgr` must be a live manager, `to` valid and `stall_ns` valid for writes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_queue_compaction(
    mgr: *const FdbManager,
    to: *const FfiStateId,
    stall_ns: *mut i64,
) -> i32 {
    ffi_guard(Export::QueueCompaction, || {
        // SAFETY: the caller passes a live manager and valid pointers.
        let (mgr, to, stall_ns) = unsafe { (mgr_ref(mgr)?, state(to)?, out(stall_ns)?) };
        *stall_ns = -1;
        if let Some(stall) = mgr.poison.guard(|| Ok(mgr.manager.compact_registered(&to)?))? {
            *stall_ns = i64::try_from(stall.as_nanos()).unwrap_or(i64::MAX);
        }
        Ok(())
    })
}

/// `GatherReadOnlySnapshotBundle(base, readerFlags)`: a reader of the state `base`, through the in-memory snapshots
/// above the persisted state and then the persistence, read with the `fdb_reader_*` exports and released with
/// `fdb_reader_release`. `filtered` serves slot reads through the bundle's negative slot filter. `reader_state` is the
/// persisted state under the snapshots. `PreGenesis` reads an empty state; C# serves it without the manager.
///
/// # Safety
/// `mgr` must be a live manager, `base` valid and the out-pointers valid for writes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_gather(
    mgr: *const FdbManager,
    base: *const FfiStateId,
    reader_flags: u32,
    filtered: u8,
    rob: *mut *mut FdbReader,
    reader_state: *mut FfiStateId,
) -> i32 {
    ffi_guard(Export::Gather, || {
        // SAFETY: the caller passes a live manager and valid pointers.
        let (mgr, base, rob, reader_state) = unsafe { (mgr_ref(mgr)?, state(base)?, out(rob)?, out(reader_state)?) };
        let bundle = mgr.poison.guard(|| {
            Ok(mgr
                .manager
                .gather_read_only_snapshot_bundle(&base, ReaderFlags::from_bits(reader_flags))?)
        })?;
        *reader_state = bundle.reader_state().into();
        *rob = Box::into_raw(Box::new(FdbReader {
            reader: Box::new(BundleReader {
                bundle,
                filtered: filtered != 0,
            }),
            poison: Arc::clone(&mgr.poison),
        }));
        Ok(())
    })
}

/// `HasStateForBlock`.
///
/// # Safety
/// `mgr` must be a live manager, `state` valid and `has` valid for writes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_has_state(mgr: *const FdbManager, state_id: *const FfiStateId, has: *mut u8) -> i32 {
    ffi_guard(Export::HasState, || {
        // SAFETY: the caller passes a live manager and valid pointers.
        let (mgr, state_id, has) = unsafe { (mgr_ref(mgr)?, state(state_id)?, out(has)?) };
        *has = mgr
            .poison
            .guard(|| Ok(mgr.manager.has_state_for_block(&state_id)?))?
            .into();
        Ok(())
    })
}

/// `IPersistenceManager.GetCurrentPersistedStateId`.
///
/// # Safety
/// `mgr` must be a live manager and `persisted` valid for writes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_persisted_state(mgr: *const FdbManager, persisted: *mut FfiStateId) -> i32 {
    ffi_guard(Export::PersistedState, || {
        // SAFETY: the caller passes a live manager and a valid out-pointer.
        let (mgr, persisted) = unsafe { (mgr_ref(mgr)?, out(persisted)?) };
        let current = mgr
            .poison
            .guard(|| Ok(mgr.manager.persistence_manager().get_current_persisted_state_id()?))?;
        *persisted = current.into();
        Ok(())
    })
}

/// `IPersistenceManager.ResetPersistedStateId`.
///
/// # Safety
/// `mgr` must be a live manager.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_reset_persisted(mgr: *const FdbManager) -> i32 {
    ffi_guard(Export::ResetPersisted, || {
        // SAFETY: the caller passes a live manager.
        let mgr = unsafe { mgr_ref(mgr)? };
        mgr.poison
            .guard(|| Ok(mgr.manager.persistence_manager().reset_persisted_state_id()?))
    })
}

/// `IPersistenceManager.AddToPersistence`, which the manager's persistence worker also runs after every compaction.
///
/// # Safety
/// `mgr` must be a live manager and `latest` valid.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_add_to_persistence(mgr: *const FdbManager, latest: *const FfiStateId) -> i32 {
    ffi_guard(Export::AddToPersistence, || {
        // SAFETY: the caller passes a live manager and a valid state.
        let (mgr, latest) = unsafe { (mgr_ref(mgr)?, state(latest)?) };
        mgr.poison
            .guard(|| Ok(mgr.manager.persistence_manager().add_to_persistence(&latest)?))
    })
}

/// `IPersistenceManager.FlushToPersistence`: persists every snapshot up to the committed head and stores the persisted
/// state in `persisted`. The bundle cache is left alone; `FlushCache` clears it with [`fdb_clear_bundle_cache`].
///
/// # Safety
/// `mgr` must be a live manager and `persisted` valid for writes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_flush_to_persistence(mgr: *const FdbManager, persisted: *mut FfiStateId) -> i32 {
    ffi_guard(Export::FlushToPersistence, || {
        // SAFETY: the caller passes a live manager and a valid out-pointer.
        let (mgr, persisted) = unsafe { (mgr_ref(mgr)?, out(persisted)?) };
        let flushed = mgr
            .poison
            .guard(|| Ok(mgr.manager.persistence_manager().flush_to_persistence()?))?;
        *persisted = flushed.into();
        Ok(())
    })
}

/// `IPersistenceManager.DropStateNotReachableFrom`. The bundle cache is left alone; `IFlatDbManager` clears it with
/// [`fdb_clear_bundle_cache`].
///
/// # Safety
/// `mgr` must be a live manager and `head` valid.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_drop_unreachable(mgr: *const FdbManager, head: *const FfiStateId) -> i32 {
    ffi_guard(Export::DropUnreachable, || {
        // SAFETY: the caller passes a live manager and a valid state.
        let (mgr, head) = unsafe { (mgr_ref(mgr)?, state(head)?) };
        mgr.poison
            .guard(|| Ok(mgr.manager.persistence_manager().drop_state_not_reachable_from(&head)?))
    })
}

/// Fails, once, with the fatal RocksDB error a persist hit, after which the manager writes nothing more; C# then shuts
/// the node down as `DbOnTheRocks.HandleFatalDbError` does. Ok while there is none, and once it was reported.
///
/// # Safety
/// `mgr` must be a live manager.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_take_fatal_error(mgr: *const FdbManager) -> i32 {
    ffi_guard(Export::TakeFatalError, || {
        // SAFETY: the caller passes a live manager.
        let fatal_error = unsafe { mgr_ref(mgr)? }
            .manager
            .persistence_manager()
            .take_fatal_error();
        fatal_error.map_or(Ok(()), |error| Err(error.into()))
    })
}

/// `ClearReadOnlyBundleCache`.
///
/// # Safety
/// `mgr` must be a live manager.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_clear_bundle_cache(mgr: *const FdbManager) -> i32 {
    ffi_guard(Export::ClearBundleCache, || {
        // SAFETY: the caller passes a live manager.
        unsafe { mgr_ref(mgr)? }.manager.clear_bundle_cache();
        Ok(())
    })
}

/// # Safety
/// `mgr` must be null or a live manager.
unsafe fn mgr_ref<'a>(mgr: *const FdbManager) -> Result<&'a FdbManager, FfiError> {
    // SAFETY: the caller passes null or a live manager.
    unsafe { mgr.as_ref() }.ok_or_else(|| FfiError::invalid_argument("null manager"))
}

/// # Safety
/// `bld` must be null or a live builder no other thread uses.
unsafe fn bld_mut<'a>(bld: *mut FdbBuilder) -> Result<&'a mut FdbBuilder, FfiError> {
    // SAFETY: the caller passes null or a live builder.
    unsafe { bld.as_mut() }.ok_or_else(|| FfiError::invalid_argument("null builder"))
}

/// # Safety
/// `state` must be null or valid.
unsafe fn state(state: *const FfiStateId) -> Result<StateId, FfiError> {
    // SAFETY: the caller passes null or a valid state.
    unsafe { state.as_ref() }
        .map(|state| (*state).into())
        .ok_or_else(|| FfiError::invalid_argument("null state"))
}

#[cfg(test)]
mod tests {
    use std::collections::HashMap;
    use std::panic::{self, AssertUnwindSafe};
    use std::ptr;
    use std::slice;
    use std::sync::Mutex;
    use std::sync::atomic::{AtomicI32, AtomicU64, AtomicUsize, Ordering};
    use std::thread;
    use std::time::{Duration, Instant};

    use flatdb_core::ABI_VERSION;
    use flatdb_core::kv::WriteFlags;
    use flatdb_core::log_ring::LogSource;
    use flatdb_core::persistence::Persistence;
    use flatdb_core::stats::{FlatCounters, Histogram, Observation};
    use flatdb_core::types::{Account, FlatLayout, Hash256, StateId, TreePath, U256};
    use rstest::rstest;

    use super::*;
    use crate::abi::{FdbKvDesc, FdbKvKind, FdbStoreCfg, FdbStoreInfo, FfiAccount, PERSISTED_STORAGE_NODE};
    use crate::faults::Fault;
    use crate::op_stream::writer::OpWriter;
    use crate::{
        fdb_last_error, fdb_reader_get_account, fdb_reader_get_slot, fdb_reader_load_state_rlp,
        fdb_reader_load_storage_rlp, fdb_reader_release, fdb_store_create, fdb_store_destroy,
    };

    /// The host of the tests: a finalized block, canonical roots by block, and a flush status.
    #[derive(Default)]
    struct TestHost {
        finalized: AtomicU64,
        roots: Mutex<HashMap<u64, Hash256>>,
        flushes: AtomicUsize,
        flush_status: AtomicI32,
    }

    fn host_of(ctx: usize) -> &'static TestHost {
        // SAFETY: the tests pass the address of a host that outlives the manager.
        unsafe { &*(ctx as *const TestHost) }
    }

    extern "C" fn finalized_block_number(ctx: usize) -> u64 {
        host_of(ctx).finalized.load(Ordering::SeqCst)
    }

    extern "C" fn finalized_state_root(ctx: usize, block_number: u64, root: &mut Hash256) -> u8 {
        match host_of(ctx).roots.lock().unwrap().get(&block_number) {
            Some(found) => {
                *root = *found;
                1
            }
            None => 0,
        }
    }

    extern "C" fn flush_deferred(ctx: usize) -> i32 {
        let host = host_of(ctx);
        host.flushes.fetch_add(1, Ordering::SeqCst);
        host.flush_status.load(Ordering::SeqCst)
    }

    fn fdb_host(host: &TestHost) -> FdbHost {
        FdbHost {
            ctx: ptr::from_ref(host) as usize,
            finalized_block_number: Some(finalized_block_number),
            finalized_state_root: Some(finalized_state_root),
            flush_deferred: Some(flush_deferred),
        }
    }

    const CFG: FdbMgrCfg = FdbMgrCfg {
        abi_version: ABI_VERSION,
        max_in_flight_compact_job: 4,
        compact_size: 4,
        persisted_snapshot_max_compact_size: 1 << 20,
        compaction_offset: 0,
        min_reorg_depth: 2,
        max_reorg_depth: 64,
        max_in_memory_snapshot_bytes: 0,
        seconds_per_slot: 12,
        in_memory_slot_filter_bits_per_key: 14.0,
        observation_capacity: 1024,
        inline_compaction: 1,
        persisted_node_events: 1,
        detailed_metrics: 0,
    };

    const A: [u8; 20] = [0xAA; 20];
    const B: [u8; 20] = [0xBB; 20];
    const OWNER: Hash256 = [0x0C; 32];

    fn last_error() -> String {
        let mut buffer = [0u8; 512];
        // SAFETY: the buffer is valid.
        let len = unsafe { fdb_last_error(buffer.as_mut_ptr(), buffer.len()) };
        String::from_utf8_lossy(&buffer[..len.min(buffer.len())]).into_owned()
    }

    fn mem_store() -> *mut FdbStore {
        let cfg = FdbStoreCfg {
            abi_version: ABI_VERSION,
            layout: FlatLayout::Flat as u32,
            write_buffer_floor: 0,
            carry_forward: 1,
            log_level: 2,
        };
        let desc = FdbKvDesc {
            kind: FdbKvKind::Mem as u32,
            column_count: 0,
            rocks_api: ptr::null(),
            rocks_db: ptr::null_mut(),
            rocks_column_families: ptr::null(),
            readahead_size: 0,
            prefix_extractor_length: 0,
            verify_checksums: 1,
            wal_sync: 0,
            managed: ptr::null(),
        };
        let (mut store, mut info) = (ptr::null_mut(), FdbStoreInfo::default());
        // SAFETY: every pointer is valid.
        assert_eq!(
            unsafe { fdb_store_create(&cfg, &desc, &mut store, &mut info) },
            FdbStatus::Ok as i32
        );
        store
    }

    fn create(store: *mut FdbStore, cfg: FdbMgrCfg, host: &FdbHost) -> (i32, *mut FdbManager) {
        let mut mgr = ptr::null_mut();
        // SAFETY: every pointer is valid.
        let status = unsafe { fdb_mgr_create(store, &cfg, host, &mut mgr) };
        (status, mgr)
    }

    fn block(number: u64) -> StateId {
        StateId::new(number, [number as u8 + 1; 32])
    }

    fn parent(number: u64) -> StateId {
        if number == 0 {
            StateId::PRE_GENESIS
        } else {
            block(number - 1)
        }
    }

    /// The writes of block `number`: account A at balance `number`, slot 1 of A at `number`, the state root node, a
    /// storage root node of the owner, and from block 3 a placeholder at path `1`; block 5 clears B and writes its slot.
    fn records(number: u64) -> Vec<u8> {
        let mut writer = OpWriter::default();
        writer
            .op(snapshot_stream::SET_ACCOUNT)
            .address(&A)
            .account(&Account::new(1, U256::from_u64(number)))
            .op(snapshot_stream::SET_SLOT)
            .address(&A)
            .u256(&U256::from_u64(1))
            .u256(&U256::from_u64(number))
            .op(snapshot_stream::STATE_NODE)
            .path(&TreePath::from_hex(""))
            .hash(&[number as u8; 32])
            .bytes(&[0xC1, number as u8])
            .op(snapshot_stream::STORAGE_NODE)
            .hash(&OWNER)
            .path(&TreePath::from_hex(""))
            .hash(&[0x80 | number as u8; 32])
            .bytes(&[0xC2, 0x80, number as u8]);
        if number >= 3 {
            writer
                .op(snapshot_stream::STATE_NODE)
                .path(&TreePath::from_hex("1"))
                .hash(&[0xEE; 32])
                .bytes(&[]);
        }
        if number == 5 {
            writer
                .op(snapshot_stream::SELF_DESTRUCT)
                .address(&B)
                .byte(0)
                .op(snapshot_stream::SET_SLOT)
                .address(&B)
                .u256(&U256::from_u64(2))
                .u256(&U256::from_u64(5))
                .op(snapshot_stream::DELETE_ACCOUNT)
                .address(&[0x01; 20]);
        }
        writer.0
    }

    /// Builds the snapshot of `records` from `from` to `to`, in two builders absorbed into one, adds it and queues its
    /// compaction, returning the first status that is not Ok.
    fn add(mgr: *mut FdbManager, from: StateId, to: StateId, records: &[u8]) -> (i32, i64) {
        let (mut first, mut second, mut stall) = (ptr::null_mut(), ptr::null_mut(), 0i64);
        // SAFETY: the manager is live and every pointer valid; the builders are consumed once.
        unsafe {
            assert_eq!(
                fdb_bld_new(&from.into(), &to.into(), 1, 1, 1, 1, &mut first),
                FdbStatus::Ok as i32
            );
            assert_eq!(
                fdb_bld_new(&from.into(), &to.into(), 0, 0, 0, 0, &mut second),
                FdbStatus::Ok as i32
            );
            let split = records.len() / 2;
            let (head, tail) = if split == 0 {
                (records, &[][..])
            } else {
                split_at_record(records)
            };
            assert_eq!(
                fdb_bld_append(first, head.as_ptr(), head.len()),
                FdbStatus::Ok as i32,
                "{}",
                last_error()
            );
            assert_eq!(
                fdb_bld_append(second, tail.as_ptr(), tail.len()),
                FdbStatus::Ok as i32,
                "{}",
                last_error()
            );
            assert_eq!(fdb_bld_absorb(first, second), FdbStatus::Ok as i32);
            let added = fdb_add_snapshot(mgr, first);
            if added != FdbStatus::Ok as i32 {
                return (added, -1);
            }
            (fdb_queue_compaction(mgr, &to.into(), &mut stall), stall)
        }
    }

    /// The first record and the rest.
    fn split_at_record(records: &[u8]) -> (&[u8], &[u8]) {
        // SET_ACCOUNT: tag, address, nonce, balance, storage root, code hash.
        records.split_at(1 + 20 + 8 + 32 + 32 + 32)
    }

    fn gather(mgr: *mut FdbManager, base: StateId, filtered: bool) -> (i32, *mut FdbReader, StateId) {
        let (mut rob, mut reader_state) = (ptr::null_mut(), FfiStateId::default());
        // SAFETY: the manager is live and every pointer valid.
        let status = unsafe { fdb_gather(mgr, &base.into(), 0, filtered.into(), &mut rob, &mut reader_state) };
        (status, rob, reader_state.into())
    }

    fn account_balance(rob: *mut FdbReader, address: &[u8; 20]) -> Option<U256> {
        let (mut account, mut found) = (FfiAccount::default(), 0u8);
        // SAFETY: the reader is live and every pointer valid.
        let status = unsafe { fdb_reader_get_account(rob, address.as_ptr(), &mut account, &mut found) };
        assert_eq!(status, FdbStatus::Ok as i32, "{}", last_error());
        (found != 0).then_some(U256(account.balance))
    }

    fn slot(rob: *mut FdbReader, address: &[u8; 20], slot: u64) -> Option<U256> {
        let (mut value, mut found) = ([0u64; 4], 0u8);
        let slot = U256::from_u64(slot).0;
        // SAFETY: the reader is live and every pointer valid.
        let status =
            unsafe { fdb_reader_get_slot(rob, address.as_ptr(), slot.as_ptr(), value.as_mut_ptr(), &mut found) };
        assert_eq!(status, FdbStatus::Ok as i32, "{}", last_error());
        (found != 0).then_some(U256(value))
    }

    fn state_rlp(rob: *mut FdbReader, path: &str) -> Option<Vec<u8>> {
        let path = TreePath::from_hex(path);
        let (mut buffer, mut len) = ([0u8; 64], 0i64);
        // SAFETY: the reader is live and every pointer valid.
        let status = unsafe {
            fdb_reader_load_state_rlp(
                rob,
                path.path.as_ptr(),
                path.length,
                0,
                buffer.as_mut_ptr(),
                buffer.len(),
                &mut len,
            )
        };
        assert_eq!(status, FdbStatus::Ok as i32, "{}", last_error());
        (len >= 0).then(|| buffer[..len as usize].to_vec())
    }

    fn storage_rlp(rob: *mut FdbReader) -> Option<Vec<u8>> {
        let (mut buffer, mut len) = ([0u8; 64], 0i64);
        // SAFETY: the reader is live and every pointer valid.
        let status = unsafe {
            fdb_reader_load_storage_rlp(
                rob,
                OWNER.as_ptr(),
                [0u8; 32].as_ptr(),
                0,
                0,
                buffer.as_mut_ptr(),
                64,
                &mut len,
            )
        };
        assert_eq!(status, FdbStatus::Ok as i32, "{}", last_error());
        (len >= 0).then(|| buffer[..len as usize].to_vec())
    }

    fn has_state(mgr: *mut FdbManager, state_id: StateId) -> bool {
        let mut has = 9u8;
        // SAFETY: the manager is live and every pointer valid.
        assert_eq!(
            unsafe { fdb_has_state(mgr, &state_id.into(), &mut has) },
            FdbStatus::Ok as i32
        );
        has != 0
    }

    fn persisted_state(mgr: *mut FdbManager) -> StateId {
        let mut persisted = FfiStateId::default();
        // SAFETY: the manager is live and every pointer valid.
        assert_eq!(
            unsafe { fdb_persisted_state(mgr, &mut persisted) },
            FdbStatus::Ok as i32
        );
        persisted.into()
    }

    /// A persisted node as (is storage, path, hash).
    type Node = (u8, TreePath, Hash256);

    /// Every queued persist: its range and its nodes.
    fn take_events(mgr: *mut FdbManager) -> Vec<(StateId, StateId, Vec<Node>)> {
        let mut taken = Vec::new();
        loop {
            let mut event = FdbPersistedEvent {
                from: FfiStateId::default(),
                to: FfiStateId::default(),
                nodes: ptr::null(),
                node_count: 0,
                owner: ptr::null_mut(),
            };
            // SAFETY: the manager is live and the record valid; the owner is released once.
            unsafe {
                assert_eq!(fdb_take_persisted_event(mgr, &mut event), FdbStatus::Ok as i32);
                if event.owner.is_null() {
                    return taken;
                }
                let nodes = slice::from_raw_parts(event.nodes, event.node_count)
                    .iter()
                    .map(|node| (node.is_storage, TreePath::new(node.path, node.path_length), node.hash))
                    .collect();
                taken.push((event.from.into(), event.to.into(), nodes));
                assert_eq!(fdb_persisted_event_release(event.owner), FdbStatus::Ok as i32);
            }
        }
    }

    /// Every record the store logged, or with a null store every one logged outside any store: (source, level, text).
    fn take_log(store: *const FdbStore) -> Vec<(u32, u8, String)> {
        let mut records = Vec::new();
        let (mut source, mut level, mut buffer, mut len, mut has) = (0u32, 0u8, [0u8; 256], 0usize, 1u8);
        loop {
            // SAFETY: every pointer is valid.
            let status = unsafe {
                crate::fdb_store_log_take(
                    store,
                    &mut source,
                    &mut level,
                    buffer.as_mut_ptr(),
                    buffer.len(),
                    &mut len,
                    &mut has,
                )
            };
            assert_eq!(status, FdbStatus::Ok as i32);
            if has == 0 {
                return records;
            }
            records.push((
                source,
                level,
                String::from_utf8_lossy(&buffer[..len.min(256)]).into_owned(),
            ));
        }
    }

    fn counters(mgr: *mut FdbManager) -> &'static FlatCounters {
        // SAFETY: the tests read the counters only while the manager is live.
        unsafe { &*fdb_stats(mgr) }
    }

    #[rstest]
    fn snapshots_are_added_gathered_flushed_and_reported(#[values(false, true)] inline: bool) {
        let host = TestHost::default();
        let store = mem_store();
        let (status, mgr) = create(
            store,
            FdbMgrCfg {
                inline_compaction: inline.into(),
                ..CFG
            },
            &fdb_host(&host),
        );
        assert_eq!(status, FdbStatus::Ok as i32, "{}", last_error());

        for number in 0..10 {
            let (status, stall) = add(mgr, parent(number), block(number), &records(number));
            assert_eq!(status, FdbStatus::Ok as i32, "{}", last_error());
            assert!(stall == -1 || !inline, "only a full compaction queue stalls");
        }
        assert_eq!(
            add(mgr, parent(9), block(9), &records(9)).0,
            FdbStatus::Duplicate as i32
        );
        assert!(has_state(mgr, block(9)) && has_state(mgr, block(0)) && !has_state(mgr, StateId::new(9, [0; 32])));
        assert!(counters(mgr).snapshot_count.load(Ordering::SeqCst) >= 10);

        for filtered in [false, true] {
            let (status, rob, reader_state) = gather(mgr, block(9), filtered);
            assert_eq!(status, FdbStatus::Ok as i32, "{}", last_error());
            assert_eq!(reader_state, StateId::PRE_GENESIS, "nothing is persisted yet");
            assert_eq!(account_balance(rob, &A), Some(U256::from_u64(9)));
            assert_eq!(account_balance(rob, &[0x01; 20]), None, "deleted at block 5");
            assert_eq!(slot(rob, &A, 1), Some(U256::from_u64(9)));
            assert_eq!(slot(rob, &B, 2), Some(U256::from_u64(5)));
            assert_eq!(slot(rob, &B, 3), None, "cleared at block 5");
            assert_eq!(state_rlp(rob, ""), Some(vec![0xC1, 9]));
            assert_eq!(
                state_rlp(rob, "1"),
                None,
                "a placeholder falls through to the empty database"
            );
            assert_eq!(storage_rlp(rob), Some(vec![0xC2, 0x80, 9]));
            // SAFETY: the reader is released once.
            assert_eq!(unsafe { fdb_reader_release(rob) }, FdbStatus::Ok as i32);
        }

        let (status, rob, _) = gather(mgr, StateId::new(3, [0; 32]), false);
        assert_eq!((status, rob), (FdbStatus::StateNotRetained as i32, ptr::null_mut()));

        // A reader held across the flush keeps the store alive and reads the snapshots it was gathered over.
        let (_, held, _) = gather(mgr, block(4), false);
        let mut persisted = FfiStateId::default();
        // SAFETY: the manager is live and every pointer valid.
        assert_eq!(
            unsafe { fdb_flush_to_persistence(mgr, &mut persisted) },
            FdbStatus::Ok as i32,
            "{}",
            last_error()
        );
        assert_eq!(StateId::from(persisted), block(9), "flushed up to the committed head");
        assert_eq!(persisted_state(mgr), block(9));
        assert!(
            host.flushes.load(Ordering::SeqCst) > 0,
            "the deferred block data is flushed first"
        );
        assert_eq!(account_balance(held, &A), Some(U256::from_u64(4)));

        let events = take_events(mgr);
        assert_eq!(events.first().map(|event| event.0), Some(StateId::PRE_GENESIS));
        assert_eq!(events.last().map(|event| event.1), Some(block(9)));
        let nodes: Vec<_> = events.iter().flat_map(|event| event.2.iter().copied()).collect();
        assert!(nodes.contains(&(0, TreePath::from_hex(""), [9; 32])));
        assert!(nodes.contains(&(PERSISTED_STORAGE_NODE, TreePath::from_hex(""), [0x89; 32])));
        assert!(
            !nodes.iter().any(|node| node.1 == TreePath::from_hex("1")),
            "placeholders are never written"
        );

        // SAFETY: the manager is live and the clear has no out-pointer.
        assert_eq!(unsafe { fdb_clear_bundle_cache(mgr) }, FdbStatus::Ok as i32);
        let (_, rob, reader_state) = gather(mgr, block(9), false);
        assert_eq!(reader_state, block(9));
        assert_eq!(slot(rob, &B, 2), Some(U256::from_u64(5)), "persisted");
        assert_eq!(account_balance(rob, &[0x01; 20]), None);
        assert_eq!(add(mgr, parent(9), block(9), &records(9)).0, FdbStatus::Rejected as i32);

        let observed: Vec<Observation> = {
            let mut buffer = vec![
                Observation {
                    metric: 0,
                    label: 0,
                    value: 0.0
                };
                4096
            ];
            let mut count = 0usize;
            // SAFETY: the buffer holds 4096 observations.
            assert_eq!(
                unsafe { fdb_drain_observations(mgr, buffer.as_mut_ptr(), buffer.len(), &mut count) },
                FdbStatus::Ok as i32
            );
            buffer.truncate(count);
            buffer
        };
        assert!(
            observed
                .iter()
                .any(|observation| observation.metric == Histogram::FlatPersistenceTime as u16)
        );

        // SAFETY: every handle is released once, the store last.
        unsafe {
            assert_eq!(
                fdb_store_destroy(store),
                FdbStatus::InvalidArgument as i32,
                "the manager holds the store"
            );
            assert_eq!(fdb_shutdown(mgr), FdbStatus::Ok as i32);
            assert_eq!(fdb_shutdown(mgr), FdbStatus::Ok as i32, "idempotent");
            assert_eq!(
                add(mgr, block(9), block(10), &records(10)).0,
                FdbStatus::ShutDown as i32
            );
            assert_eq!(fdb_destroy(mgr), FdbStatus::Ok as i32);
            assert_eq!(
                account_balance(rob, &A),
                Some(U256::from_u64(9)),
                "a reader outlives its manager"
            );
            assert_eq!(
                fdb_store_destroy(store),
                FdbStatus::InvalidArgument as i32,
                "readers hold the store"
            );
            fdb_reader_release(rob);
            fdb_reader_release(held);
            assert_eq!(fdb_store_destroy(store), FdbStatus::Ok as i32);
        }
    }

    #[test]
    fn a_failed_or_cancelled_deferred_flush_aborts_the_persist() {
        let host = TestHost::default();
        let store = mem_store();
        let (_, mgr) = create(store, CFG, &fdb_host(&host));
        add(mgr, parent(0), block(0), &records(0));

        let mut persisted = FfiStateId::default();
        for (flush_status, expected) in [(7, FdbStatus::InvalidOperation), (3, FdbStatus::Cancelled)] {
            host.flush_status.store(flush_status, Ordering::SeqCst);
            // SAFETY: the manager is live and every pointer valid.
            assert_eq!(
                unsafe { fdb_flush_to_persistence(mgr, &mut persisted) },
                expected as i32
            );
            assert_eq!(persisted_state(mgr), StateId::PRE_GENESIS);
        }

        host.flush_status.store(0, Ordering::SeqCst);
        // SAFETY: as above; the handles are released once.
        unsafe {
            assert_eq!(fdb_flush_to_persistence(mgr, &mut persisted), FdbStatus::Ok as i32);
            assert_eq!(StateId::from(persisted), block(0));
            fdb_destroy(mgr);
            fdb_store_destroy(store);
        }
    }

    #[test]
    fn unreachable_states_are_dropped_and_finality_drives_persistence() {
        let host = TestHost::default();
        let store = mem_store();
        let (_, mgr) = create(store, CFG, &fdb_host(&host));
        for number in 0..12 {
            add(mgr, parent(number), block(number), &records(number));
        }
        let fork = StateId::new(6, [0xF0; 32]);
        add(mgr, block(5), fork, &records(6));
        assert!(has_state(mgr, fork));

        // SAFETY: the manager is live and the state valid.
        assert_eq!(
            unsafe { fdb_drop_unreachable(mgr, &block(11).into()) },
            FdbStatus::Ok as i32
        );
        assert!(!has_state(mgr, fork) && has_state(mgr, block(11)));

        // Finalizing block 4, the first boundary, persists it once the head is MinReorgDepth above it.
        host.finalized.store(8, Ordering::SeqCst);
        host.roots.lock().unwrap().insert(4, block(4).state_root);
        // SAFETY: as above.
        assert_eq!(
            unsafe { fdb_add_to_persistence(mgr, &block(11).into()) },
            FdbStatus::Ok as i32
        );
        assert_eq!(persisted_state(mgr), block(4));
        assert!(!has_state(mgr, block(3)));

        // SAFETY: the handles are released once.
        unsafe {
            assert_eq!(fdb_reset_persisted(mgr), FdbStatus::Ok as i32);
            assert_eq!(persisted_state(mgr), block(4));
            fdb_destroy(mgr);
            fdb_store_destroy(store);
        }
    }

    /// A panic unwinding through a batch of the manager's persistence poisons the store, as one inside an export does,
    /// and the persistence the worker threads write through then refuses to.
    #[test]
    fn a_panic_through_a_batch_poisons_the_store_and_the_workers_write_nothing_more() {
        let host = TestHost::default();
        let store = mem_store();
        let (_, mgr) = create(store, CFG, &fdb_host(&host));
        for number in 0..=5 {
            add(mgr, parent(number), block(number), &records(number));
        }
        let mut persisted = FfiStateId::default();
        // SAFETY: the manager is live and every pointer valid.
        assert_eq!(
            unsafe { fdb_flush_to_persistence(mgr, &mut persisted) },
            FdbStatus::Ok as i32
        );
        for number in 6..=12 {
            add(mgr, parent(number), block(number), &records(number));
        }

        // SAFETY: the manager is live until destroyed below.
        let persistence_manager = unsafe { &*mgr }.manager.persistence_manager();
        let batch = Persistence::create_write_batch(
            &**persistence_manager.persistence(),
            &block(5),
            &block(6),
            WriteFlags::NONE,
        )
        .unwrap();
        let panicked = panic::catch_unwind(AssertUnwindSafe(move || {
            let _open = batch;
            panic!("injected");
        }));
        // Block 8, the next boundary, is now finalized, as the persistence thread would find.
        host.finalized.store(10, Ordering::SeqCst);
        host.roots.lock().unwrap().insert(8, block(8).state_root);
        let refused = persistence_manager.add_to_persistence(&block(12));
        // SAFETY: the store is live.
        let written = unsafe { &*store }
            .persistence
            .create_reader(ReaderFlags::NONE)
            .unwrap()
            .current_state();

        assert!(panicked.is_err());
        assert!(refused.is_err(), "{refused:?}");
        assert_eq!(written, block(5));
        // SAFETY: the handles are released once.
        unsafe {
            assert_eq!(
                fdb_add_to_persistence(mgr, &block(12).into()),
                FdbStatus::Poisoned as i32
            );
            assert_eq!(
                fdb_take_fatal_error(mgr),
                FdbStatus::Ok as i32,
                "a panic is no RocksDB error"
            );
            assert_eq!(fdb_destroy(mgr), FdbStatus::Ok as i32);
            assert_eq!(fdb_store_destroy(store), FdbStatus::Ok as i32);
        }
    }

    #[rstest]
    #[case::abi(FdbMgrCfg { abi_version: ABI_VERSION + 1, ..CFG }, true, "ABI version")]
    #[case::no_queue(FdbMgrCfg { max_in_flight_compact_job: 0, ..CFG }, true, "MaxInFlightCompactJob")]
    #[case::compact_size(FdbMgrCfg { compact_size: 3, ..CFG }, true, "power of 2")]
    #[case::callback(CFG, false, "lacks a callback")]
    fn invalid_settings_are_refused(#[case] cfg: FdbMgrCfg, #[case] complete_host: bool, #[case] message: &str) {
        let host = TestHost::default();
        let mut callbacks = fdb_host(&host);
        if !complete_host {
            callbacks.flush_deferred = None;
        }
        let store = mem_store();
        let (status, mgr) = create(store, cfg, &callbacks);
        assert_eq!((status, mgr), (FdbStatus::InvalidArgument as i32, ptr::null_mut()));
        assert!(last_error().contains(message), "{}", last_error());
        // SAFETY: no handle is open.
        assert_eq!(unsafe { fdb_store_destroy(store) }, FdbStatus::Ok as i32);
    }

    #[rstest]
    #[case::truncated(vec![snapshot_stream::SELF_DESTRUCT, 0xAA], "truncated (record 0 at byte 0)")]
    #[case::unknown_tag(vec![9], "unknown record tag 9 (record 0 at byte 0)")]
    #[case::long_path([vec![snapshot_stream::STATE_NODE], vec![0; 32], vec![65]].concat(), "path length 65")]
    fn malformed_records_fail_the_append(#[case] records: Vec<u8>, #[case] message: &str) {
        let mut bld = ptr::null_mut();
        // SAFETY: every pointer is valid and the builder released once.
        unsafe {
            fdb_bld_new(&block(0).into(), &block(1).into(), 0, 0, 0, 0, &mut bld);
            assert_eq!(
                fdb_bld_append(bld, records.as_ptr(), records.len()),
                FdbStatus::InvalidArgument as i32
            );
            assert_eq!(fdb_bld_release(bld), FdbStatus::Ok as i32);
        }
        assert!(last_error().contains(message), "{}", last_error());
    }

    /// Every store logs into a sink of its own, which its drain takes with the C# class of each line; nothing reaches
    /// another store's sink or the global ring.
    #[test]
    fn log_records_reach_the_sink_of_their_store() {
        let host = TestHost::default();
        let (store, other) = (mem_store(), mem_store());
        let (_, mgr) = create(store, CFG, &fdb_host(&host));
        add(mgr, parent(0), block(0), &records(0));
        add(mgr, parent(0), block(0), &records(0));

        let duplicate = (
            LogSource::FlatDbManager as u32,
            2,
            format!("State {} already added", block(0)),
        );
        assert!(take_log(store).contains(&duplicate), "{:?}", take_log(store));
        assert!(take_log(other).is_empty());
        assert!(!take_log(ptr::null()).contains(&duplicate));
        // SAFETY: the stores are live.
        assert_eq!(unsafe { crate::fdb_store_log_dropped(store) }, 0);

        // SAFETY: the handles are released once.
        unsafe {
            fdb_destroy(mgr);
            fdb_store_destroy(store);
            fdb_store_destroy(other);
        }
    }

    fn inject(store: *const FdbStore, fault: Fault, message: &str) {
        // SAFETY: the store is live and the message valid.
        let status = unsafe { crate::fdb_test_inject_fault(store, fault as u32, message.as_ptr(), message.len()) };
        assert_eq!(status, FdbStatus::Ok as i32);
    }

    /// A fatal RocksDB error writing a persist writes nothing and freezes the persisted state: every later persist
    /// fails with it, and it is handed out once, for C# to shut the node down on. A store flush tolerates a flush error
    /// that is not fatal, logging it as `DbOnTheRocks` does, and fails with a fatal one.
    #[test]
    fn injected_rocksdb_errors_freeze_the_persisted_state_or_are_tolerated() {
        let host = TestHost::default();
        let store = mem_store();
        let (_, mgr) = create(store, CFG, &fdb_host(&host));
        let mut persisted = FfiStateId::default();
        for number in 0..=9 {
            add(mgr, parent(number), block(number), &records(number));
            if number == 5 {
                // SAFETY: the manager is live and the out-pointer valid.
                assert_eq!(
                    unsafe { fdb_flush_to_persistence(mgr, &mut persisted) },
                    FdbStatus::Ok as i32
                );
            }
        }

        inject(store, Fault::KvFlush, "Busy: injected");
        // SAFETY: the store is live.
        let tolerated = unsafe { crate::fdb_store_flush(store) };
        inject(store, Fault::KvFlush, "IO error: injected flush");
        // SAFETY: as above.
        let fatal_flush = (unsafe { crate::fdb_store_flush(store) }, last_error());
        inject(store, Fault::KvWrite, "IO error: injected write");
        // SAFETY: the manager is live and the out-pointer valid.
        let persists = [(); 2].map(|_| unsafe { (fdb_flush_to_persistence(mgr, &mut persisted), last_error()) });
        // SAFETY: as above.
        let reported = [(); 2].map(|_| unsafe { fdb_take_fatal_error(mgr) });
        // SAFETY: the store is live.
        let written = unsafe { &*store }
            .persistence
            .create_reader(ReaderFlags::NONE)
            .unwrap()
            .current_state();

        assert_eq!(tolerated, FdbStatus::Ok as i32);
        assert!(take_log(store).contains(&(
            LogSource::DbOnTheRocks as u32,
            2,
            "Failed to flush Flat DB: Busy: injected".to_owned()
        )));
        assert_eq!(
            fatal_flush,
            (FdbStatus::RocksDb as i32, "IO error: injected flush".to_owned())
        );
        assert_eq!(
            persists,
            [(); 2].map(|_| (FdbStatus::RocksDb as i32, "IO error: injected write".to_owned())),
            "the second persist fails with the first error, as nothing more may be written"
        );
        assert_eq!(reported, [FdbStatus::RocksDb as i32, FdbStatus::Ok as i32]);
        assert_eq!((persisted_state(mgr), written), (block(5), block(5)));
        // SAFETY: the handles are released once.
        unsafe {
            assert_eq!(fdb_destroy(mgr), FdbStatus::Ok as i32);
            assert_eq!(fdb_store_destroy(store), FdbStatus::Ok as i32);
        }
    }

    /// Where a panic strikes.
    #[derive(Clone, Copy, Debug, PartialEq, Eq)]
    enum PanicSite {
        Export,
        PersistWorker,
        CompactorWorker,
    }

    /// A panic inside an export, or in a job of the manager's persistence or compaction worker, poisons the store:
    /// every later call on it, its readers and its manager fails with Poisoned, while the releases still work.
    #[rstest]
    fn a_panic_anywhere_poisons_the_store_for_every_later_call(
        #[values(PanicSite::Export, PanicSite::PersistWorker, PanicSite::CompactorWorker)] site: PanicSite,
    ) {
        let has_state = |mgr| {
            let mut has = 0u8;
            // SAFETY: the manager is live and every pointer valid.
            unsafe { fdb_has_state(mgr, &block(0).into(), &mut has) }
        };
        // Block 4, the first boundary, is finalized, so the persistence worker persists once the head reaches block 6.
        let host = TestHost::default();
        host.finalized.store(8, Ordering::SeqCst);
        host.roots.lock().unwrap().insert(4, block(4).state_root);
        let store = mem_store();
        let cfg = FdbMgrCfg {
            inline_compaction: (site != PanicSite::CompactorWorker).into(),
            ..CFG
        };
        let (_, mgr) = create(store, cfg, &fdb_host(&host));
        add(mgr, parent(0), block(0), &records(0));
        let (_, held, _) = gather(mgr, block(0), false);

        let fault = match site {
            PanicSite::Export => Fault::PanicInExport,
            PanicSite::PersistWorker => Fault::PanicInPersist,
            PanicSite::CompactorWorker => Fault::PanicInCompaction,
        };
        inject(store, fault, "injected");
        let panicked = if site == PanicSite::Export {
            (has_state(mgr), last_error())
        } else {
            for number in 1..=6 {
                add(mgr, parent(number), block(number), &records(number));
            }
            let deadline = Instant::now() + Duration::from_secs(10);
            while has_state(mgr) != FdbStatus::Poisoned as i32 && Instant::now() < deadline {
                thread::sleep(Duration::from_millis(10));
            }
            (FdbStatus::Panic as i32, "panic: injected".to_owned())
        };
        let logged = take_log(store);

        let (mut bld, mut reader, mut rob, mut current, mut account, mut found) = (
            ptr::null_mut(),
            ptr::null_mut(),
            ptr::null_mut(),
            FfiStateId::default(),
            FfiAccount::default(),
            0u8,
        );
        // SAFETY: every handle is live and every pointer valid; the handles are released once.
        unsafe {
            fdb_bld_new(&block(6).into(), &block(7).into(), 0, 0, 0, 0, &mut bld);
            let statuses = [
                has_state(mgr),
                fdb_add_snapshot(mgr, bld),
                fdb_gather(mgr, &block(0).into(), 0, 0, &mut rob, &mut current),
                fdb_flush_to_persistence(mgr, &mut current),
                fdb_drop_unreachable(mgr, &block(0).into()),
                crate::fdb_reader_open(store, 0, 0, &mut reader, &mut current),
                crate::fdb_store_flush(store),
                fdb_reader_get_account(held, A.as_ptr(), &mut account, &mut found),
            ];
            let released = [
                fdb_reader_release(held),
                fdb_shutdown(mgr),
                fdb_destroy(mgr),
                fdb_store_destroy(store),
            ];

            assert_eq!(panicked, (FdbStatus::Panic as i32, "panic: injected".to_owned()));
            assert_eq!(statuses, [FdbStatus::Poisoned as i32; 8]);
            assert_eq!(released, [FdbStatus::Ok as i32; 4]);
        }
        if site != PanicSite::Export {
            let job = if site == PanicSite::PersistWorker {
                "Persisting"
            } else {
                "Compacting"
            };
            assert!(
                logged
                    .iter()
                    .any(|(source, level, message)| *source == LogSource::FlatDbManager as u32
                        && *level == 1
                        && message.starts_with(&format!("Error on {job} "))
                        && message.ends_with(": injected")),
                "{logged:?}"
            );
        }
    }
}
