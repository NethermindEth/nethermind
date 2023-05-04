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
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie
{
    [DebuggerDisplay("{RootHash}")]
    public class PatriciaTree : IPatriciaTree
    {
        private readonly ILogger _logger;

        public const int OneNodeAvgMemoryEstimate = 384;
        public const int StoragePrefixLength = Keccak.Size + 1;
        public const int StorageKeyLength = Keccak.Size;

        /// <summary>
        ///     0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421
        /// </summary>
        public static readonly Keccak EmptyTreeHash = Keccak.EmptyTreeHash;
        public static readonly byte[] EmptyKeyPath = Array.Empty<byte>();

        public TrieType TrieType { get; set; }

        /// <summary>
        /// To save allocations this used to be static but this caused one of the hardest to reproduce issues
        /// when we decided to run some of the tree operations in parallel.
        /// </summary>
        private readonly Stack<StackedNode> _nodeStack = new();

        private readonly ConcurrentQueue<Exception>? _commitExceptions;

        private readonly ConcurrentQueue<NodeCommitInfo>? _currentCommit;

        /// <summary>
        /// In path based tree, we need to keep track of nodes that are to be deleted when the insertion
        /// operation is completed.
        /// </summary>
        private readonly ConcurrentQueue<TrieNode>? _deleteNodes;

        private readonly ITrieStore _trieStore;
        public TrieNodeResolverCapability Capability => _trieStore.Capability;

        private readonly bool _parallelBranches;

        private readonly bool _allowCommits;

        private Keccak _rootHash = Keccak.EmptyTreeHash;

        private TrieNode? _rootRef;


        /// <summary>
        /// In path based merkle tree, storage trees are separate merkle trees. When storing these trees
        /// by path, we get collisions with other nodes stored with the same path for other trees.
        /// Storage prefix is used to avoid this situation.
        /// This prefix is calculated by using the account leaf path and adding another byte to
        /// differentiate between the path to account leaf and storage root node.
        /// </summary>
        public byte[] StorageBytePathPrefix
        {
            set
            {
                StoreNibblePathPrefix = value.Length == 0 ? Array.Empty<byte>() : Nibbles.BytesToNibbleBytes(value);
            }
        }
        public byte[] StoreNibblePathPrefix { get; private set; }


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

        public ITrieStore TrieStore => _trieStore;

        public TrieNode? RootRef { get => _rootRef; set => _rootRef = value; }

        public PatriciaTree()
            : this(NullTrieStore.Instance, EmptyTreeHash, false, true, NullLogManager.Instance)
        {
        }

        public PatriciaTree(IKeyValueStoreWithBatching keyValueStore, TrieNodeResolverCapability capability = TrieNodeResolverCapability.Hash)
            : this(keyValueStore, EmptyTreeHash, false, true, NullLogManager.Instance, capability)
        {
        }

        public PatriciaTree(IKeyValueStoreWithBatching keyValueStore, ILogManager logManager, TrieNodeResolverCapability capability = TrieNodeResolverCapability.Hash)
            : this(keyValueStore, EmptyTreeHash, false, true, logManager, capability)
        {
        }

        public PatriciaTree(ITrieStore trieStore, ILogManager logManager)
            : this(trieStore, EmptyTreeHash, false, true, logManager)
        {
        }

        public PatriciaTree(
            IKeyValueStoreWithBatching keyValueStore,
            Keccak rootHash,
            bool parallelBranches,
            bool allowCommits,
            ILogManager logManager,
            TrieNodeResolverCapability capability = TrieNodeResolverCapability.Hash)
            : this(
                trieStore: capability switch
                {
                    TrieNodeResolverCapability.Hash => new TrieStore(keyValueStore, logManager),
                    TrieNodeResolverCapability.Path => new TrieStoreByPath(keyValueStore, logManager),
                    _ => throw new ArgumentOutOfRangeException(nameof(capability), capability, null)
                },
                rootHash,
                parallelBranches,
                allowCommits,
                logManager)
        {
        }

        public PatriciaTree(
            ITrieStore? trieStore,
            Keccak rootHash,
            bool parallelBranches,
            bool allowCommits,
            ILogManager? logManager)
        {
            _logger = logManager?.GetClassLogger(GetType()) ?? throw new ArgumentNullException(nameof(logManager));
            _trieStore = trieStore ?? throw new ArgumentNullException(nameof(trieStore));
            _parallelBranches = parallelBranches;
            _allowCommits = allowCommits;
            RootHash = rootHash;
            StoreNibblePathPrefix = Array.Empty<byte>();

            // TODO: cannot do that without knowing whether the owning account is persisted or not
            // RootRef?.MarkPersistedRecursively(_logger);

            if (_allowCommits)
            {
                _currentCommit = new ConcurrentQueue<NodeCommitInfo>();
                _commitExceptions = new ConcurrentQueue<Exception>();
                _deleteNodes = new ConcurrentQueue<TrieNode>();
            }
        }

        public void Commit(long blockNumber, bool skipRoot = false)
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
                // _logger.Info("Commiting Changes to TrieStore");
                Commit(new NodeCommitInfo(RootRef), skipSelf: skipRoot);
                while (_currentCommit.TryDequeue(out NodeCommitInfo node))
                {
                    if (_logger.IsTrace) _logger.Trace($"Committing {node} in {blockNumber}");
                    TrieStore.CommitNode(blockNumber, node);
                }

                // reset objects
                // TODO: why?
                RootRef!.ResolveKey(TrieStore, true);
                if (Capability == TrieNodeResolverCapability.Hash)
                {
                    SetRootHash(RootRef.Keccak!, true);
                }
                else
                {
                    SetRootHash(RootRef.Keccak!, false);
                }
            }

            TrieStore.FinishBlockCommit(TrieType, blockNumber, RootRef);
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

            // TODO: stcg
            while (_deleteNodes != null && _deleteNodes.TryDequeue(out TrieNode delNode))
            {
                _currentCommit.Enqueue(new NodeCommitInfo(delNode));
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

            node.ResolveKey(TrieStore, nodeCommitInfo.IsRoot);
            node.Seal();

            if (node.FullRlp?.Length >= 32)
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
            if (Capability == TrieNodeResolverCapability.Path)
                RootRef?.ResolveNode(TrieStore);
            RootRef?.ResolveKey(TrieStore, true);
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
                TrieNode? tempRoot = TrieStore.FindCachedOrUnknown(_rootHash, Array.Empty<byte>(), StoreNibblePathPrefix);
                try
                {
                    tempRoot.ResolveNode(TrieStore);
                    tempRoot.ResolveKey(TrieStore, true);
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                }
                finally
                {
                    RootRef = tempRoot.Keccak != _rootHash ? TrieStore.FindCachedOrUnknown(_rootHash, Array.Empty<byte>(), StoreNibblePathPrefix) : tempRoot;
                }


            }
        }

        private byte[]? GetInternal(Span<byte> rawKey, Keccak? rootHash = null)
        {
            try
            {
                int nibblesCount = 2 * rawKey.Length;
                byte[] array = null;
                Span<byte> nibbles = rawKey.Length <= 64
                    ? stackalloc byte[nibblesCount]
                    : array = ArrayPool<byte>.Shared.Rent(nibblesCount);
                Nibbles.BytesToNibbleBytes(rawKey, nibbles.Slice(0, nibblesCount));
                byte[]? result = Run(nibbles.Slice(0, nibblesCount), nibblesCount, Array.Empty<byte>(), false, startRootHash: rootHash);
                if (array is not null) ArrayPool<byte>.Shared.Return(array);
                return result;
            }
            catch (TrieException e)
            {
                throw new TrieException($"Failed to load key {rawKey.ToHexString()} from root hash {rootHash ?? RootHash}.", e);
            }
        }

        public byte[]? Get(Span<byte> rawKey, Keccak? rootHash = null)
        {
            return Capability switch
            {
                TrieNodeResolverCapability.Hash => GetInternal(rawKey, rootHash),
                TrieNodeResolverCapability.Path => GetInternal(rawKey, rootHash),
                // TrieNodeResolverCapability.Path => GetByPath(rawKey, rootHash),
                _ => throw new ArgumentOutOfRangeException()
            };
        }

        private byte[]? GetByPath(Span<byte> rawKey, Keccak? rootHash = null)
        {
            if (rootHash is null)
            {
                if (RootRef is null) return null;
                if (RootRef?.IsDirty == true)  return GetInternal(rawKey);
                rootHash = RootHash;
            }

            // try and get cached nodes
            Span<byte> nibbleBytes = stackalloc byte[StoreNibblePathPrefix.Length + rawKey.Length * 2];
            StoreNibblePathPrefix.CopyTo(nibbleBytes);
            Nibbles.BytesToNibbleBytes(rawKey, nibbleBytes[StoreNibblePathPrefix.Length..]);
            TrieNode? node = TrieStore.FindCachedOrUnknown(nibbleBytes[StoreNibblePathPrefix.Length..], StoreNibblePathPrefix, rootHash);

            if (node is null) return null;
            if (node.NodeType != NodeType.Leaf)
            {
                // if not in cached nodes - then check persisted nodes`
                byte[]? nodeData = TrieStore.TryLoadRlp(nibbleBytes, null);
                if (nodeData is null) return null;

                node = new TrieNode(NodeType.Unknown, nodeData);
                node.ResolveNode(TrieStore);
            }

            return node.Value;
        }


        public void Set(Span<byte> rawKey, byte[] value)
        {
            int nibblesCount = 2 * rawKey.Length;
            byte[] array = null;
            Span<byte> nibbles = rawKey.Length <= 64
                ? stackalloc byte[nibblesCount]
                : array = ArrayPool<byte>.Shared.Rent(nibblesCount);
            Nibbles.BytesToNibbleBytes(rawKey, nibbles.Slice(0, nibblesCount));
            Run(nibbles.Slice(0, nibblesCount), nibblesCount, value, true);
            if (array is not null) ArrayPool<byte>.Shared.Return(array);
        }

        public void Set(Span<byte> rawKey, Rlp? value)
        {
            Set(rawKey, value is null ? Array.Empty<byte>() : value.Bytes);
        }

        private byte[]? Run(
            Span<byte> updatePath,
            int nibblesCount,
            byte[]? updateValue,
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
                new(updatePath.Slice(0, nibblesCount), updateValue, isUpdate, ignoreMissingDelete);

            // lazy stack cleaning after the previous update
            if (traverseContext.IsUpdate)
            {
                _nodeStack.Clear();
            }

            byte[]? result;
            if (startRootHash is not null)
            {
                if (_logger.IsTrace) _logger.Trace($"Starting from {startRootHash} - {traverseContext.ToString()}");
                TrieNode startNode = TrieStore.FindCachedOrUnknown(startRootHash, Array.Empty<byte>(), StoreNibblePathPrefix);
                startNode.ResolveNode(TrieStore);
                result = TraverseNode(startNode, in traverseContext);
            }
            else
            {
                bool trieIsEmpty = RootRef is null;
                if (trieIsEmpty)
                {
                    if (traverseContext.UpdateValue is not null)
                    {
                        if (_logger.IsTrace) _logger.Trace($"Setting new leaf node with value {traverseContext.UpdateValue}");
                        byte[] key = updatePath.Slice(0, nibblesCount).ToArray();
                        RootRef = Capability switch
                        {
                            TrieNodeResolverCapability.Hash => TrieNodeFactory.CreateLeaf(key, traverseContext.UpdateValue),
                            TrieNodeResolverCapability.Path => TrieNodeFactory.CreateLeaf(key, traverseContext.UpdateValue, EmptyKeyPath, StoreNibblePathPrefix),
                            _ => throw new ArgumentOutOfRangeException()
                        };
                    }

                    if (_logger.IsTrace) _logger.Trace($"Keeping the root as null in {traverseContext.ToString()}");
                    result = traverseContext.UpdateValue;
                }
                else
                {
                    RootRef.ResolveNode(TrieStore);
                    if (_logger.IsTrace) _logger.Trace($"{traverseContext.ToString()}");
                    result = TraverseNode(RootRef, in traverseContext);
                }
            }

            return result;
        }

        private byte[]? TraverseNode(TrieNode node, in TraverseContext traverseContext)
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

        private void ConnectNodes(TrieNode? node)
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
                                // find the other child and should not be null
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

                            childNode.ResolveNode(TrieStore);
                            if (childNode.IsBranch)
                            {
                                TrieNode extensionFromBranch = Capability switch
                                {
                                    TrieNodeResolverCapability.Hash => TrieNodeFactory.CreateExtension(new[] { (byte)childNodeIndex }, childNode),
                                    TrieNodeResolverCapability.Path => TrieNodeFactory.CreateExtension(new[] { (byte)childNodeIndex }, childNode, node.PathToNode, StoreNibblePathPrefix),
                                    _ => throw new ArgumentOutOfRangeException()
                                };
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

                                TrieNode extendedExtension = Capability switch
                                {
                                    TrieNodeResolverCapability.Hash => childNode.CloneWithChangedKey(newKey),
                                    TrieNodeResolverCapability.Path => childNode.CloneWithChangedKey(newKey, 1),
                                    _ => throw new ArgumentOutOfRangeException()
                                };

                                if (_logger.IsTrace)
                                    _logger.Trace(
                                        $"Extending child {childNodeIndex} {childNode} of {node} into {extendedExtension}");
                                nextNode = extendedExtension;
                            }
                            else if (childNode.IsLeaf)
                            {
                                byte[] newKey = Bytes.Concat((byte)childNodeIndex, childNode.Key);
                                TrieNode extendedLeaf = Capability switch
                                {
                                    TrieNodeResolverCapability.Hash => childNode.CloneWithChangedKey(newKey),
                                    TrieNodeResolverCapability.Path => childNode.CloneWithChangedKey(newKey, 1),
                                    _ => throw new ArgumentOutOfRangeException()
                                };
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
                        TrieNode extendedLeaf = Capability switch
                        {
                            TrieNodeResolverCapability.Hash => nextNode.CloneWithChangedKey(newKey),
                            TrieNodeResolverCapability.Path => nextNode.CloneWithChangedKey(newKey, node.Key.Length),
                            _ => throw new ArgumentOutOfRangeException()
                        };
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
                        TrieNode extendedExtension = Capability switch
                        {
                            TrieNodeResolverCapability.Hash => nextNode.CloneWithChangedKey(newKey),
                            TrieNodeResolverCapability.Path => nextNode.CloneWithChangedKey(newKey, node.Key.Length),
                            _ => throw new ArgumentOutOfRangeException()
                        };
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
                if(Capability == TrieNodeResolverCapability.Path) _deleteNodes?.Enqueue(node.CloneNodeForDeletion());
            }

            RootRef = nextNode;
        }

        private byte[]? TraverseBranch(TrieNode node, in TraverseContext traverseContext)
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
                    if (node.Value is null)
                    {
                        return null;
                    }

                    if(Capability == TrieNodeResolverCapability.Path) _deleteNodes?.Enqueue(node.CloneNodeForDeletion());
                    ConnectNodes(null);
                }
                else if (Bytes.AreEqual(traverseContext.UpdateValue, node.Value))
                {
                    return traverseContext.UpdateValue;
                }
                else
                {
                    TrieNode withUpdatedValue = node.CloneWithChangedValue(traverseContext.UpdateValue);
                    ConnectNodes(withUpdatedValue);
                }

                return traverseContext.UpdateValue;
            }

            TrieNode childNode = Capability switch
            {
                TrieNodeResolverCapability.Hash => node.GetChild(TrieStore, traverseContext.UpdatePath[traverseContext.CurrentIndex]),
                TrieNodeResolverCapability.Path => node.GetChild(TrieStore, traverseContext.UpdatePath.Slice(0, traverseContext.CurrentIndex + 1).ToArray(), traverseContext.UpdatePath[traverseContext.CurrentIndex]),
                _ => throw new ArgumentOutOfRangeException()
            };

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
                byte[] leafPath = traverseContext.UpdatePath.Slice(
                    currentIndex,
                    traverseContext.UpdatePath.Length - currentIndex).ToArray();
                TrieNode leaf = Capability switch
                {
                    TrieNodeResolverCapability.Hash => TrieNodeFactory.CreateLeaf(leafPath, traverseContext.UpdateValue),
                    TrieNodeResolverCapability.Path => TrieNodeFactory.CreateLeaf(leafPath, traverseContext.UpdateValue, traverseContext.GetCurrentPath(currentIndex).ToArray(), StoreNibblePathPrefix),
                    _ => throw new ArgumentOutOfRangeException()
                };

                ConnectNodes(leaf);

                return traverseContext.UpdateValue;
            }

            childNode.ResolveNode(TrieStore);
            TrieNode nextNode = childNode;

            return TraverseNext(in traverseContext, 1, nextNode);
        }

        private byte[]? TraverseLeaf(TrieNode node, in TraverseContext traverseContext)
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

            byte[] shorterPathValue;
            byte[] longerPathValue;

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
                    if(Capability == TrieNodeResolverCapability.Path) _deleteNodes?.Enqueue(node.CloneNodeForDeletion());
                    ConnectNodes(null);
                    return traverseContext.UpdateValue;
                }

                if (!Bytes.AreEqual(node.Value, traverseContext.UpdateValue))
                {
                    TrieNode withUpdatedValue = node.CloneWithChangedValue(traverseContext.UpdateValue);
                    ConnectNodes(withUpdatedValue);
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
                ReadOnlySpan<byte> extensionPath = longerPath.Slice(0, extensionLength);

                TrieNode extension = Capability switch
                {
                    TrieNodeResolverCapability.Hash => TrieNodeFactory.CreateExtension(extensionPath.ToArray()),
                    TrieNodeResolverCapability.Path => TrieNodeFactory.CreateExtension(extensionPath.ToArray(), traverseContext.GetCurrentPath().ToArray(), StoreNibblePathPrefix),
                    _ => throw new ArgumentOutOfRangeException()
                };

                _nodeStack.Push(new StackedNode(extension, 0));
            }

            TrieNode branch = Capability switch
            {
                TrieNodeResolverCapability.Hash => TrieNodeFactory.CreateBranch(),
                TrieNodeResolverCapability.Path => TrieNodeFactory.CreateBranch(traverseContext.UpdatePath.Slice(0, traverseContext.CurrentIndex + extensionLength).ToArray(), StoreNibblePathPrefix),
                _ => throw new ArgumentOutOfRangeException()
            };
            if (extensionLength == shorterPath.Length)
            {
                branch.Value = shorterPathValue;
            }
            else
            {
                ReadOnlySpan<byte> shortLeafPath = shorterPath.Slice(extensionLength + 1, shorterPath.Length - extensionLength - 1);
                TrieNode shortLeaf;
                switch (Capability)
                {
                    case TrieNodeResolverCapability.Hash:
                        shortLeaf = TrieNodeFactory.CreateLeaf(shortLeafPath.ToArray(), shorterPathValue);
                        break;
                    case TrieNodeResolverCapability.Path:
                        if (shorterPath.Length == 64)
                        {
                            ReadOnlySpan<byte> pathToShortLeaf = shorterPath.Slice(0, extensionLength + 1);
                            shortLeaf = TrieNodeFactory.CreateLeaf(shortLeafPath.ToArray(), shorterPathValue, pathToShortLeaf.ToArray(), StoreNibblePathPrefix);
                        }
                        else
                        {
                            Span<byte> pathToShortLeaf = stackalloc byte[branch.PathToNode.Length + 1];
                            branch.PathToNode.CopyTo(pathToShortLeaf);
                            pathToShortLeaf[branch.PathToNode.Length] = shorterPath[extensionLength];
                            shortLeaf = TrieNodeFactory.CreateLeaf(shortLeafPath.ToArray(), shorterPathValue, pathToShortLeaf, StoreNibblePathPrefix);
                        }
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
                branch.SetChild(shorterPath[extensionLength], shortLeaf);
            }

            ReadOnlySpan<byte> leafPath = longerPath.Slice(extensionLength + 1, longerPath.Length - extensionLength - 1);
            TrieNode withUpdatedKeyAndValue;
            switch (Capability)
            {
                case TrieNodeResolverCapability.Hash:
                    withUpdatedKeyAndValue = node.CloneWithChangedKeyAndValue(
                        leafPath.ToArray(), longerPathValue);
                    break;
                case TrieNodeResolverCapability.Path:
                    Span<byte> pathToLeaf = stackalloc byte[branch.PathToNode.Length + 1];
                    branch.PathToNode.CopyTo(pathToLeaf);
                    pathToLeaf[branch.PathToNode.Length] = longerPath[extensionLength];
                    withUpdatedKeyAndValue = node.CloneWithChangedKeyAndValue(leafPath.ToArray(), longerPathValue, pathToLeaf.ToArray());
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            _nodeStack.Push(new StackedNode(branch, longerPath[extensionLength]));
            ConnectNodes(withUpdatedKeyAndValue);

            return traverseContext.UpdateValue;
        }

        private byte[]? TraverseExtension(TrieNode node, in TraverseContext traverseContext)
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
                int currentIndex = traverseContext.CurrentIndex + extensionLength;
                if (traverseContext.IsUpdate)
                {
                    _nodeStack.Push(new StackedNode(node, 0));
                }

                TrieNode next = Capability switch
                {
                    TrieNodeResolverCapability.Hash => node.GetChild(TrieStore, 0),
                    TrieNodeResolverCapability.Path => node.GetChild(TrieStore, traverseContext.GetCurrentPath(currentIndex).ToArray(), 0),
                    _ => throw new ArgumentOutOfRangeException()
                };

                if (next is null)
                {
                    ThrowMissingChildException(node);
                }

                next.ResolveNode(TrieStore);

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

            TrieNode branch = Capability switch
            {
                TrieNodeResolverCapability.Hash => TrieNodeFactory.CreateBranch(),
                TrieNodeResolverCapability.Path => TrieNodeFactory.CreateBranch(traverseContext.UpdatePath.Slice(0, traverseContext.CurrentIndex + extensionLength).ToArray(), StoreNibblePathPrefix),
                _ => throw new ArgumentOutOfRangeException()
            };

            if (extensionLength == remaining.Length)
            {
                branch.Value = traverseContext.UpdateValue;
            }
            else
            {
                byte[] path = remaining.Slice(extensionLength + 1, remaining.Length - extensionLength - 1).ToArray();
                TrieNode shortLeaf = Capability switch
                {
                    TrieNodeResolverCapability.Hash => TrieNodeFactory.CreateLeaf(path, traverseContext.UpdateValue),
                    TrieNodeResolverCapability.Path => TrieNodeFactory.CreateLeaf(path, traverseContext.UpdateValue, traverseContext.UpdatePath.Slice(0, traverseContext.CurrentIndex + extensionLength + 1).ToArray(), StoreNibblePathPrefix),
                    _ => throw new ArgumentOutOfRangeException()
                };
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
                TrieNode secondExtension;
                switch (Capability)
                {
                    case TrieNodeResolverCapability.Hash:
                        secondExtension = TrieNodeFactory.CreateExtension(extensionPath, originalNodeChild);
                        break;
                    case TrieNodeResolverCapability.Path:
                        Span<byte> fullPath = traverseContext.UpdatePath.Slice(0, traverseContext.CurrentIndex + extensionLength + 1).ToArray();
                        fullPath[traverseContext.CurrentIndex + extensionLength] = pathBeforeUpdate[extensionLength];
                        secondExtension
                            = TrieNodeFactory.CreateExtension(extensionPath, originalNodeChild, fullPath, StoreNibblePathPrefix);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
                branch.SetChild(pathBeforeUpdate[extensionLength], secondExtension);
            }
            else
            {
                TrieNode childNode = originalNodeChild;
                branch.SetChild(pathBeforeUpdate[extensionLength], childNode);
            }

            ConnectNodes(branch);
            return traverseContext.UpdateValue;
        }

        private byte[] TraverseNext(in TraverseContext traverseContext, int extensionLength, TrieNode next)
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
            public byte[]? UpdateValue { get; }
            public ReadOnlySpan<byte> UpdatePath { get; }
            public bool IsUpdate { get; }
            public bool IsRead => !IsUpdate;
            public bool IsDelete => IsUpdate && UpdateValue is null;
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

            public ReadOnlySpan<byte> GetCurrentPath()
            {
                return UpdatePath.Slice(0, CurrentIndex);
            }

            public ReadOnlySpan<byte> GetCurrentPath(int currentIndex)
            {
                return UpdatePath.Slice(0, currentIndex);
            }

            public TraverseContext(
                Span<byte> updatePath,
                byte[]? updateValue,
                bool isUpdate,
                bool ignoreMissingDelete = true)
            {
                UpdatePath = updatePath;
                if (updateValue is not null && updateValue.Length == 0)
                {
                    updateValue = null;
                }

                UpdateValue = updateValue;
                IsUpdate = isUpdate;
                IgnoreMissingDelete = ignoreMissingDelete;
                CurrentIndex = 0;
            }

            public override string ToString()
            {
                return $"{(IsDelete ? "DELETE" : IsUpdate ? "UPDATE" : "READ")} {UpdatePath.ToHexString()}{(IsRead ? string.Empty : $" -> {UpdateValue?.ToHexString()}")}";
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
                MaxDegreeOfParallelism = visitingOptions.MaxDegreeOfParallelism,
                KeepTrackOfAbsolutePath = (Capability == TrieNodeResolverCapability.Path) || visitingOptions.KeepTrackOfAbsolutePath
            };

            TrieNode rootRef = null;
            if (!rootHash.Equals(Keccak.EmptyTreeHash))
            {
                switch (Capability)
                {
                    case TrieNodeResolverCapability.Hash:
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
                        break;
                    case TrieNodeResolverCapability.Path:
                        // _logger.Info($"Try to get root node from cache: {rootHash}");
                        rootRef = RootHash == rootHash ? RootRef : TrieStore.FindCachedOrUnknown(rootHash, Array.Empty<byte>(), StoreNibblePathPrefix);
                        // _logger.Info($"Try to get root node from cache: {rootRef}");
                        ////

                        ///
                        // _logger.Info($"Starting Visitor - Resolve RootNode - rh:{rootHash} crh:{RootHash}");
                        try
                        {
                            // TrieNode? testNode = new TrieNode(NodeType.Unknown, path: Array.Empty<byte>());
                            // _logger.Info($"Test Node - try to resolve : {testNode}");
                            // testNode!.ResolveNode(TrieStore);
                            // _logger.Info($"Test Node - resolved : {testNode}");
                            // testNode!.ResolveKey(TrieStore, true);
                            // _logger.Info($"Test Root Node: Key:{testNode.Key} Keccak:{testNode.Keccak} FullPath:{testNode.FullPath}");

                            // _logger.Info($"fetched rootRef node type: {rootRef?.NodeType}");
                            if (rootRef!.NodeType == NodeType.Unknown)
                            {
                                rootRef!.ResolveNode(TrieStore);
                                rootRef!.ResolveKey(TrieStore, true);
                                // _logger.Info($"rootRef resolved keccak to:  {rootRef.Keccak}");
                                if (rootRef.Keccak != rootHash)
                                {
                                    _logger.Error($"PT.Accept wanted: {rootHash} got: {rootRef.Keccak}");
                                    throw new TrieException("Root ref hash mismatch!");
                                }
                            }
                            // _logger.Info($"root node found:  {rootRef.Keccak}");

                        }
                        catch (TrieException)
                        {
                            visitor.VisitMissingNode(rootHash, trieVisitContext);
                            return;
                        }
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            visitor.VisitTree(rootHash, trieVisitContext);
            rootRef?.Accept(visitor, TrieStore, trieVisitContext);
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
    }
}
