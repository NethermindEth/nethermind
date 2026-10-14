//! Port of `Nethermind.State.Flat/PersistenceManager.cs` (`IPersistenceManager.cs`) with long finality off: which
//! in-memory snapshot to write to the persistence next, and the writing of it.
//!
//! With long finality off C# never takes Phase 2's conversion to the persisted-snapshot tier, so only Phase 1 (the
//! finalized trigger and the force-persist backstop) and the byte-budget persist remain; the history capture hook is
//! out of scope. The persisted tier plugs in where [`PersistenceManager::determine_snapshot_action`] asks the
//! repository for a candidate, which can then be a persisted-tier snapshot.
//!
//! Deliberate deviation: a write that fails inside [`PersistenceManager::persist_snapshot`] discards the batch. C#
//! disposes the batch on the way out, which commits the writes made so far and moves the persisted state pointer
//! to the snapshot's end while `CurrentPersistedStateId` stays behind.
//!
//! A fatal RocksDB error (`DbOnTheRocks.HandleFatalDbError`) makes C# exit before anything else is written; here it
//! fails every later persist instead, until the shell, handed the error by [`PersistenceManager::take_fatal_error`],
//! shuts the node down.

use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::Instant;

use parking_lot::{Mutex, RwLock};
use thiserror::Error;

use crate::events::{PersistedNode, PersistedNodeEvents, PersistedNodes};
use crate::host::{Host, HostError};
use crate::kv::WriteFlags;
use crate::persisted::TierError;
use crate::persistence::{
    Persistence, PersistenceError, PersistenceReader, PersistenceResult, PersistenceWriteBatch, ReaderFlags,
};
use crate::repository::{Edge, SnapshotRepository};
use crate::schedule::CompactionSchedule;
use crate::snapshot::Snapshot;
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
}

/// Failure to persist.
#[derive(Debug, Error, Clone, PartialEq, Eq)]
pub enum PersistError {
    #[error(transparent)]
    Persistence(#[from] PersistenceError),
    /// `IStatePersistenceBarrier.FlushDeferred` failed, so nothing was written.
    #[error(transparent)]
    Host(#[from] HostError),
}

impl PersistError {
    /// Whether the persistence failed with on-disk corruption or an I/O error, after which nothing more may be written.
    pub fn is_fatal(&self) -> bool {
        matches!(self, Self::Persistence(PersistenceError::Kv(error)) if error.is_fatal())
    }
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
    /// The first fatal error a persist hit, with which every later persist fails.
    fatal_error: Mutex<Option<PersistError>>,
    /// Whether [`PersistenceManager::take_fatal_error`] handed the fatal error out.
    fatal_error_taken: AtomicBool,
}

impl<P: Persistence> PersistenceManager<P> {
    /// A persistence manager writing to `persistence`, recording into the repository's stats and, when enabled, queueing
    /// the nodes it persists into `events`.
    pub fn new(
        config: PersistenceManagerConfig,
        schedule: CompactionSchedule,
        host: Arc<dyn Host>,
        persistence: Arc<P>,
        repository: Arc<SnapshotRepository>,
        events: Arc<PersistedNodeEvents>,
    ) -> Self {
        let compact_size = schedule.compact_size();
        Self {
            stats: Arc::clone(repository.stats()),
            persistence,
            repository,
            schedule,
            host,
            events,
            min_reorg_depth: config.min_reorg_depth,
            // Raised to at least one CompactSize above MinReorgDepth, so the finalized trigger, which engages around
            // MinReorgDepth, always has room to act before the backstop fires.
            backstop_reorg_depth: config
                .max_reorg_depth
                .max(config.min_reorg_depth.saturating_add(compact_size)),
            compact_size,
            max_in_memory_snapshot_bytes: config.max_in_memory_snapshot_bytes as i64,
            persistence_lock: Mutex::new(()),
            last_warned_stall: Mutex::new(None),
            current_persisted_state: RwLock::new(StateId::PRE_GENESIS),
            fatal_error: Mutex::new(None),
            fatal_error_taken: AtomicBool::new(false),
        }
    }

    /// The fatal error a persist hit, handed out once, for the shell to shut the node down on as
    /// `DbOnTheRocks.HandleFatalDbError` does.
    pub fn take_fatal_error(&self) -> Option<PersistError> {
        let fatal_error = self.fatal_error.lock();
        match &*fatal_error {
            Some(error) if !self.fatal_error_taken.swap(true, Ordering::AcqRel) => Some(error.clone()),
            _ => None,
        }
    }

    fn fail_after_fatal_error(&self) -> Result<(), PersistError> {
        self.fatal_error.lock().clone().map_or(Ok(()), Err)
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

    /// Port of `DetermineSnapshotAction` with long finality off: the in-memory snapshot to persist next, if any.
    ///
    /// The finalized trigger and the backstop are evaluated independently, the backstop being a fallback that stays
    /// reachable when the finalized trigger ran but found nothing to persist:
    /// - finalized trigger: `finalized >= nextBoundary` and `latest - nextBoundary >= MinReorgDepth` seeds the walk at
    ///   the canonical state of the next boundary block, `persisted + CompactSize` on the schedule;
    /// - backstop: an in-memory depth above `max(MaxReorgDepth, MinReorgDepth + CompactSize)` seeds it at the committed
    ///   head;
    /// - byte budget: with a positive `MaxInMemorySnapshotBytes` exceeded, the next in-memory snapshot is persisted if
    ///   that leaves `MinReorgDepth` blocks above it.
    pub fn determine_snapshot_action(&self, latest_snapshot: &StateId) -> PersistenceResult<Option<Arc<Snapshot>>> {
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
            && let Some(snapshot) = self.find_in_memory_snapshot_to_persist(
                &StateId::new(next_boundary, canonical_root),
                &current_persisted_state,
            )
        {
            return Ok(Some(snapshot));
        }

        // Seeded from the committed head so the forced persist follows the canonical chain rather than the longest
        // fork, which RemoveSiblingAndDescendents would then orphan.
        if snapshots_depth > self.backstop_reorg_depth
            && let Some(snapshot) = self.find_in_memory_snapshot_to_persist(
                &self.forced_persist_seed(latest_snapshot),
                &current_persisted_state,
            )
        {
            log::warn!(
                "In-memory state depth {snapshots_depth} exceeded the force-persist backstop {}; forcing persistence \
                 to bound memory (finalized block {finalized_block_number}).",
                self.backstop_reorg_depth
            );
            return Ok(Some(snapshot));
        }

        // ---- Phase 2: the byte budget; conversion to the persisted-snapshot tier needs long finality ----
        if self.max_in_memory_snapshot_bytes > 0
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
            return Ok(Some(snapshot));
        }

        if snapshots_depth > self.backstop_reorg_depth {
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

        Ok(None)
    }

    /// The repository's next snapshot to persist when it is in memory; persisting a persisted-tier snapshot needs long
    /// finality.
    fn find_in_memory_snapshot_to_persist(
        &self,
        seed: &StateId,
        current_persisted_state: &StateId,
    ) -> Option<Arc<Snapshot>> {
        match self
            .repository
            .find_snapshot_to_persist(seed, current_persisted_state, self.compact_size)?
        {
            Edge::InMemory(snapshot) => Some(snapshot),
            Edge::Persisted(_) => None,
        }
    }

    fn forced_persist_seed(&self, latest_snapshot: &StateId) -> StateId {
        self.repository
            .get_last_committed_state_id()
            .or_else(|| self.repository.get_last_snapshot_id())
            .unwrap_or(*latest_snapshot)
    }

    /// Port of `TryFindByteBudgetPersist`: the next in-memory snapshot to persist, unless it would leave fewer than
    /// `MinReorgDepth` blocks above the new base.
    fn try_find_byte_budget_persist(
        &self,
        latest_snapshot: &StateId,
        current_persisted_state: &StateId,
    ) -> Option<Arc<Snapshot>> {
        let committed_head = self.repository.get_last_committed_state_id();
        let seed = committed_head
            .or_else(|| self.repository.get_last_snapshot_id())
            .unwrap_or(*latest_snapshot);
        let in_memory = self.find_in_memory_snapshot_to_persist(&seed, current_persisted_state)?;
        let head_block = committed_head.unwrap_or(*latest_snapshot).block_number;
        (head_block.saturating_sub(in_memory.to().block_number) >= self.min_reorg_depth).then_some(in_memory)
    }

    /// Persists up to four snapshots `latest_snapshot` makes persistable, then prunes the finality cache below the
    /// persisted state.
    ///
    /// For each: the forks the persist orphans are removed first, then the snapshot is written, the persisted state
    /// published, and every snapshot up to its block released.
    pub fn add_to_persistence(&self, latest_snapshot: &StateId) -> Result<(), PersistError> {
        let _lock = self.persistence_lock.lock();
        self.fail_after_fatal_error()?;
        for _ in 0..MAX_DRAIN_ITERATIONS {
            let Some(to_persist) = self.determine_snapshot_action(latest_snapshot)? else {
                break;
            };
            log::debug!(
                "Persisting in-memory chunk {}->{}.",
                to_persist.from().block_number,
                to_persist.to().block_number
            );
            self.persist_and_release(&to_persist)?;
        }

        // Finality can rule out forks before the depth and compaction gates allow a persist.
        self.repository
            .remove_finalized_persisted_forks(&self.get_current_persisted_state_id()?)
            .map_err(removal_failure)?;
        Ok(())
    }

    fn persist_and_release(&self, snapshot: &Snapshot) -> Result<(), PersistError> {
        let to = snapshot.to();
        self.repository
            .remove_sibling_and_descendents(&to)
            .map_err(removal_failure)?;
        self.persist_snapshot(snapshot).inspect_err(|error| {
            if error.is_fatal() {
                self.fatal_error.lock().get_or_insert_with(|| error.clone());
            }
        })?;
        self.set_current_persisted_state_id(to);
        self.repository
            .remove_states_until(to.block_number)
            .map_err(removal_failure)?;
        Ok(())
    }

    /// Persists every snapshot up to the committed head, finalized or not, ignoring the finality gate, and returns the
    /// persisted state.
    ///
    /// The walk is seeded at the finalized state when its root is known, which keeps it on the canonical chain, else
    /// at the committed head, else at the longest chain. Only for loading genesis and tests: the reorg window
    /// collapses to zero.
    pub fn flush_to_persistence(&self) -> Result<StateId, PersistError> {
        let _lock = self.persistence_lock.lock();
        self.fail_after_fatal_error()?;
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

            let Some(to_persist) = self.find_in_memory_snapshot_to_persist(&seed, &current_persisted_state) else {
                break;
            };
            self.persist_and_release(&to_persist)?;
            current_persisted_state = to_persist.to();
        }

        Ok(current_persisted_state)
    }

    /// Port of `ResetPersistedStateId`: re-reads the persisted state from the persistence.
    pub fn reset_persisted_state_id(&self) -> PersistenceResult<()> {
        let current = self.persistence.create_reader(ReaderFlags::NONE)?.current_state();
        self.set_current_persisted_state_id(current);
        Ok(())
    }

    /// Drops every snapshot not on the ancestry of `head`, serialized against persistence.
    pub fn drop_state_not_reachable_from(&self, head: &StateId) -> PersistenceResult<()> {
        let _lock = self.persistence_lock.lock();
        self.repository
            .remove_unreachable_from(head, &self.get_current_persisted_state_id()?)
            .map_err(removal_failure)?;
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
            self.events.push(PersistedNodes { from, to, nodes });
        }
        Ok(())
    }
}

/// A failed removal from the persisted tier, which fails only on the catalog row it removes: a database write, as the
/// persist's own.
fn removal_failure(error: TierError) -> PersistenceError {
    match error {
        TierError::Catalog(error) => PersistenceError::Kv(error),
        error => PersistenceError::InvalidOperation(error.to_string()),
    }
}
