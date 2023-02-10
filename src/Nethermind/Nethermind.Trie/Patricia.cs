// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie;

public abstract class Patricia: IPatriciaTree
{
    internal TrieNode? rootRef;
    internal Keccak _rootHash = Keccak.EmptyTreeHash;
    internal readonly ITrieStore _trieStore;

    /// <summary>
    ///     0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421
    /// </summary>
    public static readonly Keccak EmptyTreeHash = Keccak.EmptyTreeHash;
    public static readonly byte[] EmptyKeyPath = new byte[0];

    internal readonly ILogger _logger;

    public const int OneNodeAvgMemoryEstimate = 384;

    public TrieType TrieType { get; protected set; }

    /// <summary>
    /// To save allocations this used to be static but this caused one of the hardest to reproduce issues
    /// when we decided to run some of the tree operations in parallel.
    /// </summary>
    internal readonly Stack<StackedNode> _nodeStack = new();

    internal readonly ConcurrentQueue<Exception>? _commitExceptions;

    internal readonly ConcurrentQueue<NodeCommitInfo>? _currentCommit;

    internal readonly bool _parallelBranches;

    internal readonly bool _allowCommits;

    public Keccak RootHash
    {
        get => _rootHash;
        set => SetRootHash(value, true);
    }

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

    public abstract void SetRootHash(Keccak? value, bool resetObjects);

    public TrieNode? RootRef { get => rootRef; set => rootRef = value; }

    public Patricia()
        : this(NullTrieStore.Instance, EmptyTreeHash, false, true, NullLogManager.Instance)
    {
    }

    public Patricia(IKeyValueStoreWithBatching keyValueStore)
        : this(keyValueStore, EmptyTreeHash, false, true, NullLogManager.Instance)
    {
    }

    public Patricia(ITrieStore trieStore, ILogManager logManager)
        : this(trieStore, EmptyTreeHash, false, true, logManager)
    {
    }

    public Patricia(
        IKeyValueStoreWithBatching keyValueStore,
        Keccak rootHash,
        bool parallelBranches,
        bool allowCommits,
        ILogManager logManager)
        : this(
            new TrieStoreByPath(keyValueStore, logManager),
            rootHash,
            parallelBranches,
            allowCommits,
            logManager)
    {
    }

    public Patricia(
        ITrieStore? trieStore,
        Keccak rootHash,
        bool parallelBranches,
        bool allowCommits,
        ILogManager? logManager)
    {
        _logger = logManager?.GetClassLogger<Patricia>() ?? throw new ArgumentNullException(nameof(logManager));
        _trieStore = trieStore ?? throw new ArgumentNullException(nameof(trieStore));
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
    }

    public void UpdateRootHash()
    {
        RootRef?.ResolveKey(TrieStore, true);
        SetRootHash(RootRef?.Keccak ?? EmptyTreeHash, false);
    }

    [DebuggerStepThrough]
    public byte[]? Get(Span<byte> rawKey, Keccak? rootHash = null)
    {
        try
        {
            int nibblesCount = 2 * rawKey.Length;
            byte[] array = null;
            Span<byte> nibbles = rawKey.Length <= 64
                ? stackalloc byte[nibblesCount]
                : array = ArrayPool<byte>.Shared.Rent(nibblesCount);
            Nibbles.BytesToNibbleBytes(rawKey, nibbles);
            var result = Run(nibbles, nibblesCount, Array.Empty<byte>(), false, startRootHash: rootHash);
            if (array is not null) ArrayPool<byte>.Shared.Return(array);
            return result;
        }
        catch (TrieException e)
        {
            throw new TrieException($"Failed to load key {rawKey.ToHexString()} from root hash {rootHash ?? RootHash}.", e);
        }
    }

    [DebuggerStepThrough]
    public void Set(Span<byte> rawKey, byte[] value)
    {
        if (_logger.IsTrace)
            _logger.Trace($"{(value.Length == 0 ? $"Deleting {rawKey.ToHexString()}" : $"Setting {rawKey.ToHexString()} = {value.ToHexString()}")}");

        int nibblesCount = 2 * rawKey.Length;
        byte[] array = null;
        Span<byte> nibbles = rawKey.Length <= 64
            ? stackalloc byte[nibblesCount]
            : array = ArrayPool<byte>.Shared.Rent(nibblesCount);
        Nibbles.BytesToNibbleBytes(rawKey, nibbles);
        Run(nibbles, nibblesCount, value, true);
        if (array is not null) ArrayPool<byte>.Shared.Return(array);
    }

    [DebuggerStepThrough]
    public void Set(Span<byte> rawKey, Rlp? value)
    {
        Set(rawKey, value is null ? Array.Empty<byte>() : value.Bytes);
    }

    internal abstract byte[]? Run(
        Span<byte> updatePath,
        int nibblesCount,
        byte[]? updateValue,
        bool isUpdate,
        bool ignoreMissingDelete = true,
        Keccak? startRootHash = null);

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
            Commit(new NodeCommitInfo(RootRef), skipSelf: skipRoot);
            while (_currentCommit.TryDequeue(out NodeCommitInfo node))
            {
                if (_logger.IsTrace) _logger.Trace($"Committing {node} in {blockNumber}");
                TrieStore.CommitNode(blockNumber, node);
            }

            // reset objects
            RootRef!.ResolveKey(TrieStore, true);
            SetRootHash(RootRef.Keccak!, true);
        }

        TrieStore.FinishBlockCommit(TrieType, blockNumber, RootRef);
        if (_logger.IsDebug) _logger.Debug($"Finished committing block {blockNumber}");
    }

    internal void Commit(NodeCommitInfo nodeCommitInfo, bool skipSelf = false)
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

                    if (_commitExceptions.Count > 0)
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

    public ITrieStore TrieStore => _trieStore;
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

        visitor.VisitTree(rootHash, trieVisitContext);
        rootRef?.Accept(visitor, TrieStore, trieVisitContext);
    }

    internal void ConnectNodes(TrieNode? node)
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
                        TrieNode leafFromBranch = TrieNodeFactory.CreateLeaf(HexPrefix.Leaf(), node.Value);
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

                        childNode.ResolveNode(TrieStore);
                        if (childNode.IsBranch)
                        {
                            TrieNode extensionFromBranch =
                                TrieNodeFactory.CreateExtension(
                                    HexPrefix.Extension((byte)childNodeIndex), childNode);
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

                            HexPrefix newKey
                                = HexPrefix.Extension(Bytes.Concat((byte)childNodeIndex, childNode.Path));
                            TrieNode extendedExtension = childNode.CloneWithChangedKey(newKey);
                            if (_logger.IsTrace)
                                _logger.Trace(
                                    $"Extending child {childNodeIndex} {childNode} of {node} into {extendedExtension}");
                            nextNode = extendedExtension;
                        }
                        else if (childNode.IsLeaf)
                        {
                            HexPrefix newKey = HexPrefix.Leaf(Bytes.Concat((byte)childNodeIndex, childNode.Path));
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
                    HexPrefix newKey = HexPrefix.Leaf(Bytes.Concat(node.Path, nextNode.Path));
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

                    HexPrefix newKey
                        = HexPrefix.Extension(Bytes.Concat(node.Path, nextNode.Path));
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

    internal byte[]? TraverseNode(TrieNode node, TraverseContext traverseContext)
    {
        if (_logger.IsTrace)
            _logger.Trace(
                $"Traversing {node} to {(traverseContext.IsRead ? "READ" : traverseContext.IsDelete ? "DELETE" : "UPDATE")}");

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

    internal abstract byte[]? TraverseBranch(TrieNode node, TraverseContext traverseContext);
    internal abstract byte[]? TraverseExtension(TrieNode node, TraverseContext traverseContext);
    internal abstract byte[]? TraverseLeaf(TrieNode node, TraverseContext traverseContext);

    internal static int FindCommonPrefixLength(Span<byte> shorterPath, Span<byte> longerPath)
    {
        int commonPrefixLength = 0;
        int maxLength = Math.Min(shorterPath.Length, longerPath.Length);
        for (int i = 0; i < maxLength && shorterPath[i] == longerPath[i]; i++, commonPrefixLength++)
        {
            // just finding the common part of the path
        }

        return commonPrefixLength;
    }

    internal ref struct TraverseContext
    {
        public Span<byte> UpdatePath { get; }
        public byte[]? UpdateValue { get; }
        public bool IsUpdate { get; }
        public bool IsRead => !IsUpdate;
        public bool IsDelete => IsUpdate && UpdateValue is null;
        public bool IgnoreMissingDelete { get; }
        public int CurrentIndex { get; set; }
        public int RemainingUpdatePathLength => UpdatePath.Length - CurrentIndex;

        public Span<byte> GetRemainingUpdatePath()
        {
            return UpdatePath.Slice(CurrentIndex, RemainingUpdatePathLength);
        }

        public Span<byte> GetCurrentPath()
        {
            return UpdatePath.Slice(0, CurrentIndex);
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
            return $"{(IsDelete ? "DELETE" : IsUpdate ? "UPDATE" : "READ")} {UpdatePath.ToHexString()}{(IsRead ? string.Empty : $" -> {UpdateValue}")}";
        }
    }

    internal readonly struct StackedNode
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
}
