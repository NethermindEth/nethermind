// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using Nethermind.Core.Collections;

namespace Nethermind.Serialization.Rlp;

public sealed class RlpByteArrayList : IByteArrayList, IRlpWrapper
{
    private readonly IRlpItemList _inner;

    public RlpByteArrayList(IMemoryOwner<byte> memoryOwner, Memory<byte> rlpRegion) => _inner = new RlpItemList(memoryOwner, rlpRegion);

    public RlpByteArrayList(IRlpItemList inner) => _inner = inner;

    public int Count => _inner.Count;

    public ReadOnlySpan<byte> this[int index] => _inner.ReadContent(index);

    public int RlpLength => _inner.RlpLength;

    public void Write(RlpStream stream) => _inner.Write(stream);

    public static RlpByteArrayList DecodeList(ref Rlp.ValueDecoderContext ctx, IMemoryOwner<byte> memoryOwner, RlpLimit? limit = null)
    {
        if (limit is null)
        {
            return new(RlpItemList.DecodeList(ref ctx, memoryOwner));
        }

        int prefixStart = ctx.Position;
        int innerLength = ctx.ReadSequenceLength();
        int end = ctx.Position + innerLength;
        // Early-out the walk at limit + 1: any count above the limit triggers the same disconnect,
        // so we don't need to keep counting past it (matters for malicious inputs with millions of items).
        int count = ctx.PeekNumberOfItemsRemaining(end, limit.Value.Limit + 1);
        ctx.GuardLimit(count, limit);

        Memory<byte> rlpRegion = memoryOwner.Memory.Slice(prefixStart, end - prefixStart);
        ctx.Position = end;
        return new(new RlpItemList(memoryOwner, rlpRegion, knownCount: count));
    }

    public void Dispose() => _inner.Dispose();
}
