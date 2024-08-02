// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.WireProtocol.Packet.Handlers;
using Lantern.Discv5.WireProtocol.Packet.Types;
using Microsoft.Extensions.DependencyInjection;

namespace Nethermind.Network.Discovery.Portal.LanternAdapter;

public class CustomPacketHandlerFactory(IServiceProvider serviceProvider) : IPacketHandlerFactory
{
    private readonly Dictionary<PacketType, Type> _handlerTypes = new()
    {
        { PacketType.Ordinary, typeof(HacklyLanternPacketHandler) },
        { PacketType.WhoAreYou, typeof(WhoAreYouPacketHandler) },
        { PacketType.Handshake, typeof(HandshakePacketHandler) },
    };

    public IPacketHandler GetPacketHandler(PacketType packetType)
    {
        if (_handlerTypes.TryGetValue(packetType, out var handlerType))
        {
            return (IPacketHandler)ActivatorUtilities.CreateInstance(serviceProvider, handlerType);
        }

        throw new InvalidOperationException($"No handler found for packet type {packetType}");
    }
}
