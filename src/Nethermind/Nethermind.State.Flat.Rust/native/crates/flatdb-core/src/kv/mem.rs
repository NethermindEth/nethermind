//! In-memory multi-version store for tests.
//!
//! Every committed write gets a new sequence number and keeps the versions it replaced, so readers see the snapshot
//! taken when they were opened, like RocksDB snapshots. Old versions are never collected.

use std::collections::BTreeMap;
use std::ops::Bound;
use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};

use parking_lot::RwLock;

use super::{
    CollectedIter, Column, GetInto, IterOptions, KvRawBatch, KvReader, KvResult, KvStore, ReadFlags, WriteFlags,
};

/// The in-memory store. Cloning shares the data.
#[derive(Clone, Default)]
pub struct MemKv {
    state: Arc<RwLock<State>>,
    /// Reads into a caller buffer since [`KvStore::take_read_count`] last took them, counted as the RocksDB backend
    /// counts them.
    reads: Arc<AtomicU64>,
}

/// One committed raw batch, recorded so tests can check how writes reached the store.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct MemWrite {
    pub flags: WriteFlags,
    pub operations: usize,
}

#[derive(Default)]
struct State {
    sequence: u64,
    columns: [BTreeMap<Box<[u8]>, Vec<Version>>; Column::COUNT],
    writes: Vec<MemWrite>,
    flushes: usize,
    wal_flushes: usize,
    write_buffer_sizes: Vec<(Column, u64)>,
}

struct Version {
    sequence: u64,
    /// `None` is a deletion.
    value: Option<Arc<[u8]>>,
}

impl State {
    fn get(&self, sequence: u64, column: Column, key: &[u8]) -> Option<Arc<[u8]>> {
        self.columns[column.index()]
            .get(key)
            .and_then(|versions| visible(versions, sequence))
    }
}

fn visible(versions: &[Version], sequence: u64) -> Option<Arc<[u8]>> {
    versions
        .iter()
        .rev()
        .find(|version| version.sequence <= sequence)
        .and_then(|version| version.value.clone())
}

impl MemKv {
    pub fn new() -> Self {
        Self::default()
    }

    /// Every raw batch written so far, oldest first.
    pub fn writes(&self) -> Vec<MemWrite> {
        self.state.read().writes.clone()
    }

    /// Number of [`KvStore::flush`] calls so far.
    pub fn flushes(&self) -> usize {
        self.state.read().flushes
    }

    /// Number of [`KvStore::flush_wal`] calls so far.
    pub fn wal_flushes(&self) -> usize {
        self.state.read().wal_flushes
    }

    /// Every [`KvStore::set_write_buffer`] call so far, oldest first.
    pub fn write_buffer_sizes(&self) -> Vec<(Column, u64)> {
        self.state.read().write_buffer_sizes.clone()
    }

    /// Whether another clone, reader, iterator or batch still uses the data.
    pub fn is_shared(&self) -> bool {
        Arc::strong_count(&self.state) > 1
    }

    fn read_at<R>(
        &self,
        sequence: Option<u64>,
        column: Column,
        key: &[u8],
        read: impl FnOnce(&[u8]) -> R,
    ) -> Option<R> {
        let value = {
            let state = self.state.read();
            state.get(sequence.unwrap_or(state.sequence), column, key)
        };
        value.map(|value| read(&value))
    }
}

fn get_into(kv: &MemKv, sequence: Option<u64>, column: Column, key: &[u8], buffer: &mut [u8]) -> GetInto {
    kv.reads.fetch_add(1, Ordering::Relaxed);
    kv.read_at(sequence, column, key, |value| match buffer.get_mut(..value.len()) {
        Some(target) => {
            target.copy_from_slice(value);
            GetInto::Found(value.len())
        }
        None => GetInto::BufferTooSmall(value.len()),
    })
    .unwrap_or(GetInto::NotFound)
}

impl KvStore for MemKv {
    type Reader = MemReader;
    type RawBatch = MemBatch;

    fn get(&self, column: Column, key: &[u8], buffer: &mut [u8]) -> KvResult<GetInto> {
        Ok(get_into(self, None, column, key, buffer))
    }

    fn get_with<R>(&self, column: Column, key: &[u8], read: impl FnOnce(&[u8]) -> R) -> KvResult<Option<R>> {
        Ok(self.read_at(None, column, key, read))
    }

    fn reader(&self, _sequential_read_ahead: bool) -> KvResult<MemReader> {
        Ok(MemReader {
            kv: self.clone(),
            sequence: self.state.read().sequence,
        })
    }

    fn raw_batch(&self) -> KvResult<MemBatch> {
        Ok(MemBatch {
            kv: self.clone(),
            operations: Vec::new(),
        })
    }

    fn flush_wal(&self, _sync: bool) -> KvResult<()> {
        self.state.write().wal_flushes += 1;
        Ok(())
    }

    fn flush(&self) -> KvResult<()> {
        self.state.write().flushes += 1;
        Ok(())
    }

    fn set_write_buffer(&self, column: Column, size_bytes: u64) -> KvResult<()> {
        self.state.write().write_buffer_sizes.push((column, size_bytes));
        Ok(())
    }

    fn property(&self, column: Column, name: &str) -> KvResult<Option<String>> {
        Ok(match name {
            "rocksdb.estimate-num-keys" => {
                let state = self.state.read();
                let sequence = state.sequence;
                let count = state.columns[column.index()]
                    .values()
                    .filter(|versions| visible(versions, sequence).is_some())
                    .count();
                Some(count.to_string())
            }
            _ => None,
        })
    }

    fn take_read_count(&self) -> u64 {
        self.reads.swap(0, Ordering::Relaxed)
    }
}

/// Reads at the sequence number current when it was opened.
pub struct MemReader {
    kv: MemKv,
    sequence: u64,
}

impl KvReader for MemReader {
    type Iter = MemIter;

    fn get(&self, column: Column, key: &[u8], _flags: ReadFlags, buffer: &mut [u8]) -> KvResult<GetInto> {
        Ok(get_into(&self.kv, Some(self.sequence), column, key, buffer))
    }

    fn get_with<R>(
        &self,
        column: Column,
        key: &[u8],
        _flags: ReadFlags,
        read: impl FnOnce(&[u8]) -> R,
    ) -> KvResult<Option<R>> {
        Ok(self.kv.read_at(Some(self.sequence), column, key, read))
    }

    fn iter(&self, column: Column, options: IterOptions<'_>) -> KvResult<MemIter> {
        let lower = options.lower_bound.map_or(Bound::Unbounded, Bound::Included);
        let upper = options.upper_bound.map_or(Bound::Unbounded, Bound::Excluded);
        let entries = match (options.lower_bound, options.upper_bound) {
            (Some(lower), Some(upper)) if lower >= upper => Vec::new(),
            _ => {
                let state = self.kv.state.read();
                state.columns[column.index()]
                    .range::<[u8], _>((lower, upper))
                    .filter_map(|(key, versions)| visible(versions, self.sequence).map(|value| (key.clone(), value)))
                    .collect()
            }
        };

        Ok(CollectedIter::new(entries))
    }
}

/// An iterator over the entries a [`MemReader`] sees, collected when it was opened.
pub type MemIter = CollectedIter<Arc<[u8]>>;

enum Operation {
    Put(Column, Box<[u8]>, Arc<[u8]>),
    Delete(Column, Box<[u8]>),
    DeleteRange(Column, Box<[u8]>, Box<[u8]>),
}

/// Buffers operations and applies them at one new sequence number on write.
pub struct MemBatch {
    kv: MemKv,
    operations: Vec<Operation>,
}

impl KvRawBatch for MemBatch {
    fn put(&mut self, column: Column, key: &[u8], value: &[u8]) {
        self.operations.push(Operation::Put(column, key.into(), value.into()));
    }

    fn delete(&mut self, column: Column, key: &[u8]) {
        self.operations.push(Operation::Delete(column, key.into()));
    }

    fn delete_range(&mut self, column: Column, start: &[u8], end: &[u8]) {
        self.operations
            .push(Operation::DeleteRange(column, start.into(), end.into()));
    }

    fn count(&self) -> usize {
        self.operations.len()
    }

    fn clear(&mut self) {
        self.operations.clear();
    }

    fn write(&mut self, flags: WriteFlags) -> KvResult<()> {
        let mut state = self.kv.state.write();
        state.sequence += 1;
        let sequence = state.sequence;
        let operations = self.operations.len();

        for operation in self.operations.drain(..) {
            match operation {
                Operation::Put(column, key, value) => push(&mut state, column, key, sequence, Some(value)),
                Operation::Delete(column, key) => push(&mut state, column, key, sequence, None),
                Operation::DeleteRange(column, start, end) => {
                    if start >= end {
                        continue;
                    }
                    let keys: Vec<Box<[u8]>> = state.columns[column.index()]
                        .range::<[u8], _>((Bound::Included(&*start), Bound::Excluded(&*end)))
                        .filter(|(_, versions)| visible(versions, sequence).is_some())
                        .map(|(key, _)| key.clone())
                        .collect();
                    for key in keys {
                        push(&mut state, column, key, sequence, None);
                    }
                }
            }
        }

        state.writes.push(MemWrite { flags, operations });
        Ok(())
    }
}

fn push(state: &mut State, column: Column, key: Box<[u8]>, sequence: u64, value: Option<Arc<[u8]>>) {
    let versions = state.columns[column.index()].entry(key).or_default();
    match versions.last_mut() {
        Some(last) if last.sequence == sequence => last.value = value,
        _ => versions.push(Version { sequence, value }),
    }
}

#[cfg(test)]
mod tests {
    use super::{MemKv, MemWrite};
    use crate::kv::{Column, GetInto, IterOptions, KvIterator, KvReader, KvStore, ReadFlags, WriteBatch, WriteFlags};
    use rstest::rstest;

    fn get(kv: &MemKv, column: Column, key: &[u8]) -> Option<Vec<u8>> {
        kv.get_with(column, key, <[u8]>::to_vec).unwrap()
    }

    fn put_all(kv: &MemKv, entries: &[(Column, &[u8], &[u8])]) {
        let mut batch = kv.write_batch().unwrap();
        for (column, key, value) in entries {
            batch.put(*column, key, value, WriteFlags::NONE).unwrap();
        }
        batch.commit().unwrap();
    }

    fn collect(iter: &mut impl KvIterator, forward: bool) -> Vec<(Vec<u8>, Vec<u8>)> {
        let mut entries = Vec::new();
        while iter.valid() {
            entries.push((iter.key().to_vec(), iter.value().to_vec()));
            if forward { iter.next() } else { iter.prev() }
        }
        entries
    }

    #[test]
    fn columns_are_independent_and_reads_copy_or_borrow() {
        let kv = MemKv::new();
        put_all(
            &kv,
            &[
                (Column::Account, b"\x01", b"account"),
                (Column::Storage, b"\x01", b"storage-value"),
            ],
        );

        let mut buffer = [0u8; 8];
        assert_eq!(
            kv.get(Column::Account, b"\x01", &mut buffer).unwrap(),
            GetInto::Found(7)
        );
        assert_eq!(&buffer[..7], b"account");
        assert_eq!(
            kv.get(Column::Storage, b"\x01", &mut buffer).unwrap(),
            GetInto::BufferTooSmall(13)
        );
        assert_eq!(
            kv.get(Column::StateNodes, b"\x01", &mut buffer).unwrap(),
            GetInto::NotFound
        );
        assert_eq!(
            get(&kv, Column::Storage, b"\x01").as_deref(),
            Some(&b"storage-value"[..])
        );
        assert_eq!(get(&kv, Column::Metadata, b"\x01"), None);
    }

    #[test]
    fn readers_see_their_snapshot_and_deletes_hide_values() {
        let kv = MemKv::new();
        put_all(&kv, &[(Column::Account, b"a", b"1"), (Column::Account, b"b", b"1")]);
        let before = kv.reader(false).unwrap();

        let mut batch = kv.write_batch().unwrap();
        batch.put(Column::Account, b"a", b"2", WriteFlags::NONE).unwrap();
        batch.delete(Column::Account, b"b", WriteFlags::NONE).unwrap();
        batch.commit().unwrap();
        let after = kv.reader(false).unwrap();

        let read = |reader: &super::MemReader, key: &[u8]| {
            reader
                .get_with(Column::Account, key, ReadFlags::NONE, <[u8]>::to_vec)
                .unwrap()
        };
        assert_eq!(read(&before, b"a").as_deref(), Some(&b"1"[..]));
        assert_eq!(read(&before, b"b").as_deref(), Some(&b"1"[..]));
        assert_eq!(read(&after, b"a").as_deref(), Some(&b"2"[..]));
        assert_eq!(read(&after, b"b"), None);
        assert_eq!(get(&kv, Column::Account, b"b"), None);
    }

    #[rstest]
    #[case(None, None, "abcd")]
    #[case(Some(&b"b"[..]), Some(&b"d"[..]), "bc")]
    #[case(Some(&b"bb"[..]), None, "cd")]
    #[case(Some(&b"d"[..]), Some(&b"b"[..]), "")]
    fn iterators_respect_bounds_in_both_directions(
        #[case] lower_bound: Option<&[u8]>,
        #[case] upper_bound: Option<&[u8]>,
        // One single-byte key per character.
        #[case] expected_keys: &str,
    ) {
        let kv = MemKv::new();
        put_all(
            &kv,
            &[
                (Column::Storage, b"a", b"1"),
                (Column::Storage, b"b", b"2"),
                (Column::Storage, b"c", b"3"),
                (Column::Storage, b"d", b"4"),
                (Column::Account, b"bb", b"other column"),
            ],
        );
        let reader = kv.reader(false).unwrap();
        let options = IterOptions {
            lower_bound,
            upper_bound,
            flags: ReadFlags::NONE,
        };
        let mut iter = reader.iter(Column::Storage, options).unwrap();

        iter.seek_to_first();
        let forward: Vec<Vec<u8>> = collect(&mut iter, true).into_iter().map(|(key, _)| key).collect();
        iter.seek_to_last();
        let mut backward: Vec<Vec<u8>> = collect(&mut iter, false).into_iter().map(|(key, _)| key).collect();
        backward.reverse();

        let expected: Vec<Vec<u8>> = expected_keys.bytes().map(|key| vec![key]).collect();
        assert_eq!(forward, expected);
        assert_eq!(backward, expected);
    }

    #[test]
    fn seek_lands_on_the_ceiling_and_seek_for_prev_on_the_floor() {
        let kv = MemKv::new();
        put_all(
            &kv,
            &[(Column::StateNodes, b"\x10", b"x"), (Column::StateNodes, b"\x30", b"y")],
        );
        let reader = kv.reader(false).unwrap();
        let mut iter = reader.iter(Column::StateNodes, IterOptions::default()).unwrap();

        iter.seek(b"\x20");
        assert_eq!(iter.key(), b"\x30");
        iter.seek_for_prev(b"\x20");
        assert_eq!(iter.key(), b"\x10");
        iter.seek(b"\x31");
        assert!(!iter.valid());
        iter.seek_for_prev(b"\x0f");
        assert!(!iter.valid());
    }

    #[test]
    fn delete_range_removes_the_half_open_range_of_one_column() {
        let kv = MemKv::new();
        put_all(
            &kv,
            &[
                (Column::Storage, b"a", b"1"),
                (Column::Storage, b"b", b"2"),
                (Column::Storage, b"c", b"3"),
                (Column::Account, b"b", b"4"),
            ],
        );

        let mut batch = kv.write_batch().unwrap();
        batch.delete_range(Column::Storage, b"b", b"c");
        batch.delete_range(Column::Storage, b"c", b"a");
        batch.commit().unwrap();

        assert_eq!(get(&kv, Column::Storage, b"a").as_deref(), Some(&b"1"[..]));
        assert_eq!(get(&kv, Column::Storage, b"b"), None);
        assert_eq!(get(&kv, Column::Storage, b"c").as_deref(), Some(&b"3"[..]));
        assert_eq!(get(&kv, Column::Account, b"b").as_deref(), Some(&b"4"[..]));
    }

    /// `RocksDbWriteBatch` writes out every 256th no-WAL write and commits with the flags of the last write.
    #[rstest]
    #[case(WriteFlags::NONE, 600, &[MemWrite { flags: WriteFlags::NONE, operations: 601 }])]
    #[case(WriteFlags::DISABLE_WAL, 255, &[MemWrite { flags: WriteFlags::NONE, operations: 256 }])]
    #[case(
        WriteFlags::LOW_PRIORITY_AND_NO_WAL,
        600,
        &[
            MemWrite { flags: WriteFlags::LOW_PRIORITY_AND_NO_WAL, operations: 256 },
            MemWrite { flags: WriteFlags::LOW_PRIORITY_AND_NO_WAL, operations: 256 },
            MemWrite { flags: WriteFlags::NONE, operations: 89 },
        ]
    )]
    fn no_wal_writes_are_chunked_and_commit_uses_the_last_flags(
        #[case] flags: WriteFlags,
        #[case] writes: u32,
        #[case] expected: &[MemWrite],
    ) {
        let kv = MemKv::new();
        let mut batch: WriteBatch<_> = kv.write_batch().unwrap();
        for i in 0..writes {
            batch.put(Column::Account, &i.to_be_bytes(), b"v", flags).unwrap();
        }
        // The state pointer goes last with default flags, as `BasePersistence.SetCurrentState` writes it.
        batch.put(Column::Metadata, b"state", b"s", WriteFlags::NONE).unwrap();

        let visible_before_commit = get(&kv, Column::Account, &0u32.to_be_bytes()).is_some();
        batch.commit().unwrap();

        assert_eq!(kv.writes(), expected);
        assert_eq!(visible_before_commit, expected.len() > 1);
        assert_eq!(
            get(&kv, Column::Account, &(writes - 1).to_be_bytes()).as_deref(),
            Some(&b"v"[..])
        );
    }

    /// A delete is `Set(key, null, flags)`: it takes part in the chunking and its flags become the commit flags, so
    /// a trailing `IWriteBatch.Remove` commits the rest with the write-ahead log.
    #[rstest]
    #[case(
        WriteFlags::DISABLE_WAL,
        &[
            MemWrite { flags: WriteFlags::DISABLE_WAL, operations: 256 },
            MemWrite { flags: WriteFlags::DISABLE_WAL, operations: 0 },
        ]
    )]
    #[case(WriteFlags::NONE, &[MemWrite { flags: WriteFlags::NONE, operations: 256 }])]
    fn deletes_count_like_puts(#[case] delete_flags: WriteFlags, #[case] expected: &[MemWrite]) {
        let kv = MemKv::new();
        let mut batch = kv.write_batch().unwrap();
        for i in 0..255u32 {
            batch
                .put(Column::Account, &i.to_be_bytes(), b"v", WriteFlags::DISABLE_WAL)
                .unwrap();
        }
        batch.delete(Column::Account, b"gone", delete_flags).unwrap();
        batch.commit().unwrap();

        assert_eq!(kv.writes(), expected);
    }

    #[test]
    fn dropping_a_batch_discards_what_was_not_written_out() {
        let kv = MemKv::new();
        let mut batch = kv.write_batch().unwrap();
        batch.put(Column::Account, b"k", b"v", WriteFlags::NONE).unwrap();
        assert_eq!(batch.pending(), 1);
        drop(batch);

        assert_eq!(get(&kv, Column::Account, b"k"), None);
        assert!(kv.writes().is_empty());
    }
}
