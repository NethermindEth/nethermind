//! The snapshot manager handle and its construction over a store.

use std::sync::Arc;
use std::sync::atomic::Ordering;

use flatdb_core::ABI_VERSION;
use flatdb_core::bundle::ReadOnlySnapshotBundle;
use flatdb_core::compactor::{Compactor, SnapshotCompactor};
use flatdb_core::events::{PersistedNodeEvents, PersistedNodes};
use flatdb_core::host::HostCallbacks;
use flatdb_core::manager::{FlatDbManager, ManagerConfig};
use flatdb_core::persistence_manager::{PersistenceManager, PersistenceManagerConfig};
use flatdb_core::repository::SnapshotRepository;
use flatdb_core::schedule::CompactionSchedule;
use flatdb_core::stats::FlatStats;
use flatdb_core::types::StateId;

use crate::abi::{FdbHost, FdbMgrCfg, FdbStatus, FfiError, FfiPersistedNode, FfiStateId, PERSISTED_STORAGE_NODE};
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
    /// The store's poison flag, which every call on the manager checks, and a handle count that keeps the store alive.
    pub poison: Arc<Poison>,
}

impl FdbManager {
    /// Starts a manager over `store`, with its worker threads running.
    pub fn create(store: &FdbStore, cfg: &FdbMgrCfg, host: &FdbHost) -> Result<Self, FfiError> {
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
        let repository = Arc::new(SnapshotRepository::with_stats(host.clone(), stats));
        let events = Arc::new(PersistedNodeEvents::new(cfg.persisted_node_events != 0));
        let persistence_manager = Arc::new(PersistenceManager::new(
            PersistenceManagerConfig {
                min_reorg_depth: cfg.min_reorg_depth,
                max_reorg_depth: cfg.max_reorg_depth,
                max_in_memory_snapshot_bytes: cfg.max_in_memory_snapshot_bytes,
            },
            schedule,
            host,
            Arc::new(ErasedPersistence {
                persistence: Arc::clone(&store.persistence),
                poison: Arc::clone(&store.poison),
            }),
            Arc::clone(&repository),
            Arc::clone(&events),
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
    pub from: FfiStateId,
    pub to: FfiStateId,
    pub nodes: Vec<FfiPersistedNode>,
}

impl From<PersistedNodes> for PersistedEvent {
    fn from(event: PersistedNodes) -> Self {
        Self {
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
