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
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Optimism;

public class OptimismBlockProcessor : BlockProcessor
{
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
        _contractRewriter = contractRewriter;
        _costHelper = costHelper;
        ReceiptsTracer = new OptimismBlockReceiptTracer(opSpecHelper, stateProvider);
    }

    protected override TxReceipt[] ProcessBlock(Block block, IBlockTracer blockTracer, ProcessingOptions options, IReleaseSpec spec, CancellationToken token)
    {
        _contractRewriter?.RewriteContract(block.Header, _stateProvider);
        TxReceipt[] receipts = base.ProcessBlock(block, blockTracer, options, spec, token);

        // TODO: handle overflow differently or calculate footprint as long?
        (ulong value, bool overflow) ulongWithOverflow = CalculateDAFootprint(block).UlongWithOverflow;
        if (ulongWithOverflow.overflow || ulongWithOverflow.value > long.MaxValue)
            throw new InvalidOperationException("DA Footprint overflow");

        var daFootprint = (long)ulongWithOverflow.value;
        block.Header.GasUsed = Math.Max(block.GasUsed, daFootprint);
        block.Header.Hash = block.Header.CalculateHash();

        return receipts;
    }

    private UInt256 CalculateDAFootprint(Block block)
    {
        UInt256 footprint = UInt256.Zero;

        foreach (Transaction tx in block.Transactions)
            footprint += _costHelper.ComputeDAFootprint(tx, block.Header, _stateProvider);

        return footprint;
    }
}
