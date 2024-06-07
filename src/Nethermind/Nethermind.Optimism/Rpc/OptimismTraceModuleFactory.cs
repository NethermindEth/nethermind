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
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Optimism.Rpc;

public class OptimismTraceModuleFactory(
    IWorldStateManager worldStateManager,
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
        worldStateManager,
        blockTree,
        jsonRpcConfig,
        recoveryStep,
        rewardCalculatorSource,
        receiptFinder,
        specProvider,
        poSSwitcher,
        logManager)
{
    protected override ReadOnlyTxProcessingEnv CreateTxProcessingEnv() =>
        new OptimismReadOnlyTxProcessingEnv(_worldStateManager, _blockTree, _specProvider, _logManager, l1CostHelper, opSpecHelper);

    protected override ReadOnlyChainProcessingEnv CreateChainProcessingEnv(IBlockProcessor.IBlockTransactionsExecutor transactionsExecutor, ReadOnlyTxProcessingEnv txProcessingEnv, IRewardCalculator rewardCalculator) => new OptimismReadOnlyChainProcessingEnv(
                txProcessingEnv,
                Always.Valid,
                _recoveryStep,
                rewardCalculator,
                _receiptStorage,
                _specProvider,
                _logManager,
                opSpecHelper,
                contractRewriter,
                withdrawalProcessor,
                transactionsExecutor);
}
