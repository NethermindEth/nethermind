// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
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
/// block, not the history behind it. The watermark advances per credited block, so a failure mid-gap resumes
/// after the last block credited instead of crediting it twice. The walk runs on the forkchoice thread, so a gap longer
/// than <see cref="MaxBlocksPerFinalization"/> credits only its latest blocks. Such a gap follows an offline consensus
/// client, and also any stretch of three epochs or more without finality while the consensus client stays online;
/// in both cases the older blocks are skipped and logged. Skipping only withholds width and never grants any. Senders come from the frame transaction's own
/// <c>sender</c> field, so receipts are read without signature recovery. Inert unless
/// <see cref="ITxPoolConfig.FrameTxWidthEnabled"/> is set. The block tree raises finalizations outside its own lock, so
/// the handler serializes them: overlapping finalizations credit each block once and never move the watermark back.
/// </remarks>
public class FrameTxWidthFinalizer : IDisposable
{
    internal const ulong MaxBlocksPerFinalization = 96;

    private readonly IBlockTree _blockTree;
    private readonly IReceiptFinder _receiptFinder;
    private readonly IFrameTxWidthLedger? _ledger;
    private readonly ILogger _logger;
    private readonly Lock _finalizationLock = new();
    private ulong _lastFinalizedBlock;
    private bool _seenFinalization;

    public FrameTxWidthFinalizer(IBlockTree blockTree, IReceiptFinder receiptFinder, IFrameTxWidthLedger ledger, ITxPoolConfig txPoolConfig, ILogManager logManager)
    {
        _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
        _receiptFinder = receiptFinder ?? throw new ArgumentNullException(nameof(receiptFinder));
        _logger = logManager?.GetClassLogger<FrameTxWidthFinalizer>() ?? throw new ArgumentNullException(nameof(logManager));

        if (txPoolConfig.FrameTxWidthEnabled)
        {
            _ledger = ledger;
            _blockTree.BlocksFinalized += OnBlocksFinalized;
        }
    }

    private void OnBlocksFinalized(object? sender, FinalizeEventArgs e)
    {
        using Lock.Scope _ = _finalizationLock.EnterScope();
        try
        {
            ulong finalized = e.FinalizedBlock.Number;
            ulong from = _seenFinalization ? _lastFinalizedBlock + 1 : finalized;
            if (from <= finalized && finalized - from >= MaxBlocksPerFinalization)
            {
                ulong skippedTo = finalized - MaxBlocksPerFinalization;
                if (_logger.IsInfo) _logger.Info($"Skipped MATCHA width for finalized blocks {from}..{skippedTo}: the gap exceeds {MaxBlocksPerFinalization} blocks");
                from = skippedTo + 1;
            }

            for (ulong number = from; number <= finalized; number++)
            {
                Block? block = _blockTree.FindBlock(number, BlockTreeLookupOptions.RequireCanonical);
                if (block is not null && block.Transactions.Length != 0)
                {
                    _ledger!.EarnWidthOnFinalization(block, _receiptFinder.Get(block, recoverSender: false));
                }

                _lastFinalizedBlock = number;
                _seenFinalization = true;
            }
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
