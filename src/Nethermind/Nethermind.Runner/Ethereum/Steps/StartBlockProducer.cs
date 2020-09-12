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
using Nethermind.Runner.Ethereum.Api;

namespace Nethermind.Runner.Ethereum.Steps
{
    [RunnerStepDependencies(typeof(StartBlockProcessor), typeof(SetupKeyStore), typeof(ReviewBlockTree))]
    public abstract class StartBlockProducer : IStep
    {
        private readonly NethermindApi _api;
        private BlockProducerContext? _blockProducerContext;
        
        protected StartBlockProducer(NethermindApi api)
        {
            _api = api;
        }

        public Task Execute(CancellationToken _)
        {
            IInitConfig initConfig = _api.Config<IInitConfig>();
            if (initConfig.IsMining)
            {
                BuildProducer();
                if (_api.BlockProducer == null) throw new StepDependencyException(nameof(_api.BlockProducer));

                _api.BlockProducer.Start();
            }

            return Task.CompletedTask;
        }

        protected virtual void BuildProducer()
        {
            if (_api.ChainSpec == null) throw new StepDependencyException(nameof(_api.ChainSpec));
            throw new NotSupportedException($"Mining in {_api.ChainSpec.SealEngineType} mode is not supported");
        }

        protected BlockProducerContext GetProducerChain()
        {
            BlockProducerContext Create()
            {
                ReadOnlyDbProvider dbProvider = new ReadOnlyDbProvider(_api.DbProvider, false);
                ReadOnlyBlockTree blockTree = new ReadOnlyBlockTree(_api.BlockTree);
                ReadOnlyTxProcessingEnv txProcessingEnv =
                    new ReadOnlyTxProcessingEnv(dbProvider, _api.TrieStore, blockTree, _api.SpecProvider, _api.LogManager);
                
                ReadOnlyTxProcessorSource txProcessorSource =
                    new ReadOnlyTxProcessorSource(txProcessingEnv);
                
                BlockProcessor blockProcessor =
                    CreateBlockProcessor(txProcessingEnv, txProcessorSource, dbProvider);
                
                IBlockchainProcessor blockchainProcessor =
                    new BlockchainProcessor(
                        blockTree,
                        blockProcessor,
                        _api.RecoveryStep,
                        _api.LogManager,
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
            ITxSource innerSource = new TxPoolTxSource(_api.TxPool, processingEnv.StateReader, _api.LogManager);
            return new FilteredTxSource(innerSource, CreateGasPriceTxFilter(readOnlyTxProcessorSource));
        }

        protected virtual ITxFilter CreateGasPriceTxFilter(ReadOnlyTxProcessorSource readOnlyTxProcessorSource)
        {
            UInt256 minGasPrice = _api.Config<IMiningConfig>().MinGasPrice;
            return new MinGasPriceTxFilter(minGasPrice);
        }

        protected virtual BlockProcessor CreateBlockProcessor(
            ReadOnlyTxProcessingEnv readOnlyTxProcessingEnv, 
            ReadOnlyTxProcessorSource readOnlyTxProcessorSource, 
            IReadOnlyDbProvider readOnlyDbProvider)
        {
            if (_api.SpecProvider == null) throw new StepDependencyException(nameof(_api.SpecProvider));
            if (_api.BlockValidator == null) throw new StepDependencyException(nameof(_api.BlockValidator));
            if (_api.RewardCalculatorSource == null) throw new StepDependencyException(nameof(_api.RewardCalculatorSource));
            if (_api.ReceiptStorage == null) throw new StepDependencyException(nameof(_api.ReceiptStorage));
            if (_api.TxPool == null) throw new StepDependencyException(nameof(_api.TxPool));

            return new BlockProcessor(
                _api.SpecProvider,
                _api.BlockValidator,
                _api.RewardCalculatorSource.Get(readOnlyTxProcessingEnv.TransactionProcessor),
                readOnlyTxProcessingEnv.TransactionProcessor,
                readOnlyDbProvider.StateDb,
                readOnlyDbProvider.CodeDb,
                readOnlyTxProcessingEnv.StateProvider,
                readOnlyTxProcessingEnv.StorageProvider,
                _api.TxPool,
                _api.ReceiptStorage,
                _api.LogManager);
        }
    }
}
