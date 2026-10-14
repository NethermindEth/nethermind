//! Port of `Nethermind.State.Flat/PersistedSnapshots/Storage/BlobArenaManager.cs`: the pool of blob files
//! `blob_NNNN.bin` the trie-node RLP of the base snapshots is appended to, apart from the arena files of the tables.
//!
//! A file's id is stable for its lifetime and is what every node reference carries; many base snapshots append to one
//! file, each writer reserving it by taking it out of the pool of files with room. A new id is minted only when no file
//! has room, up to 65,536 files. The manager's slot holds the file's first lease, the writer and every snapshot naming
//! the file another; the file is deleted when its last lease drops, unless the snapshots kept it for the next session.

use std::path::{Path, PathBuf};
use std::ptr;
use std::sync::Arc;
use std::sync::atomic::Ordering;

use parking_lot::{RwLock, RwLockReadGuard, RwLockWriteGuard};

use super::arena_manager::parse_file_id;
use super::blob_file::BlobFile;
use super::blob_writer::BlobArenaWriter;
use super::pool_set::PoolSet;
use crate::persisted::lock_rank::{self, LockRank, Ranked};
use crate::persisted::reader::BlobSource;
use crate::persisted::{TierError, TierResult};
use crate::stats::TierCounters;

const BLOB_FILE_PREFIX: &str = "blob_";
const BLOB_FILE_EXTENSION: &str = ".bin";
const MAX_FILE_COUNT: usize = u16::MAX as usize + 1;

pub struct BlobManager {
    base_path: PathBuf,
    max_file_size: i64,
    counters: Arc<TierCounters>,
    /// Lookups share the lock; every change of a slot or of the pool takes it exclusively.
    table: RwLock<BlobTable>,
}

struct BlobTable {
    /// By id; the manager's lease of each file.
    files: Vec<Option<Arc<BlobFile>>>,
    /// Files with room that no writer holds.
    mutable: PoolSet<u16>,
    next_file_id: usize,
    disposed: bool,
}

impl BlobManager {
    /// A manager of the blob files in `base_path`, created if missing; call [`BlobManager::initialize`] before any
    /// snapshot is opened.
    pub fn new(base_path: &Path, max_file_size: i64, counters: Arc<TierCounters>) -> std::io::Result<Arc<Self>> {
        std::fs::create_dir_all(base_path)?;
        Ok(Arc::new(Self {
            base_path: base_path.to_owned(),
            max_file_size,
            counters,
            table: RwLock::new(BlobTable {
                files: vec![None; MAX_FILE_COUNT],
                mutable: PoolSet::default(),
                next_file_id: 0,
                disposed: false,
            }),
        }))
    }

    /// `Initialize`: registers the blob files on disk; each frontier is the file length, and a file shorter than the
    /// maximum joins the pool.
    pub fn initialize(&self) -> std::io::Result<()> {
        let mut table = self.write_table();
        for entry in std::fs::read_dir(&self.base_path)? {
            let entry = entry?;
            let Some(id) = entry.file_name().to_str().and_then(parse_blob_file_id) else {
                continue;
            };
            if !entry.file_type()?.is_file() {
                continue;
            }
            let length = entry.metadata()?.len() as i64;
            let max_size = if length > 0 {
                length.max(self.max_file_size)
            } else {
                self.max_file_size
            };
            let file = BlobFile::open(id, &entry.path(), max_size, length, self.counters.clone())?;
            if let Some(replaced) = table.files[usize::from(id)].replace(Arc::new(file)) {
                // C# loses the earlier file object without deleting its file.
                replaced.persist_on_shutdown();
            }
            table.next_file_id = table.next_file_id.max(usize::from(id) + 1);
            if length < self.max_file_size {
                table.mutable.add(id);
            }
        }
        Ok(())
    }

    /// `CreateWriter`: a writer appending to the first pooled file with room for `estimated_size` bytes, dropping
    /// from the pool the files without, else to a new file.
    pub fn create_writer(self: &Arc<Self>, estimated_size: i64) -> TierResult<BlobArenaWriter> {
        let mut table = self.write_table();
        if table.disposed {
            return Err(TierError::InvalidOperation(
                "Cannot access a disposed object.\nObject name: 'BlobArenaManager'.".to_owned(),
            ));
        }

        let mut chosen = None;
        let mut full = Vec::new();
        for id in table.mutable.items() {
            let candidate = table.files[usize::from(id)]
                .as_ref()
                .expect("a pooled blob file is registered");
            if candidate.frontier() + estimated_size <= candidate.max_size() {
                chosen = Some(candidate.clone());
                break;
            }
            full.push(id);
        }
        for id in full {
            table.mutable.remove(id);
        }

        let file = match chosen {
            Some(file) => {
                // Reserve the file; completion or cancellation returns it.
                table.mutable.remove(file.id());
                file
            }
            None => {
                let id = u16::try_from(table.next_file_id).map_err(|_| {
                    TierError::InvalidOperation(format!("Blob arena file id space exhausted ({MAX_FILE_COUNT} files)."))
                })?;
                table.next_file_id += 1;
                let path = self
                    .base_path
                    .join(format!("{BLOB_FILE_PREFIX}{id:04}{BLOB_FILE_EXTENSION}"));
                let file = Arc::new(BlobFile::open(id, &path, self.max_file_size, 0, self.counters.clone())?);
                table.files[usize::from(id)] = Some(file.clone());
                file
            }
        };
        let start_offset = file.frontier();
        Ok(BlobArenaWriter::new(self.clone(), file, start_offset))
    }

    /// `TryLeaseFile`: a lease of the file, `None` when the id has no file.
    pub fn try_lease_file(&self, id: u16) -> Option<Arc<BlobFile>> {
        self.read_table().files[usize::from(id)].clone()
    }

    /// The release of a snapshot's lease (`PersistedSnapshot.CleanUp`): when only the manager's lease is left, the
    /// orphaned file is reset to empty for reuse.
    pub fn release_lease(&self, file: Arc<BlobFile>) {
        let mut table = self.write_table();
        let registered = table.files[usize::from(file.id())]
            .as_ref()
            .is_some_and(|slot| ptr::eq(Arc::as_ptr(slot), Arc::as_ptr(&file)));
        let id = file.id();
        drop(file);
        if registered && !table.disposed {
            self.try_reset_orphaned_frontier(&mut table, id);
        }
    }

    /// `TryResetOrphanedFrontier`: truncates a file only the manager holds back to empty and returns it to the pool.
    /// A file kept for the next session is left as it is, or its bytes would be lost.
    fn try_reset_orphaned_frontier(&self, table: &mut BlobTable, id: u16) {
        let Some(file) = &table.files[usize::from(id)] else {
            return;
        };
        if Arc::strong_count(file) != 1 || file.is_shutdown_preserved() {
            return;
        }
        let file = file.clone();
        let previous = file.reported_frontier.load(Ordering::Relaxed);
        if previous == 0 {
            table.mutable.add(id);
            return;
        }

        // Out of the pool while the frontier changes; truncating under the lock keeps a writer from appending first.
        // Truncation frees the disk blocks and the cached pages of the whole file at once.
        table.mutable.remove(id);
        if let Err(error) = file.set_len(0) {
            log::warn!("Could not truncate the orphaned blob file {id}: {error}");
        }
        file.frontier.store(0, Ordering::Release);
        file.reported_frontier.store(0, Ordering::Relaxed);
        self.counters
            .blob_allocated_bytes
            .fetch_sub(previous, Ordering::Relaxed);
        table.mutable.add(id);
    }

    /// `SweepUnreferenced`: deletes the files no loaded snapshot leases, the orphans of a crash between a write and
    /// its catalog row.
    pub fn sweep_unreferenced(&self) {
        let mut table = self.write_table();
        if table.disposed {
            return;
        }
        for id in 0..MAX_FILE_COUNT {
            if table.files[id]
                .as_ref()
                .is_some_and(|file| Arc::strong_count(file) == 1)
            {
                table.files[id] = None;
                table.mutable.remove(id as u16);
            }
        }
    }

    /// `OnWriteCompleted`: returns a file with room to the pool and publishes its new frontier.
    pub(super) fn on_write_completed(&self, file: &BlobFile, has_headroom: bool) {
        let mut table = self.write_table();
        if has_headroom {
            table.mutable.add(file.id());
        }
        let frontier = file.frontier();
        let delta = frontier - file.reported_frontier.swap(frontier, Ordering::Relaxed);
        self.counters.blob_allocated_bytes.fetch_add(delta, Ordering::Relaxed);
    }

    /// `OnWriteCancelled`: the frontier did not move, so the file still has room.
    pub(super) fn on_write_cancelled(&self, id: u16) {
        self.write_table().mutable.add(id);
    }

    /// `Dispose`, idempotent: drops the manager's lease of every file. A file a snapshot still leases goes when that
    /// lease does, kept on disk if the snapshot was marked for the next session.
    pub fn dispose(&self) {
        let files = {
            let mut table = self.write_table();
            if table.disposed {
                return;
            }
            table.disposed = true;
            table.files.iter_mut().filter_map(Option::take).collect::<Vec<_>>()
        };
        drop(files);
    }

    fn read_table(&self) -> Ranked<RwLockReadGuard<'_, BlobTable>> {
        lock_rank::lock(LockRank::BlobTable, || self.table.read())
    }

    fn write_table(&self) -> Ranked<RwLockWriteGuard<'_, BlobTable>> {
        lock_rank::lock(LockRank::BlobTable, || self.table.write())
    }
}

impl Drop for BlobManager {
    fn drop(&mut self) {
        self.dispose();
    }
}

impl BlobSource for BlobManager {
    /// `GetFile(id).RandomRead`, for callers that already lease the file.
    fn random_read(&self, blob_arena_id: u16, offset: i32, buffer: &mut [u8]) -> TierResult<usize> {
        let table = self.read_table();
        let file = table
            .files
            .get(usize::from(blob_arena_id))
            .and_then(Option::as_ref)
            .ok_or_else(|| {
                TierError::InvalidOperation(format!("Blob arena {blob_arena_id} not registered with this manager."))
            })?;
        Ok(file.random_read(offset as u64, buffer)?)
    }
}

fn parse_blob_file_id(file_name: &str) -> Option<u16> {
    let digits = file_name
        .strip_suffix(BLOB_FILE_EXTENSION)?
        .strip_prefix(BLOB_FILE_PREFIX)?;
    u16::try_from(parse_file_id(digits)?).ok()
}
