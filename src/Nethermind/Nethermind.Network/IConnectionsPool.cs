// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using DotNetty.Transport.Bootstrapping;
using DotNetty.Transport.Channels;

namespace Nethermind.Network;

public interface IConnectionsPool
{
    public Task<IChannel> BindAsync(Bootstrap bootstrap, int port);
    public Task StopAsync();
}
