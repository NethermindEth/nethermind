// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Cpu;
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
        public static readonly Hash256 EmptyTreeHash = Keccak.EmptyTreeHash;

        public TrieType TrieType { get; init; }

        private Stack<StackedNode>? _nodeStack;

        private ConcurrentQueue<Exception>? _commitExceptions;
        private ConcurrentQueue<NodeCommitInfo>? _currentCommit;

        public IScopedTrieStore TrieStore { get; }
        public ICappedArrayPool? _bufferPool;

        private readonly bool _parallelBranches;

        private readonly bool _allowCommits;

        private int _isWriteInProgress;

        private Hash256 _rootHash = Keccak.EmptyTreeHash;

        public TrieNode? RootRef { get; set; }

        /// <summary>
        /// Only used in EthereumTests
        /// </summary>
        internal TrieNode? Root
        {
            get
            {
                RootRef?.ResolveNode(TrieStore, TreePath.Empty);
                return RootRef;
            }
        }

        public Hash256 RootHash
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
            : this(trieStore.GetTrieStore(null), EmptyTreeHash, false, true, logManager, bufferPool: bufferPool)
        {
        }

        public PatriciaTree(IScopedTrieStore trieStore, ILogManager logManager, ICappedArrayPool? bufferPool = null)
            : this(trieStore, EmptyTreeHash, false, true, logManager, bufferPool: bufferPool)
        {
        }

        public PatriciaTree(
            IKeyValueStoreWithBatching keyValueStore,
            Hash256 rootHash,
            bool parallelBranches,
            bool allowCommits,
            ILogManager logManager,
            ICappedArrayPool? bufferPool = null)
            : this(
                new TrieStore(keyValueStore, logManager).GetTrieStore(null),
                rootHash,
                parallelBranches,
                allowCommits,
                logManager,
                bufferPool: bufferPool)
        {
        }

        public PatriciaTree(
            IScopedTrieStore? trieStore,
            Hash256 rootHash,
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

            _bufferPool = bufferPool;
        }

        public void Commit(long blockNumber, bool skipRoot = false, WriteFlags writeFlags = WriteFlags.None)
        {
            if (!_allowCommits)
            {
                ThrowReadOnlyTrieException();
            }

            if (RootRef is not null && RootRef.IsDirty)
            {
                Commit(new NodeCommitInfo(RootRef, TreePath.Empty), skipSelf: skipRoot);
                while (TryDequeueCommit(out NodeCommitInfo node))
                {
                    if (_logger.IsTrace) Trace(blockNumber, node);
                    TrieStore.CommitNode(blockNumber, node, writeFlags: writeFlags);
                }

                // reset objects
                TreePath path = TreePath.Empty;
                RootRef!.ResolveKey(TrieStore, ref path, true, bufferPool: _bufferPool);
                SetRootHash(RootRef.Keccak!, true);
            }

            TrieStore.FinishBlockCommit(TrieType, blockNumber, RootRef, writeFlags);

            if (_logger.IsDebug) Debug(blockNumber);

            bool TryDequeueCommit(out NodeCommitInfo value)
            {
                Unsafe.SkipInit(out value);
                return _currentCommit?.TryDequeue(out value) ?? false;
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            void Trace(long blockNumber, in NodeCommitInfo node)
            {
                _logger.Trace($"Committing {node} in {blockNumber}");
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            void Debug(long blockNumber)
            {
                _logger.Debug($"Finished committing block {blockNumber}");
            }
        }

        private void Commit(NodeCommitInfo nodeCommitInfo, bool skipSelf = false)
        {
            if (!_allowCommits)
            {
                ThrowReadOnlyTrieException();
            }

            TrieNode node = nodeCommitInfo.Node;
            TreePath path = nodeCommitInfo.Path;
            if (node!.IsBranch)
            {
                // idea from EthereumJ - testing parallel branches
                if (!_parallelBranches || !nodeCommitInfo.IsRoot)
                {
                    for (int i = 0; i < 16; i++)
                    {
                        if (node.IsChildDirty(i))
                        {
                            TreePath childPath = node.GetChildPath(nodeCommitInfo.Path, i);
                            Commit(new NodeCommitInfo(node.GetChildWithChildPath(TrieStore, ref childPath, i)!, node, childPath, i));
                        }
                        else
                        {
                            if (_logger.IsTrace)
                            {
                                Trace(node, ref path, i);
                            }
                        }
                    }
                }
                else
                {
                    List<NodeCommitInfo> nodesToCommit = new(16);
                    for (int i = 0; i < 16; i++)
                    {
                        if (node.IsChildDirty(i))
                        {
                            TreePath childPath = node.GetChildPath(nodeCommitInfo.Path, i);
                            nodesToCommit.Add(new NodeCommitInfo(node.GetChildWithChildPath(TrieStore, ref childPath, i)!, node, childPath, i));
                        }
                        else
                        {
                            if (_logger.IsTrace)
                            {
                                Trace(node, ref path, i);
                            }
                        }
                    }

                    if (nodesToCommit.Count >= 4)
                    {
                        ClearExceptions();
                        Parallel.For(0, nodesToCommit.Count, RuntimeInformation.ParallelOptionsLogicalCores, i =>
                        {
                            try
                            {
                                Commit(nodesToCommit[i]);
                            }
                            catch (Exception e)
                            {
                                AddException(e);
                            }
                        });

                        if (WereExceptions())
                        {
                            ThrowAggregateExceptions();
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
                TreePath childPath = node.GetChildPath(nodeCommitInfo.Path, 0);
                TrieNode extensionChild = node.GetChildWithChildPath(TrieStore, ref childPath, 0);
                if (extensionChild is null)
                {
                    ThrowInvalidExtension();
                }

                if (extensionChild.IsDirty)
                {
                    Commit(new NodeCommitInfo(extensionChild, node, childPath, 0));
                }
                else
                {
                    if (_logger.IsTrace) TraceExtensionSkip(extensionChild);
                }
            }

            node.ResolveKey(TrieStore, ref path, nodeCommitInfo.IsRoot, bufferPool: _bufferPool);
            node.Seal();

            if (node.FullRlp.Length >= 32)
            {
                if (!skipSelf)
                {
                    EnqueueCommit(nodeCommitInfo);
                }
            }
            else
            {
                if (_logger.IsTrace) TraceSkipInlineNode(node);
            }

            void EnqueueCommit(in NodeCommitInfo value)
            {
                ConcurrentQueue<NodeCommitInfo> queue = Volatile.Read(ref _currentCommit);
                // Allocate queue if first commit made
                queue ??= CreateQueue(ref _currentCommit);
                queue.Enqueue(value);
            }

            void ClearExceptions() => _commitExceptions?.Clear();
            bool WereExceptions() => _commitExceptions?.IsEmpty == false;

            void AddException(Exception value)
            {
                ConcurrentQueue<Exception> queue = Volatile.Read(ref _commitExceptions);
                // Allocate queue if first exception thrown
                queue ??= CreateQueue(ref _commitExceptions);
                queue.Enqueue(value);
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            ConcurrentQueue<T> CreateQueue<T>(ref ConcurrentQueue<T> queueRef)
            {
                ConcurrentQueue<T> queue = new();
                ConcurrentQueue<T> current = Interlocked.CompareExchange(ref queueRef, queue, null);
                return (current is null) ? queue : current;
            }

            [DoesNotReturn]
            [StackTraceHidden]
            void ThrowAggregateExceptions() => throw new AggregateException(_commitExceptions);

            [DoesNotReturn]
            [StackTraceHidden]
            static void ThrowInvalidExtension() => throw new InvalidOperationException("An attempt to store an extension without a child.");

            [MethodImpl(MethodImplOptions.NoInlining)]
            void Trace(TrieNode node, ref TreePath path, int i)
            {
                TrieNode child = node.GetChild(TrieStore, ref path, i);
                if (child is not null)
                {
                    _logger.Trace($"Skipping commit of {child}");
                }
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceExtensionSkip(TrieNode extensionChild)
            {
                _logger.Trace($"Skipping commit of {extensionChild}");
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceSkipInlineNode(TrieNode node)
            {
                _logger.Trace($"Skipping commit of an inlined {node}");
            }
        }

        public void UpdateRootHash(bool canBeParallel = true)
        {
            TreePath path = TreePath.Empty;
            RootRef?.ResolveKey(TrieStore, ref path, isRoot: true, bufferPool: _bufferPool, canBeParallel);
            SetRootHash(RootRef?.Keccak ?? EmptyTreeHash, false);
        }

        private void SetRootHash(Hash256? value, bool resetObjects)
        {
            _rootHash = value ?? Keccak.EmptyTreeHash; // nulls were allowed before so for now we leave it this way
            if (_rootHash == Keccak.EmptyTreeHash)
            {
                RootRef = null;
            }
            else if (resetObjects)
            {
                RootRef = TrieStore.FindCachedOrUnknown(TreePath.Empty, _rootHash);
            }
        }

        [SkipLocalsInit]
        [DebuggerStepThrough]
        public virtual ReadOnlySpan<byte> Get(ReadOnlySpan<byte> rawKey, Hash256? rootHash = null)
        {
            try
            {
                int nibblesCount = 2 * rawKey.Length;
                byte[]? array = null;
                Span<byte> nibbles = (rawKey.Length <= MaxKeyStackAlloc
                        ? stackalloc byte[MaxKeyStackAlloc]
                        : array = ArrayPool<byte>.Shared.Rent(nibblesCount))
                    [..nibblesCount]; // Slice to exact size;

                Nibbles.BytesToNibbleBytes(rawKey, nibbles);
                TreePath updatePathTreePath = TreePath.Empty; // Only used on update.
                ref readonly CappedArray<byte> result = ref Run(ref updatePathTreePath, in CappedArray<byte>.Empty, nibbles, isUpdate: false, startRootHash: rootHash);
                if (array is not null) ArrayPool<byte>.Shared.Return(array);

                return result.AsSpan();
            }
            catch (TrieException e)
            {
                EnhanceException(rawKey, rootHash ?? RootHash, e);
                throw;
            }
        }

        [DebuggerStepThrough]
        public byte[]? GetNodeByPath(byte[] nibbles, Hash256? rootHash = null)
        {
            try
            {
                TreePath updatePathTreePath = TreePath.Empty; // Only used on update.
                CappedArray<byte> result = Run(ref updatePathTreePath, in CappedArray<byte>.Empty, nibbles, false, startRootHash: rootHash,
                    isNodeRead: true);
                return result.ToArray() ?? Array.Empty<byte>();
            }
            catch (TrieException e)
            {
                EnhanceExceptionNibble(nibbles, rootHash ?? RootHash, e);
                throw;
            }
        }

        [DebuggerStepThrough]
        public byte[]? GetNodeByKey(Span<byte> rawKey, Hash256? rootHash = null)
        {
            byte[] array = null;
            try
            {
                int nibblesCount = 2 * rawKey.Length;
                Span<byte> nibbles = (nibblesCount <= MaxKeyStackAlloc
                        ? stackalloc byte[MaxKeyStackAlloc]
                        : array = ArrayPool<byte>.Shared.Rent(nibblesCount))
                    [..nibblesCount]; // Slice to exact size;
                Nibbles.BytesToNibbleBytes(rawKey, nibbles);
                TreePath updatePathTreePath = TreePath.Empty; // Only used on update.
                CappedArray<byte> result = Run(ref updatePathTreePath, in CappedArray<byte>.Empty, nibbles, false, startRootHash: rootHash,
                    isNodeRead: true);
                if (array is not null) ArrayPool<byte>.Shared.Return(array);
                return result.ToArray() ?? Array.Empty<byte>();
            }
            catch (TrieException e)
            {
                EnhanceException(rawKey, rootHash ?? RootHash, e);
                throw;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void EnhanceException(ReadOnlySpan<byte> rawKey, ValueHash256 rootHash, TrieException baseException)
        {
            static TrieNodeException? GetTrieNodeException(TrieException? exception) =>
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

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void EnhanceExceptionNibble(ReadOnlySpan<byte> nibble, ValueHash256 rootHash, TrieException baseException)
        {
            static TrieNodeException? GetTrieNodeException(TrieException? exception) =>
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
                    ? $"Failed to load root hash {rootHash} while loading nibble {nibble.ToHexString()}."
                    : $"Failed to load nibble {nibble.ToHexString()} from root hash {rootHash}.";
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
        public virtual void Set(ReadOnlySpan<byte> rawKey, in CappedArray<byte> value)
        {
            if (_logger.IsTrace) Trace(in rawKey, in value);

            if (Interlocked.CompareExchange(ref _isWriteInProgress, 1, 0) != 0)
            {
                ThrowNonConcurrentWrites();
            }

            try
            {
                int nibblesCount = 2 * rawKey.Length;
                byte[] array = null;
                Span<byte> nibbles = (rawKey.Length <= MaxKeyStackAlloc
                        ? stackalloc byte[MaxKeyStackAlloc] // Fixed size stack allocation
                        : array = ArrayPool<byte>.Shared.Rent(nibblesCount))
                    [..nibblesCount]; // Slice to exact size

                Nibbles.BytesToNibbleBytes(rawKey, nibbles);
                // lazy stack cleaning after the previous update
                ClearNodeStack();
                TreePath updatePathTreePath = TreePath.FromPath(rawKey); // Only used on update.
                Run(ref updatePathTreePath, in value, nibbles, isUpdate: true);

                if (array is not null) ArrayPool<byte>.Shared.Return(array);
            }
            finally
            {
                Volatile.Write(ref _isWriteInProgress, 0);
            }

            void Trace(in ReadOnlySpan<byte> rawKey, in CappedArray<byte> value)
            {
                _logger.Trace($"{(value.Length == 0 ? $"Deleting {rawKey.ToHexString()}" : $"Setting {rawKey.ToHexString()} = {value.AsSpan().ToHexString()}")}");
            }

            [DoesNotReturn]
            [StackTraceHidden]
            static void ThrowNonConcurrentWrites()
            {
                throw new InvalidOperationException("Only reads can be done in parallel on the Patricia tree");
            }
        }

        [DebuggerStepThrough]
        public void Set(ReadOnlySpan<byte> rawKey, Rlp? value)
        {
            if (value is null)
            {
                Set(rawKey, in CappedArray<byte>.Empty);
            }
            else
            {
                CappedArray<byte> valueBytes = new(value.Bytes);
                Set(rawKey, in valueBytes);
            }
        }

        private ref readonly CappedArray<byte> Run(
            ref TreePath updatePathTreePath,
            in CappedArray<byte> updateValue,
            Span<byte> updatePath,
            bool isUpdate,
            bool ignoreMissingDelete = true,
            Hash256? startRootHash = null,
            bool isNodeRead = false)
        {
            TraverseContext traverseContext =
                new(updatePath, ref updatePathTreePath, updateValue, isUpdate, ignoreMissingDelete, isNodeRead: isNodeRead);

            if (startRootHash is not null)
            {
                if (_logger.IsTrace) TraceStart(startRootHash, in traverseContext);
                TreePath startingPath = TreePath.Empty;
                TrieNode startNode = TrieStore.FindCachedOrUnknown(startingPath, startRootHash);
                ResolveNode(startNode, in traverseContext, in startingPath);
                return ref startNode.IsBranch ?
                    ref TraverseBranches(startNode, ref startingPath, traverseContext) :
                    ref TraverseNode(startNode, traverseContext, ref startingPath);
            }
            else
            {
                bool trieIsEmpty = RootRef is null;
                if (trieIsEmpty)
                {
                    if (traverseContext.UpdateValue.IsNotNull)
                    {
                        if (_logger.IsTrace) TraceNewLeaf(in traverseContext);
                        byte[] key = updatePath.ToArray();
                        RootRef = TrieNodeFactory.CreateLeaf(key, in traverseContext.UpdateValue);
                    }

                    if (_logger.IsTrace) TraceNull(in traverseContext);
                    return ref traverseContext.UpdateValue;
                }
                else
                {
                    TreePath startingPath = TreePath.Empty;
                    ResolveNode(RootRef, in traverseContext, in startingPath);
                    if (_logger.IsTrace) TraceNode(in traverseContext);
                    return ref RootRef.IsBranch ?
                        ref TraverseBranches(RootRef, ref startingPath, traverseContext) :
                        ref TraverseNode(RootRef, traverseContext, ref startingPath);
                }
            }

            void TraceStart(Hash256 startRootHash, in TraverseContext traverseContext)
            {
                _logger.Trace($"Starting from {startRootHash} - {traverseContext.ToString()}");
            }

            void TraceNewLeaf(in TraverseContext traverseContext)
            {
                _logger.Trace($"Setting new leaf node with value {traverseContext.UpdateValue}");
            }

            void TraceNull(in TraverseContext traverseContext)
            {
                _logger.Trace($"Keeping the root as null in {traverseContext.ToString()}");
            }

            void TraceNode(in TraverseContext traverseContext)
            {
                _logger.Trace($"{traverseContext.ToString()}");
            }
        }

        private void ResolveNode(TrieNode node, in TraverseContext traverseContext, in TreePath path)
        {
            if (node.NodeType != NodeType.Unknown) return;

            try
            {
                node.ResolveUnknownNode(TrieStore, path);
            }
            catch (RlpException rlpException)
            {
                ThrowDecodingError(node, in traverseContext, rlpException);
            }
            catch (TrieNodeException e)
            {
                ThrowMissingTrieNodeException(e, in traverseContext);
            }

            [DoesNotReturn]
            [StackTraceHidden]
            static void ThrowDecodingError(TrieNode node, in TraverseContext traverseContext, RlpException rlpException)
            {
                var exception = new TrieNodeException($"Error when decoding node {node.Keccak}", node.Keccak ?? Keccak.Zero, rlpException);
                exception = (TrieNodeException)ExceptionDispatchInfo.SetCurrentStackTrace(exception);
                ThrowMissingTrieNodeException(exception, in traverseContext);
            }
        }

        private ref readonly CappedArray<byte> TraverseNode(TrieNode node, scoped in TraverseContext traverseContext, scoped ref TreePath path)
        {
            if (_logger.IsTrace) Trace(node, traverseContext);

            if (traverseContext.IsNodeRead && traverseContext.RemainingUpdatePathLength == 0)
            {
                return ref node.FullRlp;
            }

            switch (node.NodeType)
            {
                case NodeType.Extension:
                    return ref TraverseExtension(node, in traverseContext, ref path);
                case NodeType.Leaf:
                    return ref TraverseLeaf(node, in traverseContext, ref path);
                default:
                    return ref TraverseInvalid(node);
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            void Trace(TrieNode node, in TraverseContext traverseContext)
            {
                _logger.Trace($"Traversing {node} to {(traverseContext.IsReadValue ? "READ" : traverseContext.IsDelete ? "DELETE" : "UPDATE")}");
            }

            [DoesNotReturn]
            [StackTraceHidden]
            static ref readonly CappedArray<byte> TraverseInvalid(TrieNode node)
            {
                switch (node.NodeType)
                {
                    case NodeType.Branch:
                        return ref TraverseBranch(node);
                    case NodeType.Unknown:
                        return ref TraverseUnknown(node);
                    default:
                        return ref ThrowNotSupported(node);
                }
            }

            [DoesNotReturn]
            [StackTraceHidden]
            static ref readonly CappedArray<byte> TraverseBranch(TrieNode node)
            {
                throw new InvalidOperationException($"Branch node {node.Keccak} should already be handled");
            }

            [DoesNotReturn]
            [StackTraceHidden]
            static ref readonly CappedArray<byte> TraverseUnknown(TrieNode node)
            {
                throw new InvalidOperationException($"Cannot traverse unresolved node {node.Keccak}");
            }

            [DoesNotReturn]
            [StackTraceHidden]
            static ref readonly CappedArray<byte> ThrowNotSupported(TrieNode node)
            {
                throw new NotSupportedException($"Unknown node type {node.NodeType}");
            }
        }

        private void ConnectNodes(TrieNode? node, in TraverseContext traverseContext)
        {
            TreePath path = traverseContext.UpdatePathTreePath;
            bool isRoot = IsNodeStackEmpty();
            TrieNode nextNode = node;

            while (!isRoot)
            {
                StackedNode parentOnStack = PopFromNodeStack();
                node = parentOnStack.Node;
                path.TruncateMut(parentOnStack.PathLength);

                isRoot = IsNodeStackEmpty();

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

                            path.AppendMut(childNodeIndex);
                            TrieNode childNode = node.GetChildWithChildPath(TrieStore, ref path, childNodeIndex);
                            if (childNode is null)
                            {
                                /* potential corrupted trie data state when we find a branch that has only one child */
                                throw new TrieException(
                                    "Before updating branch should have had at least two non-empty children");
                            }

                            ResolveNode(childNode, in traverseContext, in path);
                            path.TruncateOne();

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

        private ref readonly CappedArray<byte> TraverseBranches(TrieNode node, scoped ref TreePath path, TraverseContext traverseContext)
        {
            while (true)
            {
                if (traverseContext.RemainingUpdatePathLength == 0)
                {
                    return ref ResolveBranchNode(node, in traverseContext);
                }

                int childIdx = traverseContext.UpdatePath[traverseContext.CurrentIndex];
                path.AppendMut(childIdx);
                TrieNode childNode = node.GetChildWithChildPath(TrieStore, ref path, childIdx);
                if (traverseContext.IsUpdate)
                {
                    PushToNodeStack(node, traverseContext.CurrentIndex, childIdx);
                }

                if (childNode is null)
                {
                    return ref ResolveCurrent(in traverseContext);
                }

                ResolveNode(childNode, in traverseContext, in path);

                traverseContext = traverseContext.WithNewIndex(traverseContext.CurrentIndex + 1);
                if (!childNode.IsBranch)
                {
                    return ref TraverseNode(childNode, in traverseContext, ref path);
                }

                // Traverse next branch
                node = childNode;
            }
        }

        private ref readonly CappedArray<byte> TraverseLeaf(TrieNode node, scoped in TraverseContext traverseContext, scoped ref TreePath path)
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

            ref readonly CappedArray<byte> shorterPathValue = ref Unsafe.NullRef<CappedArray<byte>>();
            ref readonly CappedArray<byte> longerPathValue = ref Unsafe.NullRef<CappedArray<byte>>();
            if (Bytes.AreEqual(shorterPath, node.Key))
            {
                shorterPathValue = ref node.ValueRef;
                longerPathValue = ref traverseContext.UpdateValue;
            }
            else
            {
                shorterPathValue = ref traverseContext.UpdateValue;
                longerPathValue = ref node.ValueRef;
            }

            int extensionLength = shorterPath.CommonPrefixLength(longerPath);
            if (extensionLength == shorterPath.Length && extensionLength == longerPath.Length)
            {
                if (traverseContext.IsNodeRead)
                {
                    return ref node.FullRlp;
                }
                if (traverseContext.IsReadValue)
                {
                    return ref node.ValueRef;
                }

                if (traverseContext.IsDelete)
                {
                    ConnectNodes(null, in traverseContext);
                    return ref traverseContext.UpdateValue;
                }

                if (!Bytes.AreEqual(node.Value, traverseContext.UpdateValue))
                {
                    TrieNode withUpdatedValue = node.CloneWithChangedValue(in traverseContext.UpdateValue);
                    ConnectNodes(withUpdatedValue, in traverseContext);
                    return ref traverseContext.UpdateValue;
                }

                return ref traverseContext.UpdateValue;
            }

            if (traverseContext.IsRead)
            {
                return ref CappedArray<byte>.Null;
            }

            if (traverseContext.IsDelete)
            {
                if (traverseContext.IgnoreMissingDelete)
                {
                    return ref CappedArray<byte>.Null;
                }

                ThrowMissingLeafException(in traverseContext);
            }

            if (extensionLength != 0)
            {
                ReadOnlySpan<byte> extensionPath = longerPath[..extensionLength];
                TrieNode extension = TrieNodeFactory.CreateExtension(extensionPath.ToArray());
                PushToNodeStack(extension, traverseContext.CurrentIndex, 0);
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

            PushToNodeStack(branch, traverseContext.CurrentIndex, longerPath[extensionLength]);
            ConnectNodes(withUpdatedKeyAndValue, in traverseContext);

            return ref traverseContext.UpdateValue;
        }

        private ref readonly CappedArray<byte> TraverseExtension(TrieNode node, scoped in TraverseContext traverseContext, scoped ref TreePath path)
        {
            if (node.Key is null)
            {
                ThrowMissingPrefixException();
            }

            TrieNode originalNode = node;
            ReadOnlySpan<byte> remaining = traverseContext.GetRemainingUpdatePath();

            int extensionLength = remaining.CommonPrefixLength(node.Key);
            if (extensionLength == node.Key.Length)
            {
                if (traverseContext.IsUpdate)
                {
                    PushToNodeStack(node, traverseContext.CurrentIndex, 0);
                }

                node.AppendChildPath(ref path, 0);
                TrieNode next = node.GetChildWithChildPath(TrieStore, ref path, 0);
                if (next is null)
                {
                    ThrowMissingChildException(node);
                }

                ResolveNode(next, in traverseContext, in path);
                TraverseContext newContext = traverseContext.WithNewIndex(traverseContext.CurrentIndex + extensionLength);
                return ref next.IsBranch ?
                    ref TraverseBranches(next, ref path, newContext) :
                    ref TraverseNode(next, newContext, ref path);
            }

            if (traverseContext.IsRead)
            {
                return ref CappedArray<byte>.Null;
            }

            if (traverseContext.IsDelete)
            {
                if (traverseContext.IgnoreMissingDelete)
                {
                    return ref CappedArray<byte>.Null;
                }

                ThrowMissingLeafException(in traverseContext);
            }

            byte[] pathBeforeUpdate = node.Key;
            if (extensionLength != 0)
            {
                byte[] extensionPath = node.Key.Slice(0, extensionLength);
                node = node.CloneWithChangedKey(extensionPath);
                PushToNodeStack(node, traverseContext.CurrentIndex, 0);
            }

            // The node from extension become a branch
            TrieNode branch = TrieNodeFactory.CreateBranch();
            if (extensionLength == remaining.Length)
            {
                branch.Value = traverseContext.UpdateValue;
            }
            else
            {
                byte[] remainingPath = remaining.Slice(extensionLength + 1, remaining.Length - extensionLength - 1).ToArray();
                TrieNode shortLeaf = TrieNodeFactory.CreateLeaf(remainingPath, in traverseContext.UpdateValue);
                branch.SetChild(remaining[extensionLength], shortLeaf);
            }

            TrieNode originalNodeChild = originalNode.GetChild(TrieStore, ref path, 0);
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
            return ref traverseContext.UpdateValue;
        }

        private ref readonly CappedArray<byte> ResolveCurrent(scoped in TraverseContext traverseContext)
        {
            if (traverseContext.IsRead)
            {
                return ref CappedArray<byte>.Null;
            }

            if (traverseContext.IsDelete)
            {
                if (traverseContext.IgnoreMissingDelete)
                {
                    return ref CappedArray<byte>.Null;
                }

                ThrowMissingLeafException(in traverseContext);
            }

            int currentIndex = traverseContext.CurrentIndex + 1;
            byte[] leafPath = traverseContext.UpdatePath[
                currentIndex..].ToArray();
            TrieNode leaf = TrieNodeFactory.CreateLeaf(leafPath, in traverseContext.UpdateValue);
            ConnectNodes(leaf, in traverseContext);

            return ref traverseContext.UpdateValue;
        }

        private ref readonly CappedArray<byte> ResolveBranchNode(TrieNode node, scoped in TraverseContext traverseContext)
        {
            // all these cases when the path ends on the branch assume a trie with values in the branches
            // which is not possible within the Ethereum protocol which has keys of the same length (64)

            if (traverseContext.IsNodeRead)
            {
                return ref node.FullRlp;
            }
            if (traverseContext.IsReadValue)
            {
                return ref node.ValueRef;
            }

            if (traverseContext.IsDelete)
            {
                if (node.Value.IsNull)
                {
                    return ref CappedArray<byte>.Null;
                }

                ConnectNodes(null, in traverseContext);
            }
            else if (Bytes.AreEqual(traverseContext.UpdateValue, node.Value))
            {
                return ref traverseContext.UpdateValue;
            }
            else
            {
                TrieNode withUpdatedValue = node.CloneWithChangedValue(in traverseContext.UpdateValue);
                ConnectNodes(withUpdatedValue, in traverseContext);
            }

            return ref traverseContext.UpdateValue;
        }

        private readonly ref struct TraverseContext
        {
            public readonly ref readonly CappedArray<byte> UpdateValue;
            public readonly ReadOnlySpan<byte> UpdatePath;
            public readonly ref readonly TreePath UpdatePathTreePath;
            public bool IsUpdate { get; }
            public bool IsNodeRead { get; }
            public bool IsReadValue => !IsUpdate && !IsNodeRead;
            public bool IsRead => IsNodeRead || IsReadValue;
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
                ref TreePath updatePathTreePath,
                in CappedArray<byte> updateValue,
                bool isUpdate,
                bool ignoreMissingDelete = true,
                bool isNodeRead = false)
            {
                UpdatePath = updatePath;
                UpdatePathTreePath = ref updatePathTreePath;
                UpdateValue = ref updateValue.IsNotNull && updateValue.Length == 0 ? ref CappedArray<byte>.Null : ref updateValue;
                IsUpdate = isUpdate;
                IgnoreMissingDelete = ignoreMissingDelete;
                CurrentIndex = 0;
                IsNodeRead = isNodeRead;
            }

            public override string ToString()
            {
                return $"{(IsDelete ? "DELETE" : IsUpdate ? "UPDATE" : "READ")} {UpdatePath.ToHexString()}{(IsReadValue ? string.Empty : $" -> {UpdateValue}")}";
            }
        }

        private readonly struct StackedNode
        {
            public StackedNode(TrieNode node, int pathLength, int pathIndex)
            {
                Node = node;
                PathLength = pathLength;
                PathIndex = pathIndex;
            }

            public TrieNode Node { get; }
            public int PathLength { get; }
            public int PathIndex { get; }

            public override string ToString()
            {
                return $"{PathIndex} {Node}";
            }
        }

        public void Accept(ITreeVisitor visitor, Hash256 rootHash, VisitingOptions? visitingOptions = null, Hash256? storageAddr = null) =>
            Accept(new ContextNotAwareTreeVisitor(visitor), rootHash, visitingOptions, storageAddr);

        /// <summary>
        /// Run tree visitor
        /// </summary>
        /// <param name="visitor">The visitor</param>
        /// <param name="rootHash">State root hash (not storage root)</param>
        /// <param name="visitingOptions">Options</param>
        /// <param name="storageAddr">Address of storage, if it should visit storage. </param>
        /// <param name="storageRoot">Root of storage if it should visit storage. Optional for performance.</param>
        /// <typeparam name="TNodeContext"></typeparam>
        public void Accept<TNodeContext>(
            ITreeVisitor<TNodeContext> visitor,
            Hash256 rootHash,
            VisitingOptions? visitingOptions = null,
            Hash256? storageAddr = null,
            Hash256? storageRoot = null
        ) where TNodeContext : struct, INodeContext<TNodeContext>
        {
            ArgumentNullException.ThrowIfNull(visitor);
            ArgumentNullException.ThrowIfNull(rootHash);
            visitingOptions ??= VisitingOptions.Default;

            using TrieVisitContext trieVisitContext = new()
            {
                // hacky but other solutions are not much better, something nicer would require a bit of thinking
                // we introduced a notion of an account on the visit context level which should have no knowledge of account really
                // but we know that we have multiple optimizations and assumptions on trees
                ExpectAccounts = visitingOptions.ExpectAccounts,
                MaxDegreeOfParallelism = visitingOptions.MaxDegreeOfParallelism,
                IsStorage = storageAddr is not null
            };

            if (storageAddr is not null)
            {
                Hash256 DecodeStorageRoot(Hash256 root, Hash256 address)
                {
                    ReadOnlySpan<byte> bytes = Get(address.Bytes, root);
                    Rlp.ValueDecoderContext valueContext = bytes.AsRlpValueContext();
                    return AccountDecoder.Instance.DecodeStorageRootOnly(ref valueContext);
                }

                rootHash = storageRoot ?? DecodeStorageRoot(rootHash, storageAddr);
            }

            ReadFlags flags = visitor.ExtraReadFlag;
            if (visitor.IsFullDbScan)
            {
                if (TrieStore.Scheme == INodeStorage.KeyScheme.HalfPath)
                {
                    // With halfpath or flat, the nodes are ordered so readahead will make things faster.
                    flags |= ReadFlags.HintReadAhead;
                }
                else
                {
                    // With hash, we don't wanna add cache as that will take some CPU time away.
                    flags |= ReadFlags.HintCacheMiss;
                }
            }

            ITrieNodeResolver resolver = flags != ReadFlags.None
                ? new TrieNodeResolverWithReadFlags(TrieStore, flags)
                : TrieStore;

            if (storageAddr is not null)
            {
                resolver = resolver.GetStorageTrieNodeResolver(storageAddr);
            }

            bool TryGetRootRef(out TrieNode? rootRef)
            {
                rootRef = null;
                if (rootHash != Keccak.EmptyTreeHash)
                {
                    TreePath emptyPath = TreePath.Empty;
                    rootRef = RootHash == rootHash ? RootRef : resolver.FindCachedOrUnknown(emptyPath, rootHash);
                    if (!rootRef!.TryResolveNode(resolver, ref emptyPath))
                    {
                        visitor.VisitMissingNode(default, rootHash, trieVisitContext);
                        return false;
                    }
                }

                return true;
            }

            if (!visitor.IsFullDbScan)
            {
                visitor.VisitTree(default, rootHash, trieVisitContext);
                if (TryGetRootRef(out TrieNode rootRef))
                {
                    TreePath emptyPath = TreePath.Empty;
                    rootRef?.Accept(visitor, default, resolver, ref emptyPath, trieVisitContext);
                }
            }
            // Full db scan
            else if (TrieStore.Scheme == INodeStorage.KeyScheme.Hash && visitingOptions.FullScanMemoryBudget != 0)
            {
                visitor.VisitTree(default, rootHash, trieVisitContext);
                BatchedTrieVisitor<TNodeContext> batchedTrieVisitor = new(visitor, resolver, visitingOptions);
                batchedTrieVisitor.Start(rootHash, trieVisitContext);
            }
            else if (TryGetRootRef(out TrieNode rootRef))
            {
                TreePath emptyPath = TreePath.Empty;
                visitor.VisitTree(default, rootHash, trieVisitContext);
                rootRef?.Accept(visitor, default, resolver, ref emptyPath, trieVisitContext);
            }
        }

        bool IsNodeStackEmpty()
        {
            Stack<StackedNode> nodeStack = _nodeStack;
            if (nodeStack is null) return true;
            return nodeStack.Count == 0;
        }

        void ClearNodeStack() => _nodeStack?.Clear();

        [MethodImpl(MethodImplOptions.NoInlining)]
        void PushToNodeStack(TrieNode node, int pathLength, int pathIndex)
        {
            // Allocated the _nodeStack if first push
            _nodeStack ??= new();
            _nodeStack.Push(new StackedNode(node, pathLength, pathIndex));
        }

        StackedNode PopFromNodeStack()
        {
            Stack<StackedNode> stackedNodes = _nodeStack;
            if (stackedNodes is null)
            {
                Throw();
            }

            return stackedNodes.Pop();

            [DoesNotReturn]
            [StackTraceHidden]
            static void Throw() => throw new InvalidOperationException($"Nothing on {nameof(_nodeStack)}");
        }

        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowReadOnlyTrieException() => throw new TrieException("Commits are not allowed on this trie.");

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
        private static void ThrowMissingTrieNodeException(TrieNodeException e, in TraverseContext traverseContext)
        {
            throw new MissingTrieNodeException(e.Message, e, traverseContext.UpdatePath.ToArray(), traverseContext.CurrentIndex);
        }
    }
}
