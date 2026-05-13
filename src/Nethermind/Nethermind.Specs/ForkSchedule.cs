// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nethermind.Core.Specs;

namespace Nethermind.Specs;

/// <summary>
/// Collection initializer for declaring a fork schedule as <c>[KeyConst] = spec</c> entries.
/// </summary>
/// <remarks>
/// Block- vs timestamp-keyed activations are inferred from the key's identifier name:
/// names containing <c>Timestamp</c> are timestamp-keyed; otherwise the name must contain
/// <c>Block</c> (e.g. <c>GenesisBlock</c>, <c>LondonBlockNumber</c>). This keeps call sites
/// concise and remains valid once block numbers migrate to <see cref="ulong"/>, since the
/// inference does not depend on signed/unsigned type dispatch. The explicit
/// <see cref="ForkActivationKind"/> overloads are available when a key cannot follow the
/// naming convention. Iteration preserves insertion order.
/// </remarks>
public sealed class ForkSchedule : IEnumerable<ForkSpec>
{
    private readonly List<ForkSpec> _entries = [];

    public IReleaseSpec this[long key, [CallerArgumentExpression(nameof(key))] string? keyExpression = null]
    {
        set => Add(key, InferKind(keyExpression), value);
    }

    public IReleaseSpec this[ulong key, [CallerArgumentExpression(nameof(key))] string? keyExpression = null]
    {
        set => Add(key, InferKind(keyExpression), value);
    }

    public IReleaseSpec this[long key, ForkActivationKind kind] { set => Add(key, kind, value); }
    public IReleaseSpec this[ulong key, ForkActivationKind kind] { set => Add(key, kind, value); }

    private void Add(long key, ForkActivationKind kind, IReleaseSpec spec) =>
        _entries.Add(kind switch
        {
            ForkActivationKind.Block => new ForkSpec(key, spec),
            ForkActivationKind.Timestamp => new ForkSpec((ulong)key, spec),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        });

    private void Add(ulong key, ForkActivationKind kind, IReleaseSpec spec) =>
        _entries.Add(kind switch
        {
            ForkActivationKind.Block => new ForkSpec((long)key, spec),
            ForkActivationKind.Timestamp => new ForkSpec(key, spec),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        });

    private static ForkActivationKind InferKind(string? keyExpression)
    {
        if (keyExpression is null)
            throw new ArgumentException("Cannot infer Block/Timestamp without a caller-argument expression. Use the explicit ForkActivationKind overload.");
        if (keyExpression.Contains("Timestamp", StringComparison.Ordinal))
            return ForkActivationKind.Timestamp;
        if (keyExpression.Contains("Block", StringComparison.Ordinal))
            return ForkActivationKind.Block;
        throw new ArgumentException(
            $"Cannot infer Block/Timestamp from key expression '{keyExpression}': name must contain 'Block' or 'Timestamp'. Use the explicit ForkActivationKind overload otherwise.");
    }

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
