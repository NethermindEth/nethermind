// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
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
    private readonly List<SlotShift> _shifts = [];

    internal (int Replayed, int Rejected, int Missing) Tally { get; private set; }

    public TransactionResult Execute(Transaction transaction, ITxTracer txTracer)
    {
        if (!prewarmerState.IsPrewarmer)
        {
            preWarmer.OnBeforeTxExecution();
            if (preWarmer.TryFindFootprint(transaction, _blockExecutionContext.Header, out TransactionFootprint? footprint)
                && TryReplay(footprint, transaction, txTracer, out TransactionResult result))
            {
                return result;
            }
        }

        return baseAdapter.Execute(transaction, txTracer);
    }

    public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
    {
        _blockExecutionContext = blockExecutionContext;
        baseAdapter.SetBlockExecutionContext(in blockExecutionContext);
    }

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

        bool shifted = false;
        if (!footprint.Matches(worldState))
        {
            if (!ShiftedReplay.Allowed || !footprint.MatchesShifted(worldState, _shifts))
            {
                Tally = Tally with { Rejected = Tally.Rejected + 1 };
                Blockchain.Metrics.PrewarmHandoffsRejected++;
                return false;
            }

            shifted = true;
        }

        Snapshot snapshot = worldState.TakeSnapshot();
        try
        {
            if (shifted)
            {
                footprint.ReplayShifted(worldState, spec, _shifts);
                ShiftedReplay.Used++;
                Blockchain.Metrics.PrewarmHandoffsShifted++;
            }
            else
            {
                footprint.Replay(worldState, spec);
            }
        }
        catch (Exception ex)
        {
            worldState.Restore(snapshot);
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
