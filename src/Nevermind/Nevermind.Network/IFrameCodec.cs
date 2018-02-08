namespace Nevermind.Network
{
    // TODO:
    // current context
    //   message
    //   protocol (protocol layer)
    //   packet type (message recognizer layer) 
    //   data (message serializer layer)
    //   frames (framing layer)
    //   encryption (encryption layer) (encrypt and set macs)
    //   padding
    public interface IFrameCodec
    {
        byte[] Write(int protocolType, int packetType, byte[] data);
        byte[] Write(int protocolType, int packetType, int? contextId, byte[] data);
    }
}