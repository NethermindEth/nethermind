using Lantern.Discv5.Rlp;

namespace Lantern.Discv5.WireProtocol.Messages.Requests;

public class RegTopicMessage(byte[] topic, Enr.Enr enr, byte[] ticket) : Message(MessageType.RegTopic)
{
    public byte[] Topic { get; } = topic;

    public Enr.Enr Enr { get; } = enr;

    public byte[] Ticket { get; } = ticket;

    public override byte[] EncodeMessage()
    {
        var messageId = new[] { (byte)MessageType };
        var encodedRequestId = RlpEncoder.EncodeBytes(RequestId);
        var encodedTopic = RlpEncoder.EncodeBytes(Topic);
        var encodedEnr = Enr.EncodeRecord();
        var encodedTicket = RlpEncoder.EncodeBytes(Ticket);
        var encodedMessage =
            RlpEncoder.EncodeCollectionOfBytes(ByteArrayUtils.Concatenate(encodedRequestId, encodedTopic,
                encodedEnr, encodedTicket));
        return ByteArrayUtils.Concatenate(messageId, encodedMessage);
    }
}