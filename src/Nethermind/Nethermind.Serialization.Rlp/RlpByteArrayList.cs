// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Serialization.FluentRlp;

namespace Nethermind.Serialization.Rlp;

public sealed class RlpByteArrayList : IByteArrayList, IRlpWrapper
{
    private readonly IRlpItemList _inner;

    public RlpByteArrayList(IMemoryOwner<byte> memoryOwner, Memory<byte> rlpRegion) => _inner = new RlpItemList(memoryOwner, rlpRegion);

    public RlpByteArrayList(IRlpItemList inner) => _inner = inner;

    public int Count => _inner.Count;

    public ReadOnlySpan<byte> this[int index] => _inner.ReadContent(index);

    public int RlpLength => _inner.RlpLength;

    public void Write(ref RlpWriter writer) => _inner.Write(ref writer);

    public static RlpByteArrayList DecodeList(ref RlpReader ctx, IMemoryOwner<byte> memoryOwner, RlpLimit? limit = null)
    {
        // An omitted limit must not mean no limit, so CountItems falls back to the default cap. It stops at the
        // limit, so that a malicious input with millions of items is not scanned to its end.
        RlpReader items = ctx.EnterSequence(out _);
        int count = items.CountItems(limit);

        Memory<byte> rlpRegion = memoryOwner.Memory.SliceOf(ctx.ReadEncodedItem());
        return new(new RlpItemList(memoryOwner, rlpRegion, knownCount: count));
    }

    public void Dispose() => _inner.Dispose();
}
