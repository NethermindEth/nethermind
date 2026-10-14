//! Port of the write side of `Nethermind.State.Flat/PersistedSnapshots/Storage/SnapshotCatalog.cs`: the rows of the
//! keyed catalog database that name every persisted snapshot across restarts.
//!
//! C# opens the catalog database and reads its rows at startup (see [`crate::persisted::codec::parse_rows`]); the
//! tier only adds and removes rows, through a [`CatalogKv`]: the RocksDB instance C# opened ([`RocksCatalogKv`]),
//! callbacks into a C# `IDb` that is not RocksDB ([`ManagedCatalogKv`]), or memory ([`MemCatalogKv`]).

use std::collections::BTreeMap;
use std::sync::Arc;
use std::sync::atomic::Ordering;

use parking_lot::Mutex;

use crate::kv::managed::check;
use crate::kv::rocks::RocksKv;
use crate::kv::{Column, KvRawBatch, KvResult, KvStore, WriteFlags};
use crate::persisted::TierResult;
use crate::persisted::codec::{self, CatalogEntry};
use crate::stats::SharedTierCounters;
use crate::types::StateId;

/// The operations of `IDb` the catalog uses.
pub trait CatalogKv: Send + Sync {
    /// `PutSpan`.
    fn put(&self, key: &[u8], value: &[u8]) -> KvResult<()>;
    /// `KeyExists`.
    fn key_exists(&self, key: &[u8]) -> KvResult<bool>;
    /// `Remove`.
    fn remove(&self, key: &[u8]) -> KvResult<()>;
}

/// `SnapshotCatalog`: rows keyed by `(To, depth)`, so a base, a compacted and a CompactSized snapshot ending at the
/// same block each have their own.
pub struct SnapshotCatalog {
    kv: Box<dyn CatalogKv>,
}

impl SnapshotCatalog {
    pub fn new(kv: Box<dyn CatalogKv>) -> Self {
        Self { kv }
    }

    /// `Add`: writes the entry's row, replacing a row of the same key.
    pub fn add(&self, entry: &CatalogEntry) -> TierResult<()> {
        let (key, value) = codec::encode_row(entry);
        Ok(self.kv.put(&key, &value)?)
    }

    /// `Remove`: deletes the row of `(to, depth)`, returning whether there was one.
    pub fn remove(&self, to: &StateId, depth: i64) -> TierResult<bool> {
        let key = codec::catalog_key(to, depth);
        if !self.kv.key_exists(&key)? {
            return Ok(false);
        }
        self.kv.remove(&key)?;
        Ok(true)
    }
}

/// The catalog `DbOnTheRocks` C# opened, driven through its RocksDB instance: every column of `kv` is the default
/// column family. A put or remove is a one-operation batch written with the default write options, and an existence
/// check a pinned read with the default read options, as `DbOnTheRocks` does. Only puts are counted, as only
/// `PutSpan` counts in `DbOnTheRocks`'s `TotalWrites`.
pub struct RocksCatalogKv<K: KvStore = RocksKv> {
    kv: K,
    counters: SharedTierCounters,
}

impl<K: KvStore> RocksCatalogKv<K> {
    pub fn new(kv: K, counters: SharedTierCounters) -> Self {
        Self { kv, counters }
    }

    fn write(&self, operation: impl FnOnce(&mut K::RawBatch)) -> KvResult<()> {
        let mut batch = self.kv.raw_batch()?;
        operation(&mut batch);
        batch.write(WriteFlags::NONE)
    }
}

impl<K: KvStore> CatalogKv for RocksCatalogKv<K> {
    fn put(&self, key: &[u8], value: &[u8]) -> KvResult<()> {
        self.counters.catalog_puts.fetch_add(1, Ordering::Relaxed);
        self.write(|batch| batch.put(Column::Metadata, key, value))
    }

    fn key_exists(&self, key: &[u8]) -> KvResult<bool> {
        Ok(self.kv.get_with(Column::Metadata, key, |_| ())?.is_some())
    }

    fn remove(&self, key: &[u8]) -> KvResult<()> {
        self.write(|batch| batch.delete(Column::Metadata, key))
    }
}

/// A catalog `IDb` that is not RocksDB (the tests' `MemDb`), called back for every operation; it counts its own
/// writes, so none are counted here. Every callback returns a status that is 0 on success.
pub struct ManagedCatalogKv {
    put: Box<PutCallback>,
    key_exists: Box<KeyExistsCallback>,
    remove: Box<RemoveCallback>,
}

type PutCallback = dyn Fn(&[u8], &[u8]) -> i32 + Send + Sync;
/// Sets its `bool` to whether the key exists.
type KeyExistsCallback = dyn Fn(&[u8], &mut bool) -> i32 + Send + Sync;
type RemoveCallback = dyn Fn(&[u8]) -> i32 + Send + Sync;

impl ManagedCatalogKv {
    pub fn new(
        put: impl Fn(&[u8], &[u8]) -> i32 + Send + Sync + 'static,
        key_exists: impl Fn(&[u8], &mut bool) -> i32 + Send + Sync + 'static,
        remove: impl Fn(&[u8]) -> i32 + Send + Sync + 'static,
    ) -> Self {
        Self {
            put: Box::new(put),
            key_exists: Box::new(key_exists),
            remove: Box::new(remove),
        }
    }
}

impl CatalogKv for ManagedCatalogKv {
    fn put(&self, key: &[u8], value: &[u8]) -> KvResult<()> {
        check("put", (self.put)(key, value))
    }

    fn key_exists(&self, key: &[u8]) -> KvResult<bool> {
        let mut exists = false;
        check("key_exists", (self.key_exists)(key, &mut exists))?;
        Ok(exists)
    }

    fn remove(&self, key: &[u8]) -> KvResult<()> {
        check("remove", (self.remove)(key))
    }
}

/// A catalog in memory, shared by its clones, so a test can reopen the tier over the rows a previous one wrote.
#[derive(Clone, Default)]
pub struct MemCatalogKv {
    rows: Arc<Mutex<BTreeMap<Vec<u8>, Vec<u8>>>>,
}

impl MemCatalogKv {
    /// The rows, in key order.
    pub fn rows(&self) -> Vec<(Vec<u8>, Vec<u8>)> {
        self.rows
            .lock()
            .iter()
            .map(|(key, value)| (key.clone(), value.clone()))
            .collect()
    }
}

impl CatalogKv for MemCatalogKv {
    fn put(&self, key: &[u8], value: &[u8]) -> KvResult<()> {
        self.rows.lock().insert(key.to_vec(), value.to_vec());
        Ok(())
    }

    fn key_exists(&self, key: &[u8]) -> KvResult<bool> {
        Ok(self.rows.lock().contains_key(key))
    }

    fn remove(&self, key: &[u8]) -> KvResult<()> {
        self.rows.lock().remove(key);
        Ok(())
    }
}
