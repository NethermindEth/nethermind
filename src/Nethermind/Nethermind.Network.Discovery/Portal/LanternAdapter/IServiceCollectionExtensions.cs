// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.WireProtocol.Packet.Handlers;
using Lantern.Discv5.WireProtocol.Session;
using Lantern.Discv5.WireProtocol.Table;
using Microsoft.Extensions.DependencyInjection;

namespace Nethermind.Network.Discovery.Portal.LanternAdapter;

public static class IServiceCollectionExtensions
{
    public static IServiceCollection ConfigureLanternPortalAdapter(this IServiceCollection services)
    {
        return services
            .AddSingleton<IPacketHandlerFactory, CustomPacketHandlerFactory>()
            .AddSingleton<IRoutingTable, TransientRoutingTable>()
            .AddSingleton<ISessionManager, SessionManagerNormalizer>()
            .AddSingleton<IRawTalkReqSender, LanternTalkReqSender>()
            .AddSingleton<IEnrProvider, LanternIEnrProvider>();
    }
}
