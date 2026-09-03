// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;

namespace Nethermind.State.Flat.History.Proofs;

internal sealed class CommitmentStore
{
    private readonly IDb _column;

    public CommitmentStore(IDb column)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (column is not ISortedKeyValueStore)
        {
            throw new ArgumentException($"A commitment column must be a {nameof(ISortedKeyValueStore)}.", nameof(column));
        }

        _column = column;
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

    public int ReadStorageTrieDepth(in ValueHash256 accountPath)
    {
        Span<byte> key = stackalloc byte[CommitmentKeyLayout.IdentityLength + 1];
        CommitmentKeyLayout.WriteStorageTrieDepthKey(key, accountPath);
        Span<byte> value = _column.GetSpan(key);
        try
        {
            return value.Length == 1 ? value[0] : 0;
        }
        finally
        {
            _column.DangerousReleaseMemory(value);
        }
    }

    public void WriteStorageTrieDepth(in ValueHash256 accountPath, int depth)
    {
        Span<byte> key = stackalloc byte[CommitmentKeyLayout.IdentityLength + 1];
        CommitmentKeyLayout.WriteStorageTrieDepthKey(key, accountPath);
        _column.PutSpan(key, [(byte)depth]);
    }

    public Span<byte> GetExactSpan(scoped ReadOnlySpan<byte> prefix, ulong suffix)
    {
        Span<byte> rowKey = stackalloc byte[CommitmentKeyLayout.MaxKeyLength];
        int keyLength = CommitmentKeyLayout.WriteSeekKey(rowKey, prefix, suffix);
        return _column.GetSpan(rowKey[..keyLength]);
    }

    public void Release(Span<byte> value) => _column.DangerousReleaseMemory(value);

    public RowChain OpenAtOrBelow(scoped ReadOnlySpan<byte> prefix, ulong suffix, ResolutionBudget? budget = null)
    {
        Span<byte> seekKey = stackalloc byte[CommitmentKeyLayout.MaxKeyLength];
        int keyLength = CommitmentKeyLayout.WriteSeekKey(seekKey, prefix, suffix);

        Span<byte> upperBound = stackalloc byte[CommitmentKeyLayout.MaxKeyLength + 1];
        int upperLength = CommitmentKeyLayout.WriteUpperBound(upperBound, prefix);

        return new RowChain(((ISortedKeyValueStore)_column).GetViewBetween(seekKey[..keyLength], upperBound[..upperLength]), keyLength, budget);
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
