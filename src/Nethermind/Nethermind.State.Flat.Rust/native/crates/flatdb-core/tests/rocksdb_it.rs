//! The RocksDB backend against the `librocksdb.so` of the `Nethermind.RocksDbBindings` NuGet package, opened through
//! its C API the way the C# side opens the flat DB (see [`common::rocks`]). Skipped when the library is not in the
//! NuGet cache; set `ROCKSDB_LIB` to test another copy.
#![cfg(feature = "rocksdb-it")]

mod common;

use common::rocks::TestDb;
use flatdb_core::kv::rocks::RocksKv;
use flatdb_core::kv::{
    Column, GetInto, IterOptions, KvError, KvIterator, KvReader, KvStore, ReadFlags, WriteBatch, WriteFlags,
};

fn get(kv: &RocksKv, column: Column, key: &[u8]) -> Option<Vec<u8>> {
    kv.get_with(column, key, <[u8]>::to_vec).unwrap()
}

fn commit(kv: &RocksKv, write: impl FnOnce(&mut WriteBatch<<RocksKv as KvStore>::RawBatch>)) {
    let mut batch = kv.write_batch().unwrap();
    write(&mut batch);
    batch.commit().unwrap();
}

fn keys(reader: &impl KvReader, column: Column, options: IterOptions<'_>) -> Vec<Vec<u8>> {
    let mut iter = reader.iter(column, options).unwrap();
    let mut keys = Vec::new();
    iter.seek_to_first();
    while iter.valid() {
        keys.push(iter.key().to_vec());
        iter.next();
    }
    iter.status().unwrap();
    keys
}

#[test]
fn reads_writes_and_snapshots_go_through_the_c_api() {
    let Some(db) = TestDb::open() else { return };
    let kv = db.kv(false);

    commit(&kv, |batch| {
        for column in Column::ALL {
            batch.put(column, b"key", &[column as u8; 3], WriteFlags::NONE).unwrap();
        }
        batch
            .put(Column::Storage, b"long", &[7; 100], WriteFlags::NONE)
            .unwrap();
    });

    let mut buffer = [0u8; 16];
    for column in Column::ALL {
        assert_eq!(
            kv.get(column, b"key", &mut buffer).unwrap(),
            GetInto::Found(3),
            "{column:?}"
        );
        assert_eq!(&buffer[..3], &[column as u8; 3]);
    }
    assert_eq!(
        kv.get(Column::Storage, b"long", &mut buffer).unwrap(),
        GetInto::BufferTooSmall(100)
    );
    assert_eq!(
        kv.get(Column::Storage, b"absent", &mut buffer).unwrap(),
        GetInto::NotFound
    );
    assert_eq!(get(&kv, Column::Storage, b"long"), Some(vec![7; 100]));

    let before = kv.reader(false).unwrap();
    commit(&kv, |batch| {
        batch.put(Column::Account, b"key", b"new", WriteFlags::NONE).unwrap();
        batch.delete(Column::Storage, b"key", WriteFlags::NONE).unwrap();
    });

    let at_snapshot = |column, key: &[u8]| {
        before
            .get_with(column, key, ReadFlags::HINT_CACHE_MISS, <[u8]>::to_vec)
            .unwrap()
    };
    assert_eq!(
        at_snapshot(Column::Account, b"key"),
        Some(vec![Column::Account as u8; 3])
    );
    assert_eq!(
        at_snapshot(Column::Storage, b"key"),
        Some(vec![Column::Storage as u8; 3])
    );
    assert_eq!(
        before
            .get(Column::Account, b"key", ReadFlags::NONE, &mut buffer)
            .unwrap(),
        GetInto::Found(3)
    );
    assert_eq!(get(&kv, Column::Account, b"key"), Some(b"new".to_vec()));
    assert_eq!(get(&kv, Column::Storage, b"key"), None);
}

#[test]
fn iterators_honour_bounds_and_range_deletes() {
    let Some(db) = TestDb::open() else { return };
    let kv = db.kv(false);

    commit(&kv, |batch| {
        for key in [b"a", b"b", b"c", b"d"] {
            batch.put(Column::StateNodes, key, key, WriteFlags::NONE).unwrap();
        }
        batch
            .put(Column::StorageNodes, b"b", b"other column", WriteFlags::NONE)
            .unwrap();
    });

    let reader = kv.reader(false).unwrap();
    let bounded = IterOptions {
        lower_bound: Some(b"b"),
        upper_bound: Some(b"d"),
        flags: ReadFlags::HINT_READ_AHEAD,
    };
    assert_eq!(
        keys(&reader, Column::StateNodes, bounded),
        [b"b".to_vec(), b"c".to_vec()]
    );
    assert_eq!(keys(&reader, Column::StateNodes, IterOptions::default()).len(), 4);

    let mut iter = reader.iter(Column::StateNodes, IterOptions::default()).unwrap();
    iter.seek(b"bb");
    assert_eq!((iter.key(), iter.value()), (&b"c"[..], &b"c"[..]));
    iter.prev();
    assert_eq!(iter.key(), b"b");
    iter.seek_for_prev(b"bb");
    assert_eq!(iter.key(), b"b");
    iter.seek_to_last();
    assert_eq!(iter.key(), b"d");
    iter.next();
    assert!(!iter.valid());
    drop(iter);

    commit(&kv, |batch| batch.delete_range(Column::StateNodes, b"b", b"d"));
    assert_eq!(
        keys(&kv.reader(false).unwrap(), Column::StateNodes, IterOptions::default()),
        [b"a".to_vec(), b"d".to_vec()]
    );
    assert_eq!(
        keys(&reader, Column::StateNodes, IterOptions::default()).len(),
        4,
        "the snapshot keeps the range"
    );
    assert_eq!(get(&kv, Column::StorageNodes, b"b"), Some(b"other column".to_vec()));
}

#[test]
fn no_wal_batches_write_out_in_chunks_and_flushes_succeed() {
    let Some(db) = TestDb::open() else { return };
    let kv = db.kv(false);

    let mut batch = kv.write_batch().unwrap();
    for i in 0..WriteBatch::<<RocksKv as KvStore>::RawBatch>::MAX_WRITES_ON_NO_WAL as u32 {
        batch
            .put(Column::Account, &i.to_be_bytes(), b"v", WriteFlags::DISABLE_WAL)
            .unwrap();
    }
    assert_eq!(batch.pending(), 0, "the 256th no-WAL write writes the batch out");
    assert_eq!(get(&kv, Column::Account, &0u32.to_be_bytes()), Some(b"v".to_vec()));
    batch.put(Column::Metadata, b"state", b"s", WriteFlags::NONE).unwrap();
    batch.commit().unwrap();
    assert_eq!(get(&kv, Column::Metadata, b"state"), Some(b"s".to_vec()));

    kv.flush_wal(true).unwrap();
    kv.flush().unwrap();
    kv.set_write_buffer(Column::Storage, 32 * 1024 * 1024).unwrap();
    assert_eq!(
        kv.property(Column::Storage, "rocksdb.options-statistics").unwrap(),
        None
    );
    let keys: u64 = kv
        .property(Column::Account, "rocksdb.estimate-num-keys")
        .unwrap()
        .unwrap()
        .parse()
        .unwrap();
    assert!(keys >= 256, "{keys}");
}

#[test]
fn rocksdb_errors_are_mapped_and_freed() {
    let Some(db) = TestDb::open() else { return };
    // RocksDB refuses a synced write that skips the WAL, which C# would hit too with WriteAheadLogSync.
    let kv = db.kv(true);

    let mut batch = kv.write_batch().unwrap();
    batch.put(Column::Account, b"k", b"v", WriteFlags::DISABLE_WAL).unwrap();
    let error = batch.commit().unwrap_err();

    assert!(
        matches!(&error, KvError::RocksDb { fatal: false, message } if message.contains("WAL")),
        "{error:?}"
    );
    assert_eq!(get(&kv, Column::Account, b"k"), None);
}

/// 8-byte keys of `StateNodes`, every third one written, as a trie walk reads them.
fn node_key(i: u32) -> [u8; 8] {
    let mut key = [0u8; 8];
    key[4..].copy_from_slice(&(i * 3).to_be_bytes());
    key
}

/// Port of the `ReadFlags.HintReadAhead` path of `RocksDbReader.Get` on a `FullScan` snapshot: point reads served by
/// stepping a pooled iterator forward agree with plain reads, for present and absent keys, in order and out of order,
/// across pools, and never see writes made after the snapshot.
#[test]
fn read_ahead_reads_agree_with_plain_reads_at_the_snapshot() {
    let Some(db) = TestDb::open() else { return };
    let kv = db.kv(false);
    commit(&kv, |batch| {
        for i in 0..200 {
            batch
                .put(Column::StateNodes, &node_key(i), &i.to_be_bytes(), WriteFlags::NONE)
                .unwrap();
        }
    });

    let scan = kv.reader(true).unwrap();
    let plain = kv.reader(false).unwrap();
    commit(&kv, |batch| {
        batch
            .put(Column::StateNodes, &node_key(1), b"late", WriteFlags::NONE)
            .unwrap();
        batch
            .put(Column::StateNodes, &[0, 0, 0, 0, 0, 0, 0, 4], b"late", WriteFlags::NONE)
            .unwrap();
    });

    // Forward over every key and the gaps between them, then backwards, then far jumps, in all three pools.
    let mut keys: Vec<[u8; 8]> = (0..600u32)
        .map(|i| {
            let mut key = [0u8; 8];
            key[4..].copy_from_slice(&i.to_be_bytes());
            key
        })
        .collect();
    keys.extend(keys.clone().into_iter().rev());
    keys.extend([node_key(5), node_key(150), node_key(7), node_key(199), node_key(0)]);
    for flags in [
        ReadFlags::HINT_READ_AHEAD,
        ReadFlags::HINT_READ_AHEAD | ReadFlags::HINT_READ_AHEAD_2,
        ReadFlags::HINT_READ_AHEAD | ReadFlags::HINT_READ_AHEAD_3 | ReadFlags::HINT_CACHE_MISS,
    ] {
        for key in &keys {
            let read_ahead = scan.get_with(Column::StateNodes, key, flags, <[u8]>::to_vec).unwrap();
            let expected = plain
                .get_with(Column::StateNodes, key, ReadFlags::NONE, <[u8]>::to_vec)
                .unwrap();
            assert_eq!(read_ahead, expected, "{key:?} {flags:?}");
        }
    }

    // Other threads get iterators of their own.
    std::thread::scope(|scope| {
        for _ in 0..4 {
            scope.spawn(|| {
                for i in 0..200 {
                    let value = scan
                        .get_with(
                            Column::StateNodes,
                            &node_key(i),
                            ReadFlags::HINT_READ_AHEAD,
                            <[u8]>::to_vec,
                        )
                        .unwrap();
                    assert_eq!(value, Some(i.to_be_bytes().to_vec()));
                }
            });
        }
    });
    assert_eq!(
        kv.reader(true)
            .unwrap()
            .get_with(
                Column::StateNodes,
                &node_key(1),
                ReadFlags::HINT_READ_AHEAD,
                <[u8]>::to_vec
            )
            .unwrap(),
        Some(b"late".to_vec()),
        "a new snapshot sees the later write"
    );
}

/// A store without a read-ahead size opts out of read-ahead iterators, as `ReadAheadSize = 0` does in C#.
#[test]
fn read_ahead_reads_without_a_read_ahead_size_fall_back_to_plain_reads() {
    let Some(db) = TestDb::open() else { return };
    let kv = db.kv_with_readahead(false, 0);
    commit(&kv, |batch| {
        batch
            .put(Column::StateNodes, &node_key(1), b"v", WriteFlags::NONE)
            .unwrap()
    });

    let reader = kv.reader(true).unwrap();
    for key in [node_key(1), node_key(2), node_key(1)] {
        let value = reader
            .get_with(Column::StateNodes, &key, ReadFlags::HINT_READ_AHEAD, <[u8]>::to_vec)
            .unwrap();
        assert_eq!(value, (key == node_key(1)).then(|| b"v".to_vec()));
    }
}
