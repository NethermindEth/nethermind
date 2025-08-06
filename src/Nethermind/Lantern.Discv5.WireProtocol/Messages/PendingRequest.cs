using System.Diagnostics;

namespace Lantern.Discv5.WireProtocol.Messages;

public class PendingRequest(byte[] nodeId, Message message)
{
    public byte[] NodeId { get; } = nodeId;

    public Message Message { get; } = message;

    public Stopwatch ElapsedTime { get; } = Stopwatch.StartNew();

    public bool IsFulfilled { get; set; }

    public bool IsLookupRequest { get; set; }

    public int ResponsesCount { get; set; }

    public int MaxResponses { get; set; }
}