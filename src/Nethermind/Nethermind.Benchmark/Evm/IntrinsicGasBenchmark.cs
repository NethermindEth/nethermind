// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.Specs.Forks;

namespace Nethermind.Benchmarks.Evm;

/// <summary>
/// Measures the per-call cost of <see cref="IntrinsicGasCalculator.Calculate"/> with and
/// without the intrinsic-gas cache introduced in PR #10514.
///
/// Each benchmark method executes 1 000 calls so that BenchmarkDotNet can report
/// stable per-operation numbers even for very fast (cache-hit) paths.
/// </summary>
[MemoryDiagnoser]
public class IntrinsicGasBenchmark
{
    private const int N = 1_000;

    private Transaction _simpleTx = null!;
    private Transaction _dataTx = null!;
    private Transaction _accessListTx = null!;

    private static readonly byte[] MixedData = CreateMixedData();
    private static readonly IReleaseSpec BerlinSpec = Berlin.Instance;
    private static readonly IReleaseSpec PragueSpec = Prague.Instance;

    private static byte[] CreateMixedData()
    {
        byte[] data = new byte[128];
        for (int i = 0; i < data.Length; i++)
            data[i] = i % 4 == 0 ? (byte)0 : (byte)(i & 0xFF);
        return data;
    }

    private static AccessList BuildAccessList() =>
        new AccessList.Builder()
            .AddAddress(TestItem.AddressA)
            .AddStorage(UInt256.Zero)
            .AddStorage(UInt256.One)
            .AddAddress(TestItem.AddressB)
            .AddStorage(new UInt256(42))
            .Build();

    [GlobalSetup]
    public void Setup()
    {
        _simpleTx = Build.A.Transaction.SignedAndResolved().TestObject;
        _dataTx = Build.A.Transaction.WithData(MixedData).SignedAndResolved().TestObject;
        _accessListTx = Build.A.Transaction.WithAccessList(BuildAccessList()).SignedAndResolved().TestObject;

        // Pre-warm the cache on each transaction with Prague so CacheHit benchmarks
        // start from a hot cache.
        IntrinsicGasCalculator.Calculate(_simpleTx, PragueSpec);
        IntrinsicGasCalculator.Calculate(_dataTx, PragueSpec);
        IntrinsicGasCalculator.Calculate(_accessListTx, PragueSpec);
    }

    // ── Cache-miss benchmarks ────────────────────────────────────────────────
    // Each iteration resets the validity field before every call so the full
    // computation runs every time.

    [Benchmark(Baseline = true, OperationsPerInvoke = N)]
    public EthereumIntrinsicGas SimpleTx_CacheMiss()
    {
        EthereumIntrinsicGas result = default;
        for (int i = 0; i < N; i++)
        {
            _simpleTx._cachedIntrinsicGas = null;
            result = IntrinsicGasCalculator.Calculate(_simpleTx, BerlinSpec);
        }
        return result;
    }

    [Benchmark(OperationsPerInvoke = N)]
    public EthereumIntrinsicGas DataTx_CacheMiss()
    {
        EthereumIntrinsicGas result = default;
        for (int i = 0; i < N; i++)
        {
            _dataTx._cachedIntrinsicGas = null;
            result = IntrinsicGasCalculator.Calculate(_dataTx, BerlinSpec);
        }
        return result;
    }

    [Benchmark(OperationsPerInvoke = N)]
    public EthereumIntrinsicGas AccessListTx_CacheMiss()
    {
        EthereumIntrinsicGas result = default;
        for (int i = 0; i < N; i++)
        {
            _accessListTx._cachedIntrinsicGas = null;
            result = IntrinsicGasCalculator.Calculate(_accessListTx, BerlinSpec);
        }
        return result;
    }

    // ── Cache-hit benchmarks ─────────────────────────────────────────────────
    // Cache is hot from GlobalSetup; same spec used every call → always hits.

    [Benchmark(OperationsPerInvoke = N)]
    public EthereumIntrinsicGas SimpleTx_CacheHit()
    {
        EthereumIntrinsicGas result = default;
        for (int i = 0; i < N; i++)
            result = IntrinsicGasCalculator.Calculate(_simpleTx, PragueSpec);
        return result;
    }

    [Benchmark(OperationsPerInvoke = N)]
    public EthereumIntrinsicGas DataTx_CacheHit()
    {
        EthereumIntrinsicGas result = default;
        for (int i = 0; i < N; i++)
            result = IntrinsicGasCalculator.Calculate(_dataTx, PragueSpec);
        return result;
    }

    [Benchmark(OperationsPerInvoke = N)]
    public EthereumIntrinsicGas AccessListTx_CacheHit()
    {
        EthereumIntrinsicGas result = default;
        for (int i = 0; i < N; i++)
            result = IntrinsicGasCalculator.Calculate(_accessListTx, PragueSpec);
        return result;
    }

    // ── Spec-change benchmark ────────────────────────────────────────────────
    // Alternates between two specs every call: cache never hits.

    [Benchmark(OperationsPerInvoke = N)]
    public EthereumIntrinsicGas SpecChange_CacheMiss()
    {
        EthereumIntrinsicGas result = default;
        for (int i = 0; i < N; i++)
        {
            IReleaseSpec spec = (i & 1) == 0 ? BerlinSpec : PragueSpec;
            result = IntrinsicGasCalculator.Calculate(_simpleTx, spec);
        }
        return result;
    }
}
