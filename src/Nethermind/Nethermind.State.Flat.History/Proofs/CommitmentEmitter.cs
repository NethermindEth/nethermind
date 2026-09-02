// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Trie;

namespace Nethermind.State.Flat.History.Proofs;

public sealed class CommitmentEmitter : IDisposable
{
    public const int DefaultMaxOpenWindowNodes = 200_000;
    private const int MaxRowsPerBatch = 65_536;
    private static readonly object WindowWriteLock = new();

    private readonly IColumnsDb<FlatHistoryColumns> _history;
    private readonly CommitmentDepthPolicy _policy;
    private readonly CommitmentStore _accounts;
    private readonly CommitmentStore _storages;
    private readonly bool _writeThrough;
    private readonly int _maxOpenWindowNodes;

    private readonly Dictionary<NodePathKey, byte[]> _blockNodes = [];
    private readonly Dictionary<ValueHash256, int> _blockStorageMaxDepth = [];
    private readonly Dictionary<NodePathKey, WindowState> _windows = [];
    private readonly HashSet<NodePathKey> _touchedThisBlock = [];
    private readonly byte[]?[] _children = new byte[]?[BranchRlp.ChildCount];
    private readonly byte[]?[] _merged = new byte[]?[BranchRlp.ChildCount];

    private IColumnsWriteBatch<FlatHistoryColumns>? _batch;
    private int _rowsInBatch;
    private ulong _block;

    private CommitmentEmitter(IColumnsDb<FlatHistoryColumns> history, CommitmentDepthPolicy policy, bool writeThrough, int maxOpenWindowNodes)
    {
        _history = history;
        _policy = policy;
        _writeThrough = writeThrough;
        _maxOpenWindowNodes = maxOpenWindowNodes;
        _accounts = new CommitmentStore(history.GetColumnDb(FlatHistoryColumns.AccountCommitments));
        _storages = new CommitmentStore(history.GetColumnDb(FlatHistoryColumns.StorageCommitments));
    }

    public static CommitmentEmitter ForWalk(IColumnsDb<FlatHistoryColumns> history, CommitmentDepthPolicy policy, int maxOpenWindowNodes = DefaultMaxOpenWindowNodes) =>
        new(history, policy, writeThrough: false, maxOpenWindowNodes);

    public static CommitmentEmitter ForTip(IColumnsDb<FlatHistoryColumns> history, CommitmentDepthPolicy policy) =>
        new(history, policy, writeThrough: true, DefaultMaxOpenWindowNodes);

    public void BeginBlock(ulong block)
    {
        _block = block;
        _blockNodes.Clear();
        _blockStorageMaxDepth.Clear();
        _touchedThisBlock.Clear();
    }

    public void RecordAccountNode(in TreePath path, byte[] rlp)
    {
        if (rlp.Length < Hash256.Size) return;

        _blockNodes[NodePathKey.ForAccount(path)] = rlp;
    }

    public void RecordAccountNode(in TreePath path, ReadOnlySpan<byte> rlp) => RecordAccountNode(path, rlp.ToArray());

    public void RecordStorageNode(in ValueHash256 accountPath, in TreePath path, byte[] rlp)
    {
        if (rlp.Length < Hash256.Size) return;

        _blockNodes[NodePathKey.ForStorage(accountPath, path)] = rlp;
        _blockStorageMaxDepth[accountPath] = Math.Max(_blockStorageMaxDepth.GetValueOrDefault(accountPath), path.Length);
    }

    public void RecordStorageNode(in ValueHash256 accountPath, in TreePath path, ReadOnlySpan<byte> rlp) => RecordStorageNode(accountPath, path, rlp.ToArray());

    public void CompleteBlock()
    {
        foreach ((NodePathKey key, byte[] rlp) in _blockNodes)
        {
            CommitmentTier tier = key.IsStorage
                ? _policy.StorageTier(key.Depth, _blockStorageMaxDepth.GetValueOrDefault(key.Scope) >= _policy.LargeTrieSignalDepth)
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
            lock (WindowWriteLock)
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

    public void FlushOpenWindows(ulong lastBlock) => FlushWindows(_policy.WindowClosingAt(lastBlock));

    public void Dispose() => CommitBatch();

    private void WriteExact(in NodePathKey key, byte[] rlp)
    {
        byte[] row;
        if (BranchRlp.IsBranch(rlp))
        {
            BranchRlp.ReadChildren(rlp, _children);
            ushort presence = PresenceOf(_children);
            ushort changed = CommitmentDepthPolicy.IsFullVectorSuffix(_block) ? presence : ChangedChildren(key, _children);
            row = ParentRowCodec.EncodeBranch(_block, presence, changed, _children);
        }
        else
        {
            row = ParentRowCodec.EncodeWholeNode(_block, rlp);
        }

        Write(key, exact: true, _block, row);
    }

    private void Accumulate(in NodePathKey key, byte[] rlp)
    {
        if (!_windows.TryGetValue(key, out WindowState? state))
        {
            state = new WindowState();
            _windows[key] = state;
        }

        _touchedThisBlock.Add(key);
        state.LastBlock = _block;

        if (!BranchRlp.IsBranch(rlp))
        {
            state.WholeNodeRlp = rlp;
            return;
        }

        state.WholeNodeRlp = null;
        BranchRlp.ReadChildren(rlp, _children);
        state.Presence = PresenceOf(_children);
        state.Changed |= ChangedChildren(key, _children);
        Array.Copy(_children, state.Latest, BranchRlp.ChildCount);
    }

    private void FlushWindows(ulong window)
    {
        lock (WindowWriteLock)
        {
            foreach ((NodePathKey key, WindowState state) in _windows) MergeWrite(key, state, window);
            CommitBatch();
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
        if (existing is null || (!ParentRowCodec.IsBranchRow(existing) && !ParentRowCodec.IsWholeNodeRow(existing)))
        {
            return Encode(state, full);
        }

        bool existingNewer = ParentRowCodec.LastBlock(existing) > state.LastBlock;
        if (state.WholeNodeRlp is not null || !ParentRowCodec.IsBranchRow(existing))
        {
            return existingNewer ? existing : Encode(state, full);
        }

        Array.Clear(merged);
        ushort existingChanged = ParentRowCodec.Changed(existing);
        ushort changed = (ushort)(existingChanged | state.Changed);
        ushort presence = existingNewer ? ParentRowCodec.Presence(existing) : state.Presence;
        ulong lastBlock = Math.Max(ParentRowCodec.LastBlock(existing), state.LastBlock);

        if (existingNewer)
        {
            ParentRowCodec.Fill(existing, existingChanged, merged);
            for (int index = 0; index < BranchRlp.ChildCount; index++)
            {
                if (merged[index] is null && ((state.Changed >> index) & 1) == 1) merged[index] = state.Latest[index];
            }
        }
        else
        {
            for (int index = 0; index < BranchRlp.ChildCount; index++)
            {
                if (((state.Changed >> index) & 1) == 1) merged[index] = state.Latest[index];
            }

            ParentRowCodec.Fill(existing, (ushort)(existingChanged & ~state.Changed), merged);
        }

        if (full && !existingNewer)
        {
            return ParentRowCodec.EncodeBranch(lastBlock, presence, presence, state.Latest);
        }

        return ParentRowCodec.EncodeBranch(lastBlock, presence, (ushort)(changed & presence & PresenceOf(merged)), merged);
    }

    private static byte[] Encode(WindowState state, bool full) =>
        state.WholeNodeRlp is { } whole
            ? ParentRowCodec.EncodeWholeNode(state.LastBlock, whole)
            : ParentRowCodec.EncodeBranch(state.LastBlock, state.Presence, (ushort)((full ? state.Presence : state.Changed & state.Presence) & PresenceOf(state.Latest)), state.Latest);

    private ushort ChangedChildren(in NodePathKey key, byte[]?[] children)
    {
        ushort changed = 0;
        for (int index = 0; index < BranchRlp.ChildCount; index++)
        {
            byte[]? child = children[index];
            if (child is null) continue;
            if (child.Length < Hash256.Size || _blockNodes.ContainsKey(key.Child(index))) changed |= (ushort)(1 << index);
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

    private sealed class WindowState
    {
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
