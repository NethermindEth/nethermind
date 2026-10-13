//! Port of `Nethermind.State.Flat/ReadOnlySnapshotBundle.cs`: reads at a state through the chain of in-memory
//! snapshots above the persisted state, newest first, then the persistence reader.
//!
//! The C# bundle also searches a persisted-snapshot stack between the two; that tier belongs to long finality and is
//! not ported yet, so snapshot indexes here are in-memory indexes (C#'s `_persistedSnapshotCount` is 0).
//!
//! Trie nodes come back as RLP. A hash-only placeholder found in a snapshot shadows the older snapshots and the read
//! falls through to persistence, as C# resolves a placeholder `TryFindStateNodes` returns through `TryLoadStateRlp`.
//! The loads come in two flavours: unchecked, the read-only adapters' path-keyed read, and checked, the read of a
//! scope's adapters, whose `FindCachedOrUnknown` rejects a node of another hash.
//!
//! Every read reports where it was served from ([`ReadSource`]). Slot reads take the self-destruct index of the caller,
//! as C# does. The ffi `BundleReader`, which wraps this bundle as the persistence reader of a C# bundle without
//! snapshots, reads slots through [`ReadOnlySnapshotBundle::try_get_slot`] instead, which applies the newest clear
//! itself from a lazily built index.
//!
//! A bundle is shared as `Arc<ReadOnlySnapshotBundle>` (the C# `TryLease`/`Dispose`); dropping the last one drops the
//! snapshot leases, the slot filter and the persistence reader.

use std::hash::BuildHasher;
use std::sync::atomic::{AtomicU8, Ordering};
use std::sync::{Arc, OnceLock};
use std::time::Instant;

use hashbrown::{DefaultHashBuilder, HashMap};
use thiserror::Error;

use crate::bloom::BloomFilter;
use crate::kv::ReadFlags;
use crate::persistence::{NoopPersistenceReader, PersistenceError, PersistenceReader, PersistenceResult};
use crate::seam::BundleRead;
use crate::snapshot::Snapshot;
use crate::snapshot::content::TrieNodeRlp;
use crate::snapshot::keys::StorageKey;
use crate::stats::{FlatStats, Histogram};
use crate::types::{Account, Address, Hash256, StateId, TreePath, U256};

const SLOT_FILTER_NOT_BUILT: u8 = 0;
const SLOT_FILTER_BUILDING: u8 = 1;
const SLOT_FILTER_READY: u8 = 2;
const SLOT_FILTER_SKIPPED: u8 = 3;

/// With one snapshot the loop is already a single probe, which the filter would not beat.
const MIN_SNAPSHOTS_FOR_SLOT_FILTER: usize = 2;

/// Where a read was served from.
#[repr(u8)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ReadSource {
    /// The scope's content being written.
    Current = 1,
    /// A snapshot the scope committed, or one of its seed.
    Own = 2,
    /// An in-memory snapshot of the bundle.
    Snapshot = 3,
    Persistence = 4,
    /// Nothing: a self-destruct cut the slot off.
    SelfDestructed = 5,
}

/// Failure of a checked trie node load.
#[derive(Debug, Error, Clone, PartialEq, Eq)]
pub enum NodeLoadError {
    #[error(transparent)]
    Persistence(#[from] PersistenceError),
    /// `NodeHashMismatchException`: the in-memory node at the path has another hash.
    #[error("Node hash mismatch. Path: {path:?}. Hash: {stored:?} vs Requested: {requested:?}")]
    HashMismatch {
        path: TreePath,
        stored: Hash256,
        requested: Hash256,
    },
}

pub struct ReadOnlySnapshotBundle<R> {
    snapshots: Vec<Arc<Snapshot>>,
    /// `None` for [`ReadOnlySnapshotBundle::empty`], which reads as `NoopPersistenceReader`.
    persistence_reader: Option<R>,
    has_self_destructs: bool,
    slot_filter_bits_per_key: f64,
    slot_filter_state: AtomicU8,
    slot_filter: OnceLock<BloomFilter>,
    slot_filter_hasher: DefaultHashBuilder,
    self_destruct_index: OnceLock<HashMap<Address, usize>>,
    stats: Option<Arc<FlatStats>>,
}

impl<R: PersistenceReader> ReadOnlySnapshotBundle<R> {
    /// A bundle over `snapshots`, oldest first, above `persistence_reader`. `slot_filter_bits_per_key` sizes the
    /// negative slot filter of [`ReadOnlySnapshotBundle::get_slot_filtered`]; 0 never builds one.
    pub fn new(snapshots: Vec<Arc<Snapshot>>, persistence_reader: R, slot_filter_bits_per_key: f64) -> Self {
        let slot_filter_state = if slot_filter_bits_per_key > 0.0 && snapshots.len() >= MIN_SNAPSHOTS_FOR_SLOT_FILTER {
            SLOT_FILTER_NOT_BUILT
        } else {
            SLOT_FILTER_SKIPPED
        };
        let has_self_destructs = snapshots
            .iter()
            .any(|snapshot| snapshot.counts().self_destructed_storage_addresses > 0);
        Self {
            snapshots,
            persistence_reader: Some(persistence_reader),
            has_self_destructs,
            slot_filter_bits_per_key,
            slot_filter_state: AtomicU8::new(slot_filter_state),
            slot_filter: OnceLock::new(),
            slot_filter_hasher: DefaultHashBuilder::default(),
            self_destruct_index: OnceLock::new(),
            stats: None,
        }
    }

    /// The `PreGenesis` bundle of `GatherReadOnlySnapshotBundle`: no snapshots, over `NoopPersistenceReader`.
    pub fn empty() -> Self {
        Self {
            snapshots: Vec::new(),
            persistence_reader: None,
            has_self_destructs: false,
            slot_filter_bits_per_key: 0.0,
            slot_filter_state: AtomicU8::new(SLOT_FILTER_SKIPPED),
            slot_filter: OnceLock::new(),
            slot_filter_hasher: DefaultHashBuilder::default(),
            self_destruct_index: OnceLock::new(),
            stats: None,
        }
    }

    /// The bundle recording its slot filter's builds, failures and memory into `stats`.
    pub fn with_stats(mut self, stats: Arc<FlatStats>) -> Self {
        self.stats = Some(stats);
        self
    }

    pub fn snapshot_count(&self) -> usize {
        self.snapshots.len()
    }

    /// The in-memory snapshots, oldest first.
    pub fn snapshots(&self) -> &[Arc<Snapshot>] {
        &self.snapshots
    }

    /// The persistence reader; `None` for [`ReadOnlySnapshotBundle::empty`].
    pub fn persistence_reader(&self) -> Option<&R> {
        self.persistence_reader.as_ref()
    }

    /// The persisted state the bundle reads below its snapshots.
    pub fn reader_state(&self) -> StateId {
        match &self.persistence_reader {
            Some(reader) => reader.current_state(),
            None => NoopPersistenceReader.current_state(),
        }
    }

    /// Whether any snapshot clears a storage, so [`ReadOnlySnapshotBundle::determine_self_destruct_snapshot_idx`] can
    /// find one.
    pub fn has_self_destructs(&self) -> bool {
        self.has_self_destructs
    }

    pub fn get_account(&self, address: &Address) -> PersistenceResult<(Option<Account>, ReadSource)> {
        for snapshot in self.snapshots.iter().rev() {
            if let Some(account) = snapshot.try_get_account(address) {
                BundleRead::AccountSnapshot.count();
                return Ok((account, ReadSource::Snapshot));
            }
        }
        let account = match &self.persistence_reader {
            Some(reader) => reader.get_account(address)?,
            None => None,
        };
        match account {
            Some(_) => BundleRead::AccountPersistence.count(),
            None => BundleRead::AccountPersistenceNull.count(),
        }
        Ok((account, ReadSource::Persistence))
    }

    /// The index of the newest snapshot that cleared the storage of `address`, or -1.
    pub fn determine_self_destruct_snapshot_idx(&self, address: &Address) -> i32 {
        if !self.has_self_destructs {
            return -1;
        }
        self.snapshots
            .iter()
            .rposition(|snapshot| snapshot.has_self_destruct(address))
            .map_or(-1, |index| index as i32)
    }

    /// The slot, with zero and absent distinct: `None` when no snapshot newer than the clear at the caller's
    /// `self_destruct_snapshot_idx` (-1 for none) wrote it, and, without a clear, persistence holds no value for it.
    pub fn get_slot(
        &self,
        address: &Address,
        slot: &U256,
        self_destruct_snapshot_idx: i32,
    ) -> PersistenceResult<(Option<U256>, ReadSource)> {
        for (index, snapshot) in self.snapshots.iter().enumerate().rev() {
            if let Some(value) = snapshot.try_get_storage(address, slot) {
                BundleRead::StorageSnapshot.count();
                return Ok((value, ReadSource::Snapshot));
            }
            if index as i64 <= i64::from(self_destruct_snapshot_idx) {
                BundleRead::StorageSelfDestructed.count();
                return Ok((None, ReadSource::SelfDestructed));
            }
        }
        self.persisted_slot(address, slot)
    }

    fn persisted_slot(&self, address: &Address, slot: &U256) -> PersistenceResult<(Option<U256>, ReadSource)> {
        let value = match &self.persistence_reader {
            Some(reader) => reader.try_get_slot(address, slot)?,
            None => None,
        };
        match value {
            Some(found) if found != U256::ZERO => BundleRead::StoragePersistence.count(),
            _ => BundleRead::StoragePersistenceNull.count(),
        }
        Ok((value, ReadSource::Persistence))
    }

    /// Exactly what [`ReadOnlySnapshotBundle::get_slot`] returns, but a negative filter over the snapshots' slot keys
    /// answers a slot none of them wrote at one probe. The first call builds the filter, once per bundle; reads
    /// racing that build take the plain loop instead of waiting.
    pub fn get_slot_filtered(
        &self,
        address: &Address,
        slot: &U256,
        self_destruct_snapshot_idx: i32,
    ) -> PersistenceResult<(Option<U256>, ReadSource)> {
        let filter = match self.slot_filter.get() {
            Some(filter) => Some(filter),
            None if self.slot_filter_state.load(Ordering::Acquire) == SLOT_FILTER_NOT_BUILT => {
                self.try_build_slot_filter()
            }
            None => None,
        };
        let Some(filter) = filter else {
            return self.get_slot(address, slot, self_destruct_snapshot_idx);
        };
        if filter.might_contain(self.slot_filter_key(address, slot)) {
            return self.get_slot(address, slot, self_destruct_snapshot_idx);
        }

        // No snapshot holds the key, so only a clear in one of them could end the loop; that cut-off fires at some
        // snapshot exactly when the clear is at or above the oldest one.
        if self_destruct_snapshot_idx >= 0 {
            BundleRead::StorageSelfDestructed.count();
            return Ok((None, ReadSource::SelfDestructed));
        }
        self.persisted_slot(address, slot)
    }

    /// The slot the bundle's state holds, the self-destruct cut-off applied: [`ReadOnlySnapshotBundle::get_slot`], or
    /// [`ReadOnlySnapshotBundle::get_slot_filtered`] when `filtered`, at the newest clear of `address`. For the ffi
    /// `BundleReader`, whose C# bundle never passes an index.
    pub fn try_get_slot(&self, address: &Address, slot: &U256, filtered: bool) -> PersistenceResult<Option<U256>> {
        let self_destruct_snapshot_idx = self
            .self_destruct_index()
            .get(address)
            .map_or(-1, |index| *index as i32);
        let (value, _) = if filtered {
            self.get_slot_filtered(address, slot, self_destruct_snapshot_idx)?
        } else {
            self.get_slot(address, slot, self_destruct_snapshot_idx)?
        };
        Ok(value)
    }

    /// Port of `TryFindStateNodes`: the newest snapshot's node at `path`, which can be a placeholder.
    pub fn find_state_node(&self, path: &TreePath) -> Option<TrieNodeRlp<'_>> {
        self.snapshots
            .iter()
            .rev()
            .find_map(|snapshot| snapshot.try_get_state_node(path))
    }

    /// Port of `TryFindStorageNodes`. Self-destructs are not applied: a cleared storage trie has a new root, so its
    /// old nodes are never reached.
    pub fn find_storage_node(&self, address_hash: &Hash256, path: &TreePath) -> Option<TrieNodeRlp<'_>> {
        self.snapshots
            .iter()
            .rev()
            .find_map(|snapshot| snapshot.try_get_storage_node(address_hash, path))
    }

    /// Calls `read` with the RLP of the state trie node at `path`: the newest snapshot's node, or the persisted one
    /// when no snapshot holds it or the newest holds a placeholder. Port of the read-only adapter's `TryLoadRlp`, which
    /// returned the path-keyed node without comparing its hash.
    pub fn load_state_rlp_unchecked<T>(
        &self,
        path: &TreePath,
        flags: ReadFlags,
        read: impl FnOnce(&[u8]) -> T,
    ) -> PersistenceResult<(Option<T>, ReadSource)> {
        match self.find_state_node(path) {
            Some(node) if !node.is_hash_only_placeholder() => {
                BundleRead::StateNodeSnapshot.count();
                Ok((Some(read(node.rlp)), ReadSource::Snapshot))
            }
            _ => self.load_persisted_state_rlp(path, flags, read),
        }
    }

    /// As [`ReadOnlySnapshotBundle::load_state_rlp_unchecked`], in the storage trie of the account hashing to
    /// `address_hash`.
    pub fn load_storage_rlp_unchecked<T>(
        &self,
        address_hash: &Hash256,
        path: &TreePath,
        flags: ReadFlags,
        read: impl FnOnce(&[u8]) -> T,
    ) -> PersistenceResult<(Option<T>, ReadSource)> {
        match self.find_storage_node(address_hash, path) {
            Some(node) if !node.is_hash_only_placeholder() => {
                BundleRead::StorageNodeSnapshot.count();
                Ok((Some(read(node.rlp)), ReadSource::Snapshot))
            }
            _ => self.load_persisted_storage_rlp(address_hash, path, flags, read),
        }
    }

    /// As [`ReadOnlySnapshotBundle::load_state_rlp_unchecked`], but the newest snapshot's node at `path`, a placeholder
    /// included, must have `hash`: the check of `StateTrieStoreAdapter.FindCachedOrUnknown`.
    pub fn load_state_rlp_checked<T>(
        &self,
        path: &TreePath,
        hash: &Hash256,
        flags: ReadFlags,
        read: impl FnOnce(&[u8]) -> T,
    ) -> Result<(Option<T>, ReadSource), NodeLoadError> {
        match self.find_state_node(path) {
            Some(node) if node.hash != hash => Err(hash_mismatch(path, node.hash, hash)),
            Some(node) if !node.is_hash_only_placeholder() => {
                BundleRead::StateNodeSnapshot.count();
                Ok((Some(read(node.rlp)), ReadSource::Snapshot))
            }
            _ => Ok(self.load_persisted_state_rlp(path, flags, read)?),
        }
    }

    /// As [`ReadOnlySnapshotBundle::load_state_rlp_checked`], in the storage trie of the account hashing to
    /// `address_hash`.
    pub fn load_storage_rlp_checked<T>(
        &self,
        address_hash: &Hash256,
        path: &TreePath,
        hash: &Hash256,
        flags: ReadFlags,
        read: impl FnOnce(&[u8]) -> T,
    ) -> Result<(Option<T>, ReadSource), NodeLoadError> {
        match self.find_storage_node(address_hash, path) {
            Some(node) if node.hash != hash => Err(hash_mismatch(path, node.hash, hash)),
            Some(node) if !node.is_hash_only_placeholder() => {
                BundleRead::StorageNodeSnapshot.count();
                Ok((Some(read(node.rlp)), ReadSource::Snapshot))
            }
            _ => Ok(self.load_persisted_storage_rlp(address_hash, path, flags, read)?),
        }
    }

    /// The persisted state trie node at `path`, past every snapshot.
    pub fn load_persisted_state_rlp<T>(
        &self,
        path: &TreePath,
        flags: ReadFlags,
        read: impl FnOnce(&[u8]) -> T,
    ) -> PersistenceResult<(Option<T>, ReadSource)> {
        BundleRead::StateNodePersistence.count();
        let rlp = match &self.persistence_reader {
            Some(reader) => reader.load_state_rlp(path, flags, read)?,
            None => None,
        };
        Ok((rlp, ReadSource::Persistence))
    }

    /// The persisted node at `path` of the storage trie of the account hashing to `address_hash`, past every snapshot.
    pub fn load_persisted_storage_rlp<T>(
        &self,
        address_hash: &Hash256,
        path: &TreePath,
        flags: ReadFlags,
        read: impl FnOnce(&[u8]) -> T,
    ) -> PersistenceResult<(Option<T>, ReadSource)> {
        BundleRead::StorageNodePersistence.count();
        let rlp = match &self.persistence_reader {
            Some(reader) => reader.load_storage_rlp(address_hash, path, flags, read)?,
            None => None,
        };
        Ok((rlp, ReadSource::Persistence))
    }

    /// `false` when [`ReadOnlySnapshotBundle::get_slot_filtered`] can only take the plain loop: no bits per key,
    /// fewer than two snapshots, or a build that failed.
    pub fn may_filter_slots(&self) -> bool {
        self.slot_filter_state.load(Ordering::Acquire) != SLOT_FILTER_SKIPPED
    }

    /// The published slot filter, if built.
    pub fn slot_filter(&self) -> Option<&BloomFilter> {
        self.slot_filter.get()
    }

    /// Whether the published slot filter might hold the slot; `None` while no filter is built.
    pub fn slot_filter_might_contain(&self, address: &Address, slot: &U256) -> Option<bool> {
        self.slot_filter
            .get()
            .map(|filter| filter.might_contain(self.slot_filter_key(address, slot)))
    }

    fn slot_filter_key(&self, address: &Address, slot: &U256) -> u64 {
        self.slot_filter_hasher.hash_one(StorageKey::new(*address, *slot))
    }

    /// Only the reader moving the state out of `NotBuilt` builds; a failed build leaves the plain loop in charge.
    #[cold]
    fn try_build_slot_filter(&self) -> Option<&BloomFilter> {
        if self
            .slot_filter_state
            .compare_exchange(
                SLOT_FILTER_NOT_BUILT,
                SLOT_FILTER_BUILDING,
                Ordering::AcqRel,
                Ordering::Acquire,
            )
            .is_err()
        {
            return None;
        }

        let start = Instant::now();
        // A key written by several snapshots is counted once per snapshot, which only lowers the false-positive rate.
        let capacity: usize = self.snapshots.iter().map(|snapshot| snapshot.storages_count()).sum();
        // Snapshots that wrote no slot still get a filter: every read then skips them.
        match BloomFilter::new(capacity.max(1) as i64, self.slot_filter_bits_per_key) {
            Ok(mut filter) => {
                for snapshot in &self.snapshots {
                    snapshot.add_storage_keys_to(&mut filter, &self.slot_filter_hasher);
                }
                let filter = self.slot_filter.get_or_init(|| filter);
                self.slot_filter_state.store(SLOT_FILTER_READY, Ordering::Release);
                if let Some(stats) = &self.stats {
                    let counters = &stats.counters;
                    FlatStats::add(&counters.in_memory_slot_filter_memory, filter.data_bytes() as i64);
                    FlatStats::add(&counters.in_memory_slot_filter_builds, 1);
                    stats.observe_time(Histogram::InMemorySlotFilterBuildTime, start.elapsed());
                }
                Some(filter)
            }
            Err(_) => {
                self.slot_filter_state.store(SLOT_FILTER_SKIPPED, Ordering::Release);
                if let Some(stats) = &self.stats {
                    FlatStats::add(&stats.counters.in_memory_slot_filter_build_failures, 1);
                }
                None
            }
        }
    }

    fn self_destruct_index(&self) -> &HashMap<Address, usize> {
        self.self_destruct_index.get_or_init(|| {
            let mut index = HashMap::new();
            // Oldest first, so the newest clear of an address overwrites the older ones.
            for (position, snapshot) in self.snapshots.iter().enumerate() {
                for (address, _) in snapshot.self_destructed_storage_addresses() {
                    index.insert(*address, position);
                }
            }
            index
        })
    }
}

pub(crate) fn hash_mismatch(path: &TreePath, stored: &Hash256, requested: &Hash256) -> NodeLoadError {
    NodeLoadError::HashMismatch {
        path: *path,
        stored: *stored,
        requested: *requested,
    }
}

/// Port of `ReleaseSlotFilter`: returns the filter's bytes to the gauge.
impl<R> Drop for ReadOnlySnapshotBundle<R> {
    fn drop(&mut self) {
        if let (Some(stats), Some(filter)) = (&self.stats, self.slot_filter.get()) {
            FlatStats::add(
                &stats.counters.in_memory_slot_filter_memory,
                -(filter.data_bytes() as i64),
            );
        }
    }
}
