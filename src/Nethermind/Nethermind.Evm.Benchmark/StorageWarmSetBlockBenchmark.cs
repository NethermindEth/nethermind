// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using BenchmarkDotNet.Attributes;
using Nethermind.Core;

namespace Nethermind.Evm.Benchmark;

/// <summary>
/// Sweeps per-tx warm-set size independently of tx count.
/// </summary>
/// <remarks>
/// <see cref="SlotsPerTxParam"/> = 4 is the control: many light txs whose warm sets stay
/// tiny (and whose slot keys are shared by every tx). If block cost at fixed tx count
/// grows with slots-per-tx faster than the added SLOAD gas explains, the per-tx
/// O(warm-set) reset is implicated; if the control is expensive at high tx count
/// despite tiny warm sets, the overhead is fixed per-tx (pool churn, per-tx setup).
/// </remarks>
[Config(typeof(SyntheticBlockConfig))]
[MemoryDiagnoser]
[JsonExporterAttribute.FullCompressed]
public class StorageWarmSetBlockBenchmark : SyntheticStorageBlockBenchmarkBase
{
    [Params(50, 150, 300)]
    public int TxCountParam { get; set; }

    [Params(4, 20, 100)]
    public int SlotsPerTxParam { get; set; }

    protected override int TxCount => TxCountParam;
    protected override int SlotsPerTx => SlotsPerTxParam;

    [Benchmark(OperationsPerInvoke = OpsPerInvoke)]
    public Block[] ProcessBlock() => ProcessBlockCore();
}
