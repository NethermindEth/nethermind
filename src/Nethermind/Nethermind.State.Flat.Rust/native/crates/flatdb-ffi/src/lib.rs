//! C ABI of the FlatDB core, built as `libnethermind_flatdb` and called by `Nethermind.State.Flat.Rust`.
//!
//! Every export is a thin wrapper: it validates its pointers, runs inside [`guard::ffi_guard`] so that no panic
//! unwinds into C#, and returns an [`abi::FdbStatus`]. The message of a failure is kept per thread for
//! [`fdb_last_error`].
#![deny(unsafe_op_in_unsafe_fn)]

pub mod abi;
pub mod guard;
pub mod store;

use std::ptr;
use std::slice;
use std::sync::atomic::Ordering;

use flatdb_core::ABI_VERSION;
use flatdb_core::kv::rocks_api::ROCKS_API_SYMBOLS;
use flatdb_core::kv::{Column, GetInto, KvStore};

use abi::{FIELD_OFFSETS, FdbKvDesc, FdbStatus, FdbStoreCfg, FfiError, STRUCT_SIZES};
use guard::{copy_last_error, ffi_guard};
use store::{FdbStore, debug_put, with_kv};

/// Version of the ABI this library implements; the adapter refuses to run against another.
#[unsafe(no_mangle)]
pub extern "C" fn fdb_abi_version() -> u32 {
    ABI_VERSION
}

/// Writes the size of every `repr(C)` type, in [`STRUCT_SIZES`] order, and returns how many there are.
///
/// # Safety
/// `sizes` must be null or valid for writes of `capacity` values.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_struct_sizes(sizes: *mut u32, capacity: u32) -> u32 {
    // SAFETY: the caller guarantees room for `capacity` values.
    unsafe { copy_values(&STRUCT_SIZES, sizes, capacity) }
}

/// Writes the offset of every field of the `repr(C)` types with several fields, in [`FIELD_OFFSETS`] order, and
/// returns how many there are.
///
/// # Safety
/// `offsets` must be null or valid for writes of `capacity` values.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_struct_offsets(offsets: *mut u32, capacity: u32) -> u32 {
    // SAFETY: the caller guarantees room for `capacity` values.
    unsafe { copy_values(&FIELD_OFFSETS, offsets, capacity) }
}

/// Copies as many of `values` as fit into `target` and returns how many there are.
///
/// # Safety
/// `target` must be null or valid for writes of `capacity` values.
unsafe fn copy_values(values: &[u32], target: *mut u32, capacity: u32) -> u32 {
    if !target.is_null() {
        let count = values.len().min(capacity as usize);
        // SAFETY: the caller guarantees room for `capacity` values.
        unsafe { ptr::copy_nonoverlapping(values.as_ptr(), target, count) };
    }
    values.len() as u32
}

/// Copies the UTF-8 message of the last failed call on this thread into `buffer`, truncated to `capacity` bytes and
/// not NUL-terminated, and returns its full length.
///
/// # Safety
/// `buffer` must be null or valid for writes of `capacity` bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_last_error(buffer: *mut u8, capacity: usize) -> usize {
    let buffer: &mut [u8] = if buffer.is_null() || capacity == 0 {
        &mut []
    } else {
        // SAFETY: the caller guarantees room for `capacity` bytes.
        unsafe { slice::from_raw_parts_mut(buffer, capacity) }
    };
    copy_last_error(buffer)
}

/// Number of entries of the RocksApi table.
#[unsafe(no_mangle)]
pub extern "C" fn fdb_rocks_api_symbol_count() -> u32 {
    ROCKS_API_SYMBOLS.len() as u32
}

/// Points `name` at the ASCII symbol of RocksApi entry `index`, which stays valid for the life of the process.
///
/// # Safety
/// `name` and `name_len` must be valid for writes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_rocks_api_symbol(index: u32, name: *mut *const u8, name_len: *mut usize) -> i32 {
    ffi_guard(|| {
        let symbol = ROCKS_API_SYMBOLS
            .get(index as usize)
            .ok_or_else(|| FfiError::invalid_argument(format!("no RocksApi entry {index}")))?;
        // SAFETY: the caller passes valid out-pointers.
        let (name, name_len) = unsafe { (name.as_mut(), name_len.as_mut()) };
        let (Some(name), Some(name_len)) = (name, name_len) else {
            return Err(FfiError::invalid_argument("null out-pointer"));
        };
        *name = symbol.as_ptr();
        *name_len = symbol.len();
        Ok(())
    })
}

/// Creates a store over the key-value backend `kv` describes.
///
/// # Safety
/// `cfg`, `kv` and `store` must be valid. For a RocksDB backend, the database and column family handles must stay
/// open until [`fdb_store_destroy`] returns.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_store_create(
    cfg: *const FdbStoreCfg,
    kv: *const FdbKvDesc,
    store: *mut *mut FdbStore,
) -> i32 {
    ffi_guard(|| {
        // SAFETY: the caller passes valid pointers.
        let (cfg, kv, store) = unsafe { (cfg.as_ref(), kv.as_ref(), store.as_mut()) };
        let (Some(cfg), Some(kv), Some(store)) = (cfg, kv, store) else {
            return Err(FfiError::invalid_argument("null argument"));
        };
        // SAFETY: the caller keeps the described database open for the life of the store.
        *store = Box::into_raw(Box::new(unsafe { FdbStore::create(cfg, kv) }?));
        Ok(())
    })
}

/// Destroys a store. Every reader, iterator and batch created from it must be released first; otherwise the store
/// stays alive and [`FdbStatus::InvalidArgument`] is returned, as the database must stay open for them.
///
/// # Safety
/// `store` must be null or a store from [`fdb_store_create`] that is not used after it is destroyed.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_store_destroy(store: *mut FdbStore) -> i32 {
    ffi_guard(|| {
        if store.is_null() {
            return Ok(());
        }
        // SAFETY: the caller passes a live store.
        if unsafe { &*store }.kv.is_shared() {
            return Err(FfiError::invalid_argument(
                "the store still has open readers, iterators or batches",
            ));
        }
        // SAFETY: the caller hands back ownership of a store from `fdb_store_create`.
        drop(unsafe { Box::from_raw(store) });
        Ok(())
    })
}

/// Stores in `count` the number of database reads since the last call, which C# adds to the `TotalReads` metric.
///
/// # Safety
/// `store` must be a live store and `count` valid for writes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_store_take_read_count(store: *const FdbStore, count: *mut u64) -> i32 {
    ffi_guard(|| {
        // SAFETY: the caller passes a live store and a valid out-pointer.
        let (store, count) = unsafe { (store_ref(store)?, count.as_mut()) };
        let count = count.ok_or_else(|| FfiError::invalid_argument("null out-pointer"))?;
        *count = store.read_count.swap(0, Ordering::Relaxed);
        Ok(())
    })
}

/// Writes `value` under `key` in `column`, in a batch of its own with default write flags.
///
/// # Safety
/// `store` must be a live store and the slices valid for their lengths.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_debug_put(
    store: *const FdbStore,
    column: u32,
    key: *const u8,
    key_len: usize,
    value: *const u8,
    value_len: usize,
) -> i32 {
    ffi_guard(|| {
        // SAFETY: the caller passes a live store and valid slices.
        let (store, key, value) = unsafe { (store_ref(store)?, bytes(key, key_len)?, bytes(value, value_len)?) };
        let column = column_of(column)?;
        with_kv!(store, kv => debug_put(kv, column, key, value))
    })
}

/// Copies the latest value of `key` in `column` into `buffer` and stores its length in `value_len`, or -1 when it
/// is absent. A value longer than `capacity` yields [`FdbStatus::BufferTooSmall`] with its length in `value_len`.
///
/// # Safety
/// `store` must be a live store, the slices valid for their lengths and `value_len` valid for writes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_debug_get(
    store: *const FdbStore,
    column: u32,
    key: *const u8,
    key_len: usize,
    buffer: *mut u8,
    capacity: usize,
    value_len: *mut i64,
) -> i32 {
    ffi_guard(|| {
        // SAFETY: the caller passes a live store, valid slices and a valid out-pointer.
        let (store, key, buffer, value_len) = unsafe {
            (
                store_ref(store)?,
                bytes(key, key_len)?,
                bytes_mut(buffer, capacity)?,
                value_len.as_mut(),
            )
        };
        let value_len = value_len.ok_or_else(|| FfiError::invalid_argument("null out-pointer"))?;
        let column = column_of(column)?;

        store.read_count.fetch_add(1, Ordering::Relaxed);
        match with_kv!(store, kv => Ok(kv.get(column, key, buffer)?))? {
            GetInto::NotFound => *value_len = -1,
            GetInto::Found(len) => *value_len = len as i64,
            GetInto::BufferTooSmall(len) => {
                *value_len = len as i64;
                return Err(FfiError {
                    status: FdbStatus::BufferTooSmall,
                    message: format!("the value has {len} bytes, the buffer {capacity}"),
                });
            }
        }
        Ok(())
    })
}

/// # Safety
/// `store` must be null or a live store.
unsafe fn store_ref<'a>(store: *const FdbStore) -> Result<&'a FdbStore, FfiError> {
    // SAFETY: the caller passes null or a live store.
    unsafe { store.as_ref() }.ok_or_else(|| FfiError::invalid_argument("null store"))
}

/// # Safety
/// `data` must be valid for reads of `len` bytes unless `len` is 0.
unsafe fn bytes<'a>(data: *const u8, len: usize) -> Result<&'a [u8], FfiError> {
    match (data.is_null(), len) {
        (_, 0) => Ok(&[]),
        (true, _) => Err(FfiError::invalid_argument("null pointer with a non-zero length")),
        // SAFETY: the caller guarantees `len` readable bytes.
        (false, _) => Ok(unsafe { slice::from_raw_parts(data, len) }),
    }
}

/// # Safety
/// `data` must be valid for writes of `len` bytes unless `len` is 0.
unsafe fn bytes_mut<'a>(data: *mut u8, len: usize) -> Result<&'a mut [u8], FfiError> {
    match (data.is_null(), len) {
        (_, 0) => Ok(&mut []),
        (true, _) => Err(FfiError::invalid_argument("null pointer with a non-zero length")),
        // SAFETY: the caller guarantees `len` writable bytes.
        (false, _) => Ok(unsafe { slice::from_raw_parts_mut(data, len) }),
    }
}

fn column_of(index: u32) -> Result<Column, FfiError> {
    Column::from_index(index).ok_or_else(|| FfiError::invalid_argument(format!("unknown column {index}")))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::abi::FdbKvKind;
    use crate::store::AnyKv;
    use flatdb_core::kv::managed::ManagedKvVTable;
    use rstest::rstest;
    use std::ffi::c_void;

    fn desc(kind: FdbKvKind) -> FdbKvDesc {
        FdbKvDesc {
            kind: kind as u32,
            column_count: 0,
            rocks_api: ptr::null(),
            rocks_db: ptr::null_mut(),
            rocks_column_families: ptr::null(),
            readahead_size: 0,
            prefix_extractor_length: 0,
            verify_checksums: 1,
            wal_sync: 0,
            managed: ptr::null(),
        }
    }

    fn create(cfg: FdbStoreCfg, desc: FdbKvDesc) -> (i32, *mut FdbStore) {
        let mut store = ptr::null_mut();
        // SAFETY: every pointer is valid.
        let status = unsafe { fdb_store_create(&cfg, &desc, &mut store) };
        (status, store)
    }

    fn last_error() -> String {
        let mut buffer = [0u8; 256];
        // SAFETY: the buffer is valid.
        let len = unsafe { fdb_last_error(buffer.as_mut_ptr(), buffer.len()) };
        String::from_utf8_lossy(&buffer[..len.min(buffer.len())]).into_owned()
    }

    const CFG: FdbStoreCfg = FdbStoreCfg {
        abi_version: ABI_VERSION,
        layout: 0,
    };

    #[test]
    fn struct_sizes_and_symbols_are_reported() {
        let mut sizes = [0u32; 8];
        // SAFETY: the buffer holds 8 values.
        let count = unsafe { fdb_struct_sizes(sizes.as_mut_ptr(), sizes.len() as u32) };
        assert_eq!(&sizes[..count as usize], &STRUCT_SIZES);
        let mut offsets = [0u32; 32];
        // SAFETY: the buffer holds 32 values.
        let count = unsafe { fdb_struct_offsets(offsets.as_mut_ptr(), offsets.len() as u32) };
        assert_eq!(&offsets[..count as usize], &FIELD_OFFSETS);

        let mut name = ptr::null();
        let mut name_len = 0usize;
        for index in 0..fdb_rocks_api_symbol_count() {
            // SAFETY: the out-pointers are valid.
            assert_eq!(
                unsafe { fdb_rocks_api_symbol(index, &mut name, &mut name_len) },
                FdbStatus::Ok as i32
            );
            // SAFETY: the export points at a static symbol of `name_len` bytes.
            let symbol = unsafe { slice::from_raw_parts(name, name_len) };
            assert_eq!(symbol, ROCKS_API_SYMBOLS[index as usize].as_bytes());
        }
        // SAFETY: as above.
        let past_end = unsafe { fdb_rocks_api_symbol(fdb_rocks_api_symbol_count(), &mut name, &mut name_len) };
        assert_eq!(past_end, FdbStatus::InvalidArgument as i32);
    }

    #[test]
    fn mem_store_round_trips_counts_reads_and_outlives_its_readers() {
        let (status, store) = create(CFG, desc(FdbKvKind::Mem));
        assert_eq!(status, FdbStatus::Ok as i32);

        let (key, value) = (b"\x01\x02".as_slice(), b"value".as_slice());
        // SAFETY: the store is live and the slices valid.
        let put = unsafe {
            fdb_debug_put(
                store,
                Column::Storage as u32,
                key.as_ptr(),
                key.len(),
                value.as_ptr(),
                5,
            )
        };
        assert_eq!(put, FdbStatus::Ok as i32);

        let get = |column: u32, capacity: usize| {
            let mut buffer = [0u8; 16];
            let mut len = 0i64;
            // SAFETY: as above.
            let status = unsafe {
                fdb_debug_get(
                    store,
                    column,
                    key.as_ptr(),
                    key.len(),
                    buffer.as_mut_ptr(),
                    capacity,
                    &mut len,
                )
            };
            (status, len, buffer[..len.clamp(0, capacity as i64) as usize].to_vec())
        };

        assert_eq!(
            get(Column::Storage as u32, 16),
            (FdbStatus::Ok as i32, 5, value.to_vec())
        );
        assert_eq!(get(Column::Account as u32, 16), (FdbStatus::Ok as i32, -1, vec![]));
        assert_eq!(get(Column::Storage as u32, 2).0, FdbStatus::BufferTooSmall as i32);
        assert_eq!(get(Column::Storage as u32, 2).1, 5);
        assert_eq!(get(7, 16).0, FdbStatus::InvalidArgument as i32);
        assert!(last_error().contains("unknown column 7"));

        let take_read_count = || {
            let mut count = u64::MAX;
            // SAFETY: the store is live and the out-pointer valid.
            assert_eq!(
                unsafe { fdb_store_take_read_count(store, &mut count) },
                FdbStatus::Ok as i32
            );
            count
        };
        assert_eq!(take_read_count(), 4, "every get of a valid column reads the database");
        assert_eq!(take_read_count(), 0);

        // SAFETY: the store is live.
        let AnyKv::Mem(kv) = &unsafe { &*store }.kv else {
            unreachable!()
        };
        let reader = kv.reader().unwrap();
        // SAFETY: the store is destroyed only once, after its reader.
        assert_eq!(unsafe { fdb_store_destroy(store) }, FdbStatus::InvalidArgument as i32);
        drop(reader);
        assert_eq!(unsafe { fdb_store_destroy(store) }, FdbStatus::Ok as i32);
    }

    #[rstest]
    #[case(FdbStoreCfg { abi_version: ABI_VERSION + 1, layout: 0 }, desc(FdbKvKind::Mem), FdbStatus::InvalidArgument, "ABI version")]
    #[case(FdbStoreCfg { abi_version: ABI_VERSION, layout: 4 }, desc(FdbKvKind::Mem), FdbStatus::InvalidArgument, "layout")]
    #[case(CFG, FdbKvDesc { kind: 9, ..desc(FdbKvKind::Mem) }, FdbStatus::InvalidArgument, "backend")]
    #[case(CFG, desc(FdbKvKind::Rocks), FdbStatus::InvalidArgument, "column family")]
    #[case(CFG, desc(FdbKvKind::Managed), FdbStatus::InvalidArgument, "managed")]
    fn invalid_descriptors_are_rejected(
        #[case] cfg: FdbStoreCfg,
        #[case] desc: FdbKvDesc,
        #[case] expected: FdbStatus,
        #[case] message: &str,
    ) {
        let (status, store) = create(cfg, desc);
        assert_eq!(status, expected as i32);
        assert!(store.is_null());
        assert!(last_error().contains(message), "{}", last_error());
    }

    #[test]
    fn managed_backend_is_not_supported_yet() {
        let vtable = ManagedKvVTable {
            ctx: ptr::null_mut(),
            get: None,
            snapshot_create: None,
            snapshot_release: None,
            iterator_create: None,
            iterator_next: None,
            iterator_release: None,
            write: None,
            flush: None,
        };

        let (status, store) = create(
            CFG,
            FdbKvDesc {
                managed: &vtable,
                ..desc(FdbKvKind::Managed)
            },
        );
        assert_eq!(status, FdbStatus::NotSupported as i32);
        assert!(store.is_null());
    }

    #[test]
    fn rocks_store_rejects_an_incomplete_api_table() {
        let handles = [ptr::dangling_mut::<c_void>(); Column::COUNT];
        // SAFETY: no entry is ever called.
        let api = unsafe { flatdb_core::kv::rocks_api::RocksApi::from_resolver(|_| ptr::null()) };
        let desc = FdbKvDesc {
            column_count: Column::COUNT as u32,
            rocks_api: &api,
            rocks_db: ptr::dangling_mut(),
            rocks_column_families: handles.as_ptr(),
            ..desc(FdbKvKind::Rocks)
        };

        let (status, _) = create(CFG, desc);
        assert_eq!(status, FdbStatus::InvalidArgument as i32);
        assert!(last_error().contains("RocksApi is missing"));
    }
}
