//! The persisted-snapshot tier of one manager with long finality on, wired as `FlatWorldStateModule` registers it: the
//! arena and blob managers, the catalog, the loader and the persisted compactor over one repository. It is opened as the
//! `FlatDbManager` constructor opens it (`PersistedSnapshotLoader.Load`), before any worker can read or write it.

use std::path::PathBuf;
use std::sync::Arc;

use super::compactor::{CompactorConfig, CompactorHooks, PersistedCompactor, PersistedSnapshotCompactor};
use super::loader::PersistedSnapshotLoader;
use super::storage::arena_manager::{ArenaConfig, ArenaManager};
use super::storage::blob_manager::BlobManager;
use super::storage::catalog::{CatalogKv, SnapshotCatalog};
use super::{PersistedTierConfig, TierFaults, TierResult};
use crate::manager::PanicHook;
use crate::persistence_manager::{FatalErrorSlot, PersistError};
use crate::repository::{SnapshotRepository, StateHeaderProvider};
use crate::schedule::CompactionSchedule;
use crate::stats::{FlatStats, TierStats};

/// Where the tier keeps its files and the `IFlatDbConfig` members it reads.
#[derive(Clone, Debug)]
pub struct TierSettings {
    /// `{BaseDbPath}/persistedSnapshot/arena`.
    pub arena_directory: PathBuf,
    /// `{BaseDbPath}/persistedSnapshot/blob`.
    pub blob_directory: PathBuf,
    pub arena: ArenaConfig,
    pub tier: PersistedTierConfig,
    pub schedule: CompactionSchedule,
}

/// What the tier calls back with and the faults it takes.
pub struct TierHooks {
    /// The name of every persisted compaction that panics, once logged.
    pub on_job_panic: PanicHook,
    /// The faults a test build injects; none in production.
    pub faults: Option<Arc<dyn TierFaults>>,
}

/// The parts of the tier the persistence manager and the manager act on; the repository indexes its snapshots.
pub struct PersistedTier {
    pub arena: Arc<ArenaManager>,
    pub loader: Arc<PersistedSnapshotLoader>,
    pub compactor: Arc<dyn PersistedCompactor>,
    /// Where the persisted compactor records its fatal catalog errors, shared with the persistence manager.
    pub fatal_error: Arc<FatalErrorSlot>,
}

impl PersistedTier {
    /// Builds the tier of `settings` writing its catalog through `catalog`, with a repository over its buckets and
    /// `stats`, and loads it from `catalog_rows`, the rows C# read from the catalog database, calling back with `hooks`.
    ///
    /// A failed load leaves the files of the snapshots it opened on disk.
    pub fn open(
        settings: &TierSettings,
        catalog: Box<dyn CatalogKv>,
        catalog_rows: &[u8],
        finalized_state_provider: Arc<dyn StateHeaderProvider>,
        stats: Arc<FlatStats>,
        tier_stats: Arc<TierStats>,
        hooks: TierHooks,
    ) -> TierResult<(Arc<SnapshotRepository>, Self)> {
        let arena = Arc::new(ArenaManager::new(
            &settings.arena_directory,
            settings.arena,
            tier_stats.counters.clone(),
        )?);
        let blobs = BlobManager::new(
            &settings.blob_directory,
            settings.arena.arena_file_size_bytes,
            tier_stats.counters.clone(),
        )?;
        let catalog = Arc::new(SnapshotCatalog::new(catalog));
        let repository = Arc::new(SnapshotRepository::with_persisted_tier(
            finalized_state_provider,
            stats,
            catalog.clone(),
            tier_stats.clone(),
        ));
        let loader = Arc::new(PersistedSnapshotLoader::new(
            repository.clone(),
            arena.clone(),
            blobs.clone(),
            catalog.clone(),
            tier_stats.clone(),
            settings.tier,
            hooks.faults.clone(),
        ));
        loader.load(catalog_rows)?;

        let fatal_error = Arc::new(FatalErrorSlot::default());
        let recorded = fatal_error.clone();
        let compactor = Arc::new(PersistedSnapshotCompactor::new(
            repository.clone(),
            arena.clone(),
            blobs,
            catalog,
            tier_stats,
            CompactorConfig {
                schedule: settings.schedule,
                tier: settings.tier,
            },
            CompactorHooks {
                on_fatal_error: Box::new(move |error| recorded.record(&PersistError::from(error))),
                on_job_panic: hooks.on_job_panic,
                faults: hooks.faults,
            },
        ));
        Ok((
            repository,
            Self {
                arena,
                loader,
                compactor,
                fatal_error,
            },
        ))
    }
}
