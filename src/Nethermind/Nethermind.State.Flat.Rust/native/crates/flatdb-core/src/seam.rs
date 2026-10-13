//! Counters that decompose the cost of the FFI seam: the calls of every export of the C ABI and where the reads of
//! the read-only bundles were served from, with the time spent in each export when timing is on.
//!
//! The counters are process-wide sums of per-thread blocks: a thread counts into a block of its own with plain loads
//! and stores, so the hot reads of many threads never share a cache line, and [`totals`] sums every block. A block
//! outlives its thread, which hands it to the next thread to start counting, so nothing counted is lost. While timing
//! is on, a block is never handed on, so [`by_thread`] tells apart every thread that started counting since.

use std::cell::Cell;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::time::Instant;

use parking_lot::Mutex;

/// The number of counters; [`BundleRead`] takes the first ones, the exports of the C ABI the ones after them.
pub const CAPACITY: usize = 96;

/// Where a read of a [`crate::bundle::ReadOnlySnapshotBundle`] or a [`crate::scope_bundle::ScopeBundle`] was served
/// from, after the read-time labels of `ReadOnlySnapshotBundle.cs` (`Metrics.ReadOnlySnapshotBundleTimes`), then the
/// layers only a scope has.
///
/// The C# bundle of the Rust backend has no snapshots of its own, so it labels every read the native bundle serves as
/// a persistence read and counts every trie node as loaded from the database; these counters tell the snapshot hits
/// apart.
#[repr(usize)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum BundleRead {
    /// `account_snapshot`.
    AccountSnapshot,
    /// `account_persistence`.
    AccountPersistence,
    /// `account_persistence_null`.
    AccountPersistenceNull,
    /// `storage_snapshot`.
    StorageSnapshot,
    /// A slot a self-destruct in a snapshot cleared, which C# answers without an observation.
    StorageSelfDestructed,
    /// `storage_persistence`.
    StoragePersistence,
    /// `storage_persistence_null`: absent or zero.
    StoragePersistenceNull,
    /// `state_node_snapshot`, which C# also counts as `LoadedFromCacheNodesCount`.
    StateNodeSnapshot,
    /// `state_rlp`, which C# also counts as `LoadedFromDbNodesCount`; includes placeholders a snapshot shadows with.
    StateNodePersistence,
    /// `storage_node_snapshot`.
    StorageNodeSnapshot,
    /// `storage_rlp`.
    StorageNodePersistence,
    /// An account in the content a scope is writing.
    AccountCurrent,
    /// An account in a snapshot a scope committed, or one of its seed.
    AccountOwn,
    StorageCurrent,
    StorageOwn,
    StateNodeCurrent,
    StateNodeOwn,
    StorageNodeCurrent,
    StorageNodeOwn,
}

impl BundleRead {
    pub const COUNT: usize = 19;
    pub const NAMES: [&str; Self::COUNT] = [
        "bundle_account_snapshot",
        "bundle_account_persistence",
        "bundle_account_persistence_null",
        "bundle_storage_snapshot",
        "bundle_storage_self_destructed",
        "bundle_storage_persistence",
        "bundle_storage_persistence_null",
        "bundle_state_node_snapshot",
        "bundle_state_node_persistence",
        "bundle_storage_node_snapshot",
        "bundle_storage_node_persistence",
        "bundle_account_current",
        "bundle_account_own",
        "bundle_storage_current",
        "bundle_storage_own",
        "bundle_state_node_current",
        "bundle_state_node_own",
        "bundle_storage_node_current",
        "bundle_storage_node_own",
    ];

    pub fn count(self) {
        record(self as usize, None);
    }
}

/// One thread's counters, written by that thread only.
struct Block {
    /// The OS id of the thread counting into the block, 0 when unknown.
    os_thread_id: AtomicU64,
    counts: [AtomicU64; CAPACITY],
    nanos: [AtomicU64; CAPACITY],
}

/// The counters of one thread, as [`by_thread`] reports them.
pub struct ThreadCounters {
    pub os_thread_id: u64,
    pub counts: [u64; CAPACITY],
    pub nanos: [u64; CAPACITY],
}

/// Every block ever handed out, for [`totals`].
static BLOCKS: Mutex<Vec<&'static Block>> = Mutex::new(Vec::new());
/// The blocks of threads that exited, for the next threads to start counting.
static FREE: Mutex<Vec<&'static Block>> = Mutex::new(Vec::new());
static TIMING: AtomicBool = AtomicBool::new(false);

thread_local! {
    static BLOCK: Cell<Option<&'static Block>> = const { Cell::new(None) };
    static RELEASE: ReleaseOnExit = const { ReleaseOnExit };
}

/// Returns the thread's block to [`FREE`] when the thread exits.
struct ReleaseOnExit;

impl Drop for ReleaseOnExit {
    fn drop(&mut self) {
        if let Some(block) = BLOCK.with(|block| block.take()) {
            FREE.lock().push(block);
        }
    }
}

/// Whether [`record`] measures time; off by default, as measuring costs two clock reads per call.
pub fn timing_enabled() -> bool {
    TIMING.load(Ordering::Relaxed)
}

pub fn set_timing(enabled: bool) {
    TIMING.store(enabled, Ordering::Relaxed);
}

/// Counts one event of counter `index` and, with `started`, the time since it.
///
/// # Panics
/// When `index` is not below [`CAPACITY`].
pub fn record(index: usize, started: Option<Instant>) {
    let block = BLOCK.with(|block| block.get()).unwrap_or_else(acquire);
    bump(&block.counts[index], 1);
    if let Some(started) = started {
        bump(&block.nanos[index], started.elapsed().as_nanos() as u64);
    }
}

/// A single-writer add: only this thread writes the block, readers only load.
fn bump(counter: &AtomicU64, delta: u64) {
    counter.store(counter.load(Ordering::Relaxed).wrapping_add(delta), Ordering::Relaxed);
}

#[cold]
fn acquire() -> &'static Block {
    // A block handed on would mix two threads in `by_thread`, so with timing on every thread gets a new one.
    let recycled = if timing_enabled() { None } else { FREE.lock().pop() };
    let block = recycled.unwrap_or_else(|| {
        let block: &'static Block = Box::leak(Box::new(Block {
            os_thread_id: AtomicU64::new(0),
            counts: [const { AtomicU64::new(0) }; CAPACITY],
            nanos: [const { AtomicU64::new(0) }; CAPACITY],
        }));
        BLOCKS.lock().push(block);
        block
    });
    block.os_thread_id.store(os_thread_id(), Ordering::Relaxed);
    BLOCK.with(|current| current.set(Some(block)));
    // Touching it registers its destructor, which returns the block when the thread exits. A thread counting while it
    // exits keeps the block, which then stays out of reuse.
    let _ = RELEASE.try_with(|_| {});
    block
}

/// The counts and nanoseconds of every counter, summed over every thread.
pub fn totals() -> ([u64; CAPACITY], [u64; CAPACITY]) {
    let mut counts = [0u64; CAPACITY];
    let mut nanos = [0u64; CAPACITY];
    for block in BLOCKS.lock().iter() {
        for index in 0..CAPACITY {
            counts[index] = counts[index].wrapping_add(block.counts[index].load(Ordering::Relaxed));
            nanos[index] = nanos[index].wrapping_add(block.nanos[index].load(Ordering::Relaxed));
        }
    }
    (counts, nanos)
}

/// The counters of every block, by the OS id of the thread that counts into it, or counted into it last. A thread
/// that started counting while timing was on has a block of its own; a block handed on before holds the counts of the
/// threads before too.
pub fn by_thread() -> Vec<ThreadCounters> {
    BLOCKS
        .lock()
        .iter()
        .map(|block| ThreadCounters {
            os_thread_id: block.os_thread_id.load(Ordering::Relaxed),
            counts: std::array::from_fn(|index| block.counts[index].load(Ordering::Relaxed)),
            nanos: std::array::from_fn(|index| block.nanos[index].load(Ordering::Relaxed)),
        })
        .collect()
}

/// The id `/proc/<pid>/task/<id>` names the calling thread by, 0 where `/proc/thread-self` is missing.
fn os_thread_id() -> u64 {
    std::fs::read_link("/proc/thread-self")
        .ok()
        .and_then(|link| link.file_name()?.to_str()?.parse().ok())
        .unwrap_or(0)
}

/// The counts the calling thread's block holds, which only this thread moves, for tests to count exactly.
pub fn this_thread() -> [u64; CAPACITY] {
    let block = BLOCK.with(|block| block.get()).unwrap_or_else(acquire);
    std::array::from_fn(|index| block.counts[index].load(Ordering::Relaxed))
}

#[cfg(test)]
mod tests {
    use std::thread;
    use std::time::Instant;

    use super::{BundleRead, CAPACITY, by_thread, os_thread_id, record, set_timing, totals};

    #[test]
    fn counts_of_every_thread_add_up_and_outlive_it() {
        // Counters past the bundle reads, which no other test of this binary touches.
        let (counted, timed) = (CAPACITY - 1, CAPACITY - 2);
        let (before, before_nanos) = totals();

        let workers: Vec<_> = (0..4)
            .map(|_| {
                thread::spawn(move || {
                    for _ in 0..1000 {
                        record(counted, None);
                    }
                    record(timed, Some(Instant::now()));
                })
            })
            .collect();
        workers.into_iter().for_each(|worker| worker.join().unwrap());
        BundleRead::StateNodeSnapshot.count();

        let (after, after_nanos) = totals();
        assert_eq!(after[counted] - before[counted], 4000);
        assert_eq!(after[timed] - before[timed], 4);
        assert_eq!(after_nanos[counted], before_nanos[counted], "untimed calls add no time");
        assert!(after[BundleRead::StateNodeSnapshot as usize] > before[BundleRead::StateNodeSnapshot as usize]);
    }

    #[test]
    fn with_timing_on_every_thread_counts_into_a_block_of_its_own() {
        // A counter no other test of this binary touches.
        let counted = CAPACITY - 3;
        set_timing(true);
        let ids: Vec<u64> = (0..3)
            .map(|_| {
                thread::spawn(move || {
                    record(counted, None);
                    record(counted, None);
                    os_thread_id()
                })
                .join()
                .unwrap()
            })
            .collect();
        set_timing(false);

        let threads = by_thread();
        for id in &ids {
            let mine: Vec<_> = threads.iter().filter(|thread| thread.os_thread_id == *id).collect();
            assert_eq!(mine.len(), 1, "one block of thread {id}");
            assert_eq!(mine[0].counts[counted], 2, "the counts of thread {id} alone");
        }
        assert!(ids.iter().all(|id| *id != 0) && ids[0] != ids[1] && ids[1] != ids[2]);
    }
}
