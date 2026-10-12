//! Storage slot values as the flat storage column holds them. Port of `BaseFlatPersistence.EncodeSlotValue` and
//! `DecodeSlotValue`: the stripped big-endian bytes, RLP-wrapped unless the DB uses the legacy raw encoding.

use super::rlp;
use crate::types::U256;

/// `BaseFlatPersistence.StorageValueSize`: the most bytes a decoded slot value has.
pub const STORAGE_VALUE_SIZE: usize = 32;

/// `BaseFlatPersistence.RlpSlotValueBufferSize`: the largest stored slot value, a 32-byte string with its prefix.
pub const RLP_SLOT_VALUE_BUFFER_SIZE: usize = STORAGE_VALUE_SIZE + 1;

/// Encodes `value` into `buffer` exactly as the storage column stores it and returns the number of bytes written.
/// Zero is one zero byte, in both encodings.
pub fn encode_slot_value(value: &U256, rlp_wrap: bool, buffer: &mut [u8; RLP_SLOT_VALUE_BUFFER_SIZE]) -> usize {
    let (bytes, start) = value.to_minimal_be();
    let stripped = &bytes[start..];
    if rlp_wrap {
        return rlp::encode_bytes(stripped, buffer);
    }

    buffer[..stripped.len()].copy_from_slice(stripped);
    stripped.len()
}

/// Reads a big-endian slot value of at most 32 bytes, or `None` for a longer one, where
/// `BaseFlatPersistence.DecodeSlotValue` throws.
pub fn decode_slot_value(data: &[u8]) -> Option<U256> {
    (data.len() <= STORAGE_VALUE_SIZE).then(|| U256::from_be_slice(data))
}

#[cfg(test)]
mod tests {
    use super::{RLP_SLOT_VALUE_BUFFER_SIZE, decode_slot_value, encode_slot_value};
    use crate::types::U256;
    use rstest::rstest;

    fn hex(text: &str) -> Vec<u8> {
        (0..text.len())
            .step_by(2)
            .map(|i| u8::from_str_radix(&text[i..i + 2], 16).unwrap())
            .collect()
    }

    #[rstest]
    #[case("00", true, "00")]
    #[case("00", false, "00")]
    #[case("05", true, "05")]
    #[case("80", true, "8180")]
    #[case("80", false, "80")]
    #[case("0102", true, "820102")]
    #[case("0102", false, "0102")]
    #[case(&"ab".repeat(32), true, &format!("a0{}", "ab".repeat(32)))]
    #[case(&"ab".repeat(32), false, &"ab".repeat(32))]
    fn slot_values_are_stripped_and_optionally_wrapped(
        #[case] stripped: &str,
        #[case] rlp_wrap: bool,
        #[case] stored: &str,
    ) {
        let value = decode_slot_value(&hex(stripped)).unwrap();
        let mut buffer = [0u8; RLP_SLOT_VALUE_BUFFER_SIZE];
        let written = encode_slot_value(&value, rlp_wrap, &mut buffer);
        assert_eq!(buffer[..written], hex(stored));
    }

    /// Port of `SlotRlpEncodingTests.Decode_slot_rejects_oversized_input` and `Decode_slot_pads_leading_zeros`.
    #[rstest]
    fn decoding_pads_leading_zeros_and_rejects_oversized_input(#[values(0, 1, 16, 31, 32, 33, 64)] length: usize) {
        let data: Vec<u8> = (1..=length as u8).collect();
        let value = decode_slot_value(&data);
        if length > 32 {
            assert_eq!(value, None);
            return;
        }

        let bytes = value.unwrap().to_be_bytes();
        assert!(bytes[..32 - length].iter().all(|&byte| byte == 0));
        assert_eq!(bytes[32 - length..], data[..]);
        assert_eq!(decode_slot_value(&[]), Some(U256::ZERO));
    }
}
