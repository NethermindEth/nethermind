//! Port of `Nethermind.State.Flat/PersistedSnapshots/Storage/ArenaWriter.cs` and `ArenaBufferWriter.cs`: writes one
//! table into an arena file from its frontier, through a 1 MiB buffer.
//!
//! [`ArenaWriter::complete`] publishes the table as a reservation; a writer dropped without completing cancels, which
//! returns a shared file to its pool (its bytes past the frontier are overwritten by the next write) and deletes a
//! dedicated one.

use std::sync::Arc;

use super::arena_file::ArenaFile;
use super::arena_manager::ArenaShared;
use super::arena_reservation::ArenaReservation;
use crate::persisted::codec::SnapshotLocation;
use crate::persisted::io::TableSink;
use crate::persisted::sys::round_up_to_os_page;
use crate::persisted::{TierError, TierResult};

const BUFFER_SIZE: usize = 1024 * 1024;

pub struct ArenaWriter {
    manager: Arc<ArenaShared>,
    file: Arc<ArenaFile>,
    dedicated: bool,
    start_offset: i64,
    buffer: Vec<u8>,
    /// The bytes already written to the file.
    flushed: i64,
    completed: bool,
}

impl ArenaWriter {
    pub(super) fn new(manager: Arc<ArenaShared>, file: Arc<ArenaFile>, dedicated: bool, start_offset: i64) -> Self {
        Self {
            manager,
            file,
            dedicated,
            start_offset,
            buffer: Vec::with_capacity(BUFFER_SIZE),
            flushed: 0,
            completed: false,
        }
    }

    /// `Complete`: writes out the buffer and publishes the table. A shared file's frontier moves to the next OS page,
    /// so every reservation starts page-aligned and reclaims cover whole pages; a dedicated file is truncated to the
    /// data. A table past the mapped size is an error, as its estimate was too low.
    pub fn complete(mut self) -> TierResult<(SnapshotLocation, Arc<ArenaReservation>)> {
        self.flush()?;
        let size = self.flushed;
        let data_end = self.start_offset + size;
        let mapped_size = self.file.mapped_size();
        if data_end > mapped_size {
            return Err(TierError::InvalidOperation(format!(
                "Arena write overflowed its mapped size: wrote up to {data_end} but arena {} is mapped to {mapped_size}.",
                self.file.id()
            )));
        }
        let new_frontier = if self.dedicated {
            data_end
        } else {
            round_up_to_os_page(data_end).min(mapped_size)
        };
        // No reservation of a dedicated file exists yet, so shrinking it is safe.
        if self.dedicated && new_frontier > 0 && new_frontier < mapped_size {
            self.file.truncate(new_frontier)?;
        }

        let reservation = Arc::new(ArenaReservation::new(
            self.manager.clone(),
            self.file.clone(),
            self.start_offset,
            size,
        ));
        self.completed = true;
        let has_headroom = !self.dedicated && new_frontier < self.file.mapped_size();
        self.manager.on_write_completed(&self.file, new_frontier, has_headroom);
        Ok((reservation.location(), reservation))
    }

    fn flush(&mut self) -> TierResult<()> {
        if !self.buffer.is_empty() {
            self.write_through(&self.buffer)?;
            self.flushed += self.buffer.len() as i64;
            self.buffer.clear();
        }
        Ok(())
    }

    fn write_through(&self, bytes: &[u8]) -> TierResult<()> {
        Ok(self.file.write_at(bytes, (self.start_offset + self.flushed) as u64)?)
    }
}

impl TableSink for ArenaWriter {
    fn written(&self) -> u64 {
        (self.flushed + self.buffer.len() as i64) as u64
    }

    fn write(&mut self, bytes: &[u8]) -> TierResult<()> {
        if bytes.len() > BUFFER_SIZE - self.buffer.len() {
            self.flush()?;
        }
        if bytes.len() > BUFFER_SIZE {
            // C# grows its buffer to the hint; writing past the buffer lays down the same bytes.
            self.write_through(bytes)?;
            self.flushed += bytes.len() as i64;
        } else {
            self.buffer.extend_from_slice(bytes);
        }
        Ok(())
    }
}

impl Drop for ArenaWriter {
    fn drop(&mut self) {
        if self.completed {
            return;
        }
        // As C#'s dispose, the buffered bytes still reach the file, past the unmoved frontier.
        if let Err(error) = self.flush() {
            log::warn!("A cancelled arena write could not flush its buffer: {error}");
        }
        if self.dedicated {
            self.manager.on_write_cancelled_dedicated(&self.file);
        } else {
            self.manager.on_write_cancelled_shared(&self.file);
        }
    }
}
