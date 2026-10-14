//! The long-finality persisted-snapshot tier: port of `Nethermind.State.Flat/PersistedSnapshots/`.
//!
//! A persisted snapshot is one [`sorted`] table of fully materialized [`keys`] mapped to small inline values; trie-node
//! RLP lives in blob files and the table holds a [`page_layout::NodeRef`] to it. [`builder`] writes a table from an
//! in-memory snapshot, [`merger`] merges several tables into one, [`reader`] and [`scanner`] read them back, and
//! [`stack`] probes a list of them newest first behind their blooms ([`bloom_keys`]). [`codec`] holds the catalog
//! records. Every byte written is identical to what the C# tier writes for the same input.
//!
//! The readers and writers work over plain byte slices and sinks ([`io`]), so the same code serves memory-mapped arena
//! files and the in-memory buffers of the tests.

pub mod bloom_keys;
pub mod builder;
pub mod codec;
pub mod io;
pub mod keys;
pub mod list;
pub mod merger;
pub mod page_layout;
pub mod reader;
pub mod scanner;
pub mod sorted;
pub mod stack;
pub mod storage;

use thiserror::Error;

/// A failure of the persisted tier, named after the C# exception it stands for.
#[derive(Debug, Error)]
pub enum TierError {
    /// `IOException`: a file of the tier could not be read or written.
    #[error(transparent)]
    Io(#[from] std::io::Error),
    /// `InvalidOperationException` / `InvalidDataException` thrown on bytes no writer produced; the message is the C#
    /// one, which tells the operator to wipe the tier and resync.
    #[error("{0}")]
    Corrupt(String),
    /// `InvalidOperationException`: an operation the state of the tier does not allow.
    #[error("{0}")]
    InvalidOperation(String),
    /// `ArgumentException`: an argument the caller should never pass, such as keys out of order.
    #[error("{0}")]
    InvalidArgument(String),
}

pub type TierResult<T> = Result<T, TierError>;
