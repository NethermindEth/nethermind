//! Port of `Nethermind.State.Flat/PersistedSnapshots/PersistedSnapshotBucket.cs`: the persisted snapshots of one tier,
//! by `to`, with their block order, totals and gauges; removing one also removes its catalog row.
//!
//! The bucket's `Arc` of each snapshot is its lease. A snapshot the bucket lets go is released after the lock, where
//! C# disposes it under the lock; only a failed catalog removal releases one under it, which takes the blob table and
//! the arena pool after the bucket, in lock-rank order. That snapshot keeps its files, as its catalog row stays: C#
//! throws before disposing it and so leaks its lease, here it is marked as on shutdown and released. A lookup clones
//! the `Arc` under the read lock, so an observed entry is never found retired and the C# retry of `TryAcquire` has
//! nothing to retry.

use std::collections::{BTreeSet, HashMap};
use std::sync::Arc;
use std::sync::atomic::{AtomicI64, Ordering};

use parking_lot::{RwLock, RwLockReadGuard, RwLockWriteGuard};

use super::TierResult;
use super::codec;
use super::lock_rank::{self, LockRank, Ranked};
use super::snapshot::PersistedSnapshot;
use super::storage::catalog::SnapshotCatalog;
use crate::snapshot::SnapshotTier;
use crate::stats::{TierGaugeRow, TierStats};
use crate::types::StateId;

pub struct PersistedSnapshotBucket {
    tier: SnapshotTier,
    catalog: Arc<SnapshotCatalog>,
    stats: Arc<TierStats>,
    entries: RwLock<Entries>,
    count: AtomicI64,
}

#[derive(Default)]
struct Entries {
    by_to: HashMap<StateId, Arc<PersistedSnapshot>>,
    ordered: BTreeSet<StateId>,
}

impl PersistedSnapshotBucket {
    pub fn new(tier: SnapshotTier, catalog: Arc<SnapshotCatalog>, stats: Arc<TierStats>) -> Self {
        Self {
            tier,
            catalog,
            stats,
            entries: RwLock::default(),
            count: AtomicI64::new(0),
        }
    }

    pub fn count(&self) -> i64 {
        self.count.load(Ordering::SeqCst)
    }

    /// `Max`: the greatest `to` held.
    pub fn max(&self) -> Option<StateId> {
        self.read_entries().ordered.last().copied()
    }

    /// `TryLease`.
    pub fn try_lease(&self, to: &StateId) -> Option<Arc<PersistedSnapshot>> {
        self.read_entries().by_to.get(to).cloned()
    }

    /// `Add`: inserts the snapshot under its `to`, replacing and releasing a snapshot already there; the catalog row is
    /// the caller's.
    pub fn add(&self, snapshot: Arc<PersistedSnapshot>) {
        let to = snapshot.to();
        let displaced = {
            let mut entries = self.write_entries();
            let displaced = entries.by_to.insert(to, snapshot.clone());
            // Re-adding the same instance changes nothing.
            if displaced
                .as_ref()
                .is_some_and(|displaced| Arc::ptr_eq(displaced, &snapshot))
            {
                return;
            }
            if let Some(displaced) = &displaced {
                self.account(displaced, -1);
            }
            entries.ordered.insert(to);
            self.account(&snapshot, 1);
            displaced
        };
        drop(displaced);
    }

    /// `Replace`: swaps the snapshot at `to` for `replacement`, a twin, leaving the totals and gauges as they are;
    /// `false` when nothing is there.
    ///
    /// As C#, whatever is at `to` is swapped, even a snapshot added there after the twin's source was leased (probable
    /// C# bug P-3).
    pub fn replace(&self, to: &StateId, replacement: Arc<PersistedSnapshot>) -> bool {
        let replaced = match self.write_entries().by_to.get_mut(to) {
            Some(entry) => std::mem::replace(entry, replacement),
            None => return false,
        };
        drop(replaced);
        true
    }

    /// `RemoveExact`: removes the snapshot at `to` and its catalog row; whether there was one.
    pub fn remove_exact(&self, to: &StateId) -> TierResult<bool> {
        let mut entries = self.write_entries();
        let removed = self.remove_locked(&mut entries, to);
        drop(entries);
        removed.map(|removed| removed.is_some())
    }

    /// `PruneBefore`: removes, with their catalog rows, the snapshots whose `to` is below `before_block`.
    pub fn prune_before(&self, before_block: u64) -> TierResult<()> {
        let mut entries = self.write_entries();
        let pruned = entries
            .ordered
            .iter()
            .take_while(|to| to.block_number < before_block)
            .copied()
            .collect::<Vec<_>>();
        let removed = pruned
            .iter()
            .map(|to| self.remove_locked(&mut entries, to))
            .collect::<TierResult<Vec<_>>>()?;
        drop(entries);
        drop(removed);
        Ok(())
    }

    /// `PersistAllOnShutdown`: keeps the files of every snapshot for the next session.
    pub fn persist_all_on_shutdown(&self) {
        for snapshot in self.read_entries().by_to.values() {
            snapshot.persist_on_shutdown();
        }
    }

    /// `DisposeAndClear`: releases every snapshot and takes them out of the gauges, leaving the catalog as it is.
    pub fn dispose_and_clear(&self) {
        let released = {
            let mut entries = self.write_entries();
            entries.ordered.clear();
            entries.by_to.drain().map(|(_, snapshot)| snapshot).collect::<Vec<_>>()
        };
        if !released.is_empty() {
            log::debug!(
                "Releasing {} persisted snapshot(s) ({:?}) on teardown",
                released.len(),
                self.tier
            );
        }
        for snapshot in &released {
            self.account(snapshot, -1);
        }
        drop(released);
    }

    /// `RemoveLocked`: the snapshot at `to`, out of the bucket and the catalog; returned for the caller to release
    /// after the lock.
    fn remove_locked(&self, entries: &mut Entries, to: &StateId) -> TierResult<Option<Arc<PersistedSnapshot>>> {
        entries.ordered.remove(to);
        let Some(snapshot) = entries.by_to.remove(to) else {
            return Ok(None);
        };
        self.account(&snapshot, -1);
        self.stats.counters.prunes.fetch_add(1, Ordering::Relaxed);
        if let Err(error) = self.catalog.remove(to, codec::depth(&snapshot.from(), to)) {
            // The row still names the files.
            snapshot.persist_on_shutdown();
            return Err(error);
        }
        log::debug!(
            "Released persisted snapshot {:?} {}->{}",
            self.tier,
            snapshot.from().block_number,
            to.block_number
        );
        Ok(Some(snapshot))
    }

    fn read_entries(&self) -> Ranked<RwLockReadGuard<'_, Entries>> {
        lock_rank::lock(LockRank::Bucket, || self.entries.read())
    }

    fn write_entries(&self) -> Ranked<RwLockWriteGuard<'_, Entries>> {
        lock_rank::lock(LockRank::Bucket, || self.entries.write())
    }

    /// The totals and the `{tier, size}` gauges of `sign` times one snapshot.
    fn account(&self, snapshot: &PersistedSnapshot, sign: i64) {
        let size = snapshot.size();
        self.count.fetch_add(sign, Ordering::SeqCst);
        self.stats.gauges.add(
            self.tier,
            codec::depth(&snapshot.from(), &snapshot.to()),
            TierGaugeRow {
                count: sign,
                memory: sign * size,
                active: 0,
            },
        );
    }
}

impl Drop for PersistedSnapshotBucket {
    fn drop(&mut self) {
        self.dispose_and_clear();
    }
}
