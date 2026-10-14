//! Backend calling back into a C# `IColumnsDb<FlatDbColumns>`, for databases that are not RocksDB (the tests'
//! in-memory columns, `DiagnosticMode` databases).
//!
//! It serves correctness, not speed: every read is a reverse call into C#, iterators copy their whole range when they
//! are opened, and a store without snapshots reads the latest state. Runs over it are not valid for benchmarks.

use std::ffi::c_void;
use std::ptr;
use std::slice;
use std::sync::Arc;

use super::{
    CollectedIter, Column, GetInto, IterOptions, KvError, KvRawBatch, KvReader, KvResult, KvStore, ReadFlags,
    WriteFlags,
};

/// Reads the value of `key` in `column` at `snapshot` (0 for the latest state) into `buffer`, storing its length in
/// `value_len`, or -1 when absent. A value longer than `capacity` is reported but not copied.
pub type GetFn = unsafe extern "C" fn(
    ctx: *mut c_void,
    snapshot: u64,
    column: u32,
    key: *const u8,
    key_len: usize,
    buffer: *mut u8,
    capacity: usize,
    value_len: *mut i64,
) -> i32;

/// Takes a snapshot, storing its non-zero handle.
pub type SnapshotCreateFn = unsafe extern "C" fn(ctx: *mut c_void, snapshot: *mut u64) -> i32;

pub type SnapshotReleaseFn = unsafe extern "C" fn(ctx: *mut c_void, snapshot: u64);

/// Opens an iterator over `[lower, upper)` of `column` at `snapshot`; a null bound is unbounded.
pub type IteratorCreateFn = unsafe extern "C" fn(
    ctx: *mut c_void,
    snapshot: u64,
    column: u32,
    lower: *const u8,
    lower_len: usize,
    upper: *const u8,
    upper_len: usize,
    iterator: *mut u64,
) -> i32;

/// Moves to the next entry and exposes it until the following call; returns 1 for an entry and 0 at the end.
pub type IteratorNextFn = unsafe extern "C" fn(
    ctx: *mut c_void,
    iterator: u64,
    key: *mut *const u8,
    key_len: *mut usize,
    value: *mut *const u8,
    value_len: *mut usize,
) -> i32;

pub type IteratorReleaseFn = unsafe extern "C" fn(ctx: *mut c_void, iterator: u64);

/// Applies `len` bytes of serialized operations (see [`ManagedKvVTable::write`]) atomically with `WriteFlags` `flags`.
pub type WriteFn = unsafe extern "C" fn(ctx: *mut c_void, operations: *const u8, len: usize, flags: u32) -> i32;

/// Flushes the store, only its write-ahead log when `only_wal` is 1, as `IColumnsDb.Flush(onlyWal)`.
pub type FlushFn = unsafe extern "C" fn(ctx: *mut c_void, only_wal: u8) -> i32;

/// Resizes the memtable of `column`, as `IDb.SetWriteBuffer(sizeBytes)`.
pub type SetWriteBufferFn = unsafe extern "C" fn(ctx: *mut c_void, column: u32, size_bytes: u64) -> i32;

/// Callbacks into the C# store. Every callback returns a status code, 0 for success, unless stated otherwise, and
/// may be called from any thread. Snapshots and iterators are opaque non-zero C# handles.
#[repr(C)]
pub struct ManagedKvVTable {
    /// Passed back as the first argument of every callback.
    pub ctx: *mut c_void,
    pub get: Option<GetFn>,
    /// Optional with [`ManagedKvVTable::snapshot_release`]: without them, readers read the latest state.
    pub snapshot_create: Option<SnapshotCreateFn>,
    pub snapshot_release: Option<SnapshotReleaseFn>,
    pub iterator_create: Option<IteratorCreateFn>,
    pub iterator_next: Option<IteratorNextFn>,
    pub iterator_release: Option<IteratorReleaseFn>,
    /// Applies a batch. The operations follow each other, each a tag byte, a column byte and length-prefixed byte
    /// strings (a little-endian `u32` length, then the bytes):
    /// - [`OP_PUT`]: the key, then the value;
    /// - [`OP_DELETE`]: the key;
    /// - [`OP_DELETE_RANGE`]: the inclusive start key, then the exclusive end key.
    pub write: Option<WriteFn>,
    pub flush: Option<FlushFn>,
    /// Optional: without it, [`KvStore::set_write_buffer`] does nothing.
    pub set_write_buffer: Option<SetWriteBufferFn>,
}

/// Tag of a put in a serialized batch.
pub const OP_PUT: u8 = 0;
/// Tag of a delete in a serialized batch.
pub const OP_DELETE: u8 = 1;
/// Tag of a range delete in a serialized batch.
pub const OP_DELETE_RANGE: u8 = 2;

/// The managed backend. Cloning shares the callbacks.
#[derive(Clone)]
pub struct ManagedKv {
    fns: Arc<Fns>,
}

struct Fns {
    ctx: *mut c_void,
    get: GetFn,
    snapshots: Option<(SnapshotCreateFn, SnapshotReleaseFn)>,
    iterator_create: IteratorCreateFn,
    iterator_next: IteratorNextFn,
    iterator_release: IteratorReleaseFn,
    write: WriteFn,
    flush: FlushFn,
    set_write_buffer: Option<SetWriteBufferFn>,
}

// SAFETY: the callbacks are callable from any thread, which the contract of `ManagedKv::new` requires.
unsafe impl Send for Fns {}
// SAFETY: as above.
unsafe impl Sync for Fns {}

pub(crate) fn check(operation: &'static str, status: i32) -> KvResult<()> {
    if status == 0 {
        Ok(())
    } else {
        Err(KvError::Callback { operation, status })
    }
}

impl ManagedKv {
    /// Wraps the callbacks of a C# store.
    ///
    /// # Safety
    /// The callbacks must behave as [`ManagedKvVTable`] documents and be callable from any thread, and `ctx` must
    /// stay valid until the last clone of the store, and every reader and batch it created, is dropped.
    pub unsafe fn new(vtable: &ManagedKvVTable) -> KvResult<Self> {
        let missing = |name: &str| KvError::InvalidArgument(format!("the managed key-value callbacks lack {name}"));
        let snapshots = match (vtable.snapshot_create, vtable.snapshot_release) {
            (Some(create), Some(release)) => Some((create, release)),
            (None, None) => None,
            _ => return Err(missing("snapshot_create or snapshot_release")),
        };

        Ok(Self {
            fns: Arc::new(Fns {
                ctx: vtable.ctx,
                get: vtable.get.ok_or_else(|| missing("get"))?,
                snapshots,
                iterator_create: vtable.iterator_create.ok_or_else(|| missing("iterator_create"))?,
                iterator_next: vtable.iterator_next.ok_or_else(|| missing("iterator_next"))?,
                iterator_release: vtable.iterator_release.ok_or_else(|| missing("iterator_release"))?,
                write: vtable.write.ok_or_else(|| missing("write"))?,
                flush: vtable.flush.ok_or_else(|| missing("flush"))?,
                set_write_buffer: vtable.set_write_buffer,
            }),
        })
    }

    /// Whether another clone, reader or batch still uses the callbacks.
    pub fn is_shared(&self) -> bool {
        Arc::strong_count(&self.fns) > 1
    }
}

impl Fns {
    fn get(&self, snapshot: u64, column: Column, key: &[u8], buffer: &mut [u8]) -> KvResult<GetInto> {
        let mut value_len = 0i64;
        // SAFETY: the slices are valid for their lengths and the callback honours the capacity.
        let status = unsafe {
            (self.get)(
                self.ctx,
                snapshot,
                column as u32,
                key.as_ptr(),
                key.len(),
                buffer.as_mut_ptr(),
                buffer.len(),
                &mut value_len,
            )
        };
        check("get", status)?;

        Ok(match usize::try_from(value_len) {
            Err(_) => GetInto::NotFound,
            Ok(len) if len > buffer.len() => GetInto::BufferTooSmall(len),
            Ok(len) => GetInto::Found(len),
        })
    }

    fn get_with<R>(
        &self,
        snapshot: u64,
        column: Column,
        key: &[u8],
        read: impl FnOnce(&[u8]) -> R,
    ) -> KvResult<Option<R>> {
        let mut stack = [0u8; 256];
        let mut len = match self.get(snapshot, column, key, &mut stack)? {
            GetInto::NotFound => return Ok(None),
            GetInto::Found(len) => return Ok(Some(read(&stack[..len]))),
            GetInto::BufferTooSmall(len) => len,
        };

        // Without a snapshot the value can grow between two calls, so this retries until it fits.
        loop {
            let mut heap = vec![0u8; len];
            match self.get(snapshot, column, key, &mut heap)? {
                GetInto::NotFound => return Ok(None),
                GetInto::Found(found) => return Ok(Some(read(&heap[..found]))),
                GetInto::BufferTooSmall(larger) => len = larger,
            }
        }
    }

    /// Copies the entries of `column` within the bounds of `options` at `snapshot`.
    fn collect(&self, snapshot: u64, column: Column, options: &IterOptions<'_>) -> KvResult<ManagedIter> {
        if let (Some(lower), Some(upper)) = (options.lower_bound, options.upper_bound)
            && lower >= upper
        {
            return Ok(CollectedIter::new(Vec::new()));
        }

        let bound = |bound: Option<&[u8]>| bound.map_or((ptr::null(), 0), |bound| (bound.as_ptr(), bound.len()));
        let (lower, lower_len) = bound(options.lower_bound);
        let (upper, upper_len) = bound(options.upper_bound);
        let mut iterator = 0u64;
        // SAFETY: the bounds are valid for their lengths or null.
        let status = unsafe {
            (self.iterator_create)(
                self.ctx,
                snapshot,
                column as u32,
                lower,
                lower_len,
                upper,
                upper_len,
                &mut iterator,
            )
        };
        check("iterator_create", status)?;

        let release = IteratorGuard { fns: self, iterator };
        let mut entries = Vec::new();
        loop {
            let (mut key, mut key_len, mut value, mut value_len) = (ptr::null(), 0usize, ptr::null(), 0usize);
            // SAFETY: the out-pointers are valid; the iterator is live until the guard releases it.
            let status =
                unsafe { (self.iterator_next)(self.ctx, iterator, &mut key, &mut key_len, &mut value, &mut value_len) };
            match status {
                0 => break,
                1 => {
                    // SAFETY: the callback exposes the entry until the next call, and it is copied before that.
                    let (key, value) = unsafe { (raw_slice(key, key_len), raw_slice(value, value_len)) };
                    entries.push((Box::<[u8]>::from(key), Box::<[u8]>::from(value)));
                }
                status => {
                    return Err(KvError::Callback {
                        operation: "iterator_next",
                        status,
                    });
                }
            }
        }
        drop(release);

        Ok(CollectedIter::new(entries))
    }
}

/// # Safety
/// `data` must be valid for reads of `len` bytes unless `len` is 0.
unsafe fn raw_slice<'a>(data: *const u8, len: usize) -> &'a [u8] {
    if len == 0 || data.is_null() {
        &[]
    } else {
        // SAFETY: the caller guarantees `len` readable bytes.
        unsafe { slice::from_raw_parts(data, len) }
    }
}

/// Releases a C# iterator, even when collecting its entries fails.
struct IteratorGuard<'a> {
    fns: &'a Fns,
    iterator: u64,
}

impl Drop for IteratorGuard<'_> {
    fn drop(&mut self) {
        // SAFETY: the iterator was created by `iterator_create` and is released exactly once.
        unsafe { (self.fns.iterator_release)(self.fns.ctx, self.iterator) };
    }
}

impl KvStore for ManagedKv {
    type Reader = ManagedReader;
    type RawBatch = ManagedBatch;

    fn get(&self, column: Column, key: &[u8], buffer: &mut [u8]) -> KvResult<GetInto> {
        self.fns.get(0, column, key, buffer)
    }

    fn get_with<R>(&self, column: Column, key: &[u8], read: impl FnOnce(&[u8]) -> R) -> KvResult<Option<R>> {
        self.fns.get_with(0, column, key, read)
    }

    fn reader(&self, _sequential_read_ahead: bool) -> KvResult<ManagedReader> {
        let mut snapshot = 0u64;
        if let Some((create, _)) = self.fns.snapshots {
            // SAFETY: the out-pointer is valid.
            check("snapshot_create", unsafe { create(self.fns.ctx, &mut snapshot) })?;
        }

        Ok(ManagedReader {
            kv: self.clone(),
            snapshot,
        })
    }

    fn raw_batch(&self) -> KvResult<ManagedBatch> {
        Ok(ManagedBatch {
            kv: self.clone(),
            operations: Vec::new(),
            count: 0,
        })
    }

    fn flush_wal(&self, _sync: bool) -> KvResult<()> {
        // SAFETY: the callback takes no pointers.
        check("flush", unsafe { (self.fns.flush)(self.fns.ctx, 1) })
    }

    fn flush(&self) -> KvResult<()> {
        // SAFETY: the callback takes no pointers.
        check("flush", unsafe { (self.fns.flush)(self.fns.ctx, 0) })
    }

    fn set_write_buffer(&self, column: Column, size_bytes: u64) -> KvResult<()> {
        match self.fns.set_write_buffer {
            // SAFETY: the callback takes no pointers.
            Some(set_write_buffer) => check("set_write_buffer", unsafe {
                set_write_buffer(self.fns.ctx, column as u32, size_bytes)
            }),
            None => Ok(()),
        }
    }

    fn property(&self, _column: Column, _name: &str) -> KvResult<Option<String>> {
        Ok(None)
    }
}

/// Reads at a C# snapshot, or at the latest state when the store has no snapshots.
pub struct ManagedReader {
    kv: ManagedKv,
    snapshot: u64,
}

impl Drop for ManagedReader {
    fn drop(&mut self) {
        if let Some((_, release)) = self.kv.fns.snapshots {
            // SAFETY: the snapshot was created by `snapshot_create` and is released exactly once.
            unsafe { release(self.kv.fns.ctx, self.snapshot) };
        }
    }
}

/// An iterator over a range copied from a managed store.
pub type ManagedIter = CollectedIter<Box<[u8]>>;

impl KvReader for ManagedReader {
    type Iter = ManagedIter;

    fn get(&self, column: Column, key: &[u8], _flags: ReadFlags, buffer: &mut [u8]) -> KvResult<GetInto> {
        self.kv.fns.get(self.snapshot, column, key, buffer)
    }

    fn get_with<R>(
        &self,
        column: Column,
        key: &[u8],
        _flags: ReadFlags,
        read: impl FnOnce(&[u8]) -> R,
    ) -> KvResult<Option<R>> {
        self.kv.fns.get_with(self.snapshot, column, key, read)
    }

    fn iter(&self, column: Column, options: IterOptions<'_>) -> KvResult<ManagedIter> {
        self.kv.fns.collect(self.snapshot, column, &options)
    }
}

/// Buffers serialized operations until [`KvRawBatch::write`] hands them to C#.
pub struct ManagedBatch {
    kv: ManagedKv,
    operations: Vec<u8>,
    count: usize,
}

impl ManagedBatch {
    fn push(&mut self, tag: u8, column: Column, parts: [&[u8]; 2], part_count: usize) {
        self.operations.push(tag);
        self.operations.push(column as u8);
        for part in &parts[..part_count] {
            let len = u32::try_from(part.len()).expect("a key or value is shorter than 4 GiB");
            self.operations.extend_from_slice(&len.to_le_bytes());
            self.operations.extend_from_slice(part);
        }
        self.count += 1;
    }
}

impl KvRawBatch for ManagedBatch {
    fn put(&mut self, column: Column, key: &[u8], value: &[u8]) {
        self.push(OP_PUT, column, [key, value], 2);
    }

    fn delete(&mut self, column: Column, key: &[u8]) {
        self.push(OP_DELETE, column, [key, &[]], 1);
    }

    fn delete_range(&mut self, column: Column, start: &[u8], end: &[u8]) {
        self.push(OP_DELETE_RANGE, column, [start, end], 2);
    }

    fn count(&self) -> usize {
        self.count
    }

    fn clear(&mut self) {
        self.operations.clear();
        self.count = 0;
    }

    fn write(&mut self, flags: WriteFlags) -> KvResult<()> {
        // SAFETY: the operations are valid for their length.
        let status = unsafe {
            (self.kv.fns.write)(
                self.kv.fns.ctx,
                self.operations.as_ptr(),
                self.operations.len(),
                flags.bits(),
            )
        };
        check("write", status)?;
        self.clear();
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::{ManagedKv, ManagedKvVTable};
    use std::ptr;

    fn empty_vtable() -> ManagedKvVTable {
        ManagedKvVTable {
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
        }
    }

    #[test]
    fn missing_callbacks_are_rejected() {
        // SAFETY: no callback is ever called.
        let error = unsafe { ManagedKv::new(&empty_vtable()) }
            .err()
            .expect("callbacks are missing");
        assert!(error.to_string().contains("lack get"), "{error}");
    }
}
