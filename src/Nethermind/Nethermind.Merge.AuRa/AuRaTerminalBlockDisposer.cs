// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa;

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
    private bool _disposed;

    public AuRaTerminalBlockDisposer(
        IAuRaBlockFinalizationManager auRaBlockFinalizationManager,
        IPoSSwitcher poSSwitcher,
        IBlockTree blockTree)
    {
        _auRaBlockFinalizationManager = auRaBlockFinalizationManager;
        _poSSwitcher = poSSwitcher;

        if (poSSwitcher.IsHeadPostMerge(blockTree))
        {
            _disposed = true;
            _auRaBlockFinalizationManager.Dispose();
        }
        else
        {
            _poSSwitcher.TerminalBlockReached += OnTerminalBlock;
        }
    }

    private void OnTerminalBlock(object? sender, EventArgs e)
    {
        _disposed = true;
        _poSSwitcher.TerminalBlockReached -= OnTerminalBlock;
        _auRaBlockFinalizationManager.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _poSSwitcher.TerminalBlockReached -= OnTerminalBlock;
        _auRaBlockFinalizationManager.Dispose();
    }
}
