// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Reflection;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.TxPool.Collections;
using Nethermind.TxPool.Comparison;
using Nethermind.TxPool.Filters;

namespace Nethermind.Benchmarks.TxPool;

[MemoryDiagnoser]
public class PendingReplacementBenchmarks
{
    private Func<Transaction, TxDistinctSortedPool, TxDistinctSortedPool, Transaction?> _find = null!;
    private TxDistinctSortedPool _pool = null!;
    private TxDistinctSortedPool _otherPool = null!;
    private Transaction _probe = null!;

    [Params(1, 16, 256, 2048)]
    public int Pending { get; set; }

    [Params(false, true)]
    public bool Hit { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        Type helper = typeof(IIncomingTxFilter).Assembly.GetType("Nethermind.TxPool.Filters.PendingReplacement", throwOnError: true)!;
        MethodInfo find = helper.GetMethod("Find", BindingFlags.Public | BindingFlags.Static)!;
        _find = find.CreateDelegate<Func<Transaction, TxDistinctSortedPool, TxDistinctSortedPool, Transaction?>>();
        _pool = new TxDistinctSortedPool(Pending + 1, CompareTxByNonce.Instance, LimboLogs.Instance);
        _otherPool = new TxDistinctSortedPool(1, CompareTxByNonce.Instance, LimboLogs.Instance);

        for (int i = 0; i < Pending; i++)
        {
            Transaction tx = Build.A.Transaction
                .WithNonce((ulong)i)
                .WithSenderAddress(TestItem.AddressA)
                .WithHash(Keccak.Compute($"pending-{i}"))
                .TestObject;
            if (!_pool.TryInsert(tx.Hash!, tx)) throw new InvalidOperationException("The benchmark pool is incomplete.");
        }
        _probe = Build.A.Transaction
            .WithNonce((ulong)(Hit ? Pending - 1 : Pending))
            .WithSenderAddress(TestItem.AddressA)
            .WithHash(TestItem.KeccakH)
            .TestObject;

        if ((_find(_probe, _pool, _otherPool) is not null) != Hit)
            throw new InvalidOperationException("The benchmark lookup does not match its case.");
    }

    [Benchmark]
    public Transaction? FindReplacement() => _find(_probe, _pool, _otherPool);
}
