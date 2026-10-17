// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

//! Decoded nodes the trie warmer loaded from the store, which bulk sets read in place.

use std::collections::HashMap;
use std::sync::Mutex;

use rustc_hash::FxBuildHasher;

use crate::bulk_set::{matched_length, EMPTY_TREE_HASH};
use crate::log::{LogPointer, TrieNodeLog};
use crate::node::{
    decode_rlp, nibble, record_length, write_record, ChildRef, LoggedNode, NodeKind, RecordPtr,
    TreePath,
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
}

/// The record of every warmed node by the account of its storage trie, zero for the state trie, and its path.
type Index = HashMap<([u8; 32], TreePath), RecordPtr, FxBuildHasher>;

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
        }
    }
}

impl WarmedNodes {
    /// The record of the node at `path` in the trie of `address`, the state trie when `None`, if it is the one with
    /// hash `hash`.
    pub fn get(
        &self,
        address: Option<&[u8; 32]>,
        path: &TreePath,
        hash: &[u8; 32],
    ) -> Option<RecordPtr> {
        let key = (address.copied().unwrap_or_default(), *path);
        let record = *self.shard(&key).lock().unwrap().get(&key)?;
        (record.node().hash() == hash).then_some(record)
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
        for shard in self.index.iter() {
            shard.lock().unwrap().clear();
        }
        let mut arena = self.arena.lock().unwrap();
        arena.chunks.truncate(1);
        arena.used = 0;
    }

    fn shard(&self, key: &([u8; 32], TreePath)) -> &Mutex<Index> {
        // Mixing in the account keeps the roots of storage tries off a single shard.
        &self.index[(key.1.path[0] ^ key.0[0]) as usize % INDEX_SHARDS]
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
) -> Result<(), Error> {
    if *root == EMPTY_TREE_HASH {
        return Ok(());
    }

    let session = log.pin();
    let mut path = TreePath::default();
    let mut hash = *root;
    let mut pointer = session.find_root(address, root);
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
        let Some((depth, child_hash, child_pointer)) = descend(record.node(), key, path.depth())
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
            warm_up(&store, &log, &warmed, address, root, &key).unwrap();
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
