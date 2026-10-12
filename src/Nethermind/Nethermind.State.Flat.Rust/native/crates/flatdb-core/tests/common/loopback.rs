//! A fake C# store for the managed backend: a [`ManagedKvVTable`] whose callbacks drive a [`MemKv`], the way the C#
//! side will drive an `IColumnsDb`.

use std::collections::HashMap;
use std::ffi::c_void;
use std::slice;
use std::sync::atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering};

use flatdb_core::kv::managed::{ManagedKv, ManagedKvVTable, OP_DELETE, OP_DELETE_RANGE, OP_PUT};
use flatdb_core::kv::mem::{MemIter, MemKv, MemReader};
use flatdb_core::kv::{Column, IterOptions, KvIterator, KvRawBatch, KvReader, KvStore, ReadFlags, WriteFlags};
use parking_lot::Mutex;

/// The store behind the callbacks. It must outlive every [`ManagedKv`] created from it.
pub struct Loopback {
    context: Box<Context>,
    snapshots: bool,
}

struct Context {
    kv: MemKv,
    next_handle: AtomicU64,
    snapshots: Mutex<HashMap<u64, MemReader>>,
    iterators: Mutex<HashMap<u64, Iterator>>,
    /// Number of `get` callbacks, to check reads go through C#.
    gets: AtomicUsize,
    /// Whether `write` fails, as a C# store that throws.
    failing_writes: AtomicBool,
}

struct Iterator {
    iter: MemIter,
    started: bool,
}

impl Loopback {
    /// A store with snapshots, or without them when `snapshots` is false (readers then see the latest state).
    pub fn new(snapshots: bool) -> Self {
        Self {
            context: Box::new(Context {
                kv: MemKv::new(),
                next_handle: AtomicU64::new(1),
                snapshots: Mutex::new(HashMap::new()),
                iterators: Mutex::new(HashMap::new()),
                gets: AtomicUsize::new(0),
                failing_writes: AtomicBool::new(false),
            }),
            snapshots,
        }
    }

    /// The in-memory store the callbacks drive.
    pub fn mem(&self) -> &MemKv {
        &self.context.kv
    }

    /// Makes every later `write` fail with status 3.
    pub fn fail_writes(&self) {
        self.context.failing_writes.store(true, Ordering::Relaxed);
    }

    pub fn gets(&self) -> usize {
        self.context.gets.load(Ordering::Relaxed)
    }

    /// Open snapshots and iterators, which must all be released once the readers are dropped.
    pub fn open_handles(&self) -> usize {
        self.context.snapshots.lock().len() + self.context.iterators.lock().len()
    }

    pub fn vtable(&self) -> ManagedKvVTable {
        ManagedKvVTable {
            ctx: (&*self.context as *const Context).cast_mut().cast(),
            get: Some(get),
            snapshot_create: self.snapshots.then_some(snapshot_create as _),
            snapshot_release: self.snapshots.then_some(snapshot_release as _),
            iterator_create: Some(iterator_create),
            iterator_next: Some(iterator_next),
            iterator_release: Some(iterator_release),
            write: Some(write),
            flush: Some(flush),
            set_write_buffer: Some(set_write_buffer),
        }
    }

    /// A managed store over the callbacks.
    pub fn kv(&self) -> ManagedKv {
        // SAFETY: the callbacks follow the contract, are thread-safe, and the context outlives the store as long as
        // the caller keeps `self` alive.
        unsafe { ManagedKv::new(&self.vtable()) }.expect("every callback is set")
    }
}

/// # Safety
/// `ctx` must be the context of a live [`Loopback`].
unsafe fn context<'a>(ctx: *mut c_void) -> &'a Context {
    // SAFETY: the caller guarantees a live context.
    unsafe { &*ctx.cast::<Context>() }
}

/// # Safety
/// `data` must be valid for `len` bytes unless `len` is 0.
unsafe fn bytes<'a>(data: *const u8, len: usize) -> &'a [u8] {
    if len == 0 {
        &[]
    } else {
        // SAFETY: the caller guarantees `len` readable bytes.
        unsafe { slice::from_raw_parts(data, len) }
    }
}

unsafe extern "C" fn get(
    ctx: *mut c_void,
    snapshot: u64,
    column: u32,
    key: *const u8,
    key_len: usize,
    buffer: *mut u8,
    capacity: usize,
    value_len: *mut i64,
) -> i32 {
    // SAFETY: the managed backend passes the context, valid slices and out-pointer.
    let (context, key) = unsafe { (context(ctx), bytes(key, key_len)) };
    context.gets.fetch_add(1, Ordering::Relaxed);
    let Some(column) = Column::from_index(column) else {
        return 1;
    };
    let value = if snapshot == 0 {
        context.kv.get_with(column, key, <[u8]>::to_vec)
    } else {
        context.snapshots.lock()[&snapshot].get_with(column, key, ReadFlags::NONE, <[u8]>::to_vec)
    };

    // SAFETY: as above; the buffer holds `capacity` bytes.
    unsafe {
        match value.unwrap() {
            None => *value_len = -1,
            Some(value) => {
                *value_len = value.len() as i64;
                if value.len() <= capacity && !value.is_empty() {
                    slice::from_raw_parts_mut(buffer, value.len()).copy_from_slice(&value);
                }
            }
        }
    }
    0
}

unsafe extern "C" fn snapshot_create(ctx: *mut c_void, snapshot: *mut u64) -> i32 {
    // SAFETY: the managed backend passes the context and a valid out-pointer.
    let context = unsafe { context(ctx) };
    let handle = context.next_handle.fetch_add(1, Ordering::Relaxed);
    context
        .snapshots
        .lock()
        .insert(handle, context.kv.reader(false).unwrap());
    // SAFETY: as above.
    unsafe { *snapshot = handle };
    0
}

unsafe extern "C" fn snapshot_release(ctx: *mut c_void, snapshot: u64) {
    // SAFETY: the managed backend passes the context.
    let context = unsafe { context(ctx) };
    assert!(
        context.snapshots.lock().remove(&snapshot).is_some(),
        "snapshot released twice"
    );
}

unsafe extern "C" fn iterator_create(
    ctx: *mut c_void,
    snapshot: u64,
    column: u32,
    lower: *const u8,
    lower_len: usize,
    upper: *const u8,
    upper_len: usize,
    iterator: *mut u64,
) -> i32 {
    // SAFETY: the managed backend passes the context, bounds that are null or valid, and a valid out-pointer.
    let context = unsafe { context(ctx) };
    let Some(column) = Column::from_index(column) else {
        return 1;
    };
    // SAFETY: as above.
    let bound = |data: *const u8, len: usize| (!data.is_null()).then(|| unsafe { bytes(data, len) });
    let options = IterOptions {
        lower_bound: bound(lower, lower_len),
        upper_bound: bound(upper, upper_len),
        flags: ReadFlags::NONE,
    };
    let iter = if snapshot == 0 {
        context.kv.reader(false).unwrap().iter(column, options)
    } else {
        context.snapshots.lock()[&snapshot].iter(column, options)
    }
    .unwrap();

    let handle = context.next_handle.fetch_add(1, Ordering::Relaxed);
    context
        .iterators
        .lock()
        .insert(handle, Iterator { iter, started: false });
    // SAFETY: as above.
    unsafe { *iterator = handle };
    0
}

unsafe extern "C" fn iterator_next(
    ctx: *mut c_void,
    iterator: u64,
    key: *mut *const u8,
    key_len: *mut usize,
    value: *mut *const u8,
    value_len: *mut usize,
) -> i32 {
    // SAFETY: the managed backend passes the context and valid out-pointers.
    let context = unsafe { context(ctx) };
    let mut iterators = context.iterators.lock();
    let state = iterators.get_mut(&iterator).expect("a live iterator");
    if !state.started {
        state.iter.seek_to_first();
        state.started = true;
    } else if state.iter.valid() {
        state.iter.next();
    }
    if !state.iter.valid() {
        return 0;
    }

    // The entry lives on the heap until the iterator moves or is released, so the pointers stay valid that long.
    // SAFETY: the out-pointers are valid.
    unsafe {
        *key = state.iter.key().as_ptr();
        *key_len = state.iter.key().len();
        *value = state.iter.value().as_ptr();
        *value_len = state.iter.value().len();
    }
    1
}

unsafe extern "C" fn iterator_release(ctx: *mut c_void, iterator: u64) {
    // SAFETY: the managed backend passes the context.
    let context = unsafe { context(ctx) };
    assert!(
        context.iterators.lock().remove(&iterator).is_some(),
        "iterator released twice"
    );
}

/// Reads the serialized operations of a batch.
struct Cursor<'a>(&'a [u8]);

impl<'a> Cursor<'a> {
    fn take(&mut self, count: usize) -> &'a [u8] {
        let (head, tail) = self.0.split_at(count);
        self.0 = tail;
        head
    }

    fn part(&mut self) -> &'a [u8] {
        let len = u32::from_le_bytes(self.take(4).try_into().unwrap()) as usize;
        self.take(len)
    }
}

unsafe extern "C" fn write(ctx: *mut c_void, operations: *const u8, len: usize, flags: u32) -> i32 {
    // SAFETY: the managed backend passes the context and `len` bytes of operations.
    let (context, operations) = unsafe { (context(ctx), bytes(operations, len)) };
    if context.failing_writes.load(Ordering::Relaxed) {
        return 3;
    }
    let mut batch = context.kv.raw_batch().unwrap();
    let mut cursor = Cursor(operations);
    while !cursor.0.is_empty() {
        let header = cursor.take(2);
        let column = Column::from_index(u32::from(header[1])).unwrap();
        match header[0] {
            OP_PUT => {
                let (key, value) = (cursor.part(), cursor.part());
                batch.put(column, key, value);
            }
            OP_DELETE => batch.delete(column, cursor.part()),
            OP_DELETE_RANGE => {
                let (start, end) = (cursor.part(), cursor.part());
                batch.delete_range(column, start, end);
            }
            _ => return 2,
        }
    }
    batch.write(WriteFlags::from_bits(flags)).unwrap();
    0
}

unsafe extern "C" fn flush(ctx: *mut c_void, only_wal: u8) -> i32 {
    // SAFETY: the managed backend passes the context.
    let context = unsafe { context(ctx) };
    if only_wal != 0 {
        context.kv.flush_wal(true).unwrap();
    } else {
        context.kv.flush().unwrap();
    }
    0
}

unsafe extern "C" fn set_write_buffer(ctx: *mut c_void, column: u32, size_bytes: u64) -> i32 {
    // SAFETY: the managed backend passes the context.
    let context = unsafe { context(ctx) };
    context
        .kv
        .set_write_buffer(Column::from_index(column).unwrap(), size_bytes)
        .unwrap();
    0
}
