// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

//! Process-wide counters of bulk sets and trie node logs. Temporary logs are left out of the log memory and root lookups.

use std::sync::atomic::{AtomicU64, Ordering};

/// A snapshot of the counters, mirrored by `NativeMetrics` in C#.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct Metrics {
    /// Bytes of the generations allocated, live or still pinned.
    pub log_memory: u64,
    /// Generations allocated, live or still pinned.
    pub log_generations: u64,
    /// Bulk sets on a non-empty trie whose root was in the log.
    pub root_hits: u64,
    /// Bulk sets on a non-empty trie whose root was not in the log.
    pub root_misses: u64,
    /// Nodes read in place from the log.
    pub nodes_from_log: u64,
    /// Nodes read back from the pending nodes.
    pub nodes_from_pending: u64,
    /// Nodes fetched from the node store.
    pub nodes_fetched: u64,
    /// Bytes of what compaction kept of generations, still kept.
    pub compressed_memory: u64,
    /// Compacted generations of which something is still kept.
    pub compressed_generations: u64,
}

static LOG_MEMORY: AtomicU64 = AtomicU64::new(0);
static LOG_GENERATIONS: AtomicU64 = AtomicU64::new(0);
static ROOT_HITS: AtomicU64 = AtomicU64::new(0);
static ROOT_MISSES: AtomicU64 = AtomicU64::new(0);
static NODES_FROM_LOG: AtomicU64 = AtomicU64::new(0);
static NODES_FROM_PENDING: AtomicU64 = AtomicU64::new(0);
static NODES_FETCHED: AtomicU64 = AtomicU64::new(0);
static COMPRESSED_MEMORY: AtomicU64 = AtomicU64::new(0);
static COMPRESSED_GENERATIONS: AtomicU64 = AtomicU64::new(0);

pub fn read() -> Metrics {
    Metrics {
        log_memory: LOG_MEMORY.load(Ordering::Relaxed),
        log_generations: LOG_GENERATIONS.load(Ordering::Relaxed),
        root_hits: ROOT_HITS.load(Ordering::Relaxed),
        root_misses: ROOT_MISSES.load(Ordering::Relaxed),
        nodes_from_log: NODES_FROM_LOG.load(Ordering::Relaxed),
        nodes_from_pending: NODES_FROM_PENDING.load(Ordering::Relaxed),
        nodes_fetched: NODES_FETCHED.load(Ordering::Relaxed),
        compressed_memory: COMPRESSED_MEMORY.load(Ordering::Relaxed),
        compressed_generations: COMPRESSED_GENERATIONS.load(Ordering::Relaxed),
    }
}

pub fn generation_allocated(bytes: usize) {
    LOG_MEMORY.fetch_add(bytes as u64, Ordering::Relaxed);
    LOG_GENERATIONS.fetch_add(1, Ordering::Relaxed);
}

pub fn generation_freed(bytes: usize) {
    LOG_MEMORY.fetch_sub(bytes as u64, Ordering::Relaxed);
    LOG_GENERATIONS.fetch_sub(1, Ordering::Relaxed);
}

pub fn compressed_allocated(bytes: u64) {
    COMPRESSED_MEMORY.fetch_add(bytes, Ordering::Relaxed);
    COMPRESSED_GENERATIONS.fetch_add(1, Ordering::Relaxed);
}

pub fn compressed_freed(bytes: u64) {
    COMPRESSED_MEMORY.fetch_sub(bytes, Ordering::Relaxed);
    COMPRESSED_GENERATIONS.fetch_sub(1, Ordering::Relaxed);
}

pub fn root_looked_up(found: bool) {
    match found {
        true => ROOT_HITS.fetch_add(1, Ordering::Relaxed),
        false => ROOT_MISSES.fetch_add(1, Ordering::Relaxed),
    };
}

/// Node reads of one bulk set, counted apart so concurrent bulk sets do not contend, and added to the totals when dropped.
#[derive(Default)]
pub struct NodeReads {
    from_log: AtomicU64,
    from_pending: AtomicU64,
    fetched: AtomicU64,
}

impl NodeReads {
    pub fn from_log(&self) {
        self.from_log.fetch_add(1, Ordering::Relaxed);
    }

    pub fn from_pending(&self) {
        self.from_pending.fetch_add(1, Ordering::Relaxed);
    }

    pub fn fetched(&self) {
        self.fetched.fetch_add(1, Ordering::Relaxed);
    }
}

impl Drop for NodeReads {
    fn drop(&mut self) {
        NODES_FROM_LOG.fetch_add(*self.from_log.get_mut(), Ordering::Relaxed);
        NODES_FROM_PENDING.fetch_add(*self.from_pending.get_mut(), Ordering::Relaxed);
        NODES_FETCHED.fetch_add(*self.fetched.get_mut(), Ordering::Relaxed);
    }
}
