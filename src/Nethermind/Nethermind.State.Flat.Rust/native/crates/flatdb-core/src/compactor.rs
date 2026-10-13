//! Port of `Nethermind.State.Flat/SnapshotCompactor.cs` (`ISnapshotCompactor.cs`): merges the chain of in-memory
//! snapshots ending at a block into one sorted snapshot spanning the block's compact size.
//!
//! The merged trie nodes keep pointing into the arenas of the base snapshots, which the compacted snapshot holds by
//! `Arc`; only keys and node references are merged. C# runs the four map merges as tasks; here three run on worker
//! threads the compactor keeps for its lifetime and the fourth on the compacting thread.

use std::borrow::Cow;
use std::panic::{self, AssertUnwindSafe};
use std::sync::Arc;
use std::thread::{self, JoinHandle};
use std::time::Instant;

use crossbeam_channel::{Receiver, Sender};
use hashbrown::HashMap;

use crate::keccak::account_path;
use crate::log_ring;
use crate::repository::SnapshotRepository;
use crate::schedule::CompactionSchedule;
use crate::snapshot::content::{LiveContent, LiveKind, NodeArena, NodeRef};
use crate::snapshot::keys::SnapshotKey;
use crate::snapshot::sorted::SortedSnapshotContent;
use crate::snapshot::sorted_merge::SortedMergeMap;
use crate::snapshot::{Snapshot, SnapshotTier};
use crate::stats::Histogram;
use crate::types::{Address, Hash256, StateId};

/// The part of `ISnapshotCompactor` the manager's compaction jobs call.
pub trait Compactor: Send + Sync + 'static {
    /// Compacts the chain ending at the base snapshot of `state_id`; whether a compacted snapshot was added.
    fn do_compact_snapshot(&self, state_id: &StateId) -> bool;
}

/// The merges a compaction hands to the workers.
const MERGE_WORKERS: usize = 3;

pub struct SnapshotCompactor {
    compact_size: u64,
    schedule: CompactionSchedule,
    repository: Arc<SnapshotRepository>,
    workers: MergeWorkers,
}

impl SnapshotCompactor {
    /// # Panics
    /// When a merge worker thread cannot be started.
    pub fn new(schedule: CompactionSchedule, repository: Arc<SnapshotRepository>) -> Self {
        Self {
            compact_size: schedule.compact_size(),
            schedule,
            repository,
            workers: MergeWorkers::start(),
        }
    }

    /// Compacts the chain ending at the base snapshot of `state_id` and adds the result to the compacted tier.
    /// Whether a compacted snapshot was added.
    pub fn do_compact_snapshot(&self, state_id: &StateId) -> bool {
        let Some(snapshot) = self
            .repository
            .try_lease_in_memory_state(state_id, SnapshotTier::InMemoryBase)
        else {
            return false;
        };

        let start = Instant::now();
        let snapshots = self.get_snapshots_to_compact(&snapshot);
        if snapshots.is_empty() {
            return false;
        }

        let compacted = Arc::new(self.compact_snapshot_bundle(&snapshots));
        let added = self.repository.try_add(compacted, SnapshotTier::InMemoryCompacted);
        if added {
            self.repository
                .stats()
                .observe_time(Histogram::CompactTime, start.elapsed());
        }
        added
    }

    /// The chain to compact at `snapshot`'s block, oldest first: `get_compact_size` blocks back from it. Empty when the
    /// block compacts nothing, the chain does not start exactly there, or it holds a single snapshot.
    ///
    /// Below the full compact size, the compacted snapshot one `CompactSize` back is dropped first to save memory.
    pub fn get_snapshots_to_compact(&self, snapshot: &Snapshot) -> Vec<Arc<Snapshot>> {
        let block_number = snapshot.to().block_number;
        let compact_size = self.schedule.get_compact_size(block_number);
        if compact_size <= 1 {
            return Vec::new();
        }

        if compact_size != self.compact_size {
            for state_id in self
                .repository
                .get_states_at_block_number(block_number.wrapping_sub(self.compact_size))
            {
                self.repository
                    .remove_and_release_in_memory_known_state(&state_id, SnapshotTier::InMemoryCompacted);
            }
        }

        // Below the compact size this wraps below genesis; the walk's signed heights read it back as such.
        let starting_block_number = block_number.wrapping_sub(compact_size);
        let snapshots = self.repository.assemble_in_memory_snapshots_for_compaction(
            &snapshot.to(),
            starting_block_number,
            compact_size as usize,
        );

        match snapshots.first() {
            None => {
                log::debug!(
                    "Skipping snapshot compaction at block {block_number}: assembled 0 of expected {compact_size} \
                     snapshots from start {starting_block_number}."
                );
                Vec::new()
            }
            // Could happen at start, where the block may not be aligned.
            Some(first) if first.from().block_number != starting_block_number => {
                log::debug!(
                    "Skipping snapshot compaction at block {block_number}: got {} snapshots ({} -> {}), expected start \
                     at {starting_block_number}.",
                    snapshots.len(),
                    first.from().block_number,
                    snapshots.last().map_or(0, |last| last.to().block_number)
                );
                Vec::new()
            }
            Some(_) if snapshots.len() == 1 => {
                log::debug!(
                    "Skipping snapshot compaction at block {block_number}: got only 1 of expected {compact_size} \
                     snapshots from start {starting_block_number}."
                );
                Vec::new()
            }
            Some(_) => snapshots,
        }
    }

    /// Merges `snapshots`, oldest first, into one sorted snapshot from the first `from` to the last `to`: the newest
    /// write of each key wins, except that a slot or storage node written before the last snapshot clearing its
    /// address's storage is dropped.
    ///
    /// # Panics
    /// On an empty `snapshots`.
    pub fn compact_snapshot_bundle(&self, snapshots: &[Arc<Snapshot>]) -> Snapshot {
        let from = snapshots.first().expect("snapshots to compact").from();
        let to = snapshots.last().expect("snapshots to compact").to();
        let usage = compact_usage(self.schedule.get_compact_size(to.block_number));
        let live = LiveContent::create(Arc::clone(self.repository.stats()), LiveKind::Sorted(usage));

        // A slot or node written before the highest snapshot index that clears its address is dropped by the merge.
        let mut self_destruct_merged: HashMap<Address, bool> = HashMap::new();
        let mut slot_clear_boundary: HashMap<Address, usize> = HashMap::new();
        let mut node_clear_boundary: HashMap<Hash256, usize> = HashMap::new();
        for (index, snapshot) in snapshots.iter().enumerate() {
            for (address, is_new_account) in snapshot.self_destructed_storage_addresses() {
                if is_new_account {
                    // An address already cleared as existing stays so.
                    self_destruct_merged.entry(*address).or_insert(true);
                } else {
                    self_destruct_merged.insert(*address, false);
                    slot_clear_boundary.insert(*address, index);
                    node_clear_boundary.insert(account_path(address), index);
                }
            }
        }

        // The compacted snapshot's arenas are its sources' in order; a source's nodes shift by the arenas before it.
        let mut arenas: Vec<NodeArena> = Vec::new();
        let mut arena_shifts = Vec::with_capacity(snapshots.len());
        for snapshot in snapshots {
            arena_shifts.push(u32::try_from(arenas.len()).expect("arena count fits in u32"));
            arenas.extend(snapshot.arenas().iter().cloned());
        }

        let sources = Arc::new(snapshots.to_vec());
        let arena_shifts = Arc::new(arena_shifts);
        let accounts = {
            let sources = Arc::clone(&sources);
            self.workers.run(move || {
                merge_into(
                    &sources,
                    |s| s.sorted_run(|c| &c.accounts, |c| &c.accounts),
                    |_, _| true,
                    |_, account| *account,
                )
            })
        };
        let storages = {
            let sources = Arc::clone(&sources);
            self.workers.run(move || {
                merge_into(
                    &sources,
                    |s| s.sorted_run(|c| &c.storages, |c| &c.storages),
                    |source, key| keep_after_boundary(&slot_clear_boundary, &key.address, source),
                    |_, value| *value,
                )
            })
        };
        let state_nodes = {
            let (sources, arena_shifts) = (Arc::clone(&sources), Arc::clone(&arena_shifts));
            self.workers.run(move || {
                merge_into(
                    &sources,
                    |s| s.sorted_run(|c| &c.state_nodes, |c| &c.state_nodes),
                    |_, _| true,
                    |source, node| node.shift_arena(arena_shifts[source]),
                )
            })
        };
        let storage_nodes = merge_into(
            &sources,
            |s| s.sorted_run(|c| &c.storage_nodes, |c| &c.storage_nodes),
            |source, key| keep_after_boundary(&node_clear_boundary, &key.address_hash, source),
            |source, node: &NodeRef| node.shift_arena(arena_shifts[source]),
        );

        Snapshot::new_sorted(
            from,
            to,
            SortedSnapshotContent {
                accounts: join_merge(accounts),
                storages: join_merge(storages),
                self_destructed: SortedMergeMap::from_unsorted(self_destruct_merged),
                state_nodes: join_merge(state_nodes),
                storage_nodes,
                arenas,
                _live: Some(live),
            },
        )
    }
}

impl Compactor for SnapshotCompactor {
    fn do_compact_snapshot(&self, state_id: &StateId) -> bool {
        SnapshotCompactor::do_compact_snapshot(self, state_id)
    }
}

/// The index of `ResourcePool.CompactUsage` among the compact usages: `Compact2` for 2 or less, up to `Compact2048`.
fn compact_usage(compact_size: u64) -> usize {
    (compact_size.clamp(2, 2048).ilog2() - 1) as usize
}

type MergeJob = Box<dyn FnOnce() + Send>;

/// The threads the merges of every compaction run on, started with the compactor and joined when it drops.
struct MergeWorkers {
    jobs: Option<Sender<MergeJob>>,
    threads: Vec<JoinHandle<()>>,
}

impl MergeWorkers {
    fn start() -> Self {
        let (jobs, receiver) = crossbeam_channel::unbounded::<MergeJob>();
        // The workers log where the thread starting them does.
        let log_sink = log_ring::current();
        let threads = (0..MERGE_WORKERS)
            .map(|index| {
                let receiver = receiver.clone();
                thread::Builder::new()
                    .name(format!("fdb-merge-{index}"))
                    .spawn(move || {
                        log_ring::enter(log_sink);
                        for job in receiver {
                            job();
                        }
                    })
                    .expect("cannot start a compaction merge worker")
            })
            .collect();
        Self {
            jobs: Some(jobs),
            threads,
        }
    }

    /// Runs `merge` on a worker; [`join_merge`] waits for its result.
    fn run<T: Send + 'static>(&self, merge: impl FnOnce() -> T + Send + 'static) -> Receiver<thread::Result<T>> {
        let (result, receiver) = crossbeam_channel::bounded(1);
        let job: MergeJob = Box::new(move || {
            let _ = result.send(panic::catch_unwind(AssertUnwindSafe(merge)));
        });
        self.jobs
            .as_ref()
            .and_then(|jobs| jobs.send(job).ok())
            .expect("the merge workers run while the compactor lives");
        receiver
    }
}

impl Drop for MergeWorkers {
    fn drop(&mut self) {
        drop(self.jobs.take());
        for thread in self.threads.drain(..) {
            let _ = thread.join();
        }
    }
}

/// The result of a merge run on a worker; a panic of the merge resumes here.
fn join_merge<T>(merge: Receiver<thread::Result<T>>) -> T {
    match merge.recv().expect("a merge worker answers every job") {
        Ok(merged) => merged,
        Err(panic) => panic::resume_unwind(panic),
    }
}

/// Port of `StorageBoundaryKeep`: keeps a write of a group cleared in the merge only from the clearing snapshot on.
fn keep_after_boundary<G: Eq + std::hash::Hash>(boundaries: &HashMap<G, usize>, group: &G, source: usize) -> bool {
    boundaries.get(group).is_none_or(|boundary| source >= *boundary)
}

/// Port of `MergeInto`: merges one map of every snapshot, sorting the unsorted ones into transient runs.
fn merge_into<'a, K: SnapshotKey, V: Copy + 'a>(
    snapshots: &'a [Arc<Snapshot>],
    run: impl Fn(&'a Snapshot) -> Cow<'a, [(K, V)]>,
    keep: impl FnMut(usize, &K) -> bool,
    map: impl FnMut(usize, &V) -> V,
) -> SortedMergeMap<K, V> {
    let runs: Vec<Cow<'a, [(K, V)]>> = snapshots.iter().map(|snapshot| run(snapshot)).collect();
    let sources: Vec<&[(K, V)]> = runs.iter().map(|run| &**run).collect();
    SortedMergeMap::merge(&sources, keep, map)
}

#[cfg(test)]
mod tests {
    use std::collections::BTreeSet;
    use std::panic::{self, AssertUnwindSafe};
    use std::thread;

    use super::{MERGE_WORKERS, MergeWorkers, join_merge};

    #[test]
    fn merges_reuse_the_workers_and_a_panicking_one_resumes_in_the_caller() {
        let workers = MergeWorkers::start();
        let names: BTreeSet<String> = (0..12)
            .map(|_| join_merge(workers.run(|| thread::current().name().unwrap_or_default().to_owned())))
            .collect();
        assert!(names.len() <= MERGE_WORKERS && names.iter().all(|name| name.starts_with("fdb-merge-")));

        let panicked = panic::catch_unwind(AssertUnwindSafe(|| {
            join_merge(workers.run(|| -> u32 { panic!("merge failed") }))
        }));
        assert!(panicked.is_err());
        assert_eq!(
            join_merge(workers.run(|| 7)),
            7,
            "the workers outlive a panicking merge"
        );
        drop(workers);
    }
}
