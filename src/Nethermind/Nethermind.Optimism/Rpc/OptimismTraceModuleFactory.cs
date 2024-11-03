// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;

namespace Nethermind.Optimism.Rpc;

public class OptimismTraceModuleFactory(
    IReadOnlyTrieStore trieStore,
    IDbProvider dbProvider,
    IBlockTree blockTree,
    IJsonRpcConfig jsonRpcConfig,
    IBlockPreprocessorStep recoveryStep,
    IRewardCalculatorSource rewardCalculatorSource,
    IReceiptStorage receiptFinder,
    ISpecProvider specProvider,
    IPoSSwitcher poSSwitcher,
    ILogManager logManager,
    IL1CostHelper l1CostHelper,
    IOptimismSpecHelper opSpecHelper,
    Create2DeployerContractRewriter contractRewriter,
    IWithdrawalProcessor withdrawalProcessor) : TraceModuleFactory(
        trieStore,
        dbProvider,
        blockTree,
        jsonRpcConfig,
        recoveryStep,
        rewardCalculatorSource,
        receiptFinder,
        specProvider,
        poSSwitcher,
        logManager)
{
    protected override OverridableTxProcessingEnv CreateTxProcessingEnv(OverridableWorldStateManager worldStateManager) =>
        new OptimismOverridableTxProcessingEnv(worldStateManager, _blockTree, _specProvider, _logManager, l1CostHelper, opSpecHelper);

    protected override ReadOnlyChainProcessingEnv CreateChainProcessingEnv(OverridableWorldStateManager worldStateManager, IBlockProcessor.IBlockTransactionsExecutor transactionsExecutor, IReadOnlyTxProcessingScope scope, IRewardCalculator rewardCalculator) => new OptimismReadOnlyChainProcessingEnv(
                scope,
                Always.Valid,
                _recoveryStep,
                rewardCalculator,
                _receiptStorage,
                _specProvider,
                _blockTree,
                worldStateManager.GlobalStateReader,
                _logManager,
                opSpecHelper,
                contractRewriter,
                withdrawalProcessor,
                transactionsExecutor);
}
