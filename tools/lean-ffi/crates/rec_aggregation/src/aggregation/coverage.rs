//! The coverage table: which slot each raw signature and child claim writes.

use super::error::AggregationError;
use super::proof::{EthereumClaims, EthereumProof};
use super::{LeanSphincsClaim, LeanXmssClaim};
use std::collections::BTreeSet;

/// The coverage slot each write in the guest's coverage walk targets, in walk
/// order: the raw signatures first, then each child's claim lists.
///
/// The table is one contiguous region per scheme, each its declared claims followed
/// by its own duplicate slots:
///
/// | slots | holds |
/// | --- | --- |
/// | `[0, n_lean_xmss)` | the declared leanXMSS claims |
/// | `[n_lean_xmss, X)` | leanXMSS duplicate slots |
/// | `[X, X + n_lean_sphincs)` | the declared leanSPHINCS claims |
/// | `[X + n_lean_sphincs, n_total)` | leanSPHINCS duplicate slots |
///
/// A claim the declared set holds takes its slot there, one it does not takes a
/// fresh duplicate slot in the same region, so the walk hits every one of the
/// `n_total` slots exactly once. That bijection, enforced in-circuit by write-once
/// memory plus the final count, is what makes every declared claim covered by a real
/// signature or a verified child.
///
/// Keeping each region contiguous is what binds the scheme: the guest addresses
/// every writer as an offset into one region, bounded by that region's size, one
/// range check per write, so no signature can reach a claim of the other scheme.
pub(super) struct Coverage {
    pub(super) xmss: Region<LeanXmssClaim>,
    pub(super) lean_sphincs: Region<LeanSphincsClaim>,
}

/// One scheme's region of the coverage table.
pub(super) struct Region<K> {
    /// The declared claims, strictly sorted.
    pub(super) signers: Vec<K>,
    pub(super) dups: Vec<K>,
    /// The slot of each raw signature, in the order given.
    pub(super) raw: Vec<usize>,
    /// Per child, the slot of each of its claims.
    pub(super) children: Vec<Vec<usize>>,
}

impl<K: Ord + Clone> Region<K> {
    /// Declares the union of the raw claims and the children's, or exactly
    /// `declare`, which they must cover.
    fn plan<'a>(
        raw: &[K],
        children: impl Iterator<Item = &'a [K]> + Clone,
        declare: Option<&[K]>,
    ) -> Result<Self, AggregationError>
    where
        K: 'a,
    {
        let mut signers = raw.to_vec();
        for child in children.clone() {
            signers.extend_from_slice(child);
        }
        signers.sort();
        signers.dedup();
        if let Some(declare) = declare {
            let mut wanted: BTreeSet<&K> = declare.iter().collect();
            signers.retain(|claim| wanted.remove(claim));
            if !wanted.is_empty() {
                return Err(AggregationError::NotCovered);
            }
        }
        let mut claimed = vec![false; signers.len()];
        let mut dups = Vec::new();
        let raw = raw
            .iter()
            .map(|claim| take_slot(&signers, &mut claimed, &mut dups, claim))
            .collect();
        let children = children
            .map(|child| {
                child
                    .iter()
                    .map(|claim| take_slot(&signers, &mut claimed, &mut dups, claim))
                    .collect()
            })
            .collect();
        Ok(Self {
            signers,
            dups,
            raw,
            children,
        })
    }
}

/// A claim takes its declared slot once; subsequent or omitted occurrences take
/// fresh slots outside the hashed prefix.
fn take_slot<K: Ord + Clone>(claims: &[K], claimed: &mut [bool], duplicates: &mut Vec<K>, claim: &K) -> usize {
    match claims.binary_search(claim) {
        Ok(pos) if !claimed[pos] => {
            claimed[pos] = true;
            pos
        }
        _ => {
            duplicates.push(claim.clone());
            claims.len() + duplicates.len() - 1
        }
    }
}

pub(super) fn plan_coverage(
    raw_lean_xmss: &[LeanXmssClaim],
    raw_lean_sphincs: &[LeanSphincsClaim],
    children: &[EthereumProof],
    declare: Option<&EthereumClaims>,
) -> Result<Coverage, AggregationError> {
    let cover = Coverage {
        xmss: Region::plan(
            raw_lean_xmss,
            children.iter().map(|child| child.claims.xmss.as_slice()),
            declare.map(|claims| claims.xmss.as_slice()),
        )?,
        lean_sphincs: Region::plan(
            raw_lean_sphincs,
            children.iter().map(|child| child.claims.lean_sphincs.as_slice()),
            declare.map(|claims| claims.lean_sphincs.as_slice()),
        )?,
    };
    Ok(cover)
}
