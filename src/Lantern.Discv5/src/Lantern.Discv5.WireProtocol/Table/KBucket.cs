using Microsoft.Extensions.Logging;

namespace Lantern.Discv5.WireProtocol.Table;

public class KBucket(ILoggerFactory loggerFactory, int replacementCacheSize)
{
    private readonly LinkedList<NodeTableEntry> _nodes = new();
    private readonly LinkedList<NodeTableEntry> _replacementCache = new();
    private readonly ILogger<KBucket> _logger = loggerFactory.CreateLogger<KBucket>();
    private readonly object _lock = new();

    public event Action<NodeTableEntry> NodeAdded = delegate { };

    public event Action<NodeTableEntry> NodeRemoved = delegate { };

    public event Action<NodeTableEntry> NodeAddedToCache = delegate { };

    public event Action<NodeTableEntry> NodeRemovedFromCache = delegate { };

    public IEnumerable<NodeTableEntry> Nodes
    {
        get
        {
            lock (_lock)
            {
                return _nodes.ToArray();
            }
        }
    }

    public IEnumerable<NodeTableEntry> ReplacementCache
    {
        get
        {
            lock (_lock)
            {
                return _replacementCache.ToArray();
            }
        }
    }

    public NodeTableEntry? GetNodeFromReplacementCache(byte[] nodeId)
    {
        lock (_lock)
        {
            return _replacementCache.FirstOrDefault(node => node.Id.SequenceEqual(nodeId));
        }
    }

    public NodeTableEntry? GetNodeById(byte[] nodeId)
    {
        lock (_lock)
        {
            var node = _nodes.FirstOrDefault(n => n.Id.SequenceEqual(nodeId));
            return node ?? _replacementCache.FirstOrDefault(n => n.Id.SequenceEqual(nodeId));
        }
    }

    public NodeTableEntry? GetLeastRecentlySeenNode()
    {
        lock (_lock)
        {
            return _nodes.First?.Value;
        }
    }

    public void Update(NodeTableEntry nodeEntry)
    {
        lock (_lock)
        {
            var existingNode = GetNodeById(nodeEntry.Id);

            if (existingNode != null)
            {
                UpdateExistingNode(nodeEntry, existingNode);
            }
            else if (_nodes.Count >= TableConstants.BucketSize)
            {
                CheckLeastRecentlySeenNode(nodeEntry);
            }
            else
            {
                AddNewNode(nodeEntry);
                NodeAdded.Invoke(nodeEntry);
            }
        }
    }

    public void ReplaceDeadNode(NodeTableEntry deadNodeEntry)
    {
        lock (_lock)
        {
            if (_replacementCache.Count == 0 || _replacementCache.First == null)
                return;

            _logger.LogInformation("Replacing dead node {NodeId} with node {ReplacementNodeId}", Convert.ToHexString(deadNodeEntry.Id), Convert.ToHexString(_replacementCache.First.Value.Id));

            var replacementNode = _replacementCache.First.Value;

            _replacementCache.RemoveFirst();
            NodeRemovedFromCache.Invoke(replacementNode);

            var deadNode = _nodes.FirstOrDefault(node => node.Id.SequenceEqual(deadNodeEntry.Id));

            if (deadNode != null)
            {
                _nodes.Remove(deadNode);
                NodeRemoved.Invoke(deadNode);
            }

            replacementNode.LastSeen = DateTime.UtcNow;
            _nodes.AddLast(replacementNode);
            NodeAdded.Invoke(replacementNode);
        }
    }

    public void AddToReplacementCache(NodeTableEntry nodeEntry)
    {
        if (_replacementCache.Count >= replacementCacheSize)
        {
            _logger.LogDebug("Replacement cache full. Removed first node from the bucket's replacement cache");

            var nodeToRemove = _replacementCache.First!.Value;

            _replacementCache.RemoveFirst();
            NodeRemovedFromCache.Invoke(nodeToRemove);
        }

        _replacementCache.AddLast(nodeEntry);
        NodeAddedToCache.Invoke(nodeEntry);
        _logger.LogDebug("Added node {NodeId} to replacement cache. There are {Count} nodes in the cache",
            Convert.ToHexString(nodeEntry.Id), _replacementCache.Count);
    }

    private void UpdateExistingNode(NodeTableEntry nodeEntry, NodeTableEntry existingNode)
    {
        if (nodeEntry.Status is NodeStatus.Live)
        {
            _nodes.Remove(existingNode);

            existingNode.LastSeen = DateTime.UtcNow;
            _nodes.AddLast(existingNode);
        }
        else
        {
            ReplaceDeadNode(nodeEntry);
        }
    }

    private void AddNewNode(NodeTableEntry nodeEntry)
    {
        nodeEntry.LastSeen = DateTime.UtcNow;
        _nodes.AddLast(nodeEntry);
    }

    private void CheckLeastRecentlySeenNode(NodeTableEntry nodeEntry)
    {
        var leastRecentlySeenNode = GetLeastRecentlySeenNode();

        if (leastRecentlySeenNode == null)
            return;

        if (leastRecentlySeenNode.Status is NodeStatus.Live or NodeStatus.Pending)
        {
            AddToReplacementCache(nodeEntry);
        }
        else
        {
            ReplaceDeadNode(leastRecentlySeenNode);
            AddToReplacementCache(nodeEntry);
        }
    }
}
