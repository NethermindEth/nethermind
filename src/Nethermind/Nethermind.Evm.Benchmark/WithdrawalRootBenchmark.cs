// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.State.Proofs;

namespace Nethermind.Evm.Benchmark;

[MemoryDiagnoser]
public class WithdrawalRootBenchmark
{
    private Withdrawal[] _withdrawals = null!;

    [Params(0, 1, 8, 16)]
    public int Count { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _withdrawals = new Withdrawal[Count];
        for (int i = 0; i < Count; i++)
            _withdrawals[i] = new Withdrawal { Index = (ulong)(1000000 + i), ValidatorIndex = (ulong)i, Address = TestItem.AddressA, AmountInGwei = (ulong)(i + 1) };
    }

    [Benchmark]
    public Hash256 CalculateRoot() => WithdrawalTrie.CalculateRoot(_withdrawals);
}
