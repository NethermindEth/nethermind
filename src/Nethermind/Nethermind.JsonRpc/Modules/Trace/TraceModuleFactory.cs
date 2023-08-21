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
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using Nethermind.Verkle.Tree;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Modules.Trace
{
    public class TraceModuleFactory : ModuleFactoryBase<ITraceRpcModule>
    {
        private readonly ReadOnlyDbProvider _dbProvider;
        private readonly IReadOnlyBlockTree _blockTree;
        private readonly IJsonRpcConfig _jsonRpcConfig;
        private readonly IReceiptStorage _receiptStorage;
        private readonly ISpecProvider _specProvider;
        private readonly ILogManager _logManager;
        private readonly IBlockPreprocessorStep _recoveryStep;
        private readonly IRewardCalculatorSource _rewardCalculatorSource;
        private readonly IPoSSwitcher _poSSwitcher;
        private readonly ReadOnlyTxProcessingEnvFactory _txProcessingEnvFactory;

        public TraceModuleFactory(
            ReadOnlyTxProcessingEnvFactory txProcessingEnvFactory,
            IDbProvider dbProvider,
            IBlockTree blockTree,
            IJsonRpcConfig jsonRpcConfig,
            IBlockPreprocessorStep recoveryStep,
            IRewardCalculatorSource rewardCalculatorSource,
            IReceiptStorage receiptFinder,
            ISpecProvider specProvider,
            IPoSSwitcher poSSwitcher,
            ILogManager logManager)
        {
            _txProcessingEnvFactory = txProcessingEnvFactory;
            _dbProvider = dbProvider.AsReadOnly(false);
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
            ReadOnlyTxProcessingEnv txProcessingEnv = _txProcessingEnvFactory.Create();

            IRewardCalculator rewardCalculator =
                new MergeRpcRewardCalculator(_rewardCalculatorSource.Get(txProcessingEnv.TransactionProcessor),
                    _poSSwitcher);

            RpcBlockTransactionsExecutor rpcBlockTransactionsExecutor = new(txProcessingEnv.TransactionProcessor, txProcessingEnv.StateProvider);
            BlockProcessor.BlockValidationTransactionsExecutor executeBlockTransactionsExecutor = new(txProcessingEnv.TransactionProcessor,
                txProcessingEnv.StateProvider);

            ReadOnlyChainProcessingEnv CreateChainProcessingEnv(IBlockProcessor.IBlockTransactionsExecutor transactionsExecutor) => new(
                txProcessingEnv,
                Always.Valid,
                _recoveryStep,
                rewardCalculator,
                _receiptStorage,
                _dbProvider,
                _specProvider,
                _logManager,
                transactionsExecutor);

            ReadOnlyChainProcessingEnv traceProcessingEnv = CreateChainProcessingEnv(rpcBlockTransactionsExecutor);
            ReadOnlyChainProcessingEnv executeProcessingEnv = CreateChainProcessingEnv(executeBlockTransactionsExecutor);

            Tracer tracer = new(txProcessingEnv.StateProvider, traceProcessingEnv.ChainProcessor, executeProcessingEnv.ChainProcessor);

            return new TraceRpcModule(_receiptStorage, tracer, _blockTree, _jsonRpcConfig, _specProvider, _logManager);
        }

        public static JsonConverter[] Converters =
        {
            new ParityTxTraceFromReplayConverter(),
            new ParityAccountStateChangeConverter(),
            new ParityTraceActionConverter(),
            new ParityTraceResultConverter(),
            new ParityVmOperationTraceConverter(),
            new ParityVmTraceConverter(),
            new TransactionForRpcWithTraceTypesConverter()
        };

        public override IReadOnlyCollection<JsonConverter> GetConverters() => Converters;
    }
}
