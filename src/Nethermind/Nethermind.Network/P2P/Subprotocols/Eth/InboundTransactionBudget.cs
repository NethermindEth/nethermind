// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Collections;

namespace Nethermind.Network.P2P.Subprotocols.Eth;

/// <summary>Limits transaction input retained by decoding and background submission.</summary>
/// <remarks>
/// Charges the larger of uncompressed wire bytes and estimated transaction overhead, with a floor per message.
/// Peers sharing a scheduler share the global limit. A reservation follows the owned list across rescheduling.
/// </remarks>
internal sealed class InboundTransactionBudget(IBackgroundTaskScheduler scheduler)
{
    internal const int GlobalLimit = 64 * 1024 * 1024;
    internal const int PeerLimit = 16 * 1024 * 1024;
    internal const int MinimumCharge = 4096;
    internal const int TransactionCharge = 768;
    private static readonly ConditionalWeakTable<IBackgroundTaskScheduler, SharedBudget> SharedBudgets = [];
    private readonly SharedBudget _shared = SharedBudgets.GetValue(scheduler, static _ => new());
    private int _used;

    internal Reservation? TryReserve(int bytes, int transactionCount = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        ArgumentOutOfRangeException.ThrowIfNegative(transactionCount);
        if (transactionCount > PeerLimit / TransactionCharge) return null;
        int charge = Math.Max(Math.Max(bytes, transactionCount * TransactionCharge), MinimumCharge);
        // Reject peers at their own limit before contending on the shared counter.
        if (!TryCharge(ref _used, charge, PeerLimit)) return null;
        if (!TryCharge(ref _shared.Used, charge, GlobalLimit))
        {
            Interlocked.Add(ref _used, -charge);
            return null;
        }

        try
        {
            return new Reservation(this, charge);
        }
        catch
        {
            Release(charge);
            throw;
        }
    }

    private static bool TryCharge(ref int used, int charge, int limit)
    {
        int current = Volatile.Read(ref used);
        while (charge <= limit - current)
        {
            int observed = Interlocked.CompareExchange(ref used, current + charge, current);
            if (observed == current) return true;
            current = observed;
        }
        return false;
    }

    private void Release(int charge)
    {
        Interlocked.Add(ref _shared.Used, -charge);
        Interlocked.Add(ref _used, -charge);
    }

    private sealed class SharedBudget
    {
        public int Used;
    }

    internal sealed class Reservation(InboundTransactionBudget owner, int charge) : IOwnedReadOnlyList<Transaction>
    {
        private InboundTransactionBudget? _owner = owner;
        private IOwnedReadOnlyList<Transaction> _transactions = IOwnedReadOnlyList<Transaction>.Empty;

        internal void Attach(IOwnedReadOnlyList<Transaction> transactions) => _transactions = transactions;
        public int Count => _transactions.Count;
        public Transaction this[int index] => _transactions[index];
        public ReadOnlySpan<Transaction> AsSpan() => _transactions.AsSpan();
        public IEnumerator<Transaction> GetEnumerator() => _transactions.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public void Dispose()
        {
            InboundTransactionBudget? budget = Interlocked.Exchange(ref _owner, null);
            if (budget is null) return;
            try
            {
                _transactions.Dispose();
            }
            finally
            {
                _transactions = IOwnedReadOnlyList<Transaction>.Empty;
                budget.Release(charge);
            }
        }
    }
}
