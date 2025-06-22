// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Optimism;

public class OptimismBlockProcessor : BlockProcessor
{
    private readonly Create2DeployerContractRewriter? _contractRewriter;

    public OptimismBlockProcessor(
        ISpecProvider specProvider,
        IBlockValidator blockValidator,
        IRewardCalculator rewardCalculator,
        IBlockProcessor.IBlockTransactionsExecutor blockTransactionsExecutor,
        IWorldState stateProvider,
        IReceiptStorage receiptStorage,
        IBlockhashStore blockhashStore,
        IBeaconBlockRootHandler beaconBlockRootHandler,
        ILogManager logManager,
        IOptimismSpecHelper opSpecHelper,
        Create2DeployerContractRewriter contractRewriter,
        IWithdrawalProcessor withdrawalProcessor,
        IExecutionRequestsProcessor executionRequestsProcessor,
        IBlockCachePreWarmer? preWarmer = null)
        : base(
            specProvider,
            blockValidator,
            rewardCalculator,
            blockTransactionsExecutor,
            stateProvider,
            receiptStorage,
            beaconBlockRootHandler,
            blockhashStore,
            logManager,
            withdrawalProcessor,
            executionRequestsProcessor,
            preWarmer)
    {
        ArgumentNullException.ThrowIfNull(stateProvider);
        _contractRewriter = contractRewriter;
        ReceiptsTracer = new OptimismBlockReceiptTracer(opSpecHelper, stateProvider);
    }

    protected override TxReceipt[] ProcessBlock(Block block, IBlockTracer blockTracer, ProcessingOptions options, CancellationToken token)
    {
        _contractRewriter?.RewriteContract(block.Header, _stateProvider);
        return base.ProcessBlock(block, blockTracer, options, token);
    }
}
