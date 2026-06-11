// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;

namespace Nethermind.BeaconChain.StateTransition;

/// <summary>
/// The consensus-specs top-level <c>state_transition</c> function over <see cref="BeaconStateFulu"/>.
/// </summary>
public static class StateTransition
{
    /// <summary>
    /// Applies <paramref name="signedBlock"/> to <paramref name="state"/>: advances the state to
    /// the block's slot (running epoch processing at boundaries), verifies the proposer signature,
    /// runs <c>process_block</c>, and validates the block's claimed state root.
    /// </summary>
    /// <remarks>
    /// The proposer signature is verified against the state <em>after</em> slot processing, as the
    /// spec's <c>verify_block_signature(state, signed_block)</c> runs on the advanced state. On any
    /// failure the state is left partially mutated and must be discarded.
    /// </remarks>
    /// <param name="spec">Resolves the EIP-7892 blob schedule for the block's epoch.</param>
    /// <param name="validateResult">Whether to assert the block's state root matches the post-state (skip when producing blocks).</param>
    /// <param name="verifySignatures">Whether to verify the proposer and operation signatures (skip when replaying already-verified blocks).</param>
    /// <exception cref="BeaconStateException">The block is invalid.</exception>
    public static void Apply(
        BeaconStateFulu state,
        SignedBeaconBlock signedBlock,
        EpochCache cache,
        PubkeyCache pubkeys,
        INewPayloadNotifier notifier,
        BeaconChainSpec spec,
        bool validateResult = true,
        bool verifySignatures = true)
    {
        BeaconBlock block = signedBlock.Message!;
        SlotProcessing.ProcessSlots(state, block.Slot, cache);

        if (verifySignatures && !SignatureSets.VerifyProposerSignature(state, signedBlock, pubkeys))
            throw new BeaconStateException($"Invalid proposer signature for the block at slot {block.Slot}");

        // Pre-Fulu epochs have no blob schedule entry; the Electra limit applies.
        ulong maxBlobsPerBlock = spec.GetBlobParameters(state.GetCurrentEpoch())?.MaxBlobsPerBlock ?? spec.MaxBlobsPerBlockElectra;
        BlockProcessing.ProcessBlock(state, block, cache, pubkeys, notifier, maxBlobsPerBlock, verifySignatures);

        if (validateResult && block.StateRoot != SszRoots.HashTreeRoot(state))
            throw new BeaconStateException($"Block state root {block.StateRoot} does not match the post-state root");
    }
}
