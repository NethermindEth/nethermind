// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

/// <summary>A single-use prepared mutation batch produced by <see cref="PbtWriteBatchBuilder{TKey}"/>.</summary>
public sealed class PbtWriteBatch<TKey> : IDisposable where TKey : struct, IPbtKey<TKey>
{
    private ArrayPoolList<PbtWriteOperation<TKey>>? _operations;
    private ArrayPoolList<int>? _table;

    internal PbtWriteBatch(ArrayPoolList<PbtWriteOperation<TKey>> operations, ArrayPoolList<int> table, int shardNibbleIndex)
    {
        _operations = operations;
        _table = table;
        ShardNibbleIndex = shardNibbleIndex;
    }

    /// <summary>Gets the number of prepared mutations before the batch is consumed.</summary>
    public int Count => Operations.Count;

    internal int ShardNibbleIndex { get; }
    internal ReadOnlySpan<PbtWriteOperation<TKey>> Entries => Operations.AsSpan();
    internal TrieUpdater.BucketPlan Plan
    {
        get
        {
            _ = Operations;
            int depth = ShardNibbleIndex * PbtFourLevelGroupGeometry.LevelsPerGroup;
            return new(_table!.AsSpan(), depth, depth, false, false);
        }
    }

    private ArrayPoolList<PbtWriteOperation<TKey>> Operations => _operations ?? throw new InvalidOperationException("The prepared batch has already been consumed.");

    internal void Consume(out ArrayPoolList<PbtWriteOperation<TKey>> operations, out ArrayPoolList<int> table)
    {
        operations = Operations;
        table = _table!;
        _operations = null;
        _table = null;
    }
    /// <inheritdoc/>
    public void Dispose()
    {
        _operations?.Dispose();
        _table?.Dispose();
        _operations = null;
        _table = null;
    }
}

internal readonly record struct PbtWriteOperation<TKey>(TKey Key, ValueHash256 Value) where TKey : struct, IPbtKey<TKey>
{
    internal static PbtWriteOperation<TKey> Set(TKey key, in ValueHash256 value) => new(key, value);
    internal static PbtWriteOperation<TKey> Delete(TKey key) => new(key, default);
}
