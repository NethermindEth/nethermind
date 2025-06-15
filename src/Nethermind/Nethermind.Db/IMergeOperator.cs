// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Db.Rocks;

// Generic just to avoid boxing
public interface IMergeOperator<in TOperands> where TOperands : IOperandsEnumerator
{
    byte[] ConcatenateFullMerge(ReadOnlySpan<byte> key, bool hasExistingValue, ReadOnlySpan<byte> existingValue,
        TOperands operands, out bool success);

    byte[] ConcatenatePartialMerge(ReadOnlySpan<byte> key, TOperands operands, out bool success);
}

public interface IOperandsEnumerator
{
    int Count { get; }
    ReadOnlySpan<byte> Get(int index);
}
