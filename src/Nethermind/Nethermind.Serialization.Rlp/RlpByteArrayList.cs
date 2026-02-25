// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using Nethermind.Core.Collections;

namespace Nethermind.Serialization.Rlp;

public sealed class RlpByteArrayList(IMemoryOwner<byte> memoryOwner, (int Offset, int Length)[] items) : IOwnedReadOnlyList<byte[]>
{
    private readonly Memory<byte> _memory = memoryOwner.Memory;

    public int Count => items.Length;

    public byte[] this[int index]
    {
        get
        {
            (int offset, int length) = items[index];
            return length == 0 ? Array.Empty<byte>() : _memory.Slice(offset, length).ToArray();
        }
    }

    public ReadOnlySpan<byte[]> AsSpan() => throw new NotSupportedException();

    public void Dispose() => memoryOwner.Dispose();

    public IEnumerator<byte[]> GetEnumerator()
    {
        for (int i = 0; i < items.Length; i++)
        {
            yield return this[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
