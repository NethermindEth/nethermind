// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.State.Flat.History.Proofs;
using Nethermind.Trie;

namespace Nethermind.State.Flat.History.Walk;

internal readonly struct SeriesKey(bool isStorage, in ValueHash256 scope, in TreePath path, bool scratch)
{
    public const byte ScratchMarker = 0xFD;
    public const int MaxPrefixLength = 2 + CommitmentKeyLayout.IdentityLength + CommitmentKeyLayout.MaxPathLength;
    public const int MaxKeyLength = MaxPrefixLength + sizeof(ulong) + 1;

    private readonly ValueHash256 _scope = scope;
    private readonly TreePath _path = path;

    public bool IsStorage => isStorage;

    public bool Scratch => scratch;

    public TreePath Path => _path;

    public ValueHash256 Scope => _scope;

    public FlatHistoryColumns Column => scratch || !isStorage ? FlatHistoryColumns.AccountCommitments : FlatHistoryColumns.StorageCommitments;

    public int WritePrefix(Span<byte> destination)
    {
        if (!scratch)
        {
            if (!isStorage) return CommitmentKeyLayout.WritePathPrefix(destination, _path, exact: true);

            Span<byte> identity = stackalloc byte[CommitmentKeyLayout.IdentityLength];
            CommitmentKeyLayout.WriteIdentity(identity, _scope);
            return CommitmentKeyLayout.WriteScopedPathPrefix(destination, identity, _path, exact: true);
        }

        destination[0] = ScratchMarker;
        destination[1] = isStorage ? (byte)1 : (byte)0;
        int written = 2;
        if (isStorage)
        {
            CommitmentKeyLayout.WriteIdentity(destination[written..], _scope);
            written += CommitmentKeyLayout.IdentityLength;
        }

        return written + CommitmentKeyLayout.WritePathPrefix(destination[written..], _path, exact: false);
    }
}

internal sealed class SeriesWriter(IColumnsDb<FlatHistoryColumns> history) : IDisposable
{
    private const int MaxRowsPerBatch = 65_536;

    private readonly IDb _scratchColumn = history.GetColumnDb(FlatHistoryColumns.AccountCommitments);
    private IWriteBatch? _batch;
    private int _rowsInBatch;

    public void Write(in SeriesKey key, ulong block, byte[] row)
    {
        if (!key.Scratch) return;

        Span<byte> rowKey = stackalloc byte[SeriesKey.MaxKeyLength];
        int prefixLength = key.WritePrefix(rowKey);
        int keyLength = CommitmentKeyLayout.WriteSeekKey(rowKey, rowKey[..prefixLength], block);
        _batch ??= _scratchColumn.StartWriteBatch();
        _batch.PutSpan(rowKey[..keyLength], row);
        if (++_rowsInBatch >= MaxRowsPerBatch) Flush();
    }

    public void Delete(in SeriesKey key)
    {
        if (!key.Scratch) return;

        Flush();
        Span<byte> prefix = stackalloc byte[SeriesKey.MaxKeyLength];
        int prefixLength = key.WritePrefix(prefix);
        Span<byte> upper = stackalloc byte[SeriesKey.MaxKeyLength];
        int upperLength = CommitmentKeyLayout.WriteUpperBound(upper, prefix[..prefixLength]);
        RemoveRange(prefix[..prefixLength], upper[..upperLength]);
    }

    public void DeleteAllScratch()
    {
        Flush();
        RemoveRange([SeriesKey.ScratchMarker], [SeriesKey.ScratchMarker + 1]);
    }

    public void Flush()
    {
        _batch?.Dispose();
        _batch = null;
        _rowsInBatch = 0;
    }

    public void Dispose() => Flush();

    private void RemoveRange(ReadOnlySpan<byte> lowerInclusive, ReadOnlySpan<byte> upperExclusive)
    {
        if (_scratchColumn is IRangeRemovableKeyValueStore ranged)
        {
            ranged.RemoveRange(lowerInclusive, upperExclusive);
            return;
        }

        List<byte[]> keys = [];
        using (ISortedView view = ((ISortedKeyValueStore)_scratchColumn).GetViewBetween(lowerInclusive, upperExclusive))
        {
            while (view.MoveNext()) keys.Add(view.CurrentKey.ToArray());
        }

        using IWriteBatch batch = _scratchColumn.StartWriteBatch();
        foreach (byte[] key in keys) batch.Remove(key);
    }
}

internal sealed class NodeSeriesState
{
    public NodeViewKind Kind = NodeViewKind.Empty;
    public byte[]? WholeRlp;
    public ushort Presence;
    public readonly byte[]?[] Refs = new byte[]?[BranchRlp.ChildCount];

    public void Apply(ReadOnlySpan<byte> row)
    {
        if (ParentRowCodec.IsEmptyRow(row))
        {
            Clear(NodeViewKind.Empty);
            return;
        }

        if (ParentRowCodec.IsWholeNodeRow(row))
        {
            Clear(NodeViewKind.Whole);
            WholeRlp = ParentRowCodec.WholeNodeRlp(row).ToArray();
            return;
        }

        if (!ParentRowCodec.IsBranchRow(row)) throw new InvalidDataException("A commitment series row is neither a branch, a whole node nor an empty marker.");

        ushort presence = ParentRowCodec.Presence(row);
        ushort changed = ParentRowCodec.Changed(row);
        if (Kind != NodeViewKind.Branch) Clear(NodeViewKind.Branch);

        for (int index = 0; index < BranchRlp.ChildCount; index++)
        {
            if (((changed >> index) & 1) == 1 || ((presence >> index) & 1) == 0) Refs[index] = null;
        }

        ParentRowCodec.Fill(row, changed, Refs);
        Kind = NodeViewKind.Branch;
        Presence = presence;
    }

    public void MaterializeStart(CommitmentStore.RowChain newestAtOrBelow)
    {
        Apply(newestAtOrBelow.CurrentValue);
        if (Kind != NodeViewKind.Branch) return;

        ushort missing = Missing();
        while (missing != 0 && newestAtOrBelow.MoveNext())
        {
            ReadOnlySpan<byte> older = newestAtOrBelow.CurrentValue;
            if (!ParentRowCodec.IsBranchRow(older)) break;

            missing = (ushort)(missing & ~ParentRowCodec.Fill(older, missing, Refs));
        }

        if (missing != 0) throw new InvalidDataException("A commitment series starts with a branch row whose children cannot be filled from its own chain.");
    }

    public NodeView ToView()
    {
        switch (Kind)
        {
            case NodeViewKind.Empty:
                return NodeView.Empty;
            case NodeViewKind.Whole:
                return NodeView.Whole(WholeRlp!);
            default:
                if (Presence == 0) return NodeView.Empty;
                if (Missing() != 0) throw new InvalidDataException("A commitment series row lists a child it never carried a reference for.");

                byte[]?[] copy = new byte[]?[BranchRlp.ChildCount];
                Array.Copy(Refs, copy, BranchRlp.ChildCount);
                return NodeView.Branch(copy);
        }
    }

    private ushort Missing()
    {
        ushort missing = 0;
        for (int index = 0; index < BranchRlp.ChildCount; index++)
        {
            if (((Presence >> index) & 1) == 1 && Refs[index] is null) missing |= (ushort)(1 << index);
        }

        return missing;
    }

    private void Clear(NodeViewKind kind)
    {
        Kind = kind;
        WholeRlp = null;
        Presence = 0;
        Array.Clear(Refs);
    }
}

internal sealed class SeriesReader(IColumnsDb<FlatHistoryColumns> history)
{
    private const ulong Window = 16_384;

    private readonly ISortedKeyValueStore _accountColumn = (ISortedKeyValueStore)history.GetColumnDb(FlatHistoryColumns.AccountCommitments);
    private readonly ISortedKeyValueStore _storageColumn = (ISortedKeyValueStore)history.GetColumnDb(FlatHistoryColumns.StorageCommitments);

    public NodeSeriesState ReadStart(in SeriesKey key, ulong from)
    {
        NodeSeriesState state = new();
        Span<byte> prefix = stackalloc byte[SeriesKey.MaxKeyLength];
        int prefixLength = key.WritePrefix(prefix);
        CommitmentStore store = new(history.GetColumnDb(key.Column));
        using CommitmentStore.RowChain chain = store.OpenAtOrBelow(prefix[..prefixLength], from);
        if (chain.MoveNext()) state.MaterializeStart(chain);
        return state;
    }

    public IEnumerable<(ulong Block, byte[] Row)> ReadAscending(SeriesKey key, ulong fromExclusive, ulong toInclusive)
    {
        ISortedKeyValueStore column = key.Column == FlatHistoryColumns.StorageCommitments ? _storageColumn : _accountColumn;
        byte[] prefix = new byte[SeriesKey.MaxPrefixLength];
        int prefixLength = key.WritePrefix(prefix);
        prefix = prefix[..prefixLength];

        for (ulong lo = fromExclusive + 1; lo <= toInclusive; lo += Window)
        {
            ulong hi = toInclusive - lo < Window - 1 ? toInclusive : lo + Window - 1;
            List<(ulong Block, byte[] Row)> rows = ReadDescending(column, prefix, lo, hi);
            for (int i = rows.Count - 1; i >= 0; i--) yield return rows[i];
            if (hi == toInclusive) break;
        }
    }

    private static List<(ulong Block, byte[] Row)> ReadDescending(ISortedKeyValueStore column, byte[] prefix, ulong lo, ulong hi)
    {
        List<(ulong Block, byte[] Row)> rows = [];
        Span<byte> lower = stackalloc byte[SeriesKey.MaxKeyLength];
        int lowerLength = CommitmentKeyLayout.WriteSeekKey(lower, prefix, hi);
        Span<byte> upper = stackalloc byte[SeriesKey.MaxKeyLength];
        int upperLength = CommitmentKeyLayout.WriteSeekKey(upper, prefix, lo - 1);

        using ISortedView view = column.GetViewBetween(lower[..lowerLength], upper[..upperLength]);
        while (view.MoveNext())
        {
            if (view.CurrentKey.Length != lowerLength) continue;

            ulong block = CommitmentKeyLayout.ReadSuffix(view.CurrentKey);
            if (block < lo || block > hi) continue;

            rows.Add((block, view.CurrentValue.ToArray()));
        }

        return rows;
    }
}
