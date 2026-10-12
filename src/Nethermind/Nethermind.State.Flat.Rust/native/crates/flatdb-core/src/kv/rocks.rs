//! RocksDB backend over the C API of the instance the C# side opened.
//!
//! Port of the option handling and read/write paths of `Nethermind.Db.Rocks` (`DbOnTheRocks.cs`, `ColumnsDb.cs`,
//! `ColumnDb.cs`, `RocksDbReader.cs`), calling the same native entry points as the C# bindings:
//! - reads into a caller buffer use `rocksdb_get_into_buffer_cf`, as `IReadOnlyKeyValueStore.Get(key, Span)` does;
//! - zero-copy reads use `rocksdb_get_pinned_cf`, as the `byte[] Get` path does.

use std::ffi::{CStr, CString, c_char, c_void};
use std::ptr;
use std::slice;
use std::sync::Arc;

use super::rocks_api::{
    RawColumnFamily, RawDb, RawFlushOptions, RawIterator, RawPinnableSlice, RawReadOptions, RawSnapshot, RawWriteBatch,
    RawWriteOptions, RocksApi, RocksFns,
};
use super::{
    Column, GetInto, IterOptions, KvError, KvIterator, KvRawBatch, KvReader, KvResult, KvStore, ReadFlags, WriteFlags,
    crosses_prefix_bucket,
};

/// Handles and settings of an open RocksDB instance, as `DbOnTheRocks` and `ColumnsDb<FlatDbColumns>` hold them.
#[derive(Clone, Copy, Debug)]
pub struct RocksDesc {
    /// `rocksdb_t*`.
    pub db: *mut c_void,
    /// `rocksdb_column_family_handle_t*` of every column, in [`Column`] order.
    pub column_families: [*mut c_void; Column::COUNT],
    /// `DbOnTheRocks.VerifyChecksum`.
    pub verify_checksums: bool,
    /// `IRocksDbConfig.WriteAheadLogSync`, applied to every write option.
    pub wal_sync: bool,
    /// `DbOnTheRocks.ReadAheadSize`, used by iterators asked to read ahead.
    pub readahead_size: u64,
    /// `DbOnTheRocks.PrefixExtractorLength`: 0 for none, `i32::MAX` for an extractor of unknown width.
    pub prefix_extractor_length: u32,
}

/// The RocksDB store. Cloning shares the instance.
#[derive(Clone)]
pub struct RocksKv {
    inner: Arc<Inner>,
}

struct Inner {
    api: RocksFns,
    db: *mut RawDb,
    column_families: [*mut RawColumnFamily; Column::COUNT],
    verify_checksums: bool,
    readahead_size: u64,
    prefix_extractor_length: u32,
    /// Indexed by the low two [`WriteFlags`] bits, matching `DbOnTheRocks.WriteFlagsToWriteOptions`.
    write_options: [*mut RawWriteOptions; 4],
    read_options: *mut RawReadOptions,
    read_options_no_fill: *mut RawReadOptions,
    flush_options: *mut RawFlushOptions,
}

// SAFETY: a RocksDB database and its column family handles are thread-safe. The option objects are fully set up in
// `RocksKv::new` and only read afterwards, which RocksDB allows from any number of threads.
unsafe impl Send for Inner {}
// SAFETY: as above.
unsafe impl Sync for Inner {}

impl RocksKv {
    /// Wraps an open database.
    ///
    /// # Safety
    /// `api` must hold the C API of the library that opened `desc.db`, and `desc.db` and every column family handle
    /// must stay valid until the last clone of the store, and every reader, iterator and batch it created, is dropped.
    pub unsafe fn new(api: &RocksApi, desc: RocksDesc) -> KvResult<Self> {
        let api = api.validate()?;
        if desc.db.is_null() || desc.column_families.iter().any(|cf| cf.is_null()) {
            return Err(KvError::InvalidArgument(
                "null RocksDB database or column family handle".to_owned(),
            ));
        }

        // SAFETY: every call below only creates and configures option objects owned by `Inner`.
        let inner = unsafe {
            let create_write_options = |flags: WriteFlags| {
                let options = (api.writeoptions_create)();
                (api.writeoptions_set_sync)(options, desc.wal_sync.into());
                if flags.contains(WriteFlags::DISABLE_WAL) {
                    (api.writeoptions_disable_wal)(options, 1);
                }
                if flags.contains(WriteFlags::LOW_PRIORITY) {
                    (api.writeoptions_set_low_pri)(options, 1);
                }
                options
            };

            let flush_options = (api.flushoptions_create)();
            (api.flushoptions_set_wait)(flush_options, 1);

            Inner {
                api,
                db: desc.db.cast(),
                column_families: desc.column_families.map(|cf| cf.cast()),
                verify_checksums: desc.verify_checksums,
                readahead_size: desc.readahead_size,
                prefix_extractor_length: desc.prefix_extractor_length,
                write_options: [
                    create_write_options(WriteFlags::NONE),
                    create_write_options(WriteFlags::LOW_PRIORITY),
                    create_write_options(WriteFlags::DISABLE_WAL),
                    create_write_options(WriteFlags::LOW_PRIORITY_AND_NO_WAL),
                ],
                read_options: create_read_options(&api, desc.verify_checksums, ptr::null(), true),
                read_options_no_fill: create_read_options(&api, desc.verify_checksums, ptr::null(), false),
                flush_options,
            }
        };

        Ok(Self { inner: Arc::new(inner) })
    }

    /// Whether another clone, reader, iterator or batch still uses the database, which must then stay open.
    pub fn is_shared(&self) -> bool {
        Arc::strong_count(&self.inner) > 1
    }
}

impl Drop for Inner {
    fn drop(&mut self) {
        // SAFETY: the option objects were created in `RocksKv::new` and nothing references them any more.
        unsafe {
            for options in self.write_options {
                (self.api.writeoptions_destroy)(options);
            }
            (self.api.readoptions_destroy)(self.read_options);
            (self.api.readoptions_destroy)(self.read_options_no_fill);
            (self.api.flushoptions_destroy)(self.flush_options);
        }
    }
}

/// Creates read options as `DbOnTheRocks.CreateReadOptions` does, optionally bound to a snapshot.
///
/// # Safety
/// `snapshot` must be null or a live snapshot that outlives the returned options.
unsafe fn create_read_options(
    api: &RocksFns,
    verify_checksums: bool,
    snapshot: *const RawSnapshot,
    fill_cache: bool,
) -> *mut RawReadOptions {
    // SAFETY: the options are fresh, and the caller vouches for the snapshot.
    unsafe {
        let options = (api.readoptions_create)();
        (api.readoptions_set_verify_checksums)(options, verify_checksums.into());
        if !snapshot.is_null() {
            (api.readoptions_set_snapshot)(options, snapshot);
        }
        if !fill_cache {
            (api.readoptions_set_fill_cache)(options, 0);
        }
        options
    }
}

impl Inner {
    fn column_family(&self, column: Column) -> *mut RawColumnFamily {
        self.column_families[column.index()]
    }

    /// Turns an `errptr` RocksDB filled into an error, freeing the message.
    fn check(&self, error: *mut c_char) -> KvResult<()> {
        if error.is_null() {
            return Ok(());
        }

        // SAFETY: RocksDB sets `errptr` to a NUL-terminated string allocated with malloc, released by rocksdb_free.
        let message = unsafe { CStr::from_ptr(error) }.to_string_lossy().into_owned();
        // SAFETY: as above; the string is not used afterwards.
        unsafe { (self.api.free)(error.cast()) };
        Err(KvError::rocks(message))
    }

    fn get_into(
        &self,
        options: *const RawReadOptions,
        column: Column,
        key: &[u8],
        buffer: &mut [u8],
    ) -> KvResult<GetInto> {
        let mut value_len = 0usize;
        let mut found = 0u8;
        let mut error = ptr::null_mut();
        // SAFETY: the key and buffer slices are valid for the lengths passed, and the handles are live.
        let fits = unsafe {
            (self.api.get_into_buffer_cf)(
                self.db,
                options,
                self.column_family(column),
                key.as_ptr().cast(),
                key.len(),
                buffer.as_mut_ptr().cast(),
                buffer.len(),
                &mut value_len,
                &mut found,
                &mut error,
            )
        };
        self.check(error)?;

        Ok(match (found, fits) {
            (0, _) => GetInto::NotFound,
            (_, 0) => GetInto::BufferTooSmall(value_len),
            _ => GetInto::Found(value_len),
        })
    }

    /// Flushes the memtable of `column`, or of the default column for `None`.
    fn flush_memtable(&self, column: Option<Column>) -> KvResult<()> {
        let mut error = ptr::null_mut();
        // SAFETY: the database, flush options and column family handle are live.
        unsafe {
            match column {
                None => (self.api.flush)(self.db, self.flush_options, &mut error),
                Some(column) => {
                    (self.api.flush_cf)(self.db, self.flush_options, self.column_family(column), &mut error)
                }
            }
        };
        self.check(error)
    }

    fn get_pinned<R>(
        &self,
        options: *const RawReadOptions,
        column: Column,
        key: &[u8],
        read: impl FnOnce(&[u8]) -> R,
    ) -> KvResult<Option<R>> {
        let mut error = ptr::null_mut();
        // SAFETY: the key slice is valid for its length, and the handles are live.
        let slice = unsafe {
            (self.api.get_pinned_cf)(
                self.db,
                options,
                self.column_family(column),
                key.as_ptr().cast(),
                key.len(),
                &mut error,
            )
        };
        self.check(error)?;
        if slice.is_null() {
            return Ok(None);
        }

        let pinned = PinnedSlice { api: &self.api, slice };
        let mut value_len = 0usize;
        // SAFETY: `slice` is a live pinnable slice.
        let value = unsafe { (self.api.pinnableslice_value)(slice, &mut value_len) };
        if value.is_null() {
            return Ok(None);
        }

        // SAFETY: RocksDB keeps the value valid until the slice is destroyed, which `pinned` does after `read`.
        let result = read(unsafe { slice::from_raw_parts(value.cast::<u8>(), value_len) });
        drop(pinned);
        Ok(Some(result))
    }
}

/// Records the outcome of one step of [`RocksKv::flush`] and returns whether the flush must stop.
///
/// `ColumnsDb.Flush()` only logs a failed step and goes on, unless the error is fatal, after which it exits; so the
/// first error is kept, and a fatal one replaces it and stops the flush.
fn record_flush_step(first_error: &mut Option<KvError>, step: KvResult<()>) -> bool {
    match step {
        Ok(()) => false,
        Err(error) if error.is_fatal() => {
            *first_error = Some(error);
            true
        }
        Err(error) => {
            first_error.get_or_insert(error);
            false
        }
    }
}

/// Destroys a pinnable slice, even when the reader of its value panics.
struct PinnedSlice<'a> {
    api: &'a RocksFns,
    slice: *mut RawPinnableSlice,
}

impl Drop for PinnedSlice<'_> {
    fn drop(&mut self) {
        // SAFETY: the slice is live and destroyed exactly once.
        unsafe { (self.api.pinnableslice_destroy)(self.slice) };
    }
}

impl KvStore for RocksKv {
    type Reader = RocksReader;
    type RawBatch = RocksBatch;

    fn get(&self, column: Column, key: &[u8], buffer: &mut [u8]) -> KvResult<GetInto> {
        self.inner.get_into(self.inner.read_options, column, key, buffer)
    }

    fn get_with<R>(&self, column: Column, key: &[u8], read: impl FnOnce(&[u8]) -> R) -> KvResult<Option<R>> {
        self.inner.get_pinned(self.inner.read_options, column, key, read)
    }

    fn reader(&self) -> KvResult<RocksReader> {
        let inner = &self.inner;
        // SAFETY: the database is live; the snapshot is released by `SnapshotState::drop`, after the options bound
        // to it.
        let state = unsafe {
            let snapshot = (inner.api.create_snapshot)(inner.db);
            SnapshotState {
                inner: inner.clone(),
                snapshot,
                read_options: create_read_options(&inner.api, inner.verify_checksums, snapshot, true),
                read_options_no_fill: create_read_options(&inner.api, inner.verify_checksums, snapshot, false),
            }
        };

        Ok(RocksReader { state: Arc::new(state) })
    }

    fn raw_batch(&self) -> KvResult<RocksBatch> {
        // SAFETY: creates a batch owned by the returned value.
        let batch = unsafe { (self.inner.api.writebatch_create)() };
        Ok(RocksBatch {
            inner: self.inner.clone(),
            batch,
        })
    }

    fn flush_wal(&self, sync: bool) -> KvResult<()> {
        let mut error = ptr::null_mut();
        // SAFETY: the database is live.
        unsafe { (self.inner.api.flush_wal)(self.inner.db, sync.into(), &mut error) };
        self.inner.check(error)
    }

    /// Flushes as `ColumnsDb.Flush()` does: the write-ahead log and the default column together, then each column on
    /// its own. A failed step does not stop the later ones, as C# only logs it, unless the error is fatal, after
    /// which C# exits. Returns the first error.
    fn flush(&self) -> KvResult<()> {
        let mut first_error = None;
        let default_flushed = self.flush_wal(true).and_then(|()| self.inner.flush_memtable(None));
        if !record_flush_step(&mut first_error, default_flushed) {
            for column in Column::ALL {
                if record_flush_step(&mut first_error, self.inner.flush_memtable(Some(column))) {
                    break;
                }
            }
        }

        first_error.map_or(Ok(()), Err)
    }

    fn set_write_buffer(&self, column: Column, size_bytes: u64) -> KvResult<()> {
        let names = [c"write_buffer_size", c"max_bytes_for_level_base"];
        let values = [size_bytes.to_string(), size_bytes.saturating_mul(4).to_string()]
            .map(|value| CString::new(value).expect("a number has no NUL byte"));
        let name_ptrs = names.map(|name| name.as_ptr());
        let value_ptrs = [values[0].as_ptr(), values[1].as_ptr()];

        let mut error = ptr::null_mut();
        // SAFETY: both arrays hold `names.len()` NUL-terminated strings that outlive the call.
        unsafe {
            (self.inner.api.set_options_cf)(
                self.inner.db,
                self.inner.column_family(column),
                names.len() as i32,
                name_ptrs.as_ptr(),
                value_ptrs.as_ptr(),
                &mut error,
            )
        };
        self.inner.check(error)
    }

    fn property(&self, column: Column, name: &str) -> KvResult<Option<String>> {
        let name =
            CString::new(name).map_err(|_| KvError::InvalidArgument("property name has a NUL byte".to_owned()))?;
        // SAFETY: the database and column family are live, and the name is NUL-terminated.
        let value = unsafe {
            (self.inner.api.property_value_cf)(self.inner.db, self.inner.column_family(column), name.as_ptr())
        };
        if value.is_null() {
            return Ok(None);
        }

        // SAFETY: RocksDB returns a NUL-terminated string allocated with malloc, released by rocksdb_free.
        let property = unsafe { CStr::from_ptr(value) }.to_string_lossy().into_owned();
        // SAFETY: as above; the string is not used afterwards.
        unsafe { (self.inner.api.free)(value.cast()) };
        Ok(Some(property))
    }
}

/// A database snapshot with the two read options `ColumnsDb.ColumnDbSnapshot` shares across its column readers.
struct SnapshotState {
    inner: Arc<Inner>,
    snapshot: *const RawSnapshot,
    read_options: *mut RawReadOptions,
    read_options_no_fill: *mut RawReadOptions,
}

// SAFETY: a snapshot and read options are immutable once set up and may be used from any thread.
unsafe impl Send for SnapshotState {}
// SAFETY: as above.
unsafe impl Sync for SnapshotState {}

impl SnapshotState {
    fn read_options(&self, flags: ReadFlags) -> *const RawReadOptions {
        if flags.contains(ReadFlags::HINT_CACHE_MISS) {
            self.read_options_no_fill
        } else {
            self.read_options
        }
    }
}

impl Drop for SnapshotState {
    fn drop(&mut self) {
        let api = &self.inner.api;
        // SAFETY: every iterator holds this state alive, so nothing uses the options or the snapshot any more.
        unsafe {
            (api.readoptions_destroy)(self.read_options);
            (api.readoptions_destroy)(self.read_options_no_fill);
            (api.release_snapshot)(self.inner.db, self.snapshot);
        }
    }
}

/// Reads at a database snapshot, as `ColumnsDb.ColumnDbSnapshot` does.
pub struct RocksReader {
    state: Arc<SnapshotState>,
}

impl KvReader for RocksReader {
    type Iter = RocksIter;

    fn get(&self, column: Column, key: &[u8], flags: ReadFlags, buffer: &mut [u8]) -> KvResult<GetInto> {
        self.state
            .inner
            .get_into(self.state.read_options(flags), column, key, buffer)
    }

    fn get_with<R>(
        &self,
        column: Column,
        key: &[u8],
        flags: ReadFlags,
        read: impl FnOnce(&[u8]) -> R,
    ) -> KvResult<Option<R>> {
        self.state
            .inner
            .get_pinned(self.state.read_options(flags), column, key, read)
    }

    /// Port of `RocksDbReader.GetViewBetween`: fresh snapshot-bound read options per iterator, with total order only
    /// when the bounds can leave a prefix bucket.
    fn iter(&self, column: Column, options: IterOptions<'_>) -> KvResult<RocksIter> {
        let inner = &self.state.inner;
        let api = &inner.api;
        let lower_bound: Option<Box<[u8]>> = options.lower_bound.map(Into::into);
        let upper_bound: Option<Box<[u8]>> = options.upper_bound.map(Into::into);

        // SAFETY: the snapshot outlives the options and the iterator, both of which `RocksIter` owns together with
        // a reference to the snapshot. The bounds are boxed, so the slices RocksDB keeps stay put until it is dropped.
        let (read_options, iterator) = unsafe {
            let read_options = create_read_options(
                api,
                inner.verify_checksums,
                self.state.snapshot,
                !options.flags.contains(ReadFlags::HINT_CACHE_MISS),
            );
            if options.flags.contains(ReadFlags::HINT_READ_AHEAD) {
                (api.readoptions_set_readahead_size)(read_options, inner.readahead_size as usize);
            }
            if crosses_prefix_bucket(inner.prefix_extractor_length, options.lower_bound, options.upper_bound) {
                (api.readoptions_set_total_order_seek)(read_options, 1);
            }
            if let Some(bound) = &lower_bound {
                (api.readoptions_set_iterate_lower_bound)(read_options, bound.as_ptr().cast(), bound.len());
            }
            if let Some(bound) = &upper_bound {
                (api.readoptions_set_iterate_upper_bound)(read_options, bound.as_ptr().cast(), bound.len());
            }

            let iterator = (api.create_iterator_cf)(inner.db, read_options, inner.column_family(column));
            (read_options, iterator)
        };

        Ok(RocksIter {
            state: self.state.clone(),
            read_options,
            iterator,
            valid: false,
            _lower_bound: lower_bound,
            _upper_bound: upper_bound,
        })
    }
}

/// An iterator over one column of a [`RocksReader`] snapshot.
pub struct RocksIter {
    state: Arc<SnapshotState>,
    read_options: *mut RawReadOptions,
    iterator: *mut RawIterator,
    /// `rocksdb_iter_valid` after the last positioning call, so reading the key or value never reaches RocksDB with
    /// an invalid iterator.
    valid: bool,
    _lower_bound: Option<Box<[u8]>>,
    _upper_bound: Option<Box<[u8]>>,
}

// SAFETY: a RocksDB iterator may move between threads; `&self` methods only read its current entry.
unsafe impl Send for RocksIter {}

impl RocksIter {
    fn api(&self) -> &RocksFns {
        &self.state.inner.api
    }

    fn refresh_valid(&mut self) {
        // SAFETY: the iterator is live.
        self.valid = unsafe { (self.api().iter_valid)(self.iterator) } != 0;
    }

    fn entry(&self, read: unsafe extern "C" fn(*const RawIterator, *mut usize) -> *const c_char) -> &[u8] {
        assert!(self.valid, "the iterator is not positioned on an entry");
        let mut len = 0usize;
        // SAFETY: the iterator is live and valid; the bytes stay unchanged until it moves, which needs `&mut self`.
        unsafe {
            let data = read(self.iterator, &mut len);
            if len == 0 {
                &[]
            } else {
                slice::from_raw_parts(data.cast::<u8>(), len)
            }
        }
    }
}

impl KvIterator for RocksIter {
    fn seek(&mut self, key: &[u8]) {
        // SAFETY: the iterator is live and the key is valid for its length.
        unsafe { (self.api().iter_seek)(self.iterator, key.as_ptr().cast(), key.len()) };
        self.refresh_valid();
    }

    fn seek_for_prev(&mut self, key: &[u8]) {
        // SAFETY: as above.
        unsafe { (self.api().iter_seek_for_prev)(self.iterator, key.as_ptr().cast(), key.len()) };
        self.refresh_valid();
    }

    fn seek_to_first(&mut self) {
        // SAFETY: the iterator is live.
        unsafe { (self.api().iter_seek_to_first)(self.iterator) };
        self.refresh_valid();
    }

    fn seek_to_last(&mut self) {
        // SAFETY: the iterator is live.
        unsafe { (self.api().iter_seek_to_last)(self.iterator) };
        self.refresh_valid();
    }

    fn valid(&self) -> bool {
        self.valid
    }

    fn next(&mut self) {
        assert!(self.valid, "the iterator is not positioned on an entry");
        // SAFETY: the iterator is live and valid.
        unsafe { (self.api().iter_next)(self.iterator) };
        self.refresh_valid();
    }

    fn prev(&mut self) {
        assert!(self.valid, "the iterator is not positioned on an entry");
        // SAFETY: the iterator is live and valid.
        unsafe { (self.api().iter_prev)(self.iterator) };
        self.refresh_valid();
    }

    fn key(&self) -> &[u8] {
        let read = self.api().iter_key;
        self.entry(read)
    }

    fn value(&self) -> &[u8] {
        let read = self.api().iter_value;
        self.entry(read)
    }

    fn status(&self) -> KvResult<()> {
        let mut error = ptr::null_mut();
        // SAFETY: the iterator is live.
        unsafe { (self.api().iter_get_error)(self.iterator, &mut error) };
        self.state.inner.check(error)
    }
}

impl Drop for RocksIter {
    fn drop(&mut self) {
        // SAFETY: the iterator goes first, as it reads through the options and their bounds.
        unsafe {
            (self.api().iter_destroy)(self.iterator);
            (self.api().readoptions_destroy)(self.read_options);
        }
    }
}

/// A `rocksdb_writebatch_t` over the store's column families.
pub struct RocksBatch {
    inner: Arc<Inner>,
    batch: *mut RawWriteBatch,
}

// SAFETY: a write batch is owned by one `RocksBatch` and only used through `&mut self`.
unsafe impl Send for RocksBatch {}

impl KvRawBatch for RocksBatch {
    fn put(&mut self, column: Column, key: &[u8], value: &[u8]) {
        // SAFETY: the batch is live, the slices are valid for their lengths, and RocksDB copies them.
        unsafe {
            (self.inner.api.writebatch_put_cf)(
                self.batch,
                self.inner.column_family(column),
                key.as_ptr().cast(),
                key.len(),
                value.as_ptr().cast(),
                value.len(),
            )
        };
    }

    fn delete(&mut self, column: Column, key: &[u8]) {
        // SAFETY: as above.
        unsafe {
            (self.inner.api.writebatch_delete_cf)(
                self.batch,
                self.inner.column_family(column),
                key.as_ptr().cast(),
                key.len(),
            )
        };
    }

    fn delete_range(&mut self, column: Column, start: &[u8], end: &[u8]) {
        // SAFETY: as above.
        unsafe {
            (self.inner.api.writebatch_delete_range_cf)(
                self.batch,
                self.inner.column_family(column),
                start.as_ptr().cast(),
                start.len(),
                end.as_ptr().cast(),
                end.len(),
            )
        };
    }

    fn count(&self) -> usize {
        // SAFETY: the batch is live.
        let count = unsafe { (self.inner.api.writebatch_count)(self.batch) };
        count.max(0) as usize
    }

    fn clear(&mut self) {
        // SAFETY: the batch is live.
        unsafe { (self.inner.api.writebatch_clear)(self.batch) };
    }

    fn write(&mut self, flags: WriteFlags) -> KvResult<()> {
        let options = self.inner.write_options[(flags.bits() & WriteFlags::LOW_PRIORITY_AND_NO_WAL.bits()) as usize];
        let mut error = ptr::null_mut();
        // SAFETY: the database, options and batch are live.
        unsafe { (self.inner.api.write)(self.inner.db, options, self.batch, &mut error) };
        self.inner.check(error)?;
        self.clear();
        Ok(())
    }
}

impl Drop for RocksBatch {
    fn drop(&mut self) {
        // SAFETY: the batch is live and destroyed exactly once.
        unsafe { (self.inner.api.writebatch_destroy)(self.batch) };
    }
}

#[cfg(test)]
mod tests {
    use super::{KvError, record_flush_step};
    use rstest::rstest;

    #[rstest]
    #[case(&[None, None, None], None, 3)]
    #[case(&[Some("Busy: a"), Some("Busy: b"), None], Some("Busy: a"), 3)]
    #[case(&[Some("Busy: a"), Some("IO error: b"), None], Some("IO error: b"), 2)]
    #[case(&[Some("Corruption: a"), None, None], Some("Corruption: a"), 1)]
    fn flush_goes_on_after_an_error_until_a_fatal_one(
        #[case] steps: &[Option<&str>],
        #[case] reported: Option<&str>,
        #[case] steps_run: usize,
    ) {
        let mut first_error = None;
        let mut run = 0;
        for step in steps {
            run += 1;
            if record_flush_step(
                &mut first_error,
                step.map_or(Ok(()), |message| Err(KvError::rocks(message.to_owned()))),
            ) {
                break;
            }
        }

        assert_eq!(run, steps_run);
        assert_eq!(first_error.map(|error| error.to_string()).as_deref(), reported);
    }
}
