//! Port of `Nethermind.State.Flat/PersistedSnapshots/PersistedSnapshotKey.cs` and `PersistedSnapshotTags.cs`: the keys
//! of a persisted snapshot's table, their classification on read, and the value markers.
//!
//! Column and sub-column tags are stored as `255 − tag`, so plain ascending order is the columnar emission order:
//! ```text
//! Ref-id        : 00 + blobId(u16 BE)
//! Storage node  : FA + addrHash[0..20] + {FF top | FE compact | FD fallback} + path
//! State node    : {FD top | FC compact | FB fallback} + path
//! Account       : FE + address + FD
//! Self-destruct : FE + address + FE
//! Slot          : FE + address + FF + slot(32 BE)
//! Metadata      : FF + name(10, NUL-padded)
//! ```
//! A top path (length 0..=7) is the 4-byte `TreePath` form, a compact one (8..=15) the 8-byte form, a fallback one the
//! 32 path bytes and the length. Like C#, the forms keep any nibbles a path has past its length.

use super::TierResult;
use super::io::ByteReader;
use super::sorted::enumerator::SortedTableEnumerator;
use super::sorted::table::corrupt;
use crate::types::{Address, Hash256, TreePath};

pub const REF_ID_COLUMN: u8 = 0x00;
pub const REF_ID_KEY_LENGTH: usize = 3;
pub const METADATA_COLUMN: u8 = 0xFF;
pub const ACCOUNT_COLUMN: u8 = 0xFE;
pub const STATE_TOP_COLUMN: u8 = 0xFD;
pub const STATE_COMPACT_COLUMN: u8 = 0xFC;
pub const STATE_FALLBACK_COLUMN: u8 = 0xFB;
pub const STORAGE_COLUMN: u8 = 0xFA;

pub const ACCOUNT_SUB: u8 = 0xFD;
pub const SELF_DESTRUCT_SUB: u8 = 0xFE;
pub const SLOT_SUB: u8 = 0xFF;

pub const STORAGE_TOP_SUB: u8 = 0xFF;
pub const STORAGE_COMPACT_SUB: u8 = 0xFE;
pub const STORAGE_FALLBACK_SUB: u8 = 0xFD;

const TOP_PATH_THRESHOLD: u8 = 7;
const COMPACT_PATH_THRESHOLD: u8 = 15;

pub const ADDRESS_KEY_LENGTH: usize = 20;
/// `AddressHashPrefixLength`: the storage-node column keeps the first 20 bytes of the address hash.
pub const ADDRESS_HASH_PREFIX_LENGTH: usize = 20;
pub const SLOT_LENGTH: usize = 32;

/// `MaxKeyLength`: the storage fallback key, `1 + 20 + 1 + 33`.
pub const MAX_KEY_LENGTH: usize = 1 + ADDRESS_HASH_PREFIX_LENGTH + 1 + 33;

/// `MetadataKeyLength`: metadata names are NUL-padded to the longest one.
pub const METADATA_NAME_LENGTH: usize = 10;
pub const METADATA_BLOB_RANGE: &[u8; METADATA_NAME_LENGTH] = b"blob_range";
pub const METADATA_FROM_BLOCK: &[u8; METADATA_NAME_LENGTH] = b"from_block";
pub const METADATA_FROM_HASH: &[u8; METADATA_NAME_LENGTH] = b"from_hash\0";
pub const METADATA_NODE_REFS: &[u8; METADATA_NAME_LENGTH] = b"noderefs\0\0";
pub const METADATA_TO_BLOCK: &[u8; METADATA_NAME_LENGTH] = b"to_block\0\0";
pub const METADATA_TO_HASH: &[u8; METADATA_NAME_LENGTH] = b"to_hash\0\0\0";

/// Self-destruct value of a destructed account's storage.
pub const SELF_DESTRUCT_DESTRUCTED_MARKER: u8 = 0x00;
/// Self-destruct value of a newly created account.
pub const SELF_DESTRUCT_NEW_MARKER: u8 = 0x01;
/// Account value of a deleted account; slim account RLP starts at 0xC0, so it is unambiguous.
pub const ACCOUNT_DELETED_MARKER: u8 = 0x00;
/// The value of a ref-id record.
pub const REF_ID_VALUE: u8 = 0x01;
/// The value of the `noderefs` metadata record, whose presence marks a merged table.
pub const METADATA_NODE_REFS_PRESENT_MARKER: u8 = 0x01;

/// A materialized key, at most [`MAX_KEY_LENGTH`] bytes, kept inline so a snapshot's keys sort without allocating.
#[derive(Clone, Copy, PartialEq, Eq, Hash)]
pub struct PersistedKey {
    bytes: [u8; MAX_KEY_LENGTH],
    length: u8,
}

impl PersistedKey {
    fn from_parts(parts: &[&[u8]]) -> Self {
        let mut key = Self {
            bytes: [0; MAX_KEY_LENGTH],
            length: 0,
        };
        for part in parts {
            let start = usize::from(key.length);
            key.bytes[start..start + part.len()].copy_from_slice(part);
            key.length += part.len() as u8;
        }
        key
    }

    pub fn as_slice(&self) -> &[u8] {
        &self.bytes[..usize::from(self.length)]
    }
}

impl Ord for PersistedKey {
    fn cmp(&self, other: &Self) -> std::cmp::Ordering {
        self.as_slice().cmp(other.as_slice())
    }
}

impl PartialOrd for PersistedKey {
    fn partial_cmp(&self, other: &Self) -> Option<std::cmp::Ordering> {
        Some(self.cmp(other))
    }
}

impl std::fmt::Debug for PersistedKey {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "PersistedKey(0x")?;
        for byte in self.as_slice() {
            write!(f, "{byte:02x}")?;
        }
        write!(f, ")")
    }
}

/// `WriteMetadataKey`.
pub fn metadata_key(name: &[u8; METADATA_NAME_LENGTH]) -> PersistedKey {
    PersistedKey::from_parts(&[&[METADATA_COLUMN], name])
}

/// `WriteRefIdKey`: big-endian, so the ids sort numerically.
pub fn ref_id_key(blob_arena_id: u16) -> PersistedKey {
    PersistedKey::from_parts(&[&[REF_ID_COLUMN], &blob_arena_id.to_be_bytes()])
}

/// `ReadRefId`.
pub fn read_ref_id(key: &[u8]) -> u16 {
    u16::from_be_bytes([key[1], key[2]])
}

pub fn account_key(address: &Address) -> PersistedKey {
    PersistedKey::from_parts(&[&[ACCOUNT_COLUMN], address, &[ACCOUNT_SUB]])
}

pub fn self_destruct_key(address: &Address) -> PersistedKey {
    PersistedKey::from_parts(&[&[ACCOUNT_COLUMN], address, &[SELF_DESTRUCT_SUB]])
}

/// `WriteSlotKey`, with the slot big-endian.
pub fn slot_key(address: &Address, slot_be: &[u8; SLOT_LENGTH]) -> PersistedKey {
    PersistedKey::from_parts(&[&[ACCOUNT_COLUMN], address, &[SLOT_SUB], slot_be])
}

/// `WriteStateNodeKey`.
pub fn state_node_key(path: &TreePath) -> PersistedKey {
    let (form, encoded) = encode_path(path);
    let column = [STATE_TOP_COLUMN, STATE_COMPACT_COLUMN, STATE_FALLBACK_COLUMN][form];
    PersistedKey::from_parts(&[&[column], encoded.as_slice()])
}

/// `WriteStorageNodeKey`: only the first 20 bytes of the address hash are kept.
pub fn storage_node_key(address_hash: &Hash256, path: &TreePath) -> PersistedKey {
    let (form, encoded) = encode_path(path);
    let sub = [STORAGE_TOP_SUB, STORAGE_COMPACT_SUB, STORAGE_FALLBACK_SUB][form];
    PersistedKey::from_parts(&[
        &[STORAGE_COLUMN],
        &address_hash[..ADDRESS_HASH_PREFIX_LENGTH],
        &[sub],
        encoded.as_slice(),
    ])
}

/// A path in the form its length selects: 0 top (4 bytes), 1 compact (8 bytes), 2 fallback (33 bytes).
pub struct EncodedPath {
    bytes: [u8; 33],
    length: usize,
}

impl EncodedPath {
    pub fn as_slice(&self) -> &[u8] {
        &self.bytes[..self.length]
    }
}

pub fn encode_path(path: &TreePath) -> (usize, EncodedPath) {
    let mut bytes = [0u8; 33];
    if path.length <= TOP_PATH_THRESHOLD {
        path.encode_with_4_byte(&mut bytes);
        (0, EncodedPath { bytes, length: 4 })
    } else if path.length <= COMPACT_PATH_THRESHOLD {
        path.encode_with_8_byte(&mut bytes);
        (1, EncodedPath { bytes, length: 8 })
    } else {
        bytes[..32].copy_from_slice(&path.path);
        bytes[32] = path.length;
        (2, EncodedPath { bytes, length: 33 })
    }
}

/// `PerAddressAddress`.
pub fn per_address_address(key: &[u8]) -> &[u8] {
    &key[1..1 + ADDRESS_KEY_LENGTH]
}

/// `PerAddressSubColumn`.
pub fn per_address_sub_column(key: &[u8]) -> u8 {
    key[1 + ADDRESS_KEY_LENGTH]
}

/// `SlotKeyBytes`.
pub fn slot_key_bytes(key: &[u8]) -> &[u8] {
    &key[2 + ADDRESS_KEY_LENGTH..2 + ADDRESS_KEY_LENGTH + SLOT_LENGTH]
}

/// `StorageAddressHash`.
pub fn storage_address_hash(key: &[u8]) -> &[u8] {
    &key[1..1 + ADDRESS_HASH_PREFIX_LENGTH]
}

/// `StorageSubColumn`.
pub fn storage_sub_column(key: &[u8]) -> u8 {
    key[1 + ADDRESS_HASH_PREFIX_LENGTH]
}

/// `StoragePathBytes`.
pub fn storage_path_bytes(key: &[u8]) -> &[u8] {
    &key[2 + ADDRESS_HASH_PREFIX_LENGTH..]
}

/// `StatePathBytes`.
pub fn state_path_bytes(key: &[u8]) -> &[u8] {
    &key[1..]
}

/// `DecodePath`: the path of an encoded key part, in the form `stage` names (0 top, 1 compact, else fallback). The part
/// of a key [`check_shape`] accepted always decodes.
pub fn decode_path(encoded: &[u8], stage: usize) -> TreePath {
    match stage {
        0 => TreePath::decode_with_4_byte(encoded),
        1 => TreePath::decode_with_8_byte(encoded),
        _ => TreePath::new(encoded[..32].try_into().expect("32 bytes"), encoded[32]),
    }
}

/// The form of a state-node column (`StateStage` of the scanner).
pub fn state_stage(column: u8) -> usize {
    match column {
        STATE_TOP_COLUMN => 0,
        STATE_COMPACT_COLUMN => 1,
        _ => 2,
    }
}

/// The form of a storage-node sub-column (`StorageStage` of the scanner).
pub fn storage_stage(sub_column: u8) -> usize {
    match sub_column {
        STORAGE_TOP_SUB => 0,
        STORAGE_COMPACT_SUB => 1,
        _ => 2,
    }
}

/// Checks that `key` has the exact shape the writers give its column, so the helpers above can slice it. A key no
/// writer produced is corrupt, where C# throws on slicing it or on its fallback path length.
pub fn check_shape(key: &[u8]) -> TierResult<()> {
    let well_formed = match key.first() {
        Some(&REF_ID_COLUMN) => key.len() == REF_ID_KEY_LENGTH,
        Some(&ACCOUNT_COLUMN) => match key.get(1 + ADDRESS_KEY_LENGTH) {
            Some(&(ACCOUNT_SUB | SELF_DESTRUCT_SUB)) => key.len() == 2 + ADDRESS_KEY_LENGTH,
            Some(&SLOT_SUB) => key.len() == 2 + ADDRESS_KEY_LENGTH + SLOT_LENGTH,
            _ => false,
        },
        Some(&STORAGE_COLUMN) => key.get(1 + ADDRESS_HASH_PREFIX_LENGTH).is_some_and(|&sub| {
            (STORAGE_FALLBACK_SUB..=STORAGE_TOP_SUB).contains(&sub)
                && is_encoded_path(storage_path_bytes(key), storage_stage(sub))
        }),
        Some(&column @ STATE_FALLBACK_COLUMN..=STATE_TOP_COLUMN) => {
            is_encoded_path(state_path_bytes(key), state_stage(column))
        }
        Some(&METADATA_COLUMN) => true,
        _ => false,
    };
    if well_formed {
        return Ok(());
    }
    let hex = key.iter().map(|byte| format!("{byte:02x}")).collect::<String>();
    Err(corrupt(format_args!(
        "the {}-byte key 0x{hex} does not have the shape of its column",
        key.len()
    )))
}

fn is_encoded_path(encoded: &[u8], stage: usize) -> bool {
    match stage {
        0 => encoded.len() == 4,
        1 => encoded.len() == 8,
        _ => encoded.len() == 33 && encoded[32] <= TreePath::MAX_LENGTH,
    }
}

/// [`SortedTableEnumerator::move_next`] that [`check_shape`]s the key it moves onto.
pub fn move_next_checked(enumerator: &mut SortedTableEnumerator, reader: &ByteReader<'_>) -> TierResult<bool> {
    let moved = enumerator.move_next(reader)?;
    if moved {
        check_shape(enumerator.current_key())?;
    }
    Ok(moved)
}

#[cfg(test)]
mod tests {
    use super::{
        MAX_KEY_LENGTH, account_key, decode_path, metadata_key, read_ref_id, ref_id_key, self_destruct_key, slot_key,
        state_node_key, state_path_bytes, state_stage, storage_node_key, storage_path_bytes, storage_stage,
        storage_sub_column,
    };
    use crate::types::TreePath;
    use rstest::rstest;

    fn hex(text: &str) -> Vec<u8> {
        (0..text.len())
            .step_by(2)
            .map(|i| u8::from_str_radix(&text[i..i + 2], 16).unwrap())
            .collect()
    }

    #[rstest]
    #[case("1234567", "fd12345677")]
    #[case("123456789abcdef", "fc123456789abcdeff")]
    #[case(
        "123456789abcdef0",
        "fb123456789abcdef000000000000000000000000000000000000000000000000010"
    )]
    fn state_node_keys_pick_the_form_by_length_and_decode_back(#[case] path: &str, #[case] key: &str) {
        let path = TreePath::from_hex(path);
        let encoded = state_node_key(&path);
        assert_eq!(encoded.as_slice(), hex(key));
        let bytes = encoded.as_slice();
        assert_eq!(decode_path(state_path_bytes(bytes), state_stage(bytes[0])), path);

        let storage = storage_node_key(&[0xAB; 32], &path);
        let storage = storage.as_slice();
        assert_eq!(&storage[..21], &hex(&format!("fa{}", "ab".repeat(20)))[..]);
        assert_eq!(storage_path_bytes(storage), &bytes[1..]);
        assert_eq!(
            decode_path(storage_path_bytes(storage), storage_stage(storage_sub_column(storage))),
            path
        );
    }

    #[test]
    fn per_address_and_metadata_keys_sort_in_emission_order() {
        let address = [0x11; 20];
        let mut slot = [0u8; 32];
        slot[31] = 1;
        let keys = [
            ref_id_key(0x0102),
            storage_node_key(&[0; 32], &TreePath::from_hex("1")),
            state_node_key(&TreePath::from_hex("123456789abcdef01")),
            state_node_key(&TreePath::from_hex("123456789")),
            state_node_key(&TreePath::from_hex("1")),
            account_key(&address),
            self_destruct_key(&address),
            slot_key(&address, &slot),
            metadata_key(b"blob_range"),
        ];

        assert!(keys.windows(2).all(|pair| pair[0] < pair[1]), "{keys:?}");
        assert_eq!(keys[0].as_slice(), hex("000102"));
        assert_eq!(read_ref_id(keys[0].as_slice()), 0x0102);
        assert_eq!(keys[8].as_slice(), b"\xffblob_range");
        assert_eq!(
            storage_node_key(&[0; 32], &TreePath::new([0; 32], 64)).as_slice().len(),
            MAX_KEY_LENGTH
        );
    }
}
