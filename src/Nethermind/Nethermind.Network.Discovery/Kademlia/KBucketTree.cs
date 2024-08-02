// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Network.Discovery.Kademlia;

public class KBucketTree<TNode, TContentKey> where TNode : notnull
{
    private class TreeNode
    {
        public KBucket<TNode> Bucket { get; }
        public TreeNode? Left { get; set; }
        public TreeNode? Right { get; set; }
        public int Depth { get; }

        public TreeNode(int depth, int k)
        {
            Bucket = new KBucket<TNode>(k);
            Depth = depth;
        }
    }

    private readonly TreeNode _root;
    private readonly int _k;
    private readonly int _maxDepth;
    
    private readonly INodeHashProvider<TNode, TContentKey> _nodeHashProvider;

    public KBucketTree(int k, int maxDepth, INodeHashProvider<TNode, TContentKey> nodeHashProvider)
    {
        _k = k;
        _maxDepth = maxDepth;
        _nodeHashProvider = nodeHashProvider;
        _root = new TreeNode(0, k);
    }

    public bool TryAddOrRefresh(TNode node, out TNode? toRefresh)
    {
        ValueHash256 nodeHash = _nodeHashProvider.GetHash(node);
        TreeNode current = _root;

        for (int i = 0; i < _maxDepth; i++)
        {
            bool goRight = GetBit(nodeHash, i);
            if (goRight)
            {
                current.Right ??= new TreeNode(current.Depth + 1, _k);
                current = current.Right;
            }
            else
            {
                current.Left ??= new TreeNode(current.Depth + 1, _k);
                current = current.Left;
            }

            if (current.Bucket.TryAddOrRefresh(node, out toRefresh))
            {   
                Console.WriteLine($"Added/refreshed node {node} at depth {current.Depth}");
                return true;
            }

            if (current.Depth == _maxDepth - 1)
            {
                Console.WriteLine($"Failed to add node {node} at max depth {_maxDepth}");
                return false;
            }
        }

        toRefresh = default;
        return false;
    }

    public TNode[] GetAllAtDistance(int distance)
    {
        List<TNode> result = new List<TNode>();
        GetAllAtDistanceRecursive(_root, distance, 0, result);
        return result.ToArray();
    }

    private void GetAllAtDistanceRecursive(TreeNode? node, int targetDistance, int currentDistance, List<TNode> result)
    {
        if (node == null) return;

        if (currentDistance == targetDistance)
        {
            result.AddRange(node.Bucket.GetAll());
            return;
        }

        GetAllAtDistanceRecursive(node.Left, targetDistance, currentDistance + 1, result);
        GetAllAtDistanceRecursive(node.Right, targetDistance, currentDistance + 1, result);
    }

    public void Remove(TNode node)
    {
        ValueHash256 nodeHash = _nodeHashProvider.GetHash(node);
        TreeNode current = _root;

        for (int i = 0; i < _maxDepth; i++)
        {
            bool goRight = GetBit(nodeHash, i);
            if (goRight)
            {
                if (current.Right == null) return;
                current = current.Right;
            }
            else
            {
                if (current.Left == null) return;
                current = current.Left;
            }

            current.Bucket.Remove(node);

            if (current.Depth == _maxDepth - 1)
            {
                return;
            }
        }
    }

    private bool GetBit(ValueHash256 hash, int index)
    {
        int byteIndex = index / 8;
        int bitIndex = index % 8;
        return (hash.Bytes[byteIndex] & (1 << (7 - bitIndex))) != 0;
    }

}