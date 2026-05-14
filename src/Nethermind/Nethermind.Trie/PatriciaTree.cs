// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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

        private readonly ILogger _logger;

        public const int OneNodeAvgMemoryEstimate = 384;

        /// <summary>
        ///     0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421
        /// </summary>
        public static readonly Hash256 EmptyTreeHash = Keccak.EmptyTreeHash;

        public TrieType TrieType { get; init; }

        [ThreadStatic]
        private static TraverseStack? _threadStaticTraverseStack;

        private static TraverseStack GetTraverseStack()
        {
            TraverseStack stack = _threadStaticTraverseStack ?? new();
            _threadStaticTraverseStack = null;
            return stack;
        }

        private static void ReturnTraverseStack(TraverseStack stack) => _threadStaticTraverseStack = stack;
        public readonly IScopedTrieStore TrieStore;
        private readonly ITrieNodeResolver _readResolver;
        public ICappedArrayPool? _bufferPool;

        private readonly bool _allowCommits;

        private int _isWriteInProgress;

        private Hash256 _rootHash = Keccak.EmptyTreeHash;

        // Distinct sentinel instance of TrieNodeNullSentinel (same class as TrieNode.NullNode
        // which marks empty branch slots, but a different instance - identity, not type,
        // disambiguates). When _rootRef holds this sentinel, the root has not yet been
        // lazy-resolved; any other value (typed TrieNode or null) is the authoritative
        // result of either lazy-resolve or an explicit setter call.
        private static readonly TrieNode _unresolvedSentinel = new TrieNodeNullSentinel();

        private TrieNode? _rootRef = _unresolvedSentinel;
        private readonly Lock _rootRefLock = new();

        /// <summary>
        /// The in-memory root of this trie. Three states for the backing field:
        /// <list type="bullet">
        /// <item><see cref="_unresolvedSentinel"/> - not yet lazy-resolved; first read
        /// loads from the underlying store via <see cref="ITrieNodeResolver.GetOrLoadNode"/>,
        /// or returns null when <see cref="_rootHash"/> == <see cref="Keccak.EmptyTreeHash"/>.
        /// A non-empty hash that cannot be resolved surfaces as
        /// <see cref="MissingTrieNodeException"/> - state root absent is an invariant
        /// violation, not "empty trie".</item>
        /// <item><c>null</c> - authoritative empty root (delete-all, selfdestruct, or
        /// explicit clear via the setter). Sticks; no re-resolve.</item>
        /// <item>typed <see cref="TrieNode"/> - resolved root.</item>
        /// </list>
        /// All state transitions are single atomic reference writes; the sentinel
        /// collapses what used to be a (field, flag) pair so there is no torn-state
        /// window between the two values and the setter/invalidate paths need no lock.
        /// </summary>
        public TrieNode? RootRef
        {
            get
            {
                TrieNode? local = Volatile.Read(ref _rootRef);
                if (!ReferenceEquals(local, _unresolvedSentinel))
                {
                    return local;
                }

                lock (_rootRefLock)
                {
                    local = _rootRef;
                    if (!ReferenceEquals(local, _unresolvedSentinel))
                    {
                        return local;
                    }

                    Hash256 rootHash = _rootHash;
                    TrieNode? resolved = null;
                    if (!ReferenceEquals(rootHash, Keccak.EmptyTreeHash))
                    {
                        // Throwing variant: a non-empty _rootHash that cannot be resolved
                        // is an invariant violation (state root missing from the store).
                        // The silent Try-shape would convert that into "trie appears empty",
                        // which corrupts downstream reads (account balances return 0,
                        // transactions fail with bogus "insufficient funds", etc.). Let the
                        // MissingTrieNodeException propagate so the caller sees the real fault.
                        resolved = _readResolver.GetOrLoadNode(in TreePath.Empty, in rootHash.ValueHash256);
                    }
                    Volatile.Write(ref _rootRef, resolved);
                    return resolved;
                }
            }
            set => Volatile.Write(ref _rootRef, value);
        }

        // Used to estimate if parallelization is needed during commit
        private long _writeBeforeCommit = 0;

        /// <summary>
        /// Only used in EthereumTests
        /// </summary>
        internal TrieNode? Root
        {
            get
            {
                if (RootRef is { } root)
                {
                    TrieNode.ResolveNode(ref root, TrieStore, in TreePath.Empty);
                    RootRef = root;
                }
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
            _readResolver = TrieStore.AsReadOnlyTraversal();
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

            TrieNode? newRoot = RootRef;
            using (ICommitter committer = TrieStore.BeginCommit(RootRef, writeFlags))
            {
                if (RootRef is not null && RootRef.IsDirty)
                {
                    TreePath path = TreePath.Empty;
                    newRoot = Commit(committer, ref path, RootRef, skipSelf: skipRoot, maxLevelForConcurrentCommit: maxLevelForConcurrentCommit);
                }
            }

            // Need to be after committer dispose so that it can find it in trie store properly
            RootRef = newRoot;

            // resetObjects:false - we just published the freshly-resolved typed root above.
            // Passing true would invalidate it back to the unresolved sentinel and force the
            // next RootRef read to lazy-load through the read resolver, which during genesis
            // processing cannot yet see the committed dirty nodes through that path.
            SetRootHash(newRoot?.Keccak, resetObjects: false);
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
                    path.AppendMut(0);
                    for (int i = 0; i < 16; i++)
                    {
                        if (node.TryGetDirtyChild(i, out TrieNode? childNode))
                        {
                            path.SetLast(i);
                            TrieNode newChildNode = Commit(committer, ref path, childNode, maxLevelForConcurrentCommit);
                            if (!ReferenceEquals(childNode, newChildNode))
                            {
                                node[i] = newChildNode;
                            }
                        }
                        else
                        {
                            if (_logger.IsTrace)
                            {
                                path.SetLast(i);
                                Trace(node, ref path, i);
                            }
                        }
                    }
                    path.TruncateOne();
                }
                else
                {
                    ArrayPoolList<Task>? childTasks = null;

                    path.AppendMut(0);
                    for (int i = 0; i < 16; i++)
                    {
                        if (node.TryGetDirtyChild(i, out TrieNode childNode))
                        {
                            path.SetLast(i);
                            if (i < 15 && committer.TryRequestConcurrentQuota())
                            {
                                childTasks ??= new ArrayPoolList<Task>(15);
                                // path is copied here
                                childTasks.Add(CreateTaskForPath(committer, node, maxLevelForConcurrentCommit, path, childNode, i));
                            }
                            else
                            {
                                TrieNode newChildNode = Commit(committer, ref path, childNode!, maxLevelForConcurrentCommit);
                                if (!ReferenceEquals(childNode, newChildNode))
                                {
                                    node[i] = newChildNode;
                                }
                            }
                        }
                        else
                        {
                            if (_logger.IsTrace)
                            {
                                path.SetLast(i);
                                Trace(node, ref path, i);
                            }
                        }
                    }
                    path.TruncateOne();

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
                if (node.TryGetDirtyChild(0, out TrieNode? extensionChild))
                {
                    TrieNode newExtensionChild = Commit(committer, ref path, extensionChild, maxLevelForConcurrentCommit);
                    if (!ReferenceEquals(newExtensionChild, extensionChild))
                    {
                        node[0] = newExtensionChild;
                    }
                }
                else if (_logger.IsTrace)
                {
                    extensionChild = node.GetChildWithChildPath(TrieStore, ref path, 0);
                    if (extensionChild is null)
                    {
                        ThrowInvalidExtension();
                    }

                    TraceExtensionSkip(extensionChild);
                }
                path.TruncateMut(previousPathLength);
            }

            // The child should already have all key calculated at this point, so canBeParallel flag is set
            // to false to reduce overhead.
            node.ResolveKey(TrieStore, ref path, bufferPool: _bufferPool, canBeParallel: false);
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
                TrieNode child = node.GetChildWithChildPath(TrieStore, ref path, i);
                if (child is not null)
                {
                    _logger.Trace($"Skipping commit of {child}");
                }
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceExtensionSkip(TrieNode extensionChild) => _logger.Trace($"Skipping commit of {extensionChild}");

            [MethodImpl(MethodImplOptions.NoInlining)]
            void TraceSkipInlineNode(TrieNode node) => _logger.Trace($"Skipping commit of an inlined {node}");
        }

        private Task CreateTaskForPath(ICommitter committer, TrieNode node, int maxLevelForConcurrentCommit, TreePath childPath, TrieNode childNode, int idx) => Task.Factory.StartNew(
            _ =>
            {
                try
                {
                    TrieNode newChild = Commit(committer, ref childPath, childNode!, maxLevelForConcurrentCommit);
                    if (!ReferenceEquals(childNode, newChild))
                        node[idx] = newChild;
                }
                finally
                {
                    committer.ReturnConcurrencyQuota();
                }
            },
            state: null,
            CancellationToken.None,
            TaskCreationOptions.None,
            TaskScheduler.Default);

        public void UpdateRootHash(bool canBeParallel = true)
        {
            TreePath path = TreePath.Empty;
            RootRef?.ResolveKey(TrieStore, ref path, bufferPool: _bufferPool, canBeParallel);
            SetRootHash(RootRef?.Keccak ?? EmptyTreeHash, false);
        }

        public void SetRootHash(Hash256? value, bool resetObjects)
        {
            Hash256 rootHash = value ?? Keccak.EmptyTreeHash; // nulls were allowed before so for now we leave it this way
            if (resetObjects && _rootHash == rootHash)
            {
                TrieNode? rootRef = Volatile.Read(ref _rootRef);
                if (!ReferenceEquals(rootRef, _unresolvedSentinel)
                    && rootRef is not null
                    && !rootRef.IsDirty
                    && rootRef.TryGetKeccak(out ValueHash256 rootKeccak)
                    && rootKeccak == rootHash.ValueHash256)
                {
                    return;
                }
            }

            _rootHash = rootHash;
            if (_rootHash == Keccak.EmptyTreeHash)
            {
                RootRef = null;
            }
            else if (resetObjects)
            {
                // Publish the unresolved sentinel atomically; the next RootRef read
                // lazily resolves the new root hash via _readResolver.GetOrLoadNode.
                // No lock needed - the sentinel transition is a single atomic ref write.
                Volatile.Write(ref _rootRef, _unresolvedSentinel);
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
                    // Fuse the legacy FindCachedOrUnknown + ResolveNode pair: GetOrLoadNode
                    // returns a fully resolved typed node and never publishes an Unknown placeholder.
                    root = _readResolver.GetOrLoadNode(emptyPath, rootHash);
                }

                CappedArray<byte> result = GetNew(nibbles, ref emptyPath, root, isNodeRead: false);

                return result.IsNull ? ReadOnlySpan<byte>.Empty : result.AsSpan();
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

        [SkipLocalsInit]
        [DebuggerStepThrough]
        public void WarmUpPath(ReadOnlySpan<byte> rawKey)
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

                DoWarmUpPath(nibbles, ref emptyPath, root);
            }
            catch (TrieException e)
            {
                EnhanceException(rawKey, RootHash, e);
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
                    root = _readResolver.GetOrLoadNode(emptyPath, rootHash);
                }
                CappedArray<byte> result = GetNew(nibbles, ref emptyPath, root, isNodeRead: true);
                return result.ToArray();
            }
            catch (TrieException e)
            {
                EnhanceExceptionNibble(nibbles, rootHash ?? RootHash, e);
                throw;
            }
        }

        [SkipLocalsInit]
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
                    root = _readResolver.GetOrLoadNode(emptyPath, rootHash);
                }
                CappedArray<byte> result = GetNew(nibbles, ref emptyPath, root, isNodeRead: true);

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
        public virtual void Set(ReadOnlySpan<byte> rawKey, byte[] value) => Set(rawKey, new CappedArray<byte>(value));

        [SkipLocalsInit]
        [DebuggerStepThrough]
        public void Set(ReadOnlySpan<byte> rawKey, CappedArray<byte> value)
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

                TraverseStack traverseStack = GetTraverseStack();

                TreePath empty = TreePath.Empty;
                RootRef = SetNew(traverseStack, nibbles, value, ref empty, RootRef);

                ReturnTraverseStack(traverseStack);
            }
            finally
            {
                Volatile.Write(ref _isWriteInProgress, 0);
                if (array is not null) ArrayPool<byte>.Shared.Return(array);
            }

            void Trace(in ReadOnlySpan<byte> rawKey, CappedArray<byte> value) => _logger.Trace($"{(value.Length == 0 ? $"Deleting {rawKey.ToHexString(withZeroX: true)}" : $"Setting {rawKey.ToHexString(withZeroX: true)} = {value.AsSpan().ToHexString(withZeroX: true)}")}");

            [DoesNotReturn, StackTraceHidden]
            static void ThrowNonConcurrentWrites() => throw new InvalidOperationException("Only reads can be done in parallel on the Patricia tree");
        }

        [DebuggerStepThrough]
        public void Set(ReadOnlySpan<byte> rawKey, Rlp? value)
        {
            if (value is null)
            {
                Set(rawKey, CappedArray<byte>.Empty);
            }
            else
            {
                CappedArray<byte> valueBytes = new(value.Bytes);
                Set(rawKey, valueBytes);
            }
        }

        private TrieNode? SetNew(TraverseStack traverseStack, Span<byte> remainingKey, CappedArray<byte> value, ref TreePath path, TrieNode? node)
        {
            TrieNode? originalNode = node;
            int originalPathLength = path.Length;

            while (true)
            {
                if (node is null)
                {
                    node = value.IsNullOrEmpty ? null : TrieNodeFactory.CreateLeaf(remainingKey, value);

                    // End traverse
                    break;
                }

                TrieNode.ResolveNode(ref node, TrieStore, in path);

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

                            traverseStack.Push(new TraverseStackFrame()
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
                        else if (node.Value.AsSpan().SequenceEqual(value.AsSpan()))
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
                        theBranch[currentNodeNib] = node.CloneWithChangedKey(HexPrefix.GetArray(node.Key.AsSpan(commonPrefixLength + 1)));
                    }

                    // This is the new branch
                    theBranch[remainingKey[commonPrefixLength]] =
                        TrieNodeFactory.CreateLeaf(remainingKey[(commonPrefixLength + 1)..], value);

                    // Extension in front of the branch
                    node = commonPrefixLength == 0 ?
                        theBranch :
                        TrieNodeFactory.CreateExtension(remainingKey[..commonPrefixLength], theBranch);

                    break;
                }

                int nib = remainingKey[0];
                path.AppendMut(nib);
                TrieNode? child = node.GetChildWithChildPath(TrieStore, ref path, nib);

                traverseStack.Push(new TraverseStackFrame()
                {
                    Node = node,
                    OriginalChild = child,
                    ChildIdx = nib,
                });

                // Continue loop with child as current node
                node = child;
                remainingKey = remainingKey[1..];
            }

            while (traverseStack.TryPop(out TraverseStackFrame cStack))
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
                            node = child.CloneWithChangedKey(HexPrefix.ConcatNibbles(node.Key, child.Key));
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
                node = MaybeCombineNode(ref path, node, null);
            }

            return node;
        }

        internal bool ShouldUpdateChild(TrieNode? parent, TrieNode? oldChild, TrieNode? newChild)
        {
            if (parent is null) return true;
            if (oldChild is null && newChild is null) return false;
            if (!ReferenceEquals(oldChild, newChild))
            {
                // B3b: ResolveNode rebinds the caller's reference to a typed instance,
                // so a placeholder->typed resolve looks like a "different child" by ref
                // identity. Treat both as the same child when both carry the same keccak;
                // the structural payload is identical and a re-encode would be wasteful.
                if (oldChild is not null && newChild is not null
                    && oldChild.TryGetKeccak(out ValueHash256 oldKeccak)
                    && newChild.TryGetKeccak(out ValueHash256 newKeccak)
                    && oldKeccak == newKeccak)
                {
                    return false;
                }
                return true;
            }
            // So that recalculate root knows to recalculate the parent root.
            // Parent's hash can also be null depending on nesting level - still need to update child, otherwise combine will remain original value
            return newChild.Keccak is null;
        }

        /// <summary>
        /// Tries to make the current node an extension or null if it has only one child left.
        /// </summary>
        /// <param name="path"></param>
        /// <param name="node"></param>
        /// <returns></returns>
        internal TrieNode? MaybeCombineNode(ref TreePath path, in TrieNode? node, TrieNode? originalNode)
        {
            int onlyChildIdx = -1;
            TrieNode? onlyChildNode = null;
            path.AppendMut(0);
            TrieNode.ChildIterator iterator = node.CreateChildIterator();
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
            TrieNode.ResolveNode(ref onlyChildNode, TrieStore, in path);
            path.TruncateOne();

            if (onlyChildNode.IsBranch)
            {
                byte[] extensionKey = HexPrefix.SingleNibble((byte)onlyChildIdx);
                if (originalNode is not null && originalNode.IsExtension && Bytes.AreEqual(extensionKey, originalNode.Key))
                {
                    path.AppendMut(onlyChildIdx);
                    TrieNode? originalChild = originalNode.GetChildWithChildPath(TrieStore, ref path, 0);
                    path.TruncateOne();
                    if (!ShouldUpdateChild(originalNode, originalChild, onlyChildNode))
                    {
                        return originalNode;
                    }

                    if (!originalNode.IsSealed)
                    {
                        // Use the original where possible. This is actually needed for snapsync because of the BoundaryProofNode flag
                        originalNode.SetChild(0, onlyChildNode);
                        return originalNode;
                    }
                }

                return TrieNodeFactory.CreateExtension(extensionKey, onlyChildNode);
            }

            // 35%
            // Replace the only child with something with extra key.
            byte[] newKey = HexPrefix.PrependNibble((byte)onlyChildIdx, onlyChildNode.Key);
            if (originalNode is not null) // Only bulkset provide original node
            {
                if (originalNode.IsExtension && onlyChildNode.IsExtension)
                {
                    if (Bytes.AreEqual(newKey, originalNode.Key))
                    {
                        int originalLength = path.Length;
                        path.AppendMut(newKey);
                        TrieNode? originalChild = originalNode.GetChildWithChildPath(TrieStore, ref path, 0);
                        TrieNode? newChild = onlyChildNode.GetChildWithChildPath(TrieStore, ref path, 0);
                        path.TruncateMut(originalLength);
                        if (!ShouldUpdateChild(originalNode, originalChild, newChild))
                        {
                            return originalNode;
                        }

                        if (!originalNode.IsSealed)
                        {
                            // Use the original where possible. This is actually needed for snapsync because of the BoundaryProofNode flag
                            originalNode.SetChild(0, newChild);
                            return originalNode;
                        }
                    }
                }

                if (originalNode.IsLeaf && onlyChildNode.IsLeaf)
                {
                    if (Bytes.AreEqual(newKey, originalNode.Key))
                    {
                        if (onlyChildNode.Value.Equals(originalNode.Value))
                        {
                            return originalNode;
                        }
                    }
                }
            }

            TrieNode tn = onlyChildNode.CloneWithChangedKey(newKey);
            return tn;
        }

        private record struct TraverseStackFrame
        {
            public TrieNode Node;
            public int ChildIdx;
            public TrieNode? OriginalChild;
        }

        private class TraverseStack
        {
            [InlineArray(64)]
            private struct Inline64
            {
                public TraverseStackFrame Item;
            }

            private Inline64 _entries;
            private int _count;

            public void Push(TraverseStackFrame frame) => _entries[_count++] = frame;

            public bool TryPop(out TraverseStackFrame frame)
            {
                if (_count == 0) { frame = default; return false; }
                frame = _entries[--_count];
                _entries[_count] = default; // release references
                return true;
            }

            public void Clear()
            {
                if (_count != 0)
                {
                    _entries[.._count].Clear();
                    _count = 0;
                }
            }
            public int Count => _count;
        }

        private CappedArray<byte> GetNew(Span<byte> remainingKey, ref TreePath path, TrieNode? node, bool isNodeRead)
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

                    TrieNode.ResolveNode(ref node, _readResolver, in path);

                    if (isNodeRead && remainingKey.Length == 0)
                    {
                        return node.FullRlp;
                    }

                    if (node.IsLeaf || node.IsExtension)
                    {
                        byte[] key = node.Key!;
                        int commonPrefixLength = remainingKey.CommonPrefixLength(key);
                        if (commonPrefixLength == key.Length)
                        {
                            if (node.IsLeaf)
                            {
                                if (!isNodeRead && commonPrefixLength == remainingKey.Length) return node.Value;

                                // Um..... leaf cannot have child
                                return default;
                            }

                            // Continue traversal to the child of the extension
                            path.AppendMut(key);
                            node = node.GetChildWithChildPath(_readResolver, ref path, 0);
                            remainingKey = remainingKey[key.Length..];

                            continue;
                        }

                        // No node match
                        return default;
                    }

                    int nib = remainingKey[0];
                    path.AppendMut(nib);
                    node = node.GetChildWithChildPath(_readResolver, ref path, nib);
                    remainingKey = remainingKey[1..];
                }
            }
            finally
            {
                path.TruncateMut(originalPathLength);
            }
        }

        private void DoWarmUpPath(Span<byte> remainingKey, ref TreePath path, TrieNode? node)
        {
            int originalPathLength = path.Length;

            try
            {
                while (true)
                {
                    if (node is null)
                    {
                        // If node read, then missing node. If value read.... what is it suppose to be then?
                        return;
                    }

                    // Sealed boundary rebind: hop through the resolver cache so the warm-up
                    // path shares the canonical node instance. GetOrLoadNode fuses lookup+resolve
                    // so we drop the subsequent ResolveNode no-op call.
                    if (node.IsSealed && node.TryGetKeccak(out ValueHash256 sealedKeccak) && path.Length % 2 == 1)
                    {
                        node = _readResolver.GetOrLoadNode(path, in sealedKeccak);
                    }
                    else
                    {
                        TrieNode.ResolveNode(ref node, _readResolver, in path);
                    }

                    if (node.IsLeaf || node.IsExtension)
                    {
                        byte[] key = node.Key!;
                        int commonPrefixLength = remainingKey.CommonPrefixLength(key);
                        if (commonPrefixLength == key.Length)
                        {
                            if (node.IsLeaf)
                            {
                                // Done
                                return;
                            }

                            // Continue traversal to the child of the extension
                            path.AppendMut(key);
                            node = node.GetChildWithChildPath(_readResolver, ref path, 0, keepChildRef: true);
                            remainingKey = remainingKey[key.Length..];

                            continue;
                        }

                        // No node match
                        return;
                    }

                    int nextNib = remainingKey[0];

                    path.AppendMut(nextNib);
                    node = node.GetChildWithChildPath(_readResolver, ref path, nextNib, keepChildRef: true);
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
                    if (bytes.IsEmpty) return Keccak.EmptyTreeHash;
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
                    if (RootHash == rootHash)
                    {
                        rootRef = RootRef;
                        if (rootRef is null || !TrieNode.TryResolveNode(ref rootRef, resolver, ref emptyPath))
                        {
                            visitor.VisitMissingNode(default, rootHash);
                            return false;
                        }
                    }
                    else if (!resolver.TryGetOrLoadNode(emptyPath, rootHash.ValueHash256, out rootRef))
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
    }
}
