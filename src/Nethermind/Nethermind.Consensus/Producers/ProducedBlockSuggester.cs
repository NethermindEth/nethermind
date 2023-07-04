// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Core;

namespace Nethermind.Consensus.Producers
{
    public class ProducedBlockSuggester : IDisposable
    {
        private readonly IBlockTree _blockTree;
        private readonly IBlockProducer _blockProducer;

        public ProducedBlockSuggester(IBlockTree blockTree, IBlockProducer blockProducer)
        {
            _blockTree = blockTree;
            _blockProducer = blockProducer;
            _blockProducer.BlockProduced += OnBlockProduced;
        }

        private void OnBlockProduced(object? sender, BlockEventArgs e)
        {
            // PostMerge blocks are suggested in Engine API
            if (!e.Block.IsPostMerge)
            {
                _blockTree.SuggestBlock(e.Block);
            }
        }

        public void Dispose() => _blockProducer.BlockProduced -= OnBlockProduced;
    }
}
