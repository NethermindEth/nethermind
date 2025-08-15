using Lantern.Discv5.Rlp;

namespace Lantern.Discv5.WireProtocol.Messages.Responses;

public class RegConfirmationMessage : Message
{
    public RegConfirmationMessage(byte[] topic) : base(MessageType.RegConfirmation)
    {
        Topic = topic;
    }

    public RegConfirmationMessage(byte[] requestId, byte[] topic) : base(MessageType.RegConfirmation, requestId)
    {
        Topic = topic;
    }

    public byte[] Topic { get; }

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