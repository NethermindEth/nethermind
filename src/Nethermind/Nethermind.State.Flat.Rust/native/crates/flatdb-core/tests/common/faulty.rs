//! A [`MemKv`] that fails the way RocksDB reports failures: from its flushes, and from iterators that stop early with
//! an error status. Clones of the inner store share its data, so a test can write through a healthy clone first.

use flatdb_core::kv::mem::{MemBatch, MemIter, MemKv, MemReader};
use flatdb_core::kv::{Column, GetInto, IterOptions, KvError, KvIterator, KvReader, KvResult, KvStore, ReadFlags};

/// The failures of a [`FaultyKv`].
#[derive(Clone, Default)]
pub struct Faults {
    /// Returned by every flush of the write-ahead log or the memtables.
    pub flush: Option<KvError>,
    /// Every iterator turns invalid after this many steps and reports the error as its status.
    pub iterator: Option<(usize, KvError)>,
}

#[derive(Clone)]
pub struct FaultyKv {
    pub kv: MemKv,
    pub faults: Faults,
}

impl FaultyKv {
    fn flushed(&self) -> KvResult<()> {
        self.faults.flush.clone().map_or(Ok(()), Err)
    }
}

impl KvStore for FaultyKv {
    type Reader = FaultyReader;
    type RawBatch = MemBatch;

    fn get(&self, column: Column, key: &[u8], buffer: &mut [u8]) -> KvResult<GetInto> {
        self.kv.get(column, key, buffer)
    }

    fn get_with<R>(&self, column: Column, key: &[u8], read: impl FnOnce(&[u8]) -> R) -> KvResult<Option<R>> {
        self.kv.get_with(column, key, read)
    }

    fn reader(&self, sequential_read_ahead: bool) -> KvResult<FaultyReader> {
        Ok(FaultyReader {
            reader: self.kv.reader(sequential_read_ahead)?,
            iterator_fault: self.faults.iterator.clone(),
        })
    }

    fn raw_batch(&self) -> KvResult<MemBatch> {
        self.kv.raw_batch()
    }

    fn flush_wal(&self, _sync: bool) -> KvResult<()> {
        self.flushed()
    }

    fn flush(&self) -> KvResult<()> {
        self.flushed()
    }

    fn set_write_buffer(&self, column: Column, size_bytes: u64) -> KvResult<()> {
        self.kv.set_write_buffer(column, size_bytes)
    }

    fn property(&self, column: Column, name: &str) -> KvResult<Option<String>> {
        self.kv.property(column, name)
    }
}

pub struct FaultyReader {
    reader: MemReader,
    iterator_fault: Option<(usize, KvError)>,
}

impl KvReader for FaultyReader {
    type Iter = FaultyIter;

    fn get(&self, column: Column, key: &[u8], flags: ReadFlags, buffer: &mut [u8]) -> KvResult<GetInto> {
        self.reader.get(column, key, flags, buffer)
    }

    fn get_with<R>(
        &self,
        column: Column,
        key: &[u8],
        flags: ReadFlags,
        read: impl FnOnce(&[u8]) -> R,
    ) -> KvResult<Option<R>> {
        self.reader.get_with(column, key, flags, read)
    }

    fn iter(&self, column: Column, options: IterOptions<'_>) -> KvResult<FaultyIter> {
        Ok(FaultyIter {
            iter: self.reader.iter(column, options)?,
            fault: self.iterator_fault.clone(),
            steps: 0,
        })
    }
}

pub struct FaultyIter {
    iter: MemIter,
    fault: Option<(usize, KvError)>,
    steps: usize,
}

impl FaultyIter {
    fn failure(&self) -> Option<&KvError> {
        self.fault
            .as_ref()
            .and_then(|(steps, error)| (self.steps >= *steps).then_some(error))
    }

    fn step(&mut self, step: impl FnOnce(&mut MemIter)) {
        step(&mut self.iter);
        self.steps += 1;
    }
}

impl KvIterator for FaultyIter {
    fn seek(&mut self, key: &[u8]) {
        self.step(|iter| iter.seek(key));
    }

    fn seek_for_prev(&mut self, key: &[u8]) {
        self.step(|iter| iter.seek_for_prev(key));
    }

    fn seek_to_first(&mut self) {
        self.step(MemIter::seek_to_first);
    }

    fn seek_to_last(&mut self) {
        self.step(MemIter::seek_to_last);
    }

    fn valid(&self) -> bool {
        self.failure().is_none() && self.iter.valid()
    }

    fn next(&mut self) {
        self.step(MemIter::next);
    }

    fn prev(&mut self) {
        self.step(MemIter::prev);
    }

    fn key(&self) -> &[u8] {
        self.iter.key()
    }

    fn value(&self) -> &[u8] {
        self.iter.value()
    }

    fn status(&self) -> KvResult<()> {
        self.failure().cloned().map_or(Ok(()), Err)
    }
}
