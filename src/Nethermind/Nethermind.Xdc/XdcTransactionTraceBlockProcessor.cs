// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Tracing;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;

namespace Nethermind.Xdc;

/// <summary>
/// The <see cref="XdcBlockProcessor"/> that RPC replays a transaction prefix with, as
/// <see cref="TransactionTraceBlockProcessor"/> does for other chains. A replay runs on an <see cref="XdcBlockHeader"/>
/// and in the XDC block execution context, as the block was processed.
/// </summary>
internal sealed class XdcTransactionTraceBlockProcessor(
    ISpecProvider specProvider,
    IBlockValidator blockValidator,
    IRewardCalculator rewardCalculator,
    TransactionTraceExecutor executor,
    IWorldState stateProvider,
    IReceiptStorage receiptStorage,
    IBeaconBlockRootHandler beaconBlockRootHandler,
    IBlockhashStore blockHashStore,
    ILogManager logManager,
    IWithdrawalProcessor withdrawalProcessor,
    IExecutionRequestsProcessor executionRequestsProcessor,
    IBlockAccessListManager balManager)
    : XdcBlockProcessor(specProvider, blockValidator, rewardCalculator, executor, stateProvider, receiptStorage,
        beaconBlockRootHandler, blockHashStore, logManager, withdrawalProcessor, executionRequestsProcessor, balManager)
{
    private readonly TransactionTraceReplay _replay = new(executor, balManager);

    public override (Block Block, TxReceipt[] Receipts) ProcessOne(Block suggestedBlock, ProcessingOptions options,
        IBlockTracer blockTracer, IReleaseSpec spec, CancellationToken token) =>
        base.ProcessOne(suggestedBlock, _replay.BeginBlock(blockTracer, options, ReceiptsTracer), blockTracer, spec, token);

    /// <inheritdoc/>
    /// <remarks>Intentionally empty: the suggested block is the canonical instance and a replay never writes its artifacts back onto it.</remarks>
    protected override void PostValidation(Block suggestedBlock, Block processedBlock, TxReceipt[] receipts, ProcessingOptions options)
    {
    }

    protected override TxReceipt[] FinalizeBlock(Block block, IBlockTracer blockTracer, ProcessingOptions options,
        IReleaseSpec spec, TxReceipt[] receipts) =>
        _replay.FinalizeBlock(blockTracer, options, ReceiptsTracer) switch
        {
            TransactionTraceReplay.Finalization.Skip => receipts,
            TransactionTraceReplay.Finalization.WithoutCommitments => FinalizeBlock<OffFlag>(block, blockTracer, spec, receipts),
            _ => base.FinalizeBlock(block, blockTracer, options, spec, receipts),
        };
}
