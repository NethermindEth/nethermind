using System;
using System.Collections.Generic;
using System.Diagnostics;
using Nevermind.Core;
using Nevermind.Core.Encoding;
using Nevermind.Core.Sugar;

namespace Nevermind.Store
{
    public class TreeUpdate
    {
        private readonly PatriciaTree _tree;
        private readonly byte[] _updatePath;
        private readonly byte[] _updateValue;

        private readonly Stack<StackedNode> _nodeStack = new Stack<StackedNode>();

        public TreeUpdate(PatriciaTree tree, Nibble[] updatePath, byte[] updateValue)
        {
            _tree = tree;
            _updatePath = updatePath.ToLooseByteArray();
            _updateValue = updateValue.Length == 0 ? null : updateValue;
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
            if (node is LeafNode leaf)
            {
                TraverseLeaf(leaf);
                return;
            }

            if (node is BranchNode branch)
            {
                TraverseBranch(branch);
                return;
            }

            if (node is ExtensionNode extension)
            {
                TraverseExtension(extension);
            }
        }

        private int RemainingUpdatePathLength => _updatePath.Length - _currentIndex;

        private byte[] RemainingUpdatePath => _updatePath.Slice(_currentIndex, RemainingUpdatePathLength);

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

            bool isRoot = _nodeStack.Count == 0;
            KeccakOrRlp nextNodeHash = _tree.StoreNode(node, isRoot);
            Node nextNode = node;

            // nodes should immutable here I guess
            while (!isRoot)
            {
                StackedNode parentOnStack = _nodeStack.Pop();
                node = parentOnStack.Node;

                isRoot = _nodeStack.Count == 0;

                if (node is LeafNode leaf)
                {
                    throw new InvalidOperationException($"Leaf {leaf} cannot be a parent of {nextNodeHash}");
                }

                if (node is BranchNode branch)
                {
                    _tree.DeleteNode(branch.Nodes[parentOnStack.PathIndex], true);
                    branch.Nodes[parentOnStack.PathIndex] = nextNodeHash;
                    if (branch.IsValid)
                    {
                        nextNodeHash = _tree.StoreNode(branch, isRoot);
                        nextNode = branch;
                    }
                    else
                    {
                        if (branch.Value.Length != 0)
                        {
                            LeafNode leafFromBranch = new LeafNode(new HexPrefix(true), branch.Value);
                            nextNodeHash = _tree.StoreNode(leafFromBranch, isRoot);
                            nextNode = leafFromBranch;
                        }
                        else
                        {
                            int childNodeIndex = Array.FindIndex(branch.Nodes, n => n != null);
                            KeccakOrRlp childNodeHash = branch.Nodes[childNodeIndex];
                            Debug.Assert(childNodeHash != null, "Before updating branch should have had at least two non-empty children");
                            // need to restore this node now?
                            Node childNode = _tree.GetNode(childNodeHash);
                            if (childNode is BranchNode)
                            {
                                ExtensionNode extensionFromBranch = new ExtensionNode(new HexPrefix(false, (byte)childNodeIndex), childNodeHash);
                                nextNodeHash = _tree.StoreNode(extensionFromBranch, isRoot);
                                nextNode = extensionFromBranch;
                            }
                            else if (childNode is ExtensionNode childExtension)
                            {
                                _tree.DeleteNode(childNodeHash, true);
                                ExtensionNode extensionFromBranch = new ExtensionNode(new HexPrefix(false, Bytes.Merge((byte)childNodeIndex, childExtension.Path)), childExtension.NextNode);
                                nextNodeHash = _tree.StoreNode(extensionFromBranch, isRoot);
                                nextNode = extensionFromBranch;
                            }
                            else if (childNode is LeafNode childLeaf)
                            {
                                _tree.DeleteNode(childNodeHash, true);
                                LeafNode leafFromBranch = new LeafNode(new HexPrefix(true, Bytes.Merge((byte)childNodeIndex, childLeaf.Path)), childLeaf.Value);
                                nextNodeHash = _tree.StoreNode(leafFromBranch, isRoot);
                                nextNode = leafFromBranch;
                            }
                            else
                            {
                                throw new InvalidOperationException($"Unknown node type {nextNode.GetType().Name}");
                            }
                        }
                    }
                }
                else if (node is ExtensionNode extension)
                {
                    _tree.DeleteNode(extension.NextNode, true);
                    if (nextNode is LeafNode childLeaf)
                    {
                        LeafNode leafFromExtension = new LeafNode(new HexPrefix(true, Bytes.Merge(extension.Path, childLeaf.Path)), childLeaf.Value);
                        nextNodeHash = _tree.StoreNode(leafFromExtension, isRoot);
                        nextNode = leafFromExtension;
                    }
                    else if (nextNode is ExtensionNode childExtension)
                    {
                        ExtensionNode extensionFromExtension = new ExtensionNode(new HexPrefix(false, Bytes.Merge(extension.Path, childExtension.Path)), childExtension.NextNode);
                        nextNodeHash = _tree.StoreNode(extensionFromExtension, isRoot);
                        nextNode = extensionFromExtension;
                    }
                    else if (nextNode is BranchNode)
                    {
                        extension.NextNode = nextNodeHash;
                        nextNodeHash = _tree.StoreNode(extension, isRoot);
                        nextNode = extension;
                    }
                    else
                    {
                        throw new InvalidOperationException($"Unknown node type {nextNode.GetType().Name}");
                    }
                }
                else
                {
                    throw new InvalidOperationException($"Unknown node type {node.GetType().Name}");
                }
            }

            _tree.DeleteNode(new KeccakOrRlp(previousRootHash), true);
        }

        private void TraverseBranch(BranchNode node)
        {
            if (RemainingUpdatePathLength == 0)
            {
                if (_updateValue == null)
                {
                    UpdateHashes(null);
                }
                else
                {
                    node.Value = _updateValue;
                    UpdateHashes(node);
                }

                return;
            }

            KeccakOrRlp nextHash = node.Nodes[_updatePath[_currentIndex]];
            _nodeStack.Push(new StackedNode(node, _updatePath[_currentIndex]));
            _currentIndex++;

            if (nextHash == null)
            {
                if (_updateValue == null)
                {
                    throw new InvalidOperationException($"Could not find the leaf node to delete: {Hex.FromBytes(_updatePath, false)}");
                }

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
                if (_updateValue == null)
                {
                    UpdateHashes(null);
                    return;
                }

                if (!Bytes.UnsafeCompare(node.Value, _updateValue))
                {
                    LeafNode newLeaf = new LeafNode(new HexPrefix(true, RemainingUpdatePath), _updateValue);
                    UpdateHashes(newLeaf);
                    return;
                }
            }

            if (_updateValue == null)
            {
                throw new InvalidOperationException($"Could not find the leaf node to delete: {Hex.FromBytes(_updatePath, false)}");
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

            if (_updateValue == null)
            {
                throw new InvalidOperationException("Could find the leaf node to delete: {Hex.FromBytes(_updatePath, false)}");
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