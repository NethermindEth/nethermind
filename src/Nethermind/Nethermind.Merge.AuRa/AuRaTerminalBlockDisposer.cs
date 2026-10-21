// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Merge.AuRa;

/// <summary>
/// Disposes the AuRa finalization manager at the merge transition so it stops competing with the
/// Engine API's post-merge finalization writes into <see cref="IBlockTree.ForkChoiceUpdated"/>.
/// </summary>
/// <remarks>
/// Pre-merge AuRa nodes compute their own finality via <see cref="IAuRaBlockFinalizationManager"/>,
/// which subscribes to the branch processor and calls <see cref="IBlockTree.ForkChoiceUpdated"/>.
/// Post-merge that signal must come from the beacon chain instead; disposing AuRa unsubscribes it
/// from the branch processor so both paths don't race-write into BlockTree's finalized state.
/// </remarks>
public sealed class AuRaTerminalBlockDisposer : IDisposable
{
    private readonly IAuRaBlockFinalizationManager _auRaBlockFinalizationManager;
    private readonly IPoSSwitcher _poSSwitcher;
    private readonly IMainProcessingContext _mainProcessingContext;
    private int _disposed;

    public AuRaTerminalBlockDisposer(
        IAuRaBlockFinalizationManager auRaBlockFinalizationManager,
        IPoSSwitcher poSSwitcher,
        IBlockTree blockTree,
        IMainProcessingContext mainProcessingContext)
    {
        _auRaBlockFinalizationManager = auRaBlockFinalizationManager;
        _poSSwitcher = poSSwitcher;
        _mainProcessingContext = mainProcessingContext;

        // A terminal total difficulty of zero makes the genesis block terminal. Hive Engine
        // networks use this configuration and must not expose AuRa's genesis finality as the
        // Engine API's `finalized` tag before the beacon chain provides a forkchoice update.
        if (poSSwitcher.TerminalTotalDifficulty == UInt256.Zero || poSSwitcher.IsHeadPostMerge(blockTree))
        {
            Dispose();
        }
        else
        {
            _poSSwitcher.TerminalBlockReached += OnTerminalBlock;
            _mainProcessingContext.BranchProcessor.BlockProcessing += OnBlockProcessing;
        }
    }

    private void OnTerminalBlock(object? sender, EventArgs e) => Dispose();

    private void OnBlockProcessing(object? sender, BlockEventArgs e)
    {
        if (_poSSwitcher.IsPostMerge(e.Block.Header))
        {
            Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _poSSwitcher.TerminalBlockReached -= OnTerminalBlock;
        _mainProcessingContext.BranchProcessor.BlockProcessing -= OnBlockProcessing;
        _auRaBlockFinalizationManager.Dispose();
    }
}
