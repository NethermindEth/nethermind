// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;

namespace Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Call;

/// <summary>A callTracer result: the root frame and the buffer that holds the copied bytes of all its frames.</summary>
/// <remarks>Every trace already allocates its custom result once, so the buffer lives here rather than in each frame.</remarks>
internal sealed class NativeCallTracerResult : GethLikeCustomTrace, IDisposable
{
    private const int Alive = 0;
    private const int Disposed = 1;
    private readonly NativeCallTracerCallFrame _root;
    private NativeCallTracerBuffer _buffer;
    private int _disposed = Alive;

    /// <summary>Takes over <paramref name="buffer"/> and returns it, with <paramref name="root"/>, on <see cref="Dispose"/>.</summary>
    public NativeCallTracerResult(NativeCallTracerCallFrame root, ref NativeCallTracerBuffer buffer)
    {
        _root = root;
        Value = root;
        _buffer = buffer;
        buffer = default;
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, Disposed, Alive) == Alive)
        {
            _root.Dispose();
            _buffer.Release();
        }
    }
}
