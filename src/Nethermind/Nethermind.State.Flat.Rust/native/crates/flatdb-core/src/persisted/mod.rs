//! The long-finality persisted-snapshot tier: port of `Nethermind.State.Flat/PersistedSnapshots/`.
//!
//! A persisted snapshot is one [`sorted`] table of fully materialized [`keys`] mapped to small inline values; trie-node
//! RLP lives in blob files and the table holds a [`page_layout::NodeRef`] to it. [`builder`] writes a table from an
//! in-memory snapshot, [`merger`] merges several tables into one, [`reader`] and [`scanner`] read them back, and
//! [`stack`] probes a list of them newest first behind their blooms ([`bloom_keys`]). [`codec`] holds the catalog
//! records. Every byte written is identical to what the C# tier writes for the same input.
//!
//! [`snapshot`] is one persisted snapshot over its arena reservation and blob files, [`bucket`] the snapshots of one
//! tier, and [`loader`] converts in-memory snapshots into the tier ([`validate`] checking them on request) and opens the
//! tier again at startup. [`compactor`] merges the snapshots of the tier into wider ones on threads of its own.
//! [`tier`] wires them for one manager.
//!
//! The readers and writers work over plain byte slices and sinks ([`io`]), so the same code serves memory-mapped arena
//! files and the in-memory buffers of the tests. [`storage`] holds the files and the catalog, [`sys`] their system
//! calls, the tier's only raw memory and system access.

pub mod bloom_keys;
pub mod bucket;
pub mod builder;
pub mod codec;
pub mod compactor;
pub mod io;
pub mod keys;
pub mod list;
pub mod loader;
pub(crate) mod lock_rank;
pub mod merger;
pub mod page_layout;
pub mod parallel;
pub mod reader;
pub mod scanner;
pub mod snapshot;
pub mod sorted;
pub mod stack;
pub mod storage;
pub mod sys;
pub mod tier;
pub mod validate;

use thiserror::Error;

use crate::kv::KvError;
use crate::snapshot::Snapshot;

/// A failure of the persisted tier, named after the C# exception it stands for.
#[derive(Debug, Error)]
pub enum TierError {
    /// `IOException`: a file of the tier could not be read or written.
    #[error(transparent)]
    Io(#[from] std::io::Error),
    /// `InvalidOperationException` / `InvalidDataException` thrown on bytes no writer produced; the message is the C#
    /// one, which tells the operator to wipe the tier and resync.
    #[error("{0}")]
    Corrupt(String),
    /// `InvalidOperationException`: an operation the state of the tier does not allow.
    #[error("{0}")]
    InvalidOperation(String),
    /// `ArgumentException`: an argument the caller should never pass, such as keys out of order.
    #[error("{0}")]
    InvalidArgument(String),
    /// A failure of the catalog database, fatal when RocksDB reports corruption or an I/O error.
    #[error(transparent)]
    Catalog(#[from] KvError),
    /// A converted snapshot that does not read back as its source, with the C# message naming the dump file.
    #[error("{0}")]
    Validation(String),
}

pub type TierResult<T> = Result<T, TierError>;

/// The C# exception a [`TierError`] other than a catalog failure stands for, kept when it travels as a
/// [`crate::persistence::PersistenceError::Tier`].
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum TierErrorKind {
    Io,
    Corrupt,
    InvalidOperation,
    InvalidArgument,
    Validation,
}

/// The `IFlatDbConfig` members the loader reads, and the processor count C# sizes its parallel loops with.
#[derive(Clone, Copy, Debug)]
pub struct PersistedTierConfig {
    /// `PersistedSnapshotBloomBitsPerKey`: 0 gives every snapshot the always-true bloom.
    pub bloom_bits_per_key: f64,
    /// `ValidatePersistedSnapshot`: read every converted snapshot back against its source.
    pub validate_persisted_snapshot: bool,
    /// `Environment.ProcessorCount`.
    pub processor_count: usize,
}

/// Failures a test build of the library injects into the tier, the `test-api` faults of the FFI crate. The tier of a
/// production build has none, so each point costs a check of an empty `Option`.
pub trait TierFaults: Send + Sync {
    /// Runs before a conversion builds its table, and may panic.
    fn before_conversion(&self);
    /// The I/O error a conversion fails with once its table is written, before its writers complete, so they cancel.
    fn conversion_write_error(&self) -> Option<std::io::Error>;
    /// Whether a conversion stops once its files are durable, before its catalog row is written, as a crash there
    /// would: the reservation keeps its bytes and nothing is registered.
    fn stop_before_catalog_add(&self) -> bool;
    /// What a converted snapshot is validated against instead of its source.
    fn validation_source(&self, source: &Snapshot) -> Option<Snapshot>;
    /// Runs before a persisted compaction merges its sources, and may panic.
    fn before_merge(&self);
    /// Runs at startup once the snapshots of the catalog are opened, before the unreferenced blob files are swept, and
    /// may panic.
    fn after_load_open(&self);
}
