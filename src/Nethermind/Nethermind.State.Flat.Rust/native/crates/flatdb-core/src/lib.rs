//! FlatDB core: the storage logic behind the Rust flat-state backend of Nethermind.
//!
//! Everything here is plain Rust; the C ABI lives in the `flatdb-ffi` crate. Unsafe code is confined to the store
//! backends that call foreign code ([`kv::rocks`], which drives the RocksDB instance the C# side opened through its C
//! API, and [`kv::managed`]) and to the native-memory slot table of [`persistence::slot_table`].
#![deny(unsafe_op_in_unsafe_fn)]

pub mod codec;
pub mod keccak;
pub mod kv;
pub mod persistence;
pub mod types;

/// Version of the C ABI shared with the C# adapter (`Nethermind.State.Flat.Rust`), including the
/// [`kv::rocks_api::RocksApi`] table layout. Bump it on any change to a `repr(C)` type or an export signature.
pub const ABI_VERSION: u32 = 2;
