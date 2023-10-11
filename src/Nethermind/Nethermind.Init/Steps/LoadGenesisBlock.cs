// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(StartBlockProcessor), typeof(InitializeBlockchain), typeof(InitializePlugins))]
    public class LoadGenesisBlock : IStep
    {
        private readonly IApiWithBlockchain _api;
        private readonly ILogger _logger;
        private IInitConfig? _initConfig;

        readonly TimeSpan _genesisProcessedTimeout = TimeSpan.FromSeconds(40);

        public LoadGenesisBlock(INethermindApi api)
        {
            _api = api;
            _logger = _api.LogManager.GetClassLogger();
        }

        public async Task Execute(CancellationToken _)
        {
            _initConfig = _api.Config<IInitConfig>();
            Keccak? expectedGenesisHash = string.IsNullOrWhiteSpace(_initConfig.GenesisHash) ? null : new Keccak(_initConfig.GenesisHash);

            if (_api.BlockTree is null)
            {
                throw new StepDependencyException();
            }

            // if we already have a database with blocks then we do not need to load genesis from spec
            if (_api.BlockTree.Genesis is null)
            {
                Load();
            }

            ValidateGenesisHash(expectedGenesisHash);

            if (!_initConfig.ProcessingEnabled)
            {
                if (_logger.IsWarn) _logger.Warn($"Shutting down the blockchain processor due to {nameof(InitConfig)}.{nameof(InitConfig.ProcessingEnabled)} set to false");
                await (_api.BlockchainProcessor?.StopAsync() ?? Task.CompletedTask);
            }
        }

        protected virtual void Load()
        {
            if (_api.ChainSpec is null) throw new StepDependencyException(nameof(_api.ChainSpec));
            if (_api.BlockTree is null) throw new StepDependencyException(nameof(_api.BlockTree));
            if (_api.WorldState is null) throw new StepDependencyException(nameof(_api.WorldState));
            if (_api.SpecProvider is null) throw new StepDependencyException(nameof(_api.SpecProvider));
            if (_api.DbProvider is null) throw new StepDependencyException(nameof(_api.DbProvider));
            if (_api.TransactionProcessor is null) throw new StepDependencyException(nameof(_api.TransactionProcessor));

            Block genesis = new GenesisLoader(
                _api.ChainSpec,
                _api.SpecProvider,
                _api.WorldState,
                _api.TransactionProcessor)
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
                throw new TimeoutException($"Genesis block was not processed after {_genesisProcessedTimeout.TotalSeconds} seconds");
            }
        }

        /// <summary>
        /// If <paramref name="expectedGenesisHash"/> is <value>null</value> then it means that we do not care about the genesis hash (e.g. in some quick testing of private chains)/>
        /// </summary>
        /// <param name="expectedGenesisHash"></param>
        private void ValidateGenesisHash(Keccak? expectedGenesisHash)
        {
            if (_api.WorldState is null) throw new StepDependencyException(nameof(_api.WorldState));
            if (_api.BlockTree is null) throw new StepDependencyException(nameof(_api.BlockTree));

            BlockHeader genesis = _api.BlockTree.Genesis!;
            if (expectedGenesisHash is not null && genesis.Hash != expectedGenesisHash)
            {
                if (_logger.IsWarn) _logger.Warn(_api.WorldState.DumpState());
                if (_logger.IsWarn) _logger.Warn(genesis.ToString(BlockHeader.Format.Full));
                if (_logger.IsError) _logger.Error($"Unexpected genesis hash, expected {expectedGenesisHash}, but was {genesis.Hash}");
            }
            else
            {
                if (_logger.IsDebug) _logger.Info($"Genesis hash :  {genesis.Hash}");
            }

            ThisNodeInfo.AddInfo("Genesis hash :", $"{genesis.Hash}");
        }
    }
}
