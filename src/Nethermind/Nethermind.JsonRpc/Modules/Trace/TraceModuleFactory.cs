// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;

using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Tracing;
using Nethermind.Consensus.Validators;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;

namespace Nethermind.JsonRpc.Modules.Trace
{
    public class TraceModuleFactory : ModuleFactoryBase<ITraceRpcModule>
    {
        private readonly IWorldStateManager _worldStateManager;
        private readonly IReadOnlyBlockTree _blockTree;
        private readonly IJsonRpcConfig _jsonRpcConfig;
        private readonly IReceiptStorage _receiptStorage;
        private readonly ISpecProvider _specProvider;
        private readonly ILogManager _logManager;
        private readonly IBlockPreprocessorStep _recoveryStep;
        private readonly IRewardCalculatorSource _rewardCalculatorSource;
        private readonly IPoSSwitcher _poSSwitcher;

        public TraceModuleFactory(
            IWorldStateManager worldStateManager,
            IBlockTree blockTree,
            IJsonRpcConfig jsonRpcConfig,
            IBlockPreprocessorStep recoveryStep,
            IRewardCalculatorSource rewardCalculatorSource,
            IReceiptStorage receiptFinder,
            ISpecProvider specProvider,
            IPoSSwitcher poSSwitcher,
            ILogManager logManager)
        {
            _worldStateManager = worldStateManager;
            _blockTree = blockTree.AsReadOnly();
            _jsonRpcConfig = jsonRpcConfig ?? throw new ArgumentNullException(nameof(jsonRpcConfig));
            _recoveryStep = recoveryStep ?? throw new ArgumentNullException(nameof(recoveryStep));
            _rewardCalculatorSource = rewardCalculatorSource ?? throw new ArgumentNullException(nameof(rewardCalculatorSource));
            _receiptStorage = receiptFinder ?? throw new ArgumentNullException(nameof(receiptFinder));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _poSSwitcher = poSSwitcher ?? throw new ArgumentNullException(nameof(poSSwitcher));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            logManager.GetClassLogger();
        }

        public override ITraceRpcModule Create()
        {
            ReadOnlyTxProcessingEnv txProcessingEnv =
                new(_worldStateManager, _blockTree, _specProvider, _logManager);

            IReadOnlyTxProcessingScope scope = txProcessingEnv.Build(Keccak.EmptyTreeHash);

            IRewardCalculator rewardCalculator =
                new MergeRpcRewardCalculator(_rewardCalculatorSource.Get(scope.TransactionProcessor),
                    _poSSwitcher);

            RpcBlockTransactionsExecutor rpcBlockTransactionsExecutor = new(scope.TransactionProcessor, scope.WorldState);
            BlockProcessor.BlockValidationTransactionsExecutor executeBlockTransactionsExecutor = new(scope.TransactionProcessor, scope.WorldState);

            ReadOnlyChainProcessingEnv CreateChainProcessingEnv(IBlockProcessor.IBlockTransactionsExecutor transactionsExecutor) => new(
                scope,
                Always.Valid,
                _recoveryStep,
                rewardCalculator,
                _receiptStorage,
                _specProvider,
                _blockTree,
                _worldStateManager.GlobalStateReader,
                _logManager,
                transactionsExecutor);

            ReadOnlyChainProcessingEnv traceProcessingEnv = CreateChainProcessingEnv(rpcBlockTransactionsExecutor);
            ReadOnlyChainProcessingEnv executeProcessingEnv = CreateChainProcessingEnv(executeBlockTransactionsExecutor);

            Tracer tracer = new(scope.WorldState, traceProcessingEnv.ChainProcessor, executeProcessingEnv.ChainProcessor);

            return new TraceRpcModule(_receiptStorage, tracer, _blockTree, _jsonRpcConfig, _specProvider, _logManager, txProcessingEnv.StateReader);
        }
    }
}
