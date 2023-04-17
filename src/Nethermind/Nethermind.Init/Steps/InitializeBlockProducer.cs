// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(StartBlockProcessor), typeof(SetupKeyStore), typeof(InitializeNetwork), typeof(ReviewBlockTree))]
    public class InitializeBlockProducer : IStep
    {
        protected IApiWithBlockchain _api;

        public InitializeBlockProducer(INethermindApi api)
        {
            _api = api;
        }

        public async Task Execute(CancellationToken _)
        {
            if (_api.BlockProductionPolicy.ShouldStartBlockProduction())
            {
                _api.BlockProducer = await BuildProducer();
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
                foreach (IConsensusWrapperPlugin wrapperPlugin in _api.GetConsensusWrapperPlugins())
                {
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
