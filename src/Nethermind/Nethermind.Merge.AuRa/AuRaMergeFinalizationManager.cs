// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
    }

    public long GetLastLevelFinalizedBy(Hash256 blockHash)
    {
        return _auRaBlockFinalizationManager.GetLastLevelFinalizedBy(blockHash);
    }

    public long? GetFinalizationLevel(long level)
    {
        return _auRaBlockFinalizationManager.GetFinalizationLevel(level);
    }

    public void SetMainBlockBranchProcessor(IBranchProcessor branchProcessor)
    {
        if (_poSSwitcher.IsHeadPostMerge(_blockTree)) return;
        _auRaBlockFinalizationManager.SetMainBlockBranchProcessor(branchProcessor);
    }

    public override long LastFinalizedBlockLevel
    {
        get
        {
            return IsPostMerge
                ? _manualBlockFinalizationManager.LastFinalizedBlockLevel
                : _auRaBlockFinalizationManager.LastFinalizedBlockLevel;
        }
    }

    public override void Dispose()
    {
        if (IsPostMerge)
        {
            _auRaBlockFinalizationManager.Dispose();
        }
        base.Dispose();
    }

}
