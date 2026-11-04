// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using Nethermind.Core;

namespace Nethermind.Blockchain.Tracing.ParityStyle;

/// <summary>Finds transaction positions during one block trace, keeping the first occurrence of each reference.</summary>
/// <remarks>
/// A single lookup retains the array search. Repeated searches of a large block build a bounded index after
/// scanning four block lengths. The transaction array must remain unchanged for the active block trace.
/// The owner serializes access and disposes the lookup when the block ends.
/// </remarks>
internal struct TransactionPositionLookup : IDisposable
{
    private const int MinIndexedTransactions = 128;
    private const int MaxIndexedTransactions = 1 << 16;
    private Transaction[]? _transactions;
    private int[]? _buckets;
    private int _mask;
    private long _scanned;
    private bool _cannotIndex;

    internal int? Find(Transaction[] transactions, Transaction? transaction)
    {
        if (transaction is null) return null;
        if (TryFindIndexed(transactions, transaction, out int index)) return index;

        index = Array.IndexOf(transactions, transaction);
        RecordScan(transactions, index);
        return index;
    }

    internal readonly bool CanRecordScan(Transaction[] transactions) =>
        transactions.Length is >= MinIndexedTransactions and <= MaxIndexedTransactions
        && (!ReferenceEquals(_transactions, transactions) || !_cannotIndex);

    internal void RecordScan(Transaction[] transactions, int index)
    {
        if (!ReferenceEquals(_transactions, transactions))
        {
            Dispose();
            _transactions = transactions;
        }

        if (_buckets is null)
        {
            _scanned += index < 0 ? transactions.Length : index + 1;
            if (CanRecordScan(transactions) && _scanned > 4L * transactions.Length)
            {
                Build(transactions);
            }
        }
    }

    internal readonly bool TryFindIndexed(Transaction[] transactions, Transaction transaction, out int index)
    {
        index = -1;
        if (!ReferenceEquals(_transactions, transactions) || _buckets is not { } buckets) return false;

        int slot = RuntimeHelpers.GetHashCode(transaction) & _mask;
        while (true)
        {
            int entry = buckets[slot];
            if (entry == 0) return true;
            if (ReferenceEquals(transactions[entry - 1], transaction))
            {
                index = entry - 1;
                return true;
            }
            slot = (slot + 1) & _mask;
        }
    }

    private void Build(Transaction[] transactions)
    {
        foreach (Transaction transaction in transactions)
        {
            // A derived transaction may override equality, which Array.IndexOf must still honor.
            if (transaction is not null && transaction.GetType() != typeof(Transaction))
            {
                _cannotIndex = true;
                return;
            }
        }

        int capacity = (int)BitOperations.RoundUpToPowerOf2((uint)transactions.Length * 2);
        int[] buckets = ArrayPool<int>.Shared.Rent(capacity);
        buckets.AsSpan(0, capacity).Clear();
        int mask = capacity - 1;
        for (int i = 0; i < transactions.Length; i++)
        {
            Transaction transaction = transactions[i];
            if (transaction is null) continue;
            int slot = RuntimeHelpers.GetHashCode(transaction) & mask;
            while (buckets[slot] != 0 && !ReferenceEquals(transactions[buckets[slot] - 1], transaction))
                slot = (slot + 1) & mask;
            if (buckets[slot] == 0) buckets[slot] = i + 1;
        }
        _mask = mask;
        _buckets = buckets;
    }

    public void Dispose()
    {
        int[]? buckets = _buckets;
        this = default;
        if (buckets is not null) ArrayPool<int>.Shared.Return(buckets);
    }
}
