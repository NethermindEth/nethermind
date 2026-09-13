// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;

namespace Nethermind.Consensus.Tracing;

/// <summary>RPC-only processor which omits block finalization after a completed transaction prefix.</summary>
public sealed class TransactionTraceBlockProcessor(
    ISpecProvider specProvider, IBlockValidator blockValidator, IRewardCalculator rewardCalculator,
    TransactionTraceExecutor executor, IWorldState state, IReceiptStorage receipts,
    IBeaconBlockRootHandler beaconRoot, IBlockhashStore blockHashes, ILogManager logManager,
    IWithdrawalProcessor withdrawals, IExecutionRequestsProcessor requests, IBlockAccessListManager balManager)
    : BlockProcessor(specProvider, blockValidator, rewardCalculator, executor, state, receipts,
        beaconRoot, blockHashes, logManager, withdrawals, requests, balManager)
{
    public override (Block Block, TxReceipt[] Receipts) ProcessOne(Block suggestedBlock, ProcessingOptions options,
        IBlockTracer blockTracer, IReleaseSpec spec, CancellationToken token)
    {
        if (TransactionTraceBoundary.Get(blockTracer, options) is not null && !_balManager.ForceConstructGeneratedBlockAccessList)
            options |= ProcessingOptions.ForceSequentialBlockAccessList;

        return base.ProcessOne(suggestedBlock, options, blockTracer, spec, token);
    }

    protected override TxReceipt[] FinalizeBlock(Block block, IBlockTracer blockTracer, ProcessingOptions options,
        IReleaseSpec spec, TxReceipt[] receipts)
    {
        if (!_balManager.ForceConstructGeneratedBlockAccessList
            && TransactionTraceBoundary.Get(blockTracer, options)?.IsComplete == true)
        {
            ReceiptsTracer.EndBlockTrace(accumulateBlockBloom: false);
            return receipts;
        }

        return base.FinalizeBlock(block, blockTracer, options, spec, receipts);
    }
}
