//! Port of `Nethermind.State.Flat/Persistence/CarryForwardSlotTable.cs`: a fixed-size, set-associative table of
//! storage-slot reads, the slot half of [`super::carry_forward::CarryForwardCachingPersistence`].
//!
//! Reads are lock-free through a per-entry seqlock: read the header, copy the entry, re-read the header, and treat
//! any change as a miss. Every method suffixed `no_lock` mutates the table and must be called under the owner's
//! lock, so there is only ever one writer. A full set replaces one of its ways instead of the table being wiped, and
//! a wholesale clear bumps the epoch every entry is stamped with, so neither costs time proportional to the table.
//!
//! Every field of an entry is an atomic, so a read racing a write is a stale or torn copy that the header check
//! discards, never a data race. The memory is freed once the owner and every reader have released their leases;
//! entries are only reached through a [`SlotTableLease`], which is what keeps them valid.

use std::alloc::{self, Layout};
use std::hash::{BuildHasher, RandomState};
use std::ptr::NonNull;
use std::sync::atomic::{AtomicBool, AtomicI32, AtomicI64, AtomicU32, AtomicU64, Ordering, fence};
use std::sync::{Arc, LazyLock};

use crate::types::{Address, U256};

/// Ways per set.
pub const WAYS: usize = 4;
/// Largest capacity a table takes.
pub const MAX_CAPACITY: usize = 1 << 28;

const LOCK_BIT: u64 = 1;
const VERSION_INCREMENT: u64 = 2;
const VERSION_MASK: u64 = 0x0000_FFFF_FFFF_FFFE;
const TAG_MASK: u64 = 0xFFFF_0000_0000_0000;
const EMPTY_EPOCH: u32 = 0;
const FIRST_EPOCH: u32 = 1;

/// One cached read; the header, key and epoch share the first cache line, so a probe that misses reads one line per
/// way. Zeroed memory is an empty entry.
#[repr(C, align(128))]
struct Entry {
    /// Tag (bits 48-63), version (bits 1-47) and [`LOCK_BIT`].
    header: AtomicU64,
    address0: AtomicU64,
    address1: AtomicU64,
    address2: AtomicU32,
    epoch: AtomicU32,
    slot: [AtomicU64; 4],
    value: [AtomicU64; 4],
    found: AtomicU32,
}

const _: () = assert!(size_of::<Entry>() == 128);

/// Outcome of [`SlotTableLease::add_no_lock`].
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum AddResult {
    AlreadyPresent,
    Added,
    Replaced,
}

/// A slot read: whether the slot was found, and its value (zero when it was not).
pub type CachedSlot = (bool, U256);

/// The table. It is shared through [`SlotTableLease`]s; [`CarryForwardSlotTable::create`] hands out the owner's.
pub struct CarryForwardSlotTable {
    entries: NonNull<Entry>,
    capacity: usize,
    set_mask: usize,
    epoch: AtomicU32,
    /// `RefCountingDisposable`'s lease count: positive while leased, 0 then -1 once released for good.
    leases: AtomicI64,
    freed: AtomicBool,
    count: AtomicI32,
    victim_cursor: AtomicU32,
}

// SAFETY: the entries are atomics, reached only through leases that keep the allocation alive.
unsafe impl Send for CarryForwardSlotTable {}
// SAFETY: as above.
unsafe impl Sync for CarryForwardSlotTable {}

/// A lease on a [`CarryForwardSlotTable`], which keeps its memory alive until dropped.
pub struct SlotTableLease {
    table: Arc<CarryForwardSlotTable>,
}

/// Per-process seed of [`hash`], so the sets an address and slot land in cannot be ground offline.
static SEED: LazyLock<u64> = LazyLock::new(|| RandomState::new().hash_one(0x5EED_u64));

fn mum(a: u64, b: u64) -> u64 {
    let product = u128::from(a) * u128::from(b);
    (product as u64) ^ ((product >> 64) as u64)
}

/// `CarryForwardSlotTable.Hash`: a seeded 64-bit hash of an address and a slot. The set index takes the low bits and
/// the tag the top 16, so the two stay independent.
pub fn hash(address: &Address, slot: &U256) -> u64 {
    let word = |offset: usize| u64::from_le_bytes(address[offset..offset + 8].try_into().expect("8 bytes"));
    let address_tail = u64::from(u32::from_le_bytes(address[16..20].try_into().expect("4 bytes")));
    let seed = *SEED;
    let mut hash = mum(word(0) ^ seed, word(8) ^ 0x9E37_79B9_7F4A_7C15);
    hash = mum(hash ^ address_tail, slot.0[0] ^ 0xBF58_476D_1CE4_E5B9);
    hash = mum(hash ^ slot.0[1], slot.0[2] ^ 0x94D0_49BB_1331_11EB);
    hash = mum(hash ^ slot.0[3], seed ^ 0x2545_F491_4F6C_DD1D);
    mum(hash, 0xD6E8_FEB8_6659_FD93)
}

/// `CarryForwardSlotTable.TagOf`.
pub fn tag_of(hash: u64) -> u64 {
    hash & TAG_MASK
}

fn layout(capacity: usize) -> Layout {
    Layout::array::<Entry>(capacity).expect("the capacity is bounded")
}

impl CarryForwardSlotTable {
    /// A table of `capacity` entries, rounded up to a power of two of at least [`WAYS`]; returns the owner's lease.
    /// `None` for a capacity of zero or over [`MAX_CAPACITY`], where C# throws.
    pub fn create(capacity: usize) -> Option<SlotTableLease> {
        if capacity == 0 || capacity > MAX_CAPACITY {
            return None;
        }

        let capacity = capacity.max(WAYS).next_power_of_two();
        // SAFETY: the layout has a non-zero size. Zeroed memory is a valid array of empty entries, as every field is
        // an atomic integer.
        let allocation = unsafe { alloc::alloc_zeroed(layout(capacity)) };
        let entries =
            NonNull::new(allocation.cast::<Entry>()).unwrap_or_else(|| alloc::handle_alloc_error(layout(capacity)));

        Some(SlotTableLease {
            table: Arc::new(Self {
                entries,
                capacity,
                set_mask: capacity / WAYS - 1,
                epoch: AtomicU32::new(FIRST_EPOCH),
                leases: AtomicI64::new(1),
                freed: AtomicBool::new(false),
                count: AtomicI32::new(0),
                victim_cursor: AtomicU32::new(0),
            }),
        })
    }

    pub fn capacity(&self) -> usize {
        self.capacity
    }

    /// Entries stamped with the current epoch. Read it under the owner's lock.
    pub fn count(&self) -> i32 {
        self.count.load(Ordering::Relaxed)
    }

    /// Whether the memory is still allocated.
    pub fn is_allocated(&self) -> bool {
        !self.freed.load(Ordering::Acquire)
    }

    /// The epoch entries are stamped with.
    pub fn epoch(&self) -> u32 {
        self.epoch.load(Ordering::Acquire)
    }

    /// Takes a lease that keeps the memory alive until it is dropped; `None` once the table has been released for
    /// good. Port of `RefCountingLease.TryAcquire`.
    pub fn try_lease(self: &Arc<Self>) -> Option<SlotTableLease> {
        let mut current = self.leases.load(Ordering::Acquire);
        loop {
            // At 0 the last holder has begun freeing the memory, so the table cannot be resurrected.
            if current <= 0 {
                return None;
            }
            match self
                .leases
                .compare_exchange_weak(current, current + 1, Ordering::AcqRel, Ordering::Acquire)
            {
                Ok(_) => return Some(SlotTableLease { table: self.clone() }),
                Err(observed) => current = observed,
            }
        }
    }

    /// Port of `RefCountingLease.ReleaseOnce`; the last release frees the memory.
    fn release(&self) {
        let previous = self.leases.fetch_sub(1, Ordering::AcqRel);
        debug_assert!(previous > 0, "a lease was released twice");
        if previous == 1
            && self
                .leases
                .compare_exchange(0, -1, Ordering::AcqRel, Ordering::Acquire)
                .is_ok()
        {
            self.free();
        }
    }

    fn free(&self) {
        if !self.freed.swap(true, Ordering::AcqRel) {
            // SAFETY: no lease is left, so nothing reaches the entries any more, and they are freed exactly once.
            unsafe { alloc::dealloc(self.entries.as_ptr().cast(), layout(self.capacity)) };
        }
    }
}

impl Drop for CarryForwardSlotTable {
    fn drop(&mut self) {
        self.free();
    }
}

impl Drop for SlotTableLease {
    fn drop(&mut self) {
        self.table.release();
    }
}

fn address_words(address: &Address) -> (u64, u64, u32) {
    (
        u64::from_le_bytes(address[..8].try_into().expect("8 bytes")),
        u64::from_le_bytes(address[8..16].try_into().expect("8 bytes")),
        u32::from_le_bytes(address[16..20].try_into().expect("4 bytes")),
    )
}

impl Entry {
    fn key_equals(&self, address: (u64, u64, u32), slot: &U256) -> bool {
        self.address0.load(Ordering::Relaxed) == address.0
            && self.address1.load(Ordering::Relaxed) == address.1
            && self.address2.load(Ordering::Relaxed) == address.2
            && (0..4).all(|limb| self.slot[limb].load(Ordering::Relaxed) == slot.0[limb])
    }

    /// Marks the entry as being written; returns the header [`Entry::end_write`] publishes. The version moves on
    /// every write, so a reader whose copy spans one sees a different header.
    fn begin_write(&self, tag: u64) -> u64 {
        let unlocked = tag | ((self.header.load(Ordering::Relaxed) + VERSION_INCREMENT) & VERSION_MASK);
        self.header.store(unlocked | LOCK_BIT, Ordering::Relaxed);
        // None of the field stores that follow may become visible before the lock bit.
        fence(Ordering::Release);
        unlocked
    }

    fn end_write(&self, unlocked: u64) {
        self.header.store(unlocked, Ordering::Release);
    }

    fn invalidate(&self) {
        let unlocked = self.begin_write(self.header.load(Ordering::Relaxed) & TAG_MASK);
        self.epoch.store(EMPTY_EPOCH, Ordering::Relaxed);
        self.end_write(unlocked);
    }
}

impl SlotTableLease {
    pub fn table(&self) -> &Arc<CarryForwardSlotTable> {
        &self.table
    }

    /// Another lease on the same table, if it has not been released for good.
    pub fn try_lease(&self) -> Option<SlotTableLease> {
        self.table.try_lease()
    }

    fn set(&self, hash: u64) -> &[Entry] {
        let start = (hash as usize & self.table.set_mask) * WAYS;
        // SAFETY: the lease keeps the allocation of `capacity` entries alive, and the set lies within it.
        unsafe { std::slice::from_raw_parts(self.table.entries.as_ptr().add(start), WAYS) }
    }

    /// Looks up a slot read without taking a lock; an entry being written at the same time reads as a miss.
    pub fn try_get(&self, hash: u64, address: &Address, slot: &U256) -> Option<CachedSlot> {
        self.try_get_with_hook(hash, address, slot, || {})
    }

    /// [`SlotTableLease::try_get`] that runs `after_key_copy` between copying an entry's key and its value, so a
    /// test can write mid-read.
    pub(crate) fn try_get_with_hook(
        &self,
        hash: u64,
        address: &Address,
        slot: &U256,
        mut after_key_copy: impl FnMut(),
    ) -> Option<CachedSlot> {
        let tag = tag_of(hash);
        let epoch = self.table.epoch.load(Ordering::Acquire);
        let key = address_words(address);

        for entry in self.set(hash) {
            let header = entry.header.load(Ordering::Acquire);
            if header & (TAG_MASK | LOCK_BIT) != tag {
                continue;
            }

            // Speculative copy, trusted only if the header is unchanged afterwards.
            let entry_epoch = entry.epoch.load(Ordering::Relaxed);
            let key_equal = entry.key_equals(key, slot);
            after_key_copy();
            let found = entry.found.load(Ordering::Relaxed);
            let value = U256(std::array::from_fn(|limb| entry.value[limb].load(Ordering::Relaxed)));

            fence(Ordering::Acquire);
            if entry.header.load(Ordering::Relaxed) != header {
                break;
            }

            if entry_epoch == epoch && key_equal {
                return Some((found != 0, value));
            }
        }

        None
    }

    /// Caches a slot read, replacing another entry of its set when the set is full.
    pub fn add_no_lock(&self, hash: u64, address: &Address, slot: &U256, found: bool, value: &U256) -> AddResult {
        let tag = tag_of(hash);
        let epoch = self.table.epoch.load(Ordering::Relaxed);
        let key = address_words(address);
        let set = self.set(hash);

        let mut free = None;
        for (way, entry) in set.iter().enumerate() {
            if entry.epoch.load(Ordering::Relaxed) != epoch {
                free.get_or_insert(way);
            } else if entry.header.load(Ordering::Relaxed) & TAG_MASK == tag && entry.key_equals(key, slot) {
                // Readers at one generation all read the same state, so the entry already holds this value.
                return AddResult::AlreadyPresent;
            }
        }

        let (way, result) = match free {
            Some(way) => {
                self.table.count.fetch_add(1, Ordering::Relaxed);
                (way, AddResult::Added)
            }
            None => {
                let victim = self.table.victim_cursor.fetch_add(1, Ordering::Relaxed);
                (victim as usize % WAYS, AddResult::Replaced)
            }
        };

        let target = &set[way];
        let unlocked = target.begin_write(tag);
        target.epoch.store(epoch, Ordering::Relaxed);
        target.found.store(u32::from(found), Ordering::Relaxed);
        target.address0.store(key.0, Ordering::Relaxed);
        target.address1.store(key.1, Ordering::Relaxed);
        target.address2.store(key.2, Ordering::Relaxed);
        let value = if found { *value } else { U256::ZERO };
        for limb in 0..4 {
            target.slot[limb].store(slot.0[limb], Ordering::Relaxed);
            target.value[limb].store(value.0[limb], Ordering::Relaxed);
        }
        target.end_write(unlocked);
        result
    }

    /// Drops a cached slot read; returns whether the slot was cached.
    pub fn remove_no_lock(&self, hash: u64, address: &Address, slot: &U256) -> bool {
        let tag = tag_of(hash);
        let epoch = self.table.epoch.load(Ordering::Relaxed);
        let key = address_words(address);

        for entry in self.set(hash) {
            if entry.epoch.load(Ordering::Relaxed) == epoch
                && entry.header.load(Ordering::Relaxed) & TAG_MASK == tag
                && entry.key_equals(key, slot)
            {
                entry.invalidate();
                self.table.count.fetch_sub(1, Ordering::Relaxed);
                return true;
            }
        }

        false
    }

    /// Drops every cached slot read in O(1) by moving to the next epoch.
    pub fn clear_no_lock(&self) {
        self.table.count.store(0, Ordering::Relaxed);
        let epoch = self.table.epoch.load(Ordering::Relaxed);
        if epoch != u32::MAX {
            self.table.epoch.store(epoch + 1, Ordering::Release);
            return;
        }

        // The epoch wraps, and any entry still stamped with FIRST_EPOCH would read as current again.
        // SAFETY: the lease keeps the allocation of `capacity` entries alive.
        let entries = unsafe { std::slice::from_raw_parts(self.table.entries.as_ptr(), self.table.capacity) };
        for entry in entries {
            if entry.epoch.load(Ordering::Relaxed) != EMPTY_EPOCH {
                entry.invalidate();
            }
        }
        self.table.epoch.store(FIRST_EPOCH, Ordering::Release);
    }

    /// Sets the epoch, so tests can reach the wrap.
    #[cfg(test)]
    pub(crate) fn set_epoch(&self, epoch: u32) {
        self.table.epoch.store(epoch, Ordering::Release);
    }
}

/// Helpers shared by the slot table and carry-forward tests.
#[cfg(test)]
pub(crate) mod testing {
    use crate::types::U256;

    pub(crate) const PATTERN_VERSION_BITS: u32 = 40;

    /// `CarryForwardCachingPersistenceTests.Pattern`: a value for `key` written at `version` whose limbs check each
    /// other.
    pub(crate) fn pattern(key: u64, version: u64) -> U256 {
        let u0 = (key << PATTERN_VERSION_BITS) | version;
        let u1 = u0.wrapping_mul(0x9E37_79B9_7F4A_7C15);
        let u2 = u1 ^ 0xBF58_476D_1CE4_E5B9;
        U256([u0, u1, u2, !u0])
    }

    /// A small deterministic generator (PCG-style LCG) for the randomized tests.
    pub(crate) struct TestRng(u64);

    impl TestRng {
        pub(crate) fn new(seed: u64) -> Self {
            Self(seed.wrapping_mul(0x9E37_79B9_7F4A_7C15) | 1)
        }

        pub(crate) fn next_u64(&mut self) -> u64 {
            self.0 = self
                .0
                .wrapping_mul(6_364_136_223_846_793_005)
                .wrapping_add(1_442_695_040_888_963_407);
            self.0 ^ (self.0 >> 29)
        }

        /// A number in `[0, bound)`.
        pub(crate) fn below(&mut self, bound: usize) -> usize {
            ((self.next_u64() >> 11) % bound as u64) as usize
        }

        /// A number in `[0, 1)`.
        pub(crate) fn unit(&mut self) -> f64 {
            (self.next_u64() >> 11) as f64 / (1u64 << 53) as f64
        }
    }
}

#[cfg(test)]
mod tests {
    use super::testing::{PATTERN_VERSION_BITS, TestRng, pattern};
    use super::{AddResult, CarryForwardSlotTable, MAX_CAPACITY, SlotTableLease, WAYS, hash, tag_of};
    use crate::types::{Address, U256};
    use rstest::rstest;
    use std::collections::HashMap;
    use std::sync::Barrier;
    use std::sync::atomic::{AtomicBool, AtomicI64, AtomicUsize, Ordering};

    const ADDRESS_A: Address = [0xAA; 20];
    const ADDRESS_B: Address = [0xBB; 20];

    fn is_pattern(value: &U256, key: u64) -> bool {
        value.0[0] >> PATTERN_VERSION_BITS == key
            && *value == pattern(key, value.0[0] & ((1 << PATTERN_VERSION_BITS) - 1))
    }

    fn add(table: &SlotTableLease, address: &Address, slot: u64, found: bool, value: U256) -> AddResult {
        let slot = U256::from_u64(slot);
        table.add_no_lock(hash(address, &slot), address, &slot, found, &value)
    }

    fn remove(table: &SlotTableLease, address: &Address, slot: &U256) -> bool {
        table.remove_no_lock(hash(address, slot), address, slot)
    }

    fn get(table: &SlotTableLease, address: &Address, slot: u64) -> Option<(bool, U256)> {
        let slot = U256::from_u64(slot);
        table.try_get(hash(address, &slot), address, &slot)
    }

    fn table(capacity: usize) -> SlotTableLease {
        CarryForwardSlotTable::create(capacity).unwrap()
    }

    #[rstest]
    fn invalid_capacities_are_rejected(#[values(0, MAX_CAPACITY + 1)] capacity: usize) {
        assert!(CarryForwardSlotTable::create(capacity).is_none());
        assert_eq!(table(3).table().capacity(), WAYS, "rounded up to one set");
    }

    #[rstest]
    fn get_after_add_returns_the_cached_read(#[values(true, false)] found: bool) {
        let table = table(1024);
        let value = if found { pattern(1, 1) } else { U256::ZERO };

        assert_eq!(add(&table, &ADDRESS_A, 7, found, value), AddResult::Added);

        assert_eq!(get(&table, &ADDRESS_A, 7), Some((found, value)));
        assert_eq!(get(&table, &ADDRESS_A, 8), None, "another slot of the same account");
        assert_eq!(get(&table, &ADDRESS_B, 7), None, "the same slot of another account");
        assert_eq!(table.table().count(), 1);
    }

    #[test]
    fn adding_the_same_key_twice_keeps_one_entry() {
        let table = table(1024);
        assert_eq!(add(&table, &ADDRESS_A, 1, true, pattern(1, 1)), AddResult::Added);
        assert_eq!(
            add(&table, &ADDRESS_A, 1, true, pattern(1, 1)),
            AddResult::AlreadyPresent
        );
        assert_eq!(table.table().count(), 1);
    }

    #[test]
    fn a_full_set_replaces_one_way_instead_of_wiping() {
        let table = table(WAYS);
        let results: Vec<AddResult> = (0..=WAYS as u64)
            .map(|i| add(&table, &ADDRESS_A, i, true, pattern(i, 1)))
            .collect();

        let still_cached = (0..WAYS as u64)
            .filter(|&i| {
                get(&table, &ADDRESS_A, i).is_some_and(|(_, value)| {
                    assert_eq!(value, pattern(i, 1));
                    true
                })
            })
            .count();

        assert!(results[..WAYS].iter().all(|&result| result == AddResult::Added));
        assert_eq!(results[WAYS], AddResult::Replaced);
        assert!(
            get(&table, &ADDRESS_A, WAYS as u64).is_some(),
            "the newest read is cached"
        );
        assert_eq!(still_cached, WAYS - 1, "exactly one older read was replaced");
        assert_eq!(table.table().count(), WAYS as i32);
    }

    #[test]
    fn remove_drops_only_that_slot() {
        let table = table(1024);
        add(&table, &ADDRESS_A, 1, true, pattern(1, 1));
        add(&table, &ADDRESS_A, 2, true, pattern(2, 1));

        assert!(remove(&table, &ADDRESS_A, &U256::from_u64(1)));
        assert!(!remove(&table, &ADDRESS_A, &U256::from_u64(1)));
        assert_eq!(get(&table, &ADDRESS_A, 1), None);
        assert!(get(&table, &ADDRESS_A, 2).is_some());
        assert_eq!(table.table().count(), 1);
    }

    #[test]
    fn clear_hides_every_entry_and_frees_their_ways() {
        let table = table(WAYS);
        for i in 0..WAYS as u64 {
            add(&table, &ADDRESS_A, i, true, pattern(i, 1));
        }

        table.clear_no_lock();

        assert!((0..WAYS as u64).all(|i| get(&table, &ADDRESS_A, i).is_none()));
        assert_eq!(
            add(&table, &ADDRESS_A, 100, true, pattern(100, 1)),
            AddResult::Added,
            "a cleared entry is free"
        );
        assert_eq!(table.table().count(), 1);
    }

    #[test]
    fn an_epoch_wrap_does_not_bring_back_old_entries() {
        let table = table(1024);
        let first_epoch = table.table().epoch();
        add(&table, &ADDRESS_A, 1, true, pattern(1, 1));

        // Skip ahead to the last epoch; the entry above keeps its stamp and would read as current after the wrap.
        table.set_epoch(u32::MAX);
        add(&table, &ADDRESS_A, 2, true, pattern(2, 1));
        let last_epoch_visible = get(&table, &ADDRESS_A, 2).is_some();

        table.clear_no_lock();

        assert!(last_epoch_visible);
        assert_eq!(table.table().epoch(), first_epoch);
        assert_eq!(
            get(&table, &ADDRESS_A, 1),
            None,
            "stamped with the epoch the table wrapped to"
        );
        assert_eq!(get(&table, &ADDRESS_A, 2), None, "stamped with the last epoch");
        assert_eq!(table.table().count(), 0);
    }

    fn slots_with_the_same_tag(address: &Address) -> (U256, U256) {
        let mut by_tag = HashMap::new();
        for i in 0u64.. {
            let slot = U256::from_u64(i);
            if let Some(other) = by_tag.insert(tag_of(hash(address, &slot)), slot) {
                return (other, slot);
            }
        }
        unreachable!()
    }

    #[test]
    fn keys_with_the_same_set_and_tag_compare_the_full_key() {
        let table = table(WAYS);
        let (first, second) = slots_with_the_same_tag(&ADDRESS_A);
        table.add_no_lock(hash(&ADDRESS_A, &first), &ADDRESS_A, &first, true, &pattern(1, 1));
        table.add_no_lock(hash(&ADDRESS_A, &second), &ADDRESS_A, &second, true, &pattern(2, 1));

        assert_eq!(
            table.try_get(hash(&ADDRESS_A, &first), &ADDRESS_A, &first),
            Some((true, pattern(1, 1)))
        );
        assert_eq!(
            table.try_get(hash(&ADDRESS_A, &second), &ADDRESS_A, &second),
            Some((true, pattern(2, 1)))
        );
        assert_eq!(table.table().count(), 2);
    }

    #[test]
    fn an_entry_replaced_by_the_same_tag_mid_read_misses() {
        // One set and one cached key, so every write below lands on the way the read is copying.
        let table = table(WAYS);
        let (first, second) = slots_with_the_same_tag(&ADDRESS_A);
        table.add_no_lock(hash(&ADDRESS_A, &first), &ADDRESS_A, &first, true, &pattern(1, 1));

        // Between the key and value copies: 2^20 writes, which bring a version of up to 20 bits back to where the
        // read started, and leave the way holding the other key.
        let writes = 1 << 20;
        let hit = table.try_get_with_hook(hash(&ADDRESS_A, &first), &ADDRESS_A, &first, || {
            remove(&table, &ADDRESS_A, &first);
            for _ in (2..writes).step_by(2) {
                table.add_no_lock(hash(&ADDRESS_A, &second), &ADDRESS_A, &second, true, &pattern(2, 1));
                remove(&table, &ADDRESS_A, &second);
            }
            table.add_no_lock(hash(&ADDRESS_A, &second), &ADDRESS_A, &second, true, &pattern(2, 1));
        });

        assert_eq!(hit, None, "the value of the key that replaced the one being read");
        assert_eq!(
            table.try_get(hash(&ADDRESS_A, &second), &ADDRESS_A, &second),
            Some((true, pattern(2, 1)))
        );
    }

    #[rstest]
    #[case::last_address_byte({ let mut a = ADDRESS_A; a[19] ^= 1; a }, U256([1, 2, 3, 4]))]
    #[case::first_address_byte({ let mut a = ADDRESS_A; a[0] ^= 1; a }, U256([1, 2, 3, 4]))]
    #[case::top_slot_bit(ADDRESS_A, U256([1, 2, 3, 4 ^ (1 << 63)]))]
    #[case::bottom_slot_bit(ADDRESS_A, U256([1 ^ 1, 2, 3, 4]))]
    fn a_key_differing_in_one_bit_misses(#[case] probed_address: Address, #[case] probed_slot: U256) {
        let table = table(1024);
        let slot = U256([1, 2, 3, 4]);
        table.add_no_lock(hash(&ADDRESS_A, &slot), &ADDRESS_A, &slot, true, &pattern(1, 1));

        assert_eq!(
            table.try_get(hash(&probed_address, &probed_slot), &probed_address, &probed_slot),
            None
        );
    }

    #[test]
    fn memory_is_freed_only_after_the_last_lease() {
        let owner = table(1024);
        let shared = owner.table().clone();
        let lease = owner.try_lease().expect("leased");

        drop(owner);
        let allocated_while_leased = shared.is_allocated();
        drop(lease);

        assert!(allocated_while_leased);
        assert!(!shared.is_allocated());
        assert!(shared.try_lease().is_none(), "no lease once released for good");
    }

    /// Port of `TryGet_WhileEntriesAreRewritten_NeverReturnsATornEntry`: one set and more keys than ways, so the
    /// writer both rewrites keys in place and replaces them with others while readers copy them.
    #[test]
    fn reads_racing_rewrites_never_return_a_torn_entry() {
        const KEYS: u64 = WAYS as u64 + 2;
        const TOTAL_READS: usize = 4_000_000;
        let readers = std::thread::available_parallelism()
            .map_or(1, |n| n.get().saturating_sub(1))
            .clamp(1, 8);
        let table = table(WAYS);
        let addresses: Vec<Address> = (0..KEYS).map(|i| [i as u8 + 1; 20]).collect();
        let start = Barrier::new(readers + 1);
        let (done, stop) = (AtomicUsize::new(0), AtomicBool::new(false));
        let (hits, torn) = (AtomicI64::new(0), AtomicI64::new(0));

        std::thread::scope(|scope| {
            scope.spawn(|| {
                let mut random = TestRng::new(1);
                let mut version = 0u64;
                start.wait();
                while done.load(Ordering::Acquire) < readers && !stop.load(Ordering::Relaxed) {
                    let key = random.below(KEYS as usize) as u64;
                    version += 1;
                    let slot = U256::from_u64(key);
                    remove(&table, &addresses[key as usize], &slot);
                    // Every other write caches a missing slot, so a found flag copied apart from its value shows up.
                    add(
                        &table,
                        &addresses[key as usize],
                        key,
                        version & 1 == 0,
                        pattern(key, version),
                    );
                }
            });

            for reader in 0..readers {
                let (table, addresses, start, done, hits, torn) = (&table, &addresses, &start, &done, &hits, &torn);
                scope.spawn(move || {
                    let mut random = TestRng::new(reader as u64 + 2);
                    let (mut reader_hits, mut reader_torn) = (0, 0);
                    start.wait();
                    for _ in 0..TOTAL_READS / readers {
                        let key = random.below(KEYS as usize) as u64;
                        let Some((found, value)) = get(table, &addresses[key as usize], key) else {
                            continue;
                        };
                        reader_hits += 1;
                        if if found {
                            !is_pattern(&value, key)
                        } else {
                            value != U256::ZERO
                        } {
                            reader_torn += 1;
                        }
                    }
                    hits.fetch_add(reader_hits, Ordering::Relaxed);
                    torn.fetch_add(reader_torn, Ordering::Relaxed);
                    done.fetch_add(1, Ordering::Release);
                });
            }
        });
        stop.store(true, Ordering::Relaxed);

        assert_eq!(torn.load(Ordering::Relaxed), 0);
        assert!(
            hits.load(Ordering::Relaxed) > 0,
            "readers hit entries while they were being rewritten"
        );
    }
}
