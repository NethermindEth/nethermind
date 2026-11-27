// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Body = Nethermind.BeaconChain.Legacy.Deneb.BeaconBlockBody;

namespace Nethermind.BeaconChain.Legacy.Bellatrix;

/// <summary>The Bellatrix state transition: execution payloads and the final Altair penalties.</summary>
/// <remarks>Checking the terminal proof-of-work block is fork choice, not state transition, and is not replayed.</remarks>
internal class Transition(BeaconChainSpec spec) : Altair.Transition(spec)
{
    private static readonly Hash256 s_emptyPayloadRoot = SszRoots.HashTreeRoot(new ExecutionPayload { ExtraData = [], Transactions = [] });
    private static readonly Hash256 s_emptyHeaderRoot = SszRoots.HashTreeRoot(new ExecutionPayloadHeader { ExtraData = [] });

    public override LegacyFork Fork => LegacyFork.Bellatrix;
    protected override ulong MinSlashingPenaltyQuotient => LegacyPresets.MinSlashingPenaltyQuotientBellatrix;
    protected override ulong ProportionalSlashingMultiplier => Presets.ProportionalSlashingMultiplierBellatrix;

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

    protected override void UpgradeFields(BeaconStateFulu state, EpochCache cache) =>
        state.LatestExecutionPayloadHeader = new ExecutionPayloadHeader { ExtraData = [] };

    public override void ProcessPayload(BeaconStateFulu state, Body body, INewPayloadNotifier notifier) =>
        ProcessExecutionPayload(state, body, notifier);

    /// <summary>Spec <c>process_execution_payload</c>, skipped before the merge.</summary>
    public virtual void ProcessExecutionPayload(BeaconStateFulu state, Body body, INewPayloadNotifier notifier)
    {
        bool isMergeComplete = SszRoots.HashTreeRoot((ExecutionPayloadHeader)state.LatestExecutionPayloadHeader!) != s_emptyHeaderRoot;
        if (!isMergeComplete)
        {
            ExecutionPayload payload = (ExecutionPayload)body.ExecutionPayload!;
            if (SszRoots.HashTreeRoot(payload) == s_emptyPayloadRoot)
                return;
            // The merge transition block's parent is a proof-of-work block, which the state does not know.
            state.LatestExecutionPayloadHeader = new ExecutionPayloadHeader { BlockHash = payload.ParentHash };
        }
        BlockProcessing.ProcessExecutionPayload(state, body, notifier, maxBlobsPerBlock: 0);
        state.LatestExecutionPayloadHeader = CopyHeader<ExecutionPayloadHeader>(state.LatestExecutionPayloadHeader!);
    }

    public override void ApplyRewardDeltas(BeaconStateFulu state, EpochCache cache) =>
        EpochProcessing.ApplyRewardDeltas(state, cache);

    /// <summary>Copies the fields every fork's payload header has.</summary>
    protected static THeader CopyHeader<THeader>(Types.ExecutionPayloadHeader header) where THeader : Types.ExecutionPayloadHeader, new() => new()
    {
        ParentHash = header.ParentHash,
        FeeRecipient = header.FeeRecipient,
        StateRoot = header.StateRoot,
        ReceiptsRoot = header.ReceiptsRoot,
        LogsBloom = header.LogsBloom,
        PrevRandao = header.PrevRandao,
        BlockNumber = header.BlockNumber,
        GasLimit = header.GasLimit,
        GasUsed = header.GasUsed,
        Timestamp = header.Timestamp,
        ExtraData = header.ExtraData,
        BaseFeePerGas = header.BaseFeePerGas,
        BlockHash = header.BlockHash,
        TransactionsRoot = header.TransactionsRoot,
    };
}
