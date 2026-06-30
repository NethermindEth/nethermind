// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
using Nethermind.Specs.Forks;

namespace Nethermind.Evm.Benchmark;

/// <summary>
/// Replicates the gas-benchmarks <c>storage_sload_same_key</c> fixture: a 24 KiB contract of
/// unrolled SLOAD opcodes where each loaded value is used as the next key, so after the first
/// access every SLOAD is a warm read of the same storage cell.
/// </summary>
/// <remarks>
/// Bytecode layout (24576 bytes, mirrors the deployed fixture contract):
/// <c>PUSH0, JUMPDEST, SLOAD * 24571, PUSH1 0x01, JUMP</c>.
/// With <c>PreSet=false</c> the cell is empty so the chained key stays zero; with
/// <c>PreSet=true</c> the cell holds a self-referential non-zero 32-byte value.
/// Each invocation executes one fixture transaction's worth of gas (16,777,216),
/// i.e. ~167k warm SLOADs, ending in an out-of-gas halt exactly like the fixture.
/// </remarks>
[MemoryDiagnoser]
public class SLoadSameKeyBenchmarks
{
    private const int CodeSize = 24576;
    private const long TxGas = 16_777_216;

    // Self-referential storage key/value for the PreSet=true variant: SLOAD(K) == K.
    private static readonly UInt256 SelfKey = UInt256.Parse("0x6F1D2C3B4A59687766554433221100FFEEDDCCBBAA99887766554433221100FF");

    private static readonly Address ContractAddress = new("0x55e5b385b218a8a94d5766e423fb25e6ad9c9ffa");

    private IReleaseSpec _spec = null!;
    private IVirtualMachine _virtualMachine = null!;
    private IWorldState _stateProvider = null!;
    private IDisposable _stateScope = null!;
    private ITxTracer _txTracer = NullTxTracer.Instance;
    private ExecutionEnvironment _environment = null!;
    private CodeInfo _codeInfo = null!;

    [Params(false, true)]
    public bool PreSet { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _spec = Fork.GetLatest();

        byte[] code = new byte[CodeSize];
        code[0] = (byte)Instruction.PUSH0;
        code[1] = (byte)Instruction.JUMPDEST;
        code.AsSpan(2, CodeSize - 5).Fill((byte)Instruction.SLOAD);
        code[CodeSize - 3] = (byte)Instruction.PUSH1;
        code[CodeSize - 2] = 0x01;
        code[CodeSize - 1] = (byte)Instruction.JUMP;

        _stateProvider = TestWorldStateFactory.CreateForTest();
        _stateScope = _stateProvider.BeginScope(IWorldState.PreGenesis);
        _stateProvider.CreateAccount(ContractAddress, 1.Ether);
        _stateProvider.InsertCode(ContractAddress, code, _spec);
        if (PreSet)
        {
            // SLOAD(0) -> K and SLOAD(K) -> K keep the chained key constant and non-zero.
            byte[] selfKeyBytes = SelfKey.ToBigEndian();
            _stateProvider.Set(new StorageCell(ContractAddress, UInt256.Zero), selfKeyBytes);
            _stateProvider.Set(new StorageCell(ContractAddress, SelfKey), selfKeyBytes);
        }
        _stateProvider.Commit(_spec);

        EthereumCodeInfoRepository codeInfoRepository = new(_stateProvider);
        BlockHeader header = new(
            Keccak.Zero,
            Keccak.Zero,
            Address.Zero,
            UInt256.One,
            MainnetSpecProvider.PragueActivation.BlockNumber,
            long.MaxValue,
            1UL,
            [],
            0,
            0);

        _virtualMachine = new EthereumVirtualMachine(new TestBlockhashProvider(), MainnetSpecProvider.Instance, LimboLogs.Instance);
        _virtualMachine.SetBlockExecutionContext(new BlockExecutionContext(header, _spec, UInt256.Zero));
        _virtualMachine.SetTxExecutionContext(new TxExecutionContext(Address.Zero, codeInfoRepository, null, 0));

        _codeInfo = new CodeInfo(code);
        _environment = ExecutionEnvironment.Rent(
            executingAccount: ContractAddress,
            codeSource: ContractAddress,
            caller: Address.Zero,
            codeInfo: _codeInfo,
            callDepth: 0,
            value: 0,
            inputData: default);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _environment.Dispose();
        _stateScope.Dispose();
    }

    [Benchmark(OperationsPerInvoke = (int)(TxGas / 100))]
    public void ExecuteSameKeyLoop()
    {
        VmState<EthereumGasPolicy> vmState = VmState<EthereumGasPolicy>.RentTopLevel(
            EthereumGasPolicy.FromLong(TxGas),
            ExecutionType.TRANSACTION,
            _environment,
            new StackAccessTracker(),
            _stateProvider.TakeSnapshot());
        _virtualMachine.ExecuteTransaction<OffFlag>(vmState, _stateProvider, _txTracer);
        vmState.Dispose();
        _stateProvider.Reset();
    }
}
