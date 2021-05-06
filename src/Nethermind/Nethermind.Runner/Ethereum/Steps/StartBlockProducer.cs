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
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Producers;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Consensus.Transactions;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.State;
using Nethermind.Logging;

namespace Nethermind.Runner.Ethereum.Steps
{
    [RunnerStepDependencies(typeof(StartBlockProcessor), typeof(SetupKeyStore), typeof(ReviewBlockTree))]
    public class StartBlockProducer : IStep
    {
        protected IApiWithBlockchain _api;
        private BlockProducerEnv? _blockProducerContext;

        public StartBlockProducer(INethermindApi api)
        {
            _api = api;
        }

        public async Task Execute(CancellationToken _)
        {
            IMiningConfig miningConfig = _api.Config<IMiningConfig>();
            if (miningConfig.Enabled)
            {
                await BuildProducer();
                
                if (_api.BlockProducer == null) throw new StepDependencyException(nameof(_api.BlockProducer));

                ILogger logger = _api.LogManager.GetClassLogger();
                if (logger.IsWarn) logger.Warn($"Starting {_api.SealEngineType} block producer & sealer");
                _api.BlockProducer.Start();
            }
        }

        protected virtual async Task BuildProducer()
        {
            _api.BlockProducerEnvFactory = new BlockProducerEnvFactory(_api.DbProvider,
                _api.BlockTree,
                _api.ReadOnlyTrieStore,
                _api.SpecProvider,
                _api.BlockValidator,
                _api.RewardCalculatorSource,
                _api.ReceiptStorage,
                _api.BlockPreprocessor,
                _api.TxPool,
                _api.Config<IMiningConfig>(),
                _api.LogManager);
            
            if (_api.ChainSpec == null) throw new StepDependencyException(nameof(_api.ChainSpec));
            IConsensusPlugin? consensusPlugin = _api.GetConsensusPlugin();
            
            if (consensusPlugin != null)
            {
                bool shouldInitPluginDirectly = true;
                foreach (IConsensusWrapperPlugin wrapperPlugin in _api.GetConsensusWrapperPlugins())
                {
                    shouldInitPluginDirectly = false;
                    await wrapperPlugin.InitBlockProducer(consensusPlugin);
                }

                if (shouldInitPluginDirectly)
                {
                    await consensusPlugin.InitBlockProducer();
                }
            }
            else
            {
                throw new NotSupportedException($"Mining in {_api.ChainSpec.SealEngineType} mode is not supported");    
            }
        }

        // TODO: Use BlockProducerEnvFactory
        protected BlockProducerEnv GetProducerChain()
        {
            BlockProducerEnv Create()
            {
                ReadOnlyDbProvider dbProvider = _api.DbProvider.AsReadOnly(false);
                ReadOnlyBlockTree blockTree = _api.BlockTree.AsReadOnly();

                ReadOnlyTxProcessingEnv txProcessingEnv =
                    new ReadOnlyTxProcessingEnv(dbProvider, _api.ReadOnlyTrieStore, blockTree, _api.SpecProvider, _api.LogManager);
                
                BlockProcessor blockProcessor =
                    CreateBlockProcessor(txProcessingEnv, txProcessingEnv);

                IBlockchainProcessor blockchainProcessor =
                    new BlockchainProcessor(
                        blockTree,
                        blockProcessor,
                        _api.BlockPreprocessor,
                        _api.LogManager,
                        BlockchainProcessor.Options.NoReceipts);

                OneTimeChainProcessor chainProcessor = new OneTimeChainProcessor(
                    dbProvider,
                    blockchainProcessor);

                return new BlockProducerEnv
                {
                    ChainProcessor = chainProcessor,
                    ReadOnlyStateProvider = txProcessingEnv.StateProvider,
                    TxSource = CreateTxSourceForProducer(txProcessingEnv, txProcessingEnv),
                    ReadOnlyTxProcessingEnv = txProcessingEnv
                };
            }

            return _blockProducerContext ??= Create();
        }

        protected virtual ITxSource CreateTxSourceForProducer(
            ReadOnlyTxProcessingEnv processingEnv,
            IReadOnlyTxProcessorSource readOnlyTxProcessorSource) =>
            CreateTxPoolTxSource(processingEnv, readOnlyTxProcessorSource);

        protected virtual TxPoolTxSource CreateTxPoolTxSource(ReadOnlyTxProcessingEnv processingEnv, IReadOnlyTxProcessorSource readOnlyTxProcessorSource)
        {
            ITxFilter txSourceFilter = CreateTxSourceFilter(processingEnv, readOnlyTxProcessorSource);
            return new TxPoolTxSource(_api.TxPool, processingEnv.StateReader, _api.LogManager, txSourceFilter);
        }

        protected virtual ITxFilter CreateTxSourceFilter(ReadOnlyTxProcessingEnv readOnlyTxProcessingEnv, IReadOnlyTxProcessorSource readOnlyTxProcessorSource) =>
            Blockchain.TxFilterBuilders.CreateStandardTxFilter(_api.Config<IMiningConfig>());

        protected virtual BlockProcessor CreateBlockProcessor(
            ReadOnlyTxProcessingEnv readOnlyTxProcessingEnv,
            IReadOnlyTxProcessorSource readOnlyTxProcessorSource)
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
                readOnlyTxProcessingEnv.StateProvider,
                readOnlyTxProcessingEnv.StorageProvider,
                _api.ReceiptStorage,
                NullWitnessCollector.Instance,
                _api.LogManager);
        }
    }
}
