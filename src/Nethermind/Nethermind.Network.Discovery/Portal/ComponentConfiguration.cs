// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Extensions.DependencyInjection;

namespace Nethermind.Network.Discovery.Portal;

public class ComponentConfiguration
{
    public static void Configure(IServiceCollection services)
    {
        services.AddSingleton<IPortalContentNetworkFactory, PortalContentNetworkFactory>();
        services.AddSingleton<ITalkReqTransport, TalkReqTransport>();
        services.AddSingleton<IUtpManager, TalkReqUtpManager>();
    }
}
