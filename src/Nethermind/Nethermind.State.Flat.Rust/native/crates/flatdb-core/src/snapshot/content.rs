//! The writes of one block: port of `SnapshotContent` and `SnapshotContentCounts` (`Nethermind.State.Flat/Snapshot.cs`)
//! and of `Nethermind.State.Flat/AddressStorageNodeDictionary.cs`.
//!
//! Where C# keeps `TrieNode` objects, a snapshot keeps each node as its hash and its RLP bytes, the bytes living in
//! append-only arenas shared, through `Arc`, with every compacted snapshot built over them, so compaction never copies
//! RLP. A node with empty RLP is a hash-only placeholder, the counterpart of a `TrieNode` for which
//! `TrieNodeExtensions.IsHashOnlyPlaceholder` holds (an undecoded reference without RLP): no valid node RLP is empty.
//!
//! [`SnapshotContentBuilder`] collects the writes; [`SnapshotContent`] is the immutable result. The C# content is
//! written in place and its counts sealed on first observation; building first and freezing gives every reader, and
//! both sides of the repository's memory ledger, the same counts by construction.

use std::hash::Hash;
use std::sync::Arc;

use hashbrown::HashMap;

use super::keys::{StorageKey, StorageNodeKey};
use crate::types::{Account, Address, Hash256, TreePath, U256};

/// The most bytes one arena holds, so a node's offset and length fit in a `u32`.
const MAX_ARENA_BYTES: usize = u32::MAX as usize;

/// Append-only RLP bytes of the trie nodes of one base snapshot, shared with the compacted snapshots over it.
pub(crate) type NodeArena = Arc<Vec<u8>>;

/// A trie node of a snapshot: its hash and where its RLP lies in the snapshot's arenas. Empty for a placeholder.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) struct NodeRef {
    pub(crate) hash: Hash256,
    pub(crate) arena: u32,
    pub(crate) offset: u32,
    pub(crate) len: u32,
}

impl NodeRef {
    pub(crate) const fn placeholder(hash: Hash256) -> Self {
        Self {
            hash,
            arena: 0,
            offset: 0,
            len: 0,
        }
    }

    /// Port of `TrieNodeExtensions.IsHashOnlyPlaceholder`: the node carries a hash but no RLP.
    pub(crate) const fn is_hash_only_placeholder(&self) -> bool {
        self.len == 0
    }

    /// The same node with its arena index moved by `by`, for a snapshot listing these arenas after `by` others.
    pub(crate) const fn shift_arena(self, by: u32) -> Self {
        if self.is_hash_only_placeholder() {
            return self;
        }
        Self {
            arena: self.arena + by,
            ..self
        }
    }

    pub(crate) fn resolve<'a>(&'a self, arenas: &'a [NodeArena]) -> TrieNodeRlp<'a> {
        let rlp = if self.is_hash_only_placeholder() {
            &[]
        } else {
            let start = self.offset as usize;
            &arenas[self.arena as usize][start..start + self.len as usize]
        };
        TrieNodeRlp { hash: &self.hash, rlp }
    }
}

/// A trie node read from a snapshot: its hash and its RLP, which is empty for a hash-only placeholder.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct TrieNodeRlp<'a> {
    pub hash: &'a Hash256,
    pub rlp: &'a [u8],
}

impl TrieNodeRlp<'_> {
    /// Port of `TrieNodeExtensions.IsHashOnlyPlaceholder`.
    pub const fn is_hash_only_placeholder(&self) -> bool {
        self.rlp.is_empty()
    }
}

/// Port of `SnapshotContentCounts`: the entry counts of a snapshot and the memory estimates derived from them.
///
/// The estimates keep the C# per-entry constants, which model the managed footprint rather than this one, so that
/// `MaxInMemorySnapshotBytes` triggers the byte-budget persist at the same points under both backends.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct SnapshotContentCounts {
    pub accounts: usize,
    pub storages: usize,
    pub self_destructed_storage_addresses: usize,
    pub state_nodes: usize,
    pub storage_nodes: usize,
}

impl SnapshotContentCounts {
    const NODE_SIZE_ESTIMATE: i64 = 650;

    pub fn estimate_memory(&self) -> i64 {
        self.accounts as i64 * 172
            + self.storages as i64 * 136
            + self.self_destructed_storage_addresses as i64 * 64
            + self.state_nodes as i64 * (Self::NODE_SIZE_ESTIMATE + 76)
            + self.storage_nodes as i64 * (Self::NODE_SIZE_ESTIMATE + 84)
    }

    /// The estimate of a compacted snapshot, which shares its accounts and nodes with the base snapshots it was built
    /// from and so counts only keys and value-type values.
    pub fn estimate_compacted_memory(&self) -> i64 {
        self.accounts as i64 * 68
            + self.storages as i64 * 136
            + self.self_destructed_storage_addresses as i64 * 64
            + self.state_nodes as i64 * 76
            + self.storage_nodes as i64 * 84
    }
}

/// Collects the writes of one block, as a scope writes a mutable C# `SnapshotContent`. A later write of a key
/// replaces the earlier one.
#[derive(Default)]
pub struct SnapshotContentBuilder {
    accounts: HashMap<Address, Option<Account>>,
    storages: HashMap<StorageKey, Option<U256>>,
    self_destructed: HashMap<Address, bool>,
    state_nodes: HashMap<TreePath, NodeRef>,
    storage_nodes: HashMap<StorageNodeKey, NodeRef>,
    arenas: Vec<Vec<u8>>,
}

impl SnapshotContentBuilder {
    pub fn new() -> Self {
        Self::default()
    }

    /// A builder with room for the writes `counts` expects, so filling it does not rehash.
    pub fn with_capacity(counts: SnapshotContentCounts) -> Self {
        Self {
            accounts: HashMap::with_capacity(counts.accounts),
            storages: HashMap::with_capacity(counts.storages),
            self_destructed: HashMap::with_capacity(counts.self_destructed_storage_addresses),
            state_nodes: HashMap::with_capacity(counts.state_nodes),
            storage_nodes: HashMap::with_capacity(counts.storage_nodes),
            arenas: Vec::new(),
        }
    }

    /// Sets the account; `None` deletes it.
    pub fn set_account(&mut self, address: Address, account: Option<Account>) {
        self.accounts.insert(address, account);
    }

    /// Sets the slot; `None` is a zero write, a deletion.
    pub fn set_storage(&mut self, address: Address, slot: U256, value: Option<U256>) {
        self.storages.insert(StorageKey::new(address, slot), value);
    }

    /// Records that the storage of `address` was cleared; `is_new_account` when the account did not exist before,
    /// so there was nothing to clear in older state.
    pub fn set_self_destruct(&mut self, address: Address, is_new_account: bool) {
        self.self_destructed.insert(address, is_new_account);
    }

    /// Sets the state trie node at `path`; empty `rlp` sets a hash-only placeholder.
    pub fn set_state_node(&mut self, path: TreePath, hash: Hash256, rlp: &[u8]) {
        let node = self.store_rlp(hash, rlp);
        self.state_nodes.insert(path, node);
    }

    /// Sets the node at `path` of the storage trie of the account hashing to `address_hash`; empty `rlp` sets a
    /// hash-only placeholder.
    pub fn set_storage_node(&mut self, address_hash: Hash256, path: TreePath, hash: Hash256, rlp: &[u8]) {
        let node = self.store_rlp(hash, rlp);
        self.storage_nodes.insert(StorageNodeKey::new(address_hash, path), node);
    }

    /// Moves every write of `other` into this builder, replacing equal keys, so a block can be filled in parallel.
    pub fn absorb(&mut self, mut other: Self) {
        let shift = u32::try_from(self.arenas.len()).expect("arena count fits in u32");
        if shift != 0 {
            let shifted = |node: &mut NodeRef| *node = node.shift_arena(shift);
            other.state_nodes.values_mut().for_each(shifted);
            other.storage_nodes.values_mut().for_each(shifted);
        }
        self.arenas.extend(other.arenas);
        merge(&mut self.accounts, other.accounts);
        merge(&mut self.storages, other.storages);
        merge(&mut self.self_destructed, other.self_destructed);
        merge(&mut self.state_nodes, other.state_nodes);
        merge(&mut self.storage_nodes, other.storage_nodes);
    }

    pub fn build(self) -> SnapshotContent {
        SnapshotContent {
            accounts: self.accounts,
            storages: self.storages,
            self_destructed: self.self_destructed,
            state_nodes: self.state_nodes,
            storage_nodes: self.storage_nodes,
            arenas: self.arenas.into_iter().map(Arc::new).collect(),
        }
    }

    fn store_rlp(&mut self, hash: Hash256, rlp: &[u8]) -> NodeRef {
        if rlp.is_empty() {
            return NodeRef::placeholder(hash);
        }

        assert!(rlp.len() <= MAX_ARENA_BYTES, "a trie node of {} bytes", rlp.len());
        if self
            .arenas
            .last()
            .is_none_or(|arena| arena.len() + rlp.len() > MAX_ARENA_BYTES)
        {
            self.arenas.push(Vec::new());
        }

        let arena_index = self.arenas.len() - 1;
        let arena = &mut self.arenas[arena_index];
        let offset = arena.len();
        arena.extend_from_slice(rlp);
        NodeRef {
            hash,
            arena: arena_index as u32,
            offset: offset as u32,
            len: rlp.len() as u32,
        }
    }
}

/// Moves the entries of `from` into `into`, replacing equal keys. An empty `into` takes the map of `from` as it is
/// rather than rehashing every key, as the builders of a block filled in parallel each hold different maps.
fn merge<K: Eq + Hash, V>(into: &mut HashMap<K, V>, from: HashMap<K, V>) {
    if into.is_empty() {
        *into = from;
    } else {
        into.extend(from);
    }
}

/// The immutable writes of one block. Port of `SnapshotContent`; its storage nodes are the flattened dictionary
/// `AddressStorageNodeDictionary` presents.
pub struct SnapshotContent {
    pub(crate) accounts: HashMap<Address, Option<Account>>,
    pub(crate) storages: HashMap<StorageKey, Option<U256>>,
    pub(crate) self_destructed: HashMap<Address, bool>,
    pub(crate) state_nodes: HashMap<TreePath, NodeRef>,
    pub(crate) storage_nodes: HashMap<StorageNodeKey, NodeRef>,
    pub(crate) arenas: Vec<NodeArena>,
}

impl SnapshotContent {
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

    /// The bytes of RLP the arenas hold, including any node a later write of its key replaced.
    pub fn rlp_bytes(&self) -> usize {
        self.arenas.iter().map(|arena| arena.len()).sum()
    }
}
