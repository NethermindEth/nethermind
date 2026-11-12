use rand::{Rng, SeedableRng, rngs::StdRng};
use signatures::lean_xmss::*;

fn test_message() -> Message {
    std::array::from_fn(|i| (i * 3 + 7) as u8)
}

/// A full key: the whole top tree, and signatures in two bottom trees, each with
/// its own top-layer signature.
#[test]
fn full_key_signs_and_verifies() {
    let (sk, pk) = key_gen_from_seed([1; SEED_LEN]);
    let message = test_message();
    let first = sign(&sk, &message, 5).unwrap();
    verify(&pk, &message, &first).unwrap();
    let other_tree = sign(&sk, &message, (7 << LAYER_HEIGHT) + 3).unwrap();
    verify(&pk, &message, &other_tree).unwrap();
    assert_ne!(first.top(), other_tree.top());
    assert_eq!(sign(&sk, &message, 5).unwrap(), first, "signing is deterministic");
}

/// Pins a full key's public key and one signature's bytes, so that a change to the
/// seed derivation, the addresses, the hash inputs or the serialization cannot pass
/// unnoticed. An implementation of `doc/lean_xmss` written from the text alone gives
/// the same bytes.
#[test]
fn known_answer() {
    let hex = |bytes: &[u8]| bytes.iter().map(|b| format!("{b:02x}")).collect::<String>();
    let seed: Seed = std::array::from_fn(|i| (i as u8).wrapping_mul(37).wrapping_add(11));
    let (sk, pk) = key_gen_from_seed(seed);
    let message: Message = std::array::from_fn(|i| (i * 7 + 3) as u8);
    let signature = sign(&sk, &message, 0xA5C3_7E19).unwrap();
    assert_eq!(
        hex(&pk.to_bytes()),
        "4066673001949b6ff8b9299fd4ea17d28787ad501aafe4b64e2488107b5e3ec3"
    );
    assert_eq!(
        hex(&primitives::hash::hash(&signature.to_bytes())),
        "f10d62beec95e64e1477028252c2c8683695eab9d8787ad8fa722f47b5fd5234"
    );
}

#[test]
fn mock_keys_sign_in_range() {
    let message = test_message();
    // A range across two bottom trees.
    let start = (3 << LAYER_HEIGHT) - 2;
    let (sk, pk) = mock::key_gen_from_seed([2; SEED_LEN], start, start + 5);
    for leaf_index in start..=start + 5 {
        let sig = mock::sign(&sk, &message, leaf_index).unwrap();
        assert_eq!(sig.leaf_index(), leaf_index);
        verify(&pk, &message, &sig).unwrap();
    }
    let (_, same) = mock::key_gen_from_seed([2; SEED_LEN], start, start + 5);
    assert_eq!(pk, same);
}

/// The leaf index rides the signature, so the verifier hashes at it: moved to a
/// neighbour, or the message changed, the signature no longer verifies.
#[test]
fn signature_is_bound_to_its_leaf_index_and_message() {
    let (sk, pk) = mock::key_gen_from_seed([4; SEED_LEN], 0, 7);
    let message = test_message();
    let sig = mock::sign(&sk, &message, 5).unwrap();
    let mut bytes = sig.to_bytes();
    bytes[..4].copy_from_slice(&4u32.to_le_bytes());
    let moved = LeanXmssSignature::from_bytes(&bytes);
    assert!(verify(&pk, &message, &moved).is_err());
    let mut other = message;
    other[0] ^= 1;
    assert!(verify(&pk, &other, &sig).is_err());
}

/// A secret key encodes as its seed, and the reloaded key signs exactly as the
/// original does.
#[test]
fn secret_key_survives_a_round_trip() {
    let seed = [6; SEED_LEN];
    let (sk, pk) = key_gen_from_seed(seed);
    assert_eq!(sk.to_bytes(), seed);
    let reloaded = LeanXmssSecretKey::from_bytes(&sk.to_bytes());
    assert_eq!(reloaded.public_key(), pk);
    let message = test_message();
    assert_eq!(sign(&reloaded, &message, 43).unwrap(), sign(&sk, &message, 43).unwrap());
}

/// The address layout, and `Th` as the leanVM hash of `P | A | M`.
#[test]
fn address_layout() {
    let tw = tweak(TWEAK_NODE, layer_hi(1, 0x12), 0x89abcdef).step(5);
    assert_eq!(address(&tw)[..8], [0xef, 0xcd, 0xab, 0x89, 0x12, 0x00, 0x80, 0xa3]);
    assert_eq!(address(&tw)[8..], [0; 8]);
    let pp: PublicParam = std::array::from_fn(|i| i as u8 + 1);
    let payload = [9u8; N];
    let input = [&pp[..], &address(&tw), &payload].concat();
    assert_eq!(th(&pp, &tw, &payload)[..], primitives::hash::hash(&input)[..N]);
}

/// Detect changes to the encoding predicate through its grinding cost.
#[test]
#[ignore]
fn encoding_grinding_bits() {
    let n = 1 << 26;
    let mut rng = StdRng::seed_from_u64(0);
    let hits = (0..n).filter(|_| codeword(&rng.random()).is_some()).count();
    let bits = (n as f64 / hits as f64).log2();
    println!("Average grinding bits: {bits:.1}");
    assert!((19.0..21.0).contains(&bits), "grinding cost moved: {bits:.2} bits");
}
