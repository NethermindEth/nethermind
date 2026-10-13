//! The persistence stack of a store behind object-safe traits, so one set of exports serves every key-value backend
//! and decorator combination, and the [`Erased`](ErasedPersistence) types putting it back behind the generic traits the
//! snapshot manager runs on. A call through them costs one indirect call, small next to the FFI transition.

use std::sync::Arc;
use std::thread;

use flatdb_core::kv::{GetInto, ReadFlags, WriteFlags};
use flatdb_core::persistence::{
    FlatIterator, Persistence, PersistenceError, PersistenceReader, PersistenceResult, PersistenceWriteBatch,
    ReaderFlags,
};
use flatdb_core::types::{Account, Address, Hash256, StateId, TreePath, U256};

use crate::abi::FfiError;
use crate::guard::Poison;
use crate::op_stream;

/// [`Persistence`], with readers and batches boxed.
pub trait DynPersistence: Send + Sync {
    fn create_reader(&self, flags: ReaderFlags) -> PersistenceResult<Box<dyn DynReader>>;
    fn create_write_batch(
        &self,
        from: &StateId,
        to: &StateId,
        flags: WriteFlags,
    ) -> PersistenceResult<Box<dyn DynWriteBatch>>;
    fn flush(&self) -> PersistenceResult<()>;
    fn clear(&self) -> PersistenceResult<()>;
}

impl<P: Persistence> DynPersistence for P {
    fn create_reader(&self, flags: ReaderFlags) -> PersistenceResult<Box<dyn DynReader>> {
        Ok(Box::new(Persistence::create_reader(self, flags)?))
    }

    fn create_write_batch(
        &self,
        from: &StateId,
        to: &StateId,
        flags: WriteFlags,
    ) -> PersistenceResult<Box<dyn DynWriteBatch>> {
        Ok(Box::new(Persistence::create_write_batch(self, from, to, flags)?))
    }

    fn flush(&self) -> PersistenceResult<()> {
        Persistence::flush(self)
    }

    fn clear(&self) -> PersistenceResult<()> {
        Persistence::clear(self)
    }
}

/// [`PersistenceReader`] with its generic node reads taking a visitor, and iterators boxed.
pub trait DynReader: Send + Sync {
    fn get_account(&self, address: &Address) -> PersistenceResult<Option<Account>>;
    fn try_get_slot(&self, address: &Address, slot: &U256) -> PersistenceResult<Option<U256>>;
    fn current_state(&self) -> StateId;
    /// Calls `read` with the RLP of the state trie node at `path`; whether there is one.
    fn visit_state_rlp(
        &self,
        path: &TreePath,
        flags: ReadFlags,
        read: &mut dyn FnMut(&[u8]),
    ) -> PersistenceResult<bool>;
    /// As [`DynReader::visit_state_rlp`], in the storage trie of the account hashing to `address_hash`.
    fn visit_storage_rlp(
        &self,
        address_hash: &Hash256,
        path: &TreePath,
        flags: ReadFlags,
        read: &mut dyn FnMut(&[u8]),
    ) -> PersistenceResult<bool>;
    fn get_account_raw(&self, address_hash: &Hash256) -> PersistenceResult<Option<Vec<u8>>>;
    fn try_get_storage_raw(&self, address_hash: &Hash256, slot_hash: &Hash256) -> PersistenceResult<Option<U256>>;
    fn create_account_iterator(
        &self,
        start_key: &Hash256,
        end_key: &Hash256,
    ) -> PersistenceResult<Box<dyn FlatIterator>>;
    fn create_storage_iterator(
        &self,
        account_key: &Hash256,
        start_slot_key: &Hash256,
        end_slot_key: &Hash256,
    ) -> PersistenceResult<Box<dyn FlatIterator>>;
    fn is_preimage_mode(&self) -> bool;
}

impl dyn DynReader {
    /// Copies the state trie node at `path` into `buffer` when it fits.
    pub fn load_state_rlp(&self, path: &TreePath, flags: ReadFlags, buffer: &mut [u8]) -> PersistenceResult<GetInto> {
        let mut read = GetInto::NotFound;
        self.visit_state_rlp(path, flags, &mut |value| read = copy_into(value, buffer))?;
        Ok(read)
    }

    /// Copies the storage trie node at `path` into `buffer` when it fits.
    pub fn load_storage_rlp(
        &self,
        address_hash: &Hash256,
        path: &TreePath,
        flags: ReadFlags,
        buffer: &mut [u8],
    ) -> PersistenceResult<GetInto> {
        let mut read = GetInto::NotFound;
        self.visit_storage_rlp(address_hash, path, flags, &mut |value| read = copy_into(value, buffer))?;
        Ok(read)
    }

    /// Copies the stored account value into `buffer` when it fits.
    pub fn get_account_raw_into(&self, address_hash: &Hash256, buffer: &mut [u8]) -> PersistenceResult<GetInto> {
        Ok(self
            .get_account_raw(address_hash)?
            .map_or(GetInto::NotFound, |value| copy_into(&value, buffer)))
    }
}

/// Copies `value` into `buffer` when it fits.
fn copy_into(value: &[u8], buffer: &mut [u8]) -> GetInto {
    match buffer.get_mut(..value.len()) {
        Some(target) => {
            target.copy_from_slice(value);
            GetInto::Found(value.len())
        }
        None => GetInto::BufferTooSmall(value.len()),
    }
}

impl<R: PersistenceReader> DynReader for R {
    fn get_account(&self, address: &Address) -> PersistenceResult<Option<Account>> {
        PersistenceReader::get_account(self, address)
    }

    fn try_get_slot(&self, address: &Address, slot: &U256) -> PersistenceResult<Option<U256>> {
        PersistenceReader::try_get_slot(self, address, slot)
    }

    fn current_state(&self) -> StateId {
        PersistenceReader::current_state(self)
    }

    fn visit_state_rlp(
        &self,
        path: &TreePath,
        flags: ReadFlags,
        read: &mut dyn FnMut(&[u8]),
    ) -> PersistenceResult<bool> {
        Ok(PersistenceReader::load_state_rlp(self, path, flags, read)?.is_some())
    }

    fn visit_storage_rlp(
        &self,
        address_hash: &Hash256,
        path: &TreePath,
        flags: ReadFlags,
        read: &mut dyn FnMut(&[u8]),
    ) -> PersistenceResult<bool> {
        Ok(PersistenceReader::load_storage_rlp(self, address_hash, path, flags, read)?.is_some())
    }

    fn get_account_raw(&self, address_hash: &Hash256) -> PersistenceResult<Option<Vec<u8>>> {
        PersistenceReader::get_account_raw(self, address_hash)
    }

    fn try_get_storage_raw(&self, address_hash: &Hash256, slot_hash: &Hash256) -> PersistenceResult<Option<U256>> {
        PersistenceReader::try_get_storage_raw(self, address_hash, slot_hash)
    }

    fn create_account_iterator(
        &self,
        start_key: &Hash256,
        end_key: &Hash256,
    ) -> PersistenceResult<Box<dyn FlatIterator>> {
        Ok(Box::new(PersistenceReader::create_account_iterator(
            self, start_key, end_key,
        )?))
    }

    fn create_storage_iterator(
        &self,
        account_key: &Hash256,
        start_slot_key: &Hash256,
        end_slot_key: &Hash256,
    ) -> PersistenceResult<Box<dyn FlatIterator>> {
        Ok(Box::new(PersistenceReader::create_storage_iterator(
            self,
            account_key,
            start_slot_key,
            end_slot_key,
        )?))
    }

    fn is_preimage_mode(&self) -> bool {
        PersistenceReader::is_preimage_mode(self)
    }
}

/// [`PersistenceWriteBatch`], with every write object-safe, and fed with op streams (see [`op_stream`]).
pub trait DynWriteBatch: Send {
    /// Applies the operations of `ops` in order, stopping at the first that fails; the ones before it stay applied.
    fn apply(&mut self, ops: &[u8]) -> Result<(), FfiError>;
    fn self_destruct(&mut self, address: &Address) -> PersistenceResult<()>;
    fn set_account(&mut self, address: &Address, account: Option<&Account>) -> PersistenceResult<()>;
    fn set_storage(&mut self, address: &Address, slot: &U256, value: Option<&U256>) -> PersistenceResult<()>;
    fn set_state_trie_node(&mut self, path: &TreePath, rlp: &[u8]) -> PersistenceResult<()>;
    fn set_storage_trie_node(&mut self, address_hash: &Hash256, path: &TreePath, rlp: &[u8]) -> PersistenceResult<()>;
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
    fn delete_state_trie_node_range(&mut self, from: &Hash256, to: &Hash256) -> PersistenceResult<()>;
    fn delete_storage_trie_node_range(
        &mut self,
        address_hash: &Hash256,
        from: &Hash256,
        to: &Hash256,
    ) -> PersistenceResult<()>;
    fn commit(self: Box<Self>) -> PersistenceResult<()>;
}

impl<B: PersistenceWriteBatch> DynWriteBatch for B {
    fn apply(&mut self, ops: &[u8]) -> Result<(), FfiError> {
        op_stream::apply(self, ops)
    }

    fn self_destruct(&mut self, address: &Address) -> PersistenceResult<()> {
        PersistenceWriteBatch::self_destruct(self, address)
    }

    fn set_account(&mut self, address: &Address, account: Option<&Account>) -> PersistenceResult<()> {
        PersistenceWriteBatch::set_account(self, address, account)
    }

    fn set_storage(&mut self, address: &Address, slot: &U256, value: Option<&U256>) -> PersistenceResult<()> {
        PersistenceWriteBatch::set_storage(self, address, slot, value)
    }

    fn set_state_trie_node(&mut self, path: &TreePath, rlp: &[u8]) -> PersistenceResult<()> {
        PersistenceWriteBatch::set_state_trie_node(self, path, rlp)
    }

    fn set_storage_trie_node(&mut self, address_hash: &Hash256, path: &TreePath, rlp: &[u8]) -> PersistenceResult<()> {
        PersistenceWriteBatch::set_storage_trie_node(self, address_hash, path, rlp)
    }

    fn set_storage_raw_encoded(
        &mut self,
        address_hash: &Hash256,
        slot_hash: &Hash256,
        rlp_value: &[u8],
    ) -> PersistenceResult<()> {
        PersistenceWriteBatch::set_storage_raw_encoded(self, address_hash, slot_hash, rlp_value)
    }

    fn set_account_raw(&mut self, address_hash: &Hash256, account: &Account) -> PersistenceResult<()> {
        PersistenceWriteBatch::set_account_raw(self, address_hash, account)
    }

    fn delete_account_range(&mut self, from_path: &Hash256, to_path: &Hash256) -> PersistenceResult<()> {
        PersistenceWriteBatch::delete_account_range(self, from_path, to_path)
    }

    fn delete_storage_range(
        &mut self,
        address_hash: &Hash256,
        from_path: &Hash256,
        to_path: &Hash256,
    ) -> PersistenceResult<()> {
        PersistenceWriteBatch::delete_storage_range(self, address_hash, from_path, to_path)
    }

    fn delete_state_trie_node_range(&mut self, from: &Hash256, to: &Hash256) -> PersistenceResult<()> {
        PersistenceWriteBatch::delete_state_trie_node_range(self, from, to)
    }

    fn delete_storage_trie_node_range(
        &mut self,
        address_hash: &Hash256,
        from: &Hash256,
        to: &Hash256,
    ) -> PersistenceResult<()> {
        PersistenceWriteBatch::delete_storage_trie_node_range(self, address_hash, from, to)
    }

    fn commit(self: Box<Self>) -> PersistenceResult<()> {
        PersistenceWriteBatch::commit(*self)
    }
}

/// The store's persistence stack as a [`Persistence`], for the snapshot manager, whose worker threads call it outside
/// any export: so it refuses readers and batches once the store is poisoned, and a panic unwinding through one of its
/// batches poisons the store, as one inside an export does.
#[derive(Clone)]
pub struct ErasedPersistence {
    pub persistence: Arc<dyn DynPersistence>,
    /// The store's poison flag and handle count.
    pub poison: Arc<Poison>,
}

impl ErasedPersistence {
    fn refuse_if_poisoned(&self) -> PersistenceResult<()> {
        if self.poison.is_set() {
            return Err(PersistenceError::InvalidOperation(Poison::MESSAGE.to_owned()));
        }
        Ok(())
    }
}

impl Persistence for ErasedPersistence {
    type Reader = ErasedReader;
    type WriteBatch = ErasedBatch;

    fn create_reader(&self, flags: ReaderFlags) -> PersistenceResult<ErasedReader> {
        self.refuse_if_poisoned()?;
        Ok(ErasedReader(self.persistence.create_reader(flags)?))
    }

    fn create_write_batch(&self, from: &StateId, to: &StateId, flags: WriteFlags) -> PersistenceResult<ErasedBatch> {
        self.refuse_if_poisoned()?;
        #[cfg(any(test, feature = "test-api"))]
        self.poison.faults.panic_if_armed(crate::faults::Fault::PanicInPersist);
        Ok(ErasedBatch {
            batch: Some(self.persistence.create_write_batch(from, to, flags)?),
            poison: Arc::clone(&self.poison),
        })
    }

    fn flush(&self) -> PersistenceResult<()> {
        self.refuse_if_poisoned()?;
        self.persistence.flush()
    }

    fn clear(&self) -> PersistenceResult<()> {
        self.refuse_if_poisoned()?;
        self.persistence.clear()
    }
}

/// A reader of the store's stack as a [`PersistenceReader`].
pub struct ErasedReader(pub Box<dyn DynReader>);

/// A boxed iterator as a [`FlatIterator`].
pub struct ErasedIter(pub Box<dyn FlatIterator>);

impl FlatIterator for ErasedIter {
    fn move_next(&mut self) -> PersistenceResult<bool> {
        self.0.move_next()
    }

    fn current_key(&self) -> Hash256 {
        self.0.current_key()
    }

    fn current_value(&self) -> &[u8] {
        self.0.current_value()
    }
}

/// Runs a visitor read, handing `read` the value it saw.
fn visit<T>(
    read: impl FnOnce(&[u8]) -> T,
    run: impl FnOnce(&mut dyn FnMut(&[u8])) -> PersistenceResult<bool>,
) -> PersistenceResult<Option<T>> {
    let mut read = Some(read);
    let mut result = None;
    run(&mut |value| result = read.take().map(|read| read(value)))?;
    Ok(result)
}

impl PersistenceReader for ErasedReader {
    type Iter = ErasedIter;

    fn get_account(&self, address: &Address) -> PersistenceResult<Option<Account>> {
        self.0.get_account(address)
    }

    fn try_get_slot(&self, address: &Address, slot: &U256) -> PersistenceResult<Option<U256>> {
        self.0.try_get_slot(address, slot)
    }

    fn current_state(&self) -> StateId {
        self.0.current_state()
    }

    fn load_state_rlp<T>(
        &self,
        path: &TreePath,
        flags: ReadFlags,
        read: impl FnOnce(&[u8]) -> T,
    ) -> PersistenceResult<Option<T>> {
        visit(read, |visitor| self.0.visit_state_rlp(path, flags, visitor))
    }

    fn load_storage_rlp<T>(
        &self,
        address_hash: &Hash256,
        path: &TreePath,
        flags: ReadFlags,
        read: impl FnOnce(&[u8]) -> T,
    ) -> PersistenceResult<Option<T>> {
        visit(read, |visitor| {
            self.0.visit_storage_rlp(address_hash, path, flags, visitor)
        })
    }

    fn get_account_raw(&self, address_hash: &Hash256) -> PersistenceResult<Option<Vec<u8>>> {
        self.0.get_account_raw(address_hash)
    }

    fn try_get_storage_raw(&self, address_hash: &Hash256, slot_hash: &Hash256) -> PersistenceResult<Option<U256>> {
        self.0.try_get_storage_raw(address_hash, slot_hash)
    }

    fn create_account_iterator(&self, start_key: &Hash256, end_key: &Hash256) -> PersistenceResult<ErasedIter> {
        Ok(ErasedIter(self.0.create_account_iterator(start_key, end_key)?))
    }

    fn create_storage_iterator(
        &self,
        account_key: &Hash256,
        start_slot_key: &Hash256,
        end_slot_key: &Hash256,
    ) -> PersistenceResult<ErasedIter> {
        Ok(ErasedIter(self.0.create_storage_iterator(
            account_key,
            start_slot_key,
            end_slot_key,
        )?))
    }

    fn is_preimage_mode(&self) -> bool {
        self.0.is_preimage_mode()
    }
}

/// A batch of the store's stack as a [`PersistenceWriteBatch`].
pub struct ErasedBatch {
    /// `None` once committed.
    batch: Option<Box<dyn DynWriteBatch>>,
    poison: Arc<Poison>,
}

impl ErasedBatch {
    fn open(&mut self) -> &mut dyn DynWriteBatch {
        self.batch.as_deref_mut().expect("a batch is written until its commit")
    }
}

impl Drop for ErasedBatch {
    /// A panic unwinding through the batch, while it writes or commits, may leave the caches its commit updates
    /// half-updated.
    fn drop(&mut self) {
        if thread::panicking() {
            self.poison.set();
        }
    }
}

impl PersistenceWriteBatch for ErasedBatch {
    fn self_destruct(&mut self, address: &Address) -> PersistenceResult<()> {
        self.open().self_destruct(address)
    }

    fn set_account(&mut self, address: &Address, account: Option<&Account>) -> PersistenceResult<()> {
        self.open().set_account(address, account)
    }

    fn set_storage(&mut self, address: &Address, slot: &U256, value: Option<&U256>) -> PersistenceResult<()> {
        self.open().set_storage(address, slot, value)
    }

    fn set_state_trie_node(&mut self, path: &TreePath, rlp: &[u8]) -> PersistenceResult<()> {
        self.open().set_state_trie_node(path, rlp)
    }

    fn set_storage_trie_node(&mut self, address_hash: &Hash256, path: &TreePath, rlp: &[u8]) -> PersistenceResult<()> {
        self.open().set_storage_trie_node(address_hash, path, rlp)
    }

    fn set_storage_raw_encoded(
        &mut self,
        address_hash: &Hash256,
        slot_hash: &Hash256,
        rlp_value: &[u8],
    ) -> PersistenceResult<()> {
        self.open().set_storage_raw_encoded(address_hash, slot_hash, rlp_value)
    }

    fn set_account_raw(&mut self, address_hash: &Hash256, account: &Account) -> PersistenceResult<()> {
        self.open().set_account_raw(address_hash, account)
    }

    fn delete_account_range(&mut self, from_path: &Hash256, to_path: &Hash256) -> PersistenceResult<()> {
        self.open().delete_account_range(from_path, to_path)
    }

    fn delete_storage_range(
        &mut self,
        address_hash: &Hash256,
        from_path: &Hash256,
        to_path: &Hash256,
    ) -> PersistenceResult<()> {
        self.open().delete_storage_range(address_hash, from_path, to_path)
    }

    fn delete_state_trie_node_range(&mut self, from: &Hash256, to: &Hash256) -> PersistenceResult<()> {
        self.open().delete_state_trie_node_range(from, to)
    }

    fn delete_storage_trie_node_range(
        &mut self,
        address_hash: &Hash256,
        from: &Hash256,
        to: &Hash256,
    ) -> PersistenceResult<()> {
        self.open().delete_storage_trie_node_range(address_hash, from, to)
    }

    fn commit(mut self) -> PersistenceResult<()> {
        self.batch.take().expect("a batch is committed once").commit()
    }
}
