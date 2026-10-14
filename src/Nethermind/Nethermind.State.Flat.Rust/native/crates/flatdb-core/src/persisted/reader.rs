//! Port of `Nethermind.State.Flat/PersistedSnapshots/PersistedSnapshotReader.cs` and of the reads of
//! `PersistedSnapshot.cs`: point lookups in one table, the decoding of their values, and the resolution of a
//! [`NodeRef`] into the trie-node RLP of a blob file.

use super::codec::BlobRange;
use super::io::ByteReader;
use super::keys::{self, ACCOUNT_DELETED_MARKER, REF_ID_COLUMN, SELF_DESTRUCT_DESTRUCTED_MARKER};
use super::page_layout::NodeRef;
use super::sorted::enumerator::SortedTableEnumerator;
use super::sorted::table::corrupt;
use super::sorted::table_reader;
use super::{TierError, TierResult};
use crate::codec::account::decode_account;
use crate::codec::rlp;
use crate::codec::slot::{RLP_SLOT_VALUE_BUFFER_SIZE, decode_slot_value};
use crate::types::{Account, Address, Hash256, TreePath, U256};

/// `MaxTrieNodeRlpBytes`: a branch node with 17 hashes and a long-list header fits in one read of this many bytes.
pub const MAX_TRIE_NODE_RLP_BYTES: usize = 568;

/// The largest account value a table holds; anything longer is corrupt (`MaxAccountRlp`).
const MAX_ACCOUNT_RLP: usize = 256;

/// The blob files a table's [`NodeRef`]s point into.
pub trait BlobSource {
    /// `BlobArenaFile.RandomRead`: fills `buffer` from `offset` of blob file `blob_arena_id`, returning the bytes read,
    /// fewer at the end of the file.
    fn random_read(&self, blob_arena_id: u16, offset: i32, buffer: &mut [u8]) -> TierResult<usize>;
}

/// The value at `(offset, length)`; a value overrunning the table is corrupt, as C# fails to pin it.
pub fn read_value<'a>(reader: &ByteReader<'a>, offset: usize, length: usize) -> TierResult<&'a [u8]> {
    reader.read(offset, length).ok_or_else(|| {
        corrupt(format_args!(
            "a {length}-byte value at byte {offset} overruns the table"
        ))
    })
}

/// A slot value: empty is a deleted slot, else the RLP-wrapped big-endian value (`DecodeSlot`).
pub fn decode_slot(reader: &ByteReader<'_>, offset: usize, length: usize) -> TierResult<Option<U256>> {
    if length == 0 {
        return Ok(None);
    }
    if length > RLP_SLOT_VALUE_BUFFER_SIZE {
        return Err(corrupt(format_args!(
            "storage RLP length {length} exceeds the {RLP_SLOT_VALUE_BUFFER_SIZE}-byte maximum"
        )));
    }
    let (bytes, _) = rlp::decode_bytes(read_value(reader, offset, length)?, 0)
        .map_err(|error| TierError::Corrupt(error.to_string()))?;
    decode_slot_value(bytes)
        .map(Some)
        .ok_or_else(|| TierError::Corrupt(format!("a slot value of {} bytes", bytes.len())))
}

/// One table and the blob files its trie nodes point into: the point reads of `PersistedSnapshot`.
pub struct TableView<'r, 'a> {
    reader: &'r ByteReader<'a>,
    blobs: &'r dyn BlobSource,
}

impl<'r, 'a> TableView<'r, 'a> {
    pub fn new(reader: &'r ByteReader<'a>, blobs: &'r dyn BlobSource) -> Self {
        Self { reader, blobs }
    }

    /// `TryGetAccount`: `Some(None)` for a deletion marker, `None` when the table does not hold the address.
    pub fn try_get_account(&self, address: &Address) -> TierResult<Option<Option<Account>>> {
        let Some((offset, length)) = table_reader::try_seek(self.reader, keys::account_key(address).as_slice())? else {
            return Ok(None);
        };
        if length > MAX_ACCOUNT_RLP {
            return Err(corrupt(format_args!(
                "account RLP length {length} exceeds the {MAX_ACCOUNT_RLP}-byte maximum"
            )));
        }
        let rlp = read_value(self.reader, offset, length)?;
        if rlp == [ACCOUNT_DELETED_MARKER] {
            return Ok(Some(None));
        }
        decode_account(rlp, true)
            .map(Some)
            .map_err(|error| TierError::Corrupt(error.to_string()))
    }

    /// `TryGetSlot`: `Some(None)` for a deleted slot, `None` when the table does not hold it.
    pub fn try_get_slot(&self, address: &Address, slot: &U256) -> TierResult<Option<Option<U256>>> {
        let key = keys::slot_key(address, &slot.to_be_bytes());
        let Some((offset, length)) = table_reader::try_seek(self.reader, key.as_slice())? else {
            return Ok(None);
        };
        decode_slot(self.reader, offset, length).map(Some)
    }

    /// `TryGetSelfDestructFlag`: `Some(false)` destructed, `Some(true)` new account, `None` without a record.
    pub fn try_get_self_destruct_flag(&self, address: &Address) -> TierResult<Option<bool>> {
        let key = keys::self_destruct_key(address);
        let Some((offset, length)) = table_reader::try_seek(self.reader, key.as_slice())? else {
            return Ok(None);
        };
        if length == 0 {
            return Ok(None);
        }
        Ok(self
            .reader
            .read_u8(offset)
            .map(|flag| flag != SELF_DESTRUCT_DESTRUCTED_MARKER))
    }

    /// `TryLoadStateNodeRlp`.
    pub fn try_load_state_node_rlp(&self, path: &TreePath) -> TierResult<Option<Vec<u8>>> {
        self.load_node(keys::state_node_key(path).as_slice())
    }

    /// `TryLoadStorageNodeRlp`.
    pub fn try_load_storage_node_rlp(&self, address_hash: &Hash256, path: &TreePath) -> TierResult<Option<Vec<u8>>> {
        self.load_node(keys::storage_node_key(address_hash, path).as_slice())
    }

    fn load_node(&self, key: &[u8]) -> TierResult<Option<Vec<u8>>> {
        let Some((offset, length)) = table_reader::try_seek(self.reader, key)? else {
            return Ok(None);
        };
        resolve_trie_rlp(read_value(self.reader, offset, length)?, self.blobs).map(Some)
    }
}

/// `ResolveTrieRlp`: the RLP a table value's [`NodeRef`] points to. A value shorter than a `NodeRef` is zero-padded, as
/// C# copies it over a zeroed one; a longer one is corrupt.
pub fn resolve_trie_rlp(node_ref: &[u8], blobs: &dyn BlobSource) -> TierResult<Vec<u8>> {
    if node_ref.len() > NodeRef::SIZE {
        return Err(corrupt(format_args!(
            "a trie-node reference of {} bytes",
            node_ref.len()
        )));
    }
    let mut bytes = [0u8; NodeRef::SIZE];
    bytes[..node_ref.len()].copy_from_slice(node_ref);
    let node_ref = NodeRef::read(&bytes);

    let mut buffer = [0u8; MAX_TRIE_NODE_RLP_BYTES];
    let read = blobs.random_read(node_ref.blob_arena_id, node_ref.rlp_data_offset, &mut buffer)?;
    let total = rlp::peek_next_length(&buffer[..read], 0).map_err(|error| TierError::Corrupt(error.to_string()))?;
    if total > read {
        return Err(TierError::Corrupt(format!(
            "Trie-node RLP at blob arena {}+{} declares {total} bytes but only {read} were read (MaxTrieNodeRlpBytes = {MAX_TRIE_NODE_RLP_BYTES}).",
            node_ref.blob_arena_id, node_ref.rlp_data_offset
        )));
    }
    Ok(buffer[..total].to_vec())
}

/// `ReadBlobRange`: the `blob_range` metadata of a base table, [`BlobRange::NONE`] when absent or malformed.
pub fn read_blob_range(reader: &ByteReader<'_>) -> TierResult<BlobRange> {
    Ok(
        table_reader::get(reader, keys::metadata_key(keys::METADATA_BLOB_RANGE).as_slice())?
            .map_or(BlobRange::NONE, BlobRange::read),
    )
}

/// `RefIdsEnumerator`: the blob files the table references, from its leading ref-id records.
pub fn read_ref_ids(reader: &ByteReader<'_>) -> TierResult<Vec<u16>> {
    let mut enumerator = SortedTableEnumerator::new(reader);
    let mut ids = Vec::new();
    while keys::move_next_checked(&mut enumerator, reader)? {
        let key = enumerator.current_key();
        if key.first() != Some(&REF_ID_COLUMN) {
            break;
        }
        ids.push(keys::read_ref_id(key));
    }
    Ok(ids)
}
