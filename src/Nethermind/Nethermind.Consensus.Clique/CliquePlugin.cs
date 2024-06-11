// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Evm.TransactionProcessing;
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

            ReadOnlyBlockTree readOnlyBlockTree = getFromApi.BlockTree!.AsReadOnly();
            ITransactionComparerProvider transactionComparerProvider = getFromApi.TransactionComparerProvider;

            IReadOnlyTxProcessingScope scope = _nethermindApi.ReadOnlyTxProcessorSource.Build(Keccak.EmptyTreeHash);

            BlockProcessor producerProcessor = new(
                getFromApi!.SpecProvider,
                getFromApi!.BlockValidator,
                NoBlockRewards.Instance,
                getFromApi.BlockProducerEnvFactory.TransactionsExecutorFactory.Create(scope),
                scope.WorldState,
                NullReceiptStorage.Instance,
                new BlockhashStore(getFromApi.BlockTree, getFromApi.SpecProvider, scope.WorldState),
                getFromApi.LogManager,
                new BlockProductionWithdrawalProcessor(new WithdrawalProcessor(scope.WorldState, getFromApi.LogManager)));

            IBlockchainProcessor producerChainProcessor = new BlockchainProcessor(
                readOnlyBlockTree,
                producerProcessor,
                getFromApi.BlockPreprocessor,
                getFromApi.StateReader,
                getFromApi.LogManager,
                BlockchainProcessor.Options.NoReceipts);

            OneTimeChainProcessor chainProcessor = new(
                scope.WorldState,
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

            CliqueBlockProducer blockProducer = new(
                additionalTxSource.Then(txPoolTxSource),
                chainProcessor,
                scope.WorldState,
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
