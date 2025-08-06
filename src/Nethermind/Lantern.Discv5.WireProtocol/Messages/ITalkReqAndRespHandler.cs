namespace Lantern.Discv5.WireProtocol.Messages;

public interface ITalkReqAndRespHandler
{
    byte[][]? HandleRequest(byte[] protocol, byte[] request);

    byte[]? HandleResponse(byte[] response);
}