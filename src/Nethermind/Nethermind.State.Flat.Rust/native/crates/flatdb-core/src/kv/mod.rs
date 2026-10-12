//! Column-addressed key-value store over the seven flat-DB columns.
//!
//! The semantics follow `Nethermind.Db.Rocks` (`DbOnTheRocks.cs`, `ColumnsDb.cs`, `RocksDbReader.cs`), so a store
//! driven from Rust behaves like the C# one on the same RocksDB instance. Callers are generic over [`KvStore`], so
//! the hot path is statically dispatched to one backend:
//! - [`rocks::RocksKv`]: the RocksDB instance opened by C#, driven through [`rocks_api::RocksApi`];
//! - [`mem::MemKv`]: an in-memory multi-version store for tests;
//! - [`managed::ManagedKv`]: callbacks into a C# `IColumnsDb` for non-RocksDB databases.

mod batch;
mod collected;
mod error;
pub mod managed;
pub mod mem;
pub mod rocks;
pub mod rocks_api;

pub use batch::WriteBatch;
pub use collected::CollectedIter;
pub use error::{KvError, KvResult};

/// A column of the flat DB, numbered as `Nethermind.State.Flat.FlatDbColumns`.
#[repr(u8)]
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub enum Column {
    Metadata = 0,
    Account = 1,
    Storage = 2,
    StateNodes = 3,
    StateTopNodes = 4,
    StorageNodes = 5,
    FallbackNodes = 6,
}

impl Column {
    /// Number of columns.
    pub const COUNT: usize = 7;

    /// Every column, in ordinal order.
    pub const ALL: [Column; Self::COUNT] = [
        Column::Metadata,
        Column::Account,
        Column::Storage,
        Column::StateNodes,
        Column::StateTopNodes,
        Column::StorageNodes,
        Column::FallbackNodes,
    ];

    /// The ordinal of the column.
    pub const fn index(self) -> usize {
        self as usize
    }

    /// The column with the given ordinal, if there is one.
    pub fn from_index(index: u32) -> Option<Self> {
        Self::ALL.get(index as usize).copied()
    }
}

/// Write hints, with the bit values of `Nethermind.Core.WriteFlags`.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash)]
pub struct WriteFlags(u32);

impl WriteFlags {
    pub const NONE: Self = Self(0);
    pub const LOW_PRIORITY: Self = Self(1);
    pub const DISABLE_WAL: Self = Self(2);
    pub const LOW_PRIORITY_AND_NO_WAL: Self = Self(3);

    /// Flags from their `WriteFlags` bit pattern. Unknown bits are kept and ignored.
    pub const fn from_bits(bits: u32) -> Self {
        Self(bits)
    }

    pub const fn bits(self) -> u32 {
        self.0
    }

    pub const fn contains(self, other: Self) -> bool {
        self.0 & other.0 == other.0
    }
}

/// Read hints, with the bit values of `Nethermind.Core.ReadFlags`.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash)]
pub struct ReadFlags(u32);

impl ReadFlags {
    pub const NONE: Self = Self(0);
    /// The read is unlikely to benefit from caching, so it does not fill the block cache.
    pub const HINT_CACHE_MISS: Self = Self(1);
    /// The read is followed by reads of the next keys, so iterators read ahead, and a reader opened for sequential
    /// reads serves a point read from a pooled iterator (see `RocksDbReader.Get`).
    pub const HINT_READ_AHEAD: Self = Self(2);
    /// With [`ReadFlags::HINT_READ_AHEAD`], selects a second pool of read-ahead iterators.
    pub const HINT_READ_AHEAD_2: Self = Self(4);
    /// With [`ReadFlags::HINT_READ_AHEAD`], selects a third pool of read-ahead iterators.
    pub const HINT_READ_AHEAD_3: Self = Self(8);

    /// Flags from their `ReadFlags` bit pattern. Unknown bits are kept and ignored.
    pub const fn from_bits(bits: u32) -> Self {
        Self(bits)
    }

    pub const fn bits(self) -> u32 {
        self.0
    }

    pub const fn contains(self, other: Self) -> bool {
        self.0 & other.0 == other.0
    }
}

impl std::ops::BitOr for ReadFlags {
    type Output = Self;

    fn bitor(self, rhs: Self) -> Self {
        Self(self.0 | rhs.0)
    }
}

/// Outcome of a read into a caller buffer.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum GetInto {
    NotFound,
    /// The value was copied; it has this many bytes.
    Found(usize),
    /// The value has this many bytes, more than the buffer holds; nothing useful was copied.
    BufferTooSmall(usize),
}

/// Bounds and hints of an iterator, as `ISortedKeyValueStore.GetViewBetween` takes them.
#[derive(Clone, Copy, Debug, Default)]
pub struct IterOptions<'a> {
    /// Inclusive lower bound.
    pub lower_bound: Option<&'a [u8]>,
    /// Exclusive upper bound.
    pub upper_bound: Option<&'a [u8]>,
    pub flags: ReadFlags,
}

/// A store over the flat-DB columns.
pub trait KvStore: Send + Sync + 'static {
    type Reader: KvReader;
    type RawBatch: KvRawBatch;

    /// Copies the latest value of `key` into `buffer`.
    fn get(&self, column: Column, key: &[u8], buffer: &mut [u8]) -> KvResult<GetInto>;

    /// Calls `read` with the latest value of `key` without copying it, or returns `None` when it is absent.
    fn get_with<R>(&self, column: Column, key: &[u8], read: impl FnOnce(&[u8]) -> R) -> KvResult<Option<R>>;

    /// Opens a reader over a consistent snapshot of every column, as `IColumnsDb.CreateSnapshot(sequentialReadAhead)`
    /// does. A reader for sequential reads serves [`ReadFlags::HINT_READ_AHEAD`] point reads from read-ahead
    /// iterators where the backend has them.
    fn reader(&self, sequential_read_ahead: bool) -> KvResult<Self::Reader>;

    /// Starts a backend batch; most callers want [`KvStore::write_batch`].
    fn raw_batch(&self) -> KvResult<Self::RawBatch>;

    /// Starts a write batch with `DbOnTheRocks` batch semantics.
    fn write_batch(&self) -> KvResult<WriteBatch<Self::RawBatch>> {
        Ok(WriteBatch::new(self.raw_batch()?))
    }

    /// Flushes the write-ahead log, as `ColumnsDb.Flush(onlyWal: true)` does.
    fn flush_wal(&self, sync: bool) -> KvResult<()>;

    /// Flushes the write-ahead log and then every memtable, as `ColumnsDb.Flush()` does.
    fn flush(&self) -> KvResult<()>;

    /// Resizes the memtable of `column`, as `ColumnDb.SetWriteBuffer` does.
    fn set_write_buffer(&self, column: Column, size_bytes: u64) -> KvResult<()>;

    /// Reads a backend property of `column`, or `None` when the backend does not know it.
    fn property(&self, column: Column, name: &str) -> KvResult<Option<String>>;

    /// Takes the number of reads into a caller buffer made since the last call: the reads `DbOnTheRocks` counts in
    /// its `TotalReads` metric (`GetCStyleWithColumnFamily`). A backend whose reads C# counts itself reports none.
    fn take_read_count(&self) -> u64 {
        0
    }
}

/// Reads at the snapshot taken when the reader was opened.
pub trait KvReader: Send + Sync {
    type Iter: KvIterator;

    fn get(&self, column: Column, key: &[u8], flags: ReadFlags, buffer: &mut [u8]) -> KvResult<GetInto>;

    fn get_with<R>(
        &self,
        column: Column,
        key: &[u8],
        flags: ReadFlags,
        read: impl FnOnce(&[u8]) -> R,
    ) -> KvResult<Option<R>>;

    /// Opens an iterator over `column`. It is not positioned until one of its seek methods is called.
    fn iter(&self, column: Column, options: IterOptions<'_>) -> KvResult<Self::Iter>;
}

/// A RocksDB-style iterator. [`KvIterator::key`] and [`KvIterator::value`] panic unless it is valid.
pub trait KvIterator: Send {
    fn seek(&mut self, key: &[u8]);
    fn seek_for_prev(&mut self, key: &[u8]);
    fn seek_to_first(&mut self);
    fn seek_to_last(&mut self);
    fn valid(&self) -> bool;
    fn next(&mut self);
    fn prev(&mut self);
    fn key(&self) -> &[u8];
    fn value(&self) -> &[u8];
    /// The error that stopped the iterator, if any.
    fn status(&self) -> KvResult<()>;
}

/// Operations buffered by a backend until [`KvRawBatch::write`].
pub trait KvRawBatch: Send {
    fn put(&mut self, column: Column, key: &[u8], value: &[u8]);
    fn delete(&mut self, column: Column, key: &[u8]);
    /// Deletes every key in `[start, end)`.
    fn delete_range(&mut self, column: Column, start: &[u8], end: &[u8]);
    /// Number of buffered operations.
    fn count(&self) -> usize;
    fn clear(&mut self);
    /// Atomically writes the buffered operations with the write options `flags` select, leaving the batch empty.
    fn write(&mut self, flags: WriteFlags) -> KvResult<()>;
}

/// Whether an iterator over `[lower, upper)` can leave the prefix bucket its seek lands in, so it must ask for total
/// order; port of `DbOnTheRocks.CrossesPrefixBucket`. A missing bound is unbounded and so always crosses.
pub(crate) fn crosses_prefix_bucket(prefix_length: u32, lower: Option<&[u8]>, upper: Option<&[u8]>) -> bool {
    if prefix_length == 0 {
        return false;
    }

    let (Some(lower), Some(upper)) = (lower, upper) else {
        return true;
    };

    let length = prefix_length as usize;
    lower.len() < length || upper.len() < length || lower[..length] != upper[..length]
}

#[cfg(test)]
mod tests {
    use super::{Column, crosses_prefix_bucket};
    use rstest::rstest;

    #[test]
    fn column_ordinals_match_flat_db_columns() {
        for (index, column) in Column::ALL.iter().enumerate() {
            assert_eq!(column.index(), index);
            assert_eq!(Column::from_index(index as u32), Some(*column));
        }
        assert_eq!(Column::from_index(Column::COUNT as u32), None);
    }

    #[rstest]
    #[case(0, Some(&b"aaaa"[..]), Some(&b"zzzz"[..]), false)]
    #[case(2, Some(&b"aaaa"[..]), Some(&b"aazz"[..]), false)]
    #[case(2, Some(&b"aaaa"[..]), Some(&b"abzz"[..]), true)]
    #[case(8, Some(&b"aaaa"[..]), Some(&b"aaaa"[..]), true)]
    #[case(i32::MAX as u32, Some(&b"aaaa"[..]), Some(&b"aaaa"[..]), true)]
    #[case(2, None, Some(&b"aazz"[..]), true)]
    #[case(0, None, None, false)]
    fn prefix_bucket_crossing_matches_csharp(
        #[case] prefix_length: u32,
        #[case] lower: Option<&[u8]>,
        #[case] upper: Option<&[u8]>,
        #[case] crosses: bool,
    ) {
        assert_eq!(crosses_prefix_bucket(prefix_length, lower, upper), crosses);
    }
}
