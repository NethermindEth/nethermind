// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using G1Affine = Nethermind.Crypto.Bls.P1Affine;

namespace Nethermind.BeaconChain.Crypto;

/// <summary>
/// Block-operation signature verifications over <see cref="BeaconStateGloas"/>: the
/// <see cref="SignatureSets"/> helpers <see cref="GloasBlockProcessing"/>'s operations need, plus
/// the new payload-timeliness (PTC) attestation check.
/// </summary>
/// <remarks>
/// Same contract as <see cref="SignatureSets"/>: <c>false</c> for a malformed point or signature,
/// never a throw; the spec asserts around each check belong to the caller. Typed to the Gloas
/// state for the same reason <see cref="GloasStateAccessors"/> is - the two state classes are
/// deliberately unrelated by inheritance, and the domain lookups these helpers make read the
/// state's own fork and genesis validators root.
/// </remarks>
public static class GloasSignatureSets
{
    /// <summary>
    /// Verifies the aggregate attester signature of a Gloas indexed attestation over
    /// <c>DOMAIN_BEACON_ATTESTER</c> at the attestation's target epoch (the signature half of
    /// spec <c>is_valid_indexed_attestation</c>).
    /// </summary>
    /// <remarks>The index structure (sorted, unique, in range) must already be validated by the caller.</remarks>
    public static bool VerifyIndexedAttestation(BeaconStateGloas state, IndexedAttestationGloas attestation, PubkeyCache pubkeys)
    {
        BlsSigner.AggregatedPublicKey aggregate = new(stackalloc long[Bls.P1.Sz]);
        foreach (ulong index in attestation.AttestingIndices!)
        {
            aggregate.Aggregate(pubkeys.GetPublicKey((int)index));
        }

        Hash256 domain = state.GetDomain(DomainType.BeaconAttester, attestation.Data!.Target!.Epoch);
        Hash256 signingRoot = Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(attestation.Data), domain);
        return VerifyAggregate(aggregate, attestation.Signature, signingRoot);
    }

    /// <summary>
    /// Verifies the aggregate PTC signature of an indexed payload attestation over
    /// <c>DOMAIN_PTC_ATTESTER</c> at the epoch of the attested slot (the signature half of spec
    /// <c>is_valid_indexed_payload_attestation</c>).
    /// </summary>
    /// <remarks>
    /// The index list may repeat a validator (a PTC is sampled with replacement); the pubkey is
    /// then aggregated once per occurrence, exactly as the spec's <c>FastAggregateVerify</c> over
    /// the repeated pubkey list does. Indices must already be validated as in range by the caller.
    /// </remarks>
    public static bool VerifyIndexedPayloadAttestation(BeaconStateGloas state, IndexedPayloadAttestation attestation, PubkeyCache pubkeys)
    {
        BlsSigner.AggregatedPublicKey aggregate = new(stackalloc long[Bls.P1.Sz]);
        foreach (ulong index in attestation.AttestingIndices!)
        {
            aggregate.Aggregate(pubkeys.GetPublicKey((int)index));
        }

        Hash256 domain = state.GetDomain(DomainType.PtcAttester, BeaconStateAccessors.ComputeEpochAtSlot(attestation.Data!.Slot));
        Hash256 signingRoot = Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(attestation.Data), domain);
        return VerifyAggregate(aggregate, attestation.Signature, signingRoot);
    }

    /// <summary>Verifies a signed block header against its claimed proposer (used by proposer slashings).</summary>
    public static bool VerifySignedBeaconBlockHeader(BeaconStateGloas state, SignedBeaconBlockHeader signedHeader, PubkeyCache pubkeys)
    {
        BeaconBlockHeader header = signedHeader.Message!;
        Hash256 domain = state.GetDomain(DomainType.BeaconProposer, BeaconStateAccessors.ComputeEpochAtSlot(header.Slot));
        Hash256 signingRoot = Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(header), domain);
        return Verify(pubkeys.GetPublicKey((int)header.ProposerIndex), signedHeader.Signature, signingRoot);
    }

    /// <summary>Verifies a voluntary exit signature over the EIP-7044 fork-agnostic Capella domain.</summary>
    public static bool VerifyVoluntaryExit(BeaconStateGloas state, SignedVoluntaryExit signedExit, PubkeyCache pubkeys)
    {
        VoluntaryExit exit = signedExit.Message!;
        Hash256 domain = Domains.ComputeDomain(DomainType.VoluntaryExit, Presets.CapellaForkVersion, state.GenesisValidatorsRoot!);
        Hash256 signingRoot = Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(exit), domain);
        return Verify(pubkeys.GetPublicKey((int)exit.ValidatorIndex), signedExit.Signature, signingRoot);
    }

    /// <summary>
    /// Verifies a BLS-to-execution-change signature. Capella rule: the domain is fork-agnostic
    /// (genesis fork version with the state's genesis validators root) and the pubkey is the
    /// message's <c>from_bls_pubkey</c>, not a registered validator key.
    /// </summary>
    public static bool VerifyBlsToExecutionChange(BeaconStateGloas state, SignedBlsToExecutionChange signedChange)
    {
        BlsToExecutionChange change = signedChange.Message!;
        Hash256 domain = Domains.ComputeDomain(DomainType.BlsToExecutionChange, Presets.GenesisForkVersion, state.GenesisValidatorsRoot!);
        Hash256 signingRoot = Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(change), domain);

        G1Affine pubkey = new(stackalloc long[G1Affine.Sz]);
        return pubkey.TryDecode(change.FromBlsPubkey.Bytes, out _) && Verify(pubkey, signedChange.Signature, signingRoot);
    }

    private static bool Verify(G1Affine publicKey, BlsSignature signature, Hash256 signingRoot) =>
        BlsSigner.Verify(publicKey, signature.Bytes, signingRoot.Bytes);

    private static bool VerifyAggregate(BlsSigner.AggregatedPublicKey publicKey, BlsSignature signature, Hash256 signingRoot)
    {
        Bls.P2 point = new(stackalloc long[Bls.P2.Sz]);
        return point.TryDecode(signature.Bytes, out _)
            && BlsSigner.VerifyAggregate(publicKey, new BlsSigner.Signature(point), signingRoot.Bytes);
    }
}
