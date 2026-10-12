//! The persisted half of the flat DB: port of `Nethermind.State.Flat/Persistence/`.
//!
//! [`Persistence`] mirrors `IPersistence`. [`layouts::KvPersistence`] implements it for every `FlatLayout` over a
//! [`KvStore`](crate::kv::KvStore), from the codecs of [`base`], [`flat`] and [`trie`]; [`cached_reader`] and
//! [`carry_forward`] are the decorators `FlatWorldStateModule` stacks on top of it.
//!
//! Deliberate deviation: C# never reads the status of a RocksDB iterator (`RocksdbSortedView`,
//! `DbOnTheRocks.GetEdgeKey`), so a read error ends a scan as if its range were exhausted, and a self-destruct or range
//! delete then commits having deleted only part of its range. Every scan here returns the error instead.

pub mod base;
pub mod cached_reader;
pub mod carry_forward;
pub mod flat;
pub mod layouts;
pub mod slot_table;
pub mod trie;
pub mod write_buffer;

use std::sync::Arc;

use thiserror::Error;

use crate::codec::rlp::RlpError;
use crate::kv::{KvError, ReadFlags, WriteFlags};
use crate::types::{Account, Address, Hash256, StateId, TreePath, U256};

/// What a reader is for, with the bit values of `ReaderFlags`.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash)]
pub struct ReaderFlags(u32);

impl ReaderFlags {
    pub const NONE: Self = Self(0);
    /// The reader serves sync; the reader caches are bypassed.
    pub const SYNC: Self = Self(1);
    /// The reader serves long sequential scans: the snapshot reads ahead for [`ReadFlags::HINT_READ_AHEAD`] reads,
    /// and the shared reader of [`cached_reader::CachedReaderPersistence`] is bypassed.
    pub const FULL_SCAN: Self = Self(2);

    pub const fn from_bits(bits: u32) -> Self {
        Self(bits)
    }

    pub const fn bits(self) -> u32 {
        self.0
    }

    pub const fn contains(self, other: Self) -> bool {
        self.0 & other.0 == other.0
    }

    pub const fn intersects(self, other: Self) -> bool {
        self.0 & other.0 != 0
    }
}

impl std::ops::BitOr for ReaderFlags {
    type Output = Self;

    fn bitor(self, rhs: Self) -> Self {
        Self(self.0 | rhs.0)
    }
}

/// Failure of a persistence operation, one variant per exception type the C# persistence throws.
#[derive(Debug, Error, Clone, PartialEq, Eq)]
pub enum PersistenceError {
    #[error(transparent)]
    Kv(#[from] KvError),
    /// Malformed RLP in the database (`RlpException`).
    #[error(transparent)]
    Rlp(#[from] RlpError),
    /// The database does not match the configuration or is corrupt (`InvalidConfigurationException`).
    #[error("{0}")]
    InvalidConfiguration(String),
    /// The operation is not available in this mode (`NotSupportedException`).
    #[error("{0}")]
    NotSupported(&'static str),
    /// The operation is not available in this mode (`InvalidOperationException`).
    #[error("{0}")]
    InvalidOperation(String),
    /// A batch does not start at the persisted state (the `InvalidOperationException` of `CreateWriteBatch`).
    #[error("Attempted to apply snapshot on top of wrong state. Snapshot from: {from:?}, Db state: {current:?}")]
    WrongBaseState { from: StateId, current: StateId },
    /// A stored value is longer than the buffer it is read into (the `ArgumentException` of a span `Get`).
    #[error("The value is {len} bytes but the destination holds {capacity}.")]
    ValueTooLarge { len: usize, capacity: usize },
}

pub type PersistenceResult<T> = Result<T, PersistenceError>;

/// Port of `IPersistence`: the persisted flat state and trie nodes.
pub trait Persistence: Send + Sync + 'static {
    type Reader: PersistenceReader;
    type WriteBatch: PersistenceWriteBatch;

    fn create_reader(&self, flags: ReaderFlags) -> PersistenceResult<Self::Reader>;

    /// Starts a batch moving the persisted state from `from` to `to`. Unless either is [`StateId::SYNC`], `from` must
    /// be the persisted state.
    fn create_write_batch(
        &self,
        from: &StateId,
        to: &StateId,
        flags: WriteFlags,
    ) -> PersistenceResult<Self::WriteBatch>;

    /// Flushes everything to disk; batches written with the write-ahead log already are durable.
    fn flush(&self) -> PersistenceResult<()>;

    /// Wipes the state for a state sync, keeping the format markers.
    fn clear(&self) -> PersistenceResult<()>;
}

/// Port of `IPersistence.IPersistenceReader`: reads at the state the reader was opened at. Dropping it disposes it.
pub trait PersistenceReader: Send + Sync + 'static {
    type Iter: FlatIterator;

    fn get_account(&self, address: &Address) -> PersistenceResult<Option<Account>>;

    /// The slot value, which can be zero for a slot that is present: zero and absent are distinct.
    fn try_get_slot(&self, address: &Address, slot: &U256) -> PersistenceResult<Option<U256>>;

    /// The persisted state the reader sees.
    fn current_state(&self) -> StateId;

    /// Calls `read` with the RLP of the state trie node at `path`.
    fn load_state_rlp<R>(
        &self,
        path: &TreePath,
        flags: ReadFlags,
        read: impl FnOnce(&[u8]) -> R,
    ) -> PersistenceResult<Option<R>>;

    /// Calls `read` with the RLP of the node at `path` of the storage trie of the account hashing to `address_hash`.
    fn load_storage_rlp<R>(
        &self,
        address_hash: &Hash256,
        path: &TreePath,
        flags: ReadFlags,
        read: impl FnOnce(&[u8]) -> R,
    ) -> PersistenceResult<Option<R>>;

    fn try_load_state_rlp(&self, path: &TreePath, flags: ReadFlags) -> PersistenceResult<Option<Vec<u8>>> {
        self.load_state_rlp(path, flags, <[u8]>::to_vec)
    }

    fn try_load_storage_rlp(
        &self,
        address_hash: &Hash256,
        path: &TreePath,
        flags: ReadFlags,
    ) -> PersistenceResult<Option<Vec<u8>>> {
        self.load_storage_rlp(address_hash, path, flags, <[u8]>::to_vec)
    }

    /// The stored account value of the account hashing to `address_hash`, for the importer.
    fn get_account_raw(&self, address_hash: &Hash256) -> PersistenceResult<Option<Vec<u8>>>;

    fn try_get_storage_raw(&self, address_hash: &Hash256, slot_hash: &Hash256) -> PersistenceResult<Option<U256>>;

    /// Iterates the accounts with keys in `[start_key, end_key)`, compared on their stored 20 bytes.
    fn create_account_iterator(&self, start_key: &Hash256, end_key: &Hash256) -> PersistenceResult<Self::Iter>;

    /// Iterates the slots of `account_key` with keys in `[start_slot_key, end_slot_key]`.
    fn create_storage_iterator(
        &self,
        account_key: &Hash256,
        start_slot_key: &Hash256,
        end_slot_key: &Hash256,
    ) -> PersistenceResult<Self::Iter>;

    fn is_preimage_mode(&self) -> bool;
}

/// Port of `IPersistence.IWriteBatch`. [`PersistenceWriteBatch::commit`] is its `Dispose`; dropping a batch without
/// committing discards what was not written out yet.
pub trait PersistenceWriteBatch: Send + Sized + 'static {
    fn self_destruct(&mut self, address: &Address) -> PersistenceResult<()>;
    fn set_account(&mut self, address: &Address, account: Option<&Account>) -> PersistenceResult<()>;
    fn set_storage(&mut self, address: &Address, slot: &U256, value: Option<&U256>) -> PersistenceResult<()>;
    fn set_state_trie_node(&mut self, path: &TreePath, rlp: &[u8]) -> PersistenceResult<()>;
    fn set_storage_trie_node(&mut self, address_hash: &Hash256, path: &TreePath, rlp: &[u8]) -> PersistenceResult<()>;

    /// Writes a slot whose value is already the trie-leaf RLP byte string (`RLP(stripped)`), as sync produces it.
    fn set_storage_raw_encoded(
        &mut self,
        address_hash: &Hash256,
        slot_hash: &Hash256,
        rlp_value: &[u8],
    ) -> PersistenceResult<()>;

    fn set_account_raw(&mut self, address_hash: &Hash256, account: &Account) -> PersistenceResult<()>;
    fn delete_account_range(&mut self, from_path: &Hash256, to_path: &Hash256) -> PersistenceResult<()>;
    fn delete_storage_range(
        &mut self,
        address_hash: &Hash256,
        from_path: &Hash256,
        to_path: &Hash256,
    ) -> PersistenceResult<()>;

    /// Deletes every state trie node whose whole subtree lies in `[from, to]`; an ancestor of the range is kept.
    fn delete_state_trie_node_range(&mut self, from: &Hash256, to: &Hash256) -> PersistenceResult<()>;

    /// As [`PersistenceWriteBatch::delete_state_trie_node_range`], in the storage trie of `address_hash`.
    fn delete_storage_trie_node_range(
        &mut self,
        address_hash: &Hash256,
        from: &Hash256,
        to: &Hash256,
    ) -> PersistenceResult<()>;

    fn commit(self) -> PersistenceResult<()>;
}

/// Port of `IPersistence.IFlatIterator`, the iterator of flat entries `verifytrie` and the importer walk.
pub trait FlatIterator: Send + 'static {
    fn move_next(&mut self) -> PersistenceResult<bool>;

    /// The key of the current entry, zero-padded to 32 bytes.
    fn current_key(&self) -> Hash256;

    /// The value of the current entry; valid until the next move.
    fn current_value(&self) -> &[u8];
}

impl<R: PersistenceReader> PersistenceReader for Arc<R> {
    type Iter = R::Iter;

    fn get_account(&self, address: &Address) -> PersistenceResult<Option<Account>> {
        (**self).get_account(address)
    }

    fn try_get_slot(&self, address: &Address, slot: &U256) -> PersistenceResult<Option<U256>> {
        (**self).try_get_slot(address, slot)
    }

    fn current_state(&self) -> StateId {
        (**self).current_state()
    }

    fn load_state_rlp<T>(
        &self,
        path: &TreePath,
        flags: ReadFlags,
        read: impl FnOnce(&[u8]) -> T,
    ) -> PersistenceResult<Option<T>> {
        (**self).load_state_rlp(path, flags, read)
    }

    fn load_storage_rlp<T>(
        &self,
        address_hash: &Hash256,
        path: &TreePath,
        flags: ReadFlags,
        read: impl FnOnce(&[u8]) -> T,
    ) -> PersistenceResult<Option<T>> {
        (**self).load_storage_rlp(address_hash, path, flags, read)
    }

    fn get_account_raw(&self, address_hash: &Hash256) -> PersistenceResult<Option<Vec<u8>>> {
        (**self).get_account_raw(address_hash)
    }

    fn try_get_storage_raw(&self, address_hash: &Hash256, slot_hash: &Hash256) -> PersistenceResult<Option<U256>> {
        (**self).try_get_storage_raw(address_hash, slot_hash)
    }

    fn create_account_iterator(&self, start_key: &Hash256, end_key: &Hash256) -> PersistenceResult<R::Iter> {
        (**self).create_account_iterator(start_key, end_key)
    }

    fn create_storage_iterator(
        &self,
        account_key: &Hash256,
        start_slot_key: &Hash256,
        end_slot_key: &Hash256,
    ) -> PersistenceResult<R::Iter> {
        (**self).create_storage_iterator(account_key, start_slot_key, end_slot_key)
    }

    fn is_preimage_mode(&self) -> bool {
        (**self).is_preimage_mode()
    }
}

/// Port of `NoopPersistenceReader`: a reader of an empty state at block 0.
pub struct NoopPersistenceReader;

/// An iterator without entries.
pub struct EmptyIterator;

impl FlatIterator for EmptyIterator {
    fn move_next(&mut self) -> PersistenceResult<bool> {
        Ok(false)
    }

    fn current_key(&self) -> Hash256 {
        [0; 32]
    }

    fn current_value(&self) -> &[u8] {
        &[]
    }
}

impl PersistenceReader for NoopPersistenceReader {
    type Iter = EmptyIterator;

    fn get_account(&self, _address: &Address) -> PersistenceResult<Option<Account>> {
        Ok(None)
    }

    fn try_get_slot(&self, _address: &Address, _slot: &U256) -> PersistenceResult<Option<U256>> {
        Ok(None)
    }

    fn current_state(&self) -> StateId {
        StateId::new(0, crate::types::EMPTY_TREE_HASH)
    }

    fn load_state_rlp<T>(
        &self,
        _path: &TreePath,
        _flags: ReadFlags,
        _read: impl FnOnce(&[u8]) -> T,
    ) -> PersistenceResult<Option<T>> {
        Ok(None)
    }

    fn load_storage_rlp<T>(
        &self,
        _address_hash: &Hash256,
        _path: &TreePath,
        _flags: ReadFlags,
        _read: impl FnOnce(&[u8]) -> T,
    ) -> PersistenceResult<Option<T>> {
        Ok(None)
    }

    fn get_account_raw(&self, _address_hash: &Hash256) -> PersistenceResult<Option<Vec<u8>>> {
        Ok(None)
    }

    fn try_get_storage_raw(&self, _address_hash: &Hash256, _slot_hash: &Hash256) -> PersistenceResult<Option<U256>> {
        Ok(None)
    }

    fn create_account_iterator(&self, _start_key: &Hash256, _end_key: &Hash256) -> PersistenceResult<EmptyIterator> {
        Ok(EmptyIterator)
    }

    fn create_storage_iterator(
        &self,
        _account_key: &Hash256,
        _start_slot_key: &Hash256,
        _end_slot_key: &Hash256,
    ) -> PersistenceResult<EmptyIterator> {
        Ok(EmptyIterator)
    }

    fn is_preimage_mode(&self) -> bool {
        false
    }
}
