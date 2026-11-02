// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Pbt.NodeBuffer;

/// <summary>Keeps a generation of a <see cref="PbtGroupRecordBuffer"/>, and every later one, readable in place until disposed.</summary>
/// <remarks>Obtained from <see cref="PbtGroupRecordBuffer.PinHead"/> or <see cref="Copy"/>, which have already pinned <see cref="Generation"/>.</remarks>
public sealed class PbtGroupRecordBufferPin(PbtGroupRecordBuffer buffer, ulong generation, ulong writeBoundary) : IPbtGroupRecordBufferPin
{
    private readonly PbtGroupRecordBuffer _buffer = buffer;
    private int _disposed;

    /// <summary>The inclusive allocation frontier captured when this pin was taken.</summary>
    public ulong WriteBoundary { get; } = writeBoundary;

    /// <summary>The buffer whose generation is pinned.</summary>
    public IPbtGroupRecordBuffer Buffer => _buffer;

    /// <summary>The id of the pinned generation.</summary>
    public ulong Generation { get; } = generation;

    /// <summary>A further, independently disposed pin of the same generation.</summary>
    public PbtGroupRecordBufferPin Copy()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        PbtGroupRecordBufferPin copy = new(_buffer, Generation, WriteBoundary);
        _buffer.AddPin(Generation);
        return copy;
    }

    IPbtGroupRecordBufferPin IPbtGroupRecordBufferPin.Copy() => Copy();

    /// <summary>Releases the generation; later calls do nothing.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _buffer.RemovePin(Generation);
    }
}
