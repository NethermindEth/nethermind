//! The entry points that prove one node of an aggregation tree.

use super::cells::count;
use super::child::gen_verify;
use super::coverage::plan_coverage;
use super::deferred::{DeferredClaim, aggregate_deferred_claims, bytecode_sums, bytecode_value, scalar_bytecode};
use super::error::{AggregateVerifyError, AggregationError, SnarkProveError, SnarkVerifyError};
use super::guest::unified_guest;
#[cfg(test)]
use super::hints::FORGE;
use super::hints::{Hints, lean_sphincs_hints, lean_xmss_hints};
use super::proof::{EthereumClaims, EthereumProof, check_signer_set};
use super::statement::{lean_sphincs_claim_cells, lean_xmss_claim_cells, list_split, statement_digest};
use super::{LeanSphincsClaim, LeanXmssClaim, MAX_RECURSIONS};
use fiat_shamir::merkle::hash_to_scalars;
use lean_da::{BLOB_SYMBOLS, CELL_SYMBOLS, CELLS_PER_ROW, CODEWORD_SYMBOLS};
use leanvm_core::cpu::{from_scalar, prove, public_input_bytes, scalar_proof};
use primitives::field::{F64, F192};
use signatures::lean_sphincs::{LeanSphincsPublicKey, LeanSphincsSignature};
use signatures::lean_xmss::{LeanXmssPublicKey, LeanXmssSignature};
use signatures::{lean_sphincs, lean_xmss};

/// Prove existence of signatures and valid encoding of PQ-blobs, potentially using recursive children.
///
/// - `children`: child proofs; at most [`MAX_RECURSIONS`].
/// - `raw_lean_xmss`: list of `(public_key, message, signature)`, any order, each signature at
///   the leaf index it carries.
/// - `raw_lean_sphincs`: list of `(public_key, message, signature)`, any order.
/// - `blobs`: concatenated blobs, each containing [`BLOB_SYMBOLS`] little-endian `u64` symbols;
///   at most [`lean_da::DA_MAX_ROWS`] blobs. A node with blobs publishes their matrix's root, and none
///   of its children's.
/// - `declare`: `None` keeps all signature claims and publishes the blob matrix's root, or else
///   the first child's that has one; `Some` specifies exactly the signatures and DA root to
///   publish, which must be the blob matrix's if there are blobs. Every declared claim must be
///   covered by the inputs above.
/// - `log_inv_rate`: PCS code rate `2^-log_inv_rate`, higher `log_inv_rate` means a smaller proof
///   but slower proving; in [`MIN_LOG_INV_RATE`](pcs::whir::MIN_LOG_INV_RATE)..=[`MAX_LOG_INV_RATE`](pcs::whir::MAX_LOG_INV_RATE).
///
/// IMPORTANT:
/// - `aggregate` should not be called more than once at a time in parallel per process.
/// - Raw signatures are assumed valid: one that does not decode returns
///   [`AggregationError::MalformedRawSignature`], and any other invalid one fails the guest's run,
///   [`AggregationError::ProofError`].
/// - Children are assumed verified by the caller ([`EthereumProof::verify`]): a child the replay
///   cannot walk, or one carrying a false deferred value, returns [`AggregationError::InvalidChild`],
///   and any other invalid child fails the guest's run, [`AggregationError::ProofError`] (TODO
///   return the index of invalid proof).
pub fn aggregate(
    children: &[EthereumProof],
    raw_lean_xmss: Vec<(LeanXmssPublicKey, lean_xmss::Message, LeanXmssSignature)>,
    raw_lean_sphincs: Vec<(LeanSphincsPublicKey, lean_sphincs::Message, LeanSphincsSignature)>,
    blobs: &[u64],
    declare: Option<&EthereumClaims>,
    log_inv_rate: usize,
) -> Result<EthereumProof, AggregationError> {
    aggregate_with_stats(children, raw_lean_xmss, raw_lean_sphincs, blobs, declare, log_inv_rate).map(|(sig, _)| sig)
}

/// [`aggregate`], under the same contract, also returning the prover's statistics (what the CLI's benchmarks report).
pub fn aggregate_with_stats(
    children: &[EthereumProof],
    raw_lean_xmss: Vec<(LeanXmssPublicKey, lean_xmss::Message, LeanXmssSignature)>,
    raw_lean_sphincs: Vec<(LeanSphincsPublicKey, lean_sphincs::Message, LeanSphincsSignature)>,
    blobs: &[u64],
    declare: Option<&EthereumClaims>,
    log_inv_rate: usize,
) -> Result<(EthereumProof, leanvm_core::cpu::Stats), AggregationError> {
    let declared_root = declare.map(|claims| claims.da_commitment);
    // `cpu::prove` checks the rate too, but only after the witness is built.
    if !(leanvm_core::pcs::MIN_LOG_INV_RATE..=leanvm_core::pcs::MAX_LOG_INV_RATE).contains(&log_inv_rate) {
        return Err(AggregationError::InvalidRate { log_inv_rate });
    }
    if children.len() > MAX_RECURSIONS {
        return Err(AggregationError::TooLarge);
    }
    let direct = if blobs.is_empty() {
        None
    } else {
        let _span = tracing::info_span!("LeanDA commit").entered();
        Some(lean_da::commit(blobs)?)
    };
    let direct_root = direct.as_ref().map(|&(commitment, _)| commitment);
    if direct_root.is_some() && declared_root.is_some_and(|declared| declared != direct_root) {
        return Err(AggregationError::BlobsUnpublished);
    }
    // The published root and the guest's slot for it: a child's, or `children.len()` for
    // the node's own, its blobs' or none.
    let (da_pick, da_root) = match declared_root {
        _ if direct_root.is_some() => (children.len(), direct_root),
        None => children
            .iter()
            .position(|child| child.claims.da_commitment.is_some())
            .map_or((children.len(), None), |i| (i, children[i].claims.da_commitment)),
        Some(None) => (children.len(), None),
        Some(Some(root)) => {
            let i = children
                .iter()
                .position(|child| child.claims.da_commitment == Some(root))
                .ok_or(AggregationError::BlobNotCovered)?;
            (i, Some(root))
        }
    };

    let guest = unified_guest();
    // On the whole (pubkey, message) pair, so a signer may appear once per message. One
    // claim needs one signature, whatever leaf index a leanXMSS one carries.
    let mut raw_lean_xmss = raw_lean_xmss;
    raw_lean_xmss.sort_by_key(|(pk, message, _)| (*pk, *message));
    raw_lean_xmss.dedup_by(|(a, am, _), (b, bm, _)| (a, am) == (b, bm));
    let mut raw_lean_sphincs = raw_lean_sphincs;
    raw_lean_sphincs.sort_by_key(|(pk, message, _)| (*pk, *message));
    raw_lean_sphincs.dedup_by(|(a, am, _), (b, bm, _)| (a, am) == (b, bm));

    // The children are assumed checked. Nothing here verifies one: the replay
    // only reads out of its proof the summary `gen_verify` derives the guest's
    // witness from, pinning the Merkle paths it reads, and fails on a proof it
    // cannot walk. A child that does not
    // verify fails in the guest instead, which verifies every child in the VM,
    // so `prove` below returns the error. Its deferred claim is likewise not
    // recomputed: the batching sumcheck below forces every batched value to be
    // the true evaluation, refusing a false one, and the root discharges the one
    // claim they reduce to.
    let _span = tracing::info_span!("Replay children").entered();
    let mut public_inputs = Vec::with_capacity(children.len());
    let child_da: Vec<[u8; 32]> = children
        .iter()
        .map(|child| verifier::da::digest(child.claims.da_commitment))
        .collect();
    for child in children {
        check_signer_set(&child.claims).map_err(AggregationError::InvalidChild)?;
        public_inputs.push(child.vm_public_input());
    }
    // One single-threaded replay per worker: nothing inside dispatches.
    let bytecode = scalar_bytecode();
    let summaries = parallel::map_collect(children.len(), |i| {
        let public_input = public_input_bytes(&public_inputs[i]);
        verifier::replay_unchecked(bytecode, &public_input, &scalar_proof(&children[i].proof))
    });
    let mut replayed = Vec::with_capacity(children.len());
    for (pi, summary) in public_inputs.into_iter().zip(summaries) {
        let summary =
            summary.map_err(|e| AggregationError::InvalidChild(AggregateVerifyError::Snark(SnarkVerifyError(e))))?;
        replayed.push((pi, summary));
    }

    drop(_span);

    let _span = tracing::info_span!("Build witness").entered();
    let raw_xmss_claims: Vec<LeanXmssClaim> = raw_lean_xmss.iter().map(|(pk, message, _)| (*pk, *message)).collect();
    let raw_lean_sphincs_claims: Vec<LeanSphincsClaim> = raw_lean_sphincs
        .iter()
        .map(|(pk, message, _)| (*pk, *message))
        .collect();
    let cover = plan_coverage(&raw_xmss_claims, &raw_lean_sphincs_claims, children, declare)?;

    let mut hints = Hints::default();
    hints.push(
        "meta",
        vec![
            count(cover.xmss.signers.len()),
            count(cover.xmss.dups.len()),
            count(raw_lean_xmss.len()),
            count(cover.lean_sphincs.signers.len()),
            count(cover.lean_sphincs.dups.len()),
            count(raw_lean_sphincs.len()),
            count(children.len()),
            count(usize::from(!blobs.is_empty())),
        ],
    );
    let fs_seed = leanvm_core::cpu::fs_seed(guest);
    hints.push("fs_seed", vec![fs_seed[0], fs_seed[1]]);
    // Both lists in the guest's order, leanXMSS first: each one's split, its declared
    // claims, then its duplicates.
    let lean_xmss_cells: Vec<[F192; 4]> = cover.xmss.signers.iter().map(lean_xmss_claim_cells).collect();
    let lean_sphincs_cells: Vec<[F192; 4]> = cover
        .lean_sphincs
        .signers
        .iter()
        .map(lean_sphincs_claim_cells)
        .collect();
    for cells in [&lean_xmss_cells, &lean_sphincs_cells] {
        if !cells.is_empty() {
            hints.push("signers_split", list_split(cells.len()));
        }
        for claim in cells {
            hints.push("claims", claim.to_vec());
        }
    }
    let dups = cover.xmss.dups.iter().map(lean_xmss_claim_cells);
    for claim in dups.chain(cover.lean_sphincs.dups.iter().map(lean_sphincs_claim_cells)) {
        hints.push("dup_claims", claim.to_vec());
    }
    // Each slot is hinted as an offset into its scheme's region, which is how one
    // range check keeps a scheme's writers off the other's claims.
    let lean_xmss_hints = parallel::map_collect(raw_lean_xmss.len(), |i| {
        let (pk, message, sig) = &raw_lean_xmss[i];
        lean_xmss_hints(pk, sig, message)
    });
    for (&offset, sub) in cover.xmss.raw.iter().zip(lean_xmss_hints) {
        hints.push("raw_index", vec![count(offset)]);
        for (name, entry) in sub? {
            hints.push(name, entry);
        }
    }
    let lean_sphincs_hints = parallel::map_collect(raw_lean_sphincs.len(), |i| {
        let (pk, message, sig) = &raw_lean_sphincs[i];
        lean_sphincs_hints(pk, sig, message)
    });
    for (&offset, sub) in cover.lean_sphincs.raw.iter().zip(lean_sphincs_hints) {
        hints.push("sp_raw_index", vec![count(offset)]);
        for (name, entry) in sub? {
            hints.push(name, entry);
        }
    }

    // Both claims of each child, in the batch's order: the fresh one, then the one it carried. The fresh
    // claim's value is read off the sums the batch runs on.
    let claim_points: Vec<Vec<F192>> = replayed
        .iter()
        .zip(children)
        .flat_map(|((_, summary), child)| {
            let fresh = summary.bytecode_point.iter().copied().map(from_scalar).collect();
            [fresh, child.defer.bytecode_point.clone()]
        })
        .collect();
    let sums = bytecode_sums(&claim_points);
    // Each child's witness is its own, one child per worker: nothing inside dispatches.
    let verified = parallel::map_collect(children.len(), |i| {
        let (pi, summary) = &replayed[i];
        let value = bytecode_value(&sums[2 * i], &claim_points[2 * i]);
        gen_verify(guest, *pi, &children[i].proof, summary, value)
    });
    let mut subs = Vec::with_capacity(children.len());
    let mut carried = Vec::with_capacity(children.len());
    for (i, (child, verified)) in children.iter().zip(verified).enumerate() {
        hints.push(
            "child_meta",
            vec![count(child.claims.xmss.len()), count(child.claims.lean_sphincs.len())],
        );
        for offsets in [&cover.xmss.children[i], &cover.lean_sphincs.children[i]] {
            if offsets.is_empty() {
                continue;
            }
            hints.push("signers_split", list_split(offsets.len()));
            // Strictly increasing slots go as gaps (`child_claim_list_mono`).
            if offsets.is_sorted_by(|a, b| a < b) {
                hints.push("child_mono", vec![count(1)]);
                let mut next = 0;
                for &offset in offsets {
                    hints.push("child_claim_gap", vec![count(offset - next)]);
                    next = offset + 1;
                }
            } else {
                hints.push("child_mono", vec![count(0)]);
                for &offset in offsets {
                    hints.push("child_claim_index", vec![count(offset)]);
                }
            }
        }
        hints.push("child_defer", child.defer.cells());
        hints.push("child_da", hash_to_scalars(&child_da[i]).to_vec());
        let (sub_hints, defer) = verified?;
        for (name, entry) in sub_hints {
            hints.push(name, entry);
        }
        subs.push(defer);
        carried.push(&child.defer);
    }

    drop(_span);

    let defer = if children.is_empty() {
        let leaf = DeferredClaim::leaf();
        hints.push("leaf_defer", vec![leaf.bytecode_value]);
        leaf
    } else {
        let _span = tracing::info_span!("Batch deferred claims").entered();
        let (agg_hints, reduced) =
            aggregate_deferred_claims(&subs, &carried, sums).map_err(AggregationError::InvalidChild)?;
        for (name, entry) in agg_hints {
            hints.push(name, entry);
        }
        reduced
    };

    let mut da = child_da.get(da_pick).copied().unwrap_or_default();
    if let Some((commitment, witness)) = direct {
        let n_rows = blobs.len() / BLOB_SYMBOLS;
        da = verifier::da::digest(Some(commitment));
        hints.push(
            "da_challenge",
            vec![from_scalar(verifier::da::membership_challenge(&commitment))],
        );
        hints.push(
            "da_shape",
            vec![count(n_rows), count(n_rows.next_power_of_two().ilog2() as usize)],
        );
        // Padding rows are constants the guest bakes, so only the real rows'
        // symbols ride the stream.
        let (c, m) = (CELL_SYMBOLS, CODEWORD_SYMBOLS);
        for j in 0..CELLS_PER_ROW {
            for i in 0..n_rows {
                hints.push(
                    "da_symbols",
                    witness.codewords[i * m + j * c..i * m + (j + 1) * c]
                        .iter()
                        .map(|&w| F192::from(F64(w)))
                        .collect(),
                );
            }
        }
    }
    hints.push("da_pick", vec![count(da_pick)]);
    if cover.xmss.signers.is_empty() && cover.lean_sphincs.signers.is_empty() && da_root.is_none() {
        return Err(AggregationError::Empty);
    }

    let public_input = statement_digest(&cover.xmss.signers, &cover.lean_sphincs.signers, da, &defer);
    let mut program = guest.clone();
    #[cfg(test)]
    if let Some(forge) = FORGE.with(|forge| forge.borrow_mut().take()) {
        forge(&mut hints);
    }
    hints.install(&mut program);
    let (proof, stats) =
        prove(&program, public_input, log_inv_rate).map_err(|e| AggregationError::ProofError(SnarkProveError(e)))?;
    Ok((
        EthereumProof {
            claims: EthereumClaims {
                xmss: cover.xmss.signers,
                lean_sphincs: cover.lean_sphincs.signers,
                da_commitment: da_root,
            },
            defer,
            proof,
        },
        stats,
    ))
}
