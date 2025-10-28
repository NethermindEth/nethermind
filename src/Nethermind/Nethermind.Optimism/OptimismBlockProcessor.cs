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
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;

namespace Nethermind.Optimism;

public class OptimismBlockProcessor : BlockProcessor
{
    private readonly IOptimismSpecHelper _opSpecHelper;
    private readonly Create2DeployerContractRewriter? _contractRewriter;
    private readonly ICostHelper _costHelper;

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
        ICostHelper costHelper)
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
            executionRequestsProcessor)
    {
        ArgumentNullException.ThrowIfNull(stateProvider);
        _opSpecHelper = opSpecHelper;
        _contractRewriter = contractRewriter;
        _costHelper = costHelper;
        ReceiptsTracer = new OptimismBlockReceiptTracer(opSpecHelper, stateProvider);
    }

    protected override TxReceipt[] ProcessBlock(Block block, IBlockTracer blockTracer, ProcessingOptions options, IReleaseSpec spec, CancellationToken token)
    {
        _contractRewriter?.RewriteContract(block.Header, _stateProvider);
        TxReceipt[] receipts = base.ProcessBlock(block, blockTracer, options, spec, token);

        if (_opSpecHelper.IsJovian(block.Header))
        {
            var (ulongValue, hasOverflow) = _costHelper.ComputeDAFootprint(block, _stateProvider).UlongWithOverflow;
            if (hasOverflow || ulongValue > long.MaxValue)
                throw new InvalidOperationException("DA Footprint overflow");

            var daFootprint = (long)ulongValue;
            block.Header.GasUsed = Math.Max(block.GasUsed, daFootprint);
            block.Header.Hash = block.Header.CalculateHash();
        }

        return receipts;
    }
}
