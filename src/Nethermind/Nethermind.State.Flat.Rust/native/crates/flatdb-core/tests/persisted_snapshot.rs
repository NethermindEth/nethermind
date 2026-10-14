//! Persisted snapshots built from in-memory ones and read back: ports of `PersistedSnapshotTests`, with a property test
//! of build-then-read against the in-memory snapshot.
//!
//! The persisted snapshots live in memory (a table buffer and blob buffers); the files under them are the storage
//! layer's. Not ported here, as they test that layer: `ActivePersistedSnapshotCount_TracksConstructionAndDisposal`,
//! `BlobArena_FrontierResets_WhenLastPersistedSnapshotDisposes` and the `AdviseDontNeed` re-reads of
//! `RoundTrips_AcrossSlotCounts`. `Nested_snapshot_sorting_preserves_all_node_buckets` runs once: its worker budget
//! axis has no counterpart in the single-sort builder.

mod common;

use std::sync::Arc;

use common::persisted::{MemSnapshot, convert, merge, new_blobs};
use common::snapshots::{address, snapshot, state_id};
use common::{hex, to_hex};
use flatdb_core::bloom::BloomFilter;
use flatdb_core::keccak::keccak256;
use flatdb_core::persisted::io::ByteReader;
use flatdb_core::persisted::list::PersistedSnapshotList;
use flatdb_core::persisted::reader::{read_blob_range, read_ref_ids};
use flatdb_core::persisted::scanner::Scanner;
use flatdb_core::persisted::sorted::table_builder::SortedTableBuilder;
use flatdb_core::persisted::stack::{PersistedLayer, PersistedSnapshotStack, StackSlot};
use flatdb_core::persisted::{TierError, bloom_keys, merger};
use flatdb_core::snapshot::content::SnapshotContentBuilder;
use flatdb_core::snapshot::{Snapshot, SnapshotTier};
use flatdb_core::types::{Account, Address, TreePath, U256};
use hashbrown::HashMap;
use proptest::prelude::*;
use rstest::rstest;

const BITS_PER_KEY: f64 = 14.0;

fn path(seed: &str, length: u8) -> TreePath {
    TreePath::new(keccak256(seed.as_bytes()), length)
}

fn account(nonce: u64, balance: u64) -> Account {
    Account::new(nonce, U256::from_u64(balance))
}

/// A value whose big-endian bytes are zero but for `byte` at `index`.
fn value_with(index: usize, byte: u8) -> U256 {
    let mut bytes = [0u8; 32];
    bytes[index] = byte;
    U256::from_be_bytes(&bytes)
}

fn persist(fill: impl FnOnce(&mut SnapshotContentBuilder)) -> (Arc<Snapshot>, MemSnapshot) {
    let snapshot = snapshot(state_id(0, 0), state_id(1, 1), fill);
    let (persisted, _) = convert(&snapshot, &new_blobs(), 0, BITS_PER_KEY);
    (snapshot, persisted)
}

/// `PersistedSnapshotUtils.ValidatePersistedSnapshot`: every entry of `snapshot` reads back from `persisted`, and the
/// scans see exactly them.
fn assert_reads_back(snapshot: &Snapshot, persisted: &MemSnapshot) {
    for (address, account) in snapshot.accounts() {
        assert_eq!(
            persisted.try_get_account(address).unwrap(),
            Some(*account),
            "account {address:?}"
        );
    }
    for (address, is_new) in snapshot.self_destructed_storage_addresses() {
        assert_eq!(
            persisted.try_get_self_destruct_flag(address).unwrap(),
            Some(is_new),
            "flag {address:?}"
        );
    }
    for (key, value) in snapshot.storages() {
        assert_eq!(
            persisted.try_get_slot(&key.address, &key.slot).unwrap(),
            Some(*value),
            "slot {key:?}"
        );
    }
    let mut nodes = 0;
    for (path, node) in snapshot
        .state_nodes()
        .filter(|(_, node)| !node.is_hash_only_placeholder())
    {
        assert_eq!(
            persisted.try_load_state_node_rlp(path).unwrap().as_deref(),
            Some(node.rlp),
            "{path:?}"
        );
        nodes += 1;
    }
    for (key, node) in snapshot
        .storage_nodes()
        .filter(|(_, node)| !node.is_hash_only_placeholder())
    {
        assert_eq!(
            persisted
                .try_load_storage_node_rlp(&key.address_hash, &key.path)
                .unwrap()
                .as_deref(),
            Some(node.rlp),
            "{key:?}"
        );
        nodes += 1;
    }

    let reader = ByteReader::new(&persisted.table);
    let scan = scan(&reader);
    let addresses = snapshot
        .accounts()
        .map(|(address, _)| *address)
        .chain(
            snapshot
                .self_destructed_storage_addresses()
                .map(|(address, _)| *address),
        )
        .chain(snapshot.storages().map(|(key, _)| key.address))
        .collect::<std::collections::BTreeSet<_>>();
    assert_eq!(scan.addresses.len(), addresses.len());
    assert_eq!(scan.slots.len(), snapshot.storages_count());
    assert_eq!(scan.nodes, nodes);
}

#[derive(Default)]
struct Scan {
    addresses: HashMap<Address, (bool, Option<Account>, Option<bool>)>,
    slots: HashMap<(Address, U256), Option<U256>>,
    nodes: usize,
}

fn scan(reader: &ByteReader<'_>) -> Scan {
    let scanner = Scanner::new(reader);
    let mut result = Scan::default();
    let mut per_addresses = scanner.per_addresses();
    while let Some(entry) = per_addresses.next_address().unwrap() {
        let account = per_addresses.account().unwrap();
        result
            .addresses
            .insert(entry.address, (entry.has_account, account, entry.self_destruct_flag));
        while let Some(slot) = per_addresses.next_slot().unwrap() {
            result.slots.insert((entry.address, slot.slot_index()), slot.value);
        }
    }
    for mut nodes in [scanner.state_nodes(), scanner.storage_nodes()] {
        while nodes.next_node().unwrap().is_some() {
            result.nodes += 1;
        }
    }
    result
}

#[test]
fn many_nodes_in_every_path_form_read_back() {
    let (snapshot, persisted) = persist(|c| {
        for i in 0..256 {
            let path = TreePath::new(keccak256(i.to_string().as_bytes()), [3, 8, 20][i % 3]);
            c.set_state_node(path, [0x11; 32], &[0xC2, 0x80, 0x80]);
            c.set_storage_node(keccak256(b"A"), path, [0x22; 32], &[0xC2, 0x80, 0x80]);
        }
    });
    assert_reads_back(&snapshot, &persisted);
}

#[rstest]
#[case::account(|c: &mut SnapshotContentBuilder| c.set_account(address(0xA), Some(account(0, 1000))))]
#[case::self_destruct(|c: &mut SnapshotContentBuilder| c.set_self_destruct(address(0xA), false))]
#[case::state_node_top(|c: &mut SnapshotContentBuilder| c.set_state_node(path("path", 4), [1; 32], &[0xC2, 0x80, 0x80]))]
#[case::state_node_compact(|c: &mut SnapshotContentBuilder| c.set_state_node(path("path", 8), [1; 32], &[0xC2, 0x80, 0x80]))]
#[case::state_node_long(|c: &mut SnapshotContentBuilder| c.set_state_node(path("longpath", 20), [1; 32], &[0xC2, 0x80, 0x81]))]
#[case::single_slot(|c: &mut SnapshotContentBuilder| c.set_storage(address(0xA), U256::from_u64(42), Some(value_with(31, 0xFF))))]
#[case::small_single_byte_slot(|c: &mut SnapshotContentBuilder| c.set_storage(address(0xA), U256::from_u64(9), Some(value_with(31, 0x05))))]
#[case::zero_slot(|c: &mut SnapshotContentBuilder| c.set_storage(address(0xA), U256::ZERO, Some(value_with(31, 0xAB))))]
#[case::null_slot(|c: &mut SnapshotContentBuilder| {
    c.set_storage(address(0xA), U256::from_u64(1), None);
    c.set_storage(address(0xA), U256::from_u64(2), Some(value_with(31, 0xFF)));
})]
#[case::multiple_addresses(|c: &mut SnapshotContentBuilder| {
    c.set_storage(address(0xA), U256::from_u64(1), Some(value_with(31, 1)));
    c.set_storage(address(0xA), U256::from_u64(2), Some(value_with(31, 2)));
    c.set_storage(address(0xB), U256::from_u64(5), Some(value_with(31, 3)));
})]
#[case::storage_node_top(|c: &mut SnapshotContentBuilder| c.set_storage_node(keccak256(b"address"), path("path", 4), [1; 32], &[0xC1, 0x80]))]
#[case::storage_node_compact(|c: &mut SnapshotContentBuilder| c.set_storage_node(keccak256(b"address"), path("path", 6), [1; 32], &[0xC1, 0x80]))]
#[case::storage_node_long(|c: &mut SnapshotContentBuilder| c.set_storage_node(keccak256(b"address"), path("longpath", 18), [1; 32], &[0xC3, 0x80, 0x81, 0x82]))]
#[case::all_data_types(|c: &mut SnapshotContentBuilder| {
    c.set_account(address(0xA), Some(account(7, 12345)));
    c.set_account(
        address(0xB),
        Some(Account::with_hashes(0, U256::ZERO, keccak256(b"storage"), keccak256(&[0x60, 0x00]))),
    );
    c.set_account(address(0xC), None);
    c.set_storage(address(0xA), U256::from_u64(1), Some(value_with(31, 0xFF)));
    c.set_storage(address(0xA), U256::from_u64(2), Some(value_with(0, 0x01)));
    c.set_storage(address(0xB), U256::from_u64(42), None);
    c.set_self_destruct(address(0xD), false);
    c.set_self_destruct(address(0xE), true);
    c.set_state_node(path("tp", 3), [1; 32], &[0xC1, 0x80]);
    c.set_state_node(path("sp", 8), [2; 32], &[0xC2, 0x80, 0x80]);
    c.set_state_node(path("lp", 20), [3; 32], &[0xC2, 0x80, 0x81]);
    let owner = keccak256(b"storageAddr");
    c.set_storage_node(owner, path("tsp", 3), [4; 32], &[0xC1, 0x80]);
    c.set_storage_node(owner, path("ssp", 6), [5; 32], &[0xC1, 0x80]);
    c.set_storage_node(owner, path("lsp", 18), [6; 32], &[0xC3, 0x80, 0x81, 0x82]);
    c.set_state_node(path("placeholder", 5), [7; 32], &[]);
})]
fn every_kind_of_entry_round_trips(#[case] fill: fn(&mut SnapshotContentBuilder)) {
    let (snapshot, persisted) = persist(fill);
    assert_reads_back(&snapshot, &persisted);
}

/// The scanner across every entry kind: values of every width, deleted and zero slots, a deleted account, both
/// self-destruct markers, and nodes in all three path forms.
#[test]
fn full_scan_decodes_every_entry_kind() {
    let full = U256::from_be_bytes(&std::array::from_fn(|i| i as u8 + 1));
    let (_, persisted) = persist(|c| {
        c.set_account(address(0xA), Some(account(3, 1000)));
        c.set_account(address(0xC), None);
        c.set_storage(address(0xA), U256::from_u64(1), Some(value_with(31, 0x05)));
        c.set_storage(address(0xA), U256::from_u64(2), Some(value_with(31, 0xFF)));
        c.set_storage(address(0xA), U256::from_u64(3), None);
        c.set_storage(address(0xB), U256::from_u64(4), Some(full));
        c.set_storage(address(0xB), U256::from_u64(5), Some(U256::ZERO));
        c.set_self_destruct(address(0xD), false);
        c.set_self_destruct(address(0xE), true);
        for (seed, length) in [("st-top", 3), ("st-mid", 8), ("st-long", 20)] {
            c.set_state_node(path(seed, length), [1; 32], &[0xC2, 0x80, 0x80]);
        }
        for (seed, length) in [("sn-top", 3), ("sn-mid", 6), ("sn-long", 18)] {
            c.set_storage_node(keccak256(b"storage-addr"), path(seed, length), [2; 32], &[0xC1, 0x82]);
        }
    });

    let reader = ByteReader::new(&persisted.table);
    let scan = scan(&reader);
    assert_eq!(scan.addresses[&address(0xA)], (true, Some(account(3, 1000)), None));
    assert_eq!(
        scan.addresses[&address(0xC)],
        (true, None, None),
        "a deleted account keeps its marker"
    );
    assert_eq!(scan.addresses[&address(0xD)], (false, None, Some(false)));
    assert_eq!(scan.addresses[&address(0xE)], (false, None, Some(true)));
    assert_eq!(
        scan.slots[&(address(0xA), U256::from_u64(1))],
        Some(value_with(31, 0x05))
    );
    assert_eq!(
        scan.slots[&(address(0xA), U256::from_u64(2))],
        Some(value_with(31, 0xFF))
    );
    assert_eq!(scan.slots[&(address(0xA), U256::from_u64(3))], None, "a deleted slot");
    assert_eq!(scan.slots[&(address(0xB), U256::from_u64(4))], Some(full));
    assert_eq!(
        scan.slots[&(address(0xB), U256::from_u64(5))],
        Some(U256::ZERO),
        "zero is not deleted"
    );
    assert_eq!(scan.nodes, 6);

    let scanner = Scanner::new(&reader);
    let mut storage_nodes = scanner.storage_nodes();
    let mut decoded = Vec::new();
    while let Some(node) = storage_nodes.next_node().unwrap() {
        decoded.push((node.address_hash, node.path));
    }
    let mut owner = keccak256(b"storage-addr");
    owner[20..].fill(0);
    let mut expected = [("sn-top", 3), ("sn-mid", 6), ("sn-long", 18)]
        .map(|(seed, length)| {
            // The table keeps 20 bytes of the address hash, and a top path's 4 bytes without its length nibble.
            let mut path = path(seed, length);
            if length <= 7 {
                path.path[4..].fill(0);
                path.path[3] &= 0xF0;
            }
            (owner, path)
        })
        .to_vec();
    expected.sort_by_key(|(_, path)| path.length <= 7);
    assert_eq!(decoded.len(), 3);
    assert_eq!(decoded[0], expected[0], "the fallback sub-column sorts first");
    assert!(expected.iter().all(|entry| decoded.contains(entry)), "{decoded:?}");
}

/// An account with slots over many blocks streams them all, then the scan reaches the next address; and a group whose
/// slots are left undrained is skipped to the next one.
#[rstest]
fn per_address_scan_streams_and_skips_slots(#[values(200, 3000)] slot_count: u64, #[values(false, true)] drain: bool) {
    let (big, next) = (address(0x11), address(0x22));
    let (_, persisted) = persist(|c| {
        c.set_account(big, Some(account(7, 1000)));
        for i in 1..=slot_count {
            c.set_storage(big, U256::from_u64(i), Some(U256::from_u64(i)));
        }
        c.set_account(next, Some(account(0, 2000)));
        c.set_storage(next, U256::from_u64(42), Some(U256::from_u64(0x2A)));
    });

    let reader = ByteReader::new(&persisted.table);
    let scanner = Scanner::new(&reader);
    let mut per_addresses = scanner.per_addresses();
    let mut seen = Vec::new();
    let mut slots = Vec::new();
    while let Some(entry) = per_addresses.next_address().unwrap() {
        seen.push((entry.address, per_addresses.account().unwrap()));
        if drain || entry.address == next {
            while let Some(slot) = per_addresses.next_slot().unwrap() {
                slots.push((entry.address, slot.slot_index(), slot.value));
            }
        }
    }

    assert_eq!(seen, [(big, Some(account(7, 1000))), (next, Some(account(0, 2000)))]);
    let expected = (1..=slot_count)
        .filter(|_| drain)
        .map(|i| (big, U256::from_u64(i), Some(U256::from_u64(i))))
        .chain([(next, U256::from_u64(42), Some(U256::from_u64(0x2A)))])
        .collect::<Vec<_>>();
    assert_eq!(slots, expected);
}

/// Absent columns and sub-columns are skipped: a top state node only, a fallback storage node only, no address.
#[test]
fn scans_skip_absent_columns() {
    let (_, persisted) = persist(|c| {
        c.set_state_node(path("only-top", 3), [1; 32], &[0xC1, 0x80]);
        c.set_storage_node(
            keccak256(b"absent-storage"),
            path("only-fallback", 18),
            [2; 32],
            &[0xC3, 0x80, 0x81, 0x82],
        );
    });
    let reader = ByteReader::new(&persisted.table);
    let scanner = Scanner::new(&reader);

    assert!(scanner.per_addresses().next_address().unwrap().is_none());
    let mut state = scanner.state_nodes();
    assert_eq!(state.next_node().unwrap().map(|node| node.path.length), Some(3));
    assert!(state.next_node().unwrap().is_none());
    let mut storage = scanner.storage_nodes();
    assert_eq!(storage.next_node().unwrap().map(|node| node.path.length), Some(18));
    assert!(storage.next_node().unwrap().is_none());
}

/// A key no writer produces fails every scan, the bloom rebuild and the merge as corrupt rather than panicking: a short
/// ref-id, a short account, a short slot, a storage node with a 3-byte top path, a fallback path of 65 nibbles, a key
/// of an unknown column.
#[rstest]
#[case("0001")]
#[case("fe1111111111111111111111111111111111111111")]
#[case("fe1111111111111111111111111111111111111111ff2222222222")]
#[case("fa3333333333333333333333333333333333333333ff123456")]
#[case("fb444444444444444444444444444444444444444444444444444444444444444441")]
#[case("5000")]
fn misshapen_keys_are_corrupt(#[case] key: &str) {
    let key = hex(key);
    let mut table = Vec::new();
    let mut builder = SortedTableBuilder::new(&mut table);
    builder.add(&key, &[0x01]).unwrap();
    builder.build().unwrap();
    let reader = ByteReader::new(&table);
    let scanner = Scanner::new(&reader);

    let expected = format!(
        "the {}-byte key {} does not have the shape of its column",
        key.len(),
        to_hex(&key)
    );
    let failures = [
        read_ref_ids(&reader).err(),
        scanner.per_addresses().next_address().err(),
        scanner.state_nodes().next_node().err(),
        scanner.storage_nodes().next_node().err(),
        bloom_keys::rebuild(&reader, BITS_PER_KEY).err(),
        merger::merge(
            std::slice::from_ref(&reader),
            &mut Vec::new(),
            &mut BloomFilter::always_true(),
        )
        .err(),
    ];
    for failure in failures {
        assert!(
            matches!(&failure, Some(TierError::Corrupt(message)) if message.contains(&expected)),
            "{failure:?}"
        );
    }
}

/// Misses at every level, on a populated snapshot and on an empty one.
#[rstest]
fn absent_keys_miss(#[values(false, true)] empty: bool) {
    let owner = keccak256(b"sh");
    let (_, persisted) = persist(|c| {
        if !empty {
            c.set_account(address(0xA), Some(account(0, 5)));
            c.set_account(address(0xC), Some(account(0, 9)));
            c.set_storage(address(0xA), U256::from_u64(1), Some(U256::from_u64(7)));
            c.set_self_destruct(address(0xA), true);
            c.set_state_node(path("sp", 4), [1; 32], &[0xC1, 0x80]);
            c.set_storage_node(owner, path("stp", 4), [2; 32], &[0xC1, 0x81]);
        }
    });

    assert_eq!(persisted.try_get_account(&address(0xB)).unwrap(), None);
    assert_eq!(persisted.try_get_slot(&address(0xB), &U256::from_u64(1)).unwrap(), None);
    assert_eq!(persisted.try_get_self_destruct_flag(&address(0xB)).unwrap(), None);
    assert_eq!(
        persisted.try_get_slot(&address(0xA), &U256::from_u64(999)).unwrap(),
        None
    );
    assert_eq!(persisted.try_get_slot(&address(0xC), &U256::from_u64(1)).unwrap(), None);
    assert_eq!(persisted.try_get_self_destruct_flag(&address(0xC)).unwrap(), None);
    assert_eq!(persisted.try_load_state_node_rlp(&path("absent", 4)).unwrap(), None);
    assert_eq!(
        persisted
            .try_load_storage_node_rlp(&keccak256(b"nope"), &path("stp", 4))
            .unwrap(),
        None
    );
    assert_eq!(
        persisted
            .try_load_storage_node_rlp(&owner, &path("absentSameTier", 4))
            .unwrap(),
        None
    );
    assert_eq!(
        persisted
            .try_load_storage_node_rlp(&owner, &path("absentDeep", 18))
            .unwrap(),
        None
    );

    assert_eq!(persisted.try_get_account(&address(0xA)).unwrap().is_some(), !empty);
    assert_eq!(
        persisted
            .try_load_storage_node_rlp(&owner, &path("stp", 4))
            .unwrap()
            .is_some(),
        !empty
    );
    let reader = ByteReader::new(&persisted.table);
    assert_eq!(
        read_blob_range(&reader).unwrap().is_empty(),
        empty,
        "only a base that wrote nodes has a blob run"
    );
    assert_eq!(
        read_ref_ids(&reader).unwrap(),
        [0],
        "a base references its blob file even when it wrote nothing"
    );
}

/// The stack probes newest first across every kind: the newest hit wins, an older-only key resolves after a newer
/// miss, misses fall through, and the self-destruct boundary clears slots at and below it.
#[test]
fn stack_probes_newest_first_across_all_kinds() {
    let blobs = new_blobs();
    let (s0, s1, s2) = (state_id(0, 0), state_id(1, 1), state_id(2, 2));
    let owner = keccak256(b"st-sh");
    let older = snapshot(s0, s1, |c| {
        c.set_account(address(0xA), Some(account(0, 100)));
        c.set_account(address(0xD), Some(account(0, 40)));
        c.set_storage(address(0xA), U256::from_u64(1), Some(U256::from_u64(0x11)));
        c.set_self_destruct(address(0xA), false);
        c.set_state_node(path("st-p", 4), [1; 32], &[0xC1, 0x80]);
        c.set_storage_node(owner, path("st-sp", 4), [2; 32], &[0xC1, 0x81]);
    });
    let newer = snapshot(s1, s2, |c| {
        c.set_account(address(0xA), Some(account(0, 200)));
        c.set_account(address(0xB), Some(account(0, 7)));
        c.set_storage(address(0xA), U256::from_u64(2), Some(U256::from_u64(0x22)));
    });
    let stack = PersistedSnapshotStack::new(PersistedSnapshotList::new(vec![
        Arc::new(convert(&older, &blobs, 0, BITS_PER_KEY).0),
        Arc::new(convert(&newer, &blobs, 0, BITS_PER_KEY).0),
    ]));

    assert_eq!(
        stack.try_get_account(&address(0xA)).unwrap(),
        Some(Some(account(0, 200)))
    );
    assert_eq!(
        stack.try_get_account(&address(0xD)).unwrap(),
        Some(Some(account(0, 40)))
    );
    assert_eq!(stack.try_get_account(&address(0xF)).unwrap(), None);
    assert_eq!(stack.try_get_self_destruct(&address(0xA)).unwrap(), Some(0));
    assert_eq!(stack.try_get_self_destruct(&address(0xF)).unwrap(), None);

    let slot = |slot: u64, boundary: isize| {
        stack
            .try_get_slot(&address(0xA), &U256::from_u64(slot), boundary)
            .unwrap()
    };
    assert_eq!(slot(2, -1), StackSlot::Found(Some(U256::from_u64(0x22))));
    assert_eq!(slot(1, -1), StackSlot::Found(Some(U256::from_u64(0x11))));
    assert_eq!(
        slot(999, 0),
        StackSlot::Cleared,
        "below the self-destruct boundary the slot is cleared"
    );
    assert_eq!(slot(999, -1), StackSlot::Missing);
    assert_eq!(
        slot(1, 1),
        StackSlot::Cleared,
        "a boundary at the newer snapshot hides the older value"
    );

    assert_eq!(
        stack.try_load_state_rlp(&path("st-p", 4)).unwrap(),
        Some(vec![0xC1, 0x80])
    );
    assert_eq!(stack.try_load_state_rlp(&path("nope-st", 4)).unwrap(), None);
    assert_eq!(
        stack.try_load_storage_rlp(&owner, &path("st-sp", 4)).unwrap(),
        Some(vec![0xC1, 0x81])
    );
    assert_eq!(stack.try_load_storage_rlp(&owner, &path("nope-sp", 4)).unwrap(), None);

    // A list probed newest first resolves the newest RLP of a path two snapshots hold.
    let both = snapshot(s1, s2, |c| c.set_state_node(path("st-p", 4), [3; 32], &[0xC1, 0x81]));
    let list = PersistedSnapshotList::new(vec![
        Arc::new(convert(&older, &blobs, 0, BITS_PER_KEY).0),
        Arc::new(convert(&both, &blobs, 0, BITS_PER_KEY).0),
    ]);
    let newest = list
        .iter()
        .rev()
        .find_map(|layer| layer.try_load_state_node_rlp(&path("st-p", 4)).unwrap());
    assert_eq!(newest, Some(vec![0xC1, 0x81]));
}

fn merge_two(
    older: impl FnOnce(&mut SnapshotContentBuilder),
    newer: impl FnOnce(&mut SnapshotContentBuilder),
) -> MemSnapshot {
    let blobs = new_blobs();
    let first = convert(
        &snapshot(state_id(0, 0), state_id(1, 1), older),
        &blobs,
        0,
        BITS_PER_KEY,
    )
    .0;
    let second = convert(
        &snapshot(state_id(1, 1), state_id(2, 2), newer),
        &blobs,
        0,
        BITS_PER_KEY,
    )
    .0;
    merge(
        &[&first, &second],
        SnapshotTier::PersistedLargeCompacted,
        BloomFilter::new(16, BITS_PER_KEY).unwrap(),
    )
}

#[test]
fn merged_slots_of_overlapping_addresses_take_the_newest_value() {
    let merged = merge_two(
        |c| {
            c.set_storage(address(0xA), U256::from_u64(1), Some(U256::from_u64(1)));
            c.set_storage(address(0xB), U256::from_u64(5), Some(U256::from_u64(2)));
        },
        |c| {
            c.set_storage(address(0xA), U256::from_u64(1), Some(U256::from_u64(3)));
            c.set_storage(address(0xA), U256::from_u64(2), Some(U256::from_u64(2)));
        },
    );
    for (address, slot, value) in [(address(0xA), 1, 3), (address(0xA), 2, 2), (address(0xB), 5, 2)] {
        assert_eq!(
            merged.try_get_slot(&address, &U256::from_u64(slot)).unwrap(),
            Some(Some(U256::from_u64(value)))
        );
    }
}

#[rstest]
#[case::null_overrides_value(Some(0xFF), None, Some(None))]
#[case::explicit_zero_overrides_value(Some(7), Some(0), Some(Some(0)))]
#[case::value_overrides_null(None, Some(0xFF), Some(Some(0xFF)))]
fn merged_null_slots(#[case] older: Option<u64>, #[case] newer: Option<u64>, #[case] expected: Option<Option<u64>>) {
    let merged = merge_two(
        |c| c.set_storage(address(0xA), U256::from_u64(1), older.map(U256::from_u64)),
        |c| c.set_storage(address(0xA), U256::from_u64(1), newer.map(U256::from_u64)),
    );
    assert_eq!(
        merged.try_get_slot(&address(0xA), &U256::from_u64(1)).unwrap(),
        expected.map(|value| value.map(U256::from_u64))
    );
}

#[test]
fn merged_null_slot_is_kept_beside_a_newer_slot() {
    let merged = merge_two(
        |c| c.set_storage(address(0xA), U256::from_u64(1), None),
        |c| c.set_storage(address(0xA), U256::from_u64(2), Some(U256::from_u64(0xFF))),
    );
    assert_eq!(
        merged.try_get_slot(&address(0xA), &U256::from_u64(1)).unwrap(),
        Some(None)
    );
    assert_eq!(
        merged.try_get_slot(&address(0xA), &U256::from_u64(2)).unwrap(),
        Some(Some(U256::from_u64(0xFF)))
    );
}

/// An account, its self-destruct, a storage node and many slots round-trip, the table spanning many pages.
#[rstest]
fn round_trips_across_slot_counts(#[values(4, 400, 4000)] slot_count: u64) {
    let owner = keccak256(&address(0xA));
    let (snapshot, persisted) = persist(|c| {
        c.set_account(address(0xA), Some(account(11, 987_654_321)));
        c.set_self_destruct(address(0xA), true);
        c.set_storage_node(owner, path("warmup-spath", 6), [1; 32], &[0xC3, 0x80, 0x81, 0x82]);
        for i in 1..=slot_count {
            c.set_storage(address(0xA), U256::from_u64(i), Some(U256::from_u64(i)));
        }
    });

    assert_reads_back(&snapshot, &persisted);
    assert_eq!(persisted.try_get_account(&address(0xB)).unwrap(), None);
    assert!(persisted.table.len() > if slot_count > 100 { 4096 } else { 0 });
}

/// The in-memory snapshot of a property case: each address, slot and path drawn from small sets, so keys collide.
fn content_strategy() -> impl Strategy<Value = Vec<(u8, u8, u8, Option<u8>)>> {
    prop::collection::vec((0u8..7, 0u8..4, 0u8..40, prop::option::of(any::<u8>())), 0..120)
}

fn build_content(operations: &[(u8, u8, u8, Option<u8>)]) -> Arc<Snapshot> {
    snapshot(state_id(0, 0), state_id(1, 1), |c| {
        for &(kind, who, what, value) in operations {
            let target = address(0xA0 + who);
            let node_path = TreePath::new(keccak256(&[what]), what % 40 + 1);
            let rlp = value.map_or(Vec::new(), |value| vec![0xC2, 0x80, value]);
            match kind {
                0 => c.set_account(target, value.map(|value| account(u64::from(value), u64::from(what)))),
                1 => c.set_self_destruct(target, value.is_some()),
                2 | 3 => c.set_storage(
                    target,
                    U256::from_u64(u64::from(what)),
                    value.map(|value| U256::from_u64(u64::from(value))),
                ),
                4 => c.set_state_node(node_path, [what; 32], &rlp),
                _ => c.set_storage_node(keccak256(&target), node_path, [what; 32], &rlp),
            }
        }
    })
}

proptest! {
    #![proptest_config(ProptestConfig::with_cases(64))]

    /// Whatever the in-memory snapshot holds reads back from the persisted one, as the read model of the snapshot
    /// reads it, the scans see exactly it, and the bloom holds every key a read probes.
    #[test]
    fn build_then_read_matches_the_in_memory_snapshot(operations in content_strategy()) {
        let snapshot = build_content(&operations);
        let (persisted, output) = convert(&snapshot, &new_blobs(), 3, BITS_PER_KEY);
        assert_reads_back(&snapshot, &persisted);

        let nodes = snapshot.state_nodes().filter(|(_, node)| !node.is_hash_only_placeholder()).count()
            + snapshot.storage_nodes().filter(|(_, node)| !node.is_hash_only_placeholder()).count();
        prop_assert_eq!(output.written_nodes.len(), nodes);
        prop_assert_eq!(output.may_have_self_destructs, snapshot.self_destructed_storage_addresses().next().is_some());
        let probes = operations.iter().map(|&(_, who, what, _)| (address(0xA0 + who), what));
        for (target, what) in probes {
            let address_key = flatdb_core::persisted::bloom_keys::address_key(&target);
            let slot = U256::from_u64(u64::from(what));
            if snapshot.try_get_storage(&target, &slot).is_some() {
                prop_assert!(persisted.bloom.might_contain(flatdb_core::persisted::bloom_keys::slot_key(address_key, &slot.to_be_bytes())));
            }
        }
    }
}
