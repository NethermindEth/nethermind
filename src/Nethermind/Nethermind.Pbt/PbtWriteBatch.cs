// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

/// <summary>A single-use prepared mutation batch produced by <see cref="PbtWriteBatchBuilder"/>.</summary>
public sealed class PbtWriteBatch : IDisposable
{
    private ArrayPoolList<PbtWriteOperation>? _operations;
    private ArrayPoolList<int>? _table;

    internal PbtWriteBatch(ArrayPoolList<PbtWriteOperation> operations, ArrayPoolList<int> table, int shardNibbleIndex)
    {
        _operations = operations;
        _table = table;
        ShardNibbleIndex = shardNibbleIndex;
    }

    /// <summary>Gets the number of prepared mutations before the batch is consumed.</summary>
    public int Count => Operations.Count;

    internal int ShardNibbleIndex { get; }
    internal ReadOnlySpan<PbtWriteOperation> Entries => Operations.AsSpan();
    internal ReadOnlySpan<int> Precalculated
    {
        get
        {
            _ = Operations;
            return _table!.AsSpan();
        }
    }

    private ArrayPoolList<PbtWriteOperation> Operations => _operations ?? throw new InvalidOperationException("The prepared batch has already been consumed.");

    internal void Consume(out ArrayPoolList<PbtWriteOperation> operations, out ArrayPoolList<int> table)
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

internal enum PbtWriteOperationKind : byte
{
    Set,
    Delete,
}

internal readonly record struct PbtWriteOperation(PbtFullKey Key, ValueHash256 Value, PbtWriteOperationKind Kind)
{
    internal static PbtWriteOperation Set(PbtFullKey key, in ValueHash256 value) => new(key, value, PbtWriteOperationKind.Set);
    internal static PbtWriteOperation Delete(PbtFullKey key) => new(key, default, PbtWriteOperationKind.Delete);
}
