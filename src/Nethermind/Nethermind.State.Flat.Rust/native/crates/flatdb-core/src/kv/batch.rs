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

        self.raw.write(flags)
    }
}
