// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Core;
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
    public async Task should_remove_processed_txs_from_db_after_finalization([Values(0, 1, 42, 358)] long blockOfTxs, [Values(1, 42, 358)] long finalizedBlock)
    {
        Transaction GetTx(PrivateKey sender)
        {
            return Build.A.Transaction
                .WithShardBlobTxTypeAndFields()
                .WithMaxFeePerGas(UInt256.One)
                .WithMaxPriorityFeePerGas(UInt256.One)
                .WithNonce(UInt256.Zero)
                .SignedAndResolved(new EthereumEcdsa(_specProvider.ChainId, _logManager), sender).TestObject;
        }

        IColumnsDb<BlobTxsColumns> columnsDb = new MemColumnsDb<BlobTxsColumns>(BlobTxsColumns.ProcessedTxs);
        BlobTxStorage blobTxStorage = new(columnsDb);
        Transaction[] txs = { GetTx(TestItem.PrivateKeyA), GetTx(TestItem.PrivateKeyB) };

        blobTxStorage.AddBlobTransactionsFromBlock(blockOfTxs, txs);

        blobTxStorage.TryGetBlobTransactionsFromBlock(blockOfTxs, out Transaction[]? returnedTxs).Should().BeTrue();
        returnedTxs!.Length.Should().Be(2);

        IBlockFinalizationManager finalizationManager = Substitute.For<IBlockFinalizationManager>();
        ProcessedTransactionsDbCleaner dbCleaner = new(finalizationManager, columnsDb.GetColumnDb(BlobTxsColumns.ProcessedTxs), _logManager);

        finalizationManager.BlocksFinalized += Raise.EventWith(
            new FinalizeEventArgs(Build.A.BlockHeader.TestObject,
                Build.A.BlockHeader.WithNumber(finalizedBlock).TestObject));

        await dbCleaner.CleaningTask;

        blobTxStorage.TryGetBlobTransactionsFromBlock(blockOfTxs, out returnedTxs).Should().Be(blockOfTxs > finalizedBlock);
    }
}
