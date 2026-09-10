// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

/// <summary>Appends canonical nodes in position order to an owned, growable group payload.</summary>
/// <remarks>Writable spans are borrowed until the next writer operation. Detach transfers the sole output lease.</remarks>
internal sealed class PbtNodeGroupWriter : IDisposable
{
    private const int MaxEntriesLength = ushort.MaxValue;
    private readonly IPbtNodePath _groupKey;
    private readonly IRefCountingMemoryProvider _memoryProvider;
    private RefCountingMemory? _memory;
    private OffsetBuffer _offsets;
    private uint _availability;
    private int _written;
    private int _lastPosition = -1;
    private int _pendingPosition = -1;
    private int _pendingLength;
    private bool _disposed;

    internal PbtNodeGroupWriter(IPbtNodePath groupKey, IRefCountingMemoryProvider memoryProvider)
    {
        ArgumentNullException.ThrowIfNull(groupKey);
        ArgumentNullException.ThrowIfNull(memoryProvider);
        if (!PbtFourLevelGroupGeometry.IsGroupDepth(groupKey.BitDepth))
            throw new ArgumentException("A group key depth must be a four-level boundary.", nameof(groupKey));
        _groupKey = groupKey;
        _memoryProvider = memoryProvider;
    }

    internal int WrittenCount => _written;
    internal uint Availability => _availability;
    internal int NextPosition { get; set; }
    internal int ChangedNodes { get; set; }

    /// <summary>Reserves the exact encoding length for the next position without committing it.</summary>
    internal Span<byte> GetSpan(int position, int encodingLength)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)position >= PbtNodeGroupCodec.PositionCount
            || (position == PbtFourLevelGroupGeometry.RootPosition && _groupKey.BitDepth != 0))
            throw new ArgumentOutOfRangeException(nameof(position));
        if (position <= _lastPosition) throw new InvalidDataException("PBT nodes must be written in increasing position order.");
        if (encodingLength <= 0 || encodingLength > MaxEntriesLength - _written)
            throw new InvalidDataException("PBT node group entries exceed the uint16 offset limit or have an invalid length.");

        EnsureCapacity(PbtNodeGroupCodec.HeaderLength + _written + encodingLength + PbtNodeGroupCodec.MaxTrailerLength);
        _pendingPosition = position;
        _pendingLength = encodingLength;
        return _memory!.GetSpan().Slice(PbtNodeGroupCodec.HeaderLength + _written, encodingLength);
    }

    /// <summary>Validates and commits the node in the last reserved span.</summary>
    internal void Commit()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pendingPosition < 0) throw new InvalidOperationException("No PBT node is reserved.");
        ReadOnlySpan<byte> encoding = _memory!.GetSpan().Slice(PbtNodeGroupCodec.HeaderLength + _written, _pendingLength);
        PbtNodeCodec.ValidateExact(encoding);
        PbtNodeGroupReader.ValidateLeafPath(_groupKey, _pendingPosition, encoding);
        if (!PbtNodeGroupCodec.ShouldOmit(_pendingPosition, encoding))
        {
            _offsets[_pendingPosition] = (ushort)_written;
            _availability |= 1u << _pendingPosition;
            _written += _pendingLength;
        }
        _lastPosition = _pendingPosition;
        _pendingPosition = -1;
        _pendingLength = 0;
    }

    /// <summary>Copies and commits an existing canonical encoding.</summary>
    internal void Write(int position, ReadOnlySpan<byte> encoding)
    {
        encoding.CopyTo(GetSpan(position, encoding.Length));
        Commit();
    }

    internal ValueHash256 Write<TKey, TPath>(ref GroupFrameReader<TKey, TPath> reader, int position, int depth, ref TrieUpdater<TKey, TPath>.Subtree node)
        where TKey : struct, IPbtKey<TKey>
        where TPath : class, IPbtNodePath<TPath>
    {
        if (node.IsEmpty) return default;
        reader.Resolve(this, ref node);
        if (position < NextPosition) throw new InvalidOperationException("PBT nodes must be placed in increasing position order.");
        CopyUntouchedBefore(ref reader, position);
        Span<byte> encoding = GetSpan(position, node.EncodedLength(depth));
        ValueHash256 hash = node.Encode(encoding, depth);
        ReadOnlySpan<byte> storedEncoding = PbtNodeGroupCodec.ShouldOmit(position, encoding) ? [] : encoding;
        if (!reader.GetEncoding(position).Span.SequenceEqual(storedEncoding)) ChangedNodes++;
        Commit();
        NextPosition = position + 1;
        node.Dispose();
        return hash;
    }

    internal void Flush<TKey, TPath>(IPbtStore store, TrieUpdaterMetrics? metrics, ref GroupFrameReader<TKey, TPath> reader)
        where TKey : struct, IPbtKey<TKey>
        where TPath : class, IPbtNodePath<TPath>
    {
        if (reader.Taken == 0 && Availability == 0) return;
        CopyUntouchedBefore(ref reader, PbtNodeGroupCodec.PositionCount);
        if (ChangedNodes == 0) return;

        using RefCountingMemory? payload = Detach();
        store.SetNodeGroup(reader.GroupKey, payload);
        metrics?.AddEmittedNodeWrites(ChangedNodes);
    }

    internal void CopyUntouchedBefore<TKey, TPath>(ref GroupFrameReader<TKey, TPath> reader, int endPosition)
        where TKey : struct, IPbtKey<TKey>
        where TPath : class, IPbtNodePath<TPath>
    {
        while (NextPosition < endPosition)
        {
            int position = NextPosition++;
            ReadOnlySpan<byte> previous = reader.GetEncoding(position).Span;
            if (previous.IsEmpty) continue;
            if ((reader.Taken & (1U << position)) != 0) ChangedNodes++;
            else Write(position, previous);
        }
    }

    /// <summary>Appends a validated source group's contiguous entry range at unchanged positions.</summary>
    internal int CopyRange(ReadOnlySpan<byte> entries, ReadOnlySpan<int> offsets, ReadOnlySpan<int> lengths,
        int firstPosition, int lastPosition)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pendingPosition >= 0) throw new InvalidOperationException("A reserved PBT node has not been committed.");
        if (firstPosition <= _lastPosition) throw new InvalidDataException("PBT nodes must be written in increasing position order.");
        if (entries.Length > MaxEntriesLength - _written)
            throw new InvalidDataException("PBT node group entries exceed the uint16 offset limit.");
        EnsureCapacity(PbtNodeGroupCodec.HeaderLength + _written + entries.Length + PbtNodeGroupCodec.MaxTrailerLength);
        entries.CopyTo(_memory!.GetSpan()[(PbtNodeGroupCodec.HeaderLength + _written)..]);
        int offsetAdjustment = _written - offsets[firstPosition];
        int copiedNodes = 0;
        for (int position = firstPosition; position <= lastPosition; position++)
        {
            if (lengths[position] == 0) continue;
            _offsets[position] = (ushort)(offsets[position] + offsetAdjustment);
            _availability |= 1U << position;
            copiedNodes++;
        }
        _written += entries.Length;
        _lastPosition = lastPosition;
        return copiedNodes;
    }

    /// <summary>Finishes the footer and transfers the output lease, or returns null for an empty group.</summary>
    internal RefCountingMemory? Detach()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pendingPosition >= 0) throw new InvalidOperationException("A reserved PBT node has not been committed.");
        if (_availability == 0)
        {
            Dispose();
            return null;
        }

        PbtNodeGroupCodec.Header.CopyTo(_memory!.GetSpan());
        int trailerLength = PbtNodeGroupCodec.GetTrailerLength(_availability);
        Span<byte> footer = _memory!.GetSpan().Slice(PbtNodeGroupCodec.HeaderLength + _written, trailerLength);
        PbtNodeGroupCodec.WriteFooter(footer, _offsets, _availability);
        RefCountingMemory memory = _memory;
        memory.Shrink(PbtNodeGroupCodec.HeaderLength + _written + trailerLength);
        _memory = null;
        _disposed = true;
        return memory;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ((IDisposable?)_memory)?.Dispose();
        _memory = null;
    }

    private void EnsureCapacity(int required)
    {
        int capacity = _memory?.GetSpan().Length ?? 0;
        if (capacity >= required) return;
        int nextCapacity = Math.Min(PbtNodeGroupCodec.HeaderLength + MaxEntriesLength + PbtNodeGroupCodec.MaxTrailerLength, Math.Max(required, capacity * 2));
        RefCountingMemory grown = _memoryProvider.Rent(nextCapacity);
        if (_memory is { } previous)
        {
            previous.GetSpan()[..(PbtNodeGroupCodec.HeaderLength + _written)].CopyTo(grown.GetSpan());
            ((IDisposable)previous).Dispose();
        }
        _memory = grown;
    }

    [InlineArray(PbtNodeGroupCodec.PositionCount)]
    private struct OffsetBuffer
    {
        private ushort _element;
    }
}
