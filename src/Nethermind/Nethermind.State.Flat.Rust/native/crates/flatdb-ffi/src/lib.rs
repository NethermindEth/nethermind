//! C ABI of the FlatDB core, built as `libnethermind_flatdb` and called by `Nethermind.State.Flat.Rust`.
//!
//! Every export is a thin wrapper: it validates its pointers, runs inside [`guard::ffi_guard`] so that no panic
//! unwinds into C#, and returns an [`abi::FdbStatus`]. The message of a failure is kept per thread for
//! [`fdb_last_error`]. A panic in a call on a store's state, or in a job of the workers of its manager, poisons the
//! store (see [`guard::Poison`]).
#![deny(unsafe_op_in_unsafe_fn)]

pub mod abi;
pub mod calls;
#[cfg(any(test, feature = "test-api"))]
pub mod faults;
pub mod guard;
pub mod manager;
pub mod manager_exports;
pub mod op_stream;
pub mod persistence;
pub mod snapshot_stream;
pub mod store;

use std::ptr;
use std::slice;
use std::sync::atomic::Ordering;

use flatdb_core::ABI_VERSION;
use flatdb_core::kv::rocks_api::ROCKS_API_SYMBOLS;
#[cfg(any(test, feature = "test-api"))]
use flatdb_core::kv::{Column, KvStore};
use flatdb_core::kv::{GetInto, ReadFlags, WriteFlags};
use flatdb_core::log_ring;
use flatdb_core::persistence::{PersistenceError, ReaderFlags};
use flatdb_core::seam;
use flatdb_core::types::{TreePath, U256};

use abi::{
    FIELD_OFFSETS, FdbKvDesc, FdbStats, FdbStatus, FdbStoreCfg, FdbStoreInfo, FfiAccount, FfiError, FfiStateId,
    STRUCT_SIZES,
};
use calls::Export;
use guard::{copy_last_error, ffi_guard};
use store::{FdbBatch, FdbIter, FdbReader, FdbStore};
#[cfg(any(test, feature = "test-api"))]
use store::{debug_put, with_kv};

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
    ffi_guard(Export::Other, || {
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

/// Creates a store over the key-value backend `kv` describes: the persistence stack of `cfg`, after checking the
/// database's layout and resolving its slot encoding, which `info` reports.
///
/// # Safety
/// `cfg`, `kv`, `store` and `info` must be valid. For a RocksDB backend, the database and column family handles must
/// stay open until [`fdb_store_destroy`] returns; for a managed backend, so must the callbacks' context.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_store_create(
    cfg: *const FdbStoreCfg,
    kv: *const FdbKvDesc,
    store: *mut *mut FdbStore,
    info: *mut FdbStoreInfo,
) -> i32 {
    ffi_guard(Export::StoreCreate, || {
        // SAFETY: the caller passes valid pointers.
        let (cfg, kv, store, info) = unsafe { (cfg.as_ref(), kv.as_ref(), out(store)?, out(info)?) };
        let (Some(cfg), Some(kv)) = (cfg, kv) else {
            return Err(FfiError::invalid_argument("null argument"));
        };
        // SAFETY: the caller keeps the described database open for the life of the store.
        let created = unsafe { FdbStore::create(cfg, kv) }?;
        *info = created.info;
        *store = Box::into_raw(Box::new(created));
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
    ffi_guard(Export::StoreDestroy, || {
        if store.is_null() {
            return Ok(());
        }
        // SAFETY: the caller passes a live store.
        if unsafe { &*store }.has_open_handles() {
            return Err(FfiError::invalid_argument(
                "the store still has open readers, iterators or batches",
            ));
        }
        // SAFETY: the caller hands back ownership of a store from `fdb_store_create`.
        drop(unsafe { Box::from_raw(store) });
        Ok(())
    })
}

/// Stores in `stats` the metrics gathered since the last call, which C# adds to its own.
///
/// # Safety
/// `store` must be a live store and `stats` valid for writes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_store_take_stats(store: *const FdbStore, stats: *mut FdbStats) -> i32 {
    ffi_guard(Export::StoreTakeStats, || {
        // SAFETY: the caller passes a live store and a valid out-pointer.
        let (store, stats) = unsafe { (store_ref(store)?, out(stats)?) };
        *stats = store.take_stats();
        Ok(())
    })
}

/// Takes the oldest record the store logged, or with a null `store` the oldest one logged outside any store: stores
/// whether there is one in `has`, the C# class it is the log line of (`flatdb_core::log_ring::LogSource`) in `source`,
/// its level (1 error, 2 warn, 3 info, 4 debug, 5 trace) in `level`, and its message, truncated to `capacity` bytes of
/// UTF-8, in `buffer` with its full length in `len`.
///
/// # Safety
/// `store` must be null or a live store, `buffer` valid for writes of `capacity` bytes and the other pointers for
/// writes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_store_log_take(
    store: *const FdbStore,
    source: *mut u32,
    level: *mut u8,
    buffer: *mut u8,
    capacity: usize,
    len: *mut usize,
    has: *mut u8,
) -> i32 {
    ffi_guard(Export::StoreLogTake, || {
        // SAFETY: the caller passes null or a live store and valid pointers.
        let (store, source, level, buffer, len, has) = unsafe {
            (
                store.as_ref(),
                out(source)?,
                out(level)?,
                bytes_mut(buffer, capacity)?,
                out(len)?,
                out(has)?,
            )
        };
        let ring = store.map_or_else(log_ring::global, |store| store.poison.log.ring());
        let Some(record) = ring.drain(1).pop() else {
            *has = 0;
            return Ok(());
        };
        *has = 1;
        *source = record.source as u32;
        *level = record.level as u8;
        let message = record.message.as_bytes();
        let copied = message.len().min(buffer.len());
        buffer[..copied].copy_from_slice(&message[..copied]);
        *len = message.len();
        Ok(())
    })
}

/// The records the store's log sink, or with a null `store` the ring of records logged outside any store, dropped
/// because it was full, since the last call.
///
/// # Safety
/// `store` must be null or a live store.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_store_log_dropped(store: *const FdbStore) -> u64 {
    // SAFETY: the caller passes null or a live store.
    match unsafe { store.as_ref() } {
        Some(store) => store.poison.log.ring().take_dropped(),
        None => log_ring::global().take_dropped(),
    }
}

/// Copies the seam counters of the whole process, summed over its threads: how often each export was called and,
/// while timing is on, the nanoseconds spent in it, after the reads of the read-only bundles by where they were served
/// from. Copies up to `capacity` of each and returns how many counters there are; `fdb_seam_counter_name` names them.
///
/// # Safety
/// `counts` and `nanos` must be null or valid for writes of `capacity` values.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_seam_counters(counts: *mut u64, nanos: *mut u64, capacity: usize) -> usize {
    let (all_counts, all_nanos) = seam::totals();
    let copied = calls::COUNTER_COUNT.min(capacity);
    for (values, target) in [(all_counts, counts), (all_nanos, nanos)] {
        if !target.is_null() {
            // SAFETY: the caller guarantees room for `capacity` values, and at most that many are copied.
            unsafe { ptr::copy_nonoverlapping(values.as_ptr(), target, copied) };
        }
    }
    calls::COUNTER_COUNT
}

/// Points `name` at the ASCII name of seam counter `index`, which stays valid for the life of the process.
///
/// # Safety
/// `name` and `name_len` must be valid for writes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_seam_counter_name(index: u32, name: *mut *const u8, name_len: *mut usize) -> i32 {
    ffi_guard(Export::Other, || {
        let counter = calls::counter_name(index as usize)
            .ok_or_else(|| FfiError::invalid_argument(format!("no seam counter {index}")))?;
        // SAFETY: the caller passes valid out-pointers.
        let (name, name_len) = unsafe { (out(name)?, out(name_len)?) };
        *name = counter.as_ptr();
        *name_len = counter.len();
        Ok(())
    })
}

/// Turns the timing of every guarded export on (`enabled` 1) or off, for the whole process; off by default, as it
/// costs two clock reads per call.
#[unsafe(no_mangle)]
pub extern "C" fn fdb_seam_timing(enabled: u8) {
    seam::set_timing(enabled != 0);
}

/// `IPersistence.Flush`.
///
/// # Safety
/// `store` must be a live store.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_store_flush(store: *const FdbStore) -> i32 {
    ffi_guard(Export::StoreFlush, || {
        // SAFETY: the caller passes a live store.
        let store = unsafe { store_ref(store)? };
        store.poison.guard(|| Ok(store.persistence.flush()?))
    })
}

/// `IPersistence.Clear`.
///
/// # Safety
/// `store` must be a live store.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_store_clear(store: *const FdbStore) -> i32 {
    ffi_guard(Export::StoreClear, || {
        // SAFETY: the caller passes a live store.
        let store = unsafe { store_ref(store)? };
        store.poison.guard(|| Ok(store.persistence.clear()?))
    })
}

/// `IPersistence.CreateReader(flags)`: opens a reader and stores the state it reads in `current_state`.
/// `detailed_metrics` is `Db.Metrics.DetailedMetricsEnabled`, which decides whether the reader counts carry-forward
/// hits and misses.
///
/// # Safety
/// `store` must be a live store and the out-pointers valid for writes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_reader_open(
    store: *const FdbStore,
    flags: u32,
    detailed_metrics: u8,
    reader: *mut *mut FdbReader,
    current_state: *mut FfiStateId,
) -> i32 {
    ffi_guard(Export::ReaderOpen, || {
        // SAFETY: the caller passes a live store and valid out-pointers.
        let (store, reader, current_state) = unsafe { (store_ref(store)?, out(reader)?, out(current_state)?) };
        if let Some(metrics) = &store.carry_forward_metrics {
            metrics
                .detailed_metrics_enabled
                .store(detailed_metrics != 0, Ordering::Relaxed);
        }

        let opened = store
            .poison
            .guard(|| Ok(store.persistence.create_reader(ReaderFlags::from_bits(flags))?))?;
        *current_state = opened.current_state().into();
        *reader = Box::into_raw(Box::new(FdbReader {
            reader: opened,
            poison: store.poison.clone(),
        }));
        Ok(())
    })
}

/// Disposes a reader.
///
/// # Safety
/// `reader` must be null or a reader from [`fdb_reader_open`] that is not used afterwards.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_reader_release(reader: *mut FdbReader) -> i32 {
    ffi_guard(Export::ReaderRelease, || {
        if !reader.is_null() {
            // SAFETY: the caller hands back ownership of a reader from `fdb_reader_open`.
            drop(unsafe { Box::from_raw(reader) });
        }
        Ok(())
    })
}

/// `IPersistenceReader.CurrentState`.
///
/// # Safety
/// `reader` must be a live reader and `state` valid for writes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_reader_current_state(reader: *const FdbReader, state: *mut FfiStateId) -> i32 {
    ffi_guard(Export::ReaderCurrentState, || {
        // SAFETY: the caller passes a live reader and a valid out-pointer.
        let (reader, state) = unsafe { (reader_ref(reader)?, out(state)?) };
        *state = reader.current_state().into();
        Ok(())
    })
}

/// `IPersistenceReader.IsPreimageMode`.
///
/// # Safety
/// `reader` must be a live reader and `preimage` valid for writes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_reader_is_preimage_mode(reader: *const FdbReader, preimage: *mut u8) -> i32 {
    ffi_guard(Export::ReaderIsPreimageMode, || {
        // SAFETY: the caller passes a live reader and a valid out-pointer.
        let (reader, preimage) = unsafe { (reader_ref(reader)?, out(preimage)?) };
        *preimage = reader.is_preimage_mode().into();
        Ok(())
    })
}

/// `IPersistenceReader.GetAccount`: stores whether the account exists in `found` and, if it does, the account in
/// `account`.
///
/// # Safety
/// `reader` must be a live reader, `address` valid for 20 bytes and the out-pointers valid for writes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_reader_get_account(
    reader: *const FdbReader,
    address: *const u8,
    account: *mut FfiAccount,
    found: *mut u8,
) -> i32 {
    ffi_guard(Export::ReaderGetAccount, || {
        // SAFETY: the caller passes a live reader and valid pointers.
        let (reader, address, account, found) =
            unsafe { (reader_ref(reader)?, array::<20>(address)?, out(account)?, out(found)?) };
        let read = reader.poison.guard(|| Ok(reader.get_account(address)?))?;
        *found = read.is_some().into();
        if let Some(read) = read {
            *account = FfiAccount::from(&read);
        }
        Ok(())
    })
}

/// `IPersistenceReader.TryGetSlot`: stores whether the slot exists in `found` and, if it does, its value in `value`.
/// Slot and value are `UInt256` limbs.
///
/// # Safety
/// `reader` must be a live reader, `address` valid for 20 bytes, `slot` for four limbs, `value` for writes of four
/// limbs and `found` for writes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_reader_get_slot(
    reader: *const FdbReader,
    address: *const u8,
    slot: *const u64,
    value: *mut u64,
    found: *mut u8,
) -> i32 {
    ffi_guard(Export::ReaderGetSlot, || {
        // SAFETY: the caller passes a live reader and valid pointers.
        let (reader, address, slot, found) = unsafe {
            (
                reader_ref(reader)?,
                array::<20>(address)?,
                read_u256(slot)?,
                out(found)?,
            )
        };
        let read = reader.poison.guard(|| Ok(reader.try_get_slot(address, &slot)?))?;
        // SAFETY: as above.
        unsafe { write_found_u256(read, value, found) }
    })
}

/// `IPersistenceReader.TryLoadStateRlp`: copies the node into `buffer` and stores its length in `len`, or -1 when
/// there is none. A node longer than `capacity` yields [`FdbStatus::BufferTooSmall`] with its length in `len`.
///
/// # Safety
/// `reader` must be a live reader, `path` valid for 32 bytes, `buffer` for writes of `capacity` bytes and `len` for
/// writes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_reader_load_state_rlp(
    reader: *const FdbReader,
    path: *const u8,
    path_len: u8,
    read_flags: u32,
    buffer: *mut u8,
    capacity: usize,
    len: *mut i64,
) -> i32 {
    ffi_guard(Export::ReaderLoadStateRlp, || {
        // SAFETY: the caller passes a live reader and valid pointers.
        let (reader, path, buffer, len) = unsafe {
            (
                reader_ref(reader)?,
                tree_path(path, path_len)?,
                bytes_mut(buffer, capacity)?,
                out(len)?,
            )
        };
        let flags = ReadFlags::from_bits(read_flags);
        let read = reader
            .poison
            .guard(|| Ok(reader.load_state_rlp(&path, flags, buffer)?))?;
        report_len(read, capacity, len)
    })
}

/// `IPersistenceReader.TryLoadStorageRlp`, reporting as [`fdb_reader_load_state_rlp`].
///
/// # Safety
/// As [`fdb_reader_load_state_rlp`], and `address_hash` valid for 32 bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_reader_load_storage_rlp(
    reader: *const FdbReader,
    address_hash: *const u8,
    path: *const u8,
    path_len: u8,
    read_flags: u32,
    buffer: *mut u8,
    capacity: usize,
    len: *mut i64,
) -> i32 {
    ffi_guard(Export::ReaderLoadStorageRlp, || {
        // SAFETY: the caller passes a live reader and valid pointers.
        let (reader, address_hash, path, buffer, len) = unsafe {
            (
                reader_ref(reader)?,
                array::<32>(address_hash)?,
                tree_path(path, path_len)?,
                bytes_mut(buffer, capacity)?,
                out(len)?,
            )
        };
        let flags = ReadFlags::from_bits(read_flags);
        let read = reader
            .poison
            .guard(|| Ok(reader.load_storage_rlp(address_hash, &path, flags, buffer)?))?;
        report_len(read, capacity, len)
    })
}

/// `IPersistenceReader.GetAccountRaw`, reporting as [`fdb_reader_load_state_rlp`].
///
/// # Safety
/// `reader` must be a live reader, `address_hash` valid for 32 bytes, `buffer` for writes of `capacity` bytes and
/// `len` for writes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_reader_get_account_raw(
    reader: *const FdbReader,
    address_hash: *const u8,
    buffer: *mut u8,
    capacity: usize,
    len: *mut i64,
) -> i32 {
    ffi_guard(Export::ReaderGetAccountRaw, || {
        // SAFETY: the caller passes a live reader and valid pointers.
        let (reader, address_hash, buffer, len) = unsafe {
            (
                reader_ref(reader)?,
                array::<32>(address_hash)?,
                bytes_mut(buffer, capacity)?,
                out(len)?,
            )
        };
        let read = reader
            .poison
            .guard(|| Ok(reader.get_account_raw_into(address_hash, buffer)?))?;
        report_len(read, capacity, len)
    })
}

/// `IPersistenceReader.TryGetStorageRaw`, reporting as [`fdb_reader_get_slot`].
///
/// # Safety
/// `reader` must be a live reader, `address_hash` and `slot_hash` valid for 32 bytes, `value` for writes of four
/// limbs and `found` for writes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_reader_get_storage_raw(
    reader: *const FdbReader,
    address_hash: *const u8,
    slot_hash: *const u8,
    value: *mut u64,
    found: *mut u8,
) -> i32 {
    ffi_guard(Export::ReaderGetStorageRaw, || {
        // SAFETY: the caller passes a live reader and valid pointers.
        let (reader, address_hash, slot_hash, found) = unsafe {
            (
                reader_ref(reader)?,
                array::<32>(address_hash)?,
                array::<32>(slot_hash)?,
                out(found)?,
            )
        };
        let read = reader
            .poison
            .guard(|| Ok(reader.try_get_storage_raw(address_hash, slot_hash)?))?;
        // SAFETY: as above.
        unsafe { write_found_u256(read, value, found) }
    })
}

/// `IPersistenceReader.CreateAccountIterator`.
///
/// # Safety
/// `reader` must be a live reader, `start_key` and `end_key` valid for 32 bytes and `iter` for writes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_reader_account_iter(
    reader: *const FdbReader,
    start_key: *const u8,
    end_key: *const u8,
    iter: *mut *mut FdbIter,
) -> i32 {
    ffi_guard(Export::ReaderAccountIter, || {
        // SAFETY: the caller passes a live reader and valid pointers.
        let (reader, start_key, end_key, iter) = unsafe {
            (
                reader_ref(reader)?,
                array::<32>(start_key)?,
                array::<32>(end_key)?,
                out(iter)?,
            )
        };
        let opened = reader
            .poison
            .guard(|| Ok(reader.reader.create_account_iterator(start_key, end_key)?))?;
        *iter = Box::into_raw(Box::new(FdbIter {
            iter: opened,
            poison: reader.poison.clone(),
        }));
        Ok(())
    })
}

/// `IPersistenceReader.CreateStorageIterator`.
///
/// # Safety
/// `reader` must be a live reader, the keys valid for 32 bytes and `iter` for writes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_reader_storage_iter(
    reader: *const FdbReader,
    account_key: *const u8,
    start_slot_key: *const u8,
    end_slot_key: *const u8,
    iter: *mut *mut FdbIter,
) -> i32 {
    ffi_guard(Export::ReaderStorageIter, || {
        // SAFETY: the caller passes a live reader and valid pointers.
        let (reader, account_key, start_slot_key, end_slot_key, iter) = unsafe {
            (
                reader_ref(reader)?,
                array::<32>(account_key)?,
                array::<32>(start_slot_key)?,
                array::<32>(end_slot_key)?,
                out(iter)?,
            )
        };
        let opened = reader.poison.guard(|| {
            Ok(reader
                .reader
                .create_storage_iterator(account_key, start_slot_key, end_slot_key)?)
        })?;
        *iter = Box::into_raw(Box::new(FdbIter {
            iter: opened,
            poison: reader.poison.clone(),
        }));
        Ok(())
    })
}

/// `IFlatIterator.MoveNext`: stores whether there is a next entry in `has_entry` and, if there is, its key in `key`
/// and its value in `value` and `value_len`. The value stays valid until the next call on the iterator.
///
/// # Safety
/// `iter` must be a live iterator, used by one thread at a time; `key` must be valid for writes of 32 bytes and the
/// other out-pointers for writes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_iter_next(
    iter: *mut FdbIter,
    key: *mut u8,
    value: *mut *const u8,
    value_len: *mut usize,
    has_entry: *mut u8,
) -> i32 {
    ffi_guard(Export::IterNext, || {
        // SAFETY: the caller passes a live iterator and valid out-pointers.
        let (iter, key, value, value_len, has_entry) = unsafe {
            (
                iter.as_mut()
                    .ok_or_else(|| FfiError::invalid_argument("null iterator"))?,
                out(key.cast::<[u8; 32]>())?,
                out(value)?,
                out(value_len)?,
                out(has_entry)?,
            )
        };
        let moved = iter.poison.guard(|| Ok(iter.iter.move_next()?))?;
        *has_entry = moved.into();
        if moved {
            *key = iter.iter.current_key();
            let current = iter.iter.current_value();
            *value = current.as_ptr();
            *value_len = current.len();
        }
        Ok(())
    })
}

/// Disposes an iterator.
///
/// # Safety
/// `iter` must be null or an iterator from [`fdb_reader_account_iter`] or [`fdb_reader_storage_iter`] that is not
/// used afterwards.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_iter_release(iter: *mut FdbIter) -> i32 {
    ffi_guard(Export::IterRelease, || {
        if !iter.is_null() {
            // SAFETY: the caller hands back ownership of an iterator.
            drop(unsafe { Box::from_raw(iter) });
        }
        Ok(())
    })
}

/// `IPersistence.CreateWriteBatch(from, to, flags)`. On [`FdbStatus::WrongBaseState`], `current_state` holds the
/// persisted state the batch did not start at.
///
/// # Safety
/// `store` must be a live store, `from` and `to` valid and the out-pointers valid for writes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_batch_open(
    store: *const FdbStore,
    from: *const FfiStateId,
    to: *const FfiStateId,
    write_flags: u32,
    batch: *mut *mut FdbBatch,
    current_state: *mut FfiStateId,
) -> i32 {
    ffi_guard(Export::BatchOpen, || {
        // SAFETY: the caller passes a live store and valid pointers.
        let (store, from, to, batch, current_state) = unsafe {
            (
                store_ref(store)?,
                from.as_ref(),
                to.as_ref(),
                out(batch)?,
                out(current_state)?,
            )
        };
        let (Some(from), Some(to)) = (from, to) else {
            return Err(FfiError::invalid_argument("null state"));
        };

        let opened = store.poison.guard(|| {
            store
                .persistence
                .create_write_batch(&(*from).into(), &(*to).into(), WriteFlags::from_bits(write_flags))
                .map_err(|error| {
                    if let PersistenceError::WrongBaseState { current, .. } = &error {
                        *current_state = (*current).into();
                    }
                    FfiError::from(error)
                })
        })?;
        *batch = Box::into_raw(Box::new(FdbBatch {
            batch: Some(opened),
            poison: store.poison.clone(),
        }));
        Ok(())
    })
}

/// Applies `len` bytes of the op stream of `IWriteBatch` calls (see [`op_stream`]) to the batch, stopping at the
/// first operation that fails.
///
/// # Safety
/// `batch` must be a live batch, used by one thread at a time, and `ops` valid for `len` bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_batch_apply(batch: *mut FdbBatch, ops: *const u8, len: usize) -> i32 {
    ffi_guard(Export::BatchApply, || {
        // SAFETY: the caller passes a live batch and a valid op stream.
        let (batch, ops) = unsafe { (batch_mut(batch)?, bytes(ops, len)?) };
        let open = batch
            .batch
            .as_mut()
            .ok_or_else(|| FfiError::invalid_argument("the batch is committed"))?;
        batch.poison.guard(|| open.apply(ops))
    })
}

/// Commits the batch, as disposing `IWriteBatch` does. The batch is spent even when this fails; release it with
/// [`fdb_batch_release`].
///
/// # Safety
/// `batch` must be a live batch, used by one thread at a time.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_batch_commit(batch: *mut FdbBatch) -> i32 {
    ffi_guard(Export::BatchCommit, || {
        // SAFETY: the caller passes a live batch.
        let batch = unsafe { batch_mut(batch)? };
        let spent = batch
            .batch
            .take()
            .ok_or_else(|| FfiError::invalid_argument("the batch is committed"))?;
        batch.poison.guard(|| Ok(spent.commit()?))
    })
}

/// Releases a batch; one never committed is discarded, except for what a write-ahead-log-less batch wrote out
/// already.
///
/// # Safety
/// `batch` must be null or a batch from [`fdb_batch_open`] that is not used afterwards.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_batch_release(batch: *mut FdbBatch) -> i32 {
    ffi_guard(Export::BatchRelease, || {
        if !batch.is_null() {
            // SAFETY: the caller hands back ownership of a batch from `fdb_batch_open`.
            drop(unsafe { Box::from_raw(batch) });
        }
        Ok(())
    })
}

/// Writes `value` under `key` in `column`, in a batch of its own with default write flags.
///
/// # Safety
/// `store` must be a live store and the slices valid for their lengths.
#[cfg(any(test, feature = "test-api"))]
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_debug_put(
    store: *const FdbStore,
    column: u32,
    key: *const u8,
    key_len: usize,
    value: *const u8,
    value_len: usize,
) -> i32 {
    ffi_guard(Export::Other, || {
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
#[cfg(any(test, feature = "test-api"))]
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
    ffi_guard(Export::Other, || {
        // SAFETY: the caller passes a live store, valid slices and a valid out-pointer.
        let (store, key, buffer, value_len) = unsafe {
            (
                store_ref(store)?,
                bytes(key, key_len)?,
                bytes_mut(buffer, capacity)?,
                out(value_len)?,
            )
        };
        let column = column_of(column)?;
        let read = with_kv!(store, kv => kv.get(column, key, buffer))?;
        report_len(read, capacity, value_len)
    })
}

/// Arms `fault` (a [`faults::Fault`]) on the store, its persistence and its manager with `message`, the UTF-8 message
/// of the RocksDB error or the panic, for the next pass through the fault's point to take.
///
/// # Safety
/// `store` must be a live store and `message` valid for `len` bytes.
#[cfg(any(test, feature = "test-api"))]
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fdb_test_inject_fault(
    store: *const FdbStore,
    fault: u32,
    message: *const u8,
    len: usize,
) -> i32 {
    ffi_guard(Export::Other, || {
        // SAFETY: the caller passes a live store and a valid message.
        let (store, message) = unsafe { (store_ref(store)?, bytes(message, len)?) };
        let fault = faults::Fault::from_u32(fault)
            .ok_or_else(|| FfiError::invalid_argument(format!("unknown fault {fault}")))?;
        store
            .poison
            .faults
            .arm(fault, String::from_utf8_lossy(message).into_owned());
        Ok(())
    })
}

/// Stores the length of a read in `len`, -1 when absent; a read that did not fit fails with
/// [`FdbStatus::BufferTooSmall`] and its length in `len`.
fn report_len(read: GetInto, capacity: usize, len: &mut i64) -> Result<(), FfiError> {
    match read {
        GetInto::NotFound => *len = -1,
        GetInto::Found(found) => *len = found as i64,
        GetInto::BufferTooSmall(needed) => {
            *len = needed as i64;
            return Err(FfiError {
                status: FdbStatus::BufferTooSmall,
                message: format!("the value has {needed} bytes, the buffer {capacity}"),
            });
        }
    }
    Ok(())
}

/// Stores whether `read` holds a value in `found` and, if it does, the value's limbs in `value`.
///
/// # Safety
/// `value` must be valid for writes of four limbs.
unsafe fn write_found_u256(read: Option<U256>, value: *mut u64, found: &mut u8) -> Result<(), FfiError> {
    if value.is_null() {
        return Err(FfiError::invalid_argument("null out-pointer"));
    }
    *found = read.is_some().into();
    if let Some(read) = read {
        // SAFETY: the caller guarantees four writable limbs; C# need not align them.
        unsafe { value.cast::<[u64; 4]>().write_unaligned(read.0) };
    }
    Ok(())
}

/// # Safety
/// `slot` must be valid for reads of four limbs.
unsafe fn read_u256(slot: *const u64) -> Result<U256, FfiError> {
    if slot.is_null() {
        return Err(FfiError::invalid_argument("null slot"));
    }
    // SAFETY: the caller guarantees four readable limbs; C# need not align them.
    Ok(U256(unsafe { slot.cast::<[u64; 4]>().read_unaligned() }))
}

/// # Safety
/// `path` must be valid for reads of 32 bytes.
unsafe fn tree_path(path: *const u8, length: u8) -> Result<TreePath, FfiError> {
    if length > TreePath::MAX_LENGTH {
        return Err(FfiError::invalid_argument(format!("path length {length}")));
    }
    // SAFETY: the caller guarantees 32 readable bytes.
    Ok(TreePath::new(*unsafe { array::<32>(path) }?, length))
}

/// # Safety
/// `data` must be null or valid for reads of `N` bytes.
unsafe fn array<'a, const N: usize>(data: *const u8) -> Result<&'a [u8; N], FfiError> {
    // SAFETY: the caller guarantees `N` readable bytes; a byte array needs no alignment.
    unsafe { data.cast::<[u8; N]>().as_ref() }.ok_or_else(|| FfiError::invalid_argument("null pointer"))
}

/// # Safety
/// `pointer` must be null or valid for writes.
pub(crate) unsafe fn out<'a, T>(pointer: *mut T) -> Result<&'a mut T, FfiError> {
    // SAFETY: the caller guarantees a writable pointer.
    unsafe { pointer.as_mut() }.ok_or_else(|| FfiError::invalid_argument("null out-pointer"))
}

/// # Safety
/// `reader` must be null or a live reader.
unsafe fn reader_ref<'a>(reader: *const FdbReader) -> Result<&'a FdbReader, FfiError> {
    // SAFETY: the caller passes null or a live reader.
    unsafe { reader.as_ref() }.ok_or_else(|| FfiError::invalid_argument("null reader"))
}

/// # Safety
/// `batch` must be null or a live batch no other thread uses.
unsafe fn batch_mut<'a>(batch: *mut FdbBatch) -> Result<&'a mut FdbBatch, FfiError> {
    // SAFETY: the caller passes null or a live batch.
    unsafe { batch.as_mut() }.ok_or_else(|| FfiError::invalid_argument("null batch"))
}

/// # Safety
/// `store` must be null or a live store.
pub(crate) unsafe fn store_ref<'a>(store: *const FdbStore) -> Result<&'a FdbStore, FfiError> {
    // SAFETY: the caller passes null or a live store.
    unsafe { store.as_ref() }.ok_or_else(|| FfiError::invalid_argument("null store"))
}

/// # Safety
/// `data` must be valid for reads of `len` bytes unless `len` is 0.
pub(crate) unsafe fn bytes<'a>(data: *const u8, len: usize) -> Result<&'a [u8], FfiError> {
    match (data.is_null(), len) {
        (_, 0) => Ok(&[]),
        (true, _) => Err(FfiError::invalid_argument("null pointer with a non-zero length")),
        // SAFETY: the caller guarantees `len` readable bytes.
        (false, _) => Ok(unsafe { slice::from_raw_parts(data, len) }),
    }
}

/// # Safety
/// `data` must be valid for writes of `len` bytes unless `len` is 0.
pub(crate) unsafe fn bytes_mut<'a>(data: *mut u8, len: usize) -> Result<&'a mut [u8], FfiError> {
    match (data.is_null(), len) {
        (_, 0) => Ok(&mut []),
        (true, _) => Err(FfiError::invalid_argument("null pointer with a non-zero length")),
        // SAFETY: the caller guarantees `len` writable bytes.
        (false, _) => Ok(unsafe { slice::from_raw_parts_mut(data, len) }),
    }
}

#[cfg(any(test, feature = "test-api"))]
fn column_of(index: u32) -> Result<Column, FfiError> {
    Column::from_index(index).ok_or_else(|| FfiError::invalid_argument(format!("unknown column {index}")))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::abi::{ACCOUNT_EMPTY_CODE_HASH, ACCOUNT_EMPTY_STORAGE_ROOT, FdbKvKind};
    use crate::op_stream::writer::OpWriter;
    use crate::store::AnyKv;
    use flatdb_core::kv::managed::ManagedKvVTable;
    use flatdb_core::kv::mem::MemKv;
    use flatdb_core::kv::{IterOptions, KvIterator, KvReader};
    use flatdb_core::log_ring::LogSource;
    use flatdb_core::persistence::layouts::KvPersistence;
    use flatdb_core::persistence::{Persistence, PersistenceWriteBatch};
    use flatdb_core::types::{Account, EMPTY_TREE_HASH, FlatLayout, Hash256, StateId};
    use log::Level;
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
        let (mut store, mut info) = (ptr::null_mut(), FdbStoreInfo::default());
        // SAFETY: every pointer is valid.
        let status = unsafe { fdb_store_create(&cfg, &desc, &mut store, &mut info) };
        if status == FdbStatus::Ok as i32 {
            // SAFETY: the store is live.
            let created = unsafe { &*store }.info;
            assert_eq!(
                (info.rlp_wrap_slots, info.preimage),
                (created.rlp_wrap_slots, created.preimage)
            );
        }
        (status, store)
    }

    fn mem_store(layout: FlatLayout) -> *mut FdbStore {
        let (status, store) = create(
            FdbStoreCfg {
                layout: layout as u32,
                ..CFG
            },
            desc(FdbKvKind::Mem),
        );
        assert_eq!(status, FdbStatus::Ok as i32, "{}", last_error());
        store
    }

    fn mem_of(store: *mut FdbStore) -> MemKv {
        // SAFETY: the store is live.
        let AnyKv::Mem(kv) = &unsafe { &*store }.kv else {
            unreachable!("a mem store")
        };
        kv.clone()
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
        write_buffer_floor: 0,
        carry_forward: 1,
        log_level: 2,
    };

    const A: [u8; 20] = [0xAA; 20];
    const B: [u8; 20] = [0xBB; 20];
    const HASH: Hash256 = [0x55; 32];
    const ZERO: Hash256 = [0; 32];
    const MAX: Hash256 = [0xFF; 32];

    fn account() -> Account {
        Account::with_hashes(
            7,
            U256([1, 2, 3, 4]),
            [0x11; 32],
            flatdb_core::types::KECCAK_OF_EMPTY_STRING,
        )
    }

    fn path(nibbles: &str) -> TreePath {
        TreePath::from_hex(nibbles)
    }

    /// Every entry of every column.
    fn dump(kv: &MemKv) -> Vec<(Column, Vec<u8>, Vec<u8>)> {
        let reader = kv.reader(false).unwrap();
        let mut entries = Vec::new();
        for column in Column::ALL {
            let mut iter = reader.iter(column, IterOptions::default()).unwrap();
            iter.seek_to_first();
            while iter.valid() {
                entries.push((column, iter.key().to_vec(), iter.value().to_vec()));
                iter.next();
            }
        }
        entries
    }

    /// The two batches of [`op_stream_reaches_every_batch_method`], as direct calls.
    fn write_directly(kv: &MemKv) {
        let persistence = KvPersistence::new(kv.clone(), FlatLayout::Flat, Some(0)).unwrap();
        let mut batch = persistence
            .create_write_batch(&StateId::PRE_GENESIS, &StateId::new(1, HASH), WriteFlags::NONE)
            .unwrap();
        batch.set_account(&A, Some(&account())).unwrap();
        batch.set_account(&B, Some(&Account::new(1, U256::ZERO))).unwrap();
        batch
            .set_storage(&A, &U256::from_u64(1), Some(&U256([0x0102, 0, 0, 0])))
            .unwrap();
        batch.set_storage(&B, &U256::from_u64(2), Some(&U256::MAX)).unwrap();
        batch.set_state_trie_node(&path(""), &[0xC0]).unwrap();
        batch.set_state_trie_node(&path("12345678"), &[0xC1, 0x80]).unwrap();
        batch
            .set_storage_trie_node(&HASH, &path("abc"), &[0xC2, 0x80, 0x80])
            .unwrap();
        batch
            .set_storage_raw_encoded(&HASH, &[0x01; 32], &[0x82, 0x01, 0x02])
            .unwrap();
        batch.set_account_raw(&HASH, &account()).unwrap();
        batch.commit().unwrap();

        let mut batch = persistence
            .create_write_batch(&StateId::new(1, HASH), &StateId::new(2, HASH), WriteFlags::DISABLE_WAL)
            .unwrap();
        batch.set_account(&B, None).unwrap();
        batch.set_storage(&A, &U256::from_u64(1), None).unwrap();
        batch.self_destruct(&B).unwrap();
        batch.delete_account_range(&HASH, &HASH).unwrap();
        batch.delete_storage_range(&HASH, &ZERO, &MAX).unwrap();
        batch
            .delete_state_trie_node_range(&path("1").to_lower_bound_path(), &MAX)
            .unwrap();
        batch.delete_storage_trie_node_range(&HASH, &ZERO, &MAX).unwrap();
        batch.commit().unwrap();
    }

    /// Opens a batch of `store`, applies `ops` and commits it.
    fn write_through_ffi(store: *mut FdbStore, from: StateId, to: StateId, flags: WriteFlags, ops: &[u8]) {
        let (mut batch, mut current) = (ptr::null_mut(), FfiStateId::default());
        // SAFETY: the store is live and every pointer valid; the batch is released once.
        unsafe {
            let status = fdb_batch_open(store, &from.into(), &to.into(), flags.bits(), &mut batch, &mut current);
            assert_eq!(status, FdbStatus::Ok as i32, "{}", last_error());
            // Applied in two chunks, as C# hands them over when its buffer fills.
            let split = op_boundary(ops, ops.len() / 2);
            for chunk in [&ops[..split], &ops[split..]] {
                assert_eq!(
                    fdb_batch_apply(batch, chunk.as_ptr(), chunk.len()),
                    FdbStatus::Ok as i32,
                    "{}",
                    last_error()
                );
            }
            assert_eq!(fdb_batch_commit(batch), FdbStatus::Ok as i32, "{}", last_error());
            assert_eq!(fdb_batch_commit(batch), FdbStatus::InvalidArgument as i32);
            assert_eq!(fdb_batch_release(batch), FdbStatus::Ok as i32);
        }
    }

    /// The start of the first operation at or after `at`, found by applying prefixes to a throwaway batch.
    fn op_boundary(ops: &[u8], at: usize) -> usize {
        let kv = MemKv::new();
        let persistence = KvPersistence::new(kv, FlatLayout::Flat, Some(0)).unwrap();
        (at..=ops.len())
            .find(|&end| {
                let mut batch = persistence
                    .create_write_batch(&StateId::SYNC, &StateId::SYNC, WriteFlags::NONE)
                    .unwrap();
                op_stream::apply(&mut batch, &ops[..end]).is_ok()
            })
            .unwrap()
    }

    #[test]
    fn op_stream_reaches_every_batch_method() {
        let expected = MemKv::new();
        write_directly(&expected);

        let store = mem_store(FlatLayout::Flat);
        let mut first = OpWriter::default();
        first
            .op(op_stream::SET_ACCOUNT)
            .address(&A)
            .account(&account())
            .op(op_stream::SET_ACCOUNT)
            .address(&B)
            .account(&Account::new(1, U256::ZERO))
            .op(op_stream::SET_STORAGE)
            .address(&A)
            .u256(&U256::from_u64(1))
            .u256(&U256([0x0102, 0, 0, 0]))
            .op(op_stream::SET_STORAGE)
            .address(&B)
            .u256(&U256::from_u64(2))
            .u256(&U256::MAX)
            .op(op_stream::SET_STATE_TRIE_NODE)
            .path(&path(""))
            .bytes(&[0xC0])
            .op(op_stream::SET_STATE_TRIE_NODE)
            .path(&path("12345678"))
            .bytes(&[0xC1, 0x80])
            .op(op_stream::SET_STORAGE_TRIE_NODE)
            .hash(&HASH)
            .path(&path("abc"))
            .bytes(&[0xC2, 0x80, 0x80])
            .op(op_stream::SET_STORAGE_RAW_ENCODED)
            .hash(&HASH)
            .hash(&[0x01; 32])
            .bytes(&[0x82, 0x01, 0x02])
            .op(op_stream::SET_ACCOUNT_RAW)
            .hash(&HASH)
            .account(&account());
        write_through_ffi(
            store,
            StateId::PRE_GENESIS,
            StateId::new(1, HASH),
            WriteFlags::NONE,
            &first.0,
        );

        let mut second = OpWriter::default();
        second
            .op(op_stream::REMOVE_ACCOUNT)
            .address(&B)
            .op(op_stream::REMOVE_STORAGE)
            .address(&A)
            .u256(&U256::from_u64(1))
            .op(op_stream::SELF_DESTRUCT)
            .address(&B)
            .op(op_stream::DELETE_ACCOUNT_RANGE)
            .hash(&HASH)
            .hash(&HASH)
            .op(op_stream::DELETE_STORAGE_RANGE)
            .hash(&HASH)
            .hash(&ZERO)
            .hash(&MAX)
            .op(op_stream::DELETE_STATE_TRIE_NODE_RANGE)
            .hash(&path("1").to_lower_bound_path())
            .hash(&MAX)
            .op(op_stream::DELETE_STORAGE_TRIE_NODE_RANGE)
            .hash(&HASH)
            .hash(&ZERO)
            .hash(&MAX);
        write_through_ffi(
            store,
            StateId::new(1, HASH),
            StateId::new(2, HASH),
            WriteFlags::DISABLE_WAL,
            &second.0,
        );

        let written = dump(&mem_of(store));
        assert!(written.iter().any(|(column, _, _)| *column == Column::StateTopNodes));
        assert_eq!(written, dump(&expected));
        // SAFETY: every handle was released.
        assert_eq!(unsafe { fdb_store_destroy(store) }, FdbStatus::Ok as i32);
    }

    #[rstest]
    #[case::truncated(vec![op_stream::SELF_DESTRUCT, 0xAA], FdbStatus::InvalidArgument, "truncated (operation 0 at byte 0)")]
    #[case::unknown_tag([vec![op_stream::SELF_DESTRUCT], vec![0xAA; 20], vec![13]].concat(), FdbStatus::InvalidArgument, "unknown operation tag 13 (operation 1 at byte 21)")]
    #[case::long_path([vec![op_stream::SET_STATE_TRIE_NODE], vec![0; 32], vec![65, 0, 0, 0, 0]].concat(), FdbStatus::InvalidArgument, "path length 65")]
    #[case::rejected_by_the_layout([vec![op_stream::SET_STORAGE_RAW_ENCODED], vec![0; 64], vec![1, 0, 0, 0, 0x80]].concat(), FdbStatus::NotSupported, "Encoded slot writes require RLP slot wrapping")]
    fn bad_operations_fail_the_apply(#[case] ops: Vec<u8>, #[case] status: FdbStatus, #[case] message: &str) {
        // Raw slots, so encoded slot writes are refused.
        let kv = MemKv::new();
        {
            let mut batch = kv.write_batch().unwrap();
            batch
                .put(
                    Column::Metadata,
                    &*flatdb_core::codec::metadata::SLOT_ENCODING_KEY,
                    &[0],
                    WriteFlags::NONE,
                )
                .unwrap();
            batch.commit().unwrap();
        }
        let persistence = KvPersistence::new(kv, FlatLayout::Flat, Some(0)).unwrap();
        let mut batch = persistence
            .create_write_batch(&StateId::SYNC, &StateId::SYNC, WriteFlags::NONE)
            .unwrap();
        let error = op_stream::apply(&mut batch, &ops).unwrap_err();
        assert_eq!(error.status, status);
        assert!(error.message.contains(message), "{}", error.message);
    }

    #[test]
    fn reader_exports_copy_what_the_persistence_holds() {
        let store = mem_store(FlatLayout::Flat);
        let node = [0xAB; 600];
        let mut ops = OpWriter::default();
        ops.op(op_stream::SET_ACCOUNT)
            .address(&A)
            .account(&account())
            .op(op_stream::SET_ACCOUNT)
            .address(&B)
            .account(&Account::new(1, U256::ZERO))
            .op(op_stream::SET_STORAGE)
            .address(&A)
            .u256(&U256::from_u64(1))
            .u256(&U256::ZERO)
            .op(op_stream::SET_STATE_TRIE_NODE)
            .path(&path("1234567"))
            .bytes(&node)
            .op(op_stream::SET_STORAGE_TRIE_NODE)
            .hash(&HASH)
            .path(&path(""))
            .bytes(&[0xC0])
            .op(op_stream::SET_STORAGE_RAW_ENCODED)
            .hash(&HASH)
            .hash(&[0x01; 32])
            .bytes(&[0x82, 0x01, 0x02])
            .op(op_stream::SET_ACCOUNT_RAW)
            .hash(&HASH)
            .account(&account());
        write_through_ffi(
            store,
            StateId::PRE_GENESIS,
            StateId::new(3, HASH),
            WriteFlags::NONE,
            &ops.0,
        );

        let (mut reader, mut current) = (ptr::null_mut(), FfiStateId::default());
        // SAFETY: the store is live, every pointer is valid and every handle is released once.
        unsafe {
            assert_eq!(
                fdb_reader_open(store, 0, 1, &mut reader, &mut current),
                FdbStatus::Ok as i32
            );
            assert_eq!(current, StateId::new(3, HASH).into());
            let mut preimage = 9u8;
            assert_eq!(fdb_reader_is_preimage_mode(reader, &mut preimage), FdbStatus::Ok as i32);
            assert_eq!(preimage, 0);

            let (mut read, mut found) = (FfiAccount::default(), 9u8);
            assert_eq!(
                fdb_reader_get_account(reader, A.as_ptr(), &mut read, &mut found),
                FdbStatus::Ok as i32
            );
            assert_eq!((found, Account::from(&read)), (1, account()));
            assert_eq!(read.flags, ACCOUNT_EMPTY_CODE_HASH);
            fdb_reader_get_account(reader, B.as_ptr(), &mut read, &mut found);
            assert_eq!(read.flags, ACCOUNT_EMPTY_CODE_HASH | ACCOUNT_EMPTY_STORAGE_ROOT);
            assert_eq!(read.storage_root, EMPTY_TREE_HASH);
            fdb_reader_get_account(reader, [0x01; 20].as_ptr(), &mut read, &mut found);
            assert_eq!(found, 0);

            let mut value = [9u64; 4];
            let slot = U256::from_u64(1).0;
            fdb_reader_get_slot(reader, A.as_ptr(), slot.as_ptr(), value.as_mut_ptr(), &mut found);
            assert_eq!((found, value), (1, [0; 4]), "a zero slot is present");
            fdb_reader_get_slot(reader, B.as_ptr(), slot.as_ptr(), value.as_mut_ptr(), &mut found);
            assert_eq!(found, 0);
            fdb_reader_get_storage_raw(
                reader,
                HASH.as_ptr(),
                [0x01; 32].as_ptr(),
                value.as_mut_ptr(),
                &mut found,
            );
            assert_eq!((found, value), (1, [0x0102, 0, 0, 0]));

            let mut buffer = [0u8; 512];
            let mut len = 0i64;
            let node_path = path("1234567");
            let status = fdb_reader_load_state_rlp(
                reader,
                node_path.path.as_ptr(),
                node_path.length,
                0,
                buffer.as_mut_ptr(),
                buffer.len(),
                &mut len,
            );
            assert_eq!((status, len), (FdbStatus::BufferTooSmall as i32, 600));
            let mut large = vec![0u8; 600];
            let status = fdb_reader_load_state_rlp(
                reader,
                node_path.path.as_ptr(),
                node_path.length,
                ReadFlags::HINT_CACHE_MISS.bits(),
                large.as_mut_ptr(),
                large.len(),
                &mut len,
            );
            assert_eq!(
                (status, len, large.as_slice()),
                (FdbStatus::Ok as i32, 600, node.as_slice())
            );
            let status = fdb_reader_load_state_rlp(reader, ZERO.as_ptr(), 3, 0, buffer.as_mut_ptr(), 512, &mut len);
            assert_eq!((status, len), (FdbStatus::Ok as i32, -1));
            let status = fdb_reader_load_storage_rlp(
                reader,
                HASH.as_ptr(),
                ZERO.as_ptr(),
                0,
                0,
                buffer.as_mut_ptr(),
                512,
                &mut len,
            );
            assert_eq!((status, &buffer[..len as usize]), (FdbStatus::Ok as i32, &[0xC0][..]));
            let status = fdb_reader_get_account_raw(reader, HASH.as_ptr(), buffer.as_mut_ptr(), 512, &mut len);
            assert_eq!(status, FdbStatus::Ok as i32);
            assert_eq!(
                &buffer[..len as usize],
                &flatdb_core::codec::account::encode_account(&account(), true)[..]
            );
            assert_eq!(
                fdb_reader_load_state_rlp(reader, ZERO.as_ptr(), 65, 0, buffer.as_mut_ptr(), 512, &mut len),
                FdbStatus::InvalidArgument as i32
            );

            let mut iter = ptr::null_mut();
            assert_eq!(
                fdb_reader_storage_iter(reader, HASH.as_ptr(), ZERO.as_ptr(), MAX.as_ptr(), &mut iter),
                FdbStatus::Ok as i32
            );
            let (mut key, mut entry, mut entry_len, mut has) = ([0u8; 32], ptr::null(), 0usize, 0u8);
            assert_eq!(
                fdb_iter_next(iter, key.as_mut_ptr(), &mut entry, &mut entry_len, &mut has),
                FdbStatus::Ok as i32
            );
            assert_eq!((has, key), (1, [0x01; 32]));
            assert_eq!(
                slice::from_raw_parts(entry, entry_len),
                &[0x01, 0x02],
                "the stripped value"
            );
            fdb_iter_next(iter, key.as_mut_ptr(), &mut entry, &mut entry_len, &mut has);
            assert_eq!(has, 0);
            // The iterator holds its own snapshot.
            assert_eq!(fdb_reader_release(reader), FdbStatus::Ok as i32);
            assert_eq!(fdb_iter_release(iter), FdbStatus::Ok as i32);

            let mut stats = FdbStats::default();
            assert_eq!(fdb_store_take_stats(store, &mut stats), FdbStatus::Ok as i32);
            assert_eq!(stats.reads, 7, "flat reads count, trie reads do not");
            assert_eq!(
                (stats.carry_forward_account_misses, stats.carry_forward_slot_misses),
                (3, 2)
            );
            assert_eq!(
                (stats.carry_forward_account_count, stats.carry_forward_slot_count),
                (3, 2)
            );
            fdb_store_take_stats(store, &mut stats);
            assert_eq!(
                (
                    stats.reads,
                    stats.carry_forward_account_misses,
                    stats.carry_forward_account_count
                ),
                (0, 0, 3)
            );

            assert_eq!(fdb_store_destroy(store), FdbStatus::Ok as i32);
        }
    }

    #[test]
    fn store_outlives_its_open_handles() {
        let store = mem_store(FlatLayout::PreimageFlat);
        let (mut reader, mut iter, mut batch, mut current) =
            (ptr::null_mut(), ptr::null_mut(), ptr::null_mut(), FfiStateId::default());
        // SAFETY: the store is live, every pointer is valid and every handle is released once.
        unsafe {
            fdb_reader_open(store, ReaderFlags::SYNC.bits(), 0, &mut reader, &mut current);
            assert_eq!(current, StateId::PRE_GENESIS.into());
            fdb_reader_account_iter(reader, ZERO.as_ptr(), MAX.as_ptr(), &mut iter);
            let sync = StateId::SYNC.into();
            fdb_batch_open(store, &sync, &sync, 0, &mut batch, &mut current);

            assert_eq!(fdb_store_destroy(store), FdbStatus::InvalidArgument as i32);
            assert!(last_error().contains("still has open"));
            fdb_reader_release(reader);
            assert_eq!(
                fdb_store_destroy(store),
                FdbStatus::InvalidArgument as i32,
                "the iterator is open"
            );
            fdb_iter_release(iter);
            assert_eq!(
                fdb_store_destroy(store),
                FdbStatus::InvalidArgument as i32,
                "the batch is open"
            );
            fdb_batch_release(batch);
            assert_eq!(fdb_store_destroy(store), FdbStatus::Ok as i32);
        }
    }

    /// A panic poisons the store, and its message goes to the store's log sink, not to stderr.
    #[test]
    fn a_panic_poisons_the_store_and_its_handles_until_they_are_released() {
        let (status, store) = create(FdbStoreCfg { log_level: 4, ..CFG }, desc(FdbKvKind::Mem));
        assert_eq!(status, FdbStatus::Ok as i32);
        let (mut reader, mut other, mut batch, mut current) =
            (ptr::null_mut(), ptr::null_mut(), ptr::null_mut(), FfiStateId::default());
        let (mut account, mut found) = (FfiAccount::default(), 0u8);
        let sync = StateId::SYNC.into();
        // SAFETY: the store is live, every pointer is valid and every handle is released once.
        unsafe {
            fdb_reader_open(store, 0, 0, &mut reader, &mut current);
            fdb_batch_open(store, &sync, &sync, 0, &mut batch, &mut current);
            let panicked = ffi_guard(Export::Other, || (*store).poison.guard(|| panic!("injected")));
            let logged = (&*store).poison.log.ring().drain(usize::MAX);

            let statuses = [
                fdb_reader_open(store, 0, 0, &mut other, &mut current),
                fdb_reader_get_account(reader, A.as_ptr(), &mut account, &mut found),
                fdb_batch_apply(batch, ptr::null(), 0),
                fdb_batch_commit(batch),
                fdb_store_flush(store),
            ];
            let released = [
                fdb_reader_release(reader),
                fdb_batch_release(batch),
                fdb_store_destroy(store),
            ];

            assert_eq!(panicked, FdbStatus::Panic as i32);
            assert!(
                logged.iter().any(|record| record.level == Level::Debug
                    && record.source == LogSource::Other
                    && record.message.starts_with("thread '")
                    && record.message.contains(" panicked at ")
                    && record.message.ends_with("injected")),
                "{logged:?}"
            );
            assert_eq!(statuses, [FdbStatus::Poisoned as i32; 5]);
            assert_eq!(released, [FdbStatus::Ok as i32; 3]);
        }
    }

    #[test]
    fn batches_off_the_persisted_state_report_it() {
        let store = mem_store(FlatLayout::FlatInTrie);
        write_through_ffi(
            store,
            StateId::PRE_GENESIS,
            StateId::new(5, HASH),
            WriteFlags::NONE,
            &[],
        );
        let (mut batch, mut current) = (ptr::null_mut(), FfiStateId::default());
        // SAFETY: the store is live and every pointer is valid.
        unsafe {
            let status = fdb_batch_open(
                store,
                &StateId::new(4, HASH).into(),
                &StateId::new(6, HASH).into(),
                0,
                &mut batch,
                &mut current,
            );
            assert_eq!(status, FdbStatus::WrongBaseState as i32);
            assert_eq!(current, StateId::new(5, HASH).into());
            assert!(batch.is_null());
            assert_eq!(fdb_store_destroy(store), FdbStatus::Ok as i32);
        }
    }

    #[test]
    fn struct_sizes_and_symbols_are_reported() {
        let mut sizes = [0u32; 16];
        // SAFETY: the buffer holds 16 values.
        let count = unsafe { fdb_struct_sizes(sizes.as_mut_ptr(), sizes.len() as u32) };
        assert_eq!(&sizes[..count as usize], &STRUCT_SIZES);
        let mut offsets = [0u32; 128];
        // SAFETY: the buffer holds 128 values.
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

    /// Every guarded export counts its calls, under its own name after the bundle reads, and its time while timing is
    /// on; the counters are summed over every thread.
    #[test]
    fn exports_count_their_calls_and_time() {
        let store = mem_store(FlatLayout::Flat);
        let (mut reader, mut current, mut account, mut found) =
            (ptr::null_mut(), FfiStateId::default(), FfiAccount::default(), 0u8);
        let get_account = Export::ReaderGetAccount.counter();
        let before = seam::this_thread();
        let (_, nanos_before) = seam::totals();
        // SAFETY: the store is live, every pointer is valid and every handle is released once.
        unsafe {
            fdb_reader_open(store, 0, 0, &mut reader, &mut current);
            fdb_seam_timing(1);
            for _ in 0..3 {
                fdb_reader_get_account(reader, A.as_ptr(), &mut account, &mut found);
            }
            fdb_seam_timing(0);
            fdb_reader_get_account(reader, ptr::null(), &mut account, &mut found);
            fdb_reader_release(reader);
            fdb_store_destroy(store);
        }
        let after = seam::this_thread();
        let (_, nanos_after) = seam::totals();

        let (mut counts, mut nanos) = ([0u64; 128], [0u64; 128]);
        // SAFETY: the buffers hold 128 values.
        let count = unsafe { fdb_seam_counters(counts.as_mut_ptr(), nanos.as_mut_ptr(), counts.len()) };
        let name = |index: u32| {
            let (mut name, mut name_len) = (ptr::null(), 0usize);
            // SAFETY: the out-pointers are valid; the export points at a static name of `name_len` bytes.
            unsafe {
                let status = fdb_seam_counter_name(index, &mut name, &mut name_len);
                (status == FdbStatus::Ok as i32).then(|| str::from_utf8(slice::from_raw_parts(name, name_len)).unwrap())
            }
        };

        assert_eq!(after[get_account] - before[get_account], 4, "a failed call counts too");
        assert_eq!(
            after[Export::ReaderOpen.counter()] - before[Export::ReaderOpen.counter()],
            1
        );
        assert!(
            nanos_after[get_account] > nanos_before[get_account],
            "the timed calls took time"
        );
        assert_eq!(count, calls::COUNTER_COUNT);
        assert!(counts[get_account] >= 4);
        assert_eq!(
            [
                name(0),
                name(get_account as u32),
                name(count as u32 - 1),
                name(count as u32)
            ],
            [
                Some("bundle_account_snapshot"),
                Some("reader_get_account"),
                Some("other"),
                None
            ]
        );
    }

    #[test]
    fn mem_store_round_trips_and_counts_reads() {
        let store = mem_store(FlatLayout::Flat);

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
            let mut stats = FdbStats::default();
            // SAFETY: the store is live and the out-pointer valid.
            assert_eq!(unsafe { fdb_store_take_stats(store, &mut stats) }, FdbStatus::Ok as i32);
            stats.reads
        };
        assert_eq!(take_read_count(), 4, "every get of a valid column reads the database");
        assert_eq!(take_read_count(), 0);
        // SAFETY: no handle is open.
        assert_eq!(unsafe { fdb_store_destroy(store) }, FdbStatus::Ok as i32);
    }

    #[rstest]
    #[case(FdbStoreCfg { abi_version: ABI_VERSION + 1, ..CFG }, desc(FdbKvKind::Mem), FdbStatus::InvalidArgument, "ABI version")]
    #[case(FdbStoreCfg { layout: 4, ..CFG }, desc(FdbKvKind::Mem), FdbStatus::InvalidArgument, "layout")]
    #[case(FdbStoreCfg { log_level: 6, ..CFG }, desc(FdbKvKind::Mem), FdbStatus::InvalidArgument, "log level")]
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
    fn managed_backend_needs_every_callback() {
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
            set_write_buffer: None,
        };

        let (status, store) = create(
            CFG,
            FdbKvDesc {
                managed: &vtable,
                ..desc(FdbKvKind::Managed)
            },
        );
        assert_eq!(status, FdbStatus::InvalidArgument as i32);
        assert!(store.is_null());
        assert!(last_error().contains("lack get"), "{}", last_error());
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
