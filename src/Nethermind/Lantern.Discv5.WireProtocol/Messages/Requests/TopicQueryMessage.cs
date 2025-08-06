using Lantern.Discv5.Rlp;

namespace Lantern.Discv5.WireProtocol.Messages.Requests;

public class TopicQueryMessage(byte[] topic) : Message(MessageType.TopicQuery)
{
    public byte[] Topic { get; } = topic;

    public override byte[] EncodeMessage()
    {
        var messageId = new[] { (byte)MessageType };
        var encodedRequestId = RlpEncoder.EncodeBytes(RequestId);
        var encodedTopic = RlpEncoder.EncodeBytes(Topic);
        var encodedMessage =
            RlpEncoder.EncodeCollectionOfBytes(ByteArrayUtils.Concatenate(encodedRequestId, encodedTopic));
        return ByteArrayUtils.Concatenate(messageId, encodedMessage);
    }
}