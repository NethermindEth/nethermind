// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
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
using Nethermind.Core.Collections;
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
        private readonly static byte[][] _singleByteKeys = [[0], [1], [2], [3], [4], [5], [6], [7], [8], [9], [10], [11], [12], [13], [14], [15]];

        private readonly ILogger _logger;

        public const int OneNodeAvgMemoryEstimate = 384;

        /// <summary>
        ///     0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421
        /// </summary>
        public static readonly Hash256 EmptyTreeHash = Keccak.EmptyTreeHash;

        public TrieType TrieType { get; init; }

        private Stack<StackedNode>? _nodeStack;
        private Stack<TraverseStack>? _traverseStack;
        public readonly IScopedTrieStore TrieStore;
        public ICappedArrayPool? _bufferPool;

        private readonly bool _allowCommits;

        private int _isWriteInProgress;

        private Hash256 _rootHash = Keccak.EmptyTreeHash;

        public TrieNode? RootRef { get; set; }

        // Used to estimate if parallelization is needed during commit
        private long _writeBeforeCommit = 0;

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
            : this(NullTrieStore.Instance, EmptyTreeHash, true, NullLogManager.Instance)
        {
        }

        public PatriciaTree(IKeyValueStoreWithBatching keyValueStore)
            : this(keyValueStore, EmptyTreeHash, true, NullLogManager.Instance)
        {
        }

        public PatriciaTree(ITrieStore trieStore, ILogManager logManager, ICappedArrayPool? bufferPool = null)
            : this(trieStore.GetTrieStore(null), EmptyTreeHash, true, logManager, bufferPool: bufferPool)
        {
        }

        public PatriciaTree(IScopedTrieStore trieStore, ILogManager logManager, ICappedArrayPool? bufferPool = null)
            : this(trieStore, EmptyTreeHash, true, logManager, bufferPool: bufferPool)
        {
        }

        public PatriciaTree(
            IKeyValueStoreWithBatching keyValueStore,
            Hash256 rootHash,
            bool allowCommits,
            ILogManager logManager,
            ICappedArrayPool? bufferPool = null)
            : this(
                new RawScopedTrieStore(new NodeStorage(keyValueStore), null),
                rootHash,
                allowCommits,
                logManager,
                bufferPool: bufferPool)
        {
        }

        public PatriciaTree(
            IScopedTrieStore? trieStore,
            Hash256 rootHash,
            bool allowCommits,
            ILogManager? logManager,
            ICappedArrayPool? bufferPool = null)
        {
            _logger = logManager?.GetClassLogger<PatriciaTree>() ?? throw new ArgumentNullException(nameof(logManager));
            TrieStore = trieStore ?? throw new ArgumentNullException(nameof(trieStore));
            _allowCommits = allowCommits;
            RootHash = rootHash;

            // TODO: cannot do that without knowing whether the owning account is persisted or not
            // RootRef?.MarkPersistedRecursively(_logger);

            _bufferPool = bufferPool;
        }

        public void Commit(bool skipRoot = false, WriteFlags writeFlags = WriteFlags.None)
        {
            if (!_allowCommits)
            {
                ThrowReadOnlyTrieException();
            }

            int maxLevelForConcurrentCommit = _writeBeforeCommit switch
            {
                > 4 * 16 * 16 => 2, // we separate at three top levels
                > 4 * 16 => 1, // we separate at two top levels
                > 4 => 0, // we separate at top level
                _ => -1
            };

            _writeBeforeCommit = 0;

            using ICommitter committer = TrieStore.BeginCommit(RootRef, writeFlags);
            if (RootRef is not null && RootRef.IsDirty)
            {
                TreePath path = TreePath.Empty;
                Commit(committer, ref path, new NodeCommitInfo(RootRef), skipSelf: skipRoot, maxLevelForConcurrentCommit: maxLevelForConcurrentCommit);

                // reset objects
                RootRef!.ResolveKey(TrieStore, ref path, true, bufferPool: _bufferPool);
            }

            // Sometimes RootRef is set to null, so we still need to reset roothash to empty tree hash.
            SetRootHash(RootRef?.Keccak, true);
        }

        private void Commit(ICommitter committer, ref TreePath path, NodeCommitInfo nodeCommitInfo, int maxLevelForConcurrentCommit, bool skipSelf = false)
        {
            if (!_allowCommits)
            {
                ThrowReadOnlyTrieException();
            }

            TrieNode node = nodeCommitInfo.Node;
            if (node!.IsBranch)
            {
                if (path.Length > maxLevelForConcurrentCommit)
                {
                    for (int i = 0; i < 16; i++)
                    {
                        if (node.IsChildDirty(i))
                        {
                            path.AppendMut(i);
                            TrieNode childNode = node.GetChildWithChildPath(TrieStore, ref path, i);
                            Commit(committer, ref path, new NodeCommitInfo(childNode!, node, i), maxLevelForConcurrentCommit);
                            path.TruncateOne();
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
                    Task CreateTaskForPath(TreePath childPath, TrieNode childNode, int idx) => Task.Run(() =>
                    {
                        Commit(committer, ref childPath, new NodeCommitInfo(childNode!, node, idx), maxLevelForConcurrentCommit);
                        committer.ReturnConcurrencyQuota();
                    });

                    ArrayPoolList<Task>? childTasks = null;

                    for (int i = 0; i < 16; i++)
                    {
                        if (node.IsChildDirty(i))
                        {
                            if (i < 15 && committer.TryRequestConcurrentQuota())
                            {
                                childTasks ??= new ArrayPoolList<Task>(15);
                                TreePath childPath = path.Append(i);
                                TrieNode childNode = node.GetChildWithChildPath(TrieStore, ref childPath, i);
                                childTasks.Add(CreateTaskForPath(childPath, childNode, i));
                            }
                            else
                            {
                                path.AppendMut(i);
                                TrieNode childNode = node.GetChildWithChildPath(TrieStore, ref path, i);
                                Commit(committer, ref path, new NodeCommitInfo(childNode!, node, i), maxLevelForConcurrentCommit);
                                path.TruncateOne();
                            }
                        }
                        else
                        {
                            if (_logger.IsTrace)
                            {
                                Trace(node, ref path, i);
                            }
                        }
                    }

                    if (childTasks is not null)
                    {
                        Task.WaitAll(childTasks.AsSpan());
                        childTasks.Dispose();
                    }
                }
            }
            else if (node.NodeType == NodeType.Extension)
            {
                int previousPathLength = node.AppendChildPath(ref path, 0);
                TrieNode extensionChild = node.GetChildWithChildPath(TrieStore, ref path, 0);
                if (extensionChild is null)
                {
                    ThrowInvalidExtension();
                }

                if (extensionChild.IsDirty)
                {
                    Commit(committer, ref path, new NodeCommitInfo(extensionChild, node, 0), maxLevelForConcurrentCommit);
                }
                else
                {
                    if (_logger.IsTrace) TraceExtensionSkip(extensionChild);
                }
                path.TruncateMut(previousPathLength);
            }

            node.ResolveKey(TrieStore, ref path, nodeCommitInfo.IsRoot, bufferPool: _bufferPool);
            node.Seal();

            if (node.FullRlp.Length >= 32)
            {
                if (!skipSelf)
                {
                    committer.CommitNode(ref path, nodeCommitInfo);
                }
            }
            else
            {
                if (_logger.IsTrace) TraceSkipInlineNode(node);
            }

            [DoesNotReturn, StackTraceHidden]
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
                SpanSource result = Run(ref updatePathTreePath, SpanSource.Empty, nibbles, isUpdate: false, startRootHash: rootHash);
                if (array is not null) ArrayPool<byte>.Shared.Return(array);

                return result.IsNull ? ReadOnlySpan<byte>.Empty : result.Span;
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
                SpanSource result = Run(ref updatePathTreePath, SpanSource.Empty, nibbles, false, startRootHash: rootHash,
                    isNodeRead: true);
                return result.ToArray();
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
                SpanSource result = Run(ref updatePathTreePath, SpanSource.Empty, nibbles, false, startRootHash: rootHash,
                    isNodeRead: true);
                if (array is not null) ArrayPool<byte>.Shared.Return(array);
                return result.ToArray() ?? [];
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
            Set(rawKey, new SpanSource(value));
        }

        [SkipLocalsInit]
        [DebuggerStepThrough]
        public void Set(ReadOnlySpan<byte> rawKey, SpanSource value)
        {
            if (_logger.IsTrace) Trace(in rawKey, value);

            if (Interlocked.CompareExchange(ref _isWriteInProgress, 1, 0) != 0)
            {
                ThrowNonConcurrentWrites();
            }

            _writeBeforeCommit++;

            try
            {
                int nibblesCount = 2 * rawKey.Length;
                byte[] array = null;
                Span<byte> nibbles = (rawKey.Length <= MaxKeyStackAlloc
                        ? stackalloc byte[MaxKeyStackAlloc] // Fixed size stack allocation
                        : array = ArrayPool<byte>.Shared.Rent(nibblesCount))
                    [..nibblesCount]; // Slice to exact size

                Nibbles.BytesToNibbleBytes(rawKey, nibbles);

                if (_traverseStack is null) _traverseStack = new Stack<TraverseStack>();
                if (_traverseStack.Count > 0) _traverseStack.Clear();

                TreePath empty = TreePath.Empty;
                RootRef = SetNew(_traverseStack, nibbles, value, ref empty, RootRef);

                if (array is not null) ArrayPool<byte>.Shared.Return(array);
            }
            finally
            {
                Volatile.Write(ref _isWriteInProgress, 0);
            }

            void Trace(in ReadOnlySpan<byte> rawKey, SpanSource value)
            {
                _logger.Trace($"{(value.Length == 0 ? $"Deleting {rawKey.ToHexString(withZeroX: true)}" : $"Setting {rawKey.ToHexString(withZeroX: true)} = {value.Span.ToHexString(withZeroX: true)}")}");
            }

            [DoesNotReturn, StackTraceHidden]
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
                Set(rawKey, SpanSource.Empty);
            }
            else
            {
                SpanSource valueBytes = new(value.Bytes);
                Set(rawKey, valueBytes);
            }
        }

        internal TrieNode? SetNew(Stack<TraverseStack> traverseStack, Span<byte> remainingKey, SpanSource value, ref TreePath path, TrieNode? node)
        {
            TrieNode? originalNode = node;
            int originalPathLength = path.Length;

            while (true)
            {
                if (node is null)
                {
                    node = value.IsNullOrEmpty ? null : TrieNodeFactory.CreateLeaf(remainingKey.ToArray(), value);

                    // End traverse
                    break;
                }

                node.ResolveNode(TrieStore, path);

                if (node.IsLeaf || node.IsExtension)
                {
                    int commonPrefixLength = remainingKey.CommonPrefixLength(node.Key);
                    if (commonPrefixLength == node.Key!.Length)
                    {
                        if (node.IsExtension)
                        {
                            // Continue traversal to the child of the extension
                            path.AppendMut(node.Key);
                            TrieNode? extensionChild = node.GetChildWithChildPath(TrieStore, ref path, 0);

                            traverseStack.Push(new TraverseStack()
                            {
                                Node = node,
                                OriginalChild = extensionChild,
                                ChildIdx = 0,
                            });

                            // Continue loop with the child as current node
                            remainingKey = remainingKey[node!.Key.Length..];
                            node = extensionChild;

                            continue;
                        }

                        if (value.IsNullOrEmpty)
                        {
                            // Deletion
                            node = null;
                        }
                        else if (node.Value.Equals(value))
                        {
                            // SHORTCUT!
                            path.TruncateMut(originalPathLength);
                            traverseStack.Clear();
                            return originalNode;
                        }
                        else if (node.IsSealed)
                        {
                            node = node.CloneWithChangedValue(value);
                        }
                        else
                        {
                            node.Value = value;
                            node.Keccak = null; // For parent node usually done in SetChild.
                        }

                        // end traverse
                        break;
                    }

                    // We are suppose to create a branch, but no change in structure
                    if (value.IsNullOrEmpty)
                    {
                        // SHORTCUT!
                        path.TruncateMut(originalPathLength);
                        traverseStack.Clear();
                        return originalNode;
                    }

                    // Making a T branch here.
                    // If the commonPrefixLength > 0, we'll also need to also make an extension in front of the branch.
                    TrieNode theBranch = TrieNodeFactory.CreateBranch();

                    // This is the current node branch
                    int currentNodeNib = node.Key[commonPrefixLength];
                    if (node.Key.Length == commonPrefixLength + 1 && node.IsExtension)
                    {
                        // Collapsing the extension, taking the child directly and set the branch
                        int originalLength = path.Length;
                        path.AppendMut(node.Key);
                        theBranch[currentNodeNib] = node.GetChildWithChildPath(TrieStore, ref path, 0);
                        path.TruncateMut(originalLength);
                    }
                    else
                    {
                        // Note: could be a leaf at the end of the tree which now have zero length key
                        theBranch[currentNodeNib] = node.CloneWithChangedKey(node.Key.Slice(commonPrefixLength + 1));
                    }

                    // This is the new branch
                    theBranch[remainingKey[commonPrefixLength]] =
                        TrieNodeFactory.CreateLeaf(remainingKey[(commonPrefixLength + 1)..].ToArray(), value);

                    // Extension in front of the branch
                    node = commonPrefixLength == 0 ?
                        theBranch :
                        TrieNodeFactory.CreateExtension(remainingKey[..commonPrefixLength].ToArray(), theBranch);

                    break;
                }

                int nib = remainingKey[0];
                path.AppendMut(nib);
                TrieNode? child = node.GetChildWithChildPath(TrieStore, ref path, nib);

                traverseStack.Push(new TraverseStack()
                {
                    Node = node,
                    OriginalChild = child,
                    ChildIdx = nib,
                });

                // Continue loop with child as current node
                node = child;
                remainingKey = remainingKey[1..];
            }

            while (traverseStack.TryPop(out TraverseStack cStack))
            {
                TrieNode? child = node;
                node = cStack.Node;

                if (node.IsExtension)
                {
                    path.TruncateMut(path.Length - node.Key!.Length);

                    if (ShouldUpdateChild(node, cStack.OriginalChild, child))
                    {
                        if (child is null)
                        {
                            node = null; // Remove extension
                            continue;
                        }

                        if (child.IsExtension || child.IsLeaf)
                        {
                            // Merge current node with child
                            node = child.CloneWithChangedKey(Bytes.Concat(node.Key, child.Key));
                        }
                        else
                        {
                            if (node.IsSealed) node = node.Clone();
                            node.SetChild(0, child);
                        }
                    }

                    continue;
                }

                // Branch only
                int nib = cStack.ChildIdx;

                bool hasRemove = false;
                path.TruncateOne();

                if (ShouldUpdateChild(node, cStack.OriginalChild, child))
                {
                    if (child is null) hasRemove = true;
                    if (node.IsSealed) node = node.Clone();

                    node.SetChild(nib, child);
                }

                if (!hasRemove)
                {
                    // 99%
                    continue;
                }

                // About 1% reach here
                node = MaybeCombineNode(ref path, node);
            }

            return node;
        }

        internal bool ShouldUpdateChild(TrieNode parent, TrieNode? oldChild, TrieNode? newChild)
        {
            if (oldChild is null && newChild is null) return false;
            if (!ReferenceEquals(oldChild, newChild)) return true;
            if (newChild.Keccak is null && parent.Keccak is not null) return true; // So that recalculate root knows to recalculate the parent root.
            return true;
        }

        /// <summary>
        /// Tries to make the current node an extension or null if it has only one child left.
        /// </summary>
        /// <param name="path"></param>
        /// <param name="node"></param>
        /// <returns></returns>
        internal TrieNode? MaybeCombineNode(ref TreePath path, in TrieNode? node)
        {
            int onlyChildIdx = -1;
            TrieNode? onlyChildNode = null;
            path.AppendMut(0);
            var iterator = node.CreateChildIterator();
            for (int i = 0; i < TrieNode.BranchesCount; i++)
            {
                path.SetLast(i);
                TrieNode? child = iterator.GetChildWithChildPath(TrieStore, ref path, i);

                if (child is not null)
                {
                    if (onlyChildIdx == -1)
                    {
                        onlyChildIdx = i;
                        onlyChildNode = child;
                    }
                    else
                    {
                        // 63%
                        // More than one non null child. We don't care anymore.
                        path.TruncateOne();
                        return node;
                    }
                }

            }
            path.TruncateOne();

            if (onlyChildIdx == -1) return null; // No child at all.

            path.AppendMut(onlyChildIdx);
            onlyChildNode.ResolveNode(TrieStore, path);
            path.TruncateOne();

            if (onlyChildNode.IsBranch)
            {
                return TrieNodeFactory.CreateExtension([(byte)onlyChildIdx], onlyChildNode);
            }

            // 35%
            // Replace the only child with something with extra key.
            byte[] newKey = Bytes.Concat((byte)onlyChildIdx, onlyChildNode.Key);
            TrieNode tn = onlyChildNode.CloneWithChangedKey(newKey);
            return tn;
        }

        public struct TraverseStack
        {
            public TrieNode Node;
            public int ChildIdx;
            public TrieNode? OriginalChild;
        }

        private SpanSource Run(
            ref TreePath updatePathTreePath,
            SpanSource updateValue,
            Span<byte> updatePath,
            bool isUpdate,
            Hash256? startRootHash = null,
            bool isNodeRead = false)
        {
            TraverseContext traverseContext =
                new(updatePath, ref updatePathTreePath, updateValue, isUpdate, isNodeRead: isNodeRead);

            if (startRootHash is not null)
            {
                if (_logger.IsTrace) TraceStart(startRootHash, in traverseContext);
                TreePath startingPath = TreePath.Empty;
                TrieNode startNode = TrieStore.FindCachedOrUnknown(startingPath, startRootHash);
                ResolveNode(startNode, in traverseContext, in startingPath);
                return startNode.IsBranch ?
                    TraverseBranches(startNode, ref startingPath, traverseContext) :
                    TraverseNode(startNode, traverseContext, ref startingPath);
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
                        RootRef = TrieNodeFactory.CreateLeaf(key, traverseContext.UpdateValue);
                    }

                    if (_logger.IsTrace) TraceNull(in traverseContext);
                    return traverseContext.UpdateValue;
                }
                else
                {
                    TreePath startingPath = TreePath.Empty;
                    ResolveNode(RootRef, in traverseContext, in startingPath);
                    if (_logger.IsTrace) TraceNode(in traverseContext);
                    return RootRef.IsBranch ?
                        TraverseBranches(RootRef, ref startingPath, traverseContext) :
                        TraverseNode(RootRef, traverseContext, ref startingPath);
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
                ThrowDecodingError(node, in path, rlpException);
            }

            [DoesNotReturn, StackTraceHidden]
            static void ThrowDecodingError(TrieNode node, in TreePath path, RlpException rlpException)
            {
                var exception = new TrieNodeException($"Error when decoding node {node.Keccak}", path, node.Keccak ?? Keccak.Zero, rlpException);
                exception = (TrieNodeException)ExceptionDispatchInfo.SetCurrentStackTrace(exception);
                throw exception;
            }
        }

        private SpanSource TraverseNode(TrieNode node, scoped in TraverseContext traverseContext, scoped ref TreePath path)
        {
            if (_logger.IsTrace) Trace(node, traverseContext);

            if (traverseContext.IsNodeRead && traverseContext.RemainingUpdatePathLength == 0)
            {
                return node.FullRlp;
            }

            switch (node.NodeType)
            {
                case NodeType.Extension:
                    return TraverseExtension(node, in traverseContext, ref path);
                case NodeType.Leaf:
                    return TraverseLeaf(node, in traverseContext, ref path);
                default:
                    return TraverseInvalid(node);
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            void Trace(TrieNode node, in TraverseContext traverseContext)
            {
                _logger.Trace($"Traversing {node} to {(traverseContext.IsReadValue ? "READ" : traverseContext.IsDelete ? "DELETE" : "UPDATE")}");
            }

            [DoesNotReturn, StackTraceHidden]
            static SpanSource TraverseInvalid(TrieNode node)
            {
                switch (node.NodeType)
                {
                    case NodeType.Branch:
                        return TraverseBranch(node);
                    case NodeType.Unknown:
                        return TraverseUnknown(node);
                    default:
                        return ThrowNotSupported(node);
                }
            }

            [DoesNotReturn, StackTraceHidden]
            static SpanSource TraverseBranch(TrieNode node)
            {
                throw new InvalidOperationException($"Branch node {node.Keccak} should already be handled");
            }

            [DoesNotReturn, StackTraceHidden]
            static SpanSource TraverseUnknown(TrieNode node)
            {
                throw new InvalidOperationException($"Cannot traverse unresolved node {node.Keccak}");
            }

            [DoesNotReturn, StackTraceHidden]
            static SpanSource ThrowNotSupported(TrieNode node)
            {
                throw new NotSupportedException($"Unknown node type {node.NodeType}");
            }
        }

        private void ConnectNodes(TrieNode? node, in TraverseContext traverseContext)
        {
            // Fast tail-calls into generic
            if (_logger.IsTrace)
            {
                ConnectNodes<OnFlag>(node, in traverseContext);
            }
            else
            {
                // branch eliminates _loggerTrace statements
                ConnectNodes<OffFlag>(node, in traverseContext);
            }
        }

        private void ConnectNodes<IsTrace>(TrieNode? node, in TraverseContext traverseContext)
            where IsTrace : IFlag
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
                // Use NodeType once to reduce virtual calls
                // and switch statement to not add additional long lived local variable
                switch (node.NodeType)
                {
                    case NodeType.Branch:
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
                                goto Corruption;
                            }

                            ResolveNode(childNode, in traverseContext, in path);
                            path.TruncateOne();

                            NodeType childType = childNode.NodeType;
                            if (childType == NodeType.Branch)
                            {
                                TrieNode extensionFromBranch = TrieNodeFactory.CreateExtension(_singleByteKeys[childNodeIndex], childNode);
                                if (IsTrace.IsActive) _logger.Trace($"Extending child {childNodeIndex} {childNode} of {node} into {extensionFromBranch}");

                                nextNode = extensionFromBranch;
                            }
                            else if (childType == NodeType.Extension || childType == NodeType.Leaf)
                            {
                                byte[] newKey = Bytes.Concat((byte)childNodeIndex, childNode.Key);
                                TrieNode extendedNode = childNode.CloneWithChangedKey(newKey);

                                if (IsTrace.IsActive)
                                {
                                    if (childNode.IsExtension)
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

                                        _logger.Trace($"Extending child {childNodeIndex} {childNode} of {node} into {extendedNode}");
                                    }
                                    else if (childNode.IsLeaf)
                                    {
                                        _logger.Trace($"Extending branch child {childNodeIndex} {childNode} into {extendedNode}");
                                        _logger.Trace($"Decrementing ref on a leaf extended up to eat a branch {childNode}");
                                        if (node.IsSealed)
                                        {
                                            _logger.Trace($"Decrementing ref on a branch replaced by a leaf {node}");
                                        }
                                    }
                                }
                                nextNode = extendedNode;
                            }
                            else
                            {
                                node = childNode;
                                goto InvalidNodeType;
                            }
                        }
                        break;
                    case NodeType.Extension:
                        if (nextNode is null)
                        {
                            goto NullNode;
                        }

                        NodeType nextType = nextNode.NodeType;
                        if (nextType == NodeType.Branch)
                        {
                            if (node.IsSealed)
                            {
                                node = node.Clone();
                            }

                            if (IsTrace.IsActive) _logger.Trace($"Connecting {node} with {nextNode}");
                            node.SetChild(0, nextNode);
                            nextNode = node;
                        }
                        else if (nextType == NodeType.Extension || nextType == NodeType.Leaf)
                        {
                            /* to test the Extension case we need something like this initially */
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
                            TrieNode extendedNode = nextNode.CloneWithChangedKey(newKey);
                            if (IsTrace.IsActive) _logger.Trace($"Combining {node} and {nextNode} into {extendedNode}");
                            nextNode = extendedNode;
                        }
                        else
                        {
                            node = nextNode;
                            goto InvalidNodeType;
                        }
                        break;
                    case NodeType.Leaf:
                        goto LeafCannotBeParent;
                    default:
                        goto InvalidNodeType;
                }
            }

            RootRef = nextNode;
            return;

        Corruption:
            ThrowTrieExceptionCorruption();
        InvalidNodeType:
            ThrowInvalidNodeType(node);
        NullNode:
            ThrowInvalidNullNode(node);
        LeafCannotBeParent:
            ThrowTrieExceptionLeafCannotBeParent(node, nextNode);

            [DoesNotReturn, StackTraceHidden]
            static void ThrowTrieExceptionLeafCannotBeParent(TrieNode node, TrieNode nextNode)
                => throw new TrieException($"{nameof(NodeType.Leaf)} {node} cannot be a parent of {nextNode}");

            [DoesNotReturn, StackTraceHidden]
            static void ThrowTrieExceptionCorruption()
                => throw new TrieException("Before updating branch should have had at least two non-empty children");

            [DoesNotReturn, StackTraceHidden]
            static void ThrowInvalidNodeType(TrieNode node)
                => throw new InvalidOperationException($"Unknown node type {node.NodeType}");

            [DoesNotReturn, StackTraceHidden]
            static void ThrowInvalidNullNode(TrieNode node)
                => throw new InvalidOperationException($"An attempt to set a null node as a child of the {node}");
        }

        private SpanSource TraverseBranches(TrieNode node, scoped ref TreePath path, TraverseContext traverseContext)
        {
            while (true)
            {
                if (traverseContext.RemainingUpdatePathLength == 0)
                {
                    return ResolveBranchNode(node, in traverseContext);
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
                    return ResolveCurrent(in traverseContext);
                }

                ResolveNode(childNode, in traverseContext, in path);

                traverseContext = traverseContext.WithNewIndex(traverseContext.CurrentIndex + 1);
                if (!childNode.IsBranch)
                {
                    return TraverseNode(childNode, in traverseContext, ref path);
                }

                // Traverse next branch
                node = childNode;
            }
        }

        private SpanSource TraverseLeaf(TrieNode node, scoped in TraverseContext traverseContext, scoped ref TreePath path)
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

            SpanSource shorterPathValue = default;
            SpanSource longerPathValue = default;
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

            int extensionLength = shorterPath.CommonPrefixLength(longerPath);
            if (extensionLength == shorterPath.Length && extensionLength == longerPath.Length)
            {
                if (traverseContext.IsNodeRead)
                {
                    return node.FullRlp;
                }
                if (traverseContext.IsReadValue)
                {
                    return node.Value;
                }

                if (traverseContext.IsDelete)
                {
                    ConnectNodes(null, in traverseContext);
                    return traverseContext.UpdateValue;
                }

                if (!node.Value.Equals(traverseContext.UpdateValue))
                {
                    TrieNode withUpdatedValue = node.CloneWithChangedValue(traverseContext.UpdateValue);
                    ConnectNodes(withUpdatedValue, in traverseContext);
                    return traverseContext.UpdateValue;
                }

                return traverseContext.UpdateValue;
            }

            if (traverseContext.IsRead || traverseContext.IsDelete)
            {
                return SpanSource.Null;
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
                ReadOnlySpan<byte> shortLeafPath = shorterPath[(extensionLength + 1)..];
                TrieNode shortLeaf = TrieNodeFactory.CreateLeaf(shortLeafPath.ToArray(), shorterPathValue);
                branch.SetChild(shorterPath[extensionLength], shortLeaf);
            }

            ReadOnlySpan<byte> leafPath = longerPath[(extensionLength + 1)..];
            TrieNode withUpdatedKeyAndValue = node.CloneWithChangedKeyAndValue(
                leafPath.ToArray(), longerPathValue);

            PushToNodeStack(branch, traverseContext.CurrentIndex, longerPath[extensionLength]);
            ConnectNodes(withUpdatedKeyAndValue, in traverseContext);

            return traverseContext.UpdateValue;
        }

        private SpanSource TraverseExtension(TrieNode node, scoped in TraverseContext traverseContext, scoped ref TreePath path)
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
                return next.IsBranch ?
                    TraverseBranches(next, ref path, newContext) :
                    TraverseNode(next, newContext, ref path);
            }

            if (traverseContext.IsRead || traverseContext.IsDelete)
            {
                return SpanSource.Null;
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
                byte[] remainingPath = remaining[(extensionLength + 1)..].ToArray();
                TrieNode shortLeaf = TrieNodeFactory.CreateLeaf(remainingPath, traverseContext.UpdateValue);
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
            return traverseContext.UpdateValue;
        }

        private SpanSource ResolveCurrent(scoped in TraverseContext traverseContext)
        {
            if (traverseContext.IsRead || traverseContext.IsDelete)
            {
                return default;
            }

            int currentIndex = traverseContext.CurrentIndex + 1;
            byte[] leafPath = traverseContext.UpdatePath[
                currentIndex..].ToArray();
            TrieNode leaf = TrieNodeFactory.CreateLeaf(leafPath, traverseContext.UpdateValue);
            ConnectNodes(leaf, in traverseContext);

            return traverseContext.UpdateValue;
        }

        private SpanSource ResolveBranchNode(TrieNode node, scoped in TraverseContext traverseContext)
        {
            // all these cases when the path ends on the branch assume a trie with values in the branches
            // which is not possible within the Ethereum protocol which has keys of the same length (64)

            if (traverseContext.IsNodeRead)
            {
                return node.FullRlp;
            }
            if (traverseContext.IsReadValue)
            {
                return node.Value;
            }

            if (traverseContext.IsDelete)
            {
                if (node.Value.IsNull)
                {
                    return SpanSource.Null;
                }

                ConnectNodes(null, in traverseContext);
            }
            else if (traverseContext.UpdateValue.Equals(node.Value))
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

        private readonly ref struct TraverseContext
        {
            public readonly SpanSource UpdateValue;
            public readonly ReadOnlySpan<byte> UpdatePath;
            public readonly ref readonly TreePath UpdatePathTreePath;
            public bool IsUpdate { get; }
            public bool IsNodeRead { get; }
            public bool IsReadValue => !IsUpdate && !IsNodeRead;
            public bool IsRead => IsNodeRead || IsReadValue;
            public bool IsDelete => IsUpdate && UpdateValue.IsNull;
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
                SpanSource updateValue,
                bool isUpdate,
                bool ignoreMissingDelete = true,
                bool isNodeRead = false)
            {
                UpdatePath = updatePath;
                UpdatePathTreePath = ref updatePathTreePath;
                UpdateValue = updateValue.IsNotNull && updateValue.Length == 0 ? SpanSource.Null : updateValue;
                IsUpdate = isUpdate;
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
                        visitor.VisitMissingNode(default, rootHash);
                        return false;
                    }
                }

                return true;
            }

            if (!visitor.IsFullDbScan)
            {
                visitor.VisitTree(default, rootHash);
                if (TryGetRootRef(out TrieNode rootRef))
                {
                    TreePath emptyPath = TreePath.Empty;
                    rootRef?.Accept(visitor, default, resolver, ref emptyPath, trieVisitContext);
                }
            }
            // Full db scan
            else if (TrieStore.Scheme == INodeStorage.KeyScheme.Hash && visitingOptions.FullScanMemoryBudget != 0)
            {
                visitor.VisitTree(default, rootHash);
                BatchedTrieVisitor<TNodeContext> batchedTrieVisitor = new(visitor, resolver, visitingOptions);
                batchedTrieVisitor.Start(rootHash, trieVisitContext);
            }
            else if (TryGetRootRef(out TrieNode rootRef))
            {
                TreePath emptyPath = TreePath.Empty;
                visitor.VisitTree(default, rootHash);
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

            [DoesNotReturn, StackTraceHidden]
            static void Throw() => throw new InvalidOperationException($"Nothing on {nameof(_nodeStack)}");
        }

        [DoesNotReturn, StackTraceHidden]
        static void ThrowReadOnlyTrieException() => throw new TrieException("Commits are not allowed on this trie.");

        [DoesNotReturn, StackTraceHidden]
        private static void ThrowInvalidDataException(TrieNode originalNode)
        {
            throw new InvalidDataException(
                $"Extension {originalNode.Keccak} has no child.");
        }

        [DoesNotReturn, StackTraceHidden]
        private static void ThrowMissingChildException(TrieNode node)
        {
            throw new TrieException(
                $"Found an {nameof(NodeType.Extension)} {node.Keccak} that is missing a child.");
        }

        [DoesNotReturn, StackTraceHidden]
        private static void ThrowMissingPrefixException()
        {
            throw new InvalidDataException("An attempt to visit a node without a prefix path.");
        }
    }
}
