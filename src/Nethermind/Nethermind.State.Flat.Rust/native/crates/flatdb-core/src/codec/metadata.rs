//! Keys and values of the metadata column. Port of the metadata half of `BasePersistence.cs`.

use std::sync::LazyLock;

use crate::keccak::keccak256;
use crate::types::{EMPTY_TREE_HASH, Hash256, StateId};

/// `Keccak.Compute("CurrentState")`: the persisted state pointer.
pub static CURRENT_STATE_KEY: LazyLock<Hash256> = LazyLock::new(|| keccak256(b"CurrentState"));
/// `Keccak.Compute("Layout")`: the `FlatLayout` byte.
pub static LAYOUT_KEY: LazyLock<Hash256> = LazyLock::new(|| keccak256(b"Layout"));
/// `Keccak.Compute("SlotEncoding")`: the slot encoding version byte.
pub static SLOT_ENCODING_KEY: LazyLock<Hash256> = LazyLock::new(|| keccak256(b"SlotEncoding"));
/// `Keccak.Compute("WipedForSync")`: set while a wipe for a state sync has not been followed by a persisted state.
pub static WIPED_FOR_SYNC_KEY: LazyLock<Hash256> = LazyLock::new(|| keccak256(b"WipedForSync"));

/// `BasePersistence.SlotEncodingRaw`: the stripped value bytes stored verbatim. Legacy, deprecated.
pub const SLOT_ENCODING_RAW: u8 = 0;
/// `BasePersistence.SlotEncodingRlp`: the stripped value stored as an RLP byte string.
pub const SLOT_ENCODING_RLP: u8 = 1;

/// The stored form of a state pointer: the big-endian block number, then the root.
pub fn encode_state_id(state: &StateId) -> [u8; 40] {
    let mut bytes = [0u8; 40];
    bytes[..8].copy_from_slice(&state.block_number.to_be_bytes());
    bytes[8..].copy_from_slice(&state.state_root);
    bytes
}

/// The state a stored pointer names; a missing or empty one is the pre-genesis state. `None` for a pointer too
/// short to hold a state, which C# reads past.
pub fn decode_state_id(bytes: Option<&[u8]>) -> Option<StateId> {
    match bytes {
        None | Some([]) => Some(StateId::new(u64::MAX, EMPTY_TREE_HASH)),
        Some(bytes) if bytes.len() >= 40 => Some(StateId::new(
            u64::from_be_bytes(bytes[..8].try_into().expect("8 bytes")),
            bytes[8..40].try_into().expect("32 bytes"),
        )),
        Some(_) => None,
    }
}

#[cfg(test)]
mod tests {
    use super::{decode_state_id, encode_state_id};
    use crate::types::StateId;

    #[test]
    fn state_pointers_round_trip_and_default_to_pre_genesis() {
        let state = StateId::new(0x0102, [0x11; 32]);
        let bytes = encode_state_id(&state);
        assert_eq!(bytes[..8], [0, 0, 0, 0, 0, 0, 1, 2]);
        assert_eq!(decode_state_id(Some(&bytes)), Some(state));
        assert_eq!(decode_state_id(None), Some(StateId::PRE_GENESIS));
        assert_eq!(decode_state_id(Some(&[])), Some(StateId::PRE_GENESIS));
        assert_eq!(decode_state_id(Some(&[1, 2, 3])), None);
    }
}
