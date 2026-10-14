//! Port of the write path of `Nethermind.State.Flat/PersistedSnapshots/Storage/BlobArenaWriter.cs`: appends trie-node
//! RLP to a blob file, padding so that no item of at most 4 KiB straddles a 4 KiB page.
//!
//! The writer appends to any [`Write`] from the file frontier it is given; the blob manager wraps it around a blob
//! file, the tests around a buffer.

use std::io::Write;

use crate::persisted::page_layout::{NodeRef, PAGE_MASK, PAGE_SIZE};
use crate::persisted::{TierError, TierResult};

/// `BufferSize`: writes reach the sink in chunks of at most 1 MiB.
const BUFFER_SIZE: usize = 1024 * 1024;

pub struct BlobWriter<W: Write> {
    sink: W,
    blob_arena_id: u16,
    start_offset: i64,
    /// The file-absolute offset of the next byte, pad bytes included.
    written: i64,
    buffer: Vec<u8>,
}

impl<W: Write> BlobWriter<W> {
    /// A writer appending to `sink`, which continues blob file `blob_arena_id` at its frontier `start_offset`.
    pub fn new(sink: W, blob_arena_id: u16, start_offset: i64) -> Self {
        Self {
            sink,
            blob_arena_id,
            start_offset,
            written: start_offset,
            buffer: Vec::with_capacity(BUFFER_SIZE),
        }
    }

    /// `BlobArenaId`: the id every returned [`NodeRef`] carries.
    pub fn blob_arena_id(&self) -> u16 {
        self.blob_arena_id
    }

    /// `StartOffset`: where the run of this writer starts in the file.
    pub fn start_offset(&self) -> i64 {
        self.start_offset
    }

    /// `Written`: the file-absolute offset of the next byte.
    pub fn written(&self) -> i64 {
        self.written
    }

    /// `WriteRlp`: appends `rlp`, first padding to the next page when it would cross one. An item past 2 GiB fails,
    /// as its offset would not fit the `NodeRef`.
    pub fn write_rlp(&mut self, rlp: &[u8]) -> TierResult<NodeRef> {
        let offset_in_page = self.written as u64 & PAGE_MASK;
        if rlp.len() <= PAGE_SIZE && offset_in_page != 0 && offset_in_page as usize + rlp.len() > PAGE_SIZE {
            let padding = PAGE_SIZE - offset_in_page as usize;
            self.reserve(padding)?;
            self.buffer.resize(self.buffer.len() + padding, 0);
            self.written += padding as i64;
        }

        let offset = i32::try_from(self.written)
            .ok()
            .filter(|&offset| i64::from(offset) + rlp.len() as i64 <= i64::from(i32::MAX))
            .ok_or_else(|| {
                TierError::InvalidOperation(format!(
                    "BlobArenaWriter for blob arena {} would exceed the 2 GiB per-file NodeRef offset ceiling.",
                    self.blob_arena_id
                ))
            })?;
        self.reserve(rlp.len())?;
        self.buffer.extend_from_slice(rlp);
        self.written += rlp.len() as i64;
        Ok(NodeRef::new(self.blob_arena_id, offset))
    }

    /// Writes what is buffered and returns the sink; the frontier of the file is now [`Self::written`].
    pub fn finish(mut self) -> TierResult<W> {
        self.flush_buffer()?;
        self.sink.flush()?;
        Ok(self.sink)
    }

    /// `EnsureBufferSpace`: flushes when `length` more bytes would not fit the buffer.
    fn reserve(&mut self, length: usize) -> TierResult<()> {
        if length > BUFFER_SIZE - self.buffer.len() {
            self.flush_buffer()?;
        }
        Ok(())
    }

    fn flush_buffer(&mut self) -> TierResult<()> {
        if !self.buffer.is_empty() {
            self.sink.write_all(&self.buffer)?;
            self.buffer.clear();
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::BlobWriter;
    use crate::persisted::page_layout::NodeRef;

    #[test]
    fn items_are_padded_off_page_boundaries_from_the_frontier() {
        let mut writer = BlobWriter::new(Vec::new(), 7, 4000);
        assert_eq!(
            writer.write_rlp(&[0xC0; 96]).unwrap(),
            NodeRef::new(7, 4000),
            "4000 + 96 ends on the boundary"
        );
        assert_eq!(
            writer.write_rlp(&[0xC1; 100]).unwrap(),
            NodeRef::new(7, 4096),
            "4096 starts a page"
        );
        assert_eq!(
            writer.write_rlp(&[0xC2; 4000]).unwrap(),
            NodeRef::new(7, 8192),
            "4196 + 4000 would cross 8192"
        );
        assert_eq!(
            writer.write_rlp(&[0xC3; 5000]).unwrap(),
            NodeRef::new(7, 12192),
            "an item over 4 KiB is not padded"
        );
        assert_eq!((writer.start_offset(), writer.written()), (4000, 17192));

        let bytes = writer.finish().unwrap();
        assert_eq!(bytes.len(), 17192 - 4000);
        assert!(bytes[196..4192].iter().all(|&byte| byte == 0));
        assert_eq!(
            (bytes[0], bytes[96], bytes[4192], bytes[8192]),
            (0xC0, 0xC1, 0xC2, 0xC3)
        );

        let mut full = BlobWriter::new(Vec::new(), 1, i64::from(i32::MAX) - 2);
        assert!(full.write_rlp(&[0x80; 2]).is_ok());
        assert!(full.write_rlp(&[0x80]).is_err(), "past the 2 GiB NodeRef ceiling");
    }
}
