// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;

namespace Nethermind.Evm.Benchmark;

/// <summary>
/// The multiply, divide, modulo and modular-arithmetic chains of <see cref="OpcodeChainBenchmarks"/>, with plain
/// addition as a control, so a comparison of these opcodes does not have to run every chain.
/// </summary>
[MemoryDiagnoser]
public class ArithmeticChainBenchmarks
{
    private const int ExecutedOpcodeCount = 3846;
    private OpcodeChainBenchmarks _chain = null!;

    [Params("MulDup", "DivSmall", "DivWide", "ModSmall", "ModWide", "AddMod", "MulMod", "Arithmetic")]
    public string Chain { get; set; } = "MulDup";

    [GlobalSetup]
    public void Setup()
    {
        _chain = new OpcodeChainBenchmarks { Chain = Chain, Cancelable = false };
        _chain.Setup();
    }

    [Benchmark(OperationsPerInvoke = ExecutedOpcodeCount)]
    public ReadOnlyMemory<byte> ExecuteContract() => _chain.ExecuteContract();

    [GlobalCleanup]
    public void Cleanup() => _chain.Cleanup();
}
