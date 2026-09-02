// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.Transactions;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.TxPool;
using NUnit.Framework;
using Nethermind.Consensus.Comparers;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Eip2930;
using Nethermind.Consensus.Validators;
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
using Nethermind.Crypto;
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

    private static ITxValidator CreateSpecChangeTxValidator(ISpecProvider specProvider) =>
        new SpecChangeTxValidator(specProvider.ChainId);

    private static void SetPendingForProduction(
        ITxPool txPool,
        IDictionary<AddressAsKey, Transaction[]>? transactions = null,
        IDictionary<AddressAsKey, Transaction[]>? blobTransactions = null,
        bool isRevalidated = false) =>
        txPool.GetPendingForProduction(Arg.Any<BlockHeader>(), Arg.Any<bool>(), Arg.Any<UInt256>())
            .Returns(new PendingTransactionsView(
                transactions ?? new Dictionary<AddressAsKey, Transaction[]>(),
                blobTransactions ?? new Dictionary<AddressAsKey, Transaction[]>(),
                isRevalidated));

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
        SetPendingForProduction(txPool, blobTransactions: transactionsWithBlobs);

        ITxFilterPipeline txFilterPipeline = Substitute.For<ITxFilterPipeline>();
        txFilterPipeline.Execute(Arg.Any<Transaction>(), Arg.Any<BlockHeader>(), Arg.Any<IReleaseSpec>()).Returns(true);

        TxPoolTxSource transactionSelector = new(txPool, specProvider, transactionComparerProvider, LimboLogs.Instance, txFilterPipeline, new BlocksConfig { SecondsPerSlot = 12, BlockProductionBlobLimit = customBlobLimit }, CreateSpecChangeTxValidator(specProvider));

        BlockHeader parent = Build.A.BlockHeader.WithNumber(0).WithExcessBlobGas(0).TestObject;
        BlockHeader targetBlock = Build.A.BlockHeader.WithNumber(1).WithExcessBlobGas(0).TestObject;
        IEnumerable<Transaction> txs = transactionSelector.GetTransactions(parent, targetBlock, long.MaxValue);
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
        SetPendingForProduction(txPool,
            new Dictionary<AddressAsKey, Transaction[]> { { TestItem.AddressB, [lowerPriorityRegularTx] } },
            new Dictionary<AddressAsKey, Transaction[]> { { TestItem.AddressA, [highPriorityBlobTx] } });
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
            txFilterPipeline, new BlocksConfig { SecondsPerSlot = 12 }, CreateSpecChangeTxValidator(specProvider));

        BlockHeader parent = Build.A.BlockHeader.WithNumber(0).WithExcessBlobGas(0).TestObject;
        BlockHeader targetBlock = Build.A.BlockHeader.WithNumber(1).WithExcessBlobGas(0).TestObject;

        // Act
        Transaction[] result = txSource.GetTransactions(parent, targetBlock, long.MaxValue).ToArray();

        // Assert: High priority blob tx should come BEFORE lower priority regular tx
        Assert.That(result, Is.EqualTo(new[] { highPriorityBlobTx, lowerPriorityRegularTx }).UsingTransactionComparer());
    }

    // A blob-carrying frame tx routed to the blob pool is metered against the block blob budget like a
    // type-3 tx. Source selection only — this does not assert end-to-end producibility.
    [TestCase(3, 6, true)]
    [TestCase(3, 2, false)]
    public void GetTransactions_meters_blob_carrying_frame_tx_against_blob_budget(int blobCount, int blobLimit, bool expectSelected)
    {
        TestSingleReleaseSpecProvider specProvider = new(Eip8141Prototype.Instance);
        TransactionComparerProvider transactionComparerProvider = new(specProvider, Build.A.BlockTree().TestObject);

        Transaction frameBlobTx = BuildFrameBlobTxWithSidecar(senderByte: 1, blobCount: blobCount);

        ITxPool txPool = Substitute.For<ITxPool>();
        txPool.GetPendingTransactions().Returns([]);
        txPool.GetPendingLightBlobTransactionsBySender()
            .Returns(new Dictionary<AddressAsKey, Transaction[]> { { frameBlobTx.SenderAddress!, [frameBlobTx] } });
        txPool.SupportsBlobs.Returns(true);

        ITxFilterPipeline txFilterPipeline = Substitute.For<ITxFilterPipeline>();
        txFilterPipeline.Execute(Arg.Any<Transaction>(), Arg.Any<BlockHeader>(), Arg.Any<IReleaseSpec>()).Returns(true);

        TxPoolTxSource txSource = new(txPool, specProvider, transactionComparerProvider, LimboLogs.Instance,
            txFilterPipeline, new BlocksConfig { SecondsPerSlot = 12, BlockProductionBlobLimit = blobLimit },
            CreateSpecChangeTxValidator(specProvider));

        BlockHeader parent = Build.A.BlockHeader.WithNumber(0).WithExcessBlobGas(0).TestObject;
        BlockHeader targetBlock = Build.A.BlockHeader.WithNumber(1).TestObject;
        Transaction[] result = txSource.GetTransactions(parent, targetBlock, long.MaxValue).ToArray();

        ulong selectedBlobs = result.Aggregate(0UL, (sum, tx) => sum + (ulong)tx.GetBlobCount());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Contains(frameBlobTx), Is.EqualTo(expectSelected));
            Assert.That(selectedBlobs, Is.EqualTo(expectSelected ? (ulong)blobCount : 0UL));
            Assert.That(selectedBlobs, Is.LessThanOrEqualTo((ulong)Eip8141Prototype.Instance.MaxProductionBlobCount(blobLimit)));
        }
    }

    private static Transaction BuildFrameBlobTxWithSidecar(byte senderByte, int blobCount)
    {
        byte[][] versionedHashes = new byte[blobCount][];
        byte[][] blobs = new byte[blobCount][];
        byte[][] commitments = new byte[blobCount][];
        byte[][] proofs = new byte[blobCount][];
        for (int i = 0; i < blobCount; i++)
        {
            byte[] hash = new byte[Eip4844Constants.BytesPerBlobVersionedHash];
            hash[0] = KzgPolynomialCommitments.KzgBlobHashVersionV1;
            hash[1] = (byte)i;
            versionedHashes[i] = hash;
            // Non-empty so the sidecar reads as complete rather than locally sampled; the bytes are never verified here.
            blobs[i] = [1];
            commitments[i] = [];
            proofs[i] = [];
        }

        Transaction tx = new()
        {
            Type = TxType.FrameTx,
            ChainId = TestBlockchainIds.ChainId,
            SenderAddress = new Address(new byte[19].Concat(new[] { senderByte }).ToArray()),
            Nonce = 0,
            GasLimit = 1_000_000,
            GasPrice = 1,
            DecodedMaxFeePerGas = 100.GWei,
            MaxFeePerBlobGas = 1000,
            Frames =
            [
                new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit: 100_000, UInt256.Zero, default),
            ],
            FrameSignatures = [],
            BlobVersionedHashes = versionedHashes,
            NetworkWrapper = new ShardBlobNetworkWrapper(blobs, commitments, proofs, Eip8141Prototype.Instance.BlobProofVersion),
        };
        tx.Hash = tx.CalculateHash();
        return tx;
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
        SetPendingForProduction(txPool, new Dictionary<AddressAsKey, Transaction[]>
        {
            { new AddressAsKey(underGassedTransaction.SenderAddress!), [underGassedTransaction] }
        });

        ITxFilterPipeline txFilterPipeline = new TxFilterPipelineBuilder(LimboLogs.Instance)
            .WithHeadTxFilter()
            .Build;

        TxPoolTxSource txSource = new(txPool, specProvider, transactionComparerProvider, LimboLogs.Instance,
            txFilterPipeline, new BlocksConfig(), CreateSpecChangeTxValidator(specProvider));

        BlockHeader parent = Build.A.BlockHeader.WithNumber(0).TestObject;
        BlockHeader targetBlock = Build.A.BlockHeader.WithNumber(1).TestObject;
        Transaction[] result = txSource.GetTransactions(parent, targetBlock, long.MaxValue).ToArray();

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void GetTransactions_should_use_injected_fork_sensitive_validator_when_pool_is_not_revalidated()
    {
        TestSingleReleaseSpecProvider specProvider = new(Osaka.Instance);
        TransactionComparerProvider transactionComparerProvider = new(specProvider, Build.A.BlockTree().TestObject);
        Transaction transaction = Build.A.Transaction
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;
        ITxPool txPool = Substitute.For<ITxPool>();
        SetPendingForProduction(txPool, new Dictionary<AddressAsKey, Transaction[]>
        {
            { new AddressAsKey(transaction.SenderAddress!), [transaction] }
        });
        ITxFilterPipeline txFilterPipeline = Substitute.For<ITxFilterPipeline>();
        txFilterPipeline.Execute(Arg.Any<Transaction>(), Arg.Any<BlockHeader>(), Arg.Any<IReleaseSpec>()).Returns(true);
        ITxValidator specChangeTxValidator = Substitute.For<ITxValidator>();
        specChangeTxValidator.IsWellFormed(Arg.Any<Transaction>(), Arg.Any<IReleaseSpec>())
            .Returns(new ValidationResult("chain-specific rejection"));
        TxPoolTxSource txSource = new(txPool, specProvider, transactionComparerProvider, LimboLogs.Instance,
            txFilterPipeline, new BlocksConfig(), specChangeTxValidator);
        BlockHeader parent = Build.A.BlockHeader.WithNumber(0).TestObject;
        BlockHeader targetBlock = Build.A.BlockHeader.WithNumber(1).TestObject;

        Transaction[] result = txSource.GetTransactions(parent, targetBlock, long.MaxValue).ToArray();

        Assert.That(result, Is.Empty);
        specChangeTxValidator.Received(1).IsWellFormed(transaction, Osaka.Instance);
    }

    [Test]
    public void Default_pending_view_is_empty()
    {
        PendingTransactionsView view = default;
        IReadOnlyDictionary<AddressAsKey, Transaction[]> transactions = view.Transactions;
        IReadOnlyDictionary<AddressAsKey, Transaction[]> blobTransactions = view.BlobTransactions;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(transactions, Is.Empty);
            Assert.That(blobTransactions, Is.Empty);
        }
    }

    [TestCase(1, true)]
    [TestCase(10, true)]
    [TestCase(11, false)]
    public void GetTransactions_should_bound_resolved_rejections(int invalidBlobCount, bool expectValidBlob)
    {
        TestSpecProvider specProvider = new(Osaka.Instance)
        {
            NextForkSpec = Amsterdam.Instance,
            ForkOnBlockNumber = 1
        };
        TransactionComparerProvider transactionComparerProvider = new(specProvider, Build.A.BlockTree().TestObject);

        Transaction[] invalidBlobs = new Transaction[invalidBlobCount];
        Dictionary<AddressAsKey, Transaction[]> pendingBlobTransactions = new(invalidBlobCount + 1);
        Dictionary<ValueHash256, Transaction> fullBlobTransactions = new(invalidBlobCount + 1);
        for (int i = 0; i < invalidBlobs.Length; i++)
        {
            UInt256 fee = 2.GWei + (UInt256)(invalidBlobCount - i);
            Transaction invalidBlob = Build.A.Transaction
                .WithShardBlobTxTypeAndFields(spec: Amsterdam.Instance)
                .WithAccessList(BuildUnderGassedAccessList())
                .WithGasLimit(UnderGassedTransactionGasLimit)
                .WithMaxFeePerGas(fee)
                .WithMaxPriorityFeePerGas(fee)
                .SignedAndResolved(TestItem.PrivateKeys[i])
                .TestObject;
            invalidBlobs[i] = invalidBlob;
            pendingBlobTransactions[new AddressAsKey(invalidBlob.SenderAddress!)] = [new LightTransaction(invalidBlob)];
            fullBlobTransactions[invalidBlob.Hash!.ValueHash256] = invalidBlob;
        }

        Transaction validBlob = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Amsterdam.Instance)
            .WithAccessList(BuildUnderGassedAccessList())
            .WithGasLimit(100_000)
            .WithMaxFeePerGas(1.GWei)
            .WithMaxPriorityFeePerGas(1.GWei)
            .SignedAndResolved(TestItem.PrivateKeys[invalidBlobCount])
            .TestObject;
        pendingBlobTransactions[new AddressAsKey(validBlob.SenderAddress!)] = [new LightTransaction(validBlob)];
        fullBlobTransactions[validBlob.Hash!.ValueHash256] = validBlob;

        ITxPool txPool = Substitute.For<ITxPool>();
        SetPendingForProduction(txPool, blobTransactions: pendingBlobTransactions);
        txPool.TryGetPendingBlobTransaction(Arg.Any<Hash256>(), out Arg.Any<Transaction?>())
            .Returns(callInfo =>
            {
                bool found = fullBlobTransactions.TryGetValue(
                    callInfo.ArgAt<Hash256>(0).ValueHash256,
                    out Transaction? fullBlobTransaction);
                callInfo[1] = fullBlobTransaction;
                return found;
            });
        txPool.SupportsBlobs.Returns(true);

        ITxFilterPipeline realTxFilterPipeline = new TxFilterPipelineBuilder(LimboLogs.Instance)
            .WithHeadTxFilter()
            .Build;
        ITxFilterPipeline txFilterPipeline = Substitute.For<ITxFilterPipeline>();
        txFilterPipeline.Execute(Arg.Any<Transaction>(), Arg.Any<BlockHeader>(), Arg.Any<IReleaseSpec>()).Returns(callInfo =>
            realTxFilterPipeline.Execute(
                callInfo.ArgAt<Transaction>(0),
                callInfo.ArgAt<BlockHeader>(1),
                callInfo.ArgAt<IReleaseSpec>(2)));

        TxPoolTxSource txSource = new(txPool, specProvider, transactionComparerProvider, LimboLogs.Instance,
            txFilterPipeline, new BlocksConfig { BlockProductionBlobLimit = 1 }, CreateSpecChangeTxValidator(specProvider));

        BlockHeader parent = Build.A.BlockHeader.WithNumber(0).WithExcessBlobGas(0).TestObject;
        BlockHeader targetBlock = Build.A.BlockHeader.WithNumber(1).WithExcessBlobGas(0).TestObject;

        Transaction[] result = txSource.GetTransactions(parent, targetBlock, long.MaxValue).ToArray();

        Transaction[] expected = expectValidBlob ? [validBlob] : [];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(expected).UsingTransactionComparer());
            txPool.Received(expectValidBlob ? 1 : 0).TryGetPendingBlobTransaction(validBlob.Hash!, out Arg.Any<Transaction?>());
            txFilterPipeline.DidNotReceive().Execute(validBlob, parent, Amsterdam.Instance);
        }

        for (int i = 0; i < invalidBlobs.Length; i++)
        {
            txPool.Received(1).TryGetPendingBlobTransaction(invalidBlobs[i].Hash!, out Arg.Any<Transaction?>());
            txFilterPipeline.DidNotReceive().Execute(invalidBlobs[i], parent, Amsterdam.Instance);
        }
    }

    [TestCase(26)]
    public void GetTransactions_should_skip_light_rejections_without_resolving_them(int invalidBlobCount)
    {
        TestSpecProvider specProvider = new(Osaka.Instance)
        {
            NextForkSpec = Amsterdam.Instance,
            ForkOnBlockNumber = 1
        };
        TransactionComparerProvider transactionComparerProvider = new(specProvider, Build.A.BlockTree().TestObject);
        Transaction validBlob = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Amsterdam.Instance)
            .WithMaxFeePerGas(1.GWei)
            .WithMaxPriorityFeePerGas(1.GWei)
            .SignedAndResolved(TestItem.PrivateKeys[invalidBlobCount])
            .TestObject;
        Dictionary<AddressAsKey, Transaction[]> pendingBlobTransactions = new(invalidBlobCount + 1);
        LightTransaction[] invalidBlobs = new LightTransaction[invalidBlobCount];
        for (int i = 0; i < invalidBlobs.Length; i++)
        {
            LightTransaction invalidBlob = new(validBlob)
            {
                Hash = TestItem.Keccaks[i],
                SenderAddress = TestItem.Addresses[i],
                GasPrice = 2.GWei,
                DecodedMaxFeePerGas = 2.GWei,
                GasBottleneck = 2.GWei,
                ProofVersion = ProofVersion.V0
            };
            invalidBlobs[i] = invalidBlob;
            pendingBlobTransactions[new AddressAsKey(invalidBlob.SenderAddress)] = [invalidBlob];
        }

        pendingBlobTransactions[new AddressAsKey(validBlob.SenderAddress!)] = [new LightTransaction(validBlob)];
        ITxPool txPool = Substitute.For<ITxPool>();
        SetPendingForProduction(txPool, blobTransactions: pendingBlobTransactions);
        txPool.TryGetPendingBlobTransaction(validBlob.Hash!, out Arg.Any<Transaction?>())
            .Returns(callInfo =>
            {
                callInfo[1] = validBlob;
                return true;
            });
        txPool.SupportsBlobs.Returns(true);
        ITxFilterPipeline txFilterPipeline = Substitute.For<ITxFilterPipeline>();
        txFilterPipeline.Execute(Arg.Any<Transaction>(), Arg.Any<BlockHeader>(), Arg.Any<IReleaseSpec>()).Returns(true);
        TxPoolTxSource txSource = new(txPool, specProvider, transactionComparerProvider, LimboLogs.Instance,
            txFilterPipeline, new BlocksConfig { BlockProductionBlobLimit = 1 }, CreateSpecChangeTxValidator(specProvider));
        BlockHeader parent = Build.A.BlockHeader.WithNumber(0).WithExcessBlobGas(0).TestObject;
        BlockHeader targetBlock = Build.A.BlockHeader.WithNumber(1).WithExcessBlobGas(0).TestObject;

        Transaction[] result = txSource.GetTransactions(parent, targetBlock, long.MaxValue).ToArray();

        Assert.That(result, Is.EqualTo(new[] { validBlob }).UsingTransactionComparer());
        txPool.Received(1).TryGetPendingBlobTransaction(validBlob.Hash!, out Arg.Any<Transaction?>());
        for (int i = 0; i < invalidBlobs.Length; i++)
        {
            txPool.DidNotReceive().TryGetPendingBlobTransaction(invalidBlobs[i].Hash!, out Arg.Any<Transaction?>());
        }
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
        SetPendingForProduction(txPool, blobTransactions: new Dictionary<AddressAsKey, Transaction[]>
        {
            { new AddressAsKey(blobTx.SenderAddress!), [new LightTransaction(blobTx)] }
        }, isRevalidated: true);
        txPool.TryGetPendingBlobTransaction(Arg.Is<Hash256>(h => h == blobTx.Hash), out Arg.Any<Transaction?>())
            .Returns(x =>
            {
                x[1] = blobTx;
                return true;
            });
        txPool.SupportsBlobs.Returns(true);

        BlockHeader parent = Build.A.BlockHeader.WithNumber(0).WithExcessBlobGas(0).TestObject;
        BlockHeader targetBlock = Build.A.BlockHeader.WithNumber(1).WithExcessBlobGas(0).TestObject;

        ITxFilterPipeline txFilterPipeline = Substitute.For<ITxFilterPipeline>();
        txFilterPipeline.Execute(Arg.Any<Transaction>(), Arg.Any<BlockHeader>(), Arg.Any<IReleaseSpec>()).Returns(true);

        TxPoolTxSource txSource = new(txPool, specProvider, transactionComparerProvider, LimboLogs.Instance,
            txFilterPipeline, new BlocksConfig(), CreateSpecChangeTxValidator(specProvider));

        Transaction[] result = txSource.GetTransactions(parent, targetBlock, long.MaxValue).ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(new[] { blobTx }).UsingTransactionComparer());
        }

        txPool.Received(1).GetPendingForProduction(targetBlock, Arg.Any<bool>(), Arg.Any<UInt256>());
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
        SetPendingForProduction(txPool, blobTransactions: new Dictionary<AddressAsKey, Transaction[]>
        {
            { new AddressAsKey(blobTx.SenderAddress!), [new LightTransaction(blobTx)] }
        });

        ITxFilterPipeline txFilterPipeline = Substitute.For<ITxFilterPipeline>();
        txFilterPipeline.Execute(Arg.Any<Transaction>(), Arg.Any<BlockHeader>(), Arg.Any<IReleaseSpec>()).Returns(true);

        TxPoolTxSource txSource = new(txPool, specProvider, transactionComparerProvider, LimboLogs.Instance,
            txFilterPipeline, new BlocksConfig(), CreateSpecChangeTxValidator(specProvider));

        BlockHeader parent = Build.A.BlockHeader.WithNumber(0).WithExcessBlobGas(0).TestObject;
        BlockHeader targetBlock = Build.A.BlockHeader.WithNumber(1).WithExcessBlobGas(0).TestObject;
        Transaction[] result = txSource.GetTransactions(parent, targetBlock, long.MaxValue).ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.Empty);
            txPool.DidNotReceiveWithAnyArgs().TryGetPendingBlobTransaction(Arg.Any<Hash256>(), out _);
        }
    }

    [Test]
    public void GetTransactions_should_skip_sampled_blob_txs()
    {
        Transaction sparseBlobTx = CreateSparseBlobTransaction();

        Transaction[] result = SelectSingleBlobTransaction(sparseBlobTx);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void GetTransactions_should_include_reconstructed_blob_txs()
    {
        Transaction sparseBlobTx = CreateSparseBlobTransaction(new BlobCellMask((UInt128)ulong.MaxValue));
        ShardBlobNetworkWrapper sparseWrapper = (ShardBlobNetworkWrapper)sparseBlobTx.NetworkWrapper!;
        Assert.That(BlobCellsHelper.ValidateCells(sparseWrapper), Is.True);
        Assert.That(BlobCellsHelper.TryRecoverBlobsFromVerifiedCells(sparseWrapper, out ShardBlobNetworkWrapper recoveredWrapper), Is.True);
        sparseBlobTx.NetworkWrapper = recoveredWrapper;

        Transaction[] result = SelectSingleBlobTransaction(sparseBlobTx);

        Assert.That(result, Is.EqualTo(new[] { sparseBlobTx }).UsingTransactionComparer());
    }

    [Test]
    public void GetTransactions_should_not_let_sampled_tx_crowd_out_complete_tx()
    {
        TestSingleReleaseSpecProvider specProvider = new(Osaka.Instance);
        TransactionComparerProvider comparerProvider = new(specProvider, Build.A.BlockTree().TestObject);
        Transaction sampled = CreateSparseBlobTransaction();
        Transaction complete = Build.A.Transaction
            .WithSenderAddress(TestItem.AddressB)
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .WithMaxFeePerGas(100.GWei)
            .WithMaxPriorityFeePerGas(50.GWei)
            .SignedAndResolved(TestItem.PrivateKeyB)
            .TestObject;
        ITxPool txPool = Substitute.For<ITxPool>();
        SetPendingForProduction(txPool, blobTransactions: new Dictionary<AddressAsKey, Transaction[]>
        {
            [TestItem.AddressA] = [new LightTransaction(sampled)],
            [TestItem.AddressB] = [new LightTransaction(complete)]
        }, isRevalidated: true);
        txPool.TryGetPendingBlobTransaction(complete.Hash!, out Arg.Any<Transaction?>())
            .Returns(call =>
            {
                call[1] = complete;
                return true;
            });
        txPool.SupportsBlobs.Returns(true);
        ITxFilterPipeline filterPipeline = Substitute.For<ITxFilterPipeline>();
        filterPipeline.Execute(Arg.Any<Transaction>(), Arg.Any<BlockHeader>(), Arg.Any<IReleaseSpec>()).Returns(true);
        TxPoolTxSource source = new(
            txPool,
            specProvider,
            comparerProvider,
            LimboLogs.Instance,
            filterPipeline,
            new BlocksConfig { SecondsPerSlot = 12, BlockProductionBlobLimit = 1 },
            CreateSpecChangeTxValidator(specProvider));
        BlockHeader parent = Build.A.BlockHeader.WithNumber(0).WithExcessBlobGas(0).TestObject;
        BlockHeader targetBlock = Build.A.BlockHeader.WithNumber(1).WithExcessBlobGas(0).TestObject;

        Transaction[] result = source.GetTransactions(parent, targetBlock, long.MaxValue).ToArray();

        Assert.That(result, Is.EqualTo(new[] { complete }).UsingTransactionComparer());
    }

    [Test]
    public void GetTransactions_should_not_select_blob_transaction_after_sender_nonce_gap()
    {
        TestSingleReleaseSpecProvider specProvider = new(Osaka.Instance);
        TransactionComparerProvider comparerProvider = new(specProvider, Build.A.BlockTree().TestObject);
        Transaction ready = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .WithNonce(0UL)
            .WithMaxFeePerGas(1000.GWei)
            .WithMaxPriorityFeePerGas(1000.GWei)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;
        Transaction gap = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .WithNonce(2UL)
            .WithMaxFeePerGas(900.GWei)
            .WithMaxPriorityFeePerGas(900.GWei)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;
        Transaction otherReady = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .WithNonce(0UL)
            .WithMaxFeePerGas(800.GWei)
            .WithMaxPriorityFeePerGas(800.GWei)
            .SignedAndResolved(TestItem.PrivateKeyB)
            .TestObject;
        Dictionary<ValueHash256, Transaction> fullTransactions = new()
        {
            [ready.Hash!.ValueHash256] = ready,
            [gap.Hash!.ValueHash256] = gap,
            [otherReady.Hash!.ValueHash256] = otherReady,
        };
        ITxPool txPool = Substitute.For<ITxPool>();
        SetPendingForProduction(txPool, blobTransactions: new Dictionary<AddressAsKey, Transaction[]>
        {
            [TestItem.AddressA] = [new LightTransaction(ready), new LightTransaction(gap)],
            [TestItem.AddressB] = [new LightTransaction(otherReady)],
        }, isRevalidated: true);
        txPool.TryGetPendingBlobTransaction(Arg.Any<Hash256>(), out Arg.Any<Transaction?>())
            .Returns(call =>
            {
                bool found = fullTransactions.TryGetValue(call.ArgAt<Hash256>(0).ValueHash256, out Transaction? transaction);
                call[1] = transaction;
                return found;
            });
        txPool.SupportsBlobs.Returns(true);
        ITxFilterPipeline filterPipeline = Substitute.For<ITxFilterPipeline>();
        filterPipeline.Execute(Arg.Any<Transaction>(), Arg.Any<BlockHeader>(), Arg.Any<IReleaseSpec>()).Returns(true);
        TxPoolTxSource source = new(
            txPool,
            specProvider,
            comparerProvider,
            LimboLogs.Instance,
            filterPipeline,
            new BlocksConfig { SecondsPerSlot = 12, BlockProductionBlobLimit = 2 },
            CreateSpecChangeTxValidator(specProvider));
        BlockHeader parent = Build.A.BlockHeader.WithNumber(0).WithExcessBlobGas(0).TestObject;
        BlockHeader targetBlock = Build.A.BlockHeader.WithNumber(1).WithExcessBlobGas(0).TestObject;

        Transaction[] result = source.GetTransactions(parent, targetBlock, long.MaxValue).ToArray();

        Assert.That(result, Is.EqualTo(new[] { ready, otherReady }).UsingTransactionComparer());
    }

    private static Transaction[] SelectSingleBlobTransaction(Transaction blobTx)
    {
        TestSingleReleaseSpecProvider specProvider = new(Osaka.Instance);
        TransactionComparerProvider transactionComparerProvider = new(specProvider, Build.A.BlockTree().TestObject);
        ITxPool txPool = Substitute.For<ITxPool>();
        SetPendingForProduction(txPool, blobTransactions: new Dictionary<AddressAsKey, Transaction[]>
        {
            [TestItem.AddressA] = [new LightTransaction(blobTx)]
        }, isRevalidated: true);
        txPool.TryGetPendingBlobTransaction(blobTx.Hash!, out Arg.Any<Transaction?>())
            .Returns(x =>
            {
                x[1] = blobTx;
                return true;
            });
        txPool.SupportsBlobs.Returns(true);

        ITxFilterPipeline txFilterPipeline = Substitute.For<ITxFilterPipeline>();
        txFilterPipeline.Execute(Arg.Any<Transaction>(), Arg.Any<BlockHeader>(), Arg.Any<IReleaseSpec>()).Returns(true);

        TxPoolTxSource txSource = new(
            txPool,
            specProvider,
            transactionComparerProvider,
            LimboLogs.Instance,
            txFilterPipeline,
            new BlocksConfig { SecondsPerSlot = 12 },
            CreateSpecChangeTxValidator(specProvider));

        BlockHeader parent = Build.A.BlockHeader.WithNumber(0).WithExcessBlobGas(0).TestObject;
        BlockHeader targetBlock = Build.A.BlockHeader.WithNumber(1).WithExcessBlobGas(0).TestObject;
        return txSource.GetTransactions(parent, targetBlock, long.MaxValue).ToArray();
    }

    private static Transaction CreateSparseBlobTransaction(BlobCellMask cellMask = default)
    {
        Transaction tx = Build.A.Transaction
            .WithSenderAddress(TestItem.AddressA)
            .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
            .WithMaxFeePerGas(1000.GWei)
            .WithMaxPriorityFeePerGas(500.GWei)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        ShardBlobNetworkWrapper wrapper = (ShardBlobNetworkWrapper)tx.NetworkWrapper!;
        cellMask = cellMask.IsEmpty ? BlobCellMask.FromIndices([4, 9]) : cellMask;
        Assert.That(BlobCellsHelper.TryGetFlattenedCells(wrapper, cellMask, out byte[][] cells), Is.True);

        byte[][] emptyBlobs = new byte[wrapper.Blobs.Length][];
        for (int i = 0; i < emptyBlobs.Length; i++)
        {
            emptyBlobs[i] = [];
        }

        tx.NetworkWrapper = wrapper with
        {
            Blobs = emptyBlobs,
            CellMask = cellMask,
            Cells = cells
        };
        tx.ClearLengthCache();
        return tx;
    }
}
