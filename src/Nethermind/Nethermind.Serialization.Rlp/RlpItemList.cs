// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Collections;

namespace Nethermind.Serialization.Rlp;

public sealed class RlpItemList : IByteArrayList
{
    private readonly RefCountingMemoryOwner<byte> _memoryOwner;
    private readonly Memory<byte> _rlpRegion;
    private int _count;

    private int _cachedIndex;
    private int _cachedPosition;

    public RlpItemList(IMemoryOwner<byte> memoryOwner, Memory<byte> rlpRegion)
    {
        _memoryOwner = new RefCountingMemoryOwner<byte>(memoryOwner);
        _rlpRegion = rlpRegion;
        _count = -1;
        _cachedIndex = 0;
        _cachedPosition = 0;
    }

    private RlpItemList(RefCountingMemoryOwner<byte> memoryOwner, Memory<byte> rlpRegion)
    {
        _memoryOwner = memoryOwner;
        _rlpRegion = rlpRegion;
        _count = -1;
        _cachedIndex = 0;
        _cachedPosition = 0;
    }

    public int Count
    {
        get
        {
            if (_count < 0) _count = ComputeCount();
            return _count;
        }
    }

    public ReadOnlySpan<byte> this[int index]
    {
        get
        {
            ReadOnlySpan<byte> span = _rlpRegion.Span;
            int position = GetPosition(span, index);
            (int prefixLength, int contentLength) = PeekPrefixAndContentLength(span, position);
            _cachedIndex = index;
            _cachedPosition = position;
            return span.Slice(position, prefixLength + contentLength);
        }
    }

    public RlpListReader CreateNestedReader(int index) => new(this[index]);

    public RlpItemList ReadNestedItemList(int index)
    {
        ReadOnlySpan<byte> item = this[index];
        if (item[0] < 0xc0) throw new RlpException("Item is not an RLP list");

        (int prefixLength, int contentLength) = PeekPrefixAndContentLength(_rlpRegion.Span, _cachedPosition);
        Memory<byte> nestedRegion = _rlpRegion.Slice(_cachedPosition + prefixLength, contentLength);

        _memoryOwner.AcquireLease();
        return new RlpItemList(_memoryOwner, nestedRegion);
    }

    public void Dispose() => _memoryOwner.Dispose();

    private int ComputeCount()
    {
        ReadOnlySpan<byte> span = _rlpRegion.Span;
        int position = 0;
        int count = 0;
        while (position < span.Length)
        {
            (int pLen, int cLen) = PeekPrefixAndContentLength(span, position);
            position += pLen + cLen;
            count++;
        }

        return count;
    }

    private int GetPosition(ReadOnlySpan<byte> span, int index)
    {
        int scanFrom;
        int position;
        if (index >= _cachedIndex)
        {
            scanFrom = _cachedIndex;
            position = _cachedPosition;
        }
        else
        {
            scanFrom = 0;
            position = 0;
        }

        for (int i = scanFrom; i < index; i++)
        {
            (int pLen, int cLen) = PeekPrefixAndContentLength(span, position);
            position += pLen + cLen;
        }

        return position;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static (int prefixLength, int contentLength) PeekPrefixAndContentLength(
        ReadOnlySpan<byte> span, int position)
    {
        int prefix = span[position];
        int prefixLengthForContent = RlpHelpers.GetPrefixLengthForContent(prefix);
        if (prefixLengthForContent >= 0)
            return (prefixLengthForContent, RlpHelpers.GetContentLength(prefix));

        int lengthOfLength = RlpHelpers.IsLongString(prefixLengthForContent)
            ? prefix - 183
            : prefix - 247;
        int cLen = RlpHelpers.DeserializeLengthRef(
            ref Unsafe.Add(ref MemoryMarshal.GetReference(span), position + 1),
            lengthOfLength);
        return (1 + lengthOfLength, cLen);
    }
}
