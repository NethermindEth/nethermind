// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using Nethermind.Core.Collections;

namespace Nethermind.Serialization.Rlp;

public sealed class RlpByteArrayList(IMemoryOwner<byte> memoryOwner, (int Offset, int Length)[] items) : IOwnedReadOnlyList<byte[]>, IByteArrayList
{
    private readonly Memory<byte> _memory = memoryOwner.Memory;

    public int Count => items.Length;

    ReadOnlySpan<byte> IByteArrayList.this[int index] => GetSpan(index);

    byte[] IReadOnlyList<byte[]>.this[int index] => GetSpan(index).ToArray();

    public ReadOnlySpan<byte[]> AsSpan() => throw new NotSupportedException();

    public void Dispose() => memoryOwner.Dispose();

    public IEnumerator<byte[]> GetEnumerator()
    {
        for (int i = 0; i < items.Length; i++)
        {
            yield return GetSpan(i).ToArray();
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private ReadOnlySpan<byte> GetSpan(int index)
    {
        (int offset, int length) = items[index];
        return length == 0 ? ReadOnlySpan<byte>.Empty : _memory.Span.Slice(offset, length);
    }
}
