// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Threading.Tasks;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Local;
using Nethermind.Core;
using Nethermind.Network;
using Nethermind.Network.Config;

namespace Nethermind.Synchronization.Test.Modules;

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

    private class LocalClientChannel(string networkGroup, IPEndPoint localIPEndpoint): LocalChannel
    {
        public override Task ConnectAsync(EndPoint remoteAddress, EndPoint localAddress)
        {
            if (localAddress is IPEndPoint ipAddress)
            {
                localAddress = new EqualLocalAddress(networkGroup + ipAddress.Port.ToString(), ipAddress);
            }
            if (remoteAddress is IPEndPoint ipAddress2)
            {
                remoteAddress = new EqualLocalAddress(networkGroup + ipAddress2.Port.ToString(), ipAddress2);
            }
            return base.ConnectAsync(remoteAddress, localAddress);
        }

        public override Task ConnectAsync(EndPoint localAddress)
        {
            if (localAddress is IPEndPoint ipAddress)
            {
                localAddress = new EqualLocalAddress(networkGroup + ipAddress.Port.ToString(), ipAddress);
            }
            return base.ConnectAsync(localAddress);
        }

        protected override void DoBind(EndPoint endpoint)
        {
            if (endpoint is LocalAddress localAddress and not EqualLocalAddress)
            {
                endpoint = new EqualLocalAddress(localAddress.Id, localIPEndpoint);
            }
            base.DoBind(endpoint);
        }
    }

    private class LocalServerChannelInterceptor(string networkGroup): LocalServerChannel
    {
        protected override void DoBind(EndPoint localAddress)
        {
            if (localAddress is IPEndPoint ipAddress)
            {
                localAddress = new EqualLocalAddress(networkGroup + ipAddress.Port.ToString(), ipAddress);
            }
            base.DoBind(localAddress);
        }
    }

    private class EqualLocalAddress(string id, IPEndPoint ipEndPoint) : LocalAddress(id), IConvertible<IPEndPoint>
    {
        public IPEndPoint IpEndpoint => ipEndPoint;

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
