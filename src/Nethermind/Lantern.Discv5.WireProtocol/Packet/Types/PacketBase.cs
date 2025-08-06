namespace Lantern.Discv5.WireProtocol.Packet.Types;

public abstract class PacketBase(byte[] authData)
{
    public byte[] AuthData { get; } = authData;
}