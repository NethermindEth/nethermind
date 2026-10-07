// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using EvmMetrics = Nethermind.Evm.Metrics;
using System.Collections.Generic;
using Nethermind.Int256;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Reports the main thread's per-transaction progress to the prewarmer so it can skip warming already-started txs,
/// and takes over a transaction's warm run when its footprint matches the state.
/// The <see cref="IPrewarmerState.IsPrewarmer"/> guard ensures only the main execution reports, not the prewarmer's own scope.
/// </summary>
/// <remarks>Registered for main block processing only, so footprints are never taken by block building or tracing.</remarks>
public class PrewarmerTxAdapter(
    ITransactionProcessorAdapter baseAdapter,
    BlockCachePreWarmer preWarmer,
    IPrewarmerState prewarmerState,
    IWorldState worldState,
    ILogManager logManager) : ITransactionProcessorAdapter
{
    private readonly ILogger _logger = logManager.GetClassLogger<PrewarmerTxAdapter>();
    private BlockExecutionContext _blockExecutionContext;

    internal (int Replayed, int Rejected, int Missing) Tally { get; private set; }

    public TransactionResult Execute(Transaction transaction, ITxTracer txTracer)
    {
        if (!prewarmerState.IsPrewarmer)
        {
            preWarmer.OnBeforeTxExecution();
            long start = HandoffDiagnostics.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            if (HandoffDiagnostics.Enabled) HandoffDiagnostics.TxStarted(start, preWarmer.MainThreadTxIndex);
            int outcome = HandoffDiagnostics.NotEligible;
            if (preWarmer.TryFindFootprint(transaction, _blockExecutionContext.Header, out TransactionFootprint? footprint)
                && TryReplay(footprint, transaction, txTracer, out TransactionResult result, out outcome))
            {
                if (HandoffDiagnostics.Enabled)
                {
                    HandoffDiagnostics.RecordReplayWrites(footprint!);
                    HandoffDiagnostics.Count(HandoffDiagnostics.Replayed, start, transaction);
                }

                return result;
            }

            // Experiment only: the slots the transaction writes, reported once it is committed.
            bool feedback = RewarmCounters.Feedback && RewarmCounters.Enabled;
            if (feedback)
            {
                HandoffDiagnostics.WrittenCells.Clear();
                HandoffDiagnostics.CollectingWrites = true;
            }

            try
            {
                if (HandoffDiagnostics.Enabled)
                {
                    HandoffDiagnostics.Observing = true;
                    try
                    {
                        return baseAdapter.Execute(transaction, txTracer);
                    }
                    finally
                    {
                        HandoffDiagnostics.Observing = false;
                        HandoffDiagnostics.Count(outcome, start, transaction);
                    }
                }

                return baseAdapter.Execute(transaction, txTracer);
            }
            finally
            {
                if (feedback)
                {
                    HandoffDiagnostics.CollectingWrites = false;
                    if (HandoffDiagnostics.WrittenCells.Count > 0) ReportWrites();
                }
            }
        }

        return baseAdapter.Execute(transaction, txTracer);
    }

    // The committed values: a write a revert undid reads back as the value before it.
    private void ReportWrites()
    {
        List<(StorageCell Cell, UInt256 Value)> writes = new(HandoffDiagnostics.WrittenCells.Count);
        foreach (StorageCell cell in HandoffDiagnostics.WrittenCells)
        {
            worldState.Get(in cell, out UInt256 value);
            writes.Add((cell, value));
        }

        HandoffDiagnostics.WrittenCells.Clear();
        preWarmer.ReportExecutedWrites(writes);
    }

    public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
    {
        _blockExecutionContext = blockExecutionContext;
        baseAdapter.SetBlockExecutionContext(in blockExecutionContext);
    }

    private bool TryReplay(TransactionFootprint? footprint, Transaction tx, ITxTracer txTracer, out TransactionResult result, out int outcome)
    {
        result = default;
        outcome = HandoffDiagnostics.NotEligible;
        if (footprint is null)
        {
            Tally = Tally with { Missing = Tally.Missing + 1 };
            Blockchain.Metrics.PrewarmHandoffsMissing++;
            outcome = HandoffDiagnostics.Missing;
            if (HandoffDiagnostics.Enabled) HandoffDiagnostics.CountAbsence(preWarmer.FootprintStatus(tx));
            return false;
        }

        // Any tracer beyond the receipt would miss the execution that does not happen.
        if (txTracer is not BlockReceiptsTracer { IsTracingOnlyReceipts: true }) return false;

        BlockHeader header = _blockExecutionContext.Header;
        IReleaseSpec spec = _blockExecutionContext.Spec;
        // The pre-execution checks a warm run skips: the gas left in the block, and a sender with code (EIP-3607).
        if (tx.GasLimit > header.GasLimit - header.GasUsed || worldState.IsInvalidContractSender(spec, tx.SenderAddress!)) return false;

        int mismatch = footprint.Mismatch(worldState);
        if (mismatch != 0)
        {
            Tally = Tally with { Rejected = Tally.Rejected + 1 };
            Blockchain.Metrics.PrewarmHandoffsRejected++;
            outcome = HandoffDiagnostics.Rejected;
            if (HandoffDiagnostics.Enabled)
            {
                HandoffDiagnostics.CountMismatch(mismatch, tx);
                if (mismatch == 7) HandoffDiagnostics.CountSlotRejection(preWarmer.ClassifySlotRejection(footprint, worldState), tx);
            }
            return false;
        }

        Snapshot snapshot = worldState.TakeSnapshot();
        try
        {
            footprint.Replay(worldState, spec);
        }
        catch (Exception ex)
        {
            worldState.Restore(snapshot);
            outcome = HandoffDiagnostics.Failed;
            Blockchain.Metrics.PrewarmHandoffFailures++;
            if (_logger.IsDebug) _logger.Debug($"Executing transaction {tx.Hash}, its pre-warm footprint failed to apply: {ex}");
            return false;
        }

        worldState.Commit(spec, NullStateTracer.Instance, commitRoots: !spec.IsEip658Enabled);

        ref readonly FootprintReceipt receipt = ref footprint.Receipt;
        GasConsumed gas = receipt.Gas;
        tx.SpentGas = gas.SpentGas;
        tx.BlockGasUsed = gas.EffectiveBlockGas;
        EvmMetrics.UpdateBlockGasPrice(tx.CalculateEffectiveGasPrice(spec.IsEip1559Enabled, header.BaseFeePerGas));
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
        result = footprint.Result;
        return true;
    }
}
