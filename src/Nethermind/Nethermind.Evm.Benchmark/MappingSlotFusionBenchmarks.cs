// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;

namespace Nethermind.Evm.Benchmark;

/// <summary>
/// Measures what fusing the scratch-space hash is worth, over a run of mapping slot computations.
/// </summary>
/// <remarks>
/// The unfused arm is the same opcodes with the two constant pushes swapped for equivalent PUSH2 forms,
/// which costs identical gas and does identical work but is not recognized as a fusable run — so the two
/// arms differ only in whether the run fuses, with no runtime switch.
/// </remarks>
[MemoryDiagnoser]
public class MappingSlotFusionBenchmarks
{
    /// <summary>Mapping accesses per invocation, so the per-site delta is not lost in frame setup.</summary>
    private const int Accesses = 64;

    private readonly IReleaseSpec _spec = MainnetSpecProvider.Instance.GetSpec(MainnetSpecProvider.CancunActivation);
    private readonly BlockHeader _header = new(Keccak.Zero, Keccak.Zero, Address.Zero, UInt256.One,
        MainnetSpecProvider.ParisBlockNumber + 2, long.MaxValue, MainnetSpecProvider.CancunBlockTimestamp, Bytes.Empty);

    private IVirtualMachine _virtualMachine = null!;
    private IWorldState _stateProvider = null!;
    private IDisposable _stateScope = null!;
    private CodeInfo _codeInfo = null!;

    [Params(true, false)]
    public bool Fused { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _stateProvider = TestWorldStateFactory.CreateForTest();
        _stateScope = _stateProvider.BeginScope(IWorldState.PreGenesis);
        _stateProvider.CreateAccount(Address.Zero, 1000.Ether);
        _stateProvider.Commit(_spec);

        EthereumCodeInfoRepository codeInfoRepository = new(_stateProvider);
        _virtualMachine = new EthereumVirtualMachine(new TestBlockhashProvider(), MainnetSpecProvider.Instance,
            new OneLoggerLogManager(NullLogger.Instance));
        _virtualMachine.SetBlockExecutionContext(new BlockExecutionContext(_header, _spec));
        _virtualMachine.SetTxExecutionContext(new TxExecutionContext(Address.Zero, codeInfoRepository, null, 0));

        _codeInfo = new CodeInfo(BuildCode());

        bool recognized = _codeInfo.Fusion is not null;
        if (recognized != Fused)
        {
            throw new InvalidOperationException(
                $"Bytecode was {(recognized ? "" : "not ")}recognized as fusable, but the arm expects Fused={Fused}.");
        }
    }

    [GlobalCleanup]
    public void GlobalCleanup() => _stateScope.Dispose();

    [Benchmark]
    public void HashMappingSlots()
    {
        using ExecutionEnvironment environment = ExecutionEnvironment.Rent(
            executingAccount: Address.Zero,
            codeSource: Address.Zero,
            caller: Address.Zero,
            codeInfo: _codeInfo,
            callDepth: 0,
            value: 0,
            inputData: default);

        using VmState<EthereumGasPolicy> state = VmState<EthereumGasPolicy>.RentTopLevel(
            EthereumGasPolicy.FromULong(10_000_000UL), ExecutionType.TRANSACTION, environment,
            new StackAccessTracker(), _stateProvider.TakeSnapshot());

        _virtualMachine.ExecuteTransaction<OffFlag>(state, _stateProvider, NullTxTracer.Instance);
        _stateProvider.Reset();
    }

    private byte[] BuildCode()
    {
        List<byte> code = [];
        for (int i = 0; i < Accesses; i++)
        {
            // Slot and key, as a mapping access leaves them below the scratch offset.
            code.AddRange([(byte)Instruction.PUSH1, (byte)i, (byte)Instruction.PUSH1, (byte)(i + 1)]);
            code.AddRange(Zero);
            code.Add((byte)Instruction.MSTORE);
            code.AddRange(Push(0x20));
            code.Add((byte)Instruction.MSTORE);
            code.AddRange(Push(0x40));
            code.AddRange(Zero);
            code.Add((byte)Instruction.KECCAK256);
            code.Add((byte)Instruction.POP);
        }

        code.Add((byte)Instruction.STOP);
        return [.. code];
    }

    /// <summary>PUSH1 in the fused arm; the identically priced PUSH2 form the matcher rejects otherwise.</summary>
    private byte[] Push(byte value) => Fused
        ? [(byte)Instruction.PUSH1, value]
        : [(byte)Instruction.PUSH2, 0x00, value];

    private byte[] Zero => Fused
        ? [(byte)Instruction.PUSH1, 0x00]
        : [(byte)Instruction.PUSH2, 0x00, 0x00];
}
