// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.Ethash
{
    public class NethDevPlugin : IConsensusPlugin
    {
        private INethermindApi? _nethermindApi;

        public ValueTask DisposeAsync() { return ValueTask.CompletedTask; }

        public string Name => "NethDev";

        public string Description => "NethDev (Spaceneth)";

        public string Author => "Nethermind";

        public Task Init(INethermindApi nethermindApi)
        {
            _nethermindApi = nethermindApi;
            return Task.CompletedTask;
        }

        public Task<IBlockProducer> InitBlockProducer(IBlockProductionTrigger? blockProductionTrigger = null, ITxSource? additionalTxSource = null)
        {
            if (_nethermindApi!.SealEngineType != Nethermind.Core.SealEngineType.NethDev)
            {
                return Task.FromResult((IBlockProducer)null);
            }

            var (getFromApi, _) = _nethermindApi!.ForProducer;

            ReadOnlyDbProvider readOnlyDbProvider = getFromApi.DbProvider.AsReadOnly(false);
            ReadOnlyBlockTree readOnlyBlockTree = getFromApi.BlockTree.AsReadOnly();

            ITxFilterPipeline txFilterPipeline = new TxFilterPipelineBuilder(_nethermindApi.LogManager)
                .WithBaseFeeFilter(getFromApi.SpecProvider)
                .WithNullTxFilter()
                .WithMinGasPriceFilter(_nethermindApi.Config<IBlocksConfig>(), getFromApi.SpecProvider)
                .Build;

            TxPoolTxSource txPoolTxSource = new(
                getFromApi.TxPool,
                getFromApi.SpecProvider,
                getFromApi.TransactionComparerProvider!,
                getFromApi.LogManager,
                txFilterPipeline);

            ILogger logger = getFromApi.LogManager.GetClassLogger();
            if (logger.IsInfo) logger.Info("Starting Neth Dev block producer & sealer");


            ReadOnlyTxProcessingEnv producerEnv = new(
                readOnlyDbProvider,
                getFromApi.ReadOnlyTrieStore,
                readOnlyBlockTree,
                getFromApi.SpecProvider,
                getFromApi.LogManager);

            BlockProcessor producerProcessor = new(
                getFromApi!.SpecProvider,
                getFromApi!.BlockValidator,
                NoBlockRewards.Instance,
                new BlockProcessor.BlockProductionTransactionsExecutor(producerEnv, getFromApi!.SpecProvider, getFromApi.LogManager),
                producerEnv.WorldState,
                NullReceiptStorage.Instance,
                NullWitnessCollector.Instance,
                getFromApi.LogManager);

            IBlockchainProcessor producerChainProcessor = new BlockchainProcessor(
                readOnlyBlockTree,
                producerProcessor,
                getFromApi.BlockPreprocessor,
                getFromApi.StateReader,
                getFromApi.LogManager,
                BlockchainProcessor.Options.NoReceipts);

            DefaultBlockProductionTrigger = new BuildBlocksRegularly(TimeSpan.FromMilliseconds(200))
                .IfPoolIsNotEmpty(getFromApi.TxPool)
                .Or(getFromApi.ManualBlockProductionTrigger);

            IBlockProducer blockProducer = new DevBlockProducer(
                additionalTxSource.Then(txPoolTxSource).ServeTxsOneByOne(),
                producerChainProcessor,
                producerEnv.WorldState,
                getFromApi.BlockTree,
                blockProductionTrigger ?? DefaultBlockProductionTrigger,
                getFromApi.Timestamper,
                getFromApi.SpecProvider,
                getFromApi.Config<IBlocksConfig>(),
                getFromApi.LogManager);

            return Task.FromResult(blockProducer);
        }

        public string SealEngineType => Nethermind.Core.SealEngineType.NethDev;
        public IBlockProductionTrigger DefaultBlockProductionTrigger { get; private set; }

        public Task InitNetworkProtocol()
        {
            return Task.CompletedTask;
        }

        public Task InitRpcModules()
        {
            return Task.CompletedTask;
        }
    }
}
