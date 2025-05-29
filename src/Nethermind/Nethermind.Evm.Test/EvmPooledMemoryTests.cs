// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using FluentAssertions;
using Nethermind.Core.Test;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class EvmPooledMemoryTests : EvmMemoryTestsBase
{
    protected override IEvmMemory CreateEvmMemory()
    {
        return new EvmPooledMemory();
    }

    [TestCase(32, 1)]
    [TestCase(0, 0)]
    [TestCase(33, 2)]
    [TestCase(64, 2)]
    [TestCase(int.MaxValue, int.MaxValue / 32 + 1)]
    public void Div32Ceiling(int input, int expectedResult)
    {
        long result = EvmPooledMemory.Div32Ceiling((ulong)input);
        TestContext.Out.WriteLine($"Memory cost (gas): {result}");
        Assert.That(result, Is.EqualTo(expectedResult));
    }

    private const int MaxCodeSize = 24576;

    [TestCase(0, 0)]
    [TestCase(0, 32)]
    [TestCase(0, 256)]
    [TestCase(0, 2048)]
    [TestCase(0, MaxCodeSize)]
    [TestCase(10 * MaxCodeSize, MaxCodeSize)]
    [TestCase(100 * MaxCodeSize, MaxCodeSize)]
    [TestCase(1000 * MaxCodeSize, MaxCodeSize)]
    [TestCase(0, 1024 * 1024)]
    [TestCase(0, Int32.MaxValue)]
    public void MemoryCost(int destination, int memoryAllocation)
    {
        EvmPooledMemory memory = new();
        UInt256 dest = (UInt256)destination;
        long result = memory.CalculateMemoryCost(in dest, (UInt256)memoryAllocation);
        TestContext.Out.WriteLine($"Gas cost of allocating {memoryAllocation} starting from {dest}: {result}");
    }

    [Test]
    public void Inspect_should_not_change_evm_memory()
    {
        EvmPooledMemory memory = new();
        memory.Save(3, TestItem.KeccakA.Bytes);
        ulong initialSize = memory.Size;
        ReadOnlyMemory<byte> result = memory.Inspect(initialSize + 32, 32);
        Assert.That(memory.Size, Is.EqualTo(initialSize));
        Assert.That(result, Is.EqualTo(ReadOnlyMemory<byte>.Empty));
    }

    [Test]
    public void Inspect_can_read_memory()
    {
        const int offset = 3;
        byte[] expectedEmptyRead = new byte[32 - offset];
        byte[] expectedKeccakRead = TestItem.KeccakA.BytesToArray();
        EvmPooledMemory memory = new();
        memory.Save((UInt256)offset, expectedKeccakRead);
        ulong initialSize = memory.Size;
        ReadOnlyMemory<byte> actualKeccakMemoryRead = memory.Inspect((UInt256)offset, 32);
        ReadOnlyMemory<byte> actualEmptyRead = memory.Inspect(32 + (UInt256)offset, 32 - (UInt256)offset);
        Assert.That(memory.Size, Is.EqualTo(initialSize));
        Assert.That(actualKeccakMemoryRead.ToArray(), Is.EqualTo(expectedKeccakRead));
        Assert.That(actualEmptyRead.ToArray(), Is.EqualTo(expectedEmptyRead));
    }

    [Test]
    public void Load_should_update_size_of_memory()
    {
        byte[] expectedResult = new byte[32];
        EvmPooledMemory memory = new();
        memory.Save(3, TestItem.KeccakA.Bytes);
        ulong initialSize = memory.Size;
        ReadOnlyMemory<byte> result = memory.Load(initialSize + 32, 32);
        Assert.That(memory.Size, Is.Not.EqualTo(initialSize));
        Assert.That(result.ToArray(), Is.EqualTo(expectedResult));
    }

    [Test]
    public void GetTrace_should_not_throw_on_not_initialized_memory()
    {
        EvmPooledMemory memory = new();
        memory.CalculateMemoryCost(0, 32);
        memory.GetTrace().ToHexWordList().Should().BeEquivalentTo(new string[] { "0000000000000000000000000000000000000000000000000000000000000000" });
    }

    [Test]
    public void GetTrace_memory_should_not_bleed_between_txs()
    {
        var first = new byte[] {
            0x5b, 0x38, 0x36, 0x59, 0x59, 0x59, 0x59, 0x52, 0x3a, 0x60, 0x05, 0x30,
            0xf4, 0x05, 0x56};
        var second = new byte[] {
            0x5b, 0x36, 0x59, 0x3a, 0x34, 0x60, 0x5b, 0x59, 0x05, 0x30, 0xf4, 0x3a,
            0x56};

        var a = Run(second).ToString();
        Run(first);
        var b = Run(second).ToString();

        Assert.That(b, Is.EqualTo(a));
    }

    [Test]
    public void GetTrace_memory_should_not_overflow()
    {
        var input = new byte[] {
            0x5b, 0x59, 0x60, 0x20, 0x59, 0x81, 0x91, 0x52, 0x44, 0x36, 0x5a, 0x3b,
            0x59, 0xf4, 0x5b, 0x31, 0x56, 0x08};
        Run(input);
    }

    private static readonly PrivateKey PrivateKeyD = new("0000000000000000000000000000000000000000000000000000001000000000");
    private static readonly Address sender = new Address("0x59ede65f910076f60e07b2aeb189c72348525e72");

    private static readonly Address to = new Address("0x000000000000000000000000636f6e7472616374");
    private static readonly Address coinbase = new Address("0x4444588443C3a91288c5002483449Aba1054192b");
    // for testing purposes, particular chain id does not matter. Maybe make random id so it captures the idea that signature should would irrespective of chain
    private static readonly EthereumEcdsa ethereumEcdsa = new(BlockchainIds.GenericNonRealNetwork);
    private static string Run(byte[] input)
    {
        long blocknr = 12965000;
        long gas = 34218;
        ulong ts = 123456;
        MemDb stateDb = new();
        TrieStore trieStore = TestTrieStoreFactory.Build(stateDb,
            LimboLogs.Instance);
        IWorldState stateProvider = new WorldState(
                trieStore,
                new MemDb(),
                LimboLogs.Instance);
        ISpecProvider specProvider = new TestSpecProvider(London.Instance);
        CodeInfoRepository codeInfoRepository = new();
        VirtualMachine virtualMachine = new(
            new TestBlockhashProvider(specProvider),
                specProvider,
                LimboLogs.Instance);
        TransactionProcessor transactionProcessor = new TransactionProcessor(
                specProvider,
                stateProvider,
                virtualMachine,
                codeInfoRepository,
                LimboLogs.Instance);

        stateProvider.CreateAccount(to, 123);
        stateProvider.InsertCode(to, input, specProvider.GenesisSpec);

        stateProvider.CreateAccount(sender, 40000000);
        stateProvider.Commit(specProvider.GenesisSpec);

        stateProvider.CommitTree(0);

        Transaction tx = Build.A.Transaction.
            WithData(input).
            WithTo(to).
            WithGasLimit(gas).
            WithGasPrice(0).
            WithValue(0).
            SignedAndResolved(ethereumEcdsa, PrivateKeyD, true).
            TestObject;
        Block block = Build.A.Block.
            WithBeneficiary(coinbase).
            WithNumber(blocknr + 1).
            WithTimestamp(ts).
            WithTransactions(tx).
            WithGasLimit(30000000).
            WithDifficulty(0).
            TestObject;
        MyTracer tracer = new();
        transactionProcessor.Execute(
                tx,
                new BlockExecutionContext(block.Header, specProvider.GetSpec(block.Header)),
                tracer);
        return tracer.lastmemline;
    }
}

public class MyTracer : ITxTracer, IDisposable
{
    public bool IsTracingReceipt => true;
    public bool IsTracingActions => false;
    public bool IsTracingOpLevelStorage => true;
    public bool IsTracingMemory => true;
    public bool IsTracingDetailedMemory { get; set; } = true;
    public bool IsTracingInstructions => true;
    public bool IsTracingRefunds { get; } = false;
    public bool IsTracingCode => true;
    public bool IsTracingStack { get; set; } = true;
    public bool IsTracingState => false;
    public bool IsTracingStorage => false;
    public bool IsTracingBlockHash { get; } = false;
    public bool IsTracingAccess { get; } = false;
    public bool IsTracingFees => false;
    public bool IsTracingLogs => false;
    public bool IsTracing => IsTracingReceipt
                             || IsTracingActions
                             || IsTracingOpLevelStorage
                             || IsTracingMemory
                             || IsTracingInstructions
                             || IsTracingRefunds
                             || IsTracingCode
                             || IsTracingStack
                             || IsTracingBlockHash
                             || IsTracingAccess
                             || IsTracingFees
                             || IsTracingLogs;

    public string lastmemline;

    public void MarkAsSuccess(Address recipient, GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
    {
    }

    public void MarkAsFailed(Address recipient, GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null)
    {
    }

    public void StartOperation(int pc, Instruction opcode, long gas, in ExecutionEnvironment env, int codeSection = 0, int functionDepth = 0)
    {
    }

    public void ReportOperationError(EvmExceptionType error)
    {
    }

    public void ReportOperationRemainingGas(long gas)
    {
    }

    public void ReportLog(LogEntry log)
    {
    }

    public void SetOperationStack(TraceStack stack)
    {
    }

    public void SetOperationMemory(TraceMemory memoryTrace)
    {
        lastmemline = string.Concat("0x", string.Join("", memoryTrace.ToHexWordList().Select(static mt => mt.Replace("0x", string.Empty))));
    }

    public void SetOperationMemorySize(ulong newSize)
    {
    }

    public void ReportMemoryChange(long offset, in ReadOnlySpan<byte> data)
    {
    }

    public void ReportStorageChange(in ReadOnlySpan<byte> key, in ReadOnlySpan<byte> value)
    {
    }

    public void SetOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> newValue, ReadOnlySpan<byte> currentValue)
    {
    }

    public void LoadOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> value)
    {
    }

    public void ReportSelfDestruct(Address address, UInt256 balance, Address refundAddress)
    {
        throw new NotSupportedException();
    }

    public void ReportBalanceChange(Address address, UInt256? before, UInt256? after)
    {
        throw new NotSupportedException();
    }

    public void ReportCodeChange(Address address, byte[] before, byte[] after)
    {
        throw new NotSupportedException();
    }

    public void ReportNonceChange(Address address, UInt256? before, UInt256? after)
    {
        throw new NotSupportedException();
    }

    public void ReportAccountRead(Address address)
    {
        throw new NotImplementedException();
    }

    public void ReportStorageChange(in StorageCell storageAddress, byte[] before, byte[] after)
    {
        throw new NotSupportedException();
    }

    public void ReportStorageRead(in StorageCell storageCell)
    {
        throw new NotImplementedException();
    }

    public void ReportAction(long gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
    {
        throw new NotSupportedException();
    }

    public void ReportActionEnd(long gas, ReadOnlyMemory<byte> output)
    {
        throw new NotSupportedException();
    }

    public void ReportActionError(EvmExceptionType exceptionType)
    {
        throw new NotSupportedException();
    }

    public void ReportActionRevert(long gas, ReadOnlyMemory<byte> output)
    {
        throw new NotSupportedException();
    }

    public void ReportActionEnd(long gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode)
    {
        throw new NotSupportedException();
    }

    public void ReportBlockHash(Hash256 blockHash)
    {
        throw new NotImplementedException();
    }

    public void ReportByteCode(ReadOnlyMemory<byte> byteCode)
    {
        throw new NotSupportedException();
    }

    public void ReportGasUpdateForVmTrace(long refund, long gasAvailable)
    {
    }

    public void ReportRefundForVmTrace(long refund, long gasAvailable)
    {
    }

    public void ReportRefund(long refund)
    {
    }

    public void ReportExtraGasPressure(long extraGasPressure)
    {
        throw new NotImplementedException();
    }

    public void ReportAccess(IReadOnlyCollection<Address> accessedAddresses, IReadOnlyCollection<StorageCell> accessedStorageCells)
    {
        throw new NotImplementedException();
    }

    public void ReportStackPush(in ReadOnlySpan<byte> stackItem)
    {
    }

    public void ReportFees(UInt256 fees, UInt256 burntFees)
    {
        throw new NotImplementedException();
    }

    public void Dispose() { }
}
