//! The store handle and its construction from an [`FdbKvDesc`], and the reader, iterator and batch handles created
//! from it.

use std::slice;
use std::sync::Arc;
use std::sync::atomic::Ordering;

use flatdb_core::ABI_VERSION;
use flatdb_core::kv::managed::ManagedKv;
#[cfg(any(test, feature = "test-api"))]
use flatdb_core::kv::mem::MemKv;
use flatdb_core::kv::rocks::{RocksDesc, RocksKv};
use flatdb_core::kv::{Column, KvStore};
use flatdb_core::log_ring::LogSink;
use flatdb_core::persistence::FlatIterator;
use flatdb_core::persistence::cached_reader::CachedReaderPersistence;
use flatdb_core::persistence::carry_forward::{CarryForwardCachingPersistence, CarryForwardMetrics};
use flatdb_core::persistence::layouts::KvPersistence;
use flatdb_core::types::FlatLayout;

use log::LevelFilter;

use crate::abi::{FdbKvDesc, FdbKvKind, FdbStats, FdbStoreCfg, FdbStoreInfo, FfiError};
use crate::guard::Poison;
use crate::persistence::{DynPersistence, DynReader, DynWriteBatch};

/// The backend of a store.
pub enum AnyKv {
    Rocks(RocksKv),
    #[cfg(any(test, feature = "test-api"))]
    Mem(MemKv),
    Managed(ManagedKv),
}

impl AnyKv {
    fn take_read_count(&self) -> u64 {
        match self {
            AnyKv::Rocks(kv) => kv.take_read_count(),
            #[cfg(any(test, feature = "test-api"))]
            AnyKv::Mem(kv) => kv.take_read_count(),
            AnyKv::Managed(kv) => kv.take_read_count(),
        }
    }
}

/// Runs `$body` with `$kv` bound to the concrete backend of `$store`, so the body is monomorphized per backend.
#[cfg(any(test, feature = "test-api"))]
macro_rules! with_kv {
    ($store:expr, $kv:ident => $body:expr) => {
        match &$store.kv {
            $crate::store::AnyKv::Rocks($kv) => $body,
            $crate::store::AnyKv::Mem($kv) => $body,
            $crate::store::AnyKv::Managed($kv) => $body,
        }
    };
}
#[cfg(any(test, feature = "test-api"))]
pub(crate) use with_kv;

/// A store, handed to C# as an opaque pointer: the persistence stack `FlatWorldStateModule` builds, over a key-value
/// backend. The snapshot manager created over it shares the stack.
pub struct FdbStore {
    /// The backend, for the read count and the debug exports; the persistence holds its own clone.
    pub kv: AnyKv,
    pub persistence: Arc<dyn DynPersistence>,
    pub info: FdbStoreInfo,
    pub carry_forward_metrics: Option<Arc<CarryForwardMetrics>>,
    /// The poison flag and the log sink, cloned by every open reader, iterator and batch, which the store must outlive.
    pub poison: Arc<Poison>,
}

impl FdbStore {
    /// # Safety
    /// The pointers of `desc` must satisfy [`RocksKv::new`] for [`FdbKvKind::Rocks`] and [`ManagedKv::new`] for
    /// [`FdbKvKind::Managed`].
    pub unsafe fn create(cfg: &FdbStoreCfg, desc: &FdbKvDesc) -> Result<Self, FfiError> {
        if cfg.abi_version != ABI_VERSION {
            return Err(FfiError::invalid_argument(format!(
                "the adapter speaks ABI version {}, the library {ABI_VERSION}",
                cfg.abi_version
            )));
        }
        let layout = u8::try_from(cfg.layout)
            .ok()
            .and_then(FlatLayout::from_u8)
            .ok_or_else(|| FfiError::invalid_argument(format!("unknown flat layout {}", cfg.layout)))?;
        let poison = Arc::new(Poison::new(LogSink::new(level_filter(cfg.log_level)?)));
        // Resolving the slot encoding of the database can warn already.
        // SAFETY: as the caller vouches.
        poison
            .log
            .scope(|| unsafe { Self::create_in(cfg, desc, layout, poison.clone()) })
    }

    /// # Safety
    /// As [`FdbStore::create`].
    unsafe fn create_in(
        cfg: &FdbStoreCfg,
        desc: &FdbKvDesc,
        layout: FlatLayout,
        poison: Arc<Poison>,
    ) -> Result<Self, FfiError> {
        let kv = match desc.kind {
            // SAFETY: the caller vouches for the RocksDB handles of the descriptor.
            kind if kind == FdbKvKind::Rocks as u32 => AnyKv::Rocks(unsafe { create_rocks(desc) }?),
            #[cfg(any(test, feature = "test-api"))]
            kind if kind == FdbKvKind::Mem as u32 => AnyKv::Mem(MemKv::new()),
            kind if kind == FdbKvKind::Managed as u32 => {
                // SAFETY: the caller passes a valid descriptor.
                let vtable = unsafe { desc.managed.as_ref() }
                    .ok_or_else(|| FfiError::invalid_argument("null managed key-value callbacks"))?;
                // SAFETY: the caller vouches for the callbacks and keeps their context alive with the store.
                AnyKv::Managed(unsafe { ManagedKv::new(vtable) }?)
            }
            kind => return Err(FfiError::invalid_argument(format!("unknown key-value backend {kind}"))),
        };

        let stack = match &kv {
            AnyKv::Rocks(kv) => Stack::build(instrument(kv.clone(), &poison), layout, cfg)?,
            #[cfg(any(test, feature = "test-api"))]
            AnyKv::Mem(kv) => Stack::build(instrument(kv.clone(), &poison), layout, cfg)?,
            AnyKv::Managed(kv) => Stack::build(instrument(kv.clone(), &poison), layout, cfg)?,
        };

        Ok(Self {
            kv,
            persistence: stack.persistence,
            info: FdbStoreInfo {
                rlp_wrap_slots: stack.rlp_wrap_slots.into(),
                preimage: layout.is_preimage().into(),
            },
            carry_forward_metrics: stack.carry_forward_metrics,
            poison,
        })
    }

    /// Whether a reader, iterator or batch of the store is still open.
    pub fn has_open_handles(&self) -> bool {
        Arc::strong_count(&self.poison) > 1
    }

    /// Takes the reads and carry-forward counters gathered since the last call, with the current gauges.
    pub fn take_stats(&self) -> FdbStats {
        let mut stats = FdbStats {
            reads: self.kv.take_read_count(),
            ..FdbStats::default()
        };
        if let Some(metrics) = &self.carry_forward_metrics {
            let take = |counter: &std::sync::atomic::AtomicI64| counter.swap(0, Ordering::Relaxed);
            stats.carry_forward_account_hits = take(&metrics.account_hits);
            stats.carry_forward_account_misses = take(&metrics.account_misses);
            stats.carry_forward_slot_hits = take(&metrics.slot_hits);
            stats.carry_forward_slot_misses = take(&metrics.slot_misses);
            stats.carry_forward_account_wipes = take(&metrics.account_wipes);
            stats.carry_forward_slot_evictions = take(&metrics.slot_evictions);
            stats.carry_forward_account_count = metrics.account_count.load(Ordering::Relaxed);
            stats.carry_forward_slot_count = metrics.slot_count.load(Ordering::Relaxed);
        }
        stats
    }
}

/// The backend the persistence stack runs on: the store's own, which test builds route through its armed faults.
#[cfg(any(test, feature = "test-api"))]
fn instrument<K: KvStore>(kv: K, poison: &Poison) -> crate::faults::FaultyKv<K> {
    crate::faults::FaultyKv::new(kv, Arc::clone(&poison.faults))
}

/// The backend the persistence stack runs on: the store's own, which test builds route through its armed faults.
#[cfg(not(any(test, feature = "test-api")))]
fn instrument<K: KvStore>(kv: K, _poison: &Poison) -> K {
    kv
}

/// The level filter of a log level as the C ABI numbers it.
pub fn level_filter(level: u8) -> Result<LevelFilter, FfiError> {
    Ok(match level {
        0 => LevelFilter::Off,
        1 => LevelFilter::Error,
        2 => LevelFilter::Warn,
        3 => LevelFilter::Info,
        4 => LevelFilter::Debug,
        5 => LevelFilter::Trace,
        level => return Err(FfiError::invalid_argument(format!("unknown log level {level}"))),
    })
}

/// The persistence stack over one backend.
struct Stack {
    persistence: Arc<dyn DynPersistence>,
    rlp_wrap_slots: bool,
    carry_forward_metrics: Option<Arc<CarryForwardMetrics>>,
}

impl Stack {
    /// Builds the stack `FlatWorldStateModule` registers as `IPersistence`: the layout persistence, then
    /// `CachedReaderPersistence`, then `CarryForwardCachingPersistence` when the carry-forward cache is enabled.
    fn build<K: KvStore>(kv: K, layout: FlatLayout, cfg: &FdbStoreCfg) -> Result<Self, FfiError> {
        let persistence = KvPersistence::new(kv, layout, Some(cfg.write_buffer_floor))?;
        let rlp_wrap_slots = persistence.rlp_wrap_slots();
        let cached = CachedReaderPersistence::new(persistence)?;
        if cfg.carry_forward == 0 {
            return Ok(Self {
                persistence: Arc::new(cached),
                rlp_wrap_slots,
                carry_forward_metrics: None,
            });
        }

        let carry_forward = CarryForwardCachingPersistence::new(cached)?;
        Ok(Self {
            carry_forward_metrics: Some(carry_forward.metrics().clone()),
            persistence: Arc::new(carry_forward),
            rlp_wrap_slots,
        })
    }
}

/// # Safety
/// As [`FdbStore::create`].
unsafe fn create_rocks(desc: &FdbKvDesc) -> Result<RocksKv, FfiError> {
    if desc.column_count as usize != Column::COUNT || desc.rocks_column_families.is_null() {
        return Err(FfiError::invalid_argument(format!(
            "expected {} column family handles, got {}",
            Column::COUNT,
            desc.column_count
        )));
    }

    // SAFETY: the caller passes a valid descriptor, whose handle array holds `column_count` entries.
    let api = unsafe { desc.rocks_api.as_ref() }.ok_or_else(|| FfiError::invalid_argument("null RocksApi"))?;
    // SAFETY: as above.
    let handles = unsafe { slice::from_raw_parts(desc.rocks_column_families, Column::COUNT) };
    let rocks = RocksDesc {
        db: desc.rocks_db,
        column_families: handles.try_into().expect("the length was checked"),
        verify_checksums: desc.verify_checksums != 0,
        wal_sync: desc.wal_sync != 0,
        readahead_size: desc.readahead_size,
        prefix_extractor_length: desc.prefix_extractor_length,
    };

    // SAFETY: the caller keeps the database open while the store lives.
    Ok(unsafe { RocksKv::new(api, rocks) }?)
}

/// A reader handle: a reader of the store's persistence, at the state it was opened at.
pub struct FdbReader {
    pub reader: Box<dyn DynReader>,
    /// The store's poison flag and handle count.
    pub poison: Arc<Poison>,
}

impl std::ops::Deref for FdbReader {
    type Target = dyn DynReader;

    fn deref(&self) -> &(dyn DynReader + 'static) {
        &*self.reader
    }
}

/// An iterator handle. It holds its own reference to the reader's snapshot, so it may outlive its reader.
pub struct FdbIter {
    pub iter: Box<dyn FlatIterator>,
    /// The store's poison flag and handle count.
    pub poison: Arc<Poison>,
}

/// A batch handle; `None` once committed.
pub struct FdbBatch {
    pub batch: Option<Box<dyn DynWriteBatch>>,
    /// The store's poison flag and handle count.
    pub poison: Arc<Poison>,
}

/// Writes one value in its own batch, for smoke tests of the shared database.
#[cfg(any(test, feature = "test-api"))]
pub fn debug_put<K: KvStore>(kv: &K, column: Column, key: &[u8], value: &[u8]) -> Result<(), FfiError> {
    let mut batch = kv.write_batch()?;
    batch.put(column, key, value, flatdb_core::kv::WriteFlags::NONE)?;
    batch.commit()?;
    Ok(())
}
