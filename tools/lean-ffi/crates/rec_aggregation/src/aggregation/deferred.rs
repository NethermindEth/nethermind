//! The deferred claim on the stacked bytecode and the sumcheck that batches it.

use super::cells::count;
use super::error::AggregateVerifyError;
use super::guest::unified_guest;
use super::hints::SubHints;
use leanvm_core::cpu::{from_scalar, to_scalar};
use leanvm_core::transcript::{FiatShamirState, RATE_SCALARS};
use primitives::field::{F64, F64x8, F192, F192x8, Prod8, dot_base};
use primitives::multilinear::{eq_table, interp, mle_eval_par, packed_eq};
use std::ops::Range;

pub(super) const RECURSION_AGG_LABEL: &[u8] = b"leanvm/recursion-aggregation/v1";

// The guest bakes a bytecode claim's width from `N_TUPLE_BITS` while `bytecode_vars`
// reads it off the stacked table, which is `N_BYTECODE_SELECTORS` wide. Two constants
// that happen to agree: were they to drift, a leaf's claim point would be one length in
// the guest and another in the statement, and nothing else would notice.
const _: () = assert!(leanvm_core::leaf::N_TUPLE_BITS == leanvm_core::leaf::N_BYTECODE_SELECTORS);

/// The claim on the stacked bytecode that a node defers rather than evaluating
/// in-circuit: one point and value (`doc/leanvm/main.tex` §Deferred evaluation
/// claims).
///
/// Only the point is transmitted; the value is derived from it on receipt, so a
/// prover that lies about a value changes the statement its proof has to satisfy
/// rather than the claim anyone checks.
#[derive(Clone, Debug, PartialEq, Eq)]
pub(crate) struct DeferredClaim {
    pub(super) bytecode_point: Vec<F192>,
    pub(super) bytecode_value: F192,
}

impl DeferredClaim {
    /// What a leaf defers: the all-zeros point, where the value is just the
    /// table's first entry.
    pub(super) fn leaf() -> Self {
        Self::recompute(vec![F192::ZERO; bytecode_vars()]).expect("the all-zeros point has the right shape")
    }

    /// Evaluate the stacked bytecode at `bytecode_point`, on the prover's field kernels and thread pool.
    pub(super) fn recompute(bytecode_point: Vec<F192>) -> Result<Self, AggregateVerifyError> {
        Self::evaluate(bytecode_point, |point| {
            let _span = tracing::info_span!("bytecode mle").entered();
            mle_eval_par(stacked_bytecode(), point)
        })
    }

    /// [`Self::recompute`] in the `verifier` crate: scalar and single threaded, what a received aggregate is checked with.
    pub(super) fn recompute_scalar(bytecode_point: Vec<F192>) -> Result<Self, AggregateVerifyError> {
        Self::evaluate(bytecode_point, |point| {
            let _span = tracing::info_span!("bytecode mle").entered();
            let scalars: Vec<_> = point.iter().map(to_scalar).collect();
            from_scalar(
                scalar_bytecode()
                    .evaluate(&scalars)
                    .expect("the point's length was checked"),
            )
        })
    }

    fn evaluate(
        bytecode_point: Vec<F192>,
        bytecode_at: impl FnOnce(&[F192]) -> F192,
    ) -> Result<Self, AggregateVerifyError> {
        if bytecode_point.len() != bytecode_vars() {
            return Err(AggregateVerifyError::MalformedClaim);
        }
        // Every leaf defers the all-zeros point, where the bytecode polynomial is
        // just its table's first entry. Worth special-casing: the general path is
        // a pass over 2^23 entries, and a leaf is the aggregate people verify most.
        let bytecode_value = if bytecode_point.iter().all(|x| *x == F192::ZERO) {
            F192::from(stacked_bytecode()[0])
        } else {
            bytecode_at(&bytecode_point)
        };
        Ok(Self {
            bytecode_point,
            bytecode_value,
        })
    }

    /// The cells the statement digest absorbs, in the guest's `defer_stmt` order.
    pub(super) fn cells(&self) -> Vec<F192> {
        let mut cells = self.bytecode_point.clone();
        cells.push(self.bytecode_value);
        cells
    }
}

/// The deferred-claim data the guest binds to the outer public input: the outer
/// verifier checks each claim natively (`doc/leanvm/main.tex` §Deferred evaluation claims).
/// A child's fresh claim is batched with its carried one at every child count, one included.
pub(super) struct DeferredSubproof {
    pub(super) public_input: [F192; 2],
    pub(super) bytecode_point: Vec<F192>,
    pub(super) bytecode_value: F192,
}

/// [`stacked_bytecode`] as the `verifier` crate takes it, hashed once: what [`EthereumProof::verify`](super::EthereumProof::verify) checks proofs against, and [`aggregate`](super::aggregate) reads its children with.
pub(super) fn scalar_bytecode() -> &'static verifier::Bytecode {
    static BYTECODE: std::sync::OnceLock<verifier::Bytecode> = std::sync::OnceLock::new();
    BYTECODE.get_or_init(|| {
        let words: Vec<u64> = stacked_bytecode().iter().map(|word| word.0).collect();
        verifier::Bytecode::new(&words)
    })
}

/// The stacked bytecode polynomial of the aggregation guest: the one fixed
/// table every node's bytecode claims are about. Cached, because verification
/// evaluates it and building it walks the whole program.
pub(super) fn stacked_bytecode() -> &'static [F64] {
    static TABLE: std::sync::OnceLock<Vec<F64>> = std::sync::OnceLock::new();
    TABLE.get_or_init(|| leanvm_core::cpu::layout::bytecode_table(unified_guest().prog()))
}

/// The rows of [`BLOCK`] entries of the stacked bytecode that hold its populated slots: every other row is
/// structurally zero.
fn bytecode_rows() -> Range<usize> {
    static ROWS: std::sync::OnceLock<Range<usize>> = std::sync::OnceLock::new();
    ROWS.get_or_init(|| {
        let rows = stacked_bytecode().as_chunks::<BLOCK>().0;
        let per_slot = rows.len() >> leanvm_core::leaf::N_BYTECODE_SELECTORS;
        assert!(per_slot >= GROUP, "a slot is whole groups of rows");
        let live = |s: &usize| {
            rows[s * per_slot..(s + 1) * per_slot]
                .iter()
                .flatten()
                .any(|v| *v != F64::ZERO)
        };
        let slots = || 0..1 << leanvm_core::leaf::N_BYTECODE_SELECTORS;
        let start = slots().find(live).expect("the bytecode is not all zero");
        let end = slots().rfind(live).expect("the bytecode is not all zero") + 1;
        start * per_slot..end * per_slot
    })
    .clone()
}

/// Low row variables of the stacked bytecode, which the batch binds first.
const BLOCK_VARS: usize = 10;
const BLOCK: usize = 1 << BLOCK_VARS;
/// Rows [`bytecode_sums`] adds into its accumulators between two reads of them.
const GROUP: usize = 8;

/// Per claim point, the stacked bytecode with the point's weight on all but the low `BLOCK_VARS` variables summed
/// out: `G(low) = Σ_r eq(point[BLOCK_VARS..], r) · row_r[low]`, over rows `r` of `BLOCK` entries. The weight
/// `eq(point, ·)` is a product of a factor on the low variables and one on the rest, so the batch's low rounds run
/// on these, and a point's value is read off its own ([`bytecode_value`]).
pub(super) fn bytecode_sums(points: &[Vec<F192>]) -> Vec<Vec<F192>> {
    let _span = tracing::info_span!("Bytecode sums").entered();
    let (rows, live) = (stacked_bytecode().as_chunks::<BLOCK>().0, bytecode_rows());
    points
        .iter()
        .map(|point| {
            // A leaf's claim weighs the first row alone.
            if point.iter().all(|&x| x == F192::ZERO) {
                return rows[0].iter().map(|&w| F192::from(w)).collect();
            }
            let eq = eq_table(&point[BLOCK_VARS..]);
            let (groups, weights) = (
                rows[live.clone()].as_chunks::<GROUP>().0,
                eq[live.clone()].as_chunks::<GROUP>().0,
            );
            let partial = parallel::fold_reduce(
                groups.len(),
                || vec![Prod8::ZERO; BLOCK / 8],
                |acc: &mut Vec<Prod8>, g| {
                    let splats = weights[g].map(F192x8::splat);
                    for (x, acc) in acc.iter_mut().enumerate() {
                        for (splat, row) in splats.iter().zip(&groups[g]) {
                            *acc ^= splat.mul_base_unreduced(F64x8::from_array(&row.as_chunks::<8>().0[x]));
                        }
                    }
                },
                |mut a, b| {
                    a.iter_mut().zip(b).for_each(|(a, b)| *a ^= b);
                    a
                },
            );
            partial.into_iter().flat_map(|p| p.reduce_lanes().to_array()).collect()
        })
        .collect()
}

/// The stacked bytecode at `point`, from its [`bytecode_sums`]: what [`mle_eval_par`] gives over the whole table.
pub(super) fn bytecode_value(sums: &[F192], point: &[F192]) -> F192 {
    eq_table(&point[..BLOCK_VARS])
        .iter()
        .zip(sums)
        .map(|(&eq, &sum)| eq * sum)
        .sum()
}

/// Variables of a bytecode claim's point: the log row count plus the stacking
/// selectors.
pub(super) fn bytecode_vars() -> usize {
    stacked_bytecode().len().trailing_zeros() as usize
}

fn fold_lsb(table: &mut Vec<F192>, challenge: F192) {
    let half = table.len() / 2;
    for i in 0..half {
        table[i] = interp(table[2 * i], table[2 * i + 1], challenge);
    }
    table.truncate(half);
}

/// Compressed product-sumcheck round message of `u·m`: (g1, g∞) with g0 recovered from the
/// running claim.
fn round_msg(u: &[F192], m: &[F192]) -> (F192, F192) {
    (0..u.len() / 2).fold((F192::ZERO, F192::ZERO), |acc, i| {
        let (u0, u1) = (u[2 * i], u[2 * i + 1]);
        let (m0, m1) = (m[2 * i], m[2 * i + 1]);
        (acc.0 + u1 * m1, acc.1 + (u0 + u1) * (m0 + m1))
    })
}

/// One round of a batching sumcheck, in the transcript order the guest mirrors
/// word for word: observe `(g1, g_inf)`, squeeze the fold challenge, record
/// both, and advance the running claim through the compressed round polynomial.
/// Returns the challenge, which the caller folds its tables with.
fn absorb_round(
    transcript: &mut FiatShamirState,
    messages: &mut Vec<F192>,
    challenges: &mut Vec<F192>,
    claim: &mut F192,
    (g1, gi): (F192, F192),
) -> F192 {
    transcript.observe(g1);
    transcript.observe(gi);
    let challenge = transcript.sample();
    messages.extend([g1, gi]);
    challenges.push(challenge);
    let g0 = *claim + g1;
    let c1 = g0 + g1 + gi;
    *claim = (gi * challenge + c1) * challenge + g0;
    challenge
}

/// Mirror the guest's `aggregate_claims` transcript and prove the batching
/// sumcheck over the stacked bytecode.
///
/// Each child brings two claims, the fresh one its verification raised and the one it deferred; `sums` holds their
/// [`bytecode_sums`] in that order. Returns the guest hints and the single claim they reduce to, or
/// [`AggregateVerifyError::MalformedClaim`] when a child carried a value that is not the bytecode's evaluation.
pub(super) fn aggregate_deferred_claims(
    subproofs: &[DeferredSubproof],
    carried_claims: &[&DeferredClaim],
    mut sums: Vec<Vec<F192>>,
) -> Result<(SubHints, DeferredClaim), AggregateVerifyError> {
    let child_count = subproofs.len();
    assert_eq!(child_count, carried_claims.len(), "one carried claim per child");
    let kbcv = bytecode_vars();

    // The count and each child's words are padded with zeros to whole rates, so the
    // guest's loop over the children finds every child starting the same way.
    let mut transcript = FiatShamirState::from_label(RECURSION_AGG_LABEL);
    let pad = |transcript: &mut FiatShamirState, n: usize| {
        for _ in 0..n.next_multiple_of(RATE_SCALARS) - n {
            transcript.observe(F192::ZERO);
        }
    };
    transcript.observe(count(child_count));
    pad(&mut transcript, 1);
    for (subproof, carried) in subproofs.iter().zip(carried_claims) {
        let mut words = vec![subproof.public_input[0], subproof.public_input[1]];
        words.extend(&subproof.bytecode_point);
        words.push(subproof.bytecode_value);
        words.extend(carried.cells());
        for &value in &words {
            transcript.observe(value);
        }
        pad(&mut transcript, words.len());
    }

    let _span = tracing::info_span!("Bytecode batch", vars = kbcv).entered();
    // Powers of one challenge: child i's fresh claim takes lam^(2i), its carried one lam^(2i+1).
    let lam = transcript.sample();
    let gbc: Vec<F192> = std::iter::successors(Some(F192::ONE), |&power| Some(power * lam))
        .take(2 * child_count)
        .collect();
    let points: Vec<Vec<F192>> = subproofs
        .iter()
        .zip(carried_claims)
        .flat_map(|(subproof, carried)| [subproof.bytecode_point.clone(), carried.bytecode_point.clone()])
        .collect();
    let values: Vec<F192> = subproofs
        .iter()
        .zip(carried_claims)
        .flat_map(|(subproof, carried)| [subproof.bytecode_value, carried.bytecode_value])
        .collect();
    let mut brun: F192 = (0..2 * child_count).map(|index| gbc[index] * values[index]).sum();
    let mut bscr = Vec::new();
    let mut r_bc = Vec::new();
    // The low rounds, on each claim's sums against its `eq` on the low rows, scaled by its power of `lam`.
    assert_eq!(sums.len(), points.len(), "sums for every claim");
    let mut eq_lo: Vec<Vec<F192>> = points
        .iter()
        .zip(&gbc)
        .map(|(point, &lambda)| {
            eq_table(&point[..BLOCK_VARS])
                .into_iter()
                .map(|eq| lambda * eq)
                .collect()
        })
        .collect();
    for _ in 0..BLOCK_VARS {
        let msg = sums
            .iter()
            .zip(&eq_lo)
            .fold((F192::ZERO, F192::ZERO), |acc, (sums, eq_lo)| {
                let (g1, gi) = round_msg(sums, eq_lo);
                (acc.0 + g1, acc.1 + gi)
            });
        let r = absorb_round(&mut transcript, &mut bscr, &mut r_bc, &mut brun, msg);
        for table in sums.iter_mut().chain(&mut eq_lo) {
            fold_lsb(table, r);
        }
    }
    // The rest: every row against the low challenges, zero outside the populated ones, and each claim's `eq` on
    // the rows, scaled by what its low rows left.
    let (rows, live) = (stacked_bytecode().as_chunks::<BLOCK>().0, bytecode_rows());
    let low_eq = packed_eq(&r_bc);
    let mut bt = vec![F192::ZERO; rows.len()];
    parallel::fill(&mut bt[live.clone()], |i| {
        dot_base(&low_eq, &rows[live.start + i]).reduce()
    });
    let mut wt = vec![F192::ZERO; rows.len()];
    for (point, eq_lo) in points.iter().zip(&eq_lo) {
        for (weight, eq) in wt.iter_mut().zip(eq_table(&point[BLOCK_VARS..])) {
            *weight += eq_lo[0] * eq;
        }
    }
    for _ in BLOCK_VARS..kbcv {
        let msg = round_msg(&bt, &wt);
        let r = absorb_round(&mut transcript, &mut bscr, &mut r_bc, &mut brun, msg);
        fold_lsb(&mut bt, r);
        fold_lsb(&mut wt, r);
    }
    let v_bc = bt[0];
    if brun != v_bc * wt[0] {
        return Err(AggregateVerifyError::MalformedClaim);
    }

    drop(_span);
    let hints = vec![("bc_sumcheck_msgs", bscr), ("bc_star_hint", vec![v_bc])];
    Ok((
        hints,
        DeferredClaim {
            bytecode_point: r_bc,
            bytecode_value: v_bc,
        },
    ))
}
