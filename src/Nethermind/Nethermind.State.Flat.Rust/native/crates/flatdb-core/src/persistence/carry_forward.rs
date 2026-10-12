//! Port of `Nethermind.State.Flat/Persistence/CarryForwardCachingPersistence.cs`: caches flat account and slot reads
//! across heads, so a new head does not re-read the serving working set from the database on its first calls.
//!
//! The reader serves and fills the cache; the write batch drops its write set from the cache once committed, or
//! everything on a self-destruct or any raw or range write. The cache is generation-gated: a reader behind the cache
//! basis bypasses it rather than serving stale data.

use std::collections::{HashMap, HashSet};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, AtomicI64, AtomicU64, Ordering};

use parking_lot::{Mutex, RwLock};

use crate::kv::{ReadFlags, WriteFlags};
use crate::types::{Account, Address, Hash256, StateId, TreePath, U256};

use super::slot_table::{self, AddResult, CarryForwardSlotTable, SlotTableLease};
use super::{Persistence, PersistenceError, PersistenceReader, PersistenceResult, PersistenceWriteBatch, ReaderFlags};

/// `CarryForwardCachingPersistence.DefaultMaxEntriesPerKind`.
pub const DEFAULT_MAX_ENTRIES_PER_KIND: usize = 262_144;

/// `CarryForwardCachingPersistence.DefaultSlotCapacity`: with four ways, sets fill up and replace entries well before
/// the table does; at twice the account cap, a hot working set with a cold tail hits at least as often as the
/// 262,144-entry map this replaced, which was wiped at its cap.
pub const DEFAULT_SLOT_CAPACITY: usize = 2 * DEFAULT_MAX_ENTRIES_PER_KIND;

/// The `CarryForward*` metrics of `Nethermind.State.Flat.Metrics`, per cache.
#[derive(Default, Debug)]
pub struct CarryForwardMetrics {
    pub account_hits: AtomicI64,
    pub account_misses: AtomicI64,
    pub slot_hits: AtomicI64,
    pub slot_misses: AtomicI64,
    pub account_wipes: AtomicI64,
    pub slot_evictions: AtomicI64,
    /// Gauge of cached accounts.
    pub account_count: AtomicI64,
    /// Gauge of cached slots.
    pub slot_count: AtomicI64,
    /// `Db.Metrics.DetailedMetricsEnabled`: whether readers count hits and misses. A reader captures it when created.
    pub detailed_metrics_enabled: AtomicBool,
}

/// The decorator.
pub struct CarryForwardCachingPersistence<P: Persistence> {
    shared: Arc<Shared<P>>,
}

struct Shared<P: Persistence> {
    inner: P,
    max_entries_per_kind: usize,
    accounts: RwLock<HashMap<Address, Option<Account>>>,
    slots: Arc<CarryForwardSlotTable>,
    /// The cache's own lease on the slot table, given up by [`CarryForwardCachingPersistence::dispose`].
    own_lease: Mutex<Option<SlotTableLease>>,
    /// `_lock`: every cache write runs under it.
    lock: Mutex<Basis>,
    generation: AtomicU64,
    /// Handed from one write batch to the next, so a persist does not grow fresh sets; only sets within the cache's
    /// per-kind cap are kept.
    spare_written_accounts: Mutex<Option<HashSet<Address>>>,
    spare_written_slots: Mutex<Option<HashSet<(Address, U256)>>>,
    metrics: Arc<CarryForwardMetrics>,
}

/// State guarded by the cache lock.
struct Basis {
    state: StateId,
    /// Cached accounts; exact under the lock.
    account_count: usize,
}

impl<P: Persistence> CarryForwardCachingPersistence<P> {
    /// Wraps `inner` with the default capacities.
    pub fn new(inner: P) -> PersistenceResult<Self> {
        Self::with_capacity(inner, DEFAULT_MAX_ENTRIES_PER_KIND, DEFAULT_SLOT_CAPACITY)
    }

    /// `max_entries_per_kind` caps cached accounts and the tracked account and slot writes of a batch;
    /// `slot_capacity` is rounded up to a power of two of at least [`slot_table::WAYS`].
    pub fn with_capacity(inner: P, max_entries_per_kind: usize, slot_capacity: usize) -> PersistenceResult<Self> {
        let basis = inner.create_reader(ReaderFlags::NONE)?.current_state();
        let lease = CarryForwardSlotTable::create(slot_capacity).ok_or_else(|| {
            PersistenceError::InvalidOperation(format!(
                "slot capacity {slot_capacity} is not within 1..={}",
                slot_table::MAX_CAPACITY
            ))
        })?;

        Ok(Self {
            shared: Arc::new(Shared {
                inner,
                max_entries_per_kind,
                accounts: RwLock::new(HashMap::new()),
                slots: lease.table().clone(),
                own_lease: Mutex::new(Some(lease)),
                lock: Mutex::new(Basis {
                    state: basis,
                    account_count: 0,
                }),
                generation: AtomicU64::new(0),
                spare_written_accounts: Mutex::new(None),
                spare_written_slots: Mutex::new(None),
                metrics: Arc::new(CarryForwardMetrics::default()),
            }),
        })
    }

    pub fn inner(&self) -> &P {
        &self.shared.inner
    }

    pub fn metrics(&self) -> &Arc<CarryForwardMetrics> {
        &self.shared.metrics
    }

    pub fn slot_table(&self) -> &Arc<CarryForwardSlotTable> {
        &self.shared.slots
    }

    /// Port of `DisposeAsync`: gives up the cache's lease on the slot table. Readers still holding one keep the table
    /// alive until they are dropped; commits and clears lease it while they write it, so they still see them.
    pub fn dispose(&self) {
        let lease = self.shared.own_lease.lock().take();
        drop(lease);
    }

    pub fn has_spare_written_accounts(&self) -> bool {
        self.shared.spare_written_accounts.lock().is_some()
    }

    pub fn has_spare_written_slots(&self) -> bool {
        self.shared.spare_written_slots.lock().is_some()
    }
}

impl<P: Persistence> Shared<P> {
    fn is_current(&self, reader_generation: u64) -> bool {
        self.generation.load(Ordering::Acquire) == reader_generation
    }

    fn try_cache_account(&self, address: &Address, account: Option<Account>, reader_generation: u64) {
        // Another reader can fill the same miss while this one does I/O; the cache is best-effort, so a racing
        // removal only loses a hint.
        if self.accounts.read().contains_key(address) {
            return;
        }

        let mut basis = self.lock.lock();
        if self.generation.load(Ordering::Relaxed) != reader_generation {
            return;
        }

        let mut accounts = self.accounts.write();
        if accounts.contains_key(address) {
            return;
        }
        if basis.account_count >= self.max_entries_per_kind {
            accounts.clear();
            basis.account_count = 0;
            self.metrics.account_wipes.fetch_add(1, Ordering::Relaxed);
        }
        accounts.insert(*address, account);
        basis.account_count += 1;
        self.metrics
            .account_count
            .store(basis.account_count as i64, Ordering::Relaxed);
    }

    fn try_cache_slot(
        &self,
        lease: &SlotTableLease,
        hash: u64,
        address: &Address,
        slot: &U256,
        value: Option<U256>,
        reader_generation: u64,
    ) {
        if lease.try_get(hash, address, slot).is_some() {
            return;
        }

        // Best effort: under many threads the reads a full set loses would otherwise queue here, and a skipped
        // insert only costs a later miss.
        let Some(_basis) = self.lock.try_lock() else { return };
        if self.generation.load(Ordering::Relaxed) != reader_generation {
            return;
        }

        match lease.add_no_lock(hash, address, slot, value.is_some(), &value.unwrap_or_default()) {
            AddResult::Added => self
                .metrics
                .slot_count
                .store(i64::from(self.slots.count()), Ordering::Relaxed),
            AddResult::Replaced => {
                self.metrics.slot_evictions.fetch_add(1, Ordering::Relaxed);
            }
            AddResult::AlreadyPresent => {}
        }
    }

    fn on_committed(
        &self,
        to: &StateId,
        written_accounts: Option<&HashSet<Address>>,
        written_slots: Option<&HashSet<(Address, U256)>>,
        clear_all: bool,
    ) {
        let mut basis = self.lock.lock();
        self.generation.fetch_add(1, Ordering::AcqRel);
        basis.state = *to;

        if clear_all {
            self.clear_all_no_lock(&mut basis);
            return;
        }

        if let Some(written) = written_accounts {
            let mut accounts = self.accounts.write();
            for address in written {
                if accounts.remove(address).is_some() {
                    basis.account_count -= 1;
                }
            }
            self.metrics
                .account_count
                .store(basis.account_count as i64, Ordering::Relaxed);
        }

        if let Some(written) = written_slots
            && let Some(lease) = self.slots.try_lease()
        {
            for (address, slot) in written {
                lease.remove_no_lock(slot_table::hash(address, slot), address, slot);
            }
            drop(lease);
            self.metrics
                .slot_count
                .store(i64::from(self.slots.count()), Ordering::Relaxed);
        }
    }

    fn clear_all_no_lock(&self, basis: &mut Basis) {
        // An empty cache keeps its table: raw and range writes clear everything on every batch, and snap sync and
        // healing write such a batch for every account while nothing is cached.
        if basis.account_count != 0 {
            self.accounts.write().clear();
        }
        basis.account_count = 0;
        if let Some(lease) = self.slots.try_lease() {
            lease.clear_no_lock();
        }
        self.metrics.account_count.store(0, Ordering::Relaxed);
        self.metrics.slot_count.store(0, Ordering::Relaxed);
    }

    fn rent_written_accounts(&self) -> HashSet<Address> {
        self.spare_written_accounts.lock().take().unwrap_or_default()
    }

    fn rent_written_slots(&self) -> HashSet<(Address, U256)> {
        self.spare_written_slots.lock().take().unwrap_or_default()
    }

    fn return_written_sets(
        &self,
        written_accounts: Option<HashSet<Address>>,
        written_slots: Option<HashSet<(Address, U256)>>,
    ) {
        if let Some(mut written) = written_accounts
            && written.len() <= self.max_entries_per_kind
        {
            written.clear();
            *self.spare_written_accounts.lock() = Some(written);
        }

        if let Some(mut written) = written_slots
            && written.len() <= self.max_entries_per_kind
        {
            written.clear();
            *self.spare_written_slots.lock() = Some(written);
        }
    }
}

impl<P: Persistence> Persistence for CarryForwardCachingPersistence<P> {
    type Reader = CarryForwardReader<P>;
    type WriteBatch = InvalidatingWriteBatch<P>;

    fn create_reader(&self, flags: ReaderFlags) -> PersistenceResult<CarryForwardReader<P>> {
        let reader = self.shared.inner.create_reader(flags)?;
        if flags.intersects(ReaderFlags::SYNC) {
            return Ok(CarryForwardReader::Bypass(reader));
        }

        let (at_basis, generation) = {
            let basis = self.shared.lock.lock();
            (
                reader.current_state() == basis.state,
                self.shared.generation.load(Ordering::Relaxed),
            )
        };

        Ok(match at_basis.then(|| self.shared.slots.try_lease()).flatten() {
            Some(lease) => CarryForwardReader::Caching(CachingReader {
                inner: reader,
                lease,
                parent: self.shared.clone(),
                generation,
                record_detailed_metrics: self.shared.metrics.detailed_metrics_enabled.load(Ordering::Relaxed),
            }),
            None => CarryForwardReader::Bypass(reader),
        })
    }

    fn create_write_batch(
        &self,
        from: &StateId,
        to: &StateId,
        flags: WriteFlags,
    ) -> PersistenceResult<InvalidatingWriteBatch<P>> {
        Ok(InvalidatingWriteBatch {
            inner: self.shared.inner.create_write_batch(from, to, flags)?,
            parent: self.shared.clone(),
            to: *to,
            written_accounts: None,
            written_slots: None,
            clear_all: false,
        })
    }

    fn flush(&self) -> PersistenceResult<()> {
        self.shared.inner.flush()
    }

    fn clear(&self) -> PersistenceResult<()> {
        {
            let mut basis = self.shared.lock.lock();
            self.shared.clear_all_no_lock(&mut basis);
        }
        self.shared.inner.clear()
    }
}

/// A reader of [`CarryForwardCachingPersistence`]: caching at the cache basis, the inner reader otherwise.
pub enum CarryForwardReader<P: Persistence> {
    Caching(CachingReader<P>),
    Bypass(P::Reader),
}

/// A reader at the cache basis. A reader is shared by every thread reading its state, so its counters are atomic.
pub struct CachingReader<P: Persistence> {
    // Dropped before the lease, as `CachingReader.Dispose` disposes the inner reader first.
    inner: P::Reader,
    lease: SlotTableLease,
    parent: Arc<Shared<P>>,
    generation: u64,
    /// `DetailedMetricsEnabled` as it was when the reader was created, to keep its read off the hot path.
    record_detailed_metrics: bool,
}

impl<P: Persistence> CachingReader<P> {
    fn count(&self, counter: &AtomicI64) {
        if self.record_detailed_metrics {
            counter.fetch_add(1, Ordering::Relaxed);
        }
    }

    fn get_account(&self, address: &Address) -> PersistenceResult<Option<Account>> {
        let parent = &self.parent;
        let mut current = parent.is_current(self.generation);
        if current && let Some(cached) = parent.accounts.read().get(address).copied() {
            // Checked again after the lookup: the cache can hold an entry filled after this reader's generation.
            if parent.is_current(self.generation) {
                self.count(&parent.metrics.account_hits);
                return Ok(cached);
            }
            current = false;
        }

        if current {
            self.count(&parent.metrics.account_misses);
        }
        let account = self.inner.get_account(address)?;
        if current {
            parent.try_cache_account(address, account, self.generation);
        }
        Ok(account)
    }

    fn try_get_slot(&self, address: &Address, slot: &U256) -> PersistenceResult<Option<U256>> {
        let parent = &self.parent;
        let hash = slot_table::hash(address, slot);
        let mut current = parent.is_current(self.generation);
        if current && let Some((found, value)) = self.lease.try_get(hash, address, slot) {
            // Checked again after the lookup: the cache can hold an entry filled after this reader's generation.
            if parent.is_current(self.generation) {
                self.count(&parent.metrics.slot_hits);
                return Ok(found.then_some(value));
            }
            current = false;
        }

        if current {
            self.count(&parent.metrics.slot_misses);
        }
        let value = self.inner.try_get_slot(address, slot)?;
        if current {
            parent.try_cache_slot(&self.lease, hash, address, slot, value, self.generation);
        }
        Ok(value)
    }
}

impl<P: Persistence> CarryForwardReader<P> {
    fn inner(&self) -> &P::Reader {
        match self {
            Self::Caching(reader) => &reader.inner,
            Self::Bypass(reader) => reader,
        }
    }

    /// Whether the reader serves from the cache.
    pub fn is_caching(&self) -> bool {
        matches!(self, Self::Caching(_))
    }
}

impl<P: Persistence> PersistenceReader for CarryForwardReader<P> {
    type Iter = <P::Reader as PersistenceReader>::Iter;

    fn get_account(&self, address: &Address) -> PersistenceResult<Option<Account>> {
        match self {
            Self::Caching(reader) => reader.get_account(address),
            Self::Bypass(reader) => reader.get_account(address),
        }
    }

    fn try_get_slot(&self, address: &Address, slot: &U256) -> PersistenceResult<Option<U256>> {
        match self {
            Self::Caching(reader) => reader.try_get_slot(address, slot),
            Self::Bypass(reader) => reader.try_get_slot(address, slot),
        }
    }

    fn current_state(&self) -> StateId {
        self.inner().current_state()
    }

    fn load_state_rlp<T>(
        &self,
        path: &TreePath,
        flags: ReadFlags,
        read: impl FnOnce(&[u8]) -> T,
    ) -> PersistenceResult<Option<T>> {
        self.inner().load_state_rlp(path, flags, read)
    }

    fn load_storage_rlp<T>(
        &self,
        address_hash: &Hash256,
        path: &TreePath,
        flags: ReadFlags,
        read: impl FnOnce(&[u8]) -> T,
    ) -> PersistenceResult<Option<T>> {
        self.inner().load_storage_rlp(address_hash, path, flags, read)
    }

    fn get_account_raw(&self, address_hash: &Hash256) -> PersistenceResult<Option<Vec<u8>>> {
        self.inner().get_account_raw(address_hash)
    }

    fn try_get_storage_raw(&self, address_hash: &Hash256, slot_hash: &Hash256) -> PersistenceResult<Option<U256>> {
        self.inner().try_get_storage_raw(address_hash, slot_hash)
    }

    fn create_account_iterator(&self, start_key: &Hash256, end_key: &Hash256) -> PersistenceResult<Self::Iter> {
        self.inner().create_account_iterator(start_key, end_key)
    }

    fn create_storage_iterator(
        &self,
        account_key: &Hash256,
        start_slot_key: &Hash256,
        end_slot_key: &Hash256,
    ) -> PersistenceResult<Self::Iter> {
        self.inner()
            .create_storage_iterator(account_key, start_slot_key, end_slot_key)
    }

    fn is_preimage_mode(&self) -> bool {
        self.inner().is_preimage_mode()
    }
}

/// A batch that tracks the accounts and slots it writes, and drops them from the cache once committed.
pub struct InvalidatingWriteBatch<P: Persistence> {
    inner: P::WriteBatch,
    parent: Arc<Shared<P>>,
    to: StateId,
    written_accounts: Option<HashSet<Address>>,
    written_slots: Option<HashSet<(Address, U256)>>,
    clear_all: bool,
}

impl<P: Persistence> InvalidatingWriteBatch<P> {
    /// Records a written key, or gives up tracking and clears everything once a kind exceeds its cap.
    fn track_account(&mut self, address: &Address) {
        let written = self
            .written_accounts
            .get_or_insert_with(|| self.parent.rent_written_accounts());
        if written.len() < self.parent.max_entries_per_kind || written.contains(address) {
            written.insert(*address);
            return;
        }
        self.give_up_tracking();
    }

    fn track_slot(&mut self, address: &Address, slot: &U256) {
        let written = self
            .written_slots
            .get_or_insert_with(|| self.parent.rent_written_slots());
        let key = (*address, *slot);
        if written.len() < self.parent.max_entries_per_kind || written.contains(&key) {
            written.insert(key);
            return;
        }
        self.give_up_tracking();
    }

    fn give_up_tracking(&mut self) {
        self.clear_all = true;
        self.parent
            .return_written_sets(self.written_accounts.take(), self.written_slots.take());
    }
}

impl<P: Persistence> PersistenceWriteBatch for InvalidatingWriteBatch<P> {
    fn self_destruct(&mut self, address: &Address) -> PersistenceResult<()> {
        self.clear_all = true;
        self.inner.self_destruct(address)
    }

    fn set_account(&mut self, address: &Address, account: Option<&Account>) -> PersistenceResult<()> {
        if !self.clear_all {
            self.track_account(address);
        }
        self.inner.set_account(address, account)
    }

    fn set_storage(&mut self, address: &Address, slot: &U256, value: Option<&U256>) -> PersistenceResult<()> {
        if !self.clear_all {
            self.track_slot(address, slot);
        }
        self.inner.set_storage(address, slot, value)
    }

    fn set_state_trie_node(&mut self, path: &TreePath, rlp: &[u8]) -> PersistenceResult<()> {
        self.inner.set_state_trie_node(path, rlp)
    }

    fn set_storage_trie_node(&mut self, address_hash: &Hash256, path: &TreePath, rlp: &[u8]) -> PersistenceResult<()> {
        self.inner.set_storage_trie_node(address_hash, path, rlp)
    }

    fn set_storage_raw_encoded(
        &mut self,
        address_hash: &Hash256,
        slot_hash: &Hash256,
        rlp_value: &[u8],
    ) -> PersistenceResult<()> {
        self.clear_all = true;
        self.inner.set_storage_raw_encoded(address_hash, slot_hash, rlp_value)
    }

    fn set_account_raw(&mut self, address_hash: &Hash256, account: &Account) -> PersistenceResult<()> {
        self.clear_all = true;
        self.inner.set_account_raw(address_hash, account)
    }

    fn delete_account_range(&mut self, from_path: &Hash256, to_path: &Hash256) -> PersistenceResult<()> {
        self.clear_all = true;
        self.inner.delete_account_range(from_path, to_path)
    }

    fn delete_storage_range(
        &mut self,
        address_hash: &Hash256,
        from_path: &Hash256,
        to_path: &Hash256,
    ) -> PersistenceResult<()> {
        self.clear_all = true;
        self.inner.delete_storage_range(address_hash, from_path, to_path)
    }

    fn delete_state_trie_node_range(&mut self, from: &Hash256, to: &Hash256) -> PersistenceResult<()> {
        self.inner.delete_state_trie_node_range(from, to)
    }

    fn delete_storage_trie_node_range(
        &mut self,
        address_hash: &Hash256,
        from: &Hash256,
        to: &Hash256,
    ) -> PersistenceResult<()> {
        self.inner.delete_storage_trie_node_range(address_hash, from, to)
    }

    fn commit(self) -> PersistenceResult<()> {
        let Self {
            inner,
            parent,
            to,
            written_accounts,
            written_slots,
            clear_all,
        } = self;
        inner.commit()?;
        parent.on_committed(&to, written_accounts.as_ref(), written_slots.as_ref(), clear_all);
        parent.return_written_sets(written_accounts, written_slots);
        Ok(())
    }
}

/// Port of `CarryForwardCachingPersistenceTests`. The metrics are per cache here, so the tests read them directly
/// instead of through deltas of the global C# counters. `Reader_Dispose_WhenTheInnerReaderThrows` and
/// `WriteBatch_ClearingAnEmptyCache_KeepsItsAccountTable` have no counterpart: a Rust drop cannot fail, and the
/// account map is cleared in place rather than reallocated.
#[cfg(test)]
mod tests {
    use super::{CarryForwardCachingPersistence, DEFAULT_SLOT_CAPACITY, InvalidatingWriteBatch};
    use crate::keccak::{account_path, slot_path};
    use crate::kv::{ReadFlags, WriteFlags};
    use crate::persistence::slot_table::testing::{TestRng, pattern};
    use crate::persistence::slot_table::{self, WAYS};
    use crate::persistence::{
        EmptyIterator, Persistence, PersistenceError, PersistenceReader, PersistenceResult, PersistenceWriteBatch,
        ReaderFlags,
    };
    use crate::types::{Account, Address, EMPTY_TREE_HASH, Hash256, StateId, TreePath, U256};
    use parking_lot::{Mutex, RwLock};
    use rstest::rstest;
    use std::cell::RefCell;
    use std::collections::HashMap;
    use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
    use std::sync::{Arc, Barrier, mpsc};
    use std::time::Duration;

    const BASIS0: StateId = StateId::new(0, EMPTY_TREE_HASH);
    const BASIS1: StateId = StateId::new(1, EMPTY_TREE_HASH);
    const ADDRESS: Address = [0xA0; 20];
    const ADDRESS_B: Address = [0xB0; 20];
    const ADDRESS_C: Address = [0xC0; 20];

    #[derive(Clone, Copy, Debug)]
    enum Kind {
        Account,
        Slot,
    }

    /// `FakePersistence`: counts reads and answers every one the same way, at a settable state.
    #[derive(Clone)]
    struct Fake(Arc<FakeState>);

    struct FakeState {
        reader_state: Mutex<StateId>,
        account_reads: AtomicUsize,
        slot_reads: AtomicUsize,
        exists: bool,
    }

    impl Fake {
        fn new(exists: bool) -> Self {
            Self(Arc::new(FakeState {
                reader_state: Mutex::new(BASIS0),
                account_reads: AtomicUsize::new(0),
                slot_reads: AtomicUsize::new(0),
                exists,
            }))
        }

        fn set_reader_state(&self, state: StateId) {
            *self.0.reader_state.lock() = state;
        }

        fn reads(&self, kind: Kind) -> usize {
            match kind {
                Kind::Account => self.0.account_reads.load(Ordering::Relaxed),
                Kind::Slot => self.0.slot_reads.load(Ordering::Relaxed),
            }
        }
    }

    struct FakeReader(Arc<FakeState>);

    struct NoopBatch;

    fn not_supported<T>() -> PersistenceResult<T> {
        Err(PersistenceError::NotSupported("not part of the fake"))
    }

    impl Persistence for Fake {
        type Reader = FakeReader;
        type WriteBatch = NoopBatch;

        fn create_reader(&self, _flags: ReaderFlags) -> PersistenceResult<FakeReader> {
            Ok(FakeReader(self.0.clone()))
        }

        fn create_write_batch(&self, _: &StateId, _: &StateId, _: WriteFlags) -> PersistenceResult<NoopBatch> {
            Ok(NoopBatch)
        }

        fn flush(&self) -> PersistenceResult<()> {
            Ok(())
        }

        fn clear(&self) -> PersistenceResult<()> {
            Ok(())
        }
    }

    impl PersistenceReader for FakeReader {
        type Iter = EmptyIterator;

        fn get_account(&self, _address: &Address) -> PersistenceResult<Option<Account>> {
            self.0.account_reads.fetch_add(1, Ordering::Relaxed);
            Ok(self.0.exists.then(|| Account::new(1, U256::from_u64(100))))
        }

        fn try_get_slot(&self, _address: &Address, _slot: &U256) -> PersistenceResult<Option<U256>> {
            self.0.slot_reads.fetch_add(1, Ordering::Relaxed);
            Ok(self.0.exists.then(|| U256::from_u64(0x11)))
        }

        fn current_state(&self) -> StateId {
            *self.0.reader_state.lock()
        }

        fn load_state_rlp<T>(
            &self,
            _: &TreePath,
            _: ReadFlags,
            _: impl FnOnce(&[u8]) -> T,
        ) -> PersistenceResult<Option<T>> {
            Ok(None)
        }

        fn load_storage_rlp<T>(
            &self,
            _: &Hash256,
            _: &TreePath,
            _: ReadFlags,
            _: impl FnOnce(&[u8]) -> T,
        ) -> PersistenceResult<Option<T>> {
            Ok(None)
        }

        fn get_account_raw(&self, _: &Hash256) -> PersistenceResult<Option<Vec<u8>>> {
            Ok(None)
        }

        fn try_get_storage_raw(&self, _: &Hash256, _: &Hash256) -> PersistenceResult<Option<U256>> {
            Ok(None)
        }

        fn create_account_iterator(&self, _: &Hash256, _: &Hash256) -> PersistenceResult<EmptyIterator> {
            not_supported()
        }

        fn create_storage_iterator(&self, _: &Hash256, _: &Hash256, _: &Hash256) -> PersistenceResult<EmptyIterator> {
            not_supported()
        }

        fn is_preimage_mode(&self) -> bool {
            false
        }
    }

    impl PersistenceWriteBatch for NoopBatch {
        fn self_destruct(&mut self, _: &Address) -> PersistenceResult<()> {
            Ok(())
        }
        fn set_account(&mut self, _: &Address, _: Option<&Account>) -> PersistenceResult<()> {
            Ok(())
        }
        fn set_storage(&mut self, _: &Address, _: &U256, _: Option<&U256>) -> PersistenceResult<()> {
            Ok(())
        }
        fn set_state_trie_node(&mut self, _: &TreePath, _: &[u8]) -> PersistenceResult<()> {
            Ok(())
        }
        fn set_storage_trie_node(&mut self, _: &Hash256, _: &TreePath, _: &[u8]) -> PersistenceResult<()> {
            Ok(())
        }
        fn set_storage_raw_encoded(&mut self, _: &Hash256, _: &Hash256, _: &[u8]) -> PersistenceResult<()> {
            Ok(())
        }
        fn set_account_raw(&mut self, _: &Hash256, _: &Account) -> PersistenceResult<()> {
            Ok(())
        }
        fn delete_account_range(&mut self, _: &Hash256, _: &Hash256) -> PersistenceResult<()> {
            Ok(())
        }
        fn delete_storage_range(&mut self, _: &Hash256, _: &Hash256, _: &Hash256) -> PersistenceResult<()> {
            Ok(())
        }
        fn delete_state_trie_node_range(&mut self, _: &Hash256, _: &Hash256) -> PersistenceResult<()> {
            Ok(())
        }
        fn delete_storage_trie_node_range(&mut self, _: &Hash256, _: &Hash256, _: &Hash256) -> PersistenceResult<()> {
            Ok(())
        }
        fn commit(self) -> PersistenceResult<()> {
            Ok(())
        }
    }

    type Cache<P> = CarryForwardCachingPersistence<P>;

    fn cache(inner: Fake) -> Cache<Fake> {
        Cache::new(inner).unwrap()
    }

    fn address(key: u64) -> Address {
        if key == 1 { ADDRESS } else { ADDRESS_B }
    }

    fn read<R: PersistenceReader>(kind: Kind, reader: &R, key: u64) -> bool {
        match kind {
            Kind::Account => reader.get_account(&address(key)).unwrap().is_some(),
            Kind::Slot => reader.try_get_slot(&ADDRESS, &U256::from_u64(key)).unwrap().is_some(),
        }
    }

    fn read_new<P: Persistence>(kind: Kind, cache: &Cache<P>, key: u64) -> bool {
        read(kind, &cache.create_reader(ReaderFlags::NONE).unwrap(), key)
    }

    fn write<P: Persistence>(cache: &Cache<P>, write: impl FnOnce(&mut InvalidatingWriteBatch<P>)) {
        let mut batch = cache.create_write_batch(&BASIS0, &BASIS1, WriteFlags::NONE).unwrap();
        write(&mut batch);
        batch.commit().unwrap();
    }

    fn invalidate<P: Persistence>(kind: Kind, cache: &Cache<P>, key: u64) {
        write(cache, |batch| match kind {
            Kind::Account => batch
                .set_account(&address(key), Some(&Account::new(1, U256::from_u64(100))))
                .unwrap(),
            Kind::Slot => batch
                .set_storage(&ADDRESS, &U256::from_u64(key), Some(&U256::from_u64(0x22)))
                .unwrap(),
        });
    }

    fn count(cache: &Cache<Fake>, kind: Kind) -> i64 {
        let metrics = cache.metrics();
        match kind {
            Kind::Account => metrics.account_count.load(Ordering::Relaxed),
            Kind::Slot => metrics.slot_count.load(Ordering::Relaxed),
        }
    }

    fn hits_and_misses(cache: &Cache<Fake>, kind: Kind) -> (i64, i64) {
        let metrics = cache.metrics();
        match kind {
            Kind::Account => (
                metrics.account_hits.load(Ordering::Relaxed),
                metrics.account_misses.load(Ordering::Relaxed),
            ),
            Kind::Slot => (
                metrics.slot_hits.load(Ordering::Relaxed),
                metrics.slot_misses.load(Ordering::Relaxed),
            ),
        }
    }

    type Scenario = fn(&Cache<Fake>, &Fake);

    fn write_slot(cache: &Cache<Fake>, inner: &Fake, slot: u64) {
        write(cache, |batch| {
            batch
                .set_storage(&ADDRESS, &U256::from_u64(slot), Some(&U256::from_u64(0x22)))
                .unwrap()
        });
        inner.set_reader_state(BASIS1);
    }

    fn clearing(cache: &Cache<Fake>, inner: &Fake, op: impl FnOnce(&mut InvalidatingWriteBatch<Fake>)) {
        write(cache, op);
        inner.set_reader_state(BASIS1);
    }

    fn same_basis(_: &Cache<Fake>, _: &Fake) {}

    fn unwritten_slot(cache: &Cache<Fake>, inner: &Fake) {
        write_slot(cache, inner, 2);
    }

    fn written_slot(cache: &Cache<Fake>, inner: &Fake) {
        write_slot(cache, inner, 1);
    }

    fn self_destruct(cache: &Cache<Fake>, inner: &Fake) {
        clearing(cache, inner, |batch| batch.self_destruct(&ADDRESS).unwrap());
    }

    fn delete_account_range(cache: &Cache<Fake>, inner: &Fake) {
        clearing(cache, inner, |batch| {
            batch.delete_account_range(&[0; 32], &[0; 32]).unwrap()
        });
    }

    fn delete_storage_range(cache: &Cache<Fake>, inner: &Fake) {
        clearing(cache, inner, |batch| {
            batch.delete_storage_range(&[0; 32], &[0; 32], &[0; 32]).unwrap()
        });
    }

    fn set_account_raw(cache: &Cache<Fake>, inner: &Fake) {
        clearing(cache, inner, |batch| {
            batch
                .set_account_raw(&[0; 32], &Account::new(1, U256::from_u64(100)))
                .unwrap()
        });
    }

    fn set_storage_raw_encoded(cache: &Cache<Fake>, inner: &Fake) {
        clearing(cache, inner, |batch| {
            batch.set_storage_raw_encoded(&[0; 32], &[0; 32], &[]).unwrap()
        });
    }

    /// Advances the cache basis but leaves the reader behind, so it must bypass the cache.
    fn reader_behind_basis(cache: &Cache<Fake>, _: &Fake) {
        write(cache, |_| {});
    }

    #[rstest]
    #[case::same_basis_served_from_cache(same_basis, 1)]
    #[case::unwritten_slot_carried_forward(unwritten_slot, 1)]
    #[case::written_slot_invalidated(written_slot, 2)]
    #[case::self_destruct_clears_cache(self_destruct, 2)]
    #[case::delete_account_range_clears_cache(delete_account_range, 2)]
    #[case::delete_storage_range_clears_cache(delete_storage_range, 2)]
    #[case::set_account_raw_clears_cache(set_account_raw, 2)]
    #[case::set_storage_raw_encoded_clears_cache(set_storage_raw_encoded, 2)]
    #[case::reader_behind_basis_bypasses(reader_behind_basis, 2)]
    fn second_slot_read_after_scenario_reads_inner_expected_times(
        #[case] scenario: Scenario,
        #[case] expected_slot_reads: usize,
        #[values(false, true)] disposed_with_a_reader_open: bool,
    ) {
        let inner = Fake::new(true);
        let cache = cache(inner.clone());
        // The open reader keeps the slot table alive, so the cache must go on serving and invalidating it.
        let open_reader = disposed_with_a_reader_open.then(|| cache.create_reader(ReaderFlags::NONE).unwrap());
        if disposed_with_a_reader_open {
            cache.dispose();
        }

        read_new(Kind::Slot, &cache, 1);
        scenario(&cache, &inner);
        read_new(Kind::Slot, &cache, 1);

        assert_eq!(inner.reads(Kind::Slot), expected_slot_reads);
        drop(open_reader);
    }

    #[test]
    fn accounts_are_cached_at_a_basis_and_wiped_at_the_cap() {
        let inner = Fake::new(true);
        let cache = cache(inner.clone());
        read_new(Kind::Account, &cache, 1);
        read_new(Kind::Account, &cache, 1);
        assert_eq!(
            inner.reads(Kind::Account),
            1,
            "second read at the same basis is served from the cache"
        );

        let inner = Fake::new(true);
        let cache = Cache::with_capacity(inner.clone(), 1, DEFAULT_SLOT_CAPACITY).unwrap();
        for key in [1, 1, 2, 1] {
            read_new(Kind::Account, &cache, key);
        }
        assert_eq!(
            inner.reads(Kind::Account),
            3,
            "the second address overflows capacity 1, clearing the first"
        );
        assert_eq!(cache.metrics().account_wipes.load(Ordering::Relaxed), 2);
        assert_eq!(
            count(&cache, Kind::Account),
            1,
            "the gauge is published after the overflowing fill"
        );
    }

    /// Port of `Read_AfterCommit_WriteSetCapDecidesCarryForward`.
    #[rstest]
    #[case(Kind::Account, 1, 1)]
    #[case(Kind::Account, 2, 2)]
    #[case(Kind::Slot, 1, 1)]
    #[case(Kind::Slot, 2, 2)]
    fn the_write_set_cap_decides_carry_forward(#[case] kind: Kind, #[case] writes: u64, #[case] inner_reads: usize) {
        let inner = Fake::new(true);
        let cache = Cache::with_capacity(inner.clone(), 1, DEFAULT_SLOT_CAPACITY).unwrap();
        let written_accounts = [ADDRESS_B, ADDRESS_C];

        read_new(kind, &cache, 1);
        write(&cache, |batch| {
            for i in 0..writes {
                match kind {
                    Kind::Slot => batch
                        .set_storage(&ADDRESS, &U256::from_u64(i + 2), Some(&U256::from_u64(0x22)))
                        .unwrap(),
                    Kind::Account => batch
                        .set_account(
                            &written_accounts[i as usize],
                            Some(&Account::new(1, U256::from_u64(100))),
                        )
                        .unwrap(),
                }
            }
        });
        inner.set_reader_state(BASIS1);
        read_new(kind, &cache, 1);

        assert_eq!(inner.reads(kind), inner_reads);
    }

    #[rstest]
    fn the_committed_write_set_is_kept_for_the_next_batch(
        #[values(Kind::Account, Kind::Slot)] kind: Kind,
        #[values(1, 2)] written: u64,
    ) {
        let cache = Cache::with_capacity(Fake::new(true), 1, DEFAULT_SLOT_CAPACITY).unwrap();
        write(&cache, |batch| {
            for i in 0..written {
                match kind {
                    Kind::Account => batch
                        .set_account(&[i as u8; 20], Some(&Account::new(i, U256::from_u64(i))))
                        .unwrap(),
                    Kind::Slot => batch
                        .set_storage(&ADDRESS, &U256::from_u64(i), Some(&U256::from_u64(0x22)))
                        .unwrap(),
                }
            }
        });

        assert!(match kind {
            Kind::Account => cache.has_spare_written_accounts(),
            Kind::Slot => cache.has_spare_written_slots(),
        });
    }

    /// Port of `RetainedReader_RecordsCurrentCacheProbeButNotStaleBypass`.
    #[rstest]
    fn a_retained_reader_records_the_current_probe_but_not_the_stale_bypass(
        #[values(Kind::Account, Kind::Slot)] kind: Kind,
        #[values(true, false)] found: bool,
    ) {
        let inner = Fake::new(found);
        let cache = cache(inner.clone());
        cache.metrics().detailed_metrics_enabled.store(true, Ordering::Relaxed);

        let reader = cache.create_reader(ReaderFlags::NONE).unwrap();
        let first = read(kind, &reader, 1);
        let second = read(kind, &reader, 1);
        let inner_reads_after_current_reads = inner.reads(kind);
        write(&cache, |_| {});
        inner.set_reader_state(BASIS1);
        let stale = read(kind, &reader, 1);

        assert_eq!(
            (first, second, stale),
            (found, found, found),
            "including a missing value"
        );
        assert_eq!(hits_and_misses(&cache, kind), (1, 1), "only the current probe counts");
        assert_eq!(
            inner_reads_after_current_reads, 1,
            "the second current read uses the cached result"
        );
        assert_eq!(
            inner.reads(kind),
            2,
            "the retained reader bypasses the cache once its generation is stale"
        );
    }

    #[rstest]
    fn a_reader_captures_detailed_metrics_enabled_when_created(#[values(Kind::Account, Kind::Slot)] kind: Kind) {
        let cache = cache(Fake::new(true));
        let detailed = &cache.metrics().detailed_metrics_enabled;

        detailed.store(false, Ordering::Relaxed);
        let reader = cache.create_reader(ReaderFlags::NONE).unwrap();
        detailed.store(true, Ordering::Relaxed);
        read(kind, &reader, 1);
        let disabled_reader_misses = hits_and_misses(&cache, kind).1;
        drop(reader);

        let reader = cache.create_reader(ReaderFlags::NONE).unwrap();
        detailed.store(false, Ordering::Relaxed);
        read(kind, &reader, 2);

        assert_eq!(
            disabled_reader_misses, 0,
            "a false-to-true flag change does not affect an existing reader"
        );
        assert_eq!(hits_and_misses(&cache, kind).1, 1, "nor does a true-to-false one");
    }

    #[rstest]
    fn counts_follow_fills_commits_and_clears(#[values(Kind::Account, Kind::Slot)] kind: Kind) {
        let cache = cache(Fake::new(true));
        read_new(kind, &cache, 1);
        let after_fill = count(&cache, kind);
        invalidate(kind, &cache, 1);
        let after_commit = count(&cache, kind);
        read_new(Kind::Account, &cache, 1);
        read_new(Kind::Slot, &cache, 1);
        cache.clear().unwrap();

        assert_eq!(
            (after_fill, after_commit),
            (1, 0),
            "a commit evicts a written account or slot"
        );
        assert_eq!((count(&cache, Kind::Account), count(&cache, Kind::Slot)), (0, 0));
    }

    #[test]
    fn a_full_slot_set_replaces_instead_of_wiping() {
        let inner = Fake::new(true);
        let cache = Cache::with_capacity(inner.clone(), super::DEFAULT_MAX_ENTRIES_PER_KIND, WAYS).unwrap();

        for slot in 0..=WAYS as u64 {
            read_new(Kind::Slot, &cache, slot);
        }
        let inner_reads_after_fill = inner.reads(Kind::Slot);
        read_new(Kind::Slot, &cache, WAYS as u64);

        assert_eq!(cache.metrics().slot_evictions.load(Ordering::Relaxed), 1);
        assert_eq!(count(&cache, Kind::Slot), WAYS as i64, "a full table stays full");
        assert_eq!(
            inner.reads(Kind::Slot),
            inner_reads_after_fill,
            "the newest slot is served from the cache"
        );
    }

    #[test]
    fn a_working_set_of_a_quarter_of_the_capacity_is_served_from_the_cache() {
        const SLOTS: u64 = 131_072;
        let inner = Fake::new(true);
        let cache = cache(inner.clone());
        let reader = cache.create_reader(ReaderFlags::NONE).unwrap();

        for _ in 0..2 {
            for slot in 0..SLOTS {
                reader.try_get_slot(&ADDRESS, &U256::from_u64(slot)).unwrap();
            }
        }

        // Full sets make about 0.8% of the second pass miss at the default capacity.
        assert!(inner.reads(Kind::Slot) - (SLOTS as usize) < SLOTS as usize / 50);
    }

    #[test]
    fn a_read_under_the_held_cache_lock_does_not_wait_or_cache() {
        let inner = Fake::new(true);
        let cache = cache(inner.clone());
        let reader = cache.create_reader(ReaderFlags::NONE).unwrap();

        let completed = {
            let _held = cache.shared.lock.lock();
            let (done, finished) = mpsc::channel();
            std::thread::scope(|scope| {
                scope.spawn(|| {
                    reader.try_get_slot(&ADDRESS, &U256::from_u64(1)).unwrap();
                    done.send(()).unwrap();
                });
                finished.recv_timeout(Duration::from_secs(10)).is_ok()
            })
        };
        reader.try_get_slot(&ADDRESS, &U256::from_u64(1)).unwrap();
        reader.try_get_slot(&ADDRESS, &U256::from_u64(1)).unwrap();

        assert!(completed, "a read does not wait for the lock to cache what it read");
        assert_eq!(
            inner.reads(Kind::Slot),
            2,
            "the read under the held lock was not cached, the next one was"
        );
    }

    #[test]
    fn sync_readers_bypass_the_cache() {
        let inner = Fake::new(true);
        let cache = cache(inner.clone());
        for _ in 0..2 {
            let reader = cache.create_reader(ReaderFlags::SYNC).unwrap();
            assert!(!reader.is_caching());
            reader.try_get_slot(&ADDRESS, &U256::from_u64(1)).unwrap();
        }
        assert_eq!(inner.reads(Kind::Slot), 2);
    }

    #[test]
    fn dispose_frees_the_slot_table_once_the_last_reader_is_dropped() {
        let inner = Fake::new(true);
        let cache = cache(inner.clone());
        let reader = cache.create_reader(ReaderFlags::NONE).unwrap();
        reader.try_get_slot(&ADDRESS, &U256::from_u64(1)).unwrap();

        cache.dispose();
        cache.dispose();
        let allocated_while_reader_open = cache.slot_table().is_allocated();
        let read_by_open_reader = reader.try_get_slot(&ADDRESS, &U256::from_u64(1)).unwrap();
        drop(reader);
        let allocated_after_reader_dropped = cache.slot_table().is_allocated();
        let read_after_dispose = read_new(Kind::Slot, &cache, 1);
        invalidate(Kind::Slot, &cache, 1);
        let mut clearing = cache.create_write_batch(&BASIS1, &BASIS1, WriteFlags::NONE).unwrap();
        clearing.self_destruct(&ADDRESS).unwrap();
        clearing.commit().unwrap();
        cache.clear().unwrap();

        assert!(allocated_while_reader_open, "an open reader holds a lease");
        assert!(read_by_open_reader.is_some());
        assert!(!allocated_after_reader_dropped);
        assert!(
            read_after_dispose,
            "a reader created after dispose reads the inner persistence"
        );
        assert!(
            !cache.slot_table().is_allocated(),
            "commits and clears after dispose leave the freed table alone"
        );
        assert_eq!(
            inner.reads(Kind::Slot),
            2,
            "the open reader hit the cache, the late one bypassed it"
        );
    }

    thread_local! {
        /// `ModelPersistence.LastReaderState`: the state the calling thread's last reader pinned.
        static LAST_READER_STATE: RefCell<Option<Arc<ModelState>>> = const { RefCell::new(None) };
    }

    fn last_reader_state() -> Arc<ModelState> {
        LAST_READER_STATE.with(|state| state.borrow().clone().expect("a reader was created"))
    }

    /// `ModelPersistence`: a versioned in-memory persistence. Each commit publishes an immutable state, and a reader
    /// keeps the state it was created at, like a database snapshot. It is the reference the cache's reads are
    /// checked against.
    #[derive(Clone)]
    struct Model(Arc<ModelShared>);

    struct ModelShared {
        addresses: Vec<Address>,
        slots: Vec<U256>,
        addresses_by_hash: HashMap<Hash256, Address>,
        slots_by_hash: HashMap<Hash256, U256>,
        state: RwLock<Arc<ModelState>>,
    }

    #[derive(Clone, Default)]
    struct ModelState {
        block: u64,
        accounts: HashMap<Address, Account>,
        slots: HashMap<(Address, U256), U256>,
    }

    impl ModelState {
        fn id(&self) -> StateId {
            StateId::new(self.block, EMPTY_TREE_HASH)
        }
    }

    impl Model {
        fn new(addresses: usize, slots_per_address: usize) -> Self {
            let addresses: Vec<Address> = (0..addresses).map(|i| [i as u8 + 1; 20]).collect();
            // Small slots and hashed-looking ones, as contracts use both.
            let slots: Vec<U256> = (0..slots_per_address as u64)
                .map(|i| {
                    if i % 2 == 0 {
                        U256::from_u64(i)
                    } else {
                        U256::from_be_bytes(&crate::keccak::keccak256(&[i as u8]))
                    }
                })
                .collect();
            Self(Arc::new(ModelShared {
                addresses_by_hash: addresses
                    .iter()
                    .map(|address| (account_path(address), *address))
                    .collect(),
                slots_by_hash: slots.iter().map(|slot| (slot_path(slot), *slot)).collect(),
                addresses,
                slots,
                state: RwLock::new(Arc::new(ModelState::default())),
            }))
        }

        fn current(&self) -> Arc<ModelState> {
            self.0.state.read().clone()
        }

        fn key_count(&self) -> usize {
            self.0.addresses.len() * self.0.slots.len()
        }

        fn key(&self, key: usize) -> (Address, U256) {
            (
                self.0.addresses[key / self.0.slots.len()],
                self.0.slots[key % self.0.slots.len()],
            )
        }

        /// Commits one random batch through `cache`; `clear_all_share` of them carry a write that clears the cache.
        fn commit_random_batch(&self, cache: &Cache<Model>, random: &mut TestRng, clear_all_share: f64) {
            let current = self.current();
            let block = current.block + 1;
            let mut batch = cache
                .create_write_batch(&current.id(), &StateId::new(block, EMPTY_TREE_HASH), WriteFlags::NONE)
                .unwrap();

            for _ in 0..random.below(6) {
                let key = random.below(self.key_count());
                let (address, slot) = self.key(key);
                if random.below(3) == 0 {
                    let account = (random.below(5) != 0).then(|| Account::new(block, U256::from_u64(key as u64)));
                    batch.set_account(&address, account.as_ref()).unwrap();
                } else {
                    let value = (random.below(5) != 0).then(|| pattern(key as u64, block));
                    batch.set_storage(&address, &slot, value.as_ref()).unwrap();
                }
            }

            if random.unit() < clear_all_share {
                let key = random.below(self.key_count());
                let (address, slot) = self.key(key);
                let (address_hash, slot_hash) = (account_path(&address), slot_path(&slot));
                match random.below(5) {
                    0 => batch.self_destruct(&address).unwrap(),
                    1 => batch
                        .set_storage_raw_encoded(&address_hash, &slot_hash, &pattern(key as u64, block).to_be_bytes())
                        .unwrap(),
                    2 => batch
                        .set_account_raw(&address_hash, &Account::new(block + 1, U256::from_u64(key as u64)))
                        .unwrap(),
                    3 => {
                        let to = if random.below(2) == 0 { slot_hash } else { [0; 32] };
                        batch.delete_storage_range(&address_hash, &slot_hash, &to).unwrap();
                    }
                    _ => batch.delete_account_range(&address_hash, &address_hash).unwrap(),
                }
            }

            batch.commit().unwrap();
        }

        fn check_slot_read<R: PersistenceReader>(
            &self,
            reader: &R,
            state: &ModelState,
            random: &mut TestRng,
        ) -> Option<String> {
            let address = self.0.addresses[random.below(self.0.addresses.len())];
            let slot = self.0.slots[random.below(self.0.slots.len())];
            let read = reader.try_get_slot(&address, &slot).unwrap();
            let expected = state.slots.get(&(address, slot)).copied();
            (read != expected).then(|| {
                format!(
                    "slot {address:?}:{slot:?} at block {}: read {read:?}, expected {expected:?}",
                    state.block
                )
            })
        }

        fn check_account_read<R: PersistenceReader>(
            &self,
            reader: &R,
            state: &ModelState,
            random: &mut TestRng,
        ) -> Option<String> {
            let address = self.0.addresses[random.below(self.0.addresses.len())];
            let read = reader.get_account(&address).unwrap();
            let expected = state.accounts.get(&address).copied();
            (read != expected).then(|| {
                format!(
                    "account {address:?} at block {}: read {read:?}, expected {expected:?}",
                    state.block
                )
            })
        }

        fn check_read<R: PersistenceReader>(
            &self,
            reader: &R,
            state: &ModelState,
            random: &mut TestRng,
        ) -> Option<String> {
            if random.below(4) == 0 {
                self.check_account_read(reader, state, random)
            } else {
                self.check_slot_read(reader, state, random)
            }
        }
    }

    struct ModelReader(Arc<ModelState>);

    struct ModelBatch {
        model: Model,
        state: ModelState,
    }

    impl Persistence for Model {
        type Reader = ModelReader;
        type WriteBatch = ModelBatch;

        fn create_reader(&self, _flags: ReaderFlags) -> PersistenceResult<ModelReader> {
            let state = self.current();
            LAST_READER_STATE.with(|last| *last.borrow_mut() = Some(state.clone()));
            Ok(ModelReader(state))
        }

        fn create_write_batch(&self, _: &StateId, to: &StateId, _: WriteFlags) -> PersistenceResult<ModelBatch> {
            let mut state = (*self.current()).clone();
            state.block = to.block_number;
            Ok(ModelBatch {
                model: self.clone(),
                state,
            })
        }

        fn flush(&self) -> PersistenceResult<()> {
            Ok(())
        }

        fn clear(&self) -> PersistenceResult<()> {
            let mut state = self.0.state.write();
            *state = Arc::new(ModelState {
                block: state.block,
                ..ModelState::default()
            });
            Ok(())
        }
    }

    impl PersistenceReader for ModelReader {
        type Iter = EmptyIterator;

        fn get_account(&self, address: &Address) -> PersistenceResult<Option<Account>> {
            Ok(self.0.accounts.get(address).copied())
        }

        fn try_get_slot(&self, address: &Address, slot: &U256) -> PersistenceResult<Option<U256>> {
            Ok(self.0.slots.get(&(*address, *slot)).copied())
        }

        fn current_state(&self) -> StateId {
            self.0.id()
        }

        fn load_state_rlp<T>(
            &self,
            _: &TreePath,
            _: ReadFlags,
            _: impl FnOnce(&[u8]) -> T,
        ) -> PersistenceResult<Option<T>> {
            Ok(None)
        }

        fn load_storage_rlp<T>(
            &self,
            _: &Hash256,
            _: &TreePath,
            _: ReadFlags,
            _: impl FnOnce(&[u8]) -> T,
        ) -> PersistenceResult<Option<T>> {
            Ok(None)
        }

        fn get_account_raw(&self, _: &Hash256) -> PersistenceResult<Option<Vec<u8>>> {
            Ok(None)
        }

        fn try_get_storage_raw(&self, _: &Hash256, _: &Hash256) -> PersistenceResult<Option<U256>> {
            Ok(None)
        }

        fn create_account_iterator(&self, _: &Hash256, _: &Hash256) -> PersistenceResult<EmptyIterator> {
            not_supported()
        }

        fn create_storage_iterator(&self, _: &Hash256, _: &Hash256, _: &Hash256) -> PersistenceResult<EmptyIterator> {
            not_supported()
        }

        fn is_preimage_mode(&self) -> bool {
            false
        }
    }

    impl ModelBatch {
        fn remove_slots(&mut self, address: &Address, from: &Hash256, to: &Hash256, all: bool) {
            for slot in &self.model.0.slots {
                let hash = slot_path(slot);
                if all || (hash >= *from && hash <= *to) {
                    self.state.slots.remove(&(*address, *slot));
                }
            }
        }
    }

    impl PersistenceWriteBatch for ModelBatch {
        fn self_destruct(&mut self, address: &Address) -> PersistenceResult<()> {
            self.remove_slots(address, &[0; 32], &[0; 32], true);
            Ok(())
        }

        fn set_account(&mut self, address: &Address, account: Option<&Account>) -> PersistenceResult<()> {
            match account {
                Some(account) => self.state.accounts.insert(*address, *account),
                None => self.state.accounts.remove(address),
            };
            Ok(())
        }

        fn set_storage(&mut self, address: &Address, slot: &U256, value: Option<&U256>) -> PersistenceResult<()> {
            match value {
                Some(value) => self.state.slots.insert((*address, *slot), *value),
                None => self.state.slots.remove(&(*address, *slot)),
            };
            Ok(())
        }

        fn set_state_trie_node(&mut self, _: &TreePath, _: &[u8]) -> PersistenceResult<()> {
            Ok(())
        }

        fn set_storage_trie_node(&mut self, _: &Hash256, _: &TreePath, _: &[u8]) -> PersistenceResult<()> {
            Ok(())
        }

        fn set_storage_raw_encoded(
            &mut self,
            address_hash: &Hash256,
            slot_hash: &Hash256,
            rlp_value: &[u8],
        ) -> PersistenceResult<()> {
            let key = (
                self.model.0.addresses_by_hash[address_hash],
                self.model.0.slots_by_hash[slot_hash],
            );
            self.state.slots.insert(key, U256::from_be_slice(rlp_value));
            Ok(())
        }

        fn set_account_raw(&mut self, address_hash: &Hash256, account: &Account) -> PersistenceResult<()> {
            self.state
                .accounts
                .insert(self.model.0.addresses_by_hash[address_hash], *account);
            Ok(())
        }

        fn delete_account_range(&mut self, from: &Hash256, to: &Hash256) -> PersistenceResult<()> {
            for address in &self.model.0.addresses {
                let hash = account_path(address);
                if hash >= *from && hash <= *to {
                    self.state.accounts.remove(address);
                }
            }
            Ok(())
        }

        /// A zero upper bound stands for the whole range here.
        fn delete_storage_range(
            &mut self,
            address_hash: &Hash256,
            from: &Hash256,
            to: &Hash256,
        ) -> PersistenceResult<()> {
            let address = self.model.0.addresses_by_hash[address_hash];
            self.remove_slots(&address, from, to, *to == [0; 32]);
            Ok(())
        }

        fn delete_state_trie_node_range(&mut self, _: &Hash256, _: &Hash256) -> PersistenceResult<()> {
            Ok(())
        }

        fn delete_storage_trie_node_range(&mut self, _: &Hash256, _: &Hash256, _: &Hash256) -> PersistenceResult<()> {
            Ok(())
        }

        fn commit(self) -> PersistenceResult<()> {
            *self.model.0.state.write() = Arc::new(self.state);
            Ok(())
        }
    }

    /// Port of `RandomOperations_EveryReadMatchesThePersistenceAtTheReaderState`.
    #[rstest]
    fn random_operations_read_the_state_of_their_reader(#[values(4, 8, DEFAULT_SLOT_CAPACITY)] capacity: usize) {
        const OPERATIONS: usize = 1_000_000;
        const MAX_OPEN_READERS: usize = 8;
        let mut random = TestRng::new(capacity as u64);
        let model = Model::new(4, 8);
        let cache = Cache::with_capacity(model.clone(), capacity, capacity).unwrap();
        cache.metrics().detailed_metrics_enabled.store(true, Ordering::Relaxed);
        let mut readers = Vec::new();
        let mut mismatches = Vec::new();
        let mut reads = 0;

        for operation in 0..OPERATIONS {
            if mismatches.len() >= 10 {
                break;
            }
            match random.below(1000) {
                0..850 => {
                    if readers.is_empty() || random.below(4) == 0 {
                        if readers.len() == MAX_OPEN_READERS {
                            readers.remove(0);
                        }
                        let reader = cache.create_reader(ReaderFlags::NONE).unwrap();
                        readers.push((reader, last_reader_state()));
                    }
                    let index = if random.below(2) == 0 {
                        readers.len() - 1
                    } else {
                        random.below(readers.len())
                    };
                    let (reader, state) = &readers[index];
                    reads += 1;
                    if let Some(mismatch) = model.check_read(reader, state, &mut random) {
                        mismatches.push(format!("operation {operation}: {mismatch}"));
                    }
                }
                850..880 => {
                    if !readers.is_empty() {
                        let index = random.below(readers.len());
                        readers.remove(index);
                    }
                }
                880..998 => model.commit_random_batch(&cache, &mut random, 0.03),
                _ => {
                    // Snap sync clears the persistence before anything reads it, so no reader spans the clear.
                    readers.clear();
                    cache.clear().unwrap();
                }
            }
        }
        readers.clear();

        assert!(mismatches.is_empty(), "{mismatches:#?}");
        assert!(reads > OPERATIONS / 2);
        assert!(
            cache.metrics().slot_hits.load(Ordering::Relaxed) > reads as i64 / 100,
            "the cache served a share of the reads"
        );
        assert!(cache.slot_table().count() as usize <= cache.slot_table().capacity());
    }

    /// Port of `ConcurrentReadersAndCommitter_ReadEachReaderState`.
    #[rstest]
    #[case(64, 500)]
    #[case(DEFAULT_SLOT_CAPACITY, 30_000)]
    fn concurrent_readers_read_the_state_of_their_reader(#[case] capacity: usize, #[case] commits: usize) {
        const READER_THREADS: usize = 16;
        const READS_PER_READER: usize = 64;
        let model = Model::new(8, 16);
        let cache = Cache::with_capacity(model.clone(), capacity, capacity).unwrap();
        let start = Barrier::new(READER_THREADS + 1);
        let committed = AtomicBool::new(false);
        let mismatches = Mutex::new(Vec::new());

        std::thread::scope(|scope| {
            scope.spawn(|| {
                let mut random = TestRng::new(1);
                start.wait();
                for _ in 0..commits {
                    model.commit_random_batch(&cache, &mut random, 0.005);
                    for _ in 0..random.below(2000) {
                        std::hint::spin_loop();
                    }
                }
                committed.store(true, Ordering::Release);
            });

            for thread in 0..READER_THREADS {
                let (model, cache, start, committed, mismatches) = (&model, &cache, &start, &committed, &mismatches);
                scope.spawn(move || {
                    let mut random = TestRng::new(thread as u64 + 2);
                    start.wait();
                    loop {
                        let reader = cache.create_reader(ReaderFlags::NONE).unwrap();
                        let state = last_reader_state();
                        for _ in 0..READS_PER_READER {
                            if let Some(mismatch) = model.check_read(&reader, &state, &mut random) {
                                mismatches.lock().push(mismatch);
                            }
                        }
                        if committed.load(Ordering::Acquire) {
                            break;
                        }
                    }
                });
            }
        });

        let mismatches = mismatches.into_inner();
        assert!(
            mismatches.is_empty(),
            "{} mismatches, first: {:?}",
            mismatches.len(),
            mismatches.first()
        );
    }

    #[test]
    fn slot_hash_spreads_small_slots_over_sets_and_tags() {
        let tags: std::collections::HashSet<u64> = (0..1024u64)
            .map(|slot| slot_table::tag_of(slot_table::hash(&ADDRESS, &U256::from_u64(slot))))
            .collect();
        assert!(tags.len() > 1000, "{} distinct tags", tags.len());
    }
}
