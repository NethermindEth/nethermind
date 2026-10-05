// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Network;

/// <summary>
/// A UDP socket that sends and receives whole datagrams.
/// </summary>
/// <remarks>
/// Disposing the socket completes pending and later receives with an <see cref="ObjectDisposedException"/> or a
/// <see cref="SocketException"/> with <see cref="SocketError.OperationAborted"/>.
/// </remarks>
public interface IDatagramSocket : IDisposable
{
    /// <summary>
    /// The endpoint the socket is bound to, or <c>null</c> before <see cref="Bind"/>.
    /// </summary>
    IPEndPoint? LocalEndpoint { get; }

    void Bind(IPEndPoint localEndpoint);

    ValueTask SendToAsync(ReadOnlyMemory<byte> datagram, IPEndPoint remoteEndpoint, CancellationToken cancellationToken = default);

    ValueTask<SocketReceiveFromResult> ReceiveFromAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);
}
