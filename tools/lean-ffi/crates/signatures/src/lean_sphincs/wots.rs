//! The one-time signature: `v` hash chains of `2^w - 1` steps, and the
//! target-sum code that replaces the Winternitz checksum (WOTS+C), hashed as
//! FIPS 205's WOTS+ is (§5).
//!
//! A codeword is `v` chunks summing to `T`. Two distinct words of equal sum
//! cannot be ordered componentwise, so revealing chain position `x_i` on every
//! chain gives a forger nothing: any other codeword needs a position below one
//! of the revealed ones, a preimage of a chain step. The price is that most
//! messages do not encode into the code at all, hence the counter the signer
//! searches for and the signature carries.

use crate::lean_sphincs::*;

/// A one-time key of the hypertree: its layer, counted from the bottom, its tree
/// on that layer, and its leaf in that tree.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub struct OtsKey {
    pub layer: usize,
    pub tree: u64,
    pub leaf: u32,
}

impl OtsKey {
    /// The key of `layer` on the way from bottom leaf `idx` up: the bits below
    /// the layer dropped, the rest split into a tree and a leaf.
    pub fn of_index(layer: usize, idx: u64) -> Self {
        let height = LAYER_HEIGHTS[layer];
        let e = idx >> LAYER_HEIGHTS[..layer].iter().sum::<usize>();
        Self {
            layer,
            tree: e >> height,
            leaf: (e & ((1 << height) - 1)) as u32,
        }
    }

    /// The address of the key's tree, type `WOTS_HASH`.
    pub fn tree_adrs(&self) -> Adrs {
        Adrs::new(self.layer as u32, self.tree)
    }

    /// The key's address of type `t`: its tree's, with its leaf as the key pair.
    pub fn adrs(&self, t: u32) -> Adrs {
        self.tree_adrs().with_type(t).with_key_pair(self.leaf)
    }
}

/// The starts of the `v` chains of a one-time key, one `PRF` each.
fn wots_secrets(sk: &ExpandedKey, key: &OtsKey) -> [Digest; V] {
    let adrs = key.adrs(WOTS_PRF);
    std::array::from_fn(|i| prf(&sk.pk_seed, &sk.sk_seed, &adrs.with_chain(i as u32)))
}

/// `chain`: `steps` steps of chain `i` from position `start`; a step's hash
/// address is the position it leaves.
fn chain(pk_seed: &PkSeed, key: &OtsKey, i: usize, start: usize, steps: usize, value: Digest) -> Digest {
    debug_assert!(start + steps < CHAIN_LEN);
    let adrs = key.adrs(WOTS_HASH).with_chain(i as u32);
    (start..start + steps).fold(value, |current, from| {
        th(pk_seed, &adrs.with_hash(from as u32), &current)
    })
}

/// The Merkle leaf of a one-time key, its public key: `T_v` over its chain ends.
fn wots_leaf_hash(pk_seed: &PkSeed, key: &OtsKey, ends: &[Digest; V]) -> Digest {
    th_digests(pk_seed, &key.adrs(WOTS_PK), ends)
}

/// The hash of `Enc(K, M, c)` before its counter: `PK.seed | ADRS_enc(K) | M`.
fn encoder(pk_seed: &PkSeed, key: &OtsKey, message: &Digest) -> primitives::hash::Hasher {
    let mut hasher = hasher(pk_seed, &key.adrs(WOTS_ENC));
    hasher.update(message);
    hasher
}

/// `Enc(K, M, c)`: `Trunc_n(H(PK.seed | ADRS_enc(K) | M | BE_32(c)))` as a codeword,
/// or `None` if this counter's digest is not one.
fn encode(encoder: &primitives::hash::Hasher, c: u32) -> Option<[u8; V]> {
    codeword(&crate::truncate(encoder.clone().update(&c.to_be_bytes()).finalize()))
}

/// `base_2b(D, w, v)` (FIPS 205 Algorithm 4): the digest as `v` chunks of `w` bits,
/// most significant first; a codeword if they sum to `T`.
fn codeword(digest: &Digest) -> Option<[u8; V]> {
    let d = u128::from_be_bytes(*digest);
    let x: [u8; V] = std::array::from_fn(|i| ((d >> (W * (V - 1 - i))) & (CHAIN_LEN as u128 - 1)) as u8);
    (x.iter().map(|&chunk| chunk as usize).sum::<usize>() == TARGET_SUM).then_some(x)
}

/// `Wots.sign`: the LEAST admissible counter, and the chain value each chunk
/// opens. Deterministic in its inputs, which is what keeps one key to one
/// codeword per message: a resumed or randomized search would leak two
/// incomparable codewords.
pub(crate) fn wots_sign(sk: &ExpandedKey, key: &OtsKey, message: &Digest) -> Option<(u32, [Digest; V])> {
    let pk_seed = &sk.pk_seed;
    let encoder = encoder(pk_seed, key, message);
    let (c, x) = (0..MAX_ENCODING_ATTEMPTS).find_map(|c| encode(&encoder, c as u32).map(|x| (c as u32, x)))?;
    let secrets = wots_secrets(sk, key);
    let signature = std::array::from_fn(|i| chain(pk_seed, key, i, 0, x[i] as usize, secrets[i]));
    Some((c, signature))
}

/// `Wots.leaf`: the leaf a claimed signature recovers, and the codeword it opens
/// at, or `None` if its counter is not admissible for the message.
pub fn wots_leaf(
    pk_seed: &PkSeed,
    key: &OtsKey,
    message: &Digest,
    c: u32,
    signature: &[Digest; V],
) -> Option<([u8; V], Digest)> {
    let x = encode(&encoder(pk_seed, key, message), c)?;
    let ends = std::array::from_fn(|i| {
        let start = x[i] as usize;
        chain(pk_seed, key, i, start, CHAIN_LEN - 1 - start, signature[i])
    });
    Some((x, wots_leaf_hash(pk_seed, key, &ends)))
}

/// `wots_pkGen`: the leaf of a one-time key, from the secret key, what building a
/// tree spends its hashes on.
pub(crate) fn wots_public_leaf(sk: &ExpandedKey, key: &OtsKey) -> Digest {
    let secrets = wots_secrets(sk, key);
    let ends = std::array::from_fn(|i| chain(&sk.pk_seed, key, i, 0, CHAIN_LEN - 1, secrets[i]));
    wots_leaf_hash(&sk.pk_seed, key, &ends)
}
