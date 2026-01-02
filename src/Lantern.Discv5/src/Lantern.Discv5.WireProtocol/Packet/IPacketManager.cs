using System.Net.Sockets;
using Lantern.Discv5.Enr;
using Lantern.Discv5.WireProtocol.Messages;

namespace Lantern.Discv5.WireProtocol.Packet;

public interface IPacketManager
{
    Task<byte[]?> SendPacket(IEnr dest, MessageType messageType, bool isLookup, params object[] args);

    Task HandleReceivedPacket(UdpReceiveResult returnedResult);
}
