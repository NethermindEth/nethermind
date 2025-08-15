namespace Lantern.Discv5.WireProtocol.Messages;

public interface IMessageRequester
{
    byte[]? ConstructPingMessage(byte[] destNodeId);

    byte[]? ConstructCachedPingMessage(byte[] destNodeId);

    byte[]? ConstructFindNodeMessage(byte[] destNodeId, bool isLookupPacket, int[] distances);

    byte[]? ConstructCachedFindNodeMessage(byte[] destNodeId, bool isLookupPacket, int[] distances);

    byte[]? ConstructTalkReqMessage(byte[] destNodeId, byte[] protocol, byte[] request);

    byte[]? ConstructCachedTalkReqMessage(byte[] destNodeId, byte[] protocol, byte[] request);

    byte[]? ConstructTalkRespMessage(byte[] destNodeId, byte[] response);

    byte[]? ConstructCachedTalkRespMessage(byte[] destNodeId, byte[] response);
}
