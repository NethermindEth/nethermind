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
//        private static readonly LruCache<byte[], byte[]> ValueCache = new LruCache<byte[], byte[]>(128 * 1024);

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

        internal TrieNode RootRef;

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

        internal TrieNode Root
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
                Commit(RootRef, true);
                while(!CurrentCommit.IsEmpty)
                {
                    CurrentCommit.TryDequeue(out TrieNode node);
                    _db.Set(node.Keccak, node.FullRlp.Bytes);
                }

                // reset objects
                RootRef.ResolveKey(true);
                SetRootHash(RootRef.Keccak, true);
            }
        }

        private static readonly ConcurrentQueue<TrieNode> CurrentCommit = new ConcurrentQueue<TrieNode>();

        private void Commit(TrieNode node, bool isRoot)
        {
            if (node.IsBranch)
            {
                // idea from EthereumJ - testing parallel branches
                if (!_parallelizeBranches || !isRoot)
                {
                    for (int i = 0; i < 16; i++)
                    {
                        if (node.IsChildDirty(i))
                        {
                            Commit(node.GetChild(i), false);
                        }
                    }
                }
                else
                {
                    List<TrieNode> nodesToCommit = new List<TrieNode>();
                    for (int i = 0; i < 16; i++)
                    {
                        if (node.IsChildDirty(i))
                        {
                            nodesToCommit.Add(node.GetChild(i));
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
                if (node.GetChild(0).IsDirty)
                {
                    Commit(node.GetChild(0), false);
                }
            }

            node.IsDirty = false;
            node.ResolveKey(isRoot);
            if (node.FullRlp != null && node.FullRlp.Length >= 32)
            {
                NodeCache.Set(node.Keccak, node.FullRlp);
                CurrentCommit.Enqueue(node);
            }
        }

        public void UpdateRootHash()
        {
            RootRef?.ResolveKey(true);
            SetRootHash(RootRef?.Keccak ?? EmptyTreeHash, false);
        }

        private void SetRootHash(Keccak value, bool resetObjects)
        {
            _rootHash = value;
            if (_rootHash == Keccak.EmptyTreeHash)
            {
                RootRef = null;
            }
            else if (resetObjects)
            {
                RootRef = new TrieNode(NodeType.Unknown, _rootHash);
            }
        }

        [DebuggerStepThrough]
        public void Set(Nibble[] nibbles, Rlp rlp)
        {
            Set(nibbles, rlp.Bytes);
        }

        [DebuggerStepThrough]
        public virtual void Set(Nibble[] nibbles, byte[] value)
        {
            Run(nibbles.ToLooseByteArray(), value, true);
        }

        public byte[] Get(byte[] rawKey)
        {
//            byte[] value = ValueCache.Get(rawKey);
//            if (value != null)
//            {
//                return value;
//            }

            return Run(Nibbles.BytesToNibbleBytes(rawKey), null, false);
        }

        [DebuggerStepThrough]
        public void Set(byte[] rawKey, byte[] value)
        {
//            ValueCache.Delete(rawKey);
            Run(Nibbles.BytesToNibbleBytes(rawKey), value, true);
        }

        [DebuggerStepThrough]
        public void Set(byte[] rawKey, Rlp value)
        {
//            ValueCache.Delete(rawKey);
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

        private byte[] TraverseNode(TrieNode node, TraverseContext context)
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
        private void ConnectNodes(TrieNode node)
        {
            bool isRoot = NodeStack.Count == 0;
            TrieNode nextNode = node;

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
                        node.SetChild(parentOnStack.PathIndex, nextNode);
                        node.IsDirty = true;
                        nextNode = node;
                    }
                    else
                    {
                        if (node.Value.Length != 0)
                        {
                            TrieNode leafFromBranch = TreeNodeFactory.CreateLeaf(new HexPrefix(true), node.Value);
                            leafFromBranch.IsDirty = true;
                            nextNode = leafFromBranch;
                        }
                        else
                        {
                            int childNodeIndex = 0;
                            for (int i = 0; i < 16; i++)
                            {
                                if (i != parentOnStack.PathIndex && !node.IsChildNull(i))
                                {
                                    childNodeIndex = i;
                                    break;
                                }
                            }

                            TrieNode childNode = node.GetChild(childNodeIndex);
                            if (childNode == null)
                            {
                                throw new InvalidOperationException("Before updating branch should have had at least two non-empty children");
                            }

                            childNode.ResolveNode(this);
                            if (childNode.IsBranch)
                            {
                                TrieNode extensionFromBranch = TreeNodeFactory.CreateExtension(new HexPrefix(false, (byte)childNodeIndex), childNode);
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
                        node.SetChild(0, nextNode);
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

        private byte[] TraverseBranch(TrieNode node, TraverseContext context)
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
                else if (Bytes.AreEqual(context.UpdateValue, node.Value))
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

            TrieNode childNode = node.GetChild(context.UpdatePath[context.CurrentIndex]);
            if (context.IsUpdate)
            {
                NodeStack.Push(new StackedNode(node, context.UpdatePath[context.CurrentIndex]));
            }

            context.CurrentIndex++;

            if (childNode == null)
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

                    throw new InvalidOperationException($"Could not find the leaf node to delete: {context.UpdatePath.ToHexString(false)}");
                }

                byte[] leafPath = context.UpdatePath.Slice(context.CurrentIndex, context.UpdatePath.Length - context.CurrentIndex);
                TrieNode leaf = TreeNodeFactory.CreateLeaf(new HexPrefix(true, leafPath), context.UpdateValue);
                leaf.IsDirty = true;
                ConnectNodes(leaf);

                return context.UpdateValue;
            }

            childNode.ResolveNode(this);
            TrieNode nextNode = childNode;
            return TraverseNode(nextNode, context);
        }

        private byte[] TraverseLeaf(TrieNode node, TraverseContext context)
        {
            byte[] remaining = context.GetRemainingUpdatePath();
            (byte[] shorterPath, byte[] longerPath) = context.RemainingUpdatePathLength - node.Path.Length < 0
                ? (remaining, node.Path)
                : (node.Path, remaining);

            byte[] shorterPathValue;
            byte[] longerPathValue;

            if (Bytes.AreEqual(shorterPath, node.Path))
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

                if (!Bytes.AreEqual(node.Value, context.UpdateValue))
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

                throw new InvalidOperationException($"Could not find the leaf node to delete: {context.UpdatePath.ToHexString(false)}");
            }

            if (extensionLength != 0)
            {
                byte[] extensionPath = longerPath.Slice(0, extensionLength);
                TrieNode extension = TreeNodeFactory.CreateExtension(new HexPrefix(false, extensionPath));
                extension.IsDirty = true;
                NodeStack.Push(new StackedNode(extension, 0));
            }

            TrieNode branch = TreeNodeFactory.CreateBranch();
            branch.IsDirty = true;
            if (extensionLength == shorterPath.Length)
            {
                branch.Value = shorterPathValue;
            }
            else
            {
                byte[] shortLeafPath = shorterPath.Slice(extensionLength + 1, shorterPath.Length - extensionLength - 1);
                TrieNode shortLeaf = TreeNodeFactory.CreateLeaf(new HexPrefix(true, shortLeafPath), shorterPathValue);
                shortLeaf.IsDirty = true;
                branch.SetChild(shorterPath[extensionLength], shortLeaf);
            }

            byte[] leafPath = longerPath.Slice(extensionLength + 1, longerPath.Length - extensionLength - 1);


            node.IsDirty = true;
            node.Key = new HexPrefix(true, leafPath);
            node.Value = longerPathValue;

            NodeStack.Push(new StackedNode(branch, longerPath[extensionLength]));
            ConnectNodes(node);

            return context.UpdateValue;
        }

        private byte[] TraverseExtension(TrieNode node, TraverseContext context)
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

                TrieNode next = node.GetChild(0);
                next.ResolveNode(this);
                return TraverseNode(next, context);
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

            TrieNode branch = TreeNodeFactory.CreateBranch();
            branch.IsDirty = true;
            if (extensionLength == remaining.Length)
            {
                branch.Value = context.UpdateValue;
            }
            else
            {
                byte[] path = remaining.Slice(extensionLength + 1, remaining.Length - extensionLength - 1);
                TrieNode shortLeaf = TreeNodeFactory.CreateLeaf(new HexPrefix(true, path), context.UpdateValue);
                shortLeaf.IsDirty = true;
                branch.SetChild(remaining[extensionLength], shortLeaf);
            }

            if (pathBeforeUpdate.Length - extensionLength > 1)
            {
                byte[] extensionPath = pathBeforeUpdate.Slice(extensionLength + 1, pathBeforeUpdate.Length - extensionLength - 1);
                TrieNode secondExtension = TreeNodeFactory.CreateExtension(new HexPrefix(false, extensionPath), node.GetChild(0));
                secondExtension.IsDirty = true;
                branch.SetChild(pathBeforeUpdate[extensionLength], secondExtension);
            }
            else
            {
                branch.SetChild(pathBeforeUpdate[extensionLength], node.GetChild(0));
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
            public StackedNode(TrieNode node, int pathIndex)
            {
                Node = node;
                PathIndex = pathIndex;
            }

            public TrieNode Node { get; }
            public int PathIndex { get; }
        }
    }
}