//! Keys real only over a short range of leaf indices, every node over no leaf in it
//! a pseudorandom filler. In that range they sign like a full key, and verify with
//! [`crate::lean_xmss::verify`], for a fraction of its key generation. For tests and
//! benchmarks: signing outside the range panics.
//!
//! A seed must back ONE key: never a mock key and the full key, nor two mock keys
//! over different ranges. They share every one-time key but not their bottom roots,
//! so the same top one-time key would sign two roots, and its signatures would
//! forge others.

use std::ops::RangeInclusive;

use crate::lean_xmss::*;

/// A key whose trees hold only the leaf indices `leaves`.
#[derive(Debug)]
pub struct MockSecretKey {
    key: LeanXmssSecretKey,
    leaves: RangeInclusive<LeafIndex>,
}

pub fn key_gen_from_seed(seed: Seed, first: LeafIndex, last: LeafIndex) -> (MockSecretKey, LeanXmssPublicKey) {
    assert!(first <= last, "an empty range");
    let (key, pk) = generate(seed, first..=last);
    (
        MockSecretKey {
            key,
            leaves: first..=last,
        },
        pk,
    )
}

/// [`crate::lean_xmss::sign`], in the key's range.
pub fn sign(
    sk: &MockSecretKey,
    message: &Message,
    leaf_index: LeafIndex,
) -> Result<LeanXmssSignature, LeanXmssSignError> {
    assert!(sk.leaves.contains(&leaf_index), "a mock key signs only in its range");
    sk.key.sign_over(message, leaf_index, sk.leaves.clone())
}
