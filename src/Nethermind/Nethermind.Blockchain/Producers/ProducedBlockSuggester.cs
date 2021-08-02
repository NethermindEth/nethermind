//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using System.Linq;
using Nethermind.Consensus;
using Nethermind.Core;

namespace Nethermind.Blockchain.Producers
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

        private void OnBlockProduced(object? sender, BlockEventArgs e) =>
            _blockTree.SuggestBlock(e.Block);

        public void Dispose() =>
            _blockProducer.BlockProduced -= OnBlockProduced;
    }
}
