//! WOTS+ and the Merkle trees of the hypertree.
//!
//! A layer's WOTS key signs an `n`-byte node directly: its `len1 = 32` base-16
//! digits, then the `len2 = 3` base-16 digits of the checksum `sum(w - 1 - digit)`,
//! all read most significant first. Chain `i` starts at the revealed value, at hash
//! address `digit_i`, and walks the `w - 1 - digit_i` remaining steps.

use super::*;

/// The key pair's base address on a layer: `WOTS_HASH`, chain and hash address 0.
pub fn wots_adrs(layer: u32, tree: u64, kp: u32) -> Adrs {
    Adrs::new(layer, tree, WOTS_HASH, kp, 0, 0)
}

/// The `l` digits a key signs `node` with: its nibbles, the high one of each
/// byte first, then the checksum's, most significant first.
pub fn digits(node: &Digest) -> [u8; L] {
    let mut out = [0; L];
    for (i, digit) in out[..LEN1].iter_mut().enumerate() {
        *digit = (node[i / 2] >> (LOG_W * (1 - i % 2))) & (W - 1) as u8;
    }
    let csum = MAX_CSUM - out[..LEN1].iter().map(|&x| usize::from(x)).sum::<usize>();
    for j in 0..LEN2 {
        out[LEN1 + j] = ((csum >> (LOG_W * (LEN2 - 1 - j))) & (W - 1)) as u8;
    }
    out
}

/// Chain `i` of a key pair walked `steps` steps from `value` at position `start`:
/// step `s` hashes under hash address `start + s`.
pub fn chain(pp: &PublicParam, base: Adrs, i: usize, value: Digest, start: usize, steps: usize) -> Digest {
    (start..start + steps).fold(value, |v, pos| {
        let adrs = Adrs {
            word2: i as u32,
            word3: pos as u32,
            ..base
        };
        th(pp, &adrs, &[v])
    })
}

/// The WOTS public key from the chain tops: `T_l` under `WOTS_PK`, 1184 bytes.
pub fn wots_pk_of_tops(pp: &PublicParam, layer: u32, tree: u64, kp: u32, tops: &[Digest; L]) -> Digest {
    th(pp, &Adrs::new(layer, tree, WOTS_PK, kp, 0, 0), tops)
}

/// The key a signature on `node` recovers.
pub fn wots_pk_from_sig(
    pp: &PublicParam,
    layer: u32,
    tree: u64,
    kp: u32,
    node: &Digest,
    sigma: &[Digest; L],
) -> Digest {
    let digits = digits(node);
    let base = wots_adrs(layer, tree, kp);
    let tops = std::array::from_fn(|i| {
        let d = usize::from(digits[i]);
        chain(pp, base, i, sigma[i], d, W - 1 - d)
    });
    wots_pk_of_tops(pp, layer, tree, kp, &tops)
}

/// Chain `i`'s secret, `PRF` under `WOTS_PRF`.
fn wots_secret(sk_seed: &[u8; N], layer: u32, tree: u64, kp: u32, i: usize) -> Digest {
    prf(sk_seed, &Adrs::new(layer, tree, WOTS_PRF, kp, i as u32, 0))
}

/// A key pair's public key.
pub(crate) fn wots_pk(pp: &PublicParam, sk_seed: &[u8; N], layer: u32, tree: u64, kp: u32) -> Digest {
    let base = wots_adrs(layer, tree, kp);
    let tops = std::array::from_fn(|i| chain(pp, base, i, wots_secret(sk_seed, layer, tree, kp, i), 0, W - 1));
    wots_pk_of_tops(pp, layer, tree, kp, &tops)
}

/// Sign `node`: each chain walked `digit_i` steps from its secret.
pub(crate) fn wots_sign(
    pp: &PublicParam,
    sk_seed: &[u8; N],
    layer: u32,
    tree: u64,
    kp: u32,
    node: &Digest,
) -> [Digest; L] {
    let digits = digits(node);
    let base = wots_adrs(layer, tree, kp);
    std::array::from_fn(|i| {
        chain(
            pp,
            base,
            i,
            wots_secret(sk_seed, layer, tree, kp, i),
            0,
            usize::from(digits[i]),
        )
    })
}

/// A hypertree Merkle node: `H` under `TREE`, height `height`, index `index`.
pub fn tree_node(
    pp: &PublicParam,
    layer: u32,
    tree: u64,
    height: usize,
    index: u32,
    left: &Digest,
    right: &Digest,
) -> Digest {
    th(
        pp,
        &Adrs::new(layer, tree, TREE, 0, height as u32, index),
        &[*left, *right],
    )
}

/// Fold a leaf up its authentication path to the tree's root.
pub fn tree_fold(
    pp: &PublicParam,
    layer: u32,
    tree: u64,
    leaf_idx: u32,
    leaf: Digest,
    path: &[Digest; SUBTREE_H],
) -> Digest {
    path.iter().enumerate().fold(leaf, |node, (h, sibling)| {
        let (left, right) = if (leaf_idx >> h) & 1 == 0 {
            (node, *sibling)
        } else {
            (*sibling, node)
        };
        tree_node(pp, layer, tree, h + 1, leaf_idx >> (h + 1), &left, &right)
    })
}

/// Every level of a complete Merkle tree over `leaves`, bottom first, each
/// parent `node(height, index, left, right)`.
pub(crate) fn merkle_levels(
    leaves: Vec<Digest>,
    node: impl Fn(usize, u32, &Digest, &Digest) -> Digest,
) -> Vec<Vec<Digest>> {
    let mut levels = vec![leaves];
    while levels.last().unwrap().len() > 1 {
        let below = levels.last().unwrap();
        let height = levels.len();
        let level = (0..below.len() / 2)
            .map(|j| node(height, j as u32, &below[2 * j], &below[2 * j + 1]))
            .collect();
        levels.push(level);
    }
    levels
}

/// The siblings of leaf `leaf` in `levels`.
pub(crate) fn auth_path<const HEIGHT: usize>(levels: &[Vec<Digest>], leaf: u32) -> [Digest; HEIGHT] {
    std::array::from_fn(|h| levels[h][((leaf >> h) ^ 1) as usize])
}

/// One hypertree tree, all levels: `2^h'` WOTS keys and the nodes above them.
pub(crate) fn subtree_levels(pp: &PublicParam, sk_seed: &[u8; N], layer: u32, tree: u64) -> Vec<Vec<Digest>> {
    let leaves = parallel::map_collect(1 << SUBTREE_H, |kp| wots_pk(pp, sk_seed, layer, tree, kp as u32));
    merkle_levels(leaves, |height, index, l, r| {
        tree_node(pp, layer, tree, height, index, l, r)
    })
}
