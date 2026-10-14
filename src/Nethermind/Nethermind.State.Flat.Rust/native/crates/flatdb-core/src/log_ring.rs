//! Bounded rings of log records that C# drains into its own loggers, so a Rust thread never calls back into the
//! runtime to log.
//!
//! Every store logs into a ring of its own, its [`LogSink`], so the records of one node, or of one test, never reach the
//! loggers of another. The calls on a store run with its sink current on the calling thread ([`LogSink::scope`]), and
//! a worker thread logs into the sink current on the thread that started it ([`current`], [`enter`]). A record logged
//! with no sink current goes to the process-wide [`global`] ring. [`install`] makes the router of these the `log`
//! facade's logger.
//!
//! Each record carries its [`LogSource`], the C# class whose log line it is, for C# to log it through that class's
//! logger. A full ring drops a record and counts it: the oldest less severe than the new one, or else the new one, so a
//! flood of verbose lines never costs a warning or an error.

use std::cell::Cell;
use std::collections::{BTreeMap, VecDeque};
use std::fmt;
use std::sync::atomic::{AtomicU32, AtomicU64, AtomicUsize, Ordering};
use std::sync::{Arc, Once, OnceLock, Weak};
use std::time::Duration;

use log::{Level, LevelFilter, Log, Metadata, Record};
use parking_lot::{Mutex, RwLock};

/// Records the [`global`] ring holds until drained.
pub const GLOBAL_CAPACITY: usize = 4096;
/// Records a [`LogSink`] holds until drained.
pub const SINK_CAPACITY: usize = 1024;

/// The C# class whose log lines a module of the library ports, which C# logs the module's records through.
#[repr(u32)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum LogSource {
    /// A module with no C# counterpart.
    Other = 0,
    /// `FlatDbManager`: [`crate::manager`] and [`crate::bundle_cache`].
    FlatDbManager = 1,
    /// `PersistenceManager`: [`crate::persistence_manager`].
    PersistenceManager = 2,
    /// `SnapshotRepository`: [`crate::repository`].
    SnapshotRepository = 3,
    /// `SnapshotCompactor`: [`crate::compactor`].
    SnapshotCompactor = 4,
    /// `ReadOnlySnapshotBundle`: [`crate::bundle`].
    ReadOnlySnapshotBundle = 5,
    /// The persistence class of the store's layout (`RocksDbPersistence`, `FlatInTriePersistence` or
    /// `PreimageRocksdbPersistence`): [`crate::persistence`].
    LayoutPersistence = 6,
    /// `DbOnTheRocks`: records logged with the target [`LogSource::DB_ON_THE_ROCKS`].
    DbOnTheRocks = 7,
    /// `PersistedSnapshotLoader`: [`crate::persisted::loader`].
    PersistedSnapshotLoader = 8,
    /// `PersistedSnapshotCompactor`: [`crate::persisted::compactor`].
    PersistedSnapshotCompactor = 9,
    /// `ArenaManager`: [`crate::persisted::storage::arena_manager`].
    ArenaManager = 10,
}

impl LogSource {
    /// The `log` target of the lines C# logs from `DbOnTheRocks`, such as a tolerated flush failure.
    pub const DB_ON_THE_ROCKS: &str = "DbOnTheRocks";

    /// The source of a record logged with `target`, which is the logging module unless set explicitly.
    pub fn of(target: &str) -> Self {
        match target {
            Self::DB_ON_THE_ROCKS => Self::DbOnTheRocks,
            "flatdb_core::manager" | "flatdb_core::bundle_cache" => Self::FlatDbManager,
            "flatdb_core::persistence_manager" => Self::PersistenceManager,
            // C# logs a bucket's lines through the logger of the repository that owns it.
            "flatdb_core::repository" | "flatdb_core::persisted::bucket" => Self::SnapshotRepository,
            "flatdb_core::persisted::loader" => Self::PersistedSnapshotLoader,
            "flatdb_core::persisted::compactor" => Self::PersistedSnapshotCompactor,
            "flatdb_core::persisted::storage::arena_manager" => Self::ArenaManager,
            "flatdb_core::compactor" => Self::SnapshotCompactor,
            "flatdb_core::bundle" => Self::ReadOnlySnapshotBundle,
            target if target.starts_with("flatdb_core::persistence::") => Self::LayoutPersistence,
            _ => Self::Other,
        }
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct LogRecord {
    pub level: Level,
    pub source: LogSource,
    pub message: String,
}

/// A bounded queue of records at or above its level.
pub struct LogRing {
    records: Mutex<VecDeque<LogRecord>>,
    capacity: usize,
    level: AtomicUsize,
    dropped: AtomicU64,
}

impl LogRing {
    pub fn new(capacity: usize, level: LevelFilter) -> Self {
        Self {
            records: Mutex::new(VecDeque::new()),
            capacity,
            level: AtomicUsize::new(level as usize),
            dropped: AtomicU64::new(0),
        }
    }

    /// Queues `record`. A full ring makes room by dropping its oldest record less severe than `record`, or else drops
    /// `record`, and counts the drop.
    pub fn push(&self, record: LogRecord) {
        let mut records = self.records.lock();
        if records.len() >= self.capacity {
            self.dropped.fetch_add(1, Ordering::Relaxed);
            let Some(less_severe) = records.iter().position(|kept| kept.level > record.level) else {
                return;
            };
            records.remove(less_severe);
        }
        records.push_back(record);
    }

    /// Takes up to `max` records, oldest first.
    pub fn drain(&self, max: usize) -> Vec<LogRecord> {
        let mut records = self.records.lock();
        let count = max.min(records.len());
        records.drain(..count).collect()
    }

    /// The records dropped since the last call.
    pub fn take_dropped(&self) -> u64 {
        self.dropped.swap(0, Ordering::Relaxed)
    }

    fn keeps(&self, level: Level) -> bool {
        level as usize <= self.level.load(Ordering::Relaxed)
    }

    fn offer(&self, record: &Record<'_>) {
        if self.keeps(record.level()) {
            self.push(LogRecord {
                level: record.level(),
                source: LogSource::of(record.target()),
                message: record.args().to_string(),
            });
        }
    }
}

/// The ring records go to when no sink is current.
pub fn global() -> &'static LogRing {
    static RING: OnceLock<LogRing> = OnceLock::new();
    RING.get_or_init(|| LogRing::new(GLOBAL_CAPACITY, LevelFilter::Trace))
}

/// The id of a registered [`LogSink`]; 0 is none.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct SinkId(u32);

/// The live sinks by id. Threads hold ids rather than the rings, so making a sink current costs no reference count.
static SINKS: RwLock<BTreeMap<u32, Weak<LogRing>>> = RwLock::new(BTreeMap::new());
static NEXT_SINK: AtomicU32 = AtomicU32::new(1);

thread_local! {
    static CURRENT: Cell<u32> = const { Cell::new(0) };
}

/// The ring of one store, registered as long as it lives.
pub struct LogSink {
    id: u32,
    ring: Arc<LogRing>,
}

impl LogSink {
    /// A sink keeping records up to `level`, with the router installed and the facade passing that level.
    pub fn new(level: LevelFilter) -> Self {
        install(level);
        let ring = Arc::new(LogRing::new(SINK_CAPACITY, level));
        let id = NEXT_SINK.fetch_add(1, Ordering::Relaxed);
        SINKS.write().insert(id, Arc::downgrade(&ring));
        Self { id, ring }
    }

    pub fn id(&self) -> SinkId {
        SinkId(self.id)
    }

    pub fn ring(&self) -> &LogRing {
        &self.ring
    }

    /// Runs `body` with this sink current on the calling thread.
    pub fn scope<T>(&self, body: impl FnOnce() -> T) -> T {
        struct Restore(u32);
        impl Drop for Restore {
            fn drop(&mut self) {
                CURRENT.with(|current| current.set(self.0));
            }
        }

        let _restore = Restore(CURRENT.with(|current| current.replace(self.id)));
        body()
    }
}

impl Default for LogSink {
    /// A sink keeping nothing.
    fn default() -> Self {
        Self::new(LevelFilter::Off)
    }
}

impl Drop for LogSink {
    fn drop(&mut self) {
        SINKS.write().remove(&self.id);
    }
}

/// The sink current on this thread, for a thread it starts to log into.
pub fn current() -> Option<SinkId> {
    match CURRENT.with(Cell::get) {
        0 => None,
        id => Some(SinkId(id)),
    }
}

/// Makes `sink` current on this thread from now on.
pub fn enter(sink: Option<SinkId>) {
    CURRENT.with(|current| current.set(sink.map_or(0, |sink| sink.0)));
}

struct Router;

impl Log for Router {
    fn enabled(&self, metadata: &Metadata<'_>) -> bool {
        metadata.level() <= log::max_level()
    }

    fn log(&self, record: &Record<'_>) {
        if !self.enabled(record.metadata()) {
            return;
        }
        let ring = match CURRENT.with(Cell::get) {
            0 => None,
            id => SINKS.read().get(&id).and_then(Weak::upgrade),
        };
        match ring {
            Some(ring) => ring.offer(record),
            None => global().offer(record),
        }
    }

    fn flush(&self) {}
}

/// Makes the router the `log` facade's logger, unless another logger was installed first, and raises the facade's
/// level to `level` if it is lower.
pub fn install(level: LevelFilter) {
    static ROUTER: Router = Router;
    static INSTALL: Once = Once::new();
    static RAISE: Mutex<()> = Mutex::new(());

    INSTALL.call_once(|| {
        let _ = log::set_logger(&ROUTER);
    });
    let _raise = RAISE.lock();
    if level > log::max_level() {
        log::set_max_level(level);
    }
}

/// A duration as C# formats a `TimeSpan`: `[d.]hh:mm:ss[.fffffff]`.
pub struct TimeSpan(pub Duration);

impl fmt::Display for TimeSpan {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        let seconds = self.0.as_secs();
        let (days, hours, minutes) = (seconds / 86_400, seconds / 3_600 % 24, seconds / 60 % 60);
        if days > 0 {
            write!(f, "{days}.")?;
        }
        write!(f, "{hours:02}:{minutes:02}:{:02}", seconds % 60)?;
        let ticks = self.0.subsec_nanos() / 100;
        if ticks > 0 {
            write!(f, ".{ticks:07}")?;
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use std::thread;
    use std::time::Duration;

    use log::{Level, LevelFilter};
    use rstest::rstest;

    use super::{LogRecord, LogRing, LogSink, LogSource, TimeSpan, current, enter, global};

    fn record(message: &str) -> LogRecord {
        at(Level::Warn, message)
    }

    fn at(level: Level, message: &str) -> LogRecord {
        LogRecord {
            level,
            source: LogSource::Other,
            message: message.to_owned(),
        }
    }

    #[test]
    fn a_ring_drains_in_order_and_counts_what_it_dropped() {
        let ring = LogRing::new(2, LevelFilter::Warn);
        ring.push(record("a"));
        ring.push(record("b"));
        ring.push(record("c"));

        assert_eq!(ring.drain(1), [record("a")]);
        assert_eq!(ring.drain(10), [record("b")]);
        assert_eq!(ring.take_dropped(), 1);
        assert_eq!(ring.take_dropped(), 0);
    }

    #[test]
    fn a_full_ring_drops_its_oldest_less_severe_record_for_a_more_severe_one() {
        let ring = LogRing::new(3, LevelFilter::Trace);
        for (level, message) in [
            (Level::Trace, "trace"),
            (Level::Info, "info"),
            (Level::Trace, "later trace"),
            (Level::Error, "error"),
            (Level::Warn, "warn"),
            (Level::Error, "second error"),
            (Level::Warn, "dropped warn"),
        ] {
            ring.push(at(level, message));
        }

        assert_eq!(
            ring.drain(10),
            [
                at(Level::Error, "error"),
                at(Level::Warn, "warn"),
                at(Level::Error, "second error")
            ]
        );
        assert_eq!(ring.take_dropped(), 4);
    }

    #[test]
    fn records_go_to_the_current_sink_at_its_level_and_to_its_workers() {
        let sink = LogSink::new(LevelFilter::Info);
        let quiet = LogSink::new(LevelFilter::Error);

        sink.scope(|| {
            log::info!(target: "flatdb_core::manager", "kept {}", 1);
            log::debug!(target: "flatdb_core::manager", "above the sink's level");
            quiet.scope(|| log::warn!("below the quiet sink's level"));
            let worker = current();
            thread::spawn(move || {
                enter(worker);
                log::warn!(target: LogSource::DB_ON_THE_ROCKS, "from a worker");
            })
            .join()
            .unwrap();
        });
        assert_eq!(current(), None, "the scope ends with the call");

        assert_eq!(
            sink.ring().drain(10),
            [
                LogRecord {
                    level: Level::Info,
                    source: LogSource::FlatDbManager,
                    message: "kept 1".to_owned()
                },
                LogRecord {
                    level: Level::Warn,
                    source: LogSource::DbOnTheRocks,
                    message: "from a worker".to_owned()
                },
            ]
        );
        assert!(quiet.ring().drain(10).is_empty());
        assert!(
            !global()
                .drain(usize::MAX)
                .iter()
                .any(|record| record.message == "kept 1" || record.message == "from a worker"),
            "nothing logged under a sink reaches the global ring"
        );
    }

    #[rstest]
    #[case("flatdb_core::manager", LogSource::FlatDbManager)]
    #[case("flatdb_core::bundle_cache", LogSource::FlatDbManager)]
    #[case("flatdb_core::persistence_manager", LogSource::PersistenceManager)]
    #[case("flatdb_core::repository", LogSource::SnapshotRepository)]
    #[case("flatdb_core::compactor", LogSource::SnapshotCompactor)]
    #[case("flatdb_core::bundle", LogSource::ReadOnlySnapshotBundle)]
    #[case("flatdb_core::persistence::base", LogSource::LayoutPersistence)]
    #[case("flatdb_core::persisted::bucket", LogSource::SnapshotRepository)]
    #[case("flatdb_core::persisted::loader", LogSource::PersistedSnapshotLoader)]
    #[case("flatdb_core::persisted::compactor", LogSource::PersistedSnapshotCompactor)]
    #[case("flatdb_core::persisted::storage::arena_manager", LogSource::ArenaManager)]
    #[case("flatdb_core::persisted::storage::blob_manager", LogSource::Other)]
    #[case("DbOnTheRocks", LogSource::DbOnTheRocks)]
    #[case("flatdb_core::persistence", LogSource::Other)]
    #[case("flatdb_ffi::guard", LogSource::Other)]
    fn modules_log_as_the_csharp_class_they_port(#[case] target: &str, #[case] source: LogSource) {
        assert_eq!(LogSource::of(target), source);
    }

    #[rstest]
    #[case(Duration::ZERO, "00:00:00")]
    #[case(Duration::from_nanos(2_012_345_678), "00:00:02.0123456")]
    #[case(Duration::from_secs(3 * 3600 + 4 * 60 + 5), "03:04:05")]
    #[case(Duration::from_secs(86_400 + 3600) + Duration::from_millis(500), "1.01:00:00.5000000")]
    fn durations_format_as_a_csharp_time_span(#[case] duration: Duration, #[case] expected: &str) {
        assert_eq!(TimeSpan(duration).to_string(), expected);
    }
}
