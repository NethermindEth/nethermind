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

namespace Nethermind.Evm.Benchmark;

/// <summary>
/// One transaction of many <c>SLOAD</c>/<c>SSTORE</c> through the production interpreter, so the per-access
/// storage path (warm-set check, gas charge, value read and write) dominates the time.
/// </summary>
/// <remarks>
/// Each case does about a thousand storage accesses in one transaction, which keeps the frame and transaction
/// overhead small and the run-to-run error low enough to see a few nanoseconds per access.
/// </remarks>
[MemoryDiagnoser]
public class StorageAccessBenchmarks
{
    private const int Increments = 512;
    private const int RotatingSlots = 16;
    private const int ColdReads = 1_024;
    private const int Transfers = 64;
    private const ulong GasLimit = 30_000_000;
    private const int WorkloadWarmupTransactions = 40_000;
    private const int OpcodeRefreshTransactions = 500_000;

    private static readonly Address _sender = new("0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
    private static readonly byte[] _transferTopic = Keccak.Compute("Transfer(address,address,uint256)").BytesToArray();

    private readonly IReleaseSpec _spec = MainnetSpecProvider.Instance.GetSpec(MainnetSpecProvider.OsakaActivation);
    private readonly BlockHeader _header = new(Keccak.Zero, Keccak.Zero, Address.Zero, UInt256.One, MainnetSpecProvider.IstanbulBlockNumber, Int64.MaxValue, 1UL, Bytes.Empty);
    private IVirtualMachine _virtualMachine = null!;
    private IWorldState _stateProvider = null!;
    private IDisposable _stateScope = null!;
    private CodeInfo _sameSlotCode = null!;
    private CodeInfo _rotatingSlotsCode = null!;
    private CodeInfo _coldReadsCode = null!;
    private CodeInfo _tokenTransfersCode = null!;
    private CodeInfo _stopCode = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _stateProvider = TestWorldStateFactory.CreateForTest(logManager: NullLogManager.Instance);
        _stateScope = _stateProvider.BeginScope(IWorldState.PreGenesis);
        _stateProvider.CreateAccount(Address.Zero, 1000.Ether);
        _stateProvider.Commit(_spec);

        EthereumCodeInfoRepository codeInfoRepository = new(_stateProvider);
        _virtualMachine = new EthereumVirtualMachine(new TestBlockhashProvider(), MainnetSpecProvider.Instance, new OneLoggerLogManager(NullLogger.Instance));
        _virtualMachine.SetBlockExecutionContext(new BlockExecutionContext(_header, _spec));
        _virtualMachine.SetTxExecutionContext(new TxExecutionContext(Address.Zero, codeInfoRepository, null, 0));

        _sameSlotCode = new CodeInfo(BuildSameSlotCode());
        _rotatingSlotsCode = new CodeInfo(BuildRotatingSlotsCode());
        _coldReadsCode = new CodeInfo(BuildColdReadsCode());
        _tokenTransfersCode = new CodeInfo(BuildTokenTransfersCode());
        _stopCode = new CodeInfo(new byte[] { (byte)Instruction.STOP });

        foreach (CodeInfo codeInfo in (CodeInfo[])[_sameSlotCode, _rotatingSlotsCode, _coldReadsCode, _tokenTransfersCode])
        {
            TransactionSubstate substate = Execute(codeInfo);
            if (substate.IsError || substate.ShouldRevert)
                throw new InvalidOperationException($"Storage benchmark code failed: {substate.Error}");
        }

        // Run every shape until hot, then advance the periodic opcode-table refreshes with STOP so no case tiers
        // its own handlers during measurement.
        for (int i = 0; i < WorkloadWarmupTransactions; i++)
        {
            CodeInfo codeInfo = (i & 3) switch
            {
                0 => _sameSlotCode,
                1 => _rotatingSlotsCode,
                2 => _coldReadsCode,
                _ => _tokenTransfersCode,
            };
            Execute(codeInfo);
        }

        for (int i = WorkloadWarmupTransactions; i < OpcodeRefreshTransactions; i++)
        {
            Execute(_stopCode);
        }
    }

    [GlobalCleanup]
    public void GlobalCleanup() => _stateScope.Dispose();

    /// <summary>Increments one slot again and again: every access repeats the previous cell.</summary>
    [Benchmark]
    public TransactionSubstate SameSlotIncrements() => Execute(_sameSlotCode);

    /// <summary>Increments 16 slots round-robin: every read is warm but names a different cell than the access before it.</summary>
    [Benchmark]
    public TransactionSubstate RotatingWarmSlotIncrements() => Execute(_rotatingSlotsCode);

    /// <summary>Reads distinct slots once each: every access is cold.</summary>
    [Benchmark]
    public TransactionSubstate ColdSlotReads() => Execute(_coldReadsCode);

    /// <summary>Token transfer bodies: hashed balance slots, one warm sender and a cold recipient each, and a transfer log.</summary>
    [Benchmark]
    public TransactionSubstate TokenTransferBodies() => Execute(_tokenTransfersCode);

    private TransactionSubstate Execute(CodeInfo codeInfo)
    {
        using ExecutionEnvironment environment = ExecutionEnvironment.Rent(
            executingAccount: Address.Zero,
            codeSource: Address.Zero,
            caller: Address.Zero,
            codeInfo: codeInfo,
            callDepth: 0,
            value: 0,
            inputData: default);

        TransactionSubstate substate;
        using (VmState<EthereumGasPolicy> vmState = VmState<EthereumGasPolicy>.RentTopLevel(
            EthereumGasPolicy.FromULong(GasLimit),
            ExecutionType.TRANSACTION,
            environment,
            new StackAccessTracker(),
            _stateProvider.TakeSnapshot()))
        {
            substate = _virtualMachine.ExecuteTransaction<OffFlag>(vmState, _stateProvider, NullTxTracer.Instance);
        }

        _stateProvider.Reset();
        return substate;
    }

    private static Prepare Increment(Prepare code, int slot) => code
        .PushData(slot)
        .Op(Instruction.SLOAD)
        .PushData(1)
        .Op(Instruction.ADD)
        .PushData(slot)
        .Op(Instruction.SSTORE);

    private static byte[] BuildSameSlotCode()
    {
        Prepare code = Prepare.EvmCode;
        for (int i = 0; i < Increments; i++) code = Increment(code, 0);
        return code.Op(Instruction.STOP).Done;
    }

    private static byte[] BuildRotatingSlotsCode()
    {
        Prepare code = Prepare.EvmCode;
        for (int i = 0; i < Increments; i++) code = Increment(code, i % RotatingSlots);
        return code.Op(Instruction.STOP).Done;
    }

    private static byte[] BuildColdReadsCode()
    {
        Prepare code = Prepare.EvmCode;
        for (int i = 0; i < ColdReads; i++) code = code.PushData(i).Op(Instruction.SLOAD).Op(Instruction.POP);
        return code.Op(Instruction.STOP).Done;
    }

    /// <summary>Pushes the balance slot of <paramref name="holder"/>, keccak256(holder . 0), as a mapping at slot 0 has it.</summary>
    private static Prepare BalanceSlot(Prepare code, Address holder) => code
        .PushData(holder)
        .Op(Instruction.PUSH0)
        .Op(Instruction.MSTORE)
        .PushData(64)
        .Op(Instruction.PUSH0)
        .Op(Instruction.KECCAK256);

    private static byte[] BuildTokenTransfersCode()
    {
        const int amount = 7;
        Prepare code = Prepare.EvmCode;
        for (int i = 0; i < Transfers; i++)
        {
            Address recipient = new(Keccak.Compute(i.ToString()));

            code = BalanceSlot(code, _sender)
                .Op(Instruction.DUP1)
                .Op(Instruction.SLOAD)
                .PushData(amount)
                .Op(Instruction.SWAP1)
                .Op(Instruction.SUB)
                .Op(Instruction.SWAP1)
                .Op(Instruction.SSTORE);

            code = BalanceSlot(code, recipient)
                .Op(Instruction.DUP1)
                .Op(Instruction.SLOAD)
                .PushData(amount)
                .Op(Instruction.ADD)
                .Op(Instruction.SWAP1)
                .Op(Instruction.SSTORE);

            code = code
                .PushData(amount)
                .PushData(64)
                .Op(Instruction.MSTORE)
                .PushData(recipient)
                .PushData(_sender)
                .PushData(_transferTopic)
                .PushData(32)
                .PushData(64)
                .Op(Instruction.LOG3);
        }

        return code.Op(Instruction.STOP).Done;
    }
}
