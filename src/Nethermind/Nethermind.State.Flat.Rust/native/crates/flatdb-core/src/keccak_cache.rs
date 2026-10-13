//! Port of `Nethermind.Core/Crypto/KeccakCache.std.cs`: a process-wide, direct-mapped cache of the Keccak-256 of short
//! inputs, so the account and slot keys of the hashed layouts are hashed once rather than on every read and write.
//!
//! 512k entries of 128 bytes (64 MiB, allocated on first use), as in C#. An entry is read lock-free through a seqlock:
//! read the header, speculatively read the payload and the hash, then check the header has not moved. A writer takes
//! the entry with one CAS and releases it with one store; a failed CAS skips the write, which costs one keccak later.
//! The header packs the high bits of the bucket hash, the input length, an 8-bit version (against ABA in readers) and
//! the lock bit with the C# masks. Every field is an atomic, so a read racing a write is a stale or torn copy that the
//! header check discards, never a data race.
//!
//! Differences from C#, none visible in a result: the bucket hash is a seeded multiply-mix rather than the AES
//! `FastHash` (both seeded per process); the payload is 11 eight-byte words, so inputs of 89-92 bytes are hashed
//! uncached (the codecs only hash 20-byte addresses and 32-byte slots).

use std::hash::{BuildHasher, RandomState};
use std::sync::LazyLock;
use std::sync::atomic::{AtomicU32, AtomicU64, Ordering, fence};

use crate::keccak::keccak256;
use crate::types::{Hash256, KECCAK_OF_EMPTY_STRING};

const BUCKET_MASK: u32 = 0x0007_FFFF;
/// Entries in the cache.
const COUNT: usize = BUCKET_MASK as usize + 1;
const HASH_MASK: u32 = !BUCKET_MASK;
const LOCK_MARKER: u32 = 0x0000_8000;
const VERSION_MASK: u32 = 0x0000_7F80;
const VERSION_INCREMENT: u32 = 0x0000_0080;
const HASH_LENGTH_MASK: u32 = HASH_MASK | 0x7F;

const PAYLOAD_WORDS: usize = 11;
/// Longest input the cache holds; longer and empty inputs are hashed every time.
const MAX_PAYLOAD_LENGTH: usize = PAYLOAD_WORDS * 8;

/// One cached hash. Zeroed is empty: no input has length 0. The hash comes before the input, so an address, its hash
/// and the header share the first cache line.
#[derive(Default)]
#[repr(C, align(128))]
struct Entry {
    /// Hash high bits (19-31), [`LOCK_MARKER`], version (7-14) and input length (0-6).
    combined: AtomicU32,
    keccak: [AtomicU64; 4],
    /// The input, little-endian words, the last one zero-padded.
    payload: [AtomicU64; PAYLOAD_WORDS],
}

const _: () = assert!(size_of::<Entry>() == 128);

struct Cache {
    seed: u64,
    entries: Box<[Entry]>,
}

static CACHE: LazyLock<Cache> = LazyLock::new(|| Cache {
    seed: RandomState::new().hash_one(0x4B45_4343_u64),
    entries: std::iter::repeat_with(Entry::default).take(COUNT).collect(),
});

/// `KeccakCache.Compute`: the Keccak-256 of `input`, from the cache when it holds it.
pub fn compute(input: &[u8]) -> Hash256 {
    if input.is_empty() {
        return KECCAK_OF_EMPTY_STRING;
    }

    if input.len() > MAX_PAYLOAD_LENGTH {
        return keccak256(input);
    }

    let (entry, combined) = CACHE.entry(input);
    if let Some(keccak) = entry.try_read(input, combined) {
        return keccak;
    }

    let keccak = keccak256(input);
    entry.write(input, combined, &keccak);
    keccak
}

fn words(input: &[u8]) -> impl Iterator<Item = u64> + '_ {
    let (full, rest) = input.as_chunks::<8>();
    let last = (!rest.is_empty()).then(|| rest.iter().rev().fold(0, |word, &byte| (word << 8) | u64::from(byte)));
    full.iter().map(|word| u64::from_le_bytes(*word)).chain(last)
}

fn mum(a: u64, b: u64) -> u64 {
    let product = u128::from(a) * u128::from(b);
    (product as u64) ^ ((product >> 64) as u64)
}

impl Cache {
    /// The `FastHash` stand-in: low bits pick the bucket, high bits go into the header.
    fn hash(&self, input: &[u8]) -> u32 {
        let folded = words(input).fold(self.seed ^ input.len() as u64, |hash, word| {
            mum(hash ^ word, 0x9E37_79B9_7F4A_7C15)
        });
        mum(folded, 0xD6E8_FEB8_6659_FD93) as u32
    }

    fn entry(&self, input: &[u8]) -> (&Entry, u32) {
        let hash = self.hash(input);
        let combined = (hash & HASH_MASK) | input.len() as u32;
        (&self.entries[(hash & BUCKET_MASK) as usize], combined)
    }
}

impl Entry {
    fn try_read(&self, input: &[u8], combined: u32) -> Option<Hash256> {
        let sequence = self.combined.load(Ordering::Acquire);
        if sequence & LOCK_MARKER != 0 || sequence & HASH_LENGTH_MASK != combined {
            return None;
        }

        let matches = self
            .payload
            .iter()
            .zip(words(input))
            .all(|(stored, word)| stored.load(Ordering::Relaxed) == word);
        let keccak: [u64; 4] = std::array::from_fn(|word| self.keccak[word].load(Ordering::Relaxed));
        fence(Ordering::Acquire);
        if !matches || self.combined.load(Ordering::Relaxed) != sequence {
            return None;
        }

        let mut hash = [0u8; 32];
        for (bytes, word) in hash.as_chunks_mut::<8>().0.iter_mut().zip(keccak) {
            *bytes = word.to_le_bytes();
        }
        Some(hash)
    }

    fn write(&self, input: &[u8], combined: u32, keccak: &Hash256) {
        let existing = self.combined.load(Ordering::Relaxed);
        let unlocked = combined | (((existing & VERSION_MASK) + VERSION_INCREMENT) & VERSION_MASK);
        if existing & LOCK_MARKER != 0
            || self
                .combined
                .compare_exchange(existing, unlocked | LOCK_MARKER, Ordering::Acquire, Ordering::Relaxed)
                .is_err()
        {
            return;
        }

        fence(Ordering::Release);
        for (stored, word) in self.keccak.iter().zip(words(keccak)) {
            stored.store(word, Ordering::Relaxed);
        }
        for (stored, word) in self.payload.iter().zip(words(input)) {
            stored.store(word, Ordering::Relaxed);
        }
        self.combined.store(unlocked, Ordering::Release);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use parking_lot::Mutex;
    use rstest::rstest;

    /// Serializes the tests here against each other, so one test's writes do not evict the entries another asserts are
    /// cached. Other tests in this binary still write the same process-wide cache through the codecs.
    static CACHE_TESTS: Mutex<()> = Mutex::new(());

    /// `KeccakCache.TryGet`.
    fn try_get(input: &[u8]) -> Option<Hash256> {
        if input.is_empty() || input.len() > MAX_PAYLOAD_LENGTH {
            return None;
        }

        let (entry, combined) = CACHE.entry(input);
        entry.try_read(input, combined)
    }

    fn bucket(input: &[u8]) -> u32 {
        CACHE.hash(input) & BUCKET_MASK
    }

    fn pseudo_random_bytes(seed: u64, length: usize) -> Vec<u8> {
        let mut state = seed;
        (0..length)
            .map(|_| {
                state = mum(state ^ 0xA076_1D64_78BD_642F, 0xE703_7ED1_A0B4_28DB);
                state as u8
            })
            .collect()
    }

    #[rstest]
    fn computes_the_keccak_and_caches_only_supported_lengths(
        #[values(0, 1, 4, 20, 31, 32, 63, 64, 65, 88, 89, 92, 128, 192)] length: usize,
        #[values(0, 1, 7, 15)] offset: usize,
    ) {
        let _exclusive = CACHE_TESTS.lock();
        for round in 0..100 {
            let buffer = pseudo_random_bytes((length * 16 + offset) as u64 * 1000 + round, length + offset);
            let input = &buffer[offset..];
            let expected = keccak256(input);

            assert_eq!(compute(input), expected, "first compute");
            assert_eq!(compute(input), expected, "second compute");
            let cacheable = (1..=MAX_PAYLOAD_LENGTH).contains(&length);
            assert_eq!(try_get(input), cacheable.then_some(expected), "cached");
        }
    }

    /// Plants `input` under the header of an input one byte away from it, over a longer input's stale payload words: only
    /// the payload compare separates the two, and the stale words past `input` must not count.
    #[rstest]
    fn an_entry_whose_header_matches_hits_only_for_its_own_input(#[values(1, 4, 20, 32, 64, 88)] length: usize) {
        let _exclusive = CACHE_TESTS.lock();
        let input = pseudo_random_bytes(length as u64 + 2000, length);
        let expected = keccak256(&input);
        let longer = pseudo_random_bytes(length as u64 + 3000, MAX_PAYLOAD_LENGTH);
        for position in [0, 8, length - 1].into_iter().filter(|&position| position < length) {
            let mut differing = input.clone();
            differing[position] ^= 0x01;
            let (entry, combined) = CACHE.entry(&differing);
            entry.write(&longer, CACHE.entry(&longer).1, &keccak256(&longer));
            entry.write(&input, combined, &expected);

            assert_eq!(entry.try_read(&input, combined), Some(expected), "planted {position}");
            assert_eq!(entry.try_read(&differing, combined), None, "differing {position}");
            assert_eq!(compute(&differing), keccak256(&differing), "compute {position}");
        }
    }

    #[test]
    fn spinning_through_as_many_inputs_as_entries_returns_their_keccak() {
        let _exclusive = CACHE_TESTS.lock();
        for index in 0..COUNT as u32 {
            let input = index.to_le_bytes();
            assert_eq!(compute(&input), keccak256(&input), "input {index}");
        }
    }

    #[rstest]
    fn colliding_inputs_keep_their_own_keccak_under_concurrent_reads_and_writes(#[values(20, 32, 64)] length: usize) {
        let _exclusive = CACHE_TESTS.lock();
        let first = pseudo_random_bytes(length as u64, length);
        let mut colliding = vec![first.clone()];
        let mut candidate_seed = 1u64 << 32;
        while colliding.len() < 4 {
            let candidate = pseudo_random_bytes(candidate_seed, length);
            if bucket(&candidate) == bucket(&first) {
                colliding.push(candidate);
            }
            candidate_seed += 1;
        }
        let expected: Vec<Hash256> = colliding.iter().map(|input| keccak256(input)).collect();

        std::thread::scope(|scope| {
            for thread in 0..8 {
                let (colliding, expected) = (&colliding, &expected);
                scope.spawn(move || {
                    for iteration in 0..100_000 {
                        let index = (thread + iteration) % colliding.len();
                        assert_eq!(compute(&colliding[index]), expected[index], "iteration {iteration}");
                        if let Some(cached) = try_get(&colliding[index]) {
                            assert_eq!(cached, expected[index], "cached, iteration {iteration}");
                        }
                    }
                });
            }
        });
    }
}
