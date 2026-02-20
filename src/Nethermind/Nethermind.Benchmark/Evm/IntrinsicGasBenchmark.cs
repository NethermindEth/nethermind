// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.Specs.Forks;

namespace Nethermind.Benchmarks.Evm;

[MemoryDiagnoser]
public class IntrinsicGasBenchmark
{
    private Transaction _simpleTxCold = null!;
    private Transaction _simpleTxPrimed = null!;
    private Transaction _dataTxCold = null!;
    private Transaction _dataTxPrimed = null!;
    private Transaction _accessListTxCold = null!;
    private Transaction _accessListTxPrimed = null!;
    private Transaction _specChangeTx = null!;

    private static readonly byte[] MixedData = CreateMixedData();

    private static byte[] CreateMixedData()
    {
        byte[] data = new byte[128];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = i % 4 == 0 ? (byte)0 : (byte)(i & 0xFF);
        }
        return data;
    }

    private static AccessList BuildAccessList()
    {
        return new AccessList.Builder()
            .AddAddress(TestItem.AddressA)
            .AddStorage(UInt256.Zero)
            .AddStorage(UInt256.One)
            .AddAddress(TestItem.AddressB)
            .AddStorage(new UInt256(42))
            .Build();
    }

    private static Transaction BuildSimpleTx() =>
        Build.A.Transaction.SignedAndResolved().TestObject;

    private static Transaction BuildDataTx() =>
        Build.A.Transaction.WithData(MixedData).SignedAndResolved().TestObject;

    private static Transaction BuildAccessListTx() =>
        Build.A.Transaction.WithAccessList(BuildAccessList()).SignedAndResolved().TestObject;

    [GlobalSetup]
    public void Setup()
    {
        // Primed transactions: call Calculate once to populate the cache
        _simpleTxPrimed = BuildSimpleTx();
        IntrinsicGasCalculator.Calculate(_simpleTxPrimed, Prague.Instance);

        _dataTxPrimed = BuildDataTx();
        IntrinsicGasCalculator.Calculate(_dataTxPrimed, Prague.Instance);

        _accessListTxPrimed = BuildAccessListTx();
        IntrinsicGasCalculator.Calculate(_accessListTxPrimed, Prague.Instance);

        // Cold transactions: will be replaced each iteration
        _simpleTxCold = BuildSimpleTx();
        _dataTxCold = BuildDataTx();
        _accessListTxCold = BuildAccessListTx();

        // SpecChange tx: primed with Prague, will be called with Berlin
        _specChangeTx = BuildSimpleTx();
        IntrinsicGasCalculator.Calculate(_specChangeTx, Prague.Instance);
    }

    [IterationSetup(Target = nameof(SimpleTx_CacheMiss))]
    public void SetupSimpleTxCold() => _simpleTxCold = BuildSimpleTx();

    [IterationSetup(Target = nameof(DataTx_CacheMiss))]
    public void SetupDataTxCold() => _dataTxCold = BuildDataTx();

    [IterationSetup(Target = nameof(AccessListTx_CacheMiss))]
    public void SetupAccessListTxCold() => _accessListTxCold = BuildAccessListTx();

    [IterationSetup(Target = nameof(SpecChange_CacheMiss))]
    public void SetupSpecChangeTx()
    {
        _specChangeTx = BuildSimpleTx();
        IntrinsicGasCalculator.Calculate(_specChangeTx, Prague.Instance);
    }

    [Benchmark(Baseline = true)]
    public EthereumIntrinsicGas SimpleTx_CacheMiss() =>
        IntrinsicGasCalculator.Calculate(_simpleTxCold, Berlin.Instance);

    [Benchmark]
    public EthereumIntrinsicGas SimpleTx_CacheHit() =>
        IntrinsicGasCalculator.Calculate(_simpleTxPrimed, Prague.Instance);

    [Benchmark]
    public EthereumIntrinsicGas DataTx_CacheMiss() =>
        IntrinsicGasCalculator.Calculate(_dataTxCold, Berlin.Instance);

    [Benchmark]
    public EthereumIntrinsicGas DataTx_CacheHit() =>
        IntrinsicGasCalculator.Calculate(_dataTxPrimed, Prague.Instance);

    [Benchmark]
    public EthereumIntrinsicGas AccessListTx_CacheMiss() =>
        IntrinsicGasCalculator.Calculate(_accessListTxCold, Berlin.Instance);

    [Benchmark]
    public EthereumIntrinsicGas AccessListTx_CacheHit() =>
        IntrinsicGasCalculator.Calculate(_accessListTxPrimed, Prague.Instance);

    [Benchmark]
    public EthereumIntrinsicGas SpecChange_CacheMiss() =>
        IntrinsicGasCalculator.Calculate(_specChangeTx, Berlin.Instance);
}
