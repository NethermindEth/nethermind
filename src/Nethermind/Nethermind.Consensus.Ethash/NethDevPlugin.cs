// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Evm.TransactionProcessing;
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

        public IBlockProducer InitBlockProducer(ITxSource? additionalTxSource = null)
        {
            if (_nethermindApi!.SealEngineType != Nethermind.Core.SealEngineType.NethDev)
            {
                return null;
            }

            var (getFromApi, _) = _nethermindApi!.ForProducer;

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

            ReadOnlyTxProcessorSource producerEnv = new(
                _nethermindApi.WorldStateManager!,
                readOnlyBlockTree,
                getFromApi.SpecProvider,
                getFromApi.LogManager);

            IReadOnlyTxProcessingScope scope = producerEnv.Build(Keccak.EmptyTreeHash);

            BlockProcessor producerProcessor = new(
                getFromApi!.SpecProvider,
                getFromApi!.BlockValidator,
                NoBlockRewards.Instance,
                new BlockProcessor.BlockProductionTransactionsExecutor(scope, getFromApi!.SpecProvider, getFromApi.LogManager),
                scope.WorldState,
                NullReceiptStorage.Instance,
                new BlockhashStore(getFromApi.BlockTree, getFromApi.SpecProvider, scope.WorldState),
                getFromApi.LogManager);

            IBlockchainProcessor producerChainProcessor = new BlockchainProcessor(
                readOnlyBlockTree,
                producerProcessor,
                getFromApi.BlockPreprocessor,
                getFromApi.StateReader,
                getFromApi.LogManager,
                BlockchainProcessor.Options.NoReceipts);

            IBlockProducer blockProducer = new DevBlockProducer(
                additionalTxSource.Then(txPoolTxSource).ServeTxsOneByOne(),
                producerChainProcessor,
                scope.WorldState,
                getFromApi.BlockTree,
                getFromApi.Timestamper,
                getFromApi.SpecProvider,
                getFromApi.Config<IBlocksConfig>(),
                getFromApi.LogManager);

            return blockProducer;
        }

        public string SealEngineType => Nethermind.Core.SealEngineType.NethDev;
        public IBlockProducerRunner CreateBlockProducerRunner()
        {
            IBlockProductionTrigger trigger = new BuildBlocksRegularly(TimeSpan.FromMilliseconds(200))
                .IfPoolIsNotEmpty(_nethermindApi.TxPool)
                .Or(_nethermindApi.ManualBlockProductionTrigger);
            return new StandardBlockProducerRunner(
                trigger,
                _nethermindApi.BlockTree,
                _nethermindApi.BlockProducer!);
        }
    }
}
