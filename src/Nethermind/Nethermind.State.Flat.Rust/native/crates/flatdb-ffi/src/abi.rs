//! `repr(C)` types shared with the C# adapter (`Nethermind.State.Flat.Rust/Interop/FfiTypes.cs`).

use std::ffi::c_void;
use std::mem::offset_of;

use flatdb_core::host::{HostCallbacks, HostError};
use flatdb_core::kv::KvError;
use flatdb_core::kv::managed::ManagedKvVTable;
use flatdb_core::kv::rocks_api::RocksApi;
use flatdb_core::manager::ManagerError;
use flatdb_core::persistence::PersistenceError;
use flatdb_core::persistence_manager::PersistError;
use flatdb_core::stats::{FlatCounters, Observation};
use flatdb_core::types::{Account, EMPTY_TREE_HASH, Hash256, KECCAK_OF_EMPTY_STRING, StateId, U256};

/// Status code returned by every fallible export.
#[repr(i32)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum FdbStatus {
    Ok = 0,
    StateNotRetained = 1,
    GatherTimeout = 2,
    Cancelled = 3,
    ShutDown = 4,
    Panic = 5,
    RocksDb = 6,
    Rejected = 7,
    Duplicate = 8,
    WrongBaseState = 9,
    BufferTooSmall = 10,
    Poisoned = 11,
    InvalidArgument = 12,
    NotSupported = 13,
    /// The operation is not available in this mode (`InvalidOperationException`).
    InvalidOperation = 14,
    /// Malformed RLP (`RlpException`).
    Rlp = 15,
    /// The database does not match the configuration or is corrupt (`InvalidConfigurationException`).
    InvalidConfiguration = 16,
    /// A callback of the managed key-value backend failed; C# keeps the exception it threw.
    ManagedCallback = 17,
}

/// A failed export: the status it returns and the message `fdb_last_error` reports.
#[derive(Debug)]
pub struct FfiError {
    pub status: FdbStatus,
    pub message: String,
}

impl FfiError {
    pub fn invalid_argument(message: impl Into<String>) -> Self {
        Self {
            status: FdbStatus::InvalidArgument,
            message: message.into(),
        }
    }
}

impl From<KvError> for FfiError {
    fn from(error: KvError) -> Self {
        let status = match error {
            KvError::RocksDb { .. } => FdbStatus::RocksDb,
            KvError::Callback { .. } => FdbStatus::ManagedCallback,
            KvError::NotSupported(_) => FdbStatus::NotSupported,
            KvError::InvalidArgument(_) => FdbStatus::InvalidArgument,
        };
        Self {
            status,
            message: error.to_string(),
        }
    }
}

impl From<PersistenceError> for FfiError {
    fn from(error: PersistenceError) -> Self {
        let status = match error {
            PersistenceError::Kv(error) => return error.into(),
            PersistenceError::Rlp(_) => FdbStatus::Rlp,
            PersistenceError::InvalidConfiguration(_) => FdbStatus::InvalidConfiguration,
            PersistenceError::NotSupported(_) => FdbStatus::NotSupported,
            PersistenceError::InvalidOperation(_) => FdbStatus::InvalidOperation,
            PersistenceError::WrongBaseState { .. } => FdbStatus::WrongBaseState,
            PersistenceError::ValueTooLarge { .. } => FdbStatus::InvalidArgument,
        };
        Self {
            status,
            message: error.to_string(),
        }
    }
}

impl From<ManagerError> for FfiError {
    fn from(error: ManagerError) -> Self {
        let status = match &error {
            ManagerError::Persistence(error) => return error.clone().into(),
            ManagerError::Persist(error) => return error.clone().into(),
            ManagerError::StateNotRetained(_) => FdbStatus::StateNotRetained,
            ManagerError::GatherTimeout { .. } => FdbStatus::GatherTimeout,
            ManagerError::ShutDown => FdbStatus::ShutDown,
            ManagerError::Cancelled => FdbStatus::Cancelled,
        };
        Self {
            status,
            message: error.to_string(),
        }
    }
}

impl From<PersistError> for FfiError {
    fn from(error: PersistError) -> Self {
        let status = match &error {
            PersistError::Persistence(error) => return error.clone().into(),
            PersistError::Host(HostError::Cancelled) => FdbStatus::Cancelled,
            PersistError::Host(HostError::Failed(_)) => FdbStatus::InvalidOperation,
        };
        Self {
            status,
            message: error.to_string(),
        }
    }
}

/// Store settings: the persistence stack `FlatWorldStateModule` builds, that is the layout persistence, then the
/// shared reader cache, then optionally the carry-forward cache.
#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct FdbStoreCfg {
    /// Must equal `fdb_abi_version()`.
    pub abi_version: u32,
    /// `Nethermind.Db.FlatLayout`.
    pub layout: u32,
    /// `IFlatDbConfig.PersistenceWriteBufferFloor`; only the Flat layout applies it, as only `RocksDbPersistence`
    /// takes the config.
    pub write_buffer_floor: i64,
    /// `IFlatDbConfig.EnableCarryForwardCache`.
    pub carry_forward: u8,
    /// The most verbose level the store's log sink keeps, numbered as `fdb_store_log_take` reports levels: 0 off,
    /// 1 error, 2 warn, 3 info, 4 debug, 5 trace.
    pub log_level: u8,
}

/// What a store resolved from its database when it was created.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct FdbStoreInfo {
    /// Whether slot values are RLP-wrapped rather than in the deprecated raw encoding.
    pub rlp_wrap_slots: u8,
    /// Whether the layout keys flat state by address and slot rather than their hashes.
    pub preimage: u8,
}

/// The metrics a store gathered since they were last taken, for C# to add to its own.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct FdbStats {
    /// Reads `DbOnTheRocks` would have counted in `TotalReads`.
    pub reads: u64,
    /// Increments of `Metrics.CarryForwardAccountHits` and the five counters after it.
    pub carry_forward_account_hits: i64,
    pub carry_forward_account_misses: i64,
    pub carry_forward_slot_hits: i64,
    pub carry_forward_slot_misses: i64,
    pub carry_forward_account_wipes: i64,
    pub carry_forward_slot_evictions: i64,
    /// Current values of the `CarryForwardAccountCount` and `CarryForwardSlotCount` gauges.
    pub carry_forward_account_count: i64,
    pub carry_forward_slot_count: i64,
}

/// A persisted state, as `StateId`.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct FfiStateId {
    pub block_number: u64,
    pub state_root: Hash256,
}

impl From<StateId> for FfiStateId {
    fn from(state: StateId) -> Self {
        Self {
            block_number: state.block_number,
            state_root: state.state_root,
        }
    }
}

impl From<FfiStateId> for StateId {
    fn from(state: FfiStateId) -> Self {
        StateId::new(state.block_number, state.state_root)
    }
}

/// [`FfiAccount::flags`]: the storage root is the empty tree hash.
pub const ACCOUNT_EMPTY_STORAGE_ROOT: u32 = 1;
/// [`FfiAccount::flags`]: the code hash is the hash of empty code.
pub const ACCOUNT_EMPTY_CODE_HASH: u32 = 2;

/// An account. The flags mark the empty storage root and code hash, so C# reuses its static hashes for them.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct FfiAccount {
    pub nonce: u64,
    /// Little-endian limbs, as `UInt256` holds them.
    pub balance: [u64; 4],
    pub storage_root: Hash256,
    pub code_hash: Hash256,
    pub flags: u32,
}

impl From<&Account> for FfiAccount {
    fn from(account: &Account) -> Self {
        let mut flags = 0;
        if account.storage_root == EMPTY_TREE_HASH {
            flags |= ACCOUNT_EMPTY_STORAGE_ROOT;
        }
        if account.code_hash == KECCAK_OF_EMPTY_STRING {
            flags |= ACCOUNT_EMPTY_CODE_HASH;
        }
        Self {
            nonce: account.nonce,
            balance: account.balance.0,
            storage_root: account.storage_root,
            code_hash: account.code_hash,
            flags,
        }
    }
}

impl From<&FfiAccount> for Account {
    fn from(account: &FfiAccount) -> Self {
        Account::with_hashes(
            account.nonce,
            U256(account.balance),
            account.storage_root,
            account.code_hash,
        )
    }
}

/// Which backend an [`FdbKvDesc`] describes.
#[repr(u32)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum FdbKvKind {
    /// The RocksDB instance of a C# `ColumnsDb<FlatDbColumns>`.
    Rocks = 0,
    /// Callbacks into a C# `IColumnsDb<FlatDbColumns>`.
    Managed = 1,
    /// A fresh in-memory store; only in builds with the `test-api` feature, and in the crate's own tests.
    Mem = 2,
}

/// The key-value store a new store runs on. Only the fields of the selected kind are read.
#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct FdbKvDesc {
    /// An [`FdbKvKind`].
    pub kind: u32,
    /// Number of entries of `rocks_column_families`; must be the number of flat-DB columns.
    pub column_count: u32,
    pub rocks_api: *const RocksApi,
    /// `rocksdb_t*`.
    pub rocks_db: *mut c_void,
    /// `rocksdb_column_family_handle_t*` of every column, in `FlatDbColumns` order.
    pub rocks_column_families: *const *mut c_void,
    pub readahead_size: u64,
    /// `DbOnTheRocks.PrefixExtractorLength`: 0 for none, `i32::MAX` for an extractor of unknown width.
    pub prefix_extractor_length: u32,
    pub verify_checksums: u8,
    pub wal_sync: u8,
    pub managed: *const ManagedKvVTable,
}

/// Snapshot manager settings: the `IFlatDbConfig` and `IBlocksConfig` values `FlatDbManager`, `PersistenceManager`,
/// `SnapshotCompactor` and `CompactionSchedule` read, with the compaction offset C# resolved.
#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct FdbMgrCfg {
    /// Must equal `fdb_abi_version()`.
    pub abi_version: u32,
    /// `MaxInFlightCompactJob`.
    pub max_in_flight_compact_job: u32,
    /// `CompactSize`.
    pub compact_size: u64,
    /// `PersistedSnapshotMaxCompactSize`.
    pub persisted_snapshot_max_compact_size: u64,
    /// `CompactionSchedule.Offset`.
    pub compaction_offset: u64,
    /// `MinReorgDepth`.
    pub min_reorg_depth: u64,
    /// `MaxReorgDepth`.
    pub max_reorg_depth: u64,
    /// `MaxInMemorySnapshotBytes`.
    pub max_in_memory_snapshot_bytes: u64,
    /// `IBlocksConfig.SecondsPerSlot`.
    pub seconds_per_slot: u64,
    /// `InMemorySnapshotBloomBitsPerKey`.
    pub in_memory_slot_filter_bits_per_key: f64,
    /// Histogram observations queued for `fdb_drain_observations`; 0 records none, as with detailed metrics off.
    pub observation_capacity: u32,
    /// `InlineCompaction`.
    pub inline_compaction: u8,
    /// Whether every persist queues its trie nodes for `fdb_take_persisted_event`.
    pub persisted_node_events: u8,
    /// `Db.Metrics.DetailedMetricsEnabled`, which decides whether the readers of the manager count carry-forward hits
    /// and misses.
    pub detailed_metrics: u8,
}

/// The host callbacks of a manager, `FdbHost`: [`HostCallbacks`] with every entry checked for null.
#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct FdbHost {
    pub ctx: usize,
    pub finalized_block_number: Option<extern "C" fn(ctx: usize) -> u64>,
    pub finalized_state_root: Option<extern "C" fn(ctx: usize, block_number: u64, root: &mut Hash256) -> u8>,
    pub flush_deferred: Option<extern "C" fn(ctx: usize) -> i32>,
}

impl TryFrom<&FdbHost> for HostCallbacks {
    type Error = FfiError;

    fn try_from(host: &FdbHost) -> Result<Self, FfiError> {
        let (Some(finalized_block_number), Some(finalized_state_root), Some(flush_deferred)) = (
            host.finalized_block_number,
            host.finalized_state_root,
            host.flush_deferred,
        ) else {
            return Err(FfiError::invalid_argument("the host lacks a callback"));
        };
        Ok(HostCallbacks {
            ctx: host.ctx,
            finalized_block_number,
            finalized_state_root,
            flush_deferred,
        })
    }
}

/// [`FfiPersistedNode::is_storage`]: the node is a storage trie node of the account `address_hash` is the path of.
pub const PERSISTED_STORAGE_NODE: u8 = 1;

/// A trie node a persist wrote, for the C# replay of `IsPersisted`.
#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct FfiPersistedNode {
    /// The account path of a storage trie node; zero for a state trie node.
    pub address_hash: Hash256,
    pub path: Hash256,
    /// The hash the node was added with.
    pub hash: Hash256,
    pub path_length: u8,
    /// [`PERSISTED_STORAGE_NODE`] or 0.
    pub is_storage: u8,
}

/// The nodes of one persist, `fdb_take_persisted_event`'s record. `owner` keeps `nodes` alive until released with
/// `fdb_persisted_event_release`; it is null when no persist was queued.
#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct FdbPersistedEvent {
    pub from: FfiStateId,
    pub to: FfiStateId,
    pub nodes: *const FfiPersistedNode,
    pub node_count: usize,
    pub owner: *mut c_void,
}

/// Size of every `repr(C)` type, in the order `fdb_struct_sizes` reports them.
pub const STRUCT_SIZES: [u32; 14] = [
    size_of::<FdbStoreCfg>() as u32,
    size_of::<FdbKvDesc>() as u32,
    size_of::<RocksApi>() as u32,
    size_of::<ManagedKvVTable>() as u32,
    size_of::<FdbStoreInfo>() as u32,
    size_of::<FdbStats>() as u32,
    size_of::<FfiStateId>() as u32,
    size_of::<FfiAccount>() as u32,
    size_of::<FdbMgrCfg>() as u32,
    size_of::<FdbHost>() as u32,
    size_of::<FlatCounters>() as u32,
    size_of::<Observation>() as u32,
    size_of::<FfiPersistedNode>() as u32,
    size_of::<FdbPersistedEvent>() as u32,
];

/// Offset of every field of the `repr(C)` types with several fields, in declaration order, as `fdb_struct_offsets`
/// reports them. With [`STRUCT_SIZES`] it pins the layouts the C# mirrors must match.
pub const FIELD_OFFSETS: [u32; 86] = [
    offset_of!(FdbStoreCfg, abi_version) as u32,
    offset_of!(FdbStoreCfg, layout) as u32,
    offset_of!(FdbStoreCfg, write_buffer_floor) as u32,
    offset_of!(FdbStoreCfg, carry_forward) as u32,
    offset_of!(FdbStoreCfg, log_level) as u32,
    offset_of!(FdbKvDesc, kind) as u32,
    offset_of!(FdbKvDesc, column_count) as u32,
    offset_of!(FdbKvDesc, rocks_api) as u32,
    offset_of!(FdbKvDesc, rocks_db) as u32,
    offset_of!(FdbKvDesc, rocks_column_families) as u32,
    offset_of!(FdbKvDesc, readahead_size) as u32,
    offset_of!(FdbKvDesc, prefix_extractor_length) as u32,
    offset_of!(FdbKvDesc, verify_checksums) as u32,
    offset_of!(FdbKvDesc, wal_sync) as u32,
    offset_of!(FdbKvDesc, managed) as u32,
    offset_of!(ManagedKvVTable, ctx) as u32,
    offset_of!(ManagedKvVTable, get) as u32,
    offset_of!(ManagedKvVTable, snapshot_create) as u32,
    offset_of!(ManagedKvVTable, snapshot_release) as u32,
    offset_of!(ManagedKvVTable, iterator_create) as u32,
    offset_of!(ManagedKvVTable, iterator_next) as u32,
    offset_of!(ManagedKvVTable, iterator_release) as u32,
    offset_of!(ManagedKvVTable, write) as u32,
    offset_of!(ManagedKvVTable, flush) as u32,
    offset_of!(ManagedKvVTable, set_write_buffer) as u32,
    offset_of!(FdbStoreInfo, rlp_wrap_slots) as u32,
    offset_of!(FdbStoreInfo, preimage) as u32,
    offset_of!(FdbStats, reads) as u32,
    offset_of!(FdbStats, carry_forward_account_hits) as u32,
    offset_of!(FdbStats, carry_forward_account_misses) as u32,
    offset_of!(FdbStats, carry_forward_slot_hits) as u32,
    offset_of!(FdbStats, carry_forward_slot_misses) as u32,
    offset_of!(FdbStats, carry_forward_account_wipes) as u32,
    offset_of!(FdbStats, carry_forward_slot_evictions) as u32,
    offset_of!(FdbStats, carry_forward_account_count) as u32,
    offset_of!(FdbStats, carry_forward_slot_count) as u32,
    offset_of!(FfiStateId, block_number) as u32,
    offset_of!(FfiStateId, state_root) as u32,
    offset_of!(FfiAccount, nonce) as u32,
    offset_of!(FfiAccount, balance) as u32,
    offset_of!(FfiAccount, storage_root) as u32,
    offset_of!(FfiAccount, code_hash) as u32,
    offset_of!(FfiAccount, flags) as u32,
    offset_of!(FdbMgrCfg, abi_version) as u32,
    offset_of!(FdbMgrCfg, max_in_flight_compact_job) as u32,
    offset_of!(FdbMgrCfg, compact_size) as u32,
    offset_of!(FdbMgrCfg, persisted_snapshot_max_compact_size) as u32,
    offset_of!(FdbMgrCfg, compaction_offset) as u32,
    offset_of!(FdbMgrCfg, min_reorg_depth) as u32,
    offset_of!(FdbMgrCfg, max_reorg_depth) as u32,
    offset_of!(FdbMgrCfg, max_in_memory_snapshot_bytes) as u32,
    offset_of!(FdbMgrCfg, seconds_per_slot) as u32,
    offset_of!(FdbMgrCfg, in_memory_slot_filter_bits_per_key) as u32,
    offset_of!(FdbMgrCfg, observation_capacity) as u32,
    offset_of!(FdbMgrCfg, inline_compaction) as u32,
    offset_of!(FdbMgrCfg, persisted_node_events) as u32,
    offset_of!(FdbMgrCfg, detailed_metrics) as u32,
    offset_of!(FdbHost, ctx) as u32,
    offset_of!(FdbHost, finalized_block_number) as u32,
    offset_of!(FdbHost, finalized_state_root) as u32,
    offset_of!(FdbHost, flush_deferred) as u32,
    offset_of!(FlatCounters, snapshot_count) as u32,
    offset_of!(FlatCounters, compacted_snapshot_count) as u32,
    offset_of!(FlatCounters, snapshot_memory) as u32,
    offset_of!(FlatCounters, compacted_snapshot_memory) as u32,
    offset_of!(FlatCounters, total_snapshot_memory) as u32,
    offset_of!(FlatCounters, snapshot_bundle_size) as u32,
    offset_of!(FlatCounters, snapshot_bundle_persisted_snapshot_size) as u32,
    offset_of!(FlatCounters, snapshot_bundle_persisted_snapshot_memory) as u32,
    offset_of!(FlatCounters, in_memory_slot_filter_memory) as u32,
    offset_of!(FlatCounters, in_memory_slot_filter_builds) as u32,
    offset_of!(FlatCounters, in_memory_slot_filter_build_failures) as u32,
    offset_of!(FlatCounters, observations_dropped) as u32,
    offset_of!(Observation, metric) as u32,
    offset_of!(Observation, label) as u32,
    offset_of!(Observation, value) as u32,
    offset_of!(FfiPersistedNode, address_hash) as u32,
    offset_of!(FfiPersistedNode, path) as u32,
    offset_of!(FfiPersistedNode, hash) as u32,
    offset_of!(FfiPersistedNode, path_length) as u32,
    offset_of!(FfiPersistedNode, is_storage) as u32,
    offset_of!(FdbPersistedEvent, from) as u32,
    offset_of!(FdbPersistedEvent, to) as u32,
    offset_of!(FdbPersistedEvent, nodes) as u32,
    offset_of!(FdbPersistedEvent, node_count) as u32,
    offset_of!(FdbPersistedEvent, owner) as u32,
];
