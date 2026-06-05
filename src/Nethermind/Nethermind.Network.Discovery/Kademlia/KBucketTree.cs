// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using Nethermind.Logging;

namespace Nethermind.Network.Discovery.Kademlia;

public class KBucketTree<TNode> : IRoutingTable<TNode> where TNode : notnull
{
    private class TreeNode(int k, ValueHash256 prefix)
    {
        public KBucket<TNode> Bucket { get; } = new KBucket<TNode>(k);
        public TreeNode? Left { get; set; }
        public TreeNode? Right { get; set; }
        public ValueHash256 Prefix { get; } = prefix;
        public bool IsLeaf => Left == null && Right == null;
    }

    private readonly TreeNode _root;
    private readonly int _b;
    private readonly int _k;
    private readonly ValueHash256 _currentNodeHash;
    private readonly ILogger _logger;

    // TODO: Double check and probably make lockless
    private readonly McsLock _lock = new();

    public KBucketTree(KademliaConfig<TNode> config, INodeHashProvider<TNode> nodeHashProvider, ILogManager logManager)
    {
        _k = config.KSize;
        _b = config.Beta;
        _currentNodeHash = nodeHashProvider.GetHash(config.CurrentNodeId);
        _root = new TreeNode(config.KSize, new ValueHash256());
        _logger = logManager.GetClassLogger<KBucketTree<TNode>>();
        if (_logger.IsDebug) _logger.Debug($"Initialized KBucketTree with k={_k}, currentNodeId={_currentNodeHash}");
    }

    public BucketAddResult TryAddOrRefresh(in ValueHash256 nodeHash, TNode node, out TNode? toRefresh)
    {
        BucketAddResult resp;
        bool fireAdded;
        using (_lock.Acquire())
        {
            if (_logger.IsDebug) _logger.Debug($"Adding node {node} with XOR distance {Hash256XorUtils.XorDistance(_currentNodeHash, nodeHash)}");

            TreeNode current = _root;
            // As in, what would be the depth of the node assuming all branch on the traversal is populated.
            int logDistance = Hash256XorUtils.MaxDistance - Hash256XorUtils.CalculateLogDistance(_currentNodeHash, nodeHash);
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

                bool goRight = GetBit(nodeHash, depth);
                if (_logger.IsTrace) _logger.Trace($"Traversing {(goRight ? "right" : "left")} at depth {depth}");

                current = goRight ? current.Right! : current.Left!;
                depth++;
            }
        }

        if (fireAdded) OnNodeAdded?.Invoke(this, node);
        return resp;
    }

    public TNode? GetByHash(ValueHash256 hash)
    {
        using McsLock.Disposable _ = _lock.Acquire();
        return GetBucketForHash(hash).GetByHash(hash);
    }

    private KBucket<TNode> GetBucketForHash(ValueHash256 nodeHash)
    {
        TreeNode current = _root;
        int depth = 0;
        while (true)
        {
            if (current.IsLeaf)
            {
                if (_logger.IsDebug) _logger.Debug($"Reached leaf node at depth {depth}");
                return current.Bucket;
            }

            bool goRight = GetBit(nodeHash, depth);
            if (_logger.IsDebug) _logger.Debug($"Traversing {(goRight ? "right" : "left")} at depth {depth}");

            current = goRight ? current.Right! : current.Left!;
            depth++;
        }
    }

    private bool ShouldSplit(int depth, int targetLogDistance)
    {
        bool shouldSplit = depth < 256 && targetLogDistance + _b >= depth;
        if (_logger.IsDebug) _logger.Debug($"ShouldSplit at depth {depth}: {shouldSplit}");
        return shouldSplit;
    }

    private void SplitBucket(int depth, TreeNode node)
    {
        node.Left = new TreeNode(_k, node.Prefix);
        byte[] rightPrefixBytes = node.Prefix.Bytes.ToArray();
        rightPrefixBytes[depth / 8] |= (byte)(1 << (7 - (depth % 8)));
        node.Right = new TreeNode(_k, new ValueHash256(rightPrefixBytes));

        if (_logger.IsDebug) _logger.Debug($"Created children at depth {depth + 1}");

        // Iterate from oldest to newest so the new buckets preserve original LRU order.
        (ValueHash256, TNode)[] items = node.Bucket.GetAllWithHash();
        for (int i = items.Length - 1; i >= 0; i--)
        {
            (ValueHash256 itemHash, TNode value) = items[i];
            TreeNode? targetNode = GetBit(itemHash, depth) ? node.Right : node.Left;
            targetNode.Bucket.TryAddOrRefresh(itemHash, value, out _);
            if (_logger.IsDebug) _logger.Debug($"Moved item ({itemHash}, {value}) to {(GetBit(itemHash, depth) ? "right" : "left")} child");
        }

        node.Bucket.Clear();
        if (_logger.IsDebug) _logger.Debug($"Finished splitting bucket. Left count: {node.Left.Bucket.Count}, Right count: {node.Right.Bucket.Count}");
    }

    public bool Remove(in ValueHash256 nodeHash)
    {
        bool removed;
        TNode? removedNode;
        using (_lock.Acquire())
        {
            if (_logger.IsDebug) _logger.Debug($"Attempting to remove node {nodeHash} with hash {nodeHash}");

            KBucket<TNode> bucket = GetBucketForHash(nodeHash);
            removedNode = bucket.GetByHash(nodeHash);
            removed = bucket.RemoveAndReplace(nodeHash);
        }

        if (removed && removedNode is not null) OnNodeRemoved?.Invoke(this, removedNode);
        return removed;
    }

    public TNode[] GetAllAtDistance(int distance)
    {
        using McsLock.Disposable _ = _lock.Acquire();

        if (_logger.IsDebug) _logger.Debug($"Getting all nodes at distance {distance}");
        List<TNode> result = [];
        GetAllAtDistanceRecursive(_root, 0, distance, result);
        if (_logger.IsDebug) _logger.Debug($"Found {result.Count} nodes at distance {distance}");
        return [.. result];
    }

    private void GetAllAtDistanceRecursive(TreeNode node, int depth, int distance, List<TNode> result)
    {
        int targetDepth = Hash256XorUtils.MaxDistance - distance;
        if (node.IsLeaf)
        {
            if (depth <= targetDepth)
            {
                foreach ((ValueHash256 hash, TNode item) in node.Bucket.GetAllWithHash())
                {
                    if (Hash256XorUtils.CalculateLogDistance(hash, _currentNodeHash) == distance)
                    {
                        result.Add(item);
                    }
                }
            }
            else
            {
                result.AddRange(node.Bucket.GetAll());
            }
        }
        else
        {
            if (depth < targetDepth)
            {
                bool goRight = GetBit(_currentNodeHash, depth);
                if (goRight)
                {
                    GetAllAtDistanceRecursive(node.Right!, depth + 1, distance, result);
                }
                else
                {
                    GetAllAtDistanceRecursive(node.Left!, depth + 1, distance, result);
                }
            }
            else if (depth == targetDepth)
            {
                bool goRight = GetBit(_currentNodeHash, depth);
                // Note: We go the opposite direction here, as the same direction would have a distance + 1
                if (goRight)
                {
                    GetAllAtDistanceRecursive(node.Left!, depth + 1, distance, result);
                }
                else
                {
                    GetAllAtDistanceRecursive(node.Right!, depth + 1, distance, result);
                }
            }
            else
            {
                GetAllAtDistanceRecursive(node.Left!, depth + 1, distance, result);
                GetAllAtDistanceRecursive(node.Right!, depth + 1, distance, result);
            }
        }
    }

    public IEnumerable<(ValueHash256 Prefix, int Distance, KBucket<TNode> Bucket)> IterateBuckets()
    {
        using McsLock.Disposable _ = _lock.Acquire();

        // Well, it need to ToArray, otherwise the lock does not really do anything.
        return DoIterateBucketRandomHashes(_root, 0).ToArray();
    }

    private IEnumerable<(ValueHash256 Prefix, int Distance, KBucket<TNode> Bucket)> DoIterateBucketRandomHashes(TreeNode node, int depth)
    {
        if (node.IsLeaf)
        {
            yield return (node.Prefix, depth, node.Bucket);
        }
        else
        {
            foreach ((ValueHash256 Prefix, int Distance, KBucket<TNode> Bucket) bucketInfo in DoIterateBucketRandomHashes(node.Left!, depth + 1))
            {
                yield return bucketInfo;
            }

            foreach ((ValueHash256 Prefix, int Distance, KBucket<TNode> Bucket) bucketInfo in DoIterateBucketRandomHashes(node.Right!, depth + 1))
            {
                yield return bucketInfo;
            }
        }
    }

    private IEnumerable<(ValueHash256, TNode)> IterateNeighbour(ValueHash256 hash)
    {
        foreach (TreeNode treeNode in IterateNodeFromClosestToTarget(_root, 0, hash))
        {
            foreach ((ValueHash256, TNode) entry in treeNode.Bucket.GetAllWithHash())
            {
                yield return entry;
            }
        }
    }

    private IEnumerable<TreeNode> IterateNodeFromClosestToTarget(TreeNode currentNode, int depth, ValueHash256 target)
    {
        if (currentNode.IsLeaf)
        {
            yield return currentNode;
        }
        else
        {
            if (GetBit(target, depth))
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

    public TNode[] GetKNearestNeighbour(ValueHash256 hash, ValueHash256? exclude, bool excludeSelf)
    {
        using McsLock.Disposable _ = _lock.Acquire();

        KBucket<TNode> firstBucket = GetBucketForHash(hash);
        bool shouldNotContainExcludedNode = exclude == null || !firstBucket.ContainsNode(exclude.Value);
        bool shouldNotContainSelf = !excludeSelf || !firstBucket.ContainsNode(_currentNodeHash);

        if (shouldNotContainExcludedNode && shouldNotContainSelf)
        {
            TNode[] nodes = firstBucket.GetAll();
            if (nodes.Length == _k)
            {
                // Fast path. In theory, most of the time, this would be the taken path, where no array
                // concatenation or creation is needed.
                return nodes;
            }
        }

        TNode[] resultArr = new TNode[_k];
        int count = 0;
        foreach ((ValueHash256 itemHash, TNode item) in IterateNeighbour(hash))
        {
            if (exclude != null && itemHash == exclude.Value) continue;
            if (excludeSelf && itemHash == _currentNodeHash) continue;
            resultArr[count++] = item;
            if (count == _k) break;
        }

        if (count == _k) return resultArr;
        TNode[] truncated = new TNode[count];
        Array.Copy(resultArr, truncated, count);
        return truncated;
    }

    private bool GetBit(ValueHash256 hash, int index)
    {
        int byteIndex = index / 8;
        int bitIndex = index % 8;
        return (hash.Bytes[byteIndex] & (1 << (7 - bitIndex))) != 0;
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

        if (node.Left == null && node.Right == null)
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

            if (node.Left == null && node.Right == null)
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

        _logger.Debug($"Tree Statistics:\n" +
                     $"Total Nodes: {totalNodes}\n" +
                     $"Total Buckets: {totalBuckets}\n" +
                     $"Max Depth: {maxDepth}\n" +
                     $"Total Items: {totalItems}\n" +
                     $"Average Items per Bucket: {(double)totalItems / totalBuckets:F2}");
    }

    private void LogTreeStructure()
    {
        if (!_logger.IsTrace) return;

        StringBuilder sb = new();
        LogTreeStructureRecursive(_root, "", true, 0, sb);
        _logger.Trace($"Current Tree Structure:\n{sb}");
    }

    public void LogDebugInfo()
    {
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
            foreach ((ValueHash256 Prefix, int Distance, KBucket<TNode> Bucket) in IterateBuckets())
            {
                total += Bucket.Count;
            }
            return total;
        }
    }
}
