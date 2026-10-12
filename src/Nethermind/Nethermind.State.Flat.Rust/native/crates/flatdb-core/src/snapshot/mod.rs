//! In-memory snapshots: port of `Nethermind.State.Flat/Snapshot.cs` and `Nethermind.State.Flat/SnapshotTier.cs`.
//!
//! A [`Snapshot`] holds the writes between two states, backed by the hash maps of a block's [`SnapshotContent`] or by
//! the sorted maps of a compacted [`SortedSnapshotContent`]. A snapshot is immutable once built and shared as
//! `Arc<Snapshot>`: cloning the `Arc` is `TryAcquire`/`AcquireLease`, dropping it is `Dispose`, and the last drop is
//! `CleanUp`. The C# acquire fails only on a snapshot already cleaned up, which an `Arc` a caller holds never is; the
//! repository hands out clones under the lock of the map that owns it, which is what its retry loop achieves.

pub mod content;
pub mod keys;
pub mod sorted;
pub mod sorted_merge;

use std::borrow::Cow;
use std::hash::BuildHasher;

use hashbrown::HashMap;

use self::content::{SnapshotContent, SnapshotContentCounts, TrieNodeRlp};
use self::keys::{SnapshotKey, StorageKey, StorageNodeKey};
use self::sorted::SortedSnapshotContent;
use self::sorted_merge::{SortedMergeMap, sort_run};
use crate::bloom::BloomFilter;
use crate::types::{Account, Address, Hash256, StateId, TreePath, U256};

/// Port of `SnapshotTier`: a snapshot's tier in the two-tier snapshot graph.
///
/// The persisted tiers belong to the long-finality persisted-snapshot store; the repository walks keep them in their
/// edge priorities so that store can be added without changing the walks, and until then never find a snapshot there.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum SnapshotTier {
    InMemoryBase,
    InMemoryCompacted,
    PersistedBase,
    PersistedSmallCompacted,
    PersistedCompactSized,
    PersistedLargeCompacted,
}

impl SnapshotTier {
    pub const fn is_persisted(self) -> bool {
        !matches!(self, SnapshotTier::InMemoryBase | SnapshotTier::InMemoryCompacted)
    }

    /// Port of `EnsureInMemory`.
    ///
    /// # Panics
    /// On a persisted tier, as C# throws `ArgumentOutOfRangeException`.
    pub fn ensure_in_memory(self) {
        assert!(
            !self.is_persisted(),
            "Only in-memory tiers are valid here, got {self:?}."
        );
    }
}

/// The writes between the states [`Snapshot::from`] and [`Snapshot::to`].
pub struct Snapshot {
    from: StateId,
    to: StateId,
    content: Content,
    counts: SnapshotContentCounts,
}

enum Content {
    Unsorted(SnapshotContent),
    Sorted(SortedSnapshotContent),
}

impl Snapshot {
    pub fn new(from: StateId, to: StateId, content: SnapshotContent) -> Self {
        let counts = content.counts();
        Self {
            from,
            to,
            content: Content::Unsorted(content),
            counts,
        }
    }

    pub(crate) fn new_sorted(from: StateId, to: StateId, content: SortedSnapshotContent) -> Self {
        let counts = content.counts();
        Self {
            from,
            to,
            content: Content::Sorted(content),
            counts,
        }
    }

    pub fn from(&self) -> StateId {
        self.from
    }

    pub fn to(&self) -> StateId {
        self.to
    }

    pub fn is_sorted(&self) -> bool {
        matches!(self.content, Content::Sorted(_))
    }

    pub fn counts(&self) -> SnapshotContentCounts {
        self.counts
    }

    pub fn estimate_memory(&self) -> i64 {
        self.counts.estimate_memory()
    }

    pub fn estimate_compacted_memory(&self) -> i64 {
        self.counts.estimate_compacted_memory()
    }

    pub fn accounts_count(&self) -> usize {
        self.counts.accounts
    }

    pub fn storages_count(&self) -> usize {
        self.counts.storages
    }

    pub fn state_nodes_count(&self) -> usize {
        self.counts.state_nodes
    }

    pub fn storage_nodes_count(&self) -> usize {
        self.counts.storage_nodes
    }

    /// `Some(None)` for an account the snapshot deleted, `None` for one it did not write.
    pub fn try_get_account(&self, address: &Address) -> Option<Option<Account>> {
        self.lookup(address, |c| &c.accounts, |c| &c.accounts).copied()
    }

    /// Whether the snapshot cleared the storage of `address`, and if so whether the account was new.
    pub fn self_destruct(&self, address: &Address) -> Option<bool> {
        self.lookup(address, |c| &c.self_destructed, |c| &c.self_destructed)
            .copied()
    }

    pub fn has_self_destruct(&self, address: &Address) -> bool {
        self.self_destruct(address).is_some()
    }

    /// `Some(None)` for a slot the snapshot zeroed, `None` for one it did not write.
    pub fn try_get_storage(&self, address: &Address, slot: &U256) -> Option<Option<U256>> {
        self.lookup(&StorageKey::new(*address, *slot), |c| &c.storages, |c| &c.storages)
            .copied()
    }

    pub fn try_get_state_node(&self, path: &TreePath) -> Option<TrieNodeRlp<'_>> {
        self.lookup(path, |c| &c.state_nodes, |c| &c.state_nodes)
            .map(|node| node.resolve(self.arenas()))
    }

    pub fn try_get_storage_node(&self, address_hash: &Hash256, path: &TreePath) -> Option<TrieNodeRlp<'_>> {
        self.lookup(
            &StorageNodeKey::new(*address_hash, *path),
            |c| &c.storage_nodes,
            |c| &c.storage_nodes,
        )
        .map(|node| node.resolve(self.arenas()))
    }

    /// The accounts, in key order for a sorted snapshot.
    pub fn accounts(&self) -> impl Iterator<Item = (&Address, &Option<Account>)> {
        self.entries(|c| &c.accounts, |c| &c.accounts)
    }

    /// The addresses whose storage the snapshot cleared, with whether the account was new.
    pub fn self_destructed_storage_addresses(&self) -> impl Iterator<Item = (&Address, bool)> {
        self.entries(|c| &c.self_destructed, |c| &c.self_destructed)
            .map(|(address, is_new)| (address, *is_new))
    }

    pub fn storages(&self) -> impl Iterator<Item = (&StorageKey, &Option<U256>)> {
        self.entries(|c| &c.storages, |c| &c.storages)
    }

    pub fn state_nodes(&self) -> impl Iterator<Item = (&TreePath, TrieNodeRlp<'_>)> {
        let arenas = self.arenas();
        self.entries(|c| &c.state_nodes, |c| &c.state_nodes)
            .map(move |(path, node)| (path, node.resolve(arenas)))
    }

    pub fn storage_nodes(&self) -> impl Iterator<Item = (&StorageNodeKey, TrieNodeRlp<'_>)> {
        let arenas = self.arenas();
        self.entries(|c| &c.storage_nodes, |c| &c.storage_nodes)
            .map(move |(key, node)| (key, node.resolve(arenas)))
    }

    /// Port of `AddStorageKeysTo`: adds every slot key under `hasher`'s hash of its [`StorageKey`].
    pub(crate) fn add_storage_keys_to(&self, filter: &mut BloomFilter, hasher: &impl BuildHasher) {
        for (key, _) in self.storages() {
            filter.add(hasher.hash_one(key));
        }
    }

    pub(crate) fn arenas(&self) -> &[content::NodeArena] {
        match &self.content {
            Content::Unsorted(content) => &content.arenas,
            Content::Sorted(content) => &content.arenas,
        }
    }

    /// The entries of one map as a run sorted by key: borrowed from a sorted snapshot, sorted into a copy from an
    /// unsorted one (`BuildRunFromUnsorted`). Node values index into [`Snapshot::arenas`].
    pub(crate) fn sorted_run<'a, K: SnapshotKey, V: Copy + 'a>(
        &'a self,
        unsorted: impl FnOnce(&'a SnapshotContent) -> &'a HashMap<K, V>,
        sorted: impl FnOnce(&'a SortedSnapshotContent) -> &'a SortedMergeMap<K, V>,
    ) -> Cow<'a, [(K, V)]> {
        match &self.content {
            Content::Unsorted(content) => {
                Cow::Owned(sort_run(unsorted(content).iter().map(|(key, value)| (*key, *value))))
            }
            Content::Sorted(content) => Cow::Borrowed(sorted(content).entries()),
        }
    }

    fn lookup<'a, K: SnapshotKey, V: 'a>(
        &'a self,
        key: &K,
        unsorted: impl FnOnce(&'a SnapshotContent) -> &'a HashMap<K, V>,
        sorted: impl FnOnce(&'a SortedSnapshotContent) -> &'a SortedMergeMap<K, V>,
    ) -> Option<&'a V> {
        match &self.content {
            Content::Unsorted(content) => unsorted(content).get(key),
            Content::Sorted(content) => sorted(content).get(key),
        }
    }

    fn entries<'a, K: SnapshotKey, V: 'a>(
        &'a self,
        unsorted: impl FnOnce(&'a SnapshotContent) -> &'a HashMap<K, V>,
        sorted: impl FnOnce(&'a SortedSnapshotContent) -> &'a SortedMergeMap<K, V>,
    ) -> impl Iterator<Item = (&'a K, &'a V)> {
        match &self.content {
            Content::Unsorted(content) => Either::Left(unsorted(content).iter()),
            Content::Sorted(content) => Either::Right(sorted(content).iter()),
        }
    }
}

/// One of two iterators of the same item.
enum Either<L, R> {
    Left(L),
    Right(R),
}

impl<T, L: Iterator<Item = T>, R: Iterator<Item = T>> Iterator for Either<L, R> {
    type Item = T;

    fn next(&mut self) -> Option<T> {
        match self {
            Either::Left(iter) => iter.next(),
            Either::Right(iter) => iter.next(),
        }
    }

    fn size_hint(&self) -> (usize, Option<usize>) {
        match self {
            Either::Left(iter) => iter.size_hint(),
            Either::Right(iter) => iter.size_hint(),
        }
    }
}
