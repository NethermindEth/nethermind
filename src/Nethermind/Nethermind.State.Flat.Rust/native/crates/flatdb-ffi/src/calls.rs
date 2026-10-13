//! The exports [`crate::guard::ffi_guard`] counts, in the [`flatdb_core::seam`] counters after the bundle reads.

use flatdb_core::seam::{self, BundleRead};

/// An export of the C ABI, as counted by `fdb_seam_counters`; the C# mirror is `SeamCounter`.
#[repr(usize)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Export {
    StoreCreate,
    StoreDestroy,
    StoreTakeStats,
    StoreFlush,
    StoreClear,
    StoreLogTake,
    ReaderOpen,
    ReaderRelease,
    ReaderCurrentState,
    ReaderIsPreimageMode,
    ReaderGetAccount,
    ReaderGetSlot,
    ReaderLoadStateRlp,
    ReaderLoadStorageRlp,
    ReaderGetAccountRaw,
    ReaderGetStorageRaw,
    ReaderAccountIter,
    ReaderStorageIter,
    IterNext,
    IterRelease,
    BatchOpen,
    BatchApply,
    BatchCommit,
    BatchRelease,
    MgrCreate,
    SignalExit,
    Shutdown,
    Destroy,
    DrainObservations,
    TakePersistedEvent,
    PersistedEventRelease,
    BldNew,
    BldAppend,
    BldAbsorb,
    BldRelease,
    AddSnapshot,
    QueueCompaction,
    Gather,
    HasState,
    PersistedState,
    ResetPersisted,
    AddToPersistence,
    FlushToPersistence,
    DropUnreachable,
    TakeFatalError,
    ClearBundleCache,
    /// Every other export run through the guard: metadata, the seam counters themselves and the test exports.
    Other,
}

impl Export {
    pub const COUNT: usize = Export::Other as usize + 1;
    pub const NAMES: [&str; Self::COUNT] = [
        "store_create",
        "store_destroy",
        "store_take_stats",
        "store_flush",
        "store_clear",
        "store_log_take",
        "reader_open",
        "reader_release",
        "reader_current_state",
        "reader_is_preimage_mode",
        "reader_get_account",
        "reader_get_slot",
        "reader_load_state_rlp",
        "reader_load_storage_rlp",
        "reader_get_account_raw",
        "reader_get_storage_raw",
        "reader_account_iter",
        "reader_storage_iter",
        "iter_next",
        "iter_release",
        "batch_open",
        "batch_apply",
        "batch_commit",
        "batch_release",
        "mgr_create",
        "signal_exit",
        "shutdown",
        "destroy",
        "drain_observations",
        "take_persisted_event",
        "persisted_event_release",
        "bld_new",
        "bld_append",
        "bld_absorb",
        "bld_release",
        "add_snapshot",
        "queue_compaction",
        "gather",
        "has_state",
        "persisted_state",
        "reset_persisted",
        "add_to_persistence",
        "flush_to_persistence",
        "drop_unreachable",
        "take_fatal_error",
        "clear_bundle_cache",
        "other",
    ];

    /// The index of the export's counter.
    pub const fn counter(self) -> usize {
        BundleRead::COUNT + self as usize
    }
}

/// The counters in use: the bundle reads, then the exports.
pub const COUNTER_COUNT: usize = BundleRead::COUNT + Export::COUNT;

const _: () = assert!(COUNTER_COUNT <= seam::CAPACITY);

/// The name of counter `index`, or `None` past the counters in use.
pub fn counter_name(index: usize) -> Option<&'static str> {
    match index.checked_sub(BundleRead::COUNT) {
        None => Some(BundleRead::NAMES[index]),
        Some(export) => Export::NAMES.get(export).copied(),
    }
}
