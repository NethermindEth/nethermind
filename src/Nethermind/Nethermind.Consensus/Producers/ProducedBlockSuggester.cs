// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;

namespace Nethermind.Consensus.Producers
{
    public class ProducedBlockSuggester : IProducedBlockSuggester
    {
        private readonly IBlockTree _blockTree;
        private readonly IBlockProducerRunner _blockProducerRunner;

        public ProducedBlockSuggester(IBlockTree blockTree, IBlockProducerRunner blockProducer)
        {
            _blockTree = blockTree;
            _blockProducerRunner = blockProducer;
            _blockProducerRunner.BlockProduced += OnBlockProduced;
        }

        private void OnBlockProduced(object? sender, BlockEventArgs e)
        {
            // PostMerge blocks are suggested in Engine API
            if (!e.Block.IsPostMerge)
            {
                _blockTree.SuggestBlock(e.Block);
            }
        }

        public void Dispose() => _blockProducerRunner.BlockProduced -= OnBlockProduced;
    }
}
