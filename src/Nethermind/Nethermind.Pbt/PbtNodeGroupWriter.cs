// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using Nethermind.Core.Buffers;
using Nethermind.Pbt.NodeBuffer;

namespace Nethermind.Pbt;

/// <summary>Appends canonical nodes in position order to an owned, growable group payload.</summary>
/// <remarks>
/// Writable spans are borrowed until the next writer operation. The group is composed in a pooled scratch array, which
/// <see cref="Reset"/> keeps for the next group and <see cref="Dispose"/> releases.
/// DetachTo finalizes directly into caller-owned storage.
/// </remarks>
public sealed class PbtNodeGroupWriter<TPath> : IDisposable
    where TPath : struct, IPbtNodePath<TPath>
{
    private const int MaxCapacity = PbtNodeGroupCodec.MaxPayloadLength;
    /// <summary>A scratch size that holds most groups outright, so growth rarely copies more than once.</summary>
    private const int InitialCapacity = 1024;
    private int _bitDepth;
    /// <summary>The group composed so far, from <see cref="ArrayPool{T}.Shared"/>, with room for the header in front.</summary>
    private byte[]? _scratch;
    private OffsetBuffer _offsets;
    private DescendantDeltaBuffer _descendantDeltas;
    private ushort _descendantDeltaMask;
    private uint _availability;
    private int _written;

    public PbtNodeGroupWriter(int bitDepth) => Reset(bitDepth);

    /// <summary>Starts an empty group at <paramref name="bitDepth"/>, keeping the scratch of the group written before.</summary>
    /// <remarks>A fold opens one writer per group it rewrites, so reusing them keeps the fold from allocating one, or its scratch, per group.</remarks>
    public void Reset(int bitDepth)
    {
        Debug.Assert(PbtTwoLevelGroupGeometry.IsGroupDepth(bitDepth), "A group key depth must be a group boundary.");
        _bitDepth = bitDepth;
        ((Span<long>)_descendantDeltas).Clear();
        _descendantDeltaMask = 0;
        DescendantPointers = default;
        _availability = 0;
        _written = 0;
    }

    public PbtGroupLinks DescendantPointers;

    public int WrittenCount => _written;
    /// <summary>The number of leading key bytes inline leaf keys omit in a branch written at <paramref name="position"/>.</summary>
    public int KeyOffsetAt(int position) => PbtNodeCodec.InlineKeyOffset(_bitDepth + PbtTwoLevelGroupGeometry.LocalPathOf(position).Length);

    /// <summary>The size change folded below boundary slot <paramref name="slot"/> since this frame was opened.</summary>
    public long DescendantDelta(int slot) => _descendantDeltas[slot];

    /// <summary>The boundary slots a size change was folded below; every other slot's change is zero.</summary>
    public ushort DescendantDeltaMask => _descendantDeltaMask;

    /// <summary>Records the size change of the groups folded below <paramref name="slot"/>.</summary>
    public void AddDescendantDelta(int slot, long delta)
    {
        _descendantDeltas[slot] += delta;
        _descendantDeltaMask |= (ushort)(1 << slot);
    }

    /// <summary>Appends an entry that composition settles later, reserving <paramref name="length"/> bytes at <paramref name="position"/>.</summary>
    /// <remarks>
    /// The entry is neither omitted nor validated here: composition decides that once the entry's final position is
    /// known, reading it back through <see cref="Entry"/> meanwhile and removing it with <see cref="DropLast"/> if it
    /// must not stay. The group root may be appended too, since composition always drops it below depth zero.
    /// </remarks>
    public Span<byte> Append(int position, int length)
    {
        EnsureCapacity(PbtNodeGroupCodec.HeaderLength + _written + length);
        Span<byte> entry = _scratch.AsSpan(PbtNodeGroupCodec.HeaderLength + _written, length);
        _offsets[position] = (ushort)_written;
        _availability |= 1u << position;
        _written += length;
        return entry;
    }

    /// <summary>The entry written at <paramref name="offset"/>, borrowed until the next writer operation.</summary>
    public ReadOnlyMemory<byte> Entry(int offset, int length) => new(_scratch, PbtNodeGroupCodec.HeaderLength + offset, length);

    /// <summary>Removes the last entry, appended at <paramref name="position"/>, so the next one is written over it.</summary>
    public void DropLast(int position)
    {
        Debug.Assert((_availability & (1u << position)) != 0, "Only an appended entry is dropped.");
        _availability &= ~(1u << position);
        _written = _offsets[position];
    }

    /// <summary>Checks an appended entry once its position is final.</summary>
    [Conditional("DEBUG")]
    public void ValidateEntry(scoped in PbtTraversalPath path, int position, ReadOnlySpan<byte> encoding)
    {
        Debug.Assert(path.BitDepth == _bitDepth);
        PbtNodeGroupCodec.DebugValidateNode(path, position, encoding);
    }

    /// <summary>Appends a validated source group's contiguous entry range at unchanged positions.</summary>
    /// <param name="offsets">The source footer's little-endian offsets of <paramref name="positions"/>, in position order.</param>
    /// <param name="positions">The stored positions <paramref name="entries"/> holds; at least one.</param>
    public void CopyRange(ReadOnlySpan<byte> entries, ReadOnlySpan<byte> offsets, uint positions)
    {
        EnsureCapacity(PbtNodeGroupCodec.HeaderLength + _written + entries.Length);
        entries.CopyTo(_scratch.AsSpan(PbtNodeGroupCodec.HeaderLength + _written));
        int offsetAdjustment = _written - BinaryPrimitives.ReadUInt16LittleEndian(offsets);
        for (uint remaining = positions; remaining != 0; remaining &= remaining - 1)
        {
            _offsets[BitOperations.TrailingZeroCount(remaining)] = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(offsets) + offsetAdjustment);
            offsets = offsets[sizeof(ushort)..];
        }
        _availability |= positions;
        _written += entries.Length;
    }

    /// <summary>The exact canonical payload length, or zero for an empty group.</summary>
    /// <exception cref="InvalidDataException">The group exceeds <see cref="PbtNodeGroupCodec.MaxPayloadLength"/>.</exception>
    public int GetPayloadLength(ReadOnlySpan<long> descendantBytes, ushort candidateSlots)
    {
        if (_availability == 0) return 0;
        ushort descendantMask = PbtNodeGroupCodec.DescendantMask(descendantBytes, candidateSlots);
        int length = PbtNodeGroupCodec.HeaderLength + _written + PbtNodeGroupCodec.GetTrailerLength(_availability, descendantMask, descendantBytes);
        return length <= MaxCapacity ? length : ThrowTooLarge();
    }

    [DoesNotReturn, StackTraceHidden]
    private static int ThrowTooLarge() => throw new InvalidDataException("A PBT node group exceeds the maximum payload length.");

    /// <summary>Finishes the canonical payload in an exactly sized destination.</summary>
    public void DetachTo(Span<byte> payload, ReadOnlySpan<long> descendantBytes, ushort candidateSlots)
    {
        int length = GetPayloadLength(descendantBytes, candidateSlots);
        if (payload.Length != length) throw new ArgumentException("The destination must match the canonical payload length.", nameof(payload));
        if (length != 0)
        {
            ushort descendantMask = PbtNodeGroupCodec.DescendantMask(descendantBytes, candidateSlots);
            int trailerLength = length - PbtNodeGroupCodec.HeaderLength - _written;
            PbtNodeGroupCodec.Header.CopyTo(payload);
            _scratch.AsSpan(PbtNodeGroupCodec.HeaderLength, _written).CopyTo(payload[PbtNodeGroupCodec.HeaderLength..]);
            PbtNodeGroupCodec.WriteFooter(payload.Slice(PbtNodeGroupCodec.HeaderLength + _written, trailerLength), _offsets, _availability, descendantMask,
                descendantBytes);
        }
    }

    public void Dispose()
    {
        if (_scratch is not null) ArrayPool<byte>.Shared.Return(_scratch);
        _scratch = null;
    }

    /// <summary>Sizes the scratch, before anything is written, for a group expected to be about <paramref name="length"/> bytes.</summary>
    /// <remarks>The group's previous size is the estimate, so a rewrite of similar size never grows the scratch.</remarks>
    public void ReserveFirstBuffer(int length)
    {
        Debug.Assert(_written == 0, "The first buffer is reserved before anything is written.");
        if (length <= (_scratch?.Length ?? 0)) return;
        if (_scratch is not null) ArrayPool<byte>.Shared.Return(_scratch);
        _scratch = ArrayPool<byte>.Shared.Rent(Math.Min(MaxCapacity, length));
    }

    private void EnsureCapacity(int required)
    {
        int capacity = _scratch?.Length ?? 0;
        if (capacity >= required) return;
        if (required > MaxCapacity) ThrowTooLarge();
        byte[] grown = ArrayPool<byte>.Shared.Rent(Math.Min(MaxCapacity, Math.Max(required, Math.Max(InitialCapacity, capacity * 2))));
        if (_scratch is { } previous)
        {
            previous.AsSpan(0, PbtNodeGroupCodec.HeaderLength + _written).CopyTo(grown);
            ArrayPool<byte>.Shared.Return(previous);
        }
        _scratch = grown;
    }

    [InlineArray(PbtTwoLevelGroupGeometry.PositionCount)]
    private struct OffsetBuffer
    {
        private ushort _element;
    }

    [InlineArray(PbtTwoLevelGroupGeometry.BoundarySlots)]
    private struct DescendantDeltaBuffer
    {
        private long _element;
    }
}
