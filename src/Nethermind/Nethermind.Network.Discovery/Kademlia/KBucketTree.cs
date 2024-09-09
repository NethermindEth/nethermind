// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using Nethermind.Logging;

namespace Nethermind.Network.Discovery.Kademlia;

public class KBucketTree<TNode, TContentKey>: IRoutingTable<TNode> where TNode : notnull
{
    private class TreeNode
    {
        public KBucket<TNode> Bucket { get; }
        public TreeNode? Left { get; set; }
        public TreeNode? Right { get; set; }
        public ValueHash256 Prefix { get; }
        public bool IsLeaf => Left == null && Right == null;

        public TreeNode(int k, ValueHash256 prefix)
        {
            Bucket = new KBucket<TNode>(k);
            Prefix = prefix;
        }
    }

    private readonly TreeNode _root;
    private readonly int _b;
    private readonly int _k;
    private readonly ValueHash256 _currentNodeHash;
    private readonly INodeHashProvider<TNode, TContentKey> _nodeHashProvider;
    private readonly ILogger _logger;

    // TODO: Double check and probably make lockless
    private readonly McsLock _lock = new McsLock();

    public KBucketTree(int k, int b, ValueHash256 currentNodeHash, INodeHashProvider<TNode, TContentKey> nodeHashProvider, ILogManager logManager)
    {
        _k = k;
        _b = b;
        _currentNodeHash = currentNodeHash;
        _nodeHashProvider = nodeHashProvider;
        _root = new TreeNode(k, new ValueHash256());
        _logger = logManager.GetClassLogger();
        _logger.Info($"Initialized KBucketTree with k={k}, currentNodeId={currentNodeHash}");
    }

    public BucketAddResult TryAddOrRefresh(in ValueHash256 nodeHash, TNode node, out TNode? toRefresh)
    {
        using McsLock.Disposable _ = _lock.Acquire();

        _logger.Info($"Adding node {node} with XOR distance {Hash256XORUtils.XorDistance(_currentNodeHash, nodeHash)}");

        TreeNode current = _root;
        // As in, what would be the depth of the node assuming all branch on the traversal is populated.
        int logDistance = Hash256XORUtils.MaxDistance - Hash256XORUtils.CalculateDistance(_currentNodeHash, nodeHash);
        int depth = 0;
        while (true)
        {
            if (current.IsLeaf)
            {
                _logger.Debug($"Reached leaf node at depth {depth}");
                var resp = current.Bucket.TryAddOrRefresh(nodeHash, node, out toRefresh);
                if (resp == BucketAddResult.Added)
                {
                    _logger.Info($"Successfully added/refreshed node {node} in bucket at depth {depth}");
                    return BucketAddResult.Added;
                }

                if (resp == BucketAddResult.Full && ShouldSplit(depth, logDistance))
                {
                    _logger.Info($"Splitting bucket at depth {depth}");
                    SplitBucket(depth, current);
                    continue;
                }

                _logger.Debug($"Failed to add node {node}. Bucket at depth {depth} is full");
                return resp;
            }

            bool goRight = GetBit(nodeHash, depth);
            _logger.Debug($"Traversing {(goRight ? "right" : "left")} at depth {depth}");

            current = goRight ? current.Right! : current.Left!;
            depth++;
        }
    }

    private KBucket<TNode> GetBucketForHash(ValueHash256 nodeHash)
    {
        TreeNode current = _root;
        int depth = 0;
        while (true)
        {
            if (current.IsLeaf)
            {
                _logger.Debug($"Reached leaf node at depth {depth}");
                return current.Bucket;
            }

            bool goRight = GetBit(nodeHash, depth);
            _logger.Debug($"Traversing {(goRight ? "right" : "left")} at depth {depth}");

            current = goRight ? current.Right! : current.Left!;
            depth++;
        }
    }

    private bool ShouldSplit(int depth, int targetLogDistance)
    {
        bool shouldSplit = depth < 256 && targetLogDistance + _b >= depth;
        _logger.Debug($"ShouldSplit at depth {depth}: {shouldSplit}");
        return shouldSplit;
    }

    private void SplitBucket(int depth, TreeNode node)
    {
        node.Left = new TreeNode(_k, node.Prefix);
        var rightPrefixBytes = node.Prefix.Bytes.ToArray();
        rightPrefixBytes[depth / 8] |= (byte)(1 << (7 - (depth % 8)));
        node.Right = new TreeNode(_k, new ValueHash256(rightPrefixBytes));

        _logger.Debug($"Created children at depth {depth + 1}");

        foreach (var item in node.Bucket.GetAll())
        {
            ValueHash256 itemHash = _nodeHashProvider.GetHash(item);
            TreeNode? targetNode = GetBit(itemHash, depth) ? node.Right : node.Left;
            targetNode.Bucket.TryAddOrRefresh(itemHash, item, out _);
            _logger.Debug($"Moved item {item} to {(GetBit(itemHash, depth) ? "right" : "left")} child");
        }

        node.Bucket.Clear();
        _logger.Debug($"Finished splitting bucket. Left count: {node.Left.Bucket.Count}, Right count: {node.Right.Bucket.Count}");
    }

    public void Remove(in ValueHash256 nodeHash)
    {
        using McsLock.Disposable _ = _lock.Acquire();

        _logger.Debug($"Attempting to remove node {nodeHash} with hash {nodeHash}");
        RemoveRecursive(_root, 0, nodeHash);
    }

    private void RemoveRecursive(TreeNode node, int depth, ValueHash256 nodeHash)
    {
        if (node.IsLeaf)
        {
            _logger.Debug($"Removing node {nodeHash} from bucket at depth {depth}");
            node.Bucket.RemoveAndReplace(nodeHash);
            return;
        }

        bool goRight = GetBit(nodeHash, depth);
        _logger.Debug($"Traversing {(goRight ? "right" : "left")} at depth {depth}");
        RemoveRecursive(goRight ? node.Right! : node.Left!, depth + 1, nodeHash);
    }

    public TNode[] GetAllAtDistance(int distance)
    {
        using McsLock.Disposable _ = _lock.Acquire();

        _logger.Debug($"Getting all nodes at distance {distance}");
        List<TNode> result = new List<TNode>();
        GetAllAtDistanceRecursive(_root, 0, distance, result);
        _logger.Debug($"Found {result.Count} nodes at distance {distance}");
        return result.ToArray();
    }

    public IEnumerable<ValueHash256> IterateBucketRandomHashes()
    {
        using McsLock.Disposable _ = _lock.Acquire();

        // Well, it need to ToArray, otherwise the lock does not really do anything.
        return DoIterateBucketRandomHashes(_root, 0).ToArray();
    }

    private IEnumerable<ValueHash256> DoIterateBucketRandomHashes(TreeNode node, int depth)
    {
        if (node.IsLeaf)
        {
            yield return Hash256XORUtils.GetRandomHashAtDistance(_currentNodeHash, depth);
        }
        else
        {
            foreach (ValueHash256 bucketHash in DoIterateBucketRandomHashes(node.Left!, depth + 1))
            {
                yield return bucketHash;
            }

            foreach (ValueHash256 bucketHash in DoIterateBucketRandomHashes(node.Right!, depth + 1))
            {
                yield return bucketHash;
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

    public TNode[] GetKNearestNeighbour(ValueHash256 hash, ValueHash256? exclude)
    {
        using McsLock.Disposable _ = _lock.Acquire();

        KBucket<TNode> firstBucket = GetBucketForHash(hash);
        if (exclude == null || !firstBucket.ContainsNode(exclude.Value))
        {
            TNode[] nodes = firstBucket.GetAll();
            if (nodes.Length == _k)
            {
                // Fast path. In theory, most of the time, this would be the taken path, where no array
                // concatenation or creation is needed.
                return nodes;
            }
        }

        if (exclude == null)
        {
            return IterateNeighbour(hash)
                .Select(kv => kv.Item2)
                .ToArray();
        }

        return IterateNeighbour(hash)
            .Where(kv => kv.Item1 != exclude.Value)
            .Select(kv => kv.Item2).ToArray();
    }

    private void GetAllAtDistanceRecursive(TreeNode node, int depth, int remainingDistance, List<TNode> result)
    {
        if (node.IsLeaf)
        {
            if (remainingDistance == 0)
            {
                _logger.Debug($"Adding {node.Bucket.Count} nodes from bucket at depth {depth}");
                result.AddRange(node.Bucket.GetAll());
            }
            return;
        }

        if (remainingDistance > 0)
        {
            GetAllAtDistanceRecursive(node.Left!, depth + 1, remainingDistance - 1, result);
            GetAllAtDistanceRecursive(node.Right!, depth + 1, remainingDistance - 1, result);
        }
        else
        {
            GetAllAtDistanceRecursive(node.Left!, depth + 1, 0, result);
            GetAllAtDistanceRecursive(node.Right!, depth + 1, 0, result);
        }
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
        LogTreeStructureRecursive(node.Left!, indent, false, depth+1, sb);
        LogTreeStructureRecursive(node.Right!, indent, true, depth+1, sb);
    }

    public void LogTreeStatistics()
    {
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

        _logger.Info($"Tree Statistics:\n" +
                     $"Total Nodes: {totalNodes}\n" +
                     $"Total Buckets: {totalBuckets}\n" +
                     $"Max Depth: {maxDepth}\n" +
                     $"Total Items: {totalItems}\n" +
                     $"Average Items per Bucket: {(double)totalItems / totalBuckets:F2}");
    }
    public void LogTreeStructure()
    {
        StringBuilder sb = new StringBuilder();
        LogTreeStructureRecursive(_root, "", true, 0, sb);
        _logger.Info($"Current Tree Structure:\n{sb}");
    }
}
