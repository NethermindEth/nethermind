//! FlatDB core: the storage logic behind the Rust flat-state backend of Nethermind.
//!
//! [`persistence`] is the persisted flat state over a [`kv::KvStore`]; [`snapshot`], [`repository`], [`compactor`],
//! [`schedule`] and [`bundle`] are the in-memory snapshots above it and the reads through both, [`scope_bundle`] the
//! content a scope writes on top of them; [`persistence_manager`] and [`manager`] drive them, calling the C# side
//! through [`host`] and reporting through [`stats`], [`events`] and [`log_ring`]. [`seam`] counts what crosses the C
//! ABI, for its cost to be told apart from the work behind it.
//!
//! Everything here is plain Rust; the C ABI lives in the `flatdb-ffi` crate. Unsafe code is confined to the store
//! backends that call foreign code ([`kv::rocks`], which drives the RocksDB instance the C# side opened through its C
//! API, and [`kv::managed`]) and to the native-memory slot table of [`persistence::slot_table`].
#![deny(unsafe_op_in_unsafe_fn)]

pub mod bloom;
pub mod bundle;
pub mod bundle_cache;
pub mod codec;
pub mod compactor;
pub mod events;
pub mod host;
pub mod keccak;
pub mod kv;
pub mod log_ring;
pub mod manager;
pub mod persistence;
pub mod persistence_manager;
pub mod repository;
pub mod schedule;
pub mod scope_bundle;
pub mod seam;
pub mod snapshot;
pub mod stats;
pub mod types;

/// Version of the C ABI shared with the C# adapter (`Nethermind.State.Flat.Rust`), including the
/// [`kv::rocks_api::RocksApi`] table layout. Bump it on any change to a `repr(C)` type or an export signature.
pub const ABI_VERSION: u32 = 5;
