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
/// Whole transactions into runtime bytecode deployed on mainnet, so the interpreter runs the opcode mix that compilers
/// emit: selector dispatch, argument checks, storage, logs and calls between contracts.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><see cref="TokenTransferOldCompiler"/>: a token <c>transfer</c> on code from an early compiler.</item>
/// <item><see cref="TokenTransferWithHooks"/>: a token <c>transfer</c> on code from a recent compiler with transfer hooks.</item>
/// <item><see cref="PairSwap"/>: a constant-product pair <c>swap</c>, which calls
/// two tokens running the first token code, updates its reserves and price accumulators and emits its events.</item>
/// </list>
/// Each call runs through <c>CallAndRestore</c>, so the state is the same for every iteration.
/// </remarks>
[Config(typeof(ContractBytecodeConfig))]
[MemoryDiagnoser]
public partial class ContractBytecodeBenchmarks
{
    private class ContractBytecodeConfig : ManualConfig
    {
        public ContractBytecodeConfig() => AddJob(Job.MediumRun.WithToolchain(InProcessNoEmitToolchain.Instance));
    }

    private const int N = 200;

    private static readonly IReleaseSpec Spec = Osaka.Instance;
    private static readonly ISpecProvider SpecProvider = new TestSpecProvider(Osaka.Instance);

    private static readonly Address OldToken = new("0x1000000000000000000000000000000000000001");
    private static readonly Address HookToken = new("0x1000000000000000000000000000000000000002");
    private static readonly Address Token0 = new("0x1000000000000000000000000000000000000003");
    private static readonly Address Token1 = new("0x1000000000000000000000000000000000000004");
    private static readonly Address Pair = new("0x1000000000000000000000000000000000000005");
    private static readonly Address HookTokenRouter = new("0x1000000000000000000000000000000000000006");

    private static readonly UInt256 Reserve = 1_000 * Unit.Ether;
    private static readonly UInt256 AmountIn = 1 * Unit.Ether;
    private static readonly UInt256 AmountOut = 9 * Unit.Ether / 10;

    private IWorldState _stateProvider = null!;
    private IDisposable _stateScope = null!;
    private ITransactionProcessor _processor = null!;
    private Transaction _oldTokenTransfer = null!;
    private Transaction _hookTokenTransfer = null!;
    private Transaction _pairSwap = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _stateProvider = TestWorldStateFactory.CreateForTest(logManager: NullLogManager.Instance);
        _stateScope = _stateProvider.BeginScope(IWorldState.PreGenesis);

        Address sender = TestItem.AddressA;
        _stateProvider.CreateAccount(sender, 10_000.Ether);

        // The early token keeps balances in a mapping at slot 3.
        Deploy(OldToken, TokenCodeOldCompiler);
        _stateProvider.Set(new StorageCell(OldToken, MappingSlot(sender, 3)), 1_000_000 * Unit.Ether);

        // The recent token keeps balances in a mapping at slot 1 and lets transfers through once slot 9 names a pair.
        Deploy(HookToken, TokenCodeWithHooks);
        _stateProvider.Set(new StorageCell(HookToken, MappingSlot(sender, 1)), 1_000_000 * Unit.Ether);
        _stateProvider.Set(new StorageCell(HookToken, 3), 1_000_000 * Unit.Ether);
        _stateProvider.Set(new StorageCell(HookToken, 9), new UInt256(HookTokenRouter.Bytes, isBigEndian: true));

        // The pair holds its reserves of both tokens plus the input of the swap, which the sender sent beforehand.
        Deploy(Token0, TokenCodeOldCompiler);
        Deploy(Token1, TokenCodeOldCompiler);
        Deploy(Pair, PairCode);
        _stateProvider.Set(new StorageCell(Token0, MappingSlot(Pair, 3)), Reserve + AmountIn);
        _stateProvider.Set(new StorageCell(Token1, MappingSlot(Pair, 3)), Reserve);
        _stateProvider.Set(new StorageCell(Pair, 6), new UInt256(Token0.Bytes, isBigEndian: true));
        _stateProvider.Set(new StorageCell(Pair, 7), new UInt256(Token1.Bytes, isBigEndian: true));
        // reserve0 and reserve1 in 112 bits each, then the timestamp of the last update in the top 32.
        _stateProvider.Set(new StorageCell(Pair, 8), Reserve | (Reserve << 112) | (UInt256.One << 224));
        _stateProvider.Set(new StorageCell(Pair, 12), UInt256.One);

        _stateProvider.Commit(Spec);
        _stateProvider.CommitTree(0);

        BlockHeader header = Build.A.BlockHeader
            .WithNumber(1)
            .WithTimestamp(1_700_000_000)
            .WithGasLimit(30_000_000)
            .WithBaseFee(10.GWei)
            .WithStateRoot(_stateProvider.StateRoot)
            .TestObject;

        EthereumCodeInfoRepository codeInfo = new(_stateProvider);
        EthereumVirtualMachine vm = new(new TestBlockhashProvider(), SpecProvider, NullLogManager.Instance);
        _processor = new EthereumTransactionProcessor(BlobBaseFeeCalculator.Instance, SpecProvider, _stateProvider, vm, codeInfo, NullLogManager.Instance);
        _processor.SetBlockExecutionContext(header);

        byte[] transfer = Bytes.Concat(Bytes.FromHexString("a9059cbb"), Word(TestItem.AddressB), Word(12_345 * Unit.Ether));
        _oldTokenTransfer = Call(OldToken, transfer);
        _hookTokenTransfer = Call(HookToken, transfer);
        byte[] swap = Bytes.Concat(Bytes.FromHexString("022c0d9f"), Word(UInt256.Zero), Word(AmountOut), Word(TestItem.AddressB), Word(128), Word(UInt256.Zero));
        _pairSwap = Call(Pair, swap);

        EnsureSucceeds(_oldTokenTransfer);
        EnsureSucceeds(_hookTokenTransfer);
        EnsureSucceeds(_pairSwap);
    }

    [GlobalCleanup]
    public void GlobalCleanup() => _stateScope.Dispose();

    [Benchmark(OperationsPerInvoke = N)]
    public TransactionResult TokenTransferOldCompiler() => Run(_oldTokenTransfer);

    [Benchmark(OperationsPerInvoke = N)]
    public TransactionResult TokenTransferWithHooks() => Run(_hookTokenTransfer);

    [Benchmark(OperationsPerInvoke = N)]
    public TransactionResult PairSwap() => Run(_pairSwap);

    private TransactionResult Run(Transaction transaction)
    {
        TransactionResult result = default;
        for (int i = 0; i < N; i++)
            result = _processor.CallAndRestore(transaction, NullTxTracer.Instance);
        return result;
    }

    private void Deploy(Address address, string code)
    {
        _stateProvider.CreateAccount(address, UInt256.Zero);
        _stateProvider.InsertCode(address, Bytes.FromHexString(code), Spec);
    }

    private void EnsureSucceeds(Transaction transaction)
    {
        CallOutputTracer tracer = new();
        _processor.CallAndRestore(transaction, tracer);
        if (tracer.StatusCode != StatusCode.Success)
            throw new InvalidOperationException($"Benchmark call to {transaction.To} failed: {tracer.Error}");
    }

    private static Transaction Call(Address to, byte[] data) => Build.A.Transaction
        .WithTo(to)
        .WithData(data)
        // The test builder sends one wei by default, which every non-payable function rejects.
        .WithValue(UInt256.Zero)
        .WithGasLimit(300_000)
        .WithGasPrice(20.GWei)
        .SignedAndResolved(TestItem.PrivateKeyA)
        .TestObject;

    /// <summary>The storage slot of <paramref name="key"/> in a mapping declared at <paramref name="slot"/>.</summary>
    private static UInt256 MappingSlot(Address key, int slot) =>
        new(Keccak.Compute(Bytes.Concat(Word(key), Word(slot))).Bytes, isBigEndian: true);

    private static byte[] Word(Address address) => address.Bytes.PadLeft(32);

    private static byte[] Word(in UInt256 value) => value.ToBigEndian();

    private static byte[] Word(int value) => Word((UInt256)value);
}
