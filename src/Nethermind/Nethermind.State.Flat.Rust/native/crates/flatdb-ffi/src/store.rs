//! The store handle and its construction from an [`FdbKvDesc`].

use std::slice;
use std::sync::atomic::AtomicU64;

use flatdb_core::ABI_VERSION;
use flatdb_core::kv::managed::ManagedKv;
use flatdb_core::kv::mem::MemKv;
use flatdb_core::kv::rocks::{RocksDesc, RocksKv};
use flatdb_core::kv::{Column, KvStore, WriteFlags};

use crate::abi::{FdbKvDesc, FdbKvKind, FdbStoreCfg, FfiError};

/// The backend of a store.
pub enum AnyKv {
    Rocks(RocksKv),
    Mem(MemKv),
    Managed(ManagedKv),
}

impl AnyKv {
    /// Whether a reader, iterator or batch created from the backend is still alive.
    pub fn is_shared(&self) -> bool {
        match self {
            AnyKv::Rocks(kv) => kv.is_shared(),
            AnyKv::Mem(kv) => kv.is_shared(),
            AnyKv::Managed(_) => false,
        }
    }
}

/// A store, handed to C# as an opaque pointer.
pub struct FdbStore {
    pub kv: AnyKv,
    /// Database reads since C# last took the count, which it adds to the `TotalReads` metric of the database.
    pub read_count: AtomicU64,
}

/// Runs `$body` with `$kv` bound to the concrete backend of `$store`, so the body is monomorphized per backend.
macro_rules! with_kv {
    ($store:expr, $kv:ident => $body:expr) => {
        match &$store.kv {
            $crate::store::AnyKv::Rocks($kv) => $body,
            $crate::store::AnyKv::Mem($kv) => $body,
            $crate::store::AnyKv::Managed(_) => Err($crate::abi::FfiError::from(
                flatdb_core::kv::KvError::NotSupported("the managed key-value backend is not implemented yet"),
            )),
        }
    };
}
pub(crate) use with_kv;

/// Number of `Nethermind.Db.FlatLayout` values.
const LAYOUT_COUNT: u32 = 4;

impl FdbStore {
    /// # Safety
    /// The pointers of `desc` must satisfy [`RocksKv::new`] for [`FdbKvKind::Rocks`].
    pub unsafe fn create(cfg: &FdbStoreCfg, desc: &FdbKvDesc) -> Result<Self, FfiError> {
        if cfg.abi_version != ABI_VERSION {
            return Err(FfiError::invalid_argument(format!(
                "the adapter speaks ABI version {}, the library {ABI_VERSION}",
                cfg.abi_version
            )));
        }
        if cfg.layout >= LAYOUT_COUNT {
            return Err(FfiError::invalid_argument(format!(
                "unknown flat layout {}",
                cfg.layout
            )));
        }

        let kv = match desc.kind {
            // SAFETY: the caller vouches for the RocksDB handles of the descriptor.
            kind if kind == FdbKvKind::Rocks as u32 => AnyKv::Rocks(unsafe { create_rocks(desc) }?),
            kind if kind == FdbKvKind::Mem as u32 => AnyKv::Mem(MemKv::new()),
            kind if kind == FdbKvKind::Managed as u32 => {
                // SAFETY: the caller passes a valid descriptor.
                let vtable = unsafe { desc.managed.as_ref() }
                    .ok_or_else(|| FfiError::invalid_argument("null managed key-value callbacks"))?;
                AnyKv::Managed(ManagedKv::new(vtable)?)
            }
            kind => return Err(FfiError::invalid_argument(format!("unknown key-value backend {kind}"))),
        };

        Ok(Self {
            kv,
            read_count: AtomicU64::new(0),
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

/// Writes one value in its own batch, for smoke tests of the shared database.
pub fn debug_put<K: KvStore>(kv: &K, column: Column, key: &[u8], value: &[u8]) -> Result<(), FfiError> {
    let mut batch = kv.write_batch()?;
    batch.put(column, key, value, WriteFlags::NONE)?;
    batch.commit()?;
    Ok(())
}
