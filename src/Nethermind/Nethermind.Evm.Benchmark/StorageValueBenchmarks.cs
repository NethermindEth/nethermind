// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.State;

namespace Nethermind.Evm.Benchmark;

public class StorageValueBenchmarks
{
    [Benchmark(OperationsPerInvoke = 8)]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(8)]
    [Arguments(9)]
    [Arguments(17)]
    [Arguments(18)]
    [Arguments(23)]
    [Arguments(24)]
    [Arguments(31)]
    public int LeadingZeros(int nonZero)
    {
        Span<byte> span = stackalloc byte[32];
        span[nonZero] = 1;

        var v = new StorageValue(span);

        return
            v.BytesWithNoLeadingZeroes.Length +
            v.BytesWithNoLeadingZeroes.Length +
            v.BytesWithNoLeadingZeroes.Length +
            v.BytesWithNoLeadingZeroes.Length +

            v.BytesWithNoLeadingZeroes.Length +
            v.BytesWithNoLeadingZeroes.Length +
            v.BytesWithNoLeadingZeroes.Length +
            v.BytesWithNoLeadingZeroes.Length;
    }
}
