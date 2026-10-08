// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Network.Discovery.Test;

/// <summary>
/// <see cref="IDatagramSocket"/> that records sent datagrams instead of delivering them and never receives.
/// </summary>
internal sealed class RecordingDatagramSocket : IDatagramSocket
{
    public ConcurrentQueue<(byte[] Data, IPEndPoint Destination)> Sent { get; } = new();

    public IPEndPoint? LocalEndpoint { get; private set; }

    public void Bind(IPEndPoint localEndpoint) => LocalEndpoint = localEndpoint;

    public ValueTask SendToAsync(ReadOnlyMemory<byte> datagram, IPEndPoint remoteEndpoint, CancellationToken cancellationToken = default)
    {
        Sent.Enqueue((datagram.ToArray(), remoteEndpoint));
        return ValueTask.CompletedTask;
    }

    public ValueTask<int> ReceiveFromAsync(Memory<byte> buffer, SocketAddress receivedAddress, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public void Dispose()
    {
    }
}
