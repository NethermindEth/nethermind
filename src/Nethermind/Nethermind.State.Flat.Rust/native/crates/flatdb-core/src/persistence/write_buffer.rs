//! Port of `Nethermind.State.Flat/Persistence/WriteBufferAdjuster.cs`: sizes each column's memtable after the bytes
//! the last persistence batch wrote to it, and to the per-column maximum while batches skip the write-ahead log.

use crate::kv::{Column, KvResult, KvStore, WriteFlags};

/// `WriteBufferAdjuster.DefaultWriteBufferFloor`: 16 MiB.
pub const DEFAULT_WRITE_BUFFER_FLOOR: i64 = 16 * 1024 * 1024;

const MIB: i64 = 1024 * 1024;

/// Bytes a batch wrote to each column it counts; `None` for a column it does not count.
pub type ColumnCounters = [Option<u64>; Column::COUNT];

/// `WriteBufferAdjuster.MaxWriteBufferSize`.
pub fn max_write_buffer_size(column: Column) -> i64 {
    match column {
        Column::Account => 32 * MIB,
        Column::Storage | Column::StateNodes | Column::StateTopNodes | Column::StorageNodes => 64 * MIB,
        Column::Metadata | Column::FallbackNodes => 16 * MIB,
    }
}

/// The memtable sizing state shared by the batches of one persistence.
pub struct WriteBufferAdjuster {
    /// Lower bound applied per column. Frequent small batches (a small `CompactSize`) would otherwise shrink the
    /// memtable to the floor and churn L0; a higher floor lets them coalesce in the memtable instead.
    floor: i64,
    sync_buffer_set: bool,
    last_write_buffer_size: [i64; Column::COUNT],
}

impl WriteBufferAdjuster {
    /// A floor of zero or less is the default floor.
    pub fn new(write_buffer_floor: i64) -> Self {
        Self {
            floor: if write_buffer_floor <= 0 {
                DEFAULT_WRITE_BUFFER_FLOOR
            } else {
                write_buffer_floor
            },
            sync_buffer_set: false,
            last_write_buffer_size: [0; Column::COUNT],
        }
    }

    /// Port of `WriteBufferAdjuster.Wrap`, for each of `columns` in order: a batch without the write-ahead log (sync)
    /// raises the memtables to their maximum once and is not counted; any other batch counts the bytes it writes to
    /// each wrapped column.
    pub fn wrap<K: KvStore>(
        &mut self,
        kv: &K,
        columns: &[Column],
        flags: WriteFlags,
        counters: &mut ColumnCounters,
    ) -> KvResult<()> {
        for &column in columns {
            if flags.contains(WriteFlags::DISABLE_WAL) {
                if !self.sync_buffer_set {
                    for column in [
                        Column::Account,
                        Column::Storage,
                        Column::StateNodes,
                        Column::StorageNodes,
                    ] {
                        kv.set_write_buffer(column, max_write_buffer_size(column) as u64)?;
                    }
                    self.sync_buffer_set = true;
                }
                continue;
            }

            self.sync_buffer_set = false;
            counters[column.index()].get_or_insert(0);
        }
        Ok(())
    }

    /// Port of `WriteBufferAdjuster.OnBatchDisposed`: resizes each counted column to one and a half times what the
    /// batch wrote, within the floor and the column's maximum, unless that is within 20% of its current size.
    pub fn on_batch_disposed<K: KvStore>(&mut self, kv: &K, counters: &ColumnCounters) -> KvResult<()> {
        for column in Column::ALL {
            if let Some(bytes_written) = counters[column.index()] {
                self.adjust_write_buffer(kv, column, bytes_written)?;
            }
        }
        Ok(())
    }

    fn adjust_write_buffer<K: KvStore>(&mut self, kv: &K, column: Column, bytes_written: u64) -> KvResult<()> {
        if self.sync_buffer_set || bytes_written == 0 {
            return Ok(());
        }

        let target =
            ((bytes_written as f64 * 1.5) as i64).clamp(self.floor, self.floor.max(max_write_buffer_size(column)));
        let last_size = self.last_write_buffer_size[column.index()];
        if last_size != 0 && (target - last_size).abs() <= (last_size as f64 * 0.2) as i64 {
            return Ok(());
        }

        self.last_write_buffer_size[column.index()] = target;
        kv.set_write_buffer(column, target as u64)
    }
}

#[cfg(test)]
mod tests {
    use super::{ColumnCounters, DEFAULT_WRITE_BUFFER_FLOOR, WriteBufferAdjuster};
    use crate::kv::mem::MemKv;
    use crate::kv::{Column, WriteFlags};
    use rstest::rstest;

    const MIB: u64 = 1024 * 1024;

    /// Runs one batch writing `bytes` to the account column.
    fn batch(adjuster: &mut WriteBufferAdjuster, kv: &MemKv, bytes: u64) {
        let mut counters: ColumnCounters = Default::default();
        adjuster
            .wrap(kv, &[Column::Account], WriteFlags::NONE, &mut counters)
            .unwrap();
        *counters[Column::Account.index()].as_mut().unwrap() += bytes;
        adjuster.on_batch_disposed(kv, &counters).unwrap();
    }

    /// Port of `WriteBufferAdjusterTests.OnBatchDisposed_AdjustsWriteBuffer`, `AdjustWriteBuffer_RespectsPerColumnCap`
    /// and the floor cases: the target is 1.5x the bytes written, clamped to `[floor, max(floor, cap)]`, and a second
    /// batch within 20% of the current size leaves it.
    #[rstest]
    #[case(0, &[20], &[16 * MIB])]
    #[case(0, &[200 * MIB], &[32 * MIB])]
    #[case(0, &[20 * MIB, 21 * MIB], &[30 * MIB])]
    #[case(0, &[12 * MIB, 20 * MIB], &[18 * MIB, 30 * MIB])]
    #[case(128 * MIB, &[20], &[128 * MIB])]
    #[case(512 * MIB, &[64], &[512 * MIB])]
    fn batches_resize_the_memtable(#[case] floor: u64, #[case] batches: &[u64], #[case] sizes: &[u64]) {
        let kv = MemKv::new();
        let mut adjuster = WriteBufferAdjuster::new(floor as i64);
        for &bytes in batches {
            batch(&mut adjuster, &kv, bytes);
        }

        let expected: Vec<(Column, u64)> = sizes.iter().map(|&size| (Column::Account, size)).collect();
        assert_eq!(kv.write_buffer_sizes(), expected);
    }

    /// Port of `Wrap_WithDisableWAL_UsesPerColumnCaps` and `OnBatchDisposed_NoWraps_DoesNotCallSetWriteBuffer`.
    #[test]
    fn sync_batches_raise_four_memtables_once_and_count_nothing() {
        let kv = MemKv::new();
        let mut adjuster = WriteBufferAdjuster::new(DEFAULT_WRITE_BUFFER_FLOOR);
        let mut counters: ColumnCounters = Default::default();
        adjuster.on_batch_disposed(&kv, &counters).unwrap();
        assert!(kv.write_buffer_sizes().is_empty());

        for _ in 0..2 {
            adjuster
                .wrap(
                    &kv,
                    &[Column::Account, Column::Storage],
                    WriteFlags::DISABLE_WAL,
                    &mut counters,
                )
                .unwrap();
        }
        assert_eq!(counters, ColumnCounters::default());
        assert_eq!(
            kv.write_buffer_sizes(),
            [
                (Column::Account, 32 * MIB),
                (Column::Storage, 64 * MIB),
                (Column::StateNodes, 64 * MIB),
                (Column::StorageNodes, 64 * MIB),
            ]
        );
    }
}
