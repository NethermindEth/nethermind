// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using Collections.Pooled;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Nethermind.Kademlia;

public class KBucketTree<TNode, TKadKey> : IHealthRoutingTable<TNode, TKadKey>
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
    private readonly Func<TNode, UInt128?>? _getSubnet;
    private readonly int _bucketSubnetLimit;
    private readonly int _tableSubnetLimit;
    private readonly bool _requireReplacementValidation;
    private readonly int _k;
    private readonly TKadKey _currentNodeHash;
    private readonly IKademliaDistance<TKadKey> _distance;
    private readonly Func<TNode, TNode, TNode>? _mergeOnRefresh;
    private readonly ILogger _logger;

    private readonly Lock _lock = new();
    private readonly Dictionary<TKadKey, object> _activeRevisions = [];
    private bool _isMergingRefresh;

    public KBucketTree(
        KademliaConfig<TNode> config,
        INodeHashProvider<TNode, TKadKey> nodeHashProvider,
        IKademliaDistance<TKadKey> distance)
        : this(config, nodeHashProvider, distance, NullLoggerFactory.Instance)
    {
    }

    public KBucketTree(
        KademliaConfig<TNode> config,
        INodeHashProvider<TNode, TKadKey> nodeHashProvider,
        IKademliaDistance<TKadKey> distance,
        ILoggerFactory loggerFactory)
    {
        _getSubnet = config.GetSubnet;
        _bucketSubnetLimit = config.BucketSubnetLimit;
        _tableSubnetLimit = config.TableSubnetLimit;
        _requireReplacementValidation = config.RequireReplacementValidation;
        _k = config.KSize;
        _b = config.Beta;
        _distance = distance;
        _currentNodeHash = nodeHashProvider.GetHash(config.CurrentNodeId);
        _mergeOnRefresh = config.MergeOnRefresh;
        _root = new TreeNode(config.KSize, distance.Zero);
        _logger = loggerFactory.CreateLogger<KBucketTree<TNode, TKadKey>>();
        if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace($"Initialized KBucketTree with k={_k}, currentNodeId={_currentNodeHash}");
    }

    public BucketAddResult TryAddOrRefresh(in TKadKey nodeHash, TNode node, out TNode? toRefresh)
    {
        BucketAddResult result = TryAddOrRefresh(nodeHash, node, out toRefresh, out RoutingTableNotifications<TNode> notifications);
        Publish(notifications);
        return result;
    }

    internal BucketAddResult TryAddOrRefresh(in TKadKey nodeHash, TNode node, out TNode? toRefresh, out RoutingTableNotifications<TNode> notifications)
    {
        BucketAddResult resp;
        bool fireAdded;
        TNode nodeToStore = node;
        lock (_lock)
        {
            ThrowIfMergeReentered();
            KBucket<TNode, TKadKey> bucket = GetBucketForHash(nodeHash);
            if (_mergeOnRefresh is not null && bucket.TryGetStored(nodeHash, out TNode? previous))
            {
                _isMergingRefresh = true;
                try
                {
                    nodeToStore = _mergeOnRefresh(nodeToStore, previous);
                }
                finally
                {
                    _isMergingRefresh = false;
                }
            }

            resp = TryAddOrRefreshLocked(nodeHash, nodeToStore, out toRefresh, out fireAdded);
            if (resp != BucketAddResult.Full) _activeRevisions[nodeHash] = new object();
        }

        notifications = new(fireAdded, nodeToStore, false, default);
        return resp;
    }

    internal void Publish(in RoutingTableNotifications<TNode> notifications)
    {
        if (notifications.HasRemoved) OnNodeRemoved?.Invoke(this, notifications.Removed!);
        if (notifications.HasAdded) OnNodeAdded?.Invoke(this, notifications.Added!);
    }

    BucketAddResult IHealthRoutingTable<TNode, TKadKey>.AddOrRefreshDeferred(in TKadKey hash, TNode node, out TNode? toRefresh, out RoutingTableNotifications<TNode> notifications)
        => TryAddOrRefresh(hash, node, out toRefresh, out notifications);

    bool IHealthRoutingTable<TNode, TKadKey>.TryGetActive(in TKadKey hash, [MaybeNullWhen(false)] out TNode node, [NotNullWhen(true)] out object? revision)
    {
        lock (_lock)
        {
            KBucket<TNode, TKadKey> bucket = GetBucketForHash(hash);
            if (bucket.ContainsNode(hash) && bucket.TryGetStored(hash, out node))
            {
                revision = _activeRevisions[hash];
                return true;
            }
            node = default!;
            revision = null;
            return false;
        }
    }

    bool IHealthRoutingTable<TNode, TKadKey>.Remove(in TKadKey hash, TNode expected, object revision, out RoutingTableNotifications<TNode> notifications)
        => Remove(hash, expected, true, true, out notifications, revision);

    void IHealthRoutingTable<TNode, TKadKey>.Publish(in RoutingTableNotifications<TNode> notifications)
        => Publish(notifications);

    private BucketAddResult TryAddOrRefreshLocked(
        in TKadKey nodeHash,
        TNode node,
        out TNode? toRefresh,
        out bool fireAdded)
    {
        if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace($"Adding node {node} with XOR distance {_distance.CalculateLogDistance(_currentNodeHash, nodeHash)}");

        TreeNode current = _root;
        // As in, what would be the depth of the node assuming all branch on the traversal is populated.
        int logDistance = _distance.MaxDistance - _distance.CalculateLogDistance(_currentNodeHash, nodeHash);
        int depth = 0;
        while (true)
        {
            if (current.IsLeaf)
            {
                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace($"Reached leaf node at depth {depth}");
                if (current.Bucket.Count >= _k && !current.Bucket.ContainsNode(nodeHash) && ShouldSplit(depth, logDistance))
                {
                    SplitBucket(depth, current);
                    continue;
                }
                if (_getSubnet?.Invoke(node) is { } subnet)
                {
                    bool replacing = current.Bucket.TryGetStored(nodeHash, out TNode? previous);
                    if (!replacing) replacing = current.Bucket.TryGetReplacementToEvict(out previous);
                    if (!replacing || _getSubnet(previous!) != subnet)
                    {
                        Func<TNode, bool> matches = item => (!replacing || !EqualityComparer<TNode>.Default.Equals(item, previous)) && _getSubnet(item) == subnet;
                        if (current.Bucket.CountMatches(matches) >= _bucketSubnetLimit || CountSubnet(_root, matches, _tableSubnetLimit) >= _tableSubnetLimit)
                        {
                            toRefresh = default;
                            fireAdded = false;
                            return BucketAddResult.Full;
                        }
                    }
                }
                BucketAddResult resp = current.Bucket.TryAddOrRefresh(nodeHash, node, out toRefresh);
                fireAdded = resp == BucketAddResult.Added;
                if (resp is BucketAddResult.Added or BucketAddResult.Refreshed)
                {
                    if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace($"Successfully added/refreshed node {node} in bucket at depth {depth}");
                    return resp;
                }

                if (resp == BucketAddResult.Full && ShouldSplit(depth, logDistance))
                {
                    if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace($"Splitting bucket at depth {depth}");
                    SplitBucket(depth, current);
                    continue;
                }

                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace($"Failed to add node {nodeHash} {node}. Bucket at depth {depth} is full. {_k} {current.Bucket.Count}");
                return resp;
            }

            bool goRight = _distance.GetBit(nodeHash, depth);
            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace($"Traversing {(goRight ? "right" : "left")} at depth {depth}");

            current = goRight ? current.Right! : current.Left!;
            depth++;
        }
    }

    private static int CountSubnet(TreeNode node, Func<TNode, bool> matches, int remaining)
    {
        if (node.IsLeaf) return node.Bucket.CountMatches(matches);
        int left = CountSubnet(node.Left!, matches, remaining);
        return left >= remaining ? left : left + CountSubnet(node.Right!, matches, remaining - left);
    }

    public bool TryGet(in TKadKey hash, [MaybeNullWhen(false)] out TNode node)
    {
        lock (_lock)
        {
            return GetBucketForHash(hash).TryGetStored(hash, out node);
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
                if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace($"Reached leaf node at depth {depth}");
                return current.Bucket;
            }

            bool goRight = _distance.GetBit(nodeHash, depth);
            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace($"Traversing {(goRight ? "right" : "left")} at depth {depth}");

            current = goRight ? current.Right! : current.Left!;
            depth++;
        }
    }

    private bool ShouldSplit(int depth, int targetLogDistance)
    {
        bool shouldSplit = depth < _distance.MaxDistance && targetLogDistance + _b >= depth;
        if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace($"ShouldSplit at depth {depth}: {shouldSplit}");
        return shouldSplit;
    }

    private void SplitBucket(int depth, TreeNode node)
    {
        node.Left = new TreeNode(_k, node.Prefix);
        node.Right = new TreeNode(_k, _distance.SetBit(node.Prefix, depth));

        if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace($"Created children at depth {depth + 1}");

        // Iterate from oldest to newest so the new buckets preserve original LRU order.
        (TKadKey, TNode)[] items = node.Bucket.GetAllWithHash();
        for (int i = items.Length - 1; i >= 0; i--)
        {
            (TKadKey itemHash, TNode value) = items[i];
            TreeNode? targetNode = _distance.GetBit(itemHash, depth) ? node.Right : node.Left;
            targetNode.Bucket.TryAddOrRefresh(itemHash, value, out _);
            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace($"Moved item ({itemHash}, {value}) to {(_distance.GetBit(itemHash, depth) ? "right" : "left")} child");
        }

        node.Bucket.Clear();
        if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace($"Finished splitting bucket. Left count: {node.Left.Bucket.Count}, Right count: {node.Right.Bucket.Count}");
    }

    public bool Remove(in TKadKey nodeHash)
    {
        bool removed = Remove(nodeHash, default, false, false, out RoutingTableNotifications<TNode> notifications);
        Publish(notifications);
        return removed;
    }

    private bool Remove(in TKadKey nodeHash, TNode? expected, bool conditional, bool activeOnly, out RoutingTableNotifications<TNode> notifications, object? revision = null)
    {
        notifications = default;
        bool removed;
        bool wasActive;
        TNode? removedNode;
        TNode? promoted;
        bool didPromote;
        lock (_lock)
        {
            ThrowIfMergeReentered();
            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace($"Attempting to remove node {nodeHash}");

            KBucket<TNode, TKadKey> bucket = GetBucketForHash(nodeHash);
            wasActive = bucket.ContainsNode(nodeHash);
            if (revision is not null && (!_activeRevisions.TryGetValue(nodeHash, out object? currentRevision)
                || !ReferenceEquals(currentRevision, revision))) return false;
            if (activeOnly && !wasActive) return false;
            bucket.TryGetStored(nodeHash, out removedNode);
            if (conditional && (typeof(TNode).IsValueType
                ? !EqualityComparer<TNode>.Default.Equals(removedNode, expected)
                : !ReferenceEquals(removedNode, expected))) return false;
            removed = bucket.Remove(nodeHash, !_requireReplacementValidation, out promoted, out didPromote);
            if (removed) _activeRevisions.Remove(nodeHash);
            if (didPromote)
            {
                foreach ((TKadKey hash, TNode _) in bucket.GetAllWithHash())
                {
                    if (!_activeRevisions.ContainsKey(hash)) _activeRevisions[hash] = new object();
                }
            }
        }

        notifications = new(didPromote, promoted, removed && wasActive, removedNode);
        return removed;
    }

    public TNode[] GetReplacementCandidates(in TKadKey hash)
    {
        lock (_lock)
        {
            return GetBucketForHash(hash).GetReplacementCandidates();
        }
    }

    private void ThrowIfMergeReentered()
    {
        if (_isMergingRefresh)
        {
            throw new InvalidOperationException("MergeOnRefresh must not mutate the routing table that invoked it.");
        }
    }

    public TNode[] GetAllAtDistance(int distance)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(distance);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(distance, _distance.MaxDistance);
        lock (_lock)
        {
            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace($"Getting all nodes at distance {distance}");
            using PooledList<TNode> result = new(_k);
            (TKadKey Hash, TNode Node)[] bucketEntries = ArrayPool<(TKadKey Hash, TNode Node)>.Shared.Rent(_k);
            GetAllAtDistanceRecursive(_root, 0, distance, result, bucketEntries);
            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace($"Found {result.Count} nodes at distance {distance}");

            TNode[] nodes = result.Span.ToArray();
            ArrayPool<(TKadKey Hash, TNode Node)>.Shared.Return(bucketEntries, RuntimeHelpers.IsReferenceOrContainsReferences<(TKadKey Hash, TNode Node)>());
            return nodes;
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
        if (!_logger.IsEnabled(LogLevel.Debug)) return;

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

        _logger.LogDebug($"Tree Statistics: Total Nodes: {totalNodes}, Total Buckets: {totalBuckets}, Max Depth: {maxDepth}, Total Items: {totalItems}, Average Items per Bucket: {(double)totalItems / totalBuckets:F2}");
    }

    private void LogTreeStructure()
    {
        if (!_logger.IsEnabled(LogLevel.Trace)) return;

        StringBuilder sb = new();
        LogTreeStructureRecursive(_root, "", true, 0, sb);
        _logger.LogTrace($"Current Tree Structure:{Environment.NewLine}{sb}");
    }

    public void LogDebugInfo()
    {
        if (!_logger.IsEnabled(LogLevel.Debug)) return;

        LogTreeStatistics();
        LogTreeStructure();
    }

    public event EventHandler<TNode>? OnNodeAdded;
    public event EventHandler<TNode>? OnNodeRemoved;

    public RoutingTableOccupancy GetOccupancy()
    {
        int nodeCount = 0;
        int bucketCount = 0;
        lock (_lock)
        {
            CountOccupancy(_root, ref nodeCount, ref bucketCount);
        }

        return new RoutingTableOccupancy(nodeCount, bucketCount * _k);
    }

    private static void CountOccupancy(TreeNode node, ref int nodeCount, ref int bucketCount)
    {
        if (node.IsLeaf)
        {
            nodeCount += node.Bucket.Count;
            bucketCount++;
            return;
        }

        CountOccupancy(node.Left!, ref nodeCount, ref bucketCount);
        CountOccupancy(node.Right!, ref nodeCount, ref bucketCount);
    }
}
