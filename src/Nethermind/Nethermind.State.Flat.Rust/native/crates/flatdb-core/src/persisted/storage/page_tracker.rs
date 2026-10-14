//! Port of `Nethermind.State.Flat/PersistedSnapshots/Storage/PageResidencyTracker.cs`: an 8-way set-associative clock
//! (second chance) over the OS pages of the arena mappings, which bounds how many of them the tier keeps resident.
//!
//! A slot packs `REF | VALID | arenaId (30 bits) | pageIdx (32 bits)`. A hit is lock-free: it scans the 8 ways of the
//! key's set and arms the REF bit. A miss takes the set's spin bit and runs the clock: rescan, take an empty way, or
//! advance the set's hand clearing REF bits until an unreferenced way is evicted. Concurrent missers may evict
//! different victims; a redundant `MADV_DONTNEED` of a page is harmless.

use std::sync::atomic::{AtomicI64, AtomicU32, AtomicU64, Ordering};

use crate::persisted::sys::os_page_size;

const REF_BIT: u64 = 1 << 63;
const VALID_BIT: u64 = 1 << 62;
/// Compares a slot with a packed key: strips REF, keeps VALID, the arena id and the page.
const KEY_MASK: u64 = !REF_BIT;
const ARENA_ID_MASK: u64 = 0x3FFF_FFFF;
const WAYS: usize = 8;
const META_LOCK_BIT: u32 = 1 << 7;
const META_HAND_MASK: u32 = 0x7;
/// The slots `try_pick_resident_page` probes before giving up, which bounds its cost on a mostly empty tracker.
const MAX_WARM_PROBE: usize = 16;

/// `TouchOutcome`.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum TouchOutcome {
    /// The set already held the page.
    Hit,
    /// An empty way of the set took the page.
    Inserted,
    /// The page displaced the clock victim, this `(arena id, page)`.
    Evicted(i32, u32),
}

/// One set: a 64-byte cache line of 8 ways, so threads touching different sets never share a line.
#[repr(align(64))]
#[derive(Default)]
struct Set([AtomicU64; WAYS]);

pub struct PageResidencyTracker {
    sets: Box<[Set]>,
    /// Per set: bit 7 is the miss-path spin lock, bits 0..2 the clock hand.
    meta: Box<[AtomicU32]>,
    resident_pages: AtomicI64,
    /// Advanced by every `try_pick_resident_page` probe, modulo the slot count.
    warm_hand: AtomicU64,
}

impl PageResidencyTracker {
    /// `FromByteBudget`: one slot per OS page of the budget; a budget of 0 or less disables the tracker.
    pub fn from_byte_budget(bytes: i64) -> Self {
        let capacity = if bytes <= 0 {
            0
        } else {
            (bytes / os_page_size() as i64).min(i64::from(i32::MAX))
        };
        Self::new(capacity as usize)
    }

    /// A tracker of at least `max_capacity` slots, rounded up to a power-of-two number of sets; 0 disables it.
    pub fn new(max_capacity: usize) -> Self {
        let set_count = if max_capacity == 0 {
            0
        } else {
            max_capacity.div_ceil(WAYS).next_power_of_two()
        };
        Self {
            sets: (0..set_count).map(|_| Set::default()).collect(),
            meta: (0..set_count).map(|_| AtomicU32::new(0)).collect(),
            resident_pages: AtomicI64::new(0),
            warm_hand: AtomicU64::new(0),
        }
    }

    pub fn max_capacity(&self) -> usize {
        self.sets.len() * WAYS
    }

    /// `MetadataBytes`: the slot lines and the per-set metadata.
    pub fn metadata_bytes(&self) -> i64 {
        (self.sets.len() * (size_of::<Set>() + size_of::<AtomicU32>())) as i64
    }

    /// `ResidentBytes`: the tracked pages times the OS page size.
    pub fn resident_bytes(&self) -> i64 {
        if self.sets.is_empty() {
            return 0;
        }
        self.resident_pages.load(Ordering::Relaxed) * os_page_size() as i64
    }

    /// `Count`: the occupied slots.
    pub fn count(&self) -> usize {
        self.sets
            .iter()
            .flat_map(|set| &set.0)
            .filter(|slot| slot.load(Ordering::Acquire) & VALID_BIT != 0)
            .count()
    }

    /// `TryTouch`: records the page as recently used. A disabled tracker reports every touch as a hit.
    pub fn try_touch(&self, arena_id: i32, page: u32) -> TouchOutcome {
        let Some((set_index, key)) = self.locate(arena_id, page) else {
            return TouchOutcome::Hit;
        };
        let ways = &self.sets[set_index].0;
        for way in ways {
            let slot = way.load(Ordering::Acquire);
            if slot & KEY_MASK == key {
                if slot & REF_BIT == 0 {
                    way.fetch_or(REF_BIT, Ordering::AcqRel);
                }
                return TouchOutcome::Hit;
            }
        }
        self.miss_path(set_index, key)
    }

    fn miss_path(&self, set_index: usize, key: u64) -> TouchOutcome {
        let ways = &self.sets[set_index].0;
        let meta = &self.meta[set_index];
        let _lock = SetLock::acquire(meta);

        // Another thread may have inserted the key while this one spun.
        for way in ways {
            let slot = way.load(Ordering::Acquire);
            if slot & KEY_MASK == key {
                way.store(slot | REF_BIT, Ordering::Release);
                return TouchOutcome::Hit;
            }
        }

        // New arrivals arm REF so they survive the first clock pass.
        for way in ways {
            if way.load(Ordering::Acquire) == 0 {
                way.store(key | REF_BIT, Ordering::Release);
                let resident = self.resident_pages.fetch_add(1, Ordering::Relaxed) + 1;
                debug_assert!(resident as usize <= self.max_capacity());
                return TouchOutcome::Inserted;
            }
        }

        // A full set: one pass clears at most 8 REF bits, so the second finds a victim.
        let mut hand = (meta.load(Ordering::Relaxed) & META_HAND_MASK) as usize;
        for _ in 0..2 * WAYS {
            let slot = ways[hand].load(Ordering::Acquire);
            if slot & REF_BIT != 0 {
                ways[hand].store(slot & !REF_BIT, Ordering::Release);
                hand = (hand + 1) % WAYS;
                continue;
            }

            ways[hand].store(key | REF_BIT, Ordering::Release);
            let next_hand = ((hand + 1) % WAYS) as u32;
            meta.store(
                (meta.load(Ordering::Relaxed) & !META_HAND_MASK) | next_hand,
                Ordering::Relaxed,
            );
            return TouchOutcome::Evicted(((slot >> 32) & ARENA_ID_MASK) as i32, slot as u32);
        }
        unreachable!("the clock found no victim in two passes")
    }

    /// `Forget`: removes the page if it is tracked, returning whether it was.
    pub fn forget(&self, arena_id: i32, page: u32) -> bool {
        let Some((set_index, key)) = self.locate(arena_id, page) else {
            return false;
        };
        for way in &self.sets[set_index].0 {
            let mut observed = way.load(Ordering::Acquire);
            // A REF flip racing the exchange is retried; a replacement of the key ends the attempt.
            while observed & KEY_MASK == key {
                match way.compare_exchange(observed, 0, Ordering::AcqRel, Ordering::Acquire) {
                    Ok(_) => {
                        self.resident_pages.fetch_sub(1, Ordering::Relaxed);
                        return true;
                    }
                    Err(current) => observed = current,
                }
            }
        }
        false
    }

    pub fn contains_page(&self, arena_id: i32, page: u32) -> bool {
        self.locate(arena_id, page).is_some_and(|(set_index, key)| {
            self.sets[set_index]
                .0
                .iter()
                .any(|way| way.load(Ordering::Acquire) & KEY_MASK == key)
        })
    }

    /// `TryPickResidentPage`: the next occupied slot under the keep-warm hand, within a budget of probes.
    pub fn try_pick_resident_page(&self) -> Option<(i32, u32)> {
        let slot_mask = (self.max_capacity() as u64).checked_sub(1)?;
        (0..MAX_WARM_PROBE).find_map(|_| {
            let hand = self.warm_hand.fetch_add(1, Ordering::Relaxed) + 1;
            let index = (hand & slot_mask) as usize;
            let slot = self.sets[index / WAYS].0[index % WAYS].load(Ordering::Acquire);
            (slot & VALID_BIT != 0).then_some((((slot >> 32) & ARENA_ID_MASK) as i32, slot as u32))
        })
    }

    /// The set of a page and its packed key, or `None` when the tracker is disabled.
    fn locate(&self, arena_id: i32, page: u32) -> Option<(usize, u64)> {
        if self.sets.is_empty() {
            return None;
        }
        debug_assert!(arena_id as u32 as u64 & !ARENA_ID_MASK == 0, "arena id past 30 bits");
        let key = VALID_BIT | ((arena_id as u64 & ARENA_ID_MASK) << 32) | u64::from(page);
        // Fibonacci mix: its high bits spread the dense small arena ids and pages better than the low bits do.
        let mix = (key.wrapping_mul(0x9E37_79B9_7F4A_7C15) >> 32) as u32;
        Some((mix as usize & (self.sets.len() - 1), key))
    }
}

/// The per-set spin lock of the miss path, released on drop.
struct SetLock<'a>(&'a AtomicU32);

impl<'a> SetLock<'a> {
    fn acquire(meta: &'a AtomicU32) -> Self {
        loop {
            let observed = meta.load(Ordering::Relaxed);
            if observed & META_LOCK_BIT == 0
                && meta
                    .compare_exchange_weak(observed, observed | META_LOCK_BIT, Ordering::Acquire, Ordering::Relaxed)
                    .is_ok()
            {
                return Self(meta);
            }
            std::hint::spin_loop();
        }
    }
}

impl Drop for SetLock<'_> {
    fn drop(&mut self) {
        self.0.fetch_and(!META_LOCK_BIT, Ordering::Release);
    }
}
