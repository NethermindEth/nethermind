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
/// <remarks>A chain with its own block processor registers its own variant under
/// <see cref="TransactionTraceModule.BlockProcessorKey"/>, built on <see cref="TransactionTraceReplay"/>. An unpersisted
/// replay returns no receipts, so this must not be installed where its caller reads them, such as receipt regeneration.</remarks>
public sealed class TransactionTraceBlockProcessor(
    ISpecProvider specProvider, IBlockValidator blockValidator, IRewardCalculator rewardCalculator,
    TransactionTraceExecutor executor, IWorldState state, IReceiptStorage receipts,
    IBeaconBlockRootHandler beaconRoot, IBlockhashStore blockHashes, ILogManager logManager,
    IWithdrawalProcessor withdrawals, IExecutionRequestsProcessor requests, IBlockAccessListManager balManager)
    : BlockProcessor(specProvider, blockValidator, rewardCalculator, executor, state, receipts,
        beaconRoot, blockHashes, logManager, withdrawals, requests, balManager)
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
