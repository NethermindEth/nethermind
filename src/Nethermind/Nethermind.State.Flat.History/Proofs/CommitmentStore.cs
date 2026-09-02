// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Db;

namespace Nethermind.State.Flat.History.Proofs;

internal sealed class CommitmentStore
{
    private readonly ISortedKeyValueStore _column;

    public CommitmentStore(IDb column)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (column is not ISortedKeyValueStore sorted)
        {
            throw new ArgumentException($"A commitment column must be a {nameof(ISortedKeyValueStore)}.", nameof(column));
        }

        _column = sorted;
    }

    public void Write(scoped ReadOnlySpan<byte> prefix, ulong suffix, scoped ReadOnlySpan<byte> value, IWriteBatch batch)
    {
        Span<byte> rowKey = stackalloc byte[CommitmentKeyLayout.MaxKeyLength];
        int keyLength = CommitmentKeyLayout.WriteSeekKey(rowKey, prefix, suffix);
        batch.PutSpan(rowKey[..keyLength], value);
    }

    public byte[]? TryGetExact(scoped ReadOnlySpan<byte> prefix, ulong suffix)
    {
        Span<byte> rowKey = stackalloc byte[CommitmentKeyLayout.MaxKeyLength];
        int keyLength = CommitmentKeyLayout.WriteSeekKey(rowKey, prefix, suffix);
        return _column.Get(rowKey[..keyLength]);
    }

    public RowChain OpenAtOrBelow(scoped ReadOnlySpan<byte> prefix, ulong suffix, ResolutionBudget? budget = null)
    {
        Span<byte> seekKey = stackalloc byte[CommitmentKeyLayout.MaxKeyLength];
        int keyLength = CommitmentKeyLayout.WriteSeekKey(seekKey, prefix, suffix);

        Span<byte> upperBound = stackalloc byte[CommitmentKeyLayout.MaxKeyLength + 1];
        int upperLength = CommitmentKeyLayout.WriteUpperBound(upperBound, prefix);

        return new RowChain(_column.GetViewBetween(seekKey[..keyLength], upperBound[..upperLength]), keyLength, budget);
    }

    public readonly struct RowChain(ISortedView view, int keyLength, ResolutionBudget? budget) : IDisposable
    {
        public bool MoveNext()
        {
            while (view.MoveNext())
            {
                budget?.ChargeRow();
                if (view.CurrentKey.Length == keyLength) return true;
            }

            return false;
        }

        public ulong CurrentSuffix => CommitmentKeyLayout.ReadSuffix(view.CurrentKey);

        public ReadOnlySpan<byte> CurrentValue => view.CurrentValue;

        public void Dispose() => view.Dispose();
    }
}
