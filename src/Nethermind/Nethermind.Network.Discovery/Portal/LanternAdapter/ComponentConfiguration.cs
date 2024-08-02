// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.WireProtocol.Packet.Handlers;
using Lantern.Discv5.WireProtocol.Session;
using Lantern.Discv5.WireProtocol.Table;
using Microsoft.Extensions.DependencyInjection;

namespace Nethermind.Network.Discovery.Portal.LanternAdapter;

public static class ComponentConfiguration
{
    public static void Configure(IServiceCollection services)
    {
        services.AddSingleton<IPacketHandlerFactory, CustomPacketHandlerFactory>();
        services.AddSingleton<IRoutingTable, TransientRoutingTable>();
        services.AddSingleton<ISessionManager, SessionManagerNormalizer>();
        services.AddSingleton<ITalkReqTransport, LanternTalkReqTransport>();
        services.AddSingleton<IEnrProvider, LanternIEnrProvider>();
    }
}
