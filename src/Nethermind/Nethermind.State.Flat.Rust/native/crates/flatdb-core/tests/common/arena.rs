//! Arena-manager test support: the configuration the C# storage tests use and a one-call table write.

use std::sync::Arc;

use flatdb_core::persisted::codec::SnapshotLocation;
use flatdb_core::persisted::io::TableSink;
use flatdb_core::persisted::storage::arena_manager::{ArenaConfig, ArenaManager};
use flatdb_core::persisted::storage::arena_reservation::ArenaReservation;

/// Shared files of `arena_file_size_bytes`, no dedicated files, no page tracker, no hole punching.
pub fn config(arena_file_size_bytes: i64) -> ArenaConfig {
    ArenaConfig {
        arena_file_size_bytes,
        dedicated_arena_threshold_bytes: 1 << 40,
        page_cache_bytes: 0,
        punch_hole_on_reclaim: false,
    }
}

/// Writes `data` as one table, estimated at its length.
pub fn write(manager: &ArenaManager, data: &[u8], small: bool) -> (SnapshotLocation, Arc<ArenaReservation>) {
    let mut writer = manager.create_writer(data.len() as i64, small).unwrap();
    writer.write(data).unwrap();
    writer.complete().unwrap()
}
