// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Local;
using Nethermind.Network;
using Nethermind.Network.Config;
using NonBlocking;

namespace Nethermind.Core.Test.Modules;

/// <summary>
/// Create dotnetty's LocalChannel. This is used in test to not actually create TCP client or server.
/// Internally the LocalChannel uses string as address instead of IP address.
/// To separate between different network group so that different test that use the same address does not conflict
/// with each other, a networkGroup parameter need to be specified.
/// </summary>
/// <param name="networkGroup">Something unique for each test. Unless they need to connect to each other.</param>
/// <param name="networkConfig">Network config for LocalEndpoint.</param>
public class LocalChannelFactory(string networkGroup, INetworkConfig networkConfig) : IChannelFactory
{
    private IPEndPoint LocalEndpoint = new(IPAddress.Parse(networkConfig.LocalIp ?? "127.0.0.1"), networkConfig.P2PPort);

    public IServerChannel CreateServer() => new LocalServerChannelInterceptor(networkGroup);

    public IChannel CreateClient() => new LocalClientChannel(networkGroup, LocalEndpoint);

    public IDatagramSocket CreateDatagramSocket() => new LocalDatagramSocket(networkGroup);

    private class LocalClientChannel(string networkGroup, IPEndPoint localIPEndpoint) : LocalChannel
    {
        public override Task ConnectAsync(EndPoint remoteAddress, EndPoint localAddress)
        {
            if (localAddress is IPEndPoint ipAddress)
            {
                localAddress = new NethermindLocalAddress(networkGroup + ipAddress.Port.ToString(), ipAddress);
            }
            if (remoteAddress is IPEndPoint ipAddress2)
            {
                remoteAddress = new NethermindLocalAddress(networkGroup + ipAddress2.Port.ToString(), ipAddress2);
            }
            return base.ConnectAsync(remoteAddress, localAddress);
        }

        public override Task ConnectAsync(EndPoint localAddress)
        {
            if (localAddress is IPEndPoint ipAddress)
            {
                localAddress = new NethermindLocalAddress(networkGroup + ipAddress.Port.ToString(), ipAddress);
            }
            return base.ConnectAsync(localAddress);
        }

        protected override void DoBind(EndPoint endpoint)
        {
            if (endpoint is LocalAddress localAddress and not NethermindLocalAddress)
            {
                endpoint = new NethermindLocalAddress(localAddress.Id, localIPEndpoint);
            }
            base.DoBind(endpoint);
        }
    }

    private class LocalServerChannelInterceptor(string networkGroup) : LocalServerChannel
    {
        protected override void DoBind(EndPoint localAddress)
        {
            if (localAddress is IPEndPoint ipAddress)
            {
                localAddress = new NethermindLocalAddress(networkGroup + ipAddress.Port.ToString(), ipAddress);
            }
            base.DoBind(localAddress);
        }
    }

    // Needed because the default local address did not compare the ID and because it needs to be convertible to
    // IPEndpoint
    private class NethermindLocalAddress(string id, IPEndPoint ipEndPoint) : LocalAddress(id), IIPEndpointSource
    {

        public IPEndPoint IPEndpoint => ipEndPoint;

        // Ah great. Equal is not overridden so it never match unless the address instance is exactly the same.
        public override bool Equals(object? obj)
        {
            if (obj is LocalAddress other) return Id == other.Id;
            return false;
        }

        public override int GetHashCode() => Id.GetHashCode();
    }

    /// <summary>
    /// In-memory datagram socket that delivers to the socket of the same network group bound to the destination endpoint.
    /// </summary>
    private sealed class LocalDatagramSocket(string networkGroup) : IDatagramSocket
    {
        private static readonly ConcurrentDictionary<(string, IPEndPoint), LocalDatagramSocket> SocketRegistry = new();

        private readonly Channel<(byte[] Datagram, IPEndPoint Sender)> _inbound = Channel.CreateUnbounded<(byte[], IPEndPoint)>();

        public IPEndPoint? LocalEndpoint { get; private set; }

        public void Bind(IPEndPoint localEndpoint)
        {
            if (!SocketRegistry.TryAdd((networkGroup, localEndpoint), this))
            {
                throw new SocketException((int)SocketError.AddressAlreadyInUse);
            }

            LocalEndpoint = localEndpoint;
        }

        public ValueTask SendToAsync(ReadOnlyMemory<byte> datagram, IPEndPoint remoteEndpoint, CancellationToken cancellationToken = default)
        {
            if (LocalEndpoint is not null && SocketRegistry.TryGetValue((networkGroup, remoteEndpoint), out LocalDatagramSocket? recipient))
            {
                recipient._inbound.Writer.TryWrite((datagram.ToArray(), LocalEndpoint));
            }

            return ValueTask.CompletedTask;
        }

        public async ValueTask<int> ReceiveFromAsync(Memory<byte> buffer, SocketAddress receivedAddress, CancellationToken cancellationToken = default)
        {
            (byte[] datagram, IPEndPoint sender) = await ReadAsync(cancellationToken);
            if (datagram.Length > buffer.Length)
            {
                throw new SocketException((int)SocketError.MessageSize);
            }

            SocketAddress senderAddress = ToReceiverFamily(sender).Serialize();
            receivedAddress.Size = senderAddress.Size;
            senderAddress.Buffer.Span[..senderAddress.Size].CopyTo(receivedAddress.Buffer.Span);
            datagram.CopyTo(buffer);
            return datagram.Length;
        }

        /// <summary>
        /// Reports <paramref name="sender"/> as a socket bound to <see cref="LocalEndpoint"/> would see it.
        /// </summary>
        /// <remarks>
        /// An IPv6 socket is treated as dual-stack, so it sees IPv4 senders as IPv4-mapped addresses. An IPv4 socket
        /// cannot receive from an IPv6 sender, so such a test topology is rejected.
        /// </remarks>
        private IPEndPoint ToReceiverFamily(IPEndPoint sender) =>
            (LocalEndpoint?.AddressFamily, sender.AddressFamily) switch
            {
                (AddressFamily.InterNetworkV6, AddressFamily.InterNetwork) => new IPEndPoint(sender.Address.MapToIPv6(), sender.Port),
                (AddressFamily.InterNetwork, AddressFamily.InterNetworkV6) => throw new InvalidOperationException(
                    $"An IPv4 socket bound to {LocalEndpoint} cannot receive from IPv6 sender {sender}."),
                _ => sender
            };

        private async ValueTask<(byte[], IPEndPoint)> ReadAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await _inbound.Reader.ReadAsync(cancellationToken);
            }
            catch (ChannelClosedException)
            {
                throw new ObjectDisposedException(nameof(LocalDatagramSocket));
            }
        }

        public void Dispose()
        {
            if (LocalEndpoint is not null)
            {
                SocketRegistry.TryRemove((networkGroup, LocalEndpoint), out _);
            }

            _inbound.Writer.TryComplete();
        }
    }
}
