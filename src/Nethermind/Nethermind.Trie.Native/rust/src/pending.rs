// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

//! Nodes written by bulk sets, kept until the caller publishes them, and a pool to reuse their buffers.

use std::collections::hash_map::Entry as MapEntry;
use std::collections::HashMap;
use std::sync::Mutex;

use rustc_hash::FxBuildHasher;

use crate::log::LogPointer;
use crate::node::TreePath;

/// A written node, whose RLP is `rlp_length` bytes at `rlp_offset` in the arena of its [`PendingNodes`], and whose
/// record is at `log_pointer` in the log of the bulk set that wrote it, meaningless for a temporary log.
#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct WrittenNode {
    pub path: TreePath,
    pub hash: [u8; 32],
    pub rlp_offset: i32,
    pub rlp_length: i32,
    pub log_pointer: LogPointer,
}

// Mirrored by NativeWrittenNode in C#.
const _: () = {
    assert!(std::mem::size_of::<WrittenNode>() == 88);
    assert!(std::mem::offset_of!(WrittenNode, path) == 0);
    assert!(std::mem::offset_of!(WrittenNode, hash) == 40);
    assert!(std::mem::offset_of!(WrittenNode, rlp_offset) == 72);
    assert!(std::mem::offset_of!(WrittenNode, rlp_length) == 76);
    assert!(std::mem::offset_of!(WrittenNode, log_pointer) == 80);
};

/// The nodes bulk sets wrote, one per path with the last write winning, so later bulk sets read them back. Sharded by
/// path, so the threads of a large bulk set write in parallel and the caller publishes the shards in parallel.
pub struct PendingNodes(Box<[Mutex<Inner>]>);

#[derive(Default)]
struct Inner {
    nodes: Vec<WrittenNode>,
    /// Position of the node at every path of the first `indexed` nodes, which hold no path twice.
    index: HashMap<TreePath, u32, FxBuildHasher>,
    indexed: usize,
    rlp: Vec<u8>,
}

impl Inner {
    /// Indexes the nodes written since the last call, each replacing the one at its path. Deferred until read, as a
    /// bulk set writes every path at most once, so only a later one can overwrite.
    fn settle(&mut self) {
        while self.indexed < self.nodes.len() {
            let node = self.nodes[self.indexed];
            match self.index.entry(node.path) {
                MapEntry::Occupied(entry) => {
                    self.nodes[*entry.get() as usize] = node;
                    self.nodes.swap_remove(self.indexed);
                }
                MapEntry::Vacant(entry) => {
                    entry.insert(self.indexed as u32);
                    self.indexed += 1;
                }
            }
        }
    }
}

impl Default for PendingNodes {
    fn default() -> Self {
        Self::with_capacity(0, 1)
    }
}

impl PendingNodes {
    /// Creates `shards` shards with room for `nodes` nodes in all.
    fn with_capacity(nodes: usize, shards: usize) -> Self {
        let nodes = nodes.div_ceil(shards);
        Self(
            (0..shards)
                .map(|_| {
                    Mutex::new(Inner {
                        nodes: Vec::with_capacity(nodes),
                        index: HashMap::with_capacity_and_hasher(nodes, FxBuildHasher),
                        indexed: 0,
                        rlp: Vec::with_capacity(nodes * INITIAL_ARENA_BYTES_PER_NODE),
                    })
                })
                .collect(),
        )
    }

    /// By the leading nibbles, so the parallel tasks of a bulk set, each under its own child of the root, write to
    /// shards of their own.
    fn shard(&self, path: &TreePath) -> usize {
        (path.path[0] as usize * self.0.len()) >> 8
    }

    /// Stores `record(index)`, a `(path, hash, rlp, log pointer)`, for every index below `count`, locking each shard
    /// written to once. Overwriting a path leaves its old RLP in the arena until [`PendingNodes::clear`].
    pub fn write<'a>(
        &self,
        count: usize,
        record: impl Fn(usize) -> (&'a TreePath, &'a [u8; 32], &'a [u8], LogPointer),
    ) {
        if self.0.len() == 1 {
            self.write_shard(0, (0..count).map(record));
            return;
        }

        // Counting-sorted by shard, so each record's shard is computed once.
        let shards: Vec<u8> = (0..count)
            .map(|index| self.shard(record(index).0) as u8)
            .collect();
        let mut starts = vec![0u32; self.0.len() + 1];
        for &shard in &shards {
            starts[shard as usize + 1] += 1;
        }
        for shard in 0..self.0.len() {
            starts[shard + 1] += starts[shard];
        }
        let mut order = vec![0u32; count];
        let mut positions = starts.clone();
        for (index, &shard) in shards.iter().enumerate() {
            order[positions[shard as usize] as usize] = index as u32;
            positions[shard as usize] += 1;
        }
        for shard in 0..self.0.len() {
            let run = &order[starts[shard] as usize..starts[shard + 1] as usize];
            if !run.is_empty() {
                self.write_shard(shard, run.iter().map(|&index| record(index as usize)));
            }
        }
    }

    fn write_shard<'a>(
        &self,
        shard: usize,
        records: impl Iterator<Item = (&'a TreePath, &'a [u8; 32], &'a [u8], LogPointer)>,
    ) {
        let mut inner = self.0[shard].lock().unwrap();
        let Inner { nodes, rlp, .. } = &mut *inner;
        for (path, hash, node_rlp, log_pointer) in records {
            nodes.push(WrittenNode {
                path: *path,
                hash: *hash,
                rlp_offset: rlp.len() as i32,
                rlp_length: node_rlp.len() as i32,
                log_pointer,
            });
            rlp.extend_from_slice(node_rlp);
        }
    }

    /// Replaces `buffer` with the RLP written at `path` when its hash is `hash`. Returns `false` otherwise.
    pub fn copy_to(&self, path: &TreePath, hash: &[u8; 32], buffer: &mut Vec<u8>) -> bool {
        let mut inner = self.0[self.shard(path)].lock().unwrap();
        inner.settle();
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
        self.0
            .iter()
            .map(|shard| {
                let mut inner = shard.lock().unwrap();
                inner.settle();
                inner.nodes.len()
            })
            .sum()
    }

    pub fn shard_count(&self) -> usize {
        self.0.len()
    }

    /// Removes every node, keeping the capacity.
    pub fn clear(&self) {
        for shard in self.0.iter() {
            let mut inner = shard.lock().unwrap();
            inner.nodes.clear();
            if inner.indexed > 0 {
                inner.index.clear();
                inner.indexed = 0;
            }
            inner.rlp.clear();
        }
    }

    /// Calls `view` with the nodes written to `shard` and the arena their RLP offsets point into.
    pub fn with_view<R>(&self, shard: usize, view: impl FnOnce(&[WrittenNode], &[u8]) -> R) -> R {
        let mut inner = self.0[shard].lock().unwrap();
        inner.settle();
        view(&inner.nodes, &inner.rlp)
    }

    /// Total capacity of the node records and arenas of every shard.
    fn capacity(&self) -> (usize, usize) {
        self.0.iter().fold((0, 0), |(nodes, arena), shard| {
            let inner = shard.lock().unwrap();
            (nodes + inner.nodes.capacity(), arena + inner.rlp.capacity())
        })
    }
}

/// Pooled size classes as (most nodes, structures kept, shards). A bulk set writes about [`NODES_PER_ENTRY`] nodes per
/// entry, and is sharded only when large enough to write and be published in parallel.
///
/// Worst case retained: 262,144 nodes × (88 B record + ~90 B index + at most [`MAX_ARENA_BYTES_PER_NODE`] arena),
/// about 115 MB.
const CLASSES: [(usize, usize, usize); 3] = [(64, 1024, 1), (1024, 64, 4), (16384, 8, 16)];
const NODES_PER_ENTRY: usize = 4;
const INITIAL_ARENA_BYTES_PER_NODE: usize = 128;
const MAX_ARENA_BYTES_PER_NODE: usize = 256;

static POOL: Mutex<[Vec<Box<PendingNodes>>; 3]> = Mutex::new([Vec::new(), Vec::new(), Vec::new()]);

/// Takes an empty [`PendingNodes`] sized for a bulk set of `estimated_entries`, from the pool when it has one.
pub fn rent(estimated_entries: usize) -> Box<PendingNodes> {
    let nodes = estimated_entries.saturating_mul(NODES_PER_ENTRY);
    match CLASSES
        .iter()
        .position(|&(max_nodes, _, _)| nodes <= max_nodes)
    {
        Some(class) => {
            let pooled = POOL.lock().unwrap()[class].pop();
            let (max_nodes, _, shards) = CLASSES[class];
            pooled.unwrap_or_else(|| Box::new(PendingNodes::with_capacity(max_nodes, shards)))
        }
        None => Box::new(PendingNodes::with_capacity(
            nodes,
            CLASSES[CLASSES.len() - 1].2,
        )),
    }
}

/// Clears `pending` and keeps it in the largest class its capacity serves, unless that class is full or has another
/// shard count, or it grew past the largest class.
pub fn give_back(pending: Box<PendingNodes>) {
    pending.clear();
    let (capacity, arena) = pending.capacity();
    let Some(class) = CLASSES
        .iter()
        .rposition(|&(max_nodes, _, _)| capacity >= max_nodes)
    else {
        return;
    };
    let (max_nodes, kept, shards) = CLASSES[class];
    if capacity > CLASSES[CLASSES.len() - 1].0
        || arena > max_nodes * MAX_ARENA_BYTES_PER_NODE
        || pending.shard_count() != shards
    {
        return;
    }
    let mut pool = POOL.lock().unwrap();
    if pool[class].len() < kept {
        pool[class].push(pending);
    }
}
