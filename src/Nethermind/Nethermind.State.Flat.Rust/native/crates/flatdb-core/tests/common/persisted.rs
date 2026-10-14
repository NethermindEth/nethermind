//! Persisted-tier test support: snapshots from the C# record stream, and persisted snapshots held in memory (a table
//! buffer, a bloom, and blob files shared by the tier).

use std::collections::BTreeMap;
use std::ops::{Deref, DerefMut};
use std::path::Path;
use std::sync::{Arc, Mutex, RwLock};

use flatdb_core::bloom::BloomFilter;
use flatdb_core::persisted::bloom_keys::PersistedBloom;
use flatdb_core::persisted::builder::{self, BuildOutput};
use flatdb_core::persisted::compactor::{CompactorConfig, CompactorHooks, PersistedSnapshotCompactor};
use flatdb_core::persisted::io::ByteReader;
use flatdb_core::persisted::loader::PersistedSnapshotLoader;
use flatdb_core::persisted::merger;
use flatdb_core::persisted::reader::{BlobSource, TableView};
use flatdb_core::persisted::snapshot::{PersistedSnapshot, SnapshotHeader};
use flatdb_core::persisted::stack::PersistedLayer;
use flatdb_core::persisted::storage::arena_manager::{ArenaConfig, ArenaManager};
use flatdb_core::persisted::storage::blob_manager::BlobManager;
use flatdb_core::persisted::storage::blob_writer::BlobWriter;
use flatdb_core::persisted::storage::catalog::{CatalogKv, MemCatalogKv, SnapshotCatalog};
use flatdb_core::persisted::{PersistedTierConfig, TierError, TierResult};
use flatdb_core::repository::{SnapshotRepository, StateHeaderProvider};
use flatdb_core::schedule::CompactionSchedule;
use flatdb_core::snapshot::content::SnapshotContentBuilder;
use flatdb_core::snapshot::{Snapshot, SnapshotTier};
use flatdb_core::stats::{FlatStats, TierStats};
use flatdb_core::types::{Account, Address, Hash256, StateId, TreePath, U256};

use super::snapshots::FixedFinality;

/// The blob files of a tier, by id, each from offset 0.
pub type Blobs = Arc<RwLock<MemBlobs>>;

/// Blob files held in memory, by id, each from offset 0.
#[derive(Default)]
pub struct MemBlobs(pub BTreeMap<u16, Vec<u8>>);

impl Deref for MemBlobs {
    type Target = BTreeMap<u16, Vec<u8>>;

    fn deref(&self) -> &Self::Target {
        &self.0
    }
}

impl DerefMut for MemBlobs {
    fn deref_mut(&mut self) -> &mut Self::Target {
        &mut self.0
    }
}

impl BlobSource for MemBlobs {
    fn random_read(&self, blob_arena_id: u16, offset: i32, buffer: &mut [u8]) -> TierResult<usize> {
        let file = self
            .get(&blob_arena_id)
            .ok_or_else(|| TierError::InvalidOperation(format!("no blob file {blob_arena_id}")))?;
        let start = usize::try_from(offset).unwrap_or(usize::MAX).min(file.len());
        let read = buffer.len().min(file.len() - start);
        buffer[..read].copy_from_slice(&file[start..start + read]);
        Ok(read)
    }
}

/// A snapshot from the record stream of `flatdb-ffi/src/snapshot_stream.rs`, which the C# golden generator writes.
pub fn snapshot_from_records(from: StateId, to: StateId, records: &[u8]) -> Snapshot {
    let mut reader = FieldReader {
        bytes: records,
        position: 0,
    };
    let mut builder = SnapshotContentBuilder::new();
    while reader.position < records.len() {
        match reader.u8() {
            0 => {
                let address = reader.address();
                builder.set_account(address, Some(reader.account()));
            }
            1 => builder.set_account(reader.address(), None),
            2 => {
                let (address, slot, value) = (reader.address(), reader.u256(), reader.u256());
                builder.set_storage(address, slot, Some(value));
            }
            3 => {
                let (address, slot) = (reader.address(), reader.u256());
                builder.set_storage(address, slot, None);
            }
            4 => {
                let address = reader.address();
                builder.set_self_destruct(address, reader.u8() != 0);
            }
            5 => {
                let (path, hash) = (reader.path(), reader.hash());
                builder.set_state_node(path, hash, reader.bytes());
            }
            6 => {
                let (address_hash, path, hash) = (reader.hash(), reader.path(), reader.hash());
                builder.set_storage_node(address_hash, path, hash, reader.bytes());
            }
            tag => panic!("unknown record tag {tag}"),
        }
    }
    Snapshot::new(from, to, builder.build())
}

struct FieldReader<'a> {
    bytes: &'a [u8],
    position: usize,
}

impl<'a> FieldReader<'a> {
    fn take(&mut self, count: usize) -> &'a [u8] {
        let taken = &self.bytes[self.position..self.position + count];
        self.position += count;
        taken
    }

    fn u8(&mut self) -> u8 {
        self.take(1)[0]
    }

    fn u64(&mut self) -> u64 {
        u64::from_le_bytes(self.take(8).try_into().unwrap())
    }

    fn address(&mut self) -> Address {
        self.take(20).try_into().unwrap()
    }

    fn hash(&mut self) -> Hash256 {
        self.take(32).try_into().unwrap()
    }

    fn u256(&mut self) -> U256 {
        U256([self.u64(), self.u64(), self.u64(), self.u64()])
    }

    fn account(&mut self) -> Account {
        Account::with_hashes(self.u64(), self.u256(), self.hash(), self.hash())
    }

    fn path(&mut self) -> TreePath {
        let path = self.hash();
        TreePath::new(path, self.u8())
    }

    fn bytes(&mut self) -> &'a [u8] {
        let length = u32::from_le_bytes(self.take(4).try_into().unwrap()) as usize;
        self.take(length)
    }
}

/// A persisted snapshot in memory: its table bytes, its bloom (an `Arc`, shared as C# shares a
/// `RefCountedBloomFilter`), and the tier's blob files.
pub struct MemSnapshot {
    pub from: StateId,
    pub to: StateId,
    pub tier: SnapshotTier,
    pub table: Vec<u8>,
    pub bloom: Arc<BloomFilter>,
    pub blobs: Blobs,
}

impl MemSnapshot {
    /// A twin over the same table with another bloom, as `ShareBloomAt` registers.
    pub fn with_bloom(&self, bloom: Arc<BloomFilter>) -> Self {
        Self {
            from: self.from,
            to: self.to,
            tier: self.tier,
            table: self.table.clone(),
            bloom,
            blobs: self.blobs.clone(),
        }
    }

    fn view<T>(&self, read: impl FnOnce(&TableView<'_, '_>) -> TierResult<T>) -> TierResult<T> {
        let blobs = self.blobs.read().unwrap();
        let reader = ByteReader::new(&self.table);
        read(&TableView::new(&reader, &*blobs))
    }
}

impl PersistedLayer for MemSnapshot {
    fn bloom(&self) -> &BloomFilter {
        &self.bloom
    }

    fn try_get_account(&self, address: &Address) -> TierResult<Option<Option<Account>>> {
        self.view(|view| view.try_get_account(address))
    }

    fn try_get_self_destruct_flag(&self, address: &Address) -> TierResult<Option<bool>> {
        self.view(|view| view.try_get_self_destruct_flag(address))
    }

    fn try_get_slot(&self, address: &Address, slot: &U256) -> TierResult<Option<Option<U256>>> {
        self.view(|view| view.try_get_slot(address, slot))
    }

    fn try_load_state_node_rlp(&self, path: &TreePath) -> TierResult<Option<Vec<u8>>> {
        self.view(|view| view.try_load_state_node_rlp(path))
    }

    fn try_load_storage_node_rlp(&self, address_hash: &Hash256, path: &TreePath) -> TierResult<Option<Vec<u8>>> {
        self.view(|view| view.try_load_storage_node_rlp(address_hash, path))
    }
}

/// A base snapshot built from `snapshot`, its RLP appended to blob file `blob_id`, with the 1-line bloom C# converts
/// with (or the always-true one when `bits_per_key` is 0).
pub fn convert(snapshot: &Snapshot, blobs: &Blobs, blob_id: u16, bits_per_key: f64) -> (MemSnapshot, BuildOutput) {
    let mut bloom = if bits_per_key > 0.0 {
        BloomFilter::new(1, bits_per_key).unwrap()
    } else {
        BloomFilter::always_true()
    };
    let start = blobs.read().unwrap().get(&blob_id).map_or(0, Vec::len) as i64;
    let mut blob_writer = BlobWriter::new(Vec::new(), blob_id, start);
    let mut table = Vec::new();
    let output = builder::build(snapshot, &mut table, &mut blob_writer, &mut bloom).unwrap();
    let appended = blob_writer.finish().unwrap();
    blobs
        .write()
        .unwrap()
        .entry(blob_id)
        .or_default()
        .extend_from_slice(&appended);
    let persisted = MemSnapshot {
        from: snapshot.from(),
        to: snapshot.to(),
        tier: SnapshotTier::PersistedBase,
        table,
        bloom: Arc::new(bloom),
        blobs: blobs.clone(),
    };
    (persisted, output)
}

/// The merge of `sources`, oldest first, into a snapshot of `tier` with `bloom`.
pub fn merge(sources: &[&MemSnapshot], tier: SnapshotTier, mut bloom: BloomFilter) -> MemSnapshot {
    let readers = sources
        .iter()
        .map(|source| ByteReader::new(&source.table))
        .collect::<Vec<_>>();
    let mut table = Vec::new();
    merger::merge(&readers, &mut table, &mut bloom).unwrap();
    MemSnapshot {
        from: sources[0].from,
        to: sources[sources.len() - 1].to,
        tier,
        table,
        bloom: Arc::new(bloom),
        blobs: sources[0].blobs.clone(),
    }
}

pub fn new_blobs() -> Blobs {
    Arc::new(RwLock::new(MemBlobs::default()))
}

/// A persisted tier over files in `{base}/persistedSnapshot/{arena,blob}` and an in-memory catalog: the C#
/// `FlatTestContainer`'s tier. Dropping it releases everything; [`StorageTier::shut_down`] first keeps the files of the
/// live snapshots, as the C# teardown does.
pub struct StorageTier {
    // Dropped in this order, as the C# container disposes them.
    pub loader: PersistedSnapshotLoader,
    pub repository: Arc<SnapshotRepository>,
    pub arena: Arc<ArenaManager>,
    pub blobs: Arc<BlobManager>,
    pub catalog: Arc<SnapshotCatalog>,
    pub stats: Arc<TierStats>,
    pub config: PersistedTierConfig,
}

impl StorageTier {
    /// The tier of `base`, loaded from the rows of `catalog_kv`.
    pub fn open(
        base: &Path,
        catalog_kv: &MemCatalogKv,
        arena_config: ArenaConfig,
        config: PersistedTierConfig,
        stats: Arc<TierStats>,
    ) -> TierResult<Self> {
        Self::open_with_catalog(
            base,
            &catalog_kv.rows(),
            Box::new(catalog_kv.clone()),
            arena_config,
            config,
            stats,
            Arc::new(FixedFinality::default()),
        )
    }

    /// The tier of `base`, loaded from `catalog_rows` and writing its catalog through `catalog_kv`, its repository
    /// asking `finality` for the canonical roots and queueing the latest observations of its metrics.
    pub fn open_with_catalog(
        base: &Path,
        catalog_rows: &[(Vec<u8>, Vec<u8>)],
        catalog_kv: Box<dyn CatalogKv>,
        arena_config: ArenaConfig,
        config: PersistedTierConfig,
        stats: Arc<TierStats>,
        finality: Arc<dyn StateHeaderProvider>,
    ) -> TierResult<Self> {
        let directory = base.join("persistedSnapshot");
        let arena = Arc::new(ArenaManager::new(
            &directory.join("arena"),
            arena_config,
            stats.counters.clone(),
        )?);
        let blobs = BlobManager::new(
            &directory.join("blob"),
            arena_config.arena_file_size_bytes,
            stats.counters.clone(),
        )?;
        let catalog = Arc::new(SnapshotCatalog::new(catalog_kv));
        let repository = Arc::new(SnapshotRepository::with_persisted_tier(
            finality,
            Arc::new(FlatStats::new(64)),
            catalog.clone(),
            stats.clone(),
        ));
        let loader = PersistedSnapshotLoader::new(
            repository.clone(),
            arena.clone(),
            blobs.clone(),
            catalog.clone(),
            stats.clone(),
            config,
            None,
        );
        loader.load(&pack_rows(catalog_rows))?;
        Ok(Self {
            loader,
            repository,
            arena,
            blobs,
            catalog,
            stats,
            config,
        })
    }

    /// Keeps the files of every live snapshot, then releases the tier; dropping the tier does the same.
    pub fn shut_down(self) {
        self.loader.mark_for_shutdown();
    }

    pub fn convert(&self, snapshot: &Snapshot) -> Arc<PersistedSnapshot> {
        self.loader.convert_and_register(snapshot).unwrap();
        self.repository
            .try_lease_base_persisted_snapshot(&snapshot.to())
            .unwrap()
    }

    pub fn compactor(&self, schedule: CompactionSchedule) -> PersistedSnapshotCompactor {
        self.compactor_recording_panics(schedule, Arc::default())
    }

    /// A compactor over the tier with `schedule`, recording the jobs that panicked in `panicked`.
    pub fn compactor_recording_panics(
        &self,
        schedule: CompactionSchedule,
        panicked: Arc<Mutex<Vec<String>>>,
    ) -> PersistedSnapshotCompactor {
        PersistedSnapshotCompactor::new(
            self.repository.clone(),
            self.arena.clone(),
            self.blobs.clone(),
            self.catalog.clone(),
            self.stats.clone(),
            CompactorConfig {
                schedule,
                tier: self.config,
            },
            CompactorHooks {
                on_fatal_error: Box::new(|_| {}),
                on_job_panic: Box::new(move |job| panicked.lock().unwrap().push(job.to_owned())),
                faults: None,
            },
        )
    }
}

/// A tier in a temporary directory over an in-memory catalog, with 64 KiB arena files and 14 bloom bits per key; the
/// directory goes with it.
pub struct TemporaryTier {
    // Dropped in this order: the tier keeps its files on shutdown, which the directory then takes.
    pub tier: StorageTier,
    pub catalog_kv: MemCatalogKv,
    /// An empty base no bucket holds, whose table the snapshots [`TemporaryTier::register`] indexes share.
    template: Arc<PersistedSnapshot>,
    _directory: tempfile::TempDir,
}

impl TemporaryTier {
    pub fn new() -> Self {
        Self::with_finality(Arc::new(FixedFinality::default()))
    }

    pub fn with_finality(finality: Arc<dyn StateHeaderProvider>) -> Self {
        Self::with_catalog(finality, |catalog_kv| Box::new(catalog_kv))
    }

    /// The tier writing its catalog through what `wrap_catalog` makes of the in-memory rows, its repository asking
    /// `finality` for the canonical roots.
    pub fn with_catalog(
        finality: Arc<dyn StateHeaderProvider>,
        wrap_catalog: impl FnOnce(MemCatalogKv) -> Box<dyn CatalogKv>,
    ) -> Self {
        let directory = tempfile::tempdir().unwrap();
        let catalog_kv = MemCatalogKv::default();
        let config = PersistedTierConfig {
            bloom_bits_per_key: 14.0,
            validate_persisted_snapshot: false,
            processor_count: 4,
        };
        let tier = StorageTier::open_with_catalog(
            directory.path(),
            &[],
            wrap_catalog(catalog_kv.clone()),
            super::arena::config(64 * 1024),
            config,
            Arc::default(),
            finality,
        )
        .unwrap();
        let template_to = StateId::new(u64::MAX >> 1, [0xEE; 32]);
        let template = tier.convert(&Snapshot::new(
            StateId::new(template_to.block_number - 1, [0xEE; 32]),
            template_to,
            SnapshotContentBuilder::new().build(),
        ));
        assert!(tier.repository.remove_persisted_state_exact(&template_to).unwrap());
        Self {
            tier,
            catalog_kv,
            template,
            _directory: directory,
        }
    }

    /// Indexes an empty persisted snapshot of `tier` from `from` to `to`, without a catalog row: an edge for the walks.
    pub fn register(&self, from: StateId, to: StateId, tier: SnapshotTier) {
        let snapshot = PersistedSnapshot::open(
            SnapshotHeader {
                from,
                to,
                tier,
                may_have_self_destructs: false,
            },
            self.template.reservation().clone(),
            PersistedBloom::always_true(self.tier.stats.counters.clone()),
            self.tier.blobs.clone(),
            self.tier.stats.clone(),
        )
        .unwrap();
        self.tier.repository.add_persisted_snapshot(Arc::new(snapshot));
    }
}

impl Deref for TemporaryTier {
    type Target = StorageTier;

    fn deref(&self) -> &StorageTier {
        &self.tier
    }
}

/// Catalog rows packed as `codec::parse_rows` reads them.
pub fn pack_rows(rows: &[(Vec<u8>, Vec<u8>)]) -> Vec<u8> {
    rows.iter()
        .flat_map(|(key, value)| {
            [
                &(key.len() as u32).to_le_bytes()[..],
                key,
                &(value.len() as u32).to_le_bytes(),
                value,
            ]
            .concat()
        })
        .collect()
}
