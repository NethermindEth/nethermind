// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa;
using Nethermind.Merge.AuRa;

namespace Nethermind.Merge.Plugin;

public class AuRaMergeFinalizationManager : MergeFinalizationManager
{
    private readonly IAuRaBlockFinalizationManager _auRaBlockFinalizationManager;
    private readonly IPoSSwitcher _poSSwitcher;

    public AuRaMergeFinalizationManager(IManualBlockFinalizationManager manualBlockFinalizationManager, IAuRaBlockFinalizationManager blockFinalizationManager, IPoSSwitcher poSSwitcher, IBlockTree blockTree)
        : base(manualBlockFinalizationManager, poSSwitcher)
    {
        _auRaBlockFinalizationManager = blockFinalizationManager;
        _poSSwitcher = poSSwitcher;

        if (poSSwitcher.IsHeadPostMerge(blockTree))
        {
            _auRaBlockFinalizationManager.Dispose();
        }
        else
        {
            _poSSwitcher.TerminalBlockReached += OnTerminalBlock;
        }
    }

    private void OnTerminalBlock(object? sender, EventArgs e)
    {
        _poSSwitcher.TerminalBlockReached -= OnTerminalBlock;

        // Unsubscribe AuRa finalization from block processing events — post-merge
        // finalization is handled by the beacon chain via ManualBlockFinalizationManager.
        _auRaBlockFinalizationManager.Dispose();
    }

    public override long LastFinalizedBlockLevel => IsPostMerge
        ? _manualBlockFinalizationManager.LastFinalizedBlockLevel
        : _auRaBlockFinalizationManager.LastFinalizedBlockLevel;

    public override void Dispose()
    {
        _poSSwitcher.TerminalBlockReached -= OnTerminalBlock;
        _auRaBlockFinalizationManager.Dispose();
        base.Dispose();
    }
}
