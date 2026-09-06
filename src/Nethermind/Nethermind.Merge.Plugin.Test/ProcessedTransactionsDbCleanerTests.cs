// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

[TestFixture]
[Parallelizable(ParallelScope.Self)]
public class ProcessedTransactionsDbCleanerTests
{
    private readonly ILogManager _logManager = LimboLogs.Instance;
    private readonly ISpecProvider _specProvider = MainnetSpecProvider.Instance;

    [Test]
    public async Task should_remove_processed_txs_from_db_after_finalization([Values(0UL, 1UL, 42UL, 358UL)] ulong blockOfTxs, [Values(1UL, 42UL, 358UL)] ulong finalizedBlock)
    {
        Transaction GetTx(PrivateKey sender) =>
            Build.A.Transaction
                .WithShardBlobTxTypeAndFields()
                .WithMaxFeePerGas(UInt256.One)
                .WithMaxPriorityFeePerGas(UInt256.One)
                .WithNonce(0UL)
                .SignedAndResolved(new EthereumEcdsa(_specProvider.ChainId), sender).TestObject;

        IColumnsDb<BlobTxsColumns> columnsDb = new MemColumnsDb<BlobTxsColumns>(BlobTxsColumns.ProcessedTxs);
        BlobTxStorage blobTxStorage = new(columnsDb);
        using (ArrayPoolListRef<Transaction> txs = new(2, GetTx(TestItem.PrivateKeyA), GetTx(TestItem.PrivateKeyB)))
        {
            blobTxStorage.AddBlobTransactionsFromBlock(blockOfTxs, txs);
        }

        Assert.That(blobTxStorage.TryGetBlobTransactionsFromBlock(blockOfTxs, out Transaction[]? returnedTxs), Is.True);
        Assert.That(returnedTxs!.Length, Is.EqualTo(2));

        IBlockTree blockTree = Substitute.For<IBlockTree>();
        IDbProvider dbProvider = Substitute.For<IDbProvider>();
        dbProvider.BlobTransactionsDb.Returns(columnsDb);
        ProcessedTransactionsDbCleaner dbCleaner = new(blockTree, dbProvider, _logManager, new TxPoolConfig());

        blockTree.BlocksFinalized += Raise.EventWith(
            new FinalizeEventArgs(Build.A.BlockHeader.WithNumber(finalizedBlock).TestObject));

        await dbCleaner.CleaningTask;

        Assert.That(blobTxStorage.TryGetBlobTransactionsFromBlock(blockOfTxs, out _), Is.EqualTo(blockOfTxs > finalizedBlock));
    }
}
