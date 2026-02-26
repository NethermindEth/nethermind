// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;

namespace Nethermind.Serialization.Rlp;

public ref struct RefRlpListReader
{
    private readonly ReadOnlySpan<byte> _data;
    private readonly int _dataStart;
    private readonly int _dataEnd;

    private int _count;
    private int _cachedIndex;
    private int _cachedPosition;

    public RefRlpListReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        (int prefixLength, int contentLength) = RlpItemList.PeekPrefixAndContentLength(data, 0);
        _dataStart = prefixLength;
        _dataEnd = prefixLength + contentLength;
        _count = -1;
        _cachedIndex = 0;
        _cachedPosition = prefixLength;
    }

    public int Count
    {
        get
        {
            if (_count < 0)
            {
                _count = ComputeCount();
            }
            return _count;
        }
    }

    public ReadOnlySpan<byte> this[int index]
    {
        get
        {
            int position = GetPosition(index);
            (int prefixLength, int contentLength) = RlpItemList.PeekPrefixAndContentLength(_data, position);
            _cachedIndex = index;
            _cachedPosition = position;
            return _data.Slice(position, prefixLength + contentLength);
        }
    }

    private int GetPosition(int index)
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
            (int pLen, int cLen) = RlpItemList.PeekPrefixAndContentLength(_data, position);
            position += pLen + cLen;
        }

        return position;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private int ComputeCount()
    {
        int count = 0;
        int position = _dataStart;
        while (position < _dataEnd)
        {
            (int pLen, int cLen) = RlpItemList.PeekPrefixAndContentLength(_data, position);
            position += pLen + cLen;
            count++;
        }
        return count;
    }
}
