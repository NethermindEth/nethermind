using Lantern.Discv5.Rlp;

namespace Lantern.Discv5.WireProtocol.Messages.Responses;

public class TalkRespMessage : Message
{
    public TalkRespMessage(byte[] response) : base(MessageType.TalkResp)
    {
        Response = response;
    }

    public TalkRespMessage(byte[] requestId, byte[] response) : base(MessageType.TalkResp, requestId)
    {
        Response = response;
    }

    public byte[] Response { get; }

    public override byte[] EncodeMessage()
    {
        var messageId = new[] { (byte)MessageType };
        var encodedRequestId = RlpEncoder.EncodeBytes(RequestId);
        var encodedResponse = RlpEncoder.EncodeBytes(Response);
        var encodedMessage =
            RlpEncoder.EncodeCollectionOfBytes(ByteArrayUtils.Concatenate(encodedRequestId, encodedResponse));
        return ByteArrayUtils.Concatenate(messageId, encodedMessage);
    }
}