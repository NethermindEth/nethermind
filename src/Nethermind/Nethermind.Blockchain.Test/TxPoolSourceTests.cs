// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
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
using NSubstitute;

namespace Nethermind.Consensus.Producers.Test;

public class TxPoolSourceTests
{
    [TestCaseSource(nameof(BlobTransactionsWithBlobGasLimitPerBlock))]
    public void GetTransactions_should_respect_customizable_blob_gas_limit(int[] blobCountPerTx, ulong customMaxBlobGasPerBlock)
    {
        TestSingleReleaseSpecProvider specProvider = new(Cancun.Instance);
        TransactionComparerProvider transactionComparerProvider = new(specProvider, Build.A.BlockTree().TestObject);

        ITxPool txPool = Substitute.For<ITxPool>();
        Dictionary<Address, Transaction[]> transactionsWithBlobs = blobCountPerTx
            .Select((blobsCount, index) => (blobCount: blobsCount, index))
            .ToDictionary(
                pair => new Address((new byte[19]).Concat(new byte[] { (byte)pair.index }).ToArray()),
                pair => new Transaction[] { Build.A.Transaction.WithShardBlobTxTypeAndFields(pair.blobCount).TestObject });
        txPool.GetPendingTransactions().Returns(new Transaction[0]);
        txPool.GetPendingLightBlobTransactionsBySender().Returns(transactionsWithBlobs);

        ITxFilterPipeline txFilterPipeline = Substitute.For<ITxFilterPipeline>();
        txFilterPipeline.Execute(Arg.Any<Transaction>(), Arg.Any<BlockHeader>()).Returns(true);

        TestEip4844Config eip4844Config = new(customMaxBlobGasPerBlock);

        TxPoolTxSource transactionSelector = new(txPool, specProvider, transactionComparerProvider, LimboLogs.Instance, txFilterPipeline, eip4844Config);

        IEnumerable<Transaction> txs = transactionSelector.GetTransactions(new BlockHeader { }, long.MaxValue);
        int blobsCount = txs.Sum(tx => tx.BlobVersionedHashes?.Length ?? 0);

        Assert.Multiple(() =>
        {
            Assert.That((ulong)blobsCount * eip4844Config.GasPerBlob, Is.LessThanOrEqualTo(eip4844Config.MaxBlobGasPerBlock));
            Assert.That(blobsCount, Is.LessThanOrEqualTo(eip4844Config.GetMaxBlobsPerBlock()));
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
