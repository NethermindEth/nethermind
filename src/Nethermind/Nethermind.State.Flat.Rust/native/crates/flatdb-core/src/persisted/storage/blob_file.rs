//! Port of `Nethermind.State.Flat/PersistedSnapshots/Storage/BlobArenaFile.cs`: one blob file of trie-node RLP, read
//! with `pread` (no mapping, no tracker) and appended to by one writer at a time.
//!
//! C# refcounts the file: the manager's slot holds one lease, the writer and every snapshot naming the file another.
//! Here they are the clones of an `Arc`, and the C# `CleanUp` is `Drop`: the file is deleted unless
//! [`BlobFile::persist_on_shutdown`] was called, and leaves the gauges.

use std::fs::{File, OpenOptions};
use std::io;
use std::os::unix::fs::FileExt;
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, AtomicI64, Ordering};

use crate::persisted::sys;
use crate::stats::TierCounters;

pub struct BlobFile {
    id: u16,
    path: PathBuf,
    /// `MaxSize`: the length writers fill the file up to.
    max_size: i64,
    file: File,
    preserve_on_drop: AtomicBool,
    /// `Frontier`: the file length, where the next write starts; changed under the manager's lock.
    pub(super) frontier: AtomicI64,
    /// `ReportedFrontier`: the frontier last added to `BlobAllocatedBytes`.
    pub(super) reported_frontier: AtomicI64,
    counters: Arc<TierCounters>,
}

impl BlobFile {
    /// Opens or creates the file at `path`, whose data ends at `frontier`. The file is not pre-extended: its length
    /// stays the frontier, which the next session reads back.
    pub(super) fn open(
        id: u16,
        path: &Path,
        max_size: i64,
        frontier: i64,
        counters: Arc<TierCounters>,
    ) -> io::Result<Self> {
        let file = OpenOptions::new()
            .read(true)
            .write(true)
            .create(true)
            .truncate(false)
            .open(path)?;
        counters.blob_file_count.fetch_add(1, Ordering::Relaxed);
        counters.blob_allocated_bytes.fetch_add(frontier, Ordering::Relaxed);
        Ok(Self {
            id,
            path: path.to_owned(),
            max_size,
            file,
            preserve_on_drop: AtomicBool::new(false),
            frontier: AtomicI64::new(frontier),
            reported_frontier: AtomicI64::new(frontier),
            counters,
        })
    }

    /// `BlobArenaId`: the id every node reference into this file carries.
    pub fn id(&self) -> u16 {
        self.id
    }

    pub fn max_size(&self) -> i64 {
        self.max_size
    }

    pub fn frontier(&self) -> i64 {
        self.frontier.load(Ordering::Acquire)
    }

    /// `RandomRead`: fills `buffer` from `offset`, returning the bytes read, fewer at the end of the file.
    pub fn random_read(&self, offset: u64, buffer: &mut [u8]) -> io::Result<usize> {
        let mut total = 0;
        while total < buffer.len() {
            match self.file.read_at(&mut buffer[total..], offset + total as u64) {
                Ok(0) => break,
                Ok(read) => total += read,
                Err(error) if error.kind() == io::ErrorKind::Interrupted => {}
                Err(error) => return Err(error),
            }
        }
        Ok(total)
    }

    pub(super) fn write_at(&self, bytes: &[u8], offset: u64) -> io::Result<()> {
        self.file.write_all_at(bytes, offset)
    }

    /// `FadviseDontNeed`: drops the whole pages of the range from the OS file cache.
    pub fn fadvise_dont_need(&self, offset: i64, size: i64) {
        sys::fadvise_dont_need(&self.file, offset, size);
    }

    /// `FadviseWillNeed`: starts read-ahead of a base snapshot's RLP run before it is scanned.
    pub fn fadvise_will_need(&self, offset: i64, size: i64) {
        sys::fadvise_will_need(&self.file, offset, size);
    }

    /// `Fsync`: `fdatasync`, which also makes the grown length durable.
    pub fn fsync(&self) -> io::Result<()> {
        self.file.sync_data()
    }

    /// `SetFileLength`.
    pub(super) fn set_len(&self, length: i64) -> io::Result<()> {
        self.file.set_len(length as u64)
    }

    /// `PersistOnShutdown`: keep the file on disk when the last lease drops, for the next session to load.
    pub fn persist_on_shutdown(&self) {
        self.preserve_on_drop.store(true, Ordering::Release);
    }

    /// `IsShutdownPreserved`.
    pub fn is_shutdown_preserved(&self) -> bool {
        self.preserve_on_drop.load(Ordering::Acquire)
    }
}

impl Drop for BlobFile {
    fn drop(&mut self) {
        if !self.is_shutdown_preserved() {
            // Best effort, as C#.
            let _ = std::fs::remove_file(&self.path);
        }
        self.counters.blob_file_count.fetch_sub(1, Ordering::Relaxed);
        let reported = self.reported_frontier.swap(0, Ordering::Relaxed);
        self.counters
            .blob_allocated_bytes
            .fetch_sub(reported, Ordering::Relaxed);
    }
}
