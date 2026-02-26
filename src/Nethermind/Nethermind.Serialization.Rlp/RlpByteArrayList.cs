// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using Nethermind.Core.Collections;

namespace Nethermind.Serialization.Rlp;

public sealed class RlpByteArrayList : IByteArrayList
{
    private readonly RlpItemList _inner;

    public RlpByteArrayList(IMemoryOwner<byte> memoryOwner, Memory<byte> rlpRegion)
    {
        _inner = new RlpItemList(memoryOwner, rlpRegion);
    }

    public int Count => _inner.Count;

    public ReadOnlySpan<byte> this[int index] => _inner.ReadContent(index);

    public void Dispose() => _inner.Dispose();
}
