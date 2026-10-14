//! The snapshot manager handle and its construction over a store.

use std::ffi::c_void;
use std::path::PathBuf;
use std::sync::Arc;
use std::sync::atomic::Ordering;

use flatdb_core::ABI_VERSION;
use flatdb_core::bundle::ReadOnlySnapshotBundle;
use flatdb_core::compactor::{Compactor, SnapshotCompactor};
use flatdb_core::events::{EventKind, PersistedNodeEvents, PersistedNodes};
use flatdb_core::host::HostCallbacks;
use flatdb_core::kv::Column;
use flatdb_core::kv::rocks::{RocksDesc, RocksKv};
use flatdb_core::manager::{FlatDbManager, ManagerConfig};
use flatdb_core::persisted::storage::arena_manager::ArenaConfig;
use flatdb_core::persisted::storage::catalog::{CatalogKv, ManagedCatalogKv, RocksCatalogKv};
use flatdb_core::persisted::tier::{PersistedTier, TierHooks, TierSettings};
use flatdb_core::persisted::{PersistedTierConfig, TierFaults};
use flatdb_core::persistence_manager::{LongFinalityConfig, PersistenceManager, PersistenceManagerConfig};
use flatdb_core::repository::SnapshotRepository;
use flatdb_core::schedule::CompactionSchedule;
use flatdb_core::stats::{FlatStats, TierStats};
use flatdb_core::types::StateId;

use crate::abi::{
    FdbCatalogDesc, FdbCatalogKind, FdbHost, FdbMgrCfg, FdbStatus, FdbTierCfg, FfiError, FfiPersistedNode, FfiStateId,
    PERSISTED_STORAGE_NODE,
};
use crate::bytes;
use crate::guard::Poison;
use crate::persistence::{ErasedPersistence, ErasedReader};
use crate::store::FdbStore;

pub type Manager = FlatDbManager<ErasedPersistence>;
pub type Bundle = ReadOnlySnapshotBundle<ErasedReader>;

/// A manager, handed to C# as an opaque pointer: `FlatDbManager` and `PersistenceManager` over the persistence stack
/// of a store, which it shares.
pub struct FdbManager {
    pub manager: Manager,
    pub events: Arc<PersistedNodeEvents>,
    /// The `{tier, size}` gauges of the persisted-snapshot tier; `None` without the tier.
    pub tier_stats: Option<Arc<TierStats>>,
    /// The store's poison flag, which every call on the manager checks, and a handle count that keeps the store alive.
    pub poison: Arc<Poison>,
}

impl FdbManager {
    /// Starts a manager over `store`, with its worker threads running. With a tier in `cfg`, the tier is opened from
    /// its files and catalog rows first, as the `FlatDbManager` constructor loads it before any worker runs.
    ///
    /// # Safety
    /// `cfg.tier` must be null or valid, with the pointers of [`open_tier`].
    pub unsafe fn create(store: &FdbStore, cfg: &FdbMgrCfg, host: &FdbHost) -> Result<Self, FfiError> {
        if cfg.abi_version != ABI_VERSION {
            return Err(FfiError::invalid_argument(format!(
                "the adapter speaks ABI version {}, the library {ABI_VERSION}",
                cfg.abi_version
            )));
        }
        if cfg.max_in_flight_compact_job == 0 {
            return Err(FfiError::invalid_argument("MaxInFlightCompactJob must be positive"));
        }
        let host = Arc::new(HostCallbacks::try_from(host)?);
        let schedule = CompactionSchedule::new(
            cfg.compact_size,
            cfg.persisted_snapshot_max_compact_size,
            cfg.compaction_offset,
        )
        .map_err(|error| FfiError::invalid_argument(error.0))?;

        if let Some(metrics) = &store.carry_forward_metrics {
            metrics
                .detailed_metrics_enabled
                .store(cfg.detailed_metrics != 0, Ordering::Relaxed);
        }

        let stats = Arc::new(FlatStats::new(cfg.observation_capacity as usize));
        // SAFETY: the caller passes a null or valid tier.
        let (repository, tier, long_finality, tier_stats) = match unsafe { cfg.tier.as_ref() } {
            None => (
                Arc::new(SnapshotRepository::with_stats(host.clone(), stats)),
                None,
                None,
                None,
            ),
            Some(tier_cfg) => {
                let tier_stats = Arc::new(TierStats::new(stats.clone()));
                // SAFETY: as the caller vouches.
                let (repository, tier) =
                    unsafe { open_tier(store, tier_cfg, schedule, host.clone(), stats, tier_stats.clone()) }?;
                let long_finality = LongFinalityConfig {
                    max_reorg_depth: tier_cfg.long_finality_max_reorg_depth,
                    max_in_memory_base_snapshot_count: tier_cfg.max_in_memory_base_snapshot_count,
                };
                (repository, Some(tier), Some(long_finality), Some(tier_stats))
            }
        };
        let events = Arc::new(PersistedNodeEvents::new(cfg.persisted_node_events != 0));
        let persistence_manager = Arc::new(PersistenceManager::new(
            PersistenceManagerConfig {
                min_reorg_depth: cfg.min_reorg_depth,
                max_reorg_depth: cfg.max_reorg_depth,
                max_in_memory_snapshot_bytes: cfg.max_in_memory_snapshot_bytes,
                long_finality,
            },
            schedule,
            host,
            Arc::new(ErasedPersistence {
                persistence: Arc::clone(&store.persistence),
                poison: Arc::clone(&store.poison),
            }),
            Arc::clone(&repository),
            Arc::clone(&events),
            tier,
        ));
        let compactor: Arc<dyn Compactor> = Arc::new(SnapshotCompactor::new(schedule, repository));
        #[cfg(any(test, feature = "test-api"))]
        let compactor: Arc<dyn Compactor> = Arc::new(crate::faults::FaultyCompactor {
            compactor,
            faults: Arc::clone(&store.poison.faults),
        });
        // A worker job that panicked may have left the snapshots or the persistence half-updated, as a panic inside an
        // export may, so it poisons the store too.
        let poison = Arc::clone(&store.poison);
        let manager = FlatDbManager::with_job_panic_hook(
            ManagerConfig {
                max_in_flight_compact_job: cfg.max_in_flight_compact_job as usize,
                inline_compaction: cfg.inline_compaction != 0,
                seconds_per_slot: cfg.seconds_per_slot,
                in_memory_slot_filter_bits_per_key: cfg.in_memory_slot_filter_bits_per_key,
                ..ManagerConfig::default()
            },
            compactor,
            persistence_manager,
            move |_| poison.set(),
        );

        Ok(Self {
            manager,
            events,
            tier_stats,
            poison: Arc::clone(&store.poison),
        })
    }

    /// Takes the oldest queued persist, as a record C# reads in place until it releases the owner.
    pub fn take_persisted_event(&self) -> Option<PersistedEvent> {
        self.events.drain(1).pop().map(PersistedEvent::from)
    }
}

/// A queued persist with its nodes in their `repr(C)` form.
pub struct PersistedEvent {
    pub kind: EventKind,
    pub from: FfiStateId,
    pub to: FfiStateId,
    pub nodes: Vec<FfiPersistedNode>,
}

impl From<PersistedNodes> for PersistedEvent {
    fn from(event: PersistedNodes) -> Self {
        Self {
            kind: event.kind,
            from: event.from.into(),
            to: event.to.into(),
            nodes: event
                .nodes
                .iter()
                .map(|node| FfiPersistedNode {
                    address_hash: node.address_hash.unwrap_or_default(),
                    path: node.path.path,
                    hash: node.hash,
                    path_length: node.path.length,
                    is_storage: if node.address_hash.is_some() {
                        PERSISTED_STORAGE_NODE
                    } else {
                        0
                    },
                })
                .collect(),
        }
    }
}

/// The status of a refused `AddSnapshot`.
pub fn refused(status: FdbStatus, to: &StateId) -> FfiError {
    FfiError {
        status,
        message: format!("State {to:?} was not added"),
    }
}

/// Opens the persisted-snapshot tier of `tier_cfg` over a repository sharing `stats`, with its catalog writes going to
/// the database the configuration describes.
///
/// # Safety
/// The directories and catalog rows of `tier_cfg` must be valid for their lengths, and its catalog descriptor must
/// satisfy [`catalog_kv`].
unsafe fn open_tier(
    store: &FdbStore,
    tier_cfg: &FdbTierCfg,
    schedule: CompactionSchedule,
    host: Arc<HostCallbacks>,
    stats: Arc<FlatStats>,
    tier_stats: Arc<TierStats>,
) -> Result<(Arc<SnapshotRepository>, PersistedTier), FfiError> {
    // SAFETY: the caller passes valid directories and rows.
    let (arena_directory, blob_directory, catalog_rows) = unsafe {
        (
            directory(tier_cfg.arena_dir, tier_cfg.arena_dir_len)?,
            directory(tier_cfg.blob_dir, tier_cfg.blob_dir_len)?,
            bytes(tier_cfg.catalog_rows, tier_cfg.catalog_rows_len)?,
        )
    };
    let settings = TierSettings {
        arena_directory,
        blob_directory,
        arena: ArenaConfig {
            arena_file_size_bytes: tier_cfg.arena_file_size_bytes,
            dedicated_arena_threshold_bytes: tier_cfg.dedicated_arena_threshold_bytes,
            page_cache_bytes: tier_cfg.arena_page_cache_bytes,
            punch_hole_on_reclaim: tier_cfg.punch_hole_on_reclaim != 0,
        },
        tier: PersistedTierConfig {
            bloom_bits_per_key: tier_cfg.bloom_bits_per_key,
            validate_persisted_snapshot: tier_cfg.validate_persisted_snapshot != 0,
            processor_count: (tier_cfg.processor_count as usize).max(1),
        },
        schedule,
    };
    // SAFETY: as the caller vouches.
    let catalog = unsafe { catalog_kv(&tier_cfg.catalog, &tier_stats) }?;
    #[cfg(any(test, feature = "test-api"))]
    let catalog: Box<dyn CatalogKv> = Box::new(crate::faults::FaultyCatalogKv {
        catalog,
        faults: Arc::clone(&store.poison.faults),
    });
    #[cfg(any(test, feature = "test-api"))]
    let faults: Option<Arc<dyn TierFaults>> =
        Some(Arc::new(crate::faults::FaultyTier(Arc::clone(&store.poison.faults))));
    #[cfg(not(any(test, feature = "test-api")))]
    let faults: Option<Arc<dyn TierFaults>> = None;

    let poison = Arc::clone(&store.poison);
    Ok(PersistedTier::open(
        &settings,
        catalog,
        catalog_rows,
        host,
        stats,
        tier_stats,
        TierHooks {
            on_job_panic: Box::new(move |_| poison.set()),
            faults,
        },
    )?)
}

/// The catalog database `desc` describes: the RocksDB instance of a C# `DbOnTheRocks`, every column its default column
/// family, or the callbacks into a C# `IDb`.
///
/// # Safety
/// For [`FdbCatalogKind::Rocks`], the RocksDB handles of `desc` must stay open while the tier lives; for
/// [`FdbCatalogKind::Managed`], the callbacks must be callable from any thread with their context until then.
unsafe fn catalog_kv(desc: &FdbCatalogDesc, tier_stats: &TierStats) -> Result<Box<dyn CatalogKv>, FfiError> {
    match desc.kind {
        kind if kind == FdbCatalogKind::Rocks as u8 => {
            // SAFETY: the caller passes a valid descriptor.
            let api = unsafe { desc.rocks_api.as_ref() }.ok_or_else(|| FfiError::invalid_argument("null RocksApi"))?;
            let rocks = RocksDesc {
                db: desc.rocks_db,
                column_families: [desc.default_column_family; Column::COUNT],
                verify_checksums: desc.verify_checksums != 0,
                wal_sync: desc.wal_sync != 0,
                readahead_size: 0,
                prefix_extractor_length: 0,
            };
            // SAFETY: the caller keeps the catalog database open while the tier lives.
            let kv = unsafe { RocksKv::new(api, rocks) }?;
            Ok(Box::new(RocksCatalogKv::new(kv, tier_stats.counters.clone())))
        }
        kind if kind == FdbCatalogKind::Managed as u8 => {
            // SAFETY: the caller passes a valid descriptor.
            let vtable = unsafe { desc.managed.as_ref() }
                .ok_or_else(|| FfiError::invalid_argument("null managed catalog callbacks"))?;
            let (Some(put), Some(key_exists), Some(remove)) = (vtable.put, vtable.key_exists, vtable.remove) else {
                return Err(FfiError::invalid_argument("the managed catalog lacks a callback"));
            };
            // Shared with the callbacks as an address, which the caller vouches is usable from any thread.
            let ctx = vtable.ctx as usize;
            Ok(Box::new(ManagedCatalogKv::new(
                // SAFETY: the callbacks get the context and slices valid for the call, as the caller expects.
                move |key, value| unsafe {
                    put(ctx as *mut c_void, key.as_ptr(), key.len(), value.as_ptr(), value.len())
                },
                move |key, exists| {
                    let mut found = 0u8;
                    // SAFETY: as above.
                    let status = unsafe { key_exists(ctx as *mut c_void, key.as_ptr(), key.len(), &mut found) };
                    *exists = found != 0;
                    status
                },
                // SAFETY: as above.
                move |key| unsafe { remove(ctx as *mut c_void, key.as_ptr(), key.len()) },
            )))
        }
        kind => Err(FfiError::invalid_argument(format!("unknown catalog backend {kind}"))),
    }
}

/// # Safety
/// `path` must be valid for `len` bytes.
unsafe fn directory(path: *const u8, len: usize) -> Result<PathBuf, FfiError> {
    // SAFETY: the caller passes a valid path.
    let path = unsafe { bytes(path, len) }?;
    match std::str::from_utf8(path) {
        Ok(path) if !path.is_empty() => Ok(PathBuf::from(path)),
        _ => Err(FfiError::invalid_argument("a tier directory is empty or not UTF-8")),
    }
}
