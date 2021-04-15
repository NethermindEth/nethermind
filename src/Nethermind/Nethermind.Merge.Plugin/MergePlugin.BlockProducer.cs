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
// 

using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Producers;
using Nethermind.Consensus;
using Nethermind.Consensus.Transactions;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Runner.Ethereum.Steps;
using Nethermind.State;

namespace Nethermind.Merge.Plugin
{
    public partial class MergePlugin
    {
        private IMiningConfig _miningConfig = null!;
        private Eth2BlockProducer _blockProducer = null!;

        public Task InitBlockProducer()
        {
            _miningConfig = _api.Config<IMiningConfig>();
            if (_miningConfig.Enabled)
            {
                if (_api.EngineSigner == null) throw new StepDependencyException(nameof(_api.EngineSigner));
                if (_api.ChainSpec == null) throw new StepDependencyException(nameof(_api.ChainSpec));
                if (_api.BlockTree == null) throw new StepDependencyException(nameof(_api.BlockTree));
                if (_api.BlockProcessingQueue == null) throw new StepDependencyException(nameof(_api.BlockProcessingQueue));
                if (_api.StateProvider == null) throw new StepDependencyException(nameof(_api.StateProvider));
                if (_api.SpecProvider == null) throw new StepDependencyException(nameof(_api.SpecProvider));

                ILogger logger = _api.LogManager.GetClassLogger();
                if (logger.IsWarn) logger.Warn("Starting ETH2 block producer & sealer");

                BlockProducerContext producerContext = GetProducerChain();
                _api.BlockProducer = _blockProducer = new Eth2BlockProducer(
                    producerContext.TxSource,
                    producerContext.ChainProcessor,
                    _api.BlockTree,
                    _api.BlockProcessingQueue,
                    _api.StateProvider,
                    new TargetAdjustedGasLimitCalculator(_api.SpecProvider, _miningConfig),
                    _api.EngineSigner,
                    _api.LogManager);
            }
            
            return Task.CompletedTask;
        }
        
        private BlockProducerContext GetProducerChain()
        {
            BlockProducerContext Create()
            {
                ReadOnlyDbProvider dbProvider = _api.DbProvider.AsReadOnly(false);
                ReadOnlyBlockTree blockTree = _api.BlockTree.AsReadOnly();

                ReadOnlyTxProcessingEnv txProcessingEnv =
                    new(dbProvider, _api.ReadOnlyTrieStore, blockTree, _api.SpecProvider, _api.LogManager);
                
                BlockProcessor blockProcessor =
                    CreateBlockProcessor(txProcessingEnv);

                IBlockchainProcessor blockchainProcessor =
                    new BlockchainProcessor(
                        blockTree,
                        blockProcessor,
                        _api.BlockPreprocessor,
                        _api.LogManager,
                        BlockchainProcessor.Options.NoReceipts);

                OneTimeChainProcessor chainProcessor = new(
                    dbProvider,
                    blockchainProcessor);

                return new BlockProducerContext
                {
                    ChainProcessor = chainProcessor,
                    ReadOnlyStateProvider = txProcessingEnv.StateProvider,
                    TxSource = CreateTxSourceForProducer(txProcessingEnv, txProcessingEnv),
                    ReadOnlyTxProcessingEnv = txProcessingEnv
                };
            }

            return Create();
        }

        private ITxSource CreateTxSourceForProducer(
            ReadOnlyTxProcessingEnv processingEnv,
            IReadOnlyTxProcessorSource readOnlyTxProcessorSource) =>
            CreateTxPoolTxSource(processingEnv, readOnlyTxProcessorSource);

        private TxPoolTxSource CreateTxPoolTxSource(ReadOnlyTxProcessingEnv processingEnv, IReadOnlyTxProcessorSource readOnlyTxProcessorSource)
        {
            ITxFilter txSourceFilter = CreateTxSourceFilter();
            return new TxPoolTxSource(_api.TxPool, processingEnv.StateReader, _api.LogManager, txSourceFilter);
        }

        private ITxFilter CreateTxSourceFilter() =>
            TxFilterBuilders.CreateStandardTxFilter(_miningConfig);

        private BlockProcessor CreateBlockProcessor(
            ReadOnlyTxProcessingEnv readOnlyTxProcessingEnv)
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
                _api.TxPool,
                _api.ReceiptStorage,
                NullWitnessCollector.Instance,
                _api.LogManager);
        }
        
    }
}
