// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

//! Port of `PatriciaTree.BulkSet`: entries are bucketed by nibble one level at a time, and new nodes stay pending
//! until `commit_tree` hashes and stores them.

use std::cell::RefCell;

use rayon::prelude::*;

use crate::cache::TrieNodeCache;
use crate::keccak::keccak_many;
use crate::node::{decode_rlp, empty_children, encode, nibble, ChildRef, Nibbles, Node, TreePath};
use crate::pending::PendingNodes;
use crate::{Entry, Error, NodeStore};

/// Same as `PatriciaTree.MinEntriesToParallelizeThreshold`.
const PARALLEL_THRESHOLD: usize = 128;

/// Same as `PatriciaTree.InPlaceSortThreshold`.
const IN_PLACE_SORT_THRESHOLD: usize = 32;

const FULL_BRANCH: u16 = 0xffff;

pub const EMPTY_TREE_HASH: [u8; 32] = [
    0x56, 0xe8, 0x1f, 0x17, 0x1b, 0xcc, 0x55, 0xa6, 0xff, 0x83, 0x45, 0xe6, 0x92, 0xc0, 0xf8, 0x6e,
    0x5b, 0x48, 0xe0, 0x1b, 0x99, 0x6c, 0xad, 0xc0, 0x01, 0x62, 0x2f, 0xb5, 0xe3, 0x63, 0xb4, 0x21,
];

thread_local! {
    static BUFFER: RefCell<Vec<u8>> = RefCell::new(Vec::with_capacity(1024));
    static TRAVERSE_STACK: RefCell<Vec<Frame>> = RefCell::new(Vec::with_capacity(64));
    static COMMIT_SCRATCH: RefCell<CommitScratch> = RefCell::default();
}

/// Applies `entries` to the trie whose root hash is `root`, writing every new node referenced by hash at its path into
/// `pending`, and returns the new root hash. Nodes are read from `cache`, then `pending`, then `store`, and every node
/// read or written is cached as the trie of `address`, the state trie when `None`. `entries` is reordered in place and
/// its keys must be unique.
///
/// Nodes that become unreachable are not removed.
pub fn bulk_set<S: NodeStore>(
    store: &S,
    pending: &PendingNodes,
    cache: Option<&TrieNodeCache>,
    address: Option<&[u8; 32]>,
    root: &[u8; 32],
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

    let mut root_path = TreePath::default();
    let context = Context {
        store,
        pending,
        cache,
        address,
        values,
    };
    let mut root_reference = match *root {
        EMPTY_TREE_HASH => ChildRef::Empty,
        hash => ChildRef::Hash(hash),
    };
    let root_node = context.load(&root_path, &mut root_reference)?;
    let mut sort_buffer = entries.to_vec();
    match context.update(entries, &mut sort_buffer, &mut root_path, root_node)? {
        (_, false) => Ok(*root),
        (None, true) => Ok(EMPTY_TREE_HASH),
        (Some(root), true) => match context.commit_tree(root, root_path, true).0 {
            ChildRef::Hash(hash) => Ok(hash),
            _ => unreachable!("root is always hashed"),
        },
    }
}

struct Context<'a, S> {
    store: &'a S,
    pending: &'a PendingNodes,
    cache: Option<&'a TrieNodeCache>,
    address: Option<&'a [u8; 32]>,
    values: &'a [u8],
}

struct ChildUpdate {
    /// The child with no pending descendants, kept when its reference is not pending so collapsing a branch need not
    /// load it again.
    node: Option<Node>,
    changed: bool,
}

/// A level `set_one` walked through. The child it walked into is taken out of the node when `pending`, and otherwise
/// left in place to be kept if nothing below changes.
enum Frame {
    Branch {
        children: Box<[ChildRef; 16]>,
        child_nibble: u8,
        pending: bool,
    },
    Extension {
        key: Nibbles,
        child: ChildRef,
        pending: bool,
    },
}

/// A node being committed, which becomes child `slot` of the pending node at index `parent`.
struct PendingNode {
    path: TreePath,
    node: Box<Node>,
    parent: u32,
    slot: u8,
}

/// Buffers `commit_tree` reuses between calls on one thread.
#[derive(Default)]
struct CommitScratch {
    nodes: Vec<PendingNode>,
    order: Vec<u32>,
    /// (index, RLP start, RLP end) of every node in the level referenced by hash.
    hashed: Vec<(u32, usize, usize)>,
    hashes: Vec<[u8; 32]>,
}

type Bucket<'a> = (&'a mut [Entry], &'a mut [Entry]);

impl<S: NodeStore> Context<'_, S> {
    /// Applies `entries`, whose keys all start with `path`, to the `node` at `path`, like `PatriciaTree.BulkSet`.
    /// `entries` is reordered, with `sort_buffer` of the same length as scratch. Returns the new node, whose new
    /// descendants are pending, and whether it differs from `node`.
    fn update(
        &self,
        entries: &mut [Entry],
        sort_buffer: &mut [Entry],
        path: &mut TreePath,
        node: Option<Node>,
    ) -> Result<(Option<Node>, bool), Error> {
        match entries.len() {
            0 => return Ok((node, false)),
            1 => return self.set_one(&entries[0], path, node),
            _ => {}
        }

        let depth = path.depth();
        if depth == 64 {
            return Err(Error::DuplicateKey);
        }

        let was_empty = node.is_none();
        let (mut children, is_new) = match node {
            None => (empty_children(), true),
            Some(Node::Branch { children }) => (children, false),
            Some(node) => (make_fake_branch(node), true),
        };

        let mut indexes = [0usize; 16];
        let (entries, sort_buffer, mask) = if entries.len() <= 3 {
            let mask = sort_tiny(entries, depth, &mut indexes);
            (entries, sort_buffer, mask)
        } else if entries.len() < IN_PLACE_SORT_THRESHOLD {
            let mask = in_place_bucket_sort16(entries, depth, &mut indexes);
            (entries, sort_buffer, mask)
        } else {
            // The buckets land in the sort buffer, so the two swap roles below this level.
            let mask = bucket_sort16(entries, sort_buffer, depth, &mut indexes);
            (sort_buffer, entries, mask)
        };
        let parallel = entries.len() >= PARALLEL_THRESHOLD && mask == FULL_BRANCH;
        let mut buckets = split_buckets(mask, &indexes, entries, sort_buffer);

        let mut changed = is_new;
        let mut has_remove = false;
        let mut non_empty_count = 0;
        let mut resolved_nodes: [Option<Node>; 16] = Default::default();
        let mut apply = |child_nibble: usize, update: ChildUpdate, reference: &ChildRef| {
            if update.changed {
                changed = true;
                match reference {
                    ChildRef::Empty => has_remove = true,
                    _ => non_empty_count += 1,
                }
            }
            resolved_nodes[child_nibble] = update.node;
        };

        if parallel {
            let parent_path = *path;
            let existing: [ChildRef; 16] =
                std::array::from_fn(|index| std::mem::take(&mut children[index]));
            let updates: Vec<(ChildRef, ChildUpdate)> = buckets
                .into_par_iter()
                .zip(existing)
                .enumerate()
                .map(|(child_nibble, ((bucket, bucket_buffer), mut reference))| {
                    let mut child_path = parent_path.append(&[child_nibble as u8]);
                    // Every parallel task hashes its own subtree, so no level spans threads.
                    let update =
                        self.update_child(&mut child_path, &mut reference, true, |path, node| {
                            self.update(bucket, bucket_buffer, path, node)
                        })?;
                    Ok((reference, update))
                })
                .collect::<Result<_, Error>>()?;
            for (child_nibble, (reference, update)) in updates.into_iter().enumerate() {
                apply(child_nibble, update, &reference);
                children[child_nibble] = reference;
            }
        } else {
            path.push(0);
            let mut remaining = mask;
            while remaining != 0 {
                let child_nibble = remaining.trailing_zeros() as usize;
                remaining &= remaining - 1;
                path.set_last(child_nibble as u8);
                let (bucket, bucket_buffer) = std::mem::take(&mut buckets[child_nibble]);
                let reference = &mut children[child_nibble];
                let update = self.update_child(path, reference, false, |path, node| {
                    self.update(bucket, bucket_buffer, path, node)
                })?;
                apply(child_nibble, update, reference);
            }
            path.truncate(depth);
        }

        if !changed {
            return Ok((Some(Node::Branch { children }), false));
        }

        let result = if (has_remove || is_new) && non_empty_count < 2 {
            self.maybe_combine(children, path, &mut resolved_nodes)?
        } else {
            Some(Node::Branch { children })
        };
        if was_empty && result.is_none() {
            return Ok((None, false));
        }
        Ok((result, true))
    }

    /// Applies a single entry to the `node` at `path`, like `PatriciaTree.SetNew`: walks down to where the entry
    /// lands, pushing every level it passes, then pops them to rebuild the path back up.
    fn set_one(
        &self,
        entry: &Entry,
        path: &mut TreePath,
        node: Option<Node>,
    ) -> Result<(Option<Node>, bool), Error> {
        TRAVERSE_STACK.with_borrow_mut(|stack| {
            stack.clear();
            let value = self.value(entry);
            let mut node = node;
            let (mut result, changed) = loop {
                let depth = path.depth();
                match node {
                    None => {
                        break match value {
                            [] => (None, false),
                            _ => (
                                Some(Node::Leaf {
                                    key: Nibbles::from_key(&entry.key, depth, 64 - depth),
                                    value: value.to_vec(),
                                }),
                                true,
                            ),
                        }
                    }
                    Some(Node::Branch { mut children }) => {
                        let child_nibble = nibble(&entry.key, depth);
                        let reference = &mut children[child_nibble as usize];
                        let pending = matches!(reference, ChildRef::Pending(_));
                        path.push(child_nibble);
                        node = self.load(path, reference)?;
                        stack.push(Frame::Branch {
                            children,
                            child_nibble,
                            pending,
                        });
                    }
                    Some(Node::Leaf {
                        key,
                        value: old_value,
                    }) => {
                        let matched = matched_length(key.as_slice(), &entry.key, depth);
                        if matched == key.len() {
                            break match value {
                                [] => (None, true),
                                _ if value == old_value.as_slice() => (
                                    Some(Node::Leaf {
                                        key,
                                        value: old_value,
                                    }),
                                    false,
                                ),
                                _ => (
                                    Some(Node::Leaf {
                                        key,
                                        value: value.to_vec(),
                                    }),
                                    true,
                                ),
                            };
                        }
                        if value.is_empty() {
                            break (
                                Some(Node::Leaf {
                                    key,
                                    value: old_value,
                                }),
                                false,
                            );
                        }

                        let existing = ChildRef::Pending(Box::new(Node::Leaf {
                            key: Nibbles::from_slice(&key.as_slice()[matched + 1..]),
                            value: old_value,
                        }));
                        break (
                            Some(diverging_branch(
                                entry,
                                depth,
                                matched,
                                key.as_slice()[matched],
                                existing,
                                value,
                            )),
                            true,
                        );
                    }
                    Some(Node::Extension { key, mut child }) => {
                        let matched = matched_length(key.as_slice(), &entry.key, depth);
                        if matched == key.len() {
                            path.push_all(key.as_slice());
                            let pending = matches!(child, ChildRef::Pending(_));
                            node = self.load(path, &mut child)?;
                            stack.push(Frame::Extension {
                                key,
                                child,
                                pending,
                            });
                            continue;
                        }
                        if value.is_empty() {
                            break (Some(Node::Extension { key, child }), false);
                        }

                        let rest = &key.as_slice()[matched + 1..];
                        let existing = match rest {
                            [] => child,
                            _ => ChildRef::Pending(Box::new(Node::Extension {
                                key: Nibbles::from_slice(rest),
                                child,
                            })),
                        };
                        break (
                            Some(diverging_branch(
                                entry,
                                depth,
                                matched,
                                key.as_slice()[matched],
                                existing,
                                value,
                            )),
                            true,
                        );
                    }
                }
            };

            while let Some(frame) = stack.pop() {
                match frame {
                    Frame::Extension {
                        key,
                        child,
                        pending,
                    } => {
                        path.truncate(path.depth() - key.len());
                        result = match (changed, pending) {
                            (false, false) => Some(Node::Extension { key, child }),
                            (false, true) => result.map(|node| Node::Extension {
                                key,
                                child: ChildRef::Pending(Box::new(node)),
                            }),
                            (true, _) => result.map(|node| prepend(key.as_slice(), node)),
                        };
                    }
                    Frame::Branch {
                        mut children,
                        child_nibble,
                        pending,
                    } => {
                        path.truncate(path.depth() - 1);
                        let removed = changed && result.is_none();
                        if changed || pending {
                            children[child_nibble as usize] = match result.take() {
                                Some(node) => ChildRef::Pending(Box::new(node)),
                                None => ChildRef::Empty,
                            };
                        }
                        result = match removed {
                            true => {
                                let mut resolved_nodes = Default::default();
                                self.maybe_combine(children, path, &mut resolved_nodes)?
                            }
                            false => Some(Node::Branch { children }),
                        };
                    }
                }
            }
            Ok((result, changed))
        })
    }

    /// Loads the child at `path` that `reference` refers to, applies `update` to it and points `reference` at the
    /// result. A changed child is committed when `commit` is set and left pending otherwise.
    fn update_child(
        &self,
        path: &mut TreePath,
        reference: &mut ChildRef,
        commit: bool,
        update: impl FnOnce(&mut TreePath, Option<Node>) -> Result<(Option<Node>, bool), Error>,
    ) -> Result<ChildUpdate, Error> {
        let pending = matches!(reference, ChildRef::Pending(_));
        let node = self.load(path, reference)?;
        let (node, changed) = update(path, node)?;
        let node = match node {
            None => {
                *reference = ChildRef::Empty;
                None
            }
            Some(node) if !changed && !pending => Some(node),
            Some(node) if commit => {
                let (committed, node) = self.commit_tree(node, *path, false);
                *reference = committed;
                Some(node)
            }
            Some(node) => {
                *reference = ChildRef::Pending(Box::new(node));
                None
            }
        };
        Ok(ChildUpdate { node, changed })
    }

    /// Turns a branch left with fewer than two children into nothing, an extension, or its only child with the
    /// nibble prepended, like `PatriciaTree.MaybeCombineNode`.
    fn maybe_combine(
        &self,
        mut children: Box<[ChildRef; 16]>,
        path: &mut TreePath,
        resolved_nodes: &mut [Option<Node>; 16],
    ) -> Result<Option<Node>, Error> {
        let only_child = {
            let mut present = children
                .iter()
                .enumerate()
                .filter(|(_, child)| !matches!(child, ChildRef::Empty));
            match (present.next(), present.next()) {
                (None, _) => return Ok(None),
                (Some((index, _)), None) => index,
                _ => return Ok(Some(Node::Branch { children })),
            }
        };

        let depth = path.depth();
        path.push(only_child as u8);
        let (node, committed) = match (
            std::mem::take(&mut children[only_child]),
            resolved_nodes[only_child].take(),
        ) {
            (ChildRef::Pending(node), _) => (*node, None),
            (reference, Some(node)) => (node, Some(reference)),
            (mut reference, None) => (
                self.load(path, &mut reference)?.ok_or(Error::MissingNode)?,
                Some(reference),
            ),
        };
        path.truncate(depth);

        Ok(Some(match (node, committed) {
            (Node::Branch { .. }, Some(reference)) => Node::Extension {
                key: Nibbles::from_slice(&[only_child as u8]),
                child: reference,
            },
            (node, _) => prepend(&[only_child as u8], node),
        }))
    }

    /// Returns the node `child` refers to, taking it out of `child` when it is pending.
    fn load(&self, path: &TreePath, child: &mut ChildRef) -> Result<Option<Node>, Error> {
        match child {
            ChildRef::Empty => Ok(None),
            ChildRef::Pending(_) => match std::mem::take(child) {
                ChildRef::Pending(node) => Ok(Some(*node)),
                _ => unreachable!(),
            },
            ChildRef::Inline(rlp) => decode_rlp(rlp.as_slice()).map(Some),
            ChildRef::Hash(hash) => {
                if let Some(node) = self
                    .cache
                    .and_then(|cache| cache.get(self.address, path, hash))
                {
                    return Ok(Some(node));
                }
                let pending = BUFFER.with_borrow_mut(|buffer| {
                    self.pending
                        .copy_to(path, hash, buffer)
                        .then(|| decode_rlp(buffer))
                        .transpose()
                })?;
                let node = match pending {
                    Some(node) => node,
                    None => self.store.fetch(path, hash)?.ok_or(Error::MissingNode)?,
                };
                self.cache_node(path, hash, &node);
                Ok(Some(node))
            }
        }
    }

    fn cache_node(&self, path: &TreePath, hash: &[u8; 32], node: &Node) {
        if let Some(cache) = self.cache {
            cache.insert(self.address, path, hash, node);
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
        COMMIT_SCRATCH.with_borrow_mut(|scratch| {
            let CommitScratch {
                nodes,
                order,
                hashed,
                hashes,
            } = scratch;
            collect_pending(Box::new(node), path, 0, 0, nodes);

            let mut level_ends = [0usize; 65];
            for pending in nodes.iter() {
                level_ends[pending.path.depth()] += 1;
            }
            let mut total = 0;
            for depth in (0..65).rev() {
                total += level_ends[depth];
                level_ends[depth] = total;
            }
            order.resize(nodes.len(), 0);
            let mut positions = level_ends;
            for (index, pending) in nodes.iter().enumerate() {
                let position = &mut positions[pending.path.depth()];
                *position -= 1;
                order[*position] = index as u32;
            }

            let mut top_reference = ChildRef::Empty;
            let mut resolve = |nodes: &mut Vec<PendingNode>, index: u32, reference: ChildRef| {
                if index == 0 {
                    top_reference = reference;
                } else {
                    let PendingNode { parent, slot, .. } = nodes[index as usize];
                    nodes[parent as usize]
                        .node
                        .set_child(slot as usize, reference);
                }
            };
            BUFFER.with_borrow_mut(|buffer| {
                let mut start = 0;
                for &end in level_ends.iter().rev() {
                    if end == start {
                        continue;
                    }
                    buffer.clear();
                    hashed.clear();
                    for &index in &order[start..end] {
                        let rlp_start = buffer.len();
                        encode(&nodes[index as usize].node, buffer);
                        if buffer.len() - rlp_start < 32 && !(is_root && index == 0) {
                            resolve(nodes, index, ChildRef::inline(&buffer[rlp_start..]));
                            buffer.truncate(rlp_start);
                        } else {
                            hashed.push((index, rlp_start, buffer.len()));
                        }
                    }

                    hashes.resize(hashed.len(), [0; 32]);
                    keccak_many(
                        |position| {
                            let (_, rlp_start, rlp_end) = hashed[position];
                            &buffer[rlp_start..rlp_end]
                        },
                        hashes,
                    );
                    self.pending.write(hashed.iter().zip(hashes.iter()).map(
                        |(&(index, rlp_start, rlp_end), hash)| {
                            (
                                &nodes[index as usize].path,
                                hash,
                                &buffer[rlp_start..rlp_end],
                            )
                        },
                    ));
                    for (&(index, _, _), &hash) in hashed.iter().zip(hashes.iter()) {
                        // Deeper levels are done, so the node's children are all resolved.
                        let PendingNode { path, node, .. } = &nodes[index as usize];
                        self.cache_node(path, &hash, node);
                        resolve(nodes, index, ChildRef::Hash(hash));
                    }
                    start = end;
                }
            });

            let top = nodes.swap_remove(0);
            nodes.clear();
            (top_reference, *top.node)
        })
    }

    fn value(&self, entry: &Entry) -> &[u8] {
        &self.values[entry.offset as usize..entry.offset as usize + entry.length as usize]
    }
}

/// Moves `node` and its pending descendants into `nodes`, parents before their children.
fn collect_pending(
    node: Box<Node>,
    path: TreePath,
    parent: u32,
    slot: u8,
    nodes: &mut Vec<PendingNode>,
) {
    let index = nodes.len();
    nodes.push(PendingNode {
        path,
        node,
        parent,
        slot,
    });
    for slot in 0..16u8 {
        let (child, child_path) = match nodes[index].node.as_mut() {
            Node::Branch { children } => match &mut children[slot as usize] {
                reference @ ChildRef::Pending(_) => {
                    (std::mem::take(reference), path.append(&[slot]))
                }
                _ => continue,
            },
            Node::Extension { key, child } if matches!(child, ChildRef::Pending(_)) => {
                (std::mem::take(child), path.append(key.as_slice()))
            }
            _ => return,
        };
        let ChildRef::Pending(child) = child else {
            unreachable!()
        };
        collect_pending(child, child_path, index as u32, slot, nodes);
    }
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

/// Replaces a leaf or an extension with a branch holding what remains of it, like `PatriciaTree.MakeFakeBranch`.
fn make_fake_branch(node: Node) -> Box<[ChildRef; 16]> {
    let mut children = empty_children();
    match node {
        Node::Leaf { key, value } => {
            children[key.as_slice()[0] as usize] = ChildRef::Pending(Box::new(Node::Leaf {
                key: Nibbles::from_slice(&key.as_slice()[1..]),
                value,
            }))
        }
        Node::Extension { key, child } if key.len() == 1 => {
            children[key.as_slice()[0] as usize] = child
        }
        Node::Extension { key, child } => {
            children[key.as_slice()[0] as usize] = ChildRef::Pending(Box::new(Node::Extension {
                key: Nibbles::from_slice(&key.as_slice()[1..]),
                child,
            }))
        }
        Node::Branch { .. } => unreachable!("already a branch"),
    }
    children
}

/// The branch where `entry` diverges from an existing leaf or extension after `matched` nibbles, holding `existing`
/// under `existing_nibble`, behind an extension of the matched nibbles when there are any.
fn diverging_branch(
    entry: &Entry,
    depth: usize,
    matched: usize,
    existing_nibble: u8,
    existing: ChildRef,
    value: &[u8],
) -> Node {
    let branch_depth = depth + matched;
    let mut children = empty_children();
    children[existing_nibble as usize] = existing;
    children[nibble(&entry.key, branch_depth) as usize] = ChildRef::Pending(Box::new(Node::Leaf {
        key: Nibbles::from_key(&entry.key, branch_depth + 1, 63 - branch_depth),
        value: value.to_vec(),
    }));
    let branch = Node::Branch { children };
    match matched {
        0 => branch,
        _ => Node::Extension {
            key: Nibbles::from_key(&entry.key, depth, matched),
            child: ChildRef::Pending(Box::new(branch)),
        },
    }
}

/// Sets `indexes` to where each nibble at `depth` starts in `entries`, which are already grouped by it, and returns
/// the mask of nibbles present.
fn mark_buckets(entries: &[Entry], depth: usize, indexes: &mut [usize; 16]) -> u16 {
    let mut mask = 0;
    for (index, entry) in entries.iter().enumerate().rev() {
        let child_nibble = nibble(&entry.key, depth);
        indexes[child_nibble as usize] = index;
        mask |= 1 << child_nibble;
    }
    mask
}

/// Groups two or three entries by their nibble at `depth` with compare-and-swaps, like `PatriciaTree.SortTiny`.
fn sort_tiny(entries: &mut [Entry], depth: usize, indexes: &mut [usize; 16]) -> u16 {
    let length = entries.len();
    let mut compare_and_swap = |left: usize, right: usize| {
        if nibble(&entries[left].key, depth) > nibble(&entries[right].key, depth) {
            entries.swap(left, right);
        }
    };
    compare_and_swap(0, 1);
    if length == 3 {
        compare_and_swap(1, 2);
        compare_and_swap(0, 1);
    }
    mark_buckets(entries, depth, indexes)
}

/// Groups fewer than [`IN_PLACE_SORT_THRESHOLD`] entries by their nibble at `depth` in place, like
/// `PatriciaTree.InPlaceBucketSort16`: sorts small (index, nibble) pairs, then permutes the entries by following cycles.
fn in_place_bucket_sort16(entries: &mut [Entry], depth: usize, indexes: &mut [usize; 16]) -> u16 {
    let mut pairs = [(0u8, 0u8); IN_PLACE_SORT_THRESHOLD];
    let pairs = &mut pairs[..entries.len()];
    for (index, pair) in pairs.iter_mut().enumerate() {
        *pair = (index as u8, nibble(&entries[index].key, depth));
    }
    for index in 1..pairs.len() {
        let pair = pairs[index];
        let mut position = index;
        while position > 0 && pairs[position - 1].1 > pair.1 {
            pairs[position] = pairs[position - 1];
            position -= 1;
        }
        pairs[position] = pair;
    }

    for start in 0..pairs.len() {
        if pairs[start].0 as usize == start {
            continue;
        }
        let displaced = entries[start];
        let mut position = start;
        loop {
            let source = pairs[position].0 as usize;
            pairs[position].0 = position as u8;
            if source == start {
                entries[position] = displaced;
                break;
            }
            entries[position] = entries[source];
            position = source;
        }
    }

    mark_buckets(entries, depth, indexes)
}

/// Counting-sorts `entries` by their nibble at `depth` into `target`, like `PatriciaTree.BucketSort16`.
fn bucket_sort16(
    entries: &[Entry],
    target: &mut [Entry],
    depth: usize,
    indexes: &mut [usize; 16],
) -> u16 {
    let mut counts = [0usize; 16];
    for entry in entries {
        counts[nibble(&entry.key, depth) as usize] += 1;
    }

    let mut mask = 0;
    let mut starts = [0usize; 16];
    let mut total = 0;
    for child_nibble in 0..16 {
        starts[child_nibble] = total;
        total += counts[child_nibble];
        if counts[child_nibble] != 0 {
            mask |= 1 << child_nibble;
            indexes[child_nibble] = starts[child_nibble];
        }
    }

    for entry in entries {
        let child_nibble = nibble(&entry.key, depth) as usize;
        target[starts[child_nibble]] = *entry;
        starts[child_nibble] += 1;
    }
    mask
}

/// Splits `entries` and `sort_buffer` into the bucket of every nibble in `mask`, indexed by nibble.
fn split_buckets<'a>(
    mask: u16,
    indexes: &[usize; 16],
    mut entries: &'a mut [Entry],
    mut sort_buffer: &'a mut [Entry],
) -> [Bucket<'a>; 16] {
    let mut buckets: [Bucket<'a>; 16] = Default::default();
    let total = entries.len();
    let mut remaining = mask;
    while remaining != 0 {
        let child_nibble = remaining.trailing_zeros() as usize;
        remaining &= remaining - 1;
        let end = match remaining {
            0 => total,
            _ => indexes[remaining.trailing_zeros() as usize],
        };
        let length = end - indexes[child_nibble];
        let (bucket, rest) = std::mem::take(&mut entries).split_at_mut(length);
        let (bucket_buffer, rest_buffer) = std::mem::take(&mut sort_buffer).split_at_mut(length);
        buckets[child_nibble] = (bucket, bucket_buffer);
        entries = rest;
        sort_buffer = rest_buffer;
    }
    buckets
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
    use super::*;
    use crate::keccak::keccak;
    use crate::pending;

    /// Holds nothing, so every node is read back from the pending nodes.
    struct EmptyStore;

    impl NodeStore for EmptyStore {
        fn fetch(&self, _: &TreePath, _: &[u8; 32]) -> Result<Option<Node>, Error> {
            Ok(None)
        }
    }

    type Item = ([u8; 32], Vec<u8>);

    fn apply(pending: &PendingNodes, root: [u8; 32], items: &[Item]) -> Result<[u8; 32], Error> {
        apply_cached(pending, None, root, items)
    }

    fn apply_cached(
        pending: &PendingNodes,
        cache: Option<&TrieNodeCache>,
        root: [u8; 32],
        items: &[Item],
    ) -> Result<[u8; 32], Error> {
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
        bulk_set(
            &EmptyStore,
            pending,
            cache,
            None,
            &root,
            &mut entries,
            &values,
        )
    }

    /// Checks every written node's hash and returns the RLP written at `path`.
    fn written_at(pending: &PendingNodes, path: &TreePath) -> Vec<u8> {
        pending.with_view(|nodes, rlp| {
            let mut found = None;
            for node in nodes {
                let node_rlp = &rlp[node.rlp_offset as usize..][..node.rlp_length as usize];
                assert_eq!(keccak(node_rlp), node.hash, "written node hash");
                if node.path == *path {
                    found = Some(node_rlp.to_vec());
                }
            }
            found.expect("node written at path")
        })
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
        apply(&PendingNodes::default(), EMPTY_TREE_HASH, items).unwrap()
    }

    #[test]
    fn bulk_set_is_independent_of_batching_and_deletes_back_to_empty() {
        let items = generate_items(5000, 0x1234_5678);

        let store = pending::rent(items.len());
        assert_eq!(apply(&store, EMPTY_TREE_HASH, &[]), Ok(EMPTY_TREE_HASH));
        let root = apply(&store, EMPTY_TREE_HASH, &items).unwrap();
        assert_eq!(keccak(&written_at(&store, &TreePath::default())), root);
        assert_eq!(apply(&store, root, &[]), Ok(root));

        let batched = PendingNodes::default();
        let mut batched_root = EMPTY_TREE_HASH;
        let mut start = 0;
        for size in [1, 7, 300, items.len() - 308] {
            batched_root = apply(&batched, batched_root, &items[start..start + size]).unwrap();
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
        let changed_root = apply(&store, root, &changes).unwrap();
        assert_eq!(changed_root, root_of(&expected));

        // Every node the first bulk set wrote is cached, so the second needs neither the pending nodes nor the store.
        let cache = TrieNodeCache::new(256 << 20);
        let cached = PendingNodes::default();
        let cached_root =
            apply_cached(&cached, Some(&cache), EMPTY_TREE_HASH, &items[..300]).unwrap();
        cached.clear();
        assert_eq!(
            apply_cached(&cached, Some(&cache), cached_root, &items[300..600]),
            Ok(root_of(&items[..600]))
        );

        let deletes: Vec<Item> = expected.iter().map(|(key, _)| (*key, Vec::new())).collect();
        assert_eq!(apply(&store, changed_root, &deletes), Ok(EMPTY_TREE_HASH));

        assert_eq!(
            apply(
                &store,
                EMPTY_TREE_HASH,
                &[items[0].clone(), items[0].clone()]
            ),
            Err(Error::DuplicateKey)
        );

        pending::give_back(store);
        assert_eq!(pending::rent(items.len()).count(), 0);
    }
}
