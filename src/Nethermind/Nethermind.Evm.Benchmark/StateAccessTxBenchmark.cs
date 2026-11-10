// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;

namespace Nethermind.Evm.Benchmark;

/// <summary>
/// Measures whole transactions whose cost is mostly warm/cold access tracking: a token-transfer shaped
/// read-modify-write of two mapping slots, a contract calling precompiles, and a contract reading warm
/// accounts and slots. Each call is rolled back with <c>CallAndRestore</c>, so every iteration starts cold.
/// </summary>
[Config(typeof(StateAccessTxConfig))]
[MemoryDiagnoser]
public class StateAccessTxBenchmark
{
    private class StateAccessTxConfig : ManualConfig
    {
        public StateAccessTxConfig() => AddJob(Job.MediumRun.WithToolchain(InProcessNoEmitToolchain.Instance));
    }

    private const int N = 1_000;
    private const int Repeats = 16;

    private static readonly IReleaseSpec Spec = Osaka.Instance;
    private static readonly ISpecProvider SpecProvider = new TestSpecProvider(Osaka.Instance);

    private static readonly Address TokenAddress = TestItem.AddressB;
    private static readonly Address PrecompileCallerAddress = TestItem.AddressD;
    private static readonly Address WarmReaderAddress = TestItem.AddressE;
    private static readonly Address IdentityAddress = new("0x0000000000000000000000000000000000000004");
    private static readonly Address Sha256Address = new("0x0000000000000000000000000000000000000002");

    private static readonly UInt256 FromSlot = MappingSlot(TestItem.AddressA);
    private static readonly UInt256 ToSlot = MappingSlot(TestItem.AddressC);

    private IWorldState _stateProvider = null!;
    private IDisposable _stateScope = null!;
    private ITransactionProcessor _processor = null!;

    private Transaction _tokenTransferTx = null!;
    private Transaction _precompileCallsTx = null!;
    private Transaction _warmReadsTx = null!;

    private static UInt256 MappingSlot(Address holder)
    {
        byte[] preimage = new byte[64];
        holder.Bytes.CopyTo(preimage.AsSpan(12));
        return new UInt256(Keccak.Compute(preimage).Bytes, isBigEndian: true);
    }

    // Debit the sender's slot and credit the recipient's, as a token transfer does.
    private static byte[] TokenTransferCode() => Prepare.EvmCode
        .PushData(FromSlot)
        .Op(Instruction.SLOAD)
        .PushData(1)
        .Op(Instruction.SWAP1)
        .Op(Instruction.SUB)
        .PushData(FromSlot)
        .Op(Instruction.SSTORE)
        .PushData(ToSlot)
        .Op(Instruction.SLOAD)
        .PushData(1)
        .Op(Instruction.ADD)
        .PushData(ToSlot)
        .Op(Instruction.SSTORE)
        .Op(Instruction.STOP)
        .Done;

    private static byte[] PrecompileCallsCode() => Prepare.EvmCode
        .For(Repeats, static (p, _) => p.StaticCall(IdentityAddress, 1_000).Op(Instruction.POP))
        .For(Repeats, static (p, _) => p.Call(Sha256Address, 1_000).Op(Instruction.POP))
        .Op(Instruction.STOP)
        .Done;

    // Two slots alternate so each read misses the one-entry warm cache and reaches the access set.
    private static byte[] WarmReadsCode() => Prepare.EvmCode
        .For(Repeats, static (p, _) => p
            .Op(Instruction.CALLER).Op(Instruction.BALANCE).Op(Instruction.POP)
            .Op(Instruction.ADDRESS).Op(Instruction.BALANCE).Op(Instruction.POP)
            .PushData(TokenAddress).Op(Instruction.EXTCODESIZE).Op(Instruction.POP))
        .For(Repeats, static (p, _) => p
            .PushData(1).Op(Instruction.SLOAD).Op(Instruction.POP)
            .PushData(2).Op(Instruction.SLOAD).Op(Instruction.POP))
        .Op(Instruction.STOP)
        .Done;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _stateProvider = TestWorldStateFactory.CreateForTest(logManager: NullLogManager.Instance);
        _stateScope = _stateProvider.BeginScope(IWorldState.PreGenesis);

        _stateProvider.CreateAccount(TestItem.AddressA, 10_000.Ether);
        _stateProvider.CreateAccount(TokenAddress, UInt256.Zero);
        _stateProvider.InsertCode(TokenAddress, TokenTransferCode(), Spec);
        _stateProvider.Set(new StorageCell(TokenAddress, FromSlot), 1_000_000);
        _stateProvider.Set(new StorageCell(TokenAddress, ToSlot), 1);
        _stateProvider.CreateAccount(PrecompileCallerAddress, UInt256.Zero);
        _stateProvider.InsertCode(PrecompileCallerAddress, PrecompileCallsCode(), Spec);
        _stateProvider.CreateAccount(WarmReaderAddress, UInt256.Zero);
        _stateProvider.InsertCode(WarmReaderAddress, WarmReadsCode(), Spec);
        _stateProvider.Set(new StorageCell(WarmReaderAddress, 1), 7);
        _stateProvider.Set(new StorageCell(WarmReaderAddress, 2), 9);
        _stateProvider.Commit(Spec);
        _stateProvider.CommitTree(0);

        BlockHeader header = Build.A.BlockHeader
            .WithNumber(1)
            .WithGasLimit(30_000_000)
            .WithBaseFee(10.GWei)
            .WithStateRoot(_stateProvider.StateRoot)
            .TestObject;

        EthereumCodeInfoRepository codeInfo = new(_stateProvider);
        EthereumVirtualMachine vm = new(new TestBlockhashProvider(), SpecProvider, NullLogManager.Instance);
        _processor = new EthereumTransactionProcessor(
            BlobBaseFeeCalculator.Instance,
            SpecProvider,
            _stateProvider,
            vm,
            codeInfo,
            NullLogManager.Instance);

        _tokenTransferTx = CallTo(TokenAddress);
        _precompileCallsTx = CallTo(PrecompileCallerAddress);
        _warmReadsTx = CallTo(WarmReaderAddress);

        _processor.SetBlockExecutionContext(header);
        AssertSucceeds(_tokenTransferTx);
        AssertSucceeds(_precompileCallsTx);
        AssertSucceeds(_warmReadsTx);
    }

    private static Transaction CallTo(Address contract) => Build.A.Transaction
        .WithTo(contract)
        .WithGasLimit(1_000_000)
        .WithGasPrice(20.GWei)
        .SignedAndResolved(TestItem.PrivateKeyA)
        .TestObject;

    private void AssertSucceeds(Transaction tx)
    {
        CallOutputTracer tracer = new();
        _processor.CallAndRestore(tx, tracer);
        if (tracer.StatusCode != StatusCode.Success)
            throw new InvalidOperationException($"Benchmark transaction to {tx.To} failed: {tracer.Error}");
    }

    [GlobalCleanup]
    public void GlobalCleanup() => _stateScope.Dispose();

    [Benchmark(OperationsPerInvoke = N)]
    public TransactionResult TokenTransfer() => Run(_tokenTransferTx);

    [Benchmark(OperationsPerInvoke = N)]
    public TransactionResult PrecompileCalls() => Run(_precompileCallsTx);

    [Benchmark(OperationsPerInvoke = N)]
    public TransactionResult WarmAccountAndSlotReads() => Run(_warmReadsTx);

    private TransactionResult Run(Transaction tx)
    {
        TransactionResult result = default;
        for (int i = 0; i < N; i++)
            result = _processor.CallAndRestore(tx, NullTxTracer.Instance);
        return result;
    }
}
