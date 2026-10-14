//! Port of `Nethermind.State.Flat/PersistedSnapshots/Storage/ArenaFile.cs`: one arena file of persisted-snapshot tables,
//! pre-extended to its mapped size, written with positioned writes and read through one read-only mapping advised
//! `MADV_RANDOM`.
//!
//! C# refcounts the file: the manager's entry holds one lease and every reservation another. Here they are the clones
//! of an `Arc`, and the C# `CleanUp` is `Drop`: the mapping is unmapped and the file deleted unless
//! [`ArenaFile::persist_on_shutdown`] was called.
//!
//! Deviation from C# (D-9): [`ArenaFile::truncate`] shrinks a dedicated file but keeps its original, longer mapping
//! instead of unmapping and remapping it. Only `mapped_size` changes; the pages past the new end are never read, as
//! every slice lies inside a reservation and both the reservation footprint and the advisor's warm-page touches read
//! the updated `mapped_size`.

use std::fs::{File, OpenOptions};
use std::io;
use std::os::unix::fs::FileExt;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicI64, Ordering};

use crate::persisted::sys::{self, Advice, Mapping, PunchHoleOutcome};

pub struct ArenaFile {
    id: i32,
    path: PathBuf,
    small: bool,
    file: File,
    mapping: Mapping,
    /// `MappedSize`: the file length, which a truncation lowers below the mapping's length (D-9).
    mapped_size: AtomicI64,
    preserve_on_drop: AtomicBool,
    /// The fields below are the manager's bookkeeping, changed only under its lock.
    /// `Frontier`: where the next reservation starts.
    pub(super) frontier: AtomicI64,
    /// `DeadBytes`: the footprints of the released reservations; the file is dropped once they reach the frontier.
    pub(super) dead_bytes: AtomicI64,
    /// `WriterActive`: a writer holds this shared file, so its bytes past the frontier are not dead yet.
    pub(super) writer_active: AtomicBool,
    /// `ReportedFrontier`: the frontier last added to `ArenaAllocatedBytes`.
    pub(super) reported_frontier: AtomicI64,
}

impl ArenaFile {
    /// Opens or creates the file at `path`, extends it to `mapped_size` (sparse) and maps it.
    pub fn open(id: i32, path: &Path, mapped_size: i64, small: bool) -> io::Result<Self> {
        let file = OpenOptions::new()
            .read(true)
            .write(true)
            .create(true)
            .truncate(false)
            .open(path)?;
        if (file.metadata()?.len() as i64) < mapped_size {
            file.set_len(mapped_size as u64)?;
        }
        let mapping = Mapping::map(&file, 0, mapped_size as usize)?;
        mapping.advise_all(Advice::Random);
        Ok(Self {
            id,
            path: path.to_owned(),
            small,
            file,
            mapping,
            mapped_size: AtomicI64::new(mapped_size),
            preserve_on_drop: AtomicBool::new(false),
            frontier: AtomicI64::new(0),
            dead_bytes: AtomicI64::new(0),
            writer_active: AtomicBool::new(false),
            reported_frontier: AtomicI64::new(0),
        })
    }

    pub fn id(&self) -> i32 {
        self.id
    }

    pub fn path(&self) -> &Path {
        &self.path
    }

    /// `Small`: the file holds sub-CompactSize snapshots, kept apart from the larger tiers.
    pub fn is_small(&self) -> bool {
        self.small
    }

    pub fn mapped_size(&self) -> i64 {
        self.mapped_size.load(Ordering::Acquire)
    }

    pub fn frontier(&self) -> i64 {
        self.frontier.load(Ordering::Acquire)
    }

    /// The `length` bytes at `offset`, which must lie below the file length.
    pub fn bytes(&self, offset: i64, length: i64) -> &[u8] {
        self.mapping.slice(offset as usize, length as usize)
    }

    pub(super) fn write_at(&self, bytes: &[u8], offset: u64) -> io::Result<()> {
        self.file.write_all_at(bytes, offset)
    }

    /// `Truncate`: shrinks a dedicated file to its data before any reservation of it exists.
    pub(super) fn truncate(&self, new_size: i64) -> io::Result<()> {
        if new_size != self.mapped_size() {
            self.file.set_len(new_size as u64)?;
            self.mapped_size.store(new_size, Ordering::Release);
        }
        Ok(())
    }

    /// `AdviseDontNeed`: `madvise(MADV_DONTNEED)` over the whole pages of the range.
    pub fn advise_dont_need(&self, offset: i64, size: i64) {
        self.mapping
            .advise_inward(offset as usize, size as usize, Advice::DontNeed);
    }

    /// `PopulateRead`: pre-faults the whole pages of the range.
    pub fn populate_read(&self, offset: i64, size: i64) {
        self.mapping
            .advise_inward(offset as usize, size as usize, Advice::PopulateRead);
    }

    /// `TouchByte`: reads the byte at `offset`, below the file length, to refresh its page's LRU position.
    pub fn touch_byte(&self, offset: i64) -> u8 {
        self.mapping.touch_byte(offset as usize)
    }

    /// `FadviseDontNeed`: drops the whole pages of the range from the OS file cache.
    pub fn fadvise_dont_need(&self, offset: i64, size: i64) {
        sys::fadvise_dont_need(&self.file, offset, size);
    }

    pub(super) fn punch_hole(&self, offset: i64, size: i64) -> PunchHoleOutcome {
        sys::punch_hole(&self.file, offset, size)
    }

    /// `Fsync`: `fdatasync`, so the catalog never names bytes that are not durable.
    pub fn fsync(&self) -> io::Result<()> {
        self.file.sync_data()
    }

    /// `OpenWholeView`: a separate mapping of `[offset, offset + size)` advised `MADV_NORMAL`, for a sequential scan.
    pub(super) fn open_whole_view(&self, offset: i64, size: i64) -> io::Result<Mapping> {
        let view = Mapping::map(&self.file, offset as u64, size as usize)?;
        view.advise_all(Advice::Normal);
        Ok(view)
    }

    /// `PersistOnShutdown`: keep the file on disk when the last lease drops, for the next session to load.
    pub fn persist_on_shutdown(&self) {
        self.preserve_on_drop.store(true, Ordering::Release);
    }
}

impl Drop for ArenaFile {
    fn drop(&mut self) {
        if !self.preserve_on_drop.load(Ordering::Acquire) {
            // Best effort, as C#. Unlinking before the mapping and descriptor close is fine on Linux.
            let _ = std::fs::remove_file(&self.path);
        }
    }
}
