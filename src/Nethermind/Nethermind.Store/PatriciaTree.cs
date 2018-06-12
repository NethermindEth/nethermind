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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;

namespace Nethermind.Store
{
    [DebuggerDisplay("{RootHash}")]
    public class PatriciaTree
    {
        private static readonly LruCache<Keccak, Rlp> NodeCache = new LruCache<Keccak, Rlp>(64 * 1024);
        private static readonly LruCache<byte[], byte[]> ValueCache = new LruCache<byte[], byte[]>(128 * 1024);

        /// <summary>
        ///     0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421
        /// </summary>
        public static readonly Keccak EmptyTreeHash = Keccak.EmptyTreeHash;

        /// <summary>
        /// Note at the moment this can be static because we never add to any two different Patricia trees in parallel
        /// THis would be receipts, transactions, state, storage - all of them are sequential so only on etree at the time uses NodeStack
        /// </summary>
        private static readonly Stack<StackedNode> NodeStack = new Stack<StackedNode>(); // TODO: if switching to parallel then need to pool tree operations with separate node stacks?, if...

        private static readonly ConcurrentQueue<Exception> CommitExceptions = new ConcurrentQueue<Exception>();

        private readonly IDb _db;
        private readonly bool _parallelizeBranches;

        private Keccak _rootHash;

        internal Node RootRef;

        public PatriciaTree()
            : this(NullDb.Instance, EmptyTreeHash, false)
        {
        }

        public PatriciaTree(IDb db, Keccak rootHash, bool parallelizeBranches)
        {
            _db = db;
            _parallelizeBranches = parallelizeBranches;
            RootHash = rootHash;
        }

        internal Node Root
        {
            get
            {
                RootRef?.ResolveNode(this); // TODO: needed?
                return RootRef;
            }
        }

        public Keccak RootHash
        {
            get => _rootHash;
            set => SetRootHash(value, true);
        }

        public void Commit()
        {
            if (RootRef == null)
            {
                return;
            }

            if (RootRef.IsDirty)
            {
                CurrentCommit.Clear();
                Commit(RootRef, true);
                foreach (Node nodeRef in CurrentCommit)
                {
                    _db.Set(nodeRef.Keccak, nodeRef.FullRlp.Bytes);
                }

                // reset objects
                RootRef.ResolveKey(true);
                SetRootHash(RootRef.Keccak, true);
            }
        }

        private static readonly ConcurrentBag<Node> CurrentCommit = new ConcurrentBag<Node>();

        private void Commit(Node nodeRef, bool isRoot)
        {
            Node node = nodeRef;
            if (node.IsBranch)
            {
                // idea from EthereumJ - testing parallel branches
                if (!_parallelizeBranches || !isRoot)
                {
                    for (int i = 0; i < 16; i++)
                    {
                        Node subnode = node.Children[i];
                        if (subnode?.IsDirty ?? false)
                        {
                            Commit(node.Children[i], false);
                        }
                    }
                }
                else
                {
                    List<Node> nodesToCommit = new List<Node>();
                    for (int i = 0; i < 16; i++)
                    {
                        Node subnode = node.Children[i];
                        if (subnode?.IsDirty ?? false)
                        {
                            nodesToCommit.Add(node.Children[i]);
                        }
                    }

                    if (nodesToCommit.Count >= 4)
                    {
                        CommitExceptions.Clear();
                        Parallel.For(0, nodesToCommit.Count, i =>
                        {
                            try
                            {
                                Commit(nodesToCommit[i], false);
                            }
                            catch (Exception e)
                            {
                                CommitExceptions.Enqueue(e);
                            }
                        });

                        if (CommitExceptions.Count > 0)
                        {
                            throw new AggregateException(CommitExceptions);
                        }
                    }
                    else
                    {
                        for (int i = 0; i < nodesToCommit.Count; i++)
                        {
                            Commit(nodesToCommit[i], false);
                        }
                    }
                }
            }
            else if (node.NodeType == NodeType.Extension)
            {
                if (node.Children[0].IsDirty)
                {
                    Commit(node.Children[0], false);
                }
            }

            node.IsDirty = false;
            nodeRef.ResolveKey(isRoot);
            if (nodeRef.FullRlp != null && nodeRef.FullRlp.Length >= 32)
            {
                ;
                NodeCache.Set(nodeRef.Keccak, nodeRef.FullRlp);
                CurrentCommit.Add(nodeRef);
            }
        }

        public void UpdateRootHash()
        {
            RootRef?.ResolveKey(true);
            SetRootHash(RootRef?.Keccak ?? EmptyTreeHash, false);
        }

        private void SetRootHash(Keccak value, bool resetObjects)
        {
            if (_rootHash == value)
            {
                return;
            }

            _rootHash = value;
            if (_rootHash == Keccak.EmptyTreeHash)
            {
                RootRef = null;
            }
            else
            {
                if (resetObjects)
                {
                    RootRef = new Node(NodeType.Unknown, _rootHash);
                }
            }
        }

        private static Rlp RlpEncodeRef(Node nodeRef)
        {
            if (nodeRef == null)
            {
                return Rlp.OfEmptyByteArray;
            }

            nodeRef.ResolveKey(false);
            return nodeRef.Keccak == null ? nodeRef.FullRlp : Rlp.Encode(nodeRef.Keccak);
        }

        private static Rlp RlpEncodeBranch(Node branch)
        {
            int contentLength = 0;
            for (int i = 0; i < 16; i++)
            {
                Node nodeRef = branch.Children[i];
                if (nodeRef == null)
                {
                    contentLength += Rlp.LengthOfEmptyArrayRlp;
                }
                else
                {
                    nodeRef.ResolveKey(false);
                    contentLength += nodeRef.Keccak == null ? nodeRef.FullRlp.Length : Rlp.LengthOfKeccakRlp;
                }
            }

            contentLength += Rlp.LengthOfByteArray(branch.Value);
            int sequenceLength = Rlp.GetSequenceRlpLength(contentLength);
            byte[] result = new byte[sequenceLength];
            int position = Rlp.StartSequence(result, 0, contentLength);
            for (int i = 0; i < 16; i++)
            {
                Node nodeRef = branch.Children[i];
                if (nodeRef == null)
                {
                    result[position++] = Rlp.OfEmptyByteArray[0];
                }
                else if (nodeRef.Keccak != null)
                {
                    result[position] = 160;
                    byte[] rlpBytes = nodeRef.Keccak.Bytes;
                    Array.Copy(rlpBytes, 0, result, position + 1, rlpBytes.Length);
                    position += rlpBytes.Length + 1;
                }
                else
                {
                    byte[] rlpBytes = nodeRef.FullRlp.Bytes;
                    Array.Copy(rlpBytes, 0, result, position, rlpBytes.Length);
                    position += rlpBytes.Length;
                }
            }

            Rlp.Encode(result, position, branch.Value);
            return new Rlp(result);
        }

        internal static Rlp RlpEncode(Node node)
        {
            Metrics.TreeNodeRlpEncodings++;
            if (node.IsLeaf)
            {
                Rlp result = Rlp.Encode(Rlp.Encode(node.Key.ToBytes()), Rlp.Encode(node.Value));
                return result;
            }

            if (node.IsBranch)
            {
                return RlpEncodeBranch(node);
            }

            if (node.IsExtension)
            {
                return Rlp.Encode(
                    Rlp.Encode(node.Key.ToBytes()),
                    RlpEncodeRef(node.Children[0]));
            }

            throw new InvalidOperationException($"Unknown node type {node.NodeType}");
        }

        [DebuggerStepThrough]
        public void Set(Nibble[] nibbles, Rlp rlp)
        {
            throw new NotSupportedException();
            Set(nibbles, rlp.Bytes);
        }

        [DebuggerStepThrough]
        public virtual void Set(Nibble[] nibbles, byte[] value)
        {
            throw new NotSupportedException();
            Run(nibbles.ToLooseByteArray(), value, true);
        }

        public byte[] Get(byte[] rawKey)
        {
            byte[] value = ValueCache.Get(rawKey);
            if (value != null)
            {
                return value;
            }

            return Run(Nibbles.BytesToNibbleBytes(rawKey), null, false);
        }

        [DebuggerStepThrough]
        public void Set(byte[] rawKey, byte[] value)
        {
            ValueCache.Delete(rawKey);
            Run(Nibbles.BytesToNibbleBytes(rawKey), value, true);
        }

        [DebuggerStepThrough]
        public void Set(byte[] rawKey, Rlp value)
        {
            ValueCache.Delete(rawKey);
            Run(Nibbles.BytesToNibbleBytes(rawKey), value == null ? new byte[0] : value.Bytes, true);
        }

        internal Rlp GetNode(Keccak keccak)
        {
            return NodeCache.Get(keccak) ?? new Rlp(_db[keccak.Bytes]);
        }

        public byte[] Run(byte[] updatePath, byte[] updateValue, bool isUpdate, bool ignoreMissingDelete = true)
        {
            if (isUpdate)
            {
                NodeStack.Clear();
            }

            if (isUpdate && updateValue.Length == 0)
            {
                updateValue = null;
            }

            if (RootRef == null)
            {
                if (!isUpdate || updateValue == null)
                {
                    return null;
                }

                RootRef = TreeNodeFactory.CreateLeaf(new HexPrefix(true, updatePath), updateValue);
                RootRef.IsDirty = true;
                return updateValue;
            }

            RootRef.ResolveNode(this);
            TraverseContext context = new TraverseContext(updatePath, updateValue, isUpdate, ignoreMissingDelete);
            return TraverseNode(RootRef, context);
        }

        private byte[] TraverseNode(Node node, TraverseContext context)
        {
            if (node.IsLeaf)
            {
                return TraverseLeaf(node, context);
            }

            if (node.IsBranch)
            {
                return TraverseBranch(node, context);
            }

            if (node.IsExtension)
            {
                return TraverseExtension(node, context);
            }

            throw new NotImplementedException($"Unknown node type {node.NodeType}");
        }

        // TODO: this can be removed now but is lower priority temporarily while the patricia rewrite testing is in progress
        private void ConnectNodes(Node node)
        {
            bool isRoot = NodeStack.Count == 0;
            Node nextNode = node;

            while (!isRoot)
            {
                StackedNode parentOnStack = NodeStack.Pop();
                node = parentOnStack.Node;

                isRoot = NodeStack.Count == 0;

                if (node.IsLeaf)
                {
                    throw new InvalidOperationException($"{nameof(NodeType.Leaf)} {node} cannot be a parent of {nextNode}");
                }

                if (node.IsBranch)
                {
                    if (!(nextNode == null && !node.IsValidWithOneNodeLess))
                    {
                        node.Children[parentOnStack.PathIndex] = nextNode;
                        node.IsDirty = true;
                        nextNode = node;
                    }
                    else
                    {
                        if (node.Value.Length != 0)
                        {
                            Node leafFromBranch = TreeNodeFactory.CreateLeaf(new HexPrefix(true), node.Value);
                            leafFromBranch.IsDirty = true;
                            nextNode = leafFromBranch;
                        }
                        else
                        {
                            int childNodeIndex = 0;
                            for (int i = 0; i < 16; i++)
                            {
                                if (i != parentOnStack.PathIndex && !node.Children.IsChildNull(i))
                                {
                                    childNodeIndex = i;
                                    break;
                                }
                            }

                            Node childNodeRef = node.Children[childNodeIndex];
                            if (childNodeRef == null)
                            {
                                throw new InvalidOperationException("Before updating branch should have had at least two non-empty children");
                            }

                            childNodeRef.ResolveNode(this);
                            Node childNode = childNodeRef;
                            if (childNode.IsBranch)
                            {
                                Node extensionFromBranch = TreeNodeFactory.CreateExtension(new HexPrefix(false, (byte)childNodeIndex), childNodeRef);
                                extensionFromBranch.IsDirty = true;
                                nextNode = extensionFromBranch;
                            }
                            else if (childNode.IsExtension)
                            {
                                childNode.Key = new HexPrefix(false, Bytes.Concat((byte)childNodeIndex, childNode.Path));
                                childNode.IsDirty = true;
                                nextNode = childNode;
                            }
                            else if (childNode.IsLeaf)
                            {
                                childNode.Key = new HexPrefix(true, Bytes.Concat((byte)childNodeIndex, childNode.Path));
                                childNode.IsDirty = true;
                                nextNode = childNode;
                            }
                            else
                            {
                                throw new InvalidOperationException($"Unknown node type {childNode?.NodeType}");
                            }
                        }
                    }
                }
                else if (node.IsExtension)
                {
                    if (nextNode.IsLeaf)
                    {
                        nextNode.Key = new HexPrefix(true, Bytes.Concat(node.Path, nextNode.Path));
                    }
                    else if (nextNode.IsExtension)
                    {
                        nextNode.IsDirty = true;
                        nextNode.Key = new HexPrefix(false, Bytes.Concat(node.Path, nextNode.Path));
                    }
                    else if (nextNode.IsBranch)
                    {
                        node.IsDirty = true;
                        node.Children[0] = nextNode;
                        nextNode = node;
                    }
                    else
                    {
                        throw new InvalidOperationException($"Unknown node type {nextNode.NodeType}");
                    }
                }
                else
                {
                    throw new InvalidOperationException($"Unknown node type {node.GetType().Name}");
                }
            }

            RootRef = nextNode;
        }

        private byte[] TraverseBranch(Node node, TraverseContext context)
        {
            if (context.RemainingUpdatePathLength == 0)
            {
                if (!context.IsUpdate)
                {
                    return node.Value;
                }

                if (context.UpdateValue == null)
                {
                    if (node.Value == null)
                    {
                        return null;
                    }

                    ConnectNodes(null);
                }
                else if (Bytes.UnsafeCompare(context.UpdateValue, node.Value))
                {
                    return context.UpdateValue;
                }
                else
                {
                    node.Value = context.UpdateValue;
                    node.IsDirty = true;
                }

                return context.UpdateValue;
            }

            Node nextNodeRef = node.Children[context.UpdatePath[context.CurrentIndex]];
            if (context.IsUpdate)
            {
                NodeStack.Push(new StackedNode(node, context.UpdatePath[context.CurrentIndex]));
            }

            context.CurrentIndex++;

            if (nextNodeRef == null)
            {
                if (!context.IsUpdate)
                {
                    return null;
                }

                if (context.UpdateValue == null)
                {
                    if (context.IgnoreMissingDelete)
                    {
                        return null;
                    }

                    throw new InvalidOperationException($"Could not find the leaf node to delete: {Hex.FromBytes(context.UpdatePath, false)}");
                }

                byte[] leafPath = context.UpdatePath.Slice(context.CurrentIndex, context.UpdatePath.Length - context.CurrentIndex);
                Node leaf = TreeNodeFactory.CreateLeaf(new HexPrefix(true, leafPath), context.UpdateValue);
                leaf.IsDirty = true;
                ConnectNodes(leaf);

                return context.UpdateValue;
            }

            nextNodeRef.ResolveNode(this);
            Node nextNode = nextNodeRef;
            return TraverseNode(nextNode, context);
        }

        private byte[] TraverseLeaf(Node node, TraverseContext context)
        {
            byte[] remaining = context.GetRemainingUpdatePath();
            (byte[] shorterPath, byte[] longerPath) = remaining.Length - node.Path.Length < 0
                ? (remaining, node.Path)
                : (node.Path, remaining);

            byte[] shorterPathValue;
            byte[] longerPathValue;

            if (Bytes.UnsafeCompare(shorterPath, node.Path))
            {
                shorterPathValue = node.Value;
                longerPathValue = context.UpdateValue;
            }
            else
            {
                shorterPathValue = context.UpdateValue;
                longerPathValue = node.Value;
            }

            int extensionLength = 0;
            for (int i = 0; i < Math.Min(shorterPath.Length, longerPath.Length) && shorterPath[i] == longerPath[i]; i++, extensionLength++)
            {
            }

            if (extensionLength == shorterPath.Length && extensionLength == longerPath.Length)
            {
                if (!context.IsUpdate)
                {
                    return node.Value;
                }

                if (context.UpdateValue == null)
                {
                    ConnectNodes(null);
                    return context.UpdateValue;
                }

                if (!Bytes.UnsafeCompare(node.Value, context.UpdateValue))
                {
                    node.Value = context.UpdateValue;
                    node.IsDirty = true;
                    ConnectNodes(node);
                    return context.UpdateValue;
                }

                return context.UpdateValue;
            }

            if (!context.IsUpdate)
            {
                return null;
            }

            if (context.UpdateValue == null)
            {
                if (context.IgnoreMissingDelete)
                {
                    return null;
                }

                throw new InvalidOperationException($"Could not find the leaf node to delete: {Hex.FromBytes(context.UpdatePath, false)}");
            }

            if (extensionLength != 0)
            {
                byte[] extensionPath = longerPath.Slice(0, extensionLength);
                Node extension = TreeNodeFactory.CreateExtension(new HexPrefix(false, extensionPath));
                extension.IsDirty = true;
                NodeStack.Push(new StackedNode(extension, 0));
            }

            Node branch = TreeNodeFactory.CreateBranch();
            branch.IsDirty = true;
            if (extensionLength == shorterPath.Length)
            {
                branch.Value = shorterPathValue;
            }
            else
            {
                byte[] shortLeafPath = shorterPath.Slice(extensionLength + 1, shorterPath.Length - extensionLength - 1);
                Node shortLeaf = TreeNodeFactory.CreateLeaf(new HexPrefix(true, shortLeafPath), shorterPathValue);
                shortLeaf.IsDirty = true;
                branch.Children[shorterPath[extensionLength]] = shortLeaf;
            }

            byte[] leafPath = longerPath.Slice(extensionLength + 1, longerPath.Length - extensionLength - 1);


            node.IsDirty = true;
            node.Key = new HexPrefix(true, leafPath);
            node.Value = longerPathValue;

            NodeStack.Push(new StackedNode(branch, longerPath[extensionLength]));
            ConnectNodes(node);

            return context.UpdateValue;
        }

        private byte[] TraverseExtension(Node node, TraverseContext context)
        {
            byte[] remaining = context.GetRemainingUpdatePath();
            int extensionLength = 0;
            for (int i = 0; i < Math.Min(remaining.Length, node.Path.Length) && remaining[i] == node.Path[i]; i++, extensionLength++)
            {
            }

            if (extensionLength == node.Path.Length)
            {
                context.CurrentIndex += extensionLength;
                if (context.IsUpdate)
                {
                    NodeStack.Push(new StackedNode(node, 0));
                }

                node.Children[0].ResolveNode(this);
                return TraverseNode(node.Children[0], context);
            }

            if (!context.IsUpdate)
            {
                return null;
            }

            if (context.UpdateValue == null)
            {
                if (context.IgnoreMissingDelete)
                {
                    return null;
                }

                throw new InvalidOperationException("Could find the leaf node to delete: {Hex.FromBytes(context.UpdatePath, false)}");
            }

            byte[] pathBeforeUpdate = node.Path;
            if (extensionLength != 0)
            {
                byte[] extensionPath = node.Path.Slice(0, extensionLength);
                node.Key = new HexPrefix(false, extensionPath);
                node.IsDirty = true;
                NodeStack.Push(new StackedNode(node, 0));
            }

            Node branch = TreeNodeFactory.CreateBranch();
            branch.IsDirty = true;
            if (extensionLength == remaining.Length)
            {
                branch.Value = context.UpdateValue;
            }
            else
            {
                byte[] path = remaining.Slice(extensionLength + 1, remaining.Length - extensionLength - 1);
                Node shortLeaf = TreeNodeFactory.CreateLeaf(new HexPrefix(true, path), context.UpdateValue);
                shortLeaf.IsDirty = true;
                branch.Children[remaining[extensionLength]] = shortLeaf;
            }

            if (pathBeforeUpdate.Length - extensionLength > 1)
            {
                byte[] extensionPath = pathBeforeUpdate.Slice(extensionLength + 1, pathBeforeUpdate.Length - extensionLength - 1);
                Node secondExtension = TreeNodeFactory.CreateExtension(new HexPrefix(false, extensionPath), node.Children[0]);
                secondExtension.IsDirty = true;
                branch.Children[pathBeforeUpdate[extensionLength]] = secondExtension;
            }
            else
            {
                branch.Children[pathBeforeUpdate[extensionLength]] = node.Children[0];
            }

            ConnectNodes(branch);
            return context.UpdateValue;
        }

        private struct TraverseContext
        {
            public byte[] UpdatePath { get; }
            public byte[] UpdateValue { get; }
            public bool IsUpdate { get; }
            public bool IgnoreMissingDelete { get; }
            public int CurrentIndex { get; set; }
            public int RemainingUpdatePathLength => UpdatePath.Length - CurrentIndex;

            public byte[] GetRemainingUpdatePath()
            {
                return UpdatePath.Slice(CurrentIndex, RemainingUpdatePathLength);
            }

            public TraverseContext(byte[] updatePath, byte[] updateValue, bool isUpdate, bool ignoreMissingDelete = true)
            {
                UpdatePath = updatePath;
                UpdateValue = updateValue;
                IsUpdate = isUpdate;
                IgnoreMissingDelete = ignoreMissingDelete;
                CurrentIndex = 0;
            }
        }

        private struct StackedNode
        {
            public StackedNode(Node node, int pathIndex)
            {
                Node = node;
                PathIndex = pathIndex;
            }

            public Node Node { get; }
            public int PathIndex { get; }
        }
    }
}