//! Port of `Nethermind.State.Flat/PersistedSnapshots/PersistedSnapshotCompactor.cs`: logarithmic compaction of the
//! persisted snapshots, bounded by `PersistedSnapshotMaxCompactSize`.
//!
//! [`PersistedSnapshotCompactor::do_compact_snapshot`] merges a block's natural power-of-two window: the
//! sub-`CompactSize` intermediates and the wider merges at large boundaries.
//! [`PersistedSnapshotCompactor::do_compact_compact_sized`] produces the `CompactSize`-wide snapshot a persist writes.
//! A window merges every persisted snapshot assembled within it once there are at least two; it need not be full.
//!
//! The persistence manager hands the states it converted to [`PersistedSnapshotCompactor::enqueue`], which waits for
//! room in a queue of 16 batches. The threads start with the first batch, as C#'s tasks: `fdb-pcompact` takes one batch
//! at a time and compacts its sub-`CompactSize` windows bucket by bucket in ascending size, the states of a bucket in
//! parallel, then its `CompactSize` boundaries; it hands each large boundary to the four `fdb-pcompact-boundary-{n}`
//! threads over a second queue of 16, as those merges can be gigabytes. A failed or panicking compaction is logged, a
//! panicking one also handed to the job panic hook, and the threads go on. Process exit stops the threads between
//! compactions and drops the queued batches; disposal closes the queues and waits for the work queued so far.
//!
//! A catalog write failing with corruption or an I/O error, on which C# `DbOnTheRocks.HandleFatalDbError` exits, is also
//! handed to the fatal error hook, which stops later persists.
//!
//! `PersistedSnapshotCompactions` is incremented atomically, where C#'s `++` races. The configured validation of
//! converted snapshots is not read here, as in C#.

use std::collections::BTreeMap;
use std::convert::Infallible;
use std::panic::{self, AssertUnwindSafe};
use std::sync::Arc;
use std::sync::atomic::Ordering;
use std::thread::{self, JoinHandle};
use std::time::Instant;

use crossbeam_channel::{Receiver, Sender, select};
use parking_lot::Mutex;
use thiserror::Error;

use super::bloom_keys::PersistedBloom;
use super::codec::CatalogEntry;
use super::io::TableSink;
use super::snapshot::{PersistedSnapshot, SnapshotHeader};
use super::sorted::table;
use super::storage::arena_manager::ArenaManager;
use super::storage::blob_manager::BlobManager;
use super::storage::catalog::SnapshotCatalog;
use super::{PersistedTierConfig, TierError, TierFaults, TierResult, merger, parallel};
use crate::bloom::BloomFilter;
use crate::log_ring::{self, SinkId};
use crate::manager::{PanicHook, Signal, panic_message};
use crate::repository::SnapshotRepository;
use crate::schedule::CompactionSchedule;
use crate::snapshot::SnapshotTier;
use crate::stats::{Histogram, TierStats};
use crate::types::StateId;

/// The capacity of the batch queue and of the boundary queue.
const QUEUE_CAPACITY: usize = 16;

/// `BoundaryCompactorWorkerCount`.
const BOUNDARY_WORKER_COUNT: usize = 4;

/// What the compactor reads of the configuration: the schedule of its windows and the tier's settings.
#[derive(Clone, Copy, Debug)]
pub struct CompactorConfig {
    pub schedule: CompactionSchedule,
    pub tier: PersistedTierConfig,
}

/// What the compactor calls back with, from its threads.
pub struct CompactorHooks {
    /// Every catalog failure with corruption or an I/O error, once logged.
    pub on_fatal_error: Box<dyn Fn(TierError) + Send + Sync>,
    /// The name of every compaction that panics, once logged.
    pub on_job_panic: PanicHook,
    /// The faults a test build injects; none in production.
    pub faults: Option<Arc<dyn TierFaults>>,
}

/// Why [`PersistedSnapshotCompactor::enqueue`] did not queue a batch.
#[derive(Clone, Copy, Debug, Error, PartialEq, Eq)]
pub enum EnqueueError {
    /// `ObjectDisposedException`: the compactor is disposed.
    #[error("The persisted snapshot compactor is disposed")]
    Disposed,
    /// `OperationCanceledException`: process exit was signalled.
    #[error("Cancelled by process exit")]
    Cancelled,
}

/// `IPersistedSnapshotCompactor`: where the persistence manager hands the states it converted, and what shutdown asks
/// of the compactor.
pub trait PersistedCompactor: Send + Sync {
    /// `EnqueueAsync`.
    fn enqueue(&self, batch: Vec<StateId>, persisted_block_number: u64) -> Result<(), EnqueueError>;

    /// Process exit started: a waiting enqueue gives up and the queued batches are dropped.
    fn signal_exit(&self);

    /// `DisposeAsync`, idempotent.
    fn dispose(&self);
}

impl PersistedCompactor for PersistedSnapshotCompactor {
    fn enqueue(&self, batch: Vec<StateId>, persisted_block_number: u64) -> Result<(), EnqueueError> {
        PersistedSnapshotCompactor::enqueue(self, batch, persisted_block_number)
    }

    fn signal_exit(&self) {
        PersistedSnapshotCompactor::signal_exit(self);
    }

    fn dispose(&self) {
        PersistedSnapshotCompactor::dispose(self);
    }
}

/// The states one persist converted, and the persisted block then.
type Batch = (Vec<StateId>, u64);

/// A large boundary and the persisted block of its batch.
type BoundaryJob = (StateId, u64);

pub struct PersistedSnapshotCompactor {
    shared: Arc<Shared>,
    workers: Mutex<Workers>,
}

#[derive(Default)]
struct Workers {
    disposed: bool,
    /// The writer of `_compactPersistedJobs`; `None` until the threads start and once disposed.
    batches: Option<Sender<Batch>>,
    threads: Vec<JoinHandle<()>>,
}

struct Shared {
    repository: Arc<SnapshotRepository>,
    arena: Arc<ArenaManager>,
    blobs: Arc<BlobManager>,
    catalog: Arc<SnapshotCatalog>,
    stats: Arc<TierStats>,
    config: CompactorConfig,
    /// `IProcessExitSource.Token`.
    process_exit: Signal,
    hooks: CompactorHooks,
    /// Where the threads log: where the thread creating the compactor does.
    log_sink: Option<SinkId>,
}

impl PersistedSnapshotCompactor {
    /// A compactor over the tier, calling `hooks` back.
    pub fn new(
        repository: Arc<SnapshotRepository>,
        arena: Arc<ArenaManager>,
        blobs: Arc<BlobManager>,
        catalog: Arc<SnapshotCatalog>,
        stats: Arc<TierStats>,
        config: CompactorConfig,
        hooks: CompactorHooks,
    ) -> Self {
        Self {
            shared: Arc::new(Shared {
                repository,
                arena,
                blobs,
                catalog,
                stats,
                config,
                process_exit: Signal::new(),
                hooks,
                log_sink: log_ring::current(),
            }),
            workers: Mutex::default(),
        }
    }

    /// `EnqueueAsync`: queues the states of one persist for compaction, waiting for room; starts the threads first.
    ///
    /// # Panics
    /// When a thread cannot be started.
    pub fn enqueue(&self, batch: Vec<StateId>, persisted_block_number: u64) -> Result<(), EnqueueError> {
        let batches = {
            let mut workers = self.workers.lock();
            if workers.disposed {
                return Err(EnqueueError::Disposed);
            }
            if self.shared.process_exit.is_raised() {
                return Err(EnqueueError::Cancelled);
            }
            self.ensure_started(&mut workers)
        };
        select! {
            send(batches, (batch, persisted_block_number)) -> sent => sent.map_err(|_| EnqueueError::Disposed),
            recv(self.shared.process_exit.receiver()) -> _ => Err(EnqueueError::Cancelled),
        }
    }

    /// `DoCompactSnapshot`: merges the persisted snapshots of the block's natural window ending at `to`, clamped at
    /// `persisted_block_number`; whether a compacted snapshot was added.
    pub fn do_compact_snapshot(&self, to: &StateId, persisted_block_number: u64) -> TierResult<bool> {
        self.shared.do_compact_snapshot(to, persisted_block_number)
    }

    /// `DoCompactCompactSized`: merges the `CompactSize`-wide window ending at the boundary `to`; whether a compacted
    /// snapshot was added.
    pub fn do_compact_compact_sized(&self, to: &StateId) -> TierResult<bool> {
        self.shared.do_compact_compact_sized(to)
    }

    /// Process exit started: a waiting [`PersistedSnapshotCompactor::enqueue`] gives up, the threads stop between
    /// compactions and the queued batches are dropped.
    pub fn signal_exit(&self) {
        self.shared.process_exit.raise();
    }

    /// `DisposeAsync`, idempotent: closes the batch queue and waits for the batches queued so far, then for the
    /// boundary merges they handed over, unless process exit stops them first.
    pub fn dispose(&self) {
        let (batches, threads) = {
            let mut workers = self.workers.lock();
            if workers.disposed {
                return;
            }
            workers.disposed = true;
            (workers.batches.take(), std::mem::take(&mut workers.threads))
        };
        // The batch thread holds the boundary queue's only writer, so the boundary threads end after it.
        drop(batches);
        for thread in threads {
            let name = thread.thread().name().unwrap_or("fdb-pcompact").to_owned();
            if let Err(panic) = thread.join() {
                log::error!("{name} stopped: {}", panic_message(&*panic));
            }
        }
    }

    /// `EnsureStarted`: the writer of the batch queue, starting the batch thread and the boundary threads first.
    fn ensure_started(&self, workers: &mut Workers) -> Sender<Batch> {
        if let Some(batches) = &workers.batches {
            return batches.clone();
        }

        let (batch_sender, batch_receiver) = crossbeam_channel::bounded(QUEUE_CAPACITY);
        let (boundary_sender, boundary_receiver) = crossbeam_channel::bounded(QUEUE_CAPACITY);
        let shared = self.shared.clone();
        workers.threads.push(self.spawn("fdb-pcompact".to_owned(), move || {
            shared.run_persisted_compactor(&batch_receiver, &boundary_sender)
        }));
        for index in 0..BOUNDARY_WORKER_COUNT {
            let (shared, boundaries) = (self.shared.clone(), boundary_receiver.clone());
            workers
                .threads
                .push(self.spawn(format!("fdb-pcompact-boundary-{index}"), move || {
                    shared.run_boundary_compactor(&boundaries)
                }));
        }
        workers.batches = Some(batch_sender.clone());
        batch_sender
    }

    fn spawn(&self, name: String, work: impl FnOnce() + Send + 'static) -> JoinHandle<()> {
        let log_sink = self.shared.log_sink;
        thread::Builder::new()
            .name(name.clone())
            .spawn(move || {
                log_ring::enter(log_sink);
                work();
            })
            .unwrap_or_else(|error| panic!("cannot start {name}: {error}"))
    }
}

impl Drop for PersistedSnapshotCompactor {
    fn drop(&mut self) {
        self.dispose();
    }
}

impl Shared {
    /// `RunPersistedCompactor`: one batch at a time until the queue closes; on process exit the queued batches are
    /// dropped.
    fn run_persisted_compactor(&self, batches: &Receiver<Batch>, boundaries: &Sender<BoundaryJob>) {
        loop {
            if self.process_exit.is_raised() {
                break;
            }
            select! {
                recv(batches) -> batch => match batch {
                    Ok((batch, persisted_block_number)) => {
                        if self.process_exit.is_raised() {
                            break;
                        }
                        self.run_logged(
                            "fdb-pcompact",
                            || "Error compacting persisted snapshot batch".to_owned(),
                            || self.process_compact_batch(&batch, persisted_block_number, boundaries),
                        );
                    }
                    Err(_) => return,
                },
                recv(self.process_exit.receiver()) -> _ => break,
            }
        }
        let dropped = batches.try_iter().count();
        if dropped > 0 {
            log::debug!("Dropped {dropped} queued persisted compaction batch(es) on process exit");
        }
    }

    /// `ProcessCompactBatch`: the sub-`CompactSize` windows bucket by bucket in ascending size, each bucket in
    /// parallel, then every `CompactSize` boundary's CompactSized snapshot, then each large boundary's merge handed to
    /// the boundary threads. Block 0 compacts nothing.
    ///
    /// A failed window of a bucket is logged alone, so the boundaries still get their CompactSized snapshots; a failed
    /// CompactSized merge fails the rest of the batch.
    fn process_compact_batch(
        &self,
        batch: &[StateId],
        persisted_block_number: u64,
        boundaries: &Sender<BoundaryJob>,
    ) -> TierResult<()> {
        let schedule = &self.config.schedule;
        let mut large_boundaries = Vec::new();
        let mut compact_size_boundaries = Vec::new();
        let mut buckets: BTreeMap<u64, Vec<StateId>> = BTreeMap::new();
        for state in batch {
            let block_number = state.block_number;
            if block_number == 0 {
                continue;
            }
            if schedule.is_large_compaction_boundary(block_number) {
                // A large boundary needs the CompactSized snapshot and the wider merge.
                large_boundaries.push(*state);
                compact_size_boundaries.push(*state);
            } else if schedule.is_compact_size_boundary(block_number) {
                compact_size_boundaries.push(*state);
            } else {
                buckets
                    .entry(schedule.get_persisted_snapshot_compact_size(block_number))
                    .or_default()
                    .push(*state);
            }
        }

        // Ascending, so the inputs of each layer, the outputs of the one before, exist when it runs.
        for states in buckets.values() {
            let Ok(()) = parallel::for_each_unbalanced(
                states,
                self.config.tier.processor_count,
                self.process_exit.flag(),
                |state| {
                    self.run_logged(
                        "fdb-pcompact",
                        || format!("Error compacting persisted snapshot {state}"),
                        || self.do_compact_snapshot(state, persisted_block_number).map(drop),
                    );
                    Ok::<(), Infallible>(())
                },
            );
            if self.process_exit.is_raised() {
                return Ok(());
            }
        }

        for boundary in &compact_size_boundaries {
            self.do_compact_compact_sized(boundary)?;
        }

        for boundary in large_boundaries {
            select! {
                send(boundaries, (boundary, persisted_block_number)) -> sent => if sent.is_err() { return Ok(()) },
                recv(self.process_exit.receiver()) -> _ => return Ok(()),
            }
        }
        Ok(())
    }

    /// `RunBoundaryCompactor`: the wider merges of large boundaries, whose CompactSized snapshot the batch thread
    /// already made, until the queue closes or process exit.
    fn run_boundary_compactor(&self, boundaries: &Receiver<BoundaryJob>) {
        let name = thread::current().name().unwrap_or("fdb-pcompact-boundary").to_owned();
        while !self.process_exit.is_raised() {
            select! {
                recv(boundaries) -> job => match job {
                    Ok((boundary, persisted_block_number)) => self.run_logged(
                        &name,
                        || format!("Error compacting boundary persisted snapshot {boundary}"),
                        || self.do_compact_snapshot(&boundary, persisted_block_number).map(drop),
                    ),
                    Err(_) => return,
                },
                recv(self.process_exit.receiver()) -> _ => return,
            }
        }
    }

    /// Runs one compaction job: a failure is logged with `context`, then handed to the fatal error hook when fatal; a
    /// panic is logged and handed to the job panic hook.
    fn run_logged(&self, job: &str, context: impl Fn() -> String, work: impl FnOnce() -> TierResult<()>) {
        match panic::catch_unwind(AssertUnwindSafe(work)) {
            Ok(Ok(())) => {}
            Ok(Err(error)) => {
                log::error!("{}. {error}", context());
                if matches!(&error, TierError::Catalog(error) if error.is_fatal()) {
                    (self.hooks.on_fatal_error)(error);
                }
            }
            Err(panic) => {
                log::error!("{}. {}", context(), panic_message(&*panic));
                (self.hooks.on_job_panic)(job);
            }
        }
    }

    fn do_compact_snapshot(&self, to: &StateId, persisted_block_number: u64) -> TierResult<bool> {
        let block_number = to.block_number;
        let size = self.config.schedule.get_persisted_snapshot_compact_size(block_number);
        // A window of one snapshot has nothing to merge.
        if size <= 1 || self.repository.persisted_snapshot_count() < 2 {
            return Ok(false);
        }

        // Signed: an offset can push a boundary's natural window below genesis, which the walk reads back as such.
        // Clamped at the persisted block: snapshots below it are in the persistence already, and a wide snapshot
        // reaching below it is then left for the narrower edges above it.
        let start = (block_number as i64 - size as i64).max(persisted_block_number as i64) as u64;
        self.compact_range(to, start, size, false)
    }

    fn do_compact_compact_sized(&self, to: &StateId) -> TierResult<bool> {
        let block_number = to.block_number;
        let schedule = &self.config.schedule;
        if !schedule.is_compact_size_boundary(block_number) && !schedule.is_large_compaction_boundary(block_number)
            || self.repository.persisted_snapshot_count() < 2
        {
            return Ok(false);
        }

        // Exactly CompactSize wide at any boundary, and on a persistence boundary, so never below the persisted block.
        let compact_size = schedule.get_compact_size(block_number);
        self.compact_range(to, block_number.wrapping_sub(compact_size), compact_size, true)
    }

    /// `CompactRange`: merges the chain the walk assembles from `to` back toward `start` into one snapshot of the tier
    /// the window calls for, records it durably and indexes it; whether there were two snapshots to merge.
    fn compact_range(&self, to: &StateId, start: u64, compact_size: u64, compact_sized: bool) -> TierResult<bool> {
        let sources = self.repository.assemble_persisted_snapshots_for_compaction(to, start);
        if sources.len() < 2 {
            return Ok(false);
        }
        let (from, to) = (sources[0].from(), sources[sources.len() - 1].to());
        log::debug!(
            "Compacting {} persisted snapshots at block {}, compact size {compact_size}, CompactSized {}",
            sources.len(),
            to.block_number,
            if compact_sized { "True" } else { "False" }
        );

        // One sequential-scan session per source for the whole merge, dropping the scanned pages once it ends.
        let sessions = sources
            .iter()
            .map(|source| source.begin_whole_read_session())
            .collect::<Result<Vec<_>, _>>()?;
        let readers = sessions.iter().map(|session| session.reader()).collect::<Vec<_>>();
        let estimated_size = sources.iter().map(|source| source.size()).sum::<i64>();
        // A bloom shared by several sources counts once.
        let mut bloom_owners: Vec<&Arc<PersistedBloom>> = Vec::new();
        for source in &sources {
            if !bloom_owners
                .iter()
                .any(|owner| Arc::ptr_eq(owner, source.persisted_bloom()))
            {
                bloom_owners.push(source.persisted_bloom());
            }
        }
        let bloom_capacity = bloom_owners
            .iter()
            .map(|bloom| bloom.filter().count() as i64)
            .sum::<i64>();

        let schedule = &self.config.schedule;
        let tier = if compact_sized {
            SnapshotTier::PersistedCompactSized
        } else if schedule.is_large_compaction_boundary(to.block_number) {
            SnapshotTier::PersistedLargeCompacted
        } else {
            SnapshotTier::PersistedSmallCompacted
        };
        // A sub-CompactSize intermediate is read-cold: a one-line bloom, still filled so a wider merge can size from it.
        let bits_per_key = self.config.tier.bloom_bits_per_key;
        let mut bloom = if bits_per_key > 0.0 && bloom_capacity > 0 {
            let capacity = if tier == SnapshotTier::PersistedSmallCompacted {
                1
            } else {
                bloom_capacity
            };
            BloomFilter::new(capacity, bits_per_key).map_err(|error| TierError::InvalidArgument(error.to_string()))?
        } else {
            BloomFilter::always_true()
        };

        let mut writer = self
            .arena
            .create_writer(estimated_size, tier == SnapshotTier::PersistedSmallCompacted)?;
        if let Some(faults) = &self.hooks.faults {
            faults.before_merge();
        }
        let started = Instant::now();
        merger::merge(&readers, &mut writer, &mut bloom)?;
        // A window may fall short of its size, so the label is the merged span rounded up to a power of two.
        let actual_size = to
            .block_number
            .wrapping_sub(from.block_number)
            .checked_next_power_of_two()
            .unwrap_or(0);
        let flat_stats = self.repository.stats();
        flat_stats.observe_compact_size(
            Histogram::PersistedSnapshotCompactedSize,
            actual_size,
            writer.written() as f64,
        );
        flat_stats.observe_compact_size(
            Histogram::PersistedSnapshotCompactTime,
            actual_size,
            started.elapsed().as_nanos() as f64,
        );
        let (location, reservation) = writer.complete()?;

        // The catalog must never name bytes that are not durable. A merge writes no blob bytes: its node references
        // point into the bases' blob files, made durable when they were converted.
        reservation.fsync()?;
        self.catalog.add(&CatalogEntry {
            from,
            to,
            location,
            tier,
        })?;
        let header = SnapshotHeader {
            from,
            to,
            tier,
            may_have_self_destructs: sources.iter().any(|source| source.header().may_have_self_destructs),
        };
        let compacted = Arc::new(PersistedSnapshot::open(
            header,
            reservation,
            PersistedBloom::new(bloom, self.stats.counters.clone()),
            self.blobs.clone(),
            self.stats.clone(),
        )?);
        self.repository.add_persisted_snapshot(compacted.clone());
        if !schedule.is_compact_size_boundary(to.block_number)
            && !schedule.is_large_compaction_boundary(to.block_number)
        {
            // Read only by a deep reorg, so its fresh pages leave the cache and the tracker.
            compacted.demote();
        } else {
            warm_address_column_index(&compacted);
            // A merge at a large boundary covers (from, to] of the canonical chain, so its bloom stands in for the
            // bloom of every snapshot inside.
            if schedule.is_large_compaction_boundary(to.block_number) {
                self.repository
                    .share_bloom_across_range(&from, &to, compacted.persisted_bloom())?;
            }
        }

        self.stats.counters.compactions.fetch_add(1, Ordering::Relaxed);
        Ok(true)
    }
}

/// `WarmAddressColumnIndex`: pre-faults the index block and the footer of a fresh table, its tail, into the page
/// tracker, so its first lookups do not fault page by page through the index.
fn warm_address_column_index(snapshot: &PersistedSnapshot) {
    let reservation = snapshot.reservation();
    let reader = reservation.reader();
    let Some(footer) = table::try_read_footer(&reader) else {
        return;
    };
    let index_start = footer.index_offset as i64;
    let index_length = reader.len() as i64 - index_start;
    if index_length > 0 {
        reservation.touch_range_populate(index_start, index_length);
    }
}
