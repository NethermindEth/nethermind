//! The snapshot manager handle and its construction over a store, the snapshot builder handle, and the reader a
//! gathered bundle is handed to C# as.

use std::sync::Arc;
use std::sync::atomic::Ordering;

use flatdb_core::ABI_VERSION;
use flatdb_core::bundle::ReadOnlySnapshotBundle;
use flatdb_core::compactor::{Compactor, SnapshotCompactor};
use flatdb_core::events::{PersistedNodeEvents, PersistedNodes};
use flatdb_core::host::HostCallbacks;
use flatdb_core::kv::ReadFlags;
use flatdb_core::manager::{FlatDbManager, ManagerConfig};
use flatdb_core::persistence::{EmptyIterator, NoopPersistenceReader, PersistenceReader, PersistenceResult};
use flatdb_core::persistence_manager::{PersistenceManager, PersistenceManagerConfig};
use flatdb_core::repository::SnapshotRepository;
use flatdb_core::schedule::CompactionSchedule;
use flatdb_core::snapshot::content::SnapshotContentBuilder;
use flatdb_core::stats::FlatStats;
use flatdb_core::types::{Account, Address, Hash256, StateId, TreePath, U256};

use crate::abi::{FdbHost, FdbMgrCfg, FdbStatus, FfiError, FfiPersistedNode, FfiStateId, PERSISTED_STORAGE_NODE};
use crate::guard::Poison;
use crate::persistence::{ErasedIter, ErasedPersistence, ErasedReader};
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

/// A snapshot being built, handed to C# as an opaque pointer until `fdb_add_snapshot` consumes it.
pub struct FdbBuilder {
    pub from: StateId,
    pub to: StateId,
    pub content: SnapshotContentBuilder,
}

/// A gathered bundle as a reader: the reads of `ReadOnlySnapshotBundle` over the snapshots, then the persistence; the
/// raw reads and iterators, which the C# bundle never serves, from the persistence alone.
pub struct BundleReader {
    pub bundle: Arc<Bundle>,
    /// Whether slot reads go through the bundle's negative slot filter (`GetSlotFiltered`).
    pub filtered: bool,
}

impl PersistenceReader for BundleReader {
    type Iter = ErasedIter;

    fn get_account(&self, address: &Address) -> PersistenceResult<Option<Account>> {
        Ok(self.bundle.get_account(address)?.0)
    }

    fn try_get_slot(&self, address: &Address, slot: &U256) -> PersistenceResult<Option<U256>> {
        self.bundle.try_get_slot(address, slot, self.filtered)
    }

    fn current_state(&self) -> StateId {
        self.bundle.reader_state()
    }

    fn load_state_rlp<T>(
        &self,
        path: &TreePath,
        flags: ReadFlags,
        read: impl FnOnce(&[u8]) -> T,
    ) -> PersistenceResult<Option<T>> {
        Ok(self.bundle.load_state_rlp_unchecked(path, flags, read)?.0)
    }

    fn load_storage_rlp<T>(
        &self,
        address_hash: &Hash256,
        path: &TreePath,
        flags: ReadFlags,
        read: impl FnOnce(&[u8]) -> T,
    ) -> PersistenceResult<Option<T>> {
        Ok(self
            .bundle
            .load_storage_rlp_unchecked(address_hash, path, flags, read)?
            .0)
    }

    fn get_account_raw(&self, address_hash: &Hash256) -> PersistenceResult<Option<Vec<u8>>> {
        match self.bundle.persistence_reader() {
            Some(reader) => reader.get_account_raw(address_hash),
            None => NoopPersistenceReader.get_account_raw(address_hash),
        }
    }

    fn try_get_storage_raw(&self, address_hash: &Hash256, slot_hash: &Hash256) -> PersistenceResult<Option<U256>> {
        match self.bundle.persistence_reader() {
            Some(reader) => reader.try_get_storage_raw(address_hash, slot_hash),
            None => NoopPersistenceReader.try_get_storage_raw(address_hash, slot_hash),
        }
    }

    fn create_account_iterator(&self, start_key: &Hash256, end_key: &Hash256) -> PersistenceResult<ErasedIter> {
        match self.bundle.persistence_reader() {
            Some(reader) => reader.create_account_iterator(start_key, end_key),
            None => Ok(ErasedIter(Box::new(EmptyIterator))),
        }
    }

    fn create_storage_iterator(
        &self,
        account_key: &Hash256,
        start_slot_key: &Hash256,
        end_slot_key: &Hash256,
    ) -> PersistenceResult<ErasedIter> {
        match self.bundle.persistence_reader() {
            Some(reader) => reader.create_storage_iterator(account_key, start_slot_key, end_slot_key),
            None => Ok(ErasedIter(Box::new(EmptyIterator))),
        }
    }

    fn is_preimage_mode(&self) -> bool {
        self.bundle
            .persistence_reader()
            .is_some_and(PersistenceReader::is_preimage_mode)
    }
}

/// The status of a refused `AddSnapshot`.
pub fn refused(status: FdbStatus, to: &StateId) -> FfiError {
    FfiError {
        status,
        message: format!("State {to:?} was not added"),
    }
}
