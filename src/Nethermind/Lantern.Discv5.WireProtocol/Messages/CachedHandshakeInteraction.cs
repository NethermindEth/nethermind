using System.Diagnostics;

namespace Lantern.Discv5.WireProtocol.Messages;

public class CachedHandshakeInteraction(byte[] nodeId)
{
    public byte[] NodeId { get; } = nodeId;

    public Stopwatch ElapsedTime { get; } = Stopwatch.StartNew();
}