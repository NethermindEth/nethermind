//! One store type over every backend, so a test runs unchanged on the in-memory store, the managed backend (through
//! [`super::loopback`]) and, with the `rocksdb-it` feature, real RocksDB.

use flatdb_core::kv::managed::{ManagedBatch, ManagedIter, ManagedKv, ManagedReader};
use flatdb_core::kv::mem::{MemBatch, MemIter, MemKv, MemReader};
#[cfg(feature = "rocksdb-it")]
use flatdb_core::kv::rocks::{RocksBatch, RocksIter, RocksKv, RocksReader};
use flatdb_core::kv::{
    Column, GetInto, IterOptions, KvIterator, KvRawBatch, KvReader, KvResult, KvStore, ReadFlags, WriteFlags,
};

use super::loopback::Loopback;

/// A backend a test runs on.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Backend {
    Mem,
    Managed,
    Rocks,
}

impl Backend {
    /// Opens a fresh store, or `None` when the backend is unavailable (RocksDB without the `rocksdb-it` feature or
    /// the library).
    pub fn open(self) -> Option<TestStore> {
        match self {
            Backend::Mem => Some(TestStore {
                kv: AnyKv::Mem(MemKv::new()),
                _loopback: None,
                #[cfg(feature = "rocksdb-it")]
                _db: None,
            }),
            Backend::Managed => {
                let loopback = Loopback::new(true);
                Some(TestStore {
                    kv: AnyKv::Managed(loopback.kv()),
                    _loopback: Some(loopback),
                    #[cfg(feature = "rocksdb-it")]
                    _db: None,
                })
            }
            #[cfg(feature = "rocksdb-it")]
            Backend::Rocks => {
                let db = super::rocks::TestDb::open()?;
                Some(TestStore {
                    kv: AnyKv::Rocks(db.kv(false)),
                    _loopback: None,
                    _db: Some(db),
                })
            }
            #[cfg(not(feature = "rocksdb-it"))]
            Backend::Rocks => None,
        }
    }
}

/// A store and whatever keeps it alive; the store goes first.
pub struct TestStore {
    pub kv: AnyKv,
    _loopback: Option<Loopback>,
    #[cfg(feature = "rocksdb-it")]
    _db: Option<super::rocks::TestDb>,
}

#[derive(Clone)]
pub enum AnyKv {
    Mem(MemKv),
    Managed(ManagedKv),
    #[cfg(feature = "rocksdb-it")]
    Rocks(RocksKv),
}

pub enum AnyReader {
    Mem(MemReader),
    Managed(ManagedReader),
    #[cfg(feature = "rocksdb-it")]
    Rocks(RocksReader),
}

pub enum AnyIter {
    Mem(MemIter),
    Managed(ManagedIter),
    #[cfg(feature = "rocksdb-it")]
    Rocks(RocksIter),
}

pub enum AnyBatch {
    Mem(MemBatch),
    Managed(ManagedBatch),
    #[cfg(feature = "rocksdb-it")]
    Rocks(RocksBatch),
}

/// Runs `$body` with `$inner` bound to the backend value of `$value`, an enum of `$enum`, wrapping the result with
/// `$wrap` (a variant constructor taking the same variant name) when given.
macro_rules! dispatch {
    ($enum:ident, $value:expr, $inner:ident => $body:expr) => {
        match $value {
            $enum::Mem($inner) => $body,
            $enum::Managed($inner) => $body,
            #[cfg(feature = "rocksdb-it")]
            $enum::Rocks($inner) => $body,
        }
    };
    ($enum:ident, $value:expr, $inner:ident => $body:expr, $wrap:ident) => {
        match $value {
            $enum::Mem($inner) => $wrap::Mem($body),
            $enum::Managed($inner) => $wrap::Managed($body),
            #[cfg(feature = "rocksdb-it")]
            $enum::Rocks($inner) => $wrap::Rocks($body),
        }
    };
}

impl KvStore for AnyKv {
    type Reader = AnyReader;
    type RawBatch = AnyBatch;

    fn get(&self, column: Column, key: &[u8], buffer: &mut [u8]) -> KvResult<GetInto> {
        dispatch!(AnyKv, self, kv => kv.get(column, key, buffer))
    }

    fn get_with<R>(&self, column: Column, key: &[u8], read: impl FnOnce(&[u8]) -> R) -> KvResult<Option<R>> {
        dispatch!(AnyKv, self, kv => kv.get_with(column, key, read))
    }

    fn reader(&self, sequential_read_ahead: bool) -> KvResult<AnyReader> {
        Ok(dispatch!(AnyKv, self, kv => kv.reader(sequential_read_ahead)?, AnyReader))
    }

    fn raw_batch(&self) -> KvResult<AnyBatch> {
        Ok(dispatch!(AnyKv, self, kv => kv.raw_batch()?, AnyBatch))
    }

    fn flush_wal(&self, sync: bool) -> KvResult<()> {
        dispatch!(AnyKv, self, kv => kv.flush_wal(sync))
    }

    fn flush(&self) -> KvResult<()> {
        dispatch!(AnyKv, self, kv => kv.flush())
    }

    fn set_write_buffer(&self, column: Column, size_bytes: u64) -> KvResult<()> {
        dispatch!(AnyKv, self, kv => kv.set_write_buffer(column, size_bytes))
    }

    fn property(&self, column: Column, name: &str) -> KvResult<Option<String>> {
        dispatch!(AnyKv, self, kv => kv.property(column, name))
    }
}

impl KvReader for AnyReader {
    type Iter = AnyIter;

    fn get(&self, column: Column, key: &[u8], flags: ReadFlags, buffer: &mut [u8]) -> KvResult<GetInto> {
        dispatch!(AnyReader, self, reader => reader.get(column, key, flags, buffer))
    }

    fn get_with<R>(
        &self,
        column: Column,
        key: &[u8],
        flags: ReadFlags,
        read: impl FnOnce(&[u8]) -> R,
    ) -> KvResult<Option<R>> {
        dispatch!(AnyReader, self, reader => reader.get_with(column, key, flags, read))
    }

    fn iter(&self, column: Column, options: IterOptions<'_>) -> KvResult<AnyIter> {
        Ok(dispatch!(AnyReader, self, reader => reader.iter(column, options)?, AnyIter))
    }
}

impl KvIterator for AnyIter {
    fn seek(&mut self, key: &[u8]) {
        dispatch!(AnyIter, self, iter => iter.seek(key))
    }

    fn seek_for_prev(&mut self, key: &[u8]) {
        dispatch!(AnyIter, self, iter => iter.seek_for_prev(key))
    }

    fn seek_to_first(&mut self) {
        dispatch!(AnyIter, self, iter => iter.seek_to_first())
    }

    fn seek_to_last(&mut self) {
        dispatch!(AnyIter, self, iter => iter.seek_to_last())
    }

    fn valid(&self) -> bool {
        dispatch!(AnyIter, self, iter => iter.valid())
    }

    fn next(&mut self) {
        dispatch!(AnyIter, self, iter => iter.next())
    }

    fn prev(&mut self) {
        dispatch!(AnyIter, self, iter => iter.prev())
    }

    fn key(&self) -> &[u8] {
        dispatch!(AnyIter, self, iter => iter.key())
    }

    fn value(&self) -> &[u8] {
        dispatch!(AnyIter, self, iter => iter.value())
    }

    fn status(&self) -> KvResult<()> {
        dispatch!(AnyIter, self, iter => iter.status())
    }
}

impl KvRawBatch for AnyBatch {
    fn put(&mut self, column: Column, key: &[u8], value: &[u8]) {
        dispatch!(AnyBatch, self, batch => batch.put(column, key, value))
    }

    fn delete(&mut self, column: Column, key: &[u8]) {
        dispatch!(AnyBatch, self, batch => batch.delete(column, key))
    }

    fn delete_range(&mut self, column: Column, start: &[u8], end: &[u8]) {
        dispatch!(AnyBatch, self, batch => batch.delete_range(column, start, end))
    }

    fn count(&self) -> usize {
        dispatch!(AnyBatch, self, batch => batch.count())
    }

    fn clear(&mut self) {
        dispatch!(AnyBatch, self, batch => batch.clear())
    }

    fn write(&mut self, flags: WriteFlags) -> KvResult<()> {
        dispatch!(AnyBatch, self, batch => batch.write(flags))
    }
}
