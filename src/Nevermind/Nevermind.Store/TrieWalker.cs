using System;
using System.Collections.Generic;
using System.Diagnostics;
using Nevermind.Core.Encoding;
using Nevermind.Core.Sugar;

namespace Nevermind.Store
{
    public class TreeUpdate
    {
        private readonly PatriciaTree _tree;
        private readonly byte[] _updateKey;
        private readonly byte[] _updateValue;

        private readonly Stack<StackedNode> _nodeStack = new Stack<StackedNode>();

        [DebuggerStepThrough]
        public TreeUpdate(PatriciaTree tree, byte[] updateKey, byte[] updateValue)
        {
            _tree = tree;
            _updateKey = updateKey;
            _updateValue = updateValue;
        }

        private int _currentIndex;

        public void Run()
        {
            if (_tree.Root == null)
            {
                LeafNode leafNode = new LeafNode(_updateKey, _updateValue);
                _tree.StoreNode(leafNode, true);
                return;
            }

            TraverseNode(_tree.Root);
        }

        private void TraverseNode(Node node)
        {
            LeafNode leaf = node as LeafNode;
            if (leaf != null)
            {
                TraverseLeaf(leaf);
            }

            BranchNode branch = node as BranchNode;
            if (branch != null)
            {
                TraverseBranch(branch);
            }

            ExtensionNode extension = node as ExtensionNode;
            if (extension != null)
            {
                TraverseExtension(extension);
            }
        }

        // done?
        private void TraverseLeaf(LeafNode node)
        {
            BranchNode branch = null;
            ExtensionNode extension = null;
            LeafNode leaf = null;
            KeccakOrRlp newHash = null;
            bool newNodeCreated = false;

            for (int i = 0; i < node.Key.Length; i++, _currentIndex++)
            {
                if (node.Key[i] != _updateKey[_currentIndex])
                {
                    branch = new BranchNode();

                    byte[] oldLeafKey = new HexPrefix(true, new byte[node.Key.Length - i]).ToBytes();
                    byte[] newLeafKey = new HexPrefix(true, new byte[_updateKey.Length - _currentIndex]).ToBytes();
                    LeafNode oldLeaf = new LeafNode(oldLeafKey, node.Value);
                    LeafNode newLeaf = new LeafNode(newLeafKey, _updateValue);
                    
                    branch.Nodes[node.Key[i]] = _tree.StoreNode(oldLeaf);
                    branch.Nodes[_updateKey[_currentIndex]] = _tree.StoreNode(newLeaf);
                    newHash = _tree.StoreNode(branch);

                    if (i != 0)
                    {
                        byte[] extensionKey = new byte[i];
                        Buffer.BlockCopy(node.Key, 0, extensionKey, 0, i);
                        extension = new ExtensionNode();
                        extension.NextNode = newHash;
                        extension.Key = new HexPrefix(false, extensionKey).ToBytes();
                        newHash = _tree.StoreNode(extension);
                    }

                    newNodeCreated = true;
                    break;
                }
            }

            if (!newNodeCreated && !Bytes.UnsafeCompare(node.Value, _updateValue))
            {
                leaf = new LeafNode(_updateKey, _updateValue);
                newHash = _tree.StoreNode(leaf);
                newNodeCreated = true;
            }

            if (newNodeCreated)
            {
                UpdateHashes(extension ?? branch ?? (Node) leaf, newHash);
            }
        }

        private class StackedNode
        {
            public StackedNode(Node node, int pathIndex)
            {
                Node = node;
                PathIndex = pathIndex;
            }

            public Node Node { get; }
            public int PathIndex { get; }
        }

        private void UpdateHashes(Node node, KeccakOrRlp hash)
        {
            // nodes should immutable here I guess
            bool isRoot = _nodeStack.Count == 0;
            if (isRoot)
            {
                _tree.RootHash = hash.GetKeccakOrComputeFromRlp();
                _tree.Root = node;
                return;
            }

            while (!isRoot)
            {
                StackedNode parentOnStack = _nodeStack.Pop();
                node = parentOnStack.Node;

                isRoot = _nodeStack.Count == 0;

                LeafNode leaf = node as LeafNode;
                if (leaf != null)
                {
                    throw new InvalidOperationException($"Leaf {leaf} cannot be a parent of {hash}");
                }

                BranchNode branch = node as BranchNode;
                if (branch != null)
                {
                    branch.Nodes[parentOnStack.PathIndex] = hash;
                    hash = _tree.StoreNode(branch, isRoot);
                }
                else
                {
                    ExtensionNode extension = node as ExtensionNode;
                    if (extension != null)
                    {
                        extension.NextNode = hash;
                        hash = _tree.StoreNode(extension, isRoot);
                    }
                    else
                    {
                        throw new InvalidOperationException($"Unknown node type {node.GetType().Name}");
                    }
                }
            }
        }

        private void TraverseBranch(BranchNode node)
        {
        }

        private void TraverseExtension(ExtensionNode node)
        {
        }
    }
}