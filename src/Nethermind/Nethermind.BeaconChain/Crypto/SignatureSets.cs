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
/// (BLS-to-execution changes) are decompressed and <c>KeyValidate</c>d on the fly. Deposits are
/// handled separately by <see cref="DepositSignatureVerifier"/>. Given a <see cref="BlockSignatureBatch.Deferral"/>,
/// a helper defers its check to that batch under the caller's message, returning <c>false</c>
/// only for a signature the batch refuses at once.
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

    /// <summary>Upper bound on the indices of an indexed attestation: <c>MAX_VALIDATORS_PER_COMMITTEE * MAX_COMMITTEES_PER_SLOT</c>.</summary>
    internal const int MaxAttestingIndices = Presets.MaxValidatorsPerCommittee * Presets.MaxCommitteesPerSlot;

    /// <summary>
    /// Resolves a validator index taken from a message to its cached public key, which must be in the prime-order subgroup.
    /// </summary>
    /// <returns><c>false</c> when the index is not cached (a negative or past-the-end index included) or its key fails <c>KeyValidate</c>.</returns>
    internal static bool TryGetValidatorKey(PubkeyCache pubkeys, ulong validatorIndex, out G1Affine key)
    {
        if (validatorIndex >= (ulong)pubkeys.Count)
        {
            key = default;
            return false;
        }

        return pubkeys.TryGetValidPublicKey((int)validatorIndex, out key);
    }

    /// <summary>Writes the sum of the attesting validators' keys into <paramref name="sum"/>; <c>false</c> for an oversized list, an uncached index or a key outside G1.</summary>
    internal static bool TrySumAttestingKeys(PubkeyCache pubkeys, ulong[]? attestingIndices, Span<long> sum)
    {
        ReadOnlySpan<ulong> indices = attestingIndices;
        if (indices.Length > MaxAttestingIndices)
            return false;
        foreach (ulong index in indices)
        {
            if (index >= (ulong)pubkeys.Count)
                return false;
        }

        return pubkeys.TrySumValidPublicKeys(indices, sum);
    }

    /// <summary>
    /// Verifies the aggregate attester signature of an indexed attestation over
    /// <c>DOMAIN_BEACON_ATTESTER</c> at the attestation's target epoch (the signature half of
    /// spec <c>is_valid_indexed_attestation</c>).
    /// </summary>
    /// <remarks>The index structure (sorted, unique, in range) must already be validated by the caller.</remarks>
    public static bool VerifyIndexedAttestation(BeaconStateFulu state, IndexedAttestation attestation, PubkeyCache pubkeys, BlockSignatureBatch.Deferral? deferral = null)
    {
        Span<long> sum = stackalloc long[Bls.P1.Sz];
        if (!TrySumAttestingKeys(pubkeys, attestation.AttestingIndices, sum))
            return false;
        BlsSigner.AggregatedPublicKey aggregate = new(sum);

        Hash256 domain = state.GetDomain(DomainType.BeaconAttester, attestation.Data!.Target!.Epoch);
        Hash256 signingRoot = Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(attestation.Data), domain);
        return BlockSignatureBatch.Verify(aggregate.PublicKey, attestation.Signature, signingRoot, deferral);
    }

    /// <summary>Verifies a block's proposer signature over <c>DOMAIN_BEACON_PROPOSER</c> at the block-slot epoch.</summary>
    /// <exception cref="BeaconStateException">The proposer index is not a validator of <paramref name="state"/>, or <paramref name="pubkeys"/> has no key for it.</exception>
    public static bool VerifyProposerSignature(BeaconStateFulu state, SignedBeaconBlock block, PubkeyCache pubkeys) =>
        VerifyProposerSignature(state, block.Message!, block.Signature, pubkeys);

    /// <inheritdoc cref="VerifyProposerSignature(BeaconStateFulu, SignedBeaconBlock, PubkeyCache)"/>
    public static bool VerifyProposerSignature(BeaconStateFulu state, BeaconBlock block, BlsSignature signature, PubkeyCache pubkeys)
    {
        // verify_block_signature indexes state.validators with the untrusted proposer_index (p2p beacon_block: [REJECT] a valid validator index).
        ulong proposerIndex = block.ProposerIndex;
        if (proposerIndex >= (ulong)state.Validators!.Length)
            // ethereum/consensus-specs fulu/p2p-interface.md: "[REJECT] The proposer index is a valid validator index".
            throw new BeaconStateException($"Block proposer index {proposerIndex} is not a validator index (registry size {state.Validators.Length})") { RejectGossip = true };
        // Epoch processing inside process_slots can grow the registry past the cache.
        if (proposerIndex >= (ulong)pubkeys.Count)
            throw new BeaconStateException($"Block proposer index {proposerIndex} has no cached public key ({pubkeys.Count} cached)");
        Hash256 domain = state.GetDomain(DomainType.BeaconProposer, BeaconStateAccessors.ComputeEpochAtSlot(block.Slot));
        Hash256 signingRoot = Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(block), domain);
        return pubkeys.TryGetValidPublicKey((int)block.ProposerIndex, out G1Affine key) && Verify(key, signature, signingRoot);
    }

    /// <summary>Verifies a signed block header against its claimed proposer (used by proposer slashings).</summary>
    public static bool VerifySignedBeaconBlockHeader(BeaconStateFulu state, SignedBeaconBlockHeader signedHeader, PubkeyCache pubkeys, BlockSignatureBatch.Deferral? deferral = null)
    {
        BeaconBlockHeader header = signedHeader.Message!;
        Hash256 domain = state.GetDomain(DomainType.BeaconProposer, BeaconStateAccessors.ComputeEpochAtSlot(header.Slot));
        Hash256 signingRoot = Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(header), domain);
        return TryGetValidatorKey(pubkeys, header.ProposerIndex, out G1Affine key) && BlockSignatureBatch.Verify(key, signedHeader.Signature, signingRoot, deferral);
    }

    /// <summary>Verifies the proposer's RANDAO reveal: a signature over the epoch number under <c>DOMAIN_RANDAO</c>.</summary>
    public static bool VerifyRandaoReveal(BeaconStateFulu state, int proposerIndex, ulong epoch, BlsSignature reveal, PubkeyCache pubkeys, BlockSignatureBatch.Deferral? deferral = null)
    {
        // hash_tree_root(epoch): a single little-endian uint64 chunk.
        Span<byte> epochRoot = stackalloc byte[32];
        BinaryPrimitives.WriteUInt64LittleEndian(epochRoot, epoch);

        Hash256 domain = state.GetDomain(DomainType.Randao, epoch);
        Hash256 signingRoot = Domains.ComputeSigningRoot(new Hash256(epochRoot), domain);
        return TryGetValidatorKey(pubkeys, (ulong)proposerIndex, out G1Affine key) && BlockSignatureBatch.Verify(key, reveal, signingRoot, deferral);
    }

    /// <summary>Verifies a voluntary exit signature over the EIP-7044 fork-agnostic Capella domain.</summary>
    public static bool VerifyVoluntaryExit(BeaconStateFulu state, SignedVoluntaryExit signedExit, PubkeyCache pubkeys, BlockSignatureBatch.Deferral? deferral = null)
    {
        VoluntaryExit exit = signedExit.Message!;
        Hash256 domain = Domains.ComputeDomain(DomainType.VoluntaryExit, BeaconChainSpec.ForGenesisValidatorsRoot(state.GenesisValidatorsRoot!).CapellaForkVersion, state.GenesisValidatorsRoot!);
        Hash256 signingRoot = Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(exit), domain);
        return TryGetValidatorKey(pubkeys, exit.ValidatorIndex, out G1Affine key) && BlockSignatureBatch.Verify(key, signedExit.Signature, signingRoot, deferral);
    }

    /// <summary>
    /// Verifies a BLS-to-execution-change signature. Capella rule: the domain is fork-agnostic
    /// (genesis fork version with the state's genesis validators root) and the pubkey is the
    /// message's <c>from_bls_pubkey</c>, not a registered validator key.
    /// </summary>
    public static bool VerifyBlsToExecutionChange(BeaconStateFulu state, SignedBlsToExecutionChange signedChange, BlockSignatureBatch.Deferral? deferral = null)
    {
        BlsToExecutionChange change = signedChange.Message!;
        Hash256 domain = Domains.ComputeDomain(DomainType.BlsToExecutionChange, BeaconChainSpec.ForGenesisValidatorsRoot(state.GenesisValidatorsRoot!).GenesisForkVersion, state.GenesisValidatorsRoot!);
        Hash256 signingRoot = Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(change), domain);

        G1Affine pubkey = new(stackalloc long[G1Affine.Sz]);
        return BlsSignatureSet.TryKeyValidate(change.FromBlsPubkey.Bytes, pubkey) && BlockSignatureBatch.Verify(pubkey, signedChange.Signature, signingRoot, deferral);
    }

    /// <summary>
    /// Verifies a sync aggregate: the participants' signature over the block root at the slot
    /// before the state's slot, under <c>DOMAIN_SYNC_COMMITTEE</c>.
    /// </summary>
    /// <param name="committeeIndices">The validator index of each current sync committee member, in committee order.</param>
    /// <remarks>
    /// Implements the <c>eth_fast_aggregate_verify</c> rule: with no participants, only the G2 point
    /// at infinity is a valid signature. Participants whose keys sum to infinity are refused with any signature,
    /// as <c>CoreVerify</c> in the IETF draft runs <c>KeyValidate</c> on the aggregate key.
    /// </remarks>
    public static bool VerifySyncAggregate(BeaconStateFulu state, SyncAggregate syncAggregate, int[] committeeIndices, PubkeyCache pubkeys, BlockSignatureBatch.Deferral? deferral = null)
    {
        Bls.P1 participants = new(stackalloc long[Bls.P1.Sz]);
        int participantCount = AggregateSyncParticipants(syncAggregate.SyncCommitteeBits!, state.CurrentSyncCommittee!.Pubkeys!, committeeIndices, pubkeys, participants);
        if (participantCount < 0)
            return false;
        if (participantCount == 0)
            return syncAggregate.SyncCommitteeSignature.Bytes.SequenceEqual(G2PointAtInfinity);

        ulong previousSlot = Math.Max(state.Slot, 1) - 1;
        Hash256 domain = state.GetDomain(DomainType.SyncCommittee, BeaconStateAccessors.ComputeEpochAtSlot(previousSlot));
        Hash256 signingRoot = Domains.ComputeSigningRoot(state.GetBlockRootAtSlot(previousSlot), domain);
        return BlockSignatureBatch.Verify(participants.ToAffine(), syncAggregate.SyncCommitteeSignature, signingRoot, deferral);
    }

    /// <summary>
    /// Adds the public key of each sync committee member whose bit is set into <paramref name="participants"/>,
    /// once per set bit, as <c>eth_fast_aggregate_verify</c> over the participant pubkeys does.
    /// </summary>
    /// <remarks>
    /// A member's cached key is used only when it compresses to the committee's stored key; otherwise the stored key
    /// is decompressed. Either way <c>FastAggregateVerify</c>'s <c>KeyValidate</c> is applied to each member (the cache
    /// holds no infinity key and remembers its subgroup checks), so the result never depends on <paramref name="pubkeys"/>
    /// or <paramref name="committeeIndices"/>, where -1 marks a member missing from the registry.
    /// A repeated member is added once per bit, so the adds are sequential: <see cref="PubkeyCache.SumPublicKeys"/>
    /// requires distinct indices. The aggregate itself may be infinity, which the verification refuses.
    /// </remarks>
    /// <returns>The participant count, or -1 when a member key fails <c>KeyValidate</c> or an input is not <c>SYNC_COMMITTEE_SIZE</c> long.</returns>
    internal static int AggregateSyncParticipants(BitArray bits, BlsPublicKey[] committee, int[] committeeIndices, PubkeyCache pubkeys, Bls.P1 participants)
    {
        if (bits.Length != Presets.SyncCommitteeSize || committee.Length != Presets.SyncCommitteeSize || committeeIndices.Length != Presets.SyncCommitteeSize)
            return -1;

        G1Affine decoded = new(stackalloc long[G1Affine.Sz]);
        int participantCount = 0;
        for (int i = 0; i < bits.Length; i++)
        {
            if (!bits[i])
                continue;
            int index = committeeIndices[i];
            G1Affine member = decoded;
            if ((uint)index < (uint)pubkeys.Count && pubkeys.GetPublicKey(index).Compress().AsSpan().SequenceEqual(committee[i].Bytes))
            {
                if (!pubkeys.IsInSubgroup(index))
                    return -1;
                member = pubkeys.GetPublicKey(index);
            }
            else if (!BlsSignatureSet.TryKeyValidate(committee[i].Bytes, decoded))
            {
                return -1;
            }

            participants.Add(member);
            participantCount++;
        }

        return participantCount;
    }

    private static bool Verify(G1Affine publicKey, BlsSignature signature, Hash256 signingRoot) =>
        BlsSigner.Verify(publicKey, signature.Bytes, signingRoot.Bytes);
}
