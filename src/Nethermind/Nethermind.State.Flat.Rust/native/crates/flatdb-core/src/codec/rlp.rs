//! The RLP forms the flat values use, with the canonical-form checks of `Nethermind.Serialization.Rlp`
//! (`Rlp.Encode`, `RlpHelpers.Cursor.cs`).

use thiserror::Error;

use crate::types::{Hash256, U256};

/// Malformed RLP, where C# throws an `RlpException` (or an index exception the decoders turn into one).
#[derive(Debug, Error, Clone, PartialEq, Eq)]
#[error("RLP: {0}")]
pub struct RlpError(pub String);

pub type RlpResult<T> = Result<T, RlpError>;

const SHORT_STRING_OFFSET: u8 = 0x80;
const SHORT_STRING_MAX_PREFIX: u8 = 0xB7;
const LIST_OFFSET: u8 = 0xC0;
const SHORT_LIST_MAX_PREFIX: u8 = 0xF7;
const SMALL_PREFIX_BARRIER: usize = 56;
/// The prefix of a 32-byte string, such as a hash.
pub const KECCAK_PREFIX: u8 = 0xA0;
/// `Rlp.EmptyByteArrayByte`.
pub const EMPTY_BYTE_ARRAY: u8 = 0x80;

/// Encodes a byte string into `output` and returns its length, as `Rlp.Encode(ReadOnlySpan<byte>, Span<byte>)`.
///
/// # Panics
/// When `output` is shorter than [`length_of_bytes`].
pub fn encode_bytes(value: &[u8], output: &mut [u8]) -> usize {
    match value.len() {
        0 => {
            output[0] = EMPTY_BYTE_ARRAY;
            1
        }
        1 if value[0] < SHORT_STRING_OFFSET => {
            output[0] = value[0];
            1
        }
        len if len < SMALL_PREFIX_BARRIER => {
            output[0] = SHORT_STRING_OFFSET + len as u8;
            output[1..=len].copy_from_slice(value);
            1 + len
        }
        len => {
            let header = long_length(len);
            output[0] = SHORT_STRING_MAX_PREFIX + header.len() as u8;
            output[1..=header.len()].copy_from_slice(&header);
            output[1 + header.len()..1 + header.len() + len].copy_from_slice(value);
            1 + header.len() + len
        }
    }
}

/// Length of the encoding of a byte string.
pub fn length_of_bytes(value: &[u8]) -> usize {
    match value.len() {
        1 if value[0] < SHORT_STRING_OFFSET => 1,
        len if len < SMALL_PREFIX_BARRIER => 1 + len,
        len => 1 + long_length(len).len() + len,
    }
}

/// The minimal big-endian length field of a long string or list.
fn long_length(len: usize) -> Vec<u8> {
    let bytes = (len as u64).to_be_bytes();
    let start = bytes.iter().position(|&byte| byte != 0).unwrap_or(7);
    bytes[start..].to_vec()
}

/// Appends an integer, as `IRlpWriteBackend.Encode(ulong)`.
pub fn push_u64(value: u64, output: &mut Vec<u8>) {
    push_u256(&U256::from_u64(value), output);
}

/// Appends an integer, as `IRlpWriteBackend.Encode(UInt256)`: the big-endian bytes without leading zeros as a string.
pub fn push_u256(value: &U256, output: &mut Vec<u8>) {
    let (bytes, start) = value.to_be_trimmed();
    push_bytes(&bytes[start..], output);
}

/// Appends a byte string.
pub fn push_bytes(value: &[u8], output: &mut Vec<u8>) {
    let start = output.len();
    output.resize(start + length_of_bytes(value), 0);
    encode_bytes(value, &mut output[start..]);
}

/// Appends a sequence header for `content_length` bytes of items.
pub fn push_sequence_header(content_length: usize, output: &mut Vec<u8>) {
    if content_length < SMALL_PREFIX_BARRIER {
        output.push(LIST_OFFSET + content_length as u8);
    } else {
        let header = long_length(content_length);
        output.push(SHORT_LIST_MAX_PREFIX + header.len() as u8);
        output.extend_from_slice(&header);
    }
}

/// Length of the encoding of an integer.
pub fn length_of_u256(value: &U256) -> usize {
    let (bytes, start) = value.to_be_trimmed();
    length_of_bytes(&bytes[start..])
}

fn byte_at(data: &[u8], position: usize) -> RlpResult<u8> {
    data.get(position)
        .copied()
        .ok_or_else(|| RlpError(format!("unexpected end of data at {position}")))
}

fn slice_at(data: &[u8], position: usize, len: usize) -> RlpResult<&[u8]> {
    position
        .checked_add(len)
        .and_then(|end| data.get(position..end))
        .ok_or_else(|| {
            RlpError(format!(
                "item of {len} bytes at {position} overruns the {} bytes of data",
                data.len()
            ))
        })
}

/// Reads a multi-byte length field, as `RlpHelpers.DeserializeLength`.
fn deserialize_length(data: &[u8], position: usize, length_of_length: usize) -> RlpResult<usize> {
    if length_of_length == 0 || length_of_length > 4 {
        return Err(RlpError(format!("invalid length of length {length_of_length}")));
    }

    Ok(slice_at(data, position, length_of_length)?
        .iter()
        .fold(0usize, |length, &byte| (length << 8) | usize::from(byte)))
}

/// Decodes a byte string, returning it and the position past it. Port of `RlpHelpers.DecodeByteArraySpan`. The string
/// is always a subslice of `data`, even when empty.
pub fn decode_bytes(data: &[u8], position: usize) -> RlpResult<(&[u8], usize)> {
    let start = position;
    let prefix = byte_at(data, position)?;
    let position = position + 1;
    if prefix < SHORT_STRING_OFFSET {
        return Ok((&data[start..position], position));
    }

    if prefix == EMPTY_BYTE_ARRAY {
        return Ok((&data[position..position], position));
    }

    if prefix <= SHORT_STRING_MAX_PREFIX {
        let length = usize::from(prefix - SHORT_STRING_OFFSET);
        let value = slice_at(data, position, length)?;
        if length == 1 && value[0] < SHORT_STRING_OFFSET {
            return Err(RlpError(format!("non-canonical single byte at {start}")));
        }
        return Ok((value, position + length));
    }

    if prefix < LIST_OFFSET {
        let length_of_length = usize::from(prefix - SHORT_STRING_MAX_PREFIX);
        let length = deserialize_length(data, position, length_of_length)?;
        if length < SMALL_PREFIX_BARRIER {
            return Err(RlpError(format!("expected a length of at least 56, got {length}")));
        }
        let position = position + length_of_length;
        return Ok((slice_at(data, position, length)?, position + length));
    }

    Err(RlpError(format!(
        "unexpected prefix {prefix:#04x} at {start} for a byte string"
    )))
}

/// Reads a sequence header, returning its content length and the position of its first item. Port of
/// `RlpHelpers.ReadSequenceLength`.
pub fn read_sequence_length(data: &[u8], position: usize) -> RlpResult<(usize, usize)> {
    let prefix = byte_at(data, position)?;
    let position = position + 1;
    if prefix < LIST_OFFSET {
        return Err(RlpError(format!("unexpected prefix {prefix:#04x} for a sequence")));
    }

    if prefix <= SHORT_LIST_MAX_PREFIX {
        return Ok((usize::from(prefix - LIST_OFFSET), position));
    }

    let length_of_length = usize::from(prefix - SHORT_LIST_MAX_PREFIX);
    let length = deserialize_length(data, position, length_of_length)?;
    if length < SMALL_PREFIX_BARRIER {
        return Err(RlpError(format!("expected a length of at least 56, got {length}")));
    }
    Ok((length, position + length_of_length))
}

/// Decodes an integer of up to eight bytes. Port of `RlpHelpers.DecodeULong`.
pub fn decode_u64(data: &[u8], position: usize) -> RlpResult<(u64, usize)> {
    let start = position;
    let prefix = byte_at(data, position)?;
    let position = position + 1;
    match prefix {
        0 => return Err(RlpError(format!("non-canonical integer at {start}"))),
        1..0x80 => return Ok((u64::from(prefix), position)),
        0x80 => return Ok((0, position)),
        _ => {}
    }

    let length = usize::from(prefix - 0x80);
    if length > 8 {
        return Err(RlpError(format!("integer of {length} bytes at {start}")));
    }
    let payload = slice_at(data, position, length)?;
    if payload[0] == 0 {
        return Err(RlpError(format!("non-canonical integer at {start}")));
    }
    let value = payload.iter().fold(0u64, |value, &byte| (value << 8) | u64::from(byte));
    if value < 0x80 {
        return Err(RlpError(format!("non-canonical integer at {start}")));
    }
    Ok((value, position + length))
}

/// Decodes an integer of up to 32 bytes. Port of `RlpHelpers.DecodeUInt256`.
pub fn decode_u256(data: &[u8], position: usize) -> RlpResult<(U256, usize)> {
    let start = position;
    if byte_at(data, position)? == 0 {
        return Err(RlpError(format!("non-canonical integer at {start}")));
    }

    let (bytes, position) = decode_bytes(data, position)?;
    if bytes.len() > 32 {
        return Err(RlpError(format!("integer of {} bytes at {start}", bytes.len())));
    }
    if bytes.len() > 1 && bytes[0] == 0 {
        return Err(RlpError(format!("non-canonical integer at {start}")));
    }
    Ok((U256::from_be_slice(bytes), position))
}

/// Decodes a 32-byte hash that must be present. Port of `RlpHelpers.DecodeValueKeccakNonNull`.
pub fn decode_hash(data: &[u8], position: usize) -> RlpResult<(Hash256, usize)> {
    let prefix = byte_at(data, position)?;
    if prefix != KECCAK_PREFIX {
        return Err(RlpError(if prefix == EMPTY_BYTE_ARRAY {
            "unexpected null hash".to_owned()
        } else {
            format!("unexpected prefix {prefix:#04x} for a hash")
        }));
    }

    let hash = slice_at(data, position + 1, 32)?;
    Ok((hash.try_into().expect("32 bytes"), position + 33))
}

#[cfg(test)]
mod tests {
    use super::{decode_bytes, decode_u64, decode_u256, encode_bytes, length_of_bytes, read_sequence_length};
    use crate::types::U256;
    use rstest::rstest;

    fn hex(text: &str) -> Vec<u8> {
        (0..text.len())
            .step_by(2)
            .map(|i| u8::from_str_radix(&text[i..i + 2], 16).unwrap())
            .collect()
    }

    #[rstest]
    #[case("", "80")]
    #[case("00", "00")]
    #[case("7f", "7f")]
    #[case("80", "8180")]
    #[case("0102", "820102")]
    #[case(&"ab".repeat(55), &format!("b7{}", "ab".repeat(55)))]
    #[case(&"ab".repeat(56), &format!("b838{}", "ab".repeat(56)))]
    fn byte_strings_round_trip(#[case] value: &str, #[case] encoded: &str) {
        let (value, encoded) = (hex(value), hex(encoded));
        let mut output = vec![0u8; length_of_bytes(&value)];
        assert_eq!(encode_bytes(&value, &mut output), encoded.len());
        assert_eq!(output, encoded);
        assert_eq!(decode_bytes(&encoded, 0).unwrap(), (&value[..], encoded.len()));
    }

    #[rstest]
    #[case::non_canonical_single_byte("8105")]
    #[case::short_long_string("b801ab")]
    #[case::overrun("82ab")]
    #[case::list("c0")]
    #[case::empty("")]
    fn malformed_byte_strings_are_rejected(#[case] encoded: &str) {
        assert!(decode_bytes(&hex(encoded), 0).is_err());
    }

    #[rstest]
    #[case("80", Some(0))]
    #[case("7f", Some(0x7f))]
    #[case("8180", Some(0x80))]
    #[case("88ffffffffffffffff", Some(u64::MAX))]
    #[case("00", None)]
    #[case("817f", None)]
    #[case("820001", None)]
    #[case("89010000000000000000", None)]
    fn u64_decoding_is_canonical(#[case] encoded: &str, #[case] expected: Option<u64>) {
        assert_eq!(decode_u64(&hex(encoded), 0).ok().map(|(value, _)| value), expected);
    }

    #[rstest]
    #[case("80", Some(U256::ZERO))]
    #[case("820102", Some(U256::from_u64(0x0102)))]
    #[case("00", None)]
    #[case("820001", None)]
    fn u256_decoding_is_canonical(#[case] encoded: &str, #[case] expected: Option<U256>) {
        assert_eq!(decode_u256(&hex(encoded), 0).ok().map(|(value, _)| value), expected);
    }

    #[rstest]
    #[case("c0", Some((0, 1)))]
    #[case("c3", Some((3, 1)))]
    #[case("f838", Some((56, 2)))]
    #[case("f801", None)]
    #[case("80", None)]
    fn sequence_headers(#[case] encoded: &str, #[case] expected: Option<(usize, usize)>) {
        assert_eq!(read_sequence_length(&hex(encoded), 0).ok(), expected);
    }
}
