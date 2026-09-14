// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Db;

namespace Nethermind.State.Flat.History.Changesets;

internal sealed class TransactionChangesetStore
{
    private readonly IDb _column;
    private readonly ISortedKeyValueStore _sorted;
    private readonly Lock _coverageLock = new();

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

    /// <summary>The changesets of transactions below <paramref name="beforeTransaction"/>, in execution order.</summary>
    public ISortedView OpenBefore(ulong block, ushort beforeTransaction) => OpenBetween(block, 0, beforeTransaction);

    /// <summary>The changesets of transactions in <c>[fromTransaction, beforeTransaction)</c>, in execution order.</summary>
    public ISortedView OpenBetween(ulong block, ushort fromTransaction, ushort beforeTransaction)
    {
        Span<byte> lower = stackalloc byte[ChangesetKeyLayout.RowKeyLength];
        Span<byte> upper = stackalloc byte[ChangesetKeyLayout.RowKeyLength];
        ChangesetKeyLayout.WriteBlockBound(lower, block, fromTransaction);
        ChangesetKeyLayout.WriteBlockBound(upper, block, beforeTransaction);
        return _sorted.GetViewBetween(lower, upper, ReadFlags.HintCacheMiss | ReadFlags.HintReadAhead);
    }

    public bool TryGetCoverage(out ulong fromInclusive, out ulong toInclusive)
    {
        Span<byte> key = stackalloc byte[2];
        ChangesetKeyLayout.WriteCoverageKey(key);
        byte[]? value = _column.Get(key);
        if (value is not { Length: 2 * sizeof(ulong) })
        {
            fromInclusive = 0;
            toInclusive = 0;
            return false;
        }

        fromInclusive = BinaryPrimitives.ReadUInt64BigEndian(value);
        toInclusive = BinaryPrimitives.ReadUInt64BigEndian(value.AsSpan(sizeof(ulong)));
        return fromInclusive <= toInclusive;
    }

    public bool Covers(ulong block) => TryGetCoverage(out ulong from, out ulong to) && block >= from && block <= to;

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

            Span<byte> key = stackalloc byte[2];
            ChangesetKeyLayout.WriteCoverageKey(key);
            Span<byte> value = stackalloc byte[2 * sizeof(ulong)];
            BinaryPrimitives.WriteUInt64BigEndian(value, fromInclusive);
            BinaryPrimitives.WriteUInt64BigEndian(value[sizeof(ulong)..], toInclusive);
            _column.PutSpan(key, value);
            return true;
        }
    }
}
