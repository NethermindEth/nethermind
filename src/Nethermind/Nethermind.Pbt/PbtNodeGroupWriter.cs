// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Nethermind.Core.Buffers;

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

        EnsureCapacity(_written + encodingLength + PbtNodeGroupCodec.TrailerLength);
        _pendingPosition = position;
        _pendingLength = encodingLength;
        return _memory!.GetSpan().Slice(_written, encodingLength);
    }

    /// <summary>Validates and commits the node in the last reserved span.</summary>
    internal void Commit()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pendingPosition < 0) throw new InvalidOperationException("No PBT node is reserved.");
        ReadOnlySpan<byte> encoding = _memory!.GetSpan().Slice(_written, _pendingLength);
        PbtNodeCodec.ValidateExact(encoding);
        if (encoding[0] == 0)
            PbtNodeGroupCodec.ValidateNodeEncoding(PbtFourLevelGroupGeometry.PathOf(_groupKey, _pendingPosition), encoding);
        _offsets[_pendingPosition] = (ushort)_written;
        _availability |= 1u << _pendingPosition;
        _written += _pendingLength;
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

        Span<byte> footer = _memory!.GetSpan().Slice(_written, PbtNodeGroupCodec.TrailerLength);
        for (int position = 0; position < PbtNodeGroupCodec.PositionCount; position++)
            BinaryPrimitives.WriteUInt16LittleEndian(footer[(position * sizeof(ushort))..], _offsets[position]);
        BinaryPrimitives.WriteUInt32LittleEndian(footer[(PbtNodeGroupCodec.PositionCount * sizeof(ushort))..], _availability);
        RefCountingMemory memory = _memory;
        memory.Shrink(_written + PbtNodeGroupCodec.TrailerLength);
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
        int nextCapacity = Math.Min(MaxEntriesLength + PbtNodeGroupCodec.TrailerLength, Math.Max(required, capacity * 2));
        RefCountingMemory grown = _memoryProvider.Rent(nextCapacity);
        if (_memory is { } previous)
        {
            previous.GetSpan()[.._written].CopyTo(grown.GetSpan());
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
