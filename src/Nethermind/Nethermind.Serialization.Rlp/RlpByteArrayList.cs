// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using Nethermind.Core.Collections;

namespace Nethermind.Serialization.Rlp;

public sealed class RlpByteArrayList : IByteArrayList
{
    private readonly RlpItemList _inner;

    public RlpByteArrayList(IMemoryOwner<byte> memoryOwner, int dataStart, int innerLength)
    {
        _inner = new RlpItemList(memoryOwner, memoryOwner.Memory.Slice(dataStart, innerLength));
    }

    public int Count => _inner.Count;

    public ReadOnlySpan<byte> this[int index]
    {
        get
        {
            ReadOnlySpan<byte> rawRlp = _inner[index];
            (int prefixLength, int contentLength) = RlpItemList.PeekPrefixAndContentLength(rawRlp, 0);
            return contentLength == 0
                ? ReadOnlySpan<byte>.Empty
                : rawRlp.Slice(prefixLength, contentLength);
        }
    }

    public void Dispose() => _inner.Dispose();
}
