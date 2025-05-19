// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using NUnit.Framework;
using System.Collections.Generic;
using Nethermind.Core.Test;

namespace Nethermind.Evm.Test;

[TestFixture]
internal class TransactionProcessorEip4844Tests
{
    private ISpecProvider _specProvider = null!;
    private IEthereumEcdsa _ethereumEcdsa = null!;
    private TransactionProcessor _transactionProcessor = null!;
    private IWorldState _stateProvider = null!;

    [SetUp]
    public void Setup()
    {
        MemDb stateDb = new();
        _specProvider = new TestSpecProvider(Cancun.Instance);
        TrieStore trieStore = TestTrieStoreFactory.Build(stateDb, LimboLogs.Instance);
        _stateProvider = new WorldState(trieStore, new MemDb(), LimboLogs.Instance);
        CodeInfoRepository codeInfoRepository = new();
        VirtualMachine virtualMachine = new(new TestBlockhashProvider(_specProvider), _specProvider, LimboLogs.Instance);
        _transactionProcessor = new TransactionProcessor(_specProvider, _stateProvider, virtualMachine, codeInfoRepository, LimboLogs.Instance);
        _ethereumEcdsa = new EthereumEcdsa(_specProvider.ChainId);
    }


    [TestCaseSource(nameof(BalanceIsAffectedByBlobGasTestCaseSource))]
    [TestCaseSource(nameof(BalanceIsNotAffectedWhenNotEnoughFunds))]
    public UInt256 Balance_is_affected_by_blob_gas_on_execution(UInt256 balance, int blobCount,
        ulong maxFeePerBlobGas, ulong excessBlobGas, ulong value)
    {
        _stateProvider.CreateAccount(TestItem.AddressA, balance);
        _stateProvider.Commit(_specProvider.GenesisSpec);
        _stateProvider.CommitTree(0);

        long gasLimit = GasCostOf.Transaction;
        Transaction blobTx = Build.A.Transaction
            .WithValue(value)
            .WithGasPrice(1)
            .WithMaxFeePerGas(1)
            .WithMaxFeePerBlobGas(maxFeePerBlobGas)
            .WithGasLimit(gasLimit)
            .WithShardBlobTxTypeAndFields(blobCount)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
            .TestObject;

        Block block = Build.A.Block
            .WithNumber(1)
            .WithTransactions(blobTx)
            .WithGasLimit(gasLimit)
            .WithExcessBlobGas(excessBlobGas)
            .WithBaseFeePerGas(1)
            .TestObject;

        var blkCtx = new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header));
        _transactionProcessor.CallAndRestore(blobTx, blkCtx, NullTxTracer.Instance);
        UInt256 deltaBalance = balance - _stateProvider.GetBalance(TestItem.PrivateKeyA.Address);
        Assert.That(deltaBalance, Is.EqualTo(UInt256.Zero));

        _transactionProcessor.Execute(blobTx, blkCtx, NullTxTracer.Instance);
        deltaBalance = balance - _stateProvider.GetBalance(TestItem.PrivateKeyA.Address);

        return deltaBalance;
    }

    public static IEnumerable<TestCaseData> BalanceIsAffectedByBlobGasTestCaseSource()
    {
        yield return new TestCaseData((UInt256)(GasCostOf.Transaction + Eip4844Constants.GasPerBlob), 1, 1ul, 0ul, 0ul)
        {
            TestName = "Blob gas consumed for 1 blob, minimal balance",
            ExpectedResult = (UInt256)(GasCostOf.Transaction + Eip4844Constants.GasPerBlob),
        };
        yield return new TestCaseData(1.Ether(), 1, 1ul, 0ul, 0ul)
        {
            TestName = "Blob gas consumed for 1 blob",
            ExpectedResult = (UInt256)(GasCostOf.Transaction + Eip4844Constants.GasPerBlob),
        };
        yield return new TestCaseData(1.Ether(), 2, 1ul, 0ul, 0ul)
        {
            TestName = "Blob gas consumed for 2 blobs",
            ExpectedResult = (UInt256)(GasCostOf.Transaction + 2 * Eip4844Constants.GasPerBlob),
        };
        yield return new TestCaseData(1.Ether(), (int)Cancun.Instance.MaxBlobCount, 1ul, 0ul, 0ul)
        {
            TestName = "Blob gas consumed for max blobs",
            ExpectedResult = (UInt256)(GasCostOf.Transaction + Cancun.Instance.GetMaxBlobGasPerBlock()),
        };
        yield return new TestCaseData(1.Ether(), 1, 10ul, 0ul, 0ul)
        {
            TestName = $"Blob gas consumed for 1 blob, with {nameof(Transaction.MaxFeePerBlobGas)} more than needed",
            ExpectedResult = (UInt256)(GasCostOf.Transaction + Eip4844Constants.GasPerBlob),
        };
        yield return new TestCaseData(1.Ether(), 1, 10ul, (ulong)Cancun.Instance.BlobBaseFeeUpdateFraction, 0ul)
        {
            TestName = $"Blob gas consumed for 1 blob, with blob gas price hiking",
            ExpectedResult = (UInt256)(GasCostOf.Transaction + Eip4844Constants.GasPerBlob * 2),
        };
        yield return new TestCaseData(1.Ether(), 1, 10ul, (ulong)Cancun.Instance.BlobBaseFeeUpdateFraction, 2ul)
        {
            TestName = $"Blob gas consumed for 1 blob, with blob gas price hiking and some {nameof(Transaction.Value)}",
            ExpectedResult = (UInt256)(GasCostOf.Transaction + Eip4844Constants.GasPerBlob * 2 + 2),
        };
    }

    public static IEnumerable<TestCaseData> BalanceIsNotAffectedWhenNotEnoughFunds()
    {
        yield return new TestCaseData((UInt256)(GasCostOf.Transaction + Eip4844Constants.GasPerBlob - 1), 1, 1ul, 0ul, 0ul)
        {
            TestName = $"Rejected if balance is not enough, all funds are returned",
            ExpectedResult = UInt256.Zero,
        };
        yield return new TestCaseData((UInt256)(GasCostOf.Transaction + Eip4844Constants.GasPerBlob + 41), 1, 1ul, 0ul, 42ul)
        {
            TestName = $"Rejected if balance is not enough to cover {nameof(Transaction.Value)} also, all funds are returned",
            ExpectedResult = UInt256.Zero,
        };
        yield return new TestCaseData((UInt256)(GasCostOf.Transaction + Eip4844Constants.GasPerBlob), 1, 10ul, (ulong)Cancun.Instance.BlobBaseFeeUpdateFraction, 0ul)
        {
            TestName = $"Rejected if balance is not enough due to blob gas price hiking, all funds are returned",
            ExpectedResult = UInt256.Zero,
        };
        yield return new TestCaseData((UInt256)(GasCostOf.Transaction + Eip4844Constants.GasPerBlob), 1, 2ul, 0ul, 0ul)
        {
            TestName = $"Rejected if balance does not cover {nameof(Transaction.MaxFeePerBlobGas)}, all funds are returned",
            ExpectedResult = UInt256.Zero,
        };
        yield return new TestCaseData((UInt256)(GasCostOf.Transaction + 2 * Eip4844Constants.GasPerBlob + 41), 1, 2ul, 0ul, 42ul)
        {
            TestName = $"Rejected if balance does not cover {nameof(Transaction.MaxFeePerBlobGas)} + {nameof(Transaction.Value)}, all funds are returned",
            ExpectedResult = UInt256.Zero,
        };
    }
}
