// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Core;

namespace Nethermind.JsonRpc.Benchmark;

[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class ParityTransactionPositionBenchmarks
{
    private const int FreshBlocks = 8;
    private Transaction[] _transactions = null!;
    private Transaction[][] _freshTransactions = null!;

    [Params(1, 16, 128, 1024, 4096)]
    public int TransactionCount { get; set; }

    [Params(false, true)]
    public bool WholeBlock { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _transactions = new Transaction[TransactionCount];
        for (int i = 0; i < _transactions.Length; i++) _transactions[i] = new Transaction { Nonce = (ulong)i };
        if (ArraySearch() != IndexedSearch()) throw new InvalidOperationException("Both lookups must report identical positions.");
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Reused")]
    public int ArraySearch() => ArraySearch(_transactions);

    [Benchmark]
    [BenchmarkCategory("Reused")]
    public int IndexedSearch() => IndexedSearch(_transactions);

    [IterationSetup(Targets = [nameof(FreshArraySearch), nameof(FreshIndexedSearch)])]
    public void CreateFreshTransactions()
    {
        _freshTransactions = new Transaction[FreshBlocks][];
        for (int b = 0; b < FreshBlocks; b++)
        {
            Transaction[] transactions = new Transaction[TransactionCount];
            for (int i = 0; i < transactions.Length; i++) transactions[i] = new Transaction { Nonce = (ulong)i };
            _freshTransactions[b] = transactions;
        }
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = FreshBlocks)]
    [BenchmarkCategory("Fresh")]
    [InvocationCount(1, unrollFactor: 1)]
    public int FreshArraySearch()
    {
        int sum = 0;
        foreach (Transaction[] transactions in _freshTransactions) sum += ArraySearch(transactions);
        return sum;
    }

    [Benchmark(OperationsPerInvoke = FreshBlocks)]
    [BenchmarkCategory("Fresh")]
    [InvocationCount(1, unrollFactor: 1)]
    public int FreshIndexedSearch()
    {
        int sum = 0;
        foreach (Transaction[] transactions in _freshTransactions) sum += IndexedSearch(transactions);
        return sum;
    }

    private int ArraySearch(Transaction[] transactions)
    {
        int sum = 0;
        int first = WholeBlock ? 0 : transactions.Length - 1;
        for (int i = first; i < transactions.Length; i++) sum += Array.IndexOf(transactions, transactions[i]);
        return sum;
    }

    private int IndexedSearch(Transaction[] transactions)
    {
        TransactionPositionLookup lookup = new();
        try
        {
            int sum = 0;
            int first = WholeBlock ? 0 : transactions.Length - 1;
            for (int i = first; i < transactions.Length; i++) sum += lookup.Find(transactions, transactions[i])!.Value;
            return sum;
        }
        finally
        {
            lookup.Dispose();
        }
    }
}
