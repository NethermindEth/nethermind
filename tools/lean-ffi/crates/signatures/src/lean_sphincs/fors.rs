//! FORS: `k = 19` Merkle trees of `2^a = 512` secret leaves, one leaf opened per
//! tree at the index the message digest picks.
//!
//! An instance is keyed by the hypertree leaf the digest picks (FIPS 205's
//! split): tree address `htIdx >> h'`, key pair `htIdx mod 2^h'`, and the `k`
//! trees folded into one index space, node `j` at height `z` of tree `i` being
//! tree index `(i << (a - z)) | j`.

use super::*;

/// `(d >> (i·a)) & (2^a - 1)` for each tree `i`.
pub fn fors_indices(d: &[u8; 32]) -> [u32; K] {
    std::array::from_fn(|i| digest_bits(d, i * A, A))
}

/// `htIdx = (d >> k·a) & (2^h - 1)`, the hypertree leaf.
pub fn ht_index(d: &[u8; 32]) -> u32 {
    digest_bits(d, K * A, H)
}

/// An address of the FORS instance at hypertree leaf `ht_idx`.
pub fn fors_adrs(ht_idx: u32, typ: u32, height: usize, tree_index: u32) -> Adrs {
    Adrs::new(
        0,
        u64::from(ht_idx >> SUBTREE_H),
        typ,
        ht_idx & ((1 << SUBTREE_H) - 1),
        height as u32,
        tree_index,
    )
}

/// Leaf `leaf` of tree `i`: `F` of its secret at height 0.
pub fn fors_leaf(pp: &PublicParam, ht_idx: u32, i: usize, leaf: u32, secret: &Digest) -> Digest {
    th(
        pp,
        &fors_adrs(ht_idx, FORS_TREE, 0, ((i as u32) << A) | leaf),
        &[*secret],
    )
}

/// A node of tree `i` at height `height >= 1`.
pub fn fors_node(
    pp: &PublicParam,
    ht_idx: u32,
    i: usize,
    height: usize,
    index: u32,
    left: &Digest,
    right: &Digest,
) -> Digest {
    th(
        pp,
        &fors_adrs(ht_idx, FORS_TREE, height, ((i as u32) << (A - height)) | index),
        &[*left, *right],
    )
}

/// The FORS public key: `T_k` of the `k` roots under `FORS_ROOTS`, 672 bytes.
pub fn fors_pk_of_roots(pp: &PublicParam, ht_idx: u32, roots: &[Digest; K]) -> Digest {
    th(pp, &fors_adrs(ht_idx, FORS_ROOTS, 0, 0), roots)
}

/// The FORS key a signature recovers.
pub fn fors_pk_from_sig(
    pp: &PublicParam,
    ht_idx: u32,
    indices: &[u32; K],
    secrets: &[Digest; K],
    paths: &[[Digest; A]; K],
) -> Digest {
    let roots = std::array::from_fn(|i| {
        let leaf = fors_leaf(pp, ht_idx, i, indices[i], &secrets[i]);
        paths[i].iter().enumerate().fold(leaf, |node, (z, sibling)| {
            let (left, right) = if (indices[i] >> z) & 1 == 0 {
                (node, *sibling)
            } else {
                (*sibling, node)
            };
            fors_node(pp, ht_idx, i, z + 1, indices[i] >> (z + 1), &left, &right)
        })
    });
    fors_pk_of_roots(pp, ht_idx, &roots)
}

/// Leaf `leaf` of tree `i`'s secret, `PRF` under `FORS_PRF`.
fn fors_secret(sk_seed: &[u8; N], ht_idx: u32, i: usize, leaf: u32) -> Digest {
    prf(sk_seed, &fors_adrs(ht_idx, FORS_PRF, 0, ((i as u32) << A) | leaf))
}

/// Open the instance at `ht_idx` at `indices`: the secrets, the paths, the key.
pub(crate) fn fors_sign(
    pp: &PublicParam,
    sk_seed: &[u8; N],
    ht_idx: u32,
    indices: &[u32; K],
) -> ([Digest; K], [[Digest; A]; K], Digest) {
    let trees = parallel::map_collect(K, |i| {
        let leaves = (0..1 << A)
            .map(|leaf| fors_leaf(pp, ht_idx, i, leaf, &fors_secret(sk_seed, ht_idx, i, leaf)))
            .collect();
        let levels = merkle_levels(leaves, |height, index, l, r| {
            fors_node(pp, ht_idx, i, height, index, l, r)
        });
        (
            fors_secret(sk_seed, ht_idx, i, indices[i]),
            auth_path::<A>(&levels, indices[i]),
            levels[A][0],
        )
    });
    let secrets = std::array::from_fn(|i| trees[i].0);
    let paths = std::array::from_fn(|i| trees[i].1);
    let roots = std::array::from_fn(|i| trees[i].2);
    (secrets, paths, fors_pk_of_roots(pp, ht_idx, &roots))
}
