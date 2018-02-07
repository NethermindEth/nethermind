namespace Nevermind.Network
{
    public interface IFrameCodec
    {
        byte[] Write(int protocolType, int packetType, byte[] data);
        byte[] Write(int protocolType, int packetType, int? contextId, byte[] data);
    }
}