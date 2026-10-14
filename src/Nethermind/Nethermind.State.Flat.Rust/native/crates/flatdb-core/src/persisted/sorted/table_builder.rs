//! Port of `Nethermind.State.Flat/PersistedSnapshots/Sorted/SortedTableBuilder.cs`: streams records, added in strictly
//! ascending key order, into 4 KiB data blocks, then writes the index and the footer.

use super::KEY_BUFFER_LENGTH;
use super::block::{FLAG_BLOCK, FLAG_INDEX};
use super::block_builder::BlockBuilder;
use super::table::{BLOCK_SIZE, DEFAULT_RESTART_INTERVAL, FORMAT_VERSION};
use crate::persisted::io::TableSink;
use crate::persisted::{TierError, TierResult};

/// Only the current data block and the index are buffered; each index entry maps the shortest separator between a
/// block's last key and the next block's first key (the last block's own last key) to the block's offset.
pub struct SortedTableBuilder<'s, S: TableSink> {
    sink: &'s mut S,
    table_start: u64,
    data_block: BlockBuilder,
    index_block: BlockBuilder,
    /// The last key added, which is also the last key of the current data block.
    prev_key: Vec<u8>,
    count: u64,
}

impl<'s, S: TableSink> SortedTableBuilder<'s, S> {
    pub fn new(sink: &'s mut S) -> Self {
        let table_start = sink.written();
        Self {
            sink,
            table_start,
            data_block: BlockBuilder::new(DEFAULT_RESTART_INTERVAL, BLOCK_SIZE),
            index_block: BlockBuilder::new(DEFAULT_RESTART_INTERVAL, 4096),
            prev_key: Vec::with_capacity(KEY_BUFFER_LENGTH),
            count: 0,
        }
    }

    /// `Add`. A key not strictly above the previous one fails as C# throws `ArgumentException`; so does a key or value
    /// longer than 255 bytes, which C# would truncate silently and which no caller produces.
    pub fn add(&mut self, key: &[u8], value: &[u8]) -> TierResult<()> {
        if self.count > 0 && self.prev_key.as_slice() >= key {
            return Err(TierError::InvalidArgument(
                "Keys must be added in strictly ascending order. (Parameter 'key')".to_owned(),
            ));
        }
        if key.len() >= KEY_BUFFER_LENGTH || value.len() >= KEY_BUFFER_LENGTH {
            return Err(TierError::InvalidArgument(format!(
                "A record of a {}-byte key and a {}-byte value does not fit the 255-byte fields.",
                key.len(),
                value.len()
            )));
        }

        if self.data_block.record_count() > 0 && self.data_block.would_cross_page(key.len(), value.len()) {
            self.flush_data_block(Some(key))?;
        }

        self.data_block.add(key, value);
        self.prev_key.clear();
        self.prev_key.extend_from_slice(key);
        self.count += 1;
        Ok(())
    }

    /// `Build`: the last data block, the index block right after it, then the footer.
    pub fn build(mut self) -> TierResult<()> {
        if self.data_block.record_count() > 0 {
            self.flush_data_block(None)?;
        }

        let index_offset = self.sink.written() - self.table_start;
        self.index_block.finish(self.sink, FLAG_INDEX)?;

        let mut footer = [0u8; 9];
        footer[..8].copy_from_slice(&(index_offset as i64).to_le_bytes());
        footer[8] = FORMAT_VERSION;
        self.sink.write(&footer)
    }

    /// `FlushDataBlock`: writes the current block, pads it to 4 KiB unless it is the last (`next_first_key` is `None`),
    /// and indexes it.
    fn flush_data_block(&mut self, next_first_key: Option<&[u8]>) -> TierResult<()> {
        let block_offset = self.sink.written() - self.table_start;
        self.data_block.finish(self.sink, FLAG_BLOCK)?;
        if next_first_key.is_some() {
            let written = self.sink.written() - self.table_start;
            let padding = written.wrapping_neg() & (BLOCK_SIZE as u64 - 1);
            self.sink.write(&vec![0u8; padding as usize])?;
        }

        let separator = match next_first_key {
            None => self.prev_key.clone(),
            Some(next) => find_shortest_separator(&self.prev_key, next),
        };
        self.index_block.add_changed_prefix_value(&separator, block_offset);
        self.data_block.reset();
        Ok(())
    }
}

/// `FindShortestSeparator`: the shortest `s` with `a <= s < b` for `a < b`, else `a`: the common prefix and the next
/// byte of `a` plus one, when that stays below `b`'s byte.
fn find_shortest_separator(a: &[u8], b: &[u8]) -> Vec<u8> {
    let common = a.iter().zip(b).take_while(|(x, y)| x == y).count();
    if common < a.len().min(b.len()) && u16::from(a[common]) + 1 < u16::from(b[common]) {
        let mut separator = a[..common].to_vec();
        separator.push(a[common] + 1);
        return separator;
    }
    a.to_vec()
}

#[cfg(test)]
mod tests {
    use super::find_shortest_separator;
    use rstest::rstest;

    #[rstest]
    #[case(&[0x01, 0x10], &[0x01, 0x30], &[0x01, 0x11])]
    #[case(&[0x01, 0x10], &[0x01, 0x11], &[0x01, 0x10])]
    #[case(&[0x01], &[0x01, 0x05], &[0x01])]
    #[case(&[0x01, 0xFF, 0x00], &[0x02], &[0x01, 0xFF, 0x00])]
    #[case(&[0x01, 0x10, 0x00], &[0x03], &[0x02])]
    fn separator_is_the_shortest_key_between(#[case] a: &[u8], #[case] b: &[u8], #[case] separator: &[u8]) {
        assert_eq!(find_shortest_separator(a, b), separator);
    }
}
