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
using System.Threading;
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
    public class PatriciaTree : IPatriciaTree
    {
        private const int MaxKeyStackAlloc = 64;
        private readonly ILogger _logger;

        public const int OneNodeAvgMemoryEstimate = 384;
        public const int StoragePrefixLength = Keccak.Size + 1;
        public const int StorageKeyLength = Keccak.Size;

        /// <summary>
        ///     0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421
        /// </summary>
        public static readonly Hash256 EmptyTreeHash = Keccak.EmptyTreeHash;
        public static readonly byte[] EmptyKeyPath = Array.Empty<byte>();

        public TrieType TrieType { get; init; }

        private Stack<StackedNode>? _nodeStack;

        private ConcurrentQueue<Exception>? _commitExceptions;
        private ConcurrentQueue<NodeCommitInfo>? _currentCommit;

        /// <summary>
        /// In path based tree, we need to keep track of nodes that are to be deleted when the insertion
        /// operation is completed.
        /// </summary>
        private readonly ConcurrentQueue<TrieNode>? _deleteNodes;
        private Bloom? _uncommitedPaths;
        public ITrieStore TrieStore { get; }
        public ICappedArrayPool? _bufferPool;

        public TrieNodeResolverCapability Capability => TrieStore.Capability;

        private readonly bool _parallelBranches;

        private readonly bool _allowCommits;

        private int _isWriteInProgress;

        private Hash256 _rootHash = Keccak.EmptyTreeHash;

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

        public bool ClearedBySelfDestruct = false;

        public Hash256? ParentStateRootHash { get; set; }

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

        public Hash256 RootHash
        {
            get => _rootHash;
            set => SetRootHash(value, true);
        }

        public TrieNode? RootRef { get => _rootRef; set => _rootRef = value; }

        public PatriciaTree()
            : this(NullTrieStore.Instance, EmptyTreeHash, false, true, NullLogManager.Instance)
        {
        }

        public PatriciaTree(ITrieStore trieStore, ILogManager logManager, ICappedArrayPool? bufferPool = null)
            : this(trieStore, EmptyTreeHash, false, true, logManager, bufferPool: bufferPool)
        {
        }

        public PatriciaTree(
            ITrieStore? trieStore,
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
            StoreNibblePathPrefix = Array.Empty<byte>();

            // TODO: cannot do that without knowing whether the owning account is persisted or not
            // RootRef?.MarkPersistedRecursively(_logger);

            if (_allowCommits)
            {
                _currentCommit = new ConcurrentQueue<NodeCommitInfo>();
                _commitExceptions = new ConcurrentQueue<Exception>();
                _deleteNodes = new ConcurrentQueue<TrieNode>();
                _uncommitedPaths = new Bloom();
            }

            _bufferPool = bufferPool;
        }

        public void Commit(long blockNumber, bool skipRoot = false, WriteFlags writeFlags = WriteFlags.None)
        {
            if (!_allowCommits)
            {
                ThrowReadOnlyTrieException();
            }

            // TODO: stcg
            //process deletions - it can happend that a root is set too empty hash due to deletions - needs to be outside of root ref condition
            if (TrieStore.Capability == TrieNodeResolverCapability.Path)
            {
                TrieStore.OpenContext(blockNumber, ParentStateRootHash);
                if (ClearedBySelfDestruct)
                    TrieStore.MarkPrefixDeleted(blockNumber, StoreNibblePathPrefix);
                while (_deleteNodes != null && _deleteNodes.TryDequeue(out TrieNode delNode))
                    _currentCommit.Enqueue(new NodeCommitInfo(delNode));
            }

            bool processDirtyRoot = RootRef?.IsDirty == true;
            if (processDirtyRoot)
                Commit(new NodeCommitInfo(RootRef), skipSelf: skipRoot);

            while (TryDequeueCommit(out NodeCommitInfo node))
            {
                if (_logger.IsTrace) Trace(blockNumber, node);
                TrieStore.CommitNode(blockNumber, node, writeFlags: writeFlags);
            }

            if (processDirtyRoot)
            {
                RootRef!.ResolveKey(TrieStore, true, bufferPool: _bufferPool);
                //resetting root reference for instances without cache will 'unresolve' root node, freeing TrieNode instances
                //otherwise block commit sets will retain references to TrieNodes and not free them during e.g. snap sync
                //TODO - refactor - is resetting really need - can be done without?
                SetRootHash(RootRef.Keccak!, TrieStore.ShouldResetObjectsOnRootChange());
            }

            TrieStore.FinishBlockCommit(TrieType, blockNumber, RootRef, writeFlags);
            if (TrieStore.Capability == TrieNodeResolverCapability.Path && TrieType == TrieType.State)
                ParentStateRootHash = RootHash;
            _uncommitedPaths = new Bloom();
            ClearedBySelfDestruct = false;

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
                                Trace(node, i);
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
                            nodesToCommit.Add(new NodeCommitInfo(node.GetChild(TrieStore, i)!, node, i));
                        }
                        else
                        {
                            if (_logger.IsTrace)
                            {
                                Trace(node, i);
                            }
                        }
                    }

                    if (nodesToCommit.Count >= 4)
                    {
                        ClearExceptions();
                        Parallel.For(0, nodesToCommit.Count, i =>
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
                TrieNode extensionChild = node.GetChild(TrieStore, 0);
                if (extensionChild is null)
                {
                    ThrowInvalidExtension();
                }

                if (extensionChild.IsDirty)
                {
                    Commit(new NodeCommitInfo(extensionChild, node, 0));
                }
                else
                {
                    if (_logger.IsTrace) TraceExtensionSkip(extensionChild);
                }
            }

            node.ResolveKey(TrieStore, nodeCommitInfo.IsRoot, bufferPool: _bufferPool);
            node.Seal();


            //for path based store, inlined nodes need to be stored separately to be access directly by path
            if (node.FullRlp.Length >= 32 || TrieStore.Capability == TrieNodeResolverCapability.Path)
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
            void Trace(TrieNode node, int i)
            {
                TrieNode child = node.GetChild(TrieStore, i);
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

        public void UpdateRootHash()
        {
            if (Capability == TrieNodeResolverCapability.Path)
                RootRef?.ResolveNode(TrieStore);
            RootRef?.ResolveKey(TrieStore, true);
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
                RootRef = TrieStore.FindCachedOrUnknown(_rootHash, Array.Empty<byte>(), StoreNibblePathPrefix);
            }
        }

        [SkipLocalsInit]
        [DebuggerStepThrough]
        private ReadOnlySpan<byte> GetInternal(ReadOnlySpan<byte> rawKey, out InternalReadDiagData diagData, Hash256? rootHash = null)
        {
            try
            {
                diagData = new InternalReadDiagData();
                int nibblesCount = 2 * rawKey.Length;
                byte[]? array = null;
                Span<byte> nibbles = (rawKey.Length <= MaxKeyStackAlloc
                        ? stackalloc byte[MaxKeyStackAlloc]
                        : array = ArrayPool<byte>.Shared.Rent(nibblesCount))
                    [..nibblesCount]; // Slice to exact size;

                Nibbles.BytesToNibbleBytes(rawKey, nibbles);
                var result = Run(nibbles, nibblesCount, new CappedArray<byte>(Array.Empty<byte>()), false, diagData: diagData, startRootHash: rootHash).ToArray();
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
                int nibblesCount = nibbles.Length;
                CappedArray<byte> result = Run(nibbles, nibblesCount, Array.Empty<byte>(), false, startRootHash: rootHash,
                    isNodeRead: true, diagData: new InternalReadDiagData());
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
                CappedArray<byte> result = Run(nibbles, nibblesCount, Array.Empty<byte>(), false, startRootHash: rootHash,
                    isNodeRead: true, diagData: new InternalReadDiagData());
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

        public virtual ReadOnlySpan<byte> Get(ReadOnlySpan<byte> rawKey, Hash256? rootHash = null)
        {
            //for diagnostics
            //if (Capability == TrieNodeResolverCapability.Path)
            //{
            //    byte[] pathValue = GetByPath(rawKey, out PathReadDiagData pathDiagData, rootHash);
            //    byte[] internalValue = GetInternal(rawKey, out InternalReadDiagData internalDiagData, rootHash);
            //    if (!Bytes.SpanEqualityComparer.Equals(internalValue, pathValue))
            //    {
            //        if (_logger.IsWarn)
            //        {
            //            _logger.Warn($"Difference for key: {rawKey.ToHexString()} | ST prefix: {StoreNibblePathPrefix?.ToHexString()} | internal: {internalValue?.ToHexString()} | path value: {pathValue?.ToHexString()}");
            //            _logger.Warn($"Path read for {pathDiagData.FullPath.ToHexString()} | Parent state root: {pathDiagData.ParentStateRoot} | loaded from db: {pathDiagData.LoadedFromDb} | dirty read (bloom): {pathDiagData.Dirty} | self-destruct: {pathDiagData.SelfDestruct}");
            //            _logger.Warn($"Internal read stack:");
            //            foreach (TrieNode node in internalDiagData.Stack)
            //            {
            //                _logger.Warn($"Node {node}");
            //            }
            //        }
            //        return internalValue;
            //    }
            //    return pathValue;
            //}
            //return GetInternal(rawKey, out _, rootHash);
            return Capability switch
            {
                TrieNodeResolverCapability.Hash => GetInternal(rawKey, out _, rootHash),
                TrieNodeResolverCapability.Path => GetByPath(rawKey, out _, rootHash),
                _ => throw new ArgumentOutOfRangeException()
            };
        }

        private ReadOnlySpan<byte> GetByPath(ReadOnlySpan<byte> rawKey, out PathReadDiagData diagData, Hash256? rootHash = null)
        {
            diagData = new PathReadDiagData();

            if (!TrieStore.CanAccessByPath())
            {
                diagData.Fallback = true;
                return GetInternal(rawKey, out _);
            }

            if (rootHash is null)
            {
                if (RootRef is null) return null;
                if (RootRef?.IsDirty == true)
                {
                    if (_uncommitedPaths is null || _uncommitedPaths.Matches(rawKey) || ClearedBySelfDestruct)
                    {
                        diagData.Dirty = _uncommitedPaths is null || _uncommitedPaths.Matches(rawKey);
                        diagData.SelfDestruct = ClearedBySelfDestruct;
                        return GetInternal(rawKey, out _);
                    }
                }
            }

            // try and get cached nodes
            Span<byte> nibbleBytes = stackalloc byte[StoreNibblePathPrefix.Length + rawKey.Length * 2];
            StoreNibblePathPrefix.CopyTo(nibbleBytes);
            Nibbles.BytesToNibbleBytes(rawKey, nibbleBytes[StoreNibblePathPrefix.Length..]);
            TrieNode? node = TrieStore.FindCachedOrUnknown(nibbleBytes[StoreNibblePathPrefix.Length..], StoreNibblePathPrefix, TrieType == TrieType.State ? (rootHash ?? ParentStateRootHash) : ParentStateRootHash);

            diagData.ParentStateRoot = TrieType == TrieType.State ? (rootHash ?? ParentStateRootHash) : ParentStateRootHash;
            diagData.FullPath = nibbleBytes.ToArray();

            if (node is null)
                return null;

            // if not in cached nodes - then check persisted nodes
            // TODO - when rootHash is overriden should check if it is safe to get value from DB as it might have modifications made after the block of rootHash
            // eth_call will check this by calling HasStateForRoot - need to ensure all calls do that
            if (node.NodeType == NodeType.Unknown)
            {
                byte[]? nodeData = TrieStore.TryLoadRlp(nibbleBytes, null);
                if (nodeData is null) return null;

                diagData.LoadedFromDb = true;
                RlpStream rlpStream = nodeData.AsRlpStream();

                rlpStream.ReadSequenceLength();
                rlpStream.DecodeByteArraySpan();
                ReadOnlySpan<byte> valueSpan = rlpStream.DecodeByteArraySpan();

                return valueSpan.ToArray();
            }

            return node.Value.ToArray();
        }

        private Hash256? GetPersistedRoot()
        {
            byte[]? nodeData = TrieStore.TryLoadRlp(Array.Empty<byte>(), null);
            if (nodeData is null)
                return null;

            TrieNode node = new(NodeType.Unknown, nodeData);
            node.ResolveNode(TrieStore);
            node.ResolveKey(TrieStore, true);
            return node.Keccak;
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
            Run(nibbles, nibblesCount, value, true, new InternalReadDiagData());
            _uncommitedPaths?.Set(rawKey);

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
            Set(rawKey, value is null ? Array.Empty<byte>() : value.Bytes);
        }

        private ref readonly CappedArray<byte> Run(
            Span<byte> updatePath,
            int nibblesCount,
            in CappedArray<byte> updateValue,
            bool isUpdate,
            in InternalReadDiagData diagData,
            bool ignoreMissingDelete = true,
            Hash256? startRootHash = null,
            bool isNodeRead = false)
        {
#if DEBUG
            if (nibblesCount != updatePath.Length)
            {
                throw new Exception("Does it ever happen?");
            }
#endif
            TraverseContext traverseContext =
                new(updatePath[..nibblesCount], updateValue, isUpdate, ignoreMissingDelete, isNodeRead: isNodeRead);

            if (startRootHash is not null)
            {
                if (_logger.IsTrace) TraceStart(startRootHash, in traverseContext);
                TrieNode startNode = TrieStore.FindCachedOrUnknown(startRootHash);
                ResolveNode(startNode, in traverseContext);
                return ref TraverseNode(startNode, in traverseContext, in diagData);
            }
            else
            {
                bool trieIsEmpty = RootRef is null;
                if (trieIsEmpty)
                {
                    if (traverseContext.UpdateValue.IsNotNull)
                    {
                        if (_logger.IsTrace) TraceNewLeaf(in traverseContext);
                        byte[] key = updatePath[..nibblesCount].ToArray();
                        RootRef = Capability switch
                        {
                            TrieNodeResolverCapability.Hash => TrieNodeFactory.CreateLeaf(key, traverseContext.UpdateValue),
                            TrieNodeResolverCapability.Path => TrieNodeFactory.CreateLeaf(key, traverseContext.UpdateValue, EmptyKeyPath, StoreNibblePathPrefix),
                            _ => throw new ArgumentOutOfRangeException()
                        };
                    }

                    if (_logger.IsTrace) TraceNull(in traverseContext);
                    return ref traverseContext.UpdateValue;
                }
                else
                {
                    ResolveNode(RootRef, in traverseContext);
                    if (_logger.IsTrace) TraceNode(in traverseContext);
                    return ref TraverseNode(RootRef, in traverseContext, in diagData);
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

        private void ResolveNode(TrieNode node, in TraverseContext traverseContext)
        {
            try
            {
                node.ResolveNode(TrieStore);
            }
            catch (TrieNodeException e)
            {
                ThrowMissingTrieNodeException(e, in traverseContext);
            }
        }

        private ref readonly CappedArray<byte> TraverseNode(TrieNode node, scoped in TraverseContext traverseContext, in InternalReadDiagData diagData)
        {
            if (_logger.IsTrace) Trace(node, traverseContext);
            diagData.Stack.Push(node);

            if (traverseContext.IsNodeRead && traverseContext.RemainingUpdatePathLength == 0)
            {
                return ref node.FullRlp;
            }

            switch (node.NodeType)
            {
                case NodeType.Branch:
                    return ref TraverseBranch(node, in traverseContext, in diagData);
                case NodeType.Extension:
                    return ref TraverseExtension(node, in traverseContext, in diagData);
                case NodeType.Leaf:
                    return ref TraverseLeaf(node, in traverseContext, in diagData);
                case NodeType.Unknown:
                    return ref TraverseUnknown(node);
                default:
                    return ref ThrowNotSupported(node);
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            void Trace(TrieNode node, in TraverseContext traverseContext)
            {
                _logger.Trace($"Traversing {node} to {(traverseContext.IsReadValue ? "READ" : traverseContext.IsDelete ? "DELETE" : "UPDATE")}");
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
            bool isRoot = IsNodeStackEmpty();
            TrieNode nextNode = node;

            while (!isRoot)
            {
                StackedNode parentOnStack = PopFromNodeStack();
                node = parentOnStack.Node;

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

                            ResolveNode(childNode, in traverseContext);
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
                                _deleteNodes.Enqueue(childNode.CloneNodeForDeletion());

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

                                _deleteNodes.Enqueue(childNode.CloneNodeForDeletion());

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

                        _deleteNodes.Enqueue(nextNode.CloneNodeForDeletion());

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

                        _deleteNodes.Enqueue(nextNode.CloneNodeForDeletion());

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
                //if(Capability == TrieNodeResolverCapability.Path) _deleteNodes?.Enqueue(node.CloneNodeForDeletion());
            }

            RootRef = nextNode;
        }

        private ref readonly CappedArray<byte> TraverseBranch(TrieNode node, scoped in TraverseContext traverseContext, in InternalReadDiagData diagData)
        {
            if (traverseContext.RemainingUpdatePathLength == 0)
            {
                /* all these cases when the path ends on the branch assume a trie with values in the branches
                   which is not possible within the Ethereum protocol which has keys of the same length (64) */

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

                    if (Capability == TrieNodeResolverCapability.Path) _deleteNodes?.Enqueue(node.CloneNodeForDeletion());
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

            int pathIndex = traverseContext.UpdatePath[traverseContext.CurrentIndex];
            TrieNode childNode = Capability switch
            {
                TrieNodeResolverCapability.Hash => node.GetChild(TrieStore, pathIndex),
                TrieNodeResolverCapability.Path => node.GetChild(TrieStore, pathIndex, ParentStateRootHash),
                _ => throw new ArgumentOutOfRangeException()
            };

            if (traverseContext.IsUpdate)
            {
                PushToNodeStack(new StackedNode(node, pathIndex));
            }

            if (childNode is null)
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
                byte[] leafPath = traverseContext.UpdatePath[currentIndex..].ToArray();
                TrieNode leaf = Capability switch
                {
                    TrieNodeResolverCapability.Hash => TrieNodeFactory.CreateLeaf(leafPath, traverseContext.UpdateValue),
                    TrieNodeResolverCapability.Path => TrieNodeFactory.CreateLeaf(leafPath, traverseContext.UpdateValue, traverseContext.GetCurrentPath(currentIndex).ToArray(), StoreNibblePathPrefix),
                    _ => throw new ArgumentOutOfRangeException()
                };

                ConnectNodes(leaf, in traverseContext);
                return ref traverseContext.UpdateValue;
            }

            ResolveNode(childNode, in traverseContext);

            return ref TraverseNext(childNode, in traverseContext, 1, in diagData);
        }

        private ref readonly CappedArray<byte> TraverseLeaf(TrieNode node, scoped in TraverseContext traverseContext, in InternalReadDiagData diagData)
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
                    if (Capability == TrieNodeResolverCapability.Path) _deleteNodes?.Enqueue(node.CloneNodeForDeletion());
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

                TrieNode extension = Capability switch
                {
                    TrieNodeResolverCapability.Hash => TrieNodeFactory.CreateExtension(extensionPath.ToArray()),
                    TrieNodeResolverCapability.Path => TrieNodeFactory.CreateExtension(extensionPath.ToArray(), traverseContext.GetCurrentPath().ToArray(), StoreNibblePathPrefix),
                    _ => throw new ArgumentOutOfRangeException()
                };

                PushToNodeStack(new StackedNode(extension, 0));
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

            PushToNodeStack(new StackedNode(branch, longerPath[extensionLength]));
            ConnectNodes(withUpdatedKeyAndValue, in traverseContext);

            return ref traverseContext.UpdateValue;
        }

        private ref readonly CappedArray<byte> TraverseExtension(TrieNode node, scoped in TraverseContext traverseContext, in InternalReadDiagData diagData)
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
                int currentIndex = traverseContext.CurrentIndex + extensionLength;
                if (traverseContext.IsUpdate)
                {
                    PushToNodeStack(new StackedNode(node, 0));
                }

                TrieNode next = Capability switch
                {
                    TrieNodeResolverCapability.Hash => node.GetChild(TrieStore, 0),
                    TrieNodeResolverCapability.Path => node.GetChild(TrieStore, 0, ParentStateRootHash),
                    _ => throw new ArgumentOutOfRangeException()
                };

                if (next is null)
                {
                    ThrowMissingChildException(node);
                }

                ResolveNode(next, in traverseContext);

                return ref TraverseNext(next, in traverseContext, extensionLength, diagData);
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
                PushToNodeStack(new StackedNode(node, 0));
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

            TrieNode originalNodeChild = Capability switch
            {
                TrieNodeResolverCapability.Hash => originalNode.GetChild(TrieStore, 0),
                TrieNodeResolverCapability.Path => originalNode.GetChild(TrieStore, 0, ParentStateRootHash),
                _ => throw new ArgumentOutOfRangeException()
            };
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

            ConnectNodes(branch, in traverseContext);
            return ref traverseContext.UpdateValue;
        }

        private ref readonly CappedArray<byte> TraverseNext(TrieNode next, scoped in TraverseContext traverseContext, int extensionLength, in InternalReadDiagData diagData)
        {
            // Move large struct creation out of flow so doesn't force additional stack space
            // in calling method even if not used
            TraverseContext newContext = traverseContext.WithNewIndex(traverseContext.CurrentIndex + extensionLength);
            return ref TraverseNode(next, in newContext, in diagData);
        }

        private readonly ref struct TraverseContext
        {
            public readonly ref readonly CappedArray<byte> UpdateValue;
            public readonly ReadOnlySpan<byte> UpdatePath;
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
                in CappedArray<byte> updateValue,
                bool isUpdate,
                bool ignoreMissingDelete = true,
                bool isNodeRead = false)
            {
                UpdatePath = updatePath;
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
                IsStorage = storageAddr is not null,
                KeepTrackOfAbsolutePath = (Capability == TrieNodeResolverCapability.Path) || visitingOptions.KeepTrackOfAbsolutePath,
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

            ReadFlags flags = visitor.IsFullDbScan
                ? visitor.ExtraReadFlag | ReadFlags.HintCacheMiss
                : visitor.ExtraReadFlag;

            ITrieNodeResolver resolver = flags != ReadFlags.None
                ? new TrieNodeResolverWithReadFlags(TrieStore, flags)
                : TrieStore;

            bool TryGetRootRef(out TrieNode? rootRef)
            {
                rootRef = null;
                if (rootHash != Keccak.EmptyTreeHash)
                {
                    switch (Capability)
                    {
                        case TrieNodeResolverCapability.Hash:
                            rootRef = RootHash == rootHash ? RootRef : resolver.FindCachedOrUnknown(rootHash);
                            if (!rootRef!.TryResolveNode(resolver))
                            {
                                visitor.VisitMissingNode(default, rootHash, trieVisitContext);
                                return false;
                            }
                            break;
                        case TrieNodeResolverCapability.Path:
                            rootRef = RootHash == rootHash ? RootRef : TrieStore.FindCachedOrUnknown(rootHash, Array.Empty<byte>(), StoreNibblePathPrefix);
                            if (!rootRef!.TryResolveNode(resolver))
                            {
                                visitor.VisitMissingNode(default, rootHash, trieVisitContext);
                                return false;
                            }
                            //as node is searched using path, need to verify that the keccak that was requested is the same as calculated from the resolved data
                            //maybe this should have been done automatically in ResolveNode if TrieNode has non empty Keccak when resolving (or maybe too much overhead)?
                            rootRef!.ResolveKey(TrieStore, true);
                            if (rootRef.Keccak != rootHash)
                            {
                                visitor.VisitMissingNode(default, rootHash, trieVisitContext);
                                return false;
                            }
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }
                return true;
            }

            if (!visitor.IsFullDbScan)
            {
                visitor.VisitTree(default, rootHash, trieVisitContext);
                if (TryGetRootRef(out TrieNode rootRef))
                {
                    rootRef?.Accept(visitor, default, resolver, trieVisitContext);
                }
            }
            // Full db scan
            else if (visitingOptions.FullScanMemoryBudget != 0)
            {
                visitor.VisitTree(default, rootHash, trieVisitContext);
                BatchedTrieVisitor<TNodeContext> batchedTrieVisitor = new(visitor, resolver, visitingOptions);
                batchedTrieVisitor.Start(rootHash, trieVisitContext);
            }
            else if (TryGetRootRef(out TrieNode rootRef))
            {
                visitor.VisitTree(default, rootHash, trieVisitContext);
                rootRef?.Accept(visitor, default, resolver, trieVisitContext);
            }
        }

        bool IsNodeStackEmpty()
        {
            Stack<StackedNode> nodeStack = _nodeStack;
            if (nodeStack is null) return true;
            return nodeStack.Count == 0;
        }

        void ClearNodeStack() => _nodeStack?.Clear();

        void PushToNodeStack(in StackedNode value)
        {
            // Allocated the _nodeStack if first push
            _nodeStack ??= new();
            _nodeStack.Push(value);
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

    internal ref struct PathReadDiagData
    {
        public Span<byte> FullPath { get; set; }
        public Hash256 ParentStateRoot { get; set; }
        public bool LoadedFromDb { get; set; }
        public bool Dirty { get; set; }
        public bool SelfDestruct { get; set; }
        public bool Fallback { get; set; }
    }

    internal ref struct InternalReadDiagData
    {
        public InternalReadDiagData()
        {
            Stack = new Stack<TrieNode>();
        }
        public Stack<TrieNode> Stack { get; set; }
    }
}
