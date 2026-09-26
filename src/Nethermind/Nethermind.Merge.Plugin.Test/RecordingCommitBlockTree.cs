// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;

namespace Nethermind.Merge.Plugin.Test;

/// <summary>
/// Records <see cref="IBlockTree.SuggestBlock"/> and <see cref="IBlockTree.TryUpdateMainChain"/> for commit-block
/// unit tests. Castle DynamicProxy (NSubstitute) cannot proxy <c>params ReadOnlySpan&lt;Block&gt;</c>.
/// </summary>
internal sealed class RecordingCommitBlockTree : BlockTreeTestDouble
{
    public AddBlockResult SuggestResult { get; set; } = AddBlockResult.Added;

    public Block? SuggestedBlock { get; private set; }
    public BlockTreeSuggestOptions SuggestOptions { get; private set; }
    public bool? TryUpdateWereProcessed { get; private set; }
    public bool? TryUpdateForceUpdateHeadBlock { get; private set; }
    public Block[]? TryUpdatePreloadedBlocks { get; private set; }

    public override AddBlockResult SuggestBlock(Block block, BlockTreeSuggestOptions options = BlockTreeSuggestOptions.ShouldProcess)
    {
        SuggestedBlock = block;
        SuggestOptions = options;
        return SuggestResult;
    }

    public override bool TryUpdateMainChain(BlockHeader newHead, bool wereProcessed, bool forceUpdateHeadBlock = false, params ReadOnlySpan<Block> preloadedBlocks)
    {
        TryUpdateWereProcessed = wereProcessed;
        TryUpdateForceUpdateHeadBlock = forceUpdateHeadBlock;
        TryUpdatePreloadedBlocks = preloadedBlocks.ToArray();
        return true;
    }
}
