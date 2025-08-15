using System.Collections.Concurrent;
using Lantern.Discv5.WireProtocol.Utility;

namespace Lantern.Discv5.WireProtocol.Table;

public class PathBucket(byte[] targetNodeId, int index) : IDisposable
{
    public int Index { get; } = index;

    public byte[] TargetNodeId { get; } = targetNodeId;

    public ConcurrentBag<byte[]> PendingQueries { get; } = new();

    public ConcurrentDictionary<byte[], Timer> PendingTimers { get; } = new(ByteArrayEqualityComparer.Instance);

    public ConcurrentBag<NodeTableEntry> DiscoveredNodes { get; } = new();

    public ConcurrentDictionary<byte[], List<NodeTableEntry>> Responses { get; } = new(ByteArrayEqualityComparer.Instance);

    public ConcurrentDictionary<byte[], int> ExpectedResponses { get; } = new();

    public TaskCompletionSource<bool> Completion { get; } = new();

    public void SetComplete()
    {
        if (Completion.Task.IsCompleted)
            return;

        Completion.SetResult(true);
    }

    public void DisposeTimer(byte[] nodeId)
    {
        if (PendingTimers.TryRemove(nodeId, out var timer))
        {
            timer.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var timer in PendingTimers.Values)
        {
            timer.Dispose();
        }

        PendingTimers.Clear();
        PendingQueries.Clear();
        DiscoveredNodes.Clear();
        Responses.Clear();
        ExpectedResponses.Clear();
    }
}