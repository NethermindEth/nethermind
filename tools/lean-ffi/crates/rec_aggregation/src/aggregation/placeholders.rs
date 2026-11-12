//! The constants baked into the guest.

use super::MAX_RECURSIONS;
use super::cells::{dsl_u128, pack_16_bytes};
use super::child::{
    MU_CAP, QUERY_WINDOW_BITS, QUERY_WINDOWS_CAP, STREAM_CAP, block_groups, col_groups, merkle_cap_depth, whir_shape,
};
use super::claim_pool::{ClaimSite, coord_difference, coord_kind, coord_scale, push_coord_terms, walk_claims};
use super::deferred::RECURSION_AGG_LABEL;
use super::hints::{DIGIT_GROUP, LX_DIGIT_GROUP, LX_WINDOW};
use super::statement::{BIG_GROUPS, CLAIM_GROUP, STATEMENT_HEADER};
use fiat_shamir::merkle::hash_to_scalars;
use lean_compiler::f192_literal;
use lean_da::{CELLS_PER_ROW, DA_LOG_CELL, DA_LOG_K, DA_MAX_ROWS};
use leanvm_core::constraints::xi_offsets;
use leanvm_core::cpu::{KeccakLane, keccak_value};
use leanvm_core::leaf::{Block, Coord};
use leanvm_core::tables::N_TABLES;
use leanvm_core::transcript::{FiatShamirState, RATE_SCALARS};
use pcs::whir::{MAX_LOG_INV_RATE, MIN_LOG_INV_RATE};
use primitives::field::{F64, F192, g_pow};
use signatures::{lean_sphincs, lean_xmss};
use std::collections::BTreeMap;
use std::ops::Range;

struct CoordinateDescriptor {
    kind: usize,
    constant: u128,
    /// Its value on its table's padding row; zero for a framework coordinate.
    padding: u64,
    fresh: usize,
    claim_slot: usize,
    terms: Range<usize>,
}

struct ClaimDescriptor {
    buffer: usize,
    column: usize,
    qkeccak_slot: usize,
    /// The kappa source `(src, adj)` of the claim's low dimension.
    source: (usize, usize),
}

// The guest's `POINT_BUF_*`, the point a pooled claim is made at: the bus's `zeta`, the table
// sumcheck's point, the public input's, that point on a slot of `q_keccak`, and none for an
// output lane's, which the Keccak reduction takes as its claim on the permutation's output and
// the opening never sees.
const POINT_BUF_ZETA: usize = 0;
const POINT_BUF_RHO: usize = 1;
const POINT_BUF_PI: usize = 2;
const POINT_BUF_QKECCAK_RHO: usize = 3;
const POINT_BUF_KECCAK_OUT: usize = 4;

fn literals(values: impl IntoIterator<Item = impl std::fmt::Display>) -> String {
    format!(
        "[{}]",
        values.into_iter().map(|v| v.to_string()).collect::<Vec<_>>().join(", ")
    )
}

fn flds(v: &[F192]) -> String {
    format!(
        "[{}]",
        v.iter().map(|&x| f192_literal(x)).collect::<Vec<_>>().join(", ")
    )
}

/// A greedy (Paar) straight-line program computing each row's sum, or product where a row repeats an input:
/// value `inputs + k` is op `k`, the sum or product of two earlier values, the pair most rows share first.
/// Returns the ops and the value holding each (nonempty) row.
fn straight_line(rows: &[Vec<usize>], inputs: usize) -> (Vec<(usize, usize)>, Vec<usize>) {
    let mut rows: Vec<Vec<usize>> = rows.to_vec();
    let mut ops = Vec::new();
    let pairs = |row: &Vec<usize>| {
        let mut out = std::collections::BTreeSet::new();
        for (i, &a) in row.iter().enumerate() {
            for &b in &row[i + 1..] {
                out.insert((a.min(b), a.max(b)));
            }
        }
        out
    };
    loop {
        let mut counts = std::collections::BTreeMap::<(usize, usize), usize>::new();
        for row in &rows {
            for pair in pairs(row) {
                *counts.entry(pair).or_default() += 1;
            }
        }
        // The most shared pair, the first of them; once none is shared, any pair left.
        let Some((&(a, b), _)) = counts.iter().rev().max_by_key(|&(_, &n)| n) else {
            break;
        };
        let new = inputs + ops.len();
        ops.push((a, b));
        for row in &mut rows {
            if let Some(i) = row.iter().position(|&v| v == a) {
                row.remove(i);
                if let Some(j) = row.iter().position(|&v| v == b) {
                    row.remove(j);
                    row.push(new);
                } else {
                    row.insert(i, a);
                }
            }
        }
    }
    let outputs = rows
        .iter()
        .map(|row| {
            assert_eq!(row.len(), 1, "a straight-line row is nonempty");
            row[0]
        })
        .collect();
    (ops, outputs)
}

/// Binary polynomials: the quotient and the remainder of `a` by `m`.
fn poly_divmod(mut a: u64, m: u64) -> (u64, u64) {
    let dm = m.ilog2();
    let mut q = 0;
    while a != 0 && a.ilog2() >= dm {
        let shift = a.ilog2() - dm;
        q |= 1 << shift;
        a ^= m << shift;
    }
    (q, a)
}

/// `p`'s irreducible factors, with multiplicity, smallest first.
fn poly_factors(mut p: u64) -> Vec<u64> {
    let mut factors = Vec::new();
    let mut q = 2u64;
    while p > 1 && 64 - q.leading_zeros() <= (64 - p.leading_zeros()) {
        let (quotient, rem) = poly_divmod(p, q);
        if rem == 0 {
            factors.push(q);
            p = quotient;
        } else {
            q += 1;
        }
    }
    factors
}

struct OpeningShape {
    n_levels: usize,
    yr_level: usize,
    yr_log_len: usize,
    folds: Vec<usize>,
    log_message_columns: Vec<usize>,
    queries: Vec<usize>,
    tree_depths: Vec<usize>,
    positions_per_squeeze: Vec<usize>,
    squeezes: Vec<usize>,
    interleaving: Vec<usize>,
    query_grinding_bits: Vec<usize>,
    cap_depths: Vec<usize>,
    fold_offsets: Vec<usize>,
    residual_fold_offsets: Vec<usize>,
    ood_samples: Vec<usize>,
}

/// The recursion program's placeholder map depends on table structure and bytecode
/// size, so one compiled guest serves every proof layout.
pub(super) fn placeholder_map(kbc: usize) -> BTreeMap<String, String> {
    // Only block and coordinate structure is used here; dummy instructions and
    // table sizes let us derive it before the guest's bytecode exists.
    let stand_in = vec![leanvm_core::cpu::Op::Xor { a: 0, b: 0, c: 0 }; 1 << kbc];
    let layout = leanvm_core::cpu::layout(&stand_in, 20, [1 << 10; N_TABLES], [F192::ZERO, F192::ZERO]);

    let mut rep = BTreeMap::new();
    let mut ps = |k: &str, v: String| {
        rep.insert(format!("{k}_PLACEHOLDER"), v);
    };
    ps("STREAM_CAP", STREAM_CAP.to_string());
    ps("QW", QUERY_WINDOW_BITS.to_string());
    ps("QUERY_WINDOWS_CAP", QUERY_WINDOWS_CAP.to_string());
    ps("MIN_LOG_MEM", verifier::MIN_LOG_MEMORY.to_string());
    ps("MAX_LOG_MEM", verifier::MAX_LOG_MEMORY.to_string());
    ps("INV_GEN", dsl_u128(F192::from(F64::G.inv())).to_string());
    ps("MU_CAP", MU_CAP.to_string());
    ps("NO_TABLE", N_TABLES.to_string());
    ps("GKR_ROUNDS_CAP", (MU_CAP * (MU_CAP + 1) / 2 + MU_CAP + 2).to_string());
    ps("GKR_POINTS_CAP", ((MU_CAP + 1) * MU_CAP).to_string());
    let (nclaims, nbcv) = bus_placeholders(&layout, kbc, &mut ps);
    claim_placeholders(&layout, kbc, nclaims, &mut ps);
    table_placeholders(&mut ps);
    keccak_placeholders(&mut ps);
    field_placeholders(&mut ps);
    opening_placeholders(kbc, &mut ps);
    bytecode_placeholders(kbc, nbcv, &mut ps);
    da_placeholders(&mut ps);
    signature_placeholders(&mut ps);
    rep
}

/// The bus blocks and their coordinates and terms. Returns the bus claim count and the public coordinates' count.
fn bus_placeholders(
    layout: &leanvm_core::cpu::Layout,
    kbc: usize,
    ps: &mut impl FnMut(&str, String),
) -> (usize, usize) {
    let sides: [&[Block]; 3] = [&layout.push, &layout.pull, &layout.count];

    // ---- flattened block/coord descriptors (structural) ----
    let mut sblk = vec![0usize];
    let mut block_coords = Vec::new();
    let mut coordinates = Vec::new();
    let mut terms = Vec::new();
    let (mut nclaims, mut nbcv, mut nblocks) = (0usize, 0usize, 0usize);
    // Claim dedup (mirrors leaf.rs): per coord, fresh = first (group, col,
    // kappa) occurrence gets the next pool slot; duplicates point at it.
    let mut slot_of: std::collections::HashMap<(usize, usize), usize> = Default::default();
    // A TABLE block's coordinates, flattened into terms: the guest rebuilds each as
    // `Σ_terms`, so a derived value (an XOR/MUL result, a DEREF store, a JUMP
    // successor) costs terms rather than columns. A framework coordinate has none:
    // it decomposes into pooled claims instead.
    // The table sumcheck settles table claims; only framework blocks stream column values.
    let sch_pm = leanvm_core::cpu::schema();
    let owner_pm: Vec<Option<usize>> = leanvm_core::cpu::block_kappa_sources(kbc)
        .into_iter()
        .map(|(src, _)| src.checked_sub(2))
        .collect();
    for blocks in sides.iter() {
        for blk in blocks.iter() {
            block_coords.push(coordinates.len()..coordinates.len() + blk.coords.len());
            let owner = owner_pm[nblocks];
            nblocks += 1;
            for (i, c) in blk.coords.iter().enumerate() {
                // One COORD_FRESH/COORD_CLAIM_SLOT entry PER coord (the guest
                // indexes them by global coord offset); only a framework block's
                // Col/GCol raises a claim.
                let (mut fresh, mut slot) = (0usize, 0usize);
                if let (Coord::Col(i) | Coord::GCol(i, _), None) = (c, owner) {
                    let key = (*i, blk.kappa);
                    if let Some(&known) = slot_of.get(&key) {
                        slot = known;
                    } else {
                        slot_of.insert(key, nclaims);
                        fresh = 1;
                        slot = nclaims;
                        nclaims += 1;
                    }
                }
                let start = terms.len();
                if let Some(t) = owner {
                    push_coord_terms(c, sch_pm.base[t], &mut terms);
                }
                nbcv += usize::from(matches!(c, Coord::Public(_)));
                coordinates.push(CoordinateDescriptor {
                    kind: coord_kind(c),
                    constant: dsl_u128(coord_scale(c)),
                    padding: blk.padding.get(i).map_or(0, |v| v.0),
                    fresh,
                    claim_slot: slot,
                    terms: start..terms.len(),
                });
            }
        }
        sblk.push(nblocks);
    }
    ps("SIDE_BLOCK_START", literals(&sblk));
    ps("N_BLOCKS", nblocks.to_string());
    let bks = leanvm_core::cpu::block_kappa_sources(kbc);
    // Push and pull emit bus blocks in matched pairs, so their baked kappa-source
    // segments are identical; the guest computes only push's side total and
    // aliases pull's mu to push's on this basis.
    assert_eq!(
        bks[sblk[0]..sblk[1]],
        bks[sblk[1]..sblk[2]],
        "push/pull kappa sources must match"
    );
    // The guest also reuses push's padding term for pull, which needs the same padding
    // tuples (a padding row's counts are zero, so a count's `g·` changes nothing).
    assert!(
        layout
            .push
            .iter()
            .zip(&layout.pull)
            .all(|(a, b)| a.padding == b.padding),
        "push/pull padding tuples must match"
    );
    ps("BLOCK_KAPPA_SRC", literals(bks.iter().map(|&(s, _)| s)));
    let bgroups = block_groups(&bks, &sblk);
    ps("N_BLOCK_GROUPS", bgroups.len().to_string());
    ps(
        "BLOCK_GROUP_START",
        literals(bgroups.iter().copied().chain([bks.len()])),
    );
    ps(
        "SIDE_GROUP_START",
        literals(sblk.iter().map(|&b| bgroups.partition_point(|&start| start < b))),
    );
    ps("BLOCK_KAPPA_ADJ", literals(bks.iter().map(|&(_, a)| a)));
    ps(
        "BLOCK_TABLE",
        literals(bks.iter().map(|&(s, _)| if s >= 2 { s - 2 } else { N_TABLES })),
    );
    let mut block_side = Vec::new();
    for (s, blocks) in sides.iter().enumerate() {
        block_side.extend(std::iter::repeat_n(s, blocks.len()));
    }
    ps("BLOCK_SIDE", literals(&block_side));
    ps("BLOCK_COORD_OFF", literals(block_coords.iter().map(|r| r.start)));
    ps("BLOCK_COORD_COUNT", literals(block_coords.iter().map(|r| r.len())));
    ps("COORD_TYPE", literals(coordinates.iter().map(|c| c.kind)));
    ps("COORD_CONST", literals(coordinates.iter().map(|c| c.constant)));
    ps("COORD_PADDING", literals(coordinates.iter().map(|c| c.padding)));
    ps("COORD_FRESH", literals(coordinates.iter().map(|c| c.fresh)));
    // A fresh coordinate's place in the run of words the decomposition absorbs, which
    // decides where it waits in the Fiat-Shamir sponge.
    ps(
        "COORD_FRESH_POS",
        literals(xi_offsets(coordinates.iter().map(|c| c.fresh))),
    );
    ps(
        "N_FRESH",
        coordinates.iter().map(|c| c.fresh).sum::<usize>().to_string(),
    );
    ps("COORD_CLAIM_SLOT", literals(coordinates.iter().map(|c| c.claim_slot)));
    ps("COORD_TERM_OFF", literals(coordinates.iter().map(|c| c.terms.start)));
    ps("COORD_TERM_COUNT", literals(coordinates.iter().map(|c| c.terms.len())));
    // A pull table-block coordinate's difference from its push twin's, as merged terms
    // appended after every coordinate's own: the guest counts a pull block's form as
    // push's plus these, the two blocks sharing eq_hi.
    let (mut diff_off, mut diff_count) = (vec![0usize; coordinates.len()], vec![0usize; coordinates.len()]);
    for b in sblk[1]..sblk[2] {
        let (pull, push) = (block_coords[b].clone(), block_coords[b - sblk[1]].clone());
        assert!(
            bks[b] == bks[b - sblk[1]] && pull.len() == push.len(),
            "pull block {b} must mirror its push twin"
        );
        for (p, q) in pull.zip(push) {
            let merged = coord_difference(
                &terms[coordinates[p].terms.clone()],
                &terms[coordinates[q].terms.clone()],
            );
            diff_off[p] = terms.len();
            diff_count[p] = merged.len();
            terms.extend(merged);
        }
    }
    ps("COORD_DIFF_OFF", literals(&diff_off));
    ps("COORD_DIFF_COUNT", literals(&diff_count));
    ps("TERM_TYPE", literals(terms.iter().map(|t| t.kind)));
    ps("TERM_CONST", literals(terms.iter().map(|t| t.constant)));
    ps("TERM_COL_A", literals(terms.iter().map(|t| t.column_a)));
    ps("TERM_COL_B", literals(terms.iter().map(|t| t.column_b)));
    ps("N_BUS_CLAIMS", nclaims.to_string());
    let idxc: Vec<u128> = (0..34)
        .map(|i| {
            let mut g2k = F192::from(F64::G);
            for _ in 0..i {
                g2k = g2k * g2k;
            }
            dsl_u128(F192::ONE + g2k)
        })
        .collect();
    ps("INDEX_MLE_FACTORS", literals(&idxc));
    (nclaims, nbcv)
}

/// The claim pool's descriptors.
fn claim_placeholders(
    layout: &leanvm_core::cpu::Layout,
    kbc: usize,
    nclaims: usize,
    ps: &mut impl FnMut(&str, String),
) {
    let evtot: usize = leanvm_core::tables::tables()
        .iter()
        .map(|t| t.n_committed_columns())
        .sum();
    let ncl = nclaims + evtot + 3; // bus + constraint + the three PI memory-limb claims

    // ---- claim descriptor buffer ids (structural) ----
    let col_sources_pm = leanvm_core::cpu::col_kappa_sources(kbc);
    let mut compact_col_pm = vec![usize::MAX; col_sources_pm.len()];
    let mut n_committed = 0usize;
    for (global, source) in col_sources_pm.iter().enumerate() {
        if source.is_some() {
            compact_col_pm[global] = n_committed;
            n_committed += 1;
        }
    }
    let qkeccak_compact = compact_col_pm[leanvm_core::cpu::QKECCAK];
    assert_ne!(qkeccak_compact, usize::MAX, "QKECCAK must be committed");
    let mut claims = Vec::new();
    walk_claims(layout, kbc, |site| {
        let descriptor = match site {
            ClaimSite::Framework { column, source } => {
                let column = compact_col_pm[column];
                assert_ne!(column, usize::MAX, "framework claim must target a committed column");
                ClaimDescriptor {
                    buffer: POINT_BUF_ZETA,
                    column,
                    qkeccak_slot: 0,
                    source,
                }
            }
            ClaimSite::TableColumn { column, table } => {
                // A table column's claim is at its table's chi[..tau].
                let source = (2 + table, 0);
                match keccak_value(column) {
                    None => ClaimDescriptor {
                        buffer: POINT_BUF_RHO,
                        column: compact_col_pm[column],
                        qkeccak_slot: 0,
                        source,
                    },
                    Some(KeccakLane::Slot(slot)) => ClaimDescriptor {
                        buffer: POINT_BUF_QKECCAK_RHO,
                        column: qkeccak_compact,
                        qkeccak_slot: slot,
                        source,
                    },
                    Some(KeccakLane::Output(_)) => ClaimDescriptor {
                        buffer: POINT_BUF_KECCAK_OUT,
                        column: qkeccak_compact,
                        qkeccak_slot: 0,
                        source,
                    },
                }
            }
            ClaimSite::MemoryLimb { column } => ClaimDescriptor {
                buffer: POINT_BUF_PI,
                column: compact_col_pm[column],
                qkeccak_slot: 0,
                source: (1, 0),
            },
        };
        claims.push(descriptor);
    });
    assert_eq!(claims.len(), ncl, "descriptor count == pool size");
    ps("N_CLAIMS", ncl.to_string());
    ps("CLAIM_POINT_BUF", literals(claims.iter().map(|c| c.buffer)));
    ps("CLAIM_COMMITTED_COL", literals(claims.iter().map(|c| c.column)));
    ps("CLAIM_KAPPA_SRC", literals(claims.iter().map(|c| c.source.0)));
    ps("CLAIM_KAPPA_ADJ", literals(claims.iter().map(|c| c.source.1)));
    let slot_log = leanvm_core::keccak::SLOT_LOG;
    let cpqbits: Vec<usize> = claims
        .iter()
        .flat_map(|c| (0..slot_log).map(move |k| (c.qkeccak_slot >> k) & 1))
        .collect();
    // The output lanes' claims, consecutive in the pool: the reduction's claim on the output.
    let out_pool = claims
        .iter()
        .position(|c| c.buffer == POINT_BUF_KECCAK_OUT)
        .expect("the output lanes' claims");
    assert!((0..claims.len()).all(|j| (claims[j].buffer == POINT_BUF_KECCAK_OUT)
        == (out_pool..out_pool + primitives::hash::STATE_LANES).contains(&j)));
    ps("KECCAK_OUT_POOL", out_pool.to_string());
    ps("CLAIM_QKECCAK_SLOT_BITS", literals(&cpqbits));
    ps("QKECCAK_COMMITTED_COL", qkeccak_compact.to_string());
    ps("QKECCAK_VARS_CAP", (33 + slot_log).to_string());
}

/// The table sumcheck's tables and xi layout.
fn table_placeholders(ps: &mut impl FnMut(&str, String)) {
    ps("N_TABLES", N_TABLES.to_string());
    ps("FLOORS", literals(leanvm_core::tables::TABLE_LOG_FLOORS));
    // The table sumcheck's xi layout, from the native verifier's own numbers:
    // a disjoint range of identities per table, then THREE powers shared by every
    // table, one per bus side. Sharing is what lets the target be derived from the
    // three leaf claims instead of trusted (leanvm_core::cpu::xi_form_base).
    let n_id: Vec<usize> = leanvm_core::tables::tables()
        .iter()
        .map(|t| t.n_constraints())
        .collect();
    let form_base = leanvm_core::cpu::xi_form_base();
    ps("ETA_OFFSET", literals(xi_offsets(n_id.iter().copied())));
    ps("ETA_FORM_BASE", form_base.to_string());
    ps("N_ETA_POWS", (form_base + 3).to_string());
    let committed: Vec<usize> = leanvm_core::tables::tables()
        .iter()
        .map(|t| t.n_committed_columns())
        .collect();
    ps("N_TABLE_COLS", literals(&committed));
    ps("TABLE_COL_BASE", literals(xi_offsets(committed.iter().copied())));
    ps("N_COL_EVALS", committed.iter().sum::<usize>().to_string());
    ps("TABLE_COLS_CAP", (committed.iter().max().unwrap() + 1).to_string());
}

/// The Keccak reduction (`keccak_gkr`), as index lists the guest unrolls.
fn keccak_placeholders(ps: &mut impl FnMut(&str, String)) {
    use keccak_gkr::rmfe::{EMBED_BITS, PACK, PROD_BITS, RMFE};
    let shape = keccak_gkr::Shape::new(leanvm_core::keccak::MIN_STEPS, leanvm_core::keccak::DEPTH);
    let (depth, segments) = (shape.depth(), shape.segments());
    // `starts[i]..starts[i + 1]` indexes row `i`'s entries of the flat list: each row's
    // offset, then the end.
    let lists = |rows: Vec<Vec<usize>>| {
        let starts = xi_offsets(rows.iter().map(Vec::len).chain([0]));
        (literals(starts), literals(rows.concat()))
    };
    let bits = |word: u64, n: usize| (0..n).filter(|b| word >> b & 1 == 1).collect::<Vec<_>>();
    assert!(segments > 1, "the guest opens a committed segment output");
    ps("KECCAK_DEPTH", depth.to_string());
    ps("KECCAK_SEGMENTS", segments.to_string());
    ps("KECCAK_SEG_LOG", shape.seg_log().to_string());
    ps("KECCAK_PACK", PACK.to_string());
    ps("KECCAK_EMBED_BITS", EMBED_BITS.to_string());
    ps("KECCAK_PROD_BITS", PROD_BITS.to_string());
    // Each product coefficient's reader, the sum of the instance weights whose projection reads
    // it, as a straight line of sums over the PACK weights.
    let (ops, outputs) = straight_line(
        &(0..PROD_BITS)
            .map(|c| (0..PACK).filter(|&i| RMFE.proj[i] >> c & 1 == 1).collect::<Vec<_>>())
            .collect::<Vec<_>>(),
        PACK,
    );
    ps("KECCAK_PROJ_A", literals(ops.iter().map(|op| op.0)));
    ps("KECCAK_PROJ_B", literals(ops.iter().map(|op| op.1)));
    ps("KECCAK_PROJ_OUT", literals(outputs));
    ps("KECCAK_PROJ_CELLS", (PACK + ops.len()).to_string());
    // Each unit vector's embedding is a product of small irreducible factors (the modulus's,
    // by the CRT): the factors' monomials, then a straight line of products over them.
    let factored: Vec<Vec<u64>> = RMFE.emb.iter().map(|&e| poly_factors(e as u64)).collect();
    let clmul = |a: u64, b: u64| (0..64).filter(|i| b >> i & 1 == 1).fold(0, |acc, i| acc ^ a << i);
    for (row, &e) in factored.iter().zip(&RMFE.emb) {
        assert_eq!(row.iter().fold(1, |acc, &f| clmul(acc, f)), e as u64);
    }
    let mut factors: Vec<u64> = factored.concat();
    factors.sort_unstable();
    factors.dedup();
    let (starts, idx) = lists(factors.iter().map(|&f| bits(f, EMBED_BITS)).collect());
    ps("KECCAK_FAC_START", starts);
    ps("KECCAK_FAC_IDX", idx);
    ps(
        "KECCAK_FAC_DEG",
        (factors.iter().map(|f| 63 - f.leading_zeros()).max().unwrap() + 1).to_string(),
    );
    let (ops, outputs) = straight_line(
        &factored
            .iter()
            .map(|row| {
                row.iter()
                    .map(|f| factors.binary_search(f).unwrap())
                    .collect::<Vec<_>>()
            })
            .collect::<Vec<_>>(),
        factors.len(),
    );
    ps("KECCAK_EMB_A", literals(ops.iter().map(|op| op.0)));
    ps("KECCAK_EMB_B", literals(ops.iter().map(|op| op.1)));
    ps("KECCAK_EMB_OUT", literals(outputs));
    ps("KECCAK_FAC_N", factors.len().to_string());
    ps("KECCAK_EMB_CELLS", (factors.len() + ops.len()).to_string());
    // The positions iota flips are `2^i - 1`; per (layer, segment), which `i`.
    let (starts, idx) = lists(
        (0..depth * segments)
            .map(|at| {
                let round = shape.round(at % segments, at / segments);
                (0..7).filter(|i| keccak_gkr::iota_bit(round, (1 << i) - 1)).collect()
            })
            .collect(),
    );
    ps("KECCAK_IOTA_START", starts);
    ps("KECCAK_IOTA_IDX", idx);
    ps("KECCAK_PI", literals(primitives::hash::PI));
    ps("KECCAK_RHO", literals(primitives::hash::RHO));
    // The rotations the linear layer's transpose reads a slice table at.
    let mut used = [0usize; 64];
    for rot in primitives::hash::RHO {
        used[rot as usize] = 1;
        used[(rot as usize + 1) % 64] = 1;
    }
    ps("KECCAK_SHIFT_USED", literals(used));
    ps("KECCAK_STATE", leanvm_core::keccak::STATE_CELLS.to_string());
}

/// The F192 tower and its coordinate basis.
fn field_placeholders(ps: &mut impl FnMut(&str, String)) {
    // Tower F192 = F64[Y]/(Y^3+Y+1), Y = new(0,1,0). Y_TOWER embeds Y for
    // AIR lane reassembly; Y_INV splits a canonical word into its two K limbs.
    let y_tower = F192::new(0, 1, 0);
    ps("Y_TOWER", dsl_u128(y_tower).to_string());
    ps("Y_INV", f192_literal(y_tower.inv()));
    // Coordinate basis e_i of F192 over F2 (spans the whole field): the 64
    // binary basis vectors in each of the three tower limbs. The guest uses
    // these vectors to reconstruct a word from its 192 coordinate bits.
    let coord_basis: Vec<F192> = (0..192)
        .map(|i| match i / 64 {
            0 => F192::new(1u64 << i, 0, 0),
            1 => F192::new(0, 1u64 << (i - 64), 0),
            2 => F192::new(0, 0, 1u64 << (i - 128)),
            _ => unreachable!(),
        })
        .collect();
    ps("COORD_BASIS", flds(&coord_basis));
    ps("QKECCAK_SLOT_LOG", leanvm_core::keccak::SLOT_LOG.to_string());
}

/// The stacked opening's candidate tables, one per committed size and rate: every size the native verifier accepts of
/// an execution of the guest's bytecode, `min_execution_stacked_log(kbc)..=MAX_STACKED_LOG`.
fn opening_placeholders(kbc: usize, ps: &mut impl FnMut(&str, String)) {
    let oshape = |m: usize, log_inv_rate: usize| {
        let shape = whir_shape(m, log_inv_rate);
        let (vc, sh) = (&shape.config, &shape.levels);
        let (cn, cr) = (sh.levels, vc.level_steps);
        // Every cap root must match a transcript-bound root, including the final level.
        assert_eq!(cr, cn - 1, "the yr level must be the last one");
        let (ck, cyr) = (&sh.ks, sh.yr_log_n);
        let cq = &vc.queries;
        let (cd, cp) = (&shape.depth, &shape.per_squeeze);
        let cs: Vec<usize> = (0..cn).map(|i| cq[i].div_ceil(cp[i])).collect();
        let cni: Vec<usize> = ck.iter().map(|&k| 1usize << k).collect();
        assert!(
            cni.iter().enumerate().all(|(lv, &n)| {
                let bytes = if lv == 0 { 8 * n } else { 24 * n };
                bytes <= 1024 && n % 4 == 0
            }),
            "recursive WHIR guest supports Merkle rows of at most 1024 bytes, packed four words to two cells"
        );
        let psum = |f: &dyn Fn(usize) -> usize| -> Vec<usize> {
            let mut offsets = Vec::with_capacity(cn);
            let mut acc = 0;
            for lv in 0..cn {
                offsets.push(acc);
                acc += f(lv);
            }
            offsets
        };
        let cap_depths: Vec<_> = (0..cn).map(|lv| merkle_cap_depth(cq[lv], cd[lv])).collect();
        let c_foldbase = psum(&|lv| ck[lv]);
        let c_risstart: Vec<usize> = (0..cn).map(|k| c_foldbase[k] + ck[k]).collect();
        OpeningShape {
            n_levels: cn,
            yr_level: cr,
            yr_log_len: cyr,
            folds: shape.levels.ks,
            log_message_columns: shape.levels.log_msg_cols,
            queries: shape.config.queries,
            tree_depths: shape.depth,
            positions_per_squeeze: shape.per_squeeze,
            squeezes: cs,
            interleaving: cni,
            query_grinding_bits: shape.config.grinding_bits,
            cap_depths,
            fold_offsets: c_foldbase,
            residual_fold_offsets: c_risstart,
            ood_samples: shape.config.ood_samples,
        }
    };
    let (minm, maxm) = (verifier::min_execution_stacked_log(kbc), verifier::MAX_STACKED_LOG);
    let rates = pcs::whir::MIN_LOG_INV_RATE..=pcs::whir::MAX_LOG_INV_RATE;
    let cands: Vec<_> = rates.flat_map(|r| (minm..=maxm).map(move |m| oshape(m, r))).collect();
    let maxlev = cands.iter().map(|c| c.n_levels).max().unwrap();
    let maxood = cands.iter().flat_map(|c| &c.ood_samples).copied().max().unwrap_or(0);
    ps("LIG_MAX_LEVELS", maxlev.to_string());
    ps("LIG_MAX_OOD_SAMPLES", maxood.to_string());
    ps("LIG_MIN_LOG_SIZE", minm.to_string());
    let cks: Vec<(usize, usize)> = leanvm_core::cpu::col_kappa_sources(kbc).into_iter().flatten().collect();
    ps("N_COMMITTED_COLS", cks.len().to_string());
    ps("N_COLUMN_LOGS", (verifier::MAX_STACKED_LOG + 1).to_string());
    ps("COL_KAPPA_SRC", literals(cks.iter().map(|&(s, _)| s)));
    ps("COL_KAPPA_ADJ", literals(cks.iter().map(|&(_, a)| a)));
    let groups = col_groups(&cks);
    ps("N_COL_GROUPS", groups.len().to_string());
    ps("COL_GROUP_START", literals(groups.iter().copied().chain([cks.len()])));
    ps("PCS_MIN_MU", verifier::MIN_STACKED_LOG.to_string());
    ps(
        "LIG_LOG_MSG_COLS_CAP",
        cands
            .iter()
            .map(|c| *c.log_message_columns.iter().max().unwrap())
            .max()
            .unwrap()
            .to_string(),
    );
    ps(
        "YR_LOG_CAP",
        cands.iter().map(|c| c.yr_log_len).max().unwrap().to_string(),
    );
    {
        let flat = |f: &dyn Fn(&OpeningShape) -> Vec<usize>| {
            let rows: Vec<usize> = cands
                .iter()
                .flat_map(|c| {
                    let mut row = f(c);
                    row.resize(maxlev, 0);
                    row
                })
                .collect();
            literals(rows)
        };
        let scal = |f: &dyn Fn(&OpeningShape) -> usize| literals(cands.iter().map(f));
        ps("LIG_N_LEVELS", scal(&|c| c.n_levels));
        ps("LIG_YR_LEVEL", scal(&|c| c.yr_level));
        // The guest rotates the terminal point by the lane-fold count to index it by
        // witness coordinate, and the residual segment is what it rotates the last
        // lane challenges past, so the residual may never be longer than that fold
        // (`RESIDUAL_MAX_LOG` < `INITIAL_FOLDING_FACTOR` keeps this true by a margin).
        assert!(
            cands.iter().all(|c| c.yr_log_len <= c.folds[0]),
            "residual longer than the lane fold: the guest's point rotation has no room"
        );
        ps("LIG_YR_LOG_LEN", scal(&|c| c.yr_log_len));
        ps("LIG_YR_LEN", scal(&|c| 1usize << c.yr_log_len));
        ps("LIG_TOTAL_FOLDS", scal(&|c| c.folds.iter().sum()));
        ps("LIG_MAX_INTERLEAVE", scal(&|c| *c.interleaving.iter().max().unwrap()));
        let row_cap = cands
            .iter()
            .flat_map(|c| {
                c.interleaving
                    .iter()
                    .enumerate()
                    .map(|(level, &n)| n * if level == 0 { 1 } else { 3 })
            })
            .max()
            .unwrap();
        ps("LIG_ROW_CAP", row_cap.to_string());
        ps("LIG_PACKED_ROW_CAP", (row_cap / 2).to_string());
        // `decode_query_windows`: a squeezed word's positions, each its path bits QW at a time, then the cap
        // child bit and the cap index, read back through `win_tabs`' 128 bit patterns.
        let windows = cands.iter().flat_map(|c| {
            (0..c.n_levels).map(|lv| {
                let (depth, cap) = (c.tree_depths[lv], c.cap_depths[lv]);
                assert!((2..=8).contains(&cap), "a cap of {cap} levels");
                c.positions_per_squeeze[lv] * ((depth - cap).div_ceil(QUERY_WINDOW_BITS) + 2)
            })
        });
        assert!(
            windows.max().unwrap() <= QUERY_WINDOWS_CAP,
            "a squeezed word's windows past QUERY_WINDOWS_CAP"
        );
        ps(
            "LIG_PATH_CAP",
            cands
                .iter()
                .flat_map(|c| {
                    c.tree_depths
                        .iter()
                        .zip(&c.cap_depths)
                        .map(|(depth, cap)| 4 * (depth - cap))
                })
                .max()
                .unwrap()
                .to_string(),
        );
        // The guest reads every level's query nonce, which a grind of no bits would not send.
        assert!(
            cands.iter().all(|c| c.query_grinding_bits.iter().all(|&bits| bits > 0)),
            "a query phase grinds no bits"
        );
        ps("LIG_QUERY_GRIND_BITS", flat(&|c| c.query_grinding_bits.clone()));
        ps("LIG_OOD_SAMPLES", flat(&|c| c.ood_samples.clone()));
        ps("LIG_QUERIES", flat(&|c| c.queries.clone()));
        ps("LIG_FOLDS", flat(&|c| c.folds.clone()));
        ps("LIG_INTERLEAVE", flat(&|c| c.interleaving.clone()));
        // 128-bit cells per leaf row: level 0's committed rows are base-field
        // F64 (8 bytes/lane); deeper levels are native F192 (24 bytes/word,
        // received as three embedded K limbs each).
        ps(
            "LIG_LEAF_CELLS",
            flat(&|c| {
                c.interleaving
                    .iter()
                    .enumerate()
                    .map(|(level, &n)| if level == 0 { n / 2 } else { 3 * n / 2 })
                    .collect()
            }),
        );
        ps("LIG_TREE_DEPTH", flat(&|c| c.tree_depths.clone()));
        ps("LIG_CAP_DEPTH", flat(&|c| c.cap_depths.clone()));
        ps("LIG_SQUEEZES", flat(&|c| c.squeezes.clone()));
        ps("LIG_LOG_MSG_COLS", flat(&|c| c.log_message_columns.clone()));
        ps("LIG_RESIDUAL_FOLD_OFF", flat(&|c| c.residual_fold_offsets.clone()));
        ps(
            "LIG_RESIDUAL_PREFIX_LEN",
            flat(&|c| {
                c.log_message_columns
                    .iter()
                    .map(|&columns| columns - c.yr_log_len)
                    .collect()
            }),
        );
        ps("LIG_FOLDS_OFF", flat(&|c| c.fold_offsets.clone()));
    }
    let n_log_sizes = maxm - minm + 1;
    let n_rates = MAX_LOG_INV_RATE - MIN_LOG_INV_RATE + 1;
    // `open_stacked` has one arm per size and reads these at the size's first-rate row: a rate changes none of them.
    fn size_only(c: &OpeningShape) -> (usize, usize, usize, [&Vec<usize>; 7]) {
        let lists = [
            &c.folds,
            &c.log_message_columns,
            &c.interleaving,
            &c.fold_offsets,
            &c.residual_fold_offsets,
            &c.ood_samples,
            &c.query_grinding_bits,
        ];
        (c.n_levels, c.yr_level, c.yr_log_len, lists)
    }
    for (i, c) in cands.iter().enumerate() {
        assert!(
            size_only(c) == size_only(&cands[i % n_log_sizes]),
            "a size-only opening table varies by rate"
        );
    }
    let level_positions = cands
        .iter()
        .flat_map(|c| (0..c.n_levels).map(|lv| c.squeezes[lv] * c.positions_per_squeeze[lv]));
    ps("LIG_LEVEL_POSITIONS_CAP", level_positions.max().unwrap().to_string());
    // The subspace-vanishing values of a level's message columns and their inverses, by the column count: block `n`
    // at n(n+1)/2.
    let max_cols = cands
        .iter()
        .flat_map(|c| &c.log_message_columns)
        .copied()
        .max()
        .unwrap();
    let by_cols: Vec<F64> = (0..=max_cols)
        .flat_map(|cols| {
            let values = pcs::whir::eval_sk_at_vks(cols);
            assert_eq!(values.len(), cols + 1);
            values
        })
        .collect();
    let inverses: Vec<F192> = by_cols.iter().map(|v| F192::from(v.inv())).collect();
    ps(
        "VANISH_BY_COLS",
        flds(&by_cols.into_iter().map(F192::from).collect::<Vec<_>>()),
    );
    ps("VANISH_INVS_BY_COLS", flds(&inverses));
    ps("LIG_N_LOG_SIZES", n_log_sizes.to_string());
    ps("LIG_N_RATES", n_rates.to_string());
    ps("LIG_MIN_SHIFT_INV", dsl_u128(F192::from(g_pow(minm).inv())).to_string());
}

/// The stacked bytecode's claim, the batch's seed, and the statement's layout.
fn bytecode_placeholders(kbc: usize, nbcv: usize, ps: &mut impl FnMut(&str, String)) {
    ps("BYTECODE_LOG", kbc.to_string());
    // The stacked bytecode: nbcv/2 encoding columns per side, aligned with the bus
    // tuple, so their slots span the fingerprint's own bits. The defer region is
    // the claim's point (row, then slot) and its value.
    let bc_cols = nbcv / 2;
    let log2_bc_cols = leanvm_core::leaf::N_TUPLE_BITS;
    ps("BYTECODE_COLS", bc_cols.to_string());
    ps("LOG2_BYTECODE_COLS", log2_bc_cols.to_string());
    ps("DEFER_SIZE", (kbc + log2_bc_cols + 1).to_string());
    ps("BYTECODE_VARS", (kbc + log2_bc_cols).to_string());
    let agg_seed = FiatShamirState::from_label(RECURSION_AGG_LABEL).capacity_cells();
    for (c, cell) in agg_seed.iter().enumerate() {
        ps(&format!("AGG_SEED_{c}"), dsl_u128(*cell).to_string());
    }
    // A child's words: its statement's two, its fresh claim, and its carried claim's cells.
    let child_words = 2 + (kbc + log2_bc_cols + 1) + (kbc + log2_bc_cols + 1);
    ps(
        "AGG_CHILD_PAD",
        (child_words.next_multiple_of(RATE_SCALARS) - child_words).to_string(),
    );
    let defer_cells = kbc + log2_bc_cols + 1;
    ps("STMT_HEADER", STATEMENT_HEADER.to_string());
    let pairs = defer_cells.div_ceil(2);
    ps("STMT_PAIRS", pairs.to_string());
}

/// LeanDA (`doc/leanvm` §sec:leanda).
fn da_placeholders(ps: &mut impl FnMut(&str, String)) {
    let pad_blob = hash_to_scalars(&lean_da::padding_root());
    ps("DA_LOG_K", DA_LOG_K.to_string());
    ps("DA_LOG_CELL", DA_LOG_CELL.to_string());
    ps("DA_MAX_ROWS", DA_MAX_ROWS.to_string());
    ps("DA_LOG_MAX_ROWS", DA_MAX_ROWS.ilog2().to_string());
    ps("DA_PAD_BLOB_0", f192_literal(pad_blob[0]));
    ps("DA_PAD_BLOB_1", f192_literal(pad_blob[1]));
    // `Vhat_j` at each parent node `n` of a product tree over bits `top, top - 1, ...`, in heap
    // order: `j` the bit `n` splits, the point its path's bits make.
    let vhat = lean_da::subspace_above();
    let tree = |top: usize, depth: usize| -> Vec<u64> {
        (0..1usize << depth)
            .map(|n| n.checked_ilog2().map_or(0, |d| vhat(top - d as usize, n - (1 << d)).0))
            .collect()
    };
    let cell_bits = CELLS_PER_ROW.ilog2() as usize;
    let (top_tree, cell_tree) = (tree(DA_LOG_K, cell_bits), tree(DA_LOG_CELL - 1, DA_LOG_CELL));
    ps("DA_VHAT", literals(top_tree.into_iter().chain(cell_tree)));
    // `Vhat_j(beta_(DA_LOG_CELL + b))` for each factor `j` inside a cell and bit `b` of the cell index.
    let column = (0..cell_bits).flat_map(|b| (0..DA_LOG_CELL).map(move |j| (j, b)));
    ps(
        "DA_VHAT_COLUMN",
        literals(column.map(|(j, b)| vhat(j, 1 << (DA_LOG_CELL - 1 - j + b)).0)),
    );
}

// A leanSPHINCS chain step's address is WOTS_HASH's, the type the guest leaves zero.
const _: () = assert!(signatures::lean_sphincs::WOTS_HASH == 0);

/// Both signature schemes and the coverage caps.
fn signature_placeholders(ps: &mut impl FnMut(&str, String)) {
    // Both schemes' WOTS+C: the chain length and the digit sums of a dispatched group.
    ps("W", lean_xmss::W.to_string());
    ps("DIGIT_GROUP", DIGIT_GROUP.to_string());
    let widest = LX_DIGIT_GROUP.max(DIGIT_GROUP);
    let digit_sums: Vec<String> = (0..lean_xmss::CHAIN_LEN.pow(widest as u32))
        .map(|k| {
            let digits = (0..widest).map(|r| k / lean_xmss::CHAIN_LEN.pow(r as u32) % lean_xmss::CHAIN_LEN);
            digits.sum::<usize>().to_string()
        })
        .collect();
    ps("DIGIT_SUMS", format!("[{}]", digit_sums.join(", ")));
    // leanXMSS: its hash types as its address lane holds them. Its addresses are
    // derived per signature from the leaf index the signature carries.
    let address = |tw| dsl_u128(pack_16_bytes(&lean_xmss::address(&tw))).to_string();
    for (name, tag) in [
        ("TW_CHAIN", lean_xmss::TWEAK_CHAIN),
        ("TW_LEAF", lean_xmss::TWEAK_LEAF),
        ("TW_NODE", lean_xmss::TWEAK_NODE),
        ("TW_MSG", lean_xmss::TWEAK_MSG),
    ] {
        ps(name, address(lean_xmss::tweak(tag, 0, 0)));
    }
    ps("LX_V", lean_xmss::V.to_string());
    ps("LX_TARGET_SUM", lean_xmss::TARGET_SUM.to_string());
    ps("LX_HEIGHT", lean_xmss::LAYER_HEIGHT.to_string());
    ps("LX_TOP", address(lean_xmss::tweak(0, lean_xmss::TOP, 0)));
    ps("LX_WINDOW", LX_WINDOW.to_string());
    ps("LX_DIGIT_GROUP", LX_DIGIT_GROUP.to_string());
    let sizes =
        (0..lean_xmss::V.div_ceil(LX_DIGIT_GROUP)).map(|p| LX_DIGIT_GROUP.min(lean_xmss::V - LX_DIGIT_GROUP * p));
    ps("LX_GROUP_SIZES", format!("{:?}", sizes.collect::<Vec<_>>()));
    // A chain index: zero and the powers of two are cells the guest has, the others a
    // slot of the tags it carries.
    let (mut slots, mut logs, mut n_tags) = (Vec::new(), Vec::new(), 0);
    for i in 0..lean_xmss::V {
        let carried = i != 0 && !i.is_power_of_two();
        n_tags += usize::from(carried);
        slots.push(if carried { n_tags } else { 0 });
        logs.push(i.checked_ilog2().unwrap_or(0));
    }
    ps("LX_TAG_SLOT", format!("{slots:?}"));
    ps("LX_TAG_LOG", format!("{logs:?}"));
    ps("LX_N_TAGS", n_tags.to_string());
    ps("CLAIM_GROUP", CLAIM_GROUP.to_string());
    ps("BIG_GROUPS", BIG_GROUPS.to_string());
    ps("MAX_RECURSIONS", MAX_RECURSIONS.to_string());

    // The leanSPHINCS instance: its shape, each address type as A1, the address's
    // second cell, holds it, and the cell bit each address field's low bytes start at.
    // The guest derives the rest of every address from the digest.
    use lean_sphincs::Adrs;
    let cell = |adrs: Adrs, half: usize| dsl_u128(pack_16_bytes(&adrs.as_bytes()[16 * half..][..16]));
    let at = Adrs::new(0, 0);
    for (name, adrs, half) in [
        ("SP_LAYER_MUL", Adrs::new(1, 0), 0),
        ("SP_TREE_MUL_0", Adrs::new(0, 1), 0),
        ("SP_TREE_MUL_1", Adrs::new(0, 1 << 8), 0),
        ("SP_TREE_MUL_2", Adrs::new(0, 1 << 16), 0),
        ("SP_KEY_MUL_0", at.with_key_pair(1), 1),
        ("SP_KEY_MUL_1", at.with_key_pair(1 << 8), 1),
        ("SP_WORD2_MUL", at.with_chain(1), 1),
        ("SP_WORD3_MUL_0", at.with_tree_index(1), 1),
        ("SP_WORD3_MUL_1", at.with_tree_index(1 << 8), 1),
    ] {
        ps(name, cell(adrs, half).to_string());
    }
    ps("SP_V", lean_sphincs::V.to_string());
    ps("SP_TARGET_SUM", lean_sphincs::TARGET_SUM.to_string());
    for (layer, height) in lean_sphincs::LAYER_HEIGHTS.iter().enumerate() {
        ps(&format!("SP_HEIGHT_{layer}"), height.to_string());
    }
    ps("SP_FORS_TREES", lean_sphincs::FORS_TREES.to_string());
    ps("SP_FORS_HEIGHT", lean_sphincs::FORS_HEIGHT.to_string());
    ps("SP_DIGEST_BYTES", lean_sphincs::DIGEST_BYTES.to_string());
    for (name, t) in [
        ("SP_WOTS_PK", lean_sphincs::WOTS_PK),
        ("SP_TREE", lean_sphincs::TREE),
        ("SP_FORS_TREE", lean_sphincs::FORS_TREE),
        ("SP_FORS_ROOTS", lean_sphincs::FORS_ROOTS),
        ("SP_WOTS_ENC", lean_sphincs::WOTS_ENC),
    ] {
        ps(name, cell(at.with_type(t), 1).to_string());
    }
}
