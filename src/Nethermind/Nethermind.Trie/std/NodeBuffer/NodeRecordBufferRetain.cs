// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;

namespace Nethermind.Trie.NodeBuffer;

/// <summary>Keeps a generation of a <see cref="NodeRecordBuffer"/>, and every later one, readable in place until disposed.</summary>
/// <remarks>Obtained from <see cref="NodeRecordBuffer.RetainActive"/> or <see cref="Copy"/>, which have already retained <see cref="Generation"/>.</remarks>
public sealed class NodeRecordBufferRetain(NodeRecordBuffer buffer, ulong generation) : INodeRecordBufferRetain
{
    private readonly NodeRecordBuffer _buffer = buffer;
    private int _disposed;

    internal NodeRecordBufferRetain(NodeRecordBuffer buffer, ulong generation, ulong writeBoundary) : this(buffer, generation) => WriteBoundary = writeBoundary;

    /// <summary>The inclusive allocation frontier captured when this retain was acquired.</summary>
    public ulong WriteBoundary { get; } = generation << NodeRecordBuffer.OffsetBits;

    /// <summary>The buffer whose generation is retained.</summary>
    public INodeRecordBuffer Buffer => _buffer;

    /// <summary>The id of the retained generation.</summary>
    public ulong Generation { get; } = generation;

    /// <summary>A further, independently disposed retain of the same generation.</summary>
    public NodeRecordBufferRetain Copy()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        NodeRecordBufferRetain copy = new(_buffer, Generation, WriteBoundary);
        _buffer.Retain(Generation);
        return copy;
    }

    INodeRecordBufferRetain INodeRecordBufferRetain.Copy() => Copy();

    /// <summary>Releases the generation; later calls do nothing.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _buffer.Release(Generation);
    }
}
