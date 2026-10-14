//! Port of `Nethermind.State.Flat/PersistedSnapshots/Sorted/IndexBlockReader.cs`: the ceiling seek of the index block,
//! rebuilding the changed-prefix coded data block offsets.

use super::KEY_BUFFER_LENGTH;
use super::block::{FLAG_INDEX, RECORD_HEADER_LENGTH};
use super::data_block_reader::read_or_corrupt;
use super::table::corrupt;
use crate::persisted::TierResult;
use crate::persisted::io::ByteReader;

/// `[flag u8][recordsEnd u32][numRestarts u32]`.
const HEADER_LENGTH: usize = 9;

/// The most bytes a u48 value stores.
const MAX_CHANGED_LENGTH: usize = 6;

/// A separator found by a seek: its key in the caller's buffer and the offset of its data block.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct IndexCeiling {
    pub key_length: usize,
    pub byte_offset: u64,
}

struct Header {
    records_end: usize,
    restart_count: usize,
}

fn read_header(reader: &ByteReader<'_>, block_start: usize) -> Option<Header> {
    let bytes = reader.read(block_start, HEADER_LENGTH)?;
    (bytes[0] == FLAG_INDEX).then(|| Header {
        records_end: u32::from_le_bytes(bytes[1..5].try_into().expect("4 bytes")) as usize,
        restart_count: u32::from_le_bytes(bytes[5..9].try_into().expect("4 bytes")) as usize,
    })
}

/// `TryReadRecordRange`: the block-relative start and end of the index records.
pub fn try_read_record_range(reader: &ByteReader<'_>, block_start: usize) -> Option<(usize, usize)> {
    let header = read_header(reader, block_start)?;
    Some((HEADER_LENGTH + 4 * header.restart_count, header.records_end))
}

/// `SeekCeiling`: the first separator of the index at `block_start` that is at least `target`, with its data block's
/// offset; `None` when the index is empty or every separator is below `target`.
pub fn seek_ceiling(
    reader: &ByteReader<'_>,
    block_start: usize,
    target: &[u8],
    key_buffer: &mut [u8; KEY_BUFFER_LENGTH],
) -> TierResult<Option<IndexCeiling>> {
    let Some(header) = read_header(reader, block_start).filter(|header| header.restart_count > 0) else {
        return Ok(None);
    };
    let restart_table_start = block_start + HEADER_LENGTH;
    let end = block_start + header.records_end;
    let records_start = HEADER_LENGTH + 4 * header.restart_count;
    // The restart table can be large, so each step reads only the offset it needs.
    let restart = |index: usize| -> TierResult<Option<usize>> {
        let Some(bytes) = reader.read(restart_table_start + 4 * index, 4) else {
            return Ok(None);
        };
        let offset = u32::from_le_bytes(bytes.try_into().expect("4 bytes")) as usize;
        check_restart_offset(offset, records_start, header.records_end).map(Some)
    };

    let (mut low, mut high, mut found) = (0isize, header.restart_count as isize - 1, None);
    while low <= high {
        let middle = low + ((high - low) >> 1);
        let Some(offset) = restart(middle as usize)? else {
            return Ok(None);
        };
        let record_start = block_start + offset;
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

    let Some(scan_offset) = restart(found.unwrap_or(0))? else {
        return Ok(None);
    };
    let mut position = block_start + scan_offset;
    let mut running_value = [0u8; 8];
    let mut prev_key_length = 0;
    while position < end {
        let Some(record) = reader.read(position, RECORD_HEADER_LENGTH) else {
            return Ok(None);
        };
        let (common_prefix, suffix_length, changed_length) =
            (usize::from(record[0]), usize::from(record[1]), usize::from(record[2]));
        if changed_length > MAX_CHANGED_LENGTH {
            return Err(corrupt(format_args!(
                "index record at byte {position} declares value-changed length {changed_length} exceeding the u48 maximum of 6"
            )));
        }
        if common_prefix > prev_key_length {
            return Err(corrupt(format_args!(
                "index record at byte {position} declares common-prefix {common_prefix} exceeding the previous key length {prev_key_length}"
            )));
        }
        if common_prefix + suffix_length > KEY_BUFFER_LENGTH {
            return Err(corrupt(format_args!(
                "index record at byte {position} declares key length {common_prefix}+{suffix_length} exceeding the {KEY_BUFFER_LENGTH}-byte reader buffer"
            )));
        }

        let key_start = position + RECORD_HEADER_LENGTH;
        let Some(suffix) = reader.read(key_start, suffix_length) else {
            return Ok(None);
        };
        key_buffer[common_prefix..common_prefix + suffix_length].copy_from_slice(suffix);
        let key_length = common_prefix + suffix_length;
        prev_key_length = key_length;
        let value_start = key_start + suffix_length;

        if common_prefix == 0 {
            running_value = [0; 8];
        }
        if changed_length > 0 {
            let Some(changed) = reader.read(value_start, changed_length) else {
                return Ok(None);
            };
            running_value[..changed_length].copy_from_slice(changed);
        }

        if target <= &key_buffer[..key_length] {
            return Ok(Some(IndexCeiling {
                key_length,
                byte_offset: u64::from_le_bytes(running_value),
            }));
        }
        position = value_start + changed_length;
    }
    Ok(None)
}

fn check_restart_offset(offset: usize, records_start: usize, records_end: usize) -> TierResult<usize> {
    if offset < records_start || offset >= records_end {
        return Err(corrupt(format_args!(
            "index block restart offset {offset} is outside the records region [{records_start}, {records_end})"
        )));
    }
    Ok(offset)
}
