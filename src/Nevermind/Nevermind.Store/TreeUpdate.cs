using System;
using System.Collections.Generic;
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

        private byte[] RemainingUpdatePath
        {
            get
            {
                byte[] remaining = new byte[_updatePath.Length - _currentIndex];
                Buffer.BlockCopy(_updatePath, _currentIndex, remaining, 0, remaining.Length);
                return remaining;
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
            Keccak previousRootHash = _tree.RootHash;

            // actually can do that from a branch...
            ////Debug.Assert((bool)(node is LeafNode), "Can only update hashes starting from a leaf");

            bool isRoot = _nodeStack.Count == 0;
            KeccakOrRlp hash = _tree.StoreNode(node, isRoot);

            // nodes should immutable here I guess
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
                    _tree.DeleteNode(branch.Nodes[parentOnStack.PathIndex]);
                    branch.Nodes[parentOnStack.PathIndex] = hash;
                    hash = _tree.StoreNode(branch, isRoot);
                }
                else
                {
                    ExtensionNode extension = node as ExtensionNode;
                    if (extension != null)
                    {
                        _tree.DeleteNode(extension.NextNode);
                        extension.NextNode = hash;
                        hash = _tree.StoreNode(extension, isRoot);
                    }
                    else
                    {
                        throw new InvalidOperationException($"Unknown node type {node.GetType().Name}");
                    }
                }
            }

            _tree.DeleteNode(new KeccakOrRlp(previousRootHash), true);
        }

        private void TraverseBranch(BranchNode node)
        {
            KeccakOrRlp nextHash = node.Nodes[_updatePath[_currentIndex]];
            _nodeStack.Push(new StackedNode(node, _updatePath[_currentIndex]));
            _currentIndex++;

            if (nextHash == null)
            {
                byte[] leafPath = _updatePath.Slice(_currentIndex, _updatePath.Length - _currentIndex);
                LeafNode leaf = new LeafNode(new HexPrefix(true, leafPath), _updateValue);
                UpdateHashes(leaf);
            }
            else
            {
                Node nextNode = _tree.GetNode(nextHash);
                TraverseNode(nextNode);
            }
        }

        // done?
        private void TraverseLeaf(LeafNode node)
        {
            (byte[] shorterPath, byte[] longerPath) = RemainingUpdatePath.Length - node.Path.Length < 0
                ? (RemainingUpdatePath, node.Path)
                : (node.Path, RemainingUpdatePath);

            byte[] shorterPathValue;
            byte[] longerPathValue;

            if (Bytes.UnsafeCompare(shorterPath, node.Path))
            {
                shorterPathValue = node.Value;
                longerPathValue = _updateValue;
            }
            else
            {
                shorterPathValue = _updateValue;
                longerPathValue = node.Value;
            }

            int extensionLength = 0;
            for (int i = 0; i < Math.Min(shorterPath.Length, longerPath.Length) && shorterPath[i] == longerPath[i]; i++, extensionLength++)
            {
            }

            if (extensionLength == shorterPath.Length && extensionLength == longerPath.Length)
            {
                if (!Bytes.UnsafeCompare(node.Value, _updateValue))
                {
                    // only remaining path here
                    LeafNode newLeaf = new LeafNode(new HexPrefix(true, _updatePath), _updateValue);
                    UpdateHashes(newLeaf);
                    return;
                }
            }

            if (extensionLength != 0)
            {
                ExtensionNode extension = new ExtensionNode();
                byte[] extensionPath = longerPath.Slice(0, extensionLength);
                extension.Key = new HexPrefix(false, extensionPath);
                _nodeStack.Push(new StackedNode(extension, 0));
            }

            BranchNode branch = new BranchNode();
            if (extensionLength == shorterPath.Length)
            {
                branch.Value = shorterPathValue;
            }
            else
            {
                byte[] shortLeafPath = shorterPath.Slice(extensionLength + 1, shorterPath.Length - extensionLength - 1);
                LeafNode shortLeaf = new LeafNode(new HexPrefix(true, shortLeafPath), shorterPathValue);
                branch.Nodes[shorterPath[extensionLength]] = _tree.StoreNode(shortLeaf);
            }

            byte[] leafPath = longerPath.Slice(extensionLength + 1, longerPath.Length - extensionLength - 1);
            LeafNode leaf = new LeafNode(new HexPrefix(true, leafPath), longerPathValue);
            _nodeStack.Push(new StackedNode(branch, longerPath[extensionLength]));
            UpdateHashes(leaf);
        }

        private void TraverseExtension(ExtensionNode node)
        {
            int extensionLength = 0;
            for (int i = 0; i < Math.Min(RemainingUpdatePath.Length, node.Path.Length) && RemainingUpdatePath[i] == node.Path[i]; i++, extensionLength++)
            {
            }

            if (extensionLength == node.Path.Length)
            {
                _currentIndex += extensionLength;
                _nodeStack.Push(new StackedNode(node, 0));
                Node nextNode = _tree.GetNode(node.NextNode);
                TraverseNode(nextNode);
                return;
            }

            if (extensionLength != 0)
            {
                ExtensionNode extension = new ExtensionNode();
                byte[] extensionPath = node.Path.Slice(0, extensionLength);
                extension.Key = new HexPrefix(false, extensionPath);
                _nodeStack.Push(new StackedNode(extension, 0));
            }

            BranchNode branch = new BranchNode();
            if (extensionLength == RemainingUpdatePath.Length)
            {
                branch.Value = _updateValue;
            }
            else
            {
                byte[] path = RemainingUpdatePath.Slice(extensionLength + 1, RemainingUpdatePath.Length - extensionLength - 1);
                LeafNode shortLeaf = new LeafNode(new HexPrefix(true, path), _updateValue);
                branch.Nodes[RemainingUpdatePath[extensionLength]] = _tree.StoreNode(shortLeaf);
            }

            if (node.Path.Length - extensionLength > 1)
            {
                // create extension
                byte[] extensionPath = node.Path.Slice(extensionLength + 1, node.Path.Length - extensionLength - 1);
                ExtensionNode secondExtension = new ExtensionNode(new HexPrefix(false, extensionPath), node.NextNode);
                branch.Nodes[node.Path[extensionLength]] = _tree.StoreNode(secondExtension);
            }
            else
            {
                branch.Nodes[node.Path[extensionLength]] = node.NextNode;
            }

            UpdateHashes(branch);
        }
    }
}