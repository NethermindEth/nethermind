// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Nethermind.Core.Collections;

namespace Nethermind.Network.Discovery;

/// <summary>Owns a received datagram; every reference shares the same idempotent disposal state.</summary>
internal sealed class PooledUdpReceiveResult(IPEndPoint remoteEndPoint, ArrayPoolSpan<byte> buffer) : IDisposable
{
    private readonly ArrayPoolSpan<byte> _buffer = buffer;
    private int _disposed;

    public ReadOnlyMemory<byte> Buffer
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return _buffer.AsReadOnlyMemory();
        }
    }

    public IPEndPoint RemoteEndPoint { get; } = remoteEndPoint;

    /// <summary>
    /// Copies <paramref name="datagram"/> into a pooled buffer owned by the result.
    /// </summary>
    internal static PooledUdpReceiveResult Copy(ReadOnlySpan<byte> datagram, IPEndPoint remoteEndPoint)
    {
        ArrayPoolSpan<byte> buffer = new(datagram.Length);
        datagram.CopyTo(buffer);
        return new PooledUdpReceiveResult(remoteEndPoint, buffer);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _buffer.Dispose();
        }
    }
}
