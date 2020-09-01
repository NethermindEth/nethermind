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
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Producers;
using Nethermind.Consensus;
using Nethermind.Consensus.Transactions;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Runner.Ethereum.Context;

namespace Nethermind.Runner.Ethereum.Steps
{
    [RunnerStepDependencies(typeof(StartBlockProcessor), typeof(SetupKeyStore), typeof(ReviewBlockTree))]
    public abstract class StartBlockProducer : IStep
    {
        private readonly EthereumRunnerContext _context;
        private BlockProducerContext? _blockProducerContext;
        
        protected StartBlockProducer(EthereumRunnerContext context)
        {
            _context = context;
        }

        public Task Execute(CancellationToken _)
        {
            IInitConfig initConfig = _context.Config<IInitConfig>();
            if (initConfig.IsMining)
            {
                BuildProducer();
                if (_context.BlockProducer == null) throw new StepDependencyException(nameof(_context.BlockProducer));

                _context.BlockProducer.Start();
            }

            return Task.CompletedTask;
        }

        protected virtual void BuildProducer()
        {
            if (_context.ChainSpec == null) throw new StepDependencyException(nameof(_context.ChainSpec));
            throw new NotSupportedException($"Mining in {_context.ChainSpec.SealEngineType} mode is not supported");
        }

        protected BlockProducerContext GetProducerChain()
        {
            BlockProducerContext Create()
            {
                ReadOnlyDbProvider dbProvider = new ReadOnlyDbProvider(_context.DbProvider, false);
                ReadOnlyBlockTree blockTree = new ReadOnlyBlockTree(_context.BlockTree);
                ReadOnlyTxProcessingEnv txProcessingEnv =
                    new ReadOnlyTxProcessingEnv(dbProvider, blockTree, _context.SpecProvider, _context.LogManager);
                
                ReadOnlyTxProcessorSource txProcessorSource =
                    new ReadOnlyTxProcessorSource(txProcessingEnv);
                
                BlockProcessor blockProcessor =
                    CreateBlockProcessor(txProcessingEnv, txProcessorSource, dbProvider);
                
                IBlockchainProcessor blockchainProcessor =
                    new BlockchainProcessor(
                        blockTree,
                        blockProcessor,
                        _context.RecoveryStep,
                        _context.LogManager,
                        BlockchainProcessor.Options.NoReceipts);
                
                OneTimeChainProcessor chainProcessor = new OneTimeChainProcessor(
                    dbProvider,
                    blockchainProcessor);

                return new BlockProducerContext
                {
                    ChainProcessor = chainProcessor,
                    ReadOnlyStateProvider = txProcessingEnv.StateProvider,
                    TxSource = CreateTxSourceForProducer(txProcessingEnv, txProcessorSource),
                    ReadOnlyTxProcessingEnv = txProcessingEnv,
                    ReadOnlyTxProcessorSource = txProcessorSource
                };
            }

            return _blockProducerContext ??= Create();
        }

        protected virtual ITxSource CreateTxSourceForProducer(
            ReadOnlyTxProcessingEnv processingEnv,
            ReadOnlyTxProcessorSource readOnlyTxProcessorSource)
        {
            ITxSource innerSource = new TxPoolTxSource(_context.TxPool, processingEnv.StateReader, _context.LogManager);
            return new FilteredTxSource(innerSource, CreateGasPriceTxFilter(readOnlyTxProcessorSource));
        }

        protected virtual ITxFilter CreateGasPriceTxFilter(ReadOnlyTxProcessorSource readOnlyTxProcessorSource)
        {
            UInt256 minGasPrice = _context.Config<IMiningConfig>().MinGasPrice;
            return new MinGasPriceTxFilter(minGasPrice);
        }

        protected virtual BlockProcessor CreateBlockProcessor(
            ReadOnlyTxProcessingEnv readOnlyTxProcessingEnv, 
            ReadOnlyTxProcessorSource readOnlyTxProcessorSource, 
            IReadOnlyDbProvider readOnlyDbProvider)
        {
            if (_context.SpecProvider == null) throw new StepDependencyException(nameof(_context.SpecProvider));
            if (_context.BlockValidator == null) throw new StepDependencyException(nameof(_context.BlockValidator));
            if (_context.RewardCalculatorSource == null) throw new StepDependencyException(nameof(_context.RewardCalculatorSource));
            if (_context.ReceiptStorage == null) throw new StepDependencyException(nameof(_context.ReceiptStorage));
            if (_context.TxPool == null) throw new StepDependencyException(nameof(_context.TxPool));

            return new BlockProcessor(
                _context.SpecProvider,
                _context.BlockValidator,
                _context.RewardCalculatorSource.Get(readOnlyTxProcessingEnv.TransactionProcessor),
                readOnlyTxProcessingEnv.TransactionProcessor,
                readOnlyDbProvider.StateDb,
                readOnlyDbProvider.CodeDb,
                readOnlyTxProcessingEnv.StateProvider,
                readOnlyTxProcessingEnv.StorageProvider,
                _context.TxPool,
                _context.ReceiptStorage,
                _context.LogManager);
        }
    }
}
