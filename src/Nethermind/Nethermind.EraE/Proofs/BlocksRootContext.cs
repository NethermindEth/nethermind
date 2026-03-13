// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.EraE.Archive;
using Nethermind.Int256;
using Nethermind.Serialization;
using Nethermind.Specs;

namespace Nethermind.EraE.Proofs;

public enum AccumulatorType
{
    HistoricalHashesAccumulator,
    HistoricalRoots,
    HistoricalSummaries
}

/// <summary>
/// Tracks block-level state within an era epoch and finalizes the epoch's accumulator or historical root.
/// Pre-merge epochs produce an AccumulatorRoot via SSZ hash_tree_root.
/// Post-merge paths (HistoricalRoots, HistoricalSummaries) are currently stubbed.
/// </summary>
public sealed class BlocksRootContext : IDisposable
{
    private static readonly ForkActivation ParisFork = new(MainnetSpecProvider.ParisBlockNumber);
    private static readonly ForkActivation ShanghaiFork = new(long.MaxValue, MainnetSpecProvider.ShanghaiBlockTimestamp);

    private readonly ArrayPoolList<ValueHash256> _blockRoots = new(8192);
    private readonly ArrayPoolList<ValueHash256> _stateRoots = new(8192);
    private readonly ArrayPoolList<(Hash256 Hash, UInt256 Td)> _blockHashes = new(8192);

    public readonly AccumulatorType AccumulatorType;

    private AccumulatorCalculator? _accumulatorCalculator; // kept alive after FinalizeContext for GetProof
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

    public BlocksRootContext(long startingBlockNumber, ulong? startingBlockTimestamp = null)
    {
        StartingBlockNumber = startingBlockNumber;
        StartingBlockTimestamp = startingBlockTimestamp;
        ForkActivation forkActivation = new(startingBlockNumber, startingBlockTimestamp);
        AccumulatorType = GetAccumulatorType(forkActivation);
    }

    /// <summary>
    /// Records a block for the accumulator (pre-merge) or the beacon roots/state roots (post-merge).
    /// For post-merge blocks, use the overload that accepts <paramref name="beaconBlockRoot"/> and
    /// <paramref name="stateRoot"/>; without them the post-merge context will not be populated.
    /// </summary>
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
                if (beaconBlockRoot.HasValue)
                    _blockRoots.Add(beaconBlockRoot.Value);
                else
                    _blockRoots.Add(default);

                if (stateRoot.HasValue)
                    _stateRoots.Add(stateRoot.Value);
                else
                    _stateRoots.Add(default);
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
                // Calculator is kept alive (not disposed) so EraWriter can call GetProof after Finalize.
                break;

            case AccumulatorType.HistoricalRoots:
                SszEncoding.Merkleize(
                    HistoricalBatch.From(_blockRoots.ToArray(), _stateRoots.ToArray()),
                    out UInt256 historicalRoot);
                _historicalRoot = UInt256ToHash(ref historicalRoot);
                break;

            case AccumulatorType.HistoricalSummaries:
                SszEncoding.Merkleize(ValueHash256Vector.From(_blockRoots.ToArray()), out UInt256 blockRoot);
                SszEncoding.Merkleize(ValueHash256Vector.From(_stateRoots.ToArray()), out UInt256 stateRoot);
                _historicalSummary = new HistoricalSummary(
                    UInt256ToHash(ref blockRoot),
                    UInt256ToHash(ref stateRoot));
                break;
        }
    }

    /// <summary>
    /// Returns the 15-element Merkle proof path for the pre-merge block at the given
    /// era-relative index (0 = first pre-merge block in the era).
    /// Must be called after <see cref="FinalizeContext"/>.
    /// </summary>
    public ValueHash256[] GetProof(int blockIndex)
    {
        if (_accumulatorCalculator is null)
            throw new InvalidOperationException("FinalizeContext must be called before GetProof.");
        return _accumulatorCalculator.GetProof(blockIndex);
    }

    public void Dispose()
    {
        _accumulatorCalculator?.Dispose();
        _blockRoots.Dispose();
        _stateRoots.Dispose();
        _blockHashes.Dispose();
    }

    private static AccumulatorType GetAccumulatorType(ForkActivation forkActivation)
    {
        if (forkActivation < ParisFork)
            return AccumulatorType.HistoricalHashesAccumulator;
        if (forkActivation < ShanghaiFork)
            return AccumulatorType.HistoricalRoots;
        return AccumulatorType.HistoricalSummaries;
    }

    private static ValueHash256 UInt256ToHash(ref UInt256 value)
    {
        ReadOnlySpan<byte> bytes = MemoryMarshal.Cast<UInt256, byte>(MemoryMarshal.CreateSpan(ref value, 1));
        return new ValueHash256(bytes);
    }
}
