// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Db;

/// <summary>
/// RocksDB operands enumerator.
/// </summary>
// Interface was not used because of ref struct limitations.
public readonly ref struct OperandsEnumerator(
    ReadOnlySpan<IntPtr> operandsList,
    ReadOnlySpan<long> operandsListLength)
{
    private readonly ReadOnlySpan<IntPtr> _operandsList = operandsList;
    private readonly ReadOnlySpan<long> _operandsListLength = operandsListLength;

    public int Count => _operandsList.Length;

    public unsafe ReadOnlySpan<byte> Get(int index)
    {
        return new Span<byte>((void*) _operandsList[index], (int) _operandsListLength[index]);
    }
}
