// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Network.Discovery.Kademlia;

public class KBucketTree<TNode, TContentKey> where TNode : notnull
{
    private class TreeNode
    {
        public KBucket<TNode> Bucket { get; set; }
        public TreeNode? Left { get; set; }
        public TreeNode? Right { get; set; }
        public int Depth { get; }
        public ValueHash256 Prefix { get; }
        

        public TreeNode(int depth, int k, ValueHash256 prefix)
        {
            Bucket = new KBucket<TNode>(k);
            Depth = depth;
            Prefix = prefix;
        }
    }

    private readonly TreeNode _root;
    private readonly int _k;
    private readonly ValueHash256 _currentNodeId;
    private readonly INodeHashProvider<TNode, TContentKey> _nodeHashProvider;

    public KBucketTree(int k, ValueHash256 currentNodeId, INodeHashProvider<TNode, TContentKey> nodeHashProvider)
    {
        _k = k;
        _currentNodeId = currentNodeId;
        _nodeHashProvider = nodeHashProvider;
        _root = new TreeNode(0, k, new ValueHash256());
    }

    public bool TryAddOrRefresh(TNode node, out TNode? toRefresh)
    {
        ValueHash256 nodeHash = _nodeHashProvider.GetHash(node);
        TreeNode current = _root;

        while (true)
        {
            if (current.Bucket.TryAddOrRefresh(node, out toRefresh))
            {
                return true;
            }

            if (ShouldSplit(current, nodeHash))
            {
                SplitBucket(current, nodeHash);
                continue;
            }

            return false;
        }
    }

    private bool ShouldSplit(TreeNode node, ValueHash256 nodeHash)
    {
        return node.Bucket.Count >= _k && 
               IsInRange(nodeHash, node.Prefix, node.Depth) && 
               IsInRange(_currentNodeId, node.Prefix, node.Depth);
    }

    private void SplitBucket(TreeNode node, ValueHash256 nodeHash)
    {
    var leftPrefix = new ValueHash256(node.Prefix.Bytes);
    var rightPrefix = new ValueHash256(node.Prefix.Bytes);
    var rightPrefixBytes = rightPrefix.Bytes.ToArray(); // Create a copy
    rightPrefixBytes[node.Depth / 8] |= (byte)(1 << (7 - (node.Depth % 8)));
    rightPrefix = new ValueHash256(rightPrefixBytes);

    node.Left = new TreeNode(node.Depth + 1, _k, leftPrefix);
    node.Right = new TreeNode(node.Depth + 1, _k, rightPrefix);

    foreach (var item in node.Bucket.GetAll())
    {
        ValueHash256 itemHash = _nodeHashProvider.GetHash(item);
        (GetBit(itemHash, node.Depth) ? node.Right : node.Left).Bucket.TryAddOrRefresh(item, out _);
    }

    node.Bucket = new KBucket<TNode>(_k);
    }

    public TNode[] GetAllAtDistance(int distance)
    {
        List<TNode> result = new List<TNode>();
        GetAllAtDistanceRecursive(_root, distance, result);
        return result.ToArray();
    }

    private void GetAllAtDistanceRecursive(TreeNode node, int remainingDistance, List<TNode> result)
    {
        if (remainingDistance == 0)
        {
            result.AddRange(node.Bucket.GetAll());
            return;
        }

        if (node.Left != null)
            GetAllAtDistanceRecursive(node.Left, remainingDistance - 1, result);
        if (node.Right != null)
            GetAllAtDistanceRecursive(node.Right, remainingDistance - 1, result);
    }

    public void Remove(TNode node)
    {
        ValueHash256 nodeHash = _nodeHashProvider.GetHash(node);
        RemoveRecursive(_root, node, nodeHash, 0);
    }

    private void RemoveRecursive(TreeNode node, TNode toRemove, ValueHash256 nodeHash, int depth)
    {
        node.Bucket.Remove(toRemove);

        if (node.Left == null && node.Right == null)
            return;

        bool goRight = GetBit(nodeHash, depth);
        if (goRight && node.Right != null)
            RemoveRecursive(node.Right, toRemove, nodeHash, depth + 1);
        else if (!goRight && node.Left != null)
            RemoveRecursive(node.Left, toRemove, nodeHash, depth + 1);
    }

    private bool IsInRange(ValueHash256 hash, ValueHash256 prefix, int depth)
    {
        for (int i = 0; i < depth; i++)
        {
            if (GetBit(hash, i) != GetBit(prefix, i))
                return false;
        }
        return true;
    }

    private bool GetBit(ValueHash256 hash, int index)
    {
        int byteIndex = index / 8;
        int bitIndex = index % 8;
        return (hash.Bytes[byteIndex] & (1 << (7 - bitIndex))) != 0;
    }
}