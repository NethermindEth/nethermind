//! Reads through a read-only bundle and the bundle cache: ports of the in-memory parts of `ReadOnlySnapshotBundleTests`
//! and `ReadOnlySnapshotBundleFilterTests`, a proptest of every read against an oracle scanning a plain model of the
//! layers (the C# `Random_stacks_read_the_same_through_the_filter`), and bundles over a real persistence on every
//! backend.
//!
//! Not ported: the persisted-snapshot-tier cases, which need long finality, the `ObjectDisposedException` case, which
//! ownership rules out, the detailed-metrics and filter metrics variants (metrics are M1.3), and the cases driving the
//! C# `SnapshotBundle`, `FlatOverridableWorldScope` and `FlatDbManager`, which stay C#.

mod common;

use std::collections::{BTreeMap, BTreeSet};
use std::sync::{Arc, Barrier};
use std::thread;

use common::any_kv::Backend;
use common::hex;
use common::snapshots::{DictionaryReader, address, empty_snapshot, snapshot, state_id};
use flatdb_core::bundle::ReadOnlySnapshotBundle;
use flatdb_core::bundle_cache::BundleCache;
use flatdb_core::compactor::SnapshotCompactor;
use flatdb_core::keccak::{account_path, keccak256};
use flatdb_core::kv::{ReadFlags, WriteFlags};
use flatdb_core::persistence::layouts::KvPersistence;
use flatdb_core::persistence::{Persistence, PersistenceReader, PersistenceWriteBatch, ReaderFlags};
use flatdb_core::repository::SnapshotRepository;
use flatdb_core::schedule::CompactionSchedule;
use flatdb_core::snapshot::Snapshot;
use flatdb_core::snapshot::content::SnapshotContentBuilder;
use flatdb_core::types::{Account, FlatLayout, StateId, TreePath, U256};
use proptest::prelude::*;
use proptest::test_runner::TestCaseError;
use rstest::rstest;

const REAL_BITS_PER_KEY: f64 = 14.0;
/// One bit per key saturates a small filter, so reads take both the plain loop and the definite-miss branch.
const DEGRADED_BITS_PER_KEY: f64 = 1.0;

fn bundle(
    snapshots: Vec<Arc<Snapshot>>,
    reader: DictionaryReader,
    bits_per_key: f64,
) -> ReadOnlySnapshotBundle<DictionaryReader> {
    ReadOnlySnapshotBundle::new(snapshots, reader, bits_per_key)
}

/// Snapshot `block -> block + 1` with `fill`.
fn layer(block: u64, fill: impl FnOnce(&mut SnapshotContentBuilder)) -> Arc<Snapshot> {
    snapshot(state_id(block, 0), state_id(block + 1, 0), fill)
}

fn compact(snapshots: &[Arc<Snapshot>]) -> Arc<Snapshot> {
    let repository = Arc::new(SnapshotRepository::new(Arc::new(
        common::snapshots::FixedFinality::default(),
    )));
    let compactor = SnapshotCompactor::new(CompactionSchedule::new(16, 8192, 0).unwrap(), repository);
    Arc::new(compactor.compact_snapshot_bundle(snapshots))
}

fn u256(value: u64) -> U256 {
    U256::from_u64(value)
}

#[test]
fn get_account_reads_the_newest_snapshot_then_persistence() {
    let (a, b, c) = (address(0xA1), address(0xB2), address(0xC3));
    let mut reader = DictionaryReader::default();
    reader.accounts.insert(a, Account::new(9, u256(9)));
    reader.accounts.insert(b, Account::new(8, u256(8)));
    let bundle = bundle(
        vec![
            layer(0, |s| s.set_account(a, Some(Account::new(1, u256(1))))),
            layer(1, |s| {
                s.set_account(a, Some(Account::new(2, u256(2))));
                s.set_account(c, None);
            }),
        ],
        reader,
        0.0,
    );

    assert_eq!(bundle.get_account(&a).unwrap(), Some(Account::new(2, u256(2))));
    assert_eq!(bundle.get_account(&b).unwrap(), Some(Account::new(8, u256(8))));
    assert_eq!(
        bundle.get_account(&c).unwrap(),
        None,
        "a deletion in a snapshot shadows persistence"
    );
    assert_eq!(bundle.get_account(&address(0xD4)).unwrap(), None);
    assert_eq!(bundle.snapshot_count(), 2);
}

#[test]
fn determine_self_destruct_snapshot_idx_returns_the_newest_clear() {
    let a = address(0xA1);
    let bundle = bundle(
        vec![
            layer(0, |s| s.set_self_destruct(a, false)),
            layer(1, |s| s.set_self_destruct(a, true)),
            empty_snapshot(state_id(2, 0), state_id(3, 0)),
        ],
        DictionaryReader::default(),
        0.0,
    );

    assert_eq!(bundle.determine_self_destruct_snapshot_idx(&a), Some(1));
    assert_eq!(bundle.determine_self_destruct_snapshot_idx(&address(0xB2)), None);
}

#[rstest]
#[case::no_overlay_entry(false, None, Some(7))]
#[case::overlay_deletion(true, None, None)]
#[case::overlay_explicit_zero(true, Some(0), Some(0))]
fn get_slot_overlay_presence_controls_fallback(
    #[case] has_entry: bool,
    #[case] overlay_value: Option<u64>,
    #[case] expected: Option<u64>,
) {
    let a = address(0xA1);
    let index = u256(42);
    let bundle = bundle(
        vec![
            layer(0, |s| s.set_storage(a, index, Some(u256(7)))),
            layer(1, |s| {
                if has_entry {
                    s.set_storage(a, index, overlay_value.map(u256));
                }
            }),
        ],
        DictionaryReader::default(),
        0.0,
    );

    assert_eq!(bundle.get_slot(None, &a, &index).unwrap(), expected.map(u256));
    assert_eq!(bundle.persistence_reader().slot_reads(), 0);
}

#[rstest]
fn get_slot_stops_at_the_clear_and_falls_back_to_persistence_otherwise(#[values(false, true)] found: bool) {
    let a = address(0xA1);
    let mut reader = DictionaryReader::default();
    if found {
        reader.slots.insert((a, u256(1)), U256::ZERO);
    }
    let bundle = bundle(
        vec![
            empty_snapshot(state_id(0, 0), state_id(1, 0)),
            empty_snapshot(state_id(1, 0), state_id(2, 0)),
        ],
        reader,
        0.0,
    );

    assert_eq!(bundle.get_slot(Some(1), &a, &u256(1)).unwrap(), None);
    assert_eq!(
        bundle.persistence_reader().slot_reads(),
        0,
        "a clear in memory never reaches persistence"
    );
    assert_eq!(
        bundle.get_slot(None, &a, &u256(1)).unwrap(),
        found.then_some(U256::ZERO)
    );
    assert_eq!(bundle.persistence_reader().slot_reads(), 1);
}

#[test]
fn try_get_slot_applies_the_newest_clear_itself() {
    let a = address(0xA1);
    let mut reader = DictionaryReader::default();
    reader.slots.insert((a, u256(1)), u256(11));
    reader.slots.insert((a, u256(2)), u256(22));
    let snapshots = vec![
        layer(0, |s| s.set_storage(a, u256(1), Some(u256(5)))),
        layer(1, |s| {
            s.set_self_destruct(a, false);
            s.set_storage(a, u256(3), Some(u256(33)));
        }),
        layer(2, |s| s.set_storage(address(0xB2), u256(1), Some(u256(6)))),
    ];
    let bundle = bundle(snapshots, reader, REAL_BITS_PER_KEY);

    for filtered in [false, true] {
        assert_eq!(
            bundle.try_get_slot(&a, &u256(1), filtered).unwrap(),
            None,
            "cleared after the write"
        );
        assert_eq!(
            bundle.try_get_slot(&a, &u256(2), filtered).unwrap(),
            None,
            "cleared above persistence"
        );
        assert_eq!(bundle.try_get_slot(&a, &u256(3), filtered).unwrap(), Some(u256(33)));
        assert_eq!(
            bundle.try_get_slot(&address(0xB2), &u256(1), filtered).unwrap(),
            Some(u256(6))
        );
    }
    assert_eq!(bundle.persistence_reader().slot_reads(), 0);
}

#[test]
fn trie_nodes_come_from_the_newest_snapshot_and_placeholders_fall_through_to_persistence() {
    let address_hash = keccak256(&address(0xA1));
    let (state_path, storage_path, absent) = (
        TreePath::from_hex("12"),
        TreePath::from_hex("ab"),
        TreePath::from_hex("ff"),
    );
    let mut reader = DictionaryReader::default();
    reader.state_nodes.insert(state_path, hex("0xc1ff"));
    reader.state_nodes.insert(absent, hex("0xc1fe"));
    reader.storage_nodes.insert((address_hash, storage_path), hex("0xc1ee"));
    let older = layer(0, |s| {
        s.set_state_node(state_path, [1; 32], &hex("0xc101"));
        s.set_storage_node(address_hash, storage_path, [2; 32], &hex("0xc102"));
    });
    let newer_placeholders = layer(1, |s| {
        s.set_state_node(state_path, [3; 32], &[]);
        s.set_storage_node(address_hash, storage_path, [4; 32], &[]);
    });

    let found = bundle(vec![older.clone()], DictionaryReader::default(), 0.0);
    let state = found.find_state_node(&state_path).unwrap();
    assert_eq!((state.hash, state.rlp), (&[1; 32], &hex("0xc101")[..]));
    assert_eq!(
        found.find_storage_node(&address_hash, &storage_path).unwrap().rlp,
        &hex("0xc102")[..]
    );
    assert!(found.find_state_node(&absent).is_none());
    assert_eq!(
        found
            .load_state_rlp(&state_path, ReadFlags::NONE, <[u8]>::to_vec)
            .unwrap(),
        Some(hex("0xc101"))
    );

    let shadowed = bundle(vec![older, newer_placeholders], reader, 0.0);
    assert!(
        shadowed
            .find_state_node(&state_path)
            .unwrap()
            .is_hash_only_placeholder()
    );
    assert_eq!(
        shadowed
            .load_state_rlp(&state_path, ReadFlags::NONE, <[u8]>::to_vec)
            .unwrap(),
        Some(hex("0xc1ff"))
    );
    let storage = shadowed.load_storage_rlp(&address_hash, &storage_path, ReadFlags::NONE, <[u8]>::to_vec);
    assert_eq!(storage.unwrap(), Some(hex("0xc1ee")));
    assert_eq!(
        shadowed
            .load_state_rlp(&absent, ReadFlags::NONE, <[u8]>::to_vec)
            .unwrap(),
        Some(hex("0xc1fe"))
    );
    let missing = shadowed.load_storage_rlp(&address_hash, &absent, ReadFlags::NONE, <[u8]>::to_vec);
    assert_eq!(missing.unwrap(), None);
}

#[rstest]
fn a_definite_miss_with_a_clear_in_memory_is_none_and_skips_persistence(
    #[values(REAL_BITS_PER_KEY, DEGRADED_BITS_PER_KEY)] bits_per_key: f64,
) {
    let a = address(0xA1);
    let mut reader = DictionaryReader::default();
    reader.slots.insert((a, u256(1)), u256(11));
    let bundle = bundle(
        vec![
            layer(0, |s| s.set_storage(address(0xB2), u256(1), Some(u256(5)))),
            layer(1, |s| s.set_self_destruct(a, false)),
            layer(2, |s| s.set_storage(address(0xC3), u256(1), Some(u256(6)))),
        ],
        reader,
        bits_per_key,
    );
    let clear = bundle.determine_self_destruct_snapshot_idx(&a);
    assert_eq!(clear, Some(1));

    assert_eq!(bundle.get_slot_filtered(clear, &a, &u256(1)).unwrap(), None);
    if bundle.slot_filter_might_contain(&a, &u256(1)) == Some(false) {
        assert_eq!(bundle.persistence_reader().slot_reads(), 0);
    }

    // Without the clear the same read reaches persistence.
    assert_eq!(bundle.get_slot_filtered(None, &a, &u256(1)).unwrap(), Some(u256(11)));
}

#[rstest]
fn the_filter_holds_every_key_of_unsorted_and_compacted_snapshots(#[values(false, true)] sorted: bool) {
    let mut seed = if sorted { 1u64 } else { 2 };
    let mut next = |bound: u64| {
        seed = seed.wrapping_mul(6364136223846793005).wrapping_add(1442695040888963407);
        (seed >> 33) % bound
    };
    let mut written = Vec::new();
    let mut layers = Vec::new();
    for block in 0..4 {
        let mut builder = SnapshotContentBuilder::new();
        for i in 0..2_000 {
            let key = (address(next(40) as u8), u256(next(1_000_000)));
            builder.set_storage(key.0, key.1, (next(5) != 0).then(|| u256(block * 1_000_000 + i + 1)));
            written.push(key);
        }
        let snapshot = Arc::new(Snapshot::new(
            state_id(block, 0),
            state_id(block + 1, 0),
            builder.build(),
        ));
        layers.push(if sorted { compact(&[snapshot]) } else { snapshot });
    }
    let bundle = bundle(layers, DictionaryReader::default(), REAL_BITS_PER_KEY);

    bundle.get_slot_filtered(None, &address(0), &u256(1)).unwrap();

    assert!(
        written
            .iter()
            .all(|(a, s)| bundle.slot_filter_might_contain(a, s) == Some(true))
    );
}

#[rstest]
fn the_filter_is_built_only_with_bits_per_key_and_two_snapshots(
    #[values(0, 1, 2, 5)] snapshots: u64,
    #[values(0.0, REAL_BITS_PER_KEY)] bits_per_key: f64,
) {
    let a = address(0xA1);
    let layers = (0..snapshots)
        .map(|i| layer(i, |s| s.set_storage(a, u256(i), Some(u256(i + 1)))))
        .collect();
    let bundle = bundle(layers, DictionaryReader::default(), bits_per_key);
    assert!(
        bundle.slot_filter().is_none(),
        "nothing is built before the first filtered read"
    );

    bundle.get_slot(None, &a, &u256(0)).unwrap();
    assert!(bundle.slot_filter().is_none(), "the plain read never builds");

    let value = bundle.get_slot_filtered(None, &a, &u256(0)).unwrap();
    let built = snapshots >= 2 && bits_per_key > 0.0;
    assert_eq!(value, (snapshots > 0).then_some(u256(1)));
    assert_eq!(bundle.slot_filter().is_some(), built);
    assert_eq!(bundle.may_filter_slots(), built);
}

#[test]
fn snapshots_without_slots_get_a_filter_that_answers_every_read() {
    let a = address(0xA1);
    let mut reader = DictionaryReader::default();
    reader.slots.insert((a, u256(3)), u256(33));
    let bundle = bundle(
        vec![
            layer(0, |s| s.set_account(a, Some(Account::new(1, u256(1))))),
            layer(1, |_| {}),
        ],
        reader,
        REAL_BITS_PER_KEY,
    );

    assert_eq!(bundle.get_slot_filtered(None, &a, &u256(3)).unwrap(), Some(u256(33)));
    assert_eq!(bundle.slot_filter().map(|filter| filter.data_bytes()), Some(64));
}

#[test]
fn a_filter_that_cannot_be_built_leaves_the_plain_loop_in_charge() {
    let a = address(0xA1);
    let mut reader = DictionaryReader::default();
    reader.slots.insert((a, u256(2)), u256(22));
    let bundle = bundle(
        vec![
            layer(0, |s| s.set_storage(a, u256(1), Some(u256(1)))),
            layer(1, |s| s.set_storage(a, u256(1), Some(u256(2)))),
        ],
        reader,
        f64::INFINITY,
    );

    assert!(bundle.may_filter_slots());
    assert_eq!(bundle.get_slot_filtered(None, &a, &u256(1)).unwrap(), Some(u256(2)));
    assert_eq!(bundle.get_slot_filtered(None, &a, &u256(2)).unwrap(), Some(u256(22)));
    assert!(bundle.slot_filter().is_none());
    assert!(!bundle.may_filter_slots(), "a failed build is not retried");
}

#[test]
fn concurrent_reads_racing_the_build_match_the_plain_read() {
    const THREADS: usize = 8;
    let mut seed = 7u64;
    let mut next = |bound: u64| {
        seed = seed.wrapping_mul(6364136223846793005).wrapping_add(1442695040888963407);
        (seed >> 33) % bound
    };
    let mut keys = Vec::new();
    let mut layers = Vec::new();
    for block in 0..6 {
        let mut builder = SnapshotContentBuilder::new();
        for i in 0..20_000 {
            let key = (address(next(40) as u8), u256(next(10_000_000)));
            builder.set_storage(key.0, key.1, Some(u256(block * 100_000 + i + 1)));
            if i % 20 == 0 {
                keys.push(key);
            }
        }
        if block == 3 {
            builder.set_self_destruct(address(0), false);
        }
        let snapshot = Arc::new(Snapshot::new(
            state_id(block, 0),
            state_id(block + 1, 0),
            builder.build(),
        ));
        layers.push(if block % 2 == 0 { compact(&[snapshot]) } else { snapshot });
    }
    let hits = keys.len();
    for i in 0..hits {
        keys.push((address(next(40) as u8), u256(20_000_000 + i as u64)));
    }
    let mut reader = DictionaryReader::default();
    for key in keys.iter().step_by(3) {
        reader.slots.insert(*key, u256(1));
    }
    let plain = bundle(
        layers.clone(),
        DictionaryReader {
            slots: reader.slots.clone(),
            ..Default::default()
        },
        0.0,
    );
    let expected: Vec<Option<U256>> = keys
        .iter()
        .map(|(a, s)| plain.try_get_slot(a, s, false).unwrap())
        .collect();

    for _ in 0..20 {
        let filtered = bundle(
            layers.clone(),
            DictionaryReader {
                slots: reader.slots.clone(),
                ..Default::default()
            },
            REAL_BITS_PER_KEY,
        );
        let barrier = Barrier::new(THREADS);
        let mismatches: usize = thread::scope(|scope| {
            let workers: Vec<_> = (0..THREADS)
                .map(|t| {
                    let (filtered, barrier, keys, expected) = (&filtered, &barrier, &keys, &expected);
                    scope.spawn(move || {
                        barrier.wait();
                        (0..keys.len())
                            .map(|n| (n + t * keys.len() / THREADS) % keys.len())
                            .filter(|i| filtered.try_get_slot(&keys[*i].0, &keys[*i].1, true).unwrap() != expected[*i])
                            .count()
                    })
                })
                .collect();
            workers.into_iter().map(|worker| worker.join().unwrap()).sum()
        });
        assert_eq!(mismatches, 0);
        assert!(filtered.slot_filter().is_some());
    }
}

#[test]
fn bundle_cache_shares_a_bundle_per_state_until_cleared() {
    let cache: BundleCache<ReadOnlySnapshotBundle<DictionaryReader>> = BundleCache::new();
    let state = state_id(5, 0);
    let first = Arc::new(bundle(vec![], DictionaryReader::default(), 0.0));
    let second = Arc::new(bundle(vec![], DictionaryReader::default(), 0.0));

    assert!(cache.get(&state).is_none());
    assert!(cache.try_add(state, Arc::clone(&first)));
    assert!(
        !cache.try_add(state, Arc::clone(&second)),
        "TryAdd keeps the first bundle"
    );
    let leased = cache.get(&state).unwrap();
    assert!(Arc::ptr_eq(&leased, &first));
    assert_eq!((cache.len(), Arc::strong_count(&second)), (1, 1));

    cache.clear();

    assert!(cache.is_empty() && cache.get(&state).is_none());
    assert_eq!(
        Arc::strong_count(&first),
        2,
        "a lease taken before the clear outlives it"
    );
}

#[rstest]
fn bundles_read_through_a_real_persistence(
    #[values(Backend::Mem, Backend::Managed, Backend::Rocks)] backend: Backend,
    #[values(FlatLayout::Flat, FlatLayout::PreimageFlat)] layout: FlatLayout,
) {
    let Some(store) = backend.open() else { return };
    let persistence = KvPersistence::new(store.kv.clone(), layout, None).unwrap();
    let (a, b) = (address(0xA1), address(0xB2));
    let persisted = state_id(1, 0x11);
    let storage_path = TreePath::from_hex("ab");
    let mut batch = persistence
        .create_write_batch(&StateId::PRE_GENESIS, &persisted, WriteFlags::NONE)
        .unwrap();
    batch.set_account(&a, Some(&Account::new(1, u256(10)))).unwrap();
    batch.set_account(&b, Some(&Account::new(2, u256(20)))).unwrap();
    batch.set_storage(&a, &u256(1), Some(&u256(100))).unwrap();
    batch.set_storage(&b, &u256(1), Some(&u256(200))).unwrap();
    batch
        .set_state_trie_node(&TreePath::from_hex("12"), &hex("0xc2aaaa"))
        .unwrap();
    batch
        .set_storage_trie_node(&account_path(&b), &storage_path, &hex("0xc2bbbb"))
        .unwrap();
    batch.commit().unwrap();

    let snapshots = vec![
        snapshot(persisted, state_id(2, 0), |s| {
            s.set_self_destruct(a, false);
            s.set_account(b, Some(Account::new(3, u256(30))));
            s.set_state_node(TreePath::from_hex("12"), [7; 32], &[]);
        }),
        snapshot(state_id(2, 0), state_id(3, 0), |s| {
            s.set_storage(b, u256(2), Some(u256(222)));
            s.set_storage_node(account_path(&b), storage_path, [8; 32], &hex("0xc2cccc"));
        }),
    ];
    let reader = persistence.create_reader(ReaderFlags::NONE).unwrap();
    let bundle = ReadOnlySnapshotBundle::new(snapshots, reader, REAL_BITS_PER_KEY);

    assert_eq!(bundle.persistence_reader().current_state(), persisted);
    assert_eq!(bundle.get_account(&a).unwrap(), Some(Account::new(1, u256(10))));
    assert_eq!(bundle.get_account(&b).unwrap(), Some(Account::new(3, u256(30))));
    for filtered in [false, true] {
        assert_eq!(
            bundle.try_get_slot(&a, &u256(1), filtered).unwrap(),
            None,
            "cleared in memory"
        );
        assert_eq!(bundle.try_get_slot(&b, &u256(1), filtered).unwrap(), Some(u256(200)));
        assert_eq!(bundle.try_get_slot(&b, &u256(2), filtered).unwrap(), Some(u256(222)));
        assert_eq!(bundle.try_get_slot(&b, &u256(3), filtered).unwrap(), None);
    }
    let state_rlp = bundle.load_state_rlp(&TreePath::from_hex("12"), ReadFlags::NONE, <[u8]>::to_vec);
    assert_eq!(
        state_rlp.unwrap(),
        Some(hex("0xc2aaaa")),
        "the placeholder falls through to persistence"
    );
    let storage_rlp = bundle.load_storage_rlp(&account_path(&b), &storage_path, ReadFlags::NONE, <[u8]>::to_vec);
    assert_eq!(storage_rlp.unwrap(), Some(hex("0xc2cccc")));
}

/// One layer of the oracle: what it wrote and which addresses it cleared.
#[derive(Debug, Clone, Default)]
struct Layer {
    accounts: BTreeMap<u8, Option<u64>>,
    slots: BTreeMap<(u8, u8), Option<u64>>,
    clears: BTreeMap<u8, bool>,
    state_nodes: BTreeMap<u8, Vec<u8>>,
}

impl Layer {
    /// A compacted snapshot's model is read off its content: the compactor's own drops are not what is checked.
    fn of(snapshot: &Snapshot) -> Self {
        Self {
            accounts: snapshot
                .accounts()
                .map(|(a, account)| (a[0], account.map(|account| account.nonce)))
                .collect(),
            slots: snapshot
                .storages()
                .map(|(key, value)| ((key.address[0], key.slot.0[0] as u8), value.map(|v| v.0[0])))
                .collect(),
            clears: snapshot
                .self_destructed_storage_addresses()
                .map(|(a, is_new)| (a[0], is_new))
                .collect(),
            state_nodes: snapshot
                .state_nodes()
                .map(|(path, node)| (path.path[0] >> 4, node.rlp.to_vec()))
                .collect(),
        }
    }

    fn snapshot(&self, block: u64) -> Arc<Snapshot> {
        layer(block, |s| {
            for (a, nonce) in &self.accounts {
                s.set_account(address(*a), nonce.map(|nonce| Account::new(nonce, U256::ZERO)));
            }
            for ((a, slot), value) in &self.slots {
                s.set_storage(address(*a), u256(u64::from(*slot)), value.map(u256));
            }
            for (a, is_new) in &self.clears {
                s.set_self_destruct(address(*a), *is_new);
            }
            for (nibble, rlp) in &self.state_nodes {
                s.set_state_node(node_path(*nibble), [*nibble; 32], rlp);
            }
        })
    }
}

fn node_path(nibble: u8) -> TreePath {
    TreePath::from_hex(&format!("{nibble:x}"))
}

/// A stack layer: one base snapshot, or a compacted run of 1 to 4.
#[derive(Debug, Clone)]
enum StackLayer {
    Base(Layer),
    Compacted(Vec<Layer>),
}

fn layer_strategy() -> impl Strategy<Value = Layer> {
    (
        prop::collection::btree_map(0u8..6, prop::option::of(1u64..100), 0..3),
        prop::collection::btree_map((0u8..6, 1u8..8), prop::option::of(1u64..1000), 0..12),
        prop::collection::btree_map(0u8..6, any::<bool>(), 0..2),
        prop::collection::btree_map(0u8..8, prop::collection::vec(any::<u8>(), 0..3), 0..3),
    )
        .prop_map(|(accounts, slots, clears, state_nodes)| Layer {
            accounts,
            slots,
            clears,
            state_nodes,
        })
}

fn stack_strategy() -> impl Strategy<Value = Vec<StackLayer>> {
    prop::collection::vec(
        prop_oneof![
            2 => layer_strategy().prop_map(StackLayer::Base),
            1 => prop::collection::vec(layer_strategy(), 1..4).prop_map(StackLayer::Compacted),
        ],
        0..8,
    )
}

#[derive(Debug, Clone)]
struct PersistenceModel {
    accounts: BTreeMap<u8, u64>,
    slots: BTreeMap<(u8, u8), u64>,
    state_nodes: BTreeMap<u8, Vec<u8>>,
}

impl PersistenceModel {
    fn reader(&self) -> DictionaryReader {
        DictionaryReader {
            accounts: self
                .accounts
                .iter()
                .map(|(a, nonce)| (address(*a), Account::new(*nonce, U256::ZERO)))
                .collect(),
            slots: self
                .slots
                .iter()
                .map(|((a, s), v)| ((address(*a), u256(u64::from(*s))), u256(*v)))
                .collect(),
            state_nodes: self
                .state_nodes
                .iter()
                .map(|(nibble, rlp)| (node_path(*nibble), rlp.clone()))
                .collect(),
            ..Default::default()
        }
    }
}

fn persistence_strategy() -> impl Strategy<Value = PersistenceModel> {
    (
        prop::collection::btree_map(0u8..9, 1u64..100, 0..6),
        prop::collection::btree_map((0u8..9, 1u8..10), 1u64..1_000_000, 0..30),
        prop::collection::btree_map(0u8..8, prop::collection::vec(1u8..255, 1..3), 0..6),
    )
        .prop_map(|(accounts, slots, state_nodes)| PersistenceModel {
            accounts,
            slots,
            state_nodes,
        })
}

/// Port of the C# `Oracle`: reads a list of layers newest first, honouring clears.
fn oracle_slot(layers: &[Layer], persistence: &PersistenceModel, key: (u8, u8), clear: Option<usize>) -> Option<u64> {
    for (index, layer) in layers.iter().enumerate().rev() {
        if let Some(value) = layer.slots.get(&key) {
            return *value;
        }
        if clear.is_some_and(|clear| index <= clear) {
            return None;
        }
    }
    persistence.slots.get(&key).copied()
}

fn oracle_clear(layers: &[Layer], a: u8) -> Option<usize> {
    layers.iter().rposition(|layer| layer.clears.contains_key(&a))
}

proptest! {
    #![proptest_config(ProptestConfig::with_cases(256))]

    #[test]
    fn random_stacks_read_the_same_through_the_filter_as_the_oracle(
        stack in stack_strategy(),
        persistence in persistence_strategy(),
    ) {
        check_stack_reads(&stack, &persistence)?;
    }
}

/// Every read of a bundle over `stack`, with the filter built, degraded and disabled, matches the oracle.
fn check_stack_reads(stack: &[StackLayer], persistence: &PersistenceModel) -> Result<(), TestCaseError> {
    let mut block = 0;
    let mut snapshots = Vec::new();
    let mut layers = Vec::new();
    for stack_layer in stack {
        match stack_layer {
            StackLayer::Base(model) => {
                snapshots.push(model.snapshot(block));
                layers.push(model.clone());
                block += 1;
            }
            StackLayer::Compacted(run) => {
                let sources: Vec<Arc<Snapshot>> = run
                    .iter()
                    .enumerate()
                    .map(|(i, model)| model.snapshot(block + i as u64))
                    .collect();
                block += run.len() as u64;
                let compacted = compact(&sources);
                layers.push(Layer::of(&compacted));
                snapshots.push(compacted);
            }
        }
    }

    for bits_per_key in [REAL_BITS_PER_KEY, DEGRADED_BITS_PER_KEY, 0.0] {
        let bundle = ReadOnlySnapshotBundle::new(snapshots.clone(), persistence.reader(), bits_per_key);

        for a in 0u8..9 {
            let clear = oracle_clear(&layers, a);
            prop_assert_eq!(bundle.determine_self_destruct_snapshot_idx(&address(a)), clear);
            let account = layers
                .iter()
                .rev()
                .find_map(|layer| layer.accounts.get(&a).copied())
                .unwrap_or_else(|| persistence.accounts.get(&a).copied());
            prop_assert_eq!(
                bundle.get_account(&address(a)).unwrap().map(|account| account.nonce),
                account
            );

            for s in 1u8..10 {
                let slot = u256(u64::from(s));
                for d in std::iter::once(None).chain((0..=snapshots.len()).map(Some)) {
                    let expected = oracle_slot(&layers, persistence, (a, s), d).map(u256);
                    prop_assert_eq!(bundle.get_slot(d, &address(a), &slot).unwrap(), expected);
                    prop_assert_eq!(bundle.get_slot_filtered(d, &address(a), &slot).unwrap(), expected);
                }
                let expected = oracle_slot(&layers, persistence, (a, s), clear).map(u256);
                prop_assert_eq!(bundle.try_get_slot(&address(a), &slot, false).unwrap(), expected);
                prop_assert_eq!(bundle.try_get_slot(&address(a), &slot, true).unwrap(), expected);
            }
        }

        for nibble in 0u8..8 {
            let expected = match layers.iter().rev().find_map(|layer| layer.state_nodes.get(&nibble)) {
                Some(rlp) if !rlp.is_empty() => Some(rlp.clone()),
                _ => persistence.state_nodes.get(&nibble).cloned(),
            };
            let actual = bundle.load_state_rlp(&node_path(nibble), ReadFlags::NONE, <[u8]>::to_vec);
            prop_assert_eq!(actual.unwrap(), expected);
        }

        prop_assert_eq!(
            bundle.slot_filter().is_some(),
            bits_per_key > 0.0 && snapshots.len() >= 2
        );
        let written: BTreeSet<(u8, u8)> = layers.iter().flat_map(|layer| layer.slots.keys().copied()).collect();
        for (a, s) in written {
            prop_assert_ne!(
                bundle.slot_filter_might_contain(&address(a), &u256(u64::from(s))),
                Some(false)
            );
        }
    }
    Ok(())
}
