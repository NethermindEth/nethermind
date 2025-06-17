// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Db;

/// <summary>
/// RocksDB enumerator for values of merge operation.
/// </summary>
// Interface was not used because of ref struct limitations.
public readonly ref struct RocksDbMergeEnumerator(ReadOnlySpan<IntPtr> operandsList, ReadOnlySpan<long> operandsListLength)
{
    public Span<byte> ExistingValue { get; }
    public bool HasExistingValue { get; }

    private readonly ReadOnlySpan<IntPtr> _operandsList = operandsList;
    private readonly ReadOnlySpan<long> _operandsListLength = operandsListLength;

    public RocksDbMergeEnumerator(
        Span<byte> existingValue, bool hasExistingValue,
        ReadOnlySpan<IntPtr> operandsList, ReadOnlySpan<long> operandsListLength
    ): this(operandsList, operandsListLength)
    {
        ExistingValue = existingValue;
        HasExistingValue = hasExistingValue;
    }

    public int Count => _operandsList.Length + (HasExistingValue ? 1 : 0);

    public unsafe Span<byte> Get(int index)
    {
        if (index == 0 && HasExistingValue)
            return ExistingValue;

        if (HasExistingValue)
            index -= 1;

        return new Span<byte>((void*) _operandsList[index], (int) _operandsListLength[index]);
    }
}
