//! The RocksDB backend against the `librocksdb.so` of the `Nethermind.RocksDbBindings` NuGet package, opened through
//! its C API the way the C# side opens the flat DB. Skipped when the library is not in the NuGet cache; set
//! `ROCKSDB_LIB` to test another copy.
#![cfg(feature = "rocksdb-it")]

use std::ffi::{CStr, CString, c_char, c_int, c_void};
use std::path::PathBuf;
use std::ptr;

use flatdb_core::kv::rocks::{RocksDesc, RocksKv};
use flatdb_core::kv::rocks_api::RocksApi;
use flatdb_core::kv::{
    Column, GetInto, IterOptions, KvError, KvIterator, KvReader, KvStore, ReadFlags, WriteBatch, WriteFlags,
};
use libloading::Library;

const NUGET_LIBRARY: &str =
    ".nuget/packages/nethermind.rocksdbbindings/11.8.1-preview.140/runtimes/linux-x64/native/librocksdb.so";

/// Column family names in `FlatDbColumns` order.
const COLUMN_NAMES: [&CStr; Column::COUNT] = [
    c"Metadata",
    c"Account",
    c"Storage",
    c"StateNodes",
    c"StateTopNodes",
    c"StorageNodes",
    c"FallbackNodes",
];

type OptionsCreate = unsafe extern "C" fn() -> *mut c_void;
type OptionsSetFlag = unsafe extern "C" fn(*mut c_void, u8);
type OptionsDestroy = unsafe extern "C" fn(*mut c_void);
type OpenColumnFamilies = unsafe extern "C" fn(
    *const c_void,
    *const c_char,
    c_int,
    *const *const c_char,
    *const *const c_void,
    *mut *mut c_void,
    *mut *mut c_char,
) -> *mut c_void;
type Close = unsafe extern "C" fn(*mut c_void);
type HandleDestroy = unsafe extern "C" fn(*mut c_void);

fn library_path() -> Option<PathBuf> {
    let path = match std::env::var_os("ROCKSDB_LIB") {
        Some(path) => PathBuf::from(path),
        None => PathBuf::from(std::env::var_os("HOME")?).join(NUGET_LIBRARY),
    };
    path.exists().then_some(path)
}

/// A database with the default column family and the seven flat-DB columns, closed on drop.
struct TestDb {
    library: Library,
    _dir: tempfile::TempDir,
    options: *mut c_void,
    db: *mut c_void,
    /// The default column family first, then the flat-DB columns.
    handles: Vec<*mut c_void>,
}

impl TestDb {
    fn open() -> Option<Self> {
        let Some(path) = library_path() else {
            eprintln!("skipped: librocksdb.so not found, restore the NuGet packages or set ROCKSDB_LIB");
            return None;
        };

        // SAFETY: librocksdb has no load-time side effects beyond static initialization.
        let library = unsafe { Library::new(&path) }.expect("librocksdb loads");
        let dir = tempfile::tempdir().unwrap();
        let db_path = CString::new(dir.path().to_str().unwrap()).unwrap();
        let mut names: Vec<*const c_char> = vec![c"default".as_ptr()];
        names.extend(COLUMN_NAMES.iter().map(|name| name.as_ptr()));

        // SAFETY: the C API is called as rocksdb/c.h declares it, and every buffer outlives the call using it.
        unsafe {
            let options = symbol::<OptionsCreate>(&library, "rocksdb_options_create")();
            symbol::<OptionsSetFlag>(&library, "rocksdb_options_set_create_if_missing")(options, 1);
            symbol::<OptionsSetFlag>(&library, "rocksdb_options_set_create_missing_column_families")(options, 1);

            let column_options: Vec<*const c_void> = vec![options.cast_const(); names.len()];
            let mut handles = vec![ptr::null_mut(); names.len()];
            let mut error = ptr::null_mut();
            let db = symbol::<OpenColumnFamilies>(&library, "rocksdb_open_column_families")(
                options,
                db_path.as_ptr(),
                names.len() as c_int,
                names.as_ptr(),
                column_options.as_ptr(),
                handles.as_mut_ptr(),
                &mut error,
            );
            assert!(error.is_null(), "open failed: {:?}", CStr::from_ptr(error));

            Some(Self {
                library,
                _dir: dir,
                options,
                db,
                handles,
            })
        }
    }

    fn api(&self) -> RocksApi {
        // SAFETY: every symbol resolves to the RocksDB function it names.
        unsafe {
            RocksApi::from_resolver(|name| {
                self.library
                    .get::<unsafe extern "C" fn()>(name.as_bytes())
                    .map_or(ptr::null(), |function| *function as *const c_void)
            })
        }
    }

    fn kv(&self, wal_sync: bool) -> RocksKv {
        let desc = RocksDesc {
            db: self.db,
            column_families: self.handles[1..].try_into().unwrap(),
            verify_checksums: true,
            wal_sync,
            readahead_size: 256 * 1024,
            prefix_extractor_length: 0,
        };
        // SAFETY: the handles stay open until `self` is dropped, after every store the test creates.
        unsafe { RocksKv::new(&self.api(), desc) }.expect("the RocksApi table is complete")
    }
}

impl Drop for TestDb {
    fn drop(&mut self) {
        // SAFETY: every store over the database was dropped before it.
        unsafe {
            let destroy = symbol::<HandleDestroy>(&self.library, "rocksdb_column_family_handle_destroy");
            for handle in &self.handles {
                destroy(*handle);
            }
            symbol::<Close>(&self.library, "rocksdb_close")(self.db);
            symbol::<OptionsDestroy>(&self.library, "rocksdb_options_destroy")(self.options);
        }
    }
}

/// # Safety
/// `T` must be the type of the function `name` names.
unsafe fn symbol<T: Copy>(library: &Library, name: &str) -> T {
    // SAFETY: the caller vouches for the type.
    *unsafe { library.get::<T>(name.as_bytes()) }.unwrap_or_else(|error| panic!("{name}: {error}"))
}

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

    let before = kv.reader().unwrap();
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

    let reader = kv.reader().unwrap();
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
        keys(&kv.reader().unwrap(), Column::StateNodes, IterOptions::default()),
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
