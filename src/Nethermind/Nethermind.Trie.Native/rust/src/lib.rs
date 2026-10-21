// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

//! Patricia trie bulk set over a path-keyed node store, exposed to Nethermind via FFI.
//!
//! Every node referenced by hash is stored at its `TreePath` as its RLP. Bulk sets write new nodes into a pooled
//! [`PendingNodes`] the caller publishes from, and keep decoded nodes in a shared [`NodeRecordBuffer`].

mod buffer;
mod bulk_set;
mod keccak;
mod metrics;
mod node;
mod pending;
mod traversal;
mod warmed;

use std::ffi::c_void;
use std::panic::{catch_unwind, AssertUnwindSafe};

pub use bulk_set::{bulk_set, BulkSetState, EMPTY_TREE_HASH};
pub use buffer::NodeRecordBuffer;
pub use metrics::Metrics;
pub use node::TreePath;
use node::{decode_rlp, Node};
pub use pending::{PendingNodes, WrittenNode};
pub use warmed::{warm_up, WarmedNodes};

#[global_allocator]
static ALLOCATOR: mimalloc::MiMalloc = mimalloc::MiMalloc;

/// A key to set, with its value at `values[offset..offset + length]`. A zero length deletes the key.
#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct Entry {
    pub key: [u8; 32],
    pub offset: i32,
    pub length: i32,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Error {
    InvalidArgument,
    DuplicateKey,
    Callback,
    MissingNode,
    CorruptNode,
}

impl Error {
    pub fn code(self) -> i32 {
        match self {
            Error::InvalidArgument => -2,
            Error::DuplicateKey => -3,
            Error::Callback => -4,
            Error::MissingNode => -5,
            Error::CorruptNode => -6,
        }
    }
}

pub trait NodeStore: Sync {
    /// Passes the RLP of the node at `path` whose hash is `hash` to `receive` and returns what it returned, or `None`
    /// when there is no such node.
    fn fetch_rlp<R>(
        &self,
        path: &TreePath,
        hash: &[u8; 32],
        receive: impl FnOnce(&[u8]) -> R,
    ) -> Result<Option<R>, Error>;

    /// Returns the node at `path` whose hash is `hash`, or `None` when there is none.
    fn fetch(&self, path: &TreePath, hash: &[u8; 32]) -> Result<Option<Node>, Error> {
        self.fetch_rlp(path, hash, decode_rlp)?.transpose()
    }
}

/// Receives the RLP of a fetched node, valid only for the duration of the call.
pub type ReceiveFn = unsafe extern "C" fn(context: *mut c_void, rlp: *const u8, length: i32);

/// Passes the RLP of the node at `path` with hash `hash` to `receive` with `receive_context` and returns 1, returns 0
/// when missing, or a negative value on failure.
pub type FetchFn = unsafe extern "C" fn(
    context: *mut c_void,
    path: *const TreePath,
    hash: *const [u8; 32],
    receive: ReceiveFn,
    receive_context: *mut c_void,
) -> i32;

struct CallbackStore {
    fetch: FetchFn,
    context: *mut c_void,
}

// The callback is invoked from rayon workers; the caller guarantees it is thread safe.
unsafe impl Sync for CallbackStore {}

impl NodeStore for CallbackStore {
    fn fetch_rlp<R>(
        &self,
        path: &TreePath,
        hash: &[u8; 32],
        receive: impl FnOnce(&[u8]) -> R,
    ) -> Result<Option<R>, Error> {
        let mut receive = Some(receive);
        let mut received = None;
        let mut call = |rlp: &[u8]| {
            if let Some(receive) = receive.take() {
                received = Some(receive(rlp));
            }
        };
        let mut receiver = Receiver {
            receive: &mut call,
            panicked: false,
        };
        let status = unsafe {
            (self.fetch)(
                self.context,
                path,
                hash,
                receive_rlp,
                &mut receiver as *mut Receiver as *mut c_void,
            )
        };
        let panicked = receiver.panicked;
        match status {
            0 => Ok(None),
            1 if !panicked => received.ok_or(Error::Callback).map(Some),
            1 => Err(Error::CorruptNode),
            _ => Err(Error::Callback),
        }
    }
}

/// What [`receive_rlp`] passes the RLP to, as its context.
struct Receiver<'a> {
    receive: &'a mut dyn FnMut(&[u8]),
    panicked: bool,
}

unsafe extern "C" fn receive_rlp(context: *mut c_void, rlp: *const u8, length: i32) {
    let rlp: &[u8] = if length <= 0 {
        &[]
    } else {
        std::slice::from_raw_parts(rlp, length as usize)
    };
    let receiver = &mut *(context as *mut Receiver);
    // Unwinding must not cross the caller's frames.
    if catch_unwind(AssertUnwindSafe(|| (receiver.receive)(rlp))).is_err() {
        receiver.panicked = true;
    }
}

/// Applies `entries` to the trie whose root hash is `root`, writing new nodes into `pending`, and writes the new root
/// hash to `root_out`. Nodes written also go into `buffer`, or a temporary buffer when null, the new root indexed as that of
/// the trie of `address`, the state trie when null. Nodes reachable through that root in `buffer` are read there, and the
/// others from `pending`, then `warmed` unless null, then `fetch`. `entries` is reordered in place.
/// Returns 0 on success, -1 on panic, or an [`Error::code`].
///
/// # Safety
/// `entries`, `values`, `root`, `address` and `root_out` must be valid for the given lengths (32 bytes for `root`,
/// `address` and `root_out`), `pending` must come from [`nm_patricia_pending_rent`], `buffer` must be null or come
/// from [`nm_patricia_buffer_new`], `warmed` must be null or come from [`nm_patricia_warmed_new`] and not be reset
/// during the call, and `fetch` must be callable concurrently with `context`.
#[no_mangle]
pub unsafe extern "C" fn nm_patricia_bulk_set(
    entries: *mut Entry,
    entry_count: i32,
    values: *const u8,
    values_length: i32,
    root: *const [u8; 32],
    pending: *const PendingNodes,
    buffer: *const NodeRecordBuffer,
    warmed: *const WarmedNodes,
    address: *const [u8; 32],
    fetch: FetchFn,
    context: *mut c_void,
    root_out: *mut u8,
) -> i32 {
    if entry_count < 0
        || values_length < 0
        || root.is_null()
        || pending.is_null()
        || root_out.is_null()
    {
        return Error::InvalidArgument.code();
    }

    let result = catch_unwind(AssertUnwindSafe(|| {
        let entries: &mut [Entry] = if entry_count == 0 {
            &mut []
        } else {
            std::slice::from_raw_parts_mut(entries, entry_count as usize)
        };
        let values: &[u8] = if values_length == 0 {
            &[]
        } else {
            std::slice::from_raw_parts(values, values_length as usize)
        };
        bulk_set(
            &CallbackStore { fetch, context },
            &*pending,
            buffer.as_ref(),
            warmed.as_ref(),
            address.as_ref(),
            &*root,
            entries,
            values,
        )
    }));

    match result {
        Ok(Ok(root)) => {
            std::ptr::copy_nonoverlapping(root.as_ptr(), root_out, root.len());
            0
        }
        Ok(Err(error)) => error.code(),
        Err(_) => -1,
    }
}

/// Creates an owned unhashed trie, to release with [`nm_patricia_bulk_state_free`]. Returns null for a null root or
/// a panic. The root and optional address are copied; no node store or buffer is retained.
///
/// # Safety
/// `root` must point to 32 readable bytes, and `address` must be null or point to 32 readable bytes.
#[no_mangle]
pub unsafe extern "C" fn nm_patricia_bulk_state_new(
    root: *const [u8; 32],
    address: *const [u8; 32],
) -> *mut BulkSetState {
    if root.is_null() {
        return std::ptr::null_mut();
    }
    catch_unwind(AssertUnwindSafe(|| {
        Box::into_raw(Box::new(BulkSetState::new(
            *root,
            address.as_ref().copied(),
        )))
    }))
    .unwrap_or(std::ptr::null_mut())
}

/// Applies entries to `state` without hashing or publishing nodes. Input buffers and callbacks are used only during
/// this call. Entries are reordered and must have unique keys. Returns 0 on success, -1 on panic, or an [`Error::code`].
/// Failure invalidates the state, which must then be freed.
///
/// # Safety
/// `state` must come from [`nm_patricia_bulk_state_new`], not be freed, and be exclusively accessed during this call.
/// `buffer` must come from [`nm_patricia_buffer_new`], be the same buffer for every call on this state, and not be freed
/// during the call. The caller must retain the base
/// trie's nonpersisted nodes in the buffer between calls. `warmed` must be null or come from [`nm_patricia_warmed_new`]
/// and not be reset or freed during this call. Nonempty `entries` and `values` must be valid for their lengths;
/// `fetch` and `context` must be valid for the call.
#[no_mangle]
pub unsafe extern "C" fn nm_patricia_bulk_state_update(
    state: *mut BulkSetState,
    entries: *mut Entry,
    entry_count: i32,
    values: *const u8,
    values_length: i32,
    buffer: *const NodeRecordBuffer,
    warmed: *const WarmedNodes,
    fetch: FetchFn,
    context: *mut c_void,
) -> i32 {
    let Some(state) = state.as_mut() else {
        return Error::InvalidArgument.code();
    };
    let result = catch_unwind(AssertUnwindSafe(|| {
        let buffer = buffer.as_ref().ok_or(Error::InvalidArgument)?;
        let (entries, values) = bulk_state_inputs(entries, entry_count, values, values_length)?;
        state.update(
            &CallbackStore { fetch, context },
            buffer,
            warmed.as_ref(),
            entries,
            values,
        )
    }));
    bulk_state_status(state, result)
}

/// Applies final corrections, then hashes the complete trie into `pending`, indexes its root in `buffer`, and writes
/// its hash to `root_out`. The state is invalidated on success or failure, but its allocation still must be freed.
/// Zero correction entries still finish the prepared trie. Returns 0, -1 on panic, or an [`Error::code`].
///
/// # Safety
/// The state, buffers, node record buffer, warmed nodes, and callback have the same requirements as
/// [`nm_patricia_bulk_state_update`]. `pending` must come from [`nm_patricia_pending_rent`] and remain alive for the
/// call. `root_out` must point to 32 writable bytes. Neither `state` nor `pending` may be used concurrently with this
/// call. Nodes written before a failure must not be published.
#[no_mangle]
pub unsafe extern "C" fn nm_patricia_bulk_state_finish(
    state: *mut BulkSetState,
    entries: *mut Entry,
    entry_count: i32,
    values: *const u8,
    values_length: i32,
    pending: *const PendingNodes,
    buffer: *const NodeRecordBuffer,
    warmed: *const WarmedNodes,
    fetch: FetchFn,
    context: *mut c_void,
    root_out: *mut u8,
) -> i32 {
    let Some(state) = state.as_mut() else {
        return Error::InvalidArgument.code();
    };
    let result = catch_unwind(AssertUnwindSafe(|| {
        let buffer = buffer.as_ref().ok_or(Error::InvalidArgument)?;
        let pending = pending.as_ref().ok_or(Error::InvalidArgument)?;
        if root_out.is_null() {
            return Err(Error::InvalidArgument);
        }
        let (entries, values) = bulk_state_inputs(entries, entry_count, values, values_length)?;
        let root = state.finish(
            &CallbackStore { fetch, context },
            pending,
            buffer,
            warmed.as_ref(),
            entries,
            values,
        )?;
        std::ptr::copy_nonoverlapping(root.as_ptr(), root_out, root.len());
        Ok(())
    }));
    bulk_state_status(state, result)
}

/// Releases an owned trie, whether prepared, finished, or invalidated. No buffer or node store is needed.
///
/// # Safety
/// `state` must come from [`nm_patricia_bulk_state_new`], not have been freed, and not be in use.
#[no_mangle]
pub unsafe extern "C" fn nm_patricia_bulk_state_free(state: *mut BulkSetState) {
    drop(Box::from_raw(state));
}

unsafe fn bulk_state_inputs<'a>(
    entries: *mut Entry,
    entry_count: i32,
    values: *const u8,
    values_length: i32,
) -> Result<(&'a mut [Entry], &'a [u8]), Error> {
    if entry_count < 0
        || values_length < 0
        || (entry_count > 0 && entries.is_null())
        || (values_length > 0 && values.is_null())
    {
        return Err(Error::InvalidArgument);
    }
    let entries = if entry_count == 0 {
        &mut []
    } else {
        std::slice::from_raw_parts_mut(entries, entry_count as usize)
    };
    let values = if values_length == 0 {
        &[]
    } else {
        std::slice::from_raw_parts(values, values_length as usize)
    };
    Ok((entries, values))
}

fn bulk_state_status(
    state: &mut BulkSetState,
    result: std::thread::Result<Result<(), Error>>,
) -> i32 {
    match result {
        Ok(Ok(())) => 0,
        Ok(Err(error)) => {
            state.invalidate();
            error.code()
        }
        Err(_) => {
            state.invalidate();
            -1
        }
    }
}

/// Creates a [`NodeRecordBuffer`] keeping the newest `live_generations` readable in place, whose compacted generations take
/// about `compressed_max_memory` bytes, to free with [`nm_patricia_buffer_free`].
#[no_mangle]
pub extern "C" fn nm_patricia_buffer_new(
    compressed_max_memory: u64,
    live_generations: u64,
) -> *mut NodeRecordBuffer {
    Box::into_raw(NodeRecordBuffer::new(
        compressed_max_memory,
        live_generations,
    ))
}

/// Creates a small-generation buffer for internal RLP fixtures.
#[no_mangle]
pub extern "C" fn nm_patricia_buffer_fixture_new(
    compressed_max_memory: u64,
    generation_bytes: usize,
    live_generations: u64,
) -> *mut NodeRecordBuffer {
    Box::into_raw(NodeRecordBuffer::for_fixtures(
        generation_bytes,
        compressed_max_memory,
        live_generations,
    ))
}

/// Creates an overlay of `parent`, see [`NodeRecordBuffer::overlay`], to free with [`nm_patricia_buffer_free`].
///
/// # Safety
/// `parent` must come from [`nm_patricia_buffer_new`] and outlive the returned buffer.
#[no_mangle]
pub unsafe extern "C" fn nm_patricia_buffer_overlay_new(
    parent: *const NodeRecordBuffer,
) -> *mut NodeRecordBuffer {
    Box::into_raw(NodeRecordBuffer::overlay(&*parent))
}

/// Reports the buffer's initial allocation and all subsequent allocation/reclamation deltas, including on compactor threads.
///
/// # Safety
/// `buffer` must be newly created, not yet used or shared, and this must be called only once. `report` must remain callable
/// from any thread until [`nm_patricia_buffer_free`] returns and must not unwind or re-enter the buffer.
#[no_mangle]
pub unsafe extern "C" fn nm_patricia_buffer_set_memory_pressure(
    buffer: *mut NodeRecordBuffer,
    report: buffer::MemoryPressureFn,
) {
    (*buffer).set_memory_pressure(report);
}

/// Appends fixture RLP using the live record encoder, linking only matching hash-referenced children.
///
/// # Safety
/// `buffer` is live, `rlp` is readable for `length`, `path` and `pointer_out` are valid, and `address` is null or
/// readable for 32 bytes. `children` is null or readable for 16 pointers to retained records in this buffer.
#[no_mangle]
pub unsafe extern "C" fn nm_patricia_buffer_fixture_append_rlp(
    buffer: *const NodeRecordBuffer,
    address: *const [u8; 32],
    path: *const TreePath,
    rlp: *const u8,
    length: usize,
    children: *const u64,
    index_root: bool,
    pointer_out: *mut u64,
) -> i32 {
    if buffer.is_null() || rlp.is_null() || path.is_null() || pointer_out.is_null() {
        return Error::InvalidArgument.code();
    }
    match catch_unwind(AssertUnwindSafe(|| {
        let rlp = std::slice::from_raw_parts(rlp, length);
        let mut decoded = decode_rlp(rlp)?;
        let pointers = children.as_ref().map(|_| std::slice::from_raw_parts(children, 16));
        if pointers.is_some_and(|p| p.iter().any(|pointer| pointer & node::TRANSIENT_POINTER != 0)) {
            return Err(Error::CorruptNode);
        }
        let session = (*buffer).pin();
        let link = |child: &mut node::ChildRef, slot: usize| {
            let Some(pointers) = pointers else {
                return;
            };
            let candidate = pointers[slot];
            if candidate == 0 {
                return;
            }
            if let node::ChildRef::Hash { hash, pointer } = child {
                if session
                    .record(candidate)
                    .is_some_and(|record| node::BufferedNode::new(record).hash() == hash)
                {
                    *pointer = candidate;
                }
            }
        };
        match &mut decoded {
            Node::Leaf { .. } => {},
            Node::Extension { child, .. } => link(child, 0),
            Node::Branch { children } => {
                for (slot, child) in children.iter_mut().enumerate() { link(child, slot); }
            }
        }
        let hash = keccak::keccak(rlp);
        let key = buffer::node_key(address.as_ref(), &*path);
        let mut written = Vec::new();
        session.append_all(std::iter::once((&decoded, key, &hash, rlp)), &mut written);
        let pointer = written[0];
        if index_root { session.set_root(address.as_ref(), &hash, pointer); }
        *pointer_out = pointer;
        Ok(())
    })) {
        Ok(Ok(())) => 0,
        Ok(Err(error)) => error.code(),
        Err(_) => -1,
    }
}

/// Copies a fixture record while a read session protects it; returns the required size or zero if unavailable.
///
/// # Safety
/// `buffer` is live, `pointer` is a previously imported or written record, and `output` is writable for `capacity`.
#[no_mangle]
pub unsafe extern "C" fn nm_patricia_buffer_fixture_copy(
    buffer: *const NodeRecordBuffer,
    pointer: u64,
    output: *mut u8,
    capacity: usize,
) -> i32 {
    match catch_unwind(AssertUnwindSafe(|| {
        let session = (*buffer).pin();
        let Some(record) = session.record(pointer) else {
            return 0;
        };
        if record.len() <= capacity {
            std::ptr::copy_nonoverlapping(record.as_ptr(), output, record.len());
        }
        record.len() as i32
    })) {
        Ok(length) => length,
        Err(_) => -1,
    }
}

/// Whether a fixture record still has its uncompressed generation buffer.
///
/// # Safety
/// `buffer` must be live.
#[no_mangle]
pub unsafe extern "C" fn nm_patricia_buffer_fixture_live(
    buffer: *const NodeRecordBuffer,
    pointer: u64,
) -> bool {
    (*buffer).fixture_generation_live(pointer)
}

/// # Safety
/// `buffer` must come from [`nm_patricia_buffer_new`], not be freed yet, and not be in use by a bulk set.
#[no_mangle]
pub unsafe extern "C" fn nm_patricia_buffer_free(buffer: *mut NodeRecordBuffer) {
    drop(Box::from_raw(buffer));
}

/// Retains the active generation of `buffer`, and so every later one, for bulk sets until [`nm_patricia_buffer_release`],
/// and returns it.
///
/// # Safety
/// `buffer` must come from [`nm_patricia_buffer_new`] and not be freed yet.
#[no_mangle]
pub unsafe extern "C" fn nm_patricia_buffer_retain_active(
    buffer: *const NodeRecordBuffer,
    boundary_out: *mut u64,
) -> u64 {
    let (generation, boundary) = (*buffer).retain_active_boundary();
    *boundary_out = boundary;
    generation
}

/// Retains `generation` of `buffer` once more, until [`nm_patricia_buffer_release`].
///
/// # Safety
/// `buffer` must come from [`nm_patricia_buffer_new`] and not be freed yet, and `generation` must be retained.
#[no_mangle]
pub unsafe extern "C" fn nm_patricia_buffer_retain(
    buffer: *const NodeRecordBuffer,
    generation: u64,
) {
    (*buffer).retain(generation)
}

/// Releases a retain of `generation` of `buffer`.
///
/// # Safety
/// `buffer` must come from [`nm_patricia_buffer_new`] and not be freed yet, and `generation` must be retained.
#[no_mangle]
pub unsafe extern "C" fn nm_patricia_buffer_release(
    buffer: *const NodeRecordBuffer,
    generation: u64,
) {
    (*buffer).release(generation)
}

/// Warms the path to `key` in the trie of `address`, the state trie when null, with root hash `root`: every node on it
/// that bulk sets cannot read from `buffer` nor `warmed` is fetched through `fetch` and added to `warmed`.
/// Returns 0 on success, -1 on panic, or an [`Error::code`].
///
/// # Safety
/// `buffer` must come from [`nm_patricia_buffer_new`], `warmed` from [`nm_patricia_warmed_new`] and not be reset during the
/// call, `address` must be null or valid for 32 bytes, `root` and `key` must be valid for 32 bytes, and `fetch` must be
/// callable with `context`.
#[no_mangle]
pub unsafe extern "C" fn nm_patricia_warm_up(
    buffer: *const NodeRecordBuffer,
    warmed: *const WarmedNodes,
    address: *const [u8; 32],
    root: *const [u8; 32],
    key: *const [u8; 32],
    fetch: FetchFn,
    context: *mut c_void,
    for_delete: bool,
) -> i32 {
    match catch_unwind(AssertUnwindSafe(|| {
        warm_up(
            &CallbackStore { fetch, context },
            &*buffer,
            &*warmed,
            address.as_ref(),
            &*root,
            &*key,
            for_delete,
        )
    })) {
        Ok(Ok(())) => 0,
        Ok(Err(error)) => error.code(),
        Err(_) => -1,
    }
}

/// Creates empty [`WarmedNodes`], to free with [`nm_patricia_warmed_free`].
#[no_mangle]
pub extern "C" fn nm_patricia_warmed_new() -> *mut WarmedNodes {
    Box::into_raw(Box::default())
}

/// # Safety
/// `warmed` must come from [`nm_patricia_warmed_new`], not be freed yet, and not be in use.
#[no_mangle]
pub unsafe extern "C" fn nm_patricia_warmed_free(warmed: *mut WarmedNodes) {
    drop(Box::from_raw(warmed));
}

/// Drops every node of `warmed`.
///
/// # Safety
/// `warmed` must come from [`nm_patricia_warmed_new`] and not be in use.
#[no_mangle]
pub unsafe extern "C" fn nm_patricia_warmed_reset(warmed: *const WarmedNodes) {
    (*warmed).reset();
}

/// Resolves a trie root through a session that protects the root-index walk.
///
/// # Safety
/// `buffer` must be live, `root` readable for 32 bytes, and `address` null or readable for 32 bytes.
#[no_mangle]
pub unsafe extern "C" fn nm_patricia_buffer_root(
    buffer: *const NodeRecordBuffer,
    address: *const [u8; 32],
    root: *const [u8; 32],
) -> u64 {
    (*buffer).pin().root(address.as_ref(), &*root)
}

/// Copies the exact-path node while its read session is alive. Returns its length, zero on a miss, or -1 on panic.
/// A short output buffer is not written and the required length is returned.
///
/// # Safety
/// `buffer` must be live and the root must have originated from it. `path` and `hash_out` must be valid;
/// `out` must be writable for `capacity` bytes. No borrowed record escapes the session.
#[no_mangle]
pub unsafe extern "C" fn nm_patricia_buffer_find_node(
    buffer: *const NodeRecordBuffer,
    root: u64,
    path: *const TreePath,
    hash_out: *mut [u8; 32],
    out: *mut u8,
    capacity: usize,
) -> i32 {
    match catch_unwind(AssertUnwindSafe(|| {
        if (*path).length < 0 || (*path).length > 64 {
            return 0;
        }
        let session = (*buffer).pin();
        let Some(node) = session.find_node(root, &*path) else {
            return 0;
        };
        *hash_out = *node.hash();
        if capacity >= node.rlp().len() {
            std::ptr::copy_nonoverlapping(node.rlp().as_ptr(), out, node.rlp().len());
        }
        node.rlp().len() as i32
    })) { Ok(length) => length, Err(_) => -1 }
}

/// Creates a bounded cursor with its own generation retain.
///
/// # Safety
/// `buffer` must outlive the cursor, and `boundary` must belong to a currently retained generation of it.
/// `root` must be zero or a node pointer from the same buffer.
#[no_mangle]
pub unsafe extern "C" fn nm_patricia_buffer_cursor_new(
    buffer: *const NodeRecordBuffer,
    root: u64,
    boundary: u64,
) -> *mut c_void {
    if buffer.is_null() || boundary >> 32 == 0 {
        return std::ptr::null_mut();
    }
    Box::into_raw(Box::new(traversal::SnapshotCursor::new(
        &*buffer, root, boundary,
    )))
    .cast()
}

/// # Safety
/// `cursor` must be a live exclusively owned cursor; `path_out` and `pointer_out` must be writable.
#[no_mangle]
pub unsafe extern "C" fn nm_patricia_buffer_cursor_next(
    cursor: *mut c_void,
    path_out: *mut TreePath,
    pointer_out: *mut u64,
) -> i32 {
    match catch_unwind(AssertUnwindSafe(|| {
        (*cursor.cast::<traversal::SnapshotCursor<'static>>()).next()
    })) {
        Ok(Some((path, pointer))) => {
            *path_out = path;
            *pointer_out = pointer;
            1
        }
        Ok(None) => 0,
        Err(_) => -1,
    }
}

/// # Safety
/// `cursor` must be a cursor not previously freed, whose buffer is still alive.
#[no_mangle]
pub unsafe extern "C" fn nm_patricia_buffer_cursor_free(cursor: *mut c_void) {
    drop(Box::from_raw(cursor.cast::<traversal::SnapshotCursor>()));
}

/// Writes to `hash_out` the hash of the node whose record is at `pointer` in `buffer`, points `rlp_out` at its RLP, valid
/// as long as the record, and returns the RLP length, or -1 on panic.
///
/// # Safety
/// `buffer` must come from [`nm_patricia_buffer_new`], `pointer` must be a node record of a generation retained, as by
/// [`nm_patricia_buffer_retain_active`], and `hash_out` and `rlp_out` must be writable.
#[no_mangle]
pub unsafe extern "C" fn nm_patricia_buffer_node(
    buffer: *const NodeRecordBuffer,
    pointer: u64,
    hash_out: *mut [u8; 32],
    rlp_out: *mut *const u8,
) -> i32 {
    match catch_unwind(AssertUnwindSafe(|| (*buffer).node(pointer))) {
        Ok(node) => {
            *hash_out = *node.hash();
            *rlp_out = node.rlp().as_ptr();
            node.rlp().len() as i32
        }
        Err(_) => -1,
    }
}

/// Takes an empty [`PendingNodes`] sized for a bulk set of about `estimated_entries`, to give back with
/// [`nm_patricia_pending_return`].
#[no_mangle]
pub extern "C" fn nm_patricia_pending_rent(estimated_entries: i32) -> *mut PendingNodes {
    Box::into_raw(pending::rent(estimated_entries.max(0) as usize))
}

/// Gives `pending` back to the pool. It must not be used afterwards.
///
/// # Safety
/// `pending` must come from [`nm_patricia_pending_rent`] and not have been returned yet.
#[no_mangle]
pub unsafe extern "C" fn nm_patricia_pending_return(pending: *mut PendingNodes) {
    pending::give_back(Box::from_raw(pending));
}

/// # Safety
/// `pending` must come from [`nm_patricia_pending_rent`].
#[no_mangle]
pub unsafe extern "C" fn nm_patricia_pending_count(pending: *const PendingNodes) -> i32 {
    (*pending).count() as i32
}

/// # Safety
/// `pending` must come from [`nm_patricia_pending_rent`].
#[no_mangle]
pub unsafe extern "C" fn nm_patricia_pending_clear(pending: *const PendingNodes) {
    (*pending).clear();
}

/// # Safety
/// `pending` must come from [`nm_patricia_pending_rent`].
#[no_mangle]
pub unsafe extern "C" fn nm_patricia_pending_shard_count(pending: *const PendingNodes) -> i32 {
    (*pending).shard_count() as i32
}

/// Points `nodes` at the nodes written to `shard` and `rlp` at the arena their RLP offsets point into, and returns their
/// count. Both stay valid until the next bulk set, clear or return of `pending`.
///
/// # Safety
/// `pending` must come from [`nm_patricia_pending_rent`], `shard` must be below its
/// [`nm_patricia_pending_shard_count`], and `nodes` and `rlp` must be writable.
#[no_mangle]
pub unsafe extern "C" fn nm_patricia_pending_view(
    pending: *const PendingNodes,
    shard: i32,
    nodes: *mut *const WrittenNode,
    rlp: *mut *const u8,
) -> i32 {
    (*pending).with_view(shard as usize, |written, arena| {
        *nodes = written.as_ptr();
        *rlp = arena.as_ptr();
        written.len() as i32
    })
}

/// Writes the process-wide bulk set and trie node buffer counters to `metrics`.
///
/// # Safety
/// `metrics` must be writable.
#[no_mangle]
pub unsafe extern "C" fn nm_patricia_metrics(metrics: *mut Metrics) {
    *metrics = metrics::read();
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::ptr::{null, null_mut};

    #[test]
    fn fixture_append_links_matching_children_and_rejects_invalid_inputs() {
        let buffer =
            NodeRecordBuffer::for_fixtures(64 << 10, 0, crate::buffer::DEFAULT_LIVE_GENERATIONS);
        let session = buffer.pin();
        let rlp = [0xc4, 0x82, 0x20, 0x01, 0x05];
        let hash = keccak::keccak(&rlp);
        let path = TreePath::default();
        let mut child = 0;
        unsafe {
            assert_eq!(
                nm_patricia_buffer_fixture_append_rlp(
                    &*buffer,
                    null(),
                    &path,
                    rlp.as_ptr(),
                    rlp.len(),
                    null(),
                    false,
                    &mut child
                ),
                0
            );
        }
        let extension = Node::Extension {
            key: node::Nibbles::from_slice(&[1]),
            child: node::ChildRef::Hash { hash, pointer: 0 },
        };
        let mut extension_rlp = Vec::new();
        node::encode(&extension, &mut extension_rlp);
        let extension_hash = keccak::keccak(&extension_rlp);
        let mut children = [0; 16];
        children[0] = child;
        let mut root = 0;
        unsafe {
            assert_eq!(
                nm_patricia_buffer_fixture_append_rlp(
                    &*buffer,
                    null(),
                    &path,
                    extension_rlp.as_ptr(),
                    extension_rlp.len(),
                    children.as_ptr(),
                    true,
                    &mut root
                ),
                0
            );
        }
        assert_eq!(session.root(None, &extension_hash), root);
        assert_eq!(
            session.find_node(root, &path.append(&[1])).unwrap().rlp(),
            rlp
        );
        assert_eq!(
            node::BufferedNode::new(session.record(root).unwrap()).pointer(0),
            child
        );
        children[0] = root;
        unsafe {
            assert_eq!(
                nm_patricia_buffer_fixture_append_rlp(
                    &*buffer,
                    null(),
                    &path,
                    extension_rlp.as_ptr(),
                    extension_rlp.len(),
                    children.as_ptr(),
                    false,
                    &mut root
                ),
                0
            );
        }
        assert_eq!(
            node::BufferedNode::new(session.record(root).unwrap()).pointer(0),
            0
        );
        children[0] = node::TRANSIENT_POINTER;
        unsafe {
            assert_eq!(
                nm_patricia_buffer_fixture_append_rlp(
                    &*buffer,
                    null(),
                    &path,
                    extension_rlp.as_ptr(),
                    extension_rlp.len(),
                    children.as_ptr(),
                    false,
                    &mut root
                ),
                Error::CorruptNode.code()
            );
            assert_eq!(
                nm_patricia_buffer_fixture_append_rlp(
                    &*buffer,
                    null(),
                    &path,
                    rlp.as_ptr(),
                    rlp.len() - 1,
                    null(),
                    false,
                    &mut root
                ),
                Error::CorruptNode.code()
            );
        }
    }

    unsafe extern "C" fn missing_node(
        _: *mut c_void,
        _: *const TreePath,
        _: *const [u8; 32],
        _: ReceiveFn,
        _: *mut c_void,
    ) -> i32 {
        0
    }

    unsafe extern "C" fn failing_node(
        _: *mut c_void,
        _: *const TreePath,
        _: *const [u8; 32],
        _: ReceiveFn,
        _: *mut c_void,
    ) -> i32 {
        -1
    }

    #[test]
    fn bulk_state_ffi_retains_owned_inputs_and_finishes_without_corrections() {
        let buffer = NodeRecordBuffer::minimal();
        let pending = pending::rent(2);
        let address = [7; 32];
        let mut input_address = address;
        let mut entries = [
            Entry {
                key: [1; 32],
                offset: 0,
                length: 1,
            },
            Entry {
                key: [2; 32],
                offset: 1,
                length: 1,
            },
        ];
        let mut values = [3, 4];
        let state = unsafe { nm_patricia_bulk_state_new(&EMPTY_TREE_HASH, &input_address) };
        assert!(!state.is_null());
        // SAFETY: Every native owner stays alive through its calls, and the state is exclusively accessed and freed once.
        unsafe {
            assert_eq!(
                nm_patricia_bulk_state_update(
                    state,
                    entries.as_mut_ptr(),
                    entries.len() as i32,
                    values.as_ptr(),
                    values.len() as i32,
                    &*buffer,
                    null(),
                    missing_node,
                    null_mut(),
                ),
                0
            );
            assert_eq!(pending.count(), 0);
            entries.fill(Entry {
                key: [0; 32],
                offset: 0,
                length: 0,
            });
            values.fill(0);
            input_address.fill(0);
            let mut root = [0; 32];
            assert_eq!(
                nm_patricia_bulk_state_finish(
                    state,
                    null_mut(),
                    0,
                    null(),
                    0,
                    &*pending,
                    &*buffer,
                    null(),
                    missing_node,
                    null_mut(),
                    root.as_mut_ptr(),
                ),
                0
            );
            assert_ne!(buffer.pin().root(Some(&address), &root), 0);
            let mut expected_entries = [
                Entry {
                    key: [1; 32],
                    offset: 0,
                    length: 1,
                },
                Entry {
                    key: [2; 32],
                    offset: 1,
                    length: 1,
                },
            ];
            let expected = bulk_set(
                &CallbackStore {
                    fetch: missing_node,
                    context: null_mut(),
                },
                &PendingNodes::default(),
                None,
                None,
                None,
                &EMPTY_TREE_HASH,
                &mut expected_entries,
                &[3, 4],
            )
            .unwrap();
            assert_eq!(root, expected);
            assert_eq!(
                nm_patricia_bulk_state_update(
                    state,
                    null_mut(),
                    0,
                    null(),
                    0,
                    &*buffer,
                    null(),
                    missing_node,
                    null_mut(),
                ),
                Error::InvalidArgument.code()
            );
            nm_patricia_bulk_state_free(state);
        }
        pending::give_back(pending);
    }

    #[test]
    fn bulk_state_ffi_invalidates_on_bad_arguments_and_callback_errors() {
        let buffer = NodeRecordBuffer::minimal();
        let mut entry = Entry {
            key: [0; 32],
            offset: 0,
            length: 1,
        };
        let value = 1;
        // SAFETY: Non-null pointers refer to the live locals or native states, each accessed serially and freed once.
        unsafe {
            assert!(nm_patricia_bulk_state_new(null(), null()).is_null());
            for (entries, count, values, length) in [
                (null_mut(), 1, &value as *const u8, 1),
                (&mut entry as *mut Entry, -1, &value as *const u8, 1),
                (&mut entry as *mut Entry, 1, null(), 1),
                (&mut entry as *mut Entry, 1, &value as *const u8, -1),
            ] {
                let state = nm_patricia_bulk_state_new(&EMPTY_TREE_HASH, null());
                assert_eq!(
                    nm_patricia_bulk_state_update(
                        state,
                        entries,
                        count,
                        values,
                        length,
                        &*buffer,
                        null(),
                        missing_node,
                        null_mut(),
                    ),
                    Error::InvalidArgument.code()
                );
                assert_eq!(
                    nm_patricia_bulk_state_update(
                        state,
                        null_mut(),
                        0,
                        null(),
                        0,
                        &*buffer,
                        null(),
                        missing_node,
                        null_mut(),
                    ),
                    Error::InvalidArgument.code()
                );
                nm_patricia_bulk_state_free(state);
            }
            let state = nm_patricia_bulk_state_new(&[1; 32], null());
            assert_eq!(
                nm_patricia_bulk_state_update(
                    state,
                    &mut entry,
                    1,
                    &value,
                    1,
                    &*buffer,
                    null(),
                    failing_node,
                    null_mut(),
                ),
                Error::Callback.code()
            );
            assert_eq!(
                nm_patricia_bulk_state_update(
                    state,
                    null_mut(),
                    0,
                    null(),
                    0,
                    &*buffer,
                    null(),
                    missing_node,
                    null_mut(),
                ),
                Error::InvalidArgument.code()
            );
            nm_patricia_bulk_state_free(state);
            let state = nm_patricia_bulk_state_new(&EMPTY_TREE_HASH, null());
            assert_eq!(
                nm_patricia_bulk_state_finish(
                    state,
                    null_mut(),
                    0,
                    null(),
                    0,
                    null(),
                    &*buffer,
                    null(),
                    missing_node,
                    null_mut(),
                    null_mut(),
                ),
                Error::InvalidArgument.code()
            );
            assert_eq!(
                nm_patricia_bulk_state_update(
                    state,
                    null_mut(),
                    0,
                    null(),
                    0,
                    &*buffer,
                    null(),
                    missing_node,
                    null_mut(),
                ),
                Error::InvalidArgument.code()
            );
            nm_patricia_bulk_state_free(state);
        }
    }
}
