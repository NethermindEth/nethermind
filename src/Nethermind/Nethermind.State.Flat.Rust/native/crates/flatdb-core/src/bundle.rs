//! Port of `Nethermind.State.Flat/ReadOnlySnapshotBundle.cs`: reads at a state through the chain of in-memory
//! snapshots above the persisted state, newest first, then the persistence reader.
//!
//! The C# bundle also searches a persisted-snapshot stack between the two; that tier belongs to long finality and is
//! not ported yet, so snapshot indexes here are in-memory indexes (C#'s `_persistedSnapshotCount` is 0).
//!
//! Trie nodes come back as RLP. A hash-only placeholder found in a snapshot shadows the older snapshots and the read
//! falls through to persistence, as C# resolves a placeholder `TryFindStateNodes` returns through `TryLoadStateRlp`.
//!
//! The C# shell wraps this bundle as the persistence reader of a C# bundle without snapshots, so the reads here also
//! apply the self-destruct cut-off themselves ([`ReadOnlySnapshotBundle::try_get_slot`]), from a lazily built index of
//! the newest clear per address, which is `DetermineSelfDestructSnapshotIdx` at one probe.
//!
//! A bundle is shared as `Arc<ReadOnlySnapshotBundle>` (the C# `TryLease`/`Dispose`); dropping the last one drops the
//! snapshot leases, the slot filter and the persistence reader.

use std::hash::BuildHasher;
use std::sync::atomic::{AtomicU8, Ordering};
use std::sync::{Arc, OnceLock};
use std::time::Instant;

use hashbrown::{DefaultHashBuilder, HashMap};

use crate::bloom::BloomFilter;
use crate::kv::ReadFlags;
use crate::persistence::{PersistenceReader, PersistenceResult};
use crate::seam::BundleRead;
use crate::snapshot::Snapshot;
use crate::snapshot::content::TrieNodeRlp;
use crate::snapshot::keys::StorageKey;
use crate::stats::{FlatStats, Histogram};
use crate::types::{Account, Address, Hash256, TreePath, U256};

const SLOT_FILTER_NOT_BUILT: u8 = 0;
const SLOT_FILTER_BUILDING: u8 = 1;
const SLOT_FILTER_READY: u8 = 2;
const SLOT_FILTER_SKIPPED: u8 = 3;

/// With one snapshot the loop is already a single probe, which the filter would not beat.
const MIN_SNAPSHOTS_FOR_SLOT_FILTER: usize = 2;

pub struct ReadOnlySnapshotBundle<R> {
    snapshots: Vec<Arc<Snapshot>>,
    persistence_reader: R,
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
        Self {
            snapshots,
            persistence_reader,
            slot_filter_bits_per_key,
            slot_filter_state: AtomicU8::new(slot_filter_state),
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

    pub fn persistence_reader(&self) -> &R {
        &self.persistence_reader
    }

    pub fn get_account(&self, address: &Address) -> PersistenceResult<Option<Account>> {
        for snapshot in self.snapshots.iter().rev() {
            if let Some(account) = snapshot.try_get_account(address) {
                BundleRead::AccountSnapshot.count();
                return Ok(account);
            }
        }
        let account = self.persistence_reader.get_account(address)?;
        match account {
            Some(_) => BundleRead::AccountPersistence.count(),
            None => BundleRead::AccountPersistenceNull.count(),
        }
        Ok(account)
    }

    /// The index of the newest snapshot that cleared the storage of `address`.
    pub fn determine_self_destruct_snapshot_idx(&self, address: &Address) -> Option<usize> {
        self.snapshots
            .iter()
            .rposition(|snapshot| snapshot.has_self_destruct(address))
    }

    /// The slot, with zero and absent distinct: `None` when no snapshot newer than the clear at
    /// `self_destruct_snapshot_idx` wrote it, and, without a clear, persistence holds no value for it.
    pub fn get_slot(
        &self,
        self_destruct_snapshot_idx: Option<usize>,
        address: &Address,
        slot: &U256,
    ) -> PersistenceResult<Option<U256>> {
        for (index, snapshot) in self.snapshots.iter().enumerate().rev() {
            if let Some(value) = snapshot.try_get_storage(address, slot) {
                BundleRead::StorageSnapshot.count();
                return Ok(value);
            }
            if self_destruct_snapshot_idx.is_some_and(|cleared| index <= cleared) {
                BundleRead::StorageSelfDestructed.count();
                return Ok(None);
            }
        }
        self.persisted_slot(address, slot)
    }

    fn persisted_slot(&self, address: &Address, slot: &U256) -> PersistenceResult<Option<U256>> {
        let value = self.persistence_reader.try_get_slot(address, slot)?;
        match value {
            Some(found) if found != U256::ZERO => BundleRead::StoragePersistence.count(),
            _ => BundleRead::StoragePersistenceNull.count(),
        }
        Ok(value)
    }

    /// Exactly what [`ReadOnlySnapshotBundle::get_slot`] returns, but a negative filter over the snapshots' slot keys
    /// answers a slot none of them wrote at one probe. The first call builds the filter, once per bundle; reads
    /// racing that build take the plain loop instead of waiting.
    pub fn get_slot_filtered(
        &self,
        self_destruct_snapshot_idx: Option<usize>,
        address: &Address,
        slot: &U256,
    ) -> PersistenceResult<Option<U256>> {
        let filter = match self.slot_filter.get() {
            Some(filter) => Some(filter),
            None if self.slot_filter_state.load(Ordering::Acquire) == SLOT_FILTER_NOT_BUILT => {
                self.try_build_slot_filter()
            }
            None => None,
        };
        let Some(filter) = filter else {
            return self.get_slot(self_destruct_snapshot_idx, address, slot);
        };
        if filter.might_contain(self.slot_filter_key(address, slot)) {
            return self.get_slot(self_destruct_snapshot_idx, address, slot);
        }

        // No snapshot holds the key, so only a clear in one of them could end the loop.
        if self_destruct_snapshot_idx.is_some() {
            BundleRead::StorageSelfDestructed.count();
            return Ok(None);
        }
        self.persisted_slot(address, slot)
    }

    /// The slot the bundle's state holds, the self-destruct cut-off applied: [`ReadOnlySnapshotBundle::get_slot`], or
    /// [`ReadOnlySnapshotBundle::get_slot_filtered`] when `filtered`, at the newest clear of `address`.
    pub fn try_get_slot(&self, address: &Address, slot: &U256, filtered: bool) -> PersistenceResult<Option<U256>> {
        let self_destruct_snapshot_idx = self.self_destruct_index().get(address).copied();
        if filtered {
            self.get_slot_filtered(self_destruct_snapshot_idx, address, slot)
        } else {
            self.get_slot(self_destruct_snapshot_idx, address, slot)
        }
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
    /// when no snapshot holds it or the newest holds a placeholder.
    pub fn load_state_rlp<T>(
        &self,
        path: &TreePath,
        flags: ReadFlags,
        read: impl FnOnce(&[u8]) -> T,
    ) -> PersistenceResult<Option<T>> {
        match self.find_state_node(path) {
            Some(node) if !node.is_hash_only_placeholder() => {
                BundleRead::StateNodeSnapshot.count();
                Ok(Some(read(node.rlp)))
            }
            _ => {
                BundleRead::StateNodePersistence.count();
                self.persistence_reader.load_state_rlp(path, flags, read)
            }
        }
    }

    /// As [`ReadOnlySnapshotBundle::load_state_rlp`], in the storage trie of the account hashing to `address_hash`.
    pub fn load_storage_rlp<T>(
        &self,
        address_hash: &Hash256,
        path: &TreePath,
        flags: ReadFlags,
        read: impl FnOnce(&[u8]) -> T,
    ) -> PersistenceResult<Option<T>> {
        match self.find_storage_node(address_hash, path) {
            Some(node) if !node.is_hash_only_placeholder() => {
                BundleRead::StorageNodeSnapshot.count();
                Ok(Some(read(node.rlp)))
            }
            _ => {
                BundleRead::StorageNodePersistence.count();
                self.persistence_reader
                    .load_storage_rlp(address_hash, path, flags, read)
            }
        }
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
