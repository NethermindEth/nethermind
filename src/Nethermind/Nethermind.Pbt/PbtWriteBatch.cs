// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;

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
    internal BucketPlan Plan
    {
        get
        {
            _ = Operations;
            int bitDepth = ShardNibbleIndex * PbtFourLevelGroupGeometry.LevelsPerGroup;
            return new(_table!.AsSpan(), bitDepth, false);
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
        if (_operations is not null) ReturnRuns(_operations.AsSpan());
        _operations?.Dispose();
        _table?.Dispose();
        _operations = null;
        _table = null;
    }

    /// <summary>Returns the runs of consumed operations to their pools; the consumer owns them once it took the operations.</summary>
    internal static void ReturnRuns(ReadOnlySpan<PbtWriteOperation<TKey>> operations)
    {
        foreach (ref readonly PbtWriteOperation<TKey> operation in operations)
            if (operation.Run is not null) SlotRun.Return(operation.Run);
    }
}

/// <summary>One run key and the whole run that replaces the leaves under it; the run is owned by the batch or its consumer.</summary>
internal readonly record struct PbtWriteOperation<TKey>(TKey Key, ISlotRun Run) where TKey : struct, IPbtKey<TKey>
{
    /// <summary>The bits of <see cref="Key"/> that identify the run: everything above the slot nibble.</summary>
    internal int KeyBitLength => Key.BitLength - PbtFourLevelGroupGeometry.LevelsPerGroup;

    /// <summary>Whether the run leaves no leaf under its key.</summary>
    internal bool IsDelete => Run.Count == 0;
}
