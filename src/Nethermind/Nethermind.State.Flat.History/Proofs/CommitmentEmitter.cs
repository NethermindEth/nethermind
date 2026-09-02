// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Buffers.Binary;
using Nethermind.Core;
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
    public const byte LargeTrieFlag = 0xFF;
    private const int MaxRowsPerBatch = 65_536;
    private const int WindowFlushChunk = 256;
    private const int EmptyRecord = -1;

    private readonly IColumnsDb<FlatHistoryColumns> _history;
    private readonly CommitmentDepthPolicy _policy;
    private readonly CommitmentStore _accounts;
    private readonly CommitmentStore _storages;
    private readonly IDb _storageColumn;
    private readonly CommitmentMetadata _metadata;
    private readonly object _windowWriteLock;
    private readonly bool _writeThrough;
    private readonly int _maxOpenWindowNodes;

    private readonly RowArena _blockArena = new();
    private readonly Dictionary<NodePathKey, (int Offset, int Length)> _blockNodes = [];
    private readonly Dictionary<NodePathKey, ushort> _blockChanged = [];
    private readonly HashSet<NodePathKey> _blockDirtyChildren = [];
    private readonly Dictionary<ValueHash256, int> _blockStorageMaxDepth = [];
    private readonly Dictionary<ValueHash256, bool> _largeTries = [];
    private readonly Dictionary<NodePathKey, bool> _exactBranches = [];
    private readonly Dictionary<NodePathKey, WindowState> _windows = [];
    private readonly HashSet<NodePathKey> _touchedThisBlock = [];
    private readonly ChildVector _children = ChildVector.Rent();
    private readonly ChildVector _merged = ChildVector.Rent();
    private readonly byte[] _rowBuffer = new byte[ParentRowCodec.MaxBranchRowLength];

    private IColumnsWriteBatch<FlatHistoryColumns>? _batch;
    private int _rowsInBatch;
    private ulong _block;
    private bool _haveBlock;

    private CommitmentEmitter(IColumnsDb<FlatHistoryColumns> history, CommitmentDepthPolicy policy, CommitmentMetadata metadata, bool writeThrough, int maxOpenWindowNodes)
    {
        _history = history;
        _policy = policy;
        _metadata = metadata;
        _windowWriteLock = metadata.WindowWriteLock;
        _writeThrough = writeThrough;
        _maxOpenWindowNodes = maxOpenWindowNodes;
        _accounts = new CommitmentStore(history.GetColumnDb(FlatHistoryColumns.AccountCommitments));
        _storageColumn = history.GetColumnDb(FlatHistoryColumns.StorageCommitments);
        _storages = new CommitmentStore(_storageColumn);
    }

    public static CommitmentEmitter ForWalk(IColumnsDb<FlatHistoryColumns> history, CommitmentDepthPolicy policy, CommitmentMetadata metadata) =>
        new(history, policy, metadata, writeThrough: false, WalkMaxOpenWindowNodes);

    public static CommitmentEmitter ForTip(IColumnsDb<FlatHistoryColumns> history, CommitmentDepthPolicy policy, CommitmentMetadata metadata) =>
        new(history, policy, metadata, writeThrough: true, DefaultMaxOpenWindowNodes);

    public CommitmentDepthPolicy Policy => _policy;

    public void BeginBlock(ulong block)
    {
        if (_haveBlock && _windows.Count > 0 && _policy.WindowClosingAt(block) != _policy.WindowClosingAt(_block))
        {
            FlushWindows(_policy.WindowClosingAt(_block));
        }

        _block = block;
        _haveBlock = true;
        _blockArena.Clear();
        _blockNodes.Clear();
        _blockChanged.Clear();
        _blockDirtyChildren.Clear();
        _blockStorageMaxDepth.Clear();
        _touchedThisBlock.Clear();
    }

    public void RecordAccountNode(in TreePath path, ReadOnlySpan<byte> rlp)
    {
        if (rlp.Length < Hash256.Size || path.Length > _policy.AccountCheckpointDepth + 1) return;

        NodePathKey key = NodePathKey.ForAccount(path);
        if (path.Length == _policy.AccountCheckpointDepth + 1)
        {
            _blockDirtyChildren.Add(key);
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

    public void RecordStorageNode(in ValueHash256 accountPath, in TreePath path, ReadOnlySpan<byte> rlp)
    {
        NoteStorageDepth(accountPath, path.Length);
        if (rlp.Length < Hash256.Size || path.Length > _policy.StorageCheckpointDepth + 1) return;

        NodePathKey key = NodePathKey.ForStorage(accountPath, path);
        if (path.Length == _policy.StorageCheckpointDepth + 1)
        {
            _blockDirtyChildren.Add(key);
            return;
        }

        Record(key, rlp, changed: null);
    }

    public void RecordStorageNode(in ValueHash256 accountPath, in TreePath path, ReadOnlySpan<byte> rlp, ushort changedChildren)
    {
        if (path.Length > _policy.StorageCheckpointDepth) return;

        Record(NodePathKey.ForStorage(accountPath, path), rlp, changedChildren);
    }

    public void RecordStorageEmpty(in ValueHash256 accountPath, in TreePath path)
    {
        if (path.Length > _policy.StorageCheckpointDepth) return;

        RecordEmpty(NodePathKey.ForStorage(accountPath, path));
    }

    public void RecordStorageDepthReached(in ValueHash256 accountPath, int depth) => NoteStorageDepth(accountPath, depth);

    public bool IsLargeStorageTrie(in ValueHash256 accountPath)
    {
        if (_blockStorageMaxDepth.TryGetValue(accountPath, out int depth) && depth >= _policy.LargeTrieSignalDepth)
        {
            MarkLarge(accountPath);
            return true;
        }

        if (_metadata.IsKnownLargeStorageTrie(accountPath)) return true;
        if (_largeTries.TryGetValue(accountPath, out bool large)) return large;

        Span<byte> flagKey = stackalloc byte[CommitmentKeyLayout.IdentityLength + 1];
        WriteLargeTrieFlagKey(flagKey, accountPath);
        large = _storageColumn.KeyExists(flagKey);
        _largeTries[accountPath] = large;
        if (large) _metadata.RememberLargeStorageTrie(accountPath);
        return large;
    }

    public void CompleteBlock()
    {
        foreach ((NodePathKey key, (int offset, int length)) in _blockNodes)
        {
            CommitmentTier tier = key.IsStorage
                ? _policy.StorageTier(key.Depth, IsLargeStorageTrie(key.Scope))
                : _policy.AccountTier(key.Depth);

            ReadOnlySpan<byte> rlp = length == EmptyRecord ? ReadOnlySpan<byte>.Empty : _blockArena.Slice(offset, length);
            switch (tier)
            {
                case CommitmentTier.PerChange:
                    WriteExact(key, rlp, isEmpty: length == EmptyRecord);
                    break;
                case CommitmentTier.Checkpoint:
                    Accumulate(key, rlp, isEmpty: length == EmptyRecord);
                    break;
            }
        }

        if (_writeThrough)
        {
            ulong window = _policy.WindowClosingAt(_block);
            lock (_windowWriteLock)
            {
                foreach (NodePathKey key in _touchedThisBlock) MergeWrite(key, _windows[key], window);
                CommitBatch();
            }
        }

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
    }

    public void Dispose()
    {
        CommitBatch();
        foreach (WindowState state in _windows.Values) state.Release();
        _windows.Clear();
        _blockArena.Dispose();
        ChildVector.Return(_children);
        ChildVector.Return(_merged);
    }

    public static void WriteLargeTrieFlagKey(Span<byte> destination, in ValueHash256 accountPath)
    {
        CommitmentKeyLayout.WriteIdentity(destination, accountPath);
        destination[CommitmentKeyLayout.IdentityLength] = LargeTrieFlag;
    }

    private void Record(in NodePathKey key, ReadOnlySpan<byte> rlp, ushort? changed)
    {
        _blockNodes[key] = (_blockArena.Append(rlp), rlp.Length);
        if (changed is { } mask) _blockChanged[key] = mask;
        else _blockChanged.Remove(key);
    }

    private void RecordEmpty(in NodePathKey key)
    {
        _blockNodes[key] = (0, EmptyRecord);
        _blockChanged.Remove(key);
    }

    private void NoteStorageDepth(in ValueHash256 accountPath, int depth)
    {
        if (depth < _policy.LargeTrieSignalDepth) return;

        _blockStorageMaxDepth[accountPath] = Math.Max(_blockStorageMaxDepth.GetValueOrDefault(accountPath), depth);
    }

    private void MarkLarge(in ValueHash256 accountPath)
    {
        if (_metadata.IsKnownLargeStorageTrie(accountPath)) return;

        _largeTries[accountPath] = true;
        _metadata.RememberLargeStorageTrie(accountPath);
        Span<byte> flagKey = stackalloc byte[CommitmentKeyLayout.IdentityLength + 1];
        WriteLargeTrieFlagKey(flagKey, accountPath);
        if (_storageColumn.KeyExists(flagKey)) return;

        Span<byte> since = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(since, _block);
        _storageColumn.PutSpan(flagKey, since);
    }

    private void WriteExact(in NodePathKey key, ReadOnlySpan<byte> rlp, bool isEmpty)
    {
        bool isBranch = false;
        if (isEmpty)
        {
            int length = ParentRowCodec.EncodeEmpty(_block, _rowBuffer);
            Write(key, exact: true, _block, _rowBuffer.AsSpan(0, length));
        }
        else if (BranchRlp.IsBranch(rlp))
        {
            isBranch = true;
            BranchRlp.ReadChildren(rlp, _children);
            ushort presence = _children.Presence;
            bool wasBranch = _exactBranches.TryGetValue(key, out bool previous) && previous;
            ushort changed = CommitmentDepthPolicy.IsFullVectorSuffix(_block) || !wasBranch ? presence : ChangedChildren(key, _children);
            int length = ParentRowCodec.EncodeBranch(_block, presence, changed, _children, _rowBuffer);
            Write(key, exact: true, _block, _rowBuffer.AsSpan(0, length));
        }
        else
        {
            WriteWhole(key, exact: true, _block, rlp);
        }

        _exactBranches[key] = isBranch;
    }

    private void Accumulate(in NodePathKey key, ReadOnlySpan<byte> rlp, bool isEmpty)
    {
        if (!_windows.TryGetValue(key, out WindowState? state))
        {
            state = new WindowState();
            _windows[key] = state;
        }

        _touchedThisBlock.Add(key);
        state.LastBlock = _block;

        if (isEmpty)
        {
            state.SetEmpty();
            return;
        }

        if (!BranchRlp.IsBranch(rlp))
        {
            state.SetWhole(rlp);
            return;
        }

        BranchRlp.ReadChildren(rlp, _children);
        ushort presence = _children.Presence;
        ushort changed = ChangedChildren(key, _children);
        if (state.Kind is WindowKind.Whole or WindowKind.Empty) changed |= presence;

        state.SetBranch(_children, presence, changed);
    }

    private void FlushWindows(ulong window)
    {
        using ArrayPoolList<KeyValuePair<NodePathKey, WindowState>> pending = new(_windows.Count, _windows);
        for (int start = 0; start < pending.Count; start += WindowFlushChunk)
        {
            int end = Math.Min(pending.Count, start + WindowFlushChunk);
            lock (_windowWriteLock)
            {
                for (int index = start; index < end; index++) MergeWrite(pending[index].Key, pending[index].Value, window);
                CommitBatch();
            }
        }

        foreach (KeyValuePair<NodePathKey, WindowState> entry in pending) entry.Value.Release();
        _windows.Clear();
    }

    private void MergeWrite(in NodePathKey key, WindowState state, ulong window)
    {
        Span<byte> prefix = stackalloc byte[CommitmentKeyLayout.MaxKeyLength];
        int prefixLength = key.WritePrefix(prefix, exact: false);
        CommitmentStore store = Store(key);
        IWriteBatch batch = GetBatch(key.IsStorage ? FlatHistoryColumns.StorageCommitments : FlatHistoryColumns.AccountCommitments);
        Span<byte> existing = store.GetExactSpan(prefix[..prefixLength], window);
        try
        {
            if (state.Kind == WindowKind.Branch && existing.Length > 0 && ParentRowCodec.IsBranchRow(existing))
            {
                int length = MergeBranch(existing, state, window, _merged, _rowBuffer);
                store.Write(prefix[..prefixLength], window, _rowBuffer.AsSpan(0, length), batch);
                return;
            }

            if (existing.Length > 0 && ParentRowCodec.IsValid(existing) && ParentRowCodec.LastBlock(existing) > state.LastBlock)
            {
                store.Write(prefix[..prefixLength], window, existing, batch);
                return;
            }
        }
        finally
        {
            store.Release(existing);
        }

        WriteState(store, prefix[..prefixLength], window, state, batch);
    }

    private void WriteState(CommitmentStore store, ReadOnlySpan<byte> prefix, ulong window, WindowState state, IWriteBatch batch)
    {
        bool full = CommitmentDepthPolicy.IsFullVectorSuffix(window);
        switch (state.Kind)
        {
            case WindowKind.Empty:
                store.Write(prefix, window, _rowBuffer.AsSpan(0, ParentRowCodec.EncodeEmpty(state.LastBlock, _rowBuffer)), batch);
                break;
            case WindowKind.Whole:
                {
                    byte[] row = ArrayPool<byte>.Shared.Rent(ParentRowCodec.WholeNodeRowLength(state.WholeLength));
                    int length = ParentRowCodec.EncodeWholeNode(state.LastBlock, state.Whole, row);
                    store.Write(prefix, window, row.AsSpan(0, length), batch);
                    ArrayPool<byte>.Shared.Return(row);
                    break;
                }
            default:
                {
                    ushort changed = full ? (ushort)(state.Presence | state.Changed) : state.Changed;
                    int length = ParentRowCodec.EncodeBranch(state.LastBlock, state.Presence, changed, state.Latest, _rowBuffer);
                    store.Write(prefix, window, _rowBuffer.AsSpan(0, length), batch);
                    break;
                }
        }
    }

    private static int MergeBranch(ReadOnlySpan<byte> existing, WindowState state, ulong window, ChildVector merged, Span<byte> row)
    {
        bool full = CommitmentDepthPolicy.IsFullVectorSuffix(window);
        bool existingNewer = ParentRowCodec.LastBlock(existing) > state.LastBlock;
        ushort existingChanged = ParentRowCodec.Changed(existing);
        ushort changed = (ushort)(existingChanged | state.Changed);
        ulong lastBlock = Math.Max(ParentRowCodec.LastBlock(existing), state.LastBlock);

        merged.Clear();
        ushort presence;
        ushort carried;
        if (existingNewer)
        {
            presence = ParentRowCodec.Presence(existing);
            ParentRowCodec.Fill(existing, existingChanged, merged);
            for (int index = 0; index < BranchRlp.ChildCount; index++)
            {
                if (((existingChanged >> index) & 1) == 0 && state.Latest.IsPresent(index)) merged.Set(index, state.Latest[index]);
            }

            carried = (ushort)(existingChanged | merged.Presence);
        }
        else
        {
            presence = state.Presence;
            merged.CopyFrom(state.Latest);
            carried = ushort.MaxValue;
        }

        ushort written = (ushort)((full ? (ushort)(presence | changed) : changed) & carried);
        return ParentRowCodec.EncodeBranch(lastBlock, presence, written, merged, row);
    }

    private ushort ChangedChildren(in NodePathKey key, ChildVector children)
    {
        if (_blockChanged.TryGetValue(key, out ushort explicitMask)) return explicitMask;

        ushort changed = 0;
        for (int index = 0; index < BranchRlp.ChildCount; index++)
        {
            if (!children.IsPresent(index)) continue;

            if (children[index].Length < Hash256.Size) changed |= (ushort)(1 << index);
            else
            {
                NodePathKey childKey = key.Child(index);
                if (_blockNodes.ContainsKey(childKey) || _blockDirtyChildren.Contains(childKey)) changed |= (ushort)(1 << index);
            }
        }

        return changed;
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
        Store(key).Write(prefix[..prefixLength], suffix, row, GetBatch(key.IsStorage ? FlatHistoryColumns.StorageCommitments : FlatHistoryColumns.AccountCommitments));
        if (++_rowsInBatch >= MaxRowsPerBatch) CommitBatch();
    }

    private CommitmentStore Store(in NodePathKey key) => key.IsStorage ? _storages : _accounts;

    private IWriteBatch GetBatch(FlatHistoryColumns column)
    {
        _batch ??= _history.StartWriteBatch();
        return _batch.GetColumnBatch(column);
    }

    private void CommitBatch()
    {
        _batch?.Dispose();
        _batch = null;
        _rowsInBatch = 0;
    }

    private enum WindowKind : byte
    {
        Unknown,
        Branch,
        Whole,
        Empty,
    }

    private sealed class WindowState
    {
        private byte[]? _whole;

        public WindowKind Kind;
        public ulong LastBlock;
        public ushort Presence;
        public ushort Changed;
        public int WholeLength;
        public readonly ChildVector Latest = ChildVector.Rent();

        public ReadOnlySpan<byte> Whole => _whole.AsSpan(0, WholeLength);

        public void SetEmpty()
        {
            Kind = WindowKind.Empty;
            Presence = 0;
            WholeLength = 0;
        }

        public void SetWhole(ReadOnlySpan<byte> rlp)
        {
            Kind = WindowKind.Whole;
            Presence = 0;
            if (_whole is null || _whole.Length < rlp.Length)
            {
                if (_whole is not null) ArrayPool<byte>.Shared.Return(_whole);
                _whole = ArrayPool<byte>.Shared.Rent(rlp.Length);
            }

            rlp.CopyTo(_whole);
            WholeLength = rlp.Length;
        }

        public void SetBranch(ChildVector children, ushort presence, ushort changed)
        {
            Kind = WindowKind.Branch;
            WholeLength = 0;
            Presence = presence;
            Changed |= changed;
            Latest.CopyFrom(children);
        }

        public void Release()
        {
            if (_whole is not null) ArrayPool<byte>.Shared.Return(_whole);
            _whole = null;
            ChildVector.Return(Latest);
        }
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
