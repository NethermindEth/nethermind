//! The managed backend over a fake C# store ([`common::loopback`]): reads, snapshots, bounded iterators and batches
//! cross the callback table and land in the store behind it.

mod common;

use common::loopback::Loopback;
use flatdb_core::kv::mem::MemWrite;
use flatdb_core::kv::{Column, GetInto, IterOptions, KvError, KvIterator, KvReader, KvStore, ReadFlags, WriteFlags};
use rstest::rstest;

fn keys(reader: &impl KvReader, column: Column, options: IterOptions<'_>) -> Vec<Vec<u8>> {
    let mut iter = reader.iter(column, options).unwrap();
    let mut keys = Vec::new();
    iter.seek_to_first();
    while iter.valid() {
        keys.push(iter.key().to_vec());
        iter.next();
    }
    keys
}

#[rstest]
fn reads_snapshots_iterators_and_batches_cross_the_callbacks(#[values(true, false)] snapshots: bool) {
    let loopback = Loopback::new(snapshots);
    let kv = loopback.kv();

    let mut batch = kv.write_batch().unwrap();
    for key in [b"a", b"b", b"c", b"d"] {
        batch.put(Column::StateNodes, key, key, WriteFlags::NONE).unwrap();
    }
    batch
        .put(Column::Storage, b"long", &[7; 300], WriteFlags::NONE)
        .unwrap();
    batch.put(Column::Account, b"empty", &[], WriteFlags::NONE).unwrap();
    batch.commit().unwrap();

    let mut buffer = [0u8; 4];
    assert_eq!(
        kv.get(Column::StateNodes, b"a", &mut buffer).unwrap(),
        GetInto::Found(1)
    );
    assert_eq!(
        kv.get(Column::Storage, b"long", &mut buffer).unwrap(),
        GetInto::BufferTooSmall(300)
    );
    assert_eq!(
        kv.get(Column::Storage, b"absent", &mut buffer).unwrap(),
        GetInto::NotFound
    );
    assert_eq!(
        kv.get_with(Column::Storage, b"long", <[u8]>::to_vec).unwrap(),
        Some(vec![7; 300])
    );
    assert_eq!(
        kv.get_with(Column::Account, b"empty", <[u8]>::to_vec).unwrap(),
        Some(vec![])
    );

    let reader = kv.reader(false).unwrap();
    let mut batch = kv.write_batch().unwrap();
    batch.put(Column::StateNodes, b"a", b"new", WriteFlags::NONE).unwrap();
    batch.delete(Column::StateNodes, b"b", WriteFlags::NONE).unwrap();
    batch.delete_range(Column::StateNodes, b"c", b"d");
    batch.commit().unwrap();

    let at_reader = reader
        .get_with(Column::StateNodes, b"a", ReadFlags::NONE, <[u8]>::to_vec)
        .unwrap();
    let bounded = IterOptions {
        lower_bound: Some(b"b"),
        upper_bound: Some(b"d"),
        flags: ReadFlags::NONE,
    };
    if snapshots {
        assert_eq!(at_reader, Some(b"a".to_vec()), "the reader sees its snapshot");
        assert_eq!(
            keys(&reader, Column::StateNodes, bounded),
            [b"b".to_vec(), b"c".to_vec()]
        );
    } else {
        assert_eq!(
            at_reader,
            Some(b"new".to_vec()),
            "without snapshots the reader sees the latest state"
        );
        assert!(keys(&reader, Column::StateNodes, bounded).is_empty());
    }
    let empty = IterOptions {
        lower_bound: Some(b"d"),
        upper_bound: Some(b"b"),
        flags: ReadFlags::NONE,
    };
    assert!(keys(&reader, Column::StateNodes, empty).is_empty());
    assert_eq!(
        keys(&kv.reader(false).unwrap(), Column::StateNodes, IterOptions::default()),
        [b"a".to_vec(), b"d".to_vec()]
    );

    drop(reader);
    assert_eq!(loopback.open_handles(), 0, "every snapshot and iterator is released");
    assert!(loopback.gets() >= 6, "reads go through the callbacks");
    assert!(!kv.is_shared());
}

/// The no-WAL chunking and the commit flags of `RocksDbWriteBatch` reach the C# store unchanged.
#[test]
fn batches_keep_their_chunking_and_flags_and_flushes_cross() {
    let loopback = Loopback::new(true);
    let kv = loopback.kv();

    let mut batch = kv.write_batch().unwrap();
    for i in 0..300u32 {
        batch
            .put(Column::Account, &i.to_be_bytes(), b"v", WriteFlags::DISABLE_WAL)
            .unwrap();
    }
    batch.put(Column::Metadata, b"state", b"s", WriteFlags::NONE).unwrap();
    batch.commit().unwrap();
    kv.flush_wal(true).unwrap();
    kv.flush().unwrap();

    assert_eq!(
        loopback.mem().writes(),
        [
            MemWrite {
                flags: WriteFlags::DISABLE_WAL,
                operations: 256
            },
            MemWrite {
                flags: WriteFlags::NONE,
                operations: 45
            },
        ]
    );
    assert_eq!(loopback.mem().flushes(), 1);
    assert_eq!(kv.property(Column::Account, "rocksdb.estimate-num-keys").unwrap(), None);
    kv.set_write_buffer(Column::Account, 1).unwrap();
}

#[test]
fn a_failing_callback_is_an_error() {
    let loopback = Loopback::new(true);
    let kv = loopback.kv();
    loopback.fail_writes();

    let mut batch = kv.write_batch().unwrap();
    batch.put(Column::Account, b"k", b"v", WriteFlags::NONE).unwrap();

    assert_eq!(
        batch.commit(),
        Err(KvError::Callback {
            operation: "write",
            status: 3
        })
    );
    assert_eq!(kv.get_with(Column::Account, b"k", <[u8]>::to_vec).unwrap(), None);
}
