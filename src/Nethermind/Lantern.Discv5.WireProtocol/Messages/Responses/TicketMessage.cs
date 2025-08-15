using Lantern.Discv5.Rlp;

namespace Lantern.Discv5.WireProtocol.Messages.Responses;

public class TicketMessage : Message
{
    public TicketMessage(byte[] ticket, int waitTime) : base(MessageType.Ticket)
    {
        Ticket = ticket;
        WaitTime = waitTime;
    }

    public TicketMessage(byte[] requestId, byte[] ticket, int waitTime) : base(MessageType.Ticket, requestId)
    {
        Ticket = ticket;
        WaitTime = waitTime;
    }

    public byte[] Ticket { get; }

    public int WaitTime { get; }

    public override byte[] EncodeMessage()
    {
        var messageId = new[] { (byte)MessageType };
        var encodedRequestId = RlpEncoder.EncodeBytes(RequestId);
        var encodedTicket = RlpEncoder.EncodeBytes(Ticket);
        var encodedWaitTime = RlpEncoder.EncodeInteger(WaitTime);
        var encodedMessage =
            RlpEncoder.EncodeCollectionOfBytes(ByteArrayUtils.Concatenate(encodedRequestId, encodedTicket,
                encodedWaitTime));
        return ByteArrayUtils.Concatenate(messageId, encodedMessage);
    }
}