//! The hash-based signature schemes leanVM aggregates: leanXMSS, stateful, and
//! leanSPHINCS, stateless. Both sign with WOTS+C, so the recursion guest verifies
//! both with one walk; leanXMSS hashes with its own tweakable hash, and leanSPHINCS
//! with FIPS 205's.

#![cfg_attr(not(test), warn(unused_crate_dependencies))]

pub mod lean_sphincs;
pub mod lean_xmss;

use primitives::hash::Hasher;

/// The length both schemes cut a hash value to.
const N: usize = 16;

/// Both schemes' tweakable hash is `Truncate_16(H(prefix | address | payload))`,
/// the prefix being the public parameter and the address each scheme's own: a
/// hasher that has absorbed `prefix | address`.
fn hasher(prefix: &[u8; N], address: &[u8]) -> Hasher {
    let mut hasher = Hasher::new();
    hasher.update(prefix).update(address);
    hasher
}

/// The root that `leaf`, at index `e`, and its authentication `path` reach, showing `visit` the two
/// children of every node on the way up, `node(z, j, children)` hashing node `j` at height `z`.
fn path_fold(
    leaf: [u8; N],
    e: usize,
    path: &[[u8; N]],
    mut visit: impl FnMut(&[[u8; N]; 2]),
    node: impl Fn(usize, usize, &[[u8; N]; 2]) -> [u8; N],
) -> [u8; N] {
    path.iter().enumerate().fold(leaf, |current, (z, &sibling)| {
        // Bit `z` of `e` says whether the node is the right child.
        let children = if (e >> z) & 1 == 0 {
            [current, sibling]
        } else {
            [sibling, current]
        };
        visit(&children);
        node(z + 1, e >> (z + 1), &children)
    })
}

/// `Truncate_16`: a hash value from a Keccak-256 output.
fn truncate(output: [u8; 32]) -> [u8; N] {
    output[..N].try_into().unwrap()
}
