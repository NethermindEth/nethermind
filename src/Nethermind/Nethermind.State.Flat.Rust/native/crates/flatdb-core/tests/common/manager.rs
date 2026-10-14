//! Persistence manager and manager test support: a scriptable host, a persistence recording what is asked of it, and
//! the components wired over a real persistence on any backend, with long finality over a persisted tier in a
//! temporary directory whose compactor only records what it is handed, as the C# tests substitute it.

use std::collections::VecDeque;
use std::sync::Arc;
use std::sync::atomic::{AtomicU64, AtomicUsize, Ordering};

use crossbeam_channel::{Receiver, Sender};
use flatdb_core::compactor::SnapshotCompactor;
use flatdb_core::events::PersistedNodeEvents;
use flatdb_core::host::{Host, HostError};
use flatdb_core::kv::{ReadFlags, WriteFlags};
use flatdb_core::manager::{FlatDbManager, ManagerConfig};
use flatdb_core::persisted::PersistedTierConfig;
use flatdb_core::persisted::compactor::{EnqueueError, PersistedCompactor};
use flatdb_core::persisted::snapshot::PersistedSnapshot;
use flatdb_core::persisted::storage::catalog::{CatalogKv, MemCatalogKv};
use flatdb_core::persisted::tier::{PersistedTier, TierHooks, TierSettings};
use flatdb_core::persistence::layouts::KvPersistence;
use flatdb_core::persistence::{
    Persistence, PersistenceError, PersistenceReader, PersistenceResult, PersistenceWriteBatch, ReaderFlags,
};
use flatdb_core::persistence_manager::{
    LongFinalityConfig, PersistenceManager, PersistenceManagerConfig, SnapshotAction,
};
use flatdb_core::repository::{SnapshotRepository, StateHeaderProvider};
use flatdb_core::schedule::CompactionSchedule;
use flatdb_core::snapshot::{Snapshot, SnapshotTier};
use flatdb_core::stats::{FlatStats, TierStats};
use flatdb_core::types::{Account, Address, EMPTY_TREE_HASH, FlatLayout, Hash256, StateId, TreePath, U256};
use hashbrown::HashMap;
use parking_lot::Mutex;

use super::any_kv::{AnyKv, Backend, TestStore};
use super::snapshots::{address, snapshot, state_id};

/// `Block0` of the C# tests.
pub const BLOCK0: StateId = StateId::new(0, EMPTY_TREE_HASH);

/// A host after `TestFinalizedStateProvider`: canonical roots by block regardless of the finalized block, a lookup
/// ceiling, lookup counts, and flushes that can be held or failed.
pub struct FakeHost {
    finalized_block_number: AtomicU64,
    roots: Mutex<HashMap<u64, Hash256>>,
    root_lookup_ceiling: AtomicU64,
    lookups: Mutex<HashMap<u64, usize>>,
    finalized_number_reads: AtomicUsize,
    flushes: AtomicUsize,
    flush_failures: Mutex<VecDeque<HostError>>,
    held_flush: Mutex<Option<HostSideOfHeldFlush>>,
}

struct HostSideOfHeldFlush {
    started: Sender<()>,
    result: Receiver<Result<(), HostError>>,
}

impl Default for FakeHost {
    fn default() -> Self {
        Self {
            finalized_block_number: AtomicU64::new(0),
            roots: Mutex::default(),
            root_lookup_ceiling: AtomicU64::new(u64::MAX),
            lookups: Mutex::default(),
            finalized_number_reads: AtomicUsize::new(0),
            flushes: AtomicUsize::new(0),
            flush_failures: Mutex::default(),
            held_flush: Mutex::default(),
        }
    }
}

/// The far end of a held flush: `started` fires when the flush begins, which then returns what `release` sends.
pub struct HeldFlush {
    pub started: Receiver<()>,
    pub release: Sender<Result<(), HostError>>,
}

impl FakeHost {
    pub fn set_finalized_block_number(&self, block_number: u64) {
        self.finalized_block_number.store(block_number, Ordering::SeqCst);
    }

    pub fn set_finalized_state_root_at(&self, block_number: u64, root: Hash256) {
        self.roots.lock().insert(block_number, root);
    }

    pub fn set_root_lookup_ceiling(&self, block_number: u64) {
        self.root_lookup_ceiling.store(block_number, Ordering::SeqCst);
    }

    pub fn lookup_count(&self, block_number: u64) -> usize {
        self.lookups.lock().get(&block_number).copied().unwrap_or(0)
    }

    pub fn finalized_number_reads(&self) -> usize {
        self.finalized_number_reads.load(Ordering::SeqCst)
    }

    pub fn flushes(&self) -> usize {
        self.flushes.load(Ordering::SeqCst)
    }

    /// The next flushes fail with `errors`, in order.
    pub fn fail_next_flushes(&self, errors: impl IntoIterator<Item = HostError>) {
        self.flush_failures.lock().extend(errors);
    }

    /// The next flush blocks until released.
    pub fn hold_next_flush(&self) -> HeldFlush {
        let (started_sender, started) = crossbeam_channel::bounded(1);
        let (release, result) = crossbeam_channel::bounded(1);
        *self.held_flush.lock() = Some(HostSideOfHeldFlush {
            started: started_sender,
            result,
        });
        HeldFlush { started, release }
    }
}

impl StateHeaderProvider for FakeHost {
    fn finalized_block_number(&self) -> u64 {
        self.finalized_number_reads.fetch_add(1, Ordering::SeqCst);
        self.finalized_block_number.load(Ordering::SeqCst)
    }

    fn finalized_state_root(&self, block_number: u64) -> Option<Hash256> {
        *self.lookups.lock().entry(block_number).or_default() += 1;
        if block_number > self.root_lookup_ceiling.load(Ordering::SeqCst) {
            return None;
        }
        self.roots.lock().get(&block_number).copied()
    }
}

impl Host for FakeHost {
    fn flush_deferred(&self) -> Result<(), HostError> {
        self.flushes.fetch_add(1, Ordering::SeqCst);
        let held = self.held_flush.lock().take();
        if let Some(held) = held {
            held.started.send(()).unwrap();
            return held.result.recv().unwrap_or(Err(HostError::Cancelled));
        }
        match self.flush_failures.lock().pop_front() {
            Some(error) => Err(error),
            None => Ok(()),
        }
    }
}

/// A call made to a [`Recording`] persistence or one of its batches.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Op {
    CreateBatch { from: StateId, to: StateId, flags: u32 },
    SelfDestruct(Address),
    SetAccount(Address, Option<Account>),
    SetStorage(Address, U256, Option<U256>),
    SetStateNode(TreePath, Vec<u8>),
    SetStorageNode(Hash256, TreePath, Vec<u8>),
    Other(&'static str),
    Commit,
}

/// A persistence passing everything to `inner`, recording the batches, their operations and the readers. Readers can
/// be scripted to report another current state, the race of a persist landing after a reader was leased.
pub struct Recording<P: Persistence> {
    inner: P,
    ops: Arc<Mutex<Vec<Op>>>,
    reader_flags: Mutex<Vec<ReaderFlags>>,
    live_readers: Arc<AtomicUsize>,
    scripted_states: Mutex<VecDeque<StateId>>,
    storage_write_failure: Arc<Mutex<Option<PersistenceError>>>,
}

impl<P: Persistence> Recording<P> {
    pub fn new(inner: P) -> Self {
        Self {
            inner,
            ops: Arc::default(),
            reader_flags: Mutex::default(),
            live_readers: Arc::default(),
            scripted_states: Mutex::default(),
            storage_write_failure: Arc::default(),
        }
    }

    /// Every slot write of a batch fails from now on.
    pub fn fail_storage_writes(&self) {
        self.fail_storage_writes_with(Some(PersistenceError::InvalidOperation(
            "injected slot write failure".to_owned(),
        )));
    }

    /// Every slot write of a batch fails with `failure` from now on, or none fails again for `None`.
    pub fn fail_storage_writes_with(&self, failure: Option<PersistenceError>) {
        *self.storage_write_failure.lock() = failure;
    }

    pub fn inner(&self) -> &P {
        &self.inner
    }

    pub fn ops(&self) -> Vec<Op> {
        self.ops.lock().clone()
    }

    pub fn clear_ops(&self) {
        self.ops.lock().clear();
        self.reader_flags.lock().clear();
    }

    /// The `(from, to)` of every batch created, in order.
    pub fn batches(&self) -> Vec<(StateId, StateId)> {
        self.ops
            .lock()
            .iter()
            .filter_map(|op| match op {
                Op::CreateBatch { from, to, .. } => Some((*from, *to)),
                _ => None,
            })
            .collect()
    }

    /// The flags of every reader created, in order.
    pub fn reader_flags(&self) -> Vec<ReaderFlags> {
        self.reader_flags.lock().clone()
    }

    pub fn live_readers(&self) -> usize {
        self.live_readers.load(Ordering::SeqCst)
    }

    /// The next readers report these current states instead of the persisted one.
    pub fn script_reader_states(&self, states: impl IntoIterator<Item = StateId>) {
        self.scripted_states.lock().extend(states);
    }
}

impl<P: Persistence> Persistence for Recording<P> {
    type Reader = RecordingReader<P::Reader>;
    type WriteBatch = RecordingBatch<P::WriteBatch>;

    fn create_reader(&self, flags: ReaderFlags) -> PersistenceResult<Self::Reader> {
        let inner = self.inner.create_reader(flags)?;
        self.reader_flags.lock().push(flags);
        self.live_readers.fetch_add(1, Ordering::SeqCst);
        let current_state = self
            .scripted_states
            .lock()
            .pop_front()
            .unwrap_or_else(|| inner.current_state());
        Ok(RecordingReader {
            inner,
            current_state,
            live_readers: Arc::clone(&self.live_readers),
        })
    }

    fn create_write_batch(
        &self,
        from: &StateId,
        to: &StateId,
        flags: WriteFlags,
    ) -> PersistenceResult<Self::WriteBatch> {
        let inner = self.inner.create_write_batch(from, to, flags)?;
        self.ops.lock().push(Op::CreateBatch {
            from: *from,
            to: *to,
            flags: flags.bits(),
        });
        Ok(RecordingBatch {
            inner,
            ops: Arc::clone(&self.ops),
            storage_write_failure: Arc::clone(&self.storage_write_failure),
        })
    }

    fn flush(&self) -> PersistenceResult<()> {
        self.inner.flush()
    }

    fn clear(&self) -> PersistenceResult<()> {
        self.inner.clear()
    }
}

pub struct RecordingReader<R> {
    inner: R,
    current_state: StateId,
    live_readers: Arc<AtomicUsize>,
}

impl<R> Drop for RecordingReader<R> {
    fn drop(&mut self) {
        self.live_readers.fetch_sub(1, Ordering::SeqCst);
    }
}

impl<R: PersistenceReader> PersistenceReader for RecordingReader<R> {
    type Iter = R::Iter;

    fn get_account(&self, address: &Address) -> PersistenceResult<Option<Account>> {
        self.inner.get_account(address)
    }

    fn try_get_slot(&self, address: &Address, slot: &U256) -> PersistenceResult<Option<U256>> {
        self.inner.try_get_slot(address, slot)
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
        self.inner.load_state_rlp(path, flags, read)
    }

    fn load_storage_rlp<T>(
        &self,
        address_hash: &Hash256,
        path: &TreePath,
        flags: ReadFlags,
        read: impl FnOnce(&[u8]) -> T,
    ) -> PersistenceResult<Option<T>> {
        self.inner.load_storage_rlp(address_hash, path, flags, read)
    }

    fn get_account_raw(&self, address_hash: &Hash256) -> PersistenceResult<Option<Vec<u8>>> {
        self.inner.get_account_raw(address_hash)
    }

    fn try_get_storage_raw(&self, address_hash: &Hash256, slot_hash: &Hash256) -> PersistenceResult<Option<U256>> {
        self.inner.try_get_storage_raw(address_hash, slot_hash)
    }

    fn create_account_iterator(&self, start_key: &Hash256, end_key: &Hash256) -> PersistenceResult<R::Iter> {
        self.inner.create_account_iterator(start_key, end_key)
    }

    fn create_storage_iterator(
        &self,
        account_key: &Hash256,
        start_slot_key: &Hash256,
        end_slot_key: &Hash256,
    ) -> PersistenceResult<R::Iter> {
        self.inner
            .create_storage_iterator(account_key, start_slot_key, end_slot_key)
    }

    fn is_preimage_mode(&self) -> bool {
        self.inner.is_preimage_mode()
    }
}

pub struct RecordingBatch<B> {
    inner: B,
    ops: Arc<Mutex<Vec<Op>>>,
    storage_write_failure: Arc<Mutex<Option<PersistenceError>>>,
}

impl<B> RecordingBatch<B> {
    fn record(&self, op: Op) {
        self.ops.lock().push(op);
    }
}

impl<B: PersistenceWriteBatch> PersistenceWriteBatch for RecordingBatch<B> {
    fn self_destruct(&mut self, address: &Address) -> PersistenceResult<()> {
        self.record(Op::SelfDestruct(*address));
        self.inner.self_destruct(address)
    }

    fn set_account(&mut self, address: &Address, account: Option<&Account>) -> PersistenceResult<()> {
        self.record(Op::SetAccount(*address, account.copied()));
        self.inner.set_account(address, account)
    }

    fn set_storage(&mut self, address: &Address, slot: &U256, value: Option<&U256>) -> PersistenceResult<()> {
        self.record(Op::SetStorage(*address, *slot, value.copied()));
        if let Some(failure) = self.storage_write_failure.lock().clone() {
            return Err(failure);
        }
        self.inner.set_storage(address, slot, value)
    }

    fn set_state_trie_node(&mut self, path: &TreePath, rlp: &[u8]) -> PersistenceResult<()> {
        self.record(Op::SetStateNode(*path, rlp.to_vec()));
        self.inner.set_state_trie_node(path, rlp)
    }

    fn set_storage_trie_node(&mut self, address_hash: &Hash256, path: &TreePath, rlp: &[u8]) -> PersistenceResult<()> {
        self.record(Op::SetStorageNode(*address_hash, *path, rlp.to_vec()));
        self.inner.set_storage_trie_node(address_hash, path, rlp)
    }

    fn set_storage_raw_encoded(
        &mut self,
        address_hash: &Hash256,
        slot_hash: &Hash256,
        rlp_value: &[u8],
    ) -> PersistenceResult<()> {
        self.record(Op::Other("set_storage_raw_encoded"));
        self.inner.set_storage_raw_encoded(address_hash, slot_hash, rlp_value)
    }

    fn set_account_raw(&mut self, address_hash: &Hash256, account: &Account) -> PersistenceResult<()> {
        self.record(Op::Other("set_account_raw"));
        self.inner.set_account_raw(address_hash, account)
    }

    fn delete_account_range(&mut self, from_path: &Hash256, to_path: &Hash256) -> PersistenceResult<()> {
        self.record(Op::Other("delete_account_range"));
        self.inner.delete_account_range(from_path, to_path)
    }

    fn delete_storage_range(
        &mut self,
        address_hash: &Hash256,
        from_path: &Hash256,
        to_path: &Hash256,
    ) -> PersistenceResult<()> {
        self.record(Op::Other("delete_storage_range"));
        self.inner.delete_storage_range(address_hash, from_path, to_path)
    }

    fn delete_state_trie_node_range(&mut self, from: &Hash256, to: &Hash256) -> PersistenceResult<()> {
        self.record(Op::Other("delete_state_trie_node_range"));
        self.inner.delete_state_trie_node_range(from, to)
    }

    fn delete_storage_trie_node_range(
        &mut self,
        address_hash: &Hash256,
        from: &Hash256,
        to: &Hash256,
    ) -> PersistenceResult<()> {
        self.record(Op::Other("delete_storage_trie_node_range"));
        self.inner.delete_storage_trie_node_range(address_hash, from, to)
    }

    fn commit(self) -> PersistenceResult<()> {
        self.ops.lock().push(Op::Commit);
        self.inner.commit()
    }
}

pub type TestPersistence = Recording<KvPersistence<AnyKv>>;

/// The C# fixture's settings with long finality off: `MaxReorgDepth` takes the 90000 of `LongFinalityMaxReorgDepth`,
/// the backstop those tests run against.
pub const PM_CONFIG: PersistenceManagerConfig = PersistenceManagerConfig {
    min_reorg_depth: 64,
    max_reorg_depth: 90_000,
    max_in_memory_snapshot_bytes: 0,
    long_finality: None,
};

/// The C# fixture's settings: long finality on, `MaxInMemoryBaseSnapshotCount = 128 + 32`.
pub const PM_CONFIG_LF: PersistenceManagerConfig = PersistenceManagerConfig {
    min_reorg_depth: 64,
    max_reorg_depth: 256,
    max_in_memory_snapshot_bytes: 0,
    long_finality: Some(LongFinalityConfig {
        max_reorg_depth: 90_000,
        max_in_memory_base_snapshot_count: 160,
    }),
};

/// [`PM_CONFIG_LF`] with long finality on, else [`PM_CONFIG`]: the same backstop either way.
pub fn pm_config(long_finality: bool) -> PersistenceManagerConfig {
    if long_finality { PM_CONFIG_LF } else { PM_CONFIG }
}

/// What a [`RecordingCompactor`] runs on every batch it is handed.
type OnEnqueue = Box<dyn Fn(&[StateId]) + Send + Sync>;

/// A persisted compactor that only records the batches it is handed, running `on_enqueue` on each first, as the C#
/// tests substitute `IPersistedSnapshotCompactor`.
#[derive(Default)]
pub struct RecordingCompactor {
    batches: Mutex<Vec<(Vec<StateId>, u64)>>,
    on_enqueue: Mutex<Option<OnEnqueue>>,
    exit_signalled: AtomicUsize,
    disposed: AtomicUsize,
}

impl RecordingCompactor {
    pub fn batches(&self) -> Vec<(Vec<StateId>, u64)> {
        self.batches.lock().clone()
    }

    pub fn set_on_enqueue(&self, on_enqueue: impl Fn(&[StateId]) + Send + Sync + 'static) {
        *self.on_enqueue.lock() = Some(Box::new(on_enqueue));
    }

    pub fn disposals(&self) -> usize {
        self.disposed.load(Ordering::SeqCst)
    }

    pub fn exit_signals(&self) -> usize {
        self.exit_signalled.load(Ordering::SeqCst)
    }
}

impl PersistedCompactor for RecordingCompactor {
    /// Refuses as the compactor does once process exit is signalled.
    fn enqueue(&self, batch: Vec<StateId>, persisted_block_number: u64) -> Result<(), EnqueueError> {
        if self.exit_signals() > 0 {
            return Err(EnqueueError::Cancelled);
        }
        if let Some(on_enqueue) = &*self.on_enqueue.lock() {
            on_enqueue(&batch);
        }
        self.batches.lock().push((batch, persisted_block_number));
        Ok(())
    }

    fn signal_exit(&self) {
        self.exit_signalled.fetch_add(1, Ordering::SeqCst);
    }

    fn dispose(&self) {
        self.disposed.fetch_add(1, Ordering::SeqCst);
    }
}

/// The persisted tier of a long-finality [`Fixture`]: 64 KiB arena files, 14 bloom bits per key, validation on, and
/// the real persisted compactor or one that only records.
pub struct FixtureTier {
    pub tier: PersistedTier,
    /// `None` with the real persisted compactor.
    pub recorder: Option<Arc<RecordingCompactor>>,
    pub catalog_kv: MemCatalogKv,
    pub stats: Arc<TierStats>,
    pub directory: tempfile::TempDir,
}

impl FixtureTier {
    /// The recording persisted compactor.
    pub fn recorder(&self) -> &RecordingCompactor {
        self.recorder
            .as_ref()
            .expect("a fixture recording its persisted compactions")
    }

    /// The tier as a persistence manager takes it.
    pub fn persisted_tier(&self) -> PersistedTier {
        PersistedTier {
            arena: Arc::clone(&self.tier.arena),
            loader: Arc::clone(&self.tier.loader),
            compactor: Arc::clone(&self.tier.compactor),
            fatal_error: Arc::clone(&self.tier.fatal_error),
        }
    }
}

/// The settings of a fixture's tier under `directory`.
pub fn tier_settings(directory: &std::path::Path, schedule: CompactionSchedule) -> TierSettings {
    let base = directory.join("persistedSnapshot");
    TierSettings {
        arena_directory: base.join("arena"),
        blob_directory: base.join("blob"),
        arena: super::arena::config(64 * 1024),
        tier: PersistedTierConfig {
            bloom_bits_per_key: 14.0,
            validate_persisted_snapshot: true,
            processor_count: 4,
        },
        schedule,
    }
}

/// The decided in-memory persist; any other decision fails the test.
pub fn in_memory_persist(action: SnapshotAction) -> Arc<Snapshot> {
    match action {
        SnapshotAction::PersistInMemory(snapshot) => snapshot,
        _ => panic!("expected an in-memory persist"),
    }
}

/// The `(from, to)` of a decision and what kind it is: `"memory"`, `"persisted"`, `"compacted"` or `"base"`.
pub fn decision(action: &SnapshotAction) -> (&'static str, StateId, StateId) {
    match action {
        SnapshotAction::PersistInMemory(snapshot) => ("memory", snapshot.from(), snapshot.to()),
        SnapshotAction::PersistPersisted(snapshot) => ("persisted", snapshot.from(), snapshot.to()),
        SnapshotAction::ConvertCompacted(snapshot) => ("compacted", snapshot.from(), snapshot.to()),
        SnapshotAction::ConvertBase(snapshot) => ("base", snapshot.from(), snapshot.to()),
    }
}

pub const COMPACT_SIZE: u64 = 16;

/// What the catalog of a [`Fixture`]'s tier writes through, given its in-memory rows.
pub type WrapCatalog = Arc<dyn Fn(MemCatalogKv) -> Box<dyn CatalogKv> + Send + Sync>;

/// How a [`Fixture`] differs from the C# fixture's.
#[derive(Clone)]
pub struct FixtureOptions {
    pub schedule: CompactionSchedule,
    /// With long finality, the persisted compactor only records the batches it is handed, as C# substitutes it.
    pub record_persisted_compactions: bool,
    /// With long finality, the catalog writes through what this makes of its rows.
    pub wrap_catalog: WrapCatalog,
}

impl Default for FixtureOptions {
    fn default() -> Self {
        Self {
            schedule: CompactionSchedule::new(COMPACT_SIZE, 1 << 20, 0).unwrap(),
            record_persisted_compactions: true,
            wrap_catalog: Arc::new(|rows| Box::new(rows)),
        }
    }
}

/// A repository, persistence manager and recording persistence over a fresh store.
pub struct Fixture {
    pub persistence: Arc<TestPersistence>,
    pub host: Arc<FakeHost>,
    pub repository: Arc<SnapshotRepository>,
    pub events: Arc<PersistedNodeEvents>,
    pub persistence_manager: Arc<PersistenceManager<TestPersistence>>,
    pub schedule: CompactionSchedule,
    /// With long finality on.
    pub tier: Option<FixtureTier>,
    _store: TestStore,
}

impl Fixture {
    /// The components over `backend` with the persisted state at `persisted` (none for `PreGenesis`), `None` when the
    /// backend is unavailable; with long finality in `config`, over a persisted tier.
    pub fn new(backend: Backend, config: PersistenceManagerConfig, persisted: Option<StateId>) -> Option<Self> {
        Self::with_options(backend, config, persisted, FixtureOptions::default())
    }

    /// As [`Fixture::new`], with `options`.
    pub fn with_options(
        backend: Backend,
        config: PersistenceManagerConfig,
        persisted: Option<StateId>,
        options: FixtureOptions,
    ) -> Option<Self> {
        let store = backend.open()?;
        let persistence = KvPersistence::new(store.kv.clone(), FlatLayout::Flat, None).unwrap();
        if let Some(persisted) = persisted {
            persistence
                .create_write_batch(&StateId::PRE_GENESIS, &persisted, WriteFlags::NONE)
                .unwrap()
                .commit()
                .unwrap();
        }
        let persistence = Arc::new(Recording::new(persistence));
        let host = Arc::new(FakeHost::default());
        let stats = Arc::new(FlatStats::new(1024));
        let schedule = options.schedule;
        let (repository, tier) = if config.long_finality.is_some() {
            let (repository, tier) = Self::open_tier(host.clone(), stats, options);
            (repository, Some(tier))
        } else {
            (Arc::new(SnapshotRepository::with_stats(host.clone(), stats)), None)
        };
        let events = Arc::new(PersistedNodeEvents::new(true));
        let persistence_manager = Arc::new(PersistenceManager::new(
            config,
            schedule,
            host.clone(),
            Arc::clone(&persistence),
            Arc::clone(&repository),
            Arc::clone(&events),
            tier.as_ref().map(FixtureTier::persisted_tier),
        ));
        Some(Self {
            persistence,
            host,
            repository,
            events,
            persistence_manager,
            schedule,
            tier,
            _store: store,
        })
    }

    fn open_tier(
        host: Arc<FakeHost>,
        stats: Arc<FlatStats>,
        options: FixtureOptions,
    ) -> (Arc<SnapshotRepository>, FixtureTier) {
        let directory = tempfile::tempdir().unwrap();
        let catalog_kv = MemCatalogKv::default();
        let tier_stats = Arc::new(TierStats::default());
        let (repository, opened) = PersistedTier::open(
            &tier_settings(directory.path(), options.schedule),
            (options.wrap_catalog)(catalog_kv.clone()),
            &[],
            host,
            stats,
            tier_stats.clone(),
            TierHooks {
                on_job_panic: Box::new(|job| panic!("{job} panicked")),
                faults: None,
            },
        )
        .unwrap();
        let recorder = options
            .record_persisted_compactions
            .then(|| Arc::new(RecordingCompactor::default()));
        let tier = match &recorder {
            Some(recorder) => PersistedTier {
                compactor: recorder.clone(),
                ..opened
            },
            None => opened,
        };
        (
            repository,
            FixtureTier {
                tier,
                recorder,
                catalog_kv,
                stats: tier_stats,
                directory,
            },
        )
    }

    /// The persisted tier of a long-finality fixture.
    pub fn tier(&self) -> &FixtureTier {
        self.tier.as_ref().expect("a long-finality fixture")
    }

    /// `PersistBase`: converts a snapshot setting account `0xA1..` to nonce 1, balance 100 straight into the persisted
    /// tier, bypassing the in-memory tier.
    pub fn persist_base(&self, from: StateId, to: StateId) -> Arc<PersistedSnapshot> {
        let converted = snapshot(from, to, |content| {
            content.set_account(address(0xA1), Some(account(100)))
        });
        self.tier().tier.loader.convert_and_register(&converted).unwrap();
        self.repository.try_lease_base_persisted_snapshot(&to).unwrap()
    }

    /// Another persistence manager over the same components, as the C# tests build one with other settings; with long
    /// finality over the fixture's tier.
    pub fn persistence_manager_with(&self, config: PersistenceManagerConfig) -> PersistenceManager<TestPersistence> {
        PersistenceManager::new(
            config,
            self.schedule,
            self.host.clone(),
            Arc::clone(&self.persistence),
            Arc::clone(&self.repository),
            Arc::clone(&self.events),
            config.long_finality.map(|_| self.tier().persisted_tier()),
        )
    }

    /// `CreateSnapshot`: a snapshot setting account `0xA1..` to nonce 1, balance 100, added to the tier and registered.
    pub fn add(&self, from: StateId, to: StateId, compacted: bool) -> Arc<Snapshot> {
        self.add_with_balance(from, to, compacted, 100)
    }

    pub fn add_with_balance(&self, from: StateId, to: StateId, compacted: bool, balance: u64) -> Arc<Snapshot> {
        let added = snapshot(from, to, |content| {
            content.set_account(address(0xA1), Some(Account::new(1, U256::from_u64(balance))))
        });
        let tier = if compacted {
            SnapshotTier::InMemoryCompacted
        } else {
            SnapshotTier::InMemoryBase
        };
        assert!(self.repository.try_add(Arc::clone(&added), tier));
        self.repository.add_state_id(to);
        added
    }

    /// Adds the snapshots of `edges`: `"a-b"` a base snapshot from state `a` to state `b` and `"a=b"` a compacted one,
    /// the states in the notation of [`state`]. Each sets the account to its end block as balance.
    pub fn add_edges(&self, edges: &str) {
        for edge in edges.split_whitespace() {
            let compacted = edge.contains('=');
            let (from, to) = edge.split_once(['-', '=']).unwrap();
            let to = state(to);
            self.add_with_balance(state(from), to, compacted, to.block_number);
        }
    }

    /// The persisted state the database holds.
    pub fn db_state(&self) -> StateId {
        self.persistence
            .inner()
            .create_reader(ReaderFlags::NONE)
            .unwrap()
            .current_state()
    }

    /// A manager over the components with `config`.
    pub fn manager(&self, config: ManagerConfig) -> FlatDbManager<TestPersistence> {
        FlatDbManager::new(
            config,
            Arc::new(SnapshotCompactor::new(self.schedule, Arc::clone(&self.repository))),
            Arc::clone(&self.persistence_manager),
        )
    }
}

/// The account `0xA1..` reads `balance` with nonce 1.
pub fn account(balance: u64) -> Account {
    Account::new(1, U256::from_u64(balance))
}

/// A state in the notation of the edge lists: `"16"` is block 16 with a zero root but for its first byte, `"16r2"` with
/// first byte 2, `"0"` is [`BLOCK0`] and `"pg"` is `PreGenesis`.
pub fn state(text: &str) -> StateId {
    match text {
        "pg" => StateId::PRE_GENESIS,
        "0" => BLOCK0,
        _ => match text.split_once('r') {
            Some((block, root)) => state_id(block.parse().unwrap(), root.parse().unwrap()),
            None => state_id(text.parse().unwrap(), 0),
        },
    }
}

/// Every warning and error the library logged so far in this test binary, through the global log ring, which the first
/// call installs, so a test calls it before logging anything. Records are kept across calls, so tests running in
/// parallel never take each other's.
pub fn logged_warnings() -> Vec<String> {
    static CAPTURED: Mutex<Vec<String>> = Mutex::new(Vec::new());
    static INSTALL: std::sync::Once = std::sync::Once::new();
    INSTALL.call_once(|| {
        flatdb_core::log_ring::install(log::LevelFilter::Warn);
    });

    let mut captured = CAPTURED.lock();
    captured.extend(
        flatdb_core::log_ring::global()
            .drain(usize::MAX)
            .into_iter()
            .map(|record| record.message),
    );
    captured.clone()
}
