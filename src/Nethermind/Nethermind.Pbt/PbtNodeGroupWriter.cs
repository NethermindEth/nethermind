// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using Nethermind.Core.Buffers;
using Nethermind.Core.Memory;

namespace Nethermind.Pbt;

/// <summary>Appends canonical nodes in position order to an owned, growable group payload.</summary>
/// <remarks>
/// Writable spans are borrowed until the next writer operation. The group is composed in a pooled scratch array and
/// rented from the memory provider only once, at its final size, when detached; Detach transfers that sole output lease.
/// </remarks>
public sealed class PbtNodeGroupWriter<TPath> : IDisposable
    where TPath : struct, IPbtNodePath<TPath>
{
    private const int MaxCapacity = PbtNodeGroupCodec.MaxPayloadLength;
    /// <summary>A scratch size that holds most groups outright, so growth rarely copies more than once.</summary>
    private const int InitialCapacity = 1024;
    /// <summary>How many disposed writers each thread keeps for <see cref="Rent"/>, enough for the deepest fold's frames.</summary>
    private const int CachedWritersPerThread = 64;
    [ThreadStatic] private static Stack<PbtNodeGroupWriter<TPath>>? t_cache;
    private int _bitDepth;
    private IRefCountingMemoryProvider _memoryProvider;
    /// <summary>The group composed so far, from <see cref="ArrayPool{T}.Shared"/>, with room for the header in front.</summary>
    private byte[]? _scratch;
    private OffsetBuffer _offsets;
    private DescendantDeltaBuffer _descendantDeltas;
    private ushort _descendantDeltaMask;
    private uint _availability;
    private int _written;
    private bool _disposed;
    /// <summary>Whether <see cref="Dispose"/> hands this writer back to the calling thread's cache.</summary>
    private bool _rented;

    private PbtNodeGroupWriter(int bitDepth, IRefCountingMemoryProvider memoryProvider)
    {
        Debug.Assert(PbtThreeLevelGroupGeometry.IsGroupDepth(bitDepth), "A group key depth must be a group boundary.");
        _bitDepth = bitDepth;
        _memoryProvider = memoryProvider;
    }

    /// <summary>A writer as the constructor makes it, reused from the calling thread's cache, which <see cref="Dispose"/> returns it to.</summary>
    /// <remarks>A fold opens one writer per group it rewrites, so reusing them keeps the fold from allocating one per group.</remarks>
    public static PbtNodeGroupWriter<TPath> Rent(int bitDepth, IRefCountingMemoryProvider memoryProvider)
    {
        if (t_cache is not { Count: > 0 } cache) return new(bitDepth, memoryProvider) { _rented = true };
        PbtNodeGroupWriter<TPath> writer = cache.Pop();
        Debug.Assert(PbtThreeLevelGroupGeometry.IsGroupDepth(bitDepth), "A group key depth must be a group boundary.");
        writer._bitDepth = bitDepth;
        writer._memoryProvider = memoryProvider;
        ((Span<long>)writer._descendantDeltas).Clear();
        writer._descendantDeltaMask = 0;
        writer._availability = 0;
        writer._written = 0;
        writer._disposed = false;
        writer._rented = true;
        return writer;
    }

    public int WrittenCount => _written;
    /// <summary>The number of leading key bytes inline leaf keys omit in a branch written at <paramref name="position"/>.</summary>
    public int KeyOffsetAt(int position) => PbtNodeCodec.InlineKeyOffset(_bitDepth + PbtThreeLevelGroupGeometry.LocalPathOf(position).Length);

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

    /// <summary>Finishes the footer and transfers the output lease, or returns null for an empty group.</summary>
    /// <param name="descendantBytes">The summed payload lengths of the groups physically stored below each boundary slot, or empty for none; ignored for an empty group.</param>
    /// <param name="candidateSlots">The slots of <paramref name="descendantBytes"/> that may be nonzero; every other slot is known to be zero.</param>
    public RefCountingMemory? Detach(ReadOnlySpan<long> descendantBytes, ushort candidateSlots)
    {
        if (_availability == 0)
        {
            Release();
            return null;
        }

        ushort descendantMask = PbtNodeGroupCodec.DescendantMask(descendantBytes, candidateSlots);
        int trailerLength = PbtNodeGroupCodec.GetTrailerLength(_availability, descendantMask, descendantBytes);
        int length = PbtNodeGroupCodec.HeaderLength + _written + trailerLength;
        // The snapshot retains the payload's whole capacity until the segment is persisted, so it is rented at its final size.
        RefCountingMemory memory = _memoryProvider.Rent(length);
        Span<byte> payload = memory.GetSpan();
        PbtNodeGroupCodec.Header.CopyTo(payload);
        _scratch.AsSpan(PbtNodeGroupCodec.HeaderLength, _written).CopyTo(payload[PbtNodeGroupCodec.HeaderLength..]);
        PbtNodeGroupCodec.WriteFooter(payload.Slice(PbtNodeGroupCodec.HeaderLength + _written, trailerLength), _offsets, _availability, descendantMask,
            descendantBytes);
        Release();
        return memory;
    }

    public void Dispose()
    {
        Release();
        if (!_rented) return;
        _rented = false;
        Stack<PbtNodeGroupWriter<TPath>> cache = t_cache ??= new(CachedWritersPerThread);
        if (cache.Count < CachedWritersPerThread) cache.Push(this);
    }

    private void Release()
    {
        if (_disposed) return;
        _disposed = true;
        if (_scratch is not null) ArrayPool<byte>.Shared.Return(_scratch);
        _scratch = null;
    }

    /// <summary>Sizes the scratch for a group expected to be about <paramref name="length"/> bytes.</summary>
    /// <remarks>The group's previous size is the estimate, so a rewrite of similar size never grows the scratch.</remarks>
    public void ReserveFirstBuffer(int length)
    {
        if (_scratch is null && length > 0) _scratch = ArrayPool<byte>.Shared.Rent(Math.Min(MaxCapacity, length));
    }

    private void EnsureCapacity(int required)
    {
        int capacity = _scratch?.Length ?? 0;
        if (capacity >= required) return;
        byte[] grown = ArrayPool<byte>.Shared.Rent(Math.Min(MaxCapacity, Math.Max(required, Math.Max(InitialCapacity, capacity * 2))));
        if (_scratch is { } previous)
        {
            previous.AsSpan(0, PbtNodeGroupCodec.HeaderLength + _written).CopyTo(grown);
            ArrayPool<byte>.Shared.Return(previous);
        }
        _scratch = grown;
    }

    [InlineArray(PbtThreeLevelGroupGeometry.PositionCount)]
    private struct OffsetBuffer
    {
        private ushort _element;
    }

    [InlineArray(PbtThreeLevelGroupGeometry.BoundarySlots)]
    private struct DescendantDeltaBuffer
    {
        private long _element;
    }
}
