//! The aggregate, its wire format and its native verification.

use super::deferred::{DeferredClaim, scalar_bytecode};
use super::error::{AggregateVerifyError, SnarkVerifyError};
use super::cells::cell_bytes;
use super::guest::unified_guest;
use super::generic::MAX_GENERIC_DEPENDENCIES;
use super::statement::{deps_digest_cells, statement_digest, statement_digest_of};
use fiat_shamir::merkle::hash_to_scalars;
use super::{GenericClaim, LeanSphincsClaim};
use leanvm_core::cpu::{public_input_bytes, scalar_proof};
use primitives::field::F192;

/// What an aggregate attests: every `(data_hash, vk)` in [`Self::generic`] is backed by a valid
/// leanVM execution proof of the program `vk` commits to on public input `data_hash`, every
/// `(message, pubkey)` in [`Self::lean_sphincs`] by a valid leanSPHINCS signature, and the [`Self::da_commitment`], if any,
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
    /// The leanSTARK (`0x11`) claims, at most [`MAX_GENERIC_DEPENDENCIES`].
    pub generic: Vec<GenericClaim>,
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
    if !claims.generic.windows(2).all(|w| w[0] < w[1])
        || !claims.lean_sphincs.windows(2).all(|w| w[0] < w[1])
        || claims.generic.len() > MAX_GENERIC_DEPENDENCIES
    {
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
        self.generic.len() + self.lean_sphincs.len()
    }
}

/// EIP-8288 `get_deps_hash` with `DEPS_HASH` = Keccak-256. `S` hashes `data_hash ‖ pkRoot ‖ pkSeed` for
/// each leanSPHINCS (`0x10`) dependency and `G` `data_hash ‖ vk` for each leanSTARK (`0x11`) one, each
/// list in the order given; the digest is `S` without leanSTARK dependencies (the published
/// `get_deps_hash`), else `keccak256(S ‖ G ‖ LEANSTARK_DEPS_DOMAIN)`. The caller canonicalizes (sorts,
/// deduplicates) each list.
pub fn deps_hash(lean_sphincs: &[LeanSphincsClaim], generic: &[GenericClaim]) -> [u8; 32] {
    cell_bytes(deps_digest_cells(lean_sphincs, generic))
        .try_into()
        .expect("two 16-byte cells")
}

/// What stands for EIP-8288 `AGGREGATED_VK`: the guest's Fiat-Shamir IV,
/// `keccak256(verifier::TAG ‖ stacked bytecode table)`, which seeds every transcript and leads the statement.
pub fn aggregated_vk() -> [u8; 32] {
    cell_bytes(leanvm_core::cpu::fs_seed(unified_guest()))
        .try_into()
        .expect("two 16-byte cells")
}

/// The VM public input of a dependency-only aggregate with dependency digest `deps_hash`:
/// `keccak256(AGGREGATED_VK ‖ deps_hash ‖ 0^32 ‖ deferred cells)`.
fn deps_public_input(deps_hash: &[u8; 32], defer: &DeferredClaim) -> [F192; 2] {
    statement_digest_of(hash_to_scalars(deps_hash), [0; 32], defer)
}

/// The 32-byte VM public input [`verify_deps_hash`] checks `proof_bytes` against, and the deferred
/// cells' bytes that end its preimage (three little-endian limbs a cell, zero-padded to an even count).
pub fn deps_public_input_bytes(
    deps_hash: &[u8; 32],
    proof_bytes: &[u8],
) -> Result<([u8; 32], Vec<u8>), AggregateVerifyError> {
    let (point, _) = decode(proof_bytes).ok_or(AggregateVerifyError::MalformedEncoding)?;
    let defer = DeferredClaim::recompute_scalar(point)?;
    let mut cells = defer.cells();
    cells.resize(cells.len().next_multiple_of(2), F192::ZERO);
    let tail = cells.iter().flat_map(|x| x.to_limbs()).flat_map(u64::to_le_bytes).collect();
    Ok((public_input_bytes(&deps_public_input(deps_hash, &defer)), tail))
}

/// EIP-8288 verification: `verify_leanSTARK((deps_hash, AGGREGATED_VK), AGGREGATED_VK, proof)`, holding
/// only the digest and the proof. An empty dependency list has the empty proof.
pub fn verify_deps_hash(deps_hash: &[u8; 32], proof_bytes: &[u8]) -> Result<(), AggregateVerifyError> {
    if proof_bytes.is_empty() {
        return if *deps_hash == primitives::hash::hash(&[]) {
            Ok(())
        } else {
            Err(AggregateVerifyError::MalformedEncoding)
        };
    }
    let (point, proof) = decode(proof_bytes).ok_or(AggregateVerifyError::MalformedEncoding)?;
    let proof = leanvm_core::cpu::Proof::from_bytes(proof).ok_or(AggregateVerifyError::MalformedEncoding)?;
    let defer = DeferredClaim::recompute_scalar(point)?;
    let pi = deps_public_input(deps_hash, &defer);
    verifier::verify_execution(scalar_bytecode(), &public_input_bytes(&pi), &scalar_proof(&proof))
        .map(|_| ())
        .map_err(|e| AggregateVerifyError::Snark(SnarkVerifyError(e)))
}

impl EthereumProof {
    /// The VM's public input for this aggregate: the digest of its claims and its deferred claim.
    pub(super) fn vm_public_input(&self) -> [F192; 2] {
        let da = verifier::da::digest(self.claims.da_commitment);
        statement_digest(&self.claims.generic, &self.claims.lean_sphincs, da, &self.defer)
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
    /// This says "the program each `(data_hash, vk)` in the claims' `generic` commits to accepts
    /// `data_hash`, and every `(message, pubkey)` in their `lean_sphincs` is a valid leanSPHINCS
    /// signature", and that the `da_commitment`, if any, commits to a well-formed blob matrix:
    /// exactly the claims held, which are the receiver's own after [`Self::from_proof_bytes`].
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
