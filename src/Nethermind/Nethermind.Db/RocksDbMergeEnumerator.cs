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
    private readonly ReadOnlySpan<IntPtr> _operandsList = operandsList;
    private readonly ReadOnlySpan<long> _operandsListLength = operandsListLength;

    public Span<byte> ExistingValue { get; }
    public bool HasExistingValue { get; }
    public int OperandsCount => _operandsList.Length;
    public int TotalCount => OperandsCount + (HasExistingValue ? 1 : 0);

    public RocksDbMergeEnumerator(
        Span<byte> existingValue, bool hasExistingValue,
        ReadOnlySpan<IntPtr> operandsList, ReadOnlySpan<long> operandsListLength
    ): this(operandsList, operandsListLength)
    {
        ExistingValue = existingValue;
        HasExistingValue = hasExistingValue;
    }

    public Span<byte> GetExistingValue()
    {
        return HasExistingValue ? ExistingValue : default;
    }

    public unsafe Span<byte> GetOperand(int index)
    {
        return new((void*)_operandsList[index], (int)_operandsListLength[index]);
    }

    public Span<byte> Get(int index)
    {
        if (index == 0 && HasExistingValue)
            return ExistingValue;

        if (HasExistingValue)
            index -= 1;

        return GetOperand(index);
    }
}
