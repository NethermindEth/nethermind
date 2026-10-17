// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

//! Patricia trie bulk set over a path-keyed node store, exposed to Nethermind via FFI.
//!
//! Every node referenced by hash is stored at its `TreePath` as its RLP. Bulk sets write new nodes into a pooled
//! [`PendingNodes`] the caller publishes from, and keep decoded nodes in a shared [`TrieNodeLog`].

mod bulk_set;
mod keccak;
mod log;
mod metrics;
mod node;
mod pending;
mod warmed;

use std::ffi::c_void;
use std::panic::{catch_unwind, AssertUnwindSafe};

pub use bulk_set::{bulk_set, BulkSetState, EMPTY_TREE_HASH};
pub use log::TrieNodeLog;
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
/// hash to `root_out`. Nodes written also go into `log`, or a temporary log when null, the new root indexed as that of
/// the trie of `address`, the state trie when null. Nodes reachable through that root in `log` are read there, and the
/// others from `pending`, then `warmed` unless null, then `fetch`. `entries` is reordered in place.
/// Returns 0 on success, -1 on panic, or an [`Error::code`].
///
/// # Safety
/// `entries`, `values`, `root`, `address` and `root_out` must be valid for the given lengths (32 bytes for `root`,
/// `address` and `root_out`), `pending` must come from [`nm_patricia_pending_rent`], `log` must be null or come
/// from [`nm_patricia_log_new`], `warmed` must be null or come from [`nm_patricia_warmed_new`] and not be reset
/// during the call, and `fetch` must be callable concurrently with `context`.
#[no_mangle]
pub unsafe extern "C" fn nm_patricia_bulk_set(
    entries: *mut Entry,
    entry_count: i32,
    values: *const u8,
    values_length: i32,
    root: *const [u8; 32],
    pending: *const PendingNodes,
    log: *const TrieNodeLog,
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
            log.as_ref(),
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
/// a panic. The root and optional address are copied; no node store or log is retained.
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
/// `log` must come from [`nm_patricia_log_new`], be the same log for every call on this state, and not be freed
/// during the call. The caller must retain the base
/// trie's nonpersisted nodes in the log between calls. `warmed` must be null or come from [`nm_patricia_warmed_new`]
/// and not be reset or freed during this call. Nonempty `entries` and `values` must be valid for their lengths;
/// `fetch` and `context` must be valid for the call.
#[no_mangle]
pub unsafe extern "C" fn nm_patricia_bulk_state_update(
    state: *mut BulkSetState,
    entries: *mut Entry,
    entry_count: i32,
    values: *const u8,
    values_length: i32,
    log: *const TrieNodeLog,
    warmed: *const WarmedNodes,
    fetch: FetchFn,
    context: *mut c_void,
) -> i32 {
    let Some(state) = state.as_mut() else {
        return Error::InvalidArgument.code();
    };
    let result = catch_unwind(AssertUnwindSafe(|| {
        let log = log.as_ref().ok_or(Error::InvalidArgument)?;
        let (entries, values) = bulk_state_inputs(entries, entry_count, values, values_length)?;
        state.update(
            &CallbackStore { fetch, context },
            log,
            warmed.as_ref(),
            entries,
            values,
        )
    }));
    bulk_state_status(state, result)
}

/// Applies final corrections, then hashes the complete trie into `pending`, indexes its root in `log`, and writes
/// its hash to `root_out`. The state is invalidated on success or failure, but its allocation still must be freed.
/// Zero correction entries still finish the prepared trie. Returns 0, -1 on panic, or an [`Error::code`].
///
/// # Safety
/// The state, buffers, log, warmed nodes, and callback have the same requirements as
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
    log: *const TrieNodeLog,
    warmed: *const WarmedNodes,
    fetch: FetchFn,
    context: *mut c_void,
    root_out: *mut u8,
) -> i32 {
    let Some(state) = state.as_mut() else {
        return Error::InvalidArgument.code();
    };
    let result = catch_unwind(AssertUnwindSafe(|| {
        let log = log.as_ref().ok_or(Error::InvalidArgument)?;
        let pending = pending.as_ref().ok_or(Error::InvalidArgument)?;
        if root_out.is_null() {
            return Err(Error::InvalidArgument);
        }
        let (entries, values) = bulk_state_inputs(entries, entry_count, values, values_length)?;
        let root = state.finish(
            &CallbackStore { fetch, context },
            pending,
            log,
            warmed.as_ref(),
            entries,
            values,
        )?;
        std::ptr::copy_nonoverlapping(root.as_ptr(), root_out, root.len());
        Ok(())
    }));
    bulk_state_status(state, result)
}

/// Releases an owned trie, whether prepared, finished, or invalidated. No log or node store is needed.
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

/// Creates a [`TrieNodeLog`] whose compacted generations take about `compressed_max_memory` bytes, to free with
/// [`nm_patricia_log_free`].
#[no_mangle]
pub extern "C" fn nm_patricia_log_new(compressed_max_memory: u64) -> *mut TrieNodeLog {
    Box::into_raw(TrieNodeLog::new(compressed_max_memory))
}

/// # Safety
/// `log` must come from [`nm_patricia_log_new`], not be freed yet, and not be in use by a bulk set.
#[no_mangle]
pub unsafe extern "C" fn nm_patricia_log_free(log: *mut TrieNodeLog) {
    drop(Box::from_raw(log));
}

/// Retains the active generation of `log`, and so every later one, for bulk sets until [`nm_patricia_log_release`],
/// and returns it.
///
/// # Safety
/// `log` must come from [`nm_patricia_log_new`] and not be freed yet.
#[no_mangle]
pub unsafe extern "C" fn nm_patricia_log_retain_active(log: *const TrieNodeLog) -> u64 {
    (*log).retain_active()
}

/// Retains `generation` of `log` once more, until [`nm_patricia_log_release`].
///
/// # Safety
/// `log` must come from [`nm_patricia_log_new`] and not be freed yet, and `generation` must be retained.
#[no_mangle]
pub unsafe extern "C" fn nm_patricia_log_retain(log: *const TrieNodeLog, generation: u64) {
    (*log).retain(generation)
}

/// Releases a retain of `generation` of `log`.
///
/// # Safety
/// `log` must come from [`nm_patricia_log_new`] and not be freed yet, and `generation` must be retained.
#[no_mangle]
pub unsafe extern "C" fn nm_patricia_log_release(log: *const TrieNodeLog, generation: u64) {
    (*log).release(generation)
}

/// Warms the path to `key` in the trie of `address`, the state trie when null, with root hash `root`: every node on it
/// that bulk sets cannot read from `log` nor `warmed` is fetched through `fetch` and added to `warmed`.
/// Returns 0 on success, -1 on panic, or an [`Error::code`].
///
/// # Safety
/// `log` must come from [`nm_patricia_log_new`], `warmed` from [`nm_patricia_warmed_new`] and not be reset during the
/// call, `address` must be null or valid for 32 bytes, `root` and `key` must be valid for 32 bytes, and `fetch` must be
/// callable with `context`.
#[no_mangle]
pub unsafe extern "C" fn nm_patricia_warm_up(
    log: *const TrieNodeLog,
    warmed: *const WarmedNodes,
    address: *const [u8; 32],
    root: *const [u8; 32],
    key: *const [u8; 32],
    fetch: FetchFn,
    context: *mut c_void,
) -> i32 {
    match catch_unwind(AssertUnwindSafe(|| {
        warm_up(
            &CallbackStore { fetch, context },
            &*log,
            &*warmed,
            address.as_ref(),
            &*root,
            &*key,
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

/// Writes to `hash_out` the hash of the node whose record is at `pointer` in `log`, points `rlp_out` at its RLP, valid
/// as long as the record, and returns the RLP length, or -1 on panic.
///
/// # Safety
/// `log` must come from [`nm_patricia_log_new`], `pointer` must be a node record of a generation retained, as by
/// [`nm_patricia_log_retain_active`], and `hash_out` and `rlp_out` must be writable.
#[no_mangle]
pub unsafe extern "C" fn nm_patricia_log_node(
    log: *const TrieNodeLog,
    pointer: u64,
    hash_out: *mut [u8; 32],
    rlp_out: *mut *const u8,
) -> i32 {
    match catch_unwind(AssertUnwindSafe(|| (*log).node(pointer))) {
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

/// Writes the process-wide bulk set and trie node log counters to `metrics`.
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
        let log = TrieNodeLog::minimal();
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
                    &*log,
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
                    &*log,
                    null(),
                    missing_node,
                    null_mut(),
                    root.as_mut_ptr(),
                ),
                0
            );
            assert_ne!(log.pin().root(Some(&address), &root), 0);
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
                    &*log,
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
        let log = TrieNodeLog::minimal();
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
                        &*log,
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
                        &*log,
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
                    &*log,
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
                    &*log,
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
                    &*log,
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
                    &*log,
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
