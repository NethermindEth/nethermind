// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Features.AttributeFilters;
using Nethermind.Config;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery;
using Nethermind.Network.Discovery.Discv4;

namespace Nethermind.Xdc.Discovery;

public class XdcDiscoveryApp(
    ILifetimeScope rootScope,
    IEnode enode,
    IProcessExitSource processExitSource,
    INetworkConfig networkConfig,
    IDiscoveryConfig discoveryConfig,
    ILogManager logManager)
    : DiscoveryApp(
        rootScope,
        enode,
        networkConfig,
        discoveryConfig,
        processExitSource,
        logManager,
        static builder => builder
            .RegisterType<XdcNettyDiscoveryHandler>()
            .As<NettyDiscoveryHandler>()
            .WithAttributeFiltering())
{
}
