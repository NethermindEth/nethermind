// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Collections;

namespace Nethermind.Serialization.Rlp;

public sealed class RlpByteArrayList : IByteArrayList
{
    private readonly IMemoryOwner<byte> _memoryOwner;
    private readonly Memory<byte> _memory;
    private readonly int _dataStart;
    private readonly int _count;

    private int _cachedIndex;
    private int _cachedPosition;

    public RlpByteArrayList(IMemoryOwner<byte> memoryOwner, int dataStart, int count)
    {
        _memoryOwner = memoryOwner;
        _memory = memoryOwner.Memory;
        _dataStart = dataStart;
        _count = count;
        _cachedIndex = 0;
        _cachedPosition = dataStart;
    }

    public int Count => _count;

    public ReadOnlySpan<byte> this[int index]
    {
        get
        {
            ReadOnlySpan<byte> span = _memory.Span;
            int position = GetPosition(span, index);
            (int prefixLength, int contentLength) = PeekPrefixAndContentLength(span, position);
            _cachedIndex = index;
            _cachedPosition = position;
            return contentLength == 0
                ? ReadOnlySpan<byte>.Empty
                : span.Slice(position + prefixLength, contentLength);
        }
    }

    public void Dispose() => _memoryOwner.Dispose();

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
            position = _dataStart;
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
