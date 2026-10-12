//! A RocksDB database opened through the C API of the `librocksdb.so` of the `Nethermind.RocksDbBindings` NuGet
//! package, the way the C# side opens the flat DB. Absent when the library is not in the NuGet cache; set
//! `ROCKSDB_LIB` to use another copy.

use std::ffi::{CStr, CString, c_char, c_int, c_void};
use std::path::PathBuf;
use std::ptr;

use flatdb_core::kv::Column;
use flatdb_core::kv::rocks::{RocksDesc, RocksKv};
use flatdb_core::kv::rocks_api::RocksApi;
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
pub struct TestDb {
    library: Library,
    _dir: tempfile::TempDir,
    options: *mut c_void,
    db: *mut c_void,
    /// The default column family first, then the flat-DB columns.
    handles: Vec<*mut c_void>,
}

impl TestDb {
    /// Opens a fresh database, or `None` (with a notice) when the library is missing.
    pub fn open() -> Option<Self> {
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

    /// A store over the database, with the read-ahead size C# uses by default.
    pub fn kv(&self, wal_sync: bool) -> RocksKv {
        self.kv_with_readahead(wal_sync, 256 * 1024)
    }

    pub fn kv_with_readahead(&self, wal_sync: bool, readahead_size: u64) -> RocksKv {
        let desc = RocksDesc {
            db: self.db,
            column_families: self.handles[1..].try_into().unwrap(),
            verify_checksums: true,
            wal_sync,
            readahead_size,
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
