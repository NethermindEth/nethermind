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

namespace Nethermind.Consensus.Producers.Test;

public class TxPoolSourceTests
{
    [TestCaseSource(nameof(BlobTransactionsWithBlobGasLimitPerBlock))]
    public void GetTransactions_should_respect_customizable_blob_gas_limit(int[] blobCountPerTx, ulong customMaxBlobGasPerBlock)
    {
        TestSingleReleaseSpecProvider specProvider = new(Cancun.Instance);
        TransactionComparerProvider transactionComparerProvider = new(specProvider, Build.A.BlockTree().TestObject);

        ITxPool txPool = Substitute.For<ITxPool>();
        Dictionary<AddressAsKey, Transaction[]> transactionsWithBlobs = blobCountPerTx
            .Select((blobsCount, index) => (blobCount: blobsCount, index))
            .ToDictionary(
                pair => new AddressAsKey(new Address((new byte[19]).Concat(new byte[] { (byte)pair.index }).ToArray())),
                pair => new Transaction[] { Build.A.Transaction.WithShardBlobTxTypeAndFields(pair.blobCount).TestObject });
        txPool.GetPendingTransactions().Returns([]);
        txPool.GetPendingLightBlobTransactionsBySender().Returns(transactionsWithBlobs);

        ITxFilterPipeline txFilterPipeline = Substitute.For<ITxFilterPipeline>();
        txFilterPipeline.Execute(Arg.Any<Transaction>(), Arg.Any<BlockHeader>(), Arg.Any<IReleaseSpec>()).Returns(true);

        TxPoolTxSource transactionSelector = new(txPool, specProvider, transactionComparerProvider, LimboLogs.Instance, txFilterPipeline, new BlocksConfig { SecondsPerSlot = 12 });

        IEnumerable<Transaction> txs = transactionSelector.GetTransactions(new BlockHeader { }, long.MaxValue);
        int blobsCount = txs.Sum(tx => tx.GetBlobCount());

        Assert.Multiple(() =>
        {
            Assert.That(blobsCount, Is.LessThanOrEqualTo(Cancun.Instance.MaxBlobCount));
        });
    }

    public static IEnumerable<TestCaseData> BlobTransactionsWithBlobGasLimitPerBlock()
    {
        yield return new TestCaseData(new int[] { 1, 2, 4 }, Eip4844Constants.GasPerBlob * 6);
        yield return new TestCaseData(new int[] { 1, 2, 6 }, Eip4844Constants.GasPerBlob * 6);
        yield return new TestCaseData(new int[] { 1, 6 }, Eip4844Constants.GasPerBlob * 6);
        yield return new TestCaseData(new int[] { 6, 1, 5 }, Eip4844Constants.GasPerBlob * 6);
        yield return new TestCaseData(new int[] { 1, 2 }, Eip4844Constants.GasPerBlob * 2);
        yield return new TestCaseData(new int[] { 1, 1 }, Eip4844Constants.GasPerBlob * 2);
        yield return new TestCaseData(new int[] { 2, 1 }, Eip4844Constants.GasPerBlob * 2);
        yield return new TestCaseData(new int[] { 2, 2 }, Eip4844Constants.GasPerBlob * 2);
        yield return new TestCaseData(new int[] { 3 }, Eip4844Constants.GasPerBlob * 2);
    }
}
