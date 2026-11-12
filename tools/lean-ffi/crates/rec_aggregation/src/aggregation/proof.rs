//! The aggregate, its wire format and its native verification.

use super::deferred::{DeferredClaim, scalar_bytecode};
use super::error::{AggregateVerifyError, SnarkVerifyError};
use super::statement::statement_digest;
use super::{LeanSphincsClaim, LeanXmssClaim};
use leanvm_core::cpu::{public_input_bytes, scalar_proof};
use primitives::field::F192;

/// What an aggregate attests: every `(pubkey, message)` in [`Self::xmss`] is backed by a valid
/// leanXMSS signature, at a leaf index the statement does not record, every one in
/// [`Self::lean_sphincs`] by a valid leanSPHINCS signature, and the [`Self::da_commitment`], if any,
/// commits to blob rows that are valid Reed-Solomon codewords.
///
/// The two lists remain separate because the proof says which scheme verified which claim (the
/// module docs give the coverage argument). A proof's lists are each strictly sorted and deduplicated
/// on the whole `(pubkey, message)` pair, and [`EthereumProof::from_proof_bytes`] and
/// [`EthereumProof::verify`] reject claims that are not. Either list
/// may be empty, and both are in a blob-only proof.
///
/// **Neither length counts signers, only claims.** One pubkey may appear under several messages, so a
/// committee threshold has to count distinct pubkeys itself.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct EthereumClaims {
    pub xmss: Vec<LeanXmssClaim>,
    pub lean_sphincs: Vec<LeanSphincsClaim>,
    /// `None` makes no blob claim. The statement carries its [`verifier::da::digest`].
    pub da_commitment: Option<[u8; 32]>,
}

/// A proof of its [`EthereumClaims`], established directly or carried from verified child proofs.
///
/// Until [`Self::verify`] returns `Ok` they are claims, not attestation. A receiver that decoded the
/// proof with [`Self::from_proof_bytes`] supplied them itself; otherwise, as for [`super::aggregate`]'s
/// output, they are the prover's choice, and a caller has to compare them against what it expected.
#[derive(Clone, Debug)]
pub struct EthereumProof {
    pub(super) claims: EthereumClaims,
    /// What this aggregate defers to whoever discharges it: its parent, in
    /// circuit, or [`Self::verify`], natively.
    pub(super) defer: DeferredClaim,
    pub(super) proof: leanvm_core::cpu::Proof,
}

// The wire format: the deferred point as a little-endian u64 count of limb triples `c0, c1, c2`,
// each a little-endian u64, then the VM proof in its own encoding, to the end. The claims are not
// sent: the receiver knows what it expects a proof to attest, and supplies it.
//
// Given the claims, every byte string decodes to at most one aggregate and back to itself: the VM
// proof's decoder rejects trailing bytes. Otherwise anything downstream that dedupes or indexes on the
// serialized bytes could be made to see one aggregate as many.

pub(super) fn encode(point: &[F192], proof: &[u8]) -> Vec<u8> {
    let mut out = (point.len() as u64).to_le_bytes().to_vec();
    out.extend(point.iter().flat_map(|x| x.to_limbs()).flat_map(u64::to_le_bytes));
    out.extend(proof);
    out
}

/// The deferred point and the VM proof's bytes. The point's limbs are cut from the input before
/// any is read, so a forged count allocates nothing.
pub(super) fn decode(bytes: &[u8]) -> Option<(Vec<F192>, &[u8])> {
    let (count, rest) = bytes.split_first_chunk::<8>()?;
    let count = usize::try_from(u64::from_le_bytes(*count)).ok()?;
    let (limbs, proof) = rest.split_at_checked(count.checked_mul(24)?)?;
    let limb = |bytes: &[u8; 8]| u64::from_le_bytes(*bytes);
    let triples = limbs.as_chunks::<8>().0.as_chunks::<3>().0;
    let point = triples
        .iter()
        .map(|[c0, c1, c2]| F192::new(limb(c0), limb(c1), limb(c2)))
        .collect();
    Some((point, proof))
}

/// Reject a signer set that the coverage argument does not cover: strict sorting
/// within each list is what stops one claim being counted many times, so each
/// list's length is a count of distinct `(pubkey, message)` claims. Either list may be
/// empty; both may be empty for a blob proof. Their sizes are the guest's to bound, by its
/// memory.
pub(super) fn check_signer_set(claims: &EthereumClaims) -> Result<(), AggregateVerifyError> {
    if !claims.xmss.windows(2).all(|w| w[0] < w[1]) || !claims.lean_sphincs.windows(2).all(|w| w[0] < w[1]) {
        return Err(AggregateVerifyError::MalformedSignerSet);
    }
    Ok(())
}

impl EthereumClaims {
    /// The signature claims, as many as the coverage table's declared slots.
    ///
    /// NOT a count of distinct signers: a pubkey of either scheme may hold several
    /// claims, one per message it signed. A caller that wants signers has to
    /// deduplicate by pubkey itself.
    pub fn num_signature_claims(&self) -> usize {
        self.xmss.len() + self.lean_sphincs.len()
    }
}

impl EthereumProof {
    /// The VM's public input for this aggregate: the digest of its claims and its deferred claim.
    pub(super) fn vm_public_input(&self) -> [F192; 2] {
        let da = verifier::da::digest(self.claims.da_commitment);
        statement_digest(&self.claims.xmss, &self.claims.lean_sphincs, da, &self.defer)
    }

    /// What this aggregate claims, which [`Self::verify`] checks.
    pub fn claims(&self) -> &EthereumClaims {
        &self.claims
    }

    /// What goes on the wire: the deferred point and the VM proof. Neither the claims nor the
    /// deferred claim's value is transmitted: the receiver supplies the claims to
    /// [`Self::from_proof_bytes`], which recomputes the value, so there is nothing to lie about.
    pub fn proof_bytes(&self) -> Vec<u8> {
        encode(&self.defer.bytecode_point, &self.proof.to_bytes())
    }

    /// The aggregate `bytes` encode, as a proof of `claims`: what the receiver expects it to attest.
    /// Claims other than the aggregated ones fail [`Self::verify`].
    ///
    /// WARNING: Parsing does NOT verify: the proof is untouched, only shapes are checked.
    /// Call [`Self::verify`] before believing any of it.
    pub fn from_proof_bytes(bytes: &[u8], claims: EthereumClaims) -> Result<Self, AggregateVerifyError> {
        let (bytecode_point, proof) = decode(bytes).ok_or(AggregateVerifyError::MalformedEncoding)?;
        // Cheap rejections first. `recompute_scalar` below is a pass over the whole stacked
        // bytecode, on a point a peer chose, so anything decidable without it has to be
        // decided before it.
        check_signer_set(&claims)?;
        let proof = leanvm_core::cpu::Proof::from_bytes(proof).ok_or(AggregateVerifyError::MalformedEncoding)?;
        Ok(Self {
            claims,
            // The wire carries no value, only the point to derive it from.
            defer: DeferredClaim::recompute_scalar(bytecode_point)?,
            proof,
        })
    }

    /// Verify the aggregate's internal consistency: the signer set is well
    /// formed, the deferred bytecode claim holds at its
    /// transmitted point, and the VM proof satisfies the statement built from
    /// all of it.
    ///
    /// This says "every `(pubkey, message)` in the claims' `xmss` is a valid leanXMSS signature,
    /// and every one in their `lean_sphincs` a valid leanSPHINCS signature", and that the
    /// `da_commitment`, if any, commits to a well-formed blob matrix: exactly the claims held,
    /// which are the receiver's own after [`Self::from_proof_bytes`]. Which leaf index each
    /// leanXMSS signature used is not part of the claim.
    ///
    /// Every field operation, the proof check and its Merkle hashing run in the `verifier` crate: one thread, no explicit SIMD.
    pub fn verify(&self) -> Result<(), AggregateVerifyError> {
        check_signer_set(&self.claims)?;
        // Discharging the deferred claim IS recomputing it: its value has
        // to be the true evaluation, or the recursion below proves nothing about
        // the bytecode.
        let _s = tracing::info_span!("Recompute deferred claims").entered();
        let defer = DeferredClaim::recompute_scalar(self.defer.bytecode_point.clone())?;
        drop(_s);
        if defer != self.defer {
            return Err(AggregateVerifyError::MalformedClaim);
        }
        let _s = tracing::info_span!("Statement digest").entered();
        let pi = self.vm_public_input();
        drop(_s);
        verifier::verify_execution(scalar_bytecode(), &public_input_bytes(&pi), &scalar_proof(&self.proof))
            .map(|_| ())
            .map_err(|e| AggregateVerifyError::Snark(SnarkVerifyError(e)))
    }
}
