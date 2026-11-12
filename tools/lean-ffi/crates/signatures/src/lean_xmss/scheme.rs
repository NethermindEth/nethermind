//! Keys, signing and verification. The secret key holds the top tree and builds
//! a bottom tree, with the top layer's signature of its root, the first time it
//! signs in it, keeping the last one.

use std::ops::RangeInclusive;
use std::sync::{Arc, Mutex};

use crate::lean_xmss::*;

/// Ordered lexicographically on `to_bytes()`, which is what an aggregate's claims are
/// sorted by.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub struct LeanXmssPublicKey {
    pub(crate) merkle_root: Digest,
    pub(crate) public_param: PublicParam,
}

impl LeanXmssPublicKey {
    pub fn public_param(&self) -> &PublicParam {
        &self.public_param
    }

    /// The specification's serialization, exactly [`PUB_KEY_SIZE`] bytes.
    pub fn to_bytes(&self) -> [u8; PUB_KEY_SIZE] {
        let mut out = [0u8; PUB_KEY_SIZE];
        out[..N].copy_from_slice(&self.merkle_root);
        out[N..].copy_from_slice(&self.public_param);
        out
    }

    pub fn from_bytes(bytes: &[u8; PUB_KEY_SIZE]) -> Self {
        Self {
            merkle_root: bytes[..N].try_into().unwrap(),
            public_param: bytes[N..].try_into().unwrap(),
        }
    }
}

/// One layer's signature: the randomizer, the value each digit opens, and the
/// authentication path.
#[derive(Debug, Clone, PartialEq, Eq, Hash)]
pub struct LayerSignature {
    pub(crate) randomizer: Randomizer,
    pub(crate) chain_values: [Digest; V],
    pub(crate) path: [Digest; LAYER_HEIGHT],
}

impl LayerSignature {
    pub fn randomizer(&self) -> &Randomizer {
        &self.randomizer
    }

    pub fn chain_values(&self) -> &[Digest; V] {
        &self.chain_values
    }
}

/// Carries the leaf index it was made at, so a verifier needs only the key and the
/// message. The top layer's signature is the same for every leaf of a bottom tree.
#[derive(Debug, Clone, PartialEq, Eq, Hash)]
pub struct LeanXmssSignature {
    pub(crate) leaf_index: LeafIndex,
    pub(crate) bottom: LayerSignature,
    pub(crate) top: LayerSignature,
}

impl LeanXmssSignature {
    pub fn leaf_index(&self) -> LeafIndex {
        self.leaf_index
    }

    pub fn bottom(&self) -> &LayerSignature {
        &self.bottom
    }

    pub fn top(&self) -> &LayerSignature {
        &self.top
    }

    /// The specification's serialization, exactly [`SIG_SIZE`] bytes.
    pub fn to_bytes(&self) -> [u8; SIG_SIZE] {
        let mut out = [0; SIG_SIZE];
        let mut at = 0;
        let mut put = |bytes: &[u8]| {
            out[at..at + bytes.len()].copy_from_slice(bytes);
            at += bytes.len();
        };
        put(&self.leaf_index.to_le_bytes());
        for layer in [&self.bottom, &self.top] {
            put(&layer.randomizer);
            put(layer.chain_values.as_flattened());
            put(layer.path.as_flattened());
        }
        debug_assert_eq!(at, SIG_SIZE);
        out
    }

    pub fn from_bytes(bytes: &[u8; SIG_SIZE]) -> Self {
        let mut at = 0;
        let mut take = |len: usize| {
            at += len;
            &bytes[at - len..at]
        };
        let leaf_index = LeafIndex::from_le_bytes(take(LEAF_INDEX_SIZE).try_into().unwrap());
        let mut layer = || LayerSignature {
            randomizer: take(RANDOMIZER_LEN).try_into().unwrap(),
            chain_values: std::array::from_fn(|_| take(N).try_into().unwrap()),
            path: std::array::from_fn(|_| take(N).try_into().unwrap()),
        };
        let (bottom, top) = (layer(), layer());
        debug_assert_eq!(at, SIG_SIZE);
        Self {
            leaf_index,
            bottom,
            top,
        }
    }
}

pub struct LeanXmssSecretKey {
    /// The key's seed, which the key serializes and whose [`scheme_seed`] every
    /// secret derives from.
    key_seed: Seed,
    public_param: PublicParam,
    top: LayerTree,
    /// The bottom tree the key last signed in.
    bottom: Mutex<Option<Arc<BottomTree>>>,
}

impl std::fmt::Debug for LeanXmssSecretKey {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("LeanXmssSecretKey")
            .field("public_key", &self.public_key())
            .finish_non_exhaustive()
    }
}

struct BottomTree {
    index: u32,
    tree: LayerTree,
    top_signature: LayerSignature,
}

#[derive(Debug, PartialEq, Eq, Clone, Copy, Hash)]
pub enum LeanXmssSignError {
    NoAdmissibleEncoding,
}

impl std::fmt::Display for LeanXmssSignError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::NoAdmissibleEncoding => write!(f, "no admissible encoding within the randomizer trial limit"),
        }
    }
}

impl std::error::Error for LeanXmssSignError {}

/// Every leaf index: what a key signs at, but for a mock key.
pub(crate) const ALL_LEAVES: RangeInclusive<LeafIndex> = 0..=LeafIndex::MAX;

/// A fresh key pair, its seed from the operating system's randomness. Generates
/// the whole top tree.
pub fn key_gen() -> (LeanXmssSecretKey, LeanXmssPublicKey) {
    key_gen_from_seed(rand::random())
}

/// Deterministic [`key_gen`]: the seed is the key's entire secret material. Never
/// derive another key from it, a mock key included: the two would share one-time
/// keys (see `mock`).
pub fn key_gen_from_seed(seed: Seed) -> (LeanXmssSecretKey, LeanXmssPublicKey) {
    generate(seed, ALL_LEAVES)
}

/// The key over the leaf indices `leaves`; nodes over no leaf in it are fillers.
pub(crate) fn generate(key_seed: Seed, leaves: RangeInclusive<LeafIndex>) -> (LeanXmssSecretKey, LeanXmssPublicKey) {
    let seed = scheme_seed(&key_seed);
    let public_param = public_param(&seed);
    let (first, last) = (layer_index(*leaves.start(), 1), layer_index(*leaves.end(), 1));
    let top = LayerTree::build(&public_param, &seed, 1, 0, first, last);
    let sk = LeanXmssSecretKey {
        key_seed,
        public_param,
        top,
        bottom: Mutex::new(None),
    };
    let pk = sk.public_key();
    (sk, pk)
}

/// Never use the same key and leaf index to sign two different messages.
/// Signing is deterministic. The first signature in a bottom tree builds it with
/// `parallel`, so never sign from inside a `parallel` task.
pub fn sign(
    sk: &LeanXmssSecretKey,
    message: &Message,
    leaf_index: LeafIndex,
) -> Result<LeanXmssSignature, LeanXmssSignError> {
    sk.sign_over(message, leaf_index, ALL_LEAVES)
}

impl LeanXmssSecretKey {
    /// SECRET KEY MATERIAL: the key's seed, all of the key.
    pub fn to_bytes(&self) -> Seed {
        self.key_seed
    }

    /// Regenerates the key, as key generation does.
    pub fn from_bytes(bytes: &Seed) -> Self {
        key_gen_from_seed(*bytes).0
    }

    pub fn public_key(&self) -> LeanXmssPublicKey {
        LeanXmssPublicKey {
            merkle_root: self.top.root(),
            public_param: self.public_param,
        }
    }

    /// Builds the bottom tree `leaf_index` is in, and signs its root, ahead of
    /// [`sign`]: the first signature in a bottom tree pays for both.
    pub fn prepare(&self, leaf_index: LeafIndex) -> Result<(), LeanXmssSignError> {
        self.bottom_tree(leaf_index, ALL_LEAVES).map(drop)
    }

    /// [`sign`], for a key whose trees hold only the leaf indices `leaves`.
    pub(crate) fn sign_over(
        &self,
        message: &Message,
        leaf_index: LeafIndex,
        leaves: RangeInclusive<LeafIndex>,
    ) -> Result<LeanXmssSignature, LeanXmssSignError> {
        let bottom = self.bottom_tree(leaf_index, leaves)?;
        let seed = scheme_seed(&self.key_seed);
        let (randomizer, chain_values) = wots_sign(&self.public_param, &seed, 0, leaf_index, message)
            .ok_or(LeanXmssSignError::NoAdmissibleEncoding)?;
        Ok(LeanXmssSignature {
            leaf_index,
            bottom: LayerSignature {
                randomizer,
                chain_values,
                path: bottom.tree.path(&self.public_param, &seed, tree_leaf(leaf_index)),
            },
            top: bottom.top_signature.clone(),
        })
    }

    /// The bottom tree `leaf_index` is in, with the top layer's signature of its root:
    /// the cached one, or built and cached. Built outside the lock, which is only ever
    /// held to read or replace the cache.
    fn bottom_tree(
        &self,
        leaf_index: LeafIndex,
        leaves: RangeInclusive<LeafIndex>,
    ) -> Result<Arc<BottomTree>, LeanXmssSignError> {
        let index = layer_index(leaf_index, 1);
        if let Some(bottom) = self.bottom.lock().unwrap().as_ref().filter(|b| b.index == index) {
            return Ok(Arc::clone(bottom));
        }
        let (pp, seed) = (&self.public_param, &scheme_seed(&self.key_seed));
        let base = index << LAYER_HEIGHT;
        let first = (*leaves.start()).max(base) - base;
        let last = (*leaves.end()).min(base | tree_leaf(LeafIndex::MAX)) - base;
        let tree = LayerTree::build(pp, seed, 0, base, first, last);
        let (randomizer, chain_values) =
            wots_sign(pp, seed, 1, index, &tree.root()).ok_or(LeanXmssSignError::NoAdmissibleEncoding)?;
        let bottom = Arc::new(BottomTree {
            index,
            top_signature: LayerSignature {
                randomizer,
                chain_values,
                path: self.top.path(pp, seed, index),
            },
            tree,
        });
        *self.bottom.lock().unwrap() = Some(Arc::clone(&bottom));
        Ok(bottom)
    }
}

#[derive(Debug, PartialEq, Eq, Clone, Copy, Hash)]
pub enum LeanXmssVerifyError {
    /// A layer's message digest is not a codeword.
    InvalidEncoding,
    /// The top layer does not reach the key's root.
    InvalidMerklePath,
}

impl std::fmt::Display for LeanXmssVerifyError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::InvalidEncoding => write!(f, "a layer's message digest is not a codeword"),
            Self::InvalidMerklePath => write!(f, "the signature does not reach the key's root"),
        }
    }
}

impl std::error::Error for LeanXmssVerifyError {}

pub fn verify(
    pk: &LeanXmssPublicKey,
    message: &Message,
    signature: &LeanXmssSignature,
) -> Result<(), LeanXmssVerifyError> {
    let pp = &pk.public_param;
    let leaf_index = signature.leaf_index;
    let (_, bottom_root) = layer_root(pp, 0, layer_index(leaf_index, 0), message, &signature.bottom, |_| {})
        .ok_or(LeanXmssVerifyError::InvalidEncoding)?;
    let (_, root) = layer_root(pp, 1, layer_index(leaf_index, 1), &bottom_root, &signature.top, |_| {})
        .ok_or(LeanXmssVerifyError::InvalidEncoding)?;
    if root == pk.merkle_root {
        Ok(())
    } else {
        Err(LeanXmssVerifyError::InvalidMerklePath)
    }
}

/// The codeword of a layer's signature by key `e` and the root it reaches, or `None`
/// if its digest is not a codeword. `on_children` sees the two children of each node
/// on the path, in order.
pub fn layer_root(
    pp: &PublicParam,
    layer: usize,
    e: u32,
    message: &[u8],
    signature: &LayerSignature,
    on_children: impl FnMut(&[Digest; 2]),
) -> Option<([u8; V], Digest)> {
    let (codeword, leaf) = wots_leaf(pp, layer, e, message, &signature.randomizer, &signature.chain_values)?;
    let root = crate::path_fold(leaf, e as usize, &signature.path, on_children, |level, j, children| {
        node(pp, layer, level, j as u32, children)
    });
    Some((codeword, root))
}
