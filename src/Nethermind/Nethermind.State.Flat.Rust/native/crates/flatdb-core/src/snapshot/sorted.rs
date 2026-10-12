//! Port of `Nethermind.State.Flat/SortedSnapshotContent.cs`: the sorted content of a compacted snapshot.
//!
//! Each map is a [`SortedMergeMap`], so a compacted snapshot persists in key order. Its trie nodes are indexes into the
//! arenas of the base snapshots it was merged from, which it holds by `Arc`: compaction copies no RLP.

use super::content::{NodeArena, NodeRef, SnapshotContentCounts};
use super::keys::{StorageKey, StorageNodeKey};
use super::sorted_merge::SortedMergeMap;
use crate::types::{Account, Address, TreePath, U256};

pub struct SortedSnapshotContent {
    pub(crate) accounts: SortedMergeMap<Address, Option<Account>>,
    pub(crate) storages: SortedMergeMap<StorageKey, Option<U256>>,
    pub(crate) self_destructed: SortedMergeMap<Address, bool>,
    pub(crate) state_nodes: SortedMergeMap<TreePath, NodeRef>,
    pub(crate) storage_nodes: SortedMergeMap<StorageNodeKey, NodeRef>,
    /// The arenas every node indexes into, the arenas of the merged sources in source order.
    pub(crate) arenas: Vec<NodeArena>,
}

impl SortedSnapshotContent {
    /// Port of `CaptureCounts`.
    pub fn counts(&self) -> SnapshotContentCounts {
        SnapshotContentCounts {
            accounts: self.accounts.len(),
            storages: self.storages.len(),
            self_destructed_storage_addresses: self.self_destructed.len(),
            state_nodes: self.state_nodes.len(),
            storage_nodes: self.storage_nodes.len(),
        }
    }
}
