// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Local;
using Nethermind.Network;

namespace Nethermind.Synchronization.Test.Modules;

internal class LocalChannelFactory(string networkGroup) : IChannelFactory
{
    public IServerChannel CreateServer()
    {
        return new LocalServerChannelInterceptor(networkGroup);
    }

    public IChannel CreateClient()
    {
        return new LocalClientChannel(networkGroup);
    }

    private class LocalClientChannel(string networkGroup): LocalChannel
    {
        protected override void DoBind(EndPoint localAddress)
        {
            if (localAddress is IPEndPoint ipAddress)
            {
                localAddress = new LocalAddress(networkGroup + ipAddress.Port.ToString());
            }
            base.DoBind(localAddress);
        }
    }

    private class LocalServerChannelInterceptor(string networkGroup): LocalServerChannel
    {
        protected override void DoBind(EndPoint localAddress)
        {
            if (localAddress is IPEndPoint ipAddress)
            {
                localAddress = new LocalAddress(networkGroup + ipAddress.Port.ToString());
            }
            base.DoBind(localAddress);
        }
    }
}
