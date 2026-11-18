// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Features.AttributeFilters;
using Nethermind.Config;
using Nethermind.Logging;
using Nethermind.Network;
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
    IIPResolver ipResolver,
    ILogManager logManager,
    NetworkListenerState listenerState,
    PeerAddressPolicy addressPolicy)
    : DiscoveryApp(
        addressPolicy,
        rootScope,
        enode,
        networkConfig,
        discoveryConfig,
        ipResolver,
        processExitSource,
        logManager,
        listenerState,
        static builder =>
        {
            builder.RegisterType<XdcDiscoveryHandler>()
                .As<DiscoveryHandler>()
                .WithAttributeFiltering();

            // XDC does not implement the ENR request/response messages, so remote ENR refresh is disabled.
            RegisterKademliaAdapter<XdcKademliaAdapter>(builder);
        })
{

}
