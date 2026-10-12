//! The persistence stack of a store behind object-safe traits, so one set of exports serves every key-value backend
//! and decorator combination. A call through them costs one indirect call, small next to the FFI transition.

use flatdb_core::kv::{GetInto, ReadFlags, WriteFlags};
use flatdb_core::persistence::{
    FlatIterator, Persistence, PersistenceReader, PersistenceResult, PersistenceWriteBatch, ReaderFlags,
};
use flatdb_core::types::{Account, Address, Hash256, StateId, TreePath, U256};

use crate::abi::FfiError;
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

/// [`PersistenceReader`], with byte strings copied into caller buffers and iterators boxed.
pub trait DynReader: Send + Sync {
    fn get_account(&self, address: &Address) -> PersistenceResult<Option<Account>>;
    fn try_get_slot(&self, address: &Address, slot: &U256) -> PersistenceResult<Option<U256>>;
    fn current_state(&self) -> StateId;
    fn load_state_rlp(&self, path: &TreePath, flags: ReadFlags, buffer: &mut [u8]) -> PersistenceResult<GetInto>;
    fn load_storage_rlp(
        &self,
        address_hash: &Hash256,
        path: &TreePath,
        flags: ReadFlags,
        buffer: &mut [u8],
    ) -> PersistenceResult<GetInto>;
    fn get_account_raw(&self, address_hash: &Hash256, buffer: &mut [u8]) -> PersistenceResult<GetInto>;
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

    fn load_state_rlp(&self, path: &TreePath, flags: ReadFlags, buffer: &mut [u8]) -> PersistenceResult<GetInto> {
        Ok(
            PersistenceReader::load_state_rlp(self, path, flags, |value| copy_into(value, buffer))?
                .unwrap_or(GetInto::NotFound),
        )
    }

    fn load_storage_rlp(
        &self,
        address_hash: &Hash256,
        path: &TreePath,
        flags: ReadFlags,
        buffer: &mut [u8],
    ) -> PersistenceResult<GetInto> {
        Ok(
            PersistenceReader::load_storage_rlp(self, address_hash, path, flags, |value| copy_into(value, buffer))?
                .unwrap_or(GetInto::NotFound),
        )
    }

    fn get_account_raw(&self, address_hash: &Hash256, buffer: &mut [u8]) -> PersistenceResult<GetInto> {
        Ok(PersistenceReader::get_account_raw(self, address_hash)?
            .map_or(GetInto::NotFound, |value| copy_into(&value, buffer)))
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

/// [`PersistenceWriteBatch`], fed with op streams (see [`op_stream`]).
pub trait DynWriteBatch: Send {
    /// Applies the operations of `ops` in order, stopping at the first that fails; the ones before it stay applied.
    fn apply(&mut self, ops: &[u8]) -> Result<(), FfiError>;

    fn commit(self: Box<Self>) -> PersistenceResult<()>;
}

impl<B: PersistenceWriteBatch> DynWriteBatch for B {
    fn apply(&mut self, ops: &[u8]) -> Result<(), FfiError> {
        op_stream::apply(self, ops)
    }

    fn commit(self: Box<Self>) -> PersistenceResult<()> {
        PersistenceWriteBatch::commit(*self)
    }
}
