//! Port of `Nethermind.State.Flat/FlatDbManager.cs` (`IFlatDbManager.cs`) with long finality off: adds block snapshots,
//! compacts and persists them on worker threads, and gathers read-only bundles over them.
//!
//! The C# workers are tasks over bounded channels; here they are threads (`fdb-compactor`, `fdb-persist` and
//! `fdb-housekeeping`, which clears the bundle cache periodically and warns about slow jobs) over bounded crossbeam
//! channels. Process exit and the manager's own cancellation are broadcast by dropping the sender of a channel nobody
//! sends on. The trie node cache populator, `TrieNodeCache.Clear()` and the `TransientResource` leases stay in the C#
//! shell, as does the `PreGenesis` bundle, which reads nothing.
//!
//! The `ResourcePool.Usage` overloads of `HasStateForBlock` and `GatherSnapshotBundle` are default interface methods
//! that `FlatDbManager` leaves alone, so they reduce to [`FlatDbManager::has_state_for_block`] and
//! [`FlatDbManager::gather_read_only_snapshot_bundle`].

use std::any::Any;
use std::io;
use std::panic::{self, AssertUnwindSafe};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

use crossbeam_channel::{Receiver, RecvTimeoutError, Sender, TrySendError, select};
use parking_lot::Mutex;
use thiserror::Error;

use crate::bundle::ReadOnlySnapshotBundle;
use crate::bundle_cache::BundleCache;
use crate::compactor::Compactor;
use crate::host::HostError;
use crate::log_ring::{self, TimeSpan};
use crate::persistence::{Persistence, PersistenceError, PersistenceReader, ReaderFlags};
use crate::persistence_manager::{PersistError, PersistenceManager};
use crate::repository::{AssembledSnapshots, SnapshotRepository};
use crate::snapshot::{Snapshot, SnapshotTier};
use crate::stats::{FlatStats, Histogram, HistogramLabel};
use crate::types::StateId;

/// The stall warning repeats at this interval after the first one.
const STALL_WARNING_INTERVAL: Duration = Duration::from_secs(5);
/// How often the housekeeping thread looks for slow jobs.
const SLOW_JOB_POLL_INTERVAL: Duration = Duration::from_millis(250);

/// The configuration of a manager, `IFlatDbConfig` and `IBlocksConfig` values and the C# constants.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct ManagerConfig {
    /// `MaxInFlightCompactJob`: the capacity of the compaction and the persistence queues.
    pub max_in_flight_compact_job: usize,
    /// `InlineCompaction`: compacts on the thread adding the snapshot.
    pub inline_compaction: bool,
    /// `IBlocksConfig.SecondsPerSlot`, which sizes the first compactor stall warning.
    pub seconds_per_slot: u64,
    /// `InMemorySnapshotBloomBitsPerKey`: the slot filter of shared bundles; 0 disables it.
    pub in_memory_slot_filter_bits_per_key: f64,
    /// The bundle cache is cleared at this interval, 15 seconds in C#.
    pub bundle_cache_clear_interval: Duration,
    /// A job running longer warns, and again at every further interval, 2 seconds in C#.
    pub slow_job_threshold: Duration,
    /// `GatherGiveUpDeadline`: how long a gather retries, 5 seconds in C#.
    pub gather_give_up_deadline: Duration,
}

impl Default for ManagerConfig {
    /// The C# defaults.
    fn default() -> Self {
        Self {
            max_in_flight_compact_job: 32,
            inline_compaction: false,
            seconds_per_slot: 12,
            in_memory_slot_filter_bits_per_key: 14.0,
            bundle_cache_clear_interval: Duration::from_secs(15),
            slow_job_threshold: Duration::from_secs(2),
            gather_give_up_deadline: Duration::from_secs(5),
        }
    }
}

/// Failure of a manager operation.
#[derive(Debug, Error, Clone, PartialEq, Eq)]
pub enum ManagerError {
    #[error(transparent)]
    Persistence(#[from] PersistenceError),
    #[error(transparent)]
    Persist(#[from] PersistError),
    /// `StateNotRetainedException`: the state was pruned or never existed.
    #[error("No state available for {0:?}")]
    StateNotRetained(StateId),
    /// The `InvalidOperationException` of a gather that kept losing races with persistence.
    #[error("Timed out gathering ReadOnlySnapshotBundle for block {state:?} after {attempts} retries over {elapsed:?}")]
    GatherTimeout {
        state: StateId,
        attempts: u32,
        elapsed: Duration,
    },
    /// `PreGenesis` has no snapshots and no persisted state; the caller serves it with an empty bundle.
    #[error("PreGenesis is served without the manager")]
    PreGenesis,
    /// The queue the operation needed was completed by shutdown, or its worker stopped.
    #[error("The flat DB manager is shut down")]
    ShutDown,
    /// Process exit was signalled while the operation waited.
    #[error("Cancelled by process exit")]
    Cancelled,
}

/// What [`FlatDbManager::add_snapshot`] did with a snapshot.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum AddSnapshotOutcome {
    /// Added; `stall` is how long the call waited for room in the compaction queue.
    Added { stall: Option<Duration> },
    /// Rejected: its block is at or below the persisted block.
    Rejected,
    /// Rejected: a base snapshot already ends at its state.
    Duplicate,
}

pub type SharedBundle<P> = Arc<ReadOnlySnapshotBundle<<P as Persistence>::Reader>>;

/// The body of a worker thread.
type Work = Box<dyn FnOnce() + Send>;

/// Called with the name of a worker job that panicked.
type PanicHook = Box<dyn Fn(&str) + Send + Sync>;

pub struct FlatDbManager<P: Persistence> {
    shared: Arc<Shared<P>>,
    workers: Mutex<Option<Workers>>,
    disposed: AtomicBool,
}

struct Workers {
    compactor: JoinHandle<()>,
    persistence: JoinHandle<()>,
    housekeeping: JoinHandle<()>,
}

struct Shared<P: Persistence> {
    compact_size: u64,
    inline_compaction: bool,
    compactor_stall_timeout: Duration,
    slot_filter_bits_per_key: f64,
    slow_job_threshold: Duration,
    gather_give_up_deadline: Duration,
    repository: Arc<SnapshotRepository>,
    compactor: Arc<dyn Compactor>,
    persistence_manager: Arc<PersistenceManager<P>>,
    stats: Arc<FlatStats>,
    /// `_readonlySnapshotBundleCache`: shared bundles per base state, cleared whenever snapshots they lease go away.
    bundle_cache: BundleCache<ReadOnlySnapshotBundle<P::Reader>>,
    /// The writer of `_compactorJobs`; `None` once completed.
    compactor_jobs: Mutex<Option<Sender<StateId>>>,
    /// The writer of `_persistenceJobs` for inline compaction; the compactor holds its own.
    persistence_jobs: Mutex<Option<Sender<StateId>>>,
    /// `IProcessExitSource.Token`.
    process_exit: Signal,
    /// `_cancelTokenSource`: stops every worker.
    cancel: Signal,
    compactor_watch: SlowJobWatch,
    persistence_watch: SlowJobWatch,
    on_job_panic: PanicHook,
}

impl<P: Persistence> FlatDbManager<P> {
    /// Starts a manager over the persistence manager's repository, sharing its stats, with its workers running.
    ///
    /// # Panics
    /// When `max_in_flight_compact_job` is 0, as C# fails to create the channels, or a worker thread cannot be spawned,
    /// after stopping the workers already started.
    pub fn new(
        config: ManagerConfig,
        compactor: Arc<dyn Compactor>,
        persistence_manager: Arc<PersistenceManager<P>>,
    ) -> Self {
        Self::with_job_panic_hook(config, compactor, persistence_manager, |_| {})
    }

    /// As [`FlatDbManager::new`], calling `on_job_panic` with the name of every worker job that panics, once the
    /// worker logged it and before it goes on, so the owner can stop trusting the state the job may have left
    /// half-updated.
    ///
    /// # Panics
    /// As [`FlatDbManager::new`].
    pub fn with_job_panic_hook(
        config: ManagerConfig,
        compactor: Arc<dyn Compactor>,
        persistence_manager: Arc<PersistenceManager<P>>,
        on_job_panic: impl Fn(&str) + Send + Sync + 'static,
    ) -> Self {
        // The workers log where the thread starting them does.
        let log_sink = log_ring::current();
        Self::start(
            config,
            compactor,
            persistence_manager,
            Box::new(on_job_panic),
            move |name, work| {
                thread::Builder::new().name(name.to_owned()).spawn(move || {
                    log_ring::enter(log_sink);
                    work();
                })
            },
        )
    }

    fn start(
        config: ManagerConfig,
        compactor: Arc<dyn Compactor>,
        persistence_manager: Arc<PersistenceManager<P>>,
        on_job_panic: PanicHook,
        spawn: impl Fn(&str, Work) -> io::Result<JoinHandle<()>>,
    ) -> Self {
        let repository = Arc::clone(persistence_manager.repository());
        assert!(
            config.max_in_flight_compact_job > 0,
            "MaxInFlightCompactJob must be positive"
        );
        let compact_size = persistence_manager.compact_size();
        let (compactor_sender, compactor_receiver) = crossbeam_channel::bounded(config.max_in_flight_compact_job);
        let (persistence_sender, persistence_receiver) = crossbeam_channel::bounded(config.max_in_flight_compact_job);

        let shared = Arc::new(Shared {
            compact_size,
            inline_compaction: config.inline_compaction,
            // Persistence must keep up with half a slot per block, and it persists CompactSize blocks at a time.
            compactor_stall_timeout: Duration::from_secs_f64(
                0.5 * config.seconds_per_slot as f64 * compact_size as f64,
            ),
            slot_filter_bits_per_key: config.in_memory_slot_filter_bits_per_key,
            slow_job_threshold: config.slow_job_threshold,
            gather_give_up_deadline: config.gather_give_up_deadline,
            stats: Arc::clone(repository.stats()),
            repository,
            compactor,
            persistence_manager,
            bundle_cache: BundleCache::new(),
            compactor_jobs: Mutex::new(Some(compactor_sender)),
            persistence_jobs: Mutex::new(Some(persistence_sender.clone())),
            process_exit: Signal::new(),
            cancel: Signal::new(),
            compactor_watch: SlowJobWatch::default(),
            persistence_watch: SlowJobWatch::default(),
            on_job_panic,
        });

        let works: [(&str, Work); 3] = [
            ("fdb-compactor", {
                let shared = Arc::clone(&shared);
                Box::new(move || shared.run_compactor(compactor_receiver, persistence_sender))
            }),
            ("fdb-persist", {
                let shared = Arc::clone(&shared);
                Box::new(move || shared.run_persistence(persistence_receiver))
            }),
            ("fdb-housekeeping", {
                let shared = Arc::clone(&shared);
                let interval = config.bundle_cache_clear_interval;
                Box::new(move || shared.run_housekeeping(interval))
            }),
        ];
        let mut started = Vec::with_capacity(works.len());
        for (name, work) in works {
            match spawn(name, work) {
                Ok(worker) => started.push(worker),
                Err(error) => {
                    // The started workers wait on queues they hold themselves, and would outlive the manager.
                    shared.cancel.raise();
                    started.into_iter().for_each(join_worker);
                    panic!("cannot start {name}: {error}");
                }
            }
        }
        let [compactor, persistence, housekeeping] = <[JoinHandle<()>; 3]>::try_from(started).expect("three workers");
        let workers = Workers {
            compactor,
            persistence,
            housekeeping,
        };

        Self {
            shared,
            workers: Mutex::new(Some(workers)),
            disposed: AtomicBool::new(false),
        }
    }

    pub fn repository(&self) -> &Arc<SnapshotRepository> {
        &self.shared.repository
    }

    pub fn persistence_manager(&self) -> &Arc<PersistenceManager<P>> {
        &self.shared.persistence_manager
    }

    pub fn stats(&self) -> &Arc<FlatStats> {
        &self.shared.stats
    }

    /// Port of `AddSnapshot`: [`FlatDbManager::register_snapshot`], then [`FlatDbManager::compact_registered`] when
    /// the snapshot was added.
    pub fn add_snapshot(&self, snapshot: Arc<Snapshot>) -> Result<AddSnapshotOutcome, ManagerError> {
        let to = snapshot.to();
        match self.register_snapshot(snapshot)? {
            AddSnapshotOutcome::Added { .. } => Ok(AddSnapshotOutcome::Added {
                stall: self.compact_registered(&to)?,
            }),
            refused => Ok(refused),
        }
    }

    /// The first part of `AddSnapshot`: registers the block's snapshot as an in-memory base snapshot and the last
    /// committed state, unless it is refused. [`AddSnapshotOutcome::Added`] reports no stall, as the compaction is not
    /// queued yet: the shell queues the trie node cache population in between, as C# does before it can stall.
    pub fn register_snapshot(&self, snapshot: Arc<Snapshot>) -> Result<AddSnapshotOutcome, ManagerError> {
        let shared = &self.shared;
        let (from, to) = (snapshot.from(), snapshot.to());
        log::trace!("Registering {} to {}", from.block_number, to.block_number);

        let persisted_state_id = shared.persistence_manager.get_current_persisted_state_id()?;
        // PreGenesis carries the u64::MAX sentinel, so only a real persisted state rejects.
        if persisted_state_id != StateId::PRE_GENESIS && to.block_number <= persisted_state_id.block_number {
            log::warn!(
                "Cannot register snapshot earlier than bigcache. Snapshot number {}, bigcache number: \
                 {persisted_state_id}",
                to.block_number
            );
            return Ok(AddSnapshotOutcome::Rejected);
        }

        if !shared.repository.try_add(snapshot, SnapshotTier::InMemoryBase) {
            log::warn!("State {to} already added");
            return Ok(AddSnapshotOutcome::Duplicate);
        }

        // The latest block the main processing scope committed; the head of forced persists.
        shared.repository.set_last_committed_state_id(to);
        Ok(AddSnapshotOutcome::Added { stall: None })
    }

    /// The rest of `AddSnapshot` for the registered snapshot ending at `to`: compacts it on this thread with inline
    /// compaction, else queues its compaction, which then queues its persistence. Returns how long it waited for room.
    ///
    /// When the compaction queue is full, block processing waits for room, warning at the stall timeout and every 5
    /// seconds after it. An error leaves the snapshot added, as the C# exception does.
    pub fn compact_registered(&self, to: &StateId) -> Result<Option<Duration>, ManagerError> {
        if self.shared.inline_compaction {
            self.shared.run_compact_job_inline(*to)?;
            return Ok(None);
        }
        self.shared.queue_compaction(*to)
    }

    /// Port of `GatherReadOnlySnapshotBundle`: the bundle of the snapshots from `base_block` down to the persisted
    /// state a fresh reader sees, over that reader.
    ///
    /// Bundles with [`ReaderFlags::NONE`] are shared through the cache and get the slot filter; flagged ones hold
    /// specially configured readers and are never cached. A race with persistence, which leaves no chain to the
    /// reader's state, is retried with a backoff for the give-up deadline.
    pub fn gather_read_only_snapshot_bundle(
        &self,
        base_block: &StateId,
        reader_flags: ReaderFlags,
    ) -> Result<SharedBundle<P>, ManagerError> {
        let shared = &self.shared;
        log::trace!("Gathering {base_block}.");
        if *base_block == StateId::PRE_GENESIS {
            return Err(ManagerError::PreGenesis);
        }

        let shareable = reader_flags == ReaderFlags::NONE;
        let mut retry_started = Instant::now();
        let mut attempt = 0u32;
        loop {
            if shareable && let Some(bundle) = shared.bundle_cache.get(base_block) {
                return Ok(bundle);
            }

            if attempt == 1 {
                retry_started = Instant::now();
            }
            if attempt != 0 {
                let elapsed = retry_started.elapsed();
                if elapsed > shared.gather_give_up_deadline {
                    return Err(ManagerError::GatherTimeout {
                        state: *base_block,
                        attempts: attempt,
                        elapsed,
                    });
                }
                // 1, 2, 4, ... 64, then 100 ms.
                thread::sleep(Duration::from_millis((1u64 << attempt.min(30)).min(100)));
            }

            let reader = shared.persistence_manager.lease_reader(reader_flags)?;
            let reader_state = reader.current_state();
            let estimated_size = (shared.repository.snapshot_count().max(0) as u64 / shared.compact_size.max(1)).max(1);
            let assembled = shared
                .repository
                .assemble_snapshots(base_block, &reader_state, estimated_size as usize);

            // No chain while the reader is elsewhere: the path was removed concurrently, unless the state itself is
            // gone, which no retry recovers.
            if assembled.snapshot_count() == 0 && reader_state != *base_block {
                drop(assembled);
                drop(reader);
                if !self.has_state_for_block(base_block)? {
                    return Err(ManagerError::StateNotRetained(*base_block));
                }
                attempt += 1;
                continue;
            }

            log::trace!(
                "Gathered {base_block}. Got {} known states, 0 persisted, Reader state: {reader_state}. Persistence \
                 state: {}",
                assembled.snapshot_count(),
                shared
                    .persistence_manager
                    .get_current_persisted_state_id()
                    .map_or_else(|error| error.to_string(), |persisted| persisted.to_string())
            );
            shared.report_bundle_metrics(&assembled);

            // The first filtered read builds the filter; flagged one-off scans never read through it.
            let bits_per_key = if shareable {
                shared.slot_filter_bits_per_key
            } else {
                0.0
            };
            let bundle = Arc::new(
                ReadOnlySnapshotBundle::new(assembled.in_memory, reader, bits_per_key)
                    .with_stats(Arc::clone(&shared.stats)),
            );
            if shareable {
                shared.bundle_cache.try_add(*base_block, Arc::clone(&bundle));
            }
            return Ok(bundle);
        }
    }

    /// Whether a base snapshot ends at `state_id` or it is the persisted state.
    pub fn has_state_for_block(&self, state_id: &StateId) -> Result<bool, ManagerError> {
        let shared = &self.shared;
        Ok(shared.repository.has_state(state_id)
            || shared.persistence_manager.get_current_persisted_state_id()? == *state_id)
    }

    /// Port of `FlushCache`: persists every snapshot up to the committed head and clears the bundle cache. Returns
    /// whether the caches were cleared, after which the caller clears its trie node cache.
    ///
    /// Collapses the reorg window to zero; only for genesis load and tests, never for shutdown.
    pub fn flush_cache(&self, cancelled: &AtomicBool) -> Result<bool, ManagerError> {
        log::info!("FlatDbManager FlushCache started.");
        let persisted_state = self.shared.persistence_manager.flush_to_persistence()?;
        if cancelled.load(Ordering::Acquire) || persisted_state == StateId::PRE_GENESIS {
            return Ok(false);
        }

        self.shared.bundle_cache.clear();
        log::info!("FlatDbManager FlushCache completed. Persisted to {persisted_state}.");
        Ok(true)
    }

    /// Drops every snapshot not on the ancestry of `head` and the cached bundles leasing them.
    pub fn drop_state_not_reachable_from(&self, head: &StateId) -> Result<(), ManagerError> {
        self.shared.persistence_manager.drop_state_not_reachable_from(head)?;
        // Otherwise the cached bundles keep the pruned snapshots alive until the periodic clear.
        self.shared.bundle_cache.clear();
        Ok(())
    }

    /// Port of `ClearReadOnlyBundleCache`.
    pub fn clear_bundle_cache(&self) {
        self.shared.bundle_cache.clear();
    }

    /// Process exit started: a block waiting for room in the compaction queue gives up, and one arriving with the
    /// queue full is added without being queued. The workers keep running until [`FlatDbManager::shutdown`].
    pub fn signal_exit(&self) {
        self.shared.process_exit.raise();
    }

    /// Port of `DisposeAsync`: drains the queues in feed order, the compaction queue first since it feeds the
    /// persistence one, then stops every worker. Idempotent.
    ///
    /// The unfinalized tail is not flushed, which would break reorgs across the restart. A worker that stops before
    /// draining cancels its siblings instead; a failed job does not stop a worker.
    pub fn shutdown(&self) {
        if self.disposed.swap(true, Ordering::AcqRel) {
            return;
        }
        let Some(workers) = self.workers.lock().take() else {
            return;
        };
        let shared = &self.shared;

        shared.bundle_cache.clear();
        drop(shared.compactor_jobs.lock().take());
        join_worker(workers.compactor);
        drop(shared.persistence_jobs.lock().take());
        join_worker(workers.persistence);

        shared.cancel.raise();
        join_worker(workers.housekeeping);
    }
}

impl<P: Persistence> Drop for FlatDbManager<P> {
    fn drop(&mut self) {
        self.shutdown();
    }
}

impl<P: Persistence> Shared<P> {
    /// Port of `RunCompactor`.
    fn run_compactor(&self, jobs: Receiver<StateId>, persistence_jobs: Sender<StateId>) {
        while let Some(state_id) = self.next_job(&jobs) {
            let keep_running = self.run_job(&self.compactor_watch, format!("Compacting {state_id}"), || {
                self.run_compact_job(state_id, &persistence_jobs)
            });
            if !keep_running {
                break;
            }
        }
    }

    /// Port of `RunCompactJob`, on the compactor.
    fn run_compact_job(&self, state_id: StateId, persistence_jobs: &Sender<StateId>) -> Result<(), JobError> {
        // Registered here, off the block processing thread, because of the lock.
        self.repository.add_state_id(state_id);
        self.compactor.do_compact_snapshot(&state_id);

        if self.cancel.is_raised() {
            return Err(JobError::Cancelled);
        }
        select! {
            send(persistence_jobs, state_id) -> sent => {
                sent.map_err(|_| JobError::Failed("the persistence queue is closed".to_owned()))
            }
            recv(self.cancel.receiver()) -> _ => Err(JobError::Cancelled),
        }
    }

    /// Port of `RunCompactJobSync`, on the thread adding the snapshot; process exit aborts its wait for room in the
    /// persistence queue.
    fn run_compact_job_inline(&self, state_id: StateId) -> Result<(), ManagerError> {
        self.repository.add_state_id(state_id);
        self.compactor.do_compact_snapshot(&state_id);

        let jobs = self.persistence_jobs.lock().clone().ok_or(ManagerError::ShutDown)?;
        if self.process_exit.is_raised() {
            return Err(ManagerError::Cancelled);
        }
        select! {
            send(jobs, state_id) -> sent => sent.map_err(|_| ManagerError::ShutDown),
            recv(self.process_exit.receiver()) -> _ => Err(ManagerError::Cancelled),
        }
    }

    /// The non-inline tail of `AddSnapshot`: queues the compaction, waiting for room while the queue is full.
    fn queue_compaction(&self, state_id: StateId) -> Result<Option<Duration>, ManagerError> {
        let Some(jobs) = self.compactor_jobs.lock().clone() else {
            return if self.process_exit.is_raised() {
                Ok(None)
            } else {
                Err(ManagerError::ShutDown)
            };
        };
        match jobs.try_send(state_id) {
            Ok(()) => return Ok(None),
            // When exiting the queue stops.
            Err(_) if self.process_exit.is_raised() => return Ok(None),
            Err(TrySendError::Disconnected(_)) => return Err(ManagerError::ShutDown),
            Err(TrySendError::Full(_)) => {}
        }

        // Block processing is now stalled waiting for the compactor; several blocks entered the queue without
        // blocking first, so waiting here is the backpressure.
        let stall_start = Instant::now();
        let mut delay = self.compactor_stall_timeout;
        loop {
            if self.process_exit.is_raised() {
                return Err(ManagerError::Cancelled);
            }
            if self.cancel.is_raised() {
                return Err(ManagerError::ShutDown);
            }
            select! {
                send(jobs, state_id) -> sent => {
                    sent.map_err(|_| ManagerError::ShutDown)?;
                    break;
                }
                recv(self.process_exit.receiver()) -> _ => return Err(ManagerError::Cancelled),
                recv(self.cancel.receiver()) -> _ => return Err(ManagerError::ShutDown),
                default(delay) => {
                    delay = STALL_WARNING_INTERVAL;
                    log::warn!("Compactor job stall! Persistence is too slow for the network.");
                }
            }
        }

        let stall = stall_start.elapsed();
        self.stats.observe_time(Histogram::CompactorStallTime, stall);
        Ok(Some(stall))
    }

    /// Port of `RunPersistence`.
    fn run_persistence(&self, jobs: Receiver<StateId>) {
        // `_lastClearedPersistedStateId` and `_lastClearedRemovedBaseSnapshotCount`.
        let mut last_cleared = (StateId::PRE_GENESIS, 0i64);
        while let Some(state_id) = self.next_job(&jobs) {
            let keep_running = self.run_job(&self.persistence_watch, format!("Persisting {state_id}"), || {
                self.persist_if_needed(&state_id, &mut last_cleared)
            });
            if !keep_running {
                break;
            }
        }
    }

    /// Port of `PersistIfNeeded`: persists what `latest_snapshot` allows, then clears the bundle cache when that
    /// released snapshots the cached bundles lease.
    fn persist_if_needed(&self, latest_snapshot: &StateId, last_cleared: &mut (StateId, i64)) -> Result<(), JobError> {
        self.persistence_manager.add_to_persistence(latest_snapshot)?;

        let current_persisted_state_id = self.persistence_manager.get_current_persisted_state_id()?;
        if current_persisted_state_id == StateId::PRE_GENESIS {
            return Ok(());
        }

        let cleared = (
            current_persisted_state_id,
            self.repository.removed_base_snapshot_count(),
        );
        if cleared != *last_cleared {
            *last_cleared = cleared;
            self.bundle_cache.clear();
        }
        Ok(())
    }

    /// The next job of a worker, `None` once the queue is completed and drained or the manager is cancelled.
    fn next_job(&self, jobs: &Receiver<StateId>) -> Option<StateId> {
        if self.cancel.is_raised() {
            return None;
        }
        select! {
            recv(jobs) -> job => job.ok(),
            recv(self.cancel.receiver()) -> _ => None,
        }
    }

    /// Port of `NotifyWhenSlow`: runs one job, which the housekeeping thread warns about while it is slow. A failed or
    /// panicking job is logged, a panicking one then handed to the panic hook, and the worker goes on; a cancelled one
    /// stops the worker and cancels the others.
    fn run_job(&self, watch: &SlowJobWatch, name: String, job: impl FnOnce() -> Result<(), JobError>) -> bool {
        let started = Instant::now();
        watch.start(&name, self.slow_job_threshold);
        let outcome = panic::catch_unwind(AssertUnwindSafe(job));
        watch.finish();

        match outcome {
            Ok(Ok(())) => {}
            Ok(Err(JobError::Cancelled)) => {
                self.cancel.raise();
                return false;
            }
            Ok(Err(JobError::Failed(error))) => log::error!("Error on {name}: {error}"),
            Err(panic) => {
                log::error!("Error on {name}: {}", panic_message(&*panic));
                (self.on_job_panic)(&name);
            }
        }
        log::trace!("{name} took {}", TimeSpan(started.elapsed()));
        true
    }

    /// The periodic `ClearReadOnlyBundleCache` of `RunClearBundleCache` and the slow-job warnings of `NotifyWhenSlow`.
    fn run_housekeeping(&self, clear_interval: Duration) {
        let mut next_clear = Instant::now() + clear_interval;
        loop {
            let wait = next_clear
                .saturating_duration_since(Instant::now())
                .min(SLOW_JOB_POLL_INTERVAL);
            if self.cancel.receiver().recv_timeout(wait) != Err(RecvTimeoutError::Timeout) {
                return;
            }

            let now = Instant::now();
            if now >= next_clear {
                self.bundle_cache.clear();
                next_clear = now + clear_interval;
            }
            self.compactor_watch.warn_if_slow(now, self.slow_job_threshold);
            self.persistence_watch.warn_if_slow(now, self.slow_job_threshold);
        }
    }

    /// Port of `ReportBundleMetrics`; the persisted part stays empty until the persisted tier exists.
    fn report_bundle_metrics(&self, assembled: &AssembledSnapshots) {
        let in_memory_depth = match (assembled.in_memory.first(), assembled.in_memory.last()) {
            (Some(oldest), Some(newest)) => newest.to().block_number.wrapping_sub(oldest.from().block_number) as i32,
            _ => 0,
        };
        self.stats.observe(
            Histogram::SnapshotBundleBlockNumberDepth,
            HistogramLabel::InMemory,
            f64::from(in_memory_depth),
        );
        self.stats.observe(
            Histogram::SnapshotBundleBlockNumberDepth,
            HistogramLabel::Persisted,
            0.0,
        );

        let counters = &self.stats.counters;
        FlatStats::set(&counters.snapshot_bundle_size, assembled.snapshot_count() as i64);
        FlatStats::set(&counters.snapshot_bundle_persisted_snapshot_size, 0);
        FlatStats::set(&counters.snapshot_bundle_persisted_snapshot_memory, 0);
    }
}

/// How a job ended other than successfully.
enum JobError {
    /// `OperationCanceledException`: stops the worker.
    Cancelled,
    /// Any other exception: logged.
    Failed(String),
}

impl From<PersistError> for JobError {
    fn from(error: PersistError) -> Self {
        match error {
            PersistError::Host(HostError::Cancelled) => JobError::Cancelled,
            error => JobError::Failed(error.to_string()),
        }
    }
}

impl From<PersistenceError> for JobError {
    fn from(error: PersistenceError) -> Self {
        JobError::Failed(error.to_string())
    }
}

/// A broadcast that fires once: raising drops the only sender, which wakes every receiver.
struct Signal {
    raised: AtomicBool,
    sender: Mutex<Option<Sender<()>>>,
    receiver: Receiver<()>,
}

impl Signal {
    fn new() -> Self {
        let (sender, receiver) = crossbeam_channel::bounded(0);
        Self {
            raised: AtomicBool::new(false),
            sender: Mutex::new(Some(sender)),
            receiver,
        }
    }

    fn raise(&self) {
        self.raised.store(true, Ordering::Release);
        drop(self.sender.lock().take());
    }

    fn is_raised(&self) -> bool {
        self.raised.load(Ordering::Acquire)
    }

    /// Ready, with an error, once raised.
    fn receiver(&self) -> &Receiver<()> {
        &self.receiver
    }
}

/// The job a worker runs, for the slow-job warnings.
#[derive(Default)]
struct SlowJobWatch {
    job: Mutex<Option<WatchedJob>>,
}

struct WatchedJob {
    name: String,
    started: Instant,
    next_warning: Instant,
}

impl SlowJobWatch {
    fn start(&self, name: &str, threshold: Duration) {
        let started = Instant::now();
        *self.job.lock() = Some(WatchedJob {
            name: name.to_owned(),
            started,
            next_warning: started + threshold,
        });
    }

    fn finish(&self) {
        *self.job.lock() = None;
    }

    fn warn_if_slow(&self, now: Instant, threshold: Duration) {
        if let Some(job) = self.job.lock().as_mut()
            && now >= job.next_warning
        {
            log::warn!("Slow task \"{}\". Took {}", job.name, TimeSpan(now - job.started));
            job.next_warning = now + threshold;
        }
    }
}

fn join_worker(worker: JoinHandle<()>) {
    let name = worker.thread().name().unwrap_or("worker").to_owned();
    if let Err(panic) = worker.join() {
        log::error!("{name} stopped: {}", panic_message(&*panic));
    }
}

fn panic_message(panic: &(dyn Any + Send)) -> &str {
    panic
        .downcast_ref::<&str>()
        .copied()
        .or_else(|| panic.downcast_ref::<String>().map(String::as_str))
        .unwrap_or("a panic")
}

#[cfg(test)]
mod tests {
    use std::io;
    use std::panic::{self, AssertUnwindSafe};
    use std::sync::Arc;
    use std::sync::atomic::{AtomicUsize, Ordering};
    use std::thread;
    use std::time::Duration;

    use log::LevelFilter;
    use parking_lot::Mutex;

    use super::{JobError, SlowJobWatch};
    use crate::compactor::SnapshotCompactor;
    use crate::events::PersistedNodeEvents;
    use crate::host::{Host, HostError};
    use crate::kv::mem::MemKv;
    use crate::log_ring::LogSink;
    use crate::manager::{FlatDbManager, ManagerConfig};
    use crate::persistence::layouts::KvPersistence;
    use crate::persistence_manager::{PersistenceManager, PersistenceManagerConfig};
    use crate::repository::{SnapshotRepository, StateHeaderProvider};
    use crate::schedule::CompactionSchedule;
    use crate::types::{FlatLayout, Hash256};

    struct NoFinality;

    impl StateHeaderProvider for NoFinality {
        fn finalized_block_number(&self) -> u64 {
            0
        }

        fn finalized_state_root(&self, _block_number: u64) -> Option<Hash256> {
            None
        }
    }

    impl Host for NoFinality {
        fn flush_deferred(&self) -> Result<(), HostError> {
            Ok(())
        }
    }

    type MemPersistenceManager = PersistenceManager<KvPersistence<MemKv>>;

    fn components() -> (Arc<SnapshotCompactor>, Arc<MemPersistenceManager>) {
        let host = Arc::new(NoFinality);
        let repository = Arc::new(SnapshotRepository::new(host.clone()));
        let schedule = CompactionSchedule::new(16, 1024, 0).unwrap();
        let persistence = KvPersistence::new(MemKv::new(), FlatLayout::Flat, None).unwrap();
        let persistence_manager = Arc::new(PersistenceManager::new(
            PersistenceManagerConfig {
                min_reorg_depth: 128,
                max_reorg_depth: 256,
                max_in_memory_snapshot_bytes: 0,
            },
            schedule,
            host,
            Arc::new(persistence),
            Arc::clone(&repository),
            Arc::new(PersistedNodeEvents::new(false)),
        ));
        (
            Arc::new(SnapshotCompactor::new(schedule, repository)),
            persistence_manager,
        )
    }

    /// The jobs that panicked, each with the lines the sink held by then.
    type Panicked = Arc<Mutex<Vec<(String, Vec<String>)>>>;

    /// A manager whose panic hook records the jobs that panicked, each with the lines `sink` held by then.
    fn manager(sink: Arc<LogSink>) -> (FlatDbManager<KvPersistence<MemKv>>, Panicked) {
        let (compactor, persistence_manager) = components();
        let panicked = Arc::new(Mutex::new(Vec::new()));
        let recorded = Arc::clone(&panicked);
        let manager =
            FlatDbManager::with_job_panic_hook(ManagerConfig::default(), compactor, persistence_manager, move |name| {
                let logged = sink.ring().drain(usize::MAX).into_iter().map(|record| record.message);
                recorded.lock().push((name.to_owned(), logged.collect()))
            });
        (manager, panicked)
    }

    #[test]
    fn a_worker_that_cannot_start_stops_the_ones_started() {
        let (compactor, persistence_manager) = components();
        let spawned = AtomicUsize::new(0);
        let started = panic::catch_unwind(AssertUnwindSafe(|| {
            FlatDbManager::start(
                ManagerConfig::default(),
                compactor,
                Arc::clone(&persistence_manager),
                Box::new(|_| {}),
                |name, work| {
                    if spawned.fetch_add(1, Ordering::SeqCst) == 1 {
                        return Err(io::Error::other("no more threads"));
                    }
                    thread::Builder::new().name(name.to_owned()).spawn(work)
                },
            )
        }));

        assert!(started.is_err());
        assert_eq!(
            Arc::strong_count(&persistence_manager),
            1,
            "the started compactor stopped and released the persistence"
        );
    }

    #[test]
    fn a_failed_or_panicking_job_keeps_the_worker_and_a_cancelled_one_stops_all() {
        let sink = Arc::new(LogSink::new(LevelFilter::Error));
        let (manager, panicked) = manager(Arc::clone(&sink));
        let shared = &manager.shared;
        let watch = SlowJobWatch::default();

        assert!(shared.run_job(&watch, "ok".to_owned(), || Ok(())));
        assert!(shared.run_job(&watch, "failed".to_owned(), || Err(JobError::Failed("io".to_owned()))));
        assert!(sink.scope(|| shared.run_job(&watch, "panicking".to_owned(), || panic!("compaction failed"))));
        assert!(!shared.cancel.is_raised());
        assert!(watch.job.lock().is_none(), "a finished job is no longer watched");
        assert_eq!(
            *panicked.lock(),
            [(
                "panicking".to_owned(),
                vec!["Error on panicking: compaction failed".to_owned()]
            )],
            "only the panic reaches the hook, once logged, so whoever the hook tells finds the line"
        );

        assert!(!shared.run_job(&watch, "cancelled".to_owned(), || Err(JobError::Cancelled)));
        assert!(shared.cancel.is_raised(), "a stopped worker cancels the others");
        manager.shutdown();
        manager.shutdown();
    }

    #[test]
    fn a_slow_job_warns_once_per_threshold() {
        let watch = SlowJobWatch::default();
        let threshold = Duration::from_secs(2);
        watch.start("job", threshold);
        let started = watch.job.lock().as_ref().unwrap().started;
        let next_warning = || watch.job.lock().as_ref().unwrap().next_warning;

        watch.warn_if_slow(started + Duration::from_secs(1), threshold);
        assert_eq!(next_warning(), started + threshold);
        watch.warn_if_slow(started + Duration::from_secs(3), threshold);
        assert_eq!(next_warning(), started + Duration::from_secs(5));
    }
}
