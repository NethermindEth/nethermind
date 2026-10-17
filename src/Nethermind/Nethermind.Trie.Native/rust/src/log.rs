// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

//! In-memory log of the decoded nodes bulk sets write, which later bulk sets read in place.

use std::alloc::{alloc, alloc_zeroed, dealloc, Layout};
use std::cell::RefCell;
use std::collections::HashMap;
use std::hash::{Hash, Hasher};
use std::sync::atomic::{AtomicPtr, AtomicU64, AtomicUsize, Ordering};
use std::sync::{Condvar, Mutex, OnceLock};

use rayon::{ThreadPool, ThreadPoolBuilder};
use rustc_hash::{FxBuildHasher, FxHasher};

use crate::metrics;
use crate::node::{
    record_length, record_length_of, write_record, LoggedNode, Node, TreePath, RECORD_HEADER_LENGTH,
};

/// Where a record is in a [`TrieNodeLog`]: its generation in the high 32 bits and its offset in that generation in the
/// low 32 bits. Zero is no record. Compaction keeps the pointers of the records it keeps.
pub type LogPointer = u64;

const OFFSET_BITS: u32 = 32;
const OFFSET_MASK: u64 = (1 << OFFSET_BITS) - 1;
const LIVE_GENERATIONS: u64 = 8;
/// Generations allocated at once, live, pinned or compacted, are capped by the size of the generation table.
const MAX_GENERATIONS: usize = 4096;
/// What generations start at, so as large as they grow under pressure.
const GENERATION_BYTES: usize = 64 << 20;
const MIN_GENERATION_BYTES: usize = 64 << 10;
/// What generations grow to under pressure, unless configured larger, so the generation table holds at least 256 GiB.
const MAX_GROWN_GENERATION_BYTES: usize = 64 << 20;
const RECORD_ALIGNMENT: usize = 8;
/// Generations are allocated at least cache line aligned, as the blocks of their bloom filter follow their records.
const ALLOCATION_ALIGNMENT: usize = 64;
const HUGE_PAGE_BYTES: usize = 2 << 20;
#[cfg(target_os = "linux")]
const MADV_HUGEPAGE: std::ffi::c_int = 14;

#[cfg(target_os = "linux")]
extern "C" {
    fn madvise(
        address: *mut std::ffi::c_void,
        length: usize,
        advice: std::ffi::c_int,
    ) -> std::ffi::c_int;
}

const BLOOM_BITS_PER_KEY: usize = 14;
/// Bytes a record is assumed to take, which sizes the bloom filter of a generation for the keys it can hold.
const BLOOM_BYTES_PER_KEY: usize = 128;
const BLOOM_BLOCK_BITS: usize = 512;
/// Those of the split block bloom filter of Parquet.
const BLOOM_SALTS: [u32; 8] = [
    0x47b6137b, 0x44974d91, 0x8824ad5b, 0xa2b7289d, 0x705495c7, 0x2df1424b, 0x9efc4947, 0x5c6bfb31,
];
const STATE_ROOT_SLOTS: usize = 1024;
const MEMORY_PER_STORAGE_ROOT_SLOT: u64 = 4096;
/// Kind of a root key record, after the kinds of node records.
const ROOT_KEY: u8 = 4;
/// kind: u8, zero: [u8; 7], address: [u8; 32], zero for the state trie, root hash: [u8; 32], pointer of the root's
/// record: u64, pointer of the previous root key record of the slot: u64.
const ROOT_KEY_LENGTH: usize = 88;

thread_local! {
    static TEMPORARY: RefCell<Option<Box<TrieNodeLog>>> = const { RefCell::new(None) };
}

/// Runs the compactions of every log, one at a time, off the threads of bulk sets.
static COMPACTOR: OnceLock<ThreadPool> = OnceLock::new();

/// Decoded nodes, each child reference holding the log pointer of its child, so a trie is walked through pointers once
/// its root is found. Only roots are indexed, the state roots by hash and the storage roots by address and hash, each
/// through a root key record logged next to the nodes.
///
/// Records are appended to the active generation. Once [`LIVE_GENERATIONS`] are live, starting a new one drops the
/// oldest, which is compacted in the background once no [`LogSession`] that could read its records in place is left.
/// Compaction keeps the last record of every key, see [`node_key`], unless the bloom filter of a later generation holds
/// the key, as that generation then likely holds a newer node at the same place, and keeps every root key record, so
/// lookups walk whole chains. The records kept are found by their pointer through a hash table. Compacted generations
/// are freed oldest first, once they take more than the compressed memory and no session pins them.
///
/// A session pins every generation live when it starts, and every compacted one, so a pointer is readable by it in place
/// exactly when its generation is not older than the session's floor, and otherwise through its compacted generation
/// when compaction kept it.
///
/// A generation [`TrieNodeLog::retain_active`] retained is not dropped, nor is any later one, until
/// [`TrieNodeLog::release`], so every record appended since stays readable by later sessions too, and the log then
/// holds more than its live generations.
///
/// Under pressure, while more than [`LIVE_GENERATIONS`] are allocated, each new generation is twice the size of the one
/// before, up to [`MAX_GROWN_GENERATION_BYTES`], so a bulk set larger than the log still fits in the generation table.
pub struct TrieNodeLog {
    generations: Box<[Generation]>,
    active: AtomicU64,
    state: Mutex<State>,
    /// Notified when compaction stops.
    compaction_idle: Condvar,
    generation_bytes: usize,
    compressed_max_memory: u64,
    temporary: bool,
    state_roots: RootTable,
    storage_roots: RootTable,
}

#[derive(Default)]
struct Generation {
    base: AtomicPtr<u8>,
    length: AtomicUsize,
    used: AtomicUsize,
    /// Blocks of the bloom filter of the keys of its records, which follow them in its allocation.
    bloom_blocks: AtomicUsize,
    /// What compaction kept of it, once its allocation is freed.
    compressed: AtomicPtr<CompressedGeneration>,
}

struct State {
    oldest_compressed: u64,
    oldest_allocated: u64,
    oldest_live: u64,
    /// Sessions by floor, indexed like the generation table.
    pins: Box<[u32]>,
    /// Sessions by compressed floor, indexed like the generation table.
    compressed_pins: Box<[u32]>,
    /// Retains by generation, indexed like the generation table.
    retains: Box<[u32]>,
    compressed_memory: u64,
    compacting: bool,
}

impl TrieNodeLog {
    /// Creates a log whose compacted generations take about `compressed_max_memory` bytes, exceeded only while sessions
    /// pin them. Boxed, as compaction reads it in the background, so it must not be moved out.
    pub fn new(compressed_max_memory: u64) -> Box<TrieNodeLog> {
        let storage_root_slots =
            (compressed_max_memory / MEMORY_PER_STORAGE_ROOT_SLOT).max(STATE_ROOT_SLOTS as u64);
        TrieNodeLog::create(
            GENERATION_BYTES,
            compressed_max_memory,
            false,
            RootTable::new(STATE_ROOT_SLOTS),
            RootTable::new((storage_root_slots as usize).next_power_of_two()),
        )
    }

    /// A log of the smallest generations, which keeps nothing compacted.
    #[cfg(test)]
    pub fn minimal() -> Box<TrieNodeLog> {
        TrieNodeLog::create(
            MIN_GENERATION_BYTES,
            0,
            false,
            RootTable::new(STATE_ROOT_SLOTS),
            RootTable::new(STATE_ROOT_SLOTS),
        )
    }

    /// Calls `use_log` with a log for a single bulk set, which never drops a generation, so is always under pressure,
    /// and indexes no root. The thread reuses it afterwards, emptied.
    pub fn with_temporary<R>(use_log: impl FnOnce(&TrieNodeLog) -> R) -> R {
        let mut log = TEMPORARY.take().unwrap_or_else(TrieNodeLog::temporary);
        let result = use_log(&log);
        log.empty_temporary();
        TEMPORARY.set(Some(log));
        result
    }

    fn temporary() -> Box<TrieNodeLog> {
        TrieNodeLog::create(
            MIN_GENERATION_BYTES,
            0,
            true,
            RootTable::new(0),
            RootTable::new(0),
        )
    }

    fn create(
        generation_bytes: usize,
        compressed_max_memory: u64,
        temporary: bool,
        state_roots: RootTable,
        storage_roots: RootTable,
    ) -> Box<TrieNodeLog> {
        let log = Box::new(TrieNodeLog {
            generations: (0..MAX_GENERATIONS)
                .map(|_| Generation::default())
                .collect(),
            active: AtomicU64::new(1),
            state: Mutex::new(State {
                oldest_compressed: 1,
                oldest_allocated: 1,
                oldest_live: 1,
                pins: vec![0; MAX_GENERATIONS].into_boxed_slice(),
                compressed_pins: vec![0; MAX_GENERATIONS].into_boxed_slice(),
                retains: vec![0; MAX_GENERATIONS].into_boxed_slice(),
                compressed_memory: 0,
                compacting: false,
            }),
            compaction_idle: Condvar::new(),
            generation_bytes,
            compressed_max_memory,
            temporary,
            state_roots,
            storage_roots,
        });
        log.generation(1).start(generation_bytes, temporary);
        log
    }

    /// Drops every record of a temporary log, which keeps its first generation and nothing else.
    fn empty_temporary(&mut self) {
        let active = *self.active.get_mut();
        for id in 2..=active {
            self.generation(id).free(true);
        }
        *self.generations[slot(1)].used.get_mut() = 0;
        *self.active.get_mut() = 1;
    }

    /// Pins every live and compacted generation until the returned session is dropped.
    pub fn pin(&self) -> LogSession<'_> {
        let mut state = self.state.lock().unwrap();
        let floor = state.oldest_live;
        let compressed_floor = state.oldest_compressed;
        state.pins[slot(floor)] += 1;
        state.compressed_pins[slot(compressed_floor)] += 1;
        LogSession {
            log: self,
            floor,
            compressed_floor,
        }
    }

    /// Retains the active generation until [`TrieNodeLog::release`] and returns it.
    pub fn retain_active(&self) -> u64 {
        let mut state = self.state.lock().unwrap();
        let active = self.active.load(Ordering::Acquire);
        state.retains[slot(active)] += 1;
        active
    }

    /// Retains `generation` once more until [`TrieNodeLog::release`]. It must still be live, as it is while retained.
    pub fn retain(&self, generation: u64) {
        let mut state = self.state.lock().unwrap();
        debug_assert!(generation >= state.oldest_live);
        state.retains[slot(generation)] += 1;
    }

    /// Releases a retain of `generation`.
    pub fn release(&self, generation: u64) {
        let mut state = self.state.lock().unwrap();
        state.retains[slot(generation)] -= 1;
        self.drop_old_generations(&mut state);
        self.reclaim(&mut state);
    }

    fn generation(&self, id: u64) -> &Generation {
        &self.generations[slot(id)]
    }

    /// The node record at `pointer`, whose generation must be retained, as an in-memory snapshot's pin does.
    pub fn node(&self, pointer: LogPointer) -> LoggedNode<'_> {
        let length = record_length_of(self.bytes(pointer, RECORD_HEADER_LENGTH));
        LoggedNode::new(self.bytes(pointer, length))
    }

    /// The `length` bytes at `pointer`, whose generation must not be freed.
    fn bytes(&self, pointer: LogPointer, length: usize) -> &[u8] {
        let base = self
            .generation(pointer >> OFFSET_BITS)
            .base
            .load(Ordering::Acquire);
        unsafe { std::slice::from_raw_parts(base.add((pointer & OFFSET_MASK) as usize), length) }
    }

    fn roots(&self, address: Option<&[u8; 32]>) -> &RootTable {
        match address {
            None => &self.state_roots,
            Some(_) => &self.storage_roots,
        }
    }

    /// Starts the generation after `full`, with room for a record of `length`, unless another thread already did.
    fn rotate(&self, full: u64, length: usize) {
        let mut state = self.state.lock().unwrap();
        if self.active.load(Ordering::Acquire) != full {
            return;
        }

        self.reclaim(&mut state);
        let under_pressure = self.temporary || state.oldest_allocated + LIVE_GENERATIONS <= full;
        let generation_length = match under_pressure {
            true => (self.generation(full).length.load(Ordering::Relaxed) * 2).clamp(
                self.generation_bytes,
                self.generation_bytes.max(MAX_GROWN_GENERATION_BYTES),
            ),
            false => self.generation_bytes,
        }
        .max(length);
        let id = full + 1;
        // The compacted generation the new one replaces in the table is freed first, as compaction keeps none newer.
        self.evict_compressed(&mut state, (id + 1).saturating_sub(MAX_GENERATIONS as u64));
        let generation = self.generation(id);
        assert!(
            generation.base.load(Ordering::Relaxed).is_null()
                && generation.compressed.load(Ordering::Relaxed).is_null(),
            "too many trie node log generations pinned"
        );
        generation.start(generation_length, self.temporary);
        self.active.store(id, Ordering::Release);
        if !self.temporary {
            self.drop_old_generations(&mut state);
        }
        self.reclaim(&mut state);
    }

    /// Drops generations, oldest first, until [`LIVE_GENERATIONS`] are live or the oldest live one is retained.
    fn drop_old_generations(&self, state: &mut State) {
        let active = self.active.load(Ordering::Acquire);
        let oldest_to_keep = active + 1 - LIVE_GENERATIONS.min(active);
        while state.oldest_live < oldest_to_keep && state.retains[slot(state.oldest_live)] == 0 {
            state.oldest_live += 1;
        }
    }

    /// Frees compacted generations over the compressed memory, and starts compacting dropped ones no longer pinned.
    fn reclaim(&self, state: &mut State) {
        self.evict_compressed(state, 0);
        if state.compacting || !Self::compactable(state) {
            return;
        }
        state.compacting = true;
        let log = LogRef(self);
        COMPACTOR
            .get_or_init(|| {
                ThreadPoolBuilder::new()
                    .num_threads(1)
                    .build()
                    .expect("trie node log compactor")
            })
            .spawn(move || log.get().compact_dropped());
    }

    /// Whether the oldest allocated generation is dropped and no session reads it in place, so none writes it either.
    fn compactable(state: &State) -> bool {
        state.oldest_allocated < state.oldest_live && state.pins[slot(state.oldest_allocated)] == 0
    }

    /// Compacts the generations [`TrieNodeLog::compactable`], oldest first, freeing their allocation.
    fn compact_dropped(&self) {
        loop {
            let (id, newest) = {
                let mut state = self.state.lock().unwrap();
                if !Self::compactable(&state) {
                    state.compacting = false;
                    self.compaction_idle.notify_all();
                    return;
                }
                (state.oldest_allocated, self.active.load(Ordering::Acquire))
            };
            let compressed = self.compress(id, newest);
            let mut state = self.state.lock().unwrap();
            let generation = self.generation(id);
            if let Some(compressed) = compressed {
                let memory = compressed.memory();
                metrics::compressed_allocated(memory);
                state.compressed_memory += memory;
                generation
                    .compressed
                    .store(Box::into_raw(compressed), Ordering::Release);
            }
            generation.free(false);
            state.oldest_allocated += 1;
            self.evict_compressed(&mut state, 0);
        }
    }

    /// What compaction keeps of generation `id`: the last record of every key that no bloom filter of the generations
    /// after it, up to `newest`, holds, and every root key record. `None` when nothing is kept.
    fn compress(&self, id: u64, newest: u64) -> Option<Box<CompressedGeneration>> {
        let generation = self.generation(id);
        let records = unsafe {
            std::slice::from_raw_parts(
                generation.base.load(Ordering::Acquire),
                // Reservations that did not fit leave `used` past the end.
                generation
                    .used
                    .load(Ordering::Relaxed)
                    .min(generation.length.load(Ordering::Relaxed)),
            )
        };
        let mut kept = Vec::new();
        let mut last = HashMap::<u64, u32, FxBuildHasher>::default();
        let mut offset = 0;
        while offset + RECORD_HEADER_LENGTH <= records.len() {
            let length = entry_length(&records[offset..]);
            match records[offset] {
                ROOT_KEY => kept.push(offset as u32),
                // Past the last record, the generation is zeroed, so reads as a record without RLP.
                _ if length == RECORD_HEADER_LENGTH => break,
                _ => {
                    let key = LoggedNode::new(&records[offset..offset + length]).log_key();
                    last.insert(key, offset as u32);
                }
            }
            offset += length.next_multiple_of(RECORD_ALIGNMENT);
        }

        let mut candidates: Vec<(u64, u32)> = last.into_iter().collect();
        for later in id + 1..=newest {
            let bloom = self.generation(later).bloom();
            candidates.retain(|&(key, _)| !bloom.contains(key));
        }
        kept.extend(candidates.into_iter().map(|(_, offset)| offset));
        if kept.is_empty() {
            return None;
        }
        // In log order, as a trie's records were appended near each other.
        kept.sort_unstable();

        let mut compressed = CompressedGeneration {
            records: Vec::new(),
            table: vec![(0, 0); (kept.len() * 4 / 3 + 1).next_power_of_two()].into_boxed_slice(),
        };
        for offset in kept {
            let record = &records[offset as usize..];
            compressed.insert(
                id << OFFSET_BITS | offset as u64,
                compressed.records.len() as u64,
            );
            compressed
                .records
                .extend_from_slice(&record[..entry_length(record)]);
        }
        compressed.records.shrink_to_fit();
        Some(Box::new(compressed))
    }

    /// Frees compacted generations, oldest first, up to the first one still pinned, while they take more than the
    /// compressed memory, are older than `before`, or nothing of them was kept.
    fn evict_compressed(&self, state: &mut State, before: u64) {
        while state.oldest_compressed < state.oldest_allocated
            && (state.compressed_memory > self.compressed_max_memory
                || state.oldest_compressed < before
                || self
                    .generation(state.oldest_compressed)
                    .compressed
                    .load(Ordering::Relaxed)
                    .is_null())
            && state.compressed_pins[slot(state.oldest_compressed)] == 0
        {
            state.compressed_memory -= self.generation(state.oldest_compressed).free_compressed();
            state.oldest_compressed += 1;
        }
    }
}

impl Drop for TrieNodeLog {
    fn drop(&mut self) {
        let mut state = self.state.lock().unwrap();
        while state.compacting {
            state = self.compaction_idle.wait(state).unwrap();
        }
        drop(state);
        for generation in self.generations.iter() {
            generation.free(self.temporary);
            generation.free_compressed();
        }
    }
}

/// A log compaction reads in the background, which the log waits for before it is dropped.
struct LogRef(*const TrieNodeLog);

unsafe impl Send for LogRef {}

impl LogRef {
    fn get(&self) -> &TrieNodeLog {
        unsafe { &*self.0 }
    }
}

impl Generation {
    /// Allocates `length` bytes of records, followed by a bloom filter sized for them unless `temporary`, which is
    /// never compacted.
    fn start(&self, length: usize, temporary: bool) {
        let bloom_blocks = match temporary {
            true => 0,
            false => (length / BLOOM_BYTES_PER_KEY * BLOOM_BITS_PER_KEY).div_ceil(BLOOM_BLOCK_BITS),
        };
        let size = allocation_size(length, bloom_blocks);
        if !temporary {
            metrics::generation_allocated(size);
        }
        self.length.store(length, Ordering::Relaxed);
        self.bloom_blocks.store(bloom_blocks, Ordering::Relaxed);
        self.used.store(0, Ordering::Relaxed);
        self.base.store(allocate(size), Ordering::Release);
    }

    fn free(&self, temporary: bool) {
        let base = self.base.swap(std::ptr::null_mut(), Ordering::Relaxed);
        if !base.is_null() {
            let size = allocation_size(
                self.length.load(Ordering::Relaxed),
                self.bloom_blocks.load(Ordering::Relaxed),
            );
            if !temporary {
                metrics::generation_freed(size);
            }
            free(base, size);
        }
    }

    /// Frees what compaction kept of the generation and returns the bytes it took.
    fn free_compressed(&self) -> u64 {
        let compressed = self
            .compressed
            .swap(std::ptr::null_mut(), Ordering::Relaxed);
        if compressed.is_null() {
            return 0;
        }
        let memory = unsafe { Box::from_raw(compressed) }.memory();
        metrics::compressed_freed(memory);
        memory
    }

    /// The bloom filter of an allocated generation.
    fn bloom(&self) -> Bloom<'_> {
        let base = self.base.load(Ordering::Acquire);
        let offset = self
            .length
            .load(Ordering::Relaxed)
            .next_multiple_of(ALLOCATION_ALIGNMENT);
        Bloom(unsafe {
            std::slice::from_raw_parts(
                base.add(offset) as *const [AtomicU64; 8],
                self.bloom_blocks.load(Ordering::Relaxed),
            )
        })
    }
}

fn allocation_size(length: usize, bloom_blocks: usize) -> usize {
    length.next_multiple_of(ALLOCATION_ALIGNMENT) + bloom_blocks * BLOOM_BLOCK_BITS / 8
}

fn slot(generation: u64) -> usize {
    generation as usize % MAX_GENERATIONS
}

fn allocation_layout(length: usize) -> Layout {
    let alignment = if cfg!(target_os = "linux") && length >= HUGE_PAGE_BYTES {
        HUGE_PAGE_BYTES
    } else {
        ALLOCATION_ALIGNMENT
    };
    Layout::from_size_align(length, alignment).unwrap()
}

/// Zeroed, so every record slice covers initialized memory and the bloom filter starts empty.
fn allocate(length: usize) -> *mut u8 {
    let layout = allocation_layout(length);
    let buffer = unsafe {
        if layout.align() == HUGE_PAGE_BYTES {
            alloc(layout)
        } else {
            alloc_zeroed(layout)
        }
    };
    if buffer.is_null() {
        std::alloc::handle_alloc_error(layout);
    }
    #[cfg(target_os = "linux")]
    if layout.align() == HUGE_PAGE_BYTES {
        unsafe {
            // Advise before zeroing faults pages in, and exclude the tail so madvise cannot round past the allocation.
            let _ = madvise(
                buffer.cast(),
                length / HUGE_PAGE_BYTES * HUGE_PAGE_BYTES,
                MADV_HUGEPAGE,
            );
            buffer.write_bytes(0, length);
        }
    }
    buffer
}

fn free(buffer: *mut u8, length: usize) {
    unsafe { dealloc(buffer, allocation_layout(length)) }
}

/// Length of the node or root key record starting `bytes`.
fn entry_length(bytes: &[u8]) -> usize {
    match bytes[0] {
        ROOT_KEY => ROOT_KEY_LENGTH,
        _ => record_length_of(bytes),
    }
}

/// The key of the node at `path` in the trie of `address`, the state trie when `None`, of which compaction keeps only
/// the last record.
pub fn node_key(address: Option<&[u8; 32]>, path: &TreePath) -> u64 {
    let mut hasher = FxHasher::default();
    address.copied().unwrap_or_default().hash(&mut hasher);
    path.path.hash(&mut hasher);
    path.length.hash(&mut hasher);
    // The finalizer of MurmurHash3, as bloom filters take their bits from every part of the key.
    let mut key = hasher.finish();
    key ^= key >> 33;
    key = key.wrapping_mul(0xff51afd7ed558ccd);
    key ^= key >> 33;
    key = key.wrapping_mul(0xc4ceb9fe1a85ec53);
    key ^ key >> 33
}

/// A split block bloom filter: a key sets one bit in each word of the block its high half picks.
struct Bloom<'a>(&'a [[AtomicU64; 8]]);

impl Bloom<'_> {
    fn insert(&self, key: u64) {
        if let Some(block) = self.block(key) {
            for (word, mask) in block.iter().zip(bloom_masks(key)) {
                word.fetch_or(mask, Ordering::Relaxed);
            }
        }
    }

    fn contains(&self, key: u64) -> bool {
        self.block(key).is_some_and(|block| {
            block
                .iter()
                .zip(bloom_masks(key))
                .all(|(word, mask)| word.load(Ordering::Relaxed) & mask == mask)
        })
    }

    fn block(&self, key: u64) -> Option<&[AtomicU64; 8]> {
        let blocks = self.0.len() as u64;
        (blocks != 0).then(|| &self.0[(((key >> 32) * blocks) >> 32) as usize])
    }
}

fn bloom_masks(key: u64) -> [u64; 8] {
    BLOOM_SALTS.map(|salt| 1 << ((key as u32).wrapping_mul(salt) >> 26))
}

/// The records compaction kept of a generation, found by their pointer.
struct CompressedGeneration {
    records: Vec<u8>,
    /// (pointer, offset in `records`) of every record kept, by open addressing with linear probing, zero pointers free.
    table: Box<[(LogPointer, u64)]>,
}

impl CompressedGeneration {
    fn insert(&mut self, pointer: LogPointer, offset: u64) {
        let mut index = self.index(pointer);
        while self.table[index].0 != 0 {
            index = (index + 1) & (self.table.len() - 1);
        }
        self.table[index] = (pointer, offset);
    }

    /// Where the record kept at `pointer` starts, if kept.
    fn find(&self, pointer: LogPointer) -> Option<*const u8> {
        let mut index = self.index(pointer);
        loop {
            match self.table[index] {
                (0, _) => return None,
                (key, offset) if key == pointer => {
                    return Some(self.records[offset as usize..].as_ptr())
                }
                _ => index = (index + 1) & (self.table.len() - 1),
            }
        }
    }

    fn index(&self, pointer: LogPointer) -> usize {
        (pointer.wrapping_mul(0x9e3779b97f4a7c15) >> 32) as usize & (self.table.len() - 1)
    }

    fn memory(&self) -> u64 {
        (self.records.capacity() + std::mem::size_of_val(&*self.table)) as u64
    }
}

/// A pin on a [`TrieNodeLog`], through which one bulk set reads and appends records.
pub struct LogSession<'a> {
    log: &'a TrieNodeLog,
    floor: u64,
    compressed_floor: u64,
}

impl LogSession<'_> {
    /// The node record at `pointer`, or `None` when this session cannot read it: zero, dropped before it started and
    /// not kept by compaction, or freed.
    pub fn record(&self, pointer: LogPointer) -> Option<&[u8]> {
        let start = self.locate(pointer)?;
        unsafe {
            let length = record_length_of(std::slice::from_raw_parts(start, RECORD_HEADER_LENGTH));
            Some(std::slice::from_raw_parts(start, length))
        }
    }

    /// Where the record at `pointer` starts, if this session can read it, in its generation while not older than the
    /// floor, and otherwise in what compaction kept of it.
    fn locate(&self, pointer: LogPointer) -> Option<*const u8> {
        let id = pointer >> OFFSET_BITS;
        if id >= self.floor {
            return Some(self.log.bytes(pointer, 0).as_ptr());
        }
        if id < self.compressed_floor {
            return None;
        }
        // Null while compaction has not kept anything of it, and never another generation's, as the session pins it.
        let compressed = self.log.generation(id).compressed.load(Ordering::Acquire);
        unsafe { compressed.as_ref() }?.find(pointer)
    }

    /// Appends the record of `node`, whose children must all be resolved, under key zero and returns its pointer.
    #[cfg(test)]
    pub fn append(&self, node: &Node) -> LogPointer {
        self.append_keyed(node, 0)
    }

    /// Appends the record of `node`, whose children must all be resolved, under `key` and returns its pointer.
    #[cfg(test)]
    pub fn append_keyed(&self, node: &Node, key: u64) -> LogPointer {
        let mut rlp = Vec::new();
        crate::node::encode(node, &mut rlp);
        let hash = crate::keccak::keccak(&rlp);
        let mut pointers = Vec::new();
        self.append_all(
            std::iter::once((node, key, &hash, rlp.as_slice())),
            &mut pointers,
        );
        pointers[0]
    }

    /// Appends the records of `nodes`, each a node whose children must all be resolved with its key, see [`node_key`],
    /// hash and RLP, in one reservation, and pushes their pointers to `pointers`.
    pub fn append_all<'n>(
        &self,
        nodes: impl Iterator<Item = (&'n Node, u64, &'n [u8; 32], &'n [u8])> + Clone,
        pointers: &mut Vec<LogPointer>,
    ) {
        let first = pointers.len();
        // Each record's length, until it becomes its pointer.
        pointers.extend(
            nodes
                .clone()
                .map(|(node, _, _, rlp)| record_length(node, rlp.len()) as LogPointer),
        );
        let total = pointers[first..]
            .iter()
            .map(|&length| (length as usize).next_multiple_of(RECORD_ALIGNMENT))
            .sum();
        if total == 0 {
            return;
        }
        let base = self.append_bytes(total, |out| {
            let mut offset = 0;
            for ((node, key, hash, rlp), pointer) in nodes.clone().zip(&mut pointers[first..]) {
                let length = *pointer as usize;
                write_record(node, key, hash, rlp, &mut out[offset..offset + length]);
                *pointer = offset as LogPointer;
                offset += length.next_multiple_of(RECORD_ALIGNMENT);
            }
        });
        for pointer in &mut pointers[first..] {
            *pointer += base;
        }
        let bloom = self.log.generation(base >> OFFSET_BITS).bloom();
        for (_, key, _, _) in nodes {
            bloom.insert(key);
        }
    }

    /// Appends `length` bytes that `write` fills and returns their pointer.
    fn append_bytes(&self, length: usize, write: impl FnOnce(&mut [u8])) -> LogPointer {
        let aligned = length.next_multiple_of(RECORD_ALIGNMENT);
        loop {
            let id = self.log.active.load(Ordering::Acquire);
            let generation = self.log.generation(id);
            let offset = generation.used.fetch_add(aligned, Ordering::Relaxed);
            if offset + length <= generation.length.load(Ordering::Relaxed) {
                // The range is this thread's alone, and the generation outlives the session as it is not older than
                // the floor.
                let out = unsafe {
                    std::slice::from_raw_parts_mut(
                        generation.base.load(Ordering::Relaxed).add(offset),
                        length,
                    )
                };
                write(out);
                return id << OFFSET_BITS | offset as u64;
            }
            self.log.rotate(id, length);
        }
    }

    /// The pointer of the root `root` of the trie of `address`, the state trie when `None`, or zero when not found.
    pub fn root(&self, address: Option<&[u8; 32]>, root: &[u8; 32]) -> LogPointer {
        let pointer = self.find_root(address, root);
        if !self.log.temporary {
            metrics::root_looked_up(pointer != 0);
        }
        pointer
    }

    /// Walks the root key records of the slot, newest first, up to the first this session cannot read. A chain never
    /// links a record to one of a newer generation, and compaction keeps every root key record, so the records left are
    /// all freed, or dropped and not compacted yet.
    pub(crate) fn find_root(&self, address: Option<&[u8; 32]>, root: &[u8; 32]) -> LogPointer {
        let Some(slot) = self.log.roots(address).slot(address, root) else {
            return 0;
        };
        let address = address.copied().unwrap_or_default();
        let mut key = slot.load(Ordering::Acquire);
        while let Some(start) = self.locate(key) {
            let record = unsafe { std::slice::from_raw_parts(start, ROOT_KEY_LENGTH) };
            if record[8..40] == address && record[40..72] == *root {
                let pointer = u64::from_le_bytes(record[72..80].try_into().unwrap());
                return match self.locate(pointer) {
                    Some(_) => pointer,
                    None => 0,
                };
            }
            key = u64::from_le_bytes(record[80..].try_into().unwrap());
        }
        0
    }

    /// Indexes `pointer` as the root `root` of the trie of `address`, the state trie when `None`.
    pub fn set_root(&self, address: Option<&[u8; 32]>, root: &[u8; 32], pointer: LogPointer) {
        let Some(slot) = self.log.roots(address).slot(address, root) else {
            return;
        };
        let mut previous = slot.load(Ordering::Acquire);
        loop {
            let key = self.append_bytes(ROOT_KEY_LENGTH, |out| {
                out[0] = ROOT_KEY;
                out[1..8].fill(0);
                out[8..40].copy_from_slice(&address.copied().unwrap_or_default());
                out[40..72].copy_from_slice(root);
                out[72..80].copy_from_slice(&pointer.to_le_bytes());
                out[80..].copy_from_slice(&previous.to_le_bytes());
            });
            // On a race the record is appended again rather than relinked, as the winner may be of a newer generation.
            match slot.compare_exchange(previous, key, Ordering::Release, Ordering::Acquire) {
                Ok(_) => return,
                Err(current) => previous = current,
            }
        }
    }
}

impl Drop for LogSession<'_> {
    fn drop(&mut self) {
        let mut state = self.log.state.lock().unwrap();
        state.pins[slot(self.floor)] -= 1;
        state.compressed_pins[slot(self.compressed_floor)] -= 1;
        self.log.reclaim(&mut state);
    }
}

/// Root key records by a hash of the address and root hash they hold. A slot points to the newest record, each record
/// to the one it replaced, so colliding roots are all kept, and lookups check the records, so a slot needs no lock.
struct RootTable {
    slots: Box<[AtomicU64]>,
}

impl RootTable {
    /// Creates a table of `count` slots, a power of two, or one that indexes nothing when zero.
    fn new(count: usize) -> RootTable {
        RootTable {
            slots: (0..count).map(|_| AtomicU64::new(0)).collect(),
        }
    }

    fn slot(&self, address: Option<&[u8; 32]>, root: &[u8; 32]) -> Option<&AtomicU64> {
        if self.slots.is_empty() {
            return None;
        }
        let mut hasher = FxHasher::default();
        address.copied().unwrap_or_default().hash(&mut hasher);
        root.hash(&mut hasher);
        Some(&self.slots[hasher.finish() as usize & (self.slots.len() - 1)])
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::node::{decode_rlp, empty_children, encode, ChildRef, LoggedNode, Nibbles};

    fn rlp(node: &Node) -> Vec<u8> {
        let mut out = Vec::new();
        encode(node, &mut out);
        out
    }

    fn leaf(value: Vec<u8>) -> Node {
        Node::Leaf {
            key: Nibbles::from_slice(&[1, 2, 3]),
            value,
        }
    }

    fn active_length(log: &TrieNodeLog) -> usize {
        log.generation(log.active.load(Ordering::Relaxed))
            .length
            .load(Ordering::Relaxed)
    }

    fn read(session: &LogSession, pointer: LogPointer) -> Vec<u8> {
        rlp(&LoggedNode::new(session.record(pointer).unwrap()).to_owned(0))
    }

    fn wait_for_compaction(log: &TrieNodeLog) {
        let mut state = log.state.lock().unwrap();
        while state.compacting {
            state = log.compaction_idle.wait(state).unwrap();
        }
    }

    #[test]
    fn allocations_are_aligned_and_zeroed() {
        for length in [
            MIN_GENERATION_BYTES,
            HUGE_PAGE_BYTES - 1,
            HUGE_PAGE_BYTES,
            HUGE_PAGE_BYTES + 1,
        ] {
            let alignment = if cfg!(target_os = "linux") && length >= HUGE_PAGE_BYTES {
                HUGE_PAGE_BYTES
            } else {
                ALLOCATION_ALIGNMENT
            };
            assert_eq!(allocation_layout(length).align(), alignment);
            assert_eq!(allocation_layout(length).size(), length);
            let buffer = allocate(length);
            assert_eq!(buffer as usize % alignment, 0);
            assert!(unsafe { std::slice::from_raw_parts(buffer, length) }
                .iter()
                .all(|&byte| byte == 0));
            free(buffer, length);
        }
    }

    #[test]
    fn appends_reads_in_place_truncates_and_indexes_roots() {
        let log = TrieNodeLog::minimal();
        let session = log.pin();

        let inline_leaf = decode_rlp(&[0xc4, 0x82, 0x20, 0x01, 0x05]).unwrap();
        let leaf_pointer = session.append(&leaf(vec![0xaa; 40]));
        let extension = Node::Extension {
            key: Nibbles::from_slice(&[4, 5]),
            child: ChildRef::Hash {
                hash: [0x11; 32],
                pointer: leaf_pointer,
            },
        };
        let mut children = empty_children();
        children[3] = ChildRef::Hash {
            hash: [0x22; 32],
            pointer: leaf_pointer,
        };
        children[12] = ChildRef::Hash {
            hash: [0x33; 32],
            pointer: 0,
        };
        let hashed_branch = Node::Branch {
            children: children.clone(),
        };
        children[9] = ChildRef::inline(&rlp(&inline_leaf));
        let inline_branch = Node::Branch { children };
        let pointers: Vec<LogPointer> = [
            leaf(vec![0xaa; 40]),
            extension,
            hashed_branch,
            inline_branch,
        ]
        .iter()
        .map(|node| {
            let pointer = session.append(node);
            assert_eq!(read(&session, pointer), rlp(node));
            // The record holds the node's RLP and hash, readable without a session while its generation is live.
            let logged = log.node(pointer);
            assert_eq!(logged.rlp(), rlp(node));
            assert_eq!(*logged.hash(), crate::keccak::keccak(&rlp(node)));
            pointer
        })
        .collect();
        // A branch with an inline child is of its own kind, found by parsing its children in order.
        assert_eq!(
            pointers[2..]
                .iter()
                .map(|&pointer| session.record(pointer).unwrap()[0])
                .collect::<Vec<_>>(),
            [2, 3]
        );
        let extension = LoggedNode::new(session.record(pointers[1]).unwrap());
        assert!(
            matches!(extension.child(0), ChildRef::Hash { pointer, .. } if pointer == leaf_pointer)
        );
        for &pointer in &pointers[2..] {
            let branch = LoggedNode::new(session.record(pointer).unwrap());
            assert!(
                matches!(branch.child(3), ChildRef::Hash { pointer, .. } if pointer == leaf_pointer)
            );
            assert!(matches!(branch.child(4), ChildRef::Empty));
            assert!(
                matches!(branch.child(12), ChildRef::Hash { hash, pointer: 0 } if hash == [0x33; 32])
            );
        }
        assert!(
            matches!(LoggedNode::new(session.record(pointers[3]).unwrap()).child(9), ChildRef::Inline(inline) if inline.as_slice() == rlp(&inline_leaf))
        );

        // A record larger than a generation gets one of its own.
        let large = leaf(vec![0xbb; MIN_GENERATION_BYTES * 2]);
        assert_eq!(read(&session, session.append(&large)), rlp(&large));

        // Filling more generations than are live drops the first, but not for the session pinned before, and generations
        // grow while it pins the dropped ones.
        let filler = leaf(vec![0xcc; 1000]);
        while log.active.load(Ordering::Relaxed) <= LIVE_GENERATIONS + 2 {
            session.append(&filler);
        }
        assert!(active_length(&log) > MIN_GENERATION_BYTES);
        assert!(log.pin().record(leaf_pointer).is_none());
        assert_eq!(read(&session, leaf_pointer), rlp(&leaf(vec![0xaa; 40])));
        assert_eq!(log.state.lock().unwrap().oldest_allocated, 1);
        // Compacted once the session is gone, then freed, as the log keeps no compacted generation.
        drop(session);
        wait_for_compaction(&log);
        let state = log.state.lock().unwrap();
        assert!(state.oldest_allocated > 1 && state.oldest_allocated == state.oldest_live);
        assert_eq!(state.oldest_compressed, state.oldest_allocated);
        drop(state);
        let session = log.pin();
        let active = log.active.load(Ordering::Relaxed);
        while log.active.load(Ordering::Relaxed) == active {
            session.append(&filler);
        }
        assert_eq!(active_length(&log), MIN_GENERATION_BYTES);

        // Roots hit on the same trie and hash only, and colliding roots are all kept.
        let pointer = session.append(&filler);
        let address = [0x33; 32];
        session.set_root(None, &[0x44; 32], pointer);
        session.set_root(Some(&address), &[0x55; 32], pointer);
        assert_eq!(session.root(None, &[0x44; 32]), pointer);
        assert_eq!(session.root(Some(&address), &[0x55; 32]), pointer);
        assert_eq!(session.root(Some(&address), &[0x44; 32]), 0);
        assert_eq!(session.root(None, &[0x55; 32]), 0);
        assert_eq!(session.root(Some(&[0x66; 32]), &[0x55; 32]), 0);
        let single = TrieNodeLog::create(
            MIN_GENERATION_BYTES,
            0,
            false,
            RootTable::new(1),
            RootTable::new(1),
        );
        let single_session = single.pin();
        let single_pointer = single_session.append(&filler);
        single_session.set_root(Some(&address), &[0x44; 32], single_pointer);
        single_session.set_root(Some(&[0x66; 32]), &[0x44; 32], single_pointer);
        assert_eq!(
            single_session.root(Some(&address), &[0x44; 32]),
            single_pointer
        );
        assert_eq!(
            single_session.root(Some(&[0x66; 32]), &[0x44; 32]),
            single_pointer
        );

        // A lookup stops at the first root key record of a dropped generation.
        while single.active.load(Ordering::Relaxed) <= LIVE_GENERATIONS + 1 {
            single_session.append(&filler);
        }
        let newest_pointer = single_session.append(&filler);
        single_session.set_root(Some(&[0x77; 32]), &[0x44; 32], newest_pointer);
        drop(single_session);
        wait_for_compaction(&single);
        let single_session = single.pin();
        assert_eq!(single_session.root(Some(&address), &[0x44; 32]), 0);
        assert_eq!(
            single_session.root(Some(&[0x77; 32]), &[0x44; 32]),
            newest_pointer
        );

        // A retained generation, and every later one, stays live for later sessions until released, with generations
        // growing meanwhile, and a second retain keeps it once the first is released.
        drop(session);
        let retained = log.retain_active();
        let session = log.pin();
        let retained_pointer = session.append(&filler);
        while log.active.load(Ordering::Relaxed) <= retained + LIVE_GENERATIONS {
            session.append(&filler);
        }
        drop(session);
        assert!(log.pin().record(retained_pointer).is_some());
        assert!(active_length(&log) > MIN_GENERATION_BYTES);
        log.retain(retained);
        log.release(retained);
        assert!(log.pin().record(retained_pointer).is_some());
        log.release(retained);
        wait_for_compaction(&log);
        assert!(log.pin().record(retained_pointer).is_none());
        let state = log.state.lock().unwrap();
        assert!(state.oldest_allocated > retained && state.oldest_allocated == state.oldest_live);
        drop(state);

        // A temporary log keeps every generation, which keep growing, and indexes no root.
        let temporary = TrieNodeLog::temporary();
        let session = temporary.pin();
        let first = session.append(&filler);
        while temporary.active.load(Ordering::Relaxed) <= LIVE_GENERATIONS + 2 {
            session.append(&filler);
        }
        assert!(temporary.pin().record(first).is_some());
        assert!(active_length(&temporary) > MIN_GENERATION_BYTES);
        session.set_root(None, &[0x44; 32], first);
        assert_eq!(session.root(None, &[0x44; 32]), 0);
    }

    #[test]
    fn compaction_keeps_last_record_of_keys_not_rewritten_later_and_root_keys_while_pinned() {
        let log = TrieNodeLog::create(
            MIN_GENERATION_BYTES,
            0,
            false,
            RootTable::new(1),
            RootTable::new(1),
        );
        let address = [0x33; 32];
        let keys = [1, 1, 2, 3]
            .map(|nibble| node_key(Some(&address), &TreePath::default().append(&[nibble])));
        let writer = log.pin();
        let pointers: Vec<LogPointer> = keys
            .iter()
            .enumerate()
            .map(|(index, &key)| writer.append_keyed(&leaf(vec![index as u8; 40]), key))
            .collect();
        writer.set_root(Some(&address), &[0x44; 32], pointers[3]);
        // Later generations rewrite the third key only, until the first generation is dropped.
        while log.active.load(Ordering::Relaxed) <= LIVE_GENERATIONS + 1 {
            writer.append_keyed(&leaf(vec![0xcc; 1000]), keys[2]);
        }

        // A session started once it is dropped cannot read it until compacted, but pins what compaction keeps.
        let reader = log.pin();
        assert!(reader.record(pointers[1]).is_none());
        drop(writer);
        wait_for_compaction(&log);
        let kept: Vec<bool> = pointers
            .iter()
            .map(|&pointer| reader.record(pointer).is_some())
            .collect();
        assert_eq!(kept, [false, true, false, true]);
        assert_eq!(read(&reader, pointers[1]), rlp(&leaf(vec![1; 40])));
        assert_eq!(reader.root(Some(&address), &[0x44; 32]), pointers[3]);
        assert!(log.state.lock().unwrap().compressed_memory > 0);

        // Over the compressed memory, so freed once no session pins it.
        drop(reader);
        assert!(log.pin().record(pointers[1]).is_none());
        assert_eq!(log.state.lock().unwrap().compressed_memory, 0);
    }
}
