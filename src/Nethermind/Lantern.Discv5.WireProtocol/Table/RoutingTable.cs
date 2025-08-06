using Lantern.Discv5.Enr;
using Lantern.Discv5.WireProtocol.Identity;
using Microsoft.Extensions.Logging;

namespace Lantern.Discv5.WireProtocol.Table;

public class RoutingTable : IRoutingTable
{
    private readonly IIdentityManager _identityManager;
    private readonly IEnrFactory _enrFactory;
    private readonly ILogger<RoutingTable> _logger;
    private readonly List<KBucket> _buckets;

    public RoutingTable(IIdentityManager identityManager, IEnrFactory enrFactory, ILoggerFactory loggerFactory, TableOptions options)
    {
        _identityManager = identityManager;
        _enrFactory = enrFactory;
        _logger = loggerFactory.CreateLogger<RoutingTable>();
        _buckets = Enumerable
            .Range(0, TableConstants.NumberOfBuckets)
            .Select(_ => new KBucket(loggerFactory, options.ReplacementCacheSize))
            .ToList();
        TableOptions = options;
        ConfigureBucketEventHandlers();
    }

    public event Action<NodeTableEntry> NodeAdded = delegate { };

    public event Action<NodeTableEntry> NodeRemoved = delegate { };

    public event Action<NodeTableEntry> NodeAddedToCache = delegate { };

    public event Action<NodeTableEntry> NodeRemovedFromCache = delegate { };

    public TableOptions TableOptions { get; }

    public int GetNodesCount()
    {
        lock (_buckets)
        {
            return _buckets.Sum(bucket => bucket.Nodes.Count() + bucket.ReplacementCache.Count());
        }
    }

    public int GetActiveNodesCount()
    {
        lock (_buckets)
        {
            return _buckets.Sum(bucket => bucket.Nodes.Count(node => node.Status == NodeStatus.Live) + bucket.ReplacementCache.Count(node => node.Status == NodeStatus.Live));
        }
    }

    public IEnumerable<IEnr> GetActiveNodes()
    {
        List<IEnr> activeNodes = new List<IEnr>();
        lock (_buckets)
        {
            foreach (var bucket in _buckets)
            {
                foreach (var node in bucket.Nodes.Concat(bucket.ReplacementCache))
                {
                    if (node.Status == NodeStatus.Live)
                    {
                        activeNodes.Add(node.Record);
                    }
                }
            }
        }
        foreach (var node in activeNodes)
        {
            yield return node;
        }
    }

    public IEnumerable<IEnr> GetAllNodes()
    {
        lock (_buckets)
        {
            var allNodesInBuckets = _buckets.Select(bucket => bucket.Nodes);
            var allNodesInReplacementCache = _buckets.Select(bucket => bucket.ReplacementCache);
            return allNodesInBuckets.Concat(allNodesInReplacementCache).SelectMany(x => x).Select(node => node.Record).ToArray();
        }
    }

    public List<NodeTableEntry> GetClosestNodes(byte[] targetId)
    {
        lock (_buckets)
        {
            return _buckets
                .SelectMany(bucket => bucket.Nodes)
                .Where(IsNodeConsideredLive)
                .OrderBy(nodeEntry => TableUtility.Log2Distance(nodeEntry.Id, targetId))
                .ToList();
        }
    }

    public void UpdateFromEnr(IEnr enr)
    {
        var nodeId = _identityManager.Verifier.GetNodeIdFromRecord(enr);
        var nodeEntry = GetNodeEntryForNodeId(nodeId);

        if (nodeEntry != null)
        {
            nodeEntry.Record = enr;
        }
        else
        {
            nodeEntry = new NodeTableEntry(enr, _identityManager.Verifier);
        }

        var bucketIndex = GetBucketIndex(nodeEntry.Id);

        _buckets[bucketIndex].Update(nodeEntry);
        _logger.LogDebug("Updated table with node entry {NodeId}", Convert.ToHexString(nodeId));
    }

    public NodeTableEntry? GetLeastRecentlySeenNode()
    {
        lock (_buckets)
        {
            var leastRecentlyRefreshedBucket = _buckets
                .Where(bucket => bucket.Nodes.Any())
                .MinBy(bucket => bucket.Nodes.Min(node => node.LastSeen));

            return leastRecentlyRefreshedBucket?.GetLeastRecentlySeenNode();
        }
    }

    public void MarkNodeAsResponded(byte[] nodeId)
    {
        var bucketIndex = GetBucketIndex(nodeId);
        var bucket = _buckets[bucketIndex];
        var nodeEntry = bucket.GetNodeById(nodeId);

        if (nodeEntry == null)
            return;

        nodeEntry.HasRespondedEver = true;
    }

    public void MarkNodeAsPending(byte[] nodeId)
    {
        var bucketIndex = GetBucketIndex(nodeId);
        var bucket = _buckets[bucketIndex];
        var nodeEntry = bucket.GetNodeById(nodeId);

        if (nodeEntry == null)
            return;

        nodeEntry.Status = NodeStatus.Pending;
    }

    public void MarkNodeAsLive(byte[] nodeId)
    {
        var bucketIndex = GetBucketIndex(nodeId);
        var bucket = _buckets[bucketIndex];
        var nodeEntry = bucket.GetNodeById(nodeId);

        if (nodeEntry == null)
        {
            nodeEntry = bucket.GetNodeFromReplacementCache(nodeId);
            if (nodeEntry == null)
                return;
        }

        if (nodeEntry.Status == NodeStatus.Live)
            return;

        nodeEntry.Status = NodeStatus.Live;
        nodeEntry.FailureCounter = 0;
    }

    public void MarkNodeAsDead(byte[] nodeId)
    {
        var bucketIndex = GetBucketIndex(nodeId);
        var bucket = _buckets[bucketIndex];
        var nodeEntry = bucket.GetNodeById(nodeId);

        if (nodeEntry == null)
            return;

        nodeEntry.Status = NodeStatus.Dead;
    }

    public void IncreaseFailureCounter(byte[] nodeId)
    {
        var bucketIndex = GetBucketIndex(nodeId);
        var bucket = _buckets[bucketIndex];
        var nodeEntry = bucket.GetNodeById(nodeId);

        if (nodeEntry != null)
        {
            nodeEntry.FailureCounter++;
        }
    }

    public NodeTableEntry? GetNodeEntryForNodeId(byte[] nodeId)
    {
        var nodeEntry = GetEntryFromTable(nodeId);

        if (nodeEntry != null)
            return nodeEntry;

        var bootstrapEnrs = TableOptions.BootstrapEnrs
            .Select(enr => _enrFactory.CreateFromString(enr, _identityManager.Verifier))
            .ToArray();

        foreach (var bootstrapEnr in bootstrapEnrs)
        {
            var bootstrapNodeId = _identityManager.Verifier.GetNodeIdFromRecord(bootstrapEnr);

            if (!nodeId.SequenceEqual(bootstrapNodeId))
                continue;

            var bootstrapEntry = new NodeTableEntry(bootstrapEnr, _identityManager.Verifier);
            return bootstrapEntry;
        }

        if (nodeEntry == null)
        {
            var bucketIndex = GetBucketIndex(nodeId);
            var bucket = _buckets[bucketIndex];
            nodeEntry = bucket.GetNodeFromReplacementCache(nodeId);
        }

        return nodeEntry;
    }

    public List<IEnr>? GetEnrRecordsAtDistances(IEnumerable<int> distances)
    {
        var enrRecords = new List<IEnr>();

        foreach (var distance in distances)
        {
            if (distance == 0)
            {
                enrRecords.Add(_identityManager.Record);
            }
            else
            {
                var nodesAtDistance = GetNodesAtDistance(distance);

                if (nodesAtDistance == null)
                    continue;

                enrRecords.AddRange(nodesAtDistance.Select(nodeAtDistance => nodeAtDistance.Record));
            }
        }

        return enrRecords;
    }

    private NodeTableEntry? GetEntryFromTable(byte[] nodeId)
    {
        var bucketIndex = GetBucketIndex(nodeId);
        var bucket = _buckets[bucketIndex];
        return bucket.GetNodeById(nodeId);
    }

    private bool IsNodeConsideredLive(NodeTableEntry nodeEntry)
    {
        return nodeEntry.Status == NodeStatus.Live && nodeEntry.FailureCounter < TableOptions.MaxAllowedFailures;
    }

    private List<NodeTableEntry>? GetNodesAtDistance(int distance)
    {
        if (distance is < 0 or > TableConstants.NumberOfBuckets)
        {
            _logger.LogError("Distance should be between 0 and 256");
            return null;
        }

        lock (_buckets)
        {
            var nodesAtDistance = new List<NodeTableEntry>();

            foreach (var bucket in _buckets)
            {
                foreach (var node in bucket.Nodes)
                {
                    if (!node.HasRespondedEver)
                    {
                        continue;
                    }

                    var currentDistance = TableUtility.Log2Distance(_identityManager.Record.NodeId, node.Id);

                    if (currentDistance == distance)
                    {
                        nodesAtDistance.Add(node);
                    }
                }
            }

            return nodesAtDistance;
        }
    }

    private void ConfigureBucketEventHandlers()
    {
        foreach (var bucket in _buckets)
        {
            bucket.NodeAdded += node => NodeAdded.Invoke(node);
            bucket.NodeRemoved += node => NodeRemoved.Invoke(node);
            bucket.NodeAddedToCache += node => NodeAddedToCache.Invoke(node);
            bucket.NodeRemovedFromCache += node => NodeRemovedFromCache.Invoke(node);
        }
    }

    private int GetBucketIndex(byte[] nodeId)
    {
        var distance = TableUtility.Log2Distance(_identityManager.Record.NodeId, nodeId);
        return distance == 256 ? 255 : distance;
    }
}