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
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie
{
    [DebuggerDisplay("{RootHash}")]
    public class PatriciaTree : ITrieNodeResolver
    {
        private readonly ILogger _logger = NullLogger.Instance;
        private const int OneNodeAvgMemoryEstimate = 384;

        public static readonly ICache<Keccak, byte[]> NodeCache =
            new LruCacheWithRecycling<Keccak, byte[]>(
                (int) (MemoryAllowance.TrieNodeCacheMemory / OneNodeAvgMemoryEstimate), "trie nodes");

        /// <summary>
        ///     0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421
        /// </summary>
        public static readonly Keccak EmptyTreeHash = Keccak.EmptyTreeHash;

        /// <summary>
        /// To save allocations this used to be static but this caused one of the hardest to reproduce issues
        /// when we decided to run some of the tree operations in parallel.
        /// </summary>
        private readonly Stack<StackedNode> _nodeStack = new Stack<StackedNode>();

        private readonly ConcurrentQueue<Exception>? _commitExceptions;

        private readonly ConcurrentQueue<TrieNode>? _currentCommit;

        protected readonly ITreeStore _keyValueStore;

        private readonly bool _parallelBranches;

        private readonly bool _allowCommits;

        private Keccak _rootHash = Keccak.EmptyTreeHash;

        internal TrieNode? RootRef;

        public PatriciaTree()
            : this(new NullTreeStore(), EmptyTreeHash, false, true)
        {
        }

        public PatriciaTree(IKeyValueStore keyValueStore)
            : this(keyValueStore, EmptyTreeHash, false, true)
        {
        }

        public PatriciaTree(ITreeStore keyValueStore, ILogger logger)
            : this(keyValueStore, EmptyTreeHash, false, true)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public PatriciaTree(IKeyValueStore keyValueStore, Keccak rootHash, bool parallelBranches, bool allowCommits)
            : this(new PassThroughTreeStore(keyValueStore), rootHash, parallelBranches, allowCommits)
        {
        }

        public PatriciaTree(ITreeStore keyValueStore, Keccak rootHash, bool parallelBranches, bool allowCommits)
        {
            _keyValueStore = keyValueStore ?? throw new ArgumentNullException(nameof(keyValueStore));
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
        internal TrieNode? Root
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

        public void Commit(long blockNumber)
        {
            if (_currentCommit is null)
            {
                throw new InvalidAsynchronousStateException(
                    $"{nameof(_currentCommit)} is NULL when calling {nameof(Commit)}");
            }
            
            if (!_allowCommits)
            {
                throw new TrieException("Commits are not allowed on this trie.");
            }
            
            if (RootRef != null && RootRef.IsDirty)
            {
                Commit(RootRef, true);
                while (!_currentCommit.IsEmpty)
                {
                    if (!_currentCommit.TryDequeue(out TrieNode node))
                    {
                        throw new InvalidAsynchronousStateException(
                            $"Threading issue at {nameof(_currentCommit)} - should not happen unless we use static objects somewhere here.");
                    }

                    _keyValueStore.Commit(blockNumber, node);
                }
                
                // TODO: little cheating for now - we may rename it to seal or finalize later

                // reset objects
                RootRef!.ResolveKey(this, true);
                SetRootHash(RootRef.Keccak!, true);
            }
            else
            {
                _keyValueStore.Commit(blockNumber, null);
            }
        }

        private void Commit(TrieNode node, bool isRoot)
        {
            if (_currentCommit is null)
            {
                throw new InvalidAsynchronousStateException(
                    $"{nameof(_currentCommit)} is NULL when calling {nameof(Commit)}");
            }
            
            if (_commitExceptions is null)
            {
                throw new InvalidAsynchronousStateException(
                    $"{nameof(_commitExceptions)} is NULL when calling {nameof(Commit)}");
            }
            
            if (node.IsBranch)
            {
                // idea from EthereumJ - testing parallel branches
                if (!_parallelBranches || !isRoot)
                {
                    for (int i = 0; i < 16; i++)
                    {
                        if (node.IsChildDirty(i))
                        {
                            Commit(node.GetChild(this, i)!, false);
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
                            nodesToCommit.Add(node.GetChild(this, i));
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
                                _commitExceptions!.Enqueue(e);
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
                TrieNode extensionChild = node.GetChild(this, 0);
                if (extensionChild is null)
                {
                    throw new InvalidOperationException("An attempt to store an extension without a child.");
                }

                if (extensionChild.IsDirty)
                {
                    Commit(extensionChild, false);
                }
            }

            node.ResolveKey(this, isRoot);
            node.Seal();

            if (node.FullRlp != null && node.FullRlp.Length >= 32)
            {
                NodeCache.Set(node.Keccak, node.FullRlp);
                _currentCommit.Enqueue(node);
            }
        }

        public void UpdateRootHash()
        {
            RootRef?.ResolveKey(this, true);
            SetRootHash(RootRef?.Keccak ?? EmptyTreeHash, false);
        }

        private void SetRootHash(Keccak? value, bool resetObjects)
        {
            _rootHash = value ?? Keccak.EmptyTreeHash; // nulls were allowed before so for now we leave it this way
            if (_rootHash == Keccak.EmptyTreeHash)
            {
                RootRef = null;
            }
            else if (resetObjects)
            {
                RootRef = GetUnknown(_rootHash);
            }
        }

        internal TrieNode GetUnknown(Keccak hash)
        {
            return _keyValueStore.FindCachedOrUnknown(hash);
        }

        [DebuggerStepThrough]
        public byte[]? Get(Span<byte> rawKey, Keccak? rootHash = null)
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
            if(_logger.IsTrace)
                _logger.Trace($"Setting {rawKey.ToHexString()} = {value.ToHexString()}");
            
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
        public void Set(Span<byte> rawKey, Rlp? value)
        {
            Set(rawKey, value == null ? Array.Empty<byte>() : value.Bytes);
        }

        private byte[]? Run(Span<byte> updatePath, int nibblesCount, byte[] updateValue, bool isUpdate, bool ignoreMissingDelete = true, Keccak? rootHash = null)
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
                TrieNode rootRef = GetUnknown(rootHash);
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

                HexPrefix key = HexPrefix.Leaf(updatePath.Slice(0, nibblesCount).ToArray());
                RootRef = TrieNodeFactory.CreateLeaf(key, updateValue);
                if(_logger.IsTrace)
                    _logger.Trace($"Incrementing refs on root {RootRef}");
                RootRef.Refs++;
                return updateValue;
            }

            RootRef.ResolveNode(this);
            TraverseContext traverseContext =
                new TraverseContext(updatePath.Slice(0, nibblesCount), updateValue, isUpdate, ignoreMissingDelete);
            return TraverseNode(RootRef, traverseContext);
        }

        private byte[]? TraverseNode(TrieNode node, TraverseContext traverseContext)
        {
            if(_logger.IsTrace) _logger.Trace(
                $"Traversing {node} to {(traverseContext.IsRead ? "READ" : traverseContext.IsUpdate ? "UPDATE" : "DELETE")}");
            
            return node.NodeType switch
            {
                NodeType.Branch => TraverseBranch(node, traverseContext),
                NodeType.Extension => TraverseExtension(node, traverseContext),
                NodeType.Leaf => TraverseLeaf(node, traverseContext),
                NodeType.Unknown => throw new InvalidOperationException(
                    $"Cannot traverse unresolved node {node.Keccak}"),
                _ => throw new NotSupportedException(
                    $"Unknown node type {node.NodeType}")
            };
        }

        private void ConnectNodes(TrieNode? node, TrieNode? previousHere)
        {
            bool isRoot = _nodeStack.Count == 0;
            TrieNode nextNode = node;

            if (previousHere != null)
            {
                if(_logger.IsTrace) _logger.Trace($"Decrementing ref on a node being disconnected {previousHere}");
                previousHere.Refs--;
            }

            while (!isRoot)
            {
                StackedNode parentOnStack = _nodeStack.Pop();
                node = parentOnStack.Node;

                isRoot = _nodeStack.Count == 0;

                if (node.IsLeaf)
                {
                    throw new TrieException(
                        $"{nameof(NodeType.Leaf)} {node} cannot be a parent of {nextNode}");
                }

                if (node.IsBranch)
                {
                    if (!(nextNode == null && !node.IsValidWithOneNodeLess))
                    {
                        if (node.IsSealed)
                        {
                            if(_logger.IsTrace) _logger.Trace($"Decrementing ref on disappearing branch {node}");
                            node.Refs--;
                            node = node.Clone();
                        }
                        
                        if (nextNode != null)
                        {
                            if(_logger.IsTrace)
                                _logger.Trace($"Incrementing refs after connecting {nextNode} to {node}");
                            nextNode.Refs++;
                        }

                        node.SetChild(parentOnStack.PathIndex, nextNode);

                        nextNode = node;
                    }
                    else
                    {
                        if (node.Value!.Length != 0)
                        {
                            // this only happens when we have branches with values
                            // which is not possible in the Ethereum protocol where keys are of equal lengths
                            // (it is possible in the more general trie definition)
                            TrieNode leafFromBranch = TrieNodeFactory.CreateLeaf(HexPrefix.Leaf(), node.Value);
                            if(_logger.IsTrace)
                                _logger.Trace($"Converting {node} into {leafFromBranch}");
                            if(_logger.IsTrace) _logger.Trace($"Decrementing ref on a branch turned leaf {node}");
                            node.Refs--;
                            nextNode = leafFromBranch;
                        }
                        else
                        {
                            /* all the cases below are when we have a branch that becomes something else
                               as a result of deleting one of the last two children */
                            /* case 1) - extension from branch
                               this is particularly interesting - we create an extension from
                               the implicit path in the branch children positions (marked as P) 
                               P B B B B B B B B B B B B B B B
                               B X - - - - - - - - - - - - - -
                               case 2) - extended extension
                               B B B B B B B B B B B B B B B B
                               E X - - - - - - - - - - - - - -
                               case 3) - extended leaf
                               B B B B B B B B B B B B B B B B
                               L X - - - - - - - - - - - - - - */

                            int childNodeIndex = 0;
                            for (int i = 0; i < 16; i++)
                            {
                                if (i != parentOnStack.PathIndex && !node.IsChildNull(i))
                                {
                                    childNodeIndex = i;
                                    break;
                                }
                            }

                            TrieNode childNode = node.GetChild(this, childNodeIndex);
                            if (childNode == null)
                            {
                                /* potential corrupted trie data state when we find a branch that has only one child */
                                throw new TrieException(
                                    "Before updating branch should have had at least two non-empty children");
                            }

                            childNode.ResolveNode(this);
                            if (childNode.IsBranch)
                            {
                                TrieNode extensionFromBranch =
                                    TrieNodeFactory.CreateExtension(
                                        HexPrefix.Extension((byte) childNodeIndex), childNode); // new line
                                if(_logger.IsTrace)
                                    _logger.Trace(
                                        $"Extending child {childNodeIndex} {childNode} of {node} into {extensionFromBranch}");
                                if (node.IsSealed)
                                {
                                    if(_logger.IsTrace) _logger.Trace($"Decrementing ref on a branch turned extension {node}");
                                    node.Refs--;
                                }

                                nextNode = extensionFromBranch; // new line
                            }
                            else if (childNode.IsExtension)
                            {
                                /* to test this case we need something like this initially */
                                /* R
                                   B B B B B B B B B B B B B B B B
                                   E L - - - - - - - - - - - - - -
                                   E - - - - - - - - - - - - - - -
                                   B B B B B B B B B B B B B B B B
                                   L L - - - - - - - - - - - - - - */

                                /* then we delete the leaf (marked as X) */
                                /* R
                                   B B B B B B B B B B B B B B B B
                                   E X - - - - - - - - - - - - - -
                                   E - - - - - - - - - - - - - - -
                                   B B B B B B B B B B B B B B B B
                                   L L - - - - - - - - - - - - - - */

                                /* and we end up with an extended extension (marked with +)
                                   replacing what was previously a top-level branch */
                                /* R
                                   +
                                   +
                                   + - - - - - - - - - - - - - - -
                                   B B B B B B B B B B B B B B B B
                                   L L - - - - - - - - - - - - - - */

                                HexPrefix newKey
                                    = HexPrefix.Extension(Bytes.Concat((byte) childNodeIndex, childNode.Path));
                                TrieNode extendedExtension = childNode.CloneWithChangedKey(newKey); // new line
                                if(_logger.IsTrace)
                                    _logger.Trace(
                                        $"Extending child {childNodeIndex} {childNode} of {node} into {extendedExtension}");
                                if(_logger.IsTrace) _logger.Trace($"Decrementing ref on an extension extended up to eat a branch {childNode}");
                                childNode.Refs--;
                                if(_logger.IsTrace) _logger.Trace($"Decrementing ref on a branch being replaced by an extension {node}");
                                node.Refs--;
                                // childNode.Key = newKey;
                                // childNode.IsDirty = true;
                                // nextNode = childNode;
                                nextNode = extendedExtension; // new line
                            }
                            else if (childNode.IsLeaf)
                            {
                                HexPrefix newKey = HexPrefix.Leaf(Bytes.Concat((byte) childNodeIndex, childNode.Path));
                                TrieNode extendedLeaf = childNode.CloneWithChangedKey(newKey); // new line
                                if(_logger.IsTrace)
                                    _logger.Trace(
                                        $"Extending branch child {childNodeIndex} {childNode} into {extendedLeaf}");
                                
                                if(_logger.IsTrace) _logger.Trace($"Decrementing ref on a leaf extended up to eat a branch {childNode}");
                                childNode.Refs--;
                                if (node.IsSealed)
                                {
                                    if(_logger.IsTrace) _logger.Trace($"Decrementing ref on a branch replaced by a leaf {node}");
                                    node.Refs--;
                                }

                                // childNode.Key = new HexPrefix(true, Bytes.Concat((byte) childNodeIndex, childNode.Path));
                                // childNode.IsDirty = true;
                                // nextNode = childNode;
                                nextNode = extendedLeaf; // new line
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
                        HexPrefix newKey = HexPrefix.Leaf(Bytes.Concat(node.Path, nextNode.Path));
                        TrieNode extendedLeaf = nextNode.CloneWithChangedKey(newKey); // new line
                        if(_logger.IsTrace)
                            _logger.Trace($"Combining {node} and {nextNode} into {extendedLeaf}");
                        // nextNode.Key = new HexPrefix(true, Bytes.Concat(node.Path, nextNode.Path));
                        if(_logger.IsTrace) _logger.Trace($"Decrementing ref on an extension being replaced by a longer leaf{node}");
                        node.Refs--;
                        if (nextNode.IsSealed)
                        {
                            if(_logger.IsTrace) _logger.Trace($"Decrementing ref on a leaf eating a parent extension {nextNode}");
                            nextNode.Refs--;
                        }

                        nextNode = extendedLeaf; // new line
                    }
                    else if (nextNode.IsExtension)
                    {
                        /* to test this case we need something like this initially */
                        /* R
                           E - - - - - - - - - - - - - - -
                           B B B B B B B B B B B B B B B B
                           E L - - - - - - - - - - - - - -
                           E - - - - - - - - - - - - - - -
                           B B B B B B B B B B B B B B B B
                           L L - - - - - - - - - - - - - - */

                        /* then we delete the leaf (marked as X) */
                        /* R
                           B B B B B B B B B B B B B B B B
                           E X - - - - - - - - - - - - - -
                           E - - - - - - - - - - - - - - -
                           B B B B B B B B B B B B B B B B
                           L L - - - - - - - - - - - - - - */

                        /* and we end up with an extended extension replacing what was previously a top-level branch*/
                        /* R
                           E
                           E
                           E - - - - - - - - - - - - - - -
                           B B B B B B B B B B B B B B B B
                           L L - - - - - - - - - - - - - - */

                        // nextNode.IsDirty = true;
                        // nextNode.Key = new HexPrefix(false, Bytes.Concat(node.Path, nextNode.Path));
                        HexPrefix newKey
                            = HexPrefix.Extension(Bytes.Concat(node.Path, nextNode.Path));
                        TrieNode extendedExtension = nextNode.CloneWithChangedKey(newKey); // new line
                        if(_logger.IsTrace)
                            _logger.Trace($"Combining {node} and {nextNode} into {extendedExtension}");
                        
                        if(_logger.IsTrace) _logger.Trace($"Decrementing ref on an extension extended up {node}");
                        node.Refs--;
                        if (nextNode.IsSealed)
                        {
                            if(_logger.IsTrace) _logger.Trace($"Decrementing ref on an extension extended down {nextNode}");
                            nextNode.Refs--;
                        }

                        nextNode = extendedExtension; // new line
                    }
                    else if (nextNode.IsBranch)
                    {
                        if (node.IsSealed)
                        {
                            if(_logger.IsTrace) _logger.Trace($"Decrementing ref on an extension which child is being replaced {node}");
                            node.Refs--;
                            node = node.Clone(); // new line    
                        }

                        if(_logger.IsTrace)
                            _logger.Trace($"Connecting {node} with {nextNode}");
                        node.SetChild(0, nextNode); // new line
                        if(_logger.IsTrace)
                            _logger.Trace($"Incrementing refs on {nextNode} connected to {node}");
                        nextNode.Refs++;

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

            if (nextNode != null)
            {
                if(_logger.IsTrace)
                    _logger.Trace($"Incrementing refs on a connected root {nextNode}");
                nextNode.Refs++;
            }

            RootRef = nextNode;
        }

        private byte[]? TraverseBranch(TrieNode node, TraverseContext traverseContext)
        {
            if (traverseContext.RemainingUpdatePathLength == 0)
            {
                /* all these cases when the path ends on the branch assume a trie with values in the branches
                   which is not possible within the Ethereum protocol which has keys of the same length (64) */

                if (traverseContext.IsRead)
                {
                    return node.Value;
                }

                if (traverseContext.IsDelete)
                {
                    if (node.Value == null)
                    {
                        return null;
                    }

                    ConnectNodes(null, node);
                }
                else if (Bytes.AreEqual(traverseContext.UpdateValue, node.Value))
                {
                    return traverseContext.UpdateValue;
                }
                else
                {
                    TrieNode withUpdatedValue = node.CloneWithChangedValue(traverseContext.UpdateValue); // new line
                    ConnectNodes(withUpdatedValue, node); // new line
                    // node.Value = traverseContext.UpdateValue;
                    // node.IsDirty = true;
                }

                return traverseContext.UpdateValue;
            }

            TrieNode childNode = node.GetChild(this, traverseContext.UpdatePath[traverseContext.CurrentIndex]);
            if (traverseContext.IsUpdate)
            {
                _nodeStack.Push(new StackedNode(node, traverseContext.UpdatePath[traverseContext.CurrentIndex]));
            }

            traverseContext.CurrentIndex++;

            if (childNode is null)
            {
                if (traverseContext.IsRead)
                {
                    return null;
                }

                if (traverseContext.IsDelete)
                {
                    if (traverseContext.IgnoreMissingDelete)
                    {
                        return null;
                    }

                    throw new TrieException(
                        $"Could not find the leaf node to delete: {traverseContext.UpdatePath.ToHexString(false)}");
                }

                byte[] leafPath = traverseContext.UpdatePath.Slice(
                    traverseContext.CurrentIndex,
                    traverseContext.UpdatePath.Length - traverseContext.CurrentIndex).ToArray();
                TrieNode leaf = TrieNodeFactory.CreateLeaf(HexPrefix.Leaf(leafPath), traverseContext.UpdateValue);
                ConnectNodes(leaf, null);

                return traverseContext.UpdateValue;
            }

            childNode.ResolveNode(this);
            TrieNode nextNode = childNode;
            return TraverseNode(nextNode, traverseContext);
        }

        private byte[]? TraverseLeaf(TrieNode node, TraverseContext traverseContext)
        {
            if (node.Path == null)
            {
                throw new InvalidDataException("An attempt to visit a node without a prefix path.");
            }
            
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

            int extensionLength = FindCommonPrefixLength(shorterPath, longerPath);
            if (extensionLength == shorterPath.Length && extensionLength == longerPath.Length)
            {
                if (traverseContext.IsRead)
                {
                    return node.Value;
                }

                if (traverseContext.IsDelete)
                {
                    ConnectNodes(null, node);
                    return traverseContext.UpdateValue;
                }

                if (!Bytes.AreEqual(node.Value, traverseContext.UpdateValue))
                {
                    TrieNode withUpdatedValue = node.CloneWithChangedValue(traverseContext.UpdateValue);
                    ConnectNodes(withUpdatedValue, node);
                    return traverseContext.UpdateValue;
                }

                return traverseContext.UpdateValue;
            }

            if (traverseContext.IsRead)
            {
                return null;
            }

            if (traverseContext.IsDelete)
            {
                if (traverseContext.IgnoreMissingDelete)
                {
                    return null;
                }

                throw new TrieException(
                    $"Could not find the leaf node to delete: {traverseContext.UpdatePath.ToHexString(false)}");
            }

            if (extensionLength != 0)
            {
                Span<byte> extensionPath = longerPath.Slice(0, extensionLength);
                TrieNode extension = TrieNodeFactory.CreateExtension(HexPrefix.Extension(extensionPath.ToArray()));
                _nodeStack.Push(new StackedNode(extension, 0));
            }

            TrieNode branch = TrieNodeFactory.CreateBranch();
            if (extensionLength == shorterPath.Length)
            {
                branch.Value = shorterPathValue;
            }
            else
            {
                Span<byte> shortLeafPath = shorterPath.Slice(extensionLength + 1, shorterPath.Length - extensionLength - 1);
                TrieNode shortLeaf = TrieNodeFactory.CreateLeaf(
                    HexPrefix.Leaf(shortLeafPath.ToArray()), shorterPathValue);
                if(_logger.IsTrace)
                    _logger.Trace($"Incrementing refs on new short leaf {shortLeaf}");
                shortLeaf.Refs++;
                branch.SetChild(shorterPath[extensionLength], shortLeaf);
            }

            Span<byte> leafPath = longerPath.Slice(extensionLength + 1, longerPath.Length - extensionLength - 1);
            TrieNode withUpdatedKeyAndValue = node.CloneWithChangedKeyAndValue(
                HexPrefix.Leaf(leafPath.ToArray()), longerPathValue);

            _nodeStack.Push(new StackedNode(branch, longerPath[extensionLength]));
            ConnectNodes(withUpdatedKeyAndValue, node);

            return traverseContext.UpdateValue;
        }

        private byte[]? TraverseExtension(TrieNode node, TraverseContext traverseContext)
        {
            if (node.Path == null)
            {
                throw new InvalidDataException("An attempt to visit a node without a prefix path.");
            }
            
            TrieNode originalNode = node;
            Span<byte> remaining = traverseContext.GetRemainingUpdatePath();
            
            int extensionLength = FindCommonPrefixLength(remaining, node.Path);
            if (extensionLength == node.Path.Length)
            {
                traverseContext.CurrentIndex += extensionLength;
                if (traverseContext.IsUpdate)
                {
                    _nodeStack.Push(new StackedNode(node, 0));
                }

                TrieNode next = node.GetChild(this, 0);
                if (next == null)
                {
                    throw new TrieException(
                        $"Found an {nameof(NodeType.Extension)} {node.Keccak} that is missing a child.");
                }
                
                next.ResolveNode(this);
                return TraverseNode(next, traverseContext);
            }

            if (traverseContext.IsRead)
            {
                return null;
            }

            if (traverseContext.IsDelete)
            {
                if (traverseContext.IgnoreMissingDelete)
                {
                    return null;
                }

                throw new TrieException(
                    $"Could find the leaf node to delete: {traverseContext.UpdatePath.ToHexString()}");
            }

            byte[] pathBeforeUpdate = node.Path;
            if (extensionLength != 0)
            {
                byte[] extensionPath = node.Path.Slice(0, extensionLength);
                node = node.CloneWithChangedKey(HexPrefix.Extension(extensionPath));
                // node.Key = new HexPrefix(false, extensionPath);
                // node.IsDirty = true;
                _nodeStack.Push(new StackedNode(node, 0));
            }

            TrieNode branch = TrieNodeFactory.CreateBranch();
            if (extensionLength == remaining.Length)
            {
                branch.Value = traverseContext.UpdateValue;
            }
            else
            {
                byte[] path = remaining.Slice(extensionLength + 1, remaining.Length - extensionLength - 1).ToArray();
                TrieNode shortLeaf = TrieNodeFactory.CreateLeaf(HexPrefix.Leaf(path), traverseContext.UpdateValue);
                if(_logger.IsTrace)
                    _logger.Trace($"Incrementing refs on new short leaf from extension {shortLeaf}");
                shortLeaf.Refs++;
                branch.SetChild(remaining[extensionLength], shortLeaf);
            }

            TrieNode originalNodeChild = originalNode.GetChild(this, 0);
            if (originalNodeChild is null)
            {
                throw new InvalidDataException(
                    $"Extension {originalNode.Keccak} has no child.");
            }
            
            if (pathBeforeUpdate.Length - extensionLength > 1)
            {
                byte[] extensionPath = pathBeforeUpdate.Slice(extensionLength + 1, pathBeforeUpdate.Length - extensionLength - 1);
                TrieNode secondExtension
                    = TrieNodeFactory.CreateExtension(HexPrefix.Extension(extensionPath), originalNodeChild);
                if(_logger.IsTrace)
                    _logger.Trace($"Incrementing refs on a child of the second extension {originalNodeChild}");
                originalNodeChild.Refs++;
                if(_logger.IsTrace)
                    _logger.Trace($"Incrementing refs on the second extension {secondExtension}");
                secondExtension.Refs++;
                branch.SetChild(pathBeforeUpdate[extensionLength], secondExtension);
            }
            else
            {
                TrieNode childNode = originalNodeChild;
                if(_logger.IsTrace)
                    _logger.Trace($"Incrementing refs on a child of the new brnch from extension {childNode}");
                childNode!.Refs++;
                branch.SetChild(pathBeforeUpdate[extensionLength], childNode);
            }

            ConnectNodes(branch, originalNodeChild);
            return traverseContext.UpdateValue;
        }
        
        private static int FindCommonPrefixLength(Span<byte> shorterPath, Span<byte> longerPath)
        {
            int commonPrefixLength = 0;
            int maxLength = Math.Min(shorterPath.Length, longerPath.Length);
            for (int i = 0; i < maxLength && shorterPath[i] == longerPath[i]; i++, commonPrefixLength++)
            {
                // just finding the common part of the path
            }

            return commonPrefixLength;
        }

        private ref struct TraverseContext
        {
            public Span<byte> UpdatePath { get; }
            public byte[]? UpdateValue { get; }
            public bool IsUpdate { get; }
            public bool IsRead => !IsUpdate;
            public bool IsDelete => IsUpdate && UpdateValue == null;
            public bool IgnoreMissingDelete { get; }
            public int CurrentIndex { get; set; }
            public int RemainingUpdatePathLength => UpdatePath.Length - CurrentIndex;

            public Span<byte> GetRemainingUpdatePath()
            {
                return UpdatePath.Slice(CurrentIndex, RemainingUpdatePathLength);
            }

            public TraverseContext(
                Span<byte> updatePath,
                byte[]? updateValue,
                bool isUpdate,
                bool ignoreMissingDelete = true)
            {
                UpdatePath = updatePath;
                UpdateValue = updateValue;
                IsUpdate = isUpdate;
                IgnoreMissingDelete = ignoreMissingDelete;
                CurrentIndex = 0;
            }
        }

        private readonly struct StackedNode
        {
            public StackedNode(TrieNode node, int pathIndex)
            {
                Node = node;
                PathIndex = pathIndex;
            }

            public TrieNode Node { get; }
            public int PathIndex { get; }

            public override string ToString()
            {
                return $"{PathIndex} {Node}";
            }
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
                rootRef = RootHash == rootHash ? RootRef : GetUnknown(rootHash);
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

        public TrieNode FindCachedOrUnknown(Keccak hash)
        {
            return _keyValueStore.FindCachedOrUnknown(hash);
        }

        public byte[] LoadRlp(Keccak hash, bool allowCaching)
        {
            return _keyValueStore.LoadRlp(hash, allowCaching);
        }
    }
}