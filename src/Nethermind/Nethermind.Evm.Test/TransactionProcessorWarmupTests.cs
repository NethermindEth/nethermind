// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class TransactionProcessorWarmupTests
{
    private ISpecProvider _specProvider = null!;
    private IEthereumEcdsa _ethereumEcdsa = null!;
    private ITransactionProcessor _transactionProcessor = null!;
    private IWorldState _stateProvider = null!;
    private IDisposable _worldStateCloser = null!;

    [SetUp]
    public void Setup()
    {
        _specProvider = new TestSpecProvider(Prague.Instance);
        _stateProvider = TestWorldStateFactory.CreateForTest();
        _worldStateCloser = _stateProvider.BeginScope(IWorldState.PreGenesis);
        EthereumCodeInfoRepository codeInfoRepository = new(_stateProvider);
        EthereumVirtualMachine virtualMachine = new(new TestBlockhashProvider(_specProvider), _specProvider, LimboLogs.Instance);
        _transactionProcessor = new EthereumTransactionProcessor(BlobBaseFeeCalculator.Instance, _specProvider, _stateProvider, virtualMachine, codeInfoRepository, LimboLogs.Instance);
        _ethereumEcdsa = new EthereumEcdsa(_specProvider.ChainId);
    }

    [TearDown]
    public void TearDown() => _worldStateCloser?.Dispose();

    // Warmup must take the real execution path: no-op fee/nonce handling made same-sender
    // warm sequences run with undebited balances and unbumped nonces, so deploy chains
    // computed wrong CREATE addresses and warmed the wrong state.
    [Test]
    public void Warmup_InTheThrowawayScope_DebitsFeesAndBumpsTheNonce()
    {
        _stateProvider.CreateAccount(TestItem.AddressA, 1.Ether);
        _stateProvider.Commit(_specProvider.GenesisSpec);
        _stateProvider.CommitTree(0);

        Transaction tx = Build.A.Transaction
            .WithGasPrice(1)
            .WithMaxFeePerGas(1)
            .WithTo(TestItem.AddressB)
            .WithValue(100.GWei)
            .WithGasLimit(100_000)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
            .TestObject;
        Block block = Build.A.Block
            .WithNumber(long.MaxValue)
            .WithTimestamp(MainnetSpecProvider.PragueBlockTimestamp)
            .WithTransactions(tx)
            .WithGasLimit(10_000_000)
            .TestObject;
        UInt256 balanceBefore = _stateProvider.GetBalance(TestItem.AddressA);

        _transactionProcessor.SetBlockExecutionContext(new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)));
        TransactionResult result = _transactionProcessor.Warmup(tx, NullTxTracer.Instance);

        Assert.That(result.TransactionExecuted, Is.True, "precondition: the warm execution ran");
        Assert.That(_stateProvider.GetNonce(TestItem.AddressA), Is.EqualTo(1UL),
            "a warm execution must bump the nonce so a same-sender successor warms the right state");
        Assert.That(_stateProvider.GetBalance(TestItem.AddressA), Is.EqualTo(balanceBefore - 100.GWei - 21_000),
            "a warm execution must debit value and gas so successors see real balances");
        Assert.That(tx.BlockGasUsed, Is.EqualTo(100_000UL),
            "warmup must never mutate the shared transaction object: the getter must still fall back to the gas limit");
    }

    // A sender funded earlier in the block by another sender's transaction has no balance in
    // the parent state; per-sender warm groups cannot see that funding, so the warm pass must
    // execute best-effort instead of losing the sender's warming to the balance check.
    [Test]
    public void Warmup_ForASenderWithoutParentStateBalance_StillExecutes()
    {
        _stateProvider.CreateAccount(TestItem.AddressA, UInt256.Zero);
        _stateProvider.Commit(_specProvider.GenesisSpec);
        _stateProvider.CommitTree(0);

        Transaction tx = Build.A.Transaction
            .WithGasPrice(1)
            .WithMaxFeePerGas(1)
            .WithTo(TestItem.AddressB)
            .WithGasLimit(100_000)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
            .TestObject;
        Block block = Build.A.Block
            .WithNumber(long.MaxValue)
            .WithTimestamp(MainnetSpecProvider.PragueBlockTimestamp)
            .WithTransactions(tx)
            .WithGasLimit(10_000_000)
            .TestObject;

        _transactionProcessor.SetBlockExecutionContext(new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)));
        TransactionResult result = _transactionProcessor.Warmup(tx, NullTxTracer.Instance);

        Assert.That(result.TransactionExecuted, Is.True,
            "an underfunded warm sender must still warm its execution path");
        Assert.That(_stateProvider.GetNonce(TestItem.AddressA), Is.EqualTo(1UL));
    }
}
