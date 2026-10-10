// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Test;
using Nethermind.Network.P2P.Subprotocols.Eth;

namespace Nethermind.Network.Benchmarks;

[MemoryDiagnoser]
public class ConcurrentInboundTransactionBudgetBenchmarks
{
    private const int BatchSize = 16384;
    private Action<int> _work = null!;

    [Params(1, 4, 16)]
    public int Peers { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        InboundTransactionBudget[] budgets = new InboundTransactionBudget[Peers];
        for (int i = 0; i < budgets.Length; i++) budgets[i] = new(RunImmediatelyScheduler.Instance);
        int iterations = BatchSize / Peers;
        _work = peer =>
        {
            InboundTransactionBudget budget = budgets[peer];
            for (int i = 0; i < iterations; i++) budget.TryReserve(4096)!.Dispose();
        };
    }

    [Benchmark(OperationsPerInvoke = BatchSize)]
    public void ReserveAndRelease() => Parallel.For(0, Peers, _work);
}
