// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

/// <summary>A single-use prepared mutation batch produced by <see cref="PbtWriteBatchBuilder{TKey}"/>.</summary>
public sealed class PbtWriteBatch<TKey>(ArrayPoolList<PbtWriteOperation<TKey>> operations, ArrayPoolList<int> table) : IDisposable where TKey : struct, IPbtKey<TKey>
{
    private ArrayPoolList<PbtWriteOperation<TKey>>? _operations = operations;
    private ArrayPoolList<int>? _table = table;

    public void Consume(out ArrayPoolList<PbtWriteOperation<TKey>> operations, out ArrayPoolList<int> table)
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

public readonly record struct PbtWriteOperation<TKey>(TKey Key, ValueHash256 Value) where TKey : struct, IPbtKey<TKey>;
