//! Port of `Nethermind.State.Flat/CompactionSchedule.cs` and `ICompactionSchedule.cs`: at which blocks snapshots are
//! compacted, and so where persistence boundaries fall.
//!
//! The per-node offset is resolved by C# (`CompactionSchedule.ResolveOffset`: configured, stored in the metadata DB,
//! or generated and stored) and passed in as a number; only the validation and the math live here.

use thiserror::Error;

use crate::types::StateId;

/// The largest compact size, `int.MaxValue - 8` (`FlatDbConfigExtensions.ValidateCompactSize`).
const MAX_COMPACT_SIZE: u64 = i32::MAX as u64 - 8;

/// An invalid compaction configuration (the `ArgumentException`s of the C# constructor).
#[derive(Debug, Error, Clone, PartialEq, Eq)]
#[error("{0}")]
pub struct ScheduleError(pub String);

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct CompactionSchedule {
    compact_size: u64,
    max_compact_size: u64,
    offset: u64,
}

impl CompactionSchedule {
    /// A schedule for `CompactSize` and `PersistedSnapshotMaxCompactSize`, shifted by the resolved `offset`, which is
    /// ignored when compaction is disabled (`compact_size <= 1`).
    pub fn new(
        compact_size: u64,
        persisted_snapshot_max_compact_size: u64,
        offset: u64,
    ) -> Result<Self, ScheduleError> {
        if compact_size > MAX_COMPACT_SIZE {
            return Err(ScheduleError("Compact size must not exceed int.MaxValue - 8".into()));
        }
        if persisted_snapshot_max_compact_size > MAX_COMPACT_SIZE {
            return Err(ScheduleError(
                "Persisted snapshot max compact size must not exceed int.MaxValue - 8".into(),
            ));
        }
        if persisted_snapshot_max_compact_size < compact_size {
            return Err(ScheduleError(
                "Persisted snapshot max compact size must not be smaller than CompactSize".into(),
            ));
        }
        if !persisted_snapshot_max_compact_size.is_power_of_two() {
            return Err(ScheduleError(
                "Persisted snapshot max compact size must be a power of 2".into(),
            ));
        }
        if compact_size > 1 && !compact_size.is_power_of_two() {
            return Err(ScheduleError("Compact size must be a power of 2".into()));
        }

        Ok(Self {
            compact_size,
            max_compact_size: persisted_snapshot_max_compact_size,
            offset: if compact_size <= 1 { 0 } else { offset },
        })
    }

    pub fn compact_size(&self) -> u64 {
        self.compact_size
    }

    pub fn offset(&self) -> u64 {
        self.offset
    }

    /// The compact-size tier triggered at `block_number`: the largest power of 2 dividing `block_number + offset`,
    /// capped at `CompactSize`; 1 (no compaction) at block 0 or with compaction disabled.
    pub fn get_compact_size(&self, block_number: u64) -> u64 {
        if self.compact_size <= 1 || block_number == 0 {
            return 1;
        }
        self.shifted_alignment(block_number).min(self.compact_size)
    }

    /// The next block after `from` where a full-size compaction, and so a persistence boundary, falls; `u64::MAX` with
    /// compaction disabled. [`StateId::PRE_GENESIS`] counts as block 0.
    pub fn next_full_compaction_after(&self, from: &StateId) -> u64 {
        if self.compact_size <= 1 {
            return u64::MAX;
        }

        let block_number = if *from == StateId::PRE_GENESIS {
            0
        } else {
            from.block_number
        };
        let modulo = block_number.wrapping_add(self.offset) % self.compact_size;
        let distance = if modulo == 0 {
            self.compact_size
        } else {
            self.compact_size - modulo
        };
        block_number.saturating_add(distance)
    }

    /// Whether the persisted-snapshot window at `block_number` is exactly `CompactSize`.
    pub fn is_compact_size_boundary(&self, block_number: u64) -> bool {
        self.get_persisted_snapshot_compact_size(block_number) == self.compact_size
    }

    /// Whether the persisted-snapshot window at `block_number` is wider than `CompactSize`.
    pub fn is_large_compaction_boundary(&self, block_number: u64) -> bool {
        self.get_persisted_snapshot_compact_size(block_number) > self.compact_size
    }

    /// The persisted-snapshot compaction tier at `block_number`: the largest power of 2 dividing
    /// `block_number + offset`, capped at `PersistedSnapshotMaxCompactSize`; 1 at block 0.
    pub fn get_persisted_snapshot_compact_size(&self, block_number: u64) -> u64 {
        if block_number == 0 {
            return 1;
        }
        self.shifted_alignment(block_number).min(self.max_compact_size)
    }

    /// The lowest set bit of the offset-shifted block number.
    fn shifted_alignment(&self, block_number: u64) -> u64 {
        let shifted = block_number.wrapping_add(self.offset);
        shifted & (!shifted).wrapping_add(1)
    }
}
