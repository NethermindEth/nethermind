//! leanSPHINCS over Keccak-256: the stateless scheme specified in
//! `doc/lean_sphincs/main.tex`, SLH-DSA (FIPS 205) with WOTS+C as its one-time
//! signature and a hypertree of layers of different heights. A message digest
//! picks a FORS instance, and one leaf in each of its 24 trees. The FORS
//! signature signs the message, and a hypertree of three layers of WOTS+C keys,
//! of heights 8, 8 and 10 from the bottom, signs the FORS public key up to the
//! public key. A public key is 32 bytes, a signature 7740, and a verification 515
//! permutations.
//!
//! The hashes follow FIPS 205's SHAKE instances, Keccak-256 standing in for
//! SHAKE256 and its output truncated to `n = 16` bytes: `F`, `H`, `T_l` and `PRF`
//! hash `PK.seed | ADRS | M` with the 32-byte address of FIPS 205 §4.2, and
//! `PRF_msg` hashes `SK.prf | opt_rand | M'`, `M'` being pure SLH-DSA's framing of
//! the message under a fixed context. `H_msg`, which needs more bytes than one call
//! gives, is two calls on `R | PK.seed | PK.root | M` and a counter, the 32-byte
//! message unframed, and WOTS+C's encoding hash has an address type of its own (the
//! `hash` module).
//!
//! The secret key is `SK.seed`, 32 bytes and the only secret: two hashes of it under
//! the scheme's tag give `PK.seed` and a 32-byte `SK.prf` ([`expand_key`]). Key
//! generation builds the top tree, and signing builds it again for `PK.root`, then
//! the FORS instance and the two trees below it.

mod hash;
pub use hash::*;
mod wots;
pub use wots::*;
mod fors;
pub use fors::*;
mod scheme;
pub use scheme::*;

use primitives::hash::permutations;

/// `SK.seed`, the whole secret key.
pub const SEED_LEN: usize = 32;
pub type Seed = [u8; SEED_LEN];

/// Hashed before `SK.seed`, so no other scheme derives from the same bytes.
pub(crate) const SEED_TAG: &[u8] = b"leanSPHINCS";

/// `n`: the security parameter, the length of every hash value, `PK.seed` and node.
pub const N: usize = crate::N;
pub type Digest = [u8; N];

/// The message to sign (a 256-bit message hash).
pub const MESSAGE_LEN: usize = 32;
pub type Message = [u8; MESSAGE_LEN];

/// The serialized width of an encoding counter.
pub const COUNTER_LEN: usize = 4;

// The one-time signature (WOTS+C).
/// `w`: chunk size in bits.
pub const W: usize = 2;
/// `2^w`: one more than the steps of a hash chain.
pub const CHAIN_LEN: usize = 1 << W;
/// `v`: code length, one hash chain per chunk.
pub const V: usize = 64;
/// `T`: the sum every codeword has. Above the mean `v(2^w-1)/2 = 96`, so
/// verification walks fewer chain steps and the signer grinds a counter for it.
pub const TARGET_SUM: usize = 128;
/// `C_max`: encoding counters tried per one-time signature.
pub(crate) const MAX_ENCODING_ATTEMPTS: u64 = 1 << 32;

// The hypertree.
/// `d`: the layers.
pub const LAYERS: usize = 3;
/// The height of each layer's trees, bottom first.
pub const LAYER_HEIGHTS: [usize; LAYERS] = [8, 8, 10];
/// `h`: the bits of an index, which names a bottom leaf and its FORS instance.
pub const H: usize = LAYER_HEIGHTS[0] + LAYER_HEIGHTS[1] + LAYER_HEIGHTS[2];

// The few-time signature (FORS).
/// `k`: the trees of a FORS instance.
pub const FORS_TREES: usize = 24;
/// `a`: the height of a FORS tree.
pub const FORS_HEIGHT: usize = 10;

/// The digest's parts, in bytes (FIPS 205 Algorithm 19): the FORS message
/// `md`, then the tree index, then the bottom leaf index.
pub const MD_BYTES: usize = (FORS_TREES * FORS_HEIGHT).div_ceil(8);
pub const TREE_INDEX_BYTES: usize = (H - LAYER_HEIGHTS[0]).div_ceil(8);
pub const LEAF_INDEX_BYTES: usize = LAYER_HEIGHTS[0].div_ceil(8);
/// `m`: the bytes `H_msg` outputs.
pub const DIGEST_BYTES: usize = MD_BYTES + TREE_INDEX_BYTES + LEAF_INDEX_BYTES;
/// The `H_msg` calls that give them.
pub const DIGEST_CALLS: usize = DIGEST_BYTES.div_ceil(32);

/// `PK.seed | PK.root`.
pub const PUB_KEY_SIZE: usize = 2 * N;
/// One FORS tree's opening: the secret, then the path.
pub const FORS_OPENING_SIZE: usize = N + FORS_HEIGHT * N;

/// One layer's signature on trees of height `height`: the counter, the chain
/// values and the path.
pub const fn layer_sig_size(height: usize) -> usize {
    COUNTER_LEN + V * N + height * N
}

pub const SIG_SIZE: usize = N
    + FORS_TREES * FORS_OPENING_SIZE
    + layer_sig_size(LAYER_HEIGHTS[0])
    + layer_sig_size(LAYER_HEIGHTS[1])
    + layer_sig_size(LAYER_HEIGHTS[2]);

/// Permutations of one verification: `H_msg`, the FORS leaves and nodes, the
/// FORS key, the encodings, the chains, the one-time keys and the paths.
pub(crate) const VERIFY_PERMUTATIONS: usize = DIGEST_CALLS * permutations(3 * N + MESSAGE_LEN + 4)
    + FORS_TREES * (permutations(PREFIX_LEN + N) + FORS_HEIGHT * permutations(PREFIX_LEN + 2 * N))
    + permutations(PREFIX_LEN + FORS_TREES * N)
    + LAYERS * permutations(PREFIX_LEN + N + COUNTER_LEN)
    + LAYERS * (V * (CHAIN_LEN - 1) - TARGET_SUM + permutations(PREFIX_LEN + V * N))
    + H * permutations(PREFIX_LEN + 2 * N);

const _: () = assert!(W * V == 8 * N);
const _: () = assert!(TARGET_SUM < V * (CHAIN_LEN - 1));
const _: () = assert!(H == 26 && DIGEST_BYTES == 34 && DIGEST_CALLS == 2);
const _: () = assert!(PUB_KEY_SIZE == 32);
const _: () = assert!(SIG_SIZE == 7740);
const _: () = assert!(VERIFY_PERMUTATIONS == 515);
