namespace Nevermind.Network
{
    public interface IFramingService
    {
        byte[] Package(int protocolType, int packetType, byte[] data);
        byte[] Package(int protocolType, int packetType, int? contextId, byte[] data);
    }
}