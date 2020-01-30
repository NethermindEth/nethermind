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
using Nethermind.Evm.Tracing;
using Nethermind.Runner.Ethereum.Context;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Runner.Ethereum.Steps
{
    [RunnerStepDependency(typeof(InitializeBlockchain))]
    public class LoadGenesisBlock : IStep
    {
        private readonly EthereumRunnerContext _context;

        public LoadGenesisBlock(EthereumRunnerContext context)
        {
            _context = context;
        }

        public Task Execute()
        {
            var initConfig = _context.Config<IInitConfig>();
            var expectedGenesisHash = string.IsNullOrWhiteSpace(initConfig.GenesisHash) ? null : new Keccak(initConfig.GenesisHash);
            
            // if we already have a database with blocks then we do not need to load genesis from spec
            if (_context.BlockTree.Genesis != null)
            {
                ValidateGenesisHash(expectedGenesisHash);
            }
            else
            {
                Load(expectedGenesisHash);    
            }

            return Task.CompletedTask;
        }

        protected virtual void Load(Keccak expectedGenesisHash)
        {
            Block genesis = _context.ChainSpec.Genesis;

            foreach ((Address address, ChainSpecAllocation allocation) in _context.ChainSpec.Allocations)
            {
                _context.StateProvider.CreateAccount(address, allocation.Balance);
                if (allocation.Code != null)
                {
                    Keccak codeHash = _context.StateProvider.UpdateCode(allocation.Code);
                    _context.StateProvider.UpdateCodeHash(address, codeHash, _context.SpecProvider.GenesisSpec);
                }

                if (allocation.Constructor != null)
                {
                    Transaction constructorTransaction = new Transaction(true)
                    {
                        SenderAddress = address,
                        Init = allocation.Constructor,
                        GasLimit = genesis.GasLimit
                    };
                    _context.TransactionProcessor.Execute(constructorTransaction, genesis.Header, NullTxTracer.Instance);
                }
            }

            _context.StorageProvider.Commit();
            _context.StateProvider.Commit(_context.SpecProvider.GenesisSpec);

            _context.StorageProvider.CommitTrees();
            _context.StateProvider.CommitTree();

            _context.DbProvider.StateDb.Commit();
            _context.DbProvider.CodeDb.Commit();

            genesis.Header.StateRoot = _context.StateProvider.StateRoot;
            genesis.Header.Hash = genesis.Header.CalculateHash();

            ManualResetEventSlim genesisProcessedEvent = new ManualResetEventSlim(false);

            bool genesisLoaded = false;

            void GenesisProcessed(object sender, BlockEventArgs args)
            {
                genesisLoaded = true;
                _context.BlockTree.NewHeadBlock -= GenesisProcessed;
                genesisProcessedEvent.Set();
            }

            _context.BlockTree.NewHeadBlock += GenesisProcessed;
            _context.BlockTree.SuggestBlock(genesis);
            genesisProcessedEvent.Wait(TimeSpan.FromSeconds(40));
            if (!genesisLoaded)
            {
                throw new BlockchainException("Genesis block processing failure");
            }

            ValidateGenesisHash(expectedGenesisHash);
        }

        /// <summary>
        /// If <paramref name="expectedGenesisHash"/> is <value>null</value> then it means that we do not care about the genesis hash (e.g. in some quick testing of private chains)/>
        /// </summary>
        /// <param name="expectedGenesisHash"></param>
        private void ValidateGenesisHash(Keccak expectedGenesisHash)
        {
            if (expectedGenesisHash != null && _context.BlockTree.Genesis.Hash != expectedGenesisHash)
            {
                if (_context.Logger.IsWarn) _context.Logger.Warn(_context.StateProvider.DumpState());
                if (_context.Logger.IsWarn) _context.Logger.Warn(_context.BlockTree.Genesis.ToString(BlockHeader.Format.Full));
                if (_context.Logger.IsError) _context.Logger.Error($"Unexpected genesis hash, expected {expectedGenesisHash}, but was {_context.BlockTree.Genesis.Hash}");
            }
            else
            {
                if (_context.Logger.IsDebug) _context.Logger.Debug($"Genesis hash :  {_context.BlockTree.Genesis.Hash}");
                ThisNodeInfo.AddInfo("Genesis hash :", $"{_context.BlockTree.Genesis.Hash}");
            }
        }
    }
}