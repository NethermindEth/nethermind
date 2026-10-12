//! FlatDB core: the storage logic behind the Rust flat-state backend of Nethermind.
//!
//! Everything here is plain Rust; the C ABI lives in the `flatdb-ffi` crate. Unsafe code is confined to the RocksDB
//! backend in [`kv::rocks`], which drives the RocksDB instance the C# side opened through its C API.
#![deny(unsafe_op_in_unsafe_fn)]

pub mod kv;

/// Version of the C ABI shared with the C# adapter (`Nethermind.State.Flat.Rust`), including the
/// [`kv::rocks_api::RocksApi`] table layout. Bump it on any change to a `repr(C)` type or an export signature.
pub const ABI_VERSION: u32 = 1;
