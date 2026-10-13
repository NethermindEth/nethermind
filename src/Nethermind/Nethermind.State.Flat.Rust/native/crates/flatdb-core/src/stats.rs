//! The `Nethermind.State.Flat/Metrics.cs` metrics the manager, the persistence manager, the repository, the compactor
//! and the read-only bundles update: gauges and counters as atomics C# reads, histogram observations queued for C# to
//! replay into its observers.
//!
//! Like the C# statics, the gauges are adjusted by deltas at every add and remove site, so every component of one
//! manager shares one [`FlatStats`]. Times are observed in nanoseconds, which are `Stopwatch` ticks on Linux.

use std::sync::atomic::{AtomicI64, AtomicUsize, Ordering};
use std::time::Duration;

use crossbeam_channel::{Receiver, Sender, TrySendError};

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
    /// `Metrics.SnapshotBundlePersistedSnapshotSize`: its persisted snapshots, 0 until the persisted tier exists.
    pub snapshot_bundle_persisted_snapshot_size: AtomicI64,
    /// `Metrics.SnapshotBundlePersistedSnapshotMemory`: their bytes, 0 until the persisted tier exists.
    pub snapshot_bundle_persisted_snapshot_memory: AtomicI64,
    /// `Metrics.InMemorySlotFilterMemory`: bytes of the live slot filters of read-only bundles.
    pub in_memory_slot_filter_memory: AtomicI64,
    /// `Metrics.InMemorySlotFilterBuilds`.
    pub in_memory_slot_filter_builds: AtomicI64,
    /// `Metrics.InMemorySlotFilterBuildFailures`.
    pub in_memory_slot_filter_build_failures: AtomicI64,
    /// Histogram observations dropped because the queue was full (no C# counterpart).
    pub observations_dropped: AtomicI64,
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
}

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
    pub content: ContentCounters,
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
            content: ContentCounters::default(),
            content_hints: ContentHints::default(),
            observations: (observation_capacity > 0).then(|| crossbeam_channel::bounded(observation_capacity)),
        }
    }

    /// Queues an observation of `metric`, dropping it when the queue is full.
    pub fn observe(&self, metric: Histogram, label: HistogramLabel, value: f64) {
        let Some((sender, _)) = &self.observations else {
            return;
        };
        let observation = Observation {
            metric: metric as u16,
            label: label as u16,
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

        let off = FlatStats::default();
        off.observe(Histogram::CompactTime, HistogramLabel::None, 1.0);
        assert!(off.drain_observations(10).is_empty());
        assert_eq!(off.counters.observations_dropped.load(Ordering::Relaxed), 0);
    }
}
