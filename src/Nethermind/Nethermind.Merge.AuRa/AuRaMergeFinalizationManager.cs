// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;

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
        // Skip forwarding only when the current head is already post-merge. We can't rely on
        // IPoSSwitcher.HasEverReachedTerminalBlock() because it is true on a fresh archive DB as
        // soon as Merge.FinalTotalDifficulty is set in config, even with head at genesis — skipping
        // then would leave pre-merge AuRa finalization completely inert and break validator-set
        // transitions (e.g. Gnosis block 1300).
        BlockHeader? head = _blockTree.Head?.Header;
        if (head is not null && _poSSwitcher.IsPostMerge(head)) return;
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
