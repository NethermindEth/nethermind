//! Snapshot content and its collections: ports of the Rust-relevant parts of `SnapshotTests`,
//! `SortedMergeDictionaryTests` and the `TreePath.CompareTo` order the snapshot comparers use.
//!
//! Not ported, as they test C# mechanics the Rust snapshot does not have: count sealing on first observation (a Rust
//! snapshot is immutable once built), `AddressNodes` pooling and enumerator reuse, `SnapshotBundle` setters, and the
//! pooled-array, bucket-salt and abort-cleanup tests of `SortedMergeDictionary`.

mod common;

use std::cmp::Ordering;
use std::collections::{BTreeMap, HashMap};

use common::hex;
use common::snapshots::{address, indexed_address, snapshot, state_id};
use flatdb_core::keccak::keccak256;
use flatdb_core::snapshot::content::{SnapshotContentBuilder, SnapshotContentCounts};
use flatdb_core::snapshot::keys::compare_tree_paths;
use flatdb_core::snapshot::sorted_merge::{SortedMergeMap, sort_run};
use flatdb_core::snapshot::{Snapshot, SnapshotTier};
use flatdb_core::types::{Account, Address, StateId, TreePath, U256};
use rstest::rstest;

#[test]
fn estimates_use_the_csharp_per_entry_constants_without_overflow() {
    let one_of_each = snapshot(StateId::PRE_GENESIS, state_id(1, 0), |c| {
        c.set_account(address(0xA1), Some(Account::new(1, U256::from_u64(100))));
        c.set_storage(address(0xA1), U256::from_u64(1), Some(U256::from_u64(7)));
        c.set_self_destruct(address(0xB2), true);
        c.set_state_node(TreePath::from_hex("12"), [0x11; 32], &hex("0xc180"));
        c.set_storage_node([0x22; 32], TreePath::from_hex("34"), [0x33; 32], &[]);
    });

    assert_eq!(one_of_each.estimate_memory(), 172 + 136 + 64 + 726 + 734);
    assert_eq!(one_of_each.estimate_compacted_memory(), 68 + 136 + 64 + 76 + 84);
    let many_slots = SnapshotContentCounts {
        storages: 16_000_000,
        ..SnapshotContentCounts::default()
    };
    assert_eq!(many_slots.estimate_compacted_memory(), 2_176_000_000);
}

#[test]
fn storage_nodes_keep_flattened_dictionary_semantics_and_placeholders() {
    let address_a = keccak256(&address(0xA1));
    let address_b = keccak256(&address(0xB2));
    let path_a = TreePath::from_hex("12");
    let path_b = TreePath::from_hex("34");

    let snapshot = snapshot(StateId::PRE_GENESIS, StateId::PRE_GENESIS, |c| {
        c.set_storage_node(address_a, path_a, [0x01; 32], &hex("0xc101"));
        c.set_storage_node(address_a, path_a, [0x02; 32], &hex("0xc20202"));
        c.set_storage_node(address_b, path_b, [0x03; 32], &[]);
        c.set_state_node(path_a, [0x04; 32], &hex("0xc104"));
    });

    assert_eq!(snapshot.storage_nodes_count(), 2);
    let replaced = snapshot.try_get_storage_node(&address_a, &path_a).unwrap();
    assert_eq!((replaced.hash, replaced.rlp), (&[0x02; 32], &hex("0xc20202")[..]));
    let placeholder = snapshot.try_get_storage_node(&address_b, &path_b).unwrap();
    assert!(placeholder.is_hash_only_placeholder());
    assert_eq!(placeholder.hash, &[0x03; 32]);
    assert!(snapshot.try_get_storage_node(&address_a, &path_b).is_none());
    assert_eq!(snapshot.try_get_state_node(&path_a).unwrap().rlp, &hex("0xc104")[..]);
    assert_eq!(snapshot.storage_nodes().count(), 2);
}

#[test]
fn lookups_tell_written_zero_and_deleted_from_unwritten() {
    let snapshot = snapshot(state_id(1, 0), state_id(2, 0), |c| {
        c.set_account(address(0xA1), Some(Account::new(1, U256::from_u64(100))));
        c.set_account(address(0xB2), None);
        c.set_storage(address(0xA1), U256::from_u64(1), Some(U256::ZERO));
        c.set_storage(address(0xA1), U256::from_u64(2), None);
        c.set_self_destruct(address(0xC3), false);
    });

    assert_eq!(
        snapshot.try_get_account(&address(0xA1)),
        Some(Some(Account::new(1, U256::from_u64(100))))
    );
    assert_eq!(snapshot.try_get_account(&address(0xB2)), Some(None));
    assert_eq!(snapshot.try_get_account(&address(0xC3)), None);
    assert_eq!(
        snapshot.try_get_storage(&address(0xA1), &U256::from_u64(1)),
        Some(Some(U256::ZERO))
    );
    assert_eq!(snapshot.try_get_storage(&address(0xA1), &U256::from_u64(2)), Some(None));
    assert_eq!(snapshot.try_get_storage(&address(0xA1), &U256::from_u64(3)), None);
    assert_eq!(snapshot.self_destruct(&address(0xC3)), Some(false));
    assert!(!snapshot.has_self_destruct(&address(0xA1)));
    assert_eq!(
        (snapshot.from(), snapshot.to(), snapshot.is_sorted()),
        (state_id(1, 0), state_id(2, 0), false)
    );
}

/// The storage nodes of `second` move whole into the empty map of `first`, the state nodes and accounts merge key by key.
#[test]
fn absorb_moves_writes_and_remaps_node_arenas() {
    let mut first = SnapshotContentBuilder::with_capacity(SnapshotContentCounts {
        accounts: 1,
        state_nodes: 1,
        ..SnapshotContentCounts::default()
    });
    first.set_state_node(TreePath::from_hex("01"), [0x01; 32], &hex("0xc101"));
    first.set_account(address(0xA1), None);
    let mut second = SnapshotContentBuilder::new();
    second.set_state_node(TreePath::from_hex("02"), [0x02; 32], &hex("0xc20202"));
    second.set_storage_node([0x09; 32], TreePath::from_hex("03"), [0x03; 32], &hex("0xc3030303"));
    second.set_storage_node([0x09; 32], TreePath::from_hex("04"), [0x04; 32], &[]);
    second.set_account(address(0xA1), Some(Account::new(2, U256::ZERO)));
    first.absorb(second);

    let snapshot = Snapshot::new(state_id(0, 0), state_id(1, 0), first.build());

    assert_eq!(
        snapshot.try_get_state_node(&TreePath::from_hex("01")).unwrap().rlp,
        &hex("0xc101")[..]
    );
    assert_eq!(
        snapshot.try_get_state_node(&TreePath::from_hex("02")).unwrap().rlp,
        &hex("0xc20202")[..]
    );
    let storage = snapshot
        .try_get_storage_node(&[0x09; 32], &TreePath::from_hex("03"))
        .unwrap();
    assert_eq!(storage.rlp, &hex("0xc3030303")[..]);
    assert!(
        snapshot
            .try_get_storage_node(&[0x09; 32], &TreePath::from_hex("04"))
            .unwrap()
            .is_hash_only_placeholder()
    );
    assert_eq!(
        snapshot.try_get_account(&address(0xA1)),
        Some(Some(Account::new(2, U256::ZERO)))
    );
}

#[test]
fn tiers_split_in_memory_from_persisted() {
    let persisted = [
        SnapshotTier::PersistedBase,
        SnapshotTier::PersistedSmallCompacted,
        SnapshotTier::PersistedCompactSized,
        SnapshotTier::PersistedLargeCompacted,
    ];
    assert!(persisted.iter().all(|tier| tier.is_persisted()));
    assert!(!SnapshotTier::InMemoryBase.is_persisted() && !SnapshotTier::InMemoryCompacted.is_persisted());
    SnapshotTier::InMemoryCompacted.ensure_in_memory();
    assert!(std::panic::catch_unwind(|| SnapshotTier::PersistedBase.ensure_in_memory()).is_err());
}

#[rstest]
#[case("12", "12", Ordering::Equal)]
#[case("1", "12", Ordering::Less)]
#[case("12", "1", Ordering::Greater)]
#[case("1f", "2", Ordering::Less)]
#[case("123", "124", Ordering::Less)]
#[case("abc", "ab", Ordering::Greater)]
#[case("", "0", Ordering::Less)]
fn tree_paths_compare_on_shared_nibbles_then_length(
    #[case] left: &str,
    #[case] right: &str,
    #[case] expected: Ordering,
) {
    assert_eq!(
        compare_tree_paths(&TreePath::from_hex(left), &TreePath::from_hex(right)),
        expected
    );
}

#[test]
fn tree_path_order_ignores_bytes_past_the_length_unlike_equality() {
    let clean = TreePath::new(
        hex("0x10")
            .into_iter()
            .chain([0; 31])
            .collect::<Vec<_>>()
            .try_into()
            .unwrap(),
        1,
    );
    let dirty = TreePath::new(
        hex("0x1f")
            .into_iter()
            .chain([0xEE; 31])
            .collect::<Vec<_>>()
            .try_into()
            .unwrap(),
        1,
    );
    assert_eq!(compare_tree_paths(&clean, &dirty), Ordering::Equal);
    assert_ne!(clean, dirty);
}

fn map(pairs: &[(u32, u32)]) -> SortedMergeMap<Address, u32> {
    SortedMergeMap::from_unsorted(pairs.iter().map(|(key, value)| (indexed_address(*key), *value)))
}

fn pairs(map: &SortedMergeMap<Address, u32>) -> Vec<(u32, u32)> {
    map.iter()
        .map(|(key, value)| (u32::from_be_bytes(key[16..].try_into().unwrap()), *value))
        .collect()
}

fn merge(
    sources: &[&SortedMergeMap<Address, u32>],
    keep: impl FnMut(usize, &Address) -> bool,
) -> SortedMergeMap<Address, u32> {
    let runs: Vec<&[(Address, u32)]> = sources.iter().map(|source| source.entries()).collect();
    SortedMergeMap::merge(&runs, keep, |_, value| *value)
}

fn index_of(key: &Address) -> u32 {
    u32::from_be_bytes(key[16..].try_into().unwrap())
}

#[test]
fn from_unsorted_looks_up_every_key_and_iterates_sorted() {
    let source: HashMap<u32, u32> = (0..500).map(|i| (i * 7 % 500, i)).collect();
    let dict = map(&source.iter().map(|(k, v)| (*k, *v)).collect::<Vec<_>>());

    assert_eq!(dict.len(), source.len());
    for (key, value) in &source {
        assert_eq!(dict.get(&indexed_address(*key)), Some(value));
    }
    assert_eq!(dict.get(&indexed_address(10_000)), None);
    let keys: Vec<u32> = pairs(&dict).into_iter().map(|(key, _)| key).collect();
    assert_eq!(keys, (0..500).collect::<Vec<_>>());
}

#[rstest]
fn empty_and_single_entry(#[values(0, 1)] count: u32) {
    let dict = map(&(0..count).map(|i| (i, i + 42)).collect::<Vec<_>>());
    assert_eq!((dict.len(), dict.is_empty()), (count as usize, count == 0));
    assert_eq!(dict.get(&indexed_address(0)).copied(), (count == 1).then_some(42));
}

#[test]
fn merge_combines_disjoint_sources_and_lets_the_highest_priority_source_win() {
    let disjoint = merge(
        &[&map(&[(1, 10), (3, 30), (5, 50)]), &map(&[(2, 20), (4, 40), (6, 60)])],
        |_, _| true,
    );
    assert_eq!(pairs(&disjoint), [(1, 10), (2, 20), (3, 30), (4, 40), (5, 50), (6, 60)]);

    let oldest = map(&[(1, 100), (2, 100), (3, 100)]);
    let middle = map(&[(2, 200), (4, 200)]);
    let newest = map(&[(3, 300), (4, 300), (5, 300)]);
    let merged = merge(&[&oldest, &middle, &newest], |_, _| true);
    assert_eq!(pairs(&merged), [(1, 100), (2, 200), (3, 300), (4, 300), (5, 300)]);
    assert_eq!(merged.get(&indexed_address(4)), Some(&300));
}

#[test]
fn merge_of_single_and_empty_sources_applies_keep() {
    let only = map(&[(7, 70), (8, 80)]);
    assert_eq!(pairs(&merge(&[&only], |_, _| true)), [(7, 70), (8, 80)]);

    let empty = map(&[]);
    let kept = merge(&[&empty, &only, &empty], |source, key| {
        source == 1 && index_of(key) == 7
    });
    assert_eq!(pairs(&kept), [(7, 70)]);
    assert!(merge(&[], |_, _| true).is_empty());
}

#[test]
fn merge_of_unsorted_runs_falls_back_to_a_lower_source_when_the_newest_is_rejected() {
    let run = |entries: &[(u32, u32)]| sort_run(entries.iter().map(|(key, value)| (indexed_address(*key), *value)));
    let oldest = run(&[(4, 10), (2, 10), (1, 10)]);
    let middle = run(&[(4, 20), (3, 20), (2, 20)]);
    let newest = run(&[(5, 30), (4, 30), (2, 30)]);

    let merged = SortedMergeMap::merge(
        &[&oldest[..], &middle[..], &newest[..]],
        |source, key| index_of(key) != 3 && !(source == 2 && index_of(key) == 2),
        |_, value| *value,
    );

    assert_eq!(pairs(&merged), [(1, 10), (2, 20), (4, 30), (5, 30)]);
}

#[rstest]
#[case(1, 50, true)]
#[case(2, 50, true)]
#[case(2, 50, false)]
#[case(3, 200, false)]
#[case(8, 500, true)]
#[case(16, 1000, false)]
#[case(256, 20, true)]
fn merge_matches_a_reference_map(#[case] source_count: usize, #[case] key_space: u32, #[case] filter: bool) {
    let keep = |source: usize, key: u32| !filter || (!key.is_multiple_of(5) && (source != 1 || !key.is_multiple_of(3)));
    let mut seed = (source_count as u64) * 31 + u64::from(key_space);
    let mut next = |bound: u32| {
        seed = seed.wrapping_mul(6364136223846793005).wrapping_add(1442695040888963407);
        ((seed >> 33) % u64::from(bound)) as u32
    };

    let mut sources = Vec::new();
    let mut reference = BTreeMap::new();
    for source in 0..source_count {
        let mut entries = BTreeMap::new();
        let count = key_space / 2 + next(key_space / 2);
        for _ in 0..count {
            let key = next(key_space);
            entries.insert(key, key * 100 + source as u32);
        }
        for (key, value) in &entries {
            if keep(source, *key) {
                reference.insert(*key, *value);
            }
        }
        sources.push(map(&entries.into_iter().collect::<Vec<_>>()));
    }

    let merged = merge(&sources.iter().collect::<Vec<_>>(), |source, key| {
        keep(source, index_of(key))
    });

    assert_eq!(pairs(&merged), reference.into_iter().collect::<Vec<_>>());
}
