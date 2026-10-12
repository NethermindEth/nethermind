//! Account RLP. Port of `Nethermind.Serialization.Rlp/AccountDecoder.cs`: the slim form (`AccountDecoder.Slim`, the one
//! the flat columns store) writes an empty storage root and an empty code hash as an empty byte string.

use super::rlp::{
    self, EMPTY_BYTE_ARRAY, KECCAK_PREFIX, RlpResult, decode_hash, decode_u64, decode_u256, read_sequence_length,
};
use crate::types::{Account, EMPTY_TREE_HASH, Hash256, KECCAK_OF_EMPTY_STRING};

/// Length of a hash item: its prefix and 32 bytes (`Rlp.LengthOfKeccakRlp`).
const LENGTH_OF_HASH: usize = 33;

/// Encodes an account, in the slim form when `slim`. Port of `AccountDecoder.Encode`.
pub fn encode_account(account: &Account, slim: bool) -> Vec<u8> {
    let slim_storage = slim && !account.has_storage();
    let slim_code = slim && !account.has_code();
    let content_length = rlp::length_of_u256(&account.nonce.into())
        + rlp::length_of_u256(&account.balance)
        + if slim_storage { 1 } else { LENGTH_OF_HASH }
        + if slim_code { 1 } else { LENGTH_OF_HASH };

    let mut output = Vec::with_capacity(content_length + 2);
    rlp::push_sequence_header(content_length, &mut output);
    rlp::push_u64(account.nonce, &mut output);
    rlp::push_u256(&account.balance, &mut output);
    push_hash(&account.storage_root, slim_storage, &mut output);
    push_hash(&account.code_hash, slim_code, &mut output);
    output
}

fn push_hash(hash: &Hash256, empty: bool, output: &mut Vec<u8>) {
    if empty {
        output.push(EMPTY_BYTE_ARRAY);
    } else {
        output.push(KECCAK_PREFIX);
        output.extend_from_slice(hash);
    }
}

/// Decodes an account, accepting the slim empty hashes when `slim`. Port of `AccountDecoder.DecodeInternal`: a
/// sequence with a single content byte is the encoding of no account.
pub fn decode_account(data: &[u8], slim: bool) -> RlpResult<Option<Account>> {
    let (length, position) = read_sequence_length(data, 0)?;
    if length == 1 {
        return Ok(None);
    }

    let (nonce, position) = decode_u64(data, position)?;
    let (balance, position) = decode_u256(data, position)?;
    let (storage_root, position) = decode_slim_hash(data, position, slim, EMPTY_TREE_HASH)?;
    let (code_hash, _) = decode_slim_hash(data, position, slim, KECCAK_OF_EMPTY_STRING)?;
    Ok(Some(Account::with_hashes(nonce, balance, storage_root, code_hash)))
}

/// Port of `AccountDecoder.DecodeHash`.
fn decode_slim_hash(data: &[u8], position: usize, slim: bool, slim_empty: Hash256) -> RlpResult<(Hash256, usize)> {
    if slim && data.get(position) == Some(&EMPTY_BYTE_ARRAY) {
        return Ok((slim_empty, position + 1));
    }

    decode_hash(data, position)
}

#[cfg(test)]
mod tests {
    use super::{decode_account, encode_account};
    use crate::types::{Account, U256};
    use rstest::rstest;

    fn hex(text: &str) -> Vec<u8> {
        (0..text.len())
            .step_by(2)
            .map(|i| u8::from_str_radix(&text[i..i + 2], 16).unwrap())
            .collect()
    }

    #[rstest]
    #[case(Account::new(0, U256::ZERO), true, "c4808080 80")]
    #[case(Account::new(1, U256::from_u64(1)), true, "c4010180 80")]
    #[case(Account::new(0x80, U256::from_u64(0x0102)), true, "c78180820102 8080")]
    #[case(
        Account::new(0, U256::ZERO),
        false,
        "f8448080a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421\
         a0c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470"
    )]
    #[case(
        Account::with_hashes(2, U256::from_u64(3), [0x11; 32], [0x22; 32]),
        true,
        "f8440203a01111111111111111111111111111111111111111111111111111111111111111\
         a02222222222222222222222222222222222222222222222222222222222222222"
    )]
    fn accounts_round_trip(#[case] account: Account, #[case] slim: bool, #[case] encoded: &str) {
        let encoded = hex(&encoded.replace(' ', ""));
        assert_eq!(encode_account(&account, slim), encoded);
        assert_eq!(decode_account(&encoded, slim).unwrap(), Some(account));
    }

    #[test]
    fn single_byte_sequence_is_no_account_and_garbage_fails() {
        assert_eq!(decode_account(&hex("c180"), true).unwrap(), None);
        assert!(decode_account(&hex("c40101"), true).is_err());
        assert!(decode_account(&hex("80"), true).is_err());
    }
}
