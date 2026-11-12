//! The hypertree and the three algorithms: `d = 5` layers of height-4 Merkle
//! trees over WOTS+ keys, layer 0 signing the FORS key the message digest
//! picks, the top layer's single root being the public key.
//!
//! The digest `H_msg` names a hypertree leaf `htIdx` and the FORS indices, so a
//! key answers for all `2^h` leaves with nothing reserved or spent: the scheme
//! is stateless.


use super::*;

/// `(pkRoot, pkSeed)`. Ordered lexicographically on [`Self::flatten`], which is
/// how an aggregate sorts the claims on one message. The on-chain form is two
/// `bytes32`, each value top-aligned (`v ‖ 0^16`).
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub struct LeanSphincsPublicKey {
    pub root: Digest,
    pub public_param: PublicParam,
}

impl LeanSphincsPublicKey {
    pub fn pk_seed(&self) -> &PkSeed {
        &self.public_param
    }

    pub fn root(&self) -> &Digest {
        &self.root
    }

    /// `pkSeed | pkRoot`, upstream's claim order.
    pub fn to_bytes(&self) -> [u8; PUB_KEY_SIZE] {
        let mut out = [0; PUB_KEY_SIZE];
        out[..N].copy_from_slice(&self.public_param);
        out[N..].copy_from_slice(&self.root);
        out
    }

    pub fn from_bytes(bytes: &[u8; PUB_KEY_SIZE]) -> Self {
        Self {
            public_param: bytes[..N].try_into().unwrap(),
            root: bytes[N..].try_into().unwrap(),
        }
    }

    /// The verifier's `(pkSeed, pkRoot)` arguments. Always canonical (low 128
    /// bits zero), which the EVM verifier requires.
    pub fn to_bytes32(&self) -> ([u8; 32], [u8; 32]) {
        (word(&self.public_param), word(&self.root))
    }
}

/// A secret key: its 32-byte master secret and nothing else. FIPS 205's three
/// seeds and the public key are derived from it (see [`key_gen_from_seed`]).
#[derive(Clone)]
pub struct LeanSphincsSecretKey {
    master: MasterSecret,
}

impl std::fmt::Debug for LeanSphincsSecretKey {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("LeanSphincsSecretKey").finish_non_exhaustive()
    }
}

impl LeanSphincsSecretKey {
    /// SECRET KEY MATERIAL: the master secret.
    pub fn to_bytes(&self) -> [u8; SECRET_KEY_SIZE] {
        self.master
    }

    /// Inverse of [`Self::to_bytes`].
    pub fn from_bytes(bytes: &[u8; SECRET_KEY_SIZE]) -> Self {
        Self { master: *bytes }
    }

    /// The public key, derived: the root is the top layer's tree, `2^h'` WOTS
    /// keys, so this costs about a fifth of a signature.
    pub fn public_key(&self) -> LeanSphincsPublicKey {
        let (sk_seed, _, public_param) = derive_seeds(&self.master);
        LeanSphincsPublicKey {
            root: root_of(&public_param, &sk_seed),
            public_param,
        }
    }
}

/// One hypertree layer of a signature.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct HtLayer {
    /// `σ`: chain `i` at position `digit_i`.
    pub chains: [Digest; L],
    /// The Merkle authentication path, bottom first.
    pub path: [Digest; SUBTREE_H],
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct LeanSphincsSignature {
    /// `R`.
    pub randomizer: Randomizer,
    /// One opened leaf secret per FORS tree.
    pub fors_secrets: [Digest; K],
    /// Each tree's authentication path, bottom first.
    pub fors_paths: [[Digest; A]; K],
    /// Layer 0 first.
    pub layers: [HtLayer; D],
}

impl LeanSphincsSignature {
    /// The verifier's blob, exactly [`SIG_SIZE`] bytes: `R ‖ k × [secret ‖ a path
    /// nodes] ‖ d × [l chains ‖ h' path nodes]`.
    pub fn to_bytes(&self) -> [u8; SIG_SIZE] {
        let mut out = [0; SIG_SIZE];
        let mut at = 0;
        let mut put = |bytes: &[u8]| {
            out[at..at + bytes.len()].copy_from_slice(bytes);
            at += bytes.len();
        };
        put(&self.randomizer);
        for (secret, path) in self.fors_secrets.iter().zip(&self.fors_paths) {
            put(secret);
            path.iter().for_each(|s| put(s));
        }
        for layer in &self.layers {
            layer.chains.iter().for_each(|s| put(s));
            layer.path.iter().for_each(|s| put(s));
        }
        debug_assert_eq!(at, SIG_SIZE);
        out
    }

    pub fn from_bytes(bytes: &[u8; SIG_SIZE]) -> Self {
        let at = std::cell::Cell::new(0);
        let take = |len: usize| {
            let s = &bytes[at.get()..at.get() + len];
            at.set(at.get() + len);
            s
        };
        let digest = || -> Digest { take(N).try_into().unwrap() };
        let randomizer = digest();
        let trees: [(Digest, [Digest; A]); K] = std::array::from_fn(|_| (digest(), std::array::from_fn(|_| digest())));
        let fors_secrets = trees.map(|(secret, _)| secret);
        let fors_paths = trees.map(|(_, path)| path);
        let layers = std::array::from_fn(|_| {
            let chains = std::array::from_fn(|_| digest());
            let path = std::array::from_fn(|_| digest());
            HtLayer { chains, path }
        });
        debug_assert_eq!(at.get(), SIG_SIZE);
        Self {
            randomizer,
            fors_secrets,
            fors_paths,
            layers,
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum LeanSphincsVerifyError {
    /// The hypertree walk does not reach the key's root: the only way a
    /// well-formed signature fails, there being no grinding predicate to check.
    RootMismatch,
}

impl std::fmt::Display for LeanSphincsVerifyError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::RootMismatch => write!(f, "the hypertree walk does not reach the key's root"),
        }
    }
}

impl std::error::Error for LeanSphincsVerifyError {}

/// `(tree, leaf)` on layer `layer` of hypertree leaf `ht_idx`.
pub fn ht_position(ht_idx: u32, layer: usize) -> (u64, u32) {
    let below = ht_idx >> (layer * SUBTREE_H);
    (u64::from(below >> SUBTREE_H), below & ((1 << SUBTREE_H) - 1))
}

/// `Gen` on a fresh key: the master secret comes from `rng`.
pub fn key_gen() -> (LeanSphincsSecretKey, LeanSphincsPublicKey) {
    key_gen_from_seed(rand::random())
}

/// `SK.seed`, `SK.prf` and `PK.seed` from a master secret: each
/// `hash(tag ‖ master)[..16]`, `tag` one of `"SPHINCS-v2 SK.seed"`,
/// `"SPHINCS-v2 SK.prf"`, `"SPHINCS-v2 PK.seed"`: three hash domains, so the
/// seeds are independent. (Signer-private: no verifier sees the derivation.)
fn derive_seeds(master: &MasterSecret) -> ([u8; N], [u8; N], PublicParam) {
    let seed = |tag: &[u8]| truncate(&primitives::hash::hash(&[tag, master].concat()));
    (
        seed(b"SPHINCS-v2 SK.seed"),
        seed(b"SPHINCS-v2 SK.prf"),
        seed(b"SPHINCS-v2 PK.seed"),
    )
}

/// Deterministic `Gen` on the master secret `master`. The three seeds are each
/// `hash(tag ‖ master)[..16]`, `tag` one of `"SPHINCS-v2 SK.seed"`,
/// `"SPHINCS-v2 SK.prf"`, `"SPHINCS-v2 PK.seed"`.
pub fn key_gen_from_seed(master: MasterSecret) -> (LeanSphincsSecretKey, LeanSphincsPublicKey) {
    let sk = LeanSphincsSecretKey { master };
    let pk = sk.public_key();
    (sk, pk)
}

/// The public root: the top layer's single tree.
fn root_of(public_param: &PublicParam, sk_seed: &[u8; N]) -> Digest {
    subtree_levels(public_param, sk_seed, (D - 1) as u32, 0)[SUBTREE_H][0]
}

/// Sign. Deterministic and stateless.
///
/// The key's root is rebuilt first ([`LeanSphincsSecretKey::public_key`]), since
/// the message digest depends on it.
pub fn sign(sk: &LeanSphincsSecretKey, message: &Message) -> Result<LeanSphincsSignature, LeanSphincsSignError> {
    let (sk_seed, sk_prf, public_param) = derive_seeds(&sk.master);
    Ok(sign_with_seeds(sk_seed, sk_prf, public_param, message).1)
}

/// Signing cannot fail (no grinding); kept for upstream's API.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum LeanSphincsSignError {}

impl std::fmt::Display for LeanSphincsSignError {
    fn fmt(&self, _: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match *self {}
    }
}

impl std::error::Error for LeanSphincsSignError {}

#[cfg(test)]
mod tests {
    use super::*;

    fn field(json: &str, key: &str) -> Vec<u8> {
        let at = json.find(&format!("\"{key}\"")).unwrap();
        let rest = &json[at + key.len() + 2..];
        let open = rest.find('"').unwrap() + 1;
        let close = open + rest[open..].find('"').unwrap();
        let hex = rest[open..close].trim_start_matches("0x");
        (0..hex.len() / 2)
            .map(|i| u8::from_str_radix(&hex[2 * i..2 * i + 2], 16).unwrap())
            .collect()
    }

    /// NiceTry's reference vector verifies, and its seeds reproduce key and signature byte for byte.
    #[test]
    fn matches_the_reference_vector() {
        let json = include_str!("sphincs-v2-reference-0.json");
        let (pk_seed, pk_root) = (field(json, "pkSeed"), field(json, "pkRoot"));
        let pk = LeanSphincsPublicKey {
            root: pk_root[..N].try_into().unwrap(),
            public_param: pk_seed[..N].try_into().unwrap(),
        };
        let message: Message = field(json, "message").try_into().unwrap();
        let blob = field(json, "signature");
        assert_eq!(blob.len(), SIG_SIZE);
        let reference = LeanSphincsSignature::from_bytes(blob.as_slice().try_into().unwrap());
        assert_eq!(reference.to_bytes().to_vec(), blob);
        verify(&pk, &message, &reference).unwrap();
        let mut other = message;
        other[31] ^= 1;
        assert_eq!(verify(&pk, &other, &reference), Err(LeanSphincsVerifyError::RootMismatch));
        let mut bad = blob.clone();
        bad[100] ^= 1;
        let bad = LeanSphincsSignature::from_bytes(bad.as_slice().try_into().unwrap());
        assert_eq!(verify(&pk, &message, &bad), Err(LeanSphincsVerifyError::RootMismatch));
        let (derived, signature) = sign_with_seeds([0x11; N], [0x22; N], pk.public_param, &message);
        assert_eq!(derived, pk);
        assert_eq!(signature, reference);
    }
}

/// Key generation and signing on the three seeds given directly, as FIPS 205
/// and NiceTry's reference signer (`scripts/sphincs_v2_reference.py`) state them.
/// For reproducing reference vectors: a key made this way has no master secret,
/// so use [`key_gen`] / [`key_gen_from_seed`] for real keys.
pub fn sign_with_seeds(
    sk_seed: [u8; N],
    sk_prf: [u8; N],
    public_param: PublicParam,
    message: &Message,
) -> (LeanSphincsPublicKey, LeanSphincsSignature) {
    let root = root_of(&public_param, &sk_seed);
    let signature = sign_with(&public_param, &root, &sk_seed, &sk_prf, message);
    (LeanSphincsPublicKey { root, public_param }, signature)
}

fn sign_with(
    pp: &PublicParam,
    root: &Digest,
    sk_seed: &[u8; N],
    sk_prf: &[u8; N],
    message: &Message,
) -> LeanSphincsSignature {
    let randomizer = randomizer(sk_prf, message);
    let d = h_msg(pp, root, &randomizer, message);
    let ht_idx = ht_index(&d);
    let (fors_secrets, fors_paths, fors_pk) = fors_sign(pp, sk_seed, ht_idx, &fors_indices(&d));

    let mut node = fors_pk;
    let layers = std::array::from_fn(|layer| {
        let (tree, leaf) = ht_position(ht_idx, layer);
        let chains = wots_sign(pp, sk_seed, layer as u32, tree, leaf, &node);
        let levels = subtree_levels(pp, sk_seed, layer as u32, tree);
        node = levels[SUBTREE_H][0];
        HtLayer {
            chains,
            path: auth_path(&levels, leaf),
        }
    });
    debug_assert_eq!(&node, root);

    LeanSphincsSignature {
        randomizer,
        fors_secrets,
        fors_paths,
        layers,
    }
}

/// Everything a verification computes on the way to the root, for provers that
/// replay it: the digest, what it selects, and what each layer signs.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct VerifyTrace {
    pub digest: [u8; 32],
    pub ht_idx: u32,
    pub fors_indices: [u32; K],
    /// `signed[layer]`: the node layer `layer`'s WOTS key signs (the FORS key on
    /// layer 0); `signed[d]` is the root reached.
    pub signed: [Digest; D + 1],
    /// Each layer's WOTS digits, the checksum's last.
    pub digits: [[u8; L]; D],
}

/// Replay a verification up to the root comparison.
pub fn verify_trace(pk: &LeanSphincsPublicKey, message: &Message, signature: &LeanSphincsSignature) -> VerifyTrace {
    let pp = &pk.public_param;
    let digest = h_msg(pp, &pk.root, &signature.randomizer, message);
    let indices = fors_indices(&digest);
    let ht_idx = ht_index(&digest);
    let mut signed = [[0; N]; D + 1];
    let mut digits_of = [[0; L]; D];
    signed[0] = fors_pk_from_sig(pp, ht_idx, &indices, &signature.fors_secrets, &signature.fors_paths);
    for (layer, sig) in signature.layers.iter().enumerate() {
        let (tree, leaf) = ht_position(ht_idx, layer);
        digits_of[layer] = digits(&signed[layer]);
        let wots_pk = wots_pk_from_sig(pp, layer as u32, tree, leaf, &signed[layer], &sig.chains);
        signed[layer + 1] = tree_fold(pp, layer as u32, tree, leaf, wots_pk, &sig.path);
    }
    VerifyTrace {
        digest,
        ht_idx,
        fors_indices: indices,
        signed,
        digits: digits_of,
    }
}

/// `Ver`, `SphincsVerifier_v2.verify` on the signature's blob.
pub fn verify(
    pk: &LeanSphincsPublicKey,
    message: &Message,
    signature: &LeanSphincsSignature,
) -> Result<(), LeanSphincsVerifyError> {
    if verify_trace(pk, message, signature).signed[D] == pk.root {
        Ok(())
    } else {
        Err(LeanSphincsVerifyError::RootMismatch)
    }
}
