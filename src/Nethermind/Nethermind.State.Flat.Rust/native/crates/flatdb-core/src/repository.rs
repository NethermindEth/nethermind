//! Port of `Nethermind.State.Flat/SnapshotRepository.cs` (`ISnapshotRepository.cs`): the base and compacted snapshots
//! not yet persisted, the block-ordered set of known states, the persisted-snapshot tier of long finality (four
//! [`PersistedSnapshotBucket`]s over one catalog), and the backward walks over the snapshot graph that assemble read
//! chains, compaction inputs and persist candidates.
//!
//! A repository built with [`SnapshotRepository::new`] or [`SnapshotRepository::with_stats`] has no persisted tier:
//! long finality is off, every persisted edge is absent and every result matches C#, which never fills that tier then.
//! The walks keep the C# edge priorities, persisted tiers included, and the driver keeps the rule that a walk crossing
//! into the persisted tier never returns to memory.
//!
//! Locking mirrors C#: each in-memory tier is one map behind its own lock (a `ConcurrentDictionary` there), the ordered
//! state set has its own reader-writer lock, the last committed state its own lock, and the finality cache its own,
//! which [`SnapshotRepository::remove_unreachable_from`] holds for its whole pass; each bucket has its own lock. No map
//! lock is held while another lock is taken, except that pruning the ordered set reads the base map under the set's
//! write lock, as C# does.

use std::collections::{BTreeMap, BTreeSet, VecDeque};
use std::sync::Arc;
use std::sync::atomic::{AtomicI64, Ordering};

use hashbrown::hash_map::Entry;
use hashbrown::{HashMap, HashSet};
use parking_lot::{Mutex, RwLock};

use crate::persisted::TierResult;
use crate::persisted::bloom_keys::PersistedBloom;
use crate::persisted::bucket::PersistedSnapshotBucket;
use crate::persisted::snapshot::PersistedSnapshot;
use crate::persisted::storage::catalog::SnapshotCatalog;
use crate::snapshot::{Snapshot, SnapshotTier};
use crate::stats::{FlatStats, TierStats};
use crate::types::{Hash256, StateId};

/// The ids of a block range in a walk batch, `PruneBatchSize`.
const PRUNE_BATCH_SIZE: u64 = 1000;

const MIN_ROOT: Hash256 = [0x00; 32];
const MAX_ROOT: Hash256 = [0xFF; 32];

/// Port of the parts of `IStateHeaderProvider` the repository uses: the finality boundary and the canonical state
/// roots at or below it.
pub trait StateHeaderProvider: Send + Sync {
    /// `FinalizedBlockNumber`.
    fn finalized_block_number(&self) -> u64;

    /// `GetFinalizedHeader(blockNumber)?.StateRoot`: `None` above the finalized block or when the header is unknown.
    fn finalized_state_root(&self, block_number: u64) -> Option<Hash256>;
}

/// The snapshots a backward walk assembled, oldest first: the persisted ones, then the in-memory ones above them. Port
/// of `AssembledSnapshotResult`.
#[derive(Default)]
pub struct AssembledSnapshots {
    pub in_memory: Vec<Arc<Snapshot>>,
    pub persisted: Vec<Arc<PersistedSnapshot>>,
}

impl AssembledSnapshots {
    pub fn snapshot_count(&self) -> usize {
        self.in_memory.len() + self.persisted.len()
    }
}

/// A leased edge of the snapshot graph, in memory or persisted.
pub enum Edge {
    InMemory(Arc<Snapshot>),
    Persisted(Arc<PersistedSnapshot>),
}

impl Edge {
    pub fn from(&self) -> StateId {
        match self {
            Edge::InMemory(snapshot) => snapshot.from(),
            Edge::Persisted(snapshot) => snapshot.from(),
        }
    }
}

/// The four buckets of the persisted tier.
struct PersistedBuckets {
    base: PersistedSnapshotBucket,
    small_compacted: PersistedSnapshotBucket,
    large_compacted: PersistedSnapshotBucket,
    compact_sized: PersistedSnapshotBucket,
}

impl PersistedBuckets {
    /// `BucketFor`.
    ///
    /// # Panics
    /// On an in-memory tier, as C# throws `ArgumentOutOfRangeException`.
    fn bucket_for(&self, tier: SnapshotTier) -> &PersistedSnapshotBucket {
        match tier {
            SnapshotTier::PersistedBase => &self.base,
            SnapshotTier::PersistedSmallCompacted => &self.small_compacted,
            SnapshotTier::PersistedLargeCompacted => &self.large_compacted,
            SnapshotTier::PersistedCompactSized => &self.compact_sized,
            SnapshotTier::InMemoryBase | SnapshotTier::InMemoryCompacted => {
                panic!("Only persisted tiers are valid here, got {tier:?}.")
            }
        }
    }

    /// The buckets in the order C# visits them.
    fn all(&self) -> [&PersistedSnapshotBucket; 4] {
        [
            &self.base,
            &self.small_compacted,
            &self.large_compacted,
            &self.compact_sized,
        ]
    }
}

/// Port of `SnapshotRepository`, in-memory tier.
pub struct SnapshotRepository {
    finalized_state_provider: Arc<dyn StateHeaderProvider>,
    base: RwLock<HashMap<StateId, Arc<Snapshot>>>,
    compacted: RwLock<HashMap<StateId, Arc<Snapshot>>>,
    snapshot_count: AtomicI64,
    removed_base_snapshot_count: AtomicI64,
    compacted_snapshot_count: AtomicI64,
    in_memory_bytes: AtomicI64,
    sorted_snapshot_state_ids: RwLock<BTreeSet<StateId>>,
    last_committed_state_id: Mutex<Option<StateId>>,
    /// `_finalityCacheLock` and the finalized roots it guards. Finalized roots are immutable per height, so a root
    /// found once is kept until its height is pruned.
    finality_cache: Mutex<BTreeMap<u64, Hash256>>,
    stats: Arc<FlatStats>,
    /// `None` with long finality off.
    persisted: Option<PersistedBuckets>,
}

impl SnapshotRepository {
    /// A repository with stats of its own.
    pub fn new(finalized_state_provider: Arc<dyn StateHeaderProvider>) -> Self {
        Self::with_stats(finalized_state_provider, Arc::default())
    }

    /// A repository updating the snapshot gauges of `stats`, which the compactor also records into.
    pub fn with_stats(finalized_state_provider: Arc<dyn StateHeaderProvider>, stats: Arc<FlatStats>) -> Self {
        Self::create(finalized_state_provider, stats, None)
    }

    /// A repository with a persisted tier, whose buckets write and remove their rows in `catalog`.
    pub fn with_persisted_tier(
        finalized_state_provider: Arc<dyn StateHeaderProvider>,
        stats: Arc<FlatStats>,
        catalog: Arc<SnapshotCatalog>,
        tier_stats: Arc<TierStats>,
    ) -> Self {
        let bucket = |tier| PersistedSnapshotBucket::new(tier, catalog.clone(), tier_stats.clone());
        let buckets = PersistedBuckets {
            base: bucket(SnapshotTier::PersistedBase),
            small_compacted: bucket(SnapshotTier::PersistedSmallCompacted),
            large_compacted: bucket(SnapshotTier::PersistedLargeCompacted),
            compact_sized: bucket(SnapshotTier::PersistedCompactSized),
        };
        Self::create(finalized_state_provider, stats, Some(buckets))
    }

    fn create(
        finalized_state_provider: Arc<dyn StateHeaderProvider>,
        stats: Arc<FlatStats>,
        persisted: Option<PersistedBuckets>,
    ) -> Self {
        Self {
            finalized_state_provider,
            base: RwLock::default(),
            compacted: RwLock::default(),
            snapshot_count: AtomicI64::new(0),
            removed_base_snapshot_count: AtomicI64::new(0),
            compacted_snapshot_count: AtomicI64::new(0),
            in_memory_bytes: AtomicI64::new(0),
            sorted_snapshot_state_ids: RwLock::default(),
            last_committed_state_id: Mutex::new(None),
            finality_cache: Mutex::default(),
            stats,
            persisted,
        }
    }

    pub fn stats(&self) -> &Arc<FlatStats> {
        &self.stats
    }

    /// In-memory base snapshots held.
    pub fn snapshot_count(&self) -> i64 {
        self.snapshot_count.load(Ordering::SeqCst)
    }

    /// In-memory compacted snapshots held.
    pub fn compacted_snapshot_count(&self) -> i64 {
        self.compacted_snapshot_count.load(Ordering::SeqCst)
    }

    /// Estimated bytes of both in-memory tiers: [`Snapshot::estimate_memory`] of each base snapshot plus
    /// [`Snapshot::estimate_compacted_memory`] of each compacted one.
    pub fn in_memory_bytes(&self) -> i64 {
        self.in_memory_bytes.load(Ordering::SeqCst)
    }

    /// Monotonic count of base snapshots removed, by persistence or fork pruning.
    pub fn removed_base_snapshot_count(&self) -> i64 {
        self.removed_base_snapshot_count.load(Ordering::SeqCst)
    }

    /// Registers `state_id` in the block-ordered set of known states.
    pub fn add_state_id(&self, state_id: StateId) {
        self.sorted_snapshot_state_ids.write().insert(state_id);
    }

    /// Adds `snapshot` to the in-memory `tier` under its `to`, unless that tier already holds a snapshot there; the
    /// repository then owns the snapshot's lease.
    ///
    /// # Panics
    /// On a persisted tier.
    pub fn try_add(&self, snapshot: Arc<Snapshot>, tier: SnapshotTier) -> bool {
        tier.ensure_in_memory();
        let to = snapshot.to();
        let (bytes, count) = if tier == SnapshotTier::InMemoryBase {
            (snapshot.estimate_memory(), &self.snapshot_count)
        } else {
            (snapshot.estimate_compacted_memory(), &self.compacted_snapshot_count)
        };

        // A rejected snapshot is dropped after the lock is released, as C# disposes it outside.
        let rejected = match self.tier_map(tier).write().entry(to) {
            Entry::Occupied(_) => Some(snapshot),
            Entry::Vacant(entry) => {
                entry.insert(snapshot);
                None
            }
        };
        if rejected.is_some() {
            return false;
        }

        count.fetch_add(1, Ordering::SeqCst);
        self.in_memory_bytes.fetch_add(bytes, Ordering::SeqCst);
        self.record_tier_change(tier, 1, bytes);
        true
    }

    /// Leases the snapshot ending at `state_id` in the in-memory `tier`.
    ///
    /// # Panics
    /// On a persisted tier.
    pub fn try_lease_in_memory_state(&self, state_id: &StateId, tier: SnapshotTier) -> Option<Arc<Snapshot>> {
        tier.ensure_in_memory();
        self.tier_map(tier).read().get(state_id).cloned()
    }

    /// Removes the snapshot ending at `state_id` from the in-memory `tier` and releases the repository's lease.
    ///
    /// # Panics
    /// On a persisted tier.
    pub fn remove_and_release_in_memory_known_state(&self, state_id: &StateId, tier: SnapshotTier) -> bool {
        tier.ensure_in_memory();
        let Some(removed) = self.tier_map(tier).write().remove(state_id) else {
            return false;
        };

        if tier == SnapshotTier::InMemoryCompacted {
            let bytes = removed.estimate_compacted_memory();
            self.compacted_snapshot_count.fetch_sub(1, Ordering::SeqCst);
            self.in_memory_bytes.fetch_sub(bytes, Ordering::SeqCst);
            self.record_tier_change(tier, -1, -bytes);
        } else {
            let bytes = removed.estimate_memory();
            self.snapshot_count.fetch_sub(1, Ordering::SeqCst);
            self.removed_base_snapshot_count.fetch_add(1, Ordering::SeqCst);
            self.sorted_snapshot_state_ids.write().remove(state_id);
            self.in_memory_bytes.fetch_sub(bytes, Ordering::SeqCst);
            self.record_tier_change(tier, -1, -bytes);
        }

        true
    }

    /// The `Metrics` updates of `TryAdd` and `RemoveAndReleaseInMemoryKnownState`.
    fn record_tier_change(&self, tier: SnapshotTier, count: i64, bytes: i64) {
        let counters = &self.stats.counters;
        let (count_gauge, memory_gauge) = if tier == SnapshotTier::InMemoryBase {
            (&counters.snapshot_count, &counters.snapshot_memory)
        } else {
            (&counters.compacted_snapshot_count, &counters.compacted_snapshot_memory)
        };
        FlatStats::add(count_gauge, count);
        FlatStats::add(memory_gauge, bytes);
        FlatStats::add(&counters.total_snapshot_memory, bytes);
    }

    /// Whether a base snapshot ends at `state_id`.
    pub fn has_state(&self, state_id: &StateId) -> bool {
        self.base.read().contains_key(state_id)
    }

    /// The known states at `block_number`, a fork having several.
    pub fn get_states_at_block_number(&self, block_number: u64) -> Vec<StateId> {
        self.get_states_in_range(block_number, block_number)
    }

    /// The known states at or below `block_number`, ascending; none for the [`StateId::PRE_GENESIS`] block number.
    pub fn get_states_up_to_block(&self, block_number: u64) -> Vec<StateId> {
        if block_number == u64::MAX {
            return Vec::new();
        }
        self.get_states_in_range(0, block_number)
    }

    /// The greatest known state, persisted tips included: they are not in the ordered set, and after a reorg the
    /// persisted tier can hold an orphan above the in-memory tip.
    pub fn get_last_snapshot_id(&self) -> Option<StateId> {
        let in_memory = self.sorted_snapshot_state_ids.read().last().copied();
        self.persisted_buckets()
            .filter_map(PersistedSnapshotBucket::max)
            .fold(in_memory, |max, bucket_max| max.max(Some(bucket_max)))
    }

    /// Records `state_id` as the state the main processing scope committed last, without any monotonic guard: a reorg
    /// moves the head to a same- or lower-numbered state.
    pub fn set_last_committed_state_id(&self, state_id: StateId) {
        *self.last_committed_state_id.lock() = Some(state_id);
    }

    pub fn get_last_committed_state_id(&self) -> Option<StateId> {
        *self.last_committed_state_id.lock()
    }

    /// Removes every snapshot a persist to `block_number` supersedes: the known states up to and including it, from
    /// both in-memory tiers. Also prunes the finalized roots cached below it.
    pub fn remove_states_until(&self, block_number: u64) {
        for state_id in self.get_states_up_to_block(block_number) {
            // A To can live in both in-memory tiers.
            self.remove_and_release_in_memory_known_state(&state_id, SnapshotTier::InMemoryCompacted);
            self.remove_and_release_in_memory_known_state(&state_id, SnapshotTier::InMemoryBase);
        }

        self.prune_finality_cache_before(block_number);
    }

    /// The in-memory part of `RemoveFinalizedPersistedForks`: prunes the finalized roots cached below the persisted
    /// block. Removing finalized forks only concerns the persisted tier.
    pub fn remove_finalized_persisted_forks(&self, current_persisted_state: &StateId) {
        let first_block = if *current_persisted_state == StateId::PRE_GENESIS {
            0
        } else {
            current_persisted_state.block_number
        };
        self.prune_finality_cache_before(first_block);
    }

    /// Removes the snapshots of forks that persisting `canonical_state_id` orphans: the known states above its block
    /// that do not descend from it. Must run before the persist commits.
    pub fn remove_sibling_and_descendents(&self, canonical_state_id: &StateId) {
        let canonical_block = canonical_state_id.block_number;
        // With a single state at the block, everything above it descends from it.
        if !self.has_fork_at(canonical_block) {
            return;
        }

        let Some(last_snapshot_id) = self.get_last_snapshot_id() else {
            return;
        };
        let max_block = last_snapshot_id.block_number;
        if height(&last_snapshot_id) <= height(canonical_state_id) {
            return;
        }

        let mut batch_start = canonical_block.wrapping_add(1);
        let mut total_pruned = 0usize;
        while batch_start <= max_block {
            let batch_end = batch_start.saturating_add(PRUNE_BATCH_SIZE - 1).min(max_block);
            for state_id in self.get_states_in_range(batch_start, batch_end) {
                if !self.can_reach_state(&state_id, canonical_state_id) {
                    // A To can live in both in-memory tiers.
                    self.remove_and_release_in_memory_known_state(&state_id, SnapshotTier::InMemoryCompacted);
                    self.remove_and_release_in_memory_known_state(&state_id, SnapshotTier::InMemoryBase);
                    total_pruned += 1;
                }
            }

            let Some(next) = batch_end.checked_add(1) else { break };
            batch_start = next;
        }

        if total_pruned > 0 {
            log::info!(
                "Pruned {total_pruned} orphaned non-canonical snapshot(s) above persisted state {canonical_state_id}."
            );
        }
    }

    /// Removes every snapshot whose `to` is not on the `from`-edge ancestry of `head` and records `head` as the last
    /// committed state. Does nothing when `head` is neither `current_persisted_state` nor a state a base snapshot
    /// holds, or when `current_persisted_state` is not on that ancestry.
    ///
    /// Returns the number of states removed from the in-memory tier.
    pub fn remove_unreachable_from(&self, head: &StateId, current_persisted_state: &StateId) -> usize {
        if head != current_persisted_state && !self.has_state(head) {
            log::warn!("Cannot reset head to {head}: no snapshot holds its state.");
            return 0;
        }

        // Held for the whole pass, as C# does.
        let _pass = self.finality_cache.lock();

        // Collected before the walk so a snapshot added concurrently is never removed.
        let mut candidates: Vec<StateId> = self.base.read().keys().copied().collect();
        candidates.extend(self.compacted.read().keys().copied());

        let reachable = self.collect_ancestry(head);

        // The persisted store cannot be rewound to a head that does not descend from it.
        if *current_persisted_state != StateId::PRE_GENESIS && !reachable.contains(current_persisted_state) {
            log::warn!(
                "Cannot reset head to {head}: it does not descend from persisted state {current_persisted_state}."
            );
            return 0;
        }

        let mut total_pruned = 0usize;
        for state_id in &candidates {
            if reachable.contains(state_id) {
                continue;
            }
            // A To can live in both in-memory tiers; `|` attempts both.
            if self.remove_and_release_in_memory_known_state(state_id, SnapshotTier::InMemoryCompacted)
                | self.remove_and_release_in_memory_known_state(state_id, SnapshotTier::InMemoryBase)
            {
                total_pruned += 1;
            }
        }

        // Only ids whose snapshot is gone: one registered for a snapshot added during this pass must stay.
        {
            let mut sorted = self.sorted_snapshot_state_ids.write();
            let base = self.base.read();
            sorted.retain(|state_id| reachable.contains(state_id) || base.contains_key(state_id));
        }

        self.set_last_committed_state_id(*head);

        if total_pruned > 0 {
            log::info!("Pruned {total_pruned} snapshot(s) unreachable from head {head}.");
        }
        total_pruned
    }

    /// Assembles the chain from `base_block` back to `target_state`, the persisted state a reader sees: the
    /// snapshots along the shortest path, oldest first, or none when no path reaches the target.
    pub fn assemble_snapshots(
        &self,
        base_block: &StateId,
        target_state: &StateId,
        estimated_size: usize,
    ) -> AssembledSnapshots {
        if base_block == target_state {
            return AssembledSnapshots::default();
        }
        self.walk_and_assemble(
            base_block,
            estimated_size,
            &mut AssemblePolicy { target: *target_state },
        )
    }

    /// The in-memory chain from `to_state_id` back to `min_block_number`, widest edge first, for compaction; oldest
    /// first, empty when no path reaches that block.
    pub fn assemble_in_memory_snapshots_for_compaction(
        &self,
        to_state_id: &StateId,
        min_block_number: u64,
        estimated_size: usize,
    ) -> Vec<Arc<Snapshot>> {
        self.walk_and_assemble(
            to_state_id,
            estimated_size,
            &mut InMemoryCompactionPolicy { min_block_number },
        )
        .in_memory
    }

    /// The persisted chain from `to_state_id` back toward `min_block_number`, widest edge first, reaching the deepest
    /// block at or above it, oldest first; empty unless it holds at least two snapshots. The window need not be fully
    /// populated.
    pub fn assemble_persisted_snapshots_for_compaction(
        &self,
        to_state_id: &StateId,
        min_block_number: u64,
    ) -> Vec<Arc<PersistedSnapshot>> {
        let estimated_size = to_state_id.block_number.wrapping_sub(min_block_number).clamp(4, 4096) as usize;
        let persisted = self
            .walk_and_assemble(
                to_state_id,
                estimated_size,
                &mut PersistedCompactionPolicy {
                    min_block_number,
                    winner_height: i64::MAX,
                },
            )
            .persisted;
        if persisted.len() < 2 { Vec::new() } else { persisted }
    }

    /// The next snapshot to persist: the candidate spanning at most `compact_size` blocks whose `from` is
    /// `current_persisted_state`, found walking back from `seed`, and that does not conflict with a known finalized
    /// root. `None` when no such snapshot is reachable or when the candidate is persisted.
    pub fn find_snapshot_to_persist(
        &self,
        seed: &StateId,
        current_persisted_state: &StateId,
        compact_size: u64,
    ) -> Option<Arc<Snapshot>> {
        // Height restores the sentinel order: a PreGenesis persisted state is below every seed.
        if height(seed) <= height(current_persisted_state) {
            return None;
        }

        let estimated_size = seed
            .block_number
            .wrapping_sub(current_persisted_state.block_number)
            .clamp(4, 4096) as usize;
        let mut policy = FindPersistPolicy {
            current_persisted_state: *current_persisted_state,
            compact_size,
            repository: self,
            unavailable_roots: HashSet::new(),
        };
        // The candidate is the chain's terminus, the oldest snapshot.
        let assembled = self.walk_and_assemble(seed, estimated_size, &mut policy);
        if !assembled.persisted.is_empty() {
            return None;
        }
        assembled.in_memory.into_iter().next()
    }

    /// Persisted snapshots held, across the four buckets.
    pub fn persisted_snapshot_count(&self) -> i64 {
        self.persisted_buckets().map(PersistedSnapshotBucket::count).sum()
    }

    /// `AddPersistedSnapshot`: indexes `snapshot` in the bucket of its tier, replacing one ending at the same state;
    /// the catalog row is the caller's.
    ///
    /// # Panics
    /// Without a persisted tier.
    pub fn add_persisted_snapshot(&self, snapshot: Arc<PersistedSnapshot>) {
        log::debug!(
            "Created persisted snapshot {:?} {}->{} ({} bytes)",
            snapshot.tier(),
            snapshot.from().block_number,
            snapshot.to().block_number,
            snapshot.size()
        );
        self.buckets().bucket_for(snapshot.tier()).add(snapshot);
    }

    /// `ReplacePersistedSnapshot`: swaps the snapshot ending at `to` in the bucket of `replacement`'s tier for
    /// `replacement`; `false` when there is none.
    ///
    /// # Panics
    /// Without a persisted tier.
    pub fn replace_persisted_snapshot(&self, to: &StateId, replacement: Arc<PersistedSnapshot>) -> bool {
        self.buckets().bucket_for(replacement.tier()).replace(to, replacement)
    }

    /// `TryLeasePersistedState`: the snapshot ending at `to` in the bucket of the persisted `tier`.
    ///
    /// # Panics
    /// On an in-memory tier.
    pub fn try_lease_persisted_state(&self, to: &StateId, tier: SnapshotTier) -> Option<Arc<PersistedSnapshot>> {
        self.persisted.as_ref()?.bucket_for(tier).try_lease(to)
    }

    /// `TryLeaseBasePersistedSnapshot`.
    pub fn try_lease_base_persisted_snapshot(&self, to: &StateId) -> Option<Arc<PersistedSnapshot>> {
        self.try_lease_persisted_state(to, SnapshotTier::PersistedBase)
    }

    /// `LeaseBaseSnapshotsInRange`: the bases tiling `(from, to]`, newest first, following `from` back from `to` and
    /// stopping at the first gap.
    pub fn lease_base_snapshots_in_range(&self, from: &StateId, to: &StateId) -> Vec<Arc<PersistedSnapshot>> {
        let mut bases = Vec::new();
        let mut current = *to;
        // `from` can be PreGenesis; the height keeps the walk going down to it.
        while current != *from && height(&current) > height(from) {
            let Some(base) = self.try_lease_base_persisted_snapshot(&current) else {
                break;
            };
            let parent = base.from();
            bases.push(base);
            if parent == current {
                break;
            }
            current = parent;
        }
        bases
    }

    /// `ShareBloomAcrossRange`: every persisted snapshot within `(from, to]`, down the base chain from `to`, becomes a
    /// twin reading through `shared`, the bloom of a snapshot spanning the range. A gap in the base chain ends the walk;
    /// the snapshots below it keep their own bloom.
    pub fn share_bloom_across_range(
        &self,
        from: &StateId,
        to: &StateId,
        shared: &Arc<PersistedBloom>,
    ) -> TierResult<()> {
        let Some(buckets) = &self.persisted else {
            return Ok(());
        };
        let mut current = *to;
        while height(&current) > height(from) {
            // A compacted snapshot's `from` can dip below `from`, so only the base chain advances the walk.
            let Some(base_parent) = buckets.base.try_lease(&current).map(|base| base.from()) else {
                break;
            };
            for bucket in buckets.all() {
                self.share_bloom_at(bucket, &current, from, to, shared)?;
            }
            if base_parent == current {
                break;
            }
            current = base_parent;
        }
        Ok(())
    }

    /// `ShareBloomAt`: the snapshot of `bucket` ending at `at` becomes a twin reading through `shared`, unless it already
    /// does or it extends past `(from, to]`, whose keys the shared bloom does not cover.
    fn share_bloom_at(
        &self,
        bucket: &PersistedSnapshotBucket,
        at: &StateId,
        from: &StateId,
        to: &StateId,
        shared: &Arc<PersistedBloom>,
    ) -> TierResult<()> {
        let Some(snapshot) = bucket.try_lease(at) else {
            return Ok(());
        };
        if Arc::ptr_eq(snapshot.persisted_bloom(), shared)
            || height(&snapshot.from()) < height(from)
            || height(&snapshot.to()) > height(to)
        {
            return Ok(());
        }
        let twin = Arc::new(snapshot.twin(shared.clone())?);
        // False on a racing removal, which drops the twin.
        self.replace_persisted_snapshot(at, twin);
        Ok(())
    }

    /// `RemovePersistedStatesUntil`: removes the persisted snapshots ending below `block_number`, with their catalog
    /// rows, and the finalized roots cached below it.
    pub fn remove_persisted_states_until(&self, block_number: u64) -> TierResult<()> {
        for bucket in self.persisted_buckets() {
            bucket.prune_before(block_number)?;
        }
        self.prune_finality_cache_before(block_number);
        Ok(())
    }

    /// `RemovePersistedStateExact`: removes the persisted snapshots ending at `to` from every bucket, with their catalog
    /// rows; whether there was one.
    pub fn remove_persisted_state_exact(&self, to: &StateId) -> TierResult<bool> {
        let mut removed = false;
        // Every bucket: a state can end a snapshot of several tiers.
        for bucket in self.persisted_buckets() {
            removed |= bucket.remove_exact(to)?;
        }
        Ok(removed)
    }

    /// `MarkPersistedTierForShutdown`: keeps the files of every persisted snapshot for the next session. Runs over all
    /// four buckets before any is released, as a file can be shared across tiers.
    pub fn mark_persisted_tier_for_shutdown(&self) {
        for bucket in self.persisted_buckets() {
            bucket.persist_all_on_shutdown();
        }
    }

    fn buckets(&self) -> &PersistedBuckets {
        self.persisted
            .as_ref()
            .expect("this repository has no persisted tier: long finality is off")
    }

    fn persisted_buckets(&self) -> impl Iterator<Item = &PersistedSnapshotBucket> {
        self.persisted.iter().flat_map(PersistedBuckets::all)
    }

    fn tier_map(&self, tier: SnapshotTier) -> &RwLock<HashMap<StateId, Arc<Snapshot>>> {
        if tier == SnapshotTier::InMemoryBase {
            &self.base
        } else {
            &self.compacted
        }
    }

    /// Leases the snapshot ending at `state_id` over an edge of `tier`.
    fn try_lease_edge(&self, state_id: &StateId, tier: SnapshotTier) -> Option<Edge> {
        if tier.is_persisted() {
            self.try_lease_persisted_state(state_id, tier).map(Edge::Persisted)
        } else {
            self.try_lease_in_memory_state(state_id, tier).map(Edge::InMemory)
        }
    }

    fn get_states_in_range(&self, block_start_inclusive: u64, block_end_inclusive: u64) -> Vec<StateId> {
        self.sorted_snapshot_state_ids
            .read()
            .range(StateId::new(block_start_inclusive, MIN_ROOT)..=StateId::new(block_end_inclusive, MAX_ROOT))
            .copied()
            .collect()
    }

    fn has_fork_at(&self, block_number: u64) -> bool {
        self.sorted_snapshot_state_ids
            .read()
            .range(StateId::new(block_number, MIN_ROOT)..=StateId::new(block_number, MAX_ROOT))
            .nth(1)
            .is_some()
    }

    fn prune_finality_cache_before(&self, block_number: u64) {
        let mut finalized_roots = self.finality_cache.lock();
        *finalized_roots = finalized_roots.split_off(&block_number);
    }

    /// Port of `GetFinalizedRoot`: the canonical root at `height`, cached once known; heights found unavailable in this
    /// walk are not asked again.
    fn get_finalized_root(&self, height: u64, unavailable_roots: &mut HashSet<u64>) -> Option<Hash256> {
        if unavailable_roots.contains(&height) {
            return None;
        }

        let mut finalized_roots = self.finality_cache.lock();
        if let Some(root) = finalized_roots.get(&height) {
            return Some(*root);
        }
        let root = self.finalized_state_provider.finalized_state_root(height);
        match root {
            Some(root) => {
                finalized_roots.insert(height, root);
            }
            None => {
                unavailable_roots.insert(height);
            }
        }
        root
    }

    /// Every `to` on the `from`-edge ancestry of `head`, following every tier at every node.
    fn collect_ancestry(&self, head: &StateId) -> HashSet<StateId> {
        const TIERS: [SnapshotTier; 6] = [
            SnapshotTier::InMemoryBase,
            SnapshotTier::InMemoryCompacted,
            SnapshotTier::PersistedBase,
            SnapshotTier::PersistedSmallCompacted,
            SnapshotTier::PersistedLargeCompacted,
            SnapshotTier::PersistedCompactSized,
        ];

        let mut seen: HashSet<StateId> = HashSet::from([*head]);
        let mut stack = vec![*head];
        while let Some(current) = stack.pop() {
            for tier in TIERS {
                let Some(edge) = self.try_lease_edge(&current, tier) else {
                    continue;
                };
                let from = edge.from();
                if seen.insert(from) {
                    stack.push(from);
                }
            }
        }
        seen
    }

    /// Whether `target` is on the `from`-edge ancestry of `from`, across both tiers.
    fn can_reach_state(&self, from: &StateId, target: &StateId) -> bool {
        if from == target {
            return true;
        }
        if height(from) <= height(target) {
            return false;
        }

        let mut seen: HashSet<StateId> = HashSet::from([*from]);
        let mut stack = vec![WalkNode {
            current: *from,
            via_persisted: false,
            parent_index: None,
        }];
        while let Some(node) = stack.pop() {
            for &tier in AssemblePolicy::EDGE_PRIORITY {
                if node.via_persisted && !tier.is_persisted() {
                    continue;
                }
                let Some(edge) = self.try_lease_edge(&node.current, tier) else {
                    continue;
                };
                let parent_from = edge.from();
                drop(edge);

                if parent_from == *target {
                    return true;
                }
                // A genesis-spanning snapshot's From is PreGenesis, which Height keeps below the target.
                if height(&parent_from) > height(target) && seen.insert(parent_from) {
                    stack.push(WalkNode {
                        current: parent_from,
                        via_persisted: tier.is_persisted(),
                        parent_index: None,
                    });
                }
            }
        }
        false
    }

    /// Port of `WalkAndAssemble`: a backward BFS over `from` edges in the policy's edge priority, gathering the
    /// winning chain oldest first. A node reached over a persisted edge chains only to persisted tiers.
    fn walk_and_assemble(
        &self,
        start: &StateId,
        estimated_size: usize,
        policy: &mut impl WalkPolicy,
    ) -> AssembledSnapshots {
        let mut queue = VecDeque::from([WalkNode {
            current: *start,
            via_persisted: false,
            parent_index: None,
        }]);
        let mut seen: HashSet<StateId> = HashSet::from([*start]);
        // Holds a lease on every retained edge until the walk ends.
        let mut visited: Vec<(Edge, Option<usize>)> = Vec::with_capacity(estimated_size);
        let mut winner = None;

        'walk: while let Some(node) = queue.pop_front() {
            for &tier in policy.edge_priority() {
                if node.via_persisted && !tier.is_persisted() {
                    continue;
                }
                let Some(edge) = self.try_lease_edge(&node.current, tier) else {
                    continue;
                };
                let from = edge.from();

                let step = policy.decide(&node.current, &from, tier);
                if step == AssembleStep::Skip {
                    continue;
                }
                // Deduplicated after the verdict, so a skipped edge cannot shadow a later candidate edge to the same
                // node.
                if !seen.insert(from) {
                    continue;
                }

                let index = visited.len();
                visited.push((edge, node.parent_index));
                if step != AssembleStep::Traverse {
                    winner = Some(index);
                }
                if step == AssembleStep::WinAndStop {
                    break 'walk;
                }

                queue.push_back(WalkNode {
                    current: from,
                    via_persisted: tier.is_persisted(),
                    parent_index: Some(index),
                });
            }
        }

        // Port of `GatherChain`: the winner is the terminus, so walking parents from it lists the chain oldest first, and
        // a walk never returning to memory keeps each tier's part contiguous.
        let mut assembled = AssembledSnapshots::default();
        let mut walk = winner;
        while let Some(index) = walk {
            let (edge, parent_index) = &visited[index];
            match edge {
                Edge::InMemory(snapshot) => assembled.in_memory.push(Arc::clone(snapshot)),
                Edge::Persisted(snapshot) => assembled.persisted.push(Arc::clone(snapshot)),
            }
            walk = *parent_index;
        }
        assembled
    }
}

/// Port of `Height`: the block number as a signed height, so the `PreGenesis` and `Sync` sentinels at the top of the
/// `u64` range (-1 and -2) sort below genesis, as does a walk bound computed by an underflowing subtraction.
fn height(state_id: &StateId) -> i64 {
    state_id.block_number as i64
}

struct WalkNode {
    current: StateId,
    via_persisted: bool,
    parent_index: Option<usize>,
}

/// Port of `AssembleStep`: the verdict on one parent edge.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum AssembleStep {
    /// Drop the edge.
    Skip,
    /// Follow the edge; not a winner.
    Traverse,
    /// The winner so far; keep walking, the last winner wins.
    Win,
    /// The winner; stop.
    WinAndStop,
}

/// Port of `IAssemblePolicy`.
trait WalkPolicy {
    fn edge_priority(&self) -> &'static [SnapshotTier];

    /// The verdict on the edge from `to`, the node expanded, to its parent `from` over `tier`.
    fn decide(&mut self, to: &StateId, from: &StateId, tier: SnapshotTier) -> AssembleStep;
}

/// Port of `AssemblePolicy`: the full walk of a read. An overshooting persisted edge is the terminus, an overshooting
/// in-memory edge is unusable, and another state at the target's block is a sibling fork.
struct AssemblePolicy {
    target: StateId,
}

impl AssemblePolicy {
    /// Widest skip first, so a read assembles the shortest chain.
    const EDGE_PRIORITY: &'static [SnapshotTier] = &[
        SnapshotTier::PersistedLargeCompacted,
        SnapshotTier::PersistedCompactSized,
        SnapshotTier::InMemoryCompacted,
        SnapshotTier::InMemoryBase,
        SnapshotTier::PersistedSmallCompacted,
        SnapshotTier::PersistedBase,
    ];
}

impl WalkPolicy for AssemblePolicy {
    fn edge_priority(&self) -> &'static [SnapshotTier] {
        Self::EDGE_PRIORITY
    }

    fn decide(&mut self, _to: &StateId, from: &StateId, tier: SnapshotTier) -> AssembleStep {
        if height(from) < height(&self.target) {
            return if tier.is_persisted() {
                AssembleStep::WinAndStop
            } else {
                AssembleStep::Skip
            };
        }
        if *from == self.target {
            return AssembleStep::WinAndStop;
        }
        if from.block_number == self.target.block_number {
            AssembleStep::Skip
        } else {
            AssembleStep::Traverse
        }
    }
}

/// Port of `InMemoryCompactionPolicy`: widest jump first, edges below the minimum block pruned, winning at the first
/// node at it.
struct InMemoryCompactionPolicy {
    min_block_number: u64,
}

impl WalkPolicy for InMemoryCompactionPolicy {
    fn edge_priority(&self) -> &'static [SnapshotTier] {
        &[SnapshotTier::InMemoryCompacted, SnapshotTier::InMemoryBase]
    }

    fn decide(&mut self, _to: &StateId, from: &StateId, _tier: SnapshotTier) -> AssembleStep {
        if height(from) < self.min_block_number as i64 {
            AssembleStep::Skip
        } else if from.block_number == self.min_block_number {
            AssembleStep::WinAndStop
        } else {
            AssembleStep::Traverse
        }
    }
}

/// Port of `PersistedCompactionPolicy`: the persisted tiers widest jump first, edges below the minimum block pruned,
/// winning at the minimum block or else at the deepest node reached.
struct PersistedCompactionPolicy {
    min_block_number: u64,
    /// The height of the deepest winner so far.
    winner_height: i64,
}

impl WalkPolicy for PersistedCompactionPolicy {
    fn edge_priority(&self) -> &'static [SnapshotTier] {
        &[
            SnapshotTier::PersistedLargeCompacted,
            SnapshotTier::PersistedCompactSized,
            SnapshotTier::PersistedSmallCompacted,
            SnapshotTier::PersistedBase,
        ]
    }

    fn decide(&mut self, _to: &StateId, from: &StateId, _tier: SnapshotTier) -> AssembleStep {
        if height(from) < self.min_block_number as i64 {
            AssembleStep::Skip
        } else if from.block_number == self.min_block_number {
            AssembleStep::WinAndStop
        } else if height(from) < self.winner_height {
            self.winner_height = height(from);
            AssembleStep::Win
        } else {
            AssembleStep::Traverse
        }
    }
}

/// Port of `FindPersistPolicy`: walks `from` edges down to the persisted state and wins at the first edge reaching it
/// that spans at most `CompactSize` and whose `to` does not conflict with a known finalized root. A wider edge is
/// only a skip pointer above the persisted state.
struct FindPersistPolicy<'a> {
    current_persisted_state: StateId,
    compact_size: u64,
    repository: &'a SnapshotRepository,
    unavailable_roots: HashSet<u64>,
}

impl WalkPolicy for FindPersistPolicy<'_> {
    fn edge_priority(&self) -> &'static [SnapshotTier] {
        &[
            SnapshotTier::PersistedLargeCompacted,
            SnapshotTier::PersistedCompactSized,
            SnapshotTier::InMemoryCompacted,
            SnapshotTier::PersistedSmallCompacted,
            SnapshotTier::InMemoryBase,
            SnapshotTier::PersistedBase,
        ]
    }

    fn decide(&mut self, to: &StateId, from: &StateId, _tier: SnapshotTier) -> AssembleStep {
        if *from == self.current_persisted_state {
            // A PreGenesis From makes the span wrap to To + 1, the genesis-spanning span.
            let persistable = to.block_number.wrapping_sub(from.block_number) <= self.compact_size
                && self
                    .repository
                    .get_finalized_root(to.block_number, &mut self.unavailable_roots)
                    .is_none_or(|root| to.state_root == root);
            return if persistable {
                AssembleStep::WinAndStop
            } else {
                AssembleStep::Skip
            };
        }

        if height(from) > height(&self.current_persisted_state) {
            AssembleStep::Traverse
        } else {
            AssembleStep::Skip
        }
    }
}
