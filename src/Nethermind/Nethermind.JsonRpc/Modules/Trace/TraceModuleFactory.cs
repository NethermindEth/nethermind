// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Tracing;
using Nethermind.Consensus.Validators;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.JsonRpc.Modules.Trace;

public class TraceModuleFactory(
    IWorldStateManager worldStateManager,
    IBlockTree blockTree,
    IJsonRpcConfig jsonRpcConfig,
    IBlockPreprocessorStep recoveryStep,
    IRewardCalculatorSource rewardCalculatorSource,
    IReceiptStorage receiptFinder,
    ISpecProvider specProvider,
    IPoSSwitcher poSSwitcher,
    ILogManager logManager) : ModuleFactoryBase<ITraceRpcModule>
{
    protected readonly IReadOnlyBlockTree _blockTree = blockTree.AsReadOnly();
    protected readonly IJsonRpcConfig _jsonRpcConfig = jsonRpcConfig ?? throw new ArgumentNullException(nameof(jsonRpcConfig));
    protected readonly IReceiptStorage _receiptStorage = receiptFinder ?? throw new ArgumentNullException(nameof(receiptFinder));
    protected readonly ISpecProvider _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
    protected readonly ILogManager _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
    protected readonly IBlockPreprocessorStep _recoveryStep = recoveryStep ?? throw new ArgumentNullException(nameof(recoveryStep));
    protected readonly IRewardCalculatorSource _rewardCalculatorSource = rewardCalculatorSource ?? throw new ArgumentNullException(nameof(rewardCalculatorSource));
    protected readonly IPoSSwitcher _poSSwitcher = poSSwitcher ?? throw new ArgumentNullException(nameof(poSSwitcher));

    protected virtual OverridableTxProcessingEnv CreateTxProcessingEnv(IOverridableWorldScope worldScope) => new(worldScope, _blockTree, _specProvider, _logManager);

    protected virtual ReadOnlyChainProcessingEnv CreateChainProcessingEnv(IOverridableWorldScope worldScope, IBlockProcessor.IBlockTransactionsExecutor transactionsExecutor, IReadOnlyTxProcessingScope scope, IRewardCalculator rewardCalculator) => new(
                scope,
                Always.Valid,
                _recoveryStep,
                rewardCalculator,
                _receiptStorage,
                _specProvider,
                _blockTree,
                worldScope.GlobalStateReader,
                _logManager,
                transactionsExecutor);

    public override ITraceRpcModule Create()
    {
        IOverridableWorldScope overridableScope = worldStateManager.CreateOverridableWorldScope();
        OverridableTxProcessingEnv txProcessingEnv = CreateTxProcessingEnv(overridableScope);
        IReadOnlyTxProcessingScope scope = txProcessingEnv.Build(Keccak.EmptyTreeHash);

        IRewardCalculator rewardCalculator =
            new MergeRpcRewardCalculator(_rewardCalculatorSource.Get(scope.TransactionProcessor),
                _poSSwitcher);

        RpcBlockTransactionsExecutor rpcBlockTransactionsExecutor = new(scope.TransactionProcessor, scope.WorldState);
        BlockProcessor.BlockValidationTransactionsExecutor executeBlockTransactionsExecutor = new(scope.TransactionProcessor, scope.WorldState);

        ReadOnlyChainProcessingEnv traceProcessingEnv = CreateChainProcessingEnv(overridableScope, rpcBlockTransactionsExecutor, scope, rewardCalculator);
        ReadOnlyChainProcessingEnv executeProcessingEnv = CreateChainProcessingEnv(overridableScope, executeBlockTransactionsExecutor, scope, rewardCalculator);

        Tracer tracer = new(scope.WorldState, traceProcessingEnv.ChainProcessor, executeProcessingEnv.ChainProcessor,
            traceOptions: ProcessingOptions.TraceTransactions);

        return new TraceRpcModule(_receiptStorage, tracer, _blockTree, _jsonRpcConfig, txProcessingEnv);
    }
}
