// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

//! In-memory buffer of the decoded nodes bulk sets write, which later bulk sets read in place.

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
    record_length, record_length_of, write_record, BufferedNode, Node, TreePath,
    RECORD_HEADER_LENGTH,
};

/// Where a record is in a [`NodeRecordBuffer`]: its generation in the high 32 bits and its offset in that generation in the
/// low 32 bits. Zero is no record. Compaction keeps the pointers of the records it keeps.
pub type BufferPointer = u64;

/// Reports allocated bytes as positive deltas and reclaimed bytes as negative deltas. Must not unwind.
pub(crate) type MemoryPressureFn = unsafe extern "C" fn(i64);

#[derive(Clone, Copy, Default)]
struct MemoryPressure(Option<MemoryPressureFn>);

impl MemoryPressure {
    fn report(self, bytes: i64) {
        if let Some(report) = self.0 {
            unsafe { report(bytes) };
        }
    }
}

const OFFSET_BITS: u32 = 32;
const OFFSET_MASK: u64 = (1 << OFFSET_BITS) - 1;
/// The live generations of the buffers that do not take them from configuration.
pub const DEFAULT_LIVE_GENERATIONS: u64 = 8;
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
    static TEMPORARY: RefCell<Option<Box<NodeRecordBuffer>>> = const { RefCell::new(None) };
}

/// Runs the compactions of every buffer, one at a time, off the threads of bulk sets.
static COMPACTOR: OnceLock<ThreadPool> = OnceLock::new();

/// Decoded nodes, each child reference holding the buffer pointer of its child, so a trie is walked through pointers once
/// its root is found. Only roots are indexed, the state roots by hash and the storage roots by address and hash, each
/// through a root key record buffered next to the nodes.
///
/// Records are appended to the active generation. Once its live generations are live, starting a new one drops the
/// oldest, which is compacted in the background once no [`BufferSession`] that could read its records in place is left.
/// Compaction keeps the last record of every key, see [`node_key`], unless the bloom filter of a later generation holds
/// the key, as that generation then likely holds a newer node at the same place, and keeps every root key record, so
/// lookups walk whole chains. The records kept are found by their pointer through a hash table. Compacted generations
/// are freed oldest first, once they take more than the compressed memory and no session pins them.
///
/// A session pins every generation live when it starts, and every compacted one not yet due to be freed, so a pointer is
/// readable by it in place exactly when its generation is not older than the session's floor, and otherwise through its
/// compacted generation when compaction kept it and it was not due to be freed. A compacted generation due to be freed
/// thus waits only for the sessions started before, however much later ones overlap.
///
/// A generation [`NodeRecordBuffer::retain_active`] retained is not dropped, nor is any later one, until
/// [`NodeRecordBuffer::release`], so every record appended since stays readable by later sessions too, and the buffer then
/// holds more than its live generations.
///
/// Under pressure, while more than its live generations are allocated, each new generation is twice the size of the one
/// before, up to [`MAX_GROWN_GENERATION_BYTES`], so a bulk set larger than the buffer still fits in the generation table.
pub struct NodeRecordBuffer {
    generations: Box<[Generation]>,
    active: AtomicU64,
    state: Mutex<State>,
    /// Notified when compaction stops.
    compaction_idle: Condvar,
    generation_bytes: usize,
    /// How many of the newest generations stay readable in place.
    live_generations: u64,
    compressed_max_memory: u64,
    temporary: bool,
    memory_pressure: MemoryPressure,
    state_roots: RootTable,
    storage_roots: RootTable,
    /// The buffer an overlay reads the records written before it started from.
    parent: Option<Parent>,
}

/// The buffer an overlay reads, retained from the generation active when the overlay started until it is dropped.
struct Parent {
    buffer: *const NodeRecordBuffer,
    generation: u64,
    /// The first position not yet reserved when the overlay started, so the first one it does not read.
    boundary: BufferPointer,
}

// The parent buffer is shared as immutably as the overlay itself, and outlives it.
unsafe impl Send for Parent {}
unsafe impl Sync for Parent {}

impl Parent {
    /// Whether `pointer` is of the parent, so of a generation not newer than the retained one.
    fn holds(&self, pointer: BufferPointer) -> bool {
        pointer >> OFFSET_BITS <= self.generation
    }

    fn buffer<'a>(&self) -> &'a NodeRecordBuffer {
        unsafe { &*self.buffer }
    }
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
    /// The oldest compacted generation not due to be freed, the compressed floor of new sessions.
    oldest_kept_compressed: u64,
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

impl NodeRecordBuffer {
    /// Creates a buffer keeping the newest `live_generations` readable in place, whose compacted generations take about
    /// `compressed_max_memory` bytes, exceeded only while sessions pin them. Boxed, as compaction reads it in the
    /// background, so it must not be moved out.
    pub fn new(compressed_max_memory: u64, live_generations: u64) -> Box<NodeRecordBuffer> {
        let storage_root_slots =
            (compressed_max_memory / MEMORY_PER_STORAGE_ROOT_SLOT).max(STATE_ROOT_SLOTS as u64);
        NodeRecordBuffer::create(
            GENERATION_BYTES,
            live_generations,
            compressed_max_memory,
            false,
            RootTable::new(STATE_ROOT_SLOTS),
            RootTable::new((storage_root_slots as usize).next_power_of_two()),
        )
    }

    /// A buffer with small generations for record-import fixtures.
    pub(crate) fn for_fixtures(
        generation_bytes: usize,
        compressed_max_memory: u64,
        live_generations: u64,
    ) -> Box<NodeRecordBuffer> {
        NodeRecordBuffer::create(
            generation_bytes.max(MIN_GENERATION_BYTES),
            live_generations,
            compressed_max_memory,
            false,
            RootTable::new(STATE_ROOT_SLOTS),
            RootTable::new(STATE_ROOT_SLOTS),
        )
    }

    pub(crate) fn fixture_generation_live(&self, pointer: BufferPointer) -> bool {
        let id = pointer >> OFFSET_BITS;
        let state = self.state.lock().unwrap();
        id >= state.oldest_allocated && id <= self.active.load(Ordering::Acquire)
            && !self.generation(id).base.load(Ordering::Acquire).is_null()
    }

    /// A buffer of the smallest generations, which keeps nothing compacted.
    #[cfg(test)]
    pub fn minimal() -> Box<NodeRecordBuffer> {
        NodeRecordBuffer::create(
            MIN_GENERATION_BYTES,
            DEFAULT_LIVE_GENERATIONS,
            0,
            false,
            RootTable::new(STATE_ROOT_SLOTS),
            RootTable::new(STATE_ROOT_SLOTS),
        )
    }

    /// Calls `use_buffer` with a buffer for a single bulk set, which never drops a generation, so is always under pressure,
    /// and indexes no root. The thread reuses it afterwards, emptied.
    pub fn with_temporary<R>(use_buffer: impl FnOnce(&NodeRecordBuffer) -> R) -> R {
        let mut buffer = TEMPORARY.take().unwrap_or_else(NodeRecordBuffer::temporary);
        let result = use_buffer(&buffer);
        buffer.empty_temporary();
        TEMPORARY.set(Some(buffer));
        result
    }

    fn temporary() -> Box<NodeRecordBuffer> {
        NodeRecordBuffer::create(
            MIN_GENERATION_BYTES,
            DEFAULT_LIVE_GENERATIONS,
            0,
            true,
            RootTable::new(0),
            RootTable::new(0),
        )
    }

    /// Creates a buffer that reads the records `parent` holds now and keeps its own, in generations numbered after the
    /// parent's active one, until it is dropped, so they never grow the parent. Retains the parent's active generation,
    /// and resolves only the parent's roots indexed before now.
    ///
    /// # Safety
    /// `parent` must outlive the returned buffer.
    pub unsafe fn overlay(parent: &NodeRecordBuffer) -> Box<NodeRecordBuffer> {
        let (generation, boundary) = parent.retain_active_boundary();
        NodeRecordBuffer::create_over(
            Some(Parent {
                buffer: parent,
                generation,
                boundary,
            }),
            MIN_GENERATION_BYTES,
            DEFAULT_LIVE_GENERATIONS,
            0,
            false,
            RootTable::new(STATE_ROOT_SLOTS),
            RootTable::new(STATE_ROOT_SLOTS),
        )
    }

    fn create(
        generation_bytes: usize,
        live_generations: u64,
        compressed_max_memory: u64,
        temporary: bool,
        state_roots: RootTable,
        storage_roots: RootTable,
    ) -> Box<NodeRecordBuffer> {
        NodeRecordBuffer::create_over(
            None,
            generation_bytes,
            live_generations,
            compressed_max_memory,
            temporary,
            state_roots,
            storage_roots,
        )
    }

    fn create_over(
        parent: Option<Parent>,
        generation_bytes: usize,
        live_generations: u64,
        compressed_max_memory: u64,
        temporary: bool,
        state_roots: RootTable,
        storage_roots: RootTable,
    ) -> Box<NodeRecordBuffer> {
        assert!(
            (1..MAX_GENERATIONS as u64).contains(&live_generations),
            "live generations out of range"
        );
        let first = parent.as_ref().map_or(1, |parent| parent.generation + 1);
        let buffer = Box::new(NodeRecordBuffer {
            generations: (0..MAX_GENERATIONS)
                .map(|_| Generation::default())
                .collect(),
            active: AtomicU64::new(first),
            state: Mutex::new(State {
                oldest_compressed: first,
                oldest_kept_compressed: first,
                oldest_allocated: first,
                oldest_live: first,
                pins: vec![0; MAX_GENERATIONS].into_boxed_slice(),
                compressed_pins: vec![0; MAX_GENERATIONS].into_boxed_slice(),
                retains: vec![0; MAX_GENERATIONS].into_boxed_slice(),
                compressed_memory: 0,
                compacting: false,
            }),
            compaction_idle: Condvar::new(),
            generation_bytes,
            live_generations,
            compressed_max_memory,
            temporary,
            memory_pressure: MemoryPressure::default(),
            state_roots,
            storage_roots,
            parent,
        });
        buffer
            .generation(first)
            .start(generation_bytes, temporary, buffer.memory_pressure);
        buffer
    }

    /// Installs a reporter on a newly created buffer before its first use, accounting for its initial allocations once.
    pub(crate) fn set_memory_pressure(&mut self, report: MemoryPressureFn) {
        assert!(self.memory_pressure.0.is_none());
        self.memory_pressure = MemoryPressure(Some(report));
        self.memory_pressure.report(self.fixed_memory() as i64);
        for generation in self.generations.iter() {
            if !generation.base.load(Ordering::Relaxed).is_null() {
                self.memory_pressure.report(
                    allocation_size(
                        generation.length.load(Ordering::Relaxed),
                        generation.bloom_blocks.load(Ordering::Relaxed),
                    ) as i64,
                );
            }
        }
    }

    fn fixed_memory(&self) -> usize {
        std::mem::size_of::<Self>()
            + std::mem::size_of_val(&*self.generations)
            + 3 * MAX_GENERATIONS * std::mem::size_of::<u32>()
            + std::mem::size_of_val(&*self.state_roots.slots)
            + std::mem::size_of_val(&*self.storage_roots.slots)
    }

    /// Drops every record of a temporary buffer, which keeps its first generation and nothing else.
    fn empty_temporary(&mut self) {
        let active = *self.active.get_mut();
        for id in 2..=active {
            self.generation(id).free(true, self.memory_pressure);
        }
        *self.generations[slot(1)].used.get_mut() = 0;
        *self.active.get_mut() = 1;
    }

    /// Pins every live generation, and every compacted one not due to be freed, until the returned session is dropped.
    /// An overlay also pins its parent's, through a session of the parent.
    pub fn pin(&self) -> BufferSession<'_> {
        let parent = self
            .parent
            .as_ref()
            .map(|parent| Box::new(parent.buffer().pin()));
        let mut state = self.state.lock().unwrap();
        let floor = state.oldest_live;
        let compressed_floor = state.oldest_kept_compressed;
        state.pins[slot(floor)] += 1;
        state.compressed_pins[slot(compressed_floor)] += 1;
        BufferSession {
            buffer: self,
            floor,
            compressed_floor,
            parent,
        }
    }

    /// Retains the active generation until [`NodeRecordBuffer::release`] and returns it.
    pub fn retain_active(&self) -> u64 {
        self.retain_active_boundary().0
    }

    /// Retains the active generation and captures the first offset not yet reserved, under the rotation lock.
    pub(crate) fn retain_active_boundary(&self) -> (u64, BufferPointer) {
        let mut state = self.state.lock().unwrap();
        let active = self.active.load(Ordering::Acquire);
        state.retains[slot(active)] += 1;
        let generation = self.generation(active);
        let offset = generation.used.load(Ordering::Acquire).min(generation.length.load(Ordering::Relaxed));
        (active, active << OFFSET_BITS | offset as u64)
    }

    /// Retains `generation` once more until [`NodeRecordBuffer::release`]. It must still be live, as it is while retained.
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
    pub fn node(&self, pointer: BufferPointer) -> BufferedNode<'_> {
        if let Some(parent) = self.parent.as_ref().filter(|parent| parent.holds(pointer)) {
            return parent.buffer().node(pointer);
        }
        let length = record_length_of(self.bytes(pointer, RECORD_HEADER_LENGTH));
        BufferedNode::new(self.bytes(pointer, length))
    }

    /// The `length` bytes at `pointer`, whose generation must not be freed.
    fn bytes(&self, pointer: BufferPointer, length: usize) -> &[u8] {
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
        let under_pressure = self.temporary
            || self.parent.is_some()
            || state.oldest_allocated + self.live_generations <= full;
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
            "too many trie node buffer generations pinned"
        );
        generation.start(generation_length, self.temporary, self.memory_pressure);
        self.active.store(id, Ordering::Release);
        if !self.temporary {
            self.drop_old_generations(&mut state);
        }
        self.reclaim(&mut state);
    }

    /// Drops generations, oldest first, until its live generations are live or the oldest live one is retained. An
    /// overlay keeps every generation instead.
    fn drop_old_generations(&self, state: &mut State) {
        if self.parent.is_some() {
            return;
        }
        let active = self.active.load(Ordering::Acquire);
        let oldest_to_keep = active + 1 - self.live_generations.min(active);
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
        let buffer = BufferRef(self);
        COMPACTOR
            .get_or_init(|| {
                ThreadPoolBuilder::new()
                    .num_threads(1)
                    .build()
                    .expect("trie node buffer compactor")
            })
            .spawn(move || buffer.get().compact_dropped());
    }

    /// Whether the oldest allocated generation is dropped and no session reads it in place, so none writes it either.
    fn compactable(state: &State) -> bool {
        state.oldest_allocated < state.oldest_live && state.pins[slot(state.oldest_allocated)] == 0
    }

    /// Compacts the generations [`NodeRecordBuffer::compactable`], oldest first, freeing their allocation.
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
                self.memory_pressure
                    .report((memory + std::mem::size_of::<CompressedGeneration>() as u64) as i64);
                state.compressed_memory += memory;
                generation
                    .compressed
                    .store(Box::into_raw(compressed), Ordering::Release);
            }
            generation.free(false, self.memory_pressure);
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
                    let key = BufferedNode::new(&records[offset..offset + length]).buffer_key();
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
        // In buffer order, as a trie's records were appended near each other.
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

    /// Marks compacted generations due to be freed, oldest first, while they and the ones after take more than the
    /// compressed memory, are older than `before`, or nothing of them was kept, then frees the marked ones, oldest first,
    /// up to the first one a session started before its mark still pins.
    fn evict_compressed(&self, state: &mut State, before: u64) {
        let mut kept_memory = state.compressed_memory;
        let mut id = state.oldest_compressed;
        while id < state.oldest_allocated {
            let compressed = unsafe {
                self.generation(id)
                    .compressed
                    .load(Ordering::Relaxed)
                    .as_ref()
            };
            if id >= state.oldest_kept_compressed
                && compressed.is_some()
                && kept_memory <= self.compressed_max_memory
                && id >= before
            {
                break;
            }
            kept_memory -= compressed.map_or(0, |compressed| compressed.memory());
            id += 1;
        }
        state.oldest_kept_compressed = id;
        while state.oldest_compressed < state.oldest_kept_compressed
            && state.compressed_pins[slot(state.oldest_compressed)] == 0
        {
            state.compressed_memory -= self
                .generation(state.oldest_compressed)
                .free_compressed(self.memory_pressure);
            state.oldest_compressed += 1;
        }
    }
}

impl Drop for NodeRecordBuffer {
    fn drop(&mut self) {
        let mut state = self.state.lock().unwrap();
        while state.compacting {
            state = self.compaction_idle.wait(state).unwrap();
        }
        drop(state);
        for generation in self.generations.iter() {
            generation.free(self.temporary, self.memory_pressure);
            generation.free_compressed(self.memory_pressure);
        }
        self.memory_pressure.report(-(self.fixed_memory() as i64));
        if let Some(parent) = &self.parent {
            parent.buffer().release(parent.generation);
        }
    }
}

/// A buffer compaction reads in the background, which the buffer waits for before it is dropped.
struct BufferRef(*const NodeRecordBuffer);

unsafe impl Send for BufferRef {}

impl BufferRef {
    fn get(&self) -> &NodeRecordBuffer {
        unsafe { &*self.0 }
    }
}

impl Generation {
    /// Allocates `length` bytes of records, followed by a bloom filter sized for them unless `temporary`, which is
    /// never compacted.
    fn start(&self, length: usize, temporary: bool, pressure: MemoryPressure) {
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
        pressure.report(size as i64);
    }

    fn free(&self, temporary: bool, pressure: MemoryPressure) {
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
            pressure.report(-(size as i64));
        }
    }

    /// Frees what compaction kept of the generation and returns the bytes it took.
    fn free_compressed(&self, pressure: MemoryPressure) -> u64 {
        let compressed = self
            .compressed
            .swap(std::ptr::null_mut(), Ordering::Relaxed);
        if compressed.is_null() {
            return 0;
        }
        let memory = unsafe { Box::from_raw(compressed) }.memory();
        metrics::compressed_freed(memory);
        pressure.report(-((memory + std::mem::size_of::<CompressedGeneration>() as u64) as i64));
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
    table: Box<[(BufferPointer, u64)]>,
}

impl CompressedGeneration {
    fn insert(&mut self, pointer: BufferPointer, offset: u64) {
        let mut index = self.index(pointer);
        while self.table[index].0 != 0 {
            index = (index + 1) & (self.table.len() - 1);
        }
        self.table[index] = (pointer, offset);
    }

    /// Where the record kept at `pointer` starts, if kept.
    fn find(&self, pointer: BufferPointer) -> Option<*const u8> {
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

    fn index(&self, pointer: BufferPointer) -> usize {
        (pointer.wrapping_mul(0x9e3779b97f4a7c15) >> 32) as usize & (self.table.len() - 1)
    }

    fn memory(&self) -> u64 {
        (self.records.capacity() + std::mem::size_of_val(&*self.table)) as u64
    }
}

/// A pin on a [`NodeRecordBuffer`], through which one bulk set reads and appends records.
pub struct BufferSession<'a> {
    buffer: &'a NodeRecordBuffer,
    floor: u64,
    compressed_floor: u64,
    /// The session of an overlay's parent, through which it reads the parent's records.
    parent: Option<Box<BufferSession<'a>>>,
}

impl BufferSession<'_> {
    /// The node record at `pointer`, or `None` when this session cannot read it: zero, dropped before it started and
    /// not kept by compaction, or freed.
    pub fn record(&self, pointer: BufferPointer) -> Option<&[u8]> {
        let start = self.locate(pointer)?;
        unsafe {
            let length = record_length_of(std::slice::from_raw_parts(start, RECORD_HEADER_LENGTH));
            Some(std::slice::from_raw_parts(start, length))
        }
    }

    /// Where the record at `pointer` starts, if this session can read it, in its generation while not older than the
    /// floor, and otherwise in what compaction kept of it. An overlay reads its parent's records written before it
    /// started through its parent's session.
    fn locate(&self, pointer: BufferPointer) -> Option<*const u8> {
        if pointer & crate::node::TRANSIENT_POINTER != 0 {
            return None;
        }
        if let Some(parent) = self
            .buffer
            .parent
            .as_ref()
            .filter(|parent| parent.holds(pointer))
        {
            return match pointer < parent.boundary {
                true => self.parent.as_ref()?.locate(pointer),
                false => None,
            };
        }
        let id = pointer >> OFFSET_BITS;
        if id >= self.floor {
            return Some(self.buffer.bytes(pointer, 0).as_ptr());
        }
        if id < self.compressed_floor {
            return None;
        }
        // Null while compaction has not kept anything of it, and never another generation's, as the session pins it.
        let compressed = self
            .buffer
            .generation(id)
            .compressed
            .load(Ordering::Acquire);
        unsafe { compressed.as_ref() }?.find(pointer)
    }

    /// Appends the record of `node`, whose children must all be resolved, under key zero and returns its pointer.
    #[cfg(test)]
    pub fn append(&self, node: &Node) -> BufferPointer {
        self.append_keyed(node, 0)
    }

    /// Appends the record of `node`, whose children must all be resolved, under `key` and returns its pointer.
    #[cfg(test)]
    pub fn append_keyed(&self, node: &Node, key: u64) -> BufferPointer {
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
        pointers: &mut Vec<BufferPointer>,
    ) {
        let first = pointers.len();
        // Each record's length, until it becomes its pointer.
        pointers.extend(
            nodes
                .clone()
                .map(|(node, _, _, rlp)| record_length(node, rlp.len()) as BufferPointer),
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
                *pointer = offset as BufferPointer;
                offset += length.next_multiple_of(RECORD_ALIGNMENT);
            }
        });
        for pointer in &mut pointers[first..] {
            *pointer += base;
        }
        let bloom = self.buffer.generation(base >> OFFSET_BITS).bloom();
        for (_, key, _, _) in nodes {
            bloom.insert(key);
        }
    }

    /// Appends `length` bytes that `write` fills and returns their pointer.
    fn append_bytes(&self, length: usize, write: impl FnOnce(&mut [u8])) -> BufferPointer {
        let aligned = length.next_multiple_of(RECORD_ALIGNMENT);
        loop {
            let id = self.buffer.active.load(Ordering::Acquire);
            let generation = self.buffer.generation(id);
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
            self.buffer.rotate(id, length);
        }
    }

    /// The pointer of the root `root` of the trie of `address`, the state trie when `None`, or zero when not found.
    pub fn root(&self, address: Option<&[u8; 32]>, root: &[u8; 32]) -> BufferPointer {
        let pointer = self.find_root(address, root);
        if !self.buffer.temporary {
            metrics::root_looked_up(pointer != 0);
        }
        pointer
    }

    /// Finds the root in this buffer, and otherwise, for an overlay, among the roots its parent indexed before it started.
    pub(crate) fn find_root(&self, address: Option<&[u8; 32]>, root: &[u8; 32]) -> BufferPointer {
        let pointer = self.find_own_root(address, root);
        match (pointer, &self.parent, &self.buffer.parent) {
            (0, Some(session), Some(parent)) => {
                let pointer = session.find_root(address, root);
                if pointer < parent.boundary {
                    pointer
                } else {
                    0
                }
            }
            _ => pointer,
        }
    }

    /// Walks the root key records of the slot, newest first, up to the first this session cannot read. A chain never
    /// links a record to one of a newer generation, and compaction keeps every root key record, so the records left are
    /// all freed, or dropped and not compacted yet.
    fn find_own_root(&self, address: Option<&[u8; 32]>, root: &[u8; 32]) -> BufferPointer {
        let Some(slot) = self.buffer.roots(address).slot(address, root) else {
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
    pub fn set_root(&self, address: Option<&[u8; 32]>, root: &[u8; 32], pointer: BufferPointer) {
        let Some(slot) = self.buffer.roots(address).slot(address, root) else {
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

impl Drop for BufferSession<'_> {
    fn drop(&mut self) {
        let mut state = self.buffer.state.lock().unwrap();
        state.pins[slot(self.floor)] -= 1;
        state.compressed_pins[slot(self.compressed_floor)] -= 1;
        self.buffer.reclaim(&mut state);
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
    use crate::node::{decode_rlp, empty_children, encode, BufferedNode, ChildRef, Nibbles};

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

    fn active_length(buffer: &NodeRecordBuffer) -> usize {
        buffer
            .generation(buffer.active.load(Ordering::Relaxed))
            .length
            .load(Ordering::Relaxed)
    }

    fn read(session: &BufferSession, pointer: BufferPointer) -> Vec<u8> {
        rlp(&BufferedNode::new(session.record(pointer).unwrap()).to_owned(0))
    }

    fn wait_for_compaction(buffer: &NodeRecordBuffer) {
        let mut state = buffer.state.lock().unwrap();
        while state.compacting {
            state = buffer.compaction_idle.wait(state).unwrap();
        }
    }

    #[test]
    fn snapshot_cursor_releases_its_own_retain_when_abandoned() {
        let buffer = NodeRecordBuffer::minimal();
        let (generation, boundary) = buffer.retain_active_boundary();
        let pointer = buffer.pin().append(&leaf(vec![1; 40]));
        let mut cursor = crate::traversal::SnapshotCursor::new(&buffer, pointer, boundary);
        assert_eq!(buffer.state.lock().unwrap().retains[slot(generation)], 2);
        buffer.release(generation);
        assert_eq!(cursor.next(), Some((TreePath::default(), pointer)));
        drop(cursor);
        assert_eq!(buffer.state.lock().unwrap().retains[slot(generation)], 0);
    }

    #[test]
    fn memory_pressure_balances_growth_compaction_reclamation_and_drop() {
        static EVENTS: Mutex<Vec<(i64, std::thread::ThreadId)>> = Mutex::new(Vec::new());
        unsafe extern "C" fn report(bytes: i64) {
            EVENTS
                .lock()
                .unwrap()
                .push((bytes, std::thread::current().id()));
        }
        fn reported(events: &Mutex<Vec<(i64, std::thread::ThreadId)>>) -> i64 {
            events.lock().unwrap().iter().map(|&(bytes, _)| bytes).sum()
        }
        fn allocated(buffer: &NodeRecordBuffer) -> i64 {
            let _state = buffer.state.lock().unwrap();
            let mut bytes = buffer.fixed_memory();
            for generation in buffer.generations.iter() {
                if !generation.base.load(Ordering::Relaxed).is_null() {
                    bytes += allocation_size(
                        generation.length.load(Ordering::Relaxed),
                        generation.bloom_blocks.load(Ordering::Relaxed),
                    );
                }
                if let Some(compressed) =
                    unsafe { generation.compressed.load(Ordering::Relaxed).as_ref() }
                {
                    bytes += compressed.memory() as usize + std::mem::size_of::<CompressedGeneration>();
                }
            }
            bytes as i64
        }

        for compressed_max_memory in [0, 1 << 20] {
            EVENTS.lock().unwrap().clear();
            let mut buffer = NodeRecordBuffer::for_fixtures(
                MIN_GENERATION_BYTES,
                compressed_max_memory,
                DEFAULT_LIVE_GENERATIONS,
            );
            unsafe { crate::nm_patricia_buffer_set_memory_pressure(&mut *buffer, report) };
            assert_eq!(reported(&EVENTS), allocated(&buffer));
            assert_eq!(EVENTS.lock().unwrap().len(), 2);

            // Failed fixture decoding must neither allocate a generation nor change pressure.
            let before = reported(&EVENTS);
            let mut pointer = 0;
            assert_eq!(
                unsafe {
                    crate::nm_patricia_buffer_fixture_append_rlp(
                        &*buffer,
                        std::ptr::null(),
                        &TreePath::default(),
                        [0xff].as_ptr(),
                        1,
                        std::ptr::null(),
                        false,
                        &mut pointer,
                    )
                },
                crate::Error::CorruptNode.code()
            );
            assert_eq!(reported(&EVENTS), before);

            let retained = buffer.retain_active();
            buffer.retain(retained);
            let writer = buffer.pin();
            let first = writer.append_keyed(&leaf(vec![0xaa; 40]), 1);
            writer.set_root(None, &[0x11; 32], first);
            let filler = leaf(vec![0xbb; 1000]);
            while buffer.active.load(Ordering::Relaxed) <= DEFAULT_LIVE_GENERATIONS + 2 {
                writer.append_keyed(&filler, 2);
            }
            assert!(active_length(&buffer) > MIN_GENERATION_BYTES);
            assert_eq!(reported(&EVENTS), allocated(&buffer));
            buffer.release(retained);
            assert_eq!(reported(&EVENTS), allocated(&buffer));
            buffer.release(retained);
            // This reader lets compaction run but retains its compressed output.
            let reader = buffer.pin();
            drop(writer);
            wait_for_compaction(&buffer);
            assert!(buffer.state.lock().unwrap().compressed_memory > 0);
            assert_eq!(reported(&EVENTS), allocated(&buffer));
            assert!(EVENTS
                .lock()
                .unwrap()
                .iter()
                .any(|&(bytes, thread)| bytes < 0 && thread != std::thread::current().id()));
            assert!(reader.record(first).is_some());
            drop(reader);
            wait_for_compaction(&buffer);
            assert_eq!(reported(&EVENTS), allocated(&buffer));
            if compressed_max_memory == 0 {
                assert_eq!(buffer.state.lock().unwrap().compressed_memory, 0);
            }
            // Drop may itself have to wait for compaction before its final removals.
            let writer = buffer.pin();
            let active = buffer.active.load(Ordering::Relaxed);
            while buffer.active.load(Ordering::Relaxed) == active {
                writer.append_keyed(&filler, 2);
            }
            drop(writer);
            drop(buffer);
            let events = EVENTS.lock().unwrap();
            let mut outstanding = 0;
            for &(bytes, _) in events.iter() {
                assert_ne!(bytes, 0);
                outstanding += bytes;
                assert!(outstanding >= 0);
            }
            assert_eq!(outstanding, 0);
            assert_eq!(
                events
                    .iter()
                    .filter(|&&(bytes, _)| bytes > 0)
                    .map(|&(bytes, _)| bytes)
                    .sum::<i64>(),
                -events
                    .iter()
                    .filter(|&&(bytes, _)| bytes < 0)
                    .map(|&(bytes, _)| bytes)
                    .sum::<i64>()
            );
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
        let buffer = NodeRecordBuffer::minimal();
        let session = buffer.pin();

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
        let pointers: Vec<BufferPointer> = [
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
            let buffered = buffer.node(pointer);
            assert_eq!(buffered.rlp(), rlp(node));
            assert_eq!(*buffered.hash(), crate::keccak::keccak(&rlp(node)));
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
        let extension = BufferedNode::new(session.record(pointers[1]).unwrap());
        assert!(
            matches!(extension.child(0), ChildRef::Hash { pointer, .. } if pointer == leaf_pointer)
        );
        for &pointer in &pointers[2..] {
            let branch = BufferedNode::new(session.record(pointer).unwrap());
            assert!(
                matches!(branch.child(3), ChildRef::Hash { pointer, .. } if pointer == leaf_pointer)
            );
            assert!(matches!(branch.child(4), ChildRef::Empty));
            assert!(
                matches!(branch.child(12), ChildRef::Hash { hash, pointer: 0 } if hash == [0x33; 32])
            );
        }
        assert!(
            matches!(BufferedNode::new(session.record(pointers[3]).unwrap()).child(9), ChildRef::Inline(inline) if inline.as_slice() == rlp(&inline_leaf))
        );

        // A record larger than a generation gets one of its own.
        let large = leaf(vec![0xbb; MIN_GENERATION_BYTES * 2]);
        assert_eq!(read(&session, session.append(&large)), rlp(&large));

        // Filling more generations than are live drops the first, but not for the session pinned before, and generations
        // grow while it pins the dropped ones.
        let filler = leaf(vec![0xcc; 1000]);
        while buffer.active.load(Ordering::Relaxed) <= DEFAULT_LIVE_GENERATIONS + 2 {
            session.append(&filler);
        }
        assert!(active_length(&buffer) > MIN_GENERATION_BYTES);
        assert!(buffer.pin().record(leaf_pointer).is_none());
        assert_eq!(read(&session, leaf_pointer), rlp(&leaf(vec![0xaa; 40])));
        assert_eq!(buffer.state.lock().unwrap().oldest_allocated, 1);
        // Compacted once the session is gone, then freed, as the buffer keeps no compacted generation.
        drop(session);
        wait_for_compaction(&buffer);
        let state = buffer.state.lock().unwrap();
        assert!(state.oldest_allocated > 1 && state.oldest_allocated == state.oldest_live);
        assert_eq!(state.oldest_compressed, state.oldest_allocated);
        drop(state);
        let session = buffer.pin();
        let active = buffer.active.load(Ordering::Relaxed);
        while buffer.active.load(Ordering::Relaxed) == active {
            session.append(&filler);
        }
        assert_eq!(active_length(&buffer), MIN_GENERATION_BYTES);

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
        let single = NodeRecordBuffer::create(
            MIN_GENERATION_BYTES,
            DEFAULT_LIVE_GENERATIONS,
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
        while single.active.load(Ordering::Relaxed) <= DEFAULT_LIVE_GENERATIONS + 1 {
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
        let retained = buffer.retain_active();
        let session = buffer.pin();
        let retained_pointer = session.append(&filler);
        while buffer.active.load(Ordering::Relaxed) <= retained + DEFAULT_LIVE_GENERATIONS {
            session.append(&filler);
        }
        drop(session);
        assert!(buffer.pin().record(retained_pointer).is_some());
        assert!(active_length(&buffer) > MIN_GENERATION_BYTES);
        buffer.retain(retained);
        buffer.release(retained);
        assert!(buffer.pin().record(retained_pointer).is_some());
        buffer.release(retained);
        wait_for_compaction(&buffer);
        assert!(buffer.pin().record(retained_pointer).is_none());
        let state = buffer.state.lock().unwrap();
        assert!(state.oldest_allocated > retained && state.oldest_allocated == state.oldest_live);
        drop(state);

        // A temporary buffer keeps every generation, which keep growing, and indexes no root.
        let temporary = NodeRecordBuffer::temporary();
        let session = temporary.pin();
        let first = session.append(&filler);
        while temporary.active.load(Ordering::Relaxed) <= DEFAULT_LIVE_GENERATIONS + 2 {
            session.append(&filler);
        }
        assert!(temporary.pin().record(first).is_some());
        assert!(active_length(&temporary) > MIN_GENERATION_BYTES);
        session.set_root(None, &[0x44; 32], first);
        assert_eq!(session.root(None, &[0x44; 32]), 0);
    }

    #[test]
    fn compaction_preserves_real_child_links_but_never_transient_links() {
        let buffer = NodeRecordBuffer::create(
            MIN_GENERATION_BYTES,
            DEFAULT_LIVE_GENERATIONS,
            0,
            false,
            RootTable::new(1),
            RootTable::new(1),
        );
        let writer = buffer.pin();
        let child = leaf(vec![0x11; 40]);
        let child_hash = crate::keccak::keccak(&rlp(&child));
        let child_pointer = writer.append_keyed(&child, 1);
        let transient = crate::node::TRANSIENT_POINTER | 1 << 32;
        assert!(writer.record(transient).is_none());
        let mut children = empty_children();
        children[1] = ChildRef::Hash { hash: child_hash, pointer: child_pointer };
        children[2] = ChildRef::Hash { hash: [0x22; 32], pointer: transient };
        let branch = Node::Branch { children };
        let branch_hash = crate::keccak::keccak(&rlp(&branch));
        let root_pointer = writer.append_keyed(&branch, 2);
        writer.set_root(None, &branch_hash, root_pointer);
        while buffer.active.load(Ordering::Relaxed) <= DEFAULT_LIVE_GENERATIONS + 1 {
            writer.append_keyed(&leaf(vec![0xcc; 1000]), 3);
        }
        let reader = buffer.pin();
        drop(writer);
        wait_for_compaction(&buffer);
        let record = BufferedNode::new(reader.record(root_pointer).unwrap());
        assert_eq!(record.hash(), &branch_hash);
        assert!(matches!(record.child(1), ChildRef::Hash { pointer, hash } if pointer == child_pointer && hash == child_hash));
        assert!(matches!(record.child(2), ChildRef::Hash { pointer: 0, hash } if hash == [0x22; 32]));
        assert_eq!(reader.find_node(root_pointer, &TreePath::default().append(&[1])).unwrap().rlp(), rlp(&child));
        assert_eq!(reader.find_root(None, &branch_hash), root_pointer);
    }

    #[test]
    fn compaction_keeps_last_record_of_keys_not_rewritten_later_and_root_keys_while_pinned() {
        let buffer = NodeRecordBuffer::create(
            MIN_GENERATION_BYTES,
            DEFAULT_LIVE_GENERATIONS,
            0,
            false,
            RootTable::new(1),
            RootTable::new(1),
        );
        let address = [0x33; 32];
        let keys = [1, 1, 2, 3]
            .map(|nibble| node_key(Some(&address), &TreePath::default().append(&[nibble])));
        let writer = buffer.pin();
        let pointers: Vec<BufferPointer> = keys
            .iter()
            .enumerate()
            .map(|(index, &key)| writer.append_keyed(&leaf(vec![index as u8; 40]), key))
            .collect();
        writer.set_root(Some(&address), &[0x44; 32], pointers[3]);
        // Later generations rewrite the third key only, until the first generation is dropped.
        while buffer.active.load(Ordering::Relaxed) <= DEFAULT_LIVE_GENERATIONS + 1 {
            writer.append_keyed(&leaf(vec![0xcc; 1000]), keys[2]);
        }

        let mut children = empty_children();
        children[1] = ChildRef::Hash {
            hash: *buffer.node(pointers[1]).hash(),
            pointer: pointers[1],
        };
        let inherited_root = writer.append_keyed(&Node::Branch { children }, 0x12345678);

        // A session started once it is dropped cannot read it until compacted, but pins what compaction keeps.
        let reader = buffer.pin();
        assert!(reader.record(pointers[1]).is_none());
        drop(writer);
        wait_for_compaction(&buffer);
        let kept: Vec<bool> = pointers
            .iter()
            .map(|&pointer| reader.record(pointer).is_some())
            .collect();
        assert_eq!(kept, [false, true, false, true]);
        assert_eq!(read(&reader, pointers[1]), rlp(&leaf(vec![1; 40])));
        assert_eq!(reader.find_node(inherited_root, &TreePath::default().append(&[1])).unwrap().rlp(), rlp(&leaf(vec![1; 40])));
        assert_eq!(reader.root(Some(&address), &[0x44; 32]), pointers[3]);
        assert!(buffer.state.lock().unwrap().compressed_memory > 0);

        // Over the compressed memory, so freed once no session pins it.
        drop(reader);
        assert!(buffer.pin().record(pointers[1]).is_none());
        assert_eq!(buffer.state.lock().unwrap().compressed_memory, 0);
    }

    #[test]
    fn only_the_configured_live_generations_are_readable_in_place() {
        for live_generations in [1, DEFAULT_LIVE_GENERATIONS] {
            let buffer = NodeRecordBuffer::create(
                MIN_GENERATION_BYTES,
                live_generations,
                0,
                false,
                RootTable::new(1),
                RootTable::new(1),
            );
            let first = buffer.pin().append_keyed(&leaf(vec![0x11; 40]), 1);
            for active in 2..=live_generations + 1 {
                assert!(
                    buffer.pin().record(first).is_some(),
                    "live while {} is active",
                    active - 1
                );
                while buffer.active.load(Ordering::Relaxed) < active {
                    buffer.pin().append_keyed(&leaf(vec![0xcc; 1000]), 2);
                }
            }
            wait_for_compaction(&buffer);
            assert_eq!(buffer.state.lock().unwrap().oldest_live, 2);
            assert!(buffer.pin().record(first).is_none());
        }
    }

    #[test]
    fn overlapping_sessions_do_not_keep_compacted_generations_from_being_freed() {
        for compressed_max_memory in [0, 1 << 20] {
            let buffer = NodeRecordBuffer::create(
                MIN_GENERATION_BYTES,
                DEFAULT_LIVE_GENERATIONS,
                compressed_max_memory,
                false,
                RootTable::new(1),
                RootTable::new(1),
            );
            let first = buffer.pin().append_keyed(&leaf(vec![0x11; 40]), 1);
            // Each session starts before the previous one ends, so some session is always open.
            let mut reader = buffer.pin();
            for _ in 0..2 * DEFAULT_LIVE_GENERATIONS {
                let active = buffer.active.load(Ordering::Relaxed);
                while buffer.active.load(Ordering::Relaxed) == active {
                    reader.append_keyed(&leaf(vec![0xcc; 1000]), 2);
                }
                let next = buffer.pin();
                drop(reader);
                reader = next;
                wait_for_compaction(&buffer);
            }
            let next = buffer.pin();
            drop(reader);
            reader = next;

            let within_budget = compressed_max_memory != 0;
            assert_eq!(reader.record(first).is_some(), within_budget);
            assert_eq!(
                buffer.state.lock().unwrap().oldest_compressed > 1,
                !within_budget
            );
        }
    }

    #[test]
    fn overlay_reads_parent_records_from_before_it_started_and_keeps_its_own() {
        let parent = NodeRecordBuffer::minimal();
        let writer = parent.pin();
        let before = writer.append_keyed(&leaf(vec![0x11; 40]), 1);
        writer.set_root(None, &[0x11; 32], before);
        let overlay = unsafe { NodeRecordBuffer::overlay(&parent) };
        let after = writer.append_keyed(&leaf(vec![0x22; 40]), 2);
        writer.set_root(None, &[0x22; 32], after);
        let parent_active = parent.active.load(Ordering::Relaxed);
        let parent_used = parent
            .generation(parent_active)
            .used
            .load(Ordering::Relaxed);

        let session = overlay.pin();
        let own = session.append_keyed(&leaf(vec![0x33; 40]), 3);
        session.set_root(None, &[0x33; 32], own);
        while overlay.active.load(Ordering::Relaxed) <= parent_active + DEFAULT_LIVE_GENERATIONS + 1
        {
            session.append_keyed(&leaf(vec![0xcc; 1000]), 4);
        }
        drop(session);
        let reader = overlay.pin();
        assert_eq!(read(&reader, before), rlp(&leaf(vec![0x11; 40])));
        assert_eq!(overlay.node(before).rlp(), rlp(&leaf(vec![0x11; 40])));
        assert!(
            reader.record(after).is_none(),
            "written after the overlay started"
        );
        assert_eq!(
            read(&reader, own),
            rlp(&leaf(vec![0x33; 40])),
            "an overlay drops no generation"
        );
        assert_eq!(reader.root(None, &[0x11; 32]), before);
        assert_eq!(reader.root(None, &[0x22; 32]), 0);
        assert_eq!(reader.root(None, &[0x33; 32]), own);
        assert_eq!(parent.active.load(Ordering::Relaxed), parent_active);
        assert_eq!(
            parent
                .generation(parent_active)
                .used
                .load(Ordering::Relaxed),
            parent_used,
            "the overlay's writes do not grow its parent"
        );

        drop(reader);
        assert_eq!(parent.state.lock().unwrap().retains[slot(parent_active)], 1);
        drop(overlay);
        assert_eq!(parent.state.lock().unwrap().retains[slot(parent_active)], 0);
    }
}
