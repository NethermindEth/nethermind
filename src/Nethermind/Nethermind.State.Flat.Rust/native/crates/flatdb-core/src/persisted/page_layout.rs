//! Port of `Nethermind.State.Flat/PageLayout.cs` and `Nethermind.State.Flat/NodeRef.cs`: the 4 KiB logical page the
//! on-disk writers align to, and the 6-byte reference a table stores in place of a trie node's RLP.

/// `PageLayout.PageSize`: the logical page of blob files and sorted-table data blocks, whatever the OS page size.
pub const PAGE_SIZE: usize = 4096;

/// `PageLayout.PageMask`: the in-page offset of a file-absolute offset.
pub const PAGE_MASK: u64 = PAGE_SIZE as u64 - 1;

/// Where a trie node's RLP lies: the blob file and the file-absolute offset of its first byte. The length is read off
/// the RLP header, so none is stored.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub struct NodeRef {
    pub blob_arena_id: u16,
    /// 32-bit, so a blob file holds at most 2 GiB; the blob writer refuses a write past it.
    pub rlp_data_offset: i32,
}

impl NodeRef {
    /// `NodeRef.Size`.
    pub const SIZE: usize = 6;

    pub const fn new(blob_arena_id: u16, rlp_data_offset: i32) -> Self {
        Self {
            blob_arena_id,
            rlp_data_offset,
        }
    }

    /// `NodeRef.Read`: the id then the offset, little-endian.
    ///
    /// # Panics
    /// When `data` is shorter than [`Self::SIZE`].
    pub fn read(data: &[u8]) -> Self {
        Self {
            blob_arena_id: u16::from_le_bytes([data[0], data[1]]),
            rlp_data_offset: i32::from_le_bytes([data[2], data[3], data[4], data[5]]),
        }
    }

    /// `NodeRef.Write`.
    pub fn to_bytes(self) -> [u8; Self::SIZE] {
        let mut bytes = [0u8; Self::SIZE];
        bytes[..2].copy_from_slice(&self.blob_arena_id.to_le_bytes());
        bytes[2..].copy_from_slice(&self.rlp_data_offset.to_le_bytes());
        bytes
    }
}

#[cfg(test)]
mod tests {
    use super::NodeRef;
    use rstest::rstest;

    #[rstest]
    #[case(0, 0, [0, 0, 0, 0, 0, 0])]
    #[case(42, 12345, [0x2A, 0x00, 0x39, 0x30, 0x00, 0x00])]
    #[case(u16::MAX, i32::MAX, [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F])]
    fn node_ref_round_trips_little_endian(#[case] id: u16, #[case] offset: i32, #[case] bytes: [u8; 6]) {
        let node_ref = NodeRef::new(id, offset);
        assert_eq!(node_ref.to_bytes(), bytes);
        assert_eq!(NodeRef::read(&bytes), node_ref);
    }
}
