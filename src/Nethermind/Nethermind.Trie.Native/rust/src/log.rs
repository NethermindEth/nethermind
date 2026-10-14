// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

//! In-memory log of the decoded nodes bulk sets write, which later bulk sets read in place.

use std::alloc::{alloc_zeroed, dealloc, Layout};
use std::cell::RefCell;
use std::hash::{Hash, Hasher};
use std::sync::atomic::{AtomicPtr, AtomicU64, AtomicUsize, Ordering};
use std::sync::Mutex;

use rustc_hash::FxHasher;

use crate::metrics;
use crate::node::{
    record_length, record_length_of, write_record, LoggedNode, Node, RECORD_HEADER_LENGTH,
};

/// Where a node record is in a [`TrieNodeLog`]: its generation in the high 24 bits and its offset in that generation in
/// the low 40 bits. Zero is no record.
pub type LogPointer = u64;

const OFFSET_BITS: u32 = 40;
const OFFSET_MASK: u64 = (1 << OFFSET_BITS) - 1;
const LIVE_GENERATIONS: u64 = 8;
/// Generations allocated at once, live or still pinned, are capped by the size of the generation table.
const MAX_GENERATIONS: usize = 4096;
const MIN_GENERATION_BYTES: usize = 64 << 10;
/// What generations grow to under pressure, unless configured larger, so the generation table holds at least 256 GiB.
const MAX_GROWN_GENERATION_BYTES: usize = 64 << 20;
const RECORD_ALIGNMENT: usize = 8;
const STATE_ROOT_SLOTS: usize = 1024;
const MEMORY_PER_STORAGE_ROOT_SLOT: u64 = 4096;
/// address: [u8; 32], zero for the state trie, root hash: [u8; 32], pointer of the root's record: u64, pointer of the
/// previous root key record of the slot: u64.
const ROOT_KEY_LENGTH: usize = 80;

thread_local! {
    static TEMPORARY: RefCell<Option<TrieNodeLog>> = const { RefCell::new(None) };
}

/// Decoded nodes, each child reference holding the log pointer of its child, so a trie is walked through pointers once
/// its root is found. Only roots are indexed, the state roots by hash and the storage roots by address and hash, each
/// through a root key record logged next to the nodes.
///
/// Records are appended to the active generation. Once [`LIVE_GENERATIONS`] are live, starting a new one drops the
/// oldest, which is freed once no [`LogSession`] that could read it is left. A session pins every generation live when
/// it starts, and generations are freed oldest first, so a pointer is readable by a session exactly when its generation
/// is not older than the session's floor.
///
/// A generation [`TrieNodeLog::retain_active`] retained is not dropped, nor is any later one, until
/// [`TrieNodeLog::release`], so every record appended since stays readable by later sessions too, and the log then
/// holds more than its memory.
///
/// Under pressure, while more than [`LIVE_GENERATIONS`] are allocated, each new generation is twice the size of the one
/// before, up to [`MAX_GROWN_GENERATION_BYTES`], so a bulk set larger than the log still fits in the generation table.
pub struct TrieNodeLog {
    generations: Box<[Generation]>,
    active: AtomicU64,
    state: Mutex<State>,
    generation_bytes: usize,
    temporary: bool,
    state_roots: RootTable,
    storage_roots: RootTable,
}

#[derive(Default)]
struct Generation {
    base: AtomicPtr<u8>,
    length: AtomicUsize,
    used: AtomicUsize,
}

struct State {
    oldest_live: u64,
    oldest_allocated: u64,
    /// Sessions by floor, indexed like the generation table.
    pins: Box<[u32]>,
    /// Retains by generation, indexed like the generation table.
    retains: Box<[u32]>,
}

impl TrieNodeLog {
    /// Creates a log of about `max_memory` bytes, exceeded only while sessions pin dropped generations.
    pub fn new(max_memory: u64) -> TrieNodeLog {
        let storage_root_slots =
            (max_memory / MEMORY_PER_STORAGE_ROOT_SLOT).max(STATE_ROOT_SLOTS as u64);
        TrieNodeLog::create(
            ((max_memory / LIVE_GENERATIONS) as usize).max(MIN_GENERATION_BYTES),
            false,
            RootTable::new(STATE_ROOT_SLOTS),
            RootTable::new((storage_root_slots as usize).next_power_of_two()),
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

    fn temporary() -> TrieNodeLog {
        TrieNodeLog::create(
            MIN_GENERATION_BYTES,
            true,
            RootTable::new(0),
            RootTable::new(0),
        )
    }

    fn create(
        generation_bytes: usize,
        temporary: bool,
        state_roots: RootTable,
        storage_roots: RootTable,
    ) -> TrieNodeLog {
        let log = TrieNodeLog {
            generations: (0..MAX_GENERATIONS)
                .map(|_| Generation::default())
                .collect(),
            active: AtomicU64::new(1),
            state: Mutex::new(State {
                oldest_live: 1,
                oldest_allocated: 1,
                pins: vec![0; MAX_GENERATIONS].into_boxed_slice(),
                retains: vec![0; MAX_GENERATIONS].into_boxed_slice(),
            }),
            generation_bytes,
            temporary,
            state_roots,
            storage_roots,
        };
        log.generation(1)
            .start(allocate(generation_bytes), generation_bytes, temporary);
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

    /// Pins every live generation until the returned session is dropped.
    pub fn pin(&self) -> LogSession<'_> {
        let mut state = self.state.lock().unwrap();
        let floor = state.oldest_live;
        state.pins[slot(floor)] += 1;
        LogSession { log: self, floor }
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
        let generation = self.generation(id);
        assert!(
            generation.base.load(Ordering::Relaxed).is_null(),
            "too many trie node log generations pinned"
        );
        generation.start(
            allocate(generation_length),
            generation_length,
            self.temporary,
        );
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

    /// Frees dropped generations, oldest first, up to the first one still pinned.
    fn reclaim(&self, state: &mut State) {
        while state.oldest_allocated < state.oldest_live
            && state.pins[slot(state.oldest_allocated)] == 0
        {
            self.generation(state.oldest_allocated).free(self.temporary);
            state.oldest_allocated += 1;
        }
    }
}

impl Drop for TrieNodeLog {
    fn drop(&mut self) {
        for generation in self.generations.iter() {
            generation.free(self.temporary);
        }
    }
}

impl Generation {
    fn start(&self, buffer: *mut u8, length: usize, temporary: bool) {
        if !temporary {
            metrics::generation_allocated(length);
        }
        self.length.store(length, Ordering::Relaxed);
        self.used.store(0, Ordering::Relaxed);
        self.base.store(buffer, Ordering::Release);
    }

    fn free(&self, temporary: bool) {
        let base = self.base.swap(std::ptr::null_mut(), Ordering::Relaxed);
        if !base.is_null() {
            let length = self.length.load(Ordering::Relaxed);
            if !temporary {
                metrics::generation_freed(length);
            }
            free(base, length);
        }
    }
}

fn slot(generation: u64) -> usize {
    generation as usize % MAX_GENERATIONS
}

/// Zeroed, so every record slice covers initialized memory.
fn allocate(length: usize) -> *mut u8 {
    let layout = Layout::from_size_align(length, RECORD_ALIGNMENT).unwrap();
    let buffer = unsafe { alloc_zeroed(layout) };
    if buffer.is_null() {
        std::alloc::handle_alloc_error(layout);
    }
    buffer
}

fn free(buffer: *mut u8, length: usize) {
    unsafe {
        dealloc(
            buffer,
            Layout::from_size_align(length, RECORD_ALIGNMENT).unwrap(),
        )
    }
}

/// A pin on a [`TrieNodeLog`], through which one bulk set reads and appends records.
pub struct LogSession<'a> {
    log: &'a TrieNodeLog,
    floor: u64,
}

impl LogSession<'_> {
    /// Whether `pointer` is a record this session can read, that is, not zero and not dropped before it started.
    pub fn is_valid(&self, pointer: LogPointer) -> bool {
        pointer >> OFFSET_BITS >= self.floor
    }

    /// The node record at `pointer`, which must be valid.
    pub fn record(&self, pointer: LogPointer) -> &[u8] {
        let length = record_length_of(self.bytes(pointer, RECORD_HEADER_LENGTH));
        self.bytes(pointer, length)
    }

    /// The `length` bytes at `pointer`, which must be valid.
    fn bytes(&self, pointer: LogPointer, length: usize) -> &[u8] {
        debug_assert!(self.is_valid(pointer));
        self.log.bytes(pointer, length)
    }

    /// Appends the record of `node`, whose children must all be resolved, and returns its pointer.
    #[cfg(test)]
    pub fn append(&self, node: &Node) -> LogPointer {
        let mut rlp = Vec::new();
        crate::node::encode(node, &mut rlp);
        let hash = crate::keccak::keccak(&rlp);
        self.append_bytes(record_length(node, rlp.len()), |out| {
            write_record(node, &hash, &rlp, out)
        })
    }

    /// Appends the records of `nodes`, each a node whose children must all be resolved with its hash and RLP, in one
    /// reservation, and pushes their pointers to `pointers`.
    pub fn append_all<'n>(
        &self,
        nodes: impl Iterator<Item = (&'n Node, &'n [u8; 32], &'n [u8])> + Clone,
        pointers: &mut Vec<LogPointer>,
    ) {
        let first = pointers.len();
        // Each record's length, until it becomes its pointer.
        pointers.extend(
            nodes
                .clone()
                .map(|(node, _, rlp)| record_length(node, rlp.len()) as LogPointer),
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
            for ((node, hash, rlp), pointer) in nodes.zip(&mut pointers[first..]) {
                let length = *pointer as usize;
                write_record(node, hash, rlp, &mut out[offset..offset + length]);
                *pointer = offset as LogPointer;
                offset += length.next_multiple_of(RECORD_ALIGNMENT);
            }
        });
        for pointer in &mut pointers[first..] {
            *pointer += base;
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
    /// links a record to one of a newer generation, so the records left are all dropped.
    pub(crate) fn find_root(&self, address: Option<&[u8; 32]>, root: &[u8; 32]) -> LogPointer {
        let Some(slot) = self.log.roots(address).slot(address, root) else {
            return 0;
        };
        let address = address.copied().unwrap_or_default();
        let mut key = slot.load(Ordering::Acquire);
        while self.is_valid(key) {
            let record = self.bytes(key, ROOT_KEY_LENGTH);
            if record[..32] == address && record[32..64] == *root {
                let pointer = u64::from_le_bytes(record[64..72].try_into().unwrap());
                return match self.is_valid(pointer) {
                    true => pointer,
                    false => 0,
                };
            }
            key = u64::from_le_bytes(record[72..].try_into().unwrap());
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
                out[..32].copy_from_slice(&address.copied().unwrap_or_default());
                out[32..64].copy_from_slice(root);
                out[64..72].copy_from_slice(&pointer.to_le_bytes());
                out[72..].copy_from_slice(&previous.to_le_bytes());
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
        rlp(&LoggedNode::new(session.record(pointer)).to_owned(0))
    }

    #[test]
    fn appends_reads_in_place_truncates_and_indexes_roots() {
        let log = TrieNodeLog::new(0);
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
                .map(|&pointer| session.record(pointer)[0])
                .collect::<Vec<_>>(),
            [2, 3]
        );
        let extension = LoggedNode::new(session.record(pointers[1]));
        assert!(
            matches!(extension.child(0), ChildRef::Hash { pointer, .. } if pointer == leaf_pointer)
        );
        for &pointer in &pointers[2..] {
            let branch = LoggedNode::new(session.record(pointer));
            assert!(
                matches!(branch.child(3), ChildRef::Hash { pointer, .. } if pointer == leaf_pointer)
            );
            assert!(matches!(branch.child(4), ChildRef::Empty));
            assert!(
                matches!(branch.child(12), ChildRef::Hash { hash, pointer: 0 } if hash == [0x33; 32])
            );
        }
        assert!(
            matches!(LoggedNode::new(session.record(pointers[3])).child(9), ChildRef::Inline(inline) if inline.as_slice() == rlp(&inline_leaf))
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
        assert!(!log.pin().is_valid(leaf_pointer));
        assert!(session.is_valid(leaf_pointer));
        assert_eq!(read(&session, leaf_pointer), rlp(&leaf(vec![0xaa; 40])));
        assert_eq!(log.state.lock().unwrap().oldest_allocated, 1);
        drop(session);
        let state = log.state.lock().unwrap();
        assert!(state.oldest_allocated > 1 && state.oldest_allocated == state.oldest_live);
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
        assert!(log.pin().is_valid(retained_pointer));
        assert!(active_length(&log) > MIN_GENERATION_BYTES);
        log.retain(retained);
        log.release(retained);
        assert!(log.pin().is_valid(retained_pointer));
        log.release(retained);
        assert!(!log.pin().is_valid(retained_pointer));
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
        assert!(temporary.pin().is_valid(first));
        assert!(active_length(&temporary) > MIN_GENERATION_BYTES);
        session.set_root(None, &[0x44; 32], first);
        assert_eq!(session.root(None, &[0x44; 32]), 0);
    }
}
