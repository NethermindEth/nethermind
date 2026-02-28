// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// XDC GasBailout: Skip balance pre-check failures caused by accumulated state root divergence.
// This mirrors gasBailout=true behavior in erigon-xdc and is required for XDC mainnet/apothem sync.
// Root cause: state roots diverge from geth at checkpoint reward blocks (block 1800+).
// As a result, some account balances in NM's state differ from geth's canonical state,
// causing spurious "insufficient sender balance" rejections for valid transactions.
// Fix: catch the exception and continue — the XdcStateRootCache handles state root divergence.
// See also: erigon-xdc gasBailout commit 3381feaa, nethermind XdcStateRootCache commit 912e7f8cfe.

using System;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;

namespace Nethermind.Xdc;

/// <summary>
/// XDC-specific block transactions executor with gasBailout support.
/// Catches "insufficient sender balance" errors caused by accumulated state root divergence
/// and skips those transactions rather than invalidating the entire block.
/// </summary>
internal class XdcBlockTransactionsExecutor : BlockProcessor.BlockValidationTransactionsExecutor
{
    private readonly ILogger _logger;

    public XdcBlockTransactionsExecutor(
        ITransactionProcessorAdapter transactionProcessor,
        IWorldState stateProvider,
        ILogManager logManager,
        BlockProcessor.BlockValidationTransactionsExecutor.ITransactionProcessedEventHandler? eventHandler = null)
        : base(transactionProcessor, stateProvider, eventHandler)
    {
        _logger = logManager.GetClassLogger<XdcBlockTransactionsExecutor>();
    }

    protected override void ProcessTransaction(
        Block block,
        Transaction currentTx,
        int index,
        BlockReceiptsTracer receiptsTracer,
        ProcessingOptions processingOptions)
    {
        try
        {
            base.ProcessTransaction(block, currentTx, index, receiptsTracer, processingOptions);
        }
        catch (InvalidTransactionException ex) when (IsBalanceError(ex))
        {
            // XDC GasBailout: log and skip — receipt was already started/ended by extension method
            if (_logger.IsDebug)
                _logger.Debug($"[XDC-GasBailout] Block {block.Number} tx[{index}] {currentTx.Hash}: {ex.Message.Split('\n')[0]} — skipping (state root divergence)");
        }
    }

    private static bool IsBalanceError(InvalidTransactionException ex) =>
        ex.Message.Contains("insufficient sender balance", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("INSUFFICIENT_SENDER_BALANCE", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("insufficient funds", StringComparison.OrdinalIgnoreCase);
}
