//! SPHINCS+ in the NiceTry "SPHINCS- v2" profile: the stateless scheme whose EVM
//! verifier is `SphincsVerifier_v2.sol` (RivaLabs-Core/Post-Quantum-AA-Infra),
//! specified in `doc/sphincs/main.tex`.
//!
//! Standard FORS under a five-layer standard WOTS+ hypertree over Keccak-256:
//! `n = 16`, `h = 20`, `d = 5`, `h' = 4`, `a = 9`, `k = 19`, `w = 16`,
//! `l = 32 + 3 = 35`. No grinding anywhere. A public key is `(pkSeed, pkRoot)`,
//! a signature 6,176 bytes.
//!
//! Every tweakable hash is `keccak256` of 32-byte words: an `n`-byte value `v` enters as
//! `v ‖ 0^16` (top-aligned in a `bytes32`), the 32-byte FIPS 205 address as is,
//! and outputs are truncated to their first 16 bytes. Digests are read as
//! big-endian 256-bit integers, fields LSB-first (`(d >> (i·a)) & (2^a - 1)`).
//!
//! The verifier is the specification. The signer mirrors the reference signer
//! (`scripts/sphincs_v2_reference.py`) byte for byte: from the same three seeds
//! ([`sign_with_seeds`]) both produce the same signature. A real key is one
//! 32-byte master secret the three seeds are derived from ([`key_gen_from_seed`]).


mod hash;
pub use hash::*;
mod wots;
pub use wots::*;
mod fors;
pub use fors::*;
mod scheme;
pub use scheme::*;

/// `n`: hash value and Merkle node length, in bytes.
pub const N: usize = crate::N;
pub type Digest = [u8; N];

/// `pkSeed`, the public parameter every hash is keyed by.
pub const PUBLIC_PARAM_LEN: usize = 16;
pub type PublicParam = [u8; PUBLIC_PARAM_LEN];

/// The master secret a key is derived from ([`key_gen_from_seed`]).
pub const MASTER_SECRET_LEN: usize = 32;
pub type MasterSecret = [u8; MASTER_SECRET_LEN];
// The names upstream leanSPHINCS callers use.
pub const SEED_LEN: usize = MASTER_SECRET_LEN;
pub type Seed = MasterSecret;
pub type PkSeed = PublicParam;

/// `R`, the per-signature randomizer the message digest is computed under.
pub const RANDOMIZER_LEN: usize = 16;
pub type Randomizer = [u8; RANDOMIZER_LEN];

/// The message to sign: a `bytes32` (an ERC-4337 `userOpHash`).
pub const MESSAGE_LEN: usize = 32;
pub type Message = [u8; MESSAGE_LEN];

// WOTS+.
/// `log2 w`: bits per digit.
pub const LOG_W: usize = 4;
/// `w`: one more than the steps of a hash chain.
pub const W: usize = 1 << LOG_W;
/// `len1`: message digits, `8n / log w`.
pub const LEN1: usize = 8 * N / LOG_W;
/// `len2`: checksum digits, `floor(log2(len1 (w-1)) / log2 w) + 1`.
pub const LEN2: usize = 3;
/// `l`: chains, one per digit.
pub const L: usize = LEN1 + LEN2;
/// The largest checksum, `len1 (w - 1)`: its value when every digit is zero.
pub const MAX_CSUM: usize = LEN1 * (W - 1);

// The hypertree.
/// `d`: hypertree layers, numbered from the bottom (layer 0 signs the FORS key).
pub const D: usize = 5;
/// `h' = h/d`: the Merkle tree height of each layer.
pub const SUBTREE_H: usize = 4;
/// `h`: total height, so `2^h` FORS instances.
pub const H: usize = D * SUBTREE_H;

// FORS.
/// `a`: log2 of the leaves in one FORS tree.
pub const A: usize = 9;
/// `k`: FORS trees.
pub const K: usize = 19;

/// `H_msg`'s domain word, `0xFF…FF`: 112 bytes of hash input where every
/// tweakable hash takes 96 or 128.
pub const HMSG_DOMAIN: [u8; 32] = [0xFF; 32];

/// `(pkSeed, pkRoot)`.
pub const PUB_KEY_SIZE: usize = N + PUBLIC_PARAM_LEN;
/// A secret key is its master secret; the seeds and the root are derived.
pub const SECRET_KEY_SIZE: usize = MASTER_SECRET_LEN;
/// One hypertree layer of a signature: the chains, then the path.
pub const LAYER_SIZE: usize = L * N + SUBTREE_H * N;
/// `R ‖ k × [secret ‖ path] ‖ d layers`.
pub const SIG_SIZE: usize = RANDOMIZER_LEN + K * N + K * A * N + D * LAYER_SIZE;

/// Hash calls a verification makes outside the chains: `H_msg`, the FORS
/// leaves, nodes and roots, and per layer the WOTS key and the path.
/// Each chain adds `w - 1 - digit` calls, data-dependent.
pub const VERIFY_FIXED_HASHES: usize = 1 + K * (1 + A) + 1 + D * (1 + SUBTREE_H);


const _: () = assert!(LEN1 == 32 && MAX_CSUM < 1 << (LOG_W * LEN2) && MAX_CSUM >= 1 << (LOG_W * (LEN2 - 1)));
const _: () = assert!(K * A + H <= 256);
const _: () = assert!(PUB_KEY_SIZE == 32);
const _: () = assert!(LAYER_SIZE == 624);
const _: () = assert!(SIG_SIZE == 6176);
const _: () = assert!(VERIFY_FIXED_HASHES == 217);
