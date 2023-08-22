// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core.Attributes;
using Nethermind.Db;
using Nethermind.JsonRpc.Modules;
using Nethermind.State;

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

        public Task<IBlockProducer> InitBlockProducer(IBlockProductionTrigger? blockProductionTrigger = null, ITxSource? additionalTxSource = null)
        {
            if (_nethermindApi!.SealEngineType != Nethermind.Core.SealEngineType.Clique)
            {
                return Task.FromResult((IBlockProducer)null);
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

            ReadOnlyDbProvider readOnlyDbProvider = getFromApi.DbProvider!.AsReadOnly(false);
            ReadOnlyBlockTree readOnlyBlockTree = getFromApi.BlockTree!.AsReadOnly();
            ITransactionComparerProvider transactionComparerProvider = getFromApi.TransactionComparerProvider;

            ReadOnlyTxProcessingEnv producerEnv = new(
                readOnlyDbProvider,
                getFromApi.ReadOnlyTrieStore,
                readOnlyBlockTree,
                getFromApi.SpecProvider,
                getFromApi.LogManager);

            BlockProcessor producerProcessor = new(
                getFromApi!.SpecProvider,
                getFromApi!.BlockValidator,
                NoBlockRewards.Instance,
                getFromApi.BlockProducerEnvFactory.TransactionsExecutorFactory.Create(producerEnv),
                producerEnv.StateProvider,
                NullReceiptStorage.Instance,
                NullWitnessCollector.Instance,
                getFromApi.TransactionProcessor,
                getFromApi.LogManager, new BlockProductionWithdrawalProcessor(new WithdrawalProcessor(producerEnv.StateProvider, getFromApi.LogManager)));

            IBlockchainProcessor producerChainProcessor = new BlockchainProcessor(
                readOnlyBlockTree,
                producerProcessor,
                getFromApi.BlockPreprocessor,
                getFromApi.StateReader,
                getFromApi.LogManager,
                BlockchainProcessor.Options.NoReceipts);

            OneTimeChainProcessor chainProcessor = new(
                readOnlyDbProvider,
                producerChainProcessor);

            ITxFilterPipeline txFilterPipeline =
                TxFilterPipelineBuilder.CreateStandardFilteringPipeline(
                    _nethermindApi.LogManager,
                    getFromApi.SpecProvider,
                    _blocksConfig);

            TxPoolTxSource txPoolTxSource = new(
                getFromApi.TxPool,
                getFromApi.SpecProvider,
                transactionComparerProvider,
                getFromApi.LogManager,
                txFilterPipeline);

            IGasLimitCalculator gasLimitCalculator = setInApi.GasLimitCalculator = new TargetAdjustedGasLimitCalculator(getFromApi.SpecProvider, _blocksConfig);

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

            (IApiWithNetwork getFromApi, _) = _nethermindApi!.ForRpc;
            CliqueRpcModule cliqueRpcModule = new(
                getFromApi!.BlockProducer as ICliqueBlockProducer,
                _snapshotManager!,
                getFromApi.BlockTree!);

            SingletonModulePool<ICliqueRpcModule> modulePool = new(cliqueRpcModule);
            getFromApi.RpcModuleProvider!.Register(modulePool);

            return Task.CompletedTask;
        }

        public string SealEngineType => Nethermind.Core.SealEngineType.Clique;

        [Todo("Redo clique producer to support triggers and MEV")]
        public IBlockProductionTrigger DefaultBlockProductionTrigger => _nethermindApi!.ManualBlockProductionTrigger;

        public ValueTask DisposeAsync() { return ValueTask.CompletedTask; }

        private INethermindApi? _nethermindApi;

        private ISnapshotManager? _snapshotManager;

        private ICliqueConfig? _cliqueConfig;

        private IBlocksConfig? _blocksConfig;
    }
}
