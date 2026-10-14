// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

//! Nodes written by bulk sets, kept until the caller publishes them, and a pool to reuse their buffers.

use std::collections::hash_map::Entry as MapEntry;
use std::collections::HashMap;
use std::sync::Mutex;

use rustc_hash::FxBuildHasher;

use crate::node::TreePath;

/// A written node, whose RLP is `rlp_length` bytes at `rlp_offset` in the arena of its [`PendingNodes`].
#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct WrittenNode {
    pub path: TreePath,
    pub hash: [u8; 32],
    pub rlp_offset: i32,
    pub rlp_length: i32,
}

// Mirrored by NativeWrittenNode in C#.
const _: () = {
    assert!(std::mem::size_of::<WrittenNode>() == 80);
    assert!(std::mem::offset_of!(WrittenNode, path) == 0);
    assert!(std::mem::offset_of!(WrittenNode, hash) == 40);
    assert!(std::mem::offset_of!(WrittenNode, rlp_offset) == 72);
    assert!(std::mem::offset_of!(WrittenNode, rlp_length) == 76);
};

/// The nodes bulk sets wrote, one per path with the last write winning, so later bulk sets read them back.
#[derive(Default)]
pub struct PendingNodes(Mutex<Inner>);

#[derive(Default)]
struct Inner {
    nodes: Vec<WrittenNode>,
    index: HashMap<TreePath, u32, FxBuildHasher>,
    rlp: Vec<u8>,
}

impl PendingNodes {
    fn with_capacity(nodes: usize) -> Self {
        Self(Mutex::new(Inner {
            nodes: Vec::with_capacity(nodes),
            index: HashMap::with_capacity_and_hasher(nodes, FxBuildHasher),
            rlp: Vec::with_capacity(nodes * INITIAL_ARENA_BYTES_PER_NODE),
        }))
    }

    /// Stores each `(path, hash, rlp)` under one lock. Overwriting a path leaves its old RLP in the arena until
    /// [`PendingNodes::clear`].
    pub fn write<'a>(&self, records: impl Iterator<Item = (&'a TreePath, &'a [u8; 32], &'a [u8])>) {
        let mut inner = self.0.lock().unwrap();
        let Inner { nodes, index, rlp } = &mut *inner;
        for (path, hash, node_rlp) in records {
            let record = WrittenNode {
                path: *path,
                hash: *hash,
                rlp_offset: rlp.len() as i32,
                rlp_length: node_rlp.len() as i32,
            };
            rlp.extend_from_slice(node_rlp);
            match index.entry(*path) {
                MapEntry::Occupied(entry) => nodes[*entry.get() as usize] = record,
                MapEntry::Vacant(entry) => {
                    entry.insert(nodes.len() as u32);
                    nodes.push(record);
                }
            }
        }
    }

    /// Replaces `buffer` with the RLP written at `path` when its hash is `hash`. Returns `false` otherwise.
    pub fn copy_to(&self, path: &TreePath, hash: &[u8; 32], buffer: &mut Vec<u8>) -> bool {
        let inner = self.0.lock().unwrap();
        let Some(&position) = inner.index.get(path) else {
            return false;
        };
        let node = &inner.nodes[position as usize];
        if node.hash != *hash {
            return false;
        }
        buffer.clear();
        buffer
            .extend_from_slice(&inner.rlp[node.rlp_offset as usize..][..node.rlp_length as usize]);
        true
    }

    pub fn count(&self) -> usize {
        self.0.lock().unwrap().nodes.len()
    }

    /// Removes every node, keeping the capacity.
    pub fn clear(&self) {
        let mut inner = self.0.lock().unwrap();
        inner.nodes.clear();
        inner.index.clear();
        inner.rlp.clear();
    }

    /// Calls `view` with the written nodes and the arena their RLP offsets point into.
    pub fn with_view<R>(&self, view: impl FnOnce(&[WrittenNode], &[u8]) -> R) -> R {
        let inner = self.0.lock().unwrap();
        view(&inner.nodes, &inner.rlp)
    }
}

/// Pooled size classes as (most nodes, structures kept). A bulk set writes about [`NODES_PER_ENTRY`] nodes per entry.
///
/// Worst case retained: 262,144 nodes × (80 B record + ~90 B index + at most [`MAX_ARENA_BYTES_PER_NODE`] arena),
/// about 110 MB.
const CLASSES: [(usize, usize); 3] = [(64, 1024), (1024, 64), (16384, 8)];
const NODES_PER_ENTRY: usize = 4;
const INITIAL_ARENA_BYTES_PER_NODE: usize = 128;
const MAX_ARENA_BYTES_PER_NODE: usize = 256;

static POOL: Mutex<[Vec<Box<PendingNodes>>; 3]> = Mutex::new([Vec::new(), Vec::new(), Vec::new()]);

/// Takes an empty [`PendingNodes`] sized for a bulk set of `estimated_entries`, from the pool when it has one.
pub fn rent(estimated_entries: usize) -> Box<PendingNodes> {
    let nodes = estimated_entries.saturating_mul(NODES_PER_ENTRY);
    match CLASSES
        .iter()
        .position(|&(max_nodes, _)| nodes <= max_nodes)
    {
        Some(class) => {
            let pooled = POOL.lock().unwrap()[class].pop();
            pooled.unwrap_or_else(|| Box::new(PendingNodes::with_capacity(CLASSES[class].0)))
        }
        None => Box::new(PendingNodes::with_capacity(nodes)),
    }
}

/// Clears `pending` and keeps it in the largest class its capacity serves, unless that class is full or it grew past
/// the largest class.
pub fn give_back(pending: Box<PendingNodes>) {
    pending.clear();
    let (capacity, arena) = {
        let inner = pending.0.lock().unwrap();
        (inner.nodes.capacity(), inner.rlp.capacity())
    };
    let Some(class) = CLASSES
        .iter()
        .rposition(|&(max_nodes, _)| capacity >= max_nodes)
    else {
        return;
    };
    let (max_nodes, kept) = CLASSES[class];
    if capacity > CLASSES[CLASSES.len() - 1].0 || arena > max_nodes * MAX_ARENA_BYTES_PER_NODE {
        return;
    }
    let mut pool = POOL.lock().unwrap();
    if pool[class].len() < kept {
        pool[class].push(pending);
    }
}
