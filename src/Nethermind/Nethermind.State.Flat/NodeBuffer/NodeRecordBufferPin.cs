// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.NodeBuffer;

/// <summary>Keeps a generation of a <see cref="NodeRecordBuffer"/>, and every later one, readable in place until disposed.</summary>
/// <remarks>Obtained from <see cref="NodeRecordBuffer.PinHead"/> or <see cref="Copy"/>, which have already pinned <see cref="Generation"/>.</remarks>
public sealed class NodeRecordBufferPin(NodeRecordBuffer buffer, ulong generation, ulong writeBoundary) : INodeRecordBufferPin
{
    private readonly NodeRecordBuffer _buffer = buffer;
    private int _disposed;

    /// <summary>The inclusive allocation frontier captured when this pin was taken.</summary>
    public ulong WriteBoundary { get; } = writeBoundary;

    /// <summary>The buffer whose generation is pinned.</summary>
    public INodeRecordBuffer Buffer => _buffer;

    /// <summary>The id of the pinned generation.</summary>
    public ulong Generation { get; } = generation;

    /// <summary>A further, independently disposed pin of the same generation.</summary>
    public NodeRecordBufferPin Copy()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        NodeRecordBufferPin copy = new(_buffer, Generation, WriteBoundary);
        _buffer.AddPin(Generation);
        return copy;
    }

    INodeRecordBufferPin INodeRecordBufferPin.Copy() => Copy();

    /// <summary>Releases the generation; later calls do nothing.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _buffer.RemovePin(Generation);
    }
}
