// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Logging;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(StartBlockProcessor), typeof(InitializeBlockchain), typeof(InitializePlugins))]
    public class LoadGenesisBlock : IStep
    {
        private readonly IApiWithBlockchain _api;
        private readonly ILogger _logger;
        private IInitConfig? _initConfig;
        private readonly TimeSpan _genesisProcessedTimeout;

        public LoadGenesisBlock(INethermindApi api)
        {
            _api = api;
            _logger = _api.LogManager.GetClassLogger();
            _genesisProcessedTimeout = TimeSpan.FromMilliseconds(_api.Config<IBlocksConfig>().GenesisTimeoutMs);
        }

        public async Task Execute(CancellationToken cancellationToken)
        {
            _initConfig = _api.Config<IInitConfig>();

            if (_api.BlockTree is null)
            {
                throw new StepDependencyException();
            }

            IMainProcessingContext mainProcessingContext = _api.MainProcessingContext!;

            // if we already have a database with blocks then we do not need to load genesis from spec
            if (_api.BlockTree.Genesis is null)
            {
                using var _ = mainProcessingContext.WorldState.BeginScope(IWorldState.PreGenesis);

                await Load(mainProcessingContext);
            }

            if (!_initConfig.ProcessingEnabled)
            {
                if (_logger.IsWarn) _logger.Warn($"Shutting down the blockchain processor due to {nameof(InitConfig)}.{nameof(InitConfig.ProcessingEnabled)} set to false");
                await (_api.MainProcessingContext!.BlockchainProcessor?.StopAsync() ?? Task.CompletedTask);
            }
        }

        protected virtual Task Load(IMainProcessingContext mainProcessingContext)
        {
            if (_api.ChainSpec is null) throw new StepDependencyException(nameof(_api.ChainSpec));
            if (_api.BlockTree is null) throw new StepDependencyException(nameof(_api.BlockTree));
            if (_api.SpecProvider is null) throw new StepDependencyException(nameof(_api.SpecProvider));

            Block genesis = new GenesisLoader(
                _api.ChainSpec,
                _api.SpecProvider,
                _api.StateReader!,
                mainProcessingContext.WorldState,
                mainProcessingContext.TransactionProcessor,
                _api.GenesisPostProcessor,
                _api.LogManager,
                string.IsNullOrWhiteSpace(_initConfig?.GenesisHash) ? null : new Hash256(_initConfig.GenesisHash))
                .Load();

            ManualResetEventSlim genesisProcessedEvent = new(false);

            void GenesisProcessed(object? sender, BlockEventArgs args)
            {
                if (_api.BlockTree is null) throw new StepDependencyException(nameof(_api.BlockTree));
                _api.BlockTree.NewHeadBlock -= GenesisProcessed;
                genesisProcessedEvent.Set();
            }

            _api.BlockTree.NewHeadBlock += GenesisProcessed;
            _api.BlockTree.SuggestBlock(genesis);
            bool genesisLoaded = genesisProcessedEvent.Wait(_genesisProcessedTimeout);

            if (!genesisLoaded)
            {
                throw new TimeoutException($"Genesis block was not processed after {_genesisProcessedTimeout.TotalSeconds} seconds. If you are running custom chain with very big genesis file consider increasing {nameof(BlocksConfig)}.{nameof(IBlocksConfig.GenesisTimeoutMs)}.");
            }

            return Task.CompletedTask;
        }
    }
}
