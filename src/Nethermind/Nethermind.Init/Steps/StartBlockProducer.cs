// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Logging;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(InitializeBlockProducer), typeof(ReviewBlockTree),
        typeof(InitializePrecompiles))] // Unfortunately EngineRPC API need review blockTree
    public class StartBlockProducer : IStep
    {
        protected IApiWithBlockchain _api;

        public StartBlockProducer(INethermindApi api)
        {
            _api = api;
        }

        public async Task Execute(CancellationToken _)
        {
            if (_api.BlockProductionPolicy!.ShouldStartBlockProduction() && _api.BlockProducer is not null)
            {
                if (_api.BlockTree is null) throw new StepDependencyException(nameof(_api.BlockTree));

                ILogger logger = _api.LogManager.GetClassLogger();
                if (logger.IsInfo) logger.Info($"Starting {_api.SealEngineType} block producer & sealer");
                ProducedBlockSuggester suggester = new(_api.BlockTree, _api.BlockProducer);
                _api.DisposeStack.Push(suggester);
                await _api.BlockProducer.Start();
            }
        }

        protected virtual async Task<IBlockProducer> BuildProducer()
        {
            _api.BlockProducerEnvFactory = new BlockProducerEnvFactory(_api.DbProvider!,
                _api.BlockTree!,
                _api.ReadOnlyTrieStore!,
                _api.SpecProvider!,
                _api.BlockValidator!,
                _api.RewardCalculatorSource!,
                _api.ReceiptStorage!,
                _api.BlockPreprocessor,
                _api.TxPool!,
                _api.TransactionComparerProvider!,
                _api.Config<IBlocksConfig>(),
                _api.LogManager);

            if (_api.ChainSpec is null) throw new StepDependencyException(nameof(_api.ChainSpec));
            IConsensusPlugin? consensusPlugin = _api.GetConsensusPlugin();

            if (consensusPlugin is not null)
            {
                // TODO: need to wrap preMerge producer inside theMerge first, then need to wrap all of it with MEV
                // I am pretty sure that MEV can be done better than this way
                foreach (IConsensusWrapperPlugin wrapperPlugin in _api.GetConsensusWrapperPlugins())
                {
                    // TODO: foreach returns the first one now
                    return await wrapperPlugin.InitBlockProducer(consensusPlugin);
                }

                return await consensusPlugin.InitBlockProducer();
            }
            else
            {
                throw new NotSupportedException($"Mining in {_api.ChainSpec.SealEngineType} mode is not supported");
            }
        }
    }
}
