//! Port of `Nethermind.State.Flat/PersistedSnapshots/Sorted/DataBlockReader.cs`: header parsing and the ceiling seek of
//! a data block, whose values are inline bytes.

use super::KEY_BUFFER_LENGTH;
use super::block::{FLAG_BLOCK, RECORD_HEADER_LENGTH};
use super::table::{BLOCK_SIZE, corrupt};
use crate::persisted::TierResult;
use crate::persisted::io::ByteReader;

/// `[flag u8][recordsEnd u16][numRestarts u16]`.
const HEADER_LENGTH: usize = 5;

/// A record found by a seek: its key in the caller's buffer and where its value lies in the reader.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Ceiling {
    pub key_length: usize,
    pub value_offset: usize,
    pub value_length: usize,
}

struct Header {
    records_end: usize,
    restart_count: usize,
}

fn read_header(reader: &ByteReader<'_>, block_start: usize) -> Option<Header> {
    let bytes = reader.read(block_start, HEADER_LENGTH)?;
    (bytes[0] == FLAG_BLOCK).then(|| Header {
        records_end: usize::from(u16::from_le_bytes([bytes[1], bytes[2]])),
        restart_count: usize::from(u16::from_le_bytes([bytes[3], bytes[4]])),
    })
}

/// `TryReadRecordRange`: the block-relative start and end of the records of the data block at `block_start`, or `None`
/// for an unreadable block or another kind of block.
pub fn try_read_record_range(reader: &ByteReader<'_>, block_start: usize) -> Option<(usize, usize)> {
    let header = read_header(reader, block_start)?;
    Some((HEADER_LENGTH + 2 * header.restart_count, header.records_end))
}

/// `SeekCeiling`: the first record of the data block at `block_start` whose key is at least `target`, its key copied
/// into `key_buffer`; `None` when the block is empty or every key is below `target`.
///
/// Like C#, a block that fits its first 4 KiB is read with one access to that page, then searched in memory.
pub fn seek_ceiling(
    reader: &ByteReader<'_>,
    block_start: usize,
    target: &[u8],
    key_buffer: &mut [u8; KEY_BUFFER_LENGTH],
) -> TierResult<Option<Ceiling>> {
    let Some(remaining) = reader.len().checked_sub(block_start).filter(|&remaining| remaining > 0) else {
        return Ok(None);
    };
    let page_length = remaining.min(BLOCK_SIZE);
    let Some(page_bytes) = reader.read(block_start, page_length) else {
        return Ok(None);
    };
    let page = ByteReader::new(page_bytes);
    let Some(header) = read_header(&page, 0).filter(|header| header.restart_count > 0) else {
        return Ok(None);
    };

    if header.records_end <= page_length {
        let ceiling = seek_ceiling_core(&page, 0, &header, target, key_buffer)?;
        return Ok(ceiling.map(|ceiling| Ceiling {
            value_offset: block_start + ceiling.value_offset,
            ..ceiling
        }));
    }
    seek_ceiling_core(reader, block_start, &header, target, key_buffer)
}

/// `SeekCeilingCore`: the restart binary search, then a forward scan to the end of the records.
fn seek_ceiling_core(
    reader: &ByteReader<'_>,
    block_start: usize,
    header: &Header,
    target: &[u8],
    key_buffer: &mut [u8; KEY_BUFFER_LENGTH],
) -> TierResult<Option<Ceiling>> {
    let restart_table_start = block_start + HEADER_LENGTH;
    let end = block_start + header.records_end;
    let offsets_length = 2 * header.restart_count;
    let Some(offsets) = reader.read(restart_table_start, offsets_length) else {
        return Ok(None);
    };
    let records_start = HEADER_LENGTH + offsets_length;
    let restart = |index: usize| -> TierResult<usize> {
        let offset = usize::from(u16::from_le_bytes([offsets[2 * index], offsets[2 * index + 1]]));
        check_restart_offset(offset, records_start, header.records_end)
    };

    // The rightmost restart whose (full) key is at most the target.
    let (mut low, mut high, mut found) = (0isize, header.restart_count as isize - 1, None);
    while low <= high {
        let middle = low + ((high - low) >> 1);
        let record_start = block_start + restart(middle as usize)?;
        let Some(record) = reader.read(record_start, RECORD_HEADER_LENGTH) else {
            return Ok(None);
        };
        let key = read_or_corrupt(reader, record_start + RECORD_HEADER_LENGTH, usize::from(record[1]))?;
        if key <= target {
            found = Some(middle as usize);
            low = middle + 1;
        } else {
            high = middle - 1;
        }
    }

    let mut position = block_start + restart(found.unwrap_or(0))?;
    let mut prev_key_length = 0;
    while position < end {
        let Some(record) = reader.read(position, RECORD_HEADER_LENGTH) else {
            return Ok(None);
        };
        let (common_prefix, suffix_length, value_length) =
            (usize::from(record[0]), usize::from(record[1]), usize::from(record[2]));
        if common_prefix > prev_key_length {
            return Err(corrupt(format_args!(
                "data record at byte {position} declares common-prefix {common_prefix} exceeding the previous key length {prev_key_length}"
            )));
        }
        if common_prefix + suffix_length > KEY_BUFFER_LENGTH {
            return Err(corrupt(format_args!(
                "data record at byte {position} declares key length {common_prefix}+{suffix_length} exceeding the {KEY_BUFFER_LENGTH}-byte reader buffer"
            )));
        }
        let key_start = position + RECORD_HEADER_LENGTH;
        let Some(suffix) = reader.read(key_start, suffix_length) else {
            return Ok(None);
        };
        key_buffer[common_prefix..common_prefix + suffix_length].copy_from_slice(suffix);
        let key_length = common_prefix + suffix_length;
        prev_key_length = key_length;
        let value_offset = key_start + suffix_length;

        if target <= &key_buffer[..key_length] {
            return Ok(Some(Ceiling {
                key_length,
                value_offset,
                value_length,
            }));
        }
        position = value_offset + value_length;
    }
    Ok(None)
}

/// `CheckRestartOffset`: a restart must point into `[recordsStart, recordsEnd)`.
fn check_restart_offset(offset: usize, records_start: usize, records_end: usize) -> TierResult<usize> {
    if offset < records_start || offset >= records_end {
        return Err(corrupt(format_args!(
            "data block restart offset {offset} is outside the records region [{records_start}, {records_end})"
        )));
    }
    Ok(offset)
}

/// A `PinBuffer` that C# would fail with `ArgumentOutOfRangeException` on a record overrunning the table.
pub(super) fn read_or_corrupt<'a>(reader: &ByteReader<'a>, offset: usize, length: usize) -> TierResult<&'a [u8]> {
    reader.read(offset, length).ok_or_else(|| {
        corrupt(format_args!(
            "a {length}-byte read at byte {offset} overruns the {}-byte table",
            reader.len()
        ))
    })
}
