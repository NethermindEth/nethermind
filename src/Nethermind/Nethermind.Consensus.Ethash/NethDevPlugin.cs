// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core.Crypto;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Consensus.Ethash
{
    public class NethDevPlugin(ChainSpec chainSpec) : IConsensusPlugin
    {
        public const string NethDev = "NethDev";
        private INethermindApi? _nethermindApi;

        public ValueTask DisposeAsync() { return ValueTask.CompletedTask; }

        public string Name => NethDev;

        public string Description => $"{NethDev} (Spaceneth)";

        public string Author => "Nethermind";

        public bool Enabled => chainSpec.SealEngineType == SealEngineType;

        public Task Init(INethermindApi nethermindApi)
        {
            _nethermindApi = nethermindApi;
            return Task.CompletedTask;
        }

        public IBlockProducer InitBlockProducer(ITxSource? additionalTxSource = null)
        {
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

            IReadOnlyTxProcessingScope scope = getFromApi.ReadOnlyTxProcessingEnvFactory.Create().Build(Keccak.EmptyTreeHash);

            BlockProcessor producerProcessor = new BlockProcessor(
                getFromApi!.SpecProvider,
                getFromApi!.BlockValidator,
                NoBlockRewards.Instance,
                new BlockProcessor.BlockProductionTransactionsExecutor(scope, getFromApi!.SpecProvider, getFromApi.LogManager),
                scope.WorldState,
                NullReceiptStorage.Instance,
                new BeaconBlockRootHandler(scope.TransactionProcessor, scope.WorldState),
                new BlockhashStore(getFromApi.SpecProvider, scope.WorldState),
                getFromApi.LogManager,
                new WithdrawalProcessor(scope.WorldState, getFromApi.LogManager),
                new ExecutionRequestsProcessor(scope.TransactionProcessor)
            );

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

        public string SealEngineType => NethDev;

        public IBlockProducerRunner InitBlockProducerRunner(IBlockProducer blockProducer)
        {
            IBlockProductionTrigger trigger = new BuildBlocksRegularly(TimeSpan.FromMilliseconds(200))
                .IfPoolIsNotEmpty(_nethermindApi.TxPool)
                .Or(_nethermindApi.ManualBlockProductionTrigger);
            return new StandardBlockProducerRunner(
                trigger,
                _nethermindApi.BlockTree,
                blockProducer);
        }
    }
}
