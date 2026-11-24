// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;

namespace Nethermind.BeaconChain.Legacy.Deneb;

/// <summary>The Deneb state transition: blobs (EIP-4844), perpetual exits (EIP-7044), attestation inclusion (EIP-7045) and the activation churn cap (EIP-7514).</summary>
internal class Transition(BeaconChainSpec spec) : Capella.Transition(spec)
{
    public override LegacyFork Fork => LegacyFork.Deneb;

    public override LegacyBlock DecodeBlock(ReadOnlySpan<byte> ssz)
    {
        SignedBeaconBlock.Decode(ssz, out SignedBeaconBlock block);
        return new LegacyBlock(block, SszRoots.HashTreeRoot(block.Message!.Body!));
    }

    protected override BeaconStateFulu DecodeStateContainer(ReadOnlySpan<byte> ssz)
    {
        BeaconState.Decode(ssz, out BeaconState state);
        return state;
    }

    protected override void UpgradeFields(BeaconStateFulu state, EpochCache cache)
    {
        ExecutionPayloadHeader header = CopyHeader<ExecutionPayloadHeader>(state.LatestExecutionPayloadHeader!);
        header.WithdrawalsRoot = state.LatestExecutionPayloadHeader!.WithdrawalsRoot;
        state.LatestExecutionPayloadHeader = header;
    }

    public override void ProcessExecutionPayload(BeaconStateFulu state, BeaconBlockBody body, INewPayloadNotifier notifier) =>
        BlockProcessing.ProcessExecutionPayload(state, body, notifier, LegacyPresets.MaxBlobsPerBlockDeneb);

    protected override ulong GetLastInclusionSlot(ulong slot) => ulong.MaxValue;

    protected override ulong GetActivationChurnLimit(BeaconStateFulu state, EpochCache cache) => state.GetValidatorActivationChurnLimit(cache);

    protected override bool VerifyVoluntaryExit(BeaconStateFulu state, SignedVoluntaryExit signedExit, PubkeyCache pubkeys, BlockSignatureBatch.Deferral? deferral) =>
        SignatureSets.VerifyVoluntaryExit(state, signedExit, pubkeys, deferral, Spec);
}
