//! The guest's claim pool and its coordinate terms.

use super::cells::{count, dsl_u128};
use leanvm_core::cpu::keccak_value;
use leanvm_core::leaf::{Block, Coord};
use primitives::field::F192;
use std::collections::BTreeMap;

/// Why the guest reads every `q_keccak` slot claim's instance point off `chi`: a
/// virtual value column is referenced only by its own table's bus blocks, which
/// the table sumcheck settles, so no framework block can raise one at `zeta`.
const VALCOL_FRAMEWORK: &str = "a framework block must not reference a virtual value column";

/// One entry of the guest's claim pool.
pub(super) enum ClaimSite {
    /// A committed column read by a framework bus block of kappa source `source`.
    Framework { column: usize, source: (usize, usize) },
    /// A table column, a KECCAK value lane's ([`keccak_value`]) included.
    TableColumn { column: usize, table: usize },
    /// One of the three PI memory limbs (MEM_LO, MEM_HI, MEM_TOP).
    MemoryLimb { column: usize },
}

/// The guest's `COORD_KIND_*` code for a coordinate (`guests/lean_ethereum.py`),
/// shared by its `COORD_TYPE` and `TERM_TYPE` arrays.
pub(super) fn coord_kind(c: &Coord) -> usize {
    match c {
        Coord::Const(_) => 0,
        Coord::Col(_) => 1,
        Coord::GCol(..) => 2,
        Coord::Index => 3,
        Coord::Public(_) => 4,
        Coord::Prod(..) => 5,
        Coord::Sum(..) => 6,
    }
}

/// The `K` scalar a coordinate carries beside its columns: the constant itself,
/// or the `g^k` a `GCol`/`Prod` scales by. Zero for every other kind.
pub(super) fn coord_scale(c: &Coord) -> F192 {
    match c {
        Coord::Const(v) => F192::from(*v),
        Coord::GCol(_, k) | Coord::Prod(_, _, k) => count(*k as usize),
        _ => F192::ZERO,
    }
}

/// Flatten one table-block coordinate into the guest's term arrays, in local
/// column indices. A [`Coord::Sum`]'s children are its terms; every other kind is
/// one term. `Index`/`Public` never reach a table block.
pub(super) fn push_coord_terms(c: &Coord, base: usize, terms: &mut Vec<Term>) {
    let (column_a, column_b) = match c {
        Coord::Const(_) => (0, 0),
        Coord::Col(i) | Coord::GCol(i, _) => (*i - base, 0),
        Coord::Prod(i, j, _) => (*i - base, *j - base),
        Coord::Sum(cs) => {
            for c in cs {
                push_coord_terms(c, base, terms);
            }
            return;
        }
        Coord::Index | Coord::Public(_) => unreachable!("a table's bus block carries no virtual coordinate"),
    };
    terms.push(Term {
        kind: coord_kind(c),
        constant: dsl_u128(coord_scale(c)),
        column_a,
        column_b,
    });
}

/// The terms of `a + b` (characteristic 2), merged: constants summed, the scales of
/// one column or one column product summed, and whatever sums to zero dropped.
pub(super) fn coord_difference(a: &[Term], b: &[Term]) -> Vec<Term> {
    let (konst, col, gcol) = (0, 1, 2); // coord_kind of Const, Col, GCol
    let one = dsl_u128(F192::ONE);
    let mut constant = 0u128;
    let mut scaled: BTreeMap<(usize, usize, usize), u128> = BTreeMap::new();
    for t in a.iter().chain(b) {
        match t.kind {
            k if k == konst => constant ^= t.constant,
            k if k == col => *scaled.entry((gcol, t.column_a, 0)).or_default() ^= one,
            k if k == gcol => *scaled.entry((gcol, t.column_a, 0)).or_default() ^= t.constant,
            k => {
                let (x, y) = (t.column_a.min(t.column_b), t.column_a.max(t.column_b));
                *scaled.entry((k, x, y)).or_default() ^= t.constant;
            }
        }
    }
    let mut out = Vec::new();
    if constant != 0 {
        out.push(Term {
            kind: konst,
            constant,
            column_a: 0,
            column_b: 0,
        });
    }
    for ((kind, column_a, column_b), constant) in scaled {
        if constant != 0 {
            let kind = if kind == gcol && constant == one { col } else { kind };
            out.push(Term {
                kind,
                constant,
                column_a,
                column_b,
            });
        }
    }
    out
}

/// Visit the claim pool in the exact order the guest indexes it: the framework
/// bus claims (deduped by `(column, kappa)`, as `leaf.rs` pools them), then every
/// table's committed columns, then the PI memory triple. The placeholder map's
/// claim descriptors follow this order.
pub(super) fn walk_claims(layout: &leanvm_core::cpu::Layout, kbc: usize, mut visit: impl FnMut(ClaimSite)) {
    let sides: [&[Block]; 3] = [&layout.push, &layout.pull, &layout.count];
    // Only the framework blocks raise claims: a table's coords are settled inside
    // the table sumcheck.
    let sources = leanvm_core::cpu::block_kappa_sources(kbc);
    let mut seen: std::collections::HashSet<(usize, usize)> = Default::default();
    let mut bi = 0usize;
    for blocks in sides.iter() {
        for blk in blocks.iter() {
            let source = sources[bi];
            bi += 1;
            if source.0 >= 2 {
                continue;
            }
            for c in &blk.coords {
                if let Coord::Col(i) | Coord::GCol(i, _) = c {
                    if !seen.insert((*i, blk.kappa)) {
                        continue; // deduped: pooled once at its first occurrence
                    }
                    assert!(keccak_value(*i).is_none(), "{VALCOL_FRAMEWORK}");
                    visit(ClaimSite::Framework { column: *i, source });
                }
            }
        }
    }
    let sch = leanvm_core::cpu::schema();
    for (t, table) in leanvm_core::tables::tables().iter().enumerate() {
        for c in 0..table.n_committed_columns() {
            visit(ClaimSite::TableColumn {
                column: sch.base[t] + c,
                table: t,
            });
        }
    }
    for &column in &[
        leanvm_core::cpu::MEM_LO,
        leanvm_core::cpu::MEM_HI,
        leanvm_core::cpu::MEM_TOP,
    ] {
        visit(ClaimSite::MemoryLimb { column });
    }
}

pub(super) struct Term {
    pub(super) kind: usize,
    pub(super) constant: u128,
    pub(super) column_a: usize,
    pub(super) column_b: usize,
}
