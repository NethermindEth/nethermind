// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Features.AttributeFilters;
using Nethermind.Config;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery;

namespace Nethermind.Xdc.Discovery;

public class XdcDiscoveryApp(
    ILifetimeScope rootScope,
    [KeyFilter(IProtectedPrivateKey.NodeKey)] IProtectedPrivateKey nodeKey,
    IProcessExitSource processExitSource,
    INetworkConfig networkConfig,
    IDiscoveryConfig discoveryConfig,
    IIPResolver ipResolver,
    ILogManager logManager)
    : DiscoveryApp(
        rootScope,
        nodeKey,
        networkConfig,
        discoveryConfig,
        ipResolver,
        processExitSource,
        logManager,
        static builder => builder
            .RegisterType<XdcNettyDiscoveryHandler>()
            .As<NettyDiscoveryHandler>()
            .WithAttributeFiltering())
{
}
