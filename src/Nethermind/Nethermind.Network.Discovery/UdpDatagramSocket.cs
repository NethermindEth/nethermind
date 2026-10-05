// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Net.Sockets;

namespace Nethermind.Network.Discovery;

/// <summary>
/// <see cref="IDatagramSocket"/> over an operating system UDP socket.
/// </summary>
internal sealed class UdpDatagramSocket(Socket socket) : IDatagramSocket
{
    private readonly EndPoint _anyRemoteEndpoint = new IPEndPoint(
        socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);

    public IPEndPoint? LocalEndpoint { get; private set; }

    public void Bind(IPEndPoint localEndpoint)
    {
        socket.Bind(localEndpoint);
        LocalEndpoint = (IPEndPoint?)socket.LocalEndPoint;
    }

    public async ValueTask SendToAsync(ReadOnlyMemory<byte> datagram, IPEndPoint remoteEndpoint, CancellationToken cancellationToken = default)
        => await socket.SendToAsync(datagram, SocketFlags.None, remoteEndpoint, cancellationToken);

    public ValueTask<SocketReceiveFromResult> ReceiveFromAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => socket.ReceiveFromAsync(buffer, SocketFlags.None, _anyRemoteEndpoint, cancellationToken);

    public void Dispose() => socket.Dispose();
}
