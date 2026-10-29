// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Serialization.FluentRlp;

namespace Nethermind.Serialization.Rlp;

public sealed class RlpItemList : IRlpItemList
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
        : this(memoryOwner, rlpRegion, knownCount: -1)
    {
    }

    public RlpItemList(IMemoryOwner<byte> memoryOwner, Memory<byte> rlpRegion, int knownCount)
    {
        _memoryOwner = new RefCountingMemoryOwner<byte>(memoryOwner);
        _rlpRegion = rlpRegion;
        _prefixLength = GetPrefixLength(rlpRegion.Span);
        _count = knownCount;
        _cachedIndex = 0;
        _cachedPosition = _prefixLength;
    }

    private RlpItemList(RefCountingMemoryOwner<byte> memoryOwner, Memory<byte> rlpRegion, RlpItemList? parent = null)
    {
        _memoryOwner = memoryOwner;
        _rlpRegion = rlpRegion;
        _prefixLength = GetPrefixLength(rlpRegion.Span);
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
            _cachedIndex = index;
            _cachedPosition = position;
            return ReadItem(span, position);
        }
    }

    public int RlpContentLength => _rlpRegion.Length - _prefixLength;
    public ReadOnlySpan<byte> RlpContentSpan => _rlpRegion.Span.Slice(_prefixLength);
    public int RlpLength => _rlpRegion.Length;
    public ReadOnlySpan<byte> RlpSpan => _rlpRegion.Span;

    public void Write(ref RlpWriter writer) => writer.WriteEncoded(_rlpRegion.Span);

    public ReadOnlySpan<byte> ReadContent(int index)
    {
        ReadOnlySpan<byte> rawRlp = this[index];
        return rawRlp[GetPrefixLength(rawRlp)..];
    }

    public IRlpItemList GetNestedItemList(int index)
    {
        ReadOnlySpan<byte> item = this[index];
        if (item[0] < Rlp.EmptyListByte) throw new RlpReaderException("Item is not an RLP list");

        Memory<byte> nestedRegion = _rlpRegion.Slice(_cachedPosition, item.Length);

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

    public static IRlpItemList DecodeList(ref RlpReader ctx, IMemoryOwner<byte> memoryOwner)
    {
        if (!ctx.IsNextSequence) throw new RlpReaderException("RLP does not correspond to a sequence");
        ReadOnlySpan<byte> encoded = ctx.ReadEncodedItem();
        return new RlpItemList(memoryOwner, memoryOwner.Memory.SliceOf(encoded));
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
        _prefixLength = GetPrefixLength(rlpRegion.Span);
        _count = -1;
        _cachedIndex = 0;
        _cachedPosition = _prefixLength;
        _wasDisposed = false;
    }

    private int ComputeCount()
    {
        RlpReader reader = new(_rlpRegion.Span[_prefixLength..]);
        return reader.CountItems();
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
            position += ReadItem(span, position).Length;
        }

        return position;
    }

    /// <summary>Reads the encoded item at <paramref name="position"/>, and checks its prefix and length.</summary>
    private static ReadOnlySpan<byte> ReadItem(ReadOnlySpan<byte> span, int position)
    {
        RlpReader reader = new(span[position..]);
        return reader.ReadEncodedItem();
    }

    /// <summary>Gets the prefix length of an item that <see cref="ReadItem"/> checked; zero for a single byte below <c>0x80</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int GetPrefixLength(ReadOnlySpan<byte> item) => item[0] switch
    {
        < Rlp.EmptyByteArrayByte => 0,
        <= 0xb7 => 1,
        < Rlp.EmptyListByte => 1 + item[0] - 0xb7,
        <= 0xf7 => 1,
        _ => 1 + item[0] - 0xf7,
    };
}
