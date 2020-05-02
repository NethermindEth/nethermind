//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Blockchain.Visitors
{
    public class StartupBlockTreeFixer : IBlockTreeVisitor
    {
        private readonly IBlockTree _blockTree;
        private readonly ILogger _logger;
        private long _startNumber;
        private long _blocksToLoad;

        public StartupBlockTreeFixer(IBlockTree blockTree, ILogger logger)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _startNumber = (_blockTree.Head?.Number ?? 0) + 1;
            _blocksToLoad = CountKnownAheadOfHead();
        }

        public long StartLevelInclusive => _startNumber;

        public long EndLevelExclusive => _startNumber + _blocksToLoad;

        Task<LevelVisitOutcome> IBlockTreeVisitor.VisitLevel(ChainLevelInfo chainLevelInfo, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException();
        }

        Task<bool> IBlockTreeVisitor.VisitMissing(Keccak hash, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException();
        }

        Task<bool> IBlockTreeVisitor.VisitHeader(BlockHeader header, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException();
        }

        Task<BlockVisitOutcome> IBlockTreeVisitor.VisitBlock(Block block, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException();
        }


        /// <summary>
        /// This would be super slow in fast sync, right?
        /// </summary>
        /// <returns></returns>
        private long CountKnownAheadOfHead()
        {
            long headNumber = _blockTree.Head?.Number ?? 0;
            return _blockTree.BestKnownNumber - headNumber;
        }
    }
}