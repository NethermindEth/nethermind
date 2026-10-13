//! Fault injection, for tests driving the library from outside (the `test-api` feature): a store's [`Faults`] arm a
//! RocksDB error or a panic at one point of the store, its persistence or its manager, which the next pass through that
//! point takes. Nothing is armed unless `fdb_test_inject_fault` arms it, so an unarmed point only costs a load.

use std::sync::atomic::{AtomicU32, Ordering};
use std::sync::{Arc, Mutex, PoisonError};

use flatdb_core::compactor::Compactor;
use flatdb_core::kv::{Column, GetInto, KvError, KvRawBatch, KvResult, KvStore, WriteFlags};
use flatdb_core::types::StateId;

/// Where a fault strikes; the C# mirror is `NativeFaults.Fault` of the tests.
#[repr(u32)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Fault {
    /// The next write of a key-value batch fails with a RocksDB error carrying the message, which is fatal when it
    /// says `Corruption:` or `IO error`, as `DbOnTheRocks.HandleFatalDbError` tells.
    KvWrite,
    /// The next flush of the write-ahead log or of the memtables fails with a RocksDB error carrying the message.
    KvFlush,
    /// The next guarded call on the store panics with the message.
    PanicInExport,
    /// The next persist of the store's manager panics with the message before it opens its batch, on whatever thread
    /// persists, so nothing but the panic tells the store a persist went wrong.
    PanicInPersist,
    /// The next compaction of the store's manager panics with the message.
    PanicInCompaction,
}

impl Fault {
    pub const COUNT: usize = Fault::PanicInCompaction as usize + 1;

    const ALL: [Fault; Self::COUNT] = [
        Fault::KvWrite,
        Fault::KvFlush,
        Fault::PanicInExport,
        Fault::PanicInPersist,
        Fault::PanicInCompaction,
    ];

    pub fn from_u32(value: u32) -> Option<Self> {
        Self::ALL.get(value as usize).copied()
    }

    const fn bit(self) -> u32 {
        1 << self as u32
    }
}

/// The faults armed on a store, each taken by the first pass through its point.
#[derive(Default)]
pub struct Faults {
    /// One bit per armed fault.
    armed: AtomicU32,
    messages: Mutex<[Option<String>; Fault::COUNT]>,
}

impl Faults {
    pub fn arm(&self, fault: Fault, message: String) {
        self.messages.lock().unwrap_or_else(PoisonError::into_inner)[fault as usize] = Some(message);
        self.armed.fetch_or(fault.bit(), Ordering::AcqRel);
    }

    /// Takes the message of `fault` when it is armed.
    pub fn take(&self, fault: Fault) -> Option<String> {
        if self.armed.load(Ordering::Acquire) & fault.bit() == 0
            || self.armed.fetch_and(!fault.bit(), Ordering::AcqRel) & fault.bit() == 0
        {
            return None;
        }
        self.messages.lock().unwrap_or_else(PoisonError::into_inner)[fault as usize].take()
    }

    /// Panics with the message of `fault` when it is armed.
    pub fn panic_if_armed(&self, fault: Fault) {
        if let Some(message) = self.take(fault) {
            panic!("{message}");
        }
    }

    /// Fails with a RocksDB error carrying the message of `fault` when it is armed.
    fn fail_if_armed(&self, fault: Fault) -> KvResult<()> {
        self.take(fault).map_or(Ok(()), |message| Err(KvError::rocks(message)))
    }
}

/// A backend whose batch writes and flushes take the [`Fault::KvWrite`] and [`Fault::KvFlush`] faults.
pub struct FaultyKv<K> {
    kv: K,
    faults: Arc<Faults>,
}

impl<K> FaultyKv<K> {
    pub fn new(kv: K, faults: Arc<Faults>) -> Self {
        Self { kv, faults }
    }
}

impl<K: KvStore> KvStore for FaultyKv<K> {
    type Reader = K::Reader;
    type RawBatch = FaultyBatch<K::RawBatch>;

    fn get(&self, column: Column, key: &[u8], buffer: &mut [u8]) -> KvResult<GetInto> {
        self.kv.get(column, key, buffer)
    }

    fn get_with<R>(&self, column: Column, key: &[u8], read: impl FnOnce(&[u8]) -> R) -> KvResult<Option<R>> {
        self.kv.get_with(column, key, read)
    }

    fn reader(&self, sequential_read_ahead: bool) -> KvResult<K::Reader> {
        self.kv.reader(sequential_read_ahead)
    }

    fn raw_batch(&self) -> KvResult<FaultyBatch<K::RawBatch>> {
        Ok(FaultyBatch {
            batch: self.kv.raw_batch()?,
            faults: Arc::clone(&self.faults),
        })
    }

    fn flush_wal(&self, sync: bool) -> KvResult<()> {
        self.faults.fail_if_armed(Fault::KvFlush)?;
        self.kv.flush_wal(sync)
    }

    fn flush(&self) -> KvResult<()> {
        self.faults.fail_if_armed(Fault::KvFlush)?;
        self.kv.flush()
    }

    fn set_write_buffer(&self, column: Column, size_bytes: u64) -> KvResult<()> {
        self.kv.set_write_buffer(column, size_bytes)
    }

    fn property(&self, column: Column, name: &str) -> KvResult<Option<String>> {
        self.kv.property(column, name)
    }

    fn take_read_count(&self) -> u64 {
        self.kv.take_read_count()
    }
}

/// A batch of a [`FaultyKv`]; a failed write writes nothing.
pub struct FaultyBatch<B> {
    batch: B,
    faults: Arc<Faults>,
}

impl<B: KvRawBatch> KvRawBatch for FaultyBatch<B> {
    fn put(&mut self, column: Column, key: &[u8], value: &[u8]) {
        self.batch.put(column, key, value);
    }

    fn delete(&mut self, column: Column, key: &[u8]) {
        self.batch.delete(column, key);
    }

    fn delete_range(&mut self, column: Column, start: &[u8], end: &[u8]) {
        self.batch.delete_range(column, start, end);
    }

    fn count(&self) -> usize {
        self.batch.count()
    }

    fn clear(&mut self) {
        self.batch.clear();
    }

    fn write(&mut self, flags: WriteFlags) -> KvResult<()> {
        self.faults.fail_if_armed(Fault::KvWrite)?;
        self.batch.write(flags)
    }
}

/// A compactor whose compactions take the [`Fault::PanicInCompaction`] fault.
pub struct FaultyCompactor {
    pub compactor: Arc<dyn Compactor>,
    pub faults: Arc<Faults>,
}

impl Compactor for FaultyCompactor {
    fn do_compact_snapshot(&self, state_id: &StateId) -> bool {
        self.faults.panic_if_armed(Fault::PanicInCompaction);
        self.compactor.do_compact_snapshot(state_id)
    }
}

#[cfg(test)]
mod tests {
    use super::{Fault, Faults};

    #[test]
    fn an_armed_fault_is_taken_once_and_leaves_the_others_armed() {
        let faults = Faults::default();
        faults.arm(Fault::KvFlush, "Busy: injected".to_owned());
        faults.arm(Fault::PanicInExport, "boom".to_owned());

        assert_eq!(faults.take(Fault::KvWrite), None);
        assert_eq!(faults.take(Fault::KvFlush).as_deref(), Some("Busy: injected"));
        assert_eq!(faults.take(Fault::KvFlush), None);
        assert_eq!(faults.take(Fault::PanicInExport).as_deref(), Some("boom"));
        assert_eq!(
            (
                Fault::from_u32(Fault::PanicInCompaction as u32),
                Fault::from_u32(Fault::COUNT as u32)
            ),
            (Some(Fault::PanicInCompaction), None)
        );
    }
}
