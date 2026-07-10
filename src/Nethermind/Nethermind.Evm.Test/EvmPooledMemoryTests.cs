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
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Evm.State;
using Nethermind.Blockchain;
using Nethermind.Core.Test;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class EvmPooledMemoryTests : EvmMemoryTestsBase
{
    [TestCase(32UL, 1UL)]
    [TestCase(0UL, 0UL)]
    [TestCase(33UL, 2UL)]
    [TestCase(64UL, 2UL)]
    [TestCase((ulong)int.MaxValue, (ulong)(int.MaxValue / 32 + 1))]
    public void Div32Ceiling(ulong input, ulong expectedResult)
    {
        ulong result = EvmCalculations.Div32Ceiling(input);
        TestContext.Out.WriteLine($"Memory cost (gas): {result}");
        Assert.That(result, Is.EqualTo(expectedResult));
    }

    private const ulong MaxCodeSize = CodeSizeConstants.MaxCodeSizeEip170;

    [TestCase(0UL, 0UL)]
    [TestCase(0UL, 32UL)]
    [TestCase(0UL, 256UL)]
    [TestCase(0UL, 2048UL)]
    [TestCase(0UL, MaxCodeSize)]
    [TestCase(10UL * MaxCodeSize, MaxCodeSize)]
    [TestCase(100UL * MaxCodeSize, MaxCodeSize)]
    [TestCase(1000UL * MaxCodeSize, MaxCodeSize)]
    [TestCase(0UL, (ulong)MemorySizes.MiB)]
    // Note: Int32.MaxValue was removed as a test case because after word alignment
    // it exceeds the maximum allowed memory size and correctly returns out-of-gas.
    public void MemoryCost(ulong destination, ulong memoryAllocation)
    {
        EvmPooledMemory memory = new();
        UInt256 dest = destination;
        ulong result = memory.CalculateMemoryCost(in dest, memoryAllocation, out bool outOfGas);
        Assert.That(outOfGas, Is.EqualTo(false));
        TestContext.Out.WriteLine($"Gas cost of allocating {memoryAllocation} starting from {dest}: {result}");
    }

    [Test]
    public void CalculateMemoryCost_LocationExceedsULong_ShouldReturnOutOfGas()
    {
        EvmPooledMemory memory = new();
        UInt256 location = new(0, 1, 0, 0); // value larger than ulong max (u1 != 0)
        ulong result = memory.CalculateMemoryCost(in location, 32, out bool outOfGas);
        Assert.That(outOfGas, Is.EqualTo(true));
        Assert.That(result, Is.EqualTo(0UL));
    }

    [Test]
    public void CalculateMemoryCost_LengthExceedsULong_ShouldReturnOutOfGas()
    {
        EvmPooledMemory memory = new();
        UInt256 length = new(0, 1, 0, 0); // value larger than ulong max (u1 != 0)
        ulong result = memory.CalculateMemoryCost(0, in length, out bool outOfGas);
        Assert.That(outOfGas, Is.EqualTo(true));
        Assert.That(result, Is.EqualTo(0UL));
    }

    [TestCase(70 * 1024)]
    [TestCase(2 * 1024 * 1024)]
    public void Large_pooled_buffer_is_zeroed_on_reuse(int size)
    {
        EvmPooledMemory dirty = new();
        UInt256 zero = UInt256.Zero;
        Span<byte> pattern = new byte[size];
        pattern.Fill(0xff);
        Assert.That(dirty.TrySave(in zero, pattern), Is.True);
        dirty.Dispose();

        EvmPooledMemory clean = new();
        UInt256 length = (UInt256)size;
        Assert.That(clean.TryLoadSpan(in zero, in length, out Span<byte> data), Is.True);
        Assert.That(data.Length, Is.EqualTo(size));
        Assert.That(data.IndexOfAnyExcept((byte)0), Is.EqualTo(-1), "pooled buffer leaked stale data");
        clean.Dispose();
    }

    [Test]
    public void CalculateMemoryCost_LengthExceedsLongMax_ShouldReturnOutOfGas()
    {
        EvmPooledMemory memory = new();
        UInt256 length = (UInt256)long.MaxValue + 1; // just over long.MaxValue
        ulong result = memory.CalculateMemoryCost(0, in length, out bool outOfGas);
        Assert.That(outOfGas, Is.EqualTo(true));
        Assert.That(result, Is.EqualTo(0UL));
    }

    [Test]
    public void CalculateMemoryCost_LocationPlusLengthOverflows_ShouldReturnOutOfGas()
    {
        EvmPooledMemory memory = new();
        UInt256 location = ulong.MaxValue;
        ulong result = memory.CalculateMemoryCost(in location, 1, out bool outOfGas);
        Assert.That(outOfGas, Is.EqualTo(true));
        Assert.That(result, Is.EqualTo(0UL));
    }

    [Test]
    public void CalculateMemoryCost_TotalSizeExceedsLongMax_ShouldReturnOutOfGas()
    {
        EvmPooledMemory memory = new();
        UInt256 location = (UInt256)long.MaxValue;
        ulong result = memory.CalculateMemoryCost(in location, 1, out bool outOfGas);
        Assert.That(outOfGas, Is.EqualTo(true));
        Assert.That(result, Is.EqualTo(0UL));
    }

    [Test]
    public void CalculateMemoryCost_TotalSizeExceedsIntMaxAfterWordAlignment_ShouldReturnOutOfGas()
    {
        // Test that memory requests that would overflow int.MaxValue after word alignment
        // are properly rejected. This prevents crashes in .NET array operations.
        // The limit is int.MaxValue - WordSize + 1 to ensure word-aligned size fits in int.
        EvmPooledMemory memory = new();

        // Request exactly at the limit should succeed
        UInt256 maxAllowedSize = (UInt256)(int.MaxValue - EvmPooledMemory.WordSize + 1);
        ulong result = memory.CalculateMemoryCost(0, in maxAllowedSize, out bool outOfGas);
        Assert.That(outOfGas, Is.EqualTo(false), "Size at limit should be allowed");

        // Request one byte over the limit should fail
        UInt256 overLimitSize = maxAllowedSize + 1;
        result = memory.CalculateMemoryCost(0, in overLimitSize, out outOfGas);
        Assert.That(outOfGas, Is.EqualTo(true), "Size over limit should return out of gas");
        Assert.That(result, Is.EqualTo(0UL));
    }

    [Test]
    public void CalculateMemoryCost_MaxAllowedSize_ShouldReturnExpectedCostForBothLengthOverloads()
    {
        decimal maxWords = EvmPooledMemory.MaxMemoryWords;
        ulong expectedCost = decimal.ToUInt64(
            maxWords * GasCostOf.Memory +
            decimal.Floor((maxWords * maxWords) / 512m));

        EvmPooledMemory ulongMemory = new();
        ulong ulongResult = ulongMemory.CalculateMemoryCost(0, EvmPooledMemory.MaxMemorySize, out bool ulongOutOfGas);
        Assert.That(ulongOutOfGas, Is.EqualTo(false));
        Assert.That(ulongResult, Is.EqualTo(expectedCost));

        EvmPooledMemory uint256Memory = new();
        UInt256 maxAllowedSize = (UInt256)EvmPooledMemory.MaxMemorySize;
        ulong uint256Result = uint256Memory.CalculateMemoryCost(0, in maxAllowedSize, out bool uint256OutOfGas);
        Assert.That(uint256OutOfGas, Is.EqualTo(false));
        Assert.That(uint256Result, Is.EqualTo(expectedCost));
    }

    [Test]
    public void CalculateMemoryCost_4GBMemoryRequest_ShouldReturnOutOfGas()
    {
        // Regression test: 4GB memory request (0xffffffff) should return out-of-gas
        // instead of causing integer overflow crash in array operations.
        EvmPooledMemory memory = new();
        UInt256 size4GB = 0xffffffffUL;
        ulong result = memory.CalculateMemoryCost(0, in size4GB, out bool outOfGas);
        Assert.That(outOfGas, Is.EqualTo(true), "4GB memory request should return out of gas");
        Assert.That(result, Is.EqualTo(0UL));
    }

    [Test]
    public void CalculateMemoryCost_LargeOffsetPlusLength_ShouldReturnOutOfGas()
    {
        // Test that location + length exceeding int.MaxValue - WordSize + 1 returns out-of-gas
        EvmPooledMemory memory = new();
        UInt256 location = (UInt256)(int.MaxValue / 2);
        UInt256 length = (UInt256)(int.MaxValue / 2 + 100); // Sum exceeds limit
        ulong result = memory.CalculateMemoryCost(in location, in length, out bool outOfGas);
        Assert.That(outOfGas, Is.EqualTo(true), "Location + length exceeding limit should return out of gas");
        Assert.That(result, Is.EqualTo(0UL));
    }

    [Test]
    public void Save_LocationExceedsULong_ShouldReturnOutOfGas()
    {
        EvmPooledMemory memory = new();
        UInt256 location = new(0, 1, 0, 0);
        bool outOfGas = !memory.TrySave(in location, new byte[32]);
        Assert.That(outOfGas, Is.EqualTo(true));
    }

    [Test]
    public void SaveWord_LocationExceedsULong_ShouldReturnOutOfGas()
    {
        EvmPooledMemory memory = new();
        UInt256 location = new(0, 1, 0, 0);
        bool outOfGas = !memory.TrySaveWord(in location, new byte[32]);
        Assert.That(outOfGas, Is.EqualTo(true));
    }

    [Test]
    public void SaveByte_LocationExceedsULong_ShouldReturnOutOfGas()
    {
        EvmPooledMemory memory = new();
        UInt256 location = new(0, 1, 0, 0);
        bool outOfGas = !memory.TrySaveByte(in location, 0x42);
        Assert.That(outOfGas, Is.EqualTo(true));
    }

    [Test]
    public void LoadSpan_LocationExceedsULong_ShouldReturnOutOfGas()
    {
        EvmPooledMemory memory = new();
        UInt256 location = new(0, 1, 0, 0);
        bool outOfGas = !memory.TryLoadSpan(in location, out Span<byte> result);
        Assert.That(outOfGas, Is.EqualTo(true));
        Assert.That(result.IsEmpty, Is.EqualTo(true));
    }

    [Test]
    public void LoadSpan_LengthExceedsULong_ShouldReturnOutOfGas()
    {
        EvmPooledMemory memory = new();
        UInt256 length = new(0, 1, 0, 0);
        bool outOfGas = !memory.TryLoadSpan(0, in length, out Span<byte> result);
        Assert.That(outOfGas, Is.EqualTo(true));
        Assert.That(result.IsEmpty, Is.EqualTo(true));
    }

    [Test]
    public void Load_LocationExceedsULong_ShouldReturnOutOfGas()
    {
        EvmPooledMemory memory = new();
        UInt256 location = new(0, 1, 0, 0);
        bool outOfGas = !memory.TryLoad(in location, 32, out ReadOnlyMemory<byte> result);
        Assert.That(outOfGas, Is.EqualTo(true));
        Assert.That(result.IsEmpty, Is.EqualTo(true));
    }

    [Test]
    public void Load_LengthExceedsULong_ShouldReturnOutOfGas()
    {
        EvmPooledMemory memory = new();
        UInt256 length = new(0, 1, 0, 0);
        bool outOfGas = !memory.TryLoad(0, in length, out ReadOnlyMemory<byte> result);
        Assert.That(outOfGas, Is.EqualTo(true));
        Assert.That(result.IsEmpty, Is.EqualTo(true));
    }

    [Test]
    public void Inspect_should_not_change_evm_memory()
    {
        EvmPooledMemory memory = new();
        bool outOfGas = !memory.TrySave(3, TestItem.KeccakA.Bytes);
        Assert.That(outOfGas, Is.EqualTo(false));
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
        bool outOfGas = !memory.TrySave((UInt256)offset, expectedKeccakRead);
        Assert.That(outOfGas, Is.EqualTo(false));
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
        bool outOfGas = !memory.TrySave(3, TestItem.KeccakA.Bytes);
        Assert.That(outOfGas, Is.EqualTo(false));
        ulong initialSize = memory.Size;
        outOfGas = !memory.TryLoad(initialSize + 32, 32, out ReadOnlyMemory<byte> result);
        Assert.That(outOfGas, Is.EqualTo(false));
        Assert.That(memory.Size, Is.Not.EqualTo(initialSize));
        Assert.That(result.ToArray(), Is.EqualTo(expectedResult));
    }

    [TestCase(32)]
    [TestCase(64)]
    [TestCase(1024)]
    [TestCase(4096)]
    public void IncrementalGrowth_preserves_written_data_and_zeroes_new_regions(int step)
    {
        EvmPooledMemory memory = new();
        byte[] word = TestItem.KeccakA.BytesToArray();

        Assert.That(memory.TrySaveWord(0, word), Is.True);

        for (int offset = step; offset <= 64 * 1024; offset += step)
        {
            Assert.That(memory.TryLoadSpan((UInt256)offset, (UInt256)EvmPooledMemory.WordSize, out Span<byte> read), Is.True);
            Assert.That(read.ToArray(), Is.EqualTo(new byte[EvmPooledMemory.WordSize]),
                $"memory at offset {offset} must read as zero after growth to {memory.Size}");
        }

        Assert.That(memory.TryLoadSpan(0, (UInt256)EvmPooledMemory.WordSize, out Span<byte> first), Is.True);
        Assert.That(first.ToArray(), Is.EqualTo(word), "originally written word must survive re-rent");
    }

    [TestCase(1024)]
    [TestCase(64 * 1024)]
    [TestCase(256 * 1024)]
    public void Shared_sibling_frame_reuse_reads_zero(int size)
    {
        SharedEvmMemory shared = new();
        UInt256 zero = UInt256.Zero;

        EvmPooledMemory a = new();
        a.AttachShared(shared, 0);
        Span<byte> pattern = new byte[size];
        pattern.Fill(0xff);
        Assert.That(a.TrySave(in zero, pattern), Is.True);
        a.Dispose();

        EvmPooledMemory c = new();
        c.AttachShared(shared, 0);
        UInt256 length = (UInt256)size;
        Assert.That(c.TryLoadSpan(in zero, in length, out Span<byte> data), Is.True);
        Assert.That(data.Length, Is.EqualTo(size));
        Assert.That(data.IndexOfAnyExcept((byte)0), Is.EqualTo(-1), "sibling frame leaked stale data");
        c.Dispose();
    }

    [Test]
    public void Shared_nested_frame_grows_above_parent_and_leaves_it_intact()
    {
        SharedEvmMemory shared = new();
        byte[] word = TestItem.KeccakA.BytesToArray();

        EvmPooledMemory parent = new();
        parent.AttachShared(shared, 0);
        Assert.That(parent.TrySaveWord(0, word), Is.True);
        Span<byte> parentPattern = new byte[2048];
        parentPattern.Fill(0xaa);
        Assert.That(parent.TrySave((UInt256)64, parentPattern), Is.True);

        EvmPooledMemory child = new();
        child.AttachShared(shared, parent.FrameFrontier);
        UInt256 childLength = (UInt256)4096;
        Assert.That(child.TryLoadSpan(0, in childLength, out Span<byte> childData), Is.True);
        Assert.That(childData.IndexOfAnyExcept((byte)0), Is.EqualTo(-1), "child window must read as zero");
        Span<byte> childPattern = new byte[4096];
        childPattern.Fill(0xbb);
        Assert.That(child.TrySave(0, childPattern), Is.True);
        child.Dispose();

        // Parent's window below the child is untouched.
        Assert.That(parent.TryLoadSpan(0, (UInt256)EvmPooledMemory.WordSize, out Span<byte> parentFirst), Is.True);
        Assert.That(parentFirst.ToArray(), Is.EqualTo(word));
        Assert.That(parent.TryLoadSpan((UInt256)64, (UInt256)2048, out Span<byte> parentMid), Is.True);
        Assert.That(parentMid.ToArray(), Is.EqualTo(parentPattern.ToArray()));
        parent.Dispose();
    }

    [Test]
    public void Shared_frame_exceeding_reserve_spills_and_reads_zero()
    {
        SharedEvmMemory shared = new();
        EvmPooledMemory a = new();
        a.AttachShared(shared, SharedEvmMemory.ReserveBytes - 512);

        UInt256 length = (UInt256)4096;
        Assert.That(a.TryLoadSpan(0, in length, out Span<byte> data), Is.True);
        Assert.That(data.Length, Is.EqualTo(4096));
        Assert.That(data.IndexOfAnyExcept((byte)0), Is.EqualTo(-1), "spilled frame must read as zero");

        Span<byte> pattern = new byte[4096];
        pattern.Fill(0x7f);
        Assert.That(a.TrySave(0, pattern), Is.True);
        Assert.That(a.TryLoadSpan(0, in length, out Span<byte> readBack), Is.True);
        Assert.That(readBack.ToArray(), Is.EqualTo(pattern.ToArray()), "spilled frame must preserve writes");
        a.Dispose();
    }

    [Test]
    public void GetTrace_should_not_throw_on_not_initialized_memory()
    {
        EvmPooledMemory memory = new();
        memory.CalculateMemoryCost(0, 32, out bool outOfGas);
        Assert.That(outOfGas, Is.EqualTo(false));
        Assert.That(memory.GetTrace().ToHexWordList(), Is.EqualTo(new string[] { "0000000000000000000000000000000000000000000000000000000000000000" }));
    }

    [Test]
    public void GetTrace_memory_should_not_bleed_between_txs()
    {
        byte[] first = new byte[] {
            0x5b, 0x38, 0x36, 0x59, 0x59, 0x59, 0x59, 0x52, 0x3a, 0x60, 0x05, 0x30,
            0xf4, 0x05, 0x56};
        byte[] second = new byte[] {
            0x5b, 0x36, 0x59, 0x3a, 0x34, 0x60, 0x5b, 0x59, 0x05, 0x30, 0xf4, 0x3a,
            0x56};

        string a = Run(second).ToString();
        Run(first);
        string b = Run(second).ToString();

        Assert.That(b, Is.EqualTo(a));
    }

    [Test]
    public void GetTrace_memory_should_not_overflow()
    {
        byte[] input = new byte[] {
            0x5b, 0x59, 0x60, 0x20, 0x59, 0x81, 0x91, 0x52, 0x44, 0x36, 0x5a, 0x3b,
            0x59, 0xf4, 0x5b, 0x31, 0x56, 0x08};
        Run(input);
    }

    private static readonly PrivateKey PrivateKeyD = new("0000000000000000000000000000000000000000000000000000001000000000");
    private static readonly Address sender = new("0x59ede65f910076f60e07b2aeb189c72348525e72");

    private static readonly Address to = new("0x000000000000000000000000636f6e7472616374");
    private static readonly Address coinbase = new("0x4444588443C3a91288c5002483449Aba1054192b");
    // for testing purposes, particular chain id does not matter. Maybe make random id so it captures the idea that signature should would irrespective of chain
    private static readonly EthereumEcdsa ethereumEcdsa = new(BlockchainIds.GenericNonRealNetwork);
    private static string Run(byte[] input)
    {
        ulong blocknr = 12965000;
        ulong gas = 34218;
        ulong ts = 123456;
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        ISpecProvider specProvider = new TestSpecProvider(London.Instance);
        EthereumCodeInfoRepository codeInfoRepository = new(stateProvider);
        EthereumVirtualMachine virtualMachine = new(
            new TestBlockhashProvider(specProvider),
            specProvider,
            LimboLogs.Instance);
        ITransactionProcessor transactionProcessor = new EthereumTransactionProcessor(
            BlobBaseFeeCalculator.Instance,
            specProvider,
            stateProvider,
            virtualMachine,
            codeInfoRepository,
            LimboLogs.Instance);

        Hash256 stateRoot = null;
        using IDisposable _ = stateProvider.BeginScope(IWorldState.PreGenesis);
        stateProvider.CreateAccount(to, 123);
        stateProvider.InsertCode(to, input, specProvider.GenesisSpec);

        stateProvider.CreateAccount(sender, 40000000);
        stateProvider.Commit(specProvider.GenesisSpec);

        stateProvider.CommitTree(0);
        stateRoot = stateProvider.StateRoot;

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
            WithStateRoot(stateRoot).
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
    public bool IsTracingReturnData { get; } = false;
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

    public void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
    {
    }

    public void MarkAsFailed(Address recipient, in GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null)
    {
    }

    public void StartOperation(int pc, Instruction opcode, ulong gas, in ExecutionEnvironment env)
    {
    }

    public void ReportOperationError(EvmExceptionType error)
    {
    }

    public void ReportOperationRemainingGas(ulong gas)
    {
    }

    public void ReportLog(LogEntry log)
    {
    }

    public void SetOperationStack(TraceStack stack)
    {
    }

    public void SetOperationMemory(TraceMemory memoryTrace) => lastmemline = string.Concat("0x", string.Join("", memoryTrace.ToHexWordList().Select(static mt => mt.Replace("0x", string.Empty))));

    public void SetOperationMemorySize(ulong newSize)
    {
    }

    public void SetOperationReturnData(ReadOnlyMemory<byte> returnData)
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

    public void ReportSelfDestruct(Address address, UInt256 balance, Address refundAddress) => throw new NotSupportedException();

    public void ReportBalanceChange(Address address, UInt256? before, UInt256? after) => throw new NotSupportedException();

    public void ReportCodeChange(Address address, byte[] before, byte[] after) => throw new NotSupportedException();

    public void ReportNonceChange(Address address, UInt256? before, UInt256? after) => throw new NotSupportedException();

    public void ReportAccountRead(Address address) => throw new NotImplementedException();

    public void ReportStorageChange(in StorageCell storageAddress, byte[] before, byte[] after) => throw new NotSupportedException();

    public void ReportStorageRead(in StorageCell storageCell) => throw new NotImplementedException();

    public void ReportAction(ulong gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false) => throw new NotSupportedException();

    public void ReportActionEnd(ulong gas, ReadOnlyMemory<byte> output) => throw new NotSupportedException();

    public void ReportActionError(EvmExceptionType exceptionType) => throw new NotSupportedException();

    public void ReportActionRevert(ulong gas, ReadOnlyMemory<byte> output) => throw new NotSupportedException();

    public void ReportActionEnd(ulong gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode) => throw new NotSupportedException();

    public void ReportBlockHash(Hash256 blockHash) => throw new NotImplementedException();

    public void ReportByteCode(ReadOnlyMemory<byte> byteCode) => throw new NotSupportedException();

    public void ReportGasUpdateForVmTrace(ulong refund, ulong gasAvailable)
    {
    }

    public void ReportRefundForVmTrace(long refund, long gasAvailable)
    {
    }

    public void ReportRefund(long refund)
    {
    }

    public void ReportExtraGasPressure(ulong extraGasPressure) => throw new NotImplementedException();

    public void ReportAccess(IEnumerable<Address> accessedAddresses, IEnumerable<StorageCell> accessedStorageCells) => throw new NotImplementedException();

    public void ReportStackPush(in ReadOnlySpan<byte> stackItem)
    {
    }

    public void ReportFees(UInt256 fees, UInt256 burntFees) => throw new NotImplementedException();

    public void Dispose() { }
}
