// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

//! Patricia trie bulk set over a path-keyed node store, exposed to Nethermind via FFI.
//!
//! Every node referenced by hash is stored at its `TreePath` as `<tag>[<bitset>]<rlp>`, see [`node`].

mod bulk_set;
mod keccak;
mod node;

use std::ffi::c_void;
use std::panic::{catch_unwind, AssertUnwindSafe};

pub use bulk_set::{bulk_set, EMPTY_TREE_HASH};
pub use node::TreePath;

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
    /// Replaces `buffer` with the node stored at `path`. Returns `false` when there is none.
    fn fetch(&self, path: &TreePath, buffer: &mut Vec<u8>) -> Result<bool, Error>;

    /// Stores `data` at `path`. Empty `data` removes the node.
    fn write(&self, path: &TreePath, data: &[u8]);
}

/// Returns the length of the node at `path`, 0 when missing, or a negative value on failure.
/// A length greater than `buffer_length` means nothing was copied and the buffer must grow.
pub type FetchFn = unsafe extern "C" fn(
    context: *mut c_void,
    path: *const TreePath,
    buffer: *mut u8,
    buffer_length: i32,
) -> i32;

pub type WriteFn =
    unsafe extern "C" fn(context: *mut c_void, path: *const TreePath, data: *const u8, length: i32);

struct CallbackStore {
    fetch: FetchFn,
    write: WriteFn,
    context: *mut c_void,
}

// The callbacks are invoked from rayon workers; the caller guarantees they are thread safe.
unsafe impl Sync for CallbackStore {}

impl NodeStore for CallbackStore {
    fn fetch(&self, path: &TreePath, buffer: &mut Vec<u8>) -> Result<bool, Error> {
        buffer.clear();
        buffer.reserve(1024);
        loop {
            let capacity = buffer.capacity();
            let length =
                unsafe { (self.fetch)(self.context, path, buffer.as_mut_ptr(), capacity as i32) };
            if length < 0 {
                return Err(Error::Callback);
            }
            if length == 0 {
                return Ok(false);
            }
            if length as usize <= capacity {
                unsafe { buffer.set_len(length as usize) };
                return Ok(true);
            }
            buffer.reserve(length as usize);
        }
    }

    fn write(&self, path: &TreePath, data: &[u8]) {
        let data_pointer = if data.is_empty() {
            std::ptr::null()
        } else {
            data.as_ptr()
        };
        unsafe { (self.write)(self.context, path, data_pointer, data.len() as i32) }
    }
}

/// Applies `entries` to the trie whose root is stored at the empty path and writes the root hash to `root_out`.
/// `entries` is reordered in place. Returns 0 on success, -1 on panic, or an [`Error::code`].
///
/// # Safety
/// `entries`, `values` and `root_out` must be valid for the given lengths (32 bytes for `root_out`),
/// and `fetch` / `write` must be callable concurrently with `context`.
#[no_mangle]
pub unsafe extern "C" fn nm_patricia_bulk_set(
    entries: *mut Entry,
    entry_count: i32,
    values: *const u8,
    values_length: i32,
    fetch: FetchFn,
    write: WriteFn,
    context: *mut c_void,
    root_out: *mut u8,
) -> i32 {
    if entry_count < 0 || values_length < 0 || root_out.is_null() {
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
            &CallbackStore {
                fetch,
                write,
                context,
            },
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
