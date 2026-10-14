//! Port of `Nethermind.State.Flat/Persistence/BloomFilter/BloomFilter.cs`: a cache-local Bloom filter after RocksDB's,
//! each key setting `k` bits of one 64-byte line.
//!
//! Only the single-writer form is ported (`AddUnsynchronized`, `MightContain`): the in-memory slot filter is filled by
//! one thread before it is published. The probe sequence is the C# scalar path, which its AVX2 query reproduces.

use thiserror::Error;

const LINE_BYTES: usize = 64;
const MUL32: u32 = 0x9E37_79B9;
/// Bits of the hash that pick a bit of a 512-bit line.
const LINE_BIT_SHIFT: u32 = 32 - 9;

#[derive(Debug, Error, Clone, PartialEq)]
pub enum BloomError {
    #[error("Capacity must be positive, got {0}.")]
    Capacity(i64),
    #[error("BitsPerKey must be a finite value > 0, got {0}.")]
    BitsPerKey(f64),
    #[error("Cannot allocate a Bloom filter of {0} bytes.")]
    OutOfMemory(usize),
}

#[repr(C, align(64))]
#[derive(Clone, Copy, Default)]
struct Line([u64; 8]);

pub struct BloomFilter {
    lines: Vec<Line>,
    probes: u32,
    count: u64,
}

impl BloomFilter {
    /// A filter sized for `capacity` keys at `bits_per_key`, rounded up to whole 64-byte lines.
    ///
    /// Fails, rather than aborting, when the memory cannot be allocated, as C# surfaces `OutOfMemoryException`.
    pub fn new(capacity: i64, bits_per_key: f64) -> Result<Self, BloomError> {
        if capacity <= 0 {
            return Err(BloomError::Capacity(capacity));
        }
        if !bits_per_key.is_finite() || bits_per_key <= 0.0 {
            return Err(BloomError::BitsPerKey(bits_per_key));
        }

        let data_bytes = ((capacity as f64 * bits_per_key / 8.0).ceil() as usize).next_multiple_of(LINE_BYTES);
        let line_count = data_bytes / LINE_BYTES;
        let mut lines = Vec::new();
        lines
            .try_reserve_exact(line_count)
            .map_err(|_| BloomError::OutOfMemory(data_bytes))?;
        lines.resize(line_count, Line::default());

        Ok(Self {
            lines,
            probes: choose_num_probes(bits_per_key),
            count: 0,
        })
    }

    /// Port of `AlwaysTrue`: one line with every bit set, so [`Self::might_contain`] never filters a key out. Its
    /// count starts at 0, as the C# sentinel's.
    pub fn always_true() -> Self {
        let mut filter = Self::new(1, 1.0).expect("one line of one bit per key is a valid filter");
        filter.lines.fill(Line([u64::MAX; 8]));
        filter
    }

    /// The bytes of filter data, a multiple of 64.
    pub fn data_bytes(&self) -> usize {
        self.lines.len() * LINE_BYTES
    }

    /// Keys added so far.
    pub fn count(&self) -> u64 {
        self.count
    }

    /// Port of `AddUnsynchronized`.
    pub fn add(&mut self, key: u64) {
        let (line, mut hash) = self.line_and_hash(key);
        let lanes = &mut self.lines[line].0;
        for _ in 0..self.probes {
            let bit = hash >> LINE_BIT_SHIFT;
            lanes[(bit >> 6) as usize] |= 1u64 << (bit & 63);
            hash = hash.wrapping_mul(MUL32);
        }
        self.count += 1;
    }

    pub fn might_contain(&self, key: u64) -> bool {
        let (line, mut hash) = self.line_and_hash(key);
        let lanes = &self.lines[line].0;
        for _ in 0..self.probes {
            let bit = hash >> LINE_BIT_SHIFT;
            if lanes[(bit >> 6) as usize] & (1u64 << (bit & 63)) == 0 {
                return false;
            }
            hash = hash.wrapping_mul(MUL32);
        }
        true
    }

    /// Port of `GetLineAndHashState`: one 64-bit mix split into the line (a fast range reduction of its low half) and
    /// the probe hash (its high half).
    fn line_and_hash(&self, key: u64) -> (usize, u32) {
        let mixed = mix64(key);
        let line_count = self.lines.len() as u64;
        let line = if line_count <= u64::from(u32::MAX) {
            (u64::from(mixed as u32) * line_count) >> 32
        } else {
            ((u128::from(mixed) * u128::from(line_count)) >> 64) as u64
        };
        (line as usize, (mixed >> 32) as u32)
    }
}

/// Port of `ChooseNumProbesRocks`; `Math.Round` rounds half to even.
fn choose_num_probes(bits_per_key: f64) -> u32 {
    let millibits = (bits_per_key * 1000.0).round_ties_even() as i64;
    match millibits {
        ..=2080 => 1,
        2081..=3580 => 2,
        3581..=5100 => 3,
        5101..=6640 => 4,
        6641..=8300 => 5,
        8301..=10070 => 6,
        10071..=11720 => 7,
        11721..=14001 => 8,
        14002..=16050 => 9,
        16051..=18300 => 10,
        18301..=22001 => 11,
        22002..=25501 => 12,
        50001.. => 24,
        _ => ((millibits - 1) / 2000 - 1) as u32,
    }
}

/// Port of `Mix64`, the MurmurHash3 finalizer.
fn mix64(mut x: u64) -> u64 {
    x ^= x >> 33;
    x = x.wrapping_mul(0xff51_afd7_ed55_8ccd);
    x ^= x >> 33;
    x = x.wrapping_mul(0xc4ce_b9fe_1a85_ec53);
    x ^= x >> 33;
    x
}

#[cfg(test)]
mod tests {
    use super::{BloomError, BloomFilter, choose_num_probes};
    use rstest::rstest;

    #[rstest]
    #[case(1.0, 1)]
    #[case(2.08, 1)]
    #[case(2.081, 2)]
    #[case(10.0, 6)]
    #[case(14.0, 8)]
    #[case(25.501, 12)]
    #[case(30.0, 13)]
    #[case(50.0, 23)]
    #[case(50.001, 24)]
    fn probes_follow_the_rocksdb_table(#[case] bits_per_key: f64, #[case] probes: u32) {
        assert_eq!(choose_num_probes(bits_per_key), probes);
    }

    #[test]
    fn sizing_rounds_to_lines_and_rejects_bad_arguments() {
        assert_eq!(BloomFilter::new(1, 1.0).unwrap().data_bytes(), 64);
        assert_eq!(BloomFilter::new(1000, 14.0).unwrap().data_bytes(), 1792);
        assert_eq!(BloomFilter::new(0, 14.0).err(), Some(BloomError::Capacity(0)));
        assert!(matches!(
            BloomFilter::new(1, f64::INFINITY),
            Err(BloomError::BitsPerKey(_))
        ));
        assert!(matches!(BloomFilter::new(1, f64::NAN), Err(BloomError::BitsPerKey(_))));
        assert!(matches!(BloomFilter::new(1, 0.0), Err(BloomError::BitsPerKey(_))));
    }

    #[test]
    fn always_true_contains_every_key_in_one_line_and_counts_from_zero() {
        let filter = BloomFilter::always_true();

        assert_eq!((filter.data_bytes(), filter.count()), (64, 0));
        assert!((0..10_000u64).all(|key| filter.might_contain(key.wrapping_mul(0x9E37_79B9_7F4A_7C15))));
    }

    #[rstest]
    fn added_keys_are_never_missed_and_absent_keys_mostly_are(#[values(1.0, 10.0, 14.0)] bits_per_key: f64) {
        let mut filter = BloomFilter::new(10_000, bits_per_key).unwrap();
        let keys = (0..10_000u64).map(|i| i.wrapping_mul(0x9E37_79B9_7F4A_7C15));
        keys.clone().for_each(|key| filter.add(key));

        assert_eq!(filter.count(), 10_000);
        assert!(keys.clone().all(|key| filter.might_contain(key)));
        let false_positives = (0..10_000u64).filter(|i| filter.might_contain(!i)).count();
        assert!(
            false_positives < if bits_per_key > 1.0 { 300 } else { 9_000 },
            "{false_positives}"
        );
    }
}
