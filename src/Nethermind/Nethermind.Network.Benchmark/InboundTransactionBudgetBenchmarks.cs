// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using Nethermind.Core.Test;
using Nethermind.Network.P2P.Subprotocols.Eth;

namespace Nethermind.Network.Benchmarks;

[MemoryDiagnoser]
public class InboundTransactionBudgetBenchmarks
{
    private readonly InboundTransactionBudget _budget = new(RunImmediatelyScheduler.Instance);

    [Benchmark]
    public void ReserveAndRelease() => _budget.TryReserve(4096)!.Dispose();
}
