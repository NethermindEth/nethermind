//! Port of `Nethermind.State.Flat/PersistedSnapshots/Storage/ArenaManager.cs`, its nested `PageResidencyAdvisor`
//! included: the pool of arena files the tables are written into.
//!
//! Shared files pack many reservations at OS-page-aligned offsets, the Small ones (`PersistedBase`,
//! `PersistedSmallCompacted`) in `small_arena_NNNN.bin` apart from the larger tiers' `arena_NNNN.bin`; a table whose
//! estimate reaches the dedicated threshold gets a `dedicated_NNNN.bin` of its own. A writer reserves a shared file by
//! taking it out of its pool, which takes it back when room remains. A file whose released bytes reach its frontier is
//! dropped, and deleted once its last reservation goes.
//!
//! With a page-cache budget, every read of a reservation is recorded by the [`PageResidencyTracker`]; a page it evicts
//! is queued to the `fdb-page-advisor` thread, which drops it with `madvise(MADV_DONTNEED)` unless it was read again,
//! then refreshes two resident pages so the kernel's LRU does not take them instead. That thread also publishes the
//! tracker gauges every second.

use std::collections::{BTreeSet, HashMap};
use std::io;
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, AtomicI64, Ordering};
use std::thread::JoinHandle;
use std::time::{Duration, Instant};

use crossbeam_channel::{Receiver, Sender, TrySendError};
use parking_lot::Mutex;

use super::arena_file::ArenaFile;
use super::arena_reservation::ArenaReservation;
use super::arena_writer::ArenaWriter;
use super::page_tracker::PageResidencyTracker;
use super::pool_set::PoolSet;
use crate::persisted::codec::{CatalogEntry, SnapshotLocation};
use crate::persisted::sys::{PunchHoleOutcome, os_page_size};
use crate::persisted::{TierError, TierResult};
use crate::stats::TierCounters;

const ARENA_FILE_PREFIX: &str = "arena_";
const SMALL_ARENA_FILE_PREFIX: &str = "small_arena_";
const DEDICATED_ARENA_FILE_PREFIX: &str = "dedicated_";
const ARENA_FILE_EXTENSION: &str = ".bin";
/// The eviction ring holds at least 128 cache lines of 8-byte entries.
const MIN_RING_ENTRIES: usize = 128 * 64 / 8;
const METRICS_PERIOD: Duration = Duration::from_secs(1);

/// The `IFlatDbConfig` members the arena manager reads.
#[derive(Clone, Copy, Debug)]
pub struct ArenaConfig {
    /// `ArenaFileSizeBytes`: the mapped size of a shared file.
    pub arena_file_size_bytes: i64,
    /// `PersistedSnapshotDedicatedArenaThresholdBytes`.
    pub dedicated_arena_threshold_bytes: i64,
    /// `PersistedSnapshotArenaPageCacheBytes`: the page-tracker budget; 0 disables the tracker and the advisor.
    pub page_cache_bytes: i64,
    /// `PersistedSnapshotPunchHoleOnReclaim`.
    pub punch_hole_on_reclaim: bool,
}

/// The arena manager. Dropping it disposes it, as does [`ArenaManager::dispose`].
pub struct ArenaManager {
    shared: Arc<ArenaShared>,
    advisor_thread: Mutex<Option<(Sender<()>, JoinHandle<()>)>>,
}

/// What the reservations, the writers and the advisor thread share with the manager.
pub(super) struct ArenaShared {
    base_path: PathBuf,
    config: ArenaConfig,
    /// Cleared on the first `Unsupported` from the kernel, so later reclaims stop trying.
    punch_hole_supported: AtomicBool,
    pool: Mutex<ArenaPool>,
    tracker: PageResidencyTracker,
    advisor: Option<Advisor>,
    pub(super) counters: Arc<TierCounters>,
}

struct ArenaPool {
    arenas: HashMap<i32, Arc<ArenaFile>>,
    /// Shared files with room that no writer holds.
    mutable: PoolSet<i32>,
    mutable_small: PoolSet<i32>,
    next_arena_id: i32,
    disposed: bool,
}

impl ArenaPool {
    fn pool_for(&mut self, small: bool) -> &mut PoolSet<i32> {
        if small {
            &mut self.mutable_small
        } else {
            &mut self.mutable
        }
    }
}

/// The eviction queue of `PageResidencyAdvisor` and its counters, which never decrease.
struct Advisor {
    sender: Sender<u64>,
    receiver: Receiver<u64>,
    queued: AtomicI64,
    inline_fallback: AtomicI64,
    skipped_retouched: AtomicI64,
    dispatched: AtomicI64,
    refreshed: AtomicI64,
}

impl ArenaManager {
    /// A manager of the arena files in `base_path`, created if missing; call [`ArenaManager::initialize`] before use.
    pub fn new(base_path: &Path, config: ArenaConfig, counters: Arc<TierCounters>) -> io::Result<Self> {
        std::fs::create_dir_all(base_path)?;
        let tracker = PageResidencyTracker::from_byte_budget(config.page_cache_bytes);
        counters
            .page_tracker_metadata_bytes
            .store(tracker.metadata_bytes(), Ordering::Relaxed);
        let advisor = (tracker.max_capacity() > 0).then(|| {
            // About 1% of the tracker's slots, rounded up to a power of two.
            let (sender, receiver) =
                crossbeam_channel::bounded(MIN_RING_ENTRIES.max(tracker.max_capacity() / 100).next_power_of_two());
            counters.page_tracker_resident_bytes.store(0, Ordering::Relaxed);
            Advisor {
                sender,
                receiver,
                queued: AtomicI64::new(0),
                inline_fallback: AtomicI64::new(0),
                skipped_retouched: AtomicI64::new(0),
                dispatched: AtomicI64::new(0),
                refreshed: AtomicI64::new(0),
            }
        });
        let shared = Arc::new(ArenaShared {
            base_path: base_path.to_owned(),
            config,
            punch_hole_supported: AtomicBool::new(true),
            pool: Mutex::new(ArenaPool {
                arenas: HashMap::new(),
                mutable: PoolSet::default(),
                mutable_small: PoolSet::default(),
                next_arena_id: 0,
                disposed: false,
            }),
            tracker,
            advisor,
            counters,
        });

        let advisor_thread = match &shared.advisor {
            Some(advisor) => {
                let (exit_sender, exit) = crossbeam_channel::bounded::<()>(0);
                let receiver = advisor.receiver.clone();
                let drained = shared.clone();
                let thread = std::thread::Builder::new()
                    .name("fdb-page-advisor".to_owned())
                    .spawn(move || drained.run_advisor(&receiver, &exit))?;
                Some((exit_sender, thread))
            }
            None => None,
        };
        Ok(Self {
            shared,
            advisor_thread: Mutex::new(advisor_thread),
        })
    }

    /// `Initialize`: registers the arena files on disk, sets their frontiers and released bytes from the catalog
    /// `entries`, and returns the arena ids the entries name without a file. Those ids are never handed out again.
    pub fn initialize(&self, entries: &[CatalogEntry]) -> io::Result<BTreeSet<i32>> {
        self.shared.initialize(entries)
    }

    /// `CreateWriter`: a writer of a table of about `estimated_size` bytes, `small` for the sub-CompactSize tiers.
    pub fn create_writer(&self, estimated_size: i64, small: bool) -> io::Result<ArenaWriter> {
        self.shared.create_writer(estimated_size, small)
    }

    /// `Open`: a reservation of a registered location, for reads.
    pub fn open(&self, location: &SnapshotLocation) -> TierResult<Arc<ArenaReservation>> {
        self.shared.open(location)
    }

    pub fn page_tracker(&self) -> &PageResidencyTracker {
        &self.shared.tracker
    }

    /// `ForgetTrackerRange`: drops the tracked pages wholly inside the range.
    pub fn forget_tracker_range(&self, arena_id: i32, byte_offset: i64, byte_size: i64) {
        self.shared.forget_tracker_range(arena_id, byte_offset, byte_size);
    }

    /// `QueueEviction`: has the advisor drop the page, inline when its queue is full; a no-op without a tracker.
    pub fn queue_eviction(&self, arena_id: i32, page: u32) {
        self.shared.queue_eviction(arena_id, page);
    }

    /// Whether no reclaim has met a filesystem without hole punching yet, whatever the configuration.
    pub fn punch_hole_supported(&self) -> bool {
        self.shared.punch_hole_supported.load(Ordering::Relaxed)
    }

    pub fn evictions_queued(&self) -> i64 {
        self.shared.advisor_counter(|advisor| &advisor.queued)
    }

    pub fn evictions_inline_fallback(&self) -> i64 {
        self.shared.advisor_counter(|advisor| &advisor.inline_fallback)
    }

    pub fn evictions_skipped_retouched(&self) -> i64 {
        self.shared.advisor_counter(|advisor| &advisor.skipped_retouched)
    }

    pub fn evictions_dispatched(&self) -> i64 {
        self.shared.advisor_counter(|advisor| &advisor.dispatched)
    }

    pub fn pages_refreshed(&self) -> i64 {
        self.shared.advisor_counter(|advisor| &advisor.refreshed)
    }

    /// `Dispose`, idempotent: stops the advisor after it dispatched what is queued, then drops the manager's lease of
    /// every file. Released reservations stop reclaiming, so the files of live snapshots survive for the next session.
    pub fn dispose(&self) {
        {
            let mut pool = self.shared.pool.lock();
            if pool.disposed {
                return;
            }
            pool.disposed = true;
        }

        if let Some((exit, thread)) = self.advisor_thread.lock().take() {
            drop(exit);
            if thread.join().is_err() {
                log::error!("The page advisor panicked.");
            }
        }
        if let Some(advisor) = &self.shared.advisor {
            while let Ok(packed) = advisor.receiver.try_recv() {
                self.shared.dispatch_one(advisor, packed);
            }
            self.shared
                .counters
                .page_tracker_pages_refreshed
                .store(advisor.refreshed.load(Ordering::Relaxed), Ordering::Relaxed);
        }

        let arenas = std::mem::take(&mut self.shared.pool.lock().arenas);
        for file in arenas.values() {
            self.shared.report_removed(file);
        }
        drop(arenas);
        let counters = &self.shared.counters;
        counters.page_tracker_resident_bytes.store(0, Ordering::Relaxed);
        counters.page_tracker_metadata_bytes.store(0, Ordering::Relaxed);
    }
}

impl Drop for ArenaManager {
    fn drop(&mut self) {
        self.dispose();
    }
}

impl ArenaShared {
    fn initialize(&self, entries: &[CatalogEntry]) -> io::Result<BTreeSet<i32>> {
        let mut pool = self.pool.lock();
        for entry in std::fs::read_dir(&self.base_path)? {
            let entry = entry?;
            let name = entry.file_name();
            let Some((prefix, arena_id)) = name.to_str().and_then(classify) else {
                continue;
            };
            if !entry.file_type()?.is_file() {
                continue;
            }
            let length = entry.metadata()?.len() as i64;
            let mapped_size = if length > 0 {
                length
            } else {
                self.config.arena_file_size_bytes
            };
            let arena = ArenaFile::open(arena_id, &entry.path(), mapped_size, prefix == SMALL_ARENA_FILE_PREFIX)?;
            if let Some(replaced) = pool.arenas.insert(arena_id, Arc::new(arena)) {
                // C# loses the earlier file object without deleting its file.
                replaced.persist_on_shutdown();
            }
            pool.next_arena_id = pool.next_arena_id.max(arena_id + 1);
        }

        let mut live_sizes: HashMap<i32, i64> = HashMap::new();
        let mut missing = BTreeSet::new();
        for entry in entries {
            let arena_id = entry.location.arena_id;
            // The catalog is not rewritten here, so every id it names stays reserved: reusing one would make its stale
            // entries look serviceable.
            if arena_id >= pool.next_arena_id && arena_id < i32::MAX {
                pool.next_arena_id = arena_id + 1;
            }
            let Some(arena) = pool.arenas.get(&arena_id) else {
                if missing.insert(arena_id) {
                    log::warn!(
                        "Persisted-snapshot catalog references arena {arena_id} with no on-disk file; dropping its entries."
                    );
                }
                continue;
            };
            let end = entry.location.offset + entry.location.size;
            if end > arena.frontier() {
                arena.frontier.store(end, Ordering::Release);
            }
            *live_sizes.entry(arena_id).or_default() += entry.location.size;
        }

        for (arena_id, arena) in &pool.arenas {
            let live = live_sizes.get(arena_id).copied().unwrap_or(0);
            arena.dead_bytes.store(arena.frontier() - live, Ordering::Relaxed);
            self.report_added(arena);
        }
        Ok(missing)
    }

    fn create_writer(self: &Arc<Self>, estimated_size: i64, small: bool) -> io::Result<ArenaWriter> {
        let mut pool = self.pool.lock();
        let dedicated = estimated_size >= self.config.dedicated_arena_threshold_bytes;
        let file = if dedicated {
            self.create_arena_file(&mut pool, estimated_size, true, small)?
        } else {
            self.get_or_create_arena(&mut pool, estimated_size, small)?
        };
        let offset = file.frontier();
        // Reserve the file so no other writer picks it; completion or cancellation returns it when room remains. The
        // pool is the file's, which a dedicated file never enters.
        if !dedicated {
            pool.pool_for(file.is_small()).remove(file.id());
            file.writer_active.store(true, Ordering::Relaxed);
        }
        Ok(ArenaWriter::new(self.clone(), file, dedicated, offset))
    }

    /// `GetOrCreateArena`: the first file of the pool with room, dropping from it the files without, else a new one.
    fn get_or_create_arena(&self, pool: &mut ArenaPool, required_size: i64, small: bool) -> io::Result<Arc<ArenaFile>> {
        let mut chosen = None;
        let mut full = Vec::new();
        for arena_id in pool.pool_for(small).items() {
            let candidate = &pool.arenas[&arena_id];
            if candidate.frontier() + required_size <= candidate.mapped_size() {
                chosen = Some(candidate.clone());
                break;
            }
            full.push(arena_id);
        }
        for arena_id in full {
            pool.pool_for(small).remove(arena_id);
        }
        match chosen {
            Some(file) => Ok(file),
            None => self.create_arena_file(pool, 0, false, small),
        }
    }

    /// `CreateArenaFile`: a new file of `mapped_size` bytes, of the shared size when 0. It enters no pool: the writer
    /// that asked for it returns it on completion.
    fn create_arena_file(
        &self,
        pool: &mut ArenaPool,
        mapped_size: i64,
        dedicated: bool,
        small: bool,
    ) -> io::Result<Arc<ArenaFile>> {
        let mapped_size = if mapped_size == 0 {
            self.config.arena_file_size_bytes
        } else {
            mapped_size
        };
        let arena_id = pool.next_arena_id;
        let prefix = match (dedicated, small) {
            (true, _) => DEDICATED_ARENA_FILE_PREFIX,
            (false, true) => SMALL_ARENA_FILE_PREFIX,
            (false, false) => ARENA_FILE_PREFIX,
        };
        let path = self
            .base_path
            .join(format!("{prefix}{arena_id:04}{ARENA_FILE_EXTENSION}"));
        let arena = Arc::new(ArenaFile::open(arena_id, &path, mapped_size, small)?);
        pool.next_arena_id += 1;
        pool.arenas.insert(arena_id, arena.clone());
        log::debug!(
            "Created arena file {} (mapped {mapped_size} bytes{}{})",
            path.display(),
            if dedicated { ", dedicated" } else { "" },
            if small { ", small" } else { "" }
        );
        self.report_added(&arena);
        Ok(arena)
    }

    fn open(self: &Arc<Self>, location: &SnapshotLocation) -> TierResult<Arc<ArenaReservation>> {
        let file = self
            .pool
            .lock()
            .arenas
            .get(&location.arena_id)
            .cloned()
            .ok_or_else(|| {
                TierError::InvalidOperation(format!(
                    "Arena {} is not registered with this manager.",
                    location.arena_id
                ))
            })?;
        log::debug!(
            "Reserved arena {} [{}, {}) ({} bytes)",
            location.arena_id,
            location.offset,
            location.offset + location.size,
            location.size
        );
        Ok(Arc::new(ArenaReservation::new(
            self.clone(),
            file,
            location.offset,
            location.size,
        )))
    }

    /// `OnWriteCompleted`: publishes the frontier of a completed write under the lock that `mark_dead` reads it under.
    pub(super) fn on_write_completed(&self, file: &Arc<ArenaFile>, new_frontier: i64, has_headroom: bool) {
        let mut pool = self.pool.lock();
        file.writer_active.store(false, Ordering::Relaxed);
        file.frontier.store(new_frontier, Ordering::Release);
        if has_headroom {
            pool.pool_for(file.is_small()).add(file.id());
        }
        let delta = new_frontier - file.reported_frontier.swap(new_frontier, Ordering::Relaxed);
        self.counters.arena_allocated_bytes.fetch_add(delta, Ordering::Relaxed);
    }

    /// `OnWriteCancelledShared`: the frontier did not move, so the file goes back to its pool, unless every byte of it
    /// was released while the writer held it.
    pub(super) fn on_write_cancelled_shared(&self, file: &Arc<ArenaFile>) {
        let mut pool = self.pool.lock();
        file.writer_active.store(false, Ordering::Relaxed);
        let frontier = file.frontier();
        if !pool.disposed && frontier > 0 && file.dead_bytes.load(Ordering::Relaxed) >= frontier {
            self.remove_fully_dead_arena(&mut pool, file);
            return;
        }
        pool.pool_for(file.is_small()).add(file.id());
    }

    /// `OnWriteCancelledDedicated`: forgets the file, which its writer's lease then deletes.
    pub(super) fn on_write_cancelled_dedicated(&self, file: &ArenaFile) {
        let removed = self.pool.lock().arenas.remove(&file.id());
        self.report_removed(file);
        drop(removed);
    }

    /// `MarkDead`: counts a released reservation's footprint and drops the file once nothing of it is live. Returns
    /// whether the file survives; a disposed manager keeps every file, so it reports none surviving and nothing is
    /// reclaimed.
    pub(super) fn mark_dead(&self, file: &Arc<ArenaFile>, dead_size: i64) -> bool {
        let mut pool = self.pool.lock();
        if pool.disposed {
            return false;
        }
        log::debug!("Released arena reservation on arena {} ({dead_size} bytes)", file.id());
        let dead_bytes = file.dead_bytes.fetch_add(dead_size, Ordering::Relaxed) + dead_size;
        if dead_bytes < file.frontier() || file.writer_active.load(Ordering::Relaxed) {
            return true;
        }
        self.remove_fully_dead_arena(&mut pool, file);
        false
    }

    fn remove_fully_dead_arena(&self, pool: &mut ArenaPool, file: &ArenaFile) {
        pool.pool_for(file.is_small()).remove(file.id());
        if let Some(removed) = pool.arenas.remove(&file.id()) {
            log::debug!("Released arena file {} (all {} bytes dead)", file.id(), file.frontier());
            self.report_removed(&removed);
        }
    }

    /// `TryPunchHole`: frees the range's disk blocks when configured and supported; true when it did.
    pub(super) fn try_punch_hole(&self, file: &ArenaFile, offset: i64, size: i64) -> bool {
        if !self.config.punch_hole_on_reclaim || !self.punch_hole_supported.load(Ordering::Relaxed) {
            return false;
        }
        let outcome = file.punch_hole(offset, size);
        if outcome == PunchHoleOutcome::Unsupported {
            self.punch_hole_supported.store(false, Ordering::Relaxed);
        }
        outcome == PunchHoleOutcome::Done
    }

    /// `ForgetTrackerRange`: drops the tracked pages wholly inside the range, as `madvise` rounds it, then refreshes
    /// two resident pages per page actually dropped.
    pub(super) fn forget_tracker_range(&self, arena_id: i32, byte_offset: i64, byte_size: i64) {
        if self.tracker.max_capacity() == 0 || byte_size <= 0 {
            return;
        }
        let page_size = os_page_size() as i64;
        let start_page = (byte_offset + page_size - 1) / page_size;
        let end_page = (byte_offset + byte_size) / page_size;
        // A 4 KiB page index fits 32 bits up to 16 TiB per arena, far past `ArenaFileSizeBytes`.
        debug_assert!(end_page <= i64::from(u32::MAX));
        let forgotten = (start_page..end_page)
            .filter(|&page| self.tracker.forget(arena_id, page as u32))
            .count();
        if forgotten > 0
            && let Some(advisor) = &self.advisor
        {
            self.touch_warm_pages(advisor, forgotten.saturating_mul(2).min(i32::MAX as usize));
        }
    }

    pub(super) fn queue_eviction(&self, arena_id: i32, page: u32) {
        let Some(advisor) = &self.advisor else {
            return;
        };
        let packed = (u64::from(arena_id as u32) << 32) | u64::from(page);
        match advisor.sender.try_send(packed) {
            Ok(()) => {
                advisor.queued.fetch_add(1, Ordering::Relaxed);
            }
            // A full ring falls back to an inline dispatch, so no eviction is lost.
            Err(TrySendError::Full(_) | TrySendError::Disconnected(_)) => {
                advisor.inline_fallback.fetch_add(1, Ordering::Relaxed);
                self.counters
                    .page_tracker_evictions_inline_fallback
                    .fetch_add(1, Ordering::Relaxed);
                self.dispatch_inline(advisor, arena_id, page);
            }
        }
    }

    fn run_advisor(&self, receiver: &Receiver<u64>, exit: &Receiver<()>) {
        let advisor = self
            .advisor
            .as_ref()
            .expect("the advisor thread runs only with an advisor");
        let mut next_tick = Instant::now() + METRICS_PERIOD;
        loop {
            crossbeam_channel::select! {
                recv(receiver) -> packed => {
                    if let Ok(packed) = packed {
                        self.dispatch_one(advisor, packed);
                    }
                }
                recv(exit) -> _ => return,
                default(next_tick.saturating_duration_since(Instant::now())) => {}
            }
            if Instant::now() >= next_tick {
                self.counters
                    .page_tracker_resident_bytes
                    .store(self.tracker.resident_bytes(), Ordering::Relaxed);
                self.counters
                    .page_tracker_pages_refreshed
                    .store(advisor.refreshed.load(Ordering::Relaxed), Ordering::Relaxed);
                next_tick = Instant::now() + METRICS_PERIOD;
            }
        }
    }

    /// `DispatchOne`: skips a page read again since it was queued, as dropping it would only fault it back in.
    fn dispatch_one(&self, advisor: &Advisor, packed: u64) {
        let (arena_id, page) = ((packed >> 32) as u32 as i32, packed as u32);
        if self.tracker.contains_page(arena_id, page) {
            advisor.skipped_retouched.fetch_add(1, Ordering::Relaxed);
            return;
        }
        advisor.dispatched.fetch_add(1, Ordering::Relaxed);
        self.counters
            .page_tracker_evictions_dispatched
            .fetch_add(1, Ordering::Relaxed);
        self.dispatch_inline(advisor, arena_id, page);
    }

    fn dispatch_inline(&self, advisor: &Advisor, arena_id: i32, page: u32) {
        let Some(arena) = self.pool.lock().arenas.get(&arena_id).cloned() else {
            return;
        };
        let page_size = os_page_size() as i64;
        arena.advise_dont_need(i64::from(page) * page_size, page_size);
        // One dropped page, two refreshed.
        self.touch_warm_pages(advisor, 2);
    }

    /// `TouchWarmPages`: reads one byte of up to `target_touches` tracked pages, so a sibling's `MADV_DONTNEED` does
    /// not pull them out of the page cache under memory pressure.
    fn touch_warm_pages(&self, advisor: &Advisor, target_touches: usize) {
        for _ in 0..target_touches {
            let Some((arena_id, page)) = self.tracker.try_pick_resident_page() else {
                return;
            };
            // The lease keeps the mapping alive for the read.
            let Some(arena) = self.pool.lock().arenas.get(&arena_id).cloned() else {
                continue;
            };
            let offset = i64::from(page) * os_page_size() as i64;
            if offset >= arena.mapped_size() {
                continue;
            }
            arena.touch_byte(offset);
            advisor.refreshed.fetch_add(1, Ordering::Relaxed);
        }
    }

    fn advisor_counter(&self, counter: impl FnOnce(&Advisor) -> &AtomicI64) -> i64 {
        self.advisor
            .as_ref()
            .map_or(0, |advisor| counter(advisor).load(Ordering::Relaxed))
    }

    pub(super) fn tracker(&self) -> &PageResidencyTracker {
        &self.tracker
    }

    /// `ReportAdded`: the file and its frontier join the gauges.
    fn report_added(&self, file: &ArenaFile) {
        self.counters.arena_file_count.fetch_add(1, Ordering::Relaxed);
        let frontier = file.frontier();
        file.reported_frontier.store(frontier, Ordering::Relaxed);
        self.counters
            .arena_allocated_bytes
            .fetch_add(frontier, Ordering::Relaxed);
    }

    /// `ReportRemoved`.
    fn report_removed(&self, file: &ArenaFile) {
        self.counters.arena_file_count.fetch_sub(1, Ordering::Relaxed);
        let reported = file.reported_frontier.swap(0, Ordering::Relaxed);
        self.counters
            .arena_allocated_bytes
            .fetch_sub(reported, Ordering::Relaxed);
    }
}

/// The prefix and id of an arena file name, `None` for any other file.
fn classify(file_name: &str) -> Option<(&'static str, i32)> {
    let stem = file_name.strip_suffix(ARENA_FILE_EXTENSION)?;
    // "small_arena_" does not start with "arena_", but the longer prefixes go first to keep the order of C#.
    [DEDICATED_ARENA_FILE_PREFIX, SMALL_ARENA_FILE_PREFIX, ARENA_FILE_PREFIX]
        .into_iter()
        .find_map(|prefix| Some((prefix, parse_file_id(stem.strip_prefix(prefix)?)?)))
}

/// `int.TryParse(…, NumberStyles.None)`: decimal digits only, within `i32`.
pub(super) fn parse_file_id(digits: &str) -> Option<i32> {
    (!digits.is_empty() && digits.bytes().all(|byte| byte.is_ascii_digit()))
        .then(|| digits.parse().ok())
        .flatten()
}

#[cfg(test)]
mod tests {
    use rstest::rstest;

    use super::classify;

    #[rstest]
    #[case("arena_0001.bin", Some(("arena_", 1)))]
    #[case("small_arena_0012.bin", Some(("small_arena_", 12)))]
    #[case("dedicated_12345.bin", Some(("dedicated_", 12345)))]
    #[case("arena_2147483647.bin", Some(("arena_", i32::MAX)))]
    #[case("arena_2147483648.bin", None)]
    #[case("arena_-1.bin", None)]
    #[case("arena_.bin", None)]
    #[case("arena_0001.tmp", None)]
    #[case("blob_0001.bin", None)]
    fn arena_file_names_classify_like_csharp(#[case] name: &str, #[case] expected: Option<(&str, i32)>) {
        assert_eq!(classify(name), expected);
    }
}
