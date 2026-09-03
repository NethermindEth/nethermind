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

/// <summary>Disposes AuRa finalization when post-merge beacon-chain finalization takes over.</summary>
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

        // TTD zero makes genesis terminal; avoid exposing AuRa finality before beacon forkchoice updates.
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
