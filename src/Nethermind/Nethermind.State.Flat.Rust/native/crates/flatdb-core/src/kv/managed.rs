//! Backend calling back into a C# `IColumnsDb<FlatDbColumns>`, for databases that are not RocksDB (the tests'
//! in-memory columns, `DiagnosticMode` databases). Only the callback table is defined so far.

use std::ffi::c_void;

use super::{KvError, KvResult};

/// Callbacks into the C# store. Every callback returns a status code, 0 for success, unless stated otherwise.
/// Snapshots and iterators are opaque C# handles.
#[repr(C)]
pub struct ManagedKvVTable {
    /// Passed back as the first argument of every callback.
    pub ctx: *mut c_void,
    /// Copies the value of `key` at `snapshot` (0 for the latest state) into `buffer`, storing its length in
    /// `value_len`, or -1 when absent. A value longer than `capacity` is reported but not copied.
    pub get: Option<
        unsafe extern "C" fn(
            ctx: *mut c_void,
            snapshot: u64,
            column: u32,
            key: *const u8,
            key_len: usize,
            buffer: *mut u8,
            capacity: usize,
            value_len: *mut i64,
        ) -> i32,
    >,
    pub snapshot_create: Option<unsafe extern "C" fn(ctx: *mut c_void, snapshot: *mut u64) -> i32>,
    pub snapshot_release: Option<unsafe extern "C" fn(ctx: *mut c_void, snapshot: u64)>,
    /// Opens an iterator over `[lower, upper)` of `column` at `snapshot`; a null bound is unbounded.
    pub iterator_create: Option<
        unsafe extern "C" fn(
            ctx: *mut c_void,
            snapshot: u64,
            column: u32,
            lower: *const u8,
            lower_len: usize,
            upper: *const u8,
            upper_len: usize,
            iterator: *mut u64,
        ) -> i32,
    >,
    /// Moves to the next entry and exposes it until the following call; returns 1 for an entry and 0 at the end.
    pub iterator_next: Option<
        unsafe extern "C" fn(
            ctx: *mut c_void,
            iterator: u64,
            key: *mut *const u8,
            key_len: *mut usize,
            value: *mut *const u8,
            value_len: *mut usize,
        ) -> i32,
    >,
    pub iterator_release: Option<unsafe extern "C" fn(ctx: *mut c_void, iterator: u64)>,
    /// Applies a serialized batch of operations atomically with `WriteFlags` `flags`.
    pub write: Option<unsafe extern "C" fn(ctx: *mut c_void, operations: *const u8, len: usize, flags: u32) -> i32>,
    pub flush: Option<unsafe extern "C" fn(ctx: *mut c_void, only_wal: u8) -> i32>,
}

/// The managed backend.
pub struct ManagedKv {
    _vtable: ManagedKvVTable,
}

impl ManagedKv {
    /// Wraps the callbacks of a C# store.
    pub fn new(_vtable: &ManagedKvVTable) -> KvResult<Self> {
        Err(KvError::NotSupported(
            "the managed key-value backend is not implemented yet",
        ))
    }
}
