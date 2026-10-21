// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;

namespace Nethermind.Trie.Native;

/// <summary>
/// A pinned generation of a <see cref="NativeNodeBuffer"/>, which the buffer keeps, with every later generation, until
/// the pin is disposed.
/// </summary>
public sealed class NativeNodeBufferPin : IDisposable
{
    private readonly NativeNodeBuffer _buffer;
    private int _disposed;

    internal NativeNodeBufferPin(NativeNodeBuffer buffer, ulong generation, ulong writeBoundary)
    {
        _buffer = buffer;
        Generation = generation;
        WriteBoundary = writeBoundary;
    }

    /// <summary>The pinned generation, later generations being greater.</summary>
    public ulong Generation { get; }

    /// <summary>The first unreserved record position when this pin was acquired.</summary>
    public ulong WriteBoundary { get; }

    public NativeNodeBuffer Buffer => _buffer;

    /// <summary>Pins <see cref="Generation"/> again, for as long as the returned pin.</summary>
    public NativeNodeBufferPin Copy() => _buffer.Pin(Generation, WriteBoundary);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _buffer.Unpin(Generation);
    }
}
