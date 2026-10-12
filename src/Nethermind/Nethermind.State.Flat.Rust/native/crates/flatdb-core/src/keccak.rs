//! Keccak-256, the hash `ValueKeccak.Compute` and `KeccakCache.Compute` produce.

use sha3::{Digest, Keccak256};

use crate::types::{Address, Hash256, U256};

pub fn keccak256(data: &[u8]) -> Hash256 {
    Keccak256::digest(data).into()
}

/// `Address.ToAccountPath`: the hash keying an account in the hashed layouts.
pub fn account_path(address: &Address) -> Hash256 {
    keccak256(address)
}

/// `StorageTree.ComputeKeyWithLookup`: the hash of the big-endian slot index, keying a slot in the hashed layouts.
pub fn slot_path(slot: &U256) -> Hash256 {
    keccak256(&slot.to_be_bytes())
}
