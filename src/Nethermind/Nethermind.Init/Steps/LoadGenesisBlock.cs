//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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

        public LoadGenesisBlock(INethermindApi api)
        {
            _api = api;
            _logger = _api.LogManager.GetClassLogger();
        }

        public async Task Execute(CancellationToken _)
        {
            _initConfig = _api.Config<IInitConfig>();
            Keccak? expectedGenesisHash = string.IsNullOrWhiteSpace(_initConfig.GenesisHash) ? null : new Keccak(_initConfig.GenesisHash);

            if (_api.BlockTree == null)
            {
                throw new StepDependencyException();
            }
            
            // if we already have a database with blocks then we do not need to load genesis from spec
            if (_api.BlockTree.Genesis == null)
            {
                Load();    
            }
            
            ValidateGenesisHash(expectedGenesisHash);
            
            if(!_initConfig.ProcessingEnabled)
            {
                if (_logger.IsWarn) _logger.Warn($"Shutting down the blockchain processor due to {nameof(InitConfig)}.{nameof(InitConfig.ProcessingEnabled)} set to false");
                await (_api.BlockchainProcessor?.StopAsync() ?? Task.CompletedTask);
            }
        }

        protected virtual void Load()
        {
            if (_api.ChainSpec == null) throw new StepDependencyException(nameof(_api.ChainSpec));
            if (_api.BlockTree == null) throw new StepDependencyException(nameof(_api.BlockTree));
            if (_api.StateProvider == null) throw new StepDependencyException(nameof(_api.StateProvider));
            if (_api.StorageProvider == null) throw new StepDependencyException(nameof(_api.StorageProvider));
            if (_api.SpecProvider == null) throw new StepDependencyException(nameof(_api.SpecProvider));
            if (_api.DbProvider == null) throw new StepDependencyException(nameof(_api.DbProvider));
            if (_api.TransactionProcessor == null) throw new StepDependencyException(nameof(_api.TransactionProcessor));

            Block genesis = new GenesisLoader(
                _api.ChainSpec,
                _api.SpecProvider,
                _api.StateProvider,
                _api.StorageProvider,
                _api.TransactionProcessor)
                .Load();

            ManualResetEventSlim genesisProcessedEvent = new(false);

            bool genesisLoaded = false;
            void GenesisProcessed(object? sender, BlockEventArgs args)
            {
                if (_api.BlockTree == null) throw new StepDependencyException(nameof(_api.BlockTree));
                _api.BlockTree.NewHeadBlock -= GenesisProcessed;
                genesisLoaded = true;
                genesisProcessedEvent.Set();
            }

            _api.BlockTree.NewHeadBlock += GenesisProcessed;
            _api.BlockTree.SuggestBlock(genesis);
            genesisProcessedEvent.Wait(TimeSpan.FromSeconds(40));
            
            if (!genesisLoaded)
            {
                throw new BlockchainException("Genesis block processing failure");
            }
        }

        /// <summary>
        /// If <paramref name="expectedGenesisHash"/> is <value>null</value> then it means that we do not care about the genesis hash (e.g. in some quick testing of private chains)/>
        /// </summary>
        /// <param name="expectedGenesisHash"></param>
        private void ValidateGenesisHash(Keccak? expectedGenesisHash)
        {
            if (_api.StateProvider == null) throw new StepDependencyException(nameof(_api.StateProvider));
            if (_api.BlockTree == null) throw new StepDependencyException(nameof(_api.BlockTree));
            
            if (expectedGenesisHash != null && _api.BlockTree.Genesis.Hash != expectedGenesisHash)
            {
                if (_logger.IsWarn) _logger.Warn(_api.StateProvider.DumpState());
                if (_logger.IsWarn) _logger.Warn(_api.BlockTree.Genesis.ToString(BlockHeader.Format.Full));
                if (_logger.IsError) _logger.Error($"Unexpected genesis hash, expected {expectedGenesisHash}, but was {_api.BlockTree.Genesis.Hash}");
            }
            else
            {
                if (_logger.IsDebug) _logger.Info($"Genesis hash :  {_api.BlockTree.Genesis.Hash}");
            }
            
            ThisNodeInfo.AddInfo("Genesis hash :", $"{_api.BlockTree.Genesis.Hash}");
        }
    }
}
