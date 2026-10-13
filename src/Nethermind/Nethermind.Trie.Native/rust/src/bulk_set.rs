// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

use std::cell::RefCell;
use std::cmp::Reverse;

use rayon::prelude::*;

use crate::keccak::{keccak, keccak_many};
use crate::node::{
    decode_rlp, decode_stored, encode, nibble, stored_rlp, ChildRef, Nibbles, Node, TreePath,
};
use crate::{Entry, Error, NodeStore};

/// Same as `PatriciaTree.MinEntriesToParallelizeThreshold`.
const PARALLEL_THRESHOLD: usize = 128;

const NO_CHILD: u32 = u32::MAX;

pub const EMPTY_TREE_HASH: [u8; 32] = [
    0x56, 0xe8, 0x1f, 0x17, 0x1b, 0xcc, 0x55, 0xa6, 0xff, 0x83, 0x45, 0xe6, 0x92, 0xc0, 0xf8, 0x6e,
    0x5b, 0x48, 0xe0, 0x1b, 0x99, 0x6c, 0xad, 0xc0, 0x01, 0x62, 0x2f, 0xb5, 0xe3, 0x63, 0xb4, 0x21,
];

thread_local! {
    static BUFFER: RefCell<Vec<u8>> = RefCell::new(Vec::with_capacity(1024));
}

/// Applies `entries` to the trie whose root is stored at the empty path, writing every new node referenced by hash
/// at its path, and returns the new root hash. `entries` is sorted in place and its keys must be unique.
///
/// Nodes that become unreachable are not removed, except the root when the trie becomes empty.
pub fn bulk_set<S: NodeStore>(
    store: &S,
    entries: &mut [Entry],
    values: &[u8],
) -> Result<[u8; 32], Error> {
    let invalid_value = |entry: &Entry| {
        entry.offset < 0
            || entry.length < 0
            || entry.offset as usize + entry.length as usize > values.len()
    };
    if entries.iter().any(invalid_value) {
        return Err(Error::InvalidArgument);
    }

    entries.par_sort_unstable_by(|left, right| left.key.cmp(&right.key));
    if entries.windows(2).any(|pair| pair[0].key == pair[1].key) {
        return Err(Error::DuplicateKey);
    }

    let root_path = TreePath::default();
    let mut stored_root = Vec::new();
    let root = if store.fetch(&root_path, &mut stored_root)? {
        Some(decode_stored(&stored_root)?)
    } else {
        None
    };

    let context = Context { store, values };
    match context.update(entries, &root_path, root)? {
        (_, false) if stored_root.is_empty() => Ok(EMPTY_TREE_HASH),
        (_, false) => Ok(keccak(stored_rlp(&stored_root)?)),
        (None, true) => {
            store.write(&root_path, &[]);
            Ok(EMPTY_TREE_HASH)
        }
        (Some(root), true) => match context.commit_tree(root, root_path, true).0 {
            ChildRef::Hash(hash) => Ok(hash),
            _ => unreachable!("root is always hashed"),
        },
    }
}

struct Context<'a, S> {
    store: &'a S,
    values: &'a [u8],
}

struct ChildUpdate {
    nibble: usize,
    reference: ChildRef,
    /// The child with no pending descendants, kept when `reference` is not pending.
    node: Option<Node>,
    changed: bool,
}

/// A node being committed; `children[slot]` is the index of the pending node that becomes its child `slot`.
struct PendingNode {
    path: TreePath,
    node: Node,
    children: [u32; 16],
}

impl<S: NodeStore> Context<'_, S> {
    /// Applies sorted `entries`, whose keys all start with `path`, to the `node` at `path`.
    /// Returns the new node, whose new descendants are pending, and whether it differs from `node`.
    fn update(
        &self,
        entries: &[Entry],
        path: &TreePath,
        node: Option<Node>,
    ) -> Result<(Option<Node>, bool), Error> {
        if entries.is_empty() {
            return Ok((node, false));
        }

        let depth = path.depth();
        let first = &entries[0].key;
        let shared = common_prefix_length(first, &entries[entries.len() - 1].key, depth);

        match node {
            None => {
                let result = if entries.len() == 1 {
                    let value = self.value(&entries[0]);
                    (!value.is_empty()).then(|| Node::Leaf {
                        key: Nibbles::from_key(first, depth, 64 - depth),
                        value: value.to_vec(),
                    })
                } else if shared > 0 {
                    let prefix = Nibbles::from_key(first, depth, shared);
                    self.update_below_prefix(entries, path, prefix.as_slice(), None)?
                } else {
                    self.update_branch(entries, path, Node::empty_branch(), true)?
                        .0
                };
                let changed = result.is_some();
                Ok((result, changed))
            }
            Some(Node::Leaf { key, value }) => {
                let matched = matched_length(key.as_slice(), first, depth).min(shared);
                if entries.len() == 1 {
                    let new_value = self.value(&entries[0]);
                    if matched == key.len() {
                        return Ok(match new_value {
                            [] => (None, true),
                            _ if new_value == value.as_slice() => {
                                (Some(Node::Leaf { key, value }), false)
                            }
                            _ => (
                                Some(Node::Leaf {
                                    key,
                                    value: new_value.to_vec(),
                                }),
                                true,
                            ),
                        });
                    }
                    if new_value.is_empty() {
                        return Ok((Some(Node::Leaf { key, value }), false));
                    }
                }

                let key = key.as_slice();
                if matched > 0 {
                    let rest = Node::Leaf {
                        key: Nibbles::from_slice(&key[matched..]),
                        value,
                    };
                    return Ok((
                        self.update_below_prefix(entries, path, &key[..matched], Some(rest))?,
                        true,
                    ));
                }
                let rest = Node::Leaf {
                    key: Nibbles::from_slice(&key[1..]),
                    value,
                };
                self.update_branch(
                    entries,
                    path,
                    Self::split(key[0], ChildRef::Pending(Box::new(rest))),
                    true,
                )
            }
            Some(Node::Extension { key, child }) => {
                let matched = matched_length(key.as_slice(), first, depth).min(shared);
                if matched == key.len() {
                    let child_path = path.append(key.as_slice());
                    let child_node = self.load(&child_path, child.clone())?;
                    return Ok(match self.update(entries, &child_path, child_node)? {
                        (_, false) => (Some(Node::Extension { key, child }), false),
                        (result, true) => (result.map(|node| prepend(key.as_slice(), node)), true),
                    });
                }

                let key = key.as_slice();
                if matched > 0 {
                    let rest = Node::Extension {
                        key: Nibbles::from_slice(&key[matched..]),
                        child,
                    };
                    return Ok((
                        self.update_below_prefix(entries, path, &key[..matched], Some(rest))?,
                        true,
                    ));
                }
                let rest = match key.len() {
                    1 => child,
                    _ => ChildRef::Pending(Box::new(Node::Extension {
                        key: Nibbles::from_slice(&key[1..]),
                        child,
                    })),
                };
                self.update_branch(entries, path, Self::split(key[0], rest), true)
            }
            Some(branch) => self.update_branch(entries, path, branch, false),
        }
    }

    /// Updates the node at `path + prefix` and puts `prefix` in front of the result.
    fn update_below_prefix(
        &self,
        entries: &[Entry],
        path: &TreePath,
        prefix: &[u8],
        node: Option<Node>,
    ) -> Result<Option<Node>, Error> {
        let child_path = path.append(prefix);
        let (result, _) = self.update(entries, &child_path, node)?;
        Ok(result.map(|node| prepend(prefix, node)))
    }

    fn update_branch(
        &self,
        entries: &[Entry],
        path: &TreePath,
        branch: Node,
        is_new: bool,
    ) -> Result<(Option<Node>, bool), Error> {
        let Node::Branch { mut children } = branch else {
            unreachable!("not a branch")
        };
        let depth = path.depth();

        let mut buckets = Vec::with_capacity(16);
        let mut start = 0;
        while start < entries.len() {
            let child_nibble = nibble(&entries[start].key, depth);
            let end = start
                + entries[start..]
                    .partition_point(|entry| nibble(&entry.key, depth) == child_nibble);
            let existing = std::mem::take(&mut children[child_nibble as usize]);
            buckets.push((child_nibble, &entries[start..end], existing));
            start = end;
        }

        let parallel = entries.len() >= PARALLEL_THRESHOLD;
        let update_child = |(child_nibble, bucket, existing): (u8, &[Entry], ChildRef)| -> Result<ChildUpdate, Error> {
            let child_path = path.append(&[child_nibble]);
            let unchanged_reference = match &existing {
                ChildRef::Pending(_) => None,
                reference => Some(reference.clone()),
            };
            let node = self.load(&child_path, existing)?;
            let (node, changed) = self.update(bucket, &child_path, node)?;
            let (reference, node) = match (node, unchanged_reference) {
                (None, _) => (ChildRef::Empty, None),
                (Some(node), Some(reference)) if !changed => (reference, Some(node)),
                // Every parallel task hashes its own subtree, so no level spans threads.
                (Some(node), _) if parallel => {
                    let (reference, node) = self.commit_tree(node, child_path, false);
                    (reference, Some(node))
                }
                (Some(node), _) => (ChildRef::Pending(Box::new(node)), None),
            };
            Ok(ChildUpdate { nibble: child_nibble as usize, reference, node, changed })
        };
        let updates: Vec<ChildUpdate> = if parallel {
            buckets
                .into_par_iter()
                .map(update_child)
                .collect::<Result<_, Error>>()?
        } else {
            buckets
                .into_iter()
                .map(update_child)
                .collect::<Result<_, Error>>()?
        };

        let mut changed = is_new;
        let mut updated_nodes: [Option<Node>; 16] = Default::default();
        for update in updates {
            children[update.nibble] = update.reference;
            updated_nodes[update.nibble] = update.node;
            changed |= update.changed;
        }
        if !changed {
            return Ok((Some(Node::Branch { children }), false));
        }

        let mut remaining = children
            .iter()
            .enumerate()
            .filter(|(_, child)| !matches!(child, ChildRef::Empty));
        let only_child = match (remaining.next(), remaining.next()) {
            (None, _) => return Ok((None, true)),
            (Some((index, _)), None) => Some(index),
            _ => None,
        };

        if let Some(index) = only_child {
            let child_path = path.append(&[index as u8]);
            let (node, committed) = match (
                std::mem::take(&mut children[index]),
                updated_nodes[index].take(),
            ) {
                (ChildRef::Pending(node), _) => (*node, None),
                (reference, Some(node)) => (node, Some(reference)),
                (reference, None) => (
                    self.load(&child_path, reference.clone())?
                        .ok_or(Error::MissingNode)?,
                    Some(reference),
                ),
            };
            let merged = match (node, committed) {
                (Node::Branch { .. }, Some(reference)) => Node::Extension {
                    key: Nibbles::from_slice(&[index as u8]),
                    child: reference,
                },
                (node, _) => prepend(&[index as u8], node),
            };
            return Ok((Some(merged), true));
        }

        Ok((Some(Node::Branch { children }), true))
    }

    /// A new branch holding what remains of a split leaf or extension under `child_nibble`.
    fn split(child_nibble: u8, rest: ChildRef) -> Node {
        let mut branch = Node::empty_branch();
        branch.set_child(child_nibble as usize, rest);
        branch
    }

    fn load(&self, path: &TreePath, child: ChildRef) -> Result<Option<Node>, Error> {
        match child {
            ChildRef::Empty => Ok(None),
            ChildRef::Pending(node) => Ok(Some(*node)),
            ChildRef::Inline(rlp) => decode_rlp(rlp.as_slice()).map(Some),
            ChildRef::Hash(_) => BUFFER.with_borrow_mut(|buffer| {
                if !self.store.fetch(path, buffer)? {
                    return Err(Error::MissingNode);
                }
                decode_stored(buffer).map(Some)
            }),
        }
    }

    /// Encodes and hashes `node` and every pending node below it, storing each one referenced by hash, that is,
    /// whose RLP is at least 32 bytes or which is the root. Returns the reference to `node` and `node` itself with
    /// its children resolved.
    ///
    /// Nodes are processed one path length at a time, deepest first, like `DirtyNodeHasher`: two nodes of one
    /// length are never each other's ancestor and by then all their children are resolved, so a whole level is
    /// hashed together by the batch Keccak kernel.
    fn commit_tree(&self, node: Node, path: TreePath, is_root: bool) -> (ChildRef, Node) {
        let mut nodes = Vec::new();
        collect_pending(node, path, &mut nodes);
        let top = nodes.len() - 1;
        let mut order: Vec<usize> = (0..nodes.len()).collect();
        order.sort_by_key(|&index| Reverse(nodes[index].path.length));
        let mut references: Vec<ChildRef> = (0..nodes.len()).map(|_| ChildRef::Empty).collect();

        BUFFER.with_borrow_mut(|buffer| {
            // (index, stored start, RLP start, end) of every node in the level referenced by hash.
            let mut hashed: Vec<(usize, usize, usize, usize)> = Vec::new();
            let mut start = 0;
            while start < order.len() {
                let length = nodes[order[start]].path.length;
                let end = start
                    + order[start..]
                        .iter()
                        .take_while(|&&index| nodes[index].path.length == length)
                        .count();

                buffer.clear();
                hashed.clear();
                for &index in &order[start..end] {
                    let pending = &mut nodes[index];
                    for (slot, &child) in pending.children.iter().enumerate() {
                        if child != NO_CHILD {
                            pending
                                .node
                                .set_child(slot, std::mem::take(&mut references[child as usize]));
                        }
                    }
                    let stored_start = buffer.len();
                    let rlp_start = encode(&pending.node, buffer);
                    if buffer.len() - rlp_start < 32 && !(is_root && index == top) {
                        references[index] = ChildRef::inline(&buffer[rlp_start..]);
                        buffer.truncate(stored_start);
                    } else {
                        hashed.push((index, stored_start, rlp_start, buffer.len()));
                    }
                }

                let messages: Vec<&[u8]> = hashed
                    .iter()
                    .map(|&(_, _, rlp_start, end)| &buffer[rlp_start..end])
                    .collect();
                let mut hashes = vec![[0u8; 32]; messages.len()];
                keccak_many(&messages, &mut hashes);
                for (&(index, stored_start, _, end), hash) in hashed.iter().zip(hashes) {
                    self.store
                        .write(&nodes[index].path, &buffer[stored_start..end]);
                    references[index] = ChildRef::Hash(hash);
                }
                start = end;
            }
        });

        let reference = std::mem::take(&mut references[top]);
        (reference, nodes.pop().unwrap().node)
    }

    fn value(&self, entry: &Entry) -> &[u8] {
        &self.values[entry.offset as usize..entry.offset as usize + entry.length as usize]
    }
}

/// Moves `node` and its pending descendants into `nodes`, children before their parent.
fn collect_pending(mut node: Node, path: TreePath, nodes: &mut Vec<PendingNode>) {
    let mut children = [NO_CHILD; 16];
    match &mut node {
        Node::Branch {
            children: references,
        } => {
            for (slot, reference) in references.iter_mut().enumerate() {
                if matches!(reference, ChildRef::Pending(_)) {
                    let ChildRef::Pending(child) = std::mem::take(reference) else {
                        unreachable!()
                    };
                    collect_pending(*child, path.append(&[slot as u8]), nodes);
                    children[slot] = (nodes.len() - 1) as u32;
                }
            }
        }
        Node::Extension { key, child } if matches!(child, ChildRef::Pending(_)) => {
            let ChildRef::Pending(pending) = std::mem::take(child) else {
                unreachable!()
            };
            collect_pending(*pending, path.append(key.as_slice()), nodes);
            children[0] = (nodes.len() - 1) as u32;
        }
        _ => {}
    }
    nodes.push(PendingNode {
        path,
        node,
        children,
    });
}

/// Puts `prefix` in front of `node`, merging into it unless it is a branch.
fn prepend(prefix: &[u8], node: Node) -> Node {
    match node {
        Node::Leaf { key, value } => Node::Leaf {
            key: Nibbles::concat(prefix, key.as_slice()),
            value,
        },
        Node::Extension { key, child } => Node::Extension {
            key: Nibbles::concat(prefix, key.as_slice()),
            child,
        },
        branch => Node::Extension {
            key: Nibbles::from_slice(prefix),
            child: ChildRef::Pending(Box::new(branch)),
        },
    }
}

fn common_prefix_length(left: &[u8; 32], right: &[u8; 32], from: usize) -> usize {
    (from..64)
        .take_while(|&index| nibble(left, index) == nibble(right, index))
        .count()
}

fn matched_length(nibbles: &[u8], key: &[u8; 32], from: usize) -> usize {
    nibbles
        .iter()
        .enumerate()
        .take_while(|&(index, &value)| nibble(key, from + index) == value)
        .count()
}

#[cfg(test)]
mod tests {
    use std::collections::HashMap;
    use std::sync::Mutex;

    use super::*;

    #[derive(Default)]
    struct MapStore(Mutex<HashMap<TreePath, Vec<u8>>>);

    impl NodeStore for MapStore {
        fn fetch(&self, path: &TreePath, buffer: &mut Vec<u8>) -> Result<bool, Error> {
            buffer.clear();
            let nodes = self.0.lock().unwrap();
            let node = nodes.get(path);
            buffer.extend_from_slice(node.map_or(&[], Vec::as_slice));
            Ok(node.is_some())
        }

        fn write(&self, path: &TreePath, data: &[u8]) {
            let mut nodes = self.0.lock().unwrap();
            match data {
                [] => nodes.remove(path),
                _ => nodes.insert(*path, data.to_vec()),
            };
        }
    }

    type Item = ([u8; 32], Vec<u8>);

    fn apply(store: &MapStore, items: &[Item]) -> Result<[u8; 32], Error> {
        let mut values = Vec::new();
        let mut entries: Vec<Entry> = items
            .iter()
            .map(|(key, value)| {
                let entry = Entry {
                    key: *key,
                    offset: values.len() as i32,
                    length: value.len() as i32,
                };
                values.extend_from_slice(value);
                entry
            })
            .collect();
        bulk_set(store, &mut entries, &values)
    }

    /// Random keys plus keys sharing 62 nibbles, so leaves with 1-byte values are inlined into their branch.
    fn generate_items(count: usize, seed: u64) -> Vec<Item> {
        let mut state = seed;
        let mut next = move || {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            state
        };
        let mut items: Vec<Item> = (0..count)
            .map(|index| {
                let mut key = [0u8; 32];
                key.iter_mut().for_each(|byte| *byte = next() as u8);
                let value = (0..[1, 2, 32, 60][index % 4])
                    .map(|_| next() as u8)
                    .collect();
                (key, value)
            })
            .collect();
        for last in 0..40u8 {
            let mut key = [0xab; 32];
            key[31] = last;
            items.push((key, vec![last]));
        }
        items
    }

    fn root_of(items: &[Item]) -> [u8; 32] {
        apply(&MapStore::default(), items).unwrap()
    }

    #[test]
    fn bulk_set_is_independent_of_batching_and_deletes_back_to_empty() {
        let items = generate_items(5000, 0x1234_5678);

        let store = MapStore::default();
        assert_eq!(apply(&store, &[]), Ok(EMPTY_TREE_HASH));
        let root = apply(&store, &items).unwrap();
        let stored_root = store.0.lock().unwrap()[&TreePath::default()].clone();
        assert_eq!(keccak(stored_rlp(&stored_root).unwrap()), root);
        assert_eq!(apply(&store, &[]), Ok(root));

        let batched = MapStore::default();
        let mut batched_root = EMPTY_TREE_HASH;
        let mut start = 0;
        for size in [1, 7, 300, items.len() - 308] {
            batched_root = apply(&batched, &items[start..start + size]).unwrap();
            start += size;
        }
        assert_eq!(batched_root, root);

        let mut changes: Vec<Item> = items[..2500]
            .iter()
            .map(|(key, value)| (*key, [value.as_slice(), &[7]].concat()))
            .collect();
        changes.extend(items[2500..4000].iter().map(|(key, _)| (*key, Vec::new())));
        changes.extend(items[5000..5020].iter().map(|(key, _)| (*key, Vec::new())));
        let mut expected: Vec<Item> = changes[..2500].to_vec();
        expected.extend_from_slice(&items[4000..5000]);
        expected.extend_from_slice(&items[5020..]);
        assert_eq!(apply(&store, &changes), Ok(root_of(&expected)));

        let deletes: Vec<Item> = expected.iter().map(|(key, _)| (*key, Vec::new())).collect();
        assert_eq!(apply(&store, &deletes), Ok(EMPTY_TREE_HASH));
        assert!(!store.0.lock().unwrap().contains_key(&TreePath::default()));

        assert_eq!(
            apply(&store, &[items[0].clone(), items[0].clone()]),
            Err(Error::DuplicateKey)
        );
    }
}
