// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;

namespace Nethermind.State.Flat.History.Changesets;

internal sealed class TransactionChangesetStore
{
    private readonly IDb _column;
    private readonly ISortedKeyValueStore _sorted;
    private readonly Lock _coverageLock = new();
    private bool _coverageLoaded;
    private bool _hasCoverage;
    private ulong _coverageFrom;
    private ulong _coverageTo;

    public TransactionChangesetStore(IDb column)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (column is not ISortedKeyValueStore sorted)
        {
            throw new ArgumentException($"The changeset column must be a {nameof(ISortedKeyValueStore)}.", nameof(column));
        }

        _column = column;
        _sorted = sorted;
    }

    public void Write(ulong block, ushort transactionIndex, scoped ReadOnlySpan<byte> changeset, IWriteBatch batch)
    {
        Span<byte> key = stackalloc byte[ChangesetKeyLayout.RowKeyLength];
        ChangesetKeyLayout.WriteRowKey(key, block, transactionIndex);
        batch.PutSpan(key, changeset);
    }

    /// <summary>The hash of the block the rows were built from, so that a block that merely shares the height, a
    /// reorged sibling or a caller-supplied body, is never served another block's prefix.</summary>
    public void WriteBlockHash(ulong block, Hash256 hash, IWriteBatch batch)
    {
        Span<byte> key = stackalloc byte[ChangesetKeyLayout.BlockKeyLength];
        ChangesetKeyLayout.WriteBlockKey(key, block);
        batch.PutSpan(key, hash.Bytes);
    }

    public bool TryGetBlockHash(ulong block, out ValueHash256 hash)
    {
        Span<byte> key = stackalloc byte[ChangesetKeyLayout.BlockKeyLength];
        ChangesetKeyLayout.WriteBlockKey(key, block);
        byte[]? value = _column.Get(key);
        if (value is not { Length: Hash256.Size })
        {
            hash = default;
            return false;
        }

        hash = new ValueHash256(value);
        return true;
    }

    /// <summary>The changesets of transactions in <c>[fromTransaction, beforeTransaction)</c>, in execution order.</summary>
    public ISortedView OpenBetween(ulong block, ushort fromTransaction, ushort beforeTransaction)
    {
        Span<byte> lower = stackalloc byte[ChangesetKeyLayout.RowKeyLength];
        Span<byte> upper = stackalloc byte[ChangesetKeyLayout.RowKeyLength];
        ChangesetKeyLayout.WriteRowKey(lower, block, fromTransaction);
        ChangesetKeyLayout.WriteRowKey(upper, block, beforeTransaction);
        return _sorted.GetViewBetween(lower, upper, ReadFlags.HintCacheMiss | ReadFlags.HintReadAhead);
    }

    public bool TryGetCoverage(out ulong fromInclusive, out ulong toInclusive)
    {
        lock (_coverageLock)
        {
            LoadCoverage();
            fromInclusive = _coverageFrom;
            toInclusive = _coverageTo;
            return _hasCoverage;
        }
    }

    public bool Covers(ulong block) => TryGetCoverage(out ulong from, out ulong to) && block >= from && block <= to;

    /// <summary>Drops every row below <paramref name="floor"/>. Coverage is trimmed first, so a crash between the two
    /// leaves rows nothing claims rather than a claim nothing backs.</summary>
    public void PruneBelow(ulong floor)
    {
        lock (_coverageLock)
        {
            if (TryGetCoverage(out ulong from, out ulong to) && from < floor)
            {
                if (to < floor) ClearCoverage();
                else WriteCoverage(floor, to);
            }

            Span<byte> lower = stackalloc byte[ChangesetKeyLayout.RowKeyLength];
            Span<byte> upper = stackalloc byte[ChangesetKeyLayout.RowKeyLength];
            ChangesetKeyLayout.WriteRowKey(lower, 0, 0);
            ChangesetKeyLayout.WriteRowKey(upper, floor, 0);
            RemoveRange(lower, upper);

            Span<byte> lowerBlock = stackalloc byte[ChangesetKeyLayout.BlockKeyLength];
            Span<byte> upperBlock = stackalloc byte[ChangesetKeyLayout.BlockKeyLength];
            ChangesetKeyLayout.WriteBlockKey(lowerBlock, 0);
            ChangesetKeyLayout.WriteBlockKey(upperBlock, floor);
            RemoveRange(lowerBlock, upperBlock);
        }
    }

    private void RemoveRange(ReadOnlySpan<byte> lower, ReadOnlySpan<byte> upper)
    {
        if (_column is IRangeRemovableKeyValueStore ranged)
        {
            ranged.RemoveRange(lower, upper);
            return;
        }

        using IWriteBatch batch = _column.StartWriteBatch();
        using ISortedView view = _sorted.GetViewBetween(lower, upper);
        while (view.MoveNext()) batch.Remove(view.CurrentKey);
    }

    /// <summary>Extends the covered range when the new one touches it, so a gap can never be claimed as covered.</summary>
    public bool TryExtendCoverage(ulong fromInclusive, ulong toInclusive)
    {
        lock (_coverageLock)
        {
            if (TryGetCoverage(out ulong from, out ulong to))
            {
                if (fromInclusive > to + 1 || toInclusive + 1 < from) return false;

                fromInclusive = Math.Min(from, fromInclusive);
                toInclusive = Math.Max(to, toInclusive);
            }

            WriteCoverage(fromInclusive, toInclusive);
            return true;
        }
    }

    private void LoadCoverage()
    {
        if (_coverageLoaded) return;

        byte[]? value = _column.Get(CoverageKey());
        _hasCoverage = value is { Length: 2 * sizeof(ulong) };
        if (_hasCoverage)
        {
            _coverageFrom = BinaryPrimitives.ReadUInt64BigEndian(value);
            _coverageTo = BinaryPrimitives.ReadUInt64BigEndian(value.AsSpan(sizeof(ulong)));
        }

        _coverageLoaded = true;
    }

    private void WriteCoverage(ulong fromInclusive, ulong toInclusive)
    {
        Span<byte> value = stackalloc byte[2 * sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(value, fromInclusive);
        BinaryPrimitives.WriteUInt64BigEndian(value[sizeof(ulong)..], toInclusive);
        _column.PutSpan(CoverageKey(), value);
        _hasCoverage = true;
        _coverageFrom = fromInclusive;
        _coverageTo = toInclusive;
    }

    private void ClearCoverage()
    {
        _column.Remove(CoverageKey());
        _hasCoverage = false;
    }

    private static readonly byte[] CoverageKeyBytes = BuildCoverageKey();

    private static byte[] CoverageKey() => CoverageKeyBytes;

    private static byte[] BuildCoverageKey()
    {
        byte[] key = new byte[2];
        ChangesetKeyLayout.WriteCoverageKey(key);
        return key;
    }
}
