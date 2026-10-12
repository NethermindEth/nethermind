use thiserror::Error;

/// Result of a key-value store operation.
pub type KvResult<T> = Result<T, KvError>;

/// Failure of a key-value store operation.
#[derive(Debug, Error, Clone, PartialEq, Eq)]
pub enum KvError {
    /// RocksDB reported an error through its `errptr`.
    #[error("{message}")]
    RocksDb {
        message: String,
        /// Whether the error is on-disk corruption or an I/O failure, after which no further write may be applied
        /// (see `DbOnTheRocks.HandleFatalDbError`).
        fatal: bool,
    },
    /// The backend does not implement the operation.
    #[error("not supported: {0}")]
    NotSupported(&'static str),
    /// A caller-supplied argument or descriptor is invalid.
    #[error("invalid argument: {0}")]
    InvalidArgument(String),
}

impl KvError {
    /// Wraps a RocksDB error message, classifying it the way `DbOnTheRocks.HandleFatalDbError` does.
    pub fn rocks(message: String) -> Self {
        let fatal = message.contains("Corruption:") || message.contains("IO error");
        Self::RocksDb { message, fatal }
    }

    /// Whether this error must stop every further write.
    pub fn is_fatal(&self) -> bool {
        matches!(self, Self::RocksDb { fatal: true, .. })
    }
}

#[cfg(test)]
mod tests {
    use super::KvError;
    use rstest::rstest;

    #[rstest]
    #[case("Corruption: block checksum mismatch", true)]
    #[case("IO error: No space left on device", true)]
    #[case("IO error: While fsync: /db/000123.sst: Permission denied", true)]
    #[case("Invalid argument: Sync writes has to enable WAL.", false)]
    #[case("Busy: ", false)]
    fn rocks_errors_are_classified_like_csharp(#[case] message: &str, #[case] fatal: bool) {
        let error = KvError::rocks(message.to_owned());
        assert_eq!(error.is_fatal(), fatal);
        assert_eq!(error.to_string(), message);
    }
}
