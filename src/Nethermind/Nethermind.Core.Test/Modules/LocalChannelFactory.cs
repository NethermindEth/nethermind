// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Threading.Tasks;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Local;
using Nethermind.Network;
using Nethermind.Network.Config;

namespace Nethermind.Core.Test.Modules;

/// <summary>
/// Create dotnetty's LocalChannel. This is used in test to not actually create TCP client or server.
/// Internally the LocalChannel uses string as address instead of IP address.
/// To separate between different network group so that different test that use the same address does not conflict
/// with each other, a networkGroup parameter need to be specified.
/// </summary>
/// <param name="networkGroup">Something unique for each test. Unless they need to connect to each other.</param>
/// <param name="networkConfig">Network config for LocalEndpoint.</param>
internal class LocalChannelFactory(string networkGroup, INetworkConfig networkConfig) : IChannelFactory
{
    private IPEndPoint LocalEndpoint = new IPEndPoint(IPAddress.Parse(networkConfig.LocalIp ?? "127.0.0.1"), networkConfig.P2PPort);

    public IServerChannel CreateServer()
    {
        return new LocalServerChannelInterceptor(networkGroup);
    }

    public IChannel CreateClient()
    {
        return new LocalClientChannel(networkGroup, LocalEndpoint);
    }

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

    // Needed because the default local address did not compare the id and because it need to be convertiable to
    // IPEndpoint
    private class NethermindLocalAddress(string id, IPEndPoint ipEndPoint) : LocalAddress(id), IConvertible<IPEndPoint>
    {
        private IPEndPoint IpEndpoint => ipEndPoint;

        public IPEndPoint Convert()
        {
            return IpEndpoint;
        }

        // Ah great. Equal is not overridden so it never match unless the address instance is exactly the same.
        public override bool Equals(object? obj)
        {
            if (obj is LocalAddress other) return Id == other.Id;
            return false;
        }

        public override int GetHashCode()
        {
            return Id.GetHashCode();
        }
    }
}
