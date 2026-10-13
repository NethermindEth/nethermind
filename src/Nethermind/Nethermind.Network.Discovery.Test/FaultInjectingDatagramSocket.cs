// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Network.Discovery.Test;

/// <summary>
/// <see cref="IDatagramSocket"/> that fails its first receive with <paramref name="error"/> and otherwise delegates
/// to <paramref name="inner"/>.
/// </summary>
internal sealed class FaultInjectingDatagramSocket(IDatagramSocket inner, SocketError error) : IDatagramSocket
{
    private int _faultPending = 1;

    public IPEndPoint? LocalEndpoint => inner.LocalEndpoint;

    public void Bind(IPEndPoint localEndpoint) => inner.Bind(localEndpoint);

    public ValueTask SendToAsync(ReadOnlyMemory<byte> datagram, IPEndPoint remoteEndpoint, CancellationToken cancellationToken = default)
        => inner.SendToAsync(datagram, remoteEndpoint, cancellationToken);

    public ValueTask<int> ReceiveFromAsync(Memory<byte> buffer, SocketAddress receivedAddress, CancellationToken cancellationToken = default)
        => Interlocked.Exchange(ref _faultPending, 0) != 0
            ? ValueTask.FromException<int>(new SocketException((int)error))
            : inner.ReceiveFromAsync(buffer, receivedAddress, cancellationToken);

    public void Dispose() => inner.Dispose();
}
