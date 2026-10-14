//! Port of `Nethermind.State.Flat/PersistenceManager.cs` (`IPersistenceManager.cs`): which snapshot to write to the
//! persistence next, in memory or in the persisted-snapshot tier, the writing of it, and with long finality the
//! conversion of in-memory bases into that tier (Phase 2); the history capture hook is out of scope.
//!
//! A conversion runs the bases of a range in parallel, then queues one [`EventKind::Converted`] entry per base in
//! ascending block order for the C# shell to mark the nodes persisted, as `PersistedSnapshotBuilder` marks them while it
//! extracts. Deliberate deviation: only a base converted successfully is marked; C# marks before the build and keeps the
//! marks of a conversion that then fails. The states are then handed to the persisted compactor while the persistence
//! lock is held, as C# awaits `EnqueueAsync` under it; process exit ends that wait and a conversion loop.
//!
//! Writing a persisted snapshot to the persistence flushes no deferred block data, as in C#.
//!
//! Deliberate deviation: a write that fails inside [`PersistenceManager::persist_snapshot`] discards the batch. C#
//! disposes the batch on the way out, which commits the writes made so far and moves the persisted state pointer
//! to the snapshot's end while `CurrentPersistedStateId` stays behind.
//!
//! A fatal RocksDB error (`DbOnTheRocks.HandleFatalDbError`) of the flat or the catalog database, and a converted
//! snapshot failing validation, make C# exit before anything else is written; here they fail every later persist
//! instead, until the shell, handed the error by [`PersistenceManager::take_fatal_error`], shuts the node down. The
//! persisted compactor records its fatal catalog errors in the same [`FatalErrorSlot`].

use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::Instant;

use parking_lot::{Mutex, RwLock};
use thiserror::Error;

use crate::events::{EventKind, PersistedNode, PersistedNodeEvents, PersistedNodes};
use crate::host::{Host, HostError};
use crate::kv::WriteFlags;
use crate::persisted::compactor::EnqueueError;
use crate::persisted::reader::resolve_trie_rlp;
use crate::persisted::scanner::Scanner;
use crate::persisted::snapshot::PersistedSnapshot;
use crate::persisted::tier::PersistedTier;
use crate::persisted::{TierError, TierErrorKind, parallel};
use crate::persistence::{
    Persistence, PersistenceError, PersistenceReader, PersistenceResult, PersistenceWriteBatch, ReaderFlags,
};
use crate::repository::{Edge, SnapshotRepository};
use crate::schedule::CompactionSchedule;
use crate::snapshot::{Snapshot, SnapshotTier};
use crate::stats::{FlatStats, Histogram, HistogramLabel};
use crate::types::StateId;

/// Snapshots one `AddToPersistence` call persists at most, so a deep backlog is worked off over several blocks.
const MAX_DRAIN_ITERATIONS: usize = 4;

/// The `IFlatDbConfig` values the persistence manager reads, besides `CompactSize`, which the schedule carries.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct PersistenceManagerConfig {
    /// `MinReorgDepth`: blocks a persist always leaves in memory above the persisted state.
    pub min_reorg_depth: u64,
    /// `MaxReorgDepth`: the force-persist backstop with long finality off.
    pub max_reorg_depth: u64,
    /// `MaxInMemorySnapshotBytes`: the in-memory estimate above which the next snapshot is persisted; 0 disables.
    pub max_in_memory_snapshot_bytes: u64,
    /// `EnableLongFinality`: `None` when off.
    pub long_finality: Option<LongFinalityConfig>,
}

/// The `IFlatDbConfig` values of long finality the persistence manager reads.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct LongFinalityConfig {
    /// `LongFinalityMaxReorgDepth`: the force-persist backstop with long finality on.
    pub max_reorg_depth: u64,
    /// `MaxInMemoryBaseSnapshotCount`: the in-memory base snapshots above which bases are converted into the tier.
    pub max_in_memory_base_snapshot_count: u64,
}

/// Failure to persist.
#[derive(Debug, Error, Clone, PartialEq, Eq)]
pub enum PersistError {
    #[error(transparent)]
    Persistence(#[from] PersistenceError),
    /// `IStatePersistenceBarrier.FlushDeferred` failed, so nothing was written.
    #[error(transparent)]
    Host(#[from] HostError),
    /// `OperationCanceledException`: process exit ended a conversion, or the wait to hand its states to the persisted
    /// compactor.
    #[error("Cancelled by process exit")]
    Cancelled,
}

impl From<TierError> for PersistError {
    fn from(error: TierError) -> Self {
        Self::Persistence(error.into())
    }
}

impl PersistError {
    /// Whether nothing more may be written: the flat or the catalog database failed with on-disk corruption or an I/O
    /// error, or a converted snapshot failed validation.
    pub fn is_fatal(&self) -> bool {
        match self {
            Self::Persistence(PersistenceError::Kv(error) | PersistenceError::Catalog(error)) => error.is_fatal(),
            Self::Persistence(PersistenceError::Tier { kind, .. }) => *kind == TierErrorKind::Validation,
            _ => false,
        }
    }
}

/// The first fatal error of a manager, after which every persist fails with it.
#[derive(Debug, Default)]
pub struct FatalErrorSlot {
    error: Mutex<Option<PersistError>>,
    /// Whether [`FatalErrorSlot::take`] handed the error out.
    taken: AtomicBool,
}

impl FatalErrorSlot {
    /// Keeps `error` when it is fatal and no fatal error was kept before.
    pub fn record(&self, error: &PersistError) {
        if error.is_fatal() {
            self.error.lock().get_or_insert_with(|| error.clone());
        }
    }

    /// The fatal error, handed out once.
    pub fn take(&self) -> Option<PersistError> {
        let error = self.error.lock();
        match &*error {
            Some(error) if !self.taken.swap(true, Ordering::AcqRel) => Some(error.clone()),
            _ => None,
        }
    }

    fn check(&self) -> Result<(), PersistError> {
        self.error.lock().clone().map_or(Ok(()), Err)
    }
}

/// What [`PersistenceManager::determine_snapshot_action`] decided, holding the lease of its snapshot.
pub enum SnapshotAction {
    /// Write an in-memory snapshot to the persistence.
    PersistInMemory(Arc<Snapshot>),
    /// Write a snapshot of the persisted-snapshot tier to the persistence.
    PersistPersisted(Arc<PersistedSnapshot>),
    /// Branch A: convert the in-memory bases of the range this `CompactSize`-wide compacted snapshot spans.
    ConvertCompacted(Arc<Snapshot>),
    /// Branch B: convert this one in-memory base.
    ConvertBase(Arc<Snapshot>),
}

impl From<Edge> for SnapshotAction {
    fn from(edge: Edge) -> Self {
        match edge {
            Edge::InMemory(snapshot) => Self::PersistInMemory(snapshot),
            Edge::Persisted(snapshot) => Self::PersistPersisted(snapshot),
        }
    }
}

/// Long finality: its settings and the tier it converts into.
struct LongFinality {
    config: LongFinalityConfig,
    tier: PersistedTier,
}

pub struct PersistenceManager<P: Persistence> {
    persistence: Arc<P>,
    repository: Arc<SnapshotRepository>,
    schedule: CompactionSchedule,
    host: Arc<dyn Host>,
    stats: Arc<FlatStats>,
    events: Arc<PersistedNodeEvents>,
    min_reorg_depth: u64,
    backstop_reorg_depth: u64,
    compact_size: u64,
    max_in_memory_snapshot_bytes: i64,
    /// `_persistenceLock`: one persist, flush or drop at a time.
    persistence_lock: Mutex<()>,
    last_warned_stall: Mutex<Option<StateId>>,
    /// `CurrentPersistedStateId`; [`StateId::PRE_GENESIS`] until read from the persistence.
    current_persisted_state: RwLock<StateId>,
    fatal_error: Arc<FatalErrorSlot>,
    long_finality: Option<LongFinality>,
    /// `_cts`, linked to process exit: stops conversions and the wait to hand them to the persisted compactor.
    process_exit: AtomicBool,
}

impl<P: Persistence> PersistenceManager<P> {
    /// A persistence manager writing to `persistence`, recording into the repository's stats and, when enabled, queueing
    /// the nodes it persists or converts into `events`. With long finality on, `persisted_tier` is the tier of the
    /// repository, which it converts into and records its fatal errors in.
    ///
    /// # Panics
    /// When `persisted_tier` is given exactly when long finality is off.
    pub fn new(
        config: PersistenceManagerConfig,
        schedule: CompactionSchedule,
        host: Arc<dyn Host>,
        persistence: Arc<P>,
        repository: Arc<SnapshotRepository>,
        events: Arc<PersistedNodeEvents>,
        persisted_tier: Option<PersistedTier>,
    ) -> Self {
        assert_eq!(
            config.long_finality.is_some(),
            persisted_tier.is_some(),
            "long finality converts into a persisted tier, and only then"
        );
        let compact_size = schedule.compact_size();
        let backstop = config
            .long_finality
            .map_or(config.max_reorg_depth, |long_finality| long_finality.max_reorg_depth);
        let fatal_error = persisted_tier
            .as_ref()
            .map_or_else(Arc::default, |tier| Arc::clone(&tier.fatal_error));
        let long_finality = config
            .long_finality
            .zip(persisted_tier)
            .map(|(config, tier)| LongFinality { config, tier });
        Self {
            stats: Arc::clone(repository.stats()),
            persistence,
            repository,
            schedule,
            host,
            events,
            min_reorg_depth: config.min_reorg_depth,
            // The long-finality window when on, as the persisted tier then serves deep reorgs. Raised to at least one
            // CompactSize above MinReorgDepth, so the finalized trigger, which engages around MinReorgDepth, always has
            // room to act before the backstop fires.
            backstop_reorg_depth: backstop.max(config.min_reorg_depth.saturating_add(compact_size)),
            compact_size,
            max_in_memory_snapshot_bytes: config.max_in_memory_snapshot_bytes as i64,
            persistence_lock: Mutex::new(()),
            last_warned_stall: Mutex::new(None),
            current_persisted_state: RwLock::new(StateId::PRE_GENESIS),
            fatal_error,
            long_finality,
            process_exit: AtomicBool::new(false),
        }
    }

    /// The fatal error a persist, a conversion or a persisted compaction hit, handed out once, for the shell to shut the
    /// node down on as `DbOnTheRocks.HandleFatalDbError` does.
    pub fn take_fatal_error(&self) -> Option<PersistError> {
        self.fatal_error.take()
    }

    /// The persisted-snapshot tier, with long finality on.
    fn persisted_tier(&self) -> Option<&PersistedTier> {
        self.long_finality.as_ref().map(|long_finality| &long_finality.tier)
    }

    /// Process exit started: a conversion stops between bases and a wait for room in the persisted compactor's queue
    /// ends, both failing with [`PersistError::Cancelled`].
    pub fn signal_exit(&self) {
        self.process_exit.store(true, Ordering::Release);
        if let Some(tier) = self.persisted_tier() {
            tier.compactor.signal_exit();
        }
    }

    /// The shutdown of the persisted tier once nothing more is persisted, in the C# disposal order: the persisted
    /// compactor finishes what is queued (or drops it after process exit), the files of every snapshot are marked to
    /// survive for the next session, and the page advisor stops.
    pub fn shut_down_persisted_tier(&self) {
        if let Some(tier) = self.persisted_tier() {
            tier.compactor.dispose();
            tier.loader.mark_for_shutdown();
            tier.arena.stop_advisor();
        }
    }

    pub fn repository(&self) -> &Arc<SnapshotRepository> {
        &self.repository
    }

    pub fn persistence(&self) -> &Arc<P> {
        &self.persistence
    }

    /// `CompactSize`, the widest snapshot a persist writes.
    pub fn compact_size(&self) -> u64 {
        self.compact_size
    }

    /// Port of `LeaseReader`.
    pub fn lease_reader(&self, flags: ReaderFlags) -> PersistenceResult<P::Reader> {
        self.persistence.create_reader(flags)
    }

    /// The persisted state, read from the persistence while none is known.
    pub fn get_current_persisted_state_id(&self) -> PersistenceResult<StateId> {
        let current = *self.current_persisted_state.read();
        if current != StateId::PRE_GENESIS {
            return Ok(current);
        }

        let current = self.persistence.create_reader(ReaderFlags::NONE)?.current_state();
        self.set_current_persisted_state_id(current);
        Ok(current)
    }

    fn set_current_persisted_state_id(&self, state_id: StateId) {
        *self.current_persisted_state.write() = state_id;
    }

    /// Port of `DetermineSnapshotAction`: what to do next, if anything. Phase 1, persistence, runs first; Phase 2,
    /// conversion into the persisted-snapshot tier, only when Phase 1 has no candidate.
    ///
    /// Phase 1 evaluates the finalized trigger and the backstop independently, the backstop being a fallback that stays
    /// reachable when the finalized trigger ran but found nothing to persist:
    /// - finalized trigger: `finalized >= nextBoundary` and `latest - nextBoundary >= MinReorgDepth` seeds the walk at
    ///   the canonical state of the next boundary block, `persisted + CompactSize` on the schedule;
    /// - backstop: an in-memory depth above `max(backstop, MinReorgDepth + CompactSize)` seeds it at the committed
    ///   head, the backstop being `LongFinalityMaxReorgDepth` with long finality on, else `MaxReorgDepth`.
    ///
    /// Either can find a snapshot of the persisted tier. Phase 2 runs with long finality on and more in-memory bases
    /// than `MaxInMemoryBaseSnapshotCount`. When it converts nothing and a positive `MaxInMemorySnapshotBytes` is
    /// exceeded, the next in-memory snapshot is persisted if that leaves `MinReorgDepth` blocks above it.
    pub fn determine_snapshot_action(&self, latest_snapshot: &StateId) -> PersistenceResult<Option<SnapshotAction>> {
        let current_persisted_state = self.get_current_persisted_state_id()?;
        // A latest snapshot below the persisted block means a deep reorg stranded the persisted base on an orphaned
        // fork; persistence stalls until the chain climbs back.
        if current_persisted_state != StateId::PRE_GENESIS
            && latest_snapshot.block_number < current_persisted_state.block_number
        {
            log::warn!(
                "Latest snapshot {latest_snapshot} is below persisted state {current_persisted_state}; persisted \
                 base may be on an orphaned fork. Skipping persistence."
            );
        }

        // PreGenesis carries the u64::MAX sentinel; the depth from genesis is then the latest block + 1.
        let snapshots_depth = if current_persisted_state == StateId::PRE_GENESIS {
            latest_snapshot.block_number.wrapping_add(1)
        } else {
            latest_snapshot
                .block_number
                .saturating_sub(current_persisted_state.block_number)
        };

        // ---- Phase 1: persistence ----
        let finalized_block_number = self.host.finalized_block_number();
        let next_boundary = self.schedule.next_full_compaction_after(&current_persisted_state);

        // Anchored at the next boundary rather than the CL-reported finalized tip, which can be beyond the local head
        // during catch-up; the boundary is below the head, so its canonical header is known.
        if finalized_block_number >= next_boundary
            && latest_snapshot.block_number.saturating_sub(next_boundary) >= self.min_reorg_depth
            && let Some(canonical_root) = self.host.finalized_state_root(next_boundary)
            && let Some(edge) = self.repository.find_snapshot_to_persist(
                &StateId::new(next_boundary, canonical_root),
                &current_persisted_state,
                self.compact_size,
            )
        {
            return Ok(Some(edge.into()));
        }

        // Seeded from the committed head so the forced persist follows the canonical chain rather than the longest
        // fork, which RemoveSiblingAndDescendents would then orphan.
        if snapshots_depth > self.backstop_reorg_depth
            && let Some(edge) = self.repository.find_snapshot_to_persist(
                &self.forced_persist_seed(latest_snapshot),
                &current_persisted_state,
                self.compact_size,
            )
        {
            log::warn!(
                "In-memory state depth {snapshots_depth} exceeded the force-persist backstop {}; forcing persistence \
                 to bound memory (finalized block {finalized_block_number}).",
                self.backstop_reorg_depth
            );
            return Ok(Some(edge.into()));
        }

        // ---- Phase 2: conversion to the persisted-snapshot tier ----
        let conversion = match &self.long_finality {
            Some(long_finality)
                if self.repository.snapshot_count()
                    > i64::try_from(long_finality.config.max_in_memory_base_snapshot_count).unwrap_or(i64::MAX) =>
            {
                self.try_find_snapshot_to_convert(&current_persisted_state)
            }
            _ => None,
        };
        if conversion.is_none()
            && self.max_in_memory_snapshot_bytes > 0
            && snapshots_depth > self.min_reorg_depth
            && self.repository.in_memory_bytes() > self.max_in_memory_snapshot_bytes
            && let Some(snapshot) = self.try_find_byte_budget_persist(latest_snapshot, &current_persisted_state)
        {
            log::debug!(
                "In-memory snapshot bytes {} exceeded the byte budget {}; forcing persistence to bound memory (depth \
                 {snapshots_depth}, finalized block {finalized_block_number}).",
                self.repository.in_memory_bytes(),
                self.max_in_memory_snapshot_bytes
            );
            return Ok(Some(SnapshotAction::PersistInMemory(snapshot)));
        }

        if conversion.is_none() && snapshots_depth > self.backstop_reorg_depth {
            let mut last_warned_stall = self.last_warned_stall.lock();
            if *last_warned_stall != Some(current_persisted_state) {
                *last_warned_stall = Some(current_persisted_state);
                log::warn!(
                    "In-memory state depth {snapshots_depth} exceeded the force-persist backstop {}, but neither \
                     persistence nor conversion found a candidate (persisted {current_persisted_state}, latest \
                     {latest_snapshot}, finalized block {finalized_block_number}).",
                    self.backstop_reorg_depth
                );
            }
        }

        Ok(conversion)
    }

    fn forced_persist_seed(&self, latest_snapshot: &StateId) -> StateId {
        self.repository
            .get_last_committed_state_id()
            .or_else(|| self.repository.get_last_snapshot_id())
            .unwrap_or(*latest_snapshot)
    }

    /// Port of `TryFindByteBudgetPersist`: the next in-memory snapshot to persist, unless the next candidate is in the
    /// persisted tier, which holds none of the bytes the budget counts, or it would leave fewer than `MinReorgDepth`
    /// blocks above the new base.
    fn try_find_byte_budget_persist(
        &self,
        latest_snapshot: &StateId,
        current_persisted_state: &StateId,
    ) -> Option<Arc<Snapshot>> {
        let committed_head = self.repository.get_last_committed_state_id();
        let seed = committed_head
            .or_else(|| self.repository.get_last_snapshot_id())
            .unwrap_or(*latest_snapshot);
        let Edge::InMemory(in_memory) =
            self.repository
                .find_snapshot_to_persist(&seed, current_persisted_state, self.compact_size)?
        else {
            return None;
        };
        let head_block = committed_head.unwrap_or(*latest_snapshot).block_number;
        (head_block.saturating_sub(in_memory.to().block_number) >= self.min_reorg_depth).then_some(in_memory)
    }

    /// Port of `TryFindSnapshotToConvert`: over the in-memory states in ascending block order, a compacted snapshot
    /// exactly `CompactSize` wide whose `from` is on disk (Branch A), else a base whose `from` is on disk (Branch B).
    ///
    /// Two passes, so the boundary compacted snapshots, which exist only every `CompactSize` blocks, win over the bases
    /// at every block whatever their order. The span check keeps the narrower compacted snapshots of the in-memory
    /// compactor out of Branch A.
    fn try_find_snapshot_to_convert(&self, current_persisted_state: &StateId) -> Option<SnapshotAction> {
        // i64::MAX, not u64::MAX: that is the PreGenesis sentinel, below every state; this one is above every block.
        let ordered = self.repository.get_states_up_to_block(i64::MAX as u64);

        let compacted = ordered.iter().find_map(|state| {
            self.repository
                .try_lease_in_memory_state(state, SnapshotTier::InMemoryCompacted)
                .filter(|compacted| {
                    // From == PreGenesis wraps, so a genesis-spanning snapshot spans To + 1.
                    compacted.to().block_number.wrapping_sub(compacted.from().block_number) == self.compact_size
                        && self.is_on_disk(&compacted.from(), current_persisted_state)
                })
        });
        if let Some(compacted) = compacted {
            return Some(SnapshotAction::ConvertCompacted(compacted));
        }

        ordered
            .iter()
            .find_map(|state| {
                self.repository
                    .try_lease_in_memory_state(state, SnapshotTier::InMemoryBase)
                    .filter(|base| self.is_on_disk(&base.from(), current_persisted_state))
            })
            .map(SnapshotAction::ConvertBase)
    }

    /// `IsOnDisk`: the persisted state, or the end of a persisted base.
    fn is_on_disk(&self, state: &StateId, current_persisted_state: &StateId) -> bool {
        state == current_persisted_state || self.repository.has_base_persisted_snapshot(state)
    }

    /// Acts on up to four decisions `latest_snapshot` allows, then prunes the persisted forks finality rules out.
    ///
    /// A persist removes the forks it orphans first, then writes the snapshot, publishes the persisted state, and
    /// releases every snapshot up to its block in both tiers.
    pub fn add_to_persistence(&self, latest_snapshot: &StateId) -> Result<(), PersistError> {
        let _lock = self.persistence_lock.lock();
        self.fatal_error.check()?;
        self.add_to_persistence_locked(latest_snapshot)
            .inspect_err(|error| self.fatal_error.record(error))
    }

    fn add_to_persistence_locked(&self, latest_snapshot: &StateId) -> Result<(), PersistError> {
        for _ in 0..MAX_DRAIN_ITERATIONS {
            let Some(action) = self.determine_snapshot_action(latest_snapshot)? else {
                break;
            };
            match action {
                SnapshotAction::PersistInMemory(snapshot) => {
                    log::debug!(
                        "Persisting in-memory chunk {}->{}.",
                        snapshot.from().block_number,
                        snapshot.to().block_number
                    );
                    self.persist_and_release(snapshot.to(), || self.persist_snapshot(&snapshot))?;
                }
                SnapshotAction::PersistPersisted(snapshot) => {
                    log::debug!(
                        "Persisting persisted-tier chunk {}->{}.",
                        snapshot.from().block_number,
                        snapshot.to().block_number
                    );
                    self.persist_and_release(snapshot.to(), || self.persist_persisted_snapshot(&snapshot))?;
                }
                SnapshotAction::ConvertCompacted(compacted) => self.convert_compacted_range(&compacted)?,
                SnapshotAction::ConvertBase(base) => self.convert_single_base(&base)?,
            }
        }

        // Finality can rule out forks before the depth and compaction gates allow a persist.
        self.repository
            .remove_finalized_persisted_forks(&self.get_current_persisted_state_id()?)?;
        Ok(())
    }

    /// Removes the forks the persist ending at `to` orphans, runs `persist`, publishes `to` and releases every
    /// snapshot up to its block.
    fn persist_and_release(
        &self,
        to: StateId,
        persist: impl FnOnce() -> Result<(), PersistError>,
    ) -> Result<(), PersistError> {
        self.repository.remove_sibling_and_descendents(&to)?;
        persist()?;
        self.set_current_persisted_state_id(to);
        self.repository.remove_states_until(to.block_number)?;
        Ok(())
    }

    /// Port of `ConvertCompactedRange` (Branch A): converts every in-memory base ending in the range `compacted` spans
    /// whose `from` is on disk or converted with it, removes exactly those states from both in-memory tiers and hands
    /// them to the persisted compactor, which builds the `CompactSize`-wide snapshot; `compacted` only delimits the
    /// range.
    ///
    /// A fork branching below the range keeps its in-memory parent, so it is not converted: a walk that crosses into
    /// the persisted tier never returns to memory, and the fork's tip would be stranded.
    ///
    /// # Panics
    /// With long finality off.
    pub fn convert_compacted_range(&self, compacted: &Snapshot) -> Result<(), PersistError> {
        // From == PreGenesis wraps to block 0, the first block after genesis.
        let start = compacted.from().block_number.wrapping_add(1);
        let end = compacted.to().block_number;

        let current_persisted_state = self.get_current_persisted_state_id()?;
        let mut states: Vec<StateId> = Vec::new();
        for block_number in start..=end {
            for state in self.repository.get_states_at_block_number(block_number) {
                let Some(base) = self
                    .repository
                    .try_lease_in_memory_state(&state, SnapshotTier::InMemoryBase)
                else {
                    continue;
                };
                if self.is_on_disk(&base.from(), &current_persisted_state) || states.contains(&base.from()) {
                    states.push(state);
                }
            }
        }

        self.convert_bases(&states)?;
        // Exactly the converted states: a state added within the range meanwhile stays. A state can be in both tiers.
        for state in &states {
            self.repository
                .remove_and_release_in_memory_known_state(state, SnapshotTier::InMemoryCompacted);
            self.repository
                .remove_and_release_in_memory_known_state(state, SnapshotTier::InMemoryBase);
        }
        self.enqueue_persisted_compaction(states)
    }

    /// Port of `ConvertSingleBase` (Branch B): converts one base, when no `CompactSize`-wide compacted snapshot covers
    /// its range yet.
    ///
    /// # Panics
    /// With long finality off.
    fn convert_single_base(&self, base: &Snapshot) -> Result<(), PersistError> {
        let to = base.to();
        let nodes = self.convert_base(base)?;
        self.events.push(PersistedNodes {
            kind: EventKind::Converted,
            from: base.from(),
            to,
            nodes,
        });

        self.repository
            .remove_and_release_in_memory_known_state(&to, SnapshotTier::InMemoryCompacted);
        self.repository
            .remove_and_release_in_memory_known_state(&to, SnapshotTier::InMemoryBase);
        self.enqueue_persisted_compaction(vec![to])
    }

    /// Converts the in-memory bases ending at `states` in parallel, then queues the nodes of each converted one in the
    /// order of `states`, also when another failed. Process exit stops the loop between bases.
    fn convert_bases(&self, states: &[StateId]) -> Result<(), PersistError> {
        let converted = Mutex::new(Vec::with_capacity(states.len()));
        let indexed: Vec<(usize, StateId)> = states.iter().copied().enumerate().collect();
        let outcome = parallel::for_each_unbalanced(
            &indexed,
            self.persisted_tier_expected().loader.config().processor_count,
            &self.process_exit,
            |(index, state)| {
                if let Some(base) = self
                    .repository
                    .try_lease_in_memory_state(state, SnapshotTier::InMemoryBase)
                {
                    let nodes = self.convert_base(&base)?;
                    converted.lock().push((
                        *index,
                        PersistedNodes {
                            kind: EventKind::Converted,
                            from: base.from(),
                            to: base.to(),
                            nodes,
                        },
                    ));
                }
                Ok::<(), TierError>(())
            },
        );

        let mut converted = converted.into_inner();
        converted.sort_unstable_by_key(|(index, _)| *index);
        for (_, event) in converted {
            self.events.push(event);
        }
        outcome?;
        if self.process_exit.load(Ordering::Acquire) {
            return Err(PersistError::Cancelled);
        }
        Ok(())
    }

    /// `ConvertAndRegister` of one base, observed as `PersistedSnapshotConvertTime`; the nodes it wrote.
    fn convert_base(&self, base: &Snapshot) -> Result<Vec<PersistedNode>, TierError> {
        let started = Instant::now();
        let nodes = self.persisted_tier_expected().loader.convert_and_register(base)?;
        self.stats
            .observe_time(Histogram::PersistedSnapshotConvertTime, started.elapsed());
        Ok(nodes)
    }

    /// `EnqueueAsync` of the converted `states` with the persisted block, waiting for room in the queue.
    fn enqueue_persisted_compaction(&self, states: Vec<StateId>) -> Result<(), PersistError> {
        let persisted_block_number = self.get_current_persisted_state_id()?.block_number;
        match self
            .persisted_tier_expected()
            .compactor
            .enqueue(states, persisted_block_number)
        {
            Ok(()) => Ok(()),
            Err(EnqueueError::Cancelled) => Err(PersistError::Cancelled),
            Err(error @ EnqueueError::Disposed) => Err(PersistenceError::InvalidOperation(error.to_string()).into()),
        }
    }

    fn persisted_tier_expected(&self) -> &PersistedTier {
        self.persisted_tier()
            .expect("only long finality converts into the persisted tier")
    }

    /// Persists every snapshot up to the committed head, finalized or not, ignoring the finality gate, and returns the
    /// persisted state; a backlog in the persisted tier included.
    ///
    /// The walk is seeded at the finalized state when its root is known, which keeps it on the canonical chain, else
    /// at the committed head, else at the longest chain, persisted tips included. Only for loading genesis and tests:
    /// the reorg window collapses to zero.
    pub fn flush_to_persistence(&self) -> Result<StateId, PersistError> {
        let _lock = self.persistence_lock.lock();
        self.fatal_error.check()?;
        self.flush_to_persistence_locked()
            .inspect_err(|error| self.fatal_error.record(error))
    }

    fn flush_to_persistence_locked(&self) -> Result<StateId, PersistError> {
        let mut current_persisted_state = self.get_current_persisted_state_id()?;
        let Some(latest_state_id) = self
            .repository
            .get_last_committed_state_id()
            .or_else(|| self.repository.get_last_snapshot_id())
        else {
            return Ok(current_persisted_state);
        };

        while current_persisted_state == StateId::PRE_GENESIS
            || current_persisted_state.block_number < latest_state_id.block_number
        {
            let mut seed = None;
            let finalized_block_number = self.host.finalized_block_number();
            if current_persisted_state == StateId::PRE_GENESIS
                || finalized_block_number > current_persisted_state.block_number
            {
                seed = self
                    .host
                    .finalized_state_root(finalized_block_number)
                    .map(|root| StateId::new(finalized_block_number, root));
            }
            let seed = seed.unwrap_or(latest_state_id);

            current_persisted_state =
                match self
                    .repository
                    .find_snapshot_to_persist(&seed, &current_persisted_state, self.compact_size)
                {
                    Some(Edge::Persisted(snapshot)) => {
                        self.persist_and_release(snapshot.to(), || self.persist_persisted_snapshot(&snapshot))?;
                        snapshot.to()
                    }
                    Some(Edge::InMemory(snapshot)) => {
                        self.persist_and_release(snapshot.to(), || self.persist_snapshot(&snapshot))?;
                        snapshot.to()
                    }
                    None => break,
                };
        }

        Ok(current_persisted_state)
    }

    /// Port of `ResetPersistedStateId`: re-reads the persisted state from the persistence.
    pub fn reset_persisted_state_id(&self) -> PersistenceResult<()> {
        let current = self.persistence.create_reader(ReaderFlags::NONE)?.current_state();
        self.set_current_persisted_state_id(current);
        Ok(())
    }

    /// Drops every snapshot, in memory or persisted, not on the ancestry of `head`, serialized against persistence.
    pub fn drop_state_not_reachable_from(&self, head: &StateId) -> PersistenceResult<()> {
        let _lock = self.persistence_lock.lock();
        self.repository
            .remove_unreachable_from(head, &self.get_current_persisted_state_id()?)
            .map_err(PersistenceError::from)
            .inspect_err(|error| self.fatal_error.record(&PersistError::Persistence(error.clone())))?;
        Ok(())
    }

    /// Port of `PersistSnapshot`: writes `snapshot` in one batch from its `from` to its `to`.
    ///
    /// The deferred block data is flushed first, and a failure there aborts before anything is written. The batch
    /// then clears the self-destructed storages (not those of new accounts, which had none), sets the accounts, the
    /// slots, the state trie nodes and the storage trie nodes, in that order and in the snapshot's order, which is key
    /// order for a compacted snapshot. Hash-only placeholders are skipped. Once committed, the written nodes are queued
    /// as [`PersistedNodes`].
    pub fn persist_snapshot(&self, snapshot: &Snapshot) -> Result<(), PersistError> {
        let (from, to) = (snapshot.from(), snapshot.to());
        if let Err(error) = self.host.flush_deferred() {
            log::error!("Block-data flush failed before persisting state {to}; aborting this persist. {error}");
            return Err(error.into());
        }

        // From == PreGenesis wraps, so a genesis-spanning snapshot spans To + 1.
        let compact_length = to.block_number.wrapping_sub(from.block_number);
        if compact_length != self.compact_size {
            log::trace!("Persisting non compacted state of length {compact_length}");
        }

        let start = Instant::now();
        let mut batch = self.persistence.create_write_batch(&from, &to, WriteFlags::NONE)?;
        for (address, is_new_account) in snapshot.self_destructed_storage_addresses() {
            if !is_new_account {
                batch.self_destruct(address)?;
            }
        }

        for (address, account) in snapshot.accounts() {
            batch.set_account(address, account.as_ref())?;
        }

        for (key, value) in snapshot.storages() {
            batch.set_storage(&key.address, &key.slot, value.as_ref())?;
        }

        let record = self.events.is_enabled();
        let mut nodes = Vec::new();
        let mut state_nodes_size = 0usize;
        for (path, node) in snapshot.state_nodes() {
            if node.is_hash_only_placeholder() {
                continue;
            }
            state_nodes_size += node.rlp.len();
            // Re-persisted even when already persisted.
            batch.set_state_trie_node(path, node.rlp)?;
            if record {
                nodes.push(PersistedNode {
                    address_hash: None,
                    path: *path,
                    hash: *node.hash,
                });
            }
        }

        let mut storage_nodes_size = 0usize;
        for (key, node) in snapshot.storage_nodes() {
            if node.is_hash_only_placeholder() {
                continue;
            }
            storage_nodes_size += node.rlp.len();
            batch.set_storage_trie_node(&key.address_hash, &key.path, node.rlp)?;
            if record {
                nodes.push(PersistedNode {
                    address_hash: Some(key.address_hash),
                    path: key.path,
                    hash: *node.hash,
                });
            }
        }

        self.stats.observe(
            Histogram::FlatPersistenceSnapshotSize,
            HistogramLabel::StateNodes,
            state_nodes_size as f64,
        );
        self.stats.observe(
            Histogram::FlatPersistenceSnapshotSize,
            HistogramLabel::StorageNodes,
            storage_nodes_size as f64,
        );
        batch.commit()?;
        self.stats.observe_time(Histogram::FlatPersistenceTime, start.elapsed());

        if record {
            self.events.push(PersistedNodes {
                kind: EventKind::Persisted,
                from,
                to,
                nodes,
            });
        }
        Ok(())
    }

    /// Port of `PersistPersistedSnapshot`: writes a snapshot of the persisted tier in one batch from its `from` to its
    /// `to`, from one sequential scan of its table.
    ///
    /// Per address the self-destruct of an existing account, the account, then the slots; then the state nodes and
    /// the storage nodes, whose account hashes the table keeps 20 bytes of. The trie-node RLP of the bases the range
    /// covers is read ahead first and dropped from the OS cache once written, as a compacted snapshot's references
    /// scatter across their blob runs. The queued entry has no nodes: C# marks them when it converts the bases.
    fn persist_persisted_snapshot(&self, snapshot: &PersistedSnapshot) -> Result<(), PersistError> {
        let (from, to) = (snapshot.from(), snapshot.to());
        let start = Instant::now();
        let bases = self.repository.lease_base_snapshots_in_range(&from, &to);
        for base in &bases {
            base.advise_will_need_blob_range();
        }

        let session = snapshot.begin_whole_read_session().map_err(TierError::from)?;
        let reader = session.reader();
        let scanner = Scanner::new(&reader);
        let mut batch = self.persistence.create_write_batch(&from, &to, WriteFlags::NONE)?;
        let mut per_addresses = scanner.per_addresses();
        while let Some(entry) = per_addresses.next_address()? {
            if entry.self_destruct_flag == Some(false) {
                batch.self_destruct(&entry.address)?;
            }
            if entry.has_account {
                batch.set_account(&entry.address, per_addresses.account()?.as_ref())?;
            }
            while let Some(slot) = per_addresses.next_slot()? {
                batch.set_storage(&entry.address, &slot.slot_index(), slot.value.as_ref())?;
            }
        }

        let mut state_nodes = scanner.state_nodes();
        while let Some(node) = state_nodes.next_node()? {
            batch.set_state_trie_node(&node.path, &resolve_trie_rlp(node.node_ref, snapshot)?)?;
        }
        let mut storage_nodes = scanner.storage_nodes();
        while let Some(node) = storage_nodes.next_node()? {
            batch.set_storage_trie_node(
                &node.address_hash,
                &node.path,
                &resolve_trie_rlp(node.node_ref, snapshot)?,
            )?;
        }
        batch.commit()?;

        for base in &bases {
            base.advise_dont_need_blob_range();
        }
        self.stats.observe_time(Histogram::FlatPersistenceTime, start.elapsed());
        self.events.push(PersistedNodes {
            kind: EventKind::Persisted,
            from,
            to,
            nodes: Vec::new(),
        });
        Ok(())
    }
}
