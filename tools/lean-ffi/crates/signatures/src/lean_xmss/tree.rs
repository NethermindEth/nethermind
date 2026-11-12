//! One layer's Merkle tree, over the leaves `first..=last` of its `2^16`. A full
//! key's trees hold every leaf; a mock key's hold a range, every node outside it a
//! pseudorandom filler.

use crate::lean_xmss::*;

/// The Merkle parent at `level` and index `j` of `layer`.
pub fn node(pp: &PublicParam, layer: usize, level: usize, j: u32, children: &[Digest; 2]) -> Digest {
    th_digests(pp, &tweak(TWEAK_NODE, layer_hi(layer, level as u32), j), children)
}

fn filler(pp: &PublicParam, seed: &SchemeSeed, layer: usize, level: usize, j: u32) -> Digest {
    th(
        pp,
        &tweak(TWEAK_FILLER, layer_hi(layer, level as u32), j),
        seed.as_bytes(),
    )
}

pub(crate) struct LayerTree {
    layer: usize,
    /// The index of leaf 0 on its layer: the bottom tree's first leaf index, or 0.
    base: u32,
    first: u32,
    last: u32,
    /// Level `l` holds the nodes `first >> l ..= last >> l`.
    levels: Vec<Vec<Digest>>,
}

impl LayerTree {
    pub(crate) fn build(pp: &PublicParam, seed: &SchemeSeed, layer: usize, base: u32, first: u32, last: u32) -> Self {
        debug_assert!(first <= last && last >> LAYER_HEIGHT == 0);
        let leaves = parallel::map_collect((last - first + 1) as usize, |j| {
            wots_public_leaf(pp, seed, layer, base + first + j as u32)
        });
        let mut tree = Self {
            layer,
            base,
            first,
            last,
            levels: vec![leaves],
        };
        for level in 1..=LAYER_HEIGHT {
            let start = first >> level;
            let up = parallel::map_collect(((last >> level) - start + 1) as usize, |k| {
                let j = start + k as u32;
                let children = [
                    tree.at(pp, seed, level - 1, 2 * j),
                    tree.at(pp, seed, level - 1, 2 * j + 1),
                ];
                node(pp, layer, level, (base >> level) + j, &children)
            });
            tree.levels.push(up);
        }
        tree
    }

    /// Node `j` of `level`, stored if it is over a leaf in range, else a filler.
    fn at(&self, pp: &PublicParam, seed: &SchemeSeed, level: usize, j: u32) -> Digest {
        if (self.first >> level..=self.last >> level).contains(&j) {
            self.levels[level][(j - (self.first >> level)) as usize]
        } else {
            filler(pp, seed, self.layer, level, (self.base >> level) + j)
        }
    }

    pub(crate) fn root(&self) -> Digest {
        self.levels[LAYER_HEIGHT][0]
    }

    pub(crate) fn path(&self, pp: &PublicParam, seed: &SchemeSeed, leaf: u32) -> [Digest; LAYER_HEIGHT] {
        debug_assert!((self.first..=self.last).contains(&leaf));
        std::array::from_fn(|level| self.at(pp, seed, level, (leaf >> level) ^ 1))
    }
}
