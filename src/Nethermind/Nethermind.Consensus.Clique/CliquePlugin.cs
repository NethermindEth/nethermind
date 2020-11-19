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
// 

using System;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Producers;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.TxPool;

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
            if (_nethermindApi!.SealEngineType != SealEngineType.Clique)
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

            setInApi.SealValidator = new CliqueSealValidator(
                _cliqueConfig,
                _snapshotManager,
                getFromApi.LogManager);

            setInApi.RewardCalculatorSource = NoBlockRewards.Instance;

            return Task.CompletedTask;
        }

        public Task InitBlockProducer()
        {
            if (_nethermindApi!.SealEngineType != SealEngineType.Clique)
            {
                return Task.CompletedTask;
            }

            var (getFromApi, setInApi) = _nethermindApi!.ForProducer;
            ILogger logger = getFromApi.LogManager.GetClassLogger();
            if (logger.IsWarn) logger.Warn("Starting Clique block producer & sealer");

            setInApi.BlockPreprocessor.AddLast(new AuthorRecoveryStep(_snapshotManager!));

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

            ReadOnlyDbProvider readOnlyDbProvider = new ReadOnlyDbProvider(getFromApi.DbProvider, false);
            ReadOnlyBlockTree readOnlyBlockTree = new ReadOnlyBlockTree(getFromApi.BlockTree);

            ReadOnlyTxProcessingEnv producerEnv = new ReadOnlyTxProcessingEnv(
                readOnlyDbProvider,
                readOnlyBlockTree,
                getFromApi.SpecProvider,
                getFromApi.LogManager);

            BlockProcessor producerProcessor = new BlockProcessor(
                getFromApi!.SpecProvider,
                getFromApi!.BlockValidator,
                NoBlockRewards.Instance,
                producerEnv.TransactionProcessor,
                producerEnv.DbProvider.StateDb,
                producerEnv.DbProvider.CodeDb,
                producerEnv.StateProvider,
                producerEnv.StorageProvider,
                NullTxPool.Instance, // do not remove transactions from the pool when preprocessing
                NullReceiptStorage.Instance,
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

            ITxFilter txFilter = new MinGasPriceTxFilter(_miningConfig!.MinGasPrice);
            ITxSource txSource = new TxPoolTxSource(
                getFromApi.TxPool,
                getFromApi.StateReader,
                getFromApi.LogManager,
                txFilter);

            var gasLimitCalculator = new TargetAdjustedGasLimitCalculator(getFromApi.SpecProvider, _miningConfig);
            setInApi.BlockProducer = new CliqueBlockProducer(
                txSource,
                chainProcessor,
                producerEnv.StateProvider,
                getFromApi.BlockTree!,
                getFromApi.Timestamper,
                getFromApi.CryptoRandom,
                _snapshotManager!,
                getFromApi.Sealer!,
                gasLimitCalculator,
                _cliqueConfig!,
                getFromApi.LogManager);

            return Task.CompletedTask;
        }

        public Task InitNetworkProtocol()
        {
            return Task.CompletedTask;
        }

        public Task InitRpcModules()
        {
            if (_nethermindApi!.SealEngineType != SealEngineType.Clique)
            {
                return Task.CompletedTask;
            }

            var (getFromApi, _) = _nethermindApi!.ForRpc;
            CliqueRpcModule cliqueRpcModule = new CliqueRpcModule(
                getFromApi!.BlockProducer as ICliqueBlockProducer,
                _snapshotManager!,
                getFromApi.BlockTree!);

            var modulePool = new SingletonModulePool<ICliqueModule>(cliqueRpcModule);
            getFromApi.RpcModuleProvider.Register(modulePool);

            return Task.CompletedTask;
        }

        public SealEngineType SealEngineType => SealEngineType.Clique;

        public void Dispose()
        {
        }

        private INethermindApi? _nethermindApi;

        private ISnapshotManager? _snapshotManager;

        private ICliqueConfig? _cliqueConfig;

        private IMiningConfig? _miningConfig;
    }
}