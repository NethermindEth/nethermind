// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

//! Port of `PatriciaTree.BulkSet`: entries are bucketed by nibble one level at a time, and new nodes stay pending
//! until `commit_tree` hashes and stores them.

use std::cell::RefCell;

use rayon::prelude::*;

use crate::keccak::keccak_many;
use crate::log::{LogPointer, LogSession, TrieNodeLog};
use crate::node::{
    decode_inline_leaf, decode_rlp, empty_children, encode, nibble, ChildRef, InlineRlp,
    LoggedNode, Nibbles, Node, NodeKind, TreePath,
};
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
/// `pending`, and returns the new root hash. Every node written also goes into `log`, or a temporary log when `None`,
/// with the new root indexed as the root of the trie of `address`, the state trie when `None`. Nodes reachable through
/// the root in `log` are read there in place, and the others from `pending`, then `store`, without being added to
/// `log`. `entries` is reordered in place and its keys must be unique.
///
/// Nodes that become unreachable are not removed.
pub fn bulk_set<S: NodeStore>(
    store: &S,
    pending: &PendingNodes,
    log: Option<&TrieNodeLog>,
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

    match log {
        Some(log) => bulk_set_in(store, pending, log, address, root, entries, values),
        None => TrieNodeLog::with_temporary(|log| {
            bulk_set_in(store, pending, log, address, root, entries, values)
        }),
    }
}

fn bulk_set_in<S: NodeStore>(
    store: &S,
    pending: &PendingNodes,
    log: &TrieNodeLog,
    address: Option<&[u8; 32]>,
    root: &[u8; 32],
    entries: &mut [Entry],
    values: &[u8],
) -> Result<[u8; 32], Error> {
    let session = log.pin();
    let mut root_path = TreePath::default();
    let context = Context {
        store,
        pending,
        // Nodes written during this bulk set are never read back, so with none before it the lookups always miss.
        read_pending: pending.count() > 0,
        log: &session,
        values,
    };
    let root_reference = match *root {
        EMPTY_TREE_HASH => ChildRef::Empty,
        hash => ChildRef::Hash {
            hash,
            pointer: session.root(address, &hash),
        },
    };
    let root_node = context.load(&root_path, &root_reference)?;
    let mut sort_buffer = entries.to_vec();
    match context.update(entries, &mut sort_buffer, &mut root_path, root_node)? {
        Updated::Unchanged(_) => Ok(*root),
        Updated::Changed(None) => Ok(EMPTY_TREE_HASH),
        Updated::Changed(Some(root)) => match context.commit_tree(root, root_path, true) {
            ChildRef::Hash { hash, pointer } => {
                session.set_root(address, &hash, pointer);
                Ok(hash)
            }
            _ => unreachable!("root is always hashed"),
        },
    }
}

struct Context<'a, S> {
    store: &'a S,
    pending: &'a PendingNodes,
    read_pending: bool,
    log: &'a LogSession<'a>,
    values: &'a [u8],
}

/// A node the walk reads.
enum Source {
    /// Read in place from the log, less the first `skip` nibbles of its key, dropped by the fake branches above it.
    /// Only a leaf or an extension is ever skipped.
    Logged { pointer: LogPointer, skip: u8 },
    /// Embedded in its parent, so always a leaf, see [`decode_inline_leaf`], less the first `skip` nibbles of its key.
    InlineLeaf { rlp: InlineRlp, skip: u8 },
    /// Read from the pending nodes or the store, as the log does not have it, and not added to the log.
    Decoded(Node),
}

/// What applying entries to a node did.
enum Updated {
    /// Nothing changed, so the node is handed back as it was given.
    Unchanged(Option<Source>),
    /// The node replacing it, `None` when removed, whose new descendants are pending.
    Changed(Option<Node>),
}

/// A level `set_one` walked through. A logged one is copied out only when the child it walked into changed.
enum Frame {
    /// A logged branch or extension, the extension less the first `skip` nibbles of its key.
    Logged {
        pointer: LogPointer,
        skip: u8,
        child_nibble: u8,
    },
    Branch {
        children: Box<[ChildRef; 16]>,
        child_nibble: u8,
    },
    Extension {
        key: Nibbles,
        child: ChildRef,
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
    /// (index, RLP start, RLP end) of every node referenced by hash, level by level.
    hashed: Vec<(u32, usize, usize)>,
    hashes: Vec<[u8; 32]>,
    /// Log pointers of the nodes of the current level referenced by hash.
    pointers: Vec<LogPointer>,
}

type Bucket<'a> = (&'a mut [Entry], &'a mut [Entry]);

impl<S: NodeStore> Context<'_, S> {
    /// Applies `entries`, whose keys all start with `path`, to the `node` at `path`, like `PatriciaTree.BulkSet`.
    /// `entries` is reordered, with `sort_buffer` of the same length as scratch.
    fn update(
        &self,
        entries: &mut [Entry],
        sort_buffer: &mut [Entry],
        path: &mut TreePath,
        node: Option<Source>,
    ) -> Result<Updated, Error> {
        match entries.len() {
            0 => return Ok(Updated::Unchanged(node)),
            1 => return self.set_one(&entries[0], path, node),
            _ => {}
        }

        let depth = path.depth();
        if depth == 64 {
            return Err(Error::DuplicateKey);
        }

        let was_empty = node.is_none();
        let mut logged = None;
        let mut remainders: [Option<Source>; 16] = Default::default();
        let (mut children, is_new) = match node {
            None => (empty_children(), true),
            Some(Source::Decoded(Node::Branch { children })) => (children, false),
            Some(Source::Logged { pointer, .. })
                if self.view(pointer).kind() == NodeKind::Branch =>
            {
                // Copied out up front, as at least two entries land in it, so it nearly always changes.
                let Node::Branch { children } = self.view(pointer).to_owned(0) else {
                    unreachable!("checked to be a branch")
                };
                logged = Some(pointer);
                (children, false)
            }
            Some(source) => (self.make_fake_branch(source, &mut remainders)?, true),
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

        for (child_nibble, remainder) in remainders.iter_mut().enumerate() {
            // No entry lands in it, so it stays as it is below the new branch.
            if mask & (1 << child_nibble) == 0 {
                if let Some(source) = remainder.take() {
                    children[child_nibble] = ChildRef::Pending(Box::new(self.into_node(source)?));
                }
            }
        }

        let mut changed = is_new;
        let mut has_remove = false;
        let mut non_empty_count = 0;
        let mut apply = |child_changed: bool, reference: &ChildRef| {
            if child_changed {
                changed = true;
                match reference {
                    ChildRef::Empty => has_remove = true,
                    _ => non_empty_count += 1,
                }
            }
        };

        if parallel {
            let parent_path = *path;
            let existing: [ChildRef; 16] =
                std::array::from_fn(|index| std::mem::take(&mut children[index]));
            let updates: Vec<(ChildRef, bool)> = buckets
                .into_par_iter()
                .zip(existing)
                .zip(remainders)
                .enumerate()
                .map(
                    |(child_nibble, (((bucket, bucket_buffer), mut reference), remainder))| {
                        let mut child_path = parent_path.append(&[child_nibble as u8]);
                        // Every parallel task hashes its own subtree, so no level spans threads.
                        let child_changed = self.update_child(
                            &mut child_path,
                            &mut reference,
                            remainder,
                            true,
                            |path, node| self.update(bucket, bucket_buffer, path, node),
                        )?;
                        Ok((reference, child_changed))
                    },
                )
                .collect::<Result<_, Error>>()?;
            for (child_nibble, (reference, child_changed)) in updates.into_iter().enumerate() {
                apply(child_changed, &reference);
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
                let child_changed = self.update_child(
                    path,
                    reference,
                    remainders[child_nibble].take(),
                    false,
                    |path, node| self.update(bucket, bucket_buffer, path, node),
                )?;
                apply(child_changed, reference);
            }
            path.truncate(depth);
        }

        if !changed {
            return Ok(Updated::Unchanged(Some(match logged {
                Some(pointer) => Source::Logged { pointer, skip: 0 },
                None => Source::Decoded(Node::Branch { children }),
            })));
        }

        let result = if (has_remove || is_new) && non_empty_count < 2 {
            self.maybe_combine(children, path)?
        } else {
            Some(Node::Branch { children })
        };
        if was_empty && result.is_none() {
            return Ok(Updated::Unchanged(None));
        }
        Ok(Updated::Changed(result))
    }

    /// Applies a single entry to the `node` at `path`, like `PatriciaTree.SetNew`: walks down to where the entry
    /// lands, pushing every level it passes, then pops them to rebuild the path back up.
    fn set_one(
        &self,
        entry: &Entry,
        path: &mut TreePath,
        node: Option<Source>,
    ) -> Result<Updated, Error> {
        TRAVERSE_STACK.with_borrow_mut(|stack| {
            stack.clear();
            let value = self.value(entry);
            let mut node = node;
            let mut result = loop {
                let depth = path.depth();
                let Some(source) = node else {
                    break match value {
                        [] => Updated::Unchanged(None),
                        _ => Updated::Changed(Some(Node::Leaf {
                            key: Nibbles::from_key(&entry.key, depth, 64 - depth),
                            value: value.to_vec(),
                        })),
                    };
                };

                match source {
                    Source::InlineLeaf { .. } | Source::Decoded(Node::Leaf { .. }) => {
                        break self.set_leaf(entry, depth, source, value)?
                    }
                    Source::Logged { pointer, skip } => {
                        let view = self.view(pointer);
                        match view.kind() {
                            NodeKind::Leaf => break self.set_leaf(entry, depth, source, value)?,
                            NodeKind::Branch => {
                                let child_nibble = nibble(&entry.key, depth);
                                path.push(child_nibble);
                                node = self.load(path, &view.child(child_nibble))?;
                                stack.push(Frame::Logged {
                                    pointer,
                                    skip,
                                    child_nibble,
                                });
                            }
                            NodeKind::Extension => {
                                let key = &view.key()[skip as usize..];
                                let matched = matched_length(key, &entry.key, depth);
                                if matched < key.len() {
                                    break match value {
                                        [] => Updated::Unchanged(Some(source)),
                                        _ => Updated::Changed(Some(diverge_extension(
                                            entry,
                                            depth,
                                            matched,
                                            key,
                                            view.child(0),
                                            value,
                                        ))),
                                    };
                                }
                                path.push_all(key);
                                node = self.load(path, &view.child(0))?;
                                stack.push(Frame::Logged {
                                    pointer,
                                    skip,
                                    child_nibble: 0,
                                });
                            }
                        }
                    }
                    Source::Decoded(Node::Branch { children }) => {
                        let child_nibble = nibble(&entry.key, depth);
                        path.push(child_nibble);
                        node = self.load(path, &children[child_nibble as usize])?;
                        stack.push(Frame::Branch {
                            children,
                            child_nibble,
                        });
                    }
                    Source::Decoded(Node::Extension { key, child }) => {
                        let matched = matched_length(key.as_slice(), &entry.key, depth);
                        if matched < key.len() {
                            break match value {
                                [] => Updated::Unchanged(Some(Source::Decoded(Node::Extension {
                                    key,
                                    child,
                                }))),
                                _ => Updated::Changed(Some(diverge_extension(
                                    entry,
                                    depth,
                                    matched,
                                    key.as_slice(),
                                    child,
                                    value,
                                ))),
                            };
                        }
                        path.push_all(key.as_slice());
                        node = self.load(path, &child)?;
                        stack.push(Frame::Extension { key, child });
                    }
                }
            };

            while let Some(frame) = stack.pop() {
                result = match frame {
                    Frame::Logged {
                        pointer,
                        skip,
                        child_nibble,
                    } => {
                        let view = self.view(pointer);
                        let key = &view.key()[skip as usize..];
                        match view.kind() {
                            NodeKind::Extension => path.truncate(path.depth() - key.len()),
                            _ => path.truncate(path.depth() - 1),
                        }
                        match (result, view.kind()) {
                            (Updated::Unchanged(_), _) => {
                                Updated::Unchanged(Some(Source::Logged { pointer, skip }))
                            }
                            (Updated::Changed(node), NodeKind::Extension) => {
                                Updated::Changed(node.map(|node| prepend(key, node)))
                            }
                            (Updated::Changed(node), _) => {
                                let Node::Branch { children } = view.to_owned(0) else {
                                    unreachable!("only branches and extensions are walked through")
                                };
                                Updated::Changed(self.replace_child(
                                    children,
                                    child_nibble,
                                    node,
                                    path,
                                )?)
                            }
                        }
                    }
                    Frame::Branch {
                        children,
                        child_nibble,
                    } => {
                        path.truncate(path.depth() - 1);
                        match result {
                            Updated::Unchanged(_) => {
                                Updated::Unchanged(Some(Source::Decoded(Node::Branch { children })))
                            }
                            Updated::Changed(node) => Updated::Changed(self.replace_child(
                                children,
                                child_nibble,
                                node,
                                path,
                            )?),
                        }
                    }
                    Frame::Extension { key, child } => {
                        path.truncate(path.depth() - key.len());
                        match result {
                            Updated::Unchanged(_) => {
                                Updated::Unchanged(Some(Source::Decoded(Node::Extension {
                                    key,
                                    child,
                                })))
                            }
                            Updated::Changed(node) => {
                                Updated::Changed(node.map(|node| prepend(key.as_slice(), node)))
                            }
                        }
                    }
                };
            }
            Ok(result)
        })
    }

    /// Sets the key of `entry` to `value` in the leaf `source` at `depth`.
    fn set_leaf(
        &self,
        entry: &Entry,
        depth: usize,
        source: Source,
        value: &[u8],
    ) -> Result<Updated, Error> {
        let change = match &source {
            Source::Logged { pointer, skip } => {
                let view = self.view(*pointer);
                leaf_change(
                    entry,
                    depth,
                    &view.key()[*skip as usize..],
                    view.value(),
                    value,
                )
            }
            Source::InlineLeaf { rlp, skip } => {
                let (key, old_value) = decode_inline_leaf(rlp.as_slice())?;
                leaf_change(
                    entry,
                    depth,
                    &key.as_slice()[*skip as usize..],
                    old_value,
                    value,
                )
            }
            Source::Decoded(Node::Leaf {
                key,
                value: old_value,
            }) => leaf_change(entry, depth, key.as_slice(), old_value, value),
            Source::Decoded(_) => unreachable!("not a leaf"),
        };
        Ok(match change {
            Some(node) => Updated::Changed(node),
            None => Updated::Unchanged(Some(source)),
        })
    }

    /// Applies `update` to the child at `path`, `remainder` when given, otherwise the node `reference` refers to, and
    /// points `reference` at the result. A changed child, or a remainder even unchanged, is committed when `commit` is
    /// set and left pending otherwise. Returns whether the child changed.
    fn update_child(
        &self,
        path: &mut TreePath,
        reference: &mut ChildRef,
        remainder: Option<Source>,
        commit: bool,
        update: impl FnOnce(&mut TreePath, Option<Source>) -> Result<Updated, Error>,
    ) -> Result<bool, Error> {
        let is_remainder = remainder.is_some();
        let node = match remainder {
            Some(source) => Some(source),
            None => self.load(path, reference)?,
        };
        let (node, changed) = match update(path, node)? {
            Updated::Changed(Some(node)) => (node, true),
            Updated::Changed(None) => {
                *reference = ChildRef::Empty;
                return Ok(true);
            }
            // A remainder is in no node yet, so it is copied out to be one.
            Updated::Unchanged(Some(source)) if is_remainder => (self.into_node(source)?, false),
            Updated::Unchanged(_) => return Ok(false),
        };
        *reference = match commit {
            true => self.commit_tree(node, *path, false),
            false => ChildRef::Pending(Box::new(node)),
        };
        Ok(changed)
    }

    /// Puts `node`, `None` when removed, as child `child_nibble` of the branch with `children` at `path`, collapsing
    /// the branch when the child was removed.
    fn replace_child(
        &self,
        mut children: Box<[ChildRef; 16]>,
        child_nibble: u8,
        node: Option<Node>,
        path: &mut TreePath,
    ) -> Result<Option<Node>, Error> {
        match node {
            Some(node) => {
                children[child_nibble as usize] = ChildRef::Pending(Box::new(node));
                Ok(Some(Node::Branch { children }))
            }
            None => {
                children[child_nibble as usize] = ChildRef::Empty;
                self.maybe_combine(children, path)
            }
        }
    }

    /// Turns a branch left with fewer than two children into nothing, an extension, or its only child with the
    /// nibble prepended, like `PatriciaTree.MaybeCombineNode`.
    fn maybe_combine(
        &self,
        mut children: Box<[ChildRef; 16]>,
        path: &mut TreePath,
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
        let combined = match std::mem::take(&mut children[only_child]) {
            ChildRef::Pending(node) => prepend(&[only_child as u8], *node),
            reference => {
                let source = self.load(path, &reference)?.ok_or(Error::MissingNode)?;
                let is_branch = match &source {
                    Source::Logged { pointer, .. } => {
                        self.view(*pointer).kind() == NodeKind::Branch
                    }
                    Source::Decoded(node) => matches!(node, Node::Branch { .. }),
                    Source::InlineLeaf { .. } => false,
                };
                match is_branch {
                    true => Node::Extension {
                        key: Nibbles::from_slice(&[only_child as u8]),
                        child: reference,
                    },
                    false => prepend(&[only_child as u8], self.into_node(source)?),
                }
            }
        };
        path.truncate(depth);
        Ok(Some(combined))
    }

    /// Splits a leaf or an extension into the children of a branch, like `PatriciaTree.MakeFakeBranch`: its child
    /// when its key is a single nibble, otherwise what remains of it below its first nibble, put in `remainders` to be
    /// walked in place.
    fn make_fake_branch(
        &self,
        source: Source,
        remainders: &mut [Option<Source>; 16],
    ) -> Result<Box<[ChildRef; 16]>, Error> {
        let mut children = empty_children();
        let (child_nibble, remainder) = match source {
            Source::Logged { pointer, skip } => {
                let view = self.view(pointer);
                let key = &view.key()[skip as usize..];
                if view.kind() == NodeKind::Extension && key.len() == 1 {
                    children[key[0] as usize] = view.child(0);
                    return Ok(children);
                }
                (
                    key[0],
                    Source::Logged {
                        pointer,
                        skip: skip + 1,
                    },
                )
            }
            Source::InlineLeaf { rlp, skip } => {
                let (key, _) = decode_inline_leaf(rlp.as_slice())?;
                let child_nibble = key.as_slice()[skip as usize];
                (
                    child_nibble,
                    Source::InlineLeaf {
                        rlp,
                        skip: skip + 1,
                    },
                )
            }
            Source::Decoded(Node::Leaf { key, value }) => (
                key.as_slice()[0],
                Source::Decoded(Node::Leaf {
                    key: Nibbles::from_slice(&key.as_slice()[1..]),
                    value,
                }),
            ),
            Source::Decoded(Node::Extension { key, child }) if key.len() == 1 => {
                children[key.as_slice()[0] as usize] = child;
                return Ok(children);
            }
            Source::Decoded(Node::Extension { key, child }) => (
                key.as_slice()[0],
                Source::Decoded(Node::Extension {
                    key: Nibbles::from_slice(&key.as_slice()[1..]),
                    child,
                }),
            ),
            Source::Decoded(Node::Branch { .. }) => unreachable!("already a branch"),
        };
        remainders[child_nibble as usize] = Some(remainder);
        Ok(children)
    }

    /// Returns the node `child` refers to. A node referenced by hash is read in place from the log when its pointer is
    /// valid, and otherwise from the pending nodes or the store, without adding it to the log.
    fn load(&self, path: &TreePath, child: &ChildRef) -> Result<Option<Source>, Error> {
        match child {
            ChildRef::Empty => Ok(None),
            ChildRef::Pending(_) => unreachable!("only children of nodes read are walked into"),
            ChildRef::Inline(rlp) => Ok(Some(Source::InlineLeaf { rlp: *rlp, skip: 0 })),
            ChildRef::Hash { pointer, .. } if self.log.is_valid(*pointer) => {
                Ok(Some(Source::Logged {
                    pointer: *pointer,
                    skip: 0,
                }))
            }
            ChildRef::Hash { hash, .. } => {
                let pending = match self.read_pending {
                    true => BUFFER.with_borrow_mut(|buffer| {
                        self.pending
                            .copy_to(path, hash, buffer)
                            .then(|| decode_rlp(buffer))
                            .transpose()
                    })?,
                    false => None,
                };
                let node = match pending {
                    Some(node) => node,
                    None => self.store.fetch(path, hash)?.ok_or(Error::MissingNode)?,
                };
                Ok(Some(Source::Decoded(node)))
            }
        }
    }

    fn view(&self, pointer: LogPointer) -> LoggedNode<'_> {
        LoggedNode::new(self.log.record(pointer))
    }

    /// Copies `source` out, to be changed or kept below a new node.
    fn into_node(&self, source: Source) -> Result<Node, Error> {
        Ok(match source {
            Source::Logged { pointer, skip } => self.view(pointer).to_owned(skip as usize),
            Source::InlineLeaf { rlp, skip } => {
                let (key, value) = decode_inline_leaf(rlp.as_slice())?;
                Node::Leaf {
                    key: Nibbles::from_slice(&key.as_slice()[skip as usize..]),
                    value: value.to_vec(),
                }
            }
            Source::Decoded(node) => node,
        })
    }

    /// Encodes and hashes `node` and every pending node below it, storing each one referenced by hash, that is,
    /// whose RLP is at least 32 bytes or which is the root. Returns the reference to `node`.
    ///
    /// Nodes are processed one path length at a time, deepest first, like `DirtyNodeHasher`: two nodes of one
    /// length are never each other's ancestor and by then all their children are resolved, so a whole level is
    /// hashed together by the batch Keccak kernel.
    fn commit_tree(&self, node: Node, path: TreePath, is_root: bool) -> ChildRef {
        COMMIT_SCRATCH.with_borrow_mut(|scratch| {
            let CommitScratch {
                nodes,
                order,
                hashed,
                hashes,
                pointers,
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
            let mut link_to_parent =
                |nodes: &mut Vec<PendingNode>, index: u32, reference: ChildRef| {
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
                buffer.clear();
                hashed.clear();
                let mut start = 0;
                for &end in level_ends.iter().rev() {
                    if end == start {
                        continue;
                    }
                    let level_start = hashed.len();
                    for &index in &order[start..end] {
                        let rlp_start = buffer.len();
                        encode(&nodes[index as usize].node, buffer);
                        if buffer.len() - rlp_start < 32 && !(is_root && index == 0) {
                            assert!(
                                matches!(*nodes[index as usize].node, Node::Leaf { .. }),
                                "only a leaf can be inline, see decode_inline_leaf"
                            );
                            link_to_parent(nodes, index, ChildRef::inline(&buffer[rlp_start..]));
                            buffer.truncate(rlp_start);
                        } else {
                            hashed.push((index, rlp_start, buffer.len()));
                        }
                    }

                    let level = &hashed[level_start..];
                    hashes.resize(hashed.len(), [0; 32]);
                    keccak_many(
                        |position| {
                            let (_, rlp_start, rlp_end) = level[position];
                            &buffer[rlp_start..rlp_end]
                        },
                        &mut hashes[level_start..],
                    );
                    // Appended, not read: deeper levels already pointed these nodes' children at their records, and
                    // their parents, appended with the next level, get these records' pointers.
                    pointers.clear();
                    self.log.append_all(
                        level
                            .iter()
                            .map(|&(index, _, _)| &*nodes[index as usize].node),
                        pointers,
                    );
                    for ((&(index, _, _), &hash), &pointer) in level
                        .iter()
                        .zip(&hashes[level_start..])
                        .zip(pointers.iter())
                    {
                        link_to_parent(nodes, index, ChildRef::Hash { hash, pointer });
                    }
                    start = end;
                }
                self.pending.write(hashed.len(), |position| {
                    let (index, rlp_start, rlp_end) = hashed[position];
                    (
                        &nodes[index as usize].path,
                        &hashes[position],
                        &buffer[rlp_start..rlp_end],
                    )
                });
            });

            nodes.clear();
            top_reference
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

/// What setting the key of `entry` to `value` does to the leaf at `depth` with `key` and `old_value`: `None` when it
/// stays, otherwise the node replacing it, `None` when removed.
fn leaf_change(
    entry: &Entry,
    depth: usize,
    key: &[u8],
    old_value: &[u8],
    value: &[u8],
) -> Option<Option<Node>> {
    let matched = matched_length(key, &entry.key, depth);
    if matched == key.len() {
        return match value {
            [] => Some(None),
            _ if value == old_value => None,
            _ => Some(Some(Node::Leaf {
                key: Nibbles::from_slice(key),
                value: value.to_vec(),
            })),
        };
    }
    if value.is_empty() {
        return None;
    }

    let existing = ChildRef::Pending(Box::new(Node::Leaf {
        key: Nibbles::from_slice(&key[matched + 1..]),
        value: old_value.to_vec(),
    }));
    Some(Some(diverging_branch(
        entry,
        depth,
        matched,
        key[matched],
        existing,
        value,
    )))
}

/// The node replacing the extension at `depth` with `key` and `child`, from which `entry` diverges after `matched`
/// nibbles.
fn diverge_extension(
    entry: &Entry,
    depth: usize,
    matched: usize,
    key: &[u8],
    child: ChildRef,
    value: &[u8],
) -> Node {
    let rest = &key[matched + 1..];
    let existing = match rest {
        [] => child,
        _ => ChildRef::Pending(Box::new(Node::Extension {
            key: Nibbles::from_slice(rest),
            child,
        })),
    };
    diverging_branch(entry, depth, matched, key[matched], existing, value)
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

    fn apply(
        pending: &PendingNodes,
        log: Option<&TrieNodeLog>,
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
            log,
            None,
            &root,
            &mut entries,
            &values,
        )
    }

    /// Checks every written node's hash and returns the RLP written at `path`.
    fn written_at(pending: &PendingNodes, path: &TreePath) -> Vec<u8> {
        let mut found = None;
        for shard in 0..pending.shard_count() {
            pending.with_view(shard, |nodes, rlp| {
                for node in nodes {
                    let node_rlp = &rlp[node.rlp_offset as usize..][..node.rlp_length as usize];
                    assert_eq!(keccak(node_rlp), node.hash, "written node hash");
                    if node.path == *path {
                        found = Some(node_rlp.to_vec());
                    }
                }
            });
        }
        found.expect("node written at path")
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
        apply(&PendingNodes::default(), None, EMPTY_TREE_HASH, items).unwrap()
    }

    #[test]
    fn bulk_set_is_independent_of_batching_and_deletes_back_to_empty() {
        let items = generate_items(5000, 0x1234_5678);

        // A temporary log per bulk set, then a minimal shared one, which keeps dropping the nodes of earlier bulk sets.
        let minimal = TrieNodeLog::new(0);
        for log in [None, Some(&minimal)] {
            let store = pending::rent(items.len());
            assert_eq!(
                apply(&store, log, EMPTY_TREE_HASH, &[]),
                Ok(EMPTY_TREE_HASH)
            );
            let root = apply(&store, log, EMPTY_TREE_HASH, &items).unwrap();
            assert_eq!(keccak(&written_at(&store, &TreePath::default())), root);
            assert_eq!(apply(&store, log, root, &[]), Ok(root));

            let batched = PendingNodes::default();
            let mut batched_root = EMPTY_TREE_HASH;
            let mut start = 0;
            for size in [1, 7, 300, items.len() - 308] {
                batched_root =
                    apply(&batched, log, batched_root, &items[start..start + size]).unwrap();
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
            let changed_root = apply(&store, log, root, &changes).unwrap();
            assert_eq!(changed_root, root_of(&expected));

            let deletes: Vec<Item> = expected.iter().map(|(key, _)| (*key, Vec::new())).collect();
            assert_eq!(
                apply(&store, log, changed_root, &deletes),
                Ok(EMPTY_TREE_HASH)
            );

            assert_eq!(
                apply(
                    &store,
                    log,
                    EMPTY_TREE_HASH,
                    &[items[0].clone(), items[0].clone()]
                ),
                Err(Error::DuplicateKey)
            );

            pending::give_back(store);
            assert_eq!(pending::rent(items.len()).count(), 0);
        }

        // Every node the first bulk set wrote is logged, so the second needs neither the pending nodes nor the store.
        let log = TrieNodeLog::new(256 << 20);
        let logged = PendingNodes::default();
        let logged_root = apply(&logged, Some(&log), EMPTY_TREE_HASH, &items[..300]).unwrap();
        logged.clear();
        assert_eq!(
            apply(&logged, Some(&log), logged_root, &items[300..600]),
            Ok(root_of(&items[..600]))
        );

        // Two entries landing at an existing leaf, inline or not, or extension split it below its first nibble, with
        // and without one landing on what remains of it, read from the log or decoded. Values are long enough that no
        // branch is inline.
        let key = |last: u8| {
            let mut key = [0xcd; 32];
            key[31] = last;
            key
        };
        let cases = [
            ([key(0x00), key(0x10)], [key(0x01), key(0x02)]),
            ([key(0x00), key(0x10)], [key(0x00), key(0x01)]),
            ([key(0x00), key(0x01)], [[0x00; 32], [0x11; 32]]),
            ([key(0x00), key(0x01)], [key(0x05), [0x00; 32]]),
        ];
        for (value, logged) in [vec![1; 7], vec![1; 40]]
            .into_iter()
            .flat_map(|value| [(value.clone(), false), (value, true)])
        {
            for (first, second) in &cases {
                let first: Vec<Item> = first.iter().map(|key| (*key, value.clone())).collect();
                let second: Vec<Item> = second.iter().map(|key| (*key, vec![2; 7])).collect();
                let mut expected: Vec<Item> = first
                    .iter()
                    .filter(|(key, _)| second.iter().all(|(other, _)| other != key))
                    .cloned()
                    .collect();
                expected.extend_from_slice(&second);
                let split = PendingNodes::default();
                let split_log = TrieNodeLog::new(0);
                let log = logged.then_some(&split_log);
                let first_root = apply(&split, log, EMPTY_TREE_HASH, &first).unwrap();
                assert_eq!(
                    apply(&split, log, first_root, &second),
                    Ok(root_of(&expected))
                );
            }
        }
    }
}
