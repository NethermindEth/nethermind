//! Port of `Nethermind.State.Flat/PersistedSnapshots/Sorted/BlockBuilder.cs`: builds one [`super::block`] from records
//! added in ascending key order.

use super::block::{self, RECORD_HEADER_LENGTH};
use crate::persisted::io::TableSink;
use crate::persisted::page_layout::PAGE_SIZE;
use crate::persisted::{TierError, TierResult};

pub struct BlockBuilder {
    restart_interval: usize,
    body: Vec<u8>,
    restarts: Vec<usize>,
    prev_key: Vec<u8>,
    /// The previous index value, against which [`Self::add_changed_prefix_value`] finds the changed low bytes.
    prev_value: u64,
    record_count: usize,
}

impl BlockBuilder {
    pub fn new(restart_interval: usize, expected_bytes: usize) -> Self {
        Self {
            restart_interval,
            body: Vec::with_capacity(expected_bytes.max(64)),
            restarts: Vec::with_capacity(64),
            prev_key: Vec::with_capacity(256),
            prev_value: 0,
            record_count: 0,
        }
    }

    pub fn record_count(&self) -> usize {
        self.record_count
    }

    /// `Add`: a data record `[cp][suffixLen][valueLen][keySuffix][value]`. Keys arrive ascending, at most 255 bytes, as
    /// values.
    pub fn add(&mut self, key: &[u8], value: &[u8]) {
        let common_prefix = self.start_record(key);
        self.body.extend_from_slice(&[
            common_prefix as u8,
            (key.len() - common_prefix) as u8,
            value.len() as u8,
        ]);
        self.body.extend_from_slice(&key[common_prefix..]);
        self.body.extend_from_slice(value);
        self.end_record(key);
    }

    /// `AddChangedPrefixValue`: an index record whose value, a data block offset below 2^48, is stored as the low
    /// little-endian bytes that differ from the previous record's value (from 0 at a restart).
    pub fn add_changed_prefix_value(&mut self, key: &[u8], value: u64) {
        debug_assert!(value >> 48 == 0, "index value must fit in 48 bits");
        let common_prefix = self.start_record(key);
        let diff = value ^ if common_prefix == 0 { 0 } else { self.prev_value };
        let changed = if diff == 0 { 0 } else { diff.ilog2() as usize / 8 + 1 };

        self.body
            .extend_from_slice(&[common_prefix as u8, (key.len() - common_prefix) as u8, changed as u8]);
        self.body.extend_from_slice(&key[common_prefix..]);
        self.body.extend_from_slice(&value.to_le_bytes()[..changed]);
        self.prev_value = value;
        self.end_record(key);
    }

    /// `StartRecord`: the common prefix with the previous key, 0 at every restart interval, and a restart entry for
    /// every record that ends up with 0.
    fn start_record(&mut self, key: &[u8]) -> usize {
        let common_prefix = if self.record_count.is_multiple_of(self.restart_interval) {
            0
        } else {
            self.prev_key.iter().zip(key).take_while(|(a, b)| a == b).count()
        };
        if common_prefix == 0 {
            self.restarts.push(self.body.len());
        }
        common_prefix
    }

    fn end_record(&mut self, key: &[u8]) {
        self.prev_key.clear();
        self.prev_key.extend_from_slice(key);
        self.record_count += 1;
    }

    /// `WouldCrossPage`: whether a record of these lengths, which may need a restart entry, would push a 2-byte-offset
    /// block past one 4 KiB page.
    pub fn would_cross_page(&self, key_length: usize, value_length: usize) -> bool {
        let header = block::records_start(2, self.restarts.len() + 1);
        header + self.body.len() + RECORD_HEADER_LENGTH + key_length + value_length > PAGE_SIZE
    }

    /// `Finish`: writes the block under `flag`. An offset past the flag's width fails, as the
    /// C# `checked` cast throws `OverflowException`.
    pub fn finish(&self, sink: &mut impl TableSink, flag: u8) -> TierResult<()> {
        let width = block::width_from_flag(flag);
        let records_start = block::records_start(width, self.restarts.len());
        let records_end = records_start + self.body.len();

        let mut bytes = Vec::with_capacity(records_end);
        bytes.push(flag);
        push_offset(&mut bytes, width, records_end)?;
        push_offset(&mut bytes, width, self.restarts.len())?;
        for restart in &self.restarts {
            push_offset(&mut bytes, width, records_start + restart)?;
        }
        bytes.extend_from_slice(&self.body);
        sink.write(&bytes)
    }

    pub fn reset(&mut self) {
        self.body.clear();
        self.restarts.clear();
        self.prev_key.clear();
        self.prev_value = 0;
        self.record_count = 0;
    }
}

fn push_offset(bytes: &mut Vec<u8>, width: usize, value: usize) -> TierResult<()> {
    let overflow = || TierError::InvalidOperation("Arithmetic operation resulted in an overflow.".to_owned());
    if width == 2 {
        bytes.extend_from_slice(&u16::try_from(value).map_err(|_| overflow())?.to_le_bytes());
    } else {
        bytes.extend_from_slice(&u32::try_from(value).map_err(|_| overflow())?.to_le_bytes());
    }
    Ok(())
}
