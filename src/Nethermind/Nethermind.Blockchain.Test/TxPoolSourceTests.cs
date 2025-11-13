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
using Nethermind.Specs;
using Nethermind.Core;
using System.Linq;
using System.Collections.Generic;
using Nethermind.Config;
using NSubstitute;
using Nethermind.Core.Specs;
using Nethermind.Evm;

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
}
