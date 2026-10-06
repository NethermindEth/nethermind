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

    internal PbtWriteBatch(ArrayPoolList<PbtWriteOperation<TKey>> operations, ArrayPoolList<int> table)
    {
        _operations = operations;
        _table = table;
    }

    internal void Consume(out ArrayPoolList<PbtWriteOperation<TKey>> operations, out ArrayPoolList<int> table)
    {
        operations = _operations!;
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

internal readonly record struct PbtWriteOperation<TKey>(TKey Key, ValueHash256 Value) where TKey : struct, IPbtKey<TKey>;
