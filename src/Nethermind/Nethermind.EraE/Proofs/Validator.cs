// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using EraVerificationException = Nethermind.Era1.Exceptions.EraVerificationException;

namespace Nethermind.EraE.Proofs;

public class Validator
{
    private readonly IHistoricalSummariesProvider? _historicalSummariesProvider;
    private readonly IReadOnlyList<ValueHash256>? _trustedAccumulators;
    private readonly IReadOnlyList<ValueHash256>? _trustedHistoricalRoots;
    private readonly SlotTime? _slotTime;

    private const int SlotsPerHistoricalRoot = 8192;
    private const int GenIndexExecutionBlockProofBellatrix = 3228;
    private const int GenIndexExecutionBlockProofDeneb = 6444;
    private const ulong DenebSlot = 8_626_176;

    public Validator(
        ISpecProvider specProvider,
        IReadOnlyList<ValueHash256>? trustedAccumulators,
        IReadOnlyList<ValueHash256>? trustedHistoricalRoots,
        IHistoricalSummariesProvider? historicalSummariesProvider,
        IBlocksConfig? blocksConfig = null)
    {
        if (specProvider.BeaconChainGenesisTimestamp.HasValue)
        {
            ulong secondsPerSlot = blocksConfig?.SecondsPerSlot ?? 12;
            _slotTime = new SlotTime(
                specProvider.BeaconChainGenesisTimestamp.Value * 1000,
                new Timestamper(),
                TimeSpan.FromSeconds(secondsPerSlot),
                TimeSpan.FromSeconds(0));
        }
        _trustedAccumulators = trustedAccumulators;
        _trustedHistoricalRoots = trustedHistoricalRoots;
        _historicalSummariesProvider = historicalSummariesProvider;
    }

    public async Task VerifyContent(Block block, BlockHeaderProof proof)
    {
        switch (proof.ProofType)
        {
            case BlockHeaderProofType.BlockProofHistoricalHashesAccumulator:
                VerifyHashesAccumulator(block, proof);
                break;
            case BlockHeaderProofType.BlockProofHistoricalRoots:
                VerifyRoots(block, proof);
                break;
            case BlockHeaderProofType.BlockProofHistoricalSummaries:
                await VerifySummaries(block, proof);
                break;
        }
    }

    public bool VerifyAccumulator(long blockNumber, ValueHash256 accumulatorRoot)
    {
        if (!TrustedAccumulatorsProvided()) return true;
        ValueHash256? trusted = GetAccumulatorForEpoch(blockNumber / SlotsPerHistoricalRoot);
        if (trusted is null) throw new EraVerificationException("Trusted accumulator root was not provided.");
        return trusted.Equals(accumulatorRoot);
    }

    public async Task VerifyBlocksRootContext(BlocksRootContext context)
    {
        switch (context.AccumulatorType)
        {
            case AccumulatorType.HistoricalHashesAccumulator:
                if (!VerifyAccumulator(context.StartingBlockNumber, context.AccumulatorRoot))
                    throw new EraVerificationException("Computed accumulator does not match trusted accumulator.");
                break;

            case AccumulatorType.HistoricalRoots:
                if (_slotTime is null) throw new EraVerificationException("Beacon chain genesis timestamp is not available for HistoricalRoots verification.");
                long slot = (long)_slotTime.GetSlot(context.StartingBlockTimestamp!.Value);
                ValueHash256? trustedRoot = GetHistoricalRoot(slot);
                if (trustedRoot is null)
                    throw new EraVerificationException("Historical root not found.");
                if (!trustedRoot.Equals(context.HistoricalRoot))
                    throw new EraVerificationException("Computed historical root does not match trusted historical root.");
                break;

            case AccumulatorType.HistoricalSummaries:
                if (_slotTime is null) throw new EraVerificationException("Beacon chain genesis timestamp is not available for HistoricalSummaries verification.");
                long summarySlot = (long)_slotTime.GetSlot(context.StartingBlockTimestamp!.Value);
                HistoricalSummary? trustedSummary = await GetHistoricalSummary(summarySlot);
                if (trustedSummary is null)
                    throw new EraVerificationException("Historical summary not found.");
                if (!trustedSummary.Value.BlockSummaryRoot.Equals(context.HistoricalSummary.BlockSummaryRoot))
                    throw new EraVerificationException("Computed block summary root does not match trusted historical block summary root.");
                if (!trustedSummary.Value.StateSummaryRoot.Equals(context.HistoricalSummary.StateSummaryRoot))
                    throw new EraVerificationException("Computed state summary root does not match trusted historical state summary root.");
                break;
        }
    }

    private bool IsDeneb(ulong blockTimestamp) =>
        _slotTime is not null && _slotTime.GetSlot(blockTimestamp) >= DenebSlot;

    private bool TrustedAccumulatorsProvided() =>
        _trustedAccumulators is { Count: > 0 };

    private ValueHash256? GetAccumulatorForEpoch(long epochIdx)
    {
        if (_trustedAccumulators is not null && _trustedAccumulators.Count > epochIdx)
            return _trustedAccumulators[(int)epochIdx];
        return null;
    }

    private ValueHash256? GetHistoricalRoot(long slotNumber)
    {
        long idx = slotNumber / SlotsPerHistoricalRoot;
        if (_trustedHistoricalRoots is not null && _trustedHistoricalRoots.Count > idx)
            return _trustedHistoricalRoots[(int)idx];
        return null;
    }

    private async Task<HistoricalSummary?> GetHistoricalSummary(long slotNumber)
    {
        long idx = slotNumber / SlotsPerHistoricalRoot;
        if (_historicalSummariesProvider is null) return null;
        return await _historicalSummariesProvider.GetHistoricalSummary((int)idx);
    }

    private void VerifyHashesAccumulator(Block block, BlockHeaderProof proof)
    {
        long headerIndex = block.Header.Number % SlotsPerHistoricalRoot;
        long genIndex = (SlotsPerHistoricalRoot * 2 * 2) + (headerIndex * 2);
        ValueHash256? accumulatorRoot = GetAccumulatorForEpoch(block.Header.Number / SlotsPerHistoricalRoot);
        if (accumulatorRoot is null)
            throw new EraVerificationException("Accumulator root not found.");
        if (!VerifyProof(block.Header.Hash!, proof.HashesAccumulator!, 15, genIndex, accumulatorRoot.Value))
            throw new EraVerificationException("Computed accumulator does not match stored accumulator.");
    }

    private void VerifyRoots(Block block, BlockHeaderProof proof)
    {
        if (_slotTime is null) throw new EraVerificationException("Beacon chain genesis timestamp is not available for HistoricalRoots verification.");
        long slotNumber = (long)_slotTime.GetSlot(block.Header.Timestamp);
        long blockRootIndex = slotNumber % SlotsPerHistoricalRoot;
        long genIndex = 2 * SlotsPerHistoricalRoot + blockRootIndex;
        ValueHash256? historicalRoot = GetHistoricalRoot(slotNumber);
        if (historicalRoot is null)
            throw new EraVerificationException("Historical root not found.");
        if (!VerifyProof(proof.BeaconBlockRoot!.Value, proof.BeaconBlockProof!, 14, genIndex, historicalRoot.Value))
            throw new EraVerificationException("Computed historical root does not match stored historical root.");
        if (!VerifyExecutionBlockProof(block, proof))
            throw new EraVerificationException("Computed execution block root does not match stored execution block root.");
    }

    private async Task VerifySummaries(Block block, BlockHeaderProof proof)
    {
        if (_slotTime is null) throw new EraVerificationException("Beacon chain genesis timestamp is not available for HistoricalSummaries verification.");
        long slotNumber = (long)_slotTime.GetSlot(block.Header.Timestamp);
        long genIndex = SlotsPerHistoricalRoot + (slotNumber % SlotsPerHistoricalRoot);
        ValueHash256? blockSummaryRoot = (await GetHistoricalSummary(slotNumber))?.BlockSummaryRoot;
        if (blockSummaryRoot is null)
            throw new EraVerificationException("Historical block summary root not found.");
        if (!VerifyProof(proof.BeaconBlockRoot!.Value, proof.BeaconBlockProof!, 13, genIndex, blockSummaryRoot.Value))
            throw new EraVerificationException("Computed block root does not match stored historical block summary root.");

        bool valid = IsDeneb(block.Header.Timestamp)
            ? VerifyExecutionBlockProofPostDeneb(block, proof)
            : VerifyExecutionBlockProof(block, proof);
        if (!valid)
            throw new EraVerificationException("Computed execution block root does not match stored execution block root.");
    }

    private bool VerifyExecutionBlockProof(Block block, BlockHeaderProof proof) =>
        VerifyProof(block.Header.Hash!, proof.ExecutionBlockProof!, 11,
            GenIndexExecutionBlockProofBellatrix, proof.BeaconBlockRoot!.Value);

    private bool VerifyExecutionBlockProofPostDeneb(Block block, BlockHeaderProof proof) =>
        VerifyProof(block.Header.Hash!, proof.ExecutionBlockProof!, 12,
            GenIndexExecutionBlockProofDeneb, proof.BeaconBlockRoot!.Value);

    private static bool VerifyProof(ValueHash256 leaf, ValueHash256[] branch, int depth, long genIndex, ValueHash256 root)
    {
        if (branch.Length != depth) return false;

        // Two 32-byte stack buffers, ping-ponged each iteration — zero heap allocations.
        Span<byte> buf0 = stackalloc byte[32];
        Span<byte> buf1 = stackalloc byte[32];
        leaf.Bytes.CopyTo(buf0);

        for (int i = 0; i < depth; i++)
        {
            // bit i == 1 means current node is the RIGHT child, so sibling is on the LEFT
            bool siblingOnLeft = (genIndex >> i) % 2 != 0;
            Span<byte> src = (i % 2 == 0) ? buf0 : buf1;
            Span<byte> dst = (i % 2 == 0) ? buf1 : buf0;
            if (siblingOnLeft)
                Hash(branch[i].Bytes, src, dst);
            else
                Hash(src, branch[i].Bytes, dst);
        }

        Span<byte> result = (depth % 2 == 0) ? buf0 : buf1;
        return result.SequenceEqual(root.Bytes);
    }

    private static void Hash(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<byte> target)
    {
        Span<byte> combined = stackalloc byte[a.Length + b.Length];
        a.CopyTo(combined);
        b.CopyTo(combined[a.Length..]);
        SHA256.TryHashData(combined, target, out _);
    }
}
