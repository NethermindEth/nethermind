// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.Consensus.Producers;

public class NonProcessingProducedBlockSuggester : IProducedBlockSuggester
{
    private readonly IBlockTree _blockTree;
    private readonly ILogger _logger;
    private readonly IBlockProducerRunner _blockProducerRunner;

    public NonProcessingProducedBlockSuggester(IBlockTree blockTree, IBlockProducerRunner blockProducer, ILogManager logManager)
    {
        _blockTree = blockTree;
        _logger = logManager.GetClassLogger<NonProcessingProducedBlockSuggester>();
        _blockProducerRunner = blockProducer;
        _blockProducerRunner.BlockProduced += OnBlockProduced;
    }

    private void OnBlockProduced(object? sender, BlockEventArgs e)
    {
        if (_blockTree.SuggestBlock(e.Block, BlockTreeSuggestOptions.ForceDontSetAsMain) == AddBlockResult.Added &&
            !_blockTree.TryUpdateMainChain(e.Block.Header, wereProcessed: true, preloadedBlocks: [e.Block]) && _logger.IsWarn)
            _logger.Warn($"Canonical chain update refused for produced block {e.Block.Hash}.");
    }

    public void Dispose() => _blockProducerRunner.BlockProduced -= OnBlockProduced;
}
