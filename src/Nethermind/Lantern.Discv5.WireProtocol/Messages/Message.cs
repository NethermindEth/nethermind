namespace Lantern.Discv5.WireProtocol.Messages;

public abstract class Message(MessageType messageType, byte[]? requestId = null)
{
    public MessageType MessageType { get; } = messageType;

    public byte[] RequestId { get; set; } = requestId ?? MessageUtility.GenerateRequestId(MessageConstants.RequestIdLength);

    public abstract byte[] EncodeMessage();
}