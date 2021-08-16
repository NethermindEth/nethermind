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
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Synchronization.Witness
{
    public class WitnessPruner
    {
        private readonly IBlockTree _blockTree;
        private readonly IWitnessRepository _witnessRepository;
        private readonly int _followDistance;
        private readonly ILogger _logger;

        public WitnessPruner(IBlockTree blockTree, IWitnessRepository witnessRepository, ILogManager logManager, int followDistance = 16)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _witnessRepository = witnessRepository ?? throw new ArgumentNullException(nameof(witnessRepository));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _followDistance = followDistance;
        }

        public void Start()
        {
            _blockTree.NewHeadBlock += OnNewHeadBlock;
        }

        private void OnNewHeadBlock(object? sender, BlockEventArgs e)
        {
            long toPrune = e.Block.Number - _followDistance;
            if (toPrune > 0)
            {
                var level = _blockTree.FindLevel(toPrune);
                if (level != null)
                {
                    if (_logger.IsTrace) _logger.Trace($"Pruning witness from blocks with number {toPrune}");
                    
                    for (int i = 0; i < level.BlockInfos.Length; i++)
                    {
                        var blockInfo = level.BlockInfos[i];
                        if (blockInfo.BlockHash != null)
                        {
                            _witnessRepository.Delete(blockInfo.BlockHash);
                        }
                    }
                }
            }
        }
    }
}
