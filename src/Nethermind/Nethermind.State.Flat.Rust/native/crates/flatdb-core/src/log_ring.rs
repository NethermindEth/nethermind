//! A bounded lock-free ring of log records that C# drains into its own loggers, so a Rust worker thread never calls
//! back into the runtime to log.
//!
//! [`install`] makes the process-wide [`global`] ring the `log` facade's logger, so every `log::` record of the library
//! lands in it. A full ring drops new records and counts them.

use std::sync::OnceLock;
use std::sync::atomic::{AtomicU64, Ordering};

use crossbeam_channel::{Receiver, Sender};
use log::{Level, LevelFilter, Log, Metadata, Record};

/// Records the [`global`] ring holds until drained.
pub const GLOBAL_CAPACITY: usize = 4096;

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct LogRecord {
    pub level: Level,
    /// The module that logged.
    pub target: String,
    pub message: String,
}

pub struct LogRing {
    sender: Sender<LogRecord>,
    receiver: Receiver<LogRecord>,
    dropped: AtomicU64,
}

impl LogRing {
    pub fn new(capacity: usize) -> Self {
        let (sender, receiver) = crossbeam_channel::bounded(capacity);
        Self {
            sender,
            receiver,
            dropped: AtomicU64::new(0),
        }
    }

    /// Queues `record`; drops it and counts it when the ring is full.
    pub fn push(&self, record: LogRecord) {
        if self.sender.try_send(record).is_err() {
            self.dropped.fetch_add(1, Ordering::Relaxed);
        }
    }

    /// Takes up to `max` records, oldest first.
    pub fn drain(&self, max: usize) -> Vec<LogRecord> {
        self.receiver.try_iter().take(max).collect()
    }

    /// The records dropped since the last call.
    pub fn take_dropped(&self) -> u64 {
        self.dropped.swap(0, Ordering::Relaxed)
    }
}

impl Log for LogRing {
    fn enabled(&self, metadata: &Metadata<'_>) -> bool {
        metadata.level() <= log::max_level()
    }

    fn log(&self, record: &Record<'_>) {
        if self.enabled(record.metadata()) {
            self.push(LogRecord {
                level: record.level(),
                target: record.target().to_owned(),
                message: record.args().to_string(),
            });
        }
    }

    fn flush(&self) {}
}

/// The ring of the whole library.
pub fn global() -> &'static LogRing {
    static RING: OnceLock<LogRing> = OnceLock::new();
    RING.get_or_init(|| LogRing::new(GLOBAL_CAPACITY))
}

/// Makes [`global`] the logger of the `log` facade, keeping records up to `level`; `false` when another logger was
/// installed first. The level can be raised or lowered later with [`log::set_max_level`].
pub fn install(level: LevelFilter) -> bool {
    let installed = log::set_logger(global()).is_ok();
    if installed {
        log::set_max_level(level);
    }
    installed
}

#[cfg(test)]
mod tests {
    use log::{Level, LevelFilter, Log, Record};

    use super::{LogRecord, LogRing};

    fn record(message: &str) -> LogRecord {
        LogRecord {
            level: Level::Warn,
            target: "test".to_owned(),
            message: message.to_owned(),
        }
    }

    #[test]
    fn the_ring_drains_in_order_and_counts_what_it_dropped() {
        let ring = LogRing::new(2);
        ring.push(record("a"));
        ring.push(record("b"));
        ring.push(record("c"));

        assert_eq!(ring.drain(1), [record("a")]);
        assert_eq!(ring.drain(10), [record("b")]);
        assert_eq!(ring.take_dropped(), 1);
        assert_eq!(ring.take_dropped(), 0);

        // The facade's level gates what the logger keeps; `log::max_level` is Off until a logger is installed.
        log::set_max_level(LevelFilter::Info);
        ring.log(
            &Record::builder()
                .level(Level::Info)
                .target("flatdb")
                .args(format_args!("kept {}", 1))
                .build(),
        );
        ring.log(
            &Record::builder()
                .level(Level::Debug)
                .args(format_args!("dropped"))
                .build(),
        );
        assert_eq!(
            ring.drain(10),
            [LogRecord {
                level: Level::Info,
                target: "flatdb".to_owned(),
                message: "kept 1".to_owned()
            }]
        );
    }
}
