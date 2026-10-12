//! `repr(C)` types shared with the C# adapter (`Nethermind.State.Flat.Rust/Interop/FfiTypes.cs`).

use std::ffi::c_void;
use std::mem::offset_of;

use flatdb_core::kv::KvError;
use flatdb_core::kv::managed::ManagedKvVTable;
use flatdb_core::kv::rocks_api::RocksApi;

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
            KvError::NotSupported(_) => FdbStatus::NotSupported,
            KvError::InvalidArgument(_) => FdbStatus::InvalidArgument,
        };
        Self {
            status,
            message: error.to_string(),
        }
    }
}

/// Store settings.
#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct FdbStoreCfg {
    /// Must equal `fdb_abi_version()`.
    pub abi_version: u32,
    /// `Nethermind.Db.FlatLayout`.
    pub layout: u32,
}

/// Which backend an [`FdbKvDesc`] describes.
#[repr(u32)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum FdbKvKind {
    /// The RocksDB instance of a C# `ColumnsDb<FlatDbColumns>`.
    Rocks = 0,
    /// Callbacks into a C# `IColumnsDb<FlatDbColumns>`.
    Managed = 1,
    /// A fresh in-memory store.
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

/// Size of every `repr(C)` type, in the order `fdb_struct_sizes` reports them.
pub const STRUCT_SIZES: [u32; 4] = [
    size_of::<FdbStoreCfg>() as u32,
    size_of::<FdbKvDesc>() as u32,
    size_of::<RocksApi>() as u32,
    size_of::<ManagedKvVTable>() as u32,
];

/// Offset of every field of [`FdbStoreCfg`], [`FdbKvDesc`] and [`ManagedKvVTable`], in declaration order, as
/// `fdb_struct_offsets` reports them. With [`STRUCT_SIZES`] it pins the layouts the C# mirrors must match.
pub const FIELD_OFFSETS: [u32; 21] = [
    offset_of!(FdbStoreCfg, abi_version) as u32,
    offset_of!(FdbStoreCfg, layout) as u32,
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
];
