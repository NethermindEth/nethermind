// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class VirtualMachineTestsBase
{
    protected const string SampleHexData1 = "a01234";
    protected const string SampleHexData2 = "b15678";
    protected const string HexZero = "00";
    protected const long DefaultBlockGasLimit = 8000000;

    private IEthereumEcdsa _ethereumEcdsa;
    protected ITransactionProcessor _processor;
    private IDb _stateDb;

    protected VirtualMachine Machine { get; private set; }
    protected CodeInfoRepository CodeInfoRepository { get; private set; }
    protected IWorldState TestState { get; private set; }
    protected static Address Contract { get; } = new("0xd75a3a95360e44a3874e691fb48d77855f127069");
    protected static Address Sender { get; } = TestItem.AddressA;
    protected static Address Recipient { get; } = TestItem.AddressB;
    protected static Address Miner { get; } = TestItem.AddressD;

    protected static PrivateKey SenderKey { get; } = TestItem.PrivateKeyA;
    protected static PrivateKey RecipientKey { get; } = TestItem.PrivateKeyB;
    protected static PrivateKey MinerKey { get; } = TestItem.PrivateKeyD;

    protected virtual ForkActivation Activation => (BlockNumber, Timestamp);
    protected virtual long BlockNumber { get; } = MainnetSpecProvider.ByzantiumBlockNumber;
    protected virtual ulong Timestamp => 0UL;
    protected virtual ISpecProvider SpecProvider => MainnetSpecProvider.Instance;
    protected IReleaseSpec Spec => SpecProvider.GetSpec(Activation);

    protected virtual ILogManager GetLogManager()
    {
        return LimboLogs.Instance;
    }

    [SetUp]
    public virtual void Setup()
    {
        ILogManager logManager = GetLogManager();

        IDb codeDb = new MemDb();
        _stateDb = new MemDb();
        ITrieStore trieStore = new TrieStore(_stateDb, logManager);
        TestState = new WorldState(trieStore, codeDb, logManager);
        _ethereumEcdsa = new EthereumEcdsa(SpecProvider.ChainId, logManager);
        IBlockhashProvider blockhashProvider = new TestBlockhashProvider(SpecProvider);
        CodeInfoRepository = new CodeInfoRepository();
        Machine = new VirtualMachine(blockhashProvider, SpecProvider, CodeInfoRepository, logManager);
        _processor = new TransactionProcessor(SpecProvider, TestState, Machine, CodeInfoRepository, logManager);
    }

    [TearDown]
    public virtual void TearDown() => _stateDb?.Dispose();

    protected GethLikeTxTrace ExecuteAndTrace(params byte[] code)
    {
        GethLikeTxMemoryTracer tracer = new(GethTraceOptions.Default with { EnableMemory = true });
        (Block block, Transaction transaction) = PrepareTx(Activation, 100000, code);
        _processor.Execute(transaction, block.Header, tracer);
        return tracer.BuildResult();
    }

    protected GethLikeTxTrace ExecuteAndTrace(long blockNumber, long gasLimit, params byte[] code)
    {
        GethLikeTxMemoryTracer tracer = new(GethTraceOptions.Default);
        (Block block, Transaction transaction) = PrepareTx((blockNumber, Timestamp), gasLimit, code);
        _processor.Execute(transaction, block.Header, tracer);
        return tracer.BuildResult();
    }

    protected GethLikeTxTrace ExecuteAndTraceToFile(Action<GethTxFileTraceEntry> dumpCallback, byte[] code, GethTraceOptions options)
    {
        GethLikeTxFileTracer tracer = new(dumpCallback, options);
        (Block block, Transaction transaction) = PrepareTx(Activation, 100000, code);
        _processor.Execute(transaction, block.Header, tracer);
        return tracer.BuildResult();
    }

    /// <summary>
    /// deprecated. Please use activation instead of blockNumber.
    /// </summary>
    protected TestAllTracerWithOutput Execute(long blockNumber, params byte[] code)
    {
        return Execute((blockNumber, Timestamp), code);
    }

    protected TestAllTracerWithOutput Execute(ForkActivation activation, params byte[] code)
    {
        (Block block, Transaction transaction) = PrepareTx(activation, 100000, code);
        TestAllTracerWithOutput tracer = CreateTracer();
        _processor.Execute(transaction, block.Header, tracer);
        return tracer;
    }

    protected TestAllTracerWithOutput Execute(params byte[] code)
    {
        return Execute(Activation, code);
    }

    protected virtual TestAllTracerWithOutput CreateTracer() => new();


    protected T ExecuteBlock<T>(T tracer, byte[] code, ForkActivation? forkActivation = null) where T : IBlockTracer
    {
        (Block block, Transaction transaction) = PrepareTx(forkActivation ?? Activation, 100000, code);
        tracer.StartNewBlockTrace(block);
        ITxTracer txTracer = tracer.StartNewTxTrace(transaction);
        _processor.Execute(transaction, block.Header, txTracer);
        tracer.EndTxTrace();
        tracer.EndBlockTrace();
        return tracer;
    }

    protected T Execute<T>(T tracer, byte[] code, ForkActivation? forkActivation = null) where T : ITxTracer
    {
        (Block block, Transaction transaction) = PrepareTx(forkActivation ?? Activation, 100000, code);
        _processor.Execute(transaction, block.Header, tracer);
        return tracer;
    }

    /// <summary>
    /// deprecated. Please use activation instead of blockNumber.
    /// </summary>
    protected TestAllTracerWithOutput Execute(long blockNumber, long gasLimit, byte[] code,
        long blockGasLimit = DefaultBlockGasLimit, byte[][] blobVersionedHashes = null)
    {
        (Block block, Transaction transaction) = PrepareTx((blockNumber, Timestamp), gasLimit, code,
            blockGasLimit: blockGasLimit, blobVersionedHashes: blobVersionedHashes);
        TestAllTracerWithOutput tracer = CreateTracer();
        _processor.Execute(transaction, block.Header, tracer);
        return tracer;
    }

    protected TestAllTracerWithOutput Execute(ForkActivation activation, long gasLimit, byte[] code,
        long blockGasLimit = DefaultBlockGasLimit, byte[][] blobVersionedHashes = null)
    {
        (Block block, Transaction transaction) = PrepareTx(activation, gasLimit, code,
            blockGasLimit: blockGasLimit, blobVersionedHashes: blobVersionedHashes);
        TestAllTracerWithOutput tracer = CreateTracer();
        _processor.Execute(transaction, block.Header, tracer);
        return tracer;
    }

    /// <summary>
    /// deprecated. Please use activation instead of blockNumber.
    /// </summary>
    protected (Block block, Transaction transaction) PrepareTx(
        long blockNumber,
        long gasLimit,
        byte[]? code = null,
        SenderRecipientAndMiner? senderRecipientAndMiner = null,
        int value = 1,
        long blockGasLimit = DefaultBlockGasLimit,
        byte[][]? blobVersionedHashes = null,
        ulong excessBlobGas = 0)
    {
        return PrepareTx((blockNumber, Timestamp), gasLimit, code, senderRecipientAndMiner, value, blockGasLimit, blobVersionedHashes, excessBlobGas);
    }

    protected (Block block, Transaction transaction) PrepareTx(
        ForkActivation activation,
        long gasLimit,
        byte[]? code = null,
        SenderRecipientAndMiner? senderRecipientAndMiner = null,
        int value = 1,
        long blockGasLimit = DefaultBlockGasLimit,
        byte[][]? blobVersionedHashes = null,
        ulong excessBlobGas = 0)
    {
        senderRecipientAndMiner ??= SenderRecipientAndMiner.Default;

        // checking if account exists - because creating new accounts overwrites already existing accounts,
        // thus overwriting storage roots - essentially clearing the storage slots
        // earlier it used to work - because the cache mapping address:storageTree was never cleared on account of
        // TestState.CommitTrees() not being called. But now the WorldState.CommitTrees which also calls TestState.CommitTrees, clearing the cache.
        if (!TestState.AccountExists(senderRecipientAndMiner.Sender))
            TestState.CreateAccount(senderRecipientAndMiner.Sender, 100.Ether());
        else
            TestState.AddToBalance(senderRecipientAndMiner.Sender, 100.Ether(), SpecProvider.GenesisSpec);

        if (!TestState.AccountExists(senderRecipientAndMiner.Recipient))
            TestState.CreateAccount(senderRecipientAndMiner.Recipient, 100.Ether());
        else
            TestState.AddToBalance(senderRecipientAndMiner.Recipient, 100.Ether(), SpecProvider.GenesisSpec);

        if (code is not null)
        {
            TestState.InsertCode(senderRecipientAndMiner.Recipient, code, SpecProvider.GenesisSpec);
        }

        GetLogManager().GetClassLogger().Debug("Committing initial state");
        TestState.Commit(SpecProvider.GenesisSpec);
        GetLogManager().GetClassLogger().Debug("Committed initial state");
        GetLogManager().GetClassLogger().Debug("Committing initial tree");
        TestState.CommitTree(0);
        GetLogManager().GetClassLogger().Debug("Committed initial tree");

        Transaction transaction = Build.A.Transaction
            .WithGasLimit(gasLimit)
            .WithGasPrice(1)
            .WithValue(value)
            .WithBlobVersionedHashes(blobVersionedHashes)
            .WithNonce(TestState.GetNonce(senderRecipientAndMiner.Sender))
            .To(senderRecipientAndMiner.Recipient)
            .SignedAndResolved(_ethereumEcdsa, senderRecipientAndMiner.SenderKey)
            .TestObject;

        Block block = BuildBlock(activation, senderRecipientAndMiner, transaction, blockGasLimit, excessBlobGas);
        return (block, transaction);
    }

    /// <summary>
    /// deprecated. Please use activation instead of blockNumber.
    /// </summary>
    protected (Block block, Transaction transaction) PrepareTx(long blockNumber, long gasLimit, byte[] code,
        byte[] input, UInt256 value, SenderRecipientAndMiner senderRecipientAndMiner = null)
    {
        return PrepareTx((blockNumber, Timestamp), gasLimit, code, input, value, senderRecipientAndMiner);
    }

    protected (Block block, Transaction transaction) PrepareTx(ForkActivation activation, long gasLimit, byte[] code,
        byte[] input, UInt256 value, SenderRecipientAndMiner senderRecipientAndMiner = null)
    {
        senderRecipientAndMiner ??= SenderRecipientAndMiner.Default;

        // checking if account exists - because creating new accounts overwrites already existing accounts,
        // thus overwriting storage roots - essentially clearing the storage slots
        // earlier it used to work - because the cache mapping address:storageTree was never cleared on account of
        // TestState.CommitTrees() not being called. But now the WorldState.CommitTrees which also calls TestState.CommitTrees, clearing the cache.
        if (!TestState.AccountExists(senderRecipientAndMiner.Sender))
            TestState.CreateAccount(senderRecipientAndMiner.Sender, 100.Ether());
        else
            TestState.AddToBalance(senderRecipientAndMiner.Sender, 100.Ether(), SpecProvider.GenesisSpec);

        if (!TestState.AccountExists(senderRecipientAndMiner.Recipient))
            TestState.CreateAccount(senderRecipientAndMiner.Recipient, 100.Ether());
        else
            TestState.AddToBalance(senderRecipientAndMiner.Recipient, 100.Ether(), SpecProvider.GenesisSpec);
        TestState.InsertCode(senderRecipientAndMiner.Recipient, code, SpecProvider.GenesisSpec);

        TestState.Commit(SpecProvider.GenesisSpec);

        Transaction transaction = Build.A.Transaction
            .WithGasLimit(gasLimit)
            .WithGasPrice(1)
            .WithNonce(TestState.GetNonce(senderRecipientAndMiner.Sender))
            .WithData(input)
            .WithValue(value)
            .To(senderRecipientAndMiner.Recipient)
            .SignedAndResolved(_ethereumEcdsa, senderRecipientAndMiner.SenderKey)
            .TestObject;

        Block block = BuildBlock(activation, senderRecipientAndMiner);
        return (block, transaction);
    }

    protected (Block block, Transaction transaction) PrepareInitTx(ForkActivation activation, long gasLimit, byte[] code,
        SenderRecipientAndMiner senderRecipientAndMiner = null)
    {
        senderRecipientAndMiner ??= SenderRecipientAndMiner.Default;
        TestState.CreateAccount(senderRecipientAndMiner.Sender, 100.Ether());
        TestState.Commit(SpecProvider.GenesisSpec);

        Transaction transaction = Build.A.Transaction
            .WithTo(null)
            .WithGasLimit(gasLimit)
            .WithGasPrice(1)
            .WithCode(code)
            .SignedAndResolved(_ethereumEcdsa, senderRecipientAndMiner.SenderKey)
            .TestObject;

        Block block = BuildBlock(activation, senderRecipientAndMiner);
        return (block, transaction);
    }

    protected Block BuildBlock(ForkActivation activation, SenderRecipientAndMiner senderRecipientAndMiner)
    {
        return BuildBlock(activation, senderRecipientAndMiner, null);
    }

    protected virtual Block BuildBlock(ForkActivation activation, SenderRecipientAndMiner senderRecipientAndMiner,
        Transaction tx, long blockGasLimit = DefaultBlockGasLimit, ulong excessBlobGas = 0)
    {
        senderRecipientAndMiner ??= SenderRecipientAndMiner.Default;
        return Build.A.Block.WithNumber(activation.BlockNumber)
            .WithTimestamp(activation.Timestamp ?? 0)
            .WithTransactions(tx is null ? Array.Empty<Transaction>() : new[] { tx })
            .WithGasLimit(blockGasLimit)
            .WithBeneficiary(senderRecipientAndMiner.Miner)
            .WithBlobGasUsed(0)
            .WithExcessBlobGas(0)
            .WithParentBeaconBlockRoot(TestItem.KeccakG)
            .WithExcessBlobGas(excessBlobGas)
            .TestObject;
    }

    protected void AssertGas(TestAllTracerWithOutput receipt, long gas)
    {
        Assert.That(receipt.GasSpent, Is.EqualTo(gas), "gas");
    }

    protected void AssertStorage(UInt256 address, Address value)
    {
        Assert.That(TestState.Get(new StorageCell(Recipient, address)).PadLeft(32), Is.EqualTo(value.Bytes.PadLeft(32)), "storage");
    }

    protected void AssertStorage(UInt256 address, Hash256 value)
    {
        Assert.That(TestState.Get(new StorageCell(Recipient, address)).PadLeft(32), Is.EqualTo(value.BytesToArray()), "storage");
    }

    protected void AssertStorage(UInt256 address, ReadOnlySpan<byte> value)
    {
        Assert.That(TestState.Get(new StorageCell(Recipient, address)).PadLeft(32), Is.EqualTo(new ZeroPaddedSpan(value, 32 - value.Length, PadDirection.Left).ToArray()), "storage");
    }

    protected void AssertStorage(UInt256 address, BigInteger expectedValue)
    {
        byte[] actualValue = TestState.Get(new StorageCell(Recipient, address)).ToArray();
        byte[] expected = expectedValue < 0 ? expectedValue.ToBigEndianByteArray(32) : expectedValue.ToBigEndianByteArray();
        Assert.That(actualValue, Is.EqualTo(expected), "storage");
    }

    protected void AssertStorage(UInt256 address, UInt256 expectedValue)
    {
        byte[] bytes = ((BigInteger)expectedValue).ToBigEndianByteArray();

        byte[] actualValue = TestState.Get(new StorageCell(Recipient, address)).ToArray();
        Assert.That(actualValue, Is.EqualTo(bytes), "storage");
    }

    private static int _callIndex = -1;

    protected void AssertStorage(StorageCell storageCell, UInt256 expectedValue)
    {
        _callIndex++;
        if (!TestState.AccountExists(storageCell.Address))
        {
            Assert.That(new byte[] { 0 }, Is.EqualTo(expectedValue.ToBigEndian().WithoutLeadingZeros().ToArray()), $"storage {storageCell}, call {_callIndex}");
        }
        else
        {
            byte[] actualValue = TestState.Get(storageCell).ToArray();
            Assert.That(actualValue, Is.EqualTo(expectedValue.ToBigEndian().WithoutLeadingZeros().ToArray()), $"storage {storageCell}, call {_callIndex}");
        }
    }

    protected void AssertCodeHash(Address address, Hash256 codeHash)
    {
        Assert.That(TestState.GetCodeHash(address), Is.EqualTo(codeHash), "code hash");
    }
}
