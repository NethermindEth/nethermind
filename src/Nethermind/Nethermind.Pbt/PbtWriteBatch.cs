// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

/// <summary>A single-use prepared mutation batch produced by <see cref="PbtWriteBatchBuilder"/>.</summary>
public sealed class PbtWriteBatch
{
    private PbtWriteOperation[]? _operations;
    private int[] _table;

    internal PbtWriteBatch(PbtWriteOperation[] operations, int[] table, int shardNibbleIndex)
    {
        _operations = operations;
        _table = table;
        ShardNibbleIndex = shardNibbleIndex;
    }

    /// <summary>Gets the number of prepared mutations before the batch is consumed.</summary>
    public int Count => Operations.Length;

    internal int ShardNibbleIndex { get; }
    internal ReadOnlySpan<PbtWriteOperation> Entries => Operations;
    internal ReadOnlySpan<int> Precalculated
    {
        get
        {
            _ = Operations;
            return _table;
        }
    }

    private PbtWriteOperation[] Operations => _operations ?? throw new InvalidOperationException("The prepared batch has already been consumed.");

    internal void Consume(out PbtWriteOperation[] operations, out int[] table)
    {
        operations = Operations;
        table = _table;
        _operations = null;
        _table = [];
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
