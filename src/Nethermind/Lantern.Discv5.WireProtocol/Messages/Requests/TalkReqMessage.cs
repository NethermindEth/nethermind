using Lantern.Discv5.Rlp;

namespace Lantern.Discv5.WireProtocol.Messages.Requests;

public class TalkReqMessage(byte[] protocol, byte[] request) : Message(MessageType.TalkReq)
{
    public byte[] Protocol { get; } = protocol;

    public byte[] Request { get; } = request;

    public override byte[] EncodeMessage()
    {
        var messageId = new[] { (byte)MessageType };
        var encodedRequestId = RlpEncoder.EncodeBytes(RequestId);
        var encodedProtocol = RlpEncoder.EncodeBytes(Protocol);
        var encodedRequest = RlpEncoder.EncodeBytes(Request);
        var encodedMessage =
            RlpEncoder.EncodeCollectionOfBytes(ByteArrayUtils.Concatenate(encodedRequestId, encodedProtocol,
                encodedRequest));
        return ByteArrayUtils.Concatenate(messageId, encodedMessage);
    }
}