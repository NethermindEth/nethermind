using Lantern.Discv5.WireProtocol.Packet.Types;
using Microsoft.Extensions.DependencyInjection;

namespace Lantern.Discv5.WireProtocol.Packet.Handlers;

public class PacketHandlerFactory(IServiceProvider serviceProvider) : IPacketHandlerFactory
{
    private readonly Dictionary<PacketType, Type> _handlerTypes = new()
    {
        { PacketType.Ordinary, typeof(OrdinaryPacketHandler) },
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