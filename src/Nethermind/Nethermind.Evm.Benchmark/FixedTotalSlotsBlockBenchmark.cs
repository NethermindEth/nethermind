// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using BenchmarkDotNet.Attributes;
using Nethermind.Core;

namespace Nethermind.Evm.Benchmark;

/// <summary>
/// Holds total EVM work constant (6000 SLOADs per block) while splitting it into
/// 50, 150 or 300 transactions, so any cost growth with <see cref="TxCountParam"/>
/// is pure per-transaction overhead.
/// </summary>
[Config(typeof(SyntheticBlockConfig))]
[MemoryDiagnoser]
[JsonExporterAttribute.FullCompressed]
public class FixedTotalSlotsBlockBenchmark : SyntheticStorageBlockBenchmarkBase
{
    private const int TotalSlots = 6000;

    [Params(50, 150, 300)]
    public int TxCountParam { get; set; }

    protected override int TxCount => TxCountParam;
    protected override int SlotsPerTx => TotalSlots / TxCountParam;

    [Benchmark(OperationsPerInvoke = OpsPerInvoke)]
    public Block[] ProcessBlock() => ProcessBlockCore();
}
