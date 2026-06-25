// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using Collections.Pooled;
using Nethermind.Logging;

namespace Nethermind.Kademlia;

public class KBucketTree<TNode, TKadKey> : IRoutingTable<TNode, TKadKey>
    where TNode : notnull
    where TKadKey : notnull
{
    private class TreeNode(int k, TKadKey prefix)
    {
        public KBucket<TNode, TKadKey> Bucket { get; } = new KBucket<TNode, TKadKey>(k);
        public TreeNode? Left { get; set; }
        public TreeNode? Right { get; set; }
        public TKadKey Prefix { get; } = prefix;
        public bool IsLeaf => Left is null && Right is null;
    }

    private readonly TreeNode _root;
    private readonly int _b;
    private readonly int _k;
    private readonly TKadKey _currentNodeHash;
    private readonly IKademliaDistance<TKadKey> _distance;
    private readonly ILogger _logger;

    private readonly Lock _lock = new();

    public KBucketTree(
        KademliaConfig<TNode> config,
        INodeHashProvider<TNode, TKadKey> nodeHashProvider,
        IKademliaDistance<TKadKey> distance,
        ILogManager? logManager = null)
    {
        _k = config.KSize;
        _b = config.Beta;
        _distance = distance;
        _currentNodeHash = nodeHashProvider.GetHash(config.CurrentNodeId);
        _root = new TreeNode(config.KSize, distance.Zero);
        _logger = (logManager ?? NullLogManager.Instance).GetClassLogger<KBucketTree<TNode, TKadKey>>();
        if (_logger.IsDebug)
        {
            _logger.Debug($"Initialized KBucketTree with k={_k}, currentNodeId={_currentNodeHash}");
        }
    }

    public BucketAddResult TryAddOrRefresh(in TKadKey nodeHash, TNode node, out TNode? toRefresh)
    {
        BucketAddResult resp;
        bool fireAdded;
        lock (_lock)
        {
            if (_logger.IsDebug)
            {
                _logger.Debug($"Adding node {node} with XOR distance {_distance.CalculateLogDistance(_currentNodeHash, nodeHash)}");
            }

            TreeNode current = _root;
            // As in, what would be the depth of the node assuming all branch on the traversal is populated.
            int logDistance = _distance.MaxDistance - _distance.CalculateLogDistance(_currentNodeHash, nodeHash);
            int depth = 0;
            while (true)
            {
                if (current.IsLeaf)
                {
                    if (_logger.IsTrace) _logger.Trace($"Reached leaf node at depth {depth}");
                    resp = current.Bucket.TryAddOrRefresh(nodeHash, node, out toRefresh);
                    fireAdded = resp == BucketAddResult.Added;
                    if (resp is BucketAddResult.Added or BucketAddResult.Refreshed)
                    {
                        if (_logger.IsDebug) _logger.Debug($"Successfully added/refreshed node {node} in bucket at depth {depth}");
                        break;
                    }

                    if (resp == BucketAddResult.Full && ShouldSplit(depth, logDistance))
                    {
                        if (_logger.IsTrace) _logger.Trace($"Splitting bucket at depth {depth}");
                        SplitBucket(depth, current);
                        continue;
                    }

                    if (_logger.IsDebug) _logger.Debug($"Failed to add node {nodeHash} {node}. Bucket at depth {depth} is full. {_k} {current.Bucket.Count}");
                    break;
                }

                bool goRight = _distance.GetBit(nodeHash, depth);
                if (_logger.IsTrace) _logger.Trace($"Traversing {(goRight ? "right" : "left")} at depth {depth}");

                current = goRight ? current.Right! : current.Left!;
                depth++;
            }
        }

        if (fireAdded) OnNodeAdded?.Invoke(this, node);
        return resp;
    }

    public TNode? GetByHash(TKadKey hash)
    {
        lock (_lock)
        {
            return GetBucketForHash(hash).GetByHash(hash);
        }
    }

    private KBucket<TNode, TKadKey> GetBucketForHash(TKadKey nodeHash)
    {
        TreeNode current = _root;
        int depth = 0;
        while (true)
        {
            if (current.IsLeaf)
            {
                if (_logger.IsTrace) _logger.Trace($"Reached leaf node at depth {depth}");
                return current.Bucket;
            }

            bool goRight = _distance.GetBit(nodeHash, depth);
            if (_logger.IsTrace) _logger.Trace($"Traversing {(goRight ? "right" : "left")} at depth {depth}");

            current = goRight ? current.Right! : current.Left!;
            depth++;
        }
    }

    private bool ShouldSplit(int depth, int targetLogDistance)
    {
        bool shouldSplit = depth < _distance.MaxDistance && targetLogDistance + _b >= depth;
        if (_logger.IsTrace) _logger.Trace($"ShouldSplit at depth {depth}: {shouldSplit}");
        return shouldSplit;
    }

    private void SplitBucket(int depth, TreeNode node)
    {
        node.Left = new TreeNode(_k, node.Prefix);
        node.Right = new TreeNode(_k, _distance.SetBit(node.Prefix, depth));

        if (_logger.IsTrace) _logger.Trace($"Created children at depth {depth + 1}");

        // Iterate from oldest to newest so the new buckets preserve original LRU order.
        (TKadKey, TNode)[] items = node.Bucket.GetAllWithHash();
        for (int i = items.Length - 1; i >= 0; i--)
        {
            (TKadKey itemHash, TNode value) = items[i];
            TreeNode? targetNode = _distance.GetBit(itemHash, depth) ? node.Right : node.Left;
            targetNode.Bucket.TryAddOrRefresh(itemHash, value, out _);
            if (_logger.IsTrace) _logger.Trace($"Moved item ({itemHash}, {value}) to {(_distance.GetBit(itemHash, depth) ? "right" : "left")} child");
        }

        node.Bucket.Clear();
        if (_logger.IsDebug) _logger.Debug($"Finished splitting bucket. Left count: {node.Left.Bucket.Count}, Right count: {node.Right.Bucket.Count}");
    }

    public bool Remove(in TKadKey nodeHash)
    {
        bool removed;
        TNode? removedNode;
        lock (_lock)
        {
            if (_logger.IsDebug) _logger.Debug($"Attempting to remove node {nodeHash}");

            KBucket<TNode, TKadKey> bucket = GetBucketForHash(nodeHash);
            removedNode = bucket.GetByHash(nodeHash);
            removed = bucket.RemoveAndReplace(nodeHash);
        }

        if (removed && removedNode is not null) OnNodeRemoved?.Invoke(this, removedNode);
        return removed;
    }

    public TNode[] GetAllAtDistance(int distance)
    {
        lock (_lock)
        {
            if (_logger.IsDebug) _logger.Debug($"Getting all nodes at distance {distance}");
            using PooledList<TNode> result = new(_k);
            (TKadKey Hash, TNode Node)[] bucketEntries = ArrayPool<(TKadKey Hash, TNode Node)>.Shared.Rent(_k);
            try
            {
                GetAllAtDistanceRecursive(_root, 0, distance, result, bucketEntries);
                if (_logger.IsDebug) _logger.Debug($"Found {result.Count} nodes at distance {distance}");

                return result.Span.ToArray();
            }
            finally
            {
                ArrayPool<(TKadKey Hash, TNode Node)>.Shared.Return(bucketEntries, RuntimeHelpers.IsReferenceOrContainsReferences<(TKadKey Hash, TNode Node)>());
            }
        }
    }

    private void GetAllAtDistanceRecursive(TreeNode node, int depth, int distance, PooledList<TNode> result, (TKadKey Hash, TNode Node)[] bucketEntries)
    {
        int targetDepth = _distance.MaxDistance - distance;
        if (node.IsLeaf)
        {
            if (depth <= targetDepth)
            {
                int entryCount = node.Bucket.CopyAllWithHash(bucketEntries);
                for (int i = 0; i < entryCount; i++)
                {
                    (TKadKey hash, TNode item) = bucketEntries[i];
                    if (_distance.CalculateLogDistance(hash, _currentNodeHash) == distance)
                    {
                        result.Add(item);
                    }
                }
            }
            else
            {
                TNode[] items = node.Bucket.GetAllCached();
                for (int i = 0; i < items.Length; i++)
                {
                    result.Add(items[i]);
                }
            }
        }
        else
        {
            if (depth < targetDepth)
            {
                bool goRight = _distance.GetBit(_currentNodeHash, depth);
                if (goRight)
                {
                    GetAllAtDistanceRecursive(node.Right!, depth + 1, distance, result, bucketEntries);
                }
                else
                {
                    GetAllAtDistanceRecursive(node.Left!, depth + 1, distance, result, bucketEntries);
                }
            }
            else if (depth == targetDepth)
            {
                bool goRight = _distance.GetBit(_currentNodeHash, depth);
                // Note: We go the opposite direction here, as the same direction would have a distance + 1
                if (goRight)
                {
                    GetAllAtDistanceRecursive(node.Left!, depth + 1, distance, result, bucketEntries);
                }
                else
                {
                    GetAllAtDistanceRecursive(node.Right!, depth + 1, distance, result, bucketEntries);
                }
            }
            else
            {
                GetAllAtDistanceRecursive(node.Left!, depth + 1, distance, result, bucketEntries);
                GetAllAtDistanceRecursive(node.Right!, depth + 1, distance, result, bucketEntries);
            }
        }
    }

    public IEnumerable<RoutingTableBucket<TNode, TKadKey>> IterateBuckets()
    {
        lock (_lock)
        {
            // Materialize snapshots while holding the tree lock so callers cannot observe live bucket state.
            return DoIterateBucketRandomHashes(_root, 0).ToArray();
        }
    }

    private IEnumerable<RoutingTableBucket<TNode, TKadKey>> DoIterateBucketRandomHashes(TreeNode node, int depth)
    {
        if (node.IsLeaf)
        {
            yield return new RoutingTableBucket<TNode, TKadKey>(node.Prefix, depth, node.Bucket.GetAll());
        }
        else
        {
            foreach (RoutingTableBucket<TNode, TKadKey> bucketInfo in DoIterateBucketRandomHashes(node.Left!, depth + 1))
            {
                yield return bucketInfo;
            }

            foreach (RoutingTableBucket<TNode, TKadKey> bucketInfo in DoIterateBucketRandomHashes(node.Right!, depth + 1))
            {
                yield return bucketInfo;
            }
        }
    }

    private IEnumerable<(TKadKey, TNode)> IterateNeighbour(TKadKey hash)
    {
        foreach (TreeNode treeNode in IterateNodeFromClosestToTarget(_root, 0, hash))
        {
            foreach ((TKadKey, TNode) entry in treeNode.Bucket.GetAllWithHash())
            {
                yield return entry;
            }
        }
    }

    private IEnumerable<TreeNode> IterateNodeFromClosestToTarget(TreeNode currentNode, int depth, TKadKey target)
    {
        if (currentNode.IsLeaf)
        {
            yield return currentNode;
        }
        else
        {
            if (_distance.GetBit(target, depth))
            {
                foreach (TreeNode treeNode in IterateNodeFromClosestToTarget(currentNode.Right!, depth + 1, target))
                {
                    yield return treeNode;
                }

                foreach (TreeNode treeNode in IterateNodeFromClosestToTarget(currentNode.Left!, depth + 1, target))
                {
                    yield return treeNode;
                }
            }
            else
            {
                foreach (TreeNode treeNode in IterateNodeFromClosestToTarget(currentNode.Left!, depth + 1, target))
                {
                    yield return treeNode;
                }

                foreach (TreeNode treeNode in IterateNodeFromClosestToTarget(currentNode.Right!, depth + 1, target))
                {
                    yield return treeNode;
                }
            }
        }
    }

    public TNode[] GetKNearestNeighbour(TKadKey hash, bool excludeSelf = false) => GetKNearestNeighbour(hash, default!, false, excludeSelf);

    public TNode[] GetKNearestNeighbourExcluding(TKadKey hash, TKadKey exclude, bool excludeSelf = false) => GetKNearestNeighbour(hash, exclude, true, excludeSelf);

    private TNode[] GetKNearestNeighbour(TKadKey hash, TKadKey exclude, bool hasExclude, bool excludeSelf)
    {
        lock (_lock)
        {
            KBucket<TNode, TKadKey> firstBucket = GetBucketForHash(hash);
            bool shouldNotContainExcludedNode = !hasExclude || !firstBucket.ContainsNode(exclude);
            bool shouldNotContainSelf = !excludeSelf || !firstBucket.ContainsNode(_currentNodeHash);

            if (shouldNotContainExcludedNode && shouldNotContainSelf)
            {
                TNode[] nodes = firstBucket.GetAllCached();
                if (nodes.Length == _k)
                {
                    // Fast path. In theory, most of the time, this avoids neighbour traversal and concatenation.
                    return (TNode[])nodes.Clone();
                }
            }

            TNode[] resultArr = new TNode[_k];
            int count = 0;
            foreach ((TKadKey itemHash, TNode item) in IterateNeighbour(hash))
            {
                if (hasExclude && EqualityComparer<TKadKey>.Default.Equals(itemHash, exclude)) continue;
                if (excludeSelf && EqualityComparer<TKadKey>.Default.Equals(itemHash, _currentNodeHash)) continue;
                resultArr[count++] = item;
                if (count == _k) break;
            }

            if (count == _k) return resultArr;
            TNode[] truncated = new TNode[count];
            Array.Copy(resultArr, truncated, count);
            return truncated;
        }
    }

    private void LogTreeStructureRecursive(TreeNode node, string indent, bool last, int depth, StringBuilder sb)
    {
        sb.Append(indent);
        if (last)
        {
            sb.Append("└─");
            indent += "  ";
        }
        else
        {
            sb.Append("├─");
            indent += "│ ";
        }

        if (node.Left is null && node.Right is null)
        {
            sb.AppendLine($"Bucket (Depth: {depth}, Count: {node.Bucket.Count})");
            return;
        }

        sb.AppendLine($"Node (Depth: {depth})");
        LogTreeStructureRecursive(node.Left!, indent, false, depth + 1, sb);
        LogTreeStructureRecursive(node.Right!, indent, true, depth + 1, sb);
    }

    private void LogTreeStatistics()
    {
        if (!_logger.IsDebug) return;

        int totalNodes = 0;
        int totalBuckets = 0;
        int maxDepth = 0;
        int totalItems = 0;

        void TraverseTree(TreeNode node, int depth)
        {
            totalNodes++;
            maxDepth = Math.Max(maxDepth, depth);

            if (node.Left is null && node.Right is null)
            {
                totalBuckets++;
                totalItems += node.Bucket.Count;
            }
            else
            {
                TraverseTree(node.Left!, depth + 1);
                TraverseTree(node.Right!, depth + 1);
            }
        }

        TraverseTree(_root, 0);

        _logger.Debug($"Tree Statistics: Total Nodes: {totalNodes}, Total Buckets: {totalBuckets}, Max Depth: {maxDepth}, Total Items: {totalItems}, Average Items per Bucket: {(double)totalItems / totalBuckets:F2}");
    }

    private void LogTreeStructure()
    {
        if (!_logger.IsTrace) return;

        StringBuilder sb = new();
        LogTreeStructureRecursive(_root, "", true, 0, sb);
        _logger.Trace($"Current Tree Structure:{Environment.NewLine}{sb}");
    }

    public void LogDebugInfo()
    {
        if (!_logger.IsDebug) return;

        LogTreeStatistics();
        LogTreeStructure();
    }

    public event EventHandler<TNode>? OnNodeAdded;
    public event EventHandler<TNode>? OnNodeRemoved;

    public int Size
    {
        get
        {
            int total = 0;
            lock (_lock)
            {
                CountNodes(_root);
            }

            return total;

            void CountNodes(TreeNode node)
            {
                if (node.IsLeaf)
                {
                    total += node.Bucket.Count;
                    return;
                }

                CountNodes(node.Left!);
                CountNodes(node.Right!);
            }
        }
    }
}
