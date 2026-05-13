// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using Nethermind.Core.Specs;

namespace Nethermind.Specs;

/// <summary>
/// Collection initializer for declaring a fork schedule as
/// <c>[block] = spec</c> or <c>[timestamp] = spec</c> entries.
/// </summary>
/// <remarks>
/// Distinguishes block- vs timestamp-keyed activations via the indexer's parameter type
/// (<see cref="long"/> for block numbers, <see cref="ulong"/> for timestamps). Iteration
/// preserves insertion order, which is the order activations are declared.
/// </remarks>
public sealed class ForkSchedule : IEnumerable<ForkSpec>
{
    private readonly List<ForkSpec> _entries = [];

    public IReleaseSpec this[long block] { set => _entries.Add(new ForkSpec(block, value)); }
    public IReleaseSpec this[ulong timestamp] { set => _entries.Add(new ForkSpec(timestamp, value)); }

    public IEnumerator<ForkSpec> GetEnumerator() => _entries.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public static implicit operator ForkSpec[](ForkSchedule schedule) => [.. schedule._entries];

    /// <summary>
    /// Derives <see cref="ForkActivation"/> entries from this schedule (skipping the genesis spec).
    /// The algorithm is fixed; chain-specific shape is expressed through the <paramref name="postMergeBlock"/>,
    /// <paramref name="incrementBlockPerTimestampFork"/>, <paramref name="excludeBlocks"/>, and
    /// <paramref name="prepend"/> parameters.
    /// </summary>
    /// <param name="postMergeBlock">Block number paired with every timestamp-keyed activation.</param>
    /// <param name="incrementBlockPerTimestampFork">If <c>true</c>, the block paired with the <c>n</c>-th
    /// timestamp-keyed activation is <c><paramref name="postMergeBlock"/> + n</c> rather than a fixed value.
    /// Used when forks share a timestamp (Hoodi) or fork-IDs must differ per fork (Mainnet).</param>
    /// <param name="excludeBlocks">Block numbers in the schedule that should not produce an activation
    /// (e.g. Mainnet's Paris, which is TTD-determined and not a fork-ID boundary).</param>
    /// <param name="prepend">Activations to emit before iterating the schedule (e.g. Sepolia's merge-block
    /// boundary, which has no corresponding schedule entry).</param>
    public ForkActivation[] ToTransitionActivations(
        long postMergeBlock = 0L,
        bool incrementBlockPerTimestampFork = true,
        long[]? excludeBlocks = null,
        ulong[]? excludeTimestamps = null,
        ForkActivation[]? prepend = null)
    {
        ReadOnlySpan<long> excludedBlocks = excludeBlocks.AsSpan();
        ReadOnlySpan<ulong> excludedTimestamps = excludeTimestamps.AsSpan();
        int prependLength = prepend?.Length ?? 0;

        int count = prependLength;
        for (int i = 1; i < _entries.Count; i++)
        {
            ForkSpec fork = _entries[i];
            if (fork.Block is { } block)
            {
                if (!excludedBlocks.Contains(block)) count++;
            }
            else if (fork.Timestamp is { } timestamp && !excludedTimestamps.Contains(timestamp))
            {
                count++;
            }
        }

        ForkActivation[] result = new ForkActivation[count];
        prepend.AsSpan().CopyTo(result);
        int index = prependLength;
        int timestampIndex = 0;
        for (int i = 1; i < _entries.Count; i++)
        {
            ForkSpec fork = _entries[i];
            if (fork.Block is { } block)
            {
                if (!excludedBlocks.Contains(block))
                    result[index++] = (ForkActivation)block;
            }
            else if (fork.Timestamp is { } timestamp)
            {
                long blockForTimestamp = incrementBlockPerTimestampFork
                    ? postMergeBlock + timestampIndex
                    : postMergeBlock;
                if (!excludedTimestamps.Contains(timestamp))
                    result[index++] = (blockForTimestamp, timestamp);
                timestampIndex++;
            }
        }
        return result;
    }
}
