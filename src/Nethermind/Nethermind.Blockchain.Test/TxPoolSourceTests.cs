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

    [TestCase(1, 1, 21_000UL, 21_000UL, 6)]
    [TestCase(1, 2, 21_000UL, 21_000UL, 5)]
    [TestCase(1, 1, 21_000UL, 42_000UL, 5)]
    public void GetTransactions_should_prioritize_blob_fee_cap_only_when_selectable_execution_fees_are_equal(
        int fiveBlobPriorityFeeGwei,
        int oneBlobPriorityFeeGwei,
        ulong fiveBlobSpentGas,
        ulong oneBlobSpentGas,
        int expectedBlobCount)
    {
        TestSingleReleaseSpecProvider specProvider = new(Cancun.Instance);
        TransactionComparerProvider comparerProvider = new(specProvider, Build.A.BlockTree().TestObject);

        Transaction[] fiveBlobTxs = Enumerable.Range(0, 5)
            .Select(i => Build.A.Transaction
                .WithShardBlobTxTypeAndFields(5)
                .WithNonce((ulong)i)
                .WithMaxFeePerGas(30.GWei)
                .WithMaxPriorityFeePerGas(fiveBlobPriorityFeeGwei.GWei)
                .WithMaxFeePerBlobGas(120.Wei)
                .SignedAndResolved(TestItem.PrivateKeyA)
                .TestObject)
            .ToArray();
        Transaction[] oneBlobTxs = Enumerable.Range(0, 5)
            .Select(i => Build.A.Transaction
                .WithShardBlobTxTypeAndFields(1)
                .WithNonce((ulong)i)
                .WithMaxFeePerGas(30.GWei)
                .WithMaxPriorityFeePerGas(oneBlobPriorityFeeGwei.GWei)
                .WithMaxFeePerBlobGas(100.Wei)
                .SignedAndResolved(TestItem.PrivateKeyB)
                .TestObject)
            .ToArray();
        foreach (Transaction tx in fiveBlobTxs) tx.SpentGas = fiveBlobSpentGas;
        foreach (Transaction tx in oneBlobTxs) tx.SpentGas = oneBlobSpentGas;

        ITxPool txPool = Substitute.For<ITxPool>();
        txPool.GetPendingTransactionsBySender(Arg.Any<bool>(), Arg.Any<UInt256>()).Returns(new Dictionary<AddressAsKey, Transaction[]>());
        txPool.GetPendingLightBlobTransactionsBySender().Returns(new Dictionary<AddressAsKey, Transaction[]>
        {
            { TestItem.AddressA, fiveBlobTxs },
            { TestItem.AddressB, oneBlobTxs },
        });

        ITxFilterPipeline filter = Substitute.For<ITxFilterPipeline>();
        filter.Execute(Arg.Any<Transaction>(), Arg.Any<BlockHeader>(), Arg.Any<IReleaseSpec>()).Returns(true);
        TxPoolTxSource source = new(txPool, specProvider, comparerProvider, LimboLogs.Instance, filter, new BlocksConfig());
        BlockHeader parent = Build.A.BlockHeader.WithNumber(0).WithExcessBlobGas(0).WithBaseFee(1.GWei).TestObject;

        Transaction[] result = source.GetTransactions(parent, long.MaxValue).ToArray();
        Transaction[] expected = expectedBlobCount == 6
            ? [fiveBlobTxs[0], oneBlobTxs[0]]
            : oneBlobTxs;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Sum(static tx => tx.GetBlobCount()), Is.EqualTo(expectedBlobCount));
            Assert.That(result, Is.EqualTo(expected).UsingTransactionComparer());
            // The first case is the intentional economic-policy counterexample: preserving producer
            // priority selects two equal-fee transactions instead of five lower-priority transactions.
            if (expectedBlobCount == 6) Assert.That(result, Has.Length.EqualTo(2));
        }
    }

    [Test]
    public void GetTransactions_should_use_transaction_priority_instead_of_summed_blob_fee_caps()
    {
        Transaction higherPriority = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(2)
            .WithMaxFeePerGas(30.GWei)
            .WithMaxPriorityFeePerGas(1.GWei)
            .WithMaxFeePerBlobGas(120.Wei)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;
        Transaction lowerPriority = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(5)
            .WithMaxFeePerGas(30.GWei)
            .WithMaxPriorityFeePerGas(1.GWei)
            .WithMaxFeePerBlobGas(100.Wei)
            .SignedAndResolved(TestItem.PrivateKeyB)
            .TestObject;
        higherPriority.SpentGas = lowerPriority.SpentGas = 21_000;

        Transaction[] result = GetBlobTransactions(
            new Dictionary<AddressAsKey, Transaction[]>
            {
                { TestItem.AddressA, [higherPriority] },
                { TestItem.AddressB, [lowerPriority] },
            },
            blockProductionBlobLimit: 5);

        Assert.That(result, Is.EqualTo([higherPriority]).UsingTransactionComparer());
    }

    [Test]
    public void GetTransactions_should_ignore_over_capacity_prefix_when_selecting_objective()
    {
        Transaction higherPriority = BlobTransaction(TestItem.PrivateKeyA, nonce: 0, blobCount: 5, priorityFeeGwei: 1);
        higherPriority.MaxFeePerBlobGas = 120.Wei;
        Transaction unreachableDependent = BlobTransaction(TestItem.PrivateKeyA, nonce: 1, blobCount: 2, priorityFeeGwei: 2);
        unreachableDependent.MaxFeePerBlobGas = 120.Wei;
        Transaction[] lowerPriority = Enumerable.Range(0, 5)
            .Select(i => BlobTransaction(TestItem.PrivateKeyB, (ulong)i, blobCount: 1, priorityFeeGwei: 1))
            .ToArray();

        Transaction[] result = GetBlobTransactions(new Dictionary<AddressAsKey, Transaction[]>
        {
            { TestItem.AddressA, [higherPriority, unreachableDependent] },
            { TestItem.AddressB, lowerPriority },
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(new[] { higherPriority, lowerPriority[0] }).UsingTransactionComparer());
            Assert.That(result.Sum(static tx => tx.GetBlobCount()), Is.EqualTo(6));
        }
    }

    [Test]
    public void GetTransactions_should_ignore_blob_transaction_with_unresolved_sender()
    {
        Transaction unresolved = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(1)
            .WithMaxFeePerGas(30.GWei)
            .WithMaxPriorityFeePerGas(1.GWei)
            .WithMaxFeePerBlobGas(100.Wei)
            .TestObject;
        unresolved.SpentGas = 21_000;

        Transaction[] result = GetBlobTransactions(new Dictionary<AddressAsKey, Transaction[]>
        {
            { TestItem.AddressA, [unresolved] },
        });

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void GetTransactions_should_not_select_dependent_blob_transaction_without_predecessor()
    {
        Transaction predecessor = BlobTransaction(TestItem.PrivateKeyA, nonce: 0, blobCount: 5, priorityFeeGwei: 10);
        Transaction dependent = BlobTransaction(TestItem.PrivateKeyA, nonce: 1, blobCount: 1, priorityFeeGwei: 8);
        Transaction[] competitors =
        [
            BlobTransaction(TestItem.PrivateKeyB, nonce: 0, blobCount: 1, priorityFeeGwei: 9),
            BlobTransaction(TestItem.PrivateKeyC, nonce: 0, blobCount: 1, priorityFeeGwei: 9),
            BlobTransaction(TestItem.PrivateKeyD, nonce: 0, blobCount: 1, priorityFeeGwei: 9),
            BlobTransaction(TestItem.PrivateKeyE, nonce: 0, blobCount: 1, priorityFeeGwei: 9),
            BlobTransaction(TestItem.PrivateKeyF, nonce: 0, blobCount: 1, priorityFeeGwei: 9),
        ];

        Dictionary<AddressAsKey, Transaction[]> pending = new()
        {
            { TestItem.AddressA, [predecessor, dependent] },
        };
        foreach (Transaction competitor in competitors)
        {
            pending.Add(competitor.SenderAddress!, [competitor]);
        }

        Transaction[] result = GetBlobTransactions(pending);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Has.Length.EqualTo(competitors.Length));
            Assert.That(result, Does.Not.Contain(predecessor));
            Assert.That(result, Does.Not.Contain(dependent));
            Assert.That(result, Is.EquivalentTo(competitors).UsingTransactionComparer());
        }
    }

    [Test]
    public void GetTransactions_should_select_contiguous_sender_prefix()
    {
        Transaction predecessor = BlobTransaction(TestItem.PrivateKeyA, nonce: 0, blobCount: 1, priorityFeeGwei: 10);
        Transaction dependent = BlobTransaction(TestItem.PrivateKeyA, nonce: 1, blobCount: 5, priorityFeeGwei: 8);
        dependent.SpentGas = 42_000;
        Transaction competitor = BlobTransaction(TestItem.PrivateKeyB, nonce: 0, blobCount: 5, priorityFeeGwei: 9);

        Transaction[] result = GetBlobTransactions(new Dictionary<AddressAsKey, Transaction[]>
        {
            { TestItem.AddressA, [predecessor, dependent] },
            { TestItem.AddressB, [competitor] },
        });

        Assert.That(result, Is.EqualTo([predecessor, dependent]).UsingTransactionComparer());
    }

    [Test]
    public void GetTransactions_should_not_treat_blob_chain_as_nonce_continuity()
    {
        Transaction nonceZero = BlobTransaction(TestItem.PrivateKeyA, nonce: 0, blobCount: 1, priorityFeeGwei: 10);
        Transaction nonceTwo = BlobTransaction(TestItem.PrivateKeyA, nonce: 2, blobCount: 5, priorityFeeGwei: 9);
        Transaction competitor = BlobTransaction(TestItem.PrivateKeyB, nonce: 0, blobCount: 5, priorityFeeGwei: 8);

        Transaction[] result = GetBlobTransactions(new Dictionary<AddressAsKey, Transaction[]>
        {
            { TestItem.AddressA, [nonceZero, nonceTwo] },
            { TestItem.AddressB, [competitor] },
        });

        Assert.That(result, Is.EqualTo(new[] { nonceZero, competitor }).UsingTransactionComparer());
    }

    [Test]
    public void GetTransactions_should_not_select_dependent_when_predecessor_fails_blob_fee_filter()
    {
        Transaction predecessor = BlobTransaction(TestItem.PrivateKeyA, nonce: 0, blobCount: 1, priorityFeeGwei: 10);
        predecessor.MaxFeePerBlobGas = UInt256.Zero;
        Transaction dependent = BlobTransaction(TestItem.PrivateKeyA, nonce: 1, blobCount: 1, priorityFeeGwei: 9);

        Transaction[] result = GetBlobTransactions(new Dictionary<AddressAsKey, Transaction[]>
        {
            { TestItem.AddressA, [predecessor, dependent] },
        });

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void GetTransactions_should_keep_lower_value_sender_prefix_needed_by_higher_value_dependent_transaction()
    {
        Transaction fiveBlobCompetitor = BlobTransaction(TestItem.PrivateKeyA, nonce: 0, blobCount: 5, priorityFeeGwei: 10);
        Transaction predecessor = BlobTransaction(TestItem.PrivateKeyB, nonce: 0, blobCount: 1, priorityFeeGwei: 9);
        Transaction oneBlobCompetitor = BlobTransaction(TestItem.PrivateKeyC, nonce: 0, blobCount: 1, priorityFeeGwei: 8);
        Transaction dependent = BlobTransaction(TestItem.PrivateKeyB, nonce: 1, blobCount: 5, priorityFeeGwei: 7);
        oneBlobCompetitor.SpentGas = 30_000;
        dependent.SpentGas = 100_000;

        Transaction[] result = GetBlobTransactions(new Dictionary<AddressAsKey, Transaction[]>
        {
            { TestItem.AddressA, [fiveBlobCompetitor] },
            { TestItem.AddressB, [predecessor, dependent] },
            { TestItem.AddressC, [oneBlobCompetitor] },
        });

        Assert.That(result, Is.EqualTo([predecessor, dependent]).UsingTransactionComparer());
    }

    private static Transaction BlobTransaction(
        Nethermind.Crypto.PrivateKey privateKey,
        ulong nonce,
        int blobCount,
        int priorityFeeGwei)
    {
        Transaction transaction = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(blobCount)
            .WithNonce(nonce)
            .WithMaxFeePerGas(30.GWei)
            .WithMaxPriorityFeePerGas(priorityFeeGwei.GWei)
            .WithMaxFeePerBlobGas(100.Wei)
            .SignedAndResolved(privateKey)
            .TestObject;
        transaction.SpentGas = 21_000;
        return transaction;
    }

    private static Transaction[] GetBlobTransactions(
        Dictionary<AddressAsKey, Transaction[]> pending,
        int? blockProductionBlobLimit = null)
    {
        TestSingleReleaseSpecProvider specProvider = new(Cancun.Instance);
        ITxPool txPool = Substitute.For<ITxPool>();
        txPool.GetPendingTransactionsBySender(Arg.Any<bool>(), Arg.Any<UInt256>()).Returns(new Dictionary<AddressAsKey, Transaction[]>());
        txPool.GetPendingLightBlobTransactionsBySender().Returns(pending);
        ITxFilterPipeline filter = Substitute.For<ITxFilterPipeline>();
        filter.Execute(Arg.Any<Transaction>(), Arg.Any<BlockHeader>(), Arg.Any<IReleaseSpec>()).Returns(true);
        TxPoolTxSource source = new(txPool, specProvider,
            new TransactionComparerProvider(specProvider, Build.A.BlockTree().TestObject), LimboLogs.Instance,
            filter, new BlocksConfig { BlockProductionBlobLimit = blockProductionBlobLimit });
        BlockHeader parent = Build.A.BlockHeader.WithNumber(0).WithExcessBlobGas(0).WithBaseFee(1.GWei).TestObject;
        return source.GetTransactions(parent, long.MaxValue).ToArray();
    }
}
