using System;
using System.Collections.Generic;
using System.Diagnostics;
using Nevermind.Core.Encoding;
using Nevermind.Core.Sugar;

namespace Nevermind.Store
{
    /// <summary>
    /// Remember:
    ///   extension always points at a branch
    /// </summary>
    public class TreeUpdate
    {
        private readonly PatriciaTree _tree;
        private readonly byte[] _updatePath;
        private readonly byte[] _updateValue;

        private readonly Stack<StackedNode> _nodeStack = new Stack<StackedNode>();

        [DebuggerStepThrough]
        public TreeUpdate(PatriciaTree tree, byte[] updatePath, byte[] updateValue)
        {
            _tree = tree;
            _updatePath = Nibbles.FromBytes(updatePath);
            _updateValue = updateValue;
        }

        private int _currentIndex;

        public void Run()
        {
            if (_tree.Root == null)
            {
                LeafNode leafNode = new LeafNode(new HexPrefix(true, _updatePath), _updateValue);
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
                return;
            }

            BranchNode branch = node as BranchNode;
            if (branch != null)
            {
                TraverseBranch(branch);
                return;
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
            LeafNode leaf = null;

            if (node.Path.Length == 0)
            {
                byte[] newLeafPath = new byte[_updatePath.Length - _currentIndex];

                Buffer.BlockCopy(_updatePath, _currentIndex, newLeafPath, 0, newLeafPath.Length);

                HexPrefix newLeafKey = new HexPrefix(true, newLeafPath);
                leaf = new LeafNode(newLeafKey, _updateValue);

                BranchNode branch = new BranchNode();
                branch.Value = node.Value;

                _nodeStack.Push(new StackedNode(branch, _updatePath[_currentIndex]));
            }
            else
            {
                for (int i = 0; i < node.Path.Length; i++, _currentIndex++)
                {
                    if (node.Path[i] != _updatePath[_currentIndex])
                    {
                        byte[] oldLeafPath = new byte[node.Path.Length - i - 1];
                        byte[] newLeafPath = new byte[_updatePath.Length - _currentIndex - 1];

                        Buffer.BlockCopy(node.Path, i + 1, oldLeafPath, 0, oldLeafPath.Length);
                        Buffer.BlockCopy(_updatePath, _currentIndex + 1, newLeafPath, 0, newLeafPath.Length);

                        HexPrefix oldLeafKey = new HexPrefix(true, oldLeafPath);
                        HexPrefix newLeafKey = new HexPrefix(true, newLeafPath);

                        LeafNode oldLeaf = new LeafNode(oldLeafKey, node.Value);
                        leaf = new LeafNode(newLeafKey, _updateValue);

                        BranchNode branch = new BranchNode();
                        branch.Nodes[node.Path[i]] = _tree.StoreNode(oldLeaf);

                        if (i != 0)
                        {
                            byte[] extensionPath = new byte[i];
                            Buffer.BlockCopy(node.Path, 0, extensionPath, 0, i);
                            ExtensionNode extension = new ExtensionNode();
                            extension.Key = new HexPrefix(false, extensionPath);
                            _nodeStack.Push(new StackedNode(extension, 0));
                        }

                        _nodeStack.Push(new StackedNode(branch, _updatePath[_currentIndex]));
                        break;
                    }
                }
            }

            if (leaf == null && !Bytes.UnsafeCompare(node.Value, _updateValue))
            {
                // only remaining path here
                leaf = new LeafNode(new HexPrefix(true, _updatePath), _updateValue);
            }

            if (leaf != null)
            {
                UpdateHashes(leaf);
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

        private void UpdateHashes(Node node)
        {
            Debug.Assert((bool)(node is LeafNode), "Can only update hashes starting from a leaf");

            KeccakOrRlp hash = _tree.StoreNode(node);

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
            KeccakOrRlp nextHash = node.Nodes[_updatePath[_currentIndex]];
            if (nextHash == null)
            {
                byte[] leafPath = new byte[_updatePath.Length - 1];
                Buffer.BlockCopy(_updatePath, 1, leafPath, 0, leafPath.Length);

                LeafNode leaf = new LeafNode(new HexPrefix(true, leafPath), _updateValue);
                UpdateHashes(leaf);
            }
            else
            {
                _nodeStack.Push(new StackedNode(node, _updatePath[_currentIndex]));
                _currentIndex++;
                Node nextNode = _tree.GetNode(nextHash);
                TraverseNode(nextNode);
            }
        }

        // similar to leaf.. (can refactor?)
        private void TraverseExtension(ExtensionNode node)
        {
            LeafNode leaf = null;

            for (int i = 0; i < node.Path.Length; i++, _currentIndex++)
            {
                if (node.Path[i] != _updatePath[_currentIndex])
                {
                    byte[] oldExtensionPath = new byte[node.Path.Length - i];
                    byte[] newLeafPath = new byte[_updatePath.Length - _currentIndex];

                    HexPrefix oldExtensionKey = new HexPrefix(false, oldExtensionPath);
                    HexPrefix newLeafKey = new HexPrefix(true, newLeafPath);

                    ExtensionNode oldExtension = new ExtensionNode(oldExtensionKey, node.NextNode);
                    // may need to replace with branch
                    leaf = new LeafNode(newLeafKey, _updateValue);

                    BranchNode branch = new BranchNode();
                    branch.Nodes[node.Path[i]] = _tree.StoreNode(oldExtension);

                    if (i != 0)
                    {
                        byte[] extensionPath = new byte[i];
                        Buffer.BlockCopy(node.Path, 0, extensionPath, 0, i);
                        ExtensionNode extension = new ExtensionNode();
                        extension.Key = new HexPrefix(false, extensionPath);
                        _nodeStack.Push(new StackedNode(extension, 0));
                    }

                    _nodeStack.Push(new StackedNode(branch, _updatePath[_currentIndex]));
                    break;
                }
            }

            if (leaf == null)
            {
                _nodeStack.Push(new StackedNode(node, 0));
                Node nextNode = _tree.GetNode(node.NextNode);
                TraverseNode(nextNode);
            }
            else
            {
                UpdateHashes(leaf);
            }
        }
    }
}