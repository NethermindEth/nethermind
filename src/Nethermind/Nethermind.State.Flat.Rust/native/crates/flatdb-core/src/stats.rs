//! The `Nethermind.State.Flat/Metrics.cs` metrics the manager, the persistence manager, the repository, the compactor
//! and the read-only bundles update: gauges and counters as atomics C# reads, histogram observations queued for C# to
//! replay into its observers.
//!
//! Like the C# statics, the gauges are adjusted by deltas at every add and remove site, so every component of one
//! manager shares one [`FlatStats`]. Times are observed in nanoseconds, which are `Stopwatch` ticks on Linux.

use std::collections::HashMap;
use std::sync::Arc;
use std::sync::atomic::{AtomicI64, AtomicUsize, Ordering};
use std::time::Duration;

use crossbeam_channel::{Receiver, Sender, TrySendError};
use parking_lot::Mutex;

use crate::snapshot::SnapshotTier;

/// The gauges and counters, one atomic per `Metrics` property, readable in place by the C# metrics bridge.
#[repr(C)]
#[derive(Debug, Default)]
pub struct FlatCounters {
    /// `Metrics.SnapshotCount`: in-memory base snapshots.
    pub snapshot_count: AtomicI64,
    /// `Metrics.CompactedSnapshotCount`: in-memory compacted snapshots.
    pub compacted_snapshot_count: AtomicI64,
    /// `Metrics.SnapshotMemory`: estimated bytes of the base snapshots.
    pub snapshot_memory: AtomicI64,
    /// `Metrics.CompactedSnapshotMemory`: estimated bytes of the compacted snapshots.
    pub compacted_snapshot_memory: AtomicI64,
    /// `Metrics.TotalSnapshotMemory`: the sum of the two above.
    pub total_snapshot_memory: AtomicI64,
    /// `Metrics.SnapshotBundleSize`: in-memory snapshots of the last assembled read-only bundle.
    pub snapshot_bundle_size: AtomicI64,
    /// `Metrics.SnapshotBundlePersistedSnapshotSize`: its persisted snapshots.
    pub snapshot_bundle_persisted_snapshot_size: AtomicI64,
    /// `Metrics.SnapshotBundlePersistedSnapshotMemory`: their table bytes.
    pub snapshot_bundle_persisted_snapshot_memory: AtomicI64,
    /// `Metrics.InMemorySlotFilterMemory`: bytes of the live slot filters of read-only bundles.
    pub in_memory_slot_filter_memory: AtomicI64,
    /// `Metrics.InMemorySlotFilterBuilds`.
    pub in_memory_slot_filter_builds: AtomicI64,
    /// `Metrics.InMemorySlotFilterBuildFailures`.
    pub in_memory_slot_filter_build_failures: AtomicI64,
    /// Histogram observations dropped because the queue was full (no C# counterpart).
    pub observations_dropped: AtomicI64,
    /// The pooled-resource gauges of the native contents.
    pub content: ContentCounters,
}

/// The number of `ResourcePool.Usage` values a block's content is written under: `MainBlockProcessing`,
/// `PostMainBlockProcessing` and `ReadOnlyProcessingEnv`.
pub const CONTENT_USAGES: usize = 3;
/// The number of `ResourcePool.Usage` values a compacted content is built under: `Compact2` to `Compact2048`.
pub const SORTED_CONTENT_USAGES: usize = 11;

/// The pooled-resource gauges of `ResourcePool` (`Metrics.ActivePooledResource` and `Metrics.CreatedPooledResource`)
/// for the native contents, indexed like `ResourcePool.Usage`.
///
/// A block's content is live from its first write, or from the commit freezing an unwritten one, until the last
/// snapshot holding it drops: the C# rent-to-return lifetime of a pooled `SnapshotContent`. Nothing is pooled, so every
/// live content was created.
#[repr(C)]
#[derive(Debug, Default)]
pub struct ContentCounters {
    /// `("<usage>", "SnapshotContent")`: live block contents.
    pub active: [AtomicI64; CONTENT_USAGES],
    pub created: [AtomicI64; CONTENT_USAGES],
    /// `("Compact<N>", "SortedSnapshotContent")`: live compacted contents.
    pub sorted_active: [AtomicI64; SORTED_CONTENT_USAGES],
    pub sorted_created: [AtomicI64; SORTED_CONTENT_USAGES],
}

/// The gauges and counters of the persisted-snapshot tier, named after their `Metrics` property.
///
/// `page_tracker_*_bytes` and `page_tracker_pages_refreshed` are set by value, as C# assigns them; every other one moves
/// by deltas.
#[repr(C)]
#[derive(Debug, Default)]
pub struct TierCounters {
    pub bloom_memory: AtomicI64,
    pub bloom_count: AtomicI64,
    pub arena_file_count: AtomicI64,
    /// Bytes below the frontiers of the arena files, not their pre-extended length.
    pub arena_allocated_bytes: AtomicI64,
    pub blob_file_count: AtomicI64,
    /// Bytes below the frontiers of the blob files.
    pub blob_allocated_bytes: AtomicI64,
    pub arena_reservation_count: AtomicI64,
    pub arena_reservation_bytes: AtomicI64,
    pub page_tracker_resident_bytes: AtomicI64,
    pub page_tracker_metadata_bytes: AtomicI64,
    pub compactions: AtomicI64,
    pub prunes: AtomicI64,
    pub page_tracker_evictions_dispatched: AtomicI64,
    pub page_tracker_evictions_inline_fallback: AtomicI64,
    pub page_tracker_pages_refreshed: AtomicI64,
    /// Writes to the catalog database, which `DbOnTheRocks` counts in its `TotalWrites` metric.
    pub catalog_puts: AtomicI64,
}

/// The metrics of the persisted-snapshot tier: its counters and its gauges labelled by tier and size.
#[derive(Debug, Default)]
pub struct TierStats {
    pub counters: Arc<TierCounters>,
    pub gauges: TierGauges,
}

/// The gauges of one `{tier, size}` label, `size` being the blocks a snapshot spans.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct TierGaugeRow {
    /// `Metrics.PersistedSnapshotCount`: snapshots in the tier's bucket.
    pub count: i64,
    /// `Metrics.PersistedSnapshotMemory`: their table bytes.
    pub memory: i64,
    /// `Metrics.ActivePersistedSnapshotCount`: live snapshot objects, bucket entries and their twins included.
    pub active: i64,
}

/// The `{tier, size}`-labelled gauges, moved by deltas as C# moves its `ConcurrentDictionary` entries.
#[derive(Debug, Default)]
pub struct TierGauges {
    rows: Mutex<HashMap<(SnapshotTier, i64), TierGaugeRow>>,
}

impl TierGauges {
    pub fn add(&self, tier: SnapshotTier, size: i64, delta: TierGaugeRow) {
        let mut rows = self.rows.lock();
        let row = rows.entry((tier, size)).or_default();
        row.count += delta.count;
        row.memory += delta.memory;
        row.active += delta.active;
    }

    /// The row of a label, zero when it was never moved.
    pub fn row(&self, tier: SnapshotTier, size: i64) -> TierGaugeRow {
        self.rows.lock().get(&(tier, size)).copied().unwrap_or_default()
    }
}

/// The entry counts of the last content frozen under each usage, which size the maps of the next one.
#[derive(Debug, Default)]
pub(crate) struct ContentHints {
    last_frozen: [[AtomicUsize; ContentHint::COUNT]; CONTENT_USAGES],
}

/// What [`ContentHints`] records of a frozen content.
#[derive(Clone, Copy, Debug)]
pub(crate) enum ContentHint {
    Accounts,
    SelfDestructs,
    SlotAddresses,
    StateNodes,
    StorageNodeAddresses,
}

impl ContentHint {
    const COUNT: usize = 5;
}

impl ContentHints {
    pub(crate) fn get(&self, usage: usize, hint: ContentHint) -> usize {
        self.last_frozen[usage][hint as usize].load(Ordering::Relaxed)
    }

    pub(crate) fn set(&self, usage: usize, hint: ContentHint, count: usize) {
        self.last_frozen[usage][hint as usize].store(count, Ordering::Relaxed);
    }
}

/// A histogram of `Metrics`, with the id C# maps back to the observer.
#[repr(u16)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Histogram {
    /// `Metrics.FlatPersistenceTime`, nanoseconds.
    FlatPersistenceTime = 0,
    /// `Metrics.FlatPersistenceSnapshotSize`, RLP bytes, labelled by payload.
    FlatPersistenceSnapshotSize = 1,
    /// `Metrics.CompactTime`, nanoseconds.
    CompactTime = 2,
    /// `Metrics.CompactorStallTime`, nanoseconds.
    CompactorStallTime = 3,
    /// `Metrics.SnapshotBundleBlockNumberDepth`, blocks, labelled by part.
    SnapshotBundleBlockNumberDepth = 4,
    /// `Metrics.InMemorySlotFilterBuildTime`, nanoseconds.
    InMemorySlotFilterBuildTime = 5,
    /// `Metrics.PersistedSnapshotConvertTime`, nanoseconds of converting a base into the persisted tier.
    PersistedSnapshotConvertTime = 6,
    /// `Metrics.PersistedSnapshotSize`, table bytes of a converted base.
    PersistedSnapshotSize = 7,
    /// `Metrics.PersistedSnapshotCompactedSize`, table bytes of a merge, labelled by compact size.
    PersistedSnapshotCompactedSize = 8,
    /// `Metrics.PersistedSnapshotCompactTime`, nanoseconds, labelled by compact size.
    PersistedSnapshotCompactTime = 9,
    /// `Metrics.ReadOnlySnapshotBundleSkipTime`, nanoseconds of a probe of the persisted tier that found nothing,
    /// labelled by key kind.
    ReadOnlySnapshotBundleSkipTime = 10,
}

/// The label id of `CompactSizeLabel(1)`; `CompactSizeLabel(2^n)` is this plus `n`.
pub const COMPACT_SIZE_LABEL_BASE: u16 = 16;

/// The label of an observation, with the id C# maps back to its `StringLabel`.
#[repr(u16)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum HistogramLabel {
    None = 0,
    /// `payload="state_nodes"`.
    StateNodes = 1,
    /// `payload="storage_nodes"`.
    StorageNodes = 2,
    /// `part="in_memory"`.
    InMemory = 3,
    /// `part="persisted"`.
    Persisted = 4,
    /// `account`.
    Account = 5,
    /// `slot`.
    Slot = 6,
    /// `state_rlp`.
    StateRlp = 7,
    /// `storage_rlp`.
    StorageRlp = 8,
}

/// One histogram observation, `fdb_drain_observations`' record.
#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Observation {
    pub metric: u16,
    pub label: u16,
    pub value: f64,
}

/// The metrics of one manager and the components under it.
pub struct FlatStats {
    pub counters: FlatCounters,
    pub(crate) content_hints: ContentHints,
    /// `None` when histograms are off, as the C# observers of detailed metrics are no-ops then.
    observations: Option<(Sender<Observation>, Receiver<Observation>)>,
}

impl Default for FlatStats {
    /// Counters only, without histograms.
    fn default() -> Self {
        Self::new(0)
    }
}

impl FlatStats {
    /// Stats queueing at most `observation_capacity` histogram observations until drained; 0 records none.
    pub fn new(observation_capacity: usize) -> Self {
        Self {
            counters: FlatCounters::default(),
            content_hints: ContentHints::default(),
            observations: (observation_capacity > 0).then(|| crossbeam_channel::bounded(observation_capacity)),
        }
    }

    /// Whether observations are queued, as the C# detailed metrics are recorded only when enabled.
    pub fn records_observations(&self) -> bool {
        self.observations.is_some()
    }

    /// Queues an observation of `metric`, dropping it when the queue is full.
    pub fn observe(&self, metric: Histogram, label: HistogramLabel, value: f64) {
        self.push(metric, label as u16, value);
    }

    /// Queues an observation labelled `CompactSizeLabel(size)`, `size` being a power of two.
    pub fn observe_compact_size(&self, metric: Histogram, size: u64, value: f64) {
        self.push(metric, COMPACT_SIZE_LABEL_BASE + size.max(1).ilog2() as u16, value);
    }

    fn push(&self, metric: Histogram, label: u16, value: f64) {
        let Some((sender, _)) = &self.observations else {
            return;
        };
        let observation = Observation {
            metric: metric as u16,
            label,
            value,
        };
        if let Err(TrySendError::Full(_)) = sender.try_send(observation) {
            self.counters.observations_dropped.fetch_add(1, Ordering::Relaxed);
        }
    }

    /// Queues an observation of a time.
    pub fn observe_time(&self, metric: Histogram, elapsed: Duration) {
        self.observe(metric, HistogramLabel::None, elapsed.as_nanos() as f64);
    }

    /// Takes up to `max` queued observations, oldest first.
    pub fn drain_observations(&self, max: usize) -> Vec<Observation> {
        match &self.observations {
            Some((_, receiver)) => receiver.try_iter().take(max).collect(),
            None => Vec::new(),
        }
    }

    pub(crate) fn add(counter: &AtomicI64, delta: i64) {
        counter.fetch_add(delta, Ordering::Relaxed);
    }

    pub(crate) fn set(counter: &AtomicI64, value: i64) {
        counter.store(value, Ordering::Relaxed);
    }
}

#[cfg(test)]
mod tests {
    use std::sync::atomic::Ordering;
    use std::time::Duration;

    use super::{FlatStats, Histogram, HistogramLabel, Observation};

    #[test]
    fn observations_queue_until_drained_and_drop_when_full_or_off() {
        let stats = FlatStats::new(2);
        stats.observe(Histogram::FlatPersistenceSnapshotSize, HistogramLabel::StateNodes, 10.0);
        stats.observe_time(Histogram::CompactTime, Duration::from_micros(3));
        stats.observe(Histogram::CompactorStallTime, HistogramLabel::None, 1.0);

        assert_eq!(
            stats.drain_observations(1),
            [Observation {
                metric: 1,
                label: 1,
                value: 10.0
            }]
        );
        assert_eq!(
            stats.drain_observations(10),
            [Observation {
                metric: 2,
                label: 0,
                value: 3000.0
            }]
        );
        assert_eq!(stats.counters.observations_dropped.load(Ordering::Relaxed), 1);

        stats.observe_compact_size(Histogram::PersistedSnapshotCompactTime, 64, 2.0);
        assert_eq!(
            stats.drain_observations(10),
            [Observation {
                metric: 9,
                label: 16 + 6,
                value: 2.0
            }]
        );

        let off = FlatStats::default();
        off.observe(Histogram::CompactTime, HistogramLabel::None, 1.0);
        assert!(off.drain_observations(10).is_empty());
        assert_eq!(off.counters.observations_dropped.load(Ordering::Relaxed), 0);
    }
}
