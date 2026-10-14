//! Port of `Nethermind.State.Flat/PersistedSnapshots/Storage/ArenaReservation.cs` and of the tracking of
//! `ArenaByteReader.cs`: one table's bytes in an arena file, leasing the file.
//!
//! C# refcounts the reservation; here its leases are the clones of an `Arc`, and the C# `CleanUp` is `Drop`: the range
//! is advised away, counted dead in its file, its disk blocks punched (unless the file goes or the reservation is kept
//! for the next session) and its tracked pages forgotten.

use std::io;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};

use super::arena_file::ArenaFile;
use super::arena_manager::ArenaShared;
use super::page_tracker::TouchOutcome;
use super::whole_read_session::WholeReadSession;
use crate::persisted::codec::SnapshotLocation;
use crate::persisted::io::{ByteReader, PageTouch};
use crate::persisted::sys::{os_page_size, round_up_to_os_page};

pub struct ArenaReservation {
    manager: Arc<ArenaShared>,
    file: Arc<ArenaFile>,
    offset: i64,
    size: i64,
    /// Set by [`ArenaReservation::persist_on_shutdown`]: the range must not be punched, as the next session maps it.
    preserve_on_drop: AtomicBool,
}

impl ArenaReservation {
    pub(super) fn new(manager: Arc<ArenaShared>, file: Arc<ArenaFile>, offset: i64, size: i64) -> Self {
        manager.counters.arena_reservation_count.fetch_add(1, Ordering::Relaxed);
        manager
            .counters
            .arena_reservation_bytes
            .fetch_add(size, Ordering::Relaxed);
        Self {
            manager,
            file,
            offset,
            size,
            preserve_on_drop: AtomicBool::new(false),
        }
    }

    pub fn location(&self) -> SnapshotLocation {
        SnapshotLocation {
            arena_id: self.file.id(),
            offset: self.offset,
            size: self.size,
        }
    }

    pub fn size(&self) -> i64 {
        self.size
    }

    /// The bytes on disk this reservation answers for: its size padded to the OS page where the next shared
    /// reservation starts, so reclaiming them covers whole pages without touching a neighbour; capped at the file, so
    /// a truncated dedicated file reduces it to the size.
    fn footprint(&self) -> i64 {
        round_up_to_os_page(self.size).min(self.file.mapped_size() - self.offset)
    }

    /// The table, unread: the reads of [`ArenaReservation::reader`] are the tracked ones.
    pub fn bytes(&self) -> &[u8] {
        self.file.bytes(self.offset, self.size)
    }

    /// `CreateReader`: a reader of the table reporting every read to the page tracker.
    pub fn reader(&self) -> ByteReader<'_> {
        ByteReader::tracked(self.bytes(), self, self.offset as usize, os_page_size())
    }

    /// `TouchRangePopulate`: tracks the pages of the range, then pre-faults them with one `MADV_POPULATE_READ` when
    /// more than one was not resident; a single cold page is left to the read's own fault.
    pub fn touch_range_populate(&self, local_offset: i64, length: i64) {
        if length <= 0 {
            return;
        }
        let (missed, first_page_base, last_page_end) = self.track_pages(local_offset, length);
        if missed > 1 {
            self.file
                .populate_read(first_page_base, last_page_end - first_page_base);
        }
    }

    /// `TrackPages`: touches every page of the range in the tracker, queues the evicted pages, and returns how many
    /// were not hits along with the page-aligned envelope of the range.
    fn track_pages(&self, local_offset: i64, length: i64) -> (usize, i64, i64) {
        let page_size = os_page_size() as i64;
        let start = self.offset + local_offset;
        let first_page_base = start & !(page_size - 1);
        let last_page_end = (start + length + page_size - 1) & !(page_size - 1);
        let tracker = self.manager.tracker();
        let mut missed = 0;
        for page in first_page_base / page_size..last_page_end / page_size {
            match tracker.try_touch(self.file.id(), page as u32) {
                TouchOutcome::Hit => continue,
                TouchOutcome::Inserted => {}
                TouchOutcome::Evicted(arena_id, evicted_page) => self.manager.queue_eviction(arena_id, evicted_page),
            }
            missed += 1;
        }
        (missed, first_page_base, last_page_end)
    }

    /// `BeginWholeReadSession`: a separate mapping of the table for a sequential scan, leasing this reservation, whose
    /// pages are dropped from the page cache when the session ends.
    pub fn begin_whole_read_session(self: &Arc<Self>) -> io::Result<WholeReadSession> {
        let view = self.file.open_whole_view(self.offset, self.size)?;
        Ok(WholeReadSession::new(self.clone(), view))
    }

    /// `AdviseDontNeed`: drops the footprint from the mapping and the tracker.
    pub fn advise_dont_need(&self) {
        let footprint = self.footprint();
        self.file.advise_dont_need(self.offset, footprint);
        self.forget_tracker_range(footprint);
    }

    /// `ForgetTracker`: forgets the footprint's tracked pages, for when the page cache was already advised.
    pub fn forget_tracker(&self) {
        self.forget_tracker_range(self.footprint());
    }

    /// `AdviseAndFadviseDontNeed`, the demotion of a snapshot that stays readable: drops the footprint from the mapping,
    /// the OS file cache and the tracker, but keeps its disk blocks.
    pub fn advise_and_fadvise_dont_need(&self) {
        let footprint = self.footprint();
        self.file.advise_dont_need(self.offset, footprint);
        self.file.fadvise_dont_need(self.offset, footprint);
        self.forget_tracker_range(footprint);
    }

    /// `Fsync`: makes the arena file durable before the catalog names this reservation.
    pub fn fsync(&self) -> io::Result<()> {
        self.file.fsync()
    }

    /// `PersistOnShutdown`: keeps this range and its file on disk for the next session.
    pub fn persist_on_shutdown(&self) {
        self.preserve_on_drop.store(true, Ordering::Release);
        self.file.persist_on_shutdown();
    }

    fn forget_tracker_range(&self, footprint: i64) {
        self.manager
            .forget_tracker_range(self.file.id(), self.offset, footprint);
    }
}

impl PageTouch for ArenaReservation {
    /// `TouchRange`: records the pages of the read, without pre-faulting them.
    fn touch_range(&self, offset: usize, length: usize) {
        if length > 0 {
            self.track_pages(offset as i64, length as i64);
        }
    }
}

impl Drop for ArenaReservation {
    fn drop(&mut self) {
        let footprint = self.footprint();
        self.file.advise_dont_need(self.offset, footprint);
        let file_survives = self.manager.mark_dead(&self.file, footprint);
        // A file the manager dropped is about to be deleted, so neither punching nor fadvising it is worth it; a
        // successful punch already invalidated the cached pages.
        let preserve = self.preserve_on_drop.load(Ordering::Acquire);
        let punched = !preserve && file_survives && self.manager.try_punch_hole(&self.file, self.offset, footprint);
        if !punched && file_survives {
            self.file.fadvise_dont_need(self.offset, footprint);
        }
        self.forget_tracker_range(footprint);
        let counters = &self.manager.counters;
        counters.arena_reservation_count.fetch_sub(1, Ordering::Relaxed);
        counters.arena_reservation_bytes.fetch_sub(self.size, Ordering::Relaxed);
    }
}
