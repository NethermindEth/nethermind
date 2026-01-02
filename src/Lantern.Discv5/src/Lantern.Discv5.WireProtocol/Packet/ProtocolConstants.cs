using System.Text;

namespace Lantern.Discv5.WireProtocol.Packet;

public static class ProtocolConstants
{
    public static byte[] ProtocolIdBytes = Encoding.ASCII.GetBytes("discv5");

    public static readonly byte[] Version = { 0x00, 0x01 };
}
