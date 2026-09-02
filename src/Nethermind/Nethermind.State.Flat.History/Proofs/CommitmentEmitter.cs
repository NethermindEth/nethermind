// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Trie;

namespace Nethermind.State.Flat.History.Proofs;

public sealed class CommitmentEmitter : IDisposable
{
    public const int DefaultMaxOpenWindowNodes = 200_000;
    public const int WalkMaxOpenWindowNodes = 50_000;
    public const byte LargeTrieFlag = 0xFF;
    private const int MaxRowsPerBatch = 65_536;
    private const int WindowFlushChunk = 256;

    private readonly IColumnsDb<FlatHistoryColumns> _history;
    private readonly CommitmentDepthPolicy _policy;
    private readonly CommitmentStore _accounts;
    private readonly CommitmentStore _storages;
    private readonly IDb _storageColumn;
    private readonly CommitmentMetadata _metadata;
    private readonly object _windowWriteLock;
    private readonly bool _writeThrough;
    private readonly int _maxOpenWindowNodes;

    private readonly Dictionary<NodePathKey, byte[]?> _blockNodes = [];
    private readonly Dictionary<NodePathKey, ushort> _blockChanged = [];
    private readonly HashSet<NodePathKey> _blockDirtyChildren = [];
    private readonly Dictionary<ValueHash256, int> _blockStorageMaxDepth = [];
    private readonly Dictionary<ValueHash256, bool> _largeTries = [];
    private readonly Dictionary<NodePathKey, bool> _exactBranches = [];
    private readonly Dictionary<NodePathKey, WindowState> _windows = [];
    private readonly HashSet<NodePathKey> _touchedThisBlock = [];
    private readonly byte[]?[] _children = new byte[]?[BranchRlp.ChildCount];
    private readonly byte[]?[] _merged = new byte[]?[BranchRlp.ChildCount];

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
        _blockNodes.Clear();
        _blockChanged.Clear();
        _blockDirtyChildren.Clear();
        _blockStorageMaxDepth.Clear();
        _touchedThisBlock.Clear();
    }

    public void RecordAccountNode(in TreePath path, byte[] rlp)
    {
        if (rlp.Length < Hash256.Size) return;

        RecordAccount(path, rlp, changed: null);
    }

    public void RecordAccountNode(in TreePath path, ReadOnlySpan<byte> rlp)
    {
        if (rlp.Length < Hash256.Size || path.Length > _policy.AccountCheckpointDepth + 1) return;
        if (path.Length == _policy.AccountCheckpointDepth + 1)
        {
            _blockDirtyChildren.Add(NodePathKey.ForAccount(path));
            return;
        }

        RecordAccount(path, rlp.ToArray(), changed: null);
    }

    public void RecordAccountNode(in TreePath path, byte[] rlp, ushort changedChildren) => RecordAccount(path, rlp, changedChildren);

    public void RecordAccountEmpty(in TreePath path) => RecordAccount(path, rlp: null, changed: null);

    public void RecordStorageNode(in ValueHash256 accountPath, in TreePath path, byte[] rlp)
    {
        NoteStorageDepth(accountPath, path.Length);
        if (rlp.Length < Hash256.Size) return;

        RecordStorage(accountPath, path, rlp, changed: null);
    }

    public void RecordStorageNode(in ValueHash256 accountPath, in TreePath path, ReadOnlySpan<byte> rlp)
    {
        NoteStorageDepth(accountPath, path.Length);
        if (rlp.Length < Hash256.Size || path.Length > _policy.StorageCheckpointDepth + 1) return;
        if (path.Length == _policy.StorageCheckpointDepth + 1)
        {
            _blockDirtyChildren.Add(NodePathKey.ForStorage(accountPath, path));
            return;
        }

        RecordStorage(accountPath, path, rlp.ToArray(), changed: null);
    }

    public void RecordStorageNode(in ValueHash256 accountPath, in TreePath path, byte[] rlp, ushort changedChildren) =>
        RecordStorage(accountPath, path, rlp, changedChildren);

    public void RecordStorageEmpty(in ValueHash256 accountPath, in TreePath path) => RecordStorage(accountPath, path, rlp: null, changed: null);

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
        foreach ((NodePathKey key, byte[]? rlp) in _blockNodes)
        {
            CommitmentTier tier = key.IsStorage
                ? _policy.StorageTier(key.Depth, IsLargeStorageTrie(key.Scope))
                : _policy.AccountTier(key.Depth);

            switch (tier)
            {
                case CommitmentTier.PerChange:
                    WriteExact(key, rlp);
                    break;
                case CommitmentTier.Checkpoint:
                    Accumulate(key, rlp);
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

    public void Dispose() => CommitBatch();

    public static void WriteLargeTrieFlagKey(Span<byte> destination, in ValueHash256 accountPath)
    {
        CommitmentKeyLayout.WriteIdentity(destination, accountPath);
        destination[CommitmentKeyLayout.IdentityLength] = LargeTrieFlag;
    }

    private void RecordAccount(in TreePath path, byte[]? rlp, ushort? changed)
    {
        int depth = path.Length;
        if (depth > _policy.AccountCheckpointDepth + 1) return;

        NodePathKey key = NodePathKey.ForAccount(path);
        if (depth == _policy.AccountCheckpointDepth + 1)
        {
            _blockDirtyChildren.Add(key);
            return;
        }

        Record(key, rlp, changed);
    }

    private void RecordStorage(in ValueHash256 accountPath, in TreePath path, byte[]? rlp, ushort? changed)
    {
        int depth = path.Length;
        if (depth > _policy.StorageCheckpointDepth + 1) return;

        NodePathKey key = NodePathKey.ForStorage(accountPath, path);
        if (depth == _policy.StorageCheckpointDepth + 1)
        {
            _blockDirtyChildren.Add(key);
            return;
        }

        Record(key, rlp, changed);
    }

    private void Record(in NodePathKey key, byte[]? rlp, ushort? changed)
    {
        _blockNodes[key] = rlp;
        if (changed is { } mask) _blockChanged[key] = mask;
        else _blockChanged.Remove(key);
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

    private void WriteExact(in NodePathKey key, byte[]? rlp)
    {
        byte[] row;
        bool isBranch = false;
        if (rlp is null)
        {
            row = ParentRowCodec.EncodeEmpty(_block);
        }
        else if (BranchRlp.IsBranch(rlp))
        {
            isBranch = true;
            BranchRlp.ReadChildren(rlp, _children);
            ushort presence = PresenceOf(_children);
            bool wasBranch = _exactBranches.TryGetValue(key, out bool previous) && previous;
            ushort changed = CommitmentDepthPolicy.IsFullVectorSuffix(_block) || !wasBranch ? presence : ChangedChildren(key, _children);
            row = ParentRowCodec.EncodeBranch(_block, presence, changed, _children);
        }
        else
        {
            row = ParentRowCodec.EncodeWholeNode(_block, rlp);
        }

        _exactBranches[key] = isBranch;
        Write(key, exact: true, _block, row);
    }

    private void Accumulate(in NodePathKey key, byte[]? rlp)
    {
        if (!_windows.TryGetValue(key, out WindowState? state))
        {
            state = new WindowState();
            _windows[key] = state;
        }

        _touchedThisBlock.Add(key);
        state.LastBlock = _block;

        if (rlp is null)
        {
            state.Kind = WindowKind.Empty;
            state.WholeNodeRlp = null;
            state.Presence = 0;
            return;
        }

        if (!BranchRlp.IsBranch(rlp))
        {
            state.Kind = WindowKind.Whole;
            state.WholeNodeRlp = rlp;
            state.Presence = 0;
            return;
        }

        BranchRlp.ReadChildren(rlp, _children);
        ushort presence = PresenceOf(_children);
        ushort changed = ChangedChildren(key, _children);
        if (state.Kind is WindowKind.Whole or WindowKind.Empty) changed |= presence;

        state.Kind = WindowKind.Branch;
        state.WholeNodeRlp = null;
        state.Presence = presence;
        state.Changed |= changed;
        Array.Copy(_children, state.Latest, BranchRlp.ChildCount);
    }

    private void FlushWindows(ulong window)
    {
        List<KeyValuePair<NodePathKey, WindowState>> pending = [.. _windows];
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
    }

    private void MergeWrite(in NodePathKey key, WindowState state, ulong window)
    {
        Span<byte> prefix = stackalloc byte[CommitmentKeyLayout.MaxKeyLength];
        int prefixLength = key.WritePrefix(prefix, exact: false);
        CommitmentStore store = Store(key);
        byte[]? existing = store.TryGetExact(prefix[..prefixLength], window);
        byte[] row = Merge(existing, state, window, _merged);
        store.Write(prefix[..prefixLength], window, row, GetBatch(key.IsStorage ? FlatHistoryColumns.StorageCommitments : FlatHistoryColumns.AccountCommitments));
    }

    private static byte[] Merge(byte[]? existing, WindowState state, ulong window, byte[]?[] merged)
    {
        bool full = CommitmentDepthPolicy.IsFullVectorSuffix(window);
        if (existing is null || !ParentRowCodec.IsValid(existing))
        {
            return Encode(state, full);
        }

        bool existingNewer = ParentRowCodec.LastBlock(existing) > state.LastBlock;
        if (state.Kind != WindowKind.Branch || !ParentRowCodec.IsBranchRow(existing))
        {
            return existingNewer ? existing : Encode(state, full);
        }

        Array.Clear(merged);
        ushort existingChanged = ParentRowCodec.Changed(existing);
        ushort changed = (ushort)(existingChanged | state.Changed);
        ulong lastBlock = Math.Max(ParentRowCodec.LastBlock(existing), state.LastBlock);

        ushort presence;
        if (existingNewer)
        {
            presence = ParentRowCodec.Presence(existing);
            ParentRowCodec.Fill(existing, existingChanged, merged);
            for (int index = 0; index < BranchRlp.ChildCount; index++)
            {
                if (((existingChanged >> index) & 1) == 0) merged[index] = state.Latest[index];
            }
        }
        else
        {
            presence = state.Presence;
            Array.Copy(state.Latest, merged, BranchRlp.ChildCount);
        }

        return ParentRowCodec.EncodeBranch(lastBlock, presence, full ? (ushort)(presence | changed) : changed, merged);
    }

    private static byte[] Encode(WindowState state, bool full) =>
        state.Kind switch
        {
            WindowKind.Empty => ParentRowCodec.EncodeEmpty(state.LastBlock),
            WindowKind.Whole => ParentRowCodec.EncodeWholeNode(state.LastBlock, state.WholeNodeRlp!),
            _ => ParentRowCodec.EncodeBranch(state.LastBlock, state.Presence, full ? (ushort)(state.Presence | state.Changed) : state.Changed, state.Latest),
        };

    private ushort ChangedChildren(in NodePathKey key, byte[]?[] children)
    {
        if (_blockChanged.TryGetValue(key, out ushort explicitMask)) return explicitMask;

        ushort changed = 0;
        for (int index = 0; index < BranchRlp.ChildCount; index++)
        {
            byte[]? child = children[index];
            if (child is null) continue;

            if (child.Length < Hash256.Size) changed |= (ushort)(1 << index);
            else
            {
                NodePathKey childKey = key.Child(index);
                if (_blockNodes.ContainsKey(childKey) || _blockDirtyChildren.Contains(childKey)) changed |= (ushort)(1 << index);
            }
        }

        return changed;
    }

    private static ushort PresenceOf(byte[]?[] children)
    {
        ushort presence = 0;
        for (int index = 0; index < BranchRlp.ChildCount; index++)
        {
            if (children[index] is not null) presence |= (ushort)(1 << index);
        }

        return presence;
    }

    private void Write(in NodePathKey key, bool exact, ulong suffix, byte[] row)
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
        public WindowKind Kind;
        public ulong LastBlock;
        public ushort Presence;
        public ushort Changed;
        public byte[]? WholeNodeRlp;
        public readonly byte[]?[] Latest = new byte[]?[BranchRlp.ChildCount];
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
