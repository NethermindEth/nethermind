// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Blockchain.Tracing;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Reports the main thread's per-transaction progress to the prewarmer so it can skip warming already-started txs,
/// and takes over a transaction's warm run when its footprint matches the state.
/// The <see cref="IPrewarmerState.IsPrewarmer"/> guard ensures only the main execution reports, not the prewarmer's own scope.
/// </summary>
/// <remarks>
/// Registered for main block processing only, so footprints are never taken by block building or tracing.
/// With <see cref="IBlocksConfig.PreWarmBatchedReplayCommit"/>, consecutive takeovers that change nothing but balances,
/// nonces and slots share one commit: while each one's changes commit as they read, the transactions after it read the
/// state a commit would have left, and the shared commit leaves the state the commits one by one would. Every other
/// takeover, and every executed transaction, starts from committed state, as does the block after its last transaction.
/// </remarks>
public class PrewarmerTxAdapter(
    ITransactionProcessorAdapter baseAdapter,
    BlockCachePreWarmer preWarmer,
    IPrewarmerState prewarmerState,
    IWorldState worldState,
    ILogManager logManager,
    IBlocksConfig? blocksConfig = null) : ITransactionProcessorAdapter
{
    private readonly ILogger _logger = logManager.GetClassLogger<PrewarmerTxAdapter>();
    // Only the world state itself is known to commit as its changes read; a decorator may do anything at a commit.
    private readonly WorldState? _sharedCommitState = blocksConfig?.PreWarmBatchedReplayCommit == true ? worldState as WorldState : null;
    private readonly int _maxSharedCommit = blocksConfig?.PreWarmBatchedReplayCommitMaxTxs ?? 0;
    private BlockExecutionContext _blockExecutionContext;
    // The takeovers whose changes the next commit takes along.
    private int _uncommitted;

    internal (int Replayed, int Rejected, int Missing) Tally { get; private set; }

    /// <summary>The takeovers that left their commit to a later one.</summary>
    internal int CommitsShared { get; private set; }

    [SkipLocalsInit]
    public TransactionResult Execute(Transaction transaction, ITxTracer txTracer)
    {
        if (!prewarmerState.IsPrewarmer)
        {
            preWarmer.OnBeforeTxExecution();
            TransactionFootprint? footprint = preWarmer.FindFootprint(transaction, _blockExecutionContext.Header, out bool eligible);
            if (eligible && TryReplay(footprint, transaction, txTracer, out TransactionResult result))
            {
                return result;
            }

            // Executed on committed state, as after every transaction before it; its reported writes are then its own.
            if (_uncommitted != 0) CommitTakeovers();

            // What the transaction writes goes to the block's footprints, so those that read it are refreshed on those values.
            if (prewarmerState.CommittedWrites is { } committed && preWarmer.TakesExecutedWrites) return ExecuteReportingWrites(transaction, txTracer, committed);
        }

        return baseAdapter.Execute(transaction, txTracer);
    }

    // Apart, so the logging is no part of the frame of every replay.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Undo(Snapshot snapshot, Transaction tx, Exception ex)
    {
        worldState.Restore(snapshot);
        Blockchain.Metrics.PrewarmHandoffFailures++;
        if (_logger.IsDebug) _logger.Debug($"Executing transaction {tx.Hash}, its pre-warm footprint failed to apply: {ex}");
    }

    private TransactionResult ExecuteReportingWrites(Transaction transaction, ITxTracer txTracer, CommittedStorageWrites committed)
    {
        committed.Begin();
        try
        {
            return baseAdapter.Execute(transaction, txTracer);
        }
        finally
        {
            // Reported also when there are none: the writes its footprint predicted were not made.
            preWarmer.ReportExecutedWrites(committed.End());
        }
    }

    public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
    {
        _blockExecutionContext = blockExecutionContext;
        // A block that failed part way left its changes to the world state it discarded.
        _uncommitted = 0;
        baseAdapter.SetBlockExecutionContext(in blockExecutionContext);
    }

    [SkipLocalsInit]
    private bool TryReplay(TransactionFootprint? footprint, Transaction tx, ITxTracer txTracer, out TransactionResult result)
    {
        result = default;
        if (footprint is null)
        {
            Tally = Tally with { Missing = Tally.Missing + 1 };
            Blockchain.Metrics.PrewarmHandoffsMissing++;
            return false;
        }

        // Any tracer beyond the receipt would miss the execution that does not happen.
        if (txTracer is not BlockReceiptsTracer { IsTracingOnlyReceipts: true }) return false;

        BlockHeader header = _blockExecutionContext.Header;
        IReleaseSpec spec = _blockExecutionContext.Spec;
        // The pre-execution checks a warm run skips: the gas left in the block, and a sender with code (EIP-3607).
        if (tx.GasLimit > header.GasLimit - header.GasUsed || worldState.IsInvalidContractSender(spec, tx.SenderAddress!)) return false;

        // Any other takeover is matched, replayed and committed on committed state, as without sharing.
        bool mayShare = _sharedCommitState is not null && footprint.MayShareCommit;
        if (!mayShare && _uncommitted != 0) CommitTakeovers();

        if (!footprint.Matches(worldState))
        {
            Tally = Tally with { Rejected = Tally.Rejected + 1 };
            Blockchain.Metrics.PrewarmHandoffsRejected++;
            return false;
        }

        Snapshot snapshot = worldState.TakeSnapshot();
        try
        {
            footprint.Replay(worldState, spec);
        }
        catch (Exception ex)
        {
            Undo(snapshot, tx, ex);
            return false;
        }

        if (!mayShare || !TryShareCommit(in snapshot))
        {
            worldState.Commit(spec, NullStateTracer.Instance, commitRoots: !spec.IsEip658Enabled);
        }

        ref readonly FootprintReceipt receipt = ref footprint.Receipt;
        GasConsumed gas = receipt.Gas;
        tx.SpentGas = gas.SpentGas;
        tx.BlockGasUsed = gas.EffectiveBlockGas;
        footprint.Counts.Flush();

        if (receipt.Success)
        {
            txTracer.MarkAsSuccess(receipt.Recipient, in gas, [], receipt.Logs);
        }
        else
        {
            txTracer.MarkAsFailed(receipt.Recipient, in gas, [], receipt.Error);
        }

        Tally = Tally with { Replayed = Tally.Replayed + 1 };
        Blockchain.Metrics.PrewarmHandoffs++;
        if (footprint.Refreshed) Blockchain.Metrics.PrewarmRefreshesTakenOver++;
        if (footprint.FromMempool) Blockchain.Metrics.PrewarmMempoolRunsTakenOver++;
        result = footprint.Result;
        return true;
    }

    /// <summary>Leaves the commit of the replay just made to a later one, unless the shared commit must be made now.</summary>
    /// <remarks>
    /// Made now once it covers the most transactions it may, at the block's last transaction, or when the replay changed
    /// what a commit does not write as it reads (an empty account EIP-161 removes, a created one that is empty).
    /// </remarks>
    /// <returns>Whether the commit was left; if not, the caller makes it, for every replay it covers.</returns>
    private bool TryShareCommit(in Snapshot snapshot)
    {
        if (_uncommitted + 1 < _maxSharedCommit && !preWarmer.StartedLastTransaction && _sharedCommitState!.ChangesCommitAsReadSince(in snapshot))
        {
            _uncommitted++;
            CommitsShared++;
            Blockchain.Metrics.PrewarmReplayCommitsShared++;
            return true;
        }

        _uncommitted = 0;
        return false;
    }

    // Apart: only a block whose takeovers shared a commit gets here.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void CommitTakeovers()
    {
        _uncommitted = 0;
        IReleaseSpec spec = _blockExecutionContext.Spec;
        worldState.Commit(spec, NullStateTracer.Instance, commitRoots: !spec.IsEip658Enabled);
    }
}
