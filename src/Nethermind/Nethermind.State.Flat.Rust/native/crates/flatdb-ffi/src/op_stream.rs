//! The op stream of `fdb_batch_apply`: the `IPersistence.IWriteBatch` calls C# buffers and hands over a chunk at a
//! time, mirrored by `Nethermind.State.Flat.Rust/RustWriteBatch.cs`.
//!
//! Each operation is a tag byte followed by its fields, packed without padding; integers are little-endian:
//! - an address is 20 bytes, a hash 32;
//! - a `UInt256` is its four 64-bit limbs, least significant first, as `UInt256` holds them;
//! - an account is its nonce (`u64`), balance (`UInt256`), storage root and code hash;
//! - a trie path is its 32 path bytes and a length byte (0 to 64);
//! - a byte string is a `u32` length and the bytes.

use flatdb_core::persistence::PersistenceWriteBatch;
use flatdb_core::types::{Account, Address, Hash256, TreePath, U256};

use crate::abi::FfiError;

/// `SelfDestruct(address)`.
pub const SELF_DESTRUCT: u8 = 0;
/// `SetAccount(address, account)` with an account.
pub const SET_ACCOUNT: u8 = 1;
/// `SetAccount(address, null)`.
pub const REMOVE_ACCOUNT: u8 = 2;
/// `SetStorage(address, slot, value)` with a value.
pub const SET_STORAGE: u8 = 3;
/// `SetStorage(address, slot, null)`.
pub const REMOVE_STORAGE: u8 = 4;
/// `SetStateTrieNode(path, rlp)`.
pub const SET_STATE_TRIE_NODE: u8 = 5;
/// `SetStorageTrieNode(addressHash, path, rlp)`.
pub const SET_STORAGE_TRIE_NODE: u8 = 6;
/// `SetStorageRawEncoded(addressHash, slotHash, rlpValue)`.
pub const SET_STORAGE_RAW_ENCODED: u8 = 7;
/// `SetAccountRaw(addressHash, account)`.
pub const SET_ACCOUNT_RAW: u8 = 8;
/// `DeleteAccountRange(fromPath, toPath)`.
pub const DELETE_ACCOUNT_RANGE: u8 = 9;
/// `DeleteStorageRange(addressHash, fromPath, toPath)`.
pub const DELETE_STORAGE_RANGE: u8 = 10;
/// `DeleteStateTrieNodeRange(from, to)`.
pub const DELETE_STATE_TRIE_NODE_RANGE: u8 = 11;
/// `DeleteStorageTrieNodeRange(addressHash, from, to)`.
pub const DELETE_STORAGE_TRIE_NODE_RANGE: u8 = 12;

/// Applies every operation of `ops` to `batch`, in order. An operation that fails stops the stream; the ones before
/// it stay applied, as they would have been by the C# calls.
pub fn apply(batch: &mut impl PersistenceWriteBatch, ops: &[u8]) -> Result<(), FfiError> {
    let mut reader = Reader::new(ops);
    let mut index = 0usize;
    while !reader.is_done() {
        let start = reader.position;
        apply_one(batch, &mut reader).map_err(|error| FfiError {
            status: error.status,
            message: format!("{} (operation {index} at byte {start})", error.message),
        })?;
        index += 1;
    }
    Ok(())
}

fn apply_one(batch: &mut impl PersistenceWriteBatch, reader: &mut Reader<'_>) -> Result<(), FfiError> {
    match reader.u8()? {
        SELF_DESTRUCT => batch.self_destruct(&reader.address()?)?,
        SET_ACCOUNT => {
            let address = reader.address()?;
            batch.set_account(&address, Some(&reader.account()?))?;
        }
        REMOVE_ACCOUNT => batch.set_account(&reader.address()?, None)?,
        SET_STORAGE => {
            let (address, slot, value) = (reader.address()?, reader.u256()?, reader.u256()?);
            batch.set_storage(&address, &slot, Some(&value))?;
        }
        REMOVE_STORAGE => {
            let (address, slot) = (reader.address()?, reader.u256()?);
            batch.set_storage(&address, &slot, None)?;
        }
        SET_STATE_TRIE_NODE => {
            let path = reader.path()?;
            batch.set_state_trie_node(&path, reader.bytes()?)?;
        }
        SET_STORAGE_TRIE_NODE => {
            let (address_hash, path) = (reader.hash()?, reader.path()?);
            batch.set_storage_trie_node(&address_hash, &path, reader.bytes()?)?;
        }
        SET_STORAGE_RAW_ENCODED => {
            let (address_hash, slot_hash) = (reader.hash()?, reader.hash()?);
            batch.set_storage_raw_encoded(&address_hash, &slot_hash, reader.bytes()?)?;
        }
        SET_ACCOUNT_RAW => {
            let address_hash = reader.hash()?;
            batch.set_account_raw(&address_hash, &reader.account()?)?;
        }
        DELETE_ACCOUNT_RANGE => {
            let (from, to) = (reader.hash()?, reader.hash()?);
            batch.delete_account_range(&from, &to)?;
        }
        DELETE_STORAGE_RANGE => {
            let (address_hash, from, to) = (reader.hash()?, reader.hash()?, reader.hash()?);
            batch.delete_storage_range(&address_hash, &from, &to)?;
        }
        DELETE_STATE_TRIE_NODE_RANGE => {
            let (from, to) = (reader.hash()?, reader.hash()?);
            batch.delete_state_trie_node_range(&from, &to)?;
        }
        DELETE_STORAGE_TRIE_NODE_RANGE => {
            let (address_hash, from, to) = (reader.hash()?, reader.hash()?, reader.hash()?);
            batch.delete_storage_trie_node_range(&address_hash, &from, &to)?;
        }
        tag => return Err(FfiError::invalid_argument(format!("unknown operation tag {tag}"))),
    }
    Ok(())
}

/// Reads the fields of a packed stream, shared with [`crate::snapshot_stream`].
pub(crate) struct Reader<'a> {
    ops: &'a [u8],
    pub(crate) position: usize,
}

impl<'a> Reader<'a> {
    pub(crate) fn new(ops: &'a [u8]) -> Self {
        Self { ops, position: 0 }
    }

    pub(crate) fn is_done(&self) -> bool {
        self.position >= self.ops.len()
    }

    fn take(&mut self, count: usize) -> Result<&'a [u8], FfiError> {
        let end = self
            .position
            .checked_add(count)
            .filter(|&end| end <= self.ops.len())
            .ok_or_else(|| FfiError::invalid_argument("the operation is truncated"))?;
        let taken = &self.ops[self.position..end];
        self.position = end;
        Ok(taken)
    }

    fn array<const N: usize>(&mut self) -> Result<[u8; N], FfiError> {
        Ok(self.take(N)?.try_into().expect("took N bytes"))
    }

    pub(crate) fn u8(&mut self) -> Result<u8, FfiError> {
        Ok(self.array::<1>()?[0])
    }

    pub(crate) fn u64(&mut self) -> Result<u64, FfiError> {
        Ok(u64::from_le_bytes(self.array()?))
    }

    pub(crate) fn address(&mut self) -> Result<Address, FfiError> {
        self.array()
    }

    pub(crate) fn hash(&mut self) -> Result<Hash256, FfiError> {
        self.array()
    }

    pub(crate) fn u256(&mut self) -> Result<U256, FfiError> {
        Ok(U256([self.u64()?, self.u64()?, self.u64()?, self.u64()?]))
    }

    pub(crate) fn account(&mut self) -> Result<Account, FfiError> {
        Ok(Account::with_hashes(
            self.u64()?,
            self.u256()?,
            self.hash()?,
            self.hash()?,
        ))
    }

    pub(crate) fn path(&mut self) -> Result<TreePath, FfiError> {
        let path = self.hash()?;
        let length = self.u8()?;
        if length > TreePath::MAX_LENGTH {
            return Err(FfiError::invalid_argument(format!("path length {length}")));
        }
        Ok(TreePath::new(path, length))
    }

    pub(crate) fn bytes(&mut self) -> Result<&'a [u8], FfiError> {
        let length = u32::from_le_bytes(self.array()?);
        self.take(length as usize)
    }
}

/// Encodes op streams as C# does, for the tests.
#[cfg(test)]
pub mod writer {
    use flatdb_core::types::{Account, Address, Hash256, TreePath, U256};

    #[derive(Default)]
    pub struct OpWriter(pub Vec<u8>);

    impl OpWriter {
        pub fn op(&mut self, tag: u8) -> &mut Self {
            self.0.push(tag);
            self
        }

        pub fn byte(&mut self, value: u8) -> &mut Self {
            self.0.push(value);
            self
        }

        pub fn address(&mut self, address: &Address) -> &mut Self {
            self.0.extend_from_slice(address);
            self
        }

        pub fn hash(&mut self, hash: &Hash256) -> &mut Self {
            self.0.extend_from_slice(hash);
            self
        }

        pub fn u256(&mut self, value: &U256) -> &mut Self {
            for limb in value.0 {
                self.0.extend_from_slice(&limb.to_le_bytes());
            }
            self
        }

        pub fn account(&mut self, account: &Account) -> &mut Self {
            self.0.extend_from_slice(&account.nonce.to_le_bytes());
            self.u256(&account.balance)
                .hash(&account.storage_root)
                .hash(&account.code_hash)
        }

        pub fn path(&mut self, path: &TreePath) -> &mut Self {
            self.hash(&path.path);
            self.0.push(path.length);
            self
        }

        pub fn bytes(&mut self, bytes: &[u8]) -> &mut Self {
            self.0.extend_from_slice(&(bytes.len() as u32).to_le_bytes());
            self.0.extend_from_slice(bytes);
            self
        }
    }
}
