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
using EvmMetrics = Nethermind.Evm.Metrics;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Reports the main thread's per-transaction progress to the prewarmer so it can skip warming already-started txs,
/// and takes over a transaction's warm run when its footprint matches the state.
/// The <see cref="IPrewarmerState.IsPrewarmer"/> guard ensures only the main execution reports, not the prewarmer's own scope.
/// </summary>
public class PrewarmerTxAdapter(ITransactionProcessorAdapter baseAdapter, BlockCachePreWarmer preWarmer, IPrewarmerState prewarmerState, IWorldState worldState) : ITransactionProcessorAdapter
{
    // Footprints are taken by execution that commits each transaction, not by block building or tracing.
    private readonly bool _replays = baseAdapter is ExecuteTransactionProcessorAdapter;
    private BlockExecutionContext _blockExecutionContext;

    internal (int Replayed, int Rejected, int Missing) Tally { get; private set; }

    public TransactionResult Execute(Transaction transaction, ITxTracer txTracer)
    {
        if (!prewarmerState.IsPrewarmer)
        {
            preWarmer.OnBeforeTxExecution();
            if (_replays
                && preWarmer.TryFindFootprint(transaction, out TransactionFootprint? footprint)
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
            return false;
        }

        // Any tracer beyond the receipt would miss the execution that does not happen.
        if (txTracer is not BlockReceiptsTracer { IsTracingOnlyReceipts: true }) return false;

        BlockHeader header = _blockExecutionContext.Header;
        IReleaseSpec spec = _blockExecutionContext.Spec;
        // The pre-execution checks a warm run skips: the gas left in the block, and a sender with code (EIP-3607).
        if (tx.GasLimit > header.GasLimit - header.GasUsed || worldState.IsInvalidContractSender(spec, tx.SenderAddress!)) return false;

        if (!footprint.Matches(worldState))
        {
            Tally = Tally with { Rejected = Tally.Rejected + 1 };
            return false;
        }

        Snapshot snapshot = worldState.TakeSnapshot();
        try
        {
            footprint.Replay(worldState, spec);
        }
        catch (Exception)
        {
            worldState.Restore(snapshot);
            return false;
        }

        worldState.Commit(spec, NullStateTracer.Instance, commitRoots: !spec.IsEip658Enabled);

        ref readonly FootprintReceipt receipt = ref footprint.Receipt;
        GasConsumed gas = receipt.Gas;
        tx.SpentGas = gas.SpentGas;
        tx.BlockGasUsed = gas.EffectiveBlockGas;
        EvmMetrics.UpdateBlockGasPrice(tx.CalculateEffectiveGasPrice(spec.IsEip1559Enabled, header.BaseFeePerGas));
        if (tx.IsContractCreation) EvmMetrics.IncrementCreates();

        if (receipt.Success)
        {
            txTracer.MarkAsSuccess(receipt.Recipient, in gas, [], receipt.Logs);
        }
        else
        {
            txTracer.MarkAsFailed(receipt.Recipient, in gas, [], receipt.Error);
        }

        Tally = Tally with { Replayed = Tally.Replayed + 1 };
        result = footprint.Result;
        return true;
    }
}
