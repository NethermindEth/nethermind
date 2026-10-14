//! Port of `Nethermind.State.Flat/PersistedSnapshots/Sorted/SortedTable.cs`: the table constants, the footer and the
//! corruption error.

use crate::persisted::TierError;
use crate::persisted::io::ByteReader;
use crate::persisted::page_layout::PAGE_SIZE;

/// `BlockSize`: data blocks but the last are padded to this, so block `i` starts at `i · BLOCK_SIZE`.
pub const BLOCK_SIZE: usize = PAGE_SIZE;

/// `DefaultRestartInterval`: records per restart run.
pub const DEFAULT_RESTART_INTERVAL: usize = 8;

/// `FooterSize`: `[indexOffset i64][version u8]`.
pub const FOOTER_SIZE: usize = 9;

/// `FormatVersion`: the last byte of every table this build writes.
pub const FORMAT_VERSION: u8 = 1;

/// The resolved footer: where the index block starts, relative to the table.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Footer {
    pub index_offset: usize,
}

/// `TryReadFooter`: `None` for a table too short, of another version, or whose index offset points outside it.
pub fn try_read_footer(reader: &ByteReader<'_>) -> Option<Footer> {
    let length = reader.len();
    if length < FOOTER_SIZE {
        return None;
    }
    let bytes = reader.read(length - FOOTER_SIZE, FOOTER_SIZE)?;
    if bytes[8] != FORMAT_VERSION {
        return None;
    }
    let index_offset = i64::from_le_bytes(bytes[..8].try_into().expect("8 bytes"));
    let index_offset = usize::try_from(index_offset).ok()?;
    (index_offset <= length - FOOTER_SIZE).then_some(Footer { index_offset })
}

/// `ThrowCorrupt`: a record no writer produced. Loud by design, as a silent miss would read as wrong state.
pub fn corrupt(detail: impl std::fmt::Display) -> TierError {
    TierError::Corrupt(format!(
        "Corrupt persisted-snapshot SortedTable: {detail}. The persistedSnapshot/ directory is corrupted — wipe and resync."
    ))
}
