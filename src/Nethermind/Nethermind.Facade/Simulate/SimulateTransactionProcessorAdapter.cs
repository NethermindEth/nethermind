// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Facade.Proxy.Models.Simulate;

namespace Nethermind.Facade.Simulate;

/// <remarks>
/// Stateful and single-threaded: it advances a per-block <c>_currentTxIndex</c> and mutates the shared
/// <see cref="SimulateRequestState"/> gas counters without synchronization. It must therefore only ever
/// drive one transaction stream at a time, in order — i.e. the sequential block-access-list path. Simulate
/// guarantees this by never attaching a <c>BlockAccessList</c> to its synthesised blocks, which keeps
/// <c>BlockAccessListManager.ParallelExecutionEnabled</c> false. Do not register it on a parallel pool.
/// </remarks>
public class SimulateTransactionProcessorAdapter(ITransactionProcessor transactionProcessor, SimulateRequestState simulateRequestState) : ITransactionProcessorAdapter
{
    private int _currentTxIndex = 0;
    private BlockHeader? _header;
    private IReleaseSpec? _spec;
    public TransactionResult Execute(Transaction transaction, ITxTracer txTracer)
    {
        // The non-BAL / validation:false paths never run the block executor's pre-check, so resolve the gas
        // here; on the BAL path this repeats it with the same budget, leaving the accepted limit unchanged.
        PrepareForInclusionCheck(transaction, simulateRequestState.BlockStateGasLeft);
        transaction.Hash = transaction.CalculateHash();

        // The state budget only depletes while a BlockReceiptsTracer publishes the cumulative state gas;
        // without one the fix for #12692 silently reverts, so pin the premise rather than only tolerate it.
        BlockReceiptsTracer? receiptsTracer = txTracer.GetTracer<BlockReceiptsTracer>();
        Debug.Assert(receiptsTracer is not null, $"Simulate must be traced through a {nameof(BlockReceiptsTracer)}.");
        ulong cumulativeStateGasBefore = receiptsTracer?.BlockStateGasUsed ?? 0;
        TransactionResult result = simulateRequestState.Validate ? transactionProcessor.Execute(transaction, txTracer) : transactionProcessor.Trace(transaction, txTracer);

        ulong blockGasUsed = transaction.BlockGasUsed;
        ulong blockStateGasUsed = receiptsTracer?.BlockStateGasUsed.SaturatingSub(cumulativeStateGasBefore) ?? 0;
        // execution-apis: the request-wide cap depletes by the call's receipt gas; the block budgets track one EIP-8037 dimension each.
        simulateRequestState.TotalGasLeft = simulateRequestState.TotalGasLeft.SaturatingSub(transaction.SpentGas);
        simulateRequestState.BlockGasLeft = simulateRequestState.BlockGasLeft.SaturatingSub(blockGasUsed);
        // The BAL inclusion check refreshes this from receipt totals when it runs; otherwise — non-BAL specs,
        // ForceSequentialBlockAccessList, block production, Validation:false — this running value is authoritative.
        simulateRequestState.BlockStateGasLeft = simulateRequestState.BlockStateGasLeft.SaturatingSub(blockStateGasUsed);

        _currentTxIndex++;
        return result;
    }

    public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
    {
        _currentTxIndex = 0;
        _header = blockExecutionContext.Header;
        _spec = blockExecutionContext.Spec;
        transactionProcessor.SetBlockExecutionContext(in blockExecutionContext);
    }

    /// <inheritdoc/>
    public void PrepareForInclusionCheck(Transaction transaction, ulong stateGasAvailable)
    {
        simulateRequestState.BlockStateGasLeft = stateGasAvailable;

        // The per-dimension budgets shrink as the block is processed.
        ulong blockGasAvailable = GetBlockGasAvailable(stateGasAvailable);
        TransactionWithSourceDetails call = simulateRequestState.Calls[_currentTxIndex];
        if (!call.HadGasLimitInRequest)
        {
            transaction.GasLimit = Math.Min(blockGasAvailable, simulateRequestState.TotalGasLeft);
        }
        else if (call.GasLimitToFitBlock is { } requestedGas
            && !FitsBlock(transaction, requestedGas, blockGasAvailable, stateGasAvailable, out ulong remaining, out ulong required))
        {
            TransactionResult result = TransactionResult.ErrorType.BlockGasLimitExceeded.WithDetail(
                $"block gas limit reached: remaining: {remaining}, required: {required}");
            throw new InvalidTransactionException(_header!, result.ErrorDescription, result);
        }

        if (simulateRequestState.TotalGasLeft < transaction.GasLimit)
        {
            transaction.GasLimit = simulateRequestState.TotalGasLeft;
        }

        // A frame transaction's gas limit is its frames' sum, not the requested gas, so the cap cannot be told from it.
        ulong? uncappedGas = call.HadGasLimitInRequest ? call.GasLimitToFitBlock : blockGasAvailable;
        call.GasCapped = !transaction.SupportsFrames && transaction.GasLimit < uncappedGas;
    }

    /// <summary>The largest gas limit the block can still include.</summary>
    /// <remarks>
    /// EIP-8037 inclusion requires tx.gas &lt;= remaining state budget whatever state gas the tx uses, and reserves at most
    /// TX_MAX_GAS_LIMIT of execution gas, so with validation the execution budget stops binding once it holds a full cap.
    /// Without validation the execution gas is not capped, so the whole gas limit must fit the execution budget too.
    /// </remarks>
    private ulong GetBlockGasAvailable(ulong stateGasAvailable)
    {
        ulong executionGasAvailable = simulateRequestState.BlockGasLeft;
        return _spec?.IsEip8037Enabled == true && simulateRequestState.Validate && executionGasAvailable >= Eip7825Constants.DefaultTxGasLimitCap
            ? stateGasAvailable
            : Math.Min(executionGasAvailable, stateGasAvailable);
    }

    /// <remarks>
    /// EIP-8141: a frame transaction reserves block gas per dimension from its frames, not from its summed gas limit,
    /// so <paramref name="remaining"/> and <paramref name="required"/> report the dimension that does not fit.
    /// </remarks>
    private bool FitsBlock(Transaction transaction, ulong requestedGas, ulong blockGasAvailable, ulong stateGasAvailable, out ulong remaining, out ulong required)
    {
        (remaining, required) = (blockGasAvailable, requestedGas);
        if (!transaction.SupportsFrames) return required <= remaining;
        // A malformed frame transaction is left to the processor, which reports its actual error.
        if (!Eip8037BlockGasInclusionCheck.TryGetBlockGasReservations(transaction, _spec!, out ulong executionReservation, out ulong stateReservation)) return true;

        (remaining, required) = executionReservation > simulateRequestState.BlockGasLeft
            ? (simulateRequestState.BlockGasLeft, executionReservation)
            : (stateGasAvailable, stateReservation);
        return required <= remaining;
    }
}
