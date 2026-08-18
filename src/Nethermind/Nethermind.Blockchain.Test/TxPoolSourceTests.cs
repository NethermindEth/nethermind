// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Transactions;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.TxPool;
using NUnit.Framework;
using Nethermind.Consensus.Comparers;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Eip2930;
using Nethermind.Specs;
using Nethermind.Core;
using Nethermind.Core.Test;
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

namespace Nethermind.Consensus.Producers.Test;

[Parallelizable(ParallelScope.All)]
public class TxPoolSourceTests
{
    // Deliberately below Amsterdam's intrinsic gas requirement for the access list built below.
    private const ulong UnderGassedTransactionGasLimit = 42_400;

    private static AccessList BuildUnderGassedAccessList()
    {
        AccessList.Builder accessListBuilder = new();
        accessListBuilder.AddAddress(TestItem.AddressC);
        for (int i = 0; i < 10; i++)
        {
            accessListBuilder.AddStorage((UInt256)i);
        }

        return accessListBuilder.Build();
    }

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
        ulong blobsCount = txs.Aggregate(0UL, (sum, tx) => sum + (ulong)tx.GetBlobCount());

        Assert.That(blobsCount, Is.LessThanOrEqualTo((ulong)Cancun.Instance.MaxProductionBlobCount(customBlobLimit)));
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
    public ulong MaxProductionBlobCount_calculation(IReleaseSpec spec, int? customBlobLimit) => spec.MaxProductionBlobCount(customBlobLimit);

    public static IEnumerable<TestCaseData> MaxProductionBlobCountTests()
    {
        yield return new TestCaseData(Cancun.Instance, null).Returns(Cancun.Instance.MaxBlobCount);
        yield return new TestCaseData(Prague.Instance, null).Returns(Prague.Instance.MaxBlobCount);
        yield return new TestCaseData(BPO1.Instance, null).Returns(BPO1.Instance.MaxBlobCount);
        yield return new TestCaseData(BPO2.Instance, null).Returns(BPO2.Instance.MaxBlobCount);

        yield return new TestCaseData(Prague.Instance, -1).Returns(Prague.Instance.MaxBlobCount);
        yield return new TestCaseData(Prague.Instance, 0).Returns(0ul);
        yield return new TestCaseData(BPO1.Instance, 5).Returns(5ul);
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
            .WithMaxFeePerGas(1000.GWei)
            .WithMaxPriorityFeePerGas(500.GWei)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        // Create a lower-priority regular tx (lower gas price)
        Transaction lowerPriorityRegularTx = Build.A.Transaction
            .WithType(TxType.EIP1559)
            .WithMaxFeePerGas(100.GWei)
            .WithMaxPriorityFeePerGas(50.GWei)
            .SignedAndResolved(TestItem.PrivateKeyB)
            .TestObject;

        // Verify comparer semantics: higher priority tx should compare as "less than" (negative result)
        IComparer<Transaction> comparer = transactionComparerProvider.GetDefaultProducerComparer(
            new BlockPreparationContext(UInt256.Zero, 1));
        int compareResult = comparer.Compare(highPriorityBlobTx, lowerPriorityRegularTx);
        Assert.That(compareResult, Is.EqualTo(TxComparisonResult.XFirst), "Higher priority transaction should compare as XFirst (negative)");

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
        Assert.That(result, Is.EqualTo(new[] { highPriorityBlobTx, lowerPriorityRegularTx }).UsingTransactionComparer());
    }

    [Test]
    public void GetTransactions_should_filter_transactions_that_are_under_gassed_for_next_fork()
    {
        TestSpecProvider specProvider = new(Osaka.Instance)
        {
            NextForkSpec = Amsterdam.Instance,
            ForkOnBlockNumber = 1
        };
        TransactionComparerProvider transactionComparerProvider = new(specProvider, Build.A.BlockTree().TestObject);

        Transaction underGassedTransaction = Build.A.Transaction
            .WithType(TxType.AccessList)
            .WithAccessList(BuildUnderGassedAccessList())
            .WithGasLimit(UnderGassedTransactionGasLimit)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;
        ITxPool txPool = Substitute.For<ITxPool>();
        txPool.GetPendingTransactionsBySender().Returns(new Dictionary<AddressAsKey, Transaction[]>
        {
            { new AddressAsKey(underGassedTransaction.SenderAddress!), [underGassedTransaction] }
        });
        txPool.GetPendingLightBlobTransactionsBySender().Returns(new Dictionary<AddressAsKey, Transaction[]>());

        ITxFilterPipeline txFilterPipeline = new TxFilterPipelineBuilder(LimboLogs.Instance)
            .WithHeadTxFilter()
            .Build;

        TxPoolTxSource txSource = new(txPool, specProvider, transactionComparerProvider, LimboLogs.Instance,
            txFilterPipeline, new BlocksConfig());

        Transaction[] result = txSource.GetTransactions(new BlockHeader(), long.MaxValue).ToArray();

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void GetTransactions_should_not_let_an_invalid_blob_displace_a_valid_blob()
    {
        TestSpecProvider specProvider = new(Osaka.Instance)
        {
            NextForkSpec = Amsterdam.Instance,
            ForkOnBlockNumber = 1
        };
        TransactionComparerProvider transactionComparerProvider = new(specProvider, Build.A.BlockTree().TestObject);

        Transaction invalidBlob = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Amsterdam.Instance)
            .WithAccessList(BuildUnderGassedAccessList())
            .WithGasLimit(UnderGassedTransactionGasLimit)
            .WithMaxFeePerGas(2.GWei)
            .WithMaxPriorityFeePerGas(2.GWei)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;
        Transaction validBlob = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Amsterdam.Instance)
            .WithAccessList(BuildUnderGassedAccessList())
            .WithGasLimit(100_000)
            .WithMaxFeePerGas(1.GWei)
            .WithMaxPriorityFeePerGas(1.GWei)
            .SignedAndResolved(TestItem.PrivateKeyB)
            .TestObject;
        ITxPool txPool = Substitute.For<ITxPool>();
        txPool.GetPendingLightBlobTransactionsBySender().Returns(new Dictionary<AddressAsKey, Transaction[]>
        {
            { new AddressAsKey(invalidBlob.SenderAddress!), [new LightTransaction(invalidBlob)] },
            { new AddressAsKey(validBlob.SenderAddress!), [new LightTransaction(validBlob)] }
        });
        txPool.GetPendingTransactionsBySender().Returns(new Dictionary<AddressAsKey, Transaction[]>());
        txPool.TryGetPendingBlobTransaction(Arg.Is<Hash256>(h => h == invalidBlob.Hash), out Arg.Any<Transaction?>())
            .Returns(x =>
            {
                x[1] = invalidBlob;
                return true;
            });
        txPool.TryGetPendingBlobTransaction(Arg.Is<Hash256>(h => h == validBlob.Hash), out Arg.Any<Transaction?>())
            .Returns(x =>
            {
                x[1] = validBlob;
                return true;
            });
        txPool.SupportsBlobs.Returns(true);

        ITxFilterPipeline txFilterPipeline = new TxFilterPipelineBuilder(LimboLogs.Instance)
            .WithHeadTxFilter()
            .Build;

        TxPoolTxSource txSource = new(txPool, specProvider, transactionComparerProvider, LimboLogs.Instance,
            txFilterPipeline, new BlocksConfig { BlockProductionBlobLimit = 1 });

        BlockHeader parent = Build.A.BlockHeader.WithNumber(0).WithExcessBlobGas(0).TestObject;
        BlockHeader targetBlock = Build.A.BlockHeader.WithNumber(1).WithExcessBlobGas(0).TestObject;
        txPool.EnsureSafeForkState(targetBlock).Returns(false);

        Transaction[] result = txSource.GetTransactions(parent, long.MaxValue, targetBlock: targetBlock).ToArray();

        Assert.That(result, Is.EqualTo(new[] { validBlob }).UsingTransactionComparer());
    }

    [Test]
    public void GetTransactions_should_skip_full_fork_validation_when_pool_is_safe_for_target_block()
    {
        TestSingleReleaseSpecProvider specProvider = new(Cancun.Instance);
        TransactionComparerProvider transactionComparerProvider = new(specProvider, Build.A.BlockTree().TestObject);
        Transaction blobTx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields()
            .WithMaxFeePerGas(2.GWei)
            .WithMaxPriorityFeePerGas(2.GWei)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;
        ITxPool txPool = Substitute.For<ITxPool>();
        txPool.GetPendingTransactionsBySender().Returns(new Dictionary<AddressAsKey, Transaction[]>());
        txPool.GetPendingLightBlobTransactionsBySender().Returns(new Dictionary<AddressAsKey, Transaction[]>
        {
            { new AddressAsKey(blobTx.SenderAddress!), [new LightTransaction(blobTx)] }
        });
        txPool.TryGetPendingBlobTransaction(Arg.Is<Hash256>(h => h == blobTx.Hash), out Arg.Any<Transaction?>())
            .Returns(x =>
            {
                x[1] = blobTx;
                return true;
            });
        txPool.SupportsBlobs.Returns(true);

        BlockHeader parent = Build.A.BlockHeader.WithNumber(0).WithExcessBlobGas(0).TestObject;
        BlockHeader targetBlock = Build.A.BlockHeader.WithNumber(1).WithExcessBlobGas(0).TestObject;
        txPool.EnsureSafeForkState(targetBlock).Returns(true);

        ITxFilterPipeline txFilterPipeline = Substitute.For<ITxFilterPipeline>();
        txFilterPipeline.Execute(Arg.Any<Transaction>(), Arg.Any<BlockHeader>(), Arg.Any<IReleaseSpec>()).Returns(true);

        TxPoolTxSource txSource = new(txPool, specProvider, transactionComparerProvider, LimboLogs.Instance,
            txFilterPipeline, new BlocksConfig());

        Transaction[] result = txSource.GetTransactions(parent, long.MaxValue, targetBlock: targetBlock).ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(new[] { blobTx }).UsingTransactionComparer());
        }

        txPool.Received(1).EnsureSafeForkState(targetBlock);
        txFilterPipeline.DidNotReceive().Execute(blobTx, parent, Arg.Any<IReleaseSpec>());
    }

    [Test]
    public void GetTransactions_should_not_resolve_blob_when_blob_fee_is_too_low()
    {
        TestSingleReleaseSpecProvider specProvider = new(Cancun.Instance);
        TransactionComparerProvider transactionComparerProvider = new(specProvider, Build.A.BlockTree().TestObject);
        Transaction blobTx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields()
            .WithMaxFeePerBlobGas(0)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        ITxPool txPool = Substitute.For<ITxPool>();
        txPool.GetPendingTransactionsBySender().Returns(new Dictionary<AddressAsKey, Transaction[]>());
        txPool.GetPendingLightBlobTransactionsBySender().Returns(new Dictionary<AddressAsKey, Transaction[]>
        {
            { new AddressAsKey(blobTx.SenderAddress!), [new LightTransaction(blobTx)] }
        });

        ITxFilterPipeline txFilterPipeline = Substitute.For<ITxFilterPipeline>();
        txFilterPipeline.Execute(Arg.Any<Transaction>(), Arg.Any<BlockHeader>(), Arg.Any<IReleaseSpec>()).Returns(true);

        TxPoolTxSource txSource = new(txPool, specProvider, transactionComparerProvider, LimboLogs.Instance,
            txFilterPipeline, new BlocksConfig());

        Transaction[] result = txSource.GetTransactions(
            Build.A.BlockHeader.WithNumber(0).WithExcessBlobGas(0).TestObject,
            long.MaxValue).ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.Empty);
            txPool.DidNotReceiveWithAnyArgs().TryGetPendingBlobTransaction(Arg.Any<Hash256>(), out _);
        }
    }
}
