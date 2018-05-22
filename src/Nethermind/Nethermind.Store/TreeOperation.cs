/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Extensions;

namespace Nethermind.Store
{
    public class TreeOperation
    {
        private readonly PatriciaTree _tree;
        private readonly byte[] _updatePath;
        private readonly byte[] _updateValue;
        private readonly bool _isUpdate;
        private readonly bool _ignoreMissingDelete;

        private readonly Stack<StackedNode> _nodeStack = new Stack<StackedNode>();

        public TreeOperation(PatriciaTree tree, byte[] looseByteArrayOfNibbles, byte[] updateValue, bool isUpdate, bool ignoreMissingDelete = true)
        {
            _tree = tree;
            _updatePath = looseByteArrayOfNibbles;
            if (isUpdate)
            {
                _updateValue = updateValue.Length == 0 ? null : updateValue;
            }

            _isUpdate = isUpdate;
            _ignoreMissingDelete = ignoreMissingDelete;
        }

        public TreeOperation(PatriciaTree tree, Nibble[] updatePath, byte[] updateValue, bool isUpdate, bool ignoreMissingDelete = true)
        {
            _tree = tree;
            _updatePath = updatePath.ToLooseByteArray();
            if (isUpdate)
            {
                _updateValue = updateValue.Length == 0 ? null : updateValue;
            }

            _isUpdate = isUpdate;
            _ignoreMissingDelete = ignoreMissingDelete;
        }

        private int _currentIndex;

        public byte[] Run()
        {
            if (_tree.RootRef == null)
            {
                if (!_isUpdate || _updateValue == null)
                {
                    return null;
                }

                Leaf leaf = new Leaf(new HexPrefix(true, _updatePath), _updateValue);
                leaf.IsDirty = true;
                _tree.RootRef = new NodeRef(leaf, true);
                return _updateValue;
            }

            _tree.RootRef.ResolveNode(_tree);
            return TraverseNode(_tree.RootRef.Node);
        }

        private byte[] TraverseNode(Node node)
        {
            if (node is Leaf leaf)
            {
                return TraverseLeaf(leaf);
            }

            if (node is Branch branch)
            {
                return TraverseBranch(branch);
            }

            if (node is Extension extension)
            {
                return TraverseExtension(extension);
            }

            throw new NotImplementedException($"Unknown node type {typeof(Node).Name}");
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

        // TODO: this can be removed now but is lower priority temporarily while the patricia rewrite testing is in progress
        private void ConnectNodes(Node node)
        {
//            Keccak previousRootHash = _tree.RootHash;

            bool isRoot = _nodeStack.Count == 0;
            NodeRef nextNodeRef = node == null ? null : new NodeRef(node, isRoot);
            Node nextNode = node;

            // nodes should immutable here I guess
            while (!isRoot)
            {
                StackedNode parentOnStack = _nodeStack.Pop();
                node = parentOnStack.Node;

                isRoot = _nodeStack.Count == 0;

                if (node is Leaf leaf)
                {
                    throw new InvalidOperationException($"{nameof(Leaf)} {leaf} cannot be a parent of {nextNodeRef}");
                }

                if (node is Branch branch)
                {
                    node.IsDirty = true;
//                    _tree.DeleteNode(branch.Nodes[parentOnStack.PathIndex], true);
                    branch.Nodes[parentOnStack.PathIndex] = nextNodeRef;
                    if (branch.IsValid)
                    {
                        nextNodeRef = new NodeRef(branch, isRoot);
                        nextNode = branch;
                    }
                    else
                    {
                        if (branch.Value.Length != 0)
                        {
                            Leaf leafFromBranch = new Leaf(new HexPrefix(true), branch.Value);
                            nextNodeRef = new NodeRef(leafFromBranch, isRoot);
                            nextNode = leafFromBranch;
                        }
                        else
                        {
                            int childNodeIndex = Array.FindIndex(branch.Nodes, n => n != null);
                            NodeRef childNodeRef = branch.Nodes[childNodeIndex];
                            if (childNodeRef == null)
                            {
                                throw new InvalidOperationException("Before updating branch should have had at least two non-empty children");
                            }

                            // need to restore this node now?
                            if (childNodeRef.Node == null)
                            {
                            }

                            childNodeRef.ResolveNode(_tree);
                            Node childNode = childNodeRef.Node;
                            if (childNode is Branch)
                            {
                                Extension extensionFromBranch = new Extension(new HexPrefix(false, (byte)childNodeIndex), childNodeRef);
                                extensionFromBranch.IsDirty = true;
                                nextNodeRef = new NodeRef(extensionFromBranch, isRoot);
                                nextNode = extensionFromBranch;
                            }
                            else if (childNode is Extension childExtension)
                            {
//                                _tree.DeleteNode(childNodeHash, true);
                                Extension extensionFromBranch = new Extension(new HexPrefix(false, Bytes.Concat((byte)childNodeIndex, childExtension.Path)), childExtension.NextNodeRef);
                                extensionFromBranch.IsDirty = true;
                                nextNodeRef = new NodeRef(extensionFromBranch, isRoot);
                                nextNode = extensionFromBranch;
                            }
                            else if (childNode is Leaf childLeaf)
                            {
//                                _tree.DeleteNode(childNodeHash, true);
                                Leaf leafFromBranch = new Leaf(new HexPrefix(true, Bytes.Concat((byte)childNodeIndex, childLeaf.Path)), childLeaf.Value);
                                leafFromBranch.IsDirty = true;
                                nextNodeRef = new NodeRef(leafFromBranch, isRoot);
                                nextNode = leafFromBranch;
                            }
                            else
                            {
                                throw new InvalidOperationException($"Unknown node type {nextNode.GetType().Name}");
                            }
                        }
                    }
                }
                else if (node is Extension extension)
                {
//                    _tree.DeleteNode(extension.NextNodeRef, true);
                    if (nextNode is Leaf childLeaf)
                    {
                        Leaf leafFromExtension = new Leaf(new HexPrefix(true, Bytes.Concat(extension.Path, childLeaf.Path)), childLeaf.Value);
                        leafFromExtension.IsDirty = true;
                        nextNodeRef = new NodeRef(leafFromExtension, isRoot);
                        nextNode = leafFromExtension;
                    }
                    else if (nextNode is Extension childExtension)
                    {
                        Extension extensionFromExtension = new Extension(new HexPrefix(false, Bytes.Concat(extension.Path, childExtension.Path)), childExtension.NextNodeRef);
                        extensionFromExtension.IsDirty = true;
                        nextNodeRef = new NodeRef(extensionFromExtension, isRoot);
                        nextNode = extensionFromExtension;
                    }
                    else if (nextNode is Branch)
                    {
                        // TODO: review modification of an existing node...
                        extension.NextNodeRef = nextNodeRef;
                        extension.IsDirty = true;
                        nextNodeRef = new NodeRef(extension, isRoot);
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

            if (!nextNodeRef?.IsRoot ?? false)
            {
                throw new InvalidOperationException("Non-root being made root");
            }

            _tree.RootRef = nextNodeRef;

//            _tree.DeleteNode(new KeccakOrRlp(previousRootHash), true);
        }

        private byte[] TraverseBranch(Branch node)
        {
            if (RemainingUpdatePathLength == 0)
            {
                if (!_isUpdate)
                {
                    return node.Value;
                }

                if (_updateValue == null)
                {
                    ConnectNodes(null);
                }
                else
                {
                    Branch newBranch = new Branch(node.Nodes, _updateValue);
                    newBranch.IsDirty = true;
                    ConnectNodes(newBranch);
                }

                return _updateValue;
            }

            NodeRef nextNodeRef = node.Nodes[_updatePath[_currentIndex]];
            _nodeStack.Push(new StackedNode(node, _updatePath[_currentIndex]));
            _currentIndex++;

            if (nextNodeRef == null)
            {
                if (!_isUpdate)
                {
                    return null;
                }

                if (_updateValue == null)
                {
                    if (_ignoreMissingDelete)
                    {
                        return null;
                    }

                    throw new InvalidOperationException($"Could not find the leaf node to delete: {Hex.FromBytes(_updatePath, false)}");
                }

                byte[] leafPath = _updatePath.Slice(_currentIndex, _updatePath.Length - _currentIndex);
                Leaf leaf = new Leaf(new HexPrefix(true, leafPath), _updateValue);
                leaf.IsDirty = true;
                ConnectNodes(leaf);

                return _updateValue;
            }

            nextNodeRef.ResolveNode(_tree);
            Node nextNode = nextNodeRef.Node;
            return TraverseNode(nextNode);
        }

        private byte[] TraverseLeaf(Leaf node)
        {
            byte[] remaining = RemainingUpdatePath;
            (byte[] shorterPath, byte[] longerPath) = remaining.Length - node.Path.Length < 0
                ? (remaining, node.Path)
                : (node.Path, remaining);

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
                if (!_isUpdate)
                {
                    return node.Value;
                }

                if (_updateValue == null)
                {
                    ConnectNodes(null);
                    return _updateValue;
                }

                if (!Bytes.UnsafeCompare(node.Value, _updateValue))
                {
                    Leaf newLeaf = new Leaf(new HexPrefix(true, remaining), _updateValue);
                    newLeaf.IsDirty = true;
                    ConnectNodes(newLeaf);
                    return _updateValue;
                }

                return _updateValue;
            }

            if (!_isUpdate)
            {
                return null;
            }

            if (_updateValue == null)
            {
                if (_ignoreMissingDelete)
                {
                    return null;
                }

                throw new InvalidOperationException($"Could not find the leaf node to delete: {Hex.FromBytes(_updatePath, false)}");
            }

            if (extensionLength != 0)
            {
                byte[] extensionPath = longerPath.Slice(0, extensionLength);
                Extension extension = new Extension(new HexPrefix(false, extensionPath));
                extension.IsDirty = true;
                _nodeStack.Push(new StackedNode(extension, 0));
            }

            Branch branch = new Branch();
            branch.IsDirty = true;
            if (extensionLength == shorterPath.Length)
            {
                branch.Value = shorterPathValue;
            }
            else
            {
                byte[] shortLeafPath = shorterPath.Slice(extensionLength + 1, shorterPath.Length - extensionLength - 1);
                Leaf shortLeaf = new Leaf(new HexPrefix(true, shortLeafPath), shorterPathValue);
                shortLeaf.IsDirty = true;
                branch.Nodes[shorterPath[extensionLength]] = new NodeRef(shortLeaf);
            }

            byte[] leafPath = longerPath.Slice(extensionLength + 1, longerPath.Length - extensionLength - 1);
            Leaf leaf = new Leaf(new HexPrefix(true, leafPath), longerPathValue);
            leaf.IsDirty = true;
            _nodeStack.Push(new StackedNode(branch, longerPath[extensionLength]));
            ConnectNodes(leaf);

            return _updateValue;
        }

        private byte[] TraverseExtension(Extension node)
        {
            byte[] remaining = RemainingUpdatePath;
            int extensionLength = 0;
            for (int i = 0; i < Math.Min(remaining.Length, node.Path.Length) && remaining[i] == node.Path[i]; i++, extensionLength++)
            {
            }

            if (extensionLength == node.Path.Length)
            {
                _currentIndex += extensionLength;
                _nodeStack.Push(new StackedNode(node, 0));
                node.NextNodeRef.ResolveNode(_tree);
                return TraverseNode(node.NextNodeRef.Node);
            }

            if (!_isUpdate)
            {
                return null;
            }

            if (_updateValue == null)
            {
                if (_ignoreMissingDelete)
                {
                    return null;
                }

                throw new InvalidOperationException("Could find the leaf node to delete: {Hex.FromBytes(_updatePath, false)}");
            }

            if (extensionLength != 0)
            {
                byte[] extensionPath = node.Path.Slice(0, extensionLength);
                Extension extension = new Extension(new HexPrefix(false, extensionPath));
                extension.IsDirty = true;
                _nodeStack.Push(new StackedNode(extension, 0));
            }

            Branch branch = new Branch();
            branch.IsDirty = true;
            if (extensionLength == remaining.Length)
            {
                branch.Value = _updateValue;
            }
            else
            {
                byte[] path = remaining.Slice(extensionLength + 1, remaining.Length - extensionLength - 1);
                Leaf shortLeaf = new Leaf(new HexPrefix(true, path), _updateValue);
                shortLeaf.IsDirty = true;
                branch.Nodes[remaining[extensionLength]] = new NodeRef(shortLeaf);
            }

            if (node.Path.Length - extensionLength > 1)
            {
                byte[] extensionPath = node.Path.Slice(extensionLength + 1, node.Path.Length - extensionLength - 1);
                Extension secondExtension = new Extension(new HexPrefix(false, extensionPath), node.NextNodeRef);
                secondExtension.IsDirty = true;
                branch.Nodes[node.Path[extensionLength]] = new NodeRef(secondExtension);
            }
            else
            {
                branch.Nodes[node.Path[extensionLength]] = node.NextNodeRef;
            }

            ConnectNodes(branch);
            return _updateValue;
        }
    }
}