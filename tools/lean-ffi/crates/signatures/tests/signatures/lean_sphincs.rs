use signatures::lean_sphincs::*;

fn seed(x: u8) -> Seed {
    std::array::from_fn(|i| x.wrapping_mul(31).wrapping_add(i as u8))
}

#[test]
fn signatures_verify_and_tampered_ones_do_not() {
    let (sk, pk) = key_gen_from_seed(seed(3));
    // The seed is SK.seed, PK.seed = H("leanSPHINCS" || SK.seed || 0) cut to N bytes and
    // SK.prf = H("leanSPHINCS" || SK.seed || 1).
    let derive = |domain: u8| primitives::hash::hash(&[b"leanSPHINCS".as_slice(), &seed(3), &[domain]].concat());
    let expanded = expand_key(&seed(3));
    assert_eq!(expanded.sk_seed, seed(3));
    assert_eq!(expanded.pk_seed[..], derive(0)[..N]);
    assert_eq!(expanded.sk_prf, derive(1));
    assert_eq!(pk.pk_seed(), &expanded.pk_seed);
    assert_eq!(LeanSphincsSecretKey::from_bytes(&sk.to_bytes()).public_key(), pk);
    let message: Message = std::array::from_fn(|i| (3 + i) as u8);
    let signature = sign(&sk, &message).unwrap();
    assert_eq!(verify(&pk, &message, &signature), Ok(()));
    assert_eq!(LeanSphincsSignature::from_bytes(&signature.to_bytes()), signature);

    let mut other = message;
    other[0] ^= 1;
    assert!(verify(&pk, &other, &signature).is_err());
    let mut bytes = pk.to_bytes();
    bytes[0] ^= 1;
    let key = LeanSphincsPublicKey::from_bytes(&bytes);
    assert!(verify(&key, &message, &signature).is_err());
    // Every value of the signature is bound: a flipped bit in any of them, or in
    // a counter, is refused.
    let bytes = signature.to_bytes();
    let mut values = (0..N + FORS_TREES * FORS_OPENING_SIZE).step_by(N).collect::<Vec<_>>();
    let mut at = N + FORS_TREES * FORS_OPENING_SIZE;
    for height in LAYER_HEIGHTS {
        values.extend((at..at + COUNTER_LEN).chain((at + COUNTER_LEN..at + layer_sig_size(height)).step_by(N)));
        at += layer_sig_size(height);
    }
    for at in values {
        let mut bad = bytes;
        bad[at] ^= 1;
        let bad = LeanSphincsSignature::from_bytes(&bad);
        assert!(verify(&pk, &message, &bad).is_err(), "byte {at}");
    }
}
