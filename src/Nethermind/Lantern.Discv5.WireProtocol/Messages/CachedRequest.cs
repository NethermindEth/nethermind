using System.Diagnostics;

namespace Lantern.Discv5.WireProtocol.Messages;

public class CachedRequest(byte[] nodeId, Message message)
{
    public byte[] NodeId { get; } = nodeId;

    public Message Message { get; } = message;

    public bool IsLookupRequest { get; set; }

    public Stopwatch ElapsedTime { get; } = Stopwatch.StartNew();
}