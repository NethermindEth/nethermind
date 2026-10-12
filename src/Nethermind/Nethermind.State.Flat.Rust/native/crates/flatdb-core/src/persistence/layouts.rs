//! Ports of the three `IPersistence` classes over a key-value store, one per `FlatLayout` family:
//! - `RocksDbPersistence.cs` ([`FlatLayout::Flat`]): hashed flat state in the account and storage columns;
//! - `FlatInTriePersistence.cs` ([`FlatLayout::FlatInTrie`]): hashed flat state in the state and storage node
//!   columns, next to the trie nodes;
//! - `PreimageRocksdbPersistence.cs` ([`FlatLayout::PreimageFlatV1`], [`FlatLayout::PreimageFlat`]): flat state keyed
//!   by address and slot rather than their hashes, which skips hashing and compresses better but rules out snap sync
//!   and raw (importer) access. The trie nodes stay keyed by hashes.
//!
//! The classes differ only in those parameters, so one type serves them all, composed from [`super::base`],
//! [`super::flat`] and [`super::trie`] as C# composes them through `BasePersistence.Reader` and `WriteBatch`.

use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};

use parking_lot::Mutex;

use crate::codec::account::{decode_account, encode_account};
use crate::keccak::{account_path, slot_path};
use crate::kv::{Column, KvError, KvReader, KvResult, KvStore, ReadFlags, WriteBatch, WriteFlags};
use crate::types::{Account, Address, FlatLayout, Hash256, StateId, TreePath, U256};

use super::base::{
    BatchColumn, ColumnSink, SnapshotColumn, clear_all_columns, read_current_state, resolve_slot_encoding,
    set_current_state, set_layout, validate_layout,
};
use super::flat::{self, FlatKvIterator, FlatShape};
use super::trie;
use super::write_buffer::{ColumnCounters, DEFAULT_WRITE_BUFFER_FLOOR, WriteBufferAdjuster};
use super::{Persistence, PersistenceError, PersistenceReader, PersistenceResult, PersistenceWriteBatch, ReaderFlags};

/// The persistence of one layout over a key-value store. Cloning shares it.
pub struct KvPersistence<K: KvStore> {
    shared: Arc<Shared<K>>,
}

impl<K: KvStore> Clone for KvPersistence<K> {
    fn clone(&self) -> Self {
        Self {
            shared: self.shared.clone(),
        }
    }
}

struct Shared<K> {
    kv: K,
    layout: FlatLayout,
    shape: FlatShape,
    /// The columns whose writes the write buffer adjuster counts, in the order C# wraps them.
    counted_columns: &'static [Column],
    /// `_layoutPersisted`: whether a batch has recorded the layout.
    layout_persisted: AtomicBool,
    adjuster: Mutex<WriteBufferAdjuster>,
}

const FLAT_COUNTED_COLUMNS: &[Column] = &[
    Column::Account,
    Column::Storage,
    Column::StateTopNodes,
    Column::StateNodes,
    Column::StorageNodes,
    Column::FallbackNodes,
];

const FLAT_IN_TRIE_COUNTED_COLUMNS: &[Column] = &[
    Column::StateTopNodes,
    Column::StateNodes,
    Column::StorageNodes,
    Column::FallbackNodes,
];

impl<K: KvStore> KvPersistence<K> {
    /// Opens the persistence of `layout` over `kv`: fails when the DB was synced with another layout, and resolves
    /// whether its slot values are RLP-wrapped.
    ///
    /// `write_buffer_floor` is `IFlatDbConfig.PersistenceWriteBufferFloor`. Only the Flat layout applies it, as only
    /// `RocksDbPersistence` takes the config; the others use the default floor.
    pub fn new(kv: K, layout: FlatLayout, write_buffer_floor: Option<i64>) -> PersistenceResult<Self> {
        let (flat_columns, counted_columns) = match layout {
            FlatLayout::FlatInTrie => ((Column::StateNodes, Column::StorageNodes), FLAT_IN_TRIE_COUNTED_COLUMNS),
            _ => ((Column::Account, Column::Storage), FLAT_COUNTED_COLUMNS),
        };
        let floor = match layout {
            FlatLayout::Flat => write_buffer_floor.unwrap_or(DEFAULT_WRITE_BUFFER_FLOOR),
            _ => DEFAULT_WRITE_BUFFER_FLOOR,
        };

        validate_layout(&kv, layout)?;
        let rlp_wrap_slots = resolve_slot_encoding(&kv, flat_columns.1)?;

        Ok(Self {
            shared: Arc::new(Shared {
                kv,
                layout,
                shape: FlatShape {
                    account_column: flat_columns.0,
                    storage_column: flat_columns.1,
                    rlp_wrap_slots,
                    full_address_storage_key: layout == FlatLayout::PreimageFlat,
                },
                counted_columns,
                layout_persisted: AtomicBool::new(false),
                adjuster: Mutex::new(WriteBufferAdjuster::new(floor)),
            }),
        })
    }

    pub fn kv(&self) -> &K {
        &self.shared.kv
    }

    pub fn layout(&self) -> FlatLayout {
        self.shared.layout
    }

    /// Whether slot values are RLP-wrapped rather than in the legacy raw encoding.
    pub fn rlp_wrap_slots(&self) -> bool {
        self.shared.shape.rlp_wrap_slots
    }
}

impl<K: KvStore> Persistence for KvPersistence<K> {
    type Reader = KvPersistenceReader<K::Reader>;
    type WriteBatch = KvPersistenceWriteBatch<K>;

    fn create_reader(&self, flags: ReaderFlags) -> PersistenceResult<KvPersistenceReader<K::Reader>> {
        let snapshot = self.shared.kv.reader(flags.contains(ReaderFlags::FULL_SCAN))?;
        let current_state = read_current_state(&SnapshotColumn(&snapshot, Column::Metadata))?;
        Ok(KvPersistenceReader {
            snapshot,
            current_state,
            shape: self.shared.shape,
            preimage: self.shared.layout.is_preimage(),
        })
    }

    fn create_write_batch(
        &self,
        from: &StateId,
        to: &StateId,
        flags: WriteFlags,
    ) -> PersistenceResult<KvPersistenceWriteBatch<K>> {
        let shared = &self.shared;
        let snapshot = shared.kv.reader(false)?;
        let current = read_current_state(&SnapshotColumn(&snapshot, Column::Metadata))?;
        if *from != StateId::SYNC && *to != StateId::SYNC && current != *from {
            return Err(PersistenceError::WrongBaseState { from: *from, current });
        }

        let batch = shared.kv.write_batch()?;
        let mut counters = ColumnCounters::default();
        shared
            .adjuster
            .lock()
            .wrap(&shared.kv, shared.counted_columns, flags, &mut counters)?;

        Ok(KvPersistenceWriteBatch {
            shared: shared.clone(),
            snapshot,
            sink: Sink { batch, counters, flags },
            from: *from,
            to: *to,
        })
    }

    fn flush(&self) -> PersistenceResult<()> {
        Ok(tolerate_flush_error(self.shared.kv.flush())?)
    }

    fn clear(&self) -> PersistenceResult<()> {
        clear_all_columns(&self.shared.kv)
    }
}

/// `DbOnTheRocks.InnerFlush`: a RocksDB flush failure that is not fatal is only logged, so the flush, or the commit
/// whose write-ahead log it syncs, still succeeds. A fatal one is returned, for C# to shut down on.
fn tolerate_flush_error(flushed: KvResult<()>) -> KvResult<()> {
    match flushed {
        Err(KvError::RocksDb { message, fatal: false }) => {
            log::warn!("Failed to flush the flat DB: {message}");
            Ok(())
        }
        flushed => flushed,
    }
}

/// `PreimageRocksdbPersistence` fakes the address hash with the address, zero-padded.
fn fake_address_hash(address: &Address) -> Hash256 {
    let mut hash = [0u8; 32];
    hash[..20].copy_from_slice(address);
    hash
}

/// The keys of an account and a slot in the flat columns: their hashes, or in preimage mode the zero-padded address
/// and the big-endian slot.
fn flat_slot_paths(preimage: bool, address: &Address, slot: &U256) -> (Hash256, Hash256) {
    if preimage {
        (fake_address_hash(address), slot.to_be_bytes())
    } else {
        (account_path(address), slot_path(slot))
    }
}

fn flat_address_path(preimage: bool, address: &Address) -> Hash256 {
    if preimage {
        fake_address_hash(address)
    } else {
        account_path(address)
    }
}

/// A reader at a store snapshot.
pub struct KvPersistenceReader<R> {
    snapshot: R,
    current_state: StateId,
    shape: FlatShape,
    preimage: bool,
}

impl<R: KvReader + 'static> PersistenceReader for KvPersistenceReader<R> {
    type Iter = FlatKvIterator<R::Iter>;

    fn get_account(&self, address: &Address) -> PersistenceResult<Option<Account>> {
        let path = flat_address_path(self.preimage, address);
        Ok(
            flat::read_account(&self.snapshot, &self.shape, &path, |value| decode_account(value, true))?
                .transpose()?
                .flatten(),
        )
    }

    fn try_get_slot(&self, address: &Address, slot: &U256) -> PersistenceResult<Option<U256>> {
        let (address_path, slot_path) = flat_slot_paths(self.preimage, address, slot);
        flat::try_get_storage(&self.snapshot, &self.shape, &address_path, &slot_path)
    }

    fn current_state(&self) -> StateId {
        self.current_state
    }

    fn load_state_rlp<T>(
        &self,
        path: &TreePath,
        flags: ReadFlags,
        read: impl FnOnce(&[u8]) -> T,
    ) -> PersistenceResult<Option<T>> {
        trie::load_state_rlp(&self.snapshot, path, flags, read)
    }

    fn load_storage_rlp<T>(
        &self,
        address_hash: &Hash256,
        path: &TreePath,
        flags: ReadFlags,
        read: impl FnOnce(&[u8]) -> T,
    ) -> PersistenceResult<Option<T>> {
        trie::load_storage_rlp(&self.snapshot, address_hash, path, flags, read)
    }

    fn get_account_raw(&self, address_hash: &Hash256) -> PersistenceResult<Option<Vec<u8>>> {
        if self.preimage {
            return Err(PersistenceError::InvalidOperation(
                "Raw operation not available in preimage mode".to_owned(),
            ));
        }
        flat::read_account(&self.snapshot, &self.shape, address_hash, <[u8]>::to_vec)
    }

    fn try_get_storage_raw(&self, address_hash: &Hash256, slot_hash: &Hash256) -> PersistenceResult<Option<U256>> {
        flat::try_get_storage(&self.snapshot, &self.shape, address_hash, slot_hash)
    }

    fn create_account_iterator(&self, start_key: &Hash256, end_key: &Hash256) -> PersistenceResult<Self::Iter> {
        flat::create_account_iterator(&self.snapshot, &self.shape, start_key, end_key)
    }

    fn create_storage_iterator(
        &self,
        account_key: &Hash256,
        start_slot_key: &Hash256,
        end_slot_key: &Hash256,
    ) -> PersistenceResult<Self::Iter> {
        flat::create_storage_iterator(&self.snapshot, &self.shape, account_key, start_slot_key, end_slot_key)
    }

    fn is_preimage_mode(&self) -> bool {
        self.preimage
    }
}

/// The column batches of a write batch: writes with the batch's flags, counting bytes for the write buffer adjuster
/// on the columns it wraps (`WriteBufferAdjuster.CountingWriteBatch`).
struct Sink<B: crate::kv::KvRawBatch> {
    batch: WriteBatch<B>,
    counters: ColumnCounters,
    flags: WriteFlags,
}

impl<B: crate::kv::KvRawBatch> Sink<B> {
    fn count(&mut self, column: Column, bytes: usize) {
        if let Some(counter) = &mut self.counters[column.index()] {
            *counter += bytes as u64;
        }
    }
}

impl<B: crate::kv::KvRawBatch> ColumnSink for Sink<B> {
    fn put(&mut self, column: Column, key: &[u8], value: &[u8]) -> KvResult<()> {
        self.count(column, key.len() + value.len());
        self.batch.put(column, key, value, self.flags)
    }

    fn remove(&mut self, column: Column, key: &[u8]) -> KvResult<()> {
        self.count(column, key.len());
        self.batch.delete(column, key, WriteFlags::NONE)
    }
}

/// A write batch; its writes become visible together on [`PersistenceWriteBatch::commit`], except that a batch
/// without the write-ahead log writes out every 256 writes (see [`WriteBatch`]).
pub struct KvPersistenceWriteBatch<K: KvStore> {
    shared: Arc<Shared<K>>,
    /// The snapshot the range deletes scan (`dbSnap`).
    snapshot: K::Reader,
    sink: Sink<K::RawBatch>,
    from: StateId,
    to: StateId,
}

impl<K: KvStore> KvPersistenceWriteBatch<K> {
    fn shape(&self) -> &FlatShape {
        &self.shared.shape
    }

    fn preimage(&self) -> bool {
        self.shared.layout.is_preimage()
    }

    fn raw_operations(&self) -> PersistenceResult<()> {
        if self.preimage() {
            return Err(PersistenceError::InvalidOperation(
                "Raw operations not available in preimage mode".to_owned(),
            ));
        }
        Ok(())
    }

    fn snap_sync(&self) -> PersistenceResult<()> {
        if self.preimage() {
            return Err(PersistenceError::NotSupported(
                "Snap sync not supported in preimage mode",
            ));
        }
        Ok(())
    }
}

impl<K: KvStore> PersistenceWriteBatch for KvPersistenceWriteBatch<K> {
    /// Deletes the account's slots and then its storage trie, whose nodes are keyed by the address hash in every
    /// layout.
    fn self_destruct(&mut self, address: &Address) -> PersistenceResult<()> {
        let account_hash = account_path(address);
        let flat_path = if self.preimage() {
            fake_address_hash(address)
        } else {
            account_hash
        };
        let shape = self.shared.shape;
        flat::self_destruct(&self.snapshot, &shape, &mut self.sink, &flat_path)?;
        trie::self_destruct(&self.snapshot, &mut self.sink, &account_hash)
    }

    fn set_account(&mut self, address: &Address, account: Option<&Account>) -> PersistenceResult<()> {
        let path = flat_address_path(self.preimage(), address);
        let shape = self.shared.shape;
        match account {
            None => flat::remove_account(&mut self.sink, &shape, &path),
            Some(account) => flat::set_account(&mut self.sink, &shape, &path, &encode_account(account, true)),
        }
    }

    fn set_storage(&mut self, address: &Address, slot: &U256, value: Option<&U256>) -> PersistenceResult<()> {
        let (address_path, slot_path) = flat_slot_paths(self.preimage(), address, slot);
        let shape = self.shared.shape;
        flat::set_storage(&mut self.sink, &shape, &address_path, &slot_path, value)
    }

    fn set_state_trie_node(&mut self, path: &TreePath, rlp: &[u8]) -> PersistenceResult<()> {
        trie::set_state_trie_node(&mut self.sink, path, rlp)
    }

    fn set_storage_trie_node(&mut self, address_hash: &Hash256, path: &TreePath, rlp: &[u8]) -> PersistenceResult<()> {
        trie::set_storage_trie_node(&mut self.sink, address_hash, path, rlp)
    }

    fn set_storage_raw_encoded(
        &mut self,
        address_hash: &Hash256,
        slot_hash: &Hash256,
        rlp_value: &[u8],
    ) -> PersistenceResult<()> {
        self.raw_operations()?;
        let shape = self.shared.shape;
        flat::set_storage_encoded(&mut self.sink, &shape, address_hash, slot_hash, rlp_value)
    }

    fn set_account_raw(&mut self, address_hash: &Hash256, account: &Account) -> PersistenceResult<()> {
        self.raw_operations()?;
        let shape = self.shared.shape;
        flat::set_account(&mut self.sink, &shape, address_hash, &encode_account(account, true))
    }

    fn delete_account_range(&mut self, from_path: &Hash256, to_path: &Hash256) -> PersistenceResult<()> {
        self.snap_sync()?;
        let shape = self.shared.shape;
        flat::delete_account_range(&self.snapshot, &shape, &mut self.sink, from_path, to_path)
    }

    fn delete_storage_range(
        &mut self,
        address_hash: &Hash256,
        from_path: &Hash256,
        to_path: &Hash256,
    ) -> PersistenceResult<()> {
        self.snap_sync()?;
        let shape = *self.shape();
        flat::delete_storage_range(&self.snapshot, &shape, &mut self.sink, address_hash, from_path, to_path)
    }

    fn delete_state_trie_node_range(&mut self, from: &Hash256, to: &Hash256) -> PersistenceResult<()> {
        trie::delete_state_trie_node_range(&self.snapshot, &mut self.sink, from, to)
    }

    fn delete_storage_trie_node_range(
        &mut self,
        address_hash: &Hash256,
        from: &Hash256,
        to: &Hash256,
    ) -> PersistenceResult<()> {
        trie::delete_storage_trie_node_range(&self.snapshot, &mut self.sink, address_hash, from, to)
    }

    /// The dispose action of the C# batch: moves the state pointer unless this is a sync batch, records the layout
    /// with the first RLP batch, commits, resizes the memtables and, unless the batch skipped it, syncs the
    /// write-ahead log.
    fn commit(self) -> PersistenceResult<()> {
        let Self {
            shared,
            snapshot,
            sink: Sink {
                mut batch,
                counters,
                flags,
            },
            from,
            to,
        } = self;

        if from != StateId::SYNC && to != StateId::SYNC {
            set_current_state(&mut BatchColumn(&mut batch, Column::Metadata), &to)?;
        }
        if shared.shape.rlp_wrap_slots
            && shared
                .layout_persisted
                .compare_exchange(false, true, Ordering::AcqRel, Ordering::Acquire)
                .is_ok()
        {
            set_layout(&mut BatchColumn(&mut batch, Column::Metadata), shared.layout)?;
        }

        batch.commit()?;
        drop(snapshot);
        shared.adjuster.lock().on_batch_disposed(&shared.kv, &counters)?;
        if !flags.contains(WriteFlags::DISABLE_WAL) {
            tolerate_flush_error(shared.kv.flush_wal(true))?;
        }
        Ok(())
    }
}
