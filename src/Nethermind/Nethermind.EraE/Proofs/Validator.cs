// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using EraVerificationException = Nethermind.Era1.Exceptions.EraVerificationException;

namespace Nethermind.EraE.Proofs;

public sealed class Validator
{
    private readonly ISpecProvider _specProvider;
    private readonly IHistoricalSummariesProvider? _historicalSummariesProvider;
    private readonly IReadOnlyList<ValueHash256>? _trustedAccumulators;
    private readonly IReadOnlyList<ValueHash256>? _trustedHistoricalRoots;
    private readonly SlotTime? _slotTime;

    private const int SlotsPerHistoricalRoot = 8192;

    public Validator(
        ISpecProvider specProvider,
        IReadOnlyList<ValueHash256>? trustedAccumulators,
        IReadOnlyList<ValueHash256>? trustedHistoricalRoots,
        IHistoricalSummariesProvider? historicalSummariesProvider,
        IBlocksConfig? blocksConfig = null)
    {
        _specProvider = specProvider;
        if (specProvider.BeaconChainGenesisTimestamp.HasValue)
        {
            ulong secondsPerSlot = blocksConfig?.SecondsPerSlot ?? 12;
            _slotTime = new SlotTime(
                specProvider.BeaconChainGenesisTimestamp.Value * 1000,
                new Timestamper(),
                TimeSpan.FromSeconds(secondsPerSlot),
                TimeSpan.Zero);
        }
        _trustedAccumulators = trustedAccumulators;
        _trustedHistoricalRoots = trustedHistoricalRoots;
        _historicalSummariesProvider = historicalSummariesProvider;
    }

    public bool VerifyAccumulator(long blockNumber, ValueHash256 accumulatorRoot)
    {
        if (!TrustedAccumulatorsProvided()) return true;
        ValueHash256? trusted = GetAccumulatorForEpoch(blockNumber / SlotsPerHistoricalRoot);
        return trusted is null
            ? throw new EraVerificationException("Trusted accumulator root was not provided.")
            : trusted.Equals(accumulatorRoot);
    }

    public async Task VerifyBlocksRootContext(BlocksRootContext context, CancellationToken cancellation = default)
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
                ValueHash256? trustedRoot = GetHistoricalRoot(slot) ?? throw new EraVerificationException("Historical root not found.");
                if (!trustedRoot.Equals(context.HistoricalRoot))
                    throw new EraVerificationException("Computed historical root does not match trusted historical root.");
                break;

            case AccumulatorType.HistoricalSummaries:
                if (_slotTime is null) throw new EraVerificationException("Beacon chain genesis timestamp is not available for HistoricalSummaries verification.");
                long summarySlot = (long)_slotTime.GetSlot(context.StartingBlockTimestamp!.Value);
                HistoricalSummary? trustedSummary = await GetHistoricalSummary(summarySlot, cancellation) ?? throw new EraVerificationException("Historical summary not found.");
                if (!trustedSummary.Value.BlockSummaryRoot.Equals(context.HistoricalSummary.BlockSummaryRoot))
                    throw new EraVerificationException("Computed block summary root does not match trusted historical block summary root.");
                if (!trustedSummary.Value.StateSummaryRoot.Equals(context.HistoricalSummary.StateSummaryRoot))
                    throw new EraVerificationException("Computed state summary root does not match trusted historical state summary root.");
                break;
        }
    }

    private bool TrustedAccumulatorsProvided() => _trustedAccumulators is { Count: > 0 };

    private ValueHash256? GetAccumulatorForEpoch(long epochIdx) =>
        _trustedAccumulators is not null && _trustedAccumulators.Count > epochIdx
            ? _trustedAccumulators[(int)epochIdx]
            : null;

    private ValueHash256? GetHistoricalRoot(long slotNumber)
    {
        long idx = slotNumber / SlotsPerHistoricalRoot;
        return _trustedHistoricalRoots is not null && _trustedHistoricalRoots.Count > idx
            ? _trustedHistoricalRoots[(int)idx]
            : null;
    }

    private async Task<HistoricalSummary?> GetHistoricalSummary(long slotNumber, CancellationToken cancellation = default)
    {
        long idx = slotNumber / SlotsPerHistoricalRoot;
        return _historicalSummariesProvider is null
            ? null
            : await _historicalSummariesProvider.GetHistoricalSummary((int)idx, cancellationToken: cancellation);
    }

}
