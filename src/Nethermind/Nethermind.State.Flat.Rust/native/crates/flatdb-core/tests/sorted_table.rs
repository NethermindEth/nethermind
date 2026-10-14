//! The persisted snapshot's sorted table: ports of `SortedTableTests` and `BlockTests`, and a property test of the
//! table against an ordered map.
//!
//! The C# fuzz test draws from `System.Random`; the port keeps its seeds, shape and counts over a SplitMix64 stream.

mod common;

use std::collections::BTreeMap;

use common::hex;
use flatdb_core::persisted::io::ByteReader;
use flatdb_core::persisted::sorted::block::{FLAG_BLOCK, FLAG_INDEX};
use flatdb_core::persisted::sorted::block_builder::BlockBuilder;
use flatdb_core::persisted::sorted::enumerator::SortedTableEnumerator;
use flatdb_core::persisted::sorted::table::{BLOCK_SIZE, try_read_footer};
use flatdb_core::persisted::sorted::table_builder::SortedTableBuilder;
use flatdb_core::persisted::sorted::{data_block_reader, index_block_reader, table_reader};
use flatdb_core::persisted::{TierError, TierResult};
use proptest::prelude::*;
use rstest::rstest;

/// The table of `entries`, fed in ascending key order.
fn build_table(entries: &[(Vec<u8>, Vec<u8>)]) -> Vec<u8> {
    let mut sorted = entries.to_vec();
    sorted.sort();
    let mut bytes = Vec::new();
    let mut table = SortedTableBuilder::new(&mut bytes);
    for (key, value) in &sorted {
        table.add(key, value).unwrap();
    }
    table.build().unwrap();
    bytes
}

fn seek(bytes: &[u8], key: &[u8]) -> TierResult<Option<Vec<u8>>> {
    Ok(table_reader::get(&ByteReader::new(bytes), key)?.map(<[u8]>::to_vec))
}

fn enumerate(bytes: &[u8]) -> Vec<(Vec<u8>, Vec<u8>)> {
    let reader = ByteReader::new(bytes);
    let mut enumerator = SortedTableEnumerator::new(&reader);
    let mut entries = Vec::new();
    while enumerator.move_next(&reader).unwrap() {
        let (offset, length) = enumerator.current_value();
        entries.push((
            enumerator.current_key().to_vec(),
            bytes[offset..offset + length].to_vec(),
        ));
    }
    entries
}

/// `TestFixtureHelpers.DataBlockCount`: the data blocks, which the index block follows.
fn data_block_count(bytes: &[u8]) -> usize {
    try_read_footer(&ByteReader::new(bytes))
        .unwrap()
        .index_offset
        .div_ceil(BLOCK_SIZE)
}

fn be_key(value: u32) -> Vec<u8> {
    value.to_be_bytes().to_vec()
}

fn sample_entries() -> Vec<(Vec<u8>, Vec<u8>)> {
    [
        ("00", "aa"),
        ("0000", ""),
        ("01ff", "0102030405"),
        ("7f", "01"),
        ("fe00112233", "99"),
        ("ff", "deadbeef"),
    ]
    .into_iter()
    .map(|(key, value)| (hex(key), hex(value)))
    .collect()
}

#[test]
fn round_trips_every_key_reports_misses_and_enumerates_in_order() {
    let entries = sample_entries();
    let bytes = build_table(&entries);

    for (key, value) in &entries {
        assert_eq!(seek(&bytes, key).unwrap().as_ref(), Some(value), "{key:?}");
    }
    // An absent key, a prefix of a present key, and a key past the last one.
    for missing in ["02", "0001", "ffff"] {
        assert_eq!(seek(&bytes, &hex(missing)).unwrap(), None, "{missing}");
    }

    let mut sorted = entries;
    sorted.sort();
    assert_eq!(enumerate(&bytes), sorted);
}

#[test]
fn corrupt_common_prefix_length_byte_fails_with_the_csharp_message() {
    let mut bytes = build_table(&[(hex("abcdef"), hex("1234"))]);
    assert!(seek(&bytes, &hex("abcdef")).unwrap().is_some());

    let restarts = usize::from(u16::from_le_bytes([bytes[3], bytes[4]]));
    bytes[5 + 2 * restarts] = 0xFF;

    let error = seek(&bytes, &hex("abcdef")).unwrap_err();
    assert!(
        matches!(&error, TierError::Corrupt(message) if message.starts_with("Corrupt persisted-snapshot SortedTable: ")
        && message.ends_with("The persistedSnapshot/ directory is corrupted — wipe and resync.")),
        "{error}"
    );
}

#[rstest]
#[case::descending("02", "01", false)]
#[case::duplicate("02", "02", false)]
#[case::ascending("01", "02", true)]
fn add_accepts_only_strictly_ascending_keys(#[case] first: &str, #[case] second: &str, #[case] accepted: bool) {
    let mut bytes = Vec::new();
    let mut table = SortedTableBuilder::new(&mut bytes);
    table.add(&hex(first), &hex("aa")).unwrap();
    let outcome = table.add(&hex(second), &hex("bb"));
    assert_eq!(outcome.is_ok(), accepted);
    if let Err(error) = outcome {
        assert!(matches!(error, TierError::InvalidArgument(_)), "{error}");
    }
}

#[rstest]
#[case::empty(&[], 0)]
#[case::single(&[("abcdef", "1234")], 1)]
fn small_tables_seek_and_enumerate(#[case] entries: &[(&str, &str)], #[case] blocks: usize) {
    let entries = entries
        .iter()
        .map(|(key, value)| (hex(key), hex(value)))
        .collect::<Vec<_>>();
    let bytes = build_table(&entries);

    assert_eq!(data_block_count(&bytes), blocks);
    assert_eq!(enumerate(&bytes), entries);
    for probe in ["00", "abcdee", "abcdef", "abcdff"] {
        let expected = entries
            .iter()
            .find(|(key, _)| *key == hex(probe))
            .map(|(_, value)| value.clone());
        assert_eq!(seek(&bytes, &hex(probe)).unwrap(), expected, "{probe}");
    }
}

/// One block across restart-run boundaries (interval 8) and the last-block fill.
#[rstest]
fn single_block_round_trips_across_restart_boundaries(#[values(1, 7, 8, 9, 16, 17, 24, 25, 48)] count: u32) {
    let entries = (0..count)
        .map(|i| (be_key(i), vec![i as u8, (i + 1) as u8]))
        .collect::<Vec<_>>();
    let bytes = build_table(&entries);

    assert_eq!(
        data_block_count(&bytes),
        1,
        "small values keep all records in one block"
    );
    for (key, value) in &entries {
        assert_eq!(seek(&bytes, key).unwrap().as_ref(), Some(value));
    }
    assert_eq!(seek(&bytes, &be_key(count)).unwrap(), None);
}

/// 200-byte values span many 4 KiB blocks; present keys are odd, so every even probe lands in a gap, some across a
/// block boundary, plus the before-first and after-last ones.
#[rstest]
fn multi_block_tables_round_trip_with_gaps(#[values(50, 800, 4000)] count: u32) {
    let value = (0..200).map(|i| i as u8).collect::<Vec<_>>();
    let entries = (0..count)
        .map(|i| (be_key(2 * i + 1), value.clone()))
        .collect::<Vec<_>>();
    let bytes = build_table(&entries);

    assert!(data_block_count(&bytes) > 1);
    for i in 0..count {
        assert_eq!(
            seek(&bytes, &be_key(2 * i + 1)).unwrap().as_ref(),
            Some(&value),
            "present key {i}"
        );
        assert_eq!(seek(&bytes, &be_key(2 * i)).unwrap(), None, "gap key {}", 2 * i);
    }
    assert_eq!(seek(&bytes, &be_key(2 * count)).unwrap(), None);
    assert_eq!(enumerate(&bytes), entries);
}

/// 32-byte keys sharing 30 bytes: long common prefixes, reset at every restart and block.
#[rstest]
fn long_shared_prefixes_round_trip(#[values(20, 4000, 5000)] count: u16) {
    let key = |i: u16| [vec![0xAB; 30], i.to_be_bytes().to_vec()].concat();
    let entries = (0..count)
        .map(|i| (key(i), vec![i as u8, (i >> 8) as u8]))
        .collect::<Vec<_>>();
    let bytes = build_table(&entries);

    for (key, value) in &entries {
        assert_eq!(seek(&bytes, key).unwrap().as_ref(), Some(value));
    }
    assert_eq!(enumerate(&bytes), entries);
    assert_eq!(seek(&bytes, &key(count)).unwrap(), None);
}

/// SplitMix64, a deterministic stream for the fuzz test.
struct Random(u64);

impl Random {
    fn next(&mut self) -> u64 {
        self.0 = self.0.wrapping_add(0x9E37_79B9_7F4A_7C15);
        let mut z = self.0;
        z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
        z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
        z ^ (z >> 31)
    }

    fn below(&mut self, low: usize, high: usize) -> usize {
        low + (self.next() % (high - low) as u64) as usize
    }

    fn bytes(&mut self, length: usize) -> Vec<u8> {
        (0..length).map(|_| self.next() as u8).collect()
    }
}

/// Random unique keys of 1..55 bytes and values of 0..254: block fills, restarts, separators and front coding across
/// boundaries.
#[rstest]
fn random_tables_round_trip(#[values(1, 7, 42)] seed: u64) {
    let mut random = Random(seed);
    for _ in 0..25 {
        let count = random.below(1, 1500);
        let mut map = BTreeMap::new();
        while map.len() < count {
            let key_length = random.below(1, 56);
            let value_length = random.below(0, 255);
            map.insert(random.bytes(key_length), random.bytes(value_length));
        }
        let entries = map.clone().into_iter().collect::<Vec<_>>();
        let bytes = build_table(&entries);

        for (key, value) in &entries {
            assert_eq!(seek(&bytes, key).unwrap().as_ref(), Some(value));
        }
        for _ in 0..50 {
            let probe_length = random.below(1, 56);
            let probe = random.bytes(probe_length);
            assert_eq!(seek(&bytes, &probe).unwrap(), map.get(&probe).cloned());
        }
        assert_eq!(enumerate(&bytes), entries);
    }
}

/// Every data block but the last starts at a multiple of 4 KiB; the index follows the last one and the footer locates
/// it.
#[test]
fn data_blocks_are_4k_aligned_and_the_footer_locates_the_index() {
    let entries = (0..300).map(|i| (be_key(i), vec![0u8; 200])).collect::<Vec<_>>();
    let bytes = build_table(&entries);
    let reader = ByteReader::new(&bytes);

    let footer = try_read_footer(&reader).unwrap();
    let blocks = data_block_count(&bytes);
    assert!(blocks > 1);
    for block in 0..blocks {
        assert!(
            data_block_reader::try_read_record_range(&reader, block * BLOCK_SIZE).is_some(),
            "block {block}"
        );
    }
    assert!(footer.index_offset >= (blocks - 1) * BLOCK_SIZE);
    assert!(index_block_reader::try_read_record_range(&reader, footer.index_offset).is_some());
    assert_eq!(bytes[bytes.len() - 1], 1, "format version");
}

fn build_block(restart_interval: usize, entries: &[(Vec<u8>, Vec<u8>)]) -> Vec<u8> {
    let mut block = BlockBuilder::new(restart_interval, 4096);
    for (key, value) in entries {
        block.add(key, value);
    }
    let mut bytes = Vec::new();
    block.finish(&mut bytes, FLAG_BLOCK).unwrap();
    bytes
}

fn seek_block(block: &[u8], target: &[u8]) -> Option<(Vec<u8>, Vec<u8>)> {
    let mut key = [0u8; 256];
    let ceiling = data_block_reader::seek_ceiling(&ByteReader::new(block), 0, target, &mut key).unwrap()?;
    Some((
        key[..ceiling.key_length].to_vec(),
        block[ceiling.value_offset..ceiling.value_offset + ceiling.value_length].to_vec(),
    ))
}

fn build_index_block(restart_interval: usize, entries: &[(Vec<u8>, u64)]) -> Vec<u8> {
    let mut block = BlockBuilder::new(restart_interval, 4096);
    for (key, value) in entries {
        block.add_changed_prefix_value(key, *value);
    }
    let mut bytes = Vec::new();
    block.finish(&mut bytes, FLAG_INDEX).unwrap();
    bytes
}

fn seek_index(block: &[u8], target: &[u8]) -> Option<(Vec<u8>, u64)> {
    let mut key = [0u8; 256];
    let ceiling = index_block_reader::seek_ceiling(&ByteReader::new(block), 0, target, &mut key).unwrap()?;
    Some((key[..ceiling.key_length].to_vec(), ceiling.byte_offset))
}

/// The ceiling of a data block: each key itself, the first key for a target before it, across restart runs for a
/// target in their gap (9 records at interval 8), and nothing past the last key or in an empty block.
#[test]
fn data_block_ceilings() {
    let entries = (0..8u8)
        .map(|i| (vec![i], vec![i]))
        .chain([(hex("10"), hex("ff"))])
        .collect::<Vec<_>>();
    let block = build_block(8, &entries);
    assert_eq!(block[0], FLAG_BLOCK);

    for (key, value) in &entries {
        assert_eq!(seek_block(&block, key), Some((key.clone(), value.clone())));
    }
    assert_eq!(
        seek_block(&block, &hex("0a")),
        Some((hex("10"), hex("ff"))),
        "across the restart runs"
    );
    assert_eq!(
        seek_block(&build_block(8, &entries[1..]), &hex("00")),
        Some((hex("01"), hex("01")))
    );
    assert_eq!(seek_block(&block, &hex("11")), None);
    assert_eq!(seek_block(&build_block(8, &[]), &hex("00")), None);
}

/// The index block's 4-byte offsets reach past 64 KiB.
#[test]
fn index_block_round_trips_past_64_kib() {
    let entries = (0..12_000u32)
        .map(|i| (be_key(i), u64::from(i) * 4096))
        .collect::<Vec<_>>();
    let block = build_index_block(8, &entries);
    assert_eq!(block[0], FLAG_INDEX);
    assert!(block.len() > usize::from(u16::MAX));

    for i in [0, 1, 100, 4000, 11_999] {
        assert_eq!(seek_index(&block, &entries[i].0), Some(entries[i].clone()));
    }
    assert_eq!(seek_index(&block, &be_key(12_000)), None);
}

/// Changed-prefix values over 3 restart runs of 4: restated at each head (the zero head included), the low bytes only
/// in between; a probe in a gap takes the next run's restated head.
#[test]
fn changed_prefix_values_reconstruct_the_offsets() {
    let values = [
        0,
        4096,
        8192,
        12_288,
        1_000_000,
        1_004_096,
        1_008_192,
        1_012_288,
        250_000_000,
        250_004_096,
        250_008_192,
        250_012_288,
    ];
    let entries = values
        .iter()
        .enumerate()
        .map(|(i, &value)| (vec![0x01, 2 * (i as u8 + 1)], value))
        .collect::<Vec<_>>();
    let block = build_index_block(4, &entries);

    for entry in &entries {
        assert_eq!(seek_index(&block, &entry.0), Some(entry.clone()));
    }
    assert_eq!(seek_index(&block, &hex("0100")), Some((hex("0102"), 0)));
    assert_eq!(seek_index(&block, &hex("0109")), Some((hex("010a"), 1_000_000)));
    assert_eq!(seek_index(&block, &hex("0119")), None);
}

proptest! {
    #![proptest_config(ProptestConfig::with_cases(64))]

    /// Any map of keys and values within the field limits: the scan returns it in order, every key seeks to its value
    /// and any other key misses.
    #[test]
    fn table_matches_an_ordered_map(
        map in prop::collection::btree_map(
            prop::collection::vec(any::<u8>(), 1..56),
            prop::collection::vec(any::<u8>(), 0..255),
            0..600,
        ),
        probes in prop::collection::vec(prop::collection::vec(any::<u8>(), 1..56), 0..40),
    ) {
        let entries = map.clone().into_iter().collect::<Vec<_>>();
        let bytes = build_table(&entries);

        prop_assert_eq!(enumerate(&bytes), entries.clone());
        for (key, value) in &entries {
            prop_assert_eq!(seek(&bytes, key).unwrap(), Some(value.clone()));
        }
        for probe in &probes {
            prop_assert_eq!(seek(&bytes, probe).unwrap(), map.get(probe).cloned());
        }
    }
}
