//! Port of `Nethermind.State.Flat/PersistedSnapshots/Storage/WholeReadSession.cs` and `WholeReadSessionReader.cs`: a
//! scan of a whole table through a mapping of its own, advised `MADV_NORMAL` for read-ahead, apart from the
//! random-access mapping the point reads use.
//!
//! The session leases its reservation. Ending it drops the scanned pages from the page cache (`MADV_DONTNEED` on a
//! shared file mapping reaches every mapping of the file) and from the tracker together, so the tracker never holds
//! pages the kernel released. Every C# caller asks for that drop, so it is not optional here.

use std::sync::Arc;

use super::arena_reservation::ArenaReservation;
use crate::persisted::io::ByteReader;
use crate::persisted::sys::{Advice, Mapping};

pub struct WholeReadSession {
    reservation: Arc<ArenaReservation>,
    view: Mapping,
}

impl WholeReadSession {
    pub(super) fn new(reservation: Arc<ArenaReservation>, view: Mapping) -> Self {
        Self { reservation, view }
    }

    /// `CreateReader`: an untracked reader of the table, offset 0 being its first byte.
    pub fn reader(&self) -> ByteReader<'_> {
        ByteReader::new(self.view.slice(0, self.view.len()))
    }
}

impl Drop for WholeReadSession {
    fn drop(&mut self) {
        self.view.advise_inward(0, self.view.len(), Advice::DontNeed);
        self.reservation.forget_tracker();
    }
}
