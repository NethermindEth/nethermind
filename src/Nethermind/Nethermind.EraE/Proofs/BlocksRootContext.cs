// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using AccumulatorCalculator = Nethermind.Era1.AccumulatorCalculator;
using Nethermind.Int256;

namespace Nethermind.EraE.Proofs;

public enum AccumulatorType
{
    HistoricalHashesAccumulator,
    HistoricalRoots,
    HistoricalSummaries
}

public sealed class BlocksRootContext : IDisposable
{
    private readonly ArrayPoolList<ValueHash256> _blockRoots = new(8192);
    private readonly ArrayPoolList<ValueHash256> _stateRoots = new(8192);
    private readonly ArrayPoolList<(Hash256 Hash, UInt256 Td)> _blockHashes = new(8192);

    public AccumulatorType AccumulatorType { get; }

    private AccumulatorCalculator? _accumulatorCalculator;
    private ValueHash256? _accumulatorRoot;
    private HistoricalSummary? _historicalSummary;
    private ValueHash256? _historicalRoot;

    public bool Populated { get; private set; }
    public long StartingBlockNumber { get; }
    public ulong? StartingBlockTimestamp { get; }

    public ValueHash256 AccumulatorRoot =>
        _accumulatorRoot ?? throw new InvalidOperationException("Accumulator root not set or not finalized.");

    public HistoricalSummary HistoricalSummary =>
        _historicalSummary ?? throw new InvalidOperationException("Historical summary not set or not finalized.");

    public ValueHash256 HistoricalRoot =>
        _historicalRoot ?? throw new InvalidOperationException("Historical root not set or not finalized.");

    public BlocksRootContext(long startingBlockNumber, ulong? startingBlockTimestamp = null, ISpecProvider? specProvider = null)
    {
        StartingBlockNumber = startingBlockNumber;
        StartingBlockTimestamp = startingBlockTimestamp;
        ForkActivation forkActivation = new(startingBlockNumber, startingBlockTimestamp);
        AccumulatorType = GetAccumulatorType(forkActivation, specProvider);
    }

    public void ProcessBlock(Block block, ValueHash256? beaconBlockRoot = null, ValueHash256? stateRoot = null)
    {
        switch (AccumulatorType)
        {
            case AccumulatorType.HistoricalHashesAccumulator:
                // Only track pre-merge blocks in the accumulator.
                if (!block.Header.IsPostMerge)
                    _blockHashes.Add((block.Header.Hash!, block.TotalDifficulty!.Value));
                break;

            case AccumulatorType.HistoricalRoots:
            case AccumulatorType.HistoricalSummaries:
                // Post-merge: collect beacon block roots and state roots per slot.
                // Missed slots are represented by zero hashes (default ValueHash256).
                _blockRoots.Add(beaconBlockRoot ?? default);
                _stateRoots.Add(stateRoot ?? default);
                break;
        }
        Populated = true;
    }

    public void FinalizeContext()
    {
        if (!Populated) return;

        switch (AccumulatorType)
        {
            case AccumulatorType.HistoricalHashesAccumulator:
                _accumulatorCalculator = new AccumulatorCalculator();
                foreach ((Hash256 hash, UInt256 td) in _blockHashes.AsSpan())
                    _accumulatorCalculator.Add(hash, td);
                _accumulatorRoot = _accumulatorCalculator.ComputeRoot();
                break;

            case AccumulatorType.HistoricalRoots:
                HistoricalBatch.Merkleize(
                    HistoricalBatch.From(_blockRoots.AsSpan(), _stateRoots.AsSpan()),
                    out UInt256 historicalRoot);
                _historicalRoot = UInt256ToHash(ref historicalRoot);
                break;

            case AccumulatorType.HistoricalSummaries:
                ValueHash256Vector.Merkleize(ValueHash256Vector.From(_blockRoots.AsSpan()), out UInt256 blockRoot);
                ValueHash256Vector.Merkleize(ValueHash256Vector.From(_stateRoots.AsSpan()), out UInt256 stateRoot);
                _historicalSummary = new HistoricalSummary(
                    UInt256ToHash(ref blockRoot),
                    UInt256ToHash(ref stateRoot));
                break;
        }
    }

    public void Dispose()
    {
        _accumulatorCalculator?.Dispose();
        _blockRoots.Dispose();
        _stateRoots.Dispose();
        _blockHashes.Dispose();
    }

    private static AccumulatorType GetAccumulatorType(ForkActivation forkActivation, ISpecProvider? specProvider) =>
        specProvider switch
        {
            null => AccumulatorType.HistoricalHashesAccumulator,
            _ when specProvider.GetSpec(forkActivation).IsEip4895Enabled => AccumulatorType.HistoricalSummaries,
            _ when specProvider.MergeBlockNumber is { BlockNumber: var merge } && forkActivation.BlockNumber >= merge => AccumulatorType.HistoricalRoots,
            _ => AccumulatorType.HistoricalHashesAccumulator
        };

    private static ValueHash256 UInt256ToHash(ref UInt256 value) =>
        new(MemoryMarshal.Cast<UInt256, byte>(MemoryMarshal.CreateSpan(ref value, 1)));
}
