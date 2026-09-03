// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Threading.Tasks;
using DotNetty.Transport.Bootstrapping;
using DotNetty.Transport.Channels;

namespace Nethermind.Network;

public interface IConnectionsPool
{
    public Task<IChannel> BindAsync(
        Func<Bootstrap> bootstrapFactory,
        Func<IPAddress, IChannel> channelFactory,
        int port,
        IPAddress preferredAddress,
        IPAddress fallbackAddress);
    public Task StopAsync();
}
