// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.TxPool;

namespace Nethermind.Merge.Plugin;

/// <summary>
/// Earns MATCHA sender width from block finalization: every block the block tree finalizes is handed to the
/// transaction pool's <see cref="IFrameTxWidthLedger"/>, so a sender earns width only from gas that can no
/// longer reorg out.
/// </summary>
/// <remarks>
/// Lives in the consensus layer, which legitimately observes both the block tree and the pool, so
/// <c>Nethermind.TxPool</c> keeps no reference to <c>Nethermind.Blockchain</c>. Blocks between two finalized
/// heights finalize together, so the gap is walked; the first finalization after start credits only its own
/// block, not the history behind it. Inert unless the pool holds a width ledger and
/// <see cref="ITxPoolConfig.FrameTxWidthEnabled"/> is set.
/// </remarks>
public class FrameTxWidthFinalizer : IDisposable
{
    private readonly IBlockTree _blockTree;
    private readonly IReceiptFinder _receiptFinder;
    private readonly IFrameTxWidthLedger? _ledger;
    private readonly ILogger _logger;
    private ulong _lastFinalizedBlock;
    private bool _seenFinalization;

    public FrameTxWidthFinalizer(IBlockTree blockTree, IReceiptFinder receiptFinder, ITxPool txPool, ITxPoolConfig txPoolConfig, ILogManager logManager)
    {
        _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
        _receiptFinder = receiptFinder ?? throw new ArgumentNullException(nameof(receiptFinder));
        _logger = logManager?.GetClassLogger<FrameTxWidthFinalizer>() ?? throw new ArgumentNullException(nameof(logManager));

        if (txPoolConfig.FrameTxWidthEnabled && txPool is IFrameTxWidthLedger ledger)
        {
            _ledger = ledger;
            _blockTree.BlocksFinalized += OnBlocksFinalized;
        }
    }

    private void OnBlocksFinalized(object? sender, FinalizeEventArgs e)
    {
        try
        {
            ulong finalized = e.FinalizedBlock.Number;
            ulong from = _seenFinalization ? _lastFinalizedBlock + 1 : finalized;
            for (ulong number = from; number <= finalized; number++)
            {
                Block? block = _blockTree.FindBlock(number, BlockTreeLookupOptions.RequireCanonical);
                if (block is null || block.Transactions.Length == 0) continue;

                _ledger!.EarnWidthOnFinalization(block, _receiptFinder.Get(block));
            }

            _lastFinalizedBlock = finalized;
            _seenFinalization = true;
        }
        catch (Exception exception)
        {
            if (_logger.IsError) _logger.Error($"Couldn't earn MATCHA width up to finalized block {e.FinalizedBlock.Number}, last handled {_lastFinalizedBlock}", exception);
        }
    }

    public void Dispose()
    {
        if (_ledger is not null) _blockTree.BlocksFinalized -= OnBlocksFinalized;
    }
}
