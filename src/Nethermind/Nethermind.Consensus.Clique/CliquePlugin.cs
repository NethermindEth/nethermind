// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.JsonRpc.Modules;

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
            if (_nethermindApi!.SealEngineType != Core.SealEngineType.Clique)
            {
                return Task.CompletedTask;
            }

            (IApiWithStores getFromApi, IApiWithBlockchain setInApi) = _nethermindApi.ForInit;


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

            // both Clique and the merge provide no block rewards
            setInApi.RewardCalculatorSource = NoBlockRewards.Instance;
            setInApi.BlockPreprocessor.AddLast(new AuthorRecoveryStep(_snapshotManager!));

            return Task.CompletedTask;
        }

        public IBlockProducerEnvFactory? BuildBlockProducerEnvFactory(
            IBlockTransactionsExecutorFactory transactionsExecutorFactory)
        {
            return new BlockProducerEnvFactory(
                _nethermindApi.WorldStateManager!,
                _nethermindApi.BlockTree!,
                _nethermindApi.SpecProvider!,
                _nethermindApi.BlockValidator!,
                _nethermindApi.RewardCalculatorSource!,
                // So it does not have receipt here, but for some reason, by default `BlockProducerEnvFactory` have real receipt store.
                NullReceiptStorage.Instance,
                _nethermindApi.BlockPreprocessor,
                _nethermindApi.TxPool!,
                _nethermindApi.TransactionComparerProvider!,
                _nethermindApi.Config<IBlocksConfig>(),
                _nethermindApi.LogManager,
                transactionsExecutorFactory);
        }

        public IBlockProducer InitBlockProducer(ITxSource? additionalTxSource = null)
        {
            if (_nethermindApi!.SealEngineType != Nethermind.Core.SealEngineType.Clique)
            {
                return null;
            }

            (IApiWithBlockchain getFromApi, IApiWithBlockchain setInApi) = _nethermindApi!.ForProducer;

            _blocksConfig = getFromApi.Config<IBlocksConfig>();
            IMiningConfig miningConfig = getFromApi.Config<IMiningConfig>();

            if (!miningConfig.Enabled)
            {
                throw new InvalidOperationException("Request to start block producer while mining disabled.");
            }

            setInApi.Sealer = new CliqueSealer(
                getFromApi.EngineSigner!,
                _cliqueConfig!,
                _snapshotManager!,
                getFromApi.LogManager);

            BlockProducerEnv env = _nethermindApi.BlockProducerEnvFactory!.Create(additionalTxSource);
            IGasLimitCalculator gasLimitCalculator = setInApi.GasLimitCalculator = new TargetAdjustedGasLimitCalculator(getFromApi.SpecProvider, _blocksConfig);
            CliqueBlockProducer blockProducer = new(
                env.TxSource,
                env.ChainProcessor,
                env.ReadOnlyTxProcessingEnv.StateProvider,
                getFromApi.Timestamper,
                getFromApi.CryptoRandom,
                _snapshotManager!,
                getFromApi.Sealer!,
                gasLimitCalculator,
                getFromApi.SpecProvider,
                _cliqueConfig!,
                getFromApi.LogManager);

            return blockProducer;
        }

        public IBlockProducerRunner CreateBlockProducerRunner()
        {
            _blockProducerRunner = new CliqueBlockProducerRunner(
                _nethermindApi.BlockTree,
                _nethermindApi.Timestamper,
                _nethermindApi.CryptoRandom,
                _snapshotManager,
                (CliqueBlockProducer)_nethermindApi.BlockProducer!,
                _cliqueConfig,
                _nethermindApi.LogManager);
            _nethermindApi.DisposeStack.Push(_blockProducerRunner);
            return _blockProducerRunner;
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

            (IApiWithNetwork getFromApi, _) = _nethermindApi!.ForRpc;
            CliqueRpcModule cliqueRpcModule = new(
                _blockProducerRunner,
                _snapshotManager!,
                getFromApi.BlockTree!);

            SingletonModulePool<ICliqueRpcModule> modulePool = new(cliqueRpcModule);
            getFromApi.RpcModuleProvider!.Register(modulePool);

            return Task.CompletedTask;
        }

        public string SealEngineType => Nethermind.Core.SealEngineType.Clique;

        public ValueTask DisposeAsync() { return ValueTask.CompletedTask; }

        private INethermindApi? _nethermindApi;

        private ISnapshotManager? _snapshotManager;

        private ICliqueConfig? _cliqueConfig;

        private IBlocksConfig? _blocksConfig;
        private CliqueBlockProducerRunner _blockProducerRunner = null!;
    }
}
