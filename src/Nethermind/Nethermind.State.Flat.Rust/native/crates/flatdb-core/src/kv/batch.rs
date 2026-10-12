//! Port of `DbOnTheRocks.RocksDbWriteBatch` (Nethermind.Db.Rocks/DbOnTheRocks.cs).

use super::{Column, KvRawBatch, KvResult, WriteFlags};

/// A write batch with the semantics of `DbOnTheRocks.RocksDbWriteBatch` as the column batches of
/// `ColumnsDb.StartWriteBatch` drive it, where a delete is `Set(key, null, flags)`:
/// - [`WriteBatch::commit`] writes with the flags of the last put or delete, as the C# batch writes on dispose;
/// - every [`WriteBatch::MAX_WRITES_ON_NO_WAL`]-th put or delete carrying [`WriteFlags::DISABLE_WAL`] writes out the
///   operations buffered so far, so one large batch does not stall concurrent writers. That gives up atomicity, which
///   is acceptable only because such writes are redone after a crash anyway.
///
/// Dropping the batch without committing discards whatever was not written out yet.
pub struct WriteBatch<B: KvRawBatch> {
    raw: B,
    last_flags: WriteFlags,
    no_wal_writes: u64,
}

impl<B: KvRawBatch> WriteBatch<B> {
    /// Port of `RocksDbWriteBatch.MaxWritesOnNoWal`.
    pub const MAX_WRITES_ON_NO_WAL: u64 = 256;

    pub fn new(raw: B) -> Self {
        Self {
            raw,
            last_flags: WriteFlags::NONE,
            no_wal_writes: 0,
        }
    }

    pub fn put(&mut self, column: Column, key: &[u8], value: &[u8], flags: WriteFlags) -> KvResult<()> {
        self.raw.put(column, key, value);
        self.on_write(flags)
    }

    /// Deletes `key`. `IWriteBatch.Remove` is a delete with [`WriteFlags::NONE`].
    pub fn delete(&mut self, column: Column, key: &[u8], flags: WriteFlags) -> KvResult<()> {
        self.raw.delete(column, key);
        self.on_write(flags)
    }

    /// Deletes every key in `[start, end)`. Neither the commit flags nor the no-WAL chunking take it into account.
    pub fn delete_range(&mut self, column: Column, start: &[u8], end: &[u8]) {
        self.raw.delete_range(column, start, end);
    }

    /// Drops the buffered operations, keeping the flags and the no-WAL write count as `RocksDbWriteBatch.Clear` does.
    pub fn clear(&mut self) {
        self.raw.clear();
    }

    /// Number of operations buffered since the last write-out.
    pub fn pending(&self) -> usize {
        self.raw.count()
    }

    pub fn commit(mut self) -> KvResult<()> {
        self.raw.write(self.last_flags)
    }

    fn on_write(&mut self, flags: WriteFlags) -> KvResult<()> {
        self.last_flags = flags;
        if !flags.contains(WriteFlags::DISABLE_WAL) {
            return Ok(());
        }

        self.no_wal_writes += 1;
        if !self.no_wal_writes.is_multiple_of(Self::MAX_WRITES_ON_NO_WAL) {
            return Ok(());
        }

        // `FlushOnTooManyWrites` swaps in a fresh batch before writing, so a write-out that fails drops its operations
        // rather than leaving them for the next write-out or the commit.
        let written = self.raw.write(flags);
        if written.is_err() {
            self.raw.clear();
        }
        written
    }
}

#[cfg(test)]
mod tests {
    use super::WriteBatch;
    use crate::kv::mem::{MemBatch, MemKv, MemWrite};
    use crate::kv::{Column, KvError, KvRawBatch, KvResult, KvStore, WriteFlags};

    /// A [`MemBatch`] whose first write fails, as RocksDB fails a write with a non-fatal status.
    struct FirstWriteFails {
        inner: MemBatch,
        failed: bool,
    }

    impl KvRawBatch for FirstWriteFails {
        fn put(&mut self, column: Column, key: &[u8], value: &[u8]) {
            self.inner.put(column, key, value);
        }

        fn delete(&mut self, column: Column, key: &[u8]) {
            self.inner.delete(column, key);
        }

        fn delete_range(&mut self, column: Column, start: &[u8], end: &[u8]) {
            self.inner.delete_range(column, start, end);
        }

        fn count(&self) -> usize {
            self.inner.count()
        }

        fn clear(&mut self) {
            self.inner.clear();
        }

        fn write(&mut self, flags: WriteFlags) -> KvResult<()> {
            if !std::mem::replace(&mut self.failed, true) {
                return Err(KvError::rocks("Busy: injected".to_owned()));
            }
            self.inner.write(flags)
        }
    }

    #[test]
    fn a_failed_no_wal_write_out_drops_its_operations_as_csharp_does() {
        let kv = MemKv::new();
        let raw = FirstWriteFails {
            inner: kv.raw_batch().unwrap(),
            failed: false,
        };
        let mut batch = WriteBatch::new(raw);
        for i in 1..WriteBatch::<MemBatch>::MAX_WRITES_ON_NO_WAL {
            batch
                .put(Column::Storage, &i.to_be_bytes(), &[1], WriteFlags::DISABLE_WAL)
                .unwrap();
        }

        let failure = batch.put(Column::Storage, &[0xff], &[1], WriteFlags::DISABLE_WAL);
        let pending_after_failure = batch.pending();
        batch.put(Column::Metadata, &[0], &[2], WriteFlags::NONE).unwrap();
        batch.commit().unwrap();

        assert_eq!(failure, Err(KvError::rocks("Busy: injected".to_owned())));
        assert_eq!(pending_after_failure, 0);
        assert_eq!(
            kv.writes(),
            [MemWrite {
                flags: WriteFlags::NONE,
                operations: 1
            }],
            "only the write after the failed write-out is committed"
        );
    }
}
