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
/// names ending in <c>Timestamp</c> are timestamp-keyed; names ending in <c>BlockNumber</c>
/// are block-keyed. This keeps call sites concise and remains valid once block numbers
/// migrate to <see cref="ulong"/>, since the inference does not depend on signed/unsigned
/// type dispatch. The explicit <see cref="ForkActivationKind"/> overloads are available
/// when a key cannot follow the naming convention. Iteration preserves insertion order.
/// </remarks>
public sealed class ForkSchedule : IEnumerable<ForkSpec>
{
    private readonly List<ForkSpec> _entries = [];

    public IReleaseSpec this[long key, [CallerArgumentExpression(nameof(key))] string? keyExpression = null]
    {
        set => this[key, InferKind(keyExpression)] = value;
    }

    public IReleaseSpec this[ulong key, [CallerArgumentExpression(nameof(key))] string? keyExpression = null]
    {
        set => this[key, InferKind(keyExpression)] = value;
    }

    public IReleaseSpec this[long key, ForkActivationKind kind]
    {
        set => _entries.Add(kind switch
        {
            ForkActivationKind.Block => ForkSpec.AtBlock((ulong)key, value),
            ForkActivationKind.Timestamp => ForkSpec.AtTimestamp((ulong)key, value),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        });
    }

    public IReleaseSpec this[ulong key, ForkActivationKind kind]
    {
        set => _entries.Add(kind switch
        {
            ForkActivationKind.Block => ForkSpec.AtBlock(key, value),
            ForkActivationKind.Timestamp => ForkSpec.AtTimestamp(key, value),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        });
    }

    private static ForkActivationKind InferKind(string? keyExpression) => keyExpression switch
    {
        null => throw new ArgumentException("Cannot infer Block/Timestamp without a caller-argument expression. Use the explicit ForkActivationKind overload."),
        _ when keyExpression.EndsWith("Timestamp", StringComparison.Ordinal) => ForkActivationKind.Timestamp,
        _ when keyExpression.EndsWith("BlockNumber", StringComparison.Ordinal) => ForkActivationKind.Block,
        _ => throw new ArgumentException($"Cannot infer Block/Timestamp from key expression '{keyExpression}': name must end with 'BlockNumber' or 'Timestamp'. Use the explicit ForkActivationKind overload otherwise."),
    };

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
        ulong postMergeBlock = 0,
        bool incrementBlockPerTimestampFork = true,
        ReadOnlySpan<ulong> excludeBlocks = default,
        ReadOnlySpan<ForkActivation> prepend = default)
    {
        int count = prepend.Length;
        for (int i = 1; i < _entries.Count; i++)
        {
            ForkSpec fork = _entries[i];
            if (fork.Block is { } block)
            {
                if (!excludeBlocks.Contains(block)) count++;
            }
            else if (fork.Timestamp.HasValue)
            {
                count++;
            }
        }

        ForkActivation[] result = new ForkActivation[count];
        prepend.CopyTo(result);
        int index = prepend.Length;
        ulong timestampIndex = 0;
        for (int i = 1; i < _entries.Count; i++)
        {
            ForkSpec fork = _entries[i];
            if (fork.Block is { } block)
            {
                if (!excludeBlocks.Contains(block))
                    result[index++] = (ForkActivation)block;
            }
            else if (fork.Timestamp is { } timestamp)
            {
                ulong blockForTimestamp = incrementBlockPerTimestampFork
                    ? postMergeBlock + timestampIndex
                    : postMergeBlock;
                result[index++] = (blockForTimestamp, timestamp);
                timestampIndex++;
            }
        }
        return result;
    }
}
