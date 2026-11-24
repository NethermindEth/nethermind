// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.ComponentModel;

namespace Nethermind.Serialization.Ssz;

/// <summary>Collects the bytes that the <c>Writer</c> of a generated SSZ type writes.</summary>
/// <remarks>
/// The bytes are held in pooled arrays that are never moved, so a reserved span stays writable while later bytes are added.
/// Also tracks the open nested writers, so writers themselves are immutable and can be copied.
/// After a write throws, the output is unusable. Not thread-safe.
/// </remarks>
/// <param name="chunkSize">The minimum size of each array, in bytes.</param>
public sealed class SszWriter(int chunkSize) : IDisposable
{
    private readonly int _chunkSize = chunkSize > 0 ? chunkSize : throw new ArgumentOutOfRangeException(nameof(chunkSize));
    private Chunk? _first;
    private Chunk? _last;

    /// <summary>The open writers from the outermost, after a sentinel that stands for no writer.</summary>
    private (int Id, int Written)[] _open = new (int, int)[8];
    private int _depth;
    private int _lastId;

    /// <summary>The number of bytes written.</summary>
    public int Length { get; private set; }

    /// <summary>Appends <paramref name="size"/> contiguous bytes, which the caller has to fill.</summary>
    /// <remarks>When the current array has no room for them, it ends where it is and a new array is started.</remarks>
    public Span<byte> Reserve(int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        Length = checked(Length + size);
        Chunk chunk = _last is { } last && size <= last.Free ? last : AddChunk(Math.Max(size, _chunkSize));
        return chunk.Reserve(size);
    }

    /// <summary>Appends a copy of <paramref name="bytes"/>, which may be split between arrays.</summary>
    public void Write(scoped ReadOnlySpan<byte> bytes)
    {
        while (!bytes.IsEmpty)
        {
            int count = Math.Min(bytes.Length, _last is { Free: > 0 } last ? last.Free : _chunkSize);
            bytes[..count].CopyTo(Reserve(count));
            bytes = bytes[count..];
        }
    }

    /// <summary>The written bytes, valid until this writer is disposed or written to.</summary>
    /// <exception cref="InvalidOperationException">A writer is still open, so the bytes are incomplete.</exception>
    public ReadOnlySequence<byte> ToSequence()
    {
        EnsureInnermost(0, "the output");
        if (_first is null || _last is null) return ReadOnlySequence<byte>.Empty;
        _last.End();
        return new ReadOnlySequence<byte>(_first, 0, _last, _last.Memory.Length);
    }

    /// <summary>Returns the arrays to the pool, which invalidates every span and sequence taken from this writer.</summary>
    public void Dispose()
    {
        for (Chunk? chunk = _first; chunk is not null; chunk = chunk.NextChunk) chunk.Return();
        _first = _last = null;
        Length = 0;
        _depth = 0;
    }

    /// <summary>Opens a writer, which has to be closed before the writer that it is nested in is used again.</summary>
    /// <returns>The id of the writer.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public int Open()
    {
        if (++_depth == _open.Length) Array.Resize(ref _open, _depth * 2);
        _open[_depth] = (++_lastId, 0);
        return _lastId;
    }

    /// <summary>Checks that the member at <paramref name="index"/> is the next one for the writer <paramref name="id"/> to write.</summary>
    /// <exception cref="InvalidOperationException">The member is out of order, or a nested writer is open.</exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void BeginMember(int id, int index, string name)
    {
        EnsureInnermost(id, name);
        if (_open[_depth].Written != index) ThrowOutOfOrder(name);
        _open[_depth].Written++;
    }

    /// <inheritdoc cref="BeginMember"/>
    /// <remarks>Also writes the offset of the member, whose bytes start at the end of the written ones.</remarks>
    /// <param name="id">The id of the writer.</param>
    /// <param name="index">The index of the member.</param>
    /// <param name="name">The name of the member.</param>
    /// <param name="offset">The place of the offset in the fixed section.</param>
    /// <param name="start">Where the value that has the member starts.</param>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void BeginVariableMember(int id, int index, string name, Span<byte> offset, int start)
    {
        BeginMember(id, index, name);
        BinaryPrimitives.WriteInt32LittleEndian(offset, Length - start);
    }

    /// <summary>Closes the writer <paramref name="id"/> if it has written <paramref name="count"/> members.</summary>
    /// <remarks>
    /// Does not throw, so that it cannot hide the exception that interrupted a writer. An incomplete writer stays open, which
    /// makes the next use of its parent, or <see cref="ToSequence"/>, throw.
    /// </remarks>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void Close(int id, int count)
    {
        if (_open[_depth] == (id, count)) _depth--;
    }

    /// <summary>Closes the writer <paramref name="id"/>, however many items it has written.</summary>
    internal void Close(int id)
    {
        if (_open[_depth].Id == id) _depth--;
    }

    /// <returns>The number of items that the writer <paramref name="id"/> has written, including these.</returns>
    internal int AddItems(int id, int count)
    {
        EnsureInnermost(id, "an item");
        return _open[_depth].Written = checked(_open[_depth].Written + count);
    }

    private void EnsureInnermost(int id, string name)
    {
        if (_open[_depth].Id != id) ThrowNotInnermost(name);
    }

    private static void ThrowNotInnermost(string name) =>
        throw new InvalidOperationException($"SSZ writer: {name} cannot be accessed while a nested writer is open or incomplete, or after its own writer is closed.");

    private static void ThrowOutOfOrder(string name) =>
        throw new InvalidOperationException($"SSZ writer: {name} is out of order. Every member is written once, first the fixed-size ones and then the variable-size ones, each in SSZ order.");

    private Chunk AddChunk(int size)
    {
        Chunk chunk = new(size);
        if (_last is null) _first = chunk;
        else _last.EndBefore(chunk);
        return _last = chunk;
    }

    private sealed class Chunk(int size) : ReadOnlySequenceSegment<byte>
    {
        private readonly byte[] _array = ArrayPool<byte>.Shared.Rent(size);
        private int _used;

        public int Free => _array.Length - _used;

        public Chunk? NextChunk { get; private set; }

        public Span<byte> Reserve(int length)
        {
            Span<byte> reserved = _array.AsSpan(_used, length);
            _used += length;
            return reserved;
        }

        public void End() => Memory = _array.AsMemory(0, _used);

        public void EndBefore(Chunk next)
        {
            End();
            Next = NextChunk = next;
            next.RunningIndex = RunningIndex + _used;
        }

        public void Return() => ArrayPool<byte>.Shared.Return(_array);
    }
}
