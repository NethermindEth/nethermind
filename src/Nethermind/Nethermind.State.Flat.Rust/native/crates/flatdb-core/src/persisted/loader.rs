//! Port of `Nethermind.State.Flat/PersistedSnapshots/PersistedSnapshotLoader.cs`: converts in-memory base snapshots into
//! the persisted tier, opens the tier again at startup, and marks its files to survive shutdown.
//!
//! The catalog rows come from C#, which opens the catalog database and reads them once (`IDb.GetAll`), packed as
//! [`codec::parse_rows`] reads them. A converted snapshot's catalog row is written before it is indexed; indexing cannot
//! fail here, so the C# rollback of the row has nothing to roll back. A failed validation returns
//! [`TierError::Validation`] instead of exiting the process: the host exits, as C# does, after logging the C# line.
//! The progress of a load above 1,024 snapshots is logged once a second as a plain count, without the C# progress bar.

use std::path::Path;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::time::{Duration, Instant};

use parking_lot::Mutex;

use super::bloom_keys::{self, PersistedBloom};
use super::codec::{self, CatalogEntry};
use super::io::TableSink;
use super::snapshot::{PersistedSnapshot, SnapshotHeader};
use super::sorted::table::FORMAT_VERSION;
use super::storage::arena_manager::ArenaManager;
use super::storage::blob_manager::BlobManager;
use super::storage::catalog::SnapshotCatalog;
use super::{PersistedTierConfig, TierError, TierFaults, TierResult, builder, parallel, validate};
use crate::bloom::BloomFilter;
use crate::events::PersistedNode;
use crate::repository::SnapshotRepository;
use crate::snapshot::{Snapshot, SnapshotTier};
use crate::stats::{Histogram, HistogramLabel, TierStats};

/// `ParallelLoadThreshold`: a load of more snapshots logs its progress.
const PARALLEL_LOAD_THRESHOLD: usize = 1024;
const PROGRESS_LOG_INTERVAL: Duration = Duration::from_secs(1);

pub struct PersistedSnapshotLoader {
    repository: Arc<SnapshotRepository>,
    arena: Arc<ArenaManager>,
    blobs: Arc<BlobManager>,
    catalog: Arc<SnapshotCatalog>,
    stats: Arc<TierStats>,
    config: PersistedTierConfig,
    faults: Option<Arc<dyn TierFaults>>,
    marked_for_shutdown: AtomicBool,
}

impl PersistedSnapshotLoader {
    pub fn new(
        repository: Arc<SnapshotRepository>,
        arena: Arc<ArenaManager>,
        blobs: Arc<BlobManager>,
        catalog: Arc<SnapshotCatalog>,
        stats: Arc<TierStats>,
        config: PersistedTierConfig,
        faults: Option<Arc<dyn TierFaults>>,
    ) -> Self {
        Self {
            repository,
            arena,
            blobs,
            catalog,
            stats,
            config,
            faults,
            marked_for_shutdown: AtomicBool::new(false),
        }
    }

    pub fn config(&self) -> PersistedTierConfig {
        self.config
    }

    /// `Load`: registers the blob and arena files on disk, opens a snapshot for every catalog row whose arena file
    /// exists, deletes the blob files none of them names, and rebuilds the blooms of the widest chain.
    ///
    /// The first snapshot is opened alone to check the table format, which every snapshot of a directory shares.
    /// Orphaned bytes are not truncated: a blob file keeps its length, and orphaned arena bytes stay past the frontier,
    /// as a loaded arena file never rejoins the pool of files with room and the next table goes to a new file.
    pub fn load(&self, catalog_rows: &[u8]) -> TierResult<()> {
        self.blobs.initialize()?;
        let mut entries = codec::parse_rows(catalog_rows)?;
        let missing_arenas = self.arena.initialize(&entries)?;
        entries.retain(|entry| !missing_arenas.contains(&entry.location.arena_id));

        self.load_snapshots(&entries)?;
        if let Some(faults) = &self.faults {
            faults.after_load_open();
        }
        self.blobs.sweep_unreferenced();
        self.reconstruct_bloom(&entries)
    }

    fn load_snapshots(&self, entries: &[CatalogEntry]) -> TierResult<()> {
        let Some((first, rest)) = entries.split_first() else {
            return Ok(());
        };
        self.load_snapshot(first, true)?;

        let progress = (entries.len() > PARALLEL_LOAD_THRESHOLD).then(|| LoadProgress::new(entries.len()));
        parallel::for_each_unbalanced(rest, self.config.processor_count, &AtomicBool::new(false), |entry| {
            self.load_snapshot(entry, false)?;
            if let Some(progress) = &progress {
                progress.loaded_one();
            }
            Ok(())
        })
    }

    /// `LoadSnapshot`: the snapshot of a catalog row, with the always-true bloom until the rebuild, indexed without a
    /// catalog write. A loaded snapshot may have self-destructs, as nothing tells otherwise.
    fn load_snapshot(&self, entry: &CatalogEntry, verify_format: bool) -> TierResult<()> {
        let reservation = self.arena.open(&entry.location)?;
        if verify_format {
            let on_disk = (reservation.size() as usize)
                .checked_sub(1)
                .and_then(|last| reservation.reader().read_u8(last))
                .unwrap_or(0);
            if on_disk != FORMAT_VERSION {
                return Err(TierError::InvalidOperation(format!(
                    "Persisted snapshot format mismatch: on-disk v{on_disk}, runtime expects v{FORMAT_VERSION}. \
                     The persistedSnapshot/ directory has an incompatible layout — wipe and resync."
                )));
            }
        }

        let header = SnapshotHeader {
            from: entry.from,
            to: entry.to,
            tier: entry.tier,
            may_have_self_destructs: true,
        };
        let bloom = PersistedBloom::always_true(self.stats.counters.clone());
        let snapshot = PersistedSnapshot::open(header, reservation, bloom, self.blobs.clone(), self.stats.clone())?;
        self.repository.add_persisted_snapshot(Arc::new(snapshot));
        Ok(())
    }

    /// `ReconstructBloom`: a bloom for each snapshot of the widest chain from the newest state down to the oldest
    /// `from`, shared across the snapshots it contains. Snapshots no chain snapshot covers keep the always-true bloom.
    fn reconstruct_bloom(&self, entries: &[CatalogEntry]) -> TierResult<()> {
        let bits_per_key = self.config.bloom_bits_per_key;
        if bits_per_key <= 0.0 || entries.is_empty() {
            return Ok(());
        }
        let Some(head) = self.repository.get_last_snapshot_id() else {
            return Ok(());
        };
        // Signed: a genesis-spanning `from` is PreGenesis, which sorts below genesis.
        let committed = entries
            .iter()
            .map(|entry| entry.from)
            .min_by_key(|from| from.block_number as i64)
            .expect("entries is not empty");
        if head == committed {
            return Ok(());
        }

        let estimated_size = head.block_number.wrapping_sub(committed.block_number).clamp(4, 4096) as usize;
        let widest = self
            .repository
            .assemble_snapshots(&head, &committed, estimated_size)
            .persisted;
        parallel::for_each_unbalanced(
            &widest,
            self.config.processor_count,
            &AtomicBool::new(false),
            |snapshot| {
                let filter = {
                    let session = snapshot.begin_whole_read_session()?;
                    bloom_keys::rebuild(&session.reader(), bits_per_key)?
                };
                let bloom = PersistedBloom::new(filter, self.stats.counters.clone());
                self.repository
                    .share_bloom_across_range(&snapshot.from(), &snapshot.to(), &bloom)
            },
        )
    }

    /// `ConvertAndRegister`: writes `snapshot` as a base of the tier, makes its table and blob run durable, records its
    /// catalog row and indexes it; then validates it when configured. Returns the trie nodes it wrote, which C# marks
    /// persisted.
    pub fn convert_and_register(&self, snapshot: &Snapshot) -> TierResult<Vec<PersistedNode>> {
        // A base is read-cold once compacted, so it gets a one-line bloom, still filled to keep its key count for the
        // merges that size their blooms from it.
        let bits_per_key = self.config.bloom_bits_per_key;
        let mut bloom = if bits_per_key > 0.0 {
            BloomFilter::new(1, bits_per_key).map_err(|error| TierError::InvalidArgument(error.to_string()))?
        } else {
            BloomFilter::always_true()
        };

        if let Some(faults) = &self.faults {
            faults.before_conversion();
        }
        let estimated_size = builder::estimate_size(snapshot);
        let mut blob_writer = self.blobs.create_writer(estimated_size)?;
        let mut arena_writer = self.arena.create_writer(estimated_size, true)?;
        let output = builder::build(snapshot, &mut arena_writer, blob_writer.writer()?, &mut bloom)?;
        if let Some(error) = self.faults.as_ref().and_then(|faults| faults.conversion_write_error()) {
            return Err(error.into());
        }
        self.repository.stats().observe(
            Histogram::PersistedSnapshotSize,
            HistogramLabel::None,
            arena_writer.written() as f64,
        );
        let (location, reservation) = arena_writer.complete()?;
        blob_writer.complete()?;

        // The catalog must never name bytes that are not durable.
        reservation.fsync()?;
        blob_writer.fsync()?;
        if self
            .faults
            .as_ref()
            .is_some_and(|faults| faults.stop_before_catalog_add())
        {
            reservation.persist_on_shutdown();
            return Err(TierError::InvalidOperation(format!(
                "Conversion of {}->{} stopped before its catalog row",
                snapshot.from().block_number,
                snapshot.to().block_number
            )));
        }
        log::debug!(
            "Persisted snapshot {}->{} to disk (arena {}, {} bytes)",
            snapshot.from().block_number,
            snapshot.to().block_number,
            location.arena_id,
            location.size
        );

        let header = SnapshotHeader {
            from: snapshot.from(),
            to: snapshot.to(),
            tier: SnapshotTier::PersistedBase,
            may_have_self_destructs: output.may_have_self_destructs,
        };
        let bloom = PersistedBloom::new(bloom, self.stats.counters.clone());
        let persisted = Arc::new(PersistedSnapshot::open(
            header,
            reservation,
            bloom,
            self.blobs.clone(),
            self.stats.clone(),
        )?);
        self.catalog.add(&CatalogEntry {
            from: header.from,
            to: header.to,
            location,
            tier: header.tier,
        })?;
        self.repository.add_persisted_snapshot(persisted.clone());

        if self.config.validate_persisted_snapshot {
            let substitute = self
                .faults
                .as_ref()
                .and_then(|faults| faults.validation_source(snapshot));
            validate::validate_persisted_snapshot(substitute.as_ref().unwrap_or(snapshot), &persisted, Path::new(""))?;
        }
        Ok(output.written_nodes)
    }

    /// `Dispose`, idempotent: keeps the files of every snapshot of the tier for the next session, before the buckets
    /// release them.
    pub fn mark_for_shutdown(&self) {
        if !self.marked_for_shutdown.swap(true, Ordering::SeqCst) {
            self.repository.mark_persisted_tier_for_shutdown();
        }
    }
}

/// As the C# container always disposes the loader before the repository, dropping it marks the tier for shutdown.
impl Drop for PersistedSnapshotLoader {
    fn drop(&mut self) {
        self.mark_for_shutdown();
    }
}

/// The progress of a large load, logged at most once a second by whichever worker passes the interval.
struct LoadProgress {
    total: usize,
    /// The first snapshot is loaded before the parallel part.
    loaded: AtomicUsize,
    next_log: Mutex<Instant>,
}

impl LoadProgress {
    fn new(total: usize) -> Self {
        Self {
            total,
            loaded: AtomicUsize::new(1),
            next_log: Mutex::new(Instant::now() + PROGRESS_LOG_INTERVAL),
        }
    }

    fn loaded_one(&self) {
        let loaded = self.loaded.fetch_add(1, Ordering::Relaxed) + 1;
        let now = Instant::now();
        let mut next_log = self.next_log.lock();
        if now >= *next_log || loaded == self.total {
            *next_log = now + PROGRESS_LOG_INTERVAL;
            log::info!("Persisted snapshot load {loaded} / {}", self.total);
        }
    }
}
