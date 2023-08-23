// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie
{
    [DebuggerDisplay("{RootHash}")]
    public class PatriciaTree
    {
        private const int MaxKeyStackAlloc = 64;
        private readonly ILogger _logger;

        public const int OneNodeAvgMemoryEstimate = 384;

        /// <summary>
        ///     0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421
        /// </summary>
        public static readonly Keccak EmptyTreeHash = Keccak.EmptyTreeHash;

        public TrieType TrieType { get; init; }

        /// <summary>
        /// To save allocations this used to be static but this caused one of the hardest to reproduce issues
        /// when we decided to run some of the tree operations in parallel.
        /// </summary>
        private readonly Stack<StackedNode> _nodeStack = new();

        private readonly ConcurrentQueue<Exception>? _commitExceptions;

        private readonly ConcurrentQueue<NodeCommitInfo>? _currentCommit;

        public ITrieStore TrieStore { get; }
        public ICappedArrayPool? _bufferPool;

        private readonly bool _parallelBranches;

        private readonly bool _allowCommits;

        private Keccak _rootHash = Keccak.EmptyTreeHash;

        public TrieNode? RootRef { get; set; }

        /// <summary>
        /// Only used in EthereumTests
        /// </summary>
        internal TrieNode? Root
        {
            get
            {
                RootRef?.ResolveNode(TrieStore);
                return RootRef;
            }
        }

        public Keccak RootHash
        {
            get => _rootHash;
            set => SetRootHash(value, true);
        }

        public PatriciaTree()
            : this(NullTrieStore.Instance, EmptyTreeHash, false, true, NullLogManager.Instance)
        {
        }

        public PatriciaTree(IKeyValueStoreWithBatching keyValueStore)
            : this(keyValueStore, EmptyTreeHash, false, true, NullLogManager.Instance)
        {
        }

        public PatriciaTree(ITrieStore trieStore, ILogManager logManager, ICappedArrayPool? bufferPool = null)
            : this(trieStore, EmptyTreeHash, false, true, logManager, bufferPool: bufferPool)
        {
        }

        public PatriciaTree(
            IKeyValueStoreWithBatching keyValueStore,
            Keccak rootHash,
            bool parallelBranches,
            bool allowCommits,
            ILogManager logManager,
            ICappedArrayPool? bufferPool = null)
            : this(
                new TrieStore(keyValueStore, logManager),
                rootHash,
                parallelBranches,
                allowCommits,
                logManager,
                bufferPool: bufferPool)
        {
        }

        public PatriciaTree(
            ITrieStore? trieStore,
            Keccak rootHash,
            bool parallelBranches,
            bool allowCommits,
            ILogManager? logManager,
            ICappedArrayPool? bufferPool = null)
        {
            _logger = logManager?.GetClassLogger<PatriciaTree>() ?? throw new ArgumentNullException(nameof(logManager));
            TrieStore = trieStore ?? throw new ArgumentNullException(nameof(trieStore));
            _parallelBranches = parallelBranches;
            _allowCommits = allowCommits;
            RootHash = rootHash;

            // TODO: cannot do that without knowing whether the owning account is persisted or not
            // RootRef?.MarkPersistedRecursively(_logger);

            if (_allowCommits)
            {
                _currentCommit = new ConcurrentQueue<NodeCommitInfo>();
                _commitExceptions = new ConcurrentQueue<Exception>();
            }

            _bufferPool = bufferPool;
        }

        public void Commit(long blockNumber, bool skipRoot = false, WriteFlags writeFlags = WriteFlags.None)
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

            if (RootRef is not null && RootRef.IsDirty)
            {
                Commit(new NodeCommitInfo(RootRef), skipSelf: skipRoot);
                while (_currentCommit.TryDequeue(out NodeCommitInfo node))
                {
                    if (_logger.IsTrace) _logger.Trace($"Committing {node} in {blockNumber}");
                    TrieStore.CommitNode(blockNumber, node, writeFlags: writeFlags);
                }

                // reset objects
                RootRef!.ResolveKey(TrieStore, true, bufferPool: _bufferPool);
                SetRootHash(RootRef.Keccak!, true);
            }

            TrieStore.FinishBlockCommit(TrieType, blockNumber, RootRef, writeFlags);
            if (_logger.IsDebug) _logger.Debug($"Finished committing block {blockNumber}");
        }

        private void Commit(NodeCommitInfo nodeCommitInfo, bool skipSelf = false)
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

            TrieNode node = nodeCommitInfo.Node;
            if (node!.IsBranch)
            {
                // idea from EthereumJ - testing parallel branches
                if (!_parallelBranches || !nodeCommitInfo.IsRoot)
                {
                    for (int i = 0; i < 16; i++)
                    {
                        if (node.IsChildDirty(i))
                        {
                            Commit(new NodeCommitInfo(node.GetChild(TrieStore, i)!, node, i));
                        }
                        else
                        {
                            if (_logger.IsTrace)
                            {
                                TrieNode child = node.GetChild(TrieStore, i);
                                if (child is not null)
                                {
                                    _logger.Trace($"Skipping commit of {child}");
                                }
                            }
                        }
                    }
                }
                else
                {
                    List<NodeCommitInfo> nodesToCommit = new();
                    for (int i = 0; i < 16; i++)
                    {
                        if (node.IsChildDirty(i))
                        {
                            nodesToCommit.Add(new NodeCommitInfo(node.GetChild(TrieStore, i)!, node, i));
                        }
                        else
                        {
                            if (_logger.IsTrace)
                            {
                                TrieNode child = node.GetChild(TrieStore, i);
                                if (child is not null)
                                {
                                    _logger.Trace($"Skipping commit of {child}");
                                }
                            }
                        }
                    }

                    if (nodesToCommit.Count >= 4)
                    {
                        _commitExceptions.Clear();
                        Parallel.For(0, nodesToCommit.Count, i =>
                        {
                            try
                            {
                                Commit(nodesToCommit[i]);
                            }
                            catch (Exception e)
                            {
                                _commitExceptions!.Enqueue(e);
                            }
                        });

                        if (!_commitExceptions.IsEmpty)
                        {
                            throw new AggregateException(_commitExceptions);
                        }
                    }
                    else
                    {
                        for (int i = 0; i < nodesToCommit.Count; i++)
                        {
                            Commit(nodesToCommit[i]);
                        }
                    }
                }
            }
            else if (node.NodeType == NodeType.Extension)
            {
                TrieNode extensionChild = node.GetChild(TrieStore, 0);
                if (extensionChild is null)
                {
                    throw new InvalidOperationException("An attempt to store an extension without a child.");
                }

                if (extensionChild.IsDirty)
                {
                    Commit(new NodeCommitInfo(extensionChild, node, 0));
                }
                else
                {
                    if (_logger.IsTrace) _logger.Trace($"Skipping commit of {extensionChild}");
                }
            }

            node.ResolveKey(TrieStore, nodeCommitInfo.IsRoot, bufferPool: _bufferPool);
            node.Seal();

            if (node.FullRlp.Length >= 32)
            {
                if (!skipSelf)
                {
                    _currentCommit.Enqueue(nodeCommitInfo);
                }
            }
            else
            {
                if (_logger.IsTrace) _logger.Trace($"Skipping commit of an inlined {node}");
            }
        }

        public void UpdateRootHash()
        {
            RootRef?.ResolveKey(TrieStore, true, bufferPool: _bufferPool);
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
                RootRef = TrieStore.FindCachedOrUnknown(_rootHash);
            }
        }

        [SkipLocalsInit]
        [DebuggerStepThrough]
        public virtual byte[]? Get(ReadOnlySpan<byte> rawKey, Keccak? rootHash = null)
        {
            try
            {
                int nibblesCount = 2 * rawKey.Length;
                byte[] array = null;
                Span<byte> nibbles = (rawKey.Length <= MaxKeyStackAlloc
                        ? stackalloc byte[MaxKeyStackAlloc]
                        : array = ArrayPool<byte>.Shared.Rent(nibblesCount))
                    [..nibblesCount]; // Slice to exact size;

                try
                {
                    Nibbles.BytesToNibbleBytes(rawKey, nibbles);
                    return Run(nibbles, nibblesCount, new CappedArray<byte>(Array.Empty<byte>()), false, startRootHash: rootHash).ToArray();
                }
                finally
                {
                    if (array is not null) ArrayPool<byte>.Shared.Return(array);
                }
            }
            catch (TrieException e)
            {
                EnhanceException(rawKey, rootHash ?? RootHash, e);
                throw;
            }
        }

        private static void EnhanceException(ReadOnlySpan<byte> rawKey, ValueKeccak rootHash, TrieException baseException)
        {
            TrieNodeException? GetTrieNodeException(TrieException? exception) =>
                exception switch
                {
                    null => null,
                    TrieNodeException ex => ex,
                    _ => GetTrieNodeException(exception.InnerException as TrieException)
                };

            TrieNodeException? trieNodeException = GetTrieNodeException(baseException);
            if (trieNodeException is not null)
            {
                trieNodeException.EnhancedMessage = trieNodeException.NodeHash == rootHash
                    ? $"Failed to load root hash {rootHash} while loading key {rawKey.ToHexString()}."
                    : $"Failed to load key {rawKey.ToHexString()} from root hash {rootHash}.";
            }
        }

        [SkipLocalsInit]
        [DebuggerStepThrough]
        public virtual void Set(ReadOnlySpan<byte> rawKey, byte[] value)
        {
            Set(rawKey, new CappedArray<byte>(value));
        }

        [SkipLocalsInit]
        [DebuggerStepThrough]
        public virtual void Set(ReadOnlySpan<byte> rawKey, CappedArray<byte> value)
        {
            if (_logger.IsTrace)
                _logger.Trace($"{(value.Length == 0 ? $"Deleting {rawKey.ToHexString()}" : $"Setting {rawKey.ToHexString()} = {value.AsSpan().ToHexString()}")}");

            int nibblesCount = 2 * rawKey.Length;
            byte[] array = null;
            Span<byte> nibbles = (rawKey.Length <= MaxKeyStackAlloc
                    ? stackalloc byte[MaxKeyStackAlloc] // Fixed size stack allocation
                    : array = ArrayPool<byte>.Shared.Rent(nibblesCount))
                [..nibblesCount]; // Slice to exact size

            try
            {
                Nibbles.BytesToNibbleBytes(rawKey, nibbles);
                Run(nibbles, nibblesCount, value, true);
            }
            finally
            {
                if (array is not null) ArrayPool<byte>.Shared.Return(array);
            }
        }

        [DebuggerStepThrough]
        public void Set(ReadOnlySpan<byte> rawKey, Rlp? value)
        {
            Set(rawKey, value is null ? Array.Empty<byte>() : value.Bytes);
        }

        private CappedArray<byte> Run(
            Span<byte> updatePath,
            int nibblesCount,
            CappedArray<byte> updateValue,
            bool isUpdate,
            bool ignoreMissingDelete = true,
            Keccak? startRootHash = null)
        {
            if (isUpdate && startRootHash is not null)
            {
                throw new InvalidOperationException("Only reads can be done in parallel on the Patricia tree");
            }

#if DEBUG
            if (nibblesCount != updatePath.Length)
            {
                throw new Exception("Does it ever happen?");
            }
#endif

            TraverseContext traverseContext =
                new(updatePath[..nibblesCount], updateValue, isUpdate, ignoreMissingDelete);

            // lazy stack cleaning after the previous update
            if (traverseContext.IsUpdate)
            {
                _nodeStack.Clear();
            }

            CappedArray<byte> result;
            if (startRootHash is not null)
            {
                if (_logger.IsTrace) _logger.Trace($"Starting from {startRootHash} - {traverseContext.ToString()}");
                TrieNode startNode = TrieStore.FindCachedOrUnknown(startRootHash);
                ResolveNode(startNode, in traverseContext);
                result = TraverseNode(startNode, in traverseContext);
            }
            else
            {
                bool trieIsEmpty = RootRef is null;
                if (trieIsEmpty)
                {
                    if (traverseContext.UpdateValue.IsNotNull)
                    {
                        if (_logger.IsTrace) _logger.Trace($"Setting new leaf node with value {traverseContext.UpdateValue}");
                        byte[] key = updatePath[..nibblesCount].ToArray();
                        RootRef = TrieNodeFactory.CreateLeaf(key, traverseContext.UpdateValue);
                    }

                    if (_logger.IsTrace) _logger.Trace($"Keeping the root as null in {traverseContext.ToString()}");
                    result = traverseContext.UpdateValue;
                }
                else
                {
                    ResolveNode(RootRef, in traverseContext);
                    if (_logger.IsTrace) _logger.Trace($"{traverseContext.ToString()}");
                    result = TraverseNode(RootRef, in traverseContext);
                }
            }

            return result;
        }

        private void ResolveNode(TrieNode node, in TraverseContext traverseContext)
        {
            try
            {
                node.ResolveNode(TrieStore);
            }
            catch (TrieNodeException e)
            {
                ThrowMissingTrieNodeException(in traverseContext, e);
            }
        }

        private CappedArray<byte> TraverseNode(TrieNode node, in TraverseContext traverseContext)
        {
            if (_logger.IsTrace)
                _logger.Trace(
                    $"Traversing {node} to {(traverseContext.IsRead ? "READ" : traverseContext.IsDelete ? "DELETE" : "UPDATE")}");

            return node.NodeType switch
            {
                NodeType.Branch => TraverseBranch(node, in traverseContext),
                NodeType.Extension => TraverseExtension(node, in traverseContext),
                NodeType.Leaf => TraverseLeaf(node, in traverseContext),
                NodeType.Unknown => throw new InvalidOperationException(
                    $"Cannot traverse unresolved node {node.Keccak}"),
                _ => throw new NotSupportedException(
                    $"Unknown node type {node.NodeType}")
            };
        }

        private void ConnectNodes(TrieNode? node, in TraverseContext traverseContext)
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
                    throw new TrieException(
                        $"{nameof(NodeType.Leaf)} {node} cannot be a parent of {nextNode}");
                }

                if (node.IsBranch)
                {
                    if (!(nextNode is null && !node.IsValidWithOneNodeLess))
                    {
                        if (node.IsSealed)
                        {
                            node = node.Clone();
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
                            TrieNode leafFromBranch = TrieNodeFactory.CreateLeaf(Array.Empty<byte>(), node.Value);
                            if (_logger.IsTrace) _logger.Trace($"Converting {node} into {leafFromBranch}");
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

                            TrieNode childNode = node.GetChild(TrieStore, childNodeIndex);
                            if (childNode is null)
                            {
                                /* potential corrupted trie data state when we find a branch that has only one child */
                                throw new TrieException(
                                    "Before updating branch should have had at least two non-empty children");
                            }

                            ResolveNode(childNode, in traverseContext);
                            if (childNode.IsBranch)
                            {
                                TrieNode extensionFromBranch =
                                    TrieNodeFactory.CreateExtension(new[] { (byte)childNodeIndex }, childNode);
                                if (_logger.IsTrace)
                                    _logger.Trace(
                                        $"Extending child {childNodeIndex} {childNode} of {node} into {extensionFromBranch}");

                                nextNode = extensionFromBranch;
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

                                byte[] newKey = Bytes.Concat((byte)childNodeIndex, childNode.Key);

                                TrieNode extendedExtension = childNode.CloneWithChangedKey(newKey);
                                if (_logger.IsTrace)
                                    _logger.Trace(
                                        $"Extending child {childNodeIndex} {childNode} of {node} into {extendedExtension}");
                                nextNode = extendedExtension;
                            }
                            else if (childNode.IsLeaf)
                            {
                                byte[] newKey = Bytes.Concat((byte)childNodeIndex, childNode.Key);

                                TrieNode extendedLeaf = childNode.CloneWithChangedKey(newKey);
                                if (_logger.IsTrace)
                                    _logger.Trace(
                                        $"Extending branch child {childNodeIndex} {childNode} into {extendedLeaf}");

                                if (_logger.IsTrace) _logger.Trace($"Decrementing ref on a leaf extended up to eat a branch {childNode}");
                                if (node.IsSealed)
                                {
                                    if (_logger.IsTrace) _logger.Trace($"Decrementing ref on a branch replaced by a leaf {node}");
                                }

                                nextNode = extendedLeaf;
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
                    if (nextNode is null)
                    {
                        throw new InvalidOperationException(
                            $"An attempt to set a null node as a child of the {node}");
                    }

                    if (nextNode.IsLeaf)
                    {
                        byte[] newKey = Bytes.Concat(node.Key, nextNode.Key);
                        TrieNode extendedLeaf = nextNode.CloneWithChangedKey(newKey);
                        if (_logger.IsTrace)
                            _logger.Trace($"Combining {node} and {nextNode} into {extendedLeaf}");

                        nextNode = extendedLeaf;
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

                        byte[] newKey = Bytes.Concat(node.Key, nextNode.Key);
                        TrieNode extendedExtension = nextNode.CloneWithChangedKey(newKey);
                        if (_logger.IsTrace)
                            _logger.Trace($"Combining {node} and {nextNode} into {extendedExtension}");

                        nextNode = extendedExtension;
                    }
                    else if (nextNode.IsBranch)
                    {
                        if (node.IsSealed)
                        {
                            node = node.Clone();
                        }

                        if (_logger.IsTrace) _logger.Trace($"Connecting {node} with {nextNode}");
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

        private CappedArray<byte> TraverseBranch(TrieNode node, in TraverseContext traverseContext)
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
                    if (node.Value.IsNull)
                    {
                        return null;
                    }

                    ConnectNodes(null, in traverseContext);
                }
                else if (Bytes.AreEqual(traverseContext.UpdateValue, node.Value))
                {
                    return traverseContext.UpdateValue;
                }
                else
                {
                    TrieNode withUpdatedValue = node.CloneWithChangedValue(traverseContext.UpdateValue);
                    ConnectNodes(withUpdatedValue, in traverseContext);
                }

                return traverseContext.UpdateValue;
            }

            TrieNode childNode = node.GetChild(TrieStore, traverseContext.UpdatePath[traverseContext.CurrentIndex]);
            if (traverseContext.IsUpdate)
            {
                _nodeStack.Push(new StackedNode(node, traverseContext.UpdatePath[traverseContext.CurrentIndex]));
            }

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

                    ThrowMissingLeafException(in traverseContext);
                }

                int currentIndex = traverseContext.CurrentIndex + 1;
                byte[] leafPath = traverseContext.UpdatePath[
                    currentIndex..].ToArray();
                TrieNode leaf = TrieNodeFactory.CreateLeaf(leafPath, traverseContext.UpdateValue);
                ConnectNodes(leaf, in traverseContext);

                return traverseContext.UpdateValue;
            }

            ResolveNode(childNode, in traverseContext);
            TrieNode nextNode = childNode;

            return TraverseNext(in traverseContext, 1, nextNode);
        }

        private CappedArray<byte> TraverseLeaf(TrieNode node, in TraverseContext traverseContext)
        {
            if (node.Key is null)
            {
                ThrowMissingPrefixException();
            }

            ReadOnlySpan<byte> remaining = traverseContext.GetRemainingUpdatePath();
            ReadOnlySpan<byte> shorterPath;
            ReadOnlySpan<byte> longerPath;
            if (traverseContext.RemainingUpdatePathLength - node.Key.Length < 0)
            {
                shorterPath = remaining;
                longerPath = node.Key;
            }
            else
            {
                shorterPath = node.Key;
                longerPath = remaining;
            }

            CappedArray<byte> shorterPathValue;
            CappedArray<byte> longerPathValue;

            if (Bytes.AreEqual(shorterPath, node.Key))
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
                    ConnectNodes(null, in traverseContext);
                    return traverseContext.UpdateValue;
                }

                if (!Bytes.AreEqual(node.Value, traverseContext.UpdateValue))
                {
                    TrieNode withUpdatedValue = node.CloneWithChangedValue(traverseContext.UpdateValue);
                    ConnectNodes(withUpdatedValue, in traverseContext);
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

                ThrowMissingLeafException(in traverseContext);
            }

            if (extensionLength != 0)
            {
                ReadOnlySpan<byte> extensionPath = longerPath[..extensionLength];
                TrieNode extension = TrieNodeFactory.CreateExtension(extensionPath.ToArray());
                _nodeStack.Push(new StackedNode(extension, 0));
            }

            TrieNode branch = TrieNodeFactory.CreateBranch();
            if (extensionLength == shorterPath.Length)
            {
                branch.Value = shorterPathValue;
            }
            else
            {
                ReadOnlySpan<byte> shortLeafPath = shorterPath.Slice(extensionLength + 1, shorterPath.Length - extensionLength - 1);
                TrieNode shortLeaf = TrieNodeFactory.CreateLeaf(shortLeafPath.ToArray(), shorterPathValue);
                branch.SetChild(shorterPath[extensionLength], shortLeaf);
            }

            ReadOnlySpan<byte> leafPath = longerPath.Slice(extensionLength + 1, longerPath.Length - extensionLength - 1);
            TrieNode withUpdatedKeyAndValue = node.CloneWithChangedKeyAndValue(
                leafPath.ToArray(), longerPathValue);

            _nodeStack.Push(new StackedNode(branch, longerPath[extensionLength]));
            ConnectNodes(withUpdatedKeyAndValue, in traverseContext);

            return traverseContext.UpdateValue;
        }

        private CappedArray<byte> TraverseExtension(TrieNode node, in TraverseContext traverseContext)
        {
            if (node.Key is null)
            {
                ThrowMissingPrefixException();
            }

            TrieNode originalNode = node;
            ReadOnlySpan<byte> remaining = traverseContext.GetRemainingUpdatePath();

            int extensionLength = FindCommonPrefixLength(remaining, node.Key);
            if (extensionLength == node.Key.Length)
            {
                if (traverseContext.IsUpdate)
                {
                    _nodeStack.Push(new StackedNode(node, 0));
                }

                TrieNode next = node.GetChild(TrieStore, 0);
                if (next is null)
                {
                    ThrowMissingChildException(node);
                }

                ResolveNode(next, in traverseContext);

                return TraverseNext(in traverseContext, extensionLength, next);
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

                ThrowMissingLeafException(in traverseContext);
            }

            byte[] pathBeforeUpdate = node.Key;
            if (extensionLength != 0)
            {
                byte[] extensionPath = node.Key.Slice(0, extensionLength);
                node = node.CloneWithChangedKey(extensionPath);
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
                TrieNode shortLeaf = TrieNodeFactory.CreateLeaf(path, traverseContext.UpdateValue);
                branch.SetChild(remaining[extensionLength], shortLeaf);
            }

            TrieNode originalNodeChild = originalNode.GetChild(TrieStore, 0);
            if (originalNodeChild is null)
            {
                ThrowInvalidDataException(originalNode);
            }

            if (pathBeforeUpdate.Length - extensionLength > 1)
            {
                byte[] extensionPath = pathBeforeUpdate.Slice(extensionLength + 1, pathBeforeUpdate.Length - extensionLength - 1);
                TrieNode secondExtension
                    = TrieNodeFactory.CreateExtension(extensionPath, originalNodeChild);
                branch.SetChild(pathBeforeUpdate[extensionLength], secondExtension);
            }
            else
            {
                TrieNode childNode = originalNodeChild;
                branch.SetChild(pathBeforeUpdate[extensionLength], childNode);
            }

            ConnectNodes(branch, in traverseContext);
            return traverseContext.UpdateValue;
        }

        private CappedArray<byte> TraverseNext(in TraverseContext traverseContext, int extensionLength, TrieNode next)
        {
            // Move large struct creation out of flow so doesn't force additional stack space
            // in calling method even if not used
            TraverseContext newContext = traverseContext.WithNewIndex(traverseContext.CurrentIndex + extensionLength);
            return TraverseNode(next, in newContext);
        }

        private static int FindCommonPrefixLength(ReadOnlySpan<byte> shorterPath, ReadOnlySpan<byte> longerPath)
        {
            int commonPrefixLength = 0;
            int maxLength = Math.Min(shorterPath.Length, longerPath.Length);
            for (int i = 0; i < maxLength && shorterPath[i] == longerPath[i]; i++, commonPrefixLength++)
            {
                // just finding the common part of the path
            }

            return commonPrefixLength;
        }

        private readonly ref struct TraverseContext
        {
            public CappedArray<byte> UpdateValue { get; }
            public ReadOnlySpan<byte> UpdatePath { get; }
            public bool IsUpdate { get; }
            public bool IsRead => !IsUpdate;
            public bool IsDelete => IsUpdate && UpdateValue.IsNull;
            public bool IgnoreMissingDelete { get; }
            public int CurrentIndex { get; }
            public int RemainingUpdatePathLength => UpdatePath.Length - CurrentIndex;

            public TraverseContext WithNewIndex(int index)
            {
                return new TraverseContext(in this, index);
            }

            public ReadOnlySpan<byte> GetRemainingUpdatePath()
            {
                return UpdatePath.Slice(CurrentIndex, RemainingUpdatePathLength);
            }

            public TraverseContext(scoped in TraverseContext context, int index)
            {
                this = context;
                CurrentIndex = index;
            }

            public TraverseContext(
                Span<byte> updatePath,
                CappedArray<byte> updateValue,
                bool isUpdate,
                bool ignoreMissingDelete = true)
            {
                UpdatePath = updatePath;
                if (updateValue.IsNotNull && updateValue.Array?.Length == 0)
                {
                    updateValue = new CappedArray<byte>(null);
                }

                UpdateValue = updateValue;
                IsUpdate = isUpdate;
                IgnoreMissingDelete = ignoreMissingDelete;
                CurrentIndex = 0;
            }

            public override string ToString()
            {
                return $"{(IsDelete ? "DELETE" : IsUpdate ? "UPDATE" : "READ")} {UpdatePath.ToHexString()}{(IsRead ? string.Empty : $" -> {UpdateValue}")}";
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

        public void Accept(ITreeVisitor visitor, Keccak rootHash, VisitingOptions? visitingOptions = null)
        {
            if (visitor is null) throw new ArgumentNullException(nameof(visitor));
            if (rootHash is null) throw new ArgumentNullException(nameof(rootHash));
            visitingOptions ??= VisitingOptions.Default;

            using TrieVisitContext trieVisitContext = new()
            {
                // hacky but other solutions are not much better, something nicer would require a bit of thinking
                // we introduced a notion of an account on the visit context level which should have no knowledge of account really
                // but we know that we have multiple optimizations and assumptions on trees
                ExpectAccounts = visitingOptions.ExpectAccounts,
                MaxDegreeOfParallelism = visitingOptions.MaxDegreeOfParallelism
            };

            TrieNode rootRef = null;
            if (!rootHash.Equals(Keccak.EmptyTreeHash))
            {
                rootRef = RootHash == rootHash ? RootRef : TrieStore.FindCachedOrUnknown(rootHash);
                try
                {
                    rootRef!.ResolveNode(TrieStore);
                }
                catch (TrieException)
                {
                    visitor.VisitMissingNode(rootHash, trieVisitContext);
                    return;
                }
            }

            ITrieNodeResolver resolver = TrieStore;
            if (visitor.IsFullDbScan)
            {
                resolver = new TrieNodeResolverWithReadFlags(TrieStore, ReadFlags.HintCacheMiss);
            }

            visitor.VisitTree(rootHash, trieVisitContext);
            if (visitingOptions.FullScanMemoryBudget != 0)
            {
                BatchedTrieVisitor batchedTrieVisitor = new(visitor, resolver, visitingOptions);
                batchedTrieVisitor.Start(rootHash, trieVisitContext);
            }
            else
            {
                rootRef?.Accept(visitor, resolver, trieVisitContext);
            }
        }

        [DoesNotReturn]
        [StackTraceHidden]
        private static void ThrowInvalidDataException(TrieNode originalNode)
        {
            throw new InvalidDataException(
                $"Extension {originalNode.Keccak} has no child.");
        }

        [DoesNotReturn]
        [StackTraceHidden]
        private static void ThrowMissingChildException(TrieNode node)
        {
            throw new TrieException(
                $"Found an {nameof(NodeType.Extension)} {node.Keccak} that is missing a child.");
        }

        [DoesNotReturn]
        [StackTraceHidden]
        private static void ThrowMissingLeafException(in TraverseContext traverseContext)
        {
            throw new TrieException(
                $"Could not find the leaf node to delete: {traverseContext.UpdatePath.ToHexString()}");
        }

        [DoesNotReturn]
        [StackTraceHidden]
        private static void ThrowMissingPrefixException()
        {
            throw new InvalidDataException("An attempt to visit a node without a prefix path.");
        }

        [DoesNotReturn]
        [StackTraceHidden]
        private static void ThrowMissingTrieNodeException(in TraverseContext traverseContext, TrieNodeException e)
        {
            throw new MissingTrieNodeException(e.Message, e, traverseContext.UpdatePath.ToArray(), traverseContext.CurrentIndex);
        }
    }
}
