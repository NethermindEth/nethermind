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

pub use bulk_set::{bulk_set, EMPTY_TREE_HASH};
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

/// Creates a [`TrieNodeLog`] of about `max_memory` bytes, to free with [`nm_patricia_log_free`].
#[no_mangle]
pub extern "C" fn nm_patricia_log_new(max_memory: u64) -> *mut TrieNodeLog {
    Box::into_raw(Box::new(TrieNodeLog::new(max_memory)))
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
