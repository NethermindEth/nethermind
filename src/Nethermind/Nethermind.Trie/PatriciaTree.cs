//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Trie
{
    [DebuggerDisplay("{RootHash}")]
    public class PatriciaTree
    {
        public static readonly LruCache<Keccak, byte[]> NodeCache = new LruCache<Keccak, byte[]>(MemoryAllowance.TrieNodeCacheSize, MemoryAllowance.TrieNodeCacheSize, "trie nodes");

        /// <summary>
        ///     0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421
        /// </summary>
        public static readonly Keccak EmptyTreeHash = Keccak.EmptyTreeHash;

        /// <summary>
        /// To save allocations this used to be static but this caused one of the hardest to reproduce issues when we actually decided to run some of the tree operations in parallel.
        /// </summary>
        private readonly Stack<StackedNode> _nodeStack = new Stack<StackedNode>();

        private readonly ConcurrentQueue<Exception> _commitExceptions;
        
        private readonly ConcurrentQueue<TrieNode> _currentCommit;

        protected readonly IKeyValueStore _keyValueStore;
        private readonly bool _parallelBranches;
        private readonly bool _allowCommits;

        private Keccak _rootHash = Keccak.EmptyTreeHash;

        internal TrieNode RootRef;

        public PatriciaTree()
            : this(NullKeyValueStore.Instance, EmptyTreeHash, false, true)
        {
        }
        
        public PatriciaTree(IKeyValueStore keyValueStore)
            : this(keyValueStore, EmptyTreeHash, false, true)
        {
        }

        public PatriciaTree(IKeyValueStore keyValueStore, Keccak rootHash, bool parallelBranches, bool allowCommits)
        {
            _keyValueStore = keyValueStore;
            _parallelBranches = parallelBranches;
            _allowCommits = allowCommits;
            RootHash = rootHash;

            if (_allowCommits)
            {
                _currentCommit = new ConcurrentQueue<TrieNode>();
                _commitExceptions = new ConcurrentQueue<Exception>();
            }
        }

        /// <summary>
        /// Only used in EthereumTests
        /// </summary>
        internal TrieNode Root
        {
            get
            {
                RootRef?.ResolveNode(this);
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
            if (!_allowCommits)
            {
                throw new TrieException("Commits are not allowed on this trie.");
            }
            
            if (RootRef == null)
            {
                return;
            }

            if (RootRef.IsDirty)
            {
                Commit(RootRef, true);
                while (!_currentCommit.IsEmpty)
                {
                    if (!_currentCommit.TryDequeue(out TrieNode node))
                    {
                        throw new ArgumentNullException($"Threading issue at {nameof(_currentCommit)} - should not happen unless we use static objects somewhere here.");
                    }

                    _keyValueStore[node.Keccak.Bytes] = node.FullRlp;
                }

                // reset objects
                RootRef.ResolveKey(true);
                SetRootHash(RootRef.Keccak, true);
            }
        }

        private void Commit(TrieNode node, bool isRoot)
        {
            if (node.IsBranch)
            {
                // idea from EthereumJ - testing parallel branches
                if (!_parallelBranches || !isRoot)
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
                        _commitExceptions.Clear();
                        Parallel.For(0, nodesToCommit.Count, i =>
                        {
                            try
                            {
                                Commit(nodesToCommit[i], false);
                            }
                            catch (Exception e)
                            {
                                _commitExceptions.Enqueue(e);
                            }
                        });

                        if (_commitExceptions.Count > 0)
                        {
                            throw new AggregateException(_commitExceptions);
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

            node.ResolveKey(isRoot);
            node.IsDirty = false;

            if (node.FullRlp != null && node.FullRlp.Length >= 32)
            {
                NodeCache.Set(node.Keccak, node.FullRlp);
                _currentCommit.Enqueue(node);
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
        public byte[] Get(Span<byte> rawKey, Keccak rootHash = null)
        {
            int nibblesCount = 2 * rawKey.Length;
            byte[] array = null;
            Span<byte> nibbles = rawKey.Length <= 64
                ? stackalloc byte[nibblesCount]
                : array = ArrayPool<byte>.Shared.Rent(nibblesCount);
            Nibbles.BytesToNibbleBytes(rawKey, nibbles);
            var result = Run(nibbles, nibblesCount, null, false, rootHash: rootHash);
            if (array != null) ArrayPool<byte>.Shared.Return(array);
            return result;
        }

        [DebuggerStepThrough]
        public void Set(Span<byte> rawKey, byte[] value)
        {
//            ValueCache.Delete(rawKey);
            int nibblesCount = 2 * rawKey.Length;
            byte[] array = null;
            Span<byte> nibbles = rawKey.Length <= 64
                ? stackalloc byte[nibblesCount]
                : array = ArrayPool<byte>.Shared.Rent(nibblesCount);
            Nibbles.BytesToNibbleBytes(rawKey, nibbles);
            Run(nibbles, nibblesCount, value, true);
            if (array != null) ArrayPool<byte>.Shared.Return(array);
        }

        [DebuggerStepThrough]
        public void Set(Span<byte> rawKey, Rlp value)
        {
            Set(rawKey, value == null ? new byte[0] : value.Bytes);
        }

        internal byte[] GetNode(Keccak keccak, bool allowCaching)
        {
            if (!allowCaching)
            {
                return _keyValueStore[keccak.Bytes];
            }

            byte[] cachedRlp = NodeCache.Get(keccak);
            if (cachedRlp == null)
            {
                byte[] dbValue = _keyValueStore[keccak.Bytes];
                if (dbValue == null)
                {
                    throw new TrieException($"Node {keccak} is missing from the DB");
                }
                
                NodeCache.Set(keccak, dbValue);
                return dbValue;
            }

            return cachedRlp;
        }

        private byte[] Run(Span<byte> updatePath, int nibblesCount, byte[] updateValue, bool isUpdate, bool ignoreMissingDelete = true, Keccak rootHash = null)
        {
            if (isUpdate && rootHash != null)
            {
                throw new InvalidOperationException("Only reads can be done in parallel on the Patricia tree");
            }

            if (isUpdate)
            {
                _nodeStack.Clear();
            }

            if (isUpdate && updateValue.Length == 0)
            {
                updateValue = null;
            }

            if (!(rootHash is null))
            {
                var rootRef = new TrieNode(NodeType.Unknown, rootHash);
                rootRef.ResolveNode(this);
                return TraverseNode(rootRef, new TraverseContext(updatePath.Slice(0, nibblesCount), updateValue,
                    false, ignoreMissingDelete));
            }

            if (RootRef == null)
            {
                if (!isUpdate || updateValue == null)
                {
                    return null;
                }

                RootRef = TrieNodeFactory.CreateLeaf(new HexPrefix(true, updatePath.Slice(0, nibblesCount).ToArray()), updateValue);
                RootRef.IsDirty = true;
                return updateValue;
            }

            RootRef.ResolveNode(this);
            TraverseContext traverseContext = new TraverseContext(updatePath.Slice(0, nibblesCount), updateValue, isUpdate, ignoreMissingDelete);
            return TraverseNode(RootRef, traverseContext);
        }

        private byte[] TraverseNode(TrieNode node, TraverseContext traverseContext)
        {
            if (node.IsLeaf)
            {
                return TraverseLeaf(node, traverseContext);
            }

            if (node.IsBranch)
            {
                return TraverseBranch(node, traverseContext);
            }

            if (node.IsExtension)
            {
                return TraverseExtension(node, traverseContext);
            }

            throw new NotSupportedException($"Unknown node type {node.NodeType}");
        }

        // TODO: this can be removed now but is lower priority temporarily while the patricia rewrite testing is in progress
        private void ConnectNodes(TrieNode node)
        {
            bool isRoot = _nodeStack.Count == 0;
            TrieNode nextNode = node;

            while (!isRoot)
            {
                StackedNode parentOnStack = _nodeStack.Pop();
                node = parentOnStack.Node;

                isRoot = _nodeStack.Count == 0;

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
                            TrieNode leafFromBranch = TrieNodeFactory.CreateLeaf(new HexPrefix(true), node.Value);
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
                                TrieNode extensionFromBranch = TrieNodeFactory.CreateExtension(new HexPrefix(false, (byte) childNodeIndex), childNode);
                                extensionFromBranch.IsDirty = true;
                                nextNode = extensionFromBranch;
                            }
                            else if (childNode.IsExtension)
                            {
                                childNode.Key = new HexPrefix(false, Bytes.Concat((byte) childNodeIndex, childNode.Path));
                                childNode.IsDirty = true;
                                nextNode = childNode;
                            }
                            else if (childNode.IsLeaf)
                            {
                                childNode.Key = new HexPrefix(true, Bytes.Concat((byte) childNodeIndex, childNode.Path));
                                childNode.IsDirty = true;
                                nextNode = childNode;
                            }
                            else
                            {
                                throw new InvalidOperationException($"Unknown node type {childNode.NodeType}");
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

        private byte[] TraverseBranch(TrieNode node, TraverseContext traverseContext)
        {
            if (traverseContext.RemainingUpdatePathLength == 0)
            {
                if (!traverseContext.IsUpdate)
                {
                    return node.Value;
                }

                if (traverseContext.UpdateValue == null)
                {
                    if (node.Value == null)
                    {
                        return null;
                    }

                    ConnectNodes(null);
                }
                else if (Bytes.AreEqual(traverseContext.UpdateValue, node.Value))
                {
                    return traverseContext.UpdateValue;
                }
                else
                {
                    node.Value = traverseContext.UpdateValue;
                    node.IsDirty = true;
                }

                return traverseContext.UpdateValue;
            }

            TrieNode childNode = node.GetChild(traverseContext.UpdatePath[traverseContext.CurrentIndex]);
            if (traverseContext.IsUpdate)
            {
                _nodeStack.Push(new StackedNode(node, traverseContext.UpdatePath[traverseContext.CurrentIndex]));
            }

            traverseContext.CurrentIndex++;

            if (childNode == null)
            {
                if (!traverseContext.IsUpdate)
                {
                    return null;
                }

                if (traverseContext.UpdateValue == null)
                {
                    if (traverseContext.IgnoreMissingDelete)
                    {
                        return null;
                    }

                    throw new InvalidOperationException($"Could not find the leaf node to delete: {traverseContext.UpdatePath.ToHexString(false)}");
                }

                byte[] leafPath = traverseContext.UpdatePath.Slice(traverseContext.CurrentIndex, traverseContext.UpdatePath.Length - traverseContext.CurrentIndex).ToArray();
                TrieNode leaf = TrieNodeFactory.CreateLeaf(new HexPrefix(true, leafPath), traverseContext.UpdateValue);
                leaf.IsDirty = true;
                ConnectNodes(leaf);

                return traverseContext.UpdateValue;
            }

            childNode.ResolveNode(this);
            TrieNode nextNode = childNode;
            return TraverseNode(nextNode, traverseContext);
        }

        private byte[] TraverseLeaf(TrieNode node, TraverseContext traverseContext)
        {
            Span<byte> remaining = traverseContext.GetRemainingUpdatePath();
            Span<byte> shorterPath;
            Span<byte> longerPath;
            if (traverseContext.RemainingUpdatePathLength - node.Path.Length < 0)
            {
                shorterPath = remaining;
                longerPath = node.Path;
            }
            else
            {
                shorterPath = node.Path;
                longerPath = remaining;
            }

            byte[] shorterPathValue;
            byte[] longerPathValue;

            if (Bytes.AreEqual(shorterPath, node.Path))
            {
                shorterPathValue = node.Value;
                longerPathValue = traverseContext.UpdateValue;
            }
            else
            {
                shorterPathValue = traverseContext.UpdateValue;
                longerPathValue = node.Value;
            }

            int extensionLength = 0;
            for (int i = 0; i < Math.Min(shorterPath.Length, longerPath.Length) && shorterPath[i] == longerPath[i]; i++, extensionLength++)
            {
            }

            if (extensionLength == shorterPath.Length && extensionLength == longerPath.Length)
            {
                if (!traverseContext.IsUpdate)
                {
                    return node.Value;
                }

                if (traverseContext.UpdateValue == null)
                {
                    ConnectNodes(null);
                    return traverseContext.UpdateValue;
                }

                if (!Bytes.AreEqual(node.Value, traverseContext.UpdateValue))
                {
                    node.Value = traverseContext.UpdateValue;
                    node.IsDirty = true;
                    ConnectNodes(node);
                    return traverseContext.UpdateValue;
                }

                return traverseContext.UpdateValue;
            }

            if (!traverseContext.IsUpdate)
            {
                return null;
            }

            if (traverseContext.UpdateValue == null)
            {
                if (traverseContext.IgnoreMissingDelete)
                {
                    return null;
                }

                throw new InvalidOperationException($"Could not find the leaf node to delete: {traverseContext.UpdatePath.ToHexString(false)}");
            }

            if (extensionLength != 0)
            {
                Span<byte> extensionPath = longerPath.Slice(0, extensionLength);
                TrieNode extension = TrieNodeFactory.CreateExtension(new HexPrefix(false, extensionPath.ToArray()));
                extension.IsDirty = true;
                _nodeStack.Push(new StackedNode(extension, 0));
            }

            TrieNode branch = TrieNodeFactory.CreateBranch();
            branch.IsDirty = true;
            if (extensionLength == shorterPath.Length)
            {
                branch.Value = shorterPathValue;
            }
            else
            {
                Span<byte> shortLeafPath = shorterPath.Slice(extensionLength + 1, shorterPath.Length - extensionLength - 1);
                TrieNode shortLeaf = TrieNodeFactory.CreateLeaf(new HexPrefix(true, shortLeafPath.ToArray()), shorterPathValue);
                shortLeaf.IsDirty = true;
                branch.SetChild(shorterPath[extensionLength], shortLeaf);
            }

            Span<byte> leafPath = longerPath.Slice(extensionLength + 1, longerPath.Length - extensionLength - 1);


            node.IsDirty = true;
            node.Key = new HexPrefix(true, leafPath.ToArray());
            node.Value = longerPathValue;

            _nodeStack.Push(new StackedNode(branch, longerPath[extensionLength]));
            ConnectNodes(node);

            return traverseContext.UpdateValue;
        }

        private byte[] TraverseExtension(TrieNode node, TraverseContext traverseContext)
        {
            Span<byte> remaining = traverseContext.GetRemainingUpdatePath();
            int extensionLength = 0;
            for (int i = 0; i < Math.Min(remaining.Length, node.Path.Length) && remaining[i] == node.Path[i]; i++, extensionLength++)
            {
            }

            if (extensionLength == node.Path.Length)
            {
                traverseContext.CurrentIndex += extensionLength;
                if (traverseContext.IsUpdate)
                {
                    _nodeStack.Push(new StackedNode(node, 0));
                }

                TrieNode next = node.GetChild(0);
                next.ResolveNode(this);
                return TraverseNode(next, traverseContext);
            }

            if (!traverseContext.IsUpdate)
            {
                return null;
            }

            if (traverseContext.UpdateValue == null)
            {
                if (traverseContext.IgnoreMissingDelete)
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
                _nodeStack.Push(new StackedNode(node, 0));
            }

            TrieNode branch = TrieNodeFactory.CreateBranch();
            branch.IsDirty = true;
            if (extensionLength == remaining.Length)
            {
                branch.Value = traverseContext.UpdateValue;
            }
            else
            {
                byte[] path = remaining.Slice(extensionLength + 1, remaining.Length - extensionLength - 1).ToArray();
                TrieNode shortLeaf = TrieNodeFactory.CreateLeaf(new HexPrefix(true, path), traverseContext.UpdateValue);
                shortLeaf.IsDirty = true;
                branch.SetChild(remaining[extensionLength], shortLeaf);
            }

            if (pathBeforeUpdate.Length - extensionLength > 1)
            {
                byte[] extensionPath = pathBeforeUpdate.Slice(extensionLength + 1, pathBeforeUpdate.Length - extensionLength - 1);
                TrieNode secondExtension = TrieNodeFactory.CreateExtension(new HexPrefix(false, extensionPath), node.GetChild(0));
                secondExtension.IsDirty = true;
                branch.SetChild(pathBeforeUpdate[extensionLength], secondExtension);
            }
            else
            {
                branch.SetChild(pathBeforeUpdate[extensionLength], node.GetChild(0));
            }

            ConnectNodes(branch);
            return traverseContext.UpdateValue;
        }

        private ref struct TraverseContext
        {
            public Span<byte> UpdatePath { get; }
            public byte[] UpdateValue { get; }
            public bool IsUpdate { get; }
            public bool IgnoreMissingDelete { get; }
            public int CurrentIndex { get; set; }
            public int RemainingUpdatePathLength => UpdatePath.Length - CurrentIndex;

            public Span<byte> GetRemainingUpdatePath()
            {
                return UpdatePath.Slice(CurrentIndex, RemainingUpdatePathLength);
            }

            public TraverseContext(Span<byte> updatePath, byte[] updateValue, bool isUpdate, bool ignoreMissingDelete = true)
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

        public void Accept(ITreeVisitor visitor, Keccak rootHash, bool expectAccounts)
        {
            if (visitor == null) throw new ArgumentNullException(nameof(visitor));
            if (rootHash == null) throw new ArgumentNullException(nameof(rootHash));
            
            TrieVisitContext trieVisitContext = new TrieVisitContext();
            
            // hacky but other solutions are not much better, something nicer would require a bit of thinking
            // we introduced a notion of an account on the visit context level which should have no knowledge of account really
            // but we know that we have multiple optimizations and assumptions on trees
            trieVisitContext.ExpectAccounts = expectAccounts;
            
            TrieNode rootRef = null;
            if (!rootHash.Equals(Keccak.EmptyTreeHash))
            {
                rootRef = RootHash == rootHash ? RootRef : new TrieNode(NodeType.Unknown, rootHash);
                try
                {
                    // not allowing caching just for test scenarios when we use multiple trees
                    rootRef.ResolveNode(this, false);
                }
                catch (TrieException)
                {
                    visitor.VisitMissingNode(rootHash, trieVisitContext);
                    return;
                }
            }
            
            visitor.VisitTree(rootHash, trieVisitContext);
            rootRef?.Accept(visitor, this, trieVisitContext);
        }
    }
}