//! Key layouts of the flat and trie columns. Ports of the key encoders of `BaseFlatPersistence.cs` and
//! `BaseTriePersistence.cs`; their doc comments draw the layouts.
//!
//! Flat columns:
//! - account: `addressHash[0..20]` (20 bytes);
//! - storage, split (hashed layouts and `PreimageFlatV1`): `addressHash[0..4] | slotHash | addressHash[4..20]`
//!   (52 bytes), so RocksDB compares short prefixes and shortens index keys;
//! - storage, full (`PreimageFlat`): `addressHash[0..20] | slotHash` (52 bytes), as a preimage prefix is
//!   attacker-chosen.
//!
//! Trie columns, by path length:
//! - state 0-5 nibbles, `StateTopNodes`: `path[0..3]` with the length in the low nibble of byte 2;
//! - state 6-15, `StateNodes`: `path[0..8]` with the length in the low nibble of byte 7;
//! - state 16+, `FallbackNodes`: `0x00 | path | length` (34 bytes);
//! - storage 0-15, `StorageNodes`: `addressHash[0..4] | path[0..8] with length | addressHash[4..20]` (28 bytes);
//! - storage 16+, `FallbackNodes`: `0x01 | addressHash[0..4] | path | length | addressHash[4..20]` (54 bytes).

use crate::kv::Column;
use crate::types::{Hash256, TreePath};

/// `BaseFlatPersistence.AccountKeyLength`.
pub const ACCOUNT_KEY_LENGTH: usize = 20;
/// `BasePersistence.StoragePrefixPortion`: the address bytes leading a split storage or storage node key.
pub const STORAGE_PREFIX_PORTION: usize = 4;
/// Size of the slot hash in a storage key.
pub const STORAGE_SLOT_KEY_SIZE: usize = 32;
const STORAGE_POSTFIX_PORTION: usize = 16;
/// `BaseFlatPersistence.StorageKeyLength`.
pub const STORAGE_KEY_LENGTH: usize = STORAGE_PREFIX_PORTION + STORAGE_SLOT_KEY_SIZE + STORAGE_POSTFIX_PORTION;

/// Address hash bytes a storage node key holds (`BaseTriePersistence.StorageHashPrefixLength`).
pub const STORAGE_HASH_PREFIX_LENGTH: usize = 20;
pub const FULL_PATH_LENGTH: usize = 32;
pub const PATH_LENGTH_LENGTH: usize = 1;
/// Longest path kept in the shortened columns. Must be odd.
pub const SHORTENED_PATH_THRESHOLD: u8 = 15;
/// `ceil(SHORTENED_PATH_THRESHOLD / 2)`.
pub const SHORTENED_PATH_LENGTH: usize = 8;
/// Longest state path kept in the top column.
pub const STATE_NODES_TOP_THRESHOLD: u8 = 5;
pub const STATE_NODES_TOP_PATH_LENGTH: usize = 3;
pub const FULL_STATE_NODES_KEY_LENGTH: usize = 1 + FULL_PATH_LENGTH + PATH_LENGTH_LENGTH;
pub const SHORTENED_STORAGE_NODES_KEY_LENGTH: usize =
    STORAGE_PREFIX_PORTION + SHORTENED_PATH_LENGTH + (STORAGE_HASH_PREFIX_LENGTH - STORAGE_PREFIX_PORTION);
pub const FULL_STORAGE_NODES_KEY_LENGTH: usize = 1 + STORAGE_HASH_PREFIX_LENGTH + FULL_PATH_LENGTH + PATH_LENGTH_LENGTH;

/// `BaseFlatPersistence.EncodeAccountKeyHashed`.
pub fn account_key(address_hash: &Hash256) -> [u8; ACCOUNT_KEY_LENGTH] {
    address_hash[..ACCOUNT_KEY_LENGTH].try_into().expect("20 bytes")
}

/// `BaseFlatPersistence.EncodeStorageKey`.
pub fn storage_key(address_hash: &Hash256, slot_hash: &Hash256, full_address: bool) -> [u8; STORAGE_KEY_LENGTH] {
    let mut key = [0u8; STORAGE_KEY_LENGTH];
    if full_address {
        key[..ACCOUNT_KEY_LENGTH].copy_from_slice(&address_hash[..ACCOUNT_KEY_LENGTH]);
        key[ACCOUNT_KEY_LENGTH..].copy_from_slice(slot_hash);
    } else {
        let slot_end = STORAGE_PREFIX_PORTION + STORAGE_SLOT_KEY_SIZE;
        key[..STORAGE_PREFIX_PORTION].copy_from_slice(&address_hash[..STORAGE_PREFIX_PORTION]);
        key[STORAGE_PREFIX_PORTION..slot_end].copy_from_slice(slot_hash);
        key[slot_end..].copy_from_slice(&address_hash[STORAGE_PREFIX_PORTION..STORAGE_PREFIX_PORTION + 16]);
    }
    key
}

/// `BaseFlatPersistence.StoragePrefixLength`: the leading address bytes of a storage key, which is also where the
/// slot starts.
pub const fn storage_prefix_length(full_address: bool) -> usize {
    if full_address {
        ACCOUNT_KEY_LENGTH
    } else {
        STORAGE_PREFIX_PORTION
    }
}

/// `BaseFlatPersistence.StorageAddressSuffix`: the trailing address bytes a range scan re-verifies, as keys of other
/// addresses sharing the leading bytes fall inside the scanned range; empty when the whole address leads.
pub fn storage_address_suffix(address_hash: &Hash256, full_address: bool) -> &[u8] {
    if full_address {
        &[]
    } else {
        &address_hash[STORAGE_PREFIX_PORTION..STORAGE_PREFIX_PORTION + STORAGE_POSTFIX_PORTION]
    }
}

/// The column and key of a trie node.
#[derive(Clone, Copy)]
pub struct NodeKey {
    pub column: Column,
    bytes: [u8; FULL_STORAGE_NODES_KEY_LENGTH],
    len: u8,
}

impl NodeKey {
    pub fn as_slice(&self) -> &[u8] {
        &self.bytes[..usize::from(self.len)]
    }

    fn new(column: Column, len: usize, fill: impl FnOnce(&mut [u8])) -> Self {
        let mut bytes = [0u8; FULL_STORAGE_NODES_KEY_LENGTH];
        fill(&mut bytes[..len]);
        Self {
            column,
            bytes,
            len: len as u8,
        }
    }
}

/// `BaseTriePersistence.EncodeStateTopNodeKey`.
pub fn encode_state_top_node_key(buffer: &mut [u8], path: &TreePath) {
    path.encode_with_3_byte(buffer);
}

/// `BaseTriePersistence.EncodeShortenedStateNodeKey`.
pub fn encode_shortened_state_node_key(buffer: &mut [u8], path: &TreePath) {
    path.encode_with_8_byte(buffer);
}

/// `BaseTriePersistence.EncodeFullStateNodeKey`.
pub fn encode_full_state_node_key(buffer: &mut [u8], path: &TreePath) {
    buffer[0] = 0;
    buffer[1..=FULL_PATH_LENGTH].copy_from_slice(&path.path);
    buffer[1 + FULL_PATH_LENGTH] = path.length;
}

/// `BaseTriePersistence.EncodeShortenedStorageNodeKey`.
pub fn encode_shortened_storage_node_key(buffer: &mut [u8], address_hash: &Hash256, path: &TreePath) {
    buffer[..STORAGE_PREFIX_PORTION].copy_from_slice(&address_hash[..STORAGE_PREFIX_PORTION]);
    path.encode_with_8_byte(&mut buffer[STORAGE_PREFIX_PORTION..]);
    buffer[STORAGE_PREFIX_PORTION + SHORTENED_PATH_LENGTH..SHORTENED_STORAGE_NODES_KEY_LENGTH]
        .copy_from_slice(&address_hash[STORAGE_PREFIX_PORTION..STORAGE_HASH_PREFIX_LENGTH]);
}

/// `BaseTriePersistence.EncodeFullStorageNodeKey`.
pub fn encode_full_storage_node_key(buffer: &mut [u8], address_hash: &Hash256, path: &TreePath) {
    let path_start = 1 + STORAGE_PREFIX_PORTION;
    let length_at = path_start + FULL_PATH_LENGTH;
    buffer[0] = 1;
    buffer[1..path_start].copy_from_slice(&address_hash[..STORAGE_PREFIX_PORTION]);
    buffer[path_start..length_at].copy_from_slice(&path.path);
    buffer[length_at] = path.length;
    buffer[length_at + 1..FULL_STORAGE_NODES_KEY_LENGTH]
        .copy_from_slice(&address_hash[STORAGE_PREFIX_PORTION..STORAGE_HASH_PREFIX_LENGTH]);
}

/// The column and key a state trie node is stored under.
pub fn state_node_key(path: &TreePath) -> NodeKey {
    if path.length <= STATE_NODES_TOP_THRESHOLD {
        NodeKey::new(Column::StateTopNodes, STATE_NODES_TOP_PATH_LENGTH, |key| {
            encode_state_top_node_key(key, path)
        })
    } else if path.length <= SHORTENED_PATH_THRESHOLD {
        NodeKey::new(Column::StateNodes, SHORTENED_PATH_LENGTH, |key| {
            encode_shortened_state_node_key(key, path)
        })
    } else {
        NodeKey::new(Column::FallbackNodes, FULL_STATE_NODES_KEY_LENGTH, |key| {
            encode_full_state_node_key(key, path)
        })
    }
}

/// The column and key a storage trie node of the account hashing to `address_hash` is stored under.
pub fn storage_node_key(address_hash: &Hash256, path: &TreePath) -> NodeKey {
    if path.length <= SHORTENED_PATH_THRESHOLD {
        NodeKey::new(Column::StorageNodes, SHORTENED_STORAGE_NODES_KEY_LENGTH, |key| {
            encode_shortened_storage_node_key(key, address_hash, path)
        })
    } else {
        NodeKey::new(Column::FallbackNodes, FULL_STORAGE_NODES_KEY_LENGTH, |key| {
            encode_full_storage_node_key(key, address_hash, path)
        })
    }
}

#[cfg(test)]
mod tests {
    use super::{state_node_key, storage_key, storage_node_key};
    use crate::kv::Column;
    use crate::types::TreePath;
    use rstest::rstest;

    fn hex(text: &str) -> Vec<u8> {
        (0..text.len())
            .step_by(2)
            .map(|i| u8::from_str_radix(&text[i..i + 2], 16).unwrap())
            .collect()
    }

    fn hash(first: u8) -> [u8; 32] {
        std::array::from_fn(|i| first.wrapping_add(i as u8))
    }

    #[test]
    fn storage_keys_split_or_lead_with_the_address() {
        let (address, slot) = (hash(0x10), hash(0xA0));
        let split = storage_key(&address, &slot, false);
        assert_eq!(split[..4], address[..4]);
        assert_eq!(split[4..36], slot);
        assert_eq!(split[36..], address[4..20]);

        let full = storage_key(&address, &slot, true);
        assert_eq!(full[..20], address[..20]);
        assert_eq!(full[20..], slot);
    }

    #[rstest]
    #[case("12345", Column::StateTopNodes, "123455")]
    #[case("123456", Column::StateNodes, "1234560000000006")]
    #[case("123456789abcdef", Column::StateNodes, "123456789abcdeff")]
    #[case("123456789abcdef0", Column::FallbackNodes, &format!("00123456789abcdef0{}10", "00".repeat(24)))]
    fn state_nodes_go_to_the_column_of_their_length(#[case] path: &str, #[case] column: Column, #[case] key: &str) {
        let node = state_node_key(&TreePath::from_hex(path));
        assert_eq!(node.column, column);
        assert_eq!(node.as_slice(), hex(key));
    }

    #[rstest]
    #[case("abcd", Column::StorageNodes, 28)]
    #[case("123456789abcdef", Column::StorageNodes, 28)]
    #[case("abcdef1234567890", Column::FallbackNodes, 54)]
    fn storage_nodes_wrap_the_path_in_the_address(#[case] path: &str, #[case] column: Column, #[case] len: usize) {
        let address = hash(0x40);
        let path = TreePath::from_hex(path);
        let node = storage_node_key(&address, &path);
        let key = node.as_slice();
        assert_eq!((node.column, key.len()), (column, len));
        if column == Column::StorageNodes {
            assert_eq!(key[..4], address[..4]);
            assert_eq!(key[4..11], path.path[..7]);
            assert_eq!(key[11], (path.path[7] & 0xF0) | path.length);
            assert_eq!(key[12..], address[4..20]);
        } else {
            assert_eq!(key[0], 1);
            assert_eq!(key[1..5], address[..4]);
            assert_eq!(key[5..37], path.path);
            assert_eq!(key[37], path.length);
            assert_eq!(key[38..], address[4..20]);
        }
    }
}
