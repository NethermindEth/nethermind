// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

//! Decoded-node counterpart of Nethermind's `TrieNodeCache`.

use std::hash::{Hash, Hasher};
use std::sync::atomic::{AtomicI64, Ordering};
use std::sync::Mutex;

use rustc_hash::FxHasher;

use crate::node::{ChildRef, Node, TreePath};

/// Same as `TrieNodeCache.EstimatedSizePerNode`.
const ESTIMATED_SIZE_PER_NODE: u64 = 700;
/// Same as `TrieNodeCache.UtilRatio`.
const UTIL_RATIO: f64 = 0.25;
const SHARD_COUNT: usize = 256;

struct CachedNode {
    hash: [u8; 32],
    node: Node,
}

type Slot = Mutex<Option<Box<CachedNode>>>;

/// A cache of decoded nodes laid out like `TrieNodeCache`: shards of slots, the shard picked by the first path byte
/// and the slot by a hash of the address and path. A colliding node replaces the old one, and a lookup only hits when
/// the node hash matches too, so a node of another branch at the same path is never returned. When over the memory
/// budget, whole shards are cleared in turn, which keeps nodes of nearby paths together.
pub struct TrieNodeCache {
    shards: Box<[Box<[Slot]>]>,
    bucket_mask: usize,
    max_memory: i64,
    memory: AtomicI64,
    next_shard_to_clear: Mutex<usize>,
}

impl TrieNodeCache {
    /// Creates a cache of about `max_memory` bytes. A zero `max_memory` caches nothing.
    pub fn new(max_memory: u64) -> TrieNodeCache {
        let total_node_count = max_memory / ESTIMATED_SIZE_PER_NODE;
        let target_bucket_size = (total_node_count as f64 / UTIL_RATIO) as usize / SHARD_COUNT;
        let bucket_size = target_bucket_size.max(16).next_power_of_two();
        TrieNodeCache {
            shards: (0..SHARD_COUNT)
                .map(|_| (0..bucket_size).map(|_| Slot::default()).collect())
                .collect(),
            bucket_mask: bucket_size - 1,
            max_memory: max_memory.min(i64::MAX as u64) as i64,
            memory: AtomicI64::new(0),
            next_shard_to_clear: Mutex::new(0),
        }
    }

    /// Returns a copy of the node at `path` of the trie of `address`, the state trie when `None`, whose hash is `hash`.
    pub fn get(
        &self,
        address: Option<&[u8; 32]>,
        path: &TreePath,
        hash: &[u8; 32],
    ) -> Option<Node> {
        let slot = self.slot(address, path).lock().unwrap();
        slot.as_ref()
            .filter(|cached| cached.hash == *hash)
            .map(|cached| cached.node.clone())
    }

    /// Caches a copy of `node`, whose children must all be resolved, as the node at `path` with hash `hash`.
    pub fn insert(
        &self,
        address: Option<&[u8; 32]>,
        path: &TreePath,
        hash: &[u8; 32],
        node: &Node,
    ) {
        if self.max_memory == 0 {
            return;
        }

        let replaced = {
            let mut slot = self.slot(address, path).lock().unwrap();
            if slot.as_ref().is_some_and(|cached| cached.hash == *hash) {
                return;
            }
            let replaced = slot.replace(Box::new(CachedNode {
                hash: *hash,
                node: node.clone(),
            }));
            // Under the slot lock, so it never races a shard clear taking the same node out.
            self.memory.fetch_add(
                memory_size(node)
                    - replaced
                        .as_ref()
                        .map_or(0, |cached| memory_size(&cached.node)),
                Ordering::Relaxed,
            );
            replaced
        };
        drop(replaced);

        if self.memory.load(Ordering::Relaxed) > self.max_memory {
            self.evict();
        }
    }

    /// Clears shards in turn until the cache is within its memory budget. Left to the thread already evicting when
    /// there is one.
    fn evict(&self) {
        let Ok(mut next_shard_to_clear) = self.next_shard_to_clear.try_lock() else {
            return;
        };
        for _ in 0..SHARD_COUNT {
            if self.memory.load(Ordering::Relaxed) <= self.max_memory {
                break;
            }
            for slot in self.shards[*next_shard_to_clear].iter() {
                let removed = {
                    let mut slot = slot.lock().unwrap();
                    let removed = slot.take();
                    if let Some(cached) = &removed {
                        self.memory
                            .fetch_sub(memory_size(&cached.node), Ordering::Relaxed);
                    }
                    removed
                };
                drop(removed);
            }
            *next_shard_to_clear = (*next_shard_to_clear + 1) % SHARD_COUNT;
        }
    }

    fn slot(&self, address: Option<&[u8; 32]>, path: &TreePath) -> &Slot {
        let mut shard = path.path[0] as usize;
        let mut hasher = FxHasher::default();
        path.hash(&mut hasher);
        if let Some(address) = address {
            // So the roots of every storage trie do not all sit in one shard.
            shard = (shard + address[0] as usize) % SHARD_COUNT;
            address.hash(&mut hasher);
        }
        &self.shards[shard][hasher.finish() as usize & self.bucket_mask]
    }

    #[cfg(test)]
    fn memory(&self) -> i64 {
        self.memory.load(Ordering::Relaxed)
    }
}

fn memory_size(node: &Node) -> i64 {
    let heap = match node {
        Node::Leaf { value, .. } => value.capacity(),
        Node::Extension { .. } => 0,
        Node::Branch { .. } => std::mem::size_of::<[ChildRef; 16]>(),
    };
    (std::mem::size_of::<CachedNode>() + heap) as i64
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::node::decode_rlp;

    fn leaf(value: u8) -> Node {
        decode_rlp(&[0xc4, 0x82, 0x20, 0x01, value]).unwrap()
    }

    fn value_of(node: Option<Node>) -> Option<u8> {
        node.map(|node| match node {
            Node::Leaf { value, .. } => value[0],
            _ => panic!("not a leaf"),
        })
    }

    #[test]
    fn hits_only_the_same_trie_path_and_hash_and_stays_within_budget() {
        let cache = TrieNodeCache::new(1 << 20);
        let address = [0x11; 32];
        let mut path = TreePath::default();
        path.push_all(&[1, 2, 3]);
        let (hash, other_hash) = ([0xaa; 32], [0xbb; 32]);

        cache.insert(None, &path, &hash, &leaf(1));
        cache.insert(Some(&address), &path, &hash, &leaf(2));
        assert_eq!(value_of(cache.get(None, &path, &hash)), Some(1));
        assert_eq!(value_of(cache.get(Some(&address), &path, &hash)), Some(2));
        assert_eq!(value_of(cache.get(None, &path, &other_hash)), None);
        assert_eq!(value_of(cache.get(None, &TreePath::default(), &hash)), None);

        // A node of another branch at the same path replaces the old one.
        cache.insert(None, &path, &other_hash, &leaf(3));
        assert_eq!(value_of(cache.get(None, &path, &hash)), None);
        assert_eq!(value_of(cache.get(None, &path, &other_hash)), Some(3));
        assert_eq!(cache.memory(), 2 * memory_size(&leaf(0)));

        for index in 0..100_000u32 {
            let mut path = TreePath::default();
            path.path[..4].copy_from_slice(&index.to_be_bytes());
            path.length = 8;
            cache.insert(None, &path, &hash, &leaf(4));
        }
        assert!(cache.memory() <= 1 << 20);

        let disabled = TrieNodeCache::new(0);
        disabled.insert(None, &path, &hash, &leaf(5));
        assert_eq!(value_of(disabled.get(None, &path, &hash)), None);
    }
}
