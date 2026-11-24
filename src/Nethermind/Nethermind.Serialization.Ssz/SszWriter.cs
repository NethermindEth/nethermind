// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.ComponentModel;
using Nethermind.Int256;

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
    private readonly SszChunks _bytes = new(chunkSize);
    private readonly SszNesting _nesting = new();

    /// <summary>Receives the hash trees of the written value, or <c>null</c> to not hash it.</summary>
    public SszTreeWriter? Trees { get; init; }

    /// <summary>The number of bytes written.</summary>
    public int Length => _bytes.Length;

    /// <summary>Appends <paramref name="size"/> contiguous bytes, which the caller has to fill.</summary>
    /// <remarks>When the current array has no room for them, it ends where it is and a new array is started.</remarks>
    public Span<byte> Reserve(int size) => _bytes.Reserve(size);

    /// <summary>Appends a copy of <paramref name="bytes"/>, which may be split between arrays.</summary>
    public void Write(scoped ReadOnlySpan<byte> bytes) => _bytes.Write(bytes);

    /// <summary>The written bytes, valid until this writer is disposed or written to.</summary>
    /// <exception cref="InvalidOperationException">A writer is still open, so the bytes are incomplete.</exception>
    public ReadOnlySequence<byte> ToSequence()
    {
        _nesting.EnsureInnermost(0, "the output");
        return _bytes.ToSequence();
    }

    /// <summary>Returns the arrays to the pool, which invalidates every span and sequence taken from this writer.</summary>
    public void Dispose()
    {
        _bytes.Dispose();
        _nesting.Reset();
    }

    /// <summary>Opens a writer, which has to be closed before the writer that it is nested in is used again.</summary>
    /// <returns>The id of the writer.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public int Open() => _nesting.Open();

    /// <summary>Checks that the member at <paramref name="index"/> is the next one for the writer <paramref name="id"/> to write.</summary>
    /// <exception cref="InvalidOperationException">The member is out of order, or a nested writer is open.</exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void BeginMember(int id, int index, string name) => _nesting.BeginMember(id, index, name);

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
    /// <returns>Whether the writer was closed.</returns>
    public bool Close(int id, int count) => _nesting.Close(id, count);

    /// <summary>Hashes the bytes from <paramref name="start"/> to the end.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void Hash(int start, SszHasher hash, out UInt256 root) => _bytes.Hash(start, hash, out root);

    /// <inheritdoc cref="SszNesting.Close(int)"/>
    internal bool Close(int id) => _nesting.Close(id);

    /// <inheritdoc cref="SszNesting.AddItems"/>
    internal int AddItems(int id, int count) => _nesting.AddItems(id, count);
}
