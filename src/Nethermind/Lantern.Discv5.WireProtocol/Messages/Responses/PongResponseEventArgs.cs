namespace Lantern.Discv5.WireProtocol.Messages.Responses;

public class PongResponseEventArgs(IEnumerable<byte> requestId, PongMessage pongMessage) : EventArgs
{
    public IEnumerable<byte> RequestId { get; } = requestId;

    public PongMessage PongMessage { get; } = pongMessage;
}