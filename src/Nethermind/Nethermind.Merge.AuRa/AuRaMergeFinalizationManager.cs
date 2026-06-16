// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.Processing;
using Nethermind.Core.Crypto;
using Nethermind.Merge.AuRa;

namespace Nethermind.Merge.Plugin;

public class AuRaMergeFinalizationManager : MergeFinalizationManager, IAuRaBlockFinalizationManager
{
    private readonly IAuRaBlockFinalizationManager _auRaBlockFinalizationManager;
    private readonly IPoSSwitcher _poSSwitcher;
    private readonly IBlockTree _blockTree;

    public AuRaMergeFinalizationManager(IManualBlockFinalizationManager manualBlockFinalizationManager, IAuRaBlockFinalizationManager blockFinalizationManager, IPoSSwitcher poSSwitcher, IBlockTree blockTree)
        : base(manualBlockFinalizationManager, blockFinalizationManager, poSSwitcher)
    {
        _auRaBlockFinalizationManager = blockFinalizationManager;
        _poSSwitcher = poSSwitcher;
        _blockTree = blockTree;
        _auRaBlockFinalizationManager.BlocksFinalized += OnBlockFinalized;

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

    public long GetLastLevelFinalizedBy(Hash256 blockHash) => _auRaBlockFinalizationManager.GetLastLevelFinalizedBy(blockHash);

    public long? GetFinalizationLevel(long level) => _auRaBlockFinalizationManager.GetFinalizationLevel(level);

    public void SetMainBlockBranchProcessor(IBranchProcessor branchProcessor)
    {
        if (_poSSwitcher.IsHeadPostMerge(_blockTree)) return;
        _auRaBlockFinalizationManager.SetMainBlockBranchProcessor(branchProcessor);
    }

    public override long LastFinalizedBlockLevel => IsPostMerge
        ? _manualBlockFinalizationManager.LastFinalizedBlockLevel
        : _auRaBlockFinalizationManager.LastFinalizedBlockLevel;

    public override void Dispose()
    {
        _poSSwitcher.TerminalBlockReached -= OnTerminalBlock;
        _auRaBlockFinalizationManager.BlocksFinalized -= OnBlockFinalized;
        _auRaBlockFinalizationManager.Dispose();
        base.Dispose();
    }

}
