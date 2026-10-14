// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

//! Patricia trie bulk set over a path-keyed node store, exposed to Nethermind via FFI.
//!
//! Every node referenced by hash is stored at its `TreePath` as its RLP. Bulk sets write new nodes into a pooled
//! [`PendingNodes`] the caller publishes from.

mod bulk_set;
mod keccak;
mod node;
mod pending;

use std::ffi::c_void;
use std::panic::{catch_unwind, AssertUnwindSafe};

pub use bulk_set::{bulk_set, EMPTY_TREE_HASH};
pub use node::TreePath;
use node::{decode_rlp, Node};
pub use pending::{PendingNodes, WrittenNode};

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
    /// Returns the node at `path` whose hash is `hash`, or `None` when there is none.
    fn fetch(&self, path: &TreePath, hash: &[u8; 32]) -> Result<Option<Node>, Error>;
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
    fn fetch(&self, path: &TreePath, hash: &[u8; 32]) -> Result<Option<Node>, Error> {
        let mut decoded: Option<Result<Node, Error>> = None;
        let status = unsafe {
            (self.fetch)(
                self.context,
                path,
                hash,
                receive,
                &mut decoded as *mut Option<Result<Node, Error>> as *mut c_void,
            )
        };
        match status {
            0 => Ok(None),
            1 => decoded.ok_or(Error::Callback)?.map(Some),
            _ => Err(Error::Callback),
        }
    }
}

unsafe extern "C" fn receive(context: *mut c_void, rlp: *const u8, length: i32) {
    let rlp: &[u8] = if length <= 0 {
        &[]
    } else {
        std::slice::from_raw_parts(rlp, length as usize)
    };
    let decoded = catch_unwind(|| decode_rlp(rlp)).unwrap_or(Err(Error::CorruptNode));
    *(context as *mut Option<Result<Node, Error>>) = Some(decoded);
}

/// Applies `entries` to the trie whose root hash is `root`, writing new nodes into `pending`, and writes the new root
/// hash to `root_out`. Nodes are read from `pending` before `fetch`. `entries` is reordered in place. Returns 0 on
/// success, -1 on panic, or an [`Error::code`].
///
/// # Safety
/// `entries`, `values`, `root` and `root_out` must be valid for the given lengths (32 bytes for `root` and
/// `root_out`), `pending` must come from [`nm_patricia_pending_rent`], and `fetch` must be callable concurrently with
/// `context`.
#[no_mangle]
pub unsafe extern "C" fn nm_patricia_bulk_set(
    entries: *mut Entry,
    entry_count: i32,
    values: *const u8,
    values_length: i32,
    root: *const [u8; 32],
    pending: *const PendingNodes,
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

/// Points `nodes` at the written nodes and `rlp` at the arena their RLP offsets point into, and returns their count.
/// Both stay valid until the next bulk set, clear or return of `pending`.
///
/// # Safety
/// `pending` must come from [`nm_patricia_pending_rent`], and `nodes` and `rlp` must be writable.
#[no_mangle]
pub unsafe extern "C" fn nm_patricia_pending_view(
    pending: *const PendingNodes,
    nodes: *mut *const WrittenNode,
    rlp: *mut *const u8,
) -> i32 {
    (*pending).with_view(|written, arena| {
        *nodes = written.as_ptr();
        *rlp = arena.as_ptr();
        written.len() as i32
    })
}
