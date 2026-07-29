// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using DotNetty.Transport.Channels;
using Nethermind.Config;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery;
using Nethermind.Network.Discovery.Discv4;

namespace Nethermind.Bootnode;

internal sealed class BootnodeDiscoveryApp(
    ILifetimeScope rootScope,
    IEnode enode,
    INetworkConfig networkConfig,
    IDiscoveryConfig discoveryConfig,
    IIPResolver ipResolver,
    IProcessExitSource processExitSource,
    ILogManager logManager,
    Action<ContainerBuilder>? configureDiscv4Services = null)
    : DiscoveryApp(rootScope, enode, networkConfig, discoveryConfig, ipResolver, processExitSource, logManager, configureDiscv4Services)
{
    private readonly Nethermind.Logging.ILogger _logger = logManager.GetClassLogger<BootnodeDiscoveryApp>();

    public override void InitializeChannel(IChannel channel)
    {
        try
        {
            base.InitializeChannel(channel);
        }
        catch (Exception exception)
        {
            if (_logger.IsError) _logger.Error("Failed to initialize bootnode discv4 UDP channel.", exception);
            throw;
        }
    }
}
