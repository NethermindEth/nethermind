// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
namespace Nethermind.Serialization.Rlp;

public sealed partial class RlpItemList : IRlpItemList
{
    private readonly RefCountingMemoryOwner<byte> _memoryOwner;
    private Memory<byte> _rlpRegion;
    private int _prefixLength;
    private int _count;

    private int _cachedIndex;
    private int _cachedPosition;

    private RlpItemList? _pooledChild;
    private RlpItemList? _parent;

    public RlpItemList(IMemoryOwner<byte> memoryOwner, Memory<byte> rlpRegion)
    {
        _memoryOwner = new RefCountingMemoryOwner<byte>(memoryOwner);
        _rlpRegion = rlpRegion;
        _prefixLength = PeekPrefixAndContentLength(rlpRegion.Span, 0).prefixLength;
        _count = -1;
        _cachedIndex = 0;
        _cachedPosition = _prefixLength;
    }

    private RlpItemList(RefCountingMemoryOwner<byte> memoryOwner, Memory<byte> rlpRegion, RlpItemList? parent = null)
    {
        _memoryOwner = memoryOwner;
        _rlpRegion = rlpRegion;
        _prefixLength = PeekPrefixAndContentLength(rlpRegion.Span, 0).prefixLength;
        _count = -1;
        _cachedIndex = 0;
        _cachedPosition = _prefixLength;
        _parent = parent;
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

    public int RlpContentLength => _rlpRegion.Length - _prefixLength;
    public ReadOnlySpan<byte> RlpContentSpan => _rlpRegion.Span.Slice(_prefixLength);
    public int RlpLength => _rlpRegion.Length;
    public ReadOnlySpan<byte> RlpSpan => _rlpRegion.Span;

    public void Write(RlpStream stream) => stream.Write(_rlpRegion.Span);

    public ReadOnlySpan<byte> ReadContent(int index)
    {
        ReadOnlySpan<byte> rawRlp = this[index];
        (int prefixLength, int contentLength) = PeekPrefixAndContentLength(rawRlp, 0);
        return contentLength == 0 ? ReadOnlySpan<byte>.Empty : rawRlp.Slice(prefixLength, contentLength);
    }

    public RefRlpListReader CreateNestedReader(int index) => new(this[index]);

    public IRlpItemList GetNestedItemList(int index)
    {
        ReadOnlySpan<byte> item = this[index];
        if (item[0] < 0xc0) throw new RlpException("Item is not an RLP list");

        (int prefixLength, int contentLength) = PeekPrefixAndContentLength(_rlpRegion.Span, _cachedPosition);
        Memory<byte> nestedRegion = _rlpRegion.Slice(_cachedPosition, prefixLength + contentLength);

        RlpItemList? child = _pooledChild;
        if (child is not null)
        {
            _pooledChild = null;
            child.Reset(nestedRegion);
            return child;
        }

        _memoryOwner.AcquireLease();
        return new RlpItemList(_memoryOwner, nestedRegion, parent: this);
    }

    public static IRlpItemList DecodeList(ref Rlp.ValueDecoderContext ctx, IMemoryOwner<byte> memoryOwner)
    {
        int prefixStart = ctx.Position;
        int innerLength = ctx.ReadSequenceLength();
        int totalLength = (ctx.Position - prefixStart) + innerLength;
        RlpItemList list = new(memoryOwner, memoryOwner.Memory.Slice(prefixStart, totalLength));
        ctx.Position = prefixStart + totalLength;
        return list;
    }

    private bool _wasDisposed = false;
    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _wasDisposed, true, false)) return;

        if (_parent is not null)
        {
            if (!_parent._wasDisposed && _parent._pooledChild is null)
            {
                _parent._pooledChild = this;
                return;
            }
            // Parent already disposed or already has a pooled child, fall through to release lease
        }

        _pooledChild?.DisposePooledLease();
        _memoryOwner.Dispose();
    }

    private void DisposePooledLease()
    {
        _pooledChild?.DisposePooledLease();
        _memoryOwner.Dispose();
    }

    private void Reset(Memory<byte> rlpRegion)
    {
        _rlpRegion = rlpRegion;
        _prefixLength = PeekPrefixAndContentLength(rlpRegion.Span, 0).prefixLength;
        _count = -1;
        _cachedIndex = 0;
        _cachedPosition = _prefixLength;
        _wasDisposed = false;
    }

    private int ComputeCount()
    {
        ReadOnlySpan<byte> span = _rlpRegion.Span;
        int position = _prefixLength;
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
            position = _prefixLength;
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
