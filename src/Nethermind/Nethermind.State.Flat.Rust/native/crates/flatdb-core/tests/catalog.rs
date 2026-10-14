//! The persisted-snapshot catalog over its three backends: memory, callbacks into a managed `IDb`, and (with
//! `rocksdb-it`) a real single-column-family RocksDB database, the way C# opens the keyed catalog. Ports the catalog
//! cases of `StorageLayerTests` and checks the rows C# wrote in the golden vectors round-trip.

mod common;

use std::collections::BTreeMap;
use std::sync::Arc;
use std::sync::atomic::Ordering;

use common::{hex, hex32};
use flatdb_core::kv::KvError;
use flatdb_core::persisted::TierError;
use flatdb_core::persisted::codec::{self, CatalogEntry, SnapshotLocation};
use flatdb_core::persisted::storage::catalog::{CatalogKv, ManagedCatalogKv, MemCatalogKv, SnapshotCatalog};
use flatdb_core::snapshot::SnapshotTier;
use flatdb_core::stats::SharedTierCounters;
use flatdb_core::types::StateId;
use parking_lot::Mutex;
use serde_json::Value;

type Rows = Vec<(Vec<u8>, Vec<u8>)>;

fn state(block: u64, seed: u8) -> StateId {
    StateId::new(block, [seed; 32])
}

fn entry(from: StateId, to: StateId, offset: i64, size: i64, tier: SnapshotTier) -> CatalogEntry {
    CatalogEntry {
        from,
        to,
        location: SnapshotLocation {
            arena_id: 0,
            offset,
            size,
        },
        tier,
    }
}

fn pack(rows: &Rows) -> Vec<u8> {
    let mut packed = Vec::new();
    for (key, value) in rows {
        for field in [key, value] {
            packed.extend_from_slice(&(field.len() as u32).to_le_bytes());
            packed.extend_from_slice(field);
        }
    }
    packed
}

/// `SnapshotCatalog_SaveLoad_RoundTrips` and `SnapshotCatalog_Remove_And_Find`: three rows sharing a `To` at depths
/// 1, 2 and 4 stay apart, a removal takes only its `(To, depth)`, a missing row is not removed, and what is left loads
/// back.
fn rows_round_trip_by_to_and_depth(kv: Box<dyn CatalogKv>, rows: impl Fn() -> Rows) {
    let shared_to = state(100, 0x64);
    let base = entry(state(99, 0x63), shared_to, 0, 1024, SnapshotTier::PersistedBase);
    let compacted = entry(
        state(98, 0x62),
        shared_to,
        1024,
        2048,
        SnapshotTier::PersistedSmallCompacted,
    );
    let compact_sized = entry(
        state(96, 0x60),
        shared_to,
        3072,
        4096,
        SnapshotTier::PersistedCompactSized,
    );
    let tail = entry(
        shared_to,
        state(200, 0xC8),
        7168,
        2048,
        SnapshotTier::PersistedCompactSized,
    );
    let pre_genesis = entry(
        StateId::PRE_GENESIS,
        state(0, 0),
        9216,
        100,
        SnapshotTier::PersistedBase,
    );

    let catalog = SnapshotCatalog::new(kv);
    assert!(rows().is_empty());
    assert!(
        codec::parse_rows(&pack(&rows())).unwrap().is_empty(),
        "an empty catalog loads nothing"
    );
    for added in [&base, &compacted, &compact_sized, &tail, &pre_genesis] {
        catalog.add(added).unwrap();
    }
    let mut loaded = codec::parse_rows(&pack(&rows())).unwrap();
    loaded.sort_by_key(|entry| entry.location.offset);
    assert_eq!(loaded, [base, compacted, compact_sized, tail, pre_genesis]);

    assert!(catalog.remove(&shared_to, 1).unwrap());
    assert!(!catalog.remove(&shared_to, 1).unwrap(), "already removed");
    assert!(!catalog.remove(&state(999, 0x99), 1).unwrap(), "never added");
    assert!(
        catalog.remove(&StateId::new(0, [0; 32]), 1).unwrap(),
        "PreGenesis spans 1 block"
    );
    let mut left = codec::parse_rows(&pack(&rows())).unwrap();
    left.sort_by_key(|entry| entry.location.offset);
    assert_eq!(
        left,
        [compacted, compact_sized, tail],
        "the siblings at the same To are kept"
    );
}

#[test]
fn memory_catalog_keys_rows_by_to_and_depth() {
    let kv = MemCatalogKv::default();
    let rows = kv.clone();
    rows_round_trip_by_to_and_depth(Box::new(kv), move || rows.rows());
}

/// A `MemDb` behind callbacks; `failing` makes every callback report status 7.
fn managed(rows: Arc<Mutex<BTreeMap<Vec<u8>, Vec<u8>>>>, failing: bool) -> ManagedCatalogKv {
    let status = if failing { 7 } else { 0 };
    let (put_rows, exists_rows, remove_rows) = (rows.clone(), rows.clone(), rows);
    ManagedCatalogKv::new(
        move |key, value| {
            put_rows.lock().insert(key.to_vec(), value.to_vec());
            status
        },
        move |key, exists| {
            *exists = exists_rows.lock().contains_key(key);
            status
        },
        move |key| {
            remove_rows.lock().remove(key);
            status
        },
    )
}

#[test]
fn managed_catalog_calls_back_and_reports_failed_statuses() {
    let rows = Arc::new(Mutex::new(BTreeMap::new()));
    let snapshot_rows = rows.clone();
    rows_round_trip_by_to_and_depth(Box::new(managed(rows, false)), move || {
        snapshot_rows
            .lock()
            .iter()
            .map(|(key, value)| (key.clone(), value.clone()))
            .collect()
    });

    let failing = SnapshotCatalog::new(Box::new(managed(Arc::default(), true)));
    let added = entry(state(1, 1), state(2, 2), 0, 10, SnapshotTier::PersistedBase);
    for (outcome, operation) in [
        (failing.add(&added).map(|_| false), "put"),
        (failing.remove(&added.to, 1), "key_exists"),
    ] {
        assert!(
            matches!(outcome, Err(TierError::Catalog(KvError::Callback { operation: failed, status: 7 })) if failed == operation),
            "{operation}"
        );
    }
}

/// The rows of the C# golden `catalog` scenario, as its last successful reload lists them.
fn csharp_rows() -> Rows {
    let path = concat!(env!("CARGO_MANIFEST_DIR"), "/tests/golden/persisted/catalog.json");
    let vector: Value = serde_json::from_str(&std::fs::read_to_string(path).unwrap()).unwrap();
    let reload = vector["steps"]
        .as_array()
        .unwrap()
        .iter()
        .find(|step| step["kind"] == "reload" && step["error"].is_null())
        .expect("a successful reload");
    reload["state"]["catalog"]
        .as_array()
        .unwrap()
        .iter()
        .map(|row| (hex(row[0].as_str().unwrap()), hex(row[1].as_str().unwrap())))
        .collect()
}

/// Every C# row of the catalog's key and value lengths parses, the foreign ones are skipped, and adding the parsed
/// entries writes the identical rows.
fn csharp_rows_round_trip(kv: Box<dyn CatalogKv>, rows: impl Fn() -> Rows) {
    let all = csharp_rows();
    let entries = codec::parse_rows(&pack(&all)).unwrap();
    let mut expected: Rows = all
        .iter()
        .filter(|(key, value)| key.len() == codec::CATALOG_KEY_SIZE && value.len() == codec::CATALOG_VALUE_SIZE)
        .cloned()
        .collect();
    assert!(
        expected.len() >= 3 && expected.len() < all.len(),
        "the scenario has foreign rows"
    );
    assert_eq!(entries.len(), expected.len());
    assert!(entries.iter().any(|entry| entry.from == StateId::PRE_GENESIS));

    let catalog = SnapshotCatalog::new(kv);
    for parsed in &entries {
        catalog.add(parsed).unwrap();
    }
    let mut written = rows();
    written.sort();
    expected.sort();
    assert_eq!(written, expected);
    assert_eq!(
        entries[0].to,
        StateId::new(
            0,
            hex32("0x044852b2a670ade5407e78fb2863c51de9fcb96542a07186fe3aeda6bb8a116d")
        )
    );
}

#[test]
fn csharp_rows_parse_and_rewrite_byte_for_byte() {
    let kv = MemCatalogKv::default();
    let rows = kv.clone();
    csharp_rows_round_trip(Box::new(kv), move || rows.rows());
}

#[cfg(feature = "rocksdb-it")]
mod rocks {
    use std::sync::atomic::Ordering;

    use flatdb_core::kv::{Column, KvStore};
    use flatdb_core::persisted::storage::catalog::{RocksCatalogKv, SnapshotCatalog};
    use flatdb_core::snapshot::SnapshotTier;
    use flatdb_core::stats::SharedTierCounters;

    use super::common::column_entries;
    use super::common::rocks::TestDb;
    use super::{csharp_rows_round_trip, entry, rows_round_trip_by_to_and_depth, state};

    #[test]
    fn rocks_catalog_writes_the_default_column_family_and_counts_puts() {
        let Some(db) = TestDb::open_catalog() else { return };
        let counters = SharedTierCounters::default();
        let rows_kv = db.catalog_kv();
        rows_round_trip_by_to_and_depth(Box::new(RocksCatalogKv::new(db.catalog_kv(), counters.clone())), || {
            column_entries(&rows_kv, Column::Metadata)
        });
        assert_eq!(
            counters.catalog_puts.load(Ordering::Relaxed),
            5,
            "puts count, existence checks and removes do not"
        );

        let catalog = SnapshotCatalog::new(Box::new(RocksCatalogKv::new(db.catalog_kv(), counters.clone())));
        let added = entry(state(7, 7), state(8, 8), 0, 64, SnapshotTier::PersistedLargeCompacted);
        catalog.add(&added).unwrap();
        catalog.add(&added).unwrap();
        assert_eq!(
            column_entries(&rows_kv, Column::Metadata).len(),
            4,
            "a re-add replaces the row"
        );
        assert!(
            rows_kv
                .get_with(
                    Column::Account,
                    &flatdb_core::persisted::codec::catalog_key(&added.to, 1),
                    |_| ()
                )
                .unwrap()
                .is_some(),
            "every column is the default one"
        );
    }

    #[test]
    fn rocks_catalog_rewrites_csharp_rows_byte_for_byte() {
        let Some(db) = TestDb::open_catalog() else { return };
        let rows_kv = db.catalog_kv();
        csharp_rows_round_trip(
            Box::new(RocksCatalogKv::new(db.catalog_kv(), SharedTierCounters::default())),
            || column_entries(&rows_kv, Column::Metadata),
        );
    }
}

#[test]
fn rocks_catalog_counts_puts_over_any_store() {
    let counters = SharedTierCounters::default();
    let kv = flatdb_core::kv::mem::MemKv::new();
    let catalog = SnapshotCatalog::new(Box::new(flatdb_core::persisted::storage::catalog::RocksCatalogKv::new(
        kv.clone(),
        counters.clone(),
    )));
    let added = entry(state(1, 1), state(2, 2), 0, 10, SnapshotTier::PersistedBase);
    catalog.add(&added).unwrap();
    assert!(catalog.remove(&added.to, 1).unwrap());
    assert!(!catalog.remove(&added.to, 1).unwrap());
    assert_eq!(counters.catalog_puts.load(Ordering::Relaxed), 1);
    assert!(common::column_entries(&kv, flatdb_core::kv::Column::Metadata).is_empty());
}
