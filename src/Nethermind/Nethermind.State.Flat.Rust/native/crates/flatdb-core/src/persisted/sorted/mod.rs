//! Port of `Nethermind.State.Flat/PersistedSnapshots/Sorted/`: the two-level sorted table a persisted snapshot is
//! stored as, an ascending byte-sorted map of keys to small inline values.
//!
//! Layout (offsets relative to the table start):
//! ```text
//! data block × M   block i at i·4096; blocks 0..M-2 zero-padded to 4096, the last one not padded
//! index block      right after the last data block; key = separator, value = the data block's offset (u48)
//! footer (9 B)     [indexOffset i64 LE][version u8 = 1]
//! ```
//! A lookup is a ceiling seek of the index then of the data block it points to; a full scan walks the index in order.

pub mod block;
pub mod block_builder;
pub mod data_block_reader;
pub mod enumerator;
pub mod index_block_reader;
pub mod table;
pub mod table_builder;
pub mod table_reader;

/// Keys and values are at most this long; the readers' key buffers have this size.
pub const KEY_BUFFER_LENGTH: usize = 256;
