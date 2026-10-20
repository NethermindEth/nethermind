// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.State.Flat.History.Walk;
using Nethermind.Trie;

namespace Nethermind.State.Flat.History.Proofs;

public sealed class CommitmentEmitter : IDisposable
{
    public const int DefaultMaxOpenWindowNodes = 200_000;
    public const int WalkMaxOpenWindowNodes = 50_000;
    private const int TipExactBranchEntries = 1 << 18;
    private const int WalkExactBranchEntriesCeiling = 1 << 14;
    private const int InitialExactBranchEntries = 1 << 10;
    private const int TipExtensionTargetEntries = 1 << 16;
    private const int WalkExtensionTargetEntries = 1 << 12;
    private const int ShallowStorageSnapshotDepth = 1;
    private const int MaxRowsPerBatch = 65_536;
    private const int WindowFlushChunk = 256;
    private const int EmptyRecord = -1;

    private readonly IColumnsDb<FlatHistoryColumns> _history;
    private readonly CommitmentDepthPolicy _policy;
    private readonly CommitmentStore _accounts;
    private readonly CommitmentStore _storages;
    private readonly CommitmentMetadata _metadata;
    private readonly object _windowWriteLock;
    private readonly int _maxOpenWindowNodes;
    private readonly bool _respectFloors;
    private readonly bool _deepStorageSnapshots;
    private readonly Action<CommitmentEmitter>? _release;

    private readonly RowArena _blockArena = new();
    private readonly Dictionary<NodePathKey, (int Offset, int Length)> _blockNodes = [];
    private readonly Dictionary<NodePathKey, ushort> _blockChanged = [];
    // Per parent, the children this block recorded or marked dirty.
    private readonly Dictionary<NodePathKey, ushort> _blockChangedChildren = [];
    private readonly Dictionary<ValueHash256, int> _blockStorageMaxDepth = [];
    private readonly Dictionary<ValueHash256, int> _blockTrieDepths = [];
    private readonly HashSet<NodePathKey> _exactBranches;
    private readonly int _maxExactBranches;
    private readonly ClockCache<NodePathKey, bool> _extensionTargets;
    private readonly HashSet<NodePathKey> _blockAdoptedTargets = [];
    private readonly List<NodePathKey> _composedNodes = [];
    private readonly bool _recordComposedChildViews;
    private readonly Dictionary<NodePathKey, int> _windows = [];
    private readonly WindowSlab _windowSlab = new();
    private readonly ChildVector _latest = ChildVector.Rent();
    private readonly ChildVector _children = ChildVector.Rent();
    private readonly ChildVector _merged = ChildVector.Rent();
    private bool _disposed;
    private bool _inPool;
    private bool _retired;
    private readonly byte[] _rowBuffer = new byte[ParentRowCodec.MaxBranchRowLength];

    private IColumnsWriteBatch<FlatHistoryColumns>? _batch;
    private IWriteBatch? _accountBatch;
    private IWriteBatch? _storageBatch;
    private int _rowsInBatch;
    private ulong _block;
    private bool _haveBlock;
    private ulong _floorsEpoch = ulong.MaxValue;
    private ulong _retainedFloor;
    private ulong _fineFloor;

    private CommitmentEmitter(IColumnsDb<FlatHistoryColumns> history, CommitmentDepthPolicy policy, CommitmentMetadata metadata, int maxOpenWindowNodes, int exactBranchEntries, int extensionTargetEntries, bool respectFloors, bool deepStorageSnapshots, Action<CommitmentEmitter>? release = null, bool recordComposedChildViews = false)
    {
        _recordComposedChildViews = recordComposedChildViews;
        _deepStorageSnapshots = deepStorageSnapshots;
        _release = release;
        _exactBranches = new HashSet<NodePathKey>(Math.Min(exactBranchEntries, InitialExactBranchEntries));
        _maxExactBranches = exactBranchEntries;
        _extensionTargets = new ClockCache<NodePathKey, bool>(extensionTargetEntries);
        _respectFloors = respectFloors;
        _history = history;
        _policy = policy;
        _metadata = metadata;
        _windowWriteLock = metadata.WindowWriteLock;
        _maxOpenWindowNodes = maxOpenWindowNodes;
        _accounts = new CommitmentStore(history.GetColumnDb(FlatHistoryColumns.AccountCommitments), policy, 0);
        _storages = new CommitmentStore(history.GetColumnDb(FlatHistoryColumns.StorageCommitments), policy, CommitmentKeyLayout.IdentityLength);
    }

    /// <param name="release">When set, disposing the emitter resets it to the state of a new one and hands it here
    /// instead of releasing its buffers, so a walk that emits per partition and per contract builds each emitter once
    /// per worker: a new one allocates about 420 KB, most of it the extension-target cache, on the large object heap.</param>
    public static CommitmentEmitter ForWalk(IColumnsDb<FlatHistoryColumns> history, CommitmentDepthPolicy policy, CommitmentMetadata metadata, bool deepStorageSnapshots = false, Action<CommitmentEmitter>? release = null) =>
        new(history, policy, metadata, WalkMaxOpenWindowNodes, WalkExactBranchEntries(policy), WalkExtensionTargetEntries, respectFloors: false, deepStorageSnapshots, release);

    private static int WalkExactBranchEntries(CommitmentDepthPolicy policy)
    {
        int deepest = Math.Max(policy.AccountExactDepth, policy.StorageExactDepth);
        long entries = 0;
        for (int depth = 0; depth <= deepest && entries < WalkExactBranchEntriesCeiling; depth++) entries += 4L << (4 * depth);
        return (int)Math.Clamp(entries, 1 << 10, WalkExactBranchEntriesCeiling);
    }

    public static CommitmentEmitter ForTip(IColumnsDb<FlatHistoryColumns> history, CommitmentDepthPolicy policy, CommitmentMetadata metadata) =>
        new(history, policy, metadata, DefaultMaxOpenWindowNodes, TipExactBranchEntries, TipExtensionTargetEntries, respectFloors: true, deepStorageSnapshots: false, recordComposedChildViews: true);

    public CommitmentDepthPolicy Policy => _policy;

    public int AccountRecordDepth => _policy.AccountCheckpointDepth + 1;

    public int StorageRecordDepth => _policy.StorageCheckpointDepth + 1;

    public int StorageSnapshotDepth => _deepStorageSnapshots ? StorageRecordDepth : ShallowStorageSnapshotDepth;

    public void BeginBlock(ulong block)
    {
        if (_haveBlock && _windows.Count > 0 && _policy.WindowClosingAt(block) != _policy.WindowClosingAt(_block))
        {
            FlushWindows(_policy.WindowClosingAt(_block));
        }

        _block = block;
        _haveBlock = true;
        ulong epoch = _policy.Epoch(block);
        if (epoch != _floorsEpoch)
        {
            _floorsEpoch = epoch;
            _retainedFloor = _respectFloors ? _metadata.RetainedFromEpoch : 0;
            _fineFloor = _respectFloors ? _metadata.FineFromEpoch : 0;
        }

        _blockArena.Clear();
        _blockNodes.Clear();
        _blockChanged.Clear();
        _blockChangedChildren.Clear();
        _blockStorageMaxDepth.Clear();
        _blockTrieDepths.Clear();
        _blockAdoptedTargets.Clear();
    }

    public void RecordAccountNode(in TreePath path, ReadOnlySpan<byte> rlp)
    {
        if (rlp.Length < Hash256.Size || path.Length > _policy.AccountCheckpointDepth + 1) return;

        NodePathKey key = NodePathKey.ForAccount(path);
        if (path.Length == _policy.AccountCheckpointDepth + 1)
        {
            NoteChangedChild(key);
            return;
        }

        Record(key, rlp, changed: null);
    }

    public void RecordAccountNode(in TreePath path, ReadOnlySpan<byte> rlp, ushort changedChildren)
    {
        if (path.Length > _policy.AccountCheckpointDepth) return;

        Record(NodePathKey.ForAccount(path), rlp, changedChildren);
    }

    public void RecordAccountEmpty(in TreePath path)
    {
        if (path.Length > _policy.AccountCheckpointDepth) return;

        RecordEmpty(NodePathKey.ForAccount(path));
    }

    internal void SeedAccountNode(in TreePath path, ReadOnlySpan<byte> rlp)
    {
        if (_policy.AccountTier(path.Length) != CommitmentTier.PerChange) return;

        NodePathKey key = NodePathKey.ForAccount(path);
        if (rlp.Length > 0 && BranchRlp.TryReadChildren(rlp, _children)) RememberExactBranch(key);
        else _exactBranches.Remove(key);
    }

    public void RecordStorageNode(in ValueHash256 accountPath, in TreePath path, ReadOnlySpan<byte> rlp)
    {
        NoteStorageDepth(accountPath, path.Length);
        if (rlp.Length < Hash256.Size || path.Length > _policy.StorageCheckpointDepth + 1) return;

        NodePathKey key = NodePathKey.ForStorage(accountPath, path);
        if (path.Length == _policy.StorageCheckpointDepth + 1)
        {
            NoteChangedChild(key);
            return;
        }

        Record(key, rlp, changed: null);
    }

    public void RecordStorageNode(in ValueHash256 accountPath, in TreePath path, ReadOnlySpan<byte> rlp, ushort changedChildren)
    {
        NoteStorageDepth(accountPath, path.Length);
        if (path.Length > _policy.StorageCheckpointDepth) return;

        Record(NodePathKey.ForStorage(accountPath, path), rlp, changedChildren);
    }

    public void RecordStorageEmpty(in ValueHash256 accountPath, in TreePath path)
    {
        NoteStorageDepth(accountPath, path.Length);
        if (path.Length > _policy.StorageCheckpointDepth) return;

        RecordEmpty(NodePathKey.ForStorage(accountPath, path));
    }

    public void RecordStorageDepthReached(in ValueHash256 accountPath, int depth) => NoteStorageDepth(accountPath, depth);

    public int StorageTrieDepth(in ValueHash256 accountPath)
    {
        if (_blockTrieDepths.TryGetValue(accountPath, out int depth)) return depth;

        depth = _metadata.StorageTrieDepth(accountPath);
        _blockTrieDepths[accountPath] = depth;
        return depth;
    }

    private void PersistStorageDepthsReached()
    {
        foreach ((ValueHash256 accountPath, int reached) in _blockStorageMaxDepth)
        {
            if (reached > StorageTrieDepth(accountPath)) _blockTrieDepths[accountPath] = _metadata.NoteStorageTrieDepth(accountPath, reached);
        }
    }

    public void CompleteBlock()
    {
        if (_recordComposedChildViews) RecordComposedChildViews();
        PersistStorageDepthsReached();
        bool fine = _floorsEpoch >= _fineFloor;
        if (_floorsEpoch >= _retainedFloor)
        {
            foreach ((NodePathKey key, (int offset, int length)) in _blockNodes)
            {
                CommitmentTier tier = key.IsStorage
                    ? _policy.StorageTier(key.Depth, StorageTrieDepth(key.Scope))
                    : _policy.AccountTier(key.Depth);

                ReadOnlySpan<byte> rlp = length == EmptyRecord ? ReadOnlySpan<byte>.Empty : _blockArena.Slice(offset, length);
                if (tier != CommitmentTier.Recomputed && length != EmptyRecord) NoteExtensionTarget(key, rlp);

                switch (tier)
                {
                    case CommitmentTier.PerChange:
                        if (fine) WriteExact(key, rlp, isEmpty: length == EmptyRecord);
                        Accumulate(key, rlp, isEmpty: length == EmptyRecord);
                        break;
                    case CommitmentTier.Checkpoint:
                        Accumulate(key, rlp, isEmpty: length == EmptyRecord);
                        break;
                    case CommitmentTier.Composed:
                        break;
                }
            }

        }

        foreach (NodePathKey adopted in _blockAdoptedTargets) _extensionTargets.Delete(adopted);
        if (_policy.ClosesWindow(_block))
        {
            FlushWindows(_policy.WindowAtOrBelow(_block));
        }
        else if (_windows.Count > _maxOpenWindowNodes)
        {
            FlushWindows(_policy.WindowClosingAt(_block));
        }
    }

    public void FlushOpenWindows()
    {
        if (_haveBlock) FlushWindows(_policy.WindowClosingAt(_block));
        CommitBatch();
    }

    public void Dispose()
    {
        if (_disposed || _inPool) return;

        if (_release is not null && !_retired)
        {
            CommitBatch();
            Reset();
            _inPool = true;
            _release(this);
            return;
        }

        _disposed = true;
        CommitBatch();
        _windows.Clear();
        _windowSlab.Dispose();
        _blockArena.Dispose();
        ChildVector.Return(_children);
        ChildVector.Return(_merged);
        ChildVector.Return(_latest);
    }

    /// <summary>Takes an emitter its release callback was handed back out for the next use.</summary>
    internal CommitmentEmitter Reuse()
    {
        _inPool = false;
        return this;
    }

    /// <summary>Releases the buffers of an emitter that hands itself back on dispose, for good.</summary>
    internal void Retire()
    {
        _retired = true;
        _inPool = false;
        Dispose();
    }

    private void Reset()
    {
        _windows.Clear();
        _windowSlab.Reset();
        _blockArena.Clear();
        _blockNodes.Clear();
        _blockChanged.Clear();
        _blockChangedChildren.Clear();
        _blockStorageMaxDepth.Clear();
        _blockTrieDepths.Clear();
        _blockAdoptedTargets.Clear();
        _exactBranches.Clear();
        if (_extensionTargets.Count > 0) _extensionTargets.Clear();
        _block = 0;
        _haveBlock = false;
        _floorsEpoch = ulong.MaxValue;
        _retainedFloor = 0;
        _fineFloor = 0;
    }

    private void Record(in NodePathKey key, ReadOnlySpan<byte> rlp, ushort? changed)
    {
        _blockNodes[key] = (_blockArena.Append(rlp), rlp.Length);
        NoteChangedChild(key);
        if (changed is { } mask) _blockChanged[key] = mask;
        else _blockChanged.Remove(key);
    }

    /// <summary>The reader builds a node at a composed depth from the rows of its children, and the walk records the view
    /// of every child, empty or not. At the tip only committed nodes arrive, so a child that holds no node of its own
    /// (an empty slot, or the path under a leaf or extension) would keep the row of whatever was there before. Records
    /// those children from the composed node itself; a full branch, which every composed node of a large trie is, adds
    /// nothing. The children are never composed themselves, since the policy rejects adjacent composed depths.</summary>
    private void RecordComposedChildViews()
    {
        _composedNodes.Clear();
        foreach ((NodePathKey key, (int _, int length)) in _blockNodes)
        {
            if (!key.IsStorage && length != EmptyRecord && _policy.IsComposedAccountDepth(key.Depth) && key.Depth < _policy.AccountCheckpointDepth) _composedNodes.Add(key);
        }

        foreach (NodePathKey key in _composedNodes)
        {
            (int offset, int length) = _blockNodes[key];
            ReadOnlySpan<byte> rlp = _blockArena.Slice(offset, length);
            if (BranchRlp.TryReadChildren(rlp, _children))
            {
                ushort presence = _children.Presence;
                for (int nibble = 0; nibble < BranchRlp.ChildCount; nibble++)
                {
                    if (((presence >> nibble) & 1) == 0) RecordAbsentChild(key.Child(nibble));
                }

                continue;
            }

            bool descended = NodeViews.TryDescend(rlp, out int first, out NodeView child);
            try
            {
                for (int nibble = 0; nibble < BranchRlp.ChildCount; nibble++)
                {
                    if (nibble != first) RecordAbsentChild(key.Child(nibble));
                }

                if (descended && !_blockNodes.ContainsKey(key.Child(first))) Record(key.Child(first), child.Rlp, changed: null);
            }
            finally
            {
                child.Release();
            }
        }
    }

    private void RecordAbsentChild(in NodePathKey key)
    {
        if (!_blockNodes.ContainsKey(key)) RecordEmpty(key);
    }

    private void RecordEmpty(in NodePathKey key)
    {
        _blockNodes[key] = (0, EmptyRecord);
        NoteChangedChild(key);
        _blockChanged.Remove(key);
    }

    private void NoteChangedChild(in NodePathKey key)
    {
        if (key.Depth == 0) return;

        ref ushort children = ref CollectionsMarshal.GetValueRefOrAddDefault(_blockChangedChildren, key.Parent(), out _);
        children |= (ushort)(1 << key.LastNibble);
    }

    private void NoteStorageDepth(in ValueHash256 accountPath, int depth)
    {
        if (depth < _policy.StorageRowsSignalDepth) return;

        int capped = Math.Min(depth, _policy.LargeTrieSignalDepth);
        _blockStorageMaxDepth[accountPath] = Math.Max(_blockStorageMaxDepth.GetValueOrDefault(accountPath), capped);
    }

    private void WriteExact(in NodePathKey key, ReadOnlySpan<byte> rlp, bool isEmpty)
    {
        if (isEmpty)
        {
            int length = ParentRowCodec.EncodeEmpty(_block, _rowBuffer);
            Write(key, exact: true, _block, _rowBuffer.AsSpan(0, length));
            _exactBranches.Remove(key);
        }
        else if (BranchRlp.TryReadChildren(rlp, _children))
        {
            ushort presence = _children.Presence;
            bool wasBranch = _exactBranches.Contains(key);
            ushort changed = _policy.IsFullVectorBlock(_block) || !wasBranch ? presence : ChangedChildren(key, _children);
            int length = ParentRowCodec.EncodeBranch(_block, presence, changed, _children, _rowBuffer);
            Write(key, exact: true, _block, _rowBuffer.AsSpan(0, length));
            if (!wasBranch) RememberExactBranch(key);
        }
        else
        {
            WriteWhole(key, exact: true, _block, rlp);
            _exactBranches.Remove(key);
        }
    }

    private void RememberExactBranch(in NodePathKey key)
    {
        // Forgetting only makes the next row of a forgotten node a full vector, so a whole clear is always safe.
        if (_exactBranches.Count >= _maxExactBranches) _exactBranches.Clear();
        _exactBranches.Add(key);
    }

    private void Accumulate(in NodePathKey key, ReadOnlySpan<byte> rlp, bool isEmpty)
    {
        if (!_windows.TryGetValue(key, out int slot))
        {
            slot = _windowSlab.Allocate();
            _windows[key] = slot;
        }

        ref WindowHeader state = ref _windowSlab.Header(slot);
        state.LastBlock = _block;

        if (isEmpty)
        {
            _windowSlab.SetEmpty(slot);
            return;
        }

        if (!BranchRlp.TryReadChildren(rlp, _children))
        {
            _windowSlab.SetWhole(slot, rlp);
            return;
        }

        ushort presence = _children.Presence;
        ushort changed = ChangedChildren(key, _children);
        if (state.Kind is WindowKind.Whole or WindowKind.Empty) changed |= presence;

        _windowSlab.SetBranch(slot, _children, presence, changed);
    }

    private void FlushWindows(ulong window)
    {
        using ArrayPoolList<KeyValuePair<NodePathKey, int>> pending = new(_windows.Count);
        foreach (KeyValuePair<NodePathKey, int> entry in _windows) pending.Add(entry);
        for (int start = 0; start < pending.Count; start += WindowFlushChunk)
        {
            int end = Math.Min(pending.Count, start + WindowFlushChunk);
            lock (_windowWriteLock)
            {
                for (int index = start; index < end; index++) MergeWrite(pending[index].Key, pending[index].Value, window);
                CommitBatch();
            }
        }

        _windows.Clear();
        _windowSlab.Reset();
    }

    private void MergeWrite(in NodePathKey key, int slot, ulong window)
    {
        ref WindowHeader state = ref _windowSlab.Header(slot);
        if (state.Kind == WindowKind.Branch) _windowSlab.ReadLatest(slot, _latest);

        Span<byte> prefix = stackalloc byte[CommitmentKeyLayout.MaxKeyLength];
        int prefixLength = key.WritePrefix(prefix, exact: false);
        CommitmentStore store = Store(key);
        IWriteBatch batch = GetBatch(key);
        Span<byte> existing = store.GetExactSpan(prefix[..prefixLength], window);
        try
        {
            if (state.Kind == WindowKind.Branch && existing.Length > 0 && ParentRowCodec.IsBranchRow(existing))
            {
                int length = MergeBranch(existing, state, _latest, window, _merged, _rowBuffer);
                store.Write(prefix[..prefixLength], window, _rowBuffer.AsSpan(0, length), batch);
                return;
            }

            if (existing.Length > 0 && ParentRowCodec.IsValid(existing) && ParentRowCodec.LastBlock(existing) > state.LastBlock) return;

            if (existing.Length > 0 && state.Kind == WindowKind.Branch && ParentRowCodec.IsValid(existing) && !ParentRowCodec.IsBranchRow(existing)) state.Changed |= state.Presence;
        }
        finally
        {
            store.Release(existing);
        }

        WriteState(store, prefix[..prefixLength], window, slot, batch);
    }

    private void WriteState(CommitmentStore store, ReadOnlySpan<byte> prefix, ulong window, int slot, IWriteBatch batch)
    {
        ref WindowHeader state = ref _windowSlab.Header(slot);
        bool full = _policy.IsFullVectorWindow(window);
        switch (state.Kind)
        {
            case WindowKind.Empty:
                store.Write(prefix, window, _rowBuffer.AsSpan(0, ParentRowCodec.EncodeEmpty(state.LastBlock, _rowBuffer)), batch);
                break;
            case WindowKind.Whole:
                {
                    byte[] row = ArrayPool<byte>.Shared.Rent(ParentRowCodec.WholeNodeRowLength(state.WholeLength));
                    int length = ParentRowCodec.EncodeWholeNode(state.LastBlock, _windowSlab.Whole(slot), row);
                    store.Write(prefix, window, row.AsSpan(0, length), batch);
                    ArrayPool<byte>.Shared.Return(row);
                    break;
                }
            default:
                {
                    ushort changed = full ? (ushort)(state.Presence | state.Changed) : state.Changed;
                    int length = ParentRowCodec.EncodeBranch(state.LastBlock, state.Presence, changed, _latest, _rowBuffer);
                    store.Write(prefix, window, _rowBuffer.AsSpan(0, length), batch);
                    break;
                }
        }
    }

    private int MergeBranch(ReadOnlySpan<byte> existing, in WindowHeader state, ChildVector latest, ulong window, ChildVector merged, Span<byte> row)
    {
        bool full = _policy.IsFullVectorWindow(window);
        bool existingNewer = ParentRowCodec.LastBlock(existing) > state.LastBlock;
        ushort existingChanged = ParentRowCodec.Changed(existing);
        ushort changed = (ushort)(existingChanged | state.Changed);
        ulong lastBlock = Math.Max(ParentRowCodec.LastBlock(existing), state.LastBlock);

        merged.Clear();
        ushort presence;
        if (existingNewer)
        {
            presence = ParentRowCodec.Presence(existing);
            ParentRowCodec.Fill(existing, existingChanged, merged);
            for (int index = 0; index < BranchRlp.ChildCount; index++)
            {
                if (((existingChanged >> index) & 1) == 0 && latest.IsPresent(index)) merged.Set(index, latest[index]);
            }
        }
        else
        {
            presence = state.Presence;
            merged.CopyFrom(latest);
        }

        ushort written = full ? (ushort)(presence | changed) : changed;
        return ParentRowCodec.EncodeBranch(lastBlock, presence, written, merged, row);
    }

    private ushort ChangedChildren(in NodePathKey key, ChildVector children)
    {
        if (_blockChanged.TryGetValue(key, out ushort explicitMask)) return explicitMask;

        ushort changed = (ushort)(children.Presence & _blockChangedChildren.GetValueOrDefault(key));
        for (int index = 0; index < BranchRlp.ChildCount; index++)
        {
            ushort bit = (ushort)(1 << index);
            if (!children.IsPresent(index) || (changed & bit) != 0) continue;

            if (children[index].Length < Hash256.Size) changed |= bit;
            else
            {
                NodePathKey childKey = key.Child(index);
                if (_extensionTargets.Contains(childKey))
                {
                    changed |= bit;
                    _blockAdoptedTargets.Add(childKey);
                }
            }
        }

        return changed;
    }

    private void NoteExtensionTarget(in NodePathKey key, ReadOnlySpan<byte> rlp)
    {
        Span<byte> nibbles = stackalloc byte[CommitmentDepthPolicy.MaxTrieDepth];
        if (!NodeViews.TryReadExtensionPath(rlp, nibbles, out int nibbleCount)) return;
        if (key.Depth + nibbleCount > (key.IsStorage ? StorageRecordDepth : AccountRecordDepth)) return;

        _extensionTargets.Set(key.Descendant(nibbles[..nibbleCount]), true);
    }

    private void WriteWhole(in NodePathKey key, bool exact, ulong suffix, ReadOnlySpan<byte> rlp)
    {
        byte[] row = ArrayPool<byte>.Shared.Rent(ParentRowCodec.WholeNodeRowLength(rlp.Length));
        int length = ParentRowCodec.EncodeWholeNode(suffix, rlp, row);
        Write(key, exact, suffix, row.AsSpan(0, length));
        ArrayPool<byte>.Shared.Return(row);
    }

    private void Write(in NodePathKey key, bool exact, ulong suffix, ReadOnlySpan<byte> row)
    {
        Span<byte> prefix = stackalloc byte[CommitmentKeyLayout.MaxKeyLength];
        int prefixLength = key.WritePrefix(prefix, exact);
        Store(key).Write(prefix[..prefixLength], suffix, row, GetBatch(key));
        if (++_rowsInBatch >= MaxRowsPerBatch) CommitBatch();
    }

    private CommitmentStore Store(in NodePathKey key) => key.IsStorage ? _storages : _accounts;

    private IWriteBatch GetBatch(in NodePathKey key)
    {
        _batch ??= _history.StartWriteBatch();
        return key.IsStorage
            ? _storageBatch ??= _batch.GetColumnBatch(FlatHistoryColumns.StorageCommitments)
            : _accountBatch ??= _batch.GetColumnBatch(FlatHistoryColumns.AccountCommitments);
    }

    private void CommitBatch()
    {
        _batch?.Dispose();
        _batch = null;
        _accountBatch = null;
        _storageBatch = null;
        _rowsInBatch = 0;
    }

    internal readonly struct NodePathKey : IEquatable<NodePathKey>
    {
        private readonly ValueHash256 _path;

        private NodePathKey(in ValueHash256 scope, in ValueHash256 path, byte depth, bool isStorage)
        {
            Scope = scope;
            _path = path;
            Depth = depth;
            IsStorage = isStorage;
        }

        public ValueHash256 Scope { get; }

        public int Depth { get; }

        public bool IsStorage { get; }

        public static NodePathKey ForAccount(in TreePath path) => new(default, path.Path, (byte)path.Length, isStorage: false);

        public static NodePathKey ForStorage(in ValueHash256 accountPath, in TreePath path) => new(accountPath, path.Path, (byte)path.Length, isStorage: true);

        public NodePathKey Child(int nibble)
        {
            TreePath child = new TreePath(_path, Depth).Append(nibble);
            return new NodePathKey(Scope, child.Path, (byte)child.Length, IsStorage);
        }

        public NodePathKey Parent()
        {
            Debug.Assert(Depth > 0, "a root has no parent within its scope");
            TreePath parent = new TreePath(_path, Depth).Truncate(Depth - 1);
            return new NodePathKey(Scope, parent.Path, (byte)parent.Length, IsStorage);
        }

        public int LastNibble
        {
            get
            {
                Debug.Assert(Depth > 0, "a root has no last nibble");
                return new TreePath(_path, Depth)[Depth - 1];
            }
        }

        public NodePathKey Descendant(ReadOnlySpan<byte> nibbles)
        {
            TreePath descendant = new TreePath(_path, Depth).Append(nibbles);
            return new NodePathKey(Scope, descendant.Path, (byte)descendant.Length, IsStorage);
        }

        public int WritePrefix(Span<byte> destination, bool exact)
        {
            TreePath path = new(_path, Depth);
            if (!IsStorage) return CommitmentKeyLayout.WritePathPrefix(destination, path, exact);

            Span<byte> identity = stackalloc byte[CommitmentKeyLayout.IdentityLength];
            CommitmentKeyLayout.WriteIdentity(identity, Scope);
            return CommitmentKeyLayout.WriteScopedPathPrefix(destination, identity, path, exact);
        }

        public bool Equals(NodePathKey other) => Depth == other.Depth && IsStorage == other.IsStorage && _path == other._path && Scope == other.Scope;

        public override bool Equals(object? obj) => obj is NodePathKey other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Scope, _path, Depth);
    }
}
