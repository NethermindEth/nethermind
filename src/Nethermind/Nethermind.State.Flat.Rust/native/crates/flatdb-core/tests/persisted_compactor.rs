//! The persisted compactor: port of `PersistedSnapshotCompactorTests`, with a property test of the merge against a
//! newest-wins model with the self-destruct barrier.
//!
//! The merge cases run over in-memory tables; the compactor's own (its queues and threads, windows, schedule offsets,
//! tier split, tiny and shared blooms, warm-up) over a tier on disk. `EnqueueAsync_FailedHandoff_ReturnsBatchToPool`
//! keeps its refusals; the batch is a plain vector here, with no pool to return it to.

mod common;

use std::collections::{BTreeMap, BTreeSet};
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use std::thread;

use common::persisted::{MemSnapshot, TemporaryTier, convert, merge, new_blobs};
use common::snapshots::{FixedFinality, address, indexed_address, snapshot, state_id};
use flatdb_core::bloom::BloomFilter;
use flatdb_core::keccak::keccak256;
use flatdb_core::kv::{KvError, KvResult};
use flatdb_core::log_ring::LogSink;
use flatdb_core::persisted::bloom_keys::{self, PersistedBloom};
use flatdb_core::persisted::codec::{self, BlobRange};
use flatdb_core::persisted::compactor::EnqueueError;
use flatdb_core::persisted::io::ByteReader;
use flatdb_core::persisted::keys;
use flatdb_core::persisted::list::PersistedSnapshotList;
use flatdb_core::persisted::reader::{read_blob_range, read_ref_ids};
use flatdb_core::persisted::snapshot::PersistedSnapshot;
use flatdb_core::persisted::sorted::table_reader;
use flatdb_core::persisted::stack::{PersistedLayer, PersistedSnapshotStack, StackSlot};
use flatdb_core::persisted::storage::catalog::{CatalogKv, MemCatalogKv};
use flatdb_core::schedule::CompactionSchedule;
use flatdb_core::snapshot::content::SnapshotContentBuilder;
use flatdb_core::snapshot::{Snapshot, SnapshotTier};
use flatdb_core::stats::{COMPACT_SIZE_LABEL_BASE, Histogram};
use flatdb_core::types::{Account, Address, Hash256, StateId, TreePath, U256};
use log::{Level, LevelFilter};
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

/// The state at `block` of the compactor cases' chains.
fn chain_state(block: u64) -> StateId {
    state_id(block, 1)
}

/// `BuildBase`: the base ending at `block`, writing its own account with balance `block * 100`.
fn build_base(tier: &TemporaryTier, block: u64) {
    tier.convert(&snapshot(chain_state(block - 1), chain_state(block), |c| {
        c.set_account(indexed_address(block as u32), Some(account(block * 100)))
    }));
}

fn schedule(compact_size: u64, max_compact_size: u64, offset: u64) -> CompactionSchedule {
    CompactionSchedule::new(compact_size, max_compact_size, offset).unwrap()
}

fn lease(tier: &TemporaryTier, block: u64, snapshot_tier: SnapshotTier) -> Option<Arc<PersistedSnapshot>> {
    tier.repository
        .try_lease_persisted_state(&chain_state(block), snapshot_tier)
}

fn balance_of(snapshot: &PersistedSnapshot, block: u64) -> Option<U256> {
    snapshot
        .try_get_account(&indexed_address(block as u32))
        .unwrap()
        .flatten()
        .map(|account| account.balance)
}

#[rstest]
fn enqueue_refuses_a_batch_after_disposal_or_process_exit(#[values(false, true)] disposed: bool) {
    let tier = TemporaryTier::new();
    let compactor = tier.compactor(schedule(4, 16, 0));
    if disposed {
        compactor.dispose();
    } else {
        compactor.signal_exit();
    }

    let refused = compactor.enqueue(vec![chain_state(1)], 0);

    assert_eq!(
        refused,
        Err(if disposed {
            EnqueueError::Disposed
        } else {
            EnqueueError::Cancelled
        })
    );
}

/// `DoCompactSnapshot_CompactsPartialWindow`: with every block a boundary, block 8's window merges whatever chain
/// reaches back into it.
#[rstest]
#[case::non_power_of_two_start(&[3, 4, 5, 6, 7, 8], Some(2))]
#[case::half(&[5, 6, 7, 8], Some(4))]
#[case::quarter(&[7, 8], Some(6))]
#[case::single_snapshot(&[8], None)]
fn a_partial_window_merges_the_chain_reaching_into_it(#[case] present_blocks: &[u64], #[case] from_block: Option<u64>) {
    let tier = TemporaryTier::new();
    for &block in present_blocks {
        build_base(&tier, block);
    }

    let compacted = tier
        .compactor(schedule(1, 8, 0))
        .do_compact_snapshot(&chain_state(8), 0)
        .unwrap();

    assert_eq!(compacted, from_block.is_some());
    assert_eq!(
        lease(&tier, 8, SnapshotTier::PersistedLargeCompacted).map(|large| (large.from(), large.to())),
        from_block.map(|from_block| (chain_state(from_block), chain_state(8)))
    );
}

/// `DoCompactSnapshot_ClampsWindowToPersistencePoint`, `LargeBoundary_DoesNotShareBloomIntoSnapshotExtendingBelowFrom`
/// and `LargeBoundary_MergedBloomCapacity_DeduplicatesSharedSourceBloom`: block 16's window, every block a boundary,
/// over the bases and a [0,8] merge. Clamped above 0 it skips that wider merge for the bases, and the merge keeps its
/// own bloom; the bases [5,8] already share the [0,8] bloom, which sizes the merged bloom once.
#[rstest]
#[case::at_genesis(0, 0)]
#[case::inside_the_wider_merge(4, 4)]
#[case::at_its_end(8, 8)]
fn the_window_is_clamped_to_the_persisted_block(#[case] persisted_block: u64, #[case] from_block: u64) {
    let tier = TemporaryTier::new();
    let compactor = tier.compactor(schedule(1, 16, 0));
    for block in 1..=8 {
        build_base(&tier, block);
    }
    assert!(compactor.do_compact_snapshot(&chain_state(8), 0).unwrap());
    for block in 9..=16 {
        build_base(&tier, block);
    }
    let source_blooms = (from_block + 1..=16)
        .map(|block| {
            lease(&tier, block, SnapshotTier::PersistedBase)
                .unwrap()
                .persisted_bloom()
                .clone()
        })
        .collect::<Vec<_>>();
    let (mut distinct, mut deduplicated_keys) = (Vec::<&Arc<PersistedBloom>>::new(), 0);
    for bloom in &source_blooms {
        if !distinct.iter().any(|seen| Arc::ptr_eq(seen, bloom)) {
            distinct.push(bloom);
            deduplicated_keys += bloom.filter().count() as i64;
        }
    }

    assert!(
        compactor
            .do_compact_snapshot(&chain_state(16), persisted_block)
            .unwrap()
    );

    let large = lease(&tier, 16, SnapshotTier::PersistedLargeCompacted).unwrap();
    assert_eq!((large.from(), large.to()), (chain_state(from_block), chain_state(16)));
    assert_eq!(
        large.persisted_bloom().filter().data_bytes(),
        BloomFilter::new(deduplicated_keys, BITS_PER_KEY).unwrap().data_bytes()
    );
    let below = lease(&tier, 8, SnapshotTier::PersistedLargeCompacted).unwrap();
    assert_eq!(
        Arc::ptr_eq(below.persisted_bloom(), large.persisted_bloom()),
        persisted_block == 0,
        "a merge reaching below the new one's start keeps its own bloom"
    );
    if persisted_block == 4 {
        let naive_keys = source_blooms
            .iter()
            .map(|bloom| bloom.filter().count() as i64)
            .sum::<i64>();
        assert!(distinct.len() < source_blooms.len());
        assert!(
            BloomFilter::new(naive_keys, BITS_PER_KEY).unwrap().data_bytes()
                > large.persisted_bloom().filter().data_bytes(),
            "a bloom shared by several sources counts once"
        );
    }
}

/// `DoCompactSnapshot_WithNonZeroScheduleOffset_StartingBlockSpansFullAlignment`: at block 45 with offset 3 the
/// alignment is 16, so the window is (29, 45].
#[test]
fn an_offset_window_spans_the_whole_alignment() {
    let tier = TemporaryTier::new();
    for block in 1..=45 {
        build_base(&tier, block);
    }

    assert!(
        tier.compactor(schedule(64, 64, 3))
            .do_compact_snapshot(&chain_state(45), 0)
            .unwrap()
    );

    let compacted = lease(&tier, 45, SnapshotTier::PersistedSmallCompacted).unwrap();
    assert_eq!((compacted.from(), compacted.to()), (chain_state(29), chain_state(45)));
}

/// `DoCompactSnapshot_NoOp_WhenWindowSizeOneOrTooFewSnapshots`, `DoCompactCompactSized_NoOp_WhenNotBoundaryOrTooFewSnapshots`
/// and `DoCompactCompactSized_AtBoundary_ProducesCompactSizedSnapshot`.
#[test]
fn compact_sized_merges_only_at_a_boundary_holding_two_snapshots() {
    let tier = TemporaryTier::new();
    let compactor = tier.compactor(schedule(4, 16, 0));
    assert!(
        !compactor.do_compact_snapshot(&chain_state(1), 0).unwrap(),
        "a window of one block"
    );
    assert!(
        !compactor.do_compact_snapshot(&chain_state(4), 0).unwrap(),
        "an empty tier"
    );
    assert!(
        !compactor.do_compact_compact_sized(&chain_state(4)).unwrap(),
        "an empty tier"
    );
    for block in 1..=4 {
        build_base(&tier, block);
    }
    assert!(
        !compactor.do_compact_compact_sized(&chain_state(3)).unwrap(),
        "not a boundary"
    );
    assert_eq!(tier.repository.persisted_snapshot_count(), 4);

    assert!(compactor.do_compact_compact_sized(&chain_state(4)).unwrap());

    let compact_sized = lease(&tier, 4, SnapshotTier::PersistedCompactSized).unwrap();
    assert_eq!(
        (compact_sized.from(), compact_sized.to()),
        (chain_state(0), chain_state(4))
    );
    for block in 1..=4 {
        assert_eq!(balance_of(&compact_sized, block), Some(U256::from_u64(block * 100)));
    }
    assert_eq!(tier.stats.counters.compactions.load(Ordering::Relaxed), 1);
}

/// `DoCompactSnapshot_AtBoundary_NoAddressColumn_WarmsGracefully`: warming the index of a table without accounts.
#[test]
fn a_boundary_merge_without_accounts_is_warmed() {
    let tier = TemporaryTier::new();
    for block in 1..=2u8 {
        tier.convert(&snapshot(
            chain_state(block as u64 - 1),
            chain_state(block as u64),
            |c| c.set_state_node(path(&format!("node{block}"), 4), [block; 32], &[0xC2, 0x80, block]),
        ));
    }

    assert!(
        tier.compactor(schedule(2, 16, 0))
            .do_compact_snapshot(&chain_state(2), 0)
            .unwrap()
    );

    let compacted = lease(&tier, 2, SnapshotTier::PersistedSmallCompacted).unwrap();
    assert_eq!(
        compacted.try_load_state_node_rlp(&path("node2", 4)).unwrap(),
        Some(vec![0xC2, 0x80, 2])
    );
}

/// `DoCompactSnapshot_SplitsCompactedAndLargeCompactedByWindowWidth` and `SubCompactSizeIntermediate_UsesTinyBloom`: a
/// window narrower than `CompactSize` is a small compacted snapshot with a one-line bloom, still filled; a wider one is
/// a large compacted snapshot; each is found only in its own tier.
#[test]
fn windows_split_by_width_into_small_and_large_tiers() {
    let tier = TemporaryTier::new();
    let compactor = tier.compactor(schedule(4, 16, 0));
    for block in 1..=8 {
        build_base(&tier, block);
    }

    assert!(compactor.do_compact_snapshot(&chain_state(2), 0).unwrap());
    let small = lease(&tier, 2, SnapshotTier::PersistedSmallCompacted).unwrap();
    assert_eq!(small.persisted_bloom().filter().data_bytes(), 64);
    assert!(small.persisted_bloom().filter().count() > 0);
    assert_eq!(
        (balance_of(&small, 1), balance_of(&small, 2)),
        (Some(U256::from_u64(100)), Some(U256::from_u64(200)))
    );

    assert!(compactor.do_compact_snapshot(&chain_state(8), 0).unwrap());
    assert_eq!(
        lease(&tier, 8, SnapshotTier::PersistedLargeCompacted).unwrap().to(),
        chain_state(8)
    );
    assert!(lease(&tier, 2, SnapshotTier::PersistedLargeCompacted).is_none());
    assert!(lease(&tier, 8, SnapshotTier::PersistedSmallCompacted).is_none());

    // Each merge observes its size and time, labelled by its span: 2, then 8.
    let (size, time) = (
        Histogram::PersistedSnapshotCompactedSize as u16,
        Histogram::PersistedSnapshotCompactTime as u16,
    );
    let observations = tier
        .repository
        .stats()
        .drain_observations(usize::MAX)
        .into_iter()
        .filter(|observation| [size, time].contains(&observation.metric))
        .collect::<Vec<_>>();
    let (span_2, span_8) = (COMPACT_SIZE_LABEL_BASE + 1, COMPACT_SIZE_LABEL_BASE + 3);
    assert_eq!(
        observations
            .iter()
            .map(|observation| (observation.metric, observation.label))
            .collect::<Vec<_>>(),
        [(size, span_2), (time, span_2), (size, span_8), (time, span_8)]
    );
    assert!(observations[0].value > 0.0 && observations[2].value > 0.0);
}

/// `LargeBoundary_SharesBloomAcrossContainedSnapshots`: a merge at a large boundary hands its bloom to every snapshot
/// within its window, which still read their own accounts.
#[test]
fn a_large_merge_shares_its_bloom_with_the_snapshots_inside() {
    let tier = TemporaryTier::new();
    let compactor = tier.compactor(schedule(4, 16, 0));
    for block in 1..=8 {
        build_base(&tier, block);
    }
    assert!(compactor.do_compact_snapshot(&chain_state(2), 0).unwrap());
    assert!(compactor.do_compact_compact_sized(&chain_state(4)).unwrap());

    assert!(compactor.do_compact_snapshot(&chain_state(8), 0).unwrap());

    let large = lease(&tier, 8, SnapshotTier::PersistedLargeCompacted).unwrap();
    let shared = large.persisted_bloom();
    assert!(shared.filter().count() > 0);
    for (block, snapshot_tier) in [
        (2, SnapshotTier::PersistedSmallCompacted),
        (4, SnapshotTier::PersistedCompactSized),
    ] {
        assert!(Arc::ptr_eq(
            lease(&tier, block, snapshot_tier).unwrap().persisted_bloom(),
            shared
        ));
    }
    for block in 1..=8 {
        let base = lease(&tier, block, SnapshotTier::PersistedBase).unwrap();
        assert!(Arc::ptr_eq(base.persisted_bloom(), shared), "base {block}");
        assert_eq!(balance_of(&base, block), Some(U256::from_u64(block * 100)));
    }
}

/// The probe of the probable C# bug `PersistedBloomShareTests` pins: a slot a base writes, below a later base of the
/// same large window that self-destructs the account, reads back at a head between the two once the large merge shared
/// its bloom with the bases. The merge drops the slot without its bloom key, so the bases skip it.
#[test]
#[ignore = "C# shares a merged bloom that lacks slots dropped below a self-destruct barrier (PersistedSnapshotMerger drops them without bloom keys; ShareBloomAcrossRange/ReconstructBloom hand that bloom to contained snapshots)"]
fn a_slot_below_a_self_destruct_reads_back_through_a_shared_large_bloom() {
    let tier = TemporaryTier::new();
    let compactor = tier.compactor(schedule(4, 16, 0));
    let target = address(0xA);
    let slot = U256::from_u64(7);
    for block in 1..=8 {
        tier.convert(&snapshot(chain_state(block - 1), chain_state(block), |c| {
            c.set_account(target, Some(account(block)));
            match block {
                1 | 3 => c.set_storage(target, slot, Some(U256::from_u64(block))),
                6 => c.set_self_destruct(target, false),
                _ => {}
            }
        }));
    }
    assert!(compactor.do_compact_snapshot(&chain_state(8), 0).unwrap());
    assert!(lease(&tier, 8, SnapshotTier::PersistedLargeCompacted).is_some());

    let stack = PersistedSnapshotStack::new(PersistedSnapshotList::new(
        tier.repository
            .assemble_snapshots(&chain_state(3), &chain_state(0), 4)
            .persisted,
    ));
    let self_destruct = stack
        .try_get_self_destruct(&target)
        .unwrap()
        .map_or(-1, |index| index as isize);
    assert_eq!(
        stack.try_get_slot(&target, &slot, self_destruct).unwrap(),
        StackSlot::Found(Some(U256::from_u64(3)))
    );
}

/// The batches `EnqueueAsync` hands the threads: the sub-`CompactSize` windows, the CompactSized snapshot at every
/// boundary and the wider merge at every large one, which the boundary threads make; disposal waits for all of them.
#[test]
fn a_batch_compacts_every_window_its_states_end() {
    let tier = TemporaryTier::new();
    for block in 1..=16 {
        build_base(&tier, block);
    }
    let panicked = Arc::new(Mutex::new(Vec::new()));
    let compactor = tier.compactor_recording_panics(schedule(4, 16, 0), panicked.clone());

    compactor.enqueue((0..=16).map(chain_state).collect(), 0).unwrap();
    compactor.dispose();

    let ends = |snapshot_tier| {
        (1..=16)
            .filter_map(|block| lease(&tier, block, snapshot_tier))
            .map(|snapshot| (snapshot.from().block_number, snapshot.to().block_number))
            .collect::<Vec<_>>()
    };
    assert_eq!(
        ends(SnapshotTier::PersistedSmallCompacted),
        [(0, 2), (4, 6), (8, 10), (12, 14)]
    );
    assert_eq!(
        ends(SnapshotTier::PersistedCompactSized),
        [(0, 4), (4, 8), (8, 12), (12, 16)]
    );
    assert_eq!(ends(SnapshotTier::PersistedLargeCompacted), [(0, 8), (0, 16)]);
    assert_eq!(tier.stats.counters.compactions.load(Ordering::Relaxed), 10);
    let widest = lease(&tier, 16, SnapshotTier::PersistedLargeCompacted).unwrap();
    for block in 1..=16 {
        assert_eq!(balance_of(&widest, block), Some(U256::from_u64(block * 100)));
    }
    assert!(panicked.lock().unwrap().is_empty());
    assert_eq!(compactor.enqueue(Vec::new(), 0), Err(EnqueueError::Disposed));
}

/// How [`ScriptedCatalog`] fails the put of its faulty row.
#[derive(Clone, Copy, Debug)]
enum Fault {
    Error,
    Panic,
}

/// An in-memory catalog whose every put counts itself and waits for the gate, then fails or panics on one row.
struct ScriptedCatalog {
    rows: MemCatalogKv,
    faulty_row: Option<(Vec<u8>, Fault)>,
    gate: Arc<Mutex<()>>,
    puts: Arc<AtomicUsize>,
}

impl CatalogKv for ScriptedCatalog {
    fn put(&self, key: &[u8], value: &[u8]) -> KvResult<()> {
        self.puts.fetch_add(1, Ordering::SeqCst);
        drop(self.gate.lock());
        match &self.faulty_row {
            Some((faulty_key, Fault::Error)) if faulty_key == key => Err(KvError::Callback {
                operation: "put",
                status: 1,
            }),
            Some((faulty_key, Fault::Panic)) if faulty_key == key => panic!("injected catalog fault"),
            _ => self.rows.put(key, value),
        }
    }

    fn key_exists(&self, key: &[u8]) -> KvResult<bool> {
        self.rows.key_exists(key)
    }

    fn remove(&self, key: &[u8]) -> KvResult<()> {
        self.rows.remove(key)
    }
}

/// A tier over a [`ScriptedCatalog`] faulting on `faulty_row` and waiting for `gate`, counting its puts in `puts`.
fn scripted_tier(
    faulty_row: Option<(Vec<u8>, Fault)>,
    gate: &Arc<Mutex<()>>,
    puts: &Arc<AtomicUsize>,
) -> TemporaryTier {
    let (gate, puts) = (gate.clone(), puts.clone());
    TemporaryTier::with_catalog(Arc::new(FixedFinality::default()), |rows| {
        Box::new(ScriptedCatalog {
            rows,
            faulty_row,
            gate,
            puts,
        })
    })
}

/// The `(from, to)` blocks of the small, CompactSized and large compacted snapshots.
type Windows = [&'static [(u64, u64)]; 3];

/// A failed or panicking compaction is logged into the sink of the thread that made the compactor, a panicking one
/// also handed to the job panic hook under its thread's name, and the threads go on: a failed window of a bucket still
/// leaves the CompactSized snapshots to make, a failed CompactSized merge abandons the rest of its batch, and a failed
/// large boundary merge only itself. The fault is the catalog row of the merge from `to - depth` to `to`.
#[rstest]
#[case::bucket_window(
    2, 2, "fdb-pcompact", "Error compacting persisted snapshot ",
    [&[(4, 6)][..], &[(0, 4), (4, 8)], &[(0, 8)]]
)]
#[case::compact_sized(
    4, 4, "fdb-pcompact", "Error compacting persisted snapshot batch",
    [&[(0, 2), (4, 6)][..], &[], &[]]
)]
#[case::large_boundary(
    8, 8, "fdb-pcompact-boundary-", "Error compacting boundary persisted snapshot ",
    [&[(0, 2), (4, 6)][..], &[(0, 4), (4, 8)], &[]]
)]
fn a_failed_compaction_is_logged_and_the_threads_go_on(
    #[values(Fault::Error, Fault::Panic)] fault: Fault,
    #[case] faulty_to: u64,
    #[case] faulty_depth: i64,
    #[case] job: &str,
    #[case] logged: &str,
    #[case] windows: Windows,
) {
    let faulty_key = codec::catalog_key(&chain_state(faulty_to), faulty_depth).to_vec();
    let tier = scripted_tier(Some((faulty_key, fault)), &Arc::default(), &Arc::default());
    for block in 1..=8 {
        build_base(&tier, block);
    }
    let panicked = Arc::new(Mutex::new(Vec::new()));
    let sink = LogSink::new(LevelFilter::Error);
    let compactor = sink.scope(|| tier.compactor_recording_panics(schedule(4, 16, 0), panicked.clone()));

    compactor.enqueue((0..=8).map(chain_state).collect(), 0).unwrap();
    compactor.dispose();

    let ends = |snapshot_tier| {
        (1..=8)
            .filter_map(|block| lease(&tier, block, snapshot_tier))
            .map(|snapshot| (snapshot.from().block_number, snapshot.to().block_number))
            .collect::<Vec<_>>()
    };
    assert_eq!(
        [
            ends(SnapshotTier::PersistedSmallCompacted),
            ends(SnapshotTier::PersistedCompactSized),
            ends(SnapshotTier::PersistedLargeCompacted)
        ],
        windows.map(<[_]>::to_vec)
    );
    let records = sink.ring().drain(usize::MAX);
    assert_eq!(records.len(), 1, "{records:?}");
    assert_eq!(records[0].level, Level::Error);
    assert!(records[0].message.starts_with(logged), "{}", records[0].message);
    let panicked_jobs = panicked
        .lock()
        .unwrap()
        .iter()
        .map(|name| {
            name.trim_end_matches(|character: char| character.is_ascii_digit())
                .to_owned()
        })
        .collect::<Vec<_>>();
    assert_eq!(
        panicked_jobs,
        match fault {
            Fault::Error => Vec::<String>::new(),
            Fault::Panic => vec![job.to_owned()],
        }
    );
}

/// Process exit while the batch thread compacts: an enqueue waiting on the full queue gives up, the compaction under way
/// completes and the queued batches are dropped.
#[test]
fn process_exit_drops_the_queued_batches() {
    let (gate, puts) = (Arc::new(Mutex::new(())), Arc::new(AtomicUsize::new(0)));
    let tier = scripted_tier(None, &gate, &puts);
    for block in 1..=8 {
        build_base(&tier, block);
    }
    let compactor = tier.compactor(schedule(4, 16, 0));

    let closed = gate.lock().unwrap();
    let puts_before = puts.load(Ordering::SeqCst);
    compactor.enqueue(vec![chain_state(2)], 0).unwrap();
    while puts.load(Ordering::SeqCst) == puts_before {
        thread::yield_now();
    }
    for _ in 0..16 {
        compactor.enqueue(vec![chain_state(6)], 0).unwrap();
    }
    let refused = thread::scope(|scope| {
        let waiting = scope.spawn(|| compactor.enqueue(vec![chain_state(6)], 0));
        compactor.signal_exit();
        waiting.join().unwrap()
    });
    drop(closed);
    compactor.dispose();

    assert_eq!(refused, Err(EnqueueError::Cancelled));
    assert!(lease(&tier, 2, SnapshotTier::PersistedSmallCompacted).is_some());
    assert!(lease(&tier, 6, SnapshotTier::PersistedSmallCompacted).is_none());
}
