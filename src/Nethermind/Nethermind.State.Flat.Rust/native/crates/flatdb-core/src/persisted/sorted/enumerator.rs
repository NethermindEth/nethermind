//! Port of `Nethermind.State.Flat/PersistedSnapshots/Sorted/SortedTableEnumerator.cs`: a forward cursor over a table in
//! ascending key order.

use super::KEY_BUFFER_LENGTH;
use super::block::RECORD_HEADER_LENGTH;
use super::table::corrupt;
use super::{data_block_reader, index_block_reader, table};
use crate::persisted::TierResult;
use crate::persisted::io::ByteReader;

/// Visits the data blocks in the order of the index records, whose values locate them, and the records of each block
/// up to its records end, never the padding. It does not hold the reader, which each [`Self::move_next`] takes, so the
/// merger can keep one per source; the current key is copied, so it outlives the reads.
pub struct SortedTableEnumerator {
    index_position: usize,
    index_end: usize,
    /// The running data block offset, each index record overwriting its changed low bytes.
    index_running_value: [u8; 8],
    position: usize,
    block_end: usize,
    key_buffer: [u8; KEY_BUFFER_LENGTH],
    key_length: usize,
    value: (usize, usize),
}

impl SortedTableEnumerator {
    pub fn new(reader: &ByteReader<'_>) -> Self {
        let mut enumerator = Self {
            index_position: 0,
            index_end: 0,
            index_running_value: [0; 8],
            position: 0,
            block_end: 0,
            key_buffer: [0; KEY_BUFFER_LENGTH],
            key_length: 0,
            value: (0, 0),
        };
        if let Some(footer) = table::try_read_footer(reader)
            && let Some((start, end)) = index_block_reader::try_read_record_range(reader, footer.index_offset)
        {
            enumerator.index_position = footer.index_offset + start;
            enumerator.index_end = footer.index_offset + end;
        }
        enumerator
    }

    /// `MoveNext`: `false` past the last record, or when a read falls outside the table, as C# ends the walk there.
    pub fn move_next(&mut self, reader: &ByteReader<'_>) -> TierResult<bool> {
        while self.position >= self.block_end {
            if !self.try_advance_to_next_data_block(reader)? {
                return Ok(false);
            }
        }

        let Some(record) = reader.read(self.position, RECORD_HEADER_LENGTH) else {
            return Ok(false);
        };
        let (common_prefix, suffix_length, value_length) =
            (usize::from(record[0]), usize::from(record[1]), usize::from(record[2]));
        if common_prefix > self.key_length || common_prefix + suffix_length > KEY_BUFFER_LENGTH {
            return Err(corrupt(format_args!(
                "data record at byte {} declares common-prefix {common_prefix} and suffix {suffix_length} inconsistent with the previous key length {} / {KEY_BUFFER_LENGTH}-byte buffer",
                self.position, self.key_length
            )));
        }
        let key_start = self.position + RECORD_HEADER_LENGTH;
        let Some(suffix) = reader.read(key_start, suffix_length) else {
            return Ok(false);
        };
        self.key_buffer[common_prefix..common_prefix + suffix_length].copy_from_slice(suffix);
        self.key_length = common_prefix + suffix_length;

        let value_start = key_start + suffix_length;
        self.value = (value_start, value_length);
        self.position = value_start + value_length;
        Ok(true)
    }

    /// `TryAdvanceToNextDataBlock`: the next index record's offset, and the cursor on that block's first record.
    fn try_advance_to_next_data_block(&mut self, reader: &ByteReader<'_>) -> TierResult<bool> {
        if self.index_position >= self.index_end {
            return Ok(false);
        }

        let Some(record) = reader.read(self.index_position, RECORD_HEADER_LENGTH) else {
            return Ok(false);
        };
        let changed_length = usize::from(record[2]);
        if changed_length > 6 {
            return Err(corrupt(format_args!(
                "index record at byte {} declares value-changed length {changed_length} exceeding the u48 maximum of 6",
                self.index_position
            )));
        }

        let value_start = self.index_position + RECORD_HEADER_LENGTH + usize::from(record[1]);
        if record[0] == 0 {
            self.index_running_value = [0; 8];
        }
        if changed_length > 0 {
            let Some(changed) = reader.read(value_start, changed_length) else {
                return Ok(false);
            };
            self.index_running_value[..changed_length].copy_from_slice(changed);
        }
        self.index_position = value_start + changed_length;

        let Ok(block_start) = usize::try_from(u64::from_le_bytes(self.index_running_value)) else {
            return Ok(false);
        };
        let Some((records_start, records_end)) = data_block_reader::try_read_record_range(reader, block_start) else {
            return Ok(false);
        };
        self.position = block_start + records_start;
        self.block_end = block_start + records_end;
        Ok(true)
    }

    pub fn current_key(&self) -> &[u8] {
        &self.key_buffer[..self.key_length]
    }

    /// The current value, as `(offset, length)` in the reader.
    pub fn current_value(&self) -> (usize, usize) {
        self.value
    }
}
