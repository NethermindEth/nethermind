//! Port of `Nethermind.State.Flat/PersistedSnapshots/Sorted/SortedTableReader.cs`: the point lookup of a table.

use super::KEY_BUFFER_LENGTH;
use super::{data_block_reader, index_block_reader, table};
use crate::persisted::TierResult;
use crate::persisted::io::ByteReader;

/// `TrySeek`: the value of `key` in the table `reader` covers, as `(offset, length)` in the reader; `None` on a miss,
/// for an empty table and for one whose footer does not read.
pub fn try_seek(reader: &ByteReader<'_>, key: &[u8]) -> TierResult<Option<(usize, usize)>> {
    let Some(footer) = table::try_read_footer(reader) else {
        return Ok(None);
    };

    let mut separator = [0u8; KEY_BUFFER_LENGTH];
    let Some(index) = index_block_reader::seek_ceiling(reader, footer.index_offset, key, &mut separator)? else {
        return Ok(None);
    };
    let Ok(block_start) = usize::try_from(index.byte_offset) else {
        return Ok(None);
    };

    let mut key_buffer = [0u8; KEY_BUFFER_LENGTH];
    let Some(ceiling) = data_block_reader::seek_ceiling(reader, block_start, key, &mut key_buffer)? else {
        return Ok(None);
    };
    Ok((&key_buffer[..ceiling.key_length] == key).then_some((ceiling.value_offset, ceiling.value_length)))
}

/// [`try_seek`] returning the value's bytes.
pub fn get<'a>(reader: &ByteReader<'a>, key: &[u8]) -> TierResult<Option<&'a [u8]>> {
    Ok(try_seek(reader, key)?.and_then(|(offset, length)| reader.read(offset, length)))
}
