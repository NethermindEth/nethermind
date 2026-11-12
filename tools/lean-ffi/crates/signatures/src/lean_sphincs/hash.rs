//! The framing every hash shares: Keccak-256 over 32-byte words, the FIPS 205
//! address, the message digest, and the signer's secret derivation.

use super::*;
use primitives::hash::hash;

/// An `n`-byte value as the word it is hashed as: top-aligned, `v ‖ 0^16`.
pub fn word(v: &[u8; N]) -> [u8; 32] {
    let mut out = [0; 32];
    out[..N].copy_from_slice(v);
    out
}

/// A hash output truncated to `n` bytes (the verifier's `N_MASK`).
pub fn truncate(digest: &[u8; 32]) -> Digest {
    digest[..N].try_into().unwrap()
}

// FIPS 205 address types (Table 1).
pub const WOTS_HASH: u32 = 0;
pub const WOTS_PK: u32 = 1;
pub const TREE: u32 = 2;
pub const FORS_TREE: u32 = 3;
pub const FORS_ROOTS: u32 = 4;
/// Secret derivation (signer-private: no verifier ever sees these addresses).
pub const WOTS_PRF: u32 = 5;
pub const FORS_PRF: u32 = 6;

/// The 32-byte address (FIPS 205 §4.2, uncompressed), every field big-endian:
/// layer in bytes `0..4`, the 96-bit tree address in `4..16`, the type in
/// `16..20`, then three type-dependent words in `20..24`, `24..28`, `28..32`
/// (key pair, chain or height, hash address or tree index).
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash)]
pub struct Adrs {
    pub layer: u32,
    pub tree: u64,
    pub typ: u32,
    pub word1: u32,
    pub word2: u32,
    pub word3: u32,
}

impl Adrs {
    pub const fn new(layer: u32, tree: u64, typ: u32, word1: u32, word2: u32, word3: u32) -> Self {
        Self {
            layer,
            tree,
            typ,
            word1,
            word2,
            word3,
        }
    }

    pub fn to_bytes(&self) -> [u8; 32] {
        let mut out = [0; 32];
        out[0..4].copy_from_slice(&self.layer.to_be_bytes());
        out[8..16].copy_from_slice(&self.tree.to_be_bytes());
        out[16..20].copy_from_slice(&self.typ.to_be_bytes());
        out[20..24].copy_from_slice(&self.word1.to_be_bytes());
        out[24..28].copy_from_slice(&self.word2.to_be_bytes());
        out[28..32].copy_from_slice(&self.word3.to_be_bytes());
        out
    }
}

/// The tweakable hash `hash(pkSeed ‖ ADRS ‖ v_0 ‖ v_1 ‖ …)[..16]`, every
/// value a top-aligned word. `F` on one value (96 bytes), `H` on two (128), `T_l`
/// on the FORS roots or a WOTS key.
pub fn th(pp: &PublicParam, adrs: &Adrs, values: &[Digest]) -> Digest {
    let mut input = Vec::with_capacity(64 + 32 * values.len());
    input.extend_from_slice(&word(pp));
    input.extend_from_slice(&adrs.to_bytes());
    for v in values {
        input.extend_from_slice(&word(v));
    }
    truncate(&hash(&input))
}

/// `H_msg = hash(0xFF…FF ‖ R ‖ pkSeed ‖ pkRoot ‖ M)`, 112 bytes, in full.
pub fn h_msg(pp: &PublicParam, root: &Digest, r: &Randomizer, m: &Message) -> [u8; 32] {
    let mut input = [0u8; 112];
    input[0..32].copy_from_slice(&HMSG_DOMAIN);
    input[32..48].copy_from_slice(r);
    input[48..64].copy_from_slice(pp);
    input[64..80].copy_from_slice(root);
    input[80..112].copy_from_slice(m);
    hash(&input)
}

/// `(d >> shift) & (2^n - 1)`, `d` read as a big-endian 256-bit integer: bit `b`
/// is bit `b mod 8` of byte `31 - b/8`.
pub fn digest_bits(d: &[u8; 32], shift: usize, n: usize) -> u32 {
    debug_assert!(n <= 32 && shift + n <= 256);
    (0..n).fold(0, |acc, i| {
        let b = shift + i;
        acc | (u32::from(d[31 - b / 8] >> (b % 8) & 1) << i)
    })
}

// ---------------------------------------------------------------------------
// The signer's secrets. Nothing here is checked by a verifier; it is the
// reference signer's derivation, kept so both produce the same signatures.

/// `PRF(SK.seed, ADRS) = hash(SK.seed ‖ ADRS)[..16]`, `SK.seed` a
/// top-aligned word and `ADRS` of type [`WOTS_PRF`] or [`FORS_PRF`].
pub(crate) fn prf(sk_seed: &[u8; N], adrs: &Adrs) -> Digest {
    let mut input = [0u8; 64];
    input[..32].copy_from_slice(&word(sk_seed));
    input[32..].copy_from_slice(&adrs.to_bytes());
    truncate(&hash(&input))
}

/// `R = hash(SK.prf ‖ M)[..16]`: deterministic, and secret-keyed so nobody
/// else can steer messages onto chosen FORS instances.
pub(crate) fn randomizer(sk_prf: &[u8; N], m: &Message) -> Randomizer {
    let mut input = [0u8; 64];
    input[..32].copy_from_slice(&word(sk_prf));
    input[32..].copy_from_slice(m);
    truncate(&hash(&input))
}
