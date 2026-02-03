// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Transactions;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.TxPool;
using NUnit.Framework;
using Nethermind.Consensus.Comparers;
using Nethermind.Core.Test.Builders;
using Nethermind.Specs;
using Nethermind.Core;
using System.Linq;
using System.Collections.Generic;
using Nethermind.Config;
using NSubstitute;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.TxPool.Comparison;
using FluentAssertions;

namespace Nethermind.Consensus.Producers.Test;

[Parallelizable(ParallelScope.All)]
public class TxPoolSourceTests
{
    [TestCaseSource(nameof(BlobTransactionsWithBlobGasLimitPerBlockCombinations))]
    public void GetTransactions_should_respect_customizable_blob_gas_limit(int[] blobCountPerTx, ulong customMaxBlobGasPerBlock, int? customBlobLimit)
    {
        TestSingleReleaseSpecProvider specProvider = new(Cancun.Instance);
        TransactionComparerProvider transactionComparerProvider = new(specProvider, Build.A.BlockTree().TestObject);

        ITxPool txPool = Substitute.For<ITxPool>();
        Dictionary<AddressAsKey, Transaction[]> transactionsWithBlobs = blobCountPerTx
            .Select((blobsCount, index) => (blobCount: blobsCount, index))
            .ToDictionary(
                pair => new AddressAsKey(new Address(new byte[19].Concat(new[] { (byte)pair.index }).ToArray())),
                pair => new[] { Build.A.Transaction.WithShardBlobTxTypeAndFields(pair.blobCount).TestObject });
        txPool.GetPendingTransactions().Returns([]);
        txPool.GetPendingLightBlobTransactionsBySender().Returns(transactionsWithBlobs);

        ITxFilterPipeline txFilterPipeline = Substitute.For<ITxFilterPipeline>();
        txFilterPipeline.Execute(Arg.Any<Transaction>(), Arg.Any<BlockHeader>(), Arg.Any<IReleaseSpec>()).Returns(true);

        TxPoolTxSource transactionSelector = new(txPool, specProvider, transactionComparerProvider, LimboLogs.Instance, txFilterPipeline, new BlocksConfig { SecondsPerSlot = 12, BlockProductionBlobLimit = customBlobLimit });

        IEnumerable<Transaction> txs = transactionSelector.GetTransactions(new BlockHeader(), long.MaxValue);
        int blobsCount = txs.Sum(tx => tx.GetBlobCount());

        Assert.That(blobsCount, Is.LessThanOrEqualTo(Cancun.Instance.MaxProductionBlobCount(customBlobLimit)));
    }

    public static IEnumerable<TestCaseData> BlobTransactionsWithBlobGasLimitPerBlockCombinations()
    {
        int?[] customBlobLimits = [null, 0, 1, 2, 3, 5, 500];
        foreach ((int[] blobCountPerTx, ulong customMaxBlobGasPerBlock) in BlobTransactionsWithBlobGasLimitPerBlock())
        {
            foreach (int? customBlobLimit in customBlobLimits)
            {
                yield return new TestCaseData(blobCountPerTx, customMaxBlobGasPerBlock, customBlobLimit);
            }
        }
    }

    public static IEnumerable<(int[], ulong)> BlobTransactionsWithBlobGasLimitPerBlock()
    {
        yield return ([1, 2, 4], Eip4844Constants.GasPerBlob * 6);
        yield return ([1, 2, 6], Eip4844Constants.GasPerBlob * 6);
        yield return ([1, 6], Eip4844Constants.GasPerBlob * 6);
        yield return ([6, 1, 5], Eip4844Constants.GasPerBlob * 6);
        yield return ([1, 2], Eip4844Constants.GasPerBlob * 2);
        yield return ([1, 1], Eip4844Constants.GasPerBlob * 2);
        yield return ([2, 1], Eip4844Constants.GasPerBlob * 2);
        yield return ([2, 2], Eip4844Constants.GasPerBlob * 2);
        yield return ([3], Eip4844Constants.GasPerBlob * 2);
    }

    [TestCaseSource(nameof(MaxProductionBlobCountTests))]
    public int MaxProductionBlobCount_calculation(IReleaseSpec spec, int? customBlobLimit) => spec.MaxProductionBlobCount(customBlobLimit);

    public static IEnumerable<TestCaseData> MaxProductionBlobCountTests()
    {
        yield return new TestCaseData(Cancun.Instance, null).Returns(Cancun.Instance.MaxBlobCount);
        yield return new TestCaseData(Prague.Instance, null).Returns(Prague.Instance.MaxBlobCount);
        yield return new TestCaseData(BPO1.Instance, null).Returns(BPO1.Instance.MaxBlobCount);
        yield return new TestCaseData(BPO2.Instance, null).Returns(BPO2.Instance.MaxBlobCount);

        yield return new TestCaseData(Prague.Instance, -1).Returns(Prague.Instance.MaxBlobCount);
        yield return new TestCaseData(Prague.Instance, 0).Returns(0);
        yield return new TestCaseData(BPO1.Instance, 5).Returns(5);
        yield return new TestCaseData(BPO2.Instance, 500_000).Returns(BPO2.Instance.MaxBlobCount);
    }

    [Test]
    public void GetTransactions_should_order_blob_txs_before_regular_txs_when_blob_has_higher_priority()
    {
        TestSingleReleaseSpecProvider specProvider = new(Cancun.Instance);
        TransactionComparerProvider transactionComparerProvider = new(specProvider, Build.A.BlockTree().TestObject);

        // Create a high-priority blob tx (high gas price)
        Transaction highPriorityBlobTx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields()
            .WithMaxFeePerGas(1000.GWei())
            .WithMaxPriorityFeePerGas(500.GWei())
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        // Create a lower-priority regular tx (lower gas price)
        Transaction lowerPriorityRegularTx = Build.A.Transaction
            .WithType(TxType.EIP1559)
            .WithMaxFeePerGas(100.GWei())
            .WithMaxPriorityFeePerGas(50.GWei())
            .SignedAndResolved(TestItem.PrivateKeyB)
            .TestObject;

        // Verify comparer semantics: higher priority tx should compare as "less than" (negative result)
        IComparer<Transaction> comparer = transactionComparerProvider.GetDefaultProducerComparer(
            new BlockPreparationContext(UInt256.Zero, 1));
        int compareResult = comparer.Compare(highPriorityBlobTx, lowerPriorityRegularTx);
        compareResult.Should().Be(TxComparisonResult.FirstIsBetter, "Higher priority transaction should compare as FirstIsBetter (negative)");

        // Setup mocks
        ITxPool txPool = Substitute.For<ITxPool>();
        txPool.GetPendingTransactionsBySender(Arg.Any<bool>(), Arg.Any<UInt256>())
            .Returns(new Dictionary<AddressAsKey, Transaction[]> { { TestItem.AddressB, [lowerPriorityRegularTx] } });
        txPool.GetPendingLightBlobTransactionsBySender()
            .Returns(new Dictionary<AddressAsKey, Transaction[]> { { TestItem.AddressA, [highPriorityBlobTx] } });
        txPool.TryGetPendingBlobTransaction(Arg.Is<Hash256>(h => h == highPriorityBlobTx.Hash), out Arg.Any<Transaction?>())
            .Returns(x =>
            {
                x[1] = highPriorityBlobTx;
                return true;
            });
        txPool.SupportsBlobs.Returns(true);

        ITxFilterPipeline txFilterPipeline = Substitute.For<ITxFilterPipeline>();
        txFilterPipeline.Execute(Arg.Any<Transaction>(), Arg.Any<BlockHeader>(), Arg.Any<IReleaseSpec>()).Returns(true);

        TxPoolTxSource txSource = new(txPool, specProvider, transactionComparerProvider, LimboLogs.Instance,
            txFilterPipeline, new BlocksConfig { SecondsPerSlot = 12 });

        BlockHeader parent = Build.A.BlockHeader.WithNumber(0).WithExcessBlobGas(0).TestObject;

        // Act
        Transaction[] result = txSource.GetTransactions(parent, long.MaxValue).ToArray();

        // Assert: High priority blob tx should come BEFORE lower priority regular tx
        result.Should().BeEquivalentTo([highPriorityBlobTx, lowerPriorityRegularTx], o => o.WithStrictOrdering());
    }
}
