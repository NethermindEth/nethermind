// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
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
    public partial class PatriciaTree
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
                RootRef = Commit(committer, ref path, RootRef, skipSelf: skipRoot, maxLevelForConcurrentCommit: maxLevelForConcurrentCommit);
            }

            // Sometimes RootRef is set to null, so we still need to reset roothash to empty tree hash.
            SetRootHash(RootRef?.Keccak, true);
        }

        private TrieNode Commit(ICommitter committer, ref TreePath path, TrieNode node, int maxLevelForConcurrentCommit, bool skipSelf = false)
        {
            if (!_allowCommits)
            {
                ThrowReadOnlyTrieException();
            }

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
                            TrieNode newChildNode = Commit(committer, ref path, childNode, maxLevelForConcurrentCommit);
                            if (!ReferenceEquals(childNode, newChildNode))
                            {
                                node[i] = newChildNode;
                            }
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
                        TrieNode newChild = Commit(committer, ref childPath, childNode!, maxLevelForConcurrentCommit);
                        if (!ReferenceEquals(childNode, newChild))
                        {
                            node[idx] = newChild;
                        }
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
                                TrieNode newChildNode = Commit(committer, ref path, childNode!, maxLevelForConcurrentCommit);
                                if (!ReferenceEquals(childNode, newChildNode))
                                {
                                    node[i] = newChildNode;
                                }
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
                    TrieNode newExtensionChild = Commit(committer, ref path, extensionChild, maxLevelForConcurrentCommit);
                    if (!ReferenceEquals(newExtensionChild, extensionChild))
                    {
                        node[0] = newExtensionChild;
                    }
                }
                else
                {
                    if (_logger.IsTrace) TraceExtensionSkip(extensionChild);
                }
                path.TruncateMut(previousPathLength);
            }

            node.ResolveKey(TrieStore, ref path, bufferPool: _bufferPool);
            node.Seal();

            if (node.FullRlp.Length >= 32)
            {
                if (!skipSelf)
                {
                    node = committer.CommitNode(ref path, node);
                }
            }
            else
            {
                if (_logger.IsTrace) TraceSkipInlineNode(node);
            }

            return node;

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
            RootRef?.ResolveKey(TrieStore, ref path, bufferPool: _bufferPool, canBeParallel);
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
            byte[]? array = null;
            try
            {
                int nibblesCount = 2 * rawKey.Length;
                Span<byte> nibbles = (rawKey.Length <= MaxKeyStackAlloc
                        ? stackalloc byte[MaxKeyStackAlloc]
                        : array = ArrayPool<byte>.Shared.Rent(nibblesCount))
                    [..nibblesCount]; // Slice to exact size;

                Nibbles.BytesToNibbleBytes(rawKey, nibbles);

                TreePath emptyPath = TreePath.Empty;
                TrieNode root = RootRef;

                if (rootHash is not null)
                {
                    root = TrieStore.FindCachedOrUnknown(emptyPath, rootHash);
                }

                SpanSource result = GetNew(nibbles, ref emptyPath, root, isNodeRead: false);

                return result.IsNull ? ReadOnlySpan<byte>.Empty : result.Span;
            }
            catch (TrieException e)
            {
                EnhanceException(rawKey, rootHash ?? RootHash, e);
                throw;
            }
            finally
            {
                if (array is not null) ArrayPool<byte>.Shared.Return(array);
            }
        }

        [DebuggerStepThrough]
        public byte[]? GetNodeByPath(byte[] nibbles, Hash256? rootHash = null)
        {
            try
            {
                TreePath emptyPath = TreePath.Empty;
                TrieNode root = RootRef;
                if (rootHash is not null)
                {
                    root = TrieStore.FindCachedOrUnknown(emptyPath, rootHash);
                }
                SpanSource result = GetNew(nibbles, ref emptyPath, root, isNodeRead: true);
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
            byte[]? array = null;
            try
            {
                int nibblesCount = 2 * rawKey.Length;
                Span<byte> nibbles = (nibblesCount <= MaxKeyStackAlloc
                        ? stackalloc byte[MaxKeyStackAlloc]
                        : array = ArrayPool<byte>.Shared.Rent(nibblesCount))
                    [..nibblesCount]; // Slice to exact size;
                Nibbles.BytesToNibbleBytes(rawKey, nibbles);

                TreePath emptyPath = TreePath.Empty;
                TrieNode root = RootRef;
                if (rootHash is not null)
                {
                    root = TrieStore.FindCachedOrUnknown(emptyPath, rootHash);
                }
                SpanSource result = GetNew(nibbles, ref emptyPath, root, isNodeRead: true);

                return result.ToArray() ?? [];
            }
            catch (TrieException e)
            {
                EnhanceException(rawKey, rootHash ?? RootHash, e);
                throw;
            }
            finally
            {
                if (array is not null) ArrayPool<byte>.Shared.Return(array);
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

            byte[]? array = null;
            try
            {
                int nibblesCount = 2 * rawKey.Length;
                Span<byte> nibbles = (rawKey.Length <= MaxKeyStackAlloc
                        ? stackalloc byte[MaxKeyStackAlloc] // Fixed size stack allocation
                        : array = ArrayPool<byte>.Shared.Rent(nibblesCount))
                    [..nibblesCount]; // Slice to exact size

                Nibbles.BytesToNibbleBytes(rawKey, nibbles);

                if (_traverseStack is null) _traverseStack = new Stack<TraverseStack>();
                else if (_traverseStack.Count > 0) _traverseStack.Clear();

                TreePath empty = TreePath.Empty;
                RootRef = SetNew(_traverseStack, nibbles, value, ref empty, RootRef);

            }
            finally
            {
                Volatile.Write(ref _isWriteInProgress, 0);
                if (array is not null) ArrayPool<byte>.Shared.Return(array);
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

        private TrieNode? SetNew(Stack<TraverseStack> traverseStack, Span<byte> remainingKey, SpanSource value, ref TreePath path, TrieNode? node)
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
            return false;
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

        private record struct TraverseStack
        {
            public TrieNode Node;
            public int ChildIdx;
            public TrieNode? OriginalChild;
        }

        private SpanSource GetNew(Span<byte> remainingKey, ref TreePath path, TrieNode? node, bool isNodeRead)
        {
            int originalPathLength = path.Length;

            try
            {
                while (true)
                {
                    if (node is null)
                    {
                        // If node read, then missing node. If value read.... what is it suppose to be then?
                        return default;
                    }

                    node.ResolveNode(TrieStore, path);

                    if (isNodeRead && remainingKey.Length == 0)
                    {
                        return node.FullRlp;
                    }

                    if (node.IsLeaf || node.IsExtension)
                    {
                        int commonPrefixLength = remainingKey.CommonPrefixLength(node.Key);
                        if (commonPrefixLength == node.Key!.Length)
                        {
                            if (node.IsLeaf)
                            {
                                if (!isNodeRead && commonPrefixLength == remainingKey.Length) return node.Value;

                                // Um..... leaf cannot have child
                                return default;
                            }

                            // Continue traversal to the child of the extension
                            path.AppendMut(node.Key);
                            TrieNode? extensionChild = node.GetChildWithChildPath(TrieStore, ref path, 0);
                            remainingKey = remainingKey[node!.Key.Length..];
                            node = extensionChild;

                            continue;
                        }

                        // No node match
                        return default;
                    }

                    int nib = remainingKey[0];
                    path.AppendMut(nib);
                    TrieNode? child = node.GetChildWithChildPath(TrieStore, ref path, nib);

                    // Continue loop with child as current node
                    node = child;
                    remainingKey = remainingKey[1..];
                }
            }
            finally
            {
                path.TruncateMut(originalPathLength);
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
