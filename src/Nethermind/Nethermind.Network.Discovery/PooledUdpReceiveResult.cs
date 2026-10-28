// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Nethermind.Core.Collections;

namespace Nethermind.Network.Discovery;

internal readonly struct PooledUdpReceiveResult(IPEndPoint remoteEndPoint, ArrayPoolSpan<byte> buffer)
{
    private readonly bool _hasBuffer = true;
    private readonly ArrayPoolSpan<byte> _buffer = buffer;

    public ReadOnlyMemory<byte> Buffer => _buffer.AsReadOnlyMemory();

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

    internal void Dispose()
    {
        if (_hasBuffer)
        {
            _buffer.Dispose();
        }
    }
}
