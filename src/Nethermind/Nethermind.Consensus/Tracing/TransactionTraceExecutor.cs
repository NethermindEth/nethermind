// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.Consensus.Tracing;

/// <summary>Executes read-only transaction prefixes; delegates all other processing unchanged.</summary>
public sealed class TransactionTraceExecutor(
    IBlockProcessor.IBlockTransactionsExecutor inner,
    ITransactionProcessorAdapter transactionProcessor,
    IWorldState state,
    IBlockAccessListManager balManager,
    BlockProcessor.BlockValidationTransactionsExecutor.ITransactionProcessedEventHandler? transactionProcessed = null,
    StateReadOverlaySlot? readOverlay = null)
    : IBlockProcessor.IBlockTransactionsExecutor
{
    private const int OverlayUnprobed = 0;
    private const int OverlayAccepted = 1;
    private const int OverlayRefused = 2;
    private int _overlaySupport;

    /// <summary>Probes basic overlay support without requiring full BAL construction.</summary>
    /// <remarks>Applies an empty overlay once per environment, which may invalidate account caches; the state the
    /// environment was built with keeps its answer for every later rent. This does not guarantee that a populated
    /// overlay will be accepted; conditional refusal falls back to prefix replay.</remarks>
    public bool CanSeed => readOverlay is not null && !balManager.ForceConstructGeneratedBlockAccessList && StateAcceptsOverlay();

    private bool StateAcceptsOverlay()
    {
        int support = Volatile.Read(ref _overlaySupport);
        if (support == OverlayUnprobed)
        {
            support = state.TryApplyAccountOverlay(EmptyOverlay.Instance) ? OverlayAccepted : OverlayRefused;
            Volatile.Write(ref _overlaySupport, support);
        }

        return support == OverlayAccepted;
    }

    /// <summary>Whether processed transactions are reported with their receipts.</summary>
    internal bool ReportsProcessedTransactions => transactionProcessed is not null;

    /// <inheritdoc />
    public void SetBlockExecutionContext(in BlockExecutionContext context) => inner.SetBlockExecutionContext(in context);

    /// <inheritdoc />
    public void PublishTransactionProcessedEvents() => inner.PublishTransactionProcessedEvents();

    /// <inheritdoc />
    public void ClearTransactionProcessedEvents() => inner.ClearTransactionProcessedEvents();

    /// <summary>Arms the seed of the traced transaction ahead of the block's opening system calls, with its overlay
    /// withheld until the transactions start: the calls read the parent state, as in the replay, and read it through
    /// the block's read cache, which every transaction environment of the block would otherwise read again.</summary>
    internal void PrepareSeed(Block block, IBlockTracer blockTracer, ProcessingOptions options)
    {
        if (readOverlay is null) return;

        readOverlay.Disarm();
        if (Seedable(blockTracer, options) is { Seeds: { } seeds } boundary
            && boundary.IndexOf(block) is >= 0 and int target
            && seeds.TrySeed(block, target, readOverlay))
        {
            readOverlay.Withhold();
        }
    }

    private TransactionTraceBoundary? Seedable(IBlockTracer tracer, ProcessingOptions options) =>
        TransactionTraceBoundary.Get(tracer, options) is { } boundary
        && !balManager.ForceConstructGeneratedBlockAccessList
        && (!boundary.IsTracingRewards || boundary.SkipsTransactions)
            ? boundary
            : null;

    /// <inheritdoc />
    public TxReceipt[] ProcessTransactions(Block block, ProcessingOptions options, BlockReceiptsTracer tracer, CancellationToken token)
    {
        if (Seedable(tracer.OtherTracer, options) is not { } boundary)
            return inner.ProcessTransactions(block, options, tracer, token);

        inner.SetupTxTimingMetrics(block);
        if (balManager.Enabled) balManager.NextTransaction();

        // A seeded prefix stands in for the transactions ahead of the target: they are neither executed nor traced.
        // The access list generated alongside misses them, which is harmless only because a trace discards it and a
        // caller that reads it forces full construction above. Which seed a block carrying a list may take is the
        // seed source's rule, not this executor's.
        try
        {
            int first = 0;
            int target = -1;
            if (boundary.Seeds is not null && readOverlay is not null)
            {
                target = boundary.IndexOf(block);
                if (readOverlay.Restore() is { } overlay)
                {
                    if (state.TryApplyAccountOverlay(overlay)) first = target;
                    else readOverlay.Disarm();
                }
            }
            if (boundary.IsExecutionRequired && target < 0)
                throw new InvalidOperationException("The indexed trace could not locate its transaction boundary.");
            TxReceipt[] receipts = Execute(block, options, tracer, token, boundary, first);
            boundary.HasExecuted = true;
            return receipts;
        }
        finally
        {
            // What follows the transactions, the rewards and withdrawals, must see the seeded end state too when the
            // seed stood for the whole block; the environment disarms the slot when its scope closes.
            if (!boundary.SkipsTransactions) readOverlay?.Disarm();
        }
    }

    private sealed class EmptyOverlay : IStateReadOverlay
    {
        public static readonly EmptyOverlay Instance = new();
        public bool TryGetAccount(Address address, Account? underlying, out Account? overlaid)
        {
            overlaid = null;
            return false;
        }
        public bool TryGetStorage(Address address, in UInt256 index, out UInt256 value)
        {
            value = default;
            return false;
        }
        public bool HasStorage(Address address) => false;
    }

    private TxReceipt[] Execute(Block block, ProcessingOptions options, BlockReceiptsTracer tracer, CancellationToken token, TransactionTraceBoundary boundary, int first)
    {
        for (int i = first; i < block.Transactions.Length && !boundary.IsComplete; i++)
        {
            token.ThrowIfCancellationRequested();
            Transaction tx = block.Transactions[i];
            ITransactionProcessorAdapter processor = balManager.Enabled ? balManager.GetTxProcessor((uint)i + 1) : transactionProcessor;
            long start = inner.StartTxTimer();
            TransactionResult result;
            try
            {
                result = processor.ProcessTransaction(tx, tracer, options, state);
            }
            finally
            {
                inner.StopTxTimer(i, start);
            }

            if (!result) BlockProcessor.BlockValidationTransactionsExecutor.ThrowInvalidTransactionException(result, block.Header, tx, i);
            // With a seeded prefix the receipts tracer numbers receipts from the first executed transaction, so the
            // receipt of transaction i carries index i - first and a cumulative gas that leaves out the prefix; a
            // handler keyed on either would be told about the wrong transaction, so it is told nothing.
            if (first == 0 && transactionProcessed is { HasSubscribers: true } handler)
                handler.OnTransactionProcessed(new TxProcessedEventArgs(i, tx, block.Header, tracer.TxReceipts[i]));
            if (balManager.Enabled)
            {
                balManager.NextTransaction();
                balManager.SpendGas(tx.BlockGasUsed);
            }
        }

        // The receipts of what executed: with a seeded prefix that is the tail from the target on, not the block's.
        return [.. tracer.TxReceipts];
    }
}
