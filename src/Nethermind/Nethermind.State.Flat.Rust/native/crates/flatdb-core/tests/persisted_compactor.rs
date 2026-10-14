//! The N-way merge of persisted snapshots: the merge cases of `PersistedSnapshotCompactorTests`, with a property test of
//! the merge against a newest-wins model with the self-destruct barrier.
//!
//! Here the merges run over in-memory tables. The compactor's own cases (its queue, windows, schedule offsets, tier
//! split, tiny and shared blooms, warm-up) come with the compactor.

mod common;

use std::collections::{BTreeMap, BTreeSet};
use std::sync::Arc;

use common::persisted::{MemSnapshot, convert, merge, new_blobs};
use common::snapshots::{address, indexed_address, snapshot, state_id};
use flatdb_core::bloom::BloomFilter;
use flatdb_core::keccak::keccak256;
use flatdb_core::persisted::bloom_keys;
use flatdb_core::persisted::codec::BlobRange;
use flatdb_core::persisted::io::ByteReader;
use flatdb_core::persisted::keys;
use flatdb_core::persisted::reader::{read_blob_range, read_ref_ids};
use flatdb_core::persisted::sorted::table_reader;
use flatdb_core::persisted::stack::PersistedLayer;
use flatdb_core::snapshot::content::SnapshotContentBuilder;
use flatdb_core::snapshot::{Snapshot, SnapshotTier};
use flatdb_core::types::{Account, Address, Hash256, TreePath, U256};
use proptest::prelude::*;
use rstest::rstest;

const BITS_PER_KEY: f64 = 14.0;

fn account(balance: u64) -> Account {
    Account::new(0, U256::from_u64(balance))
}

fn path(seed: &str, length: u8) -> TreePath {
    TreePath::new(keccak256(seed.as_bytes()), length)
}

fn high_byte(value: u8) -> U256 {
    let mut bytes = [0u8; 32];
    bytes[0] = value;
    U256::from_be_bytes(&bytes)
}

type Fill = Box<dyn Fn(&mut SnapshotContentBuilder)>;

/// Converts one base per content, block `i` to `i + 1`, into blob file `i % blob_files`, and merges them all.
fn merge_bases(contents: &[Fill], blob_files: u16) -> (Vec<MemSnapshot>, MemSnapshot) {
    let blobs = new_blobs();
    let bases = contents
        .iter()
        .enumerate()
        .map(|(i, fill)| {
            let snapshot = snapshot(state_id(i as u64, i as u8), state_id(i as u64 + 1, i as u8 + 1), fill);
            convert(&snapshot, &blobs, i as u16 % blob_files, BITS_PER_KEY).0
        })
        .collect::<Vec<_>>();
    let capacity = bases.iter().map(|base| base.bloom.count() as i64).sum::<i64>().max(1);
    let sources = bases.iter().collect::<Vec<_>>();
    let merged = merge(
        &sources,
        SnapshotTier::PersistedLargeCompacted,
        BloomFilter::new(capacity, BITS_PER_KEY).unwrap(),
    );
    (bases, merged)
}

#[rstest]
fn merging_n_bases_keeps_every_account_and_slot(#[values(8, 16, 32)] n: u64) {
    let contents = (1..=n)
        .map(|i| -> Fill {
            Box::new(move |c| {
                c.set_account(indexed_address(i as u32), Some(account(i * 100)));
                c.set_account(address(0xA), Some(account(i)));
                c.set_storage(address(0xA), U256::from_u64(i), Some(high_byte(i as u8)));
            })
        })
        .collect::<Vec<_>>();
    let (_, merged) = merge_bases(&contents, 1);

    assert_eq!((merged.from, merged.to), (state_id(0, 0), state_id(n, n as u8)));
    for i in 1..=n {
        assert_eq!(
            merged.try_get_account(&indexed_address(i as u32)).unwrap(),
            Some(Some(account(i * 100)))
        );
        assert_eq!(
            merged.try_get_slot(&address(0xA), &U256::from_u64(i)).unwrap(),
            Some(Some(high_byte(i as u8)))
        );
    }
    assert_eq!(
        merged.try_get_account(&address(0xA)).unwrap(),
        Some(Some(account(n))),
        "the newest wins"
    );
}

/// An address only one source holds still gets its address, slot and storage-node keys in the merged bloom, in every
/// path form.
#[test]
fn a_single_source_address_adds_every_bloom_key() {
    let owner = keccak256(&address(0xA));
    let (top, compact, fallback) = (path("trie_top", 4), path("trie_compact", 10), path("trie_fb", 20));
    let (_, merged) = merge_bases(
        &[
            Box::new(move |c| {
                c.set_account(address(0xA), Some(account(100)));
                c.set_storage(address(0xA), U256::from_u64(7), Some(high_byte(0x42)));
                c.set_storage_node(owner, top, [1; 32], &[0xC1, 0x80]);
                c.set_storage_node(owner, compact, [2; 32], &[0xC1, 0x81]);
                c.set_storage_node(owner, fallback, [3; 32], &[0xC1, 0x82]);
            }),
            Box::new(|c| c.set_account(address(0xB), Some(account(200)))),
        ],
        1,
    );

    let bloom = &merged.bloom;
    let address_key = bloom_keys::address_key(&address(0xA));
    assert!(bloom.count() > 0);
    assert!(bloom.might_contain(address_key));
    assert!(bloom.might_contain(bloom_keys::slot_key(address_key, &U256::from_u64(7).to_be_bytes())));
    for path in [top, compact, fallback] {
        assert!(
            bloom.might_contain(bloom_keys::storage_node_key(&owner, &path)),
            "{path:?}"
        );
    }
}

/// A base references its own blob file and records its run; the merge references the union of the bases' files,
/// carries the `noderefs` marker and no run.
#[test]
fn merged_metadata_unions_the_ref_ids_and_marks_node_refs() {
    let contents = (1..=8u8)
        .map(|i| -> Fill {
            Box::new(move |c| {
                c.set_account(indexed_address(u32::from(i)), Some(account(u64::from(i) * 100)));
                c.set_state_node(path(&format!("path{i}"), 4), [i; 32], &[0xC1, i]);
            })
        })
        .collect::<Vec<_>>();
    let (bases, merged) = merge_bases(&contents, 3);

    let node_refs = keys::metadata_key(keys::METADATA_NODE_REFS);
    let mut union = BTreeSet::new();
    for base in &bases {
        let reader = ByteReader::new(&base.table);
        let ids = read_ref_ids(&reader).unwrap();
        assert_eq!(ids.len(), 1);
        assert!(!read_blob_range(&reader).unwrap().is_empty());
        assert_eq!(table_reader::get(&reader, node_refs.as_slice()).unwrap(), None);
        union.insert(ids[0]);
    }
    let reader = ByteReader::new(&merged.table);
    assert_eq!(read_ref_ids(&reader).unwrap(), union.into_iter().collect::<Vec<_>>());
    assert_eq!(read_blob_range(&reader).unwrap(), BlobRange::NONE);
    assert_eq!(
        table_reader::get(&reader, node_refs.as_slice()).unwrap(),
        Some(&[1u8][..])
    );
    for (name, expected) in [
        (keys::METADATA_FROM_BLOCK, 0u64.to_le_bytes()),
        (keys::METADATA_TO_BLOCK, 8u64.to_le_bytes()),
    ] {
        assert_eq!(
            table_reader::get(&reader, keys::metadata_key(name).as_slice()).unwrap(),
            Some(&expected[..])
        );
    }
}

type Check = fn(&MemSnapshot);

fn state_rlp(merged: &MemSnapshot, path: &TreePath) -> Option<Vec<u8>> {
    merged.try_load_state_node_rlp(path).unwrap()
}

fn storage_rlp(merged: &MemSnapshot, owner: &Hash256, path: &TreePath) -> Option<Vec<u8>> {
    merged.try_load_storage_node_rlp(owner, path).unwrap()
}

#[rstest]
#[case::account_override(
    |c: &mut SnapshotContentBuilder| c.set_account(address(0xA), Some(account(100))),
    |c: &mut SnapshotContentBuilder| c.set_account(address(0xA), Some(account(200))),
    |m: &MemSnapshot| assert_eq!(m.try_get_account(&address(0xA)).unwrap(), Some(Some(account(200)))),
)]
#[case::state_nodes_advance_in_order(
    |c: &mut SnapshotContentBuilder| {
        c.set_state_node(TreePath::new([0; 32], 4), [1; 32], &[0xC0]);
        c.set_state_node(TreePath::new(hash_10(), 4), [1; 32], &[0xC0]);
    },
    |c: &mut SnapshotContentBuilder| c.set_state_node(TreePath::new(hash_10(), 4), [2; 32], &[0xC1, 0x80]),
    |m: &MemSnapshot| {
        assert_eq!(state_rlp(m, &TreePath::new([0; 32], 4)), Some(vec![0xC0]));
        assert_eq!(state_rlp(m, &TreePath::new(hash_10(), 4)), Some(vec![0xC1, 0x80]));
    },
)]
#[case::storage_nodes_advance_in_order(
    |c: &mut SnapshotContentBuilder| {
        c.set_storage_node(keccak256(b"storageAddr"), TreePath::new([0; 32], 8), [1; 32], &[0xC1, 0x80]);
        c.set_storage_node(keccak256(b"storageAddr"), TreePath::new(hash_10(), 8), [1; 32], &[0xC1, 0x80]);
    },
    |c: &mut SnapshotContentBuilder| c.set_storage_node(keccak256(b"storageAddr"), TreePath::new(hash_10(), 8), [2; 32], &[0xC2, 0x80, 0x81]),
    |m: &MemSnapshot| {
        let owner = keccak256(b"storageAddr");
        assert_eq!(storage_rlp(m, &owner, &TreePath::new([0; 32], 8)), Some(vec![0xC1, 0x80]));
        assert_eq!(storage_rlp(m, &owner, &TreePath::new(hash_10(), 8)), Some(vec![0xC2, 0x80, 0x81]));
    },
)]
#[case::single_source_sub_columns(
    |c: &mut SnapshotContentBuilder| {
        let owner = keccak256(&address(0xA));
        c.set_storage_node(owner, path("trie_top", 4), [1; 32], &[0xC1, 0x80]);
        c.set_storage_node(owner, path("trie_compact", 10), [1; 32], &[0xC1, 0x81]);
        c.set_storage_node(owner, path("trie_fb", 20), [1; 32], &[0xC1, 0x82]);
    },
    |c: &mut SnapshotContentBuilder| c.set_storage_node(keccak256(&address(0xA)), path("trie_compact", 10), [2; 32], &[0xC2, 0x80, 0x81]),
    |m: &MemSnapshot| {
        let owner = keccak256(&address(0xA));
        assert_eq!(storage_rlp(m, &owner, &path("trie_top", 4)), Some(vec![0xC1, 0x80]));
        assert_eq!(storage_rlp(m, &owner, &path("trie_compact", 10)), Some(vec![0xC2, 0x80, 0x81]));
        assert_eq!(storage_rlp(m, &owner, &path("trie_fb", 20)), Some(vec![0xC1, 0x82]));
    },
)]
#[case::mixed_data_types(
    |c: &mut SnapshotContentBuilder| {
        c.set_account(address(0xA), Some(account(100)));
        c.set_storage(address(0xA), U256::from_u64(1), Some(high_byte(0x42)));
        c.set_self_destruct(address(0xB), true);
        c.set_state_node(path("statePath", 4), [1; 32], &[0xC0, 0x80]);
        c.set_storage_node(keccak256(b"storageAddr"), TreePath::new([0; 32], 4), [1; 32], &[0xC1, 0x80]);
    },
    |c: &mut SnapshotContentBuilder| {
        c.set_account(address(0xA), Some(account(200)));
        c.set_storage(address(0xA), U256::from_u64(2), Some(high_byte(0x99)));
        c.set_state_node(path("statePath", 4), [2; 32], &[0xC1, 0x80]);
        c.set_storage_node(keccak256(b"storageAddr"), TreePath::new([0; 32], 4), [2; 32], &[0xC2, 0x80, 0x81]);
    },
    |m: &MemSnapshot| {
        assert_eq!(m.try_get_account(&address(0xA)).unwrap(), Some(Some(account(200))));
        assert_eq!(m.try_get_slot(&address(0xA), &U256::from_u64(1)).unwrap(), Some(Some(high_byte(0x42))));
        assert_eq!(m.try_get_slot(&address(0xA), &U256::from_u64(2)).unwrap(), Some(Some(high_byte(0x99))));
        assert!(m.try_get_self_destruct_flag(&address(0xB)).unwrap().is_some());
        assert_eq!(state_rlp(m, &path("statePath", 4)), Some(vec![0xC1, 0x80]));
        assert_eq!(
            storage_rlp(m, &keccak256(b"storageAddr"), &TreePath::new([0; 32], 4)),
            Some(vec![0xC2, 0x80, 0x81])
        );
    },
)]
#[case::newer_overrides_older(
    |c: &mut SnapshotContentBuilder| {
        c.set_state_node(path("path", 4), [1; 32], &[0xC0]);
        c.set_account(address(0xA), Some(account(100)));
    },
    |c: &mut SnapshotContentBuilder| {
        c.set_state_node(path("path", 4), [2; 32], &[0xC1, 0x80]);
        c.set_account(address(0xB), Some(account(200)));
    },
    |m: &MemSnapshot| {
        assert_eq!(state_rlp(m, &path("path", 4)), Some(vec![0xC1, 0x80]));
        assert_eq!(m.try_get_account(&address(0xA)).unwrap(), Some(Some(account(100))));
        assert_eq!(m.try_get_account(&address(0xB)).unwrap(), Some(Some(account(200))));
    },
)]
#[case::preserves_non_overlapping(
    |c: &mut SnapshotContentBuilder| c.set_state_node(path("path1", 4), [1; 32], &[0xC0]),
    |c: &mut SnapshotContentBuilder| c.set_state_node(path("path2", 4), [2; 32], &[0xC1, 0x80]),
    |m: &MemSnapshot| {
        assert_eq!(state_rlp(m, &path("path1", 4)), Some(vec![0xC0]));
        assert_eq!(state_rlp(m, &path("path2", 4)), Some(vec![0xC1, 0x80]));
    },
)]
#[case::self_destruct_clears_older_storage(
    |c: &mut SnapshotContentBuilder| c.set_storage(address(0xA), U256::from_u64(1), Some(high_byte(0x42))),
    |c: &mut SnapshotContentBuilder| {
        c.set_self_destruct(address(0xA), false);
        c.set_storage(address(0xA), U256::from_u64(2), Some(high_byte(0x99)));
    },
    |m: &MemSnapshot| {
        assert_eq!(m.try_get_slot(&address(0xA), &U256::from_u64(1)).unwrap(), None);
        assert_eq!(m.try_get_slot(&address(0xA), &U256::from_u64(2)).unwrap(), Some(Some(high_byte(0x99))));
        assert_eq!(m.try_get_self_destruct_flag(&address(0xA)).unwrap(), Some(false));
    },
)]
#[case::older_destruct_wins_over_newer_new_account(
    |c: &mut SnapshotContentBuilder| c.set_self_destruct(address(0xA), false),
    |c: &mut SnapshotContentBuilder| c.set_self_destruct(address(0xA), true),
    |m: &MemSnapshot| assert_eq!(m.try_get_self_destruct_flag(&address(0xA)).unwrap(), Some(false)),
)]
#[case::self_destruct_keeps_storage_nodes(
    |c: &mut SnapshotContentBuilder| c.set_storage_node(keccak256(&address(0xA)), path("storage_path", 4), [1; 32], &[0xC1, 0x80]),
    |c: &mut SnapshotContentBuilder| c.set_self_destruct(address(0xA), false),
    |m: &MemSnapshot| assert_eq!(storage_rlp(m, &keccak256(&address(0xA)), &path("storage_path", 4)), Some(vec![0xC1, 0x80])),
)]
#[case::single_source_without_slots(
    |c: &mut SnapshotContentBuilder| {
        c.set_account(address(0xA), Some(account(100)));
        c.set_account(address(0xC), Some(account(300)));
        c.set_self_destruct(address(0xC), false);
    },
    |c: &mut SnapshotContentBuilder| c.set_account(address(0xB), Some(account(200))),
    |m: &MemSnapshot| {
        assert_eq!(m.try_get_account(&address(0xA)).unwrap(), Some(Some(account(100))));
        assert_eq!(m.try_get_slot(&address(0xA), &U256::from_u64(1)).unwrap(), None);
        assert_eq!(m.try_get_account(&address(0xC)).unwrap(), Some(Some(account(300))));
        assert_eq!(m.try_get_self_destruct_flag(&address(0xC)).unwrap(), Some(false));
        assert_eq!(m.try_get_account(&address(0xB)).unwrap(), Some(Some(account(200))));
    },
)]
fn two_source_merges(
    #[case] older: fn(&mut SnapshotContentBuilder),
    #[case] newer: fn(&mut SnapshotContentBuilder),
    #[case] check: Check,
) {
    let (_, merged) = merge_bases(&[Box::new(older), Box::new(newer)], 1);
    check(&merged);
}

fn hash_10() -> Hash256 {
    let mut hash = [0u8; 32];
    hash[0] = 0x10;
    hash
}

/// A path written in the oldest and the newest of eight bases resolves to the newest RLP; one written once keeps its
/// own, whichever end of the window holds it.
#[test]
fn merged_trie_nodes_resolve_to_the_newest_writer() {
    let owner = keccak256(b"storage_trie_addr");
    let contents = (1..=8u8)
        .map(|i| -> Fill {
            Box::new(move |c| match i {
                1 => {
                    c.set_state_node(path("shared_state", 4), [1; 32], &[0xC1, 0x80]);
                    c.set_state_node(path("only_old_state", 4), [1; 32], &[0xC1, 0x33]);
                    c.set_storage_node(owner, path("shared_storage", 6), [1; 32], &[0xC1, 0x80]);
                }
                8 => {
                    c.set_state_node(path("shared_state", 4), [8; 32], &[0xC2, 0x81, 0x42]);
                    c.set_state_node(path("only_new_state", 4), [8; 32], &[0xC1, 0x55]);
                    c.set_storage_node(owner, path("shared_storage", 6), [8; 32], &[0xC2, 0x82, 0x99]);
                }
                _ => c.set_account(indexed_address(u32::from(i)), Some(account(u64::from(i) * 10))),
            })
        })
        .collect::<Vec<_>>();
    let (_, merged) = merge_bases(&contents, 1);

    assert_eq!(
        state_rlp(&merged, &path("shared_state", 4)),
        Some(vec![0xC2, 0x81, 0x42])
    );
    assert_eq!(state_rlp(&merged, &path("only_old_state", 4)), Some(vec![0xC1, 0x33]));
    assert_eq!(state_rlp(&merged, &path("only_new_state", 4)), Some(vec![0xC1, 0x55]));
    assert_eq!(
        storage_rlp(&merged, &owner, &path("shared_storage", 6)),
        Some(vec![0xC2, 0x82, 0x99])
    );
}

/// Both sources touch every account; every fifth carries a self-destruct only in the older one.
#[rstest]
fn colliding_accounts_take_the_newest_and_keep_older_flags(#[values(40, 120)] count: u32) {
    let (_, merged) = merge_bases(
        &[
            Box::new(move |c| {
                for i in 0..count {
                    c.set_account(indexed_address(i), Some(account(u64::from(i) + 1)));
                    if i % 5 == 0 {
                        c.set_self_destruct(indexed_address(i), false);
                    }
                }
            }),
            Box::new(move |c| {
                for i in 0..count {
                    c.set_account(indexed_address(i), Some(account((u64::from(i) + 1) * 1000)));
                }
            }),
        ],
        1,
    );
    for i in 0..count {
        assert_eq!(
            merged.try_get_account(&indexed_address(i)).unwrap(),
            Some(Some(account((u64::from(i) + 1) * 1000)))
        );
        let flag = merged.try_get_self_destruct_flag(&indexed_address(i)).unwrap();
        assert_eq!(flag, (i % 5 == 0).then_some(false));
    }
}

/// A source's writes in the property test: each address, slot and path from small sets so keys collide across sources.
fn source_strategy() -> impl Strategy<Value = Vec<(u8, u8, u8, Option<u8>)>> {
    prop::collection::vec((0u8..6, 0u8..3, 0u8..6, prop::option::of(any::<u8>())), 0..25)
}

fn fill(operations: &[(u8, u8, u8, Option<u8>)], c: &mut SnapshotContentBuilder) {
    for &(kind, who, what, value) in operations {
        let target: Address = address(0xA0 + who);
        let node_path = TreePath::new(keccak256(&[what]), what * 7 + 1);
        let rlp = vec![0xC2, 0x80, value.unwrap_or(0)];
        match kind {
            0 => c.set_account(target, value.map(|value| account(u64::from(value)))),
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
}

/// What the merge of `sources` (oldest first) holds: the newest value of every key, the self-destruct flag destructed
/// when any source destructed, and a slot only when its newest source is at or after the newest destructing one.
struct Model {
    accounts: BTreeMap<Address, Option<Account>>,
    flags: BTreeMap<Address, bool>,
    slots: BTreeMap<(Address, U256), Option<U256>>,
    state_nodes: BTreeMap<[u8; 33], Vec<u8>>,
}

fn model(sources: &[Arc<Snapshot>]) -> Model {
    let mut result = Model {
        accounts: BTreeMap::new(),
        flags: BTreeMap::new(),
        slots: BTreeMap::new(),
        state_nodes: BTreeMap::new(),
    };
    let mut barriers = BTreeMap::new();
    let mut newest_slot_source = BTreeMap::new();
    for (index, source) in sources.iter().enumerate() {
        for (address, account) in source.accounts() {
            result.accounts.insert(*address, *account);
        }
        for (address, is_new) in source.self_destructed_storage_addresses() {
            let destructed = !is_new || result.flags.get(address) == Some(&false);
            result.flags.insert(*address, !destructed);
            if !is_new {
                barriers.insert(*address, index);
            }
        }
        for (key, value) in source.storages() {
            result.slots.insert((key.address, key.slot), *value);
            newest_slot_source.insert((key.address, key.slot), index);
        }
        for (path, node) in source.state_nodes() {
            let mut key = [0u8; 33];
            key[..32].copy_from_slice(&path.path);
            key[32] = path.length;
            result.state_nodes.insert(key, node.rlp.to_vec());
        }
    }
    result.slots.retain(|key, _| {
        barriers
            .get(&key.0)
            .is_none_or(|&barrier| newest_slot_source[key] >= barrier)
    });
    result
}

proptest! {
    #![proptest_config(ProptestConfig::with_cases(128))]

    #[test]
    fn merge_matches_the_newest_wins_model(sources in prop::collection::vec(source_strategy(), 2..5)) {
        let snapshots = sources
            .iter()
            .enumerate()
            .map(|(i, operations)| snapshot(state_id(i as u64, 0), state_id(i as u64 + 1, 0), |c| fill(operations, c)))
            .collect::<Vec<_>>();
        let blobs = new_blobs();
        let bases = snapshots
            .iter()
            .enumerate()
            .map(|(i, snapshot)| convert(snapshot, &blobs, i as u16 % 2, BITS_PER_KEY).0)
            .collect::<Vec<_>>();
        let merged = merge(
            &bases.iter().collect::<Vec<_>>(),
            SnapshotTier::PersistedCompactSized,
            BloomFilter::new(64, BITS_PER_KEY).unwrap(),
        );
        let expected = model(&snapshots);

        for who in 0..3 {
            let target = address(0xA0 + who);
            prop_assert_eq!(merged.try_get_account(&target).unwrap(), expected.accounts.get(&target).copied());
            prop_assert_eq!(merged.try_get_self_destruct_flag(&target).unwrap(), expected.flags.get(&target).copied());
            let address_key = bloom_keys::address_key(&target);
            for what in 0..6u64 {
                let slot = U256::from_u64(what);
                let value = expected.slots.get(&(target, slot)).copied();
                prop_assert_eq!(merged.try_get_slot(&target, &slot).unwrap(), value);
                if value.is_some() {
                    prop_assert!(merged.bloom.might_contain(bloom_keys::slot_key(address_key, &slot.to_be_bytes())));
                }
            }
        }
        for (key, rlp) in &expected.state_nodes {
            let path = TreePath::new(key[..32].try_into().unwrap(), key[32]);
            prop_assert_eq!(merged.try_load_state_node_rlp(&path).unwrap(), Some(rlp.clone()));
            prop_assert!(merged.bloom.might_contain(bloom_keys::state_path_key(&path)));
        }
    }
}
