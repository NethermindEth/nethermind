// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Db.Blooms;
using Nethermind.Facade.Find;
using Nethermind.Logging;
using static Nethermind.Merge.AuRa.Test.AuRaMergeEngineModuleTests;

namespace Nethermind.Shutter.Test;

public class ShutterTestBlockchain(Random rnd, ITimestamper? timestamper = null, ShutterEventSimulator? eventSimulator = null) : MergeAuRaTestBlockchain(null, null)
{
    public ShutterApiSimulator? Api { get => _api; }
    private ShutterApiSimulator? _api;
    protected readonly Random _rnd = rnd;
    protected readonly ITimestamper? _timestamper = timestamper;

    protected virtual ShutterApiSimulator CreateShutterApi()
        => ShutterTestsCommon.InitApi(_rnd, this, _timestamper, eventSimulator);

    protected override IBlockProducer CreateTestBlockProducer(ITxSource txPoolTxSource, ISealer sealer, ITransactionComparerProvider transactionComparerProvider)
    {
        _api = CreateShutterApi();
        _additionalTxSource = _api.TxSource;
        return base.CreateTestBlockProducer(txPoolTxSource, sealer, transactionComparerProvider);
    }

    protected override IBlockImprovementContextFactory CreateBlockImprovementContextFactory(IBlockProducer blockProducer)
        => _api!.GetBlockImprovementContextFactory(blockProducer);

    protected override ContainerBuilder ConfigureContainer(ContainerBuilder builder, IConfigProvider configProvider) =>
        base.ConfigureContainer(builder, configProvider)
            // Weird stuff where there are receipts but no tx.
            .AddSingleton<IReceiptStorage, InMemoryReceiptStorage>()

            // For rpc
            .AddSingleton<ILogFinder>(ctx =>
            {
                // TODO: Test fail when using the same BloomStorage as BlockTree.
                var receiptStorage = ctx.Resolve<IReceiptStorage>();
                var blockTree = ctx.Resolve<IBlockTree>();
                var fileStorage = ctx.Resolve<IFileStoreFactory>();
                var bloomDb = ctx.ResolveKeyed<IDb>(DbNames.Bloom);
                var bloomConfig = ctx.Resolve<IBloomConfig>();
                var receiptsRecovery = ctx.Resolve<IReceiptsRecovery>();
                IBloomStorage bloomStorage = new BloomStorage(bloomConfig, bloomDb, fileStorage);
                return new LogFinder(blockTree, receiptStorage, receiptStorage, bloomStorage, LimboLogs.Instance,
                    receiptsRecovery);
            });
}
