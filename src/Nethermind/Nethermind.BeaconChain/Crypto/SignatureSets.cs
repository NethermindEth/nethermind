// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using G1Affine = Nethermind.Crypto.Bls.P1Affine;

namespace Nethermind.BeaconChain.Crypto;

/// <summary>
/// Block-processing signature verifications over <see cref="BeaconStateFulu"/>
/// (the equivalents of Lighthouse's <c>signature_sets.rs</c>).
/// </summary>
/// <remarks>
/// All helpers return <c>false</c> instead of throwing for malformed points or signatures; the
/// surrounding spec asserts are the caller's responsibility. Pubkeys of registered validators come
/// decompressed from <see cref="PubkeyCache"/>; pubkeys carried by the message itself
/// (BLS-to-execution changes, sync committee members) are decompressed on the fly. Deposits are
/// handled separately by <see cref="DepositSignatureVerifier"/>.
/// </remarks>
public static class SignatureSets
{
    /// <summary>Compressed BLS G2 point at infinity — the spec's "empty" signature placeholder.</summary>
    internal static readonly byte[] G2PointAtInfinity = CreateG2PointAtInfinity();

    private static byte[] CreateG2PointAtInfinity()
    {
        byte[] bytes = new byte[BlsSignature.Length];
        bytes[0] = 0xc0;
        return bytes;
    }

    /// <summary>
    /// Verifies the aggregate attester signature of an indexed attestation over
    /// <c>DOMAIN_BEACON_ATTESTER</c> at the attestation's target epoch (the signature half of
    /// spec <c>is_valid_indexed_attestation</c>).
    /// </summary>
    /// <remarks>The index structure (sorted, unique, in range) must already be validated by the caller.</remarks>
    public static bool VerifyIndexedAttestation(BeaconStateFulu state, IndexedAttestation attestation, PubkeyCache pubkeys)
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

    /// <summary>Verifies a block's proposer signature over <c>DOMAIN_BEACON_PROPOSER</c> at the block-slot epoch.</summary>
    public static bool VerifyProposerSignature(BeaconStateFulu state, SignedBeaconBlock block, PubkeyCache pubkeys) =>
        VerifyProposerSignature(state, block.Message!, block.Signature, pubkeys);

    /// <inheritdoc cref="VerifyProposerSignature(BeaconStateFulu, SignedBeaconBlock, PubkeyCache)"/>
    public static bool VerifyProposerSignature(BeaconStateFulu state, BeaconBlock block, BlsSignature signature, PubkeyCache pubkeys)
    {
        Hash256 domain = state.GetDomain(DomainType.BeaconProposer, BeaconStateAccessors.ComputeEpochAtSlot(block.Slot));
        Hash256 signingRoot = Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(block), domain);
        return Verify(pubkeys.GetPublicKey((int)block.ProposerIndex), signature, signingRoot);
    }

    /// <summary>Verifies a signed block header against its claimed proposer (used by proposer slashings).</summary>
    public static bool VerifySignedBeaconBlockHeader(BeaconStateFulu state, SignedBeaconBlockHeader signedHeader, PubkeyCache pubkeys)
    {
        BeaconBlockHeader header = signedHeader.Message!;
        Hash256 domain = state.GetDomain(DomainType.BeaconProposer, BeaconStateAccessors.ComputeEpochAtSlot(header.Slot));
        Hash256 signingRoot = Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(header), domain);
        return Verify(pubkeys.GetPublicKey((int)header.ProposerIndex), signedHeader.Signature, signingRoot);
    }

    /// <summary>Verifies the proposer's RANDAO reveal: a signature over the epoch number under <c>DOMAIN_RANDAO</c>.</summary>
    public static bool VerifyRandaoReveal(BeaconStateFulu state, int proposerIndex, ulong epoch, BlsSignature reveal, PubkeyCache pubkeys)
    {
        // hash_tree_root(epoch): a single little-endian uint64 chunk.
        Span<byte> epochRoot = stackalloc byte[32];
        BinaryPrimitives.WriteUInt64LittleEndian(epochRoot, epoch);

        Hash256 domain = state.GetDomain(DomainType.Randao, epoch);
        Hash256 signingRoot = Domains.ComputeSigningRoot(new Hash256(epochRoot), domain);
        return Verify(pubkeys.GetPublicKey(proposerIndex), reveal, signingRoot);
    }

    /// <summary>Verifies a voluntary exit signature over the EIP-7044 fork-agnostic Capella domain.</summary>
    public static bool VerifyVoluntaryExit(BeaconStateFulu state, SignedVoluntaryExit signedExit, PubkeyCache pubkeys)
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
    public static bool VerifyBlsToExecutionChange(BeaconStateFulu state, SignedBlsToExecutionChange signedChange)
    {
        BlsToExecutionChange change = signedChange.Message!;
        Hash256 domain = Domains.ComputeDomain(DomainType.BlsToExecutionChange, Presets.GenesisForkVersion, state.GenesisValidatorsRoot!);
        Hash256 signingRoot = Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(change), domain);

        G1Affine pubkey = new(stackalloc long[G1Affine.Sz]);
        return pubkey.TryDecode(change.FromBlsPubkey.Bytes, out _) && Verify(pubkey, signedChange.Signature, signingRoot);
    }

    /// <summary>
    /// Verifies a sync aggregate: the participants' signature over the block root at the slot
    /// before the state's slot, under <c>DOMAIN_SYNC_COMMITTEE</c>.
    /// </summary>
    /// <remarks>
    /// Participant pubkeys are decompressed from the current sync committee's stored 48-byte keys
    /// (committee members may repeat, so this cannot go through validator indices). Implements the
    /// <c>eth_fast_aggregate_verify</c> rule: with no participants, only the G2 point at infinity
    /// is a valid signature.
    /// </remarks>
    public static bool VerifySyncAggregate(BeaconStateFulu state, SyncAggregate syncAggregate)
    {
        BitArray bits = syncAggregate.SyncCommitteeBits!;
        BlsPublicKey[] committee = state.CurrentSyncCommittee!.Pubkeys!;

        BlsSigner.AggregatedPublicKey participants = new(stackalloc long[Bls.P1.Sz]);
        int participantCount = 0;
        for (int i = 0; i < bits.Length; i++)
        {
            if (!bits[i])
                continue;
            if (!participants.TryAggregate(committee[i].Bytes, out _))
                return false;
            participantCount++;
        }

        if (participantCount == 0)
            return syncAggregate.SyncCommitteeSignature.Bytes.SequenceEqual(G2PointAtInfinity);

        ulong previousSlot = Math.Max(state.Slot, 1) - 1;
        Hash256 domain = state.GetDomain(DomainType.SyncCommittee, BeaconStateAccessors.ComputeEpochAtSlot(previousSlot));
        Hash256 signingRoot = Domains.ComputeSigningRoot(state.GetBlockRootAtSlot(previousSlot), domain);
        return VerifyAggregate(participants, syncAggregate.SyncCommitteeSignature, signingRoot);
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
