// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Runtime.CompilerServices;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

/// <summary>Appends canonical nodes in position order to an owned, growable group payload.</summary>
/// <remarks>Writable spans are borrowed until the next writer operation. Detach transfers the sole output lease.</remarks>
internal sealed class PbtNodeGroupWriter<TPath> : IDisposable
    where TPath : struct, IPbtNodePath<TPath>
{
    private const int MaxEntriesLength = ushort.MaxValue;
    private const int MaxCapacity = PbtNodeGroupCodec.MaxPayloadLength;
    /// <summary>A pool bucket that holds most groups outright, so growth rarely copies more than once.</summary>
    private const int InitialCapacity = 1024;
    private readonly int _bitDepth;
    private readonly IRefCountingMemoryProvider _memoryProvider;
    private readonly PbtPrefixlessBranchOmission _omission;
    private RefCountingMemory? _memory;
    private OffsetBuffer _offsets;
    private DescendantDeltaBuffer _descendantDeltas;
    private uint _availability;
    private int _written;
    private int _lastPosition = -1;
    private int _pendingPosition = -1;
    private int _pendingLength;
    private bool _disposed;

    internal PbtNodeGroupWriter(int bitDepth, IRefCountingMemoryProvider memoryProvider, PbtPrefixlessBranchOmission omission)
    {
        ArgumentNullException.ThrowIfNull(memoryProvider);
        Debug.Assert(PbtFourLevelGroupGeometry.IsGroupDepth(bitDepth), "A group key depth must be a four-level boundary.");
        _bitDepth = bitDepth;
        _memoryProvider = memoryProvider;
        _omission = omission;
    }

    internal int WrittenCount => _written;
    internal int LastPosition => _lastPosition;

    /// <summary>The size change folded below boundary slot <paramref name="slot"/> since this frame was opened.</summary>
    internal long DescendantDelta(int slot) => _descendantDeltas[slot];

    /// <summary>The summed size change folded below every boundary slot.</summary>
    internal long DescendantDelta()
    {
        long descendantDelta = 0;
        foreach (long slotDelta in _descendantDeltas) descendantDelta += slotDelta;
        return descendantDelta;
    }

    /// <summary>Records the size change of the groups folded below <paramref name="slot"/>.</summary>
    internal void AddDescendantDelta(int slot, long delta) => _descendantDeltas[slot] += delta;

    /// <summary>Reserves the exact encoding length for the next position without committing it.</summary>
    internal Span<byte> GetSpan(int position, int encodingLength)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)position >= PbtNodeGroupCodec.PositionCount
            || (position == PbtFourLevelGroupGeometry.RootPosition && _bitDepth != 0))
            throw new ArgumentOutOfRangeException(nameof(position));
        ValidatePositionOrder(position);
        if (encodingLength <= 0 || encodingLength > MaxEntriesLength - _written)
            throw new InvalidDataException("PBT node group entries exceed the uint16 offset limit or have an invalid length.");

        EnsureCapacity(PbtNodeGroupCodec.HeaderLength + _written + encodingLength);
        _pendingPosition = position;
        _pendingLength = encodingLength;
        return _memory!.GetSpan().Slice(PbtNodeGroupCodec.HeaderLength + _written, encodingLength);
    }

    /// <summary>Commits the node in the last reserved span.</summary>
    internal void Commit(scoped in PbtTraversalPath path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Debug.Assert(path.BitDepth == _bitDepth);
        ValidateReservedNode();
        ReadOnlySpan<byte> encoding = _memory!.GetSpan().Slice(PbtNodeGroupCodec.HeaderLength + _written, _pendingLength);
        ValidateEncoding(path, _pendingPosition, encoding);
        if (!PbtNodeGroupCodec.ShouldOmit(_omission, _pendingPosition, encoding))
        {
            _offsets[_pendingPosition] = (ushort)_written;
            _availability |= 1u << _pendingPosition;
            _written += _pendingLength;
        }
        _lastPosition = _pendingPosition;
        _pendingPosition = -1;
        _pendingLength = 0;
    }

    /// <summary>Appends an entry that composition settles later, reserving <paramref name="length"/> bytes at <paramref name="position"/>.</summary>
    /// <remarks>
    /// The entry is neither omitted nor validated here: composition decides that once the entry's final position is
    /// known, reading it back through <see cref="Entry"/> meanwhile and removing it with <see cref="DropLast"/> if it
    /// must not stay. The group root may be appended too, since composition always drops it below depth zero.
    /// </remarks>
    internal Span<byte> Append(int position, int length)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateCommitted();
        ValidatePositionOrder(position);
        if (length <= 0 || length > MaxEntriesLength - _written)
            throw new InvalidDataException("PBT node group entries exceed the uint16 offset limit or have an invalid length.");
        EnsureCapacity(PbtNodeGroupCodec.HeaderLength + _written + length);
        Span<byte> entry = _memory!.GetSpan().Slice(PbtNodeGroupCodec.HeaderLength + _written, length);
        _offsets[position] = (ushort)_written;
        _availability |= 1u << position;
        _written += length;
        _lastPosition = position;
        return entry;
    }

    /// <summary>The entry written at <paramref name="offset"/>, borrowed until the next writer operation.</summary>
    internal ReadOnlyMemory<byte> Entry(int offset, int length) => _memory!.Memory.Slice(PbtNodeGroupCodec.HeaderLength + offset, length);

    /// <summary>Removes the last entry, appended at <paramref name="position"/>, so the next one is written over it.</summary>
    internal void DropLast(int position)
    {
        Debug.Assert((_availability & (1u << position)) != 0, "Only an appended entry is dropped.");
        _availability &= ~(1u << position);
        _written = _offsets[position];
        // Every earlier entry sits below the dropped one, so its position may be written again.
        _lastPosition = position - 1;
    }

    /// <summary>Whether <paramref name="encoding"/> at <paramref name="position"/> is left out of the group and rebuilt from its children.</summary>
    internal bool Omits(int position, ReadOnlySpan<byte> encoding) => PbtNodeGroupCodec.ShouldOmit(_omission, position, encoding);

    /// <summary>Checks an appended entry once its position is final.</summary>
    [Conditional("DEBUG")]
    internal void ValidateEntry(scoped in PbtTraversalPath path, int position, ReadOnlySpan<byte> encoding)
    {
        Debug.Assert(path.BitDepth == _bitDepth);
        ValidateEncoding(path, position, encoding);
    }

    /// <summary>Copies and commits an existing canonical encoding.</summary>
    internal void Write(scoped in PbtTraversalPath path, int position, ReadOnlySpan<byte> encoding)
    {
        encoding.CopyTo(GetSpan(position, encoding.Length));
        Commit(path);
    }

    /// <summary>Emits the resolved subtree root at its final position and clears the borrowed value.</summary>
    /// <remarks>A leaf below the root is stored inline by the branch composed above it, so only its hash is returned.</remarks>
    internal ValueHash256 Write<TKey>(scoped in PbtTraversalPath path, int position, int depth, ref TrieUpdater<TKey, TPath>.TraversalSubtree node, TrieUpdaterMetrics? metrics)
        where TKey : unmanaged, IPbtKey<TKey>
    {
        if (node.IsEmpty) return default;
        if (node.IsLeaf && position != PbtFourLevelGroupGeometry.RootPosition)
        {
            ValueHash256 leafHash = node.Node.LeafHash;
            node.Clear();
            return leafHash;
        }
        Span<byte> encoding = GetSpan(position, node.EncodedLength(depth));
        ValueHash256 hash = node.Encode(encoding, depth, metrics);
        Commit(path);
        node.Clear();
        return hash;
    }

    /// <summary>Appends a validated source group's contiguous entry range at unchanged positions.</summary>
    internal int CopyRange(ReadOnlySpan<byte> entries, ReadOnlySpan<int> offsets, ReadOnlySpan<int> lengths,
        int firstPosition, int lastPosition)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateCommitted();
        ValidatePositionOrder(firstPosition);
        if (entries.Length > MaxEntriesLength - _written)
            throw new InvalidDataException("PBT node group entries exceed the uint16 offset limit.");
        EnsureCapacity(PbtNodeGroupCodec.HeaderLength + _written + entries.Length);
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
    /// <param name="descendantBytes">The summed payload lengths of the groups physically stored below each boundary slot, or empty for none; ignored for an empty group.</param>
    internal RefCountingMemory? Detach(ReadOnlySpan<long> descendantBytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateCommitted();
        if (_availability == 0)
        {
            Dispose();
            return null;
        }

        int trailerLength = PbtNodeGroupCodec.GetTrailerLength(_availability, PbtNodeGroupCodec.DescendantMask(descendantBytes));
        int length = PbtNodeGroupCodec.HeaderLength + _written + trailerLength;
        EnsureCapacity(length);
        PbtNodeGroupCodec.Header.CopyTo(_memory!.GetSpan());
        Span<byte> footer = _memory!.GetSpan().Slice(PbtNodeGroupCodec.HeaderLength + _written, trailerLength);
        PbtNodeGroupCodec.WriteFooter(footer, _offsets, _availability, descendantBytes);
        RefCountingMemory memory = _memory;
        // The snapshot retains the detached buffer's whole capacity until the segment is persisted, so
        // the payload moves whenever a re-rent would land it in a smaller bucket.
        if (_memoryProvider.RoundUpCapacity(length) < memory.Capacity)
        {
            RefCountingMemory compacted = _memoryProvider.Rent(length);
            memory.GetSpan()[..length].CopyTo(compacted.GetSpan());
            ((IDisposable)memory).Dispose();
            memory = compacted;
        }
        else
        {
            memory.Shrink(length);
        }

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

    [Conditional("DEBUG")]
    private void ValidatePositionOrder(int position)
    {
        if (position <= _lastPosition) throw new InvalidDataException("PBT nodes must be written in increasing position order.");
    }

    [Conditional("DEBUG")]
    private void ValidateReservedNode()
    {
        if (_pendingPosition < 0) throw new InvalidOperationException("No PBT node is reserved.");
    }

    [Conditional("DEBUG")]
    private void ValidateCommitted()
    {
        if (_pendingPosition >= 0) throw new InvalidOperationException("A reserved PBT node has not been committed.");
    }

    [Conditional("DEBUG")]
    private static void ValidateEncoding(scoped in PbtTraversalPath path, int position, ReadOnlySpan<byte> encoding)
    {
        PbtNodeCodec.ValidateExact(encoding);
        PbtNodeGroupReader.ValidateLeafPath(path, position, encoding);
    }

    /// <summary>Sizes the first payload buffer for a group expected to be about <paramref name="length"/> bytes.</summary>
    /// <remarks>
    /// The group's previous size is the estimate, so a rewrite of similar size fills one buffer that detaching keeps
    /// rather than compacts, where the default first buffer would be compacted for every small group.
    /// </remarks>
    internal void ReserveFirstBuffer(int length)
    {
        if (_memory is null && length > 0) _memory = _memoryProvider.Rent(Math.Min(MaxCapacity, length));
    }

    private void EnsureCapacity(int required)
    {
        int capacity = _memory?.GetSpan().Length ?? 0;
        if (capacity >= required) return;
        int nextCapacity = Math.Min(MaxCapacity, Math.Max(required, Math.Max(InitialCapacity, capacity * 2)));
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

    [InlineArray(PbtNodeGroupCodec.DescendantSlots)]
    private struct DescendantDeltaBuffer
    {
        private long _element;
    }
}
