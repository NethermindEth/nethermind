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

using System;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Comparers;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Producers;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Services;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules;
using Nethermind.State;
using Nethermind.Trie.Pruning;

namespace Nethermind.Consensus.Clique
{
    public class CliquePlugin : IConsensusPlugin
    {
        public string Name => "Clique";

        public string Description => "Clique Consensus Engine";

        public string Author => "Nethermind";

        public Task Init(INethermindApi nethermindApi)
        {
            _nethermindApi = nethermindApi;
            if (_nethermindApi!.SealEngineType != Nethermind.Core.SealEngineType.Clique)
            {
                return Task.CompletedTask;
            }

            var (getFromApi, setInApi) = _nethermindApi.ForInit;

            _cliqueConfig = new CliqueConfig
            {
                BlockPeriod = getFromApi!.ChainSpec!.Clique.Period,
                Epoch = getFromApi.ChainSpec.Clique.Epoch
            };

            _snapshotManager = new SnapshotManager(
                _cliqueConfig,
                getFromApi.DbProvider!.BlocksDb,
                getFromApi.BlockTree!,
                getFromApi.EthereumEcdsa!,
                getFromApi.LogManager);
            
            setInApi.HealthHintService = new CliqueHealthHintService(_snapshotManager, getFromApi.ChainSpec);

            setInApi.SealValidator = new CliqueSealValidator(
                _cliqueConfig,
                _snapshotManager,
                getFromApi.LogManager);

            setInApi.RewardCalculatorSource = NoBlockRewards.Instance;
            setInApi.BlockPreprocessor.AddLast(new AuthorRecoveryStep(_snapshotManager!));

            return Task.CompletedTask;
        }

        public Task<IBlockProducer> InitBlockProducer(IBlockProductionTrigger? blockProductionTrigger = null, ITxSource? additionalTxSource = null)
        {
            if (_nethermindApi!.SealEngineType != Nethermind.Core.SealEngineType.Clique)
            {
                return Task.FromResult((IBlockProducer)null);
            }

            var (getFromApi, setInApi) = _nethermindApi!.ForProducer;
            
            _miningConfig = getFromApi.Config<IMiningConfig>();
            if (!_miningConfig.Enabled)
            {
                throw new InvalidOperationException("Request to start block producer while mining disabled.");
            }

            setInApi.Sealer = new CliqueSealer(
                getFromApi.EngineSigner!,
                _cliqueConfig!,
                _snapshotManager!,
                getFromApi.LogManager);
            
            ReadOnlyDbProvider readOnlyDbProvider = getFromApi.DbProvider.AsReadOnly(false);
            ReadOnlyBlockTree readOnlyBlockTree = getFromApi.BlockTree.AsReadOnly();
            ITransactionComparerProvider transactionComparerProvider = getFromApi.TransactionComparerProvider;

            ReadOnlyTxProcessingEnv producerEnv = new ReadOnlyTxProcessingEnv(
                readOnlyDbProvider,
                getFromApi.ReadOnlyTrieStore,
                readOnlyBlockTree,
                getFromApi.SpecProvider,
                getFromApi.LogManager);
                
            BlockProcessor producerProcessor = new BlockProcessor(
                getFromApi!.SpecProvider,
                getFromApi!.BlockValidator,
                NoBlockRewards.Instance,
                getFromApi.BlockProducerEnvFactory.TransactionsExecutorFactory.Create(producerEnv),
                producerEnv.StateProvider,
                producerEnv.StorageProvider, // do not remove transactions from the pool when preprocessing
                NullReceiptStorage.Instance,
                NullWitnessCollector.Instance,
                getFromApi.LogManager);

            IBlockchainProcessor producerChainProcessor = new BlockchainProcessor(
                readOnlyBlockTree,
                producerProcessor,
                getFromApi.BlockPreprocessor,
                getFromApi.LogManager,
                BlockchainProcessor.Options.NoReceipts);

            OneTimeChainProcessor chainProcessor = new OneTimeChainProcessor(
                readOnlyDbProvider,
                producerChainProcessor);

            ITxFilterPipeline txFilterPipeline =
                TxFilterPipelineBuilder.CreateStandardFilteringPipeline(
                    _nethermindApi.LogManager,
                    getFromApi.SpecProvider,
                    _miningConfig);

            TxPoolTxSource txPoolTxSource = new(
                getFromApi.TxPool,
                getFromApi.SpecProvider,
                transactionComparerProvider,
                getFromApi.LogManager,
                txFilterPipeline);

            IGasLimitCalculator gasLimitCalculator = setInApi.GasLimitCalculator = new TargetAdjustedGasLimitCalculator(getFromApi.SpecProvider, _miningConfig);
            
            IBlockProducer blockProducer = new CliqueBlockProducer(
                additionalTxSource.Then(txPoolTxSource),
                chainProcessor,
                producerEnv.StateProvider,
                getFromApi.BlockTree!,
                getFromApi.Timestamper,
                getFromApi.CryptoRandom,
                _snapshotManager!,
                getFromApi.Sealer!,
                gasLimitCalculator,
                getFromApi.SpecProvider,
                _cliqueConfig!,
                getFromApi.LogManager);

            return Task.FromResult(blockProducer);
        }

        public Task InitNetworkProtocol()
        {
            return Task.CompletedTask;
        }

        public Task InitRpcModules()
        {
            if (_nethermindApi!.SealEngineType != Nethermind.Core.SealEngineType.Clique)
            {
                return Task.CompletedTask;
            }

            var (getFromApi, _) = _nethermindApi!.ForRpc;
            CliqueRpcRpcModule cliqueRpcRpcModule = new CliqueRpcRpcModule(
                getFromApi!.BlockProducer as ICliqueBlockProducer,
                _snapshotManager!,
                getFromApi.BlockTree!);

            var modulePool = new SingletonModulePool<ICliqueRpcModule>(cliqueRpcRpcModule);
            getFromApi.RpcModuleProvider.Register(modulePool);

            return Task.CompletedTask;
        }

        public string SealEngineType => Nethermind.Core.SealEngineType.Clique;
        
        [Todo("Redo clique producer to support triggers and MEV")]
        public IBlockProductionTrigger DefaultBlockProductionTrigger => _nethermindApi.ManualBlockProductionTrigger;

        public ValueTask DisposeAsync() { return ValueTask.CompletedTask; }

        private INethermindApi? _nethermindApi;

        private ISnapshotManager? _snapshotManager;

        private ICliqueConfig? _cliqueConfig;

        private IMiningConfig? _miningConfig;
    }
}
