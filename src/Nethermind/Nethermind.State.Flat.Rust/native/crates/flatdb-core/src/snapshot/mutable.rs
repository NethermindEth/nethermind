//! The content a scope writes while a block is processed: the write side of `SnapshotContent`
//! (`Nethermind.State.Flat/Snapshot.cs`) as `Nethermind.State.Flat/SnapshotBundle.cs` fills it, frozen into an immutable
//! [`SnapshotContent`] at commit.
//!
//! Every keyed kind is split into 16 shards by the low nibble of the first address (or account path) byte, each under
//! its own lock, so the per-address writers of a block rarely meet. The locking rules:
//! 1. No I/O happens under a lock: only in-memory probes and copies into the caller's buffer.
//! 2. Outside a commit, a thread holds at most one lock of a content at a time.
//! 3. A commit takes every lock in one global order ([`MutableContent::lock_all`]): the self-destructs, the 16 account
//!    shards, the 16 slot shards, the state nodes, the 16 storage node shards.
//! 4. The written bit of a kind is set under that kind's lock after the insert, and reset only by the commit holding
//!    every lock, after it published the frozen content.
//!
//! Debug and test builds check 2 and 3 with a per-thread lock-rank checker.
//!
//! Trie node RLP is copied once into 32 KiB chunks that are never reallocated. Chunk ids are handed out per content
//! from one counter, so a frozen content lists every shard's chunks at their ids and no node reference moves.

use std::mem;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, AtomicU8, AtomicU32, Ordering};

use hashbrown::HashMap;
use parking_lot::Mutex;

use self::layout::{SlotShard, StorageNodeMap};
use self::rank::{CommitScope, Ranked, lock};
use super::content::{LiveContent, LiveKind, NodeArena, NodeRef, SnapshotContent, TrieNodeRlp};
use super::keys::{SHARDS, shard_of};
use crate::stats::{ContentHint, FlatStats};
use crate::types::{Account, Address, Hash256, TreePath, U256};

/// The size of an RLP chunk; a larger node gets a chunk of its own size.
const CHUNK_BYTES: usize = 32 * 1024;

const ACCOUNTS: u8 = 1;
const SLOTS: u8 = 1 << 1;
const SELF_DESTRUCTS: u8 = 1 << 2;
const STATE_NODES: u8 = 1 << 3;
const STORAGE_NODES: u8 = 1 << 4;

const SELF_DESTRUCTS_RANK: u8 = 0;
const ACCOUNTS_RANK: u8 = 1;
const SLOTS_RANK: u8 = ACCOUNTS_RANK + SHARDS as u8;
const STATE_NODES_RANK: u8 = SLOTS_RANK + SHARDS as u8;
const STORAGE_NODES_RANK: u8 = STATE_NODES_RANK + 1;

/// `ResourcePool.Usage` of a block's content: sizes its maps and labels its pooled-resource gauges.
#[repr(u8)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Usage {
    MainBlockProcessing = 0,
    PostMainBlockProcessing = 1,
    ReadOnlyProcessingEnv = 2,
}

impl Usage {
    /// # Panics
    /// On a value that is not a usage.
    pub fn from_u8(value: u8) -> Self {
        match value {
            0 => Usage::MainBlockProcessing,
            1 => Usage::PostMainBlockProcessing,
            2 => Usage::ReadOnlyProcessingEnv,
            _ => panic!("not a content usage: {value}"),
        }
    }
}

/// A trie node of a batch: its path, its hash (zero for an inline node), and where its RLP lies in the batch's blob.
/// Empty RLP is a hash-only placeholder.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct NodeRecord {
    pub path: TreePath,
    pub hash: Hash256,
    pub rlp_offset: usize,
    pub rlp_len: usize,
}

#[derive(Default)]
struct AccountShard {
    changed: HashMap<Address, Option<Account>>,
    /// Accounts read through the scope (`ReadAccounts`); never part of a frozen content.
    read: HashMap<Address, Option<Account>>,
}

#[derive(Default)]
struct StateNodeShard {
    nodes: HashMap<TreePath, NodeRef>,
    arena: ChunkedArena,
}

#[derive(Default)]
struct StorageNodeShard {
    nodes: StorageNodeMap,
    arena: ChunkedArena,
}

/// The writes of one block, shared by every thread of its scope.
pub struct MutableContent {
    stats: Arc<FlatStats>,
    usage: AtomicU8,
    /// Whether the content is counted live in the pooled-resource gauges.
    live: AtomicBool,
    /// One bit per kind written since the last freeze; readers skip a kind while its bit is 0.
    written: AtomicU8,
    next_chunk: AtomicU32,
    self_destructed: Mutex<HashMap<Address, bool>>,
    accounts: [Mutex<AccountShard>; SHARDS],
    slots: [Mutex<SlotShard>; SHARDS],
    state_nodes: Mutex<StateNodeShard>,
    storage_nodes: [Mutex<StorageNodeShard>; SHARDS],
}

impl MutableContent {
    /// An empty content, which allocates its maps on their first insert, sized from the last content frozen under
    /// `usage`.
    pub fn new(stats: Arc<FlatStats>, usage: Usage) -> Self {
        Self {
            stats,
            usage: AtomicU8::new(usage as u8),
            live: AtomicBool::new(false),
            written: AtomicU8::new(0),
            next_chunk: AtomicU32::new(0),
            self_destructed: Mutex::default(),
            accounts: Default::default(),
            slots: Default::default(),
            state_nodes: Mutex::default(),
            storage_nodes: Default::default(),
        }
    }

    pub fn usage(&self) -> Usage {
        Usage::from_u8(self.usage.load(Ordering::Relaxed))
    }

    /// Sets the account; `None` deletes it.
    pub fn set_account(&self, address: &Address, account: Option<Account>) {
        let mut shard = self.account_shard(address);
        reserve_first(&mut shard.changed, self.shard_hint(ContentHint::Accounts));
        shard.changed.insert(*address, account);
        self.mark_written(ACCOUNTS);
    }

    /// Port of `PromoteAccount`: caches a read account unless the content holds the address already.
    pub fn promote_account(&self, address: &Address, account: Option<Account>) {
        let mut shard = self.account_shard(address);
        if shard.changed.contains_key(address) || shard.read.contains_key(address) {
            return;
        }
        reserve_first(&mut shard.read, self.shard_hint(ContentHint::Accounts));
        shard.read.insert(*address, account);
        self.mark_written(ACCOUNTS);
    }

    /// Port of `SetChangedSlot`: a zero value deletes the slot.
    pub fn set_slot(&self, address: &Address, slot: &U256, value: &U256) {
        let value = (!value.is_zero()).then_some(*value);
        let mut shard = lock(
            &self.slots[shard_of(address[0])],
            SLOTS_RANK + shard_of(address[0]) as u8,
        );
        shard.set(address, slot, value, self.shard_hint(ContentHint::SlotAddresses));
        self.mark_written(SLOTS);
    }

    /// Records the clear of `address`'s storage unless it is recorded already, as `TryAdd` does. Whether it was.
    pub fn set_self_destruct_if_absent(&self, address: &Address, is_new_account: bool) -> bool {
        let mut self_destructed = lock(&self.self_destructed, SELF_DESTRUCTS_RANK);
        if self_destructed.contains_key(address) {
            return false;
        }
        reserve_first(&mut self_destructed, self.stats_hint(ContentHint::SelfDestructs));
        self_destructed.insert(*address, is_new_account);
        self.mark_written(SELF_DESTRUCTS);
        true
    }

    /// Drops the slots of `address` and the storage trie nodes of `address_hash`, its account path.
    pub fn remove_storage(&self, address: &Address, address_hash: &Hash256) {
        lock(
            &self.slots[shard_of(address[0])],
            SLOTS_RANK + shard_of(address[0]) as u8,
        )
        .remove_address(address);
        self.storage_node_shard(address_hash).nodes.remove_address(address_hash);
    }

    /// Sets the state trie node at `path`; empty `rlp` sets a hash-only placeholder.
    pub fn set_state_node(&self, path: &TreePath, hash: &Hash256, rlp: &[u8]) {
        let mut shard = lock(&self.state_nodes, STATE_NODES_RANK);
        let StateNodeShard { nodes, arena } = &mut *shard;
        let node = arena.store(&self.next_chunk, hash, rlp);
        reserve_first(nodes, self.stats_hint(ContentHint::StateNodes));
        nodes.insert(*path, node);
        self.mark_written(STATE_NODES);
    }

    /// Sets every node of `records`, whose RLP lies in `blob`, under one lock; an empty batch writes nothing.
    ///
    /// # Panics
    /// When a record's RLP is not inside `blob`.
    pub fn put_state_nodes(&self, records: &[NodeRecord], blob: &[u8]) {
        if records.is_empty() {
            return;
        }
        let rlps: Vec<&[u8]> = records.iter().map(|record| record_rlp(record, blob)).collect();
        let mut shard = lock(&self.state_nodes, STATE_NODES_RANK);
        let StateNodeShard { nodes, arena } = &mut *shard;
        reserve_first(nodes, self.stats_hint(ContentHint::StateNodes));
        nodes.reserve(records.len());
        for (record, rlp) in records.iter().zip(rlps) {
            let node = arena.store(&self.next_chunk, &record.hash, rlp);
            nodes.insert(record.path, node);
        }
        self.mark_written(STATE_NODES);
    }

    /// Sets the node at `path` of the storage trie of the account hashing to `address_hash`; empty `rlp` sets a
    /// hash-only placeholder.
    pub fn set_storage_node(&self, address_hash: &Hash256, path: &TreePath, hash: &Hash256, rlp: &[u8]) {
        let hint = self.shard_hint(ContentHint::StorageNodeAddresses);
        let mut shard = self.storage_node_shard(address_hash);
        let StorageNodeShard { nodes, arena } = &mut *shard;
        let node = arena.store(&self.next_chunk, hash, rlp);
        nodes.insert_all(address_hash, [(*path, node)], hint);
        self.mark_written(STORAGE_NODES);
    }

    /// As [`MutableContent::put_state_nodes`], in the storage trie of the account hashing to `address_hash`.
    ///
    /// # Panics
    /// When a record's RLP is not inside `blob`.
    pub fn put_storage_nodes(&self, address_hash: &Hash256, records: &[NodeRecord], blob: &[u8]) {
        if records.is_empty() {
            return;
        }
        let rlps: Vec<&[u8]> = records.iter().map(|record| record_rlp(record, blob)).collect();
        let hint = self.shard_hint(ContentHint::StorageNodeAddresses);
        let mut shard = self.storage_node_shard(address_hash);
        let StorageNodeShard { nodes, arena } = &mut *shard;
        let next_chunk = &self.next_chunk;
        let stored = records
            .iter()
            .zip(rlps)
            .map(|(record, rlp)| (record.path, arena.store(next_chunk, &record.hash, rlp)));
        nodes.insert_all(address_hash, stored, hint);
        self.mark_written(STORAGE_NODES);
    }

    /// The account the content holds, changed or read: `Some(None)` for a deleted or absent one, `None` if the
    /// content holds nothing for it.
    pub fn account(&self, address: &Address) -> Option<Option<Account>> {
        if !self.has_written(ACCOUNTS) {
            return None;
        }
        let shard = self.account_shard(address);
        shard.changed.get(address).or_else(|| shard.read.get(address)).copied()
    }

    /// The slot the content holds: `Some(None)` for a zeroed one.
    pub fn slot(&self, address: &Address, slot: &U256) -> Option<Option<U256>> {
        if !self.has_written(SLOTS) {
            return None;
        }
        lock(
            &self.slots[shard_of(address[0])],
            SLOTS_RANK + shard_of(address[0]) as u8,
        )
        .get(address, slot)
    }

    /// Whether the content cleared the storage of `address`, and if so whether the account was new.
    pub fn self_destruct(&self, address: &Address) -> Option<bool> {
        if !self.has_written(SELF_DESTRUCTS) {
            return None;
        }
        lock(&self.self_destructed, SELF_DESTRUCTS_RANK).get(address).copied()
    }

    /// Calls `read` with the state trie node at `path`, under the shard's lock.
    pub fn with_state_node<T>(&self, path: &TreePath, read: impl FnOnce(TrieNodeRlp<'_>) -> T) -> Option<T> {
        if !self.has_written(STATE_NODES) {
            return None;
        }
        let shard = lock(&self.state_nodes, STATE_NODES_RANK);
        let node = shard.nodes.get(path)?;
        Some(read(shard.arena.resolve(node)))
    }

    /// Calls `read` with the node at `path` of the storage trie of `address_hash`, under the shard's lock.
    pub fn with_storage_node<T>(
        &self,
        address_hash: &Hash256,
        path: &TreePath,
        read: impl FnOnce(TrieNodeRlp<'_>) -> T,
    ) -> Option<T> {
        if !self.has_written(STORAGE_NODES) {
            return None;
        }
        let shard = self.storage_node_shard(address_hash);
        let node = shard.nodes.get(address_hash, path)?;
        Some(read(shard.arena.resolve(node)))
    }

    /// Freezes the content, which is left empty for the next block.
    pub fn freeze(&mut self) -> SnapshotContent {
        let mut locked = self.lock_all();
        let content = locked.freeze();
        locked.reset_written();
        content
    }

    /// Takes every lock in the commit order, so the content can be frozen and published before any writer or reader
    /// sees it again.
    pub fn lock_all(&self) -> AllLocked<'_> {
        let commit = CommitScope::enter();
        let self_destructed = lock(&self.self_destructed, SELF_DESTRUCTS_RANK);
        let accounts = rank::lock_shards(&self.accounts, ACCOUNTS_RANK);
        let slots = rank::lock_shards(&self.slots, SLOTS_RANK);
        let state_nodes = lock(&self.state_nodes, STATE_NODES_RANK);
        let storage_nodes = rank::lock_shards(&self.storage_nodes, STORAGE_NODES_RANK);
        AllLocked {
            storage_nodes,
            state_nodes,
            slots,
            accounts,
            self_destructed,
            content: self,
            _commit: commit,
        }
    }

    fn account_shard(&self, address: &Address) -> Ranked<'_, AccountShard> {
        let shard = shard_of(address[0]);
        lock(&self.accounts[shard], ACCOUNTS_RANK + shard as u8)
    }

    fn storage_node_shard(&self, address_hash: &Hash256) -> Ranked<'_, StorageNodeShard> {
        let shard = shard_of(address_hash[0]);
        lock(&self.storage_nodes[shard], STORAGE_NODES_RANK + shard as u8)
    }

    fn has_written(&self, kind: u8) -> bool {
        self.written.load(Ordering::Acquire) & kind != 0
    }

    /// Called under the lock of `kind` after an insert; the first write counts the content live.
    fn mark_written(&self, kind: u8) {
        if self.written.load(Ordering::Relaxed) & kind == 0 {
            self.written.fetch_or(kind, Ordering::Release);
        }
        if !self.live.load(Ordering::Relaxed) && !self.live.swap(true, Ordering::AcqRel) {
            LiveContent::count_created(&self.stats, self.live_kind());
        }
    }

    fn live_kind(&self) -> LiveKind {
        LiveKind::Block(self.usage() as usize)
    }

    /// The last frozen count of `hint` under the content's usage.
    fn stats_hint(&self, hint: ContentHint) -> usize {
        self.stats.content_hints.get(self.usage() as usize, hint)
    }

    /// [`MutableContent::stats_hint`] for one of the 16 shards.
    fn shard_hint(&self, hint: ContentHint) -> usize {
        self.stats_hint(hint) / SHARDS
    }
}

impl Drop for MutableContent {
    fn drop(&mut self) {
        if *self.live.get_mut() {
            LiveContent::release(&self.stats, self.live_kind());
        }
    }
}

/// Every lock of a [`MutableContent`], taken by [`MutableContent::lock_all`]. Dropping it unlocks.
pub struct AllLocked<'a> {
    // Declared in the reverse of the lock order, so the kinds unlock in reverse; the shards of a kind unlock in index
    // order.
    storage_nodes: [Ranked<'a, StorageNodeShard>; SHARDS],
    state_nodes: Ranked<'a, StateNodeShard>,
    slots: [Ranked<'a, SlotShard>; SHARDS],
    accounts: [Ranked<'a, AccountShard>; SHARDS],
    self_destructed: Ranked<'a, HashMap<Address, bool>>,
    content: &'a MutableContent,
    _commit: CommitScope,
}

impl AllLocked<'_> {
    /// Moves the writes out into an immutable content and drops the read cache, which a snapshot never exports
    /// (`Snapshot.cs`); records their counts as the size of the next content of this usage.
    pub fn freeze(&mut self) -> SnapshotContent {
        let content = self.content;
        let usage = content.usage() as usize;
        let hints = &content.stats.content_hints;

        let self_destructed = mem::take(&mut *self.self_destructed);
        let mut accounts = HashMap::with_capacity(self.accounts.iter().map(|shard| shard.changed.len()).sum());
        for shard in &mut self.accounts {
            accounts.extend(mem::take(&mut shard.changed));
            shard.read = HashMap::new();
        }
        hints.set(usage, ContentHint::Accounts, accounts.len());
        hints.set(usage, ContentHint::SelfDestructs, self_destructed.len());
        hints.set(
            usage,
            ContentHint::SlotAddresses,
            self.slots.iter().map(|shard| shard.groups()).sum(),
        );
        hints.set(
            usage,
            ContentHint::StorageNodeAddresses,
            self.storage_nodes.iter().map(|shard| shard.nodes.groups()).sum(),
        );
        let storages = layout::freeze_slots(self.slots.each_mut().map(|shard| &mut **shard));

        let chunk_count = content.next_chunk.swap(0, Ordering::Relaxed) as usize;
        let mut arenas: Vec<Option<NodeArena>> = vec![None; chunk_count];
        let state_nodes = mem::take(&mut self.state_nodes.nodes);
        hints.set(usage, ContentHint::StateNodes, state_nodes.len());
        self.state_nodes.arena.move_into(&mut arenas);
        for shard in &mut self.storage_nodes {
            shard.arena.move_into(&mut arenas);
        }
        let storage_nodes = layout::freeze_storage_nodes(self.storage_nodes.each_mut().map(|shard| &mut shard.nodes));

        let kind = content.live_kind();
        let live = if content.live.swap(false, Ordering::AcqRel) {
            LiveContent::adopt(Arc::clone(&content.stats), kind)
        } else {
            LiveContent::create(Arc::clone(&content.stats), kind)
        };
        SnapshotContent {
            accounts,
            storages,
            self_destructed,
            state_nodes,
            storage_nodes,
            arenas: arenas
                .into_iter()
                .map(|arena| arena.expect("every chunk id belongs to one shard"))
                .collect(),
            _live: Some(live),
        }
    }

    /// Lets readers skip the content again; call once the frozen content is published where they look next.
    pub fn reset_written(&self) {
        self.content.written.store(0, Ordering::Release);
    }

    /// The usage of the content written after this freeze.
    pub fn set_usage(&self, usage: Usage) {
        self.content.usage.store(usage as u8, Ordering::Relaxed);
    }
}

/// The RLP chunks of one shard, each with its id in the content.
#[derive(Default)]
struct ChunkedArena {
    chunks: Vec<(u32, Vec<u8>)>,
}

impl ChunkedArena {
    fn store(&mut self, next_chunk: &AtomicU32, hash: &Hash256, rlp: &[u8]) -> NodeRef {
        if rlp.is_empty() {
            return NodeRef::placeholder(*hash);
        }
        let len = u32::try_from(rlp.len()).unwrap_or_else(|_| panic!("a trie node of {} bytes", rlp.len()));
        if self
            .chunks
            .last()
            .is_none_or(|(_, chunk)| chunk.capacity() - chunk.len() < rlp.len())
        {
            let id = next_chunk.fetch_add(1, Ordering::Relaxed);
            self.chunks.push((id, Vec::with_capacity(rlp.len().max(CHUNK_BYTES))));
        }
        let (id, chunk) = self.chunks.last_mut().expect("a chunk with room");
        let offset = chunk.len() as u32;
        chunk.extend_from_slice(rlp);
        NodeRef {
            hash: *hash,
            arena: *id,
            offset,
            len,
        }
    }

    fn resolve<'a>(&'a self, node: &'a NodeRef) -> TrieNodeRlp<'a> {
        if node.is_hash_only_placeholder() {
            return TrieNodeRlp {
                hash: &node.hash,
                rlp: &[],
            };
        }
        let index = self
            .chunks
            .binary_search_by_key(&node.arena, |(id, _)| *id)
            .expect("a node of this shard");
        let start = node.offset as usize;
        TrieNodeRlp {
            hash: &node.hash,
            rlp: &self.chunks[index].1[start..start + node.len as usize],
        }
    }

    /// Moves the chunks to their ids in `arenas`, the last one shrunk to its bytes.
    fn move_into(&mut self, arenas: &mut [Option<NodeArena>]) {
        if let Some((_, last)) = self.chunks.last_mut() {
            last.shrink_to_fit();
        }
        for (id, chunk) in self.chunks.drain(..) {
            arenas[id as usize] = Some(Arc::new(chunk));
        }
    }
}

fn record_rlp<'a>(record: &NodeRecord, blob: &'a [u8]) -> &'a [u8] {
    record
        .rlp_offset
        .checked_add(record.rlp_len)
        .and_then(|end| blob.get(record.rlp_offset..end))
        .unwrap_or_else(|| panic!("node record {record:?} outside a blob of {} bytes", blob.len()))
}

/// Sizes a map on its first insert.
fn reserve_first<K: Eq + std::hash::Hash, V>(map: &mut HashMap<K, V>, capacity: usize) {
    if map.capacity() == 0 {
        map.reserve(capacity);
    }
}

/// The slot and storage-node shards: grouped per address, so a clear drops one group and a freeze flattens them.
#[cfg(not(feature = "sharded-freeze"))]
mod layout {
    use std::mem;

    use hashbrown::HashMap;

    use super::reserve_first;
    use crate::snapshot::content::{self, NodeRef};
    use crate::snapshot::keys::{StorageKey, StorageNodeKey};
    use crate::types::{Address, Hash256, TreePath, U256};

    #[derive(Default)]
    pub(super) struct SlotShard(HashMap<Address, HashMap<U256, Option<U256>>>);

    impl SlotShard {
        pub(super) fn get(&self, address: &Address, slot: &U256) -> Option<Option<U256>> {
            self.0.get(address)?.get(slot).copied()
        }

        pub(super) fn set(&mut self, address: &Address, slot: &U256, value: Option<U256>, groups_hint: usize) {
            reserve_first(&mut self.0, groups_hint);
            self.0.entry(*address).or_default().insert(*slot, value);
        }

        pub(super) fn remove_address(&mut self, address: &Address) {
            self.0.remove(address);
        }

        pub(super) fn groups(&self) -> usize {
            self.0.len()
        }
    }

    pub(super) fn freeze_slots(shards: [&mut SlotShard; super::SHARDS]) -> content::StorageMap {
        let total = shards.iter().flat_map(|shard| shard.0.values()).map(HashMap::len).sum();
        let mut storages = HashMap::with_capacity(total);
        for shard in shards {
            for (address, slots) in mem::take(&mut shard.0) {
                storages.extend(
                    slots
                        .into_iter()
                        .map(|(slot, value)| (StorageKey::new(address, slot), value)),
                );
            }
        }
        storages
    }

    #[derive(Default)]
    pub(super) struct StorageNodeMap(HashMap<Hash256, HashMap<TreePath, NodeRef>>);

    impl StorageNodeMap {
        pub(super) fn get(&self, address_hash: &Hash256, path: &TreePath) -> Option<&NodeRef> {
            self.0.get(address_hash)?.get(path)
        }

        pub(super) fn insert_all(
            &mut self,
            address_hash: &Hash256,
            nodes: impl IntoIterator<Item = (TreePath, NodeRef)>,
            groups_hint: usize,
        ) {
            reserve_first(&mut self.0, groups_hint);
            self.0.entry(*address_hash).or_default().extend(nodes);
        }

        pub(super) fn remove_address(&mut self, address_hash: &Hash256) {
            self.0.remove(address_hash);
        }

        pub(super) fn groups(&self) -> usize {
            self.0.len()
        }
    }

    pub(super) fn freeze_storage_nodes(shards: [&mut StorageNodeMap; super::SHARDS]) -> content::StorageNodeMap {
        let total = shards.iter().flat_map(|shard| shard.0.values()).map(HashMap::len).sum();
        let mut storage_nodes = HashMap::with_capacity(total);
        for shard in shards {
            for (address_hash, nodes) in mem::take(&mut shard.0) {
                storage_nodes.extend(
                    nodes
                        .into_iter()
                        .map(|(path, node)| (StorageNodeKey::new(address_hash, path), node)),
                );
            }
        }
        storage_nodes
    }
}

/// The slot and storage-node shards of `sharded-freeze`: flat maps a freeze moves as they are, so a clear scans its
/// shard as `ClearStorage` scans every slot.
#[cfg(feature = "sharded-freeze")]
mod layout {
    use std::mem;

    use hashbrown::HashMap;

    use super::reserve_first;
    use crate::snapshot::content::{self, NodeRef, ShardedMap};
    use crate::snapshot::keys::{StorageKey, StorageNodeKey};
    use crate::types::{Address, Hash256, TreePath, U256};

    #[derive(Default)]
    pub(super) struct SlotShard(HashMap<StorageKey, Option<U256>>);

    impl SlotShard {
        pub(super) fn get(&self, address: &Address, slot: &U256) -> Option<Option<U256>> {
            self.0.get(&StorageKey::new(*address, *slot)).copied()
        }

        pub(super) fn set(&mut self, address: &Address, slot: &U256, value: Option<U256>, entries_hint: usize) {
            reserve_first(&mut self.0, entries_hint);
            self.0.insert(StorageKey::new(*address, *slot), value);
        }

        pub(super) fn remove_address(&mut self, address: &Address) {
            self.0.retain(|key, _| key.address != *address);
        }

        /// The hint of this layout counts entries, not addresses.
        pub(super) fn groups(&self) -> usize {
            self.0.len()
        }
    }

    pub(super) fn freeze_slots(shards: [&mut SlotShard; super::SHARDS]) -> content::StorageMap {
        ShardedMap::from_shards(shards.map(|shard| mem::take(&mut shard.0)))
    }

    #[derive(Default)]
    pub(super) struct StorageNodeMap(HashMap<StorageNodeKey, NodeRef>);

    impl StorageNodeMap {
        pub(super) fn get(&self, address_hash: &Hash256, path: &TreePath) -> Option<&NodeRef> {
            self.0.get(&StorageNodeKey::new(*address_hash, *path))
        }

        pub(super) fn insert_all(
            &mut self,
            address_hash: &Hash256,
            nodes: impl IntoIterator<Item = (TreePath, NodeRef)>,
            entries_hint: usize,
        ) {
            reserve_first(&mut self.0, entries_hint);
            self.0.extend(
                nodes
                    .into_iter()
                    .map(|(path, node)| (StorageNodeKey::new(*address_hash, path), node)),
            );
        }

        pub(super) fn remove_address(&mut self, address_hash: &Hash256) {
            self.0.retain(|key, _| key.address_hash != *address_hash);
        }

        pub(super) fn groups(&self) -> usize {
            self.0.len()
        }
    }

    pub(super) fn freeze_storage_nodes(shards: [&mut StorageNodeMap; super::SHARDS]) -> content::StorageNodeMap {
        ShardedMap::from_shards(shards.map(|shard| mem::take(&mut shard.0)))
    }
}

/// The lock-rank checker of rules 2 and 3, compiled into debug and test builds only.
mod rank {
    use std::ops::{Deref, DerefMut};

    use parking_lot::{Mutex, MutexGuard};

    #[cfg(any(debug_assertions, test))]
    mod checker {
        use std::cell::Cell;

        thread_local! {
            static HELD: Cell<u32> = const { Cell::new(0) };
            static LAST_RANK: Cell<i32> = const { Cell::new(-1) };
            static IN_COMMIT: Cell<bool> = const { Cell::new(false) };
        }

        pub(super) fn acquiring(rank: u8) {
            let held = HELD.get();
            if IN_COMMIT.get() {
                assert!(
                    i32::from(rank) > LAST_RANK.get(),
                    "commit lock {rank} taken after {}",
                    LAST_RANK.get()
                );
            } else {
                assert_eq!(held, 0, "lock {rank} taken while holding another lock of the content");
            }
            HELD.set(held + 1);
            LAST_RANK.set(i32::from(rank));
        }

        pub(super) fn released() {
            let held = HELD.get() - 1;
            HELD.set(held);
            if held == 0 {
                LAST_RANK.set(-1);
            }
        }

        pub(super) fn enter_commit() {
            assert_eq!(HELD.get(), 0, "a commit started while holding a lock of the content");
            IN_COMMIT.set(true);
        }

        pub(super) fn leave_commit() {
            IN_COMMIT.set(false);
        }
    }

    /// A guard of one lock of a content.
    pub(super) struct Ranked<'a, T> {
        guard: MutexGuard<'a, T>,
    }

    pub(super) fn lock<T>(mutex: &Mutex<T>, rank: u8) -> Ranked<'_, T> {
        #[cfg(any(debug_assertions, test))]
        checker::acquiring(rank);
        #[cfg(not(any(debug_assertions, test)))]
        let _ = rank;
        Ranked { guard: mutex.lock() }
    }

    /// Locks every shard in index order, the first at `first_rank`.
    pub(super) fn lock_shards<T, const N: usize>(shards: &[Mutex<T>; N], first_rank: u8) -> [Ranked<'_, T>; N] {
        std::array::from_fn(|index| lock(&shards[index], first_rank + index as u8))
    }

    impl<T> Deref for Ranked<'_, T> {
        type Target = T;

        fn deref(&self) -> &T {
            &self.guard
        }
    }

    impl<T> DerefMut for Ranked<'_, T> {
        fn deref_mut(&mut self) -> &mut T {
            &mut self.guard
        }
    }

    #[cfg(any(debug_assertions, test))]
    impl<T> Drop for Ranked<'_, T> {
        fn drop(&mut self) {
            checker::released();
        }
    }

    /// Marks the thread as committing while alive, which allows holding several locks in rank order.
    pub(super) struct CommitScope;

    impl CommitScope {
        pub(super) fn enter() -> Self {
            #[cfg(any(debug_assertions, test))]
            checker::enter_commit();
            CommitScope
        }
    }

    #[cfg(any(debug_assertions, test))]
    impl Drop for CommitScope {
        fn drop(&mut self) {
            checker::leave_commit();
        }
    }
}

#[cfg(test)]
mod tests {
    use std::sync::Arc;

    use super::{MutableContent, Usage};
    use crate::stats::FlatStats;
    use crate::types::U256;

    /// Rules 2 and 3 of the module: a second lock outside a commit, or a commit started under a lock, is a bug.
    #[rstest::rstest]
    #[should_panic(expected = "while holding")]
    fn the_rank_checker_rejects_nested_locks(#[values(false, true)] commit: bool) {
        let content = MutableContent::new(Arc::new(FlatStats::default()), Usage::MainBlockProcessing);
        let _account_shard = content.account_shard(&[0; 20]);
        if commit {
            let _ = content.lock_all();
        } else {
            content.set_slot(&[1; 20], &U256::ZERO, &U256::from_u64(1));
        }
    }
}
