// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.Producers
{
    public class BlockProducerEnvFactory : IBlockProducerEnvFactory
    {
        protected readonly IWorldStateManager _worldStateManager;
        protected readonly IBlockTree _blockTree;
        protected readonly ISpecProvider _specProvider;
        protected readonly IBlockValidator _blockValidator;
        protected readonly IRewardCalculatorSource _rewardCalculatorSource;
        protected readonly IReceiptStorage _receiptStorage;
        protected readonly IBlockPreprocessorStep _blockPreprocessorStep;
        protected readonly IBlocksConfig _blocksConfig;
        protected readonly ILogManager _logManager;
        private readonly IReadOnlyTxProcessingEnvFactory _readOnlyTxProcessingEnvFactory;
        private readonly IBlockProducerTxSourceFactory _blockProducerTxSourceFactory;

        public IBlockTransactionsExecutorFactory TransactionsExecutorFactory { get; set; }
        public IExecutionRequestsProcessor? ExecutionRequestsProcessorOverride { get; set; }

        public BlockProducerEnvFactory(
            IWorldStateManager worldStateManager,
            IReadOnlyTxProcessingEnvFactory readOnlyTxProcessingEnvFactory,
            IBlockTree blockTree,
            ISpecProvider specProvider,
            IBlockValidator blockValidator,
            IRewardCalculatorSource rewardCalculatorSource,
            IBlockPreprocessorStep blockPreprocessorStep,
            IBlocksConfig blocksConfig,
            IBlockProducerTxSourceFactory blockProducerTxSourceFactory,
            ILogManager logManager)
        {
            _worldStateManager = worldStateManager;
            _readOnlyTxProcessingEnvFactory = readOnlyTxProcessingEnvFactory;
            _blockTree = blockTree;
            _specProvider = specProvider;
            _blockValidator = blockValidator;
            _rewardCalculatorSource = rewardCalculatorSource;
            _receiptStorage = NullReceiptStorage.Instance;
            _blockPreprocessorStep = blockPreprocessorStep;
            _blocksConfig = blocksConfig;
            _blockProducerTxSourceFactory = blockProducerTxSourceFactory;
            _logManager = logManager;

            TransactionsExecutorFactory = new BlockProducerTransactionsExecutorFactory(specProvider, _blocksConfig.BlockProductionMaxTxKilobytes, logManager);
        }

        public virtual IBlockProducerEnv Create()
        {
            ReadOnlyBlockTree readOnlyBlockTree = _blockTree.AsReadOnly();

            IReadOnlyTxProcessorSource txProcessingEnv = _readOnlyTxProcessingEnvFactory.Create();

            IReadOnlyTxProcessingScope scope = txProcessingEnv.Build(Keccak.EmptyTreeHash);

            BlockProcessor blockProcessor = CreateBlockProcessor(scope);

            IBlockchainProcessor blockchainProcessor =
                new BlockchainProcessor(
                    readOnlyBlockTree,
                    blockProcessor,
                    _blockPreprocessorStep,
                    _worldStateManager.GlobalStateReader,
                    _logManager,
                    BlockchainProcessor.Options.NoReceipts);

            OneTimeChainProcessor chainProcessor = new(
                scope.WorldState,
                blockchainProcessor);

            return new BlockProducerEnv
            {
                BlockTree = readOnlyBlockTree,
                ChainProcessor = chainProcessor,
                ReadOnlyStateProvider = scope.WorldState,
                TxSource = _blockProducerTxSourceFactory.Create()
            };
        }

        protected virtual BlockProcessor CreateBlockProcessor(IReadOnlyTxProcessingScope readOnlyTxProcessingEnv) =>
            new BlockProcessor(
                _specProvider,
                _blockValidator,
                _rewardCalculatorSource.Get(readOnlyTxProcessingEnv.TransactionProcessor),
                TransactionsExecutorFactory.Create(readOnlyTxProcessingEnv),
                readOnlyTxProcessingEnv.WorldState,
                _receiptStorage,
                new BeaconBlockRootHandler(readOnlyTxProcessingEnv.TransactionProcessor, readOnlyTxProcessingEnv.WorldState),
                new BlockhashStore(_specProvider, readOnlyTxProcessingEnv.WorldState),
                _logManager,
                new BlockProductionWithdrawalProcessor(new WithdrawalProcessor(readOnlyTxProcessingEnv.WorldState, _logManager)),
                executionRequestsProcessor: ExecutionRequestsProcessorOverride ?? new ExecutionRequestsProcessor(readOnlyTxProcessingEnv.TransactionProcessor));
    }
}
