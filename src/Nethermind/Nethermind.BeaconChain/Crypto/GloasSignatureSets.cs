// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
/// Same contract as <see cref="SignatureSets"/>, deferral to a <see cref="BlockSignatureBatch"/>
/// included: <c>false</c> for a malformed point or signature, never a throw; the spec asserts around each check belong to the caller. Typed to the Gloas
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
    public static bool VerifyIndexedAttestation(BeaconStateGloas state, IndexedAttestationGloas attestation, PubkeyCache pubkeys, BlockSignatureBatch.Deferral? deferral = null)
    {
        Span<long> sum = stackalloc long[Bls.P1.Sz];
        if (!pubkeys.TrySumValidPublicKeys(attestation.AttestingIndices, sum))
            return false;
        BlsSigner.AggregatedPublicKey aggregate = new(sum);

        Hash256 domain = state.GetDomain(DomainType.BeaconAttester, attestation.Data!.Target!.Epoch);
        Hash256 signingRoot = Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(attestation.Data), domain);
        return BlockSignatureBatch.Verify(aggregate.PublicKey, attestation.Signature, signingRoot, deferral);
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
    public static bool VerifyIndexedPayloadAttestation(BeaconStateGloas state, IndexedPayloadAttestation attestation, PubkeyCache pubkeys, BlockSignatureBatch.Deferral? deferral = null)
    {
        BlsSigner.AggregatedPublicKey aggregate = new(stackalloc long[Bls.P1.Sz]);
        foreach (ulong index in attestation.AttestingIndices!)
        {
            if (!pubkeys.TryGetValidPublicKey((int)index, out G1Affine key))
                return false;
            aggregate.Aggregate(key);
        }

        Hash256 domain = state.GetDomain(DomainType.PtcAttester, BeaconStateAccessors.ComputeEpochAtSlot(attestation.Data!.Slot));
        Hash256 signingRoot = Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(attestation.Data), domain);
        return BlockSignatureBatch.Verify(aggregate.PublicKey, attestation.Signature, signingRoot, deferral);
    }

    /// <summary>Verifies a signed block header against its claimed proposer (used by proposer slashings).</summary>
    public static bool VerifySignedBeaconBlockHeader(BeaconStateGloas state, SignedBeaconBlockHeader signedHeader, PubkeyCache pubkeys, BlockSignatureBatch.Deferral? deferral = null)
    {
        BeaconBlockHeader header = signedHeader.Message!;
        Hash256 domain = state.GetDomain(DomainType.BeaconProposer, BeaconStateAccessors.ComputeEpochAtSlot(header.Slot));
        Hash256 signingRoot = Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(header), domain);
        return pubkeys.TryGetValidPublicKey((int)header.ProposerIndex, out G1Affine key) && BlockSignatureBatch.Verify(key, signedHeader.Signature, signingRoot, deferral);
    }

    /// <summary>Verifies a voluntary exit signature over the EIP-7044 fork-agnostic Capella domain.</summary>
    public static bool VerifyVoluntaryExit(BeaconStateGloas state, SignedVoluntaryExit signedExit, PubkeyCache pubkeys, BlockSignatureBatch.Deferral? deferral = null)
    {
        VoluntaryExit exit = signedExit.Message!;
        Hash256 domain = Domains.ComputeDomain(DomainType.VoluntaryExit, BeaconChainSpec.ForGenesisValidatorsRoot(state.GenesisValidatorsRoot!).CapellaForkVersion, state.GenesisValidatorsRoot!);
        Hash256 signingRoot = Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(exit), domain);
        return pubkeys.TryGetValidPublicKey((int)exit.ValidatorIndex, out G1Affine key) && BlockSignatureBatch.Verify(key, signedExit.Signature, signingRoot, deferral);
    }

    /// <summary>
    /// Verifies a BLS-to-execution-change signature. Capella rule: the domain is fork-agnostic
    /// (genesis fork version with the state's genesis validators root) and the pubkey is the
    /// message's <c>from_bls_pubkey</c>, not a registered validator key.
    /// </summary>
    public static bool VerifyBlsToExecutionChange(BeaconStateGloas state, SignedBlsToExecutionChange signedChange, BlockSignatureBatch.Deferral? deferral = null)
    {
        BlsToExecutionChange change = signedChange.Message!;
        Hash256 domain = Domains.ComputeDomain(DomainType.BlsToExecutionChange, BeaconChainSpec.ForGenesisValidatorsRoot(state.GenesisValidatorsRoot!).GenesisForkVersion, state.GenesisValidatorsRoot!);
        Hash256 signingRoot = Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(change), domain);

        G1Affine pubkey = new(stackalloc long[G1Affine.Sz]);
        return BlsSignatureSet.TryKeyValidate(change.FromBlsPubkey.Bytes, pubkey) && BlockSignatureBatch.Verify(pubkey, signedChange.Signature, signingRoot, deferral);
    }
}
