// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

//! In-memory log of the decoded nodes bulk sets write, which later bulk sets read in place.

use std::alloc::{alloc_zeroed, dealloc, Layout};
use std::hash::{Hash, Hasher};
use std::sync::atomic::{AtomicPtr, AtomicU64, AtomicUsize, Ordering};
use std::sync::Mutex;

use rustc_hash::FxHasher;

use crate::node::{record_length, record_length_of, write_record, Node, RECORD_HEADER_LENGTH};

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
/// address: [u8; 32], zero for the state trie, root hash: [u8; 32], pointer of the root's record: u64.
const ROOT_KEY_LENGTH: usize = 72;

/// Decoded nodes, each child reference holding the log pointer of its child, so a trie is walked through pointers once
/// its root is found. Only roots are indexed, the state roots by hash and the storage roots by address and hash, each
/// through a root key record logged next to the nodes.
///
/// Records are appended to the active generation. Once [`LIVE_GENERATIONS`] are live, starting a new one drops the
/// oldest, which is freed once no [`LogSession`] that could read it is left. A session pins every generation live when
/// it starts, and generations are freed oldest first, so a pointer is readable by a session exactly when its generation
/// is not older than the session's floor.
///
/// Under pressure, while sessions pin dropped generations, each new generation is twice the size of the one before, up
/// to [`MAX_GROWN_GENERATION_BYTES`], so a bulk set larger than the log still fits in the generation table.
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

    /// Creates a log for a single bulk set, which never drops a generation, so is always under pressure, and indexes no
    /// root.
    pub fn temporary() -> TrieNodeLog {
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
            }),
            generation_bytes,
            temporary,
            state_roots,
            storage_roots,
        };
        log.generation(1)
            .start(allocate(generation_bytes), generation_bytes);
        log
    }

    /// Pins every live generation until the returned session is dropped.
    pub fn pin(&self) -> LogSession<'_> {
        let mut state = self.state.lock().unwrap();
        let floor = state.oldest_live;
        state.pins[slot(floor)] += 1;
        LogSession { log: self, floor }
    }

    fn generation(&self, id: u64) -> &Generation {
        &self.generations[slot(id)]
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
        let under_pressure = self.temporary || state.oldest_allocated < state.oldest_live;
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
        generation.start(allocate(generation_length), generation_length);
        self.active.store(id, Ordering::Release);
        if !self.temporary {
            state.oldest_live = state.oldest_live.max(id + 1 - LIVE_GENERATIONS.min(id));
        }
        self.reclaim(&mut state);
    }

    /// Frees dropped generations, oldest first, up to the first one still pinned.
    fn reclaim(&self, state: &mut State) {
        while state.oldest_allocated < state.oldest_live
            && state.pins[slot(state.oldest_allocated)] == 0
        {
            self.generation(state.oldest_allocated).free();
            state.oldest_allocated += 1;
        }
    }
}

impl Drop for TrieNodeLog {
    fn drop(&mut self) {
        for generation in self.generations.iter() {
            generation.free();
        }
    }
}

impl Generation {
    fn start(&self, buffer: *mut u8, length: usize) {
        self.length.store(length, Ordering::Relaxed);
        self.used.store(0, Ordering::Relaxed);
        self.base.store(buffer, Ordering::Release);
    }

    fn free(&self) {
        let base = self.base.swap(std::ptr::null_mut(), Ordering::Relaxed);
        if !base.is_null() {
            free(base, self.length.load(Ordering::Relaxed));
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
        let base = self
            .log
            .generation(pointer >> OFFSET_BITS)
            .base
            .load(Ordering::Acquire);
        unsafe { std::slice::from_raw_parts(base.add((pointer & OFFSET_MASK) as usize), length) }
    }

    /// Appends the record of `node`, whose children must all be resolved, and returns its pointer.
    #[cfg(test)]
    pub fn append(&self, node: &Node) -> LogPointer {
        self.append_bytes(record_length(node), |out| write_record(node, out))
    }

    /// Appends the records of `nodes`, whose children must all be resolved, in one reservation, and pushes their
    /// pointers to `pointers`.
    pub fn append_all<'n>(
        &self,
        nodes: impl Iterator<Item = &'n Node> + Clone,
        pointers: &mut Vec<LogPointer>,
    ) {
        let first = pointers.len();
        let total = nodes
            .clone()
            .map(|node| record_length(node).next_multiple_of(RECORD_ALIGNMENT))
            .sum();
        if total == 0 {
            return;
        }
        let base = self.append_bytes(total, |out| {
            let mut offset = 0;
            for node in nodes {
                let length = record_length(node);
                write_record(node, &mut out[offset..offset + length]);
                pointers.push(offset as LogPointer);
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
        let Some(slot) = self.log.roots(address).slot(address, root) else {
            return 0;
        };
        let key = slot.load(Ordering::Acquire);
        if !self.is_valid(key) {
            return 0;
        }
        let record = self.bytes(key, ROOT_KEY_LENGTH);
        if record[..32] != address.copied().unwrap_or_default() || record[32..64] != *root {
            return 0;
        }
        let pointer = u64::from_le_bytes(record[64..].try_into().unwrap());
        match self.is_valid(pointer) {
            true => pointer,
            false => 0,
        }
    }

    /// Indexes `pointer` as the root `root` of the trie of `address`, the state trie when `None`.
    pub fn set_root(&self, address: Option<&[u8; 32]>, root: &[u8; 32], pointer: LogPointer) {
        let Some(slot) = self.log.roots(address).slot(address, root) else {
            return;
        };
        let key = self.append_bytes(ROOT_KEY_LENGTH, |out| {
            out[..32].copy_from_slice(&address.copied().unwrap_or_default());
            out[32..64].copy_from_slice(root);
            out[64..].copy_from_slice(&pointer.to_le_bytes());
        });
        slot.store(key, Ordering::Release);
    }
}

impl Drop for LogSession<'_> {
    fn drop(&mut self) {
        let mut state = self.log.state.lock().unwrap();
        state.pins[slot(self.floor)] -= 1;
        self.log.reclaim(&mut state);
    }
}

/// Root key records by a hash of the address and root hash they hold, a colliding root replacing the old one. Lookups
/// check the record, so a slot is a single pointer and needs no lock.
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
        children[9] = ChildRef::inline(&rlp(&inline_leaf));
        let branch = Node::Branch { children };
        let pointers: Vec<LogPointer> = [leaf(vec![0xaa; 40]), extension, branch]
            .iter()
            .map(|node| {
                let pointer = session.append(node);
                assert_eq!(read(&session, pointer), rlp(node));
                pointer
            })
            .collect();
        let extension = LoggedNode::new(session.record(pointers[1]));
        let branch = LoggedNode::new(session.record(pointers[2]));
        assert!(
            matches!(extension.child(0), ChildRef::Hash { pointer, .. } if pointer == leaf_pointer)
        );
        assert!(
            matches!(branch.child(3), ChildRef::Hash { pointer, .. } if pointer == leaf_pointer)
        );
        assert!(matches!(branch.child(4), ChildRef::Empty));

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

        // Roots hit on the same trie and hash only, and a colliding root replaces the old one.
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
        assert_eq!(single_session.root(Some(&address), &[0x44; 32]), 0);
        assert_eq!(
            single_session.root(Some(&[0x66; 32]), &[0x44; 32]),
            single_pointer
        );

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
