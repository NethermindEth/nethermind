//  Copyright (c) 2018 Demerzel Solutions Limited
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
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.Runner.Ethereum.Context;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;

namespace Nethermind.Runner.Ethereum.Steps
{
    [RunnerStepDependencies(typeof(StartBlockProcessor), typeof(InitializeBlockchain))]
    public class LoadGenesisBlock : IStep
    {
        private readonly EthereumRunnerContext _context;
        private ILogger _logger;

        public LoadGenesisBlock(EthereumRunnerContext context)
        {
            _context = context;
            _logger = _context.LogManager.GetClassLogger();
        }

        public Task Execute()
        {
            IInitConfig initConfig = _context.Config<IInitConfig>();
            Keccak? expectedGenesisHash = string.IsNullOrWhiteSpace(initConfig.GenesisHash) ? null : new Keccak(initConfig.GenesisHash);

            if (_context.BlockTree == null)
            {
                throw new StepDependencyException();
            }
            
            // if we already have a database with blocks then we do not need to load genesis from spec
            if (_context.BlockTree.Genesis == null)
            {
                Load();    
            }
            
            ValidateGenesisHash(expectedGenesisHash);
            return Task.CompletedTask;
        }

        protected virtual void Load()
        {
            if (_context.ChainSpec == null) throw new StepDependencyException(nameof(_context.ChainSpec));
            if (_context.BlockTree == null) throw new StepDependencyException(nameof(_context.BlockTree));
            if (_context.StateProvider == null) throw new StepDependencyException(nameof(_context.StateProvider));
            if (_context.StorageProvider == null) throw new StepDependencyException(nameof(_context.StorageProvider));
            if (_context.SpecProvider == null) throw new StepDependencyException(nameof(_context.SpecProvider));
            if (_context.DbProvider == null) throw new StepDependencyException(nameof(_context.DbProvider));
            if (_context.TransactionProcessor == null) throw new StepDependencyException(nameof(_context.TransactionProcessor));

            var genesis = new GenesisLoader(
                _context.ChainSpec,
                _context.SpecProvider,
                _context.StateProvider,
                _context.StorageProvider,
                _context.DbProvider,
                _context.TransactionProcessor)
                .Load();

            ManualResetEventSlim genesisProcessedEvent = new ManualResetEventSlim(false);

            bool genesisLoaded = false;
            void GenesisProcessed(object? sender, BlockEventArgs args)
            {
                if (_context.BlockTree == null) throw new StepDependencyException(nameof(_context.BlockTree));
                _context.BlockTree.NewHeadBlock -= GenesisProcessed;
                genesisLoaded = true;
                genesisProcessedEvent.Set();
            }

            _context.BlockTree.NewHeadBlock += GenesisProcessed;
            _context.BlockTree.SuggestBlock(genesis);
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
            if (_context.StateProvider == null) throw new StepDependencyException(nameof(_context.StateProvider));
            if (_context.BlockTree == null) throw new StepDependencyException(nameof(_context.BlockTree));
            
            if (expectedGenesisHash != null && _context.BlockTree.Genesis.Hash != expectedGenesisHash)
            {
                if (_logger.IsWarn) _logger.Warn(_context.StateProvider.DumpState());
                if (_logger.IsWarn) _logger.Warn(_context.BlockTree.Genesis.ToString(BlockHeader.Format.Full));
                if (_logger.IsError) _logger.Error($"Unexpected genesis hash, expected {expectedGenesisHash}, but was {_context.BlockTree.Genesis.Hash}");
            }
            else
            {
                if (_logger.IsDebug) _logger.Debug($"Genesis hash :  {_context.BlockTree.Genesis.Hash}");
            }
            
            ThisNodeInfo.AddInfo("Genesis hash :", $"{_context.BlockTree.Genesis.Hash}");
        }
    }
}