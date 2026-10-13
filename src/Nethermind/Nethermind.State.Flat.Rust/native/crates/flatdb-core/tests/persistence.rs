//! The persistence of every layout over every backend: ports of `PersistenceScenario`, `ClearColumnsTests`,
//! `SlotRlpEncodingTests` and `PreimageStorageKeyTests`, plus the reader cache and the memtable sizing seen from the
//! store. RocksDB cases need the `rocksdb-it` feature and pass without running otherwise.

mod common;

use common::any_kv::{AnyKv, Backend, TestStore};
use common::faulty::{Faults, FaultyKv};
use common::{column_entries, hex, hex20};
use flatdb_core::codec::keys::storage_key;
use flatdb_core::codec::metadata::{LAYOUT_KEY, SLOT_ENCODING_KEY, SLOT_ENCODING_RAW, SLOT_ENCODING_RLP};
use flatdb_core::keccak::{account_path, keccak256, slot_path};
use flatdb_core::kv::mem::MemKv;
use flatdb_core::kv::{Column, KvError, KvStore, ReadFlags, WriteFlags};
use flatdb_core::log_ring::{LogRecord, LogSink, LogSource};
use flatdb_core::persistence::base::{LiveColumn, read_wiped_for_sync};
use flatdb_core::persistence::cached_reader::{CachedReader, CachedReaderPersistence};
use flatdb_core::persistence::carry_forward::CarryForwardCachingPersistence;
use flatdb_core::persistence::layouts::KvPersistence;
use flatdb_core::persistence::write_buffer::max_write_buffer_size;
use flatdb_core::persistence::{
    FlatIterator, Persistence, PersistenceError, PersistenceReader, PersistenceWriteBatch, ReaderFlags,
};
use flatdb_core::types::{Account, Address, FlatLayout, Hash256, StateId, TreePath, U256};
use log::{Level, LevelFilter};
use rstest::rstest;

const MEM: Backend = Backend::Mem;
const MANAGED: Backend = Backend::Managed;
const ROCKS: Backend = Backend::Rocks;

const FLAT: FlatLayout = FlatLayout::Flat;
const FLAT_IN_TRIE: FlatLayout = FlatLayout::FlatInTrie;
const PREIMAGE_V1: FlatLayout = FlatLayout::PreimageFlatV1;
const PREIMAGE: FlatLayout = FlatLayout::PreimageFlat;

const PRE_GENESIS: StateId = StateId::PRE_GENESIS;

fn address(byte: u8) -> Address {
    [byte; 20]
}

fn address_a() -> Address {
    address(0xA1)
}

fn address_b() -> Address {
    address(0xB2)
}

fn address_c() -> Address {
    address(0xC3)
}

/// `TestItem.GenerateIndexedAccount`.
fn indexed_account(index: u64) -> Account {
    Account::new(index, U256::from_u64(index))
}

/// `BaseFlatPersistence.DecodeSlotValue` of literal bytes.
fn value(bytes: &[u8]) -> U256 {
    U256::from_be_slice(bytes)
}

fn open(store: &TestStore, layout: FlatLayout) -> KvPersistence<AnyKv> {
    KvPersistence::new(store.kv.clone(), layout, None).unwrap()
}

/// Writes one batch from and to the pre-genesis state, as the scenarios do.
fn write<P: Persistence>(persistence: &P, ops: impl FnOnce(&mut P::WriteBatch)) {
    write_between(persistence, PRE_GENESIS, PRE_GENESIS, WriteFlags::NONE, ops);
}

fn write_between<P: Persistence>(
    persistence: &P,
    from: StateId,
    to: StateId,
    flags: WriteFlags,
    ops: impl FnOnce(&mut P::WriteBatch),
) {
    let mut batch = persistence.create_write_batch(&from, &to, flags).unwrap();
    ops(&mut batch);
    batch.commit().unwrap();
}

fn reader<P: Persistence>(persistence: &P) -> P::Reader {
    persistence.create_reader(ReaderFlags::NONE).unwrap()
}

/// `PersistenceScenario.GetSlot`: the minimal big-endian value, or `None` when absent.
fn slot(reader: &impl PersistenceReader, address: &Address, slot: impl Into<U256>) -> Option<Vec<u8>> {
    reader.try_get_slot(address, &slot.into()).unwrap().map(|value| {
        let (bytes, start) = value.to_minimal_be();
        bytes[start..].to_vec()
    })
}

fn count(mut iterator: impl FlatIterator) -> usize {
    let mut count = 0;
    while iterator.move_next().unwrap() {
        count += 1;
    }
    count
}

fn state_rlp(reader: &impl PersistenceReader, path: &str) -> Option<Vec<u8>> {
    reader
        .try_load_state_rlp(&TreePath::from_hex(path), ReadFlags::NONE)
        .unwrap()
}

fn storage_rlp(reader: &impl PersistenceReader, account: &Hash256, path: &str) -> Option<Vec<u8>> {
    reader
        .try_load_storage_rlp(account, &TreePath::from_hex(path), ReadFlags::NONE)
        .unwrap()
}

#[rstest]
fn accounts_are_written_removed_and_read_at_their_snapshot(
    #[values(MEM, MANAGED, ROCKS)] backend: Backend,
    #[values(FLAT, FLAT_IN_TRIE, PREIMAGE_V1, PREIMAGE)] layout: FlatLayout,
) {
    let Some(store) = backend.open() else { return };
    let persistence = open(&store, layout);
    let address = address_a();
    assert_eq!(reader(&persistence).get_account(&address).unwrap(), None);

    let mut readers = Vec::new();
    for index in 0..3 {
        write(&persistence, |batch| {
            batch.set_account(&address, Some(&indexed_account(index))).unwrap();
            batch
                .set_storage(&address, &U256::from_u64(1), Some(&value(&[1])))
                .unwrap();
        });
        readers.push(reader(&persistence));
    }
    write(&persistence, |batch| batch.set_account(&address, None).unwrap());

    for (index, reader) in readers.iter().enumerate() {
        assert_eq!(
            reader.get_account(&address).unwrap(),
            Some(indexed_account(index as u64))
        );
    }
    let after_removal = reader(&persistence);
    assert_eq!(after_removal.get_account(&address).unwrap(), None);
    assert_eq!(
        slot(&after_removal, &address, 1u64),
        Some(vec![1]),
        "storage stays unless removed"
    );
    assert_eq!(after_removal.is_preimage_mode(), layout.is_preimage());
    assert_eq!(after_removal.current_state(), PRE_GENESIS);
}

#[rstest]
fn self_destruct_removes_only_the_account_storage(
    #[values(MEM, MANAGED, ROCKS)] backend: Backend,
    #[values(FLAT, FLAT_IN_TRIE, PREIMAGE_V1, PREIMAGE)] layout: FlatLayout,
) {
    let Some(store) = backend.open() else { return };
    let persistence = open(&store, layout);
    let (address, address2) = (address_a(), address_b());
    let slots = [U256::ZERO, U256::from_u64(123), U256::MAX];
    write(&persistence, |batch| {
        for (index, target) in [address, address2].iter().enumerate() {
            batch.set_account(target, Some(&indexed_account(index as u64))).unwrap();
            for (i, slot) in slots.iter().enumerate() {
                batch.set_storage(target, slot, Some(&value(&[i as u8 + 1]))).unwrap();
            }
        }
    });
    let before = reader(&persistence);

    write(&persistence, |batch| batch.self_destruct(&address).unwrap());

    let after = reader(&persistence);
    for (i, &index) in slots.iter().enumerate() {
        assert_eq!(slot(&before, &address, index), Some(vec![i as u8 + 1]));
        assert_eq!(slot(&after, &address, index), None);
        assert_eq!(slot(&after, &address2, index), Some(vec![i as u8 + 1]));
    }
}

#[rstest]
fn storage_is_written_isolated_per_account_and_read_at_its_snapshot(
    #[values(MEM, MANAGED, ROCKS)] backend: Backend,
    #[values(FLAT, FLAT_IN_TRIE, PREIMAGE_V1, PREIMAGE)] layout: FlatLayout,
) {
    let Some(store) = backend.open() else { return };
    let persistence = open(&store, layout);
    let address = address_a();
    write(&persistence, |batch| {
        batch.set_account(&address, Some(&indexed_account(0))).unwrap()
    });
    let empty = reader(&persistence);

    write(&persistence, |batch| {
        batch
            .set_storage(&address, &U256::ZERO, Some(&value(&[1, 2, 3])))
            .unwrap();
        batch
            .set_storage(&address, &U256::from_u64(42), Some(&value(&[0x42])))
            .unwrap();
        batch
            .set_storage(
                &address,
                &U256::from_u64(12345),
                Some(&value(&[0x10, 0x20, 0x30, 0x40])),
            )
            .unwrap();
        batch
            .set_storage(&address, &U256::MAX, Some(&value(&[0xff, 0xfe, 0xfd])))
            .unwrap();
        for (target, byte) in [(address_a(), 0x11), (address_b(), 0x22), (address_c(), 0x33)] {
            batch
                .set_storage(&target, &U256::from_u64(7), Some(&value(&[byte])))
                .unwrap();
        }
    });
    let written = reader(&persistence);
    write(&persistence, |batch| {
        batch
            .set_storage(&address_b(), &U256::from_u64(7), Some(&value(&[0xff])))
            .unwrap();
        batch
            .set_storage(&address, &U256::from_u64(42), Some(&U256::ZERO))
            .unwrap();
        batch.set_storage(&address, &U256::from_u64(12345), None).unwrap();
    });
    let updated = reader(&persistence);

    assert_eq!(slot(&empty, &address, U256::ZERO), None);
    assert_eq!(slot(&empty, &address, U256::MAX), None);
    assert_eq!(slot(&written, &address, U256::ZERO), Some(vec![1, 2, 3]));
    assert_eq!(slot(&written, &address, 42u64), Some(vec![0x42]));
    assert_eq!(slot(&written, &address, 12345u64), Some(vec![0x10, 0x20, 0x30, 0x40]));
    assert_eq!(slot(&written, &address, U256::MAX), Some(vec![0xff, 0xfe, 0xfd]));
    assert_eq!(slot(&written, &address_b(), 7u64), Some(vec![0x22]));
    assert_eq!(slot(&updated, &address_a(), 7u64), Some(vec![0x11]));
    assert_eq!(slot(&updated, &address_b(), 7u64), Some(vec![0xff]));
    assert_eq!(slot(&updated, &address_c(), 7u64), Some(vec![0x33]));
    assert_eq!(
        slot(&updated, &address, 42u64),
        Some(vec![0]),
        "zero is present, unlike a deleted slot"
    );
    assert_eq!(slot(&updated, &address, 12345u64), None);
}

/// Port of `TestRawOperations`; the preimage layouts reject raw access instead of being skipped.
#[rstest]
fn raw_operations_round_trip_except_in_preimage_mode(
    #[values(MEM, MANAGED, ROCKS)] backend: Backend,
    #[values(FLAT, FLAT_IN_TRIE, PREIMAGE_V1, PREIMAGE)] layout: FlatLayout,
) {
    let Some(store) = backend.open() else { return };
    let persistence = open(&store, layout);
    let account = indexed_account(0);
    let address_hash = account_path(&address_a());
    let slot_hash = keccak256(&[1, 2, 3]);
    let stored = hex("0x000000ffffffffffffffffffffffffffffffffffffffffffffffffffffffffff");

    if layout.is_preimage() {
        let mut batch = persistence
            .create_write_batch(&PRE_GENESIS, &PRE_GENESIS, WriteFlags::NONE)
            .unwrap();
        assert!(matches!(
            batch.set_account_raw(&address_hash, &account),
            Err(PersistenceError::InvalidOperation(_))
        ));
        assert!(matches!(
            batch.set_storage_raw_encoded(&address_hash, &slot_hash, &[1]),
            Err(PersistenceError::InvalidOperation(_))
        ));
        assert!(matches!(
            batch.delete_account_range(&[0; 32], &[0xff; 32]),
            Err(PersistenceError::NotSupported(_))
        ));
        assert!(matches!(
            batch.delete_storage_range(&address_hash, &[0; 32], &[0xff; 32]),
            Err(PersistenceError::NotSupported(_))
        ));
        assert!(matches!(
            reader(&persistence).get_account_raw(&address_hash),
            Err(PersistenceError::InvalidOperation(_))
        ));
        return;
    }

    write(&persistence, |batch| {
        batch.set_account_raw(&address_hash, &account).unwrap()
    });
    let raw = reader(&persistence).get_account_raw(&address_hash).unwrap().unwrap();
    assert_eq!(
        flatdb_core::codec::account::decode_account(&raw, true).unwrap(),
        Some(account)
    );
    assert_eq!(reader(&persistence).get_account(&address_a()).unwrap(), Some(account));

    let stripped = &stored[3..];
    let mut rlp = vec![0x80 + stripped.len() as u8];
    rlp.extend_from_slice(stripped);
    write(&persistence, |batch| {
        batch.set_storage_raw_encoded(&address_hash, &slot_hash, &rlp).unwrap()
    });
    let read = reader(&persistence)
        .try_get_storage_raw(&address_hash, &slot_hash)
        .unwrap();
    assert_eq!(read, Some(U256::from_be_slice(&stored)));
}

#[rstest]
fn trie_nodes_are_keyed_by_path_length_and_account(
    #[values(MEM, MANAGED, ROCKS)] backend: Backend,
    #[values(FLAT, FLAT_IN_TRIE, PREIMAGE_V1, PREIMAGE)] layout: FlatLayout,
) {
    let Some(store) = backend.open() else { return };
    let persistence = open(&store, layout);
    let (account1, account2) = (keccak256(b"A"), keccak256(b"B"));
    let state_paths = [
        "12345",
        "123456",
        "123456789abc",
        "123456789abcdef",
        "123456789abcdef0",
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
    ];
    let storage_paths = [
        "abcd",
        "abcdef123456789",
        "abcdef1234567890",
        &"abcdef0123456789".repeat(4),
    ];

    write(&persistence, |batch| {
        for (i, path) in state_paths.iter().enumerate() {
            batch
                .set_state_trie_node(&TreePath::from_hex(path), &[0xc1, i as u8])
                .unwrap();
        }
        for (i, path) in storage_paths.iter().enumerate() {
            batch
                .set_storage_trie_node(&account1, &TreePath::from_hex(path), &[0xc1, 0xa0 + i as u8])
                .unwrap();
        }
        batch
            .set_storage_trie_node(&account2, &TreePath::from_hex("abcd"), &[0xc1, 0xbb])
            .unwrap();
    });
    let before = reader(&persistence);
    write(&persistence, |batch| {
        batch
            .set_state_trie_node(&TreePath::from_hex("123456"), &[0xc1, 0xff])
            .unwrap()
    });
    let after = reader(&persistence);

    for (i, path) in state_paths.iter().enumerate() {
        assert_eq!(state_rlp(&before, path), Some(vec![0xc1, i as u8]), "{path}");
    }
    for (i, path) in storage_paths.iter().enumerate() {
        assert_eq!(
            storage_rlp(&before, &account1, path),
            Some(vec![0xc1, 0xa0 + i as u8]),
            "{path}"
        );
    }
    assert_eq!(storage_rlp(&before, &account2, "abcd"), Some(vec![0xc1, 0xbb]));
    assert_eq!(
        state_rlp(&before, "abcd"),
        None,
        "state and storage at the same path are separate"
    );
    assert_eq!(state_rlp(&before, "1234"), None);
    assert_eq!(
        state_rlp(&after, "123456"),
        Some(vec![0xc1, 0xff]),
        "readers see their snapshot"
    );
}

/// Port of `TestSelfDestructTrieNodes` and `TestSelfDestructTrieNodesWithSimilarAddressHashPrefix`.
#[rstest]
fn self_destruct_removes_the_storage_trie_of_the_account_only(
    #[values(MEM, MANAGED, ROCKS)] backend: Backend,
    #[values(FLAT, FLAT_IN_TRIE, PREIMAGE_V1, PREIMAGE)] layout: FlatLayout,
) {
    let Some(store) = backend.open() else { return };
    let persistence = open(&store, layout);
    let destructed = account_path(&address_a());
    let other = account_path(&address_b());
    // Shares the destructed account's 4-byte key prefix.
    let mut twin = destructed;
    for byte in &mut twin[4..] {
        *byte ^= 0xff;
    }
    let paths = ["abcd", "123456789abcdef", "0123456789abcdef0123456789abcdef01234567"];

    write(&persistence, |batch| {
        for owner in [destructed, other, twin] {
            for (i, path) in paths.iter().enumerate() {
                batch
                    .set_storage_trie_node(&owner, &TreePath::from_hex(path), &[0xc1, i as u8])
                    .unwrap();
            }
        }
    });
    write(&persistence, |batch| batch.self_destruct(&address_a()).unwrap());

    let reader = reader(&persistence);
    for (i, path) in paths.iter().enumerate() {
        assert_eq!(storage_rlp(&reader, &destructed, path), None, "{path}");
        assert_eq!(storage_rlp(&reader, &other, path), Some(vec![0xc1, i as u8]), "{path}");
        assert_eq!(storage_rlp(&reader, &twin, path), Some(vec![0xc1, i as u8]), "{path}");
    }
}

/// `PersistenceScenario.SubtreeDeleteCases`: the value range of a subtree root and nodes across all columns, tagged
/// with whether their whole subtree is contained in the range.
fn subtree_delete_case(case: usize) -> (Hash256, Hash256, Vec<(String, bool)>) {
    let pad = |prefix: &str, len: usize, fill: char| {
        let mut padded = prefix.to_owned();
        padded.extend(std::iter::repeat_n(fill, len - prefix.len()));
        padded
    };
    let subtree = |root: &str| {
        let root = TreePath::from_hex(root);
        (root.to_lower_bound_path(), root.to_upper_bound_path())
    };
    let nodes = |list: &[(String, bool)]| list.to_vec();
    match case {
        0 => {
            let (from, to) = subtree("ab");
            (
                from,
                to,
                nodes(&[
                    ("ab00".into(), true),
                    ("abffff".into(), true),
                    ("ab00000000".into(), true),
                    ("abffffffffffff".into(), true),
                    (pad("ab", 32, '0'), true),
                    (pad("ab", 32, 'f'), true),
                    (pad("ab", 64, '0'), true),
                    (pad("ab", 64, 'f'), true),
                    ("aa00".into(), false),
                    ("ac00".into(), false),
                    (pad("aa", 64, '0'), false),
                    (pad("ac", 64, '0'), false),
                ]),
            )
        }
        1 => {
            // Ancestors on the zero tail only partially overlap the range, so they stay.
            let (from, to) = subtree(&pad("ab", 20, '0'));
            (
                from,
                to,
                nodes(&[
                    ("ab".into(), false),
                    (pad("ab", 10, '0'), false),
                    (pad("ab", 16, '0'), false),
                    (pad("ab", 18, '0'), false),
                    (pad("ab", 20, '0'), true),
                    (pad("ab", 20, '0') + "cccc", true),
                    (pad("ab", 32, '0'), true),
                    (pad("ab", 64, '0'), true),
                    (pad("aa", 20, '0'), false),
                    (pad("ac", 20, '0'), false),
                    (pad("aa", 64, '0'), false),
                    (pad("ac", 64, '0'), false),
                ]),
            )
        }
        2 => {
            let (from, to) = subtree(&pad("ab", 10, '0'));
            (
                from,
                to,
                nodes(&[
                    ("ab".into(), false),
                    (pad("ab", 6, '0'), false),
                    (pad("ab", 8, '0'), false),
                    (pad("ab", 10, '0'), true),
                    (pad("ab", 12, '0'), true),
                    (pad("ab", 32, '0'), true),
                    (pad("ab", 64, '0'), true),
                    (pad("aa", 64, '0'), false),
                    (pad("ac", 64, '0'), false),
                ]),
            )
        }
        _ => (
            [0; 32],
            [0xff; 32],
            nodes(&[
                ("ab".into(), true),
                (pad("ab", 10, '0'), true),
                (pad("ab", 64, '0'), true),
                (pad("cd", 64, 'f'), true),
            ]),
        ),
    }
}

/// Port of `TestDeleteStateTrieNodeRange` and `TestDeleteStorageTrieNodeRange`.
#[rstest]
fn range_deletes_remove_the_nodes_whose_subtree_is_contained(
    #[values(MEM, MANAGED, ROCKS)] backend: Backend,
    #[values(FLAT, FLAT_IN_TRIE, PREIMAGE_V1, PREIMAGE)] layout: FlatLayout,
    #[values(0, 1, 2, 3)] case: usize,
    #[values(false, true)] storage: bool,
) {
    let Some(store) = backend.open() else { return };
    let persistence = open(&store, layout);
    let (from, to, nodes) = subtree_delete_case(case);
    let account = keccak256(b"A");
    let rlp = [0xc1, 0x11];

    write(&persistence, |batch| {
        for (path, _) in &nodes {
            let path = TreePath::from_hex(path);
            if storage {
                batch.set_storage_trie_node(&account, &path, &rlp).unwrap();
            } else {
                batch.set_state_trie_node(&path, &rlp).unwrap();
            }
        }
    });
    write(&persistence, |batch| {
        if storage {
            batch.delete_storage_trie_node_range(&account, &from, &to).unwrap();
        } else {
            batch.delete_state_trie_node_range(&from, &to).unwrap();
        }
    });

    let reader = reader(&persistence);
    for (path, deleted) in &nodes {
        let node = if storage {
            storage_rlp(&reader, &account, path)
        } else {
            state_rlp(&reader, path)
        };
        assert_eq!(node, (!deleted).then(|| rlp.to_vec()), "{path}");
    }
}

#[rstest]
fn iterators_enumerate_accounts_and_the_storage_of_one_account(
    #[values(MEM, MANAGED, ROCKS)] backend: Backend,
    #[values(FLAT, FLAT_IN_TRIE, PREIMAGE_V1, PREIMAGE)] layout: FlatLayout,
) {
    let Some(store) = backend.open() else { return };
    let persistence = open(&store, layout);
    let all = |reader: &<KvPersistence<AnyKv> as Persistence>::Reader| {
        count(reader.create_account_iterator(&[0; 32], &[0xff; 32]).unwrap())
    };
    let storage_of = |reader: &<KvPersistence<AnyKv> as Persistence>::Reader, address: &Address| {
        let key = if layout.is_preimage() {
            let mut key = [0u8; 32];
            key[..20].copy_from_slice(address);
            key
        } else {
            account_path(address)
        };
        count(reader.create_storage_iterator(&key, &[0; 32], &[0xff; 32]).unwrap())
    };
    assert_eq!(all(&reader(&persistence)), 0);

    write(&persistence, |batch| {
        for (index, target) in [address_a(), address_b(), address_c()].iter().enumerate() {
            batch
                .set_account(target, Some(&indexed_account(index as u64 + 1)))
                .unwrap();
        }
        // Trie nodes share the columns of FlatInTrie and must not show up as flat entries.
        batch
            .set_state_trie_node(&TreePath::from_hex("123456"), &[0xc1, 1])
            .unwrap();
        batch
            .set_storage_trie_node(&account_path(&address_a()), &TreePath::from_hex("12"), &[0xc1, 2])
            .unwrap();
        for slot in [1, 2] {
            batch
                .set_storage(&address_a(), &U256::from_u64(slot), Some(&value(&[0x11])))
                .unwrap();
        }
        for slot in [10, 20, 30] {
            batch
                .set_storage(&address_b(), &U256::from_u64(slot), Some(&value(&[0xaa])))
                .unwrap();
        }
    });

    let reader = reader(&persistence);
    assert_eq!(all(&reader), 3);
    assert_eq!(storage_of(&reader, &address_a()), 2);
    assert_eq!(storage_of(&reader, &address_b()), 3);
    assert_eq!(storage_of(&reader, &address_c()), 0);
}

/// Port of `ClearColumnsTests.Wipe_marker_lasts_until_a_state_pointer_is_persisted`.
#[rstest]
fn the_wipe_marker_lasts_until_a_state_pointer_is_persisted(
    #[values(MEM, MANAGED, ROCKS)] backend: Backend,
    #[values(FLAT, FLAT_IN_TRIE, PREIMAGE)] layout: FlatLayout,
    #[values(true, false)] sync_batch: bool,
) {
    let Some(store) = backend.open() else { return };
    let persistence = open(&store, layout);
    persistence.clear().unwrap();

    let to = if sync_batch {
        StateId::SYNC
    } else {
        StateId::new(1, flatdb_core::types::EMPTY_TREE_HASH)
    };
    let from = if sync_batch { StateId::SYNC } else { PRE_GENESIS };
    write_between(&persistence, from, to, WriteFlags::NONE, |_| {});

    assert_eq!(
        read_wiped_for_sync(&LiveColumn(&store.kv, Column::Metadata)).unwrap(),
        sync_batch
    );
    assert_eq!(
        reader(&persistence).current_state(),
        if sync_batch { PRE_GENESIS } else { to }
    );
}

#[rstest]
fn batches_from_another_state_are_rejected(
    #[values(MEM, MANAGED)] backend: Backend,
    #[values(FLAT, PREIMAGE)] layout: FlatLayout,
) {
    let Some(store) = backend.open() else { return };
    let persistence = open(&store, layout);
    let block1 = StateId::new(1, [1; 32]);
    write_between(&persistence, PRE_GENESIS, block1, WriteFlags::NONE, |_| {});

    assert!(matches!(
        persistence.create_write_batch(&PRE_GENESIS, &StateId::new(2, [2; 32]), WriteFlags::NONE),
        Err(PersistenceError::WrongBaseState { current, .. }) if current == block1
    ));
    assert!(
        persistence
            .create_write_batch(&StateId::SYNC, &StateId::new(2, [2; 32]), WriteFlags::NONE)
            .is_ok()
    );
    assert!(
        persistence
            .create_write_batch(&block1, &StateId::SYNC, WriteFlags::NONE)
            .is_ok()
    );
}

/// The stored layout pins the persistence: another layout fails to open, as `ValidateLayout` throws.
#[rstest]
fn a_db_synced_with_another_layout_fails_to_open(
    #[values(FLAT, FLAT_IN_TRIE, PREIMAGE_V1, PREIMAGE)] layout: FlatLayout,
) {
    let store = MEM.open().unwrap();
    write(&open(&store, layout), |_| {});
    for other in FlatLayout::ALL {
        let opened = KvPersistence::new(store.kv.clone(), other, None);
        assert_eq!(opened.is_ok(), other == layout, "{other}");
    }
}

// SlotRlpEncodingTests, over RocksDbPersistence (the Flat layout).

const SLOT: u64 = 7;

fn slot_key() -> [u8; 52] {
    storage_key(&account_path(&address_a()), &slot_path(&U256::from_u64(SLOT)), false)
}

fn put(kv: &impl KvStore, column: Column, key: &[u8], value: &[u8]) {
    let mut batch = kv.write_batch().unwrap();
    batch.put(column, key, value, WriteFlags::NONE).unwrap();
    batch.commit().unwrap();
}

/// `SlotRlpEncodingTests.CreatePersistence`: raw mode only exists for pre-feature DBs, so it is pinned in the
/// metadata.
fn slot_persistence(kv: &MemKv, rlp_wrap: bool) -> KvPersistence<MemKv> {
    if !rlp_wrap {
        put(kv, Column::Metadata, &*SLOT_ENCODING_KEY, &[SLOT_ENCODING_RAW]);
    }
    KvPersistence::new(kv.clone(), FLAT, None).unwrap()
}

fn write_slot(persistence: &KvPersistence<MemKv>, slot_value: &U256) {
    write_between(
        persistence,
        StateId::SYNC,
        StateId::SYNC,
        WriteFlags::DISABLE_WAL,
        |batch| {
            batch
                .set_storage(&address_a(), &U256::from_u64(SLOT), Some(slot_value))
                .unwrap()
        },
    );
}

fn stored_slot(kv: &MemKv) -> Option<Vec<u8>> {
    kv.get_with(Column::Storage, &slot_key(), <[u8]>::to_vec).unwrap()
}

#[rstest]
fn slot_values_round_trip_in_both_encodings(
    #[values(true, false)] rlp_wrap: bool,
    #[values(
        "05",
        "80",
        "ff",
        "0102",
        "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789"
    )]
    stripped: &str,
) {
    let kv = MemKv::new();
    let persistence = slot_persistence(&kv, rlp_wrap);
    let stripped = hex(stripped);
    write_slot(&persistence, &value(&stripped));

    assert_eq!(slot(&reader(&persistence), &address_a(), SLOT), Some(stripped.clone()));
    let mut expected = stripped.clone();
    if rlp_wrap && !(stripped.len() == 1 && stripped[0] < 0x80) {
        expected.insert(0, 0x80 + stripped.len() as u8);
    }
    assert_eq!(stored_slot(&kv), Some(expected));
}

#[rstest]
fn encoded_slot_writes_are_stored_verbatim_when_wrapping(
    #[values("05", "0102", "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789")] stripped: &str,
) {
    let kv = MemKv::new();
    let persistence = slot_persistence(&kv, true);
    let stripped = hex(stripped);
    let mut leaf = stripped.clone();
    if stripped.len() > 1 || stripped[0] >= 0x80 {
        leaf.insert(0, 0x80 + stripped.len() as u8);
    }
    write_between(
        &persistence,
        StateId::SYNC,
        StateId::SYNC,
        WriteFlags::DISABLE_WAL,
        |batch| {
            batch
                .set_storage_raw_encoded(&account_path(&address_a()), &slot_path(&U256::from_u64(SLOT)), &leaf)
                .unwrap()
        },
    );

    assert_eq!(slot(&reader(&persistence), &address_a(), SLOT), Some(stripped));
    assert_eq!(stored_slot(&kv), Some(leaf));
}

#[test]
fn encoded_slot_writes_are_unsupported_in_raw_mode() {
    let kv = MemKv::new();
    let persistence = slot_persistence(&kv, false);
    let mut batch = persistence
        .create_write_batch(&StateId::SYNC, &StateId::SYNC, WriteFlags::DISABLE_WAL)
        .unwrap();
    assert!(matches!(
        batch.set_storage_raw_encoded(
            &account_path(&address_a()),
            &slot_path(&U256::from_u64(SLOT)),
            &hex("820102")
        ),
        Err(PersistenceError::NotSupported(_))
    ));
}

/// Ports of `Fresh_db_defaults_to_rlp_and_records_version` and `Empty_db_with_layout_marker_defaults_to_rlp`.
#[rstest]
fn a_db_without_slots_wraps_and_records_the_version(#[values(false, true)] seed_layout_marker: bool) {
    let kv = MemKv::new();
    if seed_layout_marker {
        put(&kv, Column::Metadata, &*LAYOUT_KEY, &[FLAT as u8]);
    }
    let persistence = KvPersistence::new(kv.clone(), FLAT, None).unwrap();
    write_slot(&persistence, &value(&hex("0102")));

    assert_eq!(
        kv.get_with(Column::Metadata, &*SLOT_ENCODING_KEY, <[u8]>::to_vec)
            .unwrap(),
        Some(vec![SLOT_ENCODING_RLP])
    );
    assert_eq!(stored_slot(&kv), Some(hex("820102")));
}

/// Port of `Pre_feature_db_with_raw_slots_falls_back_to_raw`: a pre-feature DB holds raw slots but may lack both
/// metadata markers, so detection keys on slot presence, and writes on it stay raw without stamping the markers.
#[rstest]
fn a_pre_feature_db_with_raw_slots_falls_back_to_raw(#[values(false, true)] seed_layout_marker: bool) {
    let kv = MemKv::new();
    if seed_layout_marker {
        put(&kv, Column::Metadata, &*LAYOUT_KEY, &[FLAT as u8]);
    }
    put(&kv, Column::Storage, &slot_key(), &hex("0102"));

    let persistence = KvPersistence::new(kv.clone(), FLAT, None).unwrap();
    assert!(!persistence.rlp_wrap_slots());
    assert_eq!(slot(&reader(&persistence), &address_a(), SLOT), Some(hex("0102")));
    write_slot(&persistence, &value(&hex("abcd")));

    assert_eq!(
        kv.get_with(Column::Metadata, &*SLOT_ENCODING_KEY, <[u8]>::to_vec)
            .unwrap(),
        None
    );
    assert_eq!(stored_slot(&kv), Some(hex("abcd")), "raw, not RLP(0x82abcd)");
    let reopened = KvPersistence::new(kv.clone(), FLAT, None).unwrap();
    assert_eq!(slot(&reader(&reopened), &address_a(), SLOT), Some(hex("abcd")));
}

#[test]
fn an_unknown_slot_encoding_version_fails_to_open() {
    let kv = MemKv::new();
    put(&kv, Column::Metadata, &*SLOT_ENCODING_KEY, &[2]);
    assert!(matches!(
        KvPersistence::new(kv, FLAT, None),
        Err(PersistenceError::InvalidConfiguration(_))
    ));
}

/// Port of `Oversized_slot_iterator_value_is_rejected`: a slot value over 32 bytes means a mismatched encoding, and
/// the iterator fails rather than hand healing a wrong value.
#[rstest]
fn an_oversized_slot_value_fails_the_iterator(#[values(true, false)] rlp_wrap: bool) {
    let kv = MemKv::new();
    let persistence = slot_persistence(&kv, rlp_wrap);
    let mut oversized = vec![1u8; 33];
    if rlp_wrap {
        oversized.insert(0, 0x80 + 33);
    }
    put(&kv, Column::Storage, &slot_key(), &oversized);

    let reader = reader(&persistence);
    let slot_hash = slot_path(&U256::from_u64(SLOT));
    let mut iterator = reader
        .create_storage_iterator(&account_path(&address_a()), &slot_hash, &slot_hash)
        .unwrap();
    assert!(matches!(
        iterator.move_next(),
        Err(PersistenceError::InvalidConfiguration(_))
    ));
}

/// Port of `PreimageStorageKeyTests.StorageKey_HasExpectedShape`.
#[rstest]
#[case(PREIMAGE, format!("00000000{}{}01", "aa".repeat(16), "00".repeat(31)))]
#[case(PREIMAGE_V1, format!("00000000{}01{}", "00".repeat(31), "aa".repeat(16)))]
fn preimage_storage_keys_lead_with_the_address_or_its_prefix(#[case] layout: FlatLayout, #[case] key: String) {
    let kv = MemKv::new();
    let persistence = KvPersistence::new(kv.clone(), layout, None).unwrap();
    write(&persistence, |batch| {
        batch
            .set_storage(
                &hex20("0x00000000aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
                &U256::from_u64(1),
                Some(&value(&[1])),
            )
            .unwrap()
    });

    assert_eq!(column_entries(&kv, Column::Storage)[0].0, hex(&key));
}

/// The memtable sizing and write-ahead log flush of the C# dispose action, seen from the store.
#[test]
fn batches_resize_memtables_and_flush_the_wal_unless_skipping_it() {
    let kv = MemKv::new();
    let persistence = KvPersistence::new(kv.clone(), FLAT, Some(64 * 1024 * 1024)).unwrap();

    write(&persistence, |batch| {
        batch.set_account(&address_a(), Some(&indexed_account(1))).unwrap()
    });
    assert_eq!(
        kv.write_buffer_sizes(),
        [(Column::Account, 64 * 1024 * 1024)],
        "the configured floor"
    );
    assert_eq!(kv.wal_flushes(), 1);

    write_between(
        &persistence,
        StateId::SYNC,
        StateId::SYNC,
        WriteFlags::DISABLE_WAL,
        |batch| batch.set_account(&address_b(), Some(&indexed_account(2))).unwrap(),
    );
    assert_eq!(
        kv.wal_flushes(),
        1,
        "a batch without the write-ahead log does not flush it"
    );
    assert_eq!(
        kv.write_buffer_sizes()[1..],
        [
            Column::Account,
            Column::Storage,
            Column::StateNodes,
            Column::StorageNodes
        ]
        .map(|column| (column, max_write_buffer_size(column) as u64))
    );

    let flat_in_trie_kv = MemKv::new();
    let flat_in_trie = KvPersistence::new(flat_in_trie_kv.clone(), FLAT_IN_TRIE, Some(64 * 1024 * 1024)).unwrap();
    write(&flat_in_trie, |batch| {
        batch.set_account(&address_a(), Some(&indexed_account(1))).unwrap()
    });
    assert_eq!(
        flat_in_trie_kv.write_buffer_sizes(),
        [(Column::StateNodes, 16 * 1024 * 1024)],
        "only the Flat layout takes the configured floor; FlatInTrie keeps accounts in StateNodes"
    );
}

/// `DbOnTheRocks.InnerFlush` only logs a RocksDB flush failure that is not fatal, as `DbOnTheRocks` words it, so neither
/// a flush nor the commit whose write-ahead log it syncs fails, and the commit still drops the cached reader and account.
/// A fatal failure, or any failure of the managed backend, is reported.
#[rstest]
#[case::not_fatal(KvError::rocks("Busy: injected".to_owned()), false)]
#[case::fatal(KvError::rocks("IO error: injected".to_owned()), true)]
#[case::managed(KvError::Callback { operation: "flush", status: -1 }, true)]
fn only_a_fatal_or_managed_flush_failure_fails_a_flush_or_commit(#[case] failure: KvError, #[case] reported: bool) {
    let faults = Faults {
        flush: Some(failure.clone()),
        iterator: None,
    };
    let layout = KvPersistence::new(
        FaultyKv {
            kv: MemKv::new(),
            faults,
        },
        FLAT,
        None,
    )
    .unwrap();
    let persistence = CarryForwardCachingPersistence::new(CachedReaderPersistence::new(layout).unwrap()).unwrap();
    let block1 = StateId::new(1, [1; 32]);
    assert_eq!(reader(&persistence).get_account(&address_a()).unwrap(), None);

    let mut batch = persistence
        .create_write_batch(&PRE_GENESIS, &block1, WriteFlags::NONE)
        .unwrap();
    batch.set_account(&address_a(), Some(&indexed_account(1))).unwrap();
    let sink = LogSink::new(LevelFilter::Warn);
    let (committed, flushed) = sink.scope(|| (batch.commit(), persistence.flush()));

    let expected = if reported {
        Err(PersistenceError::Kv(failure))
    } else {
        Ok(())
    };
    assert_eq!((committed, flushed), (expected.clone(), expected));
    let warning = LogRecord {
        level: Level::Warn,
        source: LogSource::DbOnTheRocks,
        message: "Failed to flush Flat DB: Busy: injected".to_owned(),
    };
    let logged = if reported {
        vec![]
    } else {
        vec![warning.clone(), warning]
    };
    assert_eq!(
        sink.ring().drain(10),
        logged,
        "the commit's write-ahead log flush and the flush"
    );
    if !reported {
        let latest = reader(&persistence);
        assert_eq!(
            (latest.current_state(), latest.get_account(&address_a()).unwrap()),
            (block1, Some(indexed_account(1)))
        );
    }
}

/// See the deviation in the `persistence` module: an iterator read error fails the scan rather than ending it.
#[test]
fn an_iterator_read_error_fails_the_scan_instead_of_ending_it() {
    let kv = MemKv::new();
    write(&KvPersistence::new(kv.clone(), FLAT, None).unwrap(), |batch| {
        for slot in 0..4 {
            batch
                .set_storage(&address_a(), &U256::from_u64(slot), Some(&U256::from_u64(1)))
                .unwrap();
        }
    });
    let error = PersistenceError::Kv(KvError::rocks("IO error: injected".to_owned()));
    let faults = Faults {
        flush: None,
        iterator: Some((2, KvError::rocks("IO error: injected".to_owned()))),
    };
    let faulty = KvPersistence::new(FaultyKv { kv, faults }, FLAT, None).unwrap();

    let mut slots = reader(&faulty)
        .create_storage_iterator(&account_path(&address_a()), &[0; 32], &[0xff; 32])
        .unwrap();
    let moves = [slots.move_next(), slots.move_next()];
    let mut batch = faulty
        .create_write_batch(&PRE_GENESIS, &PRE_GENESIS, WriteFlags::NONE)
        .unwrap();

    assert_eq!(moves, [Ok(true), Err(error.clone())]);
    assert_eq!(batch.self_destruct(&address_a()), Err(error));
}

/// `CachedReaderPersistence`: one reader is shared until a batch commits; sync and full-scan readers are their own.
#[test]
fn the_cached_reader_is_shared_until_a_commit() {
    let kv = MemKv::new();
    let persistence = CachedReaderPersistence::new(KvPersistence::new(kv, FLAT, None).unwrap()).unwrap();
    let shared = |reader: &CachedReader<_>| match reader {
        CachedReader::Shared(reader) => Some(std::sync::Arc::as_ptr(reader)),
        CachedReader::Own(_) => None,
    };

    let first = persistence.create_reader(ReaderFlags::NONE).unwrap();
    let second = persistence.create_reader(ReaderFlags::NONE).unwrap();
    write(&persistence, |batch| {
        batch.set_account(&address_a(), Some(&indexed_account(1))).unwrap()
    });
    let third = persistence.create_reader(ReaderFlags::NONE).unwrap();

    assert!(shared(&first).is_some() && shared(&first) == shared(&second));
    assert_ne!(shared(&third), shared(&first), "a commit drops the shared reader");
    assert_eq!(first.get_account(&address_a()).unwrap(), None);
    assert_eq!(third.get_account(&address_a()).unwrap(), Some(indexed_account(1)));
    for flags in [ReaderFlags::SYNC, ReaderFlags::FULL_SCAN] {
        assert_eq!(shared(&persistence.create_reader(flags).unwrap()), None);
    }

    persistence.clear().unwrap();
    assert_eq!(
        reader(&persistence).get_account(&address_a()).unwrap(),
        None,
        "a clear drops it too"
    );
}

/// Dropping the decorator stops its clearing timer at once, even when the timer thread has not started waiting yet.
#[test]
fn dropping_the_cached_reader_persistence_stops_its_timer_promptly() {
    let started = std::time::Instant::now();
    for _ in 0..20 {
        drop(CachedReaderPersistence::new(KvPersistence::new(MemKv::new(), FLAT, None).unwrap()).unwrap());
    }
    assert!(
        started.elapsed() < std::time::Duration::from_secs(2),
        "{:?}",
        started.elapsed()
    );
}
