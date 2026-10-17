// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

//! Decoded nodes the trie warmer loaded from the store, which bulk sets read in place.

use std::collections::HashMap;
use std::sync::{Arc, Mutex, OnceLock};

use rustc_hash::FxBuildHasher;

use crate::bulk_set::{matched_length, EMPTY_TREE_HASH};
use crate::log::{LogPointer, LogSession, TrieNodeLog};
use crate::node::{
    decode_rlp, nibble, record_length, write_record, ChildRef, LoggedNode, NodeKind, RecordPtr,
    Nibbles, Node, TreePath,
};
use crate::{Error, NodeStore};

const CHUNK_BYTES: usize = 1 << 20;
const INDEX_SHARDS: usize = 64;

/// Records, in the log's format with no child pointers, of nodes warmed for one block, indexed by trie and path.
///
/// Records are appended to chunks that never move, so a record stays readable until [`WarmedNodes::reset`] or drop,
/// which the owner only does once nothing reads them.
pub struct WarmedNodes {
    arena: Mutex<Arena>,
    index: Box<[Mutex<Index>]>,
    cache: OnceLock<Arc<SharedCache>>,
}

/// The record of every warmed node by the account of its storage trie, zero for the state trie, and its path.
type CacheKey = ([u8; 32], TreePath);
type Index = HashMap<CacheKey, RecordPtr, FxBuildHasher>;

/// Owned decoded persisted records shared across blocks. Each shard stays within its share of the budget.
pub(crate) struct SharedCache {
    shards: Box<[Mutex<CacheShard>]>,
    shard_budget: usize,
}

#[derive(Default)]
struct CacheShard {
    records: HashMap<CacheKey, Box<[u8]>, FxBuildHasher>,
    bytes: usize,
}

// Account for the key, owned buffer and hash table bookkeeping as well as record bytes.
const CACHE_ENTRY_BYTES: usize = std::mem::size_of::<CacheKey>()
    + std::mem::size_of::<Box<[u8]>>()
    + 2 * std::mem::size_of::<usize>();

impl SharedCache {
    pub(crate) fn clear(&self) {
        for shard in self.shards.iter() {
            let mut shard = shard.lock().unwrap();
            shard.records.clear();
            shard.bytes = 0;
        }
    }

    pub(crate) fn new(budget: u64) -> Self {
        Self {
            shards: (0..INDEX_SHARDS).map(|_| Mutex::default()).collect(),
            shard_budget: (budget / INDEX_SHARDS as u64).min(usize::MAX as u64) as usize,
        }
    }
}

fn shard_index(key: &CacheKey) -> usize {
    // Mixing in the account keeps the roots of storage tries off a single shard.
    (key.1.path[0] ^ key.0[0]) as usize % INDEX_SHARDS
}

#[derive(Default)]
struct Arena {
    chunks: Vec<Box<[u8]>>,
    used: usize,
}

impl Default for WarmedNodes {
    fn default() -> Self {
        WarmedNodes {
            arena: Mutex::default(),
            index: (0..INDEX_SHARDS).map(|_| Mutex::default()).collect(),
            cache: OnceLock::new(),
        }
    }
}

impl WarmedNodes {
    /// Binds the block's warmed records to their log's shared cache on first use.
    pub(crate) fn bind_cache(&self, cache: Arc<SharedCache>) {
        let _ = self.cache.set(cache);
    }

    /// Promotes all records, including cache hits, once no operation uses these warmed nodes.
    pub(crate) fn promote(&self) {
        let Some(cache) = self.cache.get().filter(|cache| cache.shard_budget != 0) else {
            return;
        };
        for (index, warmed) in self.index.iter().enumerate() {
            let warmed = warmed.lock().unwrap();
            if warmed.is_empty() {
                continue;
            }
            let mut shard = cache.shards[index].lock().unwrap();
            let mut required = shard.bytes;
            for (key, record) in warmed.iter() {
                if let Some(previous) = shard.records.get(key) {
                    required -= previous.len() + CACHE_ENTRY_BYTES;
                }
                required += record.node().record().len() + CACHE_ENTRY_BYTES;
            }
            // Replace only once, before replaying this whole batch. Cache hits warmed before the entry that
            // filled the shard are thereby promoted again too, rather than lost when its old arena retires.
            if required > cache.shard_budget {
                shard.records.clear();
                shard.bytes = 0;
            }
            for (key, record) in warmed.iter() {
                let node = record.node();
                if shard.records.get(key).is_some_and(|old| LoggedNode::new(old).hash() == node.hash()) {
                    continue;
                }
                let bytes = node.record();
                let previous = shard.records.get(key).map_or(0, |old| old.len() + CACHE_ENTRY_BYTES);
                let required = shard.bytes - previous + bytes.len() + CACHE_ENTRY_BYTES;
                if required <= cache.shard_budget {
                    shard.records.insert(*key, bytes.into());
                    shard.bytes = required;
                }
            }
        }
    }

    /// The record of the node at `path` in the trie of `address`, the state trie when `None`, if it is the one with
    /// hash `hash`.
    pub fn get(
        &self,
        address: Option<&[u8; 32]>,
        path: &TreePath,
        hash: &[u8; 32],
    ) -> Option<RecordPtr> {
        let key = (address.copied().unwrap_or_default(), *path);
        if let Some(record) = self.shard(&key).lock().unwrap().get(&key).copied() {
            if record.node().hash() == hash {
                return Some(record);
            }
        }
        let cache = self.cache.get().filter(|cache| cache.shard_budget != 0)?;
        let record = {
            let shard = cache.shards[shard_index(&key)].lock().unwrap();
            let bytes = shard.records.get(&key)?;
            if LoggedNode::new(bytes).hash() != hash {
                return None;
            }
            // Copy while the cache shard is locked: eviction can never invalidate the returned block-owned record.
            self.append(bytes.len(), |out| out.copy_from_slice(bytes))
        };
        let mut local = self.shard(&key).lock().unwrap();
        match local.get(&key).copied() {
            Some(existing) if existing.node().hash() == hash => Some(existing),
            _ => {
                local.insert(key, record);
                Some(record)
            }
        }
    }

    /// Adds the node with hash `hash` and RLP `rlp` at `path` in the trie of `address`, the state trie when `None`,
    /// and returns its record, or the one a concurrent warmer added first.
    pub fn insert(
        &self,
        address: Option<&[u8; 32]>,
        path: &TreePath,
        hash: &[u8; 32],
        rlp: &[u8],
    ) -> Result<RecordPtr, Error> {
        let node = decode_rlp(rlp)?;
        let record = self.append(record_length(&node, rlp.len()), |out| {
            write_record(&node, 0, hash, rlp, out)
        });
        let key = (address.copied().unwrap_or_default(), *path);
        let mut shard = self.shard(&key).lock().unwrap();
        match shard.get(&key) {
            Some(&existing) if existing.node().hash() == hash => Ok(existing),
            _ => {
                shard.insert(key, record);
                Ok(record)
            }
        }
    }

    /// Drops every record, keeping the first chunk.
    pub fn reset(&self) {
        self.promote();
        for shard in self.index.iter() {
            shard.lock().unwrap().clear();
        }
        let mut arena = self.arena.lock().unwrap();
        arena.chunks.truncate(1);
        arena.used = 0;
    }

    fn shard(&self, key: &CacheKey) -> &Mutex<Index> {
        &self.index[shard_index(key)]
    }

    fn append(&self, length: usize, write: impl FnOnce(&mut [u8])) -> RecordPtr {
        let mut arena = self.arena.lock().unwrap();
        let Arena { chunks, used } = &mut *arena;
        let chunk = match chunks.last_mut() {
            Some(chunk) if chunk.len() - *used >= length => chunk,
            _ => {
                chunks.push(vec![0; CHUNK_BYTES.max(length)].into_boxed_slice());
                *used = 0;
                chunks.last_mut().unwrap()
            }
        };
        let out = &mut chunk[*used..*used + length];
        write(out);
        *used += length;
        RecordPtr::of(out)
    }
}

/// Warms the path to `key` in the trie of `address`, the state trie when `None`, with root hash `root`: every node on
/// it that bulk sets cannot read from `log` nor `warmed` is fetched from `store` and added to `warmed`. Stops at the
/// first node `store` does not have.
pub fn warm_up<S: NodeStore>(
    store: &S,
    log: &TrieNodeLog,
    warmed: &WarmedNodes,
    address: Option<&[u8; 32]>,
    root: &[u8; 32],
    key: &[u8; 32],
    for_delete: bool,
) -> Result<(), Error> {
    if *root == EMPTY_TREE_HASH {
        return Ok(());
    }

    let session = log.pin();
    let mut path = TreePath::default();
    let mut hash = *root;
    let mut pointer = session.find_root(address, root);
    let mut parent_branch: Option<RecordPtr> = None;
    loop {
        let record = match session.record(pointer) {
            Some(record) => RecordPtr::of(record),
            None => match warmed.get(address, &path, &hash) {
                Some(record) => record,
                None => match store.fetch_rlp(&path, &hash, |rlp| {
                    warmed.insert(address, &path, &hash, rlp)
                })? {
                    Some(record) => record?,
                    None => return Ok(()),
                },
            },
        };
        let node = record.node();
        match node.kind() {
            NodeKind::Leaf if for_delete => {
                if let Some(branch) = parent_branch {
                    warm_delete_sibling(store, &session, warmed, address, branch.node(), path, node.key(), key)?;
                }
            }
            NodeKind::Extension => parent_branch = None,
            NodeKind::Branch if for_delete => {
                parent_branch = Some(record);
                if let ChildRef::Inline(rlp) = node.child(nibble(key, path.depth())) {
                    if let Node::Leaf { key: leaf_key, .. } = decode_rlp(rlp.as_slice())? {
                        let mut leaf_path = path;
                        leaf_path.push(nibble(key, path.depth()));
                        warm_delete_sibling(store, &session, warmed, address, node, leaf_path, leaf_key, key)?;
                    }
                }
            }
            _ => {}
        }
        let Some((depth, child_hash, child_pointer)) = descend(node, key, path.depth())
        else {
            return Ok(());
        };
        for index in path.depth()..depth {
            path.push(nibble(key, index));
        }
        hash = child_hash;
        pointer = child_pointer;
    }
}

/// A deletion can collapse its immediate two-child branch; an intervening extension prevents that collapse.
fn warm_delete_sibling<S: NodeStore>(
    store: &S,
    session: &LogSession<'_>,
    warmed: &WarmedNodes,
    address: Option<&[u8; 32]>,
    branch: LoggedNode<'_>,
    mut leaf_path: TreePath,
    leaf_key: Nibbles,
    key: &[u8; 32],
) -> Result<(), Error> {
    if leaf_key.len() != 64 - leaf_path.depth()
        || matched_length(leaf_key.as_slice(), key, leaf_path.depth()) != leaf_key.len()
    {
        return Ok(());
    }
    let deleted = nibble(key, leaf_path.depth() - 1);
    let mut sibling = None;
    for index in 0..16 {
        if index == deleted {
            continue;
        }
        let child = branch.child(index);
        if matches!(child, ChildRef::Empty) {
            continue;
        }
        if sibling.is_some() {
            return Ok(());
        }
        sibling = Some((index, child));
    }
    if let Some((index, ChildRef::Hash { hash, pointer })) = sibling {
        leaf_path.set_last(index);
        if session.record(pointer).is_none() && warmed.get(address, &leaf_path, &hash).is_none() {
            store.fetch_rlp(&leaf_path, &hash, |rlp| warmed.insert(address, &leaf_path, &hash, rlp))?.transpose()?;
        }
    }
    Ok(())
}

/// The depth, hash and log pointer of the child of `node`, at `depth`, on the path to `key`, or `None` when the path
/// ends at `node`, at a leaf, a diverging extension, or an empty or inline child.
fn descend(
    node: LoggedNode,
    key: &[u8; 32],
    depth: usize,
) -> Option<(usize, [u8; 32], LogPointer)> {
    let (depth, child) = match node.kind() {
        NodeKind::Leaf => return None,
        NodeKind::Extension => {
            let extension = node.key();
            if matched_length(extension.as_slice(), key, depth) < extension.len() {
                return None;
            }
            (depth + extension.len(), node.child(0))
        }
        NodeKind::Branch => (depth + 1, node.child(nibble(key, depth))),
    };
    match child {
        ChildRef::Hash { hash, pointer } => Some((depth, hash, pointer)),
        _ => None,
    }
}

#[cfg(test)]
mod tests {
    use std::sync::atomic::{AtomicUsize, Ordering};

    use super::*;
    use crate::keccak::keccak;
    use crate::node::{empty_children, encode, Nibbles, Node};

    /// Holds nodes by path and hash, counting fetches.
    #[derive(Default)]
    struct MapStore {
        nodes: HashMap<(TreePath, [u8; 32]), Vec<u8>>,
        fetches: AtomicUsize,
    }

    impl NodeStore for MapStore {
        fn fetch_rlp<R>(
            &self,
            path: &TreePath,
            hash: &[u8; 32],
            receive: impl FnOnce(&[u8]) -> R,
        ) -> Result<Option<R>, Error> {
            self.fetches.fetch_add(1, Ordering::Relaxed);
            Ok(self.nodes.get(&(*path, *hash)).map(|rlp| receive(rlp)))
        }
    }

    fn rlp(node: &Node) -> Vec<u8> {
        let mut out = Vec::new();
        encode(node, &mut out);
        out
    }

    fn cached_leaf(value: u8) -> (Vec<u8>, [u8; 32]) {
        let bytes = rlp(&Node::Leaf {
            key: Nibbles::from_slice(&[0; 60]),
            value: vec![value; 40],
        });
        let hash = keccak(&bytes);
        (bytes, hash)
    }

    fn cache_path(last: u8) -> TreePath {
        TreePath::default().append(&[0, 0, 0, last])
    }

    #[test]
    fn shared_cache_reuses_owned_records_after_reset_and_rejects_wrong_hash() {
        let cache = Arc::new(SharedCache::new(64 * 1024));
        let first = WarmedNodes::default();
        let second = WarmedNodes::default();
        first.bind_cache(cache.clone());
        second.bind_cache(cache);
        let (bytes, hash) = cached_leaf(0x42);
        let address = [0x12; 32];
        let path = cache_path(1);
        first.insert(Some(&address), &path, &hash, &bytes).unwrap();
        assert!(second.get(Some(&address), &path, &hash).is_none());
        first.reset();
        let copied = second.get(Some(&address), &path, &hash).unwrap();
        assert_eq!(copied.node().rlp(), bytes);
        assert!(second.get(Some(&address), &path, &[0x43; 32]).is_none());
        assert!(second.get(None, &path, &hash).is_none());
        first.insert(Some(&address), &path, &[0x44; 32], &bytes).unwrap();
        first.reset();
        assert_eq!(copied.node().rlp(), bytes, "another block's arena was returned");
    }

    #[test]
    fn shared_cache_repromotes_earlier_hits_when_a_full_shard_is_replaced() {
        let (bytes, hash) = cached_leaf(1);
        let node = decode_rlp(&bytes).unwrap();
        let entry_bytes = record_length(&node, bytes.len()) + CACHE_ENTRY_BYTES;
        let cache = Arc::new(SharedCache::new((2 * entry_bytes * INDEX_SHARDS) as u64));
        let first = WarmedNodes::default();
        first.bind_cache(cache.clone());
        first.insert(None, &cache_path(1), &hash, &bytes).unwrap();
        first.insert(None, &cache_path(2), &hash, &bytes).unwrap();
        first.promote();

        let second = WarmedNodes::default();
        second.bind_cache(cache.clone());
        let earlier_hit = second.get(None, &cache_path(1), &hash).unwrap();
        second.insert(None, &cache_path(3), &hash, &bytes).unwrap();
        second.promote();
        let third = WarmedNodes::default();
        third.bind_cache(cache.clone());
        assert!(third.get(None, &cache_path(1), &hash).is_some());
        assert!(third.get(None, &cache_path(3), &hash).is_some());
        assert!(third.get(None, &cache_path(2), &hash).is_none());
        assert_eq!(earlier_hit.node().rlp(), bytes, "cache eviction invalidated a block record");
        assert!(cache.shards.iter().all(|shard| shard.lock().unwrap().bytes <= cache.shard_budget));
    }

    #[test]
    fn zero_or_insufficient_cache_budget_keeps_no_records() {
        let (bytes, hash) = cached_leaf(1);
        for budget in [0, INDEX_SHARDS as u64] {
            let cache = Arc::new(SharedCache::new(budget));
            let warmed = WarmedNodes::default();
            warmed.bind_cache(cache.clone());
            warmed.insert(None, &cache_path(1), &hash, &bytes).unwrap();
            warmed.reset();
            assert!(warmed.get(None, &cache_path(1), &hash).is_none());
            assert!(cache.shards.iter().all(|shard| shard.lock().unwrap().records.is_empty()));
        }
    }

    fn apply_items(
        store: &MapStore,
        pending: &crate::PendingNodes,
        log: &TrieNodeLog,
        warmed: Option<&WarmedNodes>,
        root: &[u8; 32],
        items: &[([u8; 32], Vec<u8>)],
    ) -> [u8; 32] {
        let mut values = Vec::new();
        let mut entries: Vec<_> = items.iter().map(|(key, value)| {
            let entry = crate::Entry { key: *key, offset: values.len() as i32, length: value.len() as i32 };
            values.extend_from_slice(value);
            entry
        }).collect();
        crate::bulk_set(store, pending, Some(log), warmed, None, root, &mut entries, &values).unwrap()
    }

    fn persisted_items(items: &[([u8; 32], Vec<u8>)]) -> (MapStore, [u8; 32]) {
        let log = TrieNodeLog::minimal();
        let pending = crate::PendingNodes::default();
        let mut store = MapStore::default();
        let root = apply_items(&store, &pending, &log, None, &EMPTY_TREE_HASH, items);
        for shard in 0..pending.shard_count() {
            pending.with_view(shard, |nodes, rlp| {
                for node in nodes {
                    store.nodes.insert((node.path, node.hash), rlp[node.rlp_offset as usize..][..node.rlp_length as usize].to_vec());
                }
            });
        }
        (store, root)
    }

    #[test]
    fn delete_warming_fetches_only_the_sibling_needed_for_branch_collapse() {
        let mut key_a = [0; 32]; key_a[0] = 0x10; key_a[31] = 1;
        let mut key_b = key_a; key_b[31] = 2;
        let mut key_c = [0; 32]; key_c[0] = 0x20;
        let mut key_d = key_a; key_d[31] = 3;
        for for_delete in [false, true] {
            for three_children in [false, true] {
                for inline in [false, true] {
                    let mut items = vec![(key_a, if inline { vec![1] } else { vec![1; 40] }), (key_b, vec![2; 40]), (key_c, vec![3; 40])];
                    if three_children { items.push((key_d, vec![4; 40])); }
                    let (store, root) = persisted_items(&items);
                    let log = TrieNodeLog::minimal();
                    let warmed = WarmedNodes::default();
                    warm_up(&store, &log, &warmed, None, &root, &key_a, for_delete).unwrap();
                    let before = store.fetches.load(Ordering::Relaxed);
                    assert_eq!(before, 3 + usize::from(!inline) + usize::from(for_delete && !three_children));
                    let updated = apply_items(&store, &crate::PendingNodes::default(), &log, Some(&warmed), &root, &[(key_a, Vec::new())]);
                    assert_eq!(store.fetches.load(Ordering::Relaxed) - before, usize::from(!for_delete && !three_children));
                    items.retain(|(key, _)| *key != key_a);
                    let (_, expected) = persisted_items(&items);
                    assert_eq!(updated, expected);
                }
            }
        }
    }

    #[test]
    fn delete_warming_of_a_missing_key_does_not_load_an_off_path_sibling() {
        let mut key_a = [0; 32]; key_a[0] = 0x10; key_a[31] = 1;
        let mut key_b = [0; 32]; key_b[0] = 0x20; key_b[31] = 2;
        for diverging_leaf in [false, true] {
            let (store, root) = persisted_items(&[(key_a, vec![1; 40]), (key_b, vec![2; 40])]);
            let mut missing = key_a;
            missing[0] = if diverging_leaf { 0x10 } else { 0x30 };
            missing[31] = 4;
            let log = TrieNodeLog::minimal();
            warm_up(&store, &log, &WarmedNodes::default(), None, &root, &missing, false).unwrap();
            let ordinary = store.fetches.load(Ordering::Relaxed);
            warm_up(&store, &log, &WarmedNodes::default(), None, &root, &missing, true).unwrap();
            assert_eq!(store.fetches.load(Ordering::Relaxed), 2 * ordinary);
        }
    }

    #[test]
    fn warm_up_fetches_only_nodes_neither_logged_nor_warmed() {
        // A logged root branch whose child 1 is logged, and whose child 2, an extension over a leaf, is only in the store.
        let log = TrieNodeLog::minimal();
        let session = log.pin();
        let leaf = Node::Leaf {
            key: Nibbles::from_slice(&[0; 61]),
            value: vec![0xaa; 40],
        };
        let logged_leaf = session.append(&leaf);
        let stored_leaf_hash = keccak(&rlp(&leaf));
        let extension = Node::Extension {
            key: Nibbles::from_slice(&[0, 0]),
            child: ChildRef::Hash {
                hash: stored_leaf_hash,
                pointer: 0,
            },
        };
        let mut children = empty_children();
        children[1] = ChildRef::Hash {
            hash: [0x22; 32],
            pointer: logged_leaf,
        };
        children[2] = ChildRef::Hash {
            hash: keccak(&rlp(&extension)),
            pointer: 0,
        };
        let root = [0x44; 32];
        let address = [0x55; 32];
        session.set_root(
            Some(&address),
            &root,
            session.append(&Node::Branch { children }),
        );
        drop(session);

        let path = |nibbles: &[u8]| TreePath::default().append(nibbles);
        let mut store = MapStore::default();
        store
            .nodes
            .insert((path(&[2]), keccak(&rlp(&extension))), rlp(&extension));
        store
            .nodes
            .insert((path(&[2, 0, 0]), stored_leaf_hash), rlp(&leaf));
        let warmed = WarmedNodes::default();
        let key = |first: u8| {
            let mut key = [0; 32];
            key[0] = first;
            key
        };
        let warm = |address: Option<&[u8; 32]>, root: &[u8; 32], key: [u8; 32]| {
            let before = store.fetches.load(Ordering::Relaxed);
            warm_up(&store, &log, &warmed, address, root, &key, false).unwrap();
            store.fetches.load(Ordering::Relaxed) - before
        };

        // The logged part of a path is never fetched, the rest once, and an unknown root ends the walk at once.
        assert_eq!(warm(Some(&address), &root, key(0x10)), 0);
        assert_eq!(warm(Some(&address), &root, key(0x20)), 2);
        assert_eq!(warm(Some(&address), &root, key(0x20)), 0);
        assert_eq!(warm(Some(&address), &[0x66; 32], key(0x10)), 1);
        assert_eq!(warm(None, &root, key(0x10)), 1);
        assert_eq!(warm(Some(&address), &EMPTY_TREE_HASH, key(0x10)), 0);

        // Warmed records hold the node, its hash and RLP, under their trie, path and hash only.
        let warmed_leaf = warmed
            .get(Some(&address), &path(&[2, 0, 0]), &stored_leaf_hash)
            .unwrap()
            .node();
        assert_eq!(warmed_leaf.rlp(), rlp(&leaf));
        assert_eq!(rlp(&warmed_leaf.to_owned(0)), rlp(&leaf));
        assert!(warmed
            .get(None, &path(&[2, 0, 0]), &stored_leaf_hash)
            .is_none());
        assert!(warmed
            .get(Some(&address), &path(&[2, 0, 0]), &[0x77; 32])
            .is_none());
        warmed.reset();
        assert!(warmed
            .get(Some(&address), &path(&[2, 0, 0]), &stored_leaf_hash)
            .is_none());
    }
}
