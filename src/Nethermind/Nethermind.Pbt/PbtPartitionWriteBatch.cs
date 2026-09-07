// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Pbt;

/// <summary>Single-use, unique mutations ordered by the nibble following their fixed zone byte.</summary>
internal sealed class PbtPartitionWriteBatch(PbtWriteOperation[] operations, int[] table)
{
    private PbtWriteOperation[]? _operations = operations;
    private int[] _table = table;

    internal int Count => Operations.Length;
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
