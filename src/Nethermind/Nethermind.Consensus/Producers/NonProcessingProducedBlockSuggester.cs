// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;

namespace Nethermind.Consensus.Producers;
public class NonProcessingProducedBlockSuggester : IProducedBlockSuggester
{
    private readonly IBlockTree _blockTree;
    private readonly IBlockProducerRunner _blockProducerRunner;

    public NonProcessingProducedBlockSuggester(IBlockTree blockTree, IBlockProducerRunner blockProducer)
    {
        _blockTree = blockTree;
        _blockProducerRunner = blockProducer;
        _blockProducerRunner.BlockProduced += OnBlockProduced;
    }

    private void OnBlockProduced(object? sender, BlockEventArgs e)
    {
        if (_blockTree.SuggestBlock(e.Block, BlockTreeSuggestOptions.None) == AddBlockResult.Added)
            _blockTree.UpdateMainChain([e.Block], true);
    }

    public void Dispose() => _blockProducerRunner.BlockProduced -= OnBlockProduced;
}
